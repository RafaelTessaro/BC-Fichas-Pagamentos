using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace BCFichas.Core;

/// <summary>
/// Liberação do tablet, contra cópia: o BC Fichas só abre numa máquina liberada com a senha técnica. A liberação
/// fica num arquivo escondido fora da pasta do programa e vale só para esta instalação do Windows (o código que o
/// Windows cria ao ser instalado). Copiar a pasta do programa para outro computador não leva a liberação, e copiar
/// o arquivo também não adianta: ele não confere com a outra máquina. Vale para as três versões (original, Rede e
/// Cartão): liberou uma, as outras também abrem naquele tablet.
/// Sem internet, nenhuma proteção é impossível de quebrar: esta segura quem copia o programa, não um programador
/// decidido (o código é público). Não pesa nada: é uma leitura de arquivo pequena ao abrir.
/// </summary>
public sealed class Liberacao
{
    private const string Prefixo = "BCF1:";

    /// <summary>Não é segredo (o código é público): só faz a liberação valer para uma máquina.</summary>
    private static readonly byte[] Chave =
        Convert.FromHexString("30cd4fe41208683808bea5e3723a4cd6dc1ba1705e881d39eb8200be65df9c12");

    private readonly IReadOnlyList<string> _arquivos;
    private readonly Func<string> _codigoDaMaquina;

    /// <param name="arquivos">Onde gravar e procurar a liberação, na ordem (se o primeiro não deixar gravar, o próximo).</param>
    /// <param name="codigoDaMaquina">Código desta máquina (os testes trocam por um qualquer).</param>
    public Liberacao(IReadOnlyList<string> arquivos, Func<string> codigoDaMaquina)
    {
        _arquivos = arquivos;
        _codigoDaMaquina = codigoDaMaquina;
    }

    /// <summary>
    /// A do programa: em C:\ProgramData (vale para qualquer usuário do Windows) ou, se ali não der para gravar, na
    /// pasta do usuário.
    /// </summary>
    public static Liberacao DaMaquina() => new(
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BCFichas", "liberacao.dat"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BCFichasLiberacao", "liberacao.dat"),
    ], CodigoDoWindows);

    /// <summary>Esta máquina está liberada.</summary>
    public bool Liberada
    {
        get
        {
            string esperado;
            try
            {
                esperado = Conteudo();
            }
            catch (Exception)
            {
                return false;
            }
            return _arquivos.Any(arquivo => Confere(arquivo, esperado));
        }
    }

    /// <summary>Grava a liberação desta máquina (depois de conferir a senha técnica).</summary>
    public void Liberar()
    {
        var conteudo = Conteudo();
        string? erro = null;
        foreach (var arquivo in _arquivos)
        {
            try
            {
                Gravar(arquivo, conteudo);
                if (Confere(arquivo, conteudo)) return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                erro ??= e.Message;
            }
        }
        throw new ErroDeNegocio("Não consegui gravar a liberação deste tablet." + (erro is null ? "" : " " + erro));
    }

    private string Conteudo()
    {
        var codigo = (_codigoDaMaquina() ?? "").Trim().ToLowerInvariant();
        var assinatura = HMACSHA256.HashData(Chave, Encoding.UTF8.GetBytes("BC Fichas|liberacao|" + codigo));
        return Prefixo + Convert.ToHexString(assinatura);
    }

    private static bool Confere(string arquivo, string esperado)
    {
        try
        {
            if (!File.Exists(arquivo)) return false;
            var lido = File.ReadAllText(arquivo).Trim();
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(lido), Encoding.ASCII.GetBytes(esperado));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Grava escondido (arquivo e pasta). Um arquivo escondido não deixa ser regravado: tira antes.</summary>
    private static void Gravar(string arquivo, string conteudo)
    {
        var pasta = Directory.CreateDirectory(Path.GetDirectoryName(arquivo)!);
        if (File.Exists(arquivo)) File.SetAttributes(arquivo, FileAttributes.Normal);
        File.WriteAllText(arquivo, conteudo);
        File.SetAttributes(arquivo, FileAttributes.Hidden | FileAttributes.System);
        pasta.Attributes |= FileAttributes.Hidden;
    }

    /// <summary>
    /// O código que o Windows cria ao ser instalado (MachineGuid): não muda com atualizações, troca de nome do
    /// computador nem de usuário; muda só se o Windows for reinstalado (aí é só liberar de novo).
    /// </summary>
    public static string CodigoDoWindows()
    {
        if (!OperatingSystem.IsWindows()) return Environment.MachineName;
        return MachineGuid() ?? Environment.MachineName;
    }

    [SupportedOSPlatform("windows")]
    private static string? MachineGuid()
    {
        try
        {
            // Visão de 64 bits: o programa é de 32 bits e, num Windows de 64, a outra visão não tem o código.
            using var raiz = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var chave = raiz.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return chave?.GetValue("MachineGuid") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

using System.Runtime.InteropServices;
using BCFichas.Core;
using Microsoft.Win32;

namespace BCFichas.App;

/// <summary>
/// Coisas do Windows que o programa controla sozinho: manter a tela ligada enquanto está aberto e abrir junto
/// com o Windows. Fora do Windows (testes) não faz nada.
/// </summary>
internal static class IntegracaoWindows
{
    private const string ChaveInicio = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string NomeInicio = "BCFichas";

    // SetThreadExecutionState: avisa o Windows que o programa está em uso (a tela fica ligada e o tablet não
    // suspende). Vale enquanto esta thread (a da tela) existir; ao fechar o programa volta ao normal.
    private const uint EsContinuo = 0x80000000;
    private const uint EsSistema = 0x00000001;
    private const uint EsTela = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint estado);

    /// <summary>Aplica as configurações (chamado ao abrir o programa e ao salvar as configurações).</summary>
    public static void Aplicar(Configuracao config)
    {
        ManterTelaLigada(config.ManterTelaLigada);
        CriarPastaBackup(config.PastaBackup);
        try
        {
            var exe = Environment.ProcessPath;
            // Só o BCFichas.exe instalado (rodando pelo "dotnet run" o caminho seria o do dotnet).
            if (exe is not null && Path.GetFileNameWithoutExtension(exe).Equals("BCFichas", StringComparison.OrdinalIgnoreCase))
                AtualizarInicio(config.IniciarComWindows, exe);
        }
        catch (Exception e)
        {
            Log.Erro("Iniciar com o Windows", e);
        }
    }

    /// <summary>A pasta do backup já fica criada: pelo acesso remoto é só copiar o arquivo .bcf para ela.</summary>
    private static void CriarPastaBackup(string pasta)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Directory.CreateDirectory(Core.Servicos.ProgramacaoServico.CaminhoDaPasta(pasta));
        }
        catch (Exception e)
        {
            Log.Erro("Criar a pasta do backup", e);
        }
    }

    public static void ManterTelaLigada(bool ligada)
    {
        if (!OperatingSystem.IsWindows()) return;
        SetThreadExecutionState(ligada ? EsContinuo | EsSistema | EsTela : EsContinuo);
    }

    /// <summary>
    /// Liga ou desliga a abertura junto com o Windows (registro do usuário, não precisa de administrador).
    /// Se o programa mudou de pasta, o caminho é atualizado.
    /// </summary>
    public static void AtualizarInicio(bool iniciar, string exe, string nome = NomeInicio)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var chave = Registry.CurrentUser.CreateSubKey(ChaveInicio, writable: true);
        var valor = $"\"{exe}\"";
        if (iniciar)
        {
            if (!Equals(chave.GetValue(nome), valor)) chave.SetValue(nome, valor, RegistryValueKind.String);
        }
        else if (chave.GetValue(nome) is not null)
        {
            chave.DeleteValue(nome, throwOnMissingValue: false);
        }
    }

    /// <summary>O que está gravado para abrir com o Windows (nulo se não abre).</summary>
    public static string? Inicio(string nome = NomeInicio)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var chave = Registry.CurrentUser.OpenSubKey(ChaveInicio);
        return chave?.GetValue(nome) as string;
    }
}

using BCFichas.Core;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>
/// Liberação do tablet contra cópia: vale só na máquina em que foi feita, fica escondida fora da pasta do programa
/// e não abre o programa se o arquivo for copiado para outra máquina ou mexido.
/// </summary>
public sealed class LiberacaoTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "bcfichas-liberacao-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            foreach (var arquivo in Directory.EnumerateFiles(_pasta, "*", SearchOption.AllDirectories))
                File.SetAttributes(arquivo, FileAttributes.Normal);
            Directory.Delete(_pasta, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Arquivo(string nome = "a") => Path.Combine(_pasta, nome, "liberacao.dat");

    [Fact]
    public void Tablet_novo_nao_esta_liberado_e_depois_de_liberar_esta()
    {
        var liberacao = new Liberacao([Arquivo()], () => "MAQUINA-1");
        Assert.False(liberacao.Liberada);
        liberacao.Liberar();
        Assert.True(liberacao.Liberada);
        Assert.True(File.Exists(Arquivo()));
        // Liberar de novo (arquivo escondido já existe) também funciona
        liberacao.Liberar();
        Assert.True(liberacao.Liberada);
        // Outra versão do programa no mesmo tablet (Rede, Cartão) usa a mesma liberação
        Assert.True(new Liberacao([Arquivo()], () => "maquina-1 ").Liberada);
    }

    [Fact]
    public void Arquivo_copiado_para_outra_maquina_nao_libera()
    {
        new Liberacao([Arquivo()], () => "MAQUINA-1").Liberar();
        var outra = new Liberacao([Arquivo()], () => "MAQUINA-2");
        Assert.False(outra.Liberada);
    }

    [Fact]
    public void Arquivo_mexido_ou_vazio_nao_libera()
    {
        var liberacao = new Liberacao([Arquivo()], () => "MAQUINA-1");
        liberacao.Liberar();
        File.SetAttributes(Arquivo(), FileAttributes.Normal);
        var texto = File.ReadAllText(Arquivo());
        File.WriteAllText(Arquivo(), texto[..^1] + (texto[^1] == '0' ? '1' : '0'));
        Assert.False(liberacao.Liberada);
        File.WriteAllText(Arquivo(), "");
        Assert.False(liberacao.Liberada);
        File.WriteAllText(Arquivo(), "liberado");
        Assert.False(liberacao.Liberada);
    }

    [Fact]
    public void Sem_permissao_na_primeira_pasta_grava_na_segunda()
    {
        // A "pasta" da primeira é um arquivo: não dá para gravar ali
        Directory.CreateDirectory(_pasta);
        File.WriteAllText(Path.Combine(_pasta, "a"), "não é pasta");
        var liberacao = new Liberacao([Arquivo("a"), Arquivo("b")], () => "MAQUINA-1");
        liberacao.Liberar();
        Assert.True(liberacao.Liberada);
        Assert.True(File.Exists(Arquivo("b")));
        // E vale achando só na segunda
        Assert.True(new Liberacao([Arquivo("a"), Arquivo("b")], () => "MAQUINA-1").Liberada);
    }

    [Fact]
    public void Sem_lugar_para_gravar_avisa()
    {
        Directory.CreateDirectory(_pasta);
        File.WriteAllText(Path.Combine(_pasta, "a"), "não é pasta");
        var liberacao = new Liberacao([Arquivo("a")], () => "MAQUINA-1");
        var erro = Assert.Throws<ErroDeNegocio>(liberacao.Liberar);
        Assert.StartsWith("Não consegui gravar a liberação deste tablet.", erro.Message);
        Assert.False(liberacao.Liberada);
    }

    [Fact]
    public void Codigo_da_maquina_existe_e_nao_muda()
    {
        var codigo = Liberacao.CodigoDoWindows();
        Assert.False(string.IsNullOrWhiteSpace(codigo));
        Assert.Equal(codigo, Liberacao.CodigoDoWindows());
    }
}

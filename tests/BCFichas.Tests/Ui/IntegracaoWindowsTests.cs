using Avalonia.Headless.XUnit;
using BCFichas.App;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Servicos;
using BCFichas.Tests.Core;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>Tela sempre ligada e abrir junto com o Windows.</summary>
public class IntegracaoWindowsTests
{
    [Fact]
    public void Vem_ligado_ate_em_configuracao_antiga()
    {
        Assert.True(new Configuracao().ManterTelaLigada);
        Assert.True(new Configuracao().IniciarComWindows);

        using var t = new SistemaTemporario();
        t.Sistema.Banco.Executar("UPDATE config SET valor = $v WHERE chave = 'geral'",
            ("$v", """{"NomeEvento":"QUERMESSE","VersaoConfig":1}"""));
        var lida = new ConfigServico(t.Sistema.Banco).Atual;
        Assert.True(lida.ManterTelaLigada);
        Assert.True(lida.IniciarComWindows);
    }

    [AvaloniaFact]
    public void Configuracao_salva_vai_para_o_windows_na_hora()
    {
        using var t = new TelaDeTeste();
        var aplicadas = new List<Configuracao>();
        t.Principal.AplicarNoWindows = aplicadas.Add;

        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        Assert.True(tela.ManterTelaLigada);
        Assert.True(tela.IniciarComWindows);
        TelaDeTeste.Atualizar();
        t.Foto("21-config-geral");

        tela.ManterTelaLigada = false;
        tela.IniciarComWindows = false;
        tela.SalvarCommand.Execute(null);
        TelaDeTeste.Atualizar();

        var ultima = aplicadas.Last();
        Assert.False(ultima.ManterTelaLigada);
        Assert.False(ultima.IniciarComWindows);
        Assert.False(t.Sistema.Config.Atual.IniciarComWindows);
    }

    /// <summary>Só roda no Windows (no GitHub): grava e apaga uma entrada de teste no "Executar" do usuário.</summary>
    [Fact]
    public void No_windows_grava_e_tira_do_inicio()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string nome = "BCFichas-Teste-Automatico";
        try
        {
            IntegracaoWindows.AtualizarInicio(true, @"C:\BCFichas\BCFichas.exe", nome);
            Assert.Equal("\"C:\\BCFichas\\BCFichas.exe\"", IntegracaoWindows.Inicio(nome));
            // Programa mudou de pasta: o caminho é atualizado
            IntegracaoWindows.AtualizarInicio(true, @"D:\Fichas\BCFichas.exe", nome);
            Assert.Equal("\"D:\\Fichas\\BCFichas.exe\"", IntegracaoWindows.Inicio(nome));
            IntegracaoWindows.AtualizarInicio(false, @"D:\Fichas\BCFichas.exe", nome);
            Assert.Null(IntegracaoWindows.Inicio(nome));
            // Manter a tela ligada e voltar ao normal não dá erro
            IntegracaoWindows.ManterTelaLigada(true);
            IntegracaoWindows.ManterTelaLigada(false);
        }
        finally
        {
            IntegracaoWindows.AtualizarInicio(false, "", nome);
        }
    }
}

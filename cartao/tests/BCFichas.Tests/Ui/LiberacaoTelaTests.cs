using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>
/// Tela de liberação: num tablet não liberado (programa copiado de outra máquina) o BC Fichas não abre o caixa;
/// só depois da senha técnica. Liberado uma vez, abre direto.
/// </summary>
public class LiberacaoTelaTests
{
    [AvaloniaFact]
    public async Task Tablet_nao_liberado_so_abre_com_a_senha_tecnica()
    {
        using var t = new TelaDeTeste(1024, 600, liberada: false);
        var tela = Assert.IsType<LiberacaoViewModel>(t.Principal.Pagina);
        Assert.False(File.Exists(t.ArquivoDaLiberacao));
        t.Foto("liberacao-1024x600");

        // Senha errada: continua bloqueado
        tela.SenhaDigitada = "errada";
        await tela.LiberarCommand.ExecuteAsync(null);
        Assert.Equal("Senha técnica errada.", tela.Erro);
        Assert.Same(tela, t.Principal.Pagina);
        Assert.False(t.Liberacao.Liberada);
        t.Foto("liberacao-senha-errada-1024x600");

        // Senha técnica certa: libera e abre o caixa
        tela.SenhaDigitada = TelaDeTeste.SenhaTecnica;
        await tela.LiberarCommand.ExecuteAsync(null);
        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        Assert.True(t.Liberacao.Liberada);
        Assert.True(File.Exists(t.ArquivoDaLiberacao));
        Assert.Equal("Tablet liberado.", t.Principal.Aviso);

        // Fechou e abriu de novo: abre direto, sem pedir senha
        t.Principal.Iniciar();
        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
    }

    [AvaloniaFact]
    public void Programa_copiado_com_o_arquivo_para_outra_maquina_nao_abre()
    {
        using var t = new TelaDeTeste();
        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        // A mesma pasta e o mesmo arquivo, mas em outro computador
        t.Principal.Liberacao = new Liberacao([t.ArquivoDaLiberacao], () => "OUTRA-MAQUINA");
        t.Principal.Iniciar();
        Assert.IsType<LiberacaoViewModel>(t.Principal.Pagina);
    }

    [AvaloniaFact]
    public void Tablet_liberado_com_caixa_aberto_volta_direto_para_a_venda()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Principal.Iniciar();
        Assert.IsType<VendaViewModel>(t.Principal.Pagina);
    }

    [AvaloniaFact]
    public void Sair_na_tela_de_liberacao_fecha_o_programa()
    {
        using var t = new TelaDeTeste(liberada: false);
        var fechou = false;
        t.Principal.FecharPrograma = () => fechou = true;
        Assert.IsType<LiberacaoViewModel>(t.Principal.Pagina).SairCommand.Execute(null);
        Assert.True(fechou);
    }

    /// <summary>F1 (o atalho do modo teste) na tela de liberação não abre o caixa, nem de teste.</summary>
    [AvaloniaFact]
    public async Task F1_na_tela_de_liberacao_nao_abre_o_programa()
    {
        using var t = new TelaDeTeste(liberada: false);
        t.Janela.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        TelaDeTeste.Atualizar();
        Assert.Null(t.Principal.Dialogo);
        await t.Principal.EntrarNoModoTeste();
        Assert.False(t.Principal.ModoTeste);
        Assert.IsType<LiberacaoViewModel>(t.Principal.Pagina);
        Assert.False(t.Liberacao.Liberada);
    }
}

using Avalonia.Headless.XUnit;
using BCFichas.App.ViewModels;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>Aba Máquina: salvar e carregar a programação pelo pendrive, outra festa e como nova.</summary>
public class TelaMaquinaTests
{
    private const int AbaMaquina = 6;

    [AvaloniaFact]
    public async Task Salva_no_pendrive_e_carrega_perguntando_so_o_numero_do_caixa()
    {
        using var t = new TelaDeTeste();
        var pendrive = Path.Combine(t.Sistema.PastaDados, "pendrive");
        Directory.CreateDirectory(pendrive);
        t.Principal.Pendrives = () => [pendrive];

        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.AbaSelecionada = AbaMaquina;
        TelaDeTeste.Atualizar();
        Assert.StartsWith("Caixa 01 • 15 produto(s) em 3 aba(s)", tela.SituacaoMaquina);
        t.Foto("42-config-maquina");

        // Salvar: com um pendrive só, grava direto nele
        var salvar = tela.SalvarProgramacaoCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        var arquivo = Assert.Single(Directory.GetFiles(pendrive, "*.bcf"));
        Assert.EndsWith("BCFichas - FESTA DE SÃO JOÃO.bcf", arquivo);
        ((MensagemViewModel)t.Principal.Dialogo!).SimCommand.Execute(null);
        await salvar;

        // Carregar: acha o arquivo no pendrive, mostra o resumo e pergunta o número do caixa
        var carregar = tela.CarregarProgramacaoCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        var confirmar = (MensagemViewModel)t.Principal.Dialogo!;
        Assert.Contains("Evento: FESTA DE SÃO JOÃO", confirmar.Texto);
        Assert.Contains("15 produto(s) em 3 aba(s)", confirmar.Texto);
        t.Foto("43-carregar-programacao");
        confirmar.SimCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is NumeroViewModel);
        var numero = (NumeroViewModel)t.Principal.Dialogo!;
        Assert.Equal("1", numero.Digitado);
        numero.TeclaCommand.Execute("C");
        numero.TeclaCommand.Execute("3");
        t.Foto("44-numero-do-caixa");
        numero.ConfirmarCommand.Execute(null);
        await carregar;

        Assert.Equal(3, t.Sistema.Config.Atual.NumeroCaixa);
        Assert.Equal("Programação carregada: FESTA DE SÃO JOÃO, Caixa 03.", t.Principal.Aviso);
        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        // A configuração nova chega no cabeçalho pela fila da tela.
        await TelaDeTeste.Esperar(() => t.Principal.CaixaTexto == "CAIXA 03");
    }

    [AvaloniaFact]
    public async Task Com_caixa_aberto_avisa_para_fechar_antes()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);

        var apagar = tela.ComecarOutraFestaCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        ((MensagemViewModel)t.Principal.Dialogo!).SimCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel { Titulo: "Não deu para apagar" });
        Assert.Equal("Feche o caixa antes (Menu → Fechar caixa).", ((MensagemViewModel)t.Principal.Dialogo!).Texto);
        ((MensagemViewModel)t.Principal.Dialogo!).SimCommand.Execute(null);
        await apagar;
        Assert.NotNull(t.Principal.Sessao);
    }

    [AvaloniaFact]
    public async Task Deixar_como_nova_volta_para_a_abertura_sem_produtos()
    {
        using var t = new TelaDeTeste();
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        var apagar = tela.DeixarComoNovaCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        t.Foto("45-como-nova-confirmar");
        ((MensagemViewModel)t.Principal.Dialogo!).SimCommand.Execute(null);
        await apagar;

        Assert.Empty(t.Sistema.Catalogo.Produtos());
        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        Assert.Equal("Máquina como nova. Cadastre os produtos do próximo evento.", t.Principal.Aviso);
    }
}

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BCFichas.App;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>Pontos levantados na auditoria das telas que foram corrigidos à parte.</summary>
public class AjustesDaAuditoriaTelasTests
{
    [AvaloniaFact]
    public void Erro_inesperado_num_botao_avisa_e_o_programa_continua()
    {
        using var t = new TelaDeTeste();
        using var erros = ErrosNaTela.Instalar(t.Principal);
        t.Principal.Aviso = null;

        Dispatcher.UIThread.Post(() => throw new IOException("disco cheio"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(t.Principal.AvisoErro);
        Assert.Contains("Algo deu errado", t.Principal.Aviso);

        // Erro de regra (com mensagem para o operador): aparece a própria mensagem
        Dispatcher.UIThread.Post(() => throw new ErroDeNegocio("O caixa está fechado."));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("O caixa está fechado.", t.Principal.Aviso);
    }

    [AvaloniaFact]
    public async Task Fechar_caixa_avisa_das_fichas_pagas_que_nao_sairam()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        // Venda paga cujas fichas não saíram (a impressora falhou)
        var carrinho = new Carrinho();
        carrinho.Adicionar(t.Sistema.Catalogo.Produtos().First(p => p.Nome == "PASTEL"));
        t.Sistema.Vendas.CriarPedido(t.Principal.Sessao!, carrinho.Linhas, FormaPagamento.Dinheiro, 1000);
        Assert.Equal(1, t.Sistema.Vendas.NaoImpressos(t.Principal.Sessao!.Id));

        var fechamento = new FechamentoViewModel(t.Principal);
        t.Principal.Abrir(fechamento);
        var fechar = fechamento.FecharCaixaCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        var pergunta = (MensagemViewModel)t.Principal.Dialogo!;
        Assert.Contains("1 pedido(s) pago(s) com fichas que não saíram", pergunta.Texto);
        pergunta.NaoCommand.Execute(null);
        await fechar;
        Assert.NotNull(t.Principal.Sessao); // continua aberto: dá para imprimir antes
    }

    [AvaloniaFact]
    public void Voltar_espera_o_fechamento_terminar_de_imprimir()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        var fechamento = new FechamentoViewModel(t.Principal);
        t.Principal.Abrir(fechamento);

        fechamento.Ocupado = true; // fechando e imprimindo
        fechamento.VoltarCommand.Execute(null);
        Assert.Same(fechamento, t.Principal.Pagina);

        fechamento.Ocupado = false;
        fechamento.VoltarCommand.Execute(null);
        Assert.IsType<VendaViewModel>(t.Principal.Pagina);
    }
}

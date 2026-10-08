using Avalonia.Headless.XUnit;
using BCFichas.App;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Pagamento;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>
/// Erro no disco (cheio, com defeito) no meio de um pagamento no cartão ou PIX: a janela de pagamento nunca pode
/// ficar presa em "Enviando para a maquininha..." sem nenhum botão que funcione.
/// </summary>
public class PagamentoComErroNoDiscoTests
{
    [AvaloniaFact]
    public async Task Erro_ao_gravar_o_pedido_deixa_escolher_outra_forma()
    {
        using var t = new TelaDeTeste();
        using var erros = ErrosNaTela.Instalar(t.Principal);
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);

        // O disco falha na hora de gravar o pedido
        t.Sistema.Banco.Executar("ALTER TABLE contadores RENAME TO contadores_x");
        await pagamento.DebitoCommand.ExecuteAsync(null);
        t.Sistema.Banco.Executar("ALTER TABLE contadores_x RENAME TO contadores");

        Assert.True(pagamento.EmRecusado);
        Assert.StartsWith("Não consegui gravar o pedido", pagamento.Mensagem);
        Assert.True(pagamento.PodeFechar);
        // Nada foi cobrado: escolhe de novo e paga
        pagamento.OutraFormaCommand.Execute(null);
        Assert.True(pagamento.EmEscolher);
        Assert.False(t.Venda.Vazio);
    }

    [AvaloniaFact]
    public async Task Erro_ao_gravar_o_pagamento_aprovado_deixa_tentar_de_novo_sem_cobrar_outra_vez()
    {
        using var t = new TelaDeTeste();
        using var erros = ErrosNaTela.Instalar(t.Principal);
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);

        // O disco falha na hora de marcar o pedido como pago
        t.Sistema.Banco.Executar("CREATE TRIGGER falha_confirmar BEFORE UPDATE OF status ON pedidos " +
                                 "WHEN NEW.status = 1 BEGIN SELECT RAISE(ABORT, 'disk I/O error'); END");
        var debito = pagamento.DebitoCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => ((MaquininhaSeparada)t.Sistema.Maquininha).AguardandoDecisao);
        await Task.Delay(700);
        pagamento.ConfirmarNaMaquininhaCommand.Execute(null); // o cliente pagou na maquininha
        await debito;

        Assert.True(pagamento.EmErroGravacao);
        Assert.False(pagamento.PodeFechar); // não volta para escolher a forma: seria cobrar de novo
        var pedido = Assert.Single(t.Sistema.Vendas.Pendentes());
        Assert.Equal(StatusPedido.AguardandoPagamento, pedido.Status);
        Assert.False(t.Venda.Vazio); // o pedido continua na tela

        // Se o disco continuar com erro, dá para sair do programa (ao abrir, ele pergunta deste pedido)
        var fechou = false;
        t.Principal.FecharPrograma = () => fechou = true;
        pagamento.SairDoProgramaCommand.Execute(null);
        Assert.True(fechou);

        // O disco voltou: tentar de novo grava e as fichas saem
        t.Sistema.Banco.Executar("DROP TRIGGER falha_confirmar");
        await pagamento.GravarDeNovoCommand.ExecuteAsync(null);
        Assert.True(pagamento.EmConcluido);
        Assert.Equal(StatusPedido.Pago, t.Sistema.Vendas.Pedido(pedido.Id)!.Status);
        Assert.Empty(t.Sistema.Vendas.Pendentes());
        t.EsperarImpressoes(1);
    }
}

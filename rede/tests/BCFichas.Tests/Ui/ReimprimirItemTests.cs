using Avalonia.Headless.XUnit;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>
/// "Só este item" só vale para pedido que já saiu na impressora: a primeira impressão de um pedido é sempre o
/// pedido inteiro (senão a conta das fichas que já saíram ficaria errada).
/// </summary>
public class ReimprimirItemTests
{
    [AvaloniaFact]
    public async Task So_este_item_so_para_pedido_que_ja_saiu()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        // Um pedido pago que não saiu na impressora (a impressora falhou na hora)
        var carrinho = new Carrinho();
        carrinho.Adicionar(t.Sistema.Catalogo.Produtos().First(p => !p.EhCombo));
        var pedido = t.Sistema.Vendas.CriarPedido(t.Principal.Sessao!, carrinho.Linhas, FormaPagamento.Dinheiro, 100_000);
        Assert.Equal(0, pedido.Impressoes);

        var tela = new ReimpressaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        Assert.Equal(pedido.Id, tela.Selecionado!.Pedido.Id);
        Assert.True(tela.PodeReimprimir);
        Assert.False(tela.PodeReimprimirItem);
        await tela.ReimprimirItemCommand.ExecuteAsync(tela.Itens[0]);
        Assert.Equal("As fichas deste pedido ainda não saíram: imprima o pedido inteiro.", t.Principal.Aviso);
        Assert.Equal(0, t.Sistema.Vendas.Pedido(pedido.Id)!.Impressoes);

        // Saiu inteiro: daí em diante dá para reimprimir só um item
        await tela.ReimprimirTudoCommand.ExecuteAsync(null);
        Assert.Equal(1, t.Sistema.Vendas.Pedido(pedido.Id)!.Impressoes);
        Assert.True(tela.PodeReimprimirItem);
    }
}

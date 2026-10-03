using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using BCFichas.App.Views;
using BCFichas.Core;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>
/// O tablet é fraco: a venda não pode refazer a tela a cada toque ou a cada venda. Também a impressora que falha
/// com a reimpressão travada (o pedido pago ainda tem como sair).
/// </summary>
public class DesempenhoDaVendaTests
{
    private static async Task VenderEmDinheiro(TelaDeTeste t, params string[] produtos)
    {
        foreach (var p in produtos) t.Tocar(p);
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        pagamento.FecharCommand.Execute(null);
        TelaDeTeste.Atualizar();
    }

    [AvaloniaFact]
    public async Task Venda_atualiza_o_estoque_nos_mesmos_botoes_sem_refazer_a_grade()
    {
        using var t = new TelaDeTeste();
        var pastel = t.Sistema.Catalogo.Produtos().First(p => p.Nome == "PASTEL");
        pastel.ControlaEstoque = true;
        pastel.Estoque = 3;
        t.Sistema.Catalogo.SalvarProduto(pastel, 30);
        t.AbrirCaixa();
        TelaDeTeste.Atualizar();
        var antes = t.Venda.Botoes.ToList();
        Assert.Equal("3 rest.", antes.First(b => b.Nome == "PASTEL").Estoque);

        await VenderEmDinheiro(t, "PASTEL", "PASTEL");
        TelaDeTeste.Atualizar();
        Assert.Equal(antes, t.Venda.Botoes.ToList()); // os mesmos botões, só o número mudou
        Assert.Equal("1 rest.", t.Venda.Botoes.First(b => b.Nome == "PASTEL").Estoque);

        await VenderEmDinheiro(t, "PASTEL");
        TelaDeTeste.Atualizar();
        var botao = t.Venda.Botoes.First(b => b.Nome == "PASTEL");
        Assert.True(botao.Esgotado);
        Assert.Same(antes.First(b => b.Nome == "PASTEL"), botao);
    }

    [AvaloniaFact]
    public void Tocar_no_produto_muda_so_a_linha_dele()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        var pastel = t.Venda.Linhas[0];
        var espetinho = t.Venda.Linhas[1];
        t.Tocar("PASTEL");
        Assert.Same(pastel, t.Venda.Linhas[0]);
        Assert.Same(espetinho, t.Venda.Linhas[1]);
        Assert.Equal(2, pastel.Quantidade);
        Assert.Equal("R$ 20,00", pastel.Total);

        t.Venda.MenosCommand.Execute(espetinho);
        Assert.Equal([pastel], t.Venda.Linhas);
        t.Venda.MenosCommand.Execute(pastel);
        Assert.Same(pastel, t.Venda.Linhas.Single());
        Assert.Equal(1, pastel.Quantidade);
    }

    [AvaloniaFact]
    public async Task A_tela_de_venda_e_a_de_pagamento_sao_montadas_uma_vez_so()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        TelaDeTeste.Atualizar();
        var venda = t.Achar<VendaView>();

        t.Venda.AbrirSangriaCommand.Execute(null);
        TelaDeTeste.Atualizar();
        ((PaginaViewModel)t.Principal.Pagina!).VoltarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        Assert.Same(venda, t.Achar<VendaView>());

        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        var primeira = t.Achar<PagamentoView>();
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.FecharCommand.Execute(null);
        TelaDeTeste.Atualizar();

        // Outro pedido: a mesma janela, com o pagamento novo (valor novo na tela)
        t.Tocar("ESPETINHO");
        t.Venda.PagarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        var segunda = t.Achar<PagamentoView>();
        Assert.Same(primeira, segunda);
        Assert.Same(t.Principal.Dialogo, segunda.DataContext);
        Assert.Contains(segunda.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(),
            x => x.Text == "R$ 22,00" && x.IsEffectivelyVisible);

        // Enter no teclado com o botão Pagamento ainda selecionado não abre outro pagamento por baixo
        t.Venda.PagarCommand.Execute(null);
        Assert.Same(t.Principal.Dialogo, segunda.DataContext);
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Impressora_falhou_com_a_reimpressao_travada_o_pedido_ainda_sai_pelo_menu()
    {
        using var t = new TelaDeTeste(configurar: c =>
        {
            c.Impressora = TipoImpressora.Serial;
            c.PortaSerial = "COM99";
        });
        Assert.False(t.Sistema.Config.Atual.LiberarReimpressao);
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        Assert.True(pagamento.EmErroImpressao);
        Assert.Equal("Imprimir depois", pagamento.TextoFecharSemImprimir);
        pagamento.FecharCommand.Execute(null);

        // No menu aparece "Fichas não impressas" (e só ela: a reimpressão continua travada)
        t.Venda.AbrirMenuCommand.Execute(null);
        var menu = Assert.IsType<MenuViewModel>(t.Principal.Dialogo);
        Assert.DoesNotContain(menu.Itens, i => i.Titulo == "Reimprimir fichas");
        menu.EscolherCommand.Execute(menu.Itens.Single(i => i.Titulo == "Fichas não impressas"));
        var tela = Assert.IsType<ReimpressaoViewModel>(t.Principal.Pagina);
        Assert.True(tela.SoNaoImpressos);
        Assert.False(tela.PodeReimprimirItem);
        t.Principal.Aviso = null;
        TelaDeTeste.Atualizar();
        t.Foto("61-fichas-nao-impressas");

        // A impressora voltou: sai uma vez e o pedido some da lista
        var config = t.Sistema.Config.Atual.Clonar();
        config.Impressora = TipoImpressora.Arquivo;
        t.Sistema.Config.Salvar(config);
        await tela.ReimprimirTudoCommand.ExecuteAsync(null);
        Assert.Single(t.EsperarImpressoes(1));
        Assert.Empty(tela.Pedidos);
        Assert.Equal(0, t.Sistema.Vendas.NaoImpressos(t.Principal.Sessao!.Id));
        t.Principal.IrParaVenda();
        t.Venda.AbrirMenuCommand.Execute(null);
        Assert.DoesNotContain(Assert.IsType<MenuViewModel>(t.Principal.Dialogo).Itens, i => i.Titulo == "Fichas não impressas");
    }
}

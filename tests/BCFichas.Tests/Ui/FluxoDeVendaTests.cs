using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using BCFichas.App.Views;
using BCFichas.Core;
using BCFichas.Core.Pagamento;
using Xunit;

namespace BCFichas.Tests.Ui;

public class FluxoDeVendaTests
{
    [AvaloniaFact]
    public void Sem_caixa_aberto_mostra_a_abertura()
    {
        using var t = new TelaDeTeste();
        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        // Os caixas são identificados só pelo número: a abertura pede só o troco
        Assert.DoesNotContain(t.Janela.GetVisualDescendants().OfType<TextBox>(), c => c.IsEffectivelyVisible);
        Assert.Contains(t.Janela.GetVisualDescendants().OfType<TextBlock>(),
            b => b.Text == "Troco Inicial (Abertura de Caixa)");
        t.Foto("01-abertura-de-caixa");

        var abertura = Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        abertura.Troco.Centavos = 5000;
        abertura.AbrirCaixaCommand.Execute(null);
        TelaDeTeste.Atualizar();

        Assert.IsType<VendaViewModel>(t.Principal.Pagina);
        Assert.Equal("Caixa 01 aberto. Boas vendas!", t.Principal.Aviso);
        // Abrir o caixa imprime o comprovante de abertura (troco, data e hora)
        Assert.Single(t.EsperarImpressoes(1));
        Assert.Equal("", t.Principal.Operador);
        Assert.Equal(5000, t.Principal.Sessao!.ValorAberturaCentavos);
        Assert.Equal(12, t.Venda.Botoes.Count);
        Assert.Equal(["COMIDAS", "BEBIDAS", "DOCES"], t.Venda.Abas.Select(a => a.Nome));
    }

    [AvaloniaFact]
    public async Task Venda_em_dinheiro_mostra_troco_e_imprime_as_fichas()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Foto("02-venda-vazia");

        t.Tocar("PASTEL");
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        t.Venda.SelecionarAbaCommand.Execute(t.Venda.Abas[1]);
        t.Tocar("REFRIGERANTE");
        Assert.Equal("R$ 38,00", t.Venda.Total);
        Assert.Equal("4 itens", t.Venda.QuantidadeTexto);
        t.Foto("03-venda-com-pedido");

        t.Venda.PagarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        t.Foto("04-pagamento-formas");

        pagamento.EscolherDinheiroCommand.Execute(null);
        pagamento.Recebido.SomarCommand.Execute("5000");
        Assert.Equal("R$ 12,00", pagamento.Troco);
        t.Foto("05-pagamento-dinheiro");

        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        Assert.True(pagamento.EmConcluido);
        Assert.True(pagamento.TemTroco);
        t.Foto("06-pagamento-concluido");

        Assert.Equal(4, t.EsperarImpressoes(4).Length);
        Assert.True(t.Venda.Vazio);
        var resumo = t.Sistema.Caixa.Resumo(t.Principal.Sessao!.Id);
        Assert.Equal(3800, resumo.Total(FormaPagamento.Dinheiro));

        pagamento.FecharCommand.Execute(null);
        Assert.Null(t.Principal.Dialogo);
    }

    [AvaloniaFact]
    public async Task Maquininha_separada_so_registra_a_forma_quando_o_operador_confirma()
    {
        using var t = new TelaDeTeste();
        Assert.IsType<MaquininhaSeparada>(t.Sistema.Maquininha);
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Tocar("ESPETINHO");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);

        var cobranca = pagamento.DebitoCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => ((MaquininhaSeparada)t.Sistema.Maquininha).AguardandoDecisao);
        Assert.True(pagamento.EhSeparada);
        Assert.Equal("Passe R$ 22,00 no DÉBITO na maquininha", pagamento.Andamento);
        t.Foto("07-pagamento-maquininha-separada");

        pagamento.ConfirmarNaMaquininhaCommand.Execute(null);
        await cobranca;

        Assert.True(pagamento.EmConcluido);
        Assert.Equal(2, t.EsperarImpressoes(2).Length);
        var pedido = t.Sistema.Vendas.Pedidos(t.Principal.Sessao!.Id).Single();
        Assert.Equal(StatusPedido.Pago, pedido.Status);
        Assert.Equal(FormaPagamento.Debito, pedido.Forma);
        Assert.Equal(2200, t.Sistema.Caixa.Resumo(t.Principal.Sessao.Id).Total(FormaPagamento.Debito));
    }

    [AvaloniaFact]
    public async Task Maquininha_separada_nao_aprovou_cancela_o_pedido()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);

        var cobranca = pagamento.PixCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => ((MaquininhaSeparada)t.Sistema.Maquininha).AguardandoDecisao);
        Assert.Equal("Gere o PIX de R$ 10,00 na maquininha", pagamento.Andamento);
        pagamento.NaoAprovouCommand.Execute(null);
        await cobranca;

        Assert.True(pagamento.EmRecusado);
        Assert.Equal(StatusPedido.Cancelado, t.Sistema.Vendas.Pedidos(t.Principal.Sessao!.Id).Single().Status);
        Assert.False(t.Venda.Vazio);
    }

    [AvaloniaFact]
    public async Task Pix_espera_a_maquininha_e_imprime_quando_aprovado()
    {
        using var t = new TelaDeTeste(configurar: c => c.Maquininha = TipoMaquininha.Simulador);
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        var simulador = Assert.IsType<MaquininhaSimulada>(t.Sistema.Maquininha);

        var cobranca = pagamento.PixCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => simulador.AguardandoDecisao);
        Assert.True(pagamento.EmMaquininha);
        Assert.False(pagamento.PodeFechar);
        t.Foto("07-pagamento-pix-aguardando");

        pagamento.SimularAprovarCommand.Execute(null);
        await cobranca;

        Assert.True(pagamento.EmConcluido);
        Assert.Single(t.EsperarImpressoes(1));
        var pedido = t.Sistema.Vendas.Pedidos(t.Principal.Sessao!.Id).Single();
        Assert.Equal(StatusPedido.Pago, pedido.Status);
        Assert.Equal(FormaPagamento.Pix, pedido.Forma);
        Assert.Equal(1, pedido.Impressoes);
    }

    [AvaloniaFact]
    public async Task Cartao_recusado_cancela_o_pedido_e_deixa_tentar_de_novo()
    {
        using var t = new TelaDeTeste(configurar: c => c.Maquininha = TipoMaquininha.Simulador);
        t.AbrirCaixa();
        t.Tocar("ESPETINHO");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        var simulador = (MaquininhaSimulada)t.Sistema.Maquininha;

        var cobranca = pagamento.CreditoCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => simulador.AguardandoDecisao);
        pagamento.SimularRecusarCommand.Execute(null);
        await cobranca;

        Assert.True(pagamento.EmRecusado);
        t.Foto("08-pagamento-recusado");
        Assert.Equal(StatusPedido.Cancelado, t.Sistema.Vendas.Pedidos(t.Principal.Sessao!.Id).Single().Status);
        Assert.False(t.Venda.Vazio);

        pagamento.OutraFormaCommand.Execute(null);
        Assert.True(pagamento.EmEscolher);
    }

    [AvaloniaFact]
    public async Task Impressora_com_erro_avisa_e_permite_tentar_de_novo()
    {
        using var t = new TelaDeTeste();
        var config = t.Sistema.Config.Atual.Clonar();
        config.Impressora = TipoImpressora.Serial;
        config.PortaSerial = "COM99";
        t.Sistema.Config.Salvar(config);
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);

        pagamento.EscolherDinheiroCommand.Execute(null);
        pagamento.ValorExatoCommand.Execute(null);
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);

        Assert.True(pagamento.EmErroImpressao);
        Assert.Contains("COM99", pagamento.Mensagem);
        t.Foto("09-pagamento-erro-impressora");
        Assert.True(t.Principal.ImpressoraComErro);
    }

    [AvaloniaFact]
    public void Menu_lista_as_ferramentas_e_pede_senha_nas_protegidas()
    {
        using var t = new TelaDeTeste(configurar: c => c.SenhaMaster = "1234");
        t.AbrirCaixa();
        t.Venda.AbrirMenuCommand.Execute(null);
        var menu = Assert.IsType<MenuViewModel>(t.Principal.Dialogo);
        Assert.Equal(8, menu.Itens.Count);
        Assert.True(menu.Itens.Single(i => i.Titulo == "Devolver fichas").Protegido);
        t.Foto("10-menu");

        menu.EscolherCommand.Execute(menu.Itens.Single(i => i.Titulo == "Configurações"));
        var senha = Assert.IsType<SenhaViewModel>(t.Principal.Dialogo);
        senha.TeclaCommand.Execute("9");
        senha.ConfirmarCommand.Execute(null);
        Assert.Equal("Senha errada", senha.Erro);
        foreach (var d in "1234") senha.TeclaCommand.Execute(d.ToString());
        t.Foto("11-senha");
        senha.ConfirmarCommand.Execute(null);

        Assert.Null(t.Principal.Dialogo);
        Assert.IsType<ConfiguracaoViewModel>(t.Principal.Pagina);
    }

    [AvaloniaFact]
    public void Teclado_na_tela_digita_no_campo_selecionado()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        var produtos = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(produtos);
        TelaDeTeste.Atualizar();
        var campo = t.Achar<TextBox>(c => c.Watermark == "Ex.: PASTEL");
        campo.Focus();
        TelaDeTeste.Atualizar();
        Assert.True(t.Principal.TecladoVisivel);
        Assert.False(t.Principal.TecladoNumerico);

        var teclado = t.Achar<TecladoVirtual>();
        foreach (var letra in new[] { "J", "O", "Ã", "O" })
        {
            if (letra == "Ã") Clicar(teclado, "acentos");
            Clicar(teclado, letra);
            if (letra == "Ã") Clicar(teclado, "acentos");
        }
        Clicar(teclado, "⌫");
        Clicar(teclado, "O");
        t.Foto("12-produtos-com-teclado");

        Assert.Equal("JOÃO", campo.Text);
        Assert.Equal("JOÃO", produtos.Nome);

        Clicar(teclado, "esconder");
        Assert.False(t.Principal.TecladoVisivel);
    }

    [AvaloniaFact]
    public void Tela_pequena_1024x600_tambem_cabe()
    {
        using var t = new TelaDeTeste(1024, 600);
        t.AbrirCaixa();
        t.Tocar("PASTEL");
        t.Tocar("CALDO");
        t.Foto("13-venda-1024x600");
        t.Venda.PagarCommand.Execute(null);
        t.Foto("14-pagamento-1024x600");
    }

    [AvaloniaFact]
    public void Catalogo_padrao_com_fotos_em_tela_de_960x600()
    {
        using var t = new TelaDeTeste(960, 600, catalogoPadrao: true);
        // Fotos de mentira (emoji) só para ver o botão com imagem.
        var fotos = new Dictionary<string, string>
        {
            ["PASTEL"] = "🥟", ["MASSINHA"] = "🍝", ["BATATA PORÇÃO"] = "🍟", ["ENROLADINHO PORÇÃO"] = "🥐",
            ["PÃO DE MEL"] = "🍫", ["HEINEKEN"] = "🍺", ["ORIGINAL"] = "🍺", ["BRAHMA"] = "🍺",
            ["IMPÉRIO"] = "🍺", ["REFRIGERANTE"] = "🥤", ["ÁGUA"] = "💧", ["SUCO"] = "🧃",
        };
        foreach (var produto in t.Sistema.Catalogo.Produtos())
        {
            if (!fotos.TryGetValue(produto.Nome, out var emoji)) continue;
            var origem = Path.Combine(t.Sistema.PastaDados, produto.Id + "-origem.png");
            using (var bmp = new SkiaSharp.SKBitmap(256, 256))
            using (var c = new SkiaSharp.SKCanvas(bmp))
            using (var p = new SkiaSharp.SKPaint
                   {
                       Typeface = SkiaSharp.SKTypeface.FromFamilyName("Noto Color Emoji"), TextSize = 190,
                       IsAntialias = true,
                   })
            {
                c.Clear(SkiaSharp.SKColors.Transparent);
                c.DrawText(emoji, 18, 205, p);
                BCFichas.Core.Impressao.ImagemUtil.SalvarPng(bmp, origem);
            }
            var relativo = Path.Combine("imagens", "produtos", produto.Id + ".png");
            BCFichas.Core.Impressao.ImagemUtil.Importar(origem, Path.Combine(t.Sistema.PastaDados, relativo), 256);
            produto.Imagem = relativo;
            t.Sistema.Catalogo.SalvarProduto(produto, 12);
        }
        TelaDeTeste.Atualizar();
        t.AbrirCaixa();

        Assert.False(t.Venda.MostrarAbas);
        Assert.Equal(12, t.Venda.Botoes.Count(b => !b.Vazio));
        Assert.All(t.Venda.Botoes, b => Assert.True(b.TemImagem));
        t.Tocar("PASTEL");
        t.Tocar("HEINEKEN");
        t.Tocar("HEINEKEN");
        t.Foto("15-catalogo-com-fotos-960x600");
    }

    private static void Clicar(TecladoVirtual teclado, string tecla)
    {
        var botao = teclado.GetLogicalDescendantsOfType<Button>().First(b => Equals(b.Tag, tecla));
        botao.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        TelaDeTeste.Atualizar();
    }
}

internal static class LogicalExtensions
{
    public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Avalonia.LogicalTree.ILogical raiz)
    {
        foreach (var filho in raiz.LogicalChildren)
        {
            if (filho is T t) yield return t;
            foreach (var neto in GetLogicalDescendantsOfType<T>(filho)) yield return neto;
        }
    }
}

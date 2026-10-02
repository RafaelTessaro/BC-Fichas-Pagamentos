using Avalonia.Headless.XUnit;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Ui;

public class TelasDeGestaoTests
{
    /// <summary>Caixa aberto com algumas vendas e uma sangria, para as telas terem o que mostrar.</summary>
    private static TelaDeTeste ComMovimento(Action<Configuracao>? configurar = null)
    {
        var t = new TelaDeTeste(configurar: configurar);
        t.AbrirCaixa("MARIA", 10000);
        var s = t.Sistema;
        var sessao = t.Principal.Sessao!;
        var produtos = s.Catalogo.Produtos().ToDictionary(p => p.Nome);

        void Vender(FormaPagamento forma, params (string Nome, int Qtd)[] itens)
        {
            var carrinho = new Carrinho();
            foreach (var (nome, qtd) in itens) carrinho.Adicionar(produtos[nome], qtd);
            var pedido = s.Vendas.CriarPedido(sessao, carrinho.Linhas, forma, forma == FormaPagamento.Dinheiro ? 10000 : 0);
            if (forma != FormaPagamento.Dinheiro) s.Vendas.ConfirmarPagamento(pedido.Id, "X");
            s.Vendas.RegistrarImpressao(pedido.Id);
        }

        Vender(FormaPagamento.Dinheiro, ("PASTEL", 2), ("REFRIGERANTE", 2));
        Vender(FormaPagamento.Pix, ("ESPETINHO", 3), ("CERVEJA", 3));
        Vender(FormaPagamento.Debito, ("PORÇÃO DE BATATA", 1));
        Vender(FormaPagamento.Credito, ("BOLO", 2), ("ÁGUA", 1));
        Vender(FormaPagamento.Pix, ("PIPOCA", 4));
        s.Caixa.RegistrarMovimento(sessao, TipoMovimento.Sangria, 2000, "cofre");
        s.Caixa.RegistrarMovimento(sessao, TipoMovimento.Suprimento, 1000, "moedas");
        return t;
    }

    [AvaloniaFact]
    public void Produtos_cadastra_e_edita()
    {
        using var t = ComMovimento();
        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        Assert.Equal(15, tela.Lista.Count);

        tela.Selecionado = tela.Lista.First(p => p.Nome == "PASTEL");
        Assert.Equal("10,00", tela.Preco);
        Assert.Contains(1, tela.Posicoes);
        t.Foto("20-produtos-editar");

        tela.NovoCommand.Execute(null);
        tela.Nome = "Milho cozido";
        tela.Preco = "6,50";
        tela.Aba = tela.Abas.First(a => a.Nome == "COMIDAS");
        Assert.Equal(7, tela.Posicao);
        tela.SalvarCommand.Execute(null);

        var milho = t.Sistema.Catalogo.Produtos().Single(p => p.Nome == "MILHO COZIDO");
        Assert.Equal(650, milho.PrecoCentavos);
        Assert.Equal(7, milho.Posicao);
        Assert.Equal(16, tela.Lista.Count);
    }

    [AvaloniaFact]
    public void Configuracao_mostra_previa_da_ficha_e_salva()
    {
        using var t = ComMovimento();
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        Assert.NotNull(tela.Previa);
        t.Foto("21-config-geral");

        tela.AbaSelecionada = 1;
        tela.CodigoDeBarras = true;
        t.Foto("22-config-ficha");

        tela.AbaSelecionada = 2;
        t.Foto("23-config-botoes-abas");
        tela.AbaSelecionada = 3;
        t.Foto("24-config-impressora");
        tela.AbaSelecionada = 5;
        t.Foto("25-config-seguranca");

        tela.NomeEvento = "quermesse";
        tela.Colunas = 3;
        tela.SalvarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        Assert.Equal("Configurações salvas.", t.Principal.Aviso);
        Assert.Equal("QUERMESSE", t.Sistema.Config.Atual.NomeEvento);
        Assert.Equal("QUERMESSE", t.Principal.NomeEvento);
        Assert.True(t.Sistema.Config.Atual.CodigoDeBarras);

        t.Principal.IrParaVenda();
        Assert.Equal(9, t.Venda.Botoes.Count);
    }

    [AvaloniaFact]
    public async Task Reimpressao_imprime_pedido_e_item()
    {
        using var t = ComMovimento();
        var tela = new ReimpressaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        Assert.Equal(5, tela.Pedidos.Count);
        tela.Selecionado = tela.Pedidos.Single(p => p.Forma == "PIX" && p.Pedido.TotalCentavos == 6000);
        Assert.Equal(2, tela.Itens.Count);
        t.Foto("26-reimprimir");

        await tela.ReimprimirItemCommand.ExecuteAsync(tela.Itens[1]);
        Assert.Equal(3, Directory.GetFiles(t.PastaImpressoes, "*.png").Length);
        await tela.ReimprimirTudoCommand.ExecuteAsync(null);
        Assert.Equal(9, Directory.GetFiles(t.PastaImpressoes, "*.png").Length);
        Assert.Contains("impresso 3x", tela.Selecionado!.Status);

        tela.Busca = "2";
        Assert.Single(tela.Pedidos);
    }

    [AvaloniaFact]
    public async Task Sangria_registra_e_imprime_comprovante()
    {
        using var t = ComMovimento();
        var tela = new SangriaViewModel(t.Principal);
        t.Principal.Abrir(tela);
        Assert.Equal(2, tela.Movimentos.Count);

        tela.Valor.SomarCommand.Execute("5000");
        tela.Motivo = "pagar gelo";
        t.Foto("27-sangria");
        await tela.SalvarCommand.ExecuteAsync(null);

        Assert.Equal(3, tela.Movimentos.Count);
        Assert.Single(Directory.GetFiles(t.PastaImpressoes, "*.png"));
        Assert.Equal(0, tela.Valor.Centavos);
    }

    [AvaloniaFact]
    public void Relatorios_mostram_caixa_atual_e_anteriores()
    {
        using var t = ComMovimento();
        var tela = new RelatoriosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        Assert.NotNull(tela.Atual);
        Assert.Equal("R$ 146,00", tela.Atual!.TotalVendido);
        t.Foto("28-relatorios-caixa-atual");

        tela.AbaSelecionada = 1;
        t.Foto("29-relatorios-caixas");
        tela.AbaSelecionada = 2;
        t.Foto("30-relatorios-sangrias");
        Assert.Equal(2, tela.Movimentos.Count);
    }

    [AvaloniaFact]
    public async Task Fechamento_confere_a_gaveta_e_volta_para_abertura()
    {
        using var t = ComMovimento();
        var tela = new FechamentoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        // Abertura 100 + dinheiro 32 + suprimento 10 - sangria 20 = 122
        Assert.Equal(12200, tela.Resumo!.Resumo.DinheiroEsperado);
        tela.Contado.Centavos = 12000;
        Assert.Equal("Falta R$ 2,00", tela.Diferenca);
        t.Foto("31-fechamento");

        var fechar = tela.FecharCaixaCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel);
        t.Foto("32-fechamento-confirmar");
        ((MensagemViewModel)t.Principal.Dialogo!).SimCommand.Execute(null);
        await fechar;

        Assert.IsType<AberturaViewModel>(t.Principal.Pagina);
        Assert.Null(t.Principal.Sessao);
        Assert.Single(Directory.GetFiles(t.PastaImpressoes, "*.png"));
    }
}

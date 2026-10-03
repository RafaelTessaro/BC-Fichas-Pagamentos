using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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
        t.AbrirCaixa(10000);
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
    public void Produtos_aba_cheia_mostra_a_posicao_sem_erro_e_explica_no_novo()
    {
        // Catálogo do programa: a aba ITENS já tem os 12 botões da grade 4 x 3 (o caso das fotos do cliente)
        using var t = new TelaDeTeste(catalogoPadrao: true);
        t.AbrirCaixa();
        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        TelaDeTeste.Atualizar();
        var combo = t.Achar<ComboBox>(c => ReferenceEquals(c.ItemsSource, tela.Posicoes));

        tela.Selecionado = tela.Lista.First(p => p.Nome == "PASTEL");
        TelaDeTeste.Atualizar();
        Assert.Equal(1, tela.Posicao);
        Assert.Equal(1, combo.SelectedItem);
        Assert.False(DataValidationErrors.GetHasErrors(combo), "o campo Posição mostrou erro de conversão");
        t.Foto("20b-produtos-editar-aba-cheia");

        // Trocar de produto também mantém a seleção (antes ficava vazia quando o número era o mesmo)
        tela.Selecionado = tela.Lista.First(p => p.Nome == "MASSINHA");
        TelaDeTeste.Atualizar();
        Assert.Equal(2, combo.SelectedItem);
        Assert.False(DataValidationErrors.GetHasErrors(combo));

        tela.NovoCommand.Execute(null);
        TelaDeTeste.Atualizar();
        Assert.Null(tela.Posicao);
        Assert.Contains("está cheia", tela.AvisoPosicao);
        Assert.False(DataValidationErrors.GetHasErrors(combo));
        tela.Nome = "Milho";
        tela.Preco = "5,00";
        tela.SalvarCommand.Execute(null);
        Assert.Equal(tela.AvisoPosicao, t.Principal.Aviso);
        Assert.DoesNotContain(t.Sistema.Catalogo.Produtos(), p => p.Nome == "MILHO");
        t.Foto("20c-produtos-novo-aba-cheia");
    }

    [AvaloniaTheory]
    [InlineData(1280, 800, false)]
    [InlineData(1024, 600, false)]
    [InlineData(1280, 800, true)]
    public void Produtos_cadastro_aparece_inteiro_rolando_ate_o_fim(int largura, int altura, bool comTeclado)
    {
        using var t = new TelaDeTeste(largura, altura);
        t.AbrirCaixa();
        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.Selecionado = tela.Lista.First(p => p.Nome == "PASTEL");
        tela.ControlaEstoque = true;
        TelaDeTeste.Atualizar();

        var rolagem = t.Achar<ScrollViewer>(s => s.Content is Grid g && g.ColumnDefinitions.Count == 5);
        var estoque = t.Janela.GetVisualDescendants().OfType<TextBlock>()
            .First(b => b.Text == "Quantidade em estoque").GetVisualParent()!
            .GetVisualDescendants().OfType<TextBox>().First();
        var cores = t.Janela.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("cor")).ToList();
        Assert.Equal(16, cores.Count);
        if (comTeclado)
        {
            estoque.Focus();
            TelaDeTeste.Atualizar();
            Assert.True(t.Principal.TecladoVisivel);
        }
        else if (largura >= 1280)
        {
            // Na tela do tablet o cadastro cabe inteiro, sem precisar rolar
            Assert.True(rolagem.Extent.Height <= rolagem.Viewport.Height + 1,
                $"o cadastro precisa rolar: conteúdo {rolagem.Extent.Height:0} > visível {rolagem.Viewport.Height:0}");
        }

        rolagem.ScrollToEnd();
        TelaDeTeste.Atualizar();
        t.Foto($"20d-produtos-fim-{largura}x{altura}{(comTeclado ? "-teclado" : "")}");

        // Rolando até o fim, o campo de estoque e a última cor aparecem inteiros (antes ficavam atrás do Salvar)
        double Fundo(Control c) => c.TranslatePoint(new Point(0, c.Bounds.Height), t.Janela)!.Value.Y;
        var fimVisivel = Fundo(rolagem);
        Assert.True(Fundo(estoque) <= fimVisivel + 1, $"estoque em {Fundo(estoque):0}, visível até {fimVisivel:0}");
        Assert.True(Fundo(cores[^1]) <= fimVisivel + 1, $"última cor em {Fundo(cores[^1]):0}, visível até {fimVisivel:0}");
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
        // O total de botões por aba acompanha colunas × linhas
        Assert.Equal(9, tela.TotalGrade);
        Assert.Equal("3 colunas × 3 linhas", tela.TextoGrade);
        // Quem digitar o telefone da BC Fichas no rodapé não o vê duas vezes: ele já sai sozinho
        tela.Rodape = " bc-fichas fone: (19) 3023-9050 ";
        tela.SalvarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        Assert.Equal("Configurações salvas.", t.Principal.Aviso);
        Assert.Equal("", t.Sistema.Config.Atual.Rodape);
        Assert.Equal("", tela.Rodape);
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
    public void Fechamento_em_tela_pequena_mostra_o_botao_inteiro()
    {
        using var t = new TelaDeTeste(1024, 600);
        t.AbrirCaixa();
        var tela = new FechamentoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.Contado.Centavos = 4800;
        TelaDeTeste.Atualizar();
        t.Foto("31b-fechamento-1024x600");
        var botao = t.Achar<Button>(b => b.Command == tela.FecharCaixaCommand);
        var fundo = botao.TranslatePoint(new Point(0, botao.Bounds.Height), t.Janela)!.Value.Y;
        Assert.True(fundo <= t.Janela.Bounds.Height, $"botão termina em {fundo:0}, a tela tem {t.Janela.Bounds.Height:0}");
        Assert.DoesNotContain(botao.GetVisualAncestors(), a => a is ScrollViewer);
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
        // O botão de fechar fica sempre inteiro, fora da parte que rola
        var botaoFechar = t.Achar<Button>(b => b.Command == tela.FecharCaixaCommand);
        Assert.True(botaoFechar.TranslatePoint(new Point(0, botaoFechar.Bounds.Height), t.Janela)!.Value.Y
                    <= t.Janela.Bounds.Height);

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

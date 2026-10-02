using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>Cadastro do combo na tela de produtos, venda do combo e devolução de ficha do combo.</summary>
public class TelasDeComboTests
{
    [AvaloniaFact]
    public async Task Cadastra_combo_heineken_vende_e_devolve_uma_ficha()
    {
        using var t = new TelaDeTeste(catalogoPadrao: true);
        t.AbrirCaixa(10000);
        var heineken = t.Sistema.Catalogo.Produtos().First(p => p.Nome == "HEINEKEN");
        heineken.PrecoCentavos = 650;
        t.Sistema.Catalogo.SalvarProduto(heineken, 12);

        // Grade 4 x 3 cheia: aumenta para caber o combo
        var config = t.Sistema.Config.Atual.Clonar();
        config.Linhas = 4;
        t.Sistema.Config.Salvar(config);
        TelaDeTeste.Atualizar();

        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.Nome = "Combo Heineken";
        tela.Preco = "30,00";
        tela.Cor = "#2B8A3E";
        tela.EhCombo = true;
        Assert.True(tela.EditandoCombo); // ligar "Combo" abre a tela das fichas
        // Atalho do produto cadastrado: um toque põe a ficha (com a Qtde digitada) e liga ao estoque da Heineken
        tela.FichaQuantidade = "4";
        tela.AdicionarProdutoCommand.Execute(tela.ParaOCombo.Single(p => p.Nome == "HEINEKEN"));
        Assert.Equal("1", tela.FichaQuantidade);
        tela.AdicionarProdutoCommand.Execute(tela.ParaOCombo.Single(p => p.Nome == "HEINEKEN"));
        var linha = Assert.Single(tela.Componentes);
        Assert.Equal(650, linha.ValorCentavos);
        Assert.True(linha.Ligado);
        Assert.Equal(5, linha.Quantidade);
        Assert.Equal("R$ 32,50", tela.TotalFichas);
        Assert.Equal("Preço do combo: R$ 30,00 — R$ 2,50 de desconto", tela.ConfereCombo);
        TelaDeTeste.Atualizar();
        t.Foto("40-produtos-combo");
        tela.SalvarComboCommand.Execute(null);
        Assert.Equal("COMBO HEINEKEN salvo.", t.Principal.Aviso);
        Assert.False(tela.EditandoCombo);

        var combo = t.Sistema.Catalogo.Produtos().Single(p => p.Nome == "COMBO HEINEKEN");
        Assert.Equal(5, Assert.Single(combo.Componentes).Quantidade);
        Assert.Equal(650, combo.Componentes[0].ValorCentavos);
        Assert.Equal(heineken.Id, combo.Componentes[0].ProdutoId);

        // Na venda o botão diz que é combo e quantas fichas saem
        t.Principal.IrParaVenda();
        TelaDeTeste.Atualizar();
        var botao = t.Venda.Botoes.Single(b => b.Nome == "COMBO HEINEKEN");
        Assert.Equal("COMBO • 5 FICHAS", botao.Detalhe);
        t.Tocar("COMBO HEINEKEN");
        t.Venda.PagarCommand.Execute(null);
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        Assert.Equal("1 item(ns) • 5 ficha(s)", pagamento.ResumoItens);
        pagamento.EscolherDinheiroCommand.Execute(null);
        pagamento.ValorExatoCommand.Execute(null);
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        Assert.Equal(5, t.EsperarImpressoes(5).Length);
        pagamento.FecharCommand.Execute(null);

        var resumo = t.Sistema.Caixa.Resumo(t.Principal.Sessao!.Id);
        Assert.Equal("COMBO HEINEKEN", Assert.Single(resumo.Produtos).Nome);

        // Devolução: o cliente devolve 1 das 5 Heineken do combo
        var pedido = t.Sistema.Vendas.Pedidos(t.Principal.Sessao.Id).Single();
        var devolucao = new DevolucaoViewModel(t.Principal);
        t.Principal.Abrir(devolucao);
        devolucao.Busca = pedido.Numero.ToString();
        devolucao.BuscarCommand.Execute(null);
        var item = Assert.Single(devolucao.Itens);
        Assert.Equal("HEINEKEN", item.Nome);
        Assert.Equal("Ficha do COMBO HEINEKEN", item.Combo);
        item.MaisCommand.Execute(null);
        Assert.Equal("R$ 6,00", devolucao.TotalTexto);
        t.Principal.Aviso = null;
        TelaDeTeste.Atualizar();
        t.Foto("41-devolucao-combo");
        await devolucao.RegistrarCommand.ExecuteAsync(null);
        Assert.Equal(600, t.Sistema.Caixa.Resumo(t.Principal.Sessao.Id).TotalDevolvido);
    }

    [AvaloniaFact]
    public void Monta_combo_de_vales_digitando_como_no_sistema_antigo()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.Nome = "Combo R$ 100,00";
        tela.Aba = tela.Abas[2];
        tela.AbrirComboCommand.Execute(null);

        void Ficha(string nome, string quantidade, string valor)
        {
            tela.FichaNome = nome;
            tela.FichaQuantidade = quantidade;
            tela.FichaValor = valor;
            tela.AdicionarFichaCommand.Execute(null);
        }
        tela.FichaDetalhe = "val. 05/10/26";
        Ficha("vale r$ 10,00", "5", "10");
        // O detalhe continua preenchido para a próxima ficha; o resto limpa
        Assert.Equal("val. 05/10/26", tela.FichaDetalhe);
        Assert.Equal("", tela.FichaNome);
        Ficha("VALE R$ 5,00", "4", "5");
        Ficha("VALE R$ 5,00", "2", "5,00");   // a mesma ficha de novo: soma (fica 6)
        tela.FichaQuantidade = "5";
        tela.AdicionarValeCommand.Execute(200L);   // atalho: 5 vales de R$ 2,00 com a validade digitada
        Assert.Equal("VALE R$ 2,00", tela.Componentes[^1].Nome);
        Assert.Equal("VAL. 05/10/26", tela.Componentes[^1].Detalhe);
        Ficha("VALE R$ 1,00", "10", "1");
        tela.AdicionarValeCommand.Execute(100L);   // mais um vale de R$ 1,00 pelo atalho: soma na mesma linha
        Assert.Equal(["VALE R$ 10,00", "VALE R$ 5,00", "VALE R$ 2,00", "VALE R$ 1,00"], tela.Componentes.Select(c => c.Nome));
        Assert.Equal(6, tela.Componentes[1].Quantidade);
        Assert.Equal("R$ 101,00", tela.TotalFichas);

        // Tirar uma unidade (−) e tirar todas (lixeira)
        tela.Componentes[3].MenosCommand.Execute(null);
        Assert.Equal(10, tela.Componentes[3].Quantidade);
        Assert.Equal("R$ 100,00", tela.TotalFichas);
        Assert.Equal("26 fichas", tela.QuantasFichas);
        tela.UsarTotalComoPrecoCommand.Execute(null);
        Assert.Equal("Preço do combo: R$ 100,00 — igual às fichas", tela.ConfereCombo);
        Assert.True(tela.ComboConfere);
        Ficha("VALE R$ 20,00", "1", "20");
        tela.Componentes[^1].RemoverCommand.Execute(null);
        Assert.Equal(4, tela.Componentes.Count);
        TelaDeTeste.Atualizar();
        t.Foto("40b-produtos-combo-vales");

        tela.SalvarComboCommand.Execute(null);
        var combo = t.Sistema.Catalogo.Produtos().Single(p => p.Nome == "COMBO R$ 100,00");
        Assert.Equal(10000, combo.PrecoCentavos);
        Assert.Equal(26, combo.FichasPorVenda);
        Assert.All(combo.Componentes, c => Assert.Equal("VAL. 05/10/26", c.Detalhe));
        Assert.Contains("COMBO (26 fichas)", tela.Lista.Single(p => p.Nome == "COMBO R$ 100,00").Info);

        // Abrindo o combo de novo, as fichas voltam para a lista
        tela.Selecionado = tela.Lista.Single(p => p.Nome == "COMBO R$ 100,00");
        Assert.True(tela.EhCombo);
        Assert.False(tela.EditandoCombo);
        Assert.Equal(4, tela.Componentes.Count);
        Assert.StartsWith("26 fichas • as fichas somam R$ 100,00", tela.ResumoCombo);
    }

    [AvaloniaFact]
    public void Previa_do_botao_mostra_a_foto_e_a_cor_escolhida()
    {
        using var t = new TelaDeTeste(catalogoPadrao: true);
        var origem = Path.Combine(t.Sistema.PastaDados, "foto.png");
        using (var bmp = new SkiaSharp.SKBitmap(300, 300))
        {
            using var c = new SkiaSharp.SKCanvas(bmp);
            c.Clear(SkiaSharp.SKColors.OrangeRed);
            BCFichas.Core.Impressao.ImagemUtil.SalvarPng(bmp, origem);
        }
        t.Principal.EscolherImagem = (_, _) => Task.FromResult<string?>(origem);
        t.AbrirCaixa();
        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);

        tela.Selecionado = tela.Lista.Single(p => p.Nome == "PASTEL");
        tela.EscolherImagemCommand.Execute(null);
        Assert.True(tela.TemImagem);
        Assert.NotNull(tela.ImagemPrevia);
        tela.Cor = "#C92A2A";
        Assert.True(tela.Cores.Single(c => c.Hex == "#C92A2A").Marcada);
        tela.SalvarCommand.Execute(null);

        // Abrindo o produto de novo, a prévia continua com a foto e a cor
        tela.NovoCommand.Execute(null);
        Assert.Null(tela.ImagemPrevia);
        tela.Selecionado = tela.Lista.Single(p => p.Nome == "PASTEL");
        Assert.NotNull(tela.ImagemPrevia);
        Assert.True(tela.Cores.Single(c => c.Hex == "#C92A2A").Marcada);
        TelaDeTeste.Atualizar();
        Assert.Contains(t.Janela.GetVisualDescendants().OfType<Image>(), i => i.IsEffectivelyVisible && i.Source is not null);
    }

    [AvaloniaFact]
    public void Produto_fora_da_tela_de_venda_nao_precisa_de_posicao()
    {
        using var t = new TelaDeTeste(catalogoPadrao: true);
        t.AbrirCaixa();
        var tela = new ProdutosViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.Nome = "Vale R$ 2,00";
        tela.Preco = "2,00";
        tela.Ativo = false;
        // A aba ITENS está cheia (12 botões), mas o vale não vai para a grade
        Assert.Null(tela.Posicao);
        tela.SalvarCommand.Execute(null);
        Assert.Equal("VALE R$ 2,00 salvo.", t.Principal.Aviso);
        var vale = t.Sistema.Catalogo.Produtos().Single(p => p.Nome == "VALE R$ 2,00");
        Assert.False(vale.Ativo);
        Assert.Contains(tela.Lista, p => p.Nome == "VALE R$ 2,00" && p.Info.Contains("fora da tela de venda"));
    }
}

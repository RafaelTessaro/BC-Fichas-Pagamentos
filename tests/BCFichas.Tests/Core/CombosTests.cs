using BCFichas.Core;
using BCFichas.Core.Dados;
using BCFichas.Core.Impressao;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>
/// Combos: um preço só e as fichas de outros produtos. Ex.: COMBO HEINEKEN (R$ 30) imprime 5 fichas de HEINEKEN
/// de R$ 6,50; COMBO R$ 100 imprime vales de vários valores. No relatório aparece o combo.
/// </summary>
public class CombosTests : IDisposable
{
    private readonly SistemaTemporario _t = new();
    private Sistema S => _t.Sistema;

    public void Dispose() => _t.Dispose();

    private Aba Aba => S.Catalogo.Abas()[0];

    private Produto NovoProduto(string nome, long preco, bool naTela = true, int? estoque = null)
    {
        var posicao = naTela ? S.Catalogo.PosicoesLivres(Aba.Id, 36).First() : 0;
        return S.Catalogo.SalvarProduto(new Produto
        {
            Nome = nome, AbaId = Aba.Id, Posicao = posicao, PrecoCentavos = preco, Ativo = naTela,
            ControlaEstoque = estoque is not null, Estoque = estoque ?? 0,
        }, 36);
    }

    private Produto NovoCombo(string nome, long preco, params (Produto Produto, int Quantidade, long Valor)[] fichas) =>
        S.Catalogo.SalvarProduto(new Produto
        {
            Nome = nome, AbaId = Aba.Id, Posicao = S.Catalogo.PosicoesLivres(Aba.Id, 36).First(), PrecoCentavos = preco,
            Componentes = fichas.Select(f => new ComponenteCombo
                { ProdutoId = f.Produto.Id, Quantidade = f.Quantidade, ValorCentavos = f.Valor }).ToList(),
        }, 36);

    private Pedido Vender(SessaoCaixa sessao, params (Produto Produto, int Quantidade)[] itens)
    {
        var carrinho = new Carrinho();
        foreach (var (produto, quantidade) in itens)
            Assert.True(carrinho.Adicionar(S.Catalogo.Produto(produto.Id)!, quantidade));
        return S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 100000);
    }

    [Fact]
    public void Combo_heineken_imprime_as_fichas_da_heineken_e_o_relatorio_mostra_o_combo()
    {
        var heineken = NovoProduto("Heineken lata", 650, estoque: 20);
        var combo = NovoCombo("Combo Heineken", 3000, (heineken, 5, 650));
        Assert.True(S.Catalogo.Produto(combo.Id)!.EhCombo);
        Assert.Equal(5, S.Catalogo.Produto(combo.Id)!.FichasPorVenda);
        Assert.Equal(3250, S.Catalogo.Produto(combo.Id)!.ValorDasFichas);

        var sessao = S.Caixa.Abrir(1, null, 0);
        var pedido = Vender(sessao, (combo, 1), (heineken, 1));
        Assert.Equal(3650, pedido.TotalCentavos);
        Assert.Equal(6, pedido.QuantidadeFichas);

        var fichas = GeradorFichas.Gerar(S.Vendas.Pedido(pedido.Id)!, S.Config.Atual);
        Assert.Equal(6, fichas.Count);
        Assert.All(fichas, f => Assert.Equal("HEINEKEN LATA", f.Produto));
        Assert.All(fichas.Take(5), f => Assert.Equal(650, f.PrecoCentavos));
        Assert.All(fichas.Take(5), f => Assert.Equal("COMBO HEINEKEN", f.Detalhe));
        Assert.Equal(Enumerable.Range(1, 6), fichas.Select(f => f.Sequencia));
        Assert.All(fichas, f => Assert.Equal(6, f.TotalFichas));

        // Relatório: um combo vendido (R$ 30) e uma Heineken avulsa; 6 fichas saíram
        var resumo = S.Caixa.Resumo(sessao.Id);
        Assert.Contains(resumo.Produtos, p => p is { Nome: "COMBO HEINEKEN", Quantidade: 1, TotalCentavos: 3000 });
        Assert.Contains(resumo.Produtos, p => p is { Nome: "HEINEKEN LATA", Quantidade: 1, TotalCentavos: 650 });
        Assert.Equal(6, resumo.QuantidadeFichas);
        Assert.Equal(3650, resumo.TotalVendas);
        // O estoque da Heineken baixa 6 (5 do combo + 1 avulsa)
        Assert.Equal(14, S.Catalogo.Produto(heineken.Id)!.Estoque);
    }

    [Fact]
    public void Combo_de_vales_soma_o_valor_do_combo_e_os_vales_ficam_fora_da_tela()
    {
        var vale2 = NovoProduto("Vale R$ 2,00", 200, naTela: false);
        var vale5 = NovoProduto("Vale R$ 5,00", 500, naTela: false);
        var vale10 = NovoProduto("Vale R$ 10,00", 1000, naTela: false);
        Assert.Equal(0, vale2.Posicao);   // escondido não ocupa lugar na grade
        Assert.DoesNotContain(S.Catalogo.ProdutosDaAba(Aba.Id), p => p.Id == vale2.Id);
        var combo = NovoCombo("Combo R$ 100,00", 10000, (vale2, 5, 200), (vale5, 2, 500), (vale10, 8, 1000));

        var sessao = S.Caixa.Abrir(1, null, 0);
        var pedido = Vender(sessao, (combo, 1));
        var fichas = GeradorFichas.Gerar(pedido, S.Config.Atual);

        Assert.Equal(15, fichas.Count);
        Assert.Equal(10000, fichas.Sum(f => f.PrecoCentavos));
        Assert.Equal(["VALE R$ 2,00", "VALE R$ 5,00", "VALE R$ 10,00"], fichas.Select(f => f.Produto).Distinct());
        var produto = Assert.Single(S.Caixa.Resumo(sessao.Id).Produtos);
        Assert.Equal("COMBO R$ 100,00", produto.Nome);
    }

    [Fact]
    public void Combo_confere_o_estoque_dos_produtos_das_fichas()
    {
        var heineken = NovoProduto("Heineken", 650, estoque: 7);
        var combo = NovoCombo("Combo Heineken", 3000, (heineken, 5, 650));
        var sessao = S.Caixa.Abrir(1, null, 0);

        // No balcão: o combo (5) mais 2 avulsas cabem; a terceira avulsa não
        var carrinho = new Carrinho();
        Assert.True(carrinho.Adicionar(S.Catalogo.Produto(combo.Id)!));
        Assert.True(carrinho.Adicionar(S.Catalogo.Produto(heineken.Id)!, 2));
        Assert.False(carrinho.Adicionar(S.Catalogo.Produto(heineken.Id)!));
        Assert.False(carrinho.Adicionar(S.Catalogo.Produto(combo.Id)!));

        Vender(sessao, (heineken, 3));
        Assert.True(S.Catalogo.Produto(combo.Id)!.Esgotado); // só restam 4: não dá um combo de 5
        var semEstoque = new List<LinhaCarrinho> { new() { Produto = S.Catalogo.Produto(combo.Id)!, Quantidade = 1 } };
        var erro = Assert.Throws<ErroDeNegocio>(() => S.Vendas.CriarPedido(sessao, semEstoque, FormaPagamento.Dinheiro, 5000));
        Assert.Equal("Só restam 4 de HEINEKEN.", erro.Message);
    }

    [Fact]
    public void Devolucao_de_ficha_do_combo_devolve_a_parte_do_preco_do_combo()
    {
        var heineken = NovoProduto("Heineken", 650, estoque: 20);
        var combo = NovoCombo("Combo Heineken", 3000, (heineken, 5, 650));
        var sessao = S.Caixa.Abrir(1, null, 5000);
        var pedido = S.Vendas.Pedido(Vender(sessao, (combo, 1)).Id)!;
        var linha = Assert.Single(FichasDoPedido.Linhas(pedido));
        Assert.Equal(5, linha.PodeDevolver);
        Assert.Equal(600, linha.ValorUnitario); // R$ 30 / 5 fichas

        var devolucao = S.Devolucoes.Devolver(sessao, pedido.Id,
            [(linha.Item.Id, linha.Componente!.Id, 2)], "sobrou");
        Assert.Equal(1200, devolucao.ValorCentavos);
        Assert.Equal("HEINEKEN (COMBO HEINEKEN)", Assert.Single(devolucao.Itens).Nome);
        Assert.Equal(17, S.Catalogo.Produto(heineken.Id)!.Estoque); // 20 - 5 + 2

        // Reimpressão: só as 3 fichas que ficaram com o cliente
        var fichas = GeradorFichas.Gerar(S.Vendas.Pedido(pedido.Id)!, S.Config.Atual, reimpressao: true);
        Assert.Equal([1, 2, 3], fichas.Select(f => f.Sequencia));

        var resumo = S.Caixa.Resumo(sessao.Id);
        Assert.Equal(1800, resumo.VendaLiquida);
        Assert.Equal(5000 + 3000 - 1200, resumo.DinheiroEsperado);
        Assert.Equal("HEINEKEN (COMBO HEINEKEN)", Assert.Single(resumo.ProdutosDevolvidos).Nome);
    }

    [Fact]
    public void Devolvendo_todas_as_fichas_o_cliente_recebe_o_preco_do_combo_certinho()
    {
        // 7 fichas para R$ 30,00 não dá conta redonda: R$ 4,28... cada; no fim tem de dar R$ 30,00
        var heineken = NovoProduto("Heineken", 650);
        var agua = NovoProduto("Agua", 400);
        var combo = NovoCombo("Combo", 3000, (heineken, 4, 650), (agua, 3, 400));
        var sessao = S.Caixa.Abrir(1, null, 10000);
        var pedido = S.Vendas.Pedido(Vender(sessao, (combo, 2)).Id)!;
        var linhas = FichasDoPedido.Linhas(pedido);
        Assert.Equal([8, 6], linhas.Select(l => l.Total));

        long devolvido = 0;
        foreach (var linha in linhas)
            for (var i = 0; i < linha.Total; i++)
                devolvido += S.Devolucoes.Devolver(sessao, pedido.Id, [(linha.Item.Id, linha.Componente!.Id, 1)], null).ValorCentavos;

        Assert.Equal(6000, devolvido);
        Assert.Empty(GeradorFichas.Gerar(S.Vendas.Pedido(pedido.Id)!, S.Config.Atual, reimpressao: true));
    }

    [Fact]
    public void Codigo_de_barras_acha_a_ficha_certa_do_combo()
    {
        var heineken = NovoProduto("Heineken", 650);
        var agua = NovoProduto("Agua", 400);
        var combo = NovoCombo("Combo", 3000, (heineken, 2, 650), (agua, 1, 400));
        var pastel = S.Catalogo.Produtos().First(p => p.Nome == "PASTEL");
        var pedido = S.Vendas.Pedido(Vender(S.Caixa.Abrir(1, null, 0), (pastel, 1), (combo, 2)).Id)!;
        // Fichas: 1 pastel, 2-5 heineken (2 combos x 2), 6-7 água
        Assert.Equal("PASTEL", FichasDoPedido.LinhaDaFicha(pedido, 1)!.Nome);
        Assert.Equal("HEINEKEN", FichasDoPedido.LinhaDaFicha(pedido, 5)!.Nome);
        Assert.Equal("AGUA", FichasDoPedido.LinhaDaFicha(pedido, 6)!.Nome);
        Assert.Null(FichasDoPedido.LinhaDaFicha(pedido, 8));
        Assert.Equal(["PASTEL", "HEINEKEN", "HEINEKEN", "HEINEKEN", "HEINEKEN", "AGUA", "AGUA"],
            GeradorFichas.Gerar(pedido, S.Config.Atual).Select(f => f.Produto));
    }

    [Fact]
    public void Venda_guarda_o_combo_como_era_na_hora()
    {
        var heineken = NovoProduto("Heineken", 650);
        var combo = NovoCombo("Combo Heineken", 3000, (heineken, 5, 650));
        var pedido = Vender(S.Caixa.Abrir(1, null, 0), (combo, 1));

        // Depois da venda o combo muda: a reimpressão continua com as 5 fichas de R$ 6,50
        combo = S.Catalogo.Produto(combo.Id)!;
        combo.Componentes[0].Quantidade = 3;
        combo.Componentes[0].ValorCentavos = 1000;
        S.Catalogo.SalvarProduto(combo, 36);
        var fichas = GeradorFichas.Gerar(S.Vendas.Pedido(pedido.Id)!, S.Config.Atual, reimpressao: true);
        Assert.Equal(5, fichas.Count);
        Assert.All(fichas, f => Assert.Equal(650, f.PrecoCentavos));
    }

    [Fact]
    public void Regras_do_combo()
    {
        var heineken = NovoProduto("Heineken", 650);
        var combo = NovoCombo("Combo Heineken", 3000, (heineken, 5, 650));

        var dentro = Assert.Throws<ErroDeNegocio>(() => NovoCombo("Combo duplo", 5000, (combo, 2, 3000)));
        Assert.Contains("um combo não pode ter outro combo dentro", dentro.Message);
        var vira = S.Catalogo.Produto(heineken.Id)!;
        vira.Componentes = [new ComponenteCombo { ProdutoId = combo.Id, Quantidade = 1, ValorCentavos = 0 }];
        Assert.Throws<ErroDeNegocio>(() => S.Catalogo.SalvarProduto(vira, 36));
        var proprio = S.Catalogo.Produto(combo.Id)!;
        proprio.Componentes.Add(new ComponenteCombo { ProdutoId = combo.Id, Quantidade = 1 });
        Assert.Throws<ErroDeNegocio>(() => S.Catalogo.SalvarProduto(proprio, 36));
        Assert.Throws<ErroDeNegocio>(() => NovoCombo("Muitas", 100, (heineken, 51, 1)));

        var excluir = Assert.Throws<ErroDeNegocio>(() => S.Catalogo.ExcluirProduto(heineken.Id));
        Assert.Contains("COMBO HEINEKEN", excluir.Message);
        Assert.Equal(["COMBO HEINEKEN"], S.Catalogo.CombosQueUsam(heineken.Id));

        // Excluindo o combo, a Heineken fica livre
        S.Catalogo.ExcluirProduto(combo.Id);
        S.Catalogo.ExcluirProduto(heineken.Id);
        Assert.Null(S.Catalogo.Produto(heineken.Id));
    }

    [Fact]
    public void Ficha_de_combo_mostra_o_nome_do_combo_embaixo()
    {
        var config = S.Config.Atual.Clonar();
        config.Modelo = ModeloFicha.Classico4;
        Ficha Ficha(string detalhe) => new()
        {
            NomeEvento = "FESTA", Produto = "HEINEKEN", Detalhe = detalhe, PrecoCentavos = 650, NumeroPedido = 1,
            Caixa = 1, Data = DateTime.Now, Sequencia = 1, TotalFichas = 5,
        };
        using var avulsa = RenderizadorFicha.Renderizar(Ficha(""), config, null);
        using var doCombo = RenderizadorFicha.Renderizar(Ficha("COMBO HEINEKEN"), config, null);
        Assert.True(doCombo.Height > avulsa.Height);
    }

    [Fact]
    public void Banco_da_versao_32_ganha_os_combos_e_mantem_as_devolucoes()
    {
        var sessao = S.Caixa.Abrir(1, null, 5000);
        var pastel = S.Catalogo.Produtos().First(p => p.Nome == "PASTEL");
        var pedido = Vender(sessao, (pastel, 2));
        S.Devolucoes.Devolver(sessao, pedido.Id, new Dictionary<long, int> { [pedido.Itens[0].Id] = 1 }, null);
        // Volta o banco para a versão 2 (como o da versão 3.2), com a devolução gravada do jeito antigo
        S.Banco.Executar("""
            DROP TABLE componentes_combo; DROP TABLE componentes_item; DROP INDEX ix_itens_devolucao_componente;
            ALTER TABLE itens_devolucao DROP COLUMN componente_id; ALTER TABLE itens_devolucao DROP COLUMN valor;
            DELETE FROM versao WHERE v = 3;
            """);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var banco = new Banco(S.Banco.Caminho);
        Assert.Equal(3, banco.Escalar<long>("SELECT MAX(v) FROM versao"));
        Assert.Equal(1000, banco.Escalar<long>("SELECT valor FROM itens_devolucao"));
        var sistema = Sistema.Iniciar(_t.Pasta, criarExemplos: false);
        Assert.Equal(1000, sistema.Caixa.Resumo(sessao.Id).TotalDevolvido);
        Assert.Equal(1, sistema.Vendas.Pedido(pedido.Id)!.Itens[0].Devolvidas);
    }
}

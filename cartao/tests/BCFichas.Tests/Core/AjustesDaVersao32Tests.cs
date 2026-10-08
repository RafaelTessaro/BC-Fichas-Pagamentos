using BCFichas.Core;
using BCFichas.Core.Dados;
using BCFichas.Core.Impressao;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Servicos;
using BCFichas.Core.Vendas;
using SkiaSharp;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>Devolução de fichas, modo teste, maquininha separada, abertura impressa e ajustes da ficha.</summary>
public class AjustesDaVersao32Tests : IDisposable
{
    private readonly SistemaTemporario _t = new();
    private Sistema S => _t.Sistema;

    public void Dispose() => _t.Dispose();

    private Produto P(string nome) => S.Catalogo.Produtos().First(p => p.Nome == nome);

    private Pedido Vender(SessaoCaixa sessao, FormaPagamento forma, params (string Nome, int Qtd)[] itens)
    {
        var carrinho = new Carrinho();
        foreach (var (nome, qtd) in itens) carrinho.Adicionar(P(nome), qtd);
        var pedido = S.Vendas.CriarPedido(sessao, carrinho.Linhas, forma, forma == FormaPagamento.Dinheiro ? 100000 : 0);
        return forma == FormaPagamento.Dinheiro ? pedido : S.Vendas.ConfirmarPagamento(pedido.Id, null);
    }

    private static Dictionary<long, int> Itens(Pedido pedido, params (string Nome, int Qtd)[] itens) =>
        itens.ToDictionary(i => pedido.Itens.First(x => x.Nome == i.Nome).Id, i => i.Qtd);

    // ---------- Devolução de fichas ----------

    [Fact]
    public void Devolucao_em_dinheiro_tira_da_gaveta_e_nao_cancela_a_venda()
    {
        var sessao = S.Caixa.Abrir(1, null, 5000);
        // Cachorro-quente R$ 10 e pastel R$ 10: o cliente não usou o pastel
        var pedido = Vender(sessao, FormaPagamento.Dinheiro, ("CACHORRO-QUENTE", 1), ("PASTEL", 1));

        var devolucao = S.Devolucoes.Devolver(sessao, pedido.Id, Itens(pedido, ("PASTEL", 1)), "não usou");

        Assert.Equal(1000, devolucao.ValorCentavos);
        Assert.True(devolucao.EmDinheiro);
        Assert.Equal("NÃO USOU", devolucao.Motivo);
        var lido = S.Vendas.Pedido(pedido.Id)!;
        Assert.Equal(StatusPedido.Pago, lido.Status);
        Assert.Equal(1, lido.Itens.First(i => i.Nome == "PASTEL").Devolvidas);

        var resumo = S.Caixa.Resumo(sessao.Id);
        Assert.Equal(2000, resumo.TotalVendas);           // a venda continua registrada
        Assert.Equal(1000, resumo.TotalDevolvido);
        Assert.Equal(1000, resumo.VendaLiquida);
        Assert.Equal(5000 + 2000 - 1000, resumo.DinheiroEsperado);
        Assert.Equal(1, resumo.QuantidadeDevolucoes);
        Assert.Equal("PASTEL", Assert.Single(resumo.ProdutosDevolvidos).Nome);
    }

    [Fact]
    public void Devolucao_no_pix_ou_cartao_e_so_registro_o_estorno_e_na_maquininha()
    {
        var sessao = S.Caixa.Abrir(1, null, 5000);
        var pix = Vender(sessao, FormaPagamento.Pix, ("PASTEL", 2));
        var credito = Vender(sessao, FormaPagamento.Credito, ("CERVEJA", 1));

        S.Devolucoes.Devolver(sessao, pix.Id, Itens(pix, ("PASTEL", 1)), null);
        S.Devolucoes.Devolver(sessao, credito.Id, Itens(credito, ("CERVEJA", 1)), null);

        var resumo = S.Caixa.Resumo(sessao.Id);
        Assert.Equal(5000, resumo.DinheiroEsperado);   // a gaveta não muda
        Assert.Equal(1000, resumo.Devolvido(FormaPagamento.Pix));
        Assert.Equal(800, resumo.Devolvido(FormaPagamento.Credito));
        Assert.Equal(2800 - 1800, resumo.VendaLiquida);
    }

    [Fact]
    public void Nao_devolve_mais_do_que_foi_vendido_nem_duas_vezes()
    {
        var sessao = S.Caixa.Abrir(1, null, 5000);
        var pedido = Vender(sessao, FormaPagamento.Dinheiro, ("PASTEL", 2));

        var demais = Assert.Throws<ErroDeNegocio>(() => S.Devolucoes.Devolver(sessao, pedido.Id, Itens(pedido, ("PASTEL", 3)), null));
        Assert.Equal("PASTEL: só dá para devolver 2.", demais.Message);

        S.Devolucoes.Devolver(sessao, pedido.Id, Itens(pedido, ("PASTEL", 2)), null);
        var deNovo = Assert.Throws<ErroDeNegocio>(() => S.Devolucoes.Devolver(sessao, pedido.Id, Itens(pedido, ("PASTEL", 1)), null));
        Assert.Equal("PASTEL: todas as fichas já foram devolvidas.", deNovo.Message);
        Assert.Throws<ErroDeNegocio>(() => S.Devolucoes.Devolver(sessao, pedido.Id, new Dictionary<long, int>(), null));
    }

    [Fact]
    public void Devolucao_em_dinheiro_nao_passa_do_que_tem_na_gaveta()
    {
        var sessao = S.Caixa.Abrir(1, null, 0);
        var pedido = Vender(sessao, FormaPagamento.Dinheiro, ("PASTEL", 1));
        S.Caixa.RegistrarMovimento(sessao, TipoMovimento.Sangria, 1000, "cofre");

        var erro = Assert.Throws<ErroDeNegocio>(() => S.Devolucoes.Devolver(sessao, pedido.Id, Itens(pedido, ("PASTEL", 1)), null));
        Assert.Contains("No caixa há só R$ 0,00", erro.Message);
    }

    [Fact]
    public void Ficha_devolvida_volta_para_o_estoque_e_nao_e_reimpressa()
    {
        var bolo = P("BOLO");
        bolo.ControlaEstoque = true;
        bolo.Estoque = 10;
        S.Catalogo.SalvarProduto(bolo, 12);
        var sessao = S.Caixa.Abrir(1, null, 5000);
        var pedido = Vender(sessao, FormaPagamento.Dinheiro, ("BOLO", 3), ("PASTEL", 1));
        Assert.Equal(7, P("BOLO").Estoque);

        S.Devolucoes.Devolver(sessao, pedido.Id, Itens(pedido, ("BOLO", 2)), null);
        Assert.Equal(9, P("BOLO").Estoque);

        // Reimpressão: só o bolo que ficou e o pastel; a numeração "x/4" não muda
        var fichas = GeradorFichas.Gerar(S.Vendas.Pedido(pedido.Id)!, S.Config.Atual, reimpressao: true);
        Assert.Equal(["BOLO", "PASTEL"], fichas.Select(f => f.Produto));
        Assert.Equal([1, 4], fichas.Select(f => f.Sequencia));
        Assert.All(fichas, f => Assert.Equal(4, f.TotalFichas));
    }

    [Fact]
    public void Devolucao_de_pedido_de_outro_dia_sai_do_caixa_de_hoje()
    {
        var ontem = S.Caixa.Abrir(1, null, 5000);
        var pedido = Vender(ontem, FormaPagamento.Dinheiro, ("PASTEL", 1));
        S.Caixa.Fechar(ontem, null);

        var hoje = S.Caixa.Abrir(1, null, 3000);
        Assert.Equal(pedido.Id, S.Vendas.PedidoPorNumero(pedido.Numero)!.Id);
        S.Devolucoes.Devolver(hoje, pedido.Id, Itens(pedido, ("PASTEL", 1)), null);

        Assert.Equal(1000, S.Caixa.Resumo(ontem.Id).TotalVendas);
        Assert.Equal(0, S.Caixa.Resumo(ontem.Id).TotalDevolvido);
        Assert.Equal(2000, S.Caixa.Resumo(hoje.Id).DinheiroEsperado);
        Assert.Equal(1000, S.Caixa.Resumo(hoje.Id).TotalDevolvido);
        var feita = Assert.Single(S.Devolucoes.Devolucoes(hoje.Id));
        Assert.Equal(pedido.Numero, feita.NumeroPedido);
        Assert.Equal("PASTEL", Assert.Single(feita.Itens).Nome);
    }

    [Fact]
    public void Fechamento_mostra_as_devolucoes_e_a_venda_liquida()
    {
        var sessao = S.Caixa.Abrir(1, null, 5000);
        var pedido = Vender(sessao, FormaPagamento.Dinheiro, ("PASTEL", 2));
        var devolucao = S.Devolucoes.Devolver(sessao, pedido.Id, Itens(pedido, ("PASTEL", 1)), null);
        var resumo = S.Caixa.Fechar(sessao, 6000);

        using var comDevolucao = Relatorios.Fechamento(resumo, S.Config.Atual).Renderizar(576);
        using var semDevolucao = Relatorios.Fechamento(S.Caixa.Resumo(S.Caixa.Abrir(2, null, 0).Id), S.Config.Atual)
            .Renderizar(576);
        Assert.True(comDevolucao.Height > semDevolucao.Height + 100);

        using var comprovante = Relatorios.Devolucao(devolucao, S.Vendas.Pedido(pedido.Id)!, S.Config.Atual, 6000)
            .Renderizar(576);
        Assert.Equal(576, comprovante.Width);
    }

    // ---------- Modo teste ----------

    [Fact]
    public void Modo_teste_tem_caixa_e_numeracao_separados_e_nao_mexe_no_estoque()
    {
        var bolo = P("BOLO");
        bolo.ControlaEstoque = true;
        bolo.Estoque = 5;
        S.Catalogo.SalvarProduto(bolo, 12);

        var real = S.Caixa.Abrir(1, null, 5000);
        var vendaReal = Vender(real, FormaPagamento.Dinheiro, ("PASTEL", 1));
        var teste = S.Caixa.Abrir(1, null, 0, teste: true);
        Assert.True(teste.Teste);
        Assert.Equal(real.Id, S.Caixa.SessaoAberta(1)!.Id);
        Assert.Equal(teste.Id, S.Caixa.SessaoTesteAberta(1)!.Id);

        var vendaTeste = Vender(teste, FormaPagamento.Pix, ("BOLO", 2));
        Assert.True(vendaTeste.Teste);
        Assert.Equal(1, vendaTeste.Numero);                 // numeração própria
        Assert.Equal(5, P("BOLO").Estoque);                 // estoque intacto
        Assert.All(GeradorFichas.Gerar(vendaTeste, S.Config.Atual), f => Assert.True(f.Teste));
        Assert.DoesNotContain(GeradorFichas.Gerar(vendaReal, S.Config.Atual), f => f.Teste);

        // O próximo pedido de verdade continua a numeração de verdade
        Assert.Equal(vendaReal.Numero + 1, Vender(real, FormaPagamento.Dinheiro, ("PASTEL", 1)).Numero);
        // Achar pelo número separa teste e verdade (os dois têm o pedido 1)
        Assert.False(S.Vendas.PedidoPorNumero(1)!.Teste);
        Assert.True(S.Vendas.PedidoPorNumero(1, teste: true)!.Teste);
    }

    [Fact]
    public void Vendas_de_teste_nao_aparecem_nos_relatorios_e_sao_apagadas_no_fim()
    {
        var real = S.Caixa.Abrir(1, null, 5000);
        Vender(real, FormaPagamento.Dinheiro, ("PASTEL", 1));
        var teste = S.Caixa.Abrir(1, null, 0, teste: true);
        var vendaTeste = Vender(teste, FormaPagamento.Dinheiro, ("PASTEL", 3));
        S.Caixa.RegistrarMovimento(teste, TipoMovimento.Suprimento, 500, "teste");
        S.Devolucoes.Devolver(teste, vendaTeste.Id, Itens(vendaTeste, ("PASTEL", 1)), null);

        // Uma venda de verdade não pode ser devolvida no modo teste (e vice-versa)
        var pedidoReal = S.Vendas.Pedidos(real.Id).Single();
        Assert.Throws<ErroDeNegocio>(() => S.Devolucoes.Devolver(teste, pedidoReal.Id, Itens(S.Vendas.Pedido(pedidoReal.Id)!, ("PASTEL", 1)), null));

        var hoje = DateTime.Today;
        Assert.Equal([real.Id], S.Caixa.Sessoes(hoje, hoje).Select(s => s.Id));
        Assert.Empty(S.Caixa.Movimentos(hoje, hoje));

        S.Caixa.ApagarTestes();

        Assert.Null(S.Caixa.SessaoTesteAberta(1));
        Assert.Null(S.Caixa.Sessao(teste.Id));
        Assert.Equal(0, S.Banco.Escalar<long>("SELECT COUNT(*) FROM pedidos WHERE teste = 1"));
        Assert.Equal(0, S.Banco.Escalar<long>("SELECT COUNT(*) FROM devolucoes"));
        Assert.Equal(0, S.Banco.Escalar<long>("SELECT COUNT(*) FROM itens_pedido WHERE pedido_id = $p", ("$p", vendaTeste.Id)));
        Assert.Equal(1000, S.Caixa.Resumo(real.Id).TotalVendas);
        // Um novo teste começa do pedido 1 de novo
        Assert.Equal(1, Vender(S.Caixa.Abrir(1, null, 0, teste: true), FormaPagamento.Dinheiro, ("PASTEL", 1)).Numero);
    }

    [Fact]
    public void Ficha_de_teste_sai_marcada()
    {
        var config = S.Config.Atual.Clonar();
        var normal = RenderizadorFicha.Exemplo(config);
        var teste = new Ficha
        {
            NomeEvento = normal.NomeEvento, Produto = normal.Produto, PrecoCentavos = normal.PrecoCentavos,
            NumeroPedido = 1, Caixa = 1, Data = normal.Data, Sequencia = 1, TotalFichas = 1, Teste = true,
        };
        using var a = RenderizadorFicha.Renderizar(normal, config, null);
        using var b = RenderizadorFicha.Renderizar(teste, config, null);
        Assert.True(b.Height > a.Height + 20, "a ficha de teste tem a faixa TESTE - SEM VALOR");
    }

    // ---------- Banco antigo ----------

    [Fact]
    public void Banco_da_versao_31_ganha_as_colunas_novas_sem_perder_dados()
    {
        var pasta = Path.Combine(_t.Pasta, "antigo");
        Directory.CreateDirectory(pasta);
        var caminho = Path.Combine(pasta, "bcfichas.db");
        var banco = new Banco(caminho);
        // Volta o banco para a versão 1 (como o da versão 3.1)
        banco.Executar("""
            DROP TABLE componentes_item; DROP TABLE componentes_combo; DELETE FROM versao WHERE v >= 3;
            DROP TABLE itens_devolucao; DROP TABLE devolucoes; DROP INDEX ix_pedidos_numero;
            ALTER TABLE pedidos DROP COLUMN teste; ALTER TABLE sessoes DROP COLUMN teste;
            DELETE FROM contadores WHERE nome = 'pedido_teste'; DELETE FROM versao WHERE v = 2;
            INSERT INTO sessoes (caixa, operador, aberta_em, valor_abertura) VALUES (1, 'ANA', '2026-10-01 10:00:00', 5000);
            """);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var sistema = Sistema.Iniciar(pasta, criarExemplos: false);
        var sessao = sistema.Caixa.SessaoAberta(1)!;
        Assert.Equal("ANA", sessao.Operador);
        Assert.False(sessao.Teste);
        Assert.Equal(7, sistema.Banco.Escalar<long>("SELECT MAX(v) FROM versao"));
        Assert.NotNull(sistema.Caixa.Abrir(1, null, 0, teste: true));
    }

    // ---------- Configuração ----------

    [Fact]
    public void Configuracao_antiga_passa_a_usar_a_maquininha_separada_e_trava_a_devolucao()
    {
        S.Banco.Executar("UPDATE config SET valor = $v WHERE chave = 'geral'",
            ("$v", """{"NomeEvento":"QUERMESSE","Maquininha":"Simulador","SenhaMaster":"1234","TelasProtegidas":"Configuracao, Relatorios"}"""));
        var lida = new ConfigServico(S.Banco).Atual;
        Assert.Equal(TipoMaquininha.Separada, lida.Maquininha);
        Assert.True(lida.Protegida(TelaProtegida.Devolucao));
        Assert.True(lida.Protegida(TelaProtegida.Relatorios));
        Assert.Equal(Configuracao.VersaoAtual, lida.VersaoConfig);
        Assert.Equal(@"C:\Sistema_New\produtos", lida.PastaFotos);

        // Quem escolher o simulador depois continua com ele
        var config = lida.Clonar();
        config.Maquininha = TipoMaquininha.Simulador;
        S.Config.Salvar(config);
        Assert.Equal(TipoMaquininha.Simulador, new ConfigServico(S.Banco).Atual.Maquininha);
        Assert.IsType<MaquininhaSimulada>(S.Maquininha);
    }

    [Fact]
    public void Sistema_novo_ja_vem_com_a_maquininha_separada()
    {
        Assert.Equal(TipoMaquininha.Separada, new Configuracao().Maquininha);
        Assert.IsType<MaquininhaSeparada>(S.Maquininha);
    }

    [Fact]
    public async Task Maquininha_separada_espera_o_operador()
    {
        var maquininha = new MaquininhaSeparada();
        var andamento = new List<string>();
        var cobranca = maquininha.CobrarAsync(new Cobranca("C1", 1500, FormaPagamento.Credito, "teste"),
            new Progresso(andamento.Add), CancellationToken.None);
        Assert.True(maquininha.AguardandoDecisao);
        Assert.Equal(["Passe R$ 15,00 no CRÉDITO na maquininha"], andamento);
        maquininha.Aprovar();
        Assert.True((await cobranca).Aprovado);
        Assert.False(maquininha.AguardandoDecisao);
        Assert.Null(await maquininha.ConsultarAsync("C1", 100, CancellationToken.None));
    }

    private sealed class Progresso(Action<string> acao) : IProgress<string>
    {
        public void Report(string value) => acao(value);
    }

    // ---------- Impressão ----------

    [Fact]
    public void Abertura_de_caixa_e_impressa_com_troco_data_e_hora()
    {
        var sessao = S.Caixa.Abrir(1, null, 12345);
        var resultado = S.Impressao.Documento(Relatorios.Abertura(sessao, S.Config.Atual), "Abertura impressa");
        Assert.True(resultado.Ok);
        Assert.Single(Directory.GetFiles(_t.PastaImpressoes, "*.png"));
    }

    [Fact]
    public void Ajuste_horizontal_desloca_a_ficha_na_linha_da_impressora()
    {
        var config = S.Config.Atual.Clonar();
        config.Moldura = true;
        config.AjusteHorizontal = -8; // 1 mm para a esquerda
        S.Config.Salvar(config);
        Assert.Equal(576 - 16, S.Config.Atual.LarguraConteudo);

        using var desenho = RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(S.Config.Atual), S.Config.Atual, null);
        Assert.Equal(560, desenho.Width);
        using var linha = ServicoImpressao.Posicionar(desenho, S.Config.Atual);
        Assert.Equal(576, linha.Width);
        // Puxada para a esquerda: a borda começa na coluna 3 e sobra branco à direita
        Assert.Equal(SKColors.White, linha.GetPixel(575, linha.Height / 2));
        Assert.NotEqual(SKColors.White, linha.GetPixel(4, linha.Height / 2));

        config.AjusteHorizontal = 8;
        S.Config.Salvar(config);
        using var desenho2 = RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(S.Config.Atual), S.Config.Atual, null);
        using var linha2 = ServicoImpressao.Posicionar(desenho2, S.Config.Atual);
        Assert.Equal(SKColors.White, linha2.GetPixel(10, linha2.Height / 2));
        Assert.NotEqual(SKColors.White, linha2.GetPixel(16 + 4, linha2.Height / 2));

        Assert.Equal("1,0 MM PARA A DIREITA", Relatorios.AjusteEmMm(8));
        Assert.Equal("CENTRALIZADA", Relatorios.AjusteEmMm(0));
        config.AjusteHorizontal = 500;
        S.Config.Salvar(config);
        Assert.Equal(Configuracao.AjusteMaximo, S.Config.Atual.AjusteHorizontal);
    }

    [Fact]
    public void Nome_curto_fica_numa_linha_so_ocupando_a_largura()
    {
        var config = S.Config.Atual.Clonar();
        config.Modelo = ModeloFicha.Classico4;
        Ficha Ficha(string produto) => new()
        {
            NomeEvento = "FESTA", Produto = produto, PrecoCentavos = 800, NumeroPedido = 1, Caixa = 1,
            Data = DateTime.Now, Sequencia = 1, TotalFichas = 1,
        };
        using var curto = RenderizadorFicha.Renderizar(Ficha("PASTEL"), config, null);
        using var medio = RenderizadorFicha.Renderizar(Ficha("PÃO DE MEL"), config, null);
        using var longo = RenderizadorFicha.Renderizar(Ficha("ENROLADINHO PORÇÃO DE QUEIJO"), config, null);
        // PÃO DE MEL cabe numa linha (só um pouco menor que PASTEL); o nome comprido vai para duas linhas,
        // sem deixar a ficha muito mais comprida
        Assert.True(medio.Height <= curto.Height, $"{medio.Height} > {curto.Height}");
        Assert.InRange(longo.Height, curto.Height - 40, curto.Height + 60);
    }
}

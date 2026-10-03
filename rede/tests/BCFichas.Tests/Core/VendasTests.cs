using BCFichas.Core;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Core;

public class VendasTests : IDisposable
{
    private readonly SistemaTemporario _t = new();
    private Sistema S => _t.Sistema;

    public void Dispose() => _t.Dispose();

    private Produto Produto(string nome) => S.Catalogo.Produtos().First(p => p.Nome == nome);

    [Fact]
    public void Banco_novo_vem_com_os_produtos_de_exemplo()
    {
        var pasta = Path.Combine(Path.GetTempPath(), "bcfichas-exemplos-" + Guid.NewGuid().ToString("N"));
        try
        {
            var novo = Sistema.Iniciar(pasta);
            Assert.Equal(["ITENS"], novo.Catalogo.Abas().Select(a => a.Nome));
            var produtos = novo.Catalogo.Produtos();
            Assert.Equal(12, produtos.Count);
            Assert.Equal(Enumerable.Range(1, 12), produtos.Select(p => p.Posicao));
            Assert.Contains(produtos, p => p.Nome == "ENROLADINHO PORÇÃO" && p.PrecoCentavos == 2000);
            Assert.Equal(ModeloFicha.Classico2, novo.Config.Atual.Modelo);
            Assert.Equal(TipoCorte.Parcial, novo.Config.Atual.Corte);
            if (!OperatingSystem.IsWindows()) Assert.Equal(TipoImpressora.Arquivo, novo.Config.Atual.Impressora);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(pasta, recursive: true);
        }
    }

    [Fact]
    public void Venda_em_dinheiro_calcula_troco_e_entra_no_resumo()
    {
        var sessao = S.Caixa.Abrir(1, "maria", 5000);
        var carrinho = new Carrinho();
        carrinho.Adicionar(Produto("PASTEL"));
        carrinho.Adicionar(Produto("PASTEL"));
        carrinho.Adicionar(Produto("REFRIGERANTE"));

        var pedido = S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 3000);

        Assert.Equal(StatusPedido.Pago, pedido.Status);
        Assert.Equal(2600, pedido.TotalCentavos);
        Assert.Equal(400, pedido.TrocoCentavos);
        Assert.Equal(1, pedido.Numero);

        var resumo = S.Caixa.Resumo(sessao.Id);
        Assert.Equal(2600, resumo.TotalVendas);
        Assert.Equal(1, resumo.QuantidadePedidos);
        Assert.Equal(3, resumo.QuantidadeFichas);
        Assert.Equal(5000 + 2600, resumo.DinheiroEsperado);
        Assert.Equal(new ProdutoVendido("PASTEL", 2, 2000), resumo.Produtos[0]);
    }

    [Fact]
    public void Dinheiro_recebido_menor_que_total_e_recusado()
    {
        var sessao = S.Caixa.Abrir(1, "ana", 0);
        var carrinho = new Carrinho();
        carrinho.Adicionar(Produto("PASTEL"));
        var erro = Assert.Throws<ErroDeNegocio>(() =>
            S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 500));
        Assert.Contains("menor que o total", erro.Message);
    }

    [Fact]
    public void Pix_fica_aguardando_ate_a_maquininha_aprovar()
    {
        var sessao = S.Caixa.Abrir(1, "ana", 0);
        var carrinho = new Carrinho();
        carrinho.Adicionar(Produto("CERVEJA"), 3);

        var pedido = S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Pix);
        Assert.Equal(StatusPedido.AguardandoPagamento, pedido.Status);
        Assert.Equal(0, S.Caixa.Resumo(sessao.Id).TotalVendas);

        S.Vendas.ConfirmarPagamento(pedido.Id, "AUT1");
        var resumo = S.Caixa.Resumo(sessao.Id);
        Assert.Equal(2400, resumo.Total(FormaPagamento.Pix));
        Assert.Equal(0, resumo.Total(FormaPagamento.Dinheiro));
        Assert.Equal("AUT1", S.Vendas.Pedido(pedido.Id)!.Autorizacao);
    }

    [Fact]
    public void Pedido_cancelado_nao_conta_e_numero_continua_subindo()
    {
        var sessao = S.Caixa.Abrir(1, "ana", 0);
        var carrinho = new Carrinho();
        carrinho.Adicionar(Produto("ÁGUA"));

        var p1 = S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Credito);
        S.Vendas.Cancelar(p1.Id);
        var p2 = S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Debito);
        S.Vendas.ConfirmarPagamento(p2.Id, null);

        Assert.Equal(StatusPedido.Cancelado, S.Vendas.Pedido(p1.Id)!.Status);
        Assert.Equal(p1.Numero + 1, p2.Numero);
        Assert.Equal(400, S.Caixa.Resumo(sessao.Id).TotalVendas);
        Assert.Throws<ErroDeNegocio>(() => S.Vendas.Cancelar(p2.Id));
    }

    [Fact]
    public void Estoque_baixa_so_quando_pago_e_bloqueia_venda_sem_estoque()
    {
        var pipoca = Produto("PIPOCA");
        pipoca.ControlaEstoque = true;
        pipoca.Estoque = 2;
        S.Catalogo.SalvarProduto(pipoca, 12);

        var sessao = S.Caixa.Abrir(1, "ana", 0);
        var carrinho = new Carrinho();
        Assert.True(carrinho.Adicionar(pipoca, 2));
        Assert.False(carrinho.Adicionar(pipoca));

        var pedido = S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Debito);
        Assert.Equal(2, S.Catalogo.Produto(pipoca.Id)!.Estoque);
        S.Vendas.ConfirmarPagamento(pedido.Id, null);
        Assert.Equal(0, S.Catalogo.Produto(pipoca.Id)!.Estoque);
        Assert.True(S.Catalogo.Produto(pipoca.Id)!.Esgotado);

        var outro = new Carrinho();
        outro.Adicionar(new Produto { Id = pipoca.Id, Nome = pipoca.Nome, PrecoCentavos = pipoca.PrecoCentavos });
        var erro = Assert.Throws<ErroDeNegocio>(() =>
            S.Vendas.CriarPedido(sessao, outro.Linhas, FormaPagamento.Dinheiro, 10000));
        Assert.Contains("esgotado", erro.Message);
    }

    [Fact]
    public void Fichas_saem_uma_por_unidade_vezes_fichas_por_produto()
    {
        var combo = Produto("ESPETINHO");
        combo.FichasPorUnidade = 2;
        S.Catalogo.SalvarProduto(combo, 12);

        var sessao = S.Caixa.Abrir(1, "ana", 0);
        var carrinho = new Carrinho();
        carrinho.Adicionar(S.Catalogo.Produto(combo.Id)!, 2);
        carrinho.Adicionar(Produto("SUCO"));
        var pedido = S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 5000);

        var fichas = GeradorFichas.Gerar(pedido, S.Config.Atual);
        Assert.Equal(5, fichas.Count);
        Assert.Equal(4, fichas.Count(f => f.Produto == "ESPETINHO"));
        Assert.Equal(Enumerable.Range(1, 5), fichas.Select(f => f.Sequencia));
        Assert.All(fichas, f => Assert.Equal(5, f.TotalFichas));

        var suco = pedido.Itens.Single(i => i.Nome == "SUCO");
        var soSuco = GeradorFichas.Gerar(pedido, S.Config.Atual, reimpressao: true, somenteItemId: suco.Id);
        Assert.Equal(5, Assert.Single(soSuco).Sequencia);
        Assert.True(soSuco[0].Reimpressao);
    }

    [Fact]
    public async Task Pendentes_ao_reabrir_sao_cancelados_quando_a_maquininha_nao_sabe()
    {
        var sessao = S.Caixa.Abrir(1, "ana", 0);
        var carrinho = new Carrinho();
        carrinho.Adicionar(Produto("BOLO"));
        var pedido = S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Pix);

        var pagos = await S.Vendas.ResolverPendentesAsync(new MaquininhaSimulada(), CancellationToken.None);

        Assert.Empty(pagos);
        Assert.Equal(StatusPedido.Cancelado, S.Vendas.Pedido(pedido.Id)!.Status);
    }

    [Fact]
    public async Task Simulador_aprova_quando_o_operador_manda()
    {
        var maquininha = new MaquininhaSimulada();
        var cobranca = maquininha.CobrarAsync(new Cobranca("X", 1000, FormaPagamento.Credito, "teste"), null,
            CancellationToken.None);
        while (!maquininha.AguardandoDecisao) await Task.Delay(20);
        maquininha.Aprovar();
        var resultado = await cobranca;
        Assert.True(resultado.Aprovado);
        Assert.NotNull(resultado.Autorizacao);
    }

    [Fact]
    public async Task Simulador_respeita_cancelamento()
    {
        var maquininha = new MaquininhaSimulada();
        using var cts = new CancellationTokenSource();
        var cobranca = maquininha.CobrarAsync(new Cobranca("X", 1000, FormaPagamento.Pix, "teste"), null, cts.Token);
        while (!maquininha.AguardandoDecisao) await Task.Delay(20);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cobranca);
    }
}

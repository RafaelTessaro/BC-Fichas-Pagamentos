using BCFichas.Core;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Core;

public class CaixaECatalogoTests : IDisposable
{
    private readonly SistemaTemporario _t = new();
    private Sistema S => _t.Sistema;

    public void Dispose() => _t.Dispose();

    [Fact]
    public void Nao_abre_o_mesmo_caixa_duas_vezes()
    {
        S.Caixa.Abrir(1, "ana", 0);
        Assert.Throws<ErroDeNegocio>(() => S.Caixa.Abrir(1, "bia", 0));
        Assert.NotNull(S.Caixa.Abrir(2, "bia", 0));
    }

    [Fact]
    public void Sangria_nao_pode_passar_do_dinheiro_em_caixa()
    {
        var sessao = S.Caixa.Abrir(1, "ana", 10000);
        S.Caixa.RegistrarMovimento(sessao, TipoMovimento.Suprimento, 2000, "troco extra");
        S.Caixa.RegistrarMovimento(sessao, TipoMovimento.Sangria, 7000, "cofre");

        var erro = Assert.Throws<ErroDeNegocio>(() =>
            S.Caixa.RegistrarMovimento(sessao, TipoMovimento.Sangria, 6000, ""));
        Assert.Contains("R$ 50,00", erro.Message);

        var resumo = S.Caixa.Resumo(sessao.Id);
        Assert.Equal(7000, resumo.Sangrias);
        Assert.Equal(2000, resumo.Suprimentos);
        Assert.Equal(5000, resumo.DinheiroEsperado);
        Assert.Equal(2, S.Caixa.Movimentos(sessao.Id).Count);
    }

    [Fact]
    public void Fechar_caixa_guarda_valor_contado_e_bloqueia_novas_vendas()
    {
        var sessao = S.Caixa.Abrir(1, "ana", 1000);
        var resumo = S.Caixa.Fechar(sessao, 950);

        Assert.False(resumo.Sessao.Aberta);
        Assert.Equal(950, resumo.Sessao.ValorContadoCentavos);
        Assert.Null(S.Caixa.SessaoAberta(1));
        Assert.Single(S.Caixa.Sessoes(DateTime.Today, DateTime.Today));

        var carrinho = new Carrinho();
        carrinho.Adicionar(S.Catalogo.Produtos()[0]);
        Assert.Throws<ErroDeNegocio>(() => S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 99999));
    }

    [Fact]
    public void Nao_fecha_caixa_com_pagamento_pendente()
    {
        var sessao = S.Caixa.Abrir(1, "ana", 0);
        var carrinho = new Carrinho();
        carrinho.Adicionar(S.Catalogo.Produtos()[0]);
        S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Credito);
        Assert.Throws<ErroDeNegocio>(() => S.Caixa.Fechar(sessao, null));
    }

    [Fact]
    public void Posicao_do_botao_nao_pode_repetir_na_mesma_aba()
    {
        var aba = S.Catalogo.Abas()[0];
        var livres = S.Catalogo.PosicoesLivres(aba.Id, 12);
        Assert.Equal([7, 8, 9, 10, 11, 12], livres);

        var erro = Assert.Throws<ErroDeNegocio>(() => S.Catalogo.SalvarProduto(
            new Produto { Nome = "Novo", AbaId = aba.Id, Posicao = 1, PrecoCentavos = 100 }, 12));
        Assert.Contains("PASTEL", erro.Message);

        var novo = S.Catalogo.SalvarProduto(
            new Produto { Nome = "Novo", AbaId = aba.Id, Posicao = 7, PrecoCentavos = 100 }, 12);
        Assert.Equal("NOVO", novo.Nome);
        Assert.DoesNotContain(7, S.Catalogo.PosicoesLivres(aba.Id, 12));
        Assert.Contains(7, S.Catalogo.PosicoesLivres(aba.Id, 12, ignorarProdutoId: novo.Id));
    }

    [Fact]
    public void Aba_com_produtos_nao_pode_ser_excluida()
    {
        var abas = S.Catalogo.Abas();
        Assert.Throws<ErroDeNegocio>(() => S.Catalogo.ExcluirAba(abas[0].Id));

        var vazia = S.Catalogo.SalvarAba(new Aba { Nome = "promoções" });
        Assert.Equal("PROMOÇÕES", vazia.Nome);
        S.Catalogo.ExcluirAba(vazia.Id);
        Assert.Equal(3, S.Catalogo.Abas().Count);
    }

    [Fact]
    public void Configuracao_e_salva_e_lida_de_volta()
    {
        var config = S.Config.Atual.Clonar();
        config.NomeEvento = "FESTA JUNINA";
        config.Modelo = ModeloFicha.Destaque;
        config.SenhaMaster = "4321";
        config.Colunas = 99;
        S.Config.Salvar(config);

        var relido = new BCFichas.Core.Servicos.ConfigServico(S.Banco).Atual;
        Assert.Equal("FESTA JUNINA", relido.NomeEvento);
        Assert.Equal(ModeloFicha.Destaque, relido.Modelo);
        Assert.Equal(6, relido.Colunas);
        Assert.True(relido.Protegida(TelaProtegida.Configuracao));
        Assert.False(relido.Protegida(TelaProtegida.Sangria));
    }
}

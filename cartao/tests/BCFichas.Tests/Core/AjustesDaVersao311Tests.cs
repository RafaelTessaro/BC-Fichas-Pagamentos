using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>Versão 3.11: devolução só em dinheiro (um movimento do caixa, como a sangria) e senha técnica.</summary>
public class AjustesDaVersao311Tests : IDisposable
{
    private readonly SistemaTemporario _t = new();
    private Sistema S => _t.Sistema;

    public void Dispose() => _t.Dispose();

    private Pedido Vender(SessaoCaixa sessao, string produto, int quantidade)
    {
        var carrinho = new Carrinho();
        carrinho.Adicionar(S.Catalogo.Produtos().First(p => p.Nome == produto), quantidade);
        return S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 100000);
    }

    [Fact]
    public void Devolucao_sai_da_gaveta_como_sangria_mas_conta_como_devolucao()
    {
        var sessao = S.Caixa.Abrir(1, null, 5000);
        Vender(sessao, "PASTEL", 3); // R$ 30
        S.Caixa.RegistrarMovimento(sessao, TipoMovimento.Sangria, 1000, "cofre");
        var devolucao = S.Caixa.RegistrarMovimento(sessao, TipoMovimento.Devolucao, 2000, "");
        Assert.True(devolucao.Saida);
        Assert.Equal("Devolução", devolucao.NomeCurto);

        var r = S.Caixa.Resumo(sessao.Id);
        Assert.Equal(1000, r.Sangrias);                 // a devolução não vira sangria
        Assert.Equal(2000, r.Devolvido(FormaPagamento.Dinheiro));
        Assert.Equal(1, r.QuantidadeDevolucoes);
        Assert.Equal(3000, r.TotalVendas);              // a venda não é cancelada
        Assert.Equal(1000, r.VendaLiquida);
        Assert.Equal(5000 + 3000 - 1000 - 2000, r.DinheiroEsperado);

        // Mais do que tem na gaveta não dá
        var erro = Assert.Throws<ErroDeNegocio>(() =>
            S.Caixa.RegistrarMovimento(sessao, TipoMovimento.Devolucao, 6000, ""));
        Assert.Equal("Não dá para devolver R$ 60,00: no caixa há R$ 50,00 em dinheiro.", erro.Message);
    }

    [Fact]
    public void Devolucao_nova_soma_com_as_de_antes_feitas_pelo_pedido()
    {
        var sessao = S.Caixa.Abrir(1, null, 5000);
        var pedido = Vender(sessao, "PASTEL", 2);
        S.Devolucoes.Devolver(sessao, pedido.Id, new Dictionary<long, int> { [pedido.Itens[0].Id] = 1 }, null);
        S.Caixa.RegistrarMovimento(sessao, TipoMovimento.Devolucao, 500, "");

        var r = S.Caixa.Resumo(sessao.Id);
        Assert.Equal(1500, r.Devolvido(FormaPagamento.Dinheiro));
        Assert.Equal(2, r.QuantidadeDevolucoes);
        Assert.Equal(5000 + 2000 - 1500, r.DinheiroEsperado);
    }

    [Fact]
    public void Comprovante_e_fechamento_mostram_a_devolucao()
    {
        var sessao = S.Caixa.Abrir(1, null, 5000);
        var m = S.Caixa.RegistrarMovimento(sessao, TipoMovimento.Devolucao, 2000, "");
        using var comprovante = Relatorios.Movimento(m, S.Config.Atual, 3000).Renderizar(576);
        Assert.True(comprovante.Height > 200);
        using var fechamento = Relatorios.Fechamento(S.Caixa.Resumo(sessao.Id), S.Config.Atual).Renderizar(576);
        Assert.True(fechamento.Height > 200);
    }

    [Fact]
    public void Senha_tecnica_confere_sem_maiusculas_e_espacos_e_nunca_vazia()
    {
        var sal = Convert.FromHexString("7b8a16ca60db69463a996ada81e479ad");
        var resumo = Convert.FromHexString("b7e59b8ab14c2a3acfd3dde7af2e47c5622883b489c55ee13458684aa57b4cbd");
        var senha = new SenhaTecnica(sal, resumo, 1000);
        Assert.True(senha.Confere("abc123"));
        Assert.True(senha.Confere(" ABC123 "));
        Assert.False(senha.Confere("abc1234"));
        Assert.False(senha.Confere(""));
        Assert.False(senha.Confere(null));
    }

    [Fact]
    public void Opcoes_liberadas_vao_no_backup_e_comecam_desligadas()
    {
        Assert.False(new Configuracao().LiberarDevolucao);
        Assert.False(new Configuracao().LiberarReimpressao);
        // O que é da máquina não inclui as opções liberadas: elas vêm com a programação (são do cliente)
        var programada = new Configuracao { LiberarDevolucao = true, LiberarReimpressao = true };
        var outra = programada.ComDadosDaMaquina(new Configuracao());
        Assert.True(outra.LiberarDevolucao);
        Assert.True(outra.LiberarReimpressao);
    }
}

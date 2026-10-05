using BCFichas.Core;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>Auditoria das regras de negócio: casos de festa cheia que quebravam a venda, a busca ou o backup.</summary>
public class AuditoriaDasRegrasTests : IDisposable
{
    private readonly SistemaTemporario _t = new();
    private Sistema S => _t.Sistema;

    public void Dispose() => _t.Dispose();

    private Produto P(string nome) => S.Catalogo.Produtos().First(p => p.Nome == nome);

    /// <summary>A gravação das configurações falha (disco cheio, energia caiu no meio).</summary>
    private static void FalharAoGravarConfiguracoes(Sistema s) => s.Banco.Executar("""
        CREATE TRIGGER falha_config_i BEFORE INSERT ON config BEGIN SELECT RAISE(ABORT, 'disco cheio'); END;
        CREATE TRIGGER falha_config_u BEFORE UPDATE ON config BEGIN SELECT RAISE(ABORT, 'disco cheio'); END;
        """);

    [Fact]
    public void Busca_de_produto_acha_com_acento_em_minuscula()
    {
        var aba = S.Catalogo.Abas()[0];
        S.Catalogo.SalvarProduto(new Produto
        {
            Nome = "Pão de mel", AbaId = aba.Id, Posicao = S.Catalogo.PosicoesLivres(aba.Id, 36).First(), PrecoCentavos = 800,
        }, 36);

        // Os nomes ficam em maiúsculas; o operador digita em minúsculas
        Assert.Equal(["PÃO DE MEL"], S.Catalogo.Produtos("pão").Select(p => p.Nome));
        Assert.Equal(["PORÇÃO DE BATATA"], S.Catalogo.Produtos("porção").Select(p => p.Nome));
        Assert.Equal(["FEIJÃO OU MANDIOCA"], S.Catalogo.Produtos("feijão").Select(p => p.Detalhe));
    }

    [Fact]
    public void Busca_de_produto_trata_porcento_e_sublinhado_como_letra()
    {
        Assert.Empty(S.Catalogo.Produtos("%"));
        Assert.Empty(S.Catalogo.Produtos("_"));
    }

    [Fact]
    public void Linha_com_quantidade_negativa_nao_entra_no_total_nem_no_troco()
    {
        var sessao = S.Caixa.Abrir(1, null, 0);
        List<LinhaCarrinho> linhas =
        [
            new() { Produto = P("PASTEL"), Quantidade = 2 },
            new() { Produto = P("REFRIGERANTE"), Quantidade = -1 },
        ];

        var pedido = S.Vendas.CriarPedido(sessao, linhas, FormaPagamento.Dinheiro, 2000);

        // Saem 2 pastéis (R$ 20): o total é o dos itens gravados e não sobra troco
        var lido = S.Vendas.Pedido(pedido.Id)!;
        Assert.Equal(2000, lido.TotalCentavos);
        Assert.Equal(lido.Itens.Sum(i => i.TotalCentavos), lido.TotalCentavos);
        Assert.Equal(0, lido.TrocoCentavos);
    }

    [Fact]
    public void Restaurar_backup_e_tudo_ou_nada_com_as_configuracoes()
    {
        var config = S.Config.Atual.Clonar();
        config.NomeEvento = "FESTA DO PEÃO";
        S.Config.Salvar(config);
        var arquivo = Path.Combine(_t.Pasta, "festa.bcf");
        S.Programacao.Salvar(arquivo);

        using var outra = new SistemaTemporario();
        var b = outra.Sistema;
        var sessao = b.Caixa.Abrir(1, null, 0);
        var carrinho = new Carrinho();
        carrinho.Adicionar(b.Catalogo.Produtos()[0]);
        b.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 10000);
        FalharAoGravarConfiguracoes(b);

        Assert.ThrowsAny<Exception>(() => b.Programacao.Carregar(arquivo, 5));

        // Nada pela metade: a máquina B continua com as vendas, o caixa e o evento dela
        Assert.Equal(1, b.Programacao.Situacao().Pedidos);
        Assert.NotNull(b.Caixa.SessaoAberta(1));
        Assert.Equal(1, b.Config.Atual.NumeroCaixa);
        Assert.NotEqual("FESTA DO PEÃO", b.Config.Atual.NomeEvento);
    }

    [Fact]
    public void Zerar_programacao_e_tudo_ou_nada_com_as_configuracoes()
    {
        var config = S.Config.Atual.Clonar();
        config.NomeEvento = "FESTA JUNINA";
        S.Config.Salvar(config);
        FalharAoGravarConfiguracoes(S);

        Assert.ThrowsAny<Exception>(() => S.Programacao.ZerarProgramacao());

        Assert.NotEmpty(S.Catalogo.Produtos());
        Assert.Equal("FESTA JUNINA", S.Config.Atual.NomeEvento);
    }

    [Fact]
    public async Task Salvar_configuracao_nao_perde_a_cobranca_em_andamento_na_maquininha()
    {
        var maquininha = Assert.IsType<MaquininhaSeparada>(S.Maquininha);
        var cobranca = maquininha.CobrarAsync(new Cobranca("C1", 1000, FormaPagamento.Debito, "teste"), null,
            CancellationToken.None);
        var config = S.Config.Atual.Clonar();
        config.NomeEvento = "OUTRO NOME";
        S.Config.Salvar(config);

        // O operador toca em "APROVADO NA MAQUININHA": a cobrança que estava esperando termina
        (S.Maquininha as MaquininhaSeparada)!.Aprovar();
        Assert.True((await cobranca.WaitAsync(TimeSpan.FromSeconds(5))).Aprovado);
    }
}

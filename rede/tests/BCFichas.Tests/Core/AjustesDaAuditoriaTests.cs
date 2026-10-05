using BCFichas.Core;
using BCFichas.Core.Pagamento;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>Pontos levantados na auditoria (regras e cálculos) que foram corrigidos à parte.</summary>
public class AjustesDaAuditoriaTests
{
    private static Cobranca Cobranca(string id) => new(id, 2000, FormaPagamento.Debito, "Pedido");

    private static async Task Esperar(Func<bool> condicao)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (!condicao())
        {
            if (DateTime.UtcNow > limite) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Simulador_recusado_nao_aprova_sozinho_a_cobranca_seguinte()
    {
        // Aprova sozinho em 1 s: a primeira cobrança é recusada antes disso
        var simulador = new MaquininhaSimulada(aprovarEmSegundos: 1);
        var primeira = simulador.CobrarAsync(Cobranca("1"), null, CancellationToken.None);
        await Esperar(() => simulador.AguardandoDecisao);
        simulador.Recusar();
        Assert.False((await primeira).Aprovado);

        // A segunda espera o operador (sem aprovar sozinho): o relógio da primeira não pode aprová-la
        simulador.AprovarEmSegundos = 0;
        var segunda = simulador.CobrarAsync(Cobranca("2"), null, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(segunda.IsCompleted);
        Assert.True(simulador.AguardandoDecisao);
        simulador.Recusar();
        Assert.False((await segunda).Aprovado);
    }

    [Fact]
    public void Backup_nao_leva_a_maquininha_de_uma_maquina_para_a_outra()
    {
        using var a = new SistemaTemporario();
        using var b = new SistemaTemporario();
        var configA = a.Sistema.Config.Atual.Clonar();
        configA.Maquininha = TipoMaquininha.Simulador;
        configA.SimuladorAprovarEmSegundos = 3;
        a.Sistema.Config.Salvar(configA);

        var arquivo = Path.Combine(a.Pasta, a.Sistema.Programacao.NomeArquivo());
        a.Sistema.Programacao.Salvar(arquivo);
        b.Sistema.Programacao.Carregar(arquivo, 2);

        // A máquina que recebeu continua com a maquininha dela (separada), sem o simulador que aprova sozinho
        Assert.Equal(TipoMaquininha.Separada, b.Sistema.Config.Atual.Maquininha);
        Assert.Equal(0, b.Sistema.Config.Atual.SimuladorAprovarEmSegundos);
        Assert.IsType<MaquininhaSeparada>(b.Sistema.Maquininha);

        // Zerar a programação (novo evento) também mantém a maquininha da máquina
        a.Sistema.Programacao.ZerarProgramacao();
        Assert.Equal(TipoMaquininha.Simulador, a.Sistema.Config.Atual.Maquininha);
    }

    [Fact]
    public void Relatorio_de_hoje_mostra_o_caixa_que_abriu_ontem_e_virou_a_noite()
    {
        using var temp = new SistemaTemporario();
        var s = temp.Sistema;
        string Data(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var hoje = DateTime.Today;

        // Caixa 1: abriu anteontem e fechou anteontem. Caixa 2: abriu ontem às 17h e fechou hoje à 1h.
        // Caixa 3: abriu ontem às 18h e continua aberto.
        var fechadoAntes = s.Caixa.Abrir(1, null, 0);
        s.Caixa.Fechar(fechadoAntes, 0);
        var virouANoite = s.Caixa.Abrir(2, null, 0);
        s.Caixa.Fechar(virouANoite, 0);
        var aberto = s.Caixa.Abrir(3, null, 0);
        s.Banco.Executar($"UPDATE sessoes SET aberta_em = '{Data(hoje.AddDays(-2).AddHours(17))}', fechada_em = '{Data(hoje.AddDays(-2).AddHours(23))}' WHERE id = {fechadoAntes.Id}");
        s.Banco.Executar($"UPDATE sessoes SET aberta_em = '{Data(hoje.AddDays(-1).AddHours(17))}', fechada_em = '{Data(hoje.AddHours(1))}' WHERE id = {virouANoite.Id}");
        s.Banco.Executar($"UPDATE sessoes SET aberta_em = '{Data(hoje.AddDays(-1).AddHours(18))}' WHERE id = {aberto.Id}");

        Assert.Equal([aberto.Id, virouANoite.Id], s.Caixa.Sessoes(hoje, hoje).Select(x => x.Id));
        Assert.Equal([aberto.Id, virouANoite.Id], s.Caixa.Sessoes(hoje.AddDays(-1), hoje.AddDays(-1)).Select(x => x.Id));
        Assert.Equal([fechadoAntes.Id], s.Caixa.Sessoes(hoje.AddDays(-2), hoje.AddDays(-2)).Select(x => x.Id));
        Assert.Equal(3, s.Caixa.Sessoes(hoje.AddDays(-6), hoje).Count);
    }

    [Fact]
    public void Aviso_do_caixa_aberto_conta_so_as_vendas_pagas()
    {
        using var temp = new SistemaTemporario();
        var s = temp.Sistema;
        var sessao = s.Caixa.Abrir(1, null, 0);
        var pastel = s.Catalogo.Produtos().First(p => p.Nome == "PASTEL");
        var carrinho = new BCFichas.Core.Vendas.Carrinho();
        carrinho.Adicionar(pastel);

        // Débito esperando a maquininha: ainda não é venda
        s.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Debito, 0);
        Assert.Equal(0, s.Programacao.Situacao().PedidosNoCaixaAberto);

        s.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 1000);
        Assert.Equal(1, s.Programacao.Situacao().PedidosNoCaixaAberto);
    }

    [Fact]
    public void Preco_do_produto_tem_limite()
    {
        using var temp = new SistemaTemporario();
        var catalogo = temp.Sistema.Catalogo;
        var pastel = catalogo.Produtos().First(p => p.Nome == "PASTEL");

        pastel.PrecoCentavos = 99_999_999_999_900; // um monte de zeros a mais
        var erro = Assert.Throws<ErroDeNegocio>(() => catalogo.SalvarProduto(pastel, 30));
        Assert.Contains("R$ 99.999,99", erro.Message);

        pastel.PrecoCentavos = 9_999_999;
        catalogo.SalvarProduto(pastel, 30);
        Assert.Equal(9_999_999, catalogo.Produto(pastel.Id)!.PrecoCentavos);
    }

    [Fact]
    public void Apagar_as_vendas_volta_a_abertura_de_caixa_para_1()
    {
        using var temp = new SistemaTemporario();
        var caixa = temp.Sistema.Caixa;
        for (var i = 0; i < 3; i++) caixa.Fechar(caixa.Abrir(1, null, 0), 0);
        Assert.Equal(4, caixa.Abrir(1, null, 0).Id); // a 4ª abertura sai como SESSÃO 4

        // Deixar pura para o cliente: a próxima abertura sai como SESSÃO 1, como o pedido volta para o 1
        temp.Sistema.Programacao.ApagarVendas();
        Assert.Equal(1, caixa.Abrir(1, null, 0).Id);
    }

    [Fact]
    public void Modo_teste_nao_faz_a_abertura_de_caixa_pular_numero()
    {
        using var temp = new SistemaTemporario();
        var caixa = temp.Sistema.Caixa;
        caixa.Fechar(caixa.Abrir(1, null, 0), 0);          // SESSÃO 1
        caixa.Fechar(caixa.Abrir(1, null, 0, teste: true), 0); // o teste usa um número...
        caixa.ApagarTestes();                              // ...que volta a ficar livre ao sair do modo teste
        Assert.Equal(2, caixa.Abrir(1, null, 0).Id);
    }
}

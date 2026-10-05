using System.Collections;
using System.Globalization;
using System.Reflection;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Servicos;
using BCFichas.Core.Vendas;
using BCFichas.Tests.Ui;
using Avalonia.Headless.XUnit;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>
/// Auditoria dos números dos relatórios. Centenas de caixas sorteados (vendas nas 4 formas com troco, combos,
/// fichas por unidade, pagamentos cancelados e pendentes, sangrias, suprimentos, devoluções, modo teste, caixas
/// reabertos no mesmo dia) e cada número conferido de três jeitos: o que o teste anotou no balcão, uma conta feita à
/// mão direto nas tabelas do banco e o que o programa mostra (resumo, tela, papel do fechamento).
/// </summary>
public class AuditoriaDosCalculosTests
{
    /// <summary>20 bancos sorteados, cada um com 12 dias de festa: mais de 200 caixas de verdade conferidos.</summary>
    public static IEnumerable<object[]> Sementes() => Enumerable.Range(1, 20).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Sementes))]
    public void Cada_caixa_sorteado_bate_com_o_balcao_com_o_banco_e_com_o_papel(int semente)
    {
        using var t = new SistemaTemporario();
        FestaSorteada.CriarProdutosEspeciais(t.Sistema.Catalogo);
        var festa = new FestaSorteada(t.Sistema, semente, new DateTime(2026, 6, 1, 17, 0, 0),
            devolucaoAntiga: semente % 3 != 0);
        festa.Jogar(dias: 12);

        Assert.True(festa.Caixas.Count(c => !c.Teste) >= 12); // pelo menos um caixa por dia
        var banco = ContaDoBanco.Ler(t.Sistema);
        foreach (var anotado in festa.Caixas)
        {
            if (anotado.Apagado)
            {
                // O fim do modo teste apagou tudo; o número dele fica livre e pode ir para o caixa aberto depois
                // (a contagem das aberturas não pula)
                var depois = festa.Caixa.Sessao(anotado.Id);
                Assert.True(depois is null || depois.AbertaEm != anotado.AbertaEm);
                continue;
            }
            var resumo = festa.Caixa.Resumo(anotado.Id);
            var esperado = anotado.Conta();
            Assert.Equal(esperado, banco.Conta(anotado.Id));     // o banco guardou o que aconteceu
            Assert.Equal(esperado, Numeros.Do(resumo));            // o resumo soma certo
            Assert.Equal(anotado.Gaveta, resumo.DinheiroEsperado); // e o dinheiro esperado é o da gaveta
            ConferirTela(anotado, resumo);
            ConferirPapel(anotado, resumo, t.Sistema.Config.Atual);
            // Produtos na ordem do relatório: mais vendidos primeiro, depois pelo nome
            Assert.Equal(resumo.Produtos.OrderByDescending(p => p.Quantidade).ThenBy(p => p.Nome, StringComparer.Ordinal),
                resumo.Produtos);
        }
        banco.ConferirGravacao();
        ConferirPeriodos(festa);
    }

    [Fact]
    public void Ticket_medio_arredonda_o_centavo_em_vez_de_cortar()
    {
        using var t = new SistemaTemporario();
        var s = t.Sistema;
        var sessao = s.Caixa.Abrir(1, null, 0);
        // 3 vendas somando R$ 20,00: a média é R$ 6,666... = R$ 6,67 (cortando dava R$ 6,66)
        Vender(s, sessao, FormaPagamento.Pix, ("PASTEL", 1));      // 10,00
        Vender(s, sessao, FormaPagamento.Pix, ("BOLO", 1));        //  5,00
        Vender(s, sessao, FormaPagamento.Pix, ("PIPOCA", 1));      //  5,00
        var resumo = s.Caixa.Resumo(sessao.Id);
        Assert.Equal(667, resumo.TicketMedio);
        Assert.Equal("R$ 6,67", new ResumoVM(resumo).TicketMedio);
        Assert.Contains("TICKET MÉDIO | R$ 6,67", Papel.Linhas(Relatorios.Fechamento(resumo, s.Config.Atual)));

        // 2 vendas somando R$ 20,01: R$ 10,005 = R$ 10,01
        var outro = s.Caixa.Abrir(2, null, 0);
        var primeira = Vender(s, outro, FormaPagamento.Dinheiro, ("PASTEL", 1));
        Vender(s, outro, FormaPagamento.Dinheiro, ("PASTEL", 1));
        s.Banco.Executar("UPDATE pedidos SET total = 1001 WHERE id = $p", ("$p", primeira.Id));
        Assert.Equal(1001, s.Caixa.Resumo(outro.Id).TicketMedio);
    }

    [Fact]
    public void Caixa_vazio_e_caixa_so_com_devolucoes()
    {
        using var t = new SistemaTemporario();
        var s = t.Sistema;
        var vazio = s.Caixa.Fechar(s.Caixa.Abrir(1, null, 0), 0);
        Assert.Equal(new Numeros("", 0, 0, 0, 0, 0, 0, "", 0, 0, 0, 0, "", ""), Numeros.Do(vazio));
        Assert.Contains("DIFERENÇA | R$ 0,00", Papel.Linhas(Relatorios.Fechamento(vazio, s.Config.Atual)));

        // Só devolução (fichas compradas ontem): a venda líquida fica negativa e a gaveta tem a abertura menos ela
        var sessao = s.Caixa.Abrir(1, null, 10000);
        s.Caixa.RegistrarMovimento(sessao, TipoMovimento.Devolucao, 2500, "");
        s.Caixa.RegistrarMovimento(sessao, TipoMovimento.Devolucao, 1000, "");
        var r = s.Caixa.Fechar(sessao, 6000);
        Assert.Equal((0L, 3500L, -3500L, 6500L, 2), (r.TotalVendas, r.TotalDevolvido, r.VendaLiquida, r.DinheiroEsperado,
            r.QuantidadeDevolucoes));
        Assert.Equal(0, r.TicketMedio);
        var papel = Papel.Linhas(Relatorios.Fechamento(r, s.Config.Atual));
        Assert.Contains("VENDA LÍQUIDA | -R$ 35,00", papel);
        Assert.Contains("TOTAL DEVOLVIDO | - R$ 35,00", papel);
        Assert.Contains("= ESPERADO | R$ 65,00", papel);
        Assert.Contains("FALTA | R$ 5,00", papel);
        var tela = new ResumoVM(r);
        Assert.Equal("-R$ 35,00", tela.VendaLiquida);
        Assert.Contains(tela.Gaveta, l => l.Nome == "Falta" && l.Valor == "R$ 5,00");
    }

    [Fact]
    public void Valores_muito_grandes_nao_estouram()
    {
        using var t = new SistemaTemporario();
        var s = t.Sistema;
        var sessao = s.Caixa.Abrir(1, null, 0);
        Vender(s, sessao, FormaPagamento.Credito, ("PASTEL", 1));
        Vender(s, sessao, FormaPagamento.Credito, ("PASTEL", 1));
        // Dois pedidos de R$ 40 trilhões: a soma (R$ 80 tri) ainda cabe no long dos centavos
        s.Banco.Executar("UPDATE pedidos SET total = 4000000000000000");
        var r = s.Caixa.Resumo(sessao.Id);
        Assert.Equal(8_000_000_000_000_000, r.TotalVendas);
        Assert.Equal(4_000_000_000_000_000, r.TicketMedio);
        Assert.Equal("R$ 80.000.000.000.000,00", Dinheiro.Formatar(r.TotalVendas));
    }

    [AvaloniaFact]
    public void Relatorios_do_gerente_somam_os_caixas_de_cada_periodo_sem_os_de_teste()
    {
        using var t = new TelaDeTeste();
        FestaSorteada.CriarProdutosEspeciais(t.Sistema.Catalogo);
        // 34 dias de festa terminando hoje (o filtro da tela é a partir de hoje)
        var festa = new FestaSorteada(t.Sistema, 99, DateTime.Today.AddDays(-33).AddHours(17));
        festa.Jogar(dias: 34);
        var tela = new RelatoriosViewModel(t.Principal);
        tela.AoAbrir();

        foreach (var dias in new[] { 1, 7, 30, 0 })
        {
            tela.Dias = dias;
            var de = dias == 0 ? DateTime.MinValue : DateTime.Today.AddDays(1 - dias);
            var caixas = festa.Caixas.Where(c => !c.Teste && c.AbertoNoPeriodo(de, DateTime.Today))
                .OrderByDescending(c => c.Id).ToList();
            Assert.Equal(caixas.Select(c => c.Id), tela.Sessoes.Select(x => x.Sessao.Id));
            Assert.Equal(caixas.Select(c => Dinheiro.Formatar(c.TotalVendido)), tela.Sessoes.Select(x => x.Total));

            var movimentos = festa.Movimentos.Where(m => !m.Teste && m.Quando.Date >= de && m.Quando.Date <= DateTime.Today).ToList();
            long Soma(TipoMovimento tipo) => movimentos.Where(m => m.Tipo == tipo).Sum(m => m.Valor);
            var texto = $"Sangrias: {Dinheiro.Formatar(Soma(TipoMovimento.Sangria))}   •   " +
                        $"Suprimentos: {Dinheiro.Formatar(Soma(TipoMovimento.Suprimento))}";
            if (Soma(TipoMovimento.Devolucao) > 0) texto += $"   •   Devoluções: {Dinheiro.Formatar(Soma(TipoMovimento.Devolucao))}";
            Assert.Equal(texto, tela.TotalMovimentos);
            Assert.Equal(movimentos.Count, tela.Movimentos.Count);

            // O caixa escolhido na lista mostra o resumo dele
            if (tela.SessaoSelecionada is { } escolhido)
                Assert.Equal(Dinheiro.Formatar(festa.Caixas.Single(c => c.Id == escolhido.Sessao.Id && !c.Apagado).TotalVendido),
                    tela.ResumoSelecionado!.TotalVendido);
        }
    }

    // ---------- Conferências ----------

    /// <summary>A tela do resumo (relatórios e fechamento) mostra os números certos.</summary>
    private static void ConferirTela(CaixaAnotado c, ResumoCaixa resumo)
    {
        var tela = new ResumoVM(resumo);
        Assert.Equal(Dinheiro.Formatar(c.TotalVendido), tela.TotalVendido);
        Assert.Equal(c.TotalPedidos.ToString(CultureInfo.InvariantCulture), tela.Pedidos);
        Assert.Equal(c.Fichas.ToString(CultureInfo.InvariantCulture), tela.Fichas);
        Assert.Equal(Dinheiro.Formatar(Numeros.Media(c.TotalVendido, c.TotalPedidos)), tela.TicketMedio);
        Assert.Equal(Enum.GetValues<FormaPagamento>().Select(f => (Nomes.De(f), Dinheiro.Formatar(c.Vendido.GetValueOrDefault(f)),
                $"{c.Pedidos.GetValueOrDefault(f)} pedido(s)")),
            tela.Formas.Select(l => (l.Nome, l.Valor, l.Detalhe)));

        var gaveta = new List<(string, string)>
        {
            ("Abertura (troco)", Dinheiro.Formatar(c.Abertura)),
            ("+ Vendas em dinheiro", Dinheiro.Formatar(c.Vendido.GetValueOrDefault(FormaPagamento.Dinheiro))),
            ("+ Suprimentos", Dinheiro.Formatar(c.Suprimentos)),
            ("− Sangrias", Dinheiro.Formatar(c.Sangrias)),
        };
        var devolvidoDinheiro = c.Devolvido.GetValueOrDefault(FormaPagamento.Dinheiro);
        if (devolvidoDinheiro > 0) gaveta.Add(("− Devoluções", Dinheiro.Formatar(devolvidoDinheiro)));
        gaveta.Add(("= Dinheiro esperado", Dinheiro.Formatar(c.Gaveta)));
        if (c.Contado is { } contado)
        {
            gaveta.Add(("Contado na gaveta", Dinheiro.Formatar(contado)));
            var diferenca = contado - c.Gaveta;
            gaveta.Add((diferenca == 0 ? "Diferença" : diferenca > 0 ? "Sobra" : "Falta", Dinheiro.Formatar(Math.Abs(diferenca))));
        }
        Assert.Equal(gaveta, tela.Gaveta.Select(l => (l.Nome, l.Valor)));

        Assert.Equal(c.Devolucoes > 0, tela.TemDevolucoes);
        Assert.Equal("− " + Dinheiro.Formatar(c.TotalDevolvido), tela.TotalDevolvido);
        Assert.Equal(Dinheiro.Formatar(c.TotalVendido - c.TotalDevolvido), tela.VendaLiquida);
        Assert.Equal(c.Produtos.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, Dinheiro.Formatar(p.Value.Valor), $"{p.Value.Quantidade} un.")),
            tela.Produtos.Select(l => (l.Nome, l.Valor, l.Detalhe)).OrderBy(l => l.Nome, StringComparer.Ordinal));
    }

    /// <summary>O papel do fechamento (e da parcial) sai com os números certos.</summary>
    private static void ConferirPapel(CaixaAnotado c, ResumoCaixa resumo, Configuracao config)
    {
        var papel = Papel.Linhas(Relatorios.Fechamento(resumo, config, parcial: c.FechadaEm is null));
        void Tem(string esquerda, string direita) => Assert.Contains($"{esquerda} | {direita}", papel);

        foreach (var f in Enum.GetValues<FormaPagamento>())
            Tem($"{Nomes.De(f).ToUpperInvariant()} ({c.Pedidos.GetValueOrDefault(f)})", Dinheiro.Formatar(c.Vendido.GetValueOrDefault(f)));
        Tem("TOTAL VENDIDO", Dinheiro.Formatar(c.TotalVendido));
        Tem("PEDIDOS", c.TotalPedidos.ToString(CultureInfo.InvariantCulture));
        Tem("FICHAS", c.Fichas.ToString(CultureInfo.InvariantCulture));
        Tem("TICKET MÉDIO", Dinheiro.Formatar(Numeros.Media(c.TotalVendido, c.TotalPedidos)));
        Tem("ABERTURA (TROCO)", Dinheiro.Formatar(c.Abertura));
        Tem("+ VENDAS EM DINHEIRO", Dinheiro.Formatar(c.Vendido.GetValueOrDefault(FormaPagamento.Dinheiro)));
        Tem("+ SUPRIMENTOS", Dinheiro.Formatar(c.Suprimentos));
        Tem("- SANGRIAS", Dinheiro.Formatar(c.Sangrias));
        Tem("= ESPERADO", Dinheiro.Formatar(c.Gaveta));
        var devolvidoDinheiro = c.Devolvido.GetValueOrDefault(FormaPagamento.Dinheiro);
        Assert.Equal(devolvidoDinheiro > 0, papel.Contains("- DEVOLUÇÕES | " + Dinheiro.Formatar(devolvidoDinheiro)));
        if (c.Devolucoes > 0)
        {
            Assert.Contains($"DEVOLUÇÕES DE FICHAS ({c.Devolucoes})", papel);
            Tem("TOTAL DEVOLVIDO", "- " + Dinheiro.Formatar(c.TotalDevolvido));
            Tem("VENDA LÍQUIDA", Dinheiro.Formatar(c.TotalVendido - c.TotalDevolvido));
        }
        else
        {
            Assert.DoesNotContain(papel, l => l.StartsWith("VENDA LÍQUIDA", StringComparison.Ordinal));
        }
        if (c.Contado is { } contado)
        {
            Tem("CONTADO", Dinheiro.Formatar(contado));
            var diferenca = contado - c.Gaveta;
            Tem(diferenca == 0 ? "DIFERENÇA" : diferenca > 0 ? "SOBRA" : "FALTA", Dinheiro.Formatar(Math.Abs(diferenca)));
        }
        foreach (var (nome, (quantidade, valor)) in c.Produtos)
            Tem($"{quantidade} x {nome}", Dinheiro.Formatar(valor));
        foreach (var (nome, (quantidade, valor)) in c.ProdutosDevolvidos)
            Tem($"{quantidade} x {nome}", "- " + Dinheiro.Formatar(valor));
        Assert.Equal(c.Produtos.Count + c.ProdutosDevolvidos.Count, papel.Count(l => l.Contains(" x ", StringComparison.Ordinal)));
    }

    /// <summary>Caixas e sangrias de um período (relatórios do gerente) sem nada do modo teste.</summary>
    private static void ConferirPeriodos(FestaSorteada festa)
    {
        var dias = festa.Caixas.Select(c => c.AbertaEm.Date).Concat(festa.Movimentos.Select(m => m.Quando.Date))
            .Distinct().Order().ToList();
        var r = new Random(dias.Count);
        for (var i = 0; i < 15; i++)
        {
            var de = dias[r.Next(dias.Count)];
            var ate = de.AddDays(r.Next(0, 4));
            var caixas = festa.Caixas.Where(c => !c.Teste && c.AbertoNoPeriodo(de, ate)).ToList();
            Assert.Equal(caixas.Select(c => c.Id).OrderDescending(), festa.Caixa.Sessoes(de, ate).Select(s => s.Id));
            Assert.Equal(caixas.Where(c => c.TotalPedidos > 0).ToDictionary(c => c.Id, c => c.TotalVendido),
                festa.Caixa.TotaisVendidos(de, ate));
            var movimentos = festa.Movimentos.Where(m => !m.Teste && m.Quando.Date >= de && m.Quando.Date <= ate).ToList();
            Assert.Equal(movimentos.Select(m => (m.Tipo, m.Valor)).Order(),
                festa.Caixa.Movimentos(de, ate).Select(m => (m.Tipo, m.ValorCentavos)).Order());
        }
    }

    private static Pedido Vender(Sistema s, SessaoCaixa sessao, FormaPagamento forma, params (string Nome, int Qtd)[] itens)
    {
        var carrinho = new Carrinho();
        foreach (var (nome, qtd) in itens)
            carrinho.Adicionar(s.Catalogo.Produtos().First(p => p.Nome == nome), Math.Max(1, qtd));
        var pedido = s.Vendas.CriarPedido(sessao, carrinho.Linhas, forma, forma == FormaPagamento.Dinheiro ? 100000 : 0);
        return forma == FormaPagamento.Dinheiro ? pedido : s.Vendas.ConfirmarPagamento(pedido.Id, null);
    }
}

/// <summary>Os números de um caixa, num formato fácil de comparar (e de ler quando não bate).</summary>
public sealed record Numeros(
    string Formas, long Vendido, int Pedidos, int Fichas, long TicketMedio, long Sangrias, long Suprimentos,
    string Devolvido, int Devolucoes, long TotalDevolvido, long Liquido, long Esperado, string Produtos,
    string ProdutosDevolvidos)
{
    /// <summary>Média em centavos, arredondada (meio centavo para cima), como se faz com dinheiro.</summary>
    public static long Media(long total, int quantidade) =>
        quantidade == 0 ? 0 : (long)Math.Round((decimal)total / quantidade, MidpointRounding.AwayFromZero);

    public static string Lista<T>(IEnumerable<KeyValuePair<FormaPagamento, T>> valores) =>
        string.Join("; ", valores.Where(v => !Equals(v.Value, default(T))).OrderBy(v => v.Key).Select(v => $"{v.Key}={v.Value}"));

    public static string Lista(IEnumerable<(string Nome, int Quantidade, long Valor)> produtos) =>
        string.Join("; ", produtos.OrderBy(p => p.Nome, StringComparer.Ordinal).Select(p => $"{p.Nome}={p.Quantidade}/{p.Valor}"));

    public static Numeros Do(ResumoCaixa r) => new(
        Lista(r.PorForma.Select(f => new KeyValuePair<FormaPagamento, string>(f.Key,
            f.Value == 0 && r.PedidosPorForma.GetValueOrDefault(f.Key) == 0 ? "" : $"{f.Value}/{r.PedidosPorForma.GetValueOrDefault(f.Key)}"))
            .Where(f => f.Value.Length > 0)),
        r.TotalVendas, r.QuantidadePedidos, r.QuantidadeFichas, r.TicketMedio, r.Sangrias, r.Suprimentos,
        Lista(r.DevolucoesPorForma), r.QuantidadeDevolucoes, r.TotalDevolvido, r.VendaLiquida, r.DinheiroEsperado,
        Lista(r.Produtos.Select(p => (p.Nome, p.Quantidade, p.TotalCentavos))),
        Lista(r.ProdutosDevolvidos.Select(p => (p.Nome, p.Quantidade, p.TotalCentavos))));
}

/// <summary>O que aconteceu de verdade num caixa, anotado pelo teste no balcão (sem usar as contas do programa).</summary>
public sealed class CaixaAnotado
{
    public long Id;
    public int Caixa;
    public bool Teste;
    public bool Apagado;
    public DateTime AbertaEm;
    public DateTime? FechadaEm;
    public long Abertura;
    public long? Contado;
    /// <summary>Dinheiro na gaveta, nota por nota: abertura + recebido − troco + suprimentos − sangrias − devoluções.</summary>
    public long Gaveta;
    public long Sangrias;
    public long Suprimentos;
    public int Fichas;
    public int Devolucoes;
    public readonly Dictionary<FormaPagamento, long> Vendido = new();
    public readonly Dictionary<FormaPagamento, int> Pedidos = new();
    public readonly Dictionary<FormaPagamento, long> Devolvido = new();
    public readonly Dictionary<string, (int Quantidade, long Valor)> Produtos = new();
    public readonly Dictionary<string, (int Quantidade, long Valor)> ProdutosDevolvidos = new();
    /// <summary>Hora em que cada venda paga foi feita e o total dela.</summary>
    public readonly List<(DateTime Quando, long Total)> Vendas = new();

    public long TotalVendido => Vendido.Values.Sum();
    public int TotalPedidos => Pedidos.Values.Sum();
    public long TotalDevolvido => Devolvido.Values.Sum();
    public bool Aberto => FechadaEm is null;

    /// <summary>Esteve aberto em algum momento entre o começo do dia <paramref name="de"/> e o fim do dia <paramref name="ate"/>.</summary>
    public bool AbertoNoPeriodo(DateTime de, DateTime ate) =>
        AbertaEm < ate.Date.AddDays(1) && (FechadaEm is null || FechadaEm >= de.Date);

    public static void Somar(Dictionary<string, (int Quantidade, long Valor)> lista, string nome, int quantidade, long valor)
    {
        var atual = lista.GetValueOrDefault(nome);
        lista[nome] = (atual.Quantidade + quantidade, atual.Valor + valor);
    }

    public Numeros Conta() => new(
        Numeros.Lista(Vendido.Select(v => new KeyValuePair<FormaPagamento, string>(v.Key, $"{v.Value}/{Pedidos[v.Key]}"))),
        TotalVendido, TotalPedidos, Fichas, Numeros.Media(TotalVendido, TotalPedidos), Sangrias, Suprimentos,
        Numeros.Lista(Devolvido), Devolucoes, TotalDevolvido, TotalVendido - TotalDevolvido, Gaveta,
        Numeros.Lista(Produtos.Select(p => (p.Key, p.Value.Quantidade, p.Value.Valor))),
        Numeros.Lista(ProdutosDevolvidos.Select(p => (p.Key, p.Value.Quantidade, p.Value.Valor))));
}

public sealed record MovimentoAnotado(long SessaoId, TipoMovimento Tipo, long Valor, DateTime Quando, bool Teste);

/// <summary>
/// Uma festa sorteada: dias de caixa com vendas nas 4 formas (troco, combos, fichas por unidade, item grátis),
/// pagamentos cancelados ou pendentes, fichas não impressas, preço trocado no meio da festa, sangrias, suprimentos,
/// devoluções (a de hoje, em dinheiro, e a antiga, pelo pedido), modo teste e caixa reaberto no mesmo dia.
/// Tudo o que acontece no balcão é anotado à parte em <see cref="CaixaAnotado"/>.
/// </summary>
public sealed class FestaSorteada
{
    private readonly Random _r;
    private readonly bool _devolucaoAntiga;
    private DateTime _agora;
    private List<Produto> _produtos;
    private readonly List<(long PedidoId, CaixaAnotado Caixa)> _pagos = [];
    private readonly List<(long PedidoId, CaixaAnotado Caixa, Venda Venda)> _pendentes = [];
    private (SessaoCaixa Sessao, CaixaAnotado Anotado)? _teste;

    private sealed record Venda(FormaPagamento Forma, long Total, long Recebido, long Troco, int Fichas, DateTime Quando,
        List<(string Nome, int Quantidade, long Valor)> Itens);

    public FestaSorteada(Sistema sistema, int semente, DateTime inicio, int numeroCaixa = 1, bool devolucaoAntiga = true)
    {
        S = sistema;
        _r = new Random(semente);
        _agora = inicio;
        _devolucaoAntiga = devolucaoAntiga;
        NumeroCaixa = numeroCaixa;
        Caixa = new CaixaServico(sistema.Banco, () => _agora);
        Vendas = new VendaServico(sistema.Banco, () => _agora);
        Devolucoes = new DevolucaoServico(sistema.Banco, Caixa, Vendas, () => _agora);
        _produtos = sistema.Catalogo.Produtos();
    }

    public Sistema S { get; }
    public int NumeroCaixa { get; }
    public CaixaServico Caixa { get; }
    public VendaServico Vendas { get; }
    public DevolucaoServico Devolucoes { get; }
    public List<CaixaAnotado> Caixas { get; } = [];
    public List<MovimentoAnotado> Movimentos { get; } = [];
    public DateTime Agora => _agora;

    /// <summary>Produtos que mexem nas contas: 3 fichas por unidade, preço quebrado, grátis e dois combos.</summary>
    public static void CriarProdutosEspeciais(CatalogoServico catalogo)
    {
        var aba = catalogo.Abas()[^1].Id;
        var cerveja = catalogo.Produtos().First(p => p.Nome == "CERVEJA");
        catalogo.SalvarProduto(new Produto { Nome = "ESPETO TRIPLO", AbaId = aba, Posicao = 20, PrecoCentavos = 2500, FichasPorUnidade = 3 }, 36);
        catalogo.SalvarProduto(new Produto { Nome = "PREÇO QUEBRADO", AbaId = aba, Posicao = 21, PrecoCentavos = 333 }, 36);
        catalogo.SalvarProduto(new Produto { Nome = "BRINDE", AbaId = aba, Posicao = 22, PrecoCentavos = 0 }, 36);
        catalogo.SalvarProduto(new Produto
        {
            Nome = "COMBO CERVEJA", AbaId = aba, Posicao = 23, PrecoCentavos = 3500,
            Componentes = [new ComponenteCombo { ProdutoId = cerveja.Id, Nome = "CERVEJA", Quantidade = 5, ValorCentavos = 800 }],
        }, 36);
        catalogo.SalvarProduto(new Produto
        {
            Nome = "COMBO VALES", AbaId = aba, Posicao = 24, PrecoCentavos = 3333,
            Componentes =
            [
                new ComponenteCombo { Nome = "VALE R$ 10,00", Quantidade = 2, ValorCentavos = 1000 },
                new ComponenteCombo { Nome = "VALE R$ 5,00", Detalhe = "VAL. 05/10/26", Quantidade = 3, ValorCentavos = 500 },
            ],
        }, 36);
    }

    /// <summary>
    /// Joga a festa: um caixa por dia (às vezes fechado e aberto de novo no mesmo dia, às vezes passando da
    /// meia-noite), modo teste no meio. O último caixa às vezes fica aberto.
    /// </summary>
    public void Jogar(int dias)
    {
        var inicio = _agora.Date;
        for (var dia = 0; dia < dias; dia++)
        {
            if (_agora.Date < inicio.AddDays(dia)) _agora = inicio.AddDays(dia).AddHours(17).AddMinutes(_r.Next(60));
            if (_r.Next(6) == 0) Testar(semCaixaAberto: true);
            var vezes = _r.Next(4) == 0 ? 2 : 1;
            for (var vez = 0; vez < vezes; vez++)
            {
                var ultimo = dia == dias - 1 && vez == vezes - 1;
                DiaDeCaixa(fecharNoFim: !ultimo || _r.Next(2) == 0);
                Passar(_r.Next(60, 1800));
            }
        }
    }

    private void Passar(int segundos) => _agora = _agora.AddSeconds(segundos);

    private CaixaAnotado Abrir(bool teste)
    {
        var abertura = _r.Next(4) == 0 ? 0 : _r.Next(1, 40) * 500L;
        var sessao = Caixa.Abrir(NumeroCaixa, null, abertura, teste);
        var anotado = new CaixaAnotado
        {
            Id = sessao.Id, Caixa = NumeroCaixa, Teste = teste, AbertaEm = Truncar(_agora), Abertura = abertura,
            Gaveta = abertura,
        };
        Caixas.Add(anotado);
        if (teste) _teste = (sessao, anotado);
        return anotado;
    }

    private void DiaDeCaixa(bool fecharNoFim)
    {
        var anotado = Abrir(teste: false);
        var sessao = Caixa.Sessao(anotado.Id)!;
        // Tipos de caixa: vazio, só devoluções (fichas de outro dia) ou um dia normal
        var tipo = _r.Next(12);
        var acoes = tipo == 0 ? 0 : tipo == 1 ? _r.Next(1, 4) : _r.Next(5, 45);
        for (var i = 0; i < acoes; i++)
        {
            Passar(_r.Next(5, 900));
            if (tipo == 1)
            {
                Movimento(sessao, anotado, TipoMovimento.Devolucao);
                continue;
            }
            if (_r.Next(30) == 0) Testar(semCaixaAberto: false);
            if (_r.Next(40) == 0) TrocarPreco();
            Acao(sessao, anotado);
        }
        if (fecharNoFim) Fechar(sessao, anotado);
    }

    /// <summary>Uma ação no balcão, sorteada.</summary>
    private void Acao(SessaoCaixa sessao, CaixaAnotado anotado)
    {
        var sorteio = _r.Next(100);
        if (sorteio < 60) Vender(sessao, anotado);
        else if (sorteio < 68) Movimento(sessao, anotado, TipoMovimento.Sangria);
        else if (sorteio < 75) Movimento(sessao, anotado, TipoMovimento.Suprimento);
        else if (sorteio < 83) Movimento(sessao, anotado, TipoMovimento.Devolucao);
        else if (sorteio < 90 && _devolucaoAntiga) DevolverPeloPedido(sessao, anotado);
        else if (sorteio < 94) ConfirmarPendente(anotado);
        else Passar(_r.Next(600, 3600));
    }

    /// <summary>Modo teste: vendas, sangrias e devoluções de teste. No fim às vezes apaga, às vezes fica guardado.</summary>
    private void Testar(bool semCaixaAberto)
    {
        if (_teste is null) Abrir(teste: true);
        var (sessao, anotado) = _teste!.Value;
        for (var i = _r.Next(1, 8); i > 0; i--)
        {
            Passar(_r.Next(5, 300));
            Acao(sessao, anotado);
        }
        foreach (var p in _pendentes.Where(p => p.Caixa == anotado).ToList()) Pagar(p.PedidoId, p.Caixa, p.Venda, aprovar: false);
        if (_r.Next(2) == 0 || semCaixaAberto)
        {
            Caixa.ApagarTestes();
            foreach (var c in Caixas.Where(c => c.Teste)) c.Apagado = true;
            _pagos.RemoveAll(p => p.Caixa.Teste);
            _teste = null;
        }
    }

    private void TrocarPreco()
    {
        var produto = _produtos.Where(p => !p.EhCombo && p.PrecoCentavos > 0).OrderBy(_ => _r.Next()).First();
        produto.PrecoCentavos = _r.Next(1, 60) * 50L + _r.Next(2) * 7;
        S.Catalogo.SalvarProduto(produto, 36);
        _produtos = S.Catalogo.Produtos();
    }

    private void Vender(SessaoCaixa sessao, CaixaAnotado anotado)
    {
        var carrinho = new Carrinho();
        foreach (var produto in _produtos.OrderBy(_ => _r.Next()).Take(_r.Next(1, 5)))
            Assert.True(carrinho.Adicionar(produto, _r.Next(15) == 0 ? _r.Next(10, 60) : _r.Next(1, 6)));
        var itens = carrinho.Linhas
            .Select(l => (l.Produto.Nome, l.Quantidade, Valor: l.Produto.PrecoCentavos * l.Quantidade)).ToList();
        var total = itens.Sum(i => i.Valor);
        var fichas = carrinho.Linhas.Sum(l => l.Quantidade *
            (l.Produto.Componentes.Count > 0 ? l.Produto.Componentes.Sum(c => c.Quantidade) : l.Produto.FichasPorUnidade));
        var forma = (FormaPagamento)_r.Next(1, 5);

        if (forma == FormaPagamento.Dinheiro)
        {
            long troco = _r.Next(4) switch
            {
                0 => 0,
                1 => _r.Next(1, 100),
                2 => (5000 - total % 5000) % 5000,
                _ => _r.Next(0, 20000),
            };
            var pedido = Vendas.CriarPedido(sessao, carrinho.Linhas, forma, total + troco);
            Assert.Equal((total, total + troco, troco, StatusPedido.Pago),
                (pedido.TotalCentavos, pedido.RecebidoCentavos, pedido.TrocoCentavos, pedido.Status));
            Anotar(pedido.Id, anotado, new Venda(forma, total, total + troco, troco, fichas, Truncar(_agora), itens));
            return;
        }

        var cartao = Vendas.CriarPedido(sessao, carrinho.Linhas, forma);
        Assert.Equal((total, StatusPedido.AguardandoPagamento), (cartao.TotalCentavos, cartao.Status));
        var venda = new Venda(forma, total, total, 0, fichas, Truncar(_agora), itens);
        var destino = _r.Next(20);
        if (destino < 3) _pendentes.Add((cartao.Id, anotado, venda));   // a maquininha ainda não respondeu
        else Pagar(cartao.Id, anotado, venda, aprovar: destino >= 6);   // recusado ou desistiu: cancelado
    }

    private void Pagar(long pedidoId, CaixaAnotado anotado, Venda venda, bool aprovar)
    {
        _pendentes.RemoveAll(p => p.PedidoId == pedidoId);
        if (!aprovar)
        {
            Vendas.Cancelar(pedidoId);
            return;
        }
        Assert.Equal(StatusPedido.Pago, Vendas.ConfirmarPagamento(pedidoId, "AUT" + pedidoId).Status);
        Anotar(pedidoId, anotado, venda);
    }

    private void ConfirmarPendente(CaixaAnotado anotado)
    {
        var pendente = _pendentes.FirstOrDefault(p => p.Caixa == anotado);
        if (pendente.Venda is not null) Pagar(pendente.PedidoId, pendente.Caixa, pendente.Venda, aprovar: _r.Next(3) > 0);
    }

    /// <summary>Venda paga: entra nos números do caixa (e o dinheiro na gaveta).</summary>
    private void Anotar(long pedidoId, CaixaAnotado anotado, Venda venda)
    {
        anotado.Vendido[venda.Forma] = anotado.Vendido.GetValueOrDefault(venda.Forma) + venda.Total;
        anotado.Pedidos[venda.Forma] = anotado.Pedidos.GetValueOrDefault(venda.Forma) + 1;
        anotado.Fichas += venda.Fichas;
        if (venda.Forma == FormaPagamento.Dinheiro) anotado.Gaveta += venda.Recebido - venda.Troco;
        foreach (var (nome, quantidade, valor) in venda.Itens) CaixaAnotado.Somar(anotado.Produtos, nome, quantidade, valor);
        anotado.Vendas.Add((venda.Quando, venda.Total));
        _pagos.Add((pedidoId, anotado));

        // As fichas que saem na impressora são as que o relatório conta (às vezes a impressora falha: conta igual)
        var pedido = Vendas.Pedido(pedidoId)!;
        Assert.Equal(venda.Fichas, GeradorFichas.Gerar(pedido, S.Config.Atual).Count);
        if (_r.Next(6) > 0) Vendas.RegistrarImpressao(pedidoId);
    }

    /// <summary>Sangria, suprimento ou devolução em dinheiro (até o que tem na gaveta; às vezes tenta passar).</summary>
    private void Movimento(SessaoCaixa sessao, CaixaAnotado anotado, TipoMovimento tipo)
    {
        if (tipo != TipoMovimento.Suprimento && _r.Next(10) == 0)
        {
            Assert.Throws<ErroDeNegocio>(() => Caixa.RegistrarMovimento(sessao, tipo, anotado.Gaveta + 1, ""));
            return;
        }
        var valor = tipo == TipoMovimento.Suprimento ? _r.Next(1, 100) * 100L
            : anotado.Gaveta <= 0 ? 0 : _r.NextInt64(1, anotado.Gaveta + 1);
        if (valor <= 0) return;
        Caixa.RegistrarMovimento(sessao, tipo, valor, tipo == TipoMovimento.Sangria ? "cofre" : "");
        Movimentos.Add(new MovimentoAnotado(anotado.Id, tipo, valor, Truncar(_agora), anotado.Teste));
        switch (tipo)
        {
            case TipoMovimento.Sangria:
                anotado.Sangrias += valor;
                anotado.Gaveta -= valor;
                break;
            case TipoMovimento.Suprimento:
                anotado.Suprimentos += valor;
                anotado.Gaveta += valor;
                break;
            default:
                anotado.Devolvido[FormaPagamento.Dinheiro] = anotado.Devolvido.GetValueOrDefault(FormaPagamento.Dinheiro) + valor;
                anotado.Devolucoes++;
                anotado.Gaveta -= valor;
                break;
        }
    }

    /// <summary>Devolução de antes da versão 3.11 (pelo pedido, ficha por ficha), às vezes de um pedido de outro dia.</summary>
    private void DevolverPeloPedido(SessaoCaixa sessao, CaixaAnotado anotado)
    {
        var candidatos = _pagos.Where(p => p.Caixa.Teste == anotado.Teste).ToList();
        if (candidatos.Count == 0) return;
        var pedido = Vendas.Pedido(candidatos[_r.Next(candidatos.Count)].PedidoId)!;
        var escolhidas = FichasDoPedido.Linhas(pedido).Where(l => l.PodeDevolver > 0 && _r.Next(2) == 0)
            .Select(l => (l.Item.Id, l.Componente?.Id, _r.Next(1, l.PodeDevolver + 1))).ToList();
        if (escolhidas.Count == 0) return;
        Devolucao devolucao;
        try
        {
            devolucao = Devolucoes.Devolver(sessao, pedido.Id, escolhidas, null);
        }
        catch (ErroDeNegocio) when (pedido.Forma == FormaPagamento.Dinheiro)
        {
            return; // não tinha dinheiro na gaveta para devolver
        }
        Assert.Equal(devolucao.Itens.Sum(i => i.TotalCentavos), devolucao.ValorCentavos);
        anotado.Devolvido[pedido.Forma] = anotado.Devolvido.GetValueOrDefault(pedido.Forma) + devolucao.ValorCentavos;
        anotado.Devolucoes++;
        if (devolucao.EmDinheiro) anotado.Gaveta -= devolucao.ValorCentavos;
        foreach (var item in devolucao.Itens)
            CaixaAnotado.Somar(anotado.ProdutosDevolvidos, item.Nome, item.Quantidade, item.TotalCentavos);
    }

    private void Fechar(SessaoCaixa sessao, CaixaAnotado anotado)
    {
        if (_pendentes.Any(p => p.Caixa == anotado))
        {
            Assert.Throws<ErroDeNegocio>(() => Caixa.Fechar(sessao, null));
            foreach (var p in _pendentes.Where(p => p.Caixa == anotado).ToList())
                Pagar(p.PedidoId, p.Caixa, p.Venda, aprovar: _r.Next(2) == 0);
        }
        Passar(_r.Next(60, 600));
        anotado.Contado = _r.Next(4) switch
        {
            0 => null,
            1 => Math.Max(0, anotado.Gaveta + _r.Next(-3000, 3000)),
            _ => anotado.Gaveta,
        };
        Caixa.Fechar(sessao, anotado.Contado);
        anotado.FechadaEm = Truncar(_agora);
    }

    private static DateTime Truncar(DateTime d) => new(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second);
}

/// <summary>A conta feita à mão: lê as tabelas do banco inteiras e soma com laços simples.</summary>
public sealed class ContaDoBanco
{
    private readonly Sistema _s;
    private readonly List<(long Id, long Sessao, long Total, FormaPagamento Forma, long Recebido, long Troco, StatusPedido Status)> _pedidos;
    private readonly List<(long Id, long Pedido, string Nome, long Preco, int Quantidade, int Fichas)> _itens;
    private readonly List<(long Item, int Quantidade)> _componentes;
    private readonly List<(long Sessao, TipoMovimento Tipo, long Valor)> _movimentos;
    private readonly List<(long Id, long Sessao, FormaPagamento Forma, long Valor)> _devolucoes;
    private readonly List<(long Devolucao, string Nome, int Quantidade, long Valor)> _itensDevolvidos;
    private readonly Dictionary<long, long> _aberturas;

    private ContaDoBanco(Sistema s)
    {
        _s = s;
        var b = s.Banco;
        _pedidos = b.Consultar("SELECT id, sessao_id, total, forma, recebido, troco, status FROM pedidos",
            l => (l.GetInt64(0), l.GetInt64(1), l.GetInt64(2), (FormaPagamento)l.GetInt32(3), l.GetInt64(4), l.GetInt64(5),
                (StatusPedido)l.GetInt32(6)));
        _itens = b.Consultar("SELECT id, pedido_id, nome, preco, quantidade, fichas_por_unidade FROM itens_pedido",
            l => (l.GetInt64(0), l.GetInt64(1), l.GetString(2), l.GetInt64(3), l.GetInt32(4), l.GetInt32(5)));
        _componentes = b.Consultar("SELECT item_id, quantidade FROM componentes_item", l => (l.GetInt64(0), l.GetInt32(1)));
        _movimentos = b.Consultar("SELECT sessao_id, tipo, valor FROM movimentos",
            l => (l.GetInt64(0), (TipoMovimento)l.GetInt32(1), l.GetInt64(2)));
        _devolucoes = b.Consultar("SELECT id, sessao_id, forma, valor FROM devolucoes",
            l => (l.GetInt64(0), l.GetInt64(1), (FormaPagamento)l.GetInt32(2), l.GetInt64(3)));
        _itensDevolvidos = b.Consultar("SELECT devolucao_id, nome, quantidade, valor FROM itens_devolucao",
            l => (l.GetInt64(0), l.GetString(1), l.GetInt32(2), l.GetInt64(3)));
        _aberturas = b.Consultar("SELECT id, valor_abertura FROM sessoes", l => (l.GetInt64(0), l.GetInt64(1)))
            .ToDictionary(x => x.Item1, x => x.Item2);
    }

    public static ContaDoBanco Ler(Sistema s) => new(s);

    public Numeros Conta(long sessao)
    {
        var vendido = new Dictionary<FormaPagamento, long>();
        var pedidos = new Dictionary<FormaPagamento, int>();
        var produtos = new Dictionary<string, (int Quantidade, long Valor)>();
        long gaveta = _aberturas[sessao], sangrias = 0, suprimentos = 0;
        var fichas = 0;
        foreach (var p in _pedidos)
        {
            if (p.Sessao != sessao || p.Status != StatusPedido.Pago) continue;
            vendido[p.Forma] = vendido.GetValueOrDefault(p.Forma) + p.Total;
            pedidos[p.Forma] = pedidos.GetValueOrDefault(p.Forma) + 1;
            if (p.Forma == FormaPagamento.Dinheiro) gaveta += p.Recebido - p.Troco;
            foreach (var i in _itens)
            {
                if (i.Pedido != p.Id) continue;
                CaixaAnotado.Somar(produtos, i.Nome, i.Quantidade, i.Preco * i.Quantidade);
                fichas += i.Quantidade * i.Fichas;
            }
        }
        var devolvido = new Dictionary<FormaPagamento, long>();
        var devolucoes = 0;
        foreach (var m in _movimentos)
        {
            if (m.Sessao != sessao) continue;
            if (m.Tipo == TipoMovimento.Sangria) { sangrias += m.Valor; gaveta -= m.Valor; }
            if (m.Tipo == TipoMovimento.Suprimento) { suprimentos += m.Valor; gaveta += m.Valor; }
            if (m.Tipo == TipoMovimento.Devolucao)
            {
                devolvido[FormaPagamento.Dinheiro] = devolvido.GetValueOrDefault(FormaPagamento.Dinheiro) + m.Valor;
                devolucoes++;
                gaveta -= m.Valor;
            }
        }
        var produtosDevolvidos = new Dictionary<string, (int Quantidade, long Valor)>();
        foreach (var d in _devolucoes)
        {
            if (d.Sessao != sessao) continue;
            devolvido[d.Forma] = devolvido.GetValueOrDefault(d.Forma) + d.Valor;
            devolucoes++;
            if (d.Forma == FormaPagamento.Dinheiro) gaveta -= d.Valor;
            foreach (var i in _itensDevolvidos)
                if (i.Devolucao == d.Id) CaixaAnotado.Somar(produtosDevolvidos, i.Nome, i.Quantidade, i.Valor);
        }
        var total = vendido.Values.Sum();
        var quantidade = pedidos.Values.Sum();
        return new Numeros(
            Numeros.Lista(vendido.Select(v => new KeyValuePair<FormaPagamento, string>(v.Key, $"{v.Value}/{pedidos[v.Key]}"))),
            total, quantidade, fichas, Numeros.Media(total, quantidade), sangrias, suprimentos,
            Numeros.Lista(devolvido), devolucoes, devolvido.Values.Sum(), total - devolvido.Values.Sum(), gaveta,
            Numeros.Lista(produtos.Select(p => (p.Key, p.Value.Quantidade, p.Value.Valor))),
            Numeros.Lista(produtosDevolvidos.Select(p => (p.Key, p.Value.Quantidade, p.Value.Valor))));
    }

    /// <summary>
    /// O que foi gravado em cada pedido fecha: total = soma dos itens, troco = recebido − total, fichas do combo =
    /// soma das fichas dele, e um combo devolvido inteiro devolve o preço dele certinho.
    /// </summary>
    public void ConferirGravacao()
    {
        foreach (var p in _pedidos)
        {
            Assert.Equal(p.Total, _itens.Where(i => i.Pedido == p.Id).Sum(i => i.Preco * i.Quantidade));
            if (p.Forma == FormaPagamento.Dinheiro) Assert.Equal(p.Total, p.Recebido - p.Troco);
            else Assert.Equal((p.Total, 0L), (p.Recebido, p.Troco));
        }
        foreach (var i in _itens)
        {
            var componentes = _componentes.Where(c => c.Item == i.Id).ToList();
            if (componentes.Count > 0) Assert.Equal(componentes.Sum(c => c.Quantidade), i.Fichas);
        }
        foreach (var d in _devolucoes)
            Assert.Equal(d.Valor, _itensDevolvidos.Where(i => i.Devolucao == d.Id).Sum(i => i.Valor));

        // Devolução de combo: nunca mais que o preço pago e, devolvido inteiro, exatamente o preço
        var porItem = _s.Banco.Consultar("""
            SELECT i.id, i.preco * i.quantidade, i.quantidade * i.fichas_por_unidade, SUM(d.quantidade), SUM(d.valor)
            FROM itens_devolucao d JOIN itens_pedido i ON i.id = d.item_id
            WHERE d.componente_id IS NOT NULL GROUP BY i.id
            """, l => (Pago: l.GetInt64(1), Fichas: l.GetInt64(2), Devolvidas: l.GetInt64(3), Devolvido: l.GetInt64(4)));
        foreach (var x in porItem)
        {
            Assert.True(x.Devolvido <= x.Pago, $"devolveu {x.Devolvido} de um combo de {x.Pago}");
            if (x.Devolvidas == x.Fichas) Assert.Equal(x.Pago, x.Devolvido);
        }
    }
}

/// <summary>Lê o texto que vai para o papel (antes de desenhar): "ESQUERDA | DIREITA" ou a linha sozinha.</summary>
public static class Papel
{
    public static List<string> Linhas(Documento documento)
    {
        var partes = (IEnumerable)typeof(Documento).GetField("_partes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(documento)!;
        var linhas = new List<string>();
        foreach (Delegate parte in partes)
        {
            var alvo = parte.Target;
            if (alvo is null) continue;
            string? Campo(string nome) => alvo.GetType().GetField(nome)?.GetValue(alvo) as string;
            if (Campo("esquerda") is { } esquerda) linhas.Add($"{esquerda} | {Campo("direita")}");
            else if (Campo("texto") is { } texto) linhas.Add(texto);
        }
        return linhas;
    }
}

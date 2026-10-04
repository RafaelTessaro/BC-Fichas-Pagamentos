using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BCFichas.Core;
using BCFichas.Core.Vendas;
using BCFichas.Painel;
using BCFichas.Tests.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BCFichas.Tests.Painel;

/// <summary>
/// Auditoria das contas do painel: várias máquinas com festas sorteadas (as mesmas da auditoria do caixa) e cada
/// número do "Caixa aberto" e do "Evento todo" — de cada máquina e do evento somado — conferido com o que o teste
/// anotou no balcão. Também o JSON que vai para o celular e as chaves que a página usa.
/// </summary>
public class AuditoriaDoPainelCalculosTests : IAsyncLifetime
{
    private const string Pin = "246810";
    private readonly List<SistemaTemporario> _maquinas = [];
    private readonly List<Servidor> _servidores = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var s in _servidores) await s.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var m in _maquinas) m.Dispose();
    }

    public static IEnumerable<object[]> Sementes() => Enumerable.Range(1, 8).Select(i => new object[] { i });

    /// <summary>Máquina do evento com o caixa e o PIN do painel ligados.</summary>
    private SistemaTemporario Maquina(int caixa, string outras = "")
    {
        var t = new SistemaTemporario();
        _maquinas.Add(t);
        var c = t.Sistema.Config.Atual.Clonar();
        c.NomeEvento = "FESTA JUNINA";
        c.NumeroCaixa = caixa;
        c.PainelAtivo = true;
        c.PainelPin = Pin;
        c.PainelMaquinas = outras;
        t.Sistema.Config.Salvar(c);
        return t;
    }

    [Theory]
    [MemberData(nameof(Sementes))]
    public void Caixa_aberto_e_evento_todo_de_cada_maquina_e_da_rede_batem_com_o_balcao(int semente)
    {
        var festas = new List<FestaSorteada>();
        var estados = new List<EstadoMaquina>();
        for (var caixa = 1; caixa <= 3; caixa++)
        {
            var t = Maquina(caixa);
            FestaSorteada.CriarProdutosEspeciais(t.Sistema.Catalogo);
            var festa = new FestaSorteada(t.Sistema, semente * 10 + caixa, DateTime.Today.AddDays(-4).AddHours(17),
                numeroCaixa: caixa, devolucaoAntiga: caixa != 2);
            festa.Jogar(dias: 5);
            festas.Add(festa);

            using var leitor = new Leitor(t.Pasta, $"maquina{caixa}");
            var (estado, _) = leitor.Ler();
            Assert.Equal(caixa, estado.Caixa);
            Assert.Equal(Texto(Esperado(festa.Caixas.Where(c => c.Aberto && c.Caixa == caixa))), Texto(estado.CaixaAberto));
            Assert.Equal(Texto(Esperado(festa.Caixas)), Texto(estado.TodoEvento));
            estados.Add(estado);
        }

        // O evento: a soma das máquinas (a conta é refeita do zero com tudo o que aconteceu nas três)
        var todos = festas.SelectMany(f => f.Caixas).ToList();
        Assert.Equal(Texto(Esperado(todos.Where(c => c.Aberto))), Texto(Rede.Juntar(estados.Select(e => e.CaixaAberto))));
        Assert.Equal(Texto(Esperado(todos)), Texto(Rede.Juntar(estados.Select(e => e.TodoEvento))));

        // O JSON que vai para o celular leva os mesmos números
        var json = JsonSerializer.Serialize(estados[0], Json.Opcoes);
        var lido = JsonSerializer.Deserialize<EstadoMaquina>(json, Json.Opcoes)!;
        Assert.Equal(Texto(estados[0].TodoEvento), Texto(lido.TodoEvento));
    }

    [Fact]
    public void Ticket_medio_do_painel_arredonda_como_o_do_caixa()
    {
        var t = Maquina(1);
        var s = t.Sistema;
        var sessao = s.Caixa.Abrir(1, null, 0);
        foreach (var nome in new[] { "PASTEL", "BOLO", "PIPOCA" }) // R$ 20,00 em 3 vendas = R$ 6,67
        {
            var carrinho = new Carrinho();
            carrinho.Adicionar(s.Catalogo.Produtos().First(p => p.Nome == nome));
            s.Vendas.ConfirmarPagamento(s.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Pix).Id, null);
        }
        using var leitor = new Leitor(t.Pasta, "um");
        var (estado, _) = leitor.Ler();
        Assert.Equal(667, estado.CaixaAberto.TicketMedio);
        Assert.Equal(s.Caixa.Resumo(sessao.Id).TicketMedio, estado.CaixaAberto.TicketMedio);
        // Somando outra máquina: R$ 20,00 + R$ 10,02 em 4 vendas = R$ 7,505 = R$ 7,51 (cortando dava R$ 7,50)
        var outra = estado.CaixaAberto with { Vendido = 1002, Pedidos = 1 };
        Assert.Equal(751, Rede.Juntar([estado.CaixaAberto, outra]).TicketMedio);
    }

    [Fact]
    public async Task Maquina_achada_em_dois_enderecos_conta_uma_vez_so()
    {
        var b = Maquina(2);
        var sessaoB = b.Sistema.Caixa.Abrir(2, null, 0);
        var carrinho = new Carrinho();
        carrinho.Adicionar(b.Sistema.Catalogo.Produtos().First(p => p.Nome == "CERVEJA"), 3); // R$ 24
        b.Sistema.Vendas.ConfirmarPagamento(b.Sistema.Vendas.CriarPedido(sessaoB, carrinho.Linhas, FormaPagamento.Credito).Id, null);
        var servidorB = await Subir(b);

        // A máquina 2 digitada de dois jeitos (pelo IP e pelo nome): é a mesma máquina, conta uma vez
        var a = Maquina(1, $"127.0.0.1:{servidorB.Porta} localhost:{servidorB.Porta}");
        var servidorA = await Subir(a);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add(Servidor.CabecalhoPin, Pin);
        EstadoEvento? evento = null;
        for (var i = 0; i < 40; i++)
        {
            evento = await http.GetFromJsonAsync<EstadoEvento>($"http://127.0.0.1:{servidorA.Porta}/api/v1/evento", Json.Opcoes);
            await Task.Delay(100);
        }
        Assert.Equal([1, 2], evento!.Maquinas.Select(m => m.Estado.Caixa));
        Assert.Equal(2400, evento.CaixaAberto.Vendido);
        Assert.Equal(2400, evento.TodoEvento.Vendido);
        Assert.Equal(2, evento.MaquinasTotal);
    }

    [Fact]
    public void Chaves_das_formas_de_pagamento_sao_as_que_a_pagina_usa()
    {
        using var recurso = typeof(Servidor).Assembly.GetManifestResourceStream("pagina/index.html")!;
        var html = new StreamReader(recurso).ReadToEnd();
        var cores = Regex.Match(html, @"const CORES = \{([^}]*)\}").Groups[1].Value;
        var chavesDaPagina = Regex.Matches(cores, @"(\w+):").Select(m => m.Groups[1].Value).Order().ToList();
        Assert.Equal(chavesDaPagina, Enum.GetValues<FormaPagamento>().Select(Leitor.Chave).Order());

        // Cada número que a página lê do bloco existe no JSON (um nome errado mostraria "undefined" ou NaN)
        var bloco = JsonSerializer.SerializeToElement(Bloco.Vazio with { Formas = [new FormaVendida("pix", "PIX", 1, 1, 0)] },
            Json.Opcoes);
        foreach (Match m in Regex.Matches(html, @"\bb\.([a-zA-Z]+)\b"))
        {
            var nome = m.Groups[1].Value;
            // b também é o botão, a máquina e o produto nas ordenações
            if (nome is "dataset" or "addEventListener" or "estado" or "valor" or "quantidade") continue;
            Assert.True(bloco.TryGetProperty(nome, out _), $"a página lê b.{nome}, que não vem no JSON");
        }
    }

    private async Task<Servidor> Subir(SistemaTemporario t)
    {
        var s = await Servidor.Iniciar(t.Pasta, porta: 0, intervaloRede: TimeSpan.FromMilliseconds(100), soLocal: true);
        _servidores.Add(s);
        return s;
    }

    /// <summary>O bloco que o painel deveria mostrar, calculado só com o que foi anotado no balcão.</summary>
    private static Bloco Esperado(IEnumerable<CaixaAnotado> anotados)
    {
        var caixas = anotados.Where(c => !c.Teste).ToList(); // o modo teste nunca aparece no painel
        if (caixas.Count == 0) return Bloco.Vazio;
        var abertos = caixas.Where(c => c.Aberto).ToList();
        var vendas = caixas.SelectMany(c => c.Vendas).ToList();
        var vendido = caixas.Sum(c => c.TotalVendido);
        var pedidos = caixas.Sum(c => c.TotalPedidos);
        var devolvido = caixas.Sum(c => c.TotalDevolvido);
        var produtos = new Dictionary<string, (int Quantidade, long Valor)>();
        foreach (var (nome, (quantidade, valor)) in caixas.SelectMany(c => c.Produtos))
            CaixaAnotado.Somar(produtos, nome, quantidade, valor);
        string Data(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        return new Bloco
        {
            Vendido = vendido,
            Devolvido = devolvido,
            Liquido = vendido - devolvido,
            Pedidos = pedidos,
            Fichas = caixas.Sum(c => c.Fichas),
            TicketMedio = Numeros.Media(vendido, pedidos),
            Sangrias = caixas.Sum(c => c.Sangrias),
            Suprimentos = caixas.Sum(c => c.Suprimentos),
            Devolucoes = caixas.Sum(c => c.Devolucoes),
            DinheiroNoCaixa = abertos.Sum(c => c.Gaveta),
            Caixas = caixas.Count,
            CaixasAbertos = abertos.Count,
            AbertoEm = abertos.Count == 0 ? null : Data(abertos.Min(c => c.AbertaEm)),
            UltimaVendaEm = vendas.Count == 0 ? null : Data(vendas.Max(v => v.Quando)),
            Formas =
            [
                .. new[] { ("dinheiro", FormaPagamento.Dinheiro), ("debito", FormaPagamento.Debito),
                        ("credito", FormaPagamento.Credito), ("pix", FormaPagamento.Pix) }
                    .Select(f => new FormaVendida(f.Item1, Nomes.De(f.Item2), caixas.Sum(c => c.Vendido.GetValueOrDefault(f.Item2)),
                        caixas.Sum(c => c.Pedidos.GetValueOrDefault(f.Item2)), caixas.Sum(c => c.Devolvido.GetValueOrDefault(f.Item2)))),
            ],
            Produtos =
            [
                .. produtos.Select(p => new ProdutoVendidoPainel(p.Key, p.Value.Quantidade, p.Value.Valor))
                    .OrderByDescending(p => p.Quantidade).ThenBy(p => p.Nome, StringComparer.Ordinal),
            ],
            PorHora =
            [
                .. vendas.GroupBy(v => v.Quando.ToString("yyyy-MM-dd HH", CultureInfo.InvariantCulture))
                    .Select(g => new HoraVendida(g.Key, g.Count(), g.Sum(v => v.Total)))
                    .OrderBy(h => h.Hora, StringComparer.Ordinal),
            ],
        };
    }

    /// <summary>Bloco em texto para comparar (os produtos por nome; a ordem do ranking é conferida à parte).</summary>
    private static string Texto(Bloco b)
    {
        for (var i = 1; i < b.Produtos.Count; i++)
            Assert.True(b.Produtos[i - 1].Quantidade >= b.Produtos[i].Quantidade, "ranking fora da ordem");
        return JsonSerializer.Serialize(b with { Produtos = [.. b.Produtos.OrderBy(p => p.Nome, StringComparer.Ordinal)] },
            new JsonSerializerOptions(Json.Opcoes) { WriteIndented = true });
    }
}

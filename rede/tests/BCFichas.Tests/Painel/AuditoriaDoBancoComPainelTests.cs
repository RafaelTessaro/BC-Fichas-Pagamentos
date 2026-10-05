using System.Diagnostics;
using BCFichas.Core;
using BCFichas.Core.Dados;
using BCFichas.Core.Vendas;
using BCFichas.Painel;
using BCFichas.Tests.Core;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace BCFichas.Tests.Painel;

/// <summary>
/// O painel lendo o banco enquanto o caixa vende: a venda nunca espera pelo painel (nem dá "banco ocupado"), o
/// arquivo -wal não cresce sem parar e o caixa continua conseguindo compactar o banco.
/// </summary>
public class AuditoriaDoBancoComPainelTests(ITestOutputHelper saida) : IDisposable
{
    private readonly SistemaTemporario _t = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _t.Dispose();
    }

    private void Ligar()
    {
        var c = _t.Sistema.Config.Atual.Clonar();
        c.PainelAtivo = true;
        c.PainelPin = "2580";
        _t.Sistema.Config.Salvar(c);
    }

    private static void Vender(Sistema s, SessaoCaixa sessao, int i)
    {
        var produtos = s.Catalogo.Produtos();
        var carrinho = new Carrinho();
        carrinho.Adicionar(produtos[i % produtos.Count], 1 + i % 3);
        var forma = (FormaPagamento)(1 + i % 4);
        var pedido = s.Vendas.CriarPedido(sessao, carrinho.Linhas, forma, forma == FormaPagamento.Dinheiro ? 100000 : 0);
        if (forma != FormaPagamento.Dinheiro) s.Vendas.ConfirmarPagamento(pedido.Id, null);
        s.Vendas.RegistrarImpressao(pedido.Id);
    }

    [Fact]
    public async Task Caixa_vendendo_sem_parar_com_o_painel_lendo_nao_trava_nem_enche_o_wal()
    {
        Ligar();
        var s = _t.Sistema;
        MuitasVendas.Criar(s, dias: 10, pedidosPorDia: 500);
        var sessao = s.Caixa.Abrir(1, null, 0);
        var wal = s.Banco.Caminho + "-wal";

        // Três "celulares" pedindo os números sem pausa nenhuma (bem pior que a cada 3 s)
        using var leitor = new Leitor(_t.Pasta, "teste");
        using var parar = new CancellationTokenSource();
        var leituras = 0;
        var celulares = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!parar.IsCancellationRequested)
            {
                leitor.Ler();
                Interlocked.Increment(ref leituras);
            }
        })).ToList();

        var tempos = new List<double>();
        long maiorWal = 0;
        var relogio = Stopwatch.StartNew();
        var i = 0;
        while (relogio.Elapsed < TimeSpan.FromSeconds(6))
        {
            var venda = Stopwatch.StartNew();
            Vender(s, sessao, i++); // uma exceção (banco ocupado) derruba o teste
            tempos.Add(venda.Elapsed.TotalMilliseconds);
            maiorWal = Math.Max(maiorWal, new FileInfo(wal).Length);
        }
        parar.Cancel();
        await Task.WhenAll(celulares);
        tempos.Sort();
        saida.WriteLine($"{tempos.Count} vendas, {leituras} leituras; venda p50 {tempos[tempos.Count / 2]:0.0} ms, " +
                        $"p99 {tempos[tempos.Count * 99 / 100]:0.0} ms, máx {tempos[^1]:0.0} ms; wal máx {maiorWal / 1024} KB");

        Assert.True(leituras > 10);
        Assert.True(tempos[^1] < 2000, $"uma venda esperou {tempos[^1]:0} ms");
        // O caixa grava o -wal até ~4 MB e volta ao começo (checkpoint automático + journal_size_limit)
        Assert.True(maiorWal < 16 * 1024 * 1024, $"wal com {maiorWal / 1024} KB");

        // Com o painel aberto (conexão de vigia parada), o caixa ainda consegue esvaziar o -wal por completo
        var (ocupado, _, _) = s.Banco.Consultar("PRAGMA wal_checkpoint(TRUNCATE)",
            l => (l.GetInt32(0), l.GetInt32(1), l.GetInt32(2))).Single();
        Assert.Equal(0, ocupado);
        Assert.Equal(0, new FileInfo(wal).Length);
        Assert.Equal(i, leitor.Ler().Estado.CaixaAberto.Pedidos);
    }

    [Fact]
    public void Painel_que_perdeu_a_conexao_com_o_banco_nao_repete_marca_com_numeros_novos()
    {
        // O painel não conseguiu ler o banco por um instante (aqui: o arquivo sumiu e voltou, como numa cópia por
        // cima) e abre a conexão de vigia de novo. O "data_version" de uma conexão nova recomeça do mesmo número:
        // se a marca (ETag) repetir, o celular que já tinha a marca antiga recebe "nada mudou" e fica com os números
        // velhos. (No Windows o arquivo aberto não pode ser renomeado.)
        if (OperatingSystem.IsWindows()) return;
        Ligar();
        var s = _t.Sistema;
        var sessao = s.Caixa.Abrir(1, null, 0);
        using var leitor = new Leitor(_t.Pasta, "teste");
        var (antes, marcaAntes) = leitor.Ler();
        Assert.Equal(0, antes.CaixaAberto.Pedidos);

        Vender(s, sessao, 0);
        var arquivo = s.Banco.Caminho;
        File.Move(arquivo, arquivo + ".fora");
        SqliteConnection.ClearAllPools();
        var (durante, _) = leitor.Ler(); // não consegue ler: fica com o que tinha e tenta de novo depois
        Assert.Equal(0, durante.CaixaAberto.Pedidos);
        File.Move(arquivo + ".fora", arquivo);

        var (depois, marcaDepois) = leitor.Ler();
        saida.WriteLine($"marcas {marcaAntes} / {marcaDepois}");
        Assert.Equal(1, depois.CaixaAberto.Pedidos);
        Assert.NotEqual(marcaAntes, marcaDepois);
    }

    [Fact]
    public async Task Zerar_a_programacao_com_o_painel_aberto_zera_o_painel_e_compacta_o_banco()
    {
        Ligar();
        var s = _t.Sistema;
        var sessao = s.Caixa.Abrir(1, null, 0);
        for (var i = 0; i < 20; i++) Vender(s, sessao, i);
        await using var servidor = await Servidor.Iniciar(_t.Pasta, porta: 0, soLocal: true);
        Assert.Equal(20, servidor.Leitor.Ler().Estado.TodoEvento.Pedidos);

        s.Programacao.ZerarProgramacao();
        var (estado, _) = servidor.Leitor.Ler();
        Assert.Equal((0, 0L), (estado.TodoEvento.Pedidos, estado.TodoEvento.Vendido));
        Assert.True(servidor.Leitor.Config.PainelAtivo); // o painel é do kit, não do evento: continua ligado
        // O VACUUM e o checkpoint do "Zerar" não foram impedidos pelo painel: o -wal ficou vazio
        Assert.Equal(0, new FileInfo(s.Banco.Caminho + "-wal").Length);
    }
}

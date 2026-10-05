using System.Diagnostics;
using BCFichas.Core;
using BCFichas.Core.Vendas;
using BCFichas.Painel;
using BCFichas.Tests.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BCFichas.Tests.Painel;

/// <summary>
/// Fotos do painel no celular com um evento de exemplo (4 máquinas, uma fora da rede). Precisa do Chromium que
/// existe no ambiente de desenvolvimento; sem ele (ex.: CI do Windows), não tira as fotos.
/// </summary>
public class FotosDoPainelTests : IAsyncLifetime
{
    private const string Pin = "246810";
    private static readonly string Navegador = "/opt/pw-browsers/chromium_headless_shell-1194/chrome-linux/headless_shell";
    private static readonly string PastaFotos = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "saida", "telas"));

    private readonly List<SistemaTemporario> _maquinas = [];
    private readonly List<Servidor> _servidores = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var s in _servidores) await s.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var m in _maquinas) m.Dispose();
    }

    /// <summary>Uma máquina do evento com vendas espalhadas pelas horas da festa.</summary>
    private SistemaTemporario Maquina(int caixa, int vendas, string outras = "")
    {
        var t = new SistemaTemporario();
        _maquinas.Add(t);
        var s = t.Sistema;
        var c = s.Config.Atual.Clonar();
        c.NomeEvento = "FESTA JUNINA SÃO JOÃO";
        c.NumeroCaixa = caixa;
        c.PainelAtivo = true;
        c.PainelPin = Pin;
        c.PainelMaquinas = outras;
        s.Config.Salvar(c);

        var sessao = s.Caixa.Abrir(caixa, null, 10000);
        var produtos = s.Catalogo.Produtos();
        var formas = new[] { FormaPagamento.Dinheiro, FormaPagamento.Pix, FormaPagamento.Pix, FormaPagamento.Debito, FormaPagamento.Credito };
        for (var i = 0; i < vendas; i++)
        {
            var carrinho = new Carrinho();
            carrinho.Adicionar(produtos[(i * 7 + caixa) % produtos.Count], 1 + i % 3);
            if (i % 4 == 0) carrinho.Adicionar(produtos[(i + caixa * 3) % produtos.Count]);
            var forma = formas[(i + caixa) % formas.Length];
            var pedido = s.Vendas.CriarPedido(sessao, carrinho.Linhas, forma, forma == FormaPagamento.Dinheiro ? 100000 : 0);
            if (forma != FormaPagamento.Dinheiro) s.Vendas.ConfirmarPagamento(pedido.Id, null);
        }
        // Espalha as vendas entre 18h e 22h de hoje (o pico às 20h)
        var hoje = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        s.Banco.Executar($"""
            UPDATE pedidos SET criado_em = '{hoje} ' ||
                printf('%02d', CASE WHEN id % 10 < 2 THEN 18 WHEN id % 10 < 4 THEN 19 WHEN id % 10 < 8 THEN 20 WHEN id % 10 < 9 THEN 21 ELSE 22 END)
                || ':' || printf('%02d', (id * 7) % 60) || ':00'
            """);
        s.Banco.Executar($"UPDATE sessoes SET aberta_em = '{hoje} 17:45:00'");
        s.Caixa.RegistrarMovimento(sessao, TipoMovimento.Sangria, 5000 * caixa, "cofre");
        if (caixa == 2) s.Caixa.RegistrarMovimento(sessao, TipoMovimento.Devolucao, 1500, "");
        return t;
    }

    private void Foto(int porta, string nome, string aba, int altura)
    {
        Directory.CreateDirectory(PastaFotos);
        var destino = Path.Combine(PastaFotos, nome + ".png");
        using var p = Process.Start(new ProcessStartInfo(Navegador,
        [
            "--no-sandbox", "--hide-scrollbars", "--force-device-scale-factor=2", $"--window-size=412,{altura}",
            "--virtual-time-budget=6000", $"--screenshot={destino}", $"http://127.0.0.1:{porta}/?pin={Pin}#{aba}",
        ]) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        p.WaitForExit(30000);
        Assert.True(File.Exists(destino), p.StandardError.ReadToEnd());
    }

    [Fact]
    public async Task Fotos_do_painel_no_celular()
    {
        if (!File.Exists(Navegador)) return;

        var servidores = new List<Servidor>();
        for (var caixa = 2; caixa <= 4; caixa++)
        {
            var m = Maquina(caixa, 30 + caixa * 9);
            var s = await Servidor.Iniciar(m.Pasta, porta: 0, soLocal: true);
            _servidores.Add(s);
            servidores.Add(s);
        }
        var principal = Maquina(1, 64, string.Join(" ", servidores.Select(s => $"127.0.0.1:{s.Porta}")));
        var um = await Servidor.Iniciar(principal.Pasta, porta: 0, soLocal: true);
        _servidores.Add(um);

        // Espera as 4 aparecerem e tira a máquina 4 da rede (fica com os últimos números, marcada sem conexão)
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add(Servidor.CabecalhoPin, Pin);
        for (var i = 0; i < 50; i++)
        {
            var json = await http.GetStringAsync($"http://127.0.0.1:{um.Porta}/api/v1/evento");
            if (json.Split("\"instancia\"").Length - 1 >= 4) break;
            await Task.Delay(100);
        }
        var quatro = servidores[^1];
        await quatro.DisposeAsync();
        _servidores.Remove(quatro);
        await Task.Delay(TimeSpan.FromSeconds(3));

        Foto(um.Porta, "70-celular-painel", "painel", 2050);
        Foto(um.Porta, "71-celular-maquinas", "maquinas", 915);
        Foto(um.Porta, "72-celular-produtos", "produtos", 1300);
        Foto(um.Porta, "73-celular-por-hora", "horas", 915);
        Foto(um.Porta, "74-celular-evento-todo", "painel,evento", 915);
    }
}

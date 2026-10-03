using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BCFichas.Tests.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BCFichas.Tests.Painel;

/// <summary>
/// O BCFichasPainel de verdade (o programa separado que o caixa abre): responde na rede e sai sozinho quando o
/// caixa fecha ou quando o painel é desligado nas configurações.
/// </summary>
public class ProgramaDoPainelTests : IDisposable
{
    private readonly SistemaTemporario _maquina = new();
    private readonly List<Process> _processos = [];

    public ProgramaDoPainelTests()
    {
        var c = _maquina.Sistema.Config.Atual.Clonar();
        c.PainelAtivo = true;
        c.PainelPin = "2580";
        _maquina.Sistema.Config.Salvar(c);
    }

    public void Dispose()
    {
        foreach (var p in _processos)
        {
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Já saiu
            }
            p.Dispose();
        }
        SqliteConnection.ClearAllPools();
        _maquina.Dispose();
    }

    private static int PortaLivre()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var porta = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return porta;
    }

    /// <summary>Um processo qualquer no papel do caixa (fechá-lo é como fechar o programa).</summary>
    private Process Caixa()
    {
        var inicio = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 120 127.0.0.1")
            : new ProcessStartInfo("sleep", "120");
        inicio.UseShellExecute = false;
        inicio.RedirectStandardOutput = true;
        var p = Process.Start(inicio)!;
        _processos.Add(p);
        return p;
    }

    private Process Painel(int porta, int pai)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var p = Process.Start(new ProcessStartInfo(dotnet,
        [
            Path.Combine(AppContext.BaseDirectory, "BCFichasPainel.dll"),
            "--dados", _maquina.Sistema.PastaDados, "--porta", porta.ToString(), "--pai", pai.ToString(),
        ]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        _processos.Add(p);
        return p;
    }

    private static async Task EsperarResponder(int porta, Process painel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            Assert.False(painel.HasExited, "O painel fechou antes de responder.");
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{porta}/api/v1/info");
                Assert.Contains("\"ativo\":true", json);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(200);
            }
        }
        Assert.Fail("O painel não respondeu em 30 s.");
    }

    private static async Task EsperarSair(Process painel, int segundos = 20)
    {
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(segundos));
        await painel.WaitForExitAsync(limite.Token);
        Assert.Equal(0, painel.ExitCode);
    }

    [Fact]
    public async Task Sai_sozinho_quando_o_caixa_fecha()
    {
        var caixa = Caixa();
        var porta = PortaLivre();
        var painel = Painel(porta, caixa.Id);
        await EsperarResponder(porta, painel);

        caixa.Kill();
        await EsperarSair(painel);
    }

    [Fact]
    public async Task Sai_sozinho_quando_o_painel_e_desligado()
    {
        var caixa = Caixa();
        var porta = PortaLivre();
        var painel = Painel(porta, caixa.Id);
        await EsperarResponder(porta, painel);

        var c = _maquina.Sistema.Config.Atual.Clonar();
        c.PainelAtivo = false;
        _maquina.Sistema.Config.Salvar(c);
        await EsperarSair(painel);
        Assert.False(caixa.HasExited);
    }

    [Fact]
    public async Task Caixa_ja_fechado_nao_abre_o_painel()
    {
        var caixa = Caixa();
        caixa.Kill();
        await caixa.WaitForExitAsync();
        var painel = Painel(PortaLivre(), caixa.Id);
        await EsperarSair(painel);
    }
}

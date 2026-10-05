using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BCFichas.Tests.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BCFichas.Tests.Painel;

/// <summary>
/// O BCFichasPainel de verdade quando o caixa fecha e abre de novo, quando a porta está ocupada e quando o banco
/// ainda não existe.
/// </summary>
public class AuditoriaDoProgramaDoPainelTests : IDisposable
{
    private readonly SistemaTemporario _maquina = new();
    private readonly List<Process> _processos = [];

    public AuditoriaDoProgramaDoPainelTests()
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

    private Process Painel(int porta, int pai, string? pasta = null)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var p = Process.Start(new ProcessStartInfo(dotnet,
        [
            Path.Combine(AppContext.BaseDirectory, "BCFichasPainel.dll"),
            "--dados", pasta ?? _maquina.Sistema.PastaDados, "--porta", porta.ToString(), "--pai", pai.ToString(),
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

    private static async Task<int> EsperarSair(Process painel, int segundos = 20)
    {
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(segundos));
        await painel.WaitForExitAsync(limite.Token);
        return painel.ExitCode;
    }

    [Fact]
    public async Task Caixa_fechado_e_aberto_de_novo_na_hora_fica_com_painel()
    {
        // O caixa fechou e abriu de novo antes de o painel antigo perceber (ele confere a cada 5 s): o painel novo
        // não pode desistir só porque o antigo ainda está saindo (o caixa só tentaria de novo 30 s depois).
        var caixa = Caixa();
        var porta = PortaLivre();
        var antigo = Painel(porta, caixa.Id);
        await EsperarResponder(porta, antigo);

        caixa.Kill();
        var caixaNovo = Caixa();
        var novo = Painel(porta, caixaNovo.Id);
        Assert.Equal(0, await EsperarSair(antigo));
        await EsperarResponder(porta, novo);
    }

    [Fact]
    public async Task Porta_ocupada_sai_com_erro_e_anota_no_registro()
    {
        var ocupada = new TcpListener(IPAddress.Any, 0);
        ocupada.Start();
        try
        {
            var porta = ((IPEndPoint)ocupada.LocalEndpoint).Port;
            var painel = Painel(porta, Caixa().Id);
            Assert.Equal(1, await EsperarSair(painel));
            Assert.Contains("Abrir o painel", File.ReadAllText(Path.Combine(_maquina.Sistema.PastaDados, "painel-erros.log")));
        }
        finally
        {
            ocupada.Stop();
        }
    }

    [Fact]
    public async Task Painel_aberto_antes_de_o_caixa_criar_o_banco_sai_sem_erro()
    {
        var vazia = Path.Combine(Path.GetTempPath(), "bcfichas-teste-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vazia);
        try
        {
            var porta = PortaLivre();
            var painel = Painel(porta, Caixa().Id, vazia);
            // Responde (sem números e sem estar ativo) e sai sozinho: o caixa abre de novo quando o banco existir
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string? info = null;
            for (var i = 0; i < 100 && info is null && !painel.HasExited; i++)
            {
                try
                {
                    info = await http.GetStringAsync($"http://127.0.0.1:{porta}/api/v1/info");
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(100);
                }
            }
            Assert.Contains("\"ativo\":false", info);
            Assert.Equal(0, await EsperarSair(painel));
            Assert.False(File.Exists(Path.Combine(vazia, "bcfichas.db")), "o painel não cria o banco do caixa");
        }
        finally
        {
            try
            {
                Directory.Delete(vazia, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

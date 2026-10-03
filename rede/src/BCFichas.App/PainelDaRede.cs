using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using BCFichas.Core;

namespace BCFichas.App;

/// <summary>
/// O painel no celular: o BCFichasPainel.exe, na mesma pasta do caixa, que mostra as vendas de todas as máquinas
/// do evento no navegador do celular. O caixa abre o painel quando ele está ligado nas configurações e abre de
/// novo se ele fechar sozinho. O painel sai sozinho quando o caixa fecha ou quando é desligado.
/// </summary>
internal static class PainelDaRede
{
    public const string NomeExe = "BCFichasPainel";
    private const string NomeRegra = "BC Fichas Painel";

    private static readonly object Trava = new();
    private static Func<Configuracao>? _config;
    private static string _pastaDados = "";
    private static Process? _processo;
    private static DateTime _esperarAte;
    private static Timer? _vigia;

    public static string Exe => Path.Combine(AppContext.BaseDirectory, NomeExe + ".exe");

    /// <summary>Ao abrir o caixa: abre o painel (se ligado) e confere a cada 30 s se ele continua aberto.</summary>
    public static void Iniciar(Sistema sistema)
    {
        if (!OperatingSystem.IsWindows()) return;
        _pastaDados = sistema.PastaDados;
        _config = () => sistema.Config.Atual;
        Garantir(agora: true);
        _vigia ??= new Timer(_ => Garantir(agora: false), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Configurações salvas: o painel ligado abre na hora (o desligado sai sozinho em poucos segundos).</summary>
    public static void Aplicar(Configuracao config)
    {
        if (config.PainelAtivo) Garantir(agora: true);
    }

    private static void Garantir(bool agora)
    {
        if (!OperatingSystem.IsWindows() || _config is null) return;
        lock (Trava)
        {
            try
            {
                if (_processo is not null)
                {
                    if (!_processo.HasExited) return;
                    // Fechou com erro (ex.: a porta está ocupada): tenta de novo daqui a pouco, sem insistir a cada 30 s
                    if (_processo.ExitCode != 0) _esperarAte = DateTime.UtcNow.AddMinutes(5);
                    _processo.Dispose();
                    _processo = null;
                }
                var config = _config();
                if (!config.PainelAtivo || !Configuracao.PinValido(config.PainelPin) || !File.Exists(Exe)) return;
                if (!agora && DateTime.UtcNow < _esperarAte) return;

                var inicio = new ProcessStartInfo(Exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                };
                inicio.ArgumentList.Add("--dados");
                inicio.ArgumentList.Add(_pastaDados);
                inicio.ArgumentList.Add("--pai");
                inicio.ArgumentList.Add(Environment.ProcessId.ToString());
                _processo = Process.Start(inicio);
            }
            catch (Exception e)
            {
                Log.Erro("Abrir o painel no celular", e);
                _esperarAte = DateTime.UtcNow.AddMinutes(5);
            }
        }
    }

    /// <summary>Endereços IPv4 deste tablet na rede local (os testes trocam por um fixo).</summary>
    public static Func<List<IPAddress>> Enderecos { get; set; } = EnderecosDaMaquina;

    /// <summary>Endereços IPv4 deste tablet na rede local (o do Wi-Fi do roteador primeiro).</summary>
    private static List<IPAddress> EnderecosDaMaquina()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && Privado(a))
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>Faixas de rede de roteador (10.x, 172.16-31.x, 192.168.x).</summary>
    private static bool Privado(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>O que o celular abre (o PIN vai junto: quem lê o QR code já entra).</summary>
    public static string Link(IPAddress ip, string? pin) =>
        $"http://{ip}:{Configuracao.PortaPainel}/" + (Configuracao.PinValido(pin) ? $"?pin={pin}" : "");

    /// <summary>
    /// Libera o painel no firewall do Windows, só para a rede local. O Windows pede a permissão de administrador.
    /// Devolve a mensagem de erro (nulo se deu certo).
    /// </summary>
    public static async Task<string?> LiberarNoFirewall()
    {
        if (!OperatingSystem.IsWindows()) return "Só no Windows.";
        // Apaga a regra antiga (o programa pode ter mudado de pasta) e cria de novo, numa permissão só.
        var regra = $"name=\"{NomeRegra}\"";
        var comando =
            $"netsh advfirewall firewall delete rule {regra} >nul 2>&1 & " +
            $"netsh advfirewall firewall add rule {regra} dir=in action=allow program=\"{Exe}\" protocol=TCP " +
            $"localport={Configuracao.PortaPainel} remoteip=localsubnet profile=any enable=yes";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/s /c \"{comando}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return "O Windows não deixou abrir o firewall.";
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            return p.ExitCode == 0 ? null : "O Windows não aceitou a regra do firewall.";
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "A permissão de administrador foi negada.";
        }
        catch (Exception e)
        {
            Log.Erro("Liberar no firewall", e);
            return "Não foi possível liberar no firewall.";
        }
    }

    /// <summary>A regra do firewall já existe? (nulo: não deu para saber, ex.: fora do Windows)</summary>
    public static async Task<bool?> LiberadoNoFirewall()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{NomeRegra}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (p is null) return null;
            _ = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>O painel desta máquina está respondendo? (os testes trocam por uma resposta fixa)</summary>
    public static Func<Task<bool>> Respondendo { get; set; } = RespondendoNaMaquina;

    /// <summary>Pergunta para o painel desta máquina, sem sair do tablet.</summary>
    private static async Task<bool> RespondendoNaMaquina()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var r = await http.GetAsync($"http://127.0.0.1:{Configuracao.PortaPainel}/api/v1/info");
            return r.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

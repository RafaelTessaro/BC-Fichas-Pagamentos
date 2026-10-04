using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using BCFichas.Core;

namespace BCFichas.Painel;

/// <summary>
/// As outras máquinas do evento. Enquanto alguém está olhando o painel, pergunta a cada uma como estão as vendas
/// (a cada poucos segundos, e quase de graça quando nada mudou). Só entram as que têm o mesmo PIN do evento.
/// </summary>
public sealed class Rede : IDisposable
{
    public const int PortaPadrao = Configuracao.PortaPainel;

    private readonly Leitor _leitor;
    private readonly int _porta;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Vizinha> _vizinhas = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _parar = new();
    private DateTime _ultimaOlhada = DateTime.MinValue;
    private DateTime _ultimaProcura = DateTime.MinValue;
    private Task? _laco;

    private sealed class Vizinha
    {
        public EstadoMaquina? Estado;
        public string? Marca;
        public DateTime UltimaResposta = DateTime.MinValue;
        public DateTime PrimeiraFalha = DateTime.MinValue;
    }

    public Rede(Leitor leitor, int porta, TimeSpan? espera = null)
    {
        _leitor = leitor;
        _porta = porta;
        _http = new HttpClient { Timeout = espera ?? TimeSpan.FromSeconds(2) };
    }

    /// <summary>De quanto em quanto tempo pergunta às outras máquinas (enquanto alguém olha).</summary>
    public TimeSpan Intervalo { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Sem resposta há mais que isso: a máquina aparece "sem conexão" (com os últimos números dela).</summary>
    public TimeSpan Tolerancia { get; init; } = TimeSpan.FromSeconds(12);

    public void Iniciar() => _laco ??= Task.Run(() => Laco(_parar.Token));

    /// <summary>Alguém abriu ou atualizou o painel: as outras máquinas passam a ser consultadas.</summary>
    public void AlguemOlhando() => _ultimaOlhada = DateTime.UtcNow;

    /// <summary>Esta máquina e as outras que já responderam.</summary>
    public EstadoEvento Evento()
    {
        AlguemOlhando();
        var (local, _) = _leitor.Ler();
        var agora = DateTime.UtcNow;
        var maquinas = new List<MaquinaNoPainel> { new("Esta máquina", true, true, 0, local) };
        // A mesma máquina achada em dois endereços (digitada duas vezes, IP trocado, Wi-Fi e cabo) entra uma vez
        // só, senão as vendas dela seriam somadas duas vezes: fica o endereço que respondeu por último.
        var vizinhas = _vizinhas.Select(x => (Endereco: x.Key, Vizinha: x.Value, Estado: x.Value.Estado))
            .Where(x => x.Estado is not null && x.Estado.Instancia != local.Instancia)
            .GroupBy(x => x.Estado!.Instancia)
            .Select(g => g.OrderByDescending(x => x.Vizinha.UltimaResposta).ThenBy(x => x.Endereco).First());
        foreach (var (endereco, v, estado) in vizinhas)
        {
            var semResposta = (int)(agora - v.UltimaResposta).TotalSeconds;
            maquinas.Add(new MaquinaNoPainel(endereco, false, agora - v.UltimaResposta < Tolerancia, semResposta, estado!));
        }
        maquinas = maquinas.OrderBy(m => m.Estado.Caixa).ThenBy(m => m.Endereco).ToList();
        return new EstadoEvento(local.NomeEvento, Leitor.Agora(), maquinas.Count(m => m.Online), maquinas.Count,
            Juntar(maquinas.Select(m => m.Estado.CaixaAberto)), Juntar(maquinas.Select(m => m.Estado.TodoEvento)),
            maquinas);
    }

    /// <summary>Marca do evento: muda quando qualquer máquina muda (ou entra/sai da rede).</summary>
    public string Marca()
    {
        var (_, local) = _leitor.Ler();
        var agora = DateTime.UtcNow;
        var partes = _vizinhas.OrderBy(x => x.Key)
            .Select(x => $"{x.Key}:{x.Value.Marca}:{(agora - x.Value.UltimaResposta < Tolerancia ? 1 : 0)}");
        return local + "|" + string.Join("|", partes);
    }

    private async Task Laco(CancellationToken parar)
    {
        while (!parar.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - _ultimaOlhada < TimeSpan.FromSeconds(30))
                {
                    await Procurar(parar);
                    await Task.WhenAll(_vizinhas.Keys.ToList().Select(e => Consultar(e, parar)));
                }
                await Task.Delay(Intervalo, parar);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Registro.Erro("Rede", e);
            }
        }
    }

    /// <summary>
    /// As máquinas digitadas nas configurações; sem nenhuma, procura na rede deste tablet (a cada minuto, só
    /// enquanto alguém olha o painel).
    /// </summary>
    private async Task Procurar(CancellationToken parar)
    {
        var config = _leitor.Config;
        var digitadas = Enderecos(config.PainelMaquinas, _porta);
        foreach (var e in digitadas) _vizinhas.TryAdd(e, new Vizinha());
        if (digitadas.Count > 0 || DateTime.UtcNow - _ultimaProcura < TimeSpan.FromMinutes(1)) return;
        _ultimaProcura = DateTime.UtcNow;

        var candidatos = RedeLocal().SelectMany(ip => Vizinhos(ip).Select(v => $"{v}:{_porta}"))
            .Where(e => !_vizinhas.ContainsKey(e)).ToList();
        using var limite = new SemaphoreSlim(32);
        using var rapido = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
        await Task.WhenAll(candidatos.Select(async e =>
        {
            await limite.WaitAsync(parar);
            try
            {
                var info = await rapido.GetFromJsonAsync<Info>($"http://{e}/api/v1/info", Json.Opcoes, parar);
                if (info is { Ativo: true } && info.Instancia != _leitor.Instancia) _vizinhas.TryAdd(e, new Vizinha());
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Ninguém nesse endereço (ou não é um painel do BC Fichas)
            }
            finally
            {
                limite.Release();
            }
        }));
    }

    private async Task Consultar(string endereco, CancellationToken parar)
    {
        if (!_vizinhas.TryGetValue(endereco, out var v)) return;
        try
        {
            using var pedido = new HttpRequestMessage(HttpMethod.Get, $"http://{endereco}/api/v1/estado");
            pedido.Headers.Add(Servidor.CabecalhoPin, _leitor.Config.PainelPin);
            if (v.Marca is not null) pedido.Headers.TryAddWithoutValidation("If-None-Match", $"\"{v.Marca}\"");
            using var resposta = await _http.SendAsync(pedido, parar);
            if (resposta.StatusCode == HttpStatusCode.NotModified)
            {
                v.UltimaResposta = DateTime.UtcNow;
                return;
            }
            if (resposta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Outro evento (PIN diferente): fica de fora
                _vizinhas.TryRemove(endereco, out _);
                return;
            }
            resposta.EnsureSuccessStatusCode();
            v.Estado = await resposta.Content.ReadFromJsonAsync<EstadoMaquina>(Json.Opcoes, parar);
            v.Marca = resposta.Headers.ETag?.Tag.Trim('"');
            v.UltimaResposta = DateTime.UtcNow;
            v.PrimeiraFalha = DateTime.MinValue;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Sem resposta: continua com os últimos números. Achada na procura e sumida há 10 min: sai da lista.
            if (v.PrimeiraFalha == DateTime.MinValue) v.PrimeiraFalha = DateTime.UtcNow;
            if (v.Estado is null && DateTime.UtcNow - v.PrimeiraFalha > TimeSpan.FromMinutes(10) &&
                !Enderecos(_leitor.Config.PainelMaquinas, _porta).Contains(endereco))
                _vizinhas.TryRemove(endereco, out _);
        }
    }

    /// <summary>"192.168.1.11 192.168.1.12:8765" → endereços com a porta.</summary>
    internal static List<string> Enderecos(string? texto, int porta) =>
        (texto ?? "").Split([' ', ',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().Replace("http://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/'))
            .Where(e => e.Length > 0)
            .Select(e => e.Contains(':') ? e : $"{e}:{porta}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Endereços IPv4 de rede local deste tablet (Wi-Fi ou cabo).</summary>
    public static List<IPAddress> RedeLocal()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && Servidor.EnderecoLocal(a) && !IPAddress.IsLoopback(a))
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>Os outros 253 endereços da mesma rede /24 (ex.: 192.168.1.1 a .254, sem este).</summary>
    private static IEnumerable<string> Vizinhos(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        for (var i = 1; i < 255; i++)
            if (i != b[3]) yield return $"{b[0]}.{b[1]}.{b[2]}.{i}";
    }

    /// <summary>Soma as máquinas num bloco só (o total do evento).</summary>
    internal static Bloco Juntar(IEnumerable<Bloco> blocos)
    {
        var lista = blocos.ToList();
        var pedidos = lista.Sum(b => b.Pedidos);
        var vendido = lista.Sum(b => b.Vendido);
        var abertos = lista.Where(b => b.AbertoEm is not null).Select(b => b.AbertoEm!).ToList();
        var ultimas = lista.Where(b => b.UltimaVendaEm is not null).Select(b => b.UltimaVendaEm!).ToList();
        return new Bloco
        {
            Vendido = vendido,
            Devolvido = lista.Sum(b => b.Devolvido),
            Liquido = lista.Sum(b => b.Liquido),
            Pedidos = pedidos,
            Fichas = lista.Sum(b => b.Fichas),
            TicketMedio = ResumoCaixa.Media(vendido, pedidos),
            Sangrias = lista.Sum(b => b.Sangrias),
            Suprimentos = lista.Sum(b => b.Suprimentos),
            Devolucoes = lista.Sum(b => b.Devolucoes),
            DinheiroNoCaixa = lista.Sum(b => b.DinheiroNoCaixa),
            Caixas = lista.Sum(b => b.Caixas),
            CaixasAbertos = lista.Sum(b => b.CaixasAbertos),
            AbertoEm = abertos.Count == 0 ? null : abertos.Min(StringComparer.Ordinal),
            UltimaVendaEm = ultimas.Count == 0 ? null : ultimas.Max(StringComparer.Ordinal),
            Formas = lista.SelectMany(b => b.Formas).GroupBy(f => f.Chave)
                .Select(g => new FormaVendida(g.Key, g.First().Nome, g.Sum(f => f.Valor), g.Sum(f => f.Pedidos),
                    g.Sum(f => f.Devolvido)))
                .ToList(),
            Produtos = lista.SelectMany(b => b.Produtos).GroupBy(p => p.Nome)
                .Select(g => new ProdutoVendidoPainel(g.Key, g.Sum(p => p.Quantidade), g.Sum(p => p.Valor)))
                .OrderByDescending(p => p.Quantidade).ThenBy(p => p.Nome)
                .ToList(),
            PorHora = lista.SelectMany(b => b.PorHora).GroupBy(h => h.Hora)
                .Select(g => new HoraVendida(g.Key, g.Sum(h => h.Pedidos), g.Sum(h => h.Valor)))
                .OrderBy(h => h.Hora, StringComparer.Ordinal)
                .ToList(),
        };
    }

    public void Dispose()
    {
        _parar.Cancel();
        try
        {
            _laco?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _http.Dispose();
        _parar.Dispose();
    }
}

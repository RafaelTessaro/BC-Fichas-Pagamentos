using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using BCFichas.Core;

namespace BCFichas.Painel;

/// <summary>
/// As outras máquinas do evento. Só pergunta a cada uma como estão as vendas quando alguém pede no celular (ao abrir
/// o painel ou no botão Atualizar): sem ninguém pedindo, as máquinas não conversam. Só entram as que têm o mesmo PIN
/// do evento.
/// </summary>
public sealed class Rede : IDisposable
{
    public const int PortaPadrao = Configuracao.PortaPainel;

    /// <summary>
    /// Maior resposta aceita de outra máquina. Uma máquina com anos de festas manda poucas centenas de KB; mais que
    /// isso é outra coisa respondendo na porta (e ficaria guardada e indo para os celulares).
    /// </summary>
    internal const int MaiorResposta = 4 * 1024 * 1024;

    /// <summary>Máquina que recusou o PIN: fica de fora e sem perguntas por um tempo (senão ela bloqueia este tablet).</summary>
    internal static readonly TimeSpan EsperaOutroPin = TimeSpan.FromMinutes(6);

    /// <summary>
    /// Sem a lista de endereços, quem abre o painel pela primeira vez espera a primeira procura na rede (perguntar aos
    /// 253 vizinhos leva uns 6 s) para já ver as outras máquinas. As procuras seguintes não fazem ninguém esperar.
    /// </summary>
    internal static readonly TimeSpan EsperaPelaProcura = TimeSpan.FromSeconds(8);

    private readonly Leitor _leitor;
    private readonly int _porta;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Vizinha> _vizinhas = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _recusadas = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _parar = new();
    private readonly object _trava = new();
    private DateTime _ultimaProcura = DateTime.MinValue;
    private DateTime _fimDaRodada = DateTime.MinValue;
    private string? _pinUsado;
    private Task? _rodada;
    private Task _procura = Task.CompletedTask;
    private Task? _primeiraProcura;

    private sealed class Vizinha
    {
        public EstadoMaquina? Estado;
        public string? Marca;
        /// <summary>Respondeu na última vez que alguém atualizou o painel.</summary>
        public bool Respondeu;
        public DateTime UltimaResposta = DateTime.MinValue;
        public DateTime PrimeiraFalha = DateTime.MinValue;
        /// <summary>Digitada nas configurações (sai quando é tirada da lista); falso = achada na rede.</summary>
        public bool Digitada;
    }

    public Rede(Leitor leitor, int porta, TimeSpan? espera = null)
    {
        _leitor = leitor;
        _porta = porta;
        _http = new HttpClient { Timeout = espera ?? TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = MaiorResposta };
    }

    /// <summary>
    /// Dois celulares atualizando quase juntos (ou um toque duplo no Atualizar) usam a mesma consulta às outras
    /// máquinas, em vez de perguntar tudo de novo.
    /// </summary>
    public TimeSpan Reaproveitar { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Procura as outras máquinas na rede quando a lista de endereços está em branco. Desligada com o painel ouvindo
    /// só o próprio computador (testes): ninguém da rede chegaria nele.
    /// </summary>
    public bool ProcurarNaRede { get; init; } = true;

    /// <summary>Endereços perguntados na procura (os testes trocam por uma rede de mentira).</summary>
    internal Func<IEnumerable<string>>? Candidatos { get; init; }

    /// <summary>Uma procura na rede a cada tanto tempo, no máximo.</summary>
    internal TimeSpan IntervaloDaProcura { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Pergunta agora às outras máquinas como estão as vendas (o celular abriu o painel ou tocou em Atualizar).
    /// Termina quando todas responderam ou desistiu das que não responderam a tempo (2 s).
    /// </summary>
    public Task Atualizar()
    {
        lock (_trava)
        {
            if (_rodada is { } atual && (!atual.IsCompleted || DateTime.UtcNow - _fimDaRodada < Reaproveitar))
                return atual;
            return _rodada = Task.Run(() => Rodada(_parar.Token));
        }
    }

    /// <summary>Esta máquina e as outras, com os números da última vez que responderam.</summary>
    public EstadoEvento Evento()
    {
        var (local, _) = _leitor.Ler();
        var agora = DateTime.UtcNow;
        var maquinas = new List<MaquinaNoPainel> { new("Esta máquina", true, true, 0, local) };
        foreach (var (endereco, v, estado) in Visiveis(local))
        {
            var semResposta = (int)(agora - v.UltimaResposta).TotalSeconds;
            maquinas.Add(new MaquinaNoPainel(endereco, false, v.Respondeu, semResposta, estado));
        }
        maquinas = maquinas.OrderBy(m => m.Estado.Caixa).ThenBy(m => m.Endereco, StringComparer.Ordinal).ToList();
        return new EstadoEvento(local.NomeEvento, Leitor.Agora(), maquinas.Count(m => m.Online), maquinas.Count,
            Juntar(maquinas.Select(m => m.Estado.CaixaAberto)), Juntar(maquinas.Select(m => m.Estado.TodoEvento)),
            maquinas);
    }

    /// <summary>Marca do evento: muda quando qualquer máquina muda (ou entra/sai da rede).</summary>
    public string Marca()
    {
        var (local, marca) = _leitor.Ler();
        var partes = Visiveis(local).OrderBy(x => x.Endereco, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Endereco}:{x.V.Marca}:{(x.V.Respondeu ? 1 : 0)}");
        return marca + "|" + string.Join("|", partes);
    }

    /// <summary>
    /// As outras máquinas, cada uma uma vez só: a mesma máquina achada em dois endereços (dois adaptadores, nome e
    /// IP na lista, IP novo dado pelo roteador) entra pelo endereço que está respondendo, com os números mais novos
    /// (o relógio é o da própria máquina) e, empatado, sempre pelo mesmo endereço (a marca não fica mudando à toa).
    /// Esta máquina vista por outro endereço fica de fora.
    /// </summary>
    /// <param name="podar">Tira da lista os endereços velhos achados na rede (a máquina já responde em outro).</param>
    private List<(string Endereco, Vizinha V, EstadoMaquina Estado)> Visiveis(EstadoMaquina local, bool podar = false)
    {
        static bool Online(Vizinha v) => v.Respondeu;
        bool Melhor((string Endereco, Vizinha V, EstadoMaquina Estado) a, (string Endereco, Vizinha V, EstadoMaquina Estado) b)
        {
            if (Online(a.V) != Online(b.V)) return Online(a.V);
            // Programa reaberto (outra instância): o endereço antigo parou de responder, vale o que respondeu por último
            if (a.Estado.Instancia != b.Estado.Instancia && a.V.UltimaResposta != b.V.UltimaResposta)
                return a.V.UltimaResposta > b.V.UltimaResposta;
            // A mesma instância em dois endereços: os números mais novos e, empatado, sempre o mesmo endereço
            var novo = string.CompareOrdinal(a.Estado.Agora, b.Estado.Agora);
            if (novo != 0) return novo > 0;
            return string.CompareOrdinal(a.Endereco, b.Endereco) < 0;
        }

        var escolhidas = new Dictionary<string, (string Endereco, Vizinha V, EstadoMaquina Estado)>(StringComparer.Ordinal);
        var perdedoras = new List<(string Endereco, Vizinha V)>();
        foreach (var (endereco, v) in _vizinhas)
        {
            if (v.Estado is not { } estado) continue;
            if (estado.Instancia == local.Instancia || (estado.Maquina.Length > 0 && estado.Maquina == local.Maquina)) continue;
            var chave = estado.Maquina.Length > 0 ? estado.Maquina : estado.Instancia;
            var esta = (endereco, v, estado);
            if (escolhidas.TryGetValue(chave, out var outra))
            {
                if (!Melhor(esta, outra))
                {
                    perdedoras.Add((endereco, v));
                    continue;
                }
                perdedoras.Add((outra.Endereco, outra.V));
            }
            escolhidas[chave] = esta;
        }
        if (podar)
            foreach (var (endereco, v) in perdedoras)
                if (!v.Digitada && !Online(v)) _vizinhas.TryRemove(endereco, out _);
        return escolhidas.Values.ToList();
    }

    /// <summary>
    /// Uma consulta a todas as outras máquinas, ao mesmo tempo. Sem a lista de endereços, procura as máquinas na rede
    /// (no máximo uma vez por minuto); só a primeira procura faz o celular esperar.
    /// </summary>
    private async Task Rodada(CancellationToken parar)
    {
        try
        {
            Procurar(parar);
            if (_primeiraProcura is { IsCompleted: false } primeira)
            {
                try
                {
                    await primeira.WaitAsync(EsperaPelaProcura, parar);
                }
                catch (TimeoutException)
                {
                    // Consulta as que já achou; as outras entram no próximo Atualizar
                }
            }
            await Task.WhenAll(_vizinhas.Keys.ToList().Select(e => Consultar(e, parar)));
            Visiveis(_leitor.Ler().Estado, podar: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Registro.Erro("Rede", e);
        }
        finally
        {
            // Dentro da trava: no tablet de 32 bits a data não é gravada de uma vez só
            lock (_trava) _fimDaRodada = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// As máquinas digitadas nas configurações (as tiradas da lista saem do painel); sem nenhuma, procura na rede
    /// deste tablet (no máximo uma vez por minuto, só quando alguém atualiza o painel).
    /// </summary>
    private void Procurar(CancellationToken parar)
    {
        var config = _leitor.Config;
        if (config.PainelPin != _pinUsado)
        {
            // PIN novo: quem recusou o antigo pode aceitar este
            _recusadas.Clear();
            _pinUsado = config.PainelPin;
        }
        foreach (var (e, ate) in _recusadas)
            if (ate < DateTime.UtcNow) _recusadas.TryRemove(e, out _);

        var digitadas = Enderecos(config.PainelMaquinas, _porta);
        foreach (var e in digitadas)
            if (!_recusadas.ContainsKey(e)) _vizinhas.GetOrAdd(e, _ => new Vizinha()).Digitada = true;
        foreach (var (e, v) in _vizinhas)
        {
            // Com lista, só as da lista; sem lista, só as achadas na rede
            var fica = digitadas.Count > 0 ? digitadas.Contains(e, StringComparer.OrdinalIgnoreCase) : !v.Digitada;
            if (!fica) _vizinhas.TryRemove(e, out _);
        }
        if (!ProcurarNaRede || digitadas.Count > 0 || !_procura.IsCompleted ||
            DateTime.UtcNow - _ultimaProcura < IntervaloDaProcura) return;
        _ultimaProcura = DateTime.UtcNow;
        _procura = Task.Run(() => ProcurarVizinhos(parar), parar);
        _primeiraProcura ??= _procura;
    }

    /// <summary>Pergunta "quem é você?" aos 253 vizinhos de cada rede deste tablet (32 de cada vez).</summary>
    private async Task ProcurarVizinhos(CancellationToken parar)
    {
        try
        {
            var candidatos = (Candidatos?.Invoke() ?? RedeLocal().SelectMany(ip => Vizinhos(ip).Select(v => $"{v}:{_porta}")))
                .Where(e => !_vizinhas.ContainsKey(e) && !_recusadas.ContainsKey(e)).ToList();
            using var limite = new SemaphoreSlim(32);
            using var rapido = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700), MaxResponseContentBufferSize = 64 * 1024 };
            await Task.WhenAll(candidatos.Select(async e =>
            {
                await limite.WaitAsync(parar);
                try
                {
                    var info = await rapido.GetFromJsonAsync<Info>($"http://{e}/api/v1/info", Json.Opcoes, parar);
                    if (info is { Ativo: true } && info.Instancia != _leitor.Instancia) _vizinhas.TryAdd(e, new Vizinha());
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                               or NotSupportedException or InvalidOperationException)
                {
                    // Ninguém nesse endereço (ou não é um painel do BC Fichas)
                }
                finally
                {
                    limite.Release();
                }
            }));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Registro.Erro("Procurar as máquinas", e);
        }
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
                v.Respondeu = true;
                return;
            }
            if (resposta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                // Outro evento (PIN diferente): fica de fora, e sem perguntar de novo a cada Atualizar (5 PINs
                // errados seguidos fazem a outra máquina bloquear este tablet por 5 minutos)
                _recusadas[endereco] = DateTime.UtcNow + EsperaOutroPin;
                _vizinhas.TryRemove(endereco, out _);
                return;
            }
            resposta.EnsureSuccessStatusCode();
            v.Estado = await resposta.Content.ReadFromJsonAsync<EstadoMaquina>(Json.Opcoes, parar);
            v.Marca = resposta.Headers.ETag?.Tag.Trim('"');
            v.UltimaResposta = DateTime.UtcNow;
            v.Respondeu = true;
            v.PrimeiraFalha = DateTime.MinValue;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or NotSupportedException or InvalidOperationException)
        {
            // Sem resposta: continua com os últimos números. Achada na procura e sumida há 10 min: sai da lista.
            v.Respondeu = false;
            if (v.PrimeiraFalha == DateTime.MinValue) v.PrimeiraFalha = DateTime.UtcNow;
            if (v.Estado is null && DateTime.UtcNow - v.PrimeiraFalha > TimeSpan.FromMinutes(10) && !v.Digitada)
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
            _rodada?.Wait(TimeSpan.FromSeconds(2));
            _procura.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _http.Dispose();
        _parar.Dispose();
    }
}

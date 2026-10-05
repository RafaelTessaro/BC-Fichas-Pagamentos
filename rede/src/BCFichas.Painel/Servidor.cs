using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BCFichas.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BCFichas.Painel;

internal static class Json
{
    public static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>
/// O servidor do painel: a página do celular e as consultas. Só lê (não existe nenhuma rota que grave), só
/// responde a endereços de rede local e pede o PIN do evento.
/// </summary>
public sealed class Servidor : IAsyncDisposable
{
    public const string CabecalhoPin = "X-Pin";
    private const int TentativasErradas = 5;

    /// <summary>
    /// Conexões abertas ao mesmo tempo. Cada celular deixa uma ou mais abertas por alguns segundos depois de
    /// atualizar e as outras máquinas também: com 32, a equipe toda abrindo o painel junto deixava celular de fora.
    /// Conexão parada quase não usa memória.
    /// </summary>
    internal const int MaximoDeConexoes = 256;

    private readonly WebApplication _app;
    private readonly Leitor _leitor;
    private readonly Rede _rede;
    private readonly ConcurrentDictionary<IPAddress, (int Erros, DateTime Ate)> _bloqueios = new();
    /// <summary>O último JSON desta máquina, pela marca: várias máquinas perguntando, um JSON só.</summary>
    private Resposta? _ultimoEstado;

    private sealed record Resposta(string Marca, byte[] Json);

    private Servidor(WebApplication app, Leitor leitor, Rede rede)
    {
        _app = app;
        _leitor = leitor;
        _rede = rede;
    }

    public Leitor Leitor => _leitor;
    public Rede Rede => _rede;

    /// <summary>Porta em que ficou ouvindo (nos testes, uma livre qualquer).</summary>
    public int Porta { get; private set; }

    /// <param name="porta">0 = uma porta livre (testes).</param>
    public static async Task<Servidor> Iniciar(string pastaDados, int porta = Rede.PortaPadrao, bool soLocal = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxConcurrentConnections = MaximoDeConexoes;
            k.Limits.MaxRequestBodySize = 0;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            // Quem acabou de atualizar costuma tocar de novo logo: 15 s mantém a conexão e depois solta
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            k.AddServerHeader = false;
            if (soLocal) k.Listen(IPAddress.Loopback, porta);
            else k.Listen(IPAddress.Any, porta);
        });
        var app = builder.Build();
        var leitor = new Leitor(pastaDados, Guid.NewGuid().ToString("N")[..12]);
        var rede = new Rede(leitor, porta == 0 ? Rede.PortaPadrao : porta);
        var servidor = new Servidor(app, leitor, rede);
        servidor.Configurar();
        await app.StartAsync();
        servidor.Porta = new Uri(app.Urls.First()).Port;
        return servidor;
    }

    private void Configurar()
    {
        // Só a rede do evento (e o próprio tablet): nunca responde a um endereço de fora
        _app.Use(async (contexto, proximo) =>
        {
            var ip = contexto.Connection.RemoteIpAddress;
            if (ip is null || !EnderecoLocal(ip))
            {
                contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            contexto.Response.Headers.CacheControl = "no-cache";
            contexto.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await proximo(contexto);
        });

        // Quatro rotas fixas, num "switch" só: sem o roteamento das minimal APIs (que monta cada rota com árvores
        // de expressão ao abrir, carrega mais bibliotecas e pesa no tablet)
        _app.Run(async contexto =>
        {
            if (!HttpMethods.IsGet(contexto.Request.Method))
            {
                await Results.StatusCode(StatusCodes.Status405MethodNotAllowed).ExecuteAsync(contexto);
                return;
            }
            var caminho = contexto.Request.Path.Value is { Length: > 1 } p ? p.TrimEnd('/') : "/";
            IResult resposta = caminho.ToLowerInvariant() switch
            {
                "/" => Pagina("index.html"),
                "/api/v1/info" => Info(),
                "/api/v1/estado" => Recusar(contexto) ?? Estado(contexto),
                "/api/v1/evento" => Recusar(contexto) ?? await Evento(contexto),
                _ when caminho.LastIndexOf('/') == 0 => Pagina(caminho[1..]),
                _ => Results.NotFound(),
            };
            await resposta.ExecuteAsync(contexto);
        });
    }

    /// <summary>Quem é esta máquina (público: é como as outras se acham na rede; sem números).</summary>
    private IResult Info()
    {
        var c = _leitor.Config;
        return Results.Json(new Info(_leitor.Instancia, c.NumeroCaixa, c.NomeEvento, Leitor.Versao,
            c.PainelAtivo && Configuracao.PinValido(c.PainelPin)), Json.Opcoes);
    }

    /// <summary>
    /// Os números desta máquina (o que as outras máquinas perguntam). Com novidade, o JSON é montado uma vez para a
    /// marca nova e servido igual a todas as máquinas que perguntarem.
    /// </summary>
    private IResult Estado(HttpContext contexto)
    {
        var (estado, marca) = _leitor.Ler();
        if (NadaMudou(contexto, marca)) return Results.StatusCode(StatusCodes.Status304NotModified);
        var guardado = Volatile.Read(ref _ultimoEstado);
        if (guardado is null || guardado.Marca != marca)
        {
            guardado = new Resposta(marca, JsonSerializer.SerializeToUtf8Bytes(estado, Json.Opcoes));
            Volatile.Write(ref _ultimoEstado, guardado);
        }
        return Results.Bytes(guardado.Json, "application/json; charset=utf-8");
    }

    /// <summary>
    /// O evento todo (o que o celular pede ao abrir o painel e no botão Atualizar): pergunta na hora às outras
    /// máquinas e junta tudo. Montado a cada resposta: leva a hora da máquina, com que o celular calcula o "última
    /// venda há…".
    /// </summary>
    private async Task<IResult> Evento(HttpContext contexto)
    {
        await _rede.Atualizar();
        if (NadaMudou(contexto, Resumo(_rede.Marca()))) return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Json(_rede.Evento(), Json.Opcoes);
    }

    /// <summary>Marca (ETag) da resposta: o celular manda a última que recebeu e, sem novidade, não vem nada.</summary>
    private static bool NadaMudou(HttpContext contexto, string marca)
    {
        var etag = $"\"{marca}\"";
        contexto.Response.Headers.ETag = etag;
        return contexto.Request.Headers.IfNoneMatch.ToString() == etag;
    }

    /// <summary>
    /// Resumo curto de um texto (FNV-1a de 64 bits) para marca e identidade. Não é segredo nenhum, então não precisa
    /// de criptografia (que carrega a biblioteca do sistema só para isso).
    /// </summary>
    internal static string Resumo(string texto)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in texto)
        {
            hash = (hash ^ (byte)c) * 1099511628211UL;
            hash = (hash ^ (byte)(c >> 8)) * 1099511628211UL;
        }
        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>PIN errado ou faltando: não mostra nada. Cinco erros seguidos bloqueiam o endereço por 5 minutos.</summary>
    private IResult? Recusar(HttpContext contexto)
    {
        var ip = contexto.Connection.RemoteIpAddress ?? IPAddress.None;
        if (_bloqueios.TryGetValue(ip, out var b) && b.Erros >= TentativasErradas && b.Ate > DateTime.UtcNow)
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);

        var config = _leitor.Config;
        if (!config.PainelAtivo || !Configuracao.PinValido(config.PainelPin))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var pin = contexto.Request.Headers[CabecalhoPin].ToString();
        if (pin.Length == 0) pin = contexto.Request.Query["pin"].ToString();
        if (PinConfere(pin, config.PainelPin))
        {
            _bloqueios.TryRemove(ip, out _);
            return null;
        }
        _bloqueios.AddOrUpdate(ip, (1, DateTime.UtcNow.AddMinutes(5)),
            (_, atual) => (atual.Ate > DateTime.UtcNow ? atual.Erros + 1 : 1, DateTime.UtcNow.AddMinutes(5)));
        // Os endereços que erraram e foram embora não ficam guardados para sempre
        if (_bloqueios.Count > 256)
            foreach (var (velho, b2) in _bloqueios)
                if (b2.Ate <= DateTime.UtcNow) _bloqueios.TryRemove(velho, out _);
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    internal static bool PinConfere(string digitado, string certo) =>
        certo.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(digitado.Trim()),
            Encoding.UTF8.GetBytes(certo.Trim()));

    /// <summary>Endereço de rede local (Wi-Fi do evento) ou do próprio tablet.</summary>
    internal static bool EnderecoLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254);
    }

    private static readonly Dictionary<string, string> Tipos = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".webmanifest"] = "application/manifest+json",
    };

    /// <summary>Arquivos da página (dentro do programa).</summary>
    private static IResult Pagina(string arquivo)
    {
        if (!Tipos.TryGetValue(Path.GetExtension(arquivo), out var tipo)) return Results.NotFound();
        var recurso = Assembly.GetExecutingAssembly().GetManifestResourceStream("pagina/" + arquivo);
        return recurso is null ? Results.NotFound() : Results.Stream(recurso, tipo);
    }

    public async ValueTask DisposeAsync()
    {
        _rede.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _leitor.Dispose();
    }
}

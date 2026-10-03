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

    private readonly WebApplication _app;
    private readonly Leitor _leitor;
    private readonly Rede _rede;
    private readonly ConcurrentDictionary<IPAddress, (int Erros, DateTime Ate)> _bloqueios = new();

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
    public static async Task<Servidor> Iniciar(string pastaDados, int porta = Rede.PortaPadrao,
        TimeSpan? intervaloRede = null, bool soLocal = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxConcurrentConnections = 32;
            k.Limits.MaxRequestBodySize = 0;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            k.AddServerHeader = false;
            if (soLocal) k.Listen(IPAddress.Loopback, porta);
            else k.Listen(IPAddress.Any, porta);
        });
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        var app = builder.Build();
        var leitor = new Leitor(pastaDados, Guid.NewGuid().ToString("N")[..12]);
        var rede = new Rede(leitor, porta == 0 ? Rede.PortaPadrao : porta)
        {
            Intervalo = intervaloRede ?? TimeSpan.FromSeconds(3),
        };
        var servidor = new Servidor(app, leitor, rede);
        servidor.Configurar();
        await app.StartAsync();
        servidor.Porta = new Uri(app.Urls.First()).Port;
        rede.Iniciar();
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

        _app.MapGet("/", () => Pagina("index.html"));
        _app.MapGet("/{arquivo}", (string arquivo) => Pagina(arquivo));

        _app.MapGet("/api/v1/info", () =>
        {
            var c = _leitor.Config;
            return Results.Json(new Info(_leitor.Instancia, c.NumeroCaixa, c.NomeEvento, Leitor.Versao,
                c.PainelAtivo && Configuracao.PinValido(c.PainelPin)), Json.Opcoes);
        });

        _app.MapGet("/api/v1/estado", (HttpContext contexto) =>
        {
            if (Recusar(contexto) is { } recusa) return recusa;
            var (estado, marca) = _leitor.Ler();
            return ComMarca(contexto, marca, estado);
        });

        _app.MapGet("/api/v1/evento", (HttpContext contexto) =>
        {
            if (Recusar(contexto) is { } recusa) return recusa;
            _rede.AlguemOlhando();
            var marca = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_rede.Marca())))[..20];
            return ComMarca(contexto, marca, null);
        });
    }

    /// <summary>Resposta com marca (ETag): o celular manda a última que recebeu e, sem novidade, não vem nada.</summary>
    private IResult ComMarca(HttpContext contexto, string marca, object? corpo)
    {
        var etag = $"\"{marca}\"";
        contexto.Response.Headers.ETag = etag;
        if (contexto.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Json(corpo ?? _rede.Evento(), Json.Opcoes);
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
        var recurso = Assembly.GetExecutingAssembly().GetManifestResourceStream("pagina/" + arquivo);
        if (recurso is null || !Tipos.TryGetValue(Path.GetExtension(arquivo), out var tipo)) return Results.NotFound();
        return Results.Stream(recurso, tipo);
    }

    public async ValueTask DisposeAsync()
    {
        _rede.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _leitor.Dispose();
    }
}

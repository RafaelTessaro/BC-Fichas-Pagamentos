using System.Globalization;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace BCFichas.Core.Pagamento;

/// <summary>O caminho até o app ponte da maquininha: Bluetooth, porta COM ou rede (testes).</summary>
public interface IConexaoPonte
{
    /// <summary>Para mostrar na tela ("Bluetooth 00:11:22:33:44:55").</summary>
    string Descricao { get; }

    /// <summary>Abre a ligação. Lançar exceção = a maquininha não está ao alcance.</summary>
    Task<Stream> AbrirAsync(TimeSpan espera, CancellationToken cancelar);
}

public static class ConexaoPonte
{
    /// <summary>
    /// Só nos testes das telas: troca a ligação escrita na configuração (um endereço Bluetooth de verdade) por uma
    /// maquininha de mentira, já que o computador dos testes não tem Bluetooth.
    /// </summary>
    internal static Func<string, IConexaoPonte?>? Desvio { get; set; }

    /// <summary>
    /// A ligação escrita na configuração: endereço Bluetooth ("00:11:22:33:44:55"), porta COM ("COM7") ou rede
    /// ("tcp://127.0.0.1:9123", para testar o app ponte sem Bluetooth). Nulo se estiver em branco ou não for nenhuma.
    /// </summary>
    public static IConexaoPonte? Criar(string? ligacao)
    {
        var texto = (ligacao ?? "").Trim();
        if (texto.Length == 0) return null;
        if (Desvio?.Invoke(texto) is { } desviada) return desviada;
        if (BluetoothWindows.TentarLer(texto, out var endereco)) return new ConexaoBluetooth(endereco);
        if (texto.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(texto[3..], out _))
            return new ConexaoSerial(texto.ToUpperInvariant());
        if (texto.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(texto, UriKind.Absolute, out var uri) && uri.Port > 0)
            return new ConexaoTcp(uri.Host, uri.Port);
        return null;
    }
}

/// <summary>Bluetooth direto do Windows (RFCOMM), com o tablet e a maquininha pareados. Não usa porta COM.</summary>
public sealed class ConexaoBluetooth(ulong endereco) : IConexaoPonte
{
    public ulong Endereco { get; } = endereco;
    public string Descricao => "Bluetooth " + BluetoothWindows.Formatar(Endereco);

    public Task<Stream> AbrirAsync(TimeSpan espera, CancellationToken cancelar) =>
        BluetoothWindows.ConectarAsync(Endereco, ProtocoloPonte.ServicoBluetooth, espera, cancelar);
}

/// <summary>
/// Porta COM do Windows ligada à maquininha por Bluetooth ("Porta serial padrão por link Bluetooth"). Fica como
/// alternativa, para o tablet em que a ligação direta não funcionar.
/// </summary>
public sealed class ConexaoSerial(string porta) : IConexaoPonte
{
    public string Porta { get; } = porta;
    public string Descricao => "Porta " + Porta;

    public async Task<Stream> AbrirAsync(TimeSpan espera, CancellationToken cancelar)
    {
        var serial = new SerialPort(Porta, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = (int)espera.TotalMilliseconds,
            // Leitura sem limite: quem decide que a ligação caiu é o "ping" (a maquininha responde em segundos)
            ReadTimeout = SerialPort.InfiniteTimeout,
        };
        // Abrir a porta de um link Bluetooth já liga na maquininha (e espera por ela): fora da tela
        var abrir = Task.Run(serial.Open, CancellationToken.None);
        try
        {
            await abrir.WaitAsync(espera, cancelar);
            return serial.BaseStream;
        }
        catch (Exception)
        {
            // Desistiu de esperar: a porta ainda pode abrir depois e ficaria presa (a próxima tentativa daria "acesso
            // negado"). Fecha quando a abertura terminar, abrindo ou não.
            _ = abrir.ContinueWith(t =>
            {
                _ = t.Exception;
                serial.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
}

/// <summary>Pela rede: para testar o app ponte (ou um simulador) sem Bluetooth.</summary>
public sealed class ConexaoTcp(string host, int porta) : IConexaoPonte
{
    public string Descricao => $"Rede {host}:{porta.ToString(CultureInfo.InvariantCulture)}";

    public async Task<Stream> AbrirAsync(TimeSpan espera, CancellationToken cancelar)
    {
        var cliente = new TcpClient { NoDelay = true };
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancelar);
            limite.CancelAfter(espera);
            await cliente.ConnectAsync(host, porta, limite.Token);
            return new FluxoQueFechaJunto(cliente.GetStream(), cliente);
        }
        catch (OperationCanceledException) when (!cancelar.IsCancellationRequested)
        {
            // Passou o tempo (não foi quem chamou que desistiu)
            cliente.Dispose();
            throw new TimeoutException($"Ninguém atendeu em {Descricao} a tempo.");
        }
        catch (Exception)
        {
            cliente.Dispose();
            throw;
        }
    }
}

/// <summary>Um fluxo que, ao ser fechado, fecha também o que estava por trás dele (o socket, o cliente).</summary>
internal sealed class FluxoQueFechaJunto(Stream fluxo, IDisposable dono) : Stream
{
    public override bool CanRead => fluxo.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => fluxo.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => fluxo.Flush();
    public override int Read(byte[] buffer, int offset, int count) => fluxo.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => fluxo.Write(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            fluxo.Dispose();
            dono.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Endereço de um aparelho Bluetooth para o socket do Windows (SOCKADDR_BTH, 30 bytes).</summary>
internal sealed class EnderecoBluetooth(ulong endereco, Guid servico) : EndPoint
{
    public const AddressFamily Familia = (AddressFamily)32; // AF_BTH

    public override AddressFamily AddressFamily => Familia;

    public override SocketAddress Serialize()
    {
        // addressFamily (2 bytes) | btAddr (8) | serviceClassId (16, GUID) | port (4): 0 = procura pelo serviço
        var s = new SocketAddress(Familia, 30);
        var a = BitConverter.GetBytes(endereco);
        for (var i = 0; i < 8; i++) s[2 + i] = a[i];
        var g = servico.ToByteArray();
        for (var i = 0; i < 16; i++) s[10 + i] = g[i];
        return s;
    }

    public override EndPoint Create(SocketAddress socketAddress) => this;

    public override string ToString() => BluetoothWindows.Formatar(endereco);
}

using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace BCFichas.Core.Pagamento;

/// <summary>Um aparelho Bluetooth que o Windows conhece (pareado com o tablet).</summary>
public sealed record AparelhoBluetooth(string Nome, ulong Endereco, bool Conectado)
{
    public string EnderecoTexto => BluetoothWindows.Formatar(Endereco);
}

/// <summary>
/// Bluetooth do Windows sem biblioteca de fora: a lista de aparelhos pareados (bthprops) e a ligação RFCOMM pelo
/// próprio socket do Windows (AF_BTH). Funciona com o Bluetooth padrão do Windows 10, em 32 e 64 bits.
/// </summary>
public static class BluetoothWindows
{
    private const ProtocolType Rfcomm = (ProtocolType)3; // BTHPROTO_RFCOMM

    /// <summary>"00:11:22:33:44:55".</summary>
    public static string Formatar(ulong endereco) => string.Join(":",
        Enumerable.Range(0, 6).Select(i => ((endereco >> (8 * (5 - i))) & 0xFF).ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>Aceita "00:11:22:33:44:55", "00-11-22-33-44-55" e "001122334455".</summary>
    public static bool TentarLer(string texto, out ulong endereco)
    {
        endereco = 0;
        var limpo = texto.Trim().Replace(":", "").Replace("-", "");
        if (limpo.Length != 12) return false;
        return ulong.TryParse(limpo, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out endereco);
    }

    /// <summary>Aparelhos pareados com este tablet (vazio fora do Windows ou sem Bluetooth).</summary>
    public static List<AparelhoBluetooth> Pareados()
    {
        var lista = new List<AparelhoBluetooth>();
        if (!OperatingSystem.IsWindows()) return lista;
        try
        {
            var busca = new ParametrosDaBusca
            {
                Tamanho = (uint)Marshal.SizeOf<ParametrosDaBusca>(),
                Autenticados = 1,
                Lembrados = 1,
                Conectados = 1,
            };
            var info = new InfoDoAparelho { Tamanho = (uint)Marshal.SizeOf<InfoDoAparelho>() };
            var achados = BluetoothFindFirstDevice(ref busca, ref info);
            if (achados == IntPtr.Zero) return lista;
            try
            {
                do
                {
                    if (info.Autenticado != 0 || info.Lembrado != 0)
                    {
                        var nome = string.IsNullOrWhiteSpace(info.Nome) ? Formatar(info.Endereco) : info.Nome.Trim();
                        if (lista.All(a => a.Endereco != info.Endereco))
                            lista.Add(new AparelhoBluetooth(nome, info.Endereco, info.Conectado != 0));
                    }
                    info = new InfoDoAparelho { Tamanho = (uint)Marshal.SizeOf<InfoDoAparelho>() };
                } while (BluetoothFindNextDevice(achados, ref info));
            }
            finally
            {
                BluetoothFindDeviceClose(achados);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // Tablet sem Bluetooth (ou sem o Bluetooth do Windows): lista vazia
        }
        return lista.OrderBy(a => a.Nome, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Liga no serviço <paramref name="servico"/> do aparelho (o Windows acha o canal pelo serviço). Com o aparelho
    /// desligado ou longe, o Windows demora alguns segundos para desistir: depois de <paramref name="espera"/> desiste.
    /// </summary>
    public static async Task<Stream> ConectarAsync(ulong endereco, Guid servico, TimeSpan espera, CancellationToken cancelar)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Bluetooth só no Windows.");
        var socket = new Socket(EnderecoBluetooth.Familia, SocketType.Stream, Rfcomm);
        try
        {
            // A ligação Bluetooth do Windows não tem versão assíncrona confiável: espera fora da tela e, se passar do
            // tempo ou o operador desistir, fecha o socket (o que solta a espera)
            var ligar = Task.Run(() => socket.Connect(new EnderecoBluetooth(endereco, servico)), CancellationToken.None);
            // Desistiu antes: o erro que a ligação solta depois (socket fechado) não vai parar no registro de erros
            _ = ligar.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            await ligar.WaitAsync(espera, cancelar);
            return new FluxoQueFechaJunto(new NetworkStream(socket, ownsSocket: false), socket);
        }
        catch (Exception)
        {
            socket.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ParametrosDaBusca
    {
        public uint Tamanho;
        public int Autenticados;
        public int Lembrados;
        public int Desconhecidos;
        public int Conectados;
        public int Procurar;
        public byte TempoDaProcura;
        public IntPtr Radio;
    }

    /// <summary>BLUETOOTH_DEVICE_INFO (560 bytes em 32 e em 64 bits).</summary>
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 560)]
    internal struct InfoDoAparelho
    {
        [FieldOffset(0)] public uint Tamanho;
        [FieldOffset(8)] public ulong Endereco;
        [FieldOffset(16)] public uint Classe;
        [FieldOffset(20)] public int Conectado;
        [FieldOffset(24)] public int Lembrado;
        [FieldOffset(28)] public int Autenticado;
        // 32: última vez visto e 48: última vez usado (SYSTEMTIME de 16 bytes cada)
        [FieldOffset(64)] [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Nome;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref ParametrosDaBusca busca, ref InfoDoAparelho info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(IntPtr achados, ref InfoDoAparelho info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr achados);
}

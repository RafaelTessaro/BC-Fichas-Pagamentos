using System.IO.Ports;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace BCFichas.Core.Impressao;

/// <summary>Para onde vão as páginas impressas.</summary>
public interface IDestinoImpressao
{
    string Descricao { get; }
    void Imprimir(IReadOnlyList<SKBitmap> paginas);
}

/// <summary>Converte as páginas em ESC/POS e manda para a impressora.</summary>
public sealed class DestinoEscPos(string descricao, Action<byte[]> enviar, bool cortar) : IDestinoImpressao
{
    public string Descricao { get; } = descricao;

    public void Imprimir(IReadOnlyList<SKBitmap> paginas)
    {
        if (paginas.Count == 0) return;
        enviar(EscPos.Trabalho(paginas, cortar));
    }
}

/// <summary>Salva cada ficha como PNG (testar sem impressora).</summary>
public sealed class DestinoArquivo(string pasta) : IDestinoImpressao
{
    private static int _contador;

    public string Descricao => "Arquivo: " + Pasta;
    public string Pasta { get; } = pasta;

    public void Imprimir(IReadOnlyList<SKBitmap> paginas)
    {
        Directory.CreateDirectory(Pasta);
        var lote = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var pagina in paginas)
        {
            var n = Interlocked.Increment(ref _contador);
            using var mono = ImagemUtil.Monocromatico(pagina);
            ImagemUtil.SalvarPng(mono, Path.Combine(Pasta, $"{lote}-{n:0000}.png"));
        }
    }
}

/// <summary>Porta COM: a Elgin i9 USB aparece como porta serial no Windows.</summary>
public static class TransporteSerial
{
    public static void Enviar(string porta, int baudRate, byte[] dados)
    {
        using var serial = new SerialPort(porta, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = 15000,
            DtrEnable = true,
            RtsEnable = true,
        };
        try
        {
            serial.Open();
        }
        catch (Exception e)
        {
            throw new ErroDeNegocio($"Não consegui abrir a porta {porta}: {e.Message}");
        }

        const int pedaco = 4096;
        for (var i = 0; i < dados.Length; i += pedaco)
            serial.Write(dados, i, Math.Min(pedaco, dados.Length - i));
        serial.BaseStream.Flush();
    }

    public static string[] Portas()
    {
        try
        {
            return SerialPort.GetPortNames().OrderBy(p => p.Length).ThenBy(p => p).ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }
}

/// <summary>Impressora instalada no Windows: manda bytes crus (RAW) pelo spooler.</summary>
public static class TransporteWindows
{
    public static void Enviar(string impressora, byte[] dados, string nomeDocumento = "BC Fichas")
    {
        if (!OperatingSystem.IsWindows())
            throw new ErroDeNegocio("Impressora do Windows só funciona no Windows. Use 'Arquivo' para testar.");
        if (string.IsNullOrWhiteSpace(impressora))
            throw new ErroDeNegocio("Escolha a impressora nas configurações.");

        if (!OpenPrinter(impressora, out var handle, IntPtr.Zero))
            throw new ErroDeNegocio($"Não encontrei a impressora \"{impressora}\".");
        try
        {
            var documento = new DocInfo1 { NomeDocumento = nomeDocumento, TipoDados = "RAW" };
            if (StartDocPrinter(handle, 1, ref documento) == 0)
                throw new ErroDeNegocio($"A impressora \"{impressora}\" recusou o trabalho (erro {Marshal.GetLastWin32Error()}).");
            try
            {
                StartPagePrinter(handle);
                var ponteiro = Marshal.AllocHGlobal(dados.Length);
                try
                {
                    Marshal.Copy(dados, 0, ponteiro, dados.Length);
                    if (!WritePrinter(handle, ponteiro, dados.Length, out var escritos) || escritos != dados.Length)
                        throw new ErroDeNegocio($"Falha ao enviar para a impressora (erro {Marshal.GetLastWin32Error()}).");
                }
                finally
                {
                    Marshal.FreeHGlobal(ponteiro);
                }
                EndPagePrinter(handle);
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>Impressoras instaladas no Windows.</summary>
    public static List<string> Impressoras()
    {
        var lista = new List<string>();
        if (!OperatingSystem.IsWindows()) return lista;

        const int local = 2, conexoes = 4;
        EnumPrinters(local | conexoes, null, 4, IntPtr.Zero, 0, out var necessario, out _);
        if (necessario <= 0) return lista;

        var buffer = Marshal.AllocHGlobal(necessario);
        try
        {
            if (!EnumPrinters(local | conexoes, null, 4, buffer, necessario, out _, out var quantidade)) return lista;
            var tamanho = Marshal.SizeOf<PrinterInfo4>();
            for (var i = 0; i < quantidade; i++)
            {
                var info = Marshal.PtrToStructure<PrinterInfo4>(buffer + i * tamanho);
                if (!string.IsNullOrEmpty(info.Nome)) lista.Add(info.Nome);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return lista;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string NomeDocumento;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Arquivo;
        [MarshalAs(UnmanagedType.LPWStr)] public string TipoDados;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo4
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Nome;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Servidor;
        public int Atributos;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string nome, out IntPtr handle, IntPtr padrao);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr handle, int nivel, ref DocInfo1 documento);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, IntPtr dados, int tamanho, out int escritos);

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumPrinters(int flags, string? nome, int nivel, IntPtr buffer, int tamanho,
        out int necessario, out int quantidade);
}

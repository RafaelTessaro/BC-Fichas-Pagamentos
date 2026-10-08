using System.Collections.Concurrent;
using System.IO.Ports;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace BCFichas.Core.Impressao;

/// <summary>Para onde vão as páginas impressas.</summary>
public interface IDestinoImpressao
{
    string Descricao { get; }

    /// <summary>
    /// Imprime as páginas na ordem. Cada página só existe enquanto é usada: quem chama a desenha na hora e a solta
    /// assim que o destino passa para a próxima (não guarde a página depois).
    /// </summary>
    void Imprimir(IEnumerable<SKBitmap> paginas);
}

/// <summary>Converte as páginas em ESC/POS e manda para a impressora.</summary>
public sealed class DestinoEscPos(string descricao, Action<byte[]> enviar, TipoCorte corte) : IDestinoImpressao
{
    public string Descricao { get; } = descricao;

    public void Imprimir(IEnumerable<SKBitmap> paginas)
    {
        var trabalho = EscPos.Trabalho(paginas, corte, out var quantidade);
        if (quantidade > 0) enviar(trabalho);
    }
}

/// <summary>Salva cada ficha como PNG (testar sem impressora).</summary>
public sealed class DestinoArquivo(string pasta) : IDestinoImpressao
{
    private static int _contador;

    public string Descricao => "Arquivo: " + Pasta;
    public string Pasta { get; } = pasta;

    public void Imprimir(IEnumerable<SKBitmap> paginas)
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

/// <summary>A impressora parou no meio de um trabalho: as primeiras <see cref="Enviadas"/> páginas foram inteiras.</summary>
public sealed class FalhaNoMeio(int enviadas, Exception causa) : Exception(causa.Message, causa)
{
    public int Enviadas { get; } = enviadas;
}

/// <summary>
/// Porta COM, uma ficha de cada vez. Se a impressora parar no meio (papel acabou, tampa aberta, cabo solto), avisa
/// quantas fichas foram mandadas inteiras: elas saíram (ou saem quando o papel for trocado) e não podem sair de novo.
/// No envio seguinte completa com zeros a imagem que ficou pela metade dentro da impressora (saem linhas em branco):
/// senão o começo do próximo trabalho seria lido como desenho e a primeira ficha sairia embaralhada.
/// </summary>
public sealed class DestinoSerial(string porta, Func<Stream> abrir, TipoCorte corte) : IDestinoImpressao
{
    private const int Pedaco = 4096;

    /// <summary>Por porta: quantos bytes faltaram da última imagem que ficou pela metade.</summary>
    private static readonly ConcurrentDictionary<string, int> Faltaram = new(StringComparer.OrdinalIgnoreCase);

    public string Descricao => "Porta " + porta;

    public void Imprimir(IEnumerable<SKBitmap> paginas)
    {
        using var saida = abrir();
        if (Faltaram.TryRemove(porta, out var faltam) && faltam > 0)
            Mandar(saida, new byte[faltam], 0, enviadas: 0);
        var enviadas = 0;
        foreach (var pagina in paginas)
        {
            var bytes = EscPos.Trabalho([pagina], corte);
            // Só a primeira começa reiniciando a impressora (as outras seguem, como num trabalho só)
            Mandar(saida, bytes, enviadas == 0 ? 0 : EscPos.Inicializar().Length, enviadas);
            enviadas++;
        }
        try
        {
            saida.Flush();
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            throw new FalhaNoMeio(enviadas, e);
        }
    }

    private void Mandar(Stream saida, byte[] bytes, int inicio, int enviadas)
    {
        var escritos = inicio;
        try
        {
            for (var i = inicio; i < bytes.Length; i += Pedaco)
            {
                var n = Math.Min(Pedaco, bytes.Length - i);
                saida.Write(bytes, i, n);
                escritos = i + n;
            }
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            // O pedaço que estava indo pode ter entrado em parte: completa a mais (fora da imagem, zero não faz nada)
            Faltaram[porta] = bytes.Length - escritos;
            throw new FalhaNoMeio(enviadas, e);
        }
    }
}

/// <summary>Porta COM: a Elgin i9 USB aparece como porta serial no Windows.</summary>
public static class TransporteSerial
{
    /// <summary>Abre a porta para escrever (fechar o que volta fecha a porta).</summary>
    public static Stream Abrir(string porta, int baudRate)
    {
        var serial = new SerialPort(porta, baudRate, Parity.None, 8, StopBits.One)
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
            serial.Dispose();
            throw new ErroDeNegocio($"Não consegui abrir a porta {porta}: {e.Message}");
        }
        return new PortaAberta(serial);
    }

    private sealed class PortaAberta(SerialPort serial) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => serial.Write(buffer, offset, count);
        public override void Flush() => serial.BaseStream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) serial.Dispose();
            base.Dispose(disposing);
        }
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
            var enviado = false;
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
                enviado = true;
            }
            finally
            {
                // Deu erro no meio: cancela o trabalho em vez de mandar imprimir a parte que chegou (o pedido fica
                // como não impresso e, ao imprimir de novo, não sairiam fichas repetidas)
                if (enviado) EndDocPrinter(handle);
                else AbortPrinter(handle);
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

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool AbortPrinter(IntPtr handle);

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

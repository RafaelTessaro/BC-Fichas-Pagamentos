using SkiaSharp;

namespace BCFichas.Core.Impressao;

/// <summary>Comandos ESC/POS (padrão Epson, aceito pela Elgin i9).</summary>
public static class EscPos
{
    /// <summary>Linhas de imagem por comando GS v 0 (blocos menores não estouram o buffer da impressora).</summary>
    private const int LinhasPorBloco = 128;

    public static byte[] Inicializar() => [0x1B, 0x40];

    public static byte[] Avancar(int linhas) => [0x1B, 0x64, (byte)Math.Clamp(linhas, 0, 255)];

    /// <summary>GS V 66 0: avança até a guilhotina e faz corte parcial.</summary>
    public static byte[] Cortar() => [0x1D, 0x56, 0x42, 0x00];

    /// <summary>Imagem em preto e branco (GS v 0), linha por linha, 1 bit por ponto.</summary>
    public static byte[] Imagem(SKBitmap bitmap)
    {
        var largura = bitmap.Width;
        var altura = bitmap.Height;
        var bytesPorLinha = (largura + 7) / 8;
        var lum = ImagemUtil.Luminancia(bitmap);

        using var saida = new MemoryStream(bytesPorLinha * altura + altura / LinhasPorBloco * 8 + 16);
        for (var inicio = 0; inicio < altura; inicio += LinhasPorBloco)
        {
            var linhas = Math.Min(LinhasPorBloco, altura - inicio);
            saida.Write([0x1D, 0x76, 0x30, 0x00,
                (byte)(bytesPorLinha & 0xFF), (byte)(bytesPorLinha >> 8),
                (byte)(linhas & 0xFF), (byte)(linhas >> 8)]);

            var linha = new byte[bytesPorLinha];
            for (var y = inicio; y < inicio + linhas; y++)
            {
                Array.Clear(linha);
                var deslocamento = y * largura;
                for (var x = 0; x < largura; x++)
                {
                    if (lum[deslocamento + x] < ImagemUtil.Limiar)
                        linha[x >> 3] |= (byte)(0x80 >> (x & 7));
                }
                saida.Write(linha);
            }
        }
        return saida.ToArray();
    }

    /// <summary>Monta o trabalho completo: cada página é impressa e cortada.</summary>
    public static byte[] Trabalho(IReadOnlyList<SKBitmap> paginas, bool cortar)
    {
        using var saida = new MemoryStream();
        saida.Write(Inicializar());
        foreach (var pagina in paginas)
        {
            saida.Write(Imagem(pagina));
            if (cortar)
            {
                saida.Write(Avancar(1));
                saida.Write(Cortar());
            }
            else
            {
                saida.Write(Avancar(5));
            }
        }
        return saida.ToArray();
    }
}

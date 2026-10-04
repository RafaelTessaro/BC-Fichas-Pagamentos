using SkiaSharp;

namespace BCFichas.Core.Impressao;

/// <summary>Comandos ESC/POS (padrão Epson, aceito pela Elgin i9).</summary>
public static class EscPos
{
    /// <summary>Linhas de imagem por comando GS v 0 (blocos menores não estouram o buffer da impressora).</summary>
    private const int LinhasPorBloco = 128;

    public static byte[] Inicializar() => [0x1B, 0x40];

    public static byte[] Avancar(int linhas) => [0x1B, 0x64, (byte)Math.Clamp(linhas, 0, 255)];

    /// <summary>ESC J n: avança n pontos (1/203 pol.).</summary>
    public static byte[] AvancarPontos(int pontos) => [0x1B, 0x4A, (byte)Math.Clamp(pontos, 0, 255)];

    /// <summary>
    /// GS V 66 0: avança até a guilhotina e faz corte parcial (a ficha fica presa por um ponto).
    /// GS V 65 0: o mesmo com corte total.
    /// </summary>
    public static byte[] Cortar(bool parcial = true) => [0x1D, 0x56, (byte)(parcial ? 0x42 : 0x41), 0x00];

    /// <summary>Imagem em preto e branco (GS v 0), linha por linha, 1 bit por ponto.</summary>
    public static byte[] Imagem(SKBitmap bitmap)
    {
        var bytesPorLinha = (bitmap.Width + 7) / 8;
        using var saida = new MemoryStream(bytesPorLinha * bitmap.Height + bitmap.Height / LinhasPorBloco * 8 + 16);
        EscreverImagem(saida, bitmap);
        return saida.ToArray();
    }

    /// <summary>
    /// Escreve a imagem (GS v 0) direto no trabalho, lendo os pontos da imagem linha por linha. Antes fazia uma cópia
    /// em tons de cinza do tamanho da ficha (centenas de KB por ficha, na área de objetos grandes) e mais uma cópia
    /// dos bytes de cada ficha: no tablet o coletor de memória parava o programa a cada poucas fichas.
    /// </summary>
    private static void EscreverImagem(Stream saida, SKBitmap bitmap)
    {
        var largura = bitmap.Width;
        var altura = bitmap.Height;
        var bytesPorLinha = (largura + 7) / 8;
        using var rgba = ImagemUtil.CopiaRgba(bitmap);
        var pixels = (rgba ?? bitmap).GetPixelSpan();

        var linha = new byte[bytesPorLinha];
        for (var inicio = 0; inicio < altura; inicio += LinhasPorBloco)
        {
            var linhas = Math.Min(LinhasPorBloco, altura - inicio);
            saida.Write([0x1D, 0x76, 0x30, 0x00,
                (byte)(bytesPorLinha & 0xFF), (byte)(bytesPorLinha >> 8),
                (byte)(linhas & 0xFF), (byte)(linhas >> 8)]);

            for (var y = inicio; y < inicio + linhas; y++)
            {
                Array.Clear(linha);
                var deslocamento = y * largura * 4;
                for (var x = 0; x < largura; x++)
                {
                    if (ImagemUtil.Luz(pixels, deslocamento + x * 4) < ImagemUtil.Limiar)
                        linha[x >> 3] |= (byte)(0x80 >> (x & 7));
                }
                saida.Write(linha);
            }
        }
    }

    /// <summary>Monta o trabalho completo: cada página (ficha) é impressa e a guilhotina é acionada.</summary>
    public static byte[] Trabalho(IEnumerable<SKBitmap> paginas, TipoCorte corte) => Trabalho(paginas, corte, out _);

    /// <summary>Todas as páginas num trabalho só (cada uma vira ESC/POS e pode ser solta logo em seguida).</summary>
    public static byte[] Trabalho(IEnumerable<SKBitmap> paginas, TipoCorte corte, out int quantidade)
    {
        quantidade = 0;
        using var saida = new MemoryStream();
        saida.Write(Inicializar());
        foreach (var pagina in paginas)
        {
            quantidade++;
            EscreverImagem(saida, pagina);
            if (corte == TipoCorte.Nenhum)
            {
                saida.Write(Avancar(5));
                continue;
            }
            // Um pouco de folga depois da última linha e o corte (parcial deixa a ficha presa).
            saida.Write(AvancarPontos(12));
            saida.Write(Cortar(parcial: corte == TipoCorte.Parcial));
        }
        return saida.ToArray();
    }
}

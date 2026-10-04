using System.Security.Cryptography;
using SkiaSharp;

namespace BCFichas.Core.Impressao;

public static class ImagemUtil
{
    /// <summary>Abaixo disso (0-255) o ponto sai preto na impressora térmica.</summary>
    public const int Limiar = 150;

    public static SKBitmap? Carregar(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho) || !File.Exists(caminho)) return null;
        try
        {
            return SKBitmap.Decode(caminho);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Redimensiona mantendo a proporção para caber em maxLargura x maxAltura.</summary>
    public static SKBitmap Ajustar(SKBitmap origem, int maxLargura, int maxAltura, bool podeAumentar = false)
    {
        var escala = Math.Min((double)maxLargura / origem.Width, (double)maxAltura / origem.Height);
        if (!podeAumentar) escala = Math.Min(escala, 1);
        var largura = Math.Max(1, (int)Math.Round(origem.Width * escala));
        var altura = Math.Max(1, (int)Math.Round(origem.Height * escala));

        var destino = new SKBitmap(new SKImageInfo(largura, altura, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(destino);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(origem, new SKRect(0, 0, largura, altura), paint);
        return destino;
    }

    /// <summary>
    /// Copia uma imagem escolhida pelo usuário para <paramref name="pasta"/>, já reduzida (economiza memória do
    /// tablet). O nome do arquivo vem do conteúdo da imagem: escolher a mesma foto de novo (em outro produto ou no
    /// mesmo) usa o arquivo que já existe, sem fazer outra cópia. Devolve o nome do arquivo.
    /// </summary>
    public static string Importar(string origem, string pasta, string prefixo, int tamanhoMaximo)
    {
        using var imagem = DecodificarReduzida(origem, tamanhoMaximo)
                           ?? throw new ErroDeNegocio("Não consegui abrir esta imagem.");
        using var menor = Ajustar(imagem, tamanhoMaximo, tamanhoMaximo);
        var png = Png(menor);
        var nome = prefixo + Convert.ToHexString(SHA256.HashData(png))[..16].ToLowerInvariant() + ".png";
        Directory.CreateDirectory(pasta);
        var destino = Path.Combine(pasta, nome);
        if (!File.Exists(destino)) File.WriteAllBytes(destino, png);
        return nome;
    }

    /// <summary>
    /// Abre a imagem já perto do tamanho que vai ser usado: JPEG grande (foto de celular) é decodificado em 1/2,
    /// 1/4 ou 1/8 do tamanho, sem passar pela imagem inteira na memória.
    /// </summary>
    private static SKBitmap? DecodificarReduzida(string caminho, int tamanhoMaximo)
    {
        using var codec = SKCodec.Create(caminho);
        if (codec is null) return null;
        var lado = Math.Max(codec.Info.Width, codec.Info.Height);
        var escala = lado <= tamanhoMaximo ? 1f : Math.Max((float)tamanhoMaximo / lado, 1f / 8);
        var tamanho = codec.GetScaledDimensions(escala);
        var info = new SKImageInfo(tamanho.Width, tamanho.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var resultado = codec.GetPixels(info, bitmap.GetPixels());
        if (resultado is SKCodecResult.Success or SKCodecResult.IncompleteInput) return bitmap;
        bitmap.Dispose();
        return SKBitmap.Decode(caminho); // formato que não reduz na leitura: abre inteiro
    }

    public static void SalvarPng(SKBitmap bitmap, string caminho)
    {
        using var imagem = SKImage.FromBitmap(bitmap);
        using var dados = imagem.Encode(SKEncodedImageFormat.Png, 100);
        using var arquivo = File.Create(caminho);
        dados.SaveTo(arquivo);
    }

    public static byte[] Png(SKBitmap bitmap)
    {
        using var imagem = SKImage.FromBitmap(bitmap);
        using var dados = imagem.Encode(SKEncodedImageFormat.Png, 100);
        return dados.ToArray();
    }

    /// <summary>Luminância 0-255 de cada ponto, considerando transparência como branco.</summary>
    public static byte[] Luminancia(SKBitmap bitmap)
    {
        using var rgba = CopiaRgba(bitmap);
        var fonte = rgba ?? bitmap;
        var pixels = fonte.GetPixelSpan();
        var resultado = new byte[fonte.Width * fonte.Height];
        for (int i = 0, p = 0; i < resultado.Length; i++, p += 4)
            resultado[i] = Luz(pixels, p);
        return resultado;
    }

    /// <summary>
    /// Cópia em RGBA premultiplicado quando a imagem está em outro formato; nulo quando ela já pode ser lida direto
    /// (as fichas e os relatórios já são desenhados assim).
    /// </summary>
    internal static SKBitmap? CopiaRgba(SKBitmap bitmap) =>
        bitmap.ColorType == SKColorType.Rgba8888 && bitmap.AlphaType == SKAlphaType.Premul
            ? null
            : bitmap.Copy(SKColorType.Rgba8888);

    /// <summary>Luminância 0-255 do ponto que começa em <paramref name="p"/> (RGBA premultiplicado, transparente = branco).</summary>
    internal static byte Luz(ReadOnlySpan<byte> pixels, int p)
    {
        // Premultiplicado: compõe sobre branco.
        int a = pixels[p + 3];
        int r = pixels[p] + (255 - a);
        int g = pixels[p + 1] + (255 - a);
        int b = pixels[p + 2] + (255 - a);
        return (byte)Math.Clamp((r * 299 + g * 587 + b * 114) / 1000, 0, 255);
    }

    /// <summary>Converte uma foto/logo em preto e branco com pontilhado (Floyd-Steinberg).</summary>
    public static SKBitmap Pontilhar(SKBitmap origem)
    {
        var w = origem.Width;
        var h = origem.Height;
        var lum = Luminancia(origem);
        var erro = new float[w * h];
        for (var i = 0; i < lum.Length; i++) erro[i] = lum[i];

        var destino = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var saida = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var velho = erro[i];
                var preto = velho < 128;
                var novo = preto ? 0f : 255f;
                var e = velho - novo;
                if (x + 1 < w) erro[i + 1] += e * 7 / 16;
                if (y + 1 < h)
                {
                    if (x > 0) erro[i + w - 1] += e * 3 / 16;
                    erro[i + w] += e * 5 / 16;
                    if (x + 1 < w) erro[i + w + 1] += e * 1 / 16;
                }

                var v = (byte)(preto ? 0 : 255);
                saida[i * 4] = v;
                saida[i * 4 + 1] = v;
                saida[i * 4 + 2] = v;
                saida[i * 4 + 3] = 255;
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(saida, 0, destino.GetPixels(), saida.Length);
        return destino;
    }

    /// <summary>Exatamente como a impressora vai imprimir: só preto e branco.</summary>
    /// <remarks>
    /// Converte linha por linha, direto dos pontos da imagem: antes montava duas cópias do tamanho da ficha (cerca de
    /// 1 MB por ficha, na área de objetos grandes), o que fazia o coletor de memória parar o programa a cada poucas
    /// fichas.
    /// </remarks>
    public static SKBitmap Monocromatico(SKBitmap origem)
    {
        using var rgba = CopiaRgba(origem);
        var pixels = (rgba ?? origem).GetPixelSpan();
        var largura = origem.Width;
        var destino = new SKBitmap(new SKImageInfo(largura, origem.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var inicio = destino.GetPixels();
        var linha = new byte[largura * 4];
        for (var y = 0; y < origem.Height; y++)
        {
            for (int x = 0, p = y * largura * 4; x < largura; x++, p += 4)
            {
                var v = (byte)(Luz(pixels, p) < Limiar ? 0 : 255);
                linha[x * 4] = v;
                linha[x * 4 + 1] = v;
                linha[x * 4 + 2] = v;
                linha[x * 4 + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(linha, 0, inicio + y * linha.Length, linha.Length);
        }
        return destino;
    }
}

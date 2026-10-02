using SkiaSharp;

namespace BCFichas.Core.Impressao;

/// <summary>
/// Logotipo do evento já convertido para preto e branco, em cada tamanho que os modelos de ficha pedem.
/// </summary>
public sealed class LogoFicha : IDisposable
{
    private readonly SKBitmap _original;
    private readonly bool _desenho;
    private readonly Dictionary<(int, int), SKBitmap> _tamanhos = new();

    public LogoFicha(SKBitmap original)
    {
        _original = original;
        _desenho = EhDesenho(original);
    }

    /// <summary>Logo reduzido para caber em <paramref name="maxLargura"/> x <paramref name="maxAltura"/> pontos.</summary>
    public SKBitmap Obter(int maxLargura, int maxAltura)
    {
        if (_tamanhos.TryGetValue((maxLargura, maxAltura), out var pronto)) return pronto;
        using var ajustado = ImagemUtil.Ajustar(_original, maxLargura, maxAltura, podeAumentar: true);
        // Logo de traço (preto e branco) fica mais nítido sem pontilhado; foto precisa do pontilhado.
        var resultado = _desenho ? ImagemUtil.Monocromatico(ajustado) : ImagemUtil.Pontilhar(ajustado);
        _tamanhos[(maxLargura, maxAltura)] = resultado;
        return resultado;
    }

    public void Dispose()
    {
        foreach (var b in _tamanhos.Values) b.Dispose();
        _tamanhos.Clear();
        _original.Dispose();
    }

    /// <summary>Quase tudo preto ou branco (poucos tons de cinza) = desenho/logotipo.</summary>
    private static bool EhDesenho(SKBitmap imagem)
    {
        var lum = ImagemUtil.Luminancia(imagem);
        if (lum.Length == 0) return true;
        var meioTom = lum.Count(v => v is > 60 and < 200);
        return meioTom < lum.Length * 0.15;
    }
}

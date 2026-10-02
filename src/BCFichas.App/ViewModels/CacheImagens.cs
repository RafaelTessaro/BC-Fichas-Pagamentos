using Avalonia.Media.Imaging;

namespace BCFichas.App.ViewModels;

/// <summary>Imagens dos produtos já reduzidas, carregadas uma vez só (economiza memória).</summary>
internal static class CacheImagens
{
    private static readonly Dictionary<(string, DateTime, int), Bitmap> Cache = new();

    public static Bitmap? Obter(string? caminho, int largura)
    {
        if (string.IsNullOrWhiteSpace(caminho) || !File.Exists(caminho)) return null;
        try
        {
            var chave = (caminho, File.GetLastWriteTimeUtc(caminho), largura);
            if (Cache.TryGetValue(chave, out var bitmap)) return bitmap;

            using var arquivo = File.OpenRead(caminho);
            bitmap = Bitmap.DecodeToWidth(arquivo, largura, BitmapInterpolationMode.MediumQuality);
            if (Cache.Count > 200) Cache.Clear();
            Cache[chave] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

using Avalonia.Media.Imaging;

namespace BCFichas.App.ViewModels;

/// <summary>
/// Imagens dos produtos já reduzidas, carregadas uma vez só (economiza memória). Guarda no máximo
/// <see cref="Maximo"/> imagens (uma aba tem até 48 botões): a usada há mais tempo sai primeiro. Uma foto trocada
/// no mesmo arquivo substitui a antiga em vez de juntar as duas.
/// </summary>
internal static class CacheImagens
{
    /// <summary>Largura das fotos na tela de venda e no cadastro (a mesma, para os dois usarem a mesma imagem).</summary>
    public const int Largura = 240;

    private const int Maximo = 64;
    private static readonly Dictionary<string, (DateTime Alterada, Bitmap Imagem, long Uso)> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _uso;

    public static Bitmap? Obter(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho) || !File.Exists(caminho)) return null;
        try
        {
            var alterada = File.GetLastWriteTimeUtc(caminho);
            if (Cache.TryGetValue(caminho, out var guardada) && guardada.Alterada == alterada)
            {
                Cache[caminho] = guardada with { Uso = ++_uso };
                return guardada.Imagem;
            }

            Bitmap bitmap;
            using (var arquivo = File.OpenRead(caminho))
                bitmap = Bitmap.DecodeToWidth(arquivo, Largura, BitmapInterpolationMode.MediumQuality);
            // A imagem que sai do cache pode ainda estar na tela: não é descartada aqui, o coletor solta depois.
            if (Cache.Count >= Maximo && !Cache.ContainsKey(caminho))
                Cache.Remove(Cache.MinBy(x => x.Value.Uso).Key);
            Cache[caminho] = (alterada, bitmap, ++_uso);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Esquece as imagens (depois de restaurar ou zerar a programação, nada delas volta a ser usado).</summary>
    public static void Limpar() => Cache.Clear();
}

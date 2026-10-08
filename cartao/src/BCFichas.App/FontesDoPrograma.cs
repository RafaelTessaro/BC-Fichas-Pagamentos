using Avalonia;
using Avalonia.Media.Fonts;

namespace BCFichas.App;

/// <summary>
/// A letra Inter só nas espessuras que as telas usam (normal, média, seminegrito e negrito). O pacote do Avalonia
/// carregava também a fina e a leve, que ficavam na memória o dia todo sem uso. Licença: Assets/LICENCA-INTER.txt
/// (SIL Open Font License).
/// </summary>
internal static class FontesDoPrograma
{
    public static AppBuilder ComFontes(this AppBuilder app) => app.ConfigureFonts(fontes =>
        fontes.AddFontCollection(new EmbeddedFontCollection(new Uri("fonts:Inter", UriKind.Absolute),
            new Uri($"avares://{typeof(FontesDoPrograma).Assembly.GetName().Name}/Assets/Fontes", UriKind.Absolute))));
}

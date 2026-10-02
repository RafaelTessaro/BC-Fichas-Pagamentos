using System.Collections.Concurrent;
using SkiaSharp;

namespace BCFichas.Core.Impressao;

public static class Fontes
{
    private static readonly ConcurrentDictionary<(string, bool), SKTypeface> Cache = new();

    /// <summary>Fonte do texto comum das fichas e relatórios.</summary>
    public static SKTypeface Normal => Obter("Arial", false);
    public static SKTypeface Negrito => Obter("Arial", true);

    /// <summary>Fonte escolhida pelo usuário; se não existir no Windows, usa Arial negrito.</summary>
    public static SKTypeface Escolhida(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return Negrito;
        var fonte = Obter(nome, false);
        return Existe(nome) ? fonte : Negrito;
    }

    public static bool Existe(string nome) =>
        SKFontManager.Default.FontFamilies.Any(f => string.Equals(f, nome, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fontes instaladas, com as boas para ficha primeiro.</summary>
    public static List<string> Disponiveis()
    {
        string[] sugeridas =
            ["Impact", "Arial Black", "Arial", "Bahnschrift", "Segoe UI Black", "Segoe UI", "Tahoma", "Verdana", "Calibri"];
        var instaladas = SKFontManager.Default.FontFamilies
            .Where(f => !f.StartsWith('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var primeiro = sugeridas.Where(s => instaladas.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        return primeiro.Concat(instaladas.Where(f => !primeiro.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();
    }

    private static SKTypeface Obter(string nome, bool negrito) =>
        Cache.GetOrAdd((nome.ToLowerInvariant(), negrito), _ =>
            SKTypeface.FromFamilyName(nome, negrito ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default);
}

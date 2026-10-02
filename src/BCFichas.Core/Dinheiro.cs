using System.Text;

namespace BCFichas.Core;

/// <summary>
/// Valores em dinheiro são guardados sempre em centavos (long) para não ter erro de arredondamento.
/// </summary>
public static class Dinheiro
{
    /// <summary>Formata 123456 como "R$ 1.234,56".</summary>
    public static string Formatar(long centavos, bool simbolo = true)
    {
        var negativo = centavos < 0;
        var abs = Math.Abs(centavos);
        var reais = abs / 100;
        var cents = abs % 100;

        var digitos = reais.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        for (var i = 0; i < digitos.Length; i++)
        {
            if (i > 0 && (digitos.Length - i) % 3 == 0)
                sb.Append('.');
            sb.Append(digitos[i]);
        }
        sb.Append(',').Append(cents.ToString("00", System.Globalization.CultureInfo.InvariantCulture));

        var texto = sb.ToString();
        if (simbolo) texto = "R$ " + texto;
        return negativo ? "-" + texto : texto;
    }

    /// <summary>
    /// Lê "12", "12,5", "12,50", "1.234,56", "R$ 3,00" ou "12.50" (ponto como decimal quando só há 1 ou 2 casas).
    /// </summary>
    public static bool TentarLer(string? texto, out long centavos)
    {
        centavos = 0;
        if (string.IsNullOrWhiteSpace(texto)) return false;

        var t = texto.Replace("R$", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "").Trim();
        var negativo = t.StartsWith('-');
        if (negativo) t = t[1..];
        if (t.Length == 0) return false;

        string inteiro, fracao = "";
        var virgula = t.LastIndexOf(',');
        if (virgula >= 0)
        {
            inteiro = t[..virgula].Replace(".", "");
            fracao = t[(virgula + 1)..];
        }
        else
        {
            var ponto = t.LastIndexOf('.');
            if (ponto >= 0 && t.Length - ponto - 1 is 1 or 2 && t.IndexOf('.') == ponto)
            {
                inteiro = t[..ponto];
                fracao = t[(ponto + 1)..];
            }
            else
            {
                inteiro = t.Replace(".", "");
            }
        }

        if (inteiro.Length == 0) inteiro = "0";
        if (fracao.Length > 2) return false;
        if (!inteiro.All(char.IsAsciiDigit) || !fracao.All(char.IsAsciiDigit)) return false;
        if (inteiro.Length > 12) return false;

        fracao = fracao.PadRight(2, '0');
        centavos = long.Parse(inteiro) * 100 + long.Parse(fracao);
        if (negativo) centavos = -centavos;
        return true;
    }

    /// <summary>Texto para edição: 1250 vira "12,50".</summary>
    public static string ParaEdicao(long centavos) => Formatar(centavos, simbolo: false).Replace(".", "");
}

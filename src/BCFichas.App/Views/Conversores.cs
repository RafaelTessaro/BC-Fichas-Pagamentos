using Avalonia.Data.Converters;
using BCFichas.Core;

namespace BCFichas.App.Views;

public static class Conversores
{
    /// <summary>"500" (centavos) vira "R$ 5" para os botões de nota.</summary>
    public static readonly IValueConverter CentavosParaNota = new FuncValueConverter<string?, string>(texto =>
    {
        if (!long.TryParse(texto, out var centavos)) return texto ?? "";
        return centavos % 100 == 0 ? $"R$ {centavos / 100}" : Dinheiro.Formatar(centavos);
    });

    /// <summary>Tempo do simulador: 0 = espera o operador; 3 = "3 segundos".</summary>
    public static readonly IValueConverter Segundos = new FuncValueConverter<int, string>(s =>
        s == 0 ? "Nunca (eu toco em aprovar ou recusar)" : $"{s} segundos");
}

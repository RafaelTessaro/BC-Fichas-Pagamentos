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
    public static readonly IValueConverter Centavos = new FuncValueConverter<long, string>(c => Dinheiro.Formatar(c));

    /// <summary>"R$ 10" (sem centavos quando é redondo) para os atalhos de vale.</summary>
    public static readonly IValueConverter CentavosCurto = new FuncValueConverter<long, string>(c =>
        c % 100 == 0 ? $"R$ {c / 100}" : Dinheiro.Formatar(c));

    public static readonly IValueConverter Segundos = new FuncValueConverter<int, string>(s =>
        s == 0 ? "Nunca (eu toco em aprovar ou recusar)" : $"{s} segundos");
}

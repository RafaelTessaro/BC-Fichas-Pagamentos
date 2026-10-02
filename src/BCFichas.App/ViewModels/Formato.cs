using System.Globalization;

namespace BCFichas.App.ViewModels;

/// <summary>Datas sempre no padrão brasileiro, independente do idioma do Windows.</summary>
internal static class Formato
{
    public static string DataHora(DateTime d) => d.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    public static string Data(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    public static string Hora(DateTime d) => d.ToString("HH:mm", CultureInfo.InvariantCulture);
}

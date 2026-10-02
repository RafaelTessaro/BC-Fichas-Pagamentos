using System.Globalization;

namespace BCFichas.App.ViewModels;

/// <summary>Datas sempre no padrão brasileiro, independente do idioma do Windows.</summary>
internal static class Formato
{
    public static string DataHora(DateTime d) => d.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    public static string Data(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    public static string Hora(DateTime d) => d.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"Caixa 01"; caixas abertos antes da versão 3.1 tinham operador: "Caixa 01 • MARIA".</summary>
    public static string Caixa(int numero, string? operador) =>
        string.IsNullOrWhiteSpace(operador) ? $"Caixa {numero:00}" : $"Caixa {numero:00} • {operador}";
}

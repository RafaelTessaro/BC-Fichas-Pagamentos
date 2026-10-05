using System.Globalization;

namespace BCFichas.App;

/// <summary>Grava erros em dados/erros.log para facilitar o suporte.</summary>
internal static class Log
{
    private static readonly object Trava = new();

    public static string? Pasta { get; set; }

    public static void Erro(string contexto, Exception? erro)
    {
        if (Pasta is null) return;
        try
        {
            lock (Trava)
            {
                var arquivo = Path.Combine(Pasta, "erros.log");
                if (File.Exists(arquivo) && new FileInfo(arquivo).Length > 2_000_000)
                    File.Delete(arquivo);
                File.AppendAllText(arquivo,
                    $"[{DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)}] {contexto}: {erro}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Sem log não é motivo para derrubar o caixa.
        }
    }
}

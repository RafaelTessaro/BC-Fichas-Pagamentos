using System.Globalization;

namespace BCFichas.Painel;

/// <summary>Erros do painel num arquivo pequeno na pasta de dados (painel-erros.log, até 1 MB).</summary>
internal static class Registro
{
    private static readonly object Trava = new();
    private static DateTime _ultimo;

    public static string? Pasta { get; set; }

    public static void Erro(string contexto, Exception erro)
    {
        if (Pasta is null) return;
        try
        {
            lock (Trava)
            {
                // O mesmo erro repetido a cada pergunta do celular não enche o arquivo
                if (DateTime.UtcNow - _ultimo < TimeSpan.FromSeconds(30)) return;
                _ultimo = DateTime.UtcNow;
                var arquivo = Path.Combine(Pasta, "painel-erros.log");
                if (File.Exists(arquivo) && new FileInfo(arquivo).Length > 1_000_000) File.Delete(arquivo);
                File.AppendAllText(arquivo,
                    $"[{DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)}] {contexto}: {erro.Message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Sem log não é motivo para parar o painel.
        }
    }
}

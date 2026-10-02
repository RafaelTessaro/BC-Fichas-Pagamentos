using BCFichas.Core;

namespace BCFichas.Tests.Core;

/// <summary>Sistema completo numa pasta temporária, apagada no fim do teste.</summary>
public sealed class SistemaTemporario : IDisposable
{
    public SistemaTemporario(bool exemplos = true)
    {
        Pasta = Path.Combine(Path.GetTempPath(), "bcfichas-teste-" + Guid.NewGuid().ToString("N"));
        Sistema = Sistema.Iniciar(Pasta, exemplos);
        var config = Sistema.Config.Atual.Clonar();
        config.Impressora = TipoImpressora.Arquivo;
        config.PastaArquivo = Path.Combine(Pasta, "impressoes");
        Sistema.Config.Salvar(config);
    }

    public string Pasta { get; }
    public Sistema Sistema { get; }
    public string PastaImpressoes => Path.Combine(Pasta, "impressoes");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Pasta, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

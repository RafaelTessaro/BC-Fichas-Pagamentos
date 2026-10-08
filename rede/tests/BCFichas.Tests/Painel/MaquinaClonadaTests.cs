using BCFichas.Painel;
using BCFichas.Tests.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BCFichas.Tests.Painel;

/// <summary>
/// Tablets clonados (a mesma imagem do disco passada para vários tablets: o mesmo nome de computador e o mesmo
/// painel-maquina.id): cada um tem que aparecer no painel como uma máquina, não como a mesma.
/// </summary>
public sealed class MaquinaClonadaTests : IDisposable
{
    private readonly SistemaTemporario _a = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _a.Dispose();
    }

    private string Arquivo => Path.Combine(_a.Pasta, "painel-maquina.id");

    private string Identidade()
    {
        using var leitor = new Leitor(_a.Pasta, Guid.NewGuid().ToString("N"));
        return leitor.Maquina;
    }

    [Fact]
    public void Mesmo_tablet_mantem_a_identidade_ao_abrir_de_novo()
    {
        var primeira = Identidade();
        Assert.Equal(primeira, Identidade());
        Assert.Contains("|", File.ReadAllText(Arquivo));
    }

    [Fact]
    public void Disco_clonado_de_outro_tablet_vira_outra_maquina()
    {
        var original = Identidade();
        // O arquivo veio na imagem do disco de outro tablet: o número dele e a placa de rede dele (que aqui não existe)
        var numeroDoOutro = File.ReadAllText(Arquivo).Split('|')[0];
        File.WriteAllText(Arquivo, numeroDoOutro + "|0A1B2C3D4E5F");
        var clone = Identidade();
        Assert.NotEqual(original, clone);
        Assert.NotEqual(numeroDoOutro, File.ReadAllText(Arquivo).Split('|')[0]);
        // E daí em diante o clone fica com a identidade nova
        Assert.Equal(clone, Identidade());
    }

    [Fact]
    public void Tirar_um_adaptador_do_mesmo_tablet_nao_muda_a_identidade()
    {
        var original = Identidade();
        // Criado quando havia também um adaptador USB (que depois foi tirado): continua a mesma máquina
        var partes = File.ReadAllText(Arquivo).Split('|');
        File.WriteAllText(Arquivo, partes[0] + "|00E04C000001," + partes[1]);
        Assert.Equal(original, Identidade());
    }

    [Fact]
    public void Arquivo_da_versao_antiga_ganha_um_numero_novo_desta_maquina()
    {
        File.WriteAllText(Arquivo, Guid.NewGuid().ToString("N"));
        var nova = Identidade();
        Assert.Contains("|", File.ReadAllText(Arquivo));
        Assert.Equal(nova, Identidade());
    }
}

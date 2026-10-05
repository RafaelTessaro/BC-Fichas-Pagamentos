using BCFichas.App;
using Xunit;

namespace BCFichas.Tests.Painel;

/// <summary>O lado do caixa: o que ele pede ao Windows para o celular conseguir abrir o painel.</summary>
public class AuditoriaDoPainelNoCaixaTests
{
    [Fact]
    public void Liberar_no_firewall_tira_o_bloqueio_que_o_windows_cria_quando_cancelam_o_aviso()
    {
        // Na primeira vez que o painel abre a porta, o Windows mostra "Permitir acesso?" (atrás da tela cheia do
        // caixa). Quem toca em "Cancelar" cria regras de BLOQUEIO para o programa, e bloqueio vale mais que a regra
        // de liberação: o botão "Liberar no firewall" tem de apagar essas regras antes de criar a dele.
        const string exe = @"C:\BC Fichas Rede\BCFichasPainel.exe";
        var comando = PainelDaRede.ComandoDoFirewall(exe);

        var apagarBloqueios = comando.IndexOf($"delete rule name=all dir=in program=\"{exe}\"", StringComparison.Ordinal);
        var criar = comando.IndexOf("firewall add rule", StringComparison.Ordinal);
        Assert.True(apagarBloqueios >= 0 && apagarBloqueios < criar, comando);
        Assert.Contains("action=allow", comando[criar..]);
        Assert.Contains("remoteip=localsubnet", comando[criar..]);
        Assert.Contains("localport=8765", comando[criar..]);
    }
}

using BCFichas.Core;
using BCFichas.Core.Pagamento;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>Pontos levantados na auditoria (regras e cálculos) que foram corrigidos à parte.</summary>
public class AjustesDaAuditoriaTests
{
    private static Cobranca Cobranca(string id) => new(id, 2000, FormaPagamento.Debito, "Pedido");

    private static async Task Esperar(Func<bool> condicao)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (!condicao())
        {
            if (DateTime.UtcNow > limite) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Simulador_recusado_nao_aprova_sozinho_a_cobranca_seguinte()
    {
        // Aprova sozinho em 1 s: a primeira cobrança é recusada antes disso
        var simulador = new MaquininhaSimulada(aprovarEmSegundos: 1);
        var primeira = simulador.CobrarAsync(Cobranca("1"), null, CancellationToken.None);
        await Esperar(() => simulador.AguardandoDecisao);
        simulador.Recusar();
        Assert.False((await primeira).Aprovado);

        // A segunda espera o operador (sem aprovar sozinho): o relógio da primeira não pode aprová-la
        simulador.AprovarEmSegundos = 0;
        var segunda = simulador.CobrarAsync(Cobranca("2"), null, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(segunda.IsCompleted);
        Assert.True(simulador.AguardandoDecisao);
        simulador.Recusar();
        Assert.False((await segunda).Aprovado);
    }

    [Fact]
    public void Backup_nao_leva_a_maquininha_de_uma_maquina_para_a_outra()
    {
        using var a = new SistemaTemporario();
        using var b = new SistemaTemporario();
        var configA = a.Sistema.Config.Atual.Clonar();
        configA.Maquininha = TipoMaquininha.Simulador;
        configA.SimuladorAprovarEmSegundos = 3;
        a.Sistema.Config.Salvar(configA);

        var arquivo = Path.Combine(a.Pasta, a.Sistema.Programacao.NomeArquivo());
        a.Sistema.Programacao.Salvar(arquivo);
        b.Sistema.Programacao.Carregar(arquivo, 2);

        // A máquina que recebeu continua com a maquininha dela (separada), sem o simulador que aprova sozinho
        Assert.Equal(TipoMaquininha.Separada, b.Sistema.Config.Atual.Maquininha);
        Assert.Equal(0, b.Sistema.Config.Atual.SimuladorAprovarEmSegundos);
        Assert.IsType<MaquininhaSeparada>(b.Sistema.Maquininha);

        // Zerar a programação (novo evento) também mantém a maquininha da máquina
        a.Sistema.Programacao.ZerarProgramacao();
        Assert.Equal(TipoMaquininha.Simulador, a.Sistema.Config.Atual.Maquininha);
    }
}

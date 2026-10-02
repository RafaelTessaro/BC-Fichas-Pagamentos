using System.Diagnostics;
using BCFichas.Core;
using BCFichas.Core.Impressao;
using Xunit;
using Xunit.Abstractions;

namespace BCFichas.Tests.Core;

/// <summary>
/// Máquina que já tem muitas vendas acumuladas: as telas leem do banco só o que mostram, então a memória
/// não cresce com o tamanho do banco.
/// </summary>
public class MuitasVendasTests(ITestOutputHelper saida) : IDisposable
{
    private readonly SistemaTemporario _t = new();
    private Sistema S => _t.Sistema;

    public void Dispose() => _t.Dispose();

    [Fact]
    public void Com_30_mil_pedidos_resumo_relatorios_e_busca_continuam_rapidos_e_leves()
    {
        var relogio = Stopwatch.StartNew();
        var pedidos = MuitasVendas.Criar(S, dias: 30, pedidosPorDia: 1000);
        saida.WriteLine($"{pedidos} pedidos gravados em {relogio.ElapsedMilliseconds} ms; banco com {new FileInfo(S.Banco.Caminho).Length / 1024 / 1024} MB");

        // Caixa de hoje, também cheio (um dia de festa grande)
        var hoje = S.Caixa.Abrir(1, null, 10000);
        S.Banco.Executar("UPDATE pedidos SET sessao_id = $s WHERE sessao_id = (SELECT MAX(id) FROM sessoes WHERE id < $s)",
            ("$s", hoje.Id));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var memoriaAntes = GC.GetTotalMemory(forceFullCollection: true);

        long Medir(string nome, Action acao)
        {
            var r = Stopwatch.StartNew();
            acao();
            saida.WriteLine($"{nome}: {r.ElapsedMilliseconds} ms");
            return r.ElapsedMilliseconds;
        }

        ResumoCaixa? resumo = null;
        var tResumo = Medir("Resumo do caixa com 1000 pedidos", () => resumo = S.Caixa.Resumo(hoje.Id));
        Assert.Equal(1000, resumo!.QuantidadePedidos);

        var tTodos = Medir("Resumo dos 30 caixas (relatório 'Tudo')", () =>
        {
            foreach (var s in S.Caixa.Sessoes(new DateTime(2000, 1, 1), DateTime.Today)) S.Caixa.Resumo(s.Id);
        });
        var tLista = Medir("Lista da reimpressão (últimos 300)", () => Assert.Equal(300, S.Vendas.Pedidos(hoje.Id).Count));
        var ultimo = S.Banco.Escalar<long>("SELECT MAX(numero) FROM pedidos");
        var tBusca = Medir("Achar pedido pelo número (devolução)", () => Assert.NotNull(S.Vendas.PedidoPorNumero(ultimo)));
        var tImpressao = Medir("Fechamento impresso", () =>
            Assert.True(S.Impressao.Documento(Relatorios.Fechamento(resumo, S.Config.Atual)).Ok));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var crescimento = (GC.GetTotalMemory(forceFullCollection: true) - memoriaAntes) / 1024.0 / 1024.0;
        saida.WriteLine($"Memória a mais depois de tudo: {crescimento:0.0} MB");

        Assert.InRange(crescimento, -50, 5);
        Assert.True(tResumo < 1500, $"resumo levou {tResumo} ms");
        Assert.True(tTodos < 15000, $"30 resumos levaram {tTodos} ms");
        Assert.True(tLista < 1500 && tBusca < 500 && tImpressao < 3000);
    }
}

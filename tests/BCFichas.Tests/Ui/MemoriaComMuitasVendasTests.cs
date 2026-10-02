using System.Diagnostics;
using Avalonia.Headless.XUnit;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Tests.Core;
using Xunit;
using Xunit.Abstractions;

namespace BCFichas.Tests.Ui;

/// <summary>
/// Telas abertas com o banco cheio (100 dias de festa com 1000 pedidos cada): a memória não cresce com o
/// tamanho do banco, porque cada tela só lê o que mostra.
/// </summary>
public class MemoriaComMuitasVendasTests(ITestOutputHelper saida)
{
    [AvaloniaFact]
    public async Task Telas_com_100_mil_pedidos_no_banco_nao_enchem_a_memoria()
    {
        using var t = new TelaDeTeste();
        var relogio = Stopwatch.StartNew();
        var total = MuitasVendas.Criar(t.Sistema, dias: 100, pedidosPorDia: 1000);
        saida.WriteLine($"{total} pedidos em {relogio.ElapsedMilliseconds} ms; banco: " +
                        $"{new FileInfo(t.Sistema.Banco.Caminho).Length / 1024.0 / 1024.0:0.0} MB");
        t.AbrirCaixa();
        for (var i = 0; i < 50; i++) t.Tocar("PASTEL");

        double Memoria()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(true) / 1024.0 / 1024.0;
        }

        var inicio = Memoria();
        saida.WriteLine($"Memória do programa (heap) com a venda aberta: {inicio:0.0} MB");

        async Task Tela(string nome, PaginaViewModel tela, Func<Task>? depois = null)
        {
            var r = Stopwatch.StartNew();
            t.Principal.Abrir(tela);
            TelaDeTeste.Atualizar();
            if (depois is not null) await depois();
            TelaDeTeste.Atualizar();
            saida.WriteLine($"{nome}: {r.ElapsedMilliseconds} ms, heap {Memoria():0.0} MB");
        }

        var relatorios = new RelatoriosViewModel(t.Principal);
        await Tela("Relatórios (hoje)", relatorios);
        relatorios.FiltrarCommand.Execute("0");
        TelaDeTeste.Atualizar();
        Assert.Equal(101, relatorios.Sessoes.Count);
        saida.WriteLine($"Relatórios (tudo, 101 caixas): heap {Memoria():0.0} MB");
        relatorios.AbaSelecionada = 1;
        TelaDeTeste.Atualizar();

        await Tela("Reimpressão", new ReimpressaoViewModel(t.Principal));
        var devolucao = new DevolucaoViewModel(t.Principal);
        await Tela("Devolução", devolucao, () =>
        {
            devolucao.Busca = "54321";
            devolucao.BuscarCommand.Execute(null);
            Assert.True(devolucao.TemPedido);
            return Task.CompletedTask;
        });
        await Tela("Fechamento", new FechamentoViewModel(t.Principal));
        await Tela("Sangria", new SangriaViewModel(t.Principal));

        const int voltas = 10;
        for (var volta = 0; volta < voltas; volta++)
        {
            t.Principal.IrParaVenda();
            TelaDeTeste.Atualizar();
            relatorios = new RelatoriosViewModel(t.Principal);
            t.Principal.Abrir(relatorios);
            relatorios.FiltrarCommand.Execute("0");
            TelaDeTeste.Atualizar();
        }
        t.Principal.IrParaVenda();
        TelaDeTeste.Atualizar();

        var fim = Memoria();
        saida.WriteLine($"Depois de abrir os relatórios {voltas} vezes e voltar para a venda: {fim:0.0} MB (diferença {fim - inicio:+0.0;-0.0} MB)");
        saida.WriteLine($"Processo de teste inteiro (com o xUnit): {Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0:0} MB");
        Assert.InRange(fim - inicio, -100, 10);
    }
}

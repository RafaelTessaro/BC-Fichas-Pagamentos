using System.Diagnostics;
using Avalonia.Headless.XUnit;
using BCFichas.App.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace BCFichas.Tests.Ui;

/// <summary>
/// No Gerenciador de Tarefas a memória do caixa sobe nas primeiras vendas (a janela de pagamento é montada, o
/// driver da impressora e as letras da ficha são carregados, o coletor de memória junta um pouco antes de limpar)
/// e depois fica parada. Aqui: depois de esquentar, mais 100 vendas pela tela, imprimindo, não podem deixar nada
/// preso na memória.
/// </summary>
public class MemoriaVendaAVendaTests(ITestOutputHelper saida)
{
    [AvaloniaFact]
    public async Task Cem_vendas_seguidas_nao_aumentam_a_memoria()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        for (var i = 1; i <= 60; i++) await Vender(t, i);
        var (heapAntes, processoAntes) = Medir();

        var relogio = Stopwatch.StartNew();
        for (var i = 61; i <= 160; i++) await Vender(t, i);
        var (heapDepois, processoDepois) = Medir();

        saida.WriteLine($"Depois de 60 vendas: dados do programa {heapAntes:0.0} MB, processo {processoAntes:0} MB");
        saida.WriteLine($"Depois de mais 100 vendas ({relogio.ElapsedMilliseconds / 100} ms cada): dados {heapDepois:0.0} MB " +
                        $"({heapDepois - heapAntes:+0.0;-0.0} MB), processo {processoDepois:0} MB ({processoDepois - processoAntes:+0;-0} MB)");
        // Uma ficha esquecida na memória já são centenas de KB por venda: 100 vendas passariam longe disso.
        Assert.InRange(heapDepois - heapAntes, -50, 2);
        Assert.InRange(processoDepois - processoAntes, -200, 30);
    }

    /// <summary>Uma venda em dinheiro de 1 a 4 itens, pela tela, até a ficha sair e o pagamento fechar.</summary>
    private static async Task Vender(TelaDeTeste t, int n)
    {
        t.Tocar("PASTEL");
        if (n % 2 == 0) t.Tocar("PASTEL");
        if (n % 3 == 0) t.Tocar("ESPETINHO");
        if (n % 5 == 0) t.Tocar("CALDO");
        t.Venda.PagarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        pagamento.Recebido.SomarCommand.Execute("5000");
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => !pagamento.Imprimindo);
        Assert.True(pagamento.EmConcluido, pagamento.Mensagem);
        pagamento.FecharCommand.Execute(null);
        TelaDeTeste.Atualizar();
        Assert.Null(t.Principal.Dialogo);
        // As fichas vão para arquivos nos testes: apaga para a pasta não crescer
        if (n % 20 == 0)
            foreach (var arquivo in Directory.GetFiles(t.PastaImpressoes, "*.png")) File.Delete(arquivo);
    }

    /// <summary>Dados do programa (depois de o coletor limpar) e memória do processo inteiro, em MB.</summary>
    private static (double Heap, double Processo) Medir()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heap = GC.GetTotalMemory(true) / 1048576.0;
        using var processo = Process.GetCurrentProcess();
        return (heap, processo.WorkingSet64 / 1048576.0);
    }
}

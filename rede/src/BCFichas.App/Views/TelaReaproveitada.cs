using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;

namespace BCFichas.App.Views;

/// <summary>
/// Tela montada uma vez e usada de novo, em vez de criar todos os controles toda vez que ela aparece. No tablet,
/// montar a tela de venda (ou a de pagamento) do zero leva meio segundo. Só reaproveita a tela que não está na
/// janela (senão cria outra, como antes).
/// </summary>
public abstract class TelaReaproveitada : IDataTemplate
{
    public abstract bool Match(object? data);

    public Control? Build(object? data)
    {
        if (data is null) return null;
        var tela = Guardada(data);
        if (tela is null || tela.Parent is not null || tela.GetVisualParent() is not null)
        {
            tela = Criar();
            Guardar(data, tela);
        }
        return tela;
    }

    protected abstract Control Criar();
    protected abstract Control? Guardada(object data);
    protected abstract void Guardar(object data, Control tela);
}

/// <summary>A tela de venda: uma para cada caixa aberto no programa (o mesmo VendaViewModel).</summary>
public sealed class TelaDeVenda : TelaReaproveitada
{
    private readonly ConditionalWeakTable<object, Control> _telas = new();

    public override bool Match(object? data) => data is VendaViewModel;
    protected override Control Criar() => new VendaView();
    protected override Control? Guardada(object data) => _telas.TryGetValue(data, out var tela) ? tela : null;
    protected override void Guardar(object data, Control tela) => _telas.AddOrUpdate(data, tela);
}

/// <summary>A janela de pagamento: uma só, com o pagamento novo de cada venda.</summary>
public sealed class TelaDePagamento : TelaReaproveitada
{
    private Control? _tela;

    public override bool Match(object? data) => data is PagamentoViewModel;
    protected override Control Criar() => new PagamentoView();
    protected override Control? Guardada(object data) => _tela;
    protected override void Guardar(object data, Control tela) => _tela = tela;
}

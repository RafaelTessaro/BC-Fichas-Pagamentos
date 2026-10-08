using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;

namespace BCFichas.App.Views;

public partial class VendaView : UserControl
{
    /// <summary>
    /// Quanto o dedo pode escorregar num toque na lista do pedido sem virar rolagem. O padrão do Windows é 5: um toque
    /// um pouco tremido na linha do item piscava e não tirava nada.
    /// </summary>
    public const int FolgaDoToqueNoPedido = 16;

    public VendaView()
    {
        InitializeComponent();
        ListaPedido.TemplateApplied += (_, e) =>
        {
            var miolo = e.NameScope.Find<ScrollContentPresenter>("PART_ContentPresenter");
            if (miolo is null) return;
            foreach (var rolagem in miolo.GestureRecognizers.OfType<ScrollGestureRecognizer>())
                rolagem.ScrollStartDistance = FolgaDoToqueNoPedido;
        };
    }

    /// <summary>
    /// Em volta do −, do número e do + o toque não tira 1 pela linha: errar o + por pouco não pode fazer o contrário.
    /// O − e o + tratam o próprio toque antes de chegar aqui.
    /// </summary>
    private void AoTocarPertoDaQuantidade(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}

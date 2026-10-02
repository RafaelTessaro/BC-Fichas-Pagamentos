using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace BCFichas.App.Views;

public partial class ProdutosView : UserControl
{
    public ProdutosView() => InitializeComponent();

    // Ao ligar "Combo", rola a tela até a lista das fichas (ela fica no fim do cadastro).
    private void AoMarcarCombo(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { IsChecked: true, IsPointerOver: true } or ToggleSwitch { IsChecked: true, IsFocused: true })
            Dispatcher.UIThread.Post(() => BlocoCombo.BringIntoView(), DispatcherPriority.Background);
    }
}

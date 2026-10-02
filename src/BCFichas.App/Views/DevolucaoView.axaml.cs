using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BCFichas.App.Views;

public partial class DevolucaoView : UserControl
{
    public DevolucaoView() => InitializeComponent();

    // O campo do número já começa selecionado: o leitor de código de barras "digita" direto nele.
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        CampoBusca.Focus();
    }
}

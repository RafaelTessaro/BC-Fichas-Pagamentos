using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;

namespace BCFichas.App.Views;

public partial class ConfiguracaoView : UserControl
{
    private ConfiguracaoViewModel? _vm;

    public ConfiguracaoView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Abas.CollectionChanged -= AoMudarAbas;
            _vm = DataContext as ConfiguracaoViewModel;
            if (_vm is not null) _vm.Abas.CollectionChanged += AoMudarAbas;
        };
    }

    /// <summary>Nova aba: o cursor já vai para o nome dela (o teclado da tela abre junto).</summary>
    private void AoMudarAbas(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems?[0] is not AbaEdicao { Nova: true } nova) return;
        Dispatcher.UIThread.Post(() =>
        {
            var linha = ListaAbas.ContainerFromItem(nova);
            linha?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
        }, DispatcherPriority.Loaded);
    }
}

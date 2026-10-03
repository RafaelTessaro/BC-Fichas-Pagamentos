using System.Collections.Specialized;
using System.ComponentModel;
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
            if (_vm is not null)
            {
                _vm.Abas.CollectionChanged -= AoMudarAbas;
                _vm.PropertyChanged -= AoMudarTela;
            }
            _vm = DataContext as ConfiguracaoViewModel;
            if (_vm is not null)
            {
                _vm.Abas.CollectionChanged += AoMudarAbas;
                _vm.PropertyChanged += AoMudarTela;
            }
        };
    }

    /// <summary>Aba Máquina trancada: o cursor já vai para a senha técnica (o teclado da tela abre junto).</summary>
    private void AoMudarTela(object? sender, PropertyChangedEventArgs e)
    {
        // Liberou: o campo da senha some e não pode continuar com o cursor (o teclado do computador digitaria nele).
        if (e.PropertyName == nameof(ConfiguracaoViewModel.MaquinaLiberada) && _vm is { MaquinaLiberada: true } &&
            CampoSenhaTecnica.IsFocused)
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        if (e.PropertyName is not (nameof(ConfiguracaoViewModel.AbaSelecionada) or nameof(ConfiguracaoViewModel.ErroSenhaTecnica)))
            return;
        if (_vm is not { AbaSelecionada: ConfiguracaoViewModel.AbaMaquina, MaquinaLiberada: false }) return;
        Dispatcher.UIThread.Post(() => CampoSenhaTecnica.Focus(), DispatcherPriority.Loaded);
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

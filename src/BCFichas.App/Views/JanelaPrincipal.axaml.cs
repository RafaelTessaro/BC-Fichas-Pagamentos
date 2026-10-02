using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BCFichas.App.ViewModels;

namespace BCFichas.App.Views;

public partial class JanelaPrincipal : Window
{
    private PrincipalViewModel? _vm;

    public JanelaPrincipal()
    {
        InitializeComponent();
        // Mostra o teclado na tela quando um campo de texto recebe o toque.
        AddHandler(GotFocusEvent, AoFocar, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= AoMudarVm;
        _vm = DataContext as PrincipalViewModel;
        if (_vm is null) return;

        _vm.PropertyChanged += AoMudarVm;
        _vm.EscolherImagem = EscolherImagemAsync;
        _vm.FecharPrograma = Close;
        AplicarTelaCheia();
    }

    private void AoMudarVm(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PrincipalViewModel.TelaCheia):
                AplicarTelaCheia();
                break;
            case nameof(PrincipalViewModel.Pagina):
            case nameof(PrincipalViewModel.Dialogo):
                if (_vm is not null) _vm.TecladoVisivel = false;
                break;
        }
    }

    private void AplicarTelaCheia()
    {
        if (_vm is null) return;
        WindowState = _vm.TelaCheia ? WindowState.FullScreen : WindowState.Maximized;
    }

    private void AoFocar(object? sender, GotFocusEventArgs e)
    {
        if (_vm is null) return;
        if (e.Source is TextBox campo)
        {
            if (!_vm.TecladoHabilitado) return;
            _vm.TecladoNumerico = campo.Classes.Contains("numero");
            // Espera a página trocar de tamanho e garante que o campo continua visível.
            Dispatcher.UIThread.Post(() =>
            {
                _vm.TecladoVisivel = true;
                Dispatcher.UIThread.Post(campo.BringIntoView, DispatcherPriority.Background);
            });
        }
        else
        {
            _vm.TecladoVisivel = false;
        }
    }

    private async Task<string?> EscolherImagemAsync()
    {
        var arquivos = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Escolha uma imagem",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        return arquivos.FirstOrDefault()?.TryGetLocalPath();
    }
}

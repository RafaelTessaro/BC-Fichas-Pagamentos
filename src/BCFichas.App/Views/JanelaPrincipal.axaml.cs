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
        // F1 (com teclado ligado ao tablet) é o atalho escondido do modo teste.
        AddHandler(KeyDownEvent, AoTeclar, RoutingStrategies.Tunnel);
    }

    private void AoTeclar(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F1 || _vm is null) return;
        e.Handled = true;
        _vm.PedirModoTeste();
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
            // Campos com teclado próprio na tela (ex.: número do pedido na devolução) não abrem o teclado de baixo.
            if (!_vm.TecladoHabilitado || campo.Classes.Contains("semteclado")) return;
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

    /// <param name="pastaInicial">Pasta em que o seletor abre (ex.: C:\Sistema_New\produtos).</param>
    /// <param name="avisarSeNaoExistir">Avisa quando a pasta não existe (e deixa escolher em outro lugar).</param>
    private async Task<string?> EscolherImagemAsync(string? pastaInicial, bool avisarSeNaoExistir)
    {
        IStorageFolder? inicio = null;
        if (!string.IsNullOrWhiteSpace(pastaInicial))
        {
            if (Directory.Exists(pastaInicial))
            {
                try
                {
                    inicio = await StorageProvider.TryGetFolderFromPathAsync(pastaInicial);
                }
                catch (Exception e)
                {
                    Log.Erro("Pasta das fotos", e);
                }
            }
            else if (avisarSeNaoExistir && _vm is not null)
            {
                await _vm.Mensagem("Pasta das fotos não encontrada",
                    $"A pasta {pastaInicial} não existe neste computador.\n\nNa próxima tela, escolha a imagem em outro lugar.");
            }
        }

        var arquivos = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Escolha uma imagem",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
            SuggestedStartLocation = inicio,
        });
        return arquivos.FirstOrDefault()?.TryGetLocalPath();
    }
}

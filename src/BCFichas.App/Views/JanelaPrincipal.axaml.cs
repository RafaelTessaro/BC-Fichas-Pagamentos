using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        AddHandler(PointerReleasedEvent, AoSoltarToque, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, AoTocar, RoutingStrategies.Tunnel);
    }

    /// <summary>Até quanto tempo depois de um toque o seguinte ainda conta como toque duplo.</summary>
    private const int ToqueDuplo = 350;

    private long _ultimoToque;
    private Point _pontoApertado;
    private Point _pontoDoUltimoToque;
    private Button? _botaoDoUltimoToque;
    private bool _toqueIgnorado;

    private void AoSoltarToque(object? sender, PointerReleasedEventArgs e)
    {
        _ultimoToque = Environment.TickCount64;
        // Toques seguidos em cima da tela nova continuam ignorados até o dedo parar um instante.
        if (_toqueIgnorado)
        {
            _toqueIgnorado = false;
            return;
        }
        _pontoDoUltimoToque = e.GetPosition(this);
        // Arrastar o dedo (rolar uma lista) não é toque num botão: não protege nada. A mesma folga da lista do pedido
        // (lá, um toque tremido de até 16 ainda vale como toque e tira 1: a proteção também tem de valer).
        var arrastou = Math.Abs(_pontoDoUltimoToque.X - _pontoApertado.X) > VendaView.FolgaDoToqueNoPedido ||
                       Math.Abs(_pontoDoUltimoToque.Y - _pontoApertado.Y) > VendaView.FolgaDoToqueNoPedido;
        _botaoDoUltimoToque = arrastou ? null : BotaoEm(_pontoDoUltimoToque);
    }

    private Button? BotaoEm(Point ponto) =>
        (this.InputHitTest(ponto) as Visual)?.FindAncestorOfType<Button>(includeSelf: true);

    /// <summary>
    /// Toque duplo num botão que troca a tela: o segundo toque caía no que apareceu embaixo do dedo (o Abrir caixa
    /// punha um produto no pedido, o Dinheiro trocava o valor da venda pela nota de R$ 5). Se logo depois do toque o
    /// botão já não está mais lá, o toque seguinte é ignorado. Tocar duas vezes no mesmo botão (um produto, o +, as
    /// teclas) continua valendo.
    /// </summary>
    private void AoTocar(object? sender, PointerPressedEventArgs e)
    {
        _pontoApertado = e.GetPosition(this);
        if (_botaoDoUltimoToque is null || Environment.TickCount64 - _ultimoToque > ToqueDuplo) return;
        if (BotaoEm(_pontoDoUltimoToque) == _botaoDoUltimoToque) return;
        e.Handled = true;
        _toqueIgnorado = true;
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
        _vm.EscolherProgramacao = EscolherProgramacaoAsync;
        _vm.EscolherPasta = EscolherPastaAsync;
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

    private static readonly FilePickerFileType TipoProgramacao = new("Programação do BC Fichas")
    {
        Patterns = ["*" + Core.Servicos.ProgramacaoServico.Extensao],
    };

    private async Task<IStorageFolder?> Pasta(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho) || !Directory.Exists(caminho)) return null;
        try
        {
            return await StorageProvider.TryGetFolderFromPathAsync(caminho);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string?> EscolherProgramacaoAsync(string? pastaInicial)
    {
        var arquivos = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Escolha a programação do BC Fichas",
            AllowMultiple = false,
            FileTypeFilter = [TipoProgramacao],
            SuggestedStartLocation = await Pasta(pastaInicial),
        });
        return arquivos.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> EscolherPastaAsync(string? pastaInicial)
    {
        var pastas = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Escolha a pasta do backup",
            AllowMultiple = false,
            SuggestedStartLocation = await Pasta(pastaInicial),
        });
        return pastas.FirstOrDefault()?.TryGetLocalPath();
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

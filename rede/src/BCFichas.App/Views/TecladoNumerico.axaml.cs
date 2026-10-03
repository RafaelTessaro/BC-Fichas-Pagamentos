using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace BCFichas.App.Views;

public partial class TecladoNumerico : UserControl
{
    public static readonly StyledProperty<ICommand?> ComandoProperty =
        AvaloniaProperty.Register<TecladoNumerico, ICommand?>(nameof(Comando));

    /// <summary>Tecla do canto esquerdo: "00" para valores, "C" para senha.</summary>
    public static readonly StyledProperty<string> TeclaEsquerdaProperty =
        AvaloniaProperty.Register<TecladoNumerico, string>(nameof(TeclaEsquerda), "00");

    public TecladoNumerico() => InitializeComponent();

    public ICommand? Comando
    {
        get => GetValue(ComandoProperty);
        set => SetValue(ComandoProperty, value);
    }

    public string TeclaEsquerda
    {
        get => GetValue(TeclaEsquerdaProperty);
        set => SetValue(TeclaEsquerdaProperty, value);
    }

    public string TextoEsquerda => TeclaEsquerda == "C" ? "Limpar" : TeclaEsquerda;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TeclaEsquerdaProperty)
            RaisePropertyChanged<string>(TextoEsquerdaProperty, "", TextoEsquerda);
    }

    // Propriedade só de leitura para o texto do botão.
    public static readonly DirectProperty<TecladoNumerico, string> TextoEsquerdaProperty =
        AvaloniaProperty.RegisterDirect<TecladoNumerico, string>(nameof(TextoEsquerda), t => t.TextoEsquerda);
}

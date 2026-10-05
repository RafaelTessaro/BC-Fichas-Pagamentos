using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using BCFichas.App.ViewModels;

namespace BCFichas.App.Views;

/// <summary>
/// Teclado na tela para o tablet: digita no campo que estiver selecionado.
/// As teclas não pegam o foco, assim o campo continua selecionado.
/// </summary>
public sealed class TecladoVirtual : UserControl
{
    public static readonly StyledProperty<bool> NumericoProperty =
        AvaloniaProperty.Register<TecladoVirtual, bool>(nameof(Numerico));

    private const string Apagar = "⌫";
    private const string Esconder = "esconder";
    private const string Acentos = "acentos";
    private const string Espaco = "espaço";

    private static readonly string[][] Letras =
    [
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", Apagar],
        ["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"],
        ["A", "S", "D", "F", "G", "H", "J", "K", "L", "Ç"],
        [Acentos, "Z", "X", "C", "V", "B", "N", "M", ",", ".", "-"],
        ["/", Espaco, "!", Esconder],
    ];

    private static readonly string[][] Simbolos =
    [
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", Apagar],
        ["Á", "À", "Â", "Ã", "É", "Ê", "Í", "Ó", "Ô", "Õ"],
        ["Ú", "Ü", "Ç", "&", "(", ")", ":", ";", "?", "@"],
        [Acentos, "#", "$", "%", "+", "=", "*", "\"", "'", "_", "-"],
        ["/", Espaco, "!", Esconder],
    ];

    private static readonly string[][] Numeros =
    [
        ["7", "8", "9", Apagar],
        ["4", "5", "6", ","],
        ["1", "2", "3", Esconder],
        ["0"],
    ];

    private bool _acentos;

    public TecladoVirtual()
    {
        Background = new SolidColorBrush(Color.Parse("#E2E8F0"));
        Padding = new Thickness(6, 6, 6, 8);
        Montar();
    }

    public bool Numerico
    {
        get => GetValue(NumericoProperty);
        set => SetValue(NumericoProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NumericoProperty) Montar();
    }

    private void Montar()
    {
        var linhas = Numerico ? Numeros : _acentos ? Simbolos : Letras;
        var painel = new StackPanel { Spacing = 0 };

        foreach (var linha in linhas)
        {
            var grade = new Grid { Height = 54 };
            var coluna = 0;
            foreach (var tecla in linha)
            {
                var peso = tecla switch
                {
                    Espaco => 6,
                    Esconder or Acentos => 1.6,
                    Apagar => Numerico ? 1 : 1.6,
                    _ => 1,
                };
                if (Numerico && tecla == "0") peso = 3;
                grade.ColumnDefinitions.Add(new ColumnDefinition(peso, GridUnitType.Star));
                var botao = CriarTecla(tecla);
                Grid.SetColumn(botao, coluna++);
                grade.Children.Add(botao);
            }
            painel.Children.Add(grade);
        }

        Content = new Border
        {
            Child = painel,
            MaxWidth = Numerico ? 460 : 1100,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private Button CriarTecla(string tecla)
    {
        var botao = new Button { Classes = { "tecla" }, Tag = tecla };
        switch (tecla)
        {
            case Apagar:
                botao.Classes.Add("especial");
                botao.Content = new PathIcon { Data = Recursos.Icone("IconeApagar"), Width = 26, Height = 20 };
                break;
            case Esconder:
                botao.Classes.Add("especial");
                botao.Content = new PathIcon { Data = Recursos.Icone("IconeTecladoFechar"), Width = 26, Height = 24 };
                break;
            case Acentos:
                botao.Classes.Add("especial");
                botao.Content = _acentos ? "ABC" : "ÁÃ#";
                break;
            case Espaco:
                botao.Content = new Rectangle { Width = 120, Height = 3, Fill = Brushes.Gray };
                break;
            default:
                botao.Content = tecla;
                break;
        }
        botao.Click += AoTocar;
        return botao;
    }

    private void AoTocar(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tecla }) return;
        var janela = TopLevel.GetTopLevel(this);

        switch (tecla)
        {
            case Esconder:
                if (DataContext is PrincipalViewModel vm) vm.TecladoVisivel = false;
                janela?.Focus();
                return;
            case Acentos:
                _acentos = !_acentos;
                Montar();
                return;
        }

        if (janela?.FocusManager?.GetFocusedElement() is not TextBox campo) return;

        if (tecla == Apagar)
        {
            campo.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Back, Source = campo });
            return;
        }

        var texto = tecla == Espaco ? " " : tecla;
        campo.RaiseEvent(new TextInputEventArgs { RoutedEvent = TextInputEvent, Text = texto, Source = campo });
    }
}

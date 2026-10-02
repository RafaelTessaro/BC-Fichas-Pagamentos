using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BCFichas.App.Views;

/// <summary>
/// Texto que diminui a letra até a maior palavra caber na largura (o nome do produto não quebra no meio
/// da palavra quando o botão fica estreito, como em tela pequena).
/// </summary>
public class TextoQueCabe : TextBlock
{
    public static readonly StyledProperty<double> TamanhoMaximoProperty =
        AvaloniaProperty.Register<TextoQueCabe, double>(nameof(TamanhoMaximo), 20);

    public static readonly StyledProperty<double> TamanhoMinimoProperty =
        AvaloniaProperty.Register<TextoQueCabe, double>(nameof(TamanhoMinimo), 10);

    /// <summary>Faz o texto inteiro caber numa linha (não só a maior palavra).</summary>
    public static readonly StyledProperty<bool> LinhaUnicaProperty =
        AvaloniaProperty.Register<TextoQueCabe, bool>(nameof(LinhaUnica));

    static TextoQueCabe()
    {
        AffectsMeasure<TextoQueCabe>(TamanhoMaximoProperty, TamanhoMinimoProperty, LinhaUnicaProperty);
    }

    public bool LinhaUnica
    {
        get => GetValue(LinhaUnicaProperty);
        set => SetValue(LinhaUnicaProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public double TamanhoMaximo
    {
        get => GetValue(TamanhoMaximoProperty);
        set => SetValue(TamanhoMaximoProperty, value);
    }

    public double TamanhoMinimo
    {
        get => GetValue(TamanhoMinimoProperty);
        set => SetValue(TamanhoMinimoProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var tamanho = TamanhoMaximo;
        var largura = availableSize.Width - Padding.Left - Padding.Right;
        var texto = Text ?? "";
        if (!double.IsInfinity(largura) && largura > 0 && texto.Length > 0)
        {
            var maiorPalavra = LinhaUnica
                ? texto
                : texto.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries)
                    .OrderByDescending(p => p.Length).FirstOrDefault() ?? texto;
            var fonte = new Typeface(FontFamily, FontStyle, FontWeight);
            while (tamanho > TamanhoMinimo && Largura(maiorPalavra, fonte, tamanho) > largura) tamanho -= 0.5;
        }
        if (Math.Abs(FontSize - tamanho) > 0.01) SetCurrentValue(FontSizeProperty, tamanho);
        return base.MeasureOverride(availableSize);
    }

    private double Largura(string palavra, Typeface fonte, double tamanho) =>
        new FormattedText(palavra, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            fonte, tamanho, null).WidthIncludingTrailingWhitespace;
}

using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;

namespace BCFichas.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

/// <summary>Tela inteira (não diálogo). Toda página tem o botão Voltar para a venda.</summary>
public abstract partial class PaginaViewModel(PrincipalViewModel principal) : ViewModelBase
{
    public PrincipalViewModel Principal { get; } = principal;
    protected Sistema Sistema => Principal.Sistema;

    /// <summary>Chamado sempre que a tela aparece.</summary>
    public virtual void AoAbrir()
    {
    }

    [RelayCommand]
    protected virtual void Voltar() => Principal.IrParaVenda();
}

/// <summary>Valor digitado no teclado numérico estilo maquininha: 1, 2, 5, 0 vira R$ 12,50.</summary>
public sealed partial class EntradaValor : ObservableObject
{
    private const long Maximo = 99_999_999;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Texto))]
    private long _centavos;

    public string Texto => Dinheiro.Formatar(Centavos);

    [RelayCommand]
    public void Tecla(string tecla)
    {
        switch (tecla)
        {
            case "<":
                Centavos /= 10;
                break;
            case "C":
                Centavos = 0;
                break;
            case "00":
                if (Centavos * 100 <= Maximo) Centavos *= 100;
                break;
            default:
                if (tecla.Length == 1 && char.IsAsciiDigit(tecla[0]) && Centavos * 10 + (tecla[0] - '0') <= Maximo)
                    Centavos = Centavos * 10 + (tecla[0] - '0');
                break;
        }
    }

    [RelayCommand]
    public void Somar(string centavos)
    {
        if (long.TryParse(centavos, out var valor)) Centavos = Math.Min(Maximo, Centavos + valor);
    }
}

internal static class Recursos
{
    public static Geometry Icone(string chave) =>
        Application.Current?.TryGetResource(chave, null, out var r) == true && r is Geometry g
            ? g
            : new RectangleGeometry(new Rect(4, 4, 16, 16));

    public static IBrush Pincel(string chave) =>
        Application.Current?.TryGetResource(chave, null, out var r) == true && r is IBrush b ? b : Brushes.Gray;

    /// <summary>Texto branco ou escuro, o que tiver mais contraste com a cor do botão.</summary>
    public static IBrush TextoSobre(Color cor)
    {
        var luminancia = (0.299 * cor.R + 0.587 * cor.G + 0.114 * cor.B) / 255;
        return luminancia > 0.66 ? new SolidColorBrush(Color.Parse("#0F172A")) : Brushes.White;
    }

    public static Color CorOuPadrao(string? texto) =>
        Color.TryParse(texto, out var cor) ? cor : Color.Parse("#0E9F6E");
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace BCFichas.App.Views;

public partial class MarcaBC : UserControl
{
    /// <summary>Tamanho do símbolo (o texto acompanha).</summary>
    public static readonly StyledProperty<double> TamanhoProperty =
        AvaloniaProperty.Register<MarcaBC, double>(nameof(Tamanho), 64);

    private static readonly Lazy<Bitmap?> LogoDaPasta = new(CarregarLogo);

    public MarcaBC()
    {
        InitializeComponent();
        if (LogoDaPasta.Value is { } logo)
        {
            Logo.Source = logo;
            Logo.IsVisible = true;
            Simbolo.IsVisible = false;
        }
        AplicarTamanho();
    }

    public double Tamanho
    {
        get => GetValue(TamanhoProperty);
        set => SetValue(TamanhoProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TamanhoProperty) AplicarTamanho();
    }

    private void AplicarTamanho()
    {
        Quadro.Width = Quadro.Height = Tamanho;
        Nome.FontSize = Math.Round(Tamanho * 0.27);
    }

    /// <summary>
    /// O logo oficial pode ser colocado como "marca.png" na pasta do programa; sem ele, aparece o símbolo.
    /// </summary>
    private static Bitmap? CarregarLogo()
    {
        try
        {
            var caminho = Path.Combine(AppContext.BaseDirectory, "marca.png");
            if (!File.Exists(caminho)) return null;
            // Já no tamanho da tela (um logo grande ficaria inteiro na memória o dia todo)
            using var arquivo = File.OpenRead(caminho);
            return Bitmap.DecodeToWidth(arquivo, 256, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

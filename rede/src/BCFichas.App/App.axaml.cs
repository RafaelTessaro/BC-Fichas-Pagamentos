using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BCFichas.App.ViewModels;
using BCFichas.App.Views;
using BCFichas.Core;

namespace BCFichas.App;

public partial class App : Application
{
    /// <summary>Criado no Program.Main (ou pelos testes) antes de abrir a janela.</summary>
    public static Sistema Sistema { get; set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var principal = new PrincipalViewModel(Sistema)
            {
                AplicarNoWindows = c =>
                {
                    IntegracaoWindows.Aplicar(c);
                    PainelDaRede.Aplicar(c);
                },
            };
            IntegracaoWindows.Aplicar(Sistema.Config.Atual);
            PainelDaRede.Iniciar(Sistema);
            desktop.MainWindow = new JanelaPrincipal { DataContext = principal };
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            principal.Iniciar();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

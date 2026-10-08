using Avalonia;
using BCFichas.Core;

namespace BCFichas.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Evita abrir o programa duas vezes (toque duplo no ícone).
        using var unico = new Mutex(true, "BCFichasRede-instancia-unica", out var primeiro);
        if (!primeiro) return 0;

        var pasta = Sistema.PastaDadosPadrao();
        Log.Pasta = pasta;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Erro("Erro não tratado", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Erro("Erro em tarefa", e.Exception);
            e.SetObserved();
        };

        try
        {
            App.Sistema = Sistema.Iniciar(pasta);
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            Log.Erro("Falha ao iniciar", e);
            throw;
        }
    }

    // Usado também pelo designer do Avalonia.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .ComFontes()
            .With(new Win32PlatformOptions
            {
                // Tablets simples às vezes têm driver de vídeo ruim: cai para desenho por software se precisar.
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            })
            .LogToTrace();
}

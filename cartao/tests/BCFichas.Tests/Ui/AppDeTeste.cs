using Avalonia;
using Avalonia.Headless;
using BCFichas.App;
using BCFichas.Tests.Ui;

[assembly: AvaloniaTestApplication(typeof(AppDeTeste))]

namespace BCFichas.Tests.Ui;

/// <summary>Sobe o Avalonia sem janela de verdade, mas desenhando com Skia (para tirar fotos das telas).</summary>
public static class AppDeTeste
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<BCFichas.App.App>()
            .UseSkia()
            .ComFontes()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

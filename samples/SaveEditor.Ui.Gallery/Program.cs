using Avalonia;

namespace SaveEditor.Ui.Gallery;

/// <summary>Desktop entry point for the gallery.</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Used by the Avalonia previewer and by the desktop entry point.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}

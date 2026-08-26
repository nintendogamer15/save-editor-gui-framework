using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using SaveEditor.Ui.Theming;

[assembly: AvaloniaTestApplication(typeof(SaveEditor.Generated.Tests.TestAppBuilder))]

namespace SaveEditor.Generated.Tests;

/// <summary>Bootstraps a headless Avalonia application for the test session.</summary>
/// <remarks>
/// Registers styles the same way <c>App.axaml</c> does — a base Avalonia
/// theme first, then <c>SaveEditorTheme</c> — so tests exercise the same
/// resource resolution the real app does.
/// </remarks>
public sealed class TestAppBuilder : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new SaveEditorTheme());
    }

    /// <summary>Configures the headless application.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestAppBuilder>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}

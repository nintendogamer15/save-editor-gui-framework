using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using SaveEditor.Ui.Theming;

[assembly: AvaloniaTestApplication(typeof(SaveEditor.Ui.HeadlessTests.TestAppBuilder))]

namespace SaveEditor.Ui.HeadlessTests;

/// <summary>Bootstraps a headless Avalonia application for the test session.</summary>
/// <remarks>
/// The framework theme is registered the same way a consuming editor registers it —
/// after a base Avalonia theme, which supplies control behaviour while the visual
/// contract stays the framework's.
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
    /// <returns>The configured builder.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestAppBuilder>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}

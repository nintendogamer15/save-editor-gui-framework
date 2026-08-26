using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(SaveEditor.Ui.HeadlessTests.TestAppBuilder))]

namespace SaveEditor.Ui.HeadlessTests;

/// Bootstraps a headless Avalonia application for the test session.
public sealed class TestAppBuilder : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestAppBuilder>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}

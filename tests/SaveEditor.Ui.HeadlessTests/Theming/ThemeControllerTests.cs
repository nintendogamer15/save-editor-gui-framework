using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Theming;

namespace SaveEditor.Ui.HeadlessTests.Theming;

/// <summary>
/// Covers accent precedence and the requirement that a selection survives a restart.
/// </summary>
public class ThemeControllerTests
{
    /// <summary>An in-memory store standing in for a settings file across restarts.</summary>
    private sealed class FakeStore : IEditorSettingsStore
    {
        public EditorSettings Current { get; private set; } = new();

        public bool IsPersistent => true;

        public ValueTask<EditorSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);

        public ValueTask SaveAsync(EditorSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            return ValueTask.CompletedTask;
        }
    }

    private static SaveEditorTheme Theme() =>
        Application.Current!.Styles.OfType<SaveEditorTheme>().Single();

    private static Color Resolve(string key, ThemeVariant variant)
    {
        Assert.True(
            Application.Current!.TryGetResource(key, variant, out var value),
            $"Semantic resource '{key}' did not resolve for {variant}.");

        // Compiled XAML yields ImmutableSolidColorBrush, so assert the interface
        // rather than a concrete brush type.
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    [AvaloniaFact]
    public async Task Accent_And_Mode_Survive_A_Restart()
    {
        var store = new FakeStore();

        var first = new ThemeController(Theme(), store);
        await first.InitializeAsync(TestContext.Current.CancellationToken);
        await first.SetModeAsync(ThemeMode.Light, TestContext.Current.CancellationToken);
        await first.SetAccentAsync(CatppuccinAccent.Teal, TestContext.Current.CancellationToken);

        // A second controller over the same store is what "next launch" means here.
        var second = new ThemeController(Theme(), store);
        await second.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ThemeMode.Light, second.Mode);
        Assert.Equal(CatppuccinAccent.Teal, second.Accent);
        Assert.False(second.IsUsingEditorDefault);
    }

    [AvaloniaFact]
    public async Task User_Selection_Beats_The_Editor_Default()
    {
        var store = new FakeStore();

        var controller = new ThemeController(Theme(), store, CatppuccinAccent.Mauve);
        await controller.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CatppuccinAccent.Mauve, controller.Accent);
        Assert.True(controller.IsUsingEditorDefault);

        await controller.SetAccentAsync(CatppuccinAccent.Peach, TestContext.Current.CancellationToken);

        var next = new ThemeController(Theme(), store, CatppuccinAccent.Mauve);
        await next.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CatppuccinAccent.Peach, next.Accent);
    }

    [AvaloniaFact]
    public async Task Reset_Returns_To_The_Editor_Default_Not_The_Framework_One()
    {
        var store = new FakeStore();

        var controller = new ThemeController(Theme(), store, CatppuccinAccent.Mauve);
        await controller.InitializeAsync(TestContext.Current.CancellationToken);
        await controller.SetAccentAsync(CatppuccinAccent.Peach, TestContext.Current.CancellationToken);
        await controller.ResetAccentAsync(TestContext.Current.CancellationToken);

        // Mauve, not Blue: an editor that deliberately chose its accent must not lose
        // it the first time a user presses reset.
        Assert.Equal(CatppuccinAccent.Mauve, controller.Accent);
        Assert.NotEqual(ThemeController.FrameworkDefaultAccent, controller.Accent);
        Assert.True(controller.IsUsingEditorDefault);
    }

    [AvaloniaFact]
    public async Task Changing_Accent_Actually_Changes_The_Resolved_Resource()
    {
        var controller = new ThemeController(Theme(), new FakeStore());
        await controller.InitializeAsync(TestContext.Current.CancellationToken);

        await controller.SetAccentAsync(CatppuccinAccent.Green, TestContext.Current.CancellationToken);
        var green = Resolve("Primary", ThemeVariant.Dark);

        await controller.SetAccentAsync(CatppuccinAccent.Red, TestContext.Current.CancellationToken);
        var red = Resolve("Primary", ThemeVariant.Dark);

        // Proves the dictionary swap took effect rather than the controller merely
        // tracking a field.
        Assert.NotEqual(green, red);
        Assert.Equal(Color.Parse("#a6e3a1"), green);
        Assert.Equal(Color.Parse("#f38ba8"), red);
    }

    [AvaloniaFact]
    public async Task Selection_Survives_A_Restart_Through_The_Real_Settings_File()
    {
        // The FakeStore tests above leave one seam open: they never touch a real
        // settings file. This closes the whole chain — controller, store, JSON on
        // disk, and a second controller reading it back.
        var directory = Path.Combine(Path.GetTempPath(), $"se-theme-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var options = new EditorSettingsStoreOptions { BaseDirectory = directory };
            var applicationId = EditorApplicationId.Parse("ThemeRestartTest");

            var before = new ThemeController(
                Theme(), new EditorSettingsStore(applicationId, options), CatppuccinAccent.Mauve);

            await before.InitializeAsync(TestContext.Current.CancellationToken);
            await before.SetModeAsync(ThemeMode.Light, TestContext.Current.CancellationToken);
            await before.SetAccentAsync(CatppuccinAccent.Sapphire, TestContext.Current.CancellationToken);

            // A fresh store over the same directory is what a relaunch actually is.
            var after = new ThemeController(
                Theme(), new EditorSettingsStore(applicationId, options), CatppuccinAccent.Mauve);

            await after.InitializeAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ThemeMode.Light, after.Mode);
            Assert.Equal(CatppuccinAccent.Sapphire, after.Accent);
            Assert.False(after.IsUsingEditorDefault);

            // And the accent that came off disk is the one now resolving.
            Assert.Equal(Color.Parse("#74c7ec"), Resolve("Primary", ThemeVariant.Dark));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Both_Variants_Resolve_Every_Text_Role()
    {
        var controller = new ThemeController(Theme(), new FakeStore());
        await controller.InitializeAsync(TestContext.Current.CancellationToken);

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            foreach (var key in new[]
                     {
                         "WindowBackground", "PanelBackground", "CardBackground", "InputBackground",
                         "Foreground", "MutedForeground", "SubtleForeground",
                         "PrimaryText", "OnPrimaryForeground", "FocusRing", "BorderStrong",
                         "DangerText", "WarningText", "SuccessText",
                     })
            {
                Resolve(key, variant);
            }
        }
    }
}

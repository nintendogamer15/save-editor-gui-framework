using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using SaveEditor.ScreenshotDiff;
using SaveEditor.Ui.HeadlessTests.Shell;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Shell;

namespace SaveEditor.Ui.HeadlessTests.Screenshots;

/// <summary>
/// Captures the shell's welcome state, which §12 names as a covered screen in both
/// themes.
/// </summary>
public class ShellScreenshotTests
{
    private static EditorShell BuildWelcomeShell()
    {
        var store = new FakeSettingsStore
        {
            Current = new EditorSettings
            {
                RecentFiles = ["/home/player/saves/Profile1/slot1.dat", "/home/player/saves/slot2.dat"],
            },
        };

        var vm = new EditorShellViewModel(
            new FakeDocumentSession(), new FakeUserInteraction(), store);

        vm.RegisterSections(
        [
            new SectionDescriptor { Key = "player", Title = "Player", Subtitle = "Name, stats, appearance" },
            new SectionDescriptor { Key = "inventory", Title = "Inventory", Subtitle = "Items and equipment" },
        ]);

        // Populate recents synchronously so the capture is not racing a load.
        foreach (var path in store.Current.RecentFiles)
        {
            vm.Recents.Add(new RecentEntry(path, Ui.Display.PathDisplayFormatter.Default.Format(path)));
        }

        return new EditorShell { DataContext = vm };
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Welcome_State_Renders(string variantName)
    {
        var variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;

        var pixels = ScreenshotHarness.Capture(BuildWelcomeShell(), variant);

        Assert.Equal(ScreenshotHarness.Width * ScreenshotHarness.Height * 4, pixels.Length);

        var distinctTones = pixels.Chunk(4).Select(p => (p[0], p[1], p[2])).Distinct().Count();
        Assert.True(distinctTones > 20, $"{variantName} rendered only {distinctTones} distinct colours.");

        ScreenshotBaseline.Verify($"welcome-{variantName.ToLowerInvariant()}", pixels);
    }

    [AvaloniaFact]
    public void Welcome_State_Capture_Is_Deterministic()
    {
        var first = ScreenshotHarness.Capture(BuildWelcomeShell(), ThemeVariant.Dark);
        var second = ScreenshotHarness.Capture(BuildWelcomeShell(), ThemeVariant.Dark);

        var diff = PixelComparator.Compare(first, second);

        // Two separately constructed shells, not one captured twice — this also
        // catches state that leaks in from construction order.
        Assert.True(
            diff.IsIdentical,
            $"Welcome state is not reproducible: {diff.DifferingPixels} of {diff.TotalPixels} pixels differ.");
    }
}

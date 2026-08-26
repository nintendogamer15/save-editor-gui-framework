using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using SaveEditor.ScreenshotDiff;

namespace SaveEditor.Ui.HeadlessTests.Screenshots;

/// <summary>
/// Proves the screenshot pipeline works and is reproducible, without depending on
/// a committed baseline.
/// </summary>
/// <remarks>
/// PLAN.md section 12 makes determinism itself a gate: two runs of the same commit
/// must produce identical captures. Asserting that directly is stronger than
/// comparing against a stored image and needs no golden file, so it runs on every
/// platform. Committed Ubuntu baselines arrive with the real screens in P1.
/// </remarks>
public class ScreenshotHarnessTests
{
    private static Control Fixture() => new Border
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
        Padding = new Avalonia.Thickness(24),
        Child = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Save Editor", FontSize = 32 },
                new Border
                {
                    Width = 240,
                    Height = 48,
                    Background = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
                },
            },
        },
    };

    [AvaloniaFact]
    public void Capture_Produces_A_Frame_Of_The_Expected_Size()
    {
        var pixels = ScreenshotHarness.Capture(Fixture());

        Assert.Equal(ScreenshotHarness.Width * ScreenshotHarness.Height * 4, pixels.Length);
        Assert.Contains(pixels, b => b != 0);
    }

    [AvaloniaFact]
    public void Capture_Is_Deterministic_Across_Runs()
    {
        var first = ScreenshotHarness.Capture(Fixture());
        var second = ScreenshotHarness.Capture(Fixture());

        var diff = PixelComparator.Compare(first, second);

        Assert.True(
            diff.IsIdentical,
            $"Capture is not reproducible: {diff.DifferingPixels} of {diff.TotalPixels} pixels " +
            $"differ, first at index {diff.FirstDifferenceAt}. A screenshot gate built on a " +
            "non-deterministic capture reports noise as regression.");
    }

    [AvaloniaFact]
    public void Comparator_Detects_A_Single_Changed_Pixel()
    {
        var baseline = ScreenshotHarness.Capture(Fixture());
        var mutated = (byte[])baseline.Clone();
        mutated[4 * 12345] ^= 0xFF;

        var diff = PixelComparator.Compare(baseline, mutated);

        Assert.Equal(1, diff.DifferingPixels);
        Assert.Equal(12345, diff.FirstDifferenceAt);
    }
}

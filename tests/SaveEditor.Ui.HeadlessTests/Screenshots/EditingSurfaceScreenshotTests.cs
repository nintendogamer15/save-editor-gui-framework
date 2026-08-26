using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using SaveEditor.ScreenshotDiff;
using SaveEditor.Ui.Gallery.Views;

namespace SaveEditor.Ui.HeadlessTests.Screenshots;

/// <summary>
/// Captures the editing surface, which P3 names as a screenshot subject: pending
/// state and the validation banner.
/// </summary>
/// <remarks>
/// The gallery's editing section is authored with a pre-set pending edit and a
/// pre-set validation error, so a single capture covers both states without the
/// test having to drive input. Committed Ubuntu baselines are seeded from the
/// first CI run, as for the token page; until then these assert the properties
/// that hold on any platform.
/// </remarks>
public class EditingSurfaceScreenshotTests
{
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Editing_Surface_Renders_Content(string variantName)
    {
        var variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;

        var pixels = ScreenshotHarness.Capture(new EditingGalleryView(), variant);

        Assert.Equal(ScreenshotHarness.Width * ScreenshotHarness.Height * 4, pixels.Length);

        var distinctTones = pixels.Chunk(4).Select(p => (p[0], p[1], p[2])).Distinct().Count();
        Assert.True(distinctTones > 20, $"{variantName} rendered only {distinctTones} distinct colours.");
    }

    [AvaloniaFact]
    public void Editing_Surface_Capture_Is_Deterministic()
    {
        var first = ScreenshotHarness.Capture(new EditingGalleryView(), ThemeVariant.Dark);
        var second = ScreenshotHarness.Capture(new EditingGalleryView(), ThemeVariant.Dark);

        var diff = PixelComparator.Compare(first, second);

        // Field cards carry per-field state and a virtualizing list, both of which
        // are plausible sources of frame-to-frame variation. If this ever flaps, the
        // baseline gate would flap with it.
        Assert.True(
            diff.IsIdentical,
            $"Editing surface is not reproducible: {diff.DifferingPixels} of {diff.TotalPixels} pixels differ.");
    }

    [AvaloniaFact]
    public void Editing_Surface_Differs_Between_Themes()
    {
        var light = ScreenshotHarness.Capture(new EditingGalleryView(), ThemeVariant.Light);
        var dark = ScreenshotHarness.Capture(new EditingGalleryView(), ThemeVariant.Dark);

        var diff = PixelComparator.Compare(light, dark);

        Assert.True(
            diff.DifferingPixels > diff.TotalPixels / 2,
            $"Only {diff.DifferingPixels} of {diff.TotalPixels} pixels differ between themes.");
    }
}

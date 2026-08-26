using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using SaveEditor.ScreenshotDiff;
using SaveEditor.Ui.Gallery.Views;

namespace SaveEditor.Ui.HeadlessTests.Screenshots;

/// <summary>
/// Captures the gallery token page, which is P1's visual regression surface.
/// </summary>
/// <remarks>
/// Committed baselines are Ubuntu-golden per PLAN.md §12 and are seeded from a CI
/// run, not from a developer machine — a Windows-generated reference would encode
/// Windows text rasterisation and fail on the platform that owns the baseline.
/// Until that seeding happens these assert the properties that hold on any
/// platform: the page renders, it renders reproducibly, and the two themes
/// genuinely differ.
/// </remarks>
public class TokenGalleryScreenshotTests
{
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Token_Page_Renders_Content(string variantName)
    {
        var variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;

        var pixels = ScreenshotHarness.Capture(new TokenGalleryView(), variant);

        Assert.Equal(ScreenshotHarness.Width * ScreenshotHarness.Height * 4, pixels.Length);

        // A blank or single-colour page would pass a mere "not all zero" check, so
        // require real variety: swatches, text, and borders across many tones.
        var distinctTones = pixels.Chunk(4).Select(p => (p[0], p[1], p[2])).Distinct().Count();
        Assert.True(distinctTones > 20, $"{variantName} rendered only {distinctTones} distinct colours.");

        ScreenshotBaseline.Verify($"tokens-{variantName.ToLowerInvariant()}", pixels);
    }

    [AvaloniaFact]
    public void Token_Page_Capture_Is_Deterministic()
    {
        var first = ScreenshotHarness.Capture(new TokenGalleryView(), ThemeVariant.Dark);
        var second = ScreenshotHarness.Capture(new TokenGalleryView(), ThemeVariant.Dark);

        var diff = PixelComparator.Compare(first, second);

        Assert.True(
            diff.IsIdentical,
            $"Token page is not reproducible: {diff.DifferingPixels} of {diff.TotalPixels} pixels differ.");
    }

    [AvaloniaFact]
    public void Light_And_Dark_Render_Differently()
    {
        var light = ScreenshotHarness.Capture(new TokenGalleryView(), ThemeVariant.Light);
        var dark = ScreenshotHarness.Capture(new TokenGalleryView(), ThemeVariant.Dark);

        var diff = PixelComparator.Compare(light, dark);

        // Guards the failure mode where theme dictionaries silently do not apply and
        // every "both themes" screenshot is quietly the same image twice.
        Assert.True(
            diff.DifferingPixels > diff.TotalPixels / 2,
            $"Only {diff.DifferingPixels} of {diff.TotalPixels} pixels differ between themes; " +
            "the theme variant does not appear to be taking effect.");
    }
}

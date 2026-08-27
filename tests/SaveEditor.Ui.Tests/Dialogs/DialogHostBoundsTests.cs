using SaveEditor.Ui.Dialogs;

namespace SaveEditor.Ui.Tests.Dialogs;

/// <summary>
/// The About/credits dialog must size to the screen, not to its content.
/// </summary>
public sealed class DialogHostBoundsTests
{
    [Fact]
    public void Uses_Ninety_Percent_Of_A_Known_Working_Area()
    {
        var limits = DialogHostBounds.Resolve(requestedWidth: 480, screenWidth: 1920, screenHeight: 1080);

        Assert.Equal(480, limits.Width);
        Assert.Equal(1920 * DialogHostBounds.ScreenFill, limits.MaxWidth);
        Assert.Equal(1080 * DialogHostBounds.ScreenFill, limits.MaxHeight);
        Assert.True(limits.MaxHeight < 1080);
    }

    [Fact]
    public void Narrows_The_Requested_Width_To_A_Small_Screen()
    {
        var limits = DialogHostBounds.Resolve(requestedWidth: 560, screenWidth: 400, screenHeight: 600);

        Assert.Equal(400 * DialogHostBounds.ScreenFill, limits.Width);
        Assert.Equal(limits.MaxWidth, limits.Width);
        Assert.Equal(600 * DialogHostBounds.ScreenFill, limits.MaxHeight);
    }

    [Fact]
    public void Falls_Back_Per_Axis_When_That_Screen_Extent_Is_Unknown_Or_Unusable()
    {
        var bothUnknown = DialogHostBounds.Resolve(requestedWidth: 480, null, null);
        Assert.Equal(480, bothUnknown.Width);
        Assert.Equal(DialogHostBounds.FallbackMaxWidth, bothUnknown.MaxWidth);
        Assert.Equal(DialogHostBounds.FallbackMaxHeight, bothUnknown.MaxHeight);

        var badWidth = DialogHostBounds.Resolve(requestedWidth: 480, 0, 1080);
        Assert.Equal(DialogHostBounds.FallbackMaxWidth, badWidth.MaxWidth);
        Assert.Equal(1080 * DialogHostBounds.ScreenFill, badWidth.MaxHeight);

        var badHeight = DialogHostBounds.Resolve(requestedWidth: 480, 1920, 0);
        Assert.Equal(1920 * DialogHostBounds.ScreenFill, badHeight.MaxWidth);
        Assert.Equal(DialogHostBounds.FallbackMaxHeight, badHeight.MaxHeight);

        foreach (var (width, height) in new (double?, double?)[]
                 {
                     (double.NaN, double.NaN),
                     (double.PositiveInfinity, double.PositiveInfinity),
                     (-1, -1),
                 })
        {
            var limits = DialogHostBounds.Resolve(requestedWidth: 480, width, height);
            Assert.Equal(DialogHostBounds.FallbackMaxWidth, limits.MaxWidth);
            Assert.Equal(DialogHostBounds.FallbackMaxHeight, limits.MaxHeight);
        }
    }
}

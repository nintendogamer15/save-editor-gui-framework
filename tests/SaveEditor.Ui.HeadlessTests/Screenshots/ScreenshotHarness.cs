using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SaveEditor.Ui.HeadlessTests.Screenshots;

/// <summary>
/// Renders a control headlessly at a fixed size and scale and returns raw pixels.
/// </summary>
/// <remarks>
/// <para>
/// Determinism is the whole point. Captures are taken through Skia at a fixed
/// logical size with scaling pinned to 1.0, and the Inter font is embedded in the
/// framework package rather than resolved by family name. Without the embedded
/// font this would silently fall back on a machine that lacks Inter, and the
/// baseline would encode whatever the runner happened to have installed.
/// </para>
/// <para>
/// Transitions are cleared across the realized tree before capture. A capture is a
/// single frame taken after pending jobs drain, so a control mid-transition would
/// contribute whatever value it had reached at that instant — timing-dependent, and
/// enough to make a byte-exact gate flap rather than catch regressions.
/// </para>
/// <para>
/// Committed baselines are generated on Ubuntu and compared there. Windows runs
/// the behavioural tests but not baseline comparison, because the two platforms
/// rasterise text differently and a single golden set cannot serve both.
/// </para>
/// </remarks>
public static class ScreenshotHarness
{
    /// <summary>Fixed capture width in logical pixels.</summary>
    public const int Width = 1600;

    /// <summary>Fixed capture height in logical pixels.</summary>
    public const int Height = 1000;

    /// <summary>Renders a control and returns its pixels as BGRA8888.</summary>
    /// <param name="content">The control to render.</param>
    /// <param name="variant">
    /// Theme variant to render under, or <see langword="null"/> to inherit the
    /// application's. Baselines are captured per variant, so this is how the same
    /// screen produces both a Latte and a Mocha reference.
    /// </param>
    /// <returns>Raw pixel buffer, <see cref="Width"/> by <see cref="Height"/>.</returns>
    public static byte[] Capture(Control content, ThemeVariant? variant = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        var window = new Window
        {
            Width = Width,
            Height = Height,
            Content = content,
        };

        if (variant is not null)
        {
            window.RequestedThemeVariant = variant;
        }

        window.Show();
        Dispatcher.UIThread.RunJobs();

        SuppressTransitions(window);
        Dispatcher.UIThread.RunJobs();

        using var frame = window.CaptureRenderedFrame()
                          ?? throw new InvalidOperationException(
                              "Headless capture returned no frame. The test application must be " +
                              "configured with UseHeadlessDrawing = false and UseSkia().");

        return ToBgraBytes(frame);
    }

    /// <summary>Clears transitions across the realized tree.</summary>
    /// <remarks>
    /// A capture is a single frame taken after pending jobs drain. Any control
    /// mid-transition contributes whatever value the transition had reached at that
    /// instant, which is timing-dependent — so a baseline gate over animated controls
    /// flaps rather than catching regressions. Clearing transitions makes the frame a
    /// function of state alone.
    /// </remarks>
    private static void SuppressTransitions(Visual root)
    {
        if (root is Animatable animatable)
        {
            animatable.Transitions = null;
        }

        foreach (var child in root.GetVisualChildren())
        {
            SuppressTransitions(child);
        }
    }

    private static byte[] ToBgraBytes(WriteableBitmap bitmap)
    {
        using var locked = bitmap.Lock();

        var height = locked.Size.Height;
        var width = locked.Size.Width;
        var rowBytes = width * 4;
        var buffer = new byte[rowBytes * height];

        for (var row = 0; row < height; row++)
        {
            var source = locked.Address + (row * locked.RowBytes);
            System.Runtime.InteropServices.Marshal.Copy(source, buffer, row * rowBytes, rowBytes);
        }

        return buffer;
    }
}

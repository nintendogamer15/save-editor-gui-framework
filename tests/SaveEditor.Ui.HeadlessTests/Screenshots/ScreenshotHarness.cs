using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

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
/// Animation suppression is not yet wired up: nothing rendered so far animates,
/// and <c>Capture</c> takes a single frame after pending jobs drain, so captures
/// are already reproducible — <c>Capture_Is_Deterministic_Across_Runs</c> asserts
/// it. Transitions arrive with real controls in P1, and suppression has to land
/// with them or the first animated control will make this gate flap.
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
    /// <returns>Raw pixel buffer, <see cref="Width"/> by <see cref="Height"/>.</returns>
    public static byte[] Capture(Control content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var window = new Window
        {
            Width = Width,
            Height = Height,
            Content = content,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var frame = window.CaptureRenderedFrame()
                          ?? throw new InvalidOperationException(
                              "Headless capture returned no frame. The test application must be " +
                              "configured with UseHeadlessDrawing = false and UseSkia().");

        return ToBgraBytes(frame);
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

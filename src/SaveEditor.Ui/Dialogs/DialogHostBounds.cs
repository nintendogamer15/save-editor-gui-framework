namespace SaveEditor.Ui.Dialogs;

/// <summary>
/// Caps a themed dialog host so it sizes to the screen, not to an arbitrarily
/// long body of text.
/// </summary>
/// <remarks>
/// Help → About and Help → Safety both feed adopter-supplied text into a message
/// dialog. That text is often every third-party licence the binary ships, which
/// is longer than a display. A host that sizes to content then has no close
/// button on screen. The inner <see cref="DefaultBodyMaxHeight"/> is the bound
/// the scrollable body uses so a short dialog stays short and a long one
/// scrolls; <see cref="Resolve"/> is the bound the window itself uses so even
/// chrome plus that body cannot overrun the working area.
/// </remarks>
internal static class DialogHostBounds
{
    /// <summary>
    /// Default maximum height of a dialog body, in device-independent pixels.
    /// </summary>
    /// <remarks>
    /// Shared by the message, document, and About viewers. Short of a display,
    /// and short enough that title, padding, and a pinned close button still
    /// fit beneath a typical 720p working area once the window itself is also
    /// capped.
    /// </remarks>
    internal const double DefaultBodyMaxHeight = 480;

    /// <summary>Used when the host cannot enumerate a screen.</summary>
    internal const double FallbackMaxWidth = 960;

    /// <summary>Used when the host cannot enumerate a screen.</summary>
    internal const double FallbackMaxHeight = 720;

    /// <summary>Fraction of the working area a dialog may occupy.</summary>
    internal const double ScreenFill = 0.9;

    /// <summary>Window width and maximum extents for one host dialog.</summary>
    /// <param name="Width">The width to assign, already clamped to <paramref name="MaxWidth"/>.</param>
    /// <param name="MaxWidth">The window's <c>MaxWidth</c>.</param>
    /// <param name="MaxHeight">The window's <c>MaxHeight</c>.</param>
    internal readonly record struct Limits(double Width, double MaxWidth, double MaxHeight);

    /// <summary>
    /// Resolves width and maximum extents from a requested width and an optional
    /// working area.
    /// </summary>
    /// <param name="requestedWidth">The width the caller would like.</param>
    /// <param name="screenWidth">Usable width in device-independent pixels, if known.</param>
    /// <param name="screenHeight">Usable height in device-independent pixels, if known.</param>
    /// <returns>Width and maximum extents that fit the screen, or the fallbacks.</returns>
    internal static Limits Resolve(double requestedWidth, double? screenWidth, double? screenHeight)
    {
        var maxWidth = PositiveFinite(screenWidth * ScreenFill, FallbackMaxWidth);
        var maxHeight = PositiveFinite(screenHeight * ScreenFill, FallbackMaxHeight);
        return new Limits(Math.Min(requestedWidth, maxWidth), maxWidth, maxHeight);
    }

    private static double PositiveFinite(double? value, double fallback) =>
        value is { } v && double.IsFinite(v) && v > 0 ? v : fallback;
}

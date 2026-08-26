namespace SaveEditor.Ui.Settings;

/// <summary>A rectangle of usable screen space, in logical units.</summary>
/// <param name="Width">Usable width.</param>
/// <param name="Height">Usable height.</param>
public readonly record struct ScreenArea(double Width, double Height);

/// <summary>
/// Supplies the screen areas a restored window size is clamped against.
/// </summary>
/// <remarks>
/// <para>
/// This is an injected seam rather than a direct call into Avalonia's screen APIs.
/// Two reasons, in order of weight. First, the clamp is a security control against a
/// tampered settings file, and a control that can only be exercised on a machine with
/// a display attached is a control that is not exercised in CI. Second, the settings
/// store is a plain service that must be constructible before — and independently of —
/// any window, which is the same reason <c>EditorShell</c> delegates window authority
/// to <c>IEditorHost</c>.
/// </para>
/// <para>
/// Implementations must not throw. A source that cannot enumerate screens returns an
/// empty list, and the clamp falls back to its absolute plausibility bounds.
/// </para>
/// </remarks>
public interface IScreenBoundsSource
{
    /// <summary>Returns the usable areas of the currently attached screens.</summary>
    /// <returns>
    /// Zero or more areas. An empty result means "unknown", not "no space".
    /// </returns>
    IReadOnlyList<ScreenArea> GetAvailableAreas();
}

/// <summary>
/// A bounds source that reports no screens.
/// </summary>
/// <remarks>
/// The default for a store constructed without a host. Window size is still held to
/// the absolute plausibility range, so a tampered file cannot push an
/// <see cref="int.MaxValue"/> extent through simply because no display was found.
/// </remarks>
public sealed class UnknownScreenBoundsSource : IScreenBoundsSource
{
    /// <summary>The shared instance.</summary>
    public static UnknownScreenBoundsSource Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<ScreenArea> GetAvailableAreas() => [];
}

/// <summary>A bounds source over a fixed set of areas.</summary>
/// <remarks>
/// Used by a host that already knows its screen layout, and by tests that must clamp
/// deterministically without a display.
/// </remarks>
public sealed class FixedScreenBoundsSource : IScreenBoundsSource
{
    private readonly ScreenArea[] _areas;

    /// <summary>Creates a source over the supplied areas.</summary>
    /// <param name="areas">The usable areas.</param>
    public FixedScreenBoundsSource(params ScreenArea[] areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        _areas = [.. areas];
    }

    /// <inheritdoc />
    public IReadOnlyList<ScreenArea> GetAvailableAreas() => _areas;
}

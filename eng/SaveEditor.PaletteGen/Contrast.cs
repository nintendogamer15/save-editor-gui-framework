namespace SaveEditor.PaletteGen;

/// <summary>WCAG 2.x relative luminance and contrast, plus the accent derivation.</summary>
public static class Contrast
{
    /// <summary>Contrast required of body text and of any accent used as text.</summary>
    public const double TextMinimum = 4.5;

    /// <summary>Contrast required of non-text indicators such as focus rings.</summary>
    public const double NonTextMinimum = 3.0;

    /// <summary>Relative luminance per WCAG 2.x.</summary>
    /// <param name="colour">The colour to measure.</param>
    /// <returns>Relative luminance in [0, 1].</returns>
    public static double RelativeLuminance(Srgb colour) =>
        (0.2126 * Linearize(colour.R))
        + (0.7152 * Linearize(colour.G))
        + (0.0722 * Linearize(colour.B));

    /// <summary>Contrast ratio between two colours, from 1.0 to 21.0.</summary>
    /// <param name="a">First colour.</param>
    /// <param name="b">Second colour.</param>
    /// <returns>The ratio, order-independent.</returns>
    public static double Ratio(Srgb a, Srgb b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// Darkens a colour by the smallest factor that reaches
    /// <paramref name="target"/> against every supplied surface.
    /// </summary>
    /// <param name="colour">The colour to derive from.</param>
    /// <param name="surfaces">Every surface the result must be legible on.</param>
    /// <param name="target">Required contrast ratio.</param>
    /// <returns>
    /// The derived colour, which equals <paramref name="colour"/> when it already
    /// passes. Returns <see langword="null"/> when even black does not reach the
    /// target, which cannot happen for any Catppuccin accent on its own flavour's
    /// surfaces but is reported rather than silently approximated.
    /// </returns>
    /// <remarks>
    /// Darkening in sRGB preserves hue, so a derived accent still reads as the
    /// accent the user picked. Catppuccin's light accents are the reason this
    /// exists: in Latte only mauve and red reach 4.5:1 even on the window
    /// background, and none reaches it on a card.
    /// </remarks>
    public static Srgb? DeriveLegible(Srgb colour, IReadOnlyList<Srgb> surfaces, double target)
    {
        ArgumentNullException.ThrowIfNull(surfaces);

        for (var step = 0; step <= 1000; step++)
        {
            var candidate = colour.Darken(1.0 - (step / 1000.0));
            if (MinimumRatio(candidate, surfaces) >= target)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Lowest contrast between a colour and any of the supplied surfaces.</summary>
    /// <param name="colour">The foreground colour.</param>
    /// <param name="surfaces">Surfaces to measure against.</param>
    /// <returns>The minimum ratio.</returns>
    public static double MinimumRatio(Srgb colour, IReadOnlyList<Srgb> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);

        var min = double.MaxValue;
        foreach (var surface in surfaces)
        {
            min = Math.Min(min, Ratio(colour, surface));
        }

        return min;
    }

    /// <summary>
    /// Picks the foreground for text sitting on an accent fill.
    /// </summary>
    /// <param name="fill">The accent used as a fill.</param>
    /// <returns>Whichever of pure white or pure black contrasts more.</returns>
    /// <remarks>
    /// The endpoints are pure rather than palette neutrals deliberately. Measured
    /// against Latte's own base and crust, twelve of the fourteen accents fall
    /// below 4.5:1; against pure endpoints every accent in both flavours clears it,
    /// with Latte blue the worst at 4.91:1.
    /// </remarks>
    public static Srgb OnAccentForeground(Srgb fill)
    {
        var white = new Srgb(0xFF, 0xFF, 0xFF);
        var black = new Srgb(0x00, 0x00, 0x00);
        return Ratio(fill, white) >= Ratio(fill, black) ? white : black;
    }

    private static double Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}

using System.Globalization;

namespace SaveEditor.PaletteGen;

/// <summary>An sRGB colour with 8-bit channels.</summary>
/// <param name="R">Red channel.</param>
/// <param name="G">Green channel.</param>
/// <param name="B">Blue channel.</param>
public readonly record struct Srgb(byte R, byte G, byte B)
{
    /// <summary>Parses a <c>#rrggbb</c> string.</summary>
    /// <param name="hex">The colour, with or without a leading hash.</param>
    /// <returns>The parsed colour.</returns>
    public static Srgb Parse(string hex)
    {
        var span = hex.AsSpan().TrimStart('#');
        if (span.Length != 6)
        {
            throw new FormatException($"Expected a 6-digit hex colour, got '{hex}'.");
        }

        return new Srgb(
            byte.Parse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(span[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>Renders as a lowercase <c>#rrggbb</c> string.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}");

    /// <summary>
    /// Scales every channel toward black by <paramref name="factor"/>, preserving hue.
    /// </summary>
    /// <param name="factor">1.0 leaves the colour unchanged; 0.0 yields black.</param>
    /// <returns>The darkened colour.</returns>
    public Srgb Darken(double factor) => new(
        (byte)Math.Round(R * factor, MidpointRounding.AwayFromZero),
        (byte)Math.Round(G * factor, MidpointRounding.AwayFromZero),
        (byte)Math.Round(B * factor, MidpointRounding.AwayFromZero));
}

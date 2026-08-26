namespace SaveEditor.ScreenshotDiff;

/// <summary>The result of comparing two captures.</summary>
/// <param name="DifferingPixels">Count of pixels that are not byte-identical.</param>
/// <param name="TotalPixels">Total pixels compared.</param>
/// <param name="FirstDifferenceAt">
/// Pixel index of the first difference, or <c>-1</c> when the captures match.
/// </param>
public readonly record struct PixelDiff(int DifferingPixels, int TotalPixels, int FirstDifferenceAt)
{
    /// <summary>Whether the captures are byte-identical.</summary>
    public bool IsIdentical => DifferingPixels == 0;
}

/// <summary>
/// Compares screenshot captures at zero tolerance.
/// </summary>
/// <remarks>
/// <para>
/// Zero tolerance is deliberate. A perceptual threshold hides exactly the class of
/// regression a screenshot test exists to catch — a shifted baseline, a font
/// falling back, a colour drifting by one step — while doing nothing about the
/// genuine sources of noise, which are font availability and GPU rasterisation.
/// Those are addressed by embedding the font and rendering headlessly through
/// Skia at a fixed size and scale, not by loosening the comparison.
/// </para>
/// <para>
/// This lives in <c>eng/</c> rather than taking an image-diff package so the visual
/// gate carries no unpinned third-party tooling.
/// </para>
/// </remarks>
public static class PixelComparator
{
    private const int BytesPerPixel = 4;

    /// <summary>Compares two raw BGRA8888 buffers.</summary>
    /// <param name="expected">Baseline buffer.</param>
    /// <param name="actual">Captured buffer.</param>
    /// <returns>How many pixels differ, and where the first difference is.</returns>
    /// <exception cref="ArgumentException">The buffers are different lengths or not whole pixels.</exception>
    public static PixelDiff Compare(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        if (expected.Length != actual.Length)
        {
            throw new ArgumentException(
                $"Captures differ in size: baseline is {expected.Length} bytes, capture is {actual.Length}. " +
                "This usually means the render size or scale changed, not that pixels drifted.",
                nameof(actual));
        }

        if (expected.Length % BytesPerPixel != 0)
        {
            throw new ArgumentException("Buffer length is not a whole number of BGRA pixels.", nameof(expected));
        }

        var totalPixels = expected.Length / BytesPerPixel;
        var differing = 0;
        var first = -1;

        for (var pixel = 0; pixel < totalPixels; pixel++)
        {
            var offset = pixel * BytesPerPixel;
            if (expected.Slice(offset, BytesPerPixel).SequenceEqual(actual.Slice(offset, BytesPerPixel)))
            {
                continue;
            }

            differing++;
            if (first < 0)
            {
                first = pixel;
            }
        }

        return new PixelDiff(differing, totalPixels, first);
    }

    /// <summary>
    /// Builds a diff mask highlighting differing pixels, for attaching to a failed run.
    /// </summary>
    /// <param name="expected">Baseline buffer.</param>
    /// <param name="actual">Captured buffer.</param>
    /// <returns>
    /// A BGRA buffer the same size as the inputs: matching pixels are dimmed toward
    /// grey, differing pixels are opaque magenta.
    /// </returns>
    public static byte[] BuildDiffMask(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        if (expected.Length != actual.Length)
        {
            throw new ArgumentException("Captures differ in size.", nameof(actual));
        }

        var mask = new byte[expected.Length];

        for (var offset = 0; offset < expected.Length; offset += BytesPerPixel)
        {
            var same = expected.Slice(offset, BytesPerPixel).SequenceEqual(actual.Slice(offset, BytesPerPixel));

            if (same)
            {
                // Dim the unchanged content so differences read at a glance.
                var grey = (byte)((expected[offset] + expected[offset + 1] + expected[offset + 2]) / 3 / 3);
                mask[offset] = grey;
                mask[offset + 1] = grey;
                mask[offset + 2] = grey;
                mask[offset + 3] = 0xFF;
            }
            else
            {
                mask[offset] = 0xFF;     // B
                mask[offset + 1] = 0x00; // G
                mask[offset + 2] = 0xFF; // R
                mask[offset + 3] = 0xFF; // A
            }
        }

        return mask;
    }
}

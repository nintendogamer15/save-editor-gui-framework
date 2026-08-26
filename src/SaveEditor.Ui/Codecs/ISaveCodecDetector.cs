namespace SaveEditor.Ui.Codecs;

/// <summary>A detector's opinion about a candidate file.</summary>
public enum DetectionVerdict
{
    /// <summary>Not this codec's format.</summary>
    Declined,

    /// <summary>Plausibly this format, but not distinctive enough to be sure.</summary>
    Possible,

    /// <summary>Distinctively this format.</summary>
    Confident,
}

/// <summary>
/// Decides whether a file belongs to a codec, from a bounded prefix of its bytes.
/// </summary>
/// <remarks>
/// <para>
/// Every registered detector inspects the same untrusted bytes, so the parsing
/// surface exposed to a hostile save file is the union of all installed codecs,
/// not just the one that eventually matches. Detectors are therefore given a
/// bounded, read-only header slice rather than a seekable stream over the whole
/// file, are run in isolation so that a throwing detector is recorded as
/// <see cref="DetectionVerdict.Declined"/> instead of aborting detection, and are
/// individually time-boxed.
/// </para>
/// <para>
/// Ambiguity between two confident detectors is resolved by asking the user, never
/// by registration order.
/// </para>
/// </remarks>
public interface ISaveCodecDetector
{
    /// <summary>Format this detector recognizes.</summary>
    SaveFormatDescriptor Format { get; }

    /// <summary>
    /// How many leading bytes this detector needs. The registry reads the largest
    /// value across all detectors once and slices per detector.
    /// </summary>
    int HeaderBytesRequired { get; }

    /// <summary>Inspects the header slice.</summary>
    /// <param name="header">
    /// Up to <see cref="HeaderBytesRequired"/> bytes. May be shorter for a small file.
    /// </param>
    /// <returns>This detector's verdict.</returns>
    DetectionVerdict Detect(ReadOnlySpan<byte> header);
}

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

    /// <summary>
    /// The header is consistent with this format, but which format it is cannot be
    /// settled without decoding the payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For formats whose discriminator is not in a plaintext prefix: two game schemas
    /// sharing one encrypted envelope, or one compressed container, are indistinguishable
    /// until the payload is decrypted or inflated. A detector limited to a bounded header
    /// slice cannot answer, and answering
    /// <see cref="Possible"/> would put such a format permanently behind an ambiguity
    /// prompt the user cannot meaningfully resolve (finding F-8).
    /// </para>
    /// <para>
    /// Ranked above <see cref="Possible"/> and below <see cref="Confident"/>: a detector
    /// that recognises its own envelope has stronger evidence than one guessing, and
    /// weaker than one that has actually identified the format. When this is the winning
    /// tier the workflow decodes each candidate once and asks
    /// <see cref="ISaveCodec{TDocument}.ConfirmDecoded"/> to settle it.
    /// </para>
    /// </remarks>
    RequiresDecode,
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
/// <para>
/// A detector whose format cannot be recognised from a prefix at all answers
/// <see cref="DetectionVerdict.RequiresDecode"/> and settles the question in
/// <see cref="ISaveCodec{TDocument}.ConfirmDecoded"/> once the payload has been decoded.
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

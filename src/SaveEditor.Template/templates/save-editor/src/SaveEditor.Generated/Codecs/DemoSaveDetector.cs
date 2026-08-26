using SaveEditor.Ui.Codecs;

namespace SaveEditor.Generated.Codecs;

// ============================================================================
// REPLACE ME FIRST. See DemoSaveCodec.cs and Document/DemoSaveDocument.cs.
// ============================================================================

/// <summary>
/// Recognizes the demo save format from its fixed magic prefix. Delete and
/// replace this alongside <see cref="DemoSaveCodec"/>.
/// </summary>
/// <remarks>
/// Detectors are shown only a bounded header slice, run in isolation, and are
/// individually time-boxed by <c>SaveCodecRegistry</c> — every registered
/// detector inspects the same untrusted bytes from every file a user opens,
/// so keep this cheap and side-effect free regardless of what your real
/// format's header looks like.
/// </remarks>
public sealed class DemoSaveDetector : ISaveCodecDetector
{
    /// <inheritdoc />
    public SaveFormatDescriptor Format { get; } = new(
        Id: "demo-save",
        DisplayName: "Demo save (obviously fake — replace me)",
        Extensions: ["demosave"]);

    /// <inheritdoc />
    public int HeaderBytesRequired => DemoSaveCodec.Magic.Length;

    /// <inheritdoc />
    public DetectionVerdict Detect(ReadOnlySpan<byte> header) =>
        header.Length >= DemoSaveCodec.Magic.Length && header[..DemoSaveCodec.Magic.Length].SequenceEqual(DemoSaveCodec.Magic)
            ? DetectionVerdict.Confident
            : DetectionVerdict.Declined;
}

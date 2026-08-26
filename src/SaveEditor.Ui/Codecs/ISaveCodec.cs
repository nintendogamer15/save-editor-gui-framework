namespace SaveEditor.Ui.Codecs;

/// <summary>Identifies a save format and supplies its file-picker filters.</summary>
/// <param name="Id">Stable identifier, used in settings and diagnostics.</param>
/// <param name="DisplayName">Human-readable format name.</param>
/// <param name="Extensions">Extensions without a leading dot, e.g. <c>sav</c>.</param>
public sealed record SaveFormatDescriptor(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Extensions);

/// <summary>
/// Reads and writes one save format on behalf of a consuming editor.
/// </summary>
/// <typeparam name="TDocument">The editor's in-memory document type.</typeparam>
/// <remarks>
/// <para>
/// A codec is a correctness boundary, not a security boundary. Implementations run
/// in-process at full privilege, so the framework cannot defend against a hostile
/// one; see <c>PLAN.md</c> §8. What it does provide is containment of honest
/// mistakes: bounded inputs, isolated detection, exception containment at the
/// workflow boundary, and round-trip falsification of
/// <see cref="PreservesUnknownData"/> — as far as
/// <see cref="RoundTripEquivalent"/> allows, which is stated exactly there.
/// </para>
/// <para>
/// A codec never receives a handle or path resolving to the destination.
/// Serialization completes into a temporary file in full and is verified before
/// any replacement is attempted.
/// </para>
/// </remarks>
public interface ISaveCodec<TDocument>
{
    /// <summary>Format identity and picker filters.</summary>
    SaveFormatDescriptor Format { get; }

    /// <summary>
    /// Whether this codec round-trips data it does not understand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a claim, not a guarantee, and the framework tests it rather than
    /// simply trusting it: immediately after decoding it re-serializes the
    /// unmodified document and compares the result against the source bytes. A
    /// codec that declares <see langword="true"/> and drops a trailing block or
    /// checksum region is detected and downgraded to the
    /// warning-requiring-confirmation path instead of silently destroying the save.
    /// </para>
    /// <para>
    /// <strong>How much of that is proven depends on
    /// <see cref="RoundTripEquivalent"/>.</strong> Byte-identical re-serialization is
    /// proven by the framework and reported as
    /// <see cref="Workflow.UnknownDataVerification.Verified"/>. A codec that
    /// overrides the comparison is instead taken at its word, and the weaker
    /// <see cref="Workflow.UnknownDataVerification.VerifiedEquivalent"/> is reported
    /// to say so.
    /// </para>
    /// </remarks>
    bool PreservesUnknownData { get; }

    /// <summary>
    /// How a re-serialized round trip is compared against the original bytes when
    /// testing <see cref="PreservesUnknownData"/>.
    /// </summary>
    /// <param name="original">The bytes read from the file.</param>
    /// <param name="reserialized">
    /// The bytes produced by re-serializing the unmodified decoded document.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the two represent the same document for this
    /// format.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The default is byte equality, which the framework can prove for itself.
    /// Override it only for a format whose serialization cannot be byte-identical
    /// even when it is perfectly lossless — one that embeds a fresh random salt or
    /// IV, a timestamp, or a non-deterministic compression dictionary, or one that
    /// normalises whitespace the original happened to contain. An encrypting codec
    /// typically decrypts both sides and compares documents; a normalising text
    /// codec compares parsed trees.
    /// </para>
    /// <para>
    /// <strong>Why this exists rather than a stricter check.</strong> Demanding
    /// byte-identical re-serialization from a codec that derives its key and IV from
    /// an embedded random salt would require pinning that salt across saves — which
    /// means reusing one key and IV across differing plaintexts. The strict check
    /// would therefore have rewarded a real cryptographic regression, and for an
    /// AEAD format nonce reuse is catastrophic rather than merely a confidentiality
    /// leak. The framework would rather report a weaker guarantee honestly than
    /// incentivise unsound cryptography.
    /// </para>
    /// <para>
    /// <strong>What overriding this costs.</strong> The framework compares bytes
    /// first and calls this only on divergence, so an implementation that returns
    /// <see langword="true"/> unconditionally can never manufacture the
    /// byte-identical verdict — but it can suppress a
    /// <see cref="Workflow.UnknownDataVerification.Falsified"/> one it deserves. At
    /// that point the preservation guarantee rests on this method being right, and
    /// the verdict becomes
    /// <see cref="Workflow.UnknownDataVerification.VerifiedEquivalent"/> so that
    /// nothing claims more than was proven. Same posture as the rest of this
    /// interface: a codec is a correctness boundary, not a sandbox.
    /// </para>
    /// </remarks>
    bool RoundTripEquivalent(ReadOnlySpan<byte> original, ReadOnlySpan<byte> reserialized) =>
        original.SequenceEqual(reserialized);

    /// <summary>
    /// Settles a <see cref="DetectionVerdict.RequiresDecode"/> header verdict from the
    /// decoded document.
    /// </summary>
    /// <param name="document">The document this codec has just decoded.</param>
    /// <returns>
    /// <see cref="DetectionVerdict.Confident"/> when the document is this format,
    /// <see cref="DetectionVerdict.Declined"/> when it is not.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Only consulted for a codec whose detector answered
    /// <see cref="DetectionVerdict.RequiresDecode"/>. The default is
    /// <see cref="DetectionVerdict.Confident"/>: a codec that decoded the payload without
    /// throwing has already demonstrated more than any header check could.
    /// </para>
    /// <para>
    /// Override it when several codecs share one envelope and are told apart by something
    /// inside it — a schema marker or version field in the decrypted object. A codec whose
    /// <see cref="DecodeAsync"/> throws on another schema's payload needs no override; the
    /// throw is already a declination.
    /// </para>
    /// <para>
    /// The document reaching here has been decoded from untrusted bytes and is not
    /// validated. Read the discriminator; do not act on the rest of it.
    /// </para>
    /// </remarks>
    DetectionVerdict ConfirmDecoded(TDocument document) => DetectionVerdict.Confident;

    /// <summary>Decodes a document from the supplied stream.</summary>
    /// <param name="source">Read-only stream over the save file.</param>
    /// <param name="cancellationToken">
    /// Cooperative. The workflow abandons a cancelled operation and discards any
    /// late result regardless of whether the codec honours this.
    /// </param>
    /// <returns>The decoded document.</returns>
    ValueTask<TDocument> DecodeAsync(Stream source, CancellationToken cancellationToken = default);

    /// <summary>Writes a document to the supplied stream in full.</summary>
    /// <param name="document">The document to write.</param>
    /// <param name="destination">A temporary stream, never the destination file.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    ValueTask SerializeAsync(
        TDocument document,
        Stream destination,
        CancellationToken cancellationToken = default);

    /// <summary>Validates a document immediately before it is written.</summary>
    /// <param name="document">The document to validate.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Findings; errors block the write, warnings require acceptance.</returns>
    ValueTask<ValidationReport> ValidateAsync(
        TDocument document,
        CancellationToken cancellationToken = default);
}

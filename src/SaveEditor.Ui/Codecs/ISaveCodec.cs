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
/// <see cref="PreservesUnknownData"/>.
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
    /// This is a claim, not a guarantee, and the framework verifies it rather than
    /// trusting it: immediately after decoding it re-serializes the unmodified
    /// document and compares against the source bytes. A codec that declares
    /// <see langword="true"/> and drops a trailing block or checksum region is
    /// detected and downgraded to the warning-requiring-confirmation path instead
    /// of silently destroying the save.
    /// </remarks>
    bool PreservesUnknownData { get; }

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

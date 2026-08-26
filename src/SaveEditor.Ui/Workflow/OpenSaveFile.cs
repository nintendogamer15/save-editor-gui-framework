using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Workflow;

/// <summary>
/// What the framework concluded about a codec's unknown-data preservation claim, by
/// testing it rather than by believing it.
/// </summary>
public enum UnknownDataVerification
{
    /// <summary>The codec did not claim to preserve unknown data.</summary>
    NotClaimed,

    /// <summary>The claim was tested and held: re-serializing the untouched document reproduced the source bytes.</summary>
    Verified,

    /// <summary>
    /// The claim was tested and is empirically false.
    /// </summary>
    /// <remarks>
    /// Writing through this codec loses bytes that were in the file. Every subsequent
    /// destructive write is downgraded to the warning-requiring-confirmation path
    /// automatically, rather than being reported as a clean save.
    /// </remarks>
    Falsified,

    /// <summary>The check was skipped — opted out, or the file is above the size threshold.</summary>
    Skipped,

    /// <summary>The check could not be run because the codec failed while re-serializing.</summary>
    Unavailable,
}

/// <summary>
/// An open save file: the retained handle, the identity recorded for it, the
/// change-detection baseline, and the codec that decoded it.
/// </summary>
/// <typeparam name="TDocument">The editor's in-memory document type.</typeparam>
/// <remarks>
/// <para>
/// The handle is held for the lifetime of the open document, not just for the read. On
/// Windows it is held with write sharing denied, which is what excludes cooperative
/// external writers between the change check and the replace; on Linux it narrows that
/// window rather than closing it. Disposing releases the handle and ends both properties.
/// </para>
/// <para>
/// The workflow never re-resolves <see cref="Path"/> as a string in order to act on the
/// file. The one exception is the replace itself, which the operating system only offers
/// as a name-based operation; the identity re-assertion immediately before it is what
/// bounds that.
/// </para>
/// </remarks>
public sealed class OpenSaveFile<TDocument> : IDisposable
{
    private ResolvedFile _file;
    private bool _disposed;

    internal OpenSaveFile(
        ResolvedFile file,
        ISaveCodec<TDocument> codec,
        TDocument document,
        ContentBaseline baseline,
        UnknownDataVerification unknownData,
        string unknownDataDetail)
    {
        _file = file;
        Codec = codec;
        Document = document;
        Baseline = baseline;
        UnknownData = unknownData;
        UnknownDataDetail = unknownDataDetail;
    }

    /// <summary>The fully resolved path. For display and logging; never for re-opening.</summary>
    public string Path => _file.CanonicalPath;

    /// <summary>The codec that decoded this file and will serialize it back.</summary>
    public ISaveCodec<TDocument> Codec { get; }

    /// <summary>The document as it was decoded, before any edit.</summary>
    public TDocument Document { get; }

    /// <summary>What the bytes were when they were read. Updated after a successful write.</summary>
    public ContentBaseline Baseline { get; internal set; }

    /// <summary>Whether the codec's preservation claim survived being tested.</summary>
    public UnknownDataVerification UnknownData { get; }

    /// <summary>Framework-authored explanation of the preservation verdict.</summary>
    public string UnknownDataDetail { get; }

    /// <summary>Identity recorded when the handle was opened.</summary>
    public FileIdentity Identity => _file.Identity;

    /// <summary>Number of hard links to this file at resolution time.</summary>
    public int HardLinkCount => _file.HardLinkCount;

    /// <summary>
    /// Whether the retained handle no longer refers to the file at <see cref="Path"/>.
    /// </summary>
    /// <remarks>
    /// A successful replace unlinks the object this handle was opened on, so the workflow
    /// re-resolves the destination and rebinds. If that re-resolution fails, the write has
    /// still succeeded but the change-detection baseline can no longer be re-verified, so
    /// the document is marked stale and a further overwrite is refused until it is
    /// reopened. Refusing is the only safe answer: a handle that cannot prove what it
    /// points at cannot guard a destructive write.
    /// </remarks>
    public bool IsStale { get; internal set; }

    internal ResolvedFile File => _file;

    internal void Rebind(ResolvedFile file, ContentBaseline baseline)
    {
        var previous = _file;
        _file = file;
        Baseline = baseline;
        IsStale = false;
        previous.Dispose();
    }

    /// <summary>Releases the retained handle.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _file.Dispose();
    }
}

/// <summary>The outcome of opening a save file.</summary>
/// <typeparam name="TDocument">The editor's in-memory document type.</typeparam>
public abstract record OpenOutcome<TDocument>
{
    private OpenOutcome()
    {
    }

    /// <summary>The file was resolved, detected, decoded, and is now open.</summary>
    /// <param name="File">The open file. The caller owns disposal.</param>
    public sealed record Opened(OpenSaveFile<TDocument> File) : OpenOutcome<TDocument>;

    /// <summary>The user declined a confirmation the open required.</summary>
    /// <param name="Message">Framework-authored explanation.</param>
    public sealed record Declined(string Message) : OpenOutcome<TDocument>;

    /// <summary>The open was cancelled at the workflow boundary.</summary>
    public sealed record Cancelled : OpenOutcome<TDocument>;

    /// <summary>The open failed. Nothing was written and no handle is retained.</summary>
    /// <param name="Reason">Machine-readable cause.</param>
    /// <param name="Message">Framework-authored explanation.</param>
    public sealed record Failed(SaveFailureReason Reason, string Message) : OpenOutcome<TDocument>;
}

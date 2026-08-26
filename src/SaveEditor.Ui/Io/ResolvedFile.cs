namespace SaveEditor.Ui.Io;

/// <summary>
/// A file that has been resolved once, with link following disabled, and whose
/// identity is retained so later steps can prove they are acting on the same
/// object.
/// </summary>
/// <remarks>
/// <para>
/// This type exists to make "resolve once, then operate on the handle" the only
/// convenient way to use the framework's file APIs. Every step after resolution
/// — hashing, backing up, serializing, replacing — must either use
/// <see cref="Stream"/> or call <see cref="ReassertIdentity"/>. No step may
/// re-resolve <see cref="CanonicalPath"/> as a string, because that reintroduces
/// the check-then-use race the resolution was performed to close.
/// </para>
/// <para>
/// Disposing releases the handle. While it is held, Windows callers additionally
/// deny write sharing, which excludes cooperative external writers for the
/// lifetime of the operation. Linux offers no equivalent, so there the retained
/// handle narrows the race rather than closing it.
/// </para>
/// </remarks>
public sealed class ResolvedFile : IDisposable
{
    private readonly FileStream _stream;
    private readonly Func<FileStream, FileIdentity> _identityProbe;
    private bool _disposed;

    /// <summary>Creates a resolved file around an already-opened, already-checked handle.</summary>
    /// <param name="stream">The open handle. This instance takes ownership.</param>
    /// <param name="canonicalPath">The fully resolved path, for display and logging only.</param>
    /// <param name="identity">Volume and file identity recorded at open time.</param>
    /// <param name="hardLinkCount">Number of directory entries referring to this file.</param>
    /// <param name="identityProbe">
    /// Re-reads identity from an open handle. Supplied by the platform resolver so
    /// that <see cref="ReassertIdentity"/> works without static state and can be
    /// substituted in tests that simulate a swap.
    /// </param>
    public ResolvedFile(
        FileStream stream,
        string canonicalPath,
        FileIdentity identity,
        int hardLinkCount,
        Func<FileStream, FileIdentity> identityProbe)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(canonicalPath);
        ArgumentNullException.ThrowIfNull(identityProbe);

        _stream = stream;
        _identityProbe = identityProbe;
        CanonicalPath = canonicalPath;
        Identity = identity;
        HardLinkCount = hardLinkCount;
    }

    /// <summary>The retained handle. Every read and write in the operation goes through this.</summary>
    public FileStream Stream
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _stream;
        }
    }

    /// <summary>
    /// The fully resolved path. Suitable for display and logging; not for re-opening.
    /// </summary>
    public string CanonicalPath { get; }

    /// <summary>Identity recorded when the handle was opened.</summary>
    public FileIdentity Identity { get; }

    /// <summary>Number of hard links to this file at resolution time.</summary>
    public int HardLinkCount { get; }

    /// <summary>
    /// Re-reads identity from the retained handle and reports whether it still
    /// matches what was recorded at resolution time.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the handle still refers to the same object.
    /// </returns>
    /// <remarks>
    /// Called immediately before any destructive step. A <see langword="false"/>
    /// result aborts the operation; it never prompts to continue anyway.
    /// </remarks>
    public bool ReassertIdentity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _identityProbe(_stream) == Identity;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}

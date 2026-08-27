namespace SaveEditor.Ui.Io;

/// <summary>What the caller intends to do with the path, which changes what is checked.</summary>
public enum PathResolutionMode
{
    /// <summary>
    /// Open a file that must already exist. The leaf and every ancestor are checked.
    /// </summary>
    OpenExisting,

    /// <summary>
    /// Prepare to create a file that does not yet exist. Every ancestor component is
    /// checked and the containing directory is resolved; the leaf must not exist.
    /// </summary>
    /// <remarks>
    /// Save As to a new path takes this mode. Without it, a resolver that always
    /// required an existing leaf would push callers into unchecked path handling
    /// for precisely the write that creates a new file.
    /// </remarks>
    CreateNew,
}

/// <summary>Limits and permissions applied during resolution.</summary>
public sealed record PathResolutionOptions
{
    /// <summary>What the caller intends to do with the path.</summary>
    public PathResolutionMode Mode { get; init; } = PathResolutionMode.OpenExisting;

    /// <summary>
    /// Largest input accepted without refusal. Guards against unbounded reads from
    /// device-like targets and absurdly large files.
    /// </summary>
    public long MaxBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>
    /// Size above which the user is asked before the framework reads the file.
    /// </summary>
    public long ConfirmAboveBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Whether UNC and other non-local paths are permitted. Off by default.
    /// </summary>
    /// <remarks>
    /// A stored UNC path that is opened or probed automatically triggers an
    /// outbound SMB connection and an NTLM authentication attempt on Windows.
    /// Editors that genuinely edit saves on a network share opt in explicitly.
    /// </remarks>
    public bool AllowNonLocalPaths { get; init; }

    /// <summary>Whether the handle is opened for writing as well as reading.</summary>
    public bool ForWriting { get; init; }
}

/// <summary>
/// Resolves a user-supplied path to a retained, identity-recorded handle, refusing
/// anything the framework will not operate on.
/// </summary>
/// <remarks>
/// This is the single entry point through which every path reaches the filesystem:
/// the open workflow, recents, drag and drop, backups, and temporary files.
/// Bypassing it reintroduces the class of defects it exists to prevent.
/// </remarks>
public interface ISafePathResolver
{
    /// <summary>Resolves a path, or explains why it was refused.</summary>
    /// <param name="path">The user-supplied path.</param>
    /// <param name="options">Limits and permissions for this resolution.</param>
    /// <param name="cancellationToken">Cancels a resolution that blocks.</param>
    /// <returns>
    /// <see cref="PathResolution.Resolved"/>, <see cref="PathResolution.NeedsConfirmation"/>,
    /// or <see cref="PathResolution.Refused"/>. Never throws for an untrusted or
    /// hostile path; refusal is a result, not an exception.
    /// </returns>
    ValueTask<PathResolution> ResolveAsync(
        string path,
        PathResolutionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new file at <paramref name="path"/>, failing if anything already
    /// exists there.
    /// </summary>
    /// <param name="path">Where to create the file.</param>
    /// <param name="options">Limits and permissions for this resolution.</param>
    /// <param name="cancellationToken">Cancels a creation that blocks.</param>
    /// <returns>The created file, or a refusal.</returns>
    /// <remarks>
    /// Exclusive-create only. A pre-existing entry — including a symlink or hard
    /// link planted at a predictable temporary or backup path — is a refusal, never
    /// a retry through a link-following open. Backup and temporary files are created
    /// exclusively through this method.
    /// </remarks>
    ValueTask<PathResolution> CreateNewAsync(
        string path,
        PathResolutionOptions options,
        CancellationToken cancellationToken = default);
}

namespace SaveEditor.Ui.Io;

/// <summary>
/// Why the framework refused to operate on a path.
/// </summary>
/// <remarks>
/// Refusal is always terminal for the requested operation. The framework never
/// responds to a refusal by relaxing a check, clearing an attribute, elevating,
/// or deleting and recreating the target.
/// </remarks>
public enum PathRefusalReason
{
    /// <summary>The path, or a component of it, does not exist.</summary>
    NotFound,

    /// <summary>The final component is a symbolic link or a reparse point.</summary>
    LinkTarget,

    /// <summary>
    /// A directory component between the volume root and the leaf is a symbolic
    /// link, junction, mount point, or other reparse point.
    /// </summary>
    /// <remarks>
    /// Checked separately from <see cref="LinkTarget"/> because a leaf-only check
    /// is the common mistake: a link in an intermediate component redirects the
    /// write just as effectively while the leaf itself looks like a plain file.
    /// </remarks>
    LinkInAncestor,

    /// <summary>The target is not a regular file — a FIFO, device, socket, or named pipe.</summary>
    NotARegularFile,

    /// <summary>The target exceeds the configured maximum input size.</summary>
    TooLarge,

    /// <summary>The path is not rooted and local, and non-local paths were not permitted.</summary>
    NonLocalPath,

    /// <summary>The path is syntactically unusable, reserved, or names a device namespace.</summary>
    InvalidPath,

    /// <summary>The target exists but could not be opened for the requested access.</summary>
    Unreadable,

    /// <summary>The target is read-only, immutable, or otherwise write-protected.</summary>
    /// <remarks>Reported rather than cleared; see <see cref="PathRefusalReason"/>.</remarks>
    WriteProtected,
}

/// <summary>
/// A condition that does not refuse the operation outright but requires the user
/// to accept it explicitly before the framework proceeds.
/// </summary>
public enum PathConfirmationKind
{
    /// <summary>
    /// The file has more than one hard link, so replacing its contents changes
    /// every alias. Some mod managers and sync tools create such aliases.
    /// </summary>
    MultipleHardLinks,

    /// <summary>The file is larger than the size at which the framework asks before reading.</summary>
    UnusuallyLarge,
}

/// <summary>
/// The outcome of resolving a path through <see cref="ISafePathResolver"/>.
/// </summary>
public abstract record PathResolution
{
    private PathResolution() { }

    /// <summary>The path resolved to a regular file and the handle is retained.</summary>
    /// <param name="File">The opened, identity-recorded file. The caller owns disposal.</param>
    public sealed record Resolved(ResolvedFile File) : PathResolution;

    /// <summary>
    /// The path resolved, but a condition needs explicit user acceptance before use.
    /// </summary>
    /// <param name="File">The opened file. The caller owns disposal whether or not it proceeds.</param>
    /// <param name="Kind">What the user must accept.</param>
    public sealed record NeedsConfirmation(ResolvedFile File, PathConfirmationKind Kind) : PathResolution;

    /// <summary>The path was refused. No handle is produced.</summary>
    /// <param name="Reason">Machine-readable refusal cause.</param>
    /// <param name="Detail">
    /// Operator-facing detail. Never rendered into a destructive confirmation
    /// without passing through the shared path formatter.
    /// </param>
    public sealed record Refused(PathRefusalReason Reason, string Detail) : PathResolution;
}

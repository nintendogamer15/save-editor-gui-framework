namespace SaveEditor.Ui.Shell;

/// <summary>Progress through a long document operation.</summary>
/// <param name="Description">Human-readable phase, ready to show.</param>
/// <param name="Fraction">
/// Completion in [0, 1], or <see langword="null"/> when the phase has no measurable
/// size — hashing a file of unknown length, or waiting on a codec.
/// </param>
/// <param name="IsFinished">Whether the operation has ended and progress should clear.</param>
/// <remarks>
/// Deliberately a shell-level type rather than the workflow's own. The shell must not
/// depend on the workflow — that is what let it be built and tested against a stub —
/// so the session translates rather than the shell reaching across.
/// </remarks>
public readonly record struct DocumentProgress(
    string Description,
    double? Fraction,
    bool IsFinished = false);

/// <summary>
/// The shell's view of the open document and the operations it can invoke on it.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam between the shell and the safe file workflow. P2 ships the
/// shell against a stub; P4 replaces the implementation with the real workflow
/// without the shell changing. Keeping it an interface is what lets shell
/// navigation, menus, and the exit guard be tested headlessly without touching a
/// filesystem.
/// </para>
/// <para>
/// Every operation is asynchronous and cancellable because the real implementation
/// hashes, validates, backs up, and replaces files, any of which can be slow on a
/// large save or a network volume.
/// </para>
/// </remarks>
public interface IDocumentSession
{
    /// <summary>Whether a document is currently open.</summary>
    bool HasDocument { get; }

    /// <summary>Whether committed changes have not yet been written.</summary>
    bool IsDirty { get; }

    /// <summary>
    /// Whether edits have been typed but not applied.
    /// </summary>
    /// <remarks>
    /// Tracked separately from <see cref="IsDirty"/>. Pending edits are in-memory
    /// drafts that survive section navigation and are never written to disk; losing
    /// them silently on close is the failure the exit guard exists to prevent.
    /// </remarks>
    bool HasPendingEdits { get; }

    /// <summary>Path of the open document, or <see langword="null"/>.</summary>
    string? CurrentPath { get; }

    /// <summary>
    /// Where the last backup was written, or <see langword="null"/> if none.
    /// </summary>
    /// <remarks>
    /// Named separately from the outcome sentence because "a backup was written" and
    /// "here is where to find it" are different pieces of information, and only the
    /// second is any use to somebody who needs to recover.
    /// </remarks>
    string? LastBackupPath { get; }

    /// <summary>Raised as a long operation advances, for the status bar.</summary>
    /// <remarks>
    /// Reported rather than polled because the workflow hashes, backs up, and
    /// verifies, any of which can take real time on a large save or a network volume.
    /// A status bar that only updates when the operation ends cannot distinguish slow
    /// from hung, which is when a user reaches for the process manager.
    /// </remarks>
    event EventHandler<DocumentProgress>? ProgressChanged;

    /// <summary>
    /// Full-sentence outcome of the last open or write, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The status bar is the canonical outcome channel, so the sentence it shows has
    /// to come from whatever actually performed the operation. A shell that composed
    /// its own message would be reporting what it asked for rather than what happened
    /// — and the difference between those two is the entire point of a definitive
    /// status.
    /// </remarks>
    string? LastStatusMessage { get; }

    /// <summary>Whether an undo is available.</summary>
    bool CanUndo { get; }

    /// <summary>Whether a redo is available.</summary>
    bool CanRedo { get; }

    /// <summary>Raised whenever any of the state above changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>Opens a document from a path.</summary>
    /// <param name="path">Path supplied by a picker, a recent entry, or a drop.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    ValueTask OpenAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Opens a document stored as a directory rather than a single file.</summary>
    /// <param name="path">The directory to open.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <remarks>
    /// Distinct from <see cref="OpenAsync"/> because many games store a save as a
    /// directory of related files. Folders cannot share the file entry point: the
    /// path resolver refuses anything that is not a regular file, so routing a
    /// directory through it would be refused by design.
    /// </remarks>
    ValueTask OpenFolderAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Writes the document to a newly chosen path. The default write path.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask SaveAsAsync(CancellationToken cancellationToken = default);

    /// <summary>Backs up and then replaces the original file.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask OverwriteWithBackupAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-reads the document from disk, discarding uncommitted state.</summary>
    /// <param name="cancellationToken">Cancels the reload.</param>
    ValueTask ReloadAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the document.</summary>
    /// <param name="cancellationToken">Cancels the close.</param>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>Reverts the last committed operation.</summary>
    void Undo();

    /// <summary>Reapplies the last undone operation.</summary>
    void Redo();

    /// <summary>Discards pending, unapplied edits.</summary>
    void DiscardPendingEdits();
}

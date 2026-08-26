namespace SaveEditor.Ui.Shell;

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

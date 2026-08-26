using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Editing;
using SaveEditor.Ui.Shell;

namespace SaveEditor.Ui.Workflow;

/// <summary>Opens a document stored as a directory rather than a single file.</summary>
/// <typeparam name="TDocument">The editor's document type.</typeparam>
/// <remarks>
/// Folder-backed saves are format-specific, so the framework has no default. An
/// editor whose saves are directories supplies one; an editor whose saves are files
/// supplies nothing and the menu item reports that folders are not supported rather
/// than doing something surprising.
/// </remarks>
public interface IFolderDocumentOpener<TDocument>
{
    /// <summary>Opens a document from a directory.</summary>
    /// <param name="path">The directory.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The document and the codec that should write it back.</returns>
    ValueTask<(TDocument Document, ISaveCodec<TDocument> Codec)> OpenAsync(
        string path,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Connects the shell to <see cref="SafeFileWorkflow{TDocument}"/>, replacing the
/// stub the shell was built against.
/// </summary>
/// <typeparam name="TDocument">The editor's document type.</typeparam>
/// <remarks>
/// <para>
/// This is where the shell's guarantees and the workflow's meet. The shell owns
/// asking before losing work; the workflow owns not losing the file. Neither knows
/// about the other, which is why the shell could be built and tested against a stub
/// and this type is the only thing that changed when the real workflow arrived.
/// </para>
/// <para>
/// Failure is fail-loud, matching the workflow and deliberately unlike the settings
/// store: a save that silently did not happen is the worst outcome in the product,
/// so every outcome lands in <see cref="LastOutcome"/> and is reported.
/// </para>
/// </remarks>
public sealed class DocumentSession<TDocument> : IDocumentSession, IDisposable
{
    private readonly SafeFileWorkflow<TDocument> _workflow;
    private readonly EditHistory _history;
    private readonly ISaveCodec<TDocument> _defaultCodec;
    private readonly IFolderDocumentOpener<TDocument>? _folderOpener;
    private OpenSaveFile<TDocument>? _open;
    private bool _disposed;

    /// <summary>Creates a session over a workflow.</summary>
    /// <param name="workflow">The safe file workflow.</param>
    /// <param name="history">Where committed edits are recorded.</param>
    /// <param name="defaultCodec">Codec used when writing a document opened from a folder.</param>
    /// <param name="folderOpener">Optional folder support.</param>
    public DocumentSession(
        SafeFileWorkflow<TDocument> workflow,
        EditHistory history,
        ISaveCodec<TDocument> defaultCodec,
        IFolderDocumentOpener<TDocument>? folderOpener = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(defaultCodec);

        _workflow = workflow;
        _history = history;
        _defaultCodec = defaultCodec;
        _folderOpener = folderOpener;

        _history.Changed += OnHistoryChanged;
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <summary>Raised when a different document becomes current, so sections can rebuild.</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>Raised after a document is opened, carrying its canonical path.</summary>
    /// <remarks>Composition wires this to the recents list.</remarks>
    public event EventHandler<string>? Opened;

    /// <summary>The document currently open, if any.</summary>
    public TDocument? Document { get; private set; }

    /// <summary>Replaces the current document in place.</summary>
    /// <param name="document">The new document value.</param>
    /// <exception cref="InvalidOperationException">Nothing is open.</exception>
    /// <remarks>
    /// Editors whose document is mutable never need this: their field accessors write
    /// through to the object the session already holds. Editors whose document is an
    /// immutable record do, because every edit produces a new value and the session
    /// would otherwise keep serializing the one it opened. The retained file handle,
    /// its identity, and the change baseline are untouched — this replaces what will
    /// be written, never what is being written over.
    /// </remarks>
    public void ReplaceDocument(TDocument document)
    {
        if (!HasDocument)
        {
            throw new InvalidOperationException("There is no open document to replace.");
        }

        Document = document;
        Notify();
    }

    /// <summary>The outcome of the last open or write. Never silently discarded.</summary>
    public SaveOutcome? LastOutcome { get; private set; }

    /// <summary>
    /// Reports whether uncommitted drafts exist.
    /// </summary>
    /// <remarks>
    /// Supplied by composition because drafts live on the field view-models, which
    /// this type deliberately knows nothing about. Leaving it unset makes the exit
    /// guard blind to typed-but-unapplied edits, so an editor with fields must set it.
    /// </remarks>
    public Func<bool>? PendingEditProbe { get; set; }

    /// <inheritdoc />
    public bool HasDocument => _open is not null || Document is not null;

    /// <inheritdoc />
    public bool IsDirty => _history.IsDirty;

    /// <inheritdoc />
    public bool HasPendingEdits => PendingEditProbe?.Invoke() ?? false;

    /// <inheritdoc />
    public string? CurrentPath { get; private set; }

    /// <inheritdoc />
    public string? LastStatusMessage => LastOutcome?.Message;

    /// <inheritdoc />
    public string? LastBackupPath => LastOutcome?.BackupPath;

    /// <inheritdoc />
    public event EventHandler<DocumentProgress>? ProgressChanged;

    /// <summary>Translates workflow progress into something a status bar can show.</summary>
    /// <remarks>
    /// The phrasing describes what is happening to the user's file rather than naming
    /// an internal phase. "Checking the file has not changed" is actionable if it
    /// stalls; "CheckingForExternalChange" is not.
    /// </remarks>
    private IProgress<SaveProgress> Progress() => new Progress<SaveProgress>(report =>
    {
        var description = report.Phase switch
        {
            SavePhase.Reading => "Reading the save file",
            SavePhase.Detecting => "Identifying the save format",
            SavePhase.Decoding => "Reading the save contents",
            SavePhase.VerifyingPreservationClaim => "Checking nothing would be lost",
            SavePhase.Validating => "Validating",
            SavePhase.WritingBackup => "Writing a backup",
            SavePhase.VerifyingBackup => "Verifying the backup",
            SavePhase.Serializing => "Preparing the new contents",
            SavePhase.VerifyingRoundTrip => "Checking nothing would be lost",
            SavePhase.WritingTemp => "Writing",
            SavePhase.PreservingPermissions => "Preserving permissions",
            SavePhase.CheckingForExternalChange => "Checking the file has not changed",
            SavePhase.Replacing => "Replacing the original",
            _ => "Finishing",
        };

        var fraction = report.BytesTotal is > 0
            ? Math.Clamp((double)report.BytesCompleted / report.BytesTotal.Value, 0, 1)
            : (double?)null;

        ProgressChanged?.Invoke(
            this,
            new DocumentProgress(description, fraction, report.Phase == SavePhase.Completed));
    });

    /// <inheritdoc />
    public bool CanUndo => _history.CanUndo;

    /// <inheritdoc />
    public bool CanRedo => _history.CanRedo;

    /// <inheritdoc />
    public async ValueTask OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var outcome = await _workflow.OpenAsync(path, Progress(), cancellationToken).ConfigureAwait(true);

        switch (outcome)
        {
            case OpenOutcome<TDocument>.Opened opened:
                Adopt(opened.File);
                LastOutcome = SaveOutcome.Success(opened.File.Path);
                Opened?.Invoke(this, opened.File.Path);
                break;

            case OpenOutcome<TDocument>.Declined declined:
                LastOutcome = SaveOutcome.Declined(declined.Message);
                break;

            case OpenOutcome<TDocument>.Cancelled:
                LastOutcome = SaveOutcome.Cancelled();
                break;

            case OpenOutcome<TDocument>.Failed failed:
                LastOutcome = SaveOutcome.Failure(failed.Reason, failed.Message, path);
                break;
        }

        Notify();
    }

    /// <inheritdoc />
    public async ValueTask OpenFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_folderOpener is null)
        {
            LastOutcome = SaveOutcome.Declined(
                "This editor does not support folder-based saves.");
            Notify();
            return;
        }

        var (document, _) = await _folderOpener.OpenAsync(path, cancellationToken).ConfigureAwait(true);

        // A folder-backed document has no single retained handle, so it has no
        // external-change baseline either. Overwrite is unavailable until it has been
        // written to a file; Save As remains, which is the default path anyway.
        ReleaseOpenFile();
        Document = document;
        CurrentPath = path;
        _history.Clear();

        LastOutcome = SaveOutcome.Success(path);
        Opened?.Invoke(this, path);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        Notify();
    }

    /// <inheritdoc />
    public async ValueTask SaveAsAsync(CancellationToken cancellationToken = default)
    {
        if (Document is not { } document)
        {
            LastOutcome = SaveOutcome.Declined("There is nothing open to save.");
            Notify();
            return;
        }

        var codec = _open?.Codec ?? _defaultCodec;
        var outcome = await _workflow
            .SaveAsAsync(document, codec, _open, Progress(), cancellationToken)
            .ConfigureAwait(true);

        LastOutcome = outcome;

        if (outcome.IsSuccess && outcome.Path is { } written)
        {
            // Reopen the destination rather than retargeting the existing handle. The
            // external-change baseline has to describe the file now being edited, and
            // the only honest way to get one is to read what actually landed on disk.
            await ReopenAsync(written, cancellationToken).ConfigureAwait(true);
            _history.MarkSaved();
            Opened?.Invoke(this, written);
        }

        Notify();
    }

    /// <inheritdoc />
    public async ValueTask OverwriteWithBackupAsync(CancellationToken cancellationToken = default)
    {
        if (Document is not { } document || _open is null)
        {
            LastOutcome = SaveOutcome.Declined(
                "Overwrite needs a file opened from disk. Use Save As instead.");
            Notify();
            return;
        }

        var outcome = await _workflow
            .OverwriteWithBackupAsync(document, _open, Progress(), cancellationToken)
            .ConfigureAwait(true);

        LastOutcome = outcome;

        if (outcome.IsSuccess)
        {
            _history.MarkSaved();
        }

        Notify();
    }

    /// <inheritdoc />
    public async ValueTask ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentPath is not { } path)
        {
            LastOutcome = SaveOutcome.Declined("There is nothing open to reload.");
            Notify();
            return;
        }

        await OpenAsync(path, cancellationToken).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        ReleaseOpenFile();
        Document = default;
        CurrentPath = null;
        _history.Clear();

        LastOutcome = null;
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        Notify();

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Undo()
    {
        _history.Undo();
        Notify();
    }

    /// <inheritdoc />
    public void Redo()
    {
        _history.Redo();
        Notify();
    }

    /// <inheritdoc />
    public void DiscardPendingEdits()
    {
        // Drafts live on the field view-models; the shell reverts them and this exists
        // so the session's contract is complete rather than half-implemented.
        Notify();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _history.Changed -= OnHistoryChanged;
        ReleaseOpenFile();
    }

    private async ValueTask ReopenAsync(string path, CancellationToken cancellationToken)
    {
        var reopened = await _workflow.OpenAsync(path, Progress(), cancellationToken).ConfigureAwait(true);

        if (reopened is OpenOutcome<TDocument>.Opened opened)
        {
            Adopt(opened.File);
        }
        else
        {
            // The write succeeded but the file cannot be reopened. Keep the document in
            // memory and drop the stale handle rather than reporting a state that no
            // longer matches disk.
            ReleaseOpenFile();
            CurrentPath = path;
        }
    }

    private void Adopt(OpenSaveFile<TDocument> file)
    {
        ReleaseOpenFile();

        _open = file;
        Document = file.Document;
        CurrentPath = file.Path;
        _history.Clear();

        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseOpenFile()
    {
        _open?.Dispose();
        _open = null;
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Notify();

    private void Notify()
    {
        ProgressChanged?.Invoke(this, new DocumentProgress(string.Empty, null, IsFinished: true));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

using SaveEditor.Ui.Shell;

namespace SaveEditor.Ui.Gallery;

/// <summary>
/// A document session that pretends, so the gallery can demonstrate shell states.
/// </summary>
/// <remarks>
/// The gallery is a catalogue, not an editor. It performs no file I/O at all —
/// running it must never touch a real save. Toggling the flags here is how the
/// gallery shows the dirty marker, the welcome state, and the discard guard
/// without a codec or a filesystem.
/// </remarks>
internal sealed class GalleryDocumentSession : IDocumentSession
{
    private bool _hasDocument;
    private bool _isDirty;
    private bool _hasPendingEdits;

    public bool HasDocument => _hasDocument;

    public bool IsDirty => _isDirty;

    public bool HasPendingEdits => _hasPendingEdits;

    public string? CurrentPath { get; private set; }

    public bool CanUndo => _isDirty;

    public bool CanRedo => false;

    public event EventHandler? StateChanged;

    public ValueTask OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        _hasDocument = true;
        CurrentPath = path;
        Changed();
        return ValueTask.CompletedTask;
    }

    public ValueTask OpenFolderAsync(string path, CancellationToken cancellationToken = default) =>
        OpenAsync(path, cancellationToken);

    public ValueTask SaveAsAsync(CancellationToken cancellationToken = default)
    {
        _isDirty = false;
        _hasPendingEdits = false;
        Changed();
        return ValueTask.CompletedTask;
    }

    public ValueTask OverwriteWithBackupAsync(CancellationToken cancellationToken = default) =>
        SaveAsAsync(cancellationToken);

    public ValueTask ReloadAsync(CancellationToken cancellationToken = default)
    {
        _isDirty = false;
        _hasPendingEdits = false;
        Changed();
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        _hasDocument = false;
        _isDirty = false;
        _hasPendingEdits = false;
        CurrentPath = null;
        Changed();
        return ValueTask.CompletedTask;
    }

    public void Undo()
    {
        _isDirty = false;
        Changed();
    }

    public void Redo() => Changed();

    public void DiscardPendingEdits()
    {
        _hasPendingEdits = false;
        Changed();
    }

    /// <summary>Simulates the user editing something, for demonstration.</summary>
    public void SimulateEdit()
    {
        _hasDocument = true;
        CurrentPath ??= "demo-save.dat";
        _hasPendingEdits = true;
        _isDirty = true;
        Changed();
    }

    private void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);
}

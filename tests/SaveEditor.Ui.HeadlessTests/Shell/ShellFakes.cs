using Avalonia;
using SaveEditor.Ui.Hosting;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Shell;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>A document session that records what the shell asked it to do.</summary>
internal sealed class FakeDocumentSession : IDocumentSession
{
    public bool HasDocument { get; set; }

    public bool IsDirty { get; set; }

    public bool HasPendingEdits { get; set; }

    public string? CurrentPath { get; set; }

    public string? LastStatusMessage { get; set; }

    public string? LastBackupPath { get; set; }

    public event EventHandler<DocumentProgress>? ProgressChanged;

    /// <summary>Drives the progress path from tests without a real operation.</summary>
    public void RaiseProgress(DocumentProgress progress) => ProgressChanged?.Invoke(this, progress);

    public bool CanUndo { get; set; } = true;

    public bool CanRedo { get; set; } = true;

    public List<string> Calls { get; } = [];

    public string? OpenedPath { get; private set; }

    public event EventHandler? StateChanged;

    public ValueTask OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OpenAsync));
        OpenedPath = path;
        HasDocument = true;
        CurrentPath = path;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    public ValueTask OpenFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OpenFolderAsync));
        OpenedPath = path;
        HasDocument = true;
        CurrentPath = path;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    public ValueTask SaveAsAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(SaveAsAsync));
        return ValueTask.CompletedTask;
    }

    public ValueTask OverwriteWithBackupAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OverwriteWithBackupAsync));
        return ValueTask.CompletedTask;
    }

    public ValueTask ReloadAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(ReloadAsync));
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(CloseAsync));
        HasDocument = false;
        CurrentPath = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    public void Undo() => Calls.Add(nameof(Undo));

    public void Redo() => Calls.Add(nameof(Redo));

    public void DiscardPendingEdits() => Calls.Add(nameof(DiscardPendingEdits));
}

/// <summary>An interaction layer with scripted answers.</summary>
internal sealed class FakeUserInteraction : IUserInteraction
{
    public bool ConfirmResult { get; set; } = true;

    public List<ConfirmationRequest> Confirmations { get; } = [];

    public string? OpenPickerResult { get; set; }

    public string? FolderPickerResult { get; set; }

    public List<MessageRequest> Messages { get; } = [];

    public List<ChoicePrompt> Prompts { get; } = [];

    public List<DocumentRequest> Documents { get; } = [];

    /// <summary>Declines by default: a dismissal must not become a selection.</summary>
    public Func<ChoicePrompt, string?> Choose { get; set; } = _ => null;

    public ValueTask<string?> ChooseAsync(ChoicePrompt prompt, CancellationToken cancellationToken = default)
    {
        Prompts.Add(prompt);
        return ValueTask.FromResult(Choose(prompt));
    }

    public ValueTask ShowDocumentAsync(DocumentRequest request, CancellationToken cancellationToken = default)
    {
        Documents.Add(request);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> PickOpenFileAsync(
        FilePickerRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(OpenPickerResult);

    public ValueTask<SaveFilePickResult?> PickSaveFileAsync(
        FilePickerRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<SaveFilePickResult?>(null);

    public ValueTask<string?> PickFolderAsync(
        string title, string? suggestedDirectory = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(FolderPickerResult);

    public ValueTask<bool> ConfirmAsync(
        ConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        Confirmations.Add(request);
        return ValueTask.FromResult(ConfirmResult);
    }

    public ValueTask ShowMessageAsync(
        MessageRequest request, CancellationToken cancellationToken = default)
    {
        Messages.Add(request);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A host that runs the installed guard, exactly as a real window would.
/// </summary>
/// <remarks>
/// A fake that shut down unconditionally would make the guard test pass while
/// proving nothing, so this one consults the guard and records the outcome.
/// </remarks>
internal sealed class FakeEditorHost : IEditorHost
{
    private Func<CancellationToken, ValueTask<bool>>? _guard;

    public bool DidShutDown { get; private set; }

    public bool GuardWasConsulted { get; private set; }

    public Size? AppliedSize { get; private set; }

    public event EventHandler<HostSizeChangedEventArgs>? SizeChanged;

    public void ApplySize(Size size) => AppliedSize = size;

    public void SetShutdownGuard(Func<CancellationToken, ValueTask<bool>> guard) => _guard = guard;

    public async ValueTask RequestShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_guard is not null)
        {
            GuardWasConsulted = true;
            if (!await _guard(cancellationToken).ConfigureAwait(true))
            {
                return;
            }
        }

        DidShutDown = true;
    }

    public void RaiseSizeChanged(Size size) =>
        SizeChanged?.Invoke(this, new HostSizeChangedEventArgs(size));
}

/// <summary>An in-memory settings store for shell tests.</summary>
internal sealed class FakeSettingsStore : IEditorSettingsStore
{
    public EditorSettings Current { get; set; } = new();

    public bool IsPersistent => true;

    public ValueTask<EditorSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Current);

    public ValueTask SaveAsync(EditorSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        return ValueTask.CompletedTask;
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveEditor.Ui.Display;
using SaveEditor.Ui.Hosting;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Theming;

namespace SaveEditor.Ui.Shell;

/// <summary>
/// Drives the editor shell: sections, menu commands, dirty state, and the guard
/// that stands between unsaved work and losing it.
/// </summary>
/// <remarks>
/// <para>
/// Every destructive navigation — open, reload, close, exit — funnels through
/// <see cref="ConfirmDiscardAsync"/>. One guard rather than four means a new entry
/// point cannot quietly bypass it, which is the usual way this class of bug ships.
/// </para>
/// <para>
/// The view-model holds no Avalonia controls and can be driven entirely from tests.
/// </para>
/// </remarks>
public sealed partial class EditorShellViewModel : ObservableObject, IDisposable
{
    private readonly IDocumentSession _session;
    private readonly IUserInteraction _interaction;
    private readonly IEditorSettingsStore _settings;
    private readonly IEditorHost? _host;
    private readonly ThemeController? _theme;
    private readonly PathDisplayFormatter _formatter = PathDisplayFormatter.Default;
    private readonly List<SectionDescriptor> _allSections = [];
    private bool _disposed;

    /// <summary>Creates a shell view-model.</summary>
    /// <param name="session">The document seam.</param>
    /// <param name="interaction">Dialogs and pickers.</param>
    /// <param name="settings">Where recents and the last section live.</param>
    /// <param name="host">
    /// Window and application authority, or <see langword="null"/> when the shell is
    /// embedded somewhere that owns neither. With no host, Exit is unavailable rather
    /// than present and inert.
    /// </param>
    /// <param name="theme">
    /// Appearance control, or <see langword="null"/> to omit the appearance menus.
    /// </param>
    public EditorShellViewModel(
        IDocumentSession session,
        IUserInteraction interaction,
        IEditorSettingsStore settings,
        IEditorHost? host = null,
        ThemeController? theme = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(settings);

        _session = session;
        _interaction = interaction;
        _settings = settings;
        _host = host;
        _theme = theme;

        _session.StateChanged += OnSessionStateChanged;
        _session.ProgressChanged += OnSessionProgress;
        _host?.SetShutdownGuard(ct => ConfirmDiscardAsync(DiscardReason.Exit, ct));
    }

    /// <summary>What the user is about to do that could lose uncommitted work.</summary>
    public enum DiscardReason
    {
        /// <summary>Opening a different document.</summary>
        Open,

        /// <summary>Re-reading the current document from disk.</summary>
        Reload,

        /// <summary>Closing the current document.</summary>
        Close,

        /// <summary>Leaving the application.</summary>
        Exit,
    }

    /// <summary>Sections currently visible, in registration order.</summary>
    public ObservableCollection<SectionDescriptor> Sections { get; } = [];

    /// <summary>Recent entries, most recent first.</summary>
    /// <remarks>
    /// Each entry carries the raw path used to open it and the neutralised label
    /// used to show it. Pairing them means the string a user reads and the string
    /// the framework opens can never drift apart, which is the whole point of
    /// formatting paths for display in the first place.
    /// </remarks>
    public ObservableCollection<RecentEntry> Recents { get; } = [];

    /// <summary>The open document's path, neutralised for display.</summary>
    public PathLabel CurrentPathLabel => _formatter.Format(_session.CurrentPath);

    /// <summary>
    /// Where the last backup was written, neutralised for display, or
    /// <see langword="null"/> if there is none.
    /// </summary>
    /// <remarks>
    /// Shown because "a backup was written" is only useful alongside where to find
    /// it. Somebody reaching for a backup is usually already having a bad day.
    /// </remarks>
    public PathLabel? LastBackupLabel =>
        _session.LastBackupPath is { Length: > 0 } backup ? _formatter.Format(backup) : null;

    /// <summary>What the workflow is currently doing, or empty when idle.</summary>
    [ObservableProperty]
    public partial string ProgressDescription { get; private set; } = string.Empty;

    /// <summary>Completion in [0, 1], or <see langword="null"/> when indeterminate.</summary>
    [ObservableProperty]
    public partial double? ProgressFraction { get; private set; }

    /// <summary>Whether a long operation is running.</summary>
    public bool IsBusy => ProgressDescription.Length > 0;

    /// <summary>Whether Exit is offered at all.</summary>
    public bool CanExit => _host is not null;

    /// <summary>Whether the appearance menus are offered.</summary>
    public bool CanChangeAppearance => _theme is not null;

    /// <summary>The two theme modes, for the Appearance menu.</summary>
    public IReadOnlyList<ThemeMode> ThemeModes { get; } = Enum.GetValues<ThemeMode>();

    /// <summary>All fourteen accents, for the Appearance menu.</summary>
    public IReadOnlyList<CatppuccinAccent> Accents { get; } = Enum.GetValues<CatppuccinAccent>();

    /// <summary>Whether the welcome state is showing instead of a document.</summary>
    public bool IsWelcomeVisible => !_session.HasDocument;

    /// <summary>Whether there is uncommitted or unsaved work.</summary>
    public bool HasUnsavedWork => _session.IsDirty || _session.HasPendingEdits;

    /// <summary>Window title, carrying a dirty marker.</summary>
    public string Title
    {
        get
        {
            var name = _session.CurrentPath is { } path ? Path.GetFileName(path) : "No save open";
            return HasUnsavedWork ? $"{name} *" : name;
        }
    }

    /// <summary>The currently selected section.</summary>
    [ObservableProperty]
    public partial SectionDescriptor? SelectedSection { get; set; }

    /// <summary>Full-sentence outcome of the last operation.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready.";

    /// <summary>Body of the Help &gt; About dialog.</summary>
    /// <remarks>
    /// Framework text by default, so the menu item does something from the first
    /// run. Editors replace it with their own identity and credits. The themed
    /// About dialog with consumer slots arrives in P4; a plain message until then
    /// is still better than a menu item that silently does nothing.
    /// </remarks>
    public string AboutMessage { get; set; } =
        "Built on the Save Editor GUI Framework.\n\n"
        + "Framework source is 0BSD. Bundled components and their licences are listed "
        + "in THIRD-PARTY-NOTICES.";

    /// <summary>Body of the Help &gt; Safety dialog.</summary>
    public string SafetyMessage { get; set; } =
        "Save As is the default write path, and Ctrl+S always means Save As.\n\n"
        + "Overwrite + Backup writes a verified backup first and abandons the overwrite "
        + "if that backup cannot be verified. On failure the original file is left byte "
        + "for byte as it was.\n\n"
        + "Codecs run in-process at full privilege and are not sandboxed. Only install "
        + "codecs you trust.";

    /// <summary>Registers the editor's sections and evaluates their visibility.</summary>
    /// <param name="sections">Descriptors, in the order they should appear.</param>
    public void RegisterSections(IEnumerable<SectionDescriptor> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        _allSections.Clear();
        _allSections.AddRange(sections);
        RefreshSections();
    }

    /// <summary>Re-evaluates section visibility predicates and the selection.</summary>
    public void RefreshSections()
    {
        var visible = _allSections.Where(s => s.EvaluateVisibility()).ToList();

        Sections.Clear();
        foreach (var section in visible)
        {
            Sections.Add(section);
        }

        // A selection that just became invisible would otherwise leave the content
        // pane showing a section the sidebar no longer offers.
        if (SelectedSection is null || !visible.Contains(SelectedSection))
        {
            SelectedSection = visible.FirstOrDefault();
        }
    }

    /// <summary>Loads persisted recents and the last selected section.</summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(true);

        Recents.Clear();
        foreach (var path in settings.RecentFiles)
        {
            Recents.Add(new RecentEntry(path, _formatter.Format(path)));
        }

        if (settings.LastSectionKey is { } key)
        {
            var match = Sections.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
            if (match is not null)
            {
                SelectedSection = match;
            }
        }

        NotifyDocumentState();
    }

    /// <summary>
    /// Asks the user to accept losing uncommitted work, if there is any.
    /// </summary>
    /// <param name="reason">What the user is about to do.</param>
    /// <param name="cancellationToken">Cancels the prompt.</param>
    /// <returns><see langword="true"/> when it is safe to proceed.</returns>
    /// <remarks>
    /// Returns <see langword="true"/> immediately when nothing would be lost, so the
    /// common path shows no dialog. The accept label names the outcome rather than
    /// saying "OK", because this is the last thing standing between a user and their
    /// unsaved edits.
    /// </remarks>
    public async ValueTask<bool> ConfirmDiscardAsync(
        DiscardReason reason,
        CancellationToken cancellationToken = default)
    {
        if (!HasUnsavedWork)
        {
            return true;
        }

        var what = _session.HasPendingEdits && _session.IsDirty
            ? "unapplied edits and unsaved changes"
            : _session.HasPendingEdits ? "unapplied edits" : "unsaved changes";

        var request = new ConfirmationRequest
        {
            Title = "Discard changes?",
            Message = $"This save has {what}. They will be lost if you continue.",
            AcceptLabel = reason switch
            {
                DiscardReason.Open => "Discard and open",
                DiscardReason.Reload => "Discard and reload",
                DiscardReason.Close => "Discard and close",
                _ => "Discard and exit",
            },
            IsDestructive = true,
        };

        return await _interaction.ConfirmAsync(request, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Opens a path that did not come from a picker — a recent entry or a drop.</summary>
    /// <param name="path">The path to open.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <remarks>
    /// Drag-and-drop, the recents menu, and the File menu all land here, so a dropped
    /// file cannot skip the discard guard that a menu open would have raised.
    /// </remarks>
    public async ValueTask OpenPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!await ConfirmDiscardAsync(DiscardReason.Open, cancellationToken).ConfigureAwait(true))
        {
            StatusMessage = "Open cancelled. The current save is unchanged.";
            return;
        }

        await _session.OpenAsync(path, cancellationToken).ConfigureAwait(true);
        NotifyDocumentState();
    }

    [RelayCommand]
    private async Task OpenSaveAsync(CancellationToken cancellationToken)
    {
        var request = new FilePickerRequest("Open save", []);
        var chosen = await _interaction.PickOpenFileAsync(request, cancellationToken).ConfigureAwait(true);

        if (chosen is not null)
        {
            await OpenPathAsync(chosen, cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task ShowAboutAsync(CancellationToken cancellationToken) =>
        _interaction.ShowMessageAsync(
            new MessageRequest("About", AboutMessage), cancellationToken).AsTask();

    [RelayCommand]
    private Task ShowSafetyAsync(CancellationToken cancellationToken) =>
        _interaction.ShowMessageAsync(
            new MessageRequest("Safety and manual testing", SafetyMessage), cancellationToken).AsTask();

    /// <summary>
    /// Decides whether a recent entry's file is confirmed missing.
    /// </summary>
    /// <remarks>
    /// Probed only when an entry is activated, never at startup — an eager scan is
    /// what makes a planted network path dangerous. Injectable so the pruning rule can
    /// be tested without a filesystem.
    /// </remarks>
    public Func<string, bool> PathExists { get; set; } = File.Exists;

    [RelayCommand]
    private async Task OpenRecentAsync(RecentEntry? entry, CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            return;
        }

        await OpenPathAsync(entry.Path, cancellationToken).ConfigureAwait(true);

        if (_session.HasDocument || PathExists(entry.Path))
        {
            return;
        }

        // Confirmed missing at the moment the user reached for it. Temporarily
        // unreachable paths are kept: an unplugged drive is not a deleted save, and
        // silently dropping the entry would lose the only record of where it lived.
        Recents.Remove(entry);

        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(true);
        await _settings
            .SaveAsync(
                settings with { RecentFiles = [.. Recents.Select(r => r.Path)] },
                cancellationToken)
            .ConfigureAwait(true);

        StatusMessage = "That save is no longer at the recorded location. It has been removed from Recent.";
    }

    [RelayCommand]
    private async Task SaveAsAsync(CancellationToken cancellationToken)
    {
        await _session.SaveAsAsync(cancellationToken).ConfigureAwait(true);
        NotifyDocumentState();
    }

    [RelayCommand]
    private async Task OverwriteWithBackupAsync(CancellationToken cancellationToken)
    {
        await _session.OverwriteWithBackupAsync(cancellationToken).ConfigureAwait(true);
        NotifyDocumentState();
    }

    [RelayCommand]
    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (!await ConfirmDiscardAsync(DiscardReason.Reload, cancellationToken).ConfigureAwait(true))
        {
            StatusMessage = "Reload cancelled. The current save is unchanged.";
            return;
        }

        await _session.ReloadAsync(cancellationToken).ConfigureAwait(true);
        NotifyDocumentState();
    }

    [RelayCommand]
    private async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (!await ConfirmDiscardAsync(DiscardReason.Close, cancellationToken).ConfigureAwait(true))
        {
            StatusMessage = "Close cancelled. The current save is unchanged.";
            return;
        }

        await _session.CloseAsync(cancellationToken).ConfigureAwait(true);
        NotifyDocumentState();
    }

    [RelayCommand]
    private async Task ExitAsync(CancellationToken cancellationToken)
    {
        // The host consults the guard installed in the constructor, so Exit and the
        // window close button take the same path and neither can skip it.
        if (_host is not null)
        {
            await _host.RequestShutdownAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task SetThemeAsync(ThemeMode mode, CancellationToken cancellationToken)
    {
        if (_theme is not null)
        {
            await _theme.SetModeAsync(mode, cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task SetAccentAsync(CatppuccinAccent accent, CancellationToken cancellationToken)
    {
        if (_theme is not null)
        {
            await _theme.SetAccentAsync(accent, cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ResetAccentAsync(CancellationToken cancellationToken)
    {
        if (_theme is not null)
        {
            await _theme.ResetAccentAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Undo()
    {
        _session.Undo();
        NotifyDocumentState();
    }

    [RelayCommand]
    private void Redo()
    {
        _session.Redo();
        NotifyDocumentState();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.StateChanged -= OnSessionStateChanged;
        _session.ProgressChanged -= OnSessionProgress;
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) => NotifyDocumentState();

    private void OnSessionProgress(object? sender, DocumentProgress progress)
    {
        // A finished report clears the phase rather than leaving the last one on
        // screen, where it would read as work still in flight.
        ProgressDescription = progress.IsFinished ? string.Empty : progress.Description;
        ProgressFraction = progress.IsFinished ? null : progress.Fraction;
        OnPropertyChanged(nameof(IsBusy));
    }

    private void NotifyDocumentState()
    {
        // The session's sentence wins when it has one. It describes what the workflow
        // actually did; anything the shell composed would only describe what it asked
        // for. A cancelled-by-guard message is the shell's own and is set at the call
        // site, so it is not overwritten here.
        if (_session.LastStatusMessage is { Length: > 0 } reported)
        {
            StatusMessage = reported;
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CurrentPathLabel));
        OnPropertyChanged(nameof(LastBackupLabel));
        OnPropertyChanged(nameof(HasUnsavedWork));
        OnPropertyChanged(nameof(IsWelcomeVisible));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }
}

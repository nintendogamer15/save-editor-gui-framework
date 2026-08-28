using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Display;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.Dialogs;

/// <summary>
/// The platform storage calls <see cref="ThemedUserInteraction"/> makes.
/// </summary>
/// <remarks>
/// Avalonia's <see cref="IStorageProvider"/> is deliberately not implementable outside
/// Avalonia, <see cref="TopLevel.StorageProvider"/> is read-only, and the headless
/// platform supplies one that answers nothing. Without a seam of the framework's own
/// there is no way to assert that a picker is reached at all, or with what — which is
/// exactly the defect this exists for. The shipped implementation forwards to
/// <see cref="TopLevel.StorageProvider"/> and decides nothing; every option is built by
/// <see cref="ThemedUserInteraction"/>, so a test that substitutes this still sees the
/// real request.
/// </remarks>
internal interface IStoragePickers
{
    /// <summary>Runs the open picker and returns the chosen local path.</summary>
    Task<string?> PickOpenFileAsync(FilePickerOpenOptions options);

    /// <summary>Runs the save picker and returns the chosen local path.</summary>
    Task<string?> PickSaveFileAsync(FilePickerSaveOptions options);

    /// <summary>Runs the folder picker and returns the chosen local path.</summary>
    Task<string?> PickFolderAsync(FolderPickerOpenOptions options);

    /// <summary>Resolves a local directory into a folder a picker can start in.</summary>
    Task<IStorageFolder?> ResolveFolderAsync(Uri path);
}

/// <summary>The shipped <see cref="IStoragePickers"/>: the host window's own provider.</summary>
internal sealed class TopLevelStoragePickers : IStoragePickers
{
    private readonly TopLevel _owner;

    public TopLevelStoragePickers(TopLevel owner) => _owner = owner;

    public async Task<string?> PickOpenFileAsync(FilePickerOpenOptions options)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(options).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(FilePickerSaveOptions options)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(options).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(FolderPickerOpenOptions options)
    {
        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(options).ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task<IStorageFolder?> ResolveFolderAsync(Uri path) =>
        _owner.StorageProvider.TryGetFolderFromPathAsync(path);
}

/// <summary>
/// The framework's themed default <see cref="IUserInteraction"/>, hosted on a
/// <see cref="Window"/>.
/// </summary>
/// <remarks>
/// <para>
/// Storage pickers delegate to <see cref="TopLevel.StorageProvider"/> on the host
/// window, which already integrates the running platform's native picker.
/// Confirmations, messages, and the extra document and About dialogs this type
/// exposes beyond the interface are all framework-themed content hosted in a plain
/// <see cref="Window"/>. Hosts size to their content up to 90% of the owner's
/// working area (with a fallback cap when screens cannot be enumerated) so a long
/// About or document body cannot grow past the display.
/// </para>
/// <para>
/// <strong>Every entry point marshals itself onto the UI thread.</strong> The safe
/// file workflow runs codecs on the thread pool, and resuming from one leaves the
/// rest of a save running there, so a picker or dialog opened from the middle of a
/// Save As is reached from a pool thread rather than from the thread that owns the
/// window. Constructing a <see cref="Window"/> or reaching
/// <see cref="TopLevel.StorageProvider"/> from there does not fail somewhere visible:
/// the workflow catches everything and reports a failed save, so the user sees no
/// chooser at all and no explanation of why. Marshalling here rather than in the
/// workflow keeps the workflow free of a UI thread it should not know about, and puts
/// the affinity in the type that actually owns the UI objects.
/// </para>
/// <para>
/// <see cref="PickSaveFileAsync"/> always reports
/// <see cref="SaveFilePickResult.PickerConfirmedOverwrite"/> as <see langword="false"/>.
/// <see cref="SaveFilePickResult"/>'s own remarks make this the required, fail-closed
/// answer: whether a given platform's native save picker already asked about
/// overwrite is not something this type can verify uniformly across Windows,
/// GTK/portal, and macOS pickers, and a wrong <see langword="true"/> here would
/// suppress a safety prompt the workflow relies on. One redundant confirmation costs
/// far less than a silent overwrite.
/// </para>
/// </remarks>
public sealed class ThemedUserInteraction : IUserInteraction
{
    private readonly Window _owner;
    private readonly PathDisplayFormatter _pathFormatter;
    private readonly IStoragePickers _pickers;

    /// <summary>Creates the interaction over a host window.</summary>
    /// <param name="owner">
    /// The window dialogs are centered on and that supplies
    /// <see cref="TopLevel.StorageProvider"/> for the file and folder pickers.
    /// </param>
    /// <param name="pathFormatter">
    /// The formatter used to render paths named in destructive confirmations.
    /// Defaults to <see cref="PathDisplayFormatter.Default"/>.
    /// </param>
    public ThemedUserInteraction(Window owner, PathDisplayFormatter? pathFormatter = null)
        : this(owner, pickers: null, pathFormatter)
    {
    }

    /// <summary>Creates the interaction over substituted pickers.</summary>
    /// <remarks>
    /// Internal because an editor that wants a different picker replaces
    /// <see cref="IUserInteraction"/>, which is the seam that exists for that. See
    /// <see cref="IStoragePickers"/> for why one is needed here at all.
    /// </remarks>
    internal ThemedUserInteraction(
        Window owner, IStoragePickers? pickers, PathDisplayFormatter? pathFormatter = null)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _owner = owner;
        _pathFormatter = pathFormatter ?? PathDisplayFormatter.Default;
        _pickers = pickers ?? new TopLevelStoragePickers(owner);
    }

    /// <inheritdoc />
    public ValueTask<string?> PickOpenFileAsync(
        FilePickerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask<string?>(OnUiThreadAsync(() => PickOpenFileCoreAsync(request)));
    }

    /// <inheritdoc />
    public ValueTask<SaveFilePickResult?> PickSaveFileAsync(
        FilePickerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask<SaveFilePickResult?>(OnUiThreadAsync(() => PickSaveFileCoreAsync(request)));
    }

    /// <inheritdoc />
    public ValueTask<string?> PickFolderAsync(
        string title, string? suggestedDirectory = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        return new ValueTask<string?>(OnUiThreadAsync(() => PickFolderCoreAsync(title, suggestedDirectory)));
    }

    /// <inheritdoc />
    public ValueTask<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask<bool>(
            OnUiThreadAsync(() => ShowConfirmationAsync(request, request.Target, cancellationToken)));
    }

    /// <summary>
    /// Asks the user to confirm overwriting a specific file, rendering the path
    /// through <see cref="PathDisplayFormatter"/> with a full value on the tooltip
    /// and accessible description.
    /// </summary>
    /// <param name="path">The file that would be overwritten.</param>
    /// <param name="details">Optional codec-supplied warnings to show alongside the confirmation.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    /// <returns><see langword="true"/> when the user accepts the overwrite.</returns>
    /// <remarks>
    /// A convenience over <see cref="ConfirmAsync"/> that formats the path and fills
    /// <see cref="ConfirmationRequest.Target"/>. The path itself is part of the
    /// interface contract, so an editor supplying its own
    /// <see cref="IUserInteraction"/> still receives it.
    /// </remarks>
    public ValueTask<bool> ConfirmOverwriteAsync(
        string path,
        IReadOnlyList<UntrustedText>? details = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var label = _pathFormatter.Format(path);
        var request = new ConfirmationRequest
        {
            Title = "Overwrite save file",
            Message = "This replaces the file's current contents. This cannot be undone.",
            AcceptLabel = "Overwrite save file",
            IsDestructive = true,
            Details = details ?? [],
            Target = label,
        };

        return new ValueTask<bool>(
            OnUiThreadAsync(() => ShowConfirmationAsync(request, request.Target, cancellationToken)));
    }

    /// <inheritdoc />
    public ValueTask ShowMessageAsync(MessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask(OnUiThreadAsync(() => ShowMessageCoreAsync(request, cancellationToken)));
    }

    /// <summary>Shows a block of read-only text in a themed, scrollable viewer.</summary>
    /// <param name="request">Title and body. The body is neutralized before display.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    public ValueTask ShowDocumentAsync(
        DocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask(OnUiThreadAsync(() => ShowDocumentCoreAsync(request, cancellationToken)));
    }

    private async Task ShowDocumentCoreAsync(DocumentRequest request, CancellationToken cancellationToken)
    {
        var view = new DocumentViewerContent
        {
            Title = request.Title,
            DocumentText = request.Content.Value,
        };

        var window = CreateHostWindow(request.Title, view, width: 560);
        view.CloseRequested += (_, _) => window.Close();

        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(window.Close));
        await window.ShowDialog(_owner).ConfigureAwait(true);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Renders every option at once rather than as a series of yes/no questions.
    /// Asking sequentially makes the order look like a recommendation, and the whole
    /// point of resolving ambiguous detection by asking is that the framework has no
    /// basis for recommending one.
    /// </remarks>
    public ValueTask<string?> ChooseAsync(
        ChoicePrompt prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return new ValueTask<string?>(OnUiThreadAsync(() => ChooseCoreAsync(prompt, cancellationToken)));
    }

    private async Task<string?> ChooseCoreAsync(ChoicePrompt prompt, CancellationToken cancellationToken)
    {
        string? chosen = null;

        var options = new StackPanel { Spacing = 8 };
        var scroller = new ScrollViewer
        {
            Content = options,
            MaxHeight = DialogHostBounds.DefaultBodyMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var window = CreateHostWindow(prompt.Title, scroller, width: 460);

        foreach (var option in prompt.Options)
        {
            var caption = new StackPanel();
            caption.Children.Add(new TextBlock { Text = option.Label });

            if (option.Description is { } description)
            {
                caption.Children.Add(new TextBlock
                {
                    Text = description,
                    FontSize = 11,
                    [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SubtleForeground"),
                });
            }

            var button = new Button
            {
                Content = caption,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };

            AutomationProperties.SetName(button, option.Label);

            var key = option.Key;
            button.Click += (_, _) =>
            {
                chosen = key;
                window.Close();
            };

            options.Children.Add(button);
        }

        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(window.Close));
        await window.ShowDialog(_owner).ConfigureAwait(true);

        // Dismissal is not a selection. Returning the first option here would make
        // registration order decide the format after all.
        return chosen;
    }

    /// <summary>Shows the framework's themed About/Credits dialog.</summary>
    /// <param name="appIdentity">App name, version, and other identity content.</param>
    /// <param name="credits">Contributor and acknowledgement content.</param>
    /// <param name="licenses">Third-party license content.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    public ValueTask ShowAboutAsync(
        object? appIdentity,
        object? credits,
        object? licenses,
        CancellationToken cancellationToken = default) =>
        new(OnUiThreadAsync(() => ShowAboutCoreAsync(appIdentity, credits, licenses, cancellationToken)));

    private async Task ShowAboutCoreAsync(
        object? appIdentity, object? credits, object? licenses, CancellationToken cancellationToken)
    {
        var view = new AboutDialogContent { AppIdentity = appIdentity, Credits = credits, Licenses = licenses };
        var window = CreateHostWindow("About", view, width: 480);
        view.CloseRequested += (_, _) => window.Close();

        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(window.Close));
        await window.ShowDialog(_owner).ConfigureAwait(true);
    }

    private async Task<string?> PickOpenFileCoreAsync(FilePickerRequest request) =>
        await _pickers.PickOpenFileAsync(new FilePickerOpenOptions
        {
            Title = request.Title,
            AllowMultiple = false,
            SuggestedFileName = request.SuggestedFileName,
            SuggestedStartLocation = await StartLocationAsync(request.SuggestedDirectory).ConfigureAwait(true),
            FileTypeFilter = BuildFileTypes(request.Formats),
        }).ConfigureAwait(true);

    private async Task<SaveFilePickResult?> PickSaveFileCoreAsync(FilePickerRequest request)
    {
        var path = await _pickers.PickSaveFileAsync(new FilePickerSaveOptions
        {
            Title = request.Title,
            SuggestedFileName = request.SuggestedFileName,
            SuggestedStartLocation = await StartLocationAsync(request.SuggestedDirectory).ConfigureAwait(true),
            FileTypeChoices = BuildFileTypes(request.Formats),
        }).ConfigureAwait(true);

        return path is null ? null : new SaveFilePickResult(path, PickerConfirmedOverwrite: false);
    }

    private async Task<string?> PickFolderCoreAsync(string title, string? suggestedDirectory) =>
        await _pickers.PickFolderAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartLocationAsync(suggestedDirectory).ConfigureAwait(true),
        }).ConfigureAwait(true);

    /// <summary>Turns a suggested directory into the folder a picker should open in.</summary>
    /// <remarks>
    /// <see cref="FilePickerRequest.SuggestedDirectory"/> was previously accepted and
    /// dropped, which left Save As opening wherever the platform last happened to be
    /// rather than beside the save being copied. A directory that cannot be resolved
    /// yields <see langword="null"/> rather than throwing: a picker that opens in the
    /// wrong place is a nuisance, and one that does not open at all is the defect this
    /// method is part of fixing.
    /// </remarks>
    private async Task<IStorageFolder?> StartLocationAsync(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            return await _pickers.ResolveFolderAsync(new Uri(Path.GetFullPath(directory))).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Runs UI work on the thread that owns the window, from wherever it is called.</summary>
    /// <remarks>
    /// Already on the UI thread, the work runs inline — posting it would defer every
    /// dialog behind whatever else is queued, and a nested dialog opened from a click
    /// handler would then race the handler that opened it.
    /// </remarks>
    private static Task<T> OnUiThreadAsync<T>(Func<Task<T>> work) =>
        Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work);

    private static Task OnUiThreadAsync(Func<Task> work) =>
        Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work);

    private async Task<bool> ShowConfirmationAsync(
        ConfirmationRequest request, PathLabel? targetPath, CancellationToken cancellationToken)
    {
        var view = new ConfirmationDialogView(request, targetPath);
        var window = CreateHostWindow(request.Title, view, width: 480);
        var accepted = false;

        view.AcceptButton.Click += (_, _) =>
        {
            accepted = true;
            window.Close();
        };
        view.CancelButton.Click += (_, _) => window.Close();

        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(window.Close));
        await window.ShowDialog(_owner).ConfigureAwait(true);
        return accepted;
    }

    private async Task ShowMessageCoreAsync(MessageRequest request, CancellationToken cancellationToken)
    {
        var view = new MessageDialogView(request);
        var window = CreateHostWindow(request.Title, view, width: 480);
        view.CloseButton.Click += (_, _) => window.Close();

        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(window.Close));
        await window.ShowDialog(_owner).ConfigureAwait(true);
    }

    private Window CreateHostWindow(string title, Control content, double width)
    {
        var area = TryGetWorkingArea(_owner);
        var limits = DialogHostBounds.Resolve(width, area?.Width, area?.Height);

        return new Window
        {
            Title = title,
            Width = limits.Width,
            MaxWidth = limits.MaxWidth,
            MaxHeight = limits.MaxHeight,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content,
        };
    }

    private static Size? TryGetWorkingArea(Window owner)
    {
        try
        {
            var screens = owner.Screens;
            var screen = screens?.ScreenFromWindow(owner) ?? screens?.Primary;
            if (screen is null)
            {
                return null;
            }

            var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var area = screen.WorkingArea;
            var width = area.Width / scaling;
            var height = area.Height / scaling;
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            {
                return null;
            }

            return new Size(width, height);
        }
        catch (Exception)
        {
            // A missing or throwing screen source must not prevent the dialog
            // from opening; Resolve falls back to its absolute plausibility cap.
            return null;
        }
    }

    private static List<FilePickerFileType> BuildFileTypes(IReadOnlyList<SaveFormatDescriptor> formats)
    {
        var types = new List<FilePickerFileType>(formats.Count);

        foreach (var format in formats)
        {
            var patterns = format.Extensions.Select(static ext => $"*.{ext}").ToArray();
            types.Add(new FilePickerFileType(format.DisplayName) { Patterns = patterns });
        }

        return types;
    }
}

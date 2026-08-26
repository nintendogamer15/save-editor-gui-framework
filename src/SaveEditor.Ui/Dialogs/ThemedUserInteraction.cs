using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Display;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.Dialogs;

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
/// <see cref="Window"/>.
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
    {
        ArgumentNullException.ThrowIfNull(owner);

        _owner = owner;
        _pathFormatter = pathFormatter ?? PathDisplayFormatter.Default;
    }

    /// <inheritdoc />
    public async ValueTask<string?> PickOpenFileAsync(
        FilePickerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = request.Title,
            AllowMultiple = false,
            SuggestedFileName = request.SuggestedFileName,
            FileTypeFilter = BuildFileTypes(request.Formats),
        }).ConfigureAwait(true);

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public async ValueTask<SaveFilePickResult?> PickSaveFileAsync(
        FilePickerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = request.Title,
            SuggestedFileName = request.SuggestedFileName,
            FileTypeChoices = BuildFileTypes(request.Formats),
        }).ConfigureAwait(true);

        var path = file?.TryGetLocalPath();
        return path is null ? null : new SaveFilePickResult(path, PickerConfirmedOverwrite: false);
    }

    /// <inheritdoc />
    public async ValueTask<string?> PickFolderAsync(
        string title, string? suggestedDirectory = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);

        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        }).ConfigureAwait(true);

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public ValueTask<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask<bool>(ShowConfirmationAsync(request, targetPath: null, cancellationToken));
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
    /// A convenience beyond <see cref="IUserInteraction"/>: <see cref="ConfirmationRequest"/>
    /// carries only a plain <see cref="ConfirmationRequest.Message"/> string, with no
    /// dedicated path field a renderer could attach a tooltip or accessible
    /// description to. This method exists so a caller with an actual file path gets
    /// that treatment without hand-formatting the sentence itself.
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
        };

        return new ValueTask<bool>(ShowConfirmationAsync(request, label, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask ShowMessageAsync(MessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask(ShowMessageCoreAsync(request, cancellationToken));
    }

    /// <summary>Shows a block of read-only text in a themed, scrollable viewer.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="documentText">
    /// The text to show. Neutralized before display regardless of origin; see
    /// <see cref="DocumentViewerContent"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    public async ValueTask ShowDocumentAsync(
        string title, string documentText, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);

        var view = new DocumentViewerContent { Title = title, DocumentText = documentText ?? string.Empty };
        var window = CreateHostWindow(title, view, width: 560);
        view.CloseRequested += (_, _) => window.Close();

        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(window.Close));
        await window.ShowDialog(_owner).ConfigureAwait(true);
    }

    /// <summary>Shows the framework's themed About/Credits dialog.</summary>
    /// <param name="appIdentity">App name, version, and other identity content.</param>
    /// <param name="credits">Contributor and acknowledgement content.</param>
    /// <param name="licenses">Third-party license content.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    public async ValueTask ShowAboutAsync(
        object? appIdentity,
        object? credits,
        object? licenses,
        CancellationToken cancellationToken = default)
    {
        var view = new AboutDialogContent { AppIdentity = appIdentity, Credits = credits, Licenses = licenses };
        var window = CreateHostWindow("About", view, width: 480);
        view.CloseRequested += (_, _) => window.Close();

        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(window.Close));
        await window.ShowDialog(_owner).ConfigureAwait(true);
    }

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

    private static Window CreateHostWindow(string title, Control content, double width) =>
        new()
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content,
        };

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

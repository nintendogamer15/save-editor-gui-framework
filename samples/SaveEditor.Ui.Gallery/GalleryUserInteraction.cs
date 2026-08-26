using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.Gallery;

/// <summary>
/// A minimal themed <see cref="IUserInteraction"/> so the gallery can demonstrate
/// the discard guard.
/// </summary>
/// <remarks>
/// A stand-in, not the shipping implementation — the framework's own themed dialogs
/// are <see cref="SaveEditor.Ui.Dialogs.ThemedUserInteraction"/>, which is what a
/// real editor composes and which carries warning sanitisation and the shared path
/// formatter. This exists so the gallery can demonstrate the discard guard on its
/// own. What
/// this does honour already is the rule that matters most: the accept label names
/// the outcome, and a destructive accept is styled as destructive.
/// </remarks>
internal sealed class GalleryUserInteraction(Window owner) : IUserInteraction
{
    public async ValueTask<string?> PickOpenFileAsync(
        FilePickerRequest request, CancellationToken cancellationToken = default)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = request.Title, AllowMultiple = false })
            .ConfigureAwait(true);

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async ValueTask<SaveFilePickResult?> PickSaveFileAsync(
        FilePickerRequest request, CancellationToken cancellationToken = default)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions { Title = request.Title })
            .ConfigureAwait(true);

        // Reports false deliberately. The OS picker does confirm overwrite, but
        // claiming so suppresses only a duplicate prompt, and a stand-in has no
        // business asserting a safety property on the real picker's behalf.
        return file?.TryGetLocalPath() is { } path ? new SaveFilePickResult(path, false) : null;
    }

    public async ValueTask<string?> PickFolderAsync(
        string title, string? suggestedDirectory = null, CancellationToken cancellationToken = default)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false })
            .ConfigureAwait(true);

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async ValueTask<bool> ConfirmAsync(
        ConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        var result = false;

        var accept = new Button { Content = request.AcceptLabel };
        if (request.IsDestructive)
        {
            accept.Classes.Add("danger");
        }

        var cancel = new Button { Content = request.CancelLabel };

        var dialog = new Window
        {
            Title = request.Title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        accept.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = request.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, accept },
                },
            },
        };

        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }

    public async ValueTask<string?> ChooseAsync(
        ChoicePrompt prompt, CancellationToken cancellationToken = default)
    {
        // The gallery demonstrates the flow rather than shipping it; the framework's
        // ThemedUserInteraction is the real one.
        foreach (var option in prompt.Options)
        {
            var accepted = await ConfirmAsync(
                new ConfirmationRequest
                {
                    Title = prompt.Title,
                    Message = string.Join(
                        Environment.NewLine + Environment.NewLine,
                        prompt.Message,
                        option.Label),
                    AcceptLabel = $"Use {option.Label}",
                },
                cancellationToken).ConfigureAwait(true);

            if (accepted)
            {
                return option.Key;
            }
        }

        return null;
    }

    public ValueTask ShowDocumentAsync(
        DocumentRequest request, CancellationToken cancellationToken = default) =>
        ShowMessageAsync(new MessageRequest(request.Title, request.Content.Value), cancellationToken);

    public async ValueTask ShowMessageAsync(
        MessageRequest request, CancellationToken cancellationToken = default) =>
        await ConfirmAsync(
            new ConfirmationRequest
            {
                Title = request.Title,
                Message = request.Message,
                AcceptLabel = "Close",
            },
            cancellationToken).ConfigureAwait(true);
}

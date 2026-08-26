using SaveEditor.Ui.Codecs;

namespace SaveEditor.Ui.Interaction;

/// <summary>A file-picker invocation.</summary>
/// <param name="Title">Dialog title.</param>
/// <param name="Formats">Formats offered as filters.</param>
/// <param name="SuggestedFileName">Optional pre-filled name.</param>
/// <param name="SuggestedDirectory">Optional starting directory.</param>
public sealed record FilePickerRequest(
    string Title,
    IReadOnlyList<SaveFormatDescriptor> Formats,
    string? SuggestedFileName = null,
    string? SuggestedDirectory = null);

/// <summary>The outcome of a save-file picker.</summary>
/// <param name="Path">The chosen path.</param>
/// <param name="PickerConfirmedOverwrite">
/// Whether the picker itself already obtained overwrite confirmation from the user.
/// </param>
/// <remarks>
/// <para>
/// This is required rather than optional, and defaults to nothing, so an
/// implementer must decide deliberately. The safe answer is
/// <see langword="false"/>.
/// </para>
/// <para>
/// A <see langword="true"/> value suppresses only the duplicate prompt. The
/// framework still confirms whenever it independently observes that the target
/// exists and is not the currently open document, because a picker that claims to
/// confirm and does not would otherwise produce a silent overwrite — the exact
/// outcome the save workflow exists to prevent. One redundant prompt costs far
/// less than one destroyed save.
/// </para>
/// </remarks>
public sealed record SaveFilePickResult(string Path, bool PickerConfirmedOverwrite);

/// <summary>A confirmation the user must accept before the framework proceeds.</summary>
public sealed record ConfirmationRequest
{
    /// <summary>Framework-owned title. Never sourced from a codec.</summary>
    public required string Title { get; init; }

    /// <summary>Framework-owned framing sentence. Never sourced from a codec.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// Accept label naming the actual outcome — "Overwrite save file", not "OK".
    /// </summary>
    /// <remarks>Generic accept labels are not used for destructive choices.</remarks>
    public required string AcceptLabel { get; init; }

    /// <summary>Cancel label.</summary>
    public string CancelLabel { get; init; } = "Cancel";

    /// <summary>Whether accepting destroys or overwrites data.</summary>
    public bool IsDestructive { get; init; }

    /// <summary>
    /// Supplementary detail supplied by a codec. Rendered as plain text in a
    /// visually distinct, non-chrome region, with control and bidi characters
    /// stripped and length, line count, and total count capped.
    /// </summary>
    public IReadOnlyList<UntrustedText> Details { get; init; } = [];
}

/// <summary>A message shown to the user with no choice attached.</summary>
/// <param name="Title">Framework-owned title.</param>
/// <param name="Message">Framework-owned body.</param>
/// <param name="Details">Optional untrusted supplementary text.</param>
public sealed record MessageRequest(
    string Title,
    string Message,
    IReadOnlyList<UntrustedText>? Details = null);

/// <summary>
/// Everything the framework needs from the user that requires a dialog.
/// </summary>
/// <remarks>
/// A themed default implementation ships with the framework. Editors may replace
/// it — to integrate a platform picker, or to drive it from tests — but a
/// replacement inherits the fail-closed overwrite rule described on
/// <see cref="SaveFilePickResult"/>.
/// </remarks>
public interface IUserInteraction
{
    /// <summary>Asks the user to choose an existing file to open.</summary>
    /// <param name="request">Picker configuration.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    /// <returns>The chosen path, or <see langword="null"/> if dismissed.</returns>
    ValueTask<string?> PickOpenFileAsync(
        FilePickerRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Asks the user where to write a file.</summary>
    /// <param name="request">Picker configuration.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    /// <returns>The chosen path and overwrite-confirmation status, or <see langword="null"/>.</returns>
    ValueTask<SaveFilePickResult?> PickSaveFileAsync(
        FilePickerRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Asks the user to choose a folder.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="suggestedDirectory">Optional starting directory.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    /// <returns>The chosen path, or <see langword="null"/> if dismissed.</returns>
    ValueTask<string?> PickFolderAsync(
        string title,
        string? suggestedDirectory = null,
        CancellationToken cancellationToken = default);

    /// <summary>Asks the user to accept or decline an action.</summary>
    /// <param name="request">What the user is accepting.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    /// <returns><see langword="true"/> when accepted.</returns>
    ValueTask<bool> ConfirmAsync(
        ConfirmationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Shows a message with no choice attached.</summary>
    /// <param name="request">What to show.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    ValueTask ShowMessageAsync(
        MessageRequest request,
        CancellationToken cancellationToken = default);
}

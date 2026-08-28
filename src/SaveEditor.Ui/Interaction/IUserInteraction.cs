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
/// <strong>A <see langword="true"/> value no longer suppresses anything, and is
/// retained for diagnostics only.</strong> The framework confirms every destination
/// it independently observes to exist. Revision 3 let the declaration suppress the
/// prompt for the currently-open document, treating it as genuinely duplicated. It
/// is not: the operating system's dialog asks "replace this file?", while the
/// framework's asks "replace this file, having taken a verified backup, with a codec
/// whose preservation claim reads like this". A picker cannot ask the second
/// question, so it cannot stand in for it (finding F-2 supersedes finding A7).
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
    /// The file this action will affect, already neutralised for display.
    /// </summary>
    /// <remarks>
    /// Part of the contract rather than a convention of the shipped implementation.
    /// The overwrite confirmation is the most destructive prompt in the product, and
    /// an editor that replaces <see cref="IUserInteraction"/> must not silently end
    /// up showing one without naming the file it is about to replace.
    /// </remarks>
    public Display.PathLabel? Target { get; init; }

    /// <summary>
    /// Supplementary detail supplied by a codec. Rendered as plain text in a
    /// visually distinct, non-chrome region, with control and bidi characters
    /// stripped and length, line count, and total count capped.
    /// </summary>
    public IReadOnlyList<UntrustedText> Details { get; init; } = [];
}

/// <summary>One option in a choice prompt.</summary>
/// <param name="Key">Stable identifier returned when this option is chosen.</param>
/// <param name="Label">How the option is shown.</param>
/// <param name="Description">Optional supporting detail.</param>
public sealed record ChoicePromptOption(string Key, string Label, string? Description = null);

/// <summary>A prompt asking the user to pick one of several options.</summary>
/// <param name="Title">Framework-owned title.</param>
/// <param name="Message">Framework-owned framing sentence.</param>
/// <param name="Options">The options, in the order they should be offered.</param>
/// <remarks>
/// Exists because ambiguous codec detection has to be resolved by the user rather
/// than by registration order, and expressing that as a series of yes/no
/// confirmations makes the ordering itself look like a recommendation.
/// </remarks>
public sealed record ChoicePrompt(
    string Title,
    string Message,
    IReadOnlyList<ChoicePromptOption> Options);

/// <summary>A read-only document shown to the user.</summary>
/// <param name="Title">Framework-owned title.</param>
/// <param name="Content">
/// The body. Untrusted: it may be codec-supplied or derived from save-file bytes.
/// </param>
public sealed record DocumentRequest(string Title, UntrustedText Content);

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
/// <para>
/// A themed default implementation ships with the framework. Editors may replace
/// it — to integrate a platform picker, or to drive it from tests — but a
/// replacement inherits the fail-closed overwrite rule described on
/// <see cref="SaveFilePickResult"/>.
/// </para>
/// <para>
/// <strong>An implementation is called from whatever thread the operation is on,
/// and must marshal onto the UI thread itself.</strong> The safe file workflow runs
/// codecs on the thread pool and resumes there, so a picker or dialog raised from
/// the middle of a save is reached from a pool thread. An implementation that
/// touches UI objects directly gets an invalid-thread failure that the workflow
/// catches and reports as a failed save — the user sees no dialog at all, and no
/// reason why. The framework's own <c>ThemedUserInteraction</c> marshals; a
/// replacement has to as well.
/// </para>
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

    /// <summary>Asks the user to pick one of several options.</summary>
    /// <param name="prompt">The options and their framing.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    /// <returns>
    /// The chosen option's key, or <see langword="null"/> if dismissed. A dismissal
    /// is not a selection: the caller must abandon the operation rather than
    /// defaulting to the first option.
    /// </returns>
    ValueTask<string?> ChooseAsync(
        ChoicePrompt prompt,
        CancellationToken cancellationToken = default);

    /// <summary>Shows a read-only document.</summary>
    /// <param name="request">Title and body.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    ValueTask ShowDocumentAsync(
        DocumentRequest request,
        CancellationToken cancellationToken = default);
}

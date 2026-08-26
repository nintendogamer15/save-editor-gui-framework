using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.Workflow;

/// <summary>
/// Resolves detection ambiguity by asking the user.
/// </summary>
/// <remarks>
/// Detection ambiguity is never resolved by registration order. Registration order is an
/// accident of how the consuming application wired its composition root; the user is the
/// only party who knows which game wrote the file.
/// </remarks>
public interface ISaveFormatChooser
{
    /// <summary>Asks which of several formats a file should be opened as.</summary>
    /// <param name="candidates">The formats that claimed the file, at equal confidence.</param>
    /// <param name="fileName">The file being opened, for display.</param>
    /// <param name="cancellationToken">Cancels the choice.</param>
    /// <returns>The chosen format, or <see langword="null"/> if the user chose none.</returns>
    ValueTask<SaveFormatDescriptor?> ChooseAsync(
        IReadOnlyList<SaveFormatDescriptor> candidates,
        string fileName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A chooser built from the confirmation dialog every <see cref="IUserInteraction"/>
/// already provides.
/// </summary>
/// <remarks>
/// <para>
/// The candidates are offered one at a time, ordered by display name rather than by
/// registration order, and the user accepts one or declines them all. Declining every
/// candidate abandons the open rather than falling back to a guess.
/// </para>
/// <para>
/// An editor that wants a single list dialog supplies its own implementation. This one
/// exists so that ambiguity resolution never has to be skipped for want of a dialog.
/// </para>
/// </remarks>
public sealed class ConfirmationSaveFormatChooser : ISaveFormatChooser
{
    private readonly IUserInteraction _interaction;

    /// <summary>Creates a chooser over an interaction surface.</summary>
    /// <param name="interaction">Where the confirmations are shown.</param>
    public ConfirmationSaveFormatChooser(IUserInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        _interaction = interaction;
    }

    /// <inheritdoc />
    public async ValueTask<SaveFormatDescriptor?> ChooseAsync(
        IReadOnlyList<SaveFormatDescriptor> candidates,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        foreach (var candidate in candidates.OrderBy(format => format.DisplayName, StringComparer.Ordinal))
        {
            var accepted = await _interaction.ConfirmAsync(
                new ConfirmationRequest
                {
                    Title = "More than one format matches",
                    Message =
                        $"{candidates.Count} installed formats recognize this file. Open it as {candidate.DisplayName}?",
                    AcceptLabel = $"Open as {candidate.DisplayName}",
                    CancelLabel = "Try another format",
                    IsDestructive = false,
                },
                cancellationToken).ConfigureAwait(false);

            if (accepted)
            {
                return candidate;
            }
        }

        return null;
    }
}

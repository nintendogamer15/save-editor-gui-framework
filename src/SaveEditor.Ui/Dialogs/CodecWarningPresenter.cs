using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Display;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.Dialogs;

/// <summary>One codec-supplied warning after sanitization, ready to display.</summary>
/// <param name="Text">
/// Plain text with control, bidirectional, and ill-formed characters replaced, and
/// length and line count capped.
/// </param>
/// <param name="WasTruncated">
/// Whether the original text was shortened to fit <see cref="CodecWarningPresenter.MaxWarningLength"/>
/// or <see cref="CodecWarningPresenter.MaxWarningLines"/>.
/// </param>
public sealed record SanitizedWarning(string Text, bool WasTruncated);

/// <summary>
/// The warnings actually shown in a dialog, plus how many more exist than were shown.
/// </summary>
/// <param name="Shown">
/// At most <see cref="CodecWarningPresenter.MaxShownWarnings"/> sanitized warnings.
/// </param>
/// <param name="TotalCount">How many warnings were offered before capping.</param>
public sealed record SanitizedWarningList(IReadOnlyList<SanitizedWarning> Shown, int TotalCount)
{
    /// <summary>How many warnings exist beyond the ones in <see cref="Shown"/>.</summary>
    public int OmittedCount => Math.Max(0, TotalCount - Shown.Count);
}

/// <summary>
/// Selects and sanitizes codec-supplied warning text for display inside a dialog.
/// </summary>
/// <remarks>
/// <para>
/// This is the framework's answer to finding A8. Validation messages and
/// unknown-data warnings are produced by a codec from attacker-controlled save
/// bytes, and they are shown inside a dialog whose accept action can overwrite a
/// real save. Left unsanitized, such text could imitate framework chrome ("Integrity
/// verified. Safe to continue."), flood the dialog with thousands of trivial entries
/// to bury the one that mattered, or use bidi overrides to make an unrelated
/// filename read as the target being overwritten.
/// </para>
/// <para>
/// Three independent controls close that gap: <see cref="SelectMostSevere"/> orders
/// by <see cref="ValidationSeverity"/> before truncating to
/// <see cref="MaxShownWarnings"/>, so a flood of trivial warnings cannot bury an
/// error; <see cref="Sanitize"/> strips control and bidirectional characters via the
/// shared <see cref="DisplayTextNeutralizer"/> and caps per-warning length and line
/// count; and every caller renders the result in a visually distinct, non-chrome
/// region rather than as if it were framework-authored text.
/// </para>
/// </remarks>
public static class CodecWarningPresenter
{
    /// <summary>The most warnings ever shown in one dialog, regardless of how many were offered.</summary>
    public const int MaxShownWarnings = 8;

    /// <summary>The longest a single sanitized warning is allowed to render.</summary>
    public const int MaxWarningLength = 300;

    /// <summary>The most line breaks kept from a single warning before neutralization.</summary>
    /// <remarks>
    /// The cap bounds work on a pathological input; it does not by itself produce
    /// multi-line output. <see cref="DisplayTextNeutralizer"/> replaces every
    /// remaining line break with a visible marker, so a warning never actually
    /// occupies more than one flowed line on screen -- which is also what stops a
    /// crafted warning from laying out multi-line ASCII art that reads as chrome.
    /// </remarks>
    public const int MaxWarningLines = 3;

    /// <summary>
    /// Characters scanned from a single raw warning before any other cap is applied.
    /// </summary>
    /// <remarks>
    /// Bounds the cost of counting line breaks in a warning of unbounded length, so a
    /// codec cannot make sanitization itself expensive by emitting a single warning
    /// containing megabytes of text or millions of newlines.
    /// </remarks>
    internal const int RawScanCap = 4000;

    /// <summary>
    /// Orders warnings by severity, most severe first, and keeps at most
    /// <paramref name="maxCount"/>.
    /// </summary>
    /// <param name="messages">The codec's validation findings, in the codec's own order.</param>
    /// <param name="maxCount">The most entries to keep. Defaults to <see cref="MaxShownWarnings"/>.</param>
    /// <returns>
    /// The untrusted text of the most severe <paramref name="maxCount"/> messages.
    /// Ties keep the codec's original relative order.
    /// </returns>
    /// <remarks>
    /// Showing the first eight rather than the most severe eight would let a codec
    /// emitting thousands of trivial warnings bury the one that actually blocks the
    /// write or matters to the user. This is the selection half of closing A8; the
    /// text itself is still untrusted and must pass through <see cref="Sanitize"/>
    /// before it reaches a screen.
    /// </remarks>
    public static IReadOnlyList<UntrustedText> SelectMostSevere(
        IReadOnlyList<ValidationMessage> messages,
        int maxCount = MaxShownWarnings)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return
        [
            .. messages
                .Select(static (message, index) => (message, index))
                .OrderByDescending(pair => pair.message.Severity)
                .ThenBy(pair => pair.index)
                .Take(Math.Max(0, maxCount))
                .Select(pair => pair.message.Text),
        ];
    }

    /// <summary>
    /// Sanitizes and caps codec-supplied warning text for display.
    /// </summary>
    /// <param name="details">
    /// Untrusted warning text. Should already be ordered most-severe-first by
    /// <see cref="SelectMostSevere"/> when the caller has severities available; when
    /// it is not (for example, <see cref="ConfirmationRequest.Details"/>, which
    /// carries no severity), the first <paramref name="maxCount"/> entries are kept
    /// as a defense-in-depth backstop rather than as the primary selection.
    /// </param>
    /// <param name="maxCount">The most warnings to show. Defaults to <see cref="MaxShownWarnings"/>.</param>
    /// <param name="maxLength">The longest a single warning may render. Defaults to <see cref="MaxWarningLength"/>.</param>
    /// <param name="maxLines">The most line breaks kept from a single warning. Defaults to <see cref="MaxWarningLines"/>.</param>
    /// <returns>Sanitized, capped warnings plus the count omitted.</returns>
    public static SanitizedWarningList Sanitize(
        IReadOnlyList<UntrustedText> details,
        int maxCount = MaxShownWarnings,
        int maxLength = MaxWarningLength,
        int maxLines = MaxWarningLines)
    {
        ArgumentNullException.ThrowIfNull(details);

        var boundedCount = Math.Max(0, maxCount);
        var boundedLength = Math.Max(0, maxLength);
        var boundedLines = Math.Max(1, maxLines);

        var shown = new List<SanitizedWarning>(Math.Min(details.Count, boundedCount));
        for (var i = 0; i < details.Count && shown.Count < boundedCount; i++)
        {
            shown.Add(SanitizeOne(details[i].Value, boundedLength, boundedLines));
        }

        return new SanitizedWarningList(shown, details.Count);
    }

    /// <summary>
    /// Convenience combining <see cref="SelectMostSevere"/> and <see cref="Sanitize"/>
    /// for a caller that still has each message's <see cref="ValidationSeverity"/>.
    /// </summary>
    /// <param name="messages">The codec's validation findings.</param>
    /// <param name="maxCount">The most warnings to show. Defaults to <see cref="MaxShownWarnings"/>.</param>
    /// <param name="maxLength">The longest a single warning may render. Defaults to <see cref="MaxWarningLength"/>.</param>
    /// <param name="maxLines">The most line breaks kept from a single warning. Defaults to <see cref="MaxWarningLines"/>.</param>
    /// <returns>Sanitized, most-severe-first, capped warnings plus the count omitted.</returns>
    public static SanitizedWarningList SelectMostSevereAndSanitize(
        IReadOnlyList<ValidationMessage> messages,
        int maxCount = MaxShownWarnings,
        int maxLength = MaxWarningLength,
        int maxLines = MaxWarningLines) =>
        Sanitize(SelectMostSevere(messages, maxCount), maxCount, maxLength, maxLines);

    private static SanitizedWarning SanitizeOne(string? raw, int maxLength, int maxLines)
    {
        var text = raw ?? string.Empty;
        var truncated = false;

        if (text.Length > RawScanCap)
        {
            text = text[..RawScanCap];
            truncated = true;
        }

        var lines = text.Split('\n');
        if (lines.Length > maxLines)
        {
            text = string.Join('\n', lines[..maxLines]);
            truncated = true;
        }

        // The shared neutralizer used by the path formatter (finding A13). Reused
        // rather than reimplemented: it already replaces control, bidi, and
        // ill-formed characters -- including the line breaks kept above -- with a
        // visible marker.
        var neutralized = DisplayTextNeutralizer.Neutralize(text, out _);

        if (neutralized.Length > maxLength)
        {
            neutralized = neutralized[..maxLength];
            truncated = true;
        }

        return new SanitizedWarning(neutralized, truncated);
    }
}

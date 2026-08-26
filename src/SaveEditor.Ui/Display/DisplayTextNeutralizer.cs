using System.Text;

namespace SaveEditor.Ui.Display;

/// <summary>
/// Replaces the characters that let a string render as something other than what it
/// says, and wraps the result so it cannot reorder the text around it.
/// </summary>
/// <remarks>
/// <para>
/// This is the shared core of finding A13. A path arriving from a tampered settings
/// file, a drag-and-drop payload, or a picker can carry a right-to-left override, so
/// that the bytes <c>save</c>, U+202E, <c>gnp.txt</c> are displayed as
/// <c>savetxt.png</c>. When such a string is the target named in an overwrite
/// confirmation, the user reads one filename and authorizes writing to another.
/// </para>
/// <para>
/// Replacement is visible rather than silent. Deleting the offending characters would
/// produce a plausible-looking name that merely differs from the real one; substituting
/// U+FFFD makes a tampered path look tampered, and the caller additionally learns that
/// something was replaced through <see cref="PathLabel.HasReplacedCharacters"/>.
/// </para>
/// <para>
/// Internal on purpose. The output of this type is display text and must never become a
/// path again, so no public member of this assembly hands a caller a bare neutralized
/// string; <see cref="PathLabel"/> is the only way out.
/// </para>
/// <para>
/// Character literals here are written as numeric casts rather than escape sequences so
/// that this file stays pure ASCII on disk. An invisible reordering character sitting
/// literally inside the source of the code that removes reordering characters would be
/// the worst possible place for one.
/// </para>
/// </remarks>
internal static class DisplayTextNeutralizer
{
    /// <summary>Stands in for every character that is replaced. U+FFFD.</summary>
    internal const char Replacement = (char)0xFFFD;

    /// <summary>U+2068, opens an isolated run whose direction is auto-detected.</summary>
    internal const char FirstStrongIsolate = (char)0x2068;

    /// <summary>U+2069, closes the run opened by <see cref="FirstStrongIsolate"/>.</summary>
    internal const char PopDirectionalIsolate = (char)0x2069;

    /// <summary>Reports whether a character is replaced on sight.</summary>
    /// <param name="value">The candidate character.</param>
    /// <returns><see langword="true"/> when the character must not reach a display surface.</returns>
    /// <remarks>
    /// <para>
    /// Three families, all of which change what the user sees without changing what the
    /// bytes say:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// C0 (U+0000-U+001F), DEL (U+007F), and C1 (U+0080-U+009F). A newline or tab inside
    /// a status-bar path breaks the line the user is reading; the rest are undisplayable.
    /// </item>
    /// <item>
    /// The bidirectional formatting characters: U+061C, U+200E, U+200F, U+202A-U+202E,
    /// and U+2066-U+2069. These are the reordering attack. U+061C is not named in the
    /// plan's enumeration but is the same mechanism and is closed here too.
    /// </item>
    /// <item>
    /// U+2028 and U+2029, the line and paragraph separators, which are not
    /// <see cref="char.IsControl(char)"/> but end a line in every text renderer.
    /// </item>
    /// </list>
    /// <para>
    /// Deliberately absent: zero-width joiner and non-joiner (U+200C, U+200D) and the
    /// other invisible format characters. They cannot reorder anything, and they are
    /// required for correct rendering of legitimate Persian and Indic filenames.
    /// Replacing them would corrupt real names for no reordering benefit. The residual --
    /// two distinct paths that look alike because one carries an invisible character --
    /// is a homograph problem rather than a reordering one; it is outside A13 and is not
    /// claimed to be closed.
    /// </para>
    /// </remarks>
    internal static bool ShouldReplace(char value)
    {
        int code = value;

        return code <= 0x001F
            || code == 0x007F
            || (code >= 0x0080 && code <= 0x009F)
            || code == 0x061C
            || code == 0x200E
            || code == 0x200F
            || (code >= 0x202A && code <= 0x202E)
            || code == 0x2028
            || code == 0x2029
            || (code >= 0x2066 && code <= 0x2069);
    }

    /// <summary>
    /// Substitutes every character <see cref="ShouldReplace(char)"/> names, plus any
    /// unpaired surrogate.
    /// </summary>
    /// <param name="text">The text to neutralize.</param>
    /// <param name="replaced">Set when at least one character was substituted.</param>
    /// <returns>
    /// A well-formed UTF-16 string of the same length as <paramref name="text"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Unpaired surrogates are replaced as well. Windows filenames may contain them, no
    /// renderer agrees on what to draw for one, and leaving them in would mean the
    /// framework hands ill-formed UTF-16 to a screen reader.
    /// </para>
    /// <para>
    /// Idempotent: U+FFFD is not itself replaced, so neutralizing an already-neutralized
    /// string returns it unchanged. Length is preserved one for one, so a replacement can
    /// never shorten a name into a different plausible name.
    /// </para>
    /// </remarks>
    internal static string Neutralize(string text, out bool replaced)
    {
        replaced = false;

        if (text.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder? builder = null;

        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];

            if (char.IsHighSurrogate(current) &&
                i + 1 < text.Length &&
                char.IsLowSurrogate(text[i + 1]))
            {
                builder?.Append(current).Append(text[i + 1]);
                i++;
                continue;
            }

            var offending =
                char.IsHighSurrogate(current) ||
                char.IsLowSurrogate(current) ||
                ShouldReplace(current);

            if (!offending)
            {
                builder?.Append(current);
                continue;
            }

            // First offender: copy the clean prefix once, then keep appending.
            builder ??= new StringBuilder(text.Length).Append(text, 0, i);
            builder.Append(Replacement);
            replaced = true;
        }

        return builder?.ToString() ?? text;
    }

    /// <summary>
    /// Wraps text in a first-strong isolate so it cannot reorder the sentence around it.
    /// </summary>
    /// <param name="text">Already-neutralized text.</param>
    /// <returns>
    /// The isolated text, or the empty string when there is nothing to isolate.
    /// </returns>
    /// <remarks>
    /// The status bar frames the target in a sentence, and so does every confirmation
    /// dialog. Without an isolate, a path whose first strong character is right-to-left
    /// drags the surrounding words into its own run and the sentence reorders on screen.
    /// U+2068 auto-detects the run's direction, which is what a path of unknown script
    /// needs; forcing left-to-right with U+202D instead would mis-render a legitimate
    /// Hebrew or Arabic name.
    /// </remarks>
    internal static string Isolate(string text) =>
        text.Length == 0
            ? string.Empty
            : string.Create(
                text.Length + 2,
                text,
                static (span, value) =>
                {
                    span[0] = FirstStrongIsolate;
                    value.AsSpan().CopyTo(span[1..]);
                    span[^1] = PopDirectionalIsolate;
                });

    /// <summary>
    /// Removes an outer isolate pair this type added, so re-formatting is stable.
    /// </summary>
    /// <param name="text">Text that may already be isolated.</param>
    /// <returns>The text without its outer isolate pair.</returns>
    /// <remarks>
    /// Without this, formatting an already-formatted label would replace the wrapper with
    /// two U+FFFD and degrade the string on every pass. Recognizing the wrapper costs
    /// nothing in safety: a raw path that genuinely began with U+2068 and ended with
    /// U+2069 is displayed exactly as the safe form would be, because a balanced isolate
    /// spanning the whole string is precisely the neutral wrapping applied here anyway.
    /// Anything unbalanced inside is still replaced.
    /// </remarks>
    internal static string Unwrap(string text) =>
        text.Length >= 2 && text[0] == FirstStrongIsolate && text[^1] == PopDirectionalIsolate
            ? text[1..^1]
            : text;
}

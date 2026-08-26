using System.Text;

namespace SaveEditor.Ui.Display;

/// <summary>
/// Turns a file location into text safe to put on a screen. The single formatter behind
/// the recents menu, the status bar, the accessible announcement region, and every
/// confirmation dialog.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, because the status bar is the canonical channel naming the file
/// about to be overwritten and a confirmation dialog names the target of a destructive
/// action. If those two surfaces formatted paths differently, the difference would show
/// up first as a spoofed overwrite target. Finding A13.
/// </para>
/// <para>
/// Three properties hold for every input:
/// </para>
/// <list type="number">
/// <item>
/// <b>Neutralized.</b> Control, bidirectional, and ill-formed characters are replaced
/// with a visible U+FFFD, so a tampered location looks tampered instead of merely
/// looking different. See <see cref="DisplayTextNeutralizer"/>.
/// </item>
/// <item>
/// <b>Isolated.</b> The result is wrapped in U+2068/U+2069 so right-to-left content
/// cannot reorder the sentence the label sits inside.
/// </item>
/// <item>
/// <b>Middle-truncated, never end-truncated, with the final two components intact.</b>
/// </item>
/// </list>
/// <para>
/// <b>The result is display text, never a path.</b> It is returned as
/// <see cref="PathLabel"/>, which carries no path-shaped member and whose values are
/// always isolate-wrapped and so never equal, or openable as, the location they
/// describe. See the remarks on <see cref="PathLabel"/>.
/// </para>
/// <para>
/// Total by contract: any string, including null, empty, whitespace, one built entirely
/// from bidi marks, one longer than any display, or one containing unpaired surrogates,
/// produces a label. Nothing here throws.
/// </para>
/// <para>
/// The formatter is stateless and immutable; <see cref="Default"/> may be shared freely
/// across threads.
/// </para>
/// </remarks>
public sealed class PathDisplayFormatter
{
    /// <summary>
    /// Characters targeted when the caller does not say. Sized for the status bar's path
    /// field in the reference layout.
    /// </summary>
    /// <remarks>
    /// Surfaces with a known width should pass their own budget to
    /// <see cref="Format(string, int)"/> rather than inherit this. A recents menu is
    /// narrower than a status bar, and a confirmation dialog is wider than both.
    /// </remarks>
    public const int DefaultMaxLength = 64;

    /// <summary>Marks the elided middle. U+2026.</summary>
    internal const char Ellipsis = (char)0x2026;

    private const char WindowsSeparator = '\\';
    private const char PosixSeparator = '/';

    /// <summary>Creates a formatter.</summary>
    /// <param name="options">Budget and separator style. Defaults are used when null.</param>
    public PathDisplayFormatter(PathDisplayOptions? options = null) =>
        Options = options ?? new PathDisplayOptions();

    /// <summary>A shared formatter using the default budget and the native separator style.</summary>
    public static PathDisplayFormatter Default { get; } = new();

    /// <summary>The budget and separator style in force.</summary>
    public PathDisplayOptions Options { get; }

    /// <summary>Formats a location using <see cref="PathDisplayOptions.MaxLength"/>.</summary>
    /// <param name="path">The raw location. May be null, empty, or hostile.</param>
    /// <returns>Display text. Never a path -- see <see cref="PathLabel"/>.</returns>
    public PathLabel Format(string? path) => Format(path, Options.MaxLength);

    /// <summary>Formats a location into a caller-supplied budget.</summary>
    /// <param name="path">The raw location. May be null, empty, or hostile.</param>
    /// <param name="maxLength">
    /// Visible characters to aim for. Values below one are clamped to one; the result may
    /// still overrun, see <see cref="PathLabel.ExceedsMaxLength"/>.
    /// </param>
    /// <returns>Display text. Never a path -- see <see cref="PathLabel"/>.</returns>
    public PathLabel Format(string? path, int maxLength)
    {
        if (string.IsNullOrEmpty(path))
        {
            return PathLabel.Empty;
        }

        // Neutralize before splitting. A separator can never be a control character, so
        // the split is unaffected, and doing it in this order means no later step ever
        // handles a raw reordering character.
        var neutral = DisplayTextNeutralizer.Neutralize(
            DisplayTextNeutralizer.Unwrap(path),
            out var replaced);

        if (neutral.Length == 0)
        {
            return PathLabel.Empty;
        }

        var budget = Math.Max(1, maxLength);
        var windows = UsesWindowsRules();
        var separator = windows ? WindowsSeparator : PosixSeparator;

        // On Windows both separators mean the same thing, so render one of them. On
        // POSIX a backslash is part of the name and is left exactly where it was.
        var normalized = windows ? neutral.Replace('/', WindowsSeparator) : neutral;

        Split(normalized, windows, separator, out var root, out var components);

        string visible;
        bool truncated;

        if (components.Count == 0)
        {
            // A volume root, a bare UNC server, or a run of separators. Nothing to elide.
            visible = root.Length == 0 ? normalized : root;
            truncated = false;
        }
        else
        {
            var tailCount = Math.Min(2, components.Count);
            var headCount = components.Count - tailCount;

            // Lengths are computed arithmetically rather than by composing a candidate per
            // step. A drop payload is not length-bounded before it reaches a display
            // surface, and composing inside the loop would make a path of many short
            // components quadratic -- a hang in the status bar is still a denial of
            // service, even a polite one.
            var prefix = new int[components.Count + 1];
            for (var i = 0; i < components.Count; i++)
            {
                prefix[i + 1] = prefix[i] + components[i].Length;
            }

            var tailLength = prefix[components.Count] - prefix[headCount];
            var kept = headCount;

            while (kept > 0 &&
                   ComposedLength(root.Length, prefix[kept], kept, headCount, tailCount, tailLength) > budget)
            {
                kept--;
            }

            visible = Compose(root, components, kept, headCount, separator);
            truncated = kept < headCount;
        }

        return new PathLabel(
            DisplayTextNeutralizer.Isolate(visible),
            DisplayTextNeutralizer.Isolate(neutral),
            truncated,
            replaced,
            visible.Length > budget);
    }

    private bool UsesWindowsRules() => Options.SeparatorStyle switch
    {
        PathSeparatorStyle.Windows => true,
        PathSeparatorStyle.Posix => false,
        _ => OperatingSystem.IsWindows(),
    };

    /// <summary>
    /// Length <see cref="Compose"/> would produce, without producing it.
    /// </summary>
    private static int ComposedLength(
        int rootLength,
        int headLength,
        int kept,
        int headCount,
        int tailCount,
        int tailLength)
    {
        var elided = kept < headCount ? 1 : 0;
        var pieces = kept + elided + tailCount;

        return rootLength + headLength + elided + tailLength + Math.Max(0, pieces - 1);
    }

    /// <summary>
    /// Rebuilds the visible text keeping <paramref name="kept"/> leading components.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Truncation is component-granular, never character-granular. Every component that
    /// appears in the output appears whole; a component is either shown completely or
    /// replaced entirely by the ellipsis. Cutting characters out of the middle of a
    /// component would manufacture a fragment that reads like a complete name and does
    /// not correspond to one, which is a smaller version of the substitution this
    /// formatter exists to stop.
    /// </para>
    /// <para>
    /// The root is never elided. On Windows it carries the drive letter or the UNC server
    /// and share, and "which machine" is exactly as load-bearing as "which folder" when
    /// the next click overwrites a file.
    /// </para>
    /// </remarks>
    private static string Compose(
        string root,
        List<string> components,
        int kept,
        int headCount,
        char separator)
    {
        var builder = new StringBuilder(root);
        var wrote = false;

        for (var i = 0; i < kept; i++)
        {
            if (wrote)
            {
                builder.Append(separator);
            }

            builder.Append(components[i]);
            wrote = true;
        }

        if (kept < headCount)
        {
            if (wrote)
            {
                builder.Append(separator);
            }

            builder.Append(Ellipsis);
            wrote = true;
        }

        for (var i = headCount; i < components.Count; i++)
        {
            if (wrote)
            {
                builder.Append(separator);
            }

            builder.Append(components[i]);
            wrote = true;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits already-normalized text into a root that is always shown and the components
    /// that may be elided.
    /// </summary>
    /// <remarks>
    /// Purely lexical and total. It deliberately does not call <c>System.IO.Path</c>:
    /// those APIs follow the running platform rather than
    /// <see cref="PathDisplayOptions.SeparatorStyle"/>, several of them throw on input
    /// this method is required to survive, and <c>GetFullPath</c> would consult ambient
    /// state. Screening a location for use is <c>PathSyntaxGuard</c>'s job and happens
    /// somewhere else entirely; this method only decides where to draw the boxes.
    /// </remarks>
    private static void Split(
        string text,
        bool windows,
        char separator,
        out string root,
        out List<string> components)
    {
        var start = 0;
        root = string.Empty;

        if (windows)
        {
            if (text.Length >= 2 && text[0] == WindowsSeparator && text[1] == WindowsSeparator)
            {
                // UNC and device-prefixed shapes: the first two components name the host
                // and the share, and both belong to the root so neither can be elided.
                var rest = text.AsSpan(2);
                var first = rest.IndexOf(WindowsSeparator);
                var second = first < 0 ? -1 : rest[(first + 1)..].IndexOf(WindowsSeparator);

                start = second < 0 ? text.Length : 2 + first + 1 + second + 1;
                root = text[..start];
            }
            else if (text.Length >= 2 && text[1] == ':' && char.IsAsciiLetter(text[0]))
            {
                var rooted = text.Length >= 3 && text[2] == WindowsSeparator;
                start = rooted ? 3 : 2;
                root = text[..start];
            }
            else if (text[0] == WindowsSeparator)
            {
                start = 1;
                root = text[..1];
            }
        }
        else if (text[0] == PosixSeparator)
        {
            start = 1;
            root = text[..1];
        }

        components = [];

        foreach (var part in text[start..].Split(separator))
        {
            if (part.Length != 0)
            {
                components.Add(part);
            }
        }
    }
}

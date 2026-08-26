namespace SaveEditor.Ui.Display;

/// <summary>
/// Text describing a file location, prepared for a screen. Not a path, and not
/// convertible back into one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Display only.</b> Nothing in this type may be opened, written, compared against a
/// real path, stored in settings, or passed to any member of <c>System.IO</c>. It exists
/// specifically because the string a user reads and the string the framework opens must
/// be allowed to differ: neutralizing a right-to-left override is what makes the
/// displayed name honest, and that same substitution is what makes the result useless as
/// a path.
/// </para>
/// <para>
/// That rule is structural rather than advisory. Every non-empty <see cref="Label"/> and
/// <see cref="FullLabel"/> is wrapped in a U+2068/U+2069 isolate pair, so the value is
/// never equal to the path it describes and never names an existing file. A caller who
/// ignores this documentation and opens the value gets a clean "file not found" instead
/// of a silent write to the wrong target. That is the whole point: a formatter whose
/// output round-tripped into the filesystem would reintroduce the substitution attack it
/// exists to prevent.
/// </para>
/// <para>
/// The real path stays with whoever already had it -- <c>ResolvedFile.CanonicalPath</c>,
/// the recents entry, the drop payload -- and is never recovered from here.
/// </para>
/// <para>
/// Two strings, not one. <see cref="Label"/> is middle-truncated to fit a surface;
/// <see cref="FullLabel"/> is not truncated at all and is what belongs in a tooltip and
/// in the accessible description. A screen-reader user must not be handed a lossier
/// string than a sighted user, so the accessible value is the fuller one -- still
/// neutralized, because an announcement region reads it aloud and a control character
/// there is a rendering hazard of its own.
/// </para>
/// </remarks>
public sealed record PathLabel
{
    internal PathLabel(
        string label,
        string fullLabel,
        bool isTruncated,
        bool hasReplacedCharacters,
        bool exceedsMaxLength)
    {
        Label = label;
        FullLabel = fullLabel;
        IsTruncated = isTruncated;
        HasReplacedCharacters = hasReplacedCharacters;
        ExceedsMaxLength = exceedsMaxLength;
    }

    /// <summary>The label for a path that is absent -- no document open, no recent entry.</summary>
    /// <remarks>
    /// Both strings are empty rather than a placeholder such as "(none)". The wording of
    /// an empty state belongs to the surface showing it, and a placeholder invented here
    /// would appear untranslated in four different contexts.
    /// </remarks>
    public static PathLabel Empty { get; } = new(string.Empty, string.Empty, false, false, false);

    /// <summary>
    /// The visible text: neutralized, isolated, and middle-truncated to the requested
    /// budget.
    /// </summary>
    /// <remarks>
    /// Bind this to the status bar, the recents menu item, and the target named in a
    /// confirmation dialog. Never open it.
    /// </remarks>
    public string Label { get; }

    /// <summary>
    /// The whole location: neutralized and isolated, but never truncated.
    /// </summary>
    /// <remarks>
    /// Bind this to the tooltip and to <c>AutomationProperties.HelpText</c> so the
    /// accessible description carries at least as much as the visible one. Never open it.
    /// </remarks>
    public string FullLabel { get; }

    /// <summary>Whether <see cref="Label"/> elided one or more middle components.</summary>
    /// <remarks>
    /// Useful for deciding that a tooltip is worth attaching. It does not mean the label
    /// is untrustworthy; every component still shown is shown whole.
    /// </remarks>
    public bool IsTruncated { get; }

    /// <summary>
    /// Whether at least one control, bidirectional, or ill-formed character was replaced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the machine-readable half of the visible U+FFFD markers, and it is the
    /// signal a destructive confirmation should act on. A path that needed replacing did
    /// not come from a user typing a filename; it came from a tampered settings file, a
    /// hostile drop payload, or a crafted archive. A surface about to overwrite that
    /// target is entitled to say so louder than usual.
    /// </para>
    /// <para>
    /// It never blocks anything on its own. The framework refuses paths at the resolution
    /// boundary, not at the display boundary, and a display-layer refusal would be both
    /// the wrong place and easy to route around.
    /// </para>
    /// </remarks>
    public bool HasReplacedCharacters { get; }

    /// <summary>
    /// Whether <see cref="Label"/> is longer than the requested budget anyway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The final two components are never elided, so a location whose last two components
    /// alone exceed the budget produces a label that overruns it. The invariant wins:
    /// losing the parent directory loses the user's only way to tell
    /// <c>Profile1/save.dat</c> from <c>Profile2/save.dat</c>, and that is a worse
    /// outcome than a wide label.
    /// </para>
    /// <para>
    /// <b>A surface that sees this must wrap or scroll, not trim.</b> Applying
    /// <c>TextTrimming</c> to an overrunning label clips the tail, which is exactly the
    /// end-truncation the formatter refuses to do -- it would hide the filename that is
    /// about to be overwritten.
    /// </para>
    /// </remarks>
    public bool ExceedsMaxLength { get; }

    /// <summary>Whether there is nothing to show.</summary>
    public bool IsEmpty => Label.Length == 0;

    /// <summary>Returns <see cref="Label"/>, so a naive binding still shows the safe form.</summary>
    /// <returns>The truncated display text.</returns>
    /// <remarks>
    /// Returning the label rather than a type name keeps an unadorned
    /// <c>{Binding CurrentPath}</c> safe by default. The isolate wrapping still applies,
    /// so this value is no more usable as a path than <see cref="Label"/> is.
    /// </remarks>
    public override string ToString() => Label;
}

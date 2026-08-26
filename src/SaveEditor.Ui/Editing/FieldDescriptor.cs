namespace SaveEditor.Ui.Editing;

/// <summary>Common metadata for every editable field.</summary>
/// <remarks>
/// Descriptors are data supplied by the consuming editor. The framework owns
/// presentation, validation plumbing, pending-draft tracking, and history; the
/// editor owns what a field means and where it lives in the document.
/// </remarks>
public abstract record FieldDescriptor
{
    /// <summary>Stable identifier, unique within a section.</summary>
    public required string Key { get; init; }

    /// <summary>Display label.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Where the value lives in the document, shown to orient the user.
    /// </summary>
    /// <remarks>
    /// Displayed, never used to locate anything. Reading and writing go through the
    /// descriptor's own accessors, so a path shown for orientation cannot become a
    /// second, divergent way of addressing the document.
    /// </remarks>
    public string? Path { get; init; }

    /// <summary>Explanatory text shown beneath the field.</summary>
    public string? HelpText { get; init; }

    /// <summary>
    /// Caution shown alongside the field, for values that are editable but risky.
    /// </summary>
    public string? WarningText { get; init; }

    /// <summary>Whether the value is displayed without editing affordances.</summary>
    public bool IsReadOnly { get; init; }
}

/// <summary>A free-text field.</summary>
public sealed record TextFieldDescriptor : FieldDescriptor
{
    /// <summary>Reads the committed value from the document.</summary>
    public required Func<string> Read { get; init; }

    /// <summary>Writes a committed value into the document.</summary>
    public required Action<string> Write { get; init; }

    /// <summary>Returns an error message, or <see langword="null"/> when acceptable.</summary>
    public Func<string, string?>? Validate { get; init; }

    /// <summary>Maximum accepted length, or <see langword="null"/> for unbounded.</summary>
    public int? MaxLength { get; init; }
}

/// <summary>An integer field.</summary>
/// <remarks>
/// Parsing is invariant, deliberately. A save file's numbers are not localised, and
/// parsing "1,234" according to the user's culture would silently write a different
/// number on a machine configured differently from the author's.
/// </remarks>
public sealed record NumericFieldDescriptor : FieldDescriptor
{
    /// <summary>Reads the committed value from the document.</summary>
    public required Func<long> Read { get; init; }

    /// <summary>Writes a committed value into the document.</summary>
    public required Action<long> Write { get; init; }

    /// <summary>Smallest accepted value.</summary>
    public long Minimum { get; init; } = long.MinValue;

    /// <summary>Largest accepted value.</summary>
    public long Maximum { get; init; } = long.MaxValue;

    /// <summary>Whether to offer increment and decrement affordances.</summary>
    public bool ShowSpinner { get; init; }

    /// <summary>Additional validation beyond the range.</summary>
    public Func<long, string?>? Validate { get; init; }
}

/// <summary>A boolean field.</summary>
public sealed record BooleanFieldDescriptor : FieldDescriptor
{
    /// <summary>Reads the committed value from the document.</summary>
    public required Func<bool> Read { get; init; }

    /// <summary>Writes a committed value into the document.</summary>
    public required Action<bool> Write { get; init; }
}

/// <summary>One selectable option.</summary>
/// <param name="Value">The stored value.</param>
/// <param name="Label">How it is shown.</param>
public sealed record ChoiceOption(string Value, string Label);

/// <summary>Supplies options for a choice field.</summary>
/// <remarks>
/// Asynchronous because an editor may resolve options from the document, a lookup
/// table, or a file it has to read. The framework never blocks the UI thread on it.
/// </remarks>
public interface IChoiceProvider
{
    /// <summary>Gets the options matching a filter.</summary>
    /// <param name="filter">User-typed filter, or empty for all options.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>Matching options.</returns>
    ValueTask<IReadOnlyList<ChoiceOption>> GetOptionsAsync(
        string filter,
        CancellationToken cancellationToken = default);
}

/// <summary>A field selected from a set of options.</summary>
public sealed record ChoiceFieldDescriptor : FieldDescriptor
{
    /// <summary>Reads the committed value from the document.</summary>
    public required Func<string> Read { get; init; }

    /// <summary>Writes a committed value into the document.</summary>
    public required Action<string> Write { get; init; }

    /// <summary>Where the options come from.</summary>
    public required IChoiceProvider Options { get; init; }

    /// <summary>Whether a value outside the offered options is accepted.</summary>
    public bool AllowCustomValue { get; init; }
}

/// <summary>A value shown but never edited.</summary>
public sealed record ReadOnlyFieldDescriptor : FieldDescriptor
{
    /// <summary>Reads the displayed value.</summary>
    public required Func<string> Read { get; init; }
}

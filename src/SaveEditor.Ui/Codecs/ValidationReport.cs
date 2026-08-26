using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.Codecs;

/// <summary>How seriously the framework treats a validation message.</summary>
public enum ValidationSeverity
{
    /// <summary>The document is writable, but the user must accept the message first.</summary>
    Warning,

    /// <summary>The document must not be written.</summary>
    Error,
}

/// <summary>A single validation finding produced by a codec.</summary>
/// <param name="Severity">Whether this blocks the write or merely requires acceptance.</param>
/// <param name="Text">
/// Human-readable description. Untrusted: it derives from save-file contents.
/// </param>
/// <param name="FieldPath">Optional path of the offending field, for navigation.</param>
public sealed record ValidationMessage(
    ValidationSeverity Severity,
    UntrustedText Text,
    string? FieldPath = null);

/// <summary>The result of validating a document.</summary>
public sealed record ValidationReport
{
    /// <summary>A report with no findings.</summary>
    public static ValidationReport Empty { get; } = new() { Messages = [] };

    /// <summary>All findings, in the codec's own order.</summary>
    /// <remarks>
    /// Presentation sorts by severity before truncating. Showing the first eight
    /// rather than the most severe eight lets a codec emitting thousands of trivial
    /// warnings bury the one that mattered.
    /// </remarks>
    public required IReadOnlyList<ValidationMessage> Messages { get; init; }

    /// <summary>Whether any message blocks the write.</summary>
    public bool HasErrors => Messages.Any(m => m.Severity == ValidationSeverity.Error);
}

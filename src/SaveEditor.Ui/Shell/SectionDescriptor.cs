using Avalonia.Controls.Templates;

namespace SaveEditor.Ui.Shell;

/// <summary>How a section's body is rendered.</summary>
public enum SectionBodyMode
{
    /// <summary>A virtualised list of typed field cards. The common case.</summary>
    FieldList,

    /// <summary>Values presented without editing affordances.</summary>
    ReadOnly,

    /// <summary>An editor-supplied template.</summary>
    Custom,
}

/// <summary>
/// Declares one navigable section of an editor.
/// </summary>
/// <remarks>
/// <para>
/// Sections are data, not subclasses. An editor registers descriptors and the
/// framework owns navigation, selection, keyboard shortcuts, and the sidebar, so
/// adding a section never means reimplementing shell behaviour.
/// </para>
/// <para>
/// <see cref="Key"/> is stable and persisted as the last-selected section, so it
/// must not be a display string — renaming a section in the UI would otherwise
/// silently reset every user's saved position.
/// </para>
/// </remarks>
public sealed record SectionDescriptor
{
    /// <summary>Stable identifier, persisted and used for shortcuts. Never a display string.</summary>
    public required string Key { get; init; }

    /// <summary>Display title.</summary>
    public required string Title { get; init; }

    /// <summary>Optional one-line description shown beneath the title.</summary>
    public string? Subtitle { get; init; }

    /// <summary>
    /// Whether this section currently applies, or <see langword="null"/> for always.
    /// </summary>
    /// <remarks>
    /// Evaluated when the document changes. A section that does not apply to the
    /// loaded save is hidden rather than shown empty, because an empty section reads
    /// as data loss to somebody editing their own save file.
    /// </remarks>
    public Func<bool>? IsVisible { get; init; }

    /// <summary>Optional icon, interpreted by the consuming editor's templates.</summary>
    public object? Icon { get; init; }

    /// <summary>How the body is rendered.</summary>
    public SectionBodyMode BodyMode { get; init; } = SectionBodyMode.FieldList;

    /// <summary>
    /// Template for the body when <see cref="BodyMode"/> is
    /// <see cref="SectionBodyMode.Custom"/>.
    /// </summary>
    public IDataTemplate? BodyTemplate { get; init; }

    /// <summary>Content passed to the body template or field list.</summary>
    public object? Body { get; init; }

    /// <summary>Whether the section is currently visible.</summary>
    /// <returns><see langword="true"/> when it should appear in navigation.</returns>
    public bool EvaluateVisibility() => IsVisible?.Invoke() ?? true;
}

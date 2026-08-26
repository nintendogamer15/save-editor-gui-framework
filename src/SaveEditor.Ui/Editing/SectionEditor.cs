using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SaveEditor.Ui.Editing;

/// <summary>
/// The fields of one section, with their pending drafts and a transactional
/// Apply All.
/// </summary>
/// <remarks>
/// <para>
/// A section editor outlives navigation away from its section. That is the whole
/// point: pending drafts live here, so switching sections to check something and
/// coming back does not silently discard what was typed.
/// </para>
/// <para>
/// Apply All is one transaction, so twenty applied fields undo as one step. A user
/// who pressed one button expects one undo to reverse it.
/// </para>
/// </remarks>
public sealed partial class SectionEditor : ObservableObject
{
    private readonly EditHistory _history;

    /// <summary>Creates a section editor.</summary>
    /// <param name="key">Stable section key.</param>
    /// <param name="title">Display title.</param>
    /// <param name="fields">The section's fields, in display order.</param>
    /// <param name="history">Where committed edits are recorded.</param>
    public SectionEditor(
        string key,
        string title,
        IEnumerable<FieldViewModel> fields,
        EditHistory history)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(history);

        Key = key;
        Title = title;
        _history = history;

        Fields = [.. fields];
        VisibleFields = [.. Fields];

        foreach (var field in Fields)
        {
            field.PropertyChanged += OnFieldChanged;
        }
    }

    /// <summary>Stable section key.</summary>
    public string Key { get; }

    /// <summary>Display title.</summary>
    public string Title { get; }

    /// <summary>Every field, regardless of the current filter.</summary>
    public IReadOnlyList<FieldViewModel> Fields { get; }

    /// <summary>Fields matching the current filter, in display order.</summary>
    public ObservableCollection<FieldViewModel> VisibleFields { get; }

    /// <summary>How many fields have a draft differing from the document.</summary>
    public int PendingCount => Fields.Count(f => f.HasPendingEdit);

    /// <summary>Whether anything is typed but not applied.</summary>
    public bool HasPendingEdits => PendingCount > 0;

    /// <summary>How many pending fields currently fail validation.</summary>
    public int InvalidCount => Fields.Count(f => f.HasPendingEdit && !f.IsValid);

    /// <summary>Whether Apply All would commit anything.</summary>
    public bool CanApplyAll => Fields.Any(f => f.CanApply);

    /// <summary>
    /// Text filter applied to labels and paths.
    /// </summary>
    /// <remarks>
    /// Filtering never hides a field with a pending edit. Hiding one would let a user
    /// press Apply All and commit a value they cannot see, or navigate away believing
    /// nothing was outstanding.
    /// </remarks>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Commits every valid pending field as a single history entry.</summary>
    [RelayCommand]
    public void ApplyAll()
    {
        if (!CanApplyAll)
        {
            return;
        }

        using (_history.BeginTransaction($"Apply all in {Title}"))
        {
            foreach (var field in Fields.Where(f => f.CanApply).ToList())
            {
                field.Apply();
            }
        }

        NotifyState();
    }

    /// <summary>Discards every pending draft in the section.</summary>
    [RelayCommand]
    public void RevertAll()
    {
        foreach (var field in Fields)
        {
            field.Revert();
        }

        NotifyState();
    }

    /// <summary>Re-reads every field from the document, as after an undo.</summary>
    public void RefreshFromDocument()
    {
        foreach (var field in Fields)
        {
            field.RefreshFromDocument();
        }

        NotifyState();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter(value);

    private void ApplyFilter(string search)
    {
        var matches = Fields.Where(field =>
            field.HasPendingEdit
            || string.IsNullOrWhiteSpace(search)
            || field.Label.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (field.Path?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

        VisibleFields.Clear();
        foreach (var field in matches)
        {
            VisibleFields.Add(field);
        }
    }

    private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FieldViewModel.HasPendingEdit) or nameof(FieldViewModel.IsValid))
        {
            NotifyState();

            // A field that just became pending must reappear even under a filter that
            // excludes it, so nothing outstanding is ever invisible.
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                ApplyFilter(SearchText);
            }
        }
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPendingEdits));
        OnPropertyChanged(nameof(InvalidCount));
        OnPropertyChanged(nameof(CanApplyAll));
        ApplyAllCommand.NotifyCanExecuteChanged();
    }
}

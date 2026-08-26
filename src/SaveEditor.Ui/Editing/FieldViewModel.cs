using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SaveEditor.Ui.Editing;

/// <summary>
/// A field the user can edit, tracking a pending draft separately from the value
/// committed to the document.
/// </summary>
/// <remarks>
/// <para>
/// Typing changes the draft and nothing else. The document is untouched, the
/// document is not dirty, and no history entry exists until Apply. That separation
/// is what lets a draft survive section navigation without a half-typed value ever
/// reaching the save file.
/// </para>
/// <para>
/// Validation runs on the draft, so a field reports a problem while it is being
/// typed rather than at write time, when the user has moved on.
/// </para>
/// </remarks>
public abstract partial class FieldViewModel : ObservableObject
{
    private readonly EditHistory _history;

    /// <summary>Creates a field view-model.</summary>
    /// <param name="descriptor">The field's metadata.</param>
    /// <param name="history">Where committed edits are recorded.</param>
    protected FieldViewModel(FieldDescriptor descriptor, EditHistory history)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(history);

        Descriptor = descriptor;
        _history = history;
    }

    /// <summary>The field's metadata.</summary>
    public FieldDescriptor Descriptor { get; }

    /// <summary>Stable identifier.</summary>
    public string Key => Descriptor.Key;

    /// <summary>Display label.</summary>
    public string Label => Descriptor.Label;

    /// <summary>Document path, for orientation only.</summary>
    public string? Path => Descriptor.Path;

    /// <summary>Explanatory text.</summary>
    public string? HelpText => Descriptor.HelpText;

    /// <summary>Caution text.</summary>
    public string? WarningText => Descriptor.WarningText;

    /// <summary>Whether this field can be edited.</summary>
    public bool IsReadOnly => Descriptor.IsReadOnly;

    /// <summary>Whether the draft differs from the committed value.</summary>
    public abstract bool HasPendingEdit { get; }

    /// <summary>Current validation error, or <see langword="null"/>.</summary>
    [ObservableProperty]
    public partial string? ValidationError { get; protected set; }

    /// <summary>Whether the draft is acceptable.</summary>
    public bool IsValid => ValidationError is null;

    /// <summary>Whether Apply would do anything.</summary>
    public bool CanApply => HasPendingEdit && IsValid && !IsReadOnly;

    /// <summary>Commits the draft to the document and records one history entry.</summary>
    [RelayCommand]
    public void Apply()
    {
        if (!CanApply)
        {
            return;
        }

        var entry = CommitDraft();
        _history.Record(entry);
        NotifyEditState();
    }

    /// <summary>Discards the draft, returning to the committed value.</summary>
    [RelayCommand]
    public void Revert()
    {
        RevertDraft();
        NotifyEditState();
    }

    /// <summary>Re-reads the committed value, as after an undo.</summary>
    public abstract void RefreshFromDocument();

    /// <summary>
    /// Writes the draft into the document and returns the reversal for the history.
    /// </summary>
    /// <returns>The recorded operation.</returns>
    protected abstract HistoryEntry CommitDraft();

    /// <summary>Resets the draft to the committed value.</summary>
    protected abstract void RevertDraft();

    /// <summary>Raises change notification for the edit-state properties.</summary>
    protected void NotifyEditState()
    {
        OnPropertyChanged(nameof(HasPendingEdit));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>A field over a value of a specific type.</summary>
/// <typeparam name="T">The committed value type.</typeparam>
public abstract partial class FieldViewModel<T> : FieldViewModel
{
    private readonly Func<T> _read;
    private readonly Action<T> _write;
    private T _draft;

    /// <summary>Creates a typed field view-model.</summary>
    /// <param name="descriptor">The field's metadata.</param>
    /// <param name="history">Where committed edits are recorded.</param>
    /// <param name="read">Reads the committed value.</param>
    /// <param name="write">Writes a committed value.</param>
    protected FieldViewModel(
        FieldDescriptor descriptor,
        EditHistory history,
        Func<T> read,
        Action<T> write)
        : base(descriptor, history)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        _read = read;
        _write = write;
        _draft = read();
    }

    /// <summary>The value currently in the document.</summary>
    public T Committed => _read();

    /// <summary>The value being typed, not yet committed.</summary>
    public T Draft
    {
        get => _draft;
        set
        {
            if (EqualityComparer<T>.Default.Equals(_draft, value))
            {
                return;
            }

            _draft = value;
            ValidationError = ValidateDraft(value);

            OnPropertyChanged();
            NotifyEditState();
        }
    }

    /// <inheritdoc />
    public override bool HasPendingEdit => !EqualityComparer<T>.Default.Equals(_draft, Committed);

    /// <inheritdoc />
    public override void RefreshFromDocument()
    {
        _draft = _read();
        ValidationError = ValidateDraft(_draft);

        OnPropertyChanged(nameof(Draft));
        OnPropertyChanged(nameof(Committed));
        NotifyEditState();
    }

    /// <summary>Validates a candidate draft.</summary>
    /// <param name="value">The candidate.</param>
    /// <returns>An error message, or <see langword="null"/>.</returns>
    protected abstract string? ValidateDraft(T value);

    /// <inheritdoc />
    protected override HistoryEntry CommitDraft()
    {
        var before = _read();
        var after = _draft;

        _write(after);
        OnPropertyChanged(nameof(Committed));

        return new HistoryEntry(
            $"Change {Label}",
            () =>
            {
                _write(before);
                RefreshFromDocument();
            },
            () =>
            {
                _write(after);
                RefreshFromDocument();
            });
    }

    /// <inheritdoc />
    protected override void RevertDraft()
    {
        _draft = _read();
        ValidationError = null;
        OnPropertyChanged(nameof(Draft));
    }
}

/// <summary>A free-text field.</summary>
public sealed class TextFieldViewModel : FieldViewModel<string>
{
    private readonly TextFieldDescriptor _descriptor;

    /// <summary>Creates a text field.</summary>
    /// <param name="descriptor">The field's metadata.</param>
    /// <param name="history">Where committed edits are recorded.</param>
    public TextFieldViewModel(TextFieldDescriptor descriptor, EditHistory history)
        : base(descriptor, history, descriptor.Read, descriptor.Write) =>
        _descriptor = descriptor;

    /// <inheritdoc />
    protected override string? ValidateDraft(string value)
    {
        if (_descriptor.MaxLength is { } max && value.Length > max)
        {
            return $"Must be {max} characters or fewer.";
        }

        return _descriptor.Validate?.Invoke(value);
    }
}

/// <summary>A boolean field.</summary>
public sealed class BooleanFieldViewModel(BooleanFieldDescriptor descriptor, EditHistory history)
    : FieldViewModel<bool>(descriptor, history, descriptor.Read, descriptor.Write)
{
    /// <inheritdoc />
    protected override string? ValidateDraft(bool value) => null;
}

/// <summary>
/// An integer field edited as text, so an unparseable draft is reported rather than
/// silently coerced.
/// </summary>
/// <remarks>
/// The draft is the string the user typed. Binding a numeric control directly would
/// make "abc" indistinguishable from zero, and a save file where a stat silently
/// became zero is worse than one that refused to apply.
/// </remarks>
public sealed partial class NumericFieldViewModel : FieldViewModel<long>
{
    private readonly NumericFieldDescriptor _descriptor;

    /// <summary>Creates a numeric field.</summary>
    /// <param name="descriptor">The field's metadata.</param>
    /// <param name="history">Where committed edits are recorded.</param>
    public NumericFieldViewModel(NumericFieldDescriptor descriptor, EditHistory history)
        : base(descriptor, history, descriptor.Read, descriptor.Write)
    {
        _descriptor = descriptor;
        _text = Draft.ToString(CultureInfo.InvariantCulture);
    }

    private string _text;

    /// <summary>Whether to offer increment and decrement affordances.</summary>
    public bool ShowSpinner => _descriptor.ShowSpinner;

    /// <summary>
    /// Whether the typed text differs from the committed value.
    /// </summary>
    /// <remarks>
    /// Compares the <em>text</em>, not the parsed number, because an unparseable
    /// draft never updates the parsed value. Comparing the number would report no
    /// pending edit for a field the user has typed garbage into — and since the exit
    /// guard is driven by pending state, closing the editor would then discard that
    /// text without asking.
    /// </remarks>
    public override bool HasPendingEdit =>
        !string.Equals(_text, Committed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>The text being typed.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (string.Equals(_text, value, StringComparison.Ordinal))
            {
                return;
            }

            _text = value;
            OnPropertyChanged();

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                Draft = parsed;
                ValidationError = ValidateDraft(parsed);
            }
            else
            {
                ValidationError = string.IsNullOrWhiteSpace(value)
                    ? "Enter a whole number."
                    : $"'{value}' is not a whole number.";
            }

            NotifyEditState();
        }
    }

    /// <inheritdoc />
    public override void RefreshFromDocument()
    {
        base.RefreshFromDocument();
        _text = Draft.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(Text));
    }

    /// <summary>Increases the draft by one, stopping at the maximum.</summary>
    [RelayCommand]
    public void Increment() => Step(1);

    /// <summary>Decreases the draft by one, stopping at the minimum.</summary>
    [RelayCommand]
    public void Decrement() => Step(-1);

    /// <summary>Adjusts the draft, clamping to the descriptor's range.</summary>
    /// <param name="delta">How much to add.</param>
    /// <remarks>
    /// Clamping is right here and wrong for typing. Pressing increment means "one
    /// more", so stopping at the bound is what the user asked for. Typing a number
    /// past the bound is a different statement, and silently clamping it would write
    /// a value they never chose — so that path reports an error instead.
    /// </remarks>
    private void Step(long delta)
    {
        var current = long.TryParse(_text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : Committed;

        var next = Math.Clamp(
            current + delta,
            _descriptor.Minimum,
            _descriptor.Maximum);

        Text = next.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    protected override string? ValidateDraft(long value)
    {
        if (value < _descriptor.Minimum || value > _descriptor.Maximum)
        {
            return $"Must be between {_descriptor.Minimum} and {_descriptor.Maximum}.";
        }

        return _descriptor.Validate?.Invoke(value);
    }
}

/// <summary>A field selected from a set of options.</summary>
public sealed class ChoiceFieldViewModel(ChoiceFieldDescriptor descriptor, EditHistory history)
    : FieldViewModel<string>(descriptor, history, descriptor.Read, descriptor.Write)
{
    /// <summary>Where the options come from.</summary>
    public IChoiceProvider Options => descriptor.Options;

    /// <summary>Whether a value outside the offered options is accepted.</summary>
    public bool AllowCustomValue => descriptor.AllowCustomValue;

    /// <inheritdoc />
    protected override string? ValidateDraft(string value) => null;
}

/// <summary>A value shown but never edited.</summary>
public sealed class ReadOnlyFieldViewModel(ReadOnlyFieldDescriptor descriptor, EditHistory history)
    : FieldViewModel(descriptor, history)
{
    /// <summary>The displayed value.</summary>
    public string Value => descriptor.Read();

    /// <inheritdoc />
    public override bool HasPendingEdit => false;

    /// <inheritdoc />
    public override void RefreshFromDocument() => OnPropertyChanged(nameof(Value));

    /// <inheritdoc />
    protected override HistoryEntry CommitDraft() =>
        throw new InvalidOperationException("A read-only field has nothing to commit.");

    /// <inheritdoc />
    protected override void RevertDraft()
    {
        // Nothing to revert.
    }
}

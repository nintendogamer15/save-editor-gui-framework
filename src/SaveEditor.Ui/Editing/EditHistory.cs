namespace SaveEditor.Ui.Editing;

/// <summary>One reversible committed operation.</summary>
/// <param name="Label">Human-readable description, shown in status text.</param>
/// <param name="Undo">Reverts the operation.</param>
/// <param name="Redo">Reapplies it.</param>
public sealed record HistoryEntry(string Label, Action Undo, Action Redo);

/// <summary>
/// The framework's default <see cref="IEditHistory"/>: undo/redo over committed edits, with
/// revision-based dirty tracking.
/// </summary>
/// <remarks>
/// <para>
/// Only <em>committed</em> edits reach the history. Typing produces a pending draft
/// that lives on the field and is never recorded, so undo never walks back through
/// half-typed values — a user who typed four characters and pressed Apply expects
/// one undo, not four.
/// </para>
/// <para>
/// Dirty state is a revision comparison rather than a boolean, so undoing back to
/// the saved point correctly reports clean. A boolean flag cannot: it is set on the
/// first edit and has no way to learn that the document returned to what is on disk.
/// </para>
/// <para>
/// History is capped and never persisted. An unbounded history on a large save is a
/// memory leak with a friendly name, and persisting it would mean restoring a
/// document into a state that disagrees with the file it came from.
/// </para>
/// </remarks>
public sealed class EditHistory : IEditHistory
{
    /// <summary>Committed operations retained by default.</summary>
    public const int DefaultCapacity = 1000;

    private readonly List<HistoryEntry> _entries = [];
    private readonly int _capacity;
    private Transaction? _transaction;
    private int _cursor;
    private long _revision;
    private long _savedRevision;

    /// <summary>Creates a history.</summary>
    /// <param name="capacity">Committed operations to retain. Must be positive.</param>
    public EditHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>Raised whenever undo/redo availability or dirty state may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether an undo is available.</summary>
    public bool CanUndo => _cursor > 0;

    /// <summary>Whether a redo is available.</summary>
    public bool CanRedo => _cursor < _entries.Count;

    /// <summary>Committed operations currently retained.</summary>
    public int Count => _entries.Count;

    /// <summary>Whether the document differs from the last saved state.</summary>
    public bool IsDirty => _revision != _savedRevision;

    /// <summary>Label of the operation undo would revert, or <see langword="null"/>.</summary>
    public string? UndoLabel => CanUndo ? _entries[_cursor - 1].Label : null;

    /// <summary>Label of the operation redo would reapply, or <see langword="null"/>.</summary>
    public string? RedoLabel => CanRedo ? _entries[_cursor].Label : null;

    /// <summary>Records a committed operation.</summary>
    /// <param name="entry">The operation, with its undo and redo actions.</param>
    /// <remarks>
    /// Inside a transaction the operation is folded into it instead, so Apply All
    /// over twenty fields produces one undo step rather than twenty.
    /// </remarks>
    public void Record(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_transaction is not null)
        {
            _transaction.Add(entry);
            return;
        }

        // A new edit after undoing discards the redo tail; keeping it would let
        // undo and redo describe two different documents.
        if (_cursor < _entries.Count)
        {
            _entries.RemoveRange(_cursor, _entries.Count - _cursor);

            // The saved point is now unreachable, so nothing can return to clean.
            if (_savedRevision > _revision)
            {
                _savedRevision = -1;
            }
        }

        _entries.Add(entry);
        _cursor++;
        _revision++;

        TrimToCapacity();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Begins a transaction; every operation recorded until it is disposed collapses
    /// into a single history entry.
    /// </summary>
    /// <param name="label">Label for the combined entry.</param>
    /// <returns>A scope that commits the transaction when disposed.</returns>
    /// <remarks>
    /// Disposing with nothing recorded records nothing, so an Apply All that changed
    /// no fields does not leave an undo step that undoes nothing.
    /// </remarks>
    public IDisposable BeginTransaction(string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);

        if (_transaction is not null)
        {
            throw new InvalidOperationException("An edit transaction is already open.");
        }

        _transaction = new Transaction(this, label);
        return _transaction;
    }

    /// <summary>Reverts the most recent operation.</summary>
    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }

        _cursor--;
        _entries[_cursor].Undo();
        _revision--;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reapplies the most recently undone operation.</summary>
    public void Redo()
    {
        if (!CanRedo)
        {
            return;
        }

        _entries[_cursor].Redo();
        _cursor++;
        _revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks the current state as matching what is on disk.</summary>
    public void MarkSaved()
    {
        _savedRevision = _revision;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Discards all history, as when a document is closed.</summary>
    public void Clear()
    {
        _entries.Clear();
        _cursor = 0;
        _revision = 0;
        _savedRevision = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TrimToCapacity()
    {
        if (_entries.Count <= _capacity)
        {
            return;
        }

        var excess = _entries.Count - _capacity;
        _entries.RemoveRange(0, excess);
        _cursor -= excess;

        // Dropping entries past the saved point makes clean unreachable: the
        // operations needed to get back there no longer exist.
        if (_savedRevision >= 0 && _revision - _savedRevision > _capacity)
        {
            _savedRevision = -1;
        }
    }

    private void CommitTransaction(Transaction transaction)
    {
        _transaction = null;

        if (transaction.Entries.Count == 0)
        {
            return;
        }

        var entries = transaction.Entries.ToArray();

        Record(new HistoryEntry(
            transaction.Label,
            () =>
            {
                // Reverse order: later edits may depend on earlier ones.
                for (var i = entries.Length - 1; i >= 0; i--)
                {
                    entries[i].Undo();
                }
            },
            () =>
            {
                foreach (var entry in entries)
                {
                    entry.Redo();
                }
            }));
    }

    private sealed class Transaction(EditHistory history, string label) : IDisposable
    {
        private bool _disposed;

        public string Label { get; } = label;

        public List<HistoryEntry> Entries { get; } = [];

        public void Add(HistoryEntry entry) => Entries.Add(entry);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            history.CommitTransaction(this);
        }
    }
}

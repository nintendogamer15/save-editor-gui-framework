namespace SaveEditor.Ui.Editing;

/// <summary>
/// Undo/redo and dirty tracking over committed edits, as the framework's editing surface
/// consumes it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EditHistory"/> is the default implementation and remains the right answer for
/// an application that has no history model of its own. This interface exists because
/// <see cref="FieldViewModel"/>, <see cref="SectionEditor"/> and
/// <see cref="Workflow.DocumentSession{TDocument}"/> previously demanded that exact concrete
/// type, which made <c>FieldCard</c>, <c>FieldList</c> and <c>SectionEditor</c> —
/// the highest-reuse pieces in the framework — all-or-nothing for anyone who already had
/// one. The choice was to abandon an existing undo stack or run two in parallel, and an
/// application whose model is a whole-tree snapshot rollback would have been dropping to
/// per-field undo purely because a class was sealed (finding F-9).
/// </para>
/// <para>
/// <strong>The members are exactly what the framework calls, and no more.</strong> Anything
/// else <see cref="EditHistory"/> offers — its capacity, its entry count, the labels it
/// exposes for menu text — is that implementation's own surface, so an adopter bridging to a
/// different model is not made to reproduce parts nothing depends on.
/// </para>
/// <para>
/// <strong>Only committed edits belong here.</strong> Typing produces a pending draft that
/// lives on the field and is never recorded, so undo does not walk back through half-typed
/// values. An implementation that records every keystroke will produce an editor that
/// behaves differently from every other editor built on this framework.
/// </para>
/// </remarks>
public interface IEditHistory
{
    /// <summary>Raised whenever undo/redo availability or dirty state may have changed.</summary>
    /// <remarks>
    /// The shell rebinds its menus and status text from this. An implementation that never
    /// raises it will leave Undo and Redo greyed out regardless of what it is holding.
    /// </remarks>
    event EventHandler? Changed;

    /// <summary>Whether an undo is available.</summary>
    bool CanUndo { get; }

    /// <summary>Whether a redo is available.</summary>
    bool CanRedo { get; }

    /// <summary>Whether the document differs from the last saved state.</summary>
    /// <remarks>
    /// A revision comparison rather than a latch: undoing back to the saved point has to
    /// report clean again. A boolean flag set on the first edit cannot, because it has no way
    /// to learn that the document returned to what is on disk — which is what makes the exit
    /// guard ask about work that is no longer outstanding.
    /// </remarks>
    bool IsDirty { get; }

    /// <summary>Records a committed operation.</summary>
    /// <param name="entry">The operation, with its undo and redo actions.</param>
    /// <remarks>
    /// Inside a transaction the operation is folded into it instead, so Apply All over twenty
    /// fields produces one undo step rather than twenty.
    /// </remarks>
    void Record(HistoryEntry entry);

    /// <summary>
    /// Begins a transaction; every operation recorded until it is disposed collapses into a
    /// single history entry.
    /// </summary>
    /// <param name="label">Label for the combined entry.</param>
    /// <returns>A scope that commits the transaction when disposed.</returns>
    /// <remarks>
    /// Disposing with nothing recorded must record nothing, so an Apply All that changed no
    /// fields does not leave an undo step that undoes nothing.
    /// </remarks>
    IDisposable BeginTransaction(string label);

    /// <summary>Reverts the most recent operation.</summary>
    /// <remarks>Does nothing when <see cref="CanUndo"/> is <see langword="false"/>.</remarks>
    void Undo();

    /// <summary>Reapplies the most recently undone operation.</summary>
    /// <remarks>Does nothing when <see cref="CanRedo"/> is <see langword="false"/>.</remarks>
    void Redo();

    /// <summary>Marks the current state as matching what is on disk.</summary>
    void MarkSaved();

    /// <summary>Discards all history, as when a document is closed.</summary>
    void Clear();
}

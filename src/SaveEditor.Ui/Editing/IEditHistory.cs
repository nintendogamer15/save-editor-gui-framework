namespace SaveEditor.Ui.Editing;

/// <summary>
/// A batch of committed edits that becomes one history entry, or none at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Disposing without committing aborts.</strong> The scope returned by
/// <see cref="IEditHistory.BeginTransaction"/> used to be a bare
/// <see cref="IDisposable"/> that committed on disposal, so an exception escaping the
/// <c>using</c> committed whatever had been recorded before it — an Apply All over twenty
/// fields that failed on the third left two of them written to the document. The user
/// asked for one operation and got a fraction of one (finding F-18).
/// </para>
/// <para>
/// The default is therefore the safe one: work nobody confirmed is rolled back. Committing
/// is the deliberate act, and it has to be reached.
/// </para>
/// </remarks>
public interface IEditTransaction : IDisposable
{
    /// <summary>Folds everything recorded in this scope into a single history entry.</summary>
    /// <remarks>
    /// Committing nothing records nothing, so an Apply All that changed no fields does not
    /// leave an undo step that undoes nothing. Committing after
    /// <see cref="Abort"/> does nothing: an aborted batch cannot be resurrected.
    /// </remarks>
    void Commit();

    /// <summary>Undoes everything recorded in this scope and records nothing.</summary>
    /// <remarks>
    /// <para>
    /// Implemented by replaying the recorded entries' undo actions in reverse, so it needs no
    /// snapshot support and is equally correct for a history backed by whole-document
    /// snapshots.
    /// </para>
    /// <para>
    /// <strong>Best-effort when an undo action itself throws.</strong> A failing undo is a
    /// fault in the application's own model, and stopping at the first one would restore less
    /// of the document than continuing, so the remaining entries are still replayed. An
    /// implementation should not let one bad entry abandon the rest.
    /// </para>
    /// </remarks>
    void Abort();
}

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
    /// <returns>The scope. Disposing it without committing aborts.</returns>
    /// <remarks>
    /// <para>
    /// Committing with nothing recorded records nothing, so an Apply All that changed no
    /// fields does not leave an undo step that undoes nothing.
    /// </para>
    /// <para>
    /// An implementation must reset its own open-transaction state on <em>both</em> exits.
    /// Clearing it only on commit means the first aborted batch leaves the history believing
    /// a transaction is still open, and every later one fails to start.
    /// </para>
    /// </remarks>
    IEditTransaction BeginTransaction(string label);

    /// <summary>Reverts the most recent operation.</summary>
    /// <remarks>
    /// Does nothing when <see cref="CanUndo"/> is <see langword="false"/>. An undo action that
    /// throws propagates — there is no field to report it on, and an undo that cannot be
    /// performed is a fault rather than a validation result — but it must leave the history
    /// exactly as it was, so that a failed undo is not silently counted as a successful one.
    /// </remarks>
    void Undo();

    /// <summary>Reapplies the most recently undone operation.</summary>
    /// <remarks>Does nothing when <see cref="CanRedo"/> is <see langword="false"/>.</remarks>
    void Redo();

    /// <summary>Marks the current state as matching what is on disk.</summary>
    void MarkSaved();

    /// <summary>Discards all history, as when a document is closed.</summary>
    void Clear();
}

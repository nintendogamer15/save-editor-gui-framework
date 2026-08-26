using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.Tests.Editing;

/// <summary>
/// Apply All is one operation or none, and a write that is rejected says so instead of
/// crashing.
/// </summary>
/// <remarks>
/// The transaction scope used to commit on disposal, so an exception escaping the loop
/// committed whatever had been recorded before it: a batch that failed on the third field left
/// the first two written to the document (finding F-18). And <c>Apply</c> had no <c>try</c> at
/// all, so a model that rejects a write by throwing — the only way to express a cross-field
/// constraint, which <c>Validate</c> cannot see — surfaced as an unhandled exception out of a
/// bound command (finding F-19).
/// </remarks>
public sealed class EditTransactionTests
{
    private sealed class Document
    {
        public long First { get; set; } = 1;

        public long Second { get; set; } = 2;

        public long Third { get; set; } = 3;
    }

    /// <summary>Three numeric fields whose writes can be made to reject.</summary>
    private sealed class Harness
    {
        internal Document Doc { get; } = new();

        internal EditHistory History { get; } = new();

        internal SectionEditor Section { get; private set; } = null!;

        /// <summary>Set to make that field's write throw.</summary>
        internal string? FirstRejects { get; set; }

        internal string? ThirdRejects { get; set; }

        /// <summary>Runs after First's write, so a test can disturb another field.</summary>
        internal Action? AfterFirstWrite { get; set; }

        internal static Harness Build()
        {
            var h = new Harness();

            var first = Field("first", () => h.Doc.First, v =>
            {
                if (h.FirstRejects is { } message)
                {
                    throw new InvalidOperationException(message);
                }

                h.Doc.First = v;
                h.AfterFirstWrite?.Invoke();
            }, h.History);

            var second = Field("second", () => h.Doc.Second, v => h.Doc.Second = v, h.History);

            var third = Field("third", () => h.Doc.Third, v =>
            {
                if (h.ThirdRejects is { } message)
                {
                    throw new InvalidOperationException(message);
                }

                h.Doc.Third = v;
            }, h.History);

            h.Section = new SectionEditor("main", "Main", [first, second, third], h.History);
            return h;
        }

        private static NumericFieldViewModel Field(string key, Func<long> read, Action<long> write, IEditHistory history) =>
            new(
                new NumericFieldDescriptor
                {
                    Key = key,
                    Label = key,
                    Minimum = 0,
                    Maximum = 9999,
                    Read = read,
                    Write = write,
                },
                history);

        internal NumericFieldViewModel Get(string key) =>
            (NumericFieldViewModel)Section.Fields.Single(f => f.Key == key);

        /// <summary>Types a draft into a field. Numeric fields are driven through Text.</summary>
        internal void Type(string key, long value) =>
            Get(key).Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ F-18

    [Fact]
    public void ApplyAll_RollsTheWholeBatchBackWhenOneWriteIsRejected()
    {
        var h = Harness.Build();

        h.Type("first", 11);
        h.Type("second", 22);
        h.Type("third", 33);
        h.ThirdRejects = "the third field says no";

        // Does not throw: the rejection is reported, not propagated out of the command.
        h.Section.ApplyAllCommand.Execute(null);

        // (a) The document is exactly what it was.
        Assert.Equal(1, h.Doc.First);
        Assert.Equal(2, h.Doc.Second);
        Assert.Equal(3, h.Doc.Third);

        // (b) Nothing was recorded, so there is nothing to undo.
        Assert.False(h.History.CanUndo);
        Assert.Equal(0, h.History.Count);

        // (c) Every field reads its pre-ApplyAll committed value.
        Assert.Equal(1, h.Get("first").Committed);
        Assert.Equal(2, h.Get("second").Committed);
        Assert.Equal(3, h.Get("third").Committed);

        // The field that was rejected keeps its draft and says why.
        Assert.Equal("33", h.Get("third").Text);
        Assert.Equal("the third field says no", h.Get("third").ValidationError);
    }

    [Fact]
    public void ApplyAll_WithNoRejectionCommitsExactlyOneEntry()
    {
        var h = Harness.Build();

        h.Type("first", 11);
        h.Type("second", 22);
        h.Type("third", 33);

        h.Section.ApplyAllCommand.Execute(null);

        Assert.Equal(11, h.Doc.First);
        Assert.Equal(22, h.Doc.Second);
        Assert.Equal(33, h.Doc.Third);
        Assert.Equal(1, h.History.Count);

        // One button press, one undo, and it reverses all of it.
        h.History.Undo();

        Assert.Equal(1, h.Doc.First);
        Assert.Equal(2, h.Doc.Second);
        Assert.Equal(3, h.Doc.Third);
    }

    /// <summary>
    /// The first failed batch must not poison the history. Abort has to clear the
    /// open-transaction state, not just discard the entries.
    /// </summary>
    [Fact]
    public void ApplyAll_WorksAgainAfterAnAbortedBatch()
    {
        var h = Harness.Build();

        h.Type("first", 11);
        h.Type("third", 33);
        h.ThirdRejects = "no";

        h.Section.ApplyAllCommand.Execute(null);
        Assert.False(h.History.CanUndo);

        // The rollback re-read First from the document, so its draft is gone. That is the
        // documented cost of undoing through the recorded entries rather than a snapshot: the
        // field that was rejected keeps its draft, the ones that were rolled back do not.
        Assert.False(h.Get("first").HasPendingEdit);
        Assert.True(h.Get("third").HasPendingEdit);

        // Fix the cause and try again. Before Abort cleared _transaction this threw
        // "An edit transaction is already open." and never recovered.
        //
        // Both fields have to be typed again, for different reasons: First lost its draft to
        // the rollback, and Third still carries the rejection message, which makes it invalid
        // and so not applicable until its draft moves. Both are documented consequences rather
        // than accidents -- see Apply_NeedsItsDraftTouchedAgainAfterARejection below.
        h.ThirdRejects = null;
        h.Type("first", 11);
        h.Type("third", 44);

        h.Section.ApplyAllCommand.Execute(null);

        Assert.Equal(11, h.Doc.First);
        Assert.Equal(44, h.Doc.Third);
        Assert.Equal(1, h.History.Count);
        Assert.True(h.History.CanUndo);
    }

    /// <summary>
    /// A rejected field carries its message as a validation error, which makes it invalid and
    /// therefore not applicable until its draft changes.
    /// </summary>
    /// <remarks>
    /// A known cost of routing rejection through the existing <c>ValidationError</c> channel,
    /// which is what the brief asked for. It matters for a cross-field rejection, where the
    /// user fixes a <em>different</em> field and then wants to retry this one: they have to
    /// touch its value first. Separating rejection from validation would need an observable of
    /// its own plus card, theme and headless coverage.
    /// </remarks>
    [Fact]
    public void Apply_NeedsItsDraftTouchedAgainAfterARejection()
    {
        var h = Harness.Build();

        h.Type("third", 33);
        h.ThirdRejects = "not while the other field says otherwise";
        h.Get("third").ApplyCommand.Execute(null);

        Assert.NotNull(h.Get("third").ValidationError);
        Assert.False(h.Get("third").CanApply);

        // The cause is gone, but the field is still holding the message, so a retry with the
        // same draft does nothing.
        h.ThirdRejects = null;
        h.Get("third").ApplyCommand.Execute(null);
        Assert.Equal(3, h.Doc.Third);

        // Touching the draft clears it and the retry lands.
        h.Type("third", 44);
        Assert.Null(h.Get("third").ValidationError);

        h.Get("third").ApplyCommand.Execute(null);
        Assert.Equal(44, h.Doc.Third);
    }

    /// <summary>
    /// A field that an earlier write leaves with nothing to do is skipped, not treated as a
    /// rejection.
    /// </summary>
    /// <remarks>
    /// <c>CanApply</c> is live over the document, so in a model with cross-field constraints an
    /// earlier field's write can legitimately settle a later one. Treating that as failure
    /// would roll the batch back with no message anywhere, because "nothing to do" sets no
    /// validation error.
    /// </remarks>
    [Fact]
    public void ApplyAll_SkipsAFieldAnEarlierWriteAlreadySettled()
    {
        var h = Harness.Build();

        h.Type("first", 11);
        h.Type("second", 22);

        // Applying First also writes what Second was going to write, so by the time the loop
        // reaches Second there is nothing left for it to do.
        h.AfterFirstWrite = () => h.Doc.Second = 22;

        h.Section.ApplyAllCommand.Execute(null);

        Assert.Equal(11, h.Doc.First);
        Assert.Equal(22, h.Doc.Second);

        // Committed, not rolled back, and recorded as one entry.
        Assert.Equal(1, h.History.Count);
        Assert.True(h.History.CanUndo);
        Assert.Null(h.Get("second").ValidationError);
    }

    // ------------------------------------------------------------------ F-19

    [Fact]
    public void Apply_ReportsARejectedWriteOnTheFieldInsteadOfThrowing()
    {
        var h = Harness.Build();

        h.Type("third", 33);
        h.ThirdRejects = "that value is not allowed here";

        var third = h.Get("third");

        // Nothing escapes the bound command.
        third.ApplyCommand.Execute(null);

        Assert.Equal(3, h.Doc.Third);
        Assert.Equal("33", third.Text);
        Assert.True(third.HasPendingEdit);
        Assert.Equal("that value is not allowed here", third.ValidationError);
        Assert.Equal(0, h.History.Count);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void TryApply_ReportsSuccessAndFailure()
    {
        var h = Harness.Build();

        h.Type("third", 33);
        Assert.True(h.Get("third").TryApply());
        Assert.Equal(33, h.Doc.Third);

        // Nothing pending is not success.
        Assert.False(h.Get("third").TryApply());

        h.Type("third", 44);
        h.ThirdRejects = "no";
        Assert.False(h.Get("third").TryApply());
        Assert.Equal(33, h.Doc.Third);
    }

    /// <summary>Cancellation is not a rejection of the value, so it is not reported as one.</summary>
    [Fact]
    public void Apply_LetsCancellationPropagate()
    {
        var doc = new Document();
        var history = new EditHistory();

        var field = new NumericFieldViewModel(
            new NumericFieldDescriptor
            {
                Key = "first",
                Label = "First",
                Minimum = 0,
                Maximum = 9999,
                Read = () => doc.First,
                Write = _ => throw new OperationCanceledException(),
            },
            history);

        field.Text = "11";

        Assert.Throws<OperationCanceledException>(() => field.TryApply());
        Assert.Null(field.ValidationError);
    }

    /// <summary>
    /// A write that throws while an undo replays leaves the history exactly as it was.
    /// </summary>
    /// <remarks>
    /// <c>Undo</c> used to move the cursor before running the action, so a throwing undo
    /// escaped with the cursor decremented and the revision not — the history then described a
    /// document that had not changed.
    /// </remarks>
    [Fact]
    public void Undo_LeavesTheHistoryUnchangedWhenTheWriteThrows()
    {
        var h = Harness.Build();

        h.Type("first", 11);
        h.Get("first").ApplyCommand.Execute(null);

        Assert.True(h.History.CanUndo);
        Assert.False(h.History.CanRedo);
        Assert.True(h.History.IsDirty);

        // The undo action writes the previous value back, and now that write is rejected.
        h.FirstRejects = "cannot go back";

        Assert.Throws<InvalidOperationException>(h.History.Undo);

        Assert.True(h.History.CanUndo);
        Assert.False(h.History.CanRedo);
        Assert.True(h.History.IsDirty);
        Assert.Equal(11, h.Doc.First);
    }

    // ------------------------------------------------- the transaction contract

    [Fact]
    public void Transaction_DisposingWithoutCommittingAborts()
    {
        var doc = new Document();
        var history = new EditHistory();

        using (history.BeginTransaction("batch"))
        {
            doc.First = 11;
            history.Record(Entry(doc, 1, 11));
        }

        // The write really happened and the abort really took it back.
        Assert.Equal(1, doc.First);
        Assert.False(history.CanUndo);
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Transaction_CommitAfterAbortDoesNothing()
    {
        var doc = new Document();
        var history = new EditHistory();

        var transaction = history.BeginTransaction("batch");
        doc.First = 11;
        history.Record(Entry(doc, 1, 11));
        transaction.Abort();

        // An aborted batch cannot be resurrected.
        transaction.Commit();

        Assert.Equal(1, doc.First);
        Assert.False(history.CanUndo);
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Transaction_AbortOnAnEmptyScopeRecordsNothingAndLeavesTheHistoryUsable()
    {
        var doc = new Document();
        var history = new EditHistory();

        using (var empty = history.BeginTransaction("nothing"))
        {
            empty.Abort();
        }

        using (var second = history.BeginTransaction("something"))
        {
            doc.First = 11;
            history.Record(Entry(doc, 1, 11));
            second.Commit();
        }

        Assert.Equal(11, doc.First);
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Transaction_AbortReplaysUndoInReverse()
    {
        var order = new List<string>();
        var history = new EditHistory();

        var transaction = history.BeginTransaction("batch");
        history.Record(new HistoryEntry("a", () => order.Add("a"), () => { }));
        history.Record(new HistoryEntry("b", () => order.Add("b"), () => { }));
        history.Record(new HistoryEntry("c", () => order.Add("c"), () => { }));
        transaction.Abort();

        // Later edits may depend on earlier ones, so they come apart in the reverse order.
        Assert.Equal(["c", "b", "a"], order);
    }

    /// <summary>An undo action that throws must not abandon the rest of the rollback.</summary>
    [Fact]
    public void Transaction_AbortKeepsGoingWhenOneUndoThrows()
    {
        var undone = new List<string>();
        var history = new EditHistory();

        var transaction = history.BeginTransaction("batch");
        history.Record(new HistoryEntry("a", () => undone.Add("a"), () => { }));
        history.Record(new HistoryEntry("b", () => throw new InvalidOperationException("bad undo"), () => { }));
        history.Record(new HistoryEntry("c", () => undone.Add("c"), () => { }));

        transaction.Abort();

        // "c" ran, "b" threw, and "a" still ran: stopping would have restored less.
        Assert.Equal(["c", "a"], undone);
        Assert.False(history.CanUndo);
    }

    private static HistoryEntry Entry(Document doc, long before, long after) =>
        new($"set {after}", () => doc.First = before, () => doc.First = after);
}

using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.Tests.Editing;

/// <summary>
/// The editing surface works against <see cref="IEditHistory"/>, not against
/// <see cref="EditHistory"/>.
/// </summary>
/// <remarks>
/// An application that already has an undo model — the adopter's is a whole-tree snapshot
/// rollback in its own core layer — previously had to abandon it or run two stacks in
/// parallel, because <c>FieldViewModel</c>, <c>SectionEditor</c> and <c>DocumentSession</c>
/// demanded the concrete class. That made the highest-reuse controls in the framework
/// all-or-nothing over a sealed type (finding F-9).
/// </remarks>
public sealed class CustomHistoryTests
{
    private sealed class Document
    {
        public long Health { get; set; } = 100;

        public string Name { get; set; } = "hero";
    }

    /// <summary>
    /// A history that is emphatically not <see cref="EditHistory"/>: it keeps a flat list and
    /// folds transactions by remembering where one opened.
    /// </summary>
    private sealed class SnapshotHistory : IEditHistory
    {
        private readonly List<HistoryEntry> _entries = [];
        private int _transactionDepth;
        private string? _transactionLabel;
        private int _transactionStart;
        private int _labelStart;
        private int _cursor;
        private int _savedCursor;

        public event EventHandler? Changed;

        public bool CanUndo => _cursor > 0;

        public bool CanRedo => _cursor < _entries.Count;

        public bool IsDirty => _cursor != _savedCursor;

        public List<string> Labels { get; } = [];

        public void Record(HistoryEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            _entries.Add(entry);
            _cursor = _entries.Count;
            Labels.Add(entry.Label);

            if (_transactionDepth == 0)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public IEditTransaction BeginTransaction(string label)
        {
            _transactionDepth++;
            _transactionLabel = label;
            _transactionStart = _entries.Count;
            _labelStart = Labels.Count;
            return new Scope(this);
        }

        public void Undo()
        {
            if (!CanUndo)
            {
                return;
            }

            _cursor--;
            _entries[_cursor].Undo();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Redo()
        {
            if (!CanRedo)
            {
                return;
            }

            _entries[_cursor].Redo();
            _cursor++;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void MarkSaved()
        {
            _savedCursor = _cursor;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            _entries.Clear();
            Labels.Clear();
            _cursor = 0;
            _savedCursor = 0;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Undoes everything recorded in this scope and records nothing.
        /// </summary>
        /// <remarks>
        /// Decrementing the depth is as load-bearing here as clearing the open-transaction
        /// field is in <c>EditHistory</c>: a stranded depth would leave this fake silently
        /// swallowing every later <c>Changed</c> notification, so an abort would appear to
        /// work and then break the shell's menus for the rest of the session.
        /// </remarks>
        private void Abort()
        {
            _transactionDepth--;

            for (var i = _entries.Count - 1; i >= _transactionStart; i--)
            {
                try
                {
                    _entries[i].Undo();
                }
                catch (Exception)
                {
                }
            }

            _entries.RemoveRange(_transactionStart, _entries.Count - _transactionStart);
            Labels.RemoveRange(_labelStart, Labels.Count - _labelStart);

            _cursor = _entries.Count;
            _transactionLabel = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Commit()
        {
            _transactionDepth--;

            var folded = _entries.Skip(_transactionStart).ToArray();
            if (folded.Length == 0)
            {
                // Disposing with nothing recorded records nothing.
                _transactionLabel = null;
                return;
            }

            _entries.RemoveRange(_transactionStart, folded.Length);

            _entries.Add(new HistoryEntry(
                _transactionLabel ?? "Transaction",
                () =>
                {
                    for (var i = folded.Length - 1; i >= 0; i--)
                    {
                        folded[i].Undo();
                    }
                },
                () =>
                {
                    foreach (var entry in folded)
                    {
                        entry.Redo();
                    }
                }));

            _cursor = _entries.Count;
            _transactionLabel = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private sealed class Scope(SnapshotHistory owner) : IEditTransaction
        {
            private bool _completed;

            public void Commit()
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                owner.Commit();
            }

            public void Abort()
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                owner.Abort();
            }

            public void Dispose() => Abort();
        }
    }

    private static (Document Doc, SnapshotHistory History, SectionEditor Section) Build()
    {
        var document = new Document();
        var history = new SnapshotHistory();

        var health = new NumericFieldViewModel(
            new NumericFieldDescriptor
            {
                Key = "health",
                Label = "Health",
                Minimum = 0,
                Maximum = 999,
                Read = () => document.Health,
                Write = value => document.Health = value,
            },
            history);

        var name = new TextFieldViewModel(
            new TextFieldDescriptor
            {
                Key = "name",
                Label = "Name",
                Read = () => document.Name,
                Write = value => document.Name = value,
            },
            history);

        return (document, history, new SectionEditor("main", "Main", [health, name], history));
    }

    [Fact]
    public void AField_RecordsIntoACustomHistory()
    {
        var (document, history, section) = Build();

        // Numeric fields are driven through Text, not Draft: HasPendingEdit compares the
        // typed text so that unparseable input still counts as outstanding, which means
        // setting Draft alone leaves the field reporting nothing pending.
        var health = (NumericFieldViewModel)section.Fields[0];
        health.Text = "42";
        health.Apply();

        Assert.Equal(42L, document.Health);
        Assert.True(history.CanUndo);
        Assert.True(history.IsDirty);

        history.Undo();

        Assert.Equal(100L, document.Health);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);

        history.Redo();
        Assert.Equal(42L, document.Health);
    }

    /// <summary>Apply All collapses into one entry through the custom transaction too.</summary>
    [Fact]
    public void ApplyAll_FoldsIntoOneEntryOfACustomHistory()
    {
        var (document, history, section) = Build();

        ((NumericFieldViewModel)section.Fields[0]).Text = "7";
        ((TextFieldViewModel)section.Fields[1]).Draft = "renamed";

        section.ApplyAllCommand.Execute(null);

        Assert.Equal(7L, document.Health);
        Assert.Equal("renamed", document.Name);

        // One button press, one undo -- and it reverses both fields.
        history.Undo();

        Assert.Equal(100L, document.Health);
        Assert.Equal("hero", document.Name);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void ApplyAll_WithNothingPendingRecordsNothing()
    {
        var (_, history, section) = Build();

        section.ApplyAllCommand.Execute(null);

        Assert.False(history.CanUndo);
        Assert.False(history.IsDirty);
    }

    [Fact]
    public void MarkSaved_ClearsDirtyOnACustomHistory()
    {
        var (_, history, section) = Build();

        ((NumericFieldViewModel)section.Fields[0]).Text = "55";
        section.Fields[0].Apply();

        Assert.True(history.IsDirty);

        history.MarkSaved();
        Assert.False(history.IsDirty);

        history.Undo();
        Assert.True(history.IsDirty);
    }

    /// <summary>
    /// The all-or-nothing Apply All guarantee holds over a custom history too, not only over
    /// the shipped one.
    /// </summary>
    /// <remarks>
    /// The point of the seam is that an adopter's own history is a first-class implementation,
    /// so the transaction contract has to be a contract rather than a description of what
    /// <c>EditHistory</c> happens to do.
    /// </remarks>
    [Fact]
    public void ApplyAll_RollsBackOverACustomHistoryToo()
    {
        var document = new Document();
        var history = new SnapshotHistory();
        string? rejects = null;

        var health = new NumericFieldViewModel(
            new NumericFieldDescriptor
            {
                Key = "health",
                Label = "Health",
                Minimum = 0,
                Maximum = 999,
                Read = () => document.Health,
                Write = value => document.Health = value,
            },
            history);

        var name = new TextFieldViewModel(
            new TextFieldDescriptor
            {
                Key = "name",
                Label = "Name",
                Read = () => document.Name,
                Write = value =>
                {
                    if (rejects is { } message)
                    {
                        throw new InvalidOperationException(message);
                    }

                    document.Name = value;
                },
            },
            history);

        var section = new SectionEditor("main", "Main", [health, name], history);

        health.Text = "42";
        name.Draft = "renamed";
        rejects = "that name is taken";

        section.ApplyAllCommand.Execute(null);

        // Neither field reached the document, and nothing was recorded.
        Assert.Equal(100L, document.Health);
        Assert.Equal("hero", document.Name);
        Assert.False(history.CanUndo);
        Assert.Empty(history.Labels);
        Assert.False(history.IsDirty);

        // And the history is still usable: a stranded transaction depth would have suppressed
        // every later Changed notification as well as blocking the next batch.
        var changed = 0;
        history.Changed += (_, _) => changed++;

        rejects = null;
        health.Text = "55";
        name.Draft = "accepted";

        section.ApplyAllCommand.Execute(null);

        Assert.Equal(55L, document.Health);
        Assert.Equal("accepted", document.Name);
        Assert.True(history.CanUndo);
        Assert.True(changed > 0, "The custom history stopped raising Changed after an abort.");
    }

    /// <summary>The shipped implementation is one of these, so nothing has to change.</summary>
    [Fact]
    public void EditHistory_IsAnEditHistorySeam()
    {
        Assert.IsAssignableFrom<IEditHistory>(new EditHistory());
    }
}

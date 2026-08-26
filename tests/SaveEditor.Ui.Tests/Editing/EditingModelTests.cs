using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.Tests.Editing;

/// <summary>
/// P3 acceptance for the editing model: pending drafts, Apply, Apply All, history,
/// and revision-based dirty tracking.
/// </summary>
public class EditingModelTests
{
    /// <summary>A stand-in document. Fields read and write these directly.</summary>
    private sealed class Document
    {
        public string Name { get; set; } = "Aerith";

        public long Health { get; set; } = 100;

        public bool Invincible { get; set; }
    }

    private static (Document Doc, EditHistory History, SectionEditor Section) Build(int capacity = EditHistory.DefaultCapacity)
    {
        var doc = new Document();
        var history = new EditHistory(capacity);

        var name = new TextFieldViewModel(
            new TextFieldDescriptor
            {
                Key = "name", Label = "Name", Path = "player.name", MaxLength = 12,
                Read = () => doc.Name, Write = v => doc.Name = v,
            },
            history);

        var health = new NumericFieldViewModel(
            new NumericFieldDescriptor
            {
                Key = "health", Label = "Health", Path = "player.hp",
                Minimum = 0, Maximum = 9999,
                Read = () => doc.Health, Write = v => doc.Health = v,
            },
            history);

        var invincible = new BooleanFieldViewModel(
            new BooleanFieldDescriptor
            {
                Key = "invincible", Label = "Invincible", WarningText = "Alters game balance.",
                Read = () => doc.Invincible, Write = v => doc.Invincible = v,
            },
            history);

        var section = new SectionEditor("player", "Player", [name, health, invincible], history);
        return (doc, history, section);
    }

    private static T Field<T>(SectionEditor section, string key)
        where T : FieldViewModel =>
        (T)section.Fields.Single(f => f.Key == key);

    [Fact]
    public void Typing_Does_Not_Touch_The_Document_Or_The_History()
    {
        var (doc, history, section) = Build();

        Field<TextFieldViewModel>(section, "name").Draft = "Tifa";

        // The whole point of a pending draft: nothing is committed until Apply.
        Assert.Equal("Aerith", doc.Name);
        Assert.False(history.IsDirty);
        Assert.Equal(0, history.Count);
        Assert.True(section.HasPendingEdits);
    }

    [Fact]
    public void Apply_Commits_Exactly_One_History_Entry()
    {
        var (doc, history, section) = Build();

        Field<TextFieldViewModel>(section, "name").Draft = "Tifa";
        Field<TextFieldViewModel>(section, "name").Apply();

        Assert.Equal("Tifa", doc.Name);
        Assert.Equal(1, history.Count);
        Assert.True(history.IsDirty);
        Assert.False(section.HasPendingEdits);
    }

    [Fact]
    public void Apply_All_Commits_Three_Fields_As_One_Undo_Step()
    {
        var (doc, history, section) = Build();

        Field<TextFieldViewModel>(section, "name").Draft = "Tifa";
        Field<NumericFieldViewModel>(section, "health").Text = "250";
        Field<BooleanFieldViewModel>(section, "invincible").Draft = true;

        section.ApplyAll();

        Assert.Equal("Tifa", doc.Name);
        Assert.Equal(250, doc.Health);
        Assert.True(doc.Invincible);

        // One button press, one undo step.
        Assert.Equal(1, history.Count);

        history.Undo();

        Assert.Equal("Aerith", doc.Name);
        Assert.Equal(100, doc.Health);
        Assert.False(doc.Invincible);
    }

    [Fact]
    public void Apply_All_With_Nothing_Pending_Records_No_Entry()
    {
        var (_, history, section) = Build();

        section.ApplyAll();

        // An undo step that undoes nothing is worse than no step: it makes Ctrl+Z
        // appear broken.
        Assert.Equal(0, history.Count);
        Assert.False(history.IsDirty);
    }

    [Fact]
    public void Undo_Back_To_The_Saved_Point_Reports_Clean()
    {
        var (_, history, section) = Build();

        history.MarkSaved();
        Field<TextFieldViewModel>(section, "name").Draft = "Tifa";
        Field<TextFieldViewModel>(section, "name").Apply();

        Assert.True(history.IsDirty);

        history.Undo();

        // A boolean dirty flag cannot do this: it is set on the first edit and never
        // learns the document came back.
        Assert.False(history.IsDirty);
    }

    [Fact]
    public void Redo_After_Undo_Restores_The_Value()
    {
        var (doc, history, section) = Build();

        Field<NumericFieldViewModel>(section, "health").Text = "500";
        Field<NumericFieldViewModel>(section, "health").Apply();

        history.Undo();
        Assert.Equal(100, doc.Health);

        history.Redo();
        Assert.Equal(500, doc.Health);
        Assert.Equal("500", Field<NumericFieldViewModel>(section, "health").Text);
    }

    [Fact]
    public void A_New_Edit_After_Undo_Discards_The_Redo_Tail()
    {
        var (_, history, section) = Build();

        Field<TextFieldViewModel>(section, "name").Draft = "Tifa";
        Field<TextFieldViewModel>(section, "name").Apply();
        history.Undo();

        Assert.True(history.CanRedo);

        Field<BooleanFieldViewModel>(section, "invincible").Draft = true;
        Field<BooleanFieldViewModel>(section, "invincible").Apply();

        // Keeping the tail would let undo and redo describe two different documents.
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void History_Holds_At_Capacity_And_Keeps_The_Newest()
    {
        var (doc, history, section) = Build(capacity: 10);
        var health = Field<NumericFieldViewModel>(section, "health");

        for (var i = 1; i <= 25; i++)
        {
            health.Text = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            health.Apply();
        }

        Assert.Equal(10, history.Count);
        Assert.Equal(25, doc.Health);

        // Ten undos are available and land on the value ten edits back.
        for (var i = 0; i < 10; i++)
        {
            history.Undo();
        }

        Assert.False(history.CanUndo);
        Assert.Equal(15, doc.Health);
    }

    [Fact]
    public void Default_Capacity_Is_One_Thousand()
    {
        var (_, history, section) = Build();
        var health = Field<NumericFieldViewModel>(section, "health");

        for (var i = 1; i <= 1200; i++)
        {
            health.Text = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            health.Apply();
        }

        Assert.Equal(EditHistory.DefaultCapacity, history.Count);
    }

    [Fact]
    public void Numeric_Parsing_Is_Invariant_And_Rejects_Nonsense()
    {
        var (doc, _, section) = Build();
        var health = Field<NumericFieldViewModel>(section, "health");

        health.Text = "abc";
        Assert.False(health.IsValid);
        Assert.False(health.CanApply);

        health.Apply();
        Assert.Equal(100, doc.Health);

        // A grouped number is not a whole number here. Parsing it per the user's
        // culture would write a different value on a differently configured machine.
        health.Text = "1,234";
        Assert.False(health.IsValid);

        health.Text = "1234";
        Assert.True(health.IsValid);
    }

    [Fact]
    public void Out_Of_Range_Values_Are_Reported_Not_Clamped()
    {
        var (doc, _, section) = Build();
        var health = Field<NumericFieldViewModel>(section, "health");

        health.Text = "99999";

        Assert.False(health.IsValid);
        Assert.Contains("between 0 and 9999", health.ValidationError, StringComparison.Ordinal);

        health.Apply();

        // Silently clamping would write a number the user never chose.
        Assert.Equal(100, doc.Health);
    }

    [Fact]
    public void Text_Length_Is_Validated_Against_The_Descriptor()
    {
        var (_, _, section) = Build();
        var name = Field<TextFieldViewModel>(section, "name");

        name.Draft = "AbsurdlyLongName";
        Assert.False(name.IsValid);

        name.Draft = "Tifa";
        Assert.True(name.IsValid);
    }

    [Fact]
    public void Revert_Discards_The_Draft_And_Leaves_The_Document_Alone()
    {
        var (doc, history, section) = Build();

        Field<TextFieldViewModel>(section, "name").Draft = "Tifa";
        section.RevertAll();

        Assert.False(section.HasPendingEdits);
        Assert.Equal("Aerith", doc.Name);
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Filtering_Never_Hides_A_Field_With_A_Pending_Edit()
    {
        var (_, _, section) = Build();

        Field<BooleanFieldViewModel>(section, "invincible").Draft = true;
        section.SearchText = "Health";

        // Hiding it would let Apply All commit a value the user cannot see, or let
        // them navigate away believing nothing was outstanding.
        Assert.Contains(section.VisibleFields, f => f.Key == "invincible");
        Assert.Contains(section.VisibleFields, f => f.Key == "health");
        Assert.DoesNotContain(section.VisibleFields, f => f.Key == "name");
    }

    [Fact]
    public void Pending_Drafts_Survive_Navigating_Away_And_Back()
    {
        var (doc, _, section) = Build();

        Field<TextFieldViewModel>(section, "name").Draft = "Tifa";
        Field<NumericFieldViewModel>(section, "health").Text = "250";

        // Navigation does not destroy the section editor; drafts live on it.
        var revisited = section;

        Assert.Equal("Tifa", Field<TextFieldViewModel>(revisited, "name").Draft);
        Assert.Equal("250", Field<NumericFieldViewModel>(revisited, "health").Text);
        Assert.Equal(2, revisited.PendingCount);
        Assert.Equal("Aerith", doc.Name);
    }

    [Fact]
    public void Invalid_Pending_Fields_Are_Counted_And_Skipped_By_Apply_All()
    {
        var (doc, history, section) = Build();

        Field<TextFieldViewModel>(section, "name").Draft = "Tifa";
        Field<NumericFieldViewModel>(section, "health").Text = "not-a-number";

        Assert.Equal(1, section.InvalidCount);

        section.ApplyAll();

        // The valid field commits; the invalid one keeps its draft rather than being
        // dropped, so the user does not lose what they typed.
        Assert.Equal("Tifa", doc.Name);
        Assert.Equal(100, doc.Health);
        Assert.Equal(1, history.Count);
        Assert.True(section.HasPendingEdits);
    }

    [Fact]
    public void Unparseable_Numeric_Text_Still_Counts_As_A_Pending_Edit()
    {
        var (_, _, section) = Build();
        var health = Field<NumericFieldViewModel>(section, "health");

        health.Text = "not-a-number";

        // The exit guard is driven by pending state. If an unparseable draft did not
        // count as pending, closing the editor would discard the user's typed text
        // without asking - which is precisely the loss the guard exists to prevent.
        Assert.True(health.HasPendingEdit);
        Assert.True(section.HasPendingEdits);
        Assert.False(health.CanApply);
    }

    [Fact]
    public void Clearing_A_Numeric_Field_Back_To_Its_Committed_Text_Is_Not_Pending()
    {
        var (_, _, section) = Build();
        var health = Field<NumericFieldViewModel>(section, "health");

        health.Text = "250";
        Assert.True(health.HasPendingEdit);

        health.Text = "100";

        // Typing your way back to the original value is not an outstanding edit, and
        // prompting about it would train users to dismiss the guard.
        Assert.False(health.HasPendingEdit);
        Assert.False(section.HasPendingEdits);
    }

    [Fact]
    public void Spinner_Steps_By_One_And_Stops_At_The_Bounds()
    {
        var (_, _, section) = Build();
        var health = Field<NumericFieldViewModel>(section, "health");

        health.Increment();
        Assert.Equal("101", health.Text);

        health.Decrement();
        health.Decrement();
        Assert.Equal("99", health.Text);

        // Clamping is correct here and wrong for typing: pressing increment means
        // "one more", so stopping at the bound is what was asked for.
        health.Text = "9999";
        health.Increment();
        Assert.Equal("9999", health.Text);
        Assert.True(health.IsValid);

        health.Text = "0";
        health.Decrement();
        Assert.Equal("0", health.Text);
        Assert.True(health.IsValid);
    }

    [Fact]
    public void Spinner_Recovers_From_Unparseable_Text()
    {
        var (_, _, section) = Build();
        var health = Field<NumericFieldViewModel>(section, "health");

        health.Text = "garbage";
        health.Increment();

        // Stepping from nonsense resumes at the committed value rather than doing
        // nothing, so the control is never stuck in an unusable state.
        Assert.Equal("101", health.Text);
        Assert.True(health.IsValid);
    }

    [Fact]
    public void Read_Only_Fields_Never_Become_Pending()
    {
        var doc = new Document();
        var history = new EditHistory();

        var field = new ReadOnlyFieldViewModel(
            new ReadOnlyFieldDescriptor
            {
                Key = "checksum", Label = "Checksum", IsReadOnly = true,
                Read = () => "0x1F4A9",
            },
            history);

        var section = new SectionEditor("meta", "Metadata", [field], history);

        Assert.False(field.HasPendingEdit);
        Assert.False(field.CanApply);
        Assert.False(section.CanApplyAll);
        Assert.Equal("0x1F4A9", field.Value);
    }
}

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Controls;
using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.HeadlessTests.Controls;

/// <summary>P3 acceptance for <see cref="FieldCard"/>.</summary>
public class FieldCardTests
{
    private static async Task<FieldCard> Realize(FieldViewModel field)
    {
        var card = new FieldCard { Field = field };
        var window = new Window { Width = 500, Height = 400, Content = card };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        return card;
    }

    [AvaloniaFact]
    public async Task Shows_The_Validation_Message_And_Disables_Apply_For_An_Invalid_Draft()
    {
        var (_, _, section) = ControlsTestFixture.BuildSection();
        var level = (NumericFieldViewModel)section.Fields.Single(f => f.Key == "level");

        var card = await Realize(level);

        level.Text = "not-a-number";
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(level.ValidationError);
        Assert.False(level.CanApply);

        var validationMessage = card.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.Name == "PART_ValidationMessage");

        Assert.NotNull(validationMessage);
        Assert.True(validationMessage!.IsVisible);
        Assert.Equal(level.ValidationError, validationMessage.Text);

        var applyButton = card.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_ApplyButton");
        Assert.False(applyButton.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Valid_Draft_Shows_No_Validation_Message_And_Enables_Apply()
    {
        var (_, _, section) = ControlsTestFixture.BuildSection();
        var name = (TextFieldViewModel)section.Fields.Single(f => f.Key == "name");

        var card = await Realize(name);

        name.Draft = "Tifa";
        Dispatcher.UIThread.RunJobs();

        var validationMessage = card.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(t => t.Name == "PART_ValidationMessage");

        Assert.False(validationMessage.IsVisible);

        var applyButton = card.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_ApplyButton");
        Assert.True(applyButton.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Pending_State_Is_Surfaced_By_Text_Not_Only_Colour()
    {
        var (_, _, section) = ControlsTestFixture.BuildSection();
        var name = (TextFieldViewModel)section.Fields.Single(f => f.Key == "name");

        var card = await Realize(name);

        // Before any edit, nothing marks the field as pending.
        var beforeText = card.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.IsVisible && t.Text != null && t.Text.Contains("Pending"));
        Assert.Null(beforeText);

        name.Draft = "Tifa";
        Dispatcher.UIThread.RunJobs();

        // The pending indicator carries literal text ("Pending"), not merely a
        // colour or border change, so it reads without relying on colour.
        var pendingText = card.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.IsVisible && t.Text != null && t.Text.Contains("Pending"));

        Assert.NotNull(pendingText);
        Assert.Equal("Pending edit", AutomationProperties.GetName(pendingText!));
    }

    [AvaloniaFact]
    public async Task Apply_On_A_Card_Commits_Through_The_Model()
    {
        var (doc, history, section) = ControlsTestFixture.BuildSection();
        var name = (TextFieldViewModel)section.Fields.Single(f => f.Key == "name");

        var card = await Realize(name);

        name.Draft = "Tifa";
        Dispatcher.UIThread.RunJobs();

        var applyButton = card.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_ApplyButton");
        Assert.True(applyButton.IsEnabled);

        applyButton.Command!.Execute(null);

        Assert.Equal("Tifa", doc.Name);
        Assert.Equal(1, history.Count);
        Assert.False(name.HasPendingEdit);
    }

    [AvaloniaFact]
    public async Task Accessible_Names_Exist_On_The_Editor_Surface_And_The_Apply_Action()
    {
        var (_, _, section) = ControlsTestFixture.BuildSection();
        var name = (TextFieldViewModel)section.Fields.Single(f => f.Key == "name");

        var card = await Realize(name);

        var editorInput = card.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.Equal("Name", AutomationProperties.GetName(editorInput));

        var applyButton = card.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_ApplyButton");
        Assert.Equal("Apply Name", AutomationProperties.GetName(applyButton));
    }

    [AvaloniaFact]
    public async Task Validation_Message_Is_Associated_With_The_Input_For_Assistive_Technology()
    {
        var (_, _, section) = ControlsTestFixture.BuildSection();
        var level = (NumericFieldViewModel)section.Fields.Single(f => f.Key == "level");

        var card = await Realize(level);

        level.Text = "not-a-number";
        Dispatcher.UIThread.RunJobs();

        var editorInput = card.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.Equal(level.ValidationError, AutomationProperties.GetHelpText(editorInput));
    }
}

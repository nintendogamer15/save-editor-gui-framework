using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Controls;
using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.HeadlessTests.Controls;

/// <summary>P3 acceptance for <see cref="SectionToolbar"/>.</summary>
public class SectionToolbarTests
{
    private static async Task<SectionToolbar> Realize(SectionEditor section)
    {
        var toolbar = new SectionToolbar { Editor = section };
        var window = new Window { Width = 800, Height = 200, Content = toolbar };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        return toolbar;
    }

    [AvaloniaFact]
    public async Task Apply_All_Produces_Exactly_One_History_Entry()
    {
        var (doc, history, section) = ControlsTestFixture.BuildSection();
        var name = (TextFieldViewModel)section.Fields.Single(f => f.Key == "name");
        var level = (NumericFieldViewModel)section.Fields.Single(f => f.Key == "level");

        var toolbar = await Realize(section);

        name.Draft = "Tifa";
        level.Text = "42";
        Dispatcher.UIThread.RunJobs();

        var applyAllButton = toolbar.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_ApplyAllButton");
        Assert.True(applyAllButton.IsEnabled);

        applyAllButton.Command!.Execute(null);

        Assert.Equal("Tifa", doc.Name);
        Assert.Equal(42, doc.Level);
        Assert.Equal(1, history.Count);
        Assert.False(section.HasPendingEdits);
    }

    [AvaloniaFact]
    public async Task Search_Box_Is_Bound_To_The_Section_Search_Text()
    {
        var (_, _, section) = ControlsTestFixture.BuildSection();
        var toolbar = await Realize(section);

        var searchBox = toolbar.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_SearchBox");
        searchBox.Text = "level";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("level", section.SearchText);
        Assert.Single(section.VisibleFields);
        Assert.Equal("level", section.VisibleFields[0].Key);
    }

    [AvaloniaFact]
    public async Task Pending_Summary_Reflects_The_Section_Pending_Count()
    {
        var (_, _, section) = ControlsTestFixture.BuildSection();
        var name = (TextFieldViewModel)section.Fields.Single(f => f.Key == "name");

        var toolbar = await Realize(section);

        var summary = toolbar.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_PendingSummary");
        Assert.Equal("0 pending", summary.Text);

        name.Draft = "Tifa";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("1 pending", summary.Text);
    }

    [AvaloniaFact]
    public async Task Bulk_Actions_Slot_Presents_Editor_Supplied_Content()
    {
        var (_, _, section) = ControlsTestFixture.BuildSection();
        var toolbar = new SectionToolbar { Editor = section, BulkActions = new Button { Content = "Fill all HP" } };
        var window = new Window { Width = 800, Height = 200, Content = toolbar };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        var bulkButton = toolbar.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => (string?)b.Content == "Fill all HP");
        Assert.NotNull(bulkButton);
    }
}

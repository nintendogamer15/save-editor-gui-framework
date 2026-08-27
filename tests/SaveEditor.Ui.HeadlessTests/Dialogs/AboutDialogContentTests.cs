using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Dialogs;

namespace SaveEditor.Ui.HeadlessTests.Dialogs;

/// <summary>
/// <see cref="AboutDialogContent"/> gives a consuming editor real slots for app
/// identity, credits, and licenses rather than the shell's previous plain text.
/// </summary>
public class AboutDialogContentTests
{
    private static async Task<AboutDialogContent> Realize(AboutDialogContent content, double height = 500)
    {
        var window = new Window { Width = 500, Height = height, Content = content };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        return content;
    }

    [AvaloniaFact]
    public async Task Renders_Each_Consumer_Supplied_Slot()
    {
        var content = await Realize(new AboutDialogContent
        {
            AppIdentity = new TextBlock { Text = "Example Save Editor v1.0" },
            Credits = "Built by a consuming editor.",
            Licenses = "MIT",
        });

        var presenters = content.GetVisualDescendants().OfType<ContentPresenter>().ToList();

        Assert.Contains(presenters, p => p.Name == "PART_AppIdentity" && p.Content is TextBlock tb && tb.Text == "Example Save Editor v1.0");
        Assert.Contains(presenters, p => p.Name == "PART_Credits" && Equals(p.Content, "Built by a consuming editor."));
        Assert.Contains(presenters, p => p.Name == "PART_Licenses" && Equals(p.Content, "MIT"));
    }

    [AvaloniaFact]
    public async Task Close_Button_Raises_CloseRequested()
    {
        var content = await Realize(new AboutDialogContent());

        var raised = false;
        content.CloseRequested += (_, _) => raised = true;

        var closeButton = content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_CloseButton");
        closeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.True(raised);
    }

    [AvaloniaFact]
    public async Task Long_Licenses_Scroll_And_Leave_Close_Outside_The_Scroller()
    {
        var licenses = string.Join('\n', Enumerable.Repeat("Third-party licence text that has to be present.", 80));
        var content = await Realize(new AboutDialogContent
        {
            AppIdentity = "Example Save Editor",
            Credits = "Built by a consuming editor.",
            Licenses = licenses,
        }, height: 800);

        var scroller = content.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PART_BodyScroller");
        var closeButton = content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_CloseButton");

        Assert.Equal(DialogHostBounds.DefaultBodyMaxHeight, scroller.MaxHeight);
        Assert.True(
            scroller.Extent.Height > scroller.Viewport.Height,
            $"Expected the licences to overflow the scroller (extent {scroller.Extent.Height}, viewport {scroller.Viewport.Height}).");
        Assert.DoesNotContain(closeButton, scroller.GetVisualDescendants().OfType<Button>());
    }
}

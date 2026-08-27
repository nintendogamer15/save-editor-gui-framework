using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Dialogs;

namespace SaveEditor.Ui.HeadlessTests.Dialogs;

/// <summary>
/// <see cref="DocumentViewerContent"/> is the framework's reusable read-only
/// document viewer, tested directly rather than through a hosting window.
/// </summary>
public class DocumentViewerContentTests
{
    private static async Task<DocumentViewerContent> Realize(DocumentViewerContent content, double height = 500)
    {
        var window = new Window { Width = 500, Height = height, Content = content };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        return content;
    }

    [AvaloniaFact]
    public async Task Shows_Title_And_Neutralized_Document_Text()
    {
        var content = await Realize(new DocumentViewerContent
        {
            Title = "License",
            DocumentText = "Line one‮reversed",
        });

        var body = content.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "PART_Body");

        Assert.DoesNotContain('‮', body.Text ?? string.Empty);
    }

    [AvaloniaFact]
    public async Task Close_Button_Raises_CloseRequested()
    {
        var content = await Realize(new DocumentViewerContent { Title = "Title", DocumentText = "Text" });

        var raised = false;
        content.CloseRequested += (_, _) => raised = true;

        var closeButton = content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_CloseButton");
        closeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.True(raised);
    }

    [AvaloniaFact]
    public async Task Long_Document_Scrolls_And_Leaves_Close_Outside_The_Scroller()
    {
        var text = string.Join('\n', Enumerable.Repeat("A line of a validation report.", 80));
        var content = await Realize(new DocumentViewerContent { Title = "Report", DocumentText = text }, height: 800);

        var scroller = content.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PART_BodyScroller");
        var closeButton = content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_CloseButton");

        Assert.Equal(DialogHostBounds.DefaultBodyMaxHeight, scroller.MaxHeight);
        Assert.True(
            scroller.Extent.Height > scroller.Viewport.Height,
            $"Expected the document to overflow the scroller (extent {scroller.Extent.Height}, viewport {scroller.Viewport.Height}).");
        Assert.DoesNotContain(closeButton, scroller.GetVisualDescendants().OfType<Button>());
    }

    [AvaloniaFact]
    public async Task Close_Stays_Inside_A_Host_Shorter_Than_The_Body_Max_Height()
    {
        var text = string.Join('\n', Enumerable.Repeat("A line of a validation report.", 80));
        var content = new DocumentViewerContent { Title = "Report", DocumentText = text };
        var window = new Window
        {
            Width = 560,
            MaxHeight = 360,
            SizeToContent = SizeToContent.Height,
            Content = content,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        var closeButton = content.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_CloseButton");
        var closeOrigin = closeButton.TranslatePoint(new Point(0, 0), window)
                          ?? throw new InvalidOperationException("Close could not be mapped into the host.");
        Assert.True(closeOrigin.Y >= 0);
        Assert.True(closeOrigin.Y + closeButton.Bounds.Height <= window.Bounds.Height + 0.5);

        var scroller = content.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PART_BodyScroller");
        Assert.True(scroller.Bounds.Height < DialogHostBounds.DefaultBodyMaxHeight);
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
    }
}

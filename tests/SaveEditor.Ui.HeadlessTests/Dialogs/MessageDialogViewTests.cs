using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Dialogs;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.HeadlessTests.Dialogs;

/// <summary><see cref="MessageDialogView"/> tested directly, without a modal message pump.</summary>
public class MessageDialogViewTests
{
    private static async Task<MessageDialogView> Realize(MessageDialogView view, double height = 400)
    {
        var window = new Window { Width = 500, Height = height, Content = view };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        return view;
    }

    [AvaloniaFact]
    public async Task Shows_Title_Message_And_Sanitized_Details()
    {
        var request = new MessageRequest(
            "Detection result",
            "The file was recognized as a supported format.",
            [new UntrustedText("codec note: bell character present")]);

        var view = await Realize(new MessageDialogView(request));

        var title = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Title");
        var message = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Message");

        Assert.Equal("Detection result", title.Text);
        Assert.Equal("The file was recognized as a supported format.", message.Text);

        var panel = view.GetVisualDescendants().OfType<Border>().SingleOrDefault(b => b.Name == "PART_CodecWarnings");
        Assert.NotNull(panel);
    }

    [AvaloniaFact]
    public async Task Close_Click_Fires()
    {
        var view = await Realize(new MessageDialogView(new MessageRequest("Title", "Message")));

        var clicked = false;
        view.CloseButton.Click += (_, _) => clicked = true;
        view.CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.True(clicked);
    }

    [AvaloniaFact]
    public async Task Long_Body_Scrolls_And_Leaves_Close_Outside_The_Scroller()
    {
        var message = string.Join('\n', Enumerable.Repeat("Third-party licence text that has to be present.", 80));
        var view = await Realize(new MessageDialogView(new MessageRequest("About", message)), height: 800);

        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PART_BodyScroller");

        Assert.Equal(DialogHostBounds.DefaultBodyMaxHeight, scroller.MaxHeight);
        Assert.True(
            scroller.Extent.Height > scroller.Viewport.Height,
            $"Expected the licence text to overflow the scroller (extent {scroller.Extent.Height}, viewport {scroller.Viewport.Height}).");
        Assert.DoesNotContain(view.CloseButton, scroller.GetVisualDescendants().OfType<Button>());
        Assert.True(view.CloseButton.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public async Task Close_Stays_Inside_A_Host_Shorter_Than_The_Body_Max_Height()
    {
        var message = string.Join('\n', Enumerable.Repeat("Third-party licence text that has to be present.", 80));
        var view = new MessageDialogView(new MessageRequest("About", message));
        var window = new Window
        {
            Width = 480,
            MaxHeight = 360,
            SizeToContent = SizeToContent.Height,
            Content = view,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        Assert.True(
            window.Bounds.Height <= 360 + 1,
            $"Host grew to {window.Bounds.Height}px against a 360px MaxHeight.");

        var closeOrigin = view.CloseButton.TranslatePoint(new Point(0, 0), window)
                          ?? throw new InvalidOperationException("Close could not be mapped into the host.");
        Assert.True(closeOrigin.Y >= 0, $"Close top is at {closeOrigin.Y}.");
        Assert.True(
            closeOrigin.Y + view.CloseButton.Bounds.Height <= window.Bounds.Height + 0.5,
            $"Close bottom is at {closeOrigin.Y + view.CloseButton.Bounds.Height} in a {window.Bounds.Height}px host.");

        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PART_BodyScroller");
        Assert.True(
            scroller.Bounds.Height < DialogHostBounds.DefaultBodyMaxHeight,
            $"Scroller stayed at {scroller.Bounds.Height}px instead of shrinking under the 360px cap.");
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
    }
}

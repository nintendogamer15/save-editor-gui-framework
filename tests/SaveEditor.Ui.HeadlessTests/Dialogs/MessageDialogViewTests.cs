using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Dialogs;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.HeadlessTests.Dialogs;

/// <summary><see cref="MessageDialogView"/> tested directly, without a modal message pump.</summary>
public class MessageDialogViewTests
{
    private static async Task<MessageDialogView> Realize(MessageDialogView view)
    {
        var window = new Window { Width = 500, Height = 400, Content = view };

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
}

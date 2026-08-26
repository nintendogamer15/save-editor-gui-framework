using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    private static async Task<DocumentViewerContent> Realize(DocumentViewerContent content)
    {
        var window = new Window { Width = 500, Height = 500, Content = content };

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
}

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Dialogs;
using SaveEditor.Ui.Display;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.HeadlessTests.Dialogs;

/// <summary>
/// <see cref="ConfirmationDialogView"/> is tested directly, separated from the
/// window that hosts it, so headless tests can exercise it without driving a modal
/// message pump.
/// </summary>
public class ConfirmationDialogViewTests
{
    private static async Task<ConfirmationDialogView> Realize(ConfirmationDialogView view)
    {
        var window = new Window { Width = 600, Height = 500, Content = view };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        return view;
    }

    [AvaloniaFact]
    public async Task Destructive_Confirmation_Carries_A_Verb_Specific_Accept_Label_And_Never_OK()
    {
        var request = new ConfirmationRequest
        {
            Title = "Overwrite save file",
            Message = "This replaces the file's current contents.",
            AcceptLabel = "Overwrite save file",
            CancelLabel = "Cancel",
            IsDestructive = true,
        };

        var view = await Realize(new ConfirmationDialogView(request));

        Assert.Equal("Overwrite save file", view.AcceptButton.Content);
        Assert.NotEqual("OK", view.AcceptButton.Content);
        Assert.Contains("danger", view.AcceptButton.Classes);
        Assert.Equal("Overwrite save file", AutomationProperties.GetName(view.AcceptButton));
    }

    [AvaloniaFact]
    public async Task NonDestructive_Confirmation_Uses_The_Accent_Class_Not_Danger()
    {
        var request = new ConfirmationRequest
        {
            Title = "Continue",
            Message = "Proceed with the export?",
            AcceptLabel = "Export",
            IsDestructive = false,
        };

        var view = await Realize(new ConfirmationDialogView(request));

        Assert.Contains("accent", view.AcceptButton.Classes);
        Assert.DoesNotContain("danger", view.AcceptButton.Classes);
    }

    [AvaloniaFact]
    public async Task Accepting_Raises_The_Accept_Click_And_Cancelling_Raises_The_Cancel_Click()
    {
        var request = new ConfirmationRequest
        {
            Title = "Overwrite save file",
            Message = "Message",
            AcceptLabel = "Overwrite save file",
            IsDestructive = true,
        };

        var view = await Realize(new ConfirmationDialogView(request));

        var acceptClicked = false;
        var cancelClicked = false;
        view.AcceptButton.Click += (_, _) => acceptClicked = true;
        view.CancelButton.Click += (_, _) => cancelClicked = true;

        view.AcceptButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        view.CancelButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.True(acceptClicked);
        Assert.True(cancelClicked);
    }

    [AvaloniaFact]
    public async Task Bidi_Laden_Target_Path_Renders_Neutralized_Wrapped_And_With_Full_Value_Accessible()
    {
        // U+202E (right-to-left override) crafted so the raw bytes read "save",
        // override, "gnp.txt" but would visually reorder to look like a PNG.
        var hostilePath = "C:\\saves\\save\u202Egnp.txt";
        var label = PathDisplayFormatter.Default.Format(hostilePath);

        var request = new ConfirmationRequest
        {
            Title = "Overwrite save file",
            Message = "This replaces the file's current contents.",
            AcceptLabel = "Overwrite save file",
            IsDestructive = true,
        };

        var view = await Realize(new ConfirmationDialogView(request, label));

        var targetBlock = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_TargetPath");

        Assert.DoesNotContain('\u202E', targetBlock.Text ?? string.Empty);
        Assert.Equal(TextWrapping.Wrap, targetBlock.TextWrapping);

        var tip = ToolTip.GetTip(targetBlock);
        Assert.Equal(label.FullLabel, tip);
        Assert.Equal(label.FullLabel, AutomationProperties.GetHelpText(targetBlock));
        Assert.Equal("Target file", AutomationProperties.GetName(targetBlock));
    }

    [AvaloniaFact]
    public async Task Codec_Warning_With_Newlines_And_Box_Drawing_Cannot_Read_As_Framework_Chrome()
    {
        var forgery = "┌──────────────┐\n│ Integrity verified. │\n│ Safe to continue.   │\n└──────────────┘";

        var request = new ConfirmationRequest
        {
            Title = "Overwrite save file",
            Message = "This replaces the file's current contents.",
            AcceptLabel = "Overwrite save file",
            IsDestructive = true,
            Details = [new UntrustedText(forgery)],
        };

        var view = await Realize(new ConfirmationDialogView(request));

        var panel = view.GetVisualDescendants().OfType<Border>().SingleOrDefault(b => b.Name == "PART_CodecWarnings");
        Assert.NotNull(panel);

        // The framework's own title and accept label are untouched plain strings,
        // never sourced from the forged text.
        var title = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Title");
        Assert.Equal("Overwrite save file", title.Text);
        Assert.Equal("Overwrite save file", view.AcceptButton.Content);

        // The warning text itself never contains a real line break once rendered.
        var warningLines = panel!.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        Assert.NotEmpty(warningLines);
        foreach (var line in warningLines)
        {
            var text = string.Concat(line.Inlines?.Select(i => (i as Avalonia.Controls.Documents.Run)?.Text) ?? []);
            Assert.DoesNotContain('\n', text);
        }
    }

    [AvaloniaFact]
    public async Task No_Details_Means_No_Codec_Warnings_Region_Is_Built()
    {
        var request = new ConfirmationRequest
        {
            Title = "Continue",
            Message = "Message",
            AcceptLabel = "Continue",
        };

        var view = await Realize(new ConfirmationDialogView(request));

        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b => b.Name == "PART_CodecWarnings");
    }
}

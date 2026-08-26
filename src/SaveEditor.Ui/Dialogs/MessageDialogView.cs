using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.Dialogs;

/// <summary>
/// The themed content of a no-choice message dialog, separated from the window
/// that hosts it so it can be exercised headlessly.
/// </summary>
/// <remarks>
/// <see cref="MessageRequest.Title"/> and <see cref="MessageRequest.Message"/> are
/// framework- or editor-owned plain strings. <see cref="MessageRequest.Details"/> is
/// codec-supplied and renders through <see cref="CodecWarningsPanel"/> in a visually
/// distinct region instead.
/// </remarks>
public sealed class MessageDialogView : ContentControl
{
    /// <summary>Builds the view for one message.</summary>
    /// <param name="request">What to show.</param>
    public MessageDialogView(MessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        CloseButton = new Button
        {
            Name = "PART_CloseButton",
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Classes = { "accent" },
        };
        AutomationProperties.SetName(CloseButton, "Close");

        var title = new TextBlock
        {
            Name = "PART_Title",
            Text = request.Title,
            FontWeight = FontWeight.Bold,
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
        };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("Foreground"));

        var message = new TextBlock
        {
            Name = "PART_Message",
            Text = request.Message,
            TextWrapping = TextWrapping.Wrap,
        };
        message.Bind(TextBlock.ForegroundProperty, message.GetResourceObservable("Foreground"));

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(title);
        body.Children.Add(message);

        var warnings = CodecWarningsPanel.TryBuild(request.Details ?? []);
        if (warnings is not null)
        {
            body.Children.Add(warnings);
        }

        body.Children.Add(CloseButton);

        var surface = new Border { Padding = new Thickness(20), Child = body };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("WindowBackground"));

        Content = surface;
    }

    /// <summary>The button that dismisses the message. The host attaches its own <c>Click</c> handler.</summary>
    public Button CloseButton { get; }
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.Dialogs;

/// <summary>
/// The themed content of a no-choice message dialog, separated from the window
/// that hosts it so it can be exercised headlessly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MessageRequest.Title"/> and <see cref="MessageRequest.Message"/> are
/// framework- or editor-owned plain strings. <see cref="MessageRequest.Details"/> is
/// codec-supplied and renders through <see cref="CodecWarningsPanel"/> in a visually
/// distinct region instead.
/// </para>
/// <para>
/// The body scrolls inside a bounded height. Help → About and Help → Safety both
/// send adopter-authored text through this view, and that text is routinely longer
/// than a display. Title and Close are docked outside the scroller so the close
/// affordance stays in the window when the host is capped to a short working area.
/// </para>
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

        var scrollBody = new StackPanel { Spacing = 12 };
        scrollBody.Children.Add(message);

        var warnings = CodecWarningsPanel.TryBuild(request.Details ?? []);
        if (warnings is not null)
        {
            scrollBody.Children.Add(warnings);
        }

        var scroller = new ScrollViewer
        {
            Name = "PART_BodyScroller",
            Content = scrollBody,
            MaxHeight = DialogHostBounds.DefaultBodyMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        title.Margin = new Thickness(0, 0, 0, 12);
        CloseButton.Margin = new Thickness(0, 12, 0, 0);

        var dock = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(CloseButton, Dock.Bottom);
        dock.Children.Add(title);
        dock.Children.Add(CloseButton);
        dock.Children.Add(scroller);

        var surface = new Border { Padding = new Thickness(20), Child = dock };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("WindowBackground"));

        Content = surface;
    }

    /// <summary>The button that dismisses the message. The host attaches its own <c>Click</c> handler.</summary>
    public Button CloseButton { get; }
}

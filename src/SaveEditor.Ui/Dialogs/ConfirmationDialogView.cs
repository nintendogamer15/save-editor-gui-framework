using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SaveEditor.Ui.Display;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.Dialogs;

/// <summary>
/// The themed content of a confirmation dialog, separated from the window that
/// hosts it so it can be exercised headlessly without driving a modal message pump.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AcceptButton"/> and <see cref="CancelButton"/> are exposed rather than
/// wired to a private result field, so a host (or a test) attaches its own
/// <c>Click</c> handlers, and a test can invoke them directly via
/// <c>RaiseEvent</c>/<c>Click()</c> without needing a real window message loop.
/// </para>
/// <para>
/// <see cref="ConfirmationRequest.Title"/>, <see cref="ConfirmationRequest.Message"/>,
/// and <see cref="ConfirmationRequest.AcceptLabel"/> are framework- or editor-owned
/// plain strings and render as ordinary chrome text. <see cref="ConfirmationRequest.Details"/>
/// is codec-supplied and renders through <see cref="CodecWarningsPanel"/> in a
/// visually distinct region instead.
/// </para>
/// <para>
/// Message, path, and warnings scroll inside a bounded height so a long codec
/// report cannot push Accept and Cancel off the screen. The buttons stay outside
/// the scroller.
/// </para>
/// </remarks>
public sealed class ConfirmationDialogView : ContentControl
{
    /// <summary>Builds the view for one confirmation request.</summary>
    /// <param name="request">What the user is being asked to accept or decline.</param>
    /// <param name="targetPath">
    /// The location the action would affect, already formatted by
    /// <see cref="PathDisplayFormatter"/>. When supplied, it renders as its own row,
    /// wrapped rather than trimmed, with <see cref="PathLabel.FullLabel"/> on both
    /// the tooltip and the accessible description.
    /// </param>
    public ConfirmationDialogView(ConfirmationRequest request, PathLabel? targetPath = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        AcceptButton = new Button
        {
            Name = "PART_AcceptButton",
            Content = request.AcceptLabel,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AcceptButton.Classes.Add(request.IsDestructive ? "danger" : "accent");
        AutomationProperties.SetName(AcceptButton, request.AcceptLabel);

        CancelButton = new Button
        {
            Name = "PART_CancelButton",
            Content = request.CancelLabel,
        };
        AutomationProperties.SetName(CancelButton, request.CancelLabel);

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

        if (targetPath is { IsEmpty: false })
        {
            scrollBody.Children.Add(BuildTargetPathRow(targetPath));
        }

        var warnings = CodecWarningsPanel.TryBuild(request.Details);
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

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(AcceptButton);

        title.Margin = new Thickness(0, 0, 0, 12);
        buttons.Margin = new Thickness(0, 12, 0, 0);

        var dock = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(title);
        dock.Children.Add(buttons);
        dock.Children.Add(scroller);

        var surface = new Border { Padding = new Thickness(20), Child = dock };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("WindowBackground"));

        Content = surface;
    }

    /// <summary>The button that accepts the request. The host attaches its own <c>Click</c> handler.</summary>
    public Button AcceptButton { get; }

    /// <summary>The button that declines the request. The host attaches its own <c>Click</c> handler.</summary>
    public Button CancelButton { get; }

    private static Control BuildTargetPathRow(PathLabel targetPath)
    {
        var caption = new TextBlock { Text = "Target:", FontWeight = FontWeight.SemiBold };
        caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("MutedForeground"));

        // Wrapped, never trimmed: the plan requires the final two path components
        // stay visible even when the label overruns its budget, and a text-trimmed
        // surface would clip exactly the filename that is about to be overwritten.
        var value = new TextBlock
        {
            Name = "PART_TargetPath",
            Text = targetPath.Label,
            TextWrapping = TextWrapping.Wrap,
        };
        value.Bind(TextBlock.ForegroundProperty, value.GetResourceObservable("Foreground"));
        ToolTip.SetTip(value, targetPath.FullLabel);
        AutomationProperties.SetName(value, "Target file");
        AutomationProperties.SetHelpText(value, targetPath.FullLabel);

        var row = new StackPanel { Spacing = 2 };
        row.Children.Add(caption);
        row.Children.Add(value);
        return row;
    }
}

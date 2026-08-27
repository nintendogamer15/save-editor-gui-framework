using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using SaveEditor.Ui.Display;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.Dialogs;

/// <summary>A themed, read-only, scrollable viewer for a block of text.</summary>
/// <remarks>
/// <para>
/// Ships as part of the default <see cref="IUserInteraction"/> implementation for
/// showing longer text a confirmation or message dialog is too small for -- a full
/// validation report, a license file, or similar. Raw and advanced editor-owned
/// data presentation stays the editor's responsibility per <c>PLAN.md</c> §10; this
/// is a reusable shell for it, not a data browser.
/// </para>
/// <para>
/// <see cref="DocumentText"/> always passes through the shared
/// <see cref="DisplayTextNeutralizer"/> before it renders, regardless of whether the
/// caller believes the content is trusted. The cost of neutralizing trusted text is
/// negligible, and a viewer that skips it for content the caller merely assumed was
/// safe would be a second, unguarded path into the same hazard the codec-warning
/// region exists to close.
/// </para>
/// </remarks>
public sealed class DocumentViewerContent : TemplatedControl
{
    /// <summary>Identifies the <see cref="Title"/> property.</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DocumentViewerContent, string?>(nameof(Title));

    /// <summary>Identifies the <see cref="DocumentText"/> property.</summary>
    public static readonly StyledProperty<string?> DocumentTextProperty =
        AvaloniaProperty.Register<DocumentViewerContent, string?>(nameof(DocumentText));

    private TextBlock? _bodyBlock;

    static DocumentViewerContent()
    {
        TemplateProperty.OverrideDefaultValue<DocumentViewerContent>(
            new FuncControlTemplate<DocumentViewerContent>(BuildTemplate));

        DocumentTextProperty.Changed.AddClassHandler<DocumentViewerContent>((view, _) => view.ApplyDocumentText());
    }

    /// <summary>The dialog title, shown above the document body.</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The document text to display. Neutralized before it renders.</summary>
    public string? DocumentText
    {
        get => GetValue(DocumentTextProperty);
        set => SetValue(DocumentTextProperty, value);
    }

    /// <summary>Raised when the built-in close button is activated.</summary>
    public event EventHandler? CloseRequested;

    private void ApplyDocumentText()
    {
        if (_bodyBlock is null)
        {
            return;
        }

        _bodyBlock.Text = DisplayTextNeutralizer.Neutralize(DocumentText ?? string.Empty, out _);
    }

    private static Control BuildTemplate(DocumentViewerContent control, INameScope scope)
    {
        var titleBlock = new TextBlock
        {
            Name = "PART_Title",
            FontWeight = FontWeight.Bold,
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
        };
        titleBlock.Bind(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = control });
        titleBlock.Bind(TextBlock.ForegroundProperty, titleBlock.GetResourceObservable("Foreground"));

        var bodyBlock = new SelectableTextBlock
        {
            Name = "PART_Body",
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("monospace"),
        };
        bodyBlock.Bind(TextBlock.ForegroundProperty, bodyBlock.GetResourceObservable("Foreground"));
        AutomationProperties.SetName(bodyBlock, "Document text");

        var scroller = new ScrollViewer
        {
            Name = "PART_BodyScroller",
            Content = bodyBlock,
            MaxHeight = DialogHostBounds.DefaultBodyMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var background = new Border
        {
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            Child = scroller,
        };
        background.Bind(Border.BackgroundProperty, background.GetResourceObservable("InputBackground"));
        background.Bind(Border.BorderBrushProperty, background.GetResourceObservable("Border"));
        background.Bind(Border.CornerRadiusProperty, background.GetResourceObservable("RadiusSm"));

        var closeButton = new Button
        {
            Name = "PART_CloseButton",
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Classes = { "accent" },
        };
        AutomationProperties.SetName(closeButton, "Close");
        closeButton.Click += (_, _) => control.CloseRequested?.Invoke(control, EventArgs.Empty);

        titleBlock.Margin = new Thickness(0, 0, 0, 8);

        var dock = new DockPanel();
        DockPanel.SetDock(titleBlock, Dock.Top);
        DockPanel.SetDock(closeButton, Dock.Bottom);
        dock.Children.Add(titleBlock);
        dock.Children.Add(closeButton);
        dock.Children.Add(background);

        var surface = new Border { Padding = new Thickness(20), Child = dock };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("WindowBackground"));

        control._bodyBlock = bodyBlock;
        control.ApplyDocumentText();

        return surface;
    }
}

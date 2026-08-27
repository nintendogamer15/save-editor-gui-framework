using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.Dialogs;

/// <summary>
/// A reusable themed About/Credits dialog body with consumer slots for app
/// identity, credits, and licenses.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the plain-text Help &gt; About content a consuming shell would
/// otherwise have to build itself. Each slot accepts anything a
/// <see cref="ContentPresenter"/> can host -- a string, or a whole
/// editor-authored control tree -- so an editor can supply its own logo, version
/// text, and license list without subclassing this type.
/// </para>
/// <para>
/// All three slots are editor-authored content, not codec-supplied text, so they
/// render as ordinary chrome and are not sanitized the way
/// <see cref="UntrustedText"/> is. An editor that wants to show
/// codec-supplied text should use <see cref="DocumentViewerContent"/> instead.
/// </para>
/// <para>
/// Identity, credits, and licenses scroll inside a bounded height. Close stays
/// outside the scroller so a long licence list cannot hide the dismiss button.
/// </para>
/// </remarks>
public sealed class AboutDialogContent : TemplatedControl
{
    /// <summary>Identifies the <see cref="AppIdentity"/> property.</summary>
    public static readonly StyledProperty<object?> AppIdentityProperty =
        AvaloniaProperty.Register<AboutDialogContent, object?>(nameof(AppIdentity));

    /// <summary>Identifies the <see cref="Credits"/> property.</summary>
    public static readonly StyledProperty<object?> CreditsProperty =
        AvaloniaProperty.Register<AboutDialogContent, object?>(nameof(Credits));

    /// <summary>Identifies the <see cref="Licenses"/> property.</summary>
    public static readonly StyledProperty<object?> LicensesProperty =
        AvaloniaProperty.Register<AboutDialogContent, object?>(nameof(Licenses));

    static AboutDialogContent() =>
        TemplateProperty.OverrideDefaultValue<AboutDialogContent>(
            new FuncControlTemplate<AboutDialogContent>(BuildTemplate));

    /// <summary>The app's name, version, and other identity content.</summary>
    public object? AppIdentity
    {
        get => GetValue(AppIdentityProperty);
        set => SetValue(AppIdentityProperty, value);
    }

    /// <summary>Contributor and acknowledgement content.</summary>
    public object? Credits
    {
        get => GetValue(CreditsProperty);
        set => SetValue(CreditsProperty, value);
    }

    /// <summary>Third-party license content.</summary>
    public object? Licenses
    {
        get => GetValue(LicensesProperty);
        set => SetValue(LicensesProperty, value);
    }

    /// <summary>Raised when the built-in close button is activated.</summary>
    public event EventHandler? CloseRequested;

    private static Control BuildTemplate(AboutDialogContent control, INameScope scope)
    {
        var identityPresenter = new ContentPresenter { Name = "PART_AppIdentity" };
        identityPresenter.Bind(ContentPresenter.ContentProperty, new Binding(nameof(AppIdentity)) { Source = control });

        var creditsHeading = new TextBlock { Text = "Credits", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 4) };
        creditsHeading.Bind(TextBlock.ForegroundProperty, creditsHeading.GetResourceObservable("Foreground"));

        var creditsPresenter = new ContentPresenter { Name = "PART_Credits" };
        creditsPresenter.Bind(ContentPresenter.ContentProperty, new Binding(nameof(Credits)) { Source = control });

        var licensesHeading = new TextBlock { Text = "Licenses", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 4) };
        licensesHeading.Bind(TextBlock.ForegroundProperty, licensesHeading.GetResourceObservable("Foreground"));

        var licensesPresenter = new ContentPresenter { Name = "PART_Licenses" };
        licensesPresenter.Bind(ContentPresenter.ContentProperty, new Binding(nameof(Licenses)) { Source = control });

        var closeButton = new Button
        {
            Name = "PART_CloseButton",
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Classes = { "accent" },
        };
        AutomationProperties.SetName(closeButton, "Close");
        closeButton.Click += (_, _) => control.CloseRequested?.Invoke(control, EventArgs.Empty);

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(identityPresenter);
        body.Children.Add(creditsHeading);
        body.Children.Add(creditsPresenter);
        body.Children.Add(licensesHeading);
        body.Children.Add(licensesPresenter);

        var scroller = new ScrollViewer
        {
            Name = "PART_BodyScroller",
            Content = body,
            MaxHeight = DialogHostBounds.DefaultBodyMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var dock = new DockPanel();
        DockPanel.SetDock(closeButton, Dock.Bottom);
        dock.Children.Add(closeButton);
        dock.Children.Add(scroller);

        var surface = new Border { Padding = new Thickness(20), Child = dock };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("WindowBackground"));

        return surface;
    }
}

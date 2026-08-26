using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace SaveEditor.Ui.Dialogs;

/// <summary>
/// A persistent, accessible region for important errors and outcomes.
/// </summary>
/// <remarks>
/// <para>
/// The status bar in <c>PLAN.md</c> §10 stays the canonical outcome channel for
/// routine status. This control is for the messages that deserve more than a line
/// in that bar and must not be missed: it stays on screen until the caller clears
/// or replaces <see cref="Message"/>, rather than timing itself out the way a toast
/// would. A screen-reader user who looks away for the two seconds a toast is
/// visible loses it entirely; a persistent region does not have that failure mode.
/// </para>
/// <para>
/// Exposed to assistive technology as a live region via
/// <see cref="AutomationProperties.LiveSettingProperty"/>: <see cref="AnnouncementKind.Error"/>
/// announces assertively (interrupts), every other kind announces politely (queues
/// behind whatever is already being read).
/// </para>
/// </remarks>
public sealed class AnnouncementRegion : TemplatedControl
{
    /// <summary>Identifies the <see cref="Message"/> property.</summary>
    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<AnnouncementRegion, string?>(nameof(Message));

    /// <summary>Identifies the <see cref="Kind"/> property.</summary>
    public static readonly StyledProperty<AnnouncementKind> KindProperty =
        AvaloniaProperty.Register<AnnouncementRegion, AnnouncementKind>(nameof(Kind));

    private Border? _surface;
    private TextBlock? _messageBlock;

    static AnnouncementRegion()
    {
        TemplateProperty.OverrideDefaultValue<AnnouncementRegion>(
            new FuncControlTemplate<AnnouncementRegion>(BuildTemplate));

        KindProperty.Changed.AddClassHandler<AnnouncementRegion>((region, _) => region.ApplyKindStyling());
    }

    /// <summary>The text to announce. Empty or <see langword="null"/> hides the region.</summary>
    /// <remarks>
    /// This is framework- or editor-authored status text, not codec-supplied
    /// content. Untrusted text belongs in a confirmation's sanitized details region,
    /// never here.
    /// </remarks>
    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>How urgently <see cref="Message"/> should read. Defaults to <see cref="AnnouncementKind.Info"/>.</summary>
    public AnnouncementKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private void ApplyKindStyling()
    {
        if (_surface is null || _messageBlock is null)
        {
            return;
        }

        var (backgroundKey, foregroundKey) = Kind switch
        {
            AnnouncementKind.Success => ("SuccessBackground", "SuccessText"),
            AnnouncementKind.Warning => ("WarningBackground", "WarningText"),
            AnnouncementKind.Error => ("DangerBackground", "DangerText"),
            _ => ("PanelBackground", "Foreground"),
        };

        _surface.Bind(Border.BackgroundProperty, _surface.GetResourceObservable(backgroundKey));
        _messageBlock.Bind(TextBlock.ForegroundProperty, _messageBlock.GetResourceObservable(foregroundKey));

        AutomationProperties.SetLiveSetting(
            _messageBlock,
            Kind == AnnouncementKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
    }

    private static Control BuildTemplate(AnnouncementRegion control, INameScope scope)
    {
        var messageBlock = new TextBlock
        {
            Name = "PART_Message",
            TextWrapping = TextWrapping.Wrap,
        };
        messageBlock.Bind(TextBlock.TextProperty, new Binding(nameof(Message)) { Source = control });
        AutomationProperties.SetName(messageBlock, "Announcement");

        var surface = new Border
        {
            Name = "PART_Surface",
            Padding = new Thickness(12, 8),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = messageBlock,
        };
        surface.Bind(Border.BorderBrushProperty, surface.GetResourceObservable("Border"));
        surface.Bind(Border.CornerRadiusProperty, surface.GetResourceObservable("RadiusSm"));
        surface.Bind(
            Visual.IsVisibleProperty,
            new Binding(nameof(Message)) { Source = control, Converter = StringNotEmptyConverter.Instance });

        control._surface = surface;
        control._messageBlock = messageBlock;
        control.ApplyKindStyling();

        return surface;
    }

    private sealed class StringNotEmptyConverter : IValueConverter
    {
        public static readonly StringNotEmptyConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            !string.IsNullOrEmpty(value as string);

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

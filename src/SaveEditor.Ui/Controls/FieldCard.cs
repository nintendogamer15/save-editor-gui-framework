using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.Controls;

/// <summary>
/// Presents one <see cref="Editing.FieldViewModel"/>: label, document path, help
/// and warning text, the validation message, the pending-edit indicator, and the
/// per-field Apply action.
/// </summary>
/// <remarks>
/// <para>
/// The editor surface itself is a <see cref="ContentPresenter"/> whose template is
/// chosen per field type from <see cref="TextEditorTemplate"/>,
/// <see cref="NumericEditorTemplate"/>, <see cref="BooleanEditorTemplate"/>,
/// <see cref="ChoiceEditorTemplate"/>, and <see cref="ReadOnlyEditorTemplate"/>. A
/// consuming editor supplies a custom editor for a field type by assigning its own
/// <see cref="IDataTemplate"/> to the matching property — never by subclassing
/// <see cref="FieldCard"/>. Everything around the editor surface — label, path,
/// help/warning text, the validation message, the pending indicator, and Apply —
/// stays framework-owned regardless of which template is in use.
/// </para>
/// <para>
/// <see cref="Field"/> and <see cref="StyledElement.DataContext"/> are kept in
/// sync in both directions. Setting <see cref="Field"/> directly works for a
/// standalone card; when a card is realized by <see cref="FieldList"/>'s
/// virtualizing repeater, the recycler assigns <see cref="StyledElement.DataContext"/>
/// on reuse rather than rebuilding the card, so the card has to pick that up too.
/// </para>
/// </remarks>
public sealed class FieldCard : TemplatedControl
{
    /// <summary>Identifies the <see cref="Field"/> property.</summary>
    public static readonly StyledProperty<FieldViewModel?> FieldProperty =
        AvaloniaProperty.Register<FieldCard, FieldViewModel?>(nameof(Field));

    /// <summary>Identifies the <see cref="TextEditorTemplate"/> property.</summary>
    public static readonly StyledProperty<IDataTemplate?> TextEditorTemplateProperty =
        AvaloniaProperty.Register<FieldCard, IDataTemplate?>(nameof(TextEditorTemplate));

    /// <summary>Identifies the <see cref="NumericEditorTemplate"/> property.</summary>
    public static readonly StyledProperty<IDataTemplate?> NumericEditorTemplateProperty =
        AvaloniaProperty.Register<FieldCard, IDataTemplate?>(nameof(NumericEditorTemplate));

    /// <summary>Identifies the <see cref="BooleanEditorTemplate"/> property.</summary>
    public static readonly StyledProperty<IDataTemplate?> BooleanEditorTemplateProperty =
        AvaloniaProperty.Register<FieldCard, IDataTemplate?>(nameof(BooleanEditorTemplate));

    /// <summary>Identifies the <see cref="ChoiceEditorTemplate"/> property.</summary>
    public static readonly StyledProperty<IDataTemplate?> ChoiceEditorTemplateProperty =
        AvaloniaProperty.Register<FieldCard, IDataTemplate?>(nameof(ChoiceEditorTemplate));

    /// <summary>Identifies the <see cref="ReadOnlyEditorTemplate"/> property.</summary>
    public static readonly StyledProperty<IDataTemplate?> ReadOnlyEditorTemplateProperty =
        AvaloniaProperty.Register<FieldCard, IDataTemplate?>(nameof(ReadOnlyEditorTemplate));

    /// <summary>Identifies the <see cref="EffectiveEditorTemplate"/> property.</summary>
    public static readonly StyledProperty<IDataTemplate?> EffectiveEditorTemplateProperty =
        AvaloniaProperty.Register<FieldCard, IDataTemplate?>(nameof(EffectiveEditorTemplate));

    private bool _syncingFromField;
    private bool _syncingFromDataContext;

    static FieldCard()
    {
        TemplateProperty.OverrideDefaultValue<FieldCard>(new FuncControlTemplate<FieldCard>(BuildTemplate));

        FieldProperty.Changed.AddClassHandler<FieldCard>((card, _) => card.OnFieldPropertyChanged());
        TextEditorTemplateProperty.Changed.AddClassHandler<FieldCard>((card, _) => card.UpdateEffectiveTemplate());
        NumericEditorTemplateProperty.Changed.AddClassHandler<FieldCard>((card, _) => card.UpdateEffectiveTemplate());
        BooleanEditorTemplateProperty.Changed.AddClassHandler<FieldCard>((card, _) => card.UpdateEffectiveTemplate());
        ChoiceEditorTemplateProperty.Changed.AddClassHandler<FieldCard>((card, _) => card.UpdateEffectiveTemplate());
        ReadOnlyEditorTemplateProperty.Changed.AddClassHandler<FieldCard>((card, _) => card.UpdateEffectiveTemplate());
        DataContextProperty.Changed.AddClassHandler<FieldCard>((card, e) => card.OnDataContextPropertyChanged(e.NewValue));
    }

    /// <summary>The field this card presents.</summary>
    public FieldViewModel? Field
    {
        get => GetValue(FieldProperty);
        set => SetValue(FieldProperty, value);
    }

    /// <summary>Editor template used when <see cref="Field"/> is a text field.</summary>
    public IDataTemplate? TextEditorTemplate
    {
        get => GetValue(TextEditorTemplateProperty);
        set => SetValue(TextEditorTemplateProperty, value);
    }

    /// <summary>Editor template used when <see cref="Field"/> is a numeric field.</summary>
    public IDataTemplate? NumericEditorTemplate
    {
        get => GetValue(NumericEditorTemplateProperty);
        set => SetValue(NumericEditorTemplateProperty, value);
    }

    /// <summary>Editor template used when <see cref="Field"/> is a boolean field.</summary>
    public IDataTemplate? BooleanEditorTemplate
    {
        get => GetValue(BooleanEditorTemplateProperty);
        set => SetValue(BooleanEditorTemplateProperty, value);
    }

    /// <summary>Editor template used when <see cref="Field"/> is a choice field.</summary>
    public IDataTemplate? ChoiceEditorTemplate
    {
        get => GetValue(ChoiceEditorTemplateProperty);
        set => SetValue(ChoiceEditorTemplateProperty, value);
    }

    /// <summary>Editor template used when <see cref="Field"/> is a read-only field.</summary>
    public IDataTemplate? ReadOnlyEditorTemplate
    {
        get => GetValue(ReadOnlyEditorTemplateProperty);
        set => SetValue(ReadOnlyEditorTemplateProperty, value);
    }

    /// <summary>
    /// The template actually in effect for <see cref="Field"/>'s runtime type,
    /// recomputed whenever <see cref="Field"/> or one of the five typed template
    /// properties changes.
    /// </summary>
    public IDataTemplate? EffectiveEditorTemplate => GetValue(EffectiveEditorTemplateProperty);

    private void OnFieldPropertyChanged()
    {
        if (!_syncingFromDataContext)
        {
            _syncingFromField = true;
            SetCurrentValue(DataContextProperty, Field);
            _syncingFromField = false;
        }

        UpdateEffectiveTemplate();
    }

    private void OnDataContextPropertyChanged(object? newValue)
    {
        if (_syncingFromField)
        {
            return;
        }

        // A virtualizing repeater recycles the card and only ever resets
        // DataContext, so Field has to be driven from here too, not just from
        // callers that set Field directly.
        if (newValue is FieldViewModel or null)
        {
            _syncingFromDataContext = true;
            SetCurrentValue(FieldProperty, newValue as FieldViewModel);
            _syncingFromDataContext = false;
        }
    }

    private void UpdateEffectiveTemplate()
    {
        // Each typed slot falls back to the framework default when unset, rather
        // than the default living as per-type StyledProperty metadata: overriding
        // StyledProperty defaults is a once-only, owner-type-scoped operation, and
        // a fallback here is just as effective without that constraint.
        IDataTemplate? template = Field switch
        {
            TextFieldViewModel => TextEditorTemplate ?? FieldEditorTemplates.Text,
            NumericFieldViewModel => NumericEditorTemplate ?? FieldEditorTemplates.Numeric,
            BooleanFieldViewModel => BooleanEditorTemplate ?? FieldEditorTemplates.Boolean,
            ChoiceFieldViewModel => ChoiceEditorTemplate ?? FieldEditorTemplates.Choice,
            ReadOnlyFieldViewModel => ReadOnlyEditorTemplate ?? FieldEditorTemplates.ReadOnly,
            _ => null,
        };

        SetCurrentValue(EffectiveEditorTemplateProperty, template);
    }

    private static Control BuildTemplate(FieldCard control, INameScope scope)
    {
        var label = new TextBlock { FontWeight = FontWeight.SemiBold };
        label.Bind(TextBlock.TextProperty, new Binding(nameof(FieldViewModel.Label)));
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("Foreground"));

        var path = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        path.Bind(TextBlock.TextProperty, new Binding(nameof(FieldViewModel.Path)));
        path.Bind(
            TextBlock.IsVisibleProperty,
            new Binding(nameof(FieldViewModel.Path)) { Converter = StringNotEmpty.Instance });
        path.Bind(TextBlock.ForegroundProperty, path.GetResourceObservable("SubtleForeground"));

        // The pending indicator carries both a glyph and the word "Pending" so it
        // reads without relying on colour, satisfying the accessibility gate.
        var pendingIndicator = new TextBlock
        {
            Text = "● Pending",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        pendingIndicator.Bind(TextBlock.IsVisibleProperty, new Binding(nameof(FieldViewModel.HasPendingEdit)));
        pendingIndicator.Bind(TextBlock.ForegroundProperty, pendingIndicator.GetResourceObservable("PrimaryText"));
        AutomationProperties.SetName(pendingIndicator, "Pending edit");

        var headerText = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerText.Children.Add(label);
        headerText.Children.Add(path);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(headerText, 0);
        Grid.SetColumn(pendingIndicator, 1);
        header.Children.Add(headerText);
        header.Children.Add(pendingIndicator);

        var editorPresenter = new ContentPresenter { Name = "PART_EditorPresenter" };
        editorPresenter.Bind(ContentPresenter.ContentProperty, new Binding(nameof(Field)) { Source = control });
        editorPresenter.Bind(
            ContentPresenter.ContentTemplateProperty,
            new Binding(nameof(EffectiveEditorTemplate)) { Source = control });

        var helpText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        helpText.Bind(TextBlock.TextProperty, new Binding(nameof(FieldViewModel.HelpText)));
        helpText.Bind(
            TextBlock.IsVisibleProperty,
            new Binding(nameof(FieldViewModel.HelpText)) { Converter = StringNotEmpty.Instance });
        helpText.Bind(TextBlock.ForegroundProperty, helpText.GetResourceObservable("MutedForeground"));

        var warningText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        warningText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(FieldViewModel.WarningText)) { StringFormat = "⚠ {0}" });
        warningText.Bind(
            TextBlock.IsVisibleProperty,
            new Binding(nameof(FieldViewModel.WarningText)) { Converter = StringNotEmpty.Instance });
        warningText.Bind(TextBlock.ForegroundProperty, warningText.GetResourceObservable("WarningText"));

        // The validation message is also exposed as AutomationProperties.HelpText on
        // the actual input control built by each editor template, so assistive
        // technology finds it attached to the input rather than only nearby in the
        // visual layout.
        var validationText = new TextBlock { Name = "PART_ValidationMessage", TextWrapping = TextWrapping.Wrap };
        validationText.Bind(TextBlock.TextProperty, new Binding(nameof(FieldViewModel.ValidationError)));
        validationText.Bind(
            TextBlock.IsVisibleProperty,
            new Binding(nameof(FieldViewModel.ValidationError)) { Converter = StringNotEmpty.Instance });
        validationText.Bind(TextBlock.ForegroundProperty, validationText.GetResourceObservable("DangerText"));

        var applyButton = new Button
        {
            Name = "PART_ApplyButton",
            Content = "Apply",
            HorizontalAlignment = HorizontalAlignment.Right,
            Classes = { "accent" },
        };
        applyButton.Bind(Button.CommandProperty, new Binding(nameof(FieldViewModel.ApplyCommand)));
        applyButton.Bind(Button.IsEnabledProperty, new Binding(nameof(FieldViewModel.CanApply)));
        applyButton.Bind(
            AutomationProperties.NameProperty,
            new Binding(nameof(FieldViewModel.Label)) { StringFormat = "Apply {0}" });

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(header);
        body.Children.Add(editorPresenter);
        body.Children.Add(helpText);
        body.Children.Add(warningText);
        body.Children.Add(validationText);
        body.Children.Add(applyButton);

        var card = new Border
        {
            Name = "PART_Card",
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1),
            Child = body,
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("CardBackground"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("Border"));
        card.Bind(Border.CornerRadiusProperty, card.GetResourceObservable("RadiusMd"));

        return card;
    }

    private sealed class StringNotEmpty : IValueConverter
    {
        public static readonly StringNotEmpty Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            !string.IsNullOrEmpty(value as string);

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.Controls;

/// <summary>
/// Presents a section's fields — typically <see cref="SectionEditor.VisibleFields"/>
/// — as a column of <see cref="FieldCard"/> instances, virtualized by default.
/// </summary>
/// <remarks>
/// Built on <see cref="ListBox"/> with its items panel forced to
/// <see cref="VirtualizingStackPanel"/>, which realizes and lays out only the cards
/// inside the current viewport rather than every field up front — the panel and
/// its owning <see cref="ScrollViewer"/> cooperate through logical scrolling to
/// know what is actually visible. A plain <see cref="StackPanel"/> inside a
/// <see cref="ScrollViewer"/> would realize the whole section immediately
/// regardless of what is visible, which is exactly what this control exists to
/// avoid on a section with hundreds or thousands of fields. Selection is unused:
/// a field list is a form, not a picker. The item container is a blank presenter
/// that never paints selected or pointer-over fills — the card is the only visual.
/// </remarks>
public sealed class FieldList : TemplatedControl
{
    /// <summary>Identifies the <see cref="Fields"/> property.</summary>
    public static readonly StyledProperty<IEnumerable<FieldViewModel>?> FieldsProperty =
        AvaloniaProperty.Register<FieldList, IEnumerable<FieldViewModel>?>(nameof(Fields));

    private static readonly IDataTemplate CardTemplate =
        new FuncDataTemplate<FieldViewModel>((_, _) => new FieldCard());

    private static readonly ITemplate<Panel?> VirtualizingPanelTemplate =
        new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());

    /// <summary>
    /// Replaces Fluent's <see cref="ListBoxItem"/> theme, whose selected and
    /// pointer-over states fill <c>PART_ContentPresenter</c> with the accent.
    /// </summary>
    private static readonly ControlTheme PassiveItemTheme = BuildPassiveItemTheme();

    static FieldList() =>
        TemplateProperty.OverrideDefaultValue<FieldList>(new FuncControlTemplate<FieldList>(BuildTemplate));

    /// <summary>The fields to present, in display order.</summary>
    public IEnumerable<FieldViewModel>? Fields
    {
        get => GetValue(FieldsProperty);
        set => SetValue(FieldsProperty, value);
    }

    private static Control BuildTemplate(FieldList control, INameScope scope)
    {
        // ListBox is here for VirtualizingStackPanel, not for picking a row.
        var list = new ListBox
        {
            Name = "PART_List",
            ItemTemplate = CardTemplate,
            ItemsPanel = VirtualizingPanelTemplate,
            ItemContainerTheme = PassiveItemTheme,
            Background = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
        };
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Fields)) { Source = control });

        return list;
    }

    private static ControlTheme BuildPassiveItemTheme()
    {
        var theme = new ControlTheme(typeof(ListBoxItem));
        // Fluent's ListBoxItemPadding. Kept so resting layout (and the editing
        // screenshot baselines) stay put; this theme exists to kill the fills.
        theme.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 9, 12, 12)));
        theme.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        theme.Setters.Add(new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        theme.Setters.Add(new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ListBoxItem>(BuildItemTemplate)));
        return theme;
    }

    private static Control BuildItemTemplate(ListBoxItem item, INameScope _)
    {
        var presenter = new ContentPresenter
        {
            Name = "PART_ContentPresenter",
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        presenter.Bind(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content)) { Source = item });
        presenter.Bind(
            ContentPresenter.ContentTemplateProperty,
            new Binding(nameof(ContentControl.ContentTemplate)) { Source = item });
        presenter.Bind(ContentPresenter.PaddingProperty, new Binding(nameof(TemplatedControl.Padding)) { Source = item });
        return presenter;
    }
}

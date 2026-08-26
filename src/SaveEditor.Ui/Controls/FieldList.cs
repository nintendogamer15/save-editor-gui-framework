using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
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
/// avoid on a section with hundreds or thousands of fields. Selection is unused;
/// the list exists to present fields, not to let one be picked.
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
        var list = new ListBox
        {
            Name = "PART_List",
            ItemTemplate = CardTemplate,
            ItemsPanel = VirtualizingPanelTemplate,
            SelectionMode = SelectionMode.Single,
            Background = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
        };
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Fields)) { Source = control });

        return list;
    }
}

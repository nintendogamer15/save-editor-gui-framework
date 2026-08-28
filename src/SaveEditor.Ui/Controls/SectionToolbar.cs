using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.Controls;

/// <summary>
/// Chrome for one section's editing surface: a search box bound to
/// <see cref="Editing.SectionEditor.SearchText"/>, a pending-count summary, Apply
/// All, Revert All, and a slot for editor-supplied bulk actions and compatibility
/// toggles.
/// </summary>
/// <remarks>
/// The framework owns the chrome and wires search, the summary, and the two
/// commands straight to <see cref="Editor"/>. What a bulk action actually does, or
/// what a compatibility toggle actually gates, is the consuming editor's to define
/// — supplied through <see cref="BulkActions"/> rather than by subclassing.
/// </remarks>
public sealed class SectionToolbar : TemplatedControl
{
    /// <summary>Identifies the <see cref="Editor"/> property.</summary>
    public static readonly StyledProperty<SectionEditor?> EditorProperty =
        AvaloniaProperty.Register<SectionToolbar, SectionEditor?>(nameof(Editor));

    /// <summary>Identifies the <see cref="BulkActions"/> property.</summary>
    public static readonly StyledProperty<object?> BulkActionsProperty =
        AvaloniaProperty.Register<SectionToolbar, object?>(nameof(BulkActions));

    static SectionToolbar()
    {
        TemplateProperty.OverrideDefaultValue<SectionToolbar>(new FuncControlTemplate<SectionToolbar>(BuildTemplate));
        EditorProperty.Changed.AddClassHandler<SectionToolbar>(
            (toolbar, e) => toolbar.SetCurrentValue(DataContextProperty, e.NewValue));
    }

    /// <summary>The section this toolbar controls.</summary>
    public SectionEditor? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>
    /// Editor-supplied bulk actions and compatibility toggles, placed after the
    /// framework's own search box and buttons.
    /// </summary>
    public object? BulkActions
    {
        get => GetValue(BulkActionsProperty);
        set => SetValue(BulkActionsProperty, value);
    }

    private static Control BuildTemplate(SectionToolbar control, INameScope scope)
    {
        var search = new TextBox
        {
            Name = "PART_SearchBox",
            PlaceholderText = "Search fields",
            MinWidth = 220,
        };
        search.Bind(TextBox.TextProperty, new Binding(nameof(SectionEditor.SearchText)) { Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(search, "Search fields");

        var summary = new TextBlock { Name = "PART_PendingSummary", VerticalAlignment = VerticalAlignment.Center };
        summary.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(SectionEditor.PendingCount)) { StringFormat = "{0} pending" });
        summary.Bind(TextBlock.ForegroundProperty, summary.GetResourceObservable("MutedForeground"));

        var applyAll = new Button { Name = "PART_ApplyAllButton", Content = "Apply All", Classes = { "accent" } };
        applyAll.Bind(Button.CommandProperty, new Binding(nameof(SectionEditor.ApplyAllCommand)));
        applyAll.Bind(Button.IsEnabledProperty, new Binding(nameof(SectionEditor.CanApplyAll)));
        AutomationProperties.SetName(applyAll, "Apply all");

        var revertAll = new Button { Name = "PART_RevertAllButton", Content = "Revert All" };
        revertAll.Bind(Button.CommandProperty, new Binding(nameof(SectionEditor.RevertAllCommand)));
        revertAll.Bind(Button.IsEnabledProperty, new Binding(nameof(SectionEditor.HasPendingEdits)));
        AutomationProperties.SetName(revertAll, "Revert all");

        var bulkActions = new ContentPresenter { Name = "PART_BulkActions" };
        bulkActions.Bind(ContentPresenter.ContentProperty, new Binding(nameof(BulkActions)) { Source = control });

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(search);
        content.Children.Add(summary);
        content.Children.Add(applyAll);
        content.Children.Add(revertAll);
        content.Children.Add(bulkActions);

        // The same dangling-card treatment FieldCard.PART_Card uses, and for the same
        // reason: the toolbar sits directly above the cards it drives, so a different
        // surface and square corners read as a separate strip bolted onto the section
        // rather than as the top of one card-based surface. CardBackground and RadiusMd
        // are the semantic tokens the cards resolve, so the two cannot drift apart when
        // a theme changes what those tokens mean.
        var surface = new Border
        {
            Name = "PART_ToolbarSurface",
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("CardBackground"));
        surface.Bind(Border.BorderBrushProperty, surface.GetResourceObservable("Border"));
        surface.Bind(Border.CornerRadiusProperty, surface.GetResourceObservable("RadiusMd"));

        return surface;
    }
}

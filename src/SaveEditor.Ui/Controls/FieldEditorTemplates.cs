using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.Controls;

/// <summary>
/// The framework's default editor surface for each field type. Consumers override
/// one of these by assigning their own template to the matching property on
/// <see cref="FieldCard"/>, never by editing these.
/// </summary>
internal static class FieldEditorTemplates
{
    /// <summary>Default editor for <see cref="TextFieldViewModel"/>.</summary>
    public static readonly IDataTemplate Text =
        new FuncDataTemplate<TextFieldViewModel>((vm, _) => BuildText(vm));

    /// <summary>Default editor for <see cref="NumericFieldViewModel"/>.</summary>
    public static readonly IDataTemplate Numeric =
        new FuncDataTemplate<NumericFieldViewModel>((vm, _) => BuildNumeric(vm));

    /// <summary>Default editor for <see cref="BooleanFieldViewModel"/>.</summary>
    public static readonly IDataTemplate Boolean =
        new FuncDataTemplate<BooleanFieldViewModel>((vm, _) => BuildBoolean(vm));

    /// <summary>Default editor for <see cref="ChoiceFieldViewModel"/>.</summary>
    public static readonly IDataTemplate Choice =
        new FuncDataTemplate<ChoiceFieldViewModel>((vm, _) => BuildChoice(vm));

    /// <summary>Default editor for <see cref="ReadOnlyFieldViewModel"/>.</summary>
    public static readonly IDataTemplate ReadOnly =
        new FuncDataTemplate<ReadOnlyFieldViewModel>((vm, _) => BuildReadOnly(vm));

    private static Control BuildText(TextFieldViewModel vm)
    {
        var box = new TextBox();
        box.Bind(TextBox.TextProperty, new Binding(nameof(TextFieldViewModel.Draft)) { Source = vm, Mode = BindingMode.TwoWay });
        Wire(box, vm);
        return box;
    }

    private static Control BuildNumeric(NumericFieldViewModel vm)
    {
        var box = new TextBox();
        box.Bind(TextBox.TextProperty, new Binding(nameof(NumericFieldViewModel.Text)) { Source = vm, Mode = BindingMode.TwoWay });
        Wire(box, vm);

        if (!vm.ShowSpinner)
        {
            return box;
        }

        // The descriptor advertises this affordance, so it has to exist. A property
        // that silently does nothing is worse than no property.
        var down = new Button
        {
            Content = "−",
            Command = vm.DecrementCommand,
            Width = 32,
        };
        Avalonia.Automation.AutomationProperties.SetName(down, $"Decrease {vm.Label}");

        var up = new Button
        {
            Content = "+",
            Command = vm.IncrementCommand,
            Width = 32,
        };
        Avalonia.Automation.AutomationProperties.SetName(up, $"Increase {vm.Label}");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        box.Width = 160;
        row.Children.Add(box);
        row.Children.Add(down);
        row.Children.Add(up);

        return row;
    }

    private static Control BuildBoolean(BooleanFieldViewModel vm)
    {
        var check = new CheckBox { Content = "Enabled" };
        check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(BooleanFieldViewModel.Draft)) { Source = vm, Mode = BindingMode.TwoWay });
        Wire(check, vm);
        return check;
    }

    private static Control BuildChoice(ChoiceFieldViewModel vm)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<ChoiceOption>((option, _) => new TextBlock { Text = option.Label }),
        };
        Wire(combo, vm);

        var suppress = false;

        void SelectMatching(IReadOnlyList<ChoiceOption> options)
        {
            suppress = true;
            combo.SelectedItem = options.FirstOrDefault(o => o.Value == vm.Draft);
            suppress = false;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (suppress || combo.SelectedItem is not ChoiceOption selected)
            {
                return;
            }

            vm.Draft = selected.Value;
        };

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChoiceFieldViewModel.Draft) && combo.ItemsSource is IReadOnlyList<ChoiceOption> current)
            {
                SelectMatching(current);
            }
        };

        LoadOptionsAsync(vm, combo, SelectMatching);
        return combo;
    }

    private static async void LoadOptionsAsync(
        ChoiceFieldViewModel vm, ComboBox combo, Action<IReadOnlyList<ChoiceOption>> selectMatching)
    {
        var options = await vm.Options.GetOptionsAsync(string.Empty).ConfigureAwait(true);
        combo.ItemsSource = options;
        selectMatching(options);
    }

    private static Control BuildReadOnly(ReadOnlyFieldViewModel vm)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.TextProperty, new Binding(nameof(ReadOnlyFieldViewModel.Value)) { Source = vm });
        AutomationProperties.SetName(text, vm.Label);
        return text;
    }

    private static void Wire(Control control, FieldViewModel vm)
    {
        AutomationProperties.SetName(control, vm.Label);
        control.Bind(AutomationProperties.HelpTextProperty, new Binding(nameof(FieldViewModel.ValidationError)) { Source = vm });
        control.Bind(Control.IsEnabledProperty, new Binding(nameof(FieldViewModel.IsReadOnly)) { Source = vm, Converter = InverseBoolean.Instance });
    }

    private sealed class InverseBoolean : IValueConverter
    {
        public static readonly InverseBoolean Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is bool b ? !b : value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is bool b ? !b : value;
    }
}

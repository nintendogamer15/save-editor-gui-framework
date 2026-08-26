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

    /// <summary>Edge length of a numeric spinner button.</summary>
    internal const double SpinnerButtonSize = 32;

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
        // Padding is overridden, not inherited. The Button theme pads 14px a side
        // and draws a 1px border, which is 30px of chrome; a 32px-wide button
        // inheriting that leaves a 2px content area and clips the glyph to a
        // speck. The spinner rendered as two identical specks on both platforms
        // until someone measured the pixels.
        var down = new Button
        {
            Content = "−",
            Command = vm.DecrementCommand,
            Width = SpinnerButtonSize,
            Padding = new Avalonia.Thickness(0),
        };
        Avalonia.Automation.AutomationProperties.SetName(down, $"Decrease {vm.Label}");

        var up = new Button
        {
            Content = "+",
            Command = vm.IncrementCommand,
            Width = SpinnerButtonSize,
            Padding = new Avalonia.Thickness(0),
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

    /// <summary>Builds the choice editor: an autocomplete over the provider.</summary>
    /// <remarks>
    /// <para>
    /// This is an <see cref="AutoCompleteBox"/> rather than a dropdown because
    /// <c>IChoiceProvider</c> takes a filter and is documented as possibly
    /// asynchronous. A closed dropdown can only ever call it once with an empty
    /// filter, which makes half that contract dead — and makes
    /// <see cref="ChoiceFieldDescriptor.AllowCustomValue"/> impossible to honour,
    /// since a dropdown structurally cannot accept a value that is not in its list.
    /// </para>
    /// <para>
    /// Filtering is delegated to the provider rather than done locally: an editor
    /// resolving options from a large table or a file knows how to narrow them, and
    /// filtering client-side would require fetching everything first.
    /// </para>
    /// </remarks>
    private static Control BuildChoice(ChoiceFieldViewModel vm)
    {
        var labelToValue = new Dictionary<string, string>(StringComparer.Ordinal);

        var box = new AutoCompleteBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FilterMode = AutoCompleteFilterMode.None,
            MinimumPrefixLength = 0,
            IsTextCompletionEnabled = false,
        };

        box.AsyncPopulator = async (text, cancellationToken) =>
        {
            try
            {
                var options = await vm.Options
                    .GetOptionsAsync(text ?? string.Empty, cancellationToken)
                    .ConfigureAwait(true);

                // Accumulate rather than replace. A filtered fetch returns a subset,
                // and clearing would forget a label the user selected a keystroke
                // earlier — turning a valid choice into a rejected one purely because
                // the current filter no longer lists it.
                foreach (var option in options)
                {
                    labelToValue[option.Label] = option.Value;
                }

                return options.Select(o => (object)o.Label).ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Same containment as the initial load: a provider doing IO can fail
                // for ordinary reasons, and an unhandled async continuation would take
                // the application down over a dropdown.
                vm.ReportOptionsFailure(ex);
                return [];
            }
        };

        Wire(box, vm);

        var suppress = false;

        void PushDraftToBox()
        {
            suppress = true;
            box.Text = labelToValue.FirstOrDefault(p => p.Value == vm.Draft).Key ?? vm.Draft;
            suppress = false;
        }

        box.TextChanged += (_, _) =>
        {
            if (suppress)
            {
                return;
            }

            var text = box.Text ?? string.Empty;

            if (labelToValue.TryGetValue(text, out var value))
            {
                vm.Draft = value;
                vm.ClearChoiceError();
                return;
            }

            if (vm.AllowCustomValue)
            {
                vm.Draft = text;
                vm.ClearChoiceError();
                return;
            }

            // A closed set has to reject rather than quietly keep the last valid
            // value: silently discarding what someone typed is how a save ends up
            // holding a value they did not choose.
            vm.RejectUnlistedChoice(text);
        };

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChoiceFieldViewModel.Draft))
            {
                PushDraftToBox();
            }
        };

        PushDraftToBox();
        LoadInitialOptionsAsync(vm, box, labelToValue, PushDraftToBox);
        return box;
    }

    /// <summary>Seeds the list so the field is usable before anything is typed.</summary>
    private static async void LoadInitialOptionsAsync(
        ChoiceFieldViewModel vm,
        AutoCompleteBox box,
        Dictionary<string, string> labelToValue,
        Action pushDraft)
    {
        try
        {
            var options = await vm.Options
                .GetOptionsAsync(string.Empty, vm.OptionsCancellation)
                .ConfigureAwait(true);

            foreach (var option in options)
            {
                labelToValue[option.Label] = option.Value;
            }

            box.ItemsSource = options.Select(o => o.Label).ToList();
            pushDraft();
        }
        catch (OperationCanceledException)
        {
            // The field went away, or the document closed.
        }
        catch (Exception ex)
        {
            box.ItemsSource = Array.Empty<string>();
            vm.ReportOptionsFailure(ex);
        }
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

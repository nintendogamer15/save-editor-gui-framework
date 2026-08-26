using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Controls;
using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.HeadlessTests.Controls;

/// <summary>
/// The choice field has to honour both halves of what it promises: a filter fed to
/// the provider, and AllowCustomValue actually accepting a custom value.
/// </summary>
/// <remarks>
/// Release review found neither was true. The editor was a closed dropdown, so
/// IChoiceProvider.filter was always empty and AllowCustomValue was structurally
/// unhonourable — a property frozen into the 1.0 surface that nothing could obey.
/// </remarks>
public class ChoiceFieldTests
{
    private sealed class RecordingProvider : IChoiceProvider
    {
        public List<string> Filters { get; } = [];

        public ValueTask<IReadOnlyList<ChoiceOption>> GetOptionsAsync(
            string filter, CancellationToken cancellationToken = default)
        {
            Filters.Add(filter);

            IReadOnlyList<ChoiceOption> all =
            [
                new("normal", "Normal"),
                new("hard", "Hard"),
                new("nightmare", "Nightmare"),
            ];

            return ValueTask.FromResult(
                filter.Length == 0
                    ? all
                    : all.Where(o => o.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList());
        }
    }

    private static (ChoiceFieldViewModel Vm, RecordingProvider Provider, AutoCompleteBox Box, Window Window)
        Build(bool allowCustom)
    {
        var stored = "normal";
        var provider = new RecordingProvider();

        var vm = new ChoiceFieldViewModel(
            new ChoiceFieldDescriptor
            {
                Key = "difficulty",
                Label = "Difficulty",
                Options = provider,
                AllowCustomValue = allowCustom,
                Read = () => stored,
                Write = v => stored = v,
            },
            new EditHistory());

        var card = new FieldCard { Field = vm };
        var window = new Window { Width = 700, Height = 300, Content = card };
        window.Show();

        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        var box = card.GetVisualDescendants().OfType<AutoCompleteBox>().Single();
        return (vm, provider, box, window);
    }

    [AvaloniaFact]
    public void The_Choice_Editor_Is_An_Autocomplete_Not_A_Closed_Dropdown()
    {
        var (_, provider, box, _) = Build(allowCustom: false);

        Assert.NotNull(box);

        // The provider must have been asked at least once, so the field is usable
        // before anything is typed.
        Assert.NotEmpty(provider.Filters);
    }

    [AvaloniaFact]
    public void A_Custom_Value_Is_Accepted_When_The_Descriptor_Allows_It()
    {
        var (vm, _, box, _) = Build(allowCustom: true);

        box.Text = "Brutal";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Brutal", vm.Draft);
        Assert.True(vm.IsValid);
    }

    [AvaloniaFact]
    public void An_Unlisted_Value_Is_Rejected_When_The_Descriptor_Forbids_It()
    {
        var (vm, _, box, _) = Build(allowCustom: false);

        box.Text = "Brutal";
        Dispatcher.UIThread.RunJobs();

        // Rejected rather than silently reverted: quietly keeping the last valid value
        // is how a save ends up holding something the user did not choose.
        Assert.False(vm.IsValid);
        Assert.Contains("Brutal", vm.ValidationError, StringComparison.Ordinal);
        Assert.False(vm.CanApply);
    }

    [AvaloniaFact]
    public void Choosing_A_Listed_Label_Stores_Its_Value_Not_Its_Label()
    {
        var (vm, _, box, _) = Build(allowCustom: false);

        box.Text = "Nightmare";
        Dispatcher.UIThread.RunJobs();

        // The label is what the user reads; the value is what the save holds.
        Assert.Equal("nightmare", vm.Draft);
        Assert.True(vm.IsValid);
    }

    [AvaloniaFact]
    public void Recovering_To_A_Listed_Value_Clears_The_Rejection()
    {
        var (vm, _, box, _) = Build(allowCustom: false);

        box.Text = "Brutal";
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsValid);

        box.Text = "Hard";
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsValid);
        Assert.Equal("hard", vm.Draft);
    }
}

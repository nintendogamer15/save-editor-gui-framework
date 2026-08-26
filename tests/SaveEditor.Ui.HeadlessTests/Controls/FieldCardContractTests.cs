using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Controls;
using SaveEditor.Ui.Editing;
using SaveEditor.Ui.Shell;

namespace SaveEditor.Ui.HeadlessTests.Controls;

/// <summary>
/// The contracts §4 states about field editing that the behavioural tests do not
/// otherwise pin: custom editors by template, drafts surviving navigation, and a
/// failing option provider degrading one field rather than the application.
/// </summary>
public class FieldCardContractTests
{
    private sealed class Doc
    {
        public string Name { get; set; } = "Aerith";
    }

    private static (Doc Document, SectionEditor Section) BuildSection()
    {
        var doc = new Doc();
        var history = new EditHistory();

        var name = new TextFieldViewModel(
            new TextFieldDescriptor
            {
                Key = "name", Label = "Name",
                Read = () => doc.Name, Write = v => doc.Name = v,
            },
            history);

        return (doc, new SectionEditor("player", "Player", [name], history));
    }

    [AvaloniaFact]
    public void A_Custom_Editor_Template_Replaces_The_Default_Without_Subclassing()
    {
        // §4 requires custom editors through templates. FieldCard is sealed, so
        // subclassing is not merely discouraged - it does not compile.
        Assert.True(typeof(FieldCard).IsSealed);

        var (_, section) = BuildSection();

        var card = new FieldCard
        {
            Field = section.Fields[0],
            TextEditorTemplate = new FuncDataTemplate<TextFieldViewModel>(
                (_, _) => new Slider { Minimum = 0, Maximum = 10 }),
        };

        var window = new Window { Width = 600, Height = 300, Content = card };
        window.Show();

        Assert.Single(card.GetVisualDescendants().OfType<Slider>());
        Assert.Empty(card.GetVisualDescendants().OfType<TextBox>());
    }

    [AvaloniaFact]
    public void Pending_Drafts_Survive_Navigating_Away_And_Back_In_The_Real_Shell()
    {
        // The model test for this aliases the same object and so proves nothing about
        // navigation. This drives the shell: the section body is torn down and rebuilt.
        var (document, section) = BuildSection();

        var vm = new EditorShellViewModel(
            new Shell.FakeDocumentSession { HasDocument = true, CurrentPath = "/saves/slot1.dat" },
            new Shell.FakeUserInteraction(),
            new Shell.FakeSettingsStore());

        vm.RegisterSections(
        [
            new SectionDescriptor
            {
                Key = "player", Title = "Player",
                BodyMode = SectionBodyMode.Custom,
                Body = new FieldList { Fields = section.VisibleFields },
            },
            new SectionDescriptor { Key = "other", Title = "Other" },
        ]);

        var shell = new EditorShell { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, Content = shell };
        window.Show();
        Pump(window);

        var typed = shell.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "Aerith");
        typed.Text = "Tifa";

        vm.SelectedSection = vm.Sections[1];
        Pump(window);
        Assert.Empty(shell.GetVisualDescendants().OfType<FieldCard>());

        vm.SelectedSection = vm.Sections[0];
        Pump(window);

        var revisited = shell.GetVisualDescendants().OfType<TextBox>().First();

        Assert.Equal("Tifa", revisited.Text);
        Assert.True(section.HasPendingEdits);
        Assert.Equal("Aerith", document.Name);
    }

    [AvaloniaFact]
    public void A_Failing_Choice_Provider_Degrades_One_Field_Not_The_Application()
    {
        var doc = new Doc();
        var history = new EditHistory();

        var choice = new ChoiceFieldViewModel(
            new ChoiceFieldDescriptor
            {
                Key = "class", Label = "Class",
                Read = () => doc.Name, Write = v => doc.Name = v,
                Options = new ThrowingProvider(),
            },
            history);

        var card = new FieldCard { Field = choice };
        var window = new Window { Width = 600, Height = 300, Content = card };

        // The provider is consumer code documented as possibly doing IO. Uncontained,
        // its exception reaches the dispatcher and takes down the application - losing
        // every unsaved edit because a dropdown could not populate.
        window.Show();
        Pump(window);

        Assert.True(choice.OptionsFailed);
        Assert.NotNull(choice.ValidationError);

        // The message must be about the lookup, not the value: the field is not
        // invalid, it is unpopulated.
        Assert.Contains("options", choice.ValidationError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drives layout and a render pass so virtualized containers exist.</summary>
    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class ThrowingProvider : IChoiceProvider
    {
        public ValueTask<IReadOnlyList<ChoiceOption>> GetOptionsAsync(
            string filter, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Option source unavailable.");
    }
}

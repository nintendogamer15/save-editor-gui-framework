using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SaveEditor.Ui.Controls;
using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.Gallery.Views;

/// <summary>
/// P3 catalogue page: <c>FieldCard</c>, <c>FieldList</c>, and <c>SectionToolbar</c>
/// over a small in-memory demo document.
/// </summary>
public partial class EditingGalleryView : UserControl
{
    /// <summary>Creates the view and its demo section.</summary>
    public EditingGalleryView()
    {
        AvaloniaXamlLoader.Load(this);

        var document = new GalleryEditDocument();
        var history = new EditHistory();

        var name = new TextFieldViewModel(
            new TextFieldDescriptor
            {
                Key = "name",
                Label = "Character name",
                Path = "party[0].name",
                HelpText = "Shown on the title screen and every save slot.",
                WarningText = "Renaming after the tutorial can desync quest flags.",
                MaxLength = 20,
                Read = () => document.Name,
                Write = v => document.Name = v,
            },
            history);

        var level = new NumericFieldViewModel(
            new NumericFieldDescriptor
            {
                Key = "level",
                Label = "Level",
                Path = "party[0].level",
                Minimum = 1,
                Maximum = 99,
                ShowSpinner = true,
                Read = () => document.Level,
                Write = v => document.Level = v,
            },
            history);

        var hardcore = new BooleanFieldViewModel(
            new BooleanFieldDescriptor
            {
                Key = "hardcore",
                Label = "Hardcore mode",
                Path = "flags.hardcore",
                HelpText = "Permadeath is enabled while this is on.",
                Read = () => document.HardcoreMode,
                Write = v => document.HardcoreMode = v,
            },
            history);

        var difficulty = new ChoiceFieldViewModel(
            new ChoiceFieldDescriptor
            {
                Key = "difficulty",
                Label = "Difficulty",
                Path = "flags.difficulty",
                Options = new GalleryChoiceProvider("Normal", "Hard", "Nightmare"),
                Read = () => document.Difficulty,
                Write = v => document.Difficulty = v,
            },
            history);

        var characterId = new ReadOnlyFieldViewModel(
            new ReadOnlyFieldDescriptor
            {
                Key = "characterId",
                Label = "Character ID",
                Path = "party[0].id",
                Read = () => document.CharacterId,
            },
            history);

        var gold = new NumericFieldViewModel(
            new NumericFieldDescriptor
            {
                Key = "gold",
                Label = "Gold",
                Path = "wallet.gold",
                Minimum = 0,
                Maximum = 999_999,
                Read = () => document.Gold,
                Write = v => document.Gold = v,
            },
            history);

        var section = new SectionEditor(
            "demo",
            "Party member",
            [name, level, hardcore, difficulty, characterId, gold],
            history);

        // Demonstrate a pending edit and a validation error without waiting for the
        // gallery visitor to type anything.
        hardcore.Draft = !document.HardcoreMode;
        gold.Text = "lots";

        this.FindControl<SectionToolbar>("Toolbar")!.Editor = section;
        this.FindControl<FieldList>("Fields")!.Fields = section.VisibleFields;
    }

    /// <summary>A demo document, entirely in memory. The gallery writes to nothing on disk.</summary>
    private sealed class GalleryEditDocument
    {
        public string Name { get; set; } = "Aerith";

        public long Level { get; set; } = 12;

        public bool HardcoreMode { get; set; }

        public string Difficulty { get; set; } = "Normal";

        public string CharacterId { get; } = "PC-0001";

        public long Gold { get; set; } = 350;
    }

    /// <summary>Serves a fixed option list, in memory, for the demo choice field.</summary>
    private sealed class GalleryChoiceProvider(params string[] values) : IChoiceProvider
    {
        private readonly IReadOnlyList<ChoiceOption> _options =
            [.. values.Select(v => new ChoiceOption(v, v))];

        public ValueTask<IReadOnlyList<ChoiceOption>> GetOptionsAsync(
            string filter, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_options);
    }
}

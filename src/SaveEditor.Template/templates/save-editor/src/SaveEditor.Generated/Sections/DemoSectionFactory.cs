using SaveEditor.Ui.Editing;
using SaveEditor.Generated.Document;

namespace SaveEditor.Generated.Sections;

// ============================================================================
// REPLACE ME FIRST. See Document/DemoSaveDocument.cs.
// ============================================================================

/// <summary>
/// Builds the one populated example section, over whichever
/// <see cref="DemoSaveDocument"/> is currently open.
/// </summary>
/// <remarks>
/// <para>
/// This exists as a factory rather than a fixed set of fields built once
/// because field accessors close over a specific document instance. Every
/// time a document is opened, <c>MainWindow</c> calls this again over the
/// newly opened document and rebuilds the section — see
/// <c>MainWindow.axaml.cs</c>, <c>BuildHeroSection</c>.
/// </para>
/// <para>
/// Demonstrates every framework field kind: text (<c>HeroName</c>), numeric
/// with a spinner (<c>Level</c>), boolean (<c>HardcoreMode</c>), choice
/// (<c>Difficulty</c>), a caution shown alongside an editable value rather
/// than blocking it (<c>Gold</c>'s <see cref="FieldDescriptor.WarningText"/>),
/// and read-only (<c>SaveId</c>).
/// </para>
/// </remarks>
public static class DemoSectionFactory
{
    /// <summary>Stable key for the one example section this template ships.</summary>
    public const string SectionKey = "hero";

    /// <summary>Builds the section editor for one open document.</summary>
    /// <param name="document">The document currently open in the session.</param>
    /// <param name="history">The session's shared edit history.</param>
    public static SectionEditor Create(DemoSaveDocument document, EditHistory history)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(history);

        var fields = new FieldViewModel[]
        {
            new TextFieldViewModel(
                new TextFieldDescriptor
                {
                    Key = "heroName",
                    Label = "Hero name",
                    Path = "hero.name",
                    MaxLength = 32,
                    Read = () => document.HeroName,
                    Write = value => document.HeroName = value,
                },
                history),

            new NumericFieldViewModel(
                new NumericFieldDescriptor
                {
                    Key = "level",
                    Label = "Level",
                    Path = "hero.level",
                    Minimum = 1,
                    Maximum = 999,
                    ShowSpinner = true,
                    Read = () => document.Level,
                    Write = value => document.Level = value,
                },
                history),

            new BooleanFieldViewModel(
                new BooleanFieldDescriptor
                {
                    Key = "hardcoreMode",
                    Label = "Hardcore mode",
                    Path = "hero.hardcore",
                    HelpText = "Permadeath is enabled for this save.",
                    Read = () => document.HardcoreMode,
                    Write = value => document.HardcoreMode = value,
                },
                history),

            new ChoiceFieldViewModel(
                new ChoiceFieldDescriptor
                {
                    Key = "difficulty",
                    Label = "Difficulty",
                    Path = "hero.difficulty",
                    Options = new FixedChoiceProvider("Easy", "Normal", "Hard"),
                    Read = () => document.Difficulty,
                    Write = value => document.Difficulty = value,
                },
                history),

            new NumericFieldViewModel(
                new NumericFieldDescriptor
                {
                    Key = "gold",
                    Label = "Gold",
                    Path = "hero.gold",
                    Minimum = 0,
                    Maximum = 999_999,
                    WarningText = "Large jumps can trip the demo save's own economy warning on the next open.",
                    Read = () => document.Gold,
                    Write = value => document.Gold = value,
                },
                history),

            new ReadOnlyFieldViewModel(
                new ReadOnlyFieldDescriptor
                {
                    Key = "saveId",
                    Label = "Save ID",
                    Path = "hero.saveId",
                    HelpText = "Assigned when the save was first created. Not editable.",
                    Read = () => document.SaveId,
                },
                history),
        };

        return new SectionEditor(SectionKey, "Hero", fields, history);
    }

    /// <summary>Serves a fixed, in-memory option list for the demo choice field.</summary>
    private sealed class FixedChoiceProvider(params string[] values) : IChoiceProvider
    {
        private readonly IReadOnlyList<ChoiceOption> _options =
            [.. values.Select(v => new ChoiceOption(v, v))];

        public ValueTask<IReadOnlyList<ChoiceOption>> GetOptionsAsync(
            string filter, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_options);
    }
}

using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.HeadlessTests.Controls;

/// <summary>Shared demo document and field construction for the P3 control tests.</summary>
internal static class ControlsTestFixture
{
    /// <summary>A stand-in document. Fields read and write these directly.</summary>
    internal sealed class Document
    {
        public string Name { get; set; } = "Aerith";

        public long Level { get; set; } = 12;

        public bool Hardcore { get; set; }
    }

    /// <summary>Builds one section with a text, a numeric, and a boolean field.</summary>
    internal static (Document Doc, EditHistory History, SectionEditor Section) BuildSection(
        int capacity = EditHistory.DefaultCapacity)
    {
        var doc = new Document();
        var history = new EditHistory(capacity);

        var name = new TextFieldViewModel(
            new TextFieldDescriptor
            {
                Key = "name",
                Label = "Name",
                Path = "player.name",
                MaxLength = 12,
                Read = () => doc.Name,
                Write = v => doc.Name = v,
            },
            history);

        var level = new NumericFieldViewModel(
            new NumericFieldDescriptor
            {
                Key = "level",
                Label = "Level",
                Path = "player.level",
                Minimum = 1,
                Maximum = 99,
                Read = () => doc.Level,
                Write = v => doc.Level = v,
            },
            history);

        var hardcore = new BooleanFieldViewModel(
            new BooleanFieldDescriptor
            {
                Key = "hardcore",
                Label = "Hardcore",
                Read = () => doc.Hardcore,
                Write = v => doc.Hardcore = v,
            },
            history);

        var section = new SectionEditor("player", "Player", [name, level, hardcore], history);
        return (doc, history, section);
    }

    /// <summary>Builds a section with <paramref name="count"/> numeric fields, for virtualization tests.</summary>
    internal static (EditHistory History, SectionEditor Section) BuildLargeSection(int count)
    {
        var history = new EditHistory();
        var backing = new long[count];

        var fields = new List<FieldViewModel>(count);
        for (var i = 0; i < count; i++)
        {
            var index = i;
            fields.Add(new NumericFieldViewModel(
                new NumericFieldDescriptor
                {
                    Key = $"field{index}",
                    Label = $"Stat {index}",
                    Path = $"stats[{index}]",
                    Read = () => backing[index],
                    Write = v => backing[index] = v,
                },
                history));
        }

        var section = new SectionEditor("large", "Large section", fields, history);
        return (history, section);
    }
}

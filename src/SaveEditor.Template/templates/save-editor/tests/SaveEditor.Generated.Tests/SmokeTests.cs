using Avalonia;
using Avalonia.Headless.XUnit;
using SaveEditor.Generated.Codecs;
using SaveEditor.Generated.Document;
using SaveEditor.Generated.Sections;
using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Editing;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Theming;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Generated.Tests;

/// <summary>
/// P5 acceptance for the generated app itself: a sample field edit and a
/// theme switch, driven headlessly.
/// </summary>
public class SmokeTests
{
    /// <summary>An in-memory settings store, so tests never touch a real settings file.</summary>
    private sealed class InMemorySettingsStore : IEditorSettingsStore
    {
        private EditorSettings _settings = new();

        public bool IsPersistent => true;

        public ValueTask<EditorSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_settings);

        public ValueTask SaveAsync(EditorSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return ValueTask.CompletedTask;
        }
    }

    [AvaloniaFact]
    public void Sample_Field_Edit_Applies_To_The_Document_And_Records_History()
    {
        var document = new DemoSaveDocument { HeroName = "Aerith" };
        var history = new EditHistory();
        var section = DemoSectionFactory.Create(document, history);

        var heroName = Assert.IsType<TextFieldViewModel>(
            section.Fields.Single(f => f.Key == "heroName"));

        heroName.Draft = "Cloud";
        Assert.True(heroName.HasPendingEdit);
        Assert.True(section.HasPendingEdits);

        heroName.Apply();

        Assert.Equal("Cloud", document.HeroName);
        Assert.False(heroName.HasPendingEdit);
        Assert.False(section.HasPendingEdits);
        Assert.True(history.CanUndo);

        history.Undo();
        Assert.Equal("Aerith", document.HeroName);
    }

    [AvaloniaFact]
    public async Task Theme_Switch_Applies_And_Persists()
    {
        var theme = Application.Current!.Styles.OfType<SaveEditorTheme>().Single();
        var store = new InMemorySettingsStore();
        var controller = new ThemeController(theme, store);

        await controller.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ThemeMode.Dark, controller.Mode);

        await controller.SetModeAsync(ThemeMode.Light, TestContext.Current.CancellationToken);
        Assert.Equal(ThemeMode.Light, controller.Mode);

        // A second controller over the same store stands in for "next launch".
        var restarted = new ThemeController(theme, store);
        await restarted.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ThemeMode.Light, restarted.Mode);
    }

    [AvaloniaFact]
    public async Task Opening_The_Sample_Save_Editing_A_Field_And_Saving_As_Round_Trips()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("save-editor-generated-tests-").FullName;

        try
        {
            var samplePath = await DemoSampleFile.WriteAsync(directory, cancellationToken);

            var registry = new SaveCodecRegistry<DemoSaveDocument>(
            [
                new CodecRegistration<DemoSaveDocument>(new DemoSaveDetector(), new DemoSaveCodec()),
            ]);

            var interaction = new HeadlessUserInteraction();
            var workflow = new SafeFileWorkflow<DemoSaveDocument>(new SafeFileWorkflowOptions<DemoSaveDocument>
            {
                Registry = registry,
                Interaction = interaction,
            });

            var history = new EditHistory();
            using var session = new DocumentSession<DemoSaveDocument>(workflow, history, new DemoSaveCodec());

            await session.OpenAsync(samplePath, cancellationToken: cancellationToken);
            Assert.True(session.HasDocument);
            Assert.NotNull(session.Document);

            var section = DemoSectionFactory.Create(session.Document!, history);
            session.PendingEditProbe = () => section.HasPendingEdits;

            var level = Assert.IsType<NumericFieldViewModel>(section.Fields.Single(f => f.Key == "level"));
            level.Text = "42";
            Assert.True(session.HasPendingEdits);

            level.Apply();
            Assert.Equal(42L, session.Document!.Level);
            Assert.False(session.HasPendingEdits);

            var savedPath = Path.Combine(directory, "edited.demosave");
            interaction.NextSaveTarget = new SaveEditor.Ui.Interaction.SaveFilePickResult(savedPath, PickerConfirmedOverwrite: false);

            await session.SaveAsAsync(cancellationToken);

            Assert.True(File.Exists(savedPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A minimal <c>IUserInteraction</c> for headless workflow tests: no dialogs, just answers.</summary>
    private sealed class HeadlessUserInteraction : SaveEditor.Ui.Interaction.IUserInteraction
    {
        public SaveEditor.Ui.Interaction.SaveFilePickResult? NextSaveTarget { get; set; }

        public ValueTask<string?> PickOpenFileAsync(
            SaveEditor.Ui.Interaction.FilePickerRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<SaveEditor.Ui.Interaction.SaveFilePickResult?> PickSaveFileAsync(
            SaveEditor.Ui.Interaction.FilePickerRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(NextSaveTarget);

        public ValueTask<string?> PickFolderAsync(
            string title, string? suggestedDirectory = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<bool> ConfirmAsync(
            SaveEditor.Ui.Interaction.ConfirmationRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask ShowMessageAsync(
            SaveEditor.Ui.Interaction.MessageRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<string?> ChooseAsync(
            SaveEditor.Ui.Interaction.ChoicePrompt prompt, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask ShowDocumentAsync(
            SaveEditor.Ui.Interaction.DocumentRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}

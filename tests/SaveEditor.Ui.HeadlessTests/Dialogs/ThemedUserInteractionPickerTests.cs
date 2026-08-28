using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Dialogs;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.HeadlessTests.Dialogs;

/// <summary>
/// The framework's default interaction has to reach a destination chooser, with what
/// the workflow asked for, from wherever the workflow happens to be running.
/// </summary>
/// <remarks>
/// The safe file workflow runs codecs on the thread pool and resumes there, so by the
/// time Save As reaches a picker or a confirmation it is on a pool thread. Avalonia
/// objects belong to the thread that created them, and the workflow catches everything
/// and reports a failed save — so getting this wrong shows up as no chooser at all
/// rather than as an exception anybody sees.
/// </remarks>
public class ThemedUserInteractionPickerTests
{
    private static readonly IReadOnlyList<SaveFormatDescriptor> Formats =
        [new SaveFormatDescriptor("test.shell", "Shell Test Save", ["sav"])];

    private static (ThemedUserInteraction Interaction, RecordingPickers Pickers) Build()
    {
        var pickers = new RecordingPickers();
        return (new ThemedUserInteraction(new Window(), pickers), pickers);
    }

    /// <summary>Runs work the way the workflow does: from a thread pool thread.</summary>
    private static async Task FromThePoolAsync(Action work)
    {
        await Task.Run(() =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess(), "The test did not leave the UI thread.");
            work();
        });
    }

    [AvaloniaFact]
    public async Task Save_Picker_Is_Reached_With_The_Title_Name_Filters_And_Start_Location()
    {
        var (interaction, pickers) = Build();
        var directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var chosen = Path.Combine(directory, "copy.sav");

        pickers.SavePath = chosen;

        var result = await interaction.PickSaveFileAsync(
            new FilePickerRequest("Save a copy", Formats, "slot1.sav", directory));

        var options = Assert.Single(pickers.SaveOptions);
        Assert.Equal("Save a copy", options.Title);
        Assert.Equal("slot1.sav", options.SuggestedFileName);
        Assert.Equal(["*.sav"], options.FileTypeChoices!.Single().Patterns);

        // SuggestedDirectory was previously accepted and dropped, which left the chooser
        // opening wherever the platform last happened to be rather than beside the save
        // being copied.
        Assert.Equal(
            directory,
            Assert.Single(pickers.FolderLookups).LocalPath.TrimEnd(Path.DirectorySeparatorChar));

        Assert.NotNull(result);
        Assert.Equal(chosen, result.Path);

        // Fail-closed regardless of what the platform picker asked; see SaveFilePickResult.
        Assert.False(result.PickerConfirmedOverwrite);
    }

    [AvaloniaFact]
    public async Task Save_Picker_Is_Reached_From_A_Thread_Pool_Thread()
    {
        var (interaction, pickers) = Build();
        pickers.SavePath = Path.Combine(Path.GetTempPath(), "copy.sav");

        Task<SaveFilePickResult?>? picking = null;
        await FromThePoolAsync(() =>
            picking = interaction.PickSaveFileAsync(new FilePickerRequest("Save a copy", Formats)).AsTask());

        var result = await picking!;

        Assert.NotNull(result);
        Assert.Single(pickers.SaveOptions);

        // Platform storage belongs to the thread that owns the window, so where the call
        // landed is the whole assertion.
        Assert.All(pickers.ArrivedOnTheUiThread, Assert.True);
    }

    [AvaloniaFact]
    public async Task A_Dismissed_Save_Picker_Reports_No_Destination()
    {
        var (interaction, pickers) = Build();
        pickers.SavePath = null;

        var result = await interaction.PickSaveFileAsync(new FilePickerRequest("Save a copy", Formats));

        Assert.Single(pickers.SaveOptions);
        Assert.Null(result);
    }

    [AvaloniaFact]
    public async Task Open_Picker_Is_Reached_With_Its_Filters_And_Start_Location()
    {
        var (interaction, pickers) = Build();
        var directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        pickers.OpenPath = Path.Combine(directory, "slot1.sav");

        Task<string?>? picking = null;
        await FromThePoolAsync(() =>
            picking = interaction.PickOpenFileAsync(
                new FilePickerRequest("Open save", Formats, null, directory)).AsTask());

        var result = await picking!;
        var options = Assert.Single(pickers.OpenOptions);

        Assert.Equal("Open save", options.Title);
        Assert.False(options.AllowMultiple);
        Assert.Equal(["*.sav"], options.FileTypeFilter!.Single().Patterns);
        Assert.Single(pickers.FolderLookups);
        Assert.Equal(pickers.OpenPath, result);
        Assert.All(pickers.ArrivedOnTheUiThread, Assert.True);
    }

    [AvaloniaFact]
    public async Task Folder_Picker_Honours_Its_Suggested_Directory()
    {
        var (interaction, pickers) = Build();
        var directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        pickers.FolderPath = directory;

        var result = await interaction.PickFolderAsync("Choose a backup folder", directory);

        var options = Assert.Single(pickers.FolderOptions);
        Assert.Equal("Choose a backup folder", options.Title);
        Assert.Equal(
            directory,
            Assert.Single(pickers.FolderLookups).LocalPath.TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal(directory, result);
    }

    [AvaloniaFact]
    public async Task An_Unresolvable_Start_Location_Still_Opens_The_Picker()
    {
        var (interaction, pickers) = Build();
        pickers.FolderLookupFailure = new IOException("The volume is not there.");
        pickers.SavePath = Path.Combine(Path.GetTempPath(), "copy.sav");

        var result = await interaction.PickSaveFileAsync(
            new FilePickerRequest("Save a copy", Formats, "slot1.sav", Path.GetTempPath()));

        // A chooser that opens in the wrong place is a nuisance; one that does not open
        // at all is the defect this is part of fixing.
        var options = Assert.Single(pickers.SaveOptions);
        Assert.Null(options.SuggestedStartLocation);
        Assert.NotNull(result);
    }

    [AvaloniaFact]
    public async Task A_Confirmation_Raised_From_A_Thread_Pool_Thread_Opens_Its_Dialog()
    {
        var owner = new Window { Width = 600, Height = 400 };
        owner.Show();
        Dispatcher.UIThread.RunJobs();

        var interaction = new ThemedUserInteraction(owner);
        var request = new ConfirmationRequest
        {
            Title = "Overwrite save file",
            Message = "This replaces the file's current contents.",
            AcceptLabel = "Overwrite save file",
            IsDestructive = true,
        };

        Task<bool>? confirming = null;
        await FromThePoolAsync(() => confirming = interaction.ConfirmAsync(request).AsTask());

        Pump(owner);

        // Constructing the dialog on the calling thread throws "the calling thread cannot
        // access this object", which the workflow catches and reports as a failed save —
        // the user never sees a prompt, or a reason there was not one.
        Assert.False(confirming!.IsFaulted, confirming.Exception?.InnerException?.Message);

        var dialog = Assert.Single(owner.OwnedWindows);
        var view = Assert.IsType<ConfirmationDialogView>(dialog.Content);

        view.AcceptButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(owner);

        Assert.True(await confirming);
    }

    [AvaloniaFact]
    public async Task A_Message_Raised_From_A_Thread_Pool_Thread_Opens_Its_Dialog()
    {
        var owner = new Window { Width = 600, Height = 400 };
        owner.Show();
        Dispatcher.UIThread.RunJobs();

        var interaction = new ThemedUserInteraction(owner);

        Task? showing = null;
        await FromThePoolAsync(() =>
            showing = interaction.ShowMessageAsync(
                new MessageRequest("This save cannot be written", "Validation found errors.")).AsTask());

        Pump(owner);

        Assert.False(showing!.IsFaulted, showing.Exception?.InnerException?.Message);

        var dialog = Assert.Single(owner.OwnedWindows);
        Assert.IsType<MessageDialogView>(dialog.Content);

        dialog.Close();
        Pump(owner);

        await showing;
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Pickers that record what they were asked and answer from fields.</summary>
    private sealed class RecordingPickers : IStoragePickers
    {
        public List<FilePickerSaveOptions> SaveOptions { get; } = [];

        public List<FilePickerOpenOptions> OpenOptions { get; } = [];

        public List<FolderPickerOpenOptions> FolderOptions { get; } = [];

        public List<Uri> FolderLookups { get; } = [];

        /// <summary>Whether each call arrived on the UI thread. Every entry must be true.</summary>
        public List<bool> ArrivedOnTheUiThread { get; } = [];

        public string? SavePath { get; set; }

        public string? OpenPath { get; set; }

        public string? FolderPath { get; set; }

        public Exception? FolderLookupFailure { get; set; }

        public Task<string?> PickOpenFileAsync(FilePickerOpenOptions options)
        {
            Record();
            OpenOptions.Add(options);
            return Task.FromResult(OpenPath);
        }

        public Task<string?> PickSaveFileAsync(FilePickerSaveOptions options)
        {
            Record();
            SaveOptions.Add(options);
            return Task.FromResult(SavePath);
        }

        public Task<string?> PickFolderAsync(FolderPickerOpenOptions options)
        {
            Record();
            FolderOptions.Add(options);
            return Task.FromResult(FolderPath);
        }

        public Task<IStorageFolder?> ResolveFolderAsync(Uri path)
        {
            Record();
            FolderLookups.Add(path);

            // Avalonia's storage items cannot be constructed outside Avalonia, so the
            // observable part is that the lookup happened and with what.
            return FolderLookupFailure is { } failure
                ? Task.FromException<IStorageFolder?>(failure)
                : Task.FromResult<IStorageFolder?>(null);
        }

        private void Record() => ArrivedOnTheUiThread.Add(Dispatcher.UIThread.CheckAccess());
    }
}

using Avalonia.Headless.XUnit;
using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Editing;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Shell;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>
/// P2's deferred checks D5 through D7, closed against the real workflow rather than
/// the stub the shell was built on.
/// </summary>
public class DeferredWorkflowChecksTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"se-shell-{Guid.NewGuid():N}");

    public DeferredWorkflowChecksTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A retained handle on a failing test must not mask the real failure.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A save format simple enough that the test is about the shell, not parsing.</summary>
    private sealed record Doc(string Name, int Level);

    private sealed class Codec : ISaveCodec<Doc>
    {
        public SaveFormatDescriptor Format { get; } = new("test.shell", "Shell Test Save", ["sav"]);

        public bool PreservesUnknownData => true;

        public async ValueTask<Doc> DecodeAsync(Stream source, CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(source, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var parts = text.Split('|');
            return new Doc(parts[1], int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
        }

        public async ValueTask SerializeAsync(
            Doc document, Stream destination, CancellationToken cancellationToken = default)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(Encode(document));
            await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<ValidationReport> ValidateAsync(
            Doc document, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ValidationReport.Empty);

        public static string Encode(Doc d) =>
            $"SHEL|{d.Name}|{d.Level.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private sealed class Detector : ISaveCodecDetector
    {
        public SaveFormatDescriptor Format { get; } = new("test.shell", "Shell Test Save", ["sav"]);

        public int HeaderBytesRequired => 4;

        public DetectionVerdict Detect(ReadOnlySpan<byte> header) =>
            header.Length >= 4 && header[0] == (byte)'S' && header[1] == (byte)'H'
                ? DetectionVerdict.Confident
                : DetectionVerdict.Declined;
    }

    private (EditorShellViewModel Vm, DocumentSession<Doc> Session, FakeUserInteraction Interaction, FakeSettingsStore Store)
        Build()
    {
        var codec = new Codec();
        var workflow = new SafeFileWorkflow<Doc>(new SafeFileWorkflowOptions<Doc>
        {
            Registry = new SaveCodecRegistry<Doc>([new CodecRegistration<Doc>(new Detector(), codec)]),
            Interaction = new PassThroughInteraction(),
        });

        var session = new DocumentSession<Doc>(workflow, new EditHistory(), codec);
        var interaction = new FakeUserInteraction();
        var store = new FakeSettingsStore();
        var vm = new EditorShellViewModel(session, interaction, store);

        return (vm, session, interaction, store);
    }

    /// <summary>Accepts everything, so the tests exercise the workflow rather than prompts.</summary>
    private sealed class PassThroughInteraction : IUserInteraction
    {
        public ValueTask<string?> PickOpenFileAsync(FilePickerRequest r, CancellationToken c = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<SaveFilePickResult?> PickSaveFileAsync(FilePickerRequest r, CancellationToken c = default) =>
            ValueTask.FromResult<SaveFilePickResult?>(null);

        public ValueTask<string?> PickFolderAsync(string t, string? s = null, CancellationToken c = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<bool> ConfirmAsync(ConfirmationRequest r, CancellationToken c = default) =>
            ValueTask.FromResult(true);

        public ValueTask ShowMessageAsync(MessageRequest r, CancellationToken c = default) =>
            ValueTask.CompletedTask;

        public ValueTask<string?> ChooseAsync(ChoicePrompt p, CancellationToken c = default) =>
            ValueTask.FromResult<string?>(p.Options.Count > 0 ? p.Options[0].Key : null);

        public ValueTask ShowDocumentAsync(DocumentRequest r, CancellationToken c = default) =>
            ValueTask.CompletedTask;
    }

    private string WriteSave(string name, Doc document)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, Codec.Encode(document));
        return path;
    }

    [AvaloniaFact]
    public async Task D5_A_Dropped_Path_Opens_Through_The_Real_Workflow()
    {
        var (vm, session, _, _) = Build();
        using var _s = session;

        var path = WriteSave("dropped.sav", new Doc("Aerith", 42));

        // OpenPathAsync is the entry point DragDropAdapter forwards into; P2 could only
        // prove it reached a stub.
        await vm.OpenPathAsync(path, TestContext.Current.CancellationToken);

        Assert.True(session.HasDocument);
        Assert.Equal("Aerith", session.Document!.Name);
        Assert.Equal(42, session.Document.Level);
    }

    [AvaloniaFact]
    public async Task D6_Activating_A_Recent_Opens_A_Real_Document()
    {
        var (vm, session, _, _) = Build();
        using var _s = session;

        var path = WriteSave("recent.sav", new Doc("Tifa", 7));
        var entry = new RecentEntry(path, Ui.Display.PathDisplayFormatter.Default.Format(path));
        vm.Recents.Add(entry);

        await vm.OpenRecentCommand.ExecuteAsync(entry);

        Assert.True(session.HasDocument);
        Assert.Equal("Tifa", session.Document!.Name);
        Assert.Contains(entry, vm.Recents);
    }

    [AvaloniaFact]
    public async Task D6_Activating_A_Missing_Recent_Prunes_It()
    {
        var (vm, session, _, store) = Build();
        using var _s = session;

        var missing = Path.Combine(_directory, "gone.sav");
        var entry = new RecentEntry(missing, Ui.Display.PathDisplayFormatter.Default.Format(missing));
        vm.Recents.Add(entry);
        store.Current = new EditorSettings { RecentFiles = [missing] };

        await vm.OpenRecentCommand.ExecuteAsync(entry);

        Assert.False(session.HasDocument);
        Assert.DoesNotContain(entry, vm.Recents);
        Assert.Empty(store.Current.RecentFiles);
    }

    [AvaloniaFact]
    public async Task D6_A_Temporarily_Unreachable_Recent_Is_Kept()
    {
        var (vm, session, _, _) = Build();
        using var _s = session;

        var path = Path.Combine(_directory, "on-a-detached-drive.sav");
        var entry = new RecentEntry(path, Ui.Display.PathDisplayFormatter.Default.Format(path));
        vm.Recents.Add(entry);

        // The open fails, but the probe says the path is still there. An unplugged
        // drive is not a deleted save, and dropping the entry loses the only record of
        // where it lived.
        vm.PathExists = _ => true;

        await vm.OpenRecentCommand.ExecuteAsync(entry);

        Assert.False(session.HasDocument);
        Assert.Contains(entry, vm.Recents);
    }

    [AvaloniaFact]
    public async Task D7_The_Status_Bar_Reports_What_The_Workflow_Did()
    {
        var (vm, session, _, _) = Build();
        using var _s = session;

        var path = WriteSave("status.sav", new Doc("Aerith", 1));
        await vm.OpenPathAsync(path, TestContext.Current.CancellationToken);

        // The sentence has to come from whatever performed the operation. A shell that
        // composed its own would report what it asked for, not what happened.
        Assert.Equal(session.LastStatusMessage, vm.StatusMessage);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [AvaloniaFact]
    public async Task D7_The_Status_Bar_Reports_Progress_And_The_Backup_Location()
    {
        var (vm, session, _, _) = Build();
        using var _s = session;

        var phases = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ProgressDescription) && vm.ProgressDescription.Length > 0)
            {
                phases.Add(vm.ProgressDescription);
            }
        };

        var path = WriteSave("progress.sav", new Doc("Aerith", 1));
        await vm.OpenPathAsync(path, TestContext.Current.CancellationToken);

        session.ReplaceDocument(session.Document! with { Level = 2 });
        await vm.OverwriteWithBackupCommand.ExecuteAsync(null);

        Assert.True(session.LastOutcome!.IsSuccess);

        // Progress must actually have been observed, not merely wired. A status bar
        // that only updates when the operation ends cannot distinguish slow from hung.
        Assert.NotEmpty(phases);
        Assert.Contains(phases, p => p.Contains("backup", StringComparison.OrdinalIgnoreCase));

        // And it must clear afterwards rather than leaving a stale phase on screen.
        Assert.Empty(vm.ProgressDescription);
        Assert.False(vm.IsBusy);

        // "A backup was written" is only useful alongside where to find it.
        Assert.NotNull(vm.LastBackupLabel);
        Assert.NotNull(session.LastBackupPath);

        // Containment, not equality: the label is isolate-wrapped so it can never be
        // byte-equal to a path, which is the property that stops it being fed back to
        // the filesystem. The filename still has to be legible in it.
        Assert.Contains(
            Path.GetFileName(session.LastBackupPath!),
            vm.LastBackupLabel!.FullLabel,
            StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task D7_A_Failed_Open_Reports_The_Failure_Not_A_Success()
    {
        var (vm, session, _, _) = Build();
        using var _s = session;

        await vm.OpenPathAsync(Path.Combine(_directory, "nope.sav"), TestContext.Current.CancellationToken);

        Assert.False(session.HasDocument);
        Assert.Equal(session.LastStatusMessage, vm.StatusMessage);
    }
}

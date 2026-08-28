using System.Globalization;
using System.Text;
using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Editing;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Shell;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>A save format simple enough that a shell test is about the shell, not parsing.</summary>
internal sealed record ShellDoc(string Name, int Level);

/// <summary>The codec for <see cref="ShellDoc"/>, with a forceable serialization failure.</summary>
/// <remarks>
/// The failure is an override rather than a second codec type so that the failing and
/// succeeding paths run through the same code, which is the only way a test of the
/// failing path says anything about the one that ships.
/// </remarks>
internal sealed class ShellCodec : ISaveCodec<ShellDoc>
{
    public SaveFormatDescriptor Format { get; } = new("test.shell", "Shell Test Save", ["sav"]);

    public bool PreservesUnknownData => true;

    /// <summary>Set to make every serialization throw, driving the workflow's failure path.</summary>
    public string? SerializeFailure { get; set; }

    public static string Encode(ShellDoc d) =>
        $"SHEL|{d.Name}|{d.Level.ToString(CultureInfo.InvariantCulture)}";

    public async ValueTask<ShellDoc> DecodeAsync(Stream source, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(source, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var parts = text.Split('|');
        return new ShellDoc(parts[1], int.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    public async ValueTask SerializeAsync(
        ShellDoc document, Stream destination, CancellationToken cancellationToken = default)
    {
        if (SerializeFailure is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        await destination.WriteAsync(Encoding.UTF8.GetBytes(Encode(document)), cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<ValidationReport> ValidateAsync(
        ShellDoc document, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ValidationReport.Empty);
}

/// <summary>The detector for <see cref="ShellCodec"/>.</summary>
internal sealed class ShellDetector : ISaveCodecDetector
{
    public SaveFormatDescriptor Format { get; } = new("test.shell", "Shell Test Save", ["sav"]);

    public int HeaderBytesRequired => 4;

    public DetectionVerdict Detect(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == (byte)'S' && header[1] == (byte)'H'
            ? DetectionVerdict.Confident
            : DetectionVerdict.Declined;
}

/// <summary>
/// The interaction the workflow itself is given: accepts everything by default, and
/// lets a test script the destination picker.
/// </summary>
/// <remarks>
/// Separate from <see cref="FakeUserInteraction"/>, which is the shell's. The two are
/// different surfaces — the shell asks about discarding work, the workflow asks where
/// to write and whether to overwrite — and a test that conflates them cannot tell
/// which one refused.
/// </remarks>
internal sealed class WorkflowInteraction : IUserInteraction
{
    /// <summary>Answers the destination picker. Declines by default.</summary>
    public Func<FilePickerRequest, SaveFilePickResult?> SavePicker { get; set; } = _ => null;

    /// <summary>Answers destructive confirmations. Accepts by default.</summary>
    public Func<ConfirmationRequest, bool> Confirm { get; set; } = _ => true;

    public List<FilePickerRequest> SaveRequests { get; } = [];

    public List<ConfirmationRequest> Confirmations { get; } = [];

    public List<MessageRequest> Messages { get; } = [];

    public ValueTask<string?> PickOpenFileAsync(FilePickerRequest r, CancellationToken c = default) =>
        ValueTask.FromResult<string?>(null);

    public ValueTask<SaveFilePickResult?> PickSaveFileAsync(FilePickerRequest r, CancellationToken c = default)
    {
        SaveRequests.Add(r);
        return ValueTask.FromResult(SavePicker(r));
    }

    public ValueTask<string?> PickFolderAsync(string t, string? s = null, CancellationToken c = default) =>
        ValueTask.FromResult<string?>(null);

    public ValueTask<bool> ConfirmAsync(ConfirmationRequest r, CancellationToken c = default)
    {
        Confirmations.Add(r);
        return ValueTask.FromResult(Confirm(r));
    }

    public ValueTask ShowMessageAsync(MessageRequest r, CancellationToken c = default)
    {
        Messages.Add(r);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> ChooseAsync(ChoicePrompt p, CancellationToken c = default) =>
        ValueTask.FromResult<string?>(p.Options.Count > 0 ? p.Options[0].Key : null);

    public ValueTask ShowDocumentAsync(DocumentRequest r, CancellationToken c = default) =>
        ValueTask.CompletedTask;
}

/// <summary>A write policy driven by a delegate.</summary>
internal sealed class ScriptedWritePolicy : IWritePolicy
{
    public required Func<PlannedWrite, WriteDecision> Decide { get; init; }

    public List<PlannedWrite> Plans { get; } = [];

    public ValueTask<WriteDecision> EvaluateAsync(PlannedWrite plan, CancellationToken cancellationToken = default)
    {
        Plans.Add(plan);
        return ValueTask.FromResult(Decide(plan));
    }
}

/// <summary>
/// A shell view-model over the real workflow and the real session, in a scratch
/// directory of its own.
/// </summary>
/// <remarks>
/// Deliberately not the stub session: the defects this covers — a Save As that never
/// re-enables, a Recent list that never fills — live in the seam between the shell and
/// what actually writes files, which a stub cannot exercise.
/// </remarks>
internal sealed class ShellWorkflowHarness : IDisposable
{
    private ShellWorkflowHarness(string root, IWritePolicy? policy)
    {
        Root = root;
        Directory.CreateDirectory(root);

        Workflow = new SafeFileWorkflow<ShellDoc>(new SafeFileWorkflowOptions<ShellDoc>
        {
            Registry = new SaveCodecRegistry<ShellDoc>([new CodecRegistration<ShellDoc>(new ShellDetector(), Codec)]),
            Interaction = WorkflowInteraction,
            WritePolicy = policy,
        });

        Session = new DocumentSession<ShellDoc>(Workflow, new EditHistory(), Codec);
        Vm = new EditorShellViewModel(Session, Interaction, Store);
    }

    public string Root { get; }

    public ShellCodec Codec { get; } = new();

    public WorkflowInteraction WorkflowInteraction { get; } = new();

    public FakeUserInteraction Interaction { get; } = new();

    public FakeSettingsStore Store { get; } = new();

    public SafeFileWorkflow<ShellDoc> Workflow { get; }

    public DocumentSession<ShellDoc> Session { get; }

    public EditorShellViewModel Vm { get; }

    public static ShellWorkflowHarness Create(string label, IWritePolicy? policy = null) =>
        new(Path.Combine(Path.GetTempPath(), $"se-{label}-{Guid.NewGuid():N}"), policy);

    /// <summary>Writes a save file into the scratch directory and returns its path.</summary>
    public string WriteSave(string name, ShellDoc document)
    {
        var path = Destination(name);
        File.WriteAllText(path, ShellCodec.Encode(document));
        return path;
    }

    /// <summary>A path inside the scratch directory. Nothing is created at it.</summary>
    public string Destination(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        Session.Dispose();
        Vm.Dispose();

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A retained handle must not mask the real failure.
        }
    }
}

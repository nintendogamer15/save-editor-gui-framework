using System.Text;
using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Io;
using SaveEditor.Ui.Tests.Io;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// The document the test codec reads and writes. A record so that the pre-replace
/// round-trip comparison has a real equality contract to work with.
/// </summary>
internal sealed record TestDocument(string Name, int Level, string Trailer);

/// <summary>
/// A deliberately trivial save format: <c>SEDT|name|level|trailer</c>.
/// </summary>
/// <remarks>
/// Every behaviour a test needs to provoke — a throw, a lossy serializer, a falsified
/// preservation claim, a serializer that blocks past cancellation — is an override rather
/// than a separate codec type, so the tests exercise one code path through the workflow.
/// </remarks>
internal sealed class TestCodec : ISaveCodec<TestDocument>
{
    public const string Magic = "SEDT";

    public SaveFormatDescriptor Format { get; set; } =
        new("test.sedt", "Test Save", ["sav"]);

    public bool PreservesUnknownData { get; set; }

    public Func<byte[], CancellationToken, ValueTask<TestDocument>>? DecodeOverride { get; set; }

    public Func<TestDocument, Stream, CancellationToken, ValueTask>? SerializeOverride { get; set; }

    public Func<TestDocument, int, CancellationToken, ValueTask<ValidationReport>>? ValidateOverride { get; set; }

    public int DecodeCalls { get; private set; }

    public int SerializeCalls { get; private set; }

    public int ValidateCalls { get; private set; }

    public static byte[] Encode(TestDocument document) =>
        Encoding.UTF8.GetBytes($"{Magic}|{document.Name}|{document.Level}|{document.Trailer}");

    public static TestDocument Parse(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var parts = text.Split('|', 4);
        if (parts.Length != 4 || parts[0] != Magic)
        {
            throw new InvalidDataException("Not a test save file.");
        }

        return new TestDocument(parts[1], int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), parts[3]);
    }

    public async ValueTask<TestDocument> DecodeAsync(Stream source, CancellationToken cancellationToken = default)
    {
        DecodeCalls++;

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        return DecodeOverride is null
            ? Parse(bytes)
            : await DecodeOverride(bytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SerializeAsync(TestDocument document, Stream destination, CancellationToken cancellationToken = default)
    {
        SerializeCalls++;

        if (SerializeOverride is not null)
        {
            await SerializeOverride(document, destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        await destination.WriteAsync(Encode(document), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ValidationReport> ValidateAsync(TestDocument document, CancellationToken cancellationToken = default)
    {
        ValidateCalls++;

        return ValidateOverride is null
            ? ValidationReport.Empty
            : await ValidateOverride(document, ValidateCalls, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A detector that records exactly how many bytes it was shown.</summary>
internal sealed class TestDetector : ISaveCodecDetector
{
    private readonly List<byte[]> _headers = [];

    public SaveFormatDescriptor Format { get; set; } = new("test.sedt", "Test Save", ["sav"]);

    public int HeaderBytesRequired { get; set; } = TestCodec.Magic.Length;

    public Func<byte[], DetectionVerdict>? DetectOverride { get; set; }

    public IReadOnlyList<byte[]> Headers => _headers;

    public DetectionVerdict Detect(ReadOnlySpan<byte> header)
    {
        var copy = header.ToArray();
        lock (_headers)
        {
            _headers.Add(copy);
        }

        if (DetectOverride is not null)
        {
            return DetectOverride(copy);
        }

        return copy.Length >= TestCodec.Magic.Length &&
               Encoding.UTF8.GetString(copy, 0, TestCodec.Magic.Length) == TestCodec.Magic
            ? DetectionVerdict.Confident
            : DetectionVerdict.Declined;
    }
}

/// <summary>An interaction surface that records everything and answers from delegates.</summary>
internal sealed class FakeInteraction : IUserInteraction
{
    public Func<FilePickerRequest, SaveFilePickResult?>? SavePicker { get; set; }

    public Func<string?, string?>? FolderPicker { get; set; }

    public Func<ConfirmationRequest, bool> Confirm { get; set; } = _ => true;

    public List<ConfirmationRequest> Confirmations { get; } = [];

    public List<MessageRequest> Messages { get; } = [];

    public List<ChoicePrompt> Prompts { get; } = [];

    public List<DocumentRequest> Documents { get; } = [];

    /// <summary>
    /// Answers a choice prompt. Declines by default: a dismissal must abandon the
    /// operation rather than silently taking the first option.
    /// </summary>
    public Func<ChoicePrompt, string?> Choose { get; set; } = _ => null;

    public ValueTask<string?> ChooseAsync(ChoicePrompt prompt, CancellationToken cancellationToken = default)
    {
        Prompts.Add(prompt);
        return ValueTask.FromResult(Choose(prompt));
    }

    public ValueTask ShowDocumentAsync(DocumentRequest request, CancellationToken cancellationToken = default)
    {
        Documents.Add(request);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> PickOpenFileAsync(FilePickerRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<string?>(null);

    public ValueTask<SaveFilePickResult?> PickSaveFileAsync(FilePickerRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SavePicker?.Invoke(request));

    public ValueTask<string?> PickFolderAsync(string title, string? suggestedDirectory = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(FolderPicker?.Invoke(suggestedDirectory));

    public ValueTask<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        Confirmations.Add(request);
        return ValueTask.FromResult(Confirm(request));
    }

    public ValueTask ShowMessageAsync(MessageRequest request, CancellationToken cancellationToken = default)
    {
        Messages.Add(request);
        return ValueTask.CompletedTask;
    }
}

/// <summary>The real resolver, with hooks that fire around exclusive creation.</summary>
internal sealed class RecordingResolver : ISafePathResolver
{
    private readonly SafePathResolver _inner = new();

    public List<string> CreateNewPaths { get; } = [];

    public List<string> ResolvePaths { get; } = [];

    public Action<string>? BeforeCreateNew { get; set; }

    public Action<string, PathResolution>? AfterCreateNew { get; set; }

    public ValueTask<PathResolution> ResolveAsync(string path, PathResolutionOptions options, CancellationToken cancellationToken = default)
    {
        ResolvePaths.Add(path);
        return _inner.ResolveAsync(path, options, cancellationToken);
    }

    public async ValueTask<PathResolution> CreateNewAsync(string path, PathResolutionOptions options, CancellationToken cancellationToken = default)
    {
        CreateNewPaths.Add(path);
        BeforeCreateNew?.Invoke(path);

        var resolution = await _inner.CreateNewAsync(path, options, cancellationToken).ConfigureAwait(false);

        AfterCreateNew?.Invoke(path, resolution);
        return resolution;
    }
}

/// <summary>The real durability barrier, with its call order recorded.</summary>
internal sealed class RecordingDurability : IDurabilityBarrier
{
    private readonly PlatformDurabilityBarrier _inner = new();

    public List<string> Calls { get; } = [];

    public Func<string, string, bool, ReplaceResult>? ReplaceOverride { get; set; }

    public DirectoryFlushResult? LastDirectoryFlush { get; private set; }

    public ValueTask FlushFileAsync(FileStream stream, CancellationToken cancellationToken = default)
    {
        Calls.Add("flush-file");
        return _inner.FlushFileAsync(stream, cancellationToken);
    }

    public async ValueTask<ReplaceResult> ReplaceAsync(string temporaryPath, FileIdentity temporaryIdentity, string destinationPath, bool destinationExists, CancellationToken cancellationToken = default)
    {
        Calls.Add("replace");

        if (ReplaceOverride is not null)
        {
            return ReplaceOverride(temporaryPath, destinationPath, destinationExists);
        }

        return await _inner.ReplaceAsync(temporaryPath, temporaryIdentity, destinationPath, destinationExists, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DirectoryFlushResult> FlushDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        Calls.Add("flush-directory");
        var result = await _inner.FlushDirectoryAsync(directoryPath, cancellationToken).ConfigureAwait(false);
        LastDirectoryFlush = result;
        return result;
    }
}

/// <summary>The real permission policy, with the widening gate forceable.</summary>
internal sealed class RecordingPermissions : IFilePermissionPolicy
{
    private readonly PlatformFilePermissionPolicy _inner = new();

    public bool ForceWidening { get; set; }

    public PermissionCopyResult? ForceCopyResult { get; set; }

    public int CopyCalls { get; private set; }

    public PermissionSnapshot Capture(FileStream stream) => _inner.Capture(stream);

    public PermissionCopyResult CopyOnto(FileStream original, FileStream target, string targetPath, FileIdentity targetIdentity)
    {
        CopyCalls++;
        return ForceCopyResult ?? _inner.CopyOnto(original, target, targetPath, targetIdentity);
    }

    public bool IsBroaderThan(PermissionSnapshot candidate, PermissionSnapshot original, out string detail)
    {
        if (ForceWidening)
        {
            detail = "Forced widening for the abort-path test.";
            return true;
        }

        return _inner.IsBroaderThan(candidate, original, out detail);
    }
}

/// <summary>The real change guard, with individual verifications overridable by call index.</summary>
internal sealed class RecordingGuard : IExternalChangeGuard
{
    private readonly ExternalChangeGuard _inner = new();

    public int VerifyCalls { get; private set; }

    public Func<int, ExternalChangeCheck?>? VerifyOverride { get; set; }

    public ValueTask<(ContentBaseline Baseline, byte[] Bytes)> CaptureAsync(ResolvedFile file, CancellationToken cancellationToken = default) =>
        _inner.CaptureAsync(file, cancellationToken);

    public async ValueTask<ExternalChangeCheck> VerifyAsync(ResolvedFile file, ContentBaseline baseline, CancellationToken cancellationToken = default)
    {
        VerifyCalls++;

        var forced = VerifyOverride?.Invoke(VerifyCalls);
        if (forced is { } value)
        {
            return value;
        }

        return await _inner.VerifyAsync(file, baseline, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A name source that hands out names a test can pre-plant something at.</summary>
internal sealed class FixedFileNames : IWorkflowFileNames
{
    public FixedFileNames(string temporaryName, string backupName)
    {
        TemporaryName = temporaryName;
        BackupName = backupName;
    }

    public string TemporaryName { get; set; }

    public string BackupName { get; set; }

    public string NextTemporaryFileName() => TemporaryName;

    public string NextBackupFileName(string originalFileName) => BackupName;
}

/// <summary>A clock that does not move, so two operations share one timestamp component.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>A progress sink that runs an action inline, so a test can act mid-operation.</summary>
internal sealed class HookProgress : IProgress<SaveProgress>
{
    private readonly Action<SaveProgress> _action;

    public HookProgress(Action<SaveProgress> action) => _action = action;

    public void Report(SaveProgress value) => _action(value);
}

/// <summary>Everything one workflow test needs, wired together.</summary>
internal sealed class WorkflowHarness : IDisposable
{
    public WorkflowHarness(string label)
    {
        Workspace = new TempWorkspace(label);
    }

    public TempWorkspace Workspace { get; }

    public TestCodec Codec { get; } = new();

    public TestDetector Detector { get; } = new();

    public FakeInteraction Interaction { get; } = new();

    public RecordingResolver Resolver { get; } = new();

    public RecordingDurability Durability { get; } = new();

    public RecordingPermissions Permissions { get; } = new();

    public RecordingGuard Guard { get; } = new();

    public IWorkflowFileNames FileNames { get; set; } = new WorkflowFileNames();

    public bool VerifyPreservationClaim { get; set; } = true;

    public bool VerifyRoundTrip { get; set; } = true;

    public int BackupRetention { get; set; } = 10;

    public SafeFileWorkflowOptions<TestDocument> Options => new()
    {
        Registry = new SaveCodecRegistry<TestDocument>([new CodecRegistration<TestDocument>(Detector, Codec)]),
        Interaction = Interaction,
        PathResolver = Resolver,
        Durability = Durability,
        Permissions = Permissions,
        ChangeGuard = Guard,
        FileNames = FileNames,
        VerifyPreservationClaim = VerifyPreservationClaim,
        VerifyRoundTripBeforeReplace = VerifyRoundTrip,
        BackupRetention = BackupRetention,
    };

    public SafeFileWorkflow<TestDocument> Create() => new(Options);

    /// <summary>Writes a save file into the workspace and returns its path.</summary>
    public string WriteSave(string name, TestDocument document)
    {
        var path = Workspace.Path(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TestCodec.Encode(document));
        return path;
    }

    /// <summary>Opens a save file, asserting that the open succeeded.</summary>
    public async Task<OpenSaveFile<TestDocument>> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var outcome = await Create().OpenAsync(path, cancellationToken: cancellationToken);
        var opened = Assert.IsType<OpenOutcome<TestDocument>.Opened>(outcome);
        return opened.File;
    }

    /// <summary>Every framework temporary file left in a directory.</summary>
    public static IReadOnlyList<string> TemporaryResidue(string directory) =>
    [
        .. Directory.EnumerateFiles(directory)
            .Where(path => WorkflowFileNames.IsFrameworkTemporaryName(Path.GetFileName(path))),
    ];

    /// <summary>Every framework backup left in a directory.</summary>
    public static IReadOnlyList<string> Backups(string directory) =>
    [
        .. Directory.EnumerateFiles(directory)
            .Where(path => WorkflowFileNames.IsFrameworkBackupName(Path.GetFileName(path))),
    ];

    /// <summary>Asserts success, reporting the framework's own explanation when it is not.</summary>
    public static void AssertSucceeded(SaveOutcome outcome) =>
        Assert.True(
            outcome.Status == SaveStatus.Succeeded,
            $"Expected a successful save but got {outcome.Status}/{outcome.Reason}: {outcome.Message}");

    public void Dispose() => Workspace.Dispose();
}

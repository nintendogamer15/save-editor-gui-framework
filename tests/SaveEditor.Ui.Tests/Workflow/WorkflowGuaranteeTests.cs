using System.Text;
using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Tests.Io;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// The guarantees the workflow makes about what is on disk when something goes wrong:
/// all-or-nothing backups, contained codec faults, authoritative cancellation, and the
/// original-file preservation wording of <c>PLAN.md</c> §7.
/// </summary>
public sealed class WorkflowGuaranteeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TestDocument SampleDocument => new("hero", 3, "trailer-bytes");

    [Fact]
    public async Task Workflow_AbortsOverwriteWhenBackupWriteFailsMidway()
    {
        using var harness = new WorkflowHarness("backup-midway");
        harness.Codec.PreservesUnknownData = false;

        // Large enough that the copy runs several chunks, so the injected failure lands
        // after real bytes have already been written to the backup.
        var document = SampleDocument with { Trailer = new string('x', 1024 * 1024) };
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        FileStream? backupStream = null;
        harness.Resolver.AfterCreateNew = (path, resolution) =>
        {
            if (WorkflowFileNames.IsFrameworkBackupName(Path.GetFileName(path)) &&
                resolution is SaveEditor.Ui.Io.PathResolution.Resolved resolved)
            {
                backupStream = resolved.File.Stream;
            }
        };

        var broken = false;
        var progress = new HookProgress(report =>
        {
            if (report.Phase == SavePhase.WritingBackup && report.BytesCompleted > 0 && !broken && backupStream is not null)
            {
                broken = true;

                // The destination handle disappears from under the copy: the next chunk
                // fails, exactly as a disk-full or a removed volume would.
                backupStream.Dispose();
            }
        });

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, progress, Token);

        Assert.True(broken, "The failure was never injected, so this test proved nothing.");
        Assert.Equal(SaveStatus.Failed, outcome.Status);
        Assert.Equal(SaveFailureReason.BackupFailed, outcome.Reason);

        // All or nothing: the partial backup is gone and the original is untouched.
        Assert.Empty(WorkflowHarness.Backups(harness.Workspace.Root));
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.DoesNotContain("replace", harness.Durability.Calls);
        Assert.Empty(WorkflowHarness.TemporaryResidue(harness.Workspace.Root));
    }

    [Fact]
    public async Task Workflow_AbortsOverwriteWhenBackupHashMismatchesBaseline()
    {
        using var harness = new WorkflowHarness("backup-hash");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        FileStream? backupStream = null;
        harness.Resolver.AfterCreateNew = (path, resolution) =>
        {
            if (WorkflowFileNames.IsFrameworkBackupName(Path.GetFileName(path)) &&
                resolution is SaveEditor.Ui.Io.PathResolution.Resolved resolved)
            {
                backupStream = resolved.File.Stream;
            }
        };

        var corrupted = false;
        var progress = new HookProgress(report =>
        {
            if (report.Phase == SavePhase.VerifyingBackup && !corrupted && backupStream is not null)
            {
                corrupted = true;

                // The copy completed and flushed. The bytes on disk are still not what was
                // read from the original, which is precisely what "attempted, never
                // verified" would have missed.
                backupStream.Write("tampered"u8);
                backupStream.Flush();
            }
        });

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, progress, Token);

        Assert.True(corrupted, "The corruption was never injected, so this test proved nothing.");
        Assert.Equal(SaveStatus.Failed, outcome.Status);
        Assert.Equal(SaveFailureReason.BackupFailed, outcome.Reason);
        Assert.Contains("hash", outcome.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(WorkflowHarness.Backups(harness.Workspace.Root));
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.DoesNotContain("replace", harness.Durability.Calls);
    }

    [Theory]
    [InlineData("validation-error")]
    [InlineData("validate-throws-after-backup")]
    [InlineData("serialize-throws")]
    [InlineData("lossy-serializer")]
    [InlineData("permission-widening")]
    [InlineData("external-change")]
    [InlineData("replace-not-atomic")]
    [InlineData("replace-failed")]
    [InlineData("cancelled")]
    [InlineData("temp-pre-planted")]
    [InlineData("backup-pre-planted")]
    public async Task Workflow_TargetBytesUnchangedAfterFailureAtEveryStage(string stage)
    {
        using var harness = new WorkflowHarness($"stage-{stage}");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        using var cancellation = new CancellationTokenSource();
        var token = Token;

        switch (stage)
        {
            case "validation-error":
                harness.Codec.ValidateOverride = (_, _, _) => ValueTask.FromResult(new ValidationReport
                {
                    Messages = [new ValidationMessage(ValidationSeverity.Error, new UntrustedText("this document is invalid"))],
                });
                break;

            case "validate-throws-after-backup":
                harness.Codec.ValidateOverride = (_, call, _) => call >= 3
                    ? throw new InvalidOperationException("the codec threw from Validate")
                    : ValueTask.FromResult(ValidationReport.Empty);
                break;

            case "serialize-throws":
                harness.Codec.SerializeOverride = async (_, destination, cancellationToken) =>
                {
                    await destination.WriteAsync(new byte[64], cancellationToken);
                    throw new InvalidOperationException("the codec threw from Serialize");
                };
                break;

            case "lossy-serializer":
                harness.Codec.SerializeOverride = (doc, destination, cancellationToken) =>
                    destination.WriteAsync(TestCodec.Encode(doc with { Level = 0 }), cancellationToken);
                break;

            case "permission-widening":
                harness.Permissions.ForceWidening = true;
                break;

            case "external-change":
                harness.Guard.VerifyOverride = call => call >= 2
                    ? new ExternalChangeCheck(ExternalChangeVerdict.Changed, true, false, "the bytes changed")
                    : null;
                break;

            case "replace-not-atomic":
                harness.Durability.ReplaceOverride = (_, _, _) =>
                    new ReplaceResult(ReplaceStatus.NotAtomic, "no atomic replacement here");
                break;

            case "replace-failed":
                harness.Durability.ReplaceOverride = (_, _, _) =>
                    new ReplaceResult(ReplaceStatus.Failed, "the replace failed");
                break;

            case "cancelled":
                await cancellation.CancelAsync();
                token = cancellation.Token;
                break;

            case "temp-pre-planted":
                harness.FileNames = new FixedFileNames(
                    ".saveeditor-tmp-0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f.part",
                    "save.sav.saveeditor-backup.20260101T000000Z.0f0f0f0f.bak");
                File.WriteAllBytes(harness.Workspace.Path(".saveeditor-tmp-0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f.part"), [1, 2, 3]);
                break;

            case "backup-pre-planted":
                harness.FileNames = new FixedFileNames(
                    ".saveeditor-tmp-0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f.part",
                    "save.sav.saveeditor-backup.20260101T000000Z.0f0f0f0f.bak");
                File.WriteAllBytes(harness.Workspace.Path("save.sav.saveeditor-backup.20260101T000000Z.0f0f0f0f.bak"), [1, 2, 3]);
                break;

            default:
                Assert.Fail($"Unknown stage '{stage}'.");
                break;
        }

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: token);

        Assert.NotEqual(SaveStatus.Succeeded, outcome.Status);

        // The guarantee, stated exactly: on any failure the bytes at the target path are
        // the pre-operation bytes.
        Assert.Equal(targetBytes, File.ReadAllBytes(target));

        // And nothing half-written was left lying next to it.
        var residue = WorkflowHarness.TemporaryResidue(harness.Workspace.Root);
        if (stage == "temp-pre-planted")
        {
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Assert.Single(residue)));
        }
        else
        {
            Assert.Empty(residue);
        }
    }

    [Fact]
    public async Task Workflow_DowngradesFalsifiedUnknownDataCapabilityClaim()
    {
        using var harness = new WorkflowHarness("falsified-claim");

        // The codec declares preservation and then drops the trailer on the way out.
        harness.Codec.PreservesUnknownData = true;
        harness.Codec.SerializeOverride = (doc, destination, cancellationToken) =>
            destination.WriteAsync(TestCodec.Encode(doc with { Trailer = string.Empty }), cancellationToken);

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        // Verified, not trusted: re-serializing the untouched document did not reproduce
        // the file, so the declaration is empirically false.
        Assert.Equal(UnknownDataVerification.Falsified, open.UnknownData);
        Assert.True(harness.Codec.PreservesUnknownData);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Trailer = string.Empty, Level = 9 }, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(outcome);

        // The downgrade shows up where it matters: the destructive confirmation says the
        // claim was tested and is false, instead of reporting a clean save.
        var confirmation = Assert.Single(harness.Interaction.Confirmations);
        Assert.Contains("it is false", confirmation.Message, StringComparison.Ordinal);
        Assert.True(confirmation.IsDestructive);
    }

    [Fact]
    public async Task Workflow_VerifiesAnHonestUnknownDataCapabilityClaim()
    {
        using var harness = new WorkflowHarness("honest-claim");
        harness.Codec.PreservesUnknownData = true;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        Assert.Equal(UnknownDataVerification.Verified, open.UnknownData);

        await harness.Create().OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        var confirmation = Assert.Single(harness.Interaction.Confirmations);
        Assert.DoesNotContain("it is false", confirmation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_DetectsLossySerializerViaPreReplaceRoundTrip()
    {
        using var harness = new WorkflowHarness("lossy-serializer");
        harness.Codec.PreservesUnknownData = false;

        // The bytes are well-formed and decode cleanly. They are simply not the document
        // that is open — the level is dropped on the way out.
        harness.Codec.SerializeOverride = (doc, destination, cancellationToken) =>
            destination.WriteAsync(TestCodec.Encode(doc with { Level = 0 }), cancellationToken);

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, outcome.Status);
        Assert.Equal(SaveFailureReason.RoundTripMismatch, outcome.Reason);
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.DoesNotContain("replace", harness.Durability.Calls);

        // The opt-out is documented and it really does opt out, which is why it is not the
        // default: the same lossy codec then reaches the destination.
        harness.VerifyRoundTrip = false;
        var permitted = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(permitted);
    }

    [Fact]
    public async Task Workflow_CodecThrowFromValidateAfterBackupLeavesTargetIntact()
    {
        using var harness = new WorkflowHarness("validate-throws");
        harness.Codec.PreservesUnknownData = false;

        // Calls one and two gate the confirmation. Call three is the full validation run
        // immediately before writing, which happens after the backup has been taken.
        harness.Codec.ValidateOverride = (_, call, _) => call >= 3
            ? throw new InvalidOperationException("the codec threw from Validate")
            : ValueTask.FromResult(ValidationReport.Empty);

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, outcome.Status);
        Assert.Equal(SaveFailureReason.CodecFailed, outcome.Reason);
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.DoesNotContain("replace", harness.Durability.Calls);
        Assert.Empty(WorkflowHarness.TemporaryResidue(harness.Workspace.Root));

        // The backup was already complete and verified before the throw, so it is reported
        // rather than discarded: it is a good copy of a file that still exists.
        var backup = Assert.Single(WorkflowHarness.Backups(harness.Workspace.Root));
        Assert.Equal(backup, outcome.BackupPath);
        Assert.Equal(targetBytes, File.ReadAllBytes(backup));
    }

    [Fact]
    public async Task Workflow_CodecThrowMidSerializeNeverReachesReplace()
    {
        using var harness = new WorkflowHarness("serialize-throws");
        harness.Codec.PreservesUnknownData = false;

        harness.Codec.SerializeOverride = async (_, destination, cancellationToken) =>
        {
            await destination.WriteAsync(Encoding.UTF8.GetBytes("SEDT|partially-written"), cancellationToken);
            throw new InvalidOperationException("the codec threw halfway through Serialize");
        };

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, outcome.Status);
        Assert.Equal(SaveFailureReason.CodecFailed, outcome.Reason);

        // Serialization completes into bounded memory before any file is created, so a
        // mid-serialize throw does not even reach the exclusive create, let alone a replace.
        Assert.DoesNotContain("replace", harness.Durability.Calls);
        Assert.DoesNotContain(harness.Resolver.CreateNewPaths, path => WorkflowFileNames.IsFrameworkTemporaryName(Path.GetFileName(path)));
        Assert.Empty(WorkflowHarness.TemporaryResidue(harness.Workspace.Root));
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Workflow_DiscardsLateResultFromCancelledOperation()
    {
        using var harness = new WorkflowHarness("late-result");
        harness.Codec.PreservesUnknownData = false;

        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.Codec.SerializeOverride = async (doc, destination, _) =>
        {
            entered.TrySetResult();

            // Deliberately ignores the token: a cooperative token against third-party code
            // is a request, and this codec does not honour it.
            release.Wait(TimeSpan.FromSeconds(30));

            try
            {
                await destination.WriteAsync(TestCodec.Encode(doc), CancellationToken.None);
            }
            catch (Exception)
            {
            }
            finally
            {
                finished.TrySetResult();
            }
        };

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);
        using var cancellation = new CancellationTokenSource();

        var operation = harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: cancellation.Token)
            .AsTask();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        await cancellation.CancelAsync();

        var outcome = await operation.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(SaveStatus.Cancelled, outcome.Status);

        // Now let the abandoned codec finish and produce its result. It is discarded.
        release.Set();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.DoesNotContain("replace", harness.Durability.Calls);
        Assert.Empty(WorkflowHarness.TemporaryResidue(harness.Workspace.Root));
    }

    [Fact]
    public async Task Backup_TwoOverwritesWithinOneSecondDoNotCollide()
    {
        using var harness = new WorkflowHarness("backup-collision");
        harness.Codec.PreservesUnknownData = false;

        // A clock that does not move at all, so the timestamp component of both names is
        // byte-for-byte identical and only the entropy can separate them.
        harness.FileNames = new WorkflowFileNames(new FixedTimeProvider(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)));

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);
        var workflow = harness.Create();

        var first = await workflow.OverwriteWithBackupAsync(document with { Level = 4 }, open, cancellationToken: Token);
        var second = await workflow.OverwriteWithBackupAsync(document with { Level = 5 }, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(first);
        WorkflowHarness.AssertSucceeded(second);

        var backups = WorkflowHarness.Backups(harness.Workspace.Root);
        Assert.Equal(2, backups.Count);
        Assert.NotEqual(first.BackupPath, second.BackupPath);

        // Same second, same timestamp field, different files.
        Assert.All(backups, path => Assert.Contains("20260826T120000Z", Path.GetFileName(path), StringComparison.Ordinal));
    }

    [Fact]
    public void Backup_RetentionCapAppliesOnlyToFrameworkGrammar()
    {
        using var workspace = new TempWorkspace("backup-retention");

        string Make(string name)
        {
            var path = workspace.Path(name);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(name));
            return path;
        }

        var oldest = Make("save.sav.saveeditor-backup.20260101T000000Z.00000001.bak");
        var older = Make("save.sav.saveeditor-backup.20260102T000000Z.00000002.bak");
        var newer = Make("save.sav.saveeditor-backup.20260103T000000Z.00000003.bak");
        var newest = Make("save.sav.saveeditor-backup.20260104T000000Z.00000004.bak");

        // Not the framework grammar: no entropy field, uppercase entropy, a different
        // original, and a backup the user made by hand.
        var noEntropy = Make("save.sav.saveeditor-backup.20260101T000000Z.bak");
        var uppercase = Make("save.sav.saveeditor-backup.20260101T000000Z.DEADBEEF.bak");
        var otherOriginal = Make("other.sav.saveeditor-backup.20260101T000000Z.00000005.bak");
        var handMade = Make("save.sav.backup");

        var removed = BackupRetention.Apply(workspace.Root, "save.sav", retain: 2);

        Assert.Equal(2, removed.Count);
        Assert.Contains(oldest, removed);
        Assert.Contains(older, removed);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(older));

        // The cap keeps the newest, ordered by the timestamp encoded in the name rather
        // than by an mtime an external writer could rewrite.
        Assert.True(File.Exists(newer), "The retention cap removed one of the newest backups.");
        Assert.True(File.Exists(newest), "The retention cap removed one of the newest backups.");

        Assert.True(File.Exists(noEntropy), "A near-miss of the grammar was deleted.");
        Assert.True(File.Exists(uppercase), "A near-miss of the grammar was deleted.");
        Assert.True(File.Exists(otherOriginal), "A backup of a different original was deleted.");
        Assert.True(File.Exists(handMade), "A file the user made was deleted.");
    }

    [Fact]
    public void Workflow_StartupSweepRemovesOnlyPrefixedAgedTempFiles()
    {
        using var workspace = new TempWorkspace("sweep");

        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

        string Make(string name, TimeSpan age)
        {
            var path = workspace.Path(name);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(name));
            File.SetLastWriteTimeUtc(path, (now - age).UtcDateTime);
            return path;
        }

        var agedResidue = Make(".saveeditor-tmp-00112233445566778899aabbccddeeff.part", TimeSpan.FromDays(3));
        var freshResidue = Make(".saveeditor-tmp-ffeeddccbbaa99887766554433221100.part", TimeSpan.FromMinutes(5));
        var nearMiss = Make(".saveeditor-tmp-not-hex.part", TimeSpan.FromDays(3));
        var wrongSuffix = Make(".saveeditor-tmp-00112233445566778899aabbccddeeff.tmp", TimeSpan.FromDays(3));
        var unrelated = Make("save.sav", TimeSpan.FromDays(30));

        var report = TempResidueSweeper.Sweep(
            [workspace.Root],
            TimeSpan.FromHours(6),
            new FixedTimeProvider(now));

        Assert.Equal([agedResidue], report.Removed);

        Assert.False(File.Exists(agedResidue));
        Assert.True(File.Exists(freshResidue), "A temporary file young enough to belong to a live operation was removed.");
        Assert.True(File.Exists(nearMiss), "A file that only shares the prefix was removed.");
        Assert.True(File.Exists(wrongSuffix), "A file that only shares the prefix was removed.");
        Assert.True(File.Exists(unrelated), "An unrelated file was removed.");
    }

    [Fact]
    public async Task FailurePolicy_SettingsFailSoftWhileSaveFailsLoud()
    {
        using var harness = new WorkflowHarness("failure-policy");
        harness.Codec.PreservesUnknownData = false;

        // --- Settings: fail-soft. An unusable settings location produces no exception and
        // no blocked startup, only a reported loss of persistence.
        var blocker = harness.Workspace.Path("not-a-directory");
        File.WriteAllBytes(blocker, [0]);

        var store = new EditorSettingsStore(
            EditorApplicationId.Parse("save-editor-tests"),
            new EditorSettingsStoreOptions { BaseDirectory = Path.Combine(blocker, "settings") });

        Assert.False(store.IsPersistent);

        var settings = await store.LoadAsync(Token);
        await store.SaveAsync(settings, Token);

        Assert.False(store.IsPersistent);

        // --- Saves: fail-loud. The same class of failure produces a definitive status and
        // a message, and never a silently skipped write.
        harness.Durability.ReplaceOverride = (_, _, _) =>
            new ReplaceResult(ReplaceStatus.Failed, "the replace failed");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, outcome.Status);
        Assert.NotEqual(SaveFailureReason.None, outcome.Reason);
        Assert.NotEmpty(outcome.Message);
        Assert.NotEmpty(harness.Interaction.Messages);
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Validation_ErrorsBlockSaveAsToNewPathAndOverwriteAlike()
    {
        using var harness = new WorkflowHarness("validation-blocks");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);

        harness.Codec.ValidateOverride = (_, _, _) => ValueTask.FromResult(new ValidationReport
        {
            Messages =
            [
                new ValidationMessage(ValidationSeverity.Warning, new UntrustedText("a warning that does not block")),
                new ValidationMessage(ValidationSeverity.Error, new UntrustedText("an error that does")),
            ],
        });

        // Save As to a path that does not exist yet.
        var fresh = harness.Workspace.Path("fresh.sav");
        harness.Interaction.SavePicker = _ => new SaveFilePickResult(fresh, PickerConfirmedOverwrite: false);

        var saveAs = await harness.Create()
            .SaveAsAsync(document with { Level = 9 }, harness.Codec, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, saveAs.Status);
        Assert.Equal(SaveFailureReason.ValidationErrors, saveAs.Reason);
        Assert.False(File.Exists(fresh), "A new file was created for a document that failed validation.");

        // The same errors block an overwrite, and they block it before a backup is taken.
        var overwrite = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, overwrite.Status);
        Assert.Equal(SaveFailureReason.ValidationErrors, overwrite.Reason);
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.Empty(WorkflowHarness.Backups(harness.Workspace.Root));

        // The user was told, on both paths, in framework-authored words.
        Assert.Equal(2, harness.Interaction.Messages.Count);
        Assert.All(harness.Interaction.Messages, message => Assert.Contains("errors", message.Message, StringComparison.OrdinalIgnoreCase));
    }
}

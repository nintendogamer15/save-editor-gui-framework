using System.Text;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Io;
using SaveEditor.Ui.Tests.Io;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// The destructive half of the workflow: exclusive creation, permission preservation,
/// external-change guarding, atomic replacement, and the confirmations that gate them.
/// </summary>
public sealed class WorkflowWriteTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TestDocument SampleDocument => new("hero", 3, "trailer-bytes");

    [Fact]
    public async Task Workflow_AbortsWhenTempOrBackupPathPrePlantedAsLink()
    {
        using var harness = new WorkflowHarness("plant-link");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        var victim = harness.Workspace.Path("victim.dat");
        File.WriteAllBytes(victim, Encoding.UTF8.GetBytes("the victim's own bytes"));
        var victimBytes = File.ReadAllBytes(victim);

        var names = new FixedFileNames(
            ".saveeditor-tmp-00112233445566778899aabbccddeeff.part",
            "save.sav.saveeditor-backup.20260101T000000Z.deadbeef.bak");
        harness.FileNames = names;

        var backupPath = harness.Workspace.Path(names.BackupName);
        var plantFailure = PlantLink(backupPath, victim);
        Assert.SkipWhen(plantFailure is not null, $"Could not plant a link at the backup path: {plantFailure}");

        using var open = await harness.OpenAsync(target, Token);

        var backupOutcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, backupOutcome.Status);
        Assert.Equal(SaveFailureReason.BackupFailed, backupOutcome.Reason);
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.Equal(victimBytes, File.ReadAllBytes(victim));

        // The planted entry is left exactly as it was found. Refusal never becomes a
        // truncating retry through a link-following open.
        File.Delete(backupPath);
        Assert.Equal(victimBytes, File.ReadAllBytes(victim));

        var temporaryPath = harness.Workspace.Path(names.TemporaryName);
        var temporaryPlantFailure = PlantLink(temporaryPath, victim);
        Assert.SkipWhen(temporaryPlantFailure is not null, $"Could not plant a link at the temporary path: {temporaryPlantFailure}");

        var temporaryOutcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, temporaryOutcome.Status);
        Assert.Equal(SaveFailureReason.TempCreationFailed, temporaryOutcome.Reason);
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.Equal(victimBytes, File.ReadAllBytes(victim));
    }

    [Fact]
    public async Task Workflow_TempNameCarriesEntropyAndUsesExclusiveCreate()
    {
        using var harness = new WorkflowHarness("temp-entropy");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        var first = await harness.Create().OverwriteWithBackupAsync(document with { Level = 4 }, open, cancellationToken: Token);
        WorkflowHarness.AssertSucceeded(first);

        var second = await harness.Create().OverwriteWithBackupAsync(document with { Level = 5 }, open, cancellationToken: Token);
        WorkflowHarness.AssertSucceeded(second);

        var temporaryNames = harness.Resolver.CreateNewPaths
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.StartsWith(WorkflowFileNames.TemporaryPrefix, StringComparison.Ordinal))
            .Select(name => name!)
            .ToList();

        Assert.Equal(2, temporaryNames.Count);

        foreach (var name in temporaryNames)
        {
            // Grammar, not just prefix: sixteen bytes of hex between the fixed prefix and
            // the fixed suffix.
            Assert.True(
                WorkflowFileNames.IsFrameworkTemporaryName(name),
                $"'{name}' does not match the framework temporary grammar.");

            var entropy = name[WorkflowFileNames.TemporaryPrefix.Length..^WorkflowFileNames.TemporarySuffix.Length];
            Assert.Equal(32, entropy.Length);
            Assert.NotEqual(new string('0', 32), entropy);
        }

        Assert.NotEqual(temporaryNames[0], temporaryNames[1]);

        // Every temporary and backup file the workflow created went through the exclusive
        // create entry point. Nothing was produced by resolving an existing path.
        Assert.Contains(harness.Resolver.CreateNewPaths, path => Path.GetFileName(path) == temporaryNames[0]);
        Assert.DoesNotContain(harness.Resolver.ResolvePaths, path => Path.GetFileName(path)?.StartsWith(WorkflowFileNames.TemporaryPrefix, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(harness.Resolver.ResolvePaths, path => WorkflowFileNames.IsFrameworkBackupName(Path.GetFileName(path) ?? string.Empty));

        // No residue: both temporary files became the destination.
        Assert.Empty(WorkflowHarness.TemporaryResidue(harness.Workspace.Root));
    }

    [Fact]
    public async Task Workflow_AbortsWhenContentChangesBetweenCheckAndReplace()
    {
        using var harness = new WorkflowHarness("external-change");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var originalBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);

        byte[]? mutated = null;
        string? sharingRefusal = null;

        harness.Resolver.BeforeCreateNew = path =>
        {
            if (!WorkflowFileNames.IsFrameworkTemporaryName(Path.GetFileName(path)))
            {
                return;
            }

            if (mutated is not null || sharingRefusal is not null)
            {
                return;
            }

            // Same length, different bytes: the metadata short-circuit cannot see this,
            // so only the hash comparison can catch it.
            var replacement = (byte[])originalBytes.Clone();
            replacement[^1] = (byte)(replacement[^1] ^ 0xFF);

            try
            {
                using var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(replacement);
                stream.Flush();
                mutated = replacement;
            }
            catch (IOException ex)
            {
                sharingRefusal = ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                sharingRefusal = ex.Message;
            }
        };

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        if (mutated is not null)
        {
            // The window is open on this platform, and the guard closed the operation.
            Assert.Equal(SaveStatus.Failed, outcome.Status);
            Assert.Equal(SaveFailureReason.ExternalChange, outcome.Reason);
            Assert.Equal(mutated, File.ReadAllBytes(target));
            Assert.Empty(WorkflowHarness.TemporaryResidue(harness.Workspace.Root));
        }
        else
        {
            // Windows holds the original with write sharing denied for the whole
            // operation, so the external write could not happen at all. That is the
            // stronger guarantee, and asserting it is what makes this test non-vacuous
            // here rather than skipped.
            Assert.NotNull(sharingRefusal);
            Assert.True(
                OperatingSystem.IsWindows(),
                "An external writer was refused on a platform that does not deny write sharing, which is unexpected.");
            WorkflowHarness.AssertSucceeded(outcome);
        }
    }

    [Fact]
    public async Task Workflow_MetadataOnlyMatchDoesNotSatisfyPositiveGuard()
    {
        using var workspace = new TempWorkspace("metadata-guard");
        var resolver = new SafePathResolver();
        var guard = new ExternalChangeGuard();

        var path = workspace.Path("save.sav");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("AAAAAAAAAAAAAAAA"));
        var stamp = File.GetLastWriteTimeUtc(path);

        ContentBaseline baseline;
        using (var first = await Resolve(resolver, path))
        {
            (baseline, _) = await guard.CaptureAsync(first, Token);
        }

        // Unchanged: the positive answer still costs a full hash. Metadata equality alone
        // never produces it.
        using (var unchanged = await Resolve(resolver, path))
        {
            var check = await guard.VerifyAsync(unchanged, baseline, Token);
            Assert.Equal(ExternalChangeVerdict.Unchanged, check.Verdict);
            Assert.True(check.HashCompared);
        }

        // Same length, restored timestamp — metadata is indistinguishable from the
        // baseline, and mtime is trivially restorable, so only the hash can tell.
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("AAAAAAAABBBBBBBB"));
        File.SetLastWriteTimeUtc(path, stamp);

        using (var swapped = await Resolve(resolver, path))
        {
            var check = await guard.VerifyAsync(swapped, baseline, Token);

            Assert.Equal(ExternalChangeVerdict.Changed, check.Verdict);
            Assert.True(check.HashCompared);
            Assert.False(check.MetadataDiffered);
        }

        // A different length is the one sound metadata inference, and it short-circuits.
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("AAAA"));
        File.SetLastWriteTimeUtc(path, stamp);

        using (var shortened = await Resolve(resolver, path))
        {
            var check = await guard.VerifyAsync(shortened, baseline, Token);

            Assert.Equal(ExternalChangeVerdict.Changed, check.Verdict);
            Assert.False(check.HashCompared);
        }
    }

    [Fact]
    public async Task Workflow_PreservesModeSoZeroSixHundredStaysZeroSixHundred()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "POSIX mode bits do not exist on Windows. This case covers rename(2) replacing a 0600 file with a 0644 temporary one, which is a Linux behaviour; Windows coverage of the same finding is Workflow_AbortsWhenReplaceWouldWidenPermissions.");

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var harness = new WorkflowHarness("preserve-mode");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(target, Private);
        Assert.Equal(Private, File.GetUnixFileMode(target));

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(outcome);

        // rename(2) would have given the destination the temporary file's 0600-or-umask
        // mode. The workflow copies the original's mode onto the temporary file first.
        Assert.Equal(Private, File.GetUnixFileMode(target));

        // Backups inherit the original's mode, not the directory default.
        var backup = Assert.Single(WorkflowHarness.Backups(harness.Workspace.Root));
        Assert.Equal(Private, File.GetUnixFileMode(backup));
    }

    [Fact]
    public async Task Workflow_AbortsWhenReplaceWouldWidenPermissions()
    {
        using var harness = new WorkflowHarness("widening");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        harness.Permissions.ForceWidening = true;

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, outcome.Status);
        Assert.Equal(SaveFailureReason.PermissionWidening, outcome.Reason);
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.DoesNotContain("replace", harness.Durability.Calls);
        Assert.Empty(WorkflowHarness.TemporaryResidue(harness.Workspace.Root));
    }

    [Fact]
    public void Workflow_WideningComparatorRecognizesABroaderPermissionSet()
    {
        var policy = new PlatformFilePermissionPolicy();

        var restricted = new PermissionSnapshot(UnixFileMode.UserRead | UnixFileMode.UserWrite, null, "0600");
        var relaxed = new PermissionSnapshot(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            null,
            "0644");

        Assert.True(policy.IsBroaderThan(relaxed, restricted, out var widening));
        Assert.Contains("0644", widening);
        Assert.False(policy.IsBroaderThan(restricted, relaxed, out _));
        Assert.False(policy.IsBroaderThan(restricted, restricted, out _));

        var owner = new PermissionSnapshot(null, new Dictionary<string, int> { ["S-1-5-21-1"] = 0x1 }, "acl");
        var everyone = new PermissionSnapshot(
            null,
            new Dictionary<string, int> { ["S-1-5-21-1"] = 0x1, ["S-1-1-0"] = 0x2 },
            "acl");

        Assert.True(policy.IsBroaderThan(everyone, owner, out var aclWidening));
        Assert.Contains("S-1-1-0", aclWidening);
        Assert.False(policy.IsBroaderThan(owner, everyone, out _));
    }

    [Fact]
    public async Task Workflow_ConfirmsOverwriteEvenWhenPickerDeclaresConfirmation()
    {
        using var harness = new WorkflowHarness("picker-confirms");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var other = harness.WriteSave("other.sav", new TestDocument("other", 1, "other-trailer"));

        using var open = await harness.OpenAsync(target, Token);

        // The picker claims it already confirmed. The target exists and is not the open
        // document, so the framework confirms anyway.
        harness.Interaction.SavePicker = _ => new SaveFilePickResult(other, PickerConfirmedOverwrite: true);

        var outcome = await harness.Create()
            .SaveAsAsync(document with { Level = 7 }, harness.Codec, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(outcome);

        var confirmation = Assert.Single(harness.Interaction.Confirmations);
        Assert.True(confirmation.IsDestructive);
        Assert.Equal("Overwrite save file", confirmation.AcceptLabel);
        Assert.DoesNotContain("OK", confirmation.AcceptLabel, StringComparison.Ordinal);

        // Nor over the document that is already open. Revision 3 suppressed that one as a
        // genuine duplicate, which held only while this path took no backup and restated no
        // preservation claim -- neither is true now, so the picker's declaration suppresses
        // nothing anywhere (finding F-2 supersedes finding A7).
        harness.Interaction.Confirmations.Clear();
        harness.Interaction.SavePicker = _ => new SaveFilePickResult(open.Path, PickerConfirmedOverwrite: true);

        var sameFile = await harness.Create()
            .SaveAsAsync(document with { Level = 8 }, harness.Codec, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(sameFile);

        var sameFileConfirmation = Assert.Single(harness.Interaction.Confirmations);
        Assert.True(sameFileConfirmation.IsDestructive);
        Assert.Contains("verified backup", sameFileConfirmation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>Save As</c> whose target already exists is a destructive overwrite, and takes the
    /// same all-or-nothing verified backup as the operation named after it.
    /// </summary>
    /// <remarks>
    /// Before this, <c>Save As</c> -- the path the README presents as the safe default -- was
    /// the only one that could replace an existing file leaving no recoverable copy, which
    /// inverted the safety labelling relative to the actual behaviour.
    /// </remarks>
    [Fact]
    public async Task SaveAs_OntoAnExistingFileTakesAVerifiedBackup()
    {
        using var harness = new WorkflowHarness("save-as-backup");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        var victimDocument = new TestDocument("victim", 1, "victim-trailer");
        var victim = harness.WriteSave("victim.sav", victimDocument);
        var victimBytes = File.ReadAllBytes(victim);

        using var open = await harness.OpenAsync(target, Token);

        harness.Interaction.SavePicker = _ => new SaveFilePickResult(victim, PickerConfirmedOverwrite: false);

        var written = document with { Level = 7 };
        var outcome = await harness.Create()
            .SaveAsAsync(written, harness.Codec, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(outcome);

        // The new bytes landed.
        Assert.Equal(TestCodec.Encode(written), File.ReadAllBytes(victim));

        // And what was there is recoverable, byte for byte, at a path the outcome names.
        Assert.NotNull(outcome.BackupPath);
        Assert.True(File.Exists(outcome.BackupPath), "Save As reported a backup path that does not exist.");
        Assert.Equal(victimBytes, File.ReadAllBytes(outcome.BackupPath!));

        var backup = Assert.Single(WorkflowHarness.Backups(harness.Workspace.Root));
        Assert.Equal(backup, outcome.BackupPath);

        // The file that was open is not the file that was written, so it is untouched.
        Assert.Equal(TestCodec.Encode(document), File.ReadAllBytes(target));
    }

    [Fact]
    public async Task SaveAs_OntoTheOpenDocumentLeavesARecoverableCopy()
    {
        using var harness = new WorkflowHarness("save-as-self-backup");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var originalBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);

        harness.Interaction.SavePicker = _ => new SaveFilePickResult(open.Path, PickerConfirmedOverwrite: false);

        var written = document with { Level = 12 };
        var outcome = await harness.Create()
            .SaveAsAsync(written, harness.Codec, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(outcome);
        Assert.Equal(TestCodec.Encode(written), File.ReadAllBytes(target));

        Assert.NotNull(outcome.BackupPath);
        Assert.Equal(originalBytes, File.ReadAllBytes(outcome.BackupPath!));
    }

    /// <summary>
    /// A <c>Save As</c> to a path that does not exist stays exactly as cheap as it was: no
    /// backup, no prompt.
    /// </summary>
    [Fact]
    public async Task SaveAs_ToANewPathTakesNoBackupAndAsksNothing()
    {
        using var harness = new WorkflowHarness("save-as-new");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        var fresh = harness.Workspace.Path("fresh.sav");
        harness.Interaction.SavePicker = _ => new SaveFilePickResult(fresh, PickerConfirmedOverwrite: false);

        var outcome = await harness.Create()
            .SaveAsAsync(document with { Level = 3 }, harness.Codec, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(outcome);
        Assert.True(File.Exists(fresh));
        Assert.Null(outcome.BackupPath);
        Assert.Empty(harness.Interaction.Confirmations);
        Assert.Empty(WorkflowHarness.Backups(harness.Workspace.Root));
    }

    /// <summary>
    /// The backup on the <c>Save As</c> path is all-or-nothing on the same terms as the
    /// overwrite path: if it cannot be taken, the destination is not touched.
    /// </summary>
    [Fact]
    public async Task SaveAs_AbandonsTheWriteWhenTheBackupCannotBeTaken()
    {
        using var harness = new WorkflowHarness("save-as-backup-refused");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        var victim = harness.WriteSave("victim.sav", new TestDocument("victim", 1, "victim-trailer"));
        var victimBytes = File.ReadAllBytes(victim);

        var names = new FixedFileNames(
            ".saveeditor-tmp-00112233445566778899aabbccddeeff.part",
            "victim.sav.saveeditor-backup.20260101T000000Z.deadbeef.bak");
        harness.FileNames = names;

        // Something is already sitting at the backup name. Exclusive creation refuses it, and
        // the write is abandoned rather than proceeding without a backup.
        File.WriteAllBytes(harness.Workspace.Path(names.BackupName), [9, 9, 9]);

        using var open = await harness.OpenAsync(target, Token);

        harness.Interaction.SavePicker = _ => new SaveFilePickResult(victim, PickerConfirmedOverwrite: false);

        var outcome = await harness.Create()
            .SaveAsAsync(document with { Level = 7 }, harness.Codec, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, outcome.Status);
        Assert.Equal(SaveFailureReason.BackupFailed, outcome.Reason);
        Assert.Equal(victimBytes, File.ReadAllBytes(victim));
        Assert.DoesNotContain("replace", harness.Durability.Calls);
        Assert.Empty(WorkflowHarness.TemporaryResidue(harness.Workspace.Root));
    }

    /// <summary>
    /// Capturing a baseline for a destination the workflow never decoded has a second effect:
    /// the pre-replace external-change check now runs on this path, which it previously
    /// skipped for every target other than the open document.
    /// </summary>
    [Fact]
    public async Task SaveAs_OntoAnExistingFileChecksForExternalChangeBeforeReplacing()
    {
        using var harness = new WorkflowHarness("save-as-external-change");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var victim = harness.WriteSave("victim.sav", new TestDocument("victim", 1, "victim-trailer"));
        var victimBytes = File.ReadAllBytes(victim);

        using var open = await harness.OpenAsync(target, Token);

        harness.Interaction.SavePicker = _ => new SaveFilePickResult(victim, PickerConfirmedOverwrite: false);

        // The destination's bytes change after its baseline was captured. The guard is what
        // notices; before this slice there was no baseline for it to compare against.
        var verifyCallsAtStart = harness.Guard.VerifyCalls;
        harness.Guard.VerifyOverride = call => call > verifyCallsAtStart
            ? new ExternalChangeCheck(ExternalChangeVerdict.Changed, true, false, "the destination changed")
            : null;

        var outcome = await harness.Create()
            .SaveAsAsync(document with { Level = 7 }, harness.Codec, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, outcome.Status);
        Assert.Equal(SaveFailureReason.ExternalChange, outcome.Reason);
        Assert.Equal(victimBytes, File.ReadAllBytes(victim));
        Assert.DoesNotContain("replace", harness.Durability.Calls);
    }

    [Fact]
    public async Task Workflow_RefusesReadOnlyTargetWithoutClearingAttribute()
    {
        using var harness = new WorkflowHarness("read-only");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        const UnixFileMode ReadOnlyMode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(target, FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(target, ReadOnlyMode);
        }

        try
        {
            using var open = await harness.OpenAsync(target, Token);

            var outcome = await harness.Create()
                .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

            Assert.Equal(SaveStatus.Failed, outcome.Status);
            Assert.Equal(SaveFailureReason.WriteProtected, outcome.Reason);
            Assert.Equal(targetBytes, File.ReadAllBytes(target));

            // Refusal, never modification: the marking is still there afterwards.
            if (OperatingSystem.IsWindows())
            {
                Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
            }
            else
            {
                Assert.Equal(ReadOnlyMode, File.GetUnixFileMode(target));
            }

            // And nothing was staged on the way to the refusal.
            Assert.Empty(WorkflowHarness.Backups(harness.Workspace.Root));
            Assert.Empty(WorkflowHarness.TemporaryResidue(harness.Workspace.Root));
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(target, FileAttributes.Normal);
            }
            else
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [Fact]
    public async Task Workflow_FsyncsFileAndContainingDirectoryInOrder()
    {
        using var harness = new WorkflowHarness("durability-order");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(outcome);

        // The verified backup is flushed too, so the first entry belongs to it. What
        // matters is the tail: the temporary file is fsync'd, then replaced, then the
        // containing directory is fsync'd. Without the last step the rename itself can be
        // lost on power failure even though the new file is durable.
        Assert.Equal(["flush-file", "flush-file", "replace", "flush-directory"], harness.Durability.Calls);

        var flush = harness.Durability.LastDirectoryFlush;
        Assert.NotNull(flush);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(DirectoryFlushStatus.NotApplicable, flush!.Value.Status);
            Assert.Contains("journal", flush.Value.Detail, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(DirectoryFlushStatus.Flushed, flush!.Value.Status);
        }
    }

    [Fact]
    public async Task Workflow_AbortsRatherThanFallingBackToDeleteThenMove()
    {
        using var harness = new WorkflowHarness("no-fallback");

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        harness.Durability.ReplaceOverride = (_, _, _) => new ReplaceResult(
            ReplaceStatus.NotAtomic,
            "The destination is on a filesystem that cannot rename atomically.");

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, outcome.Status);
        Assert.Equal(SaveFailureReason.AtomicReplaceUnsupported, outcome.Reason);

        // The message names the limitation instead of degrading to delete-then-move.
        Assert.Contains("atomically", outcome.Message, StringComparison.OrdinalIgnoreCase);

        // The destination still exists and still holds its original bytes: no delete
        // happened, and no partially-written replacement was left in its place.
        Assert.True(File.Exists(target));
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.Empty(WorkflowHarness.TemporaryResidue(harness.Workspace.Root));

        // Exactly one replace attempt. A fallback would show as a second one.
        Assert.Single(harness.Durability.Calls, call => call == "replace");
    }

    private static async Task<ResolvedFile> Resolve(SafePathResolver resolver, string path)
    {
        var resolution = await resolver.ResolveAsync(path, new PathResolutionOptions(), Token);
        return Assert.IsType<PathResolution.Resolved>(resolution).File;
    }

    /// <summary>
    /// Plants a link at <paramref name="linkPath"/>, preferring a symbolic link and falling
    /// back to a hard link where symbolic links need a privilege this environment lacks.
    /// </summary>
    /// <returns>Null on success, or why neither kind of link could be created.</returns>
    private static string? PlantLink(string linkPath, string victim)
    {
        var symbolic = PlatformFixtures.TryCreateFileSymbolicLink(linkPath, victim);
        if (symbolic is null)
        {
            return null;
        }

        var hard = PlatformFixtures.TryCreateHardLink(linkPath, victim);
        return hard is null ? null : $"symbolic link: {symbolic}; hard link: {hard}";
    }
}

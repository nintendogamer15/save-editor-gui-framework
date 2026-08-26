using System.Text;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// Restoring a backup over the open document.
/// </summary>
/// <remarks>
/// The framework created backups, verified them, reported their paths, and then left recovery
/// to the adopter — so every adopter was going to write this, and the framework already held
/// the resolver, the change guard, the permission policy and the atomic replace needed to
/// write it correctly (finding F-10).
/// </remarks>
public sealed class RestoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TestDocument SampleDocument => new("hero", 3, "trailer-bytes");

    [Fact]
    public async Task Restore_PutsTheBackedUpBytesBack()
    {
        using var harness = new WorkflowHarness("restore");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var originalBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);

        var edited = document with { Level = 99 };
        var overwrite = await harness.Create()
            .OverwriteWithBackupAsync(edited, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(overwrite);
        Assert.Equal(TestCodec.Encode(edited), File.ReadAllBytes(target));
        Assert.NotNull(overwrite.BackupPath);

        var restore = await harness.Create()
            .RestoreFromBackupAsync(overwrite.BackupPath!, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(restore.Outcome);

        // The bytes on disk are the pre-overwrite bytes, exactly.
        Assert.Equal(originalBytes, File.ReadAllBytes(target));

        // And the caller is handed the document those bytes decode to, because its own
        // in-memory document no longer matches the file.
        Assert.Equal(document, restore.Document);
    }

    /// <summary>A restore is itself recoverable: the state it replaced is backed up first.</summary>
    [Fact]
    public async Task Restore_BacksUpTheStateItReplaces()
    {
        using var harness = new WorkflowHarness("restore-backs-up");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        var edited = document with { Level = 99 };
        var overwrite = await harness.Create().OverwriteWithBackupAsync(edited, open, cancellationToken: Token);
        WorkflowHarness.AssertSucceeded(overwrite);

        var editedBytes = File.ReadAllBytes(target);

        var restore = await harness.Create()
            .RestoreFromBackupAsync(overwrite.BackupPath!, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(restore.Outcome);

        // The restore reported a backup of its own, and it holds the edit that was undone.
        Assert.NotNull(restore.Outcome.BackupPath);
        Assert.NotEqual(overwrite.BackupPath, restore.Outcome.BackupPath);
        Assert.Equal(editedBytes, File.ReadAllBytes(restore.Outcome.BackupPath!));
    }

    /// <summary>
    /// A restore that would land bytes the codec cannot read is refused before anything is
    /// written.
    /// </summary>
    [Fact]
    public async Task Restore_RefusesABackupTheCodecCannotRead()
    {
        using var harness = new WorkflowHarness("restore-undecodable");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);

        var rubbish = harness.Workspace.Path("not-a-save.bin");
        File.WriteAllBytes(rubbish, Encoding.UTF8.GetBytes("this is not a save file at all"));

        var restore = await harness.Create()
            .RestoreFromBackupAsync(rubbish, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, restore.Outcome.Status);
        Assert.Equal(SaveFailureReason.CodecFailed, restore.Outcome.Reason);
        Assert.Null(restore.Document);

        // Nothing written, nothing backed up, target byte-identical.
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.Empty(WorkflowHarness.Backups(harness.Workspace.Root));
        Assert.DoesNotContain("replace", harness.Durability.Calls);
    }

    [Fact]
    public async Task Restore_IsDestructiveAndAsksFirst()
    {
        using var harness = new WorkflowHarness("restore-confirms");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        var overwrite = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 99 }, open, cancellationToken: Token);
        WorkflowHarness.AssertSucceeded(overwrite);

        var editedBytes = File.ReadAllBytes(target);
        harness.Interaction.Confirmations.Clear();
        harness.Interaction.Confirm = _ => false;

        var restore = await harness.Create()
            .RestoreFromBackupAsync(overwrite.BackupPath!, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Declined, restore.Outcome.Status);
        Assert.Equal(editedBytes, File.ReadAllBytes(target));

        var confirmation = Assert.Single(harness.Interaction.Confirmations);
        Assert.True(confirmation.IsDestructive);
        Assert.Equal("Restore the backup", confirmation.AcceptLabel);
        Assert.Contains("will be gone", confirmation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_RefusesToRestoreTheTargetOverItself()
    {
        using var harness = new WorkflowHarness("restore-self");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);

        var restore = await harness.Create()
            .RestoreFromBackupAsync(target, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, restore.Outcome.Status);
        Assert.Equal(SaveFailureReason.PathRefused, restore.Outcome.Reason);
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Restore_AbortsWhenTheTargetChangedSinceItWasRead()
    {
        using var harness = new WorkflowHarness("restore-external-change");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        var overwrite = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 99 }, open, cancellationToken: Token);
        WorkflowHarness.AssertSucceeded(overwrite);

        var editedBytes = File.ReadAllBytes(target);

        var callsAtStart = harness.Guard.VerifyCalls;
        harness.Guard.VerifyOverride = call => call > callsAtStart
            ? new ExternalChangeCheck(ExternalChangeVerdict.Changed, true, false, "the target changed")
            : null;

        var restore = await harness.Create()
            .RestoreFromBackupAsync(overwrite.BackupPath!, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, restore.Outcome.Status);
        Assert.Equal(SaveFailureReason.ExternalChange, restore.Outcome.Reason);
        Assert.Equal(editedBytes, File.ReadAllBytes(target));
    }

    /// <summary>
    /// After a restore the retained handle is rebound, so the document stays usable and a
    /// later overwrite sees the real file rather than the unlinked inode.
    /// </summary>
    [Fact]
    public async Task Restore_LeavesTheDocumentUsableForAFurtherWrite()
    {
        using var harness = new WorkflowHarness("restore-rebinds");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        var overwrite = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 99 }, open, cancellationToken: Token);
        WorkflowHarness.AssertSucceeded(overwrite);

        var restore = await harness.Create()
            .RestoreFromBackupAsync(overwrite.BackupPath!, open, cancellationToken: Token);
        WorkflowHarness.AssertSucceeded(restore.Outcome);
        Assert.False(open.IsStale);

        var again = document with { Level = 5 };
        var second = await harness.Create().OverwriteWithBackupAsync(again, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(second);
        Assert.Equal(TestCodec.Encode(again), File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Restore_RefusesAStaleDocument()
    {
        using var harness = new WorkflowHarness("restore-stale");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var backup = harness.WriteSave("copy.sav", document with { Level = 1 });

        using var open = await harness.OpenAsync(target, Token);
        open.IsStale = true;

        var restore = await harness.Create()
            .RestoreFromBackupAsync(backup, open, cancellationToken: Token);

        Assert.Equal(SaveFailureReason.IdentityChanged, restore.Outcome.Reason);
    }
}

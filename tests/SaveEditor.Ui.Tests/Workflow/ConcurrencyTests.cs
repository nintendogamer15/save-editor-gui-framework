using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// Two operations at once on one open document.
/// </summary>
/// <remarks>
/// <c>ExternalChangeGuard.ReadAllAsync</c> and the backup copy both seek the shared retained
/// <c>FileStream</c> to zero, and nothing prevented two operations from interleaving those
/// seeks — producing a backup or a baseline covering part of one read and part of another,
/// with nothing detecting it because every individual step succeeded. It was
/// adopter-owned by convention; conventions are not enforcement.
/// </remarks>
public sealed class ConcurrencyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TestDocument SampleDocument => new("hero", 3, "trailer-bytes");

    [Fact]
    public async Task Workflow_RefusesASecondOverwriteWhileTheFirstIsRunning()
    {
        using var harness = new WorkflowHarness("concurrent-overwrite");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = document with { Level = 11 };
        var second = document with { Level = 22 };

        // Park the first operation inside the codec, so the second one arrives while the
        // handle is genuinely in use.
        harness.Codec.SerializeOverride = async (doc, destination, cancellationToken) =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(30));
            await destination.WriteAsync(TestCodec.Encode(doc), cancellationToken);
        };

        var firstOperation = harness.Create()
            .OverwriteWithBackupAsync(first, open, cancellationToken: Token)
            .AsTask();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        var secondOutcome = await harness.Create()
            .OverwriteWithBackupAsync(second, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, secondOutcome.Status);
        Assert.Equal(SaveFailureReason.Busy, secondOutcome.Reason);

        release.Set();
        var firstOutcome = await firstOperation.WaitAsync(TimeSpan.FromSeconds(30), Token);

        WorkflowHarness.AssertSucceeded(firstOutcome);

        // Exactly one of the two payloads landed, never a mixture of both.
        var landed = File.ReadAllBytes(target);
        Assert.Equal(TestCodec.Encode(first), landed);
        Assert.NotEqual(TestCodec.Encode(second), landed);
    }

    /// <summary>The latch is released, so a refused second attempt can simply be retried.</summary>
    [Fact]
    public async Task Workflow_AcceptsTheSecondAttemptOnceTheFirstHasFinished()
    {
        using var harness = new WorkflowHarness("concurrent-then-serial");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        var first = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 11 }, open, cancellationToken: Token);
        WorkflowHarness.AssertSucceeded(first);

        var second = document with { Level = 22 };
        var outcome = await harness.Create().OverwriteWithBackupAsync(second, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(outcome);
        Assert.Equal(TestCodec.Encode(second), File.ReadAllBytes(target));
    }

    /// <summary>A failed operation releases the latch too.</summary>
    [Fact]
    public async Task Workflow_ReleasesTheLatchAfterAFailure()
    {
        using var harness = new WorkflowHarness("concurrent-after-failure");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        harness.Durability.ReplaceOverride = (_, _, _) =>
            new ReplaceResult(ReplaceStatus.Failed, "the replace failed");

        var failed = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 11 }, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, failed.Status);
        Assert.NotEqual(SaveFailureReason.Busy, failed.Reason);

        harness.Durability.ReplaceOverride = null;

        var recovered = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 22 }, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(recovered);
    }

    [Fact]
    public async Task Workflow_RefusesARestoreWhileAWriteIsRunning()
    {
        using var harness = new WorkflowHarness("concurrent-restore");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        var seed = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 11 }, open, cancellationToken: Token);
        WorkflowHarness.AssertSucceeded(seed);

        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.Codec.SerializeOverride = async (doc, destination, cancellationToken) =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(30));
            await destination.WriteAsync(TestCodec.Encode(doc), cancellationToken);
        };

        var write = harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 33 }, open, cancellationToken: Token)
            .AsTask();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        var restore = await harness.Create()
            .RestoreFromBackupAsync(seed.BackupPath!, open, cancellationToken: Token);

        Assert.Equal(SaveFailureReason.Busy, restore.Outcome.Reason);
        Assert.Null(restore.Document);

        release.Set();
        WorkflowHarness.AssertSucceeded(await write.WaitAsync(TimeSpan.FromSeconds(30), Token));
    }

    /// <summary>
    /// A Save As with no open document takes no latch, because there is no shared handle to
    /// protect.
    /// </summary>
    [Fact]
    public async Task SaveAs_WithNoOpenDocumentIsNeverRefusedAsBusy()
    {
        using var harness = new WorkflowHarness("concurrent-no-document");
        harness.Codec.PreservesUnknownData = false;

        var fresh = harness.Workspace.Path("fresh.sav");
        harness.Interaction.SavePicker = _ => new SaveFilePickResult(fresh, PickerConfirmedOverwrite: false);

        var outcome = await harness.Create()
            .SaveAsAsync(SampleDocument, harness.Codec, current: null, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(outcome);
        Assert.True(File.Exists(fresh));
    }
}

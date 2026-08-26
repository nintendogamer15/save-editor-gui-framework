using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Editing;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// An application imposing a save policy stricter than the framework's, without
/// reimplementing <see cref="Ui.Shell.IDocumentSession"/> and without substituting
/// <see cref="IUserInteraction"/>.
/// </summary>
/// <remarks>
/// The picker used to be invoked inside <c>SaveAsAsync</c> with no path-taking overload, and
/// <c>DocumentSession</c> was sealed and never exposed its <c>OpenSaveFile</c>. So an editor
/// that refuses to let Save As replace an existing file, and wants a same-path pick routed to
/// the backed-up overwrite, had two options: reproduce the session/history/state plumbing the
/// framework exists to provide, or intercept picks through a custom dialog service — policy
/// enforcement smuggled through <c>IUserInteraction.PickSaveFileAsync</c> (finding F-15).
/// </remarks>
public sealed class WritePolicyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TestDocument SampleDocument => new("hero", 3, "trailer-bytes");

    /// <summary>The adopter's actual rule: Save As never replaces an existing file.</summary>
    private sealed class NeverReplaceOnSaveAs : IWritePolicy
    {
        public List<PlannedWrite> Seen { get; } = [];

        public ValueTask<WriteDecision> EvaluateAsync(PlannedWrite plan, CancellationToken cancellationToken = default)
        {
            Seen.Add(plan);

            return ValueTask.FromResult(
                plan is { Kind: PlannedWriteKind.SaveAs, DestinationExists: true }
                    ? WriteDecision.Refuse("This editor never replaces an existing save from Save As. Use Overwrite instead.")
                    : WriteDecision.Proceed);
        }
    }

    [Fact]
    public async Task Policy_CanRefuseASaveAsOntoAnExistingFile()
    {
        using var harness = new WorkflowHarness("policy-refuses");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var victim = harness.WriteSave("victim.sav", new TestDocument("victim", 1, "victim-trailer"));
        var victimBytes = File.ReadAllBytes(victim);

        using var open = await harness.OpenAsync(target, Token);

        var policy = new NeverReplaceOnSaveAs();
        harness.Interaction.SavePicker = _ => new SaveFilePickResult(victim, PickerConfirmedOverwrite: false);

        var options = harness.Options with { WritePolicy = policy };
        var outcome = await new SafeFileWorkflow<TestDocument>(options)
            .SaveAsAsync(document with { Level = 9 }, harness.Codec, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Declined, outcome.Status);
        Assert.Contains("never replaces an existing save", outcome.Message, StringComparison.Ordinal);

        // Refused before anything destructive: no confirmation, no backup, no replace.
        Assert.Equal(victimBytes, File.ReadAllBytes(victim));
        Assert.Empty(harness.Interaction.Confirmations);
        Assert.Empty(WorkflowHarness.Backups(harness.Workspace.Root));
        Assert.DoesNotContain("replace", harness.Durability.Calls);

        var plan = Assert.Single(policy.Seen);
        Assert.Equal(PlannedWriteKind.SaveAs, plan.Kind);
        Assert.True(plan.DestinationExists);
        Assert.False(plan.IsCurrentDocument);
        Assert.True(plan.BackupWillBeWritten);
    }

    [Fact]
    public async Task Policy_LetsASaveAsToANewPathThrough()
    {
        using var harness = new WorkflowHarness("policy-allows-new");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        var policy = new NeverReplaceOnSaveAs();
        var fresh = harness.Workspace.Path("fresh.sav");
        harness.Interaction.SavePicker = _ => new SaveFilePickResult(fresh, PickerConfirmedOverwrite: false);

        var options = harness.Options with { WritePolicy = policy };
        var outcome = await new SafeFileWorkflow<TestDocument>(options)
            .SaveAsAsync(document with { Level = 9 }, harness.Codec, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(outcome);
        Assert.True(File.Exists(fresh));

        // Consulted even for a brand-new path, so a policy can refuse anything.
        var plan = Assert.Single(policy.Seen);
        Assert.False(plan.DestinationExists);
        Assert.False(plan.BackupWillBeWritten);
    }

    [Fact]
    public async Task Policy_IsConsultedOnOverwriteAndRestoreToo()
    {
        using var harness = new WorkflowHarness("policy-all-paths");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        var policy = new NeverReplaceOnSaveAs();
        var options = harness.Options with { WritePolicy = policy };
        var workflow = new SafeFileWorkflow<TestDocument>(options);

        var overwrite = await workflow.OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);
        WorkflowHarness.AssertSucceeded(overwrite);

        var restore = await workflow.RestoreFromBackupAsync(overwrite.BackupPath!, open, cancellationToken: Token);
        WorkflowHarness.AssertSucceeded(restore.Outcome);

        Assert.Equal(
            [PlannedWriteKind.Overwrite, PlannedWriteKind.Restore],
            policy.Seen.Select(p => p.Kind));
    }

    /// <summary>A policy that throws refuses; it never becomes permission by accident.</summary>
    [Fact]
    public async Task Policy_ThatThrowsAbandonsTheWrite()
    {
        using var harness = new WorkflowHarness("policy-throws");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);

        var options = harness.Options with { WritePolicy = new ThrowingPolicy() };
        var outcome = await new SafeFileWorkflow<TestDocument>(options)
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        Assert.Equal(SaveStatus.Failed, outcome.Status);
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.DoesNotContain("replace", harness.Durability.Calls);
    }

    private sealed class ThrowingPolicy : IWritePolicy
    {
        public ValueTask<WriteDecision> EvaluateAsync(PlannedWrite plan, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the policy threw");
    }

    /// <summary>
    /// The second half of the brief's acceptance: a session subclass routes a same-path pick
    /// to the backed-up overwrite.
    /// </summary>
    private sealed class RoutingSession : DocumentSession<TestDocument>
    {
        private readonly string _pick;

        public RoutingSession(
            SafeFileWorkflow<TestDocument> workflow,
            IEditHistory history,
            ISaveCodec<TestDocument> defaultCodec,
            string pick)
            : base(workflow, history, defaultCodec) => _pick = pick;

        public bool RoutedToOverwrite { get; private set; }

        public override async ValueTask SaveAsAsync(CancellationToken cancellationToken = default)
        {
            if (Document is not { } document)
            {
                await base.SaveAsAsync(cancellationToken).ConfigureAwait(true);
                return;
            }

            // A pick that resolves to the open document goes to the operation that is named
            // after what it does, rather than through a Save As that happens to be equivalent.
            if (OpenFile is { } open && string.Equals(
                    Path.GetFullPath(_pick),
                    Path.GetFullPath(open.Path),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                RoutedToOverwrite = true;

                var routed = await Workflow
                    .OverwriteWithBackupAsync(document, open, CreateProgress(), cancellationToken)
                    .ConfigureAwait(true);

                RecordOutcome(routed, routed.IsSuccess);
                return;
            }

            var outcome = await Workflow
                .SaveAsAsync(document, OpenFile?.Codec ?? DefaultCodec, _pick, OpenFile, CreateProgress(), cancellationToken)
                .ConfigureAwait(true);

            RecordOutcome(outcome, outcome.IsSuccess);
        }
    }

    [Fact]
    public async Task ASessionSubclass_RoutesASamePathPickToTheBackedUpOverwrite()
    {
        using var harness = new WorkflowHarness("session-routes");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var originalBytes = File.ReadAllBytes(target);

        var workflow = harness.Create();
        var history = new EditHistory();

        using var session = new RoutingSession(workflow, history, harness.Codec, target);
        await session.OpenAsync(target, Token);

        Assert.True(session.HasDocument);

        await session.SaveAsAsync(Token);

        Assert.True(session.RoutedToOverwrite, "The subclass did not take the routing path, so this proved nothing.");
        Assert.NotNull(session.LastOutcome);
        Assert.True(session.LastOutcome!.IsSuccess, session.LastOutcome.Message);

        // Routed to the backed-up overwrite, so a recoverable copy exists -- which a plain
        // Save As to a new path would not have produced.
        Assert.NotNull(session.LastOutcome.BackupPath);
        Assert.Equal(originalBytes, File.ReadAllBytes(session.LastOutcome.BackupPath!));

        // And the session's own bookkeeping ran through RecordOutcome.
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task ASessionSubclass_UsesThePathTakingOverloadForAnyOtherDestination()
    {
        using var harness = new WorkflowHarness("session-path-overload");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var elsewhere = harness.Workspace.Path("elsewhere.sav");

        var workflow = harness.Create();
        using var session = new RoutingSession(workflow, new EditHistory(), harness.Codec, elsewhere);
        await session.OpenAsync(target, Token);

        await session.SaveAsAsync(Token);

        Assert.False(session.RoutedToOverwrite);
        Assert.True(session.LastOutcome!.IsSuccess, session.LastOutcome.Message);
        Assert.True(File.Exists(elsewhere));

        // No picker was ever shown: the path came from the application.
        Assert.Equal(TestCodec.Encode(document), File.ReadAllBytes(elsewhere));
    }

    [Fact]
    public async Task ThePathTakingOverload_StillTakesABackupAndConfirms()
    {
        using var harness = new WorkflowHarness("path-overload-guards");
        harness.Codec.PreservesUnknownData = false;

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var victim = harness.WriteSave("victim.sav", new TestDocument("victim", 1, "victim-trailer"));
        var victimBytes = File.ReadAllBytes(victim);

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .SaveAsAsync(document with { Level = 9 }, harness.Codec, victim, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(outcome);

        // Every guard the picker-driven overload applies applies here too.
        var confirmation = Assert.Single(harness.Interaction.Confirmations);
        Assert.True(confirmation.IsDestructive);
        Assert.NotNull(outcome.BackupPath);
        Assert.Equal(victimBytes, File.ReadAllBytes(outcome.BackupPath!));
    }
}

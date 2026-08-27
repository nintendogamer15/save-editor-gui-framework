using SaveEditor.Ui.Editing;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// P2 deferred a set of acceptance checks its stubbed session could not prove, to be
/// closed once the real workflow existed. These are D1 through D4; D5 through D7
/// need the shell and live in the headless suite.
/// </summary>
/// <remarks>
/// The point of the split was that the shell could be built and tested before the
/// workflow existed. These tests are what makes that honest rather than a way of
/// never proving the hard half.
/// </remarks>
public class DeferredShellChecksTests
{
    private static (WorkflowHarness Harness, DocumentSession<TestDocument> Session, EditHistory History) Build(
        [System.Runtime.CompilerServices.CallerMemberName] string label = "")
    {
        var harness = new WorkflowHarness(label);
        var history = new EditHistory();
        var session = new DocumentSession<TestDocument>(harness.Create(), history, harness.Codec);
        return (harness, session, history);
    }

    [Fact]
    public async Task D1_Open_Decodes_A_Real_File_Through_The_Registry()
    {
        var (harness, session, _) = Build();
        using var _h = harness;
        using var _s = session;

        var path = harness.WriteSave("slot1.dat", new TestDocument("Aerith", 250, "tail"));

        await session.OpenAsync(path, TestContext.Current.CancellationToken);

        Assert.True(session.HasDocument);
        Assert.Equal("Aerith", session.Document!.Name);
        Assert.Equal(250, session.Document.Level);
        Assert.Equal(path, session.CurrentPath);
        Assert.True(session.LastOutcome!.IsSuccess);
    }

    [Fact]
    public async Task D2_SaveAs_Writes_Through_The_Workflow_And_Reports_Definitively()
    {
        var (harness, session, history) = Build();
        using var _h = harness;
        using var _s = session;

        var source = harness.WriteSave("slot1.dat", new TestDocument("Aerith", 100, "tail"));
        await session.OpenAsync(source, TestContext.Current.CancellationToken);

        var destination = harness.Workspace.Path("copy.dat");
        harness.Interaction.SavePicker = _ => new Ui.Interaction.SaveFilePickResult(destination, false);

        session.ReplaceDocument(session.Document! with { Level = 999 });
        await session.SaveAsAsync(TestContext.Current.CancellationToken);

        // Definitive means the outcome says what happened, not merely that nothing threw.
        Assert.True(session.LastOutcome!.IsSuccess);
        Assert.Equal(destination, session.LastOutcome.Path);
        Assert.True(File.Exists(destination));

        // Save As retargets: the session now edits what it just wrote, so a later
        // overwrite acts on the new file rather than the one it was opened from.
        Assert.Equal(destination, session.CurrentPath);
        Assert.False(history.IsDirty);
    }

    [Fact]
    public async Task D3_Overwrite_Produces_A_Verified_Backup()
    {
        var (harness, session, _) = Build();
        using var _h = harness;
        using var _s = session;

        var path = harness.WriteSave("slot1.dat", new TestDocument("Aerith", 100, "tail"));
        await session.OpenAsync(path, TestContext.Current.CancellationToken);

        session.ReplaceDocument(session.Document! with { Level = 500 });
        await session.OverwriteWithBackupAsync(TestContext.Current.CancellationToken);

        Assert.True(session.LastOutcome!.IsSuccess);
        Assert.NotNull(session.LastOutcome.BackupPath);
        Assert.True(File.Exists(session.LastOutcome.BackupPath));

        // The backup must be the bytes that were there before, not the ones just
        // written - a backup of the new content protects nobody.
        var backup = await File.ReadAllTextAsync(
            session.LastOutcome.BackupPath!, TestContext.Current.CancellationToken);
        Assert.Contains("100", backup, StringComparison.Ordinal);
        Assert.DoesNotContain("500", backup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D3_Overwrite_Without_An_Open_File_Is_Declined_Not_Attempted()
    {
        var (harness, session, _) = Build();
        using var _h = harness;
        using var _s = session;

        await session.OverwriteWithBackupAsync(TestContext.Current.CancellationToken);

        Assert.False(session.LastOutcome!.IsSuccess);
        Assert.Contains("Save As", session.LastOutcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task D4_Reload_Re_Reads_From_Disk()
    {
        var (harness, session, history) = Build();
        using var _h = harness;
        using var _s = session;

        var path = harness.WriteSave("slot1.dat", new TestDocument("Aerith", 100, "tail"));
        await session.OpenAsync(path, TestContext.Current.CancellationToken);

        session.ReplaceDocument(session.Document! with { Name = "Tifa", Level = 1 });
        Assert.Equal("Tifa", session.Document!.Name);

        await session.ReloadAsync(TestContext.Current.CancellationToken);

        // Reload discards in-memory state and takes what is actually on disk.
        Assert.Equal("Aerith", session.Document!.Name);
        Assert.Equal(100, session.Document.Level);
        Assert.False(history.IsDirty);
    }

    [Fact]
    public async Task An_Open_Document_Cannot_Be_Rewritten_Underneath_The_Editor()
    {
        var (harness, session, _) = Build();
        using var _h = harness;
        using var _s = session;

        var path = harness.WriteSave("slot1.dat", new TestDocument("Aerith", 100, "tail"));
        await session.OpenAsync(path, TestContext.Current.CancellationToken);

        // The workflow holds the original with write sharing denied from check through
        // replace. On Windows that is stronger than detecting a change afterwards: the
        // external write is refused outright. This test exists because writing D4 the
        // obvious way failed here, and the failure was the protection working.
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() =>
                harness.WriteSave("slot1.dat", new TestDocument("Tifa", 777, "tail")));
        }
        else
        {
            // Linux locks are advisory, so the write lands and the guard catches it at
            // save time instead. That asymmetry is stated in the plan, not papered over.
            harness.WriteSave("slot1.dat", new TestDocument("Tifa", 777, "tail"));
            Assert.Equal("Aerith", session.Document!.Name);
        }
    }

    [Fact]
    public async Task Pending_Edits_Reach_The_Session_Only_Through_The_Probe()
    {
        var (harness, session, _) = Build();
        using var _h = harness;
        using var _s = session;

        var path = harness.WriteSave("slot1.dat", new TestDocument("Aerith", 1, "tail"));
        await session.OpenAsync(path, TestContext.Current.CancellationToken);

        // Unset, the session cannot see drafts - which would make the exit guard blind
        // to typed-but-unapplied edits. The XML docs say so; this pins it.
        Assert.False(session.HasPendingEdits);

        var pending = true;
        session.PendingEditProbe = () => pending;
        Assert.True(session.HasPendingEdits);

        pending = false;
        Assert.False(session.HasPendingEdits);
    }

    [Fact]
    public async Task Closing_Releases_The_File_And_Clears_History()
    {
        var (harness, session, history) = Build();
        using var _h = harness;
        using var _s = session;

        var path = harness.WriteSave("slot1.dat", new TestDocument("Aerith", 1, "tail"));
        await session.OpenAsync(path, TestContext.Current.CancellationToken);

        await session.CloseAsync(TestContext.Current.CancellationToken);

        Assert.False(session.HasDocument);
        Assert.Null(session.CurrentPath);
        Assert.False(history.CanUndo);

        // The handle must actually be released, or the file stays locked on Windows
        // and the next open of the same path fails for a reason nobody will guess.
        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}

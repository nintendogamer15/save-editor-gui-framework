using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Shell;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>
/// Opening a save has to record it. Everything downstream of Recent — the menu, the
/// welcome list, the persistence across launches — was already built and tested
/// against entries a test had seeded by hand, so a shell that never recorded one
/// passed all of it while <b>File &gt; Recent</b> stayed empty forever.
/// </summary>
public class RecentFilesTests
{
    [AvaloniaFact]
    public async Task A_Successful_Open_Records_And_Persists_The_Path()
    {
        using var harness = ShellWorkflowHarness.Create("recent-record");

        var path = harness.WriteSave("slot1.sav", new ShellDoc("Aerith", 3));
        await harness.Vm.OpenPathAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal([path], harness.Vm.Recents.Select(r => r.Path));

        // Persisted, not merely held: the entries have to survive the next launch.
        Assert.Equal([path], harness.Store.Current.RecentFiles);
    }

    [AvaloniaFact]
    public async Task Recent_Is_Newest_First_And_Deduplicated()
    {
        using var harness = ShellWorkflowHarness.Create("recent-order");

        var a = harness.WriteSave("a.sav", new ShellDoc("Aerith", 1));
        var b = harness.WriteSave("b.sav", new ShellDoc("Tifa", 2));

        await harness.Vm.OpenPathAsync(a, TestContext.Current.CancellationToken);
        await harness.Vm.OpenPathAsync(b, TestContext.Current.CancellationToken);

        Assert.Equal([b, a], harness.Vm.Recents.Select(r => r.Path));

        // Reopening A promotes it rather than adding it a second time.
        await harness.Vm.OpenPathAsync(a, TestContext.Current.CancellationToken);

        Assert.Equal([a, b], harness.Vm.Recents.Select(r => r.Path));
        Assert.Equal([a, b], harness.Store.Current.RecentFiles);
    }

    [AvaloniaFact]
    public async Task Recent_Is_Capped_At_The_Settings_Limit()
    {
        using var harness = ShellWorkflowHarness.Create("recent-cap");

        for (var i = 0; i <= EditorSettings.MaxRecentFiles; i++)
        {
            var path = harness.WriteSave($"slot{i}.sav", new ShellDoc("Aerith", i));
            await harness.Vm.OpenPathAsync(path, TestContext.Current.CancellationToken);
        }

        Assert.Equal(EditorSettings.MaxRecentFiles, harness.Vm.Recents.Count);
        Assert.Equal(EditorSettings.MaxRecentFiles, harness.Store.Current.RecentFiles.Count);

        // The oldest is the one that fell off, not the newest.
        Assert.Equal(harness.Destination($"slot{EditorSettings.MaxRecentFiles}.sav"), harness.Vm.Recents[0].Path);
        Assert.DoesNotContain(harness.Destination("slot0.sav"), harness.Vm.Recents.Select(r => r.Path));
    }

    [AvaloniaFact]
    public async Task A_Failed_Open_Records_Nothing()
    {
        using var harness = ShellWorkflowHarness.Create("recent-failed");

        await harness.Vm.OpenPathAsync(
            harness.Destination("does-not-exist.sav"), TestContext.Current.CancellationToken);

        Assert.False(harness.Session.HasDocument);
        Assert.Empty(harness.Vm.Recents);
        Assert.Empty(harness.Store.Current.RecentFiles);
    }

    [AvaloniaFact]
    public async Task A_Failed_Open_Does_Not_Re_Record_The_Document_It_Left_Open()
    {
        using var harness = ShellWorkflowHarness.Create("recent-failed-after");

        var a = harness.WriteSave("a.sav", new ShellDoc("Aerith", 1));
        var b = harness.WriteSave("b.sav", new ShellDoc("Tifa", 2));

        await harness.Vm.OpenPathAsync(a, TestContext.Current.CancellationToken);
        await harness.Vm.OpenPathAsync(b, TestContext.Current.CancellationToken);

        var missing = harness.Destination("gone.sav");
        await harness.Vm.OpenPathAsync(missing, TestContext.Current.CancellationToken);

        // B is still open, so "something is open" is true — but this open failed, and
        // reordering Recent behind the user's back on a failure is the bug that reading
        // the session's state without checking it would produce.
        Assert.Equal([b, a], harness.Vm.Recents.Select(r => r.Path));
        Assert.DoesNotContain(missing, harness.Vm.Recents.Select(r => r.Path));
    }

    [AvaloniaFact]
    public async Task A_Cancelled_Open_Records_Nothing()
    {
        using var harness = ShellWorkflowHarness.Create("recent-cancelled");

        var open = harness.WriteSave("open.sav", new ShellDoc("Aerith", 1));
        await harness.Vm.OpenPathAsync(open, TestContext.Current.CancellationToken);

        // Uncommitted work plus a refused discard: the guard turns the open back before
        // the session is ever asked.
        harness.Session.PendingEditProbe = () => true;
        harness.Interaction.ConfirmResult = false;

        var other = harness.WriteSave("other.sav", new ShellDoc("Tifa", 2));
        await harness.Vm.OpenPathAsync(other, TestContext.Current.CancellationToken);

        Assert.Equal([open], harness.Vm.Recents.Select(r => r.Path));
        Assert.Equal([open], harness.Store.Current.RecentFiles);
    }

    [AvaloniaFact]
    public async Task A_Recorded_Entry_Reopens_Its_Save_From_The_File_Menu()
    {
        using var harness = ShellWorkflowHarness.Create("recent-menu");

        var a = harness.WriteSave("a.sav", new ShellDoc("Aerith", 1));
        var b = harness.WriteSave("b.sav", new ShellDoc("Tifa", 2));

        await harness.Vm.OpenPathAsync(a, TestContext.Current.CancellationToken);
        await harness.Vm.OpenPathAsync(b, TestContext.Current.CancellationToken);

        var shell = new EditorShell { DataContext = harness.Vm };
        var window = new Window { Width = 1000, Height = 700, Content = shell };
        window.Show();

        var menu = shell.GetVisualDescendants().OfType<Menu>().Single();
        var file = menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_File");
        var recent = Realize(window, file).Single(m => (string?)m.Header == "_Recent");
        var entries = Realize(window, recent);

        // Two rows, newest first, and the second one still opens the save it names.
        Assert.Equal(2, entries.Count);

        var oldest = entries[1];
        Assert.NotNull(oldest.Command);
        Assert.True(oldest.Command!.CanExecute(oldest.CommandParameter));

        await harness.Vm.OpenRecentCommand.ExecuteAsync(Assert.IsType<RecentEntry>(oldest.CommandParameter));

        Assert.Equal(a, harness.Session.CurrentPath);
        Assert.Equal("Aerith", harness.Session.Document!.Name);
    }

    /// <summary>Opens a submenu and returns its realized containers.</summary>
    /// <remarks>
    /// Containers do not exist until the submenu opens and a frame is produced, so
    /// reading them without the render pump returns nothing and the assertions above
    /// would be vacuous.
    /// </remarks>
    private static IReadOnlyList<MenuItem> Realize(Window window, MenuItem parent)
    {
        parent.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        return [.. Enumerable.Range(0, parent.ItemCount).Select(parent.ContainerFromIndex).OfType<MenuItem>()];
    }
}

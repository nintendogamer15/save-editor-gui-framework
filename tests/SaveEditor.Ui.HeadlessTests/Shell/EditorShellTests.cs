using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using SaveEditor.Ui.Shell;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>
/// P2 acceptance: the shell, its menus, and its guards work end to end against a
/// stubbed document session.
/// </summary>
public class EditorShellTests
{
    private static (EditorShellViewModel Vm, FakeDocumentSession Session,
        FakeUserInteraction Interaction, FakeEditorHost Host) Build(
        bool pendingEdits = false, bool dirty = false, bool confirm = true)
    {
        var session = new FakeDocumentSession
        {
            HasDocument = pendingEdits || dirty,
            HasPendingEdits = pendingEdits,
            IsDirty = dirty,
            CurrentPath = pendingEdits || dirty ? "/saves/slot1.dat" : null,
        };

        var interaction = new FakeUserInteraction { ConfirmResult = confirm };
        var host = new FakeEditorHost();
        var vm = new EditorShellViewModel(session, interaction, new FakeSettingsStore(), host);

        return (vm, session, interaction, host);
    }

    [AvaloniaFact]
    public async Task Exit_With_Pending_Edits_Raises_The_Guard_And_Does_Not_Shut_Down()
    {
        var (vm, _, interaction, host) = Build(pendingEdits: true, confirm: false);

        await vm.ExitCommand.ExecuteAsync(null);

        Assert.True(host.GuardWasConsulted);
        Assert.Single(interaction.Confirmations);
        Assert.False(host.DidShutDown);

        // The label must name the outcome; a generic accept on the last barrier
        // between a user and their unsaved edits is exactly what the plan forbids.
        var confirmation = interaction.Confirmations[0];
        Assert.True(confirmation.IsDestructive);
        Assert.Equal("Discard and exit", confirmation.AcceptLabel);
        Assert.DoesNotContain("OK", confirmation.AcceptLabel, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Exit_Shuts_Down_When_The_User_Accepts()
    {
        var (vm, _, _, host) = Build(pendingEdits: true, confirm: true);

        await vm.ExitCommand.ExecuteAsync(null);

        Assert.True(host.DidShutDown);
    }

    [AvaloniaFact]
    public async Task Exit_With_Nothing_Unsaved_Does_Not_Prompt()
    {
        var (vm, _, interaction, host) = Build();

        await vm.ExitCommand.ExecuteAsync(null);

        Assert.Empty(interaction.Confirmations);
        Assert.True(host.DidShutDown);
    }

    [AvaloniaTheory]
    [InlineData("Reload")]
    [InlineData("Close")]
    public async Task Destructive_Navigation_Is_Guarded_And_Abandoned_On_Refusal(string command)
    {
        var (vm, session, interaction, _) = Build(dirty: true, confirm: false);

        await (command == "Reload"
            ? vm.ReloadCommand.ExecuteAsync(null)
            : vm.CloseCommand.ExecuteAsync(null));

        Assert.Single(interaction.Confirmations);
        Assert.Empty(session.Calls);
        Assert.Contains("cancelled", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task A_Dropped_Path_Takes_The_Same_Route_As_A_Menu_Open()
    {
        // The drop adapter is a thin forwarder; the property that matters is that a
        // dropped file cannot skip the guard a menu open would have raised.
        var (vm, session, interaction, _) = Build(pendingEdits: true, confirm: false);

        await vm.OpenPathAsync("/saves/other.dat");

        Assert.Single(interaction.Confirmations);
        Assert.Empty(session.Calls);

        interaction.ConfirmResult = true;
        await vm.OpenPathAsync("/saves/other.dat");

        Assert.Contains(nameof(FakeDocumentSession.OpenAsync), session.Calls);
        Assert.Equal("/saves/other.dat", session.OpenedPath);
    }

    [AvaloniaFact]
    public async Task Menu_Commands_Route_To_The_Session()
    {
        var (vm, session, interaction, _) = Build();
        interaction.OpenPickerResult = "/saves/picked.dat";

        await vm.OpenSaveCommand.ExecuteAsync(null);
        await vm.SaveAsCommand.ExecuteAsync(null);
        await vm.OverwriteWithBackupCommand.ExecuteAsync(null);
        vm.UndoCommand.Execute(null);
        vm.RedoCommand.Execute(null);

        Assert.Equal(
            [
                nameof(FakeDocumentSession.OpenAsync),
                nameof(FakeDocumentSession.SaveAsAsync),
                nameof(FakeDocumentSession.OverwriteWithBackupAsync),
                nameof(FakeDocumentSession.Undo),
                nameof(FakeDocumentSession.Redo),
            ],
            session.Calls);
    }

    [AvaloniaFact]
    public async Task Welcome_State_Shows_Until_A_Document_Opens_And_Lists_Recents()
    {
        var (vm, _, interaction, _) = Build();
        interaction.OpenPickerResult = "/saves/picked.dat";

        var store = new FakeSettingsStore
        {
            Current = new Ui.Settings.EditorSettings { RecentFiles = ["/saves/a.dat", "/saves/b.dat"] },
        };

        var withRecents = new EditorShellViewModel(
            new FakeDocumentSession(), interaction, store);

        await withRecents.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(withRecents.IsWelcomeVisible);

        // The raw path is what gets opened; the label is what gets shown. They are
        // paired so they cannot drift apart.
        Assert.Equal(["/saves/a.dat", "/saves/b.dat"], withRecents.Recents.Select(r => r.Path));
        // Labels are isolate-wrapped, so they never equal the path they describe -
        // which is what stops a displayed string being fed back to the filesystem.
        Assert.All(withRecents.Recents, r => Assert.NotEqual(r.Path, r.Label.FullLabel));

        Assert.True(vm.IsWelcomeVisible);
        await vm.OpenSaveCommand.ExecuteAsync(null);
        Assert.False(vm.IsWelcomeVisible);
    }

    [AvaloniaFact]
    public void Title_Carries_A_Dirty_Marker()
    {
        var (clean, _, _, _) = Build();
        Assert.DoesNotContain('*', clean.Title);

        var (dirty, _, _, _) = Build(dirty: true);
        Assert.EndsWith(" *", dirty.Title, StringComparison.Ordinal);
        Assert.StartsWith("slot1.dat", dirty.Title, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Exit_Is_Hidden_Without_A_Host()
    {
        // Present-but-inert is worse than absent: a disabled Exit reads as a bug.
        var vm = new EditorShellViewModel(
            new FakeDocumentSession(), new FakeUserInteraction(), new FakeSettingsStore());

        Assert.False(vm.CanExit);
        Assert.False(vm.CanChangeAppearance);
    }

    [AvaloniaFact]
    public void Sections_Respect_Visibility_And_Keep_A_Valid_Selection()
    {
        var (vm, _, _, _) = Build();
        var showInventory = true;

        vm.RegisterSections(
        [
            new SectionDescriptor { Key = "stats", Title = "Stats" },
            new SectionDescriptor { Key = "inventory", Title = "Inventory", IsVisible = () => showInventory },
        ]);

        Assert.Equal(2, vm.Sections.Count);

        vm.SelectedSection = vm.Sections[1];
        showInventory = false;
        vm.RefreshSections();

        // The selection must not survive its section disappearing, or the content
        // pane shows something the sidebar no longer offers.
        Assert.Single(vm.Sections);
        Assert.Equal("stats", vm.SelectedSection?.Key);
    }

    [AvaloniaFact]
    public async Task RefreshSections_Keeps_Bound_ListBox_Selection_When_Section_Still_Visible()
    {
        var (vm, _, _, _) = Build();
        vm.RegisterSections(
        [
            new SectionDescriptor { Key = "stats", Title = "Stats" },
            new SectionDescriptor { Key = "inventory", Title = "Inventory" },
        ]);

        var shell = new EditorShell { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, Content = shell };
        window.Show();

        Dispatcher.UIThread.RunJobs();
        await Task.Yield();

        vm.SelectedSection = vm.Sections[1];
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();

        vm.RefreshSections();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();

        Assert.Equal("inventory", vm.SelectedSection?.Key);

        var listBox = shell.GetVisualDescendants()
            .OfType<ListBox>()
            .Single(l => AutomationProperties.GetName(l) == "Sections");
        var selected = Assert.IsType<SectionDescriptor>(listBox.SelectedItem);
        Assert.Equal("inventory", selected.Key);
    }

    [AvaloniaFact]
    public async Task Shell_Renders_With_Accessible_Names_On_Header_Actions()
    {
        var (vm, _, _, _) = Build();
        vm.RegisterSections([new SectionDescriptor { Key = "stats", Title = "Stats" }]);

        var shell = new EditorShell { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, Content = shell };
        window.Show();

        await Task.Yield();

        var named = shell.GetVisualDescendants()
            .OfType<Button>()
            .Select(b => Avalonia.Automation.AutomationProperties.GetName(b))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        foreach (var expected in new[] { "Open Save", "Save As", "Undo", "Redo" })
        {
            Assert.Contains(expected, named);
        }
    }
}

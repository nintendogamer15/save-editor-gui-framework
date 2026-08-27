using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Shell;
using SaveEditor.Ui.Theming;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>
/// Proves every shell command reaches its handler, and keeps the set closed so a
/// new command cannot be added without someone deciding how it is covered.
/// </summary>
public class ShellCommandRoutingTests
{
    /// <summary>
    /// Every command the shell exposes. Adding one to the view-model without adding
    /// it here fails <see cref="No_Command_Is_Added_Without_Being_Accounted_For"/>.
    /// </summary>
    /// <remarks>
    /// This list exists because "every menu command routes to its handler" was
    /// previously asserted for half the surface, and the untested half is exactly
    /// where a menu item wired to the wrong handler survived.
    /// </remarks>
    private static readonly string[] KnownCommands =
    [
        "OpenSaveCommand", "OpenRecentCommand",
        "SaveAsCommand", "OverwriteWithBackupCommand",
        "ReloadCommand", "CloseCommand", "ExitCommand",
        "UndoCommand", "RedoCommand",
        "SetThemeCommand", "SetAccentCommand", "ResetAccentCommand",
        "ShowAboutCommand", "ShowSafetyCommand",
    ];

    private static (EditorShellViewModel Vm, FakeDocumentSession Session, FakeUserInteraction Interaction)
        Build(bool confirm = true)
    {
        var session = new FakeDocumentSession { HasDocument = true, CurrentPath = "/saves/slot1.dat" };
        var interaction = new FakeUserInteraction { ConfirmResult = confirm };
        var vm = new EditorShellViewModel(session, interaction, new FakeSettingsStore());
        return (vm, session, interaction);
    }

    [AvaloniaFact]
    public void No_Command_Is_Added_Without_Being_Accounted_For()
    {
        var actual = typeof(EditorShellViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(KnownCommands.OrderBy(n => n, StringComparer.Ordinal).ToArray(), actual);
    }

    [AvaloniaFact]
    public void Every_Menu_Item_Is_Bound_To_Its_Own_Command()
    {
        // Testing the command directly is not enough: the defect that motivated this
        // was a menu item bound to the wrong command, which every command-level test
        // passes straight through. This walks the declared menu instead.
        var (vm, _, _) = Build();
        var shell = new EditorShell { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, Content = shell };
        window.Show();

        var menu = shell.GetVisualDescendants().OfType<Menu>().Single();

        var expected = new Dictionary<string, ICommand?>(StringComparer.Ordinal)
        {
            ["_Open Save…"] = vm.OpenSaveCommand,
            ["Save _As…"] = vm.SaveAsCommand,
            ["Over_write + Backup…"] = vm.OverwriteWithBackupCommand,
            ["Re_load"] = vm.ReloadCommand,
            ["_Close"] = vm.CloseCommand,
            ["E_xit"] = vm.ExitCommand,
            ["_Undo"] = vm.UndoCommand,
            ["_Redo"] = vm.RedoCommand,
            ["_About and credits"] = vm.ShowAboutCommand,
            ["_Safety and manual testing"] = vm.ShowSafetyCommand,
            ["_Reset accent to editor default"] = vm.ResetAccentCommand,
        };

        var found = Descend(menu.Items.OfType<MenuItem>())
            .Where(m => m.Header is string header && expected.ContainsKey(header))
            .ToDictionary(m => (string)m.Header!, m => m.Command, StringComparer.Ordinal);

        foreach (var (header, command) in expected)
        {
            Assert.True(found.ContainsKey(header), $"Menu item '{header}' was not found.");
            Assert.Same(command, found[header]);
        }
    }

    [AvaloniaFact]
    public void Data_Driven_Menu_Groups_Are_Bound_To_Their_Own_Commands()
    {
        // Recent, Themes, and Accent are realized from ItemsSource, so the static
        // menu walk never sees them - the same wrong-binding defect can ship green
        // in exactly these three groups unless their containers are inspected.
        var session = new FakeDocumentSession();
        var store = new FakeSettingsStore
        {
            Current = new EditorSettings { RecentFiles = ["/saves/a.dat"] },
        };

        var theme = new ThemeController(
            Application.Current!.Styles.OfType<SaveEditorTheme>().Single(), new FakeSettingsStore());

        var vm = new EditorShellViewModel(session, new FakeUserInteraction(), store, null, theme);
        vm.Recents.Add(new RecentEntry("/saves/a.dat", Display.PathDisplayFormatter.Default.Format("/saves/a.dat")));

        var shell = new EditorShell { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, Content = shell };
        window.Show();

        var menu = shell.GetVisualDescendants().OfType<Menu>().Single();
        var top = menu.Items.OfType<MenuItem>().ToList();

        var file = top.Single(m => (string?)m.Header == "_File");
        var view = top.Single(m => (string?)m.Header == "_View");

        var recent = Realize(window, file).Single(m => (string?)m.Header == "_Recent");
        Assert.All(Realize(window, recent), item =>
        {
            AssertRealizedCommand(item, vm.OpenRecentCommand, typeof(RecentEntry));
        });

        var appearance = Realize(window, view).Single(m => (string?)m.Header == "_Appearance");
        var appearanceChildren = Realize(window, appearance);

        var themes = appearanceChildren.Single(m => (string?)m.Header == "_Themes");
        var themeItems = Realize(window, themes);
        Assert.All(themeItems, item =>
        {
            AssertRealizedCommand(item, vm.SetThemeCommand, typeof(ThemeMode));
        });

        var light = themeItems.Single(item => Equals(item.CommandParameter, ThemeMode.Light));
        light.Command!.Execute(light.CommandParameter);
        Assert.Equal(ThemeMode.Light, theme.Mode);

        var accents = appearanceChildren.Single(m => (string?)m.Header == "A_ccent");
        var accentItems = Realize(window, accents);

        Assert.Equal(14, accentItems.Count);
        Assert.All(accentItems, item =>
        {
            AssertRealizedCommand(item, vm.SetAccentCommand, typeof(CatppuccinAccent));
        });
    }

    /// <summary>
    /// A realized ItemsSource row must show a header, keep the default template,
    /// and carry an executable command. Command-property-only checks stay green
    /// when the container has no template and cannot be clicked.
    /// </summary>
    private static void AssertRealizedCommand(MenuItem item, ICommand expected, Type parameterType)
    {
        Assert.False(string.IsNullOrWhiteSpace(item.Header?.ToString()));
        Assert.NotNull(item.Template);
        Assert.Same(expected, item.Command);
        Assert.NotNull(item.Command);
        Assert.True(item.Command.CanExecute(item.CommandParameter));
        Assert.IsType(parameterType, item.CommandParameter);
    }

    /// <summary>Opens a submenu and returns its realized containers.</summary>
    /// <remarks>
    /// Containers do not exist until the submenu opens and a frame is produced, so
    /// reading them without the render pump returns null commands and would make
    /// this test pass against a completely broken binding.
    /// </remarks>
    private static IReadOnlyList<MenuItem> Realize(Window window, MenuItem parent)
    {
        parent.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        var realized = Enumerable.Range(0, parent.ItemCount)
            .Select(parent.ContainerFromIndex)
            .OfType<MenuItem>()
            .ToList();

        // Separators are items but not MenuItem containers, so the expected count is
        // the non-separator items rather than ItemCount. The guard matters either
        // way: if nothing realized, every Assert.All below would pass over an empty
        // collection and the test would prove nothing.
        var expected = parent.Items.Count(item => item is not Separator);

        Assert.True(
            realized.Count == expected,
            $"Only {realized.Count} of {expected} menu containers realized under " +
            $"'{parent.Header}'; the assertions below would be vacuous.");

        return realized;
    }

    private static IEnumerable<MenuItem> Descend(IEnumerable<MenuItem> items)
    {
        foreach (var item in items)
        {
            yield return item;

            foreach (var child in Descend(item.Items.OfType<MenuItem>()))
            {
                yield return child;
            }
        }
    }

    [AvaloniaFact]
    public async Task Open_Recent_Opens_The_Raw_Path_Not_The_Display_Label()
    {
        var (vm, session, _) = Build();
        var entry = new RecentEntry("/saves/a.dat", SaveEditor.Ui.Display.PathDisplayFormatter.Default.Format("/saves/a.dat"));

        await vm.OpenRecentCommand.ExecuteAsync(entry);

        Assert.Equal("/saves/a.dat", session.OpenedPath);
        Assert.NotEqual(entry.Label.Label, session.OpenedPath);
    }

    [AvaloniaFact]
    public async Task Approved_Reload_And_Close_Reach_The_Session()
    {
        var (vm, session, _) = Build();
        session.IsDirty = true;

        await vm.ReloadCommand.ExecuteAsync(null);
        await vm.CloseCommand.ExecuteAsync(null);

        // Previously only the refusal path was covered, which asserted the session
        // was never called - so a broken approved path would have passed.
        Assert.Contains(nameof(FakeDocumentSession.ReloadAsync), session.Calls);
        Assert.Contains(nameof(FakeDocumentSession.CloseAsync), session.Calls);
    }

    [AvaloniaFact]
    public async Task Help_Commands_Show_A_Message_Rather_Than_Doing_Nothing()
    {
        var (vm, _, interaction) = Build();

        await vm.ShowAboutCommand.ExecuteAsync(null);
        await vm.ShowSafetyCommand.ExecuteAsync(null);

        Assert.Equal(2, interaction.Messages.Count);
        Assert.Equal("About", interaction.Messages[0].Title);
        Assert.Contains("0BSD", interaction.Messages[0].Message, StringComparison.Ordinal);

        // The safety text must state the codec trust boundary, since that is the one
        // thing a user cannot discover from the interface.
        Assert.Contains("not sandboxed", interaction.Messages[1].Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Appearance_Commands_Reach_The_Theme_Controller()
    {
        var theme = new ThemeController(
            Application.Current!.Styles.OfType<SaveEditorTheme>().Single(),
            new FakeSettingsStore());

        await theme.InitializeAsync(TestContext.Current.CancellationToken);

        var vm = new EditorShellViewModel(
            new FakeDocumentSession(), new FakeUserInteraction(), new FakeSettingsStore(), null, theme);

        Assert.True(vm.CanChangeAppearance);
        Assert.Equal(14, vm.Accents.Count);
        Assert.Equal(2, vm.ThemeModes.Count);

        await vm.SetThemeCommand.ExecuteAsync(ThemeMode.Light);
        Assert.Equal(ThemeMode.Light, theme.Mode);

        await vm.SetAccentCommand.ExecuteAsync(CatppuccinAccent.Teal);
        Assert.Equal(CatppuccinAccent.Teal, theme.Accent);

        await vm.ResetAccentCommand.ExecuteAsync(null);
        Assert.True(theme.IsUsingEditorDefault);
    }
}

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Shell;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>
/// Unused optional slots must collapse, and the menu bar must hold nothing but real
/// menus.
/// </summary>
/// <remarks>
/// <para>
/// A host control in the menu bar is a menu entry in its own right: <see cref="Menu"/>
/// generates a <see cref="MenuItem"/> container for any item that is not already one,
/// which is how an editor that never sets <see cref="EditorShell.MenuExtensions"/>
/// ended up with a blank 24px box after Help.
/// </para>
/// <para>
/// Asserting on the host control cannot catch that, because the generated container is
/// a different object and keeps its own visibility. These tests assert on the
/// containers the menu bar actually draws.
/// </para>
/// </remarks>
public class OptionalSlotVisibilityTests
{
    private static readonly string[] FrameworkMenus = ["_File", "_Edit", "_View", "_Help"];

    private static (EditorShell Shell, Window Window) ShowShell(Action<EditorShell>? configure = null)
    {
        var vm = new EditorShellViewModel(
            new FakeDocumentSession(), new FakeUserInteraction(), new FakeSettingsStore());

        vm.RegisterSections([new SectionDescriptor { Key = "stats", Title = "Stats" }]);

        var shell = new EditorShell { DataContext = vm };
        configure?.Invoke(shell);

        var window = new Window { Width = 1000, Height = 700, Content = shell };
        window.Show();
        Layout(window);
        return (shell, window);
    }

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    private static Menu MenuBarOf(EditorShell shell) =>
        shell.GetVisualDescendants().OfType<Menu>().Single();

    /// <summary>The containers the menu bar draws, in bar order.</summary>
    private static IReadOnlyList<Control> MenuBarContainers(EditorShell shell)
    {
        var menu = MenuBarOf(shell);
        return [.. Enumerable.Range(0, menu.ItemCount).Select(i => menu.ContainerFromIndex(i)!)];
    }

    private static IEnumerable<object?> MenuBarHeaders(EditorShell shell) =>
        MenuBarContainers(shell).Cast<MenuItem>().Select(item => item.Header);

    private static ContentControl HeaderActionsHost(EditorShell shell)
    {
        var openSave = shell.GetVisualDescendants()
            .OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "Open Save");

        // Button is a ContentControl, so the slot is the one that is not a button.
        return Assert.IsType<StackPanel>(openSave.Parent)
            .Children.OfType<ContentControl>()
            .Single(c => c.GetType() == typeof(ContentControl));
    }

    private static ContentControl StatusBarExtensionHost(EditorShell shell)
    {
        var status = shell.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(t => AutomationProperties.GetName(t) == "Status");

        return status.FindAncestorOfType<Grid>()!
            .Children.OfType<ContentControl>()
            .Single(c => c.GetType() == typeof(ContentControl));
    }

    private static ContentControl SidebarExtensionHost(EditorShell shell)
    {
        var sections = shell.GetVisualDescendants()
            .OfType<ListBox>()
            .Single(l => AutomationProperties.GetName(l) == "Sections");

        return sections.FindAncestorOfType<DockPanel>()!
            .Children.OfType<ContentControl>()
            .Single(c => DockPanel.GetDock(c) == Dock.Bottom);
    }

    private static IReadOnlyList<MenuItem> VisibleTopLevelMenus(EditorShell shell) =>
        [.. MenuBarOf(shell).GetVisualDescendants()
            .OfType<MenuItem>()
            .Where(item => item.IsVisible
                           && item.IsEffectivelyVisible
                           && !item.GetVisualAncestors().OfType<MenuItem>().Any())];

    private static MenuItem NewMenu(string header, string child)
    {
        var item = new MenuItem { Header = header };
        item.Items.Add(new MenuItem { Header = child });
        return item;
    }

    [AvaloniaFact]
    public void Unused_Optional_Slots_Collapse()
    {
        var (shell, _) = ShowShell();

        Assert.False(HeaderActionsHost(shell).IsVisible);
        Assert.False(StatusBarExtensionHost(shell).IsVisible);
        Assert.False(SidebarExtensionHost(shell).IsVisible);
    }

    [AvaloniaFact]
    public void Menu_Bar_Holds_Only_The_Framework_Menus_When_Unextended()
    {
        var (shell, _) = ShowShell();

        // Cast, not OfType&lt;string&gt;(): a container headed by a control would be
        // filtered out by OfType and the blank entry would go unnoticed.
        Assert.Equal(FrameworkMenus, MenuBarHeaders(shell));
    }

    /// <summary>
    /// The regression guard. Every menu-bar item must be its own container, because a
    /// generated container is what draws as a blank entry and no property set on the
    /// item can collapse it.
    /// </summary>
    [AvaloniaFact]
    public void Menu_Bar_Items_Are_Never_Wrapped_In_A_Generated_Container()
    {
        var (shell, _) = ShowShell(s => s.MenuExtensions = NewMenu("_Tools", "Apply All"));
        var menu = MenuBarOf(shell);

        for (var i = 0; i < menu.ItemCount; i++)
        {
            Assert.Same(menu.Items[i], menu.ContainerFromIndex(i));
        }
    }

    [AvaloniaFact]
    public void Unused_Menu_Extensions_Take_No_Menu_Bar_Space()
    {
        var (unextended, _) = ShowShell();
        var (extended, _) = ShowShell(s => s.MenuExtensions = NewMenu("_Tools", "Apply All"));

        var baseline = VisibleTopLevelMenus(unextended);
        Assert.Equal(4, baseline.Count);
        Assert.All(baseline, item => Assert.True(item.Bounds.Width > 0));

        // No fifth entry is contributing padding after Help, so adding one is what
        // widens the bar.
        var contributed = VisibleTopLevelMenus(extended).Sum(item => item.Bounds.Width)
                          - baseline.Sum(item => item.Bounds.Width);
        Assert.True(contributed > 0, "Setting MenuExtensions did not widen the menu bar.");
    }

    [AvaloniaFact]
    public void Populated_Optional_Slots_Are_Shown()
    {
        var tools = NewMenu("_Tools", "Apply All");
        var (shell, _) = ShowShell(s =>
        {
            s.MenuExtensions = tools;
            s.HeaderActions = new Button { Content = "Apply All" };
            s.StatusBarExtension = new TextBlock { Text = "Codec: demo" };
            s.SidebarExtension = new TextBlock { Text = "Filters" };
        });

        // The editor's menu is a top-level menu, not the header of one.
        Assert.Equal([.. FrameworkMenus, "_Tools"], MenuBarHeaders(shell));
        Assert.Same(tools, MenuBarContainers(shell)[4]);
        Assert.True(tools.IsEffectivelyVisible);
        Assert.True(tools.Bounds.Width > 0);

        Assert.True(HeaderActionsHost(shell).IsVisible);
        Assert.True(StatusBarExtensionHost(shell).IsVisible);
        Assert.True(SidebarExtensionHost(shell).IsVisible);

        var rendered = shell.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Apply All", rendered);
        Assert.Contains("Codec: demo", rendered);
        Assert.Contains("Filters", rendered);
    }

    [AvaloniaFact]
    public void Several_Menus_Are_Appended_In_Order()
    {
        var (shell, _) = ShowShell(s => s.MenuExtensions = new[]
        {
            NewMenu("_Tools", "Apply All"),
            NewMenu("_Debug", "Dump"),
        });

        Assert.Equal([.. FrameworkMenus, "_Tools", "_Debug"], MenuBarHeaders(shell));
    }

    [AvaloniaFact]
    public void Clearing_Menu_Extensions_Removes_Only_What_It_Added()
    {
        var (shell, window) = ShowShell(s => s.MenuExtensions = NewMenu("_Tools", "Apply All"));
        Assert.Equal(5, MenuBarOf(shell).ItemCount);

        shell.MenuExtensions = null;
        Layout(window);

        Assert.Equal(FrameworkMenus, MenuBarHeaders(shell));
        Assert.Equal(4, VisibleTopLevelMenus(shell).Count);
    }

    [AvaloniaFact]
    public void Replacing_Menu_Extensions_Swaps_Them()
    {
        var (shell, window) = ShowShell(s => s.MenuExtensions = NewMenu("_Tools", "Apply All"));

        shell.MenuExtensions = NewMenu("_Debug", "Dump");
        Layout(window);

        Assert.Equal([.. FrameworkMenus, "_Debug"], MenuBarHeaders(shell));
    }

    [AvaloniaFact]
    public void Non_Menu_Content_Is_Rejected_Rather_Than_Drawn_As_A_Menu()
    {
        var (shell, _) = ShowShell();

        // Wrapping a Button would title a menu with a control, which is the shape the
        // blank entry came from.
        var direct = Assert.Throws<ArgumentException>(() => shell.MenuExtensions = new Button());
        Assert.Contains("MenuItem or Separator", direct.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => shell.MenuExtensions = new object[] { new Button() });
        Assert.Throws<ArgumentException>(() => shell.MenuExtensions = "Tools");
    }
}

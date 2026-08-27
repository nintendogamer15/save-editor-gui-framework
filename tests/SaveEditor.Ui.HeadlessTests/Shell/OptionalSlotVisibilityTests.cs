using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Shell;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>
/// Unused optional slots must collapse. A ContentControl in the menu bar is
/// still a menu item with no content, which is how an editor that never sets
/// <see cref="EditorShell.MenuExtensions"/> ends up with a blank box after Help.
/// </summary>
public class OptionalSlotVisibilityTests
{
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

    private static ContentControl MenuExtensionsHost(EditorShell shell)
    {
        var menu = shell.GetVisualDescendants().OfType<Menu>().Single();
        return menu.Items.OfType<ContentControl>().Single();
    }

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

    private static IReadOnlyList<MenuItem> VisibleTopLevelMenus(EditorShell shell)
    {
        var menu = shell.GetVisualDescendants().OfType<Menu>().Single();
        return menu.GetVisualDescendants()
            .OfType<MenuItem>()
            .Where(item => item.IsVisible
                           && item.IsEffectivelyVisible
                           && !item.GetVisualAncestors().OfType<MenuItem>().Any())
            .ToList();
    }

    [AvaloniaFact]
    public void Unused_Optional_Slots_Collapse()
    {
        var (shell, _) = ShowShell();

        Assert.False(MenuExtensionsHost(shell).IsVisible);
        Assert.False(HeaderActionsHost(shell).IsVisible);
        Assert.False(StatusBarExtensionHost(shell).IsVisible);
        Assert.False(SidebarExtensionHost(shell).IsVisible);

        var headers = VisibleTopLevelMenus(shell)
            .Select(item => item.Header)
            .OfType<string>()
            .ToList();

        Assert.Equal(["_File", "_Edit", "_View", "_Help"], headers);
    }

    [AvaloniaFact]
    public void Unused_Menu_Extensions_Do_Not_Take_Menu_Bar_Space()
    {
        var (shell, _) = ShowShell();
        var slot = MenuExtensionsHost(shell);

        Assert.False(slot.IsVisible);
        Assert.Equal(0, slot.Bounds.Width);
    }

    [AvaloniaFact]
    public void Populated_Optional_Slots_Are_Shown()
    {
        var tools = new MenuItem { Header = "_Tools" };
        var (shell, _) = ShowShell(s =>
        {
            s.MenuExtensions = tools;
            s.HeaderActions = new Button { Content = "Apply All" };
            s.StatusBarExtension = new TextBlock { Text = "Codec: demo" };
            s.SidebarExtension = new TextBlock { Text = "Filters" };
        });

        var menuSlot = MenuExtensionsHost(shell);
        Assert.True(menuSlot.IsVisible);
        Assert.Same(tools, menuSlot.Content);
        Assert.True(tools.IsEffectivelyVisible);
        Assert.True(menuSlot.Bounds.Width > 0);

        Assert.True(HeaderActionsHost(shell).IsVisible);
        Assert.True(StatusBarExtensionHost(shell).IsVisible);
        Assert.True(SidebarExtensionHost(shell).IsVisible);

        var rendered = shell.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();

        Assert.Contains("Apply All", rendered);
        Assert.Contains("Codec: demo", rendered);
        Assert.Contains("Filters", rendered);
    }
}

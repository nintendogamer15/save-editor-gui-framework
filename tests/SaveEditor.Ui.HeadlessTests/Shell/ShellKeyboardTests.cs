using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using SaveEditor.Ui.Shell;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>
/// Keyboard reachability for the shell. Accessibility is a release gate, and a
/// control that cannot be reached by Tab cannot be used without a mouse.
/// </summary>
public class ShellKeyboardTests
{
    private static (Window Window, EditorShell Shell) Show()
    {
        var vm = new EditorShellViewModel(
            new FakeDocumentSession(), new FakeUserInteraction(), new FakeSettingsStore());

        vm.RegisterSections(
        [
            new SectionDescriptor { Key = "stats", Title = "Stats" },
            new SectionDescriptor { Key = "inventory", Title = "Inventory" },
        ]);

        var shell = new EditorShell { DataContext = vm };
        var window = new Window { Width = 1200, Height = 800, Content = shell };
        window.Show();

        return (window, shell);
    }

    [AvaloniaFact]
    public void Header_Actions_Are_Reachable_By_Tab_In_Visual_Order()
    {
        var (window, shell) = Show();

        var expected = new[] { "Open Save", "Save As", "Undo", "Redo" };
        var reached = new List<string>();

        // Twenty presses is well past the header; stopping early would let a
        // regression that pushes the actions out of reach still pass.
        for (var i = 0; i < 20 && reached.Count < expected.Length; i++)
        {
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);

            if (TopLevel.GetTopLevel(shell)?.FocusManager?.GetFocusedElement() is Button button)
            {
                var name = Avalonia.Automation.AutomationProperties.GetName(button);
                if (!string.IsNullOrEmpty(name) && !reached.Contains(name) && expected.Contains(name))
                {
                    reached.Add(name);
                }
            }
        }

        Assert.Equal(expected, reached);
    }

    [AvaloniaFact]
    public void Every_Focusable_Header_Control_Carries_An_Accessible_Name()
    {
        var (_, shell) = Show();

        var unnamed = shell.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Focusable)
            .Where(b => string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(b))
                        && b.Content is not string)
            .ToList();

        Assert.True(
            unnamed.Count == 0,
            $"{unnamed.Count} focusable buttons have neither an accessible name nor text content.");
    }

    [AvaloniaFact]
    public void Section_Navigation_Is_Exposed_To_Assistive_Technology()
    {
        var (_, shell) = Show();

        var sectionList = shell.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();

        Assert.NotNull(sectionList);
        Assert.Equal("Sections", Avalonia.Automation.AutomationProperties.GetName(sectionList));
        Assert.Equal(2, sectionList.ItemCount);
    }
}

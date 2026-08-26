using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Shell;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>
/// The section descriptor accepts an icon, so the shell has to render one.
/// </summary>
/// <remarks>
/// This exists because design review against the mockup found the icon accepted
/// and never shown — the fourth instance in this project of an API advertising a
/// capability it did not deliver, after a disabled Exit, inert Help items, and an
/// unused spinner flag. A property read but never rendered is indistinguishable
/// from a broken one.
/// </remarks>
public class SectionIconTests
{
    [AvaloniaFact]
    public void A_Section_Icon_Is_Rendered_In_The_Sidebar()
    {
        var vm = new EditorShellViewModel(
            new FakeDocumentSession(), new FakeUserInteraction(), new FakeSettingsStore());

        vm.RegisterSections(
        [
            new SectionDescriptor { Key = "player", Title = "Player", Icon = "PLAYER-ICON" },
            new SectionDescriptor { Key = "plain", Title = "Plain" },
        ]);

        var shell = new EditorShell { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, Content = shell };
        window.Show();

        // The sidebar list virtualizes, so its containers exist only after layout and
        // a render pass. Two cycles: the first realizes, the second lets the item
        // template's own content presenter materialize.
        for (var i = 0; i < 2; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            Dispatcher.UIThread.RunJobs();
        }

        var rendered = shell.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();

        Assert.Contains("PLAYER-ICON", rendered);

        // Both sections must still be listed: a section without an icon is not a
        // section that disappears.
        Assert.Contains("Player", rendered);
        Assert.Contains("Plain", rendered);
    }
}

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Dialogs;

namespace SaveEditor.Ui.HeadlessTests.Dialogs;

/// <summary>
/// <see cref="AnnouncementRegion"/> is the persistent, accessible announcement
/// region for important errors and outcomes -- exposed to assistive technology as a
/// live region rather than as a transient, auto-dismissing toast.
/// </summary>
public class AnnouncementRegionTests
{
    private static async Task<AnnouncementRegion> Realize(AnnouncementRegion region)
    {
        var window = new Window { Width = 400, Height = 200, Content = region };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        return region;
    }

    [AvaloniaFact]
    public async Task Is_Exposed_As_An_Assertive_Live_Region_For_Errors()
    {
        var region = await Realize(new AnnouncementRegion { Message = "Save failed.", Kind = AnnouncementKind.Error });

        var messageBlock = region.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Message");

        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(messageBlock));
        Assert.Equal("Save failed.", messageBlock.Text);
    }

    [AvaloniaFact]
    public async Task Is_Exposed_As_A_Polite_Live_Region_For_Non_Error_Kinds()
    {
        var region = await Realize(new AnnouncementRegion { Message = "Saved.", Kind = AnnouncementKind.Success });

        var messageBlock = region.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Message");

        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(messageBlock));
    }

    [AvaloniaFact]
    public async Task Hides_Itself_When_There_Is_No_Message()
    {
        var region = await Realize(new AnnouncementRegion());

        var surface = region.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Surface");

        Assert.False(surface.IsVisible);
    }

    [AvaloniaFact]
    public async Task Message_Persists_Until_The_Caller_Clears_It()
    {
        // Persistent, not transient: nothing in the control itself clears
        // Message on a timer. Pumping the dispatcher repeatedly must not make it
        // disappear on its own.
        var region = await Realize(new AnnouncementRegion { Message = "Backup verified.", Kind = AnnouncementKind.Success });

        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        Assert.Equal("Backup verified.", region.Message);
        var surface = region.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Surface");
        Assert.True(surface.IsVisible);
    }
}

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Controls;

namespace SaveEditor.Ui.HeadlessTests.Controls;

/// <summary>
/// P3 acceptance: <c>FieldList</c> genuinely virtualizes. This must not be a proxy
/// assertion — it counts actual realized <see cref="FieldCard"/> containers in the
/// visual tree after a real layout pass, and fails loudly rather than passing
/// vacuously if nothing realized at all.
/// </summary>
public class FieldListVirtualizationTests
{
    private const int TotalFields = 2000;

    [AvaloniaFact]
    public async Task Only_A_Small_Fraction_Of_Fields_Are_Realized()
    {
        var (_, section) = ControlsTestFixture.BuildLargeSection(TotalFields);

        var list = new FieldList { Fields = section.VisibleFields };
        var window = new Window { Width = 900, Height = 700, Content = list };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        var realizedCards = list.GetVisualDescendants().OfType<FieldCard>().ToList();

        // A count of zero would mean nothing rendered at all, which would make the
        // "small fraction" assertion below pass vacuously. Fail loudly instead.
        Assert.True(realizedCards.Count > 0, "No FieldCard containers were realized at all.");

        Assert.True(
            realizedCards.Count < TotalFields / 10,
            $"{realizedCards.Count} of {TotalFields} fields were realized; " +
            "expected only the fields inside the viewport.");
    }

    [AvaloniaFact]
    public async Task Realized_Cards_Present_The_Fields_Actually_In_View()
    {
        var (_, section) = ControlsTestFixture.BuildLargeSection(TotalFields);

        var list = new FieldList { Fields = section.VisibleFields };
        var window = new Window { Width = 900, Height = 700, Content = list };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        var realizedLabels = list.GetVisualDescendants()
            .OfType<FieldCard>()
            .Select(c => c.Field?.Label)
            .Where(l => l is not null)
            .ToList();

        // Realization should start from the top of a freshly shown, unscrolled list.
        Assert.Contains("Stat 0", realizedLabels);
        Assert.DoesNotContain($"Stat {TotalFields - 1}", realizedLabels);
    }
}

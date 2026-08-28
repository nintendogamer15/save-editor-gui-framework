using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Controls;

namespace SaveEditor.Ui.HeadlessTests.Controls;

/// <summary>
/// The section toolbar is the top of the card-based section surface, not a strip
/// bolted above it. It shipped bound to <c>PanelBackground</c> with square corners
/// while the cards below it used <c>CardBackground</c> and <c>RadiusMd</c>, which in
/// the light theme is a plainly visible mismatch.
/// </summary>
/// <remarks>
/// Asserted against the field card's own resolved values rather than against literal
/// colours, so the two surfaces cannot drift apart again by a theme changing what a
/// token means.
/// </remarks>
public class SectionToolbarSurfaceTests
{
    private static (SectionToolbar Toolbar, FieldList List) Show(ThemeVariant variant)
    {
        var (_, _, section) = ControlsTestFixture.BuildSection();

        var toolbar = new SectionToolbar { Editor = section };
        var list = new FieldList { Fields = section.VisibleFields };

        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        layout.Children.Add(list);

        var window = new Window
        {
            Width = 800,
            Height = 600,
            RequestedThemeVariant = variant,
            Content = layout,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        return (toolbar, list);
    }

    private static Border Surface(SectionToolbar toolbar) =>
        toolbar.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_ToolbarSurface");

    private static Border Card(FieldList list) =>
        list.GetVisualDescendants()
            .OfType<FieldCard>()
            .First()
            .GetVisualDescendants()
            .OfType<Border>()
            .Single(b => b.Name == "PART_Card");

    private static Color Fill(Border border) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color;

    private static object Resolve(string key, ThemeVariant variant)
    {
        Assert.True(
            Application.Current!.TryGetResource(key, variant, out var value),
            $"Semantic resource '{key}' did not resolve for {variant}.");

        return value!;
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Toolbar_Uses_The_Same_Surface_As_The_Cards_Below_It(string variantName)
    {
        var variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var (toolbar, list) = Show(variant);

        var expected = Assert.IsAssignableFrom<ISolidColorBrush>(Resolve("CardBackground", variant)).Color;
        var panel = Assert.IsAssignableFrom<ISolidColorBrush>(Resolve("PanelBackground", variant)).Color;

        Assert.Equal(expected, Fill(Surface(toolbar)));
        Assert.Equal(Fill(Card(list)), Fill(Surface(toolbar)));

        // The two tokens differ in both themes, so this is the assertion that would have
        // caught the original binding.
        Assert.NotEqual(panel, expected);
        Assert.NotEqual(panel, Fill(Surface(toolbar)));
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Toolbar_Corners_Match_The_Card_Radius(string variantName)
    {
        var variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var (toolbar, list) = Show(variant);

        var radius = Assert.IsType<CornerRadius>(Resolve("RadiusMd", variant));

        Assert.Equal(radius, Surface(toolbar).CornerRadius);
        Assert.Equal(Card(list).CornerRadius, Surface(toolbar).CornerRadius);

        // A rounded surface with an edge-only border draws as a strip with two stray
        // corners, so the border has to close as well.
        Assert.Equal(Card(list).BorderThickness, Surface(toolbar).BorderThickness);
    }
}

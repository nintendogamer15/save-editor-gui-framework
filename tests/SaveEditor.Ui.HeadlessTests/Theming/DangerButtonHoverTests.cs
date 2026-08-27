using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SaveEditor.Ui.HeadlessTests.Theming;

/// <summary>
/// Destructive buttons keep a danger-coloured fill on hover and press. The base
/// button paints <c>PART_Surface</c> with the neutral hover fill, which outclasses
/// the resting <c>.danger</c> setter on the control.
/// </summary>
public class DangerButtonHoverTests
{
    private static (Button Danger, Button Neutral) ShowPair(ThemeVariant variant)
    {
        var danger = new Button { Content = "Overwrite save file" };
        danger.Classes.Add("danger");

        var neutral = new Button { Content = "Cancel" };

        var window = new Window
        {
            Width = 400,
            Height = 120,
            RequestedThemeVariant = variant,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { neutral, danger },
            },
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (danger, neutral);
    }

    private static Color SurfaceFill(Button button)
    {
        var surface = button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Surface");
        return Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color;
    }

    private static Color Resolve(string key, ThemeVariant variant)
    {
        Assert.True(
            Application.Current!.TryGetResource(key, variant, out var value),
            $"Semantic resource '{key}' did not resolve for {variant}.");

        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static void SetPseudo(Button button, string name, bool value)
    {
        ((IPseudoClasses)button.Classes).Set(name, value);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Hover_Fill_Stays_Distinguishable_From_The_Neutral_Button(string variantName)
    {
        var variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var (danger, neutral) = ShowPair(variant);

        Assert.Equal(Resolve("Danger", variant), SurfaceFill(danger));

        SetPseudo(danger, ":pointerover", true);
        SetPseudo(neutral, ":pointerover", true);

        var dangerFill = SurfaceFill(danger);
        var neutralFill = SurfaceFill(neutral);

        Assert.Equal(Resolve("DangerHover", variant), dangerFill);
        Assert.Equal(Resolve("PanelBackground", variant), neutralFill);
        Assert.NotEqual(neutralFill, dangerFill);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Pressed_Fill_Stays_Distinguishable_From_The_Neutral_Button(string variantName)
    {
        var variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var (danger, neutral) = ShowPair(variant);

        SetPseudo(danger, ":pointerover", true);
        SetPseudo(danger, ":pressed", true);
        SetPseudo(neutral, ":pointerover", true);
        SetPseudo(neutral, ":pressed", true);

        var dangerFill = SurfaceFill(danger);
        var neutralFill = SurfaceFill(neutral);

        Assert.Equal(Resolve("DangerPressed", variant), dangerFill);
        Assert.Equal(Resolve("InputBackground", variant), neutralFill);
        Assert.NotEqual(neutralFill, dangerFill);
    }
}

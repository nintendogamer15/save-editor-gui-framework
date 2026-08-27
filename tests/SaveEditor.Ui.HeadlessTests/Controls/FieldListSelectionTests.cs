using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Controls;
using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.HeadlessTests.Controls;

/// <summary>
/// Clicking a field to edit it must not paint the row with the accent. FieldList
/// is a form; Fluent's ListBoxItem selected fill is a side effect of the
/// virtualizing container, not a state the control has.
/// </summary>
public class FieldListSelectionTests
{
    private static (FieldList List, Window Window) ShowList(ThemeVariant variant)
    {
        var (_, _, section) = ControlsTestFixture.BuildSection();
        var list = new FieldList { Fields = section.VisibleFields };
        var window = new Window
        {
            Width = 700,
            Height = 500,
            RequestedThemeVariant = variant,
            Content = list,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
        return (list, window);
    }

    private static ContentPresenter ItemPresenter(ListBoxItem item) =>
        item.GetVisualChildren().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");

    private static Color PresenterFill(ListBoxItem item)
    {
        var background = ItemPresenter(item).Background;
        Assert.NotNull(background);
        return Assert.IsAssignableFrom<ISolidColorBrush>(background).Color;
    }

    private static Color Resolve(string key, ThemeVariant variant)
    {
        Assert.True(
            Application.Current!.TryGetResource(key, variant, out var value),
            $"Semantic resource '{key}' did not resolve for {variant}.");

        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Color CardFill(FieldCard card)
    {
        var surface = card.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Card");
        return Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color;
    }

    private static void SetPseudo(ListBoxItem item, string name, bool value)
    {
        ((IPseudoClasses)item.Classes).Set(name, value);
        Dispatcher.UIThread.RunJobs();
    }

    private static void ClickCardChrome(FieldCard card, Window window)
    {
        var surface = card.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Card");
        var point = surface.TranslatePoint(new Point(10, 10), window)
                    ?? throw new InvalidOperationException("Could not map the click into the window.");

        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Selected_And_PointerOver_Do_Not_Paint_The_Accent(string variantName)
    {
        var variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var (list, _) = ShowList(variant);

        var item = list.GetVisualDescendants().OfType<ListBoxItem>().First();
        var card = item.GetVisualDescendants().OfType<FieldCard>().Single();

        Assert.Equal(0, PresenterFill(item).A);
        Assert.Equal(Resolve("CardBackground", variant), CardFill(card));

        SetPseudo(item, ":pointerover", true);
        SetPseudo(item, ":selected", true);
        SetPseudo(item, ":pressed", true);

        Assert.Equal(0, PresenterFill(item).A);
        Assert.NotEqual(Resolve("Primary", variant), PresenterFill(item));
        Assert.Equal(Resolve("CardBackground", variant), CardFill(card));
    }

    [AvaloniaFact]
    public void Clicking_A_Card_Does_Not_Paint_The_Row_With_The_Accent()
    {
        var variant = ThemeVariant.Dark;
        var (list, window) = ShowList(variant);

        var card = list.GetVisualDescendants().OfType<FieldCard>().First();
        ClickCardChrome(card, window);

        var item = list.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.IsSelected);
        Assert.Equal(0, PresenterFill(item).A);
        Assert.NotEqual(Resolve("Primary", variant), PresenterFill(item));
        Assert.Equal(Resolve("CardBackground", variant), CardFill(card));
    }

    [AvaloniaFact]
    public void A_Plain_ListBox_Still_Paints_The_Selected_Row()
    {
        var box = new ListBox { ItemsSource = new[] { "One", "Two" } };
        var window = new Window
        {
            Width = 300,
            Height = 200,
            RequestedThemeVariant = ThemeVariant.Dark,
            Content = box,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        box.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        var item = box.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.IsSelected);
        Assert.True(
            PresenterFill(item).A > 0,
            "A plain ListBox should still paint its selected row; FieldList's container theme must stay scoped to FieldList.");
    }

    [AvaloniaFact]
    public void Clicking_A_Text_Field_Still_Focuses_The_Editor()
    {
        var (list, window) = ShowList(ThemeVariant.Dark);

        var name = list.Fields!.OfType<TextFieldViewModel>().Single();
        var card = list.GetVisualDescendants().OfType<FieldCard>().Single(c => c.Field == name);
        var box = card.GetVisualDescendants().OfType<TextBox>().Single();

        var point = box.TranslatePoint(new Point(12, box.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("Could not map the click into the window.");

        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(box.IsFocused);
    }
}

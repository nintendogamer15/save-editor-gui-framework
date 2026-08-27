using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Controls;
using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.HeadlessTests.Controls;

/// <summary>
/// Clicking the blank region of a themed text field has to move the caret. A
/// presenter that sizes to its glyphs leaves that region on the chrome, so the
/// click never becomes a caret position.
/// </summary>
public class TextBoxCaretHitTests
{
    private const string SampleText = "Hi";

    private static (TextBox Box, Window Window) ShowTextBox()
    {
        var box = new TextBox
        {
            Text = SampleText,
            Width = 400,
            Height = 40,
        };

        var window = new Window { Width = 500, Height = 120, Content = box };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
        return (box, window);
    }

    private static void ClickFarRightOfText(TextBox box, Window window)
    {
        var presenter = box.GetVisualDescendants().OfType<TextPresenter>().Single();

        // Click inside the presenter, well past the glyphs. The padding around
        // the presenter belongs to the chrome; the bug is the empty run of the
        // field itself, which only receives the click when the presenter fills it.
        var local = new Point(Math.Max(presenter.Bounds.Width - 8, 1), presenter.Bounds.Height / 2);
        var point = presenter.TranslatePoint(local, window)
                    ?? throw new InvalidOperationException("Could not map the click into the window.");

        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_Text_Presenter_Fills_The_Box_Horizontally()
    {
        var (box, _) = ShowTextBox();

        var presenter = box.GetVisualDescendants().OfType<TextPresenter>().Single();

        Assert.Equal(HorizontalAlignment.Stretch, presenter.HorizontalAlignment);
        Assert.True(
            presenter.Bounds.Width > box.Bounds.Width / 2,
            $"PART_TextPresenter is {presenter.Bounds.Width:0.##}px wide in a {box.Bounds.Width:0.##}px box, "
            + "so the blank region to the right of the glyphs is not part of the presenter.");
    }

    [AvaloniaFact]
    public void Clicking_The_Empty_Space_Right_Of_The_Text_Moves_The_Caret_To_The_End()
    {
        var (box, window) = ShowTextBox();

        box.CaretIndex = 0;
        Dispatcher.UIThread.RunJobs();

        ClickFarRightOfText(box, window);

        Assert.Equal(SampleText.Length, box.CaretIndex);
        Assert.Equal(SampleText.Length, box.SelectionStart);
        Assert.Equal(SampleText.Length, box.SelectionEnd);
    }

    [AvaloniaFact]
    public void Clicking_The_Empty_Space_Of_A_Numeric_Field_Moves_The_Caret_To_The_End()
    {
        long stored = 12;
        var vm = new NumericFieldViewModel(
            new NumericFieldDescriptor
            {
                Key = "level",
                Label = "Level",
                Minimum = 0,
                Maximum = 99,
                Read = () => stored,
                Write = v => stored = v,
            },
            new EditHistory());

        var card = new FieldCard { Field = vm };
        var window = new Window { Width = 700, Height = 300, Content = card };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        var box = card.GetVisualDescendants().OfType<TextBox>().Single();
        box.CaretIndex = 0;
        Dispatcher.UIThread.RunJobs();

        ClickFarRightOfText(box, window);

        Assert.Equal(box.Text!.Length, box.CaretIndex);
    }

    [AvaloniaFact]
    public void Clicking_The_Empty_Space_Of_A_Choice_Field_Moves_The_Caret_To_The_End()
    {
        var stored = "normal";
        var vm = new ChoiceFieldViewModel(
            new ChoiceFieldDescriptor
            {
                Key = "difficulty",
                Label = "Difficulty",
                Options = new StaticChoices(),
                Read = () => stored,
                Write = v => stored = v,
            },
            new EditHistory());

        var card = new FieldCard { Field = vm };
        var window = new Window { Width = 700, Height = 300, Content = card };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        var autoComplete = card.GetVisualDescendants().OfType<AutoCompleteBox>().Single();
        var box = autoComplete.GetVisualDescendants().OfType<TextBox>().Single();
        box.CaretIndex = 0;
        Dispatcher.UIThread.RunJobs();

        ClickFarRightOfText(box, window);

        Assert.Equal(box.Text!.Length, box.CaretIndex);
    }

    private sealed class StaticChoices : IChoiceProvider
    {
        public ValueTask<IReadOnlyList<ChoiceOption>> GetOptionsAsync(
            string filter, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ChoiceOption> all =
            [
                new("normal", "Normal"),
                new("hard", "Hard"),
            ];

            return ValueTask.FromResult(all);
        }
    }
}

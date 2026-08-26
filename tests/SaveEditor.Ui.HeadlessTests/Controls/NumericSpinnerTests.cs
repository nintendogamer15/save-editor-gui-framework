using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Controls;
using SaveEditor.Ui.Editing;

namespace SaveEditor.Ui.HeadlessTests.Controls;

/// <summary>
/// The numeric spinner has to be visible and has to work. The descriptor
/// advertises <c>ShowSpinner</c>, so a spinner that renders as a speck is a
/// property that lies.
/// </summary>
/// <remarks>
/// <para>
/// The buttons hard-coded a 32px width while inheriting the Button theme's
/// 14px-a-side padding and 1px border — 30px of chrome, leaving a 2px content
/// area. Both glyphs were clipped to a single 1x2px mark on both platforms.
/// </para>
/// <para>
/// The screenshot gate did not catch it, because the baselines were seeded while
/// the bug was already present: an unreviewed reference pins whatever was on
/// screen at seeding time, including the defects. That is the argument for
/// asserting the property directly rather than trusting a golden image to
/// encode it.
/// </para>
/// </remarks>
public class NumericSpinnerTests
{
    /// <summary>
    /// Below this, a glyph is a speck rather than an affordance. The clipped
    /// spinner laid its text out at 2px; a spinner with room lays it out at
    /// about 10px.
    /// </summary>
    private const double MinimumGlyphWidth = 6;

    private static (NumericFieldViewModel Vm, IReadOnlyList<Button> Spinners, Window Window) Build()
    {
        long stored = 12;

        var vm = new NumericFieldViewModel(
            new NumericFieldDescriptor
            {
                Key = "level",
                Label = "Level",
                Minimum = 0,
                Maximum = 99,
                ShowSpinner = true,
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

        // The per-field Apply button lives on the card too, so the spinners are
        // identified by their content rather than by taking the first two buttons.
        var spinners = card.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Content is "−" or "+")
            .ToList();

        return (vm, spinners, window);
    }

    [AvaloniaFact]
    public void Both_Spinner_Buttons_Have_Room_To_Draw_Their_Glyph()
    {
        var (_, spinners, _) = Build();

        Assert.Equal(2, spinners.Count);

        foreach (var button in spinners)
        {
            // Asserted on the laid-out text rather than on the presenter. The
            // presenter fills the button either way -- it applies its padding
            // internally, so its own bounds stay 30px wide even when they leave
            // 2px for the glyph. Only the text's own width distinguishes the two.
            var text = button.GetVisualDescendants()
                .OfType<TextBlock>()
                .First();

            var presenter = button.GetVisualDescendants()
                .OfType<ContentPresenter>()
                .First();

            Assert.True(
                text.Bounds.Width >= MinimumGlyphWidth,
                $"The '{button.Content}' spinner lays its glyph out at {text.Bounds.Width:0.##}px "
                + $"inside a {button.Bounds.Width:0.##}px button carrying {presenter.Padding} of "
                + "padding, which clips it to a speck. The Button theme pads wider than this button "
                + "is; override Padding rather than inheriting it.");
        }
    }

    [AvaloniaFact]
    public void The_Spinner_Buttons_Actually_Change_The_Draft()
    {
        var (vm, spinners, _) = Build();

        var up = spinners.Single(b => (string?)b.Content == "+");
        var down = spinners.Single(b => (string?)b.Content == "−");

        var start = vm.Text;

        up.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var afterIncrement = vm.Text;

        Assert.NotEqual(start, afterIncrement);

        down.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(start, vm.Text);
    }
}

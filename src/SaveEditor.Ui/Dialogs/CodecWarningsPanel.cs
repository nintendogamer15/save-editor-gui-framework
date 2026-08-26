using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.Dialogs;

/// <summary>
/// Builds the visually distinct, non-chrome region a dialog uses to show
/// codec-supplied warning text.
/// </summary>
/// <remarks>
/// This is the display half of closing finding A8. The framework's own title,
/// framing sentence, and accept label are set directly on the surrounding dialog
/// and never pass through here; only <see cref="ConfirmationRequest.Details"/> and
/// <see cref="MessageRequest.Details"/> do. Everything built here uses a distinct
/// background wash, a distinct border, and a monospace font so a crafted warning
/// cannot be mistaken for framework chrome even before its content is considered.
/// </remarks>
public static class CodecWarningsPanel
{
    private const string Heading = "Reported by the save file itself. This text is not verified:";

    /// <summary>
    /// Builds the warnings region, or returns <see langword="null"/> when there is
    /// nothing to show.
    /// </summary>
    /// <param name="details">Untrusted warning text, as carried on the request.</param>
    /// <returns>A read-only control, or <see langword="null"/> when <paramref name="details"/> is empty.</returns>
    public static Control? TryBuild(IReadOnlyList<UntrustedText> details)
    {
        ArgumentNullException.ThrowIfNull(details);

        if (details.Count == 0)
        {
            return null;
        }

        var sanitized = CodecWarningPresenter.Sanitize(details);

        var heading = new TextBlock
        {
            Text = Heading,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        heading.Bind(TextBlock.ForegroundProperty, heading.GetResourceObservable("WarningText"));

        var list = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };

        foreach (var warning in sanitized.Shown)
        {
            var line = new SelectableTextBlock
            {
                FontFamily = new FontFamily("monospace"),
                TextWrapping = TextWrapping.Wrap,
            };
            line.Bind(TextBlock.ForegroundProperty, line.GetResourceObservable("Foreground"));

            var inlines = new InlineCollection { new Run("• "), new Run(warning.Text) };
            if (warning.WasTruncated)
            {
                inlines.Add(new Run(" (truncated)"));
            }

            line.Inlines = inlines;

            AutomationProperties.SetName(line, "Warning from save file");
            list.Children.Add(line);
        }

        if (sanitized.OmittedCount > 0)
        {
            var omitted = new TextBlock
            {
                Text = $"+{sanitized.OmittedCount} more not shown.",
                FontStyle = FontStyle.Italic,
                Margin = new Thickness(0, 4, 0, 0),
            };
            omitted.Bind(TextBlock.ForegroundProperty, omitted.GetResourceObservable("MutedForeground"));
            list.Children.Add(omitted);
        }

        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(heading);
        body.Children.Add(list);

        var surface = new Border
        {
            Name = "PART_CodecWarnings",
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            Child = body,
        };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("WarningBackground"));
        surface.Bind(Border.BorderBrushProperty, surface.GetResourceObservable("Warning"));
        surface.Bind(Border.CornerRadiusProperty, surface.GetResourceObservable("RadiusSm"));

        AutomationProperties.SetName(surface, "Warnings reported by the save file");

        return surface;
    }
}

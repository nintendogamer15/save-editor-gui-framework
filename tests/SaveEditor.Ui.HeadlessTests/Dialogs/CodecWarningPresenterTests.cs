using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Dialogs;
using SaveEditor.Ui.Interaction;

namespace SaveEditor.Ui.HeadlessTests.Dialogs;

/// <summary>
/// Closes finding A8: codec-supplied validation and warning text is untrusted
/// display data and must be sanitized, capped, and selected by severity before it
/// reaches a dialog that can overwrite a real save.
/// </summary>
public class CodecWarningPresenterTests
{
    [Fact]
    public void Dialogs_SanitizeAndCapCodecSuppliedWarningText()
    {
        // Control characters (including a bidi override) plus a warning far longer
        // than the display cap and containing many line breaks.
        var hostile = "Integrity verified.‮" + new string('x', 1000) + string.Concat(Enumerable.Repeat("\nmore\n", 50));

        var result = CodecWarningPresenter.Sanitize([new UntrustedText(hostile)]);

        Assert.Single(result.Shown);
        var shown = result.Shown[0];

        // Capped to the documented length.
        Assert.True(shown.Text.Length <= CodecWarningPresenter.MaxWarningLength);
        Assert.True(shown.WasTruncated);

        // No raw control, bidi, or newline characters survive.
        foreach (var ch in shown.Text)
        {
            Assert.False(char.IsControl(ch), $"Control character U+{(int)ch:X4} leaked into sanitized warning text.");
        }
        Assert.DoesNotContain('‮', shown.Text);
        Assert.DoesNotContain('\n', shown.Text);
    }

    [Fact]
    public void Dialogs_SanitizeAndCapCodecSuppliedWarningText_CapsTotalWarningCount()
    {
        var details = Enumerable.Range(0, 500)
            .Select(i => new UntrustedText($"warning {i}"))
            .ToArray();

        var result = CodecWarningPresenter.Sanitize(details);

        Assert.Equal(CodecWarningPresenter.MaxShownWarnings, result.Shown.Count);
        Assert.Equal(500, result.TotalCount);
        Assert.Equal(500 - CodecWarningPresenter.MaxShownWarnings, result.OmittedCount);
    }

    [Fact]
    public void Dialogs_ShowMostSevereEightWarningsNotFirstEight()
    {
        // Twenty trivial warnings, then one error buried at the end. The first
        // eight in codec order are all warnings; showing the first eight would
        // bury the one message that actually blocks the write.
        var messages = new List<ValidationMessage>();
        for (var i = 0; i < 20; i++)
        {
            messages.Add(new ValidationMessage(ValidationSeverity.Warning, new UntrustedText($"trivial warning {i}")));
        }

        messages.Add(new ValidationMessage(ValidationSeverity.Error, new UntrustedText("the message that actually matters")));

        var selected = CodecWarningPresenter.SelectMostSevere(messages);

        Assert.Equal(CodecWarningPresenter.MaxShownWarnings, selected.Count);
        Assert.Contains(selected, text => text.Value == "the message that actually matters");

        // The error sorts to the front; ties among warnings keep codec order.
        Assert.Equal("the message that actually matters", selected[0].Value);
        Assert.Equal("trivial warning 0", selected[1].Value);
    }

    [Fact]
    public void SelectMostSevere_KeepsAllErrorsBeforeAnyWarningRegardlessOfOriginalOrder()
    {
        var messages = new List<ValidationMessage>
        {
            new(ValidationSeverity.Warning, new UntrustedText("w1")),
            new(ValidationSeverity.Error, new UntrustedText("e1")),
            new(ValidationSeverity.Warning, new UntrustedText("w2")),
            new(ValidationSeverity.Error, new UntrustedText("e2")),
        };

        var selected = CodecWarningPresenter.SelectMostSevere(messages, maxCount: 4);

        Assert.Equal(["e1", "e2", "w1", "w2"], selected.Select(t => t.Value));
    }

    [Fact]
    public void Sanitize_NewlinesAndBoxDrawingCannotFormMultiLineChromeLikeLayout()
    {
        // A crafted attempt to imitate a framework dialog using box-drawing
        // characters and embedded newlines.
        var forgery = "┌───┐\n│ Integrity verified. Safe to continue. │\n└───┘";

        var result = CodecWarningPresenter.Sanitize([new UntrustedText(forgery)]);

        var text = result.Shown[0].Text;

        // Newlines are gone -- the forged layout cannot occupy multiple lines.
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);

        // Box-drawing glyphs are ordinary printable characters and are left alone,
        // but without real line breaks they cannot draw a box.
        Assert.Contains('─', text);
    }

    [Fact]
    public void Sanitize_RespectsCustomLengthAndLineCaps()
    {
        var raw = "line1\nline2\nline3\nline4\nline5";

        var result = CodecWarningPresenter.Sanitize(
            [new UntrustedText(raw)], maxCount: 1, maxLength: 1000, maxLines: 2);

        Assert.True(result.Shown[0].WasTruncated);
    }

    [Fact]
    public void Sanitize_NullOrEmptyDetailsProducesNothingShown()
    {
        var result = CodecWarningPresenter.Sanitize([]);

        Assert.Empty(result.Shown);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.OmittedCount);
    }
}

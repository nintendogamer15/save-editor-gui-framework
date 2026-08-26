using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SaveEditor.Ui.Dialogs;

namespace SaveEditor.Ui.HeadlessTests.Dialogs;

/// <summary>
/// <see cref="ThemedUserInteraction"/>'s modal dialog flows require a real modal
/// message pump, which a headless test cannot drive; see the class remarks on
/// <see cref="ConfirmationDialogView"/>, <see cref="MessageDialogView"/>, and the
/// other content controls, which are tested directly instead. This covers what does
/// not require that pump: construction and argument validation.
/// </summary>
public class ThemedUserInteractionTests
{
    [AvaloniaFact]
    public void Constructor_Requires_A_Host_Window()
    {
        Assert.Throws<ArgumentNullException>(() => new ThemedUserInteraction(null!));
    }

    [AvaloniaFact]
    public void ConfirmOverwriteAsync_Requires_A_Path()
    {
        var owner = new Window();
        var interaction = new ThemedUserInteraction(owner);

        Assert.Throws<ArgumentException>(() => interaction.ConfirmOverwriteAsync(string.Empty));
    }

    [AvaloniaFact]
    public async Task ShowDocumentAsync_Requires_A_Title()
    {
        var owner = new Window();
        var interaction = new ThemedUserInteraction(owner);

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await interaction.ShowDocumentAsync(null!));
    }
}

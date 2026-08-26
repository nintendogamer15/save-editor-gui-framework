using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SaveEditor.Ui.Hosting;
using SaveEditor.Ui.Shell;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>
/// Covers the real host rather than the fake.
/// </summary>
/// <remarks>
/// Every other exit-guard test runs against <see cref="FakeEditorHost"/>. That
/// proves the view-model installs a guard, but not that the shipped host consults
/// it — so a regression removing the <c>Closing</c> subscription, or the
/// re-entrancy flag, would leave the rest of the suite green while either
/// discarding a user's unsaved work or double-prompting them.
/// </remarks>
public class WindowEditorHostTests
{
    private static (Window Window, WindowEditorHost Host, FakeUserInteraction Interaction)
        Build(bool pendingEdits, bool confirm)
    {
        var window = new Window { Width = 800, Height = 600 };
        var host = new WindowEditorHost(window);

        var session = new FakeDocumentSession
        {
            HasDocument = pendingEdits,
            HasPendingEdits = pendingEdits,
            CurrentPath = pendingEdits ? "/saves/slot1.dat" : null,
        };

        var interaction = new FakeUserInteraction { ConfirmResult = confirm };

        // Constructing the view-model installs the guard on the host.
        _ = new EditorShellViewModel(session, interaction, new FakeSettingsStore(), host);

        window.Show();
        return (window, host, interaction);
    }

    [AvaloniaFact]
    public void Closing_The_Window_With_Pending_Edits_Prompts_And_Leaves_It_Open()
    {
        var (window, _, interaction) = Build(pendingEdits: true, confirm: false);

        window.Close();

        Assert.Single(interaction.Confirmations);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void Closing_The_Window_On_Approval_Prompts_Exactly_Once()
    {
        var (window, _, interaction) = Build(pendingEdits: true, confirm: true);

        window.Close();

        // The host cancels the first close, awaits the guard, then closes for real.
        // Without the re-entrancy flag that second close re-raises the guard and the
        // user is asked twice for one decision.
        Assert.Single(interaction.Confirmations);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task RequestShutdown_Through_The_Real_Host_Honours_A_Refusal()
    {
        var (window, host, interaction) = Build(pendingEdits: true, confirm: false);

        await host.RequestShutdownAsync(TestContext.Current.CancellationToken);

        Assert.Single(interaction.Confirmations);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void Closing_With_Nothing_Unsaved_Does_Not_Prompt()
    {
        var (window, _, interaction) = Build(pendingEdits: false, confirm: true);

        window.Close();

        Assert.Empty(interaction.Confirmations);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Host_Applies_A_Restored_Size_And_Reports_Changes()
    {
        var window = new Window { Width = 800, Height = 600 };
        using var host = new WindowEditorHost(window);

        window.Show();
        host.ApplySize(new Avalonia.Size(1024, 768));

        Assert.Equal(1024, window.Width);
        Assert.Equal(768, window.Height);
    }
}

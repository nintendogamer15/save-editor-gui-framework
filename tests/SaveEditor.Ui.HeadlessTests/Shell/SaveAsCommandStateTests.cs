using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Shell;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>
/// Save As has to come back. An asynchronous command reports itself unavailable while
/// its own task is in flight, so any outcome that leaves that task unfinished — or any
/// completion the shell fails to reflect — strands the toolbar button and the menu item
/// grey for the rest of the session, with no way back short of restarting the editor.
/// </summary>
/// <remarks>
/// Driven through the real workflow and the real session rather than a stub, and
/// asserted on the rendered button as well as the command: a command that reports
/// <c>CanExecute</c> correctly while the button it is bound to stays disabled is the
/// same defect from the user's side.
/// </remarks>
public class SaveAsCommandStateTests
{
    private static (EditorShell Shell, Window Window) Show(EditorShellViewModel vm)
    {
        var shell = new EditorShell { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, Content = shell };

        window.Show();
        Pump(window);
        return (shell, window);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    private static Button SaveAsButton(EditorShell shell) =>
        shell.GetVisualDescendants()
            .OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "Save As");

    private static void AssertSaveAsIsAvailable(EditorShellViewModel vm, EditorShell shell, Window window, string after)
    {
        Pump(window);

        Assert.True(vm.SaveAsCommand.CanExecute(null), $"SaveAsCommand stayed unavailable after {after}.");
        Assert.True(SaveAsButton(shell).IsEffectivelyEnabled, $"The Save As button stayed disabled after {after}.");
    }

    /// <summary>Opens a save and returns the harness with a document in hand.</summary>
    private static async Task<ShellWorkflowHarness> OpenedAsync(string label, CancellationToken cancellationToken)
    {
        var harness = ShellWorkflowHarness.Create(label);
        var path = harness.WriteSave("slot1.sav", new ShellDoc("Aerith", 3));

        await harness.Vm.OpenPathAsync(path, cancellationToken);
        Assert.True(harness.Session.HasDocument);

        return harness;
    }

    [AvaloniaFact]
    public async Task Save_As_Re_Enables_After_A_Successful_Write()
    {
        using var harness = await OpenedAsync("saveas-success", TestContext.Current.CancellationToken);
        var (shell, window) = Show(harness.Vm);

        var destination = harness.Destination("copy.sav");
        harness.WorkflowInteraction.SavePicker = _ => new SaveFilePickResult(destination, false);

        await harness.Vm.SaveAsCommand.ExecuteAsync(null);

        Assert.True(harness.Session.LastOutcome!.IsSuccess);
        Assert.True(File.Exists(destination));
        AssertSaveAsIsAvailable(harness.Vm, shell, window, "a successful Save As");
    }

    [AvaloniaFact]
    public async Task Save_As_Re_Enables_After_A_Declined_Picker()
    {
        using var harness = await OpenedAsync("saveas-declined", TestContext.Current.CancellationToken);
        var (shell, window) = Show(harness.Vm);

        // Dismissing the chooser. The default already declines; being explicit is the point.
        harness.WorkflowInteraction.SavePicker = _ => null;

        await harness.Vm.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(SaveStatus.Declined, harness.Session.LastOutcome!.Status);
        AssertSaveAsIsAvailable(harness.Vm, shell, window, "a dismissed picker");
    }

    [AvaloniaFact]
    public async Task Save_As_Re_Enables_After_A_Cancellation()
    {
        using var harness = await OpenedAsync("saveas-cancelled", TestContext.Current.CancellationToken);
        var (shell, window) = Show(harness.Vm);

        harness.WorkflowInteraction.SavePicker = _ => throw new OperationCanceledException();

        await harness.Vm.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(SaveStatus.Cancelled, harness.Session.LastOutcome!.Status);
        AssertSaveAsIsAvailable(harness.Vm, shell, window, "a cancelled Save As");
    }

    [AvaloniaFact]
    public async Task Save_As_Re_Enables_After_A_Failure()
    {
        using var harness = await OpenedAsync("saveas-failed", TestContext.Current.CancellationToken);
        var (shell, window) = Show(harness.Vm);

        harness.Codec.SerializeFailure = "The codec refused to serialize.";
        harness.WorkflowInteraction.SavePicker = _ => new SaveFilePickResult(harness.Destination("copy.sav"), false);

        await harness.Vm.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(SaveStatus.Failed, harness.Session.LastOutcome!.Status);
        AssertSaveAsIsAvailable(harness.Vm, shell, window, "a failed Save As");
    }

    [AvaloniaFact]
    public async Task Editing_After_A_Save_Leaves_Save_As_Available()
    {
        using var harness = await OpenedAsync("saveas-after-edit", TestContext.Current.CancellationToken);
        var (shell, window) = Show(harness.Vm);

        harness.WorkflowInteraction.SavePicker = _ => new SaveFilePickResult(harness.Destination("copy.sav"), false);
        await harness.Vm.SaveAsCommand.ExecuteAsync(null);
        Assert.True(harness.Session.LastOutcome!.IsSuccess);

        // The state change an edit raises must not be able to leave the command behind.
        harness.Session.ReplaceDocument(harness.Session.Document! with { Level = 99 });

        AssertSaveAsIsAvailable(harness.Vm, shell, window, "an edit following a save");
    }

    [AvaloniaFact]
    public async Task Save_As_Is_Unavailable_Only_While_It_Is_Running()
    {
        using var harness = await OpenedAsync("saveas-in-flight", TestContext.Current.CancellationToken);
        var (shell, window) = Show(harness.Vm);

        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        harness.WorkflowInteraction.SavePicker = _ =>
        {
            reached.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return null;
        };

        var running = harness.Vm.SaveAsCommand.ExecuteAsync(null);
        await reached.Task;

        // Disabled while the picker is up, so the guarantee under test is that it comes
        // back — not that it was never disabled in the first place.
        Assert.False(harness.Vm.SaveAsCommand.CanExecute(null));

        release.SetResult();
        await running;

        AssertSaveAsIsAvailable(harness.Vm, shell, window, "the operation finished");
    }
}

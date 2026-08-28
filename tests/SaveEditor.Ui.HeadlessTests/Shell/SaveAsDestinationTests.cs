using Avalonia.Headless.XUnit;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.HeadlessTests.Shell;

/// <summary>
/// Save As has to ask where. The framework's own coverage proved the picker-driven
/// overload writes where it is told, but never that the shell's Save As reaches a
/// chooser at all, what it asks for, or that a pick naming the open file is still
/// subject to the adopter's policy rather than quietly becoming an overwrite.
/// </summary>
public class SaveAsDestinationTests
{
    private static async Task<ShellWorkflowHarness> OpenedAsync(
        string label, CancellationToken cancellationToken, IWritePolicy? policy = null)
    {
        var harness = ShellWorkflowHarness.Create(label, policy);
        var path = harness.WriteSave("slot1.sav", new ShellDoc("Aerith", 3));

        await harness.Vm.OpenPathAsync(path, cancellationToken);
        Assert.True(harness.Session.HasDocument);

        return harness;
    }

    [AvaloniaFact]
    public async Task Save_As_Asks_Where_To_Write_Beside_The_Open_Save()
    {
        using var harness = await OpenedAsync("saveas-asks", TestContext.Current.CancellationToken);
        var open = harness.Session.CurrentPath!;

        harness.WorkflowInteraction.SavePicker = _ => null;
        await harness.Vm.SaveAsCommand.ExecuteAsync(null);

        var request = Assert.Single(harness.WorkflowInteraction.SaveRequests);

        Assert.Equal("Save a copy", request.Title);
        Assert.Equal(Path.GetFileName(open), request.SuggestedFileName);

        // The starting directory is part of the request because a chooser that opens
        // somewhere unrelated to the save being copied is the next worst thing to one
        // that does not open.
        Assert.Equal(Path.GetDirectoryName(open), request.SuggestedDirectory);
    }

    [AvaloniaFact]
    public async Task Dismissing_The_Chooser_Writes_Nothing()
    {
        using var harness = await OpenedAsync("saveas-dismissed", TestContext.Current.CancellationToken);
        var open = harness.Session.CurrentPath!;
        var before = File.ReadAllBytes(open);

        harness.WorkflowInteraction.SavePicker = _ => null;
        await harness.Vm.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(SaveStatus.Declined, harness.Session.LastOutcome!.Status);
        Assert.Equal(before, File.ReadAllBytes(open));
        Assert.Equal([open], Directory.GetFiles(harness.Root));
        Assert.Equal(open, harness.Session.CurrentPath);
    }

    [AvaloniaFact]
    public async Task A_Chosen_Path_Is_Written_And_Becomes_The_Open_Document()
    {
        using var harness = await OpenedAsync("saveas-chosen", TestContext.Current.CancellationToken);
        var open = harness.Session.CurrentPath!;
        var original = File.ReadAllBytes(open);

        harness.Session.ReplaceDocument(harness.Session.Document! with { Level = 42 });

        var destination = harness.Destination("copy.sav");
        harness.WorkflowInteraction.SavePicker = _ => new SaveFilePickResult(destination, false);

        await harness.Vm.SaveAsCommand.ExecuteAsync(null);

        Assert.True(harness.Session.LastOutcome!.IsSuccess);
        Assert.Equal(ShellCodec.Encode(new ShellDoc("Aerith", 42)), File.ReadAllText(destination));

        // The copy is what is now being edited, and the source is untouched.
        Assert.Equal(destination, harness.Session.CurrentPath);
        Assert.Equal(42, harness.Session.Document!.Level);
        Assert.Equal(original, File.ReadAllBytes(open));
    }

    [AvaloniaFact]
    public async Task A_Pick_Naming_The_Open_File_Still_Goes_Through_The_Write_Policy()
    {
        var policy = new ScriptedWritePolicy
        {
            Decide = plan => plan.DestinationExists
                ? WriteDecision.Refuse("This editor never lets Save As replace an existing save.")
                : WriteDecision.Proceed,
        };

        using var harness = await OpenedAsync("saveas-same-path", TestContext.Current.CancellationToken, policy);
        var open = harness.Session.CurrentPath!;
        var before = File.ReadAllBytes(open);

        harness.Session.ReplaceDocument(harness.Session.Document! with { Level = 42 });
        harness.WorkflowInteraction.SavePicker = _ => new SaveFilePickResult(open, false);

        await harness.Vm.SaveAsCommand.ExecuteAsync(null);

        // The policy has to be told what it is actually being asked about: the open
        // document, which already exists. A Save As that reached the write without this
        // would be an in-place overwrite the adopter never agreed to.
        var plan = Assert.Single(policy.Plans);
        Assert.Equal(PlannedWriteKind.SaveAs, plan.Kind);
        Assert.True(plan.IsCurrentDocument);
        Assert.True(plan.DestinationExists);

        Assert.False(harness.Session.LastOutcome!.IsSuccess);
        Assert.Contains("never lets Save As replace", harness.Vm.StatusMessage, StringComparison.Ordinal);

        // Refused before anything destructive: same bytes, and no backup or temporary
        // file left behind either.
        Assert.Equal(before, File.ReadAllBytes(open));
        Assert.Equal([open], Directory.GetFiles(harness.Root));
    }

    [AvaloniaFact]
    public async Task A_Pick_Naming_The_Open_File_Is_Confirmed_Before_It_Replaces_Anything()
    {
        using var harness = await OpenedAsync("saveas-same-path-confirm", TestContext.Current.CancellationToken);
        var open = harness.Session.CurrentPath!;
        var before = File.ReadAllBytes(open);

        harness.Session.ReplaceDocument(harness.Session.Document! with { Level = 42 });
        harness.WorkflowInteraction.SavePicker = _ => new SaveFilePickResult(open, false);
        harness.WorkflowInteraction.Confirm = _ => false;

        await harness.Vm.SaveAsCommand.ExecuteAsync(null);

        // With no adopter policy in the way the framework's own floor applies: an
        // unsuppressable confirmation naming the file, and nothing written when it is
        // declined.
        Assert.NotEmpty(harness.WorkflowInteraction.Confirmations);
        Assert.Equal(SaveStatus.Declined, harness.Session.LastOutcome!.Status);
        Assert.Equal(before, File.ReadAllBytes(open));
    }
}

namespace SaveEditor.Ui.Workflow;

/// <summary>Which operation is about to write.</summary>
public enum PlannedWriteKind
{
    /// <summary>A write to a path the user chose.</summary>
    SaveAs,

    /// <summary>An explicitly named overwrite of the open document.</summary>
    Overwrite,

    /// <summary>A backup being put back over the open document.</summary>
    Restore,
}

/// <summary>
/// What the framework is about to do, offered to <see cref="IWritePolicy"/> before it does it.
/// </summary>
/// <remarks>
/// Everything here is what the framework knows at the moment the destination is settled and
/// before anything destructive has happened. A policy that needs more than this is asking a
/// question about the application's own state, which the application already has.
/// </remarks>
public sealed record PlannedWrite
{
    /// <summary>Which operation is asking.</summary>
    public required PlannedWriteKind Kind { get; init; }

    /// <summary>The fully resolved destination, or the chosen path when it does not exist yet.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Whether the framework independently observed something at the destination.</summary>
    public required bool DestinationExists { get; init; }

    /// <summary>Whether the destination is the document currently open.</summary>
    public required bool IsCurrentDocument { get; init; }

    /// <summary>Whether a verified backup will be written before the replacement.</summary>
    public required bool BackupWillBeWritten { get; init; }

    /// <summary>What the framework concluded about the codec's preservation claim.</summary>
    public UnknownDataVerification UnknownData { get; init; }
}

/// <summary>A policy's answer to a <see cref="PlannedWrite"/>.</summary>
public sealed record WriteDecision
{
    /// <summary>Whether the framework may continue.</summary>
    public required bool IsAllowed { get; init; }

    /// <summary>
    /// Why not, when it may not. Reported to the caller as the outcome message.
    /// </summary>
    /// <remarks>
    /// Application-authored rather than framework-authored, and shown to the user, so it
    /// should say what the policy is rather than that a policy exists. "This editor never
    /// replaces an existing save from Save As; use Overwrite" is useful; "refused by policy"
    /// is not.
    /// </remarks>
    public string? Message { get; init; }

    /// <summary>Allows the write.</summary>
    public static WriteDecision Proceed { get; } = new() { IsAllowed = true };

    /// <summary>Refuses the write.</summary>
    /// <param name="message">What the policy is, in words a user can act on.</param>
    /// <returns>The refusal.</returns>
    public static WriteDecision Refuse(string message) => new() { IsAllowed = false, Message = message };
}

/// <summary>
/// An application-supplied gate consulted before every destructive step.
/// </summary>
/// <remarks>
/// <para>
/// The framework's own rules are the floor, not the ceiling. An application with a stricter
/// policy — the adopter that raised this refuses to let <c>Save As</c> replace an existing
/// file at all — previously had no supported way to express it: the picker was invoked inside
/// <see cref="SafeFileWorkflow{TDocument}.SaveAsAsync(TDocument, Codecs.ISaveCodec{TDocument}, OpenSaveFile{TDocument}?, IProgress{SaveProgress}?, CancellationToken)"/>,
/// so the only routes were reimplementing <see cref="Shell.IDocumentSession"/> wholesale or
/// intercepting through a custom <see cref="Interaction.IUserInteraction"/> that observes and
/// refuses picks — policy enforcement smuggled through a dialog service (finding F-15).
/// </para>
/// <para>
/// <strong>A policy can only refuse.</strong> It is consulted after the destination is
/// settled and before anything destructive happens, and its answer is either "continue" or
/// "stop, and here is why". It cannot loosen a framework rule, redirect a write, or
/// substitute a different destination: those would make the guarantees a function of
/// consumer code. An application that wants to redirect overrides the session's save entry
/// point and calls the operation it actually wants — which is what the seams on
/// <see cref="DocumentSession{TDocument}"/> are for.
/// </para>
/// <para>
/// Consulted on a <c>Save As</c> to a brand-new path too, so a policy can refuse anything.
/// </para>
/// </remarks>
public interface IWritePolicy
{
    /// <summary>Decides whether a planned write may proceed.</summary>
    /// <param name="plan">What the framework is about to do.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    /// <returns>Whether to continue, and why not if not.</returns>
    ValueTask<WriteDecision> EvaluateAsync(PlannedWrite plan, CancellationToken cancellationToken = default);
}

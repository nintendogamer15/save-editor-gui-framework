namespace SaveEditor.Ui.Workflow;

/// <summary>
/// The step a write attempt is currently performing.
/// </summary>
/// <remarks>
/// The order of the members is the order the workflow performs them, and the workflow
/// reports each one it enters. Progress is observational: nothing about correctness
/// depends on a consumer subscribing.
/// </remarks>
public enum SavePhase
{
    /// <summary>Reading the source bytes through the retained handle.</summary>
    Reading,

    /// <summary>Running codec detection over a bounded header slice.</summary>
    Detecting,

    /// <summary>Decoding the document.</summary>
    Decoding,

    /// <summary>Re-serializing the unmodified document to falsify the preservation claim.</summary>
    VerifyingPreservationClaim,

    /// <summary>Validating the document.</summary>
    Validating,

    /// <summary>Copying the original into the backup file.</summary>
    WritingBackup,

    /// <summary>Hashing the flushed backup against the change-detection baseline.</summary>
    VerifyingBackup,

    /// <summary>Serializing the document into bounded memory.</summary>
    Serializing,

    /// <summary>Decoding the serialized bytes and comparing them to the in-memory document.</summary>
    VerifyingRoundTrip,

    /// <summary>Writing the serialized bytes into the exclusively-created temporary file.</summary>
    WritingTemp,

    /// <summary>Copying mode, ACL, and extended attributes onto the temporary file.</summary>
    PreservingPermissions,

    /// <summary>Re-verifying the baseline hash immediately before the replace.</summary>
    CheckingForExternalChange,

    /// <summary>Flushing, replacing, and flushing the containing directory.</summary>
    Replacing,

    /// <summary>The operation finished.</summary>
    Completed,
}

/// <summary>One progress report from a write attempt.</summary>
/// <param name="Phase">The step being performed.</param>
/// <param name="BytesCompleted">Bytes processed in this phase, where the phase counts bytes.</param>
/// <param name="BytesTotal">Bytes the phase expects to process, or <see langword="null"/>.</param>
public readonly record struct SaveProgress(SavePhase Phase, long BytesCompleted = 0, long? BytesTotal = null);

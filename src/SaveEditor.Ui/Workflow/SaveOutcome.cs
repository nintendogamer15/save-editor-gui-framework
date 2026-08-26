namespace SaveEditor.Ui.Workflow;

/// <summary>
/// The definitive outcome of a write attempt.
/// </summary>
/// <remarks>
/// The save workflow is <strong>fail-loud</strong>: every attempt terminates in exactly
/// one of these states and the user is told which. This is deliberately the opposite of
/// <see cref="Settings.IEditorSettingsStore"/>, which is fail-soft, and the opposition is
/// the whole of finding B10 — a save that silently does not happen is the worst outcome
/// this product can produce, while a preference that silently does not persist is a minor
/// one. The two share the hardened path primitive and share no failure policy.
/// </remarks>
public enum SaveStatus
{
    /// <summary>The bytes are on disk and the replacement completed.</summary>
    Succeeded,

    /// <summary>The user declined — dismissed the picker, or refused a confirmation.</summary>
    Declined,

    /// <summary>
    /// The operation was cancelled at the workflow boundary and produced no write.
    /// </summary>
    Cancelled,

    /// <summary>The operation failed. The target is byte-identical to its pre-operation state.</summary>
    Failed,
}

/// <summary>Why a write attempt failed.</summary>
public enum SaveFailureReason
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>The path resolver refused the target, the temp path, or the backup path.</summary>
    PathRefused,

    /// <summary>The target is read-only, immutable, or on a read-only filesystem.</summary>
    /// <remarks>Reported, never cleared. See <c>PLAN.md</c> §7 step 5 and finding A12.</remarks>
    WriteProtected,

    /// <summary>Validation produced errors, which block both Save As and Overwrite.</summary>
    ValidationErrors,

    /// <summary>The bytes at the target changed between the baseline and the replace.</summary>
    ExternalChange,

    /// <summary>The all-or-nothing backup did not complete and verify.</summary>
    BackupFailed,

    /// <summary>The exclusively-created temporary file could not be produced.</summary>
    TempCreationFailed,

    /// <summary>The codec threw, exceeded the size bound, or produced unusable bytes.</summary>
    CodecFailed,

    /// <summary>The serialized bytes did not decode back to the in-memory document.</summary>
    RoundTripMismatch,

    /// <summary>The bytes read back from the temporary file are not the bytes written to it.</summary>
    TempVerificationFailed,

    /// <summary>Replacing would have produced a permission set broader than the original.</summary>
    PermissionWidening,

    /// <summary>The original permission set could not be carried onto the temporary file at all.</summary>
    PermissionCopyFailed,

    /// <summary>
    /// The destination cannot be replaced atomically, and there is no non-atomic fallback.
    /// </summary>
    AtomicReplaceUnsupported,

    /// <summary>The retained handle no longer refers to the object it was resolved to.</summary>
    IdentityChanged,

    /// <summary>Detection produced no usable codec, or the ambiguity was not resolved.</summary>
    DetectionFailed,

    /// <summary>The input exceeded a configured bound.</summary>
    TooLarge,

    /// <summary>An unexpected fault was contained at the workflow boundary.</summary>
    Unexpected,
}

/// <summary>
/// Whether the serialized bytes were decoded and compared to the document before the replace.
/// </summary>
/// <remarks>
/// Carried on the outcome because the check has a documented size limit above which it does
/// not run, and previously that produced a plain "Saved." indistinguishable from one where it
/// had run and passed. The preservation-claim check already surfaced its own skip as
/// <see cref="UnknownDataVerification.Skipped"/>; this one was invisible (finding F-6).
/// "Not checked" and "checked and fine" must never be the same report.
/// </remarks>
public enum RoundTripVerification
{
    /// <summary>The attempt never reached the round-trip stage.</summary>
    NotReached,

    /// <summary>The bytes were decoded back and matched the document in memory.</summary>
    Verified,

    /// <summary>The check did not run — switched off, or the payload is above the size limit.</summary>
    Skipped,

    /// <summary>The bytes were decoded back and did not match. The write was abandoned.</summary>
    Mismatched,
}

/// <summary>
/// The definitive, user-reportable result of one write attempt.
/// </summary>
/// <remarks>
/// A failure carries the guarantee stated in <c>PLAN.md</c> §7: <em>the bytes at the
/// target path are exactly the pre-operation bytes.</em> Not guaranteed, and not claimed
/// here: file identity, hard-link aliasing, views held through other processes' open
/// handles, and creation or change timestamps. Permissions are preserved.
/// </remarks>
public sealed record SaveOutcome
{
    /// <summary>What happened.</summary>
    public required SaveStatus Status { get; init; }

    /// <summary>Why it failed, or <see cref="SaveFailureReason.None"/>.</summary>
    public SaveFailureReason Reason { get; init; }

    /// <summary>Framework-authored explanation. Never sourced from a codec.</summary>
    public required string Message { get; init; }

    /// <summary>The path written, when one was.</summary>
    public string? Path { get; init; }

    /// <summary>Where the verified backup was written, when one was.</summary>
    public string? BackupPath { get; init; }

    /// <summary>Whether the pre-replace round-trip check ran, and what it concluded.</summary>
    public RoundTripVerification RoundTrip { get; init; }

    /// <summary>Framework-authored explanation of the round-trip verdict.</summary>
    public string RoundTripDetail { get; init; } = string.Empty;

    /// <summary>Whether the operation completed successfully.</summary>
    public bool IsSuccess => Status == SaveStatus.Succeeded;

    /// <summary>Builds a success result.</summary>
    /// <param name="path">The path written.</param>
    /// <param name="backupPath">The verified backup, if the operation made one.</param>
    /// <returns>The outcome.</returns>
    public static SaveOutcome Success(string path, string? backupPath = null) => new()
    {
        Status = SaveStatus.Succeeded,
        Message = backupPath is null
            ? "Saved. No change was detected between the check and the write."
            : "Saved. The backup was written and verified, and no change was detected between the check and the write.",
        Path = path,
        BackupPath = backupPath,
    };

    /// <summary>Builds a failure result.</summary>
    /// <param name="reason">Machine-readable cause.</param>
    /// <param name="message">Framework-authored explanation.</param>
    /// <param name="path">The target that was left untouched.</param>
    /// <returns>The outcome.</returns>
    public static SaveOutcome Failure(SaveFailureReason reason, string message, string? path = null) => new()
    {
        Status = SaveStatus.Failed,
        Reason = reason,
        Message = message,
        Path = path,
    };

    /// <summary>Builds a user-declined result.</summary>
    /// <param name="message">Framework-authored explanation.</param>
    /// <returns>The outcome.</returns>
    public static SaveOutcome Declined(string message) => new()
    {
        Status = SaveStatus.Declined,
        Message = message,
    };

    /// <summary>Builds a cancelled result.</summary>
    /// <returns>The outcome.</returns>
    /// <remarks>
    /// The wording reports the user-visible operation as cancelled without implying that
    /// third-party codec work stopped: cancellation is authoritative at the workflow
    /// boundary, not inside the codec.
    /// </remarks>
    public static SaveOutcome Cancelled() => new()
    {
        Status = SaveStatus.Cancelled,
        Message = "Cancelled. Nothing was written; any result produced after cancellation was discarded.",
    };
}

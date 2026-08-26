using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Workflow;

/// <summary>
/// Everything <see cref="SafeFileWorkflow{TDocument}"/> needs, and every bound it applies.
/// </summary>
/// <typeparam name="TDocument">The editor's in-memory document type.</typeparam>
/// <remarks>
/// The collaborators default to the framework's own implementations. They are replaceable
/// so that the workflow's abort paths can be exercised without staging a power failure, a
/// hostile local process, or a filesystem that cannot rename — not so that a consumer can
/// substitute a weaker policy. The workflow treats every one of their refusals as
/// terminal.
/// </remarks>
public sealed record SafeFileWorkflowOptions<TDocument>
{
    private readonly int _backupRetention = 10;

    /// <summary>The codecs this editor understands and the detectors that recognize them.</summary>
    public required SaveCodecRegistry<TDocument> Registry { get; init; }

    /// <summary>Where pickers, confirmations, and messages are shown.</summary>
    public required IUserInteraction Interaction { get; init; }

    /// <summary>The single entry point through which every path reaches the filesystem.</summary>
    public ISafePathResolver PathResolver { get; init; } = new SafePathResolver();

    /// <summary>Flush, replace, and directory-flush ordering.</summary>
    public IDurabilityBarrier Durability { get; init; } = new PlatformDurabilityBarrier();

    /// <summary>Permission capture, copying, and the widening gate.</summary>
    public IFilePermissionPolicy Permissions { get; init; } = new PlatformFilePermissionPolicy();

    /// <summary>Baseline capture and re-verification.</summary>
    public IExternalChangeGuard ChangeGuard { get; init; } = new ExternalChangeGuard();

    /// <summary>Temporary and backup naming.</summary>
    public IWorkflowFileNames FileNames { get; init; } = new WorkflowFileNames();

    /// <summary>
    /// Resolves detection ambiguity, or <see langword="null"/> to build one over
    /// <see cref="Interaction"/>.
    /// </summary>
    public ISaveFormatChooser? FormatChooser { get; init; }

    /// <summary>
    /// An application policy consulted before every destructive step, or
    /// <see langword="null"/> for the framework's own rules alone.
    /// </summary>
    /// <remarks>
    /// The framework's rules are a floor rather than a ceiling. A policy can refuse a write
    /// the framework would otherwise permit; it cannot permit one the framework refuses, and
    /// it cannot redirect a write to somewhere else. See <see cref="IWritePolicy"/>.
    /// </remarks>
    public IWritePolicy? WritePolicy { get; init; }

    /// <summary>
    /// Compares a decoded document against the one in memory for the pre-replace
    /// round-trip check.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="EqualityComparer{T}.Default"/>, which is exactly right for
    /// a document modelled as a record and useless for one modelled as a mutable class
    /// without an equality contract. An editor whose document type is the latter supplies a
    /// comparer, or opts the check out explicitly and says so.
    /// </remarks>
    public IEqualityComparer<TDocument> DocumentComparer { get; init; } = EqualityComparer<TDocument>.Default;

    /// <summary>Largest input the workflow will open.</summary>
    public long MaxBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Size above which the user is asked before the framework reads the file.</summary>
    public long ConfirmAboveBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Whether UNC paths and network drive letters are permitted.</summary>
    public bool AllowNonLocalPaths { get; init; }

    /// <summary>Largest number of bytes a codec may serialize before the attempt is failed.</summary>
    public long MaxSerializedBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>
    /// Whether a codec's <see cref="ISaveCodec{TDocument}.PreservesUnknownData"/> claim is
    /// falsified at open time by re-serializing the unmodified document.
    /// </summary>
    /// <remarks>
    /// This is the documented opt-out for very large saves. Turning it off returns the
    /// framework's central promise to codec self-assertion, and the status text says so.
    /// </remarks>
    public bool VerifyPreservationClaim { get; init; } = true;

    /// <summary>
    /// Whether the serialized bytes are decoded and compared to the in-memory document
    /// before the replace.
    /// </summary>
    /// <remarks>The other half of the documented opt-out for very large saves.</remarks>
    public bool VerifyRoundTripBeforeReplace { get; init; } = true;

    /// <summary>Size above which both round-trip verifications are skipped and reported as skipped.</summary>
    public long RoundTripVerificationMaxBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>How many backups of one original are kept. At least one.</summary>
    /// <remarks>
    /// Rejected below 1 rather than clamped. <see cref="Workflow.BackupRetention.Apply"/>
    /// selects everything past the newest <c>retain</c> entries, so a cap of zero selects
    /// <em>every</em> backup of that original — including the one written and hash-verified
    /// moments earlier. The overwrite then proceeded and reported a <c>BackupPath</c> pointing
    /// at a file that had just been deleted: the user was told they had a backup and did not.
    /// A cap of zero has no coherent meaning for a workflow that never writes without a
    /// backup, so it is a construction error rather than a setting (finding F-4).
    /// </remarks>
    public int BackupRetention
    {
        get => _backupRetention;
        init => _backupRetention = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(BackupRetention),
                value,
                "At least one backup must be retained. A cap of zero would delete the verified backup the overwrite is about to rely on.");
    }

    /// <summary>How old a temporary file must be before the startup sweep removes it.</summary>
    public TimeSpan TemporaryResidueMinimumAge { get; init; } = TempResidueSweeper.DefaultMinimumAge;

    /// <summary>Supplies "now" for backup naming and the residue sweep.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal PathResolutionOptions ReadResolution => new()
    {
        Mode = PathResolutionMode.OpenExisting,
        MaxBytes = MaxBytes,
        ConfirmAboveBytes = ConfirmAboveBytes,
        AllowNonLocalPaths = AllowNonLocalPaths,
        ForWriting = false,
    };

    internal PathResolutionOptions WriteResolution => new()
    {
        Mode = PathResolutionMode.OpenExisting,
        MaxBytes = MaxBytes,
        ConfirmAboveBytes = MaxBytes,
        AllowNonLocalPaths = AllowNonLocalPaths,
        ForWriting = true,
    };

    internal PathResolutionOptions CreateResolution => new()
    {
        Mode = PathResolutionMode.CreateNew,
        MaxBytes = MaxBytes,
        ConfirmAboveBytes = MaxBytes,
        AllowNonLocalPaths = AllowNonLocalPaths,
        ForWriting = true,
    };
}

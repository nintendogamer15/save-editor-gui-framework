using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SaveEditor.Ui.Workflow;

/// <summary>
/// Supplies the names the workflow creates files under.
/// </summary>
/// <remarks>
/// An interface only so that a test can pin a name it needs to pre-plant something at.
/// Production always uses <see cref="WorkflowFileNames"/>, whose names carry cryptographic
/// entropy precisely so that they cannot be pre-planted.
/// </remarks>
public interface IWorkflowFileNames
{
    /// <summary>Produces a fresh temporary file name.</summary>
    /// <returns>A file name, not a path.</returns>
    string NextTemporaryFileName();

    /// <summary>Produces a fresh backup file name for one original.</summary>
    /// <param name="originalFileName">The file being backed up, without its directory.</param>
    /// <returns>A file name, not a path.</returns>
    string NextBackupFileName(string originalFileName);
}

/// <summary>
/// The framework's naming grammar for temporary and backup files.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Entropy, not derivation.</strong> A temporary name derived from the target —
/// <c>save.sav.tmp</c> — is predictable, and a predictable name in a directory another
/// local process can write to is a place to plant a symbolic or hard link. The framework
/// would then have created its safety net through an attacker's link. Both names carry
/// 128 and 32 bits of cryptographic entropy respectively, and both are created with
/// exclusive-create semantics so that an entry already sitting there is an abort rather
/// than a retry (finding A2).
/// </para>
/// <para>
/// <strong>A Windows-safe time separator.</strong> The backup stamp uses no colon: a colon
/// in a Windows path component also names an alternate data stream, and the path guard
/// refuses it.
/// </para>
/// <para>
/// <strong>A recognizable prefix.</strong> Cleanup on handled failure cannot cover process
/// kill, out-of-memory, or power loss, each of which can leave a complete copy of the save
/// payload sitting in the user's directory. The fixed prefix is what lets a bounded
/// startup sweep recognize the framework's own residue — and only its own (finding A14).
/// </para>
/// </remarks>
public sealed class WorkflowFileNames : IWorkflowFileNames
{
    /// <summary>The fixed prefix every framework temporary file carries.</summary>
    public const string TemporaryPrefix = ".saveeditor-tmp-";

    /// <summary>The fixed suffix every framework temporary file carries.</summary>
    public const string TemporarySuffix = ".part";

    /// <summary>The fixed infix every framework backup file carries.</summary>
    public const string BackupInfix = ".saveeditor-backup.";

    /// <summary>The fixed suffix every framework backup file carries.</summary>
    public const string BackupSuffix = ".bak";

    private static readonly Regex TemporaryGrammar = new(
        @"^\.saveeditor-tmp-[0-9a-f]{32}\.part$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private static readonly Regex BackupGrammar = new(
        @"^.+\.saveeditor-backup\.[0-9]{8}T[0-9]{6}Z\.[0-9a-f]{8}\.bak$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a name source.</summary>
    /// <param name="timeProvider">Supplies the backup timestamp, or <see langword="null"/> for the system clock.</param>
    public WorkflowFileNames(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string NextTemporaryFileName() =>
        $"{TemporaryPrefix}{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}{TemporarySuffix}";

    /// <inheritdoc />
    public string NextBackupFileName(string originalFileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalFileName);

        var stamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var entropy = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

        return $"{originalFileName}{BackupInfix}{stamp}.{entropy}{BackupSuffix}";
    }

    /// <summary>Whether a file name is one the framework itself created as a temporary file.</summary>
    /// <param name="fileName">A file name without its directory.</param>
    /// <returns><see langword="true"/> when it matches the framework grammar exactly.</returns>
    /// <remarks>
    /// Prefix matching alone is not enough for a sweep that deletes. Anything else in the
    /// directory was put there by somebody else, and a residue sweep is not a licence to
    /// remove it.
    /// </remarks>
    public static bool IsFrameworkTemporaryName(string fileName) =>
        !string.IsNullOrEmpty(fileName) && TemporaryGrammar.IsMatch(fileName);

    /// <summary>Whether a file name is one the framework itself created as a backup.</summary>
    /// <param name="fileName">A file name without its directory.</param>
    /// <returns><see langword="true"/> when it matches the framework grammar exactly.</returns>
    public static bool IsFrameworkBackupName(string fileName) =>
        !string.IsNullOrEmpty(fileName) && BackupGrammar.IsMatch(fileName);

    /// <summary>Whether a backup name belongs to one particular original file.</summary>
    /// <param name="fileName">A file name without its directory.</param>
    /// <param name="originalFileName">The original the backup would belong to.</param>
    /// <returns><see langword="true"/> when the name matches the grammar and the original.</returns>
    public static bool IsBackupOf(string fileName, string originalFileName) =>
        IsFrameworkBackupName(fileName) &&
        fileName.StartsWith(originalFileName + BackupInfix, StringComparison.Ordinal);
}

namespace SaveEditor.Ui.Settings;

/// <summary>What a lazy existence check concluded about a recents entry.</summary>
public enum RecentEntryState
{
    /// <summary>Nothing has been checked. The state at rest, and the state at startup.</summary>
    Unverified,

    /// <summary>The entry was observed to exist.</summary>
    Present,

    /// <summary>
    /// The entry was observed not to exist while its container plainly did.
    /// </summary>
    /// <remarks>
    /// Only this state prunes. "The file is not there but its directory is" is a real
    /// deletion; "the whole path is unreachable" is an unplugged drive, and forgetting
    /// the user's save because a USB stick was out would be a worse failure than
    /// showing an entry that does not open.
    /// </remarks>
    ConfirmedMissing,

    /// <summary>
    /// The entry could not be checked within the time box, or its container was itself
    /// unreachable. The entry is retained.
    /// </summary>
    TemporarilyUnavailable,

    /// <summary>
    /// The entry does not name a rooted, local path and was therefore never probed.
    /// </summary>
    /// <remarks>
    /// Reported without a syscall. Probing a UNC path is the outbound SMB connection
    /// and NTLM authentication attempt that finding A3 is about, so the refusal has to
    /// precede the probe rather than result from it.
    /// </remarks>
    NotLocal,
}

/// <summary>
/// Checks whether a recents entry still exists.
/// </summary>
/// <remarks>
/// <para>
/// Injected rather than called directly so that "the framework does not touch the
/// filesystem at startup" is a property a test can prove by counting invocations,
/// instead of a claim that would also hold if the probe had been quietly moved
/// somewhere else.
/// </para>
/// <para>
/// Implementations must not throw and must honor the supplied token; the caller
/// time-boxes every call.
/// </para>
/// </remarks>
public interface IRecentEntryProbe
{
    /// <summary>Checks one entry.</summary>
    /// <param name="path">A path already screened as rooted and local.</param>
    /// <param name="cancellationToken">Cancels — and time-boxes — the check.</param>
    /// <returns>What was observed.</returns>
    ValueTask<RecentEntryState> ProbeAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// The default probe: a metadata lookup on the local filesystem.
/// </summary>
/// <remarks>
/// Existence is answered from directory metadata rather than by opening the file.
/// Opening would hydrate a cloud placeholder and would take a lock on a file the user
/// has not asked to edit, both of which are side effects a menu render must not have.
/// </remarks>
public sealed class FileSystemRecentEntryProbe : IRecentEntryProbe
{
    /// <summary>The shared instance.</summary>
    public static FileSystemRecentEntryProbe Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<RecentEntryState> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(RecentEntryState.TemporarilyUnavailable);
        }

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                return ValueTask.FromResult(RecentEntryState.Present);
            }

            var container = Path.GetDirectoryName(path);
            var containerExists = !string.IsNullOrEmpty(container) && Directory.Exists(container);

            return ValueTask.FromResult(
                containerExists ? RecentEntryState.ConfirmedMissing : RecentEntryState.TemporarilyUnavailable);
        }
        catch (Exception)
        {
            // A refusal to answer is not evidence of absence.
            return ValueTask.FromResult(RecentEntryState.TemporarilyUnavailable);
        }
    }
}

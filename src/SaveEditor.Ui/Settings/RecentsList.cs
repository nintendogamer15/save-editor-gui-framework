namespace SaveEditor.Ui.Settings;

/// <summary>
/// A recents list whose existence checking is lazy, time-boxed, and never automatic.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this type touches the filesystem until <see cref="EvaluateAsync"/> is
/// called, and <see cref="EvaluateAsync"/> is called only when an entry is about to be
/// rendered or has just been activated. An unconditional startup scan is the other half
/// of finding A3: rejecting a stored UNC path matters precisely because something would
/// otherwise have probed it, and a probe of a UNC path is an outbound SMB connection and
/// an NTLM authentication attempt before any of this code gets a say.
/// </para>
/// <para>
/// Pruning is deliberately asymmetric. Only <see cref="RecentEntryState.ConfirmedMissing"/>
/// removes an entry — the file is gone while its directory is plainly there. A path that
/// merely could not be reached, or could not be reached quickly, is retained: forgetting a
/// save because a removable drive was unplugged is a worse failure than listing an entry
/// that will not open.
/// </para>
/// <para>
/// Instances are safe to use from several threads. The list itself is guarded; the probe
/// runs outside the guard so a slow filesystem cannot block a menu render.
/// </para>
/// </remarks>
public sealed class RecentsList
{
    /// <summary>
    /// Default time box for one existence check.
    /// </summary>
    /// <remarks>
    /// Short enough that a stalled volume does not hold a menu open, long enough that a
    /// spun-down local disk usually answers.
    /// </remarks>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly List<string> _paths;
    private readonly IRecentEntryProbe _probe;
    private readonly StringComparer _comparer;

    /// <summary>Creates a recents list.</summary>
    /// <param name="paths">Initial paths, most recent first. Screened and deduplicated.</param>
    /// <param name="capacity">Largest number of entries retained.</param>
    /// <param name="probe">The existence check. Never invoked by this constructor.</param>
    /// <param name="probeTimeout">Time box for one existence check.</param>
    public RecentsList(
        IEnumerable<string?>? paths,
        int capacity,
        IRecentEntryProbe probe,
        TimeSpan probeTimeout)
        : this(paths, capacity, probe, probeTimeout, RecentPaths.Comparer)
    {
    }

    internal RecentsList(
        IEnumerable<string?>? paths,
        int capacity,
        IRecentEntryProbe probe,
        TimeSpan probeTimeout,
        StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(comparer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        if (probeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(probeTimeout),
                probeTimeout,
                "The probe time box must be positive; an unbounded existence check is what this type exists to prevent.");
        }

        _probe = probe;
        _comparer = comparer;
        Capacity = capacity;
        ProbeTimeout = probeTimeout;
        _paths = [.. RecentPaths.Normalize(paths, capacity, comparer, out _)];
    }

    /// <summary>Largest number of entries retained.</summary>
    public int Capacity { get; }

    /// <summary>Time box applied to one existence check.</summary>
    public TimeSpan ProbeTimeout { get; }

    /// <summary>
    /// The current entries, most recent first.
    /// </summary>
    /// <remarks>
    /// Reading this never probes anything. Callers that want to know whether an entry
    /// still exists ask for that explicitly, one entry at a time.
    /// </remarks>
    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_gate)
            {
                return [.. _paths];
            }
        }
    }

    /// <summary>Moves a path to the front, screening, deduplicating, and capping.</summary>
    /// <param name="path">The path just opened.</param>
    /// <returns><see langword="true"/> when the path was accepted.</returns>
    public bool Promote(string path)
    {
        if (!RecentPaths.IsStorable(path))
        {
            return false;
        }

        lock (_gate)
        {
            _paths.RemoveAll(existing => _comparer.Equals(existing, path));
            _paths.Insert(0, path);

            while (_paths.Count > Capacity)
            {
                _paths.RemoveAt(_paths.Count - 1);
            }
        }

        return true;
    }

    /// <summary>Removes a path.</summary>
    /// <param name="path">The path to remove.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    public bool Remove(string path)
    {
        lock (_gate)
        {
            return _paths.RemoveAll(existing => _comparer.Equals(existing, path)) > 0;
        }
    }

    /// <summary>
    /// Checks one entry, at the moment it is rendered or activated.
    /// </summary>
    /// <param name="path">The entry to check.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>What was observed.</returns>
    /// <remarks>
    /// A non-local path is reported as <see cref="RecentEntryState.NotLocal"/> and
    /// dropped without any syscall at all. The check is time-boxed with a linked token,
    /// so a probe that hangs on an unresponsive volume expires rather than propagating.
    /// </remarks>
    public async ValueTask<RecentEntryState> EvaluateAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!RecentPaths.IsStorable(path))
        {
            Remove(path);
            return RecentEntryState.NotLocal;
        }

        RecentEntryState observed;

        using var timeBox = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeBox.CancelAfter(ProbeTimeout);

        try
        {
            observed = await _probe.ProbeAsync(path, timeBox.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RecentEntryState.TemporarilyUnavailable;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A probe that fails has not established absence.
            return RecentEntryState.TemporarilyUnavailable;
        }

        if (observed == RecentEntryState.ConfirmedMissing)
        {
            Remove(path);
        }

        return observed;
    }
}

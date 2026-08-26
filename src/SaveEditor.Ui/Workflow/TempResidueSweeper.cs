namespace SaveEditor.Ui.Workflow;

/// <summary>What one residue sweep did.</summary>
/// <param name="Removed">Paths that were removed.</param>
/// <param name="Inspected">How many directory entries were examined.</param>
/// <param name="Skipped">
/// How many entries matched the framework grammar but were left alone — because they were
/// newer than the age threshold, or because they were links rather than regular files.
/// </param>
/// <param name="Failures">How many removals were attempted and failed.</param>
public sealed record TempSweepReport(
    IReadOnlyList<string> Removed,
    int Inspected,
    int Skipped,
    int Failures);

/// <summary>
/// Removes temporary files the framework left behind (<c>PLAN.md</c> §7 step 14, finding A14).
/// </summary>
/// <remarks>
/// <para>
/// Cleanup on handled failure covers handled failure. It does not cover a process kill, an
/// out-of-memory kill, or a power loss, any of which can leave a complete copy of the save
/// payload sitting next to the original under a name the user did not choose. This sweep
/// is the bounded answer to that: it runs at startup, over directories the framework
/// itself has written into, and removes only entries whose name matches the framework
/// temporary grammar exactly and whose last write is older than a stated age.
/// </para>
/// <para>
/// <strong>Three deliberate limits.</strong> Prefix matching alone is not sufficient — the
/// full grammar including the entropy field must match, so a user file that happens to
/// start with the same characters is not deleted. The age threshold exists so that a
/// second editor instance writing at this moment does not have its temporary file removed
/// from under it. Entries carrying a link target are counted and skipped rather than
/// removed, because unlinking through a planted name is the one thing a cleanup pass must
/// not become.
/// </para>
/// </remarks>
public static class TempResidueSweeper
{
    /// <summary>The age below which a temporary file is assumed to belong to a live operation.</summary>
    public static TimeSpan DefaultMinimumAge { get; } = TimeSpan.FromHours(6);

    /// <summary>Sweeps a set of directories.</summary>
    /// <param name="directories">Directories the framework has written into.</param>
    /// <param name="minimumAge">How old an entry must be before it is removed.</param>
    /// <param name="timeProvider">Supplies "now", or <see langword="null"/> for the system clock.</param>
    /// <param name="maximumEntriesPerDirectory">Bounds the work done in any one directory.</param>
    /// <returns>What the sweep did.</returns>
    /// <remarks>
    /// Never throws for an unreadable or missing directory. A residue sweep is
    /// housekeeping, and failing it must not fail a startup.
    /// </remarks>
    public static TempSweepReport Sweep(
        IEnumerable<string> directories,
        TimeSpan? minimumAge = null,
        TimeProvider? timeProvider = null,
        int maximumEntriesPerDirectory = 4096)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var age = minimumAge ?? DefaultMinimumAge;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;

        var removed = new List<string>();
        var inspected = 0;
        var skipped = 0;
        var failures = 0;

        foreach (var directory in directories.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFiles(directory, WorkflowFileNames.TemporaryPrefix + "*")
                    .Take(maximumEntriesPerDirectory);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                inspected++;

                try
                {
                    if (!WorkflowFileNames.IsFrameworkTemporaryName(Path.GetFileName(entry)))
                    {
                        continue;
                    }

                    var info = new FileInfo(entry);
                    if (info.LinkTarget is not null)
                    {
                        // Something planted a link under the framework's own grammar.
                        // Counted and reported; never followed and never unlinked here.
                        skipped++;
                        continue;
                    }

                    if (now - info.LastWriteTimeUtc < age)
                    {
                        skipped++;
                        continue;
                    }

                    info.Delete();
                    removed.Add(entry);
                }
                catch (Exception)
                {
                    failures++;
                }
            }
        }

        return new TempSweepReport(removed, inspected, skipped, failures);
    }
}

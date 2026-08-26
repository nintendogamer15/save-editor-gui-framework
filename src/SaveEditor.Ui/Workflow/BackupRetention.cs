namespace SaveEditor.Ui.Workflow;

/// <summary>
/// Applies the backup retention cap (<c>PLAN.md</c> §7 step 6, finding B8).
/// </summary>
/// <remarks>
/// <para>
/// Backups accumulate next to the save, one per overwrite, and without a cap a heavily
/// edited save file eventually fills the directory with copies of itself. The cap is
/// applied to the newest-first ordering of the framework's own backup grammar for one
/// specific original file.
/// </para>
/// <para>
/// <strong>Only the grammar, and only for that original.</strong> Anything else in the
/// directory was put there by somebody else — including a backup a user made by hand, or
/// one from a different save in the same folder — and a retention sweep is not a licence
/// to delete it. Ordering is by the timestamp and entropy encoded in the name rather than
/// by filesystem mtime, because mtime is the one thing an external writer can trivially
/// rewrite.
/// </para>
/// <para>
/// <strong>Name ordering cannot identify the newest backup within one second, so the
/// caller names the one that must survive.</strong> The timestamp component has
/// one-second resolution; two backups taken in the same second differ only in their
/// random entropy field, so descending name order between them is a coin flip. Without
/// <c>protect</c>, a cap of one could therefore delete the backup that was written and
/// hash-verified moments earlier and is about to be reported to the user — the same class
/// of defect as finding F-4, reached by a different route.
/// </para>
/// </remarks>
public static class BackupRetention
{
    /// <summary>Removes the oldest framework backups of one original beyond the cap.</summary>
    /// <param name="directory">The directory holding the backups.</param>
    /// <param name="originalFileName">The original file name, without its directory.</param>
    /// <param name="retain">How many backups to keep. Values below zero are treated as zero.</param>
    /// <param name="protect">
    /// A backup that must survive regardless of ordering, named by file name or full path.
    /// Counts against <paramref name="retain"/> rather than being kept in addition to it.
    /// Callers that have just written a backup pass it here: within one second, name order
    /// cannot tell which of two backups is newer.
    /// </param>
    /// <returns>The paths that were removed.</returns>
    /// <remarks>
    /// Never throws. Retention is housekeeping, and failing it must not fail a save that
    /// has already completed.
    /// </remarks>
    public static IReadOnlyList<string> Apply(
        string directory,
        string originalFileName,
        int retain,
        string? protect = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(originalFileName);

        var removed = new List<string>();
        var protectedName = string.IsNullOrEmpty(protect) ? null : Path.GetFileName(protect);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        try
        {
            var candidates = Directory
                .EnumerateFiles(directory, originalFileName + WorkflowFileNames.BackupInfix + "*")
                .Where(path => WorkflowFileNames.IsBackupOf(Path.GetFileName(path), originalFileName))

                // The protected backup sorts ahead of everything, so it is inside the cap
                // whatever its entropy field happens to be.
                .OrderByDescending(path => protectedName is not null && string.Equals(Path.GetFileName(path), protectedName, comparison))
                .ThenByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(Math.Max(0, retain))
                .ToList();

            foreach (var stale in candidates)
            {
                try
                {
                    var info = new FileInfo(stale);
                    if (info.LinkTarget is not null)
                    {
                        continue;
                    }

                    info.Delete();
                    removed.Add(stale);
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }

        return removed;
    }
}

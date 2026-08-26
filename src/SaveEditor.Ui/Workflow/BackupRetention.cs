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
/// </remarks>
public static class BackupRetention
{
    /// <summary>Removes the oldest framework backups of one original beyond the cap.</summary>
    /// <param name="directory">The directory holding the backups.</param>
    /// <param name="originalFileName">The original file name, without its directory.</param>
    /// <param name="retain">How many backups to keep. Values below zero are treated as zero.</param>
    /// <returns>The paths that were removed.</returns>
    /// <remarks>
    /// Never throws. Retention is housekeeping, and failing it must not fail a save that
    /// has already completed.
    /// </remarks>
    public static IReadOnlyList<string> Apply(string directory, string originalFileName, int retain)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(originalFileName);

        var removed = new List<string>();

        try
        {
            var candidates = Directory
                .EnumerateFiles(directory, originalFileName + WorkflowFileNames.BackupInfix + "*")
                .Where(path => WorkflowFileNames.IsBackupOf(Path.GetFileName(path), originalFileName))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
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

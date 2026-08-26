using SaveEditor.Ui.Settings;

namespace SaveEditor.Ui.Tests.Settings;

/// <summary>
/// Recents behavior: the lazy half of finding A3, and finding B7.
/// </summary>
public sealed class RecentsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Recents_DoesNotProbeFilesystemDuringStartup()
    {
        using var workspace = new SettingsWorkspace("lazy-recents");

        string[] files =
        [
            SettingsWorkspace.LocalPath("slot1.dat"),
            SettingsWorkspace.LocalPath("slot2.dat"),
            SettingsWorkspace.LocalPath("slot3.dat"),
        ];

        string[] folders = [SettingsWorkspace.LocalPath("saves")];

        workspace.PlantSettings(TamperedSettingsCorpus.Document(recentFiles: files, recentFolders: folders));

        var store = workspace.CreateStore();

        // Constructing the store is startup.
        Assert.Empty(workspace.Probe.Calls);

        var loaded = await store.LoadAsync(Token);

        // Loading is startup.
        Assert.Equal(files, loaded.RecentFiles);
        Assert.Equal(folders, loaded.RecentFolders);
        Assert.Empty(workspace.Probe.Calls);

        var recents = store.CreateFileRecents(loaded);
        var folderRecents = store.CreateFolderRecents(loaded);

        // Building the menu models is startup.
        Assert.Empty(workspace.Probe.Calls);

        // So is reading them, which is what a menu binding does.
        Assert.Equal(files, recents.Paths);
        Assert.Equal(folders, folderRecents.Paths);
        foreach (var path in recents.Paths)
        {
            Assert.NotEmpty(path);
        }

        Assert.Empty(workspace.Probe.Calls);

        // The negative above is only worth anything if the probe would in fact have been
        // called when it is supposed to be. Rendering or activating one entry calls it
        // exactly once, for exactly that entry.
        var state = await recents.EvaluateAsync(files[1], Token);

        Assert.Equal(RecentEntryState.Present, state);
        Assert.Equal([files[1]], workspace.Probe.Calls);
    }

    [Fact]
    public async Task Recents_PruneOnlyOnConfirmedAbsence()
    {
        using var workspace = new SettingsWorkspace("prune");

        var present = SettingsWorkspace.LocalPath("present.dat");
        var missing = SettingsWorkspace.LocalPath("missing.dat");
        var unreachable = SettingsWorkspace.LocalPath("unreachable.dat");

        var recents = new RecentsList(
            [present, missing, unreachable],
            EditorSettings.MaxRecentFiles,
            workspace.Probe,
            TimeSpan.FromSeconds(1));

        workspace.Probe.Result = RecentEntryState.Present;
        Assert.Equal(RecentEntryState.Present, await recents.EvaluateAsync(present, Token));
        Assert.Contains(present, recents.Paths);

        workspace.Probe.Result = RecentEntryState.TemporarilyUnavailable;
        Assert.Equal(RecentEntryState.TemporarilyUnavailable, await recents.EvaluateAsync(unreachable, Token));

        // An unplugged drive is not a deleted save.
        Assert.Contains(unreachable, recents.Paths);

        workspace.Probe.Result = RecentEntryState.ConfirmedMissing;
        Assert.Equal(RecentEntryState.ConfirmedMissing, await recents.EvaluateAsync(missing, Token));
        Assert.DoesNotContain(missing, recents.Paths);
        Assert.Equal(2, recents.Paths.Count);
    }

    [Fact]
    public async Task Recents_NeverProbeNonLocalPathsEvenWhenActivated()
    {
        using var workspace = new SettingsWorkspace("no-probe-unc");

        const string unc = @"\\attacker-host\share\slot1.dat";

        // The entry cannot even get into the list, but activation of a path supplied
        // from elsewhere — a drop, a command line, a stale menu item — still refuses
        // without a syscall.
        var recents = new RecentsList(
            [unc, SettingsWorkspace.LocalPath("slot1.dat")],
            EditorSettings.MaxRecentFiles,
            workspace.Probe,
            TimeSpan.FromSeconds(1));

        Assert.DoesNotContain(unc, recents.Paths);

        Assert.Equal(RecentEntryState.NotLocal, await recents.EvaluateAsync(unc, Token));
        Assert.Empty(workspace.Probe.Calls);

        Assert.False(recents.Promote(unc));
        Assert.DoesNotContain(unc, recents.Paths);
        Assert.Empty(workspace.Probe.Calls);
    }

    [Fact]
    public async Task Recents_ExistenceCheckIsTimeBoxed()
    {
        var probe = new HangingRecentEntryProbe();
        var path = SettingsWorkspace.LocalPath("slow.dat");

        var recents = new RecentsList(
            [path],
            EditorSettings.MaxRecentFiles,
            probe,
            TimeSpan.FromMilliseconds(50));

        var state = await recents.EvaluateAsync(path, Token);

        Assert.Equal(RecentEntryState.TemporarilyUnavailable, state);
        Assert.Equal(1, probe.CallCount);

        // Expiry is not evidence of absence, so the entry stays.
        Assert.Contains(path, recents.Paths);
    }

    [Fact]
    public async Task Recents_DeduplicatesOrdinallyOnLinuxAndIgnoreCaseOnWindows()
    {
        // The wiring: which comparison the platform actually gets.
        Assert.Same(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal,
            RecentPaths.Comparer);

        // The behavior, end to end through the store, on whichever platform is running.
        using var workspace = new SettingsWorkspace("dedup");

        var lower = SettingsWorkspace.LocalPath("save.dat");
        var upper = SettingsWorkspace.LocalPath("Save.DAT");
        var other = SettingsWorkspace.LocalPath("other.dat");

        workspace.PlantSettings(TamperedSettingsCorpus.Document(recentFiles: [lower, upper, lower, other]));

        var loaded = await workspace.CreateStore().LoadAsync(Token);

        if (OperatingSystem.IsWindows())
        {
            // One file with two spellings.
            Assert.Equal([lower, other], loaded.RecentFiles);
        }
        else
        {
            // Two files. Merging them would put an entry in the menu that opens a file
            // other than the one it names, which in a save editor is a data-loss hazard
            // rather than a cosmetic one.
            Assert.Equal([lower, upper, other], loaded.RecentFiles);
        }

        // Both semantics are exercised from either platform, so the branch this machine
        // does not run is still covered rather than merely asserted about.
        var caseInsensitive = RecentPaths.Normalize(
            [lower, upper, lower, other],
            EditorSettings.MaxRecentFiles,
            StringComparer.OrdinalIgnoreCase,
            out _);

        var ordinal = RecentPaths.Normalize(
            [lower, upper, lower, other],
            EditorSettings.MaxRecentFiles,
            StringComparer.Ordinal,
            out _);

        Assert.Equal([lower, other], caseInsensitive);
        Assert.Equal([lower, upper, other], ordinal);
    }

    [Fact]
    public void Recents_PromoteMovesToFrontDeduplicatesAndCaps()
    {
        using var workspace = new SettingsWorkspace("promote");

        var paths = Enumerable
            .Range(0, EditorSettings.MaxRecentFiles)
            .Select(i => SettingsWorkspace.LocalPath($"slot{i}.dat"))
            .ToArray();

        var recents = new RecentsList(
            paths,
            EditorSettings.MaxRecentFiles,
            workspace.Probe,
            TimeSpan.FromSeconds(1));

        Assert.True(recents.Promote(paths[^1]));
        Assert.Equal(paths[^1], recents.Paths[0]);
        Assert.Equal(EditorSettings.MaxRecentFiles, recents.Paths.Count);

        var fresh = SettingsWorkspace.LocalPath("fresh.dat");
        Assert.True(recents.Promote(fresh));
        Assert.Equal(fresh, recents.Paths[0]);
        Assert.Equal(EditorSettings.MaxRecentFiles, recents.Paths.Count);

        // The list was already full and slot9 had been moved to the front, so the entry
        // pushed off the end is the one that was least recently used: slot8.
        Assert.DoesNotContain(paths[^2], recents.Paths);
        Assert.Contains(paths[0], recents.Paths);

        // Nothing above touched the filesystem.
        Assert.Empty(workspace.Probe.Calls);
    }

    [Fact]
    public void Recents_RejectsAnUnboundedProbeTimeBox()
    {
        using var workspace = new SettingsWorkspace("timebox");

        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentsList(
            [],
            EditorSettings.MaxRecentFiles,
            workspace.Probe,
            TimeSpan.Zero));

        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentsList(
            [],
            EditorSettings.MaxRecentFiles,
            workspace.Probe,
            Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task Recents_DefaultProbeDistinguishesDeletionFromUnreachability()
    {
        using var workspace = new SettingsWorkspace("real-probe");

        var directory = Path.Combine(workspace.Root, "saves");
        Directory.CreateDirectory(directory);

        var present = Path.Combine(directory, "present.dat");
        await File.WriteAllTextAsync(present, "x", Token);

        var deleted = Path.Combine(directory, "deleted.dat");
        var unreachable = Path.Combine(workspace.Root, "no-such-directory", "slot.dat");

        var probe = FileSystemRecentEntryProbe.Instance;

        Assert.Equal(RecentEntryState.Present, await probe.ProbeAsync(present, Token));
        Assert.Equal(RecentEntryState.Present, await probe.ProbeAsync(directory, Token));
        Assert.Equal(RecentEntryState.ConfirmedMissing, await probe.ProbeAsync(deleted, Token));
        Assert.Equal(RecentEntryState.TemporarilyUnavailable, await probe.ProbeAsync(unreachable, Token));
    }
}

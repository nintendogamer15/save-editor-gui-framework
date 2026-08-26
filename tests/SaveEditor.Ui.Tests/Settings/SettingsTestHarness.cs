using System.Text;
using SaveEditor.Ui.Settings;

namespace SaveEditor.Ui.Tests.Settings;

/// <summary>
/// A per-test settings directory under the system temporary path.
/// </summary>
/// <remarks>
/// Never the repository, and never the real <c>LocalApplicationData</c>. A settings
/// test that wrote into the developer's own profile would be indistinguishable from
/// the bug it is meant to catch.
/// </remarks>
internal sealed class SettingsWorkspace : IDisposable
{
    public SettingsWorkspace(string label)
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "SaveEditorSettingsTests",
            $"{label}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(Root);
        EditorApplicationId = EditorApplicationId.Parse("SaveEditorTests");
        Probe = new CountingRecentEntryProbe();
    }

    /// <summary>The stand-in for <c>LocalApplicationData</c>.</summary>
    public string Root { get; }

    public EditorApplicationId EditorApplicationId { get; }

    public CountingRecentEntryProbe Probe { get; }

    /// <summary>The directory the store will use.</summary>
    public string SettingsDirectory => Path.Combine(Root, EditorApplicationId.Value);

    /// <summary>The file the store will read and write.</summary>
    public string SettingsFilePath => Path.Combine(SettingsDirectory, EditorSettingsStore.SettingsFileName);

    public EditorSettingsStoreOptions Options(IScreenBoundsSource? screens = null) =>
        new()
        {
            BaseDirectory = Root,
            RecentProbe = Probe,
            ScreenBounds = screens ?? UnknownScreenBoundsSource.Instance,
        };

    public EditorSettingsStore CreateStore(EditorSettingsStoreOptions? options = null) =>
        new(EditorApplicationId, options ?? Options());

    /// <summary>Plants raw bytes at the settings path, bypassing the store entirely.</summary>
    public void PlantSettings(string json)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllBytes(SettingsFilePath, Encoding.UTF8.GetBytes(json));
    }

    public IReadOnlyList<string> BackupFiles() =>
        Directory.Exists(SettingsDirectory)
            ? [.. Directory.EnumerateFiles(SettingsDirectory, "settings.corrupt.*.json").Order(StringComparer.Ordinal)]
            : [];

    /// <summary>A rooted, local path that passes screening on the running platform.</summary>
    public static string LocalPath(string leaf) =>
        OperatingSystem.IsWindows()
            ? $@"C:\SaveEditorFixtures\{leaf}"
            : $"/var/lib/save-editor-fixtures/{leaf}";

    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// A probe that records every call and never touches the filesystem.
/// </summary>
/// <remarks>
/// The point of counting rather than stubbing is that the startup test has to prove a
/// negative. Asserting that recents "look right" after a load would pass equally well
/// if the framework had probed every entry on the way, which is the behavior the test
/// exists to forbid.
/// </remarks>
internal sealed class CountingRecentEntryProbe : IRecentEntryProbe
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];

    public RecentEntryState Result { get; set; } = RecentEntryState.Present;

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    public ValueTask<RecentEntryState> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add(path);
        }

        return ValueTask.FromResult(Result);
    }
}

/// <summary>A probe that never returns until its time box expires.</summary>
internal sealed class HangingRecentEntryProbe : IRecentEntryProbe
{
    public int CallCount { get; private set; }

    public async ValueTask<RecentEntryState> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        CallCount++;
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return RecentEntryState.Present;
    }
}

/// <summary>A bounds source that throws, standing in for a display subsystem in a bad state.</summary>
internal sealed class ThrowingScreenBoundsSource : IScreenBoundsSource
{
    public IReadOnlyList<ScreenArea> GetAvailableAreas() =>
        throw new InvalidOperationException("No display connection.");
}

/// <summary>
/// Assertions over <see cref="EditorSettings"/>.
/// </summary>
/// <remarks>
/// Written out member by member rather than as one record comparison on purpose.
/// <see cref="EditorSettings"/> is a record whose recents members are typed
/// <see cref="IReadOnlyList{T}"/>, so the compiler-generated equality compares those
/// members by reference: two instances holding equal-but-distinct lists are not equal.
/// Comparing whole records here would make these tests pass or fail on an unrelated
/// implementation detail of how a list happened to be allocated.
/// </remarks>
internal static class SettingsAssert
{
    public static void IsDefault(EditorSettings actual)
    {
        var expected = new EditorSettings();

        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Theme, actual.Theme);
        Assert.Equal(expected.Accent, actual.Accent);
        Assert.Empty(actual.RecentFiles);
        Assert.Empty(actual.RecentFolders);
        Assert.Null(actual.LastSectionKey);
        Assert.Null(actual.WindowWidth);
        Assert.Null(actual.WindowHeight);
    }

    public static void Equivalent(EditorSettings expected, EditorSettings actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Theme, actual.Theme);
        Assert.Equal(expected.Accent, actual.Accent);
        Assert.Equal(expected.RecentFiles, actual.RecentFiles);
        Assert.Equal(expected.RecentFolders, actual.RecentFolders);
        Assert.Equal(expected.LastSectionKey, actual.LastSectionKey);
        Assert.Equal(expected.WindowWidth, actual.WindowWidth);
        Assert.Equal(expected.WindowHeight, actual.WindowHeight);
    }
}

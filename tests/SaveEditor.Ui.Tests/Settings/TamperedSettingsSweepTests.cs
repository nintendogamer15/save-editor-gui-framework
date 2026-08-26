using SaveEditor.Ui.Settings;

namespace SaveEditor.Ui.Tests.Settings;

/// <summary>
/// Runs the whole tampered corpus through the store and asserts the invariants that
/// must hold no matter which variant was planted.
/// </summary>
/// <remarks>
/// The named tests in §12 each pin one behavior. This one exists for the case the named
/// tests cannot cover by construction: a variant nobody thought about specifically must
/// still not throw, must not block startup, must not leave the store in a state where
/// the next launch fails the same way, and must not put a value into memory that the
/// framework would refuse if it were reading it back.
/// </remarks>
public sealed class TamperedSettingsSweepTests
{
    public static TheoryData<string> VariantNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var variant in TamperedSettingsCorpus.All)
            {
                data.Add(variant.Name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(VariantNames))]
    public async Task Settings_SurvivesEveryTamperedVariant(string variantName)
    {
        var variant = TamperedSettingsCorpus.All.Single(candidate => candidate.Name == variantName);
        var token = TestContext.Current.CancellationToken;

        // The variant name is deliberately kept out of the directory path: the path
        // guard refuses any path containing "GLOBALROOT", and a fixture directory named
        // after that variant would fail for a reason that has nothing to do with the
        // file's contents.
        using var workspace = new SettingsWorkspace("sweep");
        workspace.PlantSettings(variant.Json);

        var screens = new FixedScreenBoundsSource(new ScreenArea(1920, 1080));
        var options = workspace.Options(screens);

        var store = new EditorSettingsStore(workspace.ApplicationId, options);

        Assert.True(store.IsPersistent, $"{variant.Why} A tampered file must not cost the user persistence.");

        var loaded = await store.LoadAsync(token);

        AssertUsable(loaded, screens, variant);

        // Startup was not blocked and nothing probed the filesystem on behalf of a
        // recents entry.
        Assert.Empty(workspace.Probe.Calls);

        // Whatever the store decided, the settings directory is now in a state the next
        // launch reads cleanly. A file that fails the same way every launch would be a
        // fail-soft claim that is only true the first time.
        var second = workspace.CreateStore();
        var reloaded = await second.LoadAsync(token);

        AssertUsable(reloaded, screens, variant);

        Assert.NotEqual(SettingsRejection.Unreadable, second.Status!.Rejection);

        // And the values that did survive round-trip without being refused on the way
        // back in.
        await second.SaveAsync(reloaded, token);

        var third = workspace.CreateStore();
        var final = await third.LoadAsync(token);

        AssertUsable(final, screens, variant);
        Assert.NotEqual(SettingsLoadOutcome.Replaced, third.Status!.Outcome);
    }

    private static void AssertUsable(EditorSettings settings, IScreenBoundsSource screens, TamperedSettings variant)
    {
        var because = $"variant {variant.Name}: {variant.Why}";

        Assert.Equal(EditorSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(Enum.IsDefined(settings.Theme), because);

        if (settings.Accent is { } accent)
        {
            Assert.True(Enum.IsDefined(accent), because);
        }

        Assert.True(settings.RecentFiles.Count <= EditorSettings.MaxRecentFiles, because);
        Assert.True(settings.RecentFolders.Count <= EditorSettings.MaxRecentFolders, because);

        foreach (var path in settings.RecentFiles.Concat(settings.RecentFolders))
        {
            Assert.True(RecentPaths.IsStorable(path), $"{because}: '{path}' reached memory.");
        }

        Assert.Equal(
            settings.RecentFiles.Count,
            settings.RecentFiles.Distinct(RecentPaths.Comparer).Count());

        if (settings.LastSectionKey is { } key)
        {
            Assert.InRange(key.Length, 1, 128);
        }

        // Window size is either absent or a size a real window could have on a real
        // screen. The host never has to re-check.
        Assert.Equal(settings.WindowWidth is null, settings.WindowHeight is null);

        if (settings.WindowWidth is { } width)
        {
            var largest = screens.GetAvailableAreas();
            Assert.InRange(width, 1d, largest.Max(area => area.Width));
            Assert.InRange(settings.WindowHeight!.Value, 1d, largest.Max(area => area.Height));
        }
    }
}

using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SaveEditor.Ui.Settings;

namespace SaveEditor.Ui.Tests.Settings;

/// <summary>
/// <c>settings.json</c> as a trust boundary: findings A3, A10, and B9.
/// </summary>
public sealed class SettingsTrustBoundaryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- A3: paths

    [Fact]
    public async Task Settings_RejectsUncAndDeviceNamespacePaths()
    {
        using var workspace = new SettingsWorkspace("unc");

        var good = SettingsWorkspace.LocalPath("slot1.dat");

        string[] hostile =
        [
            @"\\attacker-host\share\slot1.dat",
            @"\\attacker-host\share",
            @"\\?\C:\saves\slot1.dat",
            @"\\.\PhysicalDrive0",
            @"\\.\C:",
            @"\\?\UNC\attacker-host\share\slot1.dat",
            @"\\?\GLOBALROOT\Device\HarddiskVolume2\slot1.dat",
            @"\\127.0.0.1\c$\slot1.dat",
        ];

        // Screening is syntactic and platform-independent. The file is expected to
        // arrive from a roaming profile, so the same bytes are read on both platforms
        // and the answer must not depend on which one is reading.
        foreach (var path in hostile)
        {
            Assert.False(RecentPaths.IsStorable(path), $"'{path}' must never be storable.");
            Assert.NotNull(RecentPaths.Screen(path));
        }

        Assert.True(RecentPaths.IsStorable(good));

        workspace.PlantSettings(TamperedSettingsCorpus.Document(
            recentFiles: [.. hostile, good],
            recentFolders: [@"\\attacker-host\share\saves", good]));

        var store = workspace.CreateStore();
        var loaded = await store.LoadAsync(Token);

        Assert.Equal([good], loaded.RecentFiles);
        Assert.Equal([good], loaded.RecentFolders);

        // The refusal happened without asking the filesystem — or the network — about
        // any of them. That ordering is the finding: probing a UNC path opens an
        // outbound SMB connection and offers an NTLM handshake before any check runs.
        Assert.Empty(workspace.Probe.Calls);

        Assert.True(store.TryTakeAnnouncement(out var announcement));
        Assert.Equal(SettingsLoadOutcome.Sanitized, announcement.Outcome);

        // And a non-local path cannot be re-introduced through the write path either.
        await store.SaveAsync(loaded with { RecentFiles = [@"\\attacker-host\share\slot1.dat", good] }, Token);

        var written = await File.ReadAllTextAsync(workspace.SettingsFilePath, Token);
        Assert.DoesNotContain("attacker-host", written, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("slot1.dat", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_RejectsRelativeTraversalAndControlCharacterPaths()
    {
        using var workspace = new SettingsWorkspace("path-shapes");

        var good = SettingsWorkspace.LocalPath("slot1.dat");

        string[] hostile =
        [
            "slot1.dat",
            "./slot1.dat",
            OperatingSystem.IsWindows() ? @"C:\saves\..\..\Windows\win.ini" : "/var/saves/../../etc/shadow",
            SettingsWorkspace.LocalPath("slot\u0007.dat"),
            SettingsWorkspace.LocalPath("slot\u001b[2K.dat"),
            SettingsWorkspace.LocalPath("slot\u0085.dat"),
            string.Empty,
            "   ",
        ];

        foreach (var path in hostile)
        {
            Assert.False(RecentPaths.IsStorable(path), $"'{path.Replace('\u0007', '?')}' must never be storable.");
        }

        workspace.PlantSettings(TamperedSettingsCorpus.Document(recentFiles: [.. hostile, good]));

        var store = workspace.CreateStore();
        var loaded = await store.LoadAsync(Token);

        Assert.Equal([good], loaded.RecentFiles);
        Assert.Equal(SettingsLoadOutcome.Sanitized, store.Status!.Outcome);
        Assert.Empty(workspace.Probe.Calls);

        // A path long enough to trip the structural string cap is a different event: it
        // routes the whole file to the malformed path rather than dropping one entry.
        // Asserted separately so the two tiers do not get conflated here.
        Assert.False(RecentPaths.IsStorable(new string('a', RecentPaths.MaxPathLength + 1)));
    }

    [Fact]
    public async Task Settings_KeepsBidiMarkedPathsVerbatimRatherThanRewritingThem()
    {
        // A right-to-left override in a filename is a display problem, and §10 assigns
        // it to the shared path formatter (finding A13, phase P2). The store's job is
        // the opposite one: never silently rewrite a stored path, because a recents
        // entry that resolves to a file other than the one the user believes is the
        // data-loss hazard this product exists not to cause.
        using var workspace = new SettingsWorkspace("bidi");

        var spoofed = SettingsWorkspace.LocalPath("harmless\u202Etad.exe");
        workspace.PlantSettings(TamperedSettingsCorpus.Document(recentFiles: [spoofed]));

        var loaded = await workspace.CreateStore().LoadAsync(Token);

        var entry = Assert.Single(loaded.RecentFiles);
        Assert.Equal(spoofed, entry, StringComparer.Ordinal);
        Assert.Contains('\u202E', entry);
        Assert.Empty(workspace.Probe.Calls);
    }

    // ------------------------------------------------- A10: bounded deserialization

    [Fact]
    public async Task Settings_RejectsPolymorphicTypeDiscriminators()
    {
        using var workspace = new SettingsWorkspace("polymorphic");

        TamperedSettings[] hostile =
        [
            TamperedSettingsCorpus.TypeDiscriminator,
            TamperedSettingsCorpus.EscapedTypeDiscriminator,
            TamperedSettingsCorpus.ReferenceMetadata,
        ];

        foreach (var variant in hostile)
        {
            workspace.PlantSettings(variant.Json);

            var store = workspace.CreateStore();
            var loaded = await store.LoadAsync(Token);

            SettingsAssert.IsDefault(loaded);
            Assert.NotNull(store.Status);
            Assert.Equal(SettingsRejection.TypeDiscriminator, store.Status.Rejection);
            Assert.Equal(SettingsLoadOutcome.Replaced, store.Status.Outcome);

            // Nothing in the file influenced the theme either: the discriminator variant
            // also asked for Light, and defaults came back.
            Assert.Equal(ThemeMode.Dark, loaded.Theme);
        }

        // The behavior above is a consequence of the contract below, which is what
        // actually forbids polymorphic resolution: one sealed POCO, reachable only
        // through a source-generated context, with no polymorphism configured and no
        // member a discriminator could select.
        JsonTypeInfo info = SettingsJsonContext.Default.SettingsDocument;

        Assert.Null(info.PolymorphismOptions);
        Assert.Equal(JsonUnmappedMemberHandling.Disallow, info.UnmappedMemberHandling);
        Assert.Same(SettingsJsonContext.Default, SettingsJsonContext.Default.Options.TypeInfoResolver);
        Assert.Null(SettingsJsonContext.Default.Options.ReferenceHandler);

        var document = typeof(SettingsDocument);
        Assert.True(document.IsSealed, "The wire type must be sealed.");

        foreach (var property in document.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var element = type.IsArray ? type.GetElementType()! : type;
            element = Nullable.GetUnderlyingType(element) ?? element;

            Assert.True(
                element.IsPrimitive || element == typeof(string),
                $"{property.Name} is typed {element}. Only primitives and strings may appear on the wire type; " +
                "anything abstract, interface-typed, or object-typed is a slot a discriminator could fill.");
        }
    }

    [Fact]
    public async Task Settings_EnforcesSizeDepthAndCountCapsOnRead()
    {
        // Size, before parsing.
        await AssertRejected(
            "oversized",
            TamperedSettingsCorpus.OversizedFile.Json,
            SettingsRejection.FileTooLarge);

        // Depth.
        await AssertRejected(
            "deep",
            TamperedSettingsCorpus.DeeplyNestedDocument.Json,
            SettingsRejection.DepthExceeded);

        // String length.
        await AssertRejected(
            "long-string",
            TamperedSettingsCorpus.OverlongString.Json,
            SettingsRejection.StringTooLong);

        // A hundred thousand recents. With default limits the size gate catches it
        // first, which is the correct ordering — the cheapest check runs earliest.
        await AssertRejected(
            "many-recents-default",
            TamperedSettingsCorpus.HundredThousandRecents.Json,
            SettingsRejection.FileTooLarge);

        // With the size gate deliberately lifted, the structural array cap is what
        // stops it, and it stops it during the scan rather than after a hundred
        // thousand strings have been materialized and then truncated to ten.
        using (var workspace = new SettingsWorkspace("many-recents"))
        {
            workspace.PlantSettings(TamperedSettingsCorpus.HundredThousandRecents.Json);

            var store = workspace.CreateStore(workspace.Options() with { MaxFileBytes = 32L * 1024 * 1024 });
            var loaded = await store.LoadAsync(Token);

            Assert.Empty(loaded.RecentFiles);
            Assert.NotNull(store.Status);
            Assert.Equal(SettingsRejection.ArrayTooLong, store.Status.Rejection);
        }

        // The product cap is separate from the structural one. Eleven entries is an
        // ordinary overlong list: it truncates to ten and the rest of the file survives.
        using (var workspace = new SettingsWorkspace("eleven-recents"))
        {
            var paths = Enumerable
                .Range(0, EditorSettings.MaxRecentFiles + 1)
                .Select(i => SettingsWorkspace.LocalPath($"slot{i}.dat"))
                .ToArray();

            workspace.PlantSettings(TamperedSettingsCorpus.Document(
                theme: "\"Light\"",
                recentFiles: paths,
                recentFolders: paths));

            var loaded = await workspace.CreateStore().LoadAsync(Token);

            Assert.Equal(EditorSettings.MaxRecentFiles, loaded.RecentFiles.Count);
            Assert.Equal(EditorSettings.MaxRecentFolders, loaded.RecentFolders.Count);
            Assert.Equal(paths[0], loaded.RecentFiles[0]);
            Assert.Equal(ThemeMode.Light, loaded.Theme);
        }
    }

    [Fact]
    public async Task Settings_ClampsWindowSizeToScreenBounds()
    {
        var screens = new FixedScreenBoundsSource(new ScreenArea(1920, 1080), new ScreenArea(1280, 1024));

        // Larger than every attached screen: clamped down to the largest one.
        var clamped = await LoadWindow("oversize", screens, "5000", "4000");
        Assert.Equal(1920d, clamped.Width);
        Assert.Equal(1080d, clamped.Height);

        // Smaller than a usable window: clamped up.
        var tiny = await LoadWindow("tiny", screens, "12", "8");
        Assert.Equal(320d, tiny.Width);
        Assert.Equal(240d, tiny.Height);

        // Plausible and on-screen: untouched.
        var kept = await LoadWindow("kept", screens, "1024", "768");
        Assert.Equal(1024d, kept.Width);
        Assert.Equal(768d, kept.Height);

        // Values no window ever had are refused outright rather than clamped, so the
        // host chooses its own size instead of inheriting a laundered one.
        foreach (var (width, height) in new[]
                 {
                     ("-1920", "-1080"),
                     ("0", "0"),
                     ("2147483647", "2147483647"),
                     ("-0.0001", "600"),
                     ("1024", "0"),
                     ("100000", "100000"),
                 })
        {
            var rejected = await LoadWindow($"reject-{width}-{height}", screens, width, height);
            Assert.Null(rejected.Width);
            Assert.Null(rejected.Height);
        }

        // A half-recorded size is not a size.
        using (var workspace = new SettingsWorkspace("half"))
        {
            workspace.PlantSettings(TamperedSettingsCorpus.Document(windowWidth: "1024"));
            var loaded = await workspace.CreateStore(workspace.Options(screens)).LoadAsync(Token);
            Assert.Null(loaded.WindowWidth);
            Assert.Null(loaded.WindowHeight);
        }

        // A bounds source that cannot answer, or that throws, must not become a way to
        // push an implausible extent through: the absolute range still applies.
        foreach (IScreenBoundsSource source in new IScreenBoundsSource[]
                 {
                     UnknownScreenBoundsSource.Instance,
                     new ThrowingScreenBoundsSource(),
                     new FixedScreenBoundsSource(),
                 })
        {
            var withoutScreens = await LoadWindow($"no-screens-{source.GetType().Name}", source, "2147483647", "2147483647");
            Assert.Null(withoutScreens.Width);

            var plausible = await LoadWindow($"no-screens-ok-{source.GetType().Name}", source, "1600", "900");
            Assert.Equal(1600d, plausible.Width);
            Assert.Equal(900d, plausible.Height);
        }

        // Whatever comes back is inside the screen it will be shown on.
        foreach (var candidate in new[] { "1", "319", "321", "1919", "1921", "32767" })
        {
            var result = await LoadWindow($"sweep-{candidate}", screens, candidate, candidate);

            if (result.Width is { } w)
            {
                Assert.InRange(w, 320d, 1920d);
                Assert.InRange(result.Height!.Value, 240d, 1080d);
            }
        }
    }

    [Fact]
    public async Task Settings_UnknownSchemaVersionRoutesToMalformedPath()
    {
        // Every one of these also asks for the Light theme. If any of them reached a
        // migrator, Light would come back.
        string[] versions = ["999", "2", "0", "-1", "-2147483648", "2147483647"];

        foreach (var version in versions)
        {
            using var workspace = new SettingsWorkspace($"schema-{version.Replace('-', 'n')}");
            workspace.PlantSettings(TamperedSettingsCorpus.Document(schemaVersion: version, theme: "\"Light\""));

            var store = workspace.CreateStore();
            var loaded = await store.LoadAsync(Token);

            Assert.Equal(ThemeMode.Dark, loaded.Theme);
            Assert.Equal(EditorSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.NotNull(store.Status);
            Assert.Equal(SettingsRejection.UnknownSchemaVersion, store.Status.Rejection);
            Assert.Equal(SettingsLoadOutcome.Replaced, store.Status.Outcome);
            Assert.Single(workspace.BackupFiles());
        }

        // A missing version is unknown, not "assume current".
        using (var workspace = new SettingsWorkspace("schema-missing"))
        {
            workspace.PlantSettings(TamperedSettingsCorpus.MissingSchemaVersion.Json);

            var store = workspace.CreateStore();
            var loaded = await store.LoadAsync(Token);

            Assert.Equal(ThemeMode.Dark, loaded.Theme);
            Assert.Equal(SettingsRejection.UnknownSchemaVersion, store.Status!.Rejection);
        }

        // The known version still works, so the test above is not passing by rejecting
        // everything.
        using (var workspace = new SettingsWorkspace("schema-current"))
        {
            workspace.PlantSettings(TamperedSettingsCorpus.Document(
                schemaVersion: EditorSettings.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture),
                theme: "\"Light\""));

            var store = workspace.CreateStore();
            var loaded = await store.LoadAsync(Token);

            Assert.Equal(ThemeMode.Light, loaded.Theme);
            Assert.Equal(SettingsLoadOutcome.Loaded, store.Status!.Outcome);
            Assert.Empty(workspace.BackupFiles());
        }
    }

    // ------------------------------------------------------------- B9: backup safety

    [Fact]
    public async Task Settings_SecondMalformedStartupPreservesFirstBackup()
    {
        using var workspace = new SettingsWorkspace("backups");

        const string first = "{\"schemaVersion\":1,\"theme\":\"Light\",\"trailing\":";
        const string second = "{\"schemaVersion\":4242}";

        workspace.PlantSettings(first);
        var storeA = workspace.CreateStore();
        await storeA.LoadAsync(Token);

        var afterFirst = workspace.BackupFiles();
        var firstBackup = Assert.Single(afterFirst);
        Assert.Equal(first, await File.ReadAllTextAsync(firstBackup, Token));

        // The malformed file was replaced, so a well-formed default now sits there.
        var repaired = await workspace.CreateStore().LoadAsync(Token);
        SettingsAssert.IsDefault(repaired);
        Assert.Single(workspace.BackupFiles());

        workspace.PlantSettings(second);
        var storeB = workspace.CreateStore();
        await storeB.LoadAsync(Token);

        var afterSecond = workspace.BackupFiles();
        Assert.Equal(2, afterSecond.Count);

        // The first backup is byte-identical to what it was. The disambiguating
        // component is what makes that true even inside the same second: a fixed
        // "settings.bak" would have been overwritten here, destroying the only copy of
        // the first failure at the exact moment a second failure made it interesting.
        Assert.Equal(first, await File.ReadAllTextAsync(firstBackup, Token));
        Assert.Contains(second, afterSecond.Select(File.ReadAllText), StringComparer.Ordinal);
        Assert.Equal(2, afterSecond.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Settings_BackupRetentionIsBoundedAndAppliesOnlyToTheFrameworkGrammar()
    {
        using var workspace = new SettingsWorkspace("retention");

        var store = workspace.CreateStore();

        // Not the framework's grammar. It matches the search pattern but not the name
        // shape, and a retention sweep is not a licence to delete somebody else's file.
        Directory.CreateDirectory(workspace.SettingsDirectory);
        var foreign = Path.Combine(workspace.SettingsDirectory, "settings.corrupt.notes.json");
        await File.WriteAllTextAsync(foreign, "not ours", Token);

        for (var i = 0; i < 9; i++)
        {
            workspace.PlantSettings($"{{\"schemaVersion\":{9000 + i},\"theme\":\"Light\"}}");
            await workspace.CreateStore().LoadAsync(Token);
        }

        var ours = workspace.BackupFiles()
            .Where(path => Path.GetFileName(path) != "settings.corrupt.notes.json")
            .ToList();

        Assert.InRange(ours.Count, 1, 5);
        Assert.True(File.Exists(foreign), "A file outside the framework's backup grammar must survive retention.");
        Assert.Equal("not ours", await File.ReadAllTextAsync(foreign, Token));
        Assert.True(store.IsPersistent);
    }

    // ---------------------------------------------------------------- fail-soft

    [Fact]
    public async Task Settings_UnwritableDirectoryRunsOnDefaultsAndSaysSoOnce()
    {
        using var workspace = new SettingsWorkspace("unwritable");

        // A file where the settings directory should be. Creating the directory is then
        // impossible for a reason that needs no permissions fixture and behaves the same
        // on both platforms.
        Directory.CreateDirectory(workspace.Root);
        await File.WriteAllTextAsync(workspace.SettingsDirectory, "occupied", Token);

        var store = workspace.CreateStore();

        Assert.False(store.IsPersistent);

        Assert.True(store.TryTakeAnnouncement(out var announcement));
        Assert.Equal(SettingsLoadOutcome.NotPersistent, announcement.Outcome);
        Assert.Contains("in-memory defaults", announcement.Message, StringComparison.Ordinal);

        // Said once, not on every subsequent interaction.
        Assert.False(store.TryTakeAnnouncement(out _));

        // And the editor still runs.
        var loaded = await store.LoadAsync(Token);
        SettingsAssert.IsDefault(loaded);

        await store.SaveAsync(loaded with { Theme = ThemeMode.Light }, Token);
        Assert.False(store.IsPersistent);
    }

    [Fact]
    public async Task Settings_RoundTripsThroughAWriteAndReadUnchanged()
    {
        using var workspace = new SettingsWorkspace("round-trip");

        var screens = new FixedScreenBoundsSource(new ScreenArea(2560, 1440));
        var store = workspace.CreateStore(workspace.Options(screens));

        var written = new EditorSettings
        {
            Theme = ThemeMode.Light,
            Accent = CatppuccinAccent.Teal,
            RecentFiles = [SettingsWorkspace.LocalPath("a.dat"), SettingsWorkspace.LocalPath("b.dat")],
            RecentFolders = [SettingsWorkspace.LocalPath("saves")],
            LastSectionKey = "inventory",
            WindowWidth = 1600,
            WindowHeight = 900,
        };

        await store.SaveAsync(written, Token);

        var reloaded = await workspace.CreateStore(workspace.Options(screens)).LoadAsync(Token);

        SettingsAssert.Equivalent(written, reloaded);
        Assert.Empty(workspace.BackupFiles());
    }

    [Fact]
    public async Task Settings_AbsentFileIsNotAFailure()
    {
        using var workspace = new SettingsWorkspace("absent");

        var store = workspace.CreateStore();
        var loaded = await store.LoadAsync(Token);

        SettingsAssert.IsDefault(loaded);
        Assert.Equal(SettingsLoadOutcome.NoStoredSettings, store.Status!.Outcome);
        Assert.False(store.TryTakeAnnouncement(out _));
        Assert.Empty(workspace.BackupFiles());
    }

    [Fact]
    public async Task Settings_NonRegularOrLinkedSettingsFileIsLeftUntouched()
    {
        using var workspace = new SettingsWorkspace("fifo");

        Directory.CreateDirectory(workspace.SettingsDirectory);

        if (OperatingSystem.IsWindows())
        {
            // A directory standing where the file should be is the portable stand-in:
            // it is a non-regular target that needs no privilege to create.
            Directory.CreateDirectory(workspace.SettingsFilePath);
        }
        else
        {
            var failure = Io.PlatformFixtures.TryCreateFifo(workspace.SettingsFilePath);
            Assert.SkipWhen(failure is not null, $"Could not create a FIFO fixture: {failure}");
        }

        var store = workspace.CreateStore();
        var loaded = await store.LoadAsync(Token);

        SettingsAssert.IsDefault(loaded);
        Assert.Equal(SettingsLoadOutcome.Unreadable, store.Status!.Outcome);

        // Nothing was deleted and nothing was copied: the framework does not destroy
        // what it could not identify.
        Assert.Empty(workspace.BackupFiles());

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(workspace.SettingsFilePath));
            File.Delete(workspace.SettingsFilePath);
        }
        else
        {
            Assert.True(Directory.Exists(workspace.SettingsFilePath));
            Directory.Delete(workspace.SettingsFilePath);
        }
    }

    [Fact]
    public async Task Settings_HardLinkedSettingsFileStillLoads()
    {
        // The resolver reports hard-link aliasing as a condition needing confirmation,
        // because replacing an aliased file's contents changes every alias. Reading is
        // not that operation: the bytes are the same bytes whatever else points at them,
        // and a settings load must not stall on a prompt. The write path does not write
        // through the alias either — it replaces the directory entry.
        using var workspace = new SettingsWorkspace("hardlink");

        workspace.PlantSettings(TamperedSettingsCorpus.Document(theme: "\"Light\""));

        var alias = Path.Combine(workspace.SettingsDirectory, "settings.alias.json");
        var failure = Io.PlatformFixtures.TryCreateHardLink(alias, workspace.SettingsFilePath);
        Assert.SkipWhen(failure is not null, $"Could not create a hard-link fixture: {failure}");

        var store = workspace.CreateStore();
        var loaded = await store.LoadAsync(Token);

        Assert.Equal(ThemeMode.Light, loaded.Theme);
        Assert.Equal(SettingsLoadOutcome.Loaded, store.Status!.Outcome);

        await store.SaveAsync(loaded with { Theme = ThemeMode.Dark }, Token);

        var reloaded = await workspace.CreateStore().LoadAsync(Token);
        Assert.Equal(ThemeMode.Dark, reloaded.Theme);
    }

    private static async Task AssertRejected(string label, string json, SettingsRejection expected)
    {
        using var workspace = new SettingsWorkspace(label);
        workspace.PlantSettings(json);

        var store = workspace.CreateStore();
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        SettingsAssert.IsDefault(loaded);
        Assert.NotNull(store.Status);
        Assert.Equal(expected, store.Status.Rejection);
        Assert.Equal(SettingsLoadOutcome.Replaced, store.Status.Outcome);
        Assert.NotNull(store.Status.BackupPath);
        Assert.True(File.Exists(store.Status.BackupPath));

        // The replacement is itself loadable, so a bad file does not produce a
        // permanently failing startup.
        var repaired = await workspace.CreateStore().LoadAsync(TestContext.Current.CancellationToken);
        SettingsAssert.IsDefault(repaired);
    }

    private static async Task<(double? Width, double? Height)> LoadWindow(
        string label,
        IScreenBoundsSource screens,
        string width,
        string height)
    {
        using var workspace = new SettingsWorkspace(label);
        workspace.PlantSettings(TamperedSettingsCorpus.Document(windowWidth: width, windowHeight: height));

        var loaded = await workspace
            .CreateStore(workspace.Options(screens))
            .LoadAsync(TestContext.Current.CancellationToken);

        return (loaded.WindowWidth, loaded.WindowHeight);
    }
}

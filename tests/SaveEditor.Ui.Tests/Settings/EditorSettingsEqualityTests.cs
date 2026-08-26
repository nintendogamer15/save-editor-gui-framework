using SaveEditor.Ui.Settings;

namespace SaveEditor.Ui.Tests.Settings;

/// <summary>
/// Pins value equality for <see cref="EditorSettings"/>.
/// </summary>
/// <remarks>
/// The compiler-generated equality compares the recents lists by reference, so a
/// consumer using this record for dirty tracking would see a spurious change on
/// every load. These tests fail if that regresses.
/// </remarks>
public class EditorSettingsEqualityTests
{
    [Fact]
    public void Equal_But_Distinct_Recents_Lists_Compare_Equal()
    {
        var left = new EditorSettings { RecentFiles = ["a.sav", "b.sav"] };
        var right = new EditorSettings { RecentFiles = ["a.sav", "b.sav"] };

        Assert.NotSame(left.RecentFiles, right.RecentFiles);
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Different_Recents_Contents_Compare_Unequal()
    {
        var left = new EditorSettings { RecentFiles = ["a.sav"] };
        var right = new EditorSettings { RecentFiles = ["b.sav"] };

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void Recents_Order_Is_Significant()
    {
        // Most-recent-first is meaningful, so a reordering is a real change.
        var left = new EditorSettings { RecentFiles = ["a.sav", "b.sav"] };
        var right = new EditorSettings { RecentFiles = ["b.sav", "a.sav"] };

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void Recents_Comparison_Is_Ordinal_Regardless_Of_Platform()
    {
        // Whether two paths name the same file is platform-sensitive and belongs
        // where recents are deduplicated. Whether two in-memory objects are the
        // same is not.
        var left = new EditorSettings { RecentFiles = ["Save.dat"] };
        var right = new EditorSettings { RecentFiles = ["save.dat"] };

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void Scalar_Fields_Still_Participate()
    {
        var baseline = new EditorSettings();

        Assert.NotEqual(baseline, baseline with { Theme = ThemeMode.Light });
        Assert.NotEqual(baseline, baseline with { Accent = CatppuccinAccent.Teal });
        Assert.NotEqual(baseline, baseline with { LastSectionKey = "inventory" });
        Assert.NotEqual(baseline, baseline with { WindowWidth = 1280 });
        Assert.NotEqual(baseline, baseline with { WindowHeight = 800 });
        Assert.NotEqual(baseline, baseline with { SchemaVersion = 99 });
        Assert.NotEqual(baseline, baseline with { RecentFolders = ["c:/saves"] });
    }

    [Fact]
    public void Round_Tripping_Through_With_Preserves_Equality()
    {
        var settings = new EditorSettings
        {
            Theme = ThemeMode.Light,
            Accent = CatppuccinAccent.Mauve,
            RecentFiles = ["a.sav"],
            RecentFolders = ["c:/saves"],
        };

        Assert.Equal(settings, settings with { });
    }
}

using SaveEditor.Ui.Display;

using static SaveEditor.Ui.Tests.Display.PathDisplayFixtures;

namespace SaveEditor.Ui.Tests.Display;

/// <summary>
/// Finding A13: bidi and control characters in a displayed path spoof the overwrite
/// target, and truncation that drops the parent directory hides which save is about to be
/// destroyed.
/// </summary>
public sealed class PathDisplayFormatterTests
{
    /// <summary>
    /// The named test of PLAN.md section 12, gating P2 acceptance for A13.
    /// </summary>
    /// <remarks>
    /// Both halves of the finding in one place, because they are one attack. The override
    /// character makes the displayed filename lie; truncation that keeps only head and
    /// tail makes the displayed directory ambiguous. A confirmation dialog that does
    /// either is a dialog whose accept button writes somewhere the user did not read.
    /// </remarks>
    [Fact]
    public void PathFormatter_StripsBidiAndShowsFinalTwoComponents()
    {
        // The classic spoof: renders as "savetxt.png" when the override survives.
        var raw = $@"C:\Users\Jake\Documents\My Games\Chronicle\Saves\Profile1\save{RightToLeftOverride}gnp.txt";

        var label = Windows.Format(raw, 48);

        // 1. The override is gone from everything the user can be shown, including the
        //    value a screen reader announces.
        Assert.False(label.Label.Contains(RightToLeftOverride, StringComparison.Ordinal));
        Assert.False(label.FullLabel.Contains(RightToLeftOverride, StringComparison.Ordinal));

        // 2. Replaced visibly, not deleted, and flagged. A tampered path looks tampered.
        Assert.True(label.HasReplacedCharacters);
        Assert.True(label.Label.Contains(Replacement, StringComparison.Ordinal));

        // 3. The final two components survive whole: the filename the user is about to
        //    overwrite, and the profile directory that distinguishes it from its sibling.
        Assert.EndsWith(
            $@"Profile1\save{Replacement}gnp.txt{PopDirectionalIsolate}",
            label.Label,
            StringComparison.Ordinal);

        // 4. Truncation happened in the middle, and the volume root stayed put.
        Assert.True(label.IsTruncated);
        Assert.True(label.Label.Contains(Ellipsis, StringComparison.Ordinal));
        Assert.StartsWith($@"{FirstStrongIsolate}C:\Users\", label.Label, StringComparison.Ordinal);

        // 5. Wrapped in a directional isolate so it cannot reorder the sentence around it.
        Assert.Equal(FirstStrongIsolate, label.Label[0]);
        Assert.Equal(PopDirectionalIsolate, label.Label[^1]);

        // 6. It fit the budget, and the full value is available separately and untruncated.
        Assert.False(label.ExceedsMaxLength);
        Assert.False(label.FullLabel.Contains(Ellipsis, StringComparison.Ordinal));
        Assert.EndsWith(
            $@"Saves\Profile1\save{Replacement}gnp.txt{PopDirectionalIsolate}",
            label.FullLabel,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The exact substitution scenario, asserted character for character.
    /// </summary>
    /// <remarks>
    /// This is the regression check for the concrete failure: the user reads
    /// "savetxt.png", approves, and the framework overwrites "save.txt". After
    /// neutralization the visible order is the logical order, so what is read is what is
    /// named.
    /// </remarks>
    [Fact]
    public void PathFormatter_RightToLeftOverrideCannotReorderTheDisplayedName()
    {
        var raw = $@"C:\Saves\save{RightToLeftOverride}gnp.txt";

        var label = Windows.Format(raw, 64);

        Assert.Equal($@"C:\Saves\save{Replacement}gnp.txt", Inner(label.Label));
        Assert.Equal($@"C:\Saves\save{Replacement}gnp.txt", Inner(label.FullLabel));
    }

    /// <summary>Every reordering character named by section 10 is neutralized, not just the override.</summary>
    [Theory]
    [InlineData(0x202A)]
    [InlineData(0x202B)]
    [InlineData(0x202C)]
    [InlineData(0x202D)]
    [InlineData(0x202E)]
    [InlineData(0x2066)]
    [InlineData(0x2067)]
    [InlineData(0x2068)]
    [InlineData(0x2069)]
    [InlineData(0x200E)]
    [InlineData(0x200F)]
    [InlineData(0x061C)]
    [InlineData(0x0000)]
    [InlineData(0x0009)]
    [InlineData(0x000A)]
    [InlineData(0x000D)]
    [InlineData(0x001B)]
    [InlineData(0x007F)]
    [InlineData(0x0085)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    public void PathFormatter_ReplacesEveryControlAndBidiCharacter(int codePoint)
    {
        var offender = (char)codePoint;
        var raw = $@"C:\Saves\a{offender}b.dat";

        var label = Windows.Format(raw, 64);

        Assert.True(label.HasReplacedCharacters);
        Assert.Equal($@"C:\Saves\a{Replacement}b.dat", Inner(label.Label));

        // Replaced one for one, never deleted: a shortened name is a different plausible
        // name, and the user would have no way to tell that anything was removed.
        Assert.False(Inner(label.Label).Contains(offender, StringComparison.Ordinal));
        Assert.Equal(raw.Length, Inner(label.Label).Length);
    }

    /// <summary>
    /// The parent directory is what tells two saves apart, so it is never the thing
    /// truncation removes.
    /// </summary>
    [Fact]
    public void PathFormatter_KeepsTheComponentThatDistinguishesSiblingProfiles()
    {
        const string First = @"C:\Users\Jake\Documents\My Games\Chronicle\Saves\Profile1\save.dat";
        const string Second = @"C:\Users\Jake\Documents\My Games\Chronicle\Saves\Profile2\save.dat";

        var first = Windows.Format(First, 40);
        var second = Windows.Format(Second, 40);

        Assert.True(first.IsTruncated);
        Assert.True(second.IsTruncated);
        Assert.NotEqual(first.Label, second.Label);
        Assert.EndsWith($@"Profile1\save.dat{PopDirectionalIsolate}", first.Label, StringComparison.Ordinal);
        Assert.EndsWith($@"Profile2\save.dat{PopDirectionalIsolate}", second.Label, StringComparison.Ordinal);
    }

    /// <summary>
    /// The final two components win over the budget when the two cannot both be honored.
    /// </summary>
    /// <remarks>
    /// A deliberate trade. Overrunning the budget makes a label wide; eliding the tail
    /// makes it wrong, and a wrong label above an Overwrite button is how a save gets
    /// destroyed. <see cref="PathLabel.ExceedsMaxLength"/> tells the surface to wrap or
    /// scroll rather than trim.
    /// </remarks>
    [Fact]
    public void PathFormatter_ShowsFinalTwoComponentsEvenWhenTheyExceedTheBudget()
    {
        const string Parent = "Chronicle-Profile-Ironman-2026-08-26T14-29-04Z";
        const string File = "autosave-slot-07-verified-backup.chronicle.sav";
        var raw = $@"C:\Users\Jake\Saves\{Parent}\{File}";

        var label = Windows.Format(raw, 24);

        Assert.True(label.ExceedsMaxLength);
        Assert.True(label.IsTruncated);
        Assert.Equal($@"C:\{Ellipsis}\{Parent}\{File}", Inner(label.Label));
    }

    /// <summary>
    /// A component is shown whole or replaced whole; it is never cut into a fragment that
    /// reads like a complete name.
    /// </summary>
    [Fact]
    public void PathFormatter_ElidesWholeComponentsNeverPartOfOne()
    {
        string[] components =
        [
            "Users", "Jake", "AppData", "Roaming", "ChronicleLauncher", "Saves", "Profile1", "save.dat",
        ];

        var raw = $@"C:\{string.Join('\\', components)}";

        var label = Windows.Format(raw, 34);
        var inner = Inner(label.Label);

        Assert.True(label.IsTruncated);
        Assert.StartsWith(@"C:\", inner, StringComparison.Ordinal);

        foreach (var part in inner[3..].Split('\\'))
        {
            Assert.True(
                (part.Length == 1 && part[0] == Ellipsis) || components.Contains(part, StringComparer.Ordinal),
                $"'{part}' is neither a whole original component nor the ellipsis.");
        }
    }

    /// <summary>The UNC host and share are part of the root and are never elided.</summary>
    /// <remarks>
    /// Which machine is as load-bearing as which folder. A label reading
    /// <c>\\...\Profile1\save.dat</c> hides that the overwrite target moved from the local
    /// disk to somebody else's file server.
    /// </remarks>
    [Fact]
    public void PathFormatter_KeepsUncHostAndShareWhenTruncating()
    {
        const string Raw = @"\\backup-server\saves$\games\chronicle\archive\2026\Profile1\save.dat";

        var label = Windows.Format(Raw, 40);

        Assert.True(label.IsTruncated);
        Assert.StartsWith($@"{FirstStrongIsolate}\\backup-server\saves$\", label.Label, StringComparison.Ordinal);
        Assert.EndsWith($@"Profile1\save.dat{PopDirectionalIsolate}", label.Label, StringComparison.Ordinal);
    }

    /// <summary>On Windows both separators mean the same thing, so one of them is rendered.</summary>
    [Fact]
    public void PathFormatter_WindowsStyleRendersForwardSlashesAsBackslashes()
    {
        var label = Windows.Format("C:/Saves/Profile1/save.dat", 64);

        Assert.Equal(@"C:\Saves\Profile1\save.dat", Inner(label.Label));
        Assert.False(label.IsTruncated);
    }

    /// <summary>
    /// On POSIX a backslash is an ordinary character in a filename, not a separator.
    /// </summary>
    /// <remarks>
    /// Splitting on it would present one component as two, and the "final two components"
    /// guarantee would then protect the wrong pair -- showing a fragment of a filename as
    /// if it were the parent directory.
    /// </remarks>
    [Fact]
    public void PathFormatter_PosixStyleTreatsBackslashAsPartOfTheName()
    {
        const string Raw = @"/home/jake/.local/share/chronicle/saves/back\slash/save.dat";

        var posix = Posix.Format(Raw, 30);

        Assert.True(posix.IsTruncated);
        Assert.EndsWith($@"back\slash/save.dat{PopDirectionalIsolate}", posix.Label, StringComparison.Ordinal);
        Assert.StartsWith($"{FirstStrongIsolate}/home/", posix.Label, StringComparison.Ordinal);

        // The same bytes read under Windows rules split at the backslash instead. The
        // difference is the reason the style is explicit rather than inferred.
        var windows = Windows.Format(Raw, 30);

        Assert.EndsWith($@"slash\save.dat{PopDirectionalIsolate}", windows.Label, StringComparison.Ordinal);
    }

    /// <summary>A path with fewer than three components is never truncated at all.</summary>
    [Theory]
    [InlineData("save.dat")]
    [InlineData(@"Profile1\save.dat")]
    [InlineData(@"C:\save.dat")]
    public void PathFormatter_ShortPathsAreNeverTruncated(string raw)
    {
        var label = Windows.Format(raw, 4);

        Assert.False(label.IsTruncated);
        Assert.False(Inner(label.Label).Contains(Ellipsis, StringComparison.Ordinal));
        Assert.Equal(raw, Inner(label.Label));
    }

    /// <summary>A volume root with nothing under it still produces something readable.</summary>
    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:", "C:")]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"\\server\share\", @"\\server\share\")]
    [InlineData(@"\\server", @"\\server")]
    public void PathFormatter_RootOnlyPathsSurvive(string raw, string expected)
    {
        var label = Windows.Format(raw, 64);

        Assert.Equal(expected, Inner(label.Label));
        Assert.False(label.IsTruncated);
    }

    /// <summary>
    /// A trailing separator does not become an empty final component, so the last two
    /// components of a folder are the folder and its parent.
    /// </summary>
    [Fact]
    public void PathFormatter_TrailingSeparatorsDoNotConsumeTheFinalComponents()
    {
        var label = Windows.Format(@"C:\Games\Chronicle\Saves\Profile1\\\", 64);

        Assert.Equal(@"C:\Games\Chronicle\Saves\Profile1", Inner(label.Label));

        // The full value is the raw location, separators and all, because a tooltip that
        // silently tidied the string would be describing something other than what it was
        // given.
        Assert.Equal(@"C:\Games\Chronicle\Saves\Profile1\\\", Inner(label.FullLabel));
    }

    /// <summary>
    /// A legitimate right-to-left filename is displayed intact, not mangled.
    /// </summary>
    /// <remarks>
    /// The false-positive guard. Neutralizing a real Hebrew or Arabic name, or forcing it
    /// left-to-right, would teach the user that the framework garbles their filenames --
    /// and a user who stops reading the path is the user this whole finding is about.
    /// U+2068 isolates the run without imposing a direction on it.
    /// </remarks>
    [Fact]
    public void PathFormatter_LeavesLegitimateRightToLeftNamesIntact()
    {
        var name = Hebrew();
        var raw = $@"C:\Saves\{name}\{name}.dat";

        var label = Windows.Format(raw, 64);

        Assert.False(label.HasReplacedCharacters);
        Assert.Equal(raw, Inner(label.Label));
        Assert.Equal(FirstStrongIsolate, label.Label[0]);
        Assert.Equal(PopDirectionalIsolate, label.Label[^1]);
    }

    /// <summary>
    /// Zero-width joiners are left alone on purpose; they cannot reorder anything and some
    /// scripts need them to render a real filename correctly.
    /// </summary>
    [Fact]
    public void PathFormatter_LeavesZeroWidthJoinersAlone()
    {
        var raw = $@"C:\Saves\a{ZeroWidthJoiner}b.dat";

        var label = Windows.Format(raw, 64);

        Assert.False(label.HasReplacedCharacters);
        Assert.Equal(raw, Inner(label.Label));
    }

    /// <summary>The accessible value is never lossier than the visible one.</summary>
    /// <remarks>
    /// Requirement 4 of the formatter. A screen-reader user reading the announcement
    /// region must be able to hear the whole location, not a middle-truncated summary of
    /// it, while still being protected from the characters that make an announcement lie.
    /// </remarks>
    [Fact]
    public void PathFormatter_FullLabelIsNeverTruncatedAndAlwaysNeutralized()
    {
        var raw =
            $@"C:\Users\Jake\Documents\My Games\Chronicle\Saves\Profile1\save{RightToLeftEmbedding}{RightToLeftMark}.dat";

        var label = Windows.Format(raw, 20);

        Assert.True(label.IsTruncated);
        Assert.False(label.FullLabel.Contains(Ellipsis, StringComparison.Ordinal));
        Assert.True(label.FullLabel.Length > label.Label.Length);
        Assert.False(label.FullLabel.Contains(RightToLeftEmbedding, StringComparison.Ordinal));
        Assert.False(label.FullLabel.Contains(RightToLeftMark, StringComparison.Ordinal));
        Assert.Equal($@"C:\Users\Jake\Documents\My Games\Chronicle\Saves\Profile1\save{Replacement}{Replacement}.dat", Inner(label.FullLabel));
    }

    /// <summary>Exactly one isolate pair, on the outside, on every label.</summary>
    [Theory]
    [InlineData(@"C:\Saves\Profile1\save.dat")]
    [InlineData("/home/jake/saves/save.dat")]
    [InlineData("save.dat")]
    [InlineData(@"\\server\share\a\b\c\d\e\f\g\save.dat")]
    public void PathFormatter_WrapsEveryLabelInExactlyOneIsolatePair(string raw)
    {
        var label = Windows.Format(raw, 24);

        string[] values = [label.Label, label.FullLabel];

        foreach (var value in values)
        {
            Assert.Equal(FirstStrongIsolate, value[0]);
            Assert.Equal(PopDirectionalIsolate, value[^1]);
            Assert.Equal(1, value.Count(c => c == FirstStrongIsolate));
            Assert.Equal(1, value.Count(c => c == PopDirectionalIsolate));
            Assert.False(Inner(value).Contains(LeftToRightIsolate, StringComparison.Ordinal));
        }
    }
}

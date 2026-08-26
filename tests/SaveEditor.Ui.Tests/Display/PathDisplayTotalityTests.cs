using SaveEditor.Ui.Display;
using SaveEditor.Ui.Settings;

using static SaveEditor.Ui.Tests.Display.PathDisplayFixtures;

namespace SaveEditor.Ui.Tests.Display;

/// <summary>
/// The two properties the formatter has to hold for inputs nobody designed for: it always
/// produces something, and what it produces can never be turned back into a path.
/// </summary>
public sealed class PathDisplayTotalityTests
{
    /// <summary>
    /// Inputs that a picker, a drop payload, or a tampered settings file can actually
    /// deliver.
    /// </summary>
    public static TheoryData<string> HostileCorpus
    {
        get
        {
            var data = new TheoryData<string>
            {
                " ",
                "   ",
                ".",
                "..",
                @"..\..\..\etc\passwd",
                "/",
                "//",
                "///",
                @"\",
                @"\\",
                @"\\\",
                @"C:",
                @"C:\",
                "C:relative.dat",
                "save.dat",
                @"C:\Saves\Profile1\save.dat",
                "/home/jake/saves/save.dat",
                @"\\server\share\save.dat",
                @"\\server\share",
                @"\\?\C:\Saves\save.dat",
                @"C:\Saves\Profile1\save.dat:$DATA",
                @"C:\Saves\Profile1\save.dat   ",
                @"C:\Saves\\\\Profile1\\\save.dat",
                new string('a', 5000),
                @"C:\" + string.Join('\\', Enumerable.Repeat("component", 400)) + @"\save.dat",
                string.Join('\\', Enumerable.Repeat("x", 20000)),
            };

            // Entirely bidi marks: nothing survives neutralization except the markers.
            data.Add(new string(RightToLeftOverride, 12));
            data.Add(new string([RightToLeftOverride, PopDirectionalIsolate, RightToLeftMark]));

            // The formatter's own wrapper arriving as input.
            data.Add($"{FirstStrongIsolate}C:\\Saves\\save.dat{PopDirectionalIsolate}");

            // A control character in every position that matters.
            data.Add($"{(char)0x000A}C:\\Saves{(char)0x0000}\\save.dat{(char)0x001B}");

            // Unpaired surrogates. Legal in a Windows filename, ill-formed as UTF-16.
            data.Add(new string([(char)0xD800]));
            data.Add($"C:\\Saves\\{(char)0xD800}\\{(char)0xDC00}save.dat");

            // A well-formed astral pair must not be mistaken for two lone surrogates.
            data.Add($"C:\\Saves\\{(char)0xD83D}{(char)0xDCBE}\\save.dat");

            return data;
        }
    }

    /// <summary>Every input produces a label. Nothing throws, at any budget.</summary>
    [Theory]
    [MemberData(nameof(HostileCorpus))]
    public void PathFormatter_ProducesALabelForEveryInput(string raw)
    {
        int[] budgets = [int.MinValue, -1, 0, 1, 2, 8, 64, int.MaxValue];

        foreach (var formatter in Formatters)
        {
            foreach (var budget in budgets)
            {
                var label = formatter.Format(raw, budget);

                Assert.NotNull(label.Label);
                Assert.NotNull(label.FullLabel);

                if (label.IsEmpty)
                {
                    continue;
                }

                // Non-empty labels are always isolated, both of them.
                Assert.Equal(FirstStrongIsolate, label.Label[0]);
                Assert.Equal(PopDirectionalIsolate, label.Label[^1]);
                Assert.Equal(FirstStrongIsolate, label.FullLabel[0]);
                Assert.Equal(PopDirectionalIsolate, label.FullLabel[^1]);

                // And carry no character the neutralizer is responsible for removing, in
                // either the visible or the announced value.
                AssertNeutralAndWellFormed(Inner(label.Label));
                AssertNeutralAndWellFormed(Inner(label.FullLabel));
            }
        }
    }

    /// <summary>Null and empty produce the empty label rather than a placeholder or a throw.</summary>
    [Fact]
    public void PathFormatter_NullAndEmptyProduceTheEmptyLabel()
    {
        Assert.Same(PathLabel.Empty, PathDisplayFormatter.Default.Format(null));
        Assert.Same(PathLabel.Empty, PathDisplayFormatter.Default.Format(string.Empty));
        Assert.True(PathLabel.Empty.IsEmpty);
        Assert.Equal(string.Empty, PathLabel.Empty.Label);
        Assert.Equal(string.Empty, PathLabel.Empty.FullLabel);
    }

    /// <summary>Whitespace is a real, if odd, component and is shown rather than swallowed.</summary>
    /// <remarks>
    /// Reporting a settings entry of three spaces as "no path" would hide that the file
    /// was edited by something other than this framework.
    /// </remarks>
    [Fact]
    public void PathFormatter_WhitespaceOnlyPathIsShownNotSwallowed()
    {
        var label = Windows.Format("   ", 64);

        Assert.False(label.IsEmpty);
        Assert.Equal("   ", Inner(label.Label));
        Assert.False(label.HasReplacedCharacters);
    }

    /// <summary>Formatting an already-formatted label does not degrade it further.</summary>
    /// <remarks>
    /// Not a licence to re-format -- a label is display text and feeding it back in is a
    /// caller mistake. It is a stability property: a shell that formats in a view model
    /// and again in a converter must not produce a string that decays on each pass, and a
    /// visible decay would look exactly like tampering that was not there.
    /// </remarks>
    [Theory]
    [MemberData(nameof(HostileCorpus))]
    public void PathFormatter_IsIdempotent(string raw)
    {
        int[] budgets = [8, 24, 64];

        foreach (var formatter in Formatters)
        {
            foreach (var budget in budgets)
            {
                var once = formatter.Format(raw, budget);

                Assert.Equal(once.Label, formatter.Format(once.Label, budget).Label);
                Assert.Equal(once.FullLabel, formatter.Format(once.FullLabel, budget).FullLabel);
            }
        }
    }

    /// <summary>
    /// The output cannot be opened, and cannot be compared equal to the location it
    /// describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the regression check for the failure mode that would undo the whole
    /// finding: a formatter whose result round-trips into the filesystem hands an attacker
    /// back the substitution the formatter exists to prevent -- display one target, open
    /// another. The isolate wrapping makes that structurally impossible rather than merely
    /// discouraged.
    /// </para>
    /// <para>
    /// Asserted against a file that genuinely exists, so the negative results below mean
    /// "this string does not name that file" and not "no file was there to find".
    /// </para>
    /// </remarks>
    [Fact]
    public void PathFormatter_OutputCannotBeUsedAsAFilesystemPath()
    {
        var directory = Directory.CreateTempSubdirectory("save-editor-a13-");

        try
        {
            var real = Path.Combine(directory.FullName, "save.dat");
            File.WriteAllBytes(real, [0x01, 0x02, 0x03]);
            Assert.True(File.Exists(real));

            var label = PathDisplayFormatter.Default.Format(real);

            Assert.NotEqual(real, label.Label);
            Assert.NotEqual(real, label.FullLabel);
            Assert.False(File.Exists(label.Label));
            Assert.False(File.Exists(label.FullLabel));

            // Nor can it slip through the comparison the recents list deduplicates with.
            Assert.False(RecentPaths.Comparer.Equals(real, label.Label));
            Assert.False(RecentPaths.Comparer.Equals(real, label.FullLabel));

            // Nor be stored back into settings as if it were a path.
            Assert.True(RecentPaths.IsStorable(real));
            Assert.False(RecentPaths.IsStorable(label.Label));
            Assert.False(RecentPaths.IsStorable(label.FullLabel));

            // ToString is the same display text, so a naive binding is safe and a naive
            // File.Open is not silently correct.
            Assert.Equal(label.Label, label.ToString());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>A path of a thousand short components does not turn the status bar into a hang.</summary>
    /// <remarks>
    /// A drop payload is not length-bounded before it reaches a display surface, so the
    /// truncation search computes candidate lengths arithmetically instead of building a
    /// candidate per step.
    /// </remarks>
    [Fact]
    public void PathFormatter_LongPathWithManyComponentsFormatsPromptly()
    {
        var raw = @"C:\" + string.Join('\\', Enumerable.Repeat("x", 100_000)) + @"\Profile1\save.dat";

        var started = System.Diagnostics.Stopwatch.StartNew();
        var label = Windows.Format(raw, 40);
        started.Stop();

        Assert.EndsWith($@"Profile1\save.dat{PopDirectionalIsolate}", label.Label, StringComparison.Ordinal);
        Assert.True(label.IsTruncated);
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(2),
            $"Formatting took {started.Elapsed}, which suggests the truncation search is not linear.");
    }

    /// <summary>An all-bidi path becomes visible markers rather than nothing at all.</summary>
    [Fact]
    public void PathFormatter_PathOfOnlyBidiMarksBecomesVisibleMarkers()
    {
        var label = Windows.Format(new string(RightToLeftOverride, 5), 64);

        Assert.True(label.HasReplacedCharacters);
        Assert.Equal(new string(Replacement, 5), Inner(label.Label));
        Assert.False(label.IsEmpty);
    }

    private static PathDisplayFormatter[] Formatters => [Windows, Posix, PathDisplayFormatter.Default];

    /// <summary>
    /// No neutralizable character survived, and the result is well-formed UTF-16.
    /// </summary>
    /// <remarks>
    /// Well-formedness matters beyond tidiness: the announcement region hands this value
    /// to a screen reader, and an unpaired surrogate there is undefined behavior in
    /// somebody else's process.
    /// </remarks>
    private static void AssertNeutralAndWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];

            Assert.False(
                DisplayTextNeutralizer.ShouldReplace(current),
                $"U+{(int)current:X4} survived neutralization at index {i}.");

            if (char.IsHighSurrogate(current))
            {
                Assert.True(
                    i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]),
                    $"Unpaired high surrogate survived at index {i}.");
                i++;
                continue;
            }

            Assert.False(char.IsLowSurrogate(current), $"Unpaired low surrogate survived at index {i}.");
        }
    }
}

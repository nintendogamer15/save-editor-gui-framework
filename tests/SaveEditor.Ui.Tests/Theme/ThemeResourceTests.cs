using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SaveEditor.PaletteGen;

namespace SaveEditor.Ui.Tests.Theme;

/// <summary>
/// Asserts the contrast contract against the resources that actually ship, rather
/// than against the generator's arithmetic.
/// </summary>
/// <remarks>
/// <see cref="ContrastContractTests"/> proves the maths is right. These prove the
/// committed XAML says what the maths produced — a generator that is correct but
/// whose output was never regenerated ships an unreadable theme just as surely as
/// a broken formula.
/// </remarks>
public class ThemeResourceTests
{
    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepositoryRoot([CallerFilePath] string callerPath = "")
    {
        var directory = Directory.GetParent(callerPath);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PLAN.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate the repository root from the test source path.");
    }

    private static string ThemesDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "SaveEditor.Ui", "Themes");

    private static Dictionary<string, Srgb> ReadBrushes(string file, string themeKey)
    {
        var document = XDocument.Load(file);

        var themed = document.Descendants(Xaml + "ResourceDictionary")
            .FirstOrDefault(d => (string?)d.Attribute(X + "Key") == themeKey)
            ?? throw new InvalidOperationException($"{Path.GetFileName(file)} has no '{themeKey}' theme dictionary.");

        return themed.Elements(Xaml + "SolidColorBrush")
            .ToDictionary(
                e => (string)e.Attribute(X + "Key")!,
                e => Srgb.Parse(e.Value.Trim()),
                StringComparer.Ordinal);
    }

    private static IReadOnlyList<Srgb> Surfaces(string themeKey)
    {
        var semantic = ReadBrushes(Path.Combine(ThemesDirectory(), "Semantic.axaml"), themeKey);
        return
        [
            semantic["WindowBackground"],
            semantic["PanelBackground"],
            semantic["InputBackground"],
            semantic["CardBackground"],
        ];
    }

    public static TheoryData<string, string> AccentsAndThemes()
    {
        var data = new TheoryData<string, string>();
        foreach (var accent in CatppuccinPalette.AccentNames)
        {
            var file = char.ToUpperInvariant(accent[0]) + accent[1..];
            data.Add(file, "Light");
            data.Add(file, "Dark");
        }

        return data;
    }

    [Fact]
    public void Committed_Resources_Match_The_Generator()
    {
        var themes = ThemesDirectory();

        foreach (var (relativePath, expected) in ThemeGenerator.GenerateAll())
        {
            var path = Path.Combine(themes, relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"{relativePath} is missing. Regenerate the theme resources.");
            Assert.True(
                File.ReadAllText(path).ReplaceLineEndings("\n") == expected.ReplaceLineEndings("\n"),
                $"{relativePath} has drifted from the generator. Regenerate rather than hand-editing it.");
        }
    }

    [Theory]
    [MemberData(nameof(AccentsAndThemes))]
    public void Shipped_Accent_Text_Meets_Contrast_On_Every_Surface(string accent, string themeKey)
    {
        var brushes = ReadBrushes(Path.Combine(ThemesDirectory(), "Accents", $"{accent}.axaml"), themeKey);
        var surfaces = Surfaces(themeKey);

        foreach (var role in new[] { "PrimaryText", "FocusRing", "BorderStrong" })
        {
            var ratio = Contrast.MinimumRatio(brushes[role], surfaces);
            Assert.True(
                ratio >= Contrast.TextMinimum,
                $"{accent}/{themeKey} {role} is {ratio:F2}:1, below {Contrast.TextMinimum}:1.");
        }
    }

    [Theory]
    [MemberData(nameof(AccentsAndThemes))]
    public void Shipped_OnPrimaryForeground_Is_Legible_On_Its_Fill(string accent, string themeKey)
    {
        var brushes = ReadBrushes(Path.Combine(ThemesDirectory(), "Accents", $"{accent}.axaml"), themeKey);

        var ratio = Contrast.Ratio(brushes["OnPrimaryForeground"], brushes["Primary"]);
        Assert.True(
            ratio >= Contrast.TextMinimum,
            $"{accent}/{themeKey} on-accent text is {ratio:F2}:1 against its own fill.");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Shipped_Neutral_And_Status_Text_Meets_Contrast(string themeKey)
    {
        var semantic = ReadBrushes(Path.Combine(ThemesDirectory(), "Semantic.axaml"), themeKey);
        var surfaces = Surfaces(themeKey);

        foreach (var role in new[] { "Foreground", "MutedForeground" })
        {
            var ratio = Contrast.MinimumRatio(semantic[role], surfaces);
            Assert.True(ratio >= Contrast.TextMinimum, $"{themeKey} {role} is {ratio:F2}:1.");
        }

        // SubtleForeground is non-essential text and holds the lower floor.
        Assert.True(
            Contrast.MinimumRatio(semantic["SubtleForeground"], surfaces) >= Contrast.NonTextMinimum,
            $"{themeKey} SubtleForeground falls below {Contrast.NonTextMinimum}:1.");

        // Status text must clear its own banner as well as the page behind it.
        foreach (var role in new[] { "Danger", "Warning", "Success" })
        {
            var against = new List<Srgb>(surfaces) { semantic[$"{role}Background"] };
            var ratio = Contrast.MinimumRatio(semantic[$"{role}Text"], against);

            Assert.True(
                ratio >= Contrast.TextMinimum,
                $"{themeKey} {role}Text is {ratio:F2}:1 against its surfaces and its own background wash.");
        }
    }

    /// <summary>Every directory tree that can contain a consuming view.</summary>
    /// <remarks>
    /// <c>samples</c> is here because the gallery is where the real views live. An
    /// earlier version scanned only <c>src</c>, which meant the gate covered the
    /// control themes and silently excluded every actual view — the one thing it
    /// exists to check.
    /// </remarks>
    private static IEnumerable<string> ViewRoots()
    {
        var root = RepositoryRoot();
        yield return Path.Combine(root, "src");
        yield return Path.Combine(root, "samples");
    }

    [Fact]
    public void View_Roots_Are_Not_Silently_Empty()
    {
        // A scan over a mistyped or moved directory finds nothing and passes. Assert
        // each root exists and actually contains XAML before trusting the gate below.
        foreach (var root in ViewRoots())
        {
            Assert.True(Directory.Exists(root), $"View root '{root}' does not exist.");
            Assert.NotEmpty(Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public void Views_Reference_Only_Semantic_Resources()
    {
        var themes = ThemesDirectory();
        var known = SemanticTokens.All.ToHashSet(StringComparer.Ordinal);

        var offenders = new List<string>();
        var scanned = 0;

        foreach (var root in ViewRoots())
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
            {
                // The generated dictionaries define the tokens; they do not consume them.
                if (file.StartsWith(themes, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                scanned++;

                foreach (var reference in ResourceReferences(File.ReadAllText(file)))
                {
                    if (!known.Contains(reference))
                    {
                        offenders.Add($"{Path.GetFileName(file)} references '{reference}'");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Views may only reference semantic resources; raw palette names are internal. " +
            string.Join("; ", offenders));

        // The gallery token page alone contributes dozens of references, so a scan
        // that suddenly covers almost nothing means the roots drifted, not that the
        // codebase got cleaner.
        Assert.True(scanned >= 3, $"Only {scanned} view files were scanned; the view roots look wrong.");
    }

    private static IEnumerable<string> ResourceReferences(string xaml)
    {
        foreach (var marker in new[] { "{DynamicResource ", "{StaticResource " })
        {
            var index = 0;
            while ((index = xaml.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                var start = index + marker.Length;
                var end = xaml.IndexOf('}', start);
                if (end < 0)
                {
                    yield break;
                }

                yield return xaml[start..end].Trim();
                index = end;
            }
        }
    }
}

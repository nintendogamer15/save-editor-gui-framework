using System.Globalization;
using System.Text;

namespace SaveEditor.PaletteGen;

/// <summary>
/// Emits the Avalonia resource dictionaries that back the two themes and the
/// fourteen accents.
/// </summary>
/// <remarks>
/// <para>
/// Output is committed to the repository and a drift test regenerates and compares,
/// so the generator is the single source of truth for every colour the framework
/// exposes and no value is hand-edited into a XAML file.
/// </para>
/// <para>
/// Accent-dependent roles live in the per-accent dictionaries rather than the
/// semantic one, because accent is switched at runtime by swapping which accent
/// dictionary is merged. `FocusRing` and `BorderStrong` are in that set: they take
/// the derived `PrimaryText` ramp rather than the raw accent, since eleven of the
/// fourteen raw Latte accents fall below even the 3:1 indicator floor.
/// </para>
/// </remarks>
public static class ThemeGenerator
{
    // Note for future edits: XML forbids a double hyphen inside a comment, so the
    // regeneration command line cannot live here. It is in eng/palette/README.txt.
    private const string Header =
        "<!--\n" +
        "    GENERATED FILE. DO NOT EDIT.\n" +
        "\n" +
        "    Produced by eng/SaveEditor.PaletteGen from the pinned Catppuccin palette\n" +
        "    at eng/palette/palette.json. Edit the generator, never this file, then\n" +
        "    regenerate using the command in eng/palette/README.txt.\n" +
        "\n" +
        "    A drift test fails the build if these files and the generator disagree.\n" +
        "-->\n";

    /// <summary>Alpha applied to the shadow colour.</summary>
    private const byte ShadowAlpha = 0x6B;

    /// <summary>How much status hue is washed into the window background.</summary>
    private const double StatusWashAmount = 0.22;

    /// <summary>Every generated file, keyed by path relative to the themes directory.</summary>
    /// <returns>Relative path to file content.</returns>
    public static IReadOnlyDictionary<string, string> GenerateAll()
    {
        var palette = CatppuccinPalette.Load();

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Semantic.axaml"] = GenerateSemantic(palette),
        };

        foreach (var accent in CatppuccinPalette.AccentNames)
        {
            files[$"Accents/{Capitalise(accent)}.axaml"] = GenerateAccent(palette, accent);
        }

        return files;
    }

    /// <summary>Colours that do not depend on the selected accent.</summary>
    /// <param name="palette">The pinned palette.</param>
    /// <returns>The semantic dictionary as XAML.</returns>
    public static string GenerateSemantic(CatppuccinPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var xaml = new StringBuilder();
        xaml.Append(Header);
        xaml.Append("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\"\n");
        xaml.Append("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n\n");
        xaml.Append("  <ResourceDictionary.ThemeDictionaries>\n");

        foreach (var (flavour, key) in new[] { (ThemeFlavor.Latte, "Light"), (ThemeFlavor.Mocha, "Dark") })
        {
            var surfaces = palette.TextBearingSurfaces(flavour);

            xaml.Append(CultureInfo.InvariantCulture, $"\n    <ResourceDictionary x:Key=\"{key}\">\n");

            Section(xaml, "surfaces");
            Brush(xaml, "WindowBackground", palette[flavour, "base"]);
            Brush(xaml, "PanelBackground", palette[flavour, "mantle"]);
            Brush(xaml, "InputBackground", palette[flavour, "crust"]);
            Brush(xaml, "CardBackground", palette[flavour, "surface0"]);
            Brush(xaml, "OverlayBackground", palette[flavour, "crust"]);

            Section(xaml, "text");
            Brush(xaml, "Foreground", palette[flavour, "text"]);
            Brush(xaml, "MutedForeground", Legible(palette[flavour, "subtext1"], surfaces));
            Brush(xaml, "SubtleForeground", palette[flavour, "subtext0"]);

            Section(xaml, "lines (BorderStrong and FocusRing are accent-dependent)");
            Brush(xaml, "Border", palette[flavour, "surface1"]);

            Section(xaml, "status");
            foreach (var (role, source) in new[] { ("Danger", "red"), ("Warning", "yellow"), ("Success", "green") })
            {
                var raw = palette[flavour, source];
                var wash = Srgb.Mix(palette[flavour, "base"], raw, StatusWashAmount);

                // The wash joins the surface set so status text is legible on the
                // banner it sits in, not merely on the page behind it.
                var legible = Legible(raw, [.. surfaces, wash]);

                Brush(xaml, role, raw);
                Brush(xaml, $"{role}Text", legible);
                Brush(xaml, $"{role}Background", wash);
            }

            Section(xaml, "elevation");
            Line(xaml, $"<Color x:Key=\"ShadowColor\">{palette[flavour, "crust"].ToHexWithAlpha(ShadowAlpha)}</Color>");

            xaml.Append("    </ResourceDictionary>\n");
        }

        xaml.Append("\n  </ResourceDictionary.ThemeDictionaries>\n\n");

        // Typography and metrics do not vary by theme.
        xaml.Append("  <FontFamily x:Key=\"FontFamilyDefault\">avares://Avalonia.Fonts.Inter/Assets#Inter</FontFamily>\n");
        xaml.Append("  <FontFamily x:Key=\"FontFamilyMono\">Cascadia Mono,Consolas,DejaVu Sans Mono,monospace</FontFamily>\n\n");

        foreach (var (name, value) in new[]
                 {
                     ("SpaceXs", 6), ("SpaceSm", 8), ("SpaceMd", 12), ("SpaceLg", 16), ("SpaceXl", 20),
                 })
        {
            xaml.Append(CultureInfo.InvariantCulture, $"  <x:Double x:Key=\"{name}\">{value}</x:Double>\n");
        }

        xaml.Append('\n');

        foreach (var (name, value) in new[] { ("RadiusSm", 6), ("RadiusMd", 9), ("RadiusLg", 14) })
        {
            xaml.Append(CultureInfo.InvariantCulture, $"  <CornerRadius x:Key=\"{name}\">{value}</CornerRadius>\n");
        }

        xaml.Append("\n</ResourceDictionary>\n");
        return xaml.ToString();
    }

    /// <summary>Colours for one accent, in both themes.</summary>
    /// <param name="palette">The pinned palette.</param>
    /// <param name="accent">Raw accent name, such as <c>mauve</c>.</param>
    /// <returns>The accent dictionary as XAML.</returns>
    public static string GenerateAccent(CatppuccinPalette palette, string accent)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var xaml = new StringBuilder();
        xaml.Append(Header);
        xaml.Append("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\"\n");
        xaml.Append("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n\n");
        xaml.Append("  <ResourceDictionary.ThemeDictionaries>\n");

        foreach (var (flavour, key) in new[] { (ThemeFlavor.Latte, "Light"), (ThemeFlavor.Mocha, "Dark") })
        {
            var raw = palette[flavour, accent];
            var surfaces = palette.TextBearingSurfaces(flavour);
            var legible = Legible(raw, surfaces);
            var onAccent = Contrast.OnAccentForeground(raw);

            xaml.Append(CultureInfo.InvariantCulture, $"\n    <ResourceDictionary x:Key=\"{key}\">\n");

            Section(xaml, "fill only - never text, lines, or focus");
            Brush(xaml, "Primary", raw);
            Brush(xaml, "PrimaryHover", Srgb.Mix(raw, new Srgb(0xFF, 0xFF, 0xFF), 0.10));
            Brush(xaml, "PrimaryPressed", raw.Darken(0.90));
            Brush(xaml, "OnPrimaryForeground", onAccent);

            Section(xaml, "derived ramp - text, focus, and stateful borders");
            Brush(xaml, "PrimaryText", legible);
            Brush(xaml, "FocusRing", legible);
            Brush(xaml, "BorderStrong", legible);

            xaml.Append("    </ResourceDictionary>\n");
        }

        xaml.Append("\n  </ResourceDictionary.ThemeDictionaries>\n\n</ResourceDictionary>\n");
        return xaml.ToString();
    }

    private static Srgb Legible(Srgb colour, IReadOnlyList<Srgb> surfaces) =>
        Contrast.DeriveLegible(colour, surfaces, Contrast.TextMinimum)
        ?? throw new InvalidOperationException(
            $"{colour} cannot reach {Contrast.TextMinimum}:1 against its surfaces even at black. " +
            "The palette pin or the surface mapping has changed in a way the contrast contract " +
            "does not cover; do not ship a theme that fails its own gate.");

    private static void Brush(StringBuilder xaml, string key, Srgb colour) =>
        Line(xaml, $"<SolidColorBrush x:Key=\"{key}\">{colour}</SolidColorBrush>");

    private static void Line(StringBuilder xaml, string content) =>
        xaml.Append(CultureInfo.InvariantCulture, $"      {content}\n");

    private static void Section(StringBuilder xaml, string label) =>
        xaml.Append(CultureInfo.InvariantCulture, $"\n      <!-- {label} -->\n");

    private static string Capitalise(string value) =>
        char.ToUpperInvariant(value[0]) + value[1..];
}

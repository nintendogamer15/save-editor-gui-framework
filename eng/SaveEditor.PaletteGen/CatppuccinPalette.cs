using System.Reflection;
using System.Text.Json;

namespace SaveEditor.PaletteGen;

/// <summary>The two flavours this framework ships.</summary>
public enum ThemeFlavor
{
    /// <summary>Catppuccin Latte, the light mode.</summary>
    Latte,

    /// <summary>Catppuccin Mocha, the dark mode and default.</summary>
    Mocha,
}

/// <summary>
/// The pinned Catppuccin palette, loaded from the vendored <c>palette.json</c>.
/// </summary>
/// <remarks>
/// The file is embedded rather than read from disk so that generation, tests, and
/// CI never depend on network access or on a working directory.
/// </remarks>
public sealed class CatppuccinPalette
{
    /// <summary>The fourteen accents, in Catppuccin's own order.</summary>
    public static readonly IReadOnlyList<string> AccentNames =
    [
        "rosewater", "flamingo", "pink", "mauve", "red", "maroon", "peach",
        "yellow", "green", "teal", "sky", "sapphire", "blue", "lavender",
    ];

    private readonly Dictionary<ThemeFlavor, Dictionary<string, Srgb>> _colours;

    private CatppuccinPalette(Dictionary<ThemeFlavor, Dictionary<string, Srgb>> colours) =>
        _colours = colours;

    /// <summary>Loads the pinned palette.</summary>
    /// <returns>The palette.</returns>
    public static CatppuccinPalette Load()
    {
        using var stream = typeof(CatppuccinPalette).GetTypeInfo().Assembly
                               .GetManifestResourceStream("palette.json")
                           ?? throw new InvalidOperationException(
                               "Embedded palette.json is missing from SaveEditor.PaletteGen.");

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var colours = new Dictionary<ThemeFlavor, Dictionary<string, Srgb>>();
        foreach (var flavour in Enum.GetValues<ThemeFlavor>())
        {
            var key = flavour.ToString().ToLowerInvariant();
            var entries = root.GetProperty(key).GetProperty("colors");

            var parsed = new Dictionary<string, Srgb>(StringComparer.Ordinal);
            foreach (var colour in entries.EnumerateObject())
            {
                parsed[colour.Name] = Srgb.Parse(colour.Value.GetProperty("hex").GetString()!);
            }

            colours[flavour] = parsed;
        }

        return new CatppuccinPalette(colours);
    }

    /// <summary>Looks up a raw palette colour.</summary>
    /// <param name="flavour">Which flavour.</param>
    /// <param name="name">Raw palette name, such as <c>mauve</c> or <c>surface0</c>.</param>
    /// <returns>The colour.</returns>
    public Srgb this[ThemeFlavor flavour, string name] => _colours[flavour][name];

    /// <summary>
    /// The surfaces that text is rendered on, and which every text role is asserted
    /// against.
    /// </summary>
    /// <param name="flavour">Which flavour.</param>
    /// <returns>Window, panel, input, and card backgrounds.</returns>
    /// <remarks>
    /// The card surface binds in both flavours — it is the darkest in Latte and the
    /// lightest in Mocha — so a role that passes on it passes on all four.
    /// </remarks>
    public IReadOnlyList<Srgb> TextBearingSurfaces(ThemeFlavor flavour) =>
    [
        this[flavour, "base"],
        this[flavour, "mantle"],
        this[flavour, "crust"],
        this[flavour, "surface0"],
    ];
}

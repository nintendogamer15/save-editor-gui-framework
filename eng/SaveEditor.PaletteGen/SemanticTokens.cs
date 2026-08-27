namespace SaveEditor.PaletteGen;

/// <summary>
/// The complete set of semantic resource names exposed to application views.
/// </summary>
/// <remarks>
/// <para>
/// Raw palette names never appear in views or control themes. This list is the
/// contract between the generator and the framework: a resource-resolution test
/// fails the build if a view references anything outside it.
/// </para>
/// <para>
/// Accent roles are split three ways because a raw Catppuccin accent is only safe
/// as a fill. In Latte, eleven of the fourteen accents fall below even the 3:1
/// indicator floor, so focus rings and stateful borders take the derived text
/// ramp rather than the raw accent.
/// </para>
/// </remarks>
public static class SemanticTokens
{
    /// <summary>Surfaces that content sits on.</summary>
    public static readonly IReadOnlyList<string> Surfaces =
    [
        "WindowBackground",
        "PanelBackground",
        "CardBackground",
        "InputBackground",
        "OverlayBackground",
    ];

    /// <summary>Neutral text roles.</summary>
    public static readonly IReadOnlyList<string> Text =
    [
        "Foreground",
        "MutedForeground",
        "SubtleForeground",
    ];

    /// <summary>Lines and separators.</summary>
    public static readonly IReadOnlyList<string> Lines =
    [
        "Border",
        "BorderStrong",
        "FocusRing",
    ];

    /// <summary>Accent roles.</summary>
    public static readonly IReadOnlyList<string> Accent =
    [
        "Primary",
        "PrimaryHover",
        "PrimaryPressed",
        "PrimaryText",
        "OnPrimaryForeground",
    ];

    /// <summary>Status roles, as fill, as text, and as background wash.</summary>
    /// <remarks>
    /// Destructive buttons use <c>Danger</c> as a fill, so they need the same
    /// hover/pressed ramp <see cref="Accent"/> gives <c>Primary</c>. Warning and
    /// Success are not button fills.
    /// </remarks>
    public static readonly IReadOnlyList<string> Status =
    [
        "Danger", "DangerHover", "DangerPressed",
        "Warning", "Success",
        "DangerText", "WarningText", "SuccessText",
        "DangerBackground", "WarningBackground", "SuccessBackground",
    ];

    /// <summary>Elevation, typography, and metrics.</summary>
    public static readonly IReadOnlyList<string> Chrome =
    [
        "ShadowColor",
        "FontFamilyDefault",
        "FontFamilyMono",
        "SpaceXs", "SpaceSm", "SpaceMd", "SpaceLg", "SpaceXl",
        "RadiusSm", "RadiusMd", "RadiusLg",
    ];

    /// <summary>Every semantic resource name, across all groups.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        .. Surfaces, .. Text, .. Lines, .. Accent, .. Status, .. Chrome,
    ];

    /// <summary>
    /// Roles that must reach <see cref="Contrast.TextMinimum"/> against every
    /// text-bearing surface.
    /// </summary>
    public static readonly IReadOnlyList<string> RequireTextContrast =
    [
        "Foreground",
        "MutedForeground",
        "PrimaryText",
        "DangerText",
        "WarningText",
        "SuccessText",
        "BorderStrong",
        "FocusRing",
    ];
}

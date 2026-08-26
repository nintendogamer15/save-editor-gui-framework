using SaveEditor.PaletteGen;

namespace SaveEditor.Ui.Tests.Theme;

/// <summary>
/// Encodes the contrast contract of PLAN.md section 5 as executable assertions, so
/// that moving the palette pin or editing a derivation cannot quietly ship an
/// unreadable theme.
/// </summary>
public class ContrastContractTests
{
    private static readonly CatppuccinPalette Palette = CatppuccinPalette.Load();

    [Fact]
    public void Ratio_Matches_Independently_Computed_Values()
    {
        Assert.Equal(7.06, Contrast.Ratio(Srgb.Parse("#4c4f69"), Srgb.Parse("#eff1f5")), 2);
        Assert.Equal(11.34, Contrast.Ratio(Srgb.Parse("#cdd6f4"), Srgb.Parse("#1e1e2e")), 2);
        Assert.Equal(2.31, Contrast.Ratio(Srgb.Parse("#df8e1d"), Srgb.Parse("#eff1f5")), 2);
        Assert.Equal(21.0, Contrast.Ratio(new Srgb(0, 0, 0), new Srgb(255, 255, 255)), 2);
        Assert.Equal(1.0, Contrast.Ratio(new Srgb(18, 52, 86), new Srgb(18, 52, 86)), 5);
    }

    [Fact]
    public void Raw_Latte_Accents_Are_Unusable_As_Text()
    {
        var window = Palette[ThemeFlavor.Latte, "base"];
        var card = Palette[ThemeFlavor.Latte, "surface0"];

        var passOnWindow = CatppuccinPalette.AccentNames
            .Where(a => Contrast.Ratio(Palette[ThemeFlavor.Latte, a], window) >= Contrast.TextMinimum)
            .ToList();

        // Only these two clear 4.5:1 even on the lightest surface.
        Assert.Equal(["mauve", "red"], passOnWindow);

        // And on a card, nothing does. This is why PrimaryText exists.
        Assert.DoesNotContain(
            CatppuccinPalette.AccentNames,
            a => Contrast.Ratio(Palette[ThemeFlavor.Latte, a], card) >= Contrast.TextMinimum);
    }

    [Fact]
    public void Raw_Latte_Accents_Mostly_Fail_The_Indicator_Floor()
    {
        var surfaces = Palette.TextBearingSurfaces(ThemeFlavor.Latte);

        var belowFloor = CatppuccinPalette.AccentNames
            .Count(a => Contrast.MinimumRatio(Palette[ThemeFlavor.Latte, a], surfaces)
                        < Contrast.NonTextMinimum);

        // 11 of 14 fail even 3:1, so a raw-accent focus ring is not perceivable in
        // the light theme. FocusRing and BorderStrong therefore take PrimaryText.
        Assert.Equal(11, belowFloor);
    }

    [Theory]
    [InlineData(ThemeFlavor.Latte)]
    [InlineData(ThemeFlavor.Mocha)]
    public void Derived_PrimaryText_Meets_Text_Minimum_On_Every_Surface(ThemeFlavor flavour)
    {
        var surfaces = Palette.TextBearingSurfaces(flavour);

        foreach (var accent in CatppuccinPalette.AccentNames)
        {
            var derived = Contrast.DeriveLegible(
                Palette[flavour, accent], surfaces, Contrast.TextMinimum);

            Assert.True(derived.HasValue, $"{flavour}/{accent} could not be derived.");
            Assert.True(
                Contrast.MinimumRatio(derived.Value, surfaces) >= Contrast.TextMinimum,
                $"{flavour}/{accent} derived to {derived} which is still below 4.5:1.");
        }
    }

    [Fact]
    public void Mocha_Accents_Need_No_Derivation()
    {
        var surfaces = Palette.TextBearingSurfaces(ThemeFlavor.Mocha);

        foreach (var accent in CatppuccinPalette.AccentNames)
        {
            var raw = Palette[ThemeFlavor.Mocha, accent];
            var derived = Contrast.DeriveLegible(raw, surfaces, Contrast.TextMinimum);

            Assert.Equal(raw, derived);
        }
    }

    [Fact]
    public void OnAccentForeground_Always_Meets_Text_Minimum()
    {
        var worst = double.MaxValue;

        foreach (var flavour in Enum.GetValues<ThemeFlavor>())
        {
            foreach (var accent in CatppuccinPalette.AccentNames)
            {
                var fill = Palette[flavour, accent];
                var ratio = Contrast.Ratio(fill, Contrast.OnAccentForeground(fill));

                Assert.True(ratio >= Contrast.TextMinimum, $"{flavour}/{accent} is {ratio:F2}:1.");
                worst = Math.Min(worst, ratio);
            }
        }

        // Latte blue is the binding case at 4.91:1. If this drifts materially, the
        // pure-endpoint decision needs revisiting.
        Assert.Equal(4.91, worst, 2);
    }

    [Fact]
    public void Palette_Neutral_Endpoints_Would_Not_Have_Worked()
    {
        // Why OnPrimaryForeground uses pure white and black rather than base/crust:
        // against the flavour's own neutrals, most Latte accents fail outright.
        var basecolour = Palette[ThemeFlavor.Latte, "base"];
        var crust = Palette[ThemeFlavor.Latte, "crust"];

        var failures = CatppuccinPalette.AccentNames.Count(a =>
        {
            var fill = Palette[ThemeFlavor.Latte, a];
            return Math.Max(Contrast.Ratio(fill, basecolour), Contrast.Ratio(fill, crust))
                   < Contrast.TextMinimum;
        });

        Assert.Equal(12, failures);
    }

    [Fact]
    public void MutedForeground_Requires_Derivation_In_Latte_Only()
    {
        var latte = Palette.TextBearingSurfaces(ThemeFlavor.Latte);
        var mocha = Palette.TextBearingSurfaces(ThemeFlavor.Mocha);

        var rawLatte = Palette[ThemeFlavor.Latte, "subtext1"];
        var rawMocha = Palette[ThemeFlavor.Mocha, "subtext1"];

        // Raw Latte subtext1 reaches only ~4.05:1 on a card, short of 4.5:1.
        Assert.True(Contrast.MinimumRatio(rawLatte, latte) < Contrast.TextMinimum);
        Assert.True(
            Contrast.MinimumRatio(
                Contrast.DeriveLegible(rawLatte, latte, Contrast.TextMinimum)!.Value, latte)
            >= Contrast.TextMinimum);

        // Mocha passes untouched.
        Assert.Equal(rawMocha, Contrast.DeriveLegible(rawMocha, mocha, Contrast.TextMinimum));
    }

    [Fact]
    public void SubtleForeground_Clears_The_NonText_Floor_Untouched()
    {
        foreach (var flavour in Enum.GetValues<ThemeFlavor>())
        {
            var ratio = Contrast.MinimumRatio(
                Palette[flavour, "subtext0"], Palette.TextBearingSurfaces(flavour));

            Assert.True(ratio >= Contrast.NonTextMinimum, $"{flavour} subtext0 is {ratio:F2}:1.");
        }
    }

    [Fact]
    public void Semantic_Token_Names_Are_Unique()
    {
        Assert.Equal(SemanticTokens.All.Count, SemanticTokens.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(SemanticTokens.RequireTextContrast, t => Assert.Contains(t, SemanticTokens.All));
    }
}

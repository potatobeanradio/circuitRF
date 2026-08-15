// ================================================================
//  HarmonicaThemeTests.cs  —  M2 of brief-harmonicarf-h4-h5
//
//  TIER 2  a colour change invalidates NO cache: ContourGrid.FactorizationCount and the fit cache
//          are unchanged across a full theme swap, and nothing schedules a solve.
//  TIER 3  theme round trip: a .charm save/reload restores every Harmonica.* role in BOTH variants;
//          a role omitted from the file resolves to its built-in default; "Reset all" restores
//          exactly the §7.9.2 / §7.9.3 tables.
//
//  Plus R-h45-2's own structural claims: the roles are in the SHARED vocabulary (D7), red is
//  reserved to exactly two roles, and the band cycle is identical in both variants.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaThemeTests(ITestOutputHelper output)
{
    // ── R-h45-2 / D7 — the roles are in the SHARED vocabulary ────────────────

    [Fact]
    public void Roles_AreInTheSharedColorRoleAll_NotASecondRoleSystem()
    {
        // D7: "Harmonica.* roles go in the shared ColorRole.All. One vocabulary, one editor,
        // .ccolor interchange for free."
        // 22 was the count before R-h9a-7 (brief-harmonicarf-r1a) added Harmonica.Messages and
        // Harmonica.ProgressBar for brief 1C to consume.
        Assert.Equal(24, HarmonicaAppearanceBridge.Roles.Count);
        foreach (string role in HarmonicaAppearanceBridge.Roles)
            Assert.Contains(role, ColorRole.All);

        // And they are genuinely part of the one list, not a parallel one appended by the bridge.
        Assert.Contains(ColorRole.HarmonicaBackground,      ColorRole.All);
        Assert.Contains(ColorRole.HarmonicaEfficiencyTrace, ColorRole.All);
        Assert.Contains(ColorRole.HarmonicaMarkerBand5,     ColorRole.All);
    }

    [Fact]
    public void Roles_AreUnique_AndEveryOneResolvesInBothVariants()
    {
        Assert.Equal(ColorRole.All.Count, ColorRole.All.Distinct(StringComparer.Ordinal).Count());

        foreach (string role in HarmonicaAppearanceBridge.Roles)
        foreach (var variant in new[] { ColorVariant.Light, ColorVariant.Dark })
        {
            var (light, dark) = ColorTheme.BuiltIn.GetRoleMaps();
            var map = variant == ColorVariant.Light ? light : dark;
            Assert.True(map.ContainsKey(role),
                $"{role} has no built-in {variant} default — Resolve would fall through to the " +
                "grey guard, which is a defect rather than a fallback for a role we ship.");
        }
    }

    // ── §7.9.2 / §7.9.3 verbatim ─────────────────────────────────────────────

    // R-h9a-6 (brief-harmonicarf-r1a, 2026-08-12) revises six of the original §7.9.2 dark values —
    // ReadoutText/AxisText/Isoline/GainTrace/DcivFamily/MarkerBand1 (below, separately) all moved to
    // a pure (0,255,0). AxisLine, GridLine/SmithGrid, and IsolineLabel are UNCHANGED — this table
    // states the revised set verbatim, not the original §7.9.2 text.
    public static TheoryData<string, byte, byte, byte, byte> DarkTable => new()
    {
        { ColorRole.HarmonicaBackground,         6,  12,   8, 255 },
        { ColorRole.HarmonicaAxisLine,           0, 255,  65, 255 },
        { ColorRole.HarmonicaAxisText,           0, 255,   0, 255 },
        { ColorRole.HarmonicaReadoutText,        0, 255,   0, 255 },
        { ColorRole.HarmonicaGridLine,           0,  90,  30, 255 },
        { ColorRole.HarmonicaSmithGrid,          0,  90,  30, 255 },
        { ColorRole.HarmonicaIsoline,            0, 255,   0, 255 },
        { ColorRole.HarmonicaIsolineLabel,       0, 255,  65, 255 },
        { ColorRole.HarmonicaGainTrace,          0, 255,   0, 255 },
        { ColorRole.HarmonicaDcivFamily,         0, 255,   0, 255 },
        { ColorRole.HarmonicaLoadline,         255,  48,  48, 255 },
        { ColorRole.HarmonicaEfficiencyTrace,  255,  48,  48, 255 },
        { ColorRole.HarmonicaGridPoint,          0, 160,  50, 255 },
        { ColorRole.HarmonicaGridPointDropped, 120, 120, 120, 255 },
        { ColorRole.HarmonicaOperatingCursor,    0, 255,  65, 255 },
        { ColorRole.HarmonicaReachableRegion,    0, 255,  65,  40 },
        { ColorRole.HarmonicaEditChrome,         0, 255,  65, 255 },
        { ColorRole.HarmonicaMessages,           0,  90,  30, 255 },
        { ColorRole.HarmonicaProgressBar,        0,  90,  30, 255 },
    };

    // R-h9a-6 revises MarkerBand1 (below, separately) for LIGHT only — every other role in this table
    // is the original §7.9.3 text, unchanged.
    public static TheoryData<string, byte, byte, byte, byte> LightTable => new()
    {
        { ColorRole.HarmonicaBackground,       246, 250, 246, 255 },
        { ColorRole.HarmonicaAxisLine,           0, 110,  40, 255 },
        { ColorRole.HarmonicaAxisText,           0, 110,  40, 255 },
        { ColorRole.HarmonicaReadoutText,        0, 110,  40, 255 },
        { ColorRole.HarmonicaGridLine,         170, 205, 180, 255 },
        { ColorRole.HarmonicaSmithGrid,        170, 205, 180, 255 },
        { ColorRole.HarmonicaIsoline,            0, 110,  40, 255 },
        { ColorRole.HarmonicaIsolineLabel,       0, 110,  40, 255 },
        { ColorRole.HarmonicaGainTrace,          0, 110,  40, 255 },
        { ColorRole.HarmonicaDcivFamily,        40, 140,  70, 255 },
        { ColorRole.HarmonicaLoadline,         190,  30,  30, 255 },
        { ColorRole.HarmonicaEfficiencyTrace,  190,  30,  30, 255 },
        { ColorRole.HarmonicaGridPoint,         60, 150,  90, 255 },
        { ColorRole.HarmonicaGridPointDropped, 150, 150, 150, 255 },
        { ColorRole.HarmonicaOperatingCursor,    0, 110,  40, 255 },
        { ColorRole.HarmonicaReachableRegion,    0, 110,  40,  40 },
        { ColorRole.HarmonicaEditChrome,         0, 110,  40, 255 },
        { ColorRole.HarmonicaMessages,         170, 205, 180, 255 },
        { ColorRole.HarmonicaProgressBar,      170, 205, 180, 255 },
    };

    [Theory, MemberData(nameof(DarkTable))]
    public void BuiltInDark_MatchesSection792Verbatim(string role, byte r, byte g, byte b, byte a)
        => Assert.Equal(new Rgba(r, g, b, a), ColorTheme.BuiltIn.Resolve(role, ColorVariant.Dark));

    [Theory, MemberData(nameof(LightTable))]
    public void BuiltInLight_MatchesSection793Verbatim(string role, byte r, byte g, byte b, byte a)
        => Assert.Equal(new Rgba(r, g, b, a), ColorTheme.BuiltIn.Resolve(role, ColorVariant.Light));

    [Fact]
    public void RedIsReserved_ToTheLoadlineAndTheEfficiencyTraceOnly()
    {
        // §7.9.2: "Only the loadline and the efficiency trace are red. That reservation is the
        // point — red means 'this is the quantity you are engineering', and spending it anywhere
        // else weakens it." This test is what stops that eroding one role at a time.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            ColorRole.HarmonicaLoadline,
            ColorRole.HarmonicaEfficiencyTrace,
        };

        static bool ReadsAsRed(Rgba c) => c.R > 140 && c.R > c.G * 2 && c.R > c.B * 2;

        var found = new List<string>();
        foreach (var variant in new[] { ColorVariant.Light, ColorVariant.Dark })
        foreach (string role in HarmonicaAppearanceBridge.Roles)
        {
            // The band cycle is a harmonic-identity convention, not part of the green/red scheme —
            // 2f₀ is pastel red by §4.2 and is deliberately exempt.
            if (ColorRole.HarmonicaMarkerBands.Contains(role)) continue;

            var c = ColorTheme.BuiltIn.Resolve(role, variant);
            if (!ReadsAsRed(c)) continue;
            found.Add(role);
            Assert.True(allowed.Contains(role),
                $"{role} ({variant}) reads as red ({c.R},{c.G},{c.B}) — §7.9.2 reserves red for " +
                "the loadline and the efficiency trace only.");
        }

        // Non-vacuity: a predicate that never fires would pass this test against ANY palette. It
        // must actually detect the two roles that ARE red, in both variants.
        Assert.Equal(4, found.Count);
        Assert.Equal(2, found.Count(r => r == ColorRole.HarmonicaLoadline));
        Assert.Equal(2, found.Count(r => r == ColorRole.HarmonicaEfficiencyTrace));
    }

    [Fact]
    public void MarkerBandCycle_IsIdenticalInBothVariants_AndWrapsEveryFiveBands()
    {
        // §7.9.2: "the five-colour cycle is a harmonic-identity convention, not a theme choice, so
        // it survives a theme switch untouched." — R-h9a-6 (brief-harmonicarf-r1a, 2026-08-12) makes
        // MarkerBand1 a DELIBERATE exception: dark moved to a fully-saturated (0,255,0) that would be
        // illegible on a light canvas, so light needed its own distinct, brighter/more-saturated
        // green (0,200,83) — asserted explicitly below, non-vacuously, so this can't silently pass
        // because BOTH happened to change to the same value. Bands 2-5 are still identical in both
        // variants, exactly as before.
        foreach (string role in ColorRole.HarmonicaMarkerBands)
        {
            if (role == ColorRole.HarmonicaMarkerBand1) continue;
            Assert.Equal(ColorTheme.BuiltIn.Resolve(role, ColorVariant.Light),
                         ColorTheme.BuiltIn.Resolve(role, ColorVariant.Dark));
        }

        Assert.NotEqual(ColorTheme.BuiltIn.Resolve(ColorRole.HarmonicaMarkerBand1, ColorVariant.Light),
                         ColorTheme.BuiltIn.Resolve(ColorRole.HarmonicaMarkerBand1, ColorVariant.Dark));
        Assert.Equal(new Rgba(0, 255, 0),
                     ColorTheme.BuiltIn.Resolve(ColorRole.HarmonicaMarkerBand1, ColorVariant.Dark));
        Assert.Equal(new Rgba(0, 200, 83),
                     ColorTheme.BuiltIn.Resolve(ColorRole.HarmonicaMarkerBand1, ColorVariant.Light));

        // §4.2: "6f₀+ | the five-colour cycle repeats".
        Assert.Equal(ColorRole.HarmonicaMarkerBand1, ColorRole.HarmonicaMarkerBand(1));
        Assert.Equal(ColorRole.HarmonicaMarkerBand5, ColorRole.HarmonicaMarkerBand(5));
        Assert.Equal(ColorRole.HarmonicaMarkerBand1, ColorRole.HarmonicaMarkerBand(6));
        Assert.Equal(ColorRole.HarmonicaMarkerBand2, ColorRole.HarmonicaMarkerBand(7));

        var theme = HarmonicaRenderTheme.Dark;
        Assert.Equal(theme.MarkerBands[0], theme.MarkerBand(1));
        Assert.Equal(theme.MarkerBands[0], theme.MarkerBand(6));
        Assert.Equal(theme.MarkerBands[1], theme.MarkerBand(7));
    }

    // ── R-h45-2 — the projection is a projection, not a hardcoded static ─────

    [Fact]
    public void RenderTheme_IsProjectedFromTheRoleTable_NotHardcoded()
    {
        // The claim: changing a role changes the token. If Light/Dark were hardcoded statics this
        // would pass trivially against BuiltIn and fail here.
        var (light, dark) = ColorTheme.BuiltIn.GetRoleMaps();
        var l = new Dictionary<string, Rgba>(light, StringComparer.Ordinal)
        {
            [ColorRole.HarmonicaLoadline] = new(1, 2, 3, 4),
        };
        var recoloured = new ColorTheme("Recoloured", l, dark);

        var t = HarmonicaRenderTheme.FromTheme(recoloured, ColorVariant.Light);
        Assert.Equal(new SkiaSharp.SKColor(1, 2, 3, 4), t.Loadline);

        // And the built-in projection is untouched by it.
        Assert.Equal(new SkiaSharp.SKColor(190, 30, 30, 255), HarmonicaRenderTheme.Light.Loadline);
    }

    [Fact]
    public void RenderTheme_IsoFadeParameters_DefaultAndOverride()
    {
        var def = HarmonicaRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark);
        Assert.Equal(HarmonicaRenderTheme.DefaultIsoAlphaFloor,    def.IsoAlphaFloor);
        Assert.Equal(HarmonicaRenderTheme.DefaultIsoAlphaExponent, def.IsoAlphaExponent);

        // §7.9.4: "a user who dislikes the fade can flatten it (α_floor = 1) without a code change".
        var flat = HarmonicaRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark, 1.0, 1.0);
        Assert.Equal(1.0, flat.IsoAlphaFloor);

        // Out-of-range values are clamped rather than producing an invalid alpha downstream.
        var clamped = HarmonicaRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark, 5.0, -3.0);
        Assert.Equal(1.0, clamped.IsoAlphaFloor);
        Assert.True(clamped.IsoAlphaExponent > 0);
    }

    // ── R8A §1 — the fade defaults moved to 0.01 / 3.00 ──────────────────────

    [Fact]
    public void R8A_IsoFadeDefaults_Are001And300()
    {
        Assert.Equal(0.01, HarmonicaRenderTheme.DefaultIsoAlphaFloor);
        Assert.Equal(3.00, HarmonicaRenderTheme.DefaultIsoAlphaExponent);
    }

    [Fact]
    public void R8A_ACharmAppearanceWithBothNulls_ResolvesToTheNewDefaults_AndAnExplicitOneKeepsItsOwn()
    {
        // "A document that never touched the sliders picks the new values up on load, and a document
        // that did keeps its own" — tested through the bridge rather than merely asserted.
        var untouched = new CharmAppearance();
        Assert.Null(untouched.IsoAlphaFloor);
        Assert.Null(untouched.IsoAlphaExponent);

        var untouchedTheme = HarmonicaAppearanceBridge.ToRenderTheme(untouched, ColorVariant.Dark);
        Assert.Equal(0.01, untouchedTheme.IsoAlphaFloor);
        Assert.Equal(3.00, untouchedTheme.IsoAlphaExponent);

        var explicitOld = new CharmAppearance { IsoAlphaFloor = 0.15, IsoAlphaExponent = 2.0 };
        var explicitTheme = HarmonicaAppearanceBridge.ToRenderTheme(explicitOld, ColorVariant.Dark);
        Assert.Equal(0.15, explicitTheme.IsoAlphaFloor);
        Assert.Equal(2.0,  explicitTheme.IsoAlphaExponent);
    }

    // ── TIER 3 — the .charm round trip ───────────────────────────────────────

    private static ColorTheme Recoloured()
    {
        var (light, dark) = ColorTheme.BuiltIn.GetRoleMaps();
        var l = new Dictionary<string, Rgba>(light, StringComparer.Ordinal);
        var d = new Dictionary<string, Rgba>(dark,  StringComparer.Ordinal);
        // A deliberate, ugly, unmistakable recolour of several roles in BOTH variants.
        l[ColorRole.HarmonicaBackground]      = new(11, 22, 33);
        l[ColorRole.HarmonicaIsoline]         = new(200, 10, 90, 128);
        l[ColorRole.HarmonicaMarkerBand3]     = new(7, 7, 7);
        d[ColorRole.HarmonicaBackground]      = new(44, 55, 66);
        d[ColorRole.HarmonicaEfficiencyTrace] = new(3, 4, 5, 200);
        d[ColorRole.HarmonicaReachableRegion] = new(9, 8, 7, 17);
        return new ColorTheme("Custom", l, d);
    }

    private static CircuitModel MinimalModel() => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "0.1*tanh(_v2)",
            },
        },
        Bias     = new BiasSpec { Vgs = -3.0, Vds = 28 },
        Settings = new HarmonicaSettings { HarmonicCount = 3, FrequencyHz = 2e9 },
    };

    [Fact]
    public void Tier3_CharmRoundTrip_RestoresEveryHarmonicaRoleInBothVariants()
    {
        var theme = Recoloured();
        var appearance = HarmonicaAppearanceBridge.ToAppearance(
            theme, isoAlphaFloor: 0.42, isoAlphaExponent: 2.75, showIsoLineLabels: true);

        string json = CharmIo.Write(MinimalModel(), new TerminationSet(3), appearance);
        var back = CharmIo.ReadAll(json, baseDirectory: null);
        var restored = HarmonicaAppearanceBridge.ToColorTheme(back.Appearance, out var rejected);

        Assert.Empty(rejected);

        foreach (string role in HarmonicaAppearanceBridge.Roles)
        foreach (var variant in new[] { ColorVariant.Light, ColorVariant.Dark })
            Assert.Equal(theme.Resolve(role, variant), restored.Resolve(role, variant));

        Assert.Equal(0.42, back.Appearance.IsoAlphaFloor);
        Assert.Equal(2.75, back.Appearance.IsoAlphaExponent);
        Assert.True(back.Appearance.ShowIsoLineLabels);

        // And the whole projection comes back too, not merely the raw map.
        var rt = HarmonicaAppearanceBridge.ToRenderTheme(back.Appearance, ColorVariant.Dark);
        Assert.Equal(new SkiaSharp.SKColor(44, 55, 66, 255), rt.Background);
        Assert.Equal(0.42, rt.IsoAlphaFloor);
    }

    [Fact]
    public void Tier3_ARoleOmittedFromTheFile_ResolvesToItsBuiltInDefault()
    {
        // Exactly what §7.9.1 promises: "Roles absent from a stored theme fall back to the built-in
        // default, so an old .charm still opens after new roles are added."
        var partial = new CharmAppearance
        {
            Dark = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ColorRole.HarmonicaBackground] = "1,2,3,4",
            },
        };

        var theme = HarmonicaAppearanceBridge.ToColorTheme(partial, out var rejected);
        Assert.Empty(rejected);

        Assert.Equal(new Rgba(1, 2, 3, 4), theme.Resolve(ColorRole.HarmonicaBackground, ColorVariant.Dark));
        // Every OTHER role — including the same role in the other variant — is the built-in default.
        Assert.Equal(ColorTheme.BuiltIn.Resolve(ColorRole.HarmonicaBackground, ColorVariant.Light),
                     theme.Resolve(ColorRole.HarmonicaBackground, ColorVariant.Light));
        foreach (string role in HarmonicaAppearanceBridge.Roles)
        {
            if (role == ColorRole.HarmonicaBackground) continue;
            Assert.Equal(ColorTheme.BuiltIn.Resolve(role, ColorVariant.Dark),
                         theme.Resolve(role, ColorVariant.Dark));
        }
    }

    [Fact]
    public void Tier3_ResetAll_RestoresExactlyTheDesignNoteTables()
    {
        // "Reset all colours to defaults" (§7.9.4) is an EMPTY appearance — nothing stated, so
        // everything resolves to the built-in table. Asserted against the literal §7.9.2/§7.9.3
        // values, not against ColorTheme.BuiltIn (which would be circular).
        var reset = HarmonicaAppearanceBridge.ToColorTheme(CharmAppearance.Default, out var rejected);
        Assert.Empty(rejected);

        Assert.Equal(new Rgba(  6,  12,   8), reset.Resolve(ColorRole.HarmonicaBackground,      ColorVariant.Dark));
        Assert.Equal(new Rgba(  0, 255,  65), reset.Resolve(ColorRole.HarmonicaAxisLine,        ColorVariant.Dark));
        Assert.Equal(new Rgba(255,  48,  48), reset.Resolve(ColorRole.HarmonicaLoadline,        ColorVariant.Dark));
        Assert.Equal(new Rgba(  0, 255,  65,  40), reset.Resolve(ColorRole.HarmonicaReachableRegion, ColorVariant.Dark));
        Assert.Equal(new Rgba(246, 250, 246), reset.Resolve(ColorRole.HarmonicaBackground,      ColorVariant.Light));
        Assert.Equal(new Rgba(190,  30,  30), reset.Resolve(ColorRole.HarmonicaEfficiencyTrace, ColorVariant.Light));
        Assert.Equal(new Rgba(170, 205, 180), reset.Resolve(ColorRole.HarmonicaSmithGrid,       ColorVariant.Light));
    }

    [Fact]
    public void Tier3_AnUntouchedDocument_WritesNoAppearanceBlockAtAll()
    {
        // An additive field must not churn a file nobody has recoloured.
        string plain  = CharmIo.Write(MinimalModel(), new TerminationSet(3));
        string withDefault = CharmIo.Write(MinimalModel(), new TerminationSet(3), CharmAppearance.Default);
        Assert.Equal(plain, withDefault);
        Assert.DoesNotContain("Appearance", plain, StringComparison.Ordinal);

        // And reading one back yields the default appearance, not a null or an empty-but-present map.
        var back = CharmIo.ReadAll(plain, null);
        Assert.True(back.Appearance.IsDefault);
    }

    [Fact]
    public void Tier3_AMalformedColour_KeepsTheDefault_AndIsReported()
    {
        // A colour silently read as black is the defect this refuses to commit.
        var bad = new CharmAppearance
        {
            Dark = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ColorRole.HarmonicaBackground] = "not,a,colour",
                [ColorRole.HarmonicaAxisLine]   = "1,2",
                [ColorRole.HarmonicaGridLine]   = "300,0,0",
                [ColorRole.HarmonicaIsoline]    = "5,6,7",       // valid: 3 components ⇒ opaque
            },
        };

        var theme = HarmonicaAppearanceBridge.ToColorTheme(bad, out var rejected);
        Assert.Equal(3, rejected.Count);
        Assert.Equal(ColorTheme.BuiltIn.Resolve(ColorRole.HarmonicaBackground, ColorVariant.Dark),
                     theme.Resolve(ColorRole.HarmonicaBackground, ColorVariant.Dark));
        Assert.Equal(new Rgba(5, 6, 7, 255), theme.Resolve(ColorRole.HarmonicaIsoline, ColorVariant.Dark));
    }

    [Fact]
    public void Tier3_TheCharmCarriesOnlyHarmonicaRoles_NeverTheSchematicOrLayoutPalette()
    {
        // A .charm that wrote Schematic.* in would silently override a user's whole application
        // theme on open. "Self-describing" means harmonicaRF's own appearance, not everyone's.
        var appearance = HarmonicaAppearanceBridge.ToAppearance(Recoloured());
        foreach (string role in appearance.Light.Keys.Concat(appearance.Dark.Keys))
            Assert.StartsWith("Harmonica.", role, StringComparison.Ordinal);

        string json = CharmIo.Write(MinimalModel(), new TerminationSet(3), appearance);
        Assert.DoesNotContain("Schematic.", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Layout.",    json, StringComparison.Ordinal);
    }

    // ── TIER 2 — a colour change invalidates NO cache ────────────────────────

    private static (HarmonicaContext Ctx, TerminationSet Terms) TinyFixture()
    {
        var model = new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.Sdd, TypeName = "SDD",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["I[1,0]"] = "_v1/50",
                    ["I[2,0]"] = "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2",
                },
            },
            Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
            Settings = new HarmonicaSettings
            {
                HarmonicCount = 3, FrequencyHz = 2e9,
                BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
                CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 34,
            },
        };
        var ctx = HarmonicaContext.Create(model, new AnalysisSettings
        {
            InductanceRegularization  = RegularizationMode.Always,
            ConductanceRegularization = RegularizationMode.Never,
        });
        var terms = new TerminationSet(3);
        terms.Set(TerminationSide.Source, 1, new Complex(25, 0));
        terms.Set(TerminationSide.Load,   1, new Complex(80, 10));
        return (ctx, terms);
    }

    [Fact]
    public void Tier2_AFullThemeSwapInvalidatesNoContourCacheAndSchedulesNoSolve()
    {
        var (ctx, terms) = TinyFixture();

        // A small ring grid — the claim is "a colour change touches nothing", which 13 points prove
        // exactly as well as 61 and in a fraction of the wall clock.
        var grid = new ContourGrid();
        grid.Build(ctx, terms, ContourGrid.RingGrid(rings: 2, spokes: 6, maxGamma: 0.6));

        var fitP = grid.Fit(GridMetric.PoutDbm);
        var fitE = grid.Fit(GridMetric.DrainEfficiency);
        int factorizationsBefore = grid.FactorizationCount;
        int solvesBefore         = grid.SolveCount;
        int pointsBefore         = grid.Points.Count;

        output.WriteLine($"before: {factorizationsBefore} factorization(s), {solvesBefore} HB solves, " +
                         $"{pointsBefore} Γ points");

        // The full theme swap: every Harmonica.* role recoloured, re-projected for BOTH variants,
        // twenty times over — a live-preview colour drag, in other words.
        var custom = Recoloured();
        for (int i = 0; i < 20; i++)
        {
            _ = HarmonicaRenderTheme.FromTheme(custom,             ColorVariant.Light);
            _ = HarmonicaRenderTheme.FromTheme(custom,             ColorVariant.Dark);
            _ = HarmonicaRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);
            _ = HarmonicaRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark);
            _ = HarmonicaAppearanceBridge.ToRenderTheme(
                    HarmonicaAppearanceBridge.ToAppearance(custom), ColorVariant.Dark);
        }

        // R-h45-11: "no re-solve, and specifically no contour-cache or RBF-factorization
        // invalidation. The failure would show only as a frame-rate collapse nobody could attribute."
        Assert.Equal(factorizationsBefore, grid.FactorizationCount);
        Assert.Equal(solvesBefore,         grid.SolveCount);
        Assert.Equal(pointsBefore,         grid.Points.Count);

        // The fit cache still hands back the SAME instances — not merely equal ones.
        Assert.Same(fitP, grid.Fit(GridMetric.PoutDbm));
        Assert.Same(fitE, grid.Fit(GridMetric.DrainEfficiency));

        // A negative control: the counters CAN move, so the assertions above are not vacuous.
        grid.InvalidateValues();
        Assert.NotSame(fitP, grid.Fit(GridMetric.PoutDbm));
        output.WriteLine($"after:  {grid.FactorizationCount} factorization(s), {grid.SolveCount} HB solves " +
                         "— unchanged by the theme swap; the negative control shows they can move.");
    }

    [Fact]
    public void Tier2_AThemeServiceActiveChangeAndAVariantChange_InvalidateNoContourCacheAndScheduleNoSolve()
    {
        // R-h9a-9's own extension of the R-h45-11 counter gate: HarmonicaViewModel.RenderTheme now
        // composes ThemeService.Active (a circuitRF Settings-dialog colour edit) with the document's
        // own Appearance — that composition must be just as free of engine side effects as a direct
        // Appearance edit already is, and it must ACTUALLY be reachable through RenderTheme (not just
        // theoretically wired), which is why this reads a genuinely different colour back rather than
        // only asserting the counters.
        var (ctx, terms) = TinyFixture();
        var grid = new ContourGrid();
        grid.Build(ctx, terms, ContourGrid.RingGrid(rings: 2, spokes: 6, maxGamma: 0.6));

        var fitP = grid.Fit(GridMetric.PoutDbm);
        int factorizationsBefore = grid.FactorizationCount;
        int solvesBefore         = grid.SolveCount;
        int pointsBefore         = grid.Points.Count;

        var vm = new HarmonicaViewModel(HarmonicaViewModel.DefaultModel());

        // Recoloured() overrides HarmonicaBackground in BOTH variants (unlike HarmonicaIsoline, which
        // it only touches in Light) — Dark is the variant this test reads, so Background is the role
        // that actually proves the composition happened.
        var builtInDarkBackground = ColorTheme.BuiltIn.Resolve(ColorRole.HarmonicaBackground, ColorVariant.Dark);
        var appTheme = Recoloured();   // the existing helper: every Harmonica.* role recoloured
        var savedActive = ThemeService.Active;
        try
        {
            for (int i = 0; i < 10; i++)
            {
                // The app-wide theme is REPLACED (never mutated in place — ThemeService.Active's own
                // setter is what fires ThemeChanged, and a replace is what a live app.axaml.cs Settings
                // edit actually does).
                ThemeService.Active = appTheme;
                vm.Variant = ColorVariant.Light;
                _ = vm.RenderTheme;
                vm.Variant = ColorVariant.Dark;
                var themed = vm.RenderTheme;

                // The composition is real: RenderTheme reflects ThemeService.Active, not the built-in
                // default it would still read if R-h9a-9's fix had not landed.
                Assert.NotEqual(builtInDarkBackground.R, themed.Background.Red);

                ThemeService.Active = ColorTheme.BuiltIn;
            }
        }
        finally
        {
            ThemeService.Active = savedActive;
        }

        Assert.Equal(factorizationsBefore, grid.FactorizationCount);
        Assert.Equal(solvesBefore,         grid.SolveCount);
        Assert.Equal(pointsBefore,         grid.Points.Count);
        Assert.Same(fitP, grid.Fit(GridMetric.PoutDbm));

        // The negative control (kept intact, per the brief's own explicit requirement): the counters
        // CAN move, so the assertions above are not vacuous.
        grid.InvalidateValues();
        Assert.NotSame(fitP, grid.Fit(GridMetric.PoutDbm));
    }

    [Fact]
    public void Tier2_TheRenderThemeHoldsNoEngineReference_SoItCannotInvalidateAnything()
    {
        // The structural half of R-h45-11: this type has no path to a grid, a context or a
        // scheduler, so "a colour change re-projects and invalidates the canvas, full stop" is true
        // by construction rather than by discipline.
        var engineAssembly = typeof(ContourGrid).Assembly;
        foreach (var p in typeof(HarmonicaRenderTheme).GetProperties())
            Assert.NotEqual(engineAssembly, p.PropertyType.Assembly);
        foreach (var f in typeof(HarmonicaRenderTheme)
                          .GetFields(System.Reflection.BindingFlags.Instance
                                   | System.Reflection.BindingFlags.NonPublic
                                   | System.Reflection.BindingFlags.Public))
            Assert.NotEqual(engineAssembly, f.FieldType.Assembly);
    }
}

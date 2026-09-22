namespace CircuitRF.Render;

/// <summary>
/// Framework-free color theme: semantic roles → RGBA for light and dark variants.
/// Placed in src/Ui because it is presentation data consumed only by src/Ui today;
/// it carries no Avalonia/SkiaSharp types and could migrate to src/Core if another
/// assembly ever needs it.
///
/// Three-layer separation:
///   L1 (this class) — framework-free data model; the .ccolor file holds this.
///   L2 (SchematicRenderTheme.FromTheme) — projects roles → SKColor for the renderer.
///   L3 (AppPreferences + WorkspacePersistence) — active-theme selection and persistence.
/// </summary>
public sealed class ColorTheme
{
    public string Name { get; }

    private readonly IReadOnlyDictionary<string, Rgba> _light;
    private readonly IReadOnlyDictionary<string, Rgba> _dark;

    public ColorTheme(
        string name,
        IReadOnlyDictionary<string, Rgba> light,
        IReadOnlyDictionary<string, Rgba> dark)
    {
        Name   = name;
        _light = light;
        _dark  = dark;
    }

    /// <summary>
    /// Returns the RGBA for <paramref name="role"/> in the given variant,
    /// falling back to <see cref="BuiltIn"/> for any role absent from this theme
    /// (so partial or old .ccolor files load without hard-failing on missing roles).
    /// </summary>
    public Rgba Resolve(string role, ColorVariant variant)
    {
        var dict = variant == ColorVariant.Dark ? _dark : _light;
        if (dict.TryGetValue(role, out var color)) return color;
        if (!ReferenceEquals(this, BuiltIn)) return BuiltIn.Resolve(role, variant);
        return new Rgba(128, 128, 128);   // guard: built-in should be complete
    }

    /// <summary>Exposes the raw role maps so L2 and ColorThemeIo can iterate them.</summary>
    public (IReadOnlyDictionary<string, Rgba> Light, IReadOnlyDictionary<string, Rgba> Dark) GetRoleMaps()
        => (_light, _dark);

    // ── Built-in default — single source of truth for missing-role fallback ──────────────

    /// <summary>
    /// The built-in default palette (from the color-themes.md table).
    /// Shipped as Default.ccolor in /Assets/Color and as this in-code fallback so the app
    /// always has valid colors even if no files are found.
    /// </summary>
    public static readonly ColorTheme BuiltIn = new("Default",
        new Dictionary<string, Rgba>
        {
            [ColorRole.SchematicBackground]        = new(250, 250, 250),
            [ColorRole.SchematicGrid]              = new(170, 170, 170,  70),
            [ColorRole.SchematicWire]              = new(164,  63, 129),
            [ColorRole.SchematicWireRouting]       = new(164,  63, 129),
            [ColorRole.SchematicNodeLabelText]     = new(164,  63, 129),
            [ColorRole.SchematicInstanceNameText]  = new( 59,  28, 243),
            [ColorRole.SchematicParameterNameText] = new( 24,   8, 122),
            [ColorRole.SchematicComponentNameText] = new(106, 142, 246),
            [ColorRole.SchematicConnectedPin]      = new( 94, 105, 216),
            [ColorRole.SchematicWireJunctionDot]   = new( 59,  28, 243),
            [ColorRole.SchematicSymbolLine]        = new( 45,  20, 195),
            [ColorRole.SchematicSymbolPlus]        = new(210,  99,  40),
            [ColorRole.SystemWarning]              = new(206,  74,  36),
            [ColorRole.LayoutBackground]           = new(246, 246, 244),
            [ColorRole.LayoutGridMinor]             = new(120, 120, 120,  60),
            [ColorRole.LayoutGridMajor]             = new( 90,  90,  90, 110),
            [ColorRole.LayoutRulerBackground]      = new(232, 232, 228),
            [ColorRole.LayoutRulerText]            = new( 60,  60,  60),
            [ColorRole.LayoutRulerTick]             = new(120, 120, 120),
            [ColorRole.LayoutCursorIndicator]       = new(206,  74,  36),
            [ColorRole.LayoutSelection]              = new( 30, 110, 220),
            [ColorRole.LayoutRulerAnnotationLine]    = new( 60,  20,   0),
            [ColorRole.LayoutRulerAnnotationText]    = new( 60,  20,   0),
            [ColorRole.LayoutPCellPin]               = new(  0, 150, 110),
            [ColorRole.LayoutPCellHandle]            = new(210, 120,  20),
            [ColorRole.LayoutEmMeshConductor]        = new(210,  60,  40),
            [ColorRole.LayoutEmMeshInterface]        = new( 40, 110, 200),
            [ColorRole.LayoutEmMeshTruncation]       = new(140, 140, 140),
            [ColorRole.LayoutPlanarMeshCell]         = new( 40, 110, 200),
            [ColorRole.LayoutDrcError]               = new(220,  40,  60),
            [ColorRole.LayoutDrcWarning]             = new(230, 150,  20),
            [ColorRole.LayoutDrcWaived]              = new(130, 130, 140),

            // ── harmonicaRF, LIGHT (harmonicarf.md §7.9.3) ──────────────────────────────────────
            // "Not a recoloured dark theme: the same STRUCTURE (green primary, red reserved,
            // low-contrast grid) re-derived for a light ground, with the greens and reds darkened
            // enough to stay legible on white."
            [ColorRole.HarmonicaBackground]       = new(246, 250, 246),
            [ColorRole.HarmonicaAxisLine]         = new(  0, 110,  40),
            [ColorRole.HarmonicaAxisText]         = new(  0, 110,  40),
            [ColorRole.HarmonicaReadoutText]      = new(  0, 110,  40),
            [ColorRole.HarmonicaGridLine]         = new(170, 205, 180),
            [ColorRole.HarmonicaSmithGrid]        = new(170, 205, 180),
            [ColorRole.HarmonicaIsoline]          = new(  0, 110,  40),
            [ColorRole.HarmonicaIsolineLabel]     = new(  0, 110,  40),
            [ColorRole.HarmonicaGainTrace]        = new(  0, 110,  40),
            [ColorRole.HarmonicaDcivFamily]       = new( 40, 140,  70),
            [ColorRole.HarmonicaLoadline]         = new(190,  30,  30),   // reserved red
            [ColorRole.HarmonicaEfficiencyTrace]  = new(190,  30,  30),   // reserved red
            [ColorRole.HarmonicaGridPoint]        = new( 60, 150,  90),
            [ColorRole.HarmonicaGridPointDropped] = new(150, 150, 150),
            [ColorRole.HarmonicaOperatingCursor]  = new(  0, 110,  40),
            [ColorRole.HarmonicaReachableRegion]  = new(  0, 110,  40,  40),
            [ColorRole.HarmonicaEditChrome]       = new(  0, 110,  40),
            // R-h9a-7: created here for brief 1C (toolbar/readouts) to consume — same defaults as
            // Harmonica.GridLine/SmithGrid, since a message strip and the grid share the same
            // deliberately-low-contrast-against-Background tone.
            [ColorRole.HarmonicaMessages]         = new(170, 205, 180),
            [ColorRole.HarmonicaProgressBar]      = new(170, 205, 180),
            // R-h9a-6 (brief-harmonicarf-r1a, 2026-08-12): MarkerBand1 no longer matches the dark
            // set — dark moved to a saturated (0,255,0) that would be illegible on a light canvas,
            // so light needed its OWN brighter/more-saturated green, distinguishable from
            // Harmonica.GridPoint (60,150,90) and Harmonica.Isoline (0,110,40) here in light mode.
            // (0,200,83) is the well-known "Material Green A700" accent — vivid and saturated enough
            // to read as "the marker", while its luminance still holds up against the near-white
            // Harmonica.Background (246,250,246) the other two roles are also judged against.
            [ColorRole.HarmonicaMarkerBand1]      = new(  0, 200,  83),   // f₀   green (light-only)
            [ColorRole.HarmonicaMarkerBand2]      = new(232, 106, 106),   // 2f₀  pastel red
            [ColorRole.HarmonicaMarkerBand3]      = new(214, 178,  54),   // 3f₀  pastel yellow
            [ColorRole.HarmonicaMarkerBand4]      = new(108, 152, 226),   // 4f₀  pastel blue
            [ColorRole.HarmonicaMarkerBand5]      = new(166, 124, 214),   // 5f₀  pastel purple

            // ── wBond, LIGHT ────────────────────────────────────────────────────────────────────
            // **The former `wBond-Orchid` palette, folded in on 2026-08-17** (owner). Six alternatives
            // shipped as selectable themes to be judged side by side; one won, so it stopped being a
            // choice and became what the default IS. There is no `wBond-…` theme any more.
            //
            // The orchid is `Schematic.Wire` itself, and the vertex is `Schematic.WireJunctionDot` —
            // the pairing the owner named as working: ~72° apart on the colour wheel with the dot
            // about twice as saturated, ADJACENT rather than complementary. WireStart is the same hue
            // two steps darker (owner: "the same as the wire, but a much darker shade of it").
            // Selected is deliberately outside the hue rule — a near-black on the light ground,
            // because it is a STATE and has to be unmistakable against wire, vertex and canvas at once.
            [ColorRole.WBondWire]      = new(165,  64, 130),
            [ColorRole.WBondWireStart] = new( 84,  28,  65),
            [ColorRole.WBondWireVertex] = new( 60,  27, 243),
            [ColorRole.WBondSelected]  = new( 46,  36,  42),
            [ColorRole.WBondEnvelope]  = new(165,  64, 130,  52),

            // ── Stackup cross-section (brief-stackup-render-1-scene.md §7), LIGHT ───────────────
            // DielectricFill is the exact translucent grey DocStackupFixtures' figures already use —
            // the picture this drawing is specified against. OnBandInk is identical in both variants
            // on purpose; see ColorRole.StackupOnBandInk for why.
            [ColorRole.StackupBackground]     = new(248, 248, 246),
            [ColorRole.StackupBandEdge]       = new(110, 110, 120,  90),
            [ColorRole.StackupDielectricFill] = new(120, 130, 145,  60),
            [ColorRole.StackupGroundAccent]   = new( 30, 130, 200),
            [ColorRole.StackupLabelInk]       = new( 60,  60,  66),
            [ColorRole.StackupOnBandInk]      = new( 25,  25,  30),
            [ColorRole.StackupGripper]        = new(210, 120,  20),
            [ColorRole.StackupDragGhost]      = new( 30, 110, 220,  90),

            // Match Designer. Absorbed carries its own ALPHA rather than a lighter grey: dimming has
            // to read as dimming over whatever the preview's background happens to be.
            [ColorRole.MatchAbsorbed]  = new( 60,  60,  66, 105),
            [ColorRole.MatchNegative]  = new(198,  40,  40),
            [ColorRole.MatchBracket]   = new( 21, 101, 192),

            // ── railRF, LIGHT (railrf.md §2.4, §2.9; brief 8) ─────────────────────────────────
            // EVERY ONE OF THESE IS FULLY OPAQUE, on purpose — see ColorRole's railRF header for the
            // stackup drill-hole case that decided it. The ramp is blue → amber → red because those
            // three are the pair of endpoints a reader already reads as cold and hot with a midpoint
            // that is neither, which is what makes "where the colour changes fastest" legible.
            [ColorRole.RailMapCold]           = new( 30,  80, 170),
            [ColorRole.RailMapMid]            = new(232, 172,  44),
            [ColorRole.RailMapHot]            = new(198,  40,  40),
            [ColorRole.RailLegendBackground]  = new(255, 255, 255),
            [ColorRole.RailLegendInk]         = new( 48,  48,  54),
            [ColorRole.RailSource]            = new( 20, 130,  90),
            [ColorRole.RailLoad]              = new( 40,  90, 200),
            [ColorRole.RailViaFlag]           = new(168,  44, 140),
            [ColorRole.RailClassTrace]        = new(108, 168, 226),
            [ColorRole.RailClassSpreading]    = new(226, 160,  96),
            [ColorRole.RailClassForced]       = new(120,  40, 190),
            [ColorRole.RailCopperHighlight]   = new(  0, 140, 190),
            [ColorRole.RailPartSelection]     = new(215,  40, 170),
            [ColorRole.RailNetPreview]        = new( 20, 170, 110),
            [ColorRole.RailNotFitted]         = new(108, 112, 124),
        },
        new Dictionary<string, Rgba>
        {
            [ColorRole.SchematicBackground]        = new( 28,  28,  30),
            [ColorRole.SchematicGrid]              = new( 70,  70,  80,  70),
            [ColorRole.SchematicWire]              = new(214, 122, 178),
            [ColorRole.SchematicWireRouting]       = new(214, 122, 178),
            [ColorRole.SchematicNodeLabelText]     = new(214, 122, 178),
            [ColorRole.SchematicInstanceNameText]  = new(138, 120, 255),
            [ColorRole.SchematicParameterNameText] = new(120, 104, 230),
            [ColorRole.SchematicComponentNameText] = new(140, 174, 255),
            [ColorRole.SchematicConnectedPin]      = new(130, 145, 240),
            [ColorRole.SchematicWireJunctionDot]   = new(138, 120, 255),
            [ColorRole.SchematicSymbolLine]        = new(150, 132, 250),
            [ColorRole.SchematicSymbolPlus]        = new(245, 140,  75),
            [ColorRole.SystemWarning]              = new(240, 120,  70),
            [ColorRole.LayoutBackground]           = new( 32,  32,  34),
            [ColorRole.LayoutGridMinor]             = new(150, 150, 160,  55),
            [ColorRole.LayoutGridMajor]             = new(190, 190, 200, 100),
            [ColorRole.LayoutRulerBackground]      = new( 44,  44,  47),
            [ColorRole.LayoutRulerText]            = new(210, 210, 210),
            [ColorRole.LayoutRulerTick]             = new(150, 150, 155),
            [ColorRole.LayoutCursorIndicator]       = new(240, 120,  70),
            [ColorRole.LayoutSelection]              = new( 90, 165, 255),
            [ColorRole.LayoutRulerAnnotationLine]    = new(255, 228, 195),
            [ColorRole.LayoutRulerAnnotationText]    = new(255, 228, 195),
            [ColorRole.LayoutPCellPin]               = new( 60, 210, 170),
            [ColorRole.LayoutPCellHandle]            = new(255, 175,  60),
            [ColorRole.LayoutEmMeshConductor]        = new(255, 120,  95),
            [ColorRole.LayoutEmMeshInterface]        = new(110, 175, 255),
            [ColorRole.LayoutEmMeshTruncation]       = new(160, 160, 160),
            [ColorRole.LayoutPlanarMeshCell]         = new(110, 175, 255),
            [ColorRole.LayoutDrcError]               = new(255, 100, 120),
            [ColorRole.LayoutDrcWarning]             = new(255, 190,  70),
            [ColorRole.LayoutDrcWaived]              = new(160, 160, 175),

            // ── harmonicaRF, DARK — the phosphor-green theme (harmonicarf.md §7.9.2) ────────────
            // "Green is the default for everything textual and structural; red is reserved. Only the
            // loadline and the efficiency trace are red. That reservation is the point."
            [ColorRole.HarmonicaBackground]       = new(  6,  12,   8),   // near-black, faint green cast
            [ColorRole.HarmonicaAxisLine]         = new(  0, 255,  65),   // phosphor green (unchanged)
            // R-h9a-6 (brief-harmonicarf-r1a, 2026-08-12): these five roles + MarkerBand1 below moved
            // to a pure, fully-saturated (0,255,0) — the owner's own explicit request. AxisLine,
            // GridLine/SmithGrid, and IsolineLabel are deliberately NOT in this set and keep their
            // original phosphor tone (0,255,65) / low-contrast grid tone (0,90,30) unchanged.
            [ColorRole.HarmonicaAxisText]         = new(  0, 255,   0),
            [ColorRole.HarmonicaReadoutText]      = new(  0, 255,   0),
            [ColorRole.HarmonicaGridLine]         = new(  0,  90,  30),   // deliberately low contrast
            [ColorRole.HarmonicaSmithGrid]        = new(  0,  90,  30),
            [ColorRole.HarmonicaIsoline]          = new(  0, 255,   0),
            [ColorRole.HarmonicaIsolineLabel]     = new(  0, 255,  65),
            [ColorRole.HarmonicaGainTrace]        = new(  0, 255,   0),
            [ColorRole.HarmonicaDcivFamily]       = new(  0, 255,   0),
            [ColorRole.HarmonicaLoadline]         = new(255,  48,  48),   // reserved red
            [ColorRole.HarmonicaEfficiencyTrace]  = new(255,  48,  48),   // reserved red
            [ColorRole.HarmonicaGridPoint]        = new(  0, 160,  50),
            [ColorRole.HarmonicaGridPointDropped] = new(120, 120, 120),   // hollow, non-compressing
            [ColorRole.HarmonicaOperatingCursor]  = new(  0, 255,  65),
            [ColorRole.HarmonicaReachableRegion]  = new(  0, 255,  65,  40),
            [ColorRole.HarmonicaEditChrome]       = new(  0, 255,  65),
            // R-h9a-7: created here for brief 1C (toolbar/readouts) to consume — same defaults as
            // Harmonica.GridLine/SmithGrid (§ same reasoning as the light map above).
            [ColorRole.HarmonicaMessages]         = new(  0,  90,  30),
            [ColorRole.HarmonicaProgressBar]      = new(  0,  90,  30),
            [ColorRole.HarmonicaMarkerBand1]      = new(  0, 255,   0),
            [ColorRole.HarmonicaMarkerBand2]      = new(232, 106, 106),
            [ColorRole.HarmonicaMarkerBand3]      = new(214, 178,  54),
            [ColorRole.HarmonicaMarkerBand4]      = new(108, 152, 226),
            [ColorRole.HarmonicaMarkerBand5]      = new(166, 124, 214),

            // ── wBond, DARK ─────────────────────────────────────────────────────────────────────
            // The dark half of the same folded-in orchid palette — see the light variant's note. Each
            // role is its light counterpart lifted for the dark ground, so the two variants mean the
            // same thing rather than being two unrelated palettes. Selected is a near-WHITE here for
            // the same reason it is a near-black there: it is a state, not an accent.
            [ColorRole.WBondWire]      = new(214, 122, 182),
            [ColorRole.WBondWireStart] = new(142,  41, 107),
            [ColorRole.WBondWireVertex] = new(142, 122, 255),
            [ColorRole.WBondSelected]  = new(244, 241, 243),
            [ColorRole.WBondEnvelope]  = new(214, 122, 182,  60),

            // ── Stackup cross-section, DARK ────────────────────────────────────────────────────
            [ColorRole.StackupBackground]     = new( 34,  34,  36),
            [ColorRole.StackupBandEdge]       = new(170, 170, 185, 110),
            [ColorRole.StackupDielectricFill] = new(150, 160, 175,  60),
            [ColorRole.StackupGroundAccent]   = new( 90, 180, 255),
            [ColorRole.StackupLabelInk]       = new(212, 212, 216),
            [ColorRole.StackupOnBandInk]      = new( 25,  25,  30),
            [ColorRole.StackupGripper]        = new(255, 175,  60),
            [ColorRole.StackupDragGhost]      = new( 90, 165, 255,  90),

            [ColorRole.MatchAbsorbed]  = new(214, 214, 222, 100),
            [ColorRole.MatchNegative]  = new(255, 118, 110),
            [ColorRole.MatchBracket]   = new(120, 178, 255),

            // ── railRF, DARK (railrf.md §2.4, §2.9; brief 8) ─────────────────────────────────
            // EVERY ONE OF THESE IS FULLY OPAQUE, on purpose — see ColorRole's railRF header for the
            // stackup drill-hole case that decided it. The ramp is blue → amber → red because those
            // three are the pair of endpoints a reader already reads as cold and hot with a midpoint
            // that is neither, which is what makes "where the colour changes fastest" legible.
            [ColorRole.RailMapCold]           = new( 80, 140, 245),
            [ColorRole.RailMapMid]            = new(250, 196,  70),
            [ColorRole.RailMapHot]            = new(240,  84,  84),
            [ColorRole.RailLegendBackground]  = new( 38,  38,  42),
            [ColorRole.RailLegendInk]         = new(226, 226, 230),
            [ColorRole.RailSource]            = new( 60, 210, 150),
            [ColorRole.RailLoad]              = new(110, 175, 255),
            [ColorRole.RailViaFlag]           = new(238, 120, 214),
            [ColorRole.RailClassTrace]        = new( 70, 130, 190),
            [ColorRole.RailClassSpreading]    = new(190, 128,  66),
            [ColorRole.RailClassForced]       = new(190, 140, 255),
            [ColorRole.RailCopperHighlight]   = new( 60, 200, 245),
            [ColorRole.RailPartSelection]     = new(255, 105, 215),
            [ColorRole.RailNetPreview]        = new( 90, 230, 160),
            [ColorRole.RailNotFitted]         = new(172, 176, 188),
        });
}

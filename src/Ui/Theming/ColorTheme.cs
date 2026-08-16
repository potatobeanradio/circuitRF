namespace CircuitRF.Ui.Theming;

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
            // Gold, darkened until it holds up against the near-white Layout.Background (246,246,244)
            // these are drawn over — the old hardcoded (224,192,96) was a dark-theme colour shown in
            // both variants. WireStart is the SAME hue two steps darker (owner: "the same as the
            // wire, but a much darker shade of it"). Selected is a deep saturated blue rather than
            // the old WHITE, which was the owner's "can't be seen over the canvas background".
            [ColorRole.WBondWire]      = new(150, 110,  20),
            [ColorRole.WBondWireStart] = new( 70,  50,   8),
            [ColorRole.WBondSelected]  = new( 10,  70, 180),
            [ColorRole.WBondEnvelope]  = new(150, 110,  20,  56),
            [ColorRole.WBondFreeWire]  = new(180,  70,  30),
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
            // The gold/orange the editor has always drawn with, kept: it was already a dark-ground
            // palette. WireStart follows the same "much darker shade of the wire" rule as light — it
            // still reads against the dark canvas, and it now means the same thing in both variants
            // (it used to be an unrelated blue in one and nothing at all in the other).
            [ColorRole.WBondWire]      = new(224, 192,  96),
            [ColorRole.WBondWireStart] = new(140, 112,  40),
            [ColorRole.WBondSelected]  = new(255, 255, 255),
            [ColorRole.WBondEnvelope]  = new(224, 192,  96,  64),
            [ColorRole.WBondFreeWire]  = new(224, 128,  96),
        });
}

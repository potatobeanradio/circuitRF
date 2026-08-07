namespace CircuitRF.Ui.Theming;

/// <summary>
/// Semantic color role keys (string constants, not enum, so new roles add without churn).
/// A new themable color = add a constant here + read it in the relevant token struct (L2).
/// </summary>
public static class ColorRole
{
    public const string SchematicBackground        = "Schematic.Background";
    public const string SchematicGrid              = "Schematic.Grid";
    public const string SchematicWire              = "Schematic.Wire";
    public const string SchematicWireRouting       = "Schematic.WireRouting";
    public const string SchematicNodeLabelText     = "Schematic.NodeLabelText";
    public const string SchematicInstanceNameText  = "Schematic.InstanceNameText";
    public const string SchematicParameterNameText = "Schematic.ParameterNameText";
    public const string SchematicComponentNameText = "Schematic.ComponentNameText";
    public const string SchematicConnectedPin      = "Schematic.ConnectedPin";
    public const string SchematicWireJunctionDot   = "Schematic.WireJunctionDot";
    public const string SchematicSymbolLine        = "Schematic.SymbolLine";
    public const string SchematicSymbolPlus        = "Schematic.SymbolPlus";
    public const string SystemWarning              = "System.Warning";

    // Layout chrome (docs/design/layout-view.md §2.2): the layer colors themselves are literal
    // Rgba stamped on LayerDef, not a role — these roles cover only the surrounding chrome
    // (background, grid, rulers, cursor indicator), which themes normally.
    public const string LayoutBackground      = "Layout.Background";
    public const string LayoutGridMinor       = "Layout.GridMinor";
    public const string LayoutGridMajor       = "Layout.GridMajor";
    public const string LayoutRulerBackground = "Layout.RulerBackground";
    public const string LayoutRulerText       = "Layout.RulerText";
    public const string LayoutRulerTick       = "Layout.RulerTick";
    public const string LayoutCursorIndicator = "Layout.CursorIndicator";

    /// <summary>Selection accent — the outline drawn above every layer on a selected shape, and the
    /// marquee rectangle (docs/design/layout-view.md §6.2, L1c). Never changes a selected shape's
    /// fill: the layer color is the information the user is reading.</summary>
    public const string LayoutSelection = "Layout.Selection";

    /// <summary>brief-L5-followups-2.md §6 (R-L5g-13): a PCell pin's screen-space dot + outward-
    /// direction tick — deliberately a color distinct from every layer color, so a pin marker never
    /// reads as copper (or any other physical layer).</summary>
    public const string LayoutPCellPin = "Layout.PCellPin";

    /// <summary>A PCell instance's draggable PARAMETER grip. Deliberately its own role rather than
    /// reusing Layout.Selection or Layout.PCellPin: a grip edits a parameter, not geometry, and a
    /// user who mistakes it for an L1d vertex handle is surprised in a way that is hard to undo.</summary>
    public const string LayoutPCellHandle = "Layout.PCellHandle";

    /// <summary>brief-L6-L7-em-ui.md R-em-15: a CONDUCTOR segment in the EM mesh overlay.</summary>
    public const string LayoutEmMeshConductor = "Layout.EmMeshConductor";

    /// <summary>R-em-15: a DIELECTRIC-INTERFACE segment in the EM mesh overlay. Deliberately a
    /// different colour from <see cref="LayoutEmMeshConductor"/> — they are different unknowns (free
    /// vs. bound charge), and a user reading a mesh needs to see which is which.</summary>
    public const string LayoutEmMeshInterface = "Layout.EmMeshInterface";

    /// <summary>R-em-15: the truncation extent marker. R-mom-10 calls truncation "the one place
    /// kernel A can be quietly wrong", so a viewer that hides it defeats the reporting the engine
    /// already does.</summary>
    public const string LayoutEmMeshTruncation = "Layout.EmMeshTruncation";

    /// <summary>
    /// brief-L8b-planar-mesher-and-overlay.md D5: a cell boundary in the PLAN-VIEW surface-mesh
    /// overlay. Distinct from the three cross-section roles above because the two overlays coexist —
    /// kernel A still produces cross-section meshes, and which one is drawn follows from which mesh
    /// was computed, not from a mode.
    /// </summary>
    public const string LayoutPlanarMeshCell = "Layout.PlanarMeshCell";

    /// <summary>
    /// L5b (docs/design/layout-view.md §9A.1): a DRC violation marker. Three roles rather than one
    /// because severity must be readable at a glance without the panel — and because a WAIVED
    /// violation must still be visible (§9A.1) while reading as deliberate rather than outstanding.
    /// Deliberately distinct from <see cref="SystemWarning"/>: a violation marker sits ON the artwork
    /// and has to stay legible against layer colours, which a general-purpose warning colour does not
    /// have to.
    /// </summary>
    public const string LayoutDrcError   = "Layout.DrcError";
    public const string LayoutDrcWarning = "Layout.DrcWarning";
    public const string LayoutDrcWaived  = "Layout.DrcWaived";

    // ── harmonicaRF (harmonicarf.md §7.9, D7) ────────────────────────────────────────────────────
    //
    // D7: these live in the SHARED role vocabulary rather than a second role system of their own.
    // One vocabulary means one editor, one `.ccolor` interchange, and one fallback rule. If the
    // Settings dialog ever proves cluttered, the fix is role GROUPING in the editor — not a parallel
    // set of keys, which would immediately owe its own editor, its own file format and its own
    // missing-role fallback.
    //
    // The defaults are §7.9.2 (dark) and §7.9.3 (light), and they live in ColorTheme.BuiltIn like
    // every other role. Two rules the palette is built around, restated because they are easy to
    // erode one commit at a time:
    //   • GREEN is the primary for everything textual and structural.
    //   • RED IS RESERVED — the loadline and the efficiency trace, and nothing else. Red means "this
    //     is the quantity you are engineering"; spending it anywhere else weakens it.

    public const string HarmonicaBackground       = "Harmonica.Background";
    public const string HarmonicaAxisLine         = "Harmonica.AxisLine";
    public const string HarmonicaAxisText         = "Harmonica.AxisText";
    /// <summary>ALL text in the §7.5 settings/readout strip.</summary>
    public const string HarmonicaReadoutText      = "Harmonica.ReadoutText";
    public const string HarmonicaGridLine         = "Harmonica.GridLine";
    /// <summary>The constant-R / constant-X arcs.</summary>
    public const string HarmonicaSmithGrid        = "Harmonica.SmithGrid";
    /// <summary>Iso-lines. Faded per §7.2's ranked alpha ramp — the ramp is applied on top of this
    /// colour at draw time, so the role itself is the FULL-opacity colour.</summary>
    public const string HarmonicaIsoline          = "Harmonica.Isoline";
    /// <summary>Only drawn when labels are on (D11 — default OFF).</summary>
    public const string HarmonicaIsolineLabel     = "Harmonica.IsolineLabel";
    public const string HarmonicaGainTrace        = "Harmonica.GainTrace";
    public const string HarmonicaDcivFamily       = "Harmonica.DcivFamily";
    /// <summary><b>Red — reserved.</b> One of exactly two roles allowed to be red.</summary>
    public const string HarmonicaLoadline         = "Harmonica.Loadline";
    /// <summary><b>Red — reserved.</b> The other one.</summary>
    public const string HarmonicaEfficiencyTrace  = "Harmonica.EfficiencyTrace";
    public const string HarmonicaGridPoint        = "Harmonica.GridPoint";
    /// <summary>A Γ point that did not reach the compression target — drawn HOLLOW (§6.3), so the
    /// hole reads as measured rather than as a rendering gap.</summary>
    public const string HarmonicaGridPointDropped = "Harmonica.GridPointDropped";
    public const string HarmonicaOperatingCursor  = "Harmonica.OperatingCursor";
    /// <summary>Intrinsic-drag reachability shading (§6.6). Carries its own alpha.</summary>
    public const string HarmonicaReachableRegion  = "Harmonica.ReachableRegion";
    public const string HarmonicaEditChrome       = "Harmonica.EditChrome";

    // The five-colour harmonic-identity cycle (§4.2). Roles, so a user CAN change them — but their
    // defaults are IDENTICAL in both variants on purpose: which colour means "2f₀" is a convention,
    // not a theme choice, so it must survive a light/dark switch untouched.
    public const string HarmonicaMarkerBand1 = "Harmonica.MarkerBand1";
    public const string HarmonicaMarkerBand2 = "Harmonica.MarkerBand2";
    public const string HarmonicaMarkerBand3 = "Harmonica.MarkerBand3";
    public const string HarmonicaMarkerBand4 = "Harmonica.MarkerBand4";
    public const string HarmonicaMarkerBand5 = "Harmonica.MarkerBand5";

    /// <summary>The five band roles in band order — band <c>n</c> uses index <c>(n-1) % 5</c>, which
    /// is what makes 6f₀ repeat the cycle (§4.2). One array so the renderer, the marker model and the
    /// colour editor cannot disagree about the order.</summary>
    public static readonly IReadOnlyList<string> HarmonicaMarkerBands =
    [
        HarmonicaMarkerBand1, HarmonicaMarkerBand2, HarmonicaMarkerBand3,
        HarmonicaMarkerBand4, HarmonicaMarkerBand5,
    ];

    /// <summary>The role for a harmonic band's marker, cycling every five bands (§4.2).</summary>
    public static string HarmonicaMarkerBand(int band)
        => HarmonicaMarkerBands[((band - 1) % 5 + 5) % 5];

    /// <summary>All defined roles in a consistent order (for iteration, UI lists, etc.).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        SchematicBackground, SchematicGrid, SchematicWire, SchematicWireRouting,
        SchematicNodeLabelText, SchematicInstanceNameText,
        SchematicParameterNameText, SchematicComponentNameText,
        SchematicConnectedPin, SchematicWireJunctionDot,
        SchematicSymbolLine, SchematicSymbolPlus,
        SystemWarning,
        LayoutBackground, LayoutGridMinor, LayoutGridMajor,
        LayoutRulerBackground, LayoutRulerText, LayoutRulerTick, LayoutCursorIndicator,
        LayoutSelection, LayoutPCellPin, LayoutPCellHandle,
        LayoutEmMeshConductor, LayoutEmMeshInterface, LayoutEmMeshTruncation,
        LayoutPlanarMeshCell,
        LayoutDrcError, LayoutDrcWarning, LayoutDrcWaived,
        HarmonicaBackground, HarmonicaAxisLine, HarmonicaAxisText, HarmonicaReadoutText,
        HarmonicaGridLine, HarmonicaSmithGrid,
        HarmonicaIsoline, HarmonicaIsolineLabel,
        HarmonicaGainTrace, HarmonicaDcivFamily,
        HarmonicaLoadline, HarmonicaEfficiencyTrace,
        HarmonicaGridPoint, HarmonicaGridPointDropped,
        HarmonicaOperatingCursor, HarmonicaReachableRegion, HarmonicaEditChrome,
        HarmonicaMarkerBand1, HarmonicaMarkerBand2, HarmonicaMarkerBand3,
        HarmonicaMarkerBand4, HarmonicaMarkerBand5,
    ];
}

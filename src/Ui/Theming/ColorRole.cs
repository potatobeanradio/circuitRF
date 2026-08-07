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
    ];
}

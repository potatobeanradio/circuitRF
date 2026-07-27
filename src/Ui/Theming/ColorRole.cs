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
    ];
}

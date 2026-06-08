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
    public const string SchematicNodeLabelText     = "Schematic.NodeLabelText";
    public const string SchematicInstanceNameText  = "Schematic.InstanceNameText";
    public const string SchematicParameterNameText = "Schematic.ParameterNameText";
    public const string SchematicComponentNameText = "Schematic.ComponentNameText";
    public const string SchematicConnectedPin      = "Schematic.ConnectedPin";
    public const string SchematicWireJunctionDot   = "Schematic.WireJunctionDot";
    public const string SchematicSymbolLine        = "Schematic.SymbolLine";
    public const string SchematicSymbolPlus        = "Schematic.SymbolPlus";
    public const string SystemWarning              = "System.Warning";

    /// <summary>All defined roles in a consistent order (for iteration, UI lists, etc.).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        SchematicBackground, SchematicGrid, SchematicWire,
        SchematicNodeLabelText, SchematicInstanceNameText,
        SchematicParameterNameText, SchematicComponentNameText,
        SchematicConnectedPin, SchematicWireJunctionDot,
        SchematicSymbolLine, SchematicSymbolPlus,
        SystemWarning,
    ];
}

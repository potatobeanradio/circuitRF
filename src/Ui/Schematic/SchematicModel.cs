namespace CircuitRF.Ui.Schematic;

// ---------------------------------------------------------------------------
//  Schematic read model — 6c render-only. No mutation API.
//  World units: 100 = one grid square (standard EDA: 100 mils).
//  Component origin is the geometric center of its body.
// ---------------------------------------------------------------------------

public enum SymbolKind
{
    Resistor,
    Inductor,
    Capacitor,
    VoltageSource,
    ToneSource,
    Ground,
    Port,
    FetSdd,
    ZPort,
    Generic
}

public enum PortConnectionState { Unconnected, Connected }

public enum SymbolRotation { R0 = 0, R90 = 90, R180 = 180, R270 = 270 }

/// <summary>Port descriptor in component-LOCAL coordinates (before rotation/translation).</summary>
public sealed record SchematicPortDef(string Name, float LocalX, float LocalY, PortConnectionState State);

/// <summary>A placed component instance with pre-computed world bounding box.</summary>
public sealed class SchematicComponent
{
    public string InstanceName { get; init; } = "";
    public SymbolKind Symbol    { get; init; }
    public double X             { get; init; }   // world X of component origin
    public double Y             { get; init; }   // world Y of component origin
    public SymbolRotation Rotation { get; init; }
    public bool MirrorX        { get; init; }
    public IReadOnlyList<SchematicPortDef> Ports { get; init; } = [];
    public string? LabelA      { get; init; }    // primary on-schematic label (instance name)
    public string? LabelB      { get; init; }    // secondary (key value with units)
    // Axis-aligned bounding box in world coords (pre-computed, includes port leads).
    public double BbMinX       { get; init; }
    public double BbMinY       { get; init; }
    public double BbMaxX       { get; init; }
    public double BbMaxY       { get; init; }
}

/// <summary>A wire segment (orthogonal polyline) with pre-computed world bounding box.</summary>
public sealed class SchematicWire
{
    public IReadOnlyList<(double X, double Y)> Points { get; init; } = [];
    public double BbMinX { get; init; }
    public double BbMinY { get; init; }
    public double BbMaxX { get; init; }
    public double BbMaxY { get; init; }
}

/// <summary>A wire-wire or port-wire junction dot (§4.3 dark square).</summary>
public sealed class SchematicDot(double x, double y)
{
    public double X { get; } = x;
    public double Y { get; } = y;
}

/// <summary>
/// The complete schematic read model consumed by SchematicRenderer.
/// Immutable after construction — 6c is read-only.
/// </summary>
public sealed class SchematicModel
{
    public IReadOnlyList<SchematicComponent> Components   { get; init; } = [];
    public IReadOnlyList<SchematicWire>      Wires        { get; init; } = [];
    public IReadOnlyList<SchematicDot>       ConnectionDots { get; init; } = [];
    public double GridSize  { get; init; } = 100.0;
    // Overall bounding box of all elements (used for zoom-to-fit).
    public double BbMinX   { get; init; }
    public double BbMinY   { get; init; }
    public double BbMaxX   { get; init; }
    public double BbMaxY   { get; init; }
}

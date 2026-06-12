// Framework-free symbol primitive model.
// No SKColor / SKPath / Avalonia — colors are SymbolColorRole enum values.
// Coordinates are component-LOCAL (100 units = 1 connection-grid square P).
// +x right, +y down (screen convention).

using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Schematic;

// ── Color / style roles ───────────────────────────────────────────────────────

public enum SymbolColorRole  { SymbolLine, SymbolText, SymbolPlus }
public enum SymbolFontStyle  { Regular, Bold, Italic, Condensed }
/// <summary>Named stroke-width tiers in local units: Thick≈9 / Normal≈6 / Thin≈3.</summary>
public enum SymbolStrokeTier { Normal, Thin, Thick }
public enum SineAxis         { Horizontal, Vertical }
public enum SymbolTextAlign  { Left, Center, Right }

/// <summary>Tri-state snap mode for symbol-editor art.  Pins ALWAYS snap to P=100 regardless.</summary>
public enum SnapMode { ConnectionGrid, FineGrid, None }

// ── Primitive base ────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(LinePrimitive),        "Line")]
[JsonDerivedType(typeof(PolylinePrimitive),    "Polyline")]
[JsonDerivedType(typeof(RectPrimitive),        "Rect")]
[JsonDerivedType(typeof(RoundedRectPrimitive), "RoundedRect")]
[JsonDerivedType(typeof(CirclePrimitive),      "Circle")]
[JsonDerivedType(typeof(EllipsePrimitive),     "Ellipse")]
[JsonDerivedType(typeof(ArcPrimitive),         "Arc")]
[JsonDerivedType(typeof(PolygonPrimitive),     "Polygon")]
[JsonDerivedType(typeof(QuadCurvePrimitive),   "QuadCurve")]
[JsonDerivedType(typeof(CubicCurvePrimitive),  "CubicCurve")]
[JsonDerivedType(typeof(SinePrimitive),             "Sine")]
[JsonDerivedType(typeof(ExponentialTaperPrimitive), "ExpTaper")]
[JsonDerivedType(typeof(TextPrimitive),             "Text")]
[JsonDerivedType(typeof(BitmapPrimitive),           "Bitmap")]
public abstract class SymbolPrimitive { }

// ── Line ─────────────────────────────────────────────────────────────────────

public sealed class LinePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }

    public LinePrimitive() { }
    public LinePrimitive(SymbolColorRole role, SymbolStrokeTier tier,
                         double x1, double y1, double x2, double y2)
    {
        ColorRole = role; StrokeTier = tier; X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
    }
}

// ── Polyline ──────────────────────────────────────────────────────────────────

public sealed class PolylinePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    /// <summary>Point list as [x, y] pairs.</summary>
    public List<double[]>   Points     { get; set; } = [];
}

// ── Rect ──────────────────────────────────────────────────────────────────────

public sealed class RectPrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public bool Filled { get; set; }
    /// <summary>Center x, y.</summary>
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double W  { get; set; }
    public double H  { get; set; }
}

// ── RoundedRect ───────────────────────────────────────────────────────────────

public sealed class RoundedRectPrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public bool Filled { get; set; }
    public double Cx     { get; set; }
    public double Cy     { get; set; }
    public double W      { get; set; }
    public double H      { get; set; }
    public double Radius { get; set; }
}

// ── Circle ────────────────────────────────────────────────────────────────────

public sealed class CirclePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public bool Filled { get; set; }
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double R  { get; set; }
}

// ── Ellipse ───────────────────────────────────────────────────────────────────

public sealed class EllipsePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public bool Filled { get; set; }
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double Rx { get; set; }
    public double Ry { get; set; }
}

// ── Arc ───────────────────────────────────────────────────────────────────────

public sealed class ArcPrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public double Cx       { get; set; }
    public double Cy       { get; set; }
    public double R        { get; set; }
    /// <summary>Start angle in degrees, measured clockwise from +x.</summary>
    public double StartDeg { get; set; }
    /// <summary>Sweep angle in degrees; positive = clockwise.</summary>
    public double SweepDeg { get; set; }
}

// ── Polygon / Triangle ────────────────────────────────────────────────────────

public sealed class PolygonPrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public bool Filled { get; set; }
    /// <summary>Vertex list as [x, y] pairs.</summary>
    public List<double[]>   Points     { get; set; } = [];
}

// ── QuadCurve ─────────────────────────────────────────────────────────────────

public sealed class QuadCurvePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public double P0X   { get; set; }
    public double P0Y   { get; set; }
    public double CtrlX { get; set; }
    public double CtrlY { get; set; }
    public double P2X   { get; set; }
    public double P2Y   { get; set; }
}

// ── CubicCurve ────────────────────────────────────────────────────────────────

public sealed class CubicCurvePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public double P0X { get; set; }
    public double P0Y { get; set; }
    public double C1X { get; set; }
    public double C1Y { get; set; }
    public double C2X { get; set; }
    public double C2Y { get; set; }
    public double P3X { get; set; }
    public double P3Y { get; set; }
}

// ── Sine (parameterized smart-path) ──────────────────────────────────────────

public sealed class SinePrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole   { get; set; }
    public SymbolStrokeTier StrokeTier  { get; set; }
    /// <summary>Center of the wave's bounding span.</summary>
    public double   Cx         { get; set; }
    public double   Cy         { get; set; }
    public double   Amp        { get; set; }
    public double   Cycles     { get; set; }
    public double   Length     { get; set; }
    /// <summary>
    /// Sample points per full cycle.  Renderer uses ceil(Cycles * PtsPerCycle) segments.
    /// Minimum effective value is 1; renderer clamps to at least 2 total segments.
    /// </summary>
    public int      PtsPerCycle { get; set; } = 20;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SineAxis Axis       { get; set; }
}

// ── ExponentialTaper ─────────────────────────────────────────────────────────
// Width profile: w(x) = W1 · (W2/W1)^(x/L), rendered as a closed filled polygon.
// Cx/Cy is the center of the taper; L is the total length along the taper axis.

public sealed class ExponentialTaperPrimitive : SymbolPrimitive
{
    public SymbolColorRole  ColorRole  { get; set; }
    public SymbolStrokeTier StrokeTier { get; set; }
    public bool   Filled   { get; set; }
    /// <summary>Center of the taper in local coords.</summary>
    public double Cx       { get; set; }
    public double Cy       { get; set; }
    /// <summary>Width at the start (x=0) end.</summary>
    public double W1       { get; set; } = 60.0;
    /// <summary>Width at the end (x=L) end.</summary>
    public double W2       { get; set; } = 15.0;
    /// <summary>Length along the taper axis.</summary>
    public double L        { get; set; } = 100.0;
    /// <summary>Sample points per outline side; minimum 2.</summary>
    public int    NumPts   { get; set; } = 20;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SineAxis Axis   { get; set; }
}

// ── Text ──────────────────────────────────────────────────────────────────────

public sealed class TextPrimitive : SymbolPrimitive
{
    public string Content   { get; set; } = "";
    public double AnchorX   { get; set; }
    public double AnchorY   { get; set; }
    public double FontSize  { get; set; } = 12.0;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolFontStyle FontStyle { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolTextAlign Align     { get; set; }
}

// ── Bitmap (reference/tracing artwork) ───────────────────────────────────────
// Stored as a path reference, not embedded bytes. Z-index is always lowest
// (behind all vector primitives, enforced by renderer). No color role.

public sealed class BitmapPrimitive : SymbolPrimitive
{
    /// <summary>Path to the image file (absolute or relative to the .csym).</summary>
    public string ImagePathRef { get; set; } = "";
    /// <summary>Placement rect: left edge x, top edge y.</summary>
    public double X       { get; set; }
    public double Y       { get; set; }
    public double W       { get; set; }
    public double H       { get; set; }
    public double Opacity { get; set; } = 1.0;
    /// <summary>When locked, accidental click/drag does not move the bitmap.</summary>
    public bool   Locked  { get; set; }
}

// ── Pin ───────────────────────────────────────────────────────────────────────

/// <summary>
/// A pin placement in the symbol. Data only — the runtime still uses
/// SymbolPortDefs for connectivity; pins here are written/read but not yet wired.
/// Every pin tip must be on P (an exact multiple of 100 in local coords).
/// </summary>
public sealed class SymbolPin
{
    public double  LocalX     { get; set; }
    public double  LocalY     { get; set; }
    public int     PortIndex  { get; set; }
    public string? Name       { get; set; }

    public SymbolPin() { }
    public SymbolPin(double localX, double localY, int portIndex, string? name = null)
    {
        LocalX = localX; LocalY = localY; PortIndex = portIndex; Name = name;
    }
}

// ── Symbol ───────────────────────────────────────────────────────────────────

/// <summary>
/// A symbol: an ordered list of drawing primitives + a list of pins.
/// Primitives and pins are both in component-LOCAL coordinates.
/// This is the single definition of "what this symbol looks like" — all three
/// consumers (editor, renderer, persistence) read the same model.
/// </summary>
public sealed class Symbol
{
    public IReadOnlyList<SymbolPrimitive> Primitives { get; }
    public IReadOnlyList<SymbolPin>       Pins       { get; }

    /// <summary>
    /// Number of ports this symbol can map pins to.
    /// Defaults to Pins.Count when portCount is 0 or omitted (backward-compat).
    /// Persisted in .csym; not the same as the schematic component's PortCount.
    /// </summary>
    public int PortCount { get; }

    public Symbol(IReadOnlyList<SymbolPrimitive> primitives, IReadOnlyList<SymbolPin> pins,
                  int portCount = 0)
    {
        Primitives = primitives;
        Pins       = pins;
        PortCount  = portCount > 0 ? portCount : pins.Count;
    }
}

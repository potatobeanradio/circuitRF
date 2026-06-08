namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  Canvas objects (§3.1) — bitmaps, text, shape primitives.
//  All are framework-free POCOs. No Avalonia types.
// ──────────────────────────────────────────────────────────────────────────────

public enum CanvasObjectKind { Bitmap, Text, Rect, Circle, Line }

/// <summary>
/// Base class for all canvas objects (§3.1 shared contract):
/// selectable, drag-move, resize, rotate, transparency, Z-order, lock.
/// </summary>
public abstract class EditableCanvasObject
{
    public string Id        { get; } = Guid.NewGuid().ToString("N")[..12];
    public abstract CanvasObjectKind Kind { get; }

    // Placement: centre position, size, rotation angle (degrees), transparency [0,1]
    public double X           { get; set; }
    public double Y           { get; set; }
    public double Width       { get; set; } = 300.0;
    public double Height      { get; set; } = 200.0;
    public double RotationDeg { get; set; }
    public double Transparency { get; set; }   // 0 = fully opaque, 1 = fully transparent
    public bool   IsLocked    { get; set; }
    public int    ZOrder      { get; set; }    // higher = drawn later (on top)

    public (double MinX, double MinY, double MaxX, double MaxY) GetBoundingBox()
        => (X - Width / 2, Y - Height / 2, X + Width / 2, Y + Height / 2);

    public abstract EditableCanvasObject Clone();
}

/// <summary>
/// Bitmap canvas object (§3.1.1).
/// Persists only the file path; pixels are reloaded on open.
/// Aspect-locked resize via bottom-left gripper.
/// </summary>
public sealed class EditableBitmap : EditableCanvasObject
{
    public override CanvasObjectKind Kind => CanvasObjectKind.Bitmap;

    /// <summary>File path (relative preferred, absolute allowed) — never pixels.</summary>
    public string ImagePath { get; set; } = "";

    public override EditableCanvasObject Clone() => new EditableBitmap
    {
        X = X, Y = Y, Width = Width, Height = Height,
        RotationDeg = RotationDeg, Transparency = Transparency,
        IsLocked = IsLocked, ZOrder = ZOrder,
        ImagePath = ImagePath,
    };
}

/// <summary>Text canvas object (§3.1.2) — inline-editable, wrapping.</summary>
public sealed class EditableText : EditableCanvasObject
{
    public override CanvasObjectKind Kind => CanvasObjectKind.Text;

    public string   Text       { get; set; } = "Text";
    public string   FontFamily { get; set; } = "";       // empty = default (IBM Plex Sans)
    public float    FontSize   { get; set; } = 12f;
    public bool     IsBold     { get; set; }
    public bool     IsItalic   { get; set; }
    public uint     ColorArgb  { get; set; } = 0xFF202020;  // packed ARGB

    public override EditableCanvasObject Clone() => new EditableText
    {
        X = X, Y = Y, Width = Width, Height = Height,
        RotationDeg = RotationDeg, Transparency = Transparency,
        IsLocked = IsLocked, ZOrder = ZOrder,
        Text = Text, FontFamily = FontFamily, FontSize = FontSize,
        IsBold = IsBold, IsItalic = IsItalic, ColorArgb = ColorArgb,
    };
}

public enum PrimitiveShape { Rect, Circle, Line }
public enum ArrowheadStyle { None, Start, End, Both }

/// <summary>
/// Shape primitive (§3.1.3) — rectangle, circle, or line.
/// Line has two draggable endpoint control points and optional arrowheads.
/// </summary>
public sealed class EditablePrimitive : EditableCanvasObject
{
    public override CanvasObjectKind Kind => CanvasObjectKind.Line; // refined below

    public PrimitiveShape Shape      { get; set; }
    public float          LineWidth  { get; set; } = 2f;
    public uint           ColorArgb  { get; set; } = 0xFF202020;

    // Line-only
    public ArrowheadStyle Arrowheads  { get; set; }
    public float          ArrowSize   { get; set; } = 12f;
    // Line endpoint control points (world coords; used instead of X/Y/Width/Height for lines)
    public double P1X { get; set; }
    public double P1Y { get; set; }
    public double P2X { get; set; }
    public double P2Y { get; set; } = 200.0;

    public override EditableCanvasObject Clone() => new EditablePrimitive
    {
        X = X, Y = Y, Width = Width, Height = Height,
        RotationDeg = RotationDeg, Transparency = Transparency,
        IsLocked = IsLocked, ZOrder = ZOrder,
        Shape = Shape, LineWidth = LineWidth, ColorArgb = ColorArgb,
        Arrowheads = Arrowheads, ArrowSize = ArrowSize,
        P1X = P1X, P1Y = P1Y, P2X = P2X, P2Y = P2Y,
    };
}

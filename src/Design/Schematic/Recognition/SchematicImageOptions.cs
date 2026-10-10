// How a schematic picture is read — brief-img-7-schematic-wires-and-regions.md R-im7-2 … R-im7-7 (overview D11, D16).
//
// Every tolerance is stated in units of the picture's stroke width w (R-im7-2), so one set of defaults reads a
// 2-pixel screenshot and a 6-pixel scan alike. Every field has the dialog's default.

using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>When a four-way crossing connects (D11). A T always connects; a hop never does.</summary>
public enum CrossingRule
{
    /// <summary>A four-way crossing never connects.</summary>
    Never,

    /// <summary>A four-way crossing connects only where a junction dot sits on it — the default.</summary>
    WithDot,

    /// <summary>Every four-way crossing connects.</summary>
    Always,
}

public sealed record SchematicImageOptions
{
    /// <summary>The binarisation (R-im1-3).</summary>
    public LineArtOptions LineArt { get; init; } = new();

    /// <summary>When a four-way crossing connects (R-im7-5).</summary>
    public CrossingRule Crossings { get; init; } = CrossingRule.WithDot;

    /// <summary>A straight run within this many degrees of 0°/90° is an orthogonal wire.</summary>
    public double SnapAngleDeg { get; init; } = 3.0;

    /// <summary>Straighten a picture whose long runs lean (R-im7-2).</summary>
    public bool Deskew { get; init; } = true;

    /// <summary>A lean at or below this many degrees is left alone — it is within the pixel staircase.</summary>
    public double MinDeskewDeg { get; init; } = 0.3;

    /// <summary>A lean above this many degrees is not a skew but a drawing at an angle, and is left alone.</summary>
    public double MaxDeskewDeg { get; init; } = 5.0;

    /// <summary>Remove a dot grid (engineering paper, a tool's snap grid) before reading.</summary>
    public bool RemoveGrid { get; init; } = true;

    /// <summary>Remove a frame enclosing most of the drawing, and the title block drawn on it.</summary>
    public bool RemoveBorder { get; init; } = true;

    // ── Text (R-im7-3) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A glyph is at least this many w tall.</summary>
    public double MinTextHeight { get; init; } = 2.0;

    /// <summary>A glyph is at most this many w tall; a straight run longer than this is line work, never text.</summary>
    public double MaxTextHeight { get; init; } = 12.0;

    /// <summary>A word touching line work along more than this many w is kept as a symbol detail.</summary>
    public double TextContact { get; init; } = 2.0;

    // ── Wires (R-im7-4) ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An orthogonal straight run shorter than this many w is not a wire.</summary>
    public double MinWireLength { get; init; } = 3.0;

    /// <summary>A diagonal straight run is a wire only above this many w.</summary>
    public double MinDiagonalLength { get; init; } = 8.0;

    /// <summary>A run is straight when its pixels lie within this many w of its line (RMS).</summary>
    public double MaxResidual { get; init; } = 0.25;

    /// <summary>A straight line no longer than this many w whose BOTH ends stop in nothing is a stroke of a symbol — a
    /// capacitor plate, a ground bar, a supply bar — not a wire.</summary>
    public double MaxSymbolStroke { get; init; } = 16.0;

    /// <summary>A junction dot's diameter is at least this many w …</summary>
    public double MinDot { get; init; } = 2.0;

    /// <summary>… and at most this many.</summary>
    public double MaxDot { get; init; } = 6.0;

    /// <summary>A junction dot is centred within this many w of the node.</summary>
    public double DotCentre { get; init; } = 1.0;

    // ── Symbols (R-im7-6, R-im7-7) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Residue pieces are grown by this many w before they are joined.</summary>
    public double Grow { get; init; } = 1.0;

    /// <summary>A wire end stopping within this many w of a symbol region is attached to it.</summary>
    public double Attach { get; init; } = 1.5;

    /// <summary>A word within this many w of a dangling end, in line with its wire, is the wire's net label.</summary>
    public double LabelGap { get; init; } = 2.0;

    public int MaxThreads { get; init; }
}

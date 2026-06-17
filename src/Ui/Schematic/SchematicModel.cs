namespace CircuitRF.Ui.Schematic;

// ---------------------------------------------------------------------------
//  Schematic read model 
//  World units: 100 = one grid square (standard EDA: 100 mils).
//  Component origin is the geometric center of its body.
// ---------------------------------------------------------------------------

public enum SymbolKind
{
    Resistor,
    Inductor,
    Capacitor,
    Vdc,
    ToneSource,
    Ground,
    Term,
    Pin,
    FetSdd,
    Sdd,
    ZPort,
    Generic,
    Var,
    P1Tone,
}

public enum PortConnectionState { Unconnected, Connected }

public enum SymbolRotation { R0 = 0, R90 = 90, R180 = 180, R270 = 270 }

/// <summary>Port descriptor in component-LOCAL coordinates (before rotation/translation).</summary>
public sealed record SchematicPortDef(string Name, float LocalX, float LocalY, PortConnectionState State);

/// <summary>A placed component instance with pre-computed world bounding boxes.</summary>
public sealed class SchematicComponent
{
    // ── Label layout constants (world units, relative to component center) ────
    // Single source of truth shared by BuildRenderModel (which stores FullBb) and
    // the renderer (which reads it). Having them here prevents the two callsites
    // drifting to different values — that drift was what caused the LabelOffsets
    // cull blind spot fixed in this commit.
    public const double LabelBaseOffsetX   = -155.0; // label anchor X from center
    public const double LabelBaseY         =  280.0; // first-row Skia baseline Y from center
    public const double LabelWorldHeight   =   70.0; // font cap-height in world units
    public const double LabelWorldStep     =   72.0; // line-to-line spacing
    public const double LabelWidthEstimate =  500.0; // conservative text-width estimate

    /// <summary>
    /// First-row label baseline Y (from component center) for this symbol and port count.
    /// For fixed-geometry symbols this is the constant LabelBaseY. For the variadic SDD/ZPort
    /// symbols whose body grows with port count, the base-Y is pushed just below the glyph's
    /// bottom edge so the label never overlaps the symbol body.
    /// </summary>
    public static double LabelBaseYFor(SymbolKind symbol, int portCount)
    {
        if (symbol is SymbolKind.Sdd or SymbolKind.ZPort)
        {
            double halfH = SymbolPortDefs.SddBodyRect(portCount).HalfH;
            return Math.Max(LabelBaseY, halfH + LabelWorldStep);
        }
        return LabelBaseY;
    }

    /// <summary>
    /// Canonical world geometry for label row <paramref name="i"/>, given the per-label offset
    /// (LabelOffsets[i] plus any live drag delta). Single source of truth shared by the renderer
    /// (DrawLabels) and the hit-test (TestComponentLabels) so the clickable zone always tracks the
    /// rendered text. Returns the left-aligned text anchor (BaselineX, BaselineY) and the vertical
    /// hit band [BandTopY, BandBotY] centered on the visual row.
    /// </summary>
    public static (double BaselineX, double BaselineY, double BandTopY, double BandBotY)
        LabelRowGeometry(double cx, double cy, int i, double oDx, double oDy,
                         SymbolKind symbol, int portCount)
    {
        double baseY     = LabelBaseYFor(symbol, portCount);
        double baselineX = cx + LabelBaseOffsetX + oDx;
        double baselineY = cy + baseY + oDy + i * LabelWorldStep;
        const double comfort = 6.0;
        double bandTopY  = baselineY - LabelWorldHeight - comfort;
        double bandBotY  = baselineY + LabelWorldHeight * 0.28 + comfort;
        return (baselineX, baselineY, bandTopY, bandBotY);
    }

    /// <summary>Stable ID carried from EditableComponent.Id — used by overlay for selection lookup.</summary>
    public string Id            { get; init; } = "";
    public string InstanceName  { get; init; } = "";
    public SymbolKind Symbol    { get; init; }
    public double X             { get; init; }
    public double Y             { get; init; }
    public SymbolRotation Rotation { get; init; }
    public bool MirrorX        { get; init; }
    public DisableState DisableState { get; init; }
    public IReadOnlyList<SchematicPortDef> Ports { get; init; } = [];

    /// <summary>
    /// On-schematic labels in display order: [0] = type, [1] = instance name, [2+] = parameters
    /// flagged ShowOnSchematic. Rendered left-aligned below the glyph.
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>
    /// Per-label world-position offsets (DX, DY) from the default auto-position.
    /// Parallel to Labels; missing entries imply (0,0).
    /// </summary>
    public IReadOnlyList<(double DX, double DY)> LabelOffsets { get; init; } = [];

    // Glyph-only bounding box (±200 around center, used for zoom-to-fit and hit-test).
    public double BbMinX { get; init; }
    public double BbMinY { get; init; }
    public double BbMaxX { get; init; }
    public double BbMaxY { get; init; }

    // Symbol-glyph-only bounding box (no text area). Used for hit-testing and selection highlight.
    public double GlyphBbMinX { get; init; }
    public double GlyphBbMinY { get; init; }
    public double GlyphBbMaxX { get; init; }
    public double GlyphBbMaxY { get; init; }

    // Full visual bounding box: glyph BB unioned with every label at its actual offset position
    // (including LabelOffsets). Read by SchematicSpatialIndex (build) and the renderer in-loop
    // cull — both reference the same value so they stay in sync automatically.
    public double FullBbMinX { get; init; }
    public double FullBbMinY { get; init; }
    public double FullBbMaxX { get; init; }
    public double FullBbMaxY { get; init; }

    // ── Cell-reference rendering state ────────────────────────────────────────
    // Non-null only for cell-reference components (EditableComponent.CellRef != null).
    // Drives which render path the renderer takes:
    //   Resolved       → draw CellRefPrimitives via DrawSymbol
    //   NotFound       → "Not Found" warning glyph
    //   PrimaryMissing → plain-rectangle stand-in
    // Null = built-in component, BuiltInSymbols path unchanged.

    /// <summary>
    /// Three-state resolution result for cell-reference components; null for built-ins.
    /// </summary>
    public CellSymbolState? CellRefState      { get; init; }

    /// <summary>Non-null when CellRefState == Resolved — the primary .csym primitives to draw.</summary>
    public IReadOnlyList<SymbolPrimitive>? CellRefPrimitives { get; init; }
}

/// <summary>A wire segment (orthogonal polyline) with pre-computed world bounding box.</summary>
public sealed class SchematicWire
{
    /// <summary>Stable ID carried from EditableWire.Id — used by overlay for selection lookup.</summary>
    public string Id            { get; init; } = "";
    public IReadOnlyList<(double X, double Y)> Points { get; init; } = [];
    public double BbMinX { get; init; }
    public double BbMinY { get; init; }
    public double BbMaxX { get; init; }
    public double BbMaxY { get; init; }
    /// <summary>Whether the first endpoint connects to another wire or component port.</summary>
    public bool StartConnected { get; init; }
    /// <summary>Whether the last endpoint connects to another wire or component port.</summary>
    public bool EndConnected   { get; init; }
}

/// <summary>
/// A junction dot (§4.3 dark square). circuitRF maintains a hard invariant (§5.1): a dot exists
/// only where it marks a genuine connection — a user dot on a real 4-way wire crossing, or a
/// derived auto-dot at a T-junction. Inert dots never reach the render model, so every dot here
/// is an unambiguous "these wires are connected" mark (load-bearing for 6e net extraction).
/// </summary>
public sealed class SchematicDot(double x, double y)
{
    public double X { get; } = x;
    public double Y { get; } = y;
}

/// <summary>A user-placed net (node) label displayed on the canvas.</summary>
public sealed class SchematicNetLabel
{
    public string Id   { get; init; } = "";
    public double X    { get; init; }
    public double Y    { get; init; }
    public string Name { get; init; } = "";
}

/// <summary>A user-placed bitmap canvas object in the schematic (read model).</summary>
public sealed record SchematicBitmap(
    string Id,
    string ImagePath,
    double X, double Y,        // top-left in world coords
    double Width, double Height,
    double Opacity);           // 0 = transparent, 1 = opaque

/// <summary>
/// The complete schematic read model consumed by SchematicRenderer.
/// Immutable after construction — 6c is read-only.
/// </summary>
public sealed class SchematicModel
{
    public IReadOnlyList<SchematicComponent>  Components   { get; init; } = [];
    public IReadOnlyList<SchematicWire>       Wires        { get; init; } = [];
    public IReadOnlyList<SchematicDot>        ConnectionDots { get; init; } = [];
    public IReadOnlyList<SchematicNetLabel>   NetLabels    { get; init; } = [];
    public IReadOnlyList<SchematicBitmap>     Bitmaps      { get; init; } = [];
    public double GridSize  { get; init; } = 100.0;
    // Overall bounding box of all elements (used for zoom-to-fit).
    public double BbMinX   { get; init; }
    public double BbMinY   { get; init; }
    public double BbMaxX   { get; init; }
    public double BbMaxY   { get; init; }
}

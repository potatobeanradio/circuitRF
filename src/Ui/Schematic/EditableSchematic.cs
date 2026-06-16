using System.Linq;
using CircuitRF.Core.Design;

namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  Editable schematic model — Phase 6d.
//  Mutable state for a schematic being edited. Framework-free (no Avalonia).
//  Commands mutate this model and call NotifyChanged(); the SchematicViewModel
//  rebuilds the immutable SchematicModel render snapshot on each change.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>A single named parameter on a component.</summary>
public sealed class EditableParameter
{
    public string Name            { get; set; } = "";
    /// <summary>Raw expression string (e.g. "1/(w0^2*C)") — not yet evaluated.</summary>
    public string Expression      { get; set; } = "";
    /// <summary>Unit string (e.g. "nH", "pF", "Ω"). Empty when dimensionless.</summary>
    public string Unit            { get; set; } = "";
    public bool   ShowOnSchematic { get; set; } = true;
    /// <summary>Physical dimension — drives the closed Unit ComboBox options.</summary>
    public UnitDimension Dimension { get; set; } = UnitDimension.None;

    public EditableParameter Clone() => new()
    {
        Name = Name, Expression = Expression, Unit = Unit, ShowOnSchematic = ShowOnSchematic,
        Dimension = Dimension,
    };
}

/// <summary>
/// Per-symbol port definitions (local coordinates, 100 units = 1 grid square).
/// Matches the rendering geometry in SchematicSymbols.cs.
/// Port indices are 0-based internally; they map to 1-based user-facing port numbers
/// following the port-index convention in project-file-formats.md.
///
/// For variadic-port types (ZPort, Sdd) the portCount overload must be used; the
/// single-argument overload falls back to portCount=2 for those types.
/// </summary>
public static class SymbolPortDefs
{
    /// <summary>Convenience overload; uses portCount=2 for variadic types.</summary>
    public static (string Name, float LocalX, float LocalY)[] For(SymbolKind kind)
        => For(kind, portCount: 2);

    /// <summary>
    /// Returns port definitions for the given kind and (for variadic types) port count.
    /// ZPort and Sdd generate 2N pins as differential ± pairs — same generator.
    /// Pin index order: pin[2(p-1)] = "p+", pin[2(p-1)+1] = "p-".
    /// </summary>
    public static (string Name, float LocalX, float LocalY)[] For(SymbolKind kind, int portCount)
    {
        switch (kind)
        {
            case SymbolKind.Var:     return [];
            case SymbolKind.Ground:  return [("1", 0f, 0f)];
            // Term: two terminals — "+" (signal, index 0) and "−" (reference, index 1).
            // Pin order is the contract: NetBindings[0]=+ net, NetBindings[1]=− net.
            case SymbolKind.Term:    return [("+", 0f, -200f), ("−", 0f, +200f)];
            // Pin: one connection terminal at the lead tip (horizontal, tip on the right).
            case SymbolKind.Pin:     return [("1", 200f, 0f)];
            case SymbolKind.FetSdd:  return [("gate",   -200f,   0f),
                                             ("drain",   200f, -100f),
                                             ("source",  200f,  100f)];
            case SymbolKind.ZPort:
            case SymbolKind.Sdd:
                return GenerateSddPorts(portCount >= 1 ? portCount : 2);
            default:
                return [("1", 0f, -200f), ("2", 0f, 200f)];
        }
    }

    // Generates 2N schematic pins for an N-port SDD/ZPort.
    // Ports are split left/right: ceil(N/2) ports on the left (x=−200), floor(N/2) on the right (x=+200).
    // Within each port the "+" pin is above center and "−" is below (portCenter ± 100).
    // Port centers are spaced 400 apart on each side: centers land on even multiples of 200,
    // so ± pins always land on ODD multiples of 100 — no P-cell collision via banker's rounding.
    // N=1 special case: "1+" at (−200,0) left, "1−" at (+200,0) right, both vertically centered.
    // Pin index order is the NetExtractor contract: pin[2(p-1)] = "p+", pin[2(p-1)+1] = "p-".
    private static (string Name, float LocalX, float LocalY)[] GenerateSddPorts(int n)
    {
        if (n == 1)
            return [("1+", -200f, 0f), ("1-", +200f, 0f)];

        var ports  = new (string, float, float)[2 * n];
        int nLeft  = (n + 1) / 2;   // ceil — ports 1..nLeft on left
        int nRight = n       / 2;   // floor — ports nLeft+1..n on right
        const float portSpacing = 400f;  // port-center pitch — multiple of 200 → pins on odd*100
        const float halfDiff    = 100f;  // y from port center to each ± pin

        for (int p = 0; p < nLeft; p++)
        {
            float cy = (p - (nLeft - 1) * 0.5f) * portSpacing;
            int   pn = p + 1;
            ports[2 * p]     = ($"{pn}+", -200f, cy - halfDiff);
            ports[2 * p + 1] = ($"{pn}-", -200f, cy + halfDiff);
        }
        for (int q = 0; q < nRight; q++)
        {
            float cy = (q - (nRight - 1) * 0.5f) * portSpacing;
            int   pn = nLeft + q + 1;
            int   i  = 2 * (nLeft + q);
            ports[i]     = ($"{pn}+", +200f, cy - halfDiff);
            ports[i + 1] = ($"{pn}-", +200f, cy + halfDiff);
        }
        return ports;
    }

    /// <summary>
    /// Returns the N-aware body rect dimensions for an SDD/ZPort symbol.
    /// Half-height = max|pin Y| + 60 margin; width is fixed at 180 (edges ±90).
    /// </summary>
    public static (float W, float HalfH) SddBodyRect(int n)
    {
        var ports = For(SymbolKind.Sdd, n);
        float maxPinY = ports.Length == 0 ? 0f : ports.Max(p => Math.Abs(p.LocalY));
        return (180f, maxPinY + 60f);
    }

}

// ── Placed component ─────────────────────────────────────────────────────────

/// <summary>A placed, editable component instance.</summary>
public sealed class EditableComponent
{
    public string         Id           { get; } = Guid.NewGuid().ToString("N")[..12];
    public string         InstanceName { get; set; } = "";
    public SymbolKind     Symbol       { get; set; }
    /// <summary>
    /// Relative path from the containing schematic's directory to the referenced cell folder.
    /// Null for built-in components (the built-in SymbolKind path is used instead).
    /// When non-null the cell-reference resolution path is used (CellSymbolResolver).
    /// </summary>
    public string?        CellRef      { get; set; }
    public double         X            { get; set; }
    public double         Y            { get; set; }
    public SymbolRotation Rotation     { get; set; }
    public bool           MirrorX      { get; set; }
    public DisableState   Disable      { get; set; } = DisableState.None;
    public List<EditableParameter> Parameters   { get; } = new();
    /// <summary>
    /// Port indices that are explicitly detached. A detached port is geometrically coincident
    /// with a wire/pin but treated as unconnected (first persistent connectivity override).
    /// Persisted in .csch. Clears on the component's next move (lifecycle in MoveCommand).
    /// </summary>
    public HashSet<int>   DetachedPorts { get; } = new();
    /// <summary>True when port <paramref name="portIndex"/> is explicitly detached.</summary>
    public bool IsPortDetached(int portIndex) => DetachedPorts.Contains(portIndex);
    /// <summary>Per-label world-offset from default position. Index matches Labels list (0=type,1=name,2+=params).</summary>
    public List<(double DX, double DY)> LabelOffsets { get; } = new();

    /// <summary>Whether to render the type label (e.g. "Z2P", "R"). Seeded from registry default at placement; overridable per-instance.</summary>
    public bool ShowTypeLabel    { get; set; } = true;
    /// <summary>Whether to render the instance name (e.g. "R1", "Z1"). Seeded from registry default at placement; overridable per-instance.</summary>
    public bool ShowInstanceName { get; set; } = true;

    public (double DX, double DY) GetLabelOffset(int index)
        => index < LabelOffsets.Count ? LabelOffsets[index] : (0, 0);

    private const double HalfBound = 200.0;  // matches SchematicModelBuilder

    public (double MinX, double MinY, double MaxX, double MaxY) GetBoundingBox()
        => (X - HalfBound, Y - HalfBound, X + HalfBound, Y + HalfBound);

    /// <summary>World coordinates of a port by 0-based port index.</summary>
    public (double X, double Y) GetPortWorldCoord(int portIndex)
    {
        var ports = SymbolPortDefs.For(Symbol, PortCount);
        if ((uint)portIndex >= (uint)ports.Length)
            throw new ArgumentOutOfRangeException(nameof(portIndex));
        var (_, lx, ly) = ports[portIndex];
        return SchematicGeometry.LocalToWorld(lx, ly, X, Y, Rotation, MirrorX);
    }

    /// <summary>
    /// Number of schematic ports on this symbol.
    /// For variadic-port types (ZPort, Sdd) this reads the NumPorts parameter; for all
    /// other types it delegates to SymbolPortDefs.
    /// </summary>
    public int PortCount
    {
        get
        {
            if (Symbol is SymbolKind.ZPort or SymbolKind.Sdd)
            {
                var p = Parameters.FirstOrDefault(q => q.Name == "NumPorts");
                if (p is not null && int.TryParse(p.Expression, out int n) && n >= 1)
                    return n;
                return 2; // default for variadic types when NumPorts not yet set
            }
            return SymbolPortDefs.For(Symbol).Length;
        }
    }

    /// <summary>Convert to the immutable render type, with port connection state.</summary>
    /// <param name="isPointConnected">World-coordinate connectivity predicate.</param>
    /// <param name="cellRefResolution">
    /// Non-null for cell-reference components (CellRef != null).
    /// Resolved → use resolved symbol pins+primitives; NotFound/PrimaryMissing → no pins, glyph placeholder.
    /// Null → built-in component path (unchanged).
    /// </param>
    public SchematicComponent ToRenderComponent(
        Func<double, double, bool>? isPointConnected = null,
        CellSymbolResolution? cellRefResolution = null)
    {
        List<SchematicPortDef> ports;
        CellSymbolState? cellRefState = null;
        IReadOnlyList<SymbolPrimitive>? cellRefPrimitives = null;

        if (cellRefResolution is not null)
        {
            cellRefState = cellRefResolution.State;
            if (cellRefResolution is { State: CellSymbolState.Resolved, Symbol: { } resolvedSym })
            {
                cellRefPrimitives = resolvedSym.Primitives;
                ports = resolvedSym.Pins.Select(pin =>
                {
                    PortConnectionState state;
                    if (IsPortDetached(pin.PortIndex))
                    {
                        state = PortConnectionState.Unconnected;
                    }
                    else
                    {
                        var (wx, wy) = SchematicGeometry.LocalToWorld(
                            (float)pin.LocalX, (float)pin.LocalY, X, Y, Rotation, MirrorX);
                        state = (isPointConnected?.Invoke(wx, wy) ?? false)
                            ? PortConnectionState.Connected
                            : PortConnectionState.Unconnected;
                    }
                    return new SchematicPortDef(
                        pin.Name ?? $"P{pin.PortIndex + 1}",
                        (float)pin.LocalX, (float)pin.LocalY,
                        state);
                }).ToList();
            }
            else
            {
                // NotFound / PrimaryMissing: no pin geometry
                ports = [];
            }
        }
        else
        {
            var portDefs = SymbolPortDefs.For(Symbol, PortCount);
            ports = portDefs.Select((p, i) =>
            {
                PortConnectionState state;
                if (IsPortDetached(i))
                {
                    state = PortConnectionState.Unconnected;
                }
                else
                {
                    var (wx, wy) = SchematicGeometry.LocalToWorld(p.LocalX, p.LocalY, X, Y, Rotation, MirrorX);
                    state = (isPointConnected?.Invoke(wx, wy) ?? false)
                        ? PortConnectionState.Connected
                        : PortConnectionState.Unconnected;
                }
                return new SchematicPortDef(p.Name, p.LocalX, p.LocalY, state);
            }).ToList();
        }

        // Labels in display order: type, instance name, then ShowOnSchematic params.
        // ShowTypeLabel/ShowInstanceName suppress the respective label (stored as ""); renderer skips empty strings.
        // For cell-reference components the type label is the cell folder name (derived from CellRef —
        // single source of truth, never a separate persisted field that could drift).
        // For built-ins the type label comes from the component registry.
        // Param format: "<Name> = <Expression> <Unit>" (spaces around =; unit omitted when empty).
        string typeLabel = ShowTypeLabel
            ? (CellRef is not null
                ? Path.GetFileName(CellRef.TrimEnd('/', '\\'))
                : ComponentTypeRegistry.DisplayName(Symbol, PortCount))
            : "";
        var labels = new List<string>
        {
            typeLabel,
            ShowInstanceName ? InstanceName : "",
        };
        foreach (var p in Parameters)
        {
            if (!p.ShowOnSchematic || string.IsNullOrEmpty(p.Expression)) continue;
            string val = string.IsNullOrEmpty(p.Unit) ? p.Expression : $"{p.Expression} {p.Unit}";
            labels.Add(string.IsNullOrEmpty(p.Name) ? val : $"{p.Name} = {val}");
        }

        var bb = GetBoundingBox();
        var (glyphMinX, glyphMinY, glyphMaxX, glyphMaxY) = cellRefPrimitives is not null
            ? ComputeGlyphBb(cellRefPrimitives)
            : cellRefResolution is not null
                ? (X - 160, Y - 60, X + 160, Y + 60)   // NotFound / PrimaryMissing placeholder
                : ComputeGlyphBb(null);

        // FullBb: glyph BB unioned with every label's actual world position including offsets.
        // Computed once here so the spatial index and the renderer in-loop cull share a single
        // pre-baked value and cannot drift from each other.
        double fullMinX = bb.MinX, fullMinY = bb.MinY;
        double fullMaxX = bb.MaxX, fullMaxY = bb.MaxY;
        for (int li = 0; li < labels.Count; li++)
        {
            if (string.IsNullOrEmpty(labels[li])) continue;
            var (oDx, oDy) = li < LabelOffsets.Count ? LabelOffsets[li] : (0.0, 0.0);
            double lx  = X + SchematicComponent.LabelBaseOffsetX + oDx;
            double ly  = Y + SchematicComponent.LabelBaseY + oDy + li * SchematicComponent.LabelWorldStep;
            fullMinX = Math.Min(fullMinX, lx);
            fullMinY = Math.Min(fullMinY, ly - SchematicComponent.LabelWorldHeight);
            fullMaxX = Math.Max(fullMaxX, lx + SchematicComponent.LabelWidthEstimate);
            fullMaxY = Math.Max(fullMaxY, ly + 20.0);
        }

        return new SchematicComponent
        {
            Id               = Id,
            InstanceName     = InstanceName,
            Symbol           = Symbol,
            X = X, Y = Y,
            Rotation         = Rotation,
            MirrorX          = MirrorX,
            DisableState     = Disable,
            Ports            = ports,
            Labels           = labels,
            LabelOffsets     = LabelOffsets.Count > 0 ? LabelOffsets.ToList() : [],
            BbMinX = bb.MinX, BbMinY = bb.MinY,
            BbMaxX = bb.MaxX, BbMaxY = bb.MaxY,
            GlyphBbMinX = glyphMinX, GlyphBbMinY = glyphMinY,
            GlyphBbMaxX = glyphMaxX, GlyphBbMaxY = glyphMaxY,
            FullBbMinX = fullMinX, FullBbMinY = fullMinY,
            FullBbMaxX = fullMaxX, FullBbMaxY = fullMaxY,
            CellRefState     = cellRefState,
            CellRefPrimitives = cellRefPrimitives,
        };
    }

    /// <summary>Axis-aligned bounding box of the symbol geometry in world coordinates.</summary>
    /// <param name="overridePrimitives">
    /// When non-null, use these primitives instead of the built-in symbol primitives.
    /// Used by the cell-reference Resolved render path to compute the glyph BB from the
    /// resolved .csym primitives without re-querying BuiltInSymbols.
    /// When null, falls back to the built-in BuiltInSymbols.Primitives(Symbol) path.
    /// </param>
    public (double MinX, double MinY, double MaxX, double MaxY) ComputeGlyphBb(
        IReadOnlyList<SymbolPrimitive>? overridePrimitives = null)
    {
        IReadOnlyList<SymbolPrimitive> prims = overridePrimitives ?? BuiltInSymbols.Primitives(Symbol, PortCount).Primitives;
        if (prims.Count == 0) return (X - 160, Y - 60, X + 160, Y + 60);

        var (lMinX, lMinY, lMaxX, lMaxY) = SymbolGeometry.ComputeBb(prims);

        // Variadic port-lead extension applies only to built-in types, not cell-ref symbols.
        if (overridePrimitives is null && Symbol is SymbolKind.ZPort or SymbolKind.Sdd)
        {
            foreach (var (_, lx, ly) in SymbolPortDefs.For(Symbol, PortCount))
            {
                if (lx < lMinX) lMinX = lx;
                if (ly < lMinY) lMinY = ly;
                if (lx > lMaxX) lMaxX = lx;
                if (ly > lMaxY) lMaxY = ly;
            }
        }
        const float pad = 15f;
        // Transform all four local corners and take world BB.
        var corners = new[]
        {
            SchematicGeometry.LocalToWorld((float)(lMinX - pad), (float)(lMinY - pad), X, Y, Rotation, MirrorX),
            SchematicGeometry.LocalToWorld((float)(lMaxX + pad), (float)(lMinY - pad), X, Y, Rotation, MirrorX),
            SchematicGeometry.LocalToWorld((float)(lMinX - pad), (float)(lMaxY + pad), X, Y, Rotation, MirrorX),
            SchematicGeometry.LocalToWorld((float)(lMaxX + pad), (float)(lMaxY + pad), X, Y, Rotation, MirrorX),
        };
        return (
            corners.Min(c => c.X),
            corners.Min(c => c.Y),
            corners.Max(c => c.X),
            corners.Max(c => c.Y));
    }

    public EditableComponent Clone()
    {
        var c = new EditableComponent
        {
            InstanceName     = InstanceName, Symbol = Symbol,
            X = X, Y = Y, Rotation = Rotation, MirrorX = MirrorX, Disable = Disable,
            ShowTypeLabel    = ShowTypeLabel,
            ShowInstanceName = ShowInstanceName,
            CellRef          = CellRef,
        };
        foreach (var p in Parameters)    c.Parameters.Add(p.Clone());
        foreach (var o in LabelOffsets) c.LabelOffsets.Add(o);
        foreach (var d in DetachedPorts) c.DetachedPorts.Add(d);
        return c;
    }
}

// ── Wire ─────────────────────────────────────────────────────────────────────

/// <summary>An editable wire (polyline).</summary>
public sealed class EditableWire
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public List<(double X, double Y)> Points { get; } = new();

    public (double MinX, double MinY, double MaxX, double MaxY) GetBoundingBox()
    {
        if (Points.Count == 0) return (0, 0, 0, 0);
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in Points)
        {
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        return (minX - 5, minY - 5, maxX + 5, maxY + 5);
    }

    public SchematicWire ToRenderWire(Func<EditableWire, double, double, bool>? isEndpointConnected = null)
    {
        var bb = GetBoundingBox();
        bool startConn = Points.Count > 0 && (isEndpointConnected?.Invoke(this, Points[0].X, Points[0].Y) ?? false);
        bool endConn   = Points.Count > 1 && (isEndpointConnected?.Invoke(this, Points[^1].X, Points[^1].Y) ?? false);
        return new SchematicWire
        {
            Id             = Id,
            Points         = Points.ToList(),
            BbMinX         = bb.MinX, BbMinY = bb.MinY,
            BbMaxX         = bb.MaxX, BbMaxY = bb.MaxY,
            StartConnected = startConn,
            EndConnected   = endConn,
        };
    }

    public EditableWire Clone()
    {
        var w = new EditableWire();
        w.Points.AddRange(Points);
        return w;
    }
}

// ── Net label ────────────────────────────────────────────────────────────────

/// <summary>A user-placed net label (§4.4), anchored to the wire it was created on. Its draw position
/// (X,Y) is DERIVED from the owner wire's geometry each build (see RecomputePosition).</summary>
public sealed class EditableNetLabel
{
    public string Id   { get; } = Guid.NewGuid().ToString("N")[..12];
    public double X    { get; set; }   // DERIVED draw origin (recomputed from the anchor each build)
    public double Y    { get; set; }
    public string Name { get; set; } = "";

    // ── Wire anchor (source of truth for position) ──
    public string OwnerWireId  { get; set; } = "";
    public int    SegmentIndex { get; set; }
    public double AlongT       { get; set; }   // foot parameter on the segment, 0..1
    public double OffsetX      { get; set; }   // world offset foot → draw origin (the perpendicular gap)
    public double OffsetY      { get; set; }

    public bool IsAnchored => OwnerWireId.Length > 0;

    /// <summary>Anchors this label to <paramref name="wire"/> by projecting (px,py) onto its nearest
    /// segment: stores SegmentIndex + AlongT (foot parameter) and the residual as a world offset.</summary>
    public void AnchorToWire(EditableWire wire, double px, double py)
    {
        OwnerWireId = wire.Id;
        var pts = wire.Points;
        int bestSeg = 0; double bestT = 0, bestDsq = double.PositiveInfinity, fx = px, fy = py;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var (ax, ay) = pts[i]; var (bx, by) = pts[i + 1];
            double dx = bx - ax, dy = by - ay, lenSq = dx * dx + dy * dy;
            double t  = lenSq < 1e-10 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0, 1);
            double cx = ax + t * dx, cy = ay + t * dy;
            double dsq = (px - cx) * (px - cx) + (py - cy) * (py - cy);
            if (dsq < bestDsq) { bestDsq = dsq; bestSeg = i; bestT = t; fx = cx; fy = cy; }
        }
        SegmentIndex = bestSeg; AlongT = bestT;
        OffsetX = px - fx; OffsetY = py - fy;
        X = px; Y = py;
    }

    /// <summary>Recomputes X,Y from the owner wire's current segment geometry. Returns false when the
    /// stored SegmentIndex no longer exists (the wire was shortened) — caller treats it as an orphan
    /// and skips it.</summary>
    public bool RecomputePosition(EditableWire wire)
    {
        var pts = wire.Points;
        if (SegmentIndex < 0 || SegmentIndex >= pts.Count - 1) return false;
        var (ax, ay) = pts[SegmentIndex]; var (bx, by) = pts[SegmentIndex + 1];
        double fx = ax + AlongT * (bx - ax), fy = ay + AlongT * (by - ay);
        X = fx + OffsetX; Y = fy + OffsetY;
        return true;
    }
}

// ── Junction dot ─────────────────────────────────────────────────────────────

/// <summary>A user-placed junction dot (§5.1). Marks a wire-wire crossing as connected.</summary>
public sealed class EditableDot
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public double X  { get; set; }
    public double Y  { get; set; }
}

// ── Edit model ───────────────────────────────────────────────────────────────

/// <summary>
/// The mutable editing state for one schematic.
/// Commands hold a reference to this model and call NotifyChanged() after mutating it.
/// SchematicViewModel listens to Changed and rebuilds the immutable render snapshot.
/// </summary>
public sealed class SchematicEditModel
{
    /// <summary>
    /// Absolute path to the directory containing this schematic's .csch file.
    /// Set by SchematicPersistence.LoadFromFile; null for unsaved schematics.
    /// Used as the base directory for resolving CellRef relative paths.
    /// </summary>
    public string? SchematicDirectory { get; set; }

    public List<EditableComponent>  Components   { get; } = new();
    public List<EditableWire>       Wires        { get; } = new();
    public List<EditableNetLabel>   NetLabels    { get; } = new();
    public List<EditableDot>        Dots         { get; } = new();
    public List<EditableCanvasObject> CanvasObjects { get; } = new();

    // ── Analysis authoring (persisted in .csch) ───────────────────────────────
    public List<Analysis>    Analyses     { get; } = new();
    public List<Measurement> Measurements { get; } = new();

    public double GridSize          { get; set; } = 100.0;
    public bool   GridSnap          { get; set; } = true;
    /// <summary>Fine authoring grid divisor k: p = P/k (default k=20 → p=5).
    /// Governs label offsets, net-label positions, and canvas objects only — never connection points.</summary>
    public int    AuthorGridDivisor { get; set; } = 20;

    /// <summary>Fine authoring grid pitch p = GridSize / AuthorGridDivisor (default 5).</summary>
    public double AuthorGridSize => GridSize / AuthorGridDivisor;

    // View state (saved/restored with .csch)
    public double ViewPanX { get; set; }
    public double ViewPanY { get; set; }
    public double ViewZoom { get; set; } = 1.0;

    // Fired by commands after each mutation; SchematicViewModel subscribes.
    public event EventHandler? Changed;
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Snap to the connection grid P. Use for all electrical points (pins, wires, dots).</summary>
    public double SnapToGrid(double v)
        => GridSnap ? Math.Round(v / GridSize) * GridSize : v;

    /// <summary>Snap to the fine authoring grid p = P/k. Use for labels, net-labels, canvas objects only.</summary>
    public double SnapToAuthorGrid(double v)
        => GridSnap ? Math.Round(v / AuthorGridSize) * AuthorGridSize : v;

    // ── Render model build ────────────────────────────────────────────────────

    /// <summary>Float-dust guard for geometric coincidence checks (world units).
    /// Connection is established at input by snapping to P (R3/R4 — not by this tolerance).
    /// Public so editing code uses the same guard as the connectivity pass.</summary>
    public const double ConnectTolerance = 0.5;

    /// <summary>
    /// Builds an immutable SchematicModel + spatial index from current state.
    /// Port connection state is determined by local geometric adjacency (§4.3).
    /// Called by SchematicViewModel after each model change.
    /// Connectivity pass is O(N) via spatial hash (not O(N²) linear scan).
    /// </summary>
    public (SchematicModel Model, SchematicSpatialIndex Index) BuildRenderModel()
    {
        // Pre-resolve all cell-refs before the connectivity pass so pin positions are available.
        var cellRefResolutions = ResolveAllCellRefs();

        // Connectivity geometry (vertex hashes, T-junctions, crossing predicate) is computed by
        // a shared helper so the live dot preview during drags reuses the identical logic.
        var cg = ComputeConnectivityGeometry(cellRefResolutions);

        // IsConnected for a port: O(1) hash check against conPointCounts.
        // A port is connected when at least one OTHER endpoint (another port or a wire vertex)
        // shares its P-cell (count >= 2 — the port itself contributes 1, so >=2 means something
        // else is there). Fallback handles port on a wire body interior (not at any endpoint).
        bool IsConnected(double wx, double wy)
        {
            var key = QuantKey(wx, wy);
            if (cg.ConPointCounts.TryGetValue(key, out int cnt) && cnt >= 2) return true;
            // Fallback: port on wire body interior (not at any vertex/endpoint).
            foreach (var w in Wires)
            {
                var pts = w.Points;
                for (int i = 0; i < pts.Count - 1; i++)
                    if (SchematicGeometry.PointOnSegment(wx, wy, pts[i].X, pts[i].Y,
                                                          pts[i + 1].X, pts[i + 1].Y, ConnectTolerance)) return true;
            }
            return false;
        }

        // IsEndpointConnected: O(1) lookup — an endpoint is connected if another vertex sits there
        // (count > 1, a shared vertex / corner) OR it is part of an auto-junction (e.g. it lands on
        // another wire's body — a T). Either way no false "unconnected" indicator shows.
        bool IsEndpointConnected(EditableWire _, double wx, double wy)
        {
            var key = QuantKey(wx, wy);
            if (cg.ConPointCounts.TryGetValue(key, out int cnt) && cnt > 1) return true;
            return cg.AutoDotKeys.Contains(key);
        }

        var comps = Components.Select(c =>
        {
            CellSymbolResolution? res = c.CellRef is not null && cellRefResolutions is not null
                && cellRefResolutions.TryGetValue(c.Id, out var r) ? r : null;
            return c.ToRenderComponent(IsConnected, res);
        }).ToList();
        var wires = Wires.Select(w => w.ToRenderWire(IsEndpointConnected)).ToList();
        var dots  = AssembleConnectionDots(cg);
        var netLabels = new List<SchematicNetLabel>(NetLabels.Count);
        foreach (var l in NetLabels)
        {
            if (l.IsAnchored)
            {
                var ow = FindWire(l.OwnerWireId);
                if (ow is null || !l.RecomputePosition(ow))
                    continue;   // orphan (owner gone / segment shortened) — not rendered; Brief 2 removes it
            }
            netLabels.Add(new SchematicNetLabel { Id = l.Id, X = l.X, Y = l.Y, Name = l.Name });
        }
        var bitmaps   = CanvasObjects
            .OfType<EditableBitmap>()
            .OrderBy(b => b.ZOrder)
            .Select(b => new SchematicBitmap(
                b.Id,
                b.ImagePath,
                b.X - b.Width  / 2.0,
                b.Y - b.Height / 2.0,
                b.Width, b.Height,
                1.0 - b.Transparency))
            .ToList();

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var c in comps)
        {
            if (c.BbMinX < minX) minX = c.BbMinX; if (c.BbMaxX > maxX) maxX = c.BbMaxX;
            if (c.BbMinY < minY) minY = c.BbMinY; if (c.BbMaxY > maxY) maxY = c.BbMaxY;
        }
        foreach (var w in wires)
        {
            if (w.BbMinX < minX) minX = w.BbMinX; if (w.BbMaxX > maxX) maxX = w.BbMaxX;
            if (w.BbMinY < minY) minY = w.BbMinY; if (w.BbMaxY > maxY) maxY = w.BbMaxY;
        }

        if (minX == double.MaxValue) { minX = minY = -500; maxX = maxY = 500; }

        var model = new SchematicModel
        {
            Components     = comps,
            Wires          = wires,
            ConnectionDots = dots,
            NetLabels      = netLabels,
            Bitmaps        = bitmaps,
            GridSize       = GridSize,
            BbMinX = minX - 200, BbMinY = minY - 200,
            BbMaxX = maxX + 200, BbMaxY = maxY + 200,
        };

        return (model, new SchematicSpatialIndex(model));
    }

    // ── Cell-ref resolution ───────────────────────────────────────────────────

    /// <summary>
    /// Resolves all CellRef values on Components using SchematicDirectory as the base path.
    /// Returns null when there are no cell-ref components or SchematicDirectory is not set.
    /// </summary>
    private Dictionary<string, CellSymbolResolution>? ResolveAllCellRefs()
    {
        if (SchematicDirectory is null) return null;
        Dictionary<string, CellSymbolResolution>? result = null;
        foreach (var comp in Components)
        {
            if (comp.CellRef is null) continue;
            result ??= new Dictionary<string, CellSymbolResolution>(StringComparer.Ordinal);
            result[comp.Id] = CellSymbolResolver.Resolve(comp.CellRef, SchematicDirectory);
        }
        return result;
    }

    // ── Cell-ref-aware pin-geometry accessor (single source of truth) ────────────

    /// <summary>
    /// Single source of a component's pin geometry. Cell-ref-aware:
    ///   CellRef + Resolved  → resolved .csym Symbol.Pins (via <paramref name="cellRefResolutions"/> or CellSymbolResolver)
    ///   CellRef + NotFound/PrimaryMissing → empty (matches the no-pins render)
    ///   built-in            → SymbolPortDefs.For(Symbol, PortCount), PortIndex = slot
    /// Per-frame callers should use a pre-built snapshot to avoid per-frame resolver calls.
    /// </summary>
    internal IReadOnlyList<(float LocalX, float LocalY, int PortIndex)> PortDefsOf(
        EditableComponent comp,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions = null)
    {
        if (comp.CellRef is not null)
        {
            CellSymbolResolution? res = null;
            if (cellRefResolutions is not null)
                cellRefResolutions.TryGetValue(comp.Id, out res);
            else if (SchematicDirectory is not null)
                res = CellSymbolResolver.Resolve(comp.CellRef, SchematicDirectory);

            if (res is { State: CellSymbolState.Resolved, Symbol: { } sym })
            {
                var pins = sym.Pins;
                var r = new (float LocalX, float LocalY, int PortIndex)[pins.Count];
                for (int i = 0; i < pins.Count; i++)
                    r[i] = ((float)pins[i].LocalX, (float)pins[i].LocalY, pins[i].PortIndex);
                return r;
            }
            return [];
        }

        var portDefs = SymbolPortDefs.For(comp.Symbol, comp.PortCount);
        var result   = new (float LocalX, float LocalY, int PortIndex)[portDefs.Length];
        for (int i = 0; i < portDefs.Length; i++)
            result[i] = (portDefs[i].LocalX, portDefs[i].LocalY, i);
        return result;
    }

    /// <summary>World coords of one pin def for a component (applies LocalToWorld with rotation/mirror).</summary>
    internal (double X, double Y) PortWorldOf(
        EditableComponent comp,
        (float LocalX, float LocalY, int PortIndex) def)
        => SchematicGeometry.LocalToWorld(def.LocalX, def.LocalY, comp.X, comp.Y, comp.Rotation, comp.MirrorX);

    /// <summary>
    /// Resolved symbol primitives for a cell-ref component (Resolved state), or null for built-ins
    /// or unresolved cell-refs. Null for a cell-ref means NotFound/PrimaryMissing — the caller
    /// should use placeholder bounds, consistent with what the renderer draws.
    /// </summary>
    internal IReadOnlyList<SymbolPrimitive>? EffectivePrimitivesOf(
        EditableComponent comp,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions = null)
    {
        if (comp.CellRef is null) return null;

        CellSymbolResolution? res = null;
        if (cellRefResolutions is not null)
            cellRefResolutions.TryGetValue(comp.Id, out res);
        else if (SchematicDirectory is not null)
            res = CellSymbolResolver.Resolve(comp.CellRef, SchematicDirectory);

        return res is { State: CellSymbolState.Resolved, Symbol: { } sym } ? sym.Primitives : null;
    }

    // ── Connectivity helpers (shared by BuildRenderModel and the live dot preview) ──

    /// <summary>Quantize a point to the connection-grid cell (P = GridSize).
    /// Exact P-multiples map to their integer index; float-dust rounds to the same cell.</summary>
    private (long, long) QuantKey(double x, double y)
        => ((long)Math.Round(x / GridSize), (long)Math.Round(y / GridSize));

    /// <summary>Geometry derived for the connectivity pass: vertex hashes, auto-junction points
    /// (3+ segment meetings with a vertex), and a crossing predicate. Shared so the render model,
    /// the live drag preview, and the net extractor (6e) agree.</summary>
    internal readonly record struct ConnectivityGeometry(
        HashSet<(long, long)> WirePointHash,
        Dictionary<(long, long), int> ConPointCounts,
        HashSet<(long, long)> AutoDotKeys,
        List<(double X, double Y)> AutoDotPts,
        Func<double, double, bool> IsCrossingAtDot);

    /// <summary>
    /// Computes the connectivity geometry from the current Wires/Components in O(N) via a
    /// segment cell-hash. This is the single source of truth for T-junction and 4-way crossing
    /// detection; both BuildRenderModel and ComputeConnectionDots() call it.
    /// </summary>
    /// <param name="cellRefResolutions">
    /// Optional pre-resolved cell-ref map from BuildRenderModel.
    /// When provided and a component has a Resolved CellRef, its symbol pins are used
    /// instead of SymbolPortDefs for the connectivity port-position pass.
    /// Null (default) is safe — callers that don't need cell-ref connectivity can omit it.
    /// </param>
    internal ConnectivityGeometry ComputeConnectivityGeometry(
        Dictionary<string, CellSymbolResolution>? cellRefResolutions = null)
    {
        // Hash of all wire vertex positions → fast port-connection detection.
        var wirePointHash = new HashSet<(long, long)>(Wires.Count * 4);
        foreach (var w in Wires)
            foreach (var (px, py) in w.Points)
                wirePointHash.Add(QuantKey(px, py));

        // Count of all connection points (wire vertices + component port positions).
        // A wire endpoint with count > 1 is connected to at least one other object.
        // Deduplicate points within each wire so a zero-length or repeated interior point
        // in one wire cannot falsely inflate the count and hide an unconnected dot.
        var conPointCounts = new Dictionary<(long, long), int>(Wires.Count * 4 + Components.Count * 3);
        void AddConPoint(double x, double y)
        {
            var key = QuantKey(x, y);
            conPointCounts[key] = conPointCounts.GetValueOrDefault(key, 0) + 1;
        }
        foreach (var w in Wires)
        {
            var seenInWire = new HashSet<(long, long)>();
            foreach (var (px, py) in w.Points)
            {
                var key = QuantKey(px, py);
                if (seenInWire.Add(key)) AddConPoint(px, py);
            }
        }
        foreach (var comp in Components)
        {
            foreach (var def in PortDefsOf(comp, cellRefResolutions))
            {
                if (comp.IsPortDetached(def.PortIndex)) continue;
                var (px, py) = PortWorldOf(comp, def);
                AddConPoint(px, py);
            }
        }

        // ── T-junction detection (§5.1) ───────────────────────────────────────
        // A wire endpoint that lands on the *interior* of another wire's segment
        // (strictly between that segment's two vertices, within tolerance) forms a
        // 3-way T-junction: an unambiguous connection that auto-shows a junction dot.
        // This is distinct from a 4-way crossing (two wires crossing, neither ending
        // on the other), which stays ambiguous and connects only via a user-placed
        // EditableDot (§5.1).
        //
        // 6e extraction note: the electrical meaning — one node shared by the three
        // incident wire-ends — is realized at net extraction (6e, union-find over
        // geometry). When 6e is built, the union step MUST treat a point lying on a
        // wire's segment interior as splitting that wire at the T and unioning all
        // three incident wire-ends into one net. This is the same rule §5.1 step 2
        // already states for "a port lying on a wire segment unions with that wire";
        // a wire endpoint on another wire's segment is the same coincidence. The 6d
        // connection visuals and the 6e extraction must agree that an endpoint-on-segment
        // is a connection — do NOT implement extraction here.
        //
        // O(N) via a segment cell-hash: index each segment by the grid cells its
        // tolerance-expanded bbox covers, then test each endpoint only against the
        // few segments sharing its cell (never an O(N²) all-pairs scan).
        const double SegCell = 100.0;
        // Wi = owning wire index — lets crossing detection require two *distinct* wires.
        var segList  = new List<(int Wi, double ax, double ay, double bx, double by)>();
        var segIndex = new Dictionary<(long, long), List<int>>();
        for (int wi = 0; wi < Wires.Count; wi++)
        {
            var pts = Wires[wi].Points;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                int si = segList.Count;
                segList.Add((wi, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y));
                long cMinX = (long)Math.Floor((Math.Min(pts[i].X, pts[i + 1].X) - ConnectTolerance) / SegCell);
                long cMaxX = (long)Math.Floor((Math.Max(pts[i].X, pts[i + 1].X) + ConnectTolerance) / SegCell);
                long cMinY = (long)Math.Floor((Math.Min(pts[i].Y, pts[i + 1].Y) - ConnectTolerance) / SegCell);
                long cMaxY = (long)Math.Floor((Math.Max(pts[i].Y, pts[i + 1].Y) + ConnectTolerance) / SegCell);
                for (long cx = cMinX; cx <= cMaxX; cx++)
                    for (long cy = cMinY; cy <= cMaxY; cy++)
                    {
                        var ck = (cx, cy);
                        if (!segIndex.TryGetValue(ck, out var lst)) segIndex[ck] = lst = new List<int>();
                        lst.Add(si);
                    }
            }
        }

        // ── Auto junction dots (§5.1) — the standard EDA rule ─────────────────
        // A junction dot is drawn wherever 3+ wire segments meet AND at least one wire has a
        // vertex (endpoint or bend) there. This covers, with one rule:
        //   • a wire ending on another wire's body  → classic T-junction;
        //   • a wire ending on another wire's CORNER (bend vertex) → corner junction;
        //   • 3+ wire-ends meeting at one point.
        // A pure 4-way crossing (two wires passing through, neither with a vertex) is NOT a
        // candidate — it has no vertex, so it never auto-connects; it joins only via a user dot.
        //
        // incident(P) = (segment-ends at P) + 2·(segments whose interior passes through P):
        //   a wire endpoint contributes 1, an interior vertex (bend) contributes 2, a segment
        //   split by P contributes 2. Evaluated only at wire vertices (a dot needs a vertex), so
        //   crossings are excluded by construction. ≥3 ⇒ a real junction ⇒ dot.
        //
        // A dot is drawn only when the incident segments form a real BRANCH — i.e. they are not all
        // collinear. Three+ collinear segments meeting (two wires overlapping/abutting on the same
        // line) is NOT a junction: it is redundant wire that should merge, so no dot is drawn there.
        // The branch test is "incident segments span both axes" (a horizontal AND a vertical one).
        //
        // 6e extraction note: every auto-dot is one node uniting the incident wire-ends/segments
        // (the through-wire is split at P). Do NOT implement extraction here.
        //
        // For each candidate vertex P, enumerate the segments incident at P (via the cell index):
        //   a segment with an ENDPOINT at P contributes 1; a segment whose interior P splits
        //   contributes 2. incident ≥ 3 ⇒ a connection point (autoDotKeys, for endpoint-connected
        //   state); additionally requiring both a horizontal and a vertical incident segment ⇒ a
        //   visible junction dot (autoDotPts).
        (int Incident, bool HasH, bool HasV) IncidentAt(double px, double py)
        {
            int incident = 0; bool hasH = false, hasV = false;
            var ck = ((long)Math.Floor(px / SegCell), (long)Math.Floor(py / SegCell));
            if (segIndex.TryGetValue(ck, out var cands))
                foreach (int si in cands)
                {
                    var s = segList[si];
                    int contrib;
                    if (SchematicGeometry.CoincidentPoints(px, py, s.ax, s.ay, ConnectTolerance) ||
                        SchematicGeometry.CoincidentPoints(px, py, s.bx, s.by, ConnectTolerance))
                        contrib = 1;                                   // segment ends at P
                    else if (SchematicGeometry.PointOnSegmentInterior(px, py, s.ax, s.ay, s.bx, s.by, ConnectTolerance))
                        contrib = 2;                                   // P splits the segment
                    else continue;                                     // not incident
                    incident += contrib;
                    if (Math.Abs(s.bx - s.ax) < Math.Abs(s.by - s.ay)) hasV = true; else hasH = true;
                }
            return (incident, hasH, hasV);
        }

        var autoDotKeys = new HashSet<(long, long)>();
        var autoDotPts  = new List<(double X, double Y)>();
        var evaluated   = new HashSet<(long, long)>();
        foreach (var w in Wires)
            foreach (var (px, py) in w.Points)
            {
                var key = QuantKey(px, py);
                if (!evaluated.Add(key)) continue;   // each distinct point judged once
                var (incident, hasH, hasV) = IncidentAt(px, py);
                if (incident < 3) continue;
                autoDotKeys.Add(key);                 // a connection point (endpoint-connected)
                if (hasH && hasV) autoDotPts.Add((px, py));   // a real branch → a visible dot
            }

        // ── 4-way crossing detection (§5.1) ───────────────────────────────────
        // Two wires whose segment interiors intersect at a point where NEITHER wire has a
        // vertex/endpoint is an ambiguous crossing: by EDA convention it connects ONLY if the
        // user places a junction dot there (the complement of the T, which auto-connects). The
        // wirePointHash guard makes this mutually exclusive with the endpoint-coincidence (merge)
        // and T cases — if any vertex sits at the point, it is handled by those paths, not here.
        //
        // 6e extraction note: at net extraction (6e), a user dot at a 4-way crossing unions the
        // two crossing wires into ONE node; a crossing WITHOUT a dot leaves them as two separate
        // nets (the wires pass over each other unconnected) — the dot-gated union, the complement
        // of the T's automatic union. Because the editor maintains the §5.1 invariant (a user dot
        // EXISTS iff it sits on a real crossing — rejected at placement, auto-removed when the
        // crossing dissolves), the extractor can treat every dot as a valid crossing-union with no
        // validity check. This predicate is the single definition of "valid crossing" reused by
        // dot rendering (AssembleConnectionDots) and re-validation (FindInvalidDots). Do NOT
        // implement extraction here.
        bool IsCrossingAtDot(double px, double py)
        {
            // A vertex here means it is a merge/T, not a pure crossing — defer to those paths.
            if (wirePointHash.Contains(QuantKey(px, py))) return false;
            var ck = ((long)Math.Floor(px / SegCell), (long)Math.Floor(py / SegCell));
            if (!segIndex.TryGetValue(ck, out var cands)) return false;
            int firstWi = -1;
            foreach (int si in cands)
            {
                var s = segList[si];
                if (!SchematicGeometry.PointOnSegmentInterior(px, py, s.ax, s.ay, s.bx, s.by, ConnectTolerance))
                    continue;
                if (firstWi == -1) firstWi = s.Wi;
                else if (s.Wi != firstWi) return true;   // two distinct wires cross here
            }
            return false;
        }

        // ── Port-coincidence dots ─────────────────────────────────────────────
        // Emit a junction dot wherever a component port coincides with another
        // connection endpoint (another port, a wire vertex, or a wire body interior).
        // Skips P-cells already covered by a wire auto-dot (no double-dots). O(N) via
        // the already-built segment cell-hash and conPointCounts.
        var portDotSeen = new HashSet<(long, long)>();

        void AddPortDot(double px, double py)
        {
            var pdKey = QuantKey(px, py);
            if (autoDotKeys.Contains(pdKey)) return;   // already covered by wire auto-dot
            if (!portDotSeen.Add(pdKey)) return;        // already processed this P-cell

            // Case 1: another endpoint (port or wire vertex) shares the P-cell.
            bool connected = conPointCounts.TryGetValue(pdKey, out int pdCnt) && pdCnt >= 2;

            // Case 2: port lands on a wire body interior (P-cell has no wire vertex).
            if (!connected)
            {
                var ck = ((long)Math.Floor(px / SegCell), (long)Math.Floor(py / SegCell));
                if (segIndex.TryGetValue(ck, out var cands))
                    foreach (int si in cands)
                    {
                        var s = segList[si];
                        if (SchematicGeometry.PointOnSegmentInterior(
                                px, py, s.ax, s.ay, s.bx, s.by, ConnectTolerance))
                        { connected = true; break; }
                    }
            }

            if (!connected) return;
            autoDotKeys.Add(pdKey);
            autoDotPts.Add((px, py));
        }

        foreach (var comp in Components)
        {
            foreach (var def in PortDefsOf(comp, cellRefResolutions))
            {
                if (comp.IsPortDetached(def.PortIndex)) continue;
                var (px, py) = PortWorldOf(comp, def);
                AddPortDot(px, py);
            }
        }

        return new ConnectivityGeometry(wirePointHash, conPointCounts, autoDotKeys, autoDotPts, IsCrossingAtDot);
    }

    /// <summary>
    /// Assembles the connection-dot list from connectivity geometry. By the §5.1 invariant a dot
    /// is rendered only where it marks a real connection:
    ///  • a user dot that sits on a genuine 4-way crossing (≥2 distinct wires' interiors meet, no
    ///    vertex there) — a user dot anywhere else is inert and is NOT rendered (and the matching
    ///    EditableDot is removed on the next geometry edit; see DotRevalidationCommand);
    ///  • a derived auto-dot at every junction where 3+ wire segments meet with a vertex present
    ///    (T, corner, or 3-way endpoint — NOT persisted, geometry-derived each build).
    /// Auto-dots and crossing dots are mutually exclusive (an auto-dot needs a vertex; a crossing
    /// has none), so the two sets never overlap.
    /// </summary>
    private List<SchematicDot> AssembleConnectionDots(ConnectivityGeometry cg)
    {
        var dots = new List<SchematicDot>(Dots.Count + cg.AutoDotPts.Count);
        foreach (var d in Dots)
            if (cg.IsCrossingAtDot(d.X, d.Y))           // invariant: render a user dot only on a real crossing
                dots.Add(new SchematicDot(d.X, d.Y));
        foreach (var (tx, ty) in cg.AutoDotPts)
            dots.Add(new SchematicDot(tx, ty));
        return dots;
    }

    /// <summary>
    /// Returns the user EditableDots that violate the §5.1 invariant — i.e. no longer sit on a
    /// genuine 4-way crossing (the crossing dissolved because a wire moved/was deleted, or the
    /// point became a T/merge, or the dot was never on a crossing). Callers remove these as part
    /// of the same undoable edit that dissolved the crossing. O(N): one connectivity pass.
    /// </summary>
    public List<EditableDot> FindInvalidDots()
    {
        if (Dots.Count == 0) return [];
        var cg = ComputeConnectivityGeometry();
        var invalid = new List<EditableDot>();
        foreach (var d in Dots)
            if (!cg.IsCrossingAtDot(d.X, d.Y)) invalid.Add(d);
        return invalid;
    }

    /// <summary>Snapshot of a net label's anchor before revalidation changed it (for undo).</summary>
    public readonly record struct NetLabelAnchorSnap(
        EditableNetLabel Label,
        string OwnerWireId, int SegmentIndex, double AlongT,
        double OffsetX, double OffsetY, double X, double Y);

    /// <summary>What a net-label revalidation pass changed (for undo by the wrapping command).</summary>
    public readonly record struct NetLabelRevalidation(
        List<(EditableNetLabel Label, int Index)> Removed,
        List<NetLabelAnchorSnap> Reanchored);

    /// <summary>First wire whose body passes through (px,py) within <paramref name="tol"/>, else null.</summary>
    public EditableWire? WireUnderPoint(double px, double py, double tol)
    {
        foreach (var w in Wires)
        {
            var pts = w.Points;
            for (int i = 0; i < pts.Count - 1; i++)
                if (SchematicGeometry.PointOnSegment(px, py, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, tol))
                    return w;
        }
        return null;
    }

    /// <summary>
    /// Re-enforces the net-label invariant after a geometry edit (no label hangs unassigned):
    ///  • valid anchor (owner exists, segment in range) → untouched (BuildRenderModel keeps X,Y fresh);
    ///  • owner exists but segment renumbered/shortened → re-anchor on the same wire at the current draw point;
    ///  • owner gone (deleted / merged / split) → re-home to a wire under the label's foot if one exists
    ///    (merge &amp; split preserve geometry, so the foot still lands on the surviving wire), else remove it.
    /// Returns the changes so the wrapping command can undo them. Mutates in place.
    /// </summary>
    public NetLabelRevalidation RevalidateNetLabels()
    {
        List<(EditableNetLabel, int)> removed    = [];
        List<NetLabelAnchorSnap>      reanchored = [];

        for (int i = NetLabels.Count - 1; i >= 0; i--)
        {
            var l = NetLabels[i];
            if (!l.IsAnchored) continue;   // legacy free label — leave alone

            var owner = FindWire(l.OwnerWireId);
            if (owner is not null && l.SegmentIndex >= 0 && l.SegmentIndex < owner.Points.Count - 1)
                continue;                  // valid anchor — no change needed

            var snap = new NetLabelAnchorSnap(
                l, l.OwnerWireId, l.SegmentIndex, l.AlongT, l.OffsetX, l.OffsetY, l.X, l.Y);

            if (owner is not null)
            {
                // Owner exists but its segment list changed under the label — re-anchor on it,
                // keeping the label's current draw position.
                l.AnchorToWire(owner, l.X, l.Y);
                reanchored.Add(snap);
                continue;
            }

            // Owner gone. Re-home to a wire coincident with the label's foot (merge/split keep geometry);
            // if the foot lies on no wire, the node is gone → remove the label.
            double footX = l.X - l.OffsetX, footY = l.Y - l.OffsetY;
            var host = WireUnderPoint(footX, footY, ConnectTolerance);
            if (host is not null)
            {
                l.AnchorToWire(host, l.X, l.Y);
                reanchored.Add(snap);
            }
            else
            {
                removed.Add((l, i));
                NetLabels.RemoveAt(i);
            }
        }

        return new NetLabelRevalidation(removed, reanchored);
    }

    /// <summary>
    /// Computes just the connection dots from the current geometry — the same result as
    /// BuildRenderModel's ConnectionDots, without building the full render model. Used for the
    /// live dot preview during drags (the geometry is live-mutated as the drag progresses).
    /// O(N) like the full connectivity pass.
    /// </summary>
    public IReadOnlyList<SchematicDot> ComputeConnectionDots()
        => AssembleConnectionDots(ComputeConnectivityGeometry());

    // ── Factory: convert from legacy demo render model ────────────────────────

    /// <summary>
    /// Import an existing immutable SchematicModel (from SchematicModelBuilder demos)
    /// into this editable model. Parameters are synthesized from the labels.
    /// </summary>
    public static SchematicEditModel FromRenderModel(SchematicModel src)
    {
        var m = new SchematicEditModel
        {
            GridSize = src.GridSize,
        };

        foreach (var rc in src.Components)
        {
            var info = ComponentTypeRegistry.Get(rc.Symbol);
            var c = new EditableComponent
            {
                InstanceName     = rc.InstanceName,
                Symbol           = rc.Symbol,
                X = rc.X, Y = rc.Y,
                Rotation         = rc.Rotation,
                MirrorX          = rc.MirrorX,
                ShowTypeLabel    = info.DefaultShowTypeLabel,
                ShowInstanceName = info.DefaultShowInstanceName,
            };
            // For variadic types derive N from the type label ("Z1P"→1, "SDD2"→2).
            // Fallback by pin count: ZPort and SDD both use 2N pins (N = pins/2).
            int portCount = 0;
            if (rc.Symbol is SymbolKind.ZPort or SymbolKind.Sdd)
            {
                if (rc.Labels.Count > 0 &&
                    ComponentTypeRegistry.TryParseCode(rc.Labels[0], out _, out int parsed) && parsed >= 1)
                    portCount = parsed;
                else
                    portCount = Math.Max(1, rc.Ports.Count / 2);
            }
            var template = ComponentTypeRegistry.DefaultParameters(rc.Symbol, portCount);

            // Match shown labels to shown template slots; skip hidden params (e.g. NumPorts).
            // Without the skip, slot 0 (NumPorts, hidden) would consume Labels[2] (the first
            // visible param label), assigning the impedance value to NumPorts and corrupting PortCount.
            int li = 2;   // next label to consume: Labels[0]=type, Labels[1]=name, Labels[2+]=shown params
            foreach (var tp in template)
            {
                string expr;
                if (tp.ShowOnSchematic)
                    expr = li < rc.Labels.Count ? ExtractExpressionFromLabel(rc.Labels[li++]) : "";
                else
                    expr = tp.Expression;   // hidden: use template default, never a label
                c.Parameters.Add(new EditableParameter
                    { Name = tp.Name, Expression = expr, Unit = tp.Unit, ShowOnSchematic = tp.ShowOnSchematic, Dimension = tp.Dimension });
            }
            m.Components.Add(c);
        }

        foreach (var rw in src.Wires)
        {
            var w = new EditableWire();
            w.Points.AddRange(rw.Points);
            m.Wires.Add(w);
        }

        foreach (var rd in src.ConnectionDots)
            m.Dots.Add(new EditableDot { X = rd.X, Y = rd.Y });

        return m;
    }

    // ── Selection helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the bare expression from a pre-formatted parameter label.
    /// Labels produced by MakeComponent/ToRenderComponent have the form:
    ///   "Name = Expr Unit", "Name = Expr", "Expr Unit", or "Expr".
    /// The "Name = " prefix is stripped; the trailing unit token (if any) is stripped;
    /// only the expression portion is returned.
    /// </summary>
    private static string ExtractExpressionFromLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";

        // Strip "Name = " prefix (produced by ToRenderComponent when Name is non-empty)
        int eqIdx = label.IndexOf(" = ", StringComparison.Ordinal);
        string exprUnit = eqIdx >= 0 ? label[(eqIdx + 3)..].TrimStart() : label.Trim();

        // Strip trailing unit token: last whitespace-separated token starting with a letter
        int lastSpace = exprUnit.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            string tail = exprUnit[(lastSpace + 1)..];
            if (tail.Length > 0 && char.IsLetter(tail[0]))
                return exprUnit[..lastSpace].Trim();
        }
        return exprUnit;
    }

    public EditableComponent?  FindComponent(string id) => Components.FirstOrDefault(c => c.Id == id);
    public EditableWire?       FindWire(string id)      => Wires.FirstOrDefault(w => w.Id == id);
    public EditableDot?        FindDot(string id)       => Dots.FirstOrDefault(d => d.Id == id);
    public EditableCanvasObject? FindCanvasObject(string id) => CanvasObjects.FirstOrDefault(o => o.Id == id);
    public EditableNetLabel?   FindNetLabel(string id)  => NetLabels.FirstOrDefault(n => n.Id == id);

    // ── Shared name-generation helper ─────────────────────────────────────────

    /// <summary>
    /// Returns the next available instance name for <paramref name="prefix"/>: the lowest
    /// positive integer N such that "{prefix}{N}" is not in <paramref name="existingNames"/>.
    /// Example: existing = {"R1","R2","R5"}, prefix = "R" → "R3".
    /// Shared by the place path, type-change path, and paste path so only one implementation exists.
    /// </summary>
    public static string NextAvailableName(IEnumerable<string> existingNames, string prefix)
    {
        var used = existingNames
            .Where(n => n.StartsWith(prefix))
            .Select(n => n[prefix.Length..])
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse)
            .ToHashSet();
        int i = 1;
        while (used.Contains(i)) i++;
        return prefix + i;
    }

    /// <summary>Convenience overload: scans component instance names.</summary>
    public static string NextAvailableName(IEnumerable<EditableComponent> existing, string prefix)
        => NextAvailableName(existing.Select(c => c.InstanceName), prefix);

    /// <summary>Convenience overload: derives the prefix from the registry.</summary>
    public static string NextAvailableName(IEnumerable<EditableComponent> existing, SymbolKind kind)
        => NextAvailableName(existing, ComponentTypeRegistry.InstancePrefix(kind));
}

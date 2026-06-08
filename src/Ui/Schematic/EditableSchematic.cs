using CircuitRF.Ui.Renderers;

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
    /// <summary>Unit string (e.g. "nH", "pF", "ohm"). Empty when dimensionless.</summary>
    public string Unit            { get; set; } = "";
    public bool   ShowOnSchematic { get; set; } = true;

    public EditableParameter Clone() => new()
    {
        Name = Name, Expression = Expression, Unit = Unit, ShowOnSchematic = ShowOnSchematic,
    };
}

/// <summary>
/// Per-symbol port definitions (local coordinates, 100 units = 1 grid square).
/// Matches the rendering geometry in SchematicSymbols.cs.
/// Port indices are 0-based internally; they map to 1-based user-facing port numbers
/// following the port-index convention in project-file-formats.md.
/// </summary>
public static class SymbolPortDefs
{
    public static (string Name, float LocalX, float LocalY)[] For(SymbolKind kind) => kind switch
    {
        SymbolKind.Ground   => [("1",      0f,    0f)],
        SymbolKind.Port     => [("1",   -150f,    0f)],
        SymbolKind.FetSdd   => [("gate", -150f,   0f),
                                ("drain",  150f,-100f),
                                ("source", 150f, 100f)],
        _                   => [("1",   -150f,    0f),
                                ("2",    150f,    0f)],
    };
}

// ── Placed component ─────────────────────────────────────────────────────────

/// <summary>A placed, editable component instance.</summary>
public sealed class EditableComponent
{
    public string         Id           { get; } = Guid.NewGuid().ToString("N")[..12];
    public string         InstanceName { get; set; } = "";
    public SymbolKind     Symbol       { get; set; }
    public double         X            { get; set; }
    public double         Y            { get; set; }
    public SymbolRotation Rotation     { get; set; }
    public bool           MirrorX      { get; set; }
    public DisableState   Disable      { get; set; } = DisableState.None;
    public List<EditableParameter> Parameters   { get; } = new();
    /// <summary>Per-label world-offset from default position. Index matches Labels list (0=type,1=name,2+=params).</summary>
    public List<(double DX, double DY)> LabelOffsets { get; } = new();

    public (double DX, double DY) GetLabelOffset(int index)
        => index < LabelOffsets.Count ? LabelOffsets[index] : (0, 0);

    private const double HalfBound = 200.0;  // matches SchematicModelBuilder

    public (double MinX, double MinY, double MaxX, double MaxY) GetBoundingBox()
        => (X - HalfBound, Y - HalfBound, X + HalfBound, Y + HalfBound);

    /// <summary>World coordinates of a port by 0-based port index.</summary>
    public (double X, double Y) GetPortWorldCoord(int portIndex)
    {
        var ports = SymbolPortDefs.For(Symbol);
        if ((uint)portIndex >= (uint)ports.Length)
            throw new ArgumentOutOfRangeException(nameof(portIndex));
        var (_, lx, ly) = ports[portIndex];
        return SchematicGeometry.LocalToWorld(lx, ly, X, Y, Rotation, MirrorX);
    }

    /// <summary>Number of ports on this symbol.</summary>
    public int PortCount => SymbolPortDefs.For(Symbol).Length;

    /// <summary>Convert to the immutable render type, with port connection state.</summary>
    public SchematicComponent ToRenderComponent(Func<double, double, bool>? isPointConnected = null)
    {
        var portDefs = SymbolPortDefs.For(Symbol);
        var ports = portDefs.Select((p, _) =>
        {
            var (wx, wy) = SchematicGeometry.LocalToWorld(p.LocalX, p.LocalY, X, Y, Rotation, MirrorX);
            bool conn = isPointConnected?.Invoke(wx, wy) ?? false;
            return new SchematicPortDef(p.Name, p.LocalX, p.LocalY,
                conn ? PortConnectionState.Connected : PortConnectionState.Unconnected);
        }).ToList();

        // Labels in display order: type (from registry), instance name, then ShowOnSchematic params.
        // Ground never shows its instance name — the symbol is self-identifying.
        // Param format: "<Name> = <Expression> <Unit>" (spaces around =; unit omitted when empty).
        var labels = new List<string>
        {
            ComponentTypeRegistry.DisplayName(Symbol, PortCount),
            Symbol == SymbolKind.Ground ? "" : InstanceName,
        };
        foreach (var p in Parameters)
        {
            if (!p.ShowOnSchematic || string.IsNullOrEmpty(p.Expression)) continue;
            string val = string.IsNullOrEmpty(p.Unit) ? p.Expression : $"{p.Expression} {p.Unit}";
            labels.Add(string.IsNullOrEmpty(p.Name) ? val : $"{p.Name} = {val}");
        }

        var bb = GetBoundingBox();
        var (glyphMinX, glyphMinY, glyphMaxX, glyphMaxY) = ComputeGlyphBb();

        return new SchematicComponent
        {
            Id           = Id,
            InstanceName = InstanceName,
            Symbol       = Symbol,
            X = X, Y = Y,
            Rotation     = Rotation,
            MirrorX      = MirrorX,
            DisableState = Disable,
            Ports        = ports,
            Labels       = labels,
            LabelOffsets = LabelOffsets.Count > 0 ? LabelOffsets.ToList() : [],
            BbMinX = bb.MinX, BbMinY = bb.MinY,
            BbMaxX = bb.MaxX, BbMaxY = bb.MaxY,
            GlyphBbMinX = glyphMinX, GlyphBbMinY = glyphMinY,
            GlyphBbMaxX = glyphMaxX, GlyphBbMaxY = glyphMaxY,
        };
    }

    /// <summary>Axis-aligned bounding box of the symbol geometry in world coordinates.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) ComputeGlyphBb()
    {
        float[] segs = SchematicSymbols.For(Symbol);
        if (segs.Length < 4) return (X - 160, Y - 60, X + 160, Y + 60);

        float lMinX = float.MaxValue, lMinY = float.MaxValue;
        float lMaxX = float.MinValue, lMaxY = float.MinValue;
        for (int i = 0; i + 3 < segs.Length; i += 4)
        {
            lMinX = Math.Min(lMinX, Math.Min(segs[i],     segs[i + 2]));
            lMinY = Math.Min(lMinY, Math.Min(segs[i + 1], segs[i + 3]));
            lMaxX = Math.Max(lMaxX, Math.Max(segs[i],     segs[i + 2]));
            lMaxY = Math.Max(lMaxY, Math.Max(segs[i + 1], segs[i + 3]));
        }
        const float pad = 15f;
        // Transform all four local corners and take world BB
        var corners = new[]
        {
            SchematicGeometry.LocalToWorld(lMinX - pad, lMinY - pad, X, Y, Rotation, MirrorX),
            SchematicGeometry.LocalToWorld(lMaxX + pad, lMinY - pad, X, Y, Rotation, MirrorX),
            SchematicGeometry.LocalToWorld(lMinX - pad, lMaxY + pad, X, Y, Rotation, MirrorX),
            SchematicGeometry.LocalToWorld(lMaxX + pad, lMaxY + pad, X, Y, Rotation, MirrorX),
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
            InstanceName = InstanceName, Symbol = Symbol,
            X = X, Y = Y, Rotation = Rotation, MirrorX = MirrorX, Disable = Disable,
        };
        foreach (var p in Parameters)    c.Parameters.Add(p.Clone());
        foreach (var o in LabelOffsets) c.LabelOffsets.Add(o);
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

/// <summary>A user-placed net label (§4.4). Stored for 6e extraction.</summary>
public sealed class EditableNetLabel
{
    public string Id   { get; } = Guid.NewGuid().ToString("N")[..12];
    public double X    { get; set; }
    public double Y    { get; set; }
    public string Name { get; set; } = "";
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
    public List<EditableComponent>  Components   { get; } = new();
    public List<EditableWire>       Wires        { get; } = new();
    public List<EditableNetLabel>   NetLabels    { get; } = new();
    public List<EditableDot>        Dots         { get; } = new();
    public List<EditableCanvasObject> CanvasObjects { get; } = new();

    public double GridSize { get; set; } = 100.0;
    public bool   GridSnap { get; set; } = true;

    // View state (saved/restored with .csch)
    public double ViewPanX { get; set; }
    public double ViewPanY { get; set; }
    public double ViewZoom { get; set; } = 1.0;

    // Fired by commands after each mutation; SchematicViewModel subscribes.
    public event EventHandler? Changed;
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public double SnapToGrid(double v)
        => GridSnap ? Math.Round(v / GridSize) * GridSize : v;

    // ── Render model build ────────────────────────────────────────────────────

    /// <summary>Quantization / coincidence tolerance for the connectivity pass (world units).
    /// Public so editing code (e.g. T-junction stem-follow) detects T's at the identical
    /// tolerance the render-model T-detection uses, keeping visuals and editing in agreement.</summary>
    public const double ConnectTolerance = 6.0;

    /// <summary>
    /// Builds an immutable SchematicModel + spatial index from current state.
    /// Port connection state is determined by local geometric adjacency (§4.3).
    /// Called by SchematicViewModel after each model change.
    /// Connectivity pass is O(N) via spatial hash (not O(N²) linear scan).
    /// </summary>
    public (SchematicModel Model, SchematicSpatialIndex Index) BuildRenderModel()
    {
        // Connectivity geometry (vertex hashes, T-junctions, crossing predicate) is computed by
        // a shared helper so the live dot preview during drags reuses the identical logic.
        var cg = ComputeConnectivityGeometry();

        // IsConnected for a port: O(1) hash lookup; rare fallback to segment scan on miss.
        bool IsConnected(double wx, double wy)
        {
            if (cg.WirePointHash.Contains(QuantKey(wx, wy))) return true;
            // Fallback: mid-segment connection (port lands on a wire body, not just endpoints).
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

        var comps = Components.Select(c => c.ToRenderComponent(IsConnected)).ToList();
        var wires = Wires.Select(w => w.ToRenderWire(IsEndpointConnected)).ToList();
        var dots  = AssembleConnectionDots(cg);
        var netLabels = NetLabels.Select(l => new SchematicNetLabel { Id = l.Id, X = l.X, Y = l.Y, Name = l.Name }).ToList();

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
            GridSize       = GridSize,
            BbMinX = minX - 200, BbMinY = minY - 200,
            BbMaxX = maxX + 200, BbMaxY = maxY + 200,
        };

        return (model, new SchematicSpatialIndex(model));
    }

    // ── Connectivity helpers (shared by BuildRenderModel and the live dot preview) ──

    /// <summary>Quantize a point to a ConnectTolerance grid cell; near-coincident points
    /// rounding to the same cell are treated as the same connection point.</summary>
    private static (long, long) QuantKey(double x, double y)
        => ((long)Math.Round(x / ConnectTolerance), (long)Math.Round(y / ConnectTolerance));

    /// <summary>Geometry derived for the connectivity pass: vertex hashes, auto-junction points
    /// (3+ segment meetings with a vertex), and a crossing predicate. Shared so the render model
    /// and the live drag preview agree.</summary>
    private readonly record struct ConnectivityGeometry(
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
    private ConnectivityGeometry ComputeConnectivityGeometry()
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
            for (int pi = 0; pi < comp.PortCount; pi++)
            {
                var (px, py) = comp.GetPortWorldCoord(pi);
                AddConPoint(px, py);
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
            var c = new EditableComponent
            {
                InstanceName = rc.InstanceName,
                Symbol       = rc.Symbol,
                X = rc.X, Y = rc.Y,
                Rotation     = rc.Rotation,
                MirrorX      = rc.MirrorX,
            };
            // Build parameters from the registry template (Name/Unit/ShowOnSchematic from template).
            // For each template slot, extract the expression from the matching label when present.
            // Labels[2+] have the form "Name = Expr Unit" or "Expr Unit"; we strip the "Name = "
            // prefix and unit suffix to recover the bare expression. Template Name/Unit win over
            // whatever the label carried — only the expression value is taken from the label.
            var template = ComponentTypeRegistry.DefaultParameters(rc.Symbol, rc.Ports.Count);
            for (int ti = 0; ti < template.Count; ti++)
            {
                var tp = template[ti];
                int li = ti + 2;                       // Labels[0]=type, Labels[1]=name, Labels[2+]=params
                string expr = li < rc.Labels.Count
                    ? ExtractExpressionFromLabel(rc.Labels[li])
                    : "";
                c.Parameters.Add(new EditableParameter
                    { Name = tp.Name, Expression = expr, Unit = tp.Unit, ShowOnSchematic = tp.ShowOnSchematic });
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

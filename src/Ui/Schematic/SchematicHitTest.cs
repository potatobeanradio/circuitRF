namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Hit-testing for the schematic canvas.
/// Uses the SchematicSpatialIndex for fast candidate narrowing, then exact
/// geometry for the final test. No Avalonia types — headless-testable.
/// </summary>
public static class SchematicHitTest
{
    private const double DefaultHitRadius = 12.0;
    private const double WireHitTol       = 8.0;
    private const double EndpointHitTol   = 12.0;

    private const double CharWidthWorld = 38.5;   // textSize_world(70) × ~0.55 avg char ratio

    // Net label: PlexItalic at 65 world units, drawn at stored lbl.X/lbl.Y (no render-time shift).
    private const double NetLabelCharWidth     = 36.0;  // 65 × ~0.55 avg char ratio
    private const double NetLabelAboveBaseline = 47.0;  // ascender height at font size 65 (measured)
    private const double NetLabelBelowBaseline = 17.0;  // descender(15) + 2 click comfort

    public enum HitKind
    {
        None,
        Component,       // component symbol glyph
        ComponentType,   // type text label (row 0)
        ComponentName,   // instance-name text label (row 1)
        ComponentParam,  // parameter text label; SubIndex = param index
        Wire,            // whole-wire (returned by TestRect rubber-band only)
        WireSegment,     // single segment of a wire (returned by Test point-click); SubIndex = segment index i (pts[i]→pts[i+1])
        WireEndpoint,    // first or last wire point; SubIndex = point index
        Dot,
        NetLabel,
        CanvasObject,
    }

    public enum SelectMode { Window, Crossing }

    public readonly record struct HitResult(
        HitKind Kind,
        string Id,
        int SubIndex = 0,
        double LabelWorldX = 0,
        double LabelWorldY = 0);

    /// <summary>
    /// Returns the topmost object under (worldX, worldY).
    /// Z-order: component labels > component body > canvas-objects > wire endpoints > wires > dots.
    /// </summary>
    public static HitResult Test(
        SchematicEditModel  editModel,
        SchematicModel      renderModel,
        SchematicSpatialIndex index,
        double worldX, double worldY,
        double hitRadius = DefaultHitRadius,
        bool includeLabels = true)
    {
        double half = hitRadius;
        var candComps = new HashSet<int>();
        var candWires = new HashSet<int>();
        index.QueryViewport(worldX - half, worldY - half, worldX + half, worldY + half,
                            candComps, candWires);

        // ── 1. Text label zones (highest Z) ──────────────────────────────────
        if (includeLabels)
        {
            foreach (int i in candComps.OrderByDescending(x => x))
            {
                if (i >= editModel.Components.Count) continue;
                var textHit = TestComponentLabels(editModel.Components[i], worldX, worldY);
                if (textHit.Kind != HitKind.None) return textHit;
            }
        }

        // ── 2. Component symbol glyphs ────────────────────────────────────────
        foreach (int i in candComps.OrderByDescending(x => x))
        {
            if (i >= editModel.Components.Count) continue;
            var comp = editModel.Components[i];
            var (gMinX, gMinY, gMaxX, gMaxY) = GetCompGlyphBb(comp, editModel);
            if (worldX >= gMinX && worldX <= gMaxX && worldY >= gMinY && worldY <= gMaxY)
                return new HitResult(HitKind.Component, comp.Id);
        }

        // ── 3. Canvas objects ─────────────────────────────────────────────────
        for (int i = editModel.CanvasObjects.Count - 1; i >= 0; i--)
        {
            var obj = editModel.CanvasObjects[i];
            if (obj.IsLocked) continue;
            var bb = obj.GetBoundingBox();
            if (worldX >= bb.MinX && worldX <= bb.MaxX && worldY >= bb.MinY && worldY <= bb.MaxY)
                return new HitResult(HitKind.CanvasObject, obj.Id);
        }

        // ── 4. Wire endpoints ─────────────────────────────────────────────────
        foreach (int i in candWires.OrderByDescending(x => x))
        {
            if (i >= editModel.Wires.Count) continue;
            var wire = editModel.Wires[i];
            if (wire.Points.Count == 0) continue;

            if (SchematicGeometry.CoincidentPoints(worldX, worldY,
                    wire.Points[0].X, wire.Points[0].Y, EndpointHitTol))
                return new HitResult(HitKind.WireEndpoint, wire.Id, SubIndex: 0);

            int last = wire.Points.Count - 1;
            if (SchematicGeometry.CoincidentPoints(worldX, worldY,
                    wire.Points[last].X, wire.Points[last].Y, EndpointHitTol))
                return new HitResult(HitKind.WireEndpoint, wire.Id, SubIndex: last);
        }

        // ── 5. Wires ──────────────────────────────────────────────────────────
        foreach (int i in candWires.OrderByDescending(x => x))
        {
            if (i >= editModel.Wires.Count) continue;
            var wire = editModel.Wires[i];
            var pts = wire.Points;
            for (int pi = 0; pi < pts.Count - 1; pi++)
            {
                if (SchematicGeometry.PointOnSegment(
                        worldX, worldY,
                        pts[pi].X, pts[pi].Y, pts[pi + 1].X, pts[pi + 1].Y,
                        WireHitTol))
                    return new HitResult(HitKind.WireSegment, wire.Id, SubIndex: pi);
            }
        }

        // ── 6. Dots ───────────────────────────────────────────────────────────
        foreach (var dot in editModel.Dots)
        {
            if (SchematicGeometry.CoincidentPoints(worldX, worldY, dot.X, dot.Y, hitRadius))
                return new HitResult(HitKind.Dot, dot.Id);
        }

        // ── 7. Net labels ─────────────────────────────────────────────────────
        foreach (var lbl in editModel.NetLabels)
        {
            if (worldY < lbl.Y - NetLabelAboveBaseline || worldY > lbl.Y + NetLabelBelowBaseline) continue;
            double right = lbl.X + lbl.Name.Length * NetLabelCharWidth;
            if (worldX >= lbl.X - 8 && worldX <= right + 8)
                return new HitResult(HitKind.NetLabel, lbl.Id);
        }

        return new HitResult(HitKind.None, "");
    }

    /// <summary>
    /// Returns every selectable object under (worldX, worldY), ordered top→bottom (same Z-priority
    /// as <see cref="Test"/>), at most one entry per object. Used for cyclic click-through selection.
    /// Labels are excluded unless includeLabels is true (left-click selection ignores labels — B9).
    /// Each wire contributes a single entry: a WireEndpoint (whole-wire) hit when the point is near an
    /// endpoint, otherwise the WireSegment under the point.
    /// </summary>
    public static IReadOnlyList<HitResult> TestStack(
        SchematicEditModel    editModel,
        SchematicModel        renderModel,
        SchematicSpatialIndex index,
        double worldX, double worldY,
        double hitRadius = DefaultHitRadius,
        bool   includeLabels = false)
    {
        double half = hitRadius;
        var candComps = new HashSet<int>();
        var candWires = new HashSet<int>();
        index.QueryViewport(worldX - half, worldY - half, worldX + half, worldY + half,
                            candComps, candWires);

        var results = new List<HitResult>();

        // 1. Labels (only if requested) — topmost.
        if (includeLabels)
            foreach (int i in candComps.OrderByDescending(x => x))
            {
                if (i >= editModel.Components.Count) continue;
                var th = TestComponentLabels(editModel.Components[i], worldX, worldY);
                if (th.Kind != HitKind.None) results.Add(th);
            }

        // 2. Component glyphs (descending index = topmost first).
        foreach (int i in candComps.OrderByDescending(x => x))
        {
            if (i >= editModel.Components.Count) continue;
            var comp = editModel.Components[i];
            var (gMinX, gMinY, gMaxX, gMaxY) = GetCompGlyphBb(comp, editModel);
            if (worldX >= gMinX && worldX <= gMaxX && worldY >= gMinY && worldY <= gMaxY)
                results.Add(new HitResult(HitKind.Component, comp.Id));
        }

        // 3. Canvas objects (topmost first).
        for (int i = editModel.CanvasObjects.Count - 1; i >= 0; i--)
        {
            var obj = editModel.CanvasObjects[i];
            if (obj.IsLocked) continue;
            var bb = obj.GetBoundingBox();
            if (worldX >= bb.MinX && worldX <= bb.MaxX && worldY >= bb.MinY && worldY <= bb.MaxY)
                results.Add(new HitResult(HitKind.CanvasObject, obj.Id));
        }

        // 4. Wires — one entry per wire (endpoint → whole-wire; else the segment under the point).
        foreach (int i in candWires.OrderByDescending(x => x))
        {
            if (i >= editModel.Wires.Count) continue;
            var wire = editModel.Wires[i];
            var pts  = wire.Points;
            if (pts.Count == 0) continue;

            if (SchematicGeometry.CoincidentPoints(worldX, worldY, pts[0].X, pts[0].Y, EndpointHitTol))
            { results.Add(new HitResult(HitKind.WireEndpoint, wire.Id, SubIndex: 0)); continue; }

            int last = pts.Count - 1;
            if (SchematicGeometry.CoincidentPoints(worldX, worldY, pts[last].X, pts[last].Y, EndpointHitTol))
            { results.Add(new HitResult(HitKind.WireEndpoint, wire.Id, SubIndex: last)); continue; }

            for (int pi = 0; pi < pts.Count - 1; pi++)
                if (SchematicGeometry.PointOnSegment(
                        worldX, worldY, pts[pi].X, pts[pi].Y, pts[pi + 1].X, pts[pi + 1].Y, WireHitTol))
                { results.Add(new HitResult(HitKind.WireSegment, wire.Id, SubIndex: pi)); break; }
        }

        // 5. Dots.
        foreach (var dot in editModel.Dots)
            if (SchematicGeometry.CoincidentPoints(worldX, worldY, dot.X, dot.Y, hitRadius))
                results.Add(new HitResult(HitKind.Dot, dot.Id));

        // 6. Net labels.
        foreach (var lbl in editModel.NetLabels)
        {
            if (worldY < lbl.Y - NetLabelAboveBaseline || worldY > lbl.Y + NetLabelBelowBaseline) continue;
            double right = lbl.X + lbl.Name.Length * NetLabelCharWidth;
            if (worldX >= lbl.X - 8 && worldX <= right + 8)
                results.Add(new HitResult(HitKind.NetLabel, lbl.Id));
        }

        return results;
    }

    private static HitResult TestComponentLabels(EditableComponent comp, double wx, double wy)
    {
        // Test each visible label row using canonical geometry from SchematicComponent.LabelRowGeometry
        // so the clickable zone always tracks the rendered text (Bug A fix — single source of truth).
        // SubIndex in the returned HitResult is the index in the FULL Parameters list (not filtered).

        var shownParams = new List<(int FullIndex, EditableParameter Param)>();
        for (int pi = 0; pi < comp.Parameters.Count; pi++)
        {
            var p = comp.Parameters[pi];
            if (p.ShowOnSchematic && !string.IsNullOrEmpty(p.Expression))
                shownParams.Add((pi, p));
        }

        int totalRows = 2 + shownParams.Count;
        for (int row = 0; row < totalRows; row++)
        {
            bool suppressed = row switch
            {
                0 => comp.Symbol == SymbolKind.Ground || !comp.ShowTypeLabel,
                1 => comp.Symbol == SymbolKind.Ground || !comp.ShowInstanceName,
                _ => false,
            };
            if (suppressed) continue;

            var (oDx, oDy) = row < comp.LabelOffsets.Count ? comp.LabelOffsets[row] : (0.0, 0.0);
            // SnP and the Tuner family grow their glyph downward (Tuner: a bias branch when
            // ShowBias=true), so the label band must clear the real glyph extent — matching the
            // renderer's DrawLabels. Passing null left the clickable zone too high over those tuners.
            double? glyphHalfH = comp.Symbol is SymbolKind.Snp
                or SymbolKind.Tuner or SymbolKind.SourceTuner or SymbolKind.LoadTuner
                ? comp.ComputeGlyphBb().MaxY - comp.Y
                : null;
            var (baseX, _, bandTop, bandBot) =
                SchematicComponent.LabelRowGeometry(comp.X, comp.Y, row, oDx, oDy, comp.Symbol, comp.PortCount, glyphHalfH);

            if (wy < bandTop || wy > bandBot) continue;

            string labelText = row switch
            {
                0 => ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount),
                1 => comp.Symbol == SymbolKind.Ground ? "" : comp.InstanceName,
                _ => ParamLabelText(shownParams[row - 2].Param),
            };

            double textLeft  = baseX - 10;
            double textRight = baseX + labelText.Length * CharWidthWorld + 10;
            if (wx < textLeft || wx > textRight) continue;

            double centerY = (bandTop + bandBot) * 0.5;
            return row switch
            {
                0 => new HitResult(HitKind.ComponentType,  comp.Id, 0, baseX, centerY),
                1 => new HitResult(HitKind.ComponentName,  comp.Id, 0, baseX, centerY),
                _ => new HitResult(HitKind.ComponentParam, comp.Id, shownParams[row - 2].FullIndex, baseX, centerY),
            };
        }
        return new HitResult(HitKind.None, "");
    }

    /// <summary>Returns the rendered label text for a parameter (matches ToRenderComponent format).</summary>
    private static string ParamLabelText(EditableParameter p)
    {
        string val = string.IsNullOrEmpty(p.Unit) ? p.Expression : $"{p.Expression} {p.Unit}";
        return string.IsNullOrEmpty(p.Name) ? val : $"{p.Name} = {val}";
    }

    // ── Rect selection ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all objects satisfying the selection mode.
    /// Window: object fully inside rect. Crossing: object intersects rect + connected expansion.
    /// </summary>
    public static IReadOnlyList<HitResult> TestRect(
        SchematicEditModel editModel,
        SchematicModel renderModel,
        SchematicSpatialIndex index,
        double x0, double y0, double x1, double y1,
        SelectMode mode = SelectMode.Window)
    {
        double minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
        double minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);

        var results = new List<HitResult>();

        var candComps = new HashSet<int>();
        var candWires = new HashSet<int>();
        index.QueryViewport(minX, minY, maxX, maxY, candComps, candWires);

        // Both Window and Crossing use the same overlap test (glyph hitbox touches the rect).
        // Crossing adds wire + next-component expansion via ExpandCrossing; Window does not.
        bool Fits(double oMinX, double oMinY, double oMaxX, double oMaxY) =>
            oMaxX >= minX && oMinX <= maxX && oMaxY >= minY && oMinY <= maxY;

        foreach (int i in candComps)
        {
            if (i >= editModel.Components.Count) continue;
            var comp = editModel.Components[i];
            var (gMinX, gMinY, gMaxX, gMaxY) = GetCompGlyphBb(comp, editModel);
            if (Fits(gMinX, gMinY, gMaxX, gMaxY))
                results.Add(new HitResult(HitKind.Component, comp.Id));
        }

        foreach (int i in candWires)
        {
            if (i >= editModel.Wires.Count) continue;
            var wire = editModel.Wires[i];
            var pts  = wire.Points;
            if (pts.Count < 2) continue;
            // Per-segment check (exact for orthogonal wires; avoids false hits in the empty corner
            // of L-shaped wires that the overall AABB would include).
            bool hitWire = false;
            for (int pi = 0; pi < pts.Count - 1 && !hitWire; pi++)
                hitWire = SchematicGeometry.SegmentIntersectsRect(
                    pts[pi].X, pts[pi].Y, pts[pi + 1].X, pts[pi + 1].Y,
                    minX, minY, maxX, maxY);
            if (hitWire)
                results.Add(new HitResult(HitKind.Wire, wire.Id));
        }

        foreach (var obj in editModel.CanvasObjects)
        {
            if (obj.IsLocked) continue;
            var bb = obj.GetBoundingBox();
            if (Fits(bb.MinX, bb.MinY, bb.MaxX, bb.MaxY))
                results.Add(new HitResult(HitKind.CanvasObject, obj.Id));
        }

        if (mode == SelectMode.Crossing)
            results = ExpandCrossing(results, editModel);

        return results;
    }

    private static List<HitResult> ExpandCrossing(List<HitResult> initial, SchematicEditModel editModel)
    {
        var selected = new HashSet<string>(initial.Select(h => h.Id));
        var result   = new List<HitResult>(initial);
        const double tol = 8.0;

        foreach (var hit in initial.Where(h => h.Kind == HitKind.Component))
        {
            var comp = editModel.FindComponent(hit.Id);
            if (comp is null) continue;

            foreach (var def in editModel.PortDefsOf(comp))
            {
                if (comp.IsPortDetached(def.PortIndex)) continue;
                var (cpx, cpy) = editModel.PortWorldOf(comp, def);

                foreach (var wire in editModel.Wires)
                {
                    if (wire.Points.Count == 0) continue;
                    bool firstMatch = SchematicGeometry.CoincidentPoints(
                        cpx, cpy, wire.Points[0].X, wire.Points[0].Y, tol);
                    bool lastMatch  = SchematicGeometry.CoincidentPoints(
                        cpx, cpy, wire.Points[^1].X, wire.Points[^1].Y, tol);
                    if (!firstMatch && !lastMatch) continue;

                    if (selected.Add(wire.Id))
                        result.Add(new HitResult(HitKind.Wire, wire.Id));

                    var (otherX, otherY) = firstMatch
                        ? (wire.Points[^1].X, wire.Points[^1].Y)
                        : (wire.Points[0].X,  wire.Points[0].Y);

                    foreach (var other in editModel.Components)
                    {
                        if (selected.Contains(other.Id)) continue;
                        foreach (var oDef in editModel.PortDefsOf(other))
                        {
                            if (other.IsPortDetached(oDef.PortIndex)) continue;
                            var (opx, opy) = editModel.PortWorldOf(other, oDef);
                            if (!SchematicGeometry.CoincidentPoints(otherX, otherY, opx, opy, tol)) continue;
                            if (selected.Add(other.Id))
                                result.Add(new HitResult(HitKind.Component, other.Id));
                        }
                    }
                }
            }
        }

        // Whole-net wire expansion (crossing only): a crossing select touching ANY wire grabs every
        // wire on the same electrical node — shared vertices, T-junctions, dot crossings — so the
        // entire net's wire segments are selected, not just the wires the rect physically crossed.
        var wireSeeds = result.Where(h => h.Kind == HitKind.Wire).Select(h => h.Id).ToList();
        if (wireSeeds.Count > 0)
            foreach (var wid in NetExtractor.ConnectedWireIds(editModel, wireSeeds))
                if (selected.Add(wid))
                    result.Add(new HitResult(HitKind.Wire, wid));

        return result;
    }

    // ── Wire endpoint / port snap helpers ─────────────────────────────────────

    public static (bool Found, string WireId, int PointIdx, double X, double Y) NearestWireEndpoint(
        SchematicEditModel editModel, double worldX, double worldY, double tolerance = 15.0)
    {
        string bestWireId = "";
        int bestIdx = -1;
        double bestDist = double.MaxValue;
        double bestX = 0, bestY = 0;

        foreach (var wire in editModel.Wires)
        {
            if (wire.Points.Count == 0) continue;
            int[] endPoints = [0, wire.Points.Count - 1];
            foreach (int pi in endPoints)
            {
                var (px, py) = wire.Points[pi];
                double d = SchematicGeometry.DistanceSq(worldX, worldY, px, py);
                if (d < bestDist && d <= tolerance * tolerance)
                {
                    bestDist = d; bestWireId = wire.Id;
                    bestIdx = pi; bestX = px; bestY = py;
                }
            }
        }

        return (bestWireId != "", bestWireId, bestIdx, bestX, bestY);
    }

    public static (bool Found, string CompId, int PortIdx, double X, double Y) NearestPort(
        SchematicEditModel editModel, double worldX, double worldY, double tolerance = 15.0)
    {
        string bestId = "";
        int bestPort = -1;
        double bestDist = double.MaxValue;
        double bestX = 0, bestY = 0;

        foreach (var comp in editModel.Components)
        {
            var defs = editModel.PortDefsOf(comp);
            for (int slot = 0; slot < defs.Count; slot++)
            {
                var def = defs[slot];
                if (comp.IsPortDetached(def.PortIndex)) continue;
                var (px, py) = editModel.PortWorldOf(comp, def);
                double d = SchematicGeometry.DistanceSq(worldX, worldY, px, py);
                if (d < bestDist && d <= tolerance * tolerance)
                {
                    bestDist = d; bestId = comp.Id;
                    bestPort = slot; bestX = px; bestY = py;
                }
            }
        }

        return (bestId != "", bestId, bestPort, bestX, bestY);
    }

    /// <summary>
    /// Nearest perpendicular projection of (worldX,worldY) onto the <em>body</em> of any
    /// wire segment (the span between two vertices, not just its endpoints). This is the
    /// lowest-priority wire-draw snap target — below ports and wire endpoints — so a wire
    /// endpoint can land exactly on another wire's mid-segment to form a T-junction (§5.1).
    /// Returns the projected point on the segment; the caller grid-snaps as usual.
    /// </summary>
    public static (bool Found, string WireId, int SegIdx, double X, double Y) NearestPointOnWireSegment(
        SchematicEditModel editModel, double worldX, double worldY, double tolerance = 15.0)
    {
        string bestWireId = "";
        int bestSeg = -1;
        double bestDist = double.MaxValue;
        double bestX = 0, bestY = 0;

        foreach (var wire in editModel.Wires)
        {
            var pts = wire.Points;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var (ax, ay) = pts[i];
                var (bx, by) = pts[i + 1];
                double dx = bx - ax, dy = by - ay;
                double lenSq = dx * dx + dy * dy;
                if (lenSq < 1e-10) continue;
                double t = ((worldX - ax) * dx + (worldY - ay) * dy) / lenSq;
                t = Math.Clamp(t, 0.0, 1.0);
                double cx = ax + t * dx, cy = ay + t * dy;
                double d = SchematicGeometry.DistanceSq(worldX, worldY, cx, cy);
                if (d < bestDist && d <= tolerance * tolerance)
                {
                    bestDist = d; bestWireId = wire.Id; bestSeg = i;
                    bestX = cx; bestY = cy;
                }
            }
        }

        return (bestWireId != "", bestWireId, bestSeg, bestX, bestY);
    }

    /// <summary>
    /// Nearest 4-way wire crossing to (worldX,worldY): the proper interior intersection of two
    /// distinct wires' segments (neither ending there). Lets a junction dot snap exactly onto a
    /// crossing so it actually unions the wires (§5.1). Returns the intersection point, or
    /// Found=false if none within tolerance. O(k²) over the few segments near the click (the
    /// spatial index prunes the rest), so it stays cheap even on a 10k schematic.
    /// </summary>
    public static (bool Found, double X, double Y) NearestWireCrossing(
        SchematicEditModel editModel, SchematicSpatialIndex index,
        double worldX, double worldY, double tolerance = 15.0)
    {
        var candWires = new HashSet<int>();
        var candComps = new HashSet<int>();
        index.QueryViewport(worldX - tolerance, worldY - tolerance,
                            worldX + tolerance, worldY + tolerance, candComps, candWires);

        double bestDist = tolerance * tolerance;
        bool found = false; double bestX = 0, bestY = 0;

        var wireIdxs = candWires.Where(wi => wi < editModel.Wires.Count).ToList();
        for (int a = 0; a < wireIdxs.Count; a++)
        for (int b = a + 1; b < wireIdxs.Count; b++)
        {
            var wa = editModel.Wires[wireIdxs[a]].Points;
            var wb = editModel.Wires[wireIdxs[b]].Points;
            for (int i = 0; i < wa.Count - 1; i++)
            for (int j = 0; j < wb.Count - 1; j++)
            {
                if (!SchematicGeometry.SegmentsIntersectInterior(
                        wa[i].X, wa[i].Y, wa[i + 1].X, wa[i + 1].Y,
                        wb[j].X, wb[j].Y, wb[j + 1].X, wb[j + 1].Y,
                        out double ix, out double iy)) continue;
                // Reject an intersection where any nearby wire has a vertex — that point is a
                // T/merge, not a pure crossing. Keeps placement consistent with the connectivity
                // pass's crossing test (which defers vertex points to the T/merge paths).
                if (AnyWireVertexAt(editModel, wireIdxs, ix, iy)) continue;
                double d = SchematicGeometry.DistanceSq(worldX, worldY, ix, iy);
                if (d <= bestDist)
                {
                    bestDist = d; bestX = ix; bestY = iy; found = true;
                }
            }
        }

        return (found, bestX, bestY);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>True if any of the given candidate wires has a vertex coincident with (x,y).</summary>
    private static bool AnyWireVertexAt(
        SchematicEditModel editModel, List<int> wireIdxs, double x, double y)
    {
        foreach (int wi in wireIdxs)
            foreach (var (px, py) in editModel.Wires[wi].Points)
                if (SchematicGeometry.CoincidentPoints(x, y, px, py, SchematicEditModel.ConnectTolerance))
                    return true;
        return false;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) GetCompGlyphBb(
        EditableComponent comp, SchematicEditModel editModel)
    {
        if (comp.CellRef is not null)
        {
            var prims = editModel.EffectivePrimitivesOf(comp);
            if (prims is not null)
                return comp.ComputeGlyphBb(prims);
            // NotFound / PrimaryMissing — placeholder bounds (matches renderer)
            return (comp.X - 160, comp.Y - 60, comp.X + 160, comp.Y + 60);
        }
        return comp.ComputeGlyphBb();
    }
}

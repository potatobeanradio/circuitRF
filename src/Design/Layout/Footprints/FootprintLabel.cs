// The reference designator a PLACEMENT draws on silkscreen — brief-footprint-4b-designators.md.
//
// ONE FUNCTION FOR THE DEFAULT POSITION, AND ONE FOR THE WHOLE SHAPE (R-fp4b-2c). The renderer, the
// hit-test, the whole-design flatten, the GDSII and DXF exports and the Reset-to-auto command all
// need the same answer, and three of them agreeing by coincidence is not the same as one of them
// being right. Nothing below is a literal at a call site.
//
// THE TEXT IS DERIVED; ONLY ITS PLACEMENT IS STORED. LayoutInstance.DisplayRefDes is the single
// accessor — an instance the schematic owns takes its designator from SchematicId and stores none of
// its own, so a rename arrives through Update Layout with no migration and no way for a board to
// disagree with its drawing about what a part is called.
//
// It draws nothing. It is artwork, not chrome (R-fp4b-4a): what comes out of here is a LabelShape on
// the technology's SILKSCREEN role, resolved through LandPatternLayers exactly as a land pattern's
// own body outline is, so what is on the screen and what is in the Gerber cannot become two
// different statements about the same board.

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>
/// Where one placement's reference designator goes, and what it is — the derived half of
/// <see cref="LayoutInstance"/>'s designator fields.
/// </summary>
public static class FootprintLabel
{
    /// <summary>
    /// The default cap height, board-wide and FIXED (R-fp4b-2d). Deliberately not a fraction of the
    /// part: a board carrying 0402s and a 7343-31 would otherwise print designators an order of
    /// magnitude apart in size and the smaller ones would be illegible, which defeats the whole
    /// purpose. Every tool that ships a default ships a fixed one.
    /// </summary>
    public const decimal DefaultHeightMm = 0.8m;

    /// <summary>Clearance between the resolved body extent and the bottom of the designator — the
    /// same <c>SilkClearanceMm</c> <see cref="ChipLandPatternGenerator"/> keeps between a silk line
    /// and a mask opening, for the same reason: text printed over solder is the classic silkscreen
    /// defect.</summary>
    internal const decimal AutoClearanceMm = 0.10m;

    /// <summary><see cref="DefaultHeightMm"/> in a view's own DBU.</summary>
    public static long DefaultHeightDbu(int dbuPerMicron)
        => Math.Max(1, LayoutUnits.ToDbu(DefaultHeightMm, LayoutUnit.Mm, Math.Max(1, dbuPerMicron)));

    /// <summary>
    /// <b>The auto position — recomputed, never frozen</b> (R-fp4b-2a). Centred above the resolved
    /// cell's own body, clear of it by <see cref="AutoClearanceMm"/>, in the CELL's own DBU and
    /// relative to the cell's origin.
    ///
    /// <para>The "body" is the cell's SILKSCREEN and COURTYARD artwork when the technology declares
    /// either role and the cell draws on it — that is the outline a reader sees — falling back to the
    /// cell's whole extent when it does not, which is the right answer for any cell that is not a
    /// land pattern. An empty cell puts the designator one clearance above its origin rather than on
    /// top of it.</para>
    ///
    /// <para>Writing this into the file at placement time would freeze it, so re-pointing a part from
    /// 0402 to 0805 would leave its designator sitting inside the bigger body — correct when written,
    /// wrong afterwards, and wrong silently. Which is why <see cref="LayoutInstance.LabelDx"/> stores
    /// null for "auto" rather than a number.</para>
    /// </summary>
    public static (long Dx, long Dy) AutoOffset(LayoutView? cellView, LandPatternRoles? roles, long heightDbu)
    {
        int dbuPerMicron = cellView?.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron;
        long clear = LandPattern.Mm(AutoClearanceMm, dbuPerMicron);

        var body = BodyExtent(cellView, roles);
        if (body.IsEmpty) return (0, clear + heightDbu / 2);

        long cx = (body.MinX + body.MaxX) / 2;
        return (cx, body.MaxY + clear + heightDbu / 2);
    }

    /// <summary>
    /// Memoized per cell view, because <see cref="AutoOffset"/> is asked once per PLACEMENT and the
    /// renderer asks it every frame — a board with 400 placements of a dozen cells would otherwise
    /// walk every one of those cells' shape lists 400 times a frame.
    ///
    /// <para>Keyed on the <see cref="LayoutView"/> INSTANCE, which is the trick
    /// <c>LayoutRenderer</c>'s own compile cache uses and for the same reason: a file change produces
    /// a new view object from the resolver, so this is simply a miss and the stale entry becomes
    /// unreachable. There is nothing to invalidate and no way to show a stale answer.</para>
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LayoutView, Dictionary<(int, int), Bbox>>
        _bodyExtents = new();

    /// <summary>The silkscreen/courtyard extent of <paramref name="cellView"/>, or its whole extent
    /// when neither role is declared or nothing is drawn on them.</summary>
    private static Bbox BodyExtent(LayoutView? cellView, LandPatternRoles? roles)
    {
        if (cellView is null || cellView.Shapes.Count == 0) return Bbox.Empty;

        // The key is the two ROLES the answer depends on, not the whole technology: a cell measured
        // under one technology's silk key and another's is genuinely two different measurements, and
        // a key that ignored that would hand the second one the first one's answer.
        var key = (roles?.Silkscreen is { } s ? s.GetHashCode() : 0,
                   roles?.Assembly   is { } a ? a.GetHashCode() : 0);
        var cache = _bodyExtents.GetValue(cellView, static _ => []);
        lock (cache) { if (cache.TryGetValue(key, out var hit)) return hit; }
        var computed = ComputeBodyExtent(cellView, roles);
        lock (cache) { cache[key] = computed; }
        return computed;
    }

    private static Bbox ComputeBodyExtent(LayoutView cellView, LandPatternRoles? roles)
    {

        var outline = Bbox.Empty;
        var all = Bbox.Empty;
        foreach (var shape in cellView.Shapes)
        {
            if (shape is LabelShape or BitmapShape) continue;
            var bb = LayoutGeometry.BboxOf(shape);
            if (bb.IsEmpty) continue;
            all = all.Union(bb);
            if (roles is not null &&
                ((roles.Silkscreen is { } silk && shape.Layer.Equals(silk)) ||
                 (roles.Assembly is { } asm && shape.Layer.Equals(asm))))
                outline = outline.Union(bb);
        }

        return outline.IsEmpty ? all : outline;
    }

    /// <summary>
    /// <b>Readable, always</b> (R-fp4b-3a): <paramref name="degrees"/> folded into <c>(-90, 90]</c>.
    /// Rotate a resistor 180 degrees and its designator stays the right way up — universal across
    /// every tool that draws one, and the behaviour a user assumes without being told.
    /// </summary>
    public static double ReadableAngle(double degrees)
    {
        double n = LayoutAngle.Normalize(degrees);   // [0, 360)
        if (n > 90.0 && n <= 270.0) return n - 180.0;
        return n > 270.0 ? n - 360.0 : n;
    }

    /// <summary>One placement's designator, resolved: what to draw, where, at what angle and size,
    /// all in the PARENT view's own DBU frame.</summary>
    public readonly record struct Placement(string Text, long X, long Y, double AngleDeg, long HeightDbu);

    /// <summary>
    /// <paramref name="inst"/>'s designator in <paramref name="parentDbuPerMicron"/> DBU, or null
    /// when there is nothing to draw — no designator, or the placement's own
    /// <see cref="LayoutInstance.ShowRefDes"/> turned off.
    ///
    /// <para><paramref name="cellView"/> is the RESOLVED sub-cell and is needed only for the auto
    /// position; a stored offset needs nothing, and a broken reference therefore still draws its
    /// designator where the user put it.</para>
    ///
    /// <para><b>A mirrored placement draws its designator UN-MIRRORED, at the mirrored anchor</b>
    /// (R-fp4b-3b). No tool draws mirror-reversed designator text, because the string is there to be
    /// read. Nothing here knows about board SIDE — circuitRF has no such notion (R-fp4b-3c) — and
    /// this is the right answer on either one.</para>
    /// </summary>
    public static Placement? PlacementFor(
        LayoutInstance inst, LayoutView? cellView, LandPatternRoles? roles, int parentDbuPerMicron)
    {
        ArgumentNullException.ThrowIfNull(inst);
        if (!inst.DesignatorShown) return null;
        if (inst.DisplayRefDes is not { Length: > 0 } text) return null;

        parentDbuPerMicron = Math.Max(1, parentDbuPerMicron);
        long height = inst.LabelHeight is { } h and > 0 ? h : DefaultHeightDbu(parentDbuPerMicron);

        long x, y;
        if (inst.LabelDx is { } dx && inst.LabelDy is { } dy)
        {
            // R-fp4b-2b: a STORED offset is already in the parent's DBU — it is the delta the drag
            // happened in — so only the placement's rotation and mirror apply to it, never its
            // magnification. Expressed as a unit-magnification placement rather than as a second copy
            // of the rotation signs, so it cannot drift from LayoutInstanceTransform's own.
            (x, y) = LayoutInstanceTransform.TransformPoint(dx, dy, UnitMag(inst), 0, 0);
        }
        else
        {
            // AUTO. The offset comes back in the CELL's DBU, so the full placement transform applies
            // — magnification included, since a 2x placement really is twice as far from its origin.
            int cellDbu = cellView?.DbuPerMicron ?? parentDbuPerMicron;
            long heightInCell = (long)Math.Round(height * (double)cellDbu / parentDbuPerMicron
                                                 / Math.Max(Math.Abs(inst.Mag), 1e-9));
            var (adx, ady) = AutoOffset(cellView, roles, heightInCell);
            (x, y) = LayoutInstanceTransform.TransformPoint(adx, ady, inst, 0, 0);
            if (cellDbu != parentDbuPerMicron)
            {
                double k = (double)parentDbuPerMicron / cellDbu;
                x = inst.X + (long)Math.Round((x - inst.X) * k);
                y = inst.Y + (long)Math.Round((y - inst.Y) * k);
            }
        }

        double angle = inst.LabelRotDeg ?? (inst.MirrorX ? -inst.RotationDegrees : inst.RotationDegrees);
        return new Placement(text, x, y, ReadableAngle(angle), height);
    }

    private static LayoutInstance UnitMag(LayoutInstance inst) => new()
    {
        X = inst.X, Y = inst.Y, RotationDegrees = inst.RotationDegrees, MirrorX = inst.MirrorX, Mag = 1.0,
    };

    /// <summary>
    /// The designator of one placement as ARTWORK — a <see cref="LabelShape"/> on
    /// <paramref name="silk"/>, centred on its anchor. Null when there is nothing to draw, and null
    /// when <paramref name="silk"/> is null: <b>a technology with no silkscreen role draws no
    /// designator and the text is NOT relocated</b> to soldermask or the board outline (R-fp4b-4c,
    /// R-fp1-3a's rule unchanged and for the same reason).
    /// </summary>
    public static LabelShape? ShapeFor(
        LayoutInstance inst, LayoutView? cellView, LandPatternRoles? roles,
        int parentDbuPerMicron, LayerKey? silk)
    {
        if (silk is not { } layer) return null;
        if (PlacementFor(inst, cellView, roles, parentDbuPerMicron) is not { } p) return null;
        return new LabelShape
        {
            Layer = layer,
            X = p.X, Y = p.Y,
            Text = p.Text,
            Height = p.HeightDbu,
            RotationDegrees = p.AngleDeg,
            HAlign = LabelHAlign.Center,
            VAlign = LabelVAlign.Middle,
        };
    }

    /// <summary>
    /// <b>A designator that duplicates another in the same view is REPORTED, never renumbered</b>
    /// (R-fp4b-8d). Two parts called C3 is a real condition a user needs told about; silently
    /// renaming one of them is how a board stops matching its BOM. Nothing here invents, renumbers or
    /// reconciles anything — the schematic is the whole source.
    /// </summary>
    public static IReadOnlyList<string> DuplicateReports(LayoutView? view)
    {
        if (view is null || view.Instances.Count == 0) return [];

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var inst in view.Instances)
            if (inst.DisplayRefDes is { Length: > 0 } d)
                counts[d] = counts.TryGetValue(d, out var n) ? n + 1 : 1;

        List<string>? reports = null;
        foreach (var (name, n) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (n > 1)
                (reports ??= []).Add(
                    $"designator '{name}' is used by {n} placements in this layout. Both are drawn as authored — " +
                    "nothing was renumbered. Two parts with one designator is a board that no longer matches its BOM.");

        return (IReadOnlyList<string>?)reports ?? [];
    }

    // ── Seeding a HAND-placed instance's designator (R-fp4b-8c) ─────────────────────────────────

    /// <summary>
    /// The reference-designator PREFIX an imported part declares — <c>C</c>, <c>R</c>, <c>U</c> —
    /// read from the cell folder's own <c>.ccell</c>, where <c>ComponentImport</c> writes every
    /// part-library metadata field as a quoted string parameter. <c>ComponentPlxReader</c> has read
    /// <c>refDesPrefix</c> into that field since it was written, and until brief-footprint-4b nothing
    /// used it.
    ///
    /// <para>Null when the cell declares none, and that is not an omission to be filled in: a part
    /// with no prefix gets NO designator, never an invented one (R-fp3-6c's principle — an instance
    /// corresponding to no schematic component must not be given a fabricated identity).</para>
    /// </summary>
    public static string? PrefixOfCell(string? resolvedCellDir)
    {
        if (string.IsNullOrEmpty(resolvedCellDir)) return null;
        string path = Path.Combine(resolvedCellDir, Cells.CellFolder.CcellFileName);
        if (!File.Exists(path)) return null;
        try
        {
            foreach (var p in Cells.CellPersistence.LoadFromFile(path).Parameters)
            {
                if (!string.Equals(p.Name, "Reference", StringComparison.OrdinalIgnoreCase)) continue;
                string v = (p.DefaultExpression ?? "").Trim().Trim('"', '\'');
                return v.Length > 0 ? v : null;
            }
        }
        catch { /* an unreadable .ccell simply declares no prefix — never a failed placement */ }
        return null;
    }

    /// <summary>
    /// <paramref name="prefix"/> plus <b>the lowest free number among <paramref name="view"/>'s own
    /// instances</b> — <c>C</c> gives <c>C1</c>, then <c>C2</c> (R-fp4b-8c). Null prefix gives null:
    /// nothing here invents an identity.
    ///
    /// <para>Lowest FREE rather than highest-plus-one, so deleting C2 and placing another capacitor
    /// reuses C2 instead of leaving a permanent gap. Nothing renumbers anything that already exists
    /// (R-fp4b-8d) — this only chooses a name for the part being placed right now.</para>
    /// </summary>
    public static string? SeedDesignator(LayoutView? view, string? prefix)
    {
        if (prefix is not { Length: > 0 } p) return null;

        var taken = new HashSet<int>();
        if (view is not null)
            foreach (var inst in view.Instances)
                if (inst.DisplayRefDes is { Length: > 0 } d &&
                    d.StartsWith(p, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(d.AsSpan(p.Length), out int n))
                    taken.Add(n);

        int next = 1;
        while (taken.Contains(next)) next++;
        return p + next.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Every designator <paramref name="view"/>'s OWN instances draw, as artwork on
    /// <paramref name="technology"/>'s silkscreen role.
    ///
    /// <para><b>The parent emits them, because they are the parent's data</b> (R-fp4b-5). That is
    /// also what makes the instance compile in the renderer stay geometry-only and what lets one
    /// function serve the whole-design flatten (Gerber, DRC, <c>check</c>) and the two hierarchical
    /// exports (GDSII, DXF) at once. A sub-cell's own nested placements are a sub-cell's business and
    /// are emitted when THAT view is the one being asked — which for GDSII and DXF is every cell in
    /// the design, and for the flat consumers is the root, the only level a board places parts at.</para>
    /// </summary>
    public static IReadOnlyList<LabelShape> ShapesFor(
        LayoutView? view, string viewLayoutDir, Technology? technology,
        Func<LayoutInstance, LayoutView?>? resolveCell)
    {
        if (view is null || view.Instances.Count == 0) return [];
        if (!view.Instances.Any(i => i.DesignatorShown && i.DisplayRefDes is { Length: > 0 })) return [];

        var roles = LandPatternLayers.Resolve(technology, PCells.PCellLayerSelection.Default, []);
        if (roles.Silkscreen is not { } silk) return [];

        resolveCell ??= inst => CellLayoutResolver.Resolve(inst.CellRef, viewLayoutDir).View;

        var shapes = new List<LabelShape>();
        foreach (var inst in view.Instances)
        {
            if (!inst.DesignatorShown || inst.DisplayRefDes is not { Length: > 0 }) continue;
            // A stored offset needs no resolution at all; only the auto position reads the cell.
            var cellView = inst.LabelDx is not null && inst.LabelDy is not null ? null : resolveCell(inst);
            if (ShapeFor(inst, cellView, roles, view.DbuPerMicron, silk) is { } s) shapes.Add(s);
        }
        return shapes;
    }
}

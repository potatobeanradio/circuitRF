// "What Z0 is this trace?" — a REVIEW tool, not a design tool (round-7 field report).
//
// A reviewer looking at an imported board wants the characteristic impedance of a trace in well under
// a second, from the stackup and the copper as drawn, and wants to be told when the return path under
// it is broken. Gerber copper arrives as unioned POLYGONS, so there is no drawn width to read: the
// width is MEASURED from the geometry at the point clicked.
//
// ── THE METHOD ─────────────────────────────────────────────────────────────────────────────────
//
// 1. DIRECTION AND WIDTH. A straight strip's chord through a point at angle φ to its axis is W/sin φ,
//    so the MINIMUM chord over all angles is the width and its direction is the cut direction. The
//    minimum is found on a 1° sweep and refined by golden section. It is then CROSS-CHECKED against
//    the edges: the two copper edges the chord ends on, and the copper edge nearest the point, must all
//    be parallel to the axis the chord implies. A minimum chord that ends on a non-parallel edge is a
//    bend, a mitre, a corner or a junction, and there is no single width to report.
// 2. UNIFORMITY. The width is re-measured along the same cut direction at ±W and ±2W along the axis,
//    and the chord midpoint must stay on the axis. Copper that ends within 2W is a pad or a stub;
//    a width that changes steadily is a taper; one that jumps is a junction, a step or the section's
//    end. Every one of those is a REFUSAL with its reason — a review tool must never print a
//    confident Z0 for a shape that has none.
// 3. THE CROSS-SECTION. A perpendicular cut through the section's centre line gives, per conductor
//    layer, the intervals of copper. The REFERENCE below is the nearest conductor layer whose copper
//    covers the whole trace width at the cut (above likewise, for stripline) — decided by the COPPER,
//    never by the stackup's IsGroundReference flag, because a Gerber board's ground can be on any
//    layer. A nearer layer with no or partial copper is a warning (a cutout under the trace). The
//    lower reference is the kernel's exact-image ground plane; every other interval within the window
//    (coplanar ground either side, partial copper between, the upper reference) is a finite conductor.
// 4. THE SOLVE is the quasi-static cross-section kernel's own boundary-element charge solve
//    (BoundaryMesher + ChargeSolver), and the stack and its dielectric regions are built by
//    CrossSectionExtractor's own BuildStack/BuildRegions — one implementation, not a second. With the
//    signal at 1 V and every other conductor at 0 V, the Maxwell matrix's diagonal C₁₁ IS the line's
//    capacitance against everything around it held at ground; the same solve air-filled gives C₀, and
//    Z0 = 1/(c·√(C·C₀)), ε_eff = C/C₀. This is why no port or reference-conductor bookkeeping is
//    needed: every other conductor is part of the return by construction.
//
// Every conductor but the signal is held at ground. On a Gerber board that is the only reading
// available (no nets), and it is what a reviewer means by "coplanar ground" — but a neighbouring
// SIGNAL trace in the window is treated as ground too, and the result says how many were.

using System.Globalization;
using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>What <see cref="TraceImpedanceProbe.Probe"/> found. Lengths in metres.</summary>
public sealed record TraceImpedanceResult
{
    /// <summary>Why no impedance is reported, or null when one is.</summary>
    public string? Refusal { get; init; }

    public bool Ok => Refusal is null;

    public double Z0Ohms { get; init; }
    public double Eeff { get; init; }
    public double WidthM { get; init; }

    /// <summary>Same-layer gap to the nearest copper on each side of the cut, or null where there is
    /// none within the window. "Left" is the negative cut direction.</summary>
    public double? GapLeftM { get; init; }
    public double? GapRightM { get; init; }

    /// <summary>What each gap ends on — <see cref="GapToViaPad"/> or <see cref="GapToConductor"/> —
    /// or null where that side is open. Said on the line because a via fence's lands read as a
    /// coplanar edge in the cut, which is worth knowing (owner, 2026-09-24: it surfaced vias the
    /// reviewer had not seen).</summary>
    public string? GapLeftTo { get; init; }
    public string? GapRightTo { get; init; }

    public const string GapToViaPad = "via pad";
    public const string GapToConductor = "conductor";

    /// <summary>Signal bottom to the lower reference's top, or null with no lower reference.</summary>
    public double? HeightBelowM { get; init; }

    /// <summary>Signal top to the upper reference's bottom, or null with no upper reference.</summary>
    public double? HeightAboveM { get; init; }

    public string SignalLayer { get; init; } = "";
    public string? ReferenceBelow { get; init; }
    public string? ReferenceAbove { get; init; }

    /// <summary>"microstrip", "grounded coplanar waveguide", "stripline", "coplanar waveguide (no ground plane)"…</summary>
    public string Configuration { get; init; } = "";

    /// <summary>The trace axis, degrees counter-clockwise from +x, in [0, 180).</summary>
    public double AxisAngleDeg { get; init; }

    /// <summary>The length of the constant-width section the click is on, as far as the walk went.</summary>
    public double SectionLengthM { get; init; }

    /// <summary>Where the cut was taken — the click moved onto the trace's centre line. DBU.</summary>
    public long CentreX { get; init; }
    public long CentreY { get; init; }

    /// <summary>How many conductors besides the signal were held at ground in the solve.</summary>
    public int GroundedConductors { get; init; }

    /// <summary>Things a reviewer must act on — a missing or broken reference.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>One short tag per warning, for the one-line <see cref="Summary"/> — the full sentence
    /// stays in <see cref="Warnings"/>.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>How the answer was reached.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The unit and resolution the sentences read in — carried so <see cref="Summary"/>
    /// speaks the layout's own unit.</summary>
    public LayoutUnit DisplayUnit { get; init; } = LayoutUnit.Um;
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>
    /// The ONE line a reviewer reads (owner, 2026-09-24: three lines were hard to read): Z0, ε_eff, the configuration, the dimensions in one unit, and a short tag per
    /// warning. The full warning and note sentences stay on the result.
    /// </summary>
    public string Summary()
    {
        if (!Ok) return $"Trace impedance: {Refusal}";

        string N(double m) => LayoutUnits.Format((long)Math.Round(m * 1e6 * DbuPerMicron), DisplayUnit, DbuPerMicron, 1);
        string unit = LayoutUnits.Suffix(DisplayUnit);
        string at = $"({LayoutUnits.Format(CentreX, DisplayUnit, DbuPerMicron, 1)}, " +
                    $"{LayoutUnits.Format(CentreY, DisplayUnit, DbuPerMicron, 1)})";
        string config = Configuration.Replace("grounded coplanar waveguide", "GCPW")
                                     .Replace("coplanar waveguide", "CPW");

        var dims = new List<string> { $"W {N(WidthM)}" };
        if (GapLeftM is not null || GapRightM is not null)
            dims.Add($"G {(GapLeftM is { } l ? N(l) : "open")}/{(GapRightM is { } r ? N(r) : "open")}");
        var refs = new List<string>();
        if (GapLeftTo is not null || GapRightTo is not null)
        {
            static string Plural(string k) => k + "s";
            refs.Add(GapLeftTo == GapRightTo || GapLeftTo is null || GapRightTo is null
                ? $"G to {Plural((GapLeftTo ?? GapRightTo)!)}"
                : $"G to {GapLeftTo}/{GapRightTo}");
        }
        if (HeightBelowM is { } hb) { dims.Add($"H {N(hb)}"); refs.Add($"ref {ReferenceBelow}"); }
        if (HeightAboveM is { } ha) { dims.Add($"H↑ {N(ha)}"); refs.Add($"ref↑ {ReferenceAbove}"); }

        string numbers = FormattableString.Invariant(
            $"Z0 {Z0Ohms:0.0} Ω, εeff {Eeff:0.00}");
        string flags = Flags.Count > 0 ? " ⚠ " + string.Join("; ", Flags) : "";
        string refText = refs.Count > 0 ? $" ({string.Join(", ", refs)})" : "";
        return $"Trace Z0: {numbers} — {config}, {string.Join(", ", dims)} {unit}{refText} — {SignalLayer} {at}{flags}";
    }

    internal static TraceImpedanceResult Refused(string why) => new() { Refusal = why };
}

public static class TraceImpedanceProbe
{
    /// <summary>How far around the click copper is gathered — ample for a board, and what bounds
    /// the cost on one: nothing outside it is flattened or unioned.</summary>
    public const double WindowHalfMicrons = 15_000;

    /// <summary>The angle two edges may differ by and still be called parallel.</summary>
    public const double ParallelToleranceDeg = 2.0;

    /// <summary>A layer covers the trace when its copper spans this fraction of the width at the cut.</summary>
    public const double FullCoverage = 0.995;

    /// <summary>
    /// The impedance of the trace at (<paramref name="x"/>, <paramref name="y"/>) on
    /// <paramref name="layer"/>, or a refusal naming why there is none.
    /// </summary>
    /// <param name="shapes">The FLATTENED artwork (instances expanded), DBU.</param>
    /// <param name="tech">Supplies the stackup.</param>
    /// <param name="dbuPerMicron">The layout's own resolution.</param>
    /// <param name="displayUnit">How lengths in the sentences read.</param>
    public static TraceImpedanceResult Probe(
        IReadOnlyList<LayoutShape> shapes, Technology tech, int dbuPerMicron,
        long x, long y, LayerKey layer, LayoutUnit displayUnit = LayoutUnit.Um,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(tech);
        if (dbuPerMicron <= 0) dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;

        string Len(double dbu) =>
            $"{LayoutUnits.Format((long)Math.Round(dbu), displayUnit, dbuPerMicron, 1)} {LayoutUnits.Suffix(displayUnit)}";
        string Pt(double px, double py) =>
            $"({LayoutUnits.Format((long)Math.Round(px), displayUnit, dbuPerMicron, 1)}, " +
            $"{LayoutUnits.Format((long)Math.Round(py), displayUnit, dbuPerMicron, 1)}) {LayoutUnits.Suffix(displayUnit)}";

        // ── the stack — CrossSectionExtractor's own ─────────────────────────────────────────────
        var stack = CrossSectionExtractor.BuildStack(tech.Stackup);
        var bands = stack.Where(b => b.Layer.Kind == StackupKind.Conductor).ToList();
        var bandOf = new Dictionary<LayerKey, CrossSectionExtractor.Band>();
        foreach (var b in bands)
            foreach (var dl in b.Layer.DrawingLayers)
                bandOf.TryAdd(dl, b);

        string layerName = tech.Layers.FirstOrDefault(l => l.Key == layer)?.Name is { Length: > 0 } n
            ? n : $"layer {layer.Layer}/{layer.Datatype}";

        if (!bandOf.TryGetValue(layer, out var signal))
            return TraceImpedanceResult.Refused(
                $"'{layerName}' is not bound to a conductor of the stackup, so it has no height and no " +
                "substrate to compute an impedance from. Bind it to a conductor on the technology's " +
                "Stackup tab.");

        foreach (var b in stack)
        {
            if (b.Layer.ThicknessDbu <= 0)
                return TraceImpedanceResult.Refused(
                    $"Stackup layer '{b.Layer.Name}' has zero thickness, so every height above it would be " +
                    "wrong. Set its thickness on the technology's Stackup tab.");
            if (b.Layer.Kind == StackupKind.Dielectric && !(b.Layer.Epsr >= 1))
                return TraceImpedanceResult.Refused(
                    $"Stackup layer '{b.Layer.Name}' has εr = {b.Layer.Epsr.ToString("G4", CultureInfo.InvariantCulture)}; " +
                    "relative permittivity is ≥ 1. Set it on the technology's Stackup tab.");
        }

        // ── the copper, per conductor band, inside the window ──────────────────────────────────
        long half = (long)(WindowHalfMicrons * dbuPerMicron);
        var window = new Bbox(x - half, y - half, x + half, y + half);
        Paths64 windowRect = [[new Point64(x - half, y - half), new Point64(x + half, y - half),
                               new Point64(x + half, y + half), new Point64(x - half, y + half)]];

        var subjects = new Dictionary<int, Paths64>();
        Paths64 signalViaPads = [], signalOther = [];   // which a coplanar gap ends on
        foreach (var shape in shapes)
        {
            if (shape is not ViaShape && !bandOf.ContainsKey(shape.Layer)) continue;
            if (!LayoutGeometry.BboxOf(shape).Intersects(window)) continue;
            DrcRegions.Expand(shape, tech, _ => long.MaxValue, (lk, _, paths) =>
            {
                if (!bandOf.TryGetValue(lk, out var band)) return;
                if (!subjects.TryGetValue(band.Index, out var list)) subjects[band.Index] = list = [];
                list.AddRange(paths);
                if (band.Index == signal.Index) (shape is ViaShape ? signalViaPads : signalOther).AddRange(paths);
            });
        }
        ct.ThrowIfCancellationRequested();

        var copper = new Dictionary<int, Copper>();
        foreach (var (index, list) in subjects)
            copper[index] = new Copper(Clipper.Intersect(list, windowRect, LayoutClipper.Rule));

        if (!copper.TryGetValue(signal.Index, out var sig) || !Regions.Contains(sig.Paths, x, y))
            return TraceImpedanceResult.Refused(
                $"There is no copper on '{layerName}' at {Pt(x, y)}. Click on the trace, on the layer it " +
                "is drawn on.");

        // ── 1. direction and width: the minimum chord ───────────────────────────────────────────
        double Chord(double theta) =>
            sig.ChordAt(x, y, Math.Cos(theta), Math.Sin(theta)) is { } c ? c.T1 - c.T0 : double.PositiveInfinity;

        double best = 0, bestLen = double.PositiveInfinity;
        for (int i = 0; i < 180; i++)
        {
            double th = i * Math.PI / 180;
            double len = Chord(th);
            if (len < bestLen) { bestLen = len; best = th; }
        }
        if (double.IsInfinity(bestLen))
            return TraceImpedanceResult.Refused($"The copper at {Pt(x, y)} could not be measured.");

        {
            // Golden section on ±1° around the sweep's minimum.
            double lo = best - Math.PI / 180, hi = best + Math.PI / 180;
            const double G = 0.6180339887498949;
            double a = hi - G * (hi - lo), b = lo + G * (hi - lo);
            double fa = Chord(a), fb = Chord(b);
            for (int k = 0; k < 40; k++)
            {
                if (fa < fb) { hi = b; b = a; fb = fa; a = hi - G * (hi - lo); fa = Chord(a); }
                else         { lo = a; a = b; fa = fb; b = lo + G * (hi - lo); fb = Chord(b); }
            }
            double mid = 0.5 * (lo + hi);
            if (Chord(mid) < bestLen) { best = mid; bestLen = Chord(mid); }
        }

        double ux = Math.Cos(best), uy = Math.Sin(best);   // across the trace
        double vx = -uy, vy = ux;                           // along it
        var chord0 = sig.ChordAt(x, y, ux, uy)!.Value;
        double w = chord0.T1 - chord0.T0;
        double cx = x + 0.5 * (chord0.T0 + chord0.T1) * ux;
        double cy = y + 0.5 * (chord0.T0 + chord0.T1) * uy;
        double axisDeg = (Math.Atan2(vy, vx) * 180 / Math.PI + 360) % 180;
        double tol = Math.Max(0.02 * w, 1.0 * dbuPerMicron);
        double sinTol = Math.Sin(ParallelToleranceDeg * Math.PI / 180);

        // Cross-check: the chord's two end edges, and the edge nearest the point, lie along the axis.
        var nearest = sig.NearestEdge(x, y);
        foreach (var e in new[] { chord0.E0, chord0.E1, nearest })
        {
            if (Math.Abs(sig.SinTo(e, vx, vy)) <= sinTol) continue;
            return TraceImpedanceResult.Refused(
                $"The copper at {Pt(x, y)} is not a straight run: the edges beside the point are not " +
                $"parallel to one axis (the narrowest cut is {Len(w)} at {axisDeg:0.#}°, but an edge there " +
                $"runs at {sig.AngleDeg(e):0.#}°). It is a bend, a mitre, a corner or a junction, where a " +
                "trace has no single width. Click on a straight, constant-width part of the trace.");
        }

        // ── 2. uniformity ±2W ───────────────────────────────────────────────────────────────────
        var widths = new SortedDictionary<int, double> { [0] = w };
        foreach (int k in new[] { -2, -1, 1, 2 })
        {
            double qx = cx + k * w * vx, qy = cy + k * w * vy;
            if (sig.ChordAt(qx, qy, ux, uy) is not { } side)
                return TraceImpedanceResult.Refused(
                    $"The copper at {Pt(x, y)} is not a line: it ends within {Math.Abs(k)} × its width " +
                    $"({Len(w)}) along its own axis. It is a pad, a via land or a short stub, and a trace " +
                    "impedance is not defined for it. Click on a trace.");

            double drift = 0.5 * (side.T0 + side.T1);
            if (Math.Abs(drift) > tol)
                return TraceImpedanceResult.Refused(
                    $"The trace at {Pt(x, y)} is not straight: its centre line moves by {Len(Math.Abs(drift))} " +
                    $"within {Math.Abs(k)} × its width along the axis. It is a bend or a curve. Click on a " +
                    "straight part of the trace.");
            widths[k] = side.T1 - side.T0;
        }

        if (widths.Values.Any(v => Math.Abs(v - w) > tol))
        {
            var seq = widths.Values.ToArray();   // −2W … +2W
            bool rising  = seq.Zip(seq.Skip(1)).All(p => p.Second > p.First);
            bool falling = seq.Zip(seq.Skip(1)).All(p => p.Second < p.First);
            return TraceImpedanceResult.Refused(rising || falling
                ? $"The trace at {Pt(x, y)} is a taper: its width runs from {Len(seq[0])} to {Len(seq[^1])} " +
                  $"over 4 × its width along the axis, so it has no single impedance. Click on a " +
                  "constant-width part."
                : $"The trace at {Pt(x, y)} is {Len(w)} wide, but {Len(widths.First(kv => Math.Abs(kv.Value - w) > tol).Value)} " +
                  "within 2 × that along its axis: a junction, a width step or the end of the section is " +
                  "next to the point. Click further into the constant-width run.");
        }

        ct.ThrowIfCancellationRequested();

        // ── 3. the cut: coverage above and below ────────────────────────────────────────────────
        // Re-measured through the centre, so the signal is [−W/2, W/2] on the cut.
        var centre = sig.ChordAt(cx, cy, ux, uy)!.Value;
        double sa = centre.T0, sb = centre.T1;
        w = sb - sa;

        var below = bands.Where(b => b.TopM <= signal.BottomM).OrderByDescending(b => b.TopM).ToList();
        var above = bands.Where(b => b.BottomM >= signal.TopM).OrderBy(b => b.BottomM).ToList();

        double Coverage(CrossSectionExtractor.Band band, double px, double py, double a, double b) =>
            copper.TryGetValue(band.Index, out var cu) ? cu.Covered(px, py, ux, uy, a, b) / (b - a) : 0;

        var warnings = new List<string>();
        var flags = new List<string>();
        var notes = new List<string>();

        // The nearest layer on each side that covers the whole width at the cut, and the nearer ones
        // passed over on the way with their coverage — what those mean (a deliberate clearance, a
        // gap in the return, copper half under the trace) is decided after the section walk below.
        (CrossSectionExtractor.Band? Ref, List<(CrossSectionExtractor.Band Band, double Cov)> Skipped)
            FindReference(List<CrossSectionExtractor.Band> side)
        {
            var skipped = new List<(CrossSectionExtractor.Band, double)>();
            foreach (var band in side)
            {
                double cov = Coverage(band, cx, cy, sa, sb);
                if (cov >= FullCoverage) return (band, skipped);
                skipped.Add((band, cov));
            }
            return (null, skipped);
        }

        var (lowerRef, skippedBelow) = FindReference(below);
        var (upperRef, skippedAbove) = FindReference(above);

        double metresPerDbu = 1.0 / (dbuPerMicron * 1e6);
        double? hBelow = lowerRef is null ? null : signal.BottomM - lowerRef.TopM;
        double? hAbove = upperRef is null ? null : upperRef.BottomM - signal.TopM;

        if (lowerRef is null && below.Count > 0)
        {
            warnings.Add(
                $"No ground under the trace: no layer below it covers its width at {Pt(cx, cy)} " +
                $"({string.Join(", ", below.Select(b => $"'{b.Layer.Name}'"))}).");
            flags.Add("no ground under the trace");
        }

        bool plane = lowerRef is not null;
        double groundM = lowerRef?.TopM ?? stack[0].BottomM;
        if (lowerRef is null && tech.Stackup.Bottom == BoundaryCondition.Ground)
        {
            plane = true;
            groundM = stack[0].BottomM;
            hBelow = signal.BottomM - groundM;
            warnings.Add(
                "No copper under the trace on any layer below it at the cut, so the stackup's own bottom " +
                "ground (Stackup.Bottom = Ground) was taken as the reference. If the board has no such " +
                "plane, this impedance is not what the board has.");
            flags.Add("stackup bottom used as ground");
        }

        double hDbu = (hBelow ?? hAbove ?? w * metresPerDbu) / metresPerDbu;
        double reach = 0.5 * w + Math.Max(6 * hDbu, 3 * w);
        reach = Math.Min(reach, half - Math.Max(Math.Abs(cx - x), Math.Abs(cy - y)) - 1);

        // ── the conductors ──────────────────────────────────────────────────────────────────────
        var conductors = new List<EmConductor>();
        double? gapL = null, gapR = null;
        EmConductor Rect(string name, double t0, double t1, CrossSectionExtractor.Band band) =>
            new(name,
            [
                new EmPoint(t0 * metresPerDbu, band.BottomM - groundM), new EmPoint(t1 * metresPerDbu, band.BottomM - groundM),
                new EmPoint(t1 * metresPerDbu, band.TopM - groundM),    new EmPoint(t0 * metresPerDbu, band.TopM - groundM),
            ], band.Layer.SigmaSm > 0 ? band.Layer.SigmaSm : double.PositiveInfinity);

        conductors.Add(Rect("signal", sa, sb, signal));
        double sliver = Math.Max(1.0 * dbuPerMicron, 0.01 * w);
        int grounded = 0;
        foreach (var band in bands)
        {
            if (plane && band.TopM <= groundM + 1e-15) continue;                  // shielded by the plane
            if (upperRef is not null && band.BottomM > upperRef.BottomM) continue; // shielded above
            if (!copper.TryGetValue(band.Index, out var cu)) continue;

            foreach (var (t0, t1) in cu.Intervals(cx, cy, ux, uy))
            {
                double a = Math.Max(t0, -reach), b = Math.Min(t1, reach);
                if (b - a < sliver) continue;
                if (band.Index == signal.Index)
                {
                    if (a <= 0 && b >= 0) continue;   // the signal itself
                    if (b <= sa) gapL = Math.Min(gapL ?? double.MaxValue, sa - b);
                    if (a >= sb) gapR = Math.Min(gapR ?? double.MaxValue, a - sb);
                }
                conductors.Add(Rect($"ground {++grounded}", a, b, band));
            }
        }

        if (!plane && grounded == 0)
            return TraceImpedanceResult.Refused(
                $"The trace at {Pt(cx, cy)} has no return conductor: no copper covers it on any layer " +
                $"below or above, and nothing is beside it within {Len(reach)}. An impedance needs a " +
                "reference. " + string.Join(" ", warnings));

        // ── 4. the solve ────────────────────────────────────────────────────────────────────────
        var regions = CrossSectionExtractor.BuildRegions(stack, groundM, out _);
        if (!plane)
        {
            // No image plane: below the stack is air, not the bottom dielectric extended to −∞.
            var first = regions[0];
            regions[0] = first with { YBottom = 0 };
            regions.Insert(0, new EmDielectricRegion(double.NegativeInfinity, 0, EmMaterial.Air));
            notes.Add("No ground plane under the trace: the return is the copper modelled beside and above " +
                      $"it within {Len(reach)} of its edges, and the result depends on that copper.");
        }

        var problem = new EmProblem(conductors, regions,
                                    plane ? new EmGroundPlane(0, double.PositiveInfinity) : null,
                                    [], w * metresPerDbu);
        double c, c0;
        try
        {
            var mesh = BoundaryMesher.Mesh(problem, EmMeshSettings.Default).Mesh;
            ct.ThrowIfCancellationRequested();
            c  = ChargeSolver.MaxwellCapacitance(mesh)[0, 0].Real;
            c0 = ChargeSolver.MaxwellCapacitance(ChargeSolver.AirFilled(mesh))[0, 0].Real;
        }
        catch (InvalidOperationException ex)
        {
            return TraceImpedanceResult.Refused($"The cross-section at {Pt(cx, cy)} could not be solved: {ex.Message}");
        }
        if (!(c > 0) || !(c0 > 0))
            return TraceImpedanceResult.Refused($"The cross-section at {Pt(cx, cy)} solved to no capacitance.");

        double z0 = 1.0 / (EmConstants.C0 * Math.Sqrt(c * c0));

        // ── the section, and where its reference breaks ─────────────────────────────────────────
        double step = 0.5 * w;
        double reachAlong = half - Math.Max(Math.Abs(cx - x), Math.Abs(cy - y)) - w;
        // The layer nearest the trace on each side is walked along the whole constant-width section:
        // where it stops covering the trace is where the return path changes. A layer missing under
        // the ENTIRE section is the signature of a deliberate clearance (a wide trace referenced to a
        // deeper plane — the reported board does exactly this, correctly); one missing under PART of
        // it is the fault a reviewer is looking for.
        var nearestBelow = below.FirstOrDefault();
        var nearestAbove = above.FirstOrDefault();
        var uncovered = new Dictionary<int, List<double>>();
        int stationCount = 0;
        double sMin = 0, sMax = 0;
        foreach (int dir in new[] { -1, 1 })
        {
            for (double s = dir < 0 ? step : 0; s <= reachAlong; s += step)
            {
                double qx = cx + dir * s * vx, qy = cy + dir * s * vy;
                if (sig.ChordAt(qx, qy, ux, uy) is not { } q) break;
                if (Math.Abs(q.T1 - q.T0 - w) > tol || Math.Abs(0.5 * (q.T0 + q.T1)) > tol) break;
                if (dir < 0) sMin = -s; else sMax = s;
                stationCount++;

                foreach (var band in new[] { nearestBelow, nearestAbove })
                {
                    if (band is null) continue;
                    if (Coverage(band, qx, qy, q.T0, q.T1) >= FullCoverage) continue;
                    if (!uncovered.TryGetValue(band.Index, out var list)) uncovered[band.Index] = list = [];
                    list.Add(dir * s);
                }
            }
        }

        void Report(CrossSectionExtractor.Band? nearest, CrossSectionExtractor.Band? reference,
                    List<(CrossSectionExtractor.Band Band, double Cov)> skipped, string where, double? height)
        {
            if (nearest is null) return;
            var gaps = uncovered.TryGetValue(nearest.Index, out var l) ? l : [];
            bool coveredAtCut = ReferenceEquals(nearest, reference);
            string then = reference is null ? "" : $" the reference is '{reference.Layer.Name}', {Len((height ?? 0) / metresPerDbu)} {where}";

            if (!coveredAtCut && skipped.FirstOrDefault().Cov is > 0.005 and var cov)
            {
                warnings.Add($"'{nearest.Layer.Name}' covers only {cov:P0} of the trace width {where} it at " +
                             $"{Pt(cx, cy)}: copper is partly under the trace, and the answer holds it at ground." +
                             (then.Length > 0 ? $" Otherwise{then}." : ""));
                flags.Add($"{nearest.Layer.Name} covers only {cov:P0} of the width");
            }
            else if (!coveredAtCut && gaps.Count >= stationCount && reference is not null)
                notes.Add($"'{nearest.Layer.Name}' has no copper {where} this trace anywhere along its " +
                          $"{Len(sMax - sMin)} constant-width section, so{then} — what a layer cleared under " +
                          "the trace on purpose looks like.");

            foreach (var (s0, s1) in Merge(gaps.OrderBy(s => s).ToList(), step))
            {
                if (!coveredAtCut && gaps.Count >= stationCount) break;   // the whole section: said above
                double lo = s0 - 0.5 * step, hi = s1 + 0.5 * step;
                bool here = s0 <= 0 && s1 >= 0;
                warnings.Add(
                    $"Discontinuity: '{nearest.Layer.Name}' is missing {where} this trace for about {Len(hi - lo)}, " +
                    $"from {Pt(cx + lo * vx, cy + lo * vy)} to {Pt(cx + hi * vx, cy + hi * vy)}, but present " +
                    "elsewhere along the same section — the return path under the trace is broken there" +
                    (here ? $" (the point clicked is in that gap, so{(then.Length > 0 ? then : " there is no reference on that side")})." : "."));
                flags.Add($"{nearest.Layer.Name} {where} broken for {Len(hi - lo)} at {Pt(cx + 0.5 * (lo + hi) * vx, cy + 0.5 * (lo + hi) * vy)}");
            }

            foreach (var (band, c) in skipped.Skip(1))
                notes.Add($"'{band.Layer.Name}' {(c <= 0.005 ? "has no copper" : $"covers only {c:P0} of the width")} " +
                          $"{where} the trace at the cut either.");
        }

        Report(nearestBelow, lowerRef, skippedBelow, "below", hBelow);
        Report(nearestAbove, upperRef, skippedAbove, "above", hAbove);

        // ── naming ──────────────────────────────────────────────────────────────────────────────
        double hRefDbu = hDbu;
        bool coplanarL = gapL is { } gl && gl <= 3 * hRefDbu;
        bool coplanarR = gapR is { } gr && gr <= 3 * hRefDbu;
        string config = (lowerRef is not null || plane, upperRef is not null) switch
        {
            (true, true)  => coplanarL || coplanarR ? "stripline with coplanar ground" : "stripline",
            (true, false) => coplanarL && coplanarR ? "grounded coplanar waveguide"
                           : coplanarL || coplanarR ? "microstrip with coplanar ground on one side"
                           : "microstrip",
            (false, true) => coplanarL || coplanarR ? "grounded coplanar waveguide (reference above)" : "microstrip (reference above)",
            _             => "coplanar waveguide (no ground plane)",
        };

        if (grounded > 0)
            notes.Add($"{grounded} other conductor{(grounded == 1 ? "" : "s")} within {Len(reach)} of the " +
                      "trace's edges held at ground in the solve.");

        // What a gap ends on: a point just inside the far copper, on the cut. Drawn copper there makes it
        // a conductor even where a via land is also there (a via in a pour is the pour's edge); only a
        // land standing alone — a via fence with its pour removed — is a via pad.
        double edgeStep = Math.Max(1.0, 0.5 * dbuPerMicron);
        string GapEndsOn(double t)
        {
            long px = (long)Math.Round(cx + t * ux), py = (long)Math.Round(cy + t * uy);
            if (Regions.Contains(signalOther, px, py)) return TraceImpedanceResult.GapToConductor;
            return Regions.Contains(signalViaPads, px, py)
                ? TraceImpedanceResult.GapToViaPad : TraceImpedanceResult.GapToConductor;
        }

        return new TraceImpedanceResult
        {
            Z0Ohms = z0,
            Eeff = c / c0,
            WidthM = w * metresPerDbu,
            GapLeftM = gapL * metresPerDbu,
            GapRightM = gapR * metresPerDbu,
            GapLeftTo = gapL is { } gl2 ? GapEndsOn(sa - gl2 - edgeStep) : null,
            GapRightTo = gapR is { } gr2 ? GapEndsOn(sb + gr2 + edgeStep) : null,
            HeightBelowM = hBelow,
            HeightAboveM = hAbove,
            SignalLayer = signal.Layer.Name,
            ReferenceBelow = lowerRef?.Layer.Name ?? (plane ? "stackup bottom ground" : null),
            ReferenceAbove = upperRef?.Layer.Name,
            Configuration = config,
            AxisAngleDeg = axisDeg,
            SectionLengthM = (sMax - sMin) * metresPerDbu,
            CentreX = (long)Math.Round(cx),
            CentreY = (long)Math.Round(cy),
            GroundedConductors = grounded,
            Warnings = warnings,
            Flags = flags,
            Notes = notes,
            DisplayUnit = displayUnit,
            DbuPerMicron = dbuPerMicron,
        };
    }

    /// <summary>Consecutive stations (within one step) merged into runs.</summary>
    private static IEnumerable<(double S0, double S1)> Merge(List<double> sorted, double step)
    {
        if (sorted.Count == 0) yield break;
        double s0 = sorted[0], prev = sorted[0];
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - prev > 1.5 * step) { yield return (s0, prev); s0 = sorted[i]; }
            prev = sorted[i];
        }
        yield return (s0, prev);
    }

    /// <summary>One layer's unioned copper and its edges, with the line queries the probe makes.</summary>
    private sealed class Copper
    {
        public Paths64 Paths { get; }
        private readonly double[] _ax, _ay, _bx, _by;

        public Copper(Paths64 paths)
        {
            Paths = paths;
            int n = paths.Sum(p => p.Count);
            _ax = new double[n]; _ay = new double[n]; _bx = new double[n]; _by = new double[n];
            int k = 0;
            foreach (var p in paths)
                for (int i = 0; i < p.Count; i++, k++)
                {
                    var a = p[i]; var b = p[(i + 1) % p.Count];
                    _ax[k] = a.X; _ay[k] = a.Y; _bx[k] = b.X; _by[k] = b.Y;
                }
        }

        /// <summary>Every crossing of the line P + t·u with an edge: (t, edge). Half-open on the side
        /// test, so a line through a vertex is counted once.</summary>
        private List<(double T, int Edge)> Crossings(double px, double py, double ux, double uy)
        {
            var hits = new List<(double, int)>();
            for (int i = 0; i < _ax.Length; i++)
            {
                double sa = ux * (_ay[i] - py) - uy * (_ax[i] - px);
                double sb = ux * (_by[i] - py) - uy * (_bx[i] - px);
                if (sa > 0 == sb > 0) continue;
                double l = sa / (sa - sb);
                double hx = _ax[i] + l * (_bx[i] - _ax[i]), hy = _ay[i] + l * (_by[i] - _ay[i]);
                hits.Add(((hx - px) * ux + (hy - py) * uy, i));
            }
            hits.Sort((p, q) => p.Item1.CompareTo(q.Item1));
            return hits;
        }

        /// <summary>The copper interval of the line through P that contains P, or null where P is not
        /// in copper.</summary>
        public (double T0, double T1, int E0, int E1)? ChordAt(double px, double py, double ux, double uy)
        {
            var hits = Crossings(px, py, ux, uy);
            int below = hits.FindLastIndex(h => h.T < 0);
            if (below < 0 || below % 2 == 1 || below + 1 >= hits.Count) return null;   // even count before P → outside
            return (hits[below].T, hits[below + 1].T, hits[below].Edge, hits[below + 1].Edge);
        }

        /// <summary>Every copper interval of the line, in t.</summary>
        public IEnumerable<(double T0, double T1)> Intervals(double px, double py, double ux, double uy)
        {
            var hits = Crossings(px, py, ux, uy);
            for (int i = 0; i + 1 < hits.Count; i += 2)
                yield return (hits[i].T, hits[i + 1].T);
        }

        /// <summary>The length of [a, b] on the line that is copper.</summary>
        public double Covered(double px, double py, double ux, double uy, double a, double b)
        {
            double sum = 0;
            foreach (var (t0, t1) in Intervals(px, py, ux, uy))
                sum += Math.Max(0, Math.Min(t1, b) - Math.Max(t0, a));
            return sum;
        }

        /// <summary>The edge nearest P.</summary>
        public int NearestEdge(double px, double py)
        {
            int best = -1; double bestD = double.PositiveInfinity;
            for (int i = 0; i < _ax.Length; i++)
            {
                double dx = _bx[i] - _ax[i], dy = _by[i] - _ay[i];
                double len2 = dx * dx + dy * dy;
                double t = len2 == 0 ? 0 : Math.Clamp(((px - _ax[i]) * dx + (py - _ay[i]) * dy) / len2, 0, 1);
                double ex = px - _ax[i] - t * dx, ey = py - _ay[i] - t * dy;
                double d = ex * ex + ey * ey;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>sin of the angle between edge <paramref name="e"/> and the unit direction v.</summary>
        public double SinTo(int e, double vx, double vy)
        {
            if (e < 0) return 0;
            double dx = _bx[e] - _ax[e], dy = _by[e] - _ay[e];
            double len = Math.Sqrt(dx * dx + dy * dy);
            return len == 0 ? 0 : (dx * vy - dy * vx) / len;
        }

        public double AngleDeg(int e) =>
            e < 0 ? 0 : (Math.Atan2(_by[e] - _ay[e], _bx[e] - _ax[e]) * 180 / Math.PI + 360) % 180;
    }
}

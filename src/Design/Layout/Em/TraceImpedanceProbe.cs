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
// 2. UNIFORMITY. The width is re-measured along the same cut direction at ±W/2 and ±W along the axis,
//    and the chord midpoint must stay on the axis to within a few percent of the width (a jog of a
//    few microns in imported artwork is not a bend). Copper that ends within 2W is a pad or a stub;
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
    public const double FullCoverage = TraceCrossSection.FullCoverage;

    /// <summary>
    /// How far a trace's centre line may wander, as a fraction of its width, and still be one straight
    /// run. Imported artwork carries jogs of a few microns where a Gerber stroke was split or a pour
    /// clearance was re-cut (the round-8 board has a 5 µm offset in a 381 µm trace, over 90 µm of
    /// length); a jog like that is not a bend, and a probe that refused it was refusing the trace.
    /// </summary>
    public const double DriftFraction = 0.04;

    /// <summary>A coplanar gap that varies by more than this fraction along the section is flagged.</summary>
    public const double GapVariationFraction = 0.10;

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
        => ProbeFirst(shapes, tech, dbuPerMicron, x, y, [layer], displayUnit, ct);

    /// <summary>
    /// The first of <paramref name="layers"/>, in order, that has a measurable trace at the point —
    /// or, when none has, the FIRST layer's refusal.
    /// </summary>
    /// <remarks>
    /// The canvas's right-click passes the layer the user means first and every other copper layer
    /// at the point after it. On the round-8 board the current drawing layer was an inner PLANE under
    /// the trace, so the probe measured the plane (a 5.4 mm "narrowest cut") and refused; the trace
    /// the user clicked on was one layer up. A refusal on the preferred layer now falls through, and
    /// the answer says which layer it came from. The window's copper is unioned once for all of them.
    /// </remarks>
    public static TraceImpedanceResult ProbeFirst(
        IReadOnlyList<LayoutShape> shapes, Technology tech, int dbuPerMicron,
        long x, long y, IReadOnlyList<LayerKey> layers, LayoutUnit displayUnit = LayoutUnit.Um,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(tech);
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count == 0) return TraceImpedanceResult.Refused("No layer was given to measure.");
        if (dbuPerMicron <= 0) dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;

        string LayerName(LayerKey k) => tech.Layers.FirstOrDefault(l => l.Key == k)?.Name is { Length: > 0 } n
            ? n : $"layer {k.Layer}/{k.Datatype}";

        var (stack, bands, bandOf, stackRefusal) = TraceStack.StackOf(tech);

        if (!bandOf.ContainsKey(layers[0]))
            return TraceImpedanceResult.Refused(
                $"'{LayerName(layers[0])}' is not bound to a conductor of the stackup, so it has no height and no " +
                "substrate to compute an impedance from. Bind it to a conductor on the technology's " +
                "Stackup tab.");
        if (stackRefusal is not null) return TraceImpedanceResult.Refused(stackRefusal);

        // ── the copper, per conductor band, inside the window ──────────────────────────────────
        long half = (long)(WindowHalfMicrons * dbuPerMicron);
        var window = new Bbox(x - half, y - half, x + half, y + half);
        Paths64 windowRect = [[new Point64(x - half, y - half), new Point64(x + half, y - half),
                               new Point64(x + half, y + half), new Point64(x - half, y + half)]];

        var subjects = new Dictionary<int, Paths64>();
        var viaPads = new Dictionary<int, Paths64>();     // which a coplanar gap ends on
        var drawn = new Dictionary<int, Paths64>();
        foreach (var shape in shapes)
        {
            if (shape is not ViaShape && !bandOf.ContainsKey(shape.Layer)) continue;
            if (!LayoutGeometry.BboxOf(shape).Intersects(window)) continue;
            DrcRegions.Expand(shape, tech, _ => long.MaxValue, (lk, _, paths) =>
            {
                if (!bandOf.TryGetValue(lk, out var band)) return;
                if (!subjects.TryGetValue(band.Index, out var list)) subjects[band.Index] = list = [];
                list.AddRange(paths);
                var kind = shape is ViaShape ? viaPads : drawn;
                if (!kind.TryGetValue(band.Index, out var k)) kind[band.Index] = k = [];
                k.AddRange(paths);
            });
        }
        ct.ThrowIfCancellationRequested();

        var copper = new Dictionary<int, TraceCopper>();
        foreach (var (index, list) in subjects)
            copper[index] = new TraceCopper(Clipper.Intersect(list, windowRect, LayoutClipper.Rule));

        var ctx = new TraceStack
        {
            Stack = stack, Bands = bands, BandOf = bandOf, Copper = copper, Tech = tech, DbuPerMicron = dbuPerMicron,
        };

        TraceImpedanceResult? first = null;
        for (int i = 0; i < layers.Count; i++)
        {
            if (!bandOf.TryGetValue(layers[i], out var signal)) continue;
            var r = ProbeLayer(ctx, signal, LayerName(layers[i]), x, y, half,
                               viaPads.GetValueOrDefault(signal.Index) ?? [],
                               drawn.GetValueOrDefault(signal.Index) ?? [], displayUnit, ct);
            if (r.Ok)
            {
                if (i == 0) return r;
                return r with
                {
                    Notes = [$"'{LayerName(layers[0])}' has no straight trace at this point ({first!.Refusal}), " +
                             $"so the trace on '{LayerName(layers[i])}' was measured.", .. r.Notes],
                };
            }
            first ??= r;
        }
        return first!;
    }

    private static TraceImpedanceResult ProbeLayer(
        TraceStack ctx, CrossSectionExtractor.Band signal, string layerName, long x, long y, long half,
        Paths64 signalViaPads, Paths64 signalOther, LayoutUnit displayUnit, CancellationToken ct)
    {
        int dbuPerMicron = ctx.DbuPerMicron;
        string Len(double dbu) =>
            $"{LayoutUnits.Format((long)Math.Round(dbu), displayUnit, dbuPerMicron, 1)} {LayoutUnits.Suffix(displayUnit)}";
        string Pt(double px, double py) =>
            $"({LayoutUnits.Format((long)Math.Round(px), displayUnit, dbuPerMicron, 1)}, " +
            $"{LayoutUnits.Format((long)Math.Round(py), displayUnit, dbuPerMicron, 1)}) {LayoutUnits.Suffix(displayUnit)}";

        if (!ctx.Copper.TryGetValue(signal.Index, out var sig) || !Regions.Contains(sig.Paths, x, y))
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

        // The minimum chord through ONE point is the width only where the edges beside it are
        // straight. At a small jog it is a chord leaning across the jog (the round-8 board: 383.8 µm at
        // 6.4° off the axis of a 381 µm trace), so the direction is refined from the CENTRE LINE —
        // the midpoints of cuts at ±W/2, ±W and ±2W, fitted by least squares — which a jog of a few
        // microns barely moves. A bend or a corner is left alone here: a station falls off the copper,
        // or the correction is implausibly large, and the checks below say what it is.
        for (int iter = 0; iter < 3; iter++)
        {
            double[] ks = [-2, -1, -0.5, 0, 0.5, 1, 2];
            double sumS = 0, sumM = 0, sumSS = 0, sumSM = 0;
            bool all = true;
            foreach (double k in ks)
            {
                double s = k * w;
                if (sig.ChordAt(cx + s * vx, cy + s * vy, ux, uy) is not { } q) { all = false; break; }
                double m = 0.5 * (q.T0 + q.T1);
                sumS += s; sumM += m; sumSS += s * s; sumSM += s * m;
            }
            if (!all) break;
            double n = ks.Length;
            double slope = (n * sumSM - sumS * sumM) / (n * sumSS - sumS * sumS);
            if (!(Math.Abs(slope) > 1e-5) || Math.Abs(slope) > Math.Tan(10 * Math.PI / 180)) break;

            double nux = ux - slope * vx, nuy = uy - slope * vy;
            double nl = Math.Sqrt(nux * nux + nuy * nuy);
            ux = nux / nl; uy = nuy / nl;
            vx = -uy; vy = ux;
            if (sig.ChordAt(x, y, ux, uy) is not { } rc) break;
            chord0 = rc;
            w = rc.T1 - rc.T0;
            cx = x + 0.5 * (rc.T0 + rc.T1) * ux;
            cy = y + 0.5 * (rc.T0 + rc.T1) * uy;
        }

        double axisDeg = (Math.Atan2(vy, vx) * 180 / Math.PI + 360) % 180;
        double tol = Math.Max(0.02 * w, 1.0 * dbuPerMicron);
        double tolDrift = Math.Max(DriftFraction * w, 2.0 * dbuPerMicron);
        double sinTol = Math.Sin(ParallelToleranceDeg * Math.PI / 180);

        // Cross-check: the chord's two end edges, and the edge nearest the point, lie along the axis.
        // An edge whose whole extent ACROSS the axis is within the drift tolerance cannot be a bend,
        // whatever its angle: it is one facet of a jog a few microns deep.
        var nearest = sig.NearestEdge(x, y);
        foreach (var e in new[] { chord0.E0, chord0.E1, nearest })
        {
            double sin = Math.Abs(sig.SinTo(e, vx, vy));
            if (sin <= sinTol) continue;
            if (e >= 0 && sig.Length(e) * sin <= tolDrift) continue;
            return TraceImpedanceResult.Refused(
                $"The copper at {Pt(x, y)} is not a straight run: the edges beside the point are not " +
                $"parallel to one axis (the narrowest cut is {Len(w)} at {axisDeg:0.#}°, but an edge there " +
                $"runs at {sig.AngleDeg(e):0.#}°). It is a bend, a mitre, a corner or a junction, where a " +
                "trace has no single width. Click on a straight, constant-width part of the trace.");
        }

        // ── 2. uniformity ±W, and copper for ±2W ────────────────────────────────────────────────
        // Copper must CONTINUE for two widths either way (otherwise the point is on a pad, a land or
        // a stub), but the width and the centre line are held only over ±W. Round 8's trace narrows
        // from 381 to 320 µm about 1.4 × its width from where the reviewer clicked; that cut is a
        // perfectly good cross-section, and a width step that far off is what the section walk below
        // reports, not a reason to answer nothing.
        foreach (int k in new[] { -2, 2 })
        {
            if (sig.ChordAt(cx + k * w * vx, cy + k * w * vy, ux, uy) is null)
                return TraceImpedanceResult.Refused(
                    $"The copper at {Pt(x, y)} is not a line: it ends within {Math.Abs(k)} × its width " +
                    $"({Len(w)}) along its own axis. It is a pad, a via land or a short stub, and a trace " +
                    "impedance is not defined for it. Click on a trace.");
        }

        var widths = new SortedDictionary<double, double> { [0] = w };
        foreach (double k in new[] { -1, -0.5, 0.5, 1 })
        {
            double qx = cx + k * w * vx, qy = cy + k * w * vy;
            if (sig.ChordAt(qx, qy, ux, uy) is not { } side)
                return TraceImpedanceResult.Refused(
                    $"The copper at {Pt(x, y)} is not a line: it ends within {Math.Abs(k)} × its width " +
                    $"({Len(w)}) along its own axis. It is a pad, a via land or a short stub, and a trace " +
                    "impedance is not defined for it. Click on a trace.");

            double drift = 0.5 * (side.T0 + side.T1);
            if (Math.Abs(drift) > tolDrift)
                return TraceImpedanceResult.Refused(
                    $"The trace at {Pt(x, y)} is not straight: its centre line moves by {Len(Math.Abs(drift))} " +
                    $"within {Math.Abs(k)} × its width along the axis. It is a bend or a curve. Click on a " +
                    "straight part of the trace.");
            widths[k] = side.T1 - side.T0;
        }

        if (widths.Values.Any(v => Math.Abs(v - w) > tol))
        {
            var seq = widths.Values.ToArray();   // −W … +W
            bool rising  = seq.Zip(seq.Skip(1)).All(p => p.Second > p.First);
            bool falling = seq.Zip(seq.Skip(1)).All(p => p.Second < p.First);
            return TraceImpedanceResult.Refused(rising || falling
                ? $"The trace at {Pt(x, y)} is a taper: its width runs from {Len(seq[0])} to {Len(seq[^1])} " +
                  $"over 2 × its width along the axis, so it has no single impedance. Click on a " +
                  "constant-width part."
                : $"The trace at {Pt(x, y)} is {Len(w)} wide, but {Len(widths.First(kv => Math.Abs(kv.Value - w) > tol).Value)} " +
                  "within 1 × that along its axis: a junction, a width step or the end of the section is " +
                  "next to the point. Click further into the constant-width run.");
        }

        ct.ThrowIfCancellationRequested();

        // ── 3. the cut, and 4. the solve ────────────────────────────────────────────────────────
        // Re-measured through the centre, so the signal is [−W/2, W/2] on the cut.
        var centre = sig.ChordAt(cx, cy, ux, uy)!.Value;
        double reachLimit = half - Math.Max(Math.Abs(cx - x), Math.Abs(cy - y)) - 1;
        var cut = TraceCrossSection.Cut(ctx, signal, cx, cy, ux, uy, centre.T0, centre.T1, reachLimit, 1);
        double sa = cut.Sa, sb = cut.Sb;
        w = cut.Width;

        var warnings = new List<string>();
        var flags = new List<string>();
        var notes = new List<string>();
        double metresPerDbu = ctx.MetresPerDbu;

        if (cut.LowerRef is null && cut.Below.Count > 0)
        {
            warnings.Add(
                $"No ground under the trace: no layer below it covers its width at {Pt(cx, cy)} " +
                $"({string.Join(", ", cut.Below.Select(b => $"'{b.Layer.Name}'"))}).");
            flags.Add("no ground under the trace");
        }
        if (cut.StackupBottomUsed)
        {
            warnings.Add(
                "No copper under the trace on any layer below it at the cut, so the stackup's own bottom " +
                "ground (Stackup.Bottom = Ground) was taken as the reference. If the board has no such " +
                "plane, this impedance is not what the board has.");
            flags.Add("stackup bottom used as ground");
        }

        if (cut.Refusal is not null)
            return TraceImpedanceResult.Refused(
                $"The trace at {Pt(cx, cy)} has no return conductor: no copper covers it on any layer " +
                $"below or above, and nothing is beside it within {Len(cut.Reach)}. An impedance needs a " +
                "reference. " + string.Join(" ", warnings));

        if (!cut.Plane)
            notes.Add("No ground plane under the trace: the return is the copper modelled beside and above " +
                      $"it within {Len(cut.Reach)} of its edges, and the result depends on that copper.");

        var (c, c0, solveRefusal) = TraceCrossSection.Solve(ctx, cut, ct);
        if (solveRefusal is not null)
            return TraceImpedanceResult.Refused(solveRefusal == "solved to no capacitance"
                ? $"The cross-section at {Pt(cx, cy)} solved to no capacitance."
                : $"The cross-section at {Pt(cx, cy)} could not be solved: {solveRefusal}");

        double z0 = TraceCrossSection.Z0(c, c0);

        // ── the section, and where its reference breaks ─────────────────────────────────────────
        double step = 0.5 * w;
        double reachAlong = half - Math.Max(Math.Abs(cx - x), Math.Abs(cy - y)) - w;
        // The layer nearest the trace on each side is walked along the whole constant-width section:
        // where it stops covering the trace is where the return path changes. A layer missing under
        // the ENTIRE section is the signature of a deliberate clearance (a wide trace referenced to a
        // deeper plane — the reported board does exactly this, correctly); one missing under PART of
        // it is the fault a reviewer is looking for.
        var nearestBelow = cut.Below.FirstOrDefault();
        var nearestAbove = cut.Above.FirstOrDefault();
        var uncovered = new Dictionary<int, List<double>>();
        int stationCount = 0;
        double sMin = 0, sMax = 0;
        // The coplanar gap either side, station by station: the cut answers for ONE gap, and imported
        // pours are rarely straight beside a trace (round 8: a pour notch takes one side's gap from
        // 259 µm to 184 µm for 240 µm of the run). Said, so the number is read for what it is.
        var gapsL = new List<double>(); var gapsR = new List<double>();
        foreach (int dir in new[] { -1, 1 })
        {
            for (double s = dir < 0 ? step : 0; s <= reachAlong; s += step)
            {
                double qx = cx + dir * s * vx, qy = cy + dir * s * vy;
                if (sig.ChordAt(qx, qy, ux, uy) is not { } q) break;
                if (Math.Abs(q.T1 - q.T0 - w) > tol || Math.Abs(0.5 * (q.T0 + q.T1)) > tolDrift) break;
                if (dir < 0) sMin = -s; else sMax = s;
                stationCount++;

                foreach (var band in new[] { nearestBelow, nearestAbove })
                {
                    if (band is null) continue;
                    if (TraceCrossSection.Coverage(ctx, band, qx, qy, ux, uy, q.T0, q.T1) >= FullCoverage) continue;
                    if (!uncovered.TryGetValue(band.Index, out var list)) uncovered[band.Index] = list = [];
                    list.Add(dir * s);
                }

                if (cut.GapL is not null || cut.GapR is not null)
                {
                    double? gl = null, gr = null;
                    foreach (var (t0, t1) in sig.Intervals(qx, qy, ux, uy))
                    {
                        if (t1 <= q.T0 && q.T0 - t1 <= cut.Reach) gl = Math.Min(gl ?? double.MaxValue, q.T0 - t1);
                        if (t0 >= q.T1 && t0 - q.T1 <= cut.Reach) gr = Math.Min(gr ?? double.MaxValue, t0 - q.T1);
                    }
                    if (gl is { } l) gapsL.Add(l);
                    if (gr is { } r) gapsR.Add(r);
                }
            }
        }

        void GapVaries(List<double> gaps, double? atCut, string side)
        {
            if (atCut is not { } g || gaps.Count < 2) return;
            double lo = gaps.Min(), hi = gaps.Max();
            if (hi - lo <= GapVariationFraction * Math.Max(g, 1)) return;
            notes.Add($"The coplanar gap on the {side} varies from {Len(lo)} to {Len(hi)} along the " +
                      $"{Len(sMax - sMin)} constant-width section; the answer is for the {Len(g)} gap at the " +
                      "point measured. The Impedance Analysis report gives Z0 along the whole trace.");
            flags.Add($"G {side} varies {Len(lo)}–{Len(hi)} along the run");
        }
        GapVaries(gapsL, cut.GapL, "left");
        GapVaries(gapsR, cut.GapR, "right");

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

            foreach (var (band, cv) in skipped.Skip(1))
                notes.Add($"'{band.Layer.Name}' {(cv <= 0.005 ? "has no copper" : $"covers only {cv:P0} of the width")} " +
                          $"{where} the trace at the cut either.");
        }

        Report(nearestBelow, cut.LowerRef, cut.SkippedBelow, "below", cut.HBelow);
        Report(nearestAbove, cut.UpperRef, cut.SkippedAbove, "above", cut.HAbove);

        if (cut.Grounded > 0)
            notes.Add($"{cut.Grounded} other conductor{(cut.Grounded == 1 ? "" : "s")} within {Len(cut.Reach)} of the " +
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
            GapLeftM = cut.GapL * metresPerDbu,
            GapRightM = cut.GapR * metresPerDbu,
            GapLeftTo = cut.GapL is { } gl2 ? GapEndsOn(sa - gl2 - edgeStep) : null,
            GapRightTo = cut.GapR is { } gr2 ? GapEndsOn(sb + gr2 + edgeStep) : null,
            HeightBelowM = cut.HBelow,
            HeightAboveM = cut.HAbove,
            SignalLayer = signal.Layer.Name,
            ReferenceBelow = cut.LowerRef?.Layer.Name ?? (cut.Plane ? "stackup bottom ground" : null),
            ReferenceAbove = cut.UpperRef?.Layer.Name,
            Configuration = cut.Configuration,
            AxisAngleDeg = axisDeg,
            SectionLengthM = (sMax - sMin) * metresPerDbu,
            CentreX = (long)Math.Round(cx),
            CentreY = (long)Math.Round(cy),
            GroundedConductors = cut.Grounded,
            Warnings = warnings,
            Flags = flags,
            Notes = notes,
            DisplayUnit = displayUnit,
            DbuPerMicron = dbuPerMicron,
        };
    }

    /// <summary>Consecutive stations (within one step) merged into runs.</summary>
    internal static IEnumerable<(double S0, double S1)> Merge(List<double> sorted, double step)
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
}

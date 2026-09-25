// The cross-section half of the trace-impedance tools: given a straight piece of trace (its centre,
// its cut direction and its extent on the cut), find the references above and below by COPPER
// COVERAGE, gather every other conductor within reach as ground, and solve C and C₀. One
// implementation for the probe (one cut) and the whole-layout analysis (thousands of cuts, most of
// them repeats, which is why the geometry is separated from the solve and carries a key).
//
// The method is TraceImpedanceProbe's header, steps 3 and 4.

using System.Globalization;
using System.Text;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>The stack, its conductor bands and each band's copper — built once, cut many times.</summary>
internal sealed class TraceStack
{
    public required List<CrossSectionExtractor.Band> Stack { get; init; }
    public required List<CrossSectionExtractor.Band> Bands { get; init; }
    public required Dictionary<LayerKey, CrossSectionExtractor.Band> BandOf { get; init; }
    public required Dictionary<int, TraceCopper> Copper { get; init; }
    public required Technology Tech { get; init; }
    public required int DbuPerMicron { get; init; }

    public double MetresPerDbu => 1.0 / (DbuPerMicron * 1e6);

    /// <summary>The stack and its bands, or a refusal naming the stackup defect.</summary>
    public static (List<CrossSectionExtractor.Band> Stack, List<CrossSectionExtractor.Band> Bands,
                   Dictionary<LayerKey, CrossSectionExtractor.Band> BandOf, string? Refusal) StackOf(Technology tech)
    {
        var stack = CrossSectionExtractor.BuildStack(tech.Stackup);
        var bands = stack.Where(b => b.Layer.Kind == StackupKind.Conductor).ToList();
        var bandOf = new Dictionary<LayerKey, CrossSectionExtractor.Band>();
        foreach (var b in bands)
            foreach (var dl in b.Layer.DrawingLayers)
                bandOf.TryAdd(dl, b);

        foreach (var b in stack)
        {
            if (b.Layer.ThicknessDbu <= 0)
                return (stack, bands, bandOf,
                    $"Stackup layer '{b.Layer.Name}' has zero thickness, so every height above it would be " +
                    "wrong. Set its thickness on the technology's Stackup tab.");
            if (b.Layer.Kind == StackupKind.Dielectric && !(b.Layer.Epsr >= 1))
                return (stack, bands, bandOf,
                    $"Stackup layer '{b.Layer.Name}' has εr = {b.Layer.Epsr.ToString("G4", CultureInfo.InvariantCulture)}; " +
                    "relative permittivity is ≥ 1. Set it on the technology's Stackup tab.");
        }
        return (stack, bands, bandOf, null);
    }
}

/// <summary>One cut through a trace, before the solve. Lengths along the cut in DBU, relative to the
/// cut point; heights in metres.</summary>
internal sealed class TraceCut
{
    public required CrossSectionExtractor.Band Signal { get; init; }
    public double Sa { get; init; }
    public double Sb { get; init; }
    public double Width => Sb - Sa;

    public List<CrossSectionExtractor.Band> Below { get; init; } = [];
    public List<CrossSectionExtractor.Band> Above { get; init; } = [];
    public CrossSectionExtractor.Band? LowerRef { get; init; }
    public CrossSectionExtractor.Band? UpperRef { get; init; }
    public List<(CrossSectionExtractor.Band Band, double Cov)> SkippedBelow { get; init; } = [];
    public List<(CrossSectionExtractor.Band Band, double Cov)> SkippedAbove { get; init; } = [];

    public bool Plane { get; init; }
    public double GroundM { get; init; }
    public double? HBelow { get; init; }
    public double? HAbove { get; init; }
    public double HDbu { get; init; }
    public double Reach { get; init; }

    /// <summary>The stackup's own bottom was taken as ground because no copper was.</summary>
    public bool StackupBottomUsed { get; init; }

    public List<EmConductor> Conductors { get; init; } = [];
    public double? GapL { get; init; }
    public double? GapR { get; init; }
    public int Grounded { get; init; }

    /// <summary>Why this cut has no answer, or null.</summary>
    public string? Refusal { get; init; }

    public string Configuration { get; init; } = "";

    /// <summary>A string that is equal for two cuts exactly when their solves are — the conductors'
    /// rectangles quantised, the plane and its height.</summary>
    public string Key { get; init; } = "";
}

internal static class TraceCrossSection
{
    /// <summary>A layer covers the trace when its copper spans this fraction of the width at the cut.</summary>
    public const double FullCoverage = 0.995;

    /// <summary>Beyond the knee, a conductor edge's distance from the signal is keyed to this fraction
    /// of itself.</summary>
    public const double KeyDistanceFraction = 0.04;

    /// <summary>As <see cref="KeyDistanceFraction"/>, for the end of an interval facing away from the
    /// signal.</summary>
    public const double KeyFarFraction = 0.25;

    /// <summary>At most this many conductors besides the signal enter one solve — the nearest.</summary>
    public const int MaxConductors = 6;

    public static double Coverage(TraceStack s, CrossSectionExtractor.Band band, double px, double py,
                                  double ux, double uy, double a, double b) =>
        s.Copper.TryGetValue(band.Index, out var cu) ? cu.Covered(px, py, ux, uy, a, b) / (b - a) : 0;

    /// <summary>
    /// The cut through (<paramref name="cx"/>, <paramref name="cy"/>) along (<paramref name="ux"/>,
    /// <paramref name="uy"/>), where the signal occupies [<paramref name="sa"/>, <paramref name="sb"/>].
    /// </summary>
    /// <param name="reachLimit">The most copper may be gathered either side of the cut point — the
    /// probe's window edge; unbounded for the whole-layout analysis.</param>
    /// <param name="quantumDbu">The key's resolution.</param>
    public static TraceCut Cut(TraceStack s, CrossSectionExtractor.Band signal,
                               double cx, double cy, double ux, double uy, double sa, double sb,
                               double reachLimit, double quantumDbu)
    {
        double w = sb - sa;
        var below = s.Bands.Where(b => b.TopM <= signal.BottomM).OrderByDescending(b => b.TopM).ToList();
        var above = s.Bands.Where(b => b.BottomM >= signal.TopM).OrderBy(b => b.BottomM).ToList();

        (CrossSectionExtractor.Band? Ref, List<(CrossSectionExtractor.Band Band, double Cov)> Skipped)
            FindReference(List<CrossSectionExtractor.Band> side)
        {
            var skipped = new List<(CrossSectionExtractor.Band, double)>();
            foreach (var band in side)
            {
                double cov = Coverage(s, band, cx, cy, ux, uy, sa, sb);
                if (cov >= FullCoverage) return (band, skipped);
                skipped.Add((band, cov));
            }
            return (null, skipped);
        }

        var (lowerRef, skippedBelow) = FindReference(below);
        var (upperRef, skippedAbove) = FindReference(above);

        double metresPerDbu = s.MetresPerDbu;
        double? hBelow = lowerRef is null ? null : signal.BottomM - lowerRef.TopM;
        double? hAbove = upperRef is null ? null : upperRef.BottomM - signal.TopM;

        bool plane = lowerRef is not null;
        double groundM = lowerRef?.TopM ?? s.Stack[0].BottomM;
        bool stackupBottom = false;
        // Only with dielectric between: under the BOTTOM copper layer the stackup's bottom boundary is
        // the copper's own underside, and a plane there would short the trace.
        if (lowerRef is null && s.Tech.Stackup.Bottom == BoundaryCondition.Ground
            && signal.BottomM - s.Stack[0].BottomM > 1e-12)
        {
            plane = true;
            stackupBottom = true;
            groundM = s.Stack[0].BottomM;
            hBelow = signal.BottomM - groundM;
        }

        double hDbu = (hBelow ?? hAbove ?? w * metresPerDbu) / metresPerDbu;
        double reach = 0.5 * w + Math.Max(6 * hDbu, 3 * w);
        reach = Math.Min(reach, reachLimit);

        double sliver = Math.Max(1.0 * s.DbuPerMicron, 0.01 * w);
        double mid = 0.5 * (sa + sb);

        // Coplanar ground nearer than the reference bounds the field: a trace 99 µm wide with ground
        // 150 µm either side and its reference 1.4 mm away has nothing to learn from copper 8 mm off,
        // and gathering it made one cut forty conductors and a 28-second solve.
        if (s.Copper.TryGetValue(signal.Index, out var own))
        {
            double? nl = null, nr = null;
            foreach (var (t0, t1) in own.Intervals(cx, cy, ux, uy, reach + 1))
            {
                double a = Math.Max(t0, -reach), b = Math.Min(t1, reach);
                if (b - a < sliver || (a <= mid && b >= mid)) continue;
                if (b <= sa) nl = Math.Min(nl ?? double.MaxValue, sa - b);
                if (a >= sb) nr = Math.Min(nr ?? double.MaxValue, a - sb);
            }
            if (nl is { } l && nr is { } r)
                reach = Math.Min(reach, 0.5 * w + Math.Max(3 * w, 6 * (Math.Min(l, r) + w)));
        }

        // ── the conductors ──────────────────────────────────────────────────────────────────────
        // Per layer, what overlaps the trace's own span and the NEAREST interval either side of it:
        // anything further out on the same layer stands in that one's shadow.
        var conductors = new List<EmConductor>();
        // The key: the signal's edges to within one quantum, every other edge by its distance from
        // the signal to within a quantum near it and a few percent of that distance further out. A
        // pour's outline curving a millimetre away changes the answer by nothing a review can see, and
        // keying it exactly made nearly every cut along a real trace a solve of its own.
        var key = new StringBuilder();
        double q = Math.Max(1, quantumDbu);
        long Q(double v) => (long)Math.Round(v / q);
        // An interval's FAR end — the one away from the signal — is keyed coarser still: where a
        // plane or a pour finally stops a millimetre out is the slowest-varying thing in the cut and
        // the one it is least sensitive to.
        long D(double t, bool far)
        {
            double f = far ? KeyFarFraction : KeyDistanceFraction;
            double d = t > sb ? t - sb : t < sa ? sa - t : 0;
            int sign = t > sb ? 1 : t < sa ? -1 : 0;
            double knee = q / f;
            long bucket = d <= knee
                ? (long)Math.Round(d / q)
                : (long)Math.Round(1 / f + Math.Log(d / knee) / Math.Log(1 + f));
            return sign * bucket;
        }

        double? gapL = null, gapR = null;
        EmConductor Rect(string name, double t0, double t1, CrossSectionExtractor.Band band) =>
            new(name,
            [
                new EmPoint(t0 * metresPerDbu, band.BottomM - groundM), new EmPoint(t1 * metresPerDbu, band.BottomM - groundM),
                new EmPoint(t1 * metresPerDbu, band.TopM - groundM),    new EmPoint(t0 * metresPerDbu, band.TopM - groundM),
            ], band.Layer.SigmaSm > 0 ? band.Layer.SigmaSm : double.PositiveInfinity);

        conductors.Add(Rect("signal", sa, sb, signal));
        key.Append(signal.Index).Append(':').Append(Q(sb - sa));
        key.Append(plane ? "|P" : "|N").Append(groundM.ToString("R", CultureInfo.InvariantCulture));

        int grounded = 0;
        var candidates = new List<(double Distance, CrossSectionExtractor.Band Band, double A, double B)>();
        foreach (var band in s.Bands)
        {
            if (plane && band.TopM <= groundM + 1e-15) continue;                  // shielded by the plane
            if (upperRef is not null && band.BottomM > upperRef.BottomM) continue; // shielded above
            if (!s.Copper.TryGetValue(band.Index, out var cu)) continue;

            var kept = new List<(double A, double B)>();
            (double A, double B)? left = null, right = null;
            foreach (var (t0, t1) in cu.Intervals(cx, cy, ux, uy, reach + 1))
            {
                double a = Math.Max(t0, -reach), b = Math.Min(t1, reach);
                if (b - a < sliver) continue;
                if (band.Index == signal.Index && a <= mid && b >= mid) continue;   // the signal itself
                if (b <= sa) { if (left is null || b > left.Value.B) left = (a, b); }
                else if (a >= sb) { if (right is null || a < right.Value.A) right = (a, b); }
                else kept.Add((a, b));
            }
            if (left is { } lft) kept.Add(lft);
            if (right is { } rgt) kept.Add(rgt);
            kept.Sort();

            foreach (var (a, b) in kept)
            {
                if (band.Index == signal.Index)
                {
                    if (b <= sa) gapL = Math.Min(gapL ?? double.MaxValue, sa - b);
                    if (a >= sb) gapR = Math.Min(gapR ?? double.MaxValue, a - sb);
                }
                // Distance from the signal's rectangle, for the cap below.
                double lateral = b <= sa ? sa - b : a >= sb ? a - sb : 0;
                double vertical = band.TopM <= signal.BottomM ? (signal.BottomM - band.TopM) / metresPerDbu
                                : band.BottomM >= signal.TopM ? (band.BottomM - signal.TopM) / metresPerDbu : 0;
                candidates.Add((Math.Sqrt(lateral * lateral + vertical * vertical), band, a, b));
            }
        }

        // The nearest MaxConductors of them. A trace running along the pulled-back edge of every plane
        // in an eight-layer board has partial copper under it on every layer; the nearest edges carry
        // the field, and the dense solve's cost grows with the cube of the count (20 conductors took
        // 20–45 s; capping at 10 cut a whole board to 3.6 min, and at 6 halved the inner layers again
        // with the probe's answers unchanged to 0.1 Ω).
        candidates.Sort((p, r) => p.Distance.CompareTo(r.Distance));
        if (candidates.Count > MaxConductors) candidates.RemoveRange(MaxConductors, candidates.Count - MaxConductors);
        candidates.Sort((p, r) => p.Band.Index != r.Band.Index ? p.Band.Index.CompareTo(r.Band.Index) : p.A.CompareTo(r.A));
        foreach (var (_, band, a, b) in candidates)
        {
            conductors.Add(Rect($"ground {++grounded}", a, b, band));
            key.Append('|').Append(band.Index).Append(':').Append(D(a, a < sb)).Append(',').Append(D(b, b > sa));
        }

        // ── naming ──────────────────────────────────────────────────────────────────────────────
        bool coplanarL = gapL is { } gl && gl <= 3 * hDbu;
        bool coplanarR = gapR is { } gr && gr <= 3 * hDbu;
        string config = (lowerRef is not null || plane, upperRef is not null) switch
        {
            (true, true)  => coplanarL || coplanarR ? "stripline with coplanar ground" : "stripline",
            (true, false) => coplanarL && coplanarR ? "grounded coplanar waveguide"
                           : coplanarL || coplanarR ? "microstrip with coplanar ground on one side"
                           : "microstrip",
            (false, true) => coplanarL || coplanarR ? "grounded coplanar waveguide (reference above)" : "microstrip (reference above)",
            _             => "coplanar waveguide (no ground plane)",
        };

        return new TraceCut
        {
            Signal = signal, Sa = sa, Sb = sb,
            Below = below, Above = above,
            LowerRef = lowerRef, UpperRef = upperRef,
            SkippedBelow = skippedBelow, SkippedAbove = skippedAbove,
            Plane = plane, GroundM = groundM, HBelow = hBelow, HAbove = hAbove, HDbu = hDbu, Reach = reach,
            StackupBottomUsed = stackupBottom,
            Conductors = conductors, GapL = gapL, GapR = gapR, Grounded = grounded,
            Configuration = config,
            Refusal = !plane && grounded == 0 ? "no return conductor" : null,
            Key = key.ToString(),
        };
    }

    /// <summary>C and C₀ of the cut (F/m), or the solver's own reason there are none.</summary>
    public static (double C, double C0, string? Refusal) Solve(TraceStack s, TraceCut cut, CancellationToken ct = default)
    {
        var regions = CrossSectionExtractor.BuildRegions(s.Stack, cut.GroundM, out _);
        if (!cut.Plane)
        {
            // No image plane: below the stack is air, not the bottom dielectric extended to −∞.
            var first = regions[0];
            regions[0] = first with { YBottom = 0 };
            regions.Insert(0, new EmDielectricRegion(double.NegativeInfinity, 0, EmMaterial.Air));
        }

        var problem = new EmProblem(cut.Conductors, regions,
                                    cut.Plane ? new EmGroundPlane(0, double.PositiveInfinity) : null,
                                    [], cut.Width * s.MetresPerDbu);
        try
        {
            var mesh = BoundaryMesher.Mesh(problem, EmMeshSettings.Default).Mesh;
            ct.ThrowIfCancellationRequested();
            double c  = ChargeSolver.MaxwellCapacitance(mesh)[0, 0].Real;
            double c0 = ChargeSolver.MaxwellCapacitance(ChargeSolver.AirFilled(mesh))[0, 0].Real;
            if (!(c > 0) || !(c0 > 0)) return (0, 0, "solved to no capacitance");
            return (c, c0, null);
        }
        catch (InvalidOperationException ex)
        {
            return (0, 0, ex.Message);
        }
    }

    public static double Z0(double c, double c0) => 1.0 / (EmConstants.C0 * Math.Sqrt(c * c0));
}

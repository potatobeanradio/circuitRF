using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Matching;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Optimization;

/// <summary>
/// One optimizer coordinate: an opt-enabled entry mapped onto [0, 1] (brief-tuneopt-6 R-to6-1). Bounds
/// and values are in the entry's own unit (<see cref="Unit"/>; <c>deg</c> for a phase).
/// </summary>
public sealed record OptimizationCoordinate(
    string       Key,
    string       ValueKey,
    ComplexPart? Part,
    string       Unit,
    double       Min,
    double       Max,
    TuneScale    Scale,
    double?      Step,
    bool         Integer)
{
    /// <summary>
    /// The values a DISCRETE coordinate may take, ascending, in <see cref="Unit"/> (brief-tuneopt-8):
    /// the integers of the range, the step's grid, or the preferred values inside the range. Null for
    /// a continuous coordinate, and for an integer or a step whose range holds more than
    /// <see cref="MaxLevels"/> of them.
    /// </summary>
    public IReadOnlyList<double>? Levels { get; init; }

    /// <summary>Whether <see cref="Levels"/> are preferred values — snapped only when a run asks
    /// (<see cref="Decode(double, bool)"/>); an integer or a step is always applied.</summary>
    public bool Preferred { get; init; }

    /// <summary>The most levels a coordinate lists.</summary>
    public const int MaxLevels = 100_000;

    /// <summary>
    /// A placed vendor part (brief vendorlib-4 R-vl4-3a): the coordinate is the candidate's INDEX in value order,
    /// its <see cref="Levels"/> are 0 … k−1, and a decoded index names the candidate whose cell the instance is
    /// rebound to. Null for every other coordinate.
    /// </summary>
    public CircuitRF.Design.VendorLibraries.VendorPartTunable? VendorPart { get; init; }

    /// <summary>The value a unit coordinate decodes to: linear or logarithmic across the range, then
    /// snapped to an integer or a step — so the cache and the history see the value simulated.</summary>
    public double Decode(double u) => Decode(u, snapPreferred: false);

    /// <summary>
    /// <see cref="Decode(double)"/>, and with <paramref name="snapPreferred"/> a preferred coordinate
    /// lands on its nearest preferred value by ratio. A continuous algorithm searches a preferred value
    /// continuously; Discrete puts it on a rung throughout, and every other run ends with the snap stage.
    /// </summary>
    public double Decode(double u, bool snapPreferred) => Decode(u, snapPreferred, snapParts: true);

    /// <summary>
    /// <see cref="Decode(double, bool)"/>, and for a vendor part a FRACTIONAL candidate index unless
    /// <paramref name="snapParts"/> — the position the continuous stage of a mixed search places an ideal
    /// stand-in at (R-vl4-3b). A part with no stand-in is always on a candidate.
    /// </summary>
    public double Decode(double u, bool snapPreferred, bool snapParts)
    {
        u = Math.Clamp(u, 0, 1);
        if (VendorPart is { } part)
        {
            double f = u * (Max - Min);
            return snapParts || !part.HasProxy ? Math.Round(f) : f;
        }
        double v = Scale == TuneScale.Log ? Min * Math.Pow(Max / Min, u) : Min + u * (Max - Min);
        if (Preferred)
            return snapPreferred && Levels is { Count: > 0 } ladder ? PreferredValues.Snap(v, ladder) : v;
        if (Integer)
        {
            v = Math.Round(v);
            if (v > Max) v = Math.Floor(Max);
            if (v < Min) v = Math.Ceiling(Min);
        }
        else if (Step is > 0 and var s)
        {
            v = Min + Math.Round((v - Min) / s) * s;
            if (v > Max + 1e-12 * Math.Abs(Max)) v -= s;
        }
        return v;
    }

    /// <summary>Where a value sits in the box (clamped).</summary>
    public double Encode(double v)
    {
        double u = Scale == TuneScale.Log ? Math.Log(v / Min) / Math.Log(Max / Min) : (v - Min) / (Max - Min);
        return double.IsFinite(u) ? Math.Clamp(u, 0, 1) : 0;
    }

    /// <summary>A number of this coordinate as its value text — for a vendor part, the candidate's part number.</summary>
    public string Text(double v)
        => VendorPart is { Candidates.Count: > 0 } part
            ? part.Candidates[Math.Clamp((int)Math.Round(v), 0, part.Candidates.Count - 1)].Part
            : TunableValue.Format(v, Unit);

    /// <summary>
    /// The value an ideal stand-in has at fractional candidate index <paramref name="f"/>: the candidates' stated
    /// values interpolated on a log scale, so the proxy runs smoothly across the candidates' value span in their
    /// own order (R-vl4-3b). NaN for a coordinate with no stand-in.
    /// </summary>
    public double ProxyValue(double f)
    {
        if (VendorPart is not { HasProxy: true } part) return double.NaN;
        var c = part.Candidates;
        int i0 = Math.Clamp((int)Math.Floor(f), 0, c.Count - 1), i1 = Math.Min(i0 + 1, c.Count - 1);
        double t = Math.Clamp(f - i0, 0, 1), v0 = c[i0].Value!.Value, v1 = c[i1].Value!.Value;
        return Math.Exp(Math.Log(v0) + t * (Math.Log(v1) - Math.Log(v0)));
    }
}

/// <summary>Which end of a range a value has railed against (overview D17).</summary>
public enum RailEnd { Min, Max }

/// <summary>
/// A value at a bound (overview D17). <paramref name="Key"/> is the optimized entry;
/// <paramref name="Against"/> names ANOTHER part's entry when the value is held at the edge of that
/// part's range rather than its own (D18), and is null otherwise.
/// </summary>
public sealed record RailedVariable(string Key, RailEnd End, string? Against = null);

/// <summary>
/// One point of the box as the design sees it.
/// </summary>
/// <param name="Values">Value key → the text a schematic would hold (a complex value WHOLE, in the
/// form the schematic wrote it) — what the evaluation's tuned values are.</param>
/// <param name="Quantities">Value key → the number, in the value's own unit (complex for a complex
/// value; NaN when an infeasible pair has no value).</param>
/// <param name="CacheKey">The decoded value vector as one string.</param>
/// <param name="Infeasible">No complex value satisfies the point, or one falls outside a range of its
/// own value (overview D18): it is not simulated.</param>
/// <param name="Distance">How far an infeasible point is from feasibility, normalized by the ranges.</param>
public sealed record DecodedPoint(
    IReadOnlyDictionary<string, string>  Values,
    IReadOnlyDictionary<string, Complex> Quantities,
    string                               CacheKey,
    bool                                 Infeasible,
    double                               Distance);

/// <summary>
/// The optimizer's variables (R-to6-1, R-to6-12): every opt-enabled entry as one unit-box coordinate,
/// the start point, the decode of a point into tuned values, and the railed report.
/// </summary>
public sealed class OptimizationVariables
{
    /// <summary>A complex value with at least one optimized part.</summary>
    private sealed class Group
    {
        public required string Whole;
        public required string Unit;
        public required ComplexForm Form;
        public required Complex Start;
        public required ComplexRegion Region;
        public required List<int> Coordinates;
        public required List<(string Key, ComplexPart Part, double Lo, double Hi)> Entries;
    }

    private readonly List<Group> _groups;
    private readonly List<string> _valueKeys;

    private OptimizationVariables(List<OptimizationCoordinate> coords, List<Group> groups, List<string> valueKeys)
    {
        Coordinates = coords;
        _groups     = groups;
        _valueKeys  = valueKeys;
        Start       = new double[coords.Count];
    }

    public IReadOnlyList<OptimizationCoordinate> Coordinates { get; }

    /// <summary>The value keys the decode writes, in the setup's order.</summary>
    public IReadOnlyList<string> ValueKeys => _valueKeys;

    /// <summary>Whether a value key names a complex value.</summary>
    public bool IsComplex(string valueKey) => _groups.Any(g => g.Whole == valueKey);

    /// <summary>The unit of a value key's quantity.</summary>
    public string UnitOf(string valueKey)
        => _groups.FirstOrDefault(g => g.Whole == valueKey)?.Unit
           ?? Coordinates.First(c => c.ValueKey == valueKey).Unit;

    /// <summary>The start point in the box.</summary>
    public double[] Start { get; private set; }

    /// <summary>What building the variables noted: a start moved into its range, a range assumed.</summary>
    public IReadOnlyList<Diagnostic> Notes { get; private set; } = [];

    /// <summary>
    /// The opt-enabled entries of <paramref name="setup"/> as coordinates. Null with a
    /// <paramref name="refusal"/> when the run cannot start: no opt-enabled value, more than two parts
    /// of one complex value, a swept value, a complex value whose ranges cannot be reached.
    /// </summary>
    /// <param name="ladders">The preferred-value ladders a <c>discrete=preferred</c> entry snaps to
    /// (a per-user preference, passed in — <c>src/Design/CLAUDE.md</c>); null is the shipped ones.</param>
    public static OptimizationVariables? Build(TuningSetup setup, TunableCatalog catalog, out Diagnostic? refusal,
                                               PreferredLadders? ladders = null)
    {
        ladders ??= PreferredLadders.Shipped;
        refusal = null;
        var notes  = new List<Diagnostic>();
        var coords = new List<OptimizationCoordinate>();
        var groups = new List<Group>();
        var keys   = new List<string>();
        var starts = new List<double>();

        foreach (var e in setup.Variables.Where(e => e.Opt))
        {
            if (catalog.Find(e.Key) is not { } t) continue;   // resolves to nothing: a note, never an error (D3)
            if (t.DisabledReason is { } why) { refusal = OptimizationDiagnostics.VariableSwept(e.Key, why); return null; }

            // A placed vendor part: its levels are its candidates (brief vendorlib-4 R-vl4-3a).
            if (t.VendorPart is { } part)
            {
                if (part.Candidates.Count == 0 || !part.Emitted)
                {
                    refusal = OptimizationDiagnostics.PartNoCandidates(e.Key, part.Selection, part.Summary);
                    return null;
                }
                int k = part.Candidates.Count;
                var pc = new OptimizationCoordinate(e.Key, t.ValueKey, null, t.Unit, 0, k - 1, TuneScale.Lin, null, false)
                {
                    Levels = [.. Enumerable.Range(0, k).Select(i => (double)i)], VendorPart = part,
                };
                coords.Add(pc);
                keys.Add(t.ValueKey);
                int start = part.StartIndex;
                if (part.PlacedIndex < 0)
                    notes.Add(OptimizationDiagnostics.PartStartMoved(e.Key, part.PlacedPart, part.Candidates[start].Part));
                starts.Add(pc.Encode(start));
                continue;
            }

            double? min = TunableValue.InUnit(e.Min, t.Unit), max = TunableValue.InUnit(e.Max, t.Unit);
            if (min is null || max is null)
            {
                min ??= TunableValue.InUnit(t.DefaultMin, t.Unit);
                max ??= TunableValue.InUnit(t.DefaultMax, t.Unit);
                notes.Add(OptimizationDiagnostics.RangeAssumed(e.Key, TunableValue.Format(min ?? 0, t.Unit), TunableValue.Format(max ?? 1, t.Unit)));
            }
            double lo = min ?? 0, hi = max ?? 1;
            var scale = t.Part == ComplexPart.Phase ? TuneScale.Lin : TunableValue.Effective(e.Scale, lo, hi);
            if (scale == TuneScale.Log && lo <= 0) scale = TuneScale.Lin;
            // A part is continuous (overview D18, brief-tuneopt-8 R-to8-7).
            if (t.Part is not null && e.Discrete != TuneDiscrete.None)
            {
                refusal = OptimizationDiagnostics.DiscreteOnPart(e.Key, DiscreteToken(e.Discrete));
                return null;
            }
            double? step = TunableValue.InUnit(e.Step, t.Unit);
            bool integer = t.Part is null && (e.Discrete == TuneDiscrete.Integer || t.IsInteger);
            IReadOnlyList<double>? levels = null;
            // A preferred-value ladder or an E series: searched continuously, then snapped (TO-8).
            bool preferred = e.Discrete == TuneDiscrete.Preferred || TunableValue.Series.Contains(e.Discrete);
            if (preferred)
            {
                if (TunableValue.DiscreteLevels(e.Discrete, t.Unit, lo, hi, ladders) is not { } ladder)
                {
                    refusal = OptimizationDiagnostics.PreferredNoLadder(e.Key, t.Unit);
                    return null;
                }
                levels = ladder;
                if (levels.Count == 0)
                {
                    refusal = OptimizationDiagnostics.PreferredNoneInRange(e.Key,
                        TunableValue.Format(lo, t.Unit), TunableValue.Format(hi, t.Unit));
                    return null;
                }
            }
            else if (integer)
            {
                double first = Math.Ceiling(lo), last = Math.Floor(hi);
                if (last >= first && last - first < OptimizationCoordinate.MaxLevels)
                    levels = [.. Enumerable.Range(0, (int)(last - first) + 1).Select(k => first + k)];
            }
            else if (step is > 0 and var s && (hi - lo) / s < OptimizationCoordinate.MaxLevels)
                levels = [.. Enumerable.Range(0, (int)Math.Floor((hi - lo) / s + 1e-9) + 1).Select(k => lo + k * s)];

            var c = new OptimizationCoordinate(e.Key, t.ValueKey, t.Part, t.Unit, lo, hi, scale, step, integer)
            {
                Levels = levels, Preferred = preferred,
            };
            int index = coords.Count;
            coords.Add(c);

            if (t.Part is null)
            {
                keys.Add(t.ValueKey);
                double v = t.Value;
                if (v < lo || v > hi)
                {
                    double clamped = Math.Clamp(v, lo, hi);
                    notes.Add(OptimizationDiagnostics.StartMoved(e.Key, t.ValueText, c.Text(lo), c.Text(hi), c.Text(clamped)));
                    v = clamped;
                }
                starts.Add(c.Encode(v));
                continue;
            }

            var g = groups.FirstOrDefault(x => x.Whole == t.ValueKey);
            if (g is null)
            {
                g = new Group
                {
                    Whole = t.ValueKey, Unit = t.WholeUnit, Form = t.Form, Start = t.Whole,
                    Region = new ComplexRegion([]), Coordinates = [], Entries = [],
                };
                groups.Add(g);
                keys.Add(t.ValueKey);
            }
            g.Coordinates.Add(index);
            starts.Add(0);
        }

        if (coords.Count == 0) { refusal = OptimizationDiagnostics.NoVariables(); return null; }

        var vars = new OptimizationVariables(coords, groups, keys);
        for (int i = 0; i < starts.Count; i++) vars.Start[i] = starts[i];

        foreach (var g in groups)
        {
            if (g.Coordinates.Count > 2)
            {
                var names = g.Coordinates.Select(i => coords[i].Key).ToList();
                string list = string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
                refusal = OptimizationDiagnostics.TooManyComplexParts(list, names.Count == 3 ? "three" : "four", g.Whole);
                return null;
            }

            // Every entry of the value limits it, whatever its flags (D18): a range belongs to the entry.
            foreach (var e in setup.Variables)
            {
                if (!TunableKey.TryParse(e.Key, out var k) || k.Part is not { } part || k.Whole.ToString() != g.Whole) continue;
                var coord = g.Coordinates.Select(i => coords[i]).FirstOrDefault(c => c.Key == e.Key);
                string unit = part == ComplexPart.Phase ? "deg" : g.Unit;
                double lo = coord?.Min ?? TunableValue.InUnit(e.Min, unit) ?? double.NegativeInfinity;
                double hi = coord?.Max ?? TunableValue.InUnit(e.Max, unit) ?? double.PositiveInfinity;
                g.Entries.Add((e.Key, part, lo, hi));
            }
            g.Region = new ComplexRegion(g.Entries.Select(x => (x.Part, x.Lo, x.Hi)));
            if (!vars.PlaceStart(g, notes, out refusal)) return null;
        }

        vars.Notes = notes;
        return vars;
    }

    /// <summary>
    /// The start of one complex value's coordinates: the design's value when it lies inside every range,
    /// otherwise the nearest value inside them along the start's own coordinate paths — reached with
    /// <see cref="ComplexRegion.Move"/> from a feasible seed, and reported (D18).
    /// </summary>
    private bool PlaceStart(Group g, List<Diagnostic> notes, out Diagnostic? refusal)
    {
        refusal = null;
        var z0 = g.Start;
        double[] target = [.. g.Coordinates.Select(i => Part(z0, i))];
        foreach (var (i, k) in g.Coordinates.Select((i, k) => (i, k))) Start[i] = Coordinates[i].Encode(target[k]);

        var start = DecodeGroup(g, Start);
        if (!start.Infeasible) return true;

        // A feasible seed: the point of a grid over this value's coordinates nearest the start.
        const int n = 41;
        double best = double.PositiveInfinity;
        double[]? seed = null;
        var probe = (double[])Start.Clone();
        int dims = g.Coordinates.Count;
        for (int a = 0; a < n; a++)
            for (int b = 0; b < (dims == 2 ? n : 1); b++)
            {
                probe[g.Coordinates[0]] = a / (n - 1.0);
                if (dims == 2) probe[g.Coordinates[1]] = b / (n - 1.0);
                if (DecodeGroup(g, probe).Infeasible) continue;
                double d = g.Coordinates.Sum(i => (probe[i] - Start[i]) * (probe[i] - Start[i]));
                if (d < best) (best, seed) = (d, (double[])probe.Clone());
            }
        if (seed is null)
        {
            refusal = OptimizationDiagnostics.ComplexNoStart(g.Whole,
                string.Join(" and ", g.Coordinates.Select(i => Coordinates[i].Key)));
            return false;
        }

        var z = ComposeAt(g, seed)!.Value;
        for (int k = 0; k < dims; k++)
            z = g.Region.Move(z, Coordinates[g.Coordinates[k]].Part!.Value, target[k]);
        notes.Add(OptimizationDiagnostics.ComplexStartMoved(g.Whole,
            ComplexValue.Format(z0, g.Unit, g.Form), ComplexValue.Format(z, g.Unit, g.Form)));

        g.Start = z;
        foreach (int i in g.Coordinates) Start[i] = Coordinates[i].Encode(Part(z, i));
        if (DecodeGroup(g, Start).Infeasible)      // snapping can push a point off an edge it was moved to
            foreach (int i in g.Coordinates) Start[i] = seed[i];
        return true;
    }

    /// <summary>Coordinate <paramref name="i"/>'s part of <paramref name="z"/>, a phase taken nearest
    /// the middle of its range.</summary>
    private double Part(Complex z, int i)
    {
        var c = Coordinates[i];
        return ComplexValue.Get(z, c.Part!.Value, phaseNear: (c.Min + c.Max) / 2);
    }

    private Complex? ComposeAt(Group g, double[] u)
    {
        var parts = new Dictionary<ComplexPart, double>();
        foreach (int i in g.Coordinates) parts[Coordinates[i].Part!.Value] = Coordinates[i].Decode(u[i]);
        return ComplexValue.Compose(g.Start, parts);
    }

    private (Complex? Z, bool Infeasible, double Distance) DecodeGroup(Group g, double[] u)
    {
        var parts = new Dictionary<ComplexPart, double>();
        foreach (int i in g.Coordinates) parts[Coordinates[i].Part!.Value] = Coordinates[i].Decode(u[i]);
        var z = ComplexValue.Compose(g.Start, parts);
        if (z is not { } v) return (null, true, ComposeGap(g, parts));
        if (g.Region.Contains(v)) return (v, false, 0);

        double d = 0;
        foreach (var (_, part, lo, hi) in g.Entries)
        {
            double width = double.IsFinite(hi - lo) && hi > lo ? hi - lo : Math.Max(1, Math.Abs(lo) + Math.Abs(hi));
            double x = ComplexValue.Get(v, part, phaseNear: double.IsFinite(lo + hi) ? (lo + hi) / 2 : null);
            if (double.IsFinite(lo) && x < lo) d += (lo - x) / width;
            if (double.IsFinite(hi) && x > hi) d += (x - hi) / width;
        }
        return (v, true, d);
    }

    /// <summary>How far a mixed pair is from having any complex value (a real part longer than the
    /// magnitude; a phase that puts the magnitude below zero), normalized by the pair's ranges.</summary>
    private double ComposeGap(Group g, Dictionary<ComplexPart, double> parts)
    {
        var rect  = parts.FirstOrDefault(p => p.Key is ComplexPart.Real or ComplexPart.Imag);
        var polar = parts.FirstOrDefault(p => p.Key is ComplexPart.Mag or ComplexPart.Phase);
        double Width(ComplexPart p)
        {
            var c = g.Coordinates.Select(i => Coordinates[i]).First(x => x.Part == p);
            return c.Max > c.Min ? c.Max - c.Min : 1;
        }
        if (polar.Key == ComplexPart.Mag) return (Math.Abs(rect.Value) - polar.Value) / Width(ComplexPart.Mag);
        double rad = polar.Value * Math.PI / 180;
        double c = rect.Key == ComplexPart.Real ? Math.Cos(rad) : Math.Sin(rad);
        return Math.Abs(c) < 1e-12 ? Math.Abs(rect.Value) / Width(rect.Key) : Math.Abs(rect.Value / c) / Width(rect.Key);
    }

    /// <summary>The coordinates that are placed vendor parts.</summary>
    public IEnumerable<int> PartCoordinates
        => Enumerable.Range(0, Coordinates.Count).Where(i => Coordinates[i].VendorPart is not null);

    /// <summary>Every optimized value is a vendor part — a problem only Discrete searches (R-vl4-3c).</summary>
    public bool PartsOnly => Coordinates.Count > 0 && Coordinates.All(c => c.VendorPart is not null);

    /// <summary>
    /// Why a continuous-only algorithm cannot run on these variables — every optimized value is a vendor part,
    /// whose levels are real parts with nothing between them (R-vl4-5b). Null otherwise, and for Discrete and
    /// Auto, which search parts. The Optimizer's menu shows it on the greyed entry; a run refuses with it.
    /// </summary>
    public Diagnostic? ContinuousUnavailable(string algorithmId)
        => !PartsOnly || algorithmId is OptimizerAlgorithms.Auto || algorithmId == CircuitRF.Engine.Optimization.Discrete.AlgorithmId
            ? null
            : OptimizationDiagnostics.PartsOnlyDiscrete(OptimizerAlgorithms.Find(algorithmId)?.Label ?? algorithmId,
                                                        string.Join(", ", Coordinates.Select(c => c.Key)));

    /// <summary>The coordinates that take only listed values (<see cref="OptimizationCoordinate.Levels"/>).</summary>
    public IEnumerable<int> DiscreteCoordinates
        => Enumerable.Range(0, Coordinates.Count).Where(i => Coordinates[i].Levels is not null);

    /// <summary>
    /// Why the <b>Discrete</b> algorithm cannot run on these variables — null when every optimized
    /// value is an integer, a step or a preferred value (brief-tuneopt-8 R-to8-3/7). The Optimizer's
    /// menu shows it on the disabled entry; a run with <c>algorithm=discrete</c> refuses with it.
    /// </summary>
    public Diagnostic? DiscreteUnavailable()
    {
        var continuous = Coordinates.Where(c => c.Levels is null).ToList();
        if (continuous.Count == 0) return null;
        if (Coordinates.All(c => c.Part is not null)) return OptimizationDiagnostics.DiscreteOnlyParts();
        return OptimizationDiagnostics.DiscreteHasContinuous(string.Join(", ", continuous.Select(c => c.Key)));
    }

    /// <summary>The design's view of point <paramref name="u"/>.</summary>
    public DecodedPoint Decode(double[] u) => Decode(u, snapPreferred: false);

    /// <summary>The design's view of point <paramref name="u"/>, preferred values snapped when
    /// <paramref name="snapPreferred"/> (see <see cref="OptimizationCoordinate.Decode(double, bool)"/>).</summary>
    public DecodedPoint Decode(double[] u, bool snapPreferred) => Decode(u, snapPreferred, snapParts: true);

    /// <summary>
    /// The design's view of point <paramref name="u"/>. A vendor part decodes to the CELL of the candidate at its
    /// index (an evaluation rebinds the instance to it) — or, unless <paramref name="snapParts"/>, to the ideal
    /// C or L stand-in at its fractional index (R-vl4-3b).
    /// </summary>
    public DecodedPoint Decode(double[] u, bool snapPreferred, bool snapParts)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var quantities = new Dictionary<string, Complex>(StringComparer.Ordinal);
        bool infeasible = false;
        double distance = 0;

        foreach (var key in _valueKeys)
        {
            if (_groups.FirstOrDefault(g => g.Whole == key) is { } g)
            {
                var (z, bad, d) = DecodeGroup(g, u);
                infeasible |= bad;
                distance   += d;
                quantities[key] = z ?? new Complex(double.NaN, double.NaN);
                values[key] = z is { } v
                    ? ComplexValue.Format(v, g.Unit, g.Form, "G15")
                    : string.Join(" ", g.Coordinates.Select(i => $"{Coordinates[i].Key}={Coordinates[i].Text(Coordinates[i].Decode(u[i]))}"));
                continue;
            }
            int c = Index(key);
            if (Coordinates[c].VendorPart is { } part)
            {
                double f = Coordinates[c].Decode(u[c], snapPreferred, snapParts);
                if (!snapParts && part.HasProxy)
                {
                    double proxy = Coordinates[c].ProxyValue(f);
                    quantities[key] = proxy;
                    values[key] = CircuitRF.Design.VendorLibraries.VendorPartCandidates.ProxyText(proxy, part.GlyphKind!);
                }
                else
                {
                    var cand = part.Candidates[Math.Clamp((int)Math.Round(f), 0, part.Candidates.Count - 1)];
                    quantities[key] = cand.Value ?? double.NaN;
                    values[key] = cand.Cell;
                }
                continue;
            }
            double x = Coordinates[c].Decode(u[c], snapPreferred);
            quantities[key] = x;
            values[key] = Coordinates[c].Text(x);
        }
        string cacheKey = OptimizationRun.CacheKeyOf(values);
        return new DecodedPoint(values, quantities, cacheKey, infeasible, distance);
    }

    private static string DiscreteToken(TuneDiscrete d) => TunableValue.DiscreteToken(d);

    private int Index(string valueKey)
    {
        for (int i = 0; i < Coordinates.Count; i++) if (Coordinates[i].ValueKey == valueKey) return i;
        return -1;
    }

    /// <summary>
    /// The values of <paramref name="u"/> within 0.5 % of a bound (D17): each coordinate at its own
    /// bound, and each optimized part of a complex value held at the edge of ANOTHER part's range,
    /// named (D18).
    /// </summary>
    public IReadOnlyList<RailedVariable> Railed(double[] u)
    {
        const double edge = 0.005;
        var railed = new List<RailedVariable>();
        for (int i = 0; i < Coordinates.Count; i++)
        {
            if (u[i] <= edge) railed.Add(new(Coordinates[i].Key, RailEnd.Min));
            else if (u[i] >= 1 - edge) railed.Add(new(Coordinates[i].Key, RailEnd.Max));
        }
        foreach (var g in _groups)
        {
            if (DecodeGroup(g, u) is not { Z: { } z }) continue;
            foreach (var (key, part, lo, hi) in g.Entries)
            {
                if (g.Coordinates.Any(i => Coordinates[i].Key == key) || !double.IsFinite(lo) || !double.IsFinite(hi)) continue;
                double x = ComplexValue.Get(z, part, phaseNear: (lo + hi) / 2), w = edge * (hi - lo);
                RailEnd? end = x - lo <= w ? RailEnd.Min : hi - x <= w ? RailEnd.Max : null;
                if (end is { } e)
                    foreach (int i in g.Coordinates) railed.Add(new(Coordinates[i].Key, e, key));
            }
        }
        return railed;
    }
}

// brief-sar-10 R-sar10-1 / R-sar10-2 — one fields analysis's recombined field as the expression engine reads it: B1pMean,
// B1pMin, B1pCoV, psSAR, SARwb, SarEff and Pabs (Evaluator.Fields.cs), each normalised as the analysis says. docs/design/sar.md §12.
//
// CHEAP WHERE IT CAN BE. Nothing is computed until an expression asks, and each answer is kept for the rest of the evaluation:
//   - Pabs is the tissue matrix's quadratic form aᴴQa — exact, and free once the waves are known (sar.md §11.4);
//   - B1pMean/B1pMin/B1pCoV and SARwb read only the cells of their region's bounding box (FieldRecombination.RegionSums,
//     O(N × box cells)), or the summed grid when the evaluation already recombined the whole field;
//   - psSAR is ALWAYS the full average (SAR-5's engine on the whole summed field) at the mass asked — never a sample, never a
//     region's local peak standing in for it. While a tuning knob moves (FieldsOutput.Local) it is refused with a sentence
//     saying it updates on release: an average that was silently skipped would read as one that was computed.
//
// REGIONS. null: the setup's own — Sar.B1Region for B1, Sar.Region (every tissue when unstated) for SAR. "all": every tissue.
// A name matches a tissue's whole path (head/brain), its last part (brain), its first (head: every layer of that phantom) or its
// material; several join with +. "box(x0, x1, y0, y1, z0, z1)" is every cell whose centre is inside, each number with a length
// unit or none for metres: B1 over all of them (air too), SAR over the tissue ones.

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d.Sar;

namespace CircuitRF.Design.Em3d;

public sealed class CosimFieldMetrics : IDisposable
{
    private readonly FieldsAnalysis _fa;
    private readonly PortFieldSet _set;
    private readonly FieldCosimulation.CosimWaves _waves;
    private readonly FieldsOutput _output;
    private SarGrid? _grid;
    private readonly SarResult? _result;
    private short[]? _owners;
    private readonly Dictionary<string, FieldRegionSums> _regions = new(StringComparer.Ordinal);
    private readonly Dictionary<double, double> _peaks = [];
    private readonly object _gate = new();

    private static long _fullAverages, _regionReads;

    /// <summary>Full psSAR averages computed in this process — the cost a goal on psSAR pays, counted.</summary>
    public static long FullAverages => Interlocked.Read(ref _fullAverages);

    /// <summary>Region sums read from the field files in this process (the fast path), counted.</summary>
    public static long RegionReads => Interlocked.Read(ref _regionReads);

    internal CosimFieldMetrics(FieldsAnalysis fa, PortFieldSet set, string snpPath, FieldCosimulation.CosimWaves waves,
                               FieldsOutput output, SarGrid? grid, SarResult? result)
    {
        _fa = fa;
        _set = set;
        _waves = waves;
        _output = output;
        _grid = grid;
        _result = result;
        SnpPath = snpPath;
        AbsorbedAllW = FieldRecombination.QuadraticPower(set.Frequency.QMatrix(), waves.A);
    }

    public string SnpPath { get; }

    /// <summary>The power absorbed in every tissue cell at the waves as solved, W (aᴴQa).</summary>
    public double AbsorbedAllW { get; }

    /// <summary>What every value is per: the accepted or the available power, W.</summary>
    public double DivisorW => _waves.DivisorW;

    public double Evaluate(FieldMetric metric, string? region, double? massGrams)
    {
        lock (_gate)
        {
            return metric switch
            {
                FieldMetric.Pabs    => AbsorbedAllW / DivisorW,
                FieldMetric.B1pMean => B1(region).B1Mean / Math.Sqrt(DivisorW),
                FieldMetric.B1pMin  => B1(region).B1Min / Math.Sqrt(DivisorW),
                FieldMetric.B1pCoV  => B1(region).B1CoV,
                FieldMetric.SarWb   => WholeSar(region),
                FieldMetric.PsSar   => PeakAverage(massGrams),
                FieldMetric.SarEff  => B1(region).B1Mean / Math.Sqrt(DivisorW) / Math.Sqrt(PeakAverage(massGrams)),
                _ => throw new ExpressionException($"'{metric}' is not a field measurement."),
            };
        }
    }

    public void Dispose() => _set.Dispose();

    // ── the measurements ─────────────────────────────────────────────────────────────────────────────

    private FieldRegionSums B1(string? region)
    {
        var m = _set.Manifest;
        if (!m.B1 || !_set.HasH)
            throw new ExpressionException($"Fields '{_fa.Name}': its field set keeps no H, so there is no B1 — turn the setup's Sar.B1 on and " +
                                          "run the EM setup again.");
        var sums = Sums(region, b1: true);
        if (sums.B1Cells == 0) throw new ExpressionException($"Fields '{_fa.Name}': the region '{RegionText(region, b1: true)}' holds no cell.");
        return sums;
    }

    private double WholeSar(string? region)
    {
        var sums = Sums(region, b1: false);
        if (!(sums.MassKg > 0)) throw new ExpressionException($"Fields '{_fa.Name}': the region '{RegionText(region, b1: false)}' holds no tissue.");
        return sums.AbsorbedPowerW / sums.MassKg / DivisorW;
    }

    private double PeakAverage(double? massGrams)
    {
        if (massGrams is not { } g || !(g > 0) || !double.IsFinite(g))
            throw new ExpressionException($"Fields '{_fa.Name}': psSAR averages over a positive mass in grams, as 10 in psSAR({_fa.Name}, 10).");
        if (_set.Manifest.IsCylindrical)
            throw new ExpressionException($"Fields '{_fa.Name}': the field set is on a cylindrical grid, and psSAR averages over cubes, which are a " +
                                          "Cartesian grid's: there is local SAR only. Run the setup on a Cartesian grid for psSAR.");
        if (_output == FieldsOutput.Local)
            throw new ExpressionException($"Fields '{_fa.Name}': psSAR{G(g)}g is the full {G(g)} g average, which is computed when the tuning " +
                                          "knob is released, not while it moves.");
        if (_peaks.TryGetValue(g, out double known)) return known;
        double peak;
        if (_result?.Averages.FirstOrDefault(a => a.MassGrams == g) is { Peak: { } p }) peak = p.Value;
        else
        {
            var m = _set.Manifest;
            if (_grid is null)
            {
                var b0 = SarSettings.ParseB0Axis(m.B0Axis) ?? (2, 1);
                _grid = FieldRecombination.Recombine(_set, _waves.A, b1: false, b0.Item1, b0.Item2).Grid;
            }
            var method = Enum.TryParse<SarMethod>(m.Method, out var meth) ? meth : SarMethod.Cube;
            var region = m.RegionStated ? SarRegion.Mask(Mask(TissueSet(m.Region)), m.RegionName) : SarRegion.All;
            var r = SarEngine.Compute(_grid, new SarOptions { MassesGrams = [g], Method = method, Region = region });
            Interlocked.Increment(ref _fullAverages);
            peak = r.Averages[0].Peak?.Value
                   ?? throw new ExpressionException($"Fields '{_fa.Name}': no {G(g)} g cube fits in the tissue, so there is no psSAR{G(g)}g.");
        }
        return _peaks[g] = peak / DivisorW;
    }

    // ── regions ──────────────────────────────────────────────────────────────────────────────────────

    private FieldRegionSums Sums(string? region, bool b1)
    {
        string key = (b1 ? "b1|" : "sar|") + (region ?? "");
        if (_regions.TryGetValue(key, out var known)) return known;
        var m = _set.Manifest;
        var b0 = SarSettings.ParseB0Axis(m.B0Axis) ?? (2, 1);
        FieldRegionSums sums;
        if (ParseBox(region) is { } corners)
        {
            var box = BoxOf(corners);
            sums = FieldRecombination.RegionSums(_set, _waves.A, box, null, b1, b0.Item1, b0.Item2);
        }
        else
        {
            var names = region is null
                ? (b1 ? m.B1Region : m.RegionStated ? m.Region : m.Tissues)
                : region.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) || region.Trim().Length == 0 ? m.Tissues
                : Resolve(region);
            var mask = Mask(TissueSet(names));
            var box = Bounds(mask) ?? throw new ExpressionException($"Fields '{_fa.Name}': the region '{RegionText(region, b1)}' holds no cell.");
            sums = FieldRecombination.RegionSums(_set, _waves.A, box, c => mask[c], b1, b0.Item1, b0.Item2);
        }
        Interlocked.Increment(ref _regionReads);
        return _regions[key] = sums;
    }

    /// <summary>The tissues a region's words name: a whole path, its last part, its first (every layer of a phantom), or a
    /// material; joined by +. An unknown word is refused listing the tissues.</summary>
    private List<string> Resolve(string region)
    {
        var m = _set.Manifest;
        var found = new List<string>();
        foreach (string raw in region.Split('+'))
        {
            string word = raw.Trim();
            if (word.Length == 0) continue;
            var hits = new List<string>();
            for (int t = 0; t < m.Tissues.Count; t++)
            {
                string path = m.Tissues[t];
                string[] parts = path.Split('/');
                bool hit = Same(path, word) || Same(parts[^1], word) || Same(parts[0], word)
                           || t < m.TissueMaterials.Count && Same(m.TissueMaterials[t], word);
                if (hit) hits.Add(path);
            }
            if (hits.Count == 0)
                throw new ExpressionException($"Fields '{_fa.Name}': no tissue is called '{word}'. Its tissues are {string.Join(", ", m.Tissues.Select(x => $"'{x}'"))}, " +
                                              "or write \"all\" or \"box(x0, x1, y0, y1, z0, z1)\".");
            found.AddRange(hits);
        }
        if (found.Count == 0) throw new ExpressionException($"Fields '{_fa.Name}': the region '{region}' names no tissue.");
        return found;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private bool[] TissueSet(IEnumerable<string> names)
    {
        var m = _set.Manifest;
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        var keep = new bool[m.Tissues.Count];
        for (int t = 0; t < keep.Length; t++) keep[t] = wanted.Contains(m.Tissues[t]);
        return keep;
    }

    private bool[] Mask(bool[] tissues)
    {
        _owners ??= _set.ReadOwners();
        var mask = new bool[_owners.Length];
        for (int c = 0; c < mask.Length; c++) mask[c] = _owners[c] >= 0 && _owners[c] < tissues.Length && tissues[_owners[c]];
        return mask;
    }

    /// <summary>The smallest box of cells holding every cell of <paramref name="mask"/>; null when it holds none.</summary>
    private FieldBox? Bounds(bool[] mask)
    {
        int ny = _set.Y.Count, nz = _set.Z.Count;
        int i0 = int.MaxValue, i1 = -1, j0 = int.MaxValue, j1 = -1, k0 = int.MaxValue, k1 = -1;
        for (int c = 0; c < mask.Length; c++)
        {
            if (!mask[c]) continue;
            int i = c / (ny * nz), j = c / nz % ny, k = c % nz;
            i0 = Math.Min(i0, i); i1 = Math.Max(i1, i);
            j0 = Math.Min(j0, j); j1 = Math.Max(j1, j);
            k0 = Math.Min(k0, k); k1 = Math.Max(k1, k);
        }
        return i1 < 0 ? null : new FieldBox(i0, i1 + 1, j0, j1 + 1, k0, k1 + 1);
    }

    private static readonly Regex BoxText = new(@"^\s*box\s*\((.*)\)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Length = new(@"^\s*([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([A-Za-zµ]*)\s*$", RegexOptions.CultureInvariant);

    /// <summary>A box region's corners in metres, [x0, x1, y0, y1, z0, z1]; null when the region is not a box.</summary>
    private double[]? ParseBox(string? region)
    {
        if (region is null || BoxText.Match(region) is not { Success: true } mt) return null;
        // brief-sar-8b — a Cartesian box is not a block of a cylindrical grid's cells.
        if (_set.Manifest.IsCylindrical)
            throw new ExpressionException($"Fields '{_fa.Name}': the field set is on a cylindrical grid, whose cells are (ρ, α, z), so a box region " +
                                          "is not a block of them. Name a tissue, or \"all\".");
        string[] parts = mt.Groups[1].Value.Split(',');
        string how = $"Fields '{_fa.Name}': a box region is \"box(x0, x1, y0, y1, z0, z1)\", six lengths with a unit each (mm) or none for metres";
        if (parts.Length != 6) throw new ExpressionException($"{how}; '{region}' has {parts.Length}.");
        var v = new double[6];
        string metre = Units.BaseUnit("mm");
        for (int i = 0; i < 6; i++)
        {
            if (Length.Match(parts[i]) is not { Success: true } n)
                throw new ExpressionException($"{how}; '{parts[i].Trim()}' is not a length.");
            double x = double.Parse(n.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            string unit = n.Groups[2].Value;
            if (unit.Length > 0)
            {
                if (Units.Scale(unit) is not { } scale || Units.BaseUnit(unit) != metre)
                    throw new ExpressionException($"{how}; '{unit}' is not a length unit.");
                x *= scale;
            }
            v[i] = x;
        }
        return v;
    }

    /// <summary>The cells whose centres lie in the box's corners (each pair either way round).</summary>
    private FieldBox BoxOf(double[] c)
    {
        (int, int) Range(SarAxis a, double lo, double hi)
        {
            if (lo > hi) (lo, hi) = (hi, lo);
            int first = -1, last = -1;
            for (int i = 0; i < a.Count; i++)
            {
                double x = a.Centre(i);
                if (x < lo || x > hi) continue;
                if (first < 0) first = i;
                last = i;
            }
            return first < 0 ? (0, 0) : (first, last + 1);
        }
        var (i0, i1) = Range(_set.X, c[0], c[1]);
        var (j0, j1) = Range(_set.Y, c[2], c[3]);
        var (k0, k1) = Range(_set.Z, c[4], c[5]);
        if (i1 <= i0 || j1 <= j0 || k1 <= k0)
            throw new ExpressionException($"Fields '{_fa.Name}': the box holds no cell of the field set " +
                                          $"(x {G(_set.X.Faces[0] * 1e3)}…{G(_set.X.Faces[^1] * 1e3)} mm, y {G(_set.Y.Faces[0] * 1e3)}…{G(_set.Y.Faces[^1] * 1e3)} mm, " +
                                          $"z {G(_set.Z.Faces[0] * 1e3)}…{G(_set.Z.Faces[^1] * 1e3)} mm).");
        return new FieldBox(i0, i1, j0, j1, k0, k1);
    }

    private string RegionText(string? region, bool b1)
        => region ?? (b1 ? _set.Manifest.B1RegionName : _set.Manifest.RegionName);

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}

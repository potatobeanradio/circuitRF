// Data hygiene (brief-modelcomposer-3 R-mc3-5). FLAGS ONLY: nothing here excludes a point — the user
// decides, and an exclusion is a PointExclusion in the .cmc with the user's reason.
//
//   Oscillation  a point whose S, below a low-frequency limit, has |S11| or |S22| above 1, or K < 1 with
//                |Δ| > 1 — a device that oscillated while it was measured.
//   Compliance   a current at an instrument limit: the data set's stated limit, or — with none stated — a
//                flat plateau, several points sharing the set's largest current to within a part in 10⁴.
//   Drift        a bias point measured twice (within one set, or across sets of the same kind measured under
//                the same conditions) whose current or S differ by more than the tolerance — [P1]'s warning
//                that a stressed device degrades, checked by measuring a reference bias at the start and end.
//   Sign         a capacitance table spelled as the matrix whose C12 is positive at most of its points (MC-20 R-mc20-4):
//                the matrix convention has C12 = −Cgd, so the circuit set was probably meant. One flag for the set.
//   Layout       what an exported table most often gets wrong, each one flag for the set (the data is read as written —
//                these say what to check): the drain current negative where Vds > 0 (recorded flowing out), |S12| above
//                |S21| at conducting biases (gate and drain swapped), frequencies or currents or temperatures whose values
//                suggest a unit other than the one read (GHz read as Hz, mA as A, K as °C), and an "S" table whose |S11|
//                never approaches 1 (a Y table mapped as S).
//   Coverage     the convex hull of the data's (Vgs, Vds), for the plots and for extrapolation (MC-2).

using System.Globalization;
using System.Numerics;

namespace CircuitRF.Design.Modeling;

public enum HygieneKind { Oscillation, Compliance, Drift, CapacitanceSign, CurrentSign, PortOrder, Unit, TableKind }

/// <summary>One flagged point: its data set, its id, what was found, and a one-line detail. A flag about the whole set
/// (<see cref="HygieneKind.CapacitanceSign"/>) has an empty <see cref="Point"/>.</summary>
public sealed record HygieneFlag(string DataSet, string Point, HygieneKind Kind, string Detail);

/// <summary>The tolerances hygiene judges by. Every default is stated here, and nowhere else.</summary>
public sealed record HygieneSettings
{
    /// <summary>Oscillation is looked for at and below this frequency, Hz.</summary>
    public double OscillationBelowHz { get; init; } = 2e9;
    /// <summary>A current within this fraction of its stated limit is at it.</summary>
    public double ComplianceFraction { get; init; } = 1e-3;
    /// <summary>Points within this fraction of the set's largest current form a plateau…</summary>
    public double PlateauTolerance { get; init; } = 1e-4;
    /// <summary>…when there are at least this many of them.</summary>
    public int PlateauMinimumPoints { get; init; } = 3;
    /// <summary>Two points closer than this in Vgs and Vds (V) are the same bias.</summary>
    public double SameBiasVolts { get; init; } = 1e-3;
    /// <summary>A repeated point's current may differ by this fraction of the larger…</summary>
    public double DriftCurrentFraction { get; init; } = 0.02;
    /// <summary>…plus this many amps.</summary>
    public double DriftCurrentFloorA { get; init; } = 1e-6;
    /// <summary>A repeated point's S may differ by this much, max |ΔS| over the frequencies.</summary>
    public double DriftS { get; init; } = 0.02;

    public static HygieneSettings Default { get; } = new();
}

public static class DataHygiene
{
    /// <summary>Every flag over the data sets of one document, in data-set and point order.</summary>
    public static IReadOnlyList<HygieneFlag> Check(
        IReadOnlyList<(CmcDataSet Set, NormalisedData Data)> sets, HygieneSettings? settings = null)
    {
        settings ??= HygieneSettings.Default;
        var flags = new List<HygieneFlag>();
        foreach (var (set, data) in sets)
        {
            Oscillation(set, data, settings, flags);
            Compliance(set, data, settings, flags);
            CapacitanceSign(set, data, flags);
            Layout(set, data, flags);
        }
        Drift(sets, settings, flags);
        return flags;
    }

    private static void Oscillation(CmcDataSet set, NormalisedData d, HygieneSettings s, List<HygieneFlag> flags)
    {
        if (d.S is null || d.Ports != 2 || d.FrequenciesHz is null) return;
        for (int p = 0; p < d.Count; p++)
            for (int f = 0; f < d.FrequenciesHz.Length && d.FrequenciesHz[f] <= s.OscillationBelowHz; f++)
            {
                Complex s11 = d.SAt(p, f, 0, 0), s12 = d.SAt(p, f, 0, 1), s21 = d.SAt(p, f, 1, 0), s22 = d.SAt(p, f, 1, 1);
                double m11 = s11.Magnitude, m22 = s22.Magnitude;
                Complex delta = s11 * s22 - s12 * s21;
                double k = (1 - m11 * m11 - m22 * m22 + delta.Magnitude * delta.Magnitude) / (2 * (s12 * s21).Magnitude);
                string? why = m11 > 1 ? $"|S11| = {G(m11)}" : m22 > 1 ? $"|S22| = {G(m22)}"
                            : k < 1 && delta.Magnitude > 1 ? $"K = {G(k)}, |Δ| = {G(delta.Magnitude)}" : null;
                if (why is null) continue;
                flags.Add(new HygieneFlag(set.Name, d.Points[p].PointId, HygieneKind.Oscillation, $"{why} at {G(d.FrequenciesHz[f] / 1e9)} GHz."));
                break;
            }
    }

    /// <summary>The flags about whole sets, each as the one line a warning list shows: <c>set: detail</c>.</summary>
    public static IEnumerable<string> SetWarnings(IEnumerable<HygieneFlag> flags) =>
        flags.Where(f => f.Point.Length == 0).Select(f => $"{f.DataSet}: {f.Detail}");

    private static void CapacitanceSign(CmcDataSet set, NormalisedData d, List<HygieneFlag> flags)
    {
        if (d.C is not { } c || DataImport.CapacitanceTable(set) != "table") return;
        int m = c.Length / 4, n = Enumerable.Range(0, m).Count(p => c[4 * p + 1] > 0);
        if (2 * n > m)
            flags.Add(new HygieneFlag(set.Name, "", HygieneKind.CapacitanceSign,
                $"C12 > 0 at {n} of {m} points; the matrix convention has C12 = −Cgd — was the circuit set (cgd) meant?"));
    }

    private static void Layout(CmcDataSet set, NormalisedData d, List<HygieneFlag> flags)
    {
        if (set.Kind is DataSetKind.PassiveSParams or DataSetKind.LargeSignal) return;
        void Flag(HygieneKind k, string detail) => flags.Add(new HygieneFlag(set.Name, "", k, detail));
        bool arrays = ImportSettings.IsArrayFormat(set.Import.Format);
        string Fix(string q, string unit) => arrays ? $"give it its unit in the map ({q}:{unit})" : $"give its column its unit ({q}:{unit})";

        var on = d.Points.Where(p => p.Vds > 0.05 && double.IsFinite(p.Id) && Math.Abs(p.Id) > 1e-9).ToList();
        int negative = on.Count(p => p.Id < 0);
        if (on.Count >= 3 && 2 * negative > on.Count)
            Flag(HygieneKind.CurrentSign, $"Id < 0 at {negative} of {on.Count} points with Vds > 0. Currents are positive into the terminal: " +
                                          "was the drain current recorded flowing out of it?" +
                                          (arrays ? $" To read it negated, map it as -{(set.Import.Arrays?.GetValueOrDefault("id") ?? "name").TrimStart('-')}=id." : ""));

        double idMax = d.Points.Select(p => Math.Abs(p.Id)).Where(double.IsFinite).DefaultIfEmpty(0).Max();
        if (idMax > 100)
            Flag(HygieneKind.Unit, $"Id reaches {G(idMax)} A. If the currents are in mA, {Fix("id", "mA")}.");

        var temps = d.Points.Select(p => p.AmbientC).Where(double.IsFinite).ToList();
        if (temps.Count > 0 && temps.Min() > 200)
            Flag(HygieneKind.Unit, $"T runs {G(temps.Min())} to {G(temps.Max())} °C. If these are kelvin, {Fix("T", "K")}.");

        if (d.FrequenciesHz is { Length: > 0 } f && f.Max() is > 0 and < 1e3)
            Flag(HygieneKind.Unit, $"the frequencies run {G(f.Min())} to {G(f.Max())} Hz. If they are GHz, {Fix("freq", "GHz")}.");

        if (d.S is null || d.Ports != 2 || d.FrequenciesHz is not { Length: > 0 }) return;
        var conducting = Enumerable.Range(0, d.Count).Where(p => d.Points[p].Vds > 0.5 && d.Points[p].Id > 1e-6).ToList();
        int swapped = conducting.Count(p => d.SAt(p, 0, 0, 1).Magnitude > d.SAt(p, 0, 1, 0).Magnitude);
        if (conducting.Count >= 3 && 2 * swapped > conducting.Count)
            Flag(HygieneKind.PortOrder, $"|S12| > |S21| at {swapped} of {conducting.Count} conducting biases. The gate is port 1 and the drain " +
                                        "port 2: were the ports swapped, or the table transposed?");
        bool mappedS = set.Import.Arrays is { } map && (map.ContainsKey("S") || map.ContainsKey("S11"));
        if (mappedS)
        {
            double s11 = 0;
            for (int p = 0; p < d.Count; p++)
                for (int k = 0; k < d.FrequenciesHz.Length; k++) s11 = Math.Max(s11, d.SAt(p, k, 0, 0).Magnitude);
            if (s11 < 0.1)
                Flag(HygieneKind.TableKind, $"|S11| never exceeds {G(s11)}; a FET's gate reflects nearly everything at low frequency. " +
                                            "Is this table Y, in siemens? Then map it as Y.");
        }
    }

    private static void Compliance(CmcDataSet set, NormalisedData d, HygieneSettings s, List<HygieneFlag> flags)
    {
        var limits = set.Conditions.Compliance;
        foreach (var (name, get, limit) in new (string, Func<BiasPoint, double>, double?)[]
                 { ("Ig", p => p.Ig, limits?.Gate), ("Id", p => p.Id, limits?.Drain) })
        {
            var at = new List<int>();
            if (limit is { } l)
            {
                for (int p = 0; p < d.Count; p++)
                    if (Math.Abs(get(d.Points[p])) >= l * (1 - s.ComplianceFraction)) at.Add(p);
                foreach (int p in at)
                    flags.Add(new HygieneFlag(set.Name, d.Points[p].PointId, HygieneKind.Compliance, $"{name} is at its {G(l)} A limit."));
                continue;
            }
            double max = d.Points.Select(p => Math.Abs(get(p))).Where(double.IsFinite).DefaultIfEmpty(0).Max();
            if (!(max > 0)) continue;
            for (int p = 0; p < d.Count; p++)
                if (Math.Abs(get(d.Points[p])) >= max * (1 - s.PlateauTolerance)) at.Add(p);
            if (at.Count < s.PlateauMinimumPoints) continue;
            foreach (int p in at)
                flags.Add(new HygieneFlag(set.Name, d.Points[p].PointId, HygieneKind.Compliance,
                    $"{name} sits on a {G(max)} A plateau shared by {at.Count} points."));
        }
    }

    private static void Drift(IReadOnlyList<(CmcDataSet Set, NormalisedData Data)> sets, HygieneSettings s, List<HygieneFlag> flags)
    {
        // Every point in order; each is compared with the earlier points it repeats.
        var seen = new List<(int Set, int Point)>();
        for (int a = 0; a < sets.Count; a++)
            for (int p = 0; p < sets[a].Data.Count; p++)
            {
                var pt = sets[a].Data.Points[p];
                if (!double.IsFinite(pt.Vgs) || !double.IsFinite(pt.Vds)) continue;
                foreach (var (b, q) in seen)
                {
                    if (!Comparable(sets[a].Set, sets[b].Set)) continue;
                    var earlier = sets[b].Data.Points[q];
                    if (Math.Abs(earlier.Vgs - pt.Vgs) > s.SameBiasVolts || Math.Abs(earlier.Vds - pt.Vds) > s.SameBiasVolts) continue;
                    if (double.IsFinite(pt.AmbientC) && double.IsFinite(earlier.AmbientC) && pt.AmbientC != earlier.AmbientC) continue;
                    if (DriftDetail(sets[b].Data, q, earlier, sets[a].Data, p, pt, s) is { } detail)
                    {
                        string where = a == b ? earlier.PointId : $"{earlier.PointId} in {sets[b].Set.Name}";
                        flags.Add(new HygieneFlag(sets[a].Set.Name, pt.PointId, HygieneKind.Drift, $"{detail} against {where}."));
                        break;
                    }
                }
                seen.Add((a, p));
            }
    }

    private static bool Comparable(CmcDataSet x, CmcDataSet y) =>
        ReferenceEquals(x, y) || x.Kind == y.Kind && x.Conditions with { Note = null } == y.Conditions with { Note = null };

    private static string? DriftDetail(NormalisedData da, int pa, BiasPoint a, NormalisedData db, int pb, BiasPoint b, HygieneSettings s)
    {
        foreach (var (name, x, y) in new[] { ("Id", a.Id, b.Id), ("Ig", a.Ig, b.Ig) })
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            double diff = Math.Abs(x - y);
            if (diff > s.DriftCurrentFraction * Math.Max(Math.Abs(x), Math.Abs(y)) + s.DriftCurrentFloorA)
                return $"{name} differs by {G(diff)} A";
        }
        if (da.S is not null && db.S is not null && da.Ports == db.Ports && da.FrequenciesHz!.SequenceEqual(db.FrequenciesHz!))
        {
            double worst = 0;
            for (int f = 0; f < da.FrequenciesHz!.Length; f++)
                for (int i = 0; i < da.Ports; i++)
                    for (int j = 0; j < da.Ports; j++)
                        worst = Math.Max(worst, (da.SAt(pa, f, i, j) - db.SAt(pb, f, i, j)).Magnitude);
            if (worst > s.DriftS) return $"S differs by {G(worst)}";
        }
        return null;
    }

    /// <summary>
    /// The convex hull of the points' (Vgs, Vds), counter-clockwise from the lowest Vgs — the data domain's
    /// boundary. Excluded points and points without a bias are left out. Fewer than three distinct points give
    /// what there is.
    /// </summary>
    public static IReadOnlyList<(double Vgs, double Vds)> Coverage(IEnumerable<NormalisedData> sets)
    {
        var pts = sets.SelectMany(d => d.Points)
                      .Where(p => !p.Excluded && double.IsFinite(p.Vgs) && double.IsFinite(p.Vds))
                      .Select(p => (p.Vgs, p.Vds)).Distinct()
                      .OrderBy(p => p.Vgs).ThenBy(p => p.Vds).ToList();
        if (pts.Count < 3) return pts;

        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        var hull = new List<(double Vgs, double Vds)>();
        foreach (var p in pts)                                   // lower chain
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)                 // upper chain
        {
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], pts[i]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(pts[i]);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}

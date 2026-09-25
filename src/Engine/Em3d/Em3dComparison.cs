// brief-em3d-10 R-em3d10-2/3 — two solvers' S on one problem, and what the difference means
// (em-3d.md §4.4, §12).
//
// Pure functions, in the numeric layer so the arithmetic is tested with nothing installed. The run
// (src/Design/Em3d/Em3dRunService) hands in the two S cubes it built and the facts the openEMS
// lowering reported; this returns a DataSet and the sentences that go beside it.
//
// SAME GRID OR NO COMPARISON (R-em3d10-2a). The two frequency vectors must be EQUAL, element for
// element. Nothing here interpolates one onto the other: interpolation error would be reported as
// solver disagreement, which is exactly the misreading §12 lists as a risk.
//
// THE NOTES (R-em3d10-3) each come from a fact about THIS run — a lossy dielectric, a solid written
// as PEC, an unconverged openEMS run. A note that applies to every comparison says nothing, so the
// only unconditional ones are the cross-check sentence (§4.4's first limit), the reference planes
// (§4.4's second) and the lumped ports, which every Tier A problem has.
//
// WHERE THE NOTES LIVE IN THE FILE. A DataSet has no free-text field and the .npy writer none either,
// so the notes are a real cube over an axis whose LABELS are the sentences — the one string an axis
// already carries through the writer and back through the reader. Its values are the severity
// (0 a note, 1 a warning).

using System.Globalization;
using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Engine.Em3d;

/// <summary>What the openEMS lowering and runs reported that makes a difference expected (brief 9
/// §2c, §2d, §3d). Plain values, so the numeric layer needs no Design type.</summary>
/// <param name="DielectricFitHz">The frequency openEMS's dielectric loss is exact at.</param>
/// <param name="PecSolids">Solid conductors openEMS wrote as perfect conductors.</param>
/// <param name="SubCellWires">Wires thinner than their FDTD cell, written as thin conductors.</param>
/// <param name="UnconvergedPorts">Ports whose openEMS run stopped before its end criterion.</param>
public sealed record Em3dComparisonFacts(
    double                DielectricFitHz,
    IReadOnlyList<string> PecSolids,
    IReadOnlyList<string> SubCellWires,
    IReadOnlyList<int>    UnconvergedPorts);

/// <summary>The comparison, or why there is none.</summary>
/// <param name="Data">The comparison DataSet; null when refused.</param>
/// <param name="Refusal">Why no comparison was made; null when it was.</param>
/// <param name="Summary">R-em3d10-3c's line — the one a user reads first.</param>
/// <param name="Notes">Everything else the comparison says, in the order it says it.</param>
/// <param name="Warnings">What must be fixed before reading any difference.</param>
public sealed record Em3dComparisonResult(
    DataSet?              Data,
    string?               Refusal,
    string?               Summary,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings);

public static class Em3dComparison
{
    public const string PalaceCube  = "S_palace";
    public const string OpenEmsCube = "S_openems";
    public const string DMagCube    = "dMag_dB";
    public const string DPhaseCube  = "dPhase_deg";
    public const string DVecCube    = "dVec";

    /// <summary>The group holding what the comparison says and the reference planes it assumed.</summary>
    public const string Group = "compare";

    /// <summary>The notes cube: one element per sentence, the sentence as the axis label, the value
    /// its severity (0 a note, 1 a warning).</summary>
    public const string NotesCube = "Notes";

    /// <summary>Each port's reference-plane shift, metres — zero for every Tier A lumped port.</summary>
    public const string ReferencePlaneCube = "ReferencePlaneShift";

    /// <summary>R-em3d10-1b's one sentence. Nothing may call agreement "validated".</summary>
    public const string CrossCheckSentence =
        "This comparison is a cross-check, not a reference: both solvers read the same generated 3D problem, " +
        "so an error in generating it appears in both results and their agreement cannot reveal it (em-3d.md §4.4).";

    /// <summary>
    /// The whole comparison: the difference cubes (<see cref="Differences"/>) and the sentences
    /// (R-em3d10-3), or the refusal when the two grids are not the same.
    /// </summary>
    public static Em3dComparisonResult Compare(DataCube palace, DataCube openEms, Em3dProblem problem, Em3dComparisonFacts facts)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(facts);
        var (data, refusal) = Differences(palace, openEms);
        if (data is null) return new Em3dComparisonResult(null, refusal, null, [], []);

        string summary = Summary(data);
        var warnings = Warnings(facts);
        var notes = new List<string> { CrossCheckSentence, ReferencePlanes(problem) };
        notes.AddRange(Expectations(problem));
        notes.AddRange(SystematicDifferences(problem, facts));

        // Carried in the file (R-em3d10-3): the summary first, as it is read first, then the warnings.
        var said = new List<(string Text, double Severity)> { (summary, 0) };
        said.AddRange(warnings.Select(w => (w, 1.0)));
        said.AddRange(notes.Select(n => (n, 0.0)));
        data.AddToGroup(Group, NotesCube, new DataCube(
            [new Axis("note", [.. Enumerable.Range(0, said.Count).Select(k => (double)k)], "", [.. said.Select(s => s.Text)])],
            [.. said.Select(s => s.Severity)]));

        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        data.AddToGroup(Group, ReferencePlaneCube, new DataCube(
            [new Axis("port", [.. ports.Select(p => (double)p.Number)], "port")],
            [.. ports.Select(p => p.ReferencePlane.ShiftM)]) { Unit = "m" });

        return new Em3dComparisonResult(data, null, summary, notes, warnings);
    }

    /// <summary>
    /// R-em3d10-2a/b — the five cubes, each [freq, i, j] as the S cube is: the two S matrices as they
    /// came, the magnitude difference in dB, the phase difference wrapped to (−180°, 180°], and
    /// |S_palace − S_openems|, which stays meaningful where |S| is small and a dB difference explodes.
    /// Refused, with a sentence, when the two frequency vectors or port counts are not identical.
    /// </summary>
    public static (DataSet? Data, string? Refusal) Differences(DataCube palace, DataCube openEms)
    {
        ArgumentNullException.ThrowIfNull(palace);
        ArgumentNullException.ThrowIfNull(openEms);
        if (palace.Axes.Count != 3 || openEms.Axes.Count != 3 ||
            palace.DataKind != DataKind.Complex || openEms.DataKind != DataKind.Complex)
            return (null, "Both results must be complex S matrices over frequency, and one of them is not.");

        int np = palace.Axes[1].Length, no = openEms.Axes[1].Length;
        if (np != no || palace.Axes[2].Length != np || openEms.Axes[2].Length != no)
            return (null, $"Palace's result has {np} port(s) and openEMS's {no}, so there is nothing to compare port for port.");

        double[] fp = palace.Axes[0].Values, fo = openEms.Axes[0].Values;
        if (fp.Length != fo.Length)
            return (null, $"Palace's result has {fp.Length} frequencies and openEMS's {fo.Length}. The comparison needs the " +
                          "same frequencies on both sides and does not interpolate, since interpolation error would read " +
                          "as a difference between the solvers.");
        for (int k = 0; k < fp.Length; k++)
            if (fp[k] != fo[k])
                return (null, $"The two results' frequency {k + 1} differs: Palace's is {fp[k].ToString("R", CultureInfo.InvariantCulture)} Hz " +
                              $"and openEMS's {fo[k].ToString("R", CultureInfo.InvariantCulture)} Hz. The comparison needs the same " +
                              "frequencies on both sides and does not interpolate, since interpolation error would read as a " +
                              "difference between the solvers.");

        Complex[] sp = palace.ComplexValues, so = openEms.ComplexValues;
        var mag   = new double[sp.Length];
        var phase = new double[sp.Length];
        var vec   = new double[sp.Length];
        for (int k = 0; k < sp.Length; k++)
        {
            mag[k]   = Db(sp[k]) - Db(so[k]);
            phase[k] = PhaseDifferenceDeg(sp[k], so[k]);
            vec[k]   = (sp[k] - so[k]).Magnitude;
        }

        var axes = new[] { new Axis("freq", fp, "Hz"), palace.Axes[1], palace.Axes[2] };
        var ds = new DataSet();
        ds.Add(PalaceCube,  new DataCube(axes, sp));
        ds.Add(OpenEmsCube, new DataCube(axes, so));
        ds.Add(DMagCube,    new DataCube(axes, mag)   { Unit = "dB" });
        ds.Add(DPhaseCube,  new DataCube(axes, phase) { Unit = "deg" });
        ds.Add(DVecCube,    new DataCube(axes, vec));
        return (ds, null);
    }

    /// <summary>arg(a) − arg(b) in degrees, wrapped to (−180, 180].</summary>
    public static double PhaseDifferenceDeg(Complex a, Complex b)
    {
        double d = (a.Phase - b.Phase) * 180 / Math.PI;
        d %= 360;                       // (−360, 360)
        if (d > 180) d -= 360;
        else if (d <= -180) d += 360;
        return d;
    }

    /// <summary>
    /// R-em3d10-3c — the largest |dMag_dB| and |dPhase| over the band, and where, for the first
    /// transmission term (S21) and for S11; a one-port has only S11.
    /// </summary>
    public static string Summary(DataSet data)
    {
        var mag = data[DMagCube];
        var phase = data[DPhaseCube];
        var vec = data[DVecCube];
        int n = mag.Axes[1].Length;
        double[] f = mag.Axes[0].Values;

        string Term(int i, int j)
        {
            var (dm, fm) = Largest(mag.RealValues, i, j);
            var (dp, fpk) = Largest(phase.RealValues, i, j);
            var (dv, fv) = Largest(vec.RealValues, i, j);
            return $"S{i + 1}{j + 1} by at most {F(dm)} dB (at {Ghz(fm)}) and {F(dp)}° (at {Ghz(fpk)}), " +
                   $"|ΔS| at most {F(dv)} (at {Ghz(fv)})";
        }

        (double Value, double AtHz) Largest(double[] v, int i, int j)
        {
            double best = double.NaN, at = double.NaN;
            for (int k = 0; k < f.Length; k++)
            {
                double x = Math.Abs(v[k * n * n + i * n + j]);
                if (double.IsNaN(x)) continue;
                if (double.IsNaN(best) || x > best) { best = x; at = f[k]; }
            }
            return (best, at);
        }

        return n >= 2
            ? $"Palace against openEMS: {Term(1, 0)}; {Term(0, 0)}."
            : $"Palace against openEMS: {Term(0, 0)}.";
    }

    // ── the sentences ───────────────────────────────────────────────────────────────────────────

    private static string ReferencePlanes(Em3dProblem problem)
    {
        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        var shifted = ports.Where(p => p.ReferencePlane.ShiftM != 0).ToList();
        return shifted.Count == 0
            ? $"Both results are referred to the same reference planes: each port's own sheet, with no shift ({(ports.Count == 1 ? "a lumped port" : $"{ports.Count} lumped ports")}). " +
              "A comparison is only fair when both are referred to the same plane (em-3d.md §4.4)."
            : "Both results are referred to the same reference planes: " +
              string.Join(", ", ports.Select(p => $"port {p.Number} shifted {F(p.ReferencePlane.ShiftM * 1e6)} µm")) +
              " (em-3d.md §4.4).";
    }

    /// <summary>R-em3d10-3a — brief 5's guidance function, as it stands, and which solver the
    /// accuracy rows (4, 7) expect to be closer here.</summary>
    private static IEnumerable<string> Expectations(Em3dProblem problem)
    {
        var guidance = Em3dSolverGuidance.For(problem);
        foreach (var g in guidance) yield return "Expected for this geometry: " + g.Sentence;

        var accuracy = guidance.Where(g => g.Row is 4 or 7).ToList();
        if (accuracy.Count > 0)
            yield return $"{Describe(accuracy)}: FEM (Palace) is expected to be the more accurate here.";
        else if (guidance.Any(g => g.Row == 1))
            yield return "Manhattan geometry: FDTD's staircasing costs nothing on it, so the geometry alone gives neither " +
                         "solver the accuracy advantage here.";
    }

    private static string Describe(List<Em3dGuidance> rows)
        => string.Join(" and ", rows.Select(g => g.Row == 4 ? "bond wires, round or diagonal metal present" : "fine features in a large volume"));

    /// <summary>R-em3d10-3b — each known systematic difference that applies to THIS run.</summary>
    private static IEnumerable<string> SystematicDifferences(Em3dProblem problem, Em3dComparisonFacts facts)
    {
        var lossy = LossyDielectrics(problem);
        if (lossy.Count > 0)
            yield return $"{Join(lossy)} {(lossy.Count == 1 ? "is" : "are")} lossy. openEMS fits a dielectric's loss so that " +
                         $"tanδ is exact at {Ghz(facts.DielectricFitHz)} only, where Palace holds tanδ constant across the band, so " +
                         $"the expected |S21| difference from this grows with distance from {Ghz(facts.DielectricFitHz)}.";

        if (facts.PecSolids.Count > 0)
            yield return $"openEMS writes solid conductors as perfect conductors, where Palace gives them their conductivity: " +
                         $"{Join(facts.PecSolids)}. openEMS's result carries none of their loss.";

        if (facts.SubCellWires.Count > 0)
            yield return $"{Join(facts.SubCellWires)} {(facts.SubCellWires.Count == 1 ? "is" : "are")} thinner than openEMS's grid " +
                         "cell and modelled there as a thin conductor, whose effective radius the cell sets rather than the wire; " +
                         "Palace meshes the wire's own surface.";

        yield return "Both solvers use lumped ports, so their port parasitics are similar but not identical, which is part " +
                     "of any difference in the ports' reflections.";
    }

    private static IReadOnlyList<string> Warnings(Em3dComparisonFacts facts)
        => facts.UnconvergedPorts.Count == 0 ? [] :
        [
            $"openEMS's run{(facts.UnconvergedPorts.Count == 1 ? "" : "s")} exciting port{(facts.UnconvergedPorts.Count == 1 ? "" : "s")} " +
            $"{string.Join(", ", facts.UnconvergedPorts)} did not reach {(facts.UnconvergedPorts.Count == 1 ? "its" : "their")} end " +
            "criterion. Fix this first — raise the openEMS section's MaxTimeSteps — before reading any difference: " +
            "openEMS's result is missing the energy still in the structure.",
        ];

    /// <summary>Dielectric materials with tanδ above zero that some dielectric solid is made of.</summary>
    public static IReadOnlyList<string> LossyDielectrics(Em3dProblem problem)
    {
        var lossy = problem.Materials.Where(m => m.TanD > 0).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        return [.. problem.Solids.Where(s => s.Role == Em3dRole.Dielectric && lossy.Contains(s.Material))
                                 .Select(s => s.Material).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static double Db(Complex s) => 20 * Math.Log10(s.Magnitude);

    private static string Join(IReadOnlyList<string> names)
    {
        var q = names.Select(n => $"'{n}'").ToList();
        return q.Count == 1 ? q[0] : string.Join(", ", q.Take(q.Count - 1)) + " and " + q[^1];
    }

    private static string F(double v) => v.ToString("G3", CultureInfo.InvariantCulture);

    private static string Ghz(double hz) => (hz / 1e9).ToString("G4", CultureInfo.InvariantCulture) + " GHz";
}

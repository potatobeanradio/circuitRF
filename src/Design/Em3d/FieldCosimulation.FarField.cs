// brief-cosim-pattern-3 R-cp3-2 / R-cp3-3 / R-cp3-7 — a schematic's fields analysis over a result's far field. The stored pattern of port k,
// E^V_k, is the field with 1 V across port k and every other port terminated in its Z0, so with the circuit's waves at the block
//
//     E = Σ_k  a_k · √Z0_k · (1 + S_kk) · E^V_k
//
// and E over the circuit's drive-port voltage is the pattern of the whole circuit at 1 V there, which is what the cubes of a 3D run mean.
// Everything after the sum is Em3dRadiation.Publish, so a metric means what it means for a 3D run: gain is per watt accepted and realized
// gain includes mismatch. The waves are Weigh's and only their ratios are read, so its power scale cancels against the drive's voltage.

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d.Sar;
using CircuitRF.Engine.Mom;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

public static partial class FieldCosimulation
{
    /// <summary>Below this |1 + S_kk| a port's voltage in its own run is under 1 % of its incident wave's, so its stored pattern, which was divided
    /// by that voltage, has lost precision and the run says so.</summary>
    public const double SmallPortVoltage = 0.01;

    /// <summary>R-cp3-3 — the far-field half of a fields analysis at the frequencies given: the cubes, into <paramref name="data"/> as
    /// <c>farfield.U</c> and so on in its default group (R-cp3-5), with the waves unless a field set's run already published them. An
    /// <see cref="FieldsOutput.OnDemand"/> evaluation stops at the waves (R-cp4-1), and a metric sums the pattern when it reads it.
    /// Returns what the measurements read, or null at <c>freq=all</c>, where a scalar function has no one frequency to answer at (R-cp4-4).</summary>
    private static CosimPatternMetrics? PublishFarField(FieldsAnalysis fa, ElaboratedNetlist nl, PortFarFieldSet far, IReadOnlyList<int> at,
                                                        IReadOnlyList<(int Port, Complex Amplitude)> drives, DataSet data,
                                                        List<string> lines, List<string> warnings, RunControl? control, FieldsOutput output)
    {
        int n = far.PortCount;
        Complex[] z0 = [.. Enumerable.Repeat(far.Z0, n)];
        var freqs = new double[at.Count];
        var waves = new CosimWaves[at.Count];
        for (int f = 0; f < at.Count; f++)
        {
            control?.ThrowIfCancellationRequested();
            freqs[f] = far.FrequenciesHz[at[f]];
            waves[f] = Weigh(fa, z0, SParameterEngine.BlockWaves(nl, freqs[f], fa.Block, drives));
        }
        string where = at.Count == 1 ? MaterialDispersion.Hz(freqs[0]) : $"{at.Count} frequencies, {MaterialDispersion.Hz(freqs[0])} to {MaterialDispersion.Hz(freqs[^1])}";
        CosimPatternMetrics? Metrics(Em3dPatternPoint? point) => fa.AtEveryFrequency ? null : new CosimPatternMetrics(fa, far, at[0], waves[0], point);

        if (output == FieldsOutput.OnDemand)
        {
            AddWaves(data, freqs, waves, far.PortNumbers);
            lines.Add($"Fields '{fa.Name}' at {where}: the circuit's waves at '{fa.Block}', its pattern summed only where a measurement reads it.");
            return Metrics(null);
        }

        var points = new Em3dPatternPoint[at.Count];
        var checks = new List<string>();
        for (int f = 0; f < at.Count; f++)
        {
            control?.ThrowIfCancellationRequested();
            points[f] = PointOf(fa, far, at[f], waves[f], out double perVolt);
            FarFieldChecks(fa, far, at[f], waves[f], points[f].Pattern.RadiatedPowerW, perVolt, checks, warnings);
        }

        var published = new DataSet();
        var sentences = Em3dRadiation.Publish(published, far.Solver, far.Grid, [drives[0].Port], freqs, points, far.ReferenceInputPowerDbm, far.Hemisphere);
        foreach (string group in published.Groups)
            foreach (var (name, cube) in published.CubesIn(group))
                data.Add($"{group}.{name}", cube);
        AddWaves(data, freqs, waves, far.PortNumbers);

        var first = waves[0];
        string drivenBy = first.Multi
            ? $"driven from circuit ports {string.Join(", ", first.Drive.Drives.Select(d => d.Port))} together ({Pct(first.Accepts)} of their available power accepted) and referred to 1 V at port {drives[0].Port}"
            : $"driven from circuit port {drives[0].Port} (|Γ| {Db(first.Reflection.Magnitude)} dB, {Pct(first.Accepts)} of the available power accepted) and referred to 1 V there";
        lines.Add($"Fields '{fa.Name}' at {where}: the pattern of '{fa.Block}' with this circuit around it, recombined from its {n} ports' stored patterns, {drivenBy}. " +
                  "Gain is per watt accepted and realized gain includes mismatch, as for a 3D run.");
        // The first of Publish's sentences describes a run that excited each port; the rest (refusals, polarization) are about the cubes.
        lines.AddRange(sentences.Skip(1));
        lines.AddRange(checks);
        return Metrics(points[0]);
    }

    /// <summary>Overview §3 at one frequency: the sum over the block's ports, referred to 1 V at the circuit's first drive.</summary>
    internal static Em3dPatternPoint PointOf(FieldsAnalysis fa, PortFarFieldSet far, int i, CosimWaves w, out double perVolt)
    {
        int n = far.PortCount;
        double hz = far.FrequenciesHz[i];
        Complex root = RfCore.RFNetwork.ComplexSqrt(far.Z0);
        var s = far.S[i];
        var eTheta = new Complex[far.Grid.DirectionCount];
        var ePhi = new Complex[far.Grid.DirectionCount];
        for (int k = 0; k < n; k++) far.AddWeighted(i, k, w.A[k] * root * (1 + s[k, k]), eTheta, ePhi);

        Complex v = DriveVoltage(w);
        if (!(v.Magnitude > 0))
            throw new InvalidOperationException($"Fields '{fa.Name}': circuit port {w.Drive.DrivenPort}'s voltage is zero at {MaterialDispersion.Hz(hz)}, so the pattern " +
                                                "has nothing to be referred to. Drive the circuit from a port that is not shorted.");
        for (int d = 0; d < eTheta.Length; d++)
        {
            eTheta[d] /= v;
            ePhi[d] /= v;
        }

        // ½ because these are peak voltages, as a 3D run's ½·Re(V·I*) at 1 V is; |a|² is the wave's power in √W only as a ratio to the drive's.
        perVolt = 0.5 / (v.Magnitude * v.Magnitude);
        return new Em3dPatternPoint(FarFieldStage.Pattern(far.Grid, eTheta, ePhi, w.Drive.DrivenPort, hz), w.AcceptedW * perVolt, w.Reflection);
    }

    /// <summary>R-cp3-7 — the checks, each a note, and a warning when it fails.</summary>
    private static void FarFieldChecks(FieldsAnalysis fa, PortFarFieldSet far, int i, CosimWaves w, double radiatedPerVolt, double perVolt,
                                       List<string> notes, List<string> warnings)
    {
        double hz = far.FrequenciesHz[i];
        string at = MaterialDispersion.Hz(hz);
        int n = far.PortCount;
        var s = far.S[i];
        double pDel = Enumerable.Range(0, n).Sum(k => w.A[k].Magnitude * w.A[k].Magnitude - w.B[k].Magnitude * w.B[k].Magnitude);
        double pDelS = FieldRecombination.QuadraticPower(Dissipation(s), w.A);
        double scale = Math.Max(Math.Abs(pDel), double.Epsilon);
        double apart = Math.Abs(pDel - pDelS) / scale;
        notes.Add($"Check at {at}: the circuit delivers {G(pDel)} W into the block and the result's S-matrix says {G(pDelS)} W ({G(apart * 100)} % apart).");
        if (apart > PowerTolerance)
            warnings.Add($"Fields '{fa.Name}' at {at}: the power the circuit delivers into the block differs from its result's S-matrix by {G(apart * 100)} %: the .sNp the block " +
                         "reads is not the run's.");

        // The block's own balance, and not the drive's: a lossy part in the circuit lowers what reaches the block, never what the block radiates of it.
        double delivered = pDel * perVolt;
        if (delivered > 0)
        {
            double ratio = radiatedPerVolt / delivered;
            notes.Add($"Check at {at}: the pattern radiates {Pct(ratio)} of the power delivered into the block.");
            if (ratio > 1 + Em3dRadiation.EfficiencyTolerance)
                warnings.Add($"Fields '{fa.Name}' at {at}: the pattern radiates {Pct(ratio)} of the power delivered into the block, past the {Pct(1 + Em3dRadiation.EfficiencyTolerance)} " +
                             "a 3D run's own balance allows. A pattern is only as good as the field its absorbing faces leave unreflected: move the air box further out, or refine the mesh, and run it again.");
        }

        for (int k = 0; k < n; k++)
        {
            double small = (1 + s[k, k]).Magnitude;
            if (small < SmallPortVoltage)
                notes.Add($"Fields '{fa.Name}': port {far.PortNumbers[k]}'s |1 + S| is {G(small)} at {at}: it is nearly a short in its own run, so its stored pattern, divided by that " +
                          "small voltage, has lost precision. Where this port carries the result, check it with a finer run.");
        }
    }

    /// <summary>The circuit's first drive's total voltage on the scale of <see cref="CosimWaves.A"/>: the same scale cancels in the ratio.</summary>
    private static Complex DriveVoltage(CosimWaves w)
    {
        var first = w.Drive.Drives[0];
        double incident = w.Drive.Drives.Sum(d => d.Incident.Magnitude * d.Incident.Magnitude);
        double root = Math.Sqrt(w.AvailableW / incident);
        return (Complex.Conjugate(first.Z0) * first.Incident + first.Z0 * first.Reflected) / Math.Sqrt(first.Z0.Real) * root;
    }

    /// <summary>The waves' cubes of <see cref="DataSetOf(double, CosimWaves, PortFieldManifest, double)"/> over every frequency, all per watt; a field set's
    /// run has already published them when it ran at this one.</summary>
    private static void AddWaves(DataSet data, double[] freqs, CosimWaves[] waves, IReadOnlyList<int> ports)
    {
        if (data.Cubes.ContainsKey(IncidentCube)) return;
        int nf = freqs.Length, n = ports.Count;
        var f = new Axis("f", (double[])freqs.Clone(), "Hz");
        var port = new Axis("Port", [.. ports.Select(p => (double)p)], "");
        var a = new Complex[nf * n];
        var b = new Complex[nf * n];
        var accepted = new double[nf];
        var delivered = new double[nf];
        var gamma = new Complex[nf];
        for (int i = 0; i < nf; i++)
        {
            var w = waves[i];
            double scale = Math.Sqrt(w.DivisorW), sum = 0;
            for (int k = 0; k < n; k++)
            {
                a[i * n + k] = w.A[k] / scale;
                b[i * n + k] = w.B[k] / scale;
                sum += w.A[k].Magnitude * w.A[k].Magnitude - w.B[k].Magnitude * w.B[k].Magnitude;
            }
            accepted[i] = w.AcceptedW / w.DivisorW;
            delivered[i] = sum / w.DivisorW;
            gamma[i] = w.Reflection;
        }
        data.Add(OpenEmsSarResults.AcceptedCube, new DataCube([f], accepted) { Unit = "W" });
        data.Add(DeliveredCube, new DataCube([f], delivered) { Unit = "W" });
        data.Add(ReflectionCube, new DataCube([f], gamma));
        data.Add(IncidentCube, new DataCube([f, port], a) { Unit = "sqrt(W)" });
        data.Add(ReflectedCube, new DataCube([f, port], b) { Unit = "sqrt(W)" });
    }

    /// <summary>R-cp3-6 — the circuit's S and Z0 over its circuit ports at the analysis's own frequencies, from the S-parameter run itself, so
    /// <c>F1.S</c> and <c>SP1.S</c> are one quantity in one shape. The drive does not change S.</summary>
    private static void AddCircuitS(DataSet data, ElaboratedNetlist nl, double[] freqs)
    {
        var run = SParameterEngine.Run(nl, freqs);
        foreach (string name in (string[])["S", "Z0"])
            if (run.Cubes.TryGetValue(name, out var cube)) data.Add(name, cube);
    }

    /// <summary>What one analysis's field measurements read: its field set's and its far field's, each with the sentence for the half it does not have.</summary>
    private sealed class FieldsRunMetrics(FieldsAnalysis fa, string record, CosimFieldMetrics? set, CosimPatternMetrics? pattern) : IFieldMetrics, IDisposable
    {
        public double Evaluate(FieldMetric metric, string? region, double? massGrams)
            => set is not null
                ? set.Evaluate(metric, region, massGrams)
                : throw new ExpressionException($"'{FieldMetricFunctions.NameOf(metric)}' reads a field set, and fields analysis '{fa.Name}' has none at its frequency: '{record}' is a far field there. " +
                                                "Turn on the setup's Keep per-port fields and run it again.");

        public double EvaluatePattern(FieldMetric metric, double? thetaDeg, double? phiDeg)
        {
            if (pattern is not null) return pattern.Evaluate(metric, thetaDeg, phiDeg);
            if (fa.AtEveryFrequency)
                throw new ExpressionException($"{metric}({fa.Name}) is one number, and fields analysis '{fa.Name}' has freq=all, which is every frequency '{record}' holds. " +
                                              "Read it from a fields analysis at one of those frequencies.");
            throw new ExpressionException($"'{metric}' reads a far field, and fields analysis '{fa.Name}' has none at its frequency: '{record}' holds a field set there. " +
                                          "Turn on the setup's Radiation pattern and run it again.");
        }

        public void Dispose() => set?.Dispose();
    }
}

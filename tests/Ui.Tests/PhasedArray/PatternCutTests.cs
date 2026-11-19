using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.AntennaArray;
using CircuitRF.Engine.Mom;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

/// <summary>
/// brief-paa-11 — the fine pattern cuts a fields analysis on an array publishes beside its overview (<c>farfield.CutU</c>, <c>CutEtheta</c>,
/// <c>CutEphi</c> over [freq, cut, angle, port]), at a tenth of the beamwidth or finer, the element pattern interpolated and the array factor exact.
/// </summary>
[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class PatternCutTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-paa11-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string ExampleRoot => Path.Combine(ExampleWorkspaces.ResolveRoot() ?? throw new InvalidOperationException("no examples/ tree"), "Phased Array");

    private static string Bench(string cell) => Path.Combine(ExampleRoot, cell, "schematic", cell + ".csch");

    private static RunResult Transmit(CircuitEvaluationRequest request)
        => CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(Bench("1 Transmit")), request);

    private const double Hz = 5.9e9;

    /// <summary>A pattern of 1 in Eθ everywhere on a 5° hemisphere: an isotropic element, which the interpolation reads as exactly 1.</summary>
    private static ElementPattern Isotropic(double stepDeg)
    {
        var grid = PlanarFarFieldGrid.Hemisphere(stepDeg, stepDeg);
        return new ElementPattern(grid, [.. Enumerable.Repeat(Complex.One, grid.DirectionCount)], new Complex[grid.DirectionCount]);
    }

    /// <summary>Gate 1 — on the record's grid the cut is the overview, for the Transmit array at θ0 = 0 and 30° (the sweep's points 0 and 3).
    /// Every cut sample whose direction is a record direction is compared, both planes, all three cubes.</summary>
    [Fact]
    public void OnTheRecordsGrid_TheCutIsTheOverview()
    {
        var run = Transmit(new CircuitEvaluationRequest { Analyses = ["SW1"], Measurements = false });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        var data = run.GroupedResults!;
        var cutU = data["F2.farfield.CutU"];
        var u = data["F2.farfield.U"];
        Assert.Equal(["theta0", "freq", "cut", "angle", "port"], cutU.Axes.Select(a => a.Name));
        var (cuts, angles, theta, phi) = (cutU.Axes[2].Values, cutU.Axes[3].Values, u.Axes[2].Values, u.Axes[3].Values);
        double step = angles[1] - angles[0];
        output.WriteLine($"{cuts.Length} planes, {angles.Length} samples at {step:G6}° against the record's {theta[1] - theta[0]:G4}°");

        int compared = 0;
        double worst = 0;
        foreach (int s in new[] { 0, 3 })
            foreach (string name in new[] { "CutU", "CutEtheta", "CutEphi" })
            {
                var cut = data[$"F2.farfield.{name}"];
                var over = data[$"F2.farfield.{name[3..]}"];
                for (int c = 0; c < cuts.Length; c++)
                    for (int i = 0; i < angles.Length; i++)
                    {
                        double t = angles[i];
                        double ph = (t >= 0 ? cuts[c] : cuts[c] + 180) % 360;
                        int ti = Array.IndexOf(theta, Math.Abs(t)), pj = Array.IndexOf(phi, ph);
                        if (ti < 0 || pj < 0) continue;
                        Complex a = Value(cut, s, 0, c, i, 0), b = Value(over, s, 0, ti, pj, 0);
                        double rel = (a - b).Magnitude / b.Magnitude;
                        if (b.Magnitude > 0) worst = Math.Max(worst, rel);
                        compared++;
                    }
            }
        output.WriteLine($"{compared} samples on the record's grid, worst relative difference {worst:E2}");
        Assert.True(compared >= 2 * 3 * 2 * 37, $"only {compared} samples were on the grid");
        Assert.True(worst <= 1e-12, $"the cut differs from the overview by {worst:E2} on the record's grid");
    }

    private static Complex Value(DataCube cube, params int[] at)
    {
        int flat = 0;
        for (int d = 0; d < at.Length; d++) flat = flat * cube.Axes[d].Length + at[d];
        return cube.DataKind == DataKind.Complex ? cube.ComplexValues[flat] : cube.RealValues[flat];
    }

    /// <summary>Gate 2 — a 16 × 16 uniform array of isotropic elements at half a wavelength: every sample of both cuts is the closed-form array factor
    /// N·sin(Nψ/2)/sin(ψ/2) to 1e-12 of the peak, and the half-power width read off the φ = 0 cut is the elevation beamwidth the metric reads
    /// (ArrayBeam, HpbwDeg(F, "elevation")) to within the cut's step.</summary>
    [Fact]
    public void BetweenTheGridPoints_TheArrayFactorIsExact()
    {
        double lambda = EmConstants.C0 / Hz;
        var lattice = new ArrayLattice(16, 16, lambda / 2, lambda / 2);
        var elements = ArrayElements.Single(Isotropic(5), lattice.Count);
        Complex[] weights = [.. Enumerable.Repeat(Complex.One, lattice.Count)];
        var grid = ArrayPattern.CutGrid(lattice, Hz, [0, 90], 5);
        var field = ArrayPattern.Cuts(lattice, weights, elements, grid, Hz);
        double k = 2 * Math.PI / lambda, peak = 256, worst = 0;
        for (int c = 0; c < 2; c++)
            for (int i = 0; i < grid.AngleDeg.Length; i++)
            {
                double psi = k * lambda / 2 * Math.Sin(grid.AngleDeg[i] * Math.PI / 180);
                double line = Math.Abs(Math.Sin(psi / 2)) < 1e-12 ? 16 : Math.Sin(16 * psi / 2) / Math.Sin(psi / 2);
                int d = c * grid.AngleDeg.Length + i;
                worst = Math.Max(worst, (field.ETheta[d] - 16 * line).Magnitude / peak);
                Assert.Equal(Complex.Zero, field.EPhi[d]);
            }
        output.WriteLine($"step {grid.StepDeg:G6}° (estimated beamwidth {grid.EstimatedBeamwidthDeg:G6}°), worst difference {worst:E2} of the peak");
        Assert.True(worst <= 1e-12, $"the cut differs from the closed form by {worst:E2} of the peak");

        // the half-power width off the φ = 0 cut, crossings interpolated in dB between samples
        int n = grid.AngleDeg.Length, mid = n / 2;
        double Db(int i) => 10 * Math.Log10(Math.Max(field.ETheta[i].Magnitude * field.ETheta[i].Magnitude, 1e-300) / (peak * peak));
        double Cross(int dir)
        {
            int i = mid;
            while (Db(i + dir) >= -3.0103) i += dir;
            double f = (Db(i) + 3.0103) / (Db(i) - Db(i + dir));
            return grid.AngleDeg[i] + f * (grid.AngleDeg[i + dir] - grid.AngleDeg[i]);
        }
        double width = Cross(+1) - Cross(-1);
        double metric = ArrayBeam.Evaluate(lattice, weights, elements, Hz, 0, 0).ElevationBeamwidthDeg;
        output.WriteLine($"half-power width {width:F4}° off the cut, {metric:F4}° from the metric");
        Assert.True(Math.Abs(width - metric) <= grid.StepDeg, $"{width:F4}° against {metric:F4}°, step {grid.StepDeg:G4}°");
    }

    /// <summary>Gate 3 — the example's Transmit run: every θ0's φ = 0 cut has at least ten samples across its main lobe, and F1's (θ0 = 30°)
    /// peak sample is within one step of the peak PointingErrorDeg reads.</summary>
    [Fact]
    public void TheTransmitRun_ResolvesEveryBeam()
    {
        var run = Transmit(new CircuitEvaluationRequest { Analyses = ["F1", "SW1"], Measurements = false, Expressions = ["PointingErrorDeg(F1, 30, 0)"] });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        Assert.True(run.Expressions[0].Error is null, run.Expressions[0].Error);
        var data = run.GroupedResults!;
        var swept = data["F2.farfield.CutU"];
        var angles = swept.Axes[3].Values;
        double step = angles[1] - angles[0];
        int na = angles.Length, per = swept.RealValues.Length / swept.Axes[0].Length;
        for (int s = 0; s < swept.Axes[0].Length; s++)
        {
            var plane = swept.RealValues.Skip(s * per).Take(na).ToArray();   // cut 0 (φ = 0) is the first row of each point
            double max = plane.Max();
            int lobe = plane.Count(v => v >= max / 2);
            output.WriteLine($"θ0 {swept.Axes[0].Values[s] * 180 / Math.PI:F0}°: peak at {angles[Array.IndexOf(plane, max)]:F3}°, {lobe} samples above half power");
            Assert.True(lobe >= 10, $"θ0 index {s}: {lobe} samples across the main lobe");
        }

        var f1 = data["F1.farfield.CutU"];
        var cut = f1.RealValues.Take(na).ToArray();
        double tPeak = angles[Array.IndexOf(cut, cut.Max())];
        double error = run.Expressions[0].Value!.Value.AsReal();
        output.WriteLine($"F1: cut peak at {tPeak:F4}°, PointingErrorDeg(F1, 30, 0) = {error:F4}°, step {step:F4}°");
        Assert.True(Math.Abs(Math.Abs(tPeak - 30) - error) <= step, $"the cut's peak at {tPeak:F4}° is not within {step:F4}° of 30 ± {error:F4}°");
    }

    /// <summary>Gate 4 — the Brute Force bench at −60 dBm, steered to 30°: the cut from harmonic-balance waves is the linear fields analysis's cut on
    /// the same bench, to 2e-9 of the peak after the one complex factor between their references: the amplifier's own nonlinearity at -60 dBm
    /// (7e-10) and HB's round-off floor relative to so small a signal (PhasedArrayHbTests' Gate 1 measures both; 1.18e-9 here).</summary>
    [Fact]
    public void FromHarmonicBalanceWaves_TheCutIsTheLinearOne()
    {
        string text = CircuitRF.Design.Schematic.SchematicCircuit.CnlTextOf(Bench("5 Brute Force"));
        var lines = text.Split('\n').Where(l => !l.StartsWith("analysis ", StringComparison.Ordinal) && !l.StartsWith("measure ", StringComparison.Ordinal))
                        .Select(l => l.StartsWith("Pin = ", StringComparison.Ordinal) ? "Pin = -60"
                                   : l.StartsWith("theta0 = ", StringComparison.Ordinal) ? "theta0 = 30 deg" : l);
        text = string.Join("\n", lines) + "\nanalysis HB1 type=hb Tone=\"f0\" ToneUnit=GHz MaxHarm=5 FFTOverSample=1 Tol=1e-9 DriveStepping=IfNecessary\n" +
               "analysis F1 type=fields block=ARR waves=HB1\nanalysis F2 type=fields block=ARR freq=5.9 freqUnit=GHz drive=1,2,3,4\n";
        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(text, ExampleRoot, ExampleRoot), new CircuitEvaluationRequest());
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
        DataSet Data(string name) => run.Results.Single(r => r.Name == name).Data;
        Complex[] Field(DataSet d) => [.. d.Cubes["farfield.CutEtheta"].ComplexValues, .. d.Cubes["farfield.CutEphi"].ComplexValues];
        var x = Field(Data("F1"));
        var y = Field(Data("F2"));
        Assert.Equal(x.Length, y.Length);
        Complex num = 0;
        double den = 0;
        for (int i = 0; i < x.Length; i++) { num += Complex.Conjugate(y[i]) * x[i]; den += y[i].Magnitude * y[i].Magnitude; }
        Complex r = num / den;
        double peak = x.Max(c => c.Magnitude), worst = 0;
        for (int i = 0; i < x.Length; i++) worst = Math.Max(worst, (x[i] - r * y[i]).Magnitude);
        output.WriteLine($"{x.Length / 2} samples, worst difference {worst / peak:E2} of the peak");
        Assert.True(worst / peak <= 2e-9, $"the HB cut differs from the linear one by {worst / peak:E2}");
    }

    /// <summary>Gate 5 — by counting, no clock: two cuts of a 64 × 64 array of four pattern classes on a 1° record. Each class's pattern is evaluated
    /// once per cut direction, the position phases come from samples × (rows + cols) table entries, and no cut exceeds 3,601 samples.</summary>
    [Fact]
    public void ACutOf64By64_CostsItsTablesAndOnePatternPerClassAndDirection()
    {
        double lambda = EmConstants.C0 / Hz;
        var lattice = new ArrayLattice(64, 64, lambda / 2, lambda / 2);
        var pattern = Isotropic(1);
        int[] classOf = [.. Enumerable.Range(0, lattice.Count).Select(e => (e / 64 < 32 ? 0 : 2) + (e % 64 < 32 ? 0 : 1))];
        var elements = ArrayElements.Classes([pattern, pattern, pattern, pattern], classOf);
        Complex[] weights = [.. Enumerable.Range(0, lattice.Count).Select(e => Complex.FromPolarCoordinates(1, 0.01 * e))];
        var grid = ArrayPattern.CutGrid(lattice, Hz, [0, 90], 1);

        long cuts0 = ArrayPattern.CutsFormed, patterns0 = ArrayPattern.CutPatternEvaluations, tables0 = ArrayPattern.CutTableEntries;
        ArrayPattern.Cuts(lattice, weights, elements, grid, Hz);
        long cuts = ArrayPattern.CutsFormed - cuts0, patterns = ArrayPattern.CutPatternEvaluations - patterns0, tables = ArrayPattern.CutTableEntries - tables0;
        output.WriteLine($"{grid.AngleDeg.Length} samples a cut at {grid.StepDeg:G6}°; {patterns:N0} pattern evaluations, {tables:N0} table entries");
        Assert.Equal(2, cuts);
        Assert.InRange(grid.AngleDeg.Length, 2, ArrayPattern.MaxCutSamples);
        Assert.True(patterns <= 4L * grid.Count, $"{patterns:N0} pattern evaluations for 4 classes × {grid.Count} directions");
        Assert.Equal((long)grid.Count * (64 + 64), tables);
    }

    /// <summary>Gate 6 — an OnDemand evaluation (an optimizer's point) that nothing reads forms no cut; Simulate's forms one per plane.</summary>
    [Fact]
    public void AnOnDemandEvaluation_FormsNoCut()
    {
        long before = ArrayPattern.CutsFormed, patterns = ArrayPattern.CutPatternEvaluations;
        var idle = Transmit(new CircuitEvaluationRequest { Analyses = ["F1"], Measurements = false, Fields = FieldsOutput.OnDemand });
        Assert.True(idle.Status == RunStatus.Success, idle.StatusMessage);
        Assert.Equal(0, ArrayPattern.CutsFormed - before);
        Assert.Equal(0, ArrayPattern.CutPatternEvaluations - patterns);
        Assert.False(idle.GroupedResults!.Contains("F1.farfield.CutU"));

        var written = Transmit(new CircuitEvaluationRequest { Analyses = ["F1"], Measurements = false });
        Assert.Equal(2, ArrayPattern.CutsFormed - before);
        Assert.True(written.GroupedResults!.Contains("F1.farfield.CutU"));
    }

    /// <summary>cuts= is written and read back by the .cnl and the schematic's own analysis record, and names the planes the run cuts.</summary>
    [Fact]
    public void Cuts_SurviveTheCnlAndTheSchematicRecord()
    {
        string path = Path.Combine(_dir, "b.cnl");
        File.WriteAllText(path, "R:R1 a 0 R=50 Ohm\nanalysis F1 type=fields block=PA1 freq=5.9 freqUnit=GHz cuts=\"0,45\"\n");
        var fa = (Core.Design.FieldsAnalysis)Core.Netlist.CnlReader.ReadFile(path).TestBench.Analyses.Single();
        Assert.Equal("0,45", fa.CutsExpr);
        var tb = new Core.Design.TestBench("tb");
        tb.Analyses.Add(fa);
        File.WriteAllText(path, Core.Netlist.CnlWriter.Write(tb));
        Assert.Equal("0,45", ((Core.Design.FieldsAnalysis)Core.Netlist.CnlReader.ReadFile(path).TestBench.Analyses.Single()).CutsExpr);
        var json = CircuitRF.Design.Schematic.AnalysisSerialization.Serialize([fa], []);
        Assert.Equal("0,45", ((Core.Design.FieldsAnalysis)CircuitRF.Design.Schematic.AnalysisSerialization.Deserialize(json).Analyses.Single()).CutsExpr);
    }

    /// <summary>R-paa11-3 — a polar trace of the cut keeps its angle as X and draws the whole plane, −90° to +90°, as one curve with no mirroring:
    /// a sample at a negative angle lands left of boresight. A fresh trace of the cut cube seeds that slice.</summary>
    [Fact]
    public void APolarTraceOfTheCut_DrawsTheWholePlane()
    {
        double[] angles = [-90, -45, 0, 45, 90];
        var cube = new DataCube([new Axis("freq", [Hz], "Hz"), new Axis("cut", [0, 90], "deg"), new Axis("angle", angles, "deg"), new Axis("port", [1], "")],
                                [.. Enumerable.Range(0, 10).Select(i => 1.0 + i)]) { Unit = "W/sr" };
        var seed = SeedSlice.Build(cube);
        Assert.Equal([AxisRole.PinToIndex, AxisRole.PinToIndex, AxisRole.KeepAsX, AxisRole.PinToIndex], seed.Select(s => s.Role));

        var ds = new DataSet();
        ds.Add("F1.farfield.CutU", cube);
        var t = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = "F1.farfield.CutU", Slice = seed, Transform = CubeTransform.dB10,
            // what the plot hands every trace on a dB-radial polar plot (Plot.RefreshPolarPattern)
            PatternScale = new PolarPatternScale { ReferenceDb = 20, FloorDb = -20, RingStepDb = 10, Normalised = false, Unit = "" },
        };
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Polar, FreqUnit.GHz);
        Assert.Null(t.ExpressionError);
        Assert.Equal(angles, t.CubeXValues);
        Assert.Equal(5, t.Points.Count);
        Assert.True(t.Points[0].X < 0 && t.Points[^1].X > 0, $"−90° at x {t.Points[0].X.ToString(CultureInfo.InvariantCulture)}, +90° at x {t.Points[^1].X.ToString(CultureInfo.InvariantCulture)}");
    }
}

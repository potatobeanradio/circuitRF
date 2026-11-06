// ================================================================
//  PlanarFarFieldCosimTests.cs — brief-cosim-pattern-7's routine gates, on real planar solves small enough to run here. A load stamped in the MoM matrix is a
//  reference that shares that matrix with the sum, so it tests the weights and their polarity. The line's own current is the reference that involves no S:
//  it says where a load at a reference plane really landed. The measured numbers are in src/Design/RESOLVED.md.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using NumFlat;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.Sar;

[Collection(FieldCosimulationRunsCollection.Name)]
public sealed class PlanarFarFieldCosimTests : IDisposable
{
    private const double F = 2.4e9, Z0 = 50, Tolerance = 1e-9;
    private const string Fields = "analysis F1 type=fields block=EM1 freq=2.4 freqUnit=GHz drive=1\n";
    private const string TwoPorts = "Port:P1 a 0 Num=1 Z=50 Ohm\nEmFieldData:EM1 a b File=\"ems.cemr\"\n";

    private static readonly GroundedSlab Slab = GroundedSlab.Fr4Starter;
    private static readonly PlanarFarFieldGrid Grid = PlanarFarFieldGrid.Hemisphere(10, 15);
    private static readonly PlanarMeshSettings Mesh = new(Auto: false, CellsPerWavelength: 12, EdgeMesh: false);

    private readonly string _dir = Directory.CreateTempSubdirectory("crf-cp7-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static PlanarPolygon Rect(double x0, double y0, double x1, double y1)
        => new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

    private static PlanarProblem Problem(params PlanarPolygon[] polygons) => new([new PlanarConductorLayer("Metal", polygons, 5.8e7, 35e-6)], Slab, F);

    private static PlanarPort Edge(int number, double x, PlanarPortSide side) => new(number, new EmPoint(x, 0), side, Z0);

    private static PlanarPort Gap(int number, double x) => new(number, new EmPoint(x, 0), PlanarPortSide.MinX, Z0, Kind: PlanarPortKind.InternalDeltaGap);

    private static PlanarKernelResult Solve(PlanarProblem problem, bool pattern, params PlanarPort[] ports)
        => new PlanarKernel().Solve(problem, Mesh, ports, [F], PlanarSolveSettings.Default with { FarField = pattern ? new PlanarFarFieldSettings(Grid, [F]) : null });

    private static PlanarSolveContext Context(PlanarProblem problem, PlanarKernelResult run)
        => new(run.MeshReport.Mesh, run.Ports, PlanarFillSettings.Default with { ConductorLoss = PlanarConductorLoss.For(problem) });

    /// <summary>The result's files, with the record the run writes. A cube named in <paramref name="without"/> is left out, as a result written before it existed has none.</summary>
    private void Write(PlanarKernelResult run, string[]? kinds = null, Func<string, bool>? without = null, DataCube? group = null)
    {
        var data = new DataSet();
        foreach (string g in run.Data.Groups)
            foreach (var (name, cube) in run.Data.CubesIn(g))
                if (without?.Invoke(name) != true) data.AddToGroup(g, name, cube);
        if (group is not null) data.AddToGroup(PlanarKernel.DiagnosticsGroup, PlanarPortBoxes.GroupCube, group);
        EmResultRecord.StampData(data, "stamp-1");
        using (var npy = File.Create(Path.Combine(_dir, "ems_em.npy"))) DataSetExporter.ExportNpy(data, npy);

        int n = run.Ports.Count;
        var s = run.Solve.Points[0].S;
        var row = new List<string> { F.ToString("R") };
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex v = n == 2 ? s[j, i] : s[i, j];
                row.Add($"{v.Real:R} {v.Imaginary:R}");
            }
        string snp = $"ems.s{n}p";
        File.WriteAllLines(Path.Combine(_dir, snp), ["! " + EmProvenanceStamp.StampPrefix + "stamp-1", $"# Hz S RI R {Z0}", string.Join(' ', row)]);
        File.WriteAllText(Path.Combine(_dir, "ems.cemr"), new EmResultRecord
        {
            Stamp = "stamp-1", Written = DateTimeOffset.Now, Solver = new() { Name = "Planar MoM" },
            Files = new() { Snp = snp, Npy = "ems_em.npy" },
            Ports = [.. Enumerable.Range(1, n).Select(k => new EmResultPort { Number = k, Name = $"P{k}", Z0Re = Z0, Kind = kinds?[k - 1] })],
            Frequencies = new() { SweepHz = [F], FarFieldHz = [F] },
        }.Serialize());
    }

    private RunResult Evaluate(string text) => CircuitEvaluation.Evaluate(PreparedCircuit.FromText(text, _dir, _dir), new CircuitEvaluationRequest());

    private DataSet Run(string text)
    {
        var run = Evaluate(text);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
        return run.Results.Single().Data;
    }

    private string Refusal(string text)
    {
        var run = Evaluate(text);
        return run.StatusMessage + string.Join(' ', run.Convergence.Select(c => c.Detail));
    }

    /// <summary>The pattern with <paramref name="load"/> stamped across port 2's gap in the matrix itself, gap 1 at 1 V, and what gap 1 takes under it.</summary>
    private static (PlanarFarFieldPattern Pattern, double AcceptedW) Stamped(PlanarProblem problem, PlanarKernelResult run, Complex load)
    {
        var context = Context(problem, run);
        var pair = PlanarFrequencyKernel.Fit(problem, F, context.Settings.Order).Pair!;
        var terms = pair.For(context.Cores, context.Settings.Order);
        var z = PlanarSystem.Build(context.Cores, terms.VectorPotential, terms.Scalar, 2 * Math.PI * F).Matrix;
        var loaded = run.Ports[1];
        foreach (int a in loaded.BasisIndices)
            foreach (int b in loaded.BasisIndices)
                z[a, b] += load * (loaded.PositiveWeight * loaded.PositiveWeight);
        var currents = z.Lu().Solve(PlanarExcitation.RightHandSide(z.RowCount, run.Ports[0]));
        return (PlanarFarField.Compute(problem, run.MeshReport.Mesh, currents, 1, F, Grid), 0.5 * PlanarExcitation.PortCurrent(currents, run.Ports[0]).Real);
    }

    private static double GainDbi(PlanarFarFieldPattern pattern, double acceptedW)
        => 10 * Math.Log10(4 * Math.PI * pattern.U.Max() / acceptedW);

    private static double Metric(DataSet data, string cube) => data.Cubes[$"farfield.{cube}"].RealValues[0];

    // ── internal gaps: the plane is the gap ──────────────────────────────────────────────────────────

    /// <summary>5 nH is the load the terminated-port formula missed by 19.9 dB in the pattern.</summary>
    [Theory]
    [InlineData("R:R2 b 0 R=200\n", 200.0, 0.0)]
    [InlineData("L:L2 b 0 L=5e-9\n", 0.0, 5e-9)]
    public void TwoGaps_TheSumIsTheLoadStampedInTheMatrix(string part, double r, double l)
    {
        var problem = Problem(Rect(0, -1.5e-3, 40e-3, 1.5e-3));
        var run = Solve(problem, true, Gap(1, 13e-3), Gap(2, 27e-3));
        Write(run);

        var data = Run(TwoPorts + part + Fields);
        var (want, acceptedW) = Stamped(problem, run, new Complex(r, 2 * Math.PI * F * l));
        var theta = data.Cubes["farfield.Etheta"].ComplexValues;
        var phi = data.Cubes["farfield.Ephi"].ComplexValues;
        double peak = want.ETheta.Concat(want.EPhi).Max(c => c.Magnitude);
        for (int d = 0; d < Grid.DirectionCount; d++)
        {
            Assert.True((theta[d] - want.ETheta[d]).Magnitude <= Tolerance * peak, $"Etheta at {d}: wanted {want.ETheta[d]}, got {theta[d]}");
            Assert.True((phi[d] - want.EPhi[d]).Magnitude <= Tolerance * peak, $"Ephi at {d}: wanted {want.EPhi[d]}, got {phi[d]}");
        }
        Assert.Equal(GainDbi(want, acceptedW), Metric(data, "GainDbi"), 6);
    }

    // R-cp7-3
    [Fact]
    public void ASum_PublishesRadiatedPower_AndOmitsTheTermsItCannotSumByName()
    {
        var problem = Problem(Rect(0, -1.5e-3, 40e-3, 1.5e-3));
        Write(Solve(problem, true, Gap(1, 13e-3), Gap(2, 27e-3)));
        var run = Evaluate(TwoPorts + "R:R2 b 0 R=200\n" + Fields);
        var data = run.Results.Single().Data;
        Assert.True(data.Cubes.ContainsKey("farfield.PowerRadiated"));
        Assert.True(data.Cubes.ContainsKey("farfield.PowerAccepted"));
        foreach (string omitted in (string[])["PowerSurfaceWave", "PowerConductor", "PowerFilm", "PowerDielectricAndGround"])
            Assert.False(data.Cubes.ContainsKey($"farfield.{omitted}"), omitted);
        Assert.Contains(run.Notes, n => n.Contains("does not itemise a surface-wave term", StringComparison.Ordinal));
        Assert.Contains(run.Notes, n => n.Contains("as for a planar run", StringComparison.Ordinal));
        Assert.Empty(run.Warnings);
    }

    // ── an edge port: the box ────────────────────────────────────────────────────────────────────────

    /// <summary>The port's voltage at its plane is not its gap's, so the level differs by the constant between them, and the shape and the gain do not. The taper grows a feed lead, which the way back has to put on again.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEdgePortAndAGap_TheSumIsTheStampedLoad_UpToTheDrivesLevel(bool taper)
    {
        double lp = 29.4e-3, wp = 36e-3, fl = 25e-3;
        PlanarProblem problem;
        PlanarPort gap;
        if (taper)
        {
            problem = Problem(new PlanarPolygon([new EmPoint(0, -1e-3), new EmPoint(6e-3, -1.5e-3), new EmPoint(6e-3, 1.5e-3), new EmPoint(0, 1e-3)]), Rect(5.5e-3, -1.5e-3, 22e-3, 1.5e-3));
            gap = Gap(2, 15e-3);
        }
        else
        {
            problem = Problem(Rect(0, -1.5e-3, fl + 0.5e-3, 1.5e-3), Rect(fl, -wp / 2, fl + lp, wp / 2), Rect(fl + lp - 0.5e-3, -1.5e-3, fl + lp + 12e-3, 1.5e-3));
            gap = Gap(2, fl + lp + 6e-3);
        }
        var run = Solve(problem, true, Edge(1, 0, PlanarPortSide.MinX), gap);
        Assert.Equal(taper, run.Solve.PortPeelLengthsM![0] > 0);
        Write(run);

        var data = Run(TwoPorts + "R:R2 b 0 R=100\n" + Fields);
        var (want, acceptedW) = Stamped(problem, run, 100);
        var theta = data.Cubes["farfield.Etheta"].ComplexValues;
        var phi = data.Cubes["farfield.Ephi"].ComplexValues;
        int peak = Enumerable.Range(0, Grid.DirectionCount).MaxBy(d => want.U[d]);
        Complex scale = want.ETheta[peak].Magnitude > want.EPhi[peak].Magnitude ? theta[peak] / want.ETheta[peak] : phi[peak] / want.EPhi[peak];
        double top = want.ETheta.Concat(want.EPhi).Max(c => c.Magnitude);
        for (int d = 0; d < Grid.DirectionCount; d++)
        {
            Assert.True((theta[d] / scale - want.ETheta[d]).Magnitude <= 1e-7 * top, $"Etheta at {d}");
            Assert.True((phi[d] / scale - want.EPhi[d]).Magnitude <= 1e-7 * top, $"Ephi at {d}");
        }
        Assert.Equal(GainDbi(want, acceptedW), Metric(data, "GainDbi"), 5);
    }

    /// <summary>With nothing around it, the block gives the numbers of the run that stored the pattern: R-ant-5's and R-ant-6's rules survive the way there and back.</summary>
    [Fact]
    public void OnePort_DrivenDirectly_ReproducesTheRunsOwnMetrics()
    {
        double lp = 29.4e-3, wp = 36e-3, fl = 25e-3;
        var run = Solve(Problem(Rect(0, -1.5e-3, fl + 0.5e-3, 1.5e-3), Rect(fl, -wp / 2, fl + lp, wp / 2)), true, Edge(1, 0, PlanarPortSide.MinX));
        Write(run);

        var data = Run("Port:P1 a 0 Num=1 Z=50 Ohm\nEmFieldData:EM1 a File=\"ems.cemr\"\n" + Fields);
        var own = run.Data.CubesIn(PlanarFarField.Group);
        foreach (string cube in (string[])["GainDbi", "RealizedGainDbi", "DirectivityDbi", "RadiationEfficiency", "TrpDbm", "PeakEirpDbm"])
            Assert.True(Math.Abs(own[cube].RealValues[0] - Metric(data, cube)) <= 1e-6, $"{cube}: the run's {own[cube].RealValues[0]}, the block's {Metric(data, cube)}");
    }

    /// <summary>The one reference with no S in it: fit the line's own current to forward and backward waves, and read the reflection at port 2's plane.</summary>
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(-1.0, 0.0)]
    [InlineData(0.0, 0.5)]
    public void AnEdgePort_TheWeightsPutALoadAtItsReferencePlane(double re, double im)
    {
        const double L = 80e-3, HalfWidth = 1.45e-3;
        Complex asked = new(re, im);
        var problem = Problem(Rect(0, -HalfWidth, L, HalfWidth));
        var run = Solve(problem, true, Edge(1, 0, PlanarPortSide.MinX), Edge(2, L, PlanarPortSide.MaxX));
        Write(run);
        var (far, refused) = PortFarFieldSet.Open(Path.Combine(_dir, "ems.cemr"));
        Assert.True(far is not null, refused);

        var point = run.Solve.Points[0];
        var s = point.S;
        var calibration = point.Calibrations[0];
        Complex zc = calibration.Zc, gamma = calibration.Gamma.Gamma;
        Complex load = zc * (1 + asked) / (1 - asked);
        Complex gl = (load - Z0) / (load + Z0);
        Complex b2 = s[1, 0] / (1 - s[1, 1] * gl), a2 = gl * b2, b1 = s[0, 0] + s[0, 1] * a2;
        var weights = far.Weights(0, [Complex.One, a2], [b1, b2], out _);

        var solved = Context(problem, run).SolveAt(PlanarFrequencyKernel.Fit(problem, F), F);
        var currents = new Vec<Complex>(solved.Currents[0].Count);
        for (int k = 0; k < 2; k++)
            for (int i = 0; i < currents.Count; i++) currents[i] += weights[k] * solved.Currents[k][i];
        var line = PlanarExcitation.LineCurrent(run.MeshReport.Mesh, currents, PlanarBasisDirection.X, -HalfWidth, HalfWidth)
                                   .Where(p => p.Coord > 0.2 * L && p.Coord < 0.8 * L).ToArray();

        // I(x) = A·e^{−γd} + B·e^{+γd}, d from the plane, by the normal equations
        Complex m00 = 0, m01 = 0, m11 = 0, r0 = 0, r1 = 0;
        foreach (var (x, i) in line)
        {
            double d = x - run.Ports[1].ReferencePlaneM;
            Complex forward = Complex.Exp(-gamma * d), backward = Complex.Exp(gamma * d);
            m00 += Complex.Conjugate(forward) * forward;
            m01 += Complex.Conjugate(forward) * backward;
            m11 += Complex.Conjugate(backward) * backward;
            r0 += Complex.Conjugate(forward) * i;
            r1 += Complex.Conjugate(backward) * i;
        }
        Complex det = m00 * m11 - m01 * Complex.Conjugate(m01);
        Complex a = (r0 * m11 - m01 * r1) / det, b = (m00 * r1 - Complex.Conjugate(m01) * r0) / det;
        Complex read = -b / a;
        Assert.True((read - asked).Magnitude < 0.1, $"asked {asked} at the plane, and the line's current reads {read}");
    }

    // ── what is refused, and what an older result can still do ───────────────────────────────────────

    [Fact]
    public void AnOlderResult_RunsWhenEveryPortIsAGap_AndIsAskedForAgainWithAnEdgePort()
    {
        static bool Box(string cube) => cube.StartsWith("__PortBox", StringComparison.Ordinal);
        var problem = Problem(Rect(0, -1.5e-3, 40e-3, 1.5e-3));
        var gaps = Solve(problem, true, Gap(1, 13e-3), Gap(2, 27e-3));
        Write(gaps);
        var kept = Run(TwoPorts + "R:R2 b 0 R=200\n" + Fields).Cubes["farfield.Etheta"].ComplexValues;

        Write(gaps, kinds: [EmResultPort.Gap, EmResultPort.Gap], without: Box);
        var older = Run(TwoPorts + "R:R2 b 0 R=200\n" + Fields).Cubes["farfield.Etheta"].ComplexValues;
        for (int d = 0; d < kept.Length; d++) Assert.True((kept[d] - older[d]).Magnitude <= Tolerance * kept[d].Magnitude + 1e-15, $"direction {d}");

        // A record that does not say what its ports are cannot be taken to hold only gaps.
        Write(gaps, without: Box);
        Assert.Contains("older planar result", Refusal(TwoPorts + "R:R2 b 0 R=200\n" + Fields), StringComparison.Ordinal);
    }

    [Fact]
    public void ACalibrationGroup_IsRefusedByName_AndARunWithNoPatternKeepsNoBoxes()
    {
        var problem = Problem(Rect(0, -1.5e-3, 40e-3, 1.5e-3));
        Assert.DoesNotContain(PlanarFarField.Group, Solve(problem, false, Gap(1, 13e-3)).Data.Groups);

        Write(Solve(problem, true, Gap(1, 13e-3), Gap(2, 27e-3)), without: c => c.StartsWith("__PortBox", StringComparison.Ordinal), group: DataCube.Scalar(1.0));
        Assert.Contains("calibration group", Refusal(TwoPorts + "R:R2 b 0 R=200\n" + Fields), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdoptedRecord_StatesThePlanarExcitation_FromTheData()
    {
        Write(Solve(Problem(Rect(0, -1.5e-3, 40e-3, 1.5e-3)), true, Gap(1, 13e-3), Gap(2, 27e-3)));
        File.Delete(Path.Combine(_dir, "ems.cemr"));
        var (path, refused) = EmResultRecord.Adopt(Path.Combine(_dir, "ems.s2p"));
        Assert.True(path is not null, refused);
        var (record, why) = EmFieldDataRecord.Open(path);
        Assert.True(record is not null, why);
        Assert.Equal(EmFarFieldExcitation.ShortedOthers, record.FarFieldExcitation);
    }
}

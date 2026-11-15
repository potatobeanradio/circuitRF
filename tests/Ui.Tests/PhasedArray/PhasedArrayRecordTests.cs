// brief-paa-2 — the array engine against records (gates 1, 3, 5, 7): the L2 identity against the fields analysis, PAA-1's L0/L1 numbers
// reproduced on the committed fixtures, port positions written by a new planar run and fitted to its lattice, and the beam's metrics
// at 64 × 64 from the local evaluation against a fine direct sum. Fixtures: testdata/phased-array (5.9 GHz, 5° grid; its README).

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Engine.AntennaArray;
using CircuitRF.Ui.Layout;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class PhasedArrayRecordTests(ITestOutputHelper output)
{
    private const double F = 5.9e9, Pitch = 25.86e-3, Z0 = 50;
    private static readonly double K = 2 * Math.PI * F / CircuitRF.Engine.Mom.EmConstants.C0;

    private static string Fixture(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", name));

    private static PortFarFieldSet Open(string name)
    {
        var (set, refusal) = PortFarFieldSet.Open(Fixture(Path.Combine("results", name + ".cemr")));
        Assert.True(set is not null, refusal);
        return set!;
    }

    private static Complex[] Steer(ArrayLattice lattice, double theta0Deg, double phi0Deg)
    {
        var (u0, v0, _) = ArrayDirections.Vector(theta0Deg * Math.PI / 180, phi0Deg * Math.PI / 180);
        return [.. Enumerable.Range(0, lattice.Count).Select(n =>
        {
            var (x, y) = lattice.Position(n);
            return Complex.FromPolarCoordinates(1, -K * (x * u0 + y * v0));
        })];
    }

    /// <summary>Each element's own port's pattern, referred to that port's position: an array solved whole.</summary>
    private static (ArrayLattice Lattice, ArrayElements Elements) Whole(PortFarFieldSet far, int rows, int cols)
    {
        var fit = RecordLattice.Of(far, rows, cols, Pitch, Pitch);
        Assert.NotNull(fit.Lattice);
        var patterns = Enumerable.Range(0, fit.Lattice!.Count)
                                 .Select(e => EmbeddedPatterns.Referred(far, fit.PortOfElement[e], F, fit.Positions[fit.PortOfElement[e]])).ToArray();
        return (fit.Lattice, ArrayElements.Each(patterns));
    }

    /// <summary>Gate 1 — the L2 identity: each port's own embedded pattern, steered, through ArrayPattern, is the fields analysis's pattern
    /// (FieldCosimulation.PointOf) on the 5×5 fixture.</summary>
    [Fact]
    public void L2_IsTheFieldsAnalysis()
    {
        var far = Open("e5");
        var (lattice, elements) = Whole(far, 5, 5);
        var w = Steer(lattice, 30, 45);

        var text = new System.Text.StringBuilder();
        for (int k = 1; k <= 25; k++) text.AppendLine($"Port:P{k} p{k} 0 Num={k} Z=50 Ohm");
        text.AppendLine($"EmFieldData:EM1 {string.Join(' ', Enumerable.Range(1, 25).Select(k => $"p{k}"))} File=\"{Fixture(Path.Combine("results", "e5.cemr"))}\"");
        for (int k = 1; k <= 25; k++) text.AppendLine($"W{k} = {w[k - 1].Real:R} + j*({w[k - 1].Imaginary:R})");
        text.AppendLine($"analysis F1 type=fields block=EM1 freq=5.9 freqUnit=GHz drive={string.Join(',', Enumerable.Range(1, 25))} " +
                        $"weights={string.Join(',', Enumerable.Range(1, 25).Select(k => $"W{k}"))}");
        string dir = Directory.CreateTempSubdirectory("crf-paa2-").FullName;
        try
        {
            var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(text.ToString(), dir, dir), new CircuitEvaluationRequest());
            Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
            var data = run.Results.Single().Data;
            var wantTheta = data.Cubes["farfield.Etheta"].ComplexValues;
            var wantPhi = data.Cubes["farfield.Ephi"].ComplexValues;
            // The circuit's own waves at the block, on its own scale: the pattern is referred to 1 V at circuit port 1.
            var a = data.Cubes[FieldCosimulation.IncidentCube].ComplexValues;
            var b = data.Cubes[FieldCosimulation.ReflectedCube].ComplexValues;
            Complex v1 = Math.Sqrt(Z0) * (a[0] + b[0]);

            var field = ArrayPattern.Sum(lattice, a, elements, ArrayDirections.OnGrid(far.Grid), F);
            double peak = wantTheta.Zip(wantPhi, (t, p) => Math.Sqrt(t.Magnitude * t.Magnitude + p.Magnitude * p.Magnitude)).Max();
            double worst = 0;
            for (int d = 0; d < far.Grid.DirectionCount; d++)
                worst = Math.Max(worst, Math.Max((field.ETheta[d] / v1 - wantTheta[d]).Magnitude, (field.EPhi[d] / v1 - wantPhi[d]).Magnitude) / peak);
            output.WriteLine($"L2 identity on E5, steered to 30°/45°: worst difference {worst:E2} of the peak.");
            Assert.True(worst <= 1e-9, $"worst difference {worst:E2} of the peak");
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed record Level(double PeakGainDbi, double PowerBalance, Complex[] Gamma, double[] Gain);

    private static Level Evaluate(ArrayLattice lattice, ArrayElements elements, ArraySparseMatrix s, Complex[] a, PortFarFieldSet target)
    {
        var field = ArrayPattern.Sum(lattice, a, elements, ArrayDirections.OnGrid(target.Grid), F);
        var pattern = ArrayWaves.Pattern(field, target.Grid);
        var active = ArrayWaves.Active(s, a);
        double incident = active.IncidentW;
        var gain = pattern.U.Select(u => 4 * Math.PI * u / incident).ToArray();
        return new Level(10 * Math.Log10(gain.Max()), ArrayWaves.PowerBalance(pattern.RadiatedPowerW, active.AcceptedW),
                         [.. active.Gamma.Select(g => g!.Value)], gain);
    }

    /// <summary>Gate 3 — the engine reproduces PAA-1's harness on the fixtures to 1e-9: L0 from the element, L1 from the 5×5 (centre
    /// pattern and position classes, coupling extended), L2 from the 6×6 itself, at every scan the harness ran.</summary>
    [Fact]
    public void L0L1L2_ReproduceTheHarness_OnTheFixtures()
    {
        var reference = JsonDocument.Parse(File.ReadAllText(Fixture("paa2_reference.json"))).RootElement;
        Assert.Equal(1 / (2 * ArrayWaves.Eta0), reference.GetProperty("C").GetDouble(), 1e-15);
        var e66 = Open("e66");
        var e5 = Open("e5");
        var e1 = Open("e1");
        var (lattice, whole) = Whole(e66, 6, 6);
        var sub = RecordLattice.Of(e5, 5, 5, Pitch, Pitch);
        var one = RecordLattice.Of(e1, 1, 1, Pitch, Pitch);
        int centre = ArrayCoupling.SourceIndex(1, 1, 3, 3, sub.Lattice!);

        long referrals = EmbeddedPatterns.Referrals;
        var classPatterns = Enumerable.Range(0, 25).Select(p => EmbeddedPatterns.Referred(e5, p, F, sub.Positions[p])).ToArray();
        Assert.Same(classPatterns[centre], EmbeddedPatterns.Referred(e5, centre, F, sub.Positions[centre]));
        Assert.True(EmbeddedPatterns.Referrals - referrals <= 25, "a pattern already referred was referred again");

        var levels = new Dictionary<string, (ArrayElements Elements, ArraySparseMatrix S)>
        {
            ["L2"] = (whole, ArraySparseMatrix.FromDense(e66.S[0])),
            ["L0"] = (ArrayElements.Single(EmbeddedPatterns.Referred(e1, 0, F, one.Positions[0]), lattice.Count),
                      ArrayCoupling.Extend(e1.S[0], one.Lattice!, lattice).S),
            ["L1c"] = (ArrayElements.Single(classPatterns[centre], lattice.Count), ArrayCoupling.Extend(e5.S[0], sub.Lattice!, lattice).S),
            ["L1k"] = (ArrayElements.Classes(classPatterns, ArrayCoupling.Classes(lattice, sub.Lattice!)),
                       ArrayCoupling.Extend(e5.S[0], sub.Lattice!, lattice, classes: true).S),
        };
        double worst = 0;
        foreach (var scan in reference.GetProperty("scans").EnumerateArray())
        {
            double t0 = scan.GetProperty("theta0").GetDouble(), p0 = scan.GetProperty("phi0").GetDouble();
            var a = Steer(lattice, t0, p0);
            var got = levels.ToDictionary(l => l.Key, l => Evaluate(lattice, l.Value.Elements, l.Value.S, a, e66));
            var l2 = got["L2"];
            var mask = l2.Gain.Select(g => g > l2.Gain.Max() * 1e-2).ToArray();
            foreach (var lv in scan.GetProperty("levels").EnumerateObject())
            {
                var want = lv.Value;
                var g = got[lv.Name];
                void Check(double expected, double actual, string what)
                {
                    worst = Math.Max(worst, Math.Abs(expected - actual));
                    Assert.True(Math.Abs(expected - actual) <= 1e-9, $"{lv.Name} at {t0}°/{p0}°, {what}: harness {expected}, engine {actual}");
                }
                Check(want.GetProperty("peakGainDbi").GetDouble(), g.PeakGainDbi, "peak realized gain");
                Check(want.GetProperty("powerBalance").GetDouble(), g.PowerBalance, "power balance");
                var re = want.GetProperty("gammaRe").EnumerateArray().Select(x => x.GetDouble()).ToArray();
                var im = want.GetProperty("gammaIm").EnumerateArray().Select(x => x.GetDouble()).ToArray();
                for (int n = 0; n < re.Length; n++) Check((new Complex(re[n], im[n]) - g.Gamma[n]).Magnitude, 0, $"Γ_act of element {n + 1}");
                if (lv.Name == "L2") continue;
                double rms = Math.Sqrt(Enumerable.Range(0, mask.Length).Where(d => mask[d])
                                                 .Average(d => Math.Pow(10 * Math.Log10(g.Gain[d] / l2.Gain[d]), 2)));
                Check(want.GetProperty("rmsErrorDb").GetDouble(), rms, "RMS pattern error");
                Check(want.GetProperty("maxGammaError").GetDouble(), g.Gamma.Zip(l2.Gamma, (x, y) => (x - y).Magnitude).Max(), "worst |ΔΓ_act|");
            }
        }
        output.WriteLine($"Engine against PAA-1's harness on the fixtures: worst difference {worst:E2}.");
    }

    /// <summary>Gate 5 — a new planar run of a 2×2 of short stubs records four positions, which fit a 2×2 at the drawn pitch; an older
    /// record reads as "positions not recorded".</summary>
    [Fact]
    public void ANewRun_RecordsPositions_ThatFitItsLattice_AndAnOlderOneSaysNotRecorded()
    {
        const int Dbu = LayoutUnits.DefaultDbuPerMicron;
        static long Mm(double mm) => (long)Math.Round(mm * 1e3 * Dbu);
        var top = new LayerKey(1, 0);
        var view = new LayoutView { DbuPerMicron = Dbu };
        int n = 0;
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < 2; c++)
            {
                double x = c * 10, y = (1 - r) * 8;
                view.Shapes.Add(new RectShape { Layer = top, X1 = Mm(x), Y1 = Mm(y), X2 = Mm(x + 3), Y2 = Mm(y + 1) });
                view.Shapes.Add(new LabelShape
                {
                    Layer = top, PortLayer = top, X = Mm(x), Y = Mm(y + 0.5), Text = $"P{++n}", Height = Mm(0.5), IsPort = true, PortDirection = LayoutRotation.R0,
                });
            }
        string dir = Directory.CreateTempSubdirectory("crf-paa2-pos-").FullName;
        try
        {
            var setup = new EmSetup
            {
                Name = "stubs", LayoutRef = "a.clay", AnalysisKind = CircuitRF.Engine.Mom.EmAnalysisKind.Planar,
                Frequency = new FrequencySpec("2", "2", 1, SweepKind.Linear, "GHz", "GHz"),
                PlanarMesh = new CircuitRF.Engine.Mom.PlanarMeshSettings(Auto: false, CellsPerWavelength: 12, EdgeMesh: false),
            };
            var source = new EmLayoutSource(Path.Combine(dir, "a.clay"), view, StarterTechnologies.Pcb2Layer(), Dbu);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var run = EmRunService.Run(setup, source, Path.Combine(dir, "results"));
            output.WriteLine($"2×2 stub run: {watch.Elapsed.TotalSeconds:F1} s.");
            Assert.True(run.Status == EmRunStatus.Ok, run.Error);
            var (record, refusal) = EmResultRecord.Load(run.RecordPath!);
            Assert.True(record is not null, refusal);
            Assert.All(record!.Ports, p => Assert.NotNull(p.Position));

            var fit = RecordLattice.Of(record);
            Assert.True(fit.Lattice is not null, fit.Refusal);
            Assert.True(fit.PositionsRecorded);
            Assert.Equal((2, 2, ArrayLatticeKind.Rect), (fit.Lattice!.Rows, fit.Lattice.Cols, fit.Lattice.Kind));
            Assert.Equal(10e-3, fit.Lattice.Dx, 9);
            Assert.Equal(8e-3, fit.Lattice.Dy, 9);
            Assert.True(fit.RowMajor, fit.Note);
        }
        finally { Directory.Delete(dir, true); }

        var (older, _) = EmResultRecord.Load(Fixture(Path.Combine("results", "e5.cemr")));
        var none = RecordLattice.Of(older!);
        Assert.Null(none.Lattice);
        Assert.Contains(RecordLattice.NotRecorded, none.Refusal);
        var stated = RecordLattice.Of(older!, 5, 5, Pitch, Pitch);
        Assert.False(stated.PositionsRecorded);
        Assert.Contains("ASSUMED numbered row-major", stated.Note);
    }

    /// <summary>R-paa2-9 — what does not fit is explained: a missing port, two pitches, a rotation; a triangular lattice and a numbering
    /// that is not row-major are read.</summary>
    [Fact]
    public void ALattice_IsFitted_OrExplained()
    {
        (double, double)? P(double x, double y) => (x * 1e-3, y * 1e-3);
        var tri = RecordLattice.Of([P(0, 10), P(10, 10), P(5, 0), P(15, 0)]);
        Assert.Equal(ArrayLatticeKind.Tri, tri.Lattice!.Kind);
        var swapped = RecordLattice.Of([P(10, 10), P(0, 10), P(0, 0), P(10, 0)]);
        Assert.False(swapped.RowMajor);
        Assert.Equal([1, 0, 2, 3], swapped.PortOfElement);
        Assert.Contains("missing", RecordLattice.Of([P(0, 10), P(10, 10), P(0, 0)]).Refusal);
        Assert.Contains("two pitches", RecordLattice.Of([P(0, 0), P(10, 0), P(25, 0)]).Refusal);
        double c = Math.Cos(0.2), s = Math.Sin(0.2);
        Assert.Contains("turned", RecordLattice.Of([P(0, 0), P(10 * c, 10 * s), P(-10 * s, 10 * c), P(10 * c - 10 * s, 10 * s + 10 * c)]).Refusal);
    }

    /// <summary>The direct sum's beam on a fine uniform grid, the element pattern sampled exactly (its interpolant evaluated at each point).</summary>
    private static double DirectPower(ArrayLattice lattice, Complex[] w, ElementPattern element, (double X, double Y, double Z) u)
    {
        var (theta, phi) = ArrayDirections.Angles(u);
        Complex af = 0;
        for (int n = 0; n < lattice.Count; n++)
        {
            var (x, y) = lattice.Position(n);
            var (sn, cs) = Math.SinCos(K * (x * u.X + y * u.Y));
            af += w[n] * new Complex(cs, sn);
        }
        var (et, ep) = element.At(theta, phi);
        return (et * af).Magnitude * (et * af).Magnitude + (ep * af).Magnitude * (ep * af).Magnitude;
    }

    private static (double, double, double) Along((double X, double Y, double Z) p, (double X, double Y, double Z) e, double deg)
    {
        var (s, c) = Math.SinCos(deg * Math.PI / 180);
        return (c * p.X + s * e.X, c * p.Y + s * e.Y, c * p.Z + s * e.Z);
    }

    /// <summary>Reads a peak (parabola), a half-power width (linear in dB) and a first sidelobe (parabola in dB) off uniform samples.</summary>
    private static (double Width, double? Sidelobe) Read(double[] offsets, double[] power, double peak)
    {
        int m = offsets.Length / 2;
        double Db(double v) => 10 * Math.Log10(Math.Max(v, 1e-300) / peak);
        double Cross(int dir)
        {
            int i = m;
            while (power[i + dir] >= peak / 2) i += dir;
            double la = Db(power[i]) + 3.0103, lb = Db(power[i + dir]) + 3.0103;
            return offsets[i] + la / (la - lb) * (offsets[i + dir] - offsets[i]);
        }
        double? best = null;
        foreach (int dir in (int[])[1, -1])
        {
            int i = m, last = dir > 0 ? power.Length - 1 : 0;
            while (i != last && power[i] >= peak / 2) i += dir;
            while (i != last && power[i + dir] <= power[i]) i += dir;
            while (i != last && power[i + dir] > power[i]) i += dir;
            if (i == last) continue;
            double l = Db(power[i - dir]), c0 = Db(power[i]), r = Db(power[i + dir]);
            double den = l - 2 * c0 + r, v = den < 0 ? 0.5 * (l - r) / den : 0;
            double level = c0 - 0.25 * (l - r) * v;
            best = best is null ? level : Math.Max(best.Value, level);
        }
        return (Cross(1) - Cross(-1), best);
    }

    /// <summary>Gate 7 — a 64 × 64 array of E5's centre pattern steered to 0° and 45°: the local evaluation's beamwidth, first sidelobe and
    /// pointing error are within 0.02°, 0.05 dB and 0.02° of a direct sum on a uniform 0.05° grid. What the same metrics read off a 1°
    /// overview grid would say, and the interpolant's own error, are written out for the completion note.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void At64x64_TheLocalEvaluation_IsTheFineDirectSum(double theta0)
    {
        var e5 = Open("e5");
        var sub = RecordLattice.Of(e5, 5, 5, Pitch, Pitch);
        var element = EmbeddedPatterns.Referred(e5, 12, F, sub.Positions[12]);
        var lattice = new ArrayLattice(64, 64, Pitch, Pitch);
        var w = Steer(lattice, theta0, 0);
        var elements = ArrayElements.Single(element, lattice.Count);
        var local = ArrayBeam.Evaluate(lattice, w, elements, F, theta0, 0, theta0, 0);

        // The reference: a 0.05° tangent-plane patch around the requested direction for the peak, then 0.05° cuts through that peak.
        const double Step = 0.05;
        var u0 = ArrayDirections.Vector(theta0 * Math.PI / 180, 0);
        var t1 = (Math.Cos(theta0 * Math.PI / 180), 0.0, -Math.Sin(theta0 * Math.PI / 180));
        var t2 = (0.0, 1.0, 0.0);
        const int Half = 20;
        double bestP = 0;
        int bi = 0, bj = 0;
        var patch = new double[2 * Half + 1, 2 * Half + 1];
        for (int i = -Half; i <= Half; i++)
            for (int j = -Half; j <= Half; j++)
            {
                var u = Along(Along(u0, t1, i * Step), t2, j * Step);
                double norm = Math.Sqrt(u.Item1 * u.Item1 + u.Item2 * u.Item2 + u.Item3 * u.Item3);
                double p = DirectPower(lattice, w, element, (u.Item1 / norm, u.Item2 / norm, u.Item3 / norm));
                patch[i + Half, j + Half] = p;
                if (p > bestP) { bestP = p; bi = i; bj = j; }
            }
        double Vertex(double l, double c, double r) => l - 2 * c + r < 0 ? 0.5 * (l - r) / (l - 2 * c + r) : 0;
        double di = Vertex(patch[bi + Half - 1, bj + Half], bestP, patch[bi + Half + 1, bj + Half]);
        double dj = Vertex(patch[bi + Half, bj + Half - 1], bestP, patch[bi + Half, bj + Half + 1]);
        double peakTheta = theta0 + (bi + di) * Step;                     // in the φ = 0 plane, plus a transverse offset
        double transverse = (bj + dj) * Step;
        var peakDir = Along(Along(u0, t1, (bi + di) * Step), t2, transverse);
        double norm0 = Math.Sqrt(peakDir.Item1 * peakDir.Item1 + peakDir.Item2 * peakDir.Item2 + peakDir.Item3 * peakDir.Item3);
        peakDir = (peakDir.Item1 / norm0, peakDir.Item2 / norm0, peakDir.Item3 / norm0);
        double pointing = ArrayBeam.Separation(peakDir, u0) * 180 / Math.PI;

        double span = 8 / Math.Max(Math.Cos(theta0 * Math.PI / 180), 0.3);
        int nCut = (int)Math.Round(span / Step);
        double[] offsets = [.. Enumerable.Range(-nCut, 2 * nCut + 1).Select(k => k * Step)];
        var (pt, pp) = ArrayDirections.Angles(peakDir);
        var e1 = (Math.Cos(pt) * Math.Cos(pp), Math.Cos(pt) * Math.Sin(pp), -Math.Sin(pt));
        var e2 = (-Math.Sin(pp), Math.Cos(pp), 0.0);
        double peak = DirectPower(lattice, w, element, peakDir);
        var elev = Read(offsets, [.. offsets.Select(o => DirectPower(lattice, w, element, Along(peakDir, e1, o)))], peak);
        var azim = Read(offsets, [.. offsets.Select(o => DirectPower(lattice, w, element, Along(peakDir, e2, o)))], peak);

        output.WriteLine($"θ0 {theta0}°: local step {local.StepDeg:F3}° (estimate {local.EstimatedBeamwidthDeg:F3}° beam); " +
                         $"peak θ {local.PeakThetaDeg:F4}° vs {peakTheta:F4}° (transverse {transverse:F4}°), pointing {local.PointingErrorDeg:F4}° vs {pointing:F4}°; " +
                         $"beamwidth E {local.ElevationBeamwidthDeg:F4}° vs {elev.Width:F4}°, A {local.AzimuthBeamwidthDeg:F4}° vs {azim.Width:F4}°; " +
                         $"first sidelobe E {local.ElevationSidelobeDb:F3} vs {elev.Sidelobe:F3} dB, A {local.AzimuthSidelobeDb:F3} vs {azim.Sidelobe:F3} dB.");
        Assert.InRange(local.ElevationBeamwidthDeg - elev.Width, -0.02, 0.02);
        Assert.InRange(local.AzimuthBeamwidthDeg - azim.Width, -0.02, 0.02);
        Assert.InRange(local.PointingErrorDeg!.Value - pointing, -0.02, 0.02);
        Assert.InRange(local.ElevationSidelobeDb!.Value - elev.Sidelobe!.Value, -0.05, 0.05);
        Assert.InRange(local.AzimuthSidelobeDb!.Value - azim.Sidelobe!.Value, -0.05, 0.05);

        // What the overview would have said: the same reading on a 1° grid's samples (the record's own grid is 5°).
        double[] coarse = [.. Enumerable.Range(-(int)span, 2 * (int)span + 1).Select(k => (double)k)];
        var gridPeak = Enumerable.Range(-10, 21).Select(k => (k: (double)k, p: DirectPower(lattice, w, element, ArrayDirections.Vector((theta0 + k) * Math.PI / 180, 0)))).MaxBy(x => x.p);
        var gridDir = ArrayDirections.Vector((theta0 + gridPeak.k) * Math.PI / 180, 0);
        var g1 = (Math.Cos((theta0 + gridPeak.k) * Math.PI / 180), 0.0, -Math.Sin((theta0 + gridPeak.k) * Math.PI / 180));
        var overview = Read(coarse, [.. coarse.Select(o => DirectPower(lattice, w, element, Along(gridDir, g1, o)))], gridPeak.p);
        output.WriteLine($"θ0 {theta0}° off a 1° grid: peak at {theta0 + gridPeak.k}° (pointing error {Math.Abs(gridPeak.k):F2}° against {pointing:F4}°), " +
                         $"beamwidth {overview.Width:F3}° against {elev.Width:F4}°, first sidelobe {overview.Sidelobe:F3} dB against {elev.Sidelobe:F3} dB.");

        if (theta0 != 0) return;
        // The interpolant's error: E5's centre pattern interpolated from its 10° samples at the 5° nodes between them.
        var g = e5.Grid;
        var coarseGrid = new CircuitRF.Engine.Mom.PlanarFarFieldGrid([.. g.ThetaDeg.Where((_, i) => i % 2 == 0)], [.. g.PhiDeg.Where((_, i) => i % 2 == 0)]);
        Complex[] Pick(Complex[] all) => [.. Enumerable.Range(0, g.ThetaDeg.Count).Where(i => i % 2 == 0)
                                              .SelectMany(i => Enumerable.Range(0, g.PhiDeg.Count).Where(j => j % 2 == 0).Select(j => all[g.IndexOf(i, j)]))];
        var thin = new ElementPattern(coarseGrid, Pick(element.ETheta), Pick(element.EPhi));
        double ePeak = element.ETheta.Zip(element.EPhi, (a, b) => Math.Sqrt(a.Magnitude * a.Magnitude + b.Magnitude * b.Magnitude)).Max();
        double worst = 0, rms = 0, worstPower = 0;
        int count = 0;
        (int, int) at = (0, 0);
        for (int i = 1; i < g.ThetaDeg.Count - 1; i += 2)
            for (int j = 0; j < g.PhiDeg.Count; j++)
            {
                var (et, ep) = thin.At(g.ThetaDeg[i] * Math.PI / 180, g.PhiDeg[j] * Math.PI / 180);
                int d = g.IndexOf(i, j);
                double err = Math.Sqrt(Math.Pow((et - element.ETheta[d]).Magnitude, 2) + Math.Pow((ep - element.EPhi[d]).Magnitude, 2)) / ePeak;
                if (err > worst) { worst = err; at = (i, j); }
                rms += err * err;
                double pw = et.Magnitude * et.Magnitude + ep.Magnitude * ep.Magnitude;
                double pe = element.ETheta[d].Magnitude * element.ETheta[d].Magnitude + element.EPhi[d].Magnitude * element.EPhi[d].Magnitude;
                worstPower = Math.Max(worstPower, Math.Abs(pw - pe) / (ePeak * ePeak));
                count++;
            }
        output.WriteLine($"Interpolant: E5's centre pattern from its 10° samples at the 5° θ nodes between them: worst {worst:E2} (at θ {g.ThetaDeg[at.Item1]}°, φ {g.PhiDeg[at.Item2]}°), RMS {Math.Sqrt(rms / count):E2} of its peak; " +
                         $"in |E|², worst {worstPower:E2} of the peak power.");
    }
}

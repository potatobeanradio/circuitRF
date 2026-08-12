// The calibration standards: solve only the two a frequency actually uses.
//
// §0 of brief-em-sweep-performance measured the standards at ~75% of a real user's de-embedded run,
// and the reason is not that a standard is expensive per se — it is that EVERY standard was filled at
// EVERY frequency while exactly two of them were ever read.
//
// PlanarCalibration.GammaBest reads sShort and sLong[pick]. `pick` comes from SelectSeparation, which
// is a function of the Δℓ set and the PREDICTED β alone — no solved matrix enters it, deliberately
// (an aliased separation reports a wrapped electrical length that can score well by accident, so the
// choice is made on the prediction). Both inputs are known before any fill. So the driver can fill
// two meshes instead of all of them, and nothing downstream can tell.
//
// That last clause is what T1 asserts, and it asserts it the only way worth asserting it: by running
// the OLD path — every standard solved, GammaBest over the full set — alongside the new one and
// comparing γ, the error box and Z_c BIT FOR BIT at every frequency of a real sweep, with the branch
// continuation stepped exactly as PlanarPortCalibrator.At steps it.
//
// The separations are sized geometrically across the band, so which standard is wasted depends on
// where in the band you are: at the top of a 1–20 GHz sweep the LONGEST standard is several times the
// DUT's own unknown count and is discarded; at the bottom the short ones are. There is no frequency
// at which all of them are wanted.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class CalibrationStandardSelectionTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // 1–20 GHz is 20:1 against DesignBandRatioPerSeparation = 4, so SuggestDeltas returns three
    // separations and BuildSet returns FOUR standards. A band that produced only two would make
    // every assertion here vacuous — T0 pins that it does not.
    private const double FLo = 1e9, FHi = 20e9;

    private static (PlanarProblem P, PlanarMesh M, IReadOnlyList<PlanarPortResolution> Ports) Fixture()
    {
        var line = PlanarLineFixtures.Fr4Line(8e-3, 6e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(line, PlanarLineFixtures.Coarse);
        return (line, mesh, ports);
    }

    private static double[] Grid(double f0, double f1, int n)
    {
        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = f0 * Math.Pow(f1 / f0, i / (double)(n - 1));
        return f;
    }

    [Fact]
    public void T0_TheBandGenuinelyAsksForSeveralSeparations_OrEverythingBelowIsVacuous()
    {
        var (p, _, ports) = Fixture();
        var set = PlanarCalibration.BuildSet(ports[0], p.Slab, FLo, FHi);

        Assert.True(set.Length >= 4,
            $"this band produced {set.Length} standard(s); the selection has nothing to choose between");
        _out.WriteLine($"{set.Length} standards: " +
                       string.Join(", ", set.Select(s => $"{s.LengthM * 1e3:F2} mm / N={s.Mesh.Bases.Count}")));
    }

    [Fact]
    public void T1_SelectiveSolving_ReproducesTheFullSetsCalibration_BitForBit()
    {
        var (p, _, ports) = Fixture();
        var port = ports[0];
        double[] freqs = Grid(FLo, FHi, 9);

        var cal = new PlanarPortCalibrator(port, p.Slab, FLo, FHi);

        // The oracle: the same standards, every one of them solved, GammaBest over the full set —
        // i.e. exactly what this code did before the selection was hoisted out of GammaBest.
        var set   = PlanarCalibration.BuildSet(port, p.Slab, FLo, FHi);
        var ctxs  = set.Select(s => new PlanarSolveContext(s.Mesh, s.Ports)).ToArray();
        var delta = set.Skip(1).Select(s => s.LengthM - set[0].LengthM).ToArray();

        double   prevBeta = double.NaN, prevF = double.NaN;
        Complex? prevA21  = null;

        foreach (double f in freqs)
        {
            var kernel = PlanarFrequencyKernel.FromPair(PlanarLineFixtures.Kernel(p.Slab, f));

            var got = cal.At(() => kernel, f);

            double expect = double.IsNaN(prevBeta)
                ? PlanarCalibration.EstimateBeta(p.Slab, f)
                : prevBeta * (f / prevF);

            var raw  = ctxs.Select(c => c.RawScatteringAt(kernel, f)).ToArray();
            var full = PlanarCalibration.GammaBest(raw[0], raw[1..], delta, expect, out int pick);
            var box  = PlanarDeembed.SolveErrorBox(raw[0], raw[pick + 1], set[0].LengthM,
                                                   set[0].LengthM + delta[pick], full.Gamma, prevA21);

            Assert.Equal(full.Gamma.Real,      got.Gamma.Gamma.Real);
            Assert.Equal(full.Gamma.Imaginary, got.Gamma.Gamma.Imaginary);
            Assert.Equal(full.ElectricalDegrees, got.Gamma.ElectricalDegrees);
            Assert.Equal(full.Unwrapped,         got.Gamma.Unwrapped);
            Assert.Equal(box.A11.Real,      got.Box.A11.Real);
            Assert.Equal(box.A11.Imaginary, got.Box.A11.Imaginary);
            Assert.Equal(box.A22.Real,      got.Box.A22.Real);
            Assert.Equal(box.A22.Imaginary, got.Box.A22.Imaginary);
            Assert.Equal(box.A21.Real,      got.Box.A21.Real);
            Assert.Equal(box.A21.Imaginary, got.Box.A21.Imaginary);
            Assert.Equal(box.ConsistencyResidual, got.Box.ConsistencyResidual);

            prevBeta = full.Beta;
            prevF    = f;
            prevA21  = box.A21;
        }

        _out.WriteLine($"{freqs.Length} frequencies, {set.Length} standards built, " +
                       $"{cal.StandardSolveCount} mesh fills — bit-identical to filling " +
                       $"{freqs.Length * set.Length}.");
    }

    [Fact]
    public void T2_EveryFrequencyFillsExactlyTwoStandards_HoweverManyTheBandBuilt()
    {
        var (p, _, ports) = Fixture();
        var cal = new PlanarPortCalibrator(ports[0], p.Slab, FLo, FHi);
        double[] freqs = Grid(FLo, FHi, 9);

        foreach (double f in freqs)
            cal.At(() => PlanarFrequencyKernel.FromPair(PlanarLineFixtures.Kernel(p.Slab, f)), f);

        Assert.True(cal.MeshCount >= 4);                       // the SET is not narrowed
        Assert.Equal(freqs.Length, cal.SolveCount);
        Assert.Equal(2 * freqs.Length, cal.StandardSolveCount);

        _out.WriteLine($"{cal.MeshCount} standards owned, {cal.StandardSolveCount} filled over " +
                       $"{freqs.Length} frequencies — {freqs.Length * cal.MeshCount} before.");
    }

    [Fact]
    public void T3_TheChoiceGenuinelyMovesAcrossTheBand_SoNoOneStandardWouldHaveDone()
    {
        // If the same separation won everywhere, "solve two" would be indistinguishable from "build
        // two", and the multiline set would be pointless. It does not: the separations are sized
        // geometrically and each owns its own sub-band.
        var (p, _, ports) = Fixture();
        var set   = PlanarCalibration.BuildSet(ports[0], p.Slab, FLo, FHi);
        var delta = set.Skip(1).Select(s => s.LengthM - set[0].LengthM).ToArray();

        var picked = new HashSet<int>();
        foreach (double f in Grid(FLo, FHi, 9))
            picked.Add(PlanarCalibration.SelectSeparation(
                delta, PlanarCalibration.EstimateBeta(p.Slab, f)));

        Assert.True(picked.Count > 1,
            "one separation covered the whole band — the fixture cannot show the selection working");
        _out.WriteLine($"separations used across 1–20 GHz: {string.Join(", ", picked.Order())} " +
                       $"of {delta.Length}");
    }

    [Fact]
    public void T4_ARestartedContinuationStillCostsNoRepeatSolveOfASolvedFrequency()
    {
        // The replay contract from L9e/M1, re-checked under selective solving: a replay re-predicts β
        // from a different neighbour, so it MAY select a separation this frequency has not filled yet
        // and legitimately fill one more mesh. What it must never do is re-fill one it already has,
        // and SolveCount must still count distinct frequencies rather than passes.
        var (p, _, ports) = Fixture();
        var cal = new PlanarPortCalibrator(ports[0], p.Slab, FLo, FHi);
        double[] fs = Grid(FLo, FHi, 5);

        foreach (double f in fs)
            cal.At(() => PlanarFrequencyKernel.FromPair(PlanarLineFixtures.Kernel(p.Slab, f)), f);
        int fillsAfterFirstPass = cal.StandardSolveCount;

        for (int round = 0; round < 3; round++)
        {
            cal.RestartBranchContinuation();
            foreach (double f in fs)
                cal.At(() => PlanarFrequencyKernel.FromPair(PlanarLineFixtures.Kernel(p.Slab, f)), f);
        }

        Assert.Equal(fs.Length, cal.SolveCount);
        Assert.True(cal.StandardSolveCount <= fillsAfterFirstPass + fs.Length,
            $"three replays cost {cal.StandardSolveCount - fillsAfterFirstPass} extra fill(s) over " +
            $"{fs.Length} frequencies — a replay is re-filling meshes it already had");

        _out.WriteLine($"{fs.Length} frequencies, 4 passes: {cal.StandardSolveCount} fills " +
                       $"({fillsAfterFirstPass} on the first pass).");
    }

    [Fact]
    public void T5_ADeembeddedSweepIsUnchangedAndStillDeterministic()
    {
        var (p, m, ports) = Fixture();
        double[] freqs = Grid(2e9, 12e9, 5);

        var a = PlanarSolve.Run(p, m, ports, freqs);
        var b = PlanarSolve.Run(p, m, ports, freqs);

        Assert.Equal(a.Points.Count, b.Points.Count);
        for (int i = 0; i < a.Points.Count; i++)
        {
            var (x, y) = (a.Points[i].S, b.Points[i].S);
            for (int r = 0; r < x.RowCount; r++)
                for (int c = 0; c < x.ColCount; c++)
                {
                    Assert.Equal(x[r, c].Real,      y[r, c].Real);
                    Assert.Equal(x[r, c].Imaginary, y[r, c].Imaginary);
                }
        }
    }
}

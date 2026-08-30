using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Fet;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// brief-hb-p4-sdd-grid-evaluate.md §4 (M4) — the closed-form built-ins on the same whole-grid door
/// as the SDD.
///
/// <para>They gain no vectorised register program from it — their <c>Evaluate</c> is already a direct
/// call — so what the door buys them is the six-arrays-PER-SAMPLE allocation, which becomes four
/// buffers per GRID. That is a refactor of live device physics (<c>Evaluate</c> now allocates and
/// forwards to <c>EvaluateInto</c>), so it is gated the same way the SDD's is: every block at every
/// sample, bit for bit against the per-sample path, on every model. The BJT is the one that most
/// needs it — its assembly writes only the entries it contributes to and relied on the arrays being
/// freshly zeroed, which reused buffers are not.</para>
/// </summary>
public sealed class BuiltInGridEvaluateTests
{
    public static TheoryData<string, ComponentModel> Models() => new()
    {
        { "Curtice",      new CurticeQuadraticFetModel(vto: -2.0, beta: 0.02, lambda: 0.05, alpha: 2.0) },
        { "CurticeCubic", new CurticeCubicFetModel(a0: 0.08, a1: 0.05, a2: 0.01, a3: -0.002,
                                                   gamma: 2.0, beta: 0.02, vds0: 5.0) },
        { "Statz",        new StatzFetModel(vto: -2.0, beta: 0.02, b: 0.3, alpha: 2.0, lambda: 0.05) },
        { "Materka",      new MaterkaFetModel(idss: 0.1, vp0: -2.0, gamma: 0.05, alpha: 2.0) },
        { "Angelov",      new AngelovFetModel(ipk: 0.1, vpk: -1.0, p1: 1.2, p2: 0.1, p3: -0.02,
                                              alpha: 2.0, lambda: 0.05) },
        { "Diode",        new DiodeModel(saturationCurrent: 1e-14, emissionCoefficient: 1.08) },
        { "DiodeRs",      new DiodeModel(saturationCurrent: 1e-14, emissionCoefficient: 1.08, seriesResistance: 2.5) },
        { "NonlinearC",   new NonlinearCModel([1e-12, 2e-13, -5e-14]) },
        { "Bjt",          new BjtModel(saturationCurrent: 1e-16, forwardBeta: 120.0, forwardEarlyVoltage: 30.0) },
    };

    /// <summary>A grid that swings each port across its interesting range — forward and reverse for a
    /// junction, pinch-off through saturation for a FET — so the branch-heavy parts of each law are
    /// compared, not just its smooth interior.</summary>
    private static double[] Grid(int p, int s)
    {
        var v = new double[s];
        for (int t = 0; t < s; t++)
        {
            double u = (double)t / (s - 1);
            v[t] = p == 0 ? -3.0 + 4.0 * u                       // gate / base-emitter sweep
                          : 0.05 + 8.0 * (0.5 - 0.5 * Math.Cos(2.0 * Math.PI * u));
        }
        return v;
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void EvaluateGrid_MatchesPerSampleEvaluate_BitForBit(string name, ComponentModel model)
    {
        const int S = 37;   // not a power of two, and not the shape of any buffer
        int P = model.PortCount;

        var ports = new double[P][];
        var portV = new double[P * S];
        for (int p = 0; p < P; p++)
        {
            ports[p] = Grid(p, S);
            ports[p].CopyTo(portV, p * S);
        }

        Assert.True(model.PrefersGridEvaluate, $"{name}: did not opt into the grid door");

        var into = new GridResult();
        model.EvaluateGrid(portV, [], S, into);
        model.EvaluateGrid(portV, [], S, into);   // twice: the buffers are reused, so stale data would show

        var pv = new double[P];
        for (int t = 0; t < S; t++)
        {
            for (int p = 0; p < P; p++) pv[p] = ports[p][t];
            var r = model.Evaluate(new PortVoltages(pv));

            for (int p = 0; p < P; p++)
            {
                Bits($"{name} t={t} I[{p}]", r.I[p], into.I[into.PortBase(p) + t]);
                Bits($"{name} t={t} Q[{p}]", r.Q[p], into.Q[into.PortBase(p) + t]);
                for (int q = 0; q < P; q++)
                {
                    Bits($"{name} t={t} Dg[{p},{q}]", r.Dg[p, q], into.Dg[into.JacBase(p, q) + t]);
                    Bits($"{name} t={t} Dc[{p},{q}]", r.Dc[p, q], into.Dc[into.JacBase(p, q) + t]);
                }
            }
        }
    }

    /// <summary>The point of M4: the grid call stops allocating per sample.</summary>
    [Theory]
    [MemberData(nameof(Models))]
    public void EvaluateGrid_AllocatesFarLessThanTheSameSamplesOneAtATime(string name, ComponentModel model)
    {
        const int S = 256;
        int P = model.PortCount;
        var portV = new double[P * S];
        for (int p = 0; p < P; p++) Grid(p, S).CopyTo(portV, p * S);

        var into = new GridResult();
        model.EvaluateGrid(portV, [], S, into);   // warm

        long before = GC.GetAllocatedBytesForCurrentThread();
        model.EvaluateGrid(portV, [], S, into);
        long grid = GC.GetAllocatedBytesForCurrentThread() - before;

        var pv = new double[P];
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < S; t++)
        {
            for (int p = 0; p < P; p++) pv[p] = portV[p * S + t];
            model.Evaluate(new PortVoltages(pv));
        }
        long scalar = GC.GetAllocatedBytesForCurrentThread() - before;

        // The grid call still allocates the four per-GRID buffers once; the claim is that it no
        // longer allocates per sample, which at 256 samples is an order of magnitude.
        Assert.True(grid * 10 < scalar,
            $"{name}: grid call allocated {grid} B against {scalar} B for the same {S} samples one at a time");
    }

    private static void Bits(string what, double expected, double actual)
    {
        if (BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual)) return;
        if (double.IsNaN(expected) && double.IsNaN(actual)) return;
        Assert.Fail($"{what}: per-sample {expected:R} vs grid {actual:R}");
    }
}

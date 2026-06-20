using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using RfCore;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Gate tests for NonlinearCModel and PolynomialFit (design §4.1–4.2).
///
///   T1 — PolynomialFit recovers known cubic coefficients from over-determined data.
///   T2 — PolynomialFit matches numpy.polyfit for the varactor reference example.
///   T3 — PolynomialFit guards: mismatched lengths, too few points, singular data.
///   T4 — NonlinearCModel C(V)/Q(V)/Dg/Dc are correct; Q(0)=0; I=0.
///   T5 — Constant-C model: Dc==C0 for all V, Q==C0·V, charge path is linear.
///   T6 — Factory creates NonlinearCModel from C0/C1 params; IsPrimitive returns true.
/// </summary>
public class NonlinearCModelTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PortVoltages Pv(double v) => new([v]);

    private static NonlinearResult Eval(NonlinearCModel m, double vd)
        => m.Evaluate(Pv(vd));

    private static double Analytic_C(double[] c, double v)
    {
        double acc = 0.0;
        for (int k = c.Length - 1; k >= 0; k--) acc = acc * v + c[k];
        return acc;
    }

    private static double Analytic_Q(double[] c, double v)
    {
        double acc = 0.0;
        for (int k = c.Length - 1; k >= 0; k--) acc = acc * v + c[k] / (k + 1);
        return acc * v;
    }

    // ── T1: PolynomialFit recovers known cubic ────────────────────────────────

    [Fact]
    public void T1_PolynomialFit_RecoversKnownCubic()
    {
        double c0 = 1.2e-11, c1 = -3.5e-13, c2 = 4.0e-14, c3 = -2.1e-15;
        double[] known = [c0, c1, c2, c3];

        // Sample at 8 distinct bias points.
        double[] v = [-2, -1, 0, 1, 2, 3, 4, 5];
        double[] c = new double[v.Length];
        for (int i = 0; i < v.Length; i++)
            c[i] = Analytic_C(known, v[i]);

        double[] fit = PolynomialFit.Fit(v, c, 3);

        Assert.Equal(4, fit.Length);
        for (int k = 0; k < 4; k++)
        {
            double relErr = Math.Abs(fit[k] - known[k]) / (Math.Abs(known[k]) + 1e-30);
            Assert.True(relErr < 1e-9, $"C{k}: fit={fit[k]:G12}, known={known[k]:G12}, relErr={relErr:G4}");
        }
    }

    // ── T2: PolynomialFit matches numpy reference for varactor data ───────────

    [Fact]
    public void T2_PolynomialFit_MatchesNumpyVaractorReference()
    {
        // Reference: numpy.polyfit([0,1,2,3,4,5],[10e-12,8.5e-12,6.2e-12,4.1e-12,2.5e-12,1.8e-12],3)[::-1]
        double[] vArr = [0, 1, 2, 3, 4, 5];
        double[] cArr = [10e-12, 8.5e-12, 6.2e-12, 4.1e-12, 2.5e-12, 1.8e-12];

        // Numpy result (lowest-power first after reversal):
        double[] numpyRef =
        [
             1.0024603174603175e-11,
            -1.1604497354497371e-12,
            -5.313492063492051e-13,
             8.703703703703683e-14,
        ];

        double[] fit = PolynomialFit.Fit(vArr, cArr, 3);

        Assert.Equal(4, fit.Length);
        for (int k = 0; k < 4; k++)
        {
            double absDiff = Math.Abs(fit[k] - numpyRef[k]);
            double scale   = Math.Max(Math.Abs(numpyRef[k]), 1e-30);
            Assert.True(absDiff / scale < 1e-9,
                $"C{k}: fit={fit[k]:G14}, numpy={numpyRef[k]:G14}, relErr={absDiff / scale:G4}");
        }
    }

    // ── T3: PolynomialFit guards ──────────────────────────────────────────────

    [Fact]
    public void T3a_PolynomialFit_MismatchedLengths_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PolynomialFit.Fit([0.0, 1.0], [1e-12], 1));
        Assert.Contains("same length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T3b_PolynomialFit_TooFewPoints_Throws()
    {
        // order=3 needs ≥4 points; give only 2.
        var ex = Assert.Throws<ArgumentException>(
            () => PolynomialFit.Fit([0.0, 1.0], [1e-12, 2e-12], 3));
        Assert.Contains("4 points", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T3c_PolynomialFit_DuplicatePoints_Throws()
    {
        // All same V → Vandermonde columns are identical → singular normal matrix.
        Assert.Throws<InvalidOperationException>(
            () => PolynomialFit.Fit([1.0, 1.0, 1.0, 1.0], [1e-12, 2e-12, 3e-12, 4e-12], 3));
    }

    // ── T4: NonlinearCModel C(V)/Q(V)/Dg/Dc/I ────────────────────────────────

    [Fact]
    public void T4_NonlinearCModel_EvaluateReturnsCorrectJacobiansAndCharge()
    {
        double[] coeffs = [1.0e-11, -2.0e-12, 3.0e-13, -1.0e-14];
        var model = new NonlinearCModel(coeffs);

        double[] testVoltages = [-3.0, -1.5, 0.0, 0.5, 2.0, 4.0];
        foreach (double vd in testVoltages)
        {
            var r = Eval(model, vd);

            // I must be 0 (pure capacitor, no conduction).
            Assert.Equal(0.0, r.I[0]);
            // Dg must be 0 (no conductance).
            Assert.Equal(0.0, r.Dg[0, 0]);

            // Dc[0,0] == C(Vd).
            double expectedC = Analytic_C(coeffs, vd);
            Assert.True(Math.Abs(r.Dc[0, 0] - expectedC) < 1e-25,
                $"Dc mismatch at V={vd}: got {r.Dc[0, 0]:G14}, expected {expectedC:G14}");

            // Q[0] == Q(Vd) = Σ Cₖ·Vd^(k+1)/(k+1).
            double expectedQ = Analytic_Q(coeffs, vd);
            Assert.True(Math.Abs(r.Q[0] - expectedQ) < 1e-25,
                $"Q mismatch at V={vd}: got {r.Q[0]:G14}, expected {expectedQ:G14}");
        }

        // Q(0) == 0 exactly.
        Assert.Equal(0.0, Eval(model, 0.0).Q[0]);
    }

    // ── T5: Constant-C ⇒ linear charge path ──────────────────────────────────

    [Fact]
    public void T5_ConstantCapacitance_LinearChargeAndFlatCap()
    {
        const double C0 = 4.7e-12;
        var model = new NonlinearCModel([C0]);

        foreach (double vd in new[] { -5.0, -1.0, 0.0, 1.0, 5.0 })
        {
            var r = Eval(model, vd);

            Assert.Equal(C0, r.Dc[0, 0]);         // flat cap at all voltages
            Assert.Equal(0.0, r.I[0]);
            Assert.Equal(0.0, r.Dg[0, 0]);

            double expectedQ = C0 * vd;
            Assert.True(Math.Abs(r.Q[0] - expectedQ) < 1e-30,
                $"Q={r.Q[0]:G14} expected {expectedQ:G14} at V={vd}");
        }
    }

    // ── T6: Factory creates NonlinearCModel; IsPrimitive true ─────────────────

    [Fact]
    public void T6_Factory_CreatesNonlinearCModel_FromC0C1Params()
    {
        const double c0 = 1e-12, c1 = 2e-13;
        var parameters = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["C0"] = new Value(c0),
            ["C1"] = new Value(c1),
        };

        var model = ComponentModelFactory.TryCreate("NonlinearC", parameters);

        Assert.NotNull(model);
        Assert.IsType<NonlinearCModel>(model);
        Assert.Equal(1, model.PortCount);
        Assert.Equal(ModelKind.Nonlinear, model.Kind);

        // Evaluate at V=1 → C(1) = c0 + c1 = 1.2e-12.
        var r = model.Evaluate(Pv(1.0));
        Assert.True(Math.Abs(r.Dc[0, 0] - (c0 + c1)) < 1e-30,
            $"Dc={r.Dc[0, 0]:G14} expected {c0 + c1:G14}");

        // Absent C2 treated as 0 — Evaluate at V=2 → C(2) = c0 + 2·c1.
        double expected2 = c0 + 2 * c1;
        var r2 = model.Evaluate(Pv(2.0));
        Assert.True(Math.Abs(r2.Dc[0, 0] - expected2) < 1e-30,
            $"Dc at V=2: {r2.Dc[0, 0]:G14} expected {expected2:G14}");

        // IsPrimitive must return true.
        Assert.True(ComponentModelFactory.IsPrimitive("NonlinearC"));
        Assert.True(ComponentModelFactory.IsPrimitive("nonlinearc")); // case-insensitive
    }
}

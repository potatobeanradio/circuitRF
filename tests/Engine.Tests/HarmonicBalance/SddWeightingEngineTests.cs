using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Gate tests for brief-sdd-weighting-engine (brief #2).
/// Validates that the w≥2 bucket path in HbNewton/NonlinearDcEngine is correct:
///   (1) StampLinearized with H[2]=jω produces the same MNA entry as the charge path.
///   (2) EvaluateNonlinear bucket WNl equals the charge-path qNl (same time-domain data).
///   (3) FD-Jacobian oracle: analytic BuildJ for a nonlinear w=2 bucket matches FD(F).
///   (4) DC with H[2]=1 (nonzero at DC) contributes to the DC operating point.
///   (5) DC with H[2]=jω (zero at DC) does NOT contribute to the DC operating point.
/// Brief #1's two facts (Equivalence tests) must stay green throughout.
/// </summary>
public class SddWeightingEngineTests(ITestOutputHelper output)
{
    // ── Stub models ────────────────────────────────────────────────────────────

    // 1-port, w=2 bucket with H[2]=jω. Linear C*v equivalent of the charge (w=1) path.
    private sealed class BucketJomegaModel(double c) : ComponentModel
    {
        public override int       PortCount => 1;
        public override ModelKind Kind      => ModelKind.Nonlinear;
        public override Complex   Weight(int w, double omega)
            => w == 2 ? new Complex(0, omega) : base.Weight(w, omega);
        public override NonlinearResult Evaluate(in PortVoltages v)
        {
            double vp  = v[0];
            var    jac = new double[1, 1]; jac[0, 0] = c;
            return new NonlinearResult([0.0], [0.0], new double[1, 1], new double[1, 1],
                [new WeightedTerm(2, [c * vp], jac)]);
        }
    }

    // 1-port standard charge model (w=1 path) — equivalence reference.
    private sealed class ChargeModel(double c) : ComponentModel
    {
        public override int       PortCount => 1;
        public override ModelKind Kind      => ModelKind.Nonlinear;
        public override NonlinearResult Evaluate(in PortVoltages v)
        {
            double vp = v[0];
            var    dc = new double[1, 1]; dc[0, 0] = c;
            return new NonlinearResult([0.0], [c * vp], new double[1, 1], dc);
        }
    }

    // 1-port, w=2 bucket with quadratic nonlinear Jac — for the FD oracle test.
    private sealed class NonlinearBucketModel(double g, double g2) : ComponentModel
    {
        public override int       PortCount => 1;
        public override ModelKind Kind      => ModelKind.Nonlinear;
        public override Complex   Weight(int w, double omega)
            => w == 2 ? new Complex(0, omega) : base.Weight(w, omega);
        public override NonlinearResult Evaluate(in PortVoltages v)
        {
            double vp  = v[0];
            double val = g * vp + g2 * vp * vp;
            double dv  = g + 2 * g2 * vp;
            var    jac = new double[1, 1]; jac[0, 0] = dv;
            return new NonlinearResult([0.0], [0.0], new double[1, 1], new double[1, 1],
                [new WeightedTerm(2, [val], jac)]);
        }
    }

    // 1-port, w=2 bucket with H[2]=1 (constant — nonzero at DC).
    private sealed class BucketConstantModel(double g) : ComponentModel
    {
        public override int       PortCount => 1;
        public override ModelKind Kind      => ModelKind.Nonlinear;
        public override Complex   Weight(int w, double omega)
            => w == 2 ? Complex.One : base.Weight(w, omega);
        public override NonlinearResult Evaluate(in PortVoltages v)
        {
            double vp  = v[0];
            var    jac = new double[1, 1]; jac[0, 0] = g;
            return new NonlinearResult([0.0], [0.0], new double[1, 1], new double[1, 1],
                [new WeightedTerm(2, [g * vp], jac)]);
        }
    }

    // ── Netlist builder helpers ─────────────────────────────────────────────────

    // Minimal 1-node nonlinear netlist: stub from n1 to gnd.
    private static (ElaboratedNetlist nl, int[] ifNodes) Build1PortNl(ComponentModel model)
    {
        var nl = new ElaboratedNetlist();
        int n1 = nl.Nodes.GetOrAssign("n1");
        nl.AddComponent(new ElaboratedComponent("Stub", "S1",
            [n1, 0], new Dictionary<string, Value>(), model));
        return (nl, [n1]);
    }

    // DC netlist: Vdc=2V at nd, R between nd and n1, stub from n1 to gnd.
    private static ElaboratedNetlist BuildDcNl(ComponentModel stub, double r = 100.0)
    {
        var nl = new ElaboratedNetlist();
        int nd = nl.Nodes.GetOrAssign("nd");
        int n1 = nl.Nodes.GetOrAssign("n1");

        nl.AddComponent(new ElaboratedComponent("Vdc", "V1", [nd, 0],
            new Dictionary<string, Value> { ["Vdc"] = new Value(2.0) }, new VdcModel()));

        nl.AddComponent(new ElaboratedComponent("R", "R1", [nd, n1],
            new Dictionary<string, Value> { ["R"] = new Value(r) }, new ResistorModel()));

        nl.AddComponent(new ElaboratedComponent("Stub", "S1", [n1, 0],
            new Dictionary<string, Value>(), stub));

        return nl;
    }

    // ── Test 1: StampLinearized ────────────────────────────────────────────────

    /// <summary>
    /// A 1-port bucket model with H[2]=jω and Jac=C must stamp the same MNA entry
    /// as a standard charge model with Dc=C — the two paths produce identical jω·C.
    /// </summary>
    [Fact]
    public void StampLinearized_BucketH2_JomegaEqualsChargeStamp()
    {
        const double C = 1e-12;
        double omega = 2 * Math.PI * 1e9;
        var bias = new PortVoltages([0.0]);

        var (nlBucket, _) = Build1PortNl(new BucketJomegaModel(C));
        var (nlCharge, _) = Build1PortNl(new ChargeModel(C));

        var mnaBucket = new MnaSystem(1);
        mnaBucket.Reset();
        nlBucket.Components[0].Model.StampLinearized(mnaBucket, nlBucket.Components[0], omega, bias);
        Complex yBucket = mnaBucket.GetEntry(0, 0);

        var mnaCharge = new MnaSystem(1);
        mnaCharge.Reset();
        nlCharge.Components[0].Model.StampLinearized(mnaCharge, nlCharge.Components[0], omega, bias);
        Complex yCharge = mnaCharge.GetEntry(0, 0);

        output.WriteLine($"Bucket  Y[0,0] = {yBucket}");
        output.WriteLine($"Charge  Y[0,0] = {yCharge}");

        double err = (yBucket - yCharge).Magnitude;
        Assert.True(err < 1e-20,
            $"StampLinearized: bucket Y={yBucket} vs charge Y={yCharge} err={err:E3}");
    }

    // ── Test 2: HB EvaluateNonlinear spectrum equivalence ─────────────────────

    /// <summary>
    /// A bucket with H[2]=jω accumulates the same time-domain signal as Q (charge),
    /// so WNl[n,k] must equal qNl[n,k] from the charge model for all harmonics.
    /// </summary>
    [Fact]
    public void HB_BucketH2_WNl_EqualsChargePathQNl()
    {
        const double C   = 10e-12;
        const int    K   = 3;
        int          gridN = HbFft.GridSize(K, 1);
        const int    N   = 1;

        var (nlBucket, ifNodes) = Build1PortNl(new BucketJomegaModel(C));
        var (nlCharge, _)       = Build1PortNl(new ChargeModel(C));

        var V = new Complex[N, K + 1];
        V[0, 1] = new Complex(1.0, 0);  // 1V fundamental

        var (_, _, _, _, buckets) = HbNewton.EvaluateNonlinear(V, N, K, gridN, nlBucket, ifNodes);
        var (_, qNl, _, _, _)     = HbNewton.EvaluateNonlinear(V, N, K, gridN, nlCharge, ifNodes);

        Assert.Single(buckets);
        Assert.Equal(2, buckets[0].W);

        output.WriteLine("k   WNl                     qNl                     err");
        for (int k = 0; k <= K; k++)
        {
            Complex wk  = buckets[0].WNl[0, k];
            Complex qk  = qNl[0, k];
            double  err = (wk - qk).Magnitude;
            output.WriteLine($"{k}  {wk,24:G6}  {qk,24:G6}  {err:E2}");
            Assert.True(err < 1e-24,
                $"WNl vs qNl mismatch at k={k}: WNl={wk:G6} qNl={qk:G6} err={err:E3}");
        }
    }

    // ── Test 3: FD Jacobian oracle ─────────────────────────────────────────────

    /// <summary>
    /// The analytic Jacobian (BuildJ) for a nonlinear w=2 bucket model must match
    /// a central-difference Jacobian of BuildF to within 1e-4 relative tolerance
    /// (same gate as the single-tone JacobianFd test).
    /// </summary>
    [Fact]
    public void HB_FdJacobian_NonlinearBucketW2_MaxRelError_LessThan1e4()
    {
        const int    K     = 3;
        int          gridN = HbFft.GridSize(K, 1);
        const int    N     = 1;
        const double f0    = 1e9;

        var (nl, ifNodes) = Build1PortNl(new NonlinearBucketModel(g: 1e-3, g2: 0.5e-3));

        // Fully-populated, modest operating point — EVERY harmonic up to K carries signal,
        // exactly as HbJacobian2DTests drives its FET point. This matters for the FD oracle:
        // the central-difference of BuildF can only validate an entry that is *physically nonzero*.
        // If the top harmonic (V[0,3]) were left at 0, the quadratic's couplings into F[0,3]
        // (∂/∂V_DC, ∂/∂Im V[0,3], …) would be structurally zero, and FD — which compares two
        // F values dominated by the g·3ω₀≈1.9e7 self-term — can't resolve a true-zero entry from
        // its own roundoff floor (it wanders ~1/eps). Populating V[0,3] makes those couplings real
        // and FD-exact (the model is quadratic, so central differences are exact up to roundoff).
        // Small harmonics keep |v(t)| bounded so the operating point stays gentle.
        var V = new Complex[N, K + 1];
        V[0, 0] = new Complex(0.05,  0);
        V[0, 1] = new Complex(0.10,  0);
        V[0, 2] = new Complex(0.01,  0.005);
        V[0, 3] = new Complex(0.008, 0.003);

        // No linear network (pure nonlinear node); yNN and iSrc are zero.
        var yNN  = Enumerable.Range(0, K + 1).Select(_ => new Complex[N, N]).ToArray();
        var iSrc = Enumerable.Range(0, K + 1).Select(_ => new Complex[N]).ToArray();

        var cmp = HbNewton.CompareJacobianNumerical(V, yNN, iSrc, f0, K, N, nl, ifNodes, gridN);

        output.WriteLine($"FD oracle: MaxRelError={cmp.MaxRelError:E3}  MaxAbsError={cmp.MaxAbsError:E3}");
        output.WriteLine($"  DOF={cmp.Dof}  DC-dummy excluded={cmp.DcDummyCount}");
        output.WriteLine($"  MaxRelErr at [{cmp.MaxRelRow},{cmp.MaxRelCol}]  MaxAbsErr at [{cmp.MaxAbsRow},{cmp.MaxAbsCol}]");
        foreach (var d in cmp.TopDiscrepancies.Take(5))
            output.WriteLine(
                $"    [{d.Row},{d.Col}] k={d.RowHarm}{(d.RowIsIm ? 'I' : 'R')} i={d.ColHarm}{(d.ColIsIm ? 'I' : 'R')}" +
                $"  an={d.AnalyticVal:E4}  fd={d.FdVal:E4}  rel={d.RelError:E3}  {d.BlockDesc}");

        // Gate 1e-4 — same as HbJacobian2DTests. The analytic Jacobian is exact for this
        // (quadratic) model; the residual error is the FD oracle's own floor. Note the
        // reactive self-block diagonals at the top harmonic (∂Re F[0,3]/∂Re V[0,3] and the
        // Im/Im twin) are *structural zeros*: with H[2]=jω the whole self-coupling rotates
        // into the off-diagonal Re↔Im block, so FD reads only roundoff there. The oracle's
        // domFloor (globalScale·1e-7) correctly treats those as zero rather than flagging
        // them as Jacobian errors — see CompareJacobianNumerical.
        Assert.True(cmp.MaxRelError < 1e-4,
            $"FD Jacobian oracle exceeded gate: MaxRelError={cmp.MaxRelError:E3} > 1e-4");
    }

    // ── Test 4: DC with H[2]=1 ─────────────────────────────────────────────────

    /// <summary>
    /// With H[2]=1 (constant), the bucket contributes conductance G at DC.
    /// Circuit: Vdc=2V → R=100Ω → n1 → stub(G=0.1S) → gnd.
    /// v(n1) = 2 * G_R / (G_R + G) = 2 * 0.01 / 0.11 = 2/11.
    /// </summary>
    [Fact]
    public void Dc_BucketH2_ConstantWeight_ContributesAtDc()
    {
        const double G_stub = 0.1;    // S
        const double R      = 100.0;  // Ω
        const double Vdc    = 2.0;    // V

        var    nl     = BuildDcNl(new BucketConstantModel(G_stub), r: R);
        var    result = NonlinearDcEngine.Run(nl);

        // NodeVoltages: [v(nd), v(n1)]
        double v_nd = result.NodeVoltages[0];
        double v_n1 = result.NodeVoltages[1];
        double G_R  = 1.0 / R;
        double expected = Vdc * G_R / (G_R + G_stub);  // ≈ 0.18182V

        output.WriteLine($"v(nd)={v_nd:G8}  v(n1)={v_n1:G8}  expected={expected:G8}  converged={result.Converged}");
        Assert.True(result.Converged, "DC solver must converge");
        Assert.True(Math.Abs(v_n1 - expected) < 1e-9,
            $"v(n1) expected {expected:G8} but got {v_n1:G8} (err={Math.Abs(v_n1 - expected):E3})");
    }

    // ── Test 5: DC with H[2]=jω ────────────────────────────────────────────────

    /// <summary>
    /// With H[2]=jω, the bucket contributes 0 at DC (Weight(2,0)=0).
    /// The stub acts as an open circuit at DC → v(n1) ≈ Vdc (pulled up through R,
    /// only gmin holds it down, so v(n1) ≈ 2.0V within gmin tolerance).
    /// </summary>
    [Fact]
    public void Dc_BucketH2_JomegaWeight_ZeroAtDc()
    {
        const double C   = 10e-12;
        const double R   = 100.0;
        const double Vdc = 2.0;

        var    nl     = BuildDcNl(new BucketJomegaModel(C), r: R);
        var    result = NonlinearDcEngine.Run(nl);

        double v_n1 = result.NodeVoltages[1];

        output.WriteLine($"v(n1)={v_n1:G8}  converged={result.Converged}");
        Assert.True(result.Converged, "DC solver must converge");
        Assert.True(Math.Abs(v_n1 - Vdc) < 1e-4,
            $"With H[2]=jω (zero at DC), v(n1) must be ≈ {Vdc:G4}V but got {v_n1:G8}");
    }
}

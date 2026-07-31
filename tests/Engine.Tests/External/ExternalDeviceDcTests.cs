using CircuitRF.Core;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// The external-device gate: a descriptor-driven device solved by circuitRF's own engine, checked
/// against a closed-form oracle that never touches the matrix.
///
/// <para>Every number asserted here is derived independently in <see cref="SolveOracle"/> — a scalar
/// fixed-point solve of the same four equations by hand. A test that only asserted "it converged"
/// would pass on a device that silently conducts nothing, which is exactly the failure mode this
/// path is prone to.</para>
/// </summary>
[Collection("ExternalDeviceRegistry")]
public class ExternalDeviceDcTests : IDisposable
{
    private const string Provider = "synthetic";

    public ExternalDeviceDcTests()
    {
        ExternalDeviceRegistry.Clear();
        ExternalDeviceRegistry.Register(new SquareLawFetProvider(Provider));
    }

    public void Dispose() => ExternalDeviceRegistry.Clear();

    // ── The oracle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Closed-form DC operating point for the fixture with gate driven from a voltage source,
    /// drain at Vdd, source grounded and the thermal pin left open.
    ///
    /// At the solution the four node equations reduce to a scalar system:
    ///   gate is DC-open   → V(gateInt)   = Vg
    ///   source-int KCL    → V(sourceInt) = Id·Rs
    ///   thermal KCL       → T            = Id·Vds_int·Rth
    ///   channel           → Id           = β(Vgs_int − Vth(T))²(1 + λ·Vds_int)
    /// Solved here by damped fixed-point iteration on Id — no matrices, no engine code.
    /// </summary>
    private static (double Id, double Vsi, double T, double VdsInt) SolveOracle(
        SquareLawFetProvider.Params p, double vg, double vdd)
    {
        double id = 0.0, vsi = 0.0, t = 0.0, vds = vdd;
        bool converged = false;
        for (int k = 0; k < 2_000_000 && !converged; k++)
        {
            vsi = id * p.Rs;
            vds = vdd - vsi;
            t   = id * vds * p.Rth;
            var (idNew, _, _, _) = SquareLawFetProvider.Channel(p, vg - vsi, vds, t);
            double step = 0.02 * (idNew - id);              // damping: the loop is strongly coupled
            id += step;
            converged = Math.Abs(step) <= 1e-15 * Math.Max(1.0, Math.Abs(id));
        }
        Assert.True(converged, "The oracle itself did not converge — fix the oracle, not the engine.");
        return (id, vsi, t, vds);
    }

    private static string Netlist(double vg, double vdd, SquareLawFetProvider.Params p, bool thermalOpen = true)
        => $@"
Vdc:VG   g 0  Vdc={vg.ToString(System.Globalization.CultureInfo.InvariantCulture)}
Vdc:VD   d 0  Vdc={vdd.ToString(System.Globalization.CultureInfo.InvariantCulture)}
ExtDevice:X1  g d 0 {(thermalOpen ? "tj" : "0")}  Provider={Provider} Type={SquareLawFetProvider.TypeName} " +
           $@"Beta={p.Beta} Vth0={p.Vth0} Lambda={p.Lambda} Rg={p.Rg} Rs={p.Rs} Rth={p.Rth} Ktv={p.Ktv}
";

    private static (NonlinearDcEngine.DcResult Result, ElaboratedNetlist Netlist) Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return (NonlinearDcEngine.Run(nl), nl);
    }

    /// <summary>
    /// Compare an engine result against the oracle at a tolerance both genuinely meet.
    /// NonlinearDcEngine stops at AbsTol = 1e-6 on the residual norm, so node voltages carry roughly
    /// that much slack scaled by the local conductance; asserting tighter tests the solver's
    /// stopping rule rather than its correctness.
    /// </summary>
    private static void AssertClose(double expected, double actual, string what)
    {
        double tol = 1e-6 * Math.Max(1.0, Math.Abs(expected));
        Assert.True(Math.Abs(expected - actual) < tol,
            $"{what}: oracle {expected:G12}, engine {actual:G12} (tolerance {tol:G3})");
    }

    private static double NodeV(NonlinearDcEngine.DcResult r, ElaboratedNetlist nl, string name)
    {
        int idx = nl.Nodes.GetOrAssign(name);
        return idx == 0 ? 0.0 : r.NodeVoltages[idx - 1];
    }

    // ── T1: descriptor drives everything ──────────────────────────────────────

    [Fact]
    public void Descriptor_DrivesNodeAllocation_ExternalPinsAndInternalNodes()
    {
        var p = new SquareLawFetProvider.Params(0.05, 1.0, 0.02, 10.0, 1.0, 5.0, 0.0);
        var (_, nl) = Run(Netlist(3.0, 10.0, p));

        var ext = nl.Components.Single(c => c.Model is ExternalDeviceModel);
        var m   = (ExternalDeviceModel)ext.Model;

        // One port per node, laid out as ground-referenced pairs.
        Assert.Equal(SquareLawFetProvider.NodeCount, m.PortCount);
        Assert.Equal(SquareLawFetProvider.NodeCount * 2, ext.Nodes.Length);
        for (int k = 0; k < m.PortCount; k++)
            Assert.Equal(0, ext.Nodes[2 * k + 1]);

        // The two internal nodes were minted, are distinct, and are real non-ground nodes.
        int gi = ext.Nodes[2 * SquareLawFetProvider.GateInt];
        int si = ext.Nodes[2 * SquareLawFetProvider.SourceInt];
        Assert.True(gi > 0 && si > 0);
        Assert.NotEqual(gi, si);
        Assert.Contains("__extdev_X1_n4", nl.Nodes.AllNames);
        Assert.Contains("__extdev_X1_n5", nl.Nodes.AllNames);
    }

    // ── T2: isothermal operating point vs the oracle ──────────────────────────

    [Theory]
    [InlineData(2.0, 10.0)]
    [InlineData(3.0, 10.0)]
    [InlineData(3.0, 28.0)]
    [InlineData(4.5, 28.0)]
    public void Isothermal_OperatingPoint_MatchesClosedForm(double vg, double vdd)
    {
        var p = new SquareLawFetProvider.Params(0.05, 1.0, 0.02, 10.0, 1.0, 5.0, Ktv: 0.0);
        var (r, nl) = Run(Netlist(vg, vdd, p));
        Assert.True(r.Converged, $"DC did not converge (residual {r.FinalResidual:G3}).");

        var (idRef, vsiRef, _, _) = SolveOracle(p, vg, vdd);

        double vsi = NodeV(r, nl, "__extdev_X1_n5");
        double vgi = NodeV(r, nl, "__extdev_X1_n4");

        AssertClose(vsiRef, vsi, "V(sourceInt)");
        Assert.Equal(vg, vgi, 8);                           // gate is DC-open → no drop across Rg
        AssertClose(idRef, vsi / p.Rs, "Id");               // Id read straight off the internal node
        Assert.True(idRef > 1e-4, $"Oracle current {idRef:G4} A is too small to be a real test.");
    }

    // ── T3: self-heating closes through the solver ────────────────────────────

    [Fact]
    public void SelfHeating_ThermalNodeIsSolved_AndDeratesTheCurrent()
    {
        var cold = new SquareLawFetProvider.Params(0.05, 1.0, 0.02, 10.0, 1.0, 5.0, Ktv: 0.0);
        var hot  = cold with { Ktv = 0.02 };                 // Vth rises 20 mV per degC

        var (rc, nc) = Run(Netlist(3.0, 28.0, cold));
        var (rh, nh) = Run(Netlist(3.0, 28.0, hot));
        Assert.True(rc.Converged && rh.Converged);

        double idCold = NodeV(rc, nc, "__extdev_X1_n5") / cold.Rs;
        double idHot  = NodeV(rh, nh, "__extdev_X1_n5") / hot.Rs;
        double tj     = NodeV(rh, nh, "tj");
        double vds    = 28.0 - NodeV(rh, nh, "__extdev_X1_n5");

        // The thermal pin is externally OPEN — its voltage is produced entirely by the device's own
        // internal Rth balancing the power it delivers. That identity is exact at the solution, so
        // the only slack is the solver's own convergence: NonlinearDcEngine stops at AbsTol=1e-6 on
        // the residual, which at this node is watts × Rth. Assert relative to that, not tighter.
        double thermalIdentity = idHot * vds * hot.Rth;
        Assert.True(Math.Abs(tj - thermalIdentity) < 1e-6 * Math.Max(1.0, Math.Abs(tj)),
            $"Thermal identity Tj = Id·Vds·Rth broken: Tj={tj:G12}, Id·Vds·Rth={thermalIdentity:G12}");
        Assert.True(tj > 1.0, $"Junction temperature {tj:G4} did not move; self-heating is inert.");

        // A positive threshold coefficient must reduce the current, and match the oracle.
        var (idRef, _, tRef, _) = SolveOracle(hot, 3.0, 28.0);
        AssertClose(idRef, idHot, "Id (self-heated)");
        AssertClose(tRef,  tj,    "junction temperature");
        Assert.True(idHot < idCold,
            $"Self-heating did not derate the current (cold {idCold:G4} A, hot {idHot:G4} A).");
    }

    // ── T4: the Jacobian the provider reports is the real one ─────────────────

    [Fact]
    public void ReportedJacobian_MatchesFiniteDifference()
    {
        var p = new SquareLawFetProvider.Params(0.05, 1.0, 0.02, 10.0, 1.0, 5.0, Ktv: 0.02);
        var provider = new SquareLawFetProvider();
        using var inst = provider.Create(SquareLawFetProvider.TypeName, new Dictionary<string, string>
        {
            ["Beta"] = "0.05", ["Vth0"] = "1.0", ["Lambda"] = "0.02",
            ["Rg"] = "10.0", ["Rs"] = "1.0", ["Rth"] = "5.0", ["Ktv"] = "0.02",
        });

        int n = SquareLawFetProvider.NodeCount;
        double[] v = [3.0, 28.0, 0.0, 40.0, 3.0, 0.35];      // a well-inside-saturation point
        var baseline = inst.Evaluate(v);

        const double h = 1e-6;
        for (int col = 0; col < n; col++)
        {
            var vp = (double[])v.Clone(); vp[col] += h;
            var vm = (double[])v.Clone(); vm[col] -= h;
            var rp = inst.Evaluate(vp);
            var rm = inst.Evaluate(vm);
            for (int row = 0; row < n; row++)
            {
                double fd = (rp.Current[row] - rm.Current[row]) / (2 * h);
                double an = baseline.Conductance[row, col];
                double tol = 1e-6 * Math.Max(1.0, Math.Abs(an)) + 1e-6;
                Assert.True(Math.Abs(fd - an) < tol,
                    $"dI[{row}]/dV[{col}]: analytic {an:G10}, finite-difference {fd:G10}");
            }
        }
    }

    // ── T5: passive sign convention, asserted rather than assumed ─────────────

    [Fact]
    public void SignConvention_IsPassive_CurrentIntoTheDevice()
    {
        var p = new SquareLawFetProvider.Params(0.05, 1.0, 0.02, 10.0, 1.0, 5.0, 0.0);
        var (r, nl) = Run(Netlist(3.0, 28.0, p));

        var ext  = nl.Components.Single(c => c.Model is ExternalDeviceModel);
        var m    = (ExternalDeviceModel)ext.Model;
        var pv   = new double[m.PortCount];
        for (int k = 0; k < m.PortCount; k++) pv[k] = NodeVByIndex(r, ext.Nodes[2 * k]);
        var res  = m.Evaluate(new PortVoltages(pv));

        // Drain current is positive INTO the device; source current is negative (it flows out).
        Assert.True(res.I[SquareLawFetProvider.Drain] > 0,
            $"Drain current {res.I[SquareLawFetProvider.Drain]:G4} A should be positive into the device.");
        Assert.True(res.I[SquareLawFetProvider.Source] < 0,
            $"Source current {res.I[SquareLawFetProvider.Source]:G4} A should be negative (out of the device).");

        // Every node's current sums to zero apart from the thermal port, which carries watts.
        double sum = 0;
        for (int k = 0; k < m.PortCount; k++)
            if (k != SquareLawFetProvider.Thermal) sum += res.I[k];
        Assert.Equal(0.0, sum, 8);

        static double NodeVByIndex(NonlinearDcEngine.DcResult rr, int idx)
            => idx == 0 ? 0.0 : rr.NodeVoltages[idx - 1];
    }

    // ── T6: failure modes are distinguishable and actionable ──────────────────

    [Fact]
    public void UnregisteredProvider_FailsWithAnActionableMessage()
    {
        ExternalDeviceRegistry.Clear();
        var ex = Assert.Throws<ExternalDeviceException>(() =>
            Run(Netlist(3.0, 10.0, new SquareLawFetProvider.Params(0.05, 1, 0.02, 10, 1, 5, 0))));
        Assert.Contains("no providers are registered", ex.Message);
    }

    [Fact]
    public void UnknownDeviceType_NamesWhatIsAvailable()
    {
        var ex = Assert.Throws<ExternalDeviceException>(() => Run($@"
ExtDevice:X1  g d 0 tj  Provider={Provider} Type=NoSuchType
"));
        Assert.Contains("NoSuchType", ex.Message);
        Assert.Contains(SquareLawFetProvider.TypeName, ex.Message);
    }

    [Fact]
    public void WrongPinCount_IsRejectedAtElaboration()
    {
        var ex = Assert.Throws<ExternalDeviceException>(() => Run($@"
ExtDevice:X1  g d 0  Provider={Provider} Type={SquareLawFetProvider.TypeName}
"));
        Assert.Contains("4", ex.Message);
        Assert.Contains("3 nets", ex.Message);
    }
}

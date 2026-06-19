using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Nonlinear;

/// <summary>
/// Gate tests for IProbe branch-current extraction on DcResult.ProbeCurrents
/// and the shared DcResultPacker (brief-dc-iprobe-currents).
/// </summary>
public class IProbeCurrentTests(ITestOutputHelper output)
{
    // ── T1: resistor + IProbe in series with DC voltage source ───────────────
    //   Vdc -- IProbe:IP1 -- R:R1 -- GND
    //   Expected: I(IP1) = Vdc / R = 10 / 100 = 0.1 A  (np→nm sign: np=n1, nm=n2)

    private const string SeriesIprobeCnl = @"
Vdc:Vs  n1 0  Vdc=10 V
IProbe:IP1  n1 n2
R:R1  n2 0  R=100 Ohm

analysis DC1  type=dc
";

    [Fact]
    public void IProbe_SeriesResistor_CurrentEqualsVOverR()
    {
        var (lib, tb) = new CnlReader().Read(SeriesIprobeCnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);

        Assert.True(dc.Converged, "DC should converge for a linear circuit.");
        output.WriteLine($"ProbeCurrents count: {dc.ProbeCurrents.Count}");
        Assert.Single(dc.ProbeCurrents);
        Assert.True(dc.ProbeCurrents.ContainsKey("IP1"), "ProbeCurrents should contain 'IP1'.");

        double iProbe = dc.ProbeCurrents["IP1"];
        output.WriteLine($"I(IP1) = {iProbe:G6} A  (expected 0.1 A)");
        Assert.True(Math.Abs(iProbe - 0.1) < 1e-9,
            $"Expected I(IP1) ≈ 0.1 A, got {iProbe:G}");

        output.WriteLine("IProbe_SeriesResistor_CurrentEqualsVOverR: PASS.");
    }

    // ── T2: DcResultPacker emits unified I [branch] cube with IP1 labeled ──────

    [Fact]
    public void DcResultPacker_EmitsIProbeCube()
    {
        var (lib, tb) = new CnlReader().Read(SeriesIprobeCnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);

        var ds = DcResultPacker.Pack(dc, nl);

        Assert.True(ds.Contains("V"),          "DataSet must contain 'V' cube.");
        Assert.True(ds.Contains("Converged"),  "DataSet must contain 'Converged' cube.");
        Assert.True(ds.Contains("Residual"),   "DataSet must contain 'Residual' cube.");
        Assert.True(ds.Contains("I"),          "DataSet must contain unified 'I' cube.");

        // I is rank-1 with a branch axis labeled with probe names.
        var iCube = ds["I"];
        Assert.Equal(1, iCube.Rank);
        Assert.Equal("branch", iCube.Axes[0].Name);
        var labels = iCube.Axes[0].Labels;
        Assert.NotNull(labels);
        Assert.Contains("IP1", labels!);

        int brIdx = Array.IndexOf(labels!, "IP1");
        double iVal = iCube.RealValues[brIdx];
        output.WriteLine($"ds[\"I\"][IP1] = {iVal:G6} A");
        Assert.True(Math.Abs(iVal - 0.1) < 1e-9,
            $"Expected I[IP1] ≈ 0.1 A, got {iVal:G}");

        // V cube has 1-axis (node) with Labels.
        var vCube = ds["V"];
        Assert.Equal(1, vCube.Rank);
        Assert.Equal("node", vCube.Axes[0].Name);
        Assert.NotNull(vCube.Axes[0].Labels);

        output.WriteLine("DcResultPacker_EmitsIProbeCube: PASS.");
    }

    // ── T3: no IProbe → ProbeCurrents is empty, no I: cube ──────────────────

    private const string NoIprobeCnl = @"
Vdc:Vs  n1 0  Vdc=5 V
R:R1  n1 0  R=50 Ohm

analysis DC1  type=dc
";

    [Fact]
    public void NoIProbe_ProbeCurrentsIsEmpty()
    {
        var (lib, tb) = new CnlReader().Read(NoIprobeCnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);

        Assert.True(dc.Converged, "DC should converge.");
        Assert.Empty(dc.ProbeCurrents);

        var ds = DcResultPacker.Pack(dc, nl);
        Assert.False(ds.Contains("I"),
            "No I cube expected when there are no IProbes.");

        output.WriteLine("NoIProbe_ProbeCurrentsIsEmpty: PASS.");
    }
}

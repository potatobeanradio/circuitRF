using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// Gate tests for VdcModel (DC voltage source) and the V:→Vdc backward-compat remap.
/// Tests 1–4 verify stamping and DC correctness; Test 6 verifies the 0-Hz tone warning.
/// </summary>
public class VdcComponentTests
{
    // Shared helper: elaborate → run DC → return named node voltage.
    private static double GetNodeVoltage(string cnl, string nodeName)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl        = new Elaborator(lib).Elaborate(tb);
        var result    = NonlinearDcEngine.Run(nl);
        int nodeNum   = nl.Nodes.GetOrAssign(nodeName);
        // NodeVoltages is 0-based with node 0 = ground excluded.
        return result.NodeVoltages[nodeNum - 1];
    }

    // ── Test 1: Vdc_Stamped ──────────────────────────────────────────────────

    [Fact]
    public void Vdc_Stamped_DcNodeEqualsSupply()
    {
        const string cnl = """
            Vdc:V1  n1 0  Vdc=48
            R:R1    n1 0  R=1000
            """;
        double v = GetNodeVoltage(cnl, "n1");
        Assert.Equal(48.0, v, precision: 3);
    }

    // ── Test 2: Vdc_IsAcShort ───────────────────────────────────────────────

    [Fact]
    public void Vdc_IsAcShort_SParamRunsWithoutError()
    {
        // Vdc acts as AC short (stamps 0 at ω>0). A 50-Ω Term with Vdc-tied node
        // still gives a valid, non-NaN S11 result.
        const string cnl = """
            Term:Term1  n1 0  Num=1 Z=50
            R:R1        n1 n2 R=50
            Vdc:V1      n2 0  Vdc=5
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var ds  = SParameterEngine.Run(nl, [1e9]);
        var s11 = (System.Numerics.Complex)ds["S"][0, 0, 0];
        Assert.False(double.IsNaN(s11.Real), "S11 should not be NaN");
    }

    // ── Test 3: LegacyV_RemapsToVdc ─────────────────────────────────────────

    [Fact]
    public void LegacyV_RemapsToVdc_DcNodeEqualsSupply()
    {
        // "V:V1 n1 0 Vac=48" uses the legacy type code; CnlReader remaps to Vdc.
        const string cnl = """
            V:V1  n1 0  Vac=48
            R:R1  n1 0  R=1000
            """;
        double v = GetNodeVoltage(cnl, "n1");
        Assert.Equal(48.0, v, precision: 3);
    }

    // ── Test 4: NetlistRepro ─────────────────────────────────────────────────

    [Fact]
    public void NetlistRepro_BiasedNodes_HaveCorrectDcVoltages()
    {
        // Reproduces the original bug: Vout DC = 0.00 when V: sources were used.
        const string cnl = """
            Vdc:Vg     n_gate  0  Vdc=-3.05
            Vdc:Vd     n_drain 0  Vdc=48
            R:Rg       n_gate  0  R=1e6
            R:Rd       n_drain 0  R=1e3
            """;
        double vGate  = GetNodeVoltage(cnl, "n_gate");
        double vDrain = GetNodeVoltage(cnl, "n_drain");
        Assert.InRange(vGate,  -3.06, -3.04);
        Assert.InRange(vDrain,  47.9,  48.1);
    }

    // ── Test 6: ToneSource_ZeroHzToneWarns ──────────────────────────────────

    [Fact]
    public void ToneSource_ZeroHzToneWarns_AndSuperposesIntoVdc()
    {
        // V_1Tone with Freq=0 → warning emitted + amplitude added to DC at ω=0.
        const string cnl = """
            V_1Tone:V1  n1 0  V=2  Freq=0  Vdc=1
            R:R1        n1 0  R=1000
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        // Warning must flag the zero-frequency tone.
        Assert.Contains(nl.Warnings, w => w.Contains("Freq=0") && w.Contains("Vdc"));

        // DC node voltage should be Vdc(1) + V_tone(2) = 3 V.
        var result = NonlinearDcEngine.Run(nl);
        double v   = result.NodeVoltages[nl.Nodes.GetOrAssign("n1") - 1];
        Assert.InRange(v, 2.9, 3.1);
    }
}

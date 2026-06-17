using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// Gate tests for the SDD default I[x,0]=_vx/50 parameters (Part 3 of
/// brief-p1tone-num-sddx-defaults). A freshly-placed SDD with default params
/// must be functional: it acts as N independent 50 Ω conductances and can be
/// run through S-parameter analysis without exception.
/// </summary>
public class SddDefaultParamsTests
{
    // ── T1: 2-port SDD with default params runs S-param without exception ───────

    [Fact]
    public void SddDefaultParams_2Port_SParamRunsWithoutException()
    {
        // CNL equivalent of a freshly-placed 2-port SDD: NumPorts=2,
        // I[1,0]=_v1/50, I[2,0]=_v2/50 (each port is a 50 Ω conductance).
        // Term:T1 and Term:T2 are the S-param ports.
        const string cnl = @"
Term:T1  n1 0  Num=1 Z=50 Ohm
Term:T2  n2 0  Num=2 Z=50 Ohm
SDD:D1  n1 0  n2 0  I[1,0]=_v1/50  I[2,0]=_v2/50
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        // Must not throw; SDD with I[x,0]=_vx/50 is a linear passive network.
        var ds = SParameterEngine.Run(nl, [1e9, 2e9]);

        Assert.True(ds.Contains("S"), "SParameterEngine must return an S cube.");
        var sCube = ds["S"];
        Assert.Equal(3, sCube.Rank); // [freq, port, port]
        Assert.Equal(2, sCube.Axes[1].Length); // 2 ports
    }
}

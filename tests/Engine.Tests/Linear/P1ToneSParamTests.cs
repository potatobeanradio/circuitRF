using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Gate tests for P1Tone as an S-parameter port (Part 2 of brief-p1tone-num-sddx-defaults).
///
/// A top-level P1Tone with Num and Z participates in S-parameter analysis exactly like a Term:
/// - wave path: conductance 1/Z stamped, Kurokawa S extraction.
/// - legacy path: 0V branch via StampAsSParamPort.
/// Buried P1Tone is inert (not a port).
/// </summary>
public class P1ToneSParamTests
{
    private static DataSet Run(string cnl, double[] freqsHz, AnalysisSettings? settings = null)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return SParameterEngine.Run(nl, freqsHz, settings);
    }

    private static Complex Sij(DataSet ds, int r, int c, int fi = 0) =>
        (Complex)ds["S"][fi, r, c];

    // ── T1: single P1Tone (Num=1, Z=50) matches a single Term ────────────────

    [Fact]
    public void P1Tone_1Port_MatchesTerm()
    {
        // Reference: Term into a load resistor.
        const string cnlTerm = @"
Term:T1  n1 0  Num=1 Z=50 Ohm
R:RL  n1 0  R=100 Ohm
";
        const string cnlP1 = @"
P1Tone:P1  n1 0  Num=1 Pavl=0 dBm Z=50 Ohm Freq=1 GHz Phase=0 deg
R:RL  n1 0  R=100 Ohm
";
        double[] freqs = [1e9, 2e9, 3e9];
        var dsTerm = Run(cnlTerm, freqs);
        var dsP1   = Run(cnlP1,   freqs);

        const double Tol = 1e-9;
        for (int fi = 0; fi < freqs.Length; fi++)
            Assert.True((Sij(dsP1, 0, 0, fi) - Sij(dsTerm, 0, 0, fi)).Magnitude < Tol,
                $"[fi={fi}] S11 P1Tone={Sij(dsP1,0,0,fi):G6} Term={Sij(dsTerm,0,0,fi):G6}");
    }

    // ── T2: mixed Term(Num=1) + P1Tone(Num=2) matches Term + Term ─────────────

    [Fact]
    public void TermAndP1Tone_2Port_MatchesTermTerm()
    {
        // Pi-attenuator reference: two Terms.
        const string cnlRef = @"
Term:T1  n1 0  Num=1 Z=50 Ohm
Term:T2  n2 0  Num=2 Z=50 Ohm
R:R1  n1 0   R=150 Ohm
R:Rs  n1 n2  R=100 Ohm
R:R2  n2 0   R=150 Ohm
";
        // Same circuit but port 2 replaced by P1Tone.
        const string cnlMixed = @"
Term:T1  n1 0  Num=1 Z=50 Ohm
P1Tone:P2  n2 0  Num=2 Pavl=0 dBm Z=50 Ohm Freq=2 GHz Phase=0 deg
R:R1  n1 0   R=150 Ohm
R:Rs  n1 n2  R=100 Ohm
R:R2  n2 0   R=150 Ohm
";
        double[] freqs = [1e9, 2e9];
        var dsRef   = Run(cnlRef,   freqs);
        var dsMixed = Run(cnlMixed, freqs);

        const double Tol = 1e-9;
        for (int fi = 0; fi < freqs.Length; fi++)
        for (int r = 0; r < 2; r++)
        for (int c = 0; c < 2; c++)
            Assert.True((Sij(dsMixed, r, c, fi) - Sij(dsRef, r, c, fi)).Magnitude < Tol,
                $"[fi={fi}] S[{r+1},{c+1}] mixed={Sij(dsMixed,r,c,fi):G6} ref={Sij(dsRef,r,c,fi):G6}");
    }

    // ── T3: P1Tone Z=75 → port Z0 in Z0 cube is 75 Ω ────────────────────────

    [Fact]
    public void P1Tone_Z75_Z0CubeIs75()
    {
        const string cnl = @"
P1Tone:P1  n1 0  Num=1 Pavl=0 dBm Z=75 Ohm Freq=1 GHz Phase=0 deg
R:RL  n1 0  R=150 Ohm
";
        var ds = Run(cnl, [1e9]);

        // Z0 cube: rank-1, one entry per port.
        var z0Cube = ds["Z0"];
        Assert.Equal(1, z0Cube.Rank);
        Assert.Equal(1, z0Cube.Axes[0].Length);
        double z0 = z0Cube.ComplexValues[0].Real;
        Assert.True(Math.Abs(z0 - 75.0) < 1e-9,
            $"Port Z0={z0:G6} Ω, expected 75 Ω");
    }

    // ── T4: buried P1Tone (dotted InstancePath) is inert — not a port ─────────

    [Fact]
    public void BuriedP1Tone_IsNotAPort()
    {
        // A P1Tone nested inside a define block is buried (InstancePath contains '.').
        // It must not be collected as an s-param port; only the top-level Term is.
        // Pattern mirrors EngineDiagnosticsChannelTests.T1 (buried Term).
        const string cnl = @"
Term:T1  n1 0  Num=1 Z=50 Ohm
R:RL  n1 0  R=100 Ohm
define Sub(A)
  P1Tone:P1  A 0  Num=1 Pavl=0 dBm Z=50 Ohm Freq=1 GHz Phase=0 deg
end Sub
Sub:X1  n_float
";
        var ds = Run(cnl, [1e9]);

        // Only one port (the top-level Term) → 1×1 S matrix.
        var sCube = ds["S"];
        Assert.Equal(3, sCube.Rank); // [freq, port, port]
        Assert.Equal(1, sCube.Axes[1].Length); // 1 port
    }
}

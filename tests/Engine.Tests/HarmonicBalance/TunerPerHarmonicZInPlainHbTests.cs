using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// A <c>Tuner</c> on an ordinary <c>type=hb</c> testbench must present its DECLARED <c>Z[k]</c> at
/// each harmonic — not <c>Z[1]</c> at all of them.
///
/// <para><b>It did the wrong thing, silently, until Round 10.</b> <c>TunerModel.GetZ</c> falls back to
/// a flat <c>Z[1]</c> whenever its tone has never been set ("S-param mode"), and the tone was only ever
/// set by the loadpull engines. So a testbench that declared <c>Z[2]</c>, <c>Z[3]</c>… ran, converged,
/// and answered for a circuit with a different load at every harmonic but the first.
/// <c>HbEngine.GiveTunerItsBandRuler</c> is the fix.</para>
///
/// <para>The oracle is deliberately a MEASUREMENT and not a mock: the same nonlinear device is solved
/// three times against three different band-2 terminations, and the second-harmonic voltage it
/// develops has to move with them. Before the fix all three are bit-identical, which is what makes
/// this test able to fail.</para>
/// </summary>
public class TunerPerHarmonicZInPlainHbTests(ITestOutputHelper output)
{
    /// <summary>A square-law drain current (so there IS a second harmonic to terminate), a 50 Ω gate,
    /// a P1Tone drive, and a load Tuner whose band-2 impedance is the only thing that varies.</summary>
    private static string Netlist(string z2) => $@"
RFfreq = 2e9
P1Tone:PIN  n_in 0   Num=1  Pavl=0  Z=50  Freq=RFfreq
SDD:DUT     n_in 0  n_out 0   NumPorts=2   I[1,0]=_v1/50   I[2,0]=0.05*_v1+0.02*_v1^2
Tuner:LOAD  n_out 0   Z[1]=50   Z[2]={z2}   Zdefault=1e-6   BiasTee=on   Vbias=0
analysis HB1 type=hb Tone=RFfreq MaxHarm=3 Tol=1e-10 MaxIter=200
";

    private static Complex SecondHarmonicAtLoad(string z2)
    {
        var (lib, tb) = new CnlReader().Read(Netlist(z2));
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<Core.Design.HarmonicBalanceAnalysis>().Single();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var ds  = new HbEngine(nl, tb).Run(p);

        Assert.Equal(1.0, ds["Converged"].RealValues[0]);

        var v = ds["V"];
        int node = Array.IndexOf(v.Axes[0].Labels!.ToArray(), "n_out");
        Assert.True(node >= 0, "the load node is not on the V cube's node axis");
        return (Complex)v[node, 2];          // harmonic 2
    }

    [Fact]
    public void TheBandTwoTermination_IsWhatTheTunerDeclares_NotItsFundamental()
    {
        var shorted = SecondHarmonicAtLoad("1e-6");
        var matched = SecondHarmonicAtLoad("50");
        var opened  = SecondHarmonicAtLoad("1e6");

        output.WriteLine($"|V(n_out)| at 2f0 — Z[2]=1e-6: {shorted.Magnitude:E4}");
        output.WriteLine($"|V(n_out)| at 2f0 — Z[2]=50  : {matched.Magnitude:E4}");
        output.WriteLine($"|V(n_out)| at 2f0 — Z[2]=1e6 : {opened.Magnitude:E4}");

        // MEASURED WITHOUT THE FIX, not assumed: all three come back 5.0000E-002 — Z[1] = 50 Ω
        // presented at harmonic 2 regardless of what Z[2] says. (Verified by disabling the
        // GiveTunerItsBandRuler call and re-running this exact test.)
        //
        // WITH it, the tuner presents EXACTLY Z[2]: the 1 F internal block is a dead short at 4 GHz
        // and the 1 H choke is 25 GΩ, so V₂ = I₂·Z[2] with nothing else in the path — and the
        // implied I₂ is 1.0000e-3 A across TWELVE decades of Z[2]. That constant is a far stronger
        // statement than "the number moved", so it is what is asserted.
        //
        // The tolerance is RELATIVE and 1e-4 rather than exact, for a stated reason: at Z[2] = 1 MΩ
        // the choke is no longer negligible next to it (1e6 ‖ 25.13e9 = 999 960 Ω), so that one case
        // reads 2 ppm low BY CONSTRUCTION. Tightening past that would be asserting the choke is
        // infinite, which it is not.
        double i2Short = shorted.Magnitude / 1e-6;
        double i2Match = matched.Magnitude / 50.0;
        double i2Open  = opened.Magnitude  / 1e6;
        output.WriteLine($"implied |I₂| = {i2Short:E6} / {i2Match:E6} / {i2Open:E6} A");

        Assert.True(Math.Abs(i2Short - i2Match) / i2Match < 1e-4,
            $"Z[2] = 1e-6 implies I₂ = {i2Short:E6}, not {i2Match:E6}");
        Assert.True(Math.Abs(i2Open - i2Match) / i2Match < 1e-4,
            $"Z[2] = 1e6 implies I₂ = {i2Open:E6}, not {i2Match:E6}");
    }

    [Fact]
    public void TheFundamental_IsUnaffectedByTheBandTwoTermination()
    {
        Complex Fundamental(string z2)
        {
            var (lib, tb) = new CnlReader().Read(Netlist(z2));
            var nl  = new Elaborator(lib).Elaborate(tb);
            var hba = tb.Analyses.OfType<Core.Design.HarmonicBalanceAnalysis>().Single();
            var ds  = new HbEngine(nl, tb).Run(
                HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit));
            var v = ds["V"];
            int node = Array.IndexOf(v.Axes[0].Labels!.ToArray(), "n_out");
            return (Complex)v[node, 1];
        }

        // The drain current here depends only on _v1, so band 2's load cannot feed back into band 1.
        // A change at the fundamental would mean the band ruler had mapped the wrong harmonic.
        var a = Fundamental("1e-6");
        var b = Fundamental("1e6");
        output.WriteLine($"|V(n_out)| at f0: {a.Magnitude:E6} vs {b.Magnitude:E6}");
        Assert.Equal(a.Magnitude, b.Magnitude, precision: 9);
    }
}

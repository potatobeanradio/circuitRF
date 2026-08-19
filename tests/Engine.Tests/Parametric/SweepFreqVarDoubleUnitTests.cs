using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Parametric;

/// <summary>
/// Gate tests for brief-var-unit-wins-consistency.
///
/// Regression: a parametric sweep over a frequency variable with Unit=GHz injected the override
/// unit-less, so GlobalsWithExplicitUnit didn't contain the swept variable. FreqUnit.ResolveHz
/// then re-applied ToneUnit → frequency was 1e18 Hz instead of 1e9 Hz.
/// Fix (Part A): inject override with baseUnit ("Hz") so MarkGlobalHasUnit fires.
/// Fix (Part B): Evaluator.Eval skips the site unit when the expression references a unit-bearing var.
/// </summary>
public class SweepFreqVarDoubleUnitTests(ITestOutputHelper output)
{
    // CNL for the exact reported circuit:
    // - RFfreq declared as pure number (no unit in VAR), swept with Unit=GHz
    // - HB Tone="RFfreq" ToneUnit=GHz
    // - P1Tone Freq=RFfreq GHz (site unit on a reference to the swept var)
    // - Outer sweep Var=RFfreq, Start=1, Stop=10, Npts=3, Unit=GHz
    private const string SweepCnl = @"
RFfreq = 2

P1Tone:P1  n_rf  0  Num=1  Pavl=0  Freq=RFfreq GHz

R:Rload  n_rf  0  R=50 Ohm

analysis HB1  type=hb  Tone=""RFfreq""  ToneUnit=GHz  MaxHarm=2  Tol=1e-4
analysis SW1  type=parametric_sweep  Var=RFfreq  Start=1  Stop=10  Npts=3  Unit=GHz  Inner=HB1
";

    // T4 — Sweep_FreqVar_NoDoubleApply (the regression)
    // For each swept point, p.ToneHz and P1ToneModel.FreqHz must equal the nominal Hz value,
    // not 1e18 (= GHz applied twice to a value already in Hz).
    [Fact]
    public void Sweep_FreqVar_NoDoubleApply()
    {
        var (lib, tb) = new CnlReader().Read(SweepCnl);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First(a => a.Name == "HB1");
        var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW1");

        // Spec: Start=1, Stop=10, Npts=3 → values [1e9, 5.5e9, 1e10] after Unit=GHz scaling
        double[] expectedHz = [1e9, 5.5e9, 1e10];

        int varIdx = tb.GlobalVariables.FindIndex(v => v.Name == "RFfreq");

        for (int si = 0; si < sw1.SweepValues.Length; si++)
        {
            double sweepVal = sw1.SweepValues[si];   // already in Hz (base unit, Brief 2 scaling)
            double expectHz = expectedHz[si];

            // Inject override exactly as ParametricSweepEngine does after the Part A fix:
            // base unit "Hz" so MarkGlobalHasUnit fires → var-unit-wins applies.
            var overrideVar = new Variable("RFfreq",
                sweepVal.ToString("G17", CultureInfo.InvariantCulture), "Hz");
            tb.GlobalVariables[varIdx] = overrideVar;

            var netlist = new Elaborator(lib).Elaborate(tb);

            // T5 check embedded: GlobalsWithExplicitUnit must contain "RFfreq" at every point.
            Assert.Contains("RFfreq", netlist.GlobalsWithExplicitUnit);

            // HB ToneHz must be the nominal Hz value, not 1e18.
            var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
            output.WriteLine($"[si={si}] sweepVal={sweepVal:G3}  expectHz={expectHz:G3}  toneHz={p.ToneHz:G3}");

            Assert.Equal(expectHz, p.ToneHz, 1e-3 * expectHz);   // relative tolerance 0.1%

            // P1ToneModel.FreqHz must also match (component-parameter path, Part B of fix).
            var p1 = netlist.Components
                .Select(ec => ec.Model)
                .OfType<P1ToneModel>()
                .FirstOrDefault();
            Assert.NotNull(p1);
            output.WriteLine($"       p1.FreqHz={p1.FreqHz:G3}");
            Assert.Equal(expectHz, p1.FreqHz, 1e-3 * expectHz);
        }

        // Restore original variable
        tb.GlobalVariables[varIdx] = new Variable("RFfreq", "2");
    }

    // T5 — Sweep_Override_Marked
    // A Unit=GHz sweep marks the swept variable in GlobalsWithExplicitUnit after elaboration.
    // A unit-less sweep (no Spec.Unit, no VAR unit) does NOT mark it.
    [Fact]
    public void Sweep_Override_Marked()
    {
        // Unit=GHz sweep → should mark
        {
            var (lib, tb) = new CnlReader().Read(SweepCnl);
            var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW1");
            int varIdx = tb.GlobalVariables.FindIndex(v => v.Name == "RFfreq");

            // Inject as ParametricSweepEngine does (Part A fix: baseUnit="Hz")
            tb.GlobalVariables[varIdx] = new Variable("RFfreq", "1000000000", "Hz");
            var netlist = new Elaborator(lib).Elaborate(tb);

            Assert.Contains("RFfreq", netlist.GlobalsWithExplicitUnit);
            tb.GlobalVariables[varIdx] = new Variable("RFfreq", "2");
        }

        // Unit-less sweep → should NOT mark (unit-less VAR, no Spec.Unit)
        const string unitlessCnl = @"
Rval = 50

Port:P1  n1 0  Num=1  Z=50 Ohm
Port:P2  n2 0  Num=2  Z=50 Ohm
R:Rs     n1 n2  R=Rval Ohm

analysis SP1  type=sparam  start=1e9  stop=1e9  npts=1
analysis SW1  type=parametric_sweep  Var=Rval  Values=25,50  Inner=SP1
";
        {
            var (lib, tb) = new CnlReader().Read(unitlessCnl);
            int varIdx = tb.GlobalVariables.FindIndex(v => v.Name == "Rval");

            // Unit-less override: baseUnit="" → no mark
            tb.GlobalVariables[varIdx] = new Variable("Rval", "25");
            var netlist = new Elaborator(lib).Elaborate(tb);

            Assert.DoesNotContain("Rval", netlist.GlobalsWithExplicitUnit);
            tb.GlobalVariables[varIdx] = new Variable("Rval", "50");
        }
    }

    /// <summary>
    /// The reported bug, in the shape it was reported in: the VAR declares the unit
    /// (<c>RFfreq = 2 GHz</c>), the sweep range is typed as the bare coefficients 2 … 3, and the
    /// sweep carries no <c>Unit=</c> of its own.
    ///
    /// <para>The engine used to attach the VAR's base unit ("Hz") to the injected override without
    /// ever scaling the values by that unit — so the mark said "already base SI" about numbers that
    /// were still GHz coefficients, var-unit-wins suppressed the ToneUnit, and a loadpull pursuit
    /// meant for 2 GHz ran at 2 Hz. Scale and mark now come from the same unit.</para>
    /// </summary>
    [Fact]
    public void Sweep_NoSpecUnit_InheritsTheVarsOwnUnit()
    {
        const string cnl = @"
RFfreq = 2 GHz

P1Tone:P1  n_rf  0  Num=1  Pavl=0  Freq=RFfreq

R:Rload  n_rf  0  R=50 Ohm

analysis HB1  type=hb  Tone=""RFfreq""  ToneUnit=GHz  MaxHarm=2  Tol=1e-4
analysis SW1  type=parametric_sweep  Var=RFfreq  Start=2  Stop=3  Step=0.5  Inner=HB1
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().Single();
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();

        Assert.Equal("GHz", tb.GlobalVariables.Single(v => v.Name == "RFfreq").Unit);
        Assert.Equal("",    sw1.Spec!.Unit);

        // Reproduce the engine's own injection for each point and read the tone back out.
        double inherited = Units.Scale("GHz")!.Value;
        int varIdx = tb.GlobalVariables.FindIndex(v => v.Name == "RFfreq");
        double[] expected = [2e9, 2.5e9, 3e9];

        Assert.Equal(expected.Length, sw1.SweepValues.Length);
        for (int si = 0; si < sw1.SweepValues.Length; si++)
        {
            tb.GlobalVariables[varIdx] = new Variable("RFfreq",
                (sw1.SweepValues[si] * inherited).ToString("G17", CultureInfo.InvariantCulture),
                Units.BaseUnit("GHz"));
            var netlist = new Elaborator(lib).Elaborate(tb);
            var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);

            output.WriteLine($"[si={si}] coeff={sw1.SweepValues[si]}  toneHz={p.ToneHz:G6}");
            Assert.Equal(expected[si], p.ToneHz, 1e-6 * expected[si]);
        }
    }

    // T6 — Sweep_Equals_NoSweep_AtSamePoint
    // The full ParametricSweepEngine run (which does Part A internally) at the 2 GHz point
    // must produce the same ToneHz as the direct no-sweep elaboration at RFfreq=2 GHz.
    // (Both should be 2e9 Hz, not 2e18.)
    [Fact]
    public void Sweep_Equals_NoSweep_AtSamePoint()
    {
        // No-sweep: RFfreq=2 GHz, P1Tone Freq=RFfreq GHz → expect FreqHz=2e9
        const string noSweepCnl = @"
RFfreq = 2 GHz

P1Tone:P1  n_rf  0  Num=1  Pavl=0  Freq=RFfreq GHz

R:Rload  n_rf  0  R=50 Ohm

analysis HB1  type=hb  Tone=""RFfreq""  ToneUnit=GHz  MaxHarm=2  Tol=1e-4
";
        var (libNs, tbNs) = new CnlReader().Read(noSweepCnl);
        var netlistNs = new Elaborator(libNs).Elaborate(tbNs);
        var hbaNs = tbNs.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var pNs = HbEngine.Resolve(hbaNs, netlistNs.ResolvedGlobals, netlistNs.GlobalsWithExplicitUnit);

        double toneHzNoSweep = pNs.ToneHz;
        double p1HzNoSweep   = netlistNs.Components.Select(ec => ec.Model).OfType<P1ToneModel>().First().FreqHz;

        output.WriteLine($"no-sweep: toneHz={toneHzNoSweep:G3}  p1Hz={p1HzNoSweep:G3}");

        // Both should be 2e9, not 2e18
        Assert.Equal(2e9, toneHzNoSweep, 1e-3 * 2e9);
        Assert.Equal(2e9, p1HzNoSweep,   1e-3 * 2e9);

        // Swept version: Start=2 Stop=2 Npts=1 Unit=GHz → single sweep point at 2 GHz
        const string sweepAt2Cnl = @"
RFfreq = 2

P1Tone:P1  n_rf  0  Num=1  Pavl=0  Freq=RFfreq GHz

R:Rload  n_rf  0  R=50 Ohm

analysis HB1  type=hb  Tone=""RFfreq""  ToneUnit=GHz  MaxHarm=2  Tol=1e-4
analysis SW1  type=parametric_sweep  Var=RFfreq  Start=2  Stop=2  Npts=1  Unit=GHz  Inner=HB1
";
        var (libSw, tbSw) = new CnlReader().Read(sweepAt2Cnl);
        var sw1 = tbSw.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW1");
        var hbaSw = tbSw.Analyses.OfType<HarmonicBalanceAnalysis>().First(a => a.Name == "HB1");
        int varIdx = tbSw.GlobalVariables.FindIndex(v => v.Name == "RFfreq");

        // Simulate what ParametricSweepEngine does for the single sweep point
        double sweepVal = sw1.SweepValues[0];  // 2e9 (scaled by Unit=GHz)
        var origVar = tbSw.GlobalVariables[varIdx];
        tbSw.GlobalVariables[varIdx] = new Variable("RFfreq", sweepVal.ToString("G17", CultureInfo.InvariantCulture), "Hz");

        var netlistSw = new Elaborator(libSw).Elaborate(tbSw);
        var pSw = HbEngine.Resolve(hbaSw, netlistSw.ResolvedGlobals, netlistSw.GlobalsWithExplicitUnit);
        double toneHzSwept = pSw.ToneHz;
        double p1HzSwept   = netlistSw.Components.Select(ec => ec.Model).OfType<P1ToneModel>().First().FreqHz;

        tbSw.GlobalVariables[varIdx] = origVar;

        output.WriteLine($"swept:    toneHz={toneHzSwept:G3}  p1Hz={p1HzSwept:G3}");

        // Swept and no-sweep must agree
        Assert.Equal(toneHzNoSweep, toneHzSwept, 1e-3 * toneHzNoSweep);
        Assert.Equal(p1HzNoSweep,   p1HzSwept,   1e-3 * p1HzNoSweep);
    }
}

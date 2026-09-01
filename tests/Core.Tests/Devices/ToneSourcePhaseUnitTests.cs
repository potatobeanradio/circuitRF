using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Every source's <c>Phase</c> reaches the matrix as the angle the user typed — for
/// <c>V_1Tone</c>/<c>V_nTone</c>, <c>I_1Tone</c>/<c>I_nTone</c>, <c>P1Tone</c> and <c>PnTone</c>.
///
/// <para><b>What this exists to hold shut.</b> The Elaborator applies a parameter's own unit before
/// the factory ever sees the value — <c>Units.Scale("deg") = π/180</c> — which is the convention
/// <c>TLineModel</c>'s <c>E</c> established and every angle-valued parameter follows. All four
/// source models then multiplied by π/180 AGAIN, so an authored <c>Phase=45 deg</c> drove the
/// circuit at 0.785°. The defaults are 0 everywhere, which is exactly why nothing caught it: a
/// gate on the DEFAULT parameter set cannot see a unit-conversion bug at all.</para>
///
/// <para>So every case here uses a NON-ZERO phase, and states both spellings — <c>45 deg</c> and a
/// bare <c>45</c> — because they are different numbers and the difference is the whole point.</para>
/// </summary>
public class ToneSourcePhaseUnitTests
{
    private const double Deg = Math.PI / 180.0;

    private static (ElaboratedComponent Comp, ElaboratedNetlist Netlist) One(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return (nl.Components[0], nl);
    }

    /// <summary>The single non-zero excitation a source stamped — a branch source value for a
    /// voltage source, a node current injection for a current one.</summary>
    private static Complex Excitation(string cnl, double freqHz, bool current = false)
    {
        var (c, _) = One(cnl);
        var mna = new CapturingMnaContext();
        c.Model.Stamp(mna, c, 2.0 * Math.PI * freqHz);

        var live = (current ? mna.CurrentInjections.Values : mna.SourceValues.Values)
            .Where(v => v.Magnitude > 1e-15).ToList();
        Assert.NotEmpty(live);
        return live[0];
    }

    private static void NearPolar(double magnitude, double phaseRad, Complex actual)
    {
        Assert.Equal(magnitude, actual.Magnitude, 12);
        Assert.Equal(Math.Cos(phaseRad) * magnitude, actual.Real,      12);
        Assert.Equal(Math.Sin(phaseRad) * magnitude, actual.Imaginary, 12);
    }

    // ── V_1Tone / I_1Tone: the tile-authored spelling, with a unit ────────────

    [Theory]
    [InlineData(45.0)]
    [InlineData(90.0)]
    [InlineData(-30.0)]
    public void VTone_APhaseInDegreesDrivesThatManyDegrees(double deg)
    {
        var e = Excitation($"V_1Tone:Vs  a 0  Freq=1e9  V=2  Phase={deg} deg\nR:R1  a 0  R=50\n", 1e9);
        NearPolar(2.0, deg * Deg, e);
    }

    [Fact]
    public void VTone_ABarePhaseIsRadians_BecauseThatIsWhatNoUnitMeansEverywhereElse()
    {
        // The same trap TLIN's E carries, and the same answer: the number is taken at face value in
        // the base SI unit of its dimension. Stated as a test so the choice is deliberate rather
        // than discovered.
        var e = Excitation("V_1Tone:Vs  a 0  Freq=1e9  V=2  Phase=1.5707963267948966\nR:R1  a 0  R=50\n", 1e9);
        NearPolar(2.0, Math.PI / 2.0, e);

        var r = Excitation("V_1Tone:Vs  a 0  Freq=1e9  V=2  Phase=1.5707963267948966 rad\nR:R1  a 0  R=50\n", 1e9);
        NearPolar(2.0, Math.PI / 2.0, r);
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(-120.0)]
    public void ITone_TheCurrentDualDoesTheSameThing(double deg)
    {
        var e = Excitation($"I_1Tone:Is  a 0  Freq=1e9  I=3 mA  Phase={deg} deg\nR:R1  a 0  R=50\n",
                           1e9, current: true);
        NearPolar(3e-3, deg * Deg, e);
    }

    [Fact]
    public void VnTone_EachTonesPhaseIsItsOwn()
    {
        const string cnl = @"
V_nTone:Vn  a 0  NumFreqs=2  Freq[1]=1e9 V[1]=1 Phase[1]=45 deg  Freq[2]=2e9 V[2]=0.5 Phase[2]=-60 deg
R:R1  a 0  R=50
";
        NearPolar(1.0, 45.0 * Deg,  Excitation(cnl, 1e9));
        NearPolar(0.5, -60.0 * Deg, Excitation(cnl, 2e9));
    }

    // ── P1Tone / PnTone ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(45.0)]
    [InlineData(-90.0)]
    public void P1Tone_DrivesAtThePhaseItWasGiven(double deg)
    {
        // P1Tone stamps its drive branch only in HB mode, so the tone context has to be set first;
        // |Vs| = √(8·Re(Z)·Pavl_W), which the test computes rather than reads back.
        var (c, _) = One($"P1Tone:P1  a 0  Num=1 Pavl=10 dBm Z=50 Ohm Freq=1e9 Phase={deg} deg\nR:R1  a 0  R=50\n");
        ((P1ToneModel)c.Model).SetToneContext(1e9, 1e9);

        var mna = new CapturingMnaContext();
        c.Model.Stamp(mna, c, 2.0 * Math.PI * 1e9);

        double vs = Math.Sqrt(8.0 * 50.0 * Math.Pow(10.0, (10.0 - 30.0) / 10.0));
        NearPolar(vs, deg * Deg, Assert.Single(mna.SourceValues.Values, v => v.Magnitude > 1e-15));
    }

    [Fact]
    public void PnTone_EachTonesPhaseIsItsOwn()
    {
        const string cnl = @"
PnTone:Ps  a 0  Z=50 Ohm  Freq[1]=1e9 Pavl[1]=10 dBm Phase[1]=45 deg  Freq[2]=2e9 Pavl[2]=10 dBm Phase[2]=-60 deg
R:R1  a 0  R=50
";
        double vs = Math.Sqrt(8.0 * 50.0 * Math.Pow(10.0, (10.0 - 30.0) / 10.0));

        foreach (var (f, deg) in new[] { (1e9, 45.0), (2e9, -60.0) })
        {
            var (c, _) = One(cnl);
            ((PnToneModel)c.Model).SetToneContext(1.5e9);

            var mna = new CapturingMnaContext();
            c.Model.Stamp(mna, c, 2.0 * Math.PI * f);
            NearPolar(vs, deg * Deg,
                      Assert.Single(mna.SourceValues.Values, v => v.Magnitude > 1e-15));
        }
    }

    // ── The sweep-time re-evaluation path ────────────────────────────────────
    //
    // A tone source whose amplitude or phase references a global re-resolves the RAW EXPRESSION at
    // each sweep point, and the raw text carries no unit — so the unit multiplier has to travel
    // with it. Two things were wrong here beyond the double conversion, and neither is visible from
    // a first stamp.

    [Fact]
    public void AVariableRefPhase_KeepsItsUnitAcrossASweepPoint()
    {
        // Before: `_expr_Phase` was never read, so PhaseExpr stayed null and the re-evaluation used
        // its 0.0 fallback — a swept phase silently became zero after the first point.
        var (c, _) = One(@"
phi = 45
V_1Tone:Vs  a 0  Freq=1e9  V=vamp  Phase=phi deg
vamp = 2
R:R1  a 0  R=50
");
        var m = Assert.IsType<ToneSourceModel>(c.Model);
        var mna = new CapturingMnaContext();
        m.Stamp(mna, c, 2.0 * Math.PI * 1e9);
        NearPolar(2.0, 45.0 * Deg, Assert.Single(mna.SourceValues.Values));

        // Now move both globals, as a parametric sweep does.
        m.ReevaluateFromGlobals(new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["phi"]  = new Value(120.0),
            ["vamp"] = new Value(3.0),
        });

        var swept = new CapturingMnaContext();
        m.Stamp(swept, c, 2.0 * Math.PI * 1e9);
        NearPolar(3.0, 120.0 * Deg, Assert.Single(swept.SourceValues.Values));
    }

    [Fact]
    public void AVariableRefAmplitudeWithALiteralPhase_StillCarriesThePhase()
    {
        // Before: the phase was applied to the initial phasor ONLY when the amplitude was a
        // literal, so `V=vamp Phase=30 deg` stamped at 0° until something happened to re-evaluate
        // it — and the re-evaluation then used its own 0.0 fallback, so it never arrived at all.
        var (c, _) = One(@"
vamp = 2
V_1Tone:Vs  a 0  Freq=1e9  V=vamp  Phase=30 deg
R:R1  a 0  R=50
");
        var m = Assert.IsType<ToneSourceModel>(c.Model);

        var mna = new CapturingMnaContext();
        m.Stamp(mna, c, 2.0 * Math.PI * 1e9);
        NearPolar(2.0, 30.0 * Deg, Assert.Single(mna.SourceValues.Values));

        m.ReevaluateFromGlobals(new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["vamp"] = new Value(5.0),
        });

        var swept = new CapturingMnaContext();
        m.Stamp(swept, c, 2.0 * Math.PI * 1e9);
        NearPolar(5.0, 30.0 * Deg, Assert.Single(swept.SourceValues.Values));
    }

    [Fact]
    public void AVariableRefAmplitude_KeepsItsOwnUnitAcrossASweepPoint()
    {
        // The same defect on the AMPLITUDE, which shares the mechanism: `I=iamp mA` resolved to
        // 2 mA and then re-evaluated to 2 AMPS, three orders out, because the stored expression
        // text has no `mA` on it.
        var (c, _) = One(@"
iamp = 2
I_1Tone:Is  a 0  Freq=1e9  I=iamp mA  Phase=0
R:R1  a 0  R=50
");
        var m = Assert.IsType<CurrentToneSourceModel>(c.Model);

        var mna = new CapturingMnaContext();
        m.Stamp(mna, c, 2.0 * Math.PI * 1e9);
        Assert.Equal(2e-3, mna.CurrentInjections[c.Nodes[0]].Magnitude, 12);

        m.ReevaluateFromGlobals(new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["iamp"] = new Value(5.0),
        });

        var swept = new CapturingMnaContext();
        m.Stamp(swept, c, 2.0 * Math.PI * 1e9);
        Assert.Equal(5e-3, swept.CurrentInjections[c.Nodes[0]].Magnitude, 12);
    }

    [Fact]
    public void AVariableThatCarriesItsOwnUnit_IsNotScaledTwice()
    {
        // The var-unit-wins rule (Evaluator.Eval): when the referenced variable declares a unit, the
        // SITE unit is deliberately not applied. The re-evaluation multiplier has to agree, or a
        // sweep would scale a value the first resolution did not.
        var (c, _) = One(@"
phi = 45 deg
V_1Tone:Vs  a 0  Freq=1e9  V=2  Phase=phi deg
R:R1  a 0  R=50
");
        var m = Assert.IsType<ToneSourceModel>(c.Model);

        var mna = new CapturingMnaContext();
        m.Stamp(mna, c, 2.0 * Math.PI * 1e9);
        NearPolar(2.0, 45.0 * Deg, Assert.Single(mna.SourceValues.Values));

        m.ReevaluateFromGlobals(new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["phi"] = new Value(90.0 * Deg),   // the global arrives already unit-applied
        });

        var swept = new CapturingMnaContext();
        m.Stamp(swept, c, 2.0 * Math.PI * 1e9);
        NearPolar(2.0, 90.0 * Deg, Assert.Single(swept.SourceValues.Values));
    }
}

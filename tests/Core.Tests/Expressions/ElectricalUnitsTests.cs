using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// The prefixed voltage, current and power units carry their real scale (2026-08-29).
///
/// <para>They used to sit in <c>_identityUnits</c> with no scale at all, so <c>Units.Scale</c>
/// returned null and <c>Evaluator.ApplyUnit</c> fell through to a multiplier of exactly <b>1</b>:
/// <c>Vdc=2 mV</c> resolved to two VOLTS and <c>I=2 mA</c> to two AMPS. Every one of these values
/// still parsed, still stamped and still converged, which is what made it worth a gate of its own
/// rather than a line in an existing one.</para>
///
/// <para>This is the same defect and the same fix as <c>nm</c>/<c>cm</c> in
/// <see cref="LengthUnitsTests"/>; read the two together.</para>
/// </summary>
public class ElectricalUnitsTests
{
    private static double Eval(string expr, string unit)
        => new Evaluator().Eval(expr, new Scope("test"), unit).AsReal();

    // ── Gate 1: every prefixed unit evaluates to its SI value ─────────────────

    [Theory]
    [InlineData("kV", 1e3)]     // was 1 — 1000x low
    [InlineData("V",  1.0)]     // was already correct (base symbol)
    [InlineData("mV", 1e-3)]    // was 1 — 1000x high
    [InlineData("uV", 1e-6)]    // was 1 — 1e6x high
    [InlineData("nV", 1e-9)]    // was 1 — 1e9x high
    [InlineData("A",  1.0)]     // was already correct (base symbol)
    [InlineData("mA", 1e-3)]    // was 1 — 1000x high
    [InlineData("uA", 1e-6)]    // was 1 — 1e6x high
    [InlineData("nA", 1e-9)]    // was 1 — 1e9x high
    [InlineData("kW", 1e3)]     // was 1 — 1000x low
    [InlineData("W",  1.0)]     // was already correct (base symbol)
    [InlineData("mW", 1e-3)]    // was 1 — 1000x high
    [InlineData("uW", 1e-6)]    // was 1 — 1e6x high
    [InlineData("S",  1.0)]     // conductance, added with the VCCS
    [InlineData("mS", 1e-3)]
    [InlineData("uS", 1e-6)]
    public void EveryElectricalUnit_EvaluatesToItsSiValue(string unit, double expected)
        => Assert.Equal(expected, Eval("1", unit), 15);

    // ── Gate 2: the base symbols stay identity-only, and that is not laziness ─
    //
    // Moving V/A/W into _scales would flip Units.IsKnown, which the CONSERVATIVE .cnl token gates
    // read (CnlReader's parameter-declaration peek, and SplitTrailingUnit with
    // includeIdentityUnits:false). "W" is this codebase's own name for a microstrip WIDTH, so
    // `L = 2 * W` would start splitting into the expression "2 *" and the unit "W". Their multiplier
    // of 1 is already correct through ApplyUnit's identity fallback, so there is nothing to gain and
    // a real regression to lose.
    [Theory]
    [InlineData("V")]
    [InlineData("A")]
    [InlineData("W")]
    [InlineData("dBm")]
    [InlineData("%")]
    public void TheBaseAndMeasurementSymbols_StayIdentityOnly(string unit)
    {
        Assert.False(Units.IsKnown(unit), $"'{unit}' must NOT be in _scales — see this test's comment");
        Assert.True(Units.IsRecognizedUnit(unit));
        Assert.Equal(1.0, Eval("1", unit), 15);
    }

    // ── Gate 3: every prefixed unit is IsKnown, not merely recognised ─────────
    //
    // The same second-order fix R-len-2 recorded for nm/cm: several .cnl and vendor-dialect token
    // gates consume a trailing unit via IsKnown rather than IsRecognizedUnit, so while these were
    // identity-only those sites left the token UNCONSUMED — silently dropping the unit, or worse,
    // leaking it into the net list as a phantom node (Units.cs's own TOhm comment).
    [Theory]
    [InlineData("kV")] [InlineData("mV")] [InlineData("uV")] [InlineData("nV")]
    [InlineData("mA")] [InlineData("uA")] [InlineData("nA")]
    [InlineData("kW")] [InlineData("mW")] [InlineData("uW")]
    [InlineData("mS")] [InlineData("uS")] [InlineData("kS")] [InlineData("nS")]
    public void EveryPrefixedElectricalUnit_IsKnown_NotMerelyRecognised(string unit)
    {
        Assert.True(Units.IsKnown(unit), $"'{unit}' must be in _scales, not _identityUnits");
        Assert.NotNull(Units.Scale(unit));
    }

    // ── Gate 4: BaseUnit's own invariant holds for all three dimensions ───────
    //
    // src/Core/CLAUDE.md: "Scale(BaseUnit(u)) == 1.0 is the property ParametricSweepEngine's
    // re-injection depends on." For V/A/W the base symbol is an identity unit, so Scale returns null
    // and the multiplier is 1 by fallback — which is the same number, and is what the sweep engine's
    // own `?? 1.0` reads. Both readings are asserted so a future caller that drops the fallback
    // fails here rather than in a sweep axis.
    [Theory]
    [InlineData("mV", "V")] [InlineData("kV", "V")] [InlineData("uV", "V")] [InlineData("nV", "V")]
    [InlineData("mA", "A")] [InlineData("uA", "A")] [InlineData("nA", "A")]
    [InlineData("mW", "W")] [InlineData("uW", "W")] [InlineData("kW", "W")]
    [InlineData("mS", "S")] [InlineData("uS", "S")] [InlineData("kS", "S")]
    public void BaseUnitReducesToAScaleOneSymbol(string unit, string expectedBase)
    {
        Assert.Equal(expectedBase, Units.BaseUnit(unit));
        Assert.Equal(1.0, Units.Scale(expectedBase) ?? 1.0, 15);
        Assert.Equal(1.0, Eval("1", expectedBase), 15);
    }

    // ── Gate 5: it reaches the elaborated netlist, not just the evaluator ─────
    //
    // The evaluator is only the first of three parse sites (src/Core/CLAUDE.md warns that an
    // instance-line unit, a cell-parameter declaration and a top-level variable assignment are
    // separate code paths, and that fixing one has repeatedly left the others broken). All three
    // are exercised here, through a real elaboration, against the resolved parameter value.

    private static double ResolvedParam(string cnl, string instance, string param)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ec = nl.Components.Single(c => c.InstancePath == instance);
        return ec.Parameters[param].AsReal();
    }

    [Fact]
    public void AnInstanceLineUnit_Scales()
    {
        double vdc = ResolvedParam(@"
Vdc:V1  n1 0  Vdc=2 mV
R:R1    n1 0  R=1 kOhm
", "V1", "Vdc");
        Assert.Equal(2e-3, vdc, 15);   // two MILLIvolts, not two volts
    }

    [Fact]
    public void ATopLevelVariableAssignmentUnit_Scales()
    {
        double vdc = ResolvedParam(@"
Vsupply = 2 mV
Vdc:V1  n1 0  Vdc=Vsupply
R:R1    n1 0  R=1 kOhm
", "V1", "Vdc");
        Assert.Equal(2e-3, vdc, 15);
    }

    [Fact]
    public void ACellParameterDeclarationUnit_Scales()
    {
        double vdc = ResolvedParam(@"
define Sub(A)
  parameters Vb=2 mV
  Vdc:V1  A 0  Vdc=Vb
  R:R1    A 0  R=1 kOhm
end Sub
Sub:X1  n1
", "X1.V1", "Vdc");
        Assert.Equal(2e-3, vdc, 15);
    }

    // A current source's own amplitude, which is what surfaced this: "1 mA" is a milliamp.
    [Fact]
    public void ACurrentSourceAmplitude_Scales()
    {
        double i = ResolvedParam(@"
I_1Tone:I1  n1 0  Freq=1 GHz  I=1 mA  Idc=2 mA
R:R1        n1 0  R=1 kOhm
", "I1", "I");
        Assert.Equal(1e-3, i, 15);
    }
}

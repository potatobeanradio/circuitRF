using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Parametric;

/// <summary>
/// A swept VOLTAGE, CURRENT or POWER round-trips (2026-08-29) — the same gate
/// <see cref="SweptLengthUnitTests"/> is for length, in the dimensions whose prefixed units had
/// exactly the same defect.
///
/// <para><see cref="ParametricSweepEngine"/> expands a sweep to already-SI values and re-injects each
/// point carrying <c>Units.BaseUnit(effUnit)</c>. Its own comment states the rule the hard way:
/// <b>"Scale and mark must come from the same unit or they contradict each other"</b> — the re-attach
/// MARKS the override as unit-bearing, and a marked override is read as already-base-SI at every use
/// site. While <c>mV</c> and <c>mA</c> were identity units, <c>Scale</c> returned null and the sweep
/// scaled by 1 while marking the values as volts and amps — so a 1 → 3 mV sweep ran at 1 → 3 VOLTS.
/// That is the shape of the reported bug where a 2 GHz sweep ran at 2 Hz, in a new dimension.</para>
///
/// <para><b>The engine needed no change</b> — a correct <c>_scales</c> entry is the whole fix. These
/// tests are what say so rather than assume it.</para>
/// </summary>
public class SweptElectricalUnitTests(ITestOutputHelper output)
{
    /// <summary>
    /// A one-node readout, exactly as <see cref="SweptLengthUnitTests"/> uses: the swept global IS the
    /// DC source's value, at a use site with no unit of its own, so the solved node voltage is
    /// numerically the injected sweep point. Reading V(n1) reads the injected number AND proves the
    /// whole elaborate-per-point round trip, not just one field.
    /// </summary>
    private const string ReadoutCnl = """
        Xvar = 1 {UNIT}

        Vdc:V1  n1 0  Vdc=Xvar
        R:R1    n1 0  R=1 Ohm

        analysis DC1  type=dc
        """;

    private static double[] SweepAndReadBack(string declaredUnit, SweepSpec spec, ITestOutputHelper output)
    {
        var (lib, tb) = new CnlReader().Read(ReadoutCnl.Replace("{UNIT}", declaredUnit));
        var dc = tb.Analyses.OfType<DcAnalysis>().Single();

        var sweep = new ParametricSweepAnalysis("SW1", "Xvar", spec, dc.Name);
        var ds = ParametricSweepEngine.Run(sweep, lib, tb);

        var v = ds["V"];
        Assert.Equal("Xvar", v.Axes[0].Name);
        Assert.Equal(1, v.Axes[1].Length);

        int nPts = v.Axes[0].Length;
        var read = new double[nPts];
        for (int i = 0; i < nPts; i++) read[i] = v.RealValues[i];

        output.WriteLine($"unit={declaredUnit,-4} axisUnit={v.Axes[0].Unit,-4} " +
                         $"readBack=[{string.Join(", ", read.Select(x => x.ToString("G6")))}]");
        return read;
    }

    // ── The spec carries the unit ─────────────────────────────────────────────

    /// <summary>
    /// 1 → 3 mV arrives as 1e-3, 2e-3, 3e-3 volts. Before the fix this ran at 1, 2 and 3 VOLTS — a
    /// thousandfold overdrive that converges and plots exactly like a correct run.
    /// </summary>
    [Theory]
    [InlineData("mV", 1e-3)]
    [InlineData("kV", 1e3)]
    [InlineData("mA", 1e-3)]
    [InlineData("uA", 1e-6)]
    [InlineData("mW", 1e-3)]
    [InlineData("mS", 1e-3)]
    public void APrefixedElectricalSweep_InjectsTheHandComputedSiValues(string unit, double perUnit)
    {
        var read = SweepAndReadBack(unit,
            new SweepSpec(1, 3, 3, SweepAxisMode.PointCount, SweepKind.Linear, unit), output);

        Assert.Equal(3, read.Length);
        Assert.Equal(1 * perUnit, read[0], 12);
        Assert.Equal(2 * perUnit, read[1], 12);
        Assert.Equal(3 * perUnit, read[2], 12);
    }

    // ── The spec carries NO unit and inherits the VAR's own ──────────────────

    /// <summary>
    /// The <c>EffectiveUnit</c> fallback: a spec with no unit of its own takes the swept VAR's
    /// declared one, and that inherited unit must reach the VALUES, not only the base-symbol
    /// re-attach. This is the half that made the earlier 2 GHz → 2 Hz bug, and it is the half a
    /// null <c>Scale</c> silently broke for every prefixed electrical unit.
    /// </summary>
    [Theory]
    [InlineData("mV", 1e-3)]
    [InlineData("mA", 1e-3)]
    public void ASweepWithNoUnitOfItsOwn_InheritsTheVarsPrefixedUnit_AndScalesByIt(string unit, double perUnit)
    {
        var read = SweepAndReadBack(unit,
            new SweepSpec(1, 2, 2, SweepAxisMode.PointCount, SweepKind.Linear, unit: ""), output);

        Assert.Equal(2, read.Length);
        Assert.Equal(1 * perUnit, read[0], 12);
        Assert.Equal(2 * perUnit, read[1], 12);
    }

    // ── The base symbols are unchanged ───────────────────────────────────────

    /// <summary>
    /// A sweep declared in the BASE symbol is scale-1 at both ends and was always correct. Asserted
    /// so the fix cannot have moved it: V/A/W stay identity units, and the engine's own
    /// <c>Scale(effUnit) ?? 1.0</c> is what reads them.
    /// </summary>
    [Theory]
    [InlineData("V")]
    [InlineData("A")]
    public void ABaseSymbolSweep_IsUnchangedByTheFix(string unit)
    {
        var read = SweepAndReadBack(unit,
            new SweepSpec(1, 3, 3, SweepAxisMode.PointCount, SweepKind.Linear, unit), output);

        Assert.Equal(3, read.Length);
        Assert.Equal(1.0, read[0], 12);
        Assert.Equal(2.0, read[1], 12);
        Assert.Equal(3.0, read[2], 12);
    }
}

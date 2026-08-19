using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Parametric;

/// <summary>
/// brief-core-length-units M2 — <b>a swept LENGTH round-trips.</b>
///
/// <para><see cref="ParametricSweepEngine"/> expands a sweep to already-SI values and then re-injects
/// each point as a <c>Variable</c> carrying <c>Units.BaseUnit(effUnit)</c> — a symbol whose scale is
/// supposed to be exactly 1.0, so "injecting it leaves the value unchanged" (the engine's own
/// comment). For frequency that was always true. For length it was false in BOTH directions:
/// <c>BaseUnit("mm")</c> was <c>"m"</c> and <c>Scale("m")</c> is 1e-3 (the SI prefix MILLI), while
/// <c>"mil"</c> was absent from the map entirely and re-scaled by its own 2.54e-5. A <c>mm</c> sweep
/// therefore arrived 1e-6 of intent and a <c>mil</c> sweep 6.45e-10 of it.</para>
///
/// <para><b>The engine itself needed NO change</b> — that was M2's own stated hope, and it is what a
/// genuine scale-1 base symbol buys. These tests are what say so rather than assume it.</para>
///
/// <para><b>How the injected value is observed.</b> The engine mutates <c>tb.GlobalVariables</c> and
/// restores it in a <c>finally</c>, so the <c>Variable</c> itself is not reachable from outside. The
/// netlist below reads it straight back out instead: <c>Vdc=Lvar</c> at a site with no unit of its
/// own, so var-unit-wins gives the node exactly the variable's own resolved value in volts. Reading
/// V(n1) IS reading the injected number, and it additionally proves the whole elaborate-per-point
/// round trip rather than only the one field.</para>
/// </summary>
public class SweptLengthUnitTests(ITestOutputHelper output)
{
    /// <summary>
    /// A one-node readout: the swept global is the DC source's own value, so the solved node voltage
    /// is numerically the injected sweep point. The resistor is only there to give the node a path to
    /// ground.
    /// </summary>
    private const string ReadoutCnl = """
        Lvar = 1 {UNIT}

        Vdc:V1  n1 0  Vdc=Lvar
        R:R1    n1 0  R=1 Ohm

        analysis DC1  type=dc
        """;

    private static double[] SweepAndReadBack(string unit, SweepSpec spec, ITestOutputHelper output)
    {
        var (lib, tb) = new CnlReader().Read(ReadoutCnl.Replace("{UNIT}", unit));
        var dc = tb.Analyses.OfType<DcAnalysis>().Single();

        var sweep = new ParametricSweepAnalysis("SW1", "Lvar", spec, dc.Name);
        var ds = ParametricSweepEngine.Run(sweep, lib, tb);

        var v = ds["V"];
        Assert.Equal("Lvar", v.Axes[0].Name);

        // V is [Lvar, node]; the netlist has exactly one non-ground node.
        int nPts = v.Axes[0].Length;
        Assert.Equal(1, v.Axes[1].Length);

        var read = new double[nPts];
        for (int i = 0; i < nPts; i++) read[i] = v.RealValues[i];

        output.WriteLine($"unit={unit,-6} axisUnit={v.Axes[0].Unit,-6} " +
                         $"axis=[{string.Join(", ", v.Axes[0].Values.Select(x => x.ToString("G6")))}] " +
                         $"readBack=[{string.Join(", ", read.Select(x => x.ToString("G6")))}]");
        return read;
    }

    // ── The two lengths the brief names by name ──────────────────────────────

    /// <summary>
    /// A <c>mm</c>-declared global swept 1 → 3 mm arrives as 1e-3, 2e-3, 3e-3 metres — the
    /// hand-computed SI values, not 1e-6 of them.
    /// </summary>
    [Fact]
    public void AMillimetreSweep_InjectsTheHandComputedSiMetres()
    {
        var read = SweepAndReadBack("mm",
            new SweepSpec(1, 3, 3, SweepAxisMode.PointCount, SweepKind.Linear, "mm"), output);

        Assert.Equal(3, read.Length);
        Assert.Equal(1e-3, read[0], 12);
        Assert.Equal(2e-3, read[1], 12);
        Assert.Equal(3e-3, read[2], 12);
    }

    /// <summary>
    /// A <c>mil</c>-declared global swept 10 → 45 mil. <c>mil</c> is the case that was wrong TWICE
    /// over — once in the table (correctly) and once in the re-attach (not at all, since it was
    /// absent from <c>_baseUnitMap</c>).
    /// </summary>
    [Fact]
    public void AMilSweep_InjectsTheHandComputedSiMetres()
    {
        var read = SweepAndReadBack("mil",
            new SweepSpec(10, 45, 2, SweepAxisMode.PointCount, SweepKind.Linear, "mil"), output);

        Assert.Equal(2, read.Length);
        Assert.Equal(10 * 2.54e-5, read[0], 15);
        Assert.Equal(45 * 2.54e-5, read[1], 15);
    }

    /// <summary>
    /// The remaining length units, including the two (<c>nm</c>, <c>cm</c>) that used to be silently
    /// unscaled at BOTH ends.
    ///
    /// <para>The coefficient is chosen per unit so the resulting SI value stays around a millivolt or
    /// larger. That is a property of the READOUT, not of the units: the DC engine's own
    /// <c>DefaultAbsTol</c> is 1e-6, so a 1 nm sweep resolves to a 1 nV source and reads back as
    /// exactly 0 — the solver at its own resolution, with the sweep axis itself still carrying the
    /// correct 1e-9. <see cref="TheSweepAxisCarriesTheBaseSymbol"/> covers <c>nm</c> at any
    /// magnitude.</para>
    /// </summary>
    [Theory]
    [InlineData("nm",    1e6, 1e-9)]
    [InlineData("cm",    1,   1e-2)]
    [InlineData("metre", 1,   1.0)]
    [InlineData("in",    1,   2.54e-2)]
    public void EveryOtherLengthUnit_InjectsTheHandComputedSiMetres(string unit, double coeff, double perUnit)
    {
        var read = SweepAndReadBack(unit,
            new SweepSpec(coeff, 2 * coeff, 2, SweepAxisMode.PointCount, SweepKind.Linear, unit), output);

        Assert.Equal(2, read.Length);
        Assert.Equal(coeff * perUnit, read[0], 12);
        Assert.Equal(2 * coeff * perUnit, read[1], 12);
    }

    /// <summary>
    /// The sweep also reads a length unit off the VAR's OWN declaration when the sweep spec carries
    /// none — the <c>EffectiveUnit</c> fallback (<c>sweep.Spec?.Unit</c> else <c>origVar?.Unit</c>) —
    /// and applies it to the VALUES, not only to the base-symbol re-attach.
    ///
    /// <para><b>Revised, and it is a behaviour change.</b> This test used to assert 10 and 45 METRES,
    /// on the reasoning that "the point of the property is that the re-attach adds nothing." That
    /// reasoning describes the re-attach correctly and the sweep wrongly: the re-attach also MARKS
    /// the injected override as unit-bearing, and a marked override is read as already-base-SI by
    /// var-unit-wins at every use site. So the two halves contradicted each other — the values were
    /// coefficients and the mark said they were not. For length it read as a 4,000× magnitude error;
    /// for frequency it was the reported bug, where <c>RFfreq = 2 GHz</c> swept 2 … 3 ran the
    /// analysis at 2 … 3 Hz with the result axis itself labelled "Hz". Scale and mark now come from
    /// the same unit, which is also the rule the sweep editor has applied at build time since
    /// brief-sweep-range-units ("the unit defaults to the swept VAR's declared unit").</para>
    /// </summary>
    [Fact]
    public void AUnitlessSpecOverAUnitBearingGlobal_InheritsTheVarsOwnUnit()
    {
        var read = SweepAndReadBack("mil",
            new SweepSpec(10, 45, 2, SweepAxisMode.PointCount, SweepKind.Linear, ""), output);

        // 10 mil and 45 mil in metres — the same values a spec that spelled out unit:"mil" produces,
        // which is the property that matters: where the unit is written must not change the physics.
        Assert.Equal(10 * 2.54e-5, read[0], 15);
        Assert.Equal(45 * 2.54e-5, read[1], 15);
    }

    /// <summary>
    /// The frequency face of the same rule, in the shape the bug was reported in: a VAR declared
    /// <c>2 GHz</c>, a sweep range typed as the bare coefficients 2 … 3, and no unit on the spec.
    /// </summary>
    [Fact]
    public void AUnitlessSpecOverAGigahertzGlobal_SweepsInGigahertz()
    {
        var read = SweepAndReadBack("GHz",
            new SweepSpec(2, 3, 3, SweepAxisMode.PointCount, SweepKind.Linear, ""), output);

        Assert.Equal(3, read.Length);
        Assert.Equal(2.0e9, read[0], 3);
        Assert.Equal(2.5e9, read[1], 3);
        Assert.Equal(3.0e9, read[2], 3);
    }

    /// <summary>
    /// The control for the pair above: a unit-less VAR with a unit-less spec is still dimensionless.
    /// There is no unit anywhere to inherit, so the coefficients are the values — this is what keeps
    /// every existing sweep over a plain bias or drive variable byte-identical.
    /// </summary>
    [Fact]
    public void AUnitlessSpecOverAUnitlessGlobal_IsUnchanged()
    {
        var (lib, tb) = new CnlReader().Read(ReadoutCnl.Replace(" {UNIT}", ""));
        Assert.Null(tb.GlobalVariables.Single().Unit);

        var dc = tb.Analyses.OfType<DcAnalysis>().Single();
        var sweep = new ParametricSweepAnalysis("SW1", "Lvar",
            new SweepSpec(2, 3, 2, SweepAxisMode.PointCount, SweepKind.Linear, ""), dc.Name);

        var ds = ParametricSweepEngine.Run(sweep, lib, tb);
        Assert.Equal("", ds["V"].Axes[0].Unit);
        Assert.Equal(2.0, ds["V"].RealValues[0], 12);
        Assert.Equal(3.0, ds["V"].RealValues[1], 12);
    }

    // ── The frequency control ────────────────────────────────────────────────

    /// <summary>
    /// <b>The control that makes the length result meaningful.</b> Frequency already satisfied the
    /// scale-1 property (<c>BaseUnit("GHz")</c> = <c>"Hz"</c>, scale 1.0), so a <c>GHz</c> sweep must
    /// be bit-identical before and after this brief. Nothing in the frequency block of the units
    /// table was touched.
    /// </summary>
    [Fact]
    public void AGigahertzSweep_IsUnchanged()
    {
        Assert.Equal("Hz", Units.BaseUnit("GHz"));
        Assert.Equal(1.0, Units.Scale("Hz"));

        var read = SweepAndReadBack("GHz",
            new SweepSpec(1, 3, 3, SweepAxisMode.PointCount, SweepKind.Linear, "GHz"), output);

        Assert.Equal(1e9, read[0], 6);
        Assert.Equal(2e9, read[1], 6);
        Assert.Equal(3e9, read[2], 6);
    }

    /// <summary>
    /// The sweep AXIS is tagged with the base symbol, which R-len-3 notes becomes the axis's unit in
    /// the published <c>DataSet</c> (and so an axis label in the Data Display, and a field in the
    /// <c>.npy</c>). "metre" is what a length axis now reads as — a decision, not an accident: it is
    /// a real unit word and needs no separate display map.
    /// </summary>
    [Theory]
    [InlineData("mm",  "metre")]
    [InlineData("mil", "metre")]
    [InlineData("nm",  "metre")]
    [InlineData("GHz", "Hz")]
    public void TheSweepAxisCarriesTheBaseSymbol(string unit, string expectedAxisUnit)
    {
        var (lib, tb) = new CnlReader().Read(ReadoutCnl.Replace("{UNIT}", unit));
        var dc = tb.Analyses.OfType<DcAnalysis>().Single();

        var sweep = new ParametricSweepAnalysis("SW1", "Lvar",
            new SweepSpec(1, 2, 2, SweepAxisMode.PointCount, SweepKind.Linear, unit), dc.Name);

        var ds = ParametricSweepEngine.Run(sweep, lib, tb);
        Assert.Equal(expectedAxisUnit, ds["V"].Axes[0].Unit);
    }
}

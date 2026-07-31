using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Freshly-placed microstrip defaults are round numbers computed for the PLACING technology.
//
//  Owner-reported (2026-07-30): a new MLIN on a PCB workspace showed W = 114.1732 mil. That came
//  from converting a fixed 2.9 mm registry baseline into mil — arithmetically right, and wrong twice
//  over: it is an ugly starting point, and 2.9 mm is 50 Ω on 1.6 mm FR-4, NOT on 20 mil RO4350B,
//  where 50 Ω is ~42 mil. Widths are now SYNTHESISED for 50 Ω on the technology's own substrate and
//  rounded in the technology's own unit; lengths are round numbers.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class MicrostripNiceDefaultsTests
{
    private static List<EditableParameter> Defaults(SymbolKind kind) =>
        ComponentTypeRegistry.DefaultParameters(kind, 0)
            .Select(dp => new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            })
            .ToList();

    private static (string Expr, string Unit) Param(List<EditableParameter> ps, string name)
    {
        var p = Assert.Single(ps.Where(x => x.Name == name));
        return (p.Expression, p.Unit);
    }

    private static double Num(string expr) =>
        double.Parse(expr, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The shipped RO4350B 20 mil PCB technology — the owner's own worked example.</summary>
    private static Technology Ro4350B20Mil()
    {
        var entry = ShippedTechnologies.All.First(e => e.Id.Contains("20mil", StringComparison.OrdinalIgnoreCase));
        return ShippedTechnologies.Load(entry);
    }

    private static Technology Mmic() =>
        ShippedTechnologies.Load(ShippedTechnologies.All.First(e =>
            e.Id.Contains("mmic", StringComparison.OrdinalIgnoreCase)));

    // ── The headline: the owner's worked example ─────────────────────────────

    [Fact]
    public void Mlin_OnRo4350B20Mil_Width_Is42Mil_Exactly()
    {
        var ps = Defaults(SymbolKind.Mlin);
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(ps, Ro4350B20Mil(), SymbolKind.Mlin);

        var (expr, unit) = Param(ps, "W");
        Assert.Equal("mil", unit);
        Assert.Equal(42.0, Num(expr));      // owner: "the 50 ohm line width should be 42 mil"
    }

    [Fact]
    public void TheOldValue_IsGone()
    {
        var ps = Defaults(SymbolKind.Mlin);
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(ps, Ro4350B20Mil(), SymbolKind.Mlin);

        // 114.1732 mil was 2.9 mm converted — the number the owner reported.
        Assert.DoesNotContain(ps, p => p.Expression.StartsWith("114.1", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryMilDefault_IsAWholeNumberOfMils()
    {
        foreach (var kind in new[] { SymbolKind.Mlin, SymbolKind.MBend, SymbolKind.MTee,
                                     SymbolKind.MCross, SymbolKind.Mtaper, SymbolKind.Mklopf })
        {
            var ps = Defaults(kind);
            MicrostripSubstrateInjection.ApplyTechnologyDefaults(ps, Ro4350B20Mil(), kind);

            foreach (var p in ps.Where(p => p.Dimension == UnitDimension.Length))
            {
                Assert.Equal("mil", p.Unit);
                var v = Num(p.Expression);
                Assert.True(Math.Abs(v - Math.Round(v)) < 1e-9,
                    $"{kind}.{p.Name} = {p.Expression} mil is not a whole number of mils");
            }
        }
    }

    // ── Lengths are round numbers ────────────────────────────────────────────

    [Fact]
    public void Lengths_AreRoundNumbers_AndMklopfIsLonger()
    {
        var mlin = Defaults(SymbolKind.Mlin);
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(mlin, Ro4350B20Mil(), SymbolKind.Mlin);
        Assert.Equal(400.0, Num(Param(mlin, "L").Expr));       // owner's suggestion

        var klopf = Defaults(SymbolKind.Mklopf);
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(klopf, Ro4350B20Mil(), SymbolKind.Mklopf);
        Assert.Equal(800.0, Num(Param(klopf, "L").Expr));      // a taper needs real length
    }

    [Fact]
    public void MklopfOffset_StaysZero_NotRewrittenToANiceLength()
    {
        var ps = Defaults(SymbolKind.Mklopf);
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(ps, Ro4350B20Mil(), SymbolKind.Mklopf);

        // Offset is Length-dimensioned but means "straight taper" at 0 — only L gets the nice value.
        Assert.Equal(0.0, Num(Param(ps, "Offset").Expr));
    }

    // ── Every arm of a junction is the 50 Ω line ─────────────────────────────

    [Fact]
    public void JunctionArms_AllGetTheSame50OhmWidth()
    {
        var tee = Defaults(SymbolKind.MTee);
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(tee, Ro4350B20Mil(), SymbolKind.MTee);
        Assert.Equal(42.0, Num(Param(tee, "W1").Expr));
        Assert.Equal(42.0, Num(Param(tee, "W2").Expr));
        Assert.Equal(42.0, Num(Param(tee, "W3").Expr));

        var cross = Defaults(SymbolKind.MCross);
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(cross, Ro4350B20Mil(), SymbolKind.MCross);
        foreach (var n in new[] { "W1", "W2", "W3", "W4" })
            Assert.Equal(42.0, Num(Param(cross, n).Expr));
    }

    [Fact]
    public void Taper_StillTapers_NarrowEndIsThe100OhmWidth()
    {
        var ps = Defaults(SymbolKind.Mtaper);
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(ps, Ro4350B20Mil(), SymbolKind.Mtaper);

        double w1 = Num(Param(ps, "W1").Expr);
        double w2 = Num(Param(ps, "W2").Expr);

        Assert.Equal(42.0, w1);                       // wide end = 50 Ω
        Assert.True(w2 < w1, $"a taper must taper: W1={w1}, W2={w2}");
        Assert.True(w2 > 0, "the narrow end must still be a real width");
    }

    // ── Other unit systems ───────────────────────────────────────────────────

    [Fact]
    public void MmicDefaults_AreWholeMicrons_AtADieAppropriateLength()
    {
        var ps = Defaults(SymbolKind.Mlin);
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(ps, Mmic(), SymbolKind.Mlin);

        var (wExpr, wUnit) = Param(ps, "W");
        Assert.Equal("µm", wUnit);
        var w = Num(wExpr);
        Assert.True(Math.Abs(w - Math.Round(w)) < 1e-9, $"W = {wExpr} µm is not a whole micron");
        Assert.True(w > 0, "a synthesised width must be positive");

        // 400 mil on a die would be absurd; the MMIC length is die-scale.
        Assert.Equal(500.0, Num(Param(ps, "L").Expr));
    }

    [Fact]
    public void NoTechnology_LeavesTheMillimetreRegistryDefaultsUntouched()
    {
        var ps = Defaults(SymbolKind.Mlin);
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(ps, (Technology?)null, SymbolKind.Mlin);

        Assert.Equal(("2.9", "mm"), Param(ps, "W"));
        Assert.Equal(("10", "mm"),  Param(ps, "L"));
    }

    // ── Graceful degradation ─────────────────────────────────────────────────

    [Fact]
    public void SubstrateUnresolvable_StillProducesARoundedNumber_NeverALongDecimal()
    {
        // A technology with a display unit but no usable stackup: width synthesis cannot run, so the
        // converted registry default stands — but it must still be rounded, or 114.1732 comes back.
        var bare = new Technology { Name = "Bare", DefaultDisplayUnit = LayoutUnit.Mil };

        var ps = Defaults(SymbolKind.Mlin);
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(ps, bare, SymbolKind.Mlin);

        var (expr, unit) = Param(ps, "W");
        Assert.Equal("mil", unit);
        var v = Num(expr);
        Assert.True(Math.Abs(v - Math.Round(v)) < 1e-9, $"W = {expr} mil must be rounded even here");
    }
}

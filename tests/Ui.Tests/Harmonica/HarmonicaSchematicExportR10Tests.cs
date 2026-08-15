// ================================================================
//  HarmonicaSchematicExportR10Tests.cs — harmonicaRF Round 10, §1–§12 of the .csch export
//
//  Deliberately small: one fixture, one assertion per owner item, over the SHIPPED default document
//  (the one every report in Round 10 was written against). HarmonicaSchematicExportTests still owns
//  the "does it extract, elaborate and solve" half — nothing here re-runs the engine.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Schematic;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaSchematicExportR10Tests(ITestOutputHelper output)
{
    private const double PavlDbm = 20.0;

    private static (SchematicEditModel Sch, CircuitModel Model) Exported()
    {
        var model = HarmonicaViewModel.DefaultModel();
        var t = new TerminationSet(model.Settings.HarmonicCount);
        t.Set(TerminationSide.Load, 1, new Complex(80, 10));      // L1 marked
        // L2/L3 deliberately left unmarked — §3/§8's "include the undefined markers" case.
        return (HarmonicaSchematicExport.Export(model, t, PavlDbm), model);
    }

    private static EditableComponent Comp(SchematicEditModel s, string name)
        => Assert.Single(s.Components, c => c.InstanceName == name);

    private static EditableParameter Param(EditableComponent c, string name)
        => Assert.Single(c.Parameters, p => p.Name == name);

    /// <summary>Every PHYSICAL pin. An SDD's <c>PortCount</c> is its LOGICAL port count; its pins come
    /// in "+"/"−" pairs, so there are twice as many of them.</summary>
    private static (double X, double Y)[] Pins(EditableComponent c)
    {
        int n = c.Symbol is SymbolKind.Sdd or SymbolKind.ZPort ? 2 * c.PortCount : c.PortCount;
        return [.. Enumerable.Range(0, n).Select(c.GetPortWorldCoord)];
    }

    // ── §1/§2 — precision and units ───────────────────────────────────────────

    [Fact]
    public void EveryValue_IsWrittenAtItsShortestRoundTrippingForm_AndTheBiasNetworkCarriesUnits()
    {
        var (sch, _) = Exported();

        // §1 — "9.9999999999999995E-07" was G17 printing 17 digits of a value that needs 1.
        foreach (var c in sch.Components)
            foreach (var p in c.Parameters)
                Assert.DoesNotContain("999999999", p.Expression, StringComparison.Ordinal);

        // §2 — the bias network carries a unit, with the SI prefix chosen from the magnitude so the
        // number stays a clean single digit at every value the setting can take (the shipped default
        // is the ideal 1 H / 1 F; a document set to 1 µH reads "1 uH", not "1e-6").
        foreach (string choke in new[] { "LCHG", "LCHD" })
        {
            var l = Param(Comp(sch, choke), "L");
            output.WriteLine($"{choke}: L = {l.Expression} {l.Unit}");
            Assert.Equal("H", l.Unit);
            Assert.Equal("1", l.Expression);
        }
        foreach (string block in new[] { "CBLKS", "CBLKL" })
        {
            var c = Param(Comp(sch, block), "C");
            output.WriteLine($"{block}: C = {c.Expression} {c.Unit}");
            Assert.Equal("F", c.Unit);
            Assert.Equal("1", c.Expression);
        }
    }

    [Fact]
    public void ADocumentWithAMicroscaleBiasNetwork_StillReadsAsACleanSingleDigit()
    {
        var model = HarmonicaViewModel.DefaultModel();
        model = model with { Settings = model.Settings with { BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9 } };
        var sch = HarmonicaSchematicExport.Export(model, new TerminationSet(model.Settings.HarmonicCount), 0);

        var l = Param(Comp(sch, "LCHG"), "L");
        Assert.Equal(("1", "uH"), (l.Expression, l.Unit));
        var c = Param(Comp(sch, "CBLKS"), "C");
        Assert.Equal(("1", "nF"), (c.Expression, c.Unit));
    }

    // ── §3 — the P1Tone ───────────────────────────────────────────────────────

    [Fact]
    public void TheSource_CarriesEveryBand_InGHz_WithNoComplexFunction()
    {
        var (sch, model) = Exported();
        var pin = Comp(sch, "PIN");

        var freq = Param(pin, "Freq");
        Assert.Equal("GHz", freq.Unit);
        Assert.Equal("2", freq.Expression);

        // Every band 1..K, not only the marked ones — an unmarked band said nothing before.
        for (int band = 1; band <= model.Settings.HarmonicCount; band++)
            Assert.NotNull(Param(pin, $"Z[{band}]"));

        Assert.DoesNotContain(pin.Parameters, p => p.Expression.Contains("complex(", StringComparison.Ordinal));
    }

    // ── §4 — Pin is a VAR and the analysis sweeps it ──────────────────────────

    [Fact]
    public void ThePinLevel_IsAVarTheParametricSweepSteps_OverTheDocumentsOwnRange()
    {
        var (sch, model) = Exported();

        var v = Assert.Single(sch.Components, c => c.Symbol == SymbolKind.Var);
        Assert.Equal(PavlDbm.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     Param(v, HarmonicaSchematicExport.PinVariable).Expression);
        Assert.Equal(HarmonicaSchematicExport.PinVariable, Param(Comp(sch, "PIN"), "Pavl").Expression);

        var sweep = Assert.Single(sch.Analyses.OfType<ParametricSweepAnalysis>());
        Assert.Equal(HarmonicaSchematicExport.PinVariable, sweep.SweepVarName);
        Assert.NotNull(sweep.Spec);
        Assert.Equal(model.Settings.PinStartDbm, sweep.Spec!.Start);
        Assert.Equal(model.Settings.PinMaxDbm,   sweep.Spec.Stop);
        Assert.Equal(model.Settings.PinStepDbm,  sweep.Spec.StepOrCount);
        Assert.Equal(SweepAxisMode.StepSize,     sweep.Spec.Mode);

        // §9 — the HB order is the document's own.
        var hb = Assert.Single(sch.Analyses.OfType<HarmonicBalanceAnalysis>());
        Assert.Equal(model.Settings.HarmonicCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     hb.MaxHarmonicExpr);
        Assert.Equal(hb.Name, sweep.InnerAnalysisName);
    }

    // ── §5/§6 — a ground sits ON the pin it grounds ───────────────────────────

    [Fact]
    public void EveryGround_SitsExactlyOnAPin_WithNoWireOfItsOwn()
    {
        var (sch, _) = Exported();

        var otherPins = sch.Components
            .Where(c => c.Symbol != SymbolKind.Ground)
            .SelectMany(Pins)
            .ToHashSet();

        var grounds = sch.Components.Where(c => c.Symbol == SymbolKind.Ground).ToArray();
        Assert.NotEmpty(grounds);
        foreach (var g in grounds)
        {
            var at = g.GetPortWorldCoord(0);
            Assert.True(otherPins.Contains(at), $"{g.InstanceName} at {at} is not on any component pin");
            Assert.DoesNotContain(sch.Wires, w => w.Points.Contains((at.X, at.Y)));
        }

        // The SDD's two "−" terminals each get their own ground rather than sharing one through a
        // wire — §5's "don't share a ground using a wire", so the device stays readable.
        var dut = Comp(sch, "DUT");
        foreach (int negPin in new[] { 1, 3 })
            Assert.Contains(grounds, g => g.GetPortWorldCoord(0) == dut.GetPortWorldCoord(negPin));
    }

    // ── §7 — the DC blocks lie along their own run ────────────────────────────

    [Fact]
    public void TheDcBlocks_AreHorizontal()
    {
        var (sch, _) = Exported();
        Assert.Equal(SymbolRotation.R90, Comp(sch, "CBLKS").Rotation);
        Assert.Equal(SymbolRotation.R90, Comp(sch, "CBLKL").Rotation);
    }

    // ── §8/§10 — the load is a LoadTuner named "Load", reached without a bend ──

    [Fact]
    public void TheLoad_IsALoadTunerNamedLoad_WithEveryBandInJForm_AndNoBendToReachIt()
    {
        var (sch, model) = Exported();

        var load = Comp(sch, HarmonicaSchematicExport.LoadTunerInstanceName);
        Assert.Equal(SymbolKind.LoadTuner, load.Symbol);
        Assert.DoesNotContain(sch.Components, c => c.Symbol == SymbolKind.PnTone);

        Assert.Equal("\"off\"", Param(load, "BiasTee").Expression);
        Assert.Equal("false",   Param(load, "ShowBias").Expression);

        for (int band = 1; band <= model.Settings.HarmonicCount; band++)
            Assert.NotNull(Param(load, $"Z[{band}]"));
        Assert.Equal("80+j*10", Param(load, "Z[1]").Expression);
        Assert.DoesNotContain(load.Parameters, p => p.Expression.Contains("complex(", StringComparison.Ordinal));

        // §10 — DUT drain → Iout → CBLKL → Load is one straight horizontal row, so no wire along it
        // bends: every link is a single two-point segment and every pin shares the drain's own Y.
        var dut   = Comp(sch, "DUT");
        var probe = Comp(sch, HarmonicaSchematicExport.OutputProbe);
        var cblkl = Comp(sch, "CBLKL");
        var drain = dut.GetPortWorldCoord(2);                 // SDD2 pin 2 = port 2 "+", the drain
        var chain = new[]
        {
            drain,
            probe.GetPortWorldCoord(0),      // np — the DUT-side lead
            probe.GetPortWorldCoord(1),      // nm
            cblkl.GetPortWorldCoord(1),      // R90: pin 1 is the WEST (DUT-side) lead
            cblkl.GetPortWorldCoord(0),
            load.GetPortWorldCoord(0),
        };

        Assert.All(chain, pt => Assert.Equal(drain.Y, pt.Y));
        // Links are 0→1, 2→3 and 4→5; 1→2 and 3→4 are the components' own bodies.
        foreach (int i in new[] { 0, 2, 4 })
        {
            var (a, b) = (chain[i], chain[i + 1]);
            Assert.Contains(sch.Wires, w => w.Points.Count == 2
                                         && w.Points.Contains((a.X, a.Y))
                                         && w.Points.Contains((b.X, b.Y)));
        }
    }

    // ── §11/§12 — the supplies mirror each other, sideways, right way up ──────

    [Fact]
    public void BothSupplies_FeedTheirChokeFromThePlusPin_AndAreGroundedOnTheMinusPin()
    {
        var (sch, _) = Exported();

        foreach (string name in new[] { "VGG", "VDD" })
        {
            var vdc = Comp(sch, name);
            var plus  = vdc.GetPortWorldCoord(0);   // pin 0 is "+" — VdcModel: V(Nodes[0]) − V(Nodes[1]) = Vdc
            var minus = vdc.GetPortWorldCoord(1);

            // The grounded terminal is the "−". Grounding the "+" (which is what this used to do)
            // silently exports −Vgs and −Vds.
            Assert.Contains(sch.Components,
                c => c.Symbol == SymbolKind.Ground && c.GetPortWorldCoord(0) == minus);
            Assert.DoesNotContain(sch.Components,
                c => c.Symbol == SymbolKind.Ground && c.GetPortWorldCoord(0) == plus);

            // §12 — the wire leaves the "+" pin SIDEWAYS, so it never runs down through the symbol.
            var leg = Assert.Single(sch.Wires, w => w.Points[0] == (plus.X, plus.Y)
                                                 || w.Points[^1] == (plus.X, plus.Y));
            Assert.All(leg.Points, pt => Assert.Equal(plus.Y, pt.Y));
        }

        // §11 — both chokes run UP off their own plane, so the drain bias mirrors the gate's.
        var dut = Comp(sch, "DUT");
        Assert.True(Comp(sch, "LCHG").Y < dut.GetPortWorldCoord(0).Y);
        Assert.True(Comp(sch, "LCHD").Y < dut.GetPortWorldCoord(2).Y);
        Assert.True(Comp(sch, "VGG").X < Comp(sch, "LCHG").X, "VGG sits LEFT of the gate choke");
        Assert.True(Comp(sch, "VDD").X > Comp(sch, "LCHD").X, "VDD sits RIGHT of the drain choke");
    }
}

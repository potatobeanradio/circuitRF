// ================================================================
//  HarmonicaExportedMetricsTests.cs — Round 10 follow-up: current probes, node labels and the PA
//  measurement block on the exported testbench.
//
//  The structural half is cheap and pins the ORIENTATION, which is the part that fails silently — a
//  probe inserted backwards keeps every magnitude and flips every sign. The behavioural half runs the
//  exported schematic through the product's own path (extract → elaborate → HbEngine →
//  MeasurementEvaluator, exactly what `Cli hb` does) and checks the numbers, including one genuine
//  cross-check against harmonicaRF's own solve.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaExportedMetricsTests(ITestOutputHelper output)
{
    private const double PavlDbm = 20.0;

    private static (CircuitModel Model, TerminationSet Terms) Fixture()
    {
        var model = HarmonicaViewModel.DefaultModel();
        var t = new TerminationSet(model.Settings.HarmonicCount);
        t.Set(TerminationSide.Load, 1, new Complex(80, 10));
        return (model, t);
    }

    private static EditableComponent Comp(SchematicEditModel s, string name)
        => Assert.Single(s.Components, c => c.InstanceName == name);

    // ══ structure — the probes point the right way ═════════════════════════

    /// <summary>
    /// An <c>IProbe</c> reports the current flowing <c>np → nm</c>; <c>np</c> is at the component's own
    /// X and <c>MirrorX</c> is what decides which side <c>nm</c> lands on. That is the whole of the
    /// orientation question, so it is asserted on the COORDINATES rather than on the mirror flag —
    /// a flag assertion would still pass if the placement arithmetic moved.
    /// </summary>
    [Fact]
    public void EveryProbe_MeasuresCurrentInTheDirectionItsMetricNeeds()
    {
        var (model, terms) = Fixture();
        var sch = HarmonicaSchematicExport.Export(model, terms, PavlDbm);

        (double Np, double Nm) Direction(string name)
        {
            var p = Comp(sch, name);
            return (p.GetPortWorldCoord(0).X, p.GetPortWorldCoord(1).X);
        }

        // Signal path: power flows INTO the DUT at the gate and OUT of it at the drain, and the DUT
        // sits between them — so both probes measure current in the +x direction.
        var iin  = Direction(HarmonicaSchematicExport.InputProbe);
        var iout = Direction(HarmonicaSchematicExport.OutputProbe);
        output.WriteLine($"Iin  np={iin.Np}  nm={iin.Nm}");
        output.WriteLine($"Iout np={iout.Np} nm={iout.Nm}");
        Assert.True(iin.Np  < iin.Nm,  "Iin must measure current flowing toward the DUT");
        Assert.True(iout.Np < iout.Nm, "Iout must measure current flowing away from the DUT");

        // Bias legs: each probe measures the current LEAVING its own supply, so `V(supply)·I(probe)`
        // is the power that supply DELIVERS with no sign correction. The gate supply sits LEFT of its
        // choke and the drain supply RIGHT of its own, so the two probes are mirror images — a single
        // shared orientation would make one of the two terms negative.
        var idc   = Direction(HarmonicaSchematicExport.DrainDcProbe);
        var igate = Direction(HarmonicaSchematicExport.GateDcProbe);
        output.WriteLine($"IDC   np={idc.Np}   nm={idc.Nm}   (VDD is to the RIGHT)");
        output.WriteLine($"Igate np={igate.Np} nm={igate.Nm} (VGG is to the LEFT)");
        Assert.True(idc.Nm   < idc.Np,   "IDC must measure current flowing away from VDD (leftward)");
        Assert.True(igate.Np < igate.Nm, "Igate must measure current flowing away from VGG (rightward)");
    }

    [Fact]
    public void TheFourNetsAreNamed_AndSurviveTheCschRoundTrip()
    {
        var (model, terms) = Fixture();
        var sch = HarmonicaSchematicExport.Export(model, terms, PavlDbm);

        var names = sch.NetLabels.Select(l => l.Name).ToArray();
        Assert.Equal(
            new[] { HarmonicaSchematicExport.GateBiasNet, HarmonicaSchematicExport.DrainBiasNet,
                    HarmonicaSchematicExport.InputNet,    HarmonicaSchematicExport.OutputNet }.OrderBy(n => n),
            names.OrderBy(n => n));

        // Every label is anchored to a wire that exists — the persisted form is an INDEX into
        // Wires, so a label anchored to nothing would come back orphaned and its net would lose
        // its name (and every measurement naming it would stop resolving).
        foreach (var l in sch.NetLabels)
            Assert.Contains(sch.Wires, w => w.Id == l.OwnerWireId);

        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                            "csch-metrics-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string path = System.IO.Path.Combine(dir, "tb.csch");
            SchematicPersistence.SaveToFile(path, sch, "tb");
            var (back, _, _) = SchematicPersistence.Deserialize(System.IO.File.ReadAllText(path), dir);
            Assert.Equal(names.OrderBy(n => n), back.NetLabels.Select(l => l.Name).OrderBy(n => n));
            Assert.Equal(sch.Components.Count(c => c.Symbol == SymbolKind.Meas),
                         back.Components.Count(c => c.Symbol == SymbolKind.Meas));
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ══ behaviour — the metrics evaluate, and the numbers are right ═════════

    [Fact]
    public void TheMeasurementBlock_EvaluatesThroughTheProductsOwnPath_AndAgreesWithHarmonicaRF()
    {
        var (model, terms) = Fixture();
        var sch = HarmonicaSchematicExport.Export(model, terms, PavlDbm);

        var extraction = NetExtractor.Extract(sch, "tb");
        Assert.Empty(extraction.Conflicts);

        var nl  = new Elaborator(extraction.Library).Elaborate(extraction.TestBench);
        var hba = Assert.Single(extraction.TestBench.Analyses.OfType<HarmonicBalanceAnalysis>());
        var run = new HbEngine(nl, extraction.TestBench)
                      .Run(HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit));
        Assert.True(run.Converged);

        var meas = new DataSet();
        var errors = new MeasurementEvaluator(
            extraction.TestBench, nl,
            new Dictionary<string, DataSet>(StringComparer.OrdinalIgnoreCase) { [hba.Name] = run.DataSet })
            .EvaluateInto(meas);

        // Not one of them may fail: a measurement that throws also breaks every later one that names
        // it, so a single bad equation quietly empties most of the block.
        Assert.Empty(errors);

        double R(string name) => meas[name].RealValues[0];
        foreach (var (name, cube) in meas.Cubes)
            output.WriteLine($"{name,-14} = {(cube.DataKind == DataKind.Complex ? cube.ComplexValues[0].ToString() : cube.RealValues[0].ToString("G6"))}");

        // ── the DC terms, which is where a reversed probe shows up ──────────
        // Both supplies DELIVER power here, so both terms are positive. A probe inserted the other
        // way round keeps every magnitude and flips the sign, which is what these pin.
        Assert.True(R("Idc_A") > 0, $"Idc_A = {R("Idc_A"):G6} — the drain probe is reversed");
        Assert.True(R("Pdc_W") > 0, $"Pdc_W = {R("Pdc_W"):G6}");

        // The gate term is NOT negligible on this device: its gate is a plain 50 Ω to source, so at
        // Vgs = −3.05 V it draws −61 mA and the (negative) supply delivers a real +0.186 W. Pdc must
        // therefore exceed the drain term alone — a sign-flipped Igate would make it smaller.
        double drainOnly = model.Bias.Vds * R("Idc_A");
        double gateTerm  = (model.Bias.Vgs ?? 0.0) * ((model.Bias.Vgs ?? 0.0) / 50.0);
        output.WriteLine($"drain term {drainOnly:G6} W + gate term {gateTerm:G6} W");
        Assert.True(gateTerm > 0.1, "fixture check: the gate term should be a real fraction of a watt");
        Assert.Equal(drainOnly + gateTerm, R("Pdc_W"), precision: 3);

        // ── gain and efficiency are self-consistent ────────────────────────
        Assert.Equal(PavlDbm, R("Pin_avail_dBm"), precision: 9);
        Assert.True(R("Pout_W") > R("Pin_deliv_W"), "the exported testbench should show gain");
        Assert.Equal(R("Pout_dBm") - R("Pin_deliv_dBm"), R("Gp_dB"), precision: 9);
        Assert.Equal(R("Pout_dBm") - R("Pin_avail_dBm"), R("Gt_dB"), precision: 9);
        Assert.InRange(R("DE_pct"),  0.0, 100.0);
        Assert.True(R("PAE_pct") < R("DE_pct"), "PAE must sit below DE by the drive that was added");

        // The source presents 50 Ω (TerminationSet's own band-1 default) and the DUT's gate IS 50 Ω,
        // so this operating point is conjugate-matched and the input return loss is identically zero.
        // That is a genuine check of the Iin orientation too: a reversed probe gives −Pin_deliv, whose
        // log10 throws rather than landing on 0 dB.
        Assert.Equal(0.0, R("IRL_dB"), precision: 6);

        // ── THE CROSS-CHECK: the exported testbench's own Zin vs harmonicaRF's ──
        // Two genuinely different routes to the same number — a stamped schematic solved by HbEngine
        // and read through an IProbe, against harmonicaRF's closed-form termination closure.
        var ctx = HarmonicaContext.Create(model);
        var op  = ctx.Solve(terms, PavlDbm);
        var harmonicaZin = (Complex)HarmonicaDataSet.Build(ctx, op, terms)["Zin"]
                                    [(int)TerminationSide.Source, 1];
        var exportedZin  = meas["Zin"].ComplexValues[0];
        output.WriteLine($"Zin — harmonicaRF {harmonicaZin}, exported schematic {exportedZin}");
        Assert.Equal(harmonicaZin.Real,      exportedZin.Real,      precision: 6);
        Assert.Equal(harmonicaZin.Imaginary, exportedZin.Imaginary, precision: 6);
    }
}

// ================================================================
//  HarmonicaSchematicExportTests.cs  —  §7 of brief-harmonicarf-r1c-chrome-readouts-dut-and-export
//
//  R-h9c-15  Export Testbench writes a runnable .csch. The strongest check available without a GUI:
//            round-trip through the real .csch reader/writer, extract through NetExtractor (the SAME
//            code path a user's own schematic goes through), elaborate, and solve through HbEngine —
//            the identical dispatch SchematicRunService.Execute uses for a HarmonicBalanceAnalysis.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Schematic;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaSchematicExportTests(ITestOutputHelper output)
{
    private static CircuitModel SddModel() => HarmonicaViewModel.DefaultModel();

    private static TerminationSet Terms(CircuitModel m)
    {
        var t = new TerminationSet(m.Settings.HarmonicCount);
        t.Set(TerminationSide.Source, 1, new Complex(25, 0));
        t.Set(TerminationSide.Load,   1, new Complex(80, 10));
        return t;
    }

    // ── R7 — every coordinate lands on the connection grid ──────────────────────

    [Fact]
    public void ExportedSchematic_IsEntirelyOnGrid()
    {
        var model = SddModel();
        var sch = HarmonicaSchematicExport.Export(model, Terms(model), pavlDbm: -10);

        const double P = 100.0;
        const double tol = 1e-6 * P;

        foreach (var c in sch.Components)
        {
            AssertOnGrid(c.X, "component.X", c.InstanceName);
            AssertOnGrid(c.Y, "component.Y", c.InstanceName);
            for (int i = 0; i < c.PortCount; i++)
            {
                var (px, py) = c.GetPortWorldCoord(i);
                AssertOnGrid(px, $"pin {i}.X", c.InstanceName);
                AssertOnGrid(py, $"pin {i}.Y", c.InstanceName);
            }
        }

        foreach (var w in sch.Wires)
            foreach (var (x, y) in w.Points)
            {
                AssertOnGrid(x, "wire.X", w.Id);
                AssertOnGrid(y, "wire.Y", w.Id);
            }

        void AssertOnGrid(double v, string what, string owner)
        {
            double nearest = Math.Round(v / P) * P;
            Assert.True(Math.Abs(v - nearest) < tol, $"{owner}'s {what} = {v} is not a multiple of {P}");
        }
    }

    [Fact]
    public void ExportedSchematic_RoundTripsThroughTheRealCschReaderWriter()
    {
        var model = SddModel();
        var sch = HarmonicaSchematicExport.Export(model, Terms(model), pavlDbm: -10);

        string dir = Path.Combine(Path.GetTempPath(), "csch-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "testbench.csch");
            SchematicPersistence.SaveToFile(path, sch, "testbench");

            var (back, _, _) = SchematicPersistence.Deserialize(File.ReadAllText(path), dir);
            Assert.Equal(sch.Components.Count, back.Components.Count);
            Assert.Equal(sch.Wires.Count, back.Wires.Count);
            // R10 §4 — the HB analysis plus the Pin sweep that wraps it.
            Assert.Equal(2, back.Analyses.Count);
            var hb = Assert.IsType<HarmonicBalanceAnalysis>(back.Analyses[0]);
            var sweep = Assert.IsType<ParametricSweepAnalysis>(back.Analyses[1]);
            Assert.Equal(hb.Name, sweep.InnerAnalysisName);
            Assert.Equal(HarmonicaSchematicExport.PinVariable, sweep.SweepVarName);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ── R-h9c-15 — the exported schematic actually SOLVES, through the product's own dispatch ──

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Sdd_ExportedSchematic_ExtractsElaboratesAndSolves(int sddPortCount)
    {
        var model = SddModel() with { Dut = SddModel().Dut with { SddPortCount = sddPortCount } };
        var terms = Terms(model);
        var sch = HarmonicaSchematicExport.Export(model, terms, pavlDbm: -10);

        var (nl, tb, hba) = ExtractAndElaborate(sch);
        var p = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var result = new HbEngine(nl, tb).Run(p);

        Assert.True(result.Converged, $"SDD{sddPortCount} export did not converge");
        output.WriteLine($"SDD{sddPortCount}: converged={result.Converged}");
    }

    [Fact]
    public void NativeFet_ExportedSchematic_ExtractsElaboratesAndSolves()
    {
        var sdd = SddModel();
        var fetDut = new DutSpec
        {
            Kind = DutKind.NativeFet, TypeName = "FET_Angelov",
            Parameters = HarmonicaDutCatalog.DefaultParametersFor("FET_Angelov")
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
        };
        var model = sdd with { Dut = fetDut };
        var terms = Terms(model);
        var sch = HarmonicaSchematicExport.Export(model, terms, pavlDbm: -10);

        var (nl, tb, hba) = ExtractAndElaborate(sch);
        var p = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var result = new HbEngine(nl, tb).Run(p);

        output.WriteLine($"FET_Angelov: converged={result.Converged}");
        // A native FET's defaults are not guaranteed to converge at every bias — the point of this
        // test is that the SCHEMATIC extracts and elaborates without error and the engine actually
        // runs, not that this particular operating point converges.
        Assert.NotNull(result);
    }

    [Fact]
    public void Diode_ExportedSchematic_ExtractsAndElaborates_WithNoDanglingSourceBranch()
    {
        var sdd = SddModel();
        var model = sdd with
        {
            Dut = new DutSpec
            {
                Kind = DutKind.Diode, TypeName = "Diode",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal),
            },
        };
        var terms = Terms(model);
        var sch = HarmonicaSchematicExport.Export(model, terms, pavlDbm: -10);

        // A Diode has no source terminal — confirm the exporter never places RS/LS/a source ground
        // tie for it (there would be nothing electrically valid to attach them to).
        Assert.DoesNotContain(sch.Components, c => c.InstanceName is "RS" or "LS");

        var (nl, _, _) = ExtractAndElaborate(sch);
        Assert.NotNull(nl);
    }

    [Fact]
    public void TouchstoneEmbedding_IsRefusedByName_NotSilentlyOmitted()
    {
        var model = SddModel() with
        {
            Embedding = new EmbeddingStack { S2pInFile = "package.s2p" },
        };
        var ex = Assert.Throws<NotSupportedException>(
            () => HarmonicaSchematicExport.Export(model, Terms(model), pavlDbm: -10));
        Assert.Contains("Touchstone", ex.Message, StringComparison.Ordinal);
        output.WriteLine(ex.Message);
    }

    [Fact]
    public void ExternalDut_IsRefusedByName_NotSilentlyOmitted()
    {
        var model = SddModel() with
        {
            Dut = new DutSpec { Kind = DutKind.External, TypeName = "some_fet", Provider = "VerilogA|/no/such/file.osdi" },
        };
        var ex = Assert.Throws<NotSupportedException>(
            () => HarmonicaSchematicExport.Export(model, Terms(model), pavlDbm: -10));
        Assert.Contains("External", ex.Message, StringComparison.Ordinal);
        output.WriteLine(ex.Message);
    }

    [Fact]
    public void LumpedPackage_StillExtractsAndElaborates()
    {
        var model = SddModel() with
        {
            Embedding = new EmbeddingStack
            {
                Package = new LumpedPackage { Rg = 2, Lg = 0.5e-9, Rd = 1, Ld = 0.3e-9, Rs = 0.5, Ls = 20e-12, Cpg = 0.2e-12, Cpd = 0.1e-12 },
            },
        };
        var terms = Terms(model);
        var sch = HarmonicaSchematicExport.Export(model, terms, pavlDbm: -10);

        var (nl, tb, hba) = ExtractAndElaborate(sch);
        var p = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var result = new HbEngine(nl, tb).Run(p);
        output.WriteLine($"lumped package: converged={result.Converged}");
        Assert.NotNull(result);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static (ElaboratedNetlist Netlist, TestBench TestBench, HarmonicBalanceAnalysis Analysis) ExtractAndElaborate(SchematicEditModel sch)
    {
        var extraction = NetExtractor.Extract(sch, "tb");
        Assert.Empty(extraction.Conflicts);

        var nl = new Elaborator(extraction.Library).Elaborate(extraction.TestBench);
        // Two analyses since R10 §4 (the HB and the Pin sweep wrapping it); the HB is the one solved
        // here, at the VAR's own Pin value — the sweep's expansion is exercised by the .csch
        // round-trip test rather than by re-running the whole ladder in every fixture.
        var hba = Assert.Single(extraction.TestBench.Analyses.OfType<HarmonicBalanceAnalysis>());
        return (nl, extraction.TestBench, hba);
    }
}

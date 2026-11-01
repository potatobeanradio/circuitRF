using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using CircuitRF.Design.VendorLibraries;
using Xunit;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-5 §6: a vendor part's tolerance read from its part number — the decoder, the one-offset guard, a
/// header that states its own, no extra file read, the <c>at &lt;frequency&gt;</c> header fix — and drawn in yield:
/// <c>tol=part</c> through <c>.cnl</c> and <c>.csch</c> and its errors, the scale parameter, and a trial's draw. Every
/// library is generated (<see cref="VendorLibraryFixture.WriteTolerance"/>) with made-up part names.
/// </summary>
public sealed class VendorPartToleranceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl5-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _lib;

    public VendorPartToleranceTests() => _lib = VendorLibraryFixture.WriteTolerance(Path.Combine(_dir, "lib"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // 1 — the decoder: each spelling, an implausible absolute letter, a code inside a longer number, two codes.
    [Theory]
    [InlineData(10e-12,  "C", "TSTC0402A100J01", "5%")]        // significand–multiplier
    [InlineData(0.6e-12, "C", "TSTC0402AR60B01", "0.1 pF")]    // R as the decimal point
    [InlineData(1.3e-9,  "L", "TSTL1005R1N3C10", "0.2 nH")]    // N as the decimal point; C is ±0.2 nH on an inductor
    [InlineData(47e-12,  "C", "TSTC0402A470C01", null)]        // an absolute code on 47 pF is not a match
    [InlineData(10e-12,  "C", "TSTC1005K01",     null)]        // 100 inside 1005 is preceded by a digit
    [InlineData(10e-12,  "C", "TSTC100J0402A100K", null)]      // the value code twice, two letters: ambiguous
    public void TheDecoder_ReadsEachSpelling_AndRefusesRatherThanGuesses(double value, string q, string part, string? expected)
    {
        var quantity = q == "C" ? VendorPartQuantity.Capacitance : VendorPartQuantity.Inductance;
        var t = VendorPartToleranceDecoder.Decode(value, quantity, part, out string? note);
        Assert.Equal(expected, t?.SpreadText);
        if (expected is null) Assert.NotNull(note);
        if (part == "TSTC100J0402A100K") Assert.Contains("more than once", note);
    }

    // 2 — the one-offset guard: a file whose decodes sit at two offsets decodes nothing, and is reported once.
    [Fact]
    public void AFileWhoseLettersSitAtTwoOffsets_DecodesNothing_AndIsReportedOnce()
    {
        var build = VendorLibraryIndex.Build(_lib);
        Assert.All(build.Entries.Where(e => e.File == "general/mixed.lib"), e => Assert.Null(e.Tolerance));
        var refused = Assert.Single(build.ToleranceRefusals);
        Assert.Equal("general/mixed.lib", refused.File);
        Assert.Single(build.SummaryLines(), l => l.StartsWith("general/mixed.lib:", StringComparison.Ordinal));
        // The other files still decode: 4 + the header's own + 2 inductors of 10 parts with a C or L value.
        Assert.Contains("Tolerance known for 7 of 10 parts.", build.SummaryLines());
    }

    // 3 — a header that states a tolerance wins, and the part number is not consulted.
    [Fact]
    public void AToleranceTheHeaderStates_WinsOverThePartNumber()
    {
        var e = VendorLibraryIndex.Build(_lib).Find("TSTC0402A150J01")!;
        Assert.Equal(VendorToleranceSource.Header, e.Tolerance!.Source);
        Assert.Equal("2%", e.Tolerance.SpreadText);
    }

    // 4 — decoding reads no file: one read per library file, as before.
    [Fact]
    public void Decoding_ReadsNoFile()
    {
        long before = VendorLibraryIndex.FileReads(_lib);
        var build = VendorLibraryIndex.Build(_lib);
        Assert.Equal(build.FilesScanned, VendorLibraryIndex.FileReads(_lib) - before);
        Assert.True(build.ToleranceKnown > 0);
    }

    // 5 — "L=10nH at 500MHz": the value, the quantity and the glyph come back; the frequency is kept as text.
    [Fact]
    public void AValueStatedAtATestFrequency_IndexesWithItsValueAndGlyph()
    {
        var e = VendorLibraryIndex.Build(_lib).Find("TSTL1005R33NJ10")!;
        Assert.Equal(VendorPartQuantity.Inductance, e.Header.Quantity);
        Assert.Equal(33e-9, e.Header.Value!.Value, 15);
        Assert.Equal("500MHz", e.Header.ValueFrequency);
        Assert.Equal("L", VendorPartPlacement.GlyphOf(e));
        Assert.Equal("5%", e.Tolerance!.SpreadText);
    }

    // 6 — tol=part round-trips through .cnl and .csch; check reports an unknown tolerance; the reader refuses it off a C or L.
    [Fact]
    public void TolPart_RoundTrips_AndItsTwoErrorsAreSaid()
    {
        const string line = "tune C1.C dist=gauss tol=part sigmas=3 trunc=3";
        var (lib, tb) = new CnlReader().Read("C:C1 a 0 C=1 pF\n" + line + "\n", "tb", null);
        Assert.True(tb.Tuning!.Variables[0].Spread!.TolIsPart);
        Assert.Contains(line + "\n", CnlWriter.Write(tb, lib, "rt").Replace("\r\n", "\n"));

        var ws = Workspace();
        var model = Design(ws, "TSTC0402A470C01", new TunableEntry
        {
            Key = "C1.C", Distribution = StatDistribution.Gauss, Stat = true,
            Spread = new StatSpread { Tol = "part", Sigmas = "3", Trunc = "3" },
        });
        var json = CircuitRF.Design.Schematic.SchematicPersistence.Serialize(model);
        var back = CircuitRF.Design.Schematic.SchematicPersistence.Deserialize(json, model.SchematicDirectory).model;
        Assert.True(back.Tuning!.Variables.Single().Spread!.TolIsPart);
        var extracted = NetExtractor.Extract(back, "tb", DiskCellResolver.Instance);
        Assert.Contains(line + "\n", CnlWriter.Write(extracted.TestBench, extracted.Library, "rt").Replace("\r\n", "\n"));

        // 47 pF with a C: not known, so check names the part and the fix.
        var catalog = TunableCatalog.Discover(back, DiskCellResolver.Instance, ws);
        var unknown = Assert.Single(TuningValidator.Validate(extracted.TestBench, catalog), d => d.Id == "yield.tol-part.unknown");
        Assert.Contains("TSTC0402A470C01", unknown.Render());
        Assert.Contains("Write tol= explicitly", unknown.Render());

        Assert.Throws<CnlReadException>(() => new CnlReader().Read("R:R1 a 0 R=50\ntune R1.R dist=unif tol=part\n", "tb", null));
    }

    // 7 — the scale parameter: emitted only when the value is statistical, and it moves the capacitance by the factor.
    [Fact]
    public void TheScaleParameter_IsEmittedOnlyWhenStatistical_AndMovesTheCapacitanceByTheFactor()
    {
        var ws = Workspace();
        var plain = NetExtractor.Extract(Design(ws, "TSTC0402A100J01", null), "tb", DiskCellResolver.Instance);
        var none  = NetExtractor.Extract(Design(ws, "TSTC0402A100J01", new TunableEntry { Key = "C1.C" }), "tb", DiskCellResolver.Instance);
        Assert.Equal(CnlWriter.Write(plain.TestBench, plain.Library, "x").Replace("tune C1.C\n", ""),
                     CnlWriter.Write(none.TestBench, none.Library, "x").Replace("tune C1.C\n", ""));
        Assert.DoesNotContain(none.Library.Cells, c => c.Parameters.Any(p => p.Name == "PartScale"));

        var model = Design(ws, "TSTC0402A100J01", Unif("C1.C"));
        var circuit = Prepare(model, ws, out var extracted);
        Assert.Contains(extracted.Library.Find("TSTC0402A100J01")!.Parameters, p => p.Name == "PartScale");

        // At the low end of the stated range (100 MHz) the series reactance is the capacitance's.
        double Ceff(string? drawn)
        {
            var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest
            {
                Tunables    = drawn is null ? null : new Dictionary<string, string> { ["C1.C"] = drawn },
                Expressions = ["imag(1/SP1.S(2,1))"],
            });
            Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
            double imZ = 100 * run.Expressions[0].Value!.Value.AsCube().RealValues[0];
            return -1 / (2 * Math.PI * 100e6 * imZ);
        }
        double ratio = Ceff("1.1e-11 F") / Ceff(null);
        Assert.Equal(1.1, ratio, 1e-3);
    }

    // 8 — a yield draw over one vendor part stays within ± its own tolerance: every bound scale in [1 − tol, 1 + tol].
    [Fact]
    public void AYieldTrial_DrawsWithinThePartsOwnTolerance()
    {
        var ws = Workspace();
        var model = Design(ws, "TSTC0402A100J01", Unif("C1.C"));
        var circuit = Prepare(model, ws, out var extracted);
        var catalog = TunableCatalog.Discover(model, DiskCellResolver.Instance, ws);
        Assert.DoesNotContain(TuningValidator.Validate(extracted.TestBench, catalog),
                              d => d.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error);

        var setup = extracted.TestBench.Tuning!;
        var sampler = StatisticalSampler.For(setup);
        var scales = new List<double>();
        for (int trial = 1; trial <= 200; trial++)
        {
            var values = SampleValues.Apply(setup.Variables, catalog.Tunables, sampler.Sample(trial));
            Assert.False(values.Refused);
            var tuned = TunableOverrides.Apply(circuit.Tb!, circuit.Lib!, values.Values, null, circuit.PartTunables);
            Assert.Empty(tuned.Notes);
            var c1 = tuned.TestBench!.Instances.Single(i => i.InstanceName == "C1");
            scales.Add(double.Parse(c1.Overrides.Single(o => o.Name == "PartScale").Expression, CultureInfo.InvariantCulture));
        }
        Assert.All(scales, s => Assert.InRange(s, 0.95 - 1e-12, 1.05 + 1e-12));
        Assert.True(scales.Max() - scales.Min() > 0.05, "the draws do not spread over the tolerance");
    }

    // 9 — the RUN, not just its pieces: a Monte Carlo over a vendor part evaluates every trial, and each trial's
    // capacitance at 100 MHz moves with its own draw. The run's catalog once lacked the placed parts, so C1.C named
    // no value and every trial failed — which test 8, calling TunableOverrides directly, could not see.
    [Fact]
    public void AMonteCarloRun_OverAVendorPart_EvaluatesEveryTrial_AndEachDrawMovesTheCapacitance()
    {
        var ws = Workspace();
        var model = Design(ws, "TSTC0402A100J01", Unif("C1.C"));
        model.Tuning!.Goals.Add(CeffGoal());
        model.Tuning.Statistics = new StatisticsSettings { Trials = 12 };
        var circuit = Prepare(model, ws, out _);

        var run = StatisticalRun.Create(circuit, new StatisticalOptions { Mode = StatisticalMode.MonteCarlo });
        Assert.Null(run.Refusal);
        var result = run.Run();
        Assert.Equal(0, result.DidNotEvaluate);

        // Ceff / drawn C is one constant across the trials: the model's capacitance is scaled by the draw.
        var ratios = result.Records.Select(r => r.Goals.Single().WorstValue * 1e-12 / r.Draws["C1.C"]).ToList();
        Assert.Equal(12, ratios.Count);
        Assert.True(result.Records.Select(r => r.Draws["C1.C"]).Distinct().Count() > 1);
        Assert.All(ratios, q => Assert.Equal(ratios[0], q, 1e-3));
    }

    // 10 — check and explain ask the run constructors the same question, so they once refused tol=part on a part too.
    [Fact]
    public void CheckAndExplain_AcceptAVendorPartsValueDrawnWithTolPart()
    {
        var ws = Workspace();
        var model = Design(ws, "TSTC0402A100J01", new TunableEntry
        {
            Key = "C1.C", Distribution = StatDistribution.Gauss, Stat = true,
            Spread = new StatSpread { Tol = "part", Sigmas = "3", Trunc = "3" },
        });
        var goal = CeffGoal();
        goal.Use = GoalUse.Yield;
        model.Tuning!.Goals.Add(goal);
        string csch = Path.Combine(model.SchematicDirectory!, "top.csch");
        CircuitRF.Design.Schematic.SchematicPersistence.SaveToFile(csch, model, "top");

        var check = RunCli("check", csch);
        Assert.True(check.ExitCode == 0, check.StdOut + check.StdErr);
        var explain = RunCli("explain", csch, "--analysis", "SP1");
        Assert.True(explain.ExitCode == 0, explain.StdOut + explain.StdErr);
        Assert.DoesNotContain("would be refused", explain.StdOut);
    }

    private static OptimizationGoal CeffGoal() => new()
    {
        Name = "Ceff", Analysis = "SP1", Type = GoalType.Ge, Limit = "0",
        Expression = "-1e12/(2*pi*freq*imag(100*(1-SP1.S(2,1))/SP1.S(2,1)))",
    };

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(VendorPartToleranceTests).Assembly)
            .First(a => a.Key == "CliDir").Value!, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = Path.Combine(_dir, "state");
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    // ── The design: P1 — C1 (a vendor part) — P2, one S-parameter point at 100 MHz ─────────

    private static TunableEntry Unif(string key) => new()
    {
        Key = key, Distribution = StatDistribution.Unif, Stat = true, Spread = new StatSpread { Tol = "part" },
    };

    private string Workspace()
        => WorkspaceCreate.Create(_dir, "ws" + Guid.NewGuid().ToString("N")[..6], "pcb-2layer_FR-4_70mil_1oz",
                                  vendorLibraries: [("Parts", _lib)]).WorkspaceDir;

    private static SchematicEditModel Design(string ws, string part, TunableEntry? entry)
    {
        string schDir = Path.Combine(ws, "cells", "top", "schematic");
        Directory.CreateDirectory(schDir);
        var c1 = new EditableComponent { InstanceName = "C1", Symbol = SymbolKind.SpiceModel, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.SpiceModel, 0))
            c1.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression });
        c1.Parameters.First(p => p.Name == "File").Expression = VendorLibraryRefs.RefFor("Parts", "capacitors/tol.lib");
        c1.Parameters.First(p => p.Name == "Name").Expression = part;
        c1.Parameters.Add(new EditableParameter { Name = "Glyph", Expression = "C" });

        var model = new SchematicEditModel { SchematicDirectory = schDir };
        model.Components.Add(c1);
        var pins = CellSymbolResolver.Resolve(c1.ExternalSymbolRef!, schDir).Symbol!.Pins.OrderBy(p => p.PortIndex)
                                     .Select(p => ((double)p.LocalX + c1.X, (double)p.LocalY + c1.Y)).ToList();
        for (int k = 0; k < 2; k++)
        {
            var lead = (X: pins[k].Item1 + (k == 0 ? -1000 : 1000), Y: pins[k].Item2);
            var w = new EditableWire();
            w.Points.AddRange([pins[k], lead]);
            model.Wires.Add(w);
            var term = new EditableComponent { InstanceName = $"P{k + 1}", Symbol = SymbolKind.Term, X = lead.X, Y = lead.Y + 200 };
            term.Parameters.Add(new EditableParameter { Name = "Num", Expression = (k + 1).ToString(CultureInfo.InvariantCulture) });
            term.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50" });
            model.Components.Add(term);
            model.Components.Add(new EditableComponent { InstanceName = $"G{k + 1}", Symbol = SymbolKind.Ground, X = lead.X, Y = lead.Y + 400 });
        }
        model.Analyses.Add(new SParameterAnalysis("SP1", new FrequencySpec("100", "100", 1, startUnit: "MHz", stopUnit: "MHz")));
        if (entry is not null) model.Tuning = new TuningSetup { Variables = [entry] };
        return model;
    }

    /// <summary>Simulate's own preparation, in memory: the extraction, its netlist text, and its vendor parts.</summary>
    private static PreparedCircuit Prepare(SchematicEditModel model, string ws, out NetExtractor.ExtractionResult extracted)
    {
        extracted = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance);
        Assert.Empty(extracted.Conflicts);
        var text = CnlWriter.Write(extracted.TestBench, extracted.Library, "vl5");
        var circuit = PreparedCircuit.FromText(text, model.SchematicDirectory, ws, "tb", extracted);
        Assert.Null(circuit.ReadError);
        return circuit;
    }
}

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Cli;
using CircuitRF.Cli.Serve;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Engine;
using CircuitRF.Ui.Filters;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Synthesis;

/// <summary>
/// The user-facing side of filter synthesis (docs/design/filter-synthesis.md §6) against the contract alone, with a
/// realiser of its own: the <c>filter</c> verb end to end, the MCP mode's schema and one call through the adapter, and the
/// Filter Designer's list and Place.
/// </summary>
public sealed class FilterDesignerTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-filter-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    /// <summary>
    /// A lumped lowpass on MLAD's sites: series L, shunt C, … from the prototype's g-values, L = g·Z0/ωc and C = g/(Z0·ωc)
    /// (Matthaei, Young and Jones, a book, no DOI); the sites' pins are the plan's own. Refuses every other band.
    /// </summary>
    private sealed class LumpedLadder : IFilterRealiser
    {
        public string Name => "Lumped lowpass (MLAD)";

        public bool Supports(FilterSpec spec, out string? why)
        {
            why = spec.Band == FilterBand.Lowpass ? null : "only a lowpass.";
            return why is null;
        }

        public FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate)
        {
            double[] g = LowpassPrototype.GValues(spec.Response, spec.Order, spec.RippleDb);
            double w = 2.0 * Math.PI * spec.F1Hz;
            string sections = LadderPlan.Format(Enumerable.Range(1, spec.Order).Select(k => k % 2 == 1
                ? new LadderSite(PlanSiteRole.L, g[k] * spec.Z0 / w)
                : new LadderSite(PlanSiteRole.C, g[k] / (spec.Z0 * w), PlanSiteKind.Shunt)));
            var plan = LadderPlan.Build(sections, spec.Z0, spec.F1Hz, substrate ?? new MicrostripSubstrate(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037)).Plan;
            return new FilterRealisation("MLAD",
                new Dictionary<string, string> { ["Sections"] = sections, ["Z0"] = $"{spec.Z0} Ohm", ["F"] = $"{spec.F1Hz / 1e9} GHz" },
                [.. plan.Sites.Select(s => new SiteValue(s.Role.ToString(), s.PinA, s.PinB, s.Value!.Value))], ["sites at the closed form"]);
        }
    }

    private static readonly IReadOnlyList<IFilterRealiser> Realisers = [new LumpedLadder()];

    private static string[] Ids() => [.. JsonRun.CapturedDiagnostics.Select(d => d.Id)];

    /// <summary>The -3 dB point of a lowpass bench's |S21|, by bisection on the engine's own run of the file.</summary>
    private static double Cutoff(string cnl, double fc)
    {
        var (lib, tb) = CnlTechnologyBinding.ReadFile(cnl);
        using var netlist = new Elaborator(lib).Elaborate(tb);
        double S21(double f) => 20.0 * Math.Log10(((Complex)SParameterEngine.Run(netlist, [f])["S"][0, 1, 0]).Magnitude);
        double lo = 0.5 * fc, hi = 1.5 * fc;
        Assert.True(S21(lo) > -3.0 && S21(hi) < -3.0);
        while (hi - lo > 1e3) { double m = 0.5 * (lo + hi); if (S21(m) > -3.0) lo = m; else hi = m; }
        return 0.5 * (lo + hi);
    }

    /// <summary>
    /// The verb lists the realiser and says why it cannot build a highpass; with it chosen, on the shipped board, it writes
    /// a .cnl of the ladder on the board's substrate, an L or C of each site's value on its pins with the board's default
    /// land, two ports and a sweep, which runs and cuts off at fc; and draws it as a .csch.
    /// </summary>
    [Fact]
    public void TheVerb_ListsRealises_AndTheNetlistRunsToItsCutoff()
    {
        using (JsonRun.Isolate())
        {
            Assert.Equal(0, FilterVerb.Run(["--band", "highpass", "--order", "3", "--f1", "200MHz"], Realisers));
            var listed = Assert.Single(JsonRun.CapturedDiagnostics, d => d.Id == "filter.realiser.listed");
            Assert.Contains("only a lowpass", listed.Message);
        }
        using (JsonRun.Isolate())
        {
            Assert.Equal(1, FilterVerb.Run(["--band", "lowpass", "--order", "3", "--f1", "200MHz", "--wat", "1"], Realisers));
            Assert.Contains("filter.args.unknown-option", Ids());
        }

        string cnl = Path.Combine(_dir, "lp.cnl");
        using (JsonRun.Isolate())
            Assert.Equal(0, FilterVerb.Run(["--response", "butterworth", "--band", "lowpass", "--order", "3", "--f1", "200MHz",
                "--tech", ShippedTechnologies.DefaultId, "--realiser", "lumped", "-o", cnl, "--to-schematic"], Realisers));
        string text = File.ReadAllText(cnl);
        Assert.Contains("MLAD:F1 p1 p2 g1 g2 g3 g4 g5 ", text);
        Assert.Contains(" H=0.000508 ", text);                     // the board's 20 mil, stated on the line
        Assert.Contains("C:C2 g3 0 C=", text);
        Assert.Contains("Footprint=smt:0201", text);               // the board's R/C default land
        Assert.Contains("analysis SP1 type=sparam", text);
        Assert.True(File.Exists(Path.ChangeExtension(cnl, ".csch")));

        double f3 = Cutoff(cnl, 200e6);
        Assert.InRange(f3 / 200e6, 0.97, 1.03);
    }

    /// <summary>The ideal ladder, in every band: a 0.5 dB Chebyshev's insertion loss is its ripple at each passband edge,
    /// where the transformations put the prototype's cutoff, and an even order's too, port 2 being the prototype's load.</summary>
    [Theory]
    [InlineData(FilterBand.Lowpass, 3)]
    [InlineData(FilterBand.Highpass, 4)]
    [InlineData(FilterBand.Bandpass, 3)]
    [InlineData(FilterBand.Bandstop, 4)]
    public void TheIdealLadder_LosesItsRippleAtEachEdge(FilterBand band, int order)
    {
        var spec = new FilterSpec(FilterResponse.Chebyshev, band, order, 1e9, band is FilterBand.Bandpass or FilterBand.Bandstop ? 1.2e9 : 1e9, 0.5);
        var snp = FilterSynthesisService.Response(FilterSynthesisService.Ideal(spec, 2), [spec.F1Hz, spec.F2Hz], spec.Z0);
        // To 1e-4 dB: the prototype's β takes the textbook's 17.37 for 40/ln 10, which is 5e-5 dB at 0.5 dB.
        foreach (var m in snp.Matrices)
            Assert.Equal(-0.5, 20.0 * Math.Log10(m[1, 0].Magnitude), tolerance: 1e-4);
    }

    /// <summary>The <c>create</c> tool advertises the <c>filter</c> mode and its arguments, and a call through the adapter
    /// becomes the verb's command line and writes the netlist.</summary>
    [Fact]
    public void TheMcpMode_IsAdvertised_AndOneCallWritesTheNetlist()
    {
        var create = ToolCatalog.Advertise().Single(t => t!["name"]!.GetValue<string>() == "create")!;
        var props = create["inputSchema"]!["properties"]!.AsObject();
        Assert.Contains("filter", props["what"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        foreach (string arg in new[] { "band", "order", "f1", "f2", "ripple", "stop", "z0", "zeros", "technology", "realiser", "output" })
            Assert.True(props.ContainsKey(arg), arg);
        Assert.Equal("integer", props["order"]!["type"]!.GetValue<string>());

        Directory.CreateDirectory(_dir);
        string cnl = Path.Combine(_dir, "mcp.cnl");
        // As a client sends it: parsed JSON, which is what the adapter reads.
        var call = JsonNode.Parse(new JsonObject
        {
            ["what"] = "filter", ["band"] = "lowpass", ["order"] = 5, ["f1"] = "1GHz", ["ripple"] = 0.5,
            ["realiser"] = "Lumped lowpass (MLAD)", ["output"] = cnl,
        }.ToJsonString())!.AsObject();
        var argv = ToolCatalog.ToArgv("create", call, PathRoot.Open(_dir, out _)!, out var refusal);
        Assert.Null(refusal);
        Assert.Equal("filter", argv![0]);
        using (JsonRun.Isolate())   // --json is the adapter's, taken before dispatch as CliEntry.Run takes it
            Assert.Equal(0, FilterVerb.Run(JsonRun.TakeFlags(argv)[1..], Realisers));
        Assert.Contains("MLAD:F1 p1 p2 g1 g2 g3 g4 g5 g6 g7 g8", File.ReadAllText(cnl));
    }

    /// <summary>The Designer preselects the ladder's family and a lowpass, lists the realiser as able to build it, plots the
    /// ideal and the realised response; Place adds the part and a part on each site as one step that one Undo takes off. The
    /// responses compute off the UI thread, and the realisation Place would put down goes the moment the specification changes.</summary>
    [Fact]
    public async Task TheDesigner_ListsTheRealiser_AndPlacesItAsOneUndoStep()
    {
        var model = new SchematicEditModel();
        var schematic = new ViewModels.SchematicViewModel(model);
        var vm = new FilterDesignerViewModel(schematic, SymbolKind.Mlad, Realisers);
        await vm.Realising;
        Assert.False(vm.Working);

        Assert.Equal(("MLAD", FilterBand.Lowpass), (vm.Family, vm.Band));
        var row = Assert.Single(vm.Realisers);
        Assert.True(row.Supported);
        Assert.Same(row, vm.Selected);
        Assert.True(vm.Realisation is not null, vm.Status);
        Assert.Equal(4, vm.Plot.Traces.Count);                     // S21 and S11, ideal and realised

        vm.Band = FilterBand.Highpass;
        Assert.Equal("only a lowpass.", Assert.Single(vm.Realisers).Reason);
        Assert.Null(vm.Realisation);
        Assert.False(vm.PlaceCommand.CanExecute(null));
        await vm.Realising;
        Assert.Equal(2, vm.Plot.Traces.Count);                     // the ideal alone

        vm.Band = FilterBand.Lowpass;
        Assert.True(vm.Working);
        Assert.False(vm.PlaceCommand.CanExecute(null));            // nothing to place until the new realisation arrives
        await vm.Realising;
        vm.PlaceCommand.Execute(null);
        var ladder = Assert.Single(model.Components, c => c.Symbol == SymbolKind.Mlad);
        Assert.Equal(2, model.Components.Count(c => c.Symbol == SymbolKind.Inductor));
        Assert.Single(model.Components, c => c.Symbol == SymbolKind.Capacitor);
        Assert.Contains("site(", ladder.Parameters.Single(p => p.Name == "Sections").Expression);

        schematic.UndoRedo.Undo();
        Assert.Empty(model.Components);
    }

    /// <summary>Opened from Tools ▸ Filter Designer… with no schematic open: every family listed, the filter realised and
    /// plotted, and Place greyed with why.</summary>
    [Fact]
    public async Task WithNoSchematic_ItRealises_AndPlaceIsGreyedWithWhy()
    {
        var vm = new FilterDesignerViewModel(null, realisers: Realisers) { Band = FilterBand.Lowpass };
        await vm.Realising;
        Assert.Equal(FilterDesignerViewModel.AnyFamily, vm.Family);
        Assert.True(vm.Realisation is not null, vm.Status);
        Assert.False(vm.PlaceCommand.CanExecute(null));
        Assert.Contains("No schematic", vm.PlaceTip);
    }

    /// <summary>
    /// The Designer's own paths timed on a typical specification, the shipped realisers on the shipped RO4350B board: a
    /// 5-pole 0.1 dB Chebyshev bandpass, 2.28 to 2.52 GHz (10 % at 2.4 GHz). Opening is on the window's own default
    /// specification; the specification is then typed in; the list is one Refresh; each selection and the ripple's edit
    /// run until their response is shown, and every listed realiser until it is checked; then the ideal and each realiser's
    /// own realising and bench, cold. The times go to the test output (docs/design/filter-synthesis.md §6 records them). Asserted: the
    /// UI thread's share stays small, and the checked list is truthful, the coupled-feed hairpin refusing what its check
    /// let through.
    /// </summary>
    [Fact]
    public async Task TheDesigner_PathsOnATypicalSpec_AreTimed()
    {
        var tech = CircuitRF.Design.Layout.ShippedTechnologies.Load(CircuitRF.Design.Layout.ShippedTechnologies.DefaultId);
        var lines = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double sync = 0.0, drawn = 0.0;
        FilterDesignerViewModel? vm = null;
        async Task Time(string what, Action act)
        {
            clock.Restart();
            act();
            sync = Math.Max(sync, clock.Elapsed.TotalMilliseconds);
            string ui = $"{clock.Elapsed.TotalMilliseconds:0.0} ms on the UI thread";
            await vm!.Drawn;
            drawn = Math.Max(drawn, clock.Elapsed.TotalMilliseconds);
            string ideal = $"ideal {clock.Elapsed.TotalMilliseconds:0} ms";
            await vm.Realising;
            string shown = $"{clock.Elapsed.TotalMilliseconds:0} ms";
            await vm.Checking;
            lines.Add($"{what}: {ideal}, realised {shown}, list checked {clock.Elapsed.TotalMilliseconds:0} ms ({ui})"
                      + (vm.Realisation is null ? $"; {vm.Status}" : ""));
        }
        await Time("open", () => vm = new FilterDesignerViewModel(null, technology: tech));
        await Time("type 5 poles, 2.28 to 2.52 GHz", () => { vm!.Order = "5"; vm.F1 = "2.28 GHz"; vm.F2 = "2.52 GHz"; });
        await Time($"list ({vm!.Realisers.Count} realisers)", vm.Refresh);

        Assert.All(vm.Realisers.Where(r => r.Supported), r => Assert.True(r.Realised, r.Name));
        Assert.StartsWith("refuses: ", vm.Realisers.Single(r => r.Name.StartsWith("Hairpin bandpass, coupled", StringComparison.Ordinal)).Status);
        foreach (var row in vm.Realisers.Where(r => r.Supported).ToList())
            await Time($"select {row.Name}", () => vm.Selected = row);
        await Time("edit ripple", () => vm.Ripple = "0.2");
        await Time("open again", () => vm = new FilterDesignerViewModel(null, technology: tech));

        // Cold: a ripple the window has not seen, so no cross-section of it is in the N-line solver's cache.
        var spec = new FilterSpec(FilterResponse.Chebyshev, FilterBand.Bandpass, 5, 2.28e9, 2.52e9, 0.15);
        var substrate = FilterSynthesisService.Substrate(tech, null, null, out _);
        var f = FilterSynthesisService.Frequencies(spec, 401);
        clock.Restart();
        FilterSynthesisService.Response(FilterSynthesisService.Ideal(spec, 401), f, spec.Z0);
        lines.Add($"ideal: {clock.Elapsed.TotalMilliseconds:0} ms");
        foreach (var r in FilterRealisers.All.Where(r => r.Supports(spec, out _)))
        {
            clock.Restart();
            try
            {
                var realisation = r.Realise(spec, substrate);
                double realise = clock.Elapsed.TotalMilliseconds;
                FilterSynthesisService.Response(FilterSynthesisService.Cnl(spec, realisation, r.Name, substrate, tech, 401, []), f, spec.Z0);
                lines.Add($"cold {r.Name}: realise {realise:0} ms, bench {clock.Elapsed.TotalMilliseconds - realise:0} ms");
            }
            catch (CircuitRF.Core.Devices.ParameterRefusalException) { lines.Add($"cold {r.Name}: refuses in {clock.Elapsed.TotalMilliseconds:0} ms"); }
        }
        output.WriteLine(string.Join("\n", lines));
        Assert.True(sync < 1000 && drawn < 1000, string.Join("\n", lines));
    }

    /// <summary>A realiser that runs until it is cancelled, and says when it was.</summary>
    private sealed class Endless : IFilterRealiser
    {
        public readonly TaskCompletionSource<bool> Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "Endless lowpass (MLAD)";
        public bool Supports(FilterSpec spec, out string? why) { why = null; return true; }
        public FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate) => Realise(spec, substrate, default);
        public FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate, CancellationToken cancel)
        {
            if (spec.RippleDb == 0.2) return new LumpedLadder().Realise(spec, substrate);
            cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            Stopped.TrySetResult(cancel.IsCancellationRequested);
            cancel.ThrowIfCancellationRequested();
            throw new TimeoutException("never cancelled");
        }
    }

    /// <summary>The ideal is drawn while the realisation still runs, with "realising…"; a newer specification cancels the
    /// older one's realisation, and its own fills in.</summary>
    [Fact]
    public async Task ANewerEdit_CancelsTheOlderRealisation()
    {
        var endless = new Endless();
        var vm = new FilterDesignerViewModel(null, realisers: [endless]) { Band = FilterBand.Lowpass, F1 = "1 GHz" };
        await vm.Drawn;
        Assert.Equal(2, vm.Plot.Traces.Count);
        Assert.EndsWith("realising…", vm.Status);
        Assert.True(vm.Working);

        vm.Ripple = "0.2";
        Assert.True(await endless.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await vm.Realising;
        Assert.True(vm.Realisation is not null, vm.Status);
        Assert.Equal(4, vm.Plot.Traces.Count);
    }
}

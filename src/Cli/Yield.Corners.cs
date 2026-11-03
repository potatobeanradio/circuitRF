using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf yield corners &lt;path&gt;</c> (brief-yield-6 R-ya6-6, <c>cli.md</c> §25.6). The run is
/// <see cref="CornerRun"/>; the cross product is <see cref="CornerGenerator"/>. What is here is the corner flags, the
/// table, and the one opt-in write (<c>--generate … --write</c>, a <c>.csch</c>, after a checkpoint).
/// </summary>
internal static partial class Yield
{
    /// <summary><c>--mc</c>'s run: a yield when the design has an enabled yield goal, a Monte Carlo of the spread otherwise.</summary>
    private static StatisticalMode McModeOf(TuningSetup setup)
        => setup.Goals.Any(g => g.Enabled && g.ForYield) ? StatisticalMode.Yield : StatisticalMode.MonteCarlo;

    // ── --generate ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Prints the corners a cross product makes, in the document's own spelling — <c>corner</c> lines for a
    /// <c>.cnl</c>, the tuning block's JSON for a <c>.csch</c> (a kit axis selection has no <c>.cnl</c> spelling, D10) —
    /// and writes nothing unless <c>--write</c> says so.
    /// </summary>
    private static int Generate(string input, string full, DocumentKind kind, string spec, bool write)
    {
        if (write && kind != DocumentKind.Schematic) return JsonRun.Fail(CliDiagnostics.YieldGenerateWriteNeedsSchematic(input));

        var axes = kind == DocumentKind.Schematic ? WorkspaceCorners.ForDocument(full) : [];
        var gen = CornerGenerator.Parse(spec, axes);
        if (gen.Refusal is { } refused) return JsonRun.Fail(refused);

        var corners = gen.Corners;
        if (kind == DocumentKind.Schematic) Console.WriteLine(SchematicPersistence.CornersJson(corners));
        else foreach (var c in corners) Console.WriteLine(TuningDirectiveText.CornerLine(c));
        Console.Error.WriteLine($"[circuitRF] {corners.Count} corner(s) generated");

        if (write)
        {
            var (model, view, cellName) = SchematicPersistence.LoadFromFile(full);
            var taken = corners.Where(c => model.Tuning?.Corners.Any(e => e.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) == true)
                               .Select(c => c.Name).ToList();
            if (taken.Count > 0) return JsonRun.Fail(CliDiagnostics.YieldGenerateNamesTaken(string.Join(", ", taken)));
            if (Optimize.CheckpointBefore(full, $"yield corners --generate --write ({corners.Count} corners)") is { } failed)
                return JsonRun.Fail(failed);
            var next = model.Tuning?.Clone() ?? new TuningSetup();
            next.Corners.AddRange(corners.Select(c => c.Clone()));
            model.Tuning = next;
            SchematicPersistence.SaveToFile(full, model, cellName, view.PanX, view.PanY, view.Zoom);
            Console.Error.WriteLine($"Added {corners.Count} corner(s) to {full}");
            JsonRun.AddOutput("schematic", full);
        }

        // Each corner in full, one structure for both kinds of document (R-ya16-6): a caller reading JSON sees what the
        // text shows — a .cnl's line besides, since that is what its author pastes in.
        JsonRun.Corners = new CornerReportJson(input, "finished", [], [], [], 0,
            Generated: [.. corners.Select(c => new CornerDefinitionJson(
                c.Name, CornerRun.TempOf(c),
                // A library part's corner (brief vendorlib-13 R-vl13-2f) reads as the line spells it: part:<number>.
                new SortedDictionary<string, string>(c.Values.Concat((c.PartCorners ?? []).Select(kv =>
                    new KeyValuePair<string, string>(CellCorner.PartPrefix + kv.Key, kv.Value))).ToDictionary(), StringComparer.Ordinal),
                c.AxisSelections is { Count: > 0 } axes ? new SortedDictionary<string, string>(axes.ToDictionary(), StringComparer.Ordinal) : null,
                kind == DocumentKind.Schematic ? null : TuningDirectiveText.CornerLine(c)))]);
        return 0;
    }

    // ── the run ──────────────────────────────────────────────────────────────────

    private static int RunCorners(string input, string full, PreparedCircuit circuit, TuningSetup? setup,
                                  List<(string Name, string Expr)> sets, IReadOnlyList<string>? names, bool mc,
                                  StatisticalMode mode, string? output, bool quiet)
    {
        var ct = RunHost.Cancellation;
        var observer = RunHost.Observer;
        string resultPath = output ?? (mc ? StatisticalRun.ResultPathFor(full) : CornerRun.ResultPathFor(full));

        // The run statistical corners came from, when it is beside the design: its recorded z-vectors replay as they
        // stand, so a renamed variable is named rather than silently redrawn (R-ya6-4).
        DataSet? recorded = null;
        string yieldFile = StatisticalRun.ResultPathFor(full);
        if (!mc && File.Exists(yieldFile))
            try { recorded = DataSetImporter.Import(yieldFile).DataSet; } catch (Exception) { /* drawn afresh, with its note */ }

        var run = CornerRun.Create(circuit, new CornerOptions
        {
            Setup = setup, Sets = sets, Corners = names, MonteCarlo = mc, Mode = mode, Recorded = recorded,
            Cancellation = ct, ResultPath = resultPath,
            Progress = new SynchronousCorner(p =>
            {
                string line = $"corners {p.Done}/{p.Total}" + (p.Corner is null ? "" : $" · {p.Corner} done");
                if (!quiet) Console.Error.WriteLine(line);
                observer?.Invoke(new RunProgress(line, p.Done, p.Total));
            }),
        });
        if (run.Refusal is { } refused) return JsonRun.Fail(refused);
        foreach (var note in run.Notes) Report(note);

        Console.Error.WriteLine($"[circuitRF] corners: {run.Corners.Count} corner(s) and the nominal" +
                                (mc ? $", a {(mode == StatisticalMode.Yield ? "yield" : "Monte Carlo")} at each" : $", {run.Goals.Count} goal(s)"));
        var result = run.Run();
        foreach (var note in result.Notes.Skip(run.Notes.Count)) Report(note);

        if (result.Outcome == StatisticalOutcome.Cancelled || ct.IsCancellationRequested)
        {
            if (result.WrittenPath is { } partial && File.Exists(partial)) File.Delete(partial);
            JsonRun.Report(CliDiagnostics.YieldCancelled());
            return 130;
        }
        if (result.Outcome == StatisticalOutcome.Refused)
            return JsonRun.Fail(result.Refusal ?? CliDiagnostics.RunFailed("no corner could be run"));
        if (result.Outcome == StatisticalOutcome.NoneEvaluated && result.Refusal is { } none) JsonRun.Report(none);

        if (result.WrittenPath is { } written)
        {
            Console.WriteLine($"Wrote {written}");
            JsonRun.AddOutput(JsonRun.KindOf(written), written);
        }

        var report = mc ? ProjectYields(input, result) : ProjectCorners(input, result);
        JsonRun.Corners = report with { Output = result.WrittenPath };
        if (mc) PrintYields(report); else PrintCorners(report);
        return result.ExitCode;
    }

    private sealed class SynchronousCorner(Action<CornerProgress> report) : IProgress<CornerProgress>
    {
        public void Report(CornerProgress value) => report(value);
    }

    private static string CornerOutcomeWord(StatisticalOutcome o) => o switch
    {
        StatisticalOutcome.Finished      => "finished",
        StatisticalOutcome.BelowTarget   => "failed",
        StatisticalOutcome.Refused       => "refused",
        StatisticalOutcome.NoneEvaluated => "noneEvaluated",
        _                                => "cancelled",
    };

    private static CornerReportJson ProjectCorners(string input, CornerResult r)
        => new(input, CornerOutcomeWord(r.Outcome), [.. r.Goals.Select(g => g.Name)],
            [.. r.Corners.Select(c => new CornerRowJson(
                c.Name, c.Definition?.IsStatistical == true, c.Evaluated,
                c.Evaluated && r.Goals.Count > 0 ? c.Pass : null,
                c.Evaluated ? null : c.Reason?.Render() ?? "did not evaluate",
                CornerRun.TempOf(c.Definition),
                new SortedDictionary<string, string>(c.Values.ToDictionary(), StringComparer.Ordinal),
                [.. c.Goals.Select(g => new CornerGoalJson(g.Name, g.Met, Finite(g.Margin), Finite(g.WorstValue)))]))],
            [.. r.Worst.Select(w => new CornerWorstJson(w.Goal, w.Corner, Finite(w.Margin), w.Met))],
            r.Evaluations);

    private static CornerReportJson ProjectYields(string input, CornerResult r)
        => new(input, CornerOutcomeWord(r.Outcome), [], [], [], r.Evaluations,
            Yields: [.. r.Yields.Select(y => new CornerYieldJson(y.Name,
                y.Result.Outcome is StatisticalOutcome.Finished or StatisticalOutcome.BelowTarget
                    ? Project(input, y.Run, y.Result, false)
                    : new YieldReportJson(input, ModeWord(y.Run.Mode), OutcomeWord(y.Result.Outcome),
                        y.Result.FinishReason, SettingsOf(y.Run), y.Result.Trials, y.Result.DidNotEvaluate, [],
                        null, null, null, [], [], [], KitOf(y.Run), y.Run.Evaluations)))],
            WorstYield: r.WorstYield);

    private static void PrintCorners(CornerReportJson r)
    {
        Console.WriteLine($"Corners: {r.Document}");
        Console.WriteLine(r.Outcome switch
        {
            "finished" => r.Goals.Count == 0 ? "  every corner evaluated (no goal scored)" : "  every goal is met at every corner",
            "failed"   => "  a goal FAILS at a corner",
            _          => "  no corner evaluated",
        });
        Console.WriteLine();
        string Cell(CornerRowJson c, string goal)
        {
            if (!c.Evaluated) return "—";
            var g = c.Goals.FirstOrDefault(x => x.Name == goal);
            return g is null ? "" : G(g.Margin) + (g.Met ? "" : " ✗");
        }
        Table(["corner", "temp", .. r.Goals.Select(g => g + " margin"), "status"],
              r.Corners.Select(c => (string[])[
                  c.Name, c.Temp is { } t ? G(t) : "", .. r.Goals.Select(g => Cell(c, g)),
                  !c.Evaluated ? "did not evaluate" : c.Pass == false ? "FAILS" : c.Pass == true ? "passes" : "evaluated"]));

        if (r.Worst.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Worst corner per goal:");
            foreach (var w in r.Worst)
                Console.WriteLine($"  {w.Goal}: {w.Corner ?? "—"} (margin {G(w.Margin)}{(w.Met ? "" : ", FAILS")})");
        }
        var dne = r.Corners.Where(c => !c.Evaluated).ToList();
        if (dne.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Did not evaluate:");
            foreach (var c in dne) Console.WriteLine($"  {c.Name}: {c.Reason}");
        }
    }

    private static void PrintYields(CornerReportJson r)
    {
        var y = r.Yields ?? [];
        Console.WriteLine($"{(y.FirstOrDefault()?.Run.Mode == "yield" ? "Yield" : "Monte Carlo")} at each corner: {r.Document}");
        Console.WriteLine();
        Table(["corner", "trials", "yield", "interval", "did not evaluate", "outcome"],
              y.Select(c => new[]
              {
                  c.Corner, c.Run.Trials.ToString(CultureInfo.InvariantCulture),
                  c.Run.Yield is { } e ? Pct(e.Yield) : "—",
                  c.Run.Yield is { } i ? $"{Pct(i.Lower)} – {Pct(i.Upper)}" : "",
                  c.Run.DidNotEvaluate.ToString(CultureInfo.InvariantCulture),
                  c.Run.Outcome == "belowTarget" ? "BELOW TARGET" : c.Run.Outcome,
              }));
        if (r.WorstYield is { } worst)
        {
            Console.WriteLine();
            Console.WriteLine($"Worst corner: {worst}");
        }
    }
}

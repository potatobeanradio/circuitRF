using System.Globalization;
using System.Text;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;
using CircuitRF.Render;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf impedance &lt;layout&gt;</c> — Trace Impedance Analysis: every trace on the chosen
/// copper layers, end to end, against a target Z0 ± tolerance, with where the return path under each
/// one breaks. <c>-o report.pdf</c> writes the report the layout editor's Impedance Analysis exports.
///
/// <para><b>It owns no analysis and no page</b>, on <c>src/Cli/Authoring.cs</c>' terms: every number
/// comes out of <see cref="TraceImpedanceAnalysis.AnalyzeFile"/> and every pixel of the PDF out of
/// <see cref="TraceImpedanceReportDocument.Pdf"/> — the two calls the editor's dialog makes — so a
/// board that passes headlessly passes when it is opened, and the two PDFs are the same document.</para>
///
/// <para><b>Exit codes</b>: 0 when every trace passes, 1 when one fails or the run is refused, 130
/// when it is cancelled. A cancelled run still writes the report for the layers that FINISHED
/// (owner, 2026-09-25) — the analysis is layer by layer precisely so that stopping a long run keeps
/// what it has done — and says on its first page that it was cancelled.</para>
/// </summary>
internal static class Impedance
{
    private sealed class Options
    {
        public string? Path;
        public string? Output;
        public double Target = TraceImpedanceOptions.DefaultTargetOhms;
        public double Tolerance = TraceImpedanceOptions.DefaultTolerancePercent;
        public readonly List<string> Layers = [];
        public double? MaxWidthMicrons;
    }

    public static int Run(string[] args)
    {
        var o = new Options();
        if (Parse(args, o) is { } bad) return bad;

        if (o.Path is null) { JsonRun.Report(CliDiagnostics.ImpedancePathRequired()); return Usage(); }
        JsonRun.InputPath = o.Path;
        if (!File.Exists(o.Path) && !Directory.Exists(o.Path))
            return JsonRun.Fail(CliDiagnostics.ImpedancePathNotFound(o.Path));

        if (o.Output is { } output && !output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return JsonRun.Fail(CliDiagnostics.ImpedanceOutputNotPdf(output));

        if (ResolveLayout(o.Path) is not { } clay)
            return JsonRun.Fail(CliDiagnostics.ImpedanceNotALayout(o.Path, DocumentKinds.Name(DocumentKinds.Classify(o.Path))));

        // The layer names, against the technology the layout resolves — a name that is not one of its
        // copper layers is a refusal listing the ones that are, never a silent skip.
        IReadOnlyList<LayerKey>? layers = null;
        if (o.Layers.Count > 0)
        {
            var (tech, copperNames) = CopperLayers(clay);
            if (tech is null) return JsonRun.Fail(CliDiagnostics.ImpedanceNoTechnology(clay));
            var keys = new List<LayerKey>();
            foreach (string name in o.Layers)
            {
                var def = tech.Layers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                if (def is null || !copperNames.Contains(def.Name))
                    return JsonRun.Fail(CliDiagnostics.ImpedanceUnknownLayer(name, string.Join(", ", copperNames)));
                keys.Add(def.Key);
            }
            layers = keys;
        }

        var options = new TraceImpedanceOptions
        {
            TargetOhms = o.Target,
            TolerancePercent = o.Tolerance,
            Layers = layers,
            MaxWidthMicrons = o.MaxWidthMicrons,
        };

        // Progress: one stderr line per stage (a layer's finding, cutting, solving), and whatever a
        // host installed beside it.
        string lastStage = "";
        var control = new RunControl
        {
            Token = RunHost.Cancellation,
            Progress = new Inline(p =>
            {
                RunHost.Observer?.Invoke(p);
                if (p.Stage != lastStage)
                {
                    lastStage = p.Stage;
                    Console.Error.WriteLine($"[circuitRF] {p.Stage}" + (p.StageTotal > 0 ? $" ({p.StageTotal} {p.StageUnit})" : ""));
                }
            }),
        };

        TraceImpedanceReport report;
        try
        {
            report = TraceImpedanceAnalysis.AnalyzeFile(clay, options, control);
        }
        catch (OperationCanceledException)
        {
            JsonRun.Report(CliDiagnostics.ImpedanceCancelled());
            return 130;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return JsonRun.Fail(CliDiagnostics.ImpedanceLayoutUnreadable(clay, ex.Message));
        }

        if (report.Refusal is { } why) return JsonRun.Fail(CliDiagnostics.ImpedanceRefused(clay, why));

        foreach (string note in report.Notes) Console.Error.WriteLine($"[circuitRF] {note}");
        Console.Write(Text(report));
        JsonRun.Impedance = Project(report);

        if (o.Output is { } path && report.Layers.Count > 0)
        {
            byte[] pdf = TraceImpedanceReportDocument.Pdf(report);
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, pdf);
            }
            catch (Exception ex)
            {
                return JsonRun.Fail(CliDiagnostics.ImpedanceOutputFailed(path, ex.Message));
            }
            JsonRun.AddOutput("report", path);
            Console.Error.WriteLine($"[circuitRF] Wrote {path}");
        }

        if (report.Cancelled)
        {
            JsonRun.Report(CliDiagnostics.ImpedanceCancelledPartial(report.Layers.Count, report.LayersRequested.Count));
            return 130;
        }
        return report.FailCount > 0 || report.AllTraces.Any(t => t.Verdict == TraceVerdict.Unsolved) ? 1 : 0;
    }

    private sealed class Inline(Action<RunProgress> a) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => a(value);
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf impedance <layout> [--target 50] [--tol 10] [--layers \"Top Copper,Inner 2\"]\n" +
            "                           [--max-width <um>] [-o report.pdf]\n" +
            "  <layout> is a .clay or a cell folder holding one.");
        return 1;
    }

    private static int? Parse(string[] args, Options o)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-o" or "--output" when i + 1 < args.Length: o.Output = args[++i]; continue;
                case "--target" when i + 1 < args.Length:
                    if (!TryOhms(args[++i], out o.Target) || !(o.Target > 0))
                        return JsonRun.Fail(CliDiagnostics.ImpedanceBadNumber("--target", args[i], "a positive number of ohms"));
                    continue;
                case "--tol" or "--tolerance" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out o.Tolerance)
                        || !(o.Tolerance > 0) || o.Tolerance >= 100)
                        return JsonRun.Fail(CliDiagnostics.ImpedanceBadNumber("--tol", args[i], "a percentage above 0 and below 100"));
                    continue;
                case "--layers" or "--layer" when i + 1 < args.Length:
                    o.Layers.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    continue;
                case "--max-width" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i].Replace("um", "", StringComparison.OrdinalIgnoreCase).Replace("µm", ""),
                                         NumberStyles.Float, CultureInfo.InvariantCulture, out double mw) || !(mw > 0))
                        return JsonRun.Fail(CliDiagnostics.ImpedanceBadNumber("--max-width", args[i], "a positive width in µm"));
                    o.MaxWidthMicrons = mw;
                    continue;
                default:
                    if (a.StartsWith('-')) { JsonRun.Report(CliDiagnostics.ImpedanceUnknownOption(a)); return Usage(); }
                    if (o.Path is not null) { JsonRun.Report(CliDiagnostics.ImpedanceMultiplePaths()); return Usage(); }
                    o.Path = a;
                    continue;
            }
        }
        return null;
    }

    private static bool TryOhms(string text, out double value)
    {
        string t = text.Trim();
        foreach (string suffix in new[] { "ohms", "ohm", "Ω", "R" })
            if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { t = t[..^suffix.Length].Trim(); break; }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The <c>.clay</c> a path means: itself, or a cell folder's primary layout view.</summary>
    private static string? ResolveLayout(string path)
    {
        var kind = DocumentKinds.Classify(path);
        if (kind == DocumentKind.Layout) return Path.GetFullPath(path);
        if (kind == DocumentKind.Cell &&
            CircuitRF.Design.Cells.CellFolder.ResolvePrimary(path, CircuitRF.Design.Cells.ViewType.Layout).ResolvedName is { Length: > 0 } name)
            return Path.Combine(CircuitRF.Design.Cells.CellFolder.SubFolderPath(path, CircuitRF.Design.Cells.ViewType.Layout), name);
        return null;
    }

    /// <summary>The technology a layout resolves, and the names of its drawing layers bound to a
    /// conductor of the stackup.</summary>
    private static (Technology? Tech, List<string> Names) CopperLayers(string clay)
    {
        var view = LayoutPersistence.LoadFromFile(clay);
        var (resolved, _) = TechnologyResolver.ResolveForDocument(view.TechRef, clay, null, new TechnologyCache());
        if (resolved.Tech is not { } tech) return (null, []);
        var bound = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).SelectMany(l => l.DrawingLayers).ToHashSet();
        return (tech, [.. tech.Layers.Where(l => bound.Contains(l.Key)).Select(l => l.Name)]);
    }

    // ── the text report (stdout) ─────────────────────────────────────────────────────────────

    private static string Text(TraceImpedanceReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Trace impedance: {r.Title} — target {r.TargetOhms:0.##} Ω ± {r.TolerancePercent:0.##} % " +
                      $"({r.LowOhms:0.0}–{r.HighOhms:0.0} Ω)");
        foreach (var layer in r.Layers)
        {
            sb.AppendLine();
            sb.AppendLine($"{layer.Name}: {layer.Traces.Count} trace(s)" +
                          (layer.PoursSkipped > 0 ? $", {layer.PoursSkipped} pour(s) not analysed" : ""));
            foreach (var t in layer.Traces)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {t.Id,-5} {Verdict(t.Verdict),-4}  Z0 {Z(t.Z0Min)}–{Z(t.Z0Max)} Ω (avg {Z(t.Z0Mean)}), {t.InTolerance:0%} in band, {t.TypeSummary}, {r.Len(t.Length)}, {r.Pt(t.StartX, t.StartY)} → {r.Pt(t.EndX, t.EndY)} [{t.StartsAt} / {t.EndsAt}]"));
                foreach (var issue in t.Issues) sb.AppendLine($"        ! {issue.Text}");
                foreach (var note in t.Notes) sb.AppendLine($"        · {note}");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"{r.TraceCount} trace(s) on {r.Layers.Count} layer(s): {r.PassCount} pass, {r.FailCount} fail." +
                      (r.Cancelled ? $" Cancelled after {r.Layers.Count} of {r.LayersRequested.Count} layers." : ""));
        return sb.ToString();

        static string Z(double? z) => z is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) : "—";
        static string Verdict(TraceVerdict v) => v switch { TraceVerdict.Pass => "PASS", TraceVerdict.Fail => "FAIL", _ => "—" };
    }

    // ── --json ───────────────────────────────────────────────────────────────────────────────

    private static ImpedanceReportJson Project(TraceImpedanceReport r)
    {
        double Um(double dbu) => dbu / r.DbuPerMicron;
        return new ImpedanceReportJson(
            r.Title, r.TechnologyName, r.TargetOhms, r.TolerancePercent, r.Cancelled,
            r.TraceCount, r.PassCount, r.FailCount,
            [.. r.Layers.Select(l => new ImpedanceLayerJson(
                l.Name, l.PoursSkipped,
                [.. l.Traces.Select(t => new ImpedanceTraceJson(
                    t.Id, t.Verdict.ToString().ToLowerInvariant(),
                    [Um(t.StartX), Um(t.StartY)], [Um(t.EndX), Um(t.EndY)], t.StartsAt, t.EndsAt,
                    Um(t.Length), Um(t.WidthMin), Um(t.WidthMax),
                    t.Z0Min, t.Z0Max, t.Z0Mean, t.InTolerance, t.Configuration,
                    [.. t.Configurations.Select(c => new ImpedanceTypeJson(c.Name, c.Share))], t.References,
                    [.. t.Issues.Select(i => new ImpedanceIssueJson(
                        IssueId(i.Kind), [Um(i.X0), Um(i.Y0)], [Um(i.X1), Um(i.Y1)], i.Text))],
                    t.Notes))]))]);
    }

    private static string IssueId(TraceIssueKind k) => k switch
    {
        TraceIssueKind.OutOfTolerance   => "out-of-tolerance",
        TraceIssueKind.ReturnBroken     => "return-broken",
        TraceIssueKind.PartialReference => "partial-reference",
        TraceIssueKind.ReferenceStep    => "reference-step",
        TraceIssueKind.NoReference      => "no-reference",
        _                               => "unsolved",
    };
}

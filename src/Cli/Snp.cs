using System.Globalization;
using System.Text.Json;
using CircuitRF.Diagnostics;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf snp &lt;file.sNp&gt;</c> — the S-Parameter Viewer, with no display.
///
/// <para><b>This file holds no reading, no rule and no drawing.</b> The file is read through
/// <see cref="TouchstoneSource"/>, which the viewer window reads it through; what the file says about
/// itself is <see cref="TouchstoneFacts"/>' and its defects are <see cref="TouchstoneFacts.Findings"/>' —
/// the sentences the window's Findings card shows; the plots are <see cref="SParameterViewerPreset"/>'s
/// and the page is <see cref="RenderDataDisplay.Emit"/>'s. What is here is argument parsing, refusals and
/// reporting, on <c>src/Cli/Authoring.cs</c>' terms.</para>
///
/// <para><b>With no <c>-o</c> it reports</b> the file pane. <c>-o</c> draws the four plots
/// (<c>.svg</c>, <c>.pdf</c>, <c>.png</c>) or writes them as a Data Display (<c>.cdd</c>) — what the
/// window's Save as Data Display writes. Exit 1 when the file has a defect, after anything asked for
/// has been written: a picture of a broken file is still the picture the window shows.</para>
/// </summary>
internal static class Snp
{
    private sealed class Options
    {
        public string? Path;
        public string? Output;
        public string? Params;

        // `plot`'s picture options, spelling for spelling, as `smith` takes them.
        public int?    Width, Height;
        public double  Scale = 1.0;
        public string? ScaleOption;
        public bool?   Transparent;
        public bool    Dark;
    }

    public static int Run(string[] args)
    {
        var o = new Options();
        if (Parse(args, o) is { } bad) return bad;

        if (o.Path is null) { JsonRun.Report(CliDiagnostics.SnpPathRequired()); return Usage(); }
        JsonRun.InputPath = o.Path;

        if (!File.Exists(o.Path))
            return JsonRun.Fail(CliDiagnostics.SnpPathNotFound(o.Path));

        if (o.Output is not null && OutputKindOf(o.Output) is null)
            return JsonRun.Fail(CliDiagnostics.SnpUnknownOutputFormat(
                o.Output, System.IO.Path.GetExtension(o.Output) is { Length: > 0 } e ? e : "(none)"));

        if (o.ScaleOption is not null && OutputKindOf(o.Output ?? "") != OutputKind.Png)
            return JsonRun.Fail(CliDiagnostics.PlotScaleOnVector(
                o.ScaleOption, o.Output is null ? "(no picture)" : FormatName(o.Output)));

        try { return Go(o); }
        catch (OperationCanceledException)
        {
            JsonRun.Report(CliDiagnostics.SnpCancelled());
            return 130;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf snp <file.sNp> [--params S11,S21,…|all|reflections|default]\n" +
            "                     [-o out.{svg,pdf,png,cdd}] [--size WxH] [--scale N | --dpi N]\n" +
            "                     [--background opaque|transparent] [--dark]");
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
                case "--params" when i + 1 < args.Length:         o.Params = args[++i]; continue;
                case "--dark":                                    o.Dark = true;        continue;

                case "--size" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split('x', 'X');
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                        || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                        || w < 8 || h < 8 || w > 20000 || h > 20000)
                        return JsonRun.Fail(CliDiagnostics.PlotSizeMalformed(args[i]));
                    o.Width = w; o.Height = h;
                    continue;
                }

                case "--scale" when i + 1 < args.Length:
                {
                    if (o.ScaleOption is not null) return JsonRun.Fail(CliDiagnostics.PlotScaleAndDpi());
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
                        || !(s > 0) || s > 16)
                        return JsonRun.Fail(CliDiagnostics.PlotScaleMalformed("--scale", args[i]));
                    o.Scale = s; o.ScaleOption = "--scale";
                    continue;
                }

                case "--dpi" when i + 1 < args.Length:
                {
                    if (o.ScaleOption is not null) return JsonRun.Fail(CliDiagnostics.PlotScaleAndDpi());
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                        || !(d > 0) || d > 1536)
                        return JsonRun.Fail(CliDiagnostics.PlotScaleMalformed("--dpi", args[i]));
                    o.Scale = d / 96.0; o.ScaleOption = "--dpi";
                    continue;
                }

                case "--background" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "opaque":      o.Transparent = false; break;
                        case "transparent": o.Transparent = true;  break;
                        default: return JsonRun.Fail(CliDiagnostics.PlotUnknownBackground(args[i]));
                    }
                    continue;

                default:
                    if (a.StartsWith('-'))
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("snp", a));
                    if (o.Path is not null)
                        return JsonRun.Fail(CliDiagnostics.RunMultipleInputs("snp", a));
                    o.Path = a;
                    continue;
            }
        }
        return null;
    }

    private enum OutputKind { Svg, Pdf, Png, DataDisplay }

    private static OutputKind? OutputKindOf(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".svg" => OutputKind.Svg,
            ".pdf" => OutputKind.Pdf,
            ".png" => OutputKind.Png,
            ".cdd" => OutputKind.DataDisplay,
            _      => null,
        };

    private static string FormatName(string path) => OutputKindOf(path) switch
    {
        OutputKind.Svg => "svg",
        OutputKind.Pdf => "pdf",
        OutputKind.Png => "png",
        _              => "cdd",
    };

    // ── the run ──────────────────────────────────────────────────────────────

    private static int Go(Options o)
    {
        RunHost.Control?.BeginStage("resolve");
        Console.Error.WriteLine("[circuitRF] resolve...");

        string path = o.Path!;
        var kind = DocumentKinds.Classify(path);
        if (kind != DocumentKind.Touchstone)
            return JsonRun.Fail(CliDiagnostics.SnpNotATouchstone(path, DocumentKinds.Name(kind)));

        if (TouchstoneSource.Read(path, out var refusal) is not { } source)
            return JsonRun.Fail(refusal!);

        int ports = source.Facts.Ports;
        IReadOnlyList<SParameterViewerPreset.Term> selection = SParameterViewerPreset.DefaultSelection(ports);
        if (o.Params is { } typed)
        {
            if (SParameterViewerPreset.ParseSelection(typed, ports, out var parseError) is not { } chosen)
                return JsonRun.Fail(parseError!);
            selection = chosen;
        }

        Report(source, selection);
        JsonRun.Snp = SnpJson(source, selection);

        bool defective = false;
        foreach (var finding in source.Findings)
        {
            JsonRun.Report(finding);
            defective |= finding.Severity == DiagnosticSeverity.Error;
        }

        int written = 0;
        if (o.Output is not null)
        {
            RunHost.Cancellation.ThrowIfCancellationRequested();
            RunHost.Control?.BeginStage("export");
            written = OutputKindOf(o.Output) == OutputKind.DataDisplay
                ? WriteDisplay(o, source, selection)
                : Draw(o, source, selection);
        }
        return written != 0 ? written : defective ? 1 : 0;
    }

    // ── the report ───────────────────────────────────────────────────────────

    /// <summary>The viewer's file pane, line for line — <see cref="SParameterViewerPreset.FileFacts"/>'s wording.</summary>
    private static void Report(TouchstoneSource source, IReadOnlyList<SParameterViewerPreset.Term> selection)
    {
        Console.WriteLine(System.IO.Path.GetFileName(source.Path));
        foreach (var (label, value) in SParameterViewerPreset.FileFacts(source.Facts))
            Console.WriteLine($"  {label,-12}  {value}");
        Console.WriteLine($"  {"Plotted",-12}  {string.Join(", ", selection.Select(t => SParameterViewerPreset.Name(t, source.Facts.Ports)))}");
    }

    private static SnpReportJson SnpJson(TouchstoneSource source, IReadOnlyList<SParameterViewerPreset.Term> selection)
    {
        var f = source.Facts;
        static double? Finite(double v) => double.IsFinite(v) ? v : null;
        return new SnpReportJson(
            source.Path, f.Ports, f.ExtensionPorts,
            f.Type.ToString(), f.Format.ToString(), f.FrequencyUnit.ToString(),
            f.FrequencyCount, f.FirstFrequencyHz, f.LastFrequencyHz,
            JsonNamingPolicy.CamelCase.ConvertName(f.Grid.ToString()),
            Finite(f.StepHz), Finite(f.PointsPerDecade), Finite(f.SmallestStepHz), Finite(f.LargestStepHz),
            f.FrequenciesOutOfOrder ? f.FirstNonIncreasingIndex + 1 : null,
            [f.Z0.Real, f.Z0.Imaginary],
            f.Z0PerPort?.Select(z => new[] { z.Real, z.Imaginary }).ToList(),
            f.NoisePoints,
            f.HasDcPoint,
            [.. selection.Select(t => SParameterViewerPreset.Name(t, f.Ports))]);
    }

    // ── -o out.cdd ───────────────────────────────────────────────────────────

    private static int WriteDisplay(Options o, TouchstoneSource source, IReadOnlyList<SParameterViewerPreset.Term> selection)
    {
        var config = SParameterViewerPreset.Build(source, selection);
        try
        {
            string full = System.IO.Path.GetFullPath(o.Output!);
            if (System.IO.Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            File.WriteAllText(full, JsonSerializer.Serialize(config, DataDisplayJson.Options), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return JsonRun.Fail(CliDiagnostics.SnpWriteFailed(o.Output!, ex.Message)); }

        JsonRun.AddOutput("data-display", o.Output!);
        Console.WriteLine();
        Console.WriteLine($"Wrote {o.Output} (a Data Display of {config.Tabs[0].Plots.Count} plots)");
        return 0;
    }

    // ── -o out.{svg,pdf,png} ─────────────────────────────────────────────────

    /// <summary>
    /// The four plots through the one composer. Each is loaded by <see cref="PlotConfigLoader"/> against
    /// the same <see cref="TouchstoneSource"/> the window loads them against — not the file read a second
    /// way — so a Y- or Z-parameter file is drawn here as S exactly as it is there.
    /// </summary>
    private static int Draw(Options o, TouchstoneSource source, IReadOnlyList<SParameterViewerPreset.Term> selection)
    {
        var placed = new List<PlacedPlot>();
        int traces = 0;
        foreach (var composed in SParameterViewerPreset.Compose(source, selection))
        {
            var pc   = composed.Config;
            var plot = PlotConfigLoader.LoadPlot(pc, source);
            plot.LabelColumnsOutside = true;   // the Data Display's own layout, as the window draws it
            traces += plot.Traces.Count;
            placed.Add(RenderDataDisplay.Place(plot, pc.Left, pc.Top, pc.Width, pc.Height, pc.FreqUnit,
                                               hasMultipleSources: false, aliasFor: null));
        }

        var req = new RenderDataDisplay.Request(
            o.Output!, FormatName(o.Output!), Data: [], Tab: null, PlotIndex: null, AllTabs: false,
            PageWidth: o.Width, PageHeight: o.Height, Scale: o.Scale,
            Transparent: o.Transparent, Dark: o.Dark, Verb: "snp");

        return RenderDataDisplay.Emit(source.Path, DocumentKinds.Name(DocumentKind.Touchstone), req, [placed],
                                      display: null, $"  {placed.Count} plot(s), {traces} trace(s)");
    }
}

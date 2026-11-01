// `circuitrf recognize <picture>…` on plot pictures — brief-img-14-sparameter-plots-to-touchstone.md R-im14-14 (D15: no
// new verb; docs/design/cli.md §26).
//
// THIS FILE OWNS NO PLOT READING (R-im14-15): the reading is PlotReading.Read, the file TouchstoneAssembly.Build and
// WriteFile, the CSV PlotCsv, the overlay ImageOverlay.BuildPlot — the functions Create Touchstone from Plot calls.
// What is here is the flags those take, the refusals a command line makes where the dialog would have waited, and the
// projection of the session into result.recognize.plots.
//
// SEVERAL PICTURES MAKE ONE SESSION: `recognize s11.png s21.png -o part.s2p`. Plots and traces are numbered across the
// session, so `--trace T3=…` and `--x-axis …@2` mean the same thing whichever picture holds them.
//
// THE CSV NEVER WAITS. A digitisation needs no assignment, no frequency and no frequency axis, so --csv is written
// whatever the Touchstone's fate; a call that asked only for the CSV is not refused for the Touchstone it did not ask
// for. Nor is one that asked only to --probe (brief-pdg-3): a value read off a curve needs no assignment either. Every
// other call whose Touchstone cannot be made exits 1 with the sentence and the flag that answers it — the dialog's
// highlighted control, spelled as a flag.
//
// BY HAND (brief-pdg-4-digitise-by-hand.md §4): --manual-box with --x-range and --y-range reads the picture as the
// dialog's Manual steps do — the same ManualPlot and ManualCurve records into the same PlotReading.Read, no frame found
// and no word read. --follow is ManualCurves.Follow, the dialog's Find curve, run before the reading.
//
// NOTHING IS WRITTEN BUT WHAT WAS ASKED, and the Touchstone last: --overlay and --csv (written even when the file is
// refused, because that is when they are most useful), then -o and the kept source pictures beside it.

using System.Text;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Design.Schematic.Recognition;
using RfCore.Export;

namespace CircuitRF.Cli;

internal static partial class Recognize
{
    /// <summary>A Touchstone's extension: <c>.s1p</c> … <c>.s4p</c>.</summary>
    private static bool IsTouchstone(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(Path.GetExtension(path), @"^\.[sS][1-4][pP]$");

    /// <summary>The flags a plot picture does not take: every layout, schematic and circuit flag.</summary>
    private static IEnumerable<string> NotPlotFlags(Options o)
    {
        foreach (string f in o.Picture.LayoutFlags.Concat(o.Picture.SchematicFlags).Concat(LayoutOnly(o))) yield return f;
        if (o.Into is not null) yield return "--into";
        if (o.Replace) yield return "--replace";
        if (o.Parts is not null) yield return "--parts";
        if (o.PartsOut is not null) yield return "--parts-out";
        if (o.Tech is not null) yield return "--tech";
        if (o.Start is not null) yield return "--start";
        if (o.Stop is not null) yield return "--stop";
        if (o.Npts is not null) yield return "--npts";
        if (o.Digits is not null) yield return "--digits";
        if (o.Picture.Underlay is not null) yield return "--underlay";
    }

    /// <summary>R-im14-14 — one or more plot pictures, read into one session.</summary>
    private static int RecognisePlots(Options o, IReadOnlyList<(string Path, ImageSource Source, ImageKindResult Kind)> pictures)
    {
        if (NotPlotFlags(o).FirstOrDefault() is { } flag)
            return JsonRun.Fail(CliDiagnostics.PictureFlagWrongKind(Verb, flag, "a layout, a schematic picture or a layout picture", "a plot picture"));
        if (o.Output is { } output && !IsTouchstone(output))
            return JsonRun.Fail(CliDiagnostics.RecognizeOutputNotTouchstone(output));

        var inputs = pictures.Select(p => new PlotPictureInput(p.Source, Path.GetFileName(p.Path))).ToList();
        var flags = o.Plot;
        PlotReadingOptions? manual = null;
        if (flags.Manual)
        {
            var (by, refused) = ManualOptions(flags, pictures);
            if (refused is { } refusal) return refusal;
            manual = by;
        }

        // A typed axis's pixels are the FILE's; a reading of a reduced picture is in the reduced picture's pixels. Which
        // picture a plot is on is known only after a reading, so a pre-pass finds it when the pictures differ.
        Func<int, double> factor = _ => pictures[0].Source.Raster.ReductionFactor;
        if (manual is null && flags.Clicks.Count > 0 && pictures.Select(p => p.Source.Raster.ReductionFactor).Distinct().Count() > 1)
        {
            var first = PlotReading.Read(inputs, flags.ReadingOptions(_ => 1) with { Axes = [] }, RunHost.Control);
            factor = n => n <= first.Plots.Count ? pictures[first.Plots[n - 1].Picture].Source.Raster.ReductionFactor : 1;
        }
        foreach (var p in pictures) Console.Error.WriteLine($"[circuitRF] reading {p.Path} as a plot picture");
        var session = PlotReading.Read(inputs, manual ?? flags.ReadingOptions(factor), RunHost.Control);

        // The overlays and the CSV: written whatever the Touchstone's fate, each fitted trace's fit with them (R-pdg2-3).
        var fits = TraceFit.Session(session, flags.Fits);
        for (int i = 0; i < pictures.Count; i++)
            o.Picture.WriteOverlay(Verb, ImageOverlay.BuildPlot(session, i, fits), pictures[i].Source, i + 1, pictures.Count);
        string? csv = null;
        if (flags.Csv is { } csvTarget)
        {
            csv = Path.GetFullPath(csvTarget);
            if (Write(csv, () => PlotCsv.WriteFile(csv, session, fits)) is { } failed) return failed;
            JsonRun.AddOutput("csv", csv);
        }

        var built = TouchstoneAssembly.Build(session, flags.AssemblyOptions());
        string? touchstone = null;
        var kept = new List<string>();
        if (built.Refusal is null && o.Output is { } target)
        {
            touchstone = Path.GetFullPath(target);
            if (!string.Equals(Path.GetExtension(touchstone), built.Extension, StringComparison.OrdinalIgnoreCase))
            {
                JsonRun.Recognize = PlotsDocument(pictures, session, built, null, csv, kept, [], flags.AllTraces);
                return JsonRun.Fail(CliDiagnostics.RecognizeOutputPorts(touchstone, built.Ports, built.Extension));
            }
            try
            {
                foreach (var k in TouchstoneAssembly.WriteFile(built, touchstone, inputs, flags.KeepSources))
                {
                    kept.Add(k.FullPath);
                    JsonRun.AddOutput("source", k.FullPath);
                }
                Console.Error.WriteLine($"[circuitRF] Wrote {touchstone}");
                JsonRun.AddOutput("touchstone", touchstone);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return JsonRun.Fail(CliDiagnostics.RecognizeOutputFailed(touchstone, ex.Message));
            }
        }

        var text = new StringBuilder();
        foreach (string line in session.Lines()) text.Append(line).Append('\n');
        foreach (string line in built.Report) text.Append(line).Append('\n');
        if (built.Matrix.Count > 0)
        {
            text.Append('\n');
            foreach (var c in built.Matrix)
                text.Append($"S{c.I}{c.J}: {c.Source}{(c.Rule.Length > 0 ? $" ({c.Rule})" : "")}{(c.Phase.Length > 0 ? $"; phase {c.Phase}" : "")}\n");
        }
        // --probe (R-pdg3-3): every rectangular trace whose range covers each x, read there — the dialog's typed x.
        var probes = new List<PlotProbeJson>();
        foreach (var (asked, x) in flags.Probes)
        {
            var read = TraceProbe.Across(session, x, fits);
            if (read.Count == 0) text.Append("probe ").Append(asked).Append(": no rectangular trace covers it\n");
            foreach (var p in read)
            {
                text.Append("probe ").Append(p.Readout).Append('\n');
                probes.Add(new PlotProbeJson(asked, x, p.Hit.Name, p.Hit.Plot, p.Hit.Y, p.Text.YUnit, p.Text.XUnit.Length > 0 ? p.Text.XUnit : null,
                                             p.Fit, p.Readout));
            }
        }
        if (csv is not null) text.Append("csv: ").Append(csv).Append('\n');
        if (touchstone is not null) text.Append("touchstone: ").Append(touchstone).Append('\n');
        Console.Write(text.ToString());
        JsonRun.Recognize = PlotsDocument(pictures, session, built, touchstone, csv, kept, probes, flags.AllTraces);

        // A call that asked only for the CSV, or only to probe, is not refused for a Touchstone it did not ask for.
        if (built.Refusal is { } no && !((csv is not null || flags.Probes.Count > 0) && o.Output is null))
            return JsonRun.Fail(CliDiagnostics.RecognizePlotRefused(no.Sentence, no.Flag));
        return 0;
    }

    /// <summary>The by-hand reading's options (brief-pdg-4 §4): the box, the scales, the curves — each --follow followed
    /// first, its fixpoints ahead of any the --curve of the same name gave — or the refusal.</summary>
    private static (PlotReadingOptions? Options, int? Refusal) ManualOptions(PlotFlags flags,
                                                                               IReadOnlyList<(string Path, ImageSource Source, ImageKindResult Kind)> pictures)
    {
        if (pictures.Count > 1) return (null, JsonRun.Fail(CliDiagnostics.RecognizeSeveralPictures("a plot read by hand is one picture")));
        foreach (var (flag, instead) in new[] { ("--x-axis", "--x-range"), ("--y-axis", "--y-range"), ("--y2-axis", "--y2-range"),
                                                ("--x-log", "--x-range …:log"), ("--y-log", "--y-range …:log"), ("--y2-log", "--y2-range …:log"),
                                                ("--trace", "--curve …,is=S21:db or --follow …,is=S21:db"), ("--freq", "--x-range"), ("--smith-z0", "a rectangular box") })
            if (flags.Given.Contains(flag)) return (null, JsonRun.Fail(CliDiagnostics.RecognizeManualConflict(flag, instead)));
        var missing = new List<string>();
        if (flags.ManualBox is null) missing.Add("--manual-box");
        if (flags.XRange is null) missing.Add("--x-range");
        if (flags.YRange is null) missing.Add("--y-range");
        if (missing.Count > 0) return (null, JsonRun.Fail(CliDiagnostics.RecognizeManualNeeds(string.Join(", ", missing))));

        var raster = pictures[0].Source.Raster;
        double f = raster.ReductionFactor;
        var plot = flags.ManualPlot(f)!;
        if (plot.Refusal is { } why) return (null, JsonRun.Fail(CliDiagnostics.RecognizeManualRefused(why)));

        var curves = new List<ManualCurve>();
        var names = flags.Curves.Select(c => c.Name).Concat(flags.Follows.Select(c => c.Name)).Distinct(StringComparer.Ordinal).ToList();
        for (int k = 0; k < names.Count; k++)
        {
            string name = names[k];
            var given = flags.Curves.LastOrDefault(c => c.Name == name);
            var points = new List<(double Px, double Py)>();
            int rgb = PlotFlags.CurveColour(k);
            PlotSide? followAxis = null;
            PlotAssignment? followIs = null;
            foreach (var (_, sx, sy, axis, assignment) in flags.Follows.Where(c => c.Name == name))
            {
                followAxis = axis ?? followAxis;
                followIs = assignment ?? followIs;
                var followed = ManualCurves.Follow(raster, plot, (sx * f, sy * f));
                // R-emb5-10: the colour followed, and the grey or black ink passed over when some was in reach — said
                // whether or not the follow succeeded, so the caller can click again knowingly.
                JsonRun.Report(CliDiagnostics.RecognizeFollowed(name, followed.Rgb, followed.Refusal, followed.Other));
                if (followed.Refusal is not null) continue;
                points.AddRange(followed.Fixpoints);
                rgb = followed.Rgb;
            }
            if (given.Points is not null) points.AddRange(given.Points.Select(p => (p.X * f, p.Y * f)));
            // A --curve of the same name decides the scale and the quantity; else a --follow's own suffix does (R-emb5-11).
            var side = given.Points is not null ? given.Axis : followAxis ?? PlotSide.Left;
            var what = given.Points is not null && (given.Assignment.Assigned || followIs is null) ? given.Assignment
                     : followIs ?? PlotAssignment.None;
            curves.Add(new ManualCurve(name, 1, points, side, what, rgb));
        }
        return (new PlotReadingOptions { Manual = [plot], ManualCurves = curves }, null);
    }

    private static RecognizeReportJson PlotsDocument(IReadOnlyList<(string Path, ImageSource Source, ImageKindResult Kind)> pictures,
                                                     PlotSession session, TouchstoneAssemblyResult built, string? touchstone, string? csv,
                                                     IReadOnlyList<string> kept, IReadOnlyList<PlotProbeJson> probes,
                                                     bool allTraces)
    {
        // R-emb5-9: a reading that made no Touchstone and named no trace — a picture it could not calibrate, a noisy
        // instrument screenshot of hundreds of fragments — answers with the count and the longest few, not a record per
        // fragment. Each trace's own findings go with it; --all-traces lists every one.
        var listed = session.Traces;
        int? tracesRead = null;
        if (!allTraces && session.Traces.Count > CollapsedTraces
            && (built.Refusal is not null || session.Traces.All(t => !t.Assignment.Assigned)))
        {
            listed = [.. session.Traces.OrderByDescending(t => t.Samples.Count).ThenBy(t => t.Number).Take(CollapsedTraces)
                                       .OrderBy(t => t.Number)];
            tracesRead = session.Traces.Count;
        }
        var omitted = session.Traces.Except(listed).Select(t => t.Name + " (").ToList();
        bool Omitted(PlotFinding f) => f.Class is "trace" or "axis" or "frequency"
                                    && omitted.Any(prefix => f.Sentence.StartsWith(prefix, StringComparison.Ordinal));
        var findings = session.Findings.Where(f => !Omitted(f)).Select(f => new RecognizeFindingJson(f.Class, 1, f.Sentence, []))
                              .Concat(built.Report.Select(l => new RecognizeFindingJson("touchstone", 1, l, [])))
                              .ToList();
        if (tracesRead is { } n)
            findings.Insert(0, new RecognizeFindingJson("trace", n - listed.Count,
                $"{n} traces were read and none could be written; the {listed.Count} longest are listed — allTraces "
              + "(--all-traces) lists every one", []));
        static PixelBoxJson Box(PixelBox b) => new(b.Left, b.Top, b.Right, b.Bottom);
        static PlotAxisJson? Axis(AxisCalibration? a, IReadOnlyList<AxisLabel> labels) => new(
            a is null ? null : a.Scale.ToString().ToLowerInvariant(), a?.Used ?? 0, a?.Rejected ?? 0, a is null ? null : Math.Round(a.ResidualPx, 3),
            a?.Manual ?? false, [.. labels.Select(l => new PlotTickJson(l.Text, Math.Round(l.Pixel, 2), l.Value, l.Used))]);
        var plots = session.Plots.Select(p => new PlotFrameJson(
            p.Number, p.Picture, PlotKind.Name(p.Type), p.Excluded, p.Calibrated, Box(p.Frame.Box),
            p.Frame.IsChart ? [Math.Round(p.Frame.Cx, 2), Math.Round(p.Frame.Cy, 2), Math.Round(p.Frame.Radius, 2)] : null,
            p.Rect is { } r ? Axis(r.X, r.XLabels) : null, p.Rect is { } ry ? Axis(ry.Y, ry.YLabels) : null,
            p.Rect?.XTitle, p.Rect?.YTitle, p.Rect?.Title,
            p.Rect is { XIsFrequency: true } rx ? (rx.XUnitStated ? "" : "assumed ") + Unit(rx.XUnit) : null,
            p.Smith?.Z0, p.Smith?.Z0Stated, p.Smith?.Admittance,
            p.Rect is { TwoY: true } r2 ? Axis(r2.Y2, r2.Y2Labels) : null, p.Rect?.Y2Title,
            p.Rect?.YSide == PlotSide.Right ? "right" : null)).ToList();
        var traces = listed.Select(t =>
        {
            var xs = t.Samples.Select(s => s.X).Where(x => !double.IsNaN(x)).ToList();
            return new PlotTraceJson(t.Name, t.Plot, t.Curve.Hex, t.Curve.StyleName, t.Assignment.ToString(), t.Assignment.Assigned, t.Evidence,
                                     t.Samples.Count, t.Resolution, t.ResolutionUnit, xs.Count > 0 ? xs.Min() : null, xs.Count > 0 ? xs.Max() : null,
                                     t.Frequency?.Source, t.Frequency?.Direction, t.Curve.Gaps.Count, t.Curve.Bridged, t.Curve.Ambiguous.Count,
                                     t.Parametric ? null : PlotReading.Side(t.Axis), t.AxisEvidence.Length > 0 ? t.AxisEvidence : null,
                                     built.Fits.TryGetValue(t.Name, out var fit) ? FitJson(fit) : null);
        }).ToList();
        var markers = session.Markers.Select(m => new PlotMarkerJson(m.Name, m.Plot, m.Trace > 0 ? $"T{m.Trace}" : null, m.FrequencyHz,
                                                                      m.Placed ? Math.Round(m.X, 2) : null, m.Placed ? Math.Round(m.Y, 2) : null,
                                                                      m.Readout)).ToList();
        var matrix = built.Matrix.Select(c => new PlotMatrixCellJson($"S{c.I}{c.J}", c.Source, c.Rule, c.Phase, c.Plotted, c.Estimated)).ToList();
        var json = new RecognizePlotsJson(
            built.Refusal is { } no ? new PlotRefusalJson(no.Kind, no.Sentence, no.Flag) : null,
            [.. session.Pictures.Select(p => new PlotPictureJson(p.Index, p.Name, p.Sha256, p.Width, p.Height, p.Plots))],
            plots, tracesRead, traces, markers, built.Ports, matrix, built.Grid?.ToString(), touchstone, csv, kept,
            built.Health is { } h ? TouchstoneAssembly.HealthLine(h) : null,
            probes.Count > 0 ? probes : null);
        return new RecognizeReportJson(Path.GetFullPath(pictures[0].Path), null, "whole", 0, findings, [], null, null, null, null, false)
        {
            Image = PictureFlags.ImageJson(pictures[0].Source, pictures[0].Kind),
            Plots = json,
        };
    }

    /// <summary>How many traces a refused or unassigned reading lists by default (R-emb5-9).</summary>
    internal const int CollapsedTraces = 5;

    private static PlotFitJson FitJson(TraceFitResult f)
    {
        static double? Finite(double v) => double.IsFinite(v) ? v : null;
        return new PlotFitJson(f.Spec.Model.ToString().ToLowerInvariant(), f.Spec.Model == TraceFitModel.Polynomial ? f.Spec.Degree : null,
                               f.Expression, f.Expression is null ? null : f.LogX ? $"log10({f.XVariable})" : f.XVariable,
                               f.XUnit.Length > 0 ? f.XUnit : null, f.Coefficients, Finite(f.RmsResidual), Finite(f.RmsResidualPx),
                               f.Spec.Model == TraceFitModel.Spline && f.Fitted ? f.Knots : null,
                               f.Spec.Model == TraceFitModel.Spline && f.Fitted ? f.Smoothing : null, f.Pieces, f.Refusal);
    }

    private static string Unit(double multiplier) => multiplier switch
    {
        >= 1e12 => "THz", >= 1e9 => "GHz", >= 1e6 => "MHz", >= 1e3 => "kHz", _ => "Hz",
    };
}

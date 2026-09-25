// The canvas's "Trace Impedance" command (round-7 field report): right-click copper, get its Z0.
// The work is TraceImpedanceProbe in src/Design; this file picks the layer the click means, hands
// the probe the same flattened artwork a DRC run checks, and posts the answer to Messages.
//
// And the toolbar's Impedance Analysis (round 8): every trace on the chosen layers, as a PDF. The
// work is TraceImpedanceAnalysis and the page TraceImpedanceReportDocument — the two calls
// `circuitrf impedance` makes — so this file only gathers the artwork and says what it did.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Engine;
using CircuitRF.Render;

namespace CircuitRF.Ui.Layout;

public partial class LayoutEditorViewModel
{
    /// <summary>
    /// The copper layer a probe at (<paramref name="x"/>, <paramref name="y"/>) means, or null where
    /// there is no stackup-bound copper there. The CURRENT drawing layer when it has copper at the
    /// point — that is how a user says which of two stacked traces they mean — otherwise the highest
    /// visible copper in the stackup, never the draw order, which an imported technology can invert.
    /// </summary>
    public LayerKey? TraceImpedanceLayerAt(long x, long y)
    {
        if (Technology is not { } tech) return null;

        var under = Regions.CopperLayersAt(Model.Shapes, tech, x, y);
        if (under.Count == 0) return null;
        if (under.Contains(CurrentLayerKey) && ResolveLayerDef(CurrentLayerKey).Visible) return CurrentLayerKey;

        return under.OrderBy(k => ResolveLayerDef(k).Visible ? 0 : 1).ThenBy(k => StackRank(tech, k))
                    .Select(k => (LayerKey?)k).FirstOrDefault();
    }

    /// <summary>Probes the trace at the point and posts the result to Messages. Null when there is
    /// no technology or no copper there — the menu item is not offered then.</summary>
    public TraceImpedanceResult? ProbeTraceImpedance(long x, long y)
    {
        if (Technology is not { } tech || TraceImpedanceLayerAt(x, y) is not { } layer) return null;

        // The artwork a DRC run sees, so a trace drawn inside a placed cell is probed too.
        var flat = LayoutDesignFlatten.Flatten(
            Model, CurrentCellDir ?? "", tech, ResolveTechAt, resolvedCrossTechMappings: null);
        IReadOnlyList<LayoutShape> shapes = flat.ExceedsCeiling ? Model.Shapes : flat.Shapes;

        // The layer the click means first, then every other copper layer at the point, visible ones
        // first and in stackup order: the round-8 board's current layer was the inner PLANE under
        // the trace, and the probe answered for the plane.
        var candidates = new List<LayerKey> { layer };
        foreach (var k in Regions.CopperLayersAt(Model.Shapes, tech, x, y)
                                 .OrderBy(k => ResolveLayerDef(k).Visible ? 0 : 1).ThenBy(k => StackRank(tech, k)))
            if (!candidates.Contains(k)) candidates.Add(k);

        var result = TraceImpedanceProbe.ProbeFirst(shapes, tech, Model.DbuPerMicron, x, y, candidates, DisplayUnit);

        if (_messageSink is { } messages)
        {
            // ONE line (owner, 2026-09-24): each warning rides on it as a short tag.
            if (result.Ok && result.Flags.Count == 0) messages.Info(result.Summary());
            else messages.Warning(result.Summary());
        }
        return result;
    }

    private static int StackRank(Technology tech, LayerKey k)
    {
        for (int i = 0; i < tech.Stackup.Layers.Count; i++)
            if (tech.Stackup.Layers[i].DrawingLayers.Contains(k)) return i;
        return int.MaxValue;
    }

    /// <summary>One copper layer the Impedance Analysis dialog offers.</summary>
    public sealed record TraceImpedanceLayerChoice(LayerKey Key, string Name, Rgba Color, bool HasCopper);

    /// <summary>
    /// The copper layers of this layout's technology — every drawing layer bound to a conductor of the
    /// stackup, one per conductor, top of the stack first — or empty with no technology.
    /// </summary>
    public IReadOnlyList<TraceImpedanceLayerChoice> TraceImpedanceLayers()
    {
        if (Technology is not { } tech) return [];
        var withCopper = Model.Shapes.Select(s => s.Layer).ToHashSet();
        var list = new List<TraceImpedanceLayerChoice>();
        foreach (var layer in tech.Stackup.Layers)
        {
            if (layer.Kind != StackupKind.Conductor) continue;
            var key = layer.DrawingLayers.FirstOrDefault(k => tech.Layers.Any(l => l.Key == k));
            if (layer.DrawingLayers.Count == 0 || tech.Layers.FirstOrDefault(l => l.Key == key) is not { } def) continue;
            list.Add(new TraceImpedanceLayerChoice(key, def.Name, def.Color,
                layer.DrawingLayers.Any(withCopper.Contains) || Model.Instances.Count > 0));
        }
        return list;
    }

    /// <summary>
    /// Runs the analysis on a worker thread and writes the PDF. The artwork is flattened HERE, on the
    /// caller's (UI) thread, because the model is the editor's and not the worker's. A cancelled run
    /// still writes the layers it finished (owner, 2026-09-25); one cancelled before the first layer
    /// finished writes nothing. Returns the report, or null when nothing was written.
    /// </summary>
    public async Task<TraceImpedanceReport?> ExportTraceImpedanceAsync(
        string pdfPath, TraceImpedanceOptions options, RunControl control)
    {
        if (Technology is not { } tech) { ReportError("Impedance Analysis: this layout has no technology, so no stackup."); return null; }

        var flat = LayoutDesignFlatten.Flatten(
            Model, CurrentCellDir ?? "", tech, ResolveTechAt, resolvedCrossTechMappings: null);
        IReadOnlyList<LayoutShape> shapes = flat.ExceedsCeiling ? Model.Shapes : flat.Shapes;
        int dbu = Model.DbuPerMicron;
        var opts = options with { DisplayUnit = options.DisplayUnit ?? DisplayUnit };
        string title = CurrentLayoutPath is { Length: > 0 } fp ? TraceImpedanceAnalysis.CellTitle(fp) : "Untitled layout";
        string? source = CurrentLayoutPath;

        TraceImpedanceReport report;
        try
        {
            report = await Task.Run(() => TraceImpedanceAnalysis.Analyze(shapes, tech, dbu, opts, control));
        }
        catch (OperationCanceledException)
        {
            _messageSink?.Info("Impedance Analysis: cancelled before the first layer finished. Nothing was written.");
            return null;
        }
        if (report.Refusal is { } why) { ReportError($"Impedance Analysis: {why}"); return null; }
        report = report with { Title = title, SourcePath = source };

        if (report.Layers.Count == 0)
        {
            _messageSink?.Info("Impedance Analysis: cancelled before the first layer finished. Nothing was written.");
            return null;
        }

        try
        {
            byte[] pdf = await Task.Run(() => TraceImpedanceReportDocument.Pdf(report));
            await File.WriteAllBytesAsync(pdfPath, pdf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportError($"Impedance Analysis: '{pdfPath}' was not written: {ex.Message}");
            return null;
        }

        string verdict = $"{report.TraceCount} trace{(report.TraceCount == 1 ? "" : "s")} on {report.Layers.Count} " +
                         $"layer{(report.Layers.Count == 1 ? "" : "s")} against {report.TargetOhms:0.##} Ω ± " +
                         $"{report.TolerancePercent:0.##} %: {report.PassCount} pass, {report.FailCount} fail" +
                         (report.Cancelled ? $" (cancelled after {report.Layers.Count} of {report.LayersRequested.Count} layers)" : "");
        if (report.FailCount > 0 || report.Cancelled) _messageSink?.Warning($"Impedance Analysis: {verdict}.", pdfPath);
        else ReportMessage($"Impedance Analysis: {verdict}.", pdfPath);
        return report;
    }
}

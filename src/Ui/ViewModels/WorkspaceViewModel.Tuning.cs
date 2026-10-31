using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tuning;
using Dock.Model.Core;

namespace CircuitRF.Ui.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
//  The Tuning panel (brief-tuneopt-4). This file decides which schematic the panel tunes, gives it a
//  session that evaluates exactly what Simulate would (the same extraction, corners and resolver, read
//  back in memory instead of through netlist.cnl), and hands it the sessions a Push writes into. The
//  panel itself — rows, Push, Revert, lag — is TuningPanelViewModel, which knows nothing of the shell.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WorkspaceViewModel
{
    private TuningPanelViewModel? _wiredTuningPanel;
    private TuningSurfaceRelay?   _tuningSurface;

    /// <summary>What every schematic session's Inspector and canvas ask about tuning — forwarded to the
    /// current panel, so a layout rebuild that replaces the panel does not strand older sessions.</summary>
    internal ITuningSurface TuningSurface => _tuningSurface ??= new TuningSurfaceRelay(() => _factory.TuningTool?.Panel);

    /// <summary>
    /// Points the Tuning panel at <paramref name="document"/>'s TOP frame for a schematic. Any other
    /// document — a Data Display above all, which is where tuned results are watched — leaves the panel
    /// on the last schematic that had focus, and a panel that has none yet takes the retained one the
    /// Analyses panel runs. Only closing the tuned schematic empties it (<see cref="ClearTuningPanel"/>),
    /// so the panel is blank only when no schematic has been open.
    /// </summary>
    private void RouteTuningPanel(IDockable? document)
    {
        WireTuningPanel();
        if (_factory.TuningTool?.Panel is not { } panel) return;

        if (document is SchematicDocument sd)
            panel.SetActiveSchematic(sd.NavFrames[0].Session, InstancesRootHeaderOf(sd));
        else if (!panel.HasSchematic && _lastActiveSchematicDoc is { } kept)
            panel.SetActiveSchematic(kept.NavFrames[0].Session, InstancesRootHeaderOf(kept));
    }

    /// <summary>The tuned schematic has closed: the panel has nothing left to tune.</summary>
    private void ClearTuningPanel()
    {
        WireTuningPanel();
        _factory.TuningTool?.Panel.SetActiveSchematic(null, null);
    }

    private void WireTuningPanel()
    {
        var panel = _factory.TuningTool?.Panel;
        if (ReferenceEquals(panel, _wiredTuningPanel) || panel is null) return;
        _wiredTuningPanel = panel;

        panel.Defer = a => Avalonia.Threading.Dispatcher.UIThread.Post(a, Avalonia.Threading.DispatcherPriority.Background);
        // ⌘Z after a Push (or any edit) made here undoes it with the panel still focused — the
        // Analyses panel's pin on the session the edit landed on.
        panel.EditCommitted     += OnAnalysesEditCommitted;
        panel.CreateSession      = CreateTuneSession;
        panel.ReportProblem      = text => Messages.Error($"Tuning: {text}");   // issue #33
        panel.Ladders            = () => CircuitRF.Ui.Smith.SmithPreferredValueStore.Ladders;
        panel.SessionForDrawing  = d => SessionForTunedDrawing(d, openTab: true);
        panel.ExistingSessionFor = d => SessionForTunedDrawing(d, openTab: false);
        panel.PointsOf           = TunePointsOf;
        panel.RevealOnCanvas     = RevealTunable;
        panel.ReportRecall       = (summary, lines) =>
        {
            Messages.Info(summary);
            foreach (var line in lines) Messages.Info("  " + line);
        };
    }

    // ── "Last tuned" (brief-tuneopt-5 R-to5-5, overview D6) ────────────────

    /// <summary>
    /// The tuned schematic is about to be saved or closed: its unpushed values go into the "Last tuned"
    /// preset first, so the save writes them and a close asks about them. Every save and close path of
    /// a schematic calls this; it does nothing for any other document or when nothing differs.
    /// </summary>
    private void StoreLastTuned(SchematicDocument? doc = null)
    {
        if (_factory.TuningTool?.Panel is not { Tuned: { } tuned } panel) return;
        if (doc is not null && !doc.NavFrames.Any(f => ReferenceEquals(f.Session, tuned))) return;
        panel.StoreLastTuned();
    }

    /// <summary><see cref="StoreLastTuned(SchematicDocument?)"/> for a save that names the session.</summary>
    private void StoreLastTuned(SchematicViewModel vm)
    {
        if (_factory.TuningTool?.Panel is { } panel && ReferenceEquals(panel.Tuned, vm)) panel.StoreLastTuned();
    }

    // ── The session: Simulate's own extraction, in memory ───────────────────

    /// <summary>The bench as Simulate would netlist it — same resolver, same corners — prepared once.</summary>
    private (PreparedCircuit Circuit, SchematicDocument Doc, string BaseDir)? PrepareTunedCircuit(SchematicViewModel tuned)
    {
        if (!FindSchematicDocumentWithFrame(tuned, out var doc, out _)) return null;

        var model     = tuned.EditModel;
        var problems  = new List<string>();
        var corners   = WorkspaceCorners.Bind(AvailableCornerAxes, model.CornerSelections, problems);
        var extracted = NetExtractor.Extract(model, doc.Id, cells: this, cornerVariables: corners.Variables,
            cornerBinder: (selections, found) => WorkspaceCorners.BindingsFor(AvailableCornerAxes, selections, found),
            cornerSections: corners.Sections);
        foreach (var p in problems.Concat(extracted.Conflicts)) Messages.Warning($"Tuning: {p}");

        var baseDir = RunBaseDirectory();
        var text    = CnlWriter.Write(extracted.TestBench, extracted.Library, "tuning session");
        var wsRoot  = CurrentWorkspacePath is not null ? Path.GetDirectoryName(CurrentWorkspacePath) : null;
        // With the extraction's vendor parts, which the text cannot carry: their candidates are what a part's
        // slider steps through and what the optimizer chooses among (brief vendorlib-4).
        return (PreparedCircuit.FromText(text, baseDir, wsRoot, doc.Id, extracted), doc, baseDir);
    }

    private TuneSession? CreateTuneSession(SchematicViewModel tuned, IReadOnlyList<string>? analyses)
    {
        if (PrepareTunedCircuit(tuned) is not { } p) return null;
        if (p.Circuit.ReadError is { } error)
        {
            Messages.Error($"Tuning: {error}");
            return null;
        }

        var key  = RunResultsWriter.SchematicKey(p.Doc.FilePath, p.Doc.Id);
        var sink = TuneSinkFor(p.BaseDir, key, tuned.EditModel.ResultsFileName);
        // A statistical corner chosen in Evaluate at replays its run's recorded draws when the run is beside the
        // schematic (brief-yield-7 R-ya7-4), as the yield verb's corners do.
        string? yieldFile = p.Doc.FilePath is { } path ? CircuitRF.Design.Statistics.StatisticalRun.ResultPathFor(path) : null;
        return TuneSession.ForCircuit(p.Circuit, sink,
            a => Avalonia.Threading.Dispatcher.UIThread.Post(a), analyses,
            () =>
            {
                if (yieldFile is null || !File.Exists(yieldFile)) return null;
                try { return RfCore.Export.DataSetImporter.Import(yieldFile).DataSet; }
                catch (Exception) { return null; }
            });
    }

    /// <summary>What one slider move costs per analysis (the ⚙ list): leaf points and sweep points.</summary>
    private IReadOnlyDictionary<string, (long Points, long SweepPoints)> TunePointsOf(
        SchematicViewModel tuned, IReadOnlyList<string> names)
    {
        var result = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        if (PrepareTunedCircuit(tuned) is not { Circuit.ReadError: null } p) return result;
        foreach (var name in names)
        {
            var plan = CircuitEvaluation.Plan(p.Circuit, new CircuitEvaluationRequest { Analyses = [name] });
            if (plan.Status == RunStatus.Success) result[name] = (plan.TotalWorkUnits, plan.SweepPoints);
        }
        return result;
    }

    // ── The documents a Push writes into (R-to4-8) ──────────────────────────

    /// <summary>
    /// The session editing <paramref name="drawing"/>. With <paramref name="openTab"/>, a session with no
    /// tab of its own is given one — WITHOUT focus — so its new unsaved state is visible and reaches the
    /// ordinary unsaved-changes prompt; a dirty document never hides in a background session.
    /// </summary>
    private SchematicViewModel? SessionForTunedDrawing(SchematicEditModel drawing, bool openTab)
    {
        foreach (var path in _registry.AllPaths.ToList())
        {
            if (!_registry.TryGet(path, out var vm) || vm is null || !ReferenceEquals(vm.EditModel, drawing)) continue;
            if (openTab && !_openDocsByPath.ContainsKey(path))
            {
                var doc = new SchematicDocument(Path.GetFileName(path), vm, path) { Messages = Messages, Hierarchy = this };
                HookSchematicCanvasFocus(doc);
                _factory.OpenDocumentInBackground(doc);
                _openDocsByPath[path] = doc;
            }
            return vm;
        }
        return null;
    }

    /// <summary>⋮ ▸ Reveal on canvas: brings the row's drawing forward and frames its component.</summary>
    private void RevealTunable(SchematicViewModel tuned, Tunable t)
    {
        var catalog = _factory.TuningTool?.Panel.Catalog;
        var drawing = t.Cell is null ? tuned.EditModel : catalog?.Drawings.GetValueOrDefault(t.Cell);
        if (drawing is null) return;

        var component = drawing.Components.FirstOrDefault(c => t.Kind == TunableKind.Variable
            ? c.Symbol == SymbolKind.Var && c.Parameters.Any(p => p.Name.Trim() == t.Owner)
            : c.InstanceName == t.Owner);
        var vm = ReferenceEquals(drawing, tuned.EditModel) ? tuned : SessionForTunedDrawing(drawing, openTab: true);
        if (component is null || vm is null) return;

        if (FindSchematicDocumentShowing(vm) is { } shown)
            RevealAfterActivating(shown, () => InstanceReveal.Reveal(shown, component));
        else if (FindSchematicDocumentWithFrame(vm, out var root, out int k))
            RevealAfterActivating(root, () =>
            {
                PopToLevel(root, k);
                InstanceReveal.Reveal(root, component);
            });
    }
}

// ================================================================
//  DataDisplayViewModel.EditHistory.cs  —  every edit is an undo step
//
//  The display's explicit commands cover what a gesture does to the
//  CANVAS: adding, removing, moving and resizing plots, panning an axis,
//  a marker added or deleted. Everything edited through a plot's
//  inspector, a flyout or a context menu — a trace card, a title, a
//  limit, the plot type — had no undo at all, so an edit there could
//  not be taken back and a document once touched could never be made
//  clean again by undoing.
//
//  The recorder closes that without a command per control. It keeps the
//  saved form of every plot (the same configuration the dirty check and
//  the .cdd compare), and whenever the display reports a content change
//  that no command made, the plots whose saved form moved become ONE undo
//  step holding their before and after configurations. Undo puts the
//  before form back in place, onto the same container and Plot objects,
//  so it restores exactly what is saved — which is what lets undoing
//  every change bring the document back to clean.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CircuitRF.Render.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class DataDisplayViewModel
{
    /// <summary>Each plot's saved form at the last point the history agrees with.</summary>
    private Dictionary<PlotContainerViewModel, string> _editCheckpoint = new();

    /// <summary>True while a pointer is held on the canvas — see <see cref="BeginCanvasGesture"/>.</summary>
    private bool _inCanvasGesture;

    /// <summary>Above zero while a change that is not an edit is being applied (a re-run's data arriving).</summary>
    private int _editRecordingSuspended;

    /// <summary>True while an undo entry from this recorder is putting plots back.</summary>
    private bool _restoringEdit;

    /// <summary>
    /// The saved form of one plot, as the dirty check serializes it (<c>BuildTabConfig</c> with no
    /// directory), so that "the same as saved" means the same thing to both.
    /// </summary>
    internal string SavedFormOf(PlotContainerViewModel c)
        => JsonSerializer.Serialize(BuildPlotContainerConfig(c, "", Library), JsonOpts);

    private Dictionary<PlotContainerViewModel, string> SavedForms()
        => _plots.ToDictionary(c => c, SavedFormOf);

    /// <summary>
    /// Takes the display as it is now as the point the history agrees with, recording nothing.
    /// After a command ran (it is its own undo step) and after a change that is not an edit.
    /// </summary>
    internal void RebaseEditHistory() => _editCheckpoint = SavedForms();

    /// <summary>
    /// Records whatever changed in the plots since the last checkpoint as one undo step. A plot that
    /// was added or removed is not an edit here: those have commands of their own.
    /// </summary>
    internal void CheckpointEdits()
    {
        if (_restoringEdit || UndoRedo.IsApplying || _inCanvasGesture || _editRecordingSuspended > 0) return;

        var now = SavedForms();
        var changed = now.Where(kv => _editCheckpoint.TryGetValue(kv.Key, out var was) && was != kv.Value)
                         .Select(kv => kv.Key).ToList();
        if (changed.Count == 0) { _editCheckpoint = now; return; }

        var before = changed.ToDictionary(c => c, c => _editCheckpoint[c]);
        var after  = changed.ToDictionary(c => c, c => now[c]);
        string what = PlotEditCommand.Signature(before, after);

        _editCheckpoint = now;
        if (UndoRedo.Top is PlotEditCommand top && top.CanExtend(this, what))
        {
            top.ExtendTo(after);
            return;
        }
        UndoRedo.Push(new PlotEditCommand(this, before, after, what));
    }

    /// <summary>
    /// A pointer went down on the canvas: whatever was edited before it is recorded now, and nothing
    /// is recorded while it is held — a drag reports a change on every move, and the gesture's own
    /// command (a move, a resize, a pan) is pushed when it ends.
    /// </summary>
    internal void BeginCanvasGesture()
    {
        // A press whose release never arrived (capture taken elsewhere) must not leave recording off.
        _inCanvasGesture = false;
        CheckpointEdits();
        _inCanvasGesture = true;
    }

    /// <summary>The pointer came up. A gesture with a command of its own pushes it in its release
    /// handler, which rebases; one without (a marker dragged along its trace) is recorded here.</summary>
    internal void EndCanvasGesture()
    {
        _inCanvasGesture = false;
        CheckpointEdits();
    }

    /// <summary>Runs a change that is not an edit — new data for every trace — and takes its result as the
    /// checkpoint rather than as an undo step.</summary>
    internal void WithoutEditRecording(Action act)
    {
        _editRecordingSuspended++;
        try { act(); }
        finally
        {
            _editRecordingSuspended--;
            if (_editRecordingSuspended == 0) RebaseEditHistory();
        }
    }

    /// <summary>
    /// Every trace of <paramref name="c"/> that reads a specific file is pointed at the display's selected data
    /// source instead, so it follows the toolbar's selection as an added trace does — the Plot inspector's
    /// "Use display source", for a plot pasted from another display, which keeps the data it was copied with.
    /// One undo step. A trace with no source of its own (owner data, a summary column) and a "plot versus"
    /// trace's X source are left as they are: the X names a second file on purpose.
    /// </summary>
    internal void FollowDisplaySource(PlotContainerViewModel c)
    {
        if (Library?.SelectedDataSourceAbs is null) return;
        CheckpointEdits();   // anything edited before this is its own step

        var pc = JsonSerializer.Deserialize<PlotContainerConfig>(SavedFormOf(c), JsonOpts);
        if (pc is null) return;
        bool changed = false;
        foreach (var tc in pc.Traces)
        {
            if (string.IsNullOrEmpty(tc.SourcePath) || tc.SourcePath == DataSourceRef.Selected) continue;
            tc.SourcePath = DataSourceRef.Selected;
            changed = true;
        }
        if (!changed) return;

        RestorePlot(c, JsonSerializer.Serialize(pc, JsonOpts));
        // Owner-data and summary traces keep their own data: RestorePlot reloaded only the traces whose saved form
        // moved, and theirs did not.
        RaiseContentChanged();   // records it as one undo step and refreshes the window's dirty state
    }

    /// <summary>
    /// Puts one plot back as <paramref name="savedForm"/> states it, on the same container and Plot.
    /// A trace whose saved form already matches is kept as it is, so markers and the commands that
    /// hold them stay attached to the live trace.
    /// </summary>
    internal void RestorePlot(PlotContainerViewModel c, string savedForm)
    {
        var pc = JsonSerializer.Deserialize<PlotContainerConfig>(savedForm, JsonOpts);
        if (pc is null) return;

        _restoringEdit = true;
        try
        {
            var plot    = c.PlotVM.Plot;
            var sources = new LibraryDataSources(Library);

            // What each existing trace saves as, matched by position: the same position with the same
            // saved form is the same trace.
            var current = plot.Traces.Select(t => JsonSerializer.Serialize(BuildTraceConfig(t, "", Library), JsonOpts)).ToList();
            var keep    = plot.Traces.ToList();

            plot.RestoreTypeAndUnit(pc.PlotType, pc.FreqUnit);
            PlotConfigLoader.ApplySettings(plot, pc);

            var traces = new List<Trace>();
            for (int i = 0; i < pc.Traces.Count; i++)
            {
                string want = JsonSerializer.Serialize(pc.Traces[i], JsonOpts);
                if (i < keep.Count && current[i] == want) traces.Add(keep[i]);
                else if (PlotConfigLoader.LoadTrace(pc.Traces[i], pc.PlotType, pc.FreqUnit, sources) is { } t) traces.Add(t);
            }

            using (plot.BeginTraceBatch())
            {
                plot.Traces.Clear();
                foreach (var t in traces) plot.Traces.Add(t);
            }
            foreach (var t in traces)
                if (!t.IsCubeBound) t.BuildPath(plot.PlotType, plot.FreqUnits);

            c.RestoreGeometry(pc.Left, pc.Top, pc.Width, pc.Height);
            c.Inspector.ReloadFromPlot();
            PlotConfigLoader.RestoreAxes(plot, pc.Axes);
            c.RestoreGeometry(pc.Left, pc.Top, pc.Width, pc.Height);

            RebuildMarkerInfoBoxesForContainer(c);
            RebuildLegendBoxForContainer(c);
            c.UpdateLabelStrips();
            c.NotifyViewProperties();
            c.RequestPlotRedraw();
        }
        finally { _restoringEdit = false; }
    }
}

/// <summary>
/// One edit of one or more plots made outside the display's own commands — a trace card, a label, a
/// limit, the plot type — as the plots' saved forms before and after it.
/// </summary>
internal sealed class PlotEditCommand : IUndoableCommand
{
    /// <summary>How long after the last change an edit of the same thing still joins it: one step for a
    /// title typed a letter at a time, or a slider dragged.</summary>
    private static readonly TimeSpan ExtendWindow = TimeSpan.FromSeconds(1.5);

    private readonly DataDisplayViewModel _display;
    private readonly Dictionary<PlotContainerViewModel, string> _before;
    private Dictionary<PlotContainerViewModel, string> _after;
    private readonly string _what;
    private DateTime _lastChange = DateTime.UtcNow;
    private bool _closed;

    public PlotEditCommand(DataDisplayViewModel display,
                           Dictionary<PlotContainerViewModel, string> before,
                           Dictionary<PlotContainerViewModel, string> after,
                           string what)
    {
        _display = display; _before = before; _after = after; _what = what;
    }

    public void Execute() => Apply(_after);
    public void Undo()    { _closed = true; Apply(_before); }

    private void Apply(Dictionary<PlotContainerViewModel, string> forms)
    {
        foreach (var (c, form) in forms)
            if (_display.Plots.Contains(c)) _display.RestorePlot(c, form);
    }

    /// <summary>True when a further change is the same edit continuing: the same plots, the same fields,
    /// soon after, and this entry has not been undone in between.</summary>
    internal bool CanExtend(DataDisplayViewModel display, string what)
        => !_closed && ReferenceEquals(display, _display) && what == _what
           && DateTime.UtcNow - _lastChange < ExtendWindow;

    internal void ExtendTo(Dictionary<PlotContainerViewModel, string> after)
    {
        foreach (var (c, form) in after) _after[c] = form;
        _lastChange = DateTime.UtcNow;
    }

    /// <summary>
    /// Which fields of which plots an edit touched — the top-level settings that differ, and the
    /// positions of the traces that differ — so a run of changes to one field joins into one step and
    /// a change to another field starts its own.
    /// </summary>
    internal static string Signature(Dictionary<PlotContainerViewModel, string> before,
                                     Dictionary<PlotContainerViewModel, string> after)
    {
        var parts = new List<string>();
        foreach (var (c, was) in before)
        {
            using var a = JsonDocument.Parse(was);
            using var b = JsonDocument.Parse(after[c]);
            var keys = new List<string>();
            foreach (var p in b.RootElement.EnumerateObject())
            {
                if (!a.RootElement.TryGetProperty(p.Name, out var old))
                { keys.Add(p.Name); continue; }
                if (p.Name == "Traces" && p.Value.ValueKind == JsonValueKind.Array && old.ValueKind == JsonValueKind.Array)
                {
                    int na = old.GetArrayLength(), nb = p.Value.GetArrayLength();
                    if (na != nb) { keys.Add($"Traces#{nb}"); continue; }
                    for (int i = 0; i < nb; i++)
                        if (old[i].GetRawText() != p.Value[i].GetRawText()) keys.Add($"Traces[{i}]");
                    continue;
                }
                if (old.GetRawText() != p.Value.GetRawText()) keys.Add(p.Name);
            }
            parts.Add($"{c.GetHashCode()}:{string.Join(",", keys)}");
        }
        return string.Join(";", parts);
    }
}

// brief-em3d-83 R-em3d83-4 — a field plot in the Properties Inspector: every field of the record, each commit one undo entry.
// The Solution picker lists what the plot's run ACTUALLY saved — the owner's frequency picker — by value, and ends with
// "Other frequency… (needs a re-run)", which asks the setup to save one more (R-em3d83-6). The Quantity picker lists only
// what the step's files hold (FieldQuantity.Offered). When the data is missing the reason is shown above the fields. The
// phase, Play and the loop period are view state: they sit at the bottom, for the plot being drawn, and are never saved.
// A picker's rows are replaced only when they differ: a pick commits, the commit reloads this panel from inside the ComboBox's
// own selection change, and refilling THAT ComboBox's rows there left its selection right but its box blank.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One row of a plot's Solution picker: a saved step (by value), the "Other frequency…" row, or — before the setup has
/// run — the disabled row saying what will appear (brief-dfr31-3 R-dfr31-3-4).</summary>
public sealed record C3dPlotSolutionChoice(string Label, C3dFieldSolution? Solution, bool IsOther = false, bool IsEnabled = true)
{
    public override string ToString() => Label;
}

/// <summary>One row of a plot's Quantity picker: the array and how it is read. Disabled before the setup has run: what its run
/// will offer, shown and not yet chosen.</summary>
public sealed record C3dPlotQuantityChoice(string Label, string Quantity, string? Mode, bool IsEnabled = true)
{
    public override string ToString() => Label;
}

public sealed partial class C3dPropertiesViewModel
{
    /// <summary>The Setup picker's first row: the setup active when the plot is drawn.</summary>
    public const string ActiveSetupChoice = "Active";

    public const string OtherFrequencyLabel = "Other frequency… (needs a re-run)";

    /// <summary>brief-dfr31-3 R-dfr31-3-3 — the offset's tooltip: the plane is a reading of the saved volume, not a setting of
    /// the solve.</summary>
    public const string PlotOffsetTip =
        "Where the plane is along its axis, in the display unit (or with a unit of its own). The field is saved for the whole " +
        "volume; move the plane at any time — before or after Simulate, without running again.";

    public const string PlotOffsetSliderTip =
        "Sweep the plane across the geometry: drawn as you drag, kept when you let go. The field is saved for the whole volume; " +
        "move the plane at any time.";

    public static IReadOnlyList<C3dAxis> PlotAxes { get; } = [C3dAxis.X, C3dAxis.Y, C3dAxis.Z];
    public static IReadOnlyList<double> PlotPercentiles { get; } = [95, 99, 99.9, 100];
    public static IReadOnlyList<string> PlotSolvers { get; } = [.. CircuitRF.Design.Em3d.Em3dSolvers.With(c => c.Fields).Select(d => d.DisplayName)];

    /// <summary>brief-em3d-100 — the Referred to picker's rows, in <see cref="C3dDriveReferredTo"/> order.</summary>
    public static IReadOnlyList<string> PlotDriveReferences { get; } = ["Incident (available)", "Accepted"];

    public const string DrivePowerTip =
        "The power this plot's field is shown at, in W, mW or dBm, saved with the plot. Incident power is what a source matched " +
        "to the port's Z₀ makes available to it. The solve is linear, so this only rescales what is drawn: field strengths by the " +
        "square root of the power ratio, power and energy densities by the ratio. It never needs a re-run and never marks a " +
        "result stale. Magnitudes are peak; RMS is 1/√2 of them.";

    public const string DriveReferredTip =
        "Incident: the power a source matched to the port's Z₀ makes available. Accepted: the power that enters the port after " +
        "reflection, |a|²(1 − |S_kk|²), with every other port terminated in its own Z₀ — it needs the run's port record at this " +
        "frequency.";

    /// <summary>A field plot's row is selected: its fields are shown.</summary>
    [ObservableProperty] private bool _isFieldPlot;
    private string _plotName = "";

    public ObservableCollection<string> PlotSetups { get; } = [];
    [ObservableProperty] private string? _plotSetup;
    [ObservableProperty] private bool _plotHasSolver;
    [ObservableProperty] private string? _plotSolver;
    public ObservableCollection<C3dPlotSolutionChoice> PlotSolutions { get; } = [];
    [ObservableProperty] private C3dPlotSolutionChoice? _plotSolution;
    [ObservableProperty] private bool _plotOtherOpen;
    [ObservableProperty] private string _plotOtherText = "";
    public ObservableCollection<C3dPlotQuantityChoice> PlotQuantities { get; } = [];
    [ObservableProperty] private C3dPlotQuantityChoice? _plotQuantity;
    public ObservableCollection<string> PlotTargets { get; } = [];
    [ObservableProperty] private string? _plotTarget;
    [ObservableProperty] private bool _plotIsClip;
    [ObservableProperty] private C3dAxis _plotAxis = C3dAxis.Z;
    [ObservableProperty] private string _plotOffsetText = "";
    /// <summary>The offset slider, DBU: across the model's extent on the plot's axis. It previews while dragged and commits on
    /// release, one undo entry per drag.</summary>
    [ObservableProperty] private double _plotOffsetSlider;
    [ObservableProperty] private double _plotOffsetMin;
    [ObservableProperty] private double _plotOffsetMax = 1;
    [ObservableProperty] private double _plotOffsetStep = 1;
    [ObservableProperty] private bool _plotHasOffsetRange;
    /// <summary>brief-dfr31-3 R-dfr31-3-1 — the plane stays drawn in the view while the plot is not selected.</summary>
    [ObservableProperty] private bool _plotShowPlane;
    /// <summary>A drag's offset not yet written; written on release.</summary>
    private long? _plotOffsetDragged;
    [ObservableProperty] private bool _plotIsFaces;
    [ObservableProperty] private string _plotFacesText = "";
    [ObservableProperty] private bool _plotIsTemperature;
    [ObservableProperty] private bool _plotDb;
    [ObservableProperty] private double _plotPercentile = 99;
    [ObservableProperty] private bool _plotFixRange;
    /// <summary>brief-sar-6 R-sar6-5 — a SAR clip plane drawn as its maximum-intensity projection; offered for SAR only.</summary>
    [ObservableProperty] private bool _plotMip;
    [ObservableProperty] private bool _plotCanMip;
    /// <summary>brief-em3d-100 — the plot shows a driven solution: its drive rows are shown.</summary>
    [ObservableProperty] private bool _plotIsDriven;
    [ObservableProperty] private string _plotDriveText = "";
    [ObservableProperty] private string? _plotDriveReferredTo;
    [ObservableProperty] private bool _plotHidden;
    /// <summary>R-em3d83-5 — why the plot draws nothing, above its fields; null when it draws.</summary>
    [ObservableProperty] private string? _plotProblem;
    /// <summary>R-em3d49-5b — the fields are from a run of a model since changed (still drawn, on the geometry that run
    /// solved); null when they are current. The toolbar strip that said so is retired (owner request, 2026-09-28).</summary>
    [ObservableProperty] private string? _plotStale;
    /// <summary>This plot is the one drawn: the phase controls apply to it.</summary>
    [ObservableProperty] private bool _plotIsDrawn;

    /// <summary>The view the phase, Play and loop period are the state of.</summary>
    public Viewer3DViewModel Viewer => Editor.Viewer;

    private const string OnClip = "Clip plane", OnFaces = "Faces";
    private string OnSurfaces => PlotIsTemperature ? "All faces" : "Surfaces";

    private void ClearFieldPlot()
    {
        IsFieldPlot = false;
        PlotOtherOpen = false;
        PlotProblem = null;
        PlotStale = null;
    }

    private void LoadFieldPlot(string name)
    {
        var editor = Editor;
        if (editor.FieldPlot(name) is not { } p) { Heading = "Nothing selected"; return; }
        _plotName = name;
        IsFieldPlot = true;
        NameText = name;
        Heading = $"Field plot {name}";
        PlotIsTemperature = p.IsTemperature;
        PlotHidden = p.Hidden;
        PlotIsDrawn = !p.Hidden;

        List<string> setups = [ActiveSetupChoice, .. editor.PlotSetupNames()];
        if (p.Setup is { } named && !setups.Contains(named)) setups.Add(named);
        KeepOrReplace(PlotSetups, setups);
        PlotSetup = p.Setup ?? ActiveSetupChoice;

        var request = editor.PlotRequest(p, resolveScene: false);
        PlotHasSolver = request.Solver is not null;
        PlotSolver = request.Solver;

        // The Solution picker: what the run saved, by value — and the plot's own value when the run lacks it, so the picker
        // never shows another step as if it were chosen.
        var found = editor.Discovered(request);
        var mine = found.Items.Where(i => request.Solver is null || string.Equals(i.Run.Solver, request.Solver, StringComparison.OrdinalIgnoreCase)).ToList();
        // brief-dfr31-3 R-dfr31-3-4 — no finished run of the plot's setup: ONE disabled row saying what will appear, in that
        // setup's own terms, and the quantities its run will offer, disabled. Never another setup kind's choices.
        var awaiting = mine.Count == 0 && !found.Ran && request.SetupProblem is null && request.RunSetup is { } runSetup ? runSetup : null;
        List<C3dPlotSolutionChoice> solutions = awaiting is not null
            ? [new C3dPlotSolutionChoice(FieldPlotResolver.AwaitingRun(awaiting), null, IsEnabled: false)]
            : [.. mine.Select(i => new C3dPlotSolutionChoice(i.Label, FieldPlotResolver.SolutionKey(i.Solution, editor.Viewer.Scene.Problem)))];
        C3dPlotSolutionChoice? chosen = awaiting is not null ? solutions[0]
            : p.Solution is null ? solutions.FirstOrDefault() : solutions.FirstOrDefault(c => c.Solution!.SameAs(p.Solution));
        if (chosen is null && p.Solution is { } missing)
        {
            chosen = new C3dPlotSolutionChoice($"{missing.Describe()} (not saved)", missing);
            solutions.Add(chosen);
        }
        if (editor.PlotCanAddFrequency(p)) solutions.Add(new C3dPlotSolutionChoice(OtherFrequencyLabel, null, IsOther: true));
        KeepOrReplace(PlotSolutions, solutions, SameSolutionChoice);
        // The row kept, not its fresh twin: the step names no identity of its own, so a fresh instance would read as a change.
        PlotSolution = chosen is null ? null : PlotSolutions[solutions.IndexOf(chosen)];

        // The Quantity picker: only what the step offers (the plot's own quantity kept, marked, when it does not).
        var item = FieldPlotResolver.PickSolution(p.Solution, request.Solver, mine, editor.Viewer.Scene.Problem, p.Quantity);
        var offered = awaiting is not null ? FieldPlotResolver.ExpectedQuantities(awaiting)
            : item is null ? [] : !p.Hidden && editor.Viewer.FieldLayers.FirstOrDefault(l => l.Name == p.Name) is { Quantities.Count: > 0 } layer
            ? [.. layer.Quantities] : editor.OfferedQuantities(item);
        bool live = awaiting is null;
        List<C3dPlotQuantityChoice> quantities = [.. offered.Select(q => new C3dPlotQuantityChoice(q.Label, q.Array.Name, q.Mode.ToString(), live))];
        var qc = quantities.FirstOrDefault(c => c.Quantity == p.Quantity && (p.Mode is null || string.Equals(c.Mode, p.Mode, StringComparison.OrdinalIgnoreCase)));
        if (qc is null)
        {
            qc = new C3dPlotQuantityChoice(FieldNames.Friendly(p.Quantity) + (p.Mode is { } md ? $" ({md})" : "") + (offered.Count > 0 ? " (not offered)" : ""),
                                           p.Quantity, p.Mode, live);
            quantities.Add(qc);
        }
        KeepOrReplace(PlotQuantities, quantities);
        PlotQuantity = qc;

        KeepOrReplace(PlotTargets, [OnClip, OnSurfaces, OnFaces]);
        PlotTarget = p.On switch { C3dFieldPlotOn.ClipPlane => OnClip, C3dFieldPlotOn.Surfaces => OnSurfaces, _ => OnFaces };
        PlotIsClip = p.On == C3dFieldPlotOn.ClipPlane;
        PlotIsFaces = p.On == C3dFieldPlotOn.Faces;
        PlotAxis = p.Axis ?? C3dAxis.Z;
        PlotShowPlane = p.ShowPlane;
        OnPropertyChanged(nameof(PlotThroughArmed));
        PlotOffsetText = p.Offset is { } o ? FormatOffset(o) : "";
        LoadPlotOffsetRange(p);
        PlotFacesText = p.Faces.Count == 0
            ? "None yet: right-click a face ▸ " + (p.IsTemperature ? "Plot Temperature" : "Plot Field") + " while this plot is drawn."
            : string.Join("\n", p.Faces.Select(f => f.Face + f.Side switch { C3dFieldSide.Top => " (top side)", C3dFieldSide.Bottom => " (bottom side)", _ => "" }));
        PlotDb = p.Db;
        PlotPercentile = p.Percentile;
        PlotFixRange = p.FixRange;
        PlotMip = p.Mip;
        PlotCanMip = p.On == C3dFieldPlotOn.ClipPlane && FieldNames.IsSar(p.Quantity);
        // brief-em3d-100 — the drive, for a driven solution only (eigenmode, static and thermal normalise their own way)
        PlotIsDriven = item?.Solution.Kind == FieldProblemKind.Driven ||
                       item is null && p.Solution?.GHz is not null;
        PlotDriveText = C3dDrivePower.Format(p.DrivePowerW ?? CircuitRF.Design.Em3d.PalaceDrive.IncidentPowerW);
        PlotDriveReferredTo = PlotDriveReferences[(int)p.DriveReferredTo];
        PlotProblem = editor.FieldPlotProblem(p);
        PlotStale = editor.FieldPlotStaleText(p);
    }

    /// <summary>Replaces <paramref name="rows"/> with <paramref name="fresh"/> only when they differ (see the file's head).</summary>
    private static void KeepOrReplace<T>(ObservableCollection<T> rows, IReadOnlyList<T> fresh, Func<T, T, bool>? same = null)
    {
        same ??= EqualityComparer<T>.Default.Equals;
        if (rows.Count == fresh.Count && rows.Zip(fresh).All(z => same(z.First, z.Second))) return;
        rows.Clear();
        foreach (var r in fresh) rows.Add(r);
    }

    private static bool SameSolutionChoice(C3dPlotSolutionChoice a, C3dPlotSolutionChoice b)
        => a.Label == b.Label && a.IsOther == b.IsOther && a.IsEnabled == b.IsEnabled && (a.Solution is null ? b.Solution is null : a.Solution.SameAs(b.Solution));

    private string FormatOffset(long dbu) => LayoutUnits.Format(dbu, Editor.Document.DisplayUnit, Editor.Document.DbuPerMicron);

    /// <summary>The slider's range, the model's extent on the plot's axis (widened to hold an offset outside it), and its
    /// thumb — at the dragged offset when a drag is under way across this reload, so the thumb does not jump back.</summary>
    private void LoadPlotOffsetRange(C3dFieldPlot p)
    {
        _plotOffsetDragged = Editor.PlotOffsetPreview(p.Name);
        long? at = _plotOffsetDragged ?? p.Offset;
        if (_plotOffsetDragged is { } d) PlotOffsetText = FormatOffset(d);
        if (Editor.PlotAxisRange(p.Axis ?? C3dAxis.Z) is not { } range)
        {
            PlotHasOffsetRange = false;
            return;
        }
        var (lo, hi) = range;
        if (at is { } a) (lo, hi) = (Math.Min(lo, a), Math.Max(hi, a));
        PlotHasOffsetRange = hi > lo;
        PlotOffsetMin = lo;
        PlotOffsetMax = Math.Max(hi, lo + 1);
        PlotOffsetStep = Math.Max(1, (hi - lo) / 100.0);
        PlotOffsetSlider = at ?? (lo + hi) / 2.0;
    }

    /// <summary>After a reload: a drag no longer shown here (the selection changed under it) ends, so the view stops drawing an
    /// offset nothing will write.</summary>
    private void EndStrayPlotOffsetPreview()
    {
        if (_plotOffsetDragged is null || !IsFieldPlot) { _plotOffsetDragged = null; Editor.EndPlotOffsetPreview(); }
    }

    partial void OnPlotOffsetSliderChanged(double value)
    {
        if (_loading || !IsFieldPlot || !PlotIsClip) return;
        long dbu = (long)Math.Round(value);
        _plotOffsetDragged = dbu;
        _loading = true;
        try { PlotOffsetText = FormatOffset(dbu); }
        finally { _loading = false; }
        Editor.PreviewPlotOffset(_plotName, dbu);
    }

    /// <summary>The slider was released (or its keys let go): the dragged offset, written as one undo entry.</summary>
    public void CommitPlotOffsetSlider()
    {
        if (_plotOffsetDragged is not { } dbu) return;
        _plotOffsetDragged = null;
        Editor.EndPlotOffsetPreview(redraw: false);
        CommitPlot($"Cut {_plotName} at {PlotAxis} = {PlotOffsetText.Trim()}", p => { p.Axis ??= PlotAxis; p.Offset = dbu; });
    }

    /// <summary>The drawn plot's verdict changed (the run was read, the step loaded): the sentence above the fields follows, and
    /// so do the pickers — a re-run may save steps or arrays the last one did not (brief-emi-11: an EMerge run gaining J_s on its
    /// surfaces), and the plot already selected must list them. Rows that did not change stay in place (KeepOrReplace).</summary>
    internal void RefreshPlotProblem()
    {
        if (!IsFieldPlot || Editor.FieldPlot(_plotName) is null) return;
        bool was = _loading;
        _loading = true;
        try { LoadFieldPlot(_plotName); }
        finally { _loading = was; }
    }

    private void CommitPlot(string description, Action<C3dFieldPlot> mutate)
    {
        if (_loading || !IsFieldPlot) return;
        Error = Editor.SetFieldPlot(_plotName, description, mutate) ?? "";
    }

    partial void OnPlotSetupChanged(string? value)
    {
        if (value is null) return;
        string? setup = value == ActiveSetupChoice ? null : value;
        // Another setup's run saves other steps: the solution is left for the picker to name again.
        CommitPlot($"Plot {_plotName} from {value}", p => { if (p.Setup != setup) { p.Setup = setup; p.Solution = null; } });
    }

    partial void OnPlotSolverChanged(string? value)
    {
        if (value is null) return;
        if (CircuitRF.Design.Em3d.Em3dSolvers.ByName(value) is not { } solver) return;
        CommitPlot($"Plot {_plotName} from {value}", p => p.Solver = solver.Id);
    }

    partial void OnPlotSolutionChanged(C3dPlotSolutionChoice? value)
    {
        if (_loading || value is null) return;
        if (value.IsOther) { PlotOtherOpen = true; return; }
        if (value.Solution is null) return;          // the disabled "once it has run" row
        PlotOtherOpen = false;
        CommitPlot($"Plot {_plotName} at {value.Solution!.Describe()}", p => p.Solution = value.Solution.Clone());
    }

    /// <summary>R-em3d83-6 — the Other frequency box's Enter: the setup saves it too, and the plot moves to it.</summary>
    public void CommitOtherFrequency()
    {
        if (!IsFieldPlot) return;
        if (!NumericText.TryParseDouble(PlotOtherText.Trim(), out double ghz))
        {
            Error = "Enter a frequency in GHz, e.g. 6.";
            return;
        }
        Error = Editor.PlotOtherFrequency(_plotName, ghz) ?? "";
        if (Error.Length == 0) { PlotOtherOpen = false; PlotOtherText = ""; }
    }

    partial void OnPlotQuantityChanged(C3dPlotQuantityChoice? value)
    {
        if (value is null || !value.IsEnabled) return;
        CommitPlot($"Plot {_plotName}: {value.Label}", p => { p.Quantity = value.Quantity; p.Mode = value.Mode; });
    }

    partial void OnPlotTargetChanged(string? value)
    {
        if (value is null) return;
        var on = value == OnClip ? C3dFieldPlotOn.ClipPlane : value == OnFaces ? C3dFieldPlotOn.Faces : C3dFieldPlotOn.Surfaces;
        CommitPlot($"Plot {_plotName} on {value.ToLowerInvariant()}", p =>
        {
            p.On = on;
            if (on == C3dFieldPlotOn.ClipPlane && p.Offset is null) (p.Axis, p.Offset) = Editor.PlotClipDefault();
        });
    }

    partial void OnPlotAxisChanged(C3dAxis value) => CommitPlot($"Cut {_plotName} across {value}", p => p.Axis = value);

    partial void OnPlotShowPlaneChanged(bool value)
        => CommitPlot($"{(value ? "Always show" : "Show only when selected")} {_plotName}'s plane", p => p.ShowPlane = value);

    /// <summary>brief-dfr31-3 R-dfr31-3-2 — the "Through…" toggle: armed, the next click on an object in the view moves this plot's
    /// plane through that object's centre on its axis.</summary>
    public bool PlotThroughArmed
    {
        get => IsFieldPlot && Editor.PlotThroughArmed == _plotName;
        set
        {
            if (value == PlotThroughArmed) return;
            if (value) Editor.ArmPlotThrough(_plotName);
            else Editor.CancelPlotThrough();
        }
    }

    /// <summary>The editor armed or put away a "Through…" pick: the toggle follows.</summary>
    internal void PlotThroughChanged() => OnPropertyChanged(nameof(PlotThroughArmed));

    /// <summary>The Offset box's Enter or lost focus: a length in the display unit (or with its own), one undo entry.</summary>
    public void CommitPlotOffset()
    {
        if (!IsFieldPlot) return;
        var doc = Editor.Document;
        if (!LayoutUnits.TryParse(PlotOffsetText, doc.DisplayUnit, doc.DbuPerMicron, out long dbu))
        {
            Error = $"Enter a position along {PlotAxis}, e.g. 120 ({LayoutUnits.Suffix(doc.DisplayUnit)}).";
            return;
        }
        _plotOffsetDragged = null;
        Editor.EndPlotOffsetPreview();
        CommitPlot($"Cut {_plotName} at {PlotAxis} = {PlotOffsetText.Trim()}", p => { p.Axis ??= PlotAxis; p.Offset = dbu; });
    }

    /// <summary>brief-em3d-100 — the Drive power box's Enter or lost focus: W, mW, µW or dBm, stored in watts, one undo entry
    /// (none when the power is unchanged).</summary>
    public void CommitPlotDrive()
    {
        if (!IsFieldPlot || Editor.FieldPlot(_plotName) is not { } p) return;
        if (!C3dDrivePower.TryParse(PlotDriveText, out double watts, out string? why)) { Error = why!; return; }
        double now = p.DrivePowerW ?? CircuitRF.Design.Em3d.PalaceDrive.IncidentPowerW;
        if (Math.Abs(watts - now) <= 1e-12 * now) { PlotDriveText = C3dDrivePower.Format(now); return; }
        CommitPlot($"Drive {_plotName} at {C3dDrivePower.Format(watts)}", x => x.DrivePowerW = watts);
    }

    /// <summary>brief-em3d-100 — Incident or Accepted. Accepted at a solution whose run recorded no reflection there is refused
    /// with the sentence, and the plot stays as it was.</summary>
    partial void OnPlotDriveReferredToChanged(string? value)
    {
        if (_loading || !IsFieldPlot || value is null || Editor.FieldPlot(_plotName) is not { } p) return;
        var to = (C3dDriveReferredTo)Math.Max(0, PlotDriveReferences.ToList().IndexOf(value));
        if (to == p.DriveReferredTo) return;
        if (Editor.PlotDriveProblem(p, to) is { } why)
        {
            Error = why;
            _loading = true;
            try { PlotDriveReferredTo = PlotDriveReferences[(int)p.DriveReferredTo]; }
            finally { _loading = false; }
            return;
        }
        CommitPlot($"{_plotName}'s drive {(to == C3dDriveReferredTo.Accepted ? "accepted" : "incident")}", x => x.DriveReferredTo = to);
    }

    partial void OnPlotDbChanged(bool value) => CommitPlot($"{_plotName} in {(value ? "dB" : "linear units")}", p => p.Db = value);
    partial void OnPlotMipChanged(bool value) => CommitPlot($"{_plotName} as {(value ? "a maximum-intensity projection" : "a clip plane")}", p => p.Mip = value);
    partial void OnPlotPercentileChanged(double value) => CommitPlot($"{_plotName}'s range to the {value} percentile", p => p.Percentile = value);
    partial void OnPlotFixRangeChanged(bool value) => CommitPlot($"{(value ? "Fix" : "Free")} the range of {_plotName}", p => p.FixRange = value);
}

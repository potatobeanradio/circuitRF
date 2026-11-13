// brief-em3d-29 — the 3D view's FIELDS: what a Palace run saved, drawn on the clip plane and on surfaces,
// coloured through a uniform, animated through a phase uniform, and read back under the cursor.
//
// The same three loops as the rest of the view (em-3d.md §8.4):
//   * reading a step and building its geometry (slice, surfaces, colour range) is KERNEL work, on the
//     thread pool, a newer request cancelling an older one — the view keeps drawing the last geometry;
//   * a colour-scale change or a PHASE STEP is INPUT work: it rewrites View.Field (the uniform blocks) and
//     asks for a frame. It builds and uploads nothing (gate 5);
//   * the FRAME loop draws Scene3DFieldGeometry, uploaded only when its version moves.
//
// Every value drawn or printed comes from the solver's own files (overview rule): a quantity is offered
// only when an array the step LISTS provides it (FieldQuantity.Offered), and the tooltip samples the
// field data, never the colour.
//
// brief-em3d-96 — every one of those is now a LAYER's (FieldLayer): up to four plots are drawn at once, each with its own
// read, its own build and its own range. The properties the Inspector, the menus and the tests read (SelectedFieldSolution,
// SelectedFieldQuantity, FieldScale, FieldText, FieldGeometry, …) are the FOCUSED layer's — the plot selected in the tree when it
// is drawn, else the first drawn — mirrored into them, and a change to one of them is a change to that layer.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Look;
using CircuitRF.Render.Scene3D.Fields;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    /// <summary>The loop period the phase animation starts at, seconds — a display choice, not the frequency.</summary>
    public const double DefaultLoopSeconds = 2;

    private System.Threading.Timer? _animation;
    private readonly Stopwatch _animationClock = new();
    private double _animationStartDegrees;
    private float _viewW = DefaultViewSize.Width, _viewH = DefaultViewSize.Height;

    /// <summary>The focused layer's triangles (its part of <see cref="FieldDrawn"/>).</summary>
    public Scene3DFieldGeometry FieldGeometry => Focused.Geometry;

    /// <summary>The focused layer's colour range — its group's (null while it draws nothing).</summary>
    public FieldColorScale? FieldScale => Focused.Scale;

    /// <summary>The map the focused quantity is painted with (ColorMap3D.For — the rule `render --field` paints with too).</summary>
    public ColorMap3D FieldMap => ColorMap3D.For(SelectedFieldQuantity);

    public ObservableCollection<FieldSolutionItem> FieldSolutions { get; } = [];
    public ObservableCollection<FieldQuantity> FieldQuantities { get; } = [];
    public IReadOnlyList<double> FieldPercentiles { get; } = [95, 99, 99.9, 100];

    [ObservableProperty] private bool _fieldsAvailable;
    [ObservableProperty] private bool _showField;
    [ObservableProperty] private FieldSolutionItem? _selectedFieldSolution;
    [ObservableProperty] private FieldQuantity? _selectedFieldQuantity;
    /// <summary>A volume quantity on the clip plane (the slice).</summary>
    [ObservableProperty] private bool _fieldOnClipPlane = true;
    /// <summary>A volume quantity on the selected solid's faces; a boundary quantity (J_s) on the conductors.</summary>
    [ObservableProperty] private bool _fieldOnSurfaces = true;
    [ObservableProperty] private bool _fieldDb;
    [ObservableProperty] private double _fieldPercentile = 99;
    [ObservableProperty] private bool _fieldPlaying;
    [ObservableProperty] private double _fieldPhaseDegrees;
    [ObservableProperty] private double _fieldLoopSeconds = DefaultLoopSeconds;
    [ObservableProperty] private string _fieldText = "";

    public string FieldsTip => FieldsAvailable
        ? "Show the solved field, read from the solver's own files"
        : "Simulate with Palace or EMerge to save fields (the setup's SaveFieldsGHz; the sweep's centre by default)";

    /// <summary>A drawn quantity is animated: φ, Play and the loop period drive it (brief-em3d-96: one clock for every layer).</summary>
    public bool FieldCanAnimate => DrawnLayers.Any(l => l.Quantity is { Animated: true }) || SelectedFieldQuantity is { Animated: true };

    partial void OnFieldsAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(FieldsTip));
        OnPropertyChanged(nameof(CanShowMesh));
        OnPropertyChanged(nameof(MeshTip));
    }

    partial void OnShowFieldChanged(bool value)
    {
        View.ShowField = value;
        if (value && !_applyingPlot) EnsureFieldLoaded(Focused);
        if (!value) FieldPlaying = false;
        FrameRequested?.Invoke();
        OnPropertyChanged(nameof(FieldLegendVisible));
        OnPropertyChanged(nameof(ShowsTemperature));
    }

    partial void OnSelectedFieldSolutionChanged(FieldSolutionItem? value)
    {
        // brief-em3d-75 — the sweep slider follows the picker, and the picker the slider.
        if (value is not null && FieldSolutions.IndexOf(value) is var at and >= 0 && at != TemperatureStep) TemperatureStep = at;
        OnPropertyChanged(nameof(TemperatureStepLabel));
        if (_applyingPlot) return;                 // brief-em3d-83 — a layer's own solution, mirrored: nothing to do
        var layer = Focused;
        layer.Item = value;
        ShowSolution(layer, value);
        // brief-em3d-96 — the sweep step is one control: every other temperature layer on this run follows it.
        if (value is not null && layer.Quantity is { IsTemperature: true } && layer.Read is { } read && IndexOfItem(read.Items, value) is var k and >= 0)
            foreach (var other in _layers)
                if (!ReferenceEquals(other, layer) && ReferenceEquals(other.Read, read) && other.Quantity is { IsTemperature: true } &&
                    !ReferenceEquals(other.Item, read.Items[k]))
                {
                    other.Item = read.Items[k];
                    ShowSolution(other, other.Item);
                }
    }

    /// <summary>A layer's new solution drawn: a thermal step on the same run re-reads its temperature alone (gate 7), anything
    /// else is loaded.</summary>
    private void ShowSolution(FieldLayer layer, FieldSolutionItem? item)
    {
        if (!LayerShown(layer)) return;
        if (item is not null && TryRevalueTemperature(layer, item)) return;
        EnsureFieldLoaded(layer);
    }

    partial void OnSelectedFieldQuantityChanged(FieldQuantity? value)
    {
        OnPropertyChanged(nameof(FieldCanAnimate));
        OnPropertyChanged(nameof(FieldMap));
        OnPropertyChanged(nameof(ShowsTemperature));
        if (_applyingPlot) return;
        Focused.Quantity = value;
        if (!FieldCanAnimate) FieldPlaying = false;
        ScheduleFieldGeometry(Focused);
    }

    partial void OnFieldOnClipPlaneChanged(bool value)
    {
        if (_applyingPlot) return;
        Focused.OnClip = value;
        ScheduleFieldGeometry(Focused);
    }

    partial void OnFieldOnSurfacesChanged(bool value)
    {
        if (_applyingPlot) return;
        Focused.OnSurfaces = value;
        ScheduleFieldGeometry(Focused);
    }

    partial void OnFieldDbChanged(bool value)
    {
        if (_applyingPlot) return;
        Focused.Db = value;
        RescaleField(Focused);
    }

    // brief-em3d-75 — the temperature's range is its own (D9): dB and the percentile are an EM field's.
    partial void OnFieldPercentileChanged(double value)
    {
        if (_applyingPlot) return;
        Focused.Percentile = value;
        RescaleField(Focused);
    }

    partial void OnFieldPhaseDegreesChanged(double value)
    {
        WriteFieldUniforms();
        FrameRequested?.Invoke();
    }

    partial void OnFieldPlayingChanged(bool value) => RestartAnimation();

    /// <summary>
    /// brief-idle-power — the pane showing this view hands over its window's frame clock (TopLevel.RequestAnimationFrame), and
    /// null when it stops showing it (its tab in the background, the document closed). Paced by that clock, a playing field
    /// moves only while Avalonia is drawing: not in a background tab, and not in a minimised or covered window, whose render
    /// timer Avalonia stops. A view no pane ever showed (headless) keeps the 16 ms timer.
    /// </summary>
    internal void ShowIn(Action<Action>? frameClock)
    {
        _hosted = true;
        _frameClock = frameClock;
        RestartAnimation();
    }

    private bool _hosted;
    private Action<Action>? _frameClock;
    private int _animationGen;

    /// <summary>True while something is set to move the field's phase (tests).</summary>
    internal bool AnimationRunning => _animation is not null || _frameClockArmed;
    private bool _frameClockArmed;

    private void RestartAnimation()
    {
        _animation?.Dispose();
        _animation = null;
        _frameClockArmed = false;
        int gen = ++_animationGen;
        if (!FieldPlaying || _disposed) return;
        _animationStartDegrees = FieldPhaseDegrees;
        _animationClock.Restart();
        // The clock only moves a number; the frame it asks for draws what is already on the GPU.
        if (!_hosted) _animation = new System.Threading.Timer(_ => _post(AnimationTick), null, 0, 16);
        else if (_frameClock is { } clock) { _frameClockArmed = true; clock(() => FrameTick(gen)); }
    }

    private void FrameTick(int gen)
    {
        if (gen != _animationGen) return;              // superseded: stopped, or re-hosted
        _frameClockArmed = false;
        AnimationTick();
        if (FieldPlaying && !_disposed && _frameClock is { } clock) { _frameClockArmed = true; clock(() => FrameTick(gen)); }
    }

    private void AnimationTick()
    {
        if (!FieldPlaying || _disposed) return;
        double period = FieldLoopSeconds > 0.05 ? FieldLoopSeconds : DefaultLoopSeconds;
        FieldPhaseDegrees = (_animationStartDegrees + 360 * _animationClock.Elapsed.TotalSeconds / period) % 360;
    }

    /// <summary>The overlay's legends: shown with the field.</summary>
    public bool FieldLegendVisible => ShowField && FieldLegendGroups.Count > 0;

    /// <summary>
    /// brief-em3d-109 R-em3d109-4 (owner decision D13) — <c>Lit Fields</c>, <c>Blended Fields</c> or <c>Lit, Blended Fields</c> while the
    /// realistic view draws a field whose colours are not the legend's own (RealisticLook.FieldIndicator, the one spelling); null otherwise.
    /// The live overlay draws it under the legend stack and every picture carries it: it is tied to neither the legend nor the caption
    /// option, and nothing turns it off.
    /// </summary>
    public string? FieldIndicator => View.DrawsRealistic && ShowField && FieldDrawn.Vertices.Length > 0 ? View.Look.FieldIndicator : null;

    /// <summary>The focused plot's legend lines: its group's (FieldLegendGroups) — FieldPlotResolver.LegendLines, the function
    /// `render --field`'s legend comes from.</summary>
    public IReadOnlyList<string> FieldLegendLines()
    {
        var groups = FieldLegendGroups;
        string? name = Focused.Name;
        return groups.FirstOrDefault(g => name is null ? g.Names.Count == 0 : g.Names.Contains(name))?.Lines
            ?? (groups.Count > 0 ? groups[0].Lines : []);
    }

    /// <summary>
    /// The legends' width, held: they are anchored to the view's right edge and sized to the widest line in the column, and a
    /// range line (and φ, while animating) changes length as a slider is dragged, so the boxes twitched sideways. It only grows
    /// while the same plots' same quantities are drawn, and starts again when any changes. brief-em3d-96 D3 — ONE width for the
    /// whole column: a drag of any one plot moves none of them sideways.
    /// </summary>
    internal double HeldLegendWidth(double width)
    {
        string key = string.Join(";", FieldLegendGroups.Select(g => $"{string.Join(",", g.Names)}|{g.Quantity.Symbol}|{g.Scale.Db}"));
        if (key != _legendKey) (_legendKey, _legendWidth) = (key, 0);
        return _legendWidth = Math.Max(_legendWidth, width);
    }

    private string? _legendKey;
    private double _legendWidth;

    /// <summary>brief-em3d-75 — the thermal run's fields (and its table) were read again: the probe table and the menus follow.</summary>
    public event Action? ThermalResultsChanged;

    // ── discovery ───────────────────────────────────────────────────────────────────────────

    /// <summary>The setup whose run a layer reads: its plot's, or — for a plot naming none, and the view's own layer — this
    /// view's own. A plot whose setup is gone reads nothing.</summary>
    private EmSetup? FieldSetupOf(FieldLayer layer)
        => layer.Request is { SetupProblem: not null } ? null : layer.Request?.RunSetup ?? _lastSetup;

    /// <summary>The run directories a layer reads (FieldPlotResolver.RunDirectories): Palace's for its setup's problem type,
    /// openEMS's — whichever the setup runs.</summary>
    private (string? Palace, string? OpenEms) DirsOf(FieldLayer layer)
        => FieldPlotResolver.RunDirectories(FieldSetupOf(layer), _resultsRoot(), layer.Request?.Solver);

    /// <summary>The runs read, by directory: every layer reading one shares it.</summary>
    private readonly Dictionary<(string?, string?), FieldRead> _reads = [];
    /// <summary>A read under way, by directory: its token (a newer one supersedes it).</summary>
    private readonly Dictionary<(string?, string?), long> _reading = [];
    private long _readTokens;

    /// <summary>The run directory the focused layer's fields were read from (Palace's, or a thermal run's own): which setup's run
    /// it is. Set when a read is adopted, not when it is asked for, so it never names a run still being read.</summary>
    public string? FieldRunDirectory => Focused.Read?.Dir;

    /// <summary>Re-reads which fields every drawn layer's run saved (a run may have made new ones). A run no layer reads is
    /// forgotten, so it is read afresh when a plot next asks for it.</summary>
    private void RefreshFields()
    {
        var targets = _layers.Where(l => !l.Disposed).ToList();
        var dirs = targets.Select(DirsOf).ToHashSet();
        foreach (var key in _reads.Keys.Where(k => !dirs.Contains(k)).ToList()) _reads.Remove(key);
        foreach (var group in targets.GroupBy(DirsOf)) StartRead(group.Key, FieldSetupOf(group.First()), group.First().Request?.Solver);
    }

    /// <summary>Discovers <paramref name="dirs"/> off the UI thread and adopts it for every layer reading it.</summary>
    private void StartRead((string? Palace, string? OpenEms) dirs, EmSetup? setup, string? solver)
    {
        var scene = Scene;
        string? root = _resultsRoot();
        long token = _reading[dirs] = ++_readTokens;
        Task.Run(() =>
        {
            var found = FieldPlotResolver.Discover(setup, root, scene.Problem, solver);
            _post(() =>
            {
                if (_disposed || !_reading.TryGetValue(dirs, out long now) || now != token) return;
                _reading.Remove(dirs);
                AdoptRead(dirs, found);
            });
        });
    }

    private void AdoptRead((string? Palace, string? OpenEms) dirs, FieldDiscovery found)
    {
        var (runs, table, groups, why) = (found.Runs, found.Table, found.Groups, found.Why);
        string? dir = dirs.Palace;
        var old = _reads.GetValueOrDefault(dirs);
        // brief-emi-11 — the boundary steps count too: a re-run can save surface steps (J_s) beside the same volume steps
        bool same = old is not null && runs.Count > 0 && runs.Count == old.Runs.Count && dir == old.Dir &&
                    runs.Zip(old.Runs).All(p => p.First.Solutions.Select(x => (x.VolumePvtu, x.BoundaryPvtu))
                                                  .SequenceEqual(p.Second.Solutions.Select(x => (x.VolumePvtu, x.BoundaryPvtu))) &&
                                                  StampOf(p.First) == StampOf(p.Second));
        FieldRead read;
        if (same)
        {
            read = old!;
            read.Table = table ?? read.Table;
            read.Ran = found.Ran;
            read.FieldsDeleted = found.FieldsDeleted;
            read.Why = why;
        }
        else
        {
            if (old is not null) ForgetSteps(old.Items);
            ForgetSteps(found.Items);
            read = new FieldRead(dirs) { Runs = runs, Items = found.Items, Groups = groups, Table = table, Ran = found.Ran, FieldsDeleted = found.FieldsDeleted, Why = why, Dir = dir };
            _reads[dirs] = read;
        }
        var readers = _layers.Where(l => !l.Disposed && DirsOf(l) == dirs).ToList();
        foreach (var layer in readers)
        {
            if (!same)
            {
                if (layer.Name is null) layer.Faces.Clear();          // brief-em3d-82 — a new run: the faces were painted on another
                layer.FacePaints = [];
                layer.Temperature = null;
                layer.Volume = layer.Boundary = null;
                layer.Loaded = null;
                layer.VolumeSampler = layer.BoundarySampler = null;
                layer.Run = runs.FirstOrDefault();
            }
            layer.Read = read;
            layer.Dirs = dirs;
        }
        if (readers.Contains(Focused)) MirrorRead(changed: !same);
        foreach (var layer in readers)
        {
            if (layer.Name is not null)
            {
                // brief-em3d-83 — a plot picks its own solution, by value.
                ApplyPlot(layer);
                if (why is not null && read.Items.Count == 0) SetText(layer, "The fields could not be read: " + why);
                continue;
            }
            if (same)
            {
                if (ShowField) ScheduleFieldGeometry(layer);
                continue;
            }
            // With no plot, the first solution is offered.
            layer.Item = read.Items.FirstOrDefault();
            MirrorFocused();
            if (read.Items.Count == 0)
            {
                ShowField = false;
                ClearFieldGeometry(layer);
                SetText(layer, why is not null ? "The fields could not be read: " + why
                             : read.FieldsDeleted ?FieldPlotResolver.FieldsDeletedSentence(FieldSetupOf(layer)?.Name) : "");
            }
            else if (ShowField) EnsureFieldLoaded(layer);
        }
        FieldsRead?.Invoke();
    }

    /// <summary>The focused layer's run into the properties the menus and the sweep slider read.</summary>
    private void MirrorRead(bool changed)
    {
        var read = Focused.Read;
        var items = read?.Items ?? [];
        _applyingPlot = true;
        try
        {
            if (!FieldSolutions.SequenceEqual(items))
            {
                FieldSolutions.Clear();
                foreach (var x in items) FieldSolutions.Add(x);
            }
        }
        finally { _applyingPlot = false; }
        FieldsAvailable = FieldSolutions.Count > 0;
        OnPropertyChanged(nameof(IsThermalRun));
        OnPropertyChanged(nameof(ThermalTable));
        OnPropertyChanged(nameof(TemperatureStepMax));
        OnPropertyChanged(nameof(HasTemperatureSweep));
        OnPropertyChanged(nameof(TemperatureStepLabel));
        if (changed) ThermalResultsChanged?.Invoke();
    }

    /// <summary>When the run's collection was written: a re-run with the same steps is still new data. A fields analysis's
    /// recombined field is rewritten without the run (brief-sar-10: every tuning step), so its files count too.</summary>
    private static DateTime StampOf(FieldRun run)
    {
        try
        {
            var stamp = run.Solutions.Count > 0 ? File.GetLastWriteTimeUtc(run.Solutions[0].VolumePvtu!) : default;
            foreach (var s in run.Solutions)
                if (s.Cosim is not null && s.VolumePvtu is { } file && File.GetLastWriteTimeUtc(file) is var t && t > stamp) stamp = t;
            return stamp;
        }
        catch (IOException) { return default; }
    }

    // ── the steps, shared (brief-em3d-96) ───────────────────────────────────────────────────

    /// <summary>The loaded steps by file: two plots of one solution read the volume once.</summary>
    private readonly Dictionary<string, Lazy<FieldStep>> _steps = new(StringComparer.Ordinal);
    private long _fieldStepReads;

    /// <summary>Gate 5 — how many step files were opened: two plots of one solution add one.</summary>
    public long FieldStepReads => Interlocked.Read(ref _fieldStepReads);

    /// <summary>What the loaded steps hold, each counted once however many plots draw it.</summary>
    public long FieldResidentBytes => _steps.Values.Where(s => s.IsValueCreated).Sum(s => s.Value.ResidentBytes);

    private static string StepKey(string pvtu, double toMetres) => pvtu + "|" + toMetres.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The step read from <paramref name="pvtu"/>, read once (off the UI thread, by whichever layer asks first) — an
    /// openEMS dump at its solution's referral (brief-em3d-100, <see cref="FieldSolution.DumpScale"/>).</summary>
    private Lazy<FieldStep> StepOf(string key, string pvtu, double toMetres, System.Numerics.Complex dumpScale)
    {
        if (_steps.TryGetValue(key, out var s)) return s;
        return _steps[key] = new Lazy<FieldStep>(() =>
        {
            Interlocked.Increment(ref _fieldStepReads);
            return FieldStep.Open(pvtu, toMetres, dumpScale);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Releases every step no layer reads.</summary>
    private void ReleaseSteps()
    {
        var used = _layers.Where(l => !l.Disposed).SelectMany(l => new[] { l.VolumeKey, l.BoundaryKey }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (string key in _steps.Keys.Where(k => !used.Contains(k)).ToList()) _steps.Remove(key);
    }

    /// <summary>A run was replaced: the steps it listed are read again (the same file names may hold new data).</summary>
    private void ForgetSteps(IEnumerable<FieldSolutionItem> items)
    {
        var files = items.SelectMany(i => new[] { i.Solution.VolumePvtu, i.Solution.BoundaryPvtu }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (string key in _steps.Keys.Where(k => files.Contains(k[..k.LastIndexOf('|')])).ToList()) _steps.Remove(key);
    }

    // ── loading a solution ──────────────────────────────────────────────────────────────────

    private void EnsureFieldLoaded(FieldLayer layer)
    {
        if (layer.Disposed || layer.Item is not { } item) return;
        var run = item.Run;
        // brief-sar-6 R-sar6-5 — a SAR plot's projection is a step of its own: the axis it was taken along is part of what is loaded
        int? mip = layer.Request is { Mip: true } rq ? (int)rq.Plane.Axis : null;
        if (layer.Loaded == item.Solution && layer.Volume is not null && layer.LoadedMip == mip)
        {
            // brief-em3d-83 — the plot's quantity, by name, among what this step offers.
            if (layer.Request is { } p0 && !PickPlotQuantity(layer, p0)) return;
            ScheduleFieldGeometry(layer);
            return;
        }
        var sol = item.Solution;
        layer.LoadCts?.Cancel();
        var cts = layer.LoadCts = new CancellationTokenSource();
        SetText(layer, "Reading the field…");
        string? vk = sol.VolumePvtu is { } v0 ? StepKey(v0, run.ToMetres) : null;
        string? bk = sol.BoundaryPvtu is { } b0 ? StepKey(b0, run.ToMetres) : null;
        (layer.VolumeKey, layer.BoundaryKey) = (vk, bk);
        var volStep = vk is null ? null : StepOf(vk, sol.VolumePvtu!, run.ToMetres, sol.DumpScale);
        var bndStep = bk is null ? null : StepOf(bk, sol.BoundaryPvtu!, run.ToMetres, sol.DumpScale);
        ReleaseSteps();
        Task.Run(() =>
        {
            try
            {
                var vol = volStep?.Value;
                var bnd = bndStep?.Value;
                if (mip is int along && layer.Request is { } mq && vol?.WithMip(mq.Quantity, along) is { } projected) vol = projected;
                cts.Token.ThrowIfCancellationRequested();
                var offered = FieldQuantity.Offered(vol?.Arrays ?? [], bnd?.Arrays ?? []);
                _post(() =>
                {
                    if (cts.IsCancellationRequested || _disposed || layer.Disposed) return;
                    // brief-em3d-82 — a solution on another mesh (the other solver's, a re-meshed run): the painted faces go.
                    // A plot's faces are the scene's, not the mesh's: they stay (brief-em3d-83).
                    if (layer.Request is null && layer.Volume is { } was && vol is not null &&
                        (was.Mesh.NodeCount != vol.Mesh.NodeCount || was.Mesh.CellCount != vol.Mesh.CellCount)) layer.Faces.Clear();
                    layer.Volume = vol;
                    layer.Boundary = bnd;
                    layer.Loaded = sol;
                    layer.LoadedMip = mip;
                    layer.MipPlane = mip is int ax && vol is not null && layer.Request is { } rp
                        ? rp.Plane with { Offset = (float)(vol.Middle(ax) - (ax == 0 ? Scene.Origin.X : ax == 1 ? Scene.Origin.Y : Scene.Origin.Z)) }
                        : null;
                    layer.Run = run;
                    layer.VolumeSampler = layer.BoundarySampler = null;
                    var keep = layer.Quantity;
                    layer.Quantities = offered;
                    if (ReferenceEquals(layer, Focused))
                    {
                        _applyingPlot = true;
                        try
                        {
                            FieldQuantities.Clear();
                            foreach (var q in offered) FieldQuantities.Add(q);
                        }
                        finally { _applyingPlot = false; }
                    }
                    BuildSamplers(layer, vol, bnd);
                    // brief-em3d-83 — a plot names its quantity; one this step does not offer draws nothing, and says so.
                    if (layer.Request is { } plot)
                    {
                        if (PickPlotQuantity(layer, plot)) ScheduleFieldGeometry(layer);
                        FieldsRead?.Invoke();
                        return;
                    }
                    // Keep the reading across solutions when the new one offers it; |E| otherwise.
                    SetQuantity(layer, offered.FirstOrDefault(q => q == keep)
                        ?? offered.FirstOrDefault(q => q.IsTemperature)
                        ?? offered.FirstOrDefault(q => q.Array.Name == "E" && q.Mode == FieldMode.Peak)
                        ?? offered.FirstOrDefault());
                    ScheduleFieldGeometry(layer);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException)
            {
                _post(() =>
                {
                    // a step that failed to read is read again next time, not remembered as failed
                    foreach (string? k in (string?[])[vk, bk])
                        if (k is not null && _steps.TryGetValue(k, out var s) && (ReferenceEquals(s, volStep) || ReferenceEquals(s, bndStep))) _steps.Remove(k);
                    if (!layer.Disposed) SetText(layer, "The field could not be read: " + e.Message);
                });
            }
        });
    }

    /// <summary>A layer's quantity, mirrored when it is the focused one.</summary>
    private void SetQuantity(FieldLayer layer, FieldQuantity? q)
    {
        layer.Quantity = q;
        if (!ReferenceEquals(layer, Focused)) return;
        _applyingPlot = true;
        try { SelectedFieldQuantity = q; }
        finally { _applyingPlot = false; }
        if (!FieldCanAnimate) FieldPlaying = false;
    }

    /// <summary>The point samplers the tooltip reads with, indexed off the UI thread.</summary>
    private void BuildSamplers(FieldLayer layer, FieldStep? vol, FieldStep? bnd)
    {
        Task.Run(() =>
        {
            var vs = vol is not null ? new FieldSampler(vol.Mesh) : null;
            var bs = bnd is not null ? new FieldSampler(bnd.Mesh) : null;
            _post(() =>
            {
                // by MESH, not by step: a sweep step revalued meanwhile (WithArraysOf) is a new step on the same mesh, and the
                // sampler indexes the mesh — adopting only the identical step left hover dead for the rest of the run
                if (vol is not null && ReferenceEquals(layer.Volume?.Mesh, vol.Mesh)) layer.VolumeSampler = vs;
                if (bnd is not null && ReferenceEquals(layer.Boundary?.Mesh, bnd.Mesh)) layer.BoundarySampler = bs;
            });
        });
    }

    // ── painted faces (brief-em3d-82) ───────────────────────────────────────────────────────

    /// <summary>The focused layer's faces painted with the EM field one by one, in the order they were asked for.</summary>
    public IReadOnlyList<PaintedFieldFace> PaintedFieldFaces => Focused.Faces;

    /// <summary>
    /// R-em3d82-1 — right-click a face ▸ Plot Field: face <paramref name="face"/> of scene object <paramref name="obj"/> painted
    /// with the quantity shown (the field turned on when it was off), or taken away when it was already painted on that side.
    /// Asking for the OTHER side of a painted sheet moves it there. Several faces accumulate; they stand beside the clip plane
    /// and the selected region, never instead of them.
    /// </summary>
    public void ToggleFieldFace(string obj, int face, int side = 0)
    {
        var faces = Focused.Faces;
        int at = faces.FindIndex(f => f.Object == obj && f.Face == face);
        if (at >= 0 && faces[at].Side == side) faces.RemoveAt(at);
        else if (at >= 0) faces[at] = new PaintedFieldFace(obj, face, side);
        else faces.Add(new PaintedFieldFace(obj, face, side));
        if (!ShowField && faces.Count > 0) ShowField = true;
        else ScheduleFieldGeometry(Focused);
    }

    /// <summary>The side face <paramref name="face"/> of <paramref name="obj"/> is painted on (0 for a face with no side), or
    /// null when it is not painted.</summary>
    public int? FieldFaceSide(string obj, int face)
        => Focused.Faces.FindIndex(f => f.Object == obj && f.Face == face) is var i and >= 0 ? Focused.Faces[i].Side : null;

    /// <summary>Takes every painted face away (the clip plane and the selected region stay).</summary>
    public void ClearFieldFaces()
    {
        if (Focused.Faces.Count == 0) return;
        Focused.Faces.Clear();
        ScheduleFieldGeometry(Focused);
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────

    private void ClearFieldGeometry(FieldLayer layer)
    {
        layer.Geometry = new Scene3DFieldGeometry([], ++_fieldVersion);
        layer.Surfaces = [];
        layer.Temperature = null;
        layer.HotSpot = null;
        layer.HotSpotLabel = "";
        layer.OwnScale = layer.Scale = null;
        layer.Drive = FieldDriveReading.None;
        layer.Covered.Clear();
        LayersChanged();
    }

    /// <summary>
    /// A ClipPlane plot's plane is being dragged (the Inspector's offset slider). Each tick would otherwise cancel the build under
    /// way, and with a build slower than the ticks no slice was drawn until the release: while dragging, the build under way is
    /// finished and drawn, and the newest plane is built after it. Set false, the newest plane is built at once. brief-em3d-96 —
    /// the focused plot's; <see cref="SetFieldPlaneDragging"/> names the plot.
    /// </summary>
    public bool FieldPlaneDragging
    {
        get => Focused.PlaneDragging;
        set => SetFieldPlaneDragging(Focused.Name, value);
    }

    /// <summary>brief-em3d-96 — plot <paramref name="name"/>'s plane is being dragged (or no longer): its builds coalesce, no
    /// other plot's.</summary>
    public void SetFieldPlaneDragging(string? name, bool dragging)
    {
        if (LayerNamed(name) is not { } layer || layer.PlaneDragging == dragging) return;
        layer.PlaneDragging = dragging;
        if (!dragging && layer.BuildPending) ScheduleFieldGeometry(layer);
    }

    /// <summary>A build ended (drawn, refused or failed) on the UI thread: a plane that waited is built now.</summary>
    private void FieldBuildEnded(FieldLayer layer, CancellationTokenSource cts)
    {
        if (!ReferenceEquals(layer.BuildingCts, cts)) return;
        layer.BuildingCts = null;
        if (layer.BuildPending) ScheduleFieldGeometry(layer);
    }

    /// <summary>Rebuilds every drawn layer that <paramref name="which"/> picks.</summary>
    private void ScheduleFieldGeometry(Func<FieldLayer, bool> which)
    {
        foreach (var layer in _layers.ToList())
            if (which(layer)) ScheduleFieldGeometry(layer);
    }

    /// <summary>Rebuilds a layer's slice and surfaces off the UI thread — on a new solution, quantity, surface choice, selection,
    /// or clip plane. A newer request of the same layer cancels an older one; another layer's never does.</summary>
    private void ScheduleFieldGeometry(FieldLayer layer)
    {
        if (_applyingPlot || layer.Disposed || !LayerShown(layer) || layer.Quantity is not { } q) return;
        if (layer.PlaneDragging && layer.BuildingCts is { } building && ReferenceEquals(building, layer.Cts) && !building.IsCancellationRequested)
        {
            layer.BuildPending = true;
            return;
        }
        layer.BuildPending = false;
        var vol = layer.Volume;
        var bnd = layer.Boundary;
        var scene = Scene;
        var clip = PlaneOf(layer);                 // brief-em3d-83 — a ClipPlane plot's own plane
        bool onPlane = layer.OnClip, onSurfaces = layer.OnSurfaces, db = layer.Db;
        double pct = layer.Percentile;
        var groups = layer.Read?.Groups ?? [];
        var selected = Scene.Object(View.Selected);
        var cts = layer.NewBuild();
        layer.BuildingCts = cts;
        if (q.IsTemperature)
        {
            // brief-em3d-75 — the temperature's own targets, range and hot spot; the triangles carry recipes for a step.
            var faces = layer.TempFaces.ToList();
            bool all = layer.TempAllFaces, onClip = layer.TempOnClip, fix = layer.FixRange;
            var mirrors = MirrorSymmetry ? SymmetryPlanes : [];
            var tempTargets = CurrentTemperatureTargets(layer);
            var table = layer.Read?.Table;
            int step = layer.Loaded?.Index ?? 0;
            var steps = layer.Run?.Solutions ?? [];
            long version = ++layer.GeometryVersion;
            Task.Run(() =>
            {
                try
                {
                    if (vol?.Load(q.Array.Name) is not { } array) { _post(() => FieldBuildEnded(layer, cts)); return; }
                    var (parts, scale, note, covered) = BuildTemperature(q, vol, array, scene, clip, groups, faces, all, onClip, table, step,
                                                                         steps, fix, version, cts.Token, mirrors, tempTargets);
                    cts.Token.ThrowIfCancellationRequested();
                    _post(() =>
                    {
                        if (cts.IsCancellationRequested || _disposed || layer.Disposed) return;
                        // A newer scene arrived while this was built: build again on it (brief-em3d-83 — a plot is not
                        // re-applied on every scene, so a dropped build would leave it undrawn).
                        if (!ReferenceEquals(Scene.Geometry, scene.Geometry)) { layer.BuildingCts = null; ScheduleFieldGeometry(layer); return; }
                        FieldGeometryBuilds++;
                        layer.Builds++;
                        AdoptTemperature(layer, parts, q, scale, covered, note);
                        FieldBuildEnded(layer, cts);
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _post(() => { if (!layer.Disposed) SetText(layer, "The temperature could not be drawn: " + e.Message); FieldBuildEnded(layer, cts); });
                }
            });
            return;
        }
        // brief-em3d-82 — the painted faces, resolved against the scene here (cheap) and painted off the UI thread.
        var targets = FieldSurfacePlot.FaceTargets(scene, layer.Faces);
        var drive = DriveOf(layer, q);                         // brief-em3d-100 — this plot's drive, on its own values
        FieldSampler? sampler = layer.Loaded?.Solver == "openEMS" ? layer.VolumeSampler : null;
        bool unclipped = layer.IsClipPlanePlot;
        bool smooth = layer.Smooth;
        bool sampled = layer.Loaded?.Solver == "openEMS";
        Task.Run(() =>
        {
            try
            {
                // brief-em3d-89 — built by FieldSurfacePlot.Em, which `render --field` draws a Surfaces or Faces plot from too
                var built = FieldSurfacePlot.Em(q, vol, bnd, groups, scene, clip, onPlane, onSurfaces, selected, targets, sampler, sampled, cts.Token,
                                                sliceClipped: !unclipped, drive: drive, smooth: smooth);
                var surfaces = built.Surfaces;
                var refused = built.Refused;
                string? hint = built.Hint;
                cts.Token.ThrowIfCancellationRequested();
                var scale = FieldSurfacePlot.EmScale(q, surfaces, db, pct, drive);
                var packed = Scene3DFieldGeometry.Pack(q, surfaces, built.Nudges);
                string text = surfaces.Count == 0 && hint is not null ? hint : $"{q.Label}: {surfaces.Sum(x => x.TriangleCount):N0} triangles.";
                if (refused.Count > 0) text += " " + string.Join(" ", refused);
                _post(() =>
                {
                    if (cts.IsCancellationRequested || _disposed || layer.Disposed) return;
                    if (!ReferenceEquals(Scene.Geometry, scene.Geometry)) { layer.BuildingCts = null; ScheduleFieldGeometry(layer); return; }
                    FieldGeometryBuilds++;
                    layer.Builds++;
                    layer.Surfaces = surfaces;
                    layer.Drive = drive;
                    layer.FacePaints = built.Paints;
                    layer.OwnScale = scale;
                    layer.Geometry = new Scene3DFieldGeometry(packed, ++_fieldVersion);
                    layer.Covered = new HashSet<string>(built.Covered, StringComparer.Ordinal);
                    SetText(layer, text);
                    LayersChanged();
                    FieldBuildEnded(layer, cts);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                _post(() => { if (!layer.Disposed) SetText(layer, "The field could not be drawn: " + e.Message); FieldBuildEnded(layer, cts); });
            }
        });
    }

    /// <summary>A new percentile or dB choice: a new range over the SAME triangles — uniforms only.</summary>
    private void RescaleField(FieldLayer layer)
    {
        if (layer.Quantity is not { } q || layer.Surfaces.Count == 0 || q.IsTemperature) return;
        layer.OwnScale = FieldSurfacePlot.EmScale(q, layer.Surfaces, layer.Db, layer.Percentile, layer.Drive);
        LayersChanged();
    }

    /// <summary>
    /// brief-em3d-100 — <paramref name="layer"/>'s drive for quantity <paramref name="q"/> at the solution it loaded: its plot's
    /// power and reference, or (the view's own layer) the solver's own power. <see cref="FieldDriveReading.None"/> for anything
    /// but a driven field.
    /// </summary>
    private static FieldDriveReading DriveOf(FieldLayer layer, FieldQuantity q)
    {
        if (layer.Loaded is not { } s) return FieldDriveReading.None;
        var p = layer.Request;
        return FieldDrive.Read(s, q.Array.Name, p?.DrivePowerW, p?.DriveReferredTo ?? CircuitRF.Design.ThreeD.C3dDriveReferredTo.Incident);
    }

    // ── Export picture… (R-em3d29-5) ────────────────────────────────────────────────────────

    public IReadOnlyList<int> ExportScales { get; } = [1, 2, 3, 4];
    [ObservableProperty] private int _exportScale = 2;
    [ObservableProperty] private bool _exportLegend = true;
    [ObservableProperty] private bool _exportCaption = true;
    /// <summary>What the last Copy or Export picture did, for the status line.</summary>
    [ObservableProperty] private string _pictureText = "";

    /// <summary>What Copy puts on the clipboard: the view at this multiple of the window (brief-em3d-29 follow-up).</summary>
    public const int CopyScale = 4;

    /// <summary>brief-em3d-107 R-em3d107-5a — the realistic view's picture is drawn at this many times its size in each direction and
    /// brought down (PictureResample): 1, 2 (the default, owner decision D3) or 4.</summary>
    public IReadOnlyList<int> ExportSupersamples { get; } = PictureResample.Factors;
    [ObservableProperty] private int _exportSupersample = PictureResample.DefaultFactor;
    /// <summary>R-em3d107-5b — the realistic view's picture with no background: the PNG's alpha carries the model's coverage, a glass
    /// object's partial cover and the ground's soft shadow. Never the live view.</summary>
    [ObservableProperty] private bool _exportTransparent;

    /// <summary>
    /// The view drawn by the GPU offscreen at <paramref name="scale"/> × the window's DEVICE-pixel size
    /// (1-4), reduced when needed so neither side passes <see cref="FieldPicture.MaxSide"/>, and read back;
    /// with the legends (brief-em3d-96: the same stack as the view's) and the caption to paint over it as the export options say.
    /// The current camera; no pick pass, so no hover id reaches the picture. UI thread: the read-back holds the render lock.
    /// brief-em3d-107 — <paramref name="supersample"/> (Export Picture's <see cref="ExportSupersample"/>; Copy draws once, at 1) and
    /// <paramref name="transparent"/> apply to the realistic view only.
    /// </summary>
    /// <param name="live">Plan the picture as the live view is planned — hover and selection drawn in the realistic view too. Only
    /// a diagnostic asks for it (tools/UiSnapshot <c>--live</c>); Export Picture never does.</param>
    public FieldPictureShot? CapturePicture(int windowPixelsW, int windowPixelsH, int scale, out string? error, bool transparent = false,
                                            int supersample = 1, bool live = false)
    {
        error = null;
        float k = Math.Clamp(scale, 1, 4);
        k = Math.Min(k, FieldPicture.MaxSide / (float)Math.Max(1, Math.Max(windowPixelsW, windowPixelsH)));
        int w = Math.Clamp((int)Math.Round(windowPixelsW * k), 1, FieldPicture.MaxSide);
        int h = Math.Clamp((int)Math.Round(windowPixelsH * k), 1, FieldPicture.MaxSide);
        // brief-em3d-107 R-em3d107-5a — the realistic picture is drawn at ss × its size (still within MaxSide) and brought down
        int ss = View.DrawsRealistic ? SupersampleFor(w, h, supersample) : 1;
        try
        {
            var backend = Session.EnsureBackend();
            var plan = new Scene3DFramePlan();
            float cx = View.CursorX, cy = View.CursorY;
            View.CursorX = View.CursorY = -1;
            // brief-em3d-106 R-em3d106-2c — export: in the realistic view a picture carries no hover and no selection.
            try
            {
                plan.Plan(Scene, View, w * ss, h * ss, backend.FlipY, pick: false, MeshOverlay, SectionOverlay, GridOverlay, FieldDrawn,
                          export: !live, transparent: transparent, pixelScale: k * ss);
            }
            finally { View.CursorX = cx; View.CursorY = cy; }
            var rgba = Session.RenderPixels(plan, Scene, MeshOverlay, SectionOverlay, GridOverlay, FieldDrawn);
            if (rgba is null) { error = "the 3D view has closed."; return null; }
            if (ss > 1) rgba = PictureResample.Downsample(rgba, w * ss, h * ss, ss);
            var legends = ExportLegend && FieldLegendVisible
                ? FieldLegendGroups.Select(g => new FieldPictureLegend(g.Lines, g.Map, g.Scale)).ToList()
                : [];
            string? caption = null;
            if (ExportCaption && ShowField)
            {
                var labels = DrawnLayers.Select(l => l.Item?.Label).OfType<string>().Distinct().ToList();
                if (labels.Count > 0) caption = string.Join(", ", labels);
            }
            return new FieldPictureShot(rgba, w, h, k, legends, caption, ThemeServiceDark())
            {
                Transparent = plan.Transparent, Supersample = ss, Indicator = FieldIndicator,
            };
        }
        catch (Exception e) when (e is Viewer3DPresentFault or InvalidOperationException or OutOfMemoryException)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>The largest of 4, 2, 1 at most <paramref name="asked"/> that keeps a <paramref name="w"/> × <paramref name="h"/> picture's
    /// drawn size within <see cref="FieldPicture.MaxSide"/>.</summary>
    internal static int SupersampleFor(int w, int h, int asked) => PictureResample.FactorFor(w, h, asked);

    private static bool ThemeServiceDark() => CircuitRF.Render.ThemeService.CurrentVariant == CircuitRF.Render.ColorVariant.Dark;

    // ── the value under the cursor (R-em3d29-3e) ────────────────────────────────────────────

    /// <summary>
    /// The field's value under the cursor, from the FIELD DATA (FieldSampler: the element's own shape
    /// functions), or "" when the cursor is on no drawn field. The clip plane's slice is found by the
    /// cursor's ray, and counts when it is nearer than whatever the ID pass hit. brief-em3d-96 D4 — with several plots drawn,
    /// the plot whose surface is FRONTMOST under the cursor, named: <c>Field2 · |E| = 3.1 kV/m</c>.
    /// </summary>
    internal string FieldValueUnderCursor(uint id, Vector3 point, bool hit)
    {
        if (!ShowField || View.CursorX < 0) return "";
        var drawn = DrawnLayers.ToList();
        string best = "";
        float bestT = float.MaxValue;
        foreach (var layer in drawn)
        {
            var (text, t) = ValueUnderCursor(layer, id, point, hit);
            if (text.Length == 0 || t >= bestT) continue;
            (best, bestT) = (drawn.Count > 1 && layer.Name is { } n ? $"{n} · {text}" : text, t);
        }
        return best;
    }

    /// <summary>One layer's value under the cursor and how far along the cursor's ray it lies, or "".</summary>
    private (string Text, float T) ValueUnderCursor(FieldLayer layer, uint id, Vector3 point, bool hit)
    {
        if (layer.Quantity is not { } q || layer.Run is not { } run) return ("", 0);
        var (o, d) = View.Camera.Ray(View.CursorX, View.CursorY, _viewW, _viewH);
        float hitT = hit ? Vector3.Dot(point - o, d) : float.MaxValue;
        if (q.IsTemperature) return TemperatureUnderCursor(layer, id, point, hit, q, run, o, d, hitT);
        Span<double> ch = stackalloc double[6];
        double toUnits = 1 / run.ToMetres;
        bool Sample(FieldSampler? sampler, FieldStep? step, Vector3 local, double tol, Span<double> into)
        {
            if (sampler is null || step?.Load(q.Array.Name) is not { } a) return false;
            var (x, y, z) = Scene.ToWorld(local);
            return sampler.Sample(a, x * toUnits, y * toUnits, z * toUnits, into, tol);
        }
        bool found = false;
        float at = hitT;
        var plane = PlaneOf(layer);
        if (!q.OnBoundary && layer.OnClip && plane.Enabled)
        {
            var e = plane.Equation;
            var n = new Vector3(e.X, e.Y, e.Z);
            float den = Vector3.Dot(n, d);
            if (Math.Abs(den) > 1e-12f)
            {
                float t = -(Vector3.Dot(n, o) + e.W) / den;
                if (t > 0 && t <= hitT * 1.0001f && Sample(layer.VolumeSampler, layer.Volume, o + t * d, 0, ch)) (found, at) = (true, t);
            }
        }
        if (!found && hit && View.IsVisible(id) && id <= Scene.Objects.Length && layer.Covered.Contains(Scene.Objects[id - 1].Name))
        {
            double tol = 1e-3 * (Scene.BoundsMax - Scene.BoundsMin).Length() * toUnits;
            found = q.OnBoundary ? Sample(layer.BoundarySampler, layer.Boundary, point, tol, ch)
                                 : Sample(layer.VolumeSampler, layer.Volume, point, 0, ch);
        }
        // brief-em3d-82 — a painted face: read where its paint was read, on the side it shows.
        if (!found && hit && View.IsVisible(id) && LastPick.Object == id && Scene.Object(id) is { } po &&
            layer.FacePaints.FirstOrDefault(p => p.Face.Object == po.Name && p.Face.Face == LastPick.Face).Paint is { } paint)
        {
            double diag = (Scene.BoundsMax - Scene.BoundsMin).Length();
            if (paint.OnBoundary) found = Sample(layer.BoundarySampler, layer.Boundary, point, 1e-3 * diag * toUnits, ch);
            else if (paint.Sampled)
                found = layer.VolumeSampler is { } vs && layer.Volume?.Load(q.Array.Name) is { } va &&
                        FieldFaces.SampleOffFace(vs, va, Vector3D.From(point), Vector3D.From(paint.ReadToward), Scene.Origin, ch);
            // Palace: a hair off the face into the tetrahedron on the side shown (a sheet's two sides differ).
            else found = Sample(layer.VolumeSampler, layer.Volume, point + (float)(1e-6 * diag) * paint.ReadToward, 0, ch);
        }
        if (!found) return ("", 0);
        // brief-em3d-100 — the value at this plot's drive (the step holds the solver's), with no unit when it is referred to none
        for (int i = 0; i < ch.Length; i++) ch[i] *= layer.Drive.Factor;
        double phase = q.Animated ? FieldPhaseDegrees * Math.PI / 180 : 0;
        double v = q.Evaluate(ch, phase);
        string unit = layer.Drive.Relative ? "" : FieldNames.Unit(q.Array.Name);
        string text = $"{q.Symbol} = {v.ToString("G4", CultureInfo.InvariantCulture)}{(unit.Length > 0 ? " " + unit : "")}";
        if (q.Animated) text += $" at φ = {FieldPhaseDegrees.ToString("0", CultureInfo.InvariantCulture)}°";
        return (text, at);
    }
}

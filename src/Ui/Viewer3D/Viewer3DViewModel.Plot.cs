// brief-em3d-83 — the viewer draws document field plots. The editor hands it the drawn plots, resolved against the scene
// (FieldPlotRequest), and the viewer sets each one's field state from it: which run it reads, which saved solution — picked BY
// VALUE, never by the combobox's index — which quantity, where, and the colour scale. The phase, the loop and play/pause stay
// the viewer's: an animation is not a design decision.
//
// A plot whose data is missing draws NOTHING and says why (FieldLayer.Problem): no run of its setup, a run that no longer holds
// its solution, a step that no longer offers its quantity. It is never re-pointed to the nearest frequency (R-em3d83-5).
//
// With no plot the viewer is what brief 29 made it: the ACTIVE setup's run, read and offered, nothing drawn.
//
// brief-em3d-84 — the RESOLUTION (run directories, discovery, the solution by value, the R-em3d83-5 sentences, the quantity pick,
// the legend's lines) is FieldPlotResolver's, below the firewall, so `circuitrf render --field` resolves a plot as this does.
//
// brief-em3d-96 — UP TO FOUR plots are drawn at once, each a FieldLayer (SetPlots). One field buffer holds every layer's
// vertices in drawn-plot order, and each layer is one draw coloured by its own uniform block (FieldUniforms, MaxLayers); the
// focused layer is drawn last, so it wins where two coincide. Plots of the same quantity at the same solution, in the same dB and
// percentile, share ONE range — the percentile over the union of their triangles — and one legend (D2).

using System.Globalization;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    /// <summary>The view's own layer: what is drawn with no plot (brief 29's view).</summary>
    private readonly FieldLayer _own = new(null);

    /// <summary>The drawn layers in drawn-plot order (at most <see cref="FieldUniforms.MaxLayers"/>), or the view's own.</summary>
    private List<FieldLayer> _layers;

    private FieldLayer _focused;
    private bool _applyingPlot;
    private long _fieldVersion;

    /// <summary>The layer the Inspector and the single-plot properties show: the selected plot when it is drawn, else the first.</summary>
    private FieldLayer Focused => _focused;

    /// <summary>brief-em3d-96 — every drawn plot's layer, in drawn-plot order (empty with no plot drawn). Index k is colour block k.</summary>
    public IReadOnlyList<FieldLayer> FieldLayers => _layers.Count == 1 && _layers[0].Name is null ? [] : _layers;

    /// <summary>The layer of plot <paramref name="name"/>, or null when it is not drawn. Null names the focused one.</summary>
    public FieldLayer? LayerNamed(string? name) => name is null ? _focused : _layers.FirstOrDefault(l => l.Name == name);

    /// <summary>The layers that have something to draw.</summary>
    private IEnumerable<FieldLayer> DrawnLayers => _layers.Where(l => !l.Disposed && LayerShown(l) && l.Quantity is not null);

    /// <summary>A layer is shown: a plot whose solution the run holds, or — the view's own — the field switched on.</summary>
    private bool LayerShown(FieldLayer layer) => layer.Name is null ? ShowField : layer.Item is not null;

    /// <summary>The focused plot drawn, or null.</summary>
    public FieldPlotRequest? Plot => _focused.Request;

    /// <summary>R-em3d83-5 — why the focused plot draws nothing (no run, a solution or quantity the run no longer holds), or null.</summary>
    [ObservableProperty] private string? _fieldPlotProblem;

    /// <summary>The run's fields were read again (a new run, another setup), or a drawn plot's problem changed: the editor re-checks
    /// its plots.</summary>
    public event Action? FieldsRead;

    /// <summary>Gate 7 — how many times drawn triangles were cut or gathered from the mesh, over every layer: a temperature's, and
    /// (brief-em3d-82 gate 6) an EM field's, painted faces included — a phase step moves neither.</summary>
    public long FieldGeometryBuilds { get; private set; }

    /// <summary>Draws <paramref name="plot"/> alone (null draws nothing): <see cref="SetPlots"/> with one.</summary>
    public void SetPlot(FieldPlotRequest? plot) => SetPlots(plot is null ? [] : [plot], plot?.Name);

    /// <summary>
    /// brief-em3d-96 — draws <paramref name="plots"/> (the first <see cref="FieldUniforms.MaxLayers"/>), focusing
    /// <paramref name="focus"/> when it is among them. A plot already drawn whose request carries the same
    /// <see cref="FieldPlotRequest.Key"/> is left exactly as it is — not re-read, not rebuilt; a changed one is applied again; a
    /// plot no longer drawn has its layer disposed and its builds cancelled.
    /// </summary>
    public void SetPlots(IReadOnlyList<FieldPlotRequest> plots, string? focus = null)
    {
        var wanted = plots.Take(FieldUniforms.MaxLayers).ToList();
        if (wanted.Count == 0)
        {
            bool had = _layers.Any(l => l.Name is not null);
            foreach (var l in _layers.Where(l => l.Name is not null)) l.Dispose();
            _layers = [_own];
            _focused = _own;
            if (had || _own.Read is null) ResetOwnLayer();
            ReleaseSteps();
            return;
        }
        var next = new List<FieldLayer>();
        var changed = new List<FieldLayer>();
        foreach (var p in wanted)
        {
            var layer = _layers.FirstOrDefault(l => l.Name == p.Name && !l.Disposed && l.Name is not null);
            if (layer is null)
            {
                layer = new FieldLayer(p.Name);
                changed.Add(layer);
            }
            else if (p.Key.Length == 0 || p.Key != layer.AppliedKey) changed.Add(layer);
            layer.Request = p;
            layer.AppliedKey = p.Key;
            next.Add(layer);
        }
        foreach (var gone in _layers.Where(l => l.Name is not null && !next.Contains(l))) gone.Dispose();
        if (_layers.Contains(_own)) ClearOwnLayer();
        _layers = next;
        var focused = next.FirstOrDefault(l => l.Name == focus) ?? (next.Contains(_focused) ? _focused : next[0]);
        UpdateShowField();
        if (!ReferenceEquals(focused, _focused)) Refocus(focused);
        foreach (var layer in changed) ApplyPlot(layer);
        ReleaseSteps();
        LayersChanged();
        OnPropertyChanged(nameof(FieldLayers));
    }

    /// <summary>brief-em3d-96 — the plot the Inspector shows (the selected one, when drawn): its state into the single-plot
    /// properties, and it is drawn last. Nothing is built or uploaded.</summary>
    public void FocusPlot(string? name)
    {
        var layer = _layers.FirstOrDefault(l => l.Name == name && name is not null);
        if (layer is null || ReferenceEquals(layer, _focused)) return;
        Refocus(layer);
        LayersChanged();
    }

    /// <summary>Another layer is the focused one: its run and its state into the single-plot properties.</summary>
    private void Refocus(FieldLayer layer)
    {
        bool otherRun = !ReferenceEquals(layer.Read, _focused.Read);
        _focused = layer;
        if (otherRun) MirrorRead(changed: true);
        MirrorFocused();
    }

    /// <summary>With no plot drawn: the view's own layer, empty, on this view's setup's run.</summary>
    private void ResetOwnLayer()
    {
        ClearOwnLayer();
        FieldPlotProblem = null;
        _applyingPlot = true;
        try { ShowField = false; }
        finally { _applyingPlot = false; }
        View.ShowField = false;
        FieldPlaying = false;
        var dirs = DirsOf(_own);
        if (_reads.TryGetValue(dirs, out var read) && !_reading.ContainsKey(dirs))
        {
            _own.Read = read;
            _own.Dirs = dirs;
            _own.Item = read.Items.FirstOrDefault();
        }
        else StartRead(dirs, FieldSetupOf(_own), _own.Request?.Solver);
        MirrorRead(changed: false);
        MirrorFocused();
        ClearFieldGeometry(_own);
        SetText(_own, "");
    }

    private void ClearOwnLayer()
    {
        _own.Faces.Clear();
        _own.TempFaces.Clear();
        _own.TempAllFaces = _own.TempOnClip = false;
        _own.Cts?.Cancel();
        _own.LoadCts?.Cancel();
        _own.Geometry = Scene3DFieldGeometry.None;
        _own.Surfaces = [];
        _own.OwnScale = _own.Scale = null;
        _own.Covered.Clear();
        _own.Temperature = null;
    }

    /// <summary>In plot mode the field is shown while any plot has something to draw.</summary>
    private void UpdateShowField()
    {
        if (_layers.Contains(_own)) return;
        bool any = _layers.Any(l => l.Item is not null);
        if (ShowField == any) { View.ShowField = any; return; }
        _applyingPlot = true;
        try { ShowField = any; }
        finally { _applyingPlot = false; }
    }

    /// <summary>Where <paramref name="item"/> is among a run's solutions (by reference: one run's items are shared), or −1.</summary>
    private static int IndexOfItem(IReadOnlyList<FieldSolutionItem> items, FieldSolutionItem item)
    {
        for (int i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }

    /// <summary>A plot's settings into its layer, its solution picked by value, then one rebuild.</summary>
    private void ApplyPlot(FieldLayer layer)
    {
        if (layer.Request is not { } p || layer.Disposed) return;
        var dirs = DirsOf(layer);
        if (!_reads.TryGetValue(dirs, out var read))
        {
            // the run is read first; AdoptRead applies every layer reading it
            layer.Read = null;
            layer.Dirs = dirs;
            if (!_reading.ContainsKey(dirs)) StartRead(dirs, FieldSetupOf(layer), layer.Request?.Solver);
            return;
        }
        bool newRead = !ReferenceEquals(layer.Read, read);
        layer.Read = read;
        layer.Dirs = dirs;
        if (newRead) layer.Run ??= read.Runs.FirstOrDefault();
        layer.OnClip = p.On == C3dFieldPlotOn.ClipPlane;
        layer.OnSurfaces = p.On == C3dFieldPlotOn.Surfaces;
        layer.Db = p.Db;
        layer.Percentile = p.Percentile;
        layer.FixRange = p.FixRange;
        layer.Faces.Clear();
        layer.TempFaces.Clear();
        if (p.On == C3dFieldPlotOn.Faces)
        {
            if (p.IsTemperature) layer.TempFaces.AddRange(p.Faces.Select(f => new TemperatureFace(f.Object, f.Face)));
            else layer.Faces.AddRange(p.Faces);
        }
        layer.TempAllFaces = p.IsTemperature && p.On == C3dFieldPlotOn.Surfaces;
        layer.TempOnClip = p.IsTemperature && p.On == C3dFieldPlotOn.ClipPlane;
        var item = FieldPlotResolver.PickSolution(p.Solution, p.Solver, read.Items, Scene.Problem, p.Quantity);
        // brief-em3d-100 — a drive that cannot be shown there (Accepted with no reflection recorded) draws nothing, and says why
        string? driveProblem = item is null ? null : FieldPlotResolver.Drive(p, item.Solution).Problem;
        if (driveProblem is not null) item = null;
        bool moved = !ReferenceEquals(layer.Item, item);
        layer.Item = item;
        if (ReferenceEquals(layer, _focused))
        {
            if (newRead) MirrorRead(changed: true);
            MirrorFocused();
        }
        UpdateShowField();
        SetProblem(layer, driveProblem ?? (item is null ? FieldPlotResolver.PlotProblem(p, read.Items, read.Ran, Scene.Problem, read.FieldsDeleted) : null));
        if (item is null)
        {
            ClearFieldGeometry(layer);
            SetText(layer, layer.Problem ?? "");
            return;
        }
        // A sweep step on the same thermal run re-reads its temperature alone (brief 75 gate 7).
        if (moved && p.IsTemperature && TryRevalueTemperature(layer, item)) return;
        EnsureFieldLoaded(layer);
    }

    /// <summary>The plot's quantity among what the loaded step offers; false (and the reason) when it offers none.</summary>
    private bool PickPlotQuantity(FieldLayer layer, FieldPlotRequest p)
    {
        var q = FieldPlotResolver.PickQuantity(layer.Quantities, p, out string? problem);
        SetQuantity(layer, q);
        if (q is not null) return true;
        SetProblem(layer, problem);
        ClearFieldGeometry(layer);
        SetText(layer, problem!);
        return false;
    }

    /// <summary>The plane a layer cuts on: a ClipPlane plot's own, or the view's clip plane.</summary>
    private ClipPlane3D PlaneOf(FieldLayer layer) => layer.Request is { On: C3dFieldPlotOn.ClipPlane } p
        ? p.Mip && layer.MipPlane is { } mip ? mip : p.Plane : ToolbarClip;

    /// <summary>The plane the focused layer cuts on.</summary>
    private ClipPlane3D FieldPlane => PlaneOf(_focused);

    /// <summary>A run directory of the focused layer's setup exists: it ran, whether or not it saved fields.</summary>
    private bool FieldsRan => _focused.Read?.Ran ?? false;

    // ── the focused layer, mirrored ───────────────────────────────────────────────────────────

    private void SetText(FieldLayer layer, string text)
    {
        layer.Text = text;
        if (ReferenceEquals(layer, _focused)) FieldText = text;
    }

    private void SetProblem(FieldLayer layer, string? problem)
    {
        if (layer.Problem == problem) return;
        layer.Problem = problem;
        if (ReferenceEquals(layer, _focused)) FieldPlotProblem = problem;
        else FieldsRead?.Invoke();
    }

    /// <summary>The focused layer's state into the properties the Inspector, the menus and the tests read — with no side effect:
    /// a mirrored setting is that layer's own already.</summary>
    private void MirrorFocused()
    {
        var l = _focused;
        _applyingPlot = true;
        try
        {
            var items = l.Read?.Items ?? [];
            if (!FieldSolutions.SequenceEqual(items))
            {
                FieldSolutions.Clear();
                foreach (var x in items) FieldSolutions.Add(x);
            }
            if (!FieldQuantities.SequenceEqual(l.Quantities))
            {
                FieldQuantities.Clear();
                foreach (var q in l.Quantities) FieldQuantities.Add(q);
            }
            SelectedFieldSolution = l.Item;
            SelectedFieldQuantity = l.Quantity;
            FieldOnClipPlane = l.OnClip;
            FieldOnSurfaces = l.OnSurfaces;
            FieldDb = l.Db;
            FieldPercentile = l.Percentile;
            FixRangeAcrossSweep = l.FixRange;
            TemperatureAllFaces = l.TempAllFaces;
            TemperatureOnClip = l.TempOnClip;
            FieldText = l.Text;
            FieldPlotProblem = l.Problem;
        }
        finally { _applyingPlot = false; }
        if (l.Read is not null) FieldsAvailable = FieldSolutions.Count > 0;
        // the run's own properties (the sweep's length, the table) are MirrorRead's: raised when the run changes, never on an
        // apply — the sweep control is rebuilt on them
        foreach (string p in (string[])[nameof(FieldScale), nameof(FieldGeometry), nameof(HotSpot), nameof(HotSpotLabel), nameof(Plot),
                                        nameof(FieldCanAnimate), nameof(FieldMap), nameof(ShowsTemperature), nameof(FieldLegendVisible)])
            OnPropertyChanged(p);
    }

    // ── what the frame draws: the buffer, the ranges, the uniform blocks ──────────────────────

    /// <summary>brief-em3d-96 — every drawn layer's triangles in one buffer (drawn-plot order), with each layer's range and colour
    /// block in draw order, the focused layer last. What the frame loop draws and uploads (by version).</summary>
    public Scene3DFieldGeometry FieldDrawn { get; private set; } = Scene3DFieldGeometry.None;

    private long[] _bufferVersions = [];
    private FieldVertex[] _bufferVertices = [];
    private FieldLayerRange[] _bufferRanges = [];
    private long _bufferVersion, _bufferGeometryVersion = -1;

    /// <summary>Any layer's triangles, range or focus changed: the groups' ranges, the buffer, the covered objects and the uniform
    /// blocks again, and a frame.</summary>
    private void LayersChanged()
    {
        Regroup();
        RebuildFieldBuffer();
        var cov = new bool[Scene.Objects.Length];
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in DrawnLayers) covered.UnionWith(l.Covered);
        for (int i = 0; i < cov.Length; i++) cov[i] = covered.Contains(Scene.Objects[i].Name);
        View.FieldCovered = cov;
        WriteFieldUniforms();
        foreach (string p in (string[])[nameof(FieldScale), nameof(FieldGeometry), nameof(FieldLegendVisible), nameof(HotSpot), nameof(HotSpotLabel),
                                        nameof(FieldCanAnimate)])
            OnPropertyChanged(p);
        FrameRequested?.Invoke();
    }

    /// <summary>The buffer again when any layer's triangles changed (a new version, uploaded once); the draw order alone when only
    /// the focus did (the same version: nothing uploads).</summary>
    private void RebuildFieldBuffer()
    {
        var versions = new long[_layers.Count];
        for (int k = 0; k < _layers.Count; k++)
            versions[k] = !_layers[k].Disposed && LayerShown(_layers[k]) && _layers[k].Quantity is not null ? _layers[k].Geometry.Version : -1;
        if (!versions.SequenceEqual(_bufferVersions))
        {
            _bufferVersions = versions;
            var shown = Enumerable.Range(0, _layers.Count).Where(k => versions[k] >= 0 && _layers[k].Geometry.Vertices.Length > 0).ToList();
            if (shown.Count == 1)
            {
                // One layer: its own buffer and version, so one plot draws and uploads exactly as it always did.
                var g = _layers[shown[0]].Geometry;
                _bufferVertices = g.Vertices;
                _bufferVersion = g.Version;
                _bufferGeometryVersion = g.GeometryVersion;
                _bufferRanges = [new FieldLayerRange(0, g.Vertices.Length, shown[0])];
            }
            else
            {
                int total = shown.Sum(k => _layers[k].Geometry.Vertices.Length), at = 0;
                _bufferVertices = new FieldVertex[total];
                _bufferRanges = new FieldLayerRange[shown.Count];
                for (int i = 0; i < shown.Count; i++)
                {
                    var v = _layers[shown[i]].Geometry.Vertices;
                    v.CopyTo(_bufferVertices, at);
                    _bufferRanges[i] = new FieldLayerRange(at, v.Length, shown[i]);
                    at += v.Length;
                }
                _bufferVersion = ++_fieldVersion;
                _bufferGeometryVersion = -1;
            }
        }
        int focus = _layers.IndexOf(_focused);
        var ordered = _bufferRanges.Where(r => r.Layer != focus).Concat(_bufferRanges.Where(r => r.Layer == focus)).ToArray();
        FieldDrawn = new Scene3DFieldGeometry(_bufferVertices, _bufferVersion, _bufferGeometryVersion, ordered);
    }

    /// <summary>
    /// D2 — each drawn layer's range: its group's. A group is the layers of one quantity at one solution in one dB and percentile
    /// (a temperature: one solution); its range is <see cref="FieldColorScale.Auto"/> over the UNION of its members' triangles, so
    /// the colours of two slices compare — a temperature group's, the union of its members' true minimum and maximum. A layer alone
    /// in its group keeps its own.
    /// </summary>
    private void Regroup()
    {
        foreach (var l in _layers) l.Scale = l.OwnScale;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in RangeGroups())
        {
            if (group.Count < 2) continue;
            var q = group[0].Quantity!;
            FieldColorScale? scale;
            if (q.IsTemperature) scale = group.Select(m => m.OwnScale).OfType<FieldColorScale>().Aggregate((a, b) => a.Union(b));
            else
            {
                string key = string.Join(",", group.Select(m => m.Geometry.Version)) + $"|{group[0].Db}|{group[0].Percentile}";
                used.Add(key);
                if (!_groupScales.TryGetValue(key, out scale))
                    scale = _groupScales[key] = FieldSurfacePlot.EmScale(q, [.. group.SelectMany(m => m.Surfaces)], group[0].Db, group[0].Percentile,
                                                                         group[0].Drive);
            }
            foreach (var m in group) m.Scale = scale;
        }
        foreach (string k in _groupScales.Keys.Where(k => !used.Contains(k)).ToList()) _groupScales.Remove(k);
    }

    private readonly Dictionary<string, FieldColorScale> _groupScales = new(StringComparer.Ordinal);

    /// <summary>The drawn layers with a range, grouped by D2's key, in drawn-plot order of each group's first member.</summary>
    private List<List<FieldLayer>> RangeGroups()
    {
        var groups = new List<List<FieldLayer>>();
        var keys = new List<string>();
        foreach (var l in DrawnLayers)
        {
            if (l.OwnScale is null || l.Quantity is not { } q) continue;
            string solution = l.Loaded is { } s ? $"{s.VolumePvtu}|{s.BoundaryPvtu}" : l.Item?.Label ?? "";
            // brief-em3d-100 — and at one drive: two plots of one solution at two drives read different numbers
            string key = q.IsTemperature ? $"T|{solution}" : $"{q.Array.Name}|{q.OnBoundary}|{q.Mode}|{solution}|{l.Db}|{l.Percentile.ToString("R", CultureInfo.InvariantCulture)}" +
                                                         $"|{l.Drive.Factor.ToString("R", CultureInfo.InvariantCulture)}";
            int at = keys.IndexOf(key);
            if (at < 0) { keys.Add(key); groups.Add([l]); }
            else groups[at].Add(l);
        }
        return groups;
    }

    /// <summary>
    /// D2/D3 — one legend per range group, in drawn-plot order: titled with every plot name in it (none for the view's own layer),
    /// its quantity, the shared range, what was solved; a temperature group "fixed across the sweep" when every member says so.
    /// Two animated plots at different frequencies share φ — each shows one cycle of its own per loop — and their legends say so.
    /// </summary>
    public IReadOnlyList<FieldLegendGroup> FieldLegendGroups
    {
        get
        {
            var list = new List<FieldLegendGroup>();
            var cycles = DrawnLayers.Where(l => l.Quantity is { Animated: true } && l.Item is not null)
                                    .Select(l => l.Item!.Solution is { Kind: FieldProblemKind.Driven } s ? $"f{s.FrequencyHz}" : $"{l.Dirs}|{l.Item.Solution.Index}")
                                    .Distinct().Count();
            foreach (var g in RangeGroups())
            {
                var first = g[0];
                var q = first.Quantity!;
                var scale = first.Scale ?? first.OwnScale!;
                var names = g.Select(m => m.Name).OfType<string>().ToList();
                var hottest = q.IsTemperature ? g.Where(m => m.HotSpot is not null).MaxBy(m => m.HotSpot!.Value.Value) : null;
                var lines = FieldPlotResolver.LegendLines(names.Count > 0 ? string.Join(", ", names) : null, q, scale, first.Item?.Label,
                                                          FieldPhaseDegrees, FieldLoopSeconds,
                                                          g.All(m => m.FixRange && (m.Read?.Items.Count ?? 0) > 1), StepLabelOf(first),
                                                          hottest?.HotSpotLabel ?? "", first.Drive);
                if (q.Animated && cycles > 1)
                    for (int i = 0; i < lines.Count; i++)
                        if (lines[i].StartsWith("φ = ", StringComparison.Ordinal)) lines[i] += "; φ is each plot's own cycle";
                list.Add(new FieldLegendGroup(names, q, scale, ColorMap3D.For(q), lines));
            }
            return list;
        }
    }

    /// <summary>
    /// The uniform blocks: block k is layer k's quantity, range, map and phase. brief-em3d-96 — ONE clock: every animated layer's
    /// block is written with the same φ in the same call, so two plots animate in lock-step and never drift.
    /// </summary>
    private void WriteFieldUniforms()
    {
        View.Field.AsSpan().Clear();
        double phase = FieldPhaseDegrees * Math.PI / 180;
        for (int k = 0; k < _layers.Count && k < FieldUniforms.MaxLayers; k++)
        {
            var l = _layers[k];
            if (l.Disposed || !LayerShown(l) || l.Quantity is not { } q || l.Scale is not { } s) continue;
            FieldUniforms.Write(View.Field.AsSpan(k * FieldUniforms.Floats, FieldUniforms.Floats), q, s, ColorMap3D.For(q),
                                q.Animated ? phase : 0, unclipped: l.IsClipPlanePlot);
        }
    }
}

// brief-em3d-29 — the 3D view's FIELDS: what a Palace run saved, drawn on the clip plane and on surfaces,
// coloured through a uniform, animated through a phase uniform, and read back under the cursor.
//
// The same three loops as the rest of the view (em-3d.md §8.4):
//   * reading a step and building its geometry (slice, surfaces, colour range) is KERNEL work, on the
//     thread pool, a newer request cancelling an older one — the view keeps drawing the last geometry;
//   * a colour-scale change or a PHASE STEP is INPUT work: it rewrites View.Field (a uniform block) and
//     asks for a frame. It builds and uploads nothing (gate 5);
//   * the FRAME loop draws Scene3DFieldGeometry, uploaded only when its version moves.
//
// Every value drawn or printed comes from the solver's own files (overview rule): a quantity is offered
// only when an array the step LISTS provides it (FieldQuantity.Offered), and the tooltip samples the
// field data, never the colour.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

/// <summary>A solution in the picker: a saved frequency (driven), a mode (eigenmode), a terminal (static).</summary>
public sealed record FieldSolutionItem(FieldSolution Solution, string Label, FieldRun Run)
{
    public override string ToString() => Label;
}

public sealed partial class Viewer3DViewModel
{
    /// <summary>The loop period the phase animation starts at, seconds — a display choice, not the frequency.</summary>
    public const double DefaultLoopSeconds = 2;

    private FieldRun? _fieldRun;
    private IReadOnlyList<FieldRun> _fieldRuns = [];
    private string? _fieldRunDir;
    private IReadOnlyList<FieldGroup> _fieldGroups = [];
    private FieldStep? _fieldVolume, _fieldBoundary;
    private FieldSolution? _fieldLoaded;
    private FieldSampler? _volumeSampler, _boundarySampler;
    private IReadOnlyList<FieldSurface> _fieldSurfaces = [];
    private long _fieldVersion;
    private CancellationTokenSource? _fieldLoadCts, _fieldCts;
    private System.Threading.Timer? _animation;
    private readonly Stopwatch _animationClock = new();
    private double _animationStartDegrees;
    private float _viewW = 800, _viewH = 500;

    /// <summary>The field's triangles, as the frame loop draws them.</summary>
    public Scene3DFieldGeometry FieldGeometry { get; private set; } = Scene3DFieldGeometry.None;

    /// <summary>The colour range of what is drawn (null while nothing is).</summary>
    public FieldColorScale? FieldScale { get; private set; }

    public ColorMap3D FieldMap => SelectedFieldQuantity is { Signed: true } ? ColorMap3D.CoolWarm : ColorMap3D.Viridis;

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
        : "Simulate with Palace to save fields (the setup's SaveFieldsGHz; the sweep's centre by default)";

    public bool FieldCanAnimate => SelectedFieldQuantity is { Animated: true };

    partial void OnFieldsAvailableChanged(bool value) => OnPropertyChanged(nameof(FieldsTip));

    partial void OnShowFieldChanged(bool value)
    {
        View.ShowField = value;
        if (value) EnsureFieldLoaded();
        if (!value) FieldPlaying = false;
        FrameRequested?.Invoke();
        OnPropertyChanged(nameof(FieldLegendVisible));
    }

    partial void OnSelectedFieldSolutionChanged(FieldSolutionItem? value) { if (ShowField) EnsureFieldLoaded(); }

    partial void OnSelectedFieldQuantityChanged(FieldQuantity? value)
    {
        OnPropertyChanged(nameof(FieldCanAnimate));
        OnPropertyChanged(nameof(FieldMap));
        if (value is not { Animated: true }) FieldPlaying = false;
        ScheduleFieldGeometry();
    }

    partial void OnFieldOnClipPlaneChanged(bool value) => ScheduleFieldGeometry();
    partial void OnFieldOnSurfacesChanged(bool value) => ScheduleFieldGeometry();
    partial void OnFieldDbChanged(bool value) => RescaleField();
    partial void OnFieldPercentileChanged(double value) => RescaleField();

    partial void OnFieldPhaseDegreesChanged(double value)
    {
        WriteFieldUniforms();
        FrameRequested?.Invoke();
    }

    partial void OnFieldPlayingChanged(bool value)
    {
        _animation?.Dispose();
        _animation = null;
        if (!value) return;
        _animationStartDegrees = FieldPhaseDegrees;
        _animationClock.Restart();
        // The timer only moves a number; the frame it asks for draws what is already on the GPU.
        _animation = new System.Threading.Timer(_ => _post(AnimationTick), null, 0, 16);
    }

    private void AnimationTick()
    {
        if (!FieldPlaying || _disposed) return;
        double period = FieldLoopSeconds > 0.05 ? FieldLoopSeconds : DefaultLoopSeconds;
        FieldPhaseDegrees = (_animationStartDegrees + 360 * _animationClock.Elapsed.TotalSeconds / period) % 360;
    }

    /// <summary>The overlay's legend: shown with the field.</summary>
    public bool FieldLegendVisible => ShowField && FieldScale is not null && SelectedFieldQuantity is not null;

    /// <summary>The legend's lines: the quantity, the range, and what was solved (R-em3d29-3c/3d).</summary>
    public IReadOnlyList<string> FieldLegendLines()
    {
        if (SelectedFieldQuantity is not { } q || FieldScale is not { } s) return [];
        string unit = FieldNames.Unit(q.Array.Name);
        var lines = new List<string>
        {
            $"{q.Symbol}{(unit.Length > 0 ? $" ({(s.Db ? "dB re 1 " + unit : unit)})" : s.Db ? " (dB)" : "")}",
            s.Describe(),
        };
        if (SelectedFieldSolution is { } sol) lines.Add(sol.Label);
        if (q.Animated)
            lines.Add($"φ = {FieldPhaseDegrees.ToString("0", CultureInfo.InvariantCulture)}°, one cycle every " +
                      $"{FieldLoopSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s on screen");
        return lines;
    }

    // ── discovery ───────────────────────────────────────────────────────────────────────────

    /// <summary>The run directories of this setup's 3D solvers — Palace's for its current problem type,
    /// openEMS's — whichever the setup runs.</summary>
    private (string? Palace, string? OpenEms) FieldRunDirectories()
    {
        if (_lastSetup is not { } setup || _resultsRoot() is not { } root) return (null, null);
        return (setup.Solver3D is Em3dSolver.Palace or Em3dSolver.Both ? Em3dRunService.RunDirectory(root, setup, Em3dSolver.Palace) : null,
                setup.Solver3D is Em3dSolver.OpenEms or Em3dSolver.Both ? Em3dRunService.RunDirectory(root, setup, Em3dSolver.OpenEms) : null);
    }

    /// <summary>Re-reads which fields the setup's runs saved (a run may have made new ones).</summary>
    private void RefreshFields()
    {
        var (dir, openEmsDir) = FieldRunDirectories();
        var scene = Scene;
        Task.Run(() =>
        {
            FieldRun? run = null, openEms = null;
            string? why = null;
            try
            {
                run = dir is null ? null : FieldRun.OpenPalace(dir, MeshToMetres);
                openEms = openEmsDir is null ? null : FieldRun.OpenOpenEms(openEmsDir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { why = e.Message; }
            var groups = dir is null ? [] : FieldGroups.Read(dir);
            var modes = run?.Kind == FieldProblemKind.Eigenmode
                ? PalaceRun.ReadModes(Path.Combine(dir!, PalaceConfigWriter.OutputDirectory, PalaceRun.EigFile), out _) : null;
            FieldRun[] runs = [.. new[] { run, openEms }.OfType<FieldRun>()];
            _post(() =>
            {
                if (_disposed) return;
                bool same = runs.Length > 0 && runs.Length == _fieldRuns.Count && dir == _fieldRunDir &&
                            runs.Zip(_fieldRuns).All(p => p.First.Solutions.Select(x => x.VolumePvtu).SequenceEqual(p.Second.Solutions.Select(x => x.VolumePvtu)) &&
                                                          StampOf(p.First) == StampOf(p.Second));
                if (same) { if (ShowField) ScheduleFieldGeometry(); return; }
                _fieldRuns = runs;
                _fieldRun = runs.FirstOrDefault();
                _fieldRunDir = dir;
                _fieldGroups = groups;
                _fieldVolume = _fieldBoundary = null;
                _fieldLoaded = null;
                _volumeSampler = _boundarySampler = null;
                FieldSolutions.Clear();
                foreach (var r in runs)
                    foreach (var x in r.Solutions)
                        FieldSolutions.Add(new FieldSolutionItem(x, SolutionLabel(x, modes, scene.Problem) + (runs.Length > 1 ? $" ({r.Solver})" : ""), r));
                FieldsAvailable = FieldSolutions.Count > 0;
                SelectedFieldSolution = FieldSolutions.FirstOrDefault();
                if (!FieldsAvailable)
                {
                    ShowField = false;
                    ClearFieldGeometry();
                    FieldText = why is null ? "" : "The fields could not be read: " + why;
                }
                else if (ShowField) EnsureFieldLoaded();
            });
        });
    }

    /// <summary>When the run's collection was written: a re-run with the same steps is still new data.</summary>
    private static DateTime StampOf(FieldRun run)
    {
        try { return run.Solutions.Count > 0 ? File.GetLastWriteTimeUtc(run.Solutions[0].VolumePvtu!) : default; }
        catch (IOException) { return default; }
    }

    private static string SolutionLabel(FieldSolution s, IReadOnlyList<PalaceMode>? modes, Em3dProblem? problem)
    {
        string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
        return s.Kind switch
        {
            FieldProblemKind.Driven => $"{G(s.Timestep)} GHz" + (s.Excitation > 0 ? $", port {s.Excitation} driven" : ""),
            FieldProblemKind.Eigenmode when modes?.FirstOrDefault(m => m.Index == s.Index + 1) is { } m =>
                $"Mode {s.Index + 1}: {G(m.FrequencyHz / 1e9)} GHz, Q {m.Q.ToString("G3", CultureInfo.InvariantCulture)}",
            FieldProblemKind.Eigenmode => $"Mode {s.Index + 1}",
            FieldProblemKind.Electrostatic => $"Terminal {TerminalName(s.Index, problem)} at 1 V, the others at 0 V",
            _ => $"Terminal {TerminalName(s.Index, problem)} carrying 1 A",
        };
    }

    private static string TerminalName(int index, Em3dProblem? problem)
        => problem?.Terminals is { } t && index >= 0 && index < t.Count ? $"'{t[index].Name}'" : (index + 1).ToString(CultureInfo.InvariantCulture);

    // ── loading a solution ──────────────────────────────────────────────────────────────────

    private void EnsureFieldLoaded()
    {
        if (SelectedFieldSolution is not { } item) return;
        var run = item.Run;
        if (_fieldLoaded == item.Solution && _fieldVolume is not null) { ScheduleFieldGeometry(); return; }
        var sol = item.Solution;
        _fieldLoadCts?.Cancel();
        var cts = _fieldLoadCts = new CancellationTokenSource();
        FieldText = "Reading the field…";
        Task.Run(() =>
        {
            try
            {
                var vol = sol.VolumePvtu is { } v ? FieldStep.Open(v, run.ToMetres) : null;
                var bnd = sol.BoundaryPvtu is { } b ? FieldStep.Open(b, run.ToMetres) : null;
                cts.Token.ThrowIfCancellationRequested();
                var offered = FieldQuantity.Offered(vol?.Arrays ?? [], bnd?.Arrays ?? []);
                _post(() =>
                {
                    if (cts.IsCancellationRequested || _disposed) return;
                    _fieldVolume = vol;
                    _fieldBoundary = bnd;
                    _fieldLoaded = sol;
                    _fieldRun = run;
                    _volumeSampler = _boundarySampler = null;
                    var keep = SelectedFieldQuantity;
                    FieldQuantities.Clear();
                    foreach (var q in offered) FieldQuantities.Add(q);
                    // Keep the reading across solutions when the new one offers it; |E| otherwise.
                    SelectedFieldQuantity = FieldQuantities.FirstOrDefault(q => q == keep)
                        ?? FieldQuantities.FirstOrDefault(q => q.Array.Name == "E" && q.Mode == FieldMode.Peak)
                        ?? FieldQuantities.FirstOrDefault();
                    ScheduleFieldGeometry();
                    BuildSamplers(vol, bnd);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException)
            {
                _post(() => FieldText = "The field could not be read: " + e.Message);
            }
        });
    }

    /// <summary>The point samplers the tooltip reads with, indexed off the UI thread.</summary>
    private void BuildSamplers(FieldStep? vol, FieldStep? bnd)
    {
        Task.Run(() =>
        {
            var vs = vol is not null ? new FieldSampler(vol.Mesh) : null;
            var bs = bnd is not null ? new FieldSampler(bnd.Mesh) : null;
            _post(() =>
            {
                if (ReferenceEquals(_fieldVolume, vol)) _volumeSampler = vs;
                if (ReferenceEquals(_fieldBoundary, bnd)) _boundarySampler = bs;
            });
        });
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────

    private void ClearFieldGeometry()
    {
        FieldGeometry = new Scene3DFieldGeometry([], ++_fieldVersion);
        _fieldSurfaces = [];
        FieldScale = null;
        View.FieldCovered = [];
        OnPropertyChanged(nameof(FieldLegendVisible));
        FrameRequested?.Invoke();
    }

    /// <summary>Rebuilds the drawn slice and surfaces off the UI thread — on a new solution, quantity,
    /// surface choice, selection, or clip plane. A newer request cancels an older one.</summary>
    private void ScheduleFieldGeometry()
    {
        if (!ShowField || SelectedFieldQuantity is not { } q) return;
        var vol = _fieldVolume;
        var bnd = _fieldBoundary;
        var scene = Scene;
        var clip = View.Clip;
        bool onPlane = FieldOnClipPlane, onSurfaces = FieldOnSurfaces, db = FieldDb;
        double pct = FieldPercentile;
        var groups = _fieldGroups;
        var selected = Scene.Object(View.Selected);
        _fieldCts?.Cancel();
        var cts = _fieldCts = new CancellationTokenSource();
        Task.Run(() =>
        {
            try
            {
                var origin = scene.Origin;
                var surfaces = new List<FieldSurface>();
                var nudges = new List<Vector3>();
                var covered = new HashSet<string>(StringComparer.Ordinal);
                string? note = null;
                if (!q.OnBoundary && vol?.Load(q.Array.Name) is { } array)
                {
                    if (onPlane && clip.Enabled)
                    {
                        var e = clip.Equation;
                        surfaces.Add(FieldSlicer.Slice(new FieldMeshTets(vol.Mesh, array, origin), new Vector3D(e.X, e.Y, e.Z), e.W, cts.Token));
                        // The slice lies ON the plane; the plane's own discard would eat half of it, so it
                        // moves a hair to the kept side (n·p + d ≤ 0).
                        float eps = 1e-4f * (scene.BoundsMax - scene.BoundsMin).Length();
                        nudges.Add(-eps * new Vector3(e.X, e.Y, e.Z));
                    }
                    if (onSurfaces && selected is { Kind: Scene3DKind.Dielectric or Scene3DKind.Air or Scene3DKind.Body } s &&
                        groups.Where(g => g.Name == s.Name && g.Dimension == 3).Select(g => g.Attribute).ToHashSet() is { Count: > 0 } region)
                    {
                        surfaces.Add(FieldSurfaces.RegionBoundary(vol.Mesh, array, region, origin, cts.Token));
                        nudges.Add(Vector3.Zero);
                        covered.Add(s.Name);
                    }
                    if (surfaces.Count == 0)
                        note = "Turn the clip plane on, or select a dielectric or the air in the tree, to show the field on it.";
                }
                else if (q.OnBoundary && bnd?.Load(q.Array.Name) is { } barray)
                {
                    var metal = groups.Where(g => g.Kind is "Conductor" or "Sheet").ToList();
                    if (onSurfaces && metal.Count > 0)
                    {
                        surfaces.Add(FieldSurfaces.Boundary(bnd.Mesh, barray, metal.Select(g => g.Attribute).ToHashSet(), origin));
                        nudges.Add(Vector3.Zero);
                        foreach (var g in metal) covered.Add(g.Name);
                    }
                    if (surfaces.Count == 0)
                        note = metal.Count == 0 ? $"{FieldNames.Friendly(q.Array.Name)} is drawn on conductors, and this problem has none."
                                                : "Turn surfaces on to show the field on the conductors.";
                }
                cts.Token.ThrowIfCancellationRequested();
                var scale = FieldColorScale.Auto(q, surfaces, db, pct);
                var packed = Scene3DFieldGeometry.Pack(q, surfaces, nudges);
                _post(() =>
                {
                    if (cts.IsCancellationRequested || _disposed || !ReferenceEquals(Scene, scene)) return;
                    _fieldSurfaces = surfaces;
                    FieldScale = scale;
                    FieldGeometry = new Scene3DFieldGeometry(packed, ++_fieldVersion);
                    var cov = new bool[scene.Objects.Length];
                    for (int i = 0; i < cov.Length; i++) cov[i] = covered.Contains(scene.Objects[i].Name);
                    View.FieldCovered = cov;
                    FieldText = note ?? $"{q.Label}: {surfaces.Sum(x => x.TriangleCount):N0} triangles.";
                    WriteFieldUniforms();
                    OnPropertyChanged(nameof(FieldLegendVisible));
                    FrameRequested?.Invoke();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                _post(() => FieldText = "The field could not be drawn: " + e.Message);
            }
        });
    }

    /// <summary>A new percentile or dB choice: a new range over the SAME triangles — uniforms only.</summary>
    private void RescaleField()
    {
        if (SelectedFieldQuantity is not { } q || _fieldSurfaces.Count == 0) return;
        FieldScale = FieldColorScale.Auto(q, _fieldSurfaces, FieldDb, FieldPercentile);
        WriteFieldUniforms();
        OnPropertyChanged(nameof(FieldLegendVisible));
        FrameRequested?.Invoke();
    }

    /// <summary>The field uniform block from the quantity, the range, the map and the phase.</summary>
    private void WriteFieldUniforms()
    {
        if (SelectedFieldQuantity is not { } q || FieldScale is not { } s) return;
        double phase = q.Animated ? FieldPhaseDegrees * Math.PI / 180 : 0;
        FieldUniforms.Write(View.Field, q, s, FieldMap, phase);
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

    /// <summary>The view as a PNG at <see cref="ExportScale"/> × the window (Export picture…).</summary>
    public byte[]? ExportPng(int windowPixelsW, int windowPixelsH, out string? error)
        => CapturePicture(windowPixelsW, windowPixelsH, ExportScale, out error)?.Png();

    /// <summary>
    /// The view drawn by the GPU offscreen at <paramref name="scale"/> × the window's DEVICE-pixel size
    /// (1-4), reduced when needed so neither side passes <see cref="FieldPicture.MaxSide"/>, and read back;
    /// with the legend and the caption to paint over it as the export options say. The current camera; no
    /// pick pass, so no hover id reaches the picture. UI thread: the read-back holds the render lock.
    /// </summary>
    public FieldPictureShot? CapturePicture(int windowPixelsW, int windowPixelsH, int scale, out string? error)
    {
        error = null;
        float k = Math.Clamp(scale, 1, 4);
        k = Math.Min(k, FieldPicture.MaxSide / (float)Math.Max(1, Math.Max(windowPixelsW, windowPixelsH)));
        int w = Math.Clamp((int)Math.Round(windowPixelsW * k), 1, FieldPicture.MaxSide);
        int h = Math.Clamp((int)Math.Round(windowPixelsH * k), 1, FieldPicture.MaxSide);
        try
        {
            var backend = Session.EnsureBackend();
            var plan = new Scene3DFramePlan();
            float cx = View.CursorX, cy = View.CursorY;
            View.CursorX = View.CursorY = -1;
            try { plan.Plan(Scene, View, w, h, backend.FlipY, pick: false, MeshOverlay, SectionOverlay, GridOverlay, FieldGeometry); }
            finally { View.CursorX = cx; View.CursorY = cy; }
            var rgba = Session.RenderPixels(plan, Scene, MeshOverlay, SectionOverlay, GridOverlay, FieldGeometry);
            if (rgba is null) { error = "the 3D view has closed."; return null; }
            var legend = ExportLegend && FieldLegendVisible ? FieldLegendLines() : [];
            var caption = ExportCaption && ShowField && SelectedFieldSolution is { } sol ? sol.Label : null;
            return new FieldPictureShot(rgba, w, h, k, legend, legend.Count > 0 ? FieldMap : null, FieldScale, caption,
                                        ThemeServiceDark());
        }
        catch (Exception e) when (e is Viewer3DPresentFault or InvalidOperationException or OutOfMemoryException)
        {
            error = e.Message;
            return null;
        }
    }

    private static bool ThemeServiceDark() => CircuitRF.Render.ThemeService.CurrentVariant == CircuitRF.Render.ColorVariant.Dark;

    // ── the value under the cursor (R-em3d29-3e) ────────────────────────────────────────────

    /// <summary>
    /// The field's value under the cursor, from the FIELD DATA (FieldSampler: the element's own shape
    /// functions), or "" when the cursor is on no drawn field. The clip plane's slice is found by the
    /// cursor's ray, and counts when it is nearer than whatever the ID pass hit.
    /// </summary>
    internal string FieldValueUnderCursor(uint id, Vector3 point, bool hit)
    {
        if (!ShowField || SelectedFieldQuantity is not { } q || _fieldRun is not { } run || View.CursorX < 0) return "";
        Span<double> ch = stackalloc double[6];
        double toUnits = 1 / run.ToMetres;
        bool Sample(FieldSampler? sampler, FieldStep? step, Vector3 local, double tol, Span<double> into)
        {
            if (sampler is null || step?.Load(q.Array.Name) is not { } a) return false;
            var (x, y, z) = Scene.ToWorld(local);
            return sampler.Sample(a, x * toUnits, y * toUnits, z * toUnits, into, tol);
        }
        bool found = false;
        if (!q.OnBoundary && FieldOnClipPlane && View.Clip.Enabled)
        {
            var (o, d) = View.Camera.Ray(View.CursorX, View.CursorY, _viewW, _viewH);
            var e = View.Clip.Equation;
            var n = new Vector3(e.X, e.Y, e.Z);
            float den = Vector3.Dot(n, d);
            if (Math.Abs(den) > 1e-12f)
            {
                float t = -(Vector3.Dot(n, o) + e.W) / den;
                float hitT = hit ? Vector3.Dot(point - o, d) : float.MaxValue;
                if (t > 0 && t <= hitT * 1.0001f) found = Sample(_volumeSampler, _fieldVolume, o + t * d, 0, ch);
            }
        }
        if (!found && hit && View.IsVisible(id) && id <= View.FieldCovered.Length && View.FieldCovered[id - 1])
        {
            double tol = 1e-3 * (Scene.BoundsMax - Scene.BoundsMin).Length() * toUnits;
            found = q.OnBoundary ? Sample(_boundarySampler, _fieldBoundary, point, tol, ch)
                                 : Sample(_volumeSampler, _fieldVolume, point, 0, ch);
        }
        if (!found) return "";
        double phase = q.Animated ? FieldPhaseDegrees * Math.PI / 180 : 0;
        double v = q.Evaluate(ch, phase);
        string unit = FieldNames.Unit(q.Array.Name);
        string text = $"{q.Symbol} = {v.ToString("G4", CultureInfo.InvariantCulture)}{(unit.Length > 0 ? " " + unit : "")}";
        if (q.Animated) text += $" at φ = {FieldPhaseDegrees.ToString("0", CultureInfo.InvariantCulture)}°";
        return text;
    }
}

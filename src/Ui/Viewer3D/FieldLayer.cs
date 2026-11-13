// brief-em3d-96 R-em3d96-2 — ONE DRAWN PLOT'S field state. The viewer draws up to four plots at once (D1); each is a layer:
// its request, the run it reads, the solution and quantity it shows, the step it loaded, the triangles it built, its own range
// and the range its group shares (D2), its text, its problem, and its own build — a newer request for THIS plot cancels an
// older one of this plot, never another plot's. With no plot the viewer has one layer of its own (brief 29's view, Name null),
// driven by the toolbar properties.
//
// The steps a layer loads are the viewer's (Viewer3DViewModel.StepOf, cached by file), so two plots of one solution read the
// volume once. The phase is not a layer's: one clock animates every layer (Viewer3DViewModel.WriteFieldUniforms).

using System.Numerics;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;

namespace CircuitRF.Ui.Viewer3D;

/// <summary>One drawn field plot's state in the 3D view (brief-em3d-96): what it reads, what it built, how it is coloured.</summary>
public sealed class FieldLayer
{
    internal FieldLayer(string? name) => Name = name;

    /// <summary>The plot's name; null for the view's own layer (no plot is drawn).</summary>
    public string? Name { get; }

    /// <summary>The plot as the editor resolved it; null for the view's own layer.</summary>
    public FieldPlotRequest? Request { get; internal set; }

    // ── what it shows: a plot's settings, or (the view's own layer) the toolbar's ──────────────

    internal bool OnClip = true, OnSurfaces = true, Db, FixRange, TempAllFaces, TempOnClip, Smooth;
    internal double Percentile = 99;
    internal readonly List<PaintedFieldFace> Faces = [];
    internal readonly List<TemperatureFace> TempFaces = [];

    // ── the run it reads ─────────────────────────────────────────────────────────────────────

    internal FieldRead? Read;
    internal (string? Palace, string? OpenEms) Dirs;

    /// <summary>The saved solution shown, picked by value; null when the run holds none the plot names.</summary>
    public FieldSolutionItem? Item { get; internal set; }

    /// <summary>The quantity drawn, among what the loaded step offers.</summary>
    public FieldQuantity? Quantity { get; internal set; }

    /// <summary>What the loaded step offers.</summary>
    public IReadOnlyList<FieldQuantity> Quantities { get; internal set; } = [];

    internal FieldStep? Volume, Boundary;
    internal FieldSolution? Loaded;
    internal FieldRun? Run;
    internal FieldSampler? VolumeSampler, BoundarySampler;
    /// <summary>The step files this layer reads (the viewer's step cache keys), so a step no layer reads is released.</summary>
    internal string? VolumeKey, BoundaryKey;

    /// <summary>brief-sar-6 R-sar6-5 — the step was loaded as a maximum-intensity projection (its axis), and the plane the
    /// projection is drawn on: through the middle of the volume.</summary>
    internal int? LoadedMip;
    internal ClipPlane3D? MipPlane;

    // ── what it built ────────────────────────────────────────────────────────────────────────

    internal IReadOnlyList<FieldSurface> Surfaces = [];
    internal IReadOnlyList<(PaintedFieldFace Face, FieldFacePaint Paint)> FacePaints = [];
    internal TemperatureParts? Temperature;
    internal HashSet<string> Covered = new(StringComparer.Ordinal);

    /// <summary>This plot's own triangles (its part of the field buffer).</summary>
    public Scene3DFieldGeometry Geometry { get; internal set; } = Scene3DFieldGeometry.None;

    /// <summary>brief-em3d-100 — the drive its triangles were built at: their factor over the step's values, and the legend's
    /// line.</summary>
    public FieldDriveReading Drive { get; internal set; } = FieldDriveReading.None;

    /// <summary>The range of this plot's own triangles.</summary>
    public FieldColorScale? OwnScale { get; internal set; }

    /// <summary>The range it is coloured with: its group's (D2), which is its own when it is alone in the group.</summary>
    public FieldColorScale? Scale { get; internal set; }

    public string Text { get; internal set; } = "";

    /// <summary>R-em3d83-5 — why the plot draws nothing, or null.</summary>
    public string? Problem { get; internal set; }

    public FieldHotSpot? HotSpot { get; internal set; }
    public string HotSpotLabel { get; internal set; } = "";

    /// <summary>How many times this layer's triangles were cut or gathered from the mesh (gate 4: a drag of another plot's
    /// slider moves none of these).</summary>
    public long Builds { get; internal set; }

    /// <summary>How many times this layer's triangles were given another sweep step's values and nothing else.</summary>
    public long Revalues { get; internal set; }

    /// <summary>How many of this layer's builds were cancelled — by a newer build of its own, or its disposal.</summary>
    public long Cancellations { get; internal set; }

    /// <summary>The plot is no longer drawn: its builds were cancelled and nothing it had is drawn.</summary>
    public bool Disposed { get; internal set; }

    /// <summary>The build under way (null when none), and a plane asked for while a drag waited on it.</summary>
    public bool Building => BuildingCts is { IsCancellationRequested: false };

    // ── builds ───────────────────────────────────────────────────────────────────────────────

    internal CancellationTokenSource? LoadCts, Cts, BuildingCts;
    internal bool BuildPending, PlaneDragging;
    internal long GeometryVersion;

    /// <summary>The editor's key of the request last applied: an unchanged plot is not applied again.</summary>
    internal string? AppliedKey;

    /// <summary>A ClipPlane plot: its slice is on its own plane, never cut by the view's section.</summary>
    public bool IsClipPlanePlot => Request is { On: C3dFieldPlotOn.ClipPlane };

    /// <summary>A newer build of this layer replaces <see cref="Cts"/>, cancelling the one under way.</summary>
    internal CancellationTokenSource NewBuild()
    {
        if (Cts is { IsCancellationRequested: false } old)
        {
            old.Cancel();
            Cancellations++;
        }
        return Cts = new CancellationTokenSource();
    }

    /// <summary>The plot is no longer drawn: every build and read it has under way is cancelled.</summary>
    internal void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        if (Cts is { IsCancellationRequested: false } c) { c.Cancel(); Cancellations++; }
        LoadCts?.Cancel();
        BuildingCts = null;
        BuildPending = false;
    }
}

/// <summary>What a setup's runs saved (FieldPlotResolver.Discover), read once per run directory and shared by every layer
/// reading it — so their solution items are the same objects, and two plots of one solution are one solution.</summary>
internal sealed class FieldRead((string? Palace, string? OpenEms) dirs)
{
    public (string? Palace, string? OpenEms) Dirs { get; } = dirs;
    public IReadOnlyList<FieldRun> Runs = [];
    public IReadOnlyList<FieldSolutionItem> Items = [];
    public IReadOnlyList<FieldGroup> Groups = [];
    public ThermalResultTable? Table;
    public bool Ran;
    /// <summary>brief-dfr32-3 — the run's saved fields were deleted since it ran (FieldDiscovery.FieldsDeleted).</summary>
    public bool FieldsDeleted;
    public string? Why;
    /// <summary>The Palace run directory read (or a thermal run's own).</summary>
    public string? Dir;
}

/// <summary>What a temperature layer's geometry was built from: each mesh surface's object (for the hot spot's name), its nudge,
/// the wires, and the steps it may be revalued on.</summary>
internal sealed record TemperatureParts(IReadOnlyList<FieldSurface> Surfaces, IReadOnlyList<Vector3> Nudges, IReadOnlyList<string> Objects,
                                        IReadOnlyList<FieldWireSurface> Wires, IReadOnlyList<TemperatureFace> Faces, long GeometryVersion)
{
    /// <summary>brief-em3d-76 — per surface, the reflections that made it (scene-local), empty for a modelled one.</summary>
    public IReadOnlyList<(int Axis, double At)[]> Reflections { get; init; } = [];

    /// <summary>What these parts were built FOR: a step may be revalued onto them only while the targets are still these.</summary>
    public TemperatureTargets? Targets { get; init; }
}

/// <summary>The choices a temperature's geometry depends on, beyond its painted faces: All Faces, the clip section and its
/// plane, the fixed range, and the mirror planes.</summary>
internal sealed record TemperatureTargets(bool AllFaces, bool OnClip, Vector4 Plane, bool FixRange, (int Axis, double AtM)[] Mirrors)
{
    public bool Same(TemperatureTargets o) => AllFaces == o.AllFaces && OnClip == o.OnClip && (!OnClip || Plane == o.Plane) &&
                                              FixRange == o.FixRange && Mirrors.SequenceEqual(o.Mirrors);
}

/// <summary>
/// brief-em3d-96 D2/D3 — one legend: the drawn plots sharing one colour range (the same quantity at the same solution, in the
/// same dB and percentile), titled with every plot name in it.
/// </summary>
public sealed record FieldLegendGroup(IReadOnlyList<string> Names, FieldQuantity Quantity, FieldColorScale Scale, ColorMap3D Map,
                                      IReadOnlyList<string> Lines);

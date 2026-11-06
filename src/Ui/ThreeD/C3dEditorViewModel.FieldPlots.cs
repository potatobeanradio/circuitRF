// brief-em3d-83 — FIELD PLOTS are records of the document: a Field Plots group in the tree (after Probes, in both groupings),
// each row ticked, renamed, duplicated and deleted like a probe, each edit one undo entry, saved with the .c3d.
//
// UP TO FOUR PLOTS ARE DRAWN AT ONCE (brief-em3d-96 D1; brief 83's Q2 drew one): each ticked plot is drawn
// (Viewer3DViewModel.SetPlots), and ticking a fifth is refused with a sentence naming the four drawn — a tick never unticks one
// of the user's. The others stay in the document and in the tree. A new plot PINS the setup active when it was made (Q3) —
// switching setups never changes what an existing plot shows — and is named Field1, Field2 … (Q1).
//
// A PLOT'S EDIT IS NOT A MODEL EDIT: it rebuilds the tree and redraws the field, never the scene, and the stale banner never
// moves for it (C3dPersistence.SerializeForRun). A plot whose data is missing stays, draws nothing, and its row says why.

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The kind of a field plot's tree row.</summary>
    public const string FieldPlotKind = "Field plot";

    /// <summary>The Field Plots group's header tooltip in a setup's 3D view, which has no document to keep a plot in.</summary>
    public const string SessionPlotsTip = "Field plots of a setup's 3D view last for this session: the view has no document to save them in. " +
                                          "Open the 3D view the setup belongs to, to keep one.";

    /// <summary>The plot named <paramref name="name"/>, or null.</summary>
    public C3dFieldPlot? FieldPlot(string name) => Document.FieldPlots.FirstOrDefault(p => p.Name == name);

    /// <summary>brief-em3d-96 D1 — the most plots drawn at once.</summary>
    public const int MaxDrawnPlots = FieldUniforms.MaxLayers;

    /// <summary>The plots drawn: the ticked ones, in list order (at most <see cref="MaxDrawnPlots"/>).</summary>
    public IReadOnlyList<C3dFieldPlot> VisibleFieldPlots => [.. Document.FieldPlots.Where(p => !p.Hidden).Take(MaxDrawnPlots)];

    /// <summary>The FOCUSED plot drawn: the selected one when it is drawn, else the first drawn — the one the Inspector's phase,
    /// the sweep step and the menus' switches act on.</summary>
    public C3dFieldPlot? VisibleFieldPlot => SelectedFieldPlot is { Hidden: false } sel && VisibleFieldPlots.Contains(sel) ? sel : VisibleFieldPlots.FirstOrDefault();

    /// <summary>brief-em3d-96 D1 — why <paramref name="wanted"/> is not drawn: four already are, and which.</summary>
    public string FourDrawnRefusal(string wanted)
        => $"Four plots are drawn: untick one of {string.Join(", ", VisibleFieldPlots.Select(p => p.Name))} to draw {wanted}.";

    /// <summary>Whether another plot may be drawn beside the ones drawn now.</summary>
    private bool RoomToDraw => Document.FieldPlots.Count(p => !p.Hidden) < MaxDrawnPlots;

    /// <summary>The selected row's plot, or null.</summary>
    public C3dFieldPlot? SelectedFieldPlot => SelectedTreeItem is { Kind: FieldPlotKind } r ? FieldPlot(r.Name) : null;

    /// <summary>The active setup's name as the tree and a plot's Setup spell it (the external <c>.cem</c>'s own).</summary>
    private string? ActiveSetupDisplayName => IsViewOnly ? null : IsExternalActive ? ExternalItemName : ActiveSetupName;

    // ── edits: one undo entry each ───────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d83-1 — every plot edit: <paramref name="mutate"/> changes the plot list, and the change is ONE undo entry and a
    /// dirty mark. The plots alone are kept before and after: a plot edit touches nothing else. False when nothing changed.
    /// </summary>
    public bool ChangePlots(string description, Action<List<C3dFieldPlot>> mutate)
    {
        string before = C3dPersistence.SerializeFieldPlots(Document.FieldPlots);
        var selected = SelectedPlotSlot();
        mutate(Document.FieldPlots);
        string after = C3dPersistence.SerializeFieldPlots(Document.FieldPlots);
        if (after == before) return false;
        Push(new C3dRecordsEdit(description, before, after, ApplyPlotRecords, alreadyApplied: true));
        PlotsChanged(selected);
        return true;
    }

    private void ApplyPlotRecords(string text)
    {
        var selected = SelectedPlotSlot();
        Document.FieldPlots = C3dPersistence.DeserializeFieldPlots(text);
        PlotsChanged(selected);
    }

    /// <summary>The selected plot's place in the list, its name, and how many plots there are — or null when no plot is selected.</summary>
    private (int Index, string Name, int Count)? SelectedPlotSlot()
        => SelectedFieldPlot is { } p ? (Document.FieldPlots.IndexOf(p), p.Name, Document.FieldPlots.Count) : null;

    /// <summary>The plots changed: the tree, the drawn plot, the inspector, the dirty mark — never the scene.</summary>
    private void PlotsChanged((int Index, string Name, int Count)? selected = null)
    {
        RebuildTree();
        // The tree keeps its selection by row NAME, so a renamed plot (a rename, or its undo or redo) fell out of it: the same
        // plot, in the same place in a list of the same length, is selected again under its new name. A deleted one is not.
        if (selected is { } was && FieldPlot(was.Name) is null && Document.FieldPlots.Count == was.Count && was.Index >= 0 &&
            was.Index < Document.FieldPlots.Count && SelectedFieldPlot is null)
            SelectPlotRow(Document.FieldPlots[was.Index].Name);
        ApplyVisiblePlots();
        Viewer.RequestFrame();      // brief-dfr31-3 — a plane's chrome moves with its plot, drawn field or none
        Properties.Reload();
        OnPropertyChanged(nameof(IsDirty));
        RaiseMenuStateChanged();
    }

    /// <summary>
    /// The tick: <paramref name="shown"/> draws the plot beside the ones drawn, or hides it. brief-em3d-96 D1 — with four drawn a
    /// fifth is REFUSED: nothing changes, and the sentence naming the four is returned and posted where the tree's refusals go.
    /// </summary>
    public string? SetPlotShown(string name, bool shown)
    {
        if (FieldPlot(name) is not { } plot) return null;
        if (shown && plot.Hidden && !RoomToDraw)
        {
            string why = FourDrawnRefusal(name);
            StatusMessage = why;
            SyncPlotRow(name);
            return why;
        }
        ChangePlots($"{(shown ? "Show" : "Hide")} {name}", plots => plots.First(x => x.Name == name).Hidden = !shown);
        return null;
    }

    /// <summary>A plot row's tick back to what the document says (a refused tick left it ticked).</summary>
    private void SyncPlotRow(string name)
    {
        if (FieldPlot(name) is { } p && AllTreeItems().FirstOrDefault(t => t.Kind == FieldPlotKind && t.Name == name) is { } row) row.Sync(!p.Hidden);
    }

    /// <summary>Changes one plot's fields: one undo entry. Null, or why not.</summary>
    public string? SetFieldPlot(string name, string description, Action<C3dFieldPlot> mutate)
    {
        if (FieldPlot(name) is null) return $"There is no field plot named '{name}'.";
        ChangePlots(description, plots => mutate(plots.First(x => x.Name == name)));
        return null;
    }

    /// <summary>Renames a plot: unique among the plots, and not empty.</summary>
    public string? RenameFieldPlot(string name, string newName)
    {
        newName = newName.Trim();
        if (newName == name) return null;
        if (newName.Length == 0) return "A field plot needs a name.";
        if (FieldPlot(newName) is not null) return $"There is already a field plot named '{newName}'.";
        return SetFieldPlot(name, $"Rename {name} to {newName}", p => p.Name = newName);
    }

    /// <summary>A copy beside the original, named as a new plot is, and drawn — or, with four drawn, added hidden (and said).</summary>
    public void DuplicateFieldPlot(string name)
    {
        if (FieldPlot(name) is not { } source) return;
        var copy = C3dPersistence.DeserializeFieldPlots(C3dPersistence.SerializeFieldPlots([source]))[0];
        copy.Name = C3dFieldPlot.NextName(Document.FieldPlots);
        string? full = RoomToDraw ? null : FourDrawnRefusal(copy.Name);
        copy.Hidden = full is not null;
        ChangePlots($"Duplicate {name}", plots => plots.Insert(plots.FindIndex(p => p.Name == name) + 1, copy));
        if (full is not null) StatusMessage = AddedHidden(copy.Name, full);
        SelectPlotRow(copy.Name);
    }

    /// <summary>The sentence for a plot made while four are drawn: it is added hidden, and why.</summary>
    private static string AddedHidden(string name, string full) => $"{name} was added hidden. {full}";

    public void DeleteFieldPlot(string name) => ChangePlots($"Delete {name}", plots => plots.RemoveAll(p => p.Name == name));

    /// <summary>R-em3d83-3 — New Field Plot…: a plot of the active setup, drawn, selected, and the inspector brought forward to
    /// define it. <paramref name="temperature"/> null takes the active setup's kind.</summary>
    [RelayCommand]
    public void NewFieldPlot() => AddFieldPlot(null, null);

    /// <summary>
    /// brief-sar-9 R-sar9-3 — the view opened on a schematic's fields analysis: the plot that already shows its recombined field,
    /// drawn and selected; else a new one of |B1+| (local SAR when the run kept no B1) at its frequency, on the view's clip plane.
    /// </summary>
    public void ShowCosim(string setup, double ghz, string analysis, bool b1)
    {
        // the recombination wrote its volumes after this view last read the runs: a view already open would not find them
        RunFinished();
        var wanted = new C3dFieldSolution { GHz = ghz, Cosim = analysis };
        if (Document.FieldPlots.FirstOrDefault(p => wanted.SameAs(p.Solution) && string.Equals(p.Setup, setup, StringComparison.Ordinal)) is { } shown)
        {
            if (shown.Hidden) SetPlotsVisible([shown.Name], true, $"Show {shown.Name}");
            SelectPlotRow(shown.Name);
            return;
        }
        AddFieldPlot(false, p =>
        {
            p.Name = C3dFieldPlot.NextName(Document.FieldPlots, analysis + " ");
            p.Setup = setup;
            p.Solver = null;
            p.Solution = wanted;
            p.Quantity = b1 ? CircuitRF.Design.Em3d.SarVolumeFiles.B1PlusQuantity : CircuitRF.Design.Em3d.SarVolumeFiles.LocalQuantity;
            p.Mode = null;
            p.On = C3dFieldPlotOn.ClipPlane;
        });
    }

    private C3dFieldPlot AddFieldPlot(bool? temperature, Action<C3dFieldPlot>? shape, bool select = true)
    {
        var plot = DefaultPlot(temperature);
        shape?.Invoke(plot);
        // brief-em3d-96 D1 — drawn beside the others; with four drawn, added HIDDEN — never by unticking one of the user's.
        string? full = RoomToDraw ? null : FourDrawnRefusal(plot.Name);
        plot.Hidden = full is not null;
        ChangePlots($"New field plot {plot.Name}", plots => plots.Add(plot));
        if (full is not null) StatusMessage = AddedHidden(plot.Name, full);
        if (select)
        {
            SelectPlotRow(plot.Name);
            PropertiesRequested?.Invoke(false);
        }
        return FieldPlot(plot.Name) ?? plot;
    }

    /// <summary>
    /// A new plot's defaults: the active setup (pinned, Q3), the first solution its run saved (by value), |E| — or the
    /// temperature on every exposed face for a thermal setup — cut where the view's clip plane cuts, or on z through the middle
    /// of the model.
    /// </summary>
    private C3dFieldPlot DefaultPlot(bool? temperature)
    {
        var setup = ActiveSetup;
        bool thermal = temperature ?? setup is { IsThermal: true };
        var plot = new C3dFieldPlot
        {
            Name = C3dFieldPlot.NextName(Document.FieldPlots),
            Setup = ActiveSetupDisplayName,
            Solver = setup is { Solver3D.IsCompare: true } ? setup.Solver3D.Reference : null,
            // brief-dfr31-3 — a magnetostatic run saves no E: its plot starts on B
            Quantity = thermal ? C3dFieldPlot.TemperatureQuantity : setup?.Problem3D == CircuitRF.Engine.Em3d.Em3dProblemType.Magnetostatic ? "B" : "E",
            Mode = thermal ? null : nameof(FieldMode.Peak),
            On = thermal ? C3dFieldPlotOn.Surfaces : C3dFieldPlotOn.ClipPlane,
        };
        var request = PlotRequest(plot, resolveScene: false);
        if (FieldPlotResolver.PickSolution(null, request.Solver, Discovered(request).Items, Viewer.Scene.Problem) is { } first)
            plot.Solution = FieldPlotResolver.SolutionKey(first.Solution, Viewer.Scene.Problem);
        (plot.Axis, plot.Offset) = ClipOfView();
        return plot;
    }

    /// <summary>The view's clip plane as a plot's axis and DBU offset, or z through the middle of the model when it is off (or
    /// faces the view).</summary>
    private (C3dAxis Axis, long Offset) ClipOfView()
    {
        var scene = Viewer.Scene;
        double perDbu = C3dLowering.Metres(1, Document.DbuPerMicron);
        var clip = Viewer.ToolbarClip;
        if (clip.Enabled && clip.IsAxis)
        {
            int a = (int)clip.Axis;
            double world = (a == 0 ? scene.Origin.X : a == 1 ? scene.Origin.Y : scene.Origin.Z) + clip.Offset;
            return ((C3dAxis)a, (long)Math.Round(world / perDbu));
        }
        return (C3dAxis.Z, (long)Math.Round(scene.Origin.Z / perDbu));
    }

    private void SelectPlotRow(string name)
    {
        if (AllTreeItems().FirstOrDefault(t => t.Kind == FieldPlotKind && t.Name == name) is { } row) SelectedTreeItem = row;
    }

    // ── what the viewer draws ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The plot as the viewer takes it: its setup's run, its solver, its reading, and — when <paramref name="resolveScene"/> —
    /// its plane in scene-local metres and its faces by the scene's face indices.
    /// </summary>
    internal FieldPlotRequest PlotRequest(C3dFieldPlot p, bool resolveScene = true)
    {
        EmSetup? run = null;
        string? problem = null;
        string setupName = p.Setup ?? ActiveSetupDisplayName ?? "";
        if (IsViewOnly) setupName = Path.GetFileNameWithoutExtension(Viewer.CemPath);
        else if (p.Setup is null)
        {
            run = ActiveRunSetup;
            if (run is null) problem = FieldPlotResolver.NoActiveSetup;
        }
        else if (ExternalSetup is { } x && p.Setup == ExternalItemName) run = x.Setup;
        else (run, problem) = FieldPlotResolver.ResolveSetup(Document, TopFilePath, p.Setup);
        var request = FieldPlotResolver.Request(p, setupName, run, problem, IsViewOnly ? null : run);
        if (!resolveScene) return request;
        return request with { Plane = ScenePlane(p), Faces = FieldPlotResolver.SceneFaces(p, SceneObject) };
    }

    /// <summary>A ClipPlane plot's plane in scene-local metres: its axis, its DBU offset less the scene's origin — kept on the
    /// side the view's clip plane keeps.</summary>
    private ClipPlane3D ScenePlane(C3dFieldPlot p)
        => FieldPlotResolver.ScenePlane(p, Document.DbuPerMicron, Viewer.Scene.Origin, Viewer.ClipFlip);

    private (string Object, int Face)? SceneFace(C3dFieldPlotFace f) => FieldPlotResolver.SceneFace(f, SceneObject);

    /// <summary>The face a gesture picked, as a plot spells it: <c>object/face</c>.</summary>
    private static string FaceKey(Scene3DObject o, int face) => $"{o.Name}/{o.FaceName(face)}";

    /// <summary>The viewer's events a plot's rows follow: the run read again, and the drawn plots' own verdicts.</summary>
    private void WatchFieldPlots()
    {
        Viewer.FieldsRead += RefreshPlotFlags;
        Viewer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Viewer3DViewModel.FieldPlotProblem)) RefreshPlotFlags();
        };
    }

    /// <summary>
    /// brief-em3d-96 — the ticked plots to the viewer (or none), each keyed as brief 83 keyed the one: the same plot, plane,
    /// faces, run and origin is not applied again, so a model edit's new scene does not rebuild a field drawn on the solved
    /// geometry, and a change to one plot rebuilds that plot alone. Called when the plots, the scene or the active setup change.
    /// </summary>
    private void ApplyVisiblePlots()
    {
        var requests = new List<FieldPlotRequest>();
        foreach (var shown in VisibleFieldPlots)
        {
            var plot = shown;
            if (PlotOffsetPreview(plot.Name) is { } dragged)
            {
                plot = C3dPersistence.DeserializeFieldPlots(C3dPersistence.SerializeFieldPlots([plot]))[0];
                plot.Offset = dragged;
            }
            // A ClipPlane plot and the toolbar's section are independent (owner decision, 2026-09-29): the slice is drawn on the
            // plot's own plane and the section never cuts it (fs_field's unclipped flag), so the plot neither moves nor opens the
            // section — it used to follow the plot's plane, which made the two one control.
            var request = PlotRequest(plot);
            string key = string.Join("|", C3dPersistence.SerializeFieldPlots([plot]), request.Plane.Offset, request.Plane.Flip,
                string.Join(",", request.Faces), FieldPlotResolver.RunDirectories(request.RunSetup, ResultsRootProvider?.Invoke(), request.Solver),
                request.Solver, request.SetupProblem, Viewer.Scene.Origin);
            requests.Add(request with { Key = key });
        }
        string all = string.Join("\n", requests.Select(r => r.Key));
        string? focus = VisibleFieldPlot?.Name;
        if (all != _appliedPlotKey)
        {
            _appliedPlotKey = all;
            Viewer.SetPlots(requests, focus);
        }
        else Viewer.FocusPlot(focus);
        RefreshPlotFlags();
    }

    private string? _appliedPlotKey;

    // The Inspector's offset slider: a ClipPlane plot's plane drawn (and the section moved) at the dragged offset while the
    // document keeps its own until the release commits it as one undo entry.
    private (string Name, long Offset)? _plotOffsetPreview;

    /// <summary>The offset a drag is previewing for <paramref name="name"/>, or null.</summary>
    internal long? PlotOffsetPreview(string name) => _plotOffsetPreview is { } pv && pv.Name == name ? pv.Offset : null;

    /// <summary>Draws plot <paramref name="name"/> cut at <paramref name="offset"/> (DBU), writing nothing.</summary>
    public void PreviewPlotOffset(string name, long offset)
    {
        if (FieldPlot(name) is null) return;
        _plotOffsetPreview = (name, offset);
        Viewer.SetFieldPlaneDragging(name, true);
        ApplyVisiblePlots();
        Viewer.RequestFrame();      // the plane's chrome follows the drag, with or without a field to redraw
    }

    /// <summary>Ends a drag's preview. <paramref name="redraw"/> false when a commit of the same offset follows, so the plane
    /// is not drawn back at the document's offset for one request in between.</summary>
    public void EndPlotOffsetPreview(bool redraw = true)
    {
        if (_plotOffsetPreview is not { } preview) return;
        _plotOffsetPreview = null;
        if (redraw) ApplyVisiblePlots();
        Viewer.SetFieldPlaneDragging(preview.Name, false);
    }

    /// <summary>The geometry's extent along <paramref name="axis"/> in DBU — the offset slider's range; null with no scene.
    /// brief-dfr31-3 R-dfr31-3-3 — every drawn object (a drawn air region too), never the air box's faces or edges: the box is
    /// mostly empty margin, so a slider across it spent most of its travel where nothing is.</summary>
    public (long Lo, long Hi)? PlotAxisRange(C3dAxis axis)
    {
        var scene = Viewer.Scene;
        if (scene.Objects.Length == 0) return null;
        var min = new System.Numerics.Vector3(float.MaxValue);
        var max = new System.Numerics.Vector3(float.MinValue);
        foreach (var o in scene.Objects)
            if (o.Kind != Scene3DKind.Boundary) { min = System.Numerics.Vector3.Min(min, o.Min); max = System.Numerics.Vector3.Max(max, o.Max); }
        if (min.X > max.X) (min, max) = (scene.BoundsMin, scene.BoundsMax);
        double perDbu = C3dLowering.Metres(1, Document.DbuPerMicron);
        var (lo, hi) = (scene.ToWorld(min), scene.ToWorld(max));
        (double a, double b) = axis switch { C3dAxis.X => (lo.X, hi.X), C3dAxis.Y => (lo.Y, hi.Y), _ => (lo.Z, hi.Z) };
        return ((long)Math.Round(a / perDbu), (long)Math.Round(b / perDbu));
    }

    // ── the plane in the view (brief-dfr31-3) ────────────────────────────────────────────────

    /// <summary>R-dfr31-3-1 — the plots whose plane is drawn: the selected clip-plane plot, the one whose offset is being dragged,
    /// and any whose <see cref="C3dFieldPlot.ShowPlane"/> keeps it on. Drawn whether or not a run exists: the point is to place
    /// the plane before solving.</summary>
    internal IEnumerable<(C3dFieldPlot Plot, long Offset)> PlotPlanesShown()
    {
        var selected = SelectedFieldPlot;
        foreach (var p in Document.FieldPlots)
        {
            if (p.On != C3dFieldPlotOn.ClipPlane) continue;
            long? dragged = PlotOffsetPreview(p.Name);
            if (p != selected && dragged is null && !p.ShowPlane) continue;
            yield return (p, dragged ?? p.Offset ?? 0);
        }
    }

    /// <summary>The plane of <paramref name="p"/> at <paramref name="offset"/> (DBU) as four world corners spanning the air box —
    /// the scene's bounds when there is no problem to have one — and its label, <c>Y = 120 µm</c>.</summary>
    internal (CircuitRF.Engine.Em3d.Point3[] Corners, string Label) PlotPlane(C3dFieldPlot p, long offset)
    {
        if (FieldSectionLine.Of(p, Document.DbuPerMicron) is { } line) return SectionPlane(p, line);     // brief-dfr34-3
        var scene = Viewer.Scene;
        var (lo, hi) = scene.Problem?.Boundary is { } box
            ? ((box.Min.X, box.Min.Y, box.Min.Z), (box.Max.X, box.Max.Y, box.Max.Z))
            : (scene.ToWorld(scene.BoundsMin), scene.ToWorld(scene.BoundsMax));
        double at = C3dLowering.Metres(offset, Document.DbuPerMicron);
        var axis = p.Axis ?? C3dAxis.Z;
        CircuitRF.Engine.Em3d.Point3 P(double u, double v) => axis switch
        {
            C3dAxis.X => new(at, u, v),
            C3dAxis.Y => new(u, at, v),
            _ => new(u, v, at),
        };
        var ((u0, v0), (u1, v1)) = axis switch
        {
            C3dAxis.X => ((lo.Item2, lo.Item3), (hi.Item2, hi.Item3)),
            C3dAxis.Y => ((lo.Item1, lo.Item3), (hi.Item1, hi.Item3)),
            _ => ((lo.Item1, lo.Item2), (hi.Item1, hi.Item2)),
        };
        string label = $"{axis} = {LayoutUnits.Format(offset, Document.DisplayUnit, Document.DbuPerMicron)} {LayoutUnits.Suffix(Document.DisplayUnit)}";
        return ([P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1)], label);
    }

    private void FillFieldPlaneOverlay(Viewer3DDrawOverlay overlay)
    {
        foreach (var (p, offset) in PlotPlanesShown()) overlay.FieldPlanes.Add(PlotPlane(p, offset));
    }

    /// <summary>R-dfr31-3-2 — the plot whose "Through…" is armed: the next click on an object in the view moves its plane through
    /// that object's centre, and selects nothing.</summary>
    private string? _plotThrough;

    /// <summary>The plot whose "Through…" pick is armed, or null.</summary>
    public string? PlotThroughArmed => _plotThrough;

    /// <summary>Arms (or, for the plot already armed, disarms) the one-shot pick of plot <paramref name="name"/>'s plane.</summary>
    public void ArmPlotThrough(string name)
    {
        if (_plotThrough == name) { CancelPlotThrough(); return; }
        if (FieldPlot(name) is not { On: C3dFieldPlotOn.ClipPlane } p) return;
        if (p.Through is not null) { StatusMessage = $"{name} is a section through two points: Use an Axis first, or make another with Section Through…."; return; }
        _plotThrough = name;
        StatusMessage = $"Click an object: {name}'s plane moves through its centre on {p.Axis ?? C3dAxis.Z}. Esc cancels.";
        OnPropertyChanged(nameof(PlotThroughArmed));
        Properties?.PlotThroughChanged();
    }

    /// <summary>The pick is put away: Esc, another row selected, or the pick made.</summary>
    public void CancelPlotThrough()
    {
        if (_plotThrough is null) return;
        _plotThrough = null;
        StatusMessage = "";
        OnPropertyChanged(nameof(PlotThroughArmed));
        Properties?.PlotThroughChanged();
    }

    /// <summary>R-dfr31-3-2 — the armed plot's plane moved through the centre of scene object <paramref name="id"/> on the plot's
    /// axis: one undo entry, and the pick is put away. Null, or why not (nothing under the cursor, the air box).</summary>
    public string? PlotThroughObject(uint id)
    {
        if (_plotThrough is not { } name || FieldPlot(name) is not { } p) { CancelPlotThrough(); return null; }
        if (Viewer.Scene.Object(id) is not { } o || o.Kind == Scene3DKind.Boundary)
            return $"Click an object to move {name}'s plane through it, or Esc.";
        var axis = p.Axis ?? C3dAxis.Z;
        var (cx, cy, cz) = Viewer.Scene.ToWorld((o.Min + o.Max) / 2);
        double centre = axis switch { C3dAxis.X => cx, C3dAxis.Y => cy, _ => cz };
        long dbu = (long)Math.Round(centre / C3dLowering.Metres(1, Document.DbuPerMicron));
        CancelPlotThrough();
        string at = LayoutUnits.Format(dbu, Document.DisplayUnit, Document.DbuPerMicron);
        SetFieldPlot(name, $"Cut {name} through {o.Name}", x => { x.Axis = axis; x.Offset = dbu; });
        StatusMessage = $"{name} cuts through {o.Name} at {axis} = {at} {LayoutUnits.Suffix(Document.DisplayUnit)}.";
        return null;
    }

    // ── missing data (R-em3d83-5) ────────────────────────────────────────────────────────────

    private readonly Dictionary<(string?, string?), FieldDiscovery> _discoveries = [];
    private readonly Dictionary<string, IReadOnlyList<FieldQuantity>> _offered = new(StringComparer.Ordinal);

    /// <summary>What the plot's setup's runs saved, read once per run (again after a run finishes).</summary>
    internal FieldDiscovery Discovered(FieldPlotRequest request)
    {
        if (IsViewOnly || request.SetupProblem is not null)
            return new FieldDiscovery([], IsViewOnly ? [.. Viewer.FieldSolutions] : [], null, [], null, IsViewOnly && Viewer.FieldsAvailable, null, null);
        var root = ResultsRootProvider?.Invoke();
        var dirs = FieldPlotResolver.RunDirectories(request.RunSetup, root, request.Solver);
        if (_discoveries.TryGetValue(dirs, out var d)) return d;
        return _discoveries[dirs] = FieldPlotResolver.Discover(request.RunSetup, root, Viewer.Scene.Problem, request.Solver);
    }

    /// <summary>A run finished, or the viewer read one: every plot's check reads the runs again.</summary>
    private void ForgetDiscoveries()
    {
        _discoveries.Clear();
        _offered.Clear();
    }

    /// <summary>The quantities <paramref name="item"/>'s files offer (FieldQuantity.Offered), read from the step's headers once.</summary>
    internal IReadOnlyList<FieldQuantity> OfferedQuantities(FieldSolutionItem item)
    {
        string key = (item.Solution.VolumePvtu ?? "") + "|" + (item.Solution.BoundaryPvtu ?? "");
        if (_offered.TryGetValue(key, out var q)) return q;
        try
        {
            var vol = item.Solution.VolumePvtu is { } v ? FieldStep.Open(v, item.Run.ToMetres) : null;
            var bnd = item.Solution.BoundaryPvtu is { } b ? FieldStep.Open(b, item.Run.ToMetres) : null;
            q = FieldQuantity.Offered(vol?.Arrays ?? [], bnd?.Arrays ?? []);
        }
        catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException) { q = []; }
        return _offered[key] = q;
    }

    /// <summary>
    /// Why <paramref name="p"/> draws nothing, or null. A drawn plot's is its layer's in the viewer (it has read the step, so it
    /// knows the quantity too); another's is checked against what its run saved.
    /// </summary>
    public string? FieldPlotProblem(C3dFieldPlot p)
    {
        var request = PlotRequest(p, resolveScene: false);
        if (!p.Hidden && Viewer.FieldLayers.FirstOrDefault(l => l.Name == p.Name) is { Problem: { } now }) return now;
        var found = Discovered(request);
        if (FieldPlotResolver.PlotProblem(request, found.Items, found.Ran, Viewer.Scene.Problem, found.FieldsDeleted) is { } why) return why;
        if (p.Faces.Count > 0 && p.On == C3dFieldPlotOn.Faces && p.Faces.FirstOrDefault(f => SceneFace(f) is null) is { } gone && Viewer.Scene.Objects.Length > 0)
            return $"The face '{gone.Face}' is no longer in the model.";
        return null;
    }

    /// <summary>brief-em3d-100 — why <paramref name="p"/> could not be drawn referred to <paramref name="to"/> at the solution it
    /// shows (Accepted where the run recorded no reflection, or the port accepts nothing), or null. A plot with no solution to show
    /// has nothing to refuse here: its own problem says so.</summary>
    internal string? PlotDriveProblem(C3dFieldPlot p, C3dDriveReferredTo to)
    {
        var request = PlotRequest(p, resolveScene: false) with { DriveReferredTo = to };
        var item = FieldPlotResolver.PickSolution(request.Solution, request.Solver, Discovered(request).Items, Viewer.Scene.Problem, request.Quantity);
        return item is null ? null : FieldPlotResolver.Drive(request, item.Solution).Problem;
    }

    /// <summary>Each plot row's warning glyph and tooltip, without rebuilding the tree.</summary>
    private void RefreshPlotFlags()
    {
        foreach (var row in AllTreeItems().Where(t => t.Kind == FieldPlotKind))
            if (FieldPlot(row.Name) is { } p) row.Refusal = FieldPlotProblem(p);
        Properties?.RefreshPlotProblem();
    }

    // ── the tree (R-em3d83-3) ────────────────────────────────────────────────────────────────

    /// <summary>What the row says after the name: <c>|E| · 10 GHz · clip Z</c>.</summary>
    public string DescribePlot(C3dFieldPlot p)
    {
        string q = FieldPlotResolver.QuantitySymbol(p);
        string on = p.On switch
        {
            C3dFieldPlotOn.ClipPlane => $"clip {p.Axis ?? C3dAxis.Z}",
            C3dFieldPlotOn.Surfaces => p.IsTemperature ? "all faces" : "surfaces",
            _ => $"{p.Faces.Count} face{(p.Faces.Count == 1 ? "" : "s")}",
        };
        string text = $"{q} · {p.Solution?.Describe() ?? "first saved"} · {on}";
        if (!IsViewOnly && p.Setup is { } s && s != ActiveSetupDisplayName) text += $" · {s}";
        return text;
    }

    /// <summary>The Field Plots group, after Probes (where Probes would be), with its <c>+</c>: listed whenever the document has a
    /// plot or a setup to plot, and in a setup's 3D view.</summary>
    private void RebuildFieldPlotTree()
    {
        foreach (var g in Tree.Where(g => g.Role == C3dTreeGroupRole.FieldPlots).ToList())
        {
            DetachExpansion([g]);
            Tree.Remove(g);
        }
        if (Document.FieldPlots.Count == 0 && !HasSetups && !IsViewOnly) return;
        var rows = Document.FieldPlots.Select(p => new C3dTreeItem(this, p.Name, FieldPlotKind, DescribePlot(p), -1, -1, !p.Hidden)
        {
            Refusal = FieldPlotProblem(p), Icon = Material.Icons.MaterialIconKind.Waves,
        }).ToList();
        var group = new C3dTreeGroup("Field Plots", rows, C3dTreeGroupRole.FieldPlots)
        {
            AddCommand = NewFieldPlotCommand,
            AddTip = "New Field Plot…: a field of the active setup's run, drawn, and defined in the Properties Inspector.",
            HeaderTip = IsViewOnly ? SessionPlotsTip : "What the 3D view draws of a run's fields, saved with the document. Up to four are drawn at once.",
        };
        int at = Tree.ToList().FindIndex(g => g.Role is C3dTreeGroupRole.MeshRegions or C3dTreeGroupRole.EffectiveBlocks or
                                                  C3dTreeGroupRole.SymmetryPlanes or C3dTreeGroupRole.ThermalBoundaries);
        if (at < 0) Tree.Add(group); else Tree.Insert(at, group);
        RestoreExpansion();
    }

    /// <summary>A plot row's menu: Show / Hide, Rename…, Duplicate, Delete, New Field Plot…, Properties.</summary>
    private List<Viewer3DMenuItem> FieldPlotMenuItems(C3dTreeItem item)
    {
        string name = item.Name;
        var plot = FieldPlot(name);
        bool shown = plot is { Hidden: false };
        return
        [
            new(name, Enabled: false), Viewer3DMenuItem.Separator,
            new(shown ? "Hide" : "Show", () => SetRowsVisible([item], !shown, $"{(shown ? "Hide" : "Show")} {name}"),
                Tip: shown ? null : "Draw this plot beside the ones drawn (up to four at once)."),
            new("Rename…", () => TextRequested?.Invoke($"Rename {name}", "Name:", name, text => RenameFieldPlot(name, text))),
            new("Duplicate", () => DuplicateFieldPlot(name)),
            Viewer3DMenuItem.Separator,
            new("Delete", () => DeleteFieldPlot(name)),
            Viewer3DMenuItem.Separator,
            new("New Field Plot…", NewFieldPlot),
            SectionThroughItem(),
            new("Properties", () => ShowProperties(rename: false)),
        ];
    }

    /// <summary>A group header's menu: the Field Plots group adds a plot; a material's header (by material) edits the material
    /// (brief-em3d-94).</summary>
    public IReadOnlyList<Viewer3DMenuItem> TreeGroupMenuItems(C3dTreeGroup group)
        => group.Role == C3dTreeGroupRole.FieldPlots ? [new Viewer3DMenuItem("New Field Plot…", NewFieldPlot), SectionThroughItem()]
         : !IsViewOnly && EditMaterialItem(group) is { } edit ? [edit] : [];

    /// <summary>
    /// brief-em3d-90 — the plots' side of SetRowsVisible: hiding hides each named plot; showing ticks them beside the ones drawn,
    /// in list order, until four are drawn (brief-em3d-96 D1) — so Show all with more than four draws the first four and says
    /// which stay hidden, and a single tick with four drawn is refused with the sentence naming them. One entry (inside the
    /// gesture's group).
    /// </summary>
    private void SetPlotsVisible(IReadOnlyList<string> names, bool visible, string description)
    {
        var listed = names.Where(n => FieldPlot(n) is not null).ToList();
        if (listed.Count == 0) return;
        if (visible && listed.Count == 1)
        {
            _plotsNote = SetPlotShown(listed[0], true);
            return;
        }
        var left = new List<string>();
        ChangePlots(listed.Count == 1 ? $"{(visible ? "Show" : "Hide")} {listed[0]}" : description, plots =>
        {
            if (!visible)
            {
                foreach (var p in plots.Where(p => listed.Contains(p.Name))) p.Hidden = true;
                return;
            }
            int drawn = plots.Count(p => !p.Hidden);
            foreach (var p in plots.Where(p => listed.Contains(p.Name) && p.Hidden))
            {
                if (drawn < MaxDrawnPlots) { p.Hidden = false; drawn++; }
                else left.Add(p.Name);
            }
        });
        if (left.Count > 0)
        {
            _plotsNote = $"Four plots are drawn: {string.Join(", ", VisibleFieldPlots.Select(p => p.Name))}. " +
                         $"{string.Join(", ", left)} {(left.Count == 1 ? "stays" : "stay")} hidden — untick one to draw another.";
            foreach (string n in left) SyncPlotRow(n);
        }
    }

    /// <summary>What a tick or Show all left undrawn (D1), said once the whole gesture is done — its other edits clear the status
    /// line as they are pushed.</summary>
    private string? _plotsNote;

    /// <summary>The gesture's <see cref="_plotsNote"/> onto the status line.</summary>
    private void PostPlotsNote()
    {
        if (_plotsNote is not { } note) return;
        _plotsNote = null;
        StatusMessage = note;
    }

    // ── gestures that make or extend a plot ──────────────────────────────────────────────────

    /// <summary>
    /// brief-em3d-82's right-click a face ▸ Plot Field, and Plot Temperature: the face is added to the DRAWN Faces plot of that
    /// kind — or taken off it, or moved to the other side of a sheet — or, with none drawn, a new Faces plot is made with it.
    /// </summary>
    private void PaintFace(Scene3DObject o, int face, int side, bool temperature)
    {
        var entry = new C3dFieldPlotFace
        {
            Face = FaceKey(o, face), Side = side switch { 1 => C3dFieldSide.Top, -1 => C3dFieldSide.Bottom, _ => C3dFieldSide.None },
        };
        // a temperature face joins the drawn plot only when that plot reads the active setup's run: Plot Temperature was
        // offered for the ACTIVE setup, and a face added to a plot pinned to another setup would paint that setup's result.
        // brief-em3d-96 — of several drawn, the focused one when it takes the face, else the first that does.
        bool Takes(C3dFieldPlot t) => t is { On: C3dFieldPlotOn.Faces } && t.IsTemperature == temperature &&
                                      (!temperature || t.Setup is null || t.Setup == ActiveSetupDisplayName);
        if ((VisibleFieldPlot is { } focused && Takes(focused) ? focused : VisibleFieldPlots.FirstOrDefault(Takes)) is { } target)
        {
            string name = target.Name;
            SetFieldPlot(name, $"Plot on {entry.Face}", p =>
            {
                int at = p.Faces.FindIndex(f => f.Face == entry.Face);
                if (at >= 0 && p.Faces[at].Side == entry.Side) p.Faces.RemoveAt(at);
                else if (at >= 0) p.Faces[at] = entry;
                else p.Faces.Add(entry);
            });
            return;
        }
        AddFieldPlot(temperature, p =>
        {
            p.On = C3dFieldPlotOn.Faces;
            p.Faces = [entry];
        }, select: false);
    }

    /// <summary>A thermal plot drawn on <paramref name="on"/> (every exposed face, the clip plane): the first such plot shown,
    /// or a new one; <paramref name="show"/> false hides every such plot.</summary>
    private void ShowTemperaturePlot(C3dFieldPlotOn on, bool show)
    {
        var existing = Document.FieldPlots.FirstOrDefault(p => p.IsTemperature && p.On == on);
        if (!show)
        {
            ChangePlots("Hide the temperature", plots => { foreach (var p in plots.Where(p => p.IsTemperature && p.On == on)) p.Hidden = true; });
            return;
        }
        if (existing is null) AddFieldPlot(true, p => p.On = on, select: false);
        else if (existing.Hidden) SetPlotShown(existing.Name, true);
    }

    /// <summary>R-em3d83-6 — "Other frequency… (needs a re-run)": <paramref name="ghz"/> added to the plot's setup's saved
    /// frequencies and the plot moved to it — ONE undo entry. The plot says it is missing until a run saves it.</summary>
    public string? PlotOtherFrequency(string name, double ghz)
    {
        if (FieldPlot(name) is not { } plot) return $"There is no field plot named '{name}'.";
        if (!(ghz > 0) || double.IsInfinity(ghz)) return "Enter a frequency in GHz, e.g. 6.";
        string? setupName = plot.Setup ?? ActiveSetupName;
        var read = C3dSetups.Read(Document).FirstOrDefault(s => s.Name == setupName);
        if (read is not { Setup: { } setup }) return "Only a setup of this 3D view can be told to save another frequency.";
        if (!setup.Is3D || setup.IsThermal) return "Only a 3D EM setup saves fields by frequency.";
        var solver = plot.Solver ?? setup.Solver3D.Reference;
        if (CircuitRF.Design.Em3d.Em3dSolvers.Get(solver).FieldsRefusal is { } noFields) return noFields;
        var saved = SavedFrequencies(setup, solver);
        if (!saved.Any(f => Math.Abs(f - ghz) <= 1e-9 * Math.Max(f, ghz))) saved.Add(ghz);
        saved.Sort();
        CircuitRF.Design.Em3d.Em3dSolvers.Get(solver).SetSavedFieldsGHz(setup, saved);
        var plotText = C3dPersistence.SerializeFieldPlots(Document.FieldPlots);
        ChangeRecords($"Save the field at {ghz.ToString("G6", CultureInfo.InvariantCulture)} GHz", d =>
        {
            d.Setups[read.Index] = EmSetupPersistence.ToEmbedded(setup);
            var p = d.FieldPlots.First(x => x.Name == name);
            p.Solution = new C3dFieldSolution { GHz = ghz, Port = p.Solution?.Port };
        });
        if (C3dPersistence.SerializeFieldPlots(Document.FieldPlots) != plotText) ApplyVisiblePlots();
        return null;
    }

    /// <summary>The Setup picker's names: this document's setups, and the external <c>.cem</c> shown with it.</summary>
    public IReadOnlyList<string> PlotSetupNames()
        => IsViewOnly ? [] : [.. C3dSetups.Read(Document).Select(s => s.Name), .. ExternalSetup is not null ? [ExternalItemName] : Array.Empty<string>()];

    /// <summary>Whether the plot's Solution picker offers "Other frequency…": its setup is one of this document's driven 3D
    /// setups, whose saved frequencies can be asked for.</summary>
    public bool PlotCanAddFrequency(C3dFieldPlot p)
        => !IsViewOnly && C3dSetups.Read(Document).FirstOrDefault(s => s.Name == (p.Setup ?? ActiveSetupName))?.Setup is
               { Is3D: true, IsThermal: false, Problem3D: CircuitRF.Engine.Em3d.Em3dProblemType.Driven } setup
           && CircuitRF.Design.Em3d.Em3dSolvers.Get(p.Solver ?? setup.Solver3D.Reference).Capabilities.Fields;

    /// <summary>Where a plot switched to the clip plane cuts: the view's clip plane, or z through the middle.</summary>
    public (C3dAxis Axis, long Offset) PlotClipDefault() => ClipOfView();

    /// <summary>The frequencies a setup's solver saves fields at now, GHz: its list, or the sweep's centre it defaults to.</summary>
    public static List<double> SavedFrequencies(EmSetup setup, Em3dSolverId solver)
    {
        var list = CircuitRF.Design.Em3d.Em3dSolvers.Get(solver).SavedFieldsGHz(setup);
        if (list is not null) return [.. list];
        return CircuitRF.Design.Em3d.PalaceConfigWriter.SweepCentreGHz(setup) is { } c ? [c] : [];
    }
}

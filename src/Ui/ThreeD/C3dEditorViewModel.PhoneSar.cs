// brief-sar-12 — the phone SAR workflow in the 3D editor: Place Against Phantom… and Band Sweep….
//
// Every decision is src/Design's: C3dDevicePlacement places (one rigid motion written into ordinary placements) and measures;
// C3dBandSweeps makes each band's setup and reads its table. This file pushes one undo entry per edit and keeps the one piece
// of session state the view needs: which objects were last placed against which face, so the separation can be drawn as a
// dimension and measured again whenever the document changes.

using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The objects last placed, and the phantom face they were placed against: what the dimension measures.</summary>
    private (IReadOnlyList<string> Movers, string Phantom, string Face)? _placed;

    /// <summary>The view asks for the placement dialog.</summary>
    public Func<DevicePlacementDialogViewModel, Task>? ShowPlacementDialog { get; set; }

    /// <summary>The view asks for the band sweep dialog.</summary>
    public Func<BandSweepDialogViewModel, Task>? ShowBandSweepDialog { get; set; }

    /// <summary>The phantoms of the document, by name.</summary>
    public IReadOnlyList<string> PhantomNames() => [.. Document.Objects.OfType<C3dPhantom>().Select(p => p.Name)];

    /// <summary>The selection as the names the placement moves: each selected object's, and each selected instance's.</summary>
    public IReadOnlyList<string> SelectedMovers()
        => [.. Targets().Select(t => t.Instance ? Document.Instances[t.Index].Name : Document.Objects[t.Index].Name).Distinct()];

    /// <summary>Why Place Against Phantom… cannot open now, or null.</summary>
    public string? PlacementRefusal()
    {
        if (IsViewOnly) return "This 3D view is read-only.";
        if (PhantomNames().Count == 0) return "This 3D view has no phantom to place against: add one (3D ▸ Draw ▸ Phantom).";
        if (SelectedMovers().Count == 0) return "Select the antenna or device to place, then Place Against Phantom….";
        return null;
    }

    /// <summary>3D ▸ Draw ▸ Phantom ▸ Place Against Phantom…: the dialog, on the selection.</summary>
    public async Task OpenPlacementAsync()
    {
        if (PlacementRefusal() is { } why) { StatusMessage = why; return; }
        var vm = new DevicePlacementDialogViewModel(this, SelectedMovers());
        if (ShowPlacementDialog is { } show) await show(vm);
    }

    /// <summary>The dialog's Place: the objects against the face (C3dDevicePlacement.Place), one undo entry, and the separation
    /// drawn. Null, or why not.</summary>
    public string? PlaceAgainstPhantom(C3dPlacementRequest request)
    {
        if (IsViewOnly) return "This 3D view is read-only.";
        if (Elaboration is not { } e) return "The 3D view has not been built yet; try again in a moment.";
        var working = C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));
        if (C3dDevicePlacement.Place(working, e, request) is { } why) return why;
        var before = C3dListsEdit.Of(Document);
        var after = C3dListsEdit.Of(working);
        string what = request.Movers.Count == 1 ? request.Movers[0] : $"{request.Movers.Count} objects";
        // Before the edit: the scene it rebuilds measures the dimension, and may adopt before Push returns.
        _placed = (request.Movers, request.Phantom, request.Face);
        Push(new C3dListsEdit($"Place {what} against {request.Phantom} {request.Face}", before, after, ApplyLists));
        StatusMessage = $"Placed {what} {Spell(request.Separation)} from {request.Phantom}'s {request.Face}" +
                        (request.TiltDeg != 0 ? $", tilted {request.TiltDeg:0.##}° about {request.TiltAxis.ToString().ToLowerInvariant()}." : ".");
        return null;
    }

    /// <summary>The faces of the phantom named <paramref name="phantom"/>.</summary>
    public IReadOnlyList<string> PhantomFaces(string phantom)
        => Document.Objects.FirstOrDefault(o => o.Name == phantom) is C3dPhantom p ? [.. C3dDevicePlacement.Faces(p).Select(f => f.Name)] : [];

    /// <summary>The separation of the last placement, measured again in the adopted elaboration, as the view's dimension.</summary>
    private void RefreshPlacementDimension()
    {
        if (_placed is not { } p || Elaboration is not { } e || C3dDevicePlacement.Measure(Document, e, p.Movers, p.Phantom, p.Face) is not { } s)
        {
            Viewer.SetDimension(null);
            return;
        }
        double m = 1e-6 / Document.DbuPerMicron;
        Viewer.SetDimension(new Viewer3DDimension(new Viewer3DMeasurePoint(s.Nearest.X * m, s.Nearest.Y * m, s.Nearest.Z * m, false),
                                                  new Viewer3DMeasurePoint(s.Foot.X * m, s.Foot.Y * m, s.Foot.Z * m, false),
                                                  Spell((long)Math.Round(s.Distance))));
    }

    // ── band sweeps (R-sar12-3) ──────────────────────────────────────────────────────────────

    /// <summary>The document's band sweeps.</summary>
    public IReadOnlyList<C3dBandSweep> BandSweeps => Document.BandSweeps ?? [];

    /// <summary>The setups a band sweep may take as its template: every driven openEMS setup.</summary>
    public IReadOnlyList<string> BandTemplates()
        => [.. C3dSetups.Read(Document).Where(s => s.Setup is { Is3D: true, Problem3D: CircuitRF.Engine.Em3d.Em3dProblemType.Driven } st
                                                   && CircuitRF.Design.Em3d.Em3dSolvers.Any(st.Solver3D, c => c.Sar))
                                       .Select(s => s.Name)];

    /// <summary>3D ▸ Draw ▸ Phantom ▸ Band Sweep…: the dialog.</summary>
    public async Task OpenBandSweepAsync()
    {
        if (IsViewOnly) { StatusMessage = "This 3D view is read-only."; return; }
        if (BandTemplates().Count == 0) { StatusMessage = "A band sweep starts from a driven openEMS setup; add one first."; return; }
        var vm = new BandSweepDialogViewModel(this);
        if (ShowBandSweepDialog is { } show) await show(vm);
    }

    /// <summary>Writes <paramref name="sweep"/> into the document (replacing one of its name) and makes its setups — one undo entry.
    /// Their names, or why not; nothing changes on a refusal.</summary>
    public (IReadOnlyList<string> Setups, string? Refusal) SaveBandSweep(C3dBandSweep sweep)
    {
        if (IsViewOnly) return ([], "This 3D view is read-only.");
        var probe = C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));
        Put(probe, sweep);
        var (setups, why) = C3dBandSweeps.Create(probe, sweep);
        if (why is not null) return ([], why);
        ChangeRecords($"Band sweep {sweep.Name}", doc =>
        {
            Put(doc, sweep);
            C3dBandSweeps.Create(doc, sweep);
        });
        return (setups, null);
    }

    private static void Put(C3dDocument doc, C3dBandSweep sweep)
    {
        var list = doc.BandSweeps ?? [];
        int at = list.FindIndex(b => b.Name == sweep.Name);
        if (at >= 0) list[at] = sweep; else list.Add(sweep);
        doc.BandSweeps = list;
    }

    /// <summary>Runs every band's setup in turn, each as Simulate runs it.</summary>
    public async Task RunBandSweepAsync(IReadOnlyList<string> setups)
    {
        foreach (string s in setups)
        {
            if (RunRequested is not { } run) return;
            await run(this, s);
        }
    }

    /// <summary>The band table of <paramref name="sweep"/>, from the runs in this workspace's results.</summary>
    public IReadOnlyList<C3dBandSarRow> BandTable(C3dBandSweep sweep)
        => ResultsRootProvider?.Invoke() is { } root ? C3dBandSweeps.Table(Document, sweep, RunPath, root) : [];
}

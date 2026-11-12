// brief-hzn-12 (follow-up, owner's choices) — the 3D view's plane waves in the editor. They are records of the view, as its ports
// are: the Object Tree's Excitations group lists every one (a disabled one greyed, as a port that is not modelled is); at most one is
// enabled, and it drives every Driven setup of the view and is the one the view draws. Add Plane Wave on the canvas's menu adds a new
// one and enables it (the one enabled before is disabled, never deleted); Add Plane Wave Onto Face aims it straight into a flat face,
// its phase zero on that face. Its row and its picture select it; the Properties Inspector edits its name, whether it is enabled and
// its values. Every edit is one undo entry.

using System.Globalization;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public partial class C3dEditorViewModel
{
    /// <summary>The kind of a plane wave's tree row.</summary>
    public const string PlaneWaveKind = "Plane wave";

    /// <summary>The prefix of a plane wave's row name (its display name is the wave's own).</summary>
    public const string PlaneWaveRowPrefix = "planewave/";

    /// <summary>The wave driving the active setup: the view's enabled one, when the active setup is a Driven 3D setup.</summary>
    public EmPlaneWave3D? ActivePlaneWave => !IsExternalActive && ActiveSetup is { IsScattering3D: true } s ? s.PlaneWave : null;

    /// <summary>The plane wave a row names, or null.</summary>
    public C3dPlaneWave? PlaneWaveOfRow(C3dTreeItem row)
        => row.Kind == PlaneWaveKind ? Document.PlaneWaves.FirstOrDefault(w => PlaneWaveRowPrefix + w.Name == row.Name) : null;

    /// <summary>The plane wave the selection is: its row, or the drawn wave's picture.</summary>
    public C3dPlaneWave? SelectedPlaneWave()
        => SelectedTreeItem is { Kind: PlaneWaveKind } row ? PlaneWaveOfRow(row)
         : Viewer.SelectedObjects() is { Count: > 0 } o && o.All(x => x.Kind == Scene3DKind.Excitation) ? C3dPlaneWaves.Enabled(Document)
         : null;

    /// <summary>The Excitations group: every plane wave of the view, after the ports' groups.</summary>
    private void RebuildPlaneWaveItem()
    {
        foreach (var g in Tree.Where(g => g.Role == C3dTreeGroupRole.Excitations).ToList())
        {
            DetachExpansion([g]);
            Tree.Remove(g);
        }
        if (Document.PlaneWaves.Count == 0) return;
        var rows = Document.PlaneWaves.Select(w => new C3dTreeItem(this, PlaneWaveRowPrefix + w.Name, PlaneWaveKind,
                                                                   w.Enabled ? "enabled" : "disabled", -1, -1, true)
                                       { IsReadOnly = true, DisplayName = w.Name, IsModelled = w.Enabled }).ToList();
        var group = new C3dTreeGroup("Excitations", rows, C3dTreeGroupRole.Excitations);
        int after = Tree.ToList().FindLastIndex(g => g.Role is C3dTreeGroupRole.Ports or C3dTreeGroupRole.ComponentPorts);
        if (after >= 0) Tree.Insert(after + 1, group);
        else Tree.Add(group);
    }

    /// <summary>The row of the drawn (enabled) wave's picture.</summary>
    private C3dTreeItem? PlaneWaveRowOf(Scene3DObject o)
        => o.Kind == Scene3DKind.Excitation && C3dPlaneWaves.Enabled(Document) is { } w
            ? AllTreeItems().FirstOrDefault(t => t.Kind == PlaneWaveKind && t.Name == PlaneWaveRowPrefix + w.Name) : null;

    /// <summary>The picture a wave's row selects: the drawn one, for the enabled wave only.</summary>
    private IEnumerable<Scene3DObject> PlaneWaveSceneObjects(C3dTreeItem row)
        => PlaneWaveOfRow(row) is { Enabled: true } ? new[] { SceneObject(Scene3DBuilder.PlaneWaveName) }.OfType<Scene3DObject>() : [];

    /// <summary>Why the active setup cannot be driven by a plane wave (its solver has no plane-wave source), or null.</summary>
    private string? PlaneWaveCannot()
        => ActiveSetup is { } s && !IsExternalActive ? Em3dPlaneWaves.SolverCannot(s.Solver3D) : null;

    /// <summary>Add Plane Wave: a new wave of the view, enabled (the one enabled before is disabled); with no setup, a new one on a solver
    /// that runs a plane wave. Null on success, else why not.</summary>
    public string? AddPlaneWave(Point3? direction = null, Point3? eField = null, Point3? originUm = null)
    {
        if (!IsEditable) return "This 3D view is read-only.";
        if (PlaneWaveCannot() is { } cannot) { StatusMessage = cannot; return cannot; }
        var k = Round(direction ?? new Point3(0, 0, -1));
        var e = Round(eField ?? PerpendicularTo(k));
        string name = C3dPlaneWaves.NewName(Document);
        var record = C3dPlaneWaves.FromSetup(new EmPlaneWave3D(k, e, PhaseOriginUm: originUm is { } o ? Round(o) : null), name, true, Document.DbuPerMicron);
        string? was = C3dPlaneWaves.Enabled(Document)?.Name;
        string? setupName = null;
        EmSetup? setup = null;
        if (ActiveSetup is null && !IsExternalActive)
        {
            var names = C3dSetups.Read(Document).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            setupName = Enumerable.Range(1, 10_000).Select(n => $"S{n}").First(n => !names.Contains(n));
            var preferred = CircuitRF.Ui.Layout.Em.EmSolverPreference.ThreeD;
            var solver = Em3dSolvers.Get(preferred).Capabilities.PlaneWave ? preferred : Em3dSolvers.With(c => c.PlaneWave)[0].Id;
            setup = new EmSetup { Name = setupName, Solver3D = Em3dSolverChoice.One(solver), RobustMeshing = true, Frequency = EmSetup.NewSetupFrequency(),
                                  AirBox = EmSetupPersistence.FromCem(Document.AirBox) }.WithNewSetupSolverDefaults();
        }
        ChangeRecords($"Add plane wave {name}", d =>
        {
            foreach (var w in d.PlaneWaves) w.Enabled = false;
            d.PlaneWaves.Add(record);
            if (setup is not null) d.Setups.Add(EmSetupPersistence.ToEmbedded(setup));
        });
        if (setupName is not null) SetActiveSetup(setupName);
        StatusMessage = PlaneWaveStatus(name, was);
        SelectPlaneWaveRow(name);
        return null;
    }

    /// <summary>What adding or enabling a wave says: which wave it replaced, and ports that would refuse the run.</summary>
    private string PlaneWaveStatus(string name, string? replaced)
    {
        var said = new List<string> { $"Plane wave '{name}' drives this 3D view's Driven setups: their result is a radar cross-section." };
        if (replaced is not null && replaced != name) said.Add($"'{replaced}' is disabled (at most one plane wave is enabled).");
        int ports = Document.Ports.Count(p => p.Model);
        if (ports > 0) said.Add($"Turn off the Model of the 3D view's {ports} port(s): a plane wave runs with no port beside it.");
        return string.Join(" ", said);
    }

    /// <summary>Add Plane Wave Onto Face: travelling into the selected flat face, along its inward normal, its phase zero at the face's centre.</summary>
    public string? AddPlaneWaveOntoFace(uint objectId, int face)
    {
        var (_, normal) = Scene3DFaces.AreaAndNormal(Viewer.Scene, objectId, face);
        if (normal is not { } n) return "The face is curved, so it has no one direction to aim the wave along: pick a flat face.";
        var centre = FaceCentreOf(objectId, face);
        return AddPlaneWave(new Point3(-n.X, -n.Y, -n.Z), originUm: centre is { } c ? new Point3(c.X * 1e6, c.Y * 1e6, c.Z * 1e6) : null);
    }

    /// <summary>Writes wave <paramref name="name"/>'s values (its name and flag kept). Null on success, else why not.</summary>
    public string? SetPlaneWaveValues(string name, EmPlaneWave3D values)
    {
        int i = Document.PlaneWaves.FindIndex(w => w.Name == name);
        if (i < 0) return $"There is no plane wave '{name}'.";
        if (values.Resolve(out string? why) is null) return $"The plane wave cannot be solved: {why}.";
        var record = C3dPlaneWaves.FromSetup(values, name, Document.PlaneWaves[i].Enabled, Document.DbuPerMicron);
        ChangeRecords($"Change plane wave {name}", d => d.PlaneWaves[i] = record);
        return null;
    }

    /// <summary>Renames wave <paramref name="name"/>. Null on success, else why not.</summary>
    public string? RenamePlaneWave(string name, string newName)
    {
        newName = newName.Trim();
        if (newName == name) return null;
        if (newName.Length == 0) return "A plane wave has a name.";
        if (Document.PlaneWaves.Any(w => w.Name == newName)) return $"Another plane wave is named '{newName}'.";
        int i = Document.PlaneWaves.FindIndex(w => w.Name == name);
        if (i < 0) return $"There is no plane wave '{name}'.";
        ChangeRecords($"Rename plane wave {name} to {newName}", d => d.PlaneWaves[i].Name = newName);
        SelectPlaneWaveRow(newName);
        return null;
    }

    /// <summary>Enables wave <paramref name="name"/> (every other one is disabled) or disables it. Null on success, else why not.</summary>
    public string? SetPlaneWaveEnabled(string name, bool enabled)
    {
        int i = Document.PlaneWaves.FindIndex(w => w.Name == name);
        if (i < 0) return $"There is no plane wave '{name}'.";
        if (Document.PlaneWaves[i].Enabled == enabled) return null;
        if (enabled && PlaneWaveCannot() is { } cannot) return cannot;
        string? was = C3dPlaneWaves.Enabled(Document)?.Name;
        ChangeRecords($"{(enabled ? "Enable" : "Disable")} plane wave {name}", d =>
        {
            if (enabled) foreach (var w in d.PlaneWaves) w.Enabled = false;
            d.PlaneWaves[i].Enabled = enabled;
        });
        StatusMessage = enabled ? PlaneWaveStatus(name, was) : $"Plane wave '{name}' is disabled: the 3D view's Driven setups are driven by their ports.";
        SelectPlaneWaveRow(name);
        return null;
    }

    /// <summary>
    /// Move… on a wave: the view's Move (base point, then target, snapped; X/Y/Z locks an axis; a typed distance) on its picture, as
    /// on a port. The commit moves the wave's phase origin by the difference, and the picture follows it: the wave is drawn arriving
    /// along the line through its phase origin. Only the enabled wave is drawn, so only it moves this way; the Inspector's Phase
    /// origin moves any wave.
    /// </summary>
    public void StartPlaneWaveMove(string name, Operations.C3dMoveLock lockTo = Operations.C3dMoveLock.None)
    {
        if (Document.PlaneWaves.FirstOrDefault(w => w.Name == name) is not { } wave) return;
        if (PlaneWaveMoveCannot(wave) is { } why) { StatusMessage = why; return; }
        var glyph = SceneObject(Scene3DBuilder.PlaneWaveName)!;
        _portMoveTargets = [];
        var tool = new Operations.MoveTool(this, _portMoveTargets, wave.PhaseOrigin, lockTo: lockTo) { PlaneWave = name };
        if (Viewer.LastPick.Object == glyph.Id && FreePoint(CursorInput(), out _) is { } p)
            tool.SetBase(p, !Viewer.Snap.IsSnap || ToDocumentPoint(Viewer.Snap).Exact);
        BeginOperation(tool);
    }

    /// <summary>Why Move… cannot move <paramref name="w"/> on the canvas, or null when it can.</summary>
    private string? PlaneWaveMoveCannot(C3dPlaneWave w)
        => IsViewOnly || !IsEditable ? "This 3D view is read-only."
         : !w.Enabled ? $"Plane wave '{w.Name}' is disabled, so it is not drawn: enable it to move it here, or type its Phase origin in the Properties."
         : SceneObject(Scene3DBuilder.PlaneWaveName) is null
             ? "The wave is drawn only while the active setup is a Driven 3D setup: make one active to move it here, or type its Phase origin in the Properties."
         : null;

    /// <summary>Moves wave <paramref name="name"/>'s phase origin by <paramref name="d"/> (DBU): one undo entry.</summary>
    public bool MovePlaneWave(string name, C3dPoint3 d, bool exact = true)
    {
        int i = Document.PlaneWaves.FindIndex(w => w.Name == name);
        if (i < 0) return false;
        bool changed = ChangeRecords($"Move plane wave {name}", doc => doc.PlaneWaves[i].PhaseOrigin = doc.PlaneWaves[i].PhaseOrigin + d);
        StatusMessage = changed ? $"Move plane wave '{name}'." + (exact ? "" : " ≈ Not a whole number of database units: rounded to the nearest one.")
                                : $"Move plane wave '{name}': nothing moved.";
        if (changed) OperationCommits++;
        SelectPlaneWaveRow(name);
        return changed;
    }

    /// <summary>Removes the waves <paramref name="names"/> name, as one entry.</summary>
    public void RemovePlaneWaves(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0) return;
        ChangeRecords(names.Count == 1 ? $"Remove plane wave {names.First()}" : $"Remove {names.Count} plane waves",
                      d => d.PlaneWaves.RemoveAll(w => names.Contains(w.Name)));
        if (C3dPlaneWaves.Enabled(Document) is null) StatusMessage = "No plane wave is enabled: the 3D view's Driven setups are driven by their ports.";
    }

    /// <summary>brief-hzn-12 — a setup changed to a solver with no plane-wave source (Palace) disables the view's enabled wave in the
    /// same entry, so that setup is driven by its ports; the sentence it says, or null when nothing was disabled.</summary>
    private static string? DisablePlaneWaveFor(EmSetup before, EmSetup after, C3dDocument d)
    {
        if (after.IsThermal || after.Problem3D != Em3dProblemType.Driven) return null;
        if (Em3dPlaneWaves.SolverCannot(after.Solver3D) is not { } why || Em3dPlaneWaves.SolverCannot(before.Solver3D) is not null) return null;
        if (C3dPlaneWaves.Enabled(d) is not { } w) return null;
        w.Enabled = false;
        return $"Plane wave '{w.Name}' was disabled, so the 3D view's Driven setups are driven by their ports: {why}";
    }

    /// <summary>The area-weighted centre of face <paramref name="face"/> of scene object <paramref name="objectId"/>, world metres.</summary>
    private Point3? FaceCentreOf(uint objectId, int face)
    {
        double area = 0;
        var sum = System.Numerics.Vector3.Zero;
        foreach (var (a, b, c) in Scene3DFaces.Triangles(Viewer.Scene, objectId, face))
        {
            float t = 0.5f * System.Numerics.Vector3.Cross(b - a, c - a).Length();
            area += t;
            sum += t * (a + b + c) / 3;
        }
        if (!(area > 0)) return null;
        var (x, y, z) = Viewer.Scene.ToWorld(sum / (float)area);
        return new Point3(x, y, z);
    }

    internal void SelectPlaneWaveRow(string name)
    {
        if (AllTreeItems().FirstOrDefault(t => t.Kind == PlaneWaveKind && t.Name == PlaneWaveRowPrefix + name) is { } row)
        {
            SelectedTreeItem = row;
            Properties.Reload();
        }
    }

    /// <summary>The canvas's items: Add Plane Wave, and Onto Face with one flat face selected.</summary>
    private IEnumerable<Viewer3DMenuItem> PlaneWaveMenuItems()
    {
        if (!IsEditable || IsViewOnly) yield break;
        string? cannot = PlaneWaveCannot();
        string tip = "A new plane wave of this 3D view, enabled: it drives the view's Driven setups, whose result is a radar cross-section" +
                     (C3dPlaneWaves.Enabled(Document) is { } w ? $" ('{w.Name}' is disabled)" : "") + ". Its row is under Excitations in the Object Tree.";
        if (Viewer.SelectMode == Scene3DSelectMode.Face && Viewer.Selection is [{ Face: >= 0 } f])
        {
            var (_, normal) = Scene3DFaces.AreaAndNormal(Viewer.Scene, f.Object, f.Face);
            yield return new Viewer3DMenuItem("Add Plane Wave Onto Face",
                () => { if (AddPlaneWaveOntoFace(f.Object, f.Face) is { } why) StatusMessage = why; },
                Enabled: normal is not null && cannot is null,
                Tip: cannot ?? (normal is null ? "The face is curved: pick a flat face to aim the wave into." : "Travelling straight into this face. " + tip));
        }
        yield return new Viewer3DMenuItem("Add Plane Wave", () => AddPlaneWave(), Enabled: cannot is null, Tip: cannot ?? tip);
    }

    /// <summary>A wave's own menu, on its row and on its picture: Properties, Rename, Enable or Disable, Remove.</summary>
    public IReadOnlyList<Viewer3DMenuItem> ExcitationMenuItems() => ExcitationMenuItems(SelectedPlaneWave());

    /// <summary>Wave <paramref name="wave"/>'s menu: the row's own, whichever row is selected.</summary>
    public IReadOnlyList<Viewer3DMenuItem> ExcitationMenuItems(C3dPlaneWave? wave)
    {
        if (wave is not { } w) return [];
        string? cannot = !w.Enabled ? PlaneWaveCannot() : null;
        return
        [
            new Viewer3DMenuItem("Properties", () => { SelectPlaneWaveRow(w.Name); ShowProperties(rename: false); }),
            new Viewer3DMenuItem("Rename…", () => { SelectPlaneWaveRow(w.Name); ShowProperties(rename: true); }, Enabled: IsEditable),
            new Viewer3DMenuItem("Move…", () => StartPlaneWaveMove(w.Name), Enabled: PlaneWaveMoveCannot(w) is null,
                                 Gesture: Viewer3DMenuItem.Plain(Avalonia.Input.Key.G),
                                 Tip: PlaneWaveMoveCannot(w) ?? "Click a base point, then the target point: the wave's phase origin, where it is drawn, moves " +
                                      "by the difference. X, Y or Z holds it to an axis; type a distance."),
            new Viewer3DMenuItem("Move Along", Enabled: PlaneWaveMoveCannot(w) is null, Tip: PlaneWaveMoveCannot(w), Children:
            [
                new("X", () => StartPlaneWaveMove(w.Name, Operations.C3dMoveLock.AxisX)),
                new("Y", () => StartPlaneWaveMove(w.Name, Operations.C3dMoveLock.AxisY)),
                new("Z", () => StartPlaneWaveMove(w.Name, Operations.C3dMoveLock.AxisZ)),
            ]),
            Viewer3DMenuItem.Separator,
            new Viewer3DMenuItem(w.Enabled ? "Disable" : "Enable", () => { if (SetPlaneWaveEnabled(w.Name, !w.Enabled) is { } why) StatusMessage = why; },
                                 Enabled: IsEditable && cannot is null,
                                 Tip: cannot ?? (w.Enabled ? "Drive the view's setups by their ports again." : "Drive the view's Driven setups by this wave (the enabled one is disabled).")),
            new Viewer3DMenuItem("Remove", () => RemovePlaneWaves([w.Name]), Enabled: IsEditable),
        ];
    }

    /// <summary>Delete on waves (their rows, or the drawn one's picture) removes them; true when that is what was selected.</summary>
    private bool DeletePlaneWaveIfSelected()
    {
        var rows = SelectedTreeItems.Where(r => r.Kind == PlaneWaveKind).ToList();
        if (rows.Count > 0 && rows.Count == SelectedTreeItems.Count)
        {
            RemovePlaneWaves([.. rows.Select(PlaneWaveOfRow).OfType<C3dPlaneWave>().Select(w => w.Name)]);
            return true;
        }
        if (Viewer.SelectedObjects() is { Count: > 0 } o && o.All(x => x.Kind == Scene3DKind.Excitation) && C3dPlaneWaves.Enabled(Document) is { } enabled)
        {
            RemovePlaneWaves([enabled.Name]);
            return true;
        }
        return false;
    }

    /// <summary>A unit vector at right angles to <paramref name="k"/>: the axis least along it, with k's part taken out.</summary>
    internal static Point3 PerpendicularTo(Point3 k)
    {
        double n = Math.Sqrt(k.X * k.X + k.Y * k.Y + k.Z * k.Z);
        if (!(n > 0)) return new Point3(1, 0, 0);
        var u = new Point3(k.X / n, k.Y / n, k.Z / n);
        Point3[] axes = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
        var a = axes.MinBy(x => Math.Abs(x.X * u.X + x.Y * u.Y + x.Z * u.Z));
        double d = a.X * u.X + a.Y * u.Y + a.Z * u.Z;
        var e = new Point3(a.X - d * u.X, a.Y - d * u.Y, a.Z - d * u.Z);
        double m = Math.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z);
        return new Point3(e.X / m, e.Y / m, e.Z / m);
    }

    /// <summary>A vector as the panel shows it: −0 and float dust gone, so a face's normal reads (0, 0, −1).</summary>
    private static Point3 Round(Point3 p)
    {
        static double R(double v) { double r = Math.Round(v, 9); return r == 0 ? 0 : r; }
        return new Point3(R(p.X), R(p.Y), R(p.Z));
    }

    /// <summary>A vector as the Inspector shows and reads it: three numbers.</summary>
    internal static string VectorText(Point3 p)
        => string.Join(", ", new[] { p.X, p.Y, p.Z }.Select(v => v.ToString("0.#########", CultureInfo.InvariantCulture)));
}

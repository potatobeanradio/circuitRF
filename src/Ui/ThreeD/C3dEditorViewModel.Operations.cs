// brief-em3d-46 — object operations in the 3D editor: Move (G, and along an axis), Rotate (R, and the quarter-turn
// quick items), Mirror, Duplicate (Ctrl/Cmd+D), Array…, Align, Order, and the move gizmo; and the editor's side of
// Measure (the point a click takes).
//
// THE SELECTION IS WHAT THEY ACT ON — in Object mode, objects and instances alike: an instance moves, rotates,
// mirrors, duplicates and arrays by its placement exactly as an object does, and its child is never elaborated
// again for it (the elaborator's child cache is keyed by the child, not by where it is placed — gate 2).
//
// A DRAG IS A PREVIEW AND COMMITS ONCE (R-em3d46-1a, em-3d.md §8.2 point 3). While Move, Rotate, Duplicate, Array or
// a gizmo drag runs, the document is not touched: the targets' batches draw under per-batch transforms
// (Scene3DPreview), and each mouse move changes those transforms only — 64 bytes a batch. On the commit the
// document changes once, the moved objects re-elaborate, and ONE undo entry is written. On Esc nothing changes and
// the transforms go back to the identity. The preview is held until the committed scene is adopted, so the moved
// objects never flick back to where they were for a frame.
//
// A ROTATION OR A MIRROR IS COMPOSED INTO THE PLACEMENT and canonicalised (C3dPlacement.Canonical); a move changes
// only the origin, so a hand-written rotation list survives a move verbatim.

using System.Numerics;
using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    // ── what an operation acts on ─────────────────────────────────────────────────────────────

    private (Scene3DItem[] Selection, long Generation, IReadOnlyList<C3dTarget> Targets)? _targetsCache;

    /// <summary>
    /// The selection's objects and instances, in selection order, each once — Object mode only (a face or a vertex is
    /// brief 47's). An instance's part selects its instance: the instance is what has a placement here.
    /// </summary>
    public IReadOnlyList<C3dTarget> Targets()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Object) return [];
        var sel = Viewer.View.Selection;
        if (_targetsCache is { } c && ReferenceEquals(c.Selection, sel) && c.Generation == AdoptedGeneration) return c.Targets;
        var list = new List<C3dTarget>();
        foreach (var o in Viewer.SelectedObjects())
        {
            C3dTarget? t = InstanceOf(o) is { } path
                ? Document.Instances.FindIndex(i => i.Name == path.Split('/', '[')[0]) is int ii and >= 0 ? new C3dTarget(true, ii) : null
                : EditableIndex(o) is int oi and >= 0 ? new C3dTarget(false, oi) : null;
            if (t is { } tt && !list.Contains(tt)) list.Add(tt);
        }
        _targetsCache = (sel, AdoptedGeneration, list);
        return list;
    }

    private string NameOf(C3dTarget t) => t.Instance ? Document.Instances[t.Index].Name : ObjectLabel(t.Index);

    private C3dPlacement PlacementOf(C3dTarget t) => t.Instance ? Document.Instances[t.Index].Placement : ObjectAt(t.Index)!.Placement;

    /// <summary>The scene objects a target became: an object's one, an instance's every part (of every array element).</summary>
    private IEnumerable<Scene3DObject> SceneObjectsOf(C3dTarget t)
    {
        // brief-em3d-66 — an entered operand is drawn under its own scene name.
        if (!t.Instance && OperandSceneName(t.Index) is { } scene) return SceneObject(scene) is { } s ? [s] : [];
        string name = NameOf(t);
        // brief-em3d-50 — a wire is its sweep and its balls. A Boolean is also every Tool it draws as an object of its own (a
        // kept Tool, a disabled operation's Tool): they move with it on the commit, so its preview moves them too — Select All
        // then a gizmo drag left a kept bore standing where it was until the drop.
        if (!t.Instance) return SceneObjectsFor(Document.Objects[t.Index], balls: true).Concat(StandaloneToolsOf(Document.Objects[t.Index].Name));
        return Viewer.Scene.Objects.Where(o => InstanceOf(o) is { } p && (p == name || p.StartsWith(name + "/", StringComparison.Ordinal)
                                                                          || p.StartsWith(name + "[", StringComparison.Ordinal)));
    }

    private (C3dElaboration Of, ILookup<string, string> Names)? _standaloneTools;

    /// <summary>The scene objects the document's top-level object <paramref name="name"/> draws besides itself: the solids the
    /// elaboration records it as the <see cref="C3dProvenance.TopObject"/> of. Looked up once per elaboration.</summary>
    private IEnumerable<Scene3DObject> StandaloneToolsOf(string name)
    {
        if (Elaboration is not { } e) return [];
        if (_standaloneTools is not { } c || !ReferenceEquals(c.Of, e))
        {
            var names = e.Provenance.Where(kv => kv.Value is { TopObject: { } top, InstancePath.Length: 0 } && top != kv.Key)
                                    .ToLookup(kv => kv.Value.TopObject!, kv => kv.Key, StringComparer.Ordinal);
            _standaloneTools = c = (e, names);
        }
        return c.Names[name].Select(SceneObject).OfType<Scene3DObject>();
    }

    /// <summary>
    /// The targets' bounds in DBU, from the ELABORATION (the geometry the solver gets): whole DBU where the lowering
    /// is exact, and whether every face of the box was. Null when nothing of them was elaborated.
    /// </summary>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1, bool Exact)? BoundsDbu(IEnumerable<C3dTarget> targets)
    {
        if (Elaboration is not { } e) return null;
        var names = new List<string>();
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in targets)
        {
            if (t.Instance) roots.Add(NameOf(t));
            else names.Add(ElaboratedName(t.Index));
        }
        bool Mine(string n) => names.Contains(n) || roots.Contains(n.Split('/', '[')[0]) && n.Contains('/');
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        void Grow((double, double, double, double, double, double) b)
        {
            x0 = Math.Min(x0, b.Item1); y0 = Math.Min(y0, b.Item2); z0 = Math.Min(z0, b.Item3);
            x1 = Math.Max(x1, b.Item4); y1 = Math.Max(y1, b.Item5); z1 = Math.Max(z1, b.Item6);
        }
        // An object with no material is not in the solver's lists, but it is selected and moved all the same.
        foreach (var s in e.Solids.Concat(e.UnassignedSolids)) if (Mine(s.Name)) Grow(Em3dProblem.Bounds(s.Primitive));
        foreach (var s in e.Sheets.Concat(e.UnassignedSheets)) if (Mine(s.Name)) Grow(s.WorldBounds());
        if (double.IsInfinity(x0)) return null;
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        bool exact = true;
        double D(double m)
        {
            double d = m / per, r = Math.Round(d);
            if (Math.Abs(d - r) <= 1e-6) return r;
            exact = false;
            return d;
        }
        var b = (D(x0), D(y0), D(z0), D(x1), D(y1), D(z1));
        return (b.Item1, b.Item2, b.Item3, b.Item4, b.Item5, b.Item6, exact);
    }

    /// <summary>R-em3d46-3a — the default pivot: the targets' bounding-box centre, on a whole DBU (rounded down), so a
    /// quarter turn about it is an integer transform.</summary>
    public C3dPoint3 PivotOf(IReadOnlyList<C3dTarget> targets)
    {
        if (BoundsDbu(targets) is not { } b) return targets.Count > 0 ? PlacementOf(targets[0]).Origin : default;
        static long Mid(double a, double c) => (long)Math.Floor((a + c) / 2);
        return new C3dPoint3(Mid(b.X0, b.X1), Mid(b.Y0, b.Y1), Mid(b.Z0, b.Z1));
    }

    private bool HaveTargets(out IReadOnlyList<C3dTarget> targets)
    {
        targets = Targets();
        if (targets.Count > 0) return true;
        StatusMessage = Viewer.SelectMode == Scene3DSelectMode.Object
            ? "Select objects or instances to act on."
            : "Object operations act on whole objects: switch to Object mode (O).";
        return false;
    }

    // ── starting an operation (keys, menus, the gizmo) ───────────────────────────────────────

    /// <summary>G, R, Ctrl/Cmd+D — and brief-em3d-47's N, Shift+E and G on a face or a vertex. True when the key was one of them
    /// (a refusal is still an answer).</summary>
    private bool OperationKey(Key key, KeyModifiers modifiers)
    {
        if (FaceKey(key, modifiers)) return true;
        bool plain = modifiers == KeyModifiers.None;
        if (plain && key == Key.G) { StartMove(); return true; }
        if (plain && key == Key.R) { StartRotate(); return true; }
        if (key == Key.D && (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && (modifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) == 0)
        {
            StartDuplicate();
            return true;
        }
        return false;
    }

    /// <summary>R-em3d46-2 — Move, optionally locked to an axis (the menu's Move Along X / Y / Z).</summary>
    public void StartMove(C3dMoveLock lockTo = C3dMoveLock.None)
    {
        // brief-dfr34-1 R-dfr34-1-1 — ports alone selected: the same Move, on their rectangles.
        if (Viewer.SelectMode == Scene3DSelectMode.Object && Targets().Count == 0 && SelectedPorts() is { Count: > 0 } ports)
        {
            StartPortMove(ports, lockTo);
            return;
        }
        if (!HaveMovableTargets(out var targets)) return;
        var tool = new MoveTool(this, targets, PivotOf(targets), lockTo: lockTo);
        // With the cursor over the selection, the snapped point under it is the base, at once (R-em3d46-2a).
        var over = Viewer.LastPick.Object;
        if (over != 0 && targets.SelectMany(SceneObjectsOf).Any(o => o.Id == over) && FreePoint(CursorInput(), out _) is { } p)
            tool.SetBase(p, !Viewer.Snap.IsSnap || ToDocumentPoint(Viewer.Snap).Exact);
        BeginOperation(tool);
    }

    /// <summary>
    /// brief-dfr34-1 R-dfr34-1-1 — Move… on ports: the existing Move (base point, then target, snapped; X/Y/Z locks an axis) on
    /// their rectangles. Their sheets follow the cursor as an object's batches do, and the commit translates each rectangle —
    /// its plane's offset and its corner — as ONE undo entry. A port on a cylinder is placed by its cylinder and is not moved.
    /// </summary>
    public void StartPortMove(IReadOnlyList<C3dPort> ports, C3dMoveLock lockTo = C3dMoveLock.None)
    {
        if (IsViewOnly) return;
        var movable = ports.Where(p => p.Cylinder is null).Distinct().ToList();
        if (movable.Count == 0)
        {
            StatusMessage = "A port on a cylinder is a patch of that cylinder: move or resize the cylinder instead.";
            return;
        }
        var first = movable[0];
        long cu = first.Rect.Min.U + first.Rect.Size.U / 2, cv = first.Rect.Min.V + first.Rect.Size.V / 2;
        var pivot = first.Plane switch
        {
            C3dPlane.XZ => new C3dPoint3(cu, first.Offset, cv),
            C3dPlane.YZ => new C3dPoint3(first.Offset, cu, cv),
            _ => new C3dPoint3(cu, cv, first.Offset),
        };
        _portMoveTargets = [];
        var tool = new MoveTool(this, _portMoveTargets, pivot, lockTo: lockTo) { Ports = movable };
        var over = Viewer.LastPick.Object;
        if (over != 0 && PortSceneObjects(movable).Any(o => o.Id == over) && FreePoint(CursorInput(), out _) is { } p)
            tool.SetBase(p, !Viewer.Snap.IsSnap || ToDocumentPoint(Viewer.Snap).Exact);
        BeginOperation(tool);
    }

    /// <summary>The empty target list a port Move previews under: one list per operation, so the preview's mask is built once.</summary>
    private IReadOnlyList<C3dTarget> _portMoveTargets = [];

    /// <summary>The scene sheets of <paramref name="ports"/>: each one's, or each of its terminals'.</summary>
    private IEnumerable<Scene3DObject> PortSceneObjects(IEnumerable<C3dPort> ports)
        => ports.SelectMany(C3dPorts.Numbers).Select(n => SceneObject(C3dPorts.ProblemName(n))).OfType<Scene3DObject>();

    /// <summary>Translates <paramref name="ports"/>' rectangles by <paramref name="d"/> (world DBU): one undo entry.</summary>
    public bool MovePorts(IReadOnlyList<C3dPort> ports, C3dPoint3 d, bool exact = true)
    {
        var set = ports.ToHashSet();
        string what = ports.Count == 1 ? C3dPorts.Label(ports[0]) : $"{ports.Count} ports";
        bool changed = ChangeRecords($"Move {what}", doc =>
        {
            foreach (var p in doc.Ports.Where(set.Contains))
            {
                var (du, dv, da) = p.Plane switch
                {
                    C3dPlane.XZ => (d.X, d.Z, d.Y),
                    C3dPlane.YZ => (d.Y, d.Z, d.X),
                    _ => (d.X, d.Y, d.Z),
                };
                p.Offset += da;
                p.Rect = new C3dRect { Min = new C3dPoint2(p.Rect.Min.U + du, p.Rect.Min.V + dv), Size = p.Rect.Size };
            }
        });
        StatusMessage = changed ? $"Move {what}." + (exact ? "" : " ≈ Not a whole number of database units: rounded to the nearest one.")
                                : $"Move {what}: nothing moved.";
        if (changed) OperationCommits++;
        return changed;
    }

    /// <summary>R-em3d46-3 — Rotate about the drawing plane's normal (X, Y or Z changes it), through the pivot.</summary>
    public void StartRotate()
    {
        if (!HaveMovableTargets(out var targets)) return;
        BeginOperation(new RotateTool(this, targets, PivotOf(targets), _plane.Normal));
    }

    /// <summary>R-em3d46-4a — Duplicate: copies in place, then a Move whose base is the pivot.</summary>
    public void StartDuplicate()
    {
        if (!HaveTargets(out var targets)) return;
        BeginOperation(new MoveTool(this, targets, PivotOf(targets), duplicate: true));
    }

    private void BeginOperation(C3dOperationTool tool)
    {
        CloseArray();
        // brief-em3d-51 R-em3d51-6 — a placement holding an expression the preview could not show in full is refused here.
        if (OperationGrabRefusal(tool) is not null) return;
        SetTool(tool);
        StatusMessage = "";
        OperationChanged();
    }

    // ── the preview (R-em3d46-1a) ────────────────────────────────────────────────────────────

    private bool _opExcluding;
    private long _holdPreviewUntil = -1;

    /// <summary>Previews drawn — gate 1 reads that a move changes the transforms and nothing else.</summary>
    public long PreviewUpdates { get; private set; }

    private void OnCursorResolvedForOperation()
    {
        if (_tool is C3dOperationTool) OperationChanged();
        else if (_tool is C3dFaceEditTool) FaceToolChanged();
        else if (_tool is Hierarchy.PlaceInstanceTool) { OnPropertyChanged(nameof(ToolPrompt)); Viewer.RequestFrame(); }
        else if (_tool is WireTool { Step: 2 } wire) { UpdateWireClash(wire); OnPropertyChanged(nameof(ToolPrompt)); Viewer.RequestFrame(); }
    }

    /// <summary>The operation's state changed (the cursor, a key): its preview and its prompt follow.</summary>
    private void OperationChanged()
    {
        if (_tool is not C3dOperationTool op) return;
        var now = op.Current(CursorInput(), out _);
        if (now is { } t)
        {
            if (!_opExcluding)
            {
                // From the moment the selection follows the cursor, it never attracts the snap (R-snpf-4); before a
                // base point is chosen it must — that is how a pad's own corner becomes the base.
                foreach (var o in op.Targets.SelectMany(SceneObjectsOf)) _snapExcludedObjects.Add(o.Name);
                if (op is MoveTool { Ports.Count: > 0 } px) foreach (var o in PortSceneObjects(px.Ports)) _snapExcludedObjects.Add(o.Name);
                ApplySnapExclusion();
                _opExcluding = true;
            }
            if (op is MoveTool { Ports.Count: > 0 } pm)
                ShowPreview(_portMoveTargets, [t.Transform], false, movingOf: null, extra: PortSceneObjects(pm.Ports));
            else ShowPreview(op.Targets, [t.Transform], op.KeepsOriginal);
        }
        OnPropertyChanged(nameof(ToolPrompt));
        Viewer.RequestFrame();
    }

    /// <summary>The targets drawn under <paramref name="transforms"/> (world DBU), and also where they are when
    /// <paramref name="keepOriginal"/>. Allocates the moving mask once per operation; a move rewrites matrices.</summary>
    private void ShowPreview(IReadOnlyList<C3dTarget> targets, IReadOnlyList<C3dTransform> transforms, bool keepOriginal,
                             Func<C3dTarget, IEnumerable<Scene3DObject>>? movingOf = null, IEnumerable<Scene3DObject>? extra = null)
    {
        var scene = Viewer.Scene;
        var current = Viewer.View.Preview;
        Scene3DPreview preview;
        if (current is not null && current.KeepOriginal == keepOriginal && current.Moving.Length == scene.Objects.Length
            && ReferenceEquals(_previewTargets, targets))
        {
            preview = current;
            if (preview.Copies.Length != transforms.Count) preview.Copies = new Matrix4x4[transforms.Count];
        }
        else
        {
            var moving = new bool[scene.Objects.Length];
            foreach (var o in targets.SelectMany(movingOf ?? SceneObjectsOf).Concat(extra ?? [])) moving[o.Id - 1] = true;
            FaceImagesFollow(scene, moving);
            preview = new Scene3DPreview(moving, new Matrix4x4[transforms.Count], keepOriginal);
            _previewTargets = targets;
        }
        for (int k = 0; k < transforms.Count; k++) preview.Copies[k] = LocalMatrix(transforms[k]);
        PreviewUpdates++;
        Viewer.SetPreview(preview);
    }

    private IReadOnlyList<C3dTarget>? _previewTargets;

    /// <summary>brief-em3d-101 — a face image is a record of its own in the scene (<c>image:object/face</c>), so the objects a
    /// preview moves did not take it: it stayed where its object was until the drop. Each one on a moving object moves with it
    /// (its image draw follows its record's id). Matched by scene name, so a placed cell's own face images follow it too.</summary>
    private static void FaceImagesFollow(Scene3DModel scene, bool[] moving)
    {
        if (scene.PlacedFaceImages.Count == 0) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in scene.Objects) if (moving[o.Id - 1]) names.Add(o.Name);
        foreach (var o in scene.Objects)
        {
            if (!o.Tint || !o.Name.StartsWith(C3dImages.FacePrefix, StringComparison.Ordinal)) continue;
            if (names.Contains(C3dModelled.ObjectOfFace(o.Name[C3dImages.FacePrefix.Length..]))) moving[o.Id - 1] = true;
        }
    }

    /// <summary>A world transform (DBU) as the scene-local row-vector matrix the vertex stage applies: with world =
    /// local + O and t in metres, local' = M·local + (M·O + t − O).</summary>
    private Matrix4x4 LocalMatrix(C3dTransform t)
    {
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        var (ox, oy, oz) = Viewer.Scene.Origin;
        double tx = t.M00 * ox + t.M01 * oy + t.M02 * oz + t.Tx * per - ox;
        double ty = t.M10 * ox + t.M11 * oy + t.M12 * oz + t.Ty * per - oy;
        double tz = t.M20 * ox + t.M21 * oy + t.M22 * oz + t.Tz * per - oz;
        return new Matrix4x4(
            (float)t.M00, (float)t.M10, (float)t.M20, 0,
            (float)t.M01, (float)t.M11, (float)t.M21, 0,
            (float)t.M02, (float)t.M12, (float)t.M22, 0,
            (float)tx, (float)ty, (float)tz, 1);
    }

    /// <summary>The operation ended (committed or cancelled): the snap sees everything again; the preview goes unless
    /// a commit is holding it for its scene.</summary>
    private void EndOperation()
    {
        _opExcluding = false;
        _previewTargets = null;
        ClearSnapExclusion();
        if (_holdPreviewUntil < 0) Viewer.SetPreview(null);
    }

    /// <summary>A new scene was adopted: a preview held for a commit's scene ends once that scene is up.</summary>
    private void ReleaseHeldPreview(long generation)
    {
        if (_holdPreviewUntil >= 0 && generation >= _holdPreviewUntil)
        {
            _holdPreviewUntil = -1;
            if (_tool is not C3dOperationTool && !ArrayOpen) Viewer.SetPreview(null);
        }
        if (_selectAfterAdopt is { } names)
        {
            _selectAfterAdopt = null;
            Viewer.SetSelection(names.SelectMany(n => SceneObjectsNamed(n)).Select(o => Scene3DItem.OfObject(o.Id)));
        }
        if (_selectGroupAfterAdopt is { } group)
        {
            _selectGroupAfterAdopt = null;
            if (C3dGroups.MembersOf(Document, group).Count > 0) SelectGroupRow(group);
        }
    }

    private List<string>? _selectAfterAdopt;

    /// <summary>A group made by an edit whose members the scene does not hold yet (Flatten): its row and all of it are the
    /// selection once they are drawn.</summary>
    private string? _selectGroupAfterAdopt;

    private IEnumerable<Scene3DObject> SceneObjectsNamed(string name)
    {
        int oi = Document.Objects.FindIndex(o => o.Name == name);
        if (oi >= 0) return SceneObjectsOf(new C3dTarget(false, oi));
        int ii = Document.Instances.FindIndex(i => i.Name == name);
        if (ii >= 0) return SceneObjectsOf(new C3dTarget(true, ii));
        // brief-em3d-66 — an entered boolean's operand, by the name the scene draws it under.
        return SceneObject(name) is { } s ? [s] : [];
    }

    // ── committing ───────────────────────────────────────────────────────────────────────────

    /// <summary>Operations committed — gate counters.</summary>
    public int OperationCommits { get; private set; }

    private void CommitOperation(C3dOperationTool op)
    {
        if (op.Committed is not { } t) return;
        if (op is MoveTool { Ports.Count: > 0 } pm)
        {
            var d = new C3dPoint3((long)Math.Round(t.Transform.Tx, MidpointRounding.AwayFromZero), (long)Math.Round(t.Transform.Ty, MidpointRounding.AwayFromZero),
                                  (long)Math.Round(t.Transform.Tz, MidpointRounding.AwayFromZero));
            if (MovePorts(pm.Ports, d, t.Exact)) HoldPreviewForCommit();
            SetTool(null);
            return;
        }
        var targets = op.Targets;
        bool changed = op.KeepsOriginal
            ? InsertCopies(targets, [t.Transform], $"Duplicate {Describe(targets)}", t.Exact)
            : ApplyTransform(targets, t.Transform, t.Exact, op.TranslationOnly, $"{op.Name} {Describe(targets)}");
        if (changed) HoldPreviewForCommit();
        SetTool(null);
    }

    /// <summary>The committed scene is the next one: the preview stays until it lands.</summary>
    private void HoldPreviewForCommit() => _holdPreviewUntil = Viewer.Source.Requested;

    private string Describe(IReadOnlyList<C3dTarget> targets) => targets.Count == 1 ? NameOf(targets[0]) : $"{targets.Count} items";

    /// <summary>
    /// One undo entry: each target's placement composed with <paramref name="t"/> — a translation changes only the
    /// origin (the rotation list is kept verbatim); anything else is composed and canonicalised. False when it
    /// changed nothing.
    /// </summary>
    public bool ApplyTransform(IReadOnlyList<C3dTarget> targets, C3dTransform t, bool exact, bool translationOnly, string description)
    {
        var slots = new List<C3dEditSlot>();
        bool allExact = exact;
        var d = new C3dPoint3((long)Math.Round(t.Tx, MidpointRounding.AwayFromZero), (long)Math.Round(t.Ty, MidpointRounding.AwayFromZero),
                              (long)Math.Round(t.Tz, MidpointRounding.AwayFromZero));
        if (translationOnly && (Math.Abs(t.Tx - d.X) > 1e-6 || Math.Abs(t.Ty - d.Y) > 1e-6 || Math.Abs(t.Tz - d.Z) > 1e-6)) allExact = false;
        C3dPlacement Next(C3dPlacement p)
        {
            if (translationOnly) return p.Translated(d);
            var q = p.Then(t, out bool ex);
            allExact &= ex;
            return q;
        }
        var moved = new List<(int, C3dObject)>();
        foreach (var target in targets.OrderBy(x => x.Instance).ThenBy(x => x.Index))
        {
            if (target.Instance)
            {
                string before = C3dPersistence.SerializeInstance(Document.Instances[target.Index]);
                var copy = C3dPersistence.DeserializeInstance(before);
                copy.Placement = Next(copy.Placement);
                string after = C3dPersistence.SerializeInstance(copy);
                if (after != before) slots.Add(new C3dEditSlot(true, target.Index, before, after));
            }
            else if (ObjectAt(target.Index) is { } obj)
            {
                // brief-em3d-66 — an entered operand moves in its world form and is written back into its boolean.
                var copy = C3dBooleans.Copy(obj);
                copy.Placement = Next(copy.Placement);
                // brief-em3d-50: a wire carries its points; brief-em3d-132: a bound one may refuse the turn
                if (C3dWires.BakePlacement(copy, Document.DisplayUnit, Document.DbuPerMicron, out bool whole) is { } why) { StatusMessage = why; return false; }
                allExact &= whole;
                moved.Add((target.Index, copy));
            }
        }
        slots.AddRange(ReplacementSlots(moved));
        if (slots.Count == 0) { StatusMessage = $"{description}: nothing moved."; return false; }
        if (!Push(new C3dEdit(description, slots, ApplySlots))) return false;
        OperationCommits++;
        StatusMessage = $"{description}." + (allExact ? "" : " ≈ Not a whole number of database units: rounded to the nearest one.");
        return true;
    }

    /// <summary>
    /// One undo entry of insertions: a copy of every target per transform, each with the next free name and its
    /// placement composed with the transform. The copies are selected once their scene is up.
    /// </summary>
    public bool InsertCopies(IReadOnlyList<C3dTarget> targets, IReadOnlyList<C3dTransform> transforms, string description, bool exact = true)
    {
        var used = new HashSet<string>(Document.Objects.Select(o => o.Name).Concat(Document.Instances.Select(i => i.Name)).Concat(NestedNames()),
                                       StringComparer.Ordinal);
        var slots = new List<C3dEditSlot>();
        int nextObject = Document.Objects.Count, nextInstance = Document.Instances.Count;
        var names = new List<string>();
        bool allExact = exact;
        // 3D editor groups — a group copied whole is a new group per set of copies; a member copied alone stays in its group.
        var copiedGroups = CopiedGroupUnits(targets);
        var usedGroups = C3dGroups.Names(Document);
        foreach (var t in transforms)
        {
            var groupOf = C3dGroups.CopyPaths(Document, copiedGroups, usedGroups);
            bool translation = t.M00 == 1 && t.M11 == 1 && t.M22 == 1 && t.M01 == 0 && t.M02 == 0 && t.M10 == 0 && t.M12 == 0 && t.M20 == 0 && t.M21 == 0;
            var d = new C3dPoint3((long)Math.Round(t.Tx), (long)Math.Round(t.Ty), (long)Math.Round(t.Tz));
            C3dPlacement Next(C3dPlacement p)
            {
                if (translation) return p.Translated(d);
                var q = p.Then(t, out bool ex);
                allExact &= ex;
                return q;
            }
            foreach (var target in targets)
            {
                if (target.Instance)
                {
                    var copy = C3dPersistence.DeserializeInstance(C3dPersistence.SerializeInstance(Document.Instances[target.Index]));
                    copy.Name = C3dOperations.NextFreeName(copy.Name, used);
                    copy.Placement = Next(copy.Placement);
                    copy.Group = groupOf(copy.Group);
                    names.Add(copy.Name);
                    slots.Add(new C3dEditSlot(true, nextInstance++, null, C3dPersistence.SerializeInstance(copy)));
                }
                else
                {
                    // brief-em3d-66 R-em3d66-5b — a copy of an operand is a new top-level object, where the operand is.
                    var copy = C3dBooleans.Copy(ObjectAt(target.Index)!);
                    copy.Name = C3dOperations.NextFreeName(IsOperandIndex(target.Index) ? CopyBaseName(target.Index) : copy.Name, used);
                    // A copied boolean's Tools are named too, and a name is unique across the whole document (R-em3d64-1d).
                    foreach (var operand in C3dOperands.SelfAndDescendants(copy).Skip(1).Where(o => o.Name.Length > 0))
                        operand.Name = C3dOperations.NextFreeName(operand.Name, used);
                    copy.Placement = Next(copy.Placement);
                    // brief-em3d-50: a wire carries its points; brief-em3d-132: a bound one may refuse the turn
                    if (C3dWires.BakePlacement(copy, Document.DisplayUnit, Document.DbuPerMicron, out bool whole) is { } why) { StatusMessage = why; return false; }
                    allExact &= whole;
                    // An operand's copy is a top-level object in its boolean's group; anything else's, in its own (or the copy's).
                    copy.Group = IsOperandIndex(target.Index) ? (TopOf(target.Index, out _) is >= 0 and var top ? Document.Objects[top].Group : null)
                                                              : groupOf(copy.Group);
                    names.Add(copy.Name);
                    slots.Add(new C3dEditSlot(false, nextObject++, null, C3dPersistence.SerializeObject(copy)));
                }
            }
        }
        if (slots.Count == 0) return false;
        if (!Push(new C3dEdit(description, slots, ApplySlots))) return false;
        OperationCommits++;
        _selectAfterAdopt = names;
        StatusMessage = $"{description}: {names.Count} {(names.Count == 1 ? "copy" : "copies")}." +
                        (allExact ? "" : " ≈ Not a whole number of database units: rounded to the nearest one.");
        return true;
    }

    // ── the quick items: a quarter turn, a mirror ────────────────────────────────────────────

    /// <summary>Rotate 90° about X / Y / Z (and the reverses), through the pivot: one entry, no gesture.</summary>
    public void RotateQuick(C3dAxis axis, double deg)
    {
        if (!HaveMovableTargets(out var targets)) return;
        var t = C3dOperations.Rotation(axis, deg, PivotOf(targets));
        ApplyTransform(targets, t, t.IsIntegral, translationOnly: false, $"Rotate {(deg > 0 ? "+" : "")}{deg}° about {axis}: {Describe(targets)}");
    }

    /// <summary>R-em3d46-3c — Mirror across the XY, YZ or XZ plane through the pivot: one entry.</summary>
    public void MirrorAcross(C3dPlane plane)
    {
        if (!HaveMovableTargets(out var targets)) return;
        var t = C3dOperations.Mirror(plane, PivotOf(targets));
        ApplyTransform(targets, t, t.IsIntegral, translationOnly: false, $"Mirror across {plane}: {Describe(targets)}");
    }

    // ── Align (R-em3d46-4c) ──────────────────────────────────────────────────────────────────

    /// <summary>Where on an axis Align lines things up.</summary>
    public enum AlignAt { Min, Centre, Max }

    /// <summary>Every target but the LAST-selected moved along <paramref name="axis"/> so its min, centre or max meets the
    /// last-selected's. One entry; exact in DBU when both bounds are. A group selected whole is one target (C3dGroups): it is
    /// lined up by its own box and every member moves by the same step.</summary>
    public void Align(C3dAxis axis, AlignAt at)
    {
        if (!HaveTargets(out var all)) return;
        var units = TargetUnits(all);
        if (units.Count < 2) { StatusMessage = "Align needs two or more selected: the others line up with the last one selected."; return; }
        var (referenceGroup, reference) = units[^1];
        // brief-em3d-101 — a locked image is a fine thing to line up WITH (the last selected), never one that moves.
        if (LockedRefusalOf(units.Take(units.Count - 1).SelectMany(u => u.Targets)) is { } locked) { StatusMessage = locked; return; }
        string referenceName = referenceGroup is { } rg ? C3dGroups.NameOf(rg) : NameOf(reference[0]);
        if (BoundsDbu(reference) is not { } rb) { StatusMessage = $"'{referenceName}' has no elaborated geometry to align to."; return; }
        var slots = new List<C3dEditSlot>();
        bool exact = rb.Exact;
        double Key((double X0, double Y0, double Z0, double X1, double Y1, double Z1, bool Exact) b)
        {
            var (lo, hi) = axis switch { C3dAxis.X => (b.X0, b.X1), C3dAxis.Y => (b.Y0, b.Y1), _ => (b.Z0, b.Z1) };
            return at switch { AlignAt.Min => lo, AlignAt.Max => hi, _ => (lo + hi) / 2 };
        }
        var moves = new List<(C3dTarget Target, long D)>();
        int movedUnits = 0;
        foreach (var (_, unit) in units.Take(units.Count - 1))
        {
            if (BoundsDbu(unit) is not { } b) continue;
            double want = Key(rb) - Key(b);
            long d = (long)Math.Round(want, MidpointRounding.AwayFromZero);
            if (Math.Abs(want - d) > 1e-6 || !b.Exact) exact = false;
            if (d == 0) continue;
            movedUnits++;
            foreach (var t in unit) moves.Add((t, d));
        }
        if (moves.Count == 0) { StatusMessage = "Already aligned."; return; }
        foreach (var (t, d) in moves)
        {
            var by = DrawingPlane.With(default, axis, d);
            if (t.Instance)
            {
                string before = C3dPersistence.SerializeInstance(Document.Instances[t.Index]);
                var copy = C3dPersistence.DeserializeInstance(before);
                copy.Placement = copy.Placement.Translated(by);
                slots.Add(new C3dEditSlot(true, t.Index, before, C3dPersistence.SerializeInstance(copy)));
            }
            else if (ObjectAt(t.Index) is { } obj)
            {
                var copy = C3dBooleans.Copy(obj);
                copy.Placement = copy.Placement.Translated(by);
                if (C3dWires.BakePlacement(copy, Document.DisplayUnit, Document.DbuPerMicron, out _) is { } why) { StatusMessage = why; return; }
                slots.AddRange(ReplacementSlots([(t.Index, copy)]));
            }
        }
        string word = at switch { AlignAt.Min => "min", AlignAt.Max => "max", _ => "centre" };
        if (!Push(new C3dEdit($"Align {axis} {word} to {referenceName}", slots, ApplySlots))) return;
        OperationCommits++;
        StatusMessage = $"Aligned {movedUnits} to the {axis} {word} of '{referenceName}'." +
                        (exact ? "" : " ≈ Not a whole number of database units: rounded to the nearest one.");
    }

    // ── Order (R-em3d46-4d) ──────────────────────────────────────────────────────────────────

    /// <summary>How Order moves the selection in the object list — construction order, which decides overlap.</summary>
    public enum OrderMove { ToFront, ToBack, Forward, Backward }

    /// <summary>
    /// Moves the selected OBJECTS in the list (instances follow the objects in construction order and are not in it).
    /// One entry of replacements: each position whose object changed.
    /// </summary>
    public void Order(OrderMove move)
    {
        if (!HaveTargets(out var targets)) return;
        var chosen = targets.Where(t => !t.Instance && !IsOperandIndex(t.Index)).Select(t => t.Index).ToHashSet();
        if (chosen.Count == 0)
        {
            StatusMessage = targets.Any(t => !t.Instance && IsOperandIndex(t.Index))
                ? "Order moves top-level objects: a boolean's operands are ordered as Blank, then Tools."
                : "Order moves objects; an instance's contents follow the document's own objects.";
            return;
        }
        var order = Enumerable.Range(0, Document.Objects.Count).ToList();
        switch (move)
        {
            case OrderMove.ToFront: order = [.. order.Where(i => !chosen.Contains(i)), .. order.Where(chosen.Contains)]; break;
            case OrderMove.ToBack:  order = [.. order.Where(chosen.Contains), .. order.Where(i => !chosen.Contains(i))]; break;
            case OrderMove.Forward:
                for (int k = order.Count - 2; k >= 0; k--)
                    if (chosen.Contains(order[k]) && !chosen.Contains(order[k + 1])) (order[k], order[k + 1]) = (order[k + 1], order[k]);
                break;
            case OrderMove.Backward:
                for (int k = 1; k < order.Count; k++)
                    if (chosen.Contains(order[k]) && !chosen.Contains(order[k - 1])) (order[k], order[k - 1]) = (order[k - 1], order[k]);
                break;
        }
        var slots = new List<C3dEditSlot>();
        for (int k = 0; k < order.Count; k++)
            if (order[k] != k)
                slots.Add(new C3dEditSlot(false, k, C3dPersistence.SerializeObject(Document.Objects[k]),
                                          C3dPersistence.SerializeObject(Document.Objects[order[k]])));
        if (slots.Count == 0) { StatusMessage = "The order is already that."; return; }
        string word = move switch
        {
            OrderMove.ToFront => "Bring to Front", OrderMove.ToBack => "Send to Back", OrderMove.Forward => "Bring Forward", _ => "Send Backward",
        };
        if (!Push(new C3dEdit($"{word}: {Describe(targets)}", slots, ApplySlots))) return;
        OperationCommits++;
        StatusMessage = $"{word}: a later object wins where solids overlap.";
    }

    // ── Array… (R-em3d46-4b) ─────────────────────────────────────────────────────────────────

    /// <summary>More copies than this and the panel says one instance array would be one object to edit.</summary>
    public const int ArrayGroupHint = 100;

    [ObservableProperty] private bool _arrayOpen;
    [ObservableProperty] private string _arrayCountX = "2";
    [ObservableProperty] private string _arrayCountY = "1";
    [ObservableProperty] private string _arrayCountZ = "1";
    [ObservableProperty] private string _arrayPitchX = "";
    [ObservableProperty] private string _arrayPitchY = "";
    [ObservableProperty] private string _arrayPitchZ = "";
    [ObservableProperty] private string? _arrayError;
    [ObservableProperty] private string? _arrayNote;

    private IReadOnlyList<C3dTarget>? _arrayTargets;

    /// <summary>Opens the panel on the selection: counts 2 × 1 × 1, pitches twice the selection's size (a gap as wide
    /// as it), and the live preview.</summary>
    public void OpenArray()
    {
        if (!HaveTargets(out var targets)) return;
        SetTool(null);
        _arrayTargets = targets;
        var b = BoundsDbu(targets);
        string P(double lo, double hi) => C3dDimension.Spell(Math.Max(1, (long)Math.Round(2 * (hi - lo))), Document.DisplayUnit, Document.DbuPerMicron);
        _arrayLoading = true;
        ArrayCountX = "2"; ArrayCountY = "1"; ArrayCountZ = "1";
        ArrayPitchX = b is { } bx ? P(bx.X0, bx.X1) : "0";
        ArrayPitchY = b is { } by ? P(by.Y0, by.Y1) : "0";
        ArrayPitchZ = b is { } bz ? P(bz.Z0, bz.Z1) : "0";
        if (ArrayWire(targets) is { } wire) OpenWireArray(wire);
        _arrayLoading = false;
        ArrayOpen = true;
        UpdateArrayPreview();
    }

    // ── a wire's own array (3D editor round 4) ─────────────────────────────────────────────

    /// <summary>The one wire the panel acts on, or null when the selection is anything else.</summary>
    private C3dWire? ArrayWire(IReadOnlyList<C3dTarget> targets)
        => targets is [{ Instance: false } t] && t.Index < Document.Objects.Count && Document.Objects[t.Index] is C3dWire w ? w : null;

    /// <summary>
    /// The panel on one wire: its own row when it has one (the count on the axis its pitch runs along), else two wires side
    /// by side — across the wire's run in plan, four diameters apart, the usual bonding pitch. The other elements of an
    /// existing row are hidden while the panel is open, so the preview is the row as it will be.
    /// </summary>
    private void OpenWireArray(C3dWire w)
    {
        string L(long dbu) => C3dDimension.Spell(dbu, Document.DisplayUnit, Document.DbuPerMicron);
        ArrayCountX = ArrayCountY = ArrayCountZ = "1";
        ArrayPitchX = ArrayPitchY = ArrayPitchZ = "0";
        if (w.Array is { Count: > 1 } a)
        {
            var p = a.Pitch;
            int axis = Math.Abs(p.X) >= Math.Abs(p.Y) && Math.Abs(p.X) >= Math.Abs(p.Z) ? 0 : Math.Abs(p.Y) >= Math.Abs(p.Z) ? 1 : 2;
            string n = a.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            (axis == 0 ? (Action)(() => ArrayCountX = n) : axis == 1 ? () => ArrayCountY = n : () => ArrayCountZ = n)();
            (ArrayPitchX, ArrayPitchY, ArrayPitchZ) = (L(p.X), L(p.Y), L(p.Z));
            foreach (var o in SceneObjectsFor(w, balls: true).Except(WireElementZero(w))) Viewer.SetVisibleEverywhere(o.Id, false);
            return;
        }
        long pitch = Math.Max(1, (long)Math.Round(4 * (w.DiameterUm ?? C3dWires.DefaultDiameterUm) * Document.DbuPerMicron));
        var (s, e) = (w.Points[0], w.Points[^1]);
        bool alongX = Math.Abs(e.X - s.X) >= Math.Abs(e.Y - s.Y);
        if (alongX) { ArrayCountY = "2"; ArrayPitchY = L(pitch); }
        else { ArrayCountX = "2"; ArrayPitchX = L(pitch); }
    }

    /// <summary>The drawn wire's own scene objects — element 0 of a row, its sweep and balls.</summary>
    private IEnumerable<Scene3DObject> WireElementZero(C3dWire w)
    {
        string n = C3dWires.ElementName(w, 0);
        return new[] { n, n + "/ball/start", n + "/ball/end" }.Select(SceneObject).OfType<Scene3DObject>();
    }

    /// <summary>
    /// Accept on one wire: the wire's OWN array — one row, the count and the pitch — rather than copies, so the row is one
    /// object whose count and pitch stay editable (and may be expressions). A wire row is one-dimensional: counts on two
    /// axes are refused, and a count of 1 everywhere removes the array.
    /// </summary>
    private bool AcceptWireArray(IReadOnlyList<C3dTarget> targets)
    {
        if (ArrayWire(targets) is not { } wire) return false;
        if (!ArrayValues(out var n, out var pitch, out var exprs, out var why)) { ArrayError = why; return true; }
        int[] axes = [.. Enumerable.Range(0, 3).Where(k => n[k] > 1)];
        if (axes.Length > 1) { ArrayError = WireRowRefusal; return true; }
        int index = targets[0].Index;
        int count = axes.Length == 0 ? 1 : n[axes[0]];
        var vector = axes.Length == 0 ? default : axes[0] switch
        {
            0 => new C3dPoint3(pitch[0], 0, 0), 1 => new C3dPoint3(0, pitch[1], 0), _ => new C3dPoint3(0, 0, pitch[2]),
        };
        if (count > 1 && vector == default) { ArrayError = "A row of wires needs a pitch: every copy would lie on the first."; return true; }
        ChangeObjects($"Array {wire.Name} ×{count}", [index], o =>
        {
            if (o is not C3dWire w) return;
            w.Array = count == 1 ? null : new C3dWireArray { Count = count, Pitch = vector };
            if (w.Array is not { } a) return;
            // A count or a pitch typed as an expression is bound on the wire's own array, as on an instance's.
            foreach (var (field, k, e) in exprs)
            {
                if (axes.Length == 0 || k != axes[0]) continue;
                if (field == nameof(C3dArray.Counts)) C3dBindings.SetExpr(a, C3dBindings.SpecOf(typeof(C3dWireArray), nameof(C3dWireArray.Count))!, 0, e);
                else C3dBindings.SetExpr(a, C3dBindings.SpecOf(typeof(C3dWireArray), nameof(C3dWireArray.Pitch))!, k, e);
            }
        });
        OperationCommits++;
        StatusMessage = count == 1 ? $"{wire.Name} is one wire again." : $"{wire.Name} is now a row of {count} wires, each its own wire in the model.";
        return true;
    }

    private const string WireRowRefusal = "A wire array is one row — a number of wires and a pitch: give a count on one axis only.";

    private bool _arrayLoading;

    partial void OnArrayCountXChanged(string value) => UpdateArrayPreview();
    partial void OnArrayCountYChanged(string value) => UpdateArrayPreview();
    partial void OnArrayCountZChanged(string value) => UpdateArrayPreview();
    partial void OnArrayPitchXChanged(string value) => UpdateArrayPreview();
    partial void OnArrayPitchYChanged(string value) => UpdateArrayPreview();
    partial void OnArrayPitchZChanged(string value) => UpdateArrayPreview();

    /// <summary>The copies the panel states now (every element but the original's), or null with the reason.</summary>
    private List<C3dTransform>? ArrayCopies(out string? why)
    {
        why = null;
        if (ArrayValues(out var n, out var pitch, out _, out why) is false) return null;
        if (_arrayTargets is { } at && ArrayWire(at) is not null && n.Count(c => c > 1) > 1) { why = WireRowRefusal; return null; }
        long total = (long)n[0] * n[1] * n[2];
        if (total > 100_000) { why = $"{total:N0} copies is more than an array here holds."; return null; }
        var list = new List<C3dTransform>((int)total - 1);
        for (int i = 0; i < n[0]; i++)
            for (int j = 0; j < n[1]; j++)
                for (int k = 0; k < n[2]; k++)
                    if (i + j + k > 0) list.Add(C3dTransform.Translation(new C3dPoint3(i * pitch[0], j * pitch[1], k * pitch[2])));
        return list;
    }

    private void UpdateArrayPreview()
    {
        if (_arrayLoading || !ArrayOpen || _arrayTargets is not { } targets) return;
        var copies = ArrayCopies(out var why);
        ArrayError = why;
        if (copies is null) { Viewer.SetPreview(null); ArrayNote = null; return; }
        ArrayNote = copies.Count > ArrayGroupHint
            ? $"{copies.Count:N0} copies. Grouping them into a cell and arraying one instance would be one object to edit and one child to " +
              "elaborate (Group into Cell, in the context menu); Accept writes independent copies."
            : null;
        if (copies.Count > Scene3DFramePlan.MaxPreviewCopies)
            ArrayNote = (ArrayNote is null ? "" : ArrayNote + " ") + $"The preview shows the first {Scene3DFramePlan.MaxPreviewCopies}.";
        ShowPreview(targets, copies, keepOriginal: true, ArrayWire(targets) is { } w ? _ => WireElementZero(w) : null);
    }

    /// <summary>Accept: the copies, independent, as ONE undo entry.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void AcceptArray()
    {
        if (_arrayTargets is not { } targets) return;
        var copies = ArrayCopies(out var why);
        if (copies is null) { ArrayError = why; return; }
        // 3D editor round 4 — one wire: its OWN row, not copies.
        if (ArrayWire(targets) is not null)
        {
            ArrayError = null;
            if (!AcceptWireArray(targets) || ArrayError is not null) return;
            ArrayOpen = false;
            _arrayTargets = null;
            HoldPreviewForCommit();
            return;
        }
        // brief-em3d-48 — one instance: its OWN array, not copies (one child, drawn as transforms).
        if (targets is [{ Instance: true }])
        {
            ArrayError = null;
            if (!AcceptInstanceArray(targets) || ArrayError is not null) return;
            ArrayOpen = false;
            _arrayTargets = null;
            HoldPreviewForCommit();
            return;
        }
        ArrayOpen = false;
        _arrayTargets = null;
        if (copies.Count == 0 || !InsertCopies(targets, copies, $"Array {Describe(targets)}")) { Viewer.SetPreview(null); return; }
        HoldPreviewForCommit();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void CloseArray()
    {
        if (!ArrayOpen) return;
        ArrayOpen = false;
        _arrayTargets = null;
        ArrayError = ArrayNote = null;
        if (_holdPreviewUntil < 0) Viewer.SetPreview(null);
        ApplyHiddenFlags();                                  // a wire row's other elements, hidden while the panel was open
    }

    // ── the gizmo (IViewer3DEditHost, R-em3d46-5) ────────────────────────────────────────────

    public Point3? GizmoPivot
    {
        get
        {
            if (Viewer.SelectMode != Scene3DSelectMode.Object || ArrayOpen || Viewer.MeasureActive) return null;
            if (_tool is not null && !(_tool is MoveTool { FromGizmo: true })) return null;
            if (_tool is C3dOperationTool op) return DrawGeometry.Metres(op.Pivot, Document.DbuPerMicron);
            // Asked on every hover and every overlay frame: the pivot is kept with the targets it was found for.
            var targets = Targets();
            if (targets.Count == 0) return null;
            if (_gizmoPivot is not { } g || !ReferenceEquals(g.Targets, targets))
                _gizmoPivot = g = (targets, DrawGeometry.Metres(PivotOf(targets), Document.DbuPerMicron));
            return g.Pivot;
        }
    }

    private (IReadOnlyList<C3dTarget> Targets, Point3 Pivot)? _gizmoPivot;

    public bool GizmoDrag(GizmoHandle handle)
    {
        if (_tool is not null || !HaveMovableTargets(out var targets)) return false;
        var pivot = PivotOf(targets);
        int k = GizmoGeometry.AxisOf(handle);
        var axis = (C3dAxis)k;
        bool plane = GizmoGeometry.IsPlane(handle);
        var lockTo = plane ? C3dMoveLock.PlaneX + k : C3dMoveLock.AxisX + k;
        var extent = BoundsDbu(targets) is { } e
            ? (new C3dPoint3((long)Math.Round(e.X0), (long)Math.Round(e.Y0), (long)Math.Round(e.Z0)),
               new C3dPoint3((long)Math.Round(e.X1), (long)Math.Round(e.Y1), (long)Math.Round(e.Z1)), e.Exact)
            : ((C3dPoint3, C3dPoint3, bool)?)null;
        var tool = new MoveTool(this, targets, pivot, lockTo: lockTo) { FromGizmo = true, Extent = extent };
        // The base: the point on the axis nearest the cursor at the press — or where the cursor meets the plane.
        var input = CursorInput();
        C3dPoint3 b = pivot;
        if (input.HasRay)
        {
            var m = DrawGeometry.Metres(pivot, Document.DbuPerMicron);
            double per = C3dLowering.Metres(1, Document.DbuPerMicron);
            if (!plane && DrawingPlane.AlongAxisClosestToRay(m, axis, input.RayOrigin!.Value, input.RayDirection!.Value) is { } t)
                b = DrawingPlane.With(pivot, axis, DrawingPlane.Get(pivot, axis) + (long)Math.Round(t / per, MidpointRounding.AwayFromZero));
            else if (plane)
            {
                var dp = new DrawingPlane(DrawingPlane.PlaneNormalTo(axis), DrawingPlane.Get(pivot, axis));
                if (!dp.IsEdgeOn(input.RayDirection!.Value) && dp.Hit(input.RayOrigin!.Value, input.RayDirection!.Value, Document.DbuPerMicron) is { } h)
                    b = DrawingPlane.With(new C3dPoint3((long)Math.Round(h.X / per), (long)Math.Round(h.Y / per), (long)Math.Round(h.Z / per)),
                                          axis, DrawingPlane.Get(pivot, axis));
            }
        }
        tool.SetBase(b, exact: true);
        BeginOperation(tool);
        return true;
    }

    public void GizmoRelease()
    {
        if (_tool is MoveTool { FromGizmo: true } tool) Apply(tool.Click(CursorInput()));
    }

    public void GizmoCancel()
    {
        if (_tool is MoveTool { FromGizmo: true })
        {
            StatusMessage = "Move cancelled.";
            Disarm();
        }
    }

    // ── Measure (IViewer3DEditHost, R-em3d46-6) ──────────────────────────────────────────────

    /// <summary>The snap as the document's point (exact where it is one); else the drawing plane under the cursor,
    /// which is a DBU point once resolved.</summary>
    public Viewer3DMeasurePoint? MeasurePoint()
    {
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        if (Viewer.Snap.IsSnap)
        {
            var p = ToDocumentPoint(Viewer.Snap);
            return p.Exact ? new(p.Dbu.X * per, p.Dbu.Y * per, p.Dbu.Z * per, true) : new(p.Metres.X, p.Metres.Y, p.Metres.Z, false);
        }
        if (PlanePoint(CursorInput(), out _) is { } q) return new(q.X * per, q.Y * per, q.Z * per, true);
        return null;
    }

    public void MeasureStarted()
    {
        CloseArray();
        SetTool(null);
    }

    // ── the menus ────────────────────────────────────────────────────────────────────────────

    /// <summary>The context menu's operations (Object mode, with something movable selected).</summary>
    private IEnumerable<Viewer3DMenuItem> OperationMenuItems()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Object || Targets() is not { Count: > 0 } targets) yield break;
        bool objects = targets.Any(t => !t.Instance);
        yield return new Viewer3DMenuItem("Move", () => StartMove(), Gesture: Viewer3DMenuItem.Plain(Key.G));
        yield return new Viewer3DMenuItem("Move Along", Children:
        [
            new("X", () => StartMove(C3dMoveLock.AxisX)), new("Y", () => StartMove(C3dMoveLock.AxisY)), new("Z", () => StartMove(C3dMoveLock.AxisZ)),
        ]);
        yield return new Viewer3DMenuItem("Rotate", StartRotate, Gesture: Viewer3DMenuItem.Plain(Key.R));
        yield return new Viewer3DMenuItem("Rotate 90°", Children:
        [
            new("+90° about X", () => RotateQuick(C3dAxis.X, 90)), new("−90° about X", () => RotateQuick(C3dAxis.X, -90)),
            new("+90° about Y", () => RotateQuick(C3dAxis.Y, 90)), new("−90° about Y", () => RotateQuick(C3dAxis.Y, -90)),
            new("+90° about Z", () => RotateQuick(C3dAxis.Z, 90)), new("−90° about Z", () => RotateQuick(C3dAxis.Z, -90)),
        ]);
        yield return new Viewer3DMenuItem("Mirror", Children:
        [
            new("Across XY", () => MirrorAcross(C3dPlane.XY)), new("Across YZ", () => MirrorAcross(C3dPlane.YZ)),
            new("Across XZ", () => MirrorAcross(C3dPlane.XZ)),
        ]);
        yield return new Viewer3DMenuItem("Duplicate", StartDuplicate, Gesture: Viewer3DMenuItem.Command(Key.D));
        yield return new Viewer3DMenuItem("Array…", OpenArray);
        yield return new Viewer3DMenuItem("Align", Enabled: targets.Count >= 2,
            Tip: targets.Count >= 2 ? "To the last one selected." : "Select two or more: the others line up with the last one selected.",
            Children: [.. AlignItems()]);
        yield return new Viewer3DMenuItem("Order", Enabled: objects,
            Tip: objects ? "Construction order: a later object wins where solids overlap." : "Order moves objects, not instances.",
            Children:
            [
                new("Bring to Front", () => Order(OrderMove.ToFront)), new("Bring Forward", () => Order(OrderMove.Forward)),
                new("Send Backward", () => Order(OrderMove.Backward)), new("Send to Back", () => Order(OrderMove.ToBack)),
            ]);
        // brief-em3d-93 — every selected member (a group's at every depth), the function the Inspector's row calls
        if (ModelItem(SelectedMembers(), DescribeUnits(SelectedUnits())) is { } model) yield return model;
    }

    private IEnumerable<Viewer3DMenuItem> AlignItems()
    {
        foreach (var axis in new[] { C3dAxis.X, C3dAxis.Y, C3dAxis.Z })
            foreach (var at in new[] { AlignAt.Min, AlignAt.Centre, AlignAt.Max })
            {
                var (a, w) = (axis, at);
                yield return new Viewer3DMenuItem($"{axis} {w.ToString().ToLowerInvariant()}", () => Align(a, w));
            }
    }

    /// <summary>3D ▸ Modify's items, by name — the same functions the context menu and the keys call.</summary>
    public void RunModify(string which)
    {
        if (RunFaceModify(which) || RunBooleanModify(which) || RunEdgeModify(which)) return;
        switch (which)
        {
            case "Move" when Viewer.SelectMode == Scene3DSelectMode.Face: StartFaceMove(); break;
            case "Move" when Viewer.SelectMode == Scene3DSelectMode.Vertex: StartVertexMove(); break;
            case "Move": StartMove(); break;
            case "MoveX": StartMove(C3dMoveLock.AxisX); break;
            case "MoveY": StartMove(C3dMoveLock.AxisY); break;
            case "MoveZ": StartMove(C3dMoveLock.AxisZ); break;
            case "Rotate": StartRotate(); break;
            case "Duplicate": StartDuplicate(); break;
            case "Array": OpenArray(); break;
            case "Group": GroupSelection(); break;
            case "Ungroup": UngroupSelection(); break;
            case "ReseatWires": ReseatWireEnds(); break;
            case "StepReload" when Targets() is [{ Instance: false } st] && StepAt(st.Index) is { } step: _ = ReloadFromSourceAsync(step); break;
            case "StepSplit" when Targets() is [{ Instance: false } sp]: _ = SplitIntoSolidsAsync(sp.Index); break;
            case "StepPrism" when Targets() is [{ Instance: false } cp]: _ = ReplaceStepAsync(StepConvertKind.Prism, cp.Index); break;
            case "StepBox" when Targets() is [{ Instance: false } cb]: _ = ReplaceStepAsync(StepConvertKind.Box, cb.Index); break;
            case "StepPolyhedron" when Targets() is [{ Instance: false } ch]: _ = ReplaceStepAsync(StepConvertKind.Polyhedron, ch.Index); break;
            case "Front": Order(OrderMove.ToFront); break;
            case "Forward": Order(OrderMove.Forward); break;
            case "Backward": Order(OrderMove.Backward); break;
            case "Back": Order(OrderMove.ToBack); break;
            default:
                if (which.StartsWith("Mirror", StringComparison.Ordinal) && Enum.TryParse<C3dPlane>(which[6..], out var plane)) MirrorAcross(plane);
                else if (which.StartsWith("Rot", StringComparison.Ordinal) && which.Length == 5 && Enum.TryParse<C3dAxis>(which[3..4], out var ax))
                    RotateQuick(ax, which[4] == '-' ? -90 : 90);
                else if (which.StartsWith("Align", StringComparison.Ordinal) && which.Length > 6 && Enum.TryParse<C3dAxis>(which[5..6], out var al)
                         && Enum.TryParse<AlignAt>(which[6..], out var at))
                    Align(al, at);
                break;
        }
    }
}

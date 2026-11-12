// brief-em3d-48 — hierarchy in the 3D editor: Place Cell Instance, Swap View, arrays of placements, Push In / Pop Out,
// Flatten, Group into Cell, and the instance states the scene cannot show by itself (a cell that resolves to nothing,
// an external instance's [alias] tag).
//
// EVERY RULE IS C3dHierarchy's (src/Design): the cycle check, the flatten, the grouping. This file asks, confirms, and
// pushes ONE undo entry — a C3dEdit for an instance's own fields, a C3dListsEdit when objects and instances change
// together.
//
// PUSH IN is a NAVIGATION STACK in the same editor (hier2/hier3's shape): each frame is a document with its own undo
// history and its own save state, and the top frame's tab is the one on screen. The pushed-in child is elaborated as
// the editable document, IN ITS OWN FRAME — so the drawing plane, the snap and the Properties panel read in the child's
// coordinates and display unit — and ONLY the child is drawn, as the layout editor draws only the cell pushed into.
// (brief-em3d-48 drew the top document around it, dimmed; owner, 2026-10-04: the parent read as part of the child.)
// Pop Out saves or discards a dirty child, as the user answers; nothing is ever dropped silently.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.ThreeD.Hierarchy;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

/// <summary>What Pop Out does with a dirty child.</summary>
public enum C3dPopOutChoice { Save, Discard, Cancel }

/// <summary>One entry of the push-in breadcrumb.</summary>
public sealed record C3dBreadcrumb(int FrameIndex, string Text, bool IsCurrent);

public sealed partial class C3dEditorViewModel
{
    // ── the pick encoding's limits (R-em3d48-3b) ─────────────────────────────────────────────

    /// <summary>
    /// The most objects one placed child may elaborate to. The ID pass writes an element's object as the prototype's id
    /// plus a per-element offset (Scene3DElement.IdOffset) — a dense 32-bit word naming element and child object
    /// together — and the view keeps a record per drawn object, element by element. A child beyond this is refused at
    /// placement, naming both numbers, rather than drawn truncated.
    /// </summary>
    public const int DefaultMaxChildObjects = 1 << 20;

    /// <summary>The most objects one placement may draw across all its array elements (elements × child objects).</summary>
    public const long DefaultMaxPlacementObjects = 1L << 24;

    /// <summary>The limits in force — the constants, lowered by a test to exercise the refusal without a million objects.</summary>
    public int MaxChildObjects { get; set; } = DefaultMaxChildObjects;
    public long MaxPlacementObjects { get; set; } = DefaultMaxPlacementObjects;

    /// <summary>The refusal for a child of <paramref name="objects"/> objects in <paramref name="elements"/> elements, or null.</summary>
    public string? EncodingRefusal(string what, long objects, long elements)
    {
        if (objects > MaxChildObjects)
            return $"{what} elaborates to {objects:N0} objects; a placed cell may hold at most {MaxChildObjects:N0} " +
                   "(the ID pass names each drawn object in one 32-bit word, and the view keeps a record per object). Nothing was placed.";
        if (objects * elements > MaxPlacementObjects)
            return $"{what} would draw {objects * elements:N0} objects ({elements:N0} elements × {objects:N0}); one placement may draw at " +
                   $"most {MaxPlacementObjects:N0}. Group the child's objects, or use fewer elements.";
        return null;
    }

    // ── events the shell answers ─────────────────────────────────────────────────────────────

    /// <summary>Push In on a LAYOUT child: the shell opens its .clay in the layout editor.</summary>
    public event Action<string>? OpenLayoutRequested;

    /// <summary>HZN-9 — Push Into (and a double-click) on an instance that places a design of a Hertzon project: the shell
    /// opens that design in the 3D editor (the project and the design name). Unset, the status line says it cannot.</summary>
    public event Action<string, string>? OpenHertzonDesignRequested;

    /// <summary>A cell folder was created (Group into Cell): the shell refreshes the Project Tree.</summary>
    public event Action<string>? CellCreated;

    /// <summary>A yes/no the shell asks before an edit that states its outcome first (Flatten). Null: yes.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>A refusal the shell shows in a dialog, for a command whose only other answer would be the status line
    /// (Flatten): a menu command that did nothing must say why where it will be seen. Null: the status line only.</summary>
    public Func<string, Task>? Inform { get; set; }

    /// <summary>Flatten's offer of the materials this view's technology lacks: the shell adds them where a new material goes
    /// (the Paste dialog's destination), leaving that document unsaved, and returns the refusal or null. Null: nothing is
    /// offered, and a missing material stays a refusal.</summary>
    public Func<IReadOnlyList<TechMaterial>, Task<string?>>? AddMaterials { get; set; }

    /// <summary>Pop Out of a dirty child: the shell asks Save / Discard / Cancel. Null: Save.</summary>
    public Func<string, Task<C3dPopOutChoice>>? PopOutQuestion { get; set; }

    /// <summary>Whether a file is open in another tab — a child open there is edited there, not in context here.</summary>
    public Func<string, bool>? OpenElsewhere { get; set; }

    /// <summary>The shell asks for a name (Group into Cell, Rename Instance): the prompt and the suggestion; null cancels.</summary>
    public Func<string, string, Task<string?>>? AskName { get; set; }

    // ── Place Cell Instance (R-em3d48-1) ─────────────────────────────────────────────────────

    /// <summary>
    /// Arms placement of <paramref name="view"/> of the cell at <paramref name="cellDir"/> (referenced as
    /// <paramref name="cellRef"/> from this document): refused at the pick — before anything is written — for a cycle
    /// (by cell AND view), a missing view, or a child beyond the pick encoding. Returns the refusal, or null when armed.
    /// </summary>
    public string? BeginInstancePlacement(string cellRef, string cellDir, C3dInstanceView view)
    {
        string? why = C3dHierarchy.MissingView(cellDir, view) ?? C3dHierarchy.CycleRefusal(FilePath, cellDir, view);
        if (why is not null) { StatusMessage = why; return why; }
        string cellName = Path.GetFileName(cellDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var template = new C3dInstance { Name = C3dHierarchy.NextInstanceName(Document), CellRef = cellRef, View = view };

        // The child's elaborated bounds and object count: one instance at the origin, through this editor's elaborator,
        // whose child cache means the placement that follows elaborates nothing again.
        var probe = new C3dDocument { DbuPerMicron = Document.DbuPerMicron, TechRef = Document.TechRef, Instances = [template] };
        C3dElaboration e;
        lock (_elaborating) e = _elaborator.Elaborate(probe, FilePath, _workspaceCws());
        if (!e.Ok && e.Solids.Count + e.Sheets.Count == 0)
        {
            why = e.Refusals.Count > 0 ? e.Refusals[0] : $"'{cellName}' elaborates to nothing.";
            StatusMessage = why;
            return why;
        }
        if (EncodingRefusal($"'{cellName}'", e.Solids.Count + e.Sheets.Count, 1) is { } limit) { StatusMessage = limit; return limit; }
        var ext = e.Extent() ?? (0, 0, 0, 0, 0, 0);
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        long Lo(double m) => (long)Math.Floor(m / per + 1e-9);
        long Hi(double m) => (long)Math.Ceiling(m / per - 1e-9);
        SetTool(new PlaceInstanceTool(this, template, cellName, new C3dPoint3(Lo(ext.X0), Lo(ext.Y0), Lo(ext.Z0)),
                                      new C3dPoint3(Hi(ext.X1), Hi(ext.Y1), Hi(ext.Z1))));
        StatusMessage = "";
        return null;
    }

    /// <summary>The armed placement, clicked at <paramref name="point"/> (a snapped DBU point) — what a click on the pane does
    /// once the cursor is resolved; a typed placement and the gates call it directly.</summary>
    public void PlaceArmedAt(C3dPoint3 point, bool bottomCentre = false)
    {
        if (_tool is PlaceInstanceTool tool) Apply(tool.Click(new C3dDrawInput(point, true, true, null, null, Command: bottomCentre)));
    }

    /// <summary>The placement's one undo entry.</summary>
    private void CommitPlacement(PlaceInstanceTool tool)
    {
        if (tool.Placed is not { } inst) return;
        Push(new C3dEdit($"Place {inst.Name}", [new C3dEditSlot(true, Document.Instances.Count, null, C3dPersistence.SerializeInstance(inst))],
                         ApplySlots));
        ToolCommits++;
        StatusMessage = $"Placed {inst.Name} ({tool.CellName}, {PlaceInstanceTool.ViewNoun(inst.View)}) with its " +
                        (tool.BottomCentre ? "bottom-centre" : "origin") + $" at ({Length3(inst.Placement.Origin)}).";
        SetTool(null);
    }

    private string Length3(C3dPoint3 p)
        => $"{LayoutUnits.Format(p.X, Document.DisplayUnit, Document.DbuPerMicron)}, {LayoutUnits.Format(p.Y, Document.DisplayUnit, Document.DbuPerMicron)}, " +
           $"{LayoutUnits.Format(p.Z, Document.DisplayUnit, Document.DbuPerMicron)} {LayoutUnits.Suffix(Document.DisplayUnit)}";

    // ── instance edits ───────────────────────────────────────────────────────────────────────

    /// <summary>One undoable edit of instance <paramref name="index"/>; nothing changed, no entry.</summary>
    public void ChangeInstance(string description, int index, Action<C3dInstance> mutate)
    {
        string before = C3dPersistence.SerializeInstance(Document.Instances[index]);
        var copy = C3dPersistence.DeserializeInstance(before);
        mutate(copy);
        string after = C3dPersistence.SerializeInstance(copy);
        if (after != before) Push(new C3dEdit(description, [new C3dEditSlot(true, index, before, after)], ApplySlots));
    }

    /// <summary>R-em3d48-1c — an instance's rename, validated; null on success.</summary>
    public string? RenameInstance(int index, string name)
    {
        var inst = Document.Instances[index];
        name = name.Trim();
        if (name == inst.Name) return null;
        if (C3dHierarchy.ValidateInstanceName(Document, name, inst) is { } why) return why;
        ChangeInstance($"Rename {inst.Name} to {name}", index, i => i.Name = name);
        return null;
    }

    /// <summary>The one instance the selection is part of (Object mode), or −1.</summary>
    public int SelectedInstance()
    {
        var targets = Targets();
        return targets is [{ Instance: true } t] ? t.Index : -1;
    }

    // ── Swap View (R-em3d48-2) ───────────────────────────────────────────────────────────────

    /// <summary>The names the last swap stranded — gate 4 reads it.</summary>
    public IReadOnlyList<string> LastSwapMissing { get; private set; } = [];

    /// <summary>
    /// Swaps instance <paramref name="index"/> between its 3D and layout views: one undo entry, the placement and array
    /// kept. The document's ports and face boundaries that name something inside it are checked against the NEW view's
    /// elaboration, and every one that no longer exists is reported in one message — the swap still happens, since a
    /// layout's generated conductor names and a .c3d's drawn names are not the same and pretending otherwise would be
    /// the silent failure. Returns the message.
    /// </summary>
    public string SwapView(int index)
    {
        var inst = Document.Instances[index];
        if (C3dHierarchy.SwapRefusal(FilePath, inst) is { } why) return StatusMessage = why;
        var to = C3dHierarchy.Other(inst.View);
        var names = C3dHierarchy.NamesInside(Document, inst.Name);
        ChangeInstance($"Swap {inst.Name} to its {(to == C3dInstanceView.Layout ? "layout" : "3D view")}", index, i => i.View = to);
        LastSwapMissing = [];
        string said = $"{inst.Name} now shows its {(to == C3dInstanceView.Layout ? "layout" : "3D view")}.";
        if (names.Count > 0)
        {
            C3dElaboration e;
            lock (_elaborating) e = _elaborator.Elaborate(Document, FilePath, _workspaceCws());
            LastSwapMissing = C3dHierarchy.Unresolved(names, e);
            if (LastSwapMissing.Count > 0)
                said += $" {LastSwapMissing.Count} name{(LastSwapMissing.Count == 1 ? "" : "s")} in this view's ports and face boundaries " +
                        $"no longer exist{(LastSwapMissing.Count == 1 ? "s" : "")} in it: {string.Join(", ", LastSwapMissing)}. The swap is kept — " +
                        "point them at what the new view calls those conductors, or swap back.";
        }
        return StatusMessage = said;
    }

    // ── Flatten and Group into Cell (R-em3d48-5) ─────────────────────────────────────────────

    /// <summary>Flattens instance <paramref name="index"/> one level — asking first for a layout child, stating the count
    /// (L3c R-L3c-1a: the command states its outcome before acting). One undo entry.</summary>
    public async Task FlattenAsync(int index)
    {
        var (result, question) = PlanFlatten(index, out var add);
        if (result is null) { await InformRefusal(); return; }
        if (question is not null && Confirm is { } ask && !await ask(question)) { StatusMessage = "Flatten cancelled."; return; }
        if (!await AddMissingMaterials(add)) return;
        ApplyFlatten(index, result);
    }

    /// <summary>The flatten of instance <paramref name="index"/>, and the question to ask first (a layout child) — or null
    /// with the refusal on the status line.</summary>
    public (C3dFlattenResult? Result, string? Question) PlanFlatten(int index) => PlanFlatten(index, out _);

    /// <summary><see cref="PlanFlatten(int)"/>, and the materials the technology must be given first (<see cref="AddMaterials"/>).</summary>
    public (C3dFlattenResult? Result, string? Question) PlanFlatten(int index, out IReadOnlyList<TechMaterial> add)
    {
        var inst = Document.Instances[index];
        string? cws = _workspaceCws();
        C3dElaboration e;
        lock (_elaborating) e = _elaborator.Elaborate(Document, FilePath, cws);
        (var r, add) = WithMissingMaterials(e.Technology, t => C3dHierarchy.FlattenOne(Document, FilePath, index, e, t, null, cws), r => r.MissingMaterials);
        if (r.Refusal is { } why) { StatusMessage = why; add = []; return (null, null); }
        string? question = inst.View == C3dInstanceView.Layout
            ? $"Flatten {inst.Name} writes its layout's {r.Objects.Count:N0} elaborated solid{(r.Objects.Count == 1 ? "" : "s")} into this view as " +
              $"objects{(r.Group is { } g ? $", in group '{C3dGroups.NameOf(g)}'" : "")}. The link to the layout is then gone: a later edit of " +
              "the layout will not reach them. Flatten?"
            : null;
        if (add.Count > 0) question = MaterialsSentence($"{inst.Name}'s", add, e.TechnologyPath) + " " + (question ?? "Flatten?");
        return (r, question);
    }

    /// <summary>
    /// The plan, offered the materials it lacks: run again on a COPY of the technology holding them until it needs no more (a
    /// deeper level can name others), so the question states the outcome before anything is written. Only when the shell can
    /// add them; what the plan ends on — success or a refusal — is returned with every material it was given.
    /// </summary>
    private (T Result, IReadOnlyList<TechMaterial> Added) WithMissingMaterials<T>(Technology? tech, Func<Technology?, T> plan,
                                                                                 Func<T, IReadOnlyList<TechMaterial>> missing)
    {
        var added = new List<TechMaterial>();
        var result = plan(tech);
        if (AddMaterials is null || tech is null) return (result, added);
        Technology? copy = null;
        for (int round = 0; round < 8 && missing(result) is { Count: > 0 } more; round++)
        {
            copy ??= TechPersistence.Clone(tech);
            copy.Materials.AddRange(more);
            added.AddRange(more);
            result = plan(copy);
        }
        return (result, added);
    }

    /// <param name="whose">The possessive the sentence names: "U1's", "the instances'".</param>
    private static string MaterialsSentence(string whose, IReadOnlyList<TechMaterial> add, string? techPath)
    {
        string tech = techPath is null ? "This view's technology" : $"'{Path.GetFileName(techPath)}'";
        var names = add.Select(m => $"'{m.Name}' ({C3dHierarchy.DescribeMaterial(m)})").ToList();
        string list = names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
        return add.Count == 1
            ? $"{tech} has no material for some of {whose} solids, so one is added to it with their values: {list}."
            : $"{tech} has no material for some of {whose} solids, so {add.Count} are added to it with their values: {list}.";
    }

    /// <summary>The materials a confirmed flatten needs, added through the shell; false (the refusal said) when they were not.</summary>
    private async Task<bool> AddMissingMaterials(IReadOnlyList<TechMaterial> add)
    {
        if (add.Count == 0 || AddMaterials is not { } shell) return true;
        if (await shell(add) is not { } why) return true;
        StatusMessage = why + " Nothing was flattened.";
        await InformRefusal();
        return false;
    }

    /// <summary>The flatten's one undo entry: the instance replaced by the result's instances, its objects appended. What
    /// came out is then the selection — its group's, when it made one.</summary>
    public void ApplyFlatten(int index, C3dFlattenResult r)
    {
        var inst = Document.Instances[index];
        var before = C3dListsEdit.Of(Document);
        var objects = before.Objects.Concat(r.Objects.Select(C3dPersistence.SerializeObject)).ToList();
        var instances = before.Instances.ToList();
        instances.RemoveAt(index);
        instances.InsertRange(index, r.Instances.Select(C3dPersistence.SerializeInstance));
        Viewer.SetSelection([]);
        if (r.Group is not null) _selectGroupAfterAdopt = r.Group;
        else _selectAfterAdopt = [.. r.Objects.Select(o => o.Name), .. r.Instances.Select(i => i.Name)];
        Push(new C3dListsEdit($"Flatten {inst.Name}", before, (objects, instances), ApplyLists));
        StatusMessage = $"Flattened {inst.Name}: {r.Objects.Count:N0} object{(r.Objects.Count == 1 ? "" : "s")}" +
                        (r.Instances.Count > 0 ? $" and {r.Instances.Count} instance{(r.Instances.Count == 1 ? "" : "s")}" : "") +
                        (r.Group is { } g ? $" in its place, gathered in group '{C3dGroups.NameOf(g)}'." : " in its place.") +
                        (r.Notes.Count > 0 ? " " + string.Join(" ", r.Notes) : "");
    }

    /// <summary>Flatten (all levels): the result computed on a copy FIRST and its object count stated (R-L3c-4), then one
    /// undo entry.</summary>
    public async Task FlattenAllAsync()
    {
        var (flat, question) = PlanFlattenAll(out var add);
        if (flat is null) { await InformRefusal(); return; }
        if (Confirm is { } ask && !await ask(question!)) { StatusMessage = "Flatten cancelled."; return; }
        if (!await AddMissingMaterials(add)) return;
        ApplyFlattenAll(flat);
    }

    /// <summary>A Plan… that returned nothing left its refusal on the status line; the shell shows it too.</summary>
    private async Task InformRefusal()
    {
        if (Inform is { } tell && StatusMessage is { Length: > 0 } why) await tell(why);
    }

    public (C3dDocument? Flat, string? Question) PlanFlattenAll() => PlanFlattenAll(out _);

    /// <summary><see cref="PlanFlattenAll()"/>, and the materials the technology must be given first (<see cref="AddMaterials"/>).</summary>
    public (C3dDocument? Flat, string? Question) PlanFlattenAll(out IReadOnlyList<TechMaterial> add)
    {
        add = [];
        if (Document.Instances.Count == 0) { StatusMessage = "There is no instance to flatten."; return (null, null); }
        string? cws = _workspaceCws();
        C3dElaboration e;
        lock (_elaborating) e = _elaborator.Elaborate(Document, FilePath, cws);
        var ((flat, why, notes, _), materials) = WithMissingMaterials(e.Technology,
            t => C3dHierarchy.FlattenAll(Document, FilePath, d => { lock (_elaborating) return _elaborator.Elaborate(d, FilePath, cws); }, t, null, cws),
            r => r.MissingMaterials);
        if (flat is null) { StatusMessage = why ?? "Flatten failed."; return (null, null); }
        add = materials;
        int added = flat.Objects.Count - Document.Objects.Count;
        return (flat, (add.Count > 0 ? MaterialsSentence("the instances'", add, e.TechnologyPath) + " " : "") + $"Flatten all levels replaces {Document.Instances.Count} instance{(Document.Instances.Count == 1 ? "" : "s")} with " +
                      $"{added:N0} object{(added == 1 ? "" : "s")}; this view will hold {flat.Objects.Count:N0} objects and no instances. " +
                      "Every link to a placed cell is then gone. Flatten?" + (notes.Count > 0 ? " " + string.Join(" ", notes) : ""));
    }

    public void ApplyFlattenAll(C3dDocument flat)
    {
        var before = C3dListsEdit.Of(Document);
        Viewer.SetSelection([]);
        Push(new C3dListsEdit("Flatten all levels", before, C3dListsEdit.Of(flat), ApplyLists));
        StatusMessage = $"Flattened every level: {Document.Objects.Count:N0} objects, no instances.";
    }

    /// <summary>
    /// Group into Cell: the selected objects and instances move into a new cell <paramref name="cellName"/> beside this
    /// one (its <c>3d/</c> holding them), replaced by one instance of it — the geometry does not move (R-L3c-5). Undo
    /// removes the instance and restores them, and does NOT delete the cell folder (R-L3c-6). Returns the refusal, or null.
    /// </summary>
    public string? GroupIntoCell(string cellName)
    {
        var targets = Targets();
        if (targets.Count == 0) return StatusMessage = "Select the objects and instances to group first.";
        if (targets.Any(t => !t.Instance && IsOperandIndex(t.Index)))
            return StatusMessage = "A boolean's operands stay in their boolean: leave it (Esc) and group the boolean.";
        var bounds = BoundsDbu(targets);
        var anchor = bounds is { } b ? new C3dPoint3((long)Math.Floor(b.X0), (long)Math.Floor(b.Y0), (long)Math.Floor(b.Z0)) : default;
        string docDir = Path.GetDirectoryName(FilePath)!;
        string parentDir = C3dHierarchy.CellDirOf(FilePath) is { } cell ? Path.GetDirectoryName(cell)! : docDir;
        var objs = targets.Where(t => !t.Instance).Select(t => t.Index).ToList();
        var insts = targets.Where(t => t.Instance).Select(t => t.Index).ToList();
        var (inst, cellDir, _, why) = C3dHierarchy.GroupIntoCell(Document, FilePath, objs, insts, cellName, parentDir, anchor);
        if (why is not null) return StatusMessage = why;

        var before = C3dListsEdit.Of(Document);
        var objects = before.Objects.Where((_, i) => !objs.Contains(i)).ToList();
        var instances = before.Instances.Where((_, i) => !insts.Contains(i)).Append(C3dPersistence.SerializeInstance(inst!)).ToList();
        Viewer.SetSelection([]);
        Push(new C3dListsEdit($"Group into Cell {cellName}", before, (objects, instances), ApplyLists));
        GroupsMade++;
        CellCreated?.Invoke(cellDir!);
        StatusMessage = $"Grouped {objs.Count + insts.Count} item{(objs.Count + insts.Count == 1 ? "" : "s")} into cell '{cellName}', placed as " +
                        $"{inst!.Name}. Undo restores them and keeps the new cell.";
        return null;
    }

    /// <summary>Group into Cell's entries, counted for the gate.</summary>
    public int GroupsMade { get; private set; }

    private void ApplyLists(IReadOnlyList<string> objects, IReadOnlyList<string> instances)
    {
        DocumentWrites++;
        C3dListsEdit.Apply(Document, objects, instances);
        DocumentChanged();
    }

    // ── arrays of placements (brief 46's Array…, acting on a placement) ──────────────────────

    /// <summary>
    /// R-em3d48-0 — Array… on ONE selected instance sets that instance's array (counts and pitch on X, Y and Z) rather than
    /// writing copies: the child is elaborated and tessellated once and every element is a transform. False when the
    /// selection is not one instance, and the copies path runs as before.
    /// </summary>
    private bool AcceptInstanceArray(IReadOnlyList<C3dTarget> targets)
    {
        if (targets is not [{ Instance: true } t]) return false;
        if (!ArrayValues(out var n, out var pitch, out var exprs, out _)) return false;
        var inst = Document.Instances[t.Index];
        long objects = Elaboration?.Provenance.Count(kv => kv.Value.InstancePath == inst.Name || kv.Value.InstancePath.StartsWith(inst.Name + "[", StringComparison.Ordinal)
                                                          || kv.Value.InstancePath.StartsWith(inst.Name + "/", StringComparison.Ordinal)) ?? 0;
        long perElement = inst.Array is { Counts: [var a, var b, var c] } ? objects / Math.Max(1L, (long)a * b * c) : objects;
        if (EncodingRefusal($"'{inst.Name}'", perElement, (long)n[0] * n[1] * n[2]) is { } why) { ArrayError = why; return true; }
        ChangeInstance($"Array {inst.Name} {n[0]}×{n[1]}×{n[2]}", t.Index, i =>
        {
            i.Array = n[0] * n[1] * n[2] == 1 ? null : new C3dArray { Counts = [n[0], n[1], n[2]], Pitch = new C3dPoint3(pitch[0], pitch[1], pitch[2]) };
            // brief-em3d-51 — a count or a pitch typed as an expression is bound on the instance's own array.
            if (i.Array is { } a)
                foreach (var (field, k, e) in exprs)
                    C3dBindings.SetExpr(a, C3dBindings.SpecOf(typeof(C3dArray), field)!, k, e);
        });
        OperationCommits++;
        StatusMessage = $"{inst.Name} is now an array of {n[0]} × {n[1]} × {n[2]}: one child, drawn {n[0] * n[1] * n[2]:N0} times.";
        return true;
    }

    // ── Push In / Pop Out (R-em3d48-4) ───────────────────────────────────────────────────────

    /// <summary>One level of the navigation stack: a document, its history and save state, and where it sits in the top
    /// document (its element's transform, metres to metres).</summary>
    private sealed class NavFrame
    {
        public required string FilePath;
        public required C3dDocument Document;
        public required UndoRedoStack UndoRedo;
        public bool PreferenceDirty;
        public string? SavedStamp;
        public string Label = "";
        public string ElementPath = "";
        public Camera3D? Camera;
        public DrawingPlane Plane = DrawingPlane.Default;
    }

    private readonly List<NavFrame> _frames = [];

    /// <summary>The TOP document's path — the tab's file, whatever frame is being edited.</summary>
    public string TopFilePath => _frames.Count > 0 ? _frames[0].FilePath : FilePath;

    public int NavDepth => Math.Max(0, _frames.Count - 1);
    public bool CanPopOut => _frames.Count > 1;

    /// <summary>One instance is selected — what the toolbar's Push Into Cell button is enabled on, as the layout editor's
    /// is. Raised on every selection change and every push or pop.</summary>
    public bool CanPushIn => SelectedInstance() >= 0;

    /// <summary>The breadcrumb: every frame, the last one current (hier4's shape).</summary>
    public IReadOnlyList<C3dBreadcrumb> Breadcrumbs
        => [.. _frames.Select((f, i) => new C3dBreadcrumb(i, f.Label, i == _frames.Count - 1))];

    /// <summary>The status line's frame: which document, whose coordinates.</summary>
    [ObservableProperty] private string _frameText = "";

    private void InitFrames()
    {
        _frames.Clear();
        _frames.Add(new NavFrame
        {
            FilePath = FilePath, Document = Document, UndoRedo = UndoRedo, SavedStamp = _savedStamp,
            Label = C3dHierarchy.CellDirOf(FilePath) is { } c ? Path.GetFileName(c) : Path.GetFileNameWithoutExtension(FilePath),
        });
    }

    /// <summary>The active frame's state, written back from the fields that hold it while it is active.</summary>
    private void StoreActive()
    {
        var f = _frames[^1];
        f.FilePath = FilePath; f.Document = Document; f.UndoRedo = UndoRedo;
        f.PreferenceDirty = _preferenceDirty; f.SavedStamp = _savedStamp; f.Plane = _plane;
    }

    private void LoadActive()
    {
        var f = _frames[^1];
        FilePath = f.FilePath;
        Document = f.Document;
        ResolveDocument();
        UndoRedo = f.UndoRedo;
        _preferenceDirty = f.PreferenceDirty;
        _savedStamp = f.SavedStamp;
        _loading = true;
        DisplayUnit = Document.DisplayUnit;
        _loading = false;
        _origin = null;
        Viewer.SetSelection([]);
        SetTool(null);
        CloseArray();
        CancelBoolean();
        LeaveBooleanFully();                // brief-em3d-66 — an entered boolean is the frame's editor state, not the next one's
        ApplyLengthFormat();
        _plane = f.Plane;
        ApplySnapGrid();
        SyncPlaneTexts();
        FrameText = _frames.Count == 1 ? "" :
            $"Editing '{f.Label}' — coordinates in {Path.GetFileName(f.FilePath)}'s frame, {LayoutUnits.Suffix(Document.DisplayUnit)}. " +
            "Pop Out to return to the parent.";
        OnPropertyChanged(nameof(UndoRedo));
        OnPropertyChanged(nameof(NavDepth));
        OnPropertyChanged(nameof(CanPopOut));
        OnPropertyChanged(nameof(CanPushIn));
        OnPropertyChanged(nameof(Breadcrumbs));
        OnPropertyChanged(nameof(IsDirty));
        DocumentChanged();
    }

    /// <summary>
    /// Push Into Cell on instance <paramref name="index"/> (element <paramref name="element"/> of an array, [0,0,0] when
    /// null): a 3D child is edited here, alone; a layout child opens in the layout editor. Returns the refusal, or
    /// null.
    /// </summary>
    public string? PushInto(int index, (int I, int J, int K)? element = null)
    {
        var inst = Document.Instances[index];
        string baseDir = Path.GetDirectoryName(FilePath)!;
        if (inst.View == C3dInstanceView.Hertzon)
        {
            string? hzn = CircuitRF.Design.Interchange.Hzn.HznInstances.ResolveFile(inst.CellRef, baseDir);
            if (hzn is null || !File.Exists(hzn))
                return StatusMessage = $"'{inst.Name}' places '{inst.CellRef}', which resolves to nothing: there is nothing to open.";
            if (OpenHertzonDesignRequested is null)
                return StatusMessage = $"'{inst.Name}' places design '{inst.Design}' of '{Path.GetFileName(hzn)}'; open it from the Project Tree.";
            OpenHertzonDesignRequested(hzn, inst.Design ?? "");
            return StatusMessage = $"{inst.Name} is a Hertzon design: it opened in its own tab.";
        }
        if (ExternalCellRef.ResolveCellDir(inst.CellRef, baseDir) is not { } cellDir || !Directory.Exists(cellDir))
            return StatusMessage = $"'{inst.Name}' places '{inst.CellRef}', which resolves to nothing: there is nothing to push into.";
        if (C3dHierarchy.ViewFile(cellDir, inst.View) is not { } file) return StatusMessage = C3dHierarchy.MissingView(cellDir, inst.View)!;
        if (inst.View == C3dInstanceView.Layout)
        {
            OpenLayoutRequested?.Invoke(file);
            return StatusMessage = $"{inst.Name} is a layout: it opened in the layout editor (a layout is not edited in 3D).";
        }
        if (OpenElsewhere?.Invoke(file) == true)
            return StatusMessage = $"'{Path.GetFileName(file)}' is open in its own tab: edit it there, or close it to edit it here.";
        if (_frames.Any(f => string.Equals(Path.GetFullPath(f.FilePath), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)))
            return StatusMessage = $"'{Path.GetFileName(file)}' is already open in this stack.";
        C3dDocument child;
        try { child = C3dPersistence.LoadFromFile(file); }
        catch (Exception ex) { return StatusMessage = $"'{file}' cannot be read: {ex.Message}"; }

        var (i, j, k) = element ?? (0, 0, 0);
        var counts = inst.Array?.Counts ?? [1, 1, 1];
        bool array = inst.Array is not null && counts.Any(n => n > 1);
        StoreActive();
        var parent = _frames[^1];
        parent.Camera = Viewer.View.Camera;
        var frame = new NavFrame
        {
            FilePath = file, Document = child, UndoRedo = new UndoRedoStack(), SavedStamp = Stamp(file),
            Label = $"{inst.Name}{(array ? $"[{i},{j},{k}]" : "")} · {Path.GetFileName(cellDir)}",
            ElementPath = (parent.ElementPath.Length > 0 ? parent.ElementPath + "/" : "") + inst.Name + (array ? $"[{i},{j},{k}]" : ""),
            Plane = _plane,
        };
        WatchStack(frame.UndoRedo);
        _frames.Add(frame);
        _fitOnAdopt = true;
        LoadActive();
        return null;
    }

    private bool _fitOnAdopt;

    /// <summary>Pop Out: asks about a dirty child (Save / Discard / Cancel), then returns to the parent. False when the
    /// user cancelled or there is nothing to pop out of.</summary>
    public async Task<bool> PopOutAsync()
    {
        if (!CanPopOut) return false;
        var choice = C3dPopOutChoice.Save;
        if (UndoRedo.IsModified || _preferenceDirty)
            choice = PopOutQuestion is { } ask
                ? await ask($"'{Path.GetFileName(FilePath)}' has unsaved changes. Save them before popping out?")
                : C3dPopOutChoice.Save;
        return PopOut(choice);
    }

    /// <summary>Pop Out with the answer already given — a dirty child is saved or discarded, never lost (R-em3d48-4c).</summary>
    public bool PopOut(C3dPopOutChoice choice)
    {
        if (!CanPopOut || choice == C3dPopOutChoice.Cancel) return false;
        if ((UndoRedo.IsModified || _preferenceDirty) && choice == C3dPopOutChoice.Save && Save() is { } why)
        {
            StatusMessage = $"'{Path.GetFileName(FilePath)}' could not be saved, so it stays open: {why}";
            return false;
        }
        StoreActive();
        UnwatchStack(_frames[^1].UndoRedo);
        _frames.RemoveAt(_frames.Count - 1);
        var camera = _frames[^1].Camera;
        LoadActive();
        if (camera is { } c) Viewer.View.Camera = c;
        // The child's saved file re-elaborates here: its stamp changed, so the elaborator's child cache misses it once.
        Viewer.Invalidate();
        return true;
    }

    /// <summary>The breadcrumb's click: pops out to <paramref name="frameIndex"/>, one level at a time, each asked about.</summary>
    public async Task PopToAsync(int frameIndex)
    {
        while (_frames.Count - 1 > frameIndex)
            if (!await PopOutAsync()) return;
    }

    /// <summary>The child's dirty edits of every frame BELOW the active one — the tab is dirty while any frame is.</summary>
    private bool OtherFramesDirty => _frames.Take(Math.Max(0, _frames.Count - 1)).Any(f => f.UndoRedo.IsModified || f.PreferenceDirty);

    private void WatchStack(UndoRedoStack s) => s.PropertyChanged += OnStackChanged;
    private void UnwatchStack(UndoRedoStack s) => s.PropertyChanged -= OnStackChanged;

    private void OnStackChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UndoRedoStack.IsModified)) OnPropertyChanged(nameof(IsDirty));
    }

    // ── instancing for the scene (R-em3d48-3a) ───────────────────────────────────────────────

    /// <summary>
    /// Where each elaborated object stands among the TOP-LEVEL placements of <paramref name="doc"/>: a run is one child
    /// file in one view under one rotation, so the elements of an array — and placements of one child turned alike — are
    /// its elements, each the first moved by a translation. The builder draws every element after the first from the
    /// first's triangles.
    /// </summary>
    internal static Func<string, Scene3DInstancing?> InstancingFor(C3dDocument doc, C3dElaboration e)
    {
        var byName = new Dictionary<string, (C3dInstance Inst, int Index, string Key)>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Instances.Count; i++)
        {
            var inst = doc.Instances[i];
            var m = inst.Placement.ToTransform();
            // brief-em3d-92 — and its transparency: an element draws its prototype's vertices, colours and alpha included, so two
            // placements of one cell at different transparencies are two runs. brief-em3d-105 — likewise its appearance slot.
            string key = string.Create(CultureInfo.InvariantCulture,
                $"{inst.CellRef}|{inst.View}|{inst.Transparency}|{inst.Appearance?.Describe()}|{m.M00:R},{m.M01:R},{m.M02:R},{m.M10:R},{m.M11:R},{m.M12:R},{m.M20:R},{m.M21:R},{m.M22:R}");
            byName.TryAdd(inst.Name, (inst, i, key));
        }
        return name =>
        {
            if (!e.Provenance.TryGetValue(name, out var p) || p.InstancePath.Length == 0) return null;
            string top = p.InstancePath;
            int slash = top.IndexOf('/');
            if (slash >= 0) top = top[..slash];
            int br = top.IndexOf('[');
            if (!byName.TryGetValue(br >= 0 ? top[..br] : top, out var t) || name.Length <= top.Length) return null;
            int i = 0, j = 0, k = 0;
            if (br >= 0)
            {
                var parts = top[(br + 1)..^1].Split(',');
                if (parts.Length != 3 || !int.TryParse(parts[0], out i) || !int.TryParse(parts[1], out j) || !int.TryParse(parts[2], out k)) return null;
            }
            var counts = t.Inst.Array?.Counts is { Count: 3 } c ? c : [1, 1, 1];
            var pitch = t.Inst.Array?.Pitch ?? default;
            var o = t.Inst.Placement.Origin;
            int dbu = doc.DbuPerMicron;
            int element = (t.Index << 20) | (i + counts[0] * (j + counts[1] * k));
            return new Scene3DInstancing(t.Key, element,
                C3dLowering.Metres(o.X + i * pitch.X, dbu), C3dLowering.Metres(o.Y + j * pitch.Y, dbu), C3dLowering.Metres(o.Z + k * pitch.Z, dbu),
                name[(top.Length + 1)..]);
        };
    }

    // ── what the scene cannot show by itself (R-em3d48-6b) ───────────────────────────────────

    /// <summary>Each top-level instance's last good bounds (world metres), for a later "not found".</summary>
    private readonly Dictionary<string, (double X0, double Y0, double Z0, double X1, double Y1, double Z1)> _lastBounds = new(StringComparer.Ordinal);

    /// <summary>Called when a scene is adopted: the resolved instances' bounds, kept for the day one stops resolving.</summary>
    private void RememberInstanceBounds()
    {
        if (Elaboration is not { } e) return;
        var per = new Dictionary<string, (double, double, double, double, double, double)>(StringComparer.Ordinal);
        foreach (var o in Viewer.Scene.Objects)
        {
            if (!e.Provenance.TryGetValue(o.Name, out var p) || p.InstancePath.Length == 0) continue;
            string top = p.InstancePath.Split('/', '[')[0];
            var (x0, y0, z0) = Viewer.Scene.ToWorld(o.Min);
            var (x1, y1, z1) = Viewer.Scene.ToWorld(o.Max);
            per[top] = per.TryGetValue(top, out var b)
                ? (Math.Min(b.Item1, x0), Math.Min(b.Item2, y0), Math.Min(b.Item3, z0), Math.Max(b.Item4, x1), Math.Max(b.Item5, y1), Math.Max(b.Item6, z1))
                : (x0, y0, z0, x1, y1, z1);
        }
        foreach (var (name, b) in per) _lastBounds[name] = b;
    }

    /// <summary>The overlay's chrome: a dashed box and the cell's name for an instance that resolves to nothing, and the
    /// <c>[alias]</c> tag on an instance of another workspace's cell.</summary>
    private void FillHierarchyOverlay(Viewer3DDrawOverlay overlay)
    {
        if (Elaboration is not { } e) return;
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        foreach (var (path, cellRef) in e.Unresolved)
        {
            if (path.Contains('/')) continue;
            string name = path.Split('[')[0];
            var inst = Document.Instances.FirstOrDefault(i => i.Name == name);
            if (inst is null) continue;
            var o = inst.Placement.Origin;
            double side = C3dLowering.Metres(Math.Max(1, LayoutUnits.ToDbu(1m, Document.DisplayUnit, Document.DbuPerMicron)), Document.DbuPerMicron);
            var b = _lastBounds.TryGetValue(name, out var last) ? last
                : (o.X * per, o.Y * per, o.Z * per, o.X * per + side, o.Y * per + side, o.Z * per + side);
            Box(overlay.Missing, b);
            overlay.Labels.Add((new Point3(b.Item1, b.Item2, b.Item6),
                                e.UnresolvedWhy.TryGetValue(path, out var why) ? $"{name}: {why}" : $"{name}: '{cellRef}' not found"));
        }
        foreach (var inst in Document.Instances)
        {
            if (!ExternalCellRef.TryParse(inst.CellRef, out string alias, out _) || !_lastBounds.TryGetValue(inst.Name, out var b)) continue;
            overlay.Labels.Add((new Point3(b.X0, b.Y0, b.Z1), $"[{alias}]"));
        }

        static void Box(List<DrawSegment> into, (double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
        {
            Point3 C(int k) => new((k & 1) == 0 ? b.X0 : b.X1, (k & 2) == 0 ? b.Y0 : b.Y1, (k & 4) == 0 ? b.Z0 : b.Z1);
            for (int k = 0; k < 8; k++)
                for (int bit = 1; bit <= 4; bit <<= 1)
                    if ((k & bit) == 0) into.Add(new DrawSegment(C(k), C(k | bit)));
        }
    }

    // ── menus and keys ───────────────────────────────────────────────────────────────────────

    /// <summary>The context menu's hierarchy entries, for one selected instance.</summary>
    private IEnumerable<Viewer3DMenuItem> HierarchyMenuItems()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Object) yield break;
        int index = SelectedInstance();
        if (index < 0)
        {
            // brief-sar-3 — a phantom selected whole: its Flatten
            if (SelectedPhantom() is >= 0 and var phantom) { yield return Viewer3DMenuItem.Separator; yield return FlattenPhantomItem(phantom); }
            if (Targets().Count > 0) yield return new Viewer3DMenuItem("Group into Cell…", () => _ = GroupIntoCellAsync());
            // brief-sar-12 — a device selected beside a phantom: put it against one of the phantom's faces.
            if (SelectedPhantom() < 0 && PlacementRefusal() is null) yield return PlaceAgainstPhantomItem();
            yield break;
        }
        yield return Viewer3DMenuItem.Separator;
        foreach (var item in InstanceMenuItems(index, PushIntoSelected)) yield return item;
        yield return new Viewer3DMenuItem("Group into Cell…", () => _ = GroupIntoCellAsync());
        if (PlacementRefusal() is null) yield return PlaceAgainstPhantomItem();
    }

    private Viewer3DMenuItem PlaceAgainstPhantomItem()
        => new("Place Against Phantom…", () => _ = OpenPlacementAsync(),
               Tip: "At a separation from a phantom face, measured along its normal from the nearest point, with a tilt.");

    /// <summary>Instance <paramref name="index"/>'s own entries — Push Into, Swap View, Flatten, Flatten All Levels, Rename —
    /// which the canvas's menu and the object tree's (on the instance's row and on each of its parts) both offer.
    /// <paramref name="pushInto"/>: the canvas pushes into the element under the selection, the tree into the instance.</summary>
    private IEnumerable<Viewer3DMenuItem> InstanceMenuItems(int index, Func<string?> pushInto)
    {
        var inst = Document.Instances[index];
        yield return new Viewer3DMenuItem(inst.View == C3dInstanceView.Layout ? "Push Into Cell (opens the layout)"
                                          : inst.View == C3dInstanceView.Hertzon ? "Open Design" : "Push Into Cell",
                                          () => pushInto(), Tip: "Ctrl/Cmd+]. Ctrl/Cmd+[ pops back out.");
        string? swap = C3dHierarchy.SwapRefusal(FilePath, inst);
        yield return new Viewer3DMenuItem(inst.View == C3dInstanceView.Layout ? "Swap View to 3D" : "Swap View to Layout", () => SwapView(index),
                                          Enabled: swap is null, Tip: swap);
        // brief-port-width-planar-and-c3d R-pw1-5 — the layout's port labels, translated again, replace this view's ports.
        if (inst.View == C3dInstanceView.Layout)
            yield return new Viewer3DMenuItem("Update Ports from Layout", () => UpdatePortsFromLayout(),
                                              Tip: "Replace this view's ports with what the placed layouts' port labels build now. " +
                                                   "Ports the layouts do not state are kept. Undoable.");
        yield return new Viewer3DMenuItem("Flatten", () => _ = FlattenAsync(index));
        yield return new Viewer3DMenuItem("Flatten All Levels", () => _ = FlattenAllAsync());
        yield return new Viewer3DMenuItem("Rename Instance…", () => _ = RenameInstanceAsync(index));
    }

    /// <summary>The object tree's hierarchy entries for an instance's row, or for one of its parts (the instance that placed
    /// it, at the top level); empty for any other row.</summary>
    private List<Viewer3DMenuItem> TreeInstanceItems(C3dTreeItem item)
    {
        int index = item.InstanceIndex >= 0 && !item.IsReadOnly ? item.InstanceIndex
                  : item.IsReadOnly && Elaboration?.Provenance.TryGetValue(item.Name, out var p) == true && p.InstancePath.Length > 0
                      ? Document.Instances.FindIndex(i => i.Name == p.InstancePath.Split('/', '[')[0])
                      : -1;
        return index < 0 || index >= Document.Instances.Count ? [] : [.. InstanceMenuItems(index, () => PushInto(index, null))];
    }

    /// <summary>Push In on the selected instance — the element under the selection, for an array.</summary>
    public string? PushIntoSelected()
    {
        int index = SelectedInstance();
        if (index < 0) return StatusMessage = "Select one instance to push into.";
        (int, int, int)? element = null;
        var first = Viewer.SelectedObjects().FirstOrDefault();
        if (first is not null && InstanceOf(first) is { } path && path.IndexOf('[') is int br and >= 0)
        {
            string ijk = path.Split('/')[0][(br + 1)..^1];
            var parts = ijk.Split(',');
            if (parts.Length == 3 && int.TryParse(parts[0], out int i) && int.TryParse(parts[1], out int j) && int.TryParse(parts[2], out int k))
                element = (i, j, k);
        }
        return PushInto(index, element);
    }

    private async Task GroupIntoCellAsync()
    {
        string suggestion = "group1";
        string? parent = C3dHierarchy.CellDirOf(FilePath) is { } cell ? Path.GetDirectoryName(cell) : Path.GetDirectoryName(FilePath);
        for (int n = 1; parent is not null && Directory.Exists(Path.Combine(parent, $"group{n}")); n++) suggestion = $"group{n + 1}";
        if (AskName is not { } ask || await ask("New cell name:", suggestion) is not { } name) return;
        GroupIntoCell(name);
    }

    private async Task RenameInstanceAsync(int index)
    {
        if (AskName is not { } ask || await ask("Instance name:", Document.Instances[index].Name) is not { } name) return;
        if (RenameInstance(index, name) is { } why) StatusMessage = why;
    }

    /// <summary>Ctrl/Cmd+] pushes in, Ctrl/Cmd+[ pops out — the layout editor's keys.</summary>
    private bool HierarchyKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers)
    {
        if ((modifiers & (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Meta)) == 0) return false;
        if (key == Avalonia.Input.Key.OemCloseBrackets) { PushIntoSelected(); return true; }
        if (key == Avalonia.Input.Key.OemOpenBrackets && CanPopOut) { _ = PopOutAsync(); return true; }
        return false;
    }
}

// 3D editor groups — objects and instances gathered under a name (C3dGroups is the model and every rewrite).
//
// A CLICK IN THE VIEW TAKES THE WHOLE GROUP (Object mode): the top-most group the picked object is in, every member of it,
// which is what makes a group act as one thing — Move, Rotate, Mirror, Duplicate, Array, Align, Delete, Hide, a material
// and a boolean all act on the selection, so they act on the group. To reach one member, click its row in the tree (or B
// through the view). A selection is read back as UNITS (C3dGroups.Units): a group whose every member is selected is one
// unit, so the tree shows its row and the Inspector shows the group.
//
// EVERY GROUP EDIT IS ONE ORDINARY UNDO ENTRY: grouping, ungrouping and renaming a group rewrite the members' paths, and
// a path is part of the object (or instance) as the file spells it, so the entry is object and instance replacements.
//
// A GROUP HAS NO PLACEMENT. The Inspector's Corner is the members' bounding box, and typing one moves every member by the
// same whole-DBU step; the Size is read, not typed — scaling a mixture of boxes, cylinders and wires has no one meaning.

using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The kind of a group's row in the tree.</summary>
    public const string GroupKind = "Group";

    private const string GroupsHeader = "Groups";

    // ── members, units, and the scene objects they are drawn as ─────────────────────────────

    /// <summary>The document member a scene object belongs to — an object, or the instance an instance's part is of — or null
    /// (an entered boolean's operand, the air box, a port).</summary>
    public C3dMemberRef? MemberOf(Scene3DObject o)
    {
        if (InstanceOf(o) is { } path)
            return Document.Instances.FindIndex(i => i.Name == path.Split('/', '[')[0]) is >= 0 and var ii ? new C3dMemberRef(true, ii) : null;
        return DocumentIndex(o) is >= 0 and var oi && !IsOperandIndex(oi) ? new C3dMemberRef(false, oi) : null;
    }

    /// <summary>What a member is drawn as: an object's scene objects (a wire's balls too), an instance's every part.</summary>
    private IEnumerable<Scene3DObject> SceneObjectsOfMember(C3dMemberRef m)
        => m.Index < (m.Instance ? Document.Instances.Count : Document.Objects.Count) ? SceneObjectsOf(new C3dTarget(m.Instance, m.Index)) : [];

    /// <summary>Everything the group at <paramref name="path"/> is drawn as.</summary>
    public IReadOnlyList<Scene3DObject> SceneObjectsOfGroup(string path)
        => [.. C3dGroups.MembersOf(Document, path).SelectMany(SceneObjectsOfMember).Distinct()];

    /// <summary>The members a tree row stands for: a group's every member, a top-level object's or an instance's own; none for
    /// any other row (an operand, a feature, an instance's part, a record).</summary>
    private IReadOnlyList<C3dMemberRef> MembersOfRow(C3dTreeItem r)
    {
        if (r.IsGroup) return C3dGroups.MembersOf(Document, r.GroupPath!);
        if (r.OperandPath is null && !r.IsFeature && r.ObjectIndex >= 0 && r.ObjectIndex < Document.Objects.Count) return [new C3dMemberRef(false, r.ObjectIndex)];
        if (r.InstanceIndex >= 0 && !r.IsReadOnly && r.InstanceIndex < Document.Instances.Count) return [new C3dMemberRef(true, r.InstanceIndex)];
        return [];
    }

    /// <summary>The selection's members, in selection order: the view's objects and instances, then what only the tree has
    /// selected (an object elaboration refused, a group row's members that are not drawn).</summary>
    public IReadOnlyList<C3dMemberRef> SelectedMembers()
    {
        var list = new List<C3dMemberRef>();
        foreach (var t in Targets())
            if (t.Instance || !IsOperandIndex(t.Index)) list.Add(new C3dMemberRef(t.Instance, t.Index));
        foreach (var r in SelectedTreeItems) list.AddRange(MembersOfRow(r));
        return [.. list.Distinct()];
    }

    /// <summary>The selection as a group sees it (<see cref="C3dGroups.Units"/>).</summary>
    public IReadOnlyList<C3dGroupUnit> SelectedUnits() => C3dGroups.Units(Document, SelectedMembers());

    /// <summary>The path of the one group the selection is, or null.</summary>
    public string? SelectedGroupPath() => !IsViewOnly && AnyGroups(Document) && SelectedUnits() is [{ IsGroup: true } only] ? only.GroupPath : null;

    private static bool AnyGroups(C3dDocument doc) => doc.Objects.Any(o => o.Group is not null) || doc.Instances.Any(i => i.Group is not null);

    // ── a click takes the group ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A click on <paramref name="item"/> in the view: in Object mode, when its object or instance is in a group, every
    /// member of the top-most such group — the picked one first. Anything else is the item alone.
    /// </summary>
    public IReadOnlyList<Scene3DItem> PickGroup(Scene3DItem item)
    {
        if (item.IsEdge || ClickGroupOf(item.Object) is not { } top) return [item];
        var all = SceneObjectsOfGroup(top).Select(s => Scene3DItem.OfObject(s.Id)).Where(i => i != item);
        return [item, .. all];
    }

    /// <summary>The path of the top-most group a click on scene object <paramref name="id"/> takes whole, or null: Object mode,
    /// no boolean entered, and the object (or the instance it is a part of) in a group.</summary>
    private string? ClickGroupOf(uint id)
    {
        if (IsViewOnly || _entered is not null || Viewer.SelectMode != Scene3DSelectMode.Object) return null;
        return Viewer.Scene.Object(id) is { } o && MemberOf(o) is { } m ? C3dGroups.TopOf(C3dGroups.PathOf(Document, m)) : null;
    }

    /// <summary>The hover line's group: the name of the group a click on <paramref name="o"/> takes, or null.</summary>
    private string? HoverGroupName(Scene3DObject o) => ClickGroupOf(o.Id) is { } top ? C3dGroups.NameOf(top) : null;

    /// <summary>
    /// A double-click on a group's member with no tool armed: that member alone (the double-click's first click took the whole
    /// group). When the member was already the selection alone, a boolean is entered instead, so a boolean inside a group is
    /// entered by double-clicking it again. False when the item is in no group, which leaves the double-click to the boolean.
    /// </summary>
    private bool DoubleClickMember()
    {
        if (Viewer.HoveredItem is not { IsEdge: false } item || ClickGroupOf(item.Object) is not { } top) return false;
        if (Viewer.SelectionBeforeClick is [var only] && only == item && DoubleClickEnter()) return true;
        Viewer.SelectOnly(item);
        if (Viewer.Scene.Object(item.Object) is { } o)
            StatusMessage = $"'{o.Name}' alone. A click selects all of '{C3dGroups.NameOf(top)}'.";
        return true;
    }

    // ── Group, Ungroup, Rename ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Group Objects (Ctrl/Cmd+G): the selection becomes one new group, <c>Group&lt;n&gt;</c>, where its members have a group
    /// in common; a group selected whole goes in as a group. One undo entry; the new group is then the selection.
    /// </summary>
    public void GroupSelection()
    {
        if (IsViewOnly) return;
        if (_entered is not null) { StatusMessage = "Leave the boolean first (Esc): its operands are grouped with it, not on their own."; return; }
        var units = SelectedUnits();
        if (GroupRefusal(units) is { } why) { StatusMessage = why; return; }
        string name = C3dGroups.NextName(Document);
        var moves = C3dGroups.Group(Document, units, name);
        var segs = moves[0].Path!.Split(C3dGroups.Separator);
        string path = string.Join(C3dGroups.Separator, segs[..(Array.IndexOf(segs, name) + 1)]);
        if (!SetGroupPaths(moves, $"Group {DescribeUnits(units)} as {name}")) return;
        SelectGroupRow(path);
        StatusMessage = $"Grouped {DescribeUnits(units)} as '{name}'. A click in the view now selects all of it; rename it in Properties.";
    }

    /// <summary>Why the selection cannot be grouped, or null: a group holds two things or more — two objects, instances or
    /// groups. One group selected on its own is already a group, and one object alone is nothing to gather.</summary>
    public static string? GroupRefusal(IReadOnlyList<C3dGroupUnit> units) => units.Count switch
    {
        0 => "Select two or more objects, instances or groups — in the view or in the tree — then Group Objects.",
        1 => units[0].IsGroup
            ? $"'{C3dGroups.NameOf(units[0].GroupPath!)}' is already a group: select something else with it to group them together."
            : "A group holds two or more things: select another object, instance or group with it.",
        _ => null,
    };

    /// <summary>The Group Objects item, enabled only for a selection it can group; its tooltip says why not.</summary>
    private Viewer3DMenuItem GroupObjectsItem()
    {
        string? why = _entered is not null ? "Leave the boolean first (Esc): its operands are grouped with it, not on their own."
                    : GroupRefusal(SelectedUnits());
        return new Viewer3DMenuItem("Group Objects", why is null ? GroupSelection : null, Enabled: why is null, Gesture: Viewer3DMenuItem.Command(Key.G),
                                    Tip: why ?? "One group of the selection: a click on any of it then selects all of it.");
    }

    /// <summary>Ungroup (Ctrl/Cmd+Shift+G): each group the selection holds whole — the top-most — gives its members, and the
    /// groups inside it, back to where it was. One undo entry.</summary>
    public void UngroupSelection()
    {
        if (IsViewOnly) return;
        var groups = SelectedUnits().Where(u => u.IsGroup).Select(u => u.GroupPath!).ToList();
        if (groups.Count == 0) { StatusMessage = "Nothing selected is a group: select a group (a click on any of it) to ungroup it."; return; }
        Ungroup(groups);
    }

    /// <summary>Ungroups the groups at <paramref name="paths"/> — one undo entry. A path inside another of them is the outer
    /// one's to keep: only the outermost is ungrouped, and the groups inside it stay groups.</summary>
    public void Ungroup(IReadOnlyList<string> paths)
    {
        var outermost = paths.Distinct().Where(p => !paths.Any(q => q != p && C3dGroups.IsIn(p, q))).ToList();
        if (outermost.Count == 0) return;
        var moves = outermost.SelectMany(p => C3dGroups.Ungroup(Document, p)).ToList();
        string what = outermost.Count == 1 ? C3dGroups.NameOf(outermost[0]) : $"{outermost.Count} groups";
        if (SetGroupPaths(moves, $"Ungroup {what}")) StatusMessage = $"Ungrouped {what}.";
    }

    /// <summary>Ungroup one group — a tree row's.</summary>
    public void Ungroup(string path) => Ungroup([path]);

    /// <summary>A group's rename: validated among the groups, one undo entry. Null on success, else why not.</summary>
    public string? RenameGroup(string path, string name)
    {
        name = name.Trim();
        string old = C3dGroups.NameOf(path);
        if (name == old) return null;
        if (C3dGroups.NameRefusal(Document, name, old) is { } why) return why;
        if (!SetGroupPaths(C3dGroups.Rename(Document, path, name), $"Rename {old} to {name}")) return null;
        SelectGroupRow(C3dGroups.ParentOf(path) is { } p ? p + C3dGroups.Separator + name : name);
        return null;
    }

    /// <summary>Writes each member's new path: ONE undo entry of object and instance replacements. False when nothing changed.</summary>
    private bool SetGroupPaths(IReadOnlyList<(C3dMemberRef Member, string? Path)> moves, string description)
    {
        var slots = new List<C3dEditSlot>();
        foreach (var (m, path) in moves.OrderBy(x => x.Member.Instance).ThenBy(x => x.Member.Index))
        {
            if (m.Instance)
            {
                string before = C3dPersistence.SerializeInstance(Document.Instances[m.Index]);
                var copy = C3dPersistence.DeserializeInstance(before);
                copy.Group = path;
                string after = C3dPersistence.SerializeInstance(copy);
                if (after != before) slots.Add(new C3dEditSlot(true, m.Index, before, after));
            }
            else
            {
                string before = C3dPersistence.SerializeObject(Document.Objects[m.Index]);
                var copy = C3dPersistence.DeserializeObject(before);
                copy.Group = path;
                string after = C3dPersistence.SerializeObject(copy);
                if (after != before) slots.Add(new C3dEditSlot(false, m.Index, before, after));
            }
        }
        return slots.Count > 0 && Push(new C3dEdit(description, slots, ApplySlots));
    }

    private string DescribeUnits(IReadOnlyList<C3dGroupUnit> units)
        => units.Count == 1 ? units[0].GroupPath is { } g ? C3dGroups.NameOf(g) : MemberName(units[0].Member) : $"{units.Count} items";

    private string MemberName(C3dMemberRef m) => m.Instance ? Document.Instances[m.Index].Name : Document.Objects[m.Index].Name;

    // ── the group's fields: material, role, corner, visibility, delete ───────────────────────

    private IReadOnlyList<int> GroupObjectIndices(string path)
        => [.. C3dGroups.MembersOf(Document, path).Where(m => !m.Instance).Select(m => m.Index)];

    /// <summary>The group's objects that take a material: all but its polylines (construction geometry).</summary>
    internal IReadOnlyList<int> GroupSolidIndices(string path) => [.. GroupObjectIndices(path).Where(i => Document.Objects[i] is not C3dPolyline)];

    /// <summary>A material for every member that has one to speak of (not a polyline, not an instance): one undo entry.</summary>
    public void SetGroupMaterial(string path, string material)
    {
        if (material == NewMaterialItem) { RequestMaterialPicker(GroupSolidIndices(path), startNew: true); return; }
        var indices = GroupSolidIndices(path);
        if (indices.Count > 0) ChangeObjects($"Material of {C3dGroups.NameOf(path)}", indices, o => SetMaterialOf(o, material));
    }

    /// <summary>A role for every member that has a material: one undo entry.</summary>
    public void SetGroupRole(string path, Em3dRole? role)
    {
        var indices = GroupSolidIndices(path);
        if (indices.Count > 0) ChangeObjects($"Role of {C3dGroups.NameOf(path)}", indices, o => SetRoleOf(o, role));
    }

    /// <summary>A role given to an operation is its Blank's (or Target's), as its material is.</summary>
    internal static void SetRoleOf(C3dObject o, Em3dRole? role)
    {
        if (o is C3dOperation && C3dOperands.Inner(o) is { } inner) SetRoleOf(inner, role);
        else o.Role = role;
    }

    /// <summary>A group's tick: its objects' <c>Hidden</c> (one undo entry); its instances' parts in the view only.</summary>
    public void SetGroupVisible(string path, bool visible)
    {
        var indices = GroupObjectIndices(path);
        if (indices.Count > 0) ChangeHidden($"{(visible ? "Show" : "Hide")} {C3dGroups.NameOf(path)}", indices, _ => !visible);
        foreach (var m in C3dGroups.MembersOf(Document, path).Where(m => m.Instance))
            foreach (var s in SceneObjectsOfMember(m)) Viewer.SetVisibleEverywhere(s.Id, visible);
        if (!visible && !_keepSelectionOnHide) Viewer.SetSelection([]);      // brief-em3d-91 — H keeps it, to show it again
        RefreshTreeVisibility();
    }

    /// <summary>Whether any of a group is shown.</summary>
    private bool GroupVisible(string path)
        => C3dGroups.MembersOf(Document, path).Any(m => m.Instance
            ? SceneObjectsOfMember(m).Any(s => Viewer.View.IsVisible(s.Id))
            : !Document.Objects[m.Index].Hidden);

    /// <summary>Deletes every member of the given groups — objects and instances — as one undo entry.</summary>
    public void DeleteMembers(IReadOnlyList<C3dMemberRef> members, string description)
    {
        var slots = members.Distinct()
            .Where(m => m.Index < (m.Instance ? Document.Instances.Count : Document.Objects.Count))
            .Select(m => new C3dEditSlot(m.Instance, m.Index,
                m.Instance ? C3dPersistence.SerializeInstance(Document.Instances[m.Index]) : C3dPersistence.SerializeObject(Document.Objects[m.Index]), null))
            .ToList();
        if (slots.Count == 0) return;
        Viewer.SetSelection([]);
        Push(new C3dEdit(description, slots, ApplySlots));
    }

    /// <summary>The group's bounding box in DBU — every member's elaborated geometry — or null when none of it is drawn.</summary>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1, bool Exact)? GroupBoundsDbu(string path)
        => BoundsDbu(C3dGroups.MembersOf(Document, path).Select(m => new C3dTarget(m.Instance, m.Index)));

    /// <summary>The Inspector's Corner: every member moved by the same whole-DBU step so the box's corner lands on
    /// <paramref name="text"/> along <paramref name="axis"/>. One undo entry; null on success, else why not.</summary>
    public string? SetGroupCorner(string path, int axis, string text)
    {
        if (GroupBoundsDbu(path) is not { } b) return "None of the group is drawn: there is no corner to move.";
        if (!LayoutUnits.TryParse(text, Document.DisplayUnit, Document.DbuPerMicron, out long want))
            return $"A corner is a length, in {LayoutUnits.Suffix(Document.DisplayUnit)} unless a unit is written.";
        double now = axis switch { 0 => b.X0, 1 => b.Y0, _ => b.Z0 };
        long d = want - (long)Math.Round(now, MidpointRounding.AwayFromZero);
        if (d == 0) return null;
        var by = DrawingPlane.With(default, (C3dAxis)axis, d);
        var targets = C3dGroups.MembersOf(Document, path).Select(m => new C3dTarget(m.Instance, m.Index)).ToList();
        ApplyTransform(targets, C3dTransform.Translation(by), exact: b.Exact, translationOnly: true, $"Move {C3dGroups.NameOf(path)}");
        return null;
    }

    // ── the tree ────────────────────────────────────────────────────────────────────────────

    /// <summary>The Groups section: each top-level group, its groups and then its members beneath it, at any depth. A group's
    /// members are listed here and nowhere else, as a boolean's operands are listed under it.</summary>
    private C3dTreeGroup? GroupsSection()
    {
        if (IsViewOnly || !AnyGroups(Document)) return null;
        var all = C3dGroups.All(Document);
        C3dTreeItem Row(C3dGroupInfo g)
        {
            var members = C3dGroups.MembersOf(Document, g.Path);
            int objects = members.Count(m => !m.Instance), instances = members.Count - objects;
            string detail = string.Join(", ", new[] { Count(objects, "object"), Count(instances, "instance") }.Where(s => s.Length > 0));
            // brief-em3d-93 — a group whose every member is not modelled has a greyed header
            bool anyModelled = members.Count == 0 || members.Any(m => m.Instance ? Document.Instances[m.Index].Model
                                                                                 : Document.Objects[m.Index] is C3dPolyline || Document.Objects[m.Index].Model);
            var row = new C3dTreeItem(this, g.Name, GroupKind, detail, -1, -1, GroupVisible(g.Path))
            {
                GroupPath = g.Path, Icon = Material.Icons.MaterialIconKind.Group, IsModelled = anyModelled,
            };
            foreach (var sub in all.Where(s => s.Parent == g.Path)) row.Children.Add(Row(sub));
            for (int i = 0; i < Document.Objects.Count; i++)
                if (Document.Objects[i].Group == g.Path && PassesTreeFilter(Document.Objects[i])) row.Children.Add(ObjectRow(Document.Objects[i], i, byMaterial: false));
            for (int i = 0; i < Document.Instances.Count; i++)
                if (Document.Instances[i].Group == g.Path && PassesTreeFilter(Document.Instances[i])) row.Children.Add(InstanceRow(Document.Instances[i], i));
            return row;
        }
        var rows = all.Where(g => g.Parent is null).Select(Row).ToList();
        return rows.Count > 0 ? new C3dTreeGroup(GroupsHeader, rows, C3dTreeGroupRole.Groups) : null;

        static string Count(int n, string what) => n == 0 ? "" : n == 1 ? $"1 {what}" : $"{n} {what}s";
    }

    /// <summary>The tree row of the group at <paramref name="path"/>, or null.</summary>
    private C3dTreeItem? GroupRow(string path) => AllTreeItems().FirstOrDefault(t => t.IsGroup && t.GroupPath == path);

    /// <summary>The tree row of a member, or null.</summary>
    private C3dTreeItem? MemberRow(C3dMemberRef m)
        => AllTreeItems().FirstOrDefault(t => !t.IsGroup && t.OperandPath is null && !t.IsFeature && !t.IsReadOnly &&
                                              (m.Instance ? t.InstanceIndex == m.Index : t.ObjectIndex == m.Index));

    /// <summary>The group's row is the selection, and the view selects all of it.</summary>
    private void SelectGroupRow(string path)
    {
        if (GroupRow(path) is not { } row) return;
        _syncingTree = true;
        try
        {
            SetTreeRows([row]);
            if (Viewer.SelectMode != Scene3DSelectMode.Object) Viewer.SelectMode = Scene3DSelectMode.Object;
            Viewer.SetSelection(SceneObjectsOfGroup(path).Select(s => Scene3DItem.OfObject(s.Id)));
        }
        finally { _syncingTree = false; }
        Properties.Reload();
    }

    /// <summary>
    /// Rows for a selection, with every group selected whole shown as its own row instead of its members' — the top-most
    /// such group. Other rows (an operand, a feature, a part, a record) are kept as they are.
    /// </summary>
    private List<C3dTreeItem> CollapseToGroups(IReadOnlyList<C3dTreeItem> rows)
    {
        if (IsViewOnly || !AnyGroups(Document)) return [.. rows];
        var units = C3dGroups.Units(Document, rows.SelectMany(MembersOfRow));
        var unitOf = new Dictionary<C3dMemberRef, C3dGroupUnit>();
        foreach (var u in units)
            if (u.GroupPath is { } g) foreach (var m in C3dGroups.MembersOf(Document, g)) unitOf[m] = u;
            else unitOf[u.Member] = u;
        var result = new List<C3dTreeItem>();
        foreach (var r in rows)
        {
            var members = MembersOfRow(r);
            if (members.Count == 0) { if (!result.Contains(r)) result.Add(r); continue; }
            foreach (var m in members)
            {
                var row = unitOf.TryGetValue(m, out var u) && u.GroupPath is { } g ? GroupRow(g) ?? r : r.IsGroup ? MemberRow(m) ?? r : r;
                if (!result.Contains(row)) result.Add(row);
            }
        }
        return result;
    }

    // ── menus and keys ──────────────────────────────────────────────────────────────────────

    /// <summary>The view's menu (and a multi-row tree menu): Group Objects, and Ungroup for the group the selection is.</summary>
    private IEnumerable<Viewer3DMenuItem> GroupMenuItems()
    {
        if (IsViewOnly || _entered is not null || Viewer.SelectMode != Scene3DSelectMode.Object) yield break;
        var units = SelectedUnits();
        if (units.Count == 0) yield break;
        yield return GroupObjectsItem();
        var groups = units.Where(u => u.IsGroup).Select(u => u.GroupPath!).ToList();
        if (groups.Count > 0)
            yield return new Viewer3DMenuItem(groups.Count == 1 ? $"Ungroup {C3dGroups.NameOf(groups[0])}" : $"Ungroup {groups.Count} groups",
                () => Ungroup(groups), Tip: "Only the outermost group: the groups inside it stay groups.",
                Gesture: Viewer3DMenuItem.Command(Key.G, KeyModifiers.Shift));
    }

    /// <summary>A group's row in the tree: the canvas's functions for all of it.</summary>
    private IEnumerable<Viewer3DMenuItem> GroupTreeItems(C3dTreeItem item)
    {
        string path = item.GroupPath!;
        var scene = SceneObjectsOfGroup(path);
        bool drawn = scene.Count > 0;
        yield return new Viewer3DMenuItem($"Ungroup {item.Name}", () => Ungroup(path), Gesture: Viewer3DMenuItem.Command(Key.G, KeyModifiers.Shift),
            Tip: "This group only: the groups inside it stay groups.");
        yield return GroupObjectsItem();
        yield return Viewer3DMenuItem.Separator;
        foreach (var op in RowOperationItems(new Viewer3DMenuItem("Duplicate", StartDuplicate, Enabled: drawn, Gesture: Viewer3DMenuItem.Command(Key.D),
                     Tip: drawn ? "A copy of the group — a new group — in place, then a Move." : "None of it is drawn.")))
            yield return op;
        yield return new Viewer3DMenuItem("Delete", () => DeleteMembers(C3dGroups.MembersOf(Document, path), $"Delete {item.Name}"),
            Tip: "The group and everything in it.");
        yield return Viewer3DMenuItem.Separator;
        yield return new Viewer3DMenuItem("Rename…", () => ShowProperties(rename: true));
        var mats = Materials;
        var objects = GroupSolidIndices(path);
        yield return new Viewer3DMenuItem("Material",
            Children: [.. mats.Select(m => new Viewer3DMenuItem(m, () => SetGroupMaterial(path, m))),
                       .. mats.Count > 0 ? [Viewer3DMenuItem.Separator] : Array.Empty<Viewer3DMenuItem>(),
                       new Viewer3DMenuItem(NewMaterialItem, () => RequestMaterialPicker(objects, startNew: true),
                           Tip: "Make a material in the technology or one of its libraries, and give it to every object in the group.")]);
        yield return new Viewer3DMenuItem("Assign Material…", () => RequestMaterialPicker(objects, startNew: false),
            Tip: "One material for every object in the group.");
        bool visible = GroupVisible(path);
        yield return new Viewer3DMenuItem(visible ? "Hide" : "Show", () => SetGroupVisible(path, !visible));
        if (drawn) yield return new Viewer3DMenuItem("Isolate", () => Viewer.Isolate(scene));
        if (ModelItem(C3dGroups.MembersOf(Document, path), item.Name) is { } model) yield return model;    // brief-em3d-93
    }

    /// <summary>Ctrl/Cmd+G groups the selection; Ctrl/Cmd+Shift+G ungroups it.</summary>
    private bool GroupKey(Key key, KeyModifiers modifiers)
    {
        if (key != Key.G || (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || (modifiers & KeyModifiers.Alt) != 0) return false;
        if ((modifiers & KeyModifiers.Shift) != 0) UngroupSelection(); else GroupSelection();
        return true;
    }

    // ── units of an operation's targets ─────────────────────────────────────────────────────

    /// <summary>
    /// The targets as Align moves them: each group the targets hold whole is ONE thing, lined up by its own box and moved
    /// as one; anything else alone. In the targets' order.
    /// </summary>
    private List<(string? Group, List<C3dTarget> Targets)> TargetUnits(IReadOnlyList<C3dTarget> targets)
    {
        var members = targets.Where(t => t.Instance || !IsOperandIndex(t.Index)).Select(t => new C3dMemberRef(t.Instance, t.Index)).ToList();
        var groups = AnyGroups(Document) ? C3dGroups.Units(Document, members).Where(u => u.IsGroup).Select(u => u.GroupPath!).ToList() : [];
        var result = new List<(string? Group, List<C3dTarget> Targets)>();
        foreach (var t in targets)
        {
            string? g = groups.Count > 0 && (t.Instance || !IsOperandIndex(t.Index))
                ? groups.FirstOrDefault(p => C3dGroups.IsIn(C3dGroups.PathOf(Document, new C3dMemberRef(t.Instance, t.Index)), p)) : null;
            if (g is not null && result.FindIndex(r => r.Group == g) is >= 0 and var k) result[k].Targets.Add(t);
            else result.Add((g, [t]));
        }
        return result;
    }

    /// <summary>The groups a Duplicate or an Array copies whole: each set of copies makes new ones (<see cref="C3dGroups.CopyPaths"/>).</summary>
    private IReadOnlyList<C3dGroupUnit> CopiedGroupUnits(IReadOnlyList<C3dTarget> targets)
        => AnyGroups(Document)
            ? [.. C3dGroups.Units(Document, targets.Where(t => t.Instance || !IsOperandIndex(t.Index)).Select(t => new C3dMemberRef(t.Instance, t.Index)))
                           .Where(u => u.IsGroup)]
            : [];
}

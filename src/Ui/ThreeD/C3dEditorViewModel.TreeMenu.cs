// 3D editor round 1 — the object tree's context menu. Right-clicking a node selects it (the scene and the Properties
// Inspector follow), then offers what the canvas offers for it, through the SAME functions the canvas menu, the keys and
// the 3D menu call — Duplicate, Delete, Rename, Material, Hide, Isolate, Show All, Properties, and a group's Ungroup — never a
// second copy.
// What a node cannot do is shown disabled with the reason, so the answer is readable (the air box, an instance's part).

using Avalonia.Input;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The tree's menu for <paramref name="item"/>, which the caller has just made the selection.</summary>
    public IReadOnlyList<Viewer3DMenuItem> TreeMenuItems(C3dTreeItem item) => WithClipboard(WithHideShowSelection(TreeMenuItemsOf(item)), [item]);

    /// <summary>brief-em3d-95 — the tree's menu on several selected rows: the canvas's (Copy Objects among it), and Paste.</summary>
    public IReadOnlyList<Viewer3DMenuItem> TreeSelectionMenuItems() => [.. Viewer.ContextMenuItems(), Viewer3DMenuItem.Separator, PasteItem()];

    /// <summary>brief-em3d-95 — the tree's empty area, and a section's header after its own items: Paste.</summary>
    public IReadOnlyList<Viewer3DMenuItem> TreeEmptyMenuItems(IReadOnlyList<Viewer3DMenuItem>? before = null)
        => before is { Count: > 0 } ? [.. before, Viewer3DMenuItem.Separator, PasteItem()] : [PasteItem()];

    /// <summary>brief-em3d-95 — Copy and Paste first under a row's title (every row: a field plot's Copy says why it is disabled).</summary>
    private IReadOnlyList<Viewer3DMenuItem> WithClipboard(IReadOnlyList<Viewer3DMenuItem> items, IReadOnlyList<C3dTreeItem> rows)
    {
        if (IsViewOnly) return items;                      // a setup's view shows the design: nothing to copy from or paste into
        var list = items.ToList();
        int at = list.Count >= 2 && !list[0].Enabled && list[1].IsSeparator ? 2 : 0;
        list.InsertRange(at, [CopyItem(rows), PasteItem(), Viewer3DMenuItem.Separator]);
        return list;
    }

    private IReadOnlyList<Viewer3DMenuItem> TreeMenuItemsOf(C3dTreeItem item)
    {
        if (item.Kind == FieldPlotKind) return FieldPlotMenuItems(item);    // brief-em3d-83 — in a setup's view too
        if (IsViewOnly) return ViewTreeMenuItems(item);
        var items = new List<Viewer3DMenuItem> { new(item.Label, Enabled: false), Viewer3DMenuItem.Separator };
        // brief-em3d-101 R-em3d101-9b — a face image's row: the record's own items
        if (item.Kind == FaceImageKind) { items.AddRange(FaceImageRowItems(item)); return items; }
        var scene = SceneObjectsOfNode(item);

        if (item.IsAirBox)
        {
            const string why = "The air box is the active setup's, not the geometry's: it cannot be duplicated or deleted. " +
                               "Its boundaries and padding are in the Properties Inspector.";
            items.Add(new Viewer3DMenuItem("Duplicate", Enabled: false, Tip: why));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Delete", Enabled: false, Tip: why));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem(AirBoxShown ? "Hide" : "Show", () => SetRowsVisible([item], !AirBoxShown, $"{(AirBoxShown ? "Hide" : "Show")} {item.Name}")));
        }
        else if (item.Kind is "Port" or TerminalKind)
        {
            var port = PortOfRow(item);
            if (port is not null) items.Add(PortMoveItem([port]));   // brief-dfr34-1 R-dfr34-1-1
            items.Add(new Viewer3DMenuItem("Duplicate", Enabled: false, Tip: "A port is drawn with the Port tool: each one joins its own pair of conductors."));
            if (port is not null && item.Kind == "Port") items.AddRange(PortValueItems(port));   // Z0, Number, Name
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Delete", port is null ? null : () => DeletePorts([port]), Enabled: port is not null));
            if (port is not null) items.Add(PortModelItem([port]));    // brief-em3d-93
        }
        // brief-sar-3 — a lumped element's row: rename, model, hide, delete.
        else if (item.Kind == LumpedElementKind)
        {
            items.AddRange(LumpedTreeItems(item));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Properties", () => ShowProperties(rename: false)));
            return items;
        }
        // brief-em3d-75 R-em3d75-1b — a thermal place's row: rename, hide, delete (and a line probe's plot); a thermal
        // boundary's row: delete, from the active thermal setup.
        else if (item.Kind is HeatSourceKind or ProbeKind or MeshRegionKind or ThermalBoundaryKindName or EffectiveBlockKind or SymmetryPlaneKind)
        {
            items.AddRange(ThermalTreeItems(item));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Properties", () => ShowProperties(rename: false)));
            return items;
        }
        else if (item.Kind == "Boundary")
        {
            var b = Document.FaceBoundaries.FirstOrDefault(f => Scene3DBuilder.FaceTintPrefix + f.Object + "/" + f.Face == item.Name);
            items.Add(new Viewer3DMenuItem("Duplicate", Enabled: false, Tip: "A boundary belongs to one named face."));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Delete", b is null ? null : () => StatusMessage = SetFaceBoundary(b.Object, b.Face, null) ?? "",
                                           Enabled: b is not null));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem(item.IsVisible ? "Hide" : "Show", () => SetRowsVisible([item], !item.IsVisible, $"{(item.IsVisible ? "Hide" : "Show")} {item.Name}")));
        }
        else if (item.IsGroup) items.AddRange(GroupTreeItems(item));
        else if (item.OperandPath is { } path && item.ObjectIndex >= 0 && item.ObjectIndex < Document.Objects.Count)
            items.AddRange(OperandTreeItems(item, path));
        // brief-em3d-67 R-em3d67-6b — a fillet's or chamfer's row: the inspector's own functions.
        else if (item.FeaturePath is { } fp && item.ObjectIndex >= 0 && item.ObjectIndex < Document.Objects.Count
                 && C3dFillets.At(Document.Objects[item.ObjectIndex], fp) is C3dOperation feature)
        {
            int top = item.ObjectIndex;
            string? kernel = KernelMissing(feature is C3dChamfer ? "Chamfer" : "Fillet");
            items.Add(new Viewer3DMenuItem(feature.Enabled ? "✓ Enabled" : "Enabled", () => SetFeatureEnabled(top, fp, !feature.Enabled),
                Enabled: kernel is null, Tip: kernel ?? "Unticked, the solid elaborates unrounded — a cheap A/B for the simulation."));
            items.Add(new Viewer3DMenuItem("Show Edges", () => ShowFeatureEdges(top, fp)));
            items.Add(new Viewer3DMenuItem("Edit Edges…", () => EditFeature(top, fp), Enabled: kernel is null, Tip: kernel));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Remove", () => RemoveFeature(top, fp),
                Tip: "The solid returns unrounded, in its place and under its name."));
        }
        // brief-sar-3 — a phantom layer's row: its phantom's Flatten; the layer itself is edited on the phantom's page.
        else if (item.Kind == PhantomLayerKind)
        {
            int pi = Document.Objects.FindIndex(o => o is C3dPhantom && item.Name.StartsWith(o.Name + "/", StringComparison.Ordinal));
            if (pi >= 0) items.Add(FlattenPhantomItem(pi));
            items.Add(new Viewer3DMenuItem("Properties", () => ShowProperties(rename: false)));
            return items;
        }
        else if (item.IsReadOnly)
        {
            const string why = "Part of an instance: it belongs to its own cell. Push into the cell to edit it.";
            items.Add(new Viewer3DMenuItem("Duplicate", Enabled: false, Tip: why));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Delete", Enabled: false, Tip: why));
            // brief-em3d-94 — a placed cell's part edits its material where that cell's technology defines it.
            if (EditMaterialItem(item) is { } edit) { items.Add(Viewer3DMenuItem.Separator); items.Add(edit); }
            // its instance's own entries (Flatten among them), as the canvas offers them for the part
            if (TreeInstanceItems(item) is { Count: > 0 } hierarchy) { items.Add(Viewer3DMenuItem.Separator); items.AddRange(hierarchy); items.Add(Viewer3DMenuItem.Separator); }
        }
        else
        {
            bool drawn = scene.Count > 0;
            items.Add(new Viewer3DMenuItem("Duplicate", StartDuplicate, Enabled: drawn, Gesture: Viewer3DMenuItem.Command(Key.D),
                Tip: drawn ? "A copy in place, then a Move: click where it goes, or Esc to leave it where it is."
                           : "Not drawn (elaboration refused it — see Properties): there is nothing to place yet."));
            // 3D editor groups — disabled for this row alone (a group holds two things or more), with the reason.
            items.Add(GroupObjectsItem());
            items.Add(Viewer3DMenuItem.Separator);
            if (item.ObjectIndex >= 0)
                items.Add(new Viewer3DMenuItem("Delete", () => DeleteObjects([item.ObjectIndex])));
            else if (item.InstanceIndex >= 0)
                items.Add(new Viewer3DMenuItem("Delete", () => DeleteInstance(item.InstanceIndex)));
            items.Add(Viewer3DMenuItem.Separator);
            if (item.ObjectIndex >= 0)
            {
                int index = item.ObjectIndex;
                // brief-em3d-66 R-em3d66-3d — a boolean's own items first, the functions the canvas and the inspector call.
                if (Document.Objects[index] is C3dBoolean)
                {
                    items.AddRange(BooleanTreeItems(item));
                    items.Add(Viewer3DMenuItem.Separator);
                }
                // brief-em3d-101 R-em3d101-3c — an image sheet's own items, the functions the canvas's menu calls.
                if (ImageSheetAt(index) is not null)
                {
                    items.AddRange(ImageItems(index));
                    items.Add(Viewer3DMenuItem.Separator);
                }
                // brief-em3d-68 R-em3d68-5a — an imported part's (or a boolean holding one) Reload from Source.
                if (StepAt(index) is { } step)
                {
                    items.Add(ReloadItem(step));
                    // brief-em3d-129 R-em3d129-3a — enabled for a top-level Step object of several solids; else says which condition failed.
                    items.Add(SplitItem(SplitRefusal(index), () => _ = SplitIntoSolidsAsync(index)));
                    // brief-em3d-130 — what a piece can become that the face and vertex editor edits.
                    items.AddRange(ConvertItems(index));
                    items.Add(Viewer3DMenuItem.Separator);
                }
                items.Add(new Viewer3DMenuItem("Rename…", () => ShowProperties(rename: true)));
                // brief-sar-3 — a phantom's materials are its layers', chosen on its Inspector page
                if (Document.Objects[index] is C3dPhantom) items.Add(FlattenPhantomItem(index));
                var mats = Materials;
                // brief-em3d-53 — the list ends in New Material…, so a technology with none is not a dead end.
                if (Document.Objects[index] is not C3dPhantom)
                items.Add(new Viewer3DMenuItem("Material",
                    Children: [.. mats.Select(m => new Viewer3DMenuItem(m, () => ChangeObjects($"Material of {Document.Objects[index].Name}", [index], o => SetMaterialOf(o, m)))),
                               .. mats.Count > 0 ? [Viewer3DMenuItem.Separator] : Array.Empty<Viewer3DMenuItem>(),
                               new Viewer3DMenuItem(NewMaterialItem, () => RequestMaterialPicker([index], startNew: true),
                                   Tip: "Make a material in the technology or one of its libraries, and give it to this object.")]));
                if (Document.Objects[index] is not C3dPhantom)
                items.Add(new Viewer3DMenuItem("Assign Material…", () => RequestMaterialPicker([index], startNew: false),
                    Tip: "Choose this object's material from its technology's materials — each with its role and source — or make a new one."));
                if (EditMaterialItem(item) is { } edit) items.Add(edit);    // brief-em3d-94
                bool hidden = Document.Objects[index].Hidden;
                items.Add(new Viewer3DMenuItem(hidden ? "Show" : "Hide", () => SetRowsVisible([item], hidden, $"{(hidden ? "Show" : "Hide")} {item.Name}")));
            }
            else if (drawn) items.Add(new Viewer3DMenuItem("Hide", () => SetRowsVisible([item], false, $"Hide {item.Name}")));
            if (drawn) items.Add(new Viewer3DMenuItem("Isolate", () => Viewer.Isolate(scene)));
            if (ModelItem(MembersOfRow(item), item.Name) is { } model) items.Add(model);    // brief-em3d-93
            // an instance's row: Push Into, Swap View, Flatten, Flatten All Levels, Rename — the canvas's own entries
            if (TreeInstanceItems(item) is { Count: > 0 } hierarchy) { items.Add(Viewer3DMenuItem.Separator); items.AddRange(hierarchy); items.Add(Viewer3DMenuItem.Separator); }
        }
        items.Add(new Viewer3DMenuItem("Show All", Viewer.ShowAll));
        items.Add(Viewer3DMenuItem.Separator);
        items.Add(new Viewer3DMenuItem("Properties", () => ShowProperties(rename: false)));
        return items;
    }

    /// <summary>brief-em3d-66 R-em3d66-3d — an operand's row: Rename…, Material, Make Blank (a Tool), Remove from Boolean (a
    /// Tool, when more than one remains), Hide — and, when the operand is itself a boolean, that boolean's items.</summary>
    private IEnumerable<Viewer3DMenuItem> OperandTreeItems(C3dTreeItem item, string path)
    {
        int top = item.ObjectIndex;
        var root = Document.Objects[top];
        if (C3dBooleans.At(root, path) is not { } operand || C3dBooleans.At(root, C3dBooleans.ParentPath(path)) is not C3dBoolean parent) yield break;
        int step = C3dBooleans.LastStep(path);
        foreach (var b in BooleanTreeItems(item)) yield return b;
        yield return new Viewer3DMenuItem("Rename…", () => { SelectOperand(top, path); ShowProperties(rename: true); }, Enabled: step >= 0,
            Tip: step >= 0 ? null : "A Blank takes its boolean's name: rename the boolean.");
        var mats = Materials;
        yield return new Viewer3DMenuItem("Material",
            Children: [.. mats.Select(m => new Viewer3DMenuItem(m, () => ChangeOperand($"Material of {item.Name}", top, path, o => SetMaterialOf(o, m))))]);
        if (step >= 0)
        {
            yield return new Viewer3DMenuItem("Make Blank", () => MakeBlank(top, C3dBooleans.ParentPath(path), step),
                Tip: "This Tool becomes the Blank, and the Blank a Tool in its place: the boolean takes this one's name.");
            bool more = parent.Tools.Count > 1;
            yield return new Viewer3DMenuItem("Remove from Boolean", () => RemoveFromBoolean(top, path), Enabled: more,
                Tip: more ? "It becomes a top-level object right after the boolean, where the boolean put it."
                          : "A boolean needs a Tool: this is its last one. Dissolve it instead.");
        }
        yield return new Viewer3DMenuItem(operand.Hidden ? "Show" : "Hide",
            () => SetRowsVisible([item], operand.Hidden, $"{(operand.Hidden ? "Show" : "Hide")} {item.Name}"));
        yield return Viewer3DMenuItem.Separator;
        yield return new Viewer3DMenuItem("Delete", step >= 0 && parent.Tools.Count > 1 ? () => DeleteTool(top, path) : null,
            Enabled: step >= 0 && parent.Tools.Count > 1,
            Tip: step < 0 ? C3dBooleans.BlankNeeded : parent.Tools.Count > 1 ? "Removed from the boolean and deleted." : "A boolean needs a Tool: this is its last one. Dissolve it instead.");
    }

    /// <summary>brief-em3d-66 — a material given to an operation is its Blank's (or Target's): the result is made of it.</summary>
    internal static void SetMaterialOf(C3dObject o, string material)
    {
        if (o is C3dOperation && C3dOperands.Inner(o) is { } inner) SetMaterialOf(inner, material);
        else if (o.Material != material) (o.Material, o.DrawingLayer) = (material, null);   // a flattened layer's colour was its old material's
    }

    /// <summary>The scene objects a tree node stands for: an instance's parts, or the node's own object.</summary>
    private IReadOnlyList<Scene3DObject> SceneObjectsOfNode(C3dTreeItem item)
    {
        if (item.IsAirBox) return AirBoxFaceObjects();
        if (item.IsGroup) return SceneObjectsOfGroup(item.GroupPath!);
        // brief-sar-3 — a phantom's row is its layers' solids
        bool phantom = item.ObjectIndex >= 0 && item.ObjectIndex < Document.Objects.Count && item.OperandPath is null && Document.Objects[item.ObjectIndex] is C3dPhantom;
        var names = item.InstanceIndex >= 0 || phantom ? item.Children.Select(c => c.Name) : [item.Name];
        return [.. names.Select(SceneObject).OfType<Scene3DObject>()];
    }

    /// <summary>Deletes the placed instance at <paramref name="index"/>: one undo entry. What it contributed goes with it;
    /// its cell is untouched.</summary>
    public void DeleteInstance(int index)
    {
        if (index < 0 || index >= Document.Instances.Count) return;
        var inst = Document.Instances[index];
        Viewer.SetSelection([]);
        Push(new C3dEdit($"Delete {inst.Name}", [new C3dEditSlot(true, index, CircuitRF.Design.ThreeD.C3dPersistence.SerializeInstance(inst), null)],
                         ApplySlots));
    }
}

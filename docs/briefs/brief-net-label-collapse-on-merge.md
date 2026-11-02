# Brief: schematic — collapse duplicate labels when nets merge

**Apply after** `brief-net-label-one-per-node` (reuses its `AddGeometricUnions` helper and NetExtractor's
private `UnionFind` / `FindLabelNetKey`). Builds on Brief 2's `DotRevalidationCommand` net-label pass.

Problem: the one-per-node guard stops a *second* label being created on a labeled node, but a geometry
edit (drawing a wire between two labeled nets, dragging a segment/endpoint to bridge them, pasting,
moving a component that bridges) can still merge two already-labeled nets into one node — leaving two
labels on one net, so the netlist no longer matches the schematic.

Fix: after every edit, in the central post-edit pass, find any physical node carrying more than one label
and keep just one (the first in `NetLabels` order), removing the rest. Same connectivity as extraction.
Folded into the same undo as the triggering edit; a warning names any distinct label removed.

Size: **S**. Files: `NetExtractor.cs`, `DotRevalidationCommand.cs`, `SchematicViewModel.cs`.

## 1. `NetExtractor.cs` — group labels that share a node

```csharp
/// <summary>
/// Groups the model's net labels by physical node (wires connected via shared vertices, T-junctions,
/// or dot crossings — the same connectivity as extraction). Returns ONLY nodes carrying more than one
/// label — the merge set the editor must collapse. Each group is in NetLabels (creation) order, so
/// element [0] is the label to keep.
/// </summary>
public static List<List<EditableNetLabel>> LabelsSharingNode(SchematicEditModel model)
{
    var result = new List<List<EditableNetLabel>>();
    if (model.NetLabels.Count < 2) return result;

    double gs = model.GridSize;
    (long, long) QK(double x, double y) => ((long)Math.Round(x / gs), (long)Math.Round(y / gs));

    var uf = new UnionFind();
    AddGeometricUnions(model, QK, uf);   // shared with ExtractModel + FindNodeLabel

    var byRoot = new Dictionary<(long, long), List<EditableNetLabel>>();
    foreach (var lbl in model.NetLabels)   // NetLabels order preserved into each group
    {
        var k = FindLabelNetKey(uf, QK, gs, model, lbl.X, lbl.Y);
        if (k is null || !uf.Contains(k.Value)) continue;
        var root = uf.Find(k.Value);
        if (!byRoot.TryGetValue(root, out var list)) byRoot[root] = list = [];
        list.Add(lbl);
    }

    foreach (var g in byRoot.Values)
        if (g.Count > 1) result.Add(g);
    return result;
}
```

## 2. `DotRevalidationCommand.cs` — collapse duplicates + warn (in the same undo)

Add a message sink so the pass can report a merge:
```csharp
    private readonly IMessageSink? _sink;
```
```csharp
    public DotRevalidationCommand(SchematicEditModel model, IUiCommand inner, IMessageSink? sink = null)
    {
        _model = model;
        _inner = inner;
        _sink  = sink;
    }
```
Add `using CircuitRF.Ui.Messages;` at the top.

In `Execute`, immediately **after** the existing net-label block (`var nl = _model.RevalidateNetLabels(); …`)
and **before** the `NotifyChanged` guard, add:
```csharp
        // One label per node: a geometry edit may have merged two already-labeled nets onto one
        // physical node (e.g. a wire drawn between them). Keep the first label on each such node
        // (NetLabels order) and remove the rest, so the schematic stays unambiguous and the netlist
        // matches it. Same connectivity as extraction; folded into THIS undo via _removedLabels.
        foreach (var group in NetExtractor.LabelsSharingNode(_model))
        {
            var keep = group[0];
            for (int gi = group.Count - 1; gi >= 1; gi--)
            {
                var extra = group[gi];
                int idx = _model.NetLabels.IndexOf(extra);
                if (idx < 0) continue;
                _removedLabels.Add((extra, idx));
                _model.NetLabels.RemoveAt(idx);
                if (!string.Equals(extra.Name, keep.Name, StringComparison.Ordinal))
                    _sink?.Warning($"Net '{extra.Name}' merged into '{keep.Name}'; label '{extra.Name}' removed.");
            }
        }
```
The existing `NotifyChanged` guard already includes `_removedLabels.Count > 0`, and `Undo` already
re-inserts `_removedLabels` (before `_inner.Undo()`), so the removed labels are restored on undo with no
further change.

## 3. `SchematicViewModel.cs` — pass the sink into the wrap

```csharp
    public void Execute(IUiCommand cmd) => _undoRedo.Execute(new DotRevalidationCommand(EditModel, cmd, _messageSink));
```

## Verification

1. Label net A "VIN" and a disjoint net B "VOUT". Draw a wire joining them → they become one net; "VOUT"
   is removed, "VIN" remains, and a warning reports the merge. The netlist shows one net named "VIN".
2. **Undo** → the joining wire is removed **and** "VOUT" is restored, both nets labeled again. Redo repeats.
3. Bridge two labeled nets by **dragging a segment/endpoint** (not just drawing) → same collapse + warn.
   Same for **paste** that overlaps a labeled net and for **moving a component** that bridges them.
4. Joining two nets labeled with the **same** name → collapses to one silently (no warning; netlist
   unchanged).
5. Same name on two nets left **disjoint** → untouched (the §2.1.6 same-name-merge feature still works).
6. Edits that don't merge anything (component nudge, parameter edit) → no labels touched, no warning.

## Acceptance

- After any edit, no physical node holds more than one net label; merges of two labeled nets resolve to a
  single label (first in creation order) with a warning when a distinctly-named label is removed.
- The collapse is one undoable step with the triggering edit and uses the same connectivity as extraction.
- Same-name-on-disjoint-nets, the one-per-node creation guard, and net extraction output are all unchanged.

## Notes

- **Keep rule:** the earliest-created label on the node wins; the user can undo (which also undoes the
  connection) or delete/rename if they wanted the other name. If you'd prefer a prompt or a "block the
  connection instead" behavior, that's a small swap — say which.
- **Shorts:** intentionally still allowed (per your call) — two nets joined only through a shorted
  component are not one node here; that produces a valid netlist that matches the schematic.
- **Cost:** `LabelsSharingNode` builds an O(N) connectivity pass per edit, gated to run only when ≥2
  labels exist; it runs at commit time (not per drag-frame), alongside the existing render rebuild.

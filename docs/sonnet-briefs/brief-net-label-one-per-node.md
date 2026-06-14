# Brief: schematic — one net label per electrical node

Prevent a second net label on a node that already has one. A "node" = wires connected via shared
vertices, T-junctions, or dot-gated crossings — the **same** connectivity the netlist extractor uses,
so the editor rule and extraction never disagree.

Mechanism: net labels are created only by double-clicking a wire. Make that path, when the clicked
wire's node already carries a label, **edit the existing label** instead of placing a new one. A second
label then cannot be created. Double-clicking anywhere on a labeled net edits that net's label.

Size: **S**. Files: `NetExtractor.cs`, `SchematicViewModel.cs`.

## 1. `NetExtractor.cs` — share the geometric union, add a node-label query

### 1a. Extract the geometric unions into a shared helper (single source of truth)

`ExtractModel` currently does wire-vertex + T-junction + crossing unions inline. Move that block
verbatim into a private helper so the editor reuses the identical logic. Add:

```csharp
/// <summary>
/// Adds the GEOMETRIC connectivity unions to <paramref name="uf"/>: wire-vertex chains, T-junction
/// auto-dots, and user-dot 4-way crossings — all from ComputeConnectivityGeometry, the single source
/// of connectivity truth. Shared by ExtractModel and FindNodeLabel so the editor's one-label-per-node
/// rule matches extraction. Does not seed component pins, shorts, or label unions; callers add those.
/// </summary>
private static void AddGeometricUnions(
    SchematicEditModel model, Func<double, double, (long, long)> QK, UnionFind uf)
{
    // Wire vertices; consecutive vertices of one wire = one net.
    foreach (var wire in model.Wires)
    {
        var pts = wire.Points;
        if (pts.Count == 0) continue;
        var first = QK(pts[0].X, pts[0].Y);
        uf.Add(first);
        for (int i = 1; i < pts.Count; i++)
        {
            var next = QK(pts[i].X, pts[i].Y);
            uf.Add(next);
            uf.Union(first, next);
            first = next;
        }
    }

    var cg = model.ComputeConnectivityGeometry();

    // T-junction unions: an auto-dot key (a wire endpoint on another wire's interior) unions with it.
    foreach (var autoDotKey in cg.AutoDotKeys)
    {
        double wx = autoDotKey.Item1 * model.GridSize;
        double wy = autoDotKey.Item2 * model.GridSize;
        foreach (var wire in model.Wires)
        {
            var pts = wire.Points;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                if (SchematicGeometry.PointOnSegmentInterior(wx, wy,
                        pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y,
                        SchematicEditModel.ConnectTolerance))
                {
                    uf.Union(autoDotKey, QK(pts[i].X, pts[i].Y));
                    break;
                }
            }
        }
    }

    // User-dot crossing unions: a dot-gated 4-way crossing connects the wires through it.
    foreach (var dot in model.Dots)
    {
        if (!cg.IsCrossingAtDot(dot.X, dot.Y)) continue;
        (long, long)? firstKey = null;
        foreach (var wire in model.Wires)
        {
            var pts = wire.Points;
            bool onInterior = false;
            for (int i = 0; i < pts.Count - 1 && !onInterior; i++)
                if (SchematicGeometry.PointOnSegmentInterior(dot.X, dot.Y,
                        pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y,
                        SchematicEditModel.ConnectTolerance))
                    onInterior = true;
            if (!onInterior) continue;
            var wireKey = QK(pts[0].X, pts[0].Y);
            if (firstKey is null) firstKey = wireKey;
            else uf.Union(firstKey.Value, wireKey);
        }
    }
}
```

In `ExtractModel`, **replace** the existing wire-vertex block, the `var cg = model.ComputeConnectivityGeometry();`
line, the T-junction `foreach (var autoDotKey in cg.AutoDotKeys)` block, and the user-dot
`foreach (var dot in model.Dots)` crossing block (the three consecutive blocks, in that order) with a
single call, keeping the pin seeding before it and the short/label unions after it unchanged:

```csharp
        // Geometric connectivity (wire vertices + T-junctions + crossing dots), shared with the
        // editor's one-label-per-node rule so the two never disagree.
        AddGeometricUnions(model, QK, uf);
```
(`cg` is used only by those two unioning blocks in `ExtractModel`; nothing later references it.)

**Verify extraction is unchanged:** netlist a schematic that exercises a plain wire net, a T-junction,
and a dot crossing before and after the refactor — the emitted nets/instances must be identical.

### 1b. Add the node-label query

```csharp
/// <summary>
/// Returns a net label already present on the same electrical node as wire <paramref name="wireId"/>
/// (connected via shared vertices, T-junctions, or dot crossings), excluding the label whose Id is
/// <paramref name="exceptId"/>; null if the node carries no other label. Uses the same connectivity
/// as extraction. The editor uses this to keep at most one label per node.
/// </summary>
public static EditableNetLabel? FindNodeLabel(
    SchematicEditModel model, string wireId, string? exceptId = null)
{
    var target = model.FindWire(wireId);
    if (target is null || target.Points.Count == 0) return null;

    double gs = model.GridSize;
    (long, long) QK(double x, double y) => ((long)Math.Round(x / gs), (long)Math.Round(y / gs));

    var uf = new UnionFind();
    AddGeometricUnions(model, QK, uf);

    var targetKey = QK(target.Points[0].X, target.Points[0].Y);
    if (!uf.Contains(targetKey)) return null;
    var targetRoot = uf.Find(targetKey);

    foreach (var lbl in model.NetLabels)
    {
        if (exceptId is not null && lbl.Id == exceptId) continue;
        var k = FindLabelNetKey(uf, QK, gs, model, lbl.X, lbl.Y);   // reuse existing private helper
        if (k is null || !uf.Contains(k.Value)) continue;
        if (uf.Find(k.Value) == targetRoot) return lbl;
    }
    return null;
}
```

## 2. `SchematicViewModel.cs` — route a labeled node to its existing label

In `BeginWireNodeLabelEdit`, after the proximity lookup, fall back to the node's label:
```csharp
        var existing = EditModel.NetLabels.FirstOrDefault(l =>
            Math.Abs(l.X - worldX) < 150 && Math.Abs(l.Y - worldY) < 80);
        // One label per electrical node: if the clicked wire's node already carries a label —
        // here, or elsewhere on the node via a T-junction / crossing — edit THAT label instead of
        // creating a second one. Makes a duplicate impossible by construction.
        existing ??= NetExtractor.FindNodeLabel(EditModel, wireId);
        _inlineEditExistingNetLabel = existing;
```
No change to `CommitInlineEdit`: its new-label branch now runs only when the node truly has no label,
its rename branch edits the resolved existing label, and its empty-text branch deletes it. (Add a
`using CircuitRF.Ui.Schematic;` if not already present — it is.)

## Verification

1. Label a wire "VIN". Double-click the **same segment** again → the box pre-fills "VIN" and edits it;
   no second label appears.
2. Double-click a **different segment of the same wire** → edits "VIN" (one label for the wire).
3. Double-click a different wire that **T-junctions** into the VIN net → edits "VIN".
4. Two wires crossing with a **junction dot**, one labeled → double-clicking the other edits the label.
   Remove the dot (no crossing) → the two wires are separate nodes again and each may have its own label.
5. Same name on **two physically-disjoint** wires still works (the §2.1.6 same-name merge is unaffected —
   different nodes, `FindNodeLabel` returns null for each).
6. Clearing the text on any of the above deletes the single label (existing behaviour).

## Acceptance

- A physical node never holds more than one net label; double-clicking anywhere on a labeled node edits
  that node's label.
- Node membership is computed from the same connectivity as extraction (wires + T-junctions + crossings).
- Same-name-on-disjoint-nets and all existing label rename/delete behaviour are unchanged; extraction
  output is unchanged by the refactor.

## Notes / not in scope

- **Shorts:** two wires joined only through a shorted component are one net to the extractor but are not
  treated as one node here (geometric connectivity only). That rare case is still caught by the existing
  extraction-time "Net conflict: labels 'A' and 'B' on same net" warning.
- **Merging two already-labeled nets** (e.g. drawing a wire between a VIN-labeled net and a VOUT-labeled
  net) is a geometry edit, not a label creation, so this guard doesn't intervene; the extraction conflict
  warning still flags it. Proactive resolution of that case can be a follow-up if you want it.
- `FindNodeLabel` builds an O(N) union per double-click — fine, since it runs only on the double-click
  gesture, not per frame.

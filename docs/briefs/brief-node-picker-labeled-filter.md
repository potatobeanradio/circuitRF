# Sonnet Brief — Node-picker filter: show only user-labeled nodes (net-label provenance)

## Goal
In a cube-bound trace's **node axis-role picker**, show ONLY nodes whose name came from a **user-placed net
label** in the schematic. Auto-generated connection-node names (`n1`, `n2`, …) are hidden — UNLESS the user
explicitly placed a net label whose text happens to be `n1` (then it shows, like any user label). The rule is
**provenance** (did a human label this net?), NOT the string.

[DECISIONS — locked by user]
- Filter is **ON by default**. With **no** labeled nodes, the node picker shows **nothing**.
- A user-placed label named `n1` PASSES the filter (provenance, not pattern). An auto `n1` does NOT.

## The core problem: provenance is lost by the time names reach the cube
By the time the picker sees the cube, the node axis is just **strings** (`Vin`, `n1`, `Vout2`) — identical whether
a name came from a user label or the auto `n#` generator. The picker (DataDisplay) has NO access to the schematic
or TestBench; it only sees the `DataSet` loaded from a `.npy`/`.cdd`. So we must **thread a provenance flag through
the whole chain into the persisted cube.** This is the bulk of the work; the picker filter itself is trivial.

Provenance is known at exactly ONE place: `NetExtractor.AssignNetNames`
(`src/Ui/Schematic/NetExtractor.cs`), where `rootToName[root] = lbl.Name` assigns a name from `model.NetLabels`.
Everything downstream only sees strings.

## The thread (5 hops)
### Hop 1 — TestBench carries the labeled-net name set (`src/Core/Design/TestBench.cs`)
Add `public HashSet<string> LabeledNets { get; } = new(StringComparer.Ordinal);` (or an `IReadOnlyCollection`
populated at construction). NetExtractor fills it with the **net names that came from a net label** (the `lbl.Name`
values it actually assigned in `AssignNetNames`). Only top-level labels matter for the testbench node axis; sub-cell
labels are internal and out of scope. Pin names and ground ("0") are NOT labeled-nets (they are not user net
labels) — only `model.NetLabels`-sourced names.

In `NetExtractor.AssignNetNames` (or its caller `ExtractModel`): collect the set of names assigned from
`model.NetLabels` and stash on the resulting `TestBench`. Note the priority subtlety already in `AssignNetNames`: a
Pin name or "0" can OVERRIDE a label on the same node — if that happens, the final node name is NOT the label, so
do NOT add it to LabeledNets (add a name only if the FINAL `rootToName[root]` equals the label text).

### Hop 2 — Elaborator propagates labeled names onto the NodeMap (`src/Core/Elaboration/`)
The cube node axis is built from `_netlist.Nodes.NameOf(c)` (a `NodeMap`). Add to `NodeMap`
(`src/Core/Elaboration/NodeMap.cs`): `public HashSet<string> LabeledNames { get; } = new(StringComparer.Ordinal);`
In `Elaborator.Elaborate`, after flattening, copy `tb.LabeledNets` into `netlist.Nodes.LabeledNames` (top-level
names only; the testbench frame uses net names as-is, so no path-prefixing at the top). Expose on
`ElaboratedNetlist` a passthrough if needed (`netlist.Nodes.LabeledNames`).

### Hop 3 — HbEngine writes a `__LabeledNodes` side cube (`src/Engine/HarmonicBalance/HbEngine.cs`)
In `BuildSingleToneDataSet` (and `BuildTwoToneDataSet` for parity), after building `nodeAxis`, emit a side cube
carrying the labeled-node names that ACTUALLY appear in the node axis:
```
// Provenance: which node-axis entries came from a user net label (for the node-picker filter).
var labeled = namesFull.Where(n => _netlist.Nodes.LabeledNames.Contains(n)).Distinct().ToArray();
if (labeled.Length > 0)
{
    var idx = Enumerable.Range(0, labeled.Length).Select(i => (double)i).ToArray();
    ds.Add("__LabeledNodes", new DataCube([new Axis("label", idx, "", labeled)],
                                          new double[labeled.Length]));   // values unused; Labels carry the names
}
```
Use the `__`-prefix so it's treated as metadata (the cube-signal list in `RebuildSignals` already skips `S`/`Z0`;
add `__`-prefixed names to that skip so the side cube never appears as a selectable signal — see Hop 5). The
`BuildSingleToneDataSet` signature gains access to `_netlist` (it's a static method today — either make it an
instance method or pass the `LabeledNames` set in as a parameter; passing the set in is the smaller change).

### Hop 4 — Persistence round-trips `__LabeledNodes`
The `.npy`/`.cdd` exporter/importer is generic over cubes + axis Labels, so a string-labeled side cube should
round-trip automatically. VERIFY: write a DataSet with `__LabeledNodes`, reload, assert the labels survive. If the
exporter drops zero-value/odd cubes or chokes on a values-array of all zeros, store the names differently (e.g.
values = 1.0 each) — but first check; it likely just works.

### Hop 5 — Picker filter (`src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs` + `AxisRoleRowViewModel.cs`)
This is the actual feature; everything above just delivers the data.

In `RebuildAxisRoles` (TraceRowViewModel), when building the per-axis pin `opts` for the **node axis**
(`axis.Name == "node"`), filter the option list to labeled nodes:
- Read the labeled set from the same DataSet: `ds.Contains("__LabeledNodes") ? ds["__LabeledNodes"].Axes[0].Labels`
  → a `HashSet<string>`. Empty/absent ⇒ empty set.
- For the **node** axis only, the selectable entries are those whose label ∈ labeled set. Other axes (harmonic,
  Pin, mixIndex) are unfiltered.
- Build a parallel list of (displayLabel, **cubeIndex**) so the picker maps the user's selection back to the TRUE
  cube node index (the filter changes which entries are shown, NOT the underlying axis indexing — `PinIndex` must
  remain the real cube axis index, per the AxisRoleRowViewModel contract). **Do not** collapse the axis; just hide
  non-labeled options. Concretely: `AxisRoleRowViewModel.PinOptions` becomes the filtered display list, and a
  parallel `PinOptionIndices[]` maps display-row → cube-axis index; `PinIndex` stores the cube-axis index (not the
  display position). Update `SetPinned`/`OnPinIndexChanged` to translate via `PinOptionIndices`.
- **Filter ON by default.** Add a per-trace (or per-picker) `bool ShowAllNodes = false`. When false → labeled-only;
  when true → all nodes. Surface it as a small "Show all nodes" checkbox/toggle on the trace card near the node
  axis-role row. (This is the safety valve — see UX cliff below.)
- If the labeled set is empty AND `ShowAllNodes==false` → the node picker shows **no** selectable node (per the
  locked decision). The toggle lets the user recover.

In `RebuildSignals` (TraceRowViewModel): add `__`-prefixed cube names to the skip list alongside `"S" or "Z0"`, so
`__LabeledNodes` never appears as a selectable signal:
```
if (cubeName is "S" or "Z0" || cubeName.StartsWith("__", StringComparison.Ordinal)) continue;
```

## ⚠️ UX cliff — hand-written netlists have NO net labels
A `.cnl` loaded by hand (no schematic, no net labels) produces a cube with an EMPTY `__LabeledNodes`. With filter
ON + empty ⇒ the node picker is **empty and unusable** for that file. This is exactly the locked behavior, but it
makes hand-written-netlist workflows hit a wall. **The `ShowAllNodes` toggle is the required escape hatch** — make
it obvious (not buried). RECOMMEND (confirm if unsure): when `__LabeledNodes` is **absent entirely** (vs present
but empty), default `ShowAllNodes=true` for that trace — i.e. "no provenance info at all ⇒ can't filter ⇒ show
all," distinct from "schematic ran, user tagged nothing ⇒ show nothing." This keeps hand-written netlists usable
while honoring the schematic-with-no-tags = empty rule. Flag this distinction to the user in the completion note.

## Tests
Core/Engine:
1. **Extract_LabeledNets_Collected:** schematic with a net label "Vout" on a wire → `TestBench.LabeledNets`
   contains "Vout"; an auto `n#` node is NOT in the set.
2. **LabelNamedN1_IsLabeled:** a user net label whose text is "n1" → "n1" IS in LabeledNets (provenance, not
   pattern).
3. **PinName_NotLabeled:** a Pin-named net and ground "0" are NOT in LabeledNets.
4. **Cube_Has_LabeledNodes_SideCube:** run HB on a labeled schematic → DataSet contains `__LabeledNodes` whose
   axis Labels == the labeled node names present in the node axis.
5. **Persistence_RoundTrips_LabeledNodes:** export+import a DataSet with `__LabeledNodes` → labels survive.
6. **HandWritten_NoLabeledNodes:** elaborate a `.cnl` with no labels → no `__LabeledNodes` cube (or empty).

UI:
7. **Picker_FiltersToLabeled:** cube with `__LabeledNodes={Vin,Vout}` and node axis `{Vin,Vout,n1,n2,Vout2}` →
   node picker shows only `Vin,Vout`; selecting `Vout` sets `PinIndex` to Vout's TRUE cube index (not its display
   position).
8. **Picker_EmptyWhenNoLabels_FilterOn:** `__LabeledNodes` present but empty, `ShowAllNodes=false` → node picker
   has no selectable node.
9. **Picker_ShowAll_RevealsAll:** toggling `ShowAllNodes=true` → all nodes appear; `PinIndex` mapping still correct.
10. **Picker_AbsentProvenance_ShowsAll:** `__LabeledNodes` absent (hand-written) → defaults to all nodes
    (per the recommended escape-hatch behavior).
11. **SideCube_NotSelectable:** `__LabeledNodes` does NOT appear in AvailableSignals.

## Gate
Build 0W/0E; tests green. Manual: schematic with labels on Vin/Vout, run HB, open a Table/Rect cube trace → node
picker lists only Vin/Vout; "Show all nodes" reveals n1/n2/Vout2; selecting a node plots the correct trace
(indexing correct). A hand-written netlist still shows its nodes (escape hatch).

## On completion
Note in `src/Ui/CLAUDE.md` + `src/Engine/CLAUDE.md`: the node-picker filters the cube node axis to **user-labeled
nodes only** (filter ON by default). Provenance is threaded NetExtractor → `TestBench.LabeledNets` →
`NodeMap.LabeledNames` → `__LabeledNodes` side cube in the HB DataSet (persisted). The picker reads
`__LabeledNodes`; `PinIndex` still indexes the TRUE cube node axis (filter hides options, does not reindex). A
`ShowAllNodes` toggle reveals all; absent `__LabeledNodes` (hand-written netlist) defaults to show-all so those
files stay usable, while a present-but-empty set (schematic, no tags) shows nothing. `__`-prefixed cubes are
metadata — skipped by the signal list.

# Sonnet Brief — Fix node-picker filter (sweep-stacking corrupts __LabeledNodes) + table dbl-click

## Bug 1 — __LabeledNodes is mangled by parametric-sweep stacking
ROOT CAUSE: `DataSet.StackSweepAxis` (RfCore/src/Data/DataSet.cs) prepends the sweep axis to EVERY
cube, including the `__LabeledNodes` metadata side cube. After a sweep, `__LabeledNodes` becomes
`[sweep, label]` instead of `[label]`. The picker (TraceRowViewModel.RebuildAxisRolesCore) reads
`lblCube.Axes[0].Labels`, which is now the numeric sweep axis (Labels == null) → labeledSet empty →
filter shows nothing OR (when the cube is dropped/absent on some paths) ShowAllNodes defaults true →
all nodes show. Either way the filter is broken in the GUI. The engine unit test passes because it
calls HbEngine.Run directly (no stacking).

### Fix 1a — don't stack metadata cubes (RfCore/src/Data/DataSet.cs, StackSweepAxis)
`__`-prefixed cubes are sweep-invariant metadata; prepending a sweep axis to them is meaningless and
corrupts their axis-0. Carry the FIRST dataset's copy through verbatim instead:



public static DataSet StackSweepAxis(Axis sweepAxis, IReadOnlyList<DataSet> datasets)

{

if (datasets.Count == 0)

throw new ArgumentException("At least one DataSet is required.", nameof(datasets));
var result = new DataSet();
foreach (var key in datasets[0].Cubes.Keys)
{
    // Metadata cubes (e.g. __LabeledNodes) are sweep-invariant — pass through, do NOT stack.
    if (key.StartsWith("__", StringComparison.Ordinal))
    {
        result.Add(key, datasets[0][key]);
        continue;
    }
    var cubes = new DataCube[datasets.Count];
    for (int n = 0; n < datasets.Count; n++)
        cubes[n] = datasets[n][key];
    result.Add(key, DataCube.PrependAxis(sweepAxis, cubes));
}
return result;
}

(`StringComparison` needs `using System;` — already present.)

### Fix 1b — picker reads the label axis by NAME, not position (TraceRowViewModel.RebuildAxisRolesCore)
Defensive against any future shape change. Replace the `lblCube.Axes[0].Labels` read:

HashSet<string>? labeledSet = null;

if (ds.Contains("__LabeledNodes"))

{

var lblCube = ds["__LabeledNodes"];

labeledSet = new HashSet<string>(StringComparer.Ordinal);

// Find the axis that actually carries the label strings (named "label"), not by position —

// a metadata cube could be wrapped by an outer axis. Fall back to the first axis with Labels.

var labelAxis = lblCube.Axes.FirstOrDefault(a => a.Name == "label" && a.Labels is not null)

?? lblCube.Axes.FirstOrDefault(a => a.Labels is not null);

if (labelAxis?.Labels is { } lbls)

foreach (var l in lbls) labeledSet.Add(l);

}


## Bug 2 — table column double-click opens Plot Properties flyout, not the inline editor
This is brief-table-cube-layout-fixes.md item #5 and was NOT landed. The Table view's pointer handler
routes a double-click on a `TableHitKind.TraceHeader` to the Plot Properties flyout; it must open the
inline spec text editor instead (the same `CommitSpec`/`SpecShorthand` editor the axis-role card uses).

FIND: the Table view code-behind (likely `src/Ui/Views/DataDisplay/TablePlotView.axaml.cs` or the
Table render control's `OnPointerPressed`/`DoubleTapped`). Locate where a hit of kind
`TableHitKind.TraceHeader` (or the column-header hit) dispatches on double-click/double-tap.
CHANGE that one branch to open the inline column-header editor (set the header into edit mode →
bind to the trace's `SpecShorthand`, commit via `TraceRowViewModel.CommitSpec` on Enter/LostFocus),
NOT `ShowPlotProperties`/the flyout. Leave single-click behavior (select) unchanged. Other
`TableHitKind` branches unchanged.

If the inline-header-edit control doesn't exist yet in the Table view, this item is larger than a
one-line reroute — STOP and report what's there (the hit-test + current dispatch) so we can scope the
editor control separately. Do not build a flyout-replacement editor speculatively.

## Tests
1. **Stack_PreservesLabeledNodesShape** (Engine.Tests): run a parametric-swept HB with LabeledNets
   {n_drain,n_gate}; assert the stacked DataSet's `__LabeledNodes` is rank-1 with axis name "label"
   and Labels containing n_drain,n_gate (NOT rank-2, NOT a sweep axis at position 0).
2. **Stack_MetaCubeNotSwept** (Engine.Tests): assert `__LabeledNodes.Axes[0].Name == "label"` after
   a 3-point sweep (regression for the prepend bug).
3. **Picker_FiltersAfterSweep** (Ui.Tests): build a swept DataSet with `__LabeledNodes={Vin,Vout}`
   and V `[sweep,node,harmonic]` with node labels {Vin,Vout,n1,n2}; node picker shows only Vin,Vout.
4. Bug 2: if the inline editor exists — **Table_TraceHeader_DoubleClick_OpensInlineEditor**: simulate
   a double-click on a trace column header → inline editor active, flyout NOT shown.

## Gate
Build 0W/0E; tests green. Manual: run a swept HB from the schematic with labeled nodes → node picker
lists only labeled nodes (n1/n2 hidden); "Show all nodes" reveals them. Double-click a result-table
column header → inline text editor appears (not the Plot Properties flyout).

## On completion
Note in `src/Engine/CLAUDE.md`: `DataSet.StackSweepAxis` passes `__`-prefixed metadata cubes through
unstacked (they are sweep-invariant); stacking them corrupted `__LabeledNodes`' axis-0 and broke the
node-picker filter for swept runs. The picker reads the label axis by name ("label"), not position.
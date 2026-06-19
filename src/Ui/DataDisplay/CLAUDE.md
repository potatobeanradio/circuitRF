# DataDisplay — local conventions

Standing instructions for `src/Ui/DataDisplay`. Read with the root `CLAUDE.md`, `src/Ui/CLAUDE.md`,
and `docs/design/data-display.md`.

## Slice grammar — one parser, two consumers

**`SliceTokenParser`** (`SliceTokenParser.cs`) is the single authority for the per-axis bracket
token grammar shared by:
- **`CubeTraceSpecParser`** — parses the single-trace spec text box (e.g. `db20 V[0, 1, :]`).
- **`TraceExpression`** — parses cube-operand expressions in the measurement/expression text field.

Never add a second token recogniser for `:` / `All` / `a..b` / integer / `"label"` anywhere in the
codebase — extend `SliceTokenParser` instead. Diverging the two parsers is the bug this was created
to prevent.

Token forms accepted:
- `:` or `All` (case-insensitive) — keep the whole axis (becomes `Range.All` / `AxisRole.KeepAsX`).
- `a..b` (end-exclusive), `..b`, `a..`, `..` — keep the axis at a narrowed sub-range.
- `integer` — pin and collapse the axis to that index (`AxisRole.PinToIndex`).
- `"quoted label"` — pin and collapse to the first index whose label matches.
- **One** `:` / `All` / range token → X axis (single curve). **Two** kept axes → family trace: last kept = X, earlier kept = Family (`AxisRole.FamilyIterate`); hard cap `Trace.MaxFamilyCurves=101`. **Three or more** kept axes is an error.

Semantics match the `DataCube` accessor layer (`ds.V("name", int, Range)`): **indices are never
physical values**, ranges are **end-exclusive** (C#/NumPy, not MATLAB). Full spec in
`docs/design/data-model.md` §7 and `src/Core/Data/CLAUDE.md`.

## Table column plan — one builder, three consumers

**`TableRenderer.BuildColumns(Plot)`** is the single authority for the per-trace X-axis column layout:
- **Draw path** (`TableRenderer.Draw`) — reads `layout.Columns` to position cells.
- **Copy Table Data** (`TableRenderer.BuildCopyGrid`) — uses the same column plan for WYSIWYG
  tab-delimited clipboard output.
- **`PlotControl`** interaction (resize, marker drag, right-click freq, double-tap fit) — calls
  `BuildColumns` at each interaction site rather than duplicating the col-0 = X assumption.

Adjacent-dedup rule: two consecutive traces with the same axis name, unit, sorted values, and point
count share a single `XAxis` column. Non-adjacent matching traces do NOT dedup (the middle different
trace breaks the run). See `docs/design/data-display.md` §"Table with multiple X axes".

## Data-display auto-refresh after re-run (brief-datadisplay-rerun-refresh, 2026-06-17)

Cube-bound traces re-render automatically after a re-run without user interaction.

**Reactivity chain:** run → `RunResultsWriter.WriteResults` → `RefreshOpenDataDisplaysAsync` →
`DataSourceLibraryViewModel.ReloadChangedAsync` → `LibraryChanged` event →
`PlotInspectorViewModel.OnLibraryChanged` → `ReseedSliceIfCubeShapeChanged` →
`TrySetCubeData` → `PlotNeedsRedraw`.

**`ReseedSliceIfCubeShapeChanged`** (private static, `PlotInspectorViewModel.cs`): called before
`TrySetCubeData` for each cube-bound trace. Compares the cube's current axis-name set against
the trace's stored `Slice`. If the sets are equal (same-shape re-run: only point counts changed),
the slice is left untouched — user's role/pin assignments survive. If the sets differ (e.g. a
new Vds sweep axis was added), the slice is rebuilt via
`TraceRowViewModel.BuildCarriedSliceFromCube`: existing axes keep their roles, new axes default
to `PinToIndex/0`, and exactly one X axis is guaranteed. `Trace.Expression` is then re-synced
via `BuildPickerExpression()`.

`INl` (node-indexed HB internal current, name starts with `"INl"`) is filtered from the trace
picker in `TraceRowViewModel.RebuildSignals` — `INl` cubes are always skipped. The unified `I`
cube (axis `"branch"`) IS offered and filtered by `__ProbeBranches` (the IProbe subset) when
present, mirroring the node-axis `__LabeledNodes` filter. `ShowAllBranchesToggleVisible` always
returns `false` — branch filtering folded entirely into `ShowAll`. See
`src/Core/Data/CLAUDE.md` §"HB branch currents" and `src/Engine/HarmonicBalance/CLAUDE.md` §C2.

## Bare measurement names (brief-bare-measurement-name, 2026-06-18) — COMPLETE

Bare measurement names — `PDC`, `Gain`, etc. — are accepted in the trace spec field for all ranks.
The `measurements` group (and the default group) are resolved by `DataSet.BareResolve`, so neither
`measurements.PDC` nor a group qualifier is required. Implementation is entirely in
`CubeTraceSpecParser.TryParse`:

- **Rank-0 bare name** (`PDC`): the bare-name branch short-circuits immediately after looking up the
  cube (`if (bareCube.Rank == 0) { slice = Array.Empty<AxisSlice>(); return true; }`), bypassing the
  `Repeat(":", 0)` → `PDC[]` → empty-token parse that previously failed.
- **Rank-0 bracketed form** (`PDC[]`): two guards in the bracket path: `string.IsNullOrWhiteSpace(sliceStr)`
  produces `Array.Empty<string>()` so `tokens.Length == cube.Rank == 0` holds; a second
  `if (slice.Length == 0) return true;` short-circuits before the "need at least one swept axis" check.
- **Rank-1+ bare names** (`Gain`): unchanged — existing synth-and-recurse path handles them.

After these changes `CommitSpec("PDC")` sets `CubeName="PDC"`, `Slice=[]`, clears `ExpressionError`,
and `TrySetCubeData` calls `SetScalarCubeData` (already present from the scalars-table brief).
`BuildPickerExpression` with an empty slice returns the bare cube name (no `[]`).

Bare names inside **multi-cube expressions** (e.g. `mag(PDC)`) are NOT resolved — `TraceExpression`
only recognises `Name[` references and builds its candidate list from qualified names. This is future
work; it is not attempted here.

5 gate tests in `BareMeasurementNameTests.cs`. Build 0W/0E; 1726 total tests pass.

## Scalar cubes (rank-0) — Table-only (brief-iprobe-currents-scalars-table, 2026-06-18)

Scalar (rank-0) cubes — DC operating-point values, IProbe DC currents, scalar measurements like
`PDC` — are surfaced in the trace picker and rendered as value cells **only** on a Table plot.
On other plot types (Rect/Smith/Polar) they draw nothing and show a soft `<invalid>` label.

**Key flag:** `Trace.ScalarOnNonTableInvalid` — set true in `BuildCubePath` when a scalar cube is
bound to a non-Table plot. `CubeShorthand` and `Description` append `" <invalid>"` when this flag
is set. `CubeIsScalar` flag tracks whether the current binding is a scalar.

**Entry point:** `Trace.SetScalarCubeData` — stores a synthetic 1-row X anchor `[0.0]` and calls
`BuildCubePath`. The `BuildCubePath` scalar short-circuit returns immediately (no points) and sets
`ScalarOnNonTableInvalid` when not on a Table. `BuildPickerExpression` with an empty slice returns
the bare cube name (no `[]`).

**Picker rule:** `TraceRowViewModel.RebuildSignals` skips `rank == 0 && !IsTablePlot`. Default slice
for rank-0 is `Array.Empty<AxisSlice>()`. `PlotInspectorViewModel.FirstPlottableCubeName` accepts
an `allowScalars` param that is true only when the plot is a Table.

**Table rendering:** `TableColumn.IsScalar` (on XAxis columns) is set for scalar anchor columns.
`TableRenderer.FormatColumnCell` returns `""` for scalar XAxis cells, keeping the anchor invisible.
Rank-1 cubes are unchanged (decision (b) — no new display work needed).

## Cascade signal picker (brief-picker-cascade-layout, 2026-06-18) — COMPLETE

The trace-card signal picker is a **group → item cascade**: `AvailableGroups`/`SelectedGroup` (left combo) → `AvailableSignals`/`SelectedSignal` (right combo, filtered to the selected group). Changing the group selects that group's first item automatically.

**Group naming rules (in `RebuildSignals`):**
- Network sources: `(singleSource ? "" : "filename..") + "S-Parameters"` — items have bare labels (`S(1,1)`, `Load Stability µ`).
- Cube sources: `filePrefix + groupDisplay`, where `groupDisplay` maps `DefaultGroup` → `"Signals"`, `MeasurementsGroup` → `"Measurements"`, and named analysis groups (`HB1`, `DC1`) pass through unchanged — items have bare quantity labels (`V`, `PDC`).

`_allSignals` (private `List<TraceDataItem>`) holds the full unfiltered set; `AvailableSignals` is the filtered view for `SelectedGroup`. `FilterSignalsToGroup` populates `AvailableSignals` from `_allSignals`.

**Transform combo** moved from the identity row onto the **spec/expression row** (left of the spec TextBox). It collapses on Smith/Polar via `IsVisible="{Binding IsRectOrTablePlot}"`, leaving the TextBox full-width. Network traces on Rect/Table still see the transform combo on this row.

**`ShowAll` eye toggle button** (`seg-btn`, `Kind="Eye"`) sits on the **filterable label-axis pin row**
(not the identity row). It is bound via `$parent[ItemsControl].DataContext` to
`ToggleShowAllCommand`/`ShowAll` on the `TraceRowViewModel`, and is `IsVisible="{Binding IsFilterableLabelAxis}"`
on the `AxisRoleRowViewModel` — so it appears only on the node or branch row, not on harmonic/frequency rows.

6 gate tests in `PickerCascadeLayoutTests.cs`. Build 0W/0E; 1737 total tests pass.

## Picker unified ShowAll + bare emission (brief-picker-showall-bare-emit, 2026-06-18) — COMPLETE

`ShowAllNodes` and `ShowAllBranches` are collapsed into a single `TraceRowViewModel.ShowAll`
property. One toggle drives both the node-axis filter and the branch-current filter.

**ShowAll defaults:**
- `__LabeledNodes` / `__ProbeBranches` absent (hand-written netlist) → `ShowAll` auto-sets to `true`
  inside `RebuildAxisRolesCore`. This auto-set is suppressed via `_rebuildingAxisRoles` guard;
  `RebuildSignals` detects the change via a `showAllSnapshot` diff and re-runs.
- Provenance cube present → `ShowAll` stays `false` (filter ON).

**Toggle visibility:** `ShowAllToggleVisible = IsCubeBoundTrace && _hasNodeAxis`, where `_hasNodeAxis`
is true when the cube has any filterable label axis (node OR branch). The eye button's `IsVisible` is
bound to `IsFilterableLabelAxis` on `AxisRoleRowViewModel` — so it renders only on the relevant axis row.

**Bare name emission rule** (in `RebuildSignals`):
```
string qualified = (group == DataSet.DefaultGroup || group == DataSet.MeasurementsGroup)
    ? bareName
    : $"{group}.{bareName}";
```
Analysis cubes stay qualified (`HB1.V`); measurements- and default-group cubes emit bare (`PDC`, `Gain`).

**`SiblingCubeName`** helper computes group-qualified side-cube names from the currently selected
cube name (e.g. `"HB1.V"` → `"HB1.__LabeledNodes"`).

5 gate tests in `PickerShowAllBareEmitTests.cs`. Build 0W/0E; 1727 total tests pass.

## Trace-card UX refinements (brief-card-ux-refinements F2, 2026-06-18) — COMPLETE

Three small UX improvements to the trace card:

**1. V/I item selector → `IconSelectButton`**
Analysis groups whose item list is exactly `["V", "I"]` now show a compact `IconSelectButton` instead of a cramped two-char `ComboBox`. VM flag `TraceRowViewModel.IsViSelector` (raised at the end of `FilterSignalsToGroup` so it tracks group changes): `AvailableSignals.Count > 0 && AvailableSignals.All(s => s.IsCubeBound && (s.Label == "V" || s.Label == "I"))`. The existing `ComboBox` stays for groups with variable items (Measurements, S-Parameters) so their disabled-item handling is preserved; both share `Grid.Column="1"` with `IsVisible` toggled.

**2. Drop trivial `[:]` in `BuildPickerExpression`**
`Trace.BuildPickerExpression()` now returns the bare cube name when the slice is a single whole-axis X (`Slice.Length == 1 && Slice[0].Role == AxisRole.KeepAsX && !Slice[0].IsNarrowedRange`). So the picker-authored expression reads `"PDC"` rather than `"PDC[:]"`, and `"mag(PDC)"` rather than `"mag(PDC[:])"`. Narrowed-range slices (`IsNarrowedRange == true`) are NOT collapsed. A user who types `PDC[:]` manually keeps it: `CommitSpec` stores the typed text in `Expression` verbatim, and `CubeShorthand` returns `Expression` first.

**3. First-add on Rect defaults complex cube to `mag()`**
`BuildSeedCubeTrace` (in `PlotInspectorViewModel`) sets `seedTrace.Transform = CubeTransform.Mag` when `_plot.PlotType == PlotType.Rect && cube.DataKind == DataKind.Complex`. This ensures a curve is immediately visible rather than an `<invalid>` state. Smith/Polar/Table are unaffected. Seed-time only — never re-applied on later edits.

5 gate tests in `CardUxRefinementsTests.cs`. `TableCubeTraceTests.CubeShorthand_WithTransform_PrependsPrefix` expectation updated (`"dB20(V[:])"` → `"dB20(V)"`). Build 0W/0E; 1756 total tests pass.

## Node/branch label axis — always a selector; scalar for no-sweep DC (brief-node-branch-selector-scalar, 2026-06-18) — COMPLETE

The `node` and `branch` label axes are **always pinned selectors** — they are never promoted to X. X defaults to the first **non-label** axis in the cube. A cube whose only axes are label axes (e.g. DC `V[node]` or DC `I[branch]` with no parametric sweep) has **no X axis** and resolves to a **scalar** (operating-point value). Scalar traces require a Table plot; non-Table bindings set `ScalarOnNonTableInvalid`.

**Rule applied in four places:**
- `RebuildSignals` default slice builder: loop over axes; label axes → `PinToIndex`; first non-label axis → `KeepAsX`; no non-label axis → empty (scalar).
- `RebuildAxisRolesCore` X-fallback: `FirstOrDefault(r => !r.IsFamily && r.AxisName is not "node" and not "branch")`; null → valid scalar (no forced promotion).
- `FlushSliceAndRebuild` X-fallback: same guard; no match → scalar (leave as-is).
- `BuildCarriedSliceFromCube` X-fallback: same guard; no match → scalar.

**`TrySetCubeData` scalar path (PlotInspectorViewModel):** when `xDim < 0` (all axes pinned, no KeepAsX), calls `SetScalarCubeData` directly instead of forcing axis 0 to X.

**`CubeTraceSpecParser`:** `keptDims.Count == 0` (fully-pinned spec, e.g. `DC1.I["Iout"]` or `DC1.I[0]`) is now valid — it produces a scalar trace. The previous "Need at least one swept axis" error is removed for this case.

**No-sweep HB improvement:** `V[node, harmonic]` defaults to `harmonic → X, node → pinned selector` (harmonic is the first non-label axis). Parametric sweep `[Pin_avail, node, harmonic]` defaults to `Pin_avail → X`, rest pinned — unchanged.

6 gate tests in `NodeBranchSelectorScalarTests.cs`. Build 0W/0E; 1751 total tests pass.

## V/I-symmetric trace card (brief-vi-symmetric-card, 2026-06-18) — COMPLETE

V and I are fully symmetric. One `V`/`node` cube and one `I`/`branch` cube, each with a
label-filtered pin row and a shared eye (`ShowAll`) sitting on that row. The spec box
reverse-syncs all combos on a valid edit (best-effort otherwise). Analysis groups always
offer both V and I, showing a "No node voltages"/"No branch currents" empty state when
the cube is missing.

**Branch filter (Part A):** `RebuildAxisRolesCore` detects both `node` and `branch` as filterable
label axes (via `foreach` over `cube.Axes`). The provenance cube is `__LabeledNodes` (node) or
`__ProbeBranches` (branch). `_hasNodeAxis` now means "has any filterable label axis". The filter
guard is `axis.Name == filterAxisName` (not hard-coded `"node"`).

**Eye on pin row (Part B):** `AxisRoleRowViewModel.IsFilterableLabelAxis` (set in constructor via
`isFilterableLabelAxis: axis.Name == filterAxisName`) marks the row. AXAML: identity row collapsed
from 5→4 columns (eye removed); axis-role row gets a 6th `Auto` column with the eye button
(`IsVisible="{Binding IsFilterableLabelAxis}"`).

**Spec reverse-sync (Part C):** `CommitSpec` calls `RebuildSignals()` on valid single-cube parse
(re-syncs group+item combos AND axis-role rows), and `RebuildAxisRoles()` only on invalid/multi-cube.

**Absent V/I (Part D):** `TraceDataItem.IsAbsent` marks placeholders. `RebuildSignals` synthesizes
an absent V or I for each analysis group missing one. `ShowEmptyQuantity`/`EmptyQuantityMessage`/
`ShowAxisRoles` on `TraceRowViewModel` expose the empty state. Selecting an absent item short-circuits
`OnSelectedSignalChanged` (clears slice + axis rows; `TrySetCubeData` finds the cube absent and
clears points gracefully). AXAML: `ItemsControl.IsVisible` bound to `ShowAxisRoles`; a `TextBlock`
shows the message when `ShowEmptyQuantity`.

Supersedes the Brief-2 separate-cube branch filter (now removed) and the identity-row eye.

6 gate tests in `ViSymmetricCardTests.cs`. Build 0W/0E; 1745 total tests pass.

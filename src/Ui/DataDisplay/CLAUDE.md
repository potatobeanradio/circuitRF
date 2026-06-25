# DataDisplay — local conventions

Standing instructions for `src/Ui/DataDisplay`. Read with the root `CLAUDE.md`, `src/Ui/CLAUDE.md`,
and `docs/design/data-display.md`.

Harmonic stem plot (brief-datadisplay-harmonic-stem-plot, 2026-06-20) — COMPLETE: HB single-tone
Rect traces whose X-axis is `"harmonic"` are rendered as discrete lollipop/stem plots instead of a
connected polyline. **Detection:** `Trace.HarmonicAxisName = "harmonic"` (const); `Trace.IsHarmonicStem`
(computed: `IsCubeBound && CubeXAxisName == "harmonic"`, Ordinal). **Wire-up:** `PlotRenderer.Draw`
computes `stemMode = plotIsRect && trace.IsHarmonicStem` and passes it to `TraceRenderer.Draw`
(`bool stemMode = false` default keeps all other callers byte-identical). **Rendering (TraceRenderer):**
when `stemMode && props.LineEnabled`, replaces the connected-line branch with per-point stems: a
vertical `DrawLine` from world-y=0 to the data point, capped by a filled triangle arrowhead pointing
away from baseline (`dir = Sign(basePx.Y − tipPx.Y)`; head size `= Min(lw*3, Max(lw*0.5, stemLen*0.33))`).
Separate `BuildStemPaint`/`BuildHeadPaint`/`DrawStem` helpers keep the implementation in one place for
both single traces and family traces. Point markers remain additive. Autoscale option (A) chosen: stems
clip at viewport floor; no autoscale Y-min extension. 3 gate tests in `HarmonicStemPlotTests.cs`
(T1 harmonic=true, T2 freq=false, T3 SNP=false). Build 0W/0E; 1998 total tests pass.

**Two-tone mixIndex stem (2026-06-25):** the two-tone spectral axis `"mixIndex"` also renders as stems.
`Trace.MixIndexAxisName="mixIndex"` + `Trace.IsMixIndexStem`; `PlotRenderer` `stemMode = plotIsRect &&
(IsHarmonicStem || IsMixIndexStem)`. The mixIndex axis VALUES are already the signed product frequencies
(`k1·f1+k2·f2`, unit "Hz"), so X positions are physical freqs (negatives included) with NO order→freq
reconstruction — unlike harmonic, mixIndex is NOT excluded from `IsCubeXMarker` (markers use the generic
cube-X path). T4/T5 in `HarmonicStemPlotTests`.

## Spec text box ↔ transform combo two-way sync (2026-06-25)

The spec TextBox is OneWay-bound to `SpecShorthand`; the transform combo is TwoWay-bound to
`SelectedTransformItem`. `RefreshDescription` (called by `RebuildAndNotify`) raises BOTH, so the VM side
syncs in both directions — `CommitSpec` → `SelectedTransformItem` is covered by `SpecTransformSyncTests`.
The bug was the **focused TextBox**: a focused OneWay TextBox does NOT pick up a model change made via the
combo, and `OnSpecEditLostFocus` then committed its STALE text, **overwriting** the combo's change. Fix
(`PlotInspectorView.axaml.cs`): `OnSpecEditGotFocus` records the text at focus time (`_specPristine`);
`OnSpecEditLostFocus` commits ONLY if the text actually changed (user edited), otherwise re-syncs the box to
`vm.SpecShorthand` (the combo may have moved it); the Enter handler resets `_specPristine` after committing.

**Measurement round-trip:** `BuildPickerExpression` emits the **function-call** form `mag(IMD2)` for a
transformed bare measurement (no brackets), but `CubeTraceSpecParser`'s bare-name path only handled the
space-separated form (`mag IMD2`) — so `mag(IMD2)` failed to parse, `CommitSpec` dropped `CubeName`/`Transform`,
and the combo couldn't sync. The bare-name path now also parses `transform(bareName)` (the `(`/`)` form). Tests
`SpecTransformSyncTests.CommitSpec_TransformedMeasurement_UpdatesTransformCombo` and
`BareNameExprTests.CubeTraceSpecParser_TransformedBareMeasurement_RoundTrips`.

## Auto-transform on add only for COMPLEX data (2026-06-25)

The "first-add nicety" auto-transform (so a Rect trace shows a curve, not `<invalid>`) applies **only to
complex cubes**: `TraceRowViewModel.DefaultTransformFor(cube, plotType)` = dB20 for S/Y/Z parameter cubes,
`mag` for other complex cubes on Rect, else **None**. Real data shows raw — no annoying "mag". This is the
single source of truth used by BOTH the seed (`PlotInspectorViewModel.BuildSeedCubeTrace`) AND the
signal-switch (`OnSelectedSignalChanged` cube branch) — the latter previously did NOT re-apply it, so a `Mag`
from a complex default cube (e.g. V) **persisted** when switching the signal to a real cube (e.g. a real
measurement), which was the user-visible bug. Switching to a complex cube now also (re)applies the transform
so it doesn't render `<invalid>`. Test `AutoTransformOnAddTests`.

## Transform combo must not corrupt a network trace (2026-06-25)

`BuildPickerExpression` returns the network **description** (`dB(S(1,1))`, parens form) when a trace has no
`CubeName` (line 453 → `ShortDescription`). `ApplySelectedTransform` (the unified transform combo) used to
store that into `Expression` whenever `IsCubeBound` was true — but a network trace with a *stale* `Expression`
has `IsCubeBound==true`, so changing Mag→None wrote `dB(S(1,1))` into `Expression`, falsely marking it
cube-bound → "No cube references found". Fix: gate the `BuildPickerExpression` rebuild on **`CubeName != null`**
(a genuine picker cube trace). For `CubeName==null` map the transform to `YAxis` and **self-heal** — clear an
`Expression` that currently fails to parse (`ExpressionError != null`, a stale network description) while
**preserving** a valid typed multi-cube expression (no error). Never call `BuildPickerExpression` to set
`Expression` on a non-`CubeName` trace. Test `NetworkTraceTransformHealTests`.

## Table trace Number Format + trace-header context menu (2026-06-25)

- **Number Format (MA/RI/DB) on Table cube cells:** `Trace.FormatCubeCell` previously hard-coded MA for a
  complex value with no scalar transform (`CubeTransform.None`/`Conj`). It now routes through
  `FormatCubeComplex(z, f)` which honors `trace.MatrixFormat` (MA `mag∠ang`, RI `re±jim`, DB `dB∠ang`) —
  so the right-click "Number Format" menu is respected on Table cube slices. Network (Touchstone) cells
  already honored it via `TableRenderer.FormatTraceCell` (`YAxis == Complex` branch). For real-valued / scalar-
  transform traces (mag/dB/…) the value is a plain number and Number Format does not apply. **Do NOT touch
  `FormatSummaryCell`** — the contour Performance Summary table is out of scope. `MatrixFormat` persists in
  `.cdd` via `TraceConfig.MatrixFormat` (saved/loaded in `DataDisplayViewModel`, default MA).
- **Trace-header context menu** (`PlotControl.ShowTraceHeaderContextMenu`): the **"Y Axis"** submenu is
  removed (the dependent variable is chosen via the trace-card expression / YAxis combo). **"Matrix Type"**
  (S/Z/Y) is shown only for network traces — gated `!trace.IsCubeBound && trace.Data is { } d && !d.IsEmpty`
  (mirrors `TraceRowViewModel.ShowMatrixTypeCombo`), so it never appears on a simulation DataCube slice.
  Test `NumberFormatTableTests`.

## Add Plot always lands in the viewport (2026-06-25)

`DataDisplayViewModel.ComputeNewPlotPosition` must keep a newly-added plot **visible** so the user never
presses "Add Plot" to no apparent effect. Three cases (viewport = current pan/zoom logical rect, from
`CanvasSizeProvider` + `_zoomLevel`/`_viewOffsetX/Y`; pan syncs via `PlotCanvasView.OnCanvasPanMoved` →
`ViewOffsetX/Y`, scroll-zoom via `ZoomAtPoint`):
1. **Nothing in view** → center the new plot in the viewport.
2. **In-view plots, grid slot fits** → next slot of the inferred grid (nice grid growth).
3. **In-view plots, grid slot off-screen** (the 2026-06-25 fix) → `PlaceInsideViewport` cascades the plot
   from the viewport top-left (clamped fully visible) instead of letting the grid grow off-screen. Without
   this, once the in-view plots fill the visible area the next slot lands off-screen — the user's bug.
The historical `(30 + count·30)` cascade is only the fallback when `CanvasSizeProvider` is null/zero (pre-load
first plot at 30,30). Tests `AddPlotPlacementTests` (grid-off-screen stays visible; pan-away lands in view).

## Arrow-key marker fine-movement on Rect plots (2026-06-25)

A selected marker on a Rect (non-contour) plot steps one x-axis sample per arrow key: Up/Right → next
higher x, Down/Left → next lower. Stepping is in **ascending display-x order**, so harmonic/mixIndex
spectral axes step in **frequency** (mixIndex values are folded freqs stored in lattice order, not sorted).

- **Engine:** `Trace.StepMarkerAlongX(Marker, int direction)` — cube/harmonic/family traces rank
  `CubeMarkerPoints(m)` by `Points[i].X` (the marker-space x for both cube-X and stem markers; folded for
  mixIndex), find the nearest current sample, and set `PositionStatic = (target.X, IsFamily ? curveIdx : 0)`.
  Network/SNP traces reuse the existing `IncrementMarkerFreq`/`DecrementMarkerFreq` (step `Data.Frequencies`).
  Returns false at an axis end (no wrap) and for contour/Smith/Polar.
- **One handler, two surfaces:** `PlotContainerViewModel.StepSelectedMarkers(int direction) → bool` is the
  single entry point — steps every selected marker (`GetSelectedMarkers`), repositioning each box via the
  **light** `FindMarkerInfoBoxVm(m).OnMarkerMoved()` + `RequestPlotRedraw` path (NOT a full info-box rebuild),
  so a focused info box keeps focus across repeated presses; the redraw also marks the doc dirty
  (`PlotNeedsRedraw → ContentChanged`). Both surfaces route here so behavior is identical:
  - **Plot canvas:** `PlotControl.OnKeyDown` → `StepSelectedMarkersHandler` (a `Func<int,bool>` set to
    `vm.StepSelectedMarkers` in `PlotContainerView.axaml.cs`, beside the other providers). The control is
    `Focusable` and gets `Focus()` on `PointerPressed`.
  - **Info box:** `MarkerInfoBoxView` is `Focusable`, calls `Focus()` in `OnPointerPressed` (so a clicked/
    selected box receives keys), and `OnKeyDown` → `Vm.Container.StepSelectedMarkers(dir)`.
  Consumes the arrow key whenever a marker is eligible (prevents scroll at axis ends). Rect-only.
- **Why not the drag's `PlotChanged` path:** it fires `OnContainerPlotChanged` →
  `RebuildMarkerInfoBoxesForContainer`, which recreates every info-box control — fine for the canvas, but it
  would steal focus from a focused info box on each keypress. So the keyboard path repositions in place.
- Tests `HarmonicStemPlotTests` T6 (mixIndex → frequency order + end-stop) / T7 (monotonic cube X). The
  container-level wiring isn't unit-tested headlessly (the info-box VM ctor measures text via Avalonia-loaded
  Skia fonts). Ui 1536.

## Table plots lock marker info boxes ON (2026-06-25)

A Table has no on-canvas way to re-open a hidden marker info box — the box itself is the only place
the toggle lives, so once `ShowInfoBox=false` on a Table the box (and its context menu) vanish with no
way back. Rule: **info-box visibility is locked ON for Table plots.** Switching a plot to Table forces
every marker's box on; the off-toggle is disabled while the plot is a Table.

- **Force-on:** `PlotContainerViewModel.EnsureInfoBoxesShownForTable` (called from the `PlotStructureChanged`
  handler, which fires on plot-type change AND trace add/remove) → static `ForceMarkerInfoBoxesOnForTable(Plot)`
  sets `ShowInfoBox=true` on every marker when `PlotType==Table`, then `RequestInfoBoxRebuild()`.
- **Disabled toggle, two surfaces:** `MarkerEditorViewModel.CanToggleInfoBox` (`_parent.PlotType != Table`)
  gates the editor checkbox's `IsEnabled`; `MarkerInfoBoxView.PopulateMarkerMenu` sets the "Show Info Box"
  menu item `IsEnabled = hostPlot.PlotType != Table`.
- Test `TableMarkerInfoBoxTests`. Ui 1534.

## Slice grammar — one parser, two consumers

**`SliceTokenParser`** (`SliceTokenParser.cs`) is the single authority for the per-axis bracket
token grammar shared by:
- **`CubeTraceSpecParser`** — parses the single-trace spec text box (e.g. `db20 V[0, 1, :]`).
- **`TraceExpression`** — parses cube-operand expressions in the measurement/expression text field.

Never add a second token recogniser for `:` / `All` / `a..b` / integer / `"label"` anywhere in the
codebase — extend `SliceTokenParser` instead. Diverging the two parsers is the bug this was created
to prevent.

**Comma-splitting is also centralized: `SliceTokenParser.SplitTokens(body)` (2026-06-25).** It splits a
bracket body into per-axis tokens on **top-level commas only** — a comma inside a double-quoted label (the
two-tone mixIndex tag `"(1,-1)"`) is NOT a separator. Both consumers call it; a naive `Split(',')` used to
break the label into two tokens (`'… got 4'`). The ref-scanner in `TraceExpression` already scans to the
matching `]` (labels have no `]`), so only the per-axis split needed the fix. Test `CommaLabelSliceTests`.

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

Bare names inside **expressions** (e.g. `(IMD2)`, `mag(PDC)`, `IMD2 + 5`) ARE now resolved (2026-06-25).
`TraceExpression`'s ref-scanner recognises a bare cube name (not followed by `[`) as a whole-cube reference
(synthesizes an all-`:` slice → reuses the bracketed slicing path), with identifier word-boundary checks
(`IsIdent` = letter/digit/`_`/`.`) so `V` never matches inside `Vout`/`HB1.V`; a trailing `(` is excluded
(call-like). The candidate list now emits BARE names for the default AND measurements groups (both
bare-resolve via `DataSet.BareResolve`), so a measurement like `IMD2` resolves. A bare rank-1 cube → its
axis is X; rank-0 scalar → "no X axis" (use a Table). Tests `BareNameExprTests`.

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

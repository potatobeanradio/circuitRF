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

`INl` (node-indexed HB branch current) is filtered from the trace picker in
`TraceRowViewModel.RebuildSignals` — any cube whose axis set includes `"node"` and whose name is
`"I"` or `"INl"` is skipped. Only the `I:<path>:<terminal>` branch cubes (no `node` axis) are
offered. See `src/Core/Data/CLAUDE.md` §"HB branch currents".

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
- Exactly **one** `:` / `All` / range token per spec is the X axis; two kept axes is an error.

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

`INl` (node-indexed HB branch current) is filtered from the trace picker in
`TraceRowViewModel.RebuildSignals` — any cube whose axis set includes `"node"` and whose name is
`"I"` or `"INl"` is skipped. Only the `I:<path>:<terminal>` branch cubes (no `node` axis) are
offered. See `src/Core/Data/CLAUDE.md` §"HB branch currents".

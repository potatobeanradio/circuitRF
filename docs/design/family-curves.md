# Family Curves (Trace Families) — Design

Status: reflects shipped behavior (alpha). This is the design doc for the Data Display **family** system —
one trace that renders as N curves over a swept "family" axis. It supersedes the original 7.3b implementation
notes and folds in the explicit family-marker work added since.

Read with: `data-display.md` (the Phase 7 plan; family is sub-phase 7.3), `parametric-sweep-ux.md` (how sweep
nesting sets the cube's axis order and therefore the default X), `analysis-cards-ui.md` (where the swept cubes
come from), and `data-model.md` + `src/Core/Data/CLAUDE.md` (the DataCube slice contract).

## What it is

A **family** is a single `Trace` whose one **family axis** is iterated, producing **N curves** that share one
trace row, one style, and one delete. The canonical case is a FET curve tracer: a nested Vgs×Vds DC sweep
yields an `I:Ids` cube of shape `[Vgs, Vds]`; plotting it draws one Id-vs-Vds curve **per Vgs** — a fan of
curves — without the user adding a trace per sweep value.

Each cube axis a trace consumes has one of three roles (`AxisRole`): **KeepAsX** (the swept X axis, exactly
one), **FamilyIterate** (the family axis, at most one), or **PinToIndex** (fixed at one index, removed from the
plot). A family is simply a trace whose slice marks one axis `FamilyIterate`.

## How it works for the user

**Default (auto-recognition).** Plotting a 2-D cube with both axes kept produces a family automatically. Bare
`I:Ids` (no brackets) and `I:Ids[:, :]` are equivalent and both render a family. The roles are assigned by a
positional **convention: the last kept axis becomes X, the earlier kept axis becomes the family.** Because the
parametric-sweep engine orders cube axes outer→inner (`[Vgs(outer), Vds(inner)]`), the convention makes the
**innermost sweep axis the default X** and the outer axis the family — exactly the curve tracer. (See
`parametric-sweep-ux.md` for the axis-order ground rule; reordering the sweep inner/outer flips which axis is X.)

**Explicit control via the picker.** The trace card's axis-role editor gives each axis a three-state toggle —
**X / Family / Pinned**. Clicking **Fam** on an axis makes it the family; exactly one axis stays X (setting a
new family or X auto-demotes/auto-promotes the others so the invariant "one X, ≤1 family" always holds). A
Family or X axis hides its pin-value picker.

**Explicit control via the spec text — the `~` marker.** The trace's shorthand can name the family axis
explicitly with `~` (also accepted: `fam`, `family`). This exists because the positional convention can only
express *one* of the two arrangements (earlier = Family, last = X); it cannot say "earlier axis = X, later axis
= Family." The marker removes that limit:

- `I:Ids[:, ~]` → axis 0 (Vgs) = X, axis 1 (Vds) = Family.
- `I:Ids[~, :]` → axis 0 (Vgs) = Family, axis 1 (Vds) = X (same as the bare/convention default, but explicit).
- `I:Ids` / `I:Ids[:, :]` (no `~`) → positional convention (last kept = X).

The two entry points stay in sync: the picker writes the slice **and** regenerates the shorthand, emitting `:`
for the X axis, `~` for the family axis, and an index/label for pins. So clicking **Fam** on Vds yields
`I:Ids[:, ~]`, which matches the render and round-trips when re-parsed (no surprise re-pinning). The `~` marker
is single-cube only; it is rejected in multi-cube element-wise expressions.

**Plot versus — the one exception to the shared X.** A family normally shares ONE X array across its
curves. A "plot versus" family (`Gain[:, ~] vs Pout`) cannot: each curve's X data genuinely differs
(Pout at 2.0 GHz is not Pout at 2.4 GHz). `FamilyCurve.RawX` carries the per-curve X and
`BuildFamilyPath` uses `fc.RawX ?? _cubeXValues`, so every ordinary family is unchanged (`RawX` is
null there); the trace-level X becomes curve 0's, and the marker readout reads the marked curve's own
X instead. The X side must iterate the SAME family axis by name — a bare X side inherits that role,
a bracketed one is checked. See `plot-versus.md` §3.

**Limits and behavior.** A family is capped at **101 curves** (`Trace.MaxFamilyCurves`); a longer family axis
clamps to the first 101. The family renders as **one trace drawn N times** — every curve uses the trace row's
single line color and style; there is no per-curve color stepping and no legend (restyling the row restyles the
whole fan). Markers on families are out of scope. A family is one trace row: one delete removes all curves, and
autoscale frames the whole fan.

## Architecture and code

**Model — `src/Ui/DataDisplay/Models/Trace.cs`.** `AxisRole { PinToIndex, KeepAsX, FamilyIterate }`. A trace is
a family when `IsFamily` (its `Slice` marks an axis `FamilyIterate`). Family geometry lives in
`FamilyCurves : List<FamilyCurve>` — each `FamilyCurve` holds the iterated axis value, an optional label, and
its own `Points`. `FamilyAxisName` records the iterated axis. `FamilyCurves`/`Points` are **derived** (never
serialized; rebuilt on load). `SetFamilyData(...)` injects N pre-sliced rank-1 curves (shared X, per-curve
complex/real values) and builds their points using the same per-sample transform mapper (`RectY`) as the
single-curve path, so dB/mag/phase/etc. behave identically. `PathBoundingRect()` spans all curves so autoscale
frames the fan. The cap constant `MaxFamilyCurves = 101` is the single source of truth.

**Shorthand grammar — `src/Ui/DataDisplay/SliceTokenParser.cs`.** Shared by the picker-spec parser and the
multi-cube expression evaluator so the grammar can't drift. Per-axis tokens: `:` / `..` / `All` → keep whole;
`a..b` → kept end-exclusive range; quoted `"label"` or integer → pin; and now `~` / `fam` / `family` → the
`Family` token kind.

**Picker-spec parser — `src/Ui/DataDisplay/CubeTraceSpecParser.cs`.** Parses a single-cube shorthand into
`(CubeName, AxisSlice[], CubeTransform)`. A bare cube name synthesizes all-`:` tokens for the cube's rank. Role
resolution: if an explicit `~` is present, that axis is the family and the lone `:` axis is X (one family + one
X required; extra kept axes are an error). With no `~`, the positional convention applies — 0 kept → error,
1 kept → single curve, 2 kept → last = X / earlier = Family, 3+ kept → error (pin the extras).

**Expression sync — `Trace.BuildPickerExpression()`.** Regenerates the shorthand from the slice: `:` for X, `~`
for family, index/label for pins, with the transform as a function-call prefix. This is what the picker writes
back after a role edit, and what keeps the displayed expression faithful to the rendered slice.

**Owner resolution — `src/Ui/DataDisplay/ViewModels/PlotInspectorViewModel.cs`.** `TrySetCubeData` routes
single-cube specs (CubeName + Slice) through the slice path and reserves `TraceExpression` for genuine
multi-cube expressions. The slice path branches to `ResolveFamily` when any axis is `FamilyIterate`:
`ResolveFamily` finds the family and X axes **by name** (order-independent), loops the family axis up to the
cap, pins the rest, slices the cube to a rank-1 array per family value, and hands the set to `SetFamilyData`.
`TraceExpression` (the multi-cube path) rejects a `~` token with a clear "single-cube only" error.

**Re-run reactivity — `ReseedSliceIfCubeShapeChanged` (same file).** When a re-run reloads the cube, the trace
re-derives its slice from its **shape-independent spec text** (re-parsing the authored expression) whenever the
cube's axis-name set **or order** differs from what the slice was built against. Order matters: reordering a
parametric sweep inner↔outer changes which axis is innermost (the default X) without changing the name set, so
the comparison is an ordered one. A pure value/point-count re-run (same names and order) is skipped, preserving
the user's explicit roles and pins. Because the expression is left untouched (not regenerated) on reseed, a
bare `I:Ids` stays a shape-independent family across reshapes, and an explicit `~` is honored on re-parse.

**Picker view-models — `AxisRoleRowViewModel` + `TraceRowViewModel`.** The role row is three-state
(`IsX` / `IsFamily` / Pinned, mutually exclusive); setting Family demotes any other family and ensures exactly
one X. `TraceRowViewModel.FlushSliceAndRebuild` maps the rows to `AxisRole`s, writes the slice, and sets
`Expression = BuildPickerExpression()`. The spec text box round-trips through `CommitSpec` →
`CubeTraceSpecParser`.

**Renderer — `src/Ui/DataDisplay/Renderers/TraceRenderer_MarkerRenderer.cs`.** When `trace.IsFamily`, the
renderer strokes one path per `FamilyCurve` using the trace's single line paint (color, width, dash), then
returns — skipping the single-curve and marker blocks. No per-curve color stepping, no legend.

**Persistence (`.cdd`).** `AxisSlice.Role` serializes by enum value, so a `FamilyIterate` slice round-trips with
no schema change; `FamilyCurves`/`Points` are derived and rebuilt on load. A saved family reloads and
re-expands to its N curves.

## Recent changes (since the original 7.3b implementation)

- **Explicit family marker `~`.** Added the `Family` token to the shared grammar and taught the parser, the
  picker-expression builder, and the expression evaluator about it. This fixed a bug where clicking **Fam** on
  the X axis produced a lossy `I:Ids[:, 0]` (the family axis was emitted as a pinned index because the builder
  had no family case), so the shorthand mismatched the render and re-parsing collapsed the family to a single
  pinned curve. The marker lets any X/Family arrangement round-trip faithfully.
- **Reseed re-derives on axis-order change.** The re-run reseed now triggers on an axis *order* change (not just
  an added/removed axis) and re-derives from the shape-independent expression, so reordering a sweep inner↔outer
  updates the plotted X axis on the next Run automatically (previously it kept the stale X until the user
  pressed Enter in the expression box).

## Key files

- `docs/design/plot-versus.md` — the `vs` separator; per-curve X (`FamilyCurve.RawX`) for a versus family.
- `src/Ui/DataDisplay/Models/Trace.cs` — `AxisRole`, `IsFamily`, `FamilyCurve`/`FamilyCurves`, `SetFamilyData`,
  `BuildPickerExpression`, `PathBoundingRect`, `MaxFamilyCurves`.
- `src/Ui/DataDisplay/SliceTokenParser.cs` — token grammar incl. the `Family` (`~`) token.
- `src/Ui/DataDisplay/CubeTraceSpecParser.cs` — single-cube spec parse + role resolution.
- `src/Ui/DataDisplay/TraceExpression.cs` — multi-cube expressions (rejects `~`).
- `src/Ui/DataDisplay/ViewModels/PlotInspectorViewModel.cs` — `TrySetCubeData`, `ResolveFamily`,
  `ReseedSliceIfCubeShapeChanged`.
- `src/Ui/DataDisplay/ViewModels/AxisRoleRowViewModel.cs` + `TraceRowViewModel.cs` — the picker role rows.
- `src/Ui/DataDisplay/Renderers/TraceRenderer_MarkerRenderer.cs` — the family draw path.

# Brief DD-P — Data Display: plot-type integrity (transform remap, Rect aspect, Smith square limits)

**Area:** `src/Ui/DataDisplay` + `src/Ui/Views/DataDisplay`. Do not touch `src/Core`, `src/Engine`,
`RfCore`.

**4 items, one theme:** *changing the plot type must never leave the user with a broken plot.* The
headline is §1 — a valid trace stays a valid trace across every plot-type change, transforming only
as much as the new plot type requires.

---

## Verified anchors (on disk)

1. **`Plot.SetPlotType` (`Models/Plot.cs:613-690`) only understands NETWORK traces.** It remaps
   `Trace.YAxis` and `Trace.Derived` (µ ↔ source-stability circles, µ′ ↔ load-stability circles) and
   splits a complex network trace into dB + phase on `→ Rect`. It does **nothing** with
   `Trace.Transform`, which is what a cube-bound trace actually renders through — so
   `SP1.S[:, 1, 1]` (Transform `None`, complex) carried from Smith to Rect stays `None` and renders
   nothing but a `<invalid: complex on scalar plot type>` Y label (`Trace.RectYLabel`,
   `Trace.cs:546-554`; the flag is `Trace.RectValueInvalid`, `:317`).
2. **Leaving Table deletes every trace.** `PlotInspectorViewModel.OnPlotTypeChanged`
   (`ViewModels/PlotInspectorViewModel.cs:181-196`) clears `_plot.Traces` wholesale when the old
   type is Table. The comment justifies it by *summary columns and scalars*, but it takes ordinary
   traces with it — a Table of `dB20(S(1,1))` switched to Rect comes back empty.
3. **`SetPlotType` DELETES on `→ Smith/Polar`**: any trace with `YAxis` Phase/Real/Imaginary, or
   `Derived == MaxGain`, is removed (`Plot.cs:625-634`).
4. **The transform list is not plot-type aware.** `TraceRowViewModel.TraceTransformItems`
   (`:1712`) returns the static `PlotInspectorViewModel.AllCubeTransforms` (`:138`, every member
   enabled) for any cube trace. `AllTransformsForNetwork` (`:142`) disables dB10/dB/Conj for network
   traces but is likewise blind to the plot type. So on a **Rect** plot with a **complex** cube,
   `None` and `Conj` are offered and selectable, and produce an unplottable trace — §4.
   The per-item `IsEnabled` plumbing already exists (`CubeTransformItem.Enabled` →
   `ComboBox.ItemContainerTheme` in `PlotInspectorView.axaml:1107-1112`); only the list construction
   is wrong.
5. **Nothing resizes the container on a plot-type change.** `OnPlotTypeChanged` (anchor 2) never
   touches size. `PlotContainerViewModel.ResizeTo` (`:338`) keeps Smith/Polar square via
   `IsSquareAspect`, and `PlotContainerView.axaml.cs:386` applies
   `AppSettingsViewModel.Instance.RectAspectRatio` on a drag-resize and `:196` on grip double-click;
   `DataDisplayViewModel.cs:536-545` applies it to a **newly added** Rect plot. So a Smith→Rect
   switch leaves the container square — the reported bug — and Table→Rect leaves whatever the Table
   was.
6. **Smith axis limits are not constrained.** `AxesLimitsViewModel.TryApplyX` / `TryApplyY`
   (`ViewModels/AxesLimitsViewModel.cs:145-176`) each write one dimension of `Axes.Window`
   independently, with no square constraint on complex plot types. `Plot` *has* the right helper
   already — `SquareCentredOnOrigin` (`Models/Plot.cs:599-609`) — used by the autoscale path but not
   by the manual-limits dialog. `IsComplex` is already exposed on the VM (`:35`) and X/Y autoscale
   are already coupled there via `SetBothAxesAutoscale` (`:190`), so the coupling precedent exists.
7. **Contour traces already survive a plot-type change**: `TraceRowViewModel.RebuildContour` derives
   `SurfacePlane` from `_parent.PlotType` on every rebuild (`:549-551`). Leave that alone.

---

## §1 — A valid trace must stay valid across every plot-type change [BUG]

**Principle (owner):** *"We always want to keep something to see."* A plot-type change may transform
a trace; it may not silently blank it, and it may not delete it when a mapping exists.

Define the mapping in **one place** — a single static function, e.g.
`Trace.RemapForPlotType(PlotType oldType, PlotType newType)` (or a helper next to `SetPlotType`) —
and drive every path through it. Do not spread the rules across `SetPlotType`, `OnPlotTypeChanged`
and the trace card.

### The mapping

**Complex-valued trace → Rect** (the owner's worked example: `SP1.S[:, 1, 1]` → `dB20(SP1.S[:, 1, 1])`):
- Cube-bound, complex data, `Transform ∈ {None, Conj}` → apply
  `TraceRowViewModel.DefaultTransformFor(cube, PlotType.Rect)`. **That function already is the
  single source of truth** for "what makes a complex cube visible on Rect" (dB20 for S/Y/Z-parameter
  cubes, `Mag` otherwise — see `src/Ui/DataDisplay/CLAUDE.md` §"Auto-transform on add only for
  COMPLEX data"). Reuse it; do not write a second table.
- Then re-derive the spec text: `Trace.Expression = Trace.BuildPickerExpression()`, so the card and
  the Y-axis label read `dB20(SP1.S[:, 1, 1])` — the owner asked for the *trace expression* to
  change, not just the render.
- Network trace: existing behaviour (complex → dB + an added phase trace on the right axis) already
  satisfies the principle. Keep it.

**Complex-valued trace → Smith/Polar (the reverse):**
- Cube-bound with a scalar transform (`dB20`/`dB10`/`dB`/`Mag`/`Phase`/`Real`/`Imag`) over **complex**
  underlying data → `Transform = None` (complex passthrough), then re-derive `Expression`. This is
  the exact inverse of the case above and must round-trip: Smith → Rect → Smith returns the original
  spec text.
- Cube-bound over **real** data → there is no complex quantity to show. Keep the trace and let the
  existing invalid-labeling surface it; **do not delete it** (deleting loses the user's authoring,
  and the reverse switch would restore a perfectly good trace).
- Network trace, `YAxis ∈ {Phase, Real, Imaginary}` → `YAxis = Complex` **instead of deleting it**
  (anchor 3). This changes today's behaviour deliberately.
- Derived metrics: µ ↔ source circles and µ′ ↔ load circles already map both ways — keep.
  `MaxGain`, `K`, `|Δ|`, `Passivity` have **no Γ-plane locus**; there is nothing to map them to.
  These stay the single documented exception. Prefer *keeping* the trace (inert, flagged) over
  deleting it so the reverse switch restores it; if you keep today's deletion instead, say so
  explicitly in the completion note and explain why.

**Anything ↔ Table:** **no transform change at all.** A Table renders scalar and complex cells
alike (`TableRenderer.FormatTraceCell` / `Trace.FormatCubeCell` handle both, honouring
`MatrixFormat` MA/RI/DB). Both directions are pure no-ops on `Transform`/`YAxis`.

**Leaving Table (anchor 2) — narrow the deletion.** Delete only what genuinely cannot exist on the
new plot type:
- `Trace.IsSummaryColumn` → delete (a summary column is a Table construct).
- A rank-0 scalar cube trace → delete (Table-only by design; see CLAUDE.md §"Scalar cubes").
- **Everything else survives** and goes through the mapping above.

**Gate:** for each ordered pair of plot types, and for each trace kind (network element, network
metric, complex cube, real cube, family, contour, summary column, scalar), switching **there and
back** leaves either a rendering trace or an explicitly-flagged one — never a blank plot, never a
silently emptied trace list. Smith `SP1.S[:, 1, 1]` → Rect shows `dB20(SP1.S[:, 1, 1])` and back
again shows `SP1.S[:, 1, 1]`.

## §2 — Rect does not adopt the golden ratio when the plot type is changed [BUG]

Root cause: anchor (5).

**Fix:** after `_plot.SetPlotType(value)` in `OnPlotTypeChanged`, re-apply the container geometry
rule for the **new** type — the same rule the add-plot path uses
(`DataDisplayViewModel.cs:536-545`): Rect → `height = width / AppSettingsViewModel.Instance.RectAspectRatio`;
Smith/Polar → square (`ResizeTo` already enforces this via `IsSquareAspect`); Table → leave free.
Keep the plot's width and derive the height, so the plot does not jump horizontally. Route it
through `PlotContainerViewModel.ResizeTo` rather than assigning `Width`/`Height` directly, so the
minimum-size and square rules stay in one place.

The inspector lives on `PlotInspectorViewModel` and the size on `PlotContainerViewModel` — use the
existing `PlotStructureChanged` notification that `OnPlotTypeChanged` already raises rather than
adding a new back-reference.

**Undo:** a plot-type change that also resizes should undo as one step. Check how the existing
`ResizePlotCommand` (`PlotContainerView.axaml.cs:205`) composes with the plot-type undo entry; if
they would land as two separate undo steps, make the resize part of the plot-type command.

**Popup Plot Inspector:** the owner notes it may need to follow the new aspect. Verify the floating
inspector's placement/size logic against the changed container bounds and fix if it lands
off-target or clipped; if it already tracks the container, say so and change nothing.

**Gate:** a Smith plot switched to Rect via Plot Properties is immediately φ-proportioned (or
whatever `RectAspectRatio` is set to), identical to a freshly-added Rect plot; switching back to
Smith returns it to square; a Table switched to Rect adopts the ratio; the inspector popup stays
correctly positioned across all of these.

## §3 — Smith/Polar axis limits must stay square [BUG]

Root cause: anchor (6) — the dialog writes X and Y independently, so any manual edit distorts the
chart.

**Fix:** on a complex plot type (`IsComplex`), the manual-limits dialog constrains the window to a
square. When the user edits one axis, the other updates automatically to match:
- Reuse `Plot.SquareCentredOnOrigin` (anchor 6) rather than writing a second squaring rule — the
  autoscale path and the dialog must agree, or a manual edit followed by an autoscale will jump.
- Apply the edited axis's span as the square's span, and refresh **both** axes' text boxes
  (`RefreshXText()` / `RefreshYText()`, already present and `_suppressApply`-guarded, `:222-245`) so
  the user sees the coupled value immediately.
- Keep the existing autoscale coupling (`SetBothAxesAutoscale`) unchanged.
- Rect is unaffected — X and Y stay independent there.

**Gate:** on a Smith plot, typing an X max of 2 updates the Y limits to the matching square and the
chart stays circular; the same in reverse; no edit sequence can produce a non-square window; Rect
behaviour is byte-identical to today.

## §4 — "None" and "Conj" must be disabled on Rect for a complex quantity [BUG]

Root cause: anchor (4).

**Fix:** make `TraceRowViewModel.TraceTransformItems` per-trace and per-plot-type instead of
returning a shared static list. Rules:
- **Rect** (and the Rect-like scalar render path), **complex** data: disable `None` and `Conj` —
  both leave a complex value that Rect cannot plot (they are precisely the two arms
  `Trace.RectValueInvalid` flags).
- **Smith/Polar:** only complex-preserving entries make sense — `None` and `Conj`; the scalar
  reductions should be disabled (they are hidden today because the combo collapses on complex plots,
  `IsVisible="{Binding IsRectOrTablePlot}"` — if the combo remains hidden there, no change is
  needed, but the *list* must still be correct for the Table case below).
- **Table:** everything stays enabled — a Table renders complex and scalar alike.
- **Real** data on any plot type: `None` is valid (and `Conj` is meaningless — disable it).
- Keep the existing network-trace exclusions (dB10/dB/Conj) and the existing
  `IsTransformComboEnabled` inert-expression rule; this is an additional filter, not a replacement.

The lists are currently static singletons; per-trace construction means a small allocation per card
refresh — acceptable, but build them from one helper so the rules exist once.

**Gate:** on a Rect plot with `SP1.S[:, 1, 1]` selected, `None` and `Conj` are greyed and
unselectable; `dB20`/`Mag`/`Phase`/`Real`/`Imag` are selectable; on a Table the same trace has every
entry enabled; a real-valued cube on Rect has `None` enabled and `Conj` disabled.

---

## Slice plan

- **P1 — §4 transform-list filtering.** Small, self-contained, and it is the rule §1 leans on.
- **P2 — §1 the remap function**, with the Table-leaving narrowing. The big one; land it alone.
- **P3 — §2 aspect on plot-type change** (+ undo composition, + inspector popup check).
- **P4 — §3 Smith square limits.**

## Constraints / gotchas

- **One mapping function.** If `SetPlotType` and `OnPlotTypeChanged` both make trace decisions after
  this brief, the design is wrong — the model-level function decides, the VM re-syncs the cards
  (`RebuildTraces()`).
- `RebuildTraces()` constructs fresh `TraceRowViewModel`s on every plot-type change — see brief DD-N
  §5 for a card-state initialisation defect on that same path. Land DD-N §5 first if both are in
  flight, or you will chase its symptoms here.
- **The transform is baked for real multi-cube expressions** (`Trace._transformBaked` /
  `TransformIsInert`, CLAUDE.md §"Expression-baked transform"). The remap must not touch a baked
  trace's `Transform`, and must not rewrite a user-typed multi-cube `Expression` — only
  picker-authored specs (`CubeName != null`) get `BuildPickerExpression()` re-derived. Re-read
  CLAUDE.md §"Transform combo must not corrupt a network trace" before touching `Expression`.
- `.cdd` round-trip after each slice: a saved display must reload with the same traces and the same
  container geometry.
- TreatWarningsAsErrors: nullable props → locals; no unused privates; no `<`/`>` in XML doc comments.

## Tests

- **Remap matrix (§1):** parametrised over (oldType, newType) × trace kind — assert the trace still
  exists, `RectValueInvalid` is false wherever a mapping exists, and the round-trip
  A→B→A restores the original `Transform`/`YAxis`/`Expression`. This is the brief's main gate.
- Smith→Rect on `SP1.S[:, 1, 1]` yields `Transform == dB20` and `Expression == "dB20(SP1.S[:, 1, 1])"`;
  Rect→Smith restores `None` and the bare spec.
- Table→Rect keeps ordinary traces and drops only summary columns and rank-0 scalars.
- §2: after `PlotType = Rect`, `container.Height ≈ container.Width / RectAspectRatio`; after
  `= Smith`, `Width == Height`.
- §3: `TryApplyX` on a complex plot leaves `Axes.Window` square; both text boxes refresh.
- §4: transform-item enabled-flags per (plot type × data kind) table.
- `dotnet test tests/Ui.Tests` then `dotnet test tests/Firewall.Tests` (separate invocations).

# Parametric-sweep UX — current model, bugs, and proposed revamp

Status: design discussion (raised by the owner). Nothing here is implemented yet except where noted.

## How it works today

### Engine model: a nested chain
A parametric sweep is `ParametricSweepAnalysis { SweepVarName, SweepValues|Spec, InnerAnalysisName }`.
Nesting is by reference: the outer sweep's `InnerAnalysisName` points to the next analysis, which may be
another sweep, down to a base analysis (DC/SP/HB). A 2-variable FET curve tracer is **three** analyses in
`tb.Analyses`:

```
DC1                      (base: operating point)
SW_Vds  Inner=DC1        (inner sweep)
SW_Vgs  Inner=SW_Vds     (outer sweep)   ← only this one is dispatched at top level
```

`SchematicRunService.RunNetlist` builds `innerOfSweep = { DC1, SW_Vds }` (every name referenced as an
`InnerAnalysisName`) and skips those at top level; only the outermost sweep (`SW_Vgs`) dispatches, and
`ParametricSweepEngine` walks the chain, re-elaborating per point and stacking axes via `StackSweepAxis`.

### Axis order = nesting order  ← the key ground rule
`StackSweepAxis` prepends each sweep axis as the chain unwinds, so the result cube is:

```
[ outer_sweep, …, inner_sweep, <base axes e.g. node> ]
e.g.  I:Ids → [ Vgs, Vds ]      (Vgs outer, Vds inner)
```

**Nesting order therefore determines the plot's default X axis.** With the family convention (last kept axis =
X; see `family-curves.md`), `[Vgs, Vds]` plots Vds = X, Vgs = Family. If you reorder so Vds is outer (`[Vds, Vgs]`), the default
flips to Vgs = X. **This is exactly why your X-axis flipped from Vds to Vgs after re-running** — the sweep
nesting order (not the plot) changed which axis is X. (You can now override per-axis with the Fam/X buttons in
the trace card.)

### The editor
The Analyses editor lists all three analyses (DC + 2 sweeps). Opening the outer sweep shows the whole chain
flattened into sweep-axis rows (Vgs and Vds), so you can edit inner values — and rename any axis — from the
outer dialog. That flattening is why it feels like "everything is editable from one place."

## Confirmed bugs

### 1. `Enabled` is dropped on the run round-trip
`Enabled` IS serialized in `.csch`/`.canl` (AnalysisSerialization) and the dispatcher DOES check
`if (!analysis.Enabled) continue;`. **But the run path is `.cnl`:** `WorkspaceViewModel.RunAnalysis →
CnlWriter → CnlReader → RunNetlist`. `CnlWriter.FormatAnalysis` emits no `enabled=` token, and `CnlReader`
never parses one — so every analysis reloads as `Enabled=true`. The toggle is silently lost. → top-level
disable has no effect.

### 2. Inner/referenced disable has no defined semantics
Even if (1) were fixed: `innerOfSweep` is built ignoring `Enabled`, and `ParametricSweepEngine` runs the
nested chain without consulting each inner's `Enabled`. So disabling an inner sweep or the DC would still do
nothing. Disabling the base DC *should* make the whole plan inert (the sweeps wrap nothing).

## Proposed ground rules
1. A run plan = exactly one **base** analysis (DC/SP/HB), optionally wrapped by an ordered list of
   **sweep axes**.
2. Sweep list order is **outer → inner**; the outer axis varies slowest.
3. Result cube axis order = `[outer, …, inner, <base axes>]`. Nesting order = plot axis order; the **last**
   sweep axis is the default plot X.
4. Enable semantics:
   - Disable the **base** analysis → the whole plan is inert (nothing runs).
   - Disable a **sweep axis** → that axis is removed from the plan; the chain re-links through it. Disabling
     all sweep axes → just the base analysis (a single DC operating point).
5. A variable may be swept by at most one axis in a plan.

## Proposed revamp (UX)
Collapse the three-analysis list into **one** "Analysis" object the user edits in one dialog:
- a base-type selector (DC / SP / HB), and
- an ordered, reorderable list of **sweep-axis rows** (var name, range Start/Stop/Step|Npts, per-axis
  Enabled), with up/down to set outer→inner and a hint: *"top = outer = slowest = first plot axis."*

The nested `ParametricSweepAnalysis` chain becomes a generated implementation detail (build the chain from the
enabled axis rows, innermost-first; skip disabled axes). The dispatcher honors `Enabled` end-to-end, and the
`.cnl` writer/reader round-trip an `enabled=false` token (default true/absent).

This removes the confusions you hit: no separate "outer vs inner" dialogs, renaming happens per-row in one
place, order is explicit, and Enabled does what it says.

## Implementation outline (when approved)
- **CnlWriter/CnlReader:** emit + parse `enabled=false` on every analysis line (default true when absent).
  *(Smallest standalone fix — makes top-level disable work immediately.)*
- **Dispatcher:** build `innerOfSweep` from *enabled* sweeps only; when a sweep is disabled, re-link its outer
  to its inner (collapse the chain) so disabling a middle axis drops just that dimension; if the base is
  disabled, run nothing.
- **Editor VM/view:** one analysis with reorderable sweep-axis rows + per-axis Enabled; generate the chain on
  save.
- **Tests:** disable-base → no run; disable-one-of-two-sweeps → cube drops that axis; reorder → cube axis order
  (and default X) changes accordingly; `.cnl` round-trips enabled.

## Decisions (the owner, locked)
1. **Unified analysis** object: one base analysis + an ordered list of sweep-axis rows.
2. **Enabled** on both the base sim AND per sweep axis. Disabling a sweep keeps its Start/Stop/Step but the
   result loses that dimension (the axis collapses out of the cube).
3. **Innermost sweep axis = default plot X** (last cube axis; 7.3b last-kept=X). Editor rows top→bottom =
   outer→inner, so the **bottom row is your X axis**.
4. **Start/Stop/Step must persist verbatim** — never auto-convert to an explicit Values list — across dialogs,
   save/reload of schematic & workspace, analysis copy/paste, and saved templates.
5. Full revamp, staged as a series of testable briefs.

## Staged implementation plan
**Stage 1 — Persistence foundation (data/serialization only; no UX change).** `brief-sweep-revamp-1-persistence.md`.
  - `.cnl`: emit + parse `enabled=false` on every analysis line (default true when absent).
  - `.csch`/`.canl`/clipboard: extend the `CschAnalysis` DTO to carry the sweep `Spec`
    (mode/start/stop/stepOrCount/kind) so Start/Stop/Step survives; fall back to the Values list only when
    there is no Spec.
  - Fixes "Start/Stop/Step becomes a list" and makes top-level Enabled start working. Independently testable.

**Stage 2 — Dispatcher Enabled semantics (engine).** `brief-sweep-revamp-2-dispatch.md` (written after Stage 1 lands).
  - Build the inner-skip set from ENABLED sweeps only; a disabled sweep collapses (its outer re-links to its
    inner, dropping that dimension); a disabled base → nothing runs.

**Stage 3 — Unified editor UX (UI).** `brief-sweep-revamp-3-editor.md` (written after Stage 2 lands).
  - One Analysis = base-type + reorderable sweep-axis rows (var, mode Start/Stop/Step | Npts | List, range,
    per-axis Enabled). Generate the nested chain on save (top=outer … bottom=inner). Preserve each row's mode
    so Start/Stop/Step never expands to a list in the UI.

Staged so each lands and is verified before the next brief (Sonnet over-reports — verify on disk).

## Resolved questions
1. One unified "Analysis + sweep axes" object (recommended) vs keep separate analyses but fix Enabled + show
   order clearly?
2. Default plot X = last (innermost) sweep axis — keep, or make the *outer* axis the default X?
3. Quick partial fix now (just round-trip `enabled` so top-level disable works) before the full revamp, or do
   the whole revamp at once?

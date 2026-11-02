# Brief 6 — a region belongs to a layer

**Series:** [Impedance review](brief-impedance-0-overview.md) · **Tag:** `R-imp6-n` · **Phase:** behaviour change (owner decision 2026-09-28)
**Area:** `src/Design/Layout/Em/TraceImpedanceScope.cs` (`TraceScopeRegion`),
`src/Design/Layout/Em/TraceImpedanceAnalysis.Selection.cs`, the `.clay` impedance-review persistence,
`src/Ui/Layout/LayoutEditorViewModel.ImpedanceScope.cs`, `src/Ui/Layout/Impedance/ImpedancePanelViewModel.cs`,
`src/Ui/Views/Impedance/ImpedanceToolView.axaml`, `src/Render/Layout/LayoutOverlay.cs`,
`src/Design/Layout/Em/TraceCrossSection.cs` + `TraceImpedanceProbe.cs` (§3), `src/Cli/Impedance.cs`
**Depends on:** 4 · **Blocks:** nothing
**Rule:** the boards, their vendors and the reporter must not be named anywhere in the repo.

---

## 0. What was reported

On a six-layer board a designer drew lasso regions to review the RF area, then turned to the bottom
layer: he could not lasso-select there, and when he enabled another layer the regions from the top
layer were still drawn. Brief 4 made a region a BOARD area that applies to every layer
(`TraceScopeRegion`'s own comment says so) — so what he saw is the design, and the design is what he
reads as a bug. The owner has decided: **regions are per layer.**

## 1. `R-imp6-1` — a region carries its layer

- `TraceScopeRegion` gains an optional `LayerName` (the technology layer name, the way
  `TraceWidthSelector` and `TracePick` already name theirs). Null means every layer — which is what every
  region saved before this brief reads as, so an existing `.clay` review is unchanged. Check every
  registration point of the review's persistence (reader, writer, `Clone`, `Same`, the CLI's scope
  flags and `serve`'s tool schema).
- A region drawn with the Rectangle or Lasso tool takes the layer the reviewer is working on: the
  layout editor's CURRENT layer where that is a conductor layer the review analyses; else the single
  analysed layer where only one is checked; else null (all layers), and the region's row says "all
  layers".
- Selection: a trace is selected by a region only on the region's layer (or on any layer when null).
- The panel's region list shows each region's layer and lets it be changed (including to "All layers").
- The canvas draws a region only while its layer is shown / analysed; an all-layers region is always
  drawn, with a visibly different outline so it is not read as a leftover.

## 2. `R-imp6-2` — "cannot lasso on the bottom layer"

Reproduce before fixing: draw a lasso with only the bottom conductor layer analysed and the bottom
layer current; the traces inside must select. If they do not, find why (the scope tool's arming, the
hit layer, or the selection's layer test) and fix it under §1's rule. Say in RESOLVED what the cause
was — it may be §1 itself, or a separate defect.

## 3. `R-imp6-3` — "solved to no capacitance", the cause

Round 9 replaced the bare sentence with one that explains it and says what to do (a cut running into
copper that touches the trace). The CAUSE was stated as the usual one, not proven. Build the case: a
trace entering a pad or via land, probed at the neck. If a touching same-layer piece is being added to
the cut as a separate "ground" conductor (`TraceCrossSection`'s candidate loop takes every band within
reach, and one that overlaps the signal has lateral and vertical distance 0), exclude copper
galvanically joined to the signal on its own layer from the ground set, or trim the signal to the
trace's own width. Gate: the probe at that point gives a Z₀ within 1 % of the same trace probed two
widths away, instead of a refusal.

## 4. Gates

1. A review saved before this brief opens with its regions as all-layers and selects exactly what it
   selected before.
2. §1's layer rule: a region drawn on Bottom selects no Top trace, and is not drawn with Top alone shown.
3. §3's neck probe prices.

## 5. Tests (minimal)

One per claim above. `--filter FullyQualifiedName~…` on `ImpedancePanelTests`, the scope tests and the
cross-section tests — never the whole of `Ui.Tests`.

## 6. On completion

`src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md`, never any `CLAUDE.md`. Update the impedance user doc
source (no DocGen run).

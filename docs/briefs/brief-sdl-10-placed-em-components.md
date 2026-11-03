# Brief SDL-10 — Placed EM components: per-instance geometry and optimisation

**Series:** [Schematic-driven layout](brief-sdl-0-overview-and-groundwork.md) · **Tag:** `R-sdl10-n`
**Area:** `src/Design/Layout/Em/ParametricEm/` (SDL-6), `src/Design/Layout/PCells/GeneratedCellStore.cs`,
`src/Design/Layout/SchematicSync/` (the planner, `ConnectivitySignature`), `src/Design/ThreeD/`
(`C3dBindings`, `C3dVariable`), `src/Design/Optimization/` (`OptimizationRun`, write-back), the instance
properties panel
**Depends on:** SDL-6 and SDL-7 (SDL-8 for 3D components).

---

## 0. What this adds

The series supports two ways of working, and both are first-class:

1. **Optimise in the schematic you drew** (SDL-6 Path A, SDL-7): the schematic is simulated by EM of its
   own layout and its own VARs are optimised.
2. **Place an EM component in a larger schematic and optimise it there** (SDL-6 Path B): a coupler, a
   filter or a transition, simulated by EM, sits among other circuitry, and the optimiser varies the
   component's own variables, the circuitry around it, or both.

SDL-6 Path B makes a placed EM component simulate at the values its instance gives it. This brief makes
the second way of working complete: which components can be placed and varied, what the parent's
layout shows when an instance's values differ from the cell's, how several instances are optimised,
and where an optimised value is written.

## 1. Decisions

- **D1 — Three kinds of EM component, by what their geometry depends on.**

  | Kind | Its geometry is a function of | Variables the optimiser can vary | Circuit model for `em=correct` |
  |---|---|---|---|
  | **Schematic-driven** | the cell's VARs and parameters, through SDL-0's planner | the instance's VAR and parameter overrides (SDL-6 D7) | the cell's own schematic |
  | **3D-parametric** | a `.c3d` whose dimensions use VARs linked to the cell's VARs or parameters (`C3dBindings`, SDL-8 D3); no schematic drives it | the same overrides | the cell's schematic if it has one; otherwise none |
  | **Fixed** | nothing: a drawn layout with a `.cem`, or a one-off EM result (`<setup>_sim`) | none of its own | — |

  SDL-6's EM-capability rule widens accordingly: a cell is EM-capable when it has an EM view whose
  geometry follows its variables (the first two kinds). A fixed component still simulates by EM at its
  one geometry, and the optimiser varies only the circuitry around it, exactly as today; `explain
  --tunables` says so for it rather than listing nothing.
- **D2 — `em=correct` needs a circuit model; without one, `em=direct` is the only mode.** A
  3D-parametric cell with no schematic has nothing for the circuit-model search to run on. With `em=`
  unstated, such a component selects `direct` and the run says why; an explicit `em=correct` is a
  refusal naming the component. A cell may carry a schematic that is a circuit model only (it drives no
  geometry); that is enough for `correct`.
- **D3 — The parent's layout shows each instance at its own values.** When the parent schematic is laid
  out (Update Layout from Schematic), an instance of a schematic-driven cell whose values differ from the
  cell's own needs its own geometry, and a cell has one saved layout. Each such instance gets a
  **variant cell**: the cell's saved layout run through the planner at the instance's values (SDL-4's
  rules, so the cell's hand edits are in every variant), stored through `GeneratedCellStore` and keyed on
  the SDL-6 geometry hash, so instances with the same geometry share one variant and the cell's own
  layout is untouched. Exactly the copy-on-write PCells already have (R-L5-1, R-L5-2). Variants are
  generated and read-only: an edit is made in the cell and reaches every variant on the next update. A
  3D-parametric cell's layout view, if it has one, cannot follow its variables; its placements are
  reported as showing the cell's own layout.
- **D4 — The parent's copper stays joined when a component's ports move.** A variant whose pins sit
  elsewhere (a longer coupler) is a part whose pin poses changed; the parent's re-run propagates that as
  SDL-4 propagates any length change, so the parent's lines stay connected to the component.
- **D5 — Connectivity is held at both levels.** During an optimisation, SDL-7 D9's signature is checked
  for each EM component's own geometry and, when the parent schematic is laid out, for the parent's
  layout with the variant in place. A point that keeps the coupler intact but pulls its port off the
  parent's feed line is infeasible like any other, with no solve spent on it.
- **D6 — Several instances are independent.** `X1:w50` and `X2:w50` are separate tunables with separate
  cache keys; the solve count and budget count distinct geometries, so two instances at the same values
  cost one solve. Tying them together is the designer's choice and already expressible: both overrides
  name one parent VAR.
- **D7 — An optimised value is written where it is defined, never further.** A tunable that is a parent
  VAR is written to the parent's VAR block; a tunable that is an instance override (`X1:w50`) is written to
  that instance. The cell itself is not changed by Apply, because it may be placed elsewhere with other
  values. A separate command, **Make These the Cell's Own Values**, writes an instance's overrides into the
  cell's VAR block or defaults and clears them from the instance; before writing, it lists every other
  placement of the cell in the workspace and what each will change to. After Apply, the parent's layout is
  updated (D3, D4) and its saved geometry checked against the EM-verified best point, as SDL-7 R-sdl7-8
  does for Path A.

## 2. Work

- **`R-sdl10-1`** The three kinds and the widened EM-capability rule (D1), in `check`, `explain` and the
  instance's properties (a line stating the kind).
- **`R-sdl10-2`** Mode selection and refusal for components with no circuit model (D2).
- **`R-sdl10-3`** Variant cells in the parent's layout (D3) and propagation of their pin moves (D4).
- **`R-sdl10-4`** Two-level connectivity during optimisation (D5).
- **`R-sdl10-5`** Write-back by definition site, and Make These the Cell's Own Values with its listing
  of other placements (D7). Replaces the Path B sentence of SDL-7 R-sdl7-8.

## 3. Tests

A parent schematic `T1` holding two instances of the C2 cell, a 50 Ω feed line to each, and goals on the
parent's S-parameter analysis:

- **Variants:** with X1 at the cell's values and X2 at a larger `L90`, the parent's layout places the
  cell's own layout for X1 and a variant for X2; the cell's `.clay` bytes are unchanged; a third instance
  at X2's values reuses X2's variant.
- **Propagation:** lengthening X2's `L90` moves the parent's feed line ends with X2's ports; LVS clean.
- **Independence:** optimising `X1:w50` and `X2:w50` with goals on each branch gives different values;
  two instances at equal values cost one solve.
- **Two-level connectivity:** a range for `X2:L90` that pulls X2's port off its feed line is infeasible
  with no solve.
- **Write-back:** `X1:w50` goes to X1, a parent VAR tunable goes to the parent's VAR block, the cell is
  unchanged; Make These the Cell's Own Values lists both placements and, confirmed, writes the cell and
  clears the override.
- **3D-parametric:** a cell with only a `.c3d` bound to a linked VAR optimises under `direct`; explicit
  `correct` is refused by name.
- **Fixed:** a `<setup>_sim` component lists no tunables of its own and says why; the circuitry around it
  optimises as today.
- Stub provider throughout (SDL-6); no real solves in this brief.

## 4. Docs

`em-cells.md` gains *Placing an EM component and optimising it*: the three kinds, per-instance
geometry, several instances, where results are written, Make These the Cell's Own Values.
`layout-view.md` §9.1: D3-D4.

## 5. Gate

As SDL-7, plus `--filter "FullyQualifiedName~GeneratedCell|FullyQualifiedName~ThreeD"`. UI: the
instance's kind line and the Make These the Cell's Own Values confirmation rendered with
`tools/UiSnapshot`, looked at, PNGs listed. Findings in `src/Design/RESOLVED.md`.

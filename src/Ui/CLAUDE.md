# UI (Avalonia) — local conventions

Standing instructions for `src/Ui`. Read with the root `CLAUDE.md`. The UI is how people drive the
engine; it must never become the source of truth for simulation.

## Framework & patterns
- **Avalonia 12**, single codebase for Windows/macOS/Linux. Mirror splotRF's structure, controls,
  and packaging recipes wherever possible — it's our proven sibling app (same stack, MIT).
- **MVVM via CommunityToolkit.MVVM.** Views are thin; logic lives in view models. No simulation
  logic in code-behind.
- **The GUI never simulates the design layer directly.** It always builds/edits the design layer,
  then asks the engine to elaborate and run. Results come back as a **`DataSet`** (named
  single-kind `DataCube`s).

## Schematic canvas — the performance-critical control
- **Do NOT render components as individual styled controls.** A 10,000-component schematic will die
  that way. Render the canvas yourself via a custom control (`DrawingContext`, dropping to
  **SkiaSharp** for hot paths), with **viewport virtualization** and a **spatial index** (e.g.
  quadtree/grid) for hit-testing and pan/zoom.
- Lean on Avalonia 12's rendering work (deferred composition, dirty-rect tracking) but verify with
  a large stress schematic in `testdata/`, not a toy one.

## Wiring & auto-routing
- Obstacle-aware auto-routing must avoid drawing wires over placed symbols. Start with **orthogonal
  A\*** over a coarse grid using the spatial index for obstacles; refine later. Keep the router
  separable and testable headless.

## Editing model
- **Undo/redo via the command pattern** across all editors (schematic, symbol). Every mutation is a
  reversible command; nothing mutates model state directly.
- **Copy/paste via the system clipboard**, all or a selectable subset of a schematic, to/from other
  schematics.
- **Hierarchy navigation:** push into a sub-cell's schematic, edit, pop back. Editing a cell affects
  every instance; instance-level parameter overrides (root `CLAUDE.md` → expressions) stay per-instance.

## Data Display
Plots and tables are **splotRF** controls from `RfCore` — do not reimplement Smith/polar/rectangular
plotting here. Support measured-vs-simulated overlay (a lab Touchstone over a simulated result
cube from the `DataSet`).

## "Easy" budgets (testable)
Honor the PRD §12 click budgets (e.g. placed FET → running HB power sweep in ≤ 8 actions). Advanced
settings must remain reachable but must not clutter the default path.

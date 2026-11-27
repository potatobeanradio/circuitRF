# Brief PCD-1 — The seams, EM copper for generator cells, and the EM step floor for placed cells

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd1-n`
**Area:** the seams of PCD-0 §5; `src/Design/Cells/CellPersistence.cs`, `src/Design/Schematic/SchematicPersistence.cs`,
new `src/Design/Schematic/PCellDesignPlacement.cs`; `src/Design/Layout/Em/EmPartKinds.cs`,
`src/Design/Layout/Em/ParametricEm/ParametricEmProvider.cs`; `src/Design/Optimization/OptimizationRun.Em.cs`,
`src/Design/Optimization/OptimizationDiagnostics.cs`
**Depends on:** PCD-0. Every other PCD lane depends on `R-pcd1-1`.

---

## 0. What this adds

A designed cell is a PCell whose layout is metal. EM must solve a placed one as copper, and an optimisation over its
lengths must keep its steps above the database unit, as a schematic's own lengths already do. Before that, every lane
needs the seams to build against.

## 1. Requirements

- **`R-pcd1-1` — the seams commit.** Every row of PCD-0 §5, with its signature exactly as written: §4.1's `.ccell`
  fields, §4.2's records and `.csch` fields, `EmEvaluation.Apply` made `internal`, the stubs of seams 4 to 11
  (`DesignedCellLayout.At` real), seam 12's registration hooks, and seam 13's round-trip test. One commit, before any
  other lane edits.
- **`R-pcd1-2` — EM copper for a generator cell.** `EmPartKinds.Shallow`: a part whose cell names a `LayoutGenerator`
  and whose `.ccell` has `EmCopper`, or a `pdk://` part whose in-memory `.ccell` (`PdkKitRegistry.Find`) has it, is
  `Copper`. Everything else is classified as before.
- **`R-pcd1-3` — the EM step floor for placed cells.** The two-database-unit floor (SDL-7 D5) also applies when the run
  has no Path A (EM cells placed in a parent, SDL-10). Its database unit is the coarsest of the placed cells' evaluated
  layouts, and it covers every length coordinate, a placed cell's own parameter (`X1.Lpad`) included.
- **`R-pcd1-4` — the no-circuit-model sentences.** `EmDirectForNoCircuitModel` and `EmModeNeedsCircuitModel` no longer
  say that the cause is a 3D-parametric cell. Since `MSHAPE` with `Model=none` (PCD-3), a cell can lack a circuit model
  for more than one reason.

## 2. Decisions

- **D1 — Copper wins before the model-cell test.** A generator cell with `EmCopper` returns `Copper` without a cell
  folder, so `IsModelCell` (brief-icp-9) does not read its schematic. The generator's output is what EM solves, and the
  cell's own schematic is its circuit model, not its layout. A generator cell without `EmCopper` stays `Part`.
- **D2 — The `pdk://` workspace is the schematic's own.** `PdkKitRegistry.Find` takes the workspace holding
  `schematicDir`, as `CellSymbolResolver` resolves a kit part.
- **D3 — The database unit travels on the record.** `EmEvaluationRecord.DbuPerMicron` carries the evaluated layout's
  unit. The floor reads the smallest `DbuPerMicron`, which is the coarsest unit, so no cell sees a step below two of its
  own units. With Path A the floor reads the cell's `.clay`, as before.
- **D4 — Neutral wording.** The sentences state the fact (no circuit model, so EM only) and the remedy (`em=direct`, or
  give it a circuit model). `EmModelSwitch.NoCircuitModelSentence` says the same thing, but its file belongs to PCD-3,
  which rewords it.

## 3. Tests

`tests/Ui.Tests/PCellDesigner/Pcd1*.cs`, one per requirement: `Pcd1SeamsTests` (`R-pcd1-1`) and `Pcd1EmTests`
(`R-pcd1-2` to `-4`).

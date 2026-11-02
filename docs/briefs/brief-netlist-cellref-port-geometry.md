# Sonnet Brief — Net extraction: use the resolved cell symbol's pins for cell-reference instances

**Bug:** A cell-reference instance's ports are extracted at the **wrong coordinates**, so its pins never union
with the wires the user attached → the instance gets isolated auto-named nets and is silently disconnected in
the netlist (e.g. `cell3:X1 n2 n3` instead of binding to the input/output nets). Every `Generic`+`CellRef`
instance is affected. In S-param analysis this produces floating nodes → singular MNA → per-frequency
regularization.

**Root cause (confirmed):** `NetExtractor` computes a cell instance's port world-coords from the **built-in**
`SymbolPortDefs.For(comp.Symbol, comp.PortCount)` + `EditableComponent.GetPortWorldCoord`. For a cell-ref the
`Symbol` is `Generic`, so this hits the default `[("1",0,-200),("2",0,+200)]` (a vertical 2-port placeholder),
and `PortCount` is hardwired to 2 — **independent of the referenced cell**. The actual pins come from the
resolved `.csym` symbol. The render/connectivity model already does this correctly via
`SchematicEditModel.PortDefsOf` / `PortWorldOf` (cell-ref-aware: `CellSymbolResolver` → `resolvedSym.Pins`), so
the editor *shows* the instance connected while extraction uses placeholder geometry and disconnects it.

**Fix:** make `NetExtractor` use the **same cell-ref-aware pin geometry as the render model** for every
component, instead of `SymbolPortDefs.For(comp.Symbol, comp.PortCount)` + `comp.GetPortWorldCoord(pi)`. Single
source of truth = `SchematicEditModel.PortDefsOf(comp, resolutions)` (built-ins → `SymbolPortDefs`; cell-refs →
resolved `.csym` pins, each with its `PortIndex`) + `PortWorldOf(comp, def)`. Both are `internal` on
`SchematicEditModel` and `NetExtractor` is in the same assembly, so they're callable. File:
`src/Ui/Schematic/NetExtractor.cs` (no change to `EditableComponent` — it has no access to `SchematicDirectory`
/ the resolver, which is why this must be done at the model level).

## Changes in `NetExtractor.ExtractModel` (and its helpers)
1. **Pre-resolve cell-refs once** for this model (mirror `BuildRenderModel.ResolveAllCellRefs`): build a
   `Dictionary<string compId, CellSymbolResolution>` via `CellSymbolResolver.Resolve(comp.CellRef,
   model.SchematicDirectory)` for every `comp.CellRef is not null`. Pass it into `PortDefsOf(comp, resolutions)`
   so each component resolves once. **Ensure `model.SchematicDirectory` is set** for both the top model and each
   recursed sub-cell model (`res.Schematic` from `cells.Resolve`) — nested cell-refs (cell3 inside cell1) need
   it to resolve their pins; if a resolver doesn't set it, set it from the resolved cell's folder.
2. **Replace every port-geometry callsite** that currently uses `SymbolPortDefs.For(comp.Symbol,
   comp.PortCount)` + `comp.GetPortWorldCoord(pi)` with `PortDefsOf(comp, resolutions)` +
   `PortWorldOf(comp, def)`. Iterate `def` (which carries `PortIndex`); use `def.PortIndex` for the
   detached-port check (`comp.IsPortDetached(def.PortIndex)`) and for ordering. Callsites:
   - **Layer-1 seeding** (component-pin `uf.Add`).
   - **Short-disable** union loop.
   - **`AssignNetNames`** deterministic auto-name ordering scan.
   - **`EmitInstance`** (built-in primitives) — `PortDefsOf` returns the built-in defs unchanged, so behavior is
     identical for non-cell components; route it through the same helper for consistency. Keep `ZPort`'s
     ref-pin handling (match on `def` name `"ref"` as today).
   - **`EmitCellInstance`** — derive port count + per-port net from the **resolved pins in `PortIndex` order**
     (so `NetBindings[k]` = parent net at the cell's interface pin `k`, matching `Cell.Ports` order). Change the
     binding-contract guard to compare `cellDef.Ports.Count` against the **resolved pin count** (not the
     always-2 `SymbolPortDefs` length).
3. A small private helper is cleanest, e.g.:
   ```csharp
   // Cell-ref-aware port world-coords for a component, in PortIndex order.
   private static IEnumerable<(double X, double Y, int PortIndex)> PortWorldCoords(
       SchematicEditModel model, EditableComponent comp,
       Dictionary<string, CellSymbolResolution>? res)
       => model.PortDefsOf(comp, res).Select(d => { var (x,y) = model.PortWorldOf(comp, d); return (x,y,d.PortIndex); });
   ```
   Use it everywhere above. (`NetForPort`/`NetAt` already take explicit (px,py) — feed them the helper's coords.)

Detached-port synthetic keys: keep the existing scheme, but key on `def.PortIndex` from `PortDefsOf` (cell-ref
pins carry their own `PortIndex`).

## Why this is the right fix
The editor's render + connectivity (`ToRenderComponent`, `ComputeConnectivityGeometry`) already use
`PortDefsOf`, so the canvas connection dots / pin-connected state are correct. Extraction is the lone holdout
using placeholder geometry. Aligning it to `PortDefsOf` makes "what you wired" == "what gets netlisted" by
construction, and fixes the ≠2-pin cell case for free.

## Tests — `tests/Ui.Tests/NetExtractorHierarchyTests.cs`
(Build models in-memory; set `SchematicDirectory` and provide an `ICellResolver` + a `CellSymbolResolver`-
resolvable `.csym`, mirroring existing hierarchy tests. If those tests already have a cell-resolver harness,
reuse it.)
1. **`CellInstance_PortsOnResolvedPins_NetThrough`**: a cell whose `.csym` pins are at **horizontal** offsets
   (e.g. local ±200 on X — *not* the built-in vertical default), instantiated with wires to those pin world
   coords. Assert the instance's `NetBindings` equal the **wire nets** (the instance binds through), not fresh
   auto-names. This is the exact reported failure (cell3 in cell1).
2. **`ThreePortCell_AllPortsBind`**: a 3-interface-pin cell instance → no binding-count conflict; all three
   ports bind to their wires (guards the always-2 `PortCount`/guard bug).
3. **`BuiltInComponent_Unchanged`**: a plain R/L/C still extracts identically (regression — `PortDefsOf` returns
   the built-in defs).
4. **Round-trip (the real gate):** extract cell1 (R1 + R2 + cell3 instance X1 + two Pins) → elaborate →
   cell3:X1's two ports connect to the cell1_input net and the R2/output net respectively (no isolated nets, no
   floating nodes). If practical, assert an S-param run of cell2 (the testbench) no longer reports
   per-frequency regularization.

## Gate
The three uploaded cells (cell1/cell2/cell3) netlist with every cell instance's pins bound to the wires drawn
to them; the S-param testbench (cell2) solves without per-frequency regularization. Build 0W/0E; suite green.
Existing Layer-1/2/3/Pin/Hierarchy extractor tests stay green.

## On completion
Note in `src/Ui/CLAUDE.md`: net extraction uses `SchematicEditModel.PortDefsOf`/`PortWorldOf` (cell-ref-aware,
the render model's single source of truth) for component port positions — built-in `SymbolPortDefs` is the
fallback for non-cell components only; cell-reference instances use the resolved `.csym` pins.

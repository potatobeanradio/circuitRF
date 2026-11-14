# Brief ICP-5 — `_cosim` puts each cut instance's own model back on its ports

**Series:** ICP-0 … ICP-9 · **Tag:** `R-icp5-<n>` · **Depends on:** ICP-2 (ICP-3/4 for their port lists)
**Area:** `src/Design/Layout/Em/EmCoSimNetlist.cs` (`EmCoSimPart`, `ModelsFor`, `ModelsForCell`, `Closures`), `EmRunService.cs`
(`WithCoSim`), `EmSnpSchematic.cs`, `EmCoSimNetlist.CreateCell`.

## The gap

`Closures` finds a part's model in the board schematic's top level by `SchematicId` or refdes, and maps a multi-pad part's
terminals onto its footprint pins **by position**. A cut instance differs in four ways: it can be nested, so its model is in
its parent cell's schematic with parameters bound in that parent's scope; its pins are named; some pins may have no port
(a source grounded through vias inside the cell, a thermal terminal); and it may have no schematic counterpart at all (a
PCell placed by hand).

## Decisions

- **D1 — A top-level instance keeps today's treatment**: its overrides written as expressions, the schematic's VARs and
  cell parameters carried (`EmCoSimModels.Variables`), so tuning works without re-running EM.
- **D2 — A nested instance's parameters are written as resolved values.** They are expressions in the parent's scope,
  bound by the parent instance's overrides; carrying that scope into a flat netlist would mean substituting expressions,
  which the expression engine never does. The netlist comment names every parameter written this way, and says a VAR
  that drove it no longer tunes it here (ICP-0 § Risks 4).

## Requirements

**R-icp5-1 — The writer reads paths and pin names.** `EmCoSimPart` carries the instance path, its schematic chain
(ICP-1), its pins by name, and for each port the pin it is. Both builders (Planar, 3D) produce it. Output for every
footprint part is byte-identical.

**R-icp5-2 — Which model.** In order:
1. **The schematic counterpart.** Top level: the cell's Primary schematic (`ModelsFor`), by `SchematicId`. Nested
   `s1/…/sn`: follow `s1`'s schematic instance to its cell (`CellRef`), that cell's Primary schematic, `s2`'s `SchematicId`
   in it, and so on. A device keeps its model and parameters; a sub-block is its cell instance (the netlist's `Library`
   carries the definition, as `EmCoSimModels.Library` already does); an SnP is the SnP instance. Parameters per D1/D2,
   nested values read from the elaborated design at that path.
2. **A PCell with no schematic counterpart:** the component its generator makes (`SchematicLayoutPlanner`'s
   generator-to-symbol mapping, read the other way), with the snapshot's parameters (`PCellSnapshots`) in that component's
   own parameter names — only where the mapping is one-to-one; the comment says the model came from the cell.
3. **Otherwise** a placeholder, per LCP-7, named in a comment with why.

**R-icp5-3 — Terminals by name.** Each model terminal maps through its symbol pin name to the pin of that name: a pin with
a port → the port's node; the reference pin (ICP-4) → `0`; a grounded pin (ICP-4 R-icp4-5) → `0`; an *abutting* pin
(ICP-2 R-icp2-3: its face touches only another cut-out instance) → one node shared with the pin it touches, named
`j_<instance>_<pin>`, so the two models connect directly as they do in the chip; a pin with no port whose net in the instance's own schematic is
ground → `0`; any other unported terminal → `nc_<instance>_<pin>`, with a warning naming it (a thermal terminal included:
the thermal network is not in the co-simulation). Pin names that do not match the symbol's are a placeholder, listing both
sets. A cell whose pins are Port numbers (`CellPins.FromSchematicPorts`) maps by `Num`.

**R-icp5-4 — Legal names.** An instance placed in the netlist takes a name the `.cnl` reader accepts, derived from its path
(`X1/Q3` and `@2` are not identifiers), with the path in its comment; two paths cannot collide. The reader round-trip is
gated, because a name it skips is silent (LCP review: spaced cell names were dropped).

**R-icp5-5 — The `_cosim` cell.** With Create SnP schematic on, a run with cut instances makes `_cosim` as LCP-7 does. When
any cut is nested, the cell is drawn from the netlist (`netlist --to-schematic`'s route) rather than from the top
schematic, which does not contain the nested instance; the run's note says so.

## Gates

No EM solve: write a small synthetic Touchstone, as LCP-7's gates do. One test per claim:
1. A synthetic N-port with a three-terminal instance (a resistor tee sub-cell with pins `A`, `B`, `C`, `C` unported and
   grounded in its schematic): `sparam` on the written `.cnl` equals closing the same matrix by the terminated-network
   formula, to 1e-6.
2. A nested instance whose parameter is an expression of its parent's cell parameter is written with the value the
   elaborated design gives it, and the comment names it.
3. A hand-placed `KIT_MIMCAP` with no schematic gets the kit component with the snapshot's capacitance.
4. Pin names that do not match the symbol give a placeholder and the warning lists both sets.
5. Every derived instance name survives `CnlReader`.
6. A footprint-part netlist is byte-identical to before.
7. Two abutting instances share one node in the written netlist, and a grounded pin's terminal is on `0`.
8. ICP-4's gate 4, moved here: a pin-referenced port (`ReferencePin` on the port) puts the model's reference terminal on `0`, and the
   netlist comment says it is tied to the N-port's reference, which is not the same as the pin being grounded in the chip.

## Not here

Running HB or loadpull on the result (it is an ordinary netlist; nothing here changes how it runs). EM cells (ICP-8).

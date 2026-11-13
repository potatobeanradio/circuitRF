# Brief series ICP — component ports on any cell instance in an IC layout hierarchy

**Goal.** A `.cem` (or a `.c3d` setup) can name any placed cell instance, at any depth, as a component port. The instance's
geometry is left out of the EM problem, each of its connected pins becomes a port at the face where the surrounding
interconnect meets it, the Touchstone names those ports `Q1.G`, `X1/LNA.IN`, and the run's `_cosim` netlist puts the
instance's own model back across them: a device with its parameters, a sub-block's schematic or S-parameters, a passive
cell's schematic model. The EM then solves only the interconnect between the cells that were cut out.

**Origin.** GitHub issue 69. It asks for the black-box flow that component ports already give a PCB, on MMIC and RFIC
layouts, where the parts are cell instances rather than footprints with pads.

## Why a series

Component ports today are footprint parts only, and four things stop a cell instance from being one:

1. **Only footprint placements are parts.** `EmPartPads.Project` reads parts through `BoardCompanions.Project`, i.e. pads
   with a reference designator, top level only. A PCell instance with layout pins is never a candidate, so the shipped
   `PDK PCells/SpiralResonator` `.cem` asked for `"*"` refuses with *no part placed on this layout has two pads*. A nested
   instance (`X1/Q3`) has no spelling at all, and an instance with no designator (both instances in that example) has no
   name.
2. **Nothing is ever left out.** A footprint part's copper is the pads, which stay in the problem; the construction adds a
   strip and a gap between them. A cell instance's shapes *are* the part (a MIM cap's plates, a transistor's fingers and
   source vias), so they have to leave the problem or the part is counted twice. `EmGeometry.Flatten` takes every
   instance's shapes, with no way to except one.
3. **The construction is a gap between two facing pads on one layer.** A transistor's pins face three directions, a MIM
   cap's two pins are on two layers, and a sub-block's pins may be far apart. None of them is a gap across a part.
4. **The co-simulation reads the board schematic's top level, by refdes, and maps terminals by footprint pad order.** A
   nested instance's model lives in its parent cell's schematic, its parameters are bound in that parent's scope, and an
   IC cell's pins are named (`G`, `D`, `S`, `IN`), not numbered pads.

## What already exists and is reused

| Need | Existing piece |
|---|---|
| An instance's name, and a path segment for an unnamed one | `LayoutDesignFlatten.PathOf` — the designator, else `@n` — already the LVS spelling |
| Taking a cell's copper out by instance path and reading its terminals at its pins | LVS bodies (`LayoutReadBodies`, `LayoutRead.cs:205`) |
| A pin as a face: point, width, outward direction, layer | `CellPins.Resolve` → `LayoutPin { X, Y, WidthDbu, OutwardDeg, Layer }`; a kit puts pins on the metal's own end face (kit RULE 11) and Update Layout makes them from Port labels (`CellPins.FromSchematicPorts`) |
| A ground-referenced port at a conductor's end face | Planar `PlanarPortKind.Edge` (grounded return below the crossover, LFE; de-embedded to the drawn face, R-fed-2); 3D `EdgePortSheet` |
| A port between two conductors | Planar `PlanarPortReference.SecondConductor` (RP-2a, `LabelShape.PortReturn`); 3D the gap sheet `C3dComponentPorts` builds |
| One place every caller gets geometry and port labels | `EmGeometry.ForSetup`, `EmComponentPorts.Collect(source, setup)` (R-lcp1-4) |
| What a schematic part is to an EM result: copper, a part, or nothing | `EmModelSwitch.KindOf` (`ParametricEm/EmModelSwitch.cs:267`) |
| Putting models back on ports | `EmCoSimNetlist.Closures`, `ModelsFor`, `WithCoSim` (LCP-7) |

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-icp-1-instances-and-format.md` | Which instances: the hierarchy walk, paths, pins in world coordinates, the `.cem` entries, the selection rules. No solve | — |
| `brief-icp-2-planar-cutout.md` | Leave the instance out and put an edge port at each pin face, on Planar. Starts with the measurement in § Risks | 1 |
| `brief-icp-3-3d-cutout.md` | The same cut and ports on a 3D `.cem` and in a `.c3d` setup | 1, 2 |
| `brief-icp-4-pin-reference.md` | A port referenced to one of the instance's pins instead of ground | 2, 3 |
| `brief-icp-5-cosimulation.md` | `_cosim` places each instance's own model on its ports, at any depth | 2 |
| `brief-icp-6-cli-and-check.md` | `em`, `explain`, `check`, the MCP tools and the format reference | 1, 2, 5 |
| `brief-icp-7-cem-editor-and-marks.md` | The instance tree in the Component ports section, and the cut drawn on the layout | 1, 2 |
| `brief-icp-8-em-cells.md` | `model=em` runs a schematic whose devices are not copper, through component ports | 2, 5 |
| `brief-icp-9-docs-and-example.md` | User pages and a shipped MMIC example | all |

ICP-1, 2 and 5 are the issue's whole request on Planar. 3 and 4 carry it to 3D and to pin references; 6 and 7 are its
`check`/`explain` and UI; 8 removes a refusal that exists only because this did not.

## Decisions already made

- **D1 — One list, one word.** An instance is an entry in `EmSetup.ComponentPorts`, as a footprint part is. Users call
  both component ports. What gets built is chosen per entry by what the instance is: a footprint part (pads, a
  `Footprint` or `LayoutPartKind`) keeps LCP's gap; any other cell instance is cut out. `Construction` on the entry
  overrides the inference. Every footprint part's result is byte-identical.
- **D2 — Names are LVS's.** A path segment is `LayoutDesignFlatten.PathOf` (designator, else `@n` by position in its
  parent's `Instances`), joined by `/`. A port is `<path>.<pin>`. One spelling for LVS, `explain`, the Touchstone header and
  the `_cosim` comments, so a device can be cross-referenced. `@n` changes when instances are reordered; the note says to
  give the instance a designator when the name has to last.
- **D3 — `"*"` takes every placed part that is not copper.** Footprint parts as today, plus cell instances whose model is a
  device (`KindOf` = Part: a `pdk://` cell, a device model, anything the EM cannot represent as metal). Copper cells — a
  PCell, a line, a spiral, a cell with a layout view and a schematic — are cut out only when named, by path or by cell.
  This keeps every board byte-identical (an MLIN placed by Update Layout is copper and is not taken by `"*"`) and makes
  `"*"` mean *cut out the devices* on a chip. The SpiralResonator case then refuses differently: it says its two instances
  are copper the solver can solve, and how to name them.
- **D4 — The instance's geometry leaves the solved problem, never the `.clay`.** Its own shapes at every level by default,
  or everything inside a keep-out box. Nothing is written to the layout; two `.cem` files on one layout may cut out
  different instances (LCP's D1).
- **D5 — The port sits at the pin face by default.** A pin already is a face — point, width, outward direction — and a
  kit's model is referenced there. `PortAt: Cell` moves it to the keep-out box's boundary, per entry; at mm-wave the two
  differ and only the user knows which plane the model was characterised at.
- **D6 — Ground reference by default.** Each port is driven against the run's return (Planar: the ground plane; 3D: the
  edge sheet to the return). With a backside ground this loses nothing: the model's reference terminal goes on its own
  port's node in the netlist. A pin reference is ICP-4 and is built only where an existing two-conductor construction fits.
- **D7 — One port per connected pin name.** A pin with no interconnect gets no port (its model terminal goes to ground
  when the schematic grounds it, else to a named unconnected node, with a warning). A pin name at several faces
  (multi-finger) is refused in this series, by name.
- **D8 — Cut cells do not nest.** Naming `X1` and `X1/Q3` is a refusal: an instance inside a cut one is already out.

## Risks — measure before building (ICP-2's first finding)

These decide whether the construction is the right one, so they are measured on a scratch fixture before any code beyond
the projection is written, and the numbers go into `src/Design/RESOLVED.md` and the ICP-2 brief's completion note.

1. **De-embedding at a device's pins.** A Planar edge port is de-embedded to the drawn face, and the solver grows its own
   calibration feed outward from it (R-fed-1) — here, into the space the cut cell vacated, toward the device's other pins
   a few tens of µm away. A lead that would hit metal is declined (pre-existing warning); ports whose feeds are close are
   grouped (PCAL4, at most 3) or refused. Measure a 3-pin fixture: does it calibrate, group, or refuse, and how far apart
   must pins be for each?
2. **Where the reference plane lands.** Cut a plain line cell (`KIT_MLIN`) out of a longer line, put an ideal line of the cut
   length back in the netlist, and compare with solving the whole line. Same solver, so a tolerance is legitimate here;
   record the |ΔS| and set the gate from it.
3. **The 3D port is tall.** A sheet from the metal to a backside ground through 100 µm GaAs carries its own inductance into
   the result at mm-wave. Measure on EMerge by hand (fast); say it in the docs rather than gate on it.
4. **Hierarchical parameters.** A nested instance's parameters are expressions in its parent cell's scope. ICP-5 writes
   them as resolved values (decided there); a global VAR that drives them then no longer tunes them in `_cosim`. Accepted,
   and said in the netlist.

If (1) shows the edge port cannot be calibrated at realistic device pitches, stop and report: the fallback is a
non-de-embedded internal port at the face, which the engine does not have today and would be its own brief.

## Simulation budget, for every brief

- Fixtures are a few lines and one or two small cells on the shipped `mmic-GaAs_2LM_100um` technology, at most 6 frequency
  points, no vias unless a via is the point. ICP-2 measures the fixture's Planar time and records it; a routine test that
  solves stays under ~5 s.
- **No Palace in any test or gate.** 3D gates assert the assembled problem. EMerge only by hand, time-capped.
- No new `Category=Benchmark` test and no timing assertion. One test per claim. Run the named test classes only, never all
  of `Ui.Tests`.
- Nothing here is run on a real chip.

## Rules that apply throughout

- **No second construction.** `check`, `explain`, the panel, the layout marks and the run call one builder (the
  `C3dProblemAssembly.Assemble` rule, LCP-0).
- **An instance that cannot be cut is left out with its reason**, and the others run; the run refuses only when no port is
  left.
- **Existing results do not move.** A setup naming no cell instance produces byte-identical output on every path this
  series touches.
- **Diagnostics are returned, never posted** (`src/Design/CLAUDE.md`).

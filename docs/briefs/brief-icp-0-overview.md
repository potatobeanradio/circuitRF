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
| `brief-icp-2a-uncalibrated-port.md` | The Uncalibrated Port: an edge port at a face, grounded, not de-embedded — the fallback § Risks 1 called for, measured | — |
| `brief-icp-2-planar-cutout.md` | Leave the instance out and put an Uncalibrated Port at each pin face, on Planar. Starts with the measurement in § Risks | 1, 2a |
| `brief-icp-3-3d-cutout.md` | The same cut and ports on a 3D `.cem` and in a `.c3d` setup | 1, 2 |
| `brief-icp-4-pin-reference.md` | Each port's reference chosen per pin (ground, another pin, or one common node), and pins tied to ground with no port | 2, 3 |
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
- **D6 — Ground reference by default, chosen per port.** Each port is driven against the run's return (Planar: the ground
  plane; 3D: the edge sheet to the return) unless its pin says otherwise. With a backside ground this loses nothing: the
  model's reference terminal goes on its own port's node in the netlist. ICP-4 lets each pin choose: ground, another pin
  of the same instance (which then gets no port and is only the return), or one common node shared by the instance's
  ports; and lets a pin be tied to ground with no port at all. A reference other than ground is built only where an
  existing two-conductor construction fits.
- **D7 — One port per connected pin name.** Every pin of a cut instance is classified before meshing (ICP-2 R-icp2-3):
  *routed* (interconnect meets its face: a port), *abutting* (its face touches only another cut-out instance: no port, the
  two model terminals are joined directly in `_cosim`), *grounded* (tied to ground by the entry, ICP-4: no port), or
  *floating* (nothing meets it: no port, and a warning, because the model sees an open there that is easy to miss). A
  floating pin's model terminal goes to ground when the schematic grounds it, else to a named unconnected node. A pin
  name at several faces (multi-finger) is refused in this series, by name.
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

**Measured 2026-11-13 (`src/Design/RESOLVED.md` §brief-icp-2 — the measurement):** a 3-pin site with pins on three sides
is refused at 20, 40 and 80 µm alike by the feed-isolation rule (a neighbour carrying a port must be 5 substrate heights
away, 500 µm on 100 µm GaAs, and a perpendicular pin cannot be grouped). Two pins facing each other in line calibrate
alone at 40 µm. The reference plane agrees with the whole line to 2.6e-3 … 1.7e-2 from 5 to 20 GHz. Grounding the third
pin does not help. **The fallback was measured and works** (an edge port grounded and not de-embedded: within
5.4e-3 … 7.0e-2 of the calibrated answer on isolated lines, passive, and the 3-pin site solves) — it is
`brief-icp-2a-uncalibrated-port.md`, and ICP-2's cut ports are built from it.

## Simulation budget, for every brief

- Fixtures are a few lines and one or two small cells on the shipped `mmic-GaAs_2LM_100um` technology, at most 6 frequency
  points, no vias unless a via is the point. ICP-2 measures the fixture's Planar time and records it; a routine test that
  solves stays under ~5 s.
- **No Palace in any test or gate.** 3D gates assert the assembled problem. EMerge only by hand, time-capped.
- No new `Category=Benchmark` test and no timing assertion. One test per claim. Run the named test classes only, never all
  of `Ui.Tests`.
- Nothing here is run on a real chip.

## Feedback from the requester (issue 69, 2026-11-13)

The requester confirmed the flow matches the one they use and asked for five additions, each now a requirement:

| Ask | Where |
|---|---|
| A pin check before meshing: each cut instance's pins as routed, touching only another cut-out instance, or floating, and the ports it will generate, in the report and drawn on the layout | ICP-2 R-icp2-3 (the classification and the report), ICP-6 R-icp6-2 (`check`/`explain`), ICP-7 R-icp7-3 (the marks) |
| A reference chosen per port: another pin (which then is only the return), the global ground, or one common node for the whole N-port | ICP-4 R-icp4-1…3, R-icp4-6 |
| A pin marked as grounded instead of getting a port (an emitter or substrate tied to ground), to save ports and run time | ICP-4 R-icp4-5 |
| Keeping selected shapes or nets apart when a cut would merge the metal around a device into one net | ICP-2 R-icp2-9 |
| A preview of the exact geometry handed to the solver, with the cut-out instances shaded | ICP-7 R-icp7-6 |

They also offered test layouts: an HBT cascode with one transistor inside a sub-cell, interconnect, a MIM cap and ground
vias, in an external open SiGe kit they maintain (outside this repository: nothing from it is committed here), and the
shipped GaAs kit's SpiralResonator. ICP-9's example covers the second shape on the shipped technology; the first is a
by-hand check once the Planar cut lands, reported on the issue rather than gated.

## Rules that apply throughout

- **No second construction.** `check`, `explain`, the panel, the layout marks and the run call one builder (the
  `C3dProblemAssembly.Assemble` rule, LCP-0).
- **An instance that cannot be cut is left out with its reason**, and the others run; the run refuses only when no port is
  left.
- **Existing results do not move.** A setup naming no cell instance produces byte-identical output on every path this
  series touches.
- **Diagnostics are returned, never posted** (`src/Design/CLAUDE.md`).

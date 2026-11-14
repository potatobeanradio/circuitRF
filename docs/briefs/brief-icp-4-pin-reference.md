# Brief ICP-4 — Each port's reference, chosen per pin, and pins tied to ground

**Series:** ICP-0 … ICP-9 · **Tag:** `R-icp4-<n>` · **Depends on:** ICP-2, ICP-3
**Area:** `EmCellCutout.cs`, `src/Engine/Mom/PlanarPort.cs` (read, RP-2a's two-cut port), `src/Design/ThreeD/C3dComponentPorts.cs`
(its gap sheet), `EmCoSimNetlist.Closures`.

## The gap

ICP-2 and ICP-3 drive every pin against the run's return. A device characterised between its terminals — a gate and drain
measured against the source, an emitter-referenced HBT — or a chip with no backside plane wants each signal pin's port
driven against a chosen pin of the same instance. `EmComponentPort.Reference` already names such a pin for a footprint part;
for a cut instance it does nothing.

The requester (issue 69, 2026-11-13) asked for the choice per PORT, not per instance — a port is a source between two
points, so each pin's port should be referable to another pin (which then stops being a port and is only the return), to
the global ground, or to one common node for the whole N-port: on an HBT, B and C against E with E carrying no port, or
all three against ground. They also asked for a pin to be tied straight to ground instead of getting a port (an emitter or
substrate strapped down), which saves a port and its share of the run time.

## What exists, and its limits

- **Planar:** `PlanarPortReference.SecondConductor` (RP-2a) cuts the return conductor too and drives the two cuts against
  each other — but both cuts must be at **one station** on **one level** (`PlanarPort.cs:1310`): the two faces must lie on
  one line across the same axis, on the same layer. A FET's gate and source faces rarely do.
- **3D:** the gap sheet `C3dComponentPorts` builds between two faces that face each other across one axis
  (`EmPartPads.Facing`), on one copper layer, with landing rectangles.

## Requirements

**R-icp4-1 — `Reference` on a cut instance, per pin.** The entry's `Reference` keeps its meaning as the default for every
pin: a pin name (each other routed pin's port is driven against that pin's cut face, and the reference pin gets no port of
its own), `Ground`, or `Common`. A new optional `Pins` map overrides it per pin — `{ "B": { "Reference": "E" },
"C": { "Reference": "E" }, "E": { "Port": false } }` — with the same three values, plus `Port: false` for a pin that is
only a return. Omitted at its default (no `FormatVersion` bump); a file written before this reads and re-saves unchanged.
A pin named in `Pins` that the instance does not have is refused naming its pins. A pin referenced by another and also
given a port of its own is refused (it cannot be both the return and a driven port at one face).

**R-icp4-2 — Built only where an existing construction fits.** Planar: when the reference face and the signal face are on
one station and one level, a two-cut edge port (`PortReference = SecondConductor`, `PortReturn` at the reference face's
middle). 3D: when the two faces face each other across one axis on one layer, the gap sheet. Otherwise that pin is left
out by `<path>.<pin>`, saying which condition failed and that the ground reference builds it. No new port construction.

**R-icp4-3 — Several pins on one reference.** Two signal pins referenced to one source face: on Planar, two two-cut ports
sharing one return cut is refused unless the engine already accepts it — read `PlanarPort.cs` first and record what it does;
on 3D, two sheets landing on one face are allowed when they do not overlap.

**R-icp4-4 — `_cosim` with a pin reference.** The model's reference terminal goes on the netlist's common node `0` and each
signal terminal on its port's node, exactly as LCP's multi-pad parts map `ReferencePin` (`EmCoSimNetlist.Closures`). The
netlist comment says the reference pin is tied to the N-port's reference, which is the standard reading of a
pin-referenced port and is not the same as that pin being grounded in the chip.

**R-icp4-5 — Grounded pins.** `Pins: { "E": { "Ground": true } }` (or the entry's `Ground: ["E", "SUB"]` for several) ties the
pin to the run's return at its face instead of building a port. Planar: the cut face is shorted to the ground plane by the
ground attachment a backside via already uses (`PlanarVia.GroundTerminal`), sized to the face, on the pin's own level —
or, where no attachment can be built on that level, refused by `<path>.<pin>`; never approximated by a port terminated in
a short, which costs the port it was meant to save. 3D: a PEC sheet from the face to the return, the shape an edge port's
sheet takes. The pin's class is *grounded* (ICP-2 R-icp2-3); `_cosim` puts its model terminal on `0` and says so. A
grounded pin can be another pin's `Reference` only as `Ground`.

**R-icp4-6 — `Common`: one reference node for the instance's ports.** Every port of the instance that says `Common` is
driven against one node shared by them, not against ground: the N-port's own reference terminal. It is built only where
the solver has a construction for it — read first whether the Planar two-cut port (RP-2a) or the 3D gap sheet can share
one return across more than one port; where neither can, `Common` is refused by `<path>`, saying ground builds it. In
`_cosim` the shared node is the netlist's `0` for that block (R-icp4-4's reading).

## Gates

No solve beyond the ICP-2 fixture. One test per claim:
1. A fixture whose gate and source faces are on one station builds a two-cut port; S equals the same layout with the two-cut
   label drawn by hand, to 1e-9.
2. A gate face not on the source's station is left out with the station sentence, and with `Reference` absent builds.
3. 3D: facing faces assemble one gap sheet; non-facing faces are left out with `Facing`'s wording.
4. The `_cosim` netlist puts the reference terminal on `0`.
5. A `Pins` map with B and C against E and E `Port: false` builds two ports and none at E; the same instance with every pin
   on ground builds three; a pin both referenced and ported is refused.
6. A grounded pin builds no port, its face carries the ground attachment in the assembled problem, and S of the remaining
   ports equals the same layout with the attachment drawn by hand, to 1e-9.
7. `Common` either builds (if the measurement in R-icp4-6 finds a construction) or is refused with its sentence.

## Not here

Differential or multi-mode ports. A pin reference that needs a new construction (faces at an angle, on two layers) is
refused by name; building one is a later brief with its own measurement.

## Completion note (2026-11-13)

Built: `Pins` and `Ground` on a cut entry, every pin's reference and class decided once in `EmCellCutout` (`PinPlan`), a gap port
between a pin's face and its reference's on both solvers, grounded pins as a ground via, and `Common` refused. Gate
`tests/Ui.Tests/Em/CellPinReferenceTests.cs`, six tests: built equals hand-drawn for the Planar gap (0 exactly) and for the ground
via (0 exactly), faces that do not face refused and building without a reference, the 3D gap sheet and its refusal, the `Pins` map
(two ports and none at E; three on ground; a pin both return and port refused; the `.cem` round trip), and `Common`. Where it
differs from the requirements, and why — `src/Design/RESOLVED.md` §brief-icp-4:
- the Planar construction is LCP's strip and delta gap between facing faces, not a two-cut edge port at one station, which the
  port extraction refuses (and which has no uncalibrated form), so both solvers take the facing condition;
- a grounded pin is a ground via in 3D too, not a PEC sheet;
- R-icp4-4 and gate 4 move to ICP-5, which puts the first model on a cut port; the reference pin and the pin classes are carried
  for it.

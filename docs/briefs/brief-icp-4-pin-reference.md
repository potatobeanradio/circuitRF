# Brief ICP-4 — A port referenced to one of the instance's pins

**Series:** ICP-0 … ICP-9 · **Tag:** `R-icp4-<n>` · **Depends on:** ICP-2, ICP-3
**Area:** `EmCellCutout.cs`, `src/Engine/Mom/PlanarPort.cs` (read, RP-2a's two-cut port), `src/Design/ThreeD/C3dComponentPorts.cs`
(its gap sheet), `EmCoSimNetlist.Closures`.

## The gap

ICP-2 and ICP-3 drive every pin against the run's return. A device characterised between its terminals — a gate and drain
measured against the source, an emitter-referenced HBT — or a chip with no backside plane wants each signal pin's port
driven against a chosen pin of the same instance. `EmComponentPort.Reference` already names such a pin for a footprint part;
for a cut instance it does nothing.

## What exists, and its limits

- **Planar:** `PlanarPortReference.SecondConductor` (RP-2a) cuts the return conductor too and drives the two cuts against
  each other — but both cuts must be at **one station** on **one level** (`PlanarPort.cs:1310`): the two faces must lie on
  one line across the same axis, on the same layer. A FET's gate and source faces rarely do.
- **3D:** the gap sheet `C3dComponentPorts` builds between two faces that face each other across one axis
  (`EmPartPads.Facing`), on one copper layer, with landing rectangles.

## Requirements

**R-icp4-1 — `Reference` on a cut instance.** A pin name: each other connected pin's port is driven against that pin's cut
face, and the reference pin gets no port of its own. Absent: ground (ICP-2/3, unchanged).

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

## Gates

No solve beyond the ICP-2 fixture. One test per claim:
1. A fixture whose gate and source faces are on one station builds a two-cut port; S equals the same layout with the two-cut
   label drawn by hand, to 1e-9.
2. A gate face not on the source's station is left out with the station sentence, and with `Reference` absent builds.
3. 3D: facing faces assemble one gap sheet; non-facing faces are left out with `Facing`'s wording.
4. The `_cosim` netlist puts the reference terminal on `0`.

## Not here

Differential or multi-mode ports. A pin reference that needs a new construction (faces at an angle, on two layers) is
refused by name; building one is a later brief with its own measurement.

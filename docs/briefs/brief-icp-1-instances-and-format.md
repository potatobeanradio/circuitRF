# Brief ICP-1 — Which instances: the hierarchy walk, paths, pins and the `.cem` entries

**Series:** ICP-0 … ICP-9 (read `brief-icp-0-overview.md` first) · **Tag:** `R-icp1-<n>`
**Area:** a new `src/Design/Layout/Em/EmCellInstances.cs`; `EmSetupModel.cs` (`EmComponentPort`), `EmSetupPersistence.cs`
(`ComponentPortsConverter`), `EmPartPads.Select`. No solve, no UI, no CLI surface.

## The gap

`EmPartPads.Project` is the only source of parts, and it reads footprint placements with pads and a refdes at the top
level. A cell instance with layout pins — a PCell, a device cell, a sub-block placed by Update Layout — is not a candidate,
a nested one cannot be named, and an unnamed one has no name. `EmComponentPort` holds a refdes, Z0, a reference pin and a
strip width: nothing says where a port sits or what to leave out.

## Requirements

**R-icp1-1 — The instance projection.** `EmCellInstances.Project(LayoutView, clayPath, Technology?, C3dTransform world)`
returns every placed instance that has at least one layout pin, at every depth (to `CellHierarchy.MaxDepth`), each with:
- its **path**: `LayoutDesignFlatten.PathOf` per level, joined by `/` (`X1/Q3`, `@2`, `X1/@0`);
- its **cell**: the cell folder name, and for a generated cell its PCell generator id (from the owning layout's
  `PCellSnapshots`, as `EmGeometry.CollectGeneratorIds` reads it);
- its **pins** in world metres: `CellPins.Resolve` on its resolved view, each pin's point, width and outward direction
  carried through the full chain of instance transforms (rotation, mirror, magnification), and its layer;
- its **schematic counterpart**: the chain of `SchematicId`s from the top (`X1` then `Q3`), or none;
- its **kind**: `Part` (footprint, or a device per `EmModelSwitch.KindOf`), `Copper`, or `Footprint` — one predicate,
  shared with `KindOf` rather than restated (extract it to a place both can call);
- its **keep-out box**: the bounding box of its own shapes on this setup's analysis levels, in world metres;
- or **why it cannot be a component port**: unresolved cell, arrayed placement, a placement angle that is not a multiple of
  90°, tilted in a 3D view (`EmPartPads`' wording, reused), no pins.
Reads files; writes nothing. Built once per layout and setup: a chip places hundreds of instances.

**R-icp1-2 — Footprint parts stay where they are.** A footprint placement keeps coming from `EmPartPads.Project` and keeps
LCP's gap. `EmCellInstances` marks it `Footprint` and builds nothing for it. On a board with no other cells every output of
this series is byte-identical.

**R-icp1-3 — The entry.** `EmComponentPort` gains, all optional and omitted at their defaults (no `FormatVersion` bump):
- `Cell` — select every instance of this cell (folder name or generator id), anywhere in the hierarchy; an entry has
  `Refdes` or `Cell`, never both;
- `PortAt` — `Pin` (default) or `Cell` (ICP-0 D5);
- `KeepOutUm` — `[x0, y0, x1, y1]` in the instance's own frame, replacing the default box (only read with `PortAt: Cell`, or
  to widen what is left out around a pin-mode cut; ICP-2 says how);
- `Construction` — `Gap` (LCP's) or `Cutout`; null infers from the kind (D1).
`Refdes` now also takes a path (`X1/Q3`) and `X1/*`. `Reference` keeps its meaning: a pin name, or absent for ground.
The converter reads and writes the new keys; a file written before this reads unchanged and re-saves byte-identically.

**R-icp1-4 — Selection.** `EmPartPads.Select` becomes the one rule for both kinds:
- `"*"` takes every footprint part with two pads (unchanged) **and** every `Part`-kind cell instance at the top level;
  `X1/*` every `Part`-kind instance inside `X1`. A `Copper` instance is taken only by name or by `Cell`.
- A named entry beside `"*"` or a `Cell` entry is that instance's override, as today.
- Refused, naming them: one instance selected twice by two entries that disagree; an instance and one inside it (D8); a
  path that names nothing (listing what is placed at that level, as `KnownParts` does); a `Cell` that matches nothing
  (listing the cells placed).
- When `"*"` finds nothing, the sentence says what is there: *no part placed on this layout is a device; @0 (KIT_SPIRAL)
  and @1 (KIT_MIMCAP) are copper the solver can solve — name them, or select their cell, to cut them out.*

**R-icp1-5 — Pins and names.** A port's name is `<path>.<pin>`. The pin name is the layout pin's; where a cell's pins are
numbers made from schematic Ports (`CellPins.FromSchematicPorts`) and its symbol names them, the symbol's name is used
(`X1/LNA.IN`, not `X1/LNA.1`) and the number is kept for the mapping. A pin name at more than one face is recorded as
such; ICP-2 refuses it (D7).

**R-icp1-6 — One result type.** The projection and selection return one record per wanted instance (path, cell, pins,
kind, box, schematic chain, the entry's options, or why not), which ICP-2…7 all read. No caller re-walks the hierarchy.

## Gates

No solve. One test per claim:
1. A two-level fixture (a top layout placing `X1`, whose cell places `Q3` rotated `R90` and mirrored): `X1/Q3`'s pins land
   where `LayoutFlatten.FlattenAllLevels` puts the pin's own point, facing the transformed direction.
2. `PathOf` agreement: every path the projection produces is the path LVS reports for the same instance.
3. `"*"` on a layout of footprints and MLIN PCells selects exactly the footprints (board byte-identity).
4. `"*"` on the SpiralResonator example selects nothing and says why, naming both instances and their cells.
5. `Cell` selection reaches a nested instance; `X1` with `X1/Q3` is refused.
6. A `.cem` from before this round-trips byte-identically; one with every new key round-trips.

## Not here

Leaving geometry out, ports, co-simulation, UI, CLI.

## Completion note (2026-11-13)

Built: `src/Design/Layout/Em/EmCellInstances.cs` (the projection), `EmPartKinds.cs` (the shared kind rule, moved from
`EmModelSwitch`), `EmPartPads.Select(setup, parts, where, instances)`, and the four entry keys in `EmSetupModel.cs` /
`EmSetupPersistence.cs`. Gate `tests/Ui.Tests/Em/CellInstanceProjectionTests.cs`, six tests; the existing component-port and
EM-cell classes pass unchanged. Where this differs from the requirements above, and why, is in `src/Design/RESOLVED.md`
§brief-icp-1: `"*"` walks down through copper to the first device on each branch rather than stopping at the top level; a path
or cell matching nothing is left out with its sentence rather than refusing the list; the Planar and 3D builders do not pass
instances until ICP-2.

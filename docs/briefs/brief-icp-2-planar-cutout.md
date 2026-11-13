# Brief ICP-2 — Cut an instance out and put an edge port at each pin, on Planar

**Series:** ICP-0 … ICP-9 · **Tag:** `R-icp2-<n>` · **Depends on:** ICP-1
**Area:** a new `src/Design/Layout/Em/EmCellCutout.cs`; `EmGeometry.cs` (`ForSetup`, `Flatten`), `LayoutFlatten.cs`,
`EmComponentPorts.cs` (`Collect(source, setup)`), `EmPlanarComponentPorts.cs` (its build type), `EmSnpProvenance.cs`,
`EmResultRecord.cs`, `EmPartsOpen.cs`. No UI, no CLI surface.

## The gap

`EmGeometry.Flatten` takes every instance's shapes, and nothing can except one. With ICP-1 an instance can be named, but a
run still solves its metal, and there is no port where the interconnect meets it.

## First: measure (ICP-0 § Risks 1 and 2)

Before R-icp2-2 is built, on a scratch fixture with labels drawn by hand (no code beyond ICP-1):
- **Reference plane.** A 50 Ω line on the MMIC technology with a `KIT_MLIN` segment in the middle. Delete the segment, put
  edge ports on the two faces it leaves, close them with an ideal line of the segment's length, and compare with the whole
  line solved. Record |ΔS| over the sweep. This sets gate 2's tolerance.
- **Device pitch.** Three short lines meeting a vacated 3-pin site from three sides (gate left, drain right, source below),
  pins 20, 40 and 80 µm apart. Record for each pitch whether the edge ports calibrate alone, as a group (PCAL4), decline
  their feed lead, or refuse, and the time per point.
Write both into `src/Design/RESOLVED.md`. If the edge port cannot be calibrated at 40 µm, stop and report (ICP-0's fallback).

## Requirements

**R-icp2-1 — Leaving geometry out.** One function, `EmCellCutout.Apply(shapes, cuts, …)`, called from `EmGeometry.ForSetup`,
so the run, the preflight, the panel's Refresh, the mesh preview and `explain` see the same problem. The flatten learns
which instance paths to skip: a cut top-level instance contributes nothing; a cut nested instance is skipped inside its
parent's walk (thread the path through `LayoutFlatten.FlattenAllLevels` as an optional exclusion set — the walk is
unchanged with none, and gated byte-identical). `EmGeometry.Flatten` with no setup is untouched.
- **`PortAt: Pin`** — the instance's own shapes at every level are left out, and nothing else.
- **`PortAt: Cell`** — everything on this setup's analysis levels inside the keep-out box (ICP-1's default box, or
  `KeepOutUm`) is left out, the parent's metal and vias included, cut along the box edge.
A component-declared EM port (`LabelShape.EmPort`) inside a cut instance goes with it, with a note.

**R-icp2-2 — The cut face.** For each pin of each cut instance:
- *Pin mode:* the conductor that touches the pin's face from outside, on the pin's layer, is cut at the face's line across
  its own width (a trace drawn onto the pin pad is trimmed back to the face).
- *Cell mode:* the conductor crossing the box edge on the side the pin faces, within the pin's transverse span, is the one;
  its face on the box edge is the cut face.
The cut face must be one straight axis-parallel segment (a Planar edge port's face); the port label is an edge port
(`IsPort`, `PortKind = Edge`, `PortLayer`, `PortDirection` into the conductor, `PortUseEdgeWidth` true) at the face's middle.
It exists in the solved problem only, like LCP's strip.

**R-icp2-3 — Verified before meshing.** Left out by `<path>.<pin>`, naming what was found, when: no conductor meets the
pin (D7: no port, not an error — a note says where its model terminal will go); two conductors meet one pin; one pin name
is at several faces (multi-finger, D7); the face is not axis-parallel (an instance at a non-cardinal angle, or a pin whose
outward direction is not); the conductor reaches the pin only through a via the cut removed; after the cut, the
conductor still touches other metal inside the instance's footprint (the parent's metal overlapping the cell beyond its
pin — in Pin mode, say that Cell mode cuts it); a parent via lands on metal the cut removed; the pin's layer is not an
analysis level of this setup; the face lies outside `SolveRegion`. An instance whose every pin is left out is left out,
and the run refuses only when no port is left at all.

**R-icp2-4 — One build, one entry point.** The cut ports are part of what `EmComponentPorts.Collect(source, setup)` returns:
generalise `EmPlanarComponentPortBuild` (or add a sibling the `Result` carries beside it) so `Z0For`, `ComponentBuild`
readers and `EmCoSimNetlist.PortsOf` see gap ports and cut ports through one list. Build once per setup and source.

**R-icp2-5 — Numbering, impedance, names.** Ports follow the drawn and component-declared ports, then the component ports in
list order; within an instance, in its pin order (symbol order where the instance has a symbol). An entry's `Z0` applies to
each of its pins. The Touchstone header reads `! Port[5] = X1/Q3.G` and the `.cemr` records path, pin and cell per port.

**R-icp2-6 — The open-parts group.** `EmPartsOpen` adds the cut ports to the ports it opens, so the automatic plot shows the
interconnect with the cut instances absent, as it shows a board with its parts not fitted.

**R-icp2-8 — Off the UI thread, and built once.** The setup panel's Refresh (`EmSetupEditorViewModel.RefreshCore`) calls
`EmComponentPorts.Collect(source, setup)` and `EmGeometry.ForSetup` synchronously on the UI thread, and those are where this brief
builds the cut — on a chip that is a walk of every cell, every level's schematic and the cut's booleans. Before the builders hand
instances in:
- Refresh takes a snapshot on the UI thread (the layout as `LayoutEditorViewModel`'s other off-thread builders take it, and a
  copy of the setup) and builds on a worker, as the panel's Check and the layout marks already do; a stale build (the layout's
  `EditCount` or the setup moved on) is discarded, not applied. The mesh preview (`PreparePlanarMesh`) follows the same rule.
- One build per (layout snapshot, setup) is cached and shared by Refresh, Check, the marks and the mesh preview, so an edit
  costs one walk rather than four. The cache key is what the build reads: the layout's text, the technology, the setup's
  component ports, analysis levels and solve region.
Gate: a view-model test that Refresh returns before a slow build finishes (a builder seam that blocks), applies the result
when it lands, and drops a result whose snapshot is stale.

**R-icp2-7 — Nothing reaches the file.** The `.clay` and every cell's `.clay` are byte-identical after a run, a Check and a
save of the `.cem`.

## Gates

On the fixture ICP-2 measured, at most 6 points; record its time in the completion note. One test per claim:
1. **Built equals hand-drawn.** A cut (Pin mode, and Cell mode) gives S identical to 1e-9 with the same layout edited by
   hand: instance deleted, conductor trimmed, edge labels drawn. Once for a top-level instance, once for `X1/Q3`.
2. **Reference plane.** The `KIT_MLIN` cut closed with an ideal line of its length matches the whole line to the tolerance
   the measurement set.
3. **A three-pin instance** solves with three ports at the measured workable pitch.
4. Each refusal in R-icp2-3, with no solve.
5. A setup with no cut instance writes what it wrote: one byte-identity check through `Flatten` and one through a run.
6. The `.clay` files are unchanged.

## Not here

3D (ICP-3), pin references (ICP-4), `_cosim` (ICP-5), CLI, UI.

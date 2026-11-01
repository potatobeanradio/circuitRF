# Brief EMB-1 — Component ports: a lumped port across each part's own pads, and the co-simulation it feeds

**Series:** EMB-0 … EMB-5 (overview in `brief-emb-0-overview.md`) · **Tag:** `R-emb1-<n>`
**Area:** `src/Design/ThreeD/C3dHierarchy.cs` (`NewFromLayout`), `src/Design/ThreeD/C3dPorts.cs`,
`src/Design/ThreeD/C3dProblemAssembly.cs`, `src/Design/Layout/Em/EmSetupModel.cs` + `EmSetupPersistence.cs`
(new setup fields), the board projection behind `netlist <.clay> --placement/--bom`, `src/Cli/Authoring.cs`
(`new`), `src/Cli/Serve/ToolCatalog.cs`, a new co-simulation writer beside `src/Design/Layout/Em/EmSnpSchematic.cs`,
`docs/user/src/reference/em-3d.md`, `docs/design/cli.md`
**Depends on:** EMB-2 R-emb2-1 (straight-sided order 2) and R-emb2-4 (post-mesh check), EMB-3 R-emb3-1.

---

## 0. The gap

Board EM is only useful when the board's N-port can be combined with the part models: every SMT part
must sit across a port at its own pads. circuitRF has no way to say that. A lumped port is a rectangle
placed by hand, and its inference wants two opposite edges each within **1 DBU** of a conductor
(`C3dPorts.cs` header, "POLARITY BY CONTACT"). On imported artwork the copper bounding a part's gap is
round trace caps, zone-fill arcs and short segments, so almost no rectangle touches it; stating
`Positive`/`Negative` skips inference but leaves the sheet's edges wherever they were drawn.

Measured on a 35 × 20 mm two-layer 0.6 mm FR-4 LNA board (15 SMT parts, full top-side ground pour):

- **A sheet reaching into copper breaks the mesh.** Conductors are holes in the volume mesh; the part of
  the sheet inside one leaves surface triangles that are no face of any tetrahedron, and Palace aborts in
  MFEM `STable3D` with three node numbers.
- **A sheet stopped 1 µm short of copper is an open circuit.** Every port read |S_ii| ≈ 1 at 0° with no
  coupling — the sheet sat behind two micron air gaps.
- **A sheet a few µm short makes slivers**, and Palace aborts with "Jacobi smoother … strictly positive
  diagonal".
- **Ground-referenced pad-to-plane port PAIRS are the wrong model for a part between two pads**, on
  principle: a part between two such ports carries its current, as far as the EM is concerned, down one
  0.6 mm sheet, along the plane and up the other, a loop that is not on the board. On the measured board
  this was NOT the dominant error (a pair run and a gap-port run put the input capacitor's series
  resonance at 0.81 and 0.855 GHz respectively), so the requirement rests on the model being right, not
  on a large measured difference; it will matter wherever pad pitch is large against substrate height.
- **A port's reference must be the conductor its current returns through**, and at an edge-launch
  connector that is the shell, which clamps top and bottom ground together at the launch. Modelling the
  shell moved the input capacitor's resonance only 0.850 → 0.855 GHz on this board, so it is a
  correctness requirement, not the explanation of the resonance; that one (~0.8 nH in the shunt
  capacitor's ground path, every port style alike) is attributed to the board's own thermal-relief spokes
  between ground pads and the pour, which the imported zone fill carries and the EM therefore sees.
- **What worked** (by hand): a 0.15 mm landing rectangle on BOTH ends of every gap, its face at the closest
  copper scanned every 10 µm across the width, reaching back until it overlaps solid copper across its full
  width; the sheet running exactly face to face at mid-copper height; both ends stated. Checked before
  solving: conductor count unchanged (no net bridged), inference by contact succeeds on every port, zero
  zero-volume tetrahedra, zero orphan surface triangles. A run then took 5–6 min at 193k unknowns.

There is also no headless way to make the 3D view this all lives in: `C3dHierarchy.NewFromLayout` exists
(called by `WorkspaceViewModel.ThreeD.cs` `NewThreeDViewFromLayoutAsync`) but no CLI verb or MCP tool
reaches it.

## 1. Requirements

**R-emb1-1 — A 3D view from a layout, headlessly.** `new view3d <cell> --from-layout` (CLI) and
`create what=view3d fromLayout=true` (MCP) call `C3dHierarchy.NewFromLayout` — the function the GUI calls,
not a copy (the `Authoring.cs` rule; extend the comment-stripped source scan in
`tests/Ui.Tests/AuthoringCliVerbTests.cs`). The created document places the layout as an instance with
`View: Layout` and carries the layout's port labels as ports, exactly as the GUI's does.

**R-emb1-2 — A setup names its component ports by refdes.** New setup field `ComponentPorts` (`.cem` and a
`.c3d`'s embedded setups): a list of refdes, or `"*"` for every placed two-terminal part. Each entry may
override `Z0` and `Reference` (R-emb1-6). Port numbers follow the document's own ports in list order. The
refdes and its pads come from the same board projection `netlist --placement` uses, so a part the
projection cannot resolve is refused by refdes, naming why (no footprint, pads not on a copper layer, more
or fewer than two signal pads — a three-pad part such as an amplifier gets one port per non-ground pad
against its ground pad, R-emb1-5).

**R-emb1-3 — The port is built on the part's own pads, and lands on copper, never in it.** For each part:
take its two pads' facing edges (the pad outlines, transformed by the instance); find the closest copper
on each side across the port's width — pad, trace cap, zone fill, anything merged with that pad's
conductor; and place the sheet exactly between, at mid-copper height. Where the facing copper is not one
straight face across the width, add a landing rectangle to the conductor on that side, its face at the
closest copper and reaching back until it overlaps solid copper across the full width (a rectangle resting
on a cap's tip leaves an air slot beside it). Landing rectangles exist only in the lowered 3D problem —
never written to the `.clay` — and are reported in `explain` with their size. **Why:** every failure in §0
is a sheet edge either short of or inside copper; this is the construction that measured clean.

**R-emb1-4 — Verified before meshing, refused by refdes.** After building every component port, before
Gmsh runs (in `check` too):
- the elaborated conductor count is unchanged by the landing rectangles (no net bridged);
- each port's two edges touch exactly its two conductors within 1 DBU, and its side edges touch neither;
- no copper lies inside the sheet (sample the sheet on a 2 µm grid);
- the two conductors are different nets.
A failure refuses the run naming the refdes, the edge and the conductor found. EMB-2 R-emb2-4's post-mesh
check then confirms zero zero-volume elements and zero orphan port triangles.

**R-emb1-5 — Gap ports, never ground-referenced pairs, for a two-terminal part.** A two-terminal part
always gets one gap port between its pads. A multi-pad part (an amplifier) gets one gap port per signal pad
against its own ground pad (the part's ground pin), not against the plane. The `.c3d` reference and
`em-3d.md` say why in one paragraph (the loop above), so a hand-written pad-to-plane pair is
recognisably a different, approximate thing.

**R-emb1-6 — A port's reference is where its current returns.** A connector port (a part whose footprint is
a coaxial launch) is referenced to the ground conductor its shell contacts on BOTH copper layers: when the
footprint has ground pads on top and bottom, the lowered problem bridges them at the board edge (a copper
block through the substrate at each ground-pad pair), as the shell does. `Reference` on a component port
overrides the conductor. `check` warns when a port's negative conductor is not the same body as the
conductor the nearest shunt part returns to, and says how far apart the two meet through vias.

**R-emb1-7 — The co-simulation netlist is written, not authored.** A finished driven run with component
ports writes `<setup>_cosim.cnl` beside the existing `_sim` cell (`EmSnpSchematic`): an `SnP` of the run's
Touchstone, the two board connector ports as `Port` lines, and every component as its own instance from its
port node to ground — the part's model taken from the board's resolved schematic when there is one
(value, `SnP` file, vendor part), else a value placeholder named in a comment and `check`'s warning. A
two-port series-measured part file is connected as one port node and ground (its series impedance across
the gap). A multi-pad part's ports connect to its model's terminals in pin order. **Why:** the connection
rule (gap port → part from node to ground; never between two port nodes) is the step most likely to be
done wrong by hand, and it is mechanical once the ports exist.

**R-emb1-8 — Spelling.** CLI: `em <file> --component-ports "C1,C2,L2"|"*"` adds them to the setup for one
run; `explain <file> --component-ports` lists each port's refdes, conductors, gap, width, landing rectangles
and verdict without meshing. MCP: `run analysis=em componentPorts=[…]`, `explain componentPorts=true`. The
GUI's 3D setup panel gets a "Component ports" list with the same verdicts (UiSnapshot image in the
completion note).

## 2. Gates

- `ComponentPortBuildTests` (Ui.Tests/Em3d): a two-pad fixture whose gap is bounded by a round trace cap
  on one side and a zone arc on the other — the sheet's edges lie on the landing faces within 1 DBU, no
  sample of the sheet is copper, conductor count unchanged, landing rectangle overlaps its pad across the
  full width.
- A fixture where a landing rectangle would bridge two nets is refused, naming the refdes.
- A three-pad part gets two ports, each against its ground pad.
- A connector footprint with top and bottom ground pads gets the bridging block; its port's negative
  conductor is the bridged ground.
- `CoSimNetlistTests`: the written `.cnl` elaborates; each part's instance is `<part> pK 0`; a series-measured
  two-port file is wired node-and-ground.
- `AuthoringCliVerbTests`: `new view3d --from-layout` is byte-identical to `C3dHierarchy.NewFromLayout`, and
  the scan finds no second copy.
- One small Palace run (a two-pad fixture, a few hundred tetrahedra, `Category=Benchmark` only if it
  exceeds the threshold) checks |S_ii| of a component port is not ≈ 1 with a 50 Ω resistor across it.

## 3. Not in scope

- Parts with more than three pads beyond "each signal pad against the ground pad".
- Wave ports at connectors (a separate refinement; lumped is what the measurement above validated).
- Choosing part models — R-emb1-7 uses what the board's schematic resolves to and says when it has none.
- Planar (MoM) component ports: the planar kernel's `InternalDeltaGap` already cuts a conductor; extending
  it to a pad pair is a follow-up once the 3D path ships.

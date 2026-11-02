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

---

## Completion note — EMB-1 (2026-11-01)

### Per requirement

**R-emb1-1 — a 3D view from a layout, headlessly. Done.**
- CLI: `new view3d <cell> --from-layout [--name N]`.
- MCP: `create what=view3d fromLayout=true`.
- `src/Cli/Authoring.cs` calls `C3dHierarchy.NewFromLayout` and `CellCreate.WriteThreeDView`, the two calls the GUI's command
  makes.
- The workspace is the cell's nearest ancestor `.cws`. The name defaults to the cell's own.
- The run reports what it copied, brought in or left out, using the GUI's own sentences.
- Gates in `AuthoringCliVerbTests`:
  - The `.c3d` the CLI process writes is byte-identical to the in-process call. The layout carries a port label, so the
    ports are part of what is compared.
  - The comment-stripped scan finds `C3dHierarchy.NewFromLayout(` in the view model and in `Authoring.cs`.
  - The scan finds no document construction in `Authoring.cs`.

**R-emb1-2 — `ComponentPorts`. Done.**
- `EmSetup.ComponentPorts` holds `EmComponentPort(Refdes, Z0?, Reference?)` entries.
- In the file it can be written three ways: `"*"`, `["C1", "L2"]`, or object entries with `Refdes`, `Z0` and `Reference`.
  `ComponentPortsConverter` writes a lone `*` back as the string.
- It is copied by `Clone`, and the `.c3d`'s embedded setups use the same schema.
- Port numbers continue after the document's own ports, in list order.
- Pads come from `BoardCompanions.Project`, the projection `netlist --placement` uses.
- Refusals name the refdes:
  - not placed (the refusal lists the placed parts)
  - footprint unresolved
  - a pad on no conductor of its own layer
  - an arrayed, tilted or non-90° placement
  - fewer than two pads
  - a multi-pad part with no ground pad
- A `.cem` on a layout refuses `ComponentPorts`. Component ports are built in a 3D view, where the lowered conductors have
  names.

**R-emb1-3 — the construction. Done.** It lives in `src/Design/ThreeD/C3dComponentPorts.cs` and is called from
`C3dProblemAssembly.Assemble`, so the run, `check`, `explain` and the GUI all build it the same way.
- **Pad outline:** the footprint shape under the pin, put through the instance transform and the frame's world transform.
- **Width:** the overlap of the two pads across the gap axis.
- **Facing copper:** the closest copper of each pad's conductor is scanned every 10 µm across the width, and also through
  every facing vertex (see findings).
- **Landing rectangle**, where that copper is not one straight face:
  - Its face is at the closest copper, snapped to the DBU grid toward the gap.
  - It reaches back in 10 µm steps until its back edge is in solid copper on every scan line.
  - It is **unioned into the conductor's outline** (Clipper2), so the conductor's name and count are unchanged.
  - It exists only in the lowered problem; the `.clay` is never touched.
- **Sheet:** flat at mid-copper height, from face to face, with both ends stated.
- `explain` reports each landing's size.

**R-emb1-4 — verified before meshing. Done**, in `check` as well as the run. A failure refuses the run, naming the refdes,
the edge and the conductor found. The checks:
- A landing touching any other conductor is refused before the union, except a via standing inside its own copper.
- The union must give one piece.
- Each end edge touches exactly its own conductor within 1 DBU, and each side edge touches none.
- No copper lies inside the sheet, sampled on a 2 µm grid a DBU in from its edges.
- The two conductors are on different nets where the board states nets.

EMB-2's post-mesh check then runs as before. In the Palace run below it found nothing.

**R-emb1-5 — gap ports. Done.**
- A two-terminal part gets one gap port.
- A part with more pads gets one port per signal pad against the facing ground pad on the same copper layer, named
  `U2.IN`, `U2.OUT`. A ground pad is one on a ground net (GND-like names, the setup's `Ground3D`, or the ground band's
  nets), or a pin named that way, or the pin `Reference` names.
- The paragraph explaining why is in `em-3d.md` (#part-ports) and in the `3d-view` reference (`DocumentSchema.cs`).

**R-emb1-6 — a port's reference. Partly done.**
- Done:
  - `Reference` (a pin name, or a conductor a pad lands on) chooses the negative end.
  - A part whose ground pads on two copper layers lie over each other gets a shell bridge: a copper block from mid-copper to
    mid-copper, kept 25 µm inside the pads. Its port's negative end is that bridged top ground.
  - Such a part, or a refdes starting with J, is marked as a connector.
- **Deferred:** `check`'s warning when a port's negative conductor is not the same body as the conductor the nearest shunt
  part returns to, and how far apart the two meet through vias. It needs a walk through the via connectivity between two
  bodies, which nothing here has yet; I left it out rather than ship half of it.

**R-emb1-7 — the co-simulation netlist. Done.**
- `src/Design/Layout/Em/EmCoSimNetlist.cs` builds a `TestBench` and writes it with `CnlWriter`. It contains:
  - `SnP:BOARD p1…pN` of the run's Touchstone.
  - A `Port` line for each of the 3D view's own ports and for each connector.
  - Each part from its port's node to ground.
  - An S-parameter analysis over the setup's sweep.
- Part models:
  - Each part is the board schematic's extracted instance with its nets replaced. A two-terminal model, including a 2-port
    SnP measured in series, is wired `[pK, 0]`.
  - A multi-pad part's terminals are wired in pin order: a signal pin to its port's node, the ground pin to `0`, any other
    pin to `nc_…`.
  - A part with no model becomes a named placeholder (`C1_value = 1 pF`, `L… 1 nH`, `R… 50 Ohm`), explained in a comment.
    `check` and `explain` warn about it.
- Where it is written:
  - `EmRunService` writes it after a finished driven run as `<workspace>/<setup>_cosim.cnl`, beside where the `_sim` cell
    goes. This happens whatever `CreateSnpSchematic` says.
  - It replaces only a file of its own, identified by its header line; any other file of that name is left alone and said.
  - It is reported on `EmRunResult.CoSim`, as the CLI's `cosim` output and in the GUI's Messages.

**R-emb1-8 — spelling. Done.**
- CLI:
  - `em <c3d> --component-ports "C1,C2"|"*"` applies to one run, in memory only. On a `.cem` that names a layout it is
    refused as `em.component-ports.refused`.
  - `explain <c3d> --component-ports ["list"]` runs `Assemble`, the run's own code, without meshing. It walks each port:
    refdes, pads, conductors, gap, width, landings, connector, verdict. It also reports bridges, notes, warnings and the
    co-sim file name. With a list, it builds that list instead of the setup's.
- MCP: `run analysis=em componentPorts=[…]`, `explain componentPorts=true`, and `create what=view3d`. I added option and
  mode entries only.
- GUI: the 3D setup panel has a **Component ports** list, below Robust meshing, for a `.c3d`'s embedded EM setups only.
  - It is a checklist of the placed parts with their pad counts (a picker, per the `src/Ui` rule).
  - **Every two-terminal part** writes `*`.
  - Each tick is one undoable commit.
  - **Check** builds and verifies the ports through `Assemble` and shows each part's verdict. A refused verdict is drawn in
    the warning colour.
- UiSnapshot has a new mode, `--c3d-setup-panel <x.c3d> [--check-component-ports]`, documented in its README.
- Picture, which I looked at: a scratch UiSnapshot of the panel (cropped and full), not committed. It shows C1 (two landings, verified), J1 (connector, gap along y)
  and U1 (U1.IN and U1.OUT against GND), all verified, with the status line.

### Findings (also in `src/Design/RESOLVED.md`)
- **Scanning every 10 µm missed a round cap's tip**, which lies on one vertex. The sheet's edge then landed 0.2 µm inside
  the copper, and the gate's sheet sample caught it. Every facing vertex's own coordinate is now a scan line too.
- A tessellated cap's tip is short of the drawn radius (4849.79 µm against 4850 µm), so the face is placed where the lowered
  copper actually is.
- A landing is unioned into its conductor instead of being added as an overlapping solid. That keeps the stated ends valid
  and means no two conductor solids overlap in the mesh.

### Tests
- New: `tests/Ui.Tests/Em3d/ComponentPortBuildTests.cs` (6 tests).
  - Cap-and-arc gap: landing faces within 1 DBU of the sheet, no copper sampled in the sheet, conductor count unchanged,
    landings across the full width reaching past the pad faces.
  - Bridging landing refused by refdes.
  - Three-pad part gets two ports, each against GND.
  - Connector gets the shell bridge, and its negative end is the bridged ground.
  - `explain` and `check` through the CLI process, including a refusal for an unknown refdes.
  - A `[PalaceFact]`, Benchmark-tier Palace run (below).
- New: `tests/Ui.Tests/Em3d/CoSimNetlistTests.cs` (1 test).
  - Checks: the board SnP, `C:C1 p2 0`, an `SnP:L1 p3 0` series part file, Port lines for a 3D view port and a connector, a
    named R placeholder, no part between two port nodes.
  - The file is read by `CnlReader` and elaborated.
- Updated:
  - `AuthoringCliVerbTests`: the byte-identity test and the source scans.
  - `CliStructuredOutputTests`: five new diagnostic ids (`new.args.view3d`, `new.view3d.note`,
    `new.view3d.ports-left-out`, `em.component-ports.refused`, `check.c3d.component-port`).
  - `ServeProtocolAdapterTests`: `create/view3d`. It also gains `run/sensitivity`; see below.
  - `tests/Firewall.Tests/user-facing-text-allowlist.txt`: the converter's two `JsonException` messages, following the
    precedent of the thermal names converter.
- Runs, targeted classes only (Ui.Tests: 16 classes: ComponentPortBuild, CoSimNetlist, AuthoringCliVerb,
  CliStructuredOutput, GeneratedReference, ReferenceCliVerb, ServeProtocolAdapter, EmFrameworkFree, C3dPortDirection,
  PortEdgeWidth, MeshRobustness, RobustMeshing, HierarchyGate, CheckAndExplainCliVerb, EmCliVerb, SimulateGate):
  - **313/315** on the first pass.
    - `ServeProtocolAdapterTests.EveryAdvertisedArgument…` failed on `'run/sensitivity'`. That mode was added at HEAD by
      SENS-1 without updating this gate's map. I added `run/sensitivity` and `create/view3d`, and the class then passed
      39/39. EMB-5 may add the same line; that conflict is trivial.
    - `MeshRobustnessTests.ACurvingStagePastItsCap…` is EMB-2's test, on a path I did not touch. It passes alone, so it is
      load-dependent: it uses a 1 s stand-in script for Gmsh.
  - Firewall: 78/79. The one failure is the same pre-existing `UserFacingTextGateTests` list EMB-2 and EMB-3 reported: 7
    entries in `ComponentModelFactory.cs` and `src/Engine/Mom/*`, all outside this brief. My own two were added to the
    allowlist.
- **The one small Palace run** (Palace 0.18.1 and Gmsh 4.15.2 are installed here). Run once with
  `--settings circuitrf.benchmark.runsettings`, under a watchdog that would kill Gmsh or Palace past 3 GB RSS; it did not
  fire.
  - Fixture: a 0.4 mm line on the 508 µm RO4350B board, broken by a 0.4 mm gap, with R1's pads either side, a drawn port at
    each end and R1's component port across the gap.
  - Board result: |S33| = 0.361 at 2 GHz. The expected value is 1/3: two 50 Ω half-lines in series.
  - Co-simulation (R1 as the 50 Ω placeholder, run through `sparam` on the written `.cnl`): |S21| = 0.664 against 2/3.
  - Size: 166,678 unknowns after two refinement passes.
  - Time: about 50 s for the test, measured under load from the other agents' builds. It is tagged Benchmark for that
    reason; no assertion is about time.

### Docs
- `docs/user/src/reference/em-3d.md`: new section #part-ports, plus the paragraph on gap ports against pad-to-plane pairs.
- `docs/user/src/reference/em-setup.md`: #component-ports-list.
- I regenerated both pages with DocGen. Like EMB-3, I reverted `docs/user/assets/js/search-index.js`, because its churn
  comes from other pages that are not yet regenerated.
- `docs/design/cli.md`: the `new view3d` row, a new §8.10, and the MCP table row. These are localized edits.
- `src/Cli/DocumentSchema.cs` (the `3d-view` reference): a component-ports paragraph.

### Deferred
- R-emb1-6's shunt-return warning, for the reason given under R-emb1-6.
- Planar (MoM) component ports, which the brief puts out of scope.

### Overlap with sibling briefs
- `src/Cli/Serve/ToolCatalog.cs`: new option entries (`run em componentPorts`, `explain componentPorts`) and the new
  `create` mode `view3d`, plus the `create` tool's one-line description.
- `docs/design/cli.md`: the verb table row, §8.10 inserted before §9, and the §11.3 `create` row.
- `tests/Ui.Tests/ServeProtocolAdapterTests.cs`: two map lines.
- I did not touch `src/Cli/JsonRun.cs` (no static fields added) or `McpServer.cs`.

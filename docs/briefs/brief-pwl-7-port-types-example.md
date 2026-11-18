# PWL-7 — The Port Types example: every port on the simplest geometry it takes, measured side by side

Series overview: `brief-pwl-0-overview.md`. Needs briefs 3 and 4; briefs 5 and 6 for the EMerge and openEMS rows (a row
whose brief has not landed is left out, not filled in by hand).

## Why

circuitRF has more than a dozen ways to feed a structure across its planar and 3D solvers, and the user docs describe
each one in its own section. Nothing puts them side by side on one structure with one known answer, so the question a
newcomer asks first, *which port should I use here?*, has no single place to be answered, and nothing shows what
choosing the wrong one costs. This example is that place, and the user docs gain the same guide.

## The workspace: `examples/Port Types`

Each cell is the simplest structure its ports take, with an answer known in closed form wherever one exists. Sweeps
are short (1–10 GHz, 10 points, unless a cell says otherwise) so every setup runs in minutes.

| Cell | Structure | Ports compared | Reference |
|---|---|---|---|
| **Microstrip** | 50 Ω line, 20 mm, on the 20 mil PTFE-glass laminate of `board-and-connector.ctech` (the 3D Wave Ports technology), 18 µm copper; a layout and a 3D view of the same line | Planar **Edge** and **Internal** (`.cem`); 3D **Lumped**, **Wave** and **Wall**, each on Palace, EMerge and openEMS | classic closed-form microstrip with dispersion and loss |
| **Coupled Pair** | edge-coupled microstrip pair, same laminate, 20 mm | Planar **Edge** (one calibration group); 3D **Wave with terminals** and **Wall** (one group per end), on every solver that runs each | classic closed-form coupled microstrip |
| **Coax** | 50 Ω coax, 0.4 mm pin in a 1.34 mm PTFE bore (the 3D Connector's), 20 mm | **Coaxial** (annular lumped) and coax **Wave**, every solver that runs each | exact closed form |
| **Waveguide** | hollow guide 22.86 × 10.16 mm, 30 mm, 8–12 GHz | **Hollow-guide wave** port, Palace and EMerge | exact TE10 |
| **Floquet Cell** | a 3 mm dielectric slab (εr 4) in a 10 mm periodic cell, broadside, 1–20 GHz | **Floquet** ports, EMerge | exact slab transmission and reflection |
| **Line over Silicon** | brief 2's fixture Si: 10 µm line on 5 µm oxide over 200 µm of 10 Ω·cm silicon, 1–40 GHz | 3D **Lumped**, **Wave** and **Wall**, on each solver | none: compared with each other, and γ and Z_c shown over frequency |

Not drawn here, with a pointer to the example that shows each: the **turned** lumped port, the lumped **gap on a
cylinder** (openEMS), **component ports** (MMIC Component Ports, PCB Component Ports), the **internal delta gap**
(the planar Internal port's other form) and the **cross-section** kernel's conductor ends (Klopfenstein Taper).

Setup names say the port and the solver (*Wall · Palace*, *Wave · openEMS*), so the setups list reads as the table does.
**Wave and Wall share one 3D view and one set of drawn ports**: the ports are drawn as wave ports, and each *Wall · …*
setup runs them as wall ports through its Analysis Setup's Kind column (brief 4 R-pwl4-6b). The README says so in its
Microstrip section, because switching the active setup and watching the overlay change is the quickest way to see what
a wall port is. Lumped ports need their own sheets, so the lumped setups run on a second 3D view of the same line.
One technology for every cell that has a board. No results are committed; the user runs them.

## The README

In this order:

1. **One paragraph** on what the workspace is for, and that every number below came from running it.
2. **Choosing a port.** One table, every port type circuitRF has (the 14 rows below), with columns *Port*, *Solvers*,
   *Best used for* and *Avoid when*. **This text is shared word for word with the user docs** (§Docs below); write it
   once, then copy it.
3. **One section per cell**: what the structure is, its closed form stated with its numbers, how each port was set up
   (and for the wall port, that it was one right-click), then the **benchmark table**: *Port*, *Solver*, worst |ΔS11|,
   worst |ΔS21|, worst ∠S21 error (degrees), ε_eff at the band's centre against the closed form, unknowns, solve time
   (with the standards' time shown separately for wall ports), all against the cell's reference. Under it, two small
   PNGs made with `circuitrf plot`: the ∠S21 error over frequency for every port in the cell, and S11 on a Smith chart.
   Times are stated with the machine and build they were measured on, and are not held by any test.
4. **What the numbers say**, per cell, in a few sentences: which port was closest, by how much, and why, in terms a
   designer can use. Only what the runs show.

**Line over Silicon** has no reference, so its section shows ε_eff and Z_c over 1–40 GHz from each port and says
whether any of them moves between neighbouring frequencies. It states plainly that nothing there is an accuracy
figure.

## Choosing a port: the draft text

Start from this, change it where the runs disagree, and keep every claim one the runs or the reference docs support.

| Port | Solvers | Best used for | Avoid when |
|---|---|---|---|
| Planar **Edge** | planar | Lines on a layered board or MMIC entering the structure at a conductor's end; fastest route to a de-embedded answer, port discontinuity removed | The structure has 3D features (connectors, bond wires, board edges that matter), or other metal sits within a few substrate heights of the feed and cannot be grouped with it |
| Planar **Internal** | planar | A component or device terminal inside the metal, between the metal and the ground plane | Power enters the structure along a line: an internal port is not de-embedded and its own parasitic stays in the answer |
| 3D **Lumped** | Palace, EMerge, openEMS | A small gap between two conductors: pads, a component site, a quick first setup on any solver | The sheet is tall against the wavelength (thick substrate, high frequency): its series inductance stays in S11 and nothing removes it |
| 3D **Lumped, turned** | Palace, EMerge | Pads placed at an angle, such as a part at 45° | openEMS (refused); a gap that is not a true rectangle |
| 3D **Lumped on a cylinder** | openEMS | A gap on a curved surface solved on a cylindrical grid, such as a birdcage coil | Any other solver, or a structure not coaxial with the grid's axis |
| 3D **Coaxial** | Palace, EMerge; openEMS inside the model | A connector or coax fed in its own TEM field, anywhere in the model | A line that is not coaxial; on openEMS, a ring on a face of the air box |
| 3D **Wave** | Palace, EMerge, openEMS | One line ending on the air box's face, fed and measured in its own mode, with no port parasitic | The substrate is lossy or conducting enough that the face's mode can change from one frequency to the next; the line cannot run to the box's face |
| 3D **Wave with terminals** | openEMS any number; Palace two whose modes differ in speed; EMerge | Coupled lines ending together on the box's face, in terminal S-parameters | Lines in a homogeneous fill on Palace (use openEMS); three or more terminals on Palace |
| 3D **Hollow-guide wave** | Palace, EMerge (two modes: EMerge) | Waveguide sections, transitions and filters | Anything with a centre conductor; openEMS |
| 3D **Floquet** | EMerge | One cell of an infinite array: frequency-selective surfaces, reflectarray and metasurface elements | A finite structure, or a cell that cannot be made periodic on both side axes |
| 3D **Wall** | Palace, EMerge, openEMS | Lines meeting a PEC wall: shielded circuits, RFIC stacks over conducting silicon, coupled lines or a bus at one wall; the port discontinuity and the coupling at the port are calibrated out | The line cannot run straight and uniform to the wall for its end run; coupled lines whose modes travel at one speed; a face that must radiate (the wall is PEC); a cross-section so large that its standards cost more than the run |
| **Component ports** | planar, Palace, EMerge, openEMS | A placed part's pads, with nothing drawn: every part's terminals at once | A connection that is a line rather than a part |
| Planar **Internal delta gap** | planar | A lumped element or device cut into the middle of a conductor | An end-fed line (use Edge) |
| Cross-section **conductor ends** | cross-section kernel | A uniform line or coupled lines from their cross-section alone, in milliseconds | Anything that is not uniform along its length |

## Requirements

- **R-pwl7-1 — The workspace** as tabled above, with a row in `examples/examples.json` directly after 3D Wave Ports, and
  the index gate (`ExampleWorkspacesTests`) passing.
- **R-pwl7-2 — The numbers are held.** `expected-numbers.json` holds every accuracy figure in the README tables (not the
  times), and `PortTypesExampleTests` (`Category=Benchmark`) re-runs the setups and holds them, as the 3D Wave Ports
  example's are held. A row is in the README only if a run produced it.
- **R-pwl7-3 — One guide, two places.** The *Choosing a port* table is identical in the README and in the user docs; a
  routine test compares the two tables cell by cell (whitespace-normalised) and fails on any difference.
- **R-pwl7-4 — Look at it.** Open every cell's views headlessly with `tools/UiSnapshot`, check that each port is drawn
  where the table says, and list the pictures in the completion note.
- **R-pwl7-5 — Runs are short.** Every setup finishes in a few minutes on the development machine under a memory
  watchdog; a setup that does not is made smaller, and the README says what was traded.

## Docs

- `docs/user/src/reference/em-setup.md`: a new section **Choosing a port** (`#choosing-a-port`) at the top of the
  Ports material, holding the table above verbatim and linking to each port's own section and to the example.
- The example's page in the user docs, made as the other examples' pages are.
- `drawing-in-3d.md` §Simulating links to *Choosing a port* where it first mentions *Make Port*.
- The docs are regenerated once, after this brief, for the whole series.

## Completion

`examples/RESOLVED.md` §Port Types: setups, run times, anything a run showed that the draft table had wrong, and what
was changed in the table because of it.

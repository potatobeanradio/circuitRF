# Brief series PWL — the wall port: a calibrated lumped port on a PEC face of the air box, and a Port Types example

**Goal.** A 3D setup can feed a line where it meets a PEC wall of the air box, with the port's own discontinuity
calibrated out by line standards that circuitRF builds and solves for itself. It runs on Palace, EMerge and openEMS.
Lines that meet one wall together (a coupled pair, a bus, a line beside a guard trace) are calibrated together, so
the coupling between them at the port is removed. A new example workspace, **Port Types**, shows every port circuitRF
has on the simplest geometry each one takes, with a measured comparison in its README.

## What the port is

A **wall port** is the 3D counterpart of the planar solver's calibrated edge port, placed where a shielded structure
has a perfect ground right beside it: a **PEC face of the air box**.

- The line is drawn running to the face, as a wave port's line is. In the solved problem its end is cut back by a
  short **gap** `g`, and a lumped source fills that gap, **from the wall (−) to the line's end (+)**, across the line's
  full width, in the line's own plane. The wall is the port's ground reference, so the source is referenced to ground
  right at the port: there is no tall vertical sheet and no series inductance of the port's own.
- The raw answer at the gap still carries the gap and the field crowding at the wall. Those are removed by
  **calibration**: circuitRF builds **line standards** — the wall's own cross-section, extruded between two PEC walls,
  at two or more lengths — solves them with the same solver and the same mesh settings, and finds from them the
  line's propagation constant γ (one per mode) and the **error box** between the source and a **reference plane at
  the wall**. The published S-parameters are at that plane (or moved into the structure by the port's Offset), in the
  port's Z0, exactly as every other port's are.
- **Several lines on one wall are one calibration group.** Their standard carries all of them, so the group has one
  N×N modal error box and one γ and one Z_c per mode. The coupling between the lines at the port is part of the box
  and is removed with it. This is the algebra the planar solver already ships (PCAL4), unchanged.

**Why the planar solver does not get one.** The planar kernel is laterally open: its substrate and ground run on
forever and there is no wall for a port to stand on. Its edge port already calibrates with mirror-image line
standards built from the structure's own mesh, and its calibration groups already handle several lines at one plane.
The wall port brings that method to the 3D solvers, where a wall exists.

## Why it matters

The 3D solvers offer two ways to feed a line today, and each has a known weak spot:

- **A lumped port** is a vertical sheet from the line to its return. Its series inductance shows as a step in S11 at
  the top of the band, and nothing removes it.
- **A wave port** solves the line's mode on the air box's face at every frequency. It is exact when the mode is found,
  and that is the weak spot on two kinds of structure: **several conductors on one face**, where modes of equal speed
  come back in an arbitrary mixture (Palace refuses them today), and **lossy or conducting substrates** such as an
  RFIC stack over doped silicon, where the mode a face-mode solve returns can change from one frequency to the next.

A wall port solves no mode. It needs only a lumped source, which every solver has, and line standards, which are
ordinary 3D solves. A field test on an RFIC stack over conducting silicon found this approach the more robust of the
two, and multi-line walls are where it is most needed.

## Background

The calibration follows the double-delay method and its unification with short-open calibration:

- J. C. Rautio, "A de-embedding algorithm for electromagnetics," *International Journal of Microwave and Millimeter-Wave
  Computer-Aided Engineering*, vol. 1, no. 3, pp. 282–287, July 1991.
- J. C. Rautio and V. I. Okhmatovski, "Unification of double-delay and SOC electromagnetic deembedding," *IEEE
  Transactions on Microwave Theory and Techniques*, vol. 53, no. 9, pp. 2892–2898, Sept. 2005,
  https://doi.org/10.1109/TMTT.2005.854250.

circuitRF's planar calibration (`src/Engine/Mom/PlanarCalibration.cs`, `PlanarDeembed.cs`,
`PlanarModalCalibration.cs`) is the implementation this series reuses; read its file headers before brief 1.

## How a user sets one up

**In a 3D view — one step.** Right-click the air box's face where the line ends (or the line's end face on that
face) and choose ***Make Port ▸ Wall***. It writes one wall port per signal conductor meeting that face, numbered
with the next free numbers from left to right along the face, as *Make Port ▸ Wave* writes terminals today. Nothing
else is asked: the face becomes PEC with no padding for every setup that runs the port, the gap, the standards and
the grouping are automatic, and the run's notes say what was chosen.

**In a layout's 3D setup (`.cem`) — two steps.** Place the port label with the layout's Port tool, as today, then set
its row's **Kind** to **Wall** in the Solver group's port table. The table already sets Lumped or Wave there; Wall is
a third value in the same column.

**Switching an existing wave port — one step.** A port on an air-box face has a **Kind** combobox (Wave / Wall), in
its Properties in a 3D view and in the port table in a `.cem`, and on its right-click menu as *Kind ▸ Wave / Wall*.
The whole face switches together, terminals and all, and switching back returns the same file.

**Per setup, without touching the drawing.** A 3D view's **Analysis Setup** (*Simulate ▸ Setup Analyses…*) gains the
port table it lacks today, listing the ports on the air box's faces with the same Kind combobox: *As drawn*, *Wave*
or *Wall*. So two setups on one 3D view can run the same ports as wave ports and as wall ports, side by side. It is
the `.cem`'s own `Ports3D` key, read by a 3D view's setup for these two kinds; Z0 and Offset stay on the port.

**Then, as for every port.** The port table row sets **Z0** and **Offset** (the reference plane moved into the
structure). The setup has one switch, **Calibrate wall ports** (on), which exists so a user can see the raw answer
at the gap for comparison. `circuitrf check` and `explain` report each wall's group, its standards and the time they
add; `run dryRun=true` prices them.

## Does it run on Palace, EMerge and openEMS?

**Yes, on all three**, because nothing in it is solver-specific beyond a lumped source in a gap that touches a PEC
face:

| | Palace | EMerge | openEMS |
|---|---|---|---|
| The gap's source | `LumpedPort` on the gap sheet, one edge on the PEC face | lumped port on the gap sheet | lumped port along the line, its first edge on the PEC boundary |
| Gap size | free | free | at least one grid cell (the grid places lines at the wall and at `g`) |
| Port-region mesh identical in DUT and standards | extruded port block (brief 2 measures it) | the same block | grid lines copied; identical by construction |
| Standards | ordinary Palace runs | ordinary EMerge runs | ordinary openEMS runs |
| Calibration | solver-independent, after the solves | same | same |

Brief 2 confirms the Palace column by measurement before anything is built; briefs 5 and 6 confirm the other two
and refuse by name whatever they cannot state.

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-pwl-1-shared-line-calibration.md` | The two-line and modal calibration algebra moved out of `Mom/` behind a solver-independent API; the planar solver bit-identical | — |
| `brief-pwl-2-palace-spike.md` | Hand-built Palace fixtures: gap size, port-region mesh identity, the source of Z_c, the end-run length. Decisions recorded before anything ships | 1 |
| `brief-pwl-3-wall-port-model-and-palace.md` | `Kind: "Wall"` in `.c3d` and `.cem`, resolution, grouping, the standards builder, the cache, Palace end to end, `check`/`explain`/dry run | 1, 2 |
| `brief-pwl-4-wall-port-editor.md` | *Make Port ▸ Wall*, the port table's Kind, the overlay, Properties, the setup switch | 3 |
| `brief-pwl-5-emerge-wall-port.md` | EMerge | 3 |
| `brief-pwl-6-openems-wall-port.md` | openEMS | 3 |
| `brief-pwl-7-port-types-example.md` | The **Port Types** example workspace and its README benchmark comparison; a *Choosing a port* guide (best used for / avoid when, every port type), the same text in the README and the user docs | 3, 4; 5 and 6 for their rows |

Briefs 5 and 6 are independent of each other and of 4.

## Decisions already made

- **D1 — One more port kind, configured like the others.** `Em3dPortKind.Wall` beside `Lumped` and `Wave`; in a
  `.c3d` a port entry with `"Kind": "Wall"`, in a `.cem` a `Ports3D` row with `"Kind": "Wall"`. It has a Number, Name,
  Z0, Offset, Model and Flip like every port, and appears in every port list, port map and Touchstone header like
  every port.
- **D2 — Polarity is the wall's.** The wall is −, the line is +. Flip swaps them, as on every port.
- **D3 — The reference plane is at the wall** (the drawn end of the line), moved into the structure by Offset. The gap
  is part of the error box and is never a user setting.
- **D4 — Calibration is by line standards only**, with the planar solver's algebra: γ from the standards' cascade
  eigenvalues, error boxes from the mirror-image relation between a standard's two ends, one modal box per group.
  This is the method already measured and gated in this repository, and it needs no new kind of standard.
- **D5 — Z_c is stated as the planar solver states it**, Z_c = γ/(jωC′) with C′ the wall cross-section's
  per-unit-length capacitance (complex where a layer conducts). Brief 2 decides where C′ comes from; whichever it
  is, it must give the same C′ to every solver.
- **D6 — Groups form by distance, as the planar solver's do.** Wall ports on one face whose lines are within the
  coupling clearance of each other (the planar solver's `DrivenNeighbourClearanceHeights`, in the cross-section's own
  substrate heights) are one group; a line clear of every other is a group of one. Group size is capped
  (`MaxCalibrationGroupSize`, raised to 4 for 3D if brief 3 measures the cost acceptable). Modes of (nearly) one
  speed are refused by name, asked at setup from the cross-section's electrostatics as the planar solver asks it: a
  coupled stripline, or any pair in a homogeneous fill, cannot be separated by line standards. The refusal names the
  wave port with terminals on openEMS as the alternative.
- **D7 — A conductor meeting a wall-port face that is not a port** is grounded by the wall, in the solved structure
  and in every standard alike; `check` warns about it by name, because a line that meets a PEC wall is shorted there.
- **D8 — The end run must be uniform.** From the wall inward for the end-run length (brief 2 measures it, in
  substrate heights), everything crossing the group's region must run straight along the line's axis with an unchanging
  cross-section, because that is what the standard reproduces. Anything else is refused, naming the object and the
  distance from the wall, with "extend the line" as the remedy. No feed is grown in this series.
- **D9 — Standards are solved at exactly the setup's frequencies, cached by cross-section.** The cache key is the
  cross-section's geometry and materials, the solver and its mesh settings, and the frequency list, so a tuning sweep
  or a re-run pays for its standards once.
- **D10 — Nothing shipped moves.** Every shipped `.c3d` and `.cem` reads, resolves and lowers to byte-identical solver
  input; no shipped file states a wall port.
- **D11 — No vendor or tool names** anywhere in the series, its code, its docs or the example.

## Gates, for the whole series

1. **Nothing shipped moves** (D10), checked on every path the series touches.
2. **The planar solver is bit-identical** after brief 1.
3. **A matched line is matched.** One fixture serves briefs 2, 3, 5 and 6: a straight 50 Ω stripline (exact closed
   form for Z_c and γ) between two wall ports, 20 mm long, solved at 1, 5 and 10 GHz. After calibration, |S11| and the
   error in |S21| and ∠S21 against the closed-form line stay within the tolerance brief 2 measures and records.
4. **A coupled pair is a pair.** An edge-coupled microstrip pair (its two modes travel at different speeds, see D6)
   with a two-port wall at each end: the four-port's even- and odd-mode γ and Z_c match the classic closed-form
   coupled-microstrip model within brief 2's tolerance, and near- and far-end coupling match the ideal coupled line
   built from them.
5. **The example's numbers are recorded and held.** Brief 7's README quotes only numbers its runs produced, and
   `expected-numbers.json` plus a `Category=Benchmark` test hold them, as the 3D Wave Ports example's are held.

## Rules that apply throughout

- **Tests:** one per claim, named classes only. Never the full suite and never all of `Ui.Tests`. No new timing test.
  Solver fixtures are `Category=Benchmark` when they cross ~5 s; their wiring (written `.geo`, Palace JSON, EMerge
  script, openEMS XML, the standards' problems) is checked in the routine tier with no solver.
- **EM runs are short and specific.** Three frequencies on fixtures, short sweeps in the example, nothing over a few
  minutes, and a memory watchdog on every ad-hoc run.
- **UI changes are rendered headlessly and looked at** (`tools/UiSnapshot`), and each picture is listed in the
  brief's completion note.
- **Findings go to the nearest `RESOLVED.md`**, never `CLAUDE.md`.
- **User docs** are edited under `docs/user/src` as each brief lands; they are regenerated once, after the series.

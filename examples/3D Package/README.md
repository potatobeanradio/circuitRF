# 3D Package

An MMIC die in a ceramic package with a lid on, **drawn in circuitRF's 3D editor**: the package in mil, the die's
layout in µm, placed inside it, and bond wires from the die to the package's leads. Two setups solve it with
**Palace**: the S-parameters through the package, and the resonances of the cavity under the lid.

The chapter that teaches the editor through this workspace is **The 3D Editor**, in **Help ▸ circuitRF
Documentation**. It quotes the same numbers as this file.

## You can draw without a solver

The 3D editor is circuitRF's own. **Opening this workspace, orbiting, selecting, editing, placing cells, drawing
wires and ports, `check` and `explain` all work with no solver installed.** Only **Run** needs Palace. The first
time you press it without Palace, the run is refused with **Install Palace …** — the expected first step, not a
fault. Palace is built from source, which took 52 minutes on the reference machine below.

## What is here

| Cell | View | What it is |
|---|---|---|
| **Thru die** | layout, µm, `tech/mmic-gaas.ctech` | A 1000 × 600 µm GaAs die, 100 µm thick with a gold back side: a 75 µm line between two 100 µm pads |
| **Package** | 3D view, mil, `tech/ceramic-package.ctech` | The package, drawn: floor, alumina base, die-attach pad on four vias, two leads, the die placed as `U1`, two bond wires, two ports, four walls and a lid |

The two technologies are different on purpose: a die is made in one process and packaged in another. A 3D view
places a layout through the layout's own technology, so the GaAs die sits in the alumina package exactly as
drawn.

**The package, dimension by dimension** (the cell's parameters and the 3D view's VARs, in the Variables panel):

- The cavity is `cav_w` × `cav_l` × `cav_h` = **400 × 320 × 40 mil** — three **cell parameters**. The floor, the
  base, the walls and the lid are written in terms of them, so changing one redraws the package; the die, its
  pad, the leads, the wires and the ports stay where they are.
- Walls `wall` = 20 mil thick, the floor `floor_t` = 2 mil, the lid `lid_t` = 5 mil (VARs). The floor, walls and
  wires are gold; the lid is a lid alloy of σ = 2 MS/m.
- The base is 5 mil of alumina (εr 9.8) over the whole cavity floor. The leads are 5 mil wide and 0.2 mil thick
  on it — 50 Ω — and 170 mil long, stopping 4 mil short of the walls.
- The die sits on a 46 × 30 mil gold attach pad, 0.5 mil thick, joined to the floor by four gold vias 4 mil across.
- Each wire is 1 mil gold, a ball on the die pad and a wedge on the lead, with an 8 mil loop.
- Each port is a 5 × 5 mil sheet at a lead's outer end, from the floor up to the lead: **floor (−) to lead (+)**.

**How the package was drawn** — every gesture is one the editor offers:

1. **Variables panel**: *Add* `cav_w`, `cav_l`, `cav_h`, `wall`, `floor_t`, `lid_t`; *Promote to Cell Parameter*
   on the first three.
2. **Box** (Shift+A B), material from the toolbar: one click, then the width, depth and height **typed**, as
   expressions (`cav_w + 2*wall`, Tab, `cav_l + 2*wall`, Enter, `floor_t`, Enter). The corner and the name typed in
   **Properties**. So for the floor, the base, the attach pad and the two leads.
3. **Cylinder** (Shift+A Y) for one via — radius `2`, height `5` — then **Array…**, 2 × 2 at 36 × 20 mil.
4. **Place Cell Instance…**: *Thru die*, View *Layout*, with the **bottom-centre** handle on the attach pad;
   renamed `U1`.
5. **Wire** (Shift+A W), ball–wedge: a click on the die pad, a click on the lead, `8` typed for the loop height.
   Properties' *Start* point then put each ball on its pad's centre line.
6. **Port**, on the YZ drawing plane at each lead's end: two corners.
7. **Box** again for the four walls and the lid — last, so they did not hide the inside while the rest was drawn.
8. **Simulate ▸ Setup Analyses…**: *Add* twice; *Driven* 2–30 GHz, 1,121 points; *Lid modes* set to
   *Eigenmode*, 1 mode above 20 GHz; on each, Quality *Draft*, element order `2`, "At metal and ports" `1`, Linear
   solver *Direct*; and every face of the air box set to **PEC** with **no padding**, from the box's face menu.

## First: why the air box is flush with the metal

The package is its own shield: floor, walls and lid enclose everything. So each setup's air box is set to lie
**exactly on the package's outer faces** — every face PEC, padding 0 — and nothing outside the metal is meshed.
Each face of that box lies wholly inside the floor, a wall and the lid, and circuitRF recognises a face covered
by metal and asks the mesh for none of it.

## The numbers, and where they come from

Every time and memory figure below was **measured once**, on an Apple M4 with 10 cores and 16 GB, running macOS
27.0, Palace 0.18.1 on 10 processes, and Gmsh 4.15.2. A time is the wall clock for the whole run, Gmsh and
Palace together; a memory figure is Palace's own peak over all its processes. Your machine will differ.


**`check`** on this workspace reports no error and one warning: the die's port labels carry no area for the
design rules to check. That is expected — a placed cell's own ports are never used; the package's are.

## Driven: S-parameters through the package

Palace, 2–30 GHz, 1,121 points: **4 min 46 s**, peak **6.4 GB**. |S21| is **−0.41 dB** at the band centre, 16 GHz.
At 22.15 GHz it falls to **−5.35 dB**: a notch about 50 MHz wide. That notch is the lid's cavity mode below — the
package coupling the line to itself — and it is why the sweep has 25 MHz steps; at 0.5 GHz steps it is invisible.

## Lid modes: the cavity's resonance

Palace eigenmode, the one mode above 20 GHz: **35 s** of Palace, peak **4.0 GB**. The mode is at **22.16 GHz**
with a Q of **318** (the gold, the lid alloy and the two ports' 50 Ω all load it). It is the same resonance the
driven sweep's notch shows.

**The external check.** A rectangular cavity 400 × 320 mil and 40 mil high with a 5 mil alumina slab (εr 9.8) on
its floor has its lowest mode, from the closed form for a layered cavity, at **22.23 GHz**. That is the one
number here that is not circuitRF's own output. The package's mode is 0.3 % below it: the die, its attach pad, the
leads and the wires are what the closed form leaves out. The run of the package without its die, which would
split that difference, was not made.

## The settings traded away, and what they cost

Both setups use **Draft with element order 2**, "At metal and ports" (`EdgeRefinement`) **1**, and the **Direct**
linear solver — chosen so each runs in under five minutes on the machine above. What the alternatives measured:

| Setting | Driven | Lid modes |
|---|---|---|
| As shipped | 4 min 46 s, 6.4 GB | 35 s, 4.0 GB |
| Linear solver *Iterative* | 7 min 46 s, 3.5 GB; the same S-parameters | not finished after 10 min |
| Element order 1 (plain *Draft*) | 25 s, 1.9 GB; **no notch**, and \|S21\| up to 1.2 dB and 60° away | not run |
| "At metal and ports" 0.2 (the default) | not run | 2.16 M unknowns: not finished after 12 min |
| *Standard* (two refinement passes) | stopped after 14 min, in its second pass | not run |

The shipped settings refine nothing at metal because every wall here is metal, so the default refinement would
fill the whole 1 mm-high cavity with fine elements; the wires keep their own curvature sizing and the ports their
own. Order 2 is what resolves the resonance. *Direct* is what makes it fast enough.


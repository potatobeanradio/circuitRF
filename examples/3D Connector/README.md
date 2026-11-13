# 3D Connector

A coaxial connector launching onto a 50 Ω microstrip at the edge of a board — the most common 3D problem an RF
board has — **built from the operations circuitRF's geometry kernel adds to the 3D editor**:

- a metal **housing with its bore subtracted** (*Boolean ▸ Subtract…*), the bore **kept** as the connector's PTFE
  fill (*Keep tools*);
- a **flange imported from a STEP file** (*Import STEP…*) and **united** with the housing (*Boolean ▸ Unite…*);
- a **centre pin whose tip is rounded** (*Edge* mode, *Fillet…*) — a fillet on a curved edge.

Two setups solve it, **Palace** and **openEMS**, and the page below asks the question the example exists to answer:
**does each solver see the fillet?** It is answered with measured numbers, and the answer is different for each.

The chapters that teach this are **The 3D Editor** (booleans, fillets, STEP) and **EM Solvers ▸ What each solver sees
of curved geometry**, in **Help ▸ circuitRF Documentation**. They quote the same numbers as this file.

## You can draw without a solver — but not without the geometry kernel

Booleans, fillets and STEP parts are built by **OpenCASCADE**, the geometry kernel that ships inside circuitRF.
**Settings ▸ Solvers** says whether this installation has it; a development build that has not built it cannot open
this example's `Launch` or `Flange` (a dialog says why and how to restore it). Opening, orbiting, editing, `check`
and `explain` need no solver. **Run** needs Palace or openEMS; the first press without one offers to install it.

## What is here

| Cell | View | What it is |
|---|---|---|
| **Board** | layout, mil | 200 × 200 mil of 20 mil PTFE-glass laminate (εr 2.2) with a ground plane under it, and a 62 mil line from 4 mil inside the board's edge to its far edge |
| **Flange** | 3D view, mm | The connector's flange: a 1 mm plate, 7 × 4.5 mm, with a hole for the bore. **The source of `Launch/3d/flange.step`** |
| **Launch** | 3D view, mm | The board placed from its layout as `B1`; the housing with its bore subtracted and kept as fill; the flange imported from STEP and united with the housing; the pin, its tip filleted; two ports; two setups |

One technology, `tech/board-and-connector.ctech`, serves all three: the laminate, copper, PTFE (εr 2.1) and a
connector alloy (σ = 15 MS/m) for the housing, flange and pin.

**The dimensions, stated once** (the Launch's VARs, in the Variables panel):

- **The coax** is `pin_d` = 0.4 mm inside `bore_d` = 1.34 mm of PTFE. Its impedance by the closed form is
  Z₀ = (60/√εr)·ln(D/d) = (60/√2.1)·ln(1.34/0.4) = **50.06 Ω**.
- **The line** is 62 mil wide on 20 mil of laminate: **49.85 Ω** by Hammerstad and Jensen's closed form (at zero thickness) — the model
  family the **Klopfenstein Taper** example's closed-form taper, MKLOPF, is built on.
- **The pin** sits `axis_z` = `sub_h + cu_t/2 + pin_d/2` above the ground: resting on the line and sunk half the
  copper's thickness into it, so the two meet in a volume rather than along a line of tangency no mesher can use. It
  runs from 0.3 mm short of the bore's floor to 1.5 mm out over the line.
- **The tip's fillet** is `tip_r` = 0.1 mm — chosen, before anything was measured, to span several of Palace's
  elements and about one of openEMS's cells, which is the point of the comparison below.
- **The housing** is 4 × 3 mm by `body_h` = 3 mm tall behind the 1 mm flange; the bore runs 4.5 mm in from the
  flange's face, so its floor is 0.5 mm of metal.

Those two closed-form impedances are **external references**: the only numbers on this page that are not
circuitRF's own output.

**How it was drawn** — every gesture is one the editor offers:

1. **Flange**: **Box** (Shift+A B) for the plate, its size typed; **Cylinder** (Shift+A Y) on the **YZ** drawing plane
   for the hole; select the cylinder, then the plate, and right-click ▸ **Boolean ▸ Subtract…** — the first selected is
   the Tool, the last the Blank; **OK**.
2. **File ▸ Export ▸ STEP…** from the Flange, as the dialog opens (flattened, precedence applied, AP214), to
   `Launch/3d/flange.step`.
3. **Launch**: the **Variables** panel for the VARs above; **Place Cell Instance…** for *Board*, its layout, the ground
   plane's top on z = 0 and the board's edge on x = 0, renamed `B1`.
4. **Box** for the housing, **Cylinder** on the YZ plane for the bore (material PTFE); select the bore, then the
   housing; **Boolean ▸ Subtract…** with **Keep tools** ticked.
5. **File ▸ Import ▸ STEP…** `flange.step`: its one part takes the connector alloy **by its colour** — the exporter
   wrote each part in its material's colour, and the importer matches colours back to the technology's materials.
6. Select the flange, then the housing; **Boolean ▸ Unite…**.
7. **Cylinder** on the YZ plane for the pin; press **E** for Edge mode, click the rim at its tip (`side|top`),
   right-click ▸ **Fillet…**, radius `tip_r`.
8. **Port** (Shift+A P): P1 on the **XZ** plane through the axis, across the 0.3 mm gap between the bore's floor and the
   pin's end; P2 on the **YZ** plane at the board's far edge, from the ground up to the line.
9. **Simulate ▸ Setup Analyses…**: **Add** twice — *Palace* (Quality *Draft*, Linear solver *Direct*) and *openEMS*
   (the default grid) — each 2–18 GHz in 5 points; on each, the air box's **xmin** face on the housing's back as **PEC**
   with no padding, and the other five **absorbing**, 2 mm out.

**Where the STEP file came from.** `flange.step` is circuitRF's own export of the Flange cell, not a third party's
file. A real connector's STEP model from its maker is imported exactly the same way —
step 5 — and its parts are mapped to materials by name or colour, or chosen in the table.

## First: why the coax port is a gap

The coax end is fed the way a probe is: **P1 bridges the 0.3 mm gap between the bore's floor and the pin's end**, the
housing (−) to the pin (+). The gap is a small capacitance across the port, the same in every run below, so it moves the
numbers but not the comparisons between them, and it is kept here because every number on this page was measured with it.
A coax can also be fed in its own field, with nothing of the port's in the answer, in two ways: a **coaxial port**, a
lumped port whose sheet is the ring of PTFE at the bore's end (*Make Port ▸ Coaxial*, on all three solvers;
**EM Setup ▸ Coaxial ports**), or a **coaxial wave port** where the coax runs out to the air box's face. The
**Loop Sensor** example sets a coaxial port against a gap port on one connector, and the **3D Wave Ports** example runs
this launch on a wave port.

## The numbers, and where they come from

Every time and size below was **measured once**, on an Apple M4 with 10 cores and 16 GB, running macOS 27.0, Palace
0.18.1 on 10 processes, Gmsh 4.15.2, openEMS 0.37.0-rc3 and OpenCASCADE 8.0.1. A time is the wall clock for the whole
run; memory is Palace's own peak over all its processes; openEMS's size is the number of cells in its grid. Your
machine will differ.

**`check`** on this workspace reports no error and two warnings, both from the openEMS setup:
*"openEMS will not represent the 100 µm fillet on 'pin' (fillet(side|top)): the grid cell there is 200 µm, so the
edge is solved as sharp."* and *"openEMS staircases the 670 µm radius of 'housing' (bore:side) with about 3 cells"*.
They are the subject of the next section, not a fault.

## Palace and openEMS, with and without the fillet

Each setup was run twice: the pin's **Fillet** enabled, then disabled (untick **Enabled** on the pin's fillet row in the
object tree — disabled is exactly as if the fillet were not there).

| Run | Time | Size | \|S11\| at 10 GHz | \|S11\| at 18 GHz |
|---|---|---|---|---|
| Palace, fillet | 1 min 10 s | 4.0 GB | −16.71 dB | −40.75 dB |
| Palace, no fillet | 1 min 9 s | 4.3 GB | −16.69 dB | −40.90 dB |
| openEMS, fillet | 1 min 20 s | 360,360 cells | −17.25 dB | −19.03 dB |
| openEMS, no fillet | 1 min 18 s | 326,040 cells | −17.71 dB | −19.75 dB |

|S21| through the launch, fillet enabled: Palace **−0.246 dB** at 10 GHz and **−0.518 dB** at 18 GHz; openEMS
**−0.133 dB** and **−0.457 dB**.

**Palace meshes the fillet as the curved surface it is** — second-order curved elements, sized to the radius: its note
says the fillet *"puts elements of about 52.36 µm on it"*, about three along the quarter-circle. Enabling it moves
|S11| by 0.02 dB at 10 GHz and 0.15 dB at 18 GHz.

**openEMS does not see it.** Its warning says the cell at the tip is 200 µm, twice the 100 µm radius, so the edge is
solved as sharp. Its two runs still differ — by 0.72 dB at 18 GHz — but not because of the fillet: the fillet's faces
add grid lines of their own (360,360 cells with it, 326,040 without), and it is **the grid that moved**. At this grid
the staircase cannot resolve this fillet, nor the bore round it (the second warning); the difference between the
variants is the grid's, not the geometry's. The cells round the pin are set by the pin's own lines and the grading from
them, not by the wavelength: the 3D Wave Ports example's copy of this launch states a grading of 1.15 instead of 1.3,
which its coax port needs, and puts a 143 µm cell on the tip.

**So does the fillet matter?** Not at this connector's band. Palace, which does represent it, sees it change |S11| by
at most 0.25 dB (at element order 2, below: −31.95 dB with it, −32.20 dB without, at 18 GHz), where the match is
already 32 dB down, and |S21| by under 0.001 dB — and it sees that same small change, with the same sign, at both
element orders. A 0.1 mm rounding on a 0.4 mm pin is electrically negligible up to 18 GHz. The example demonstrates
where each solver's resolution sits; `tip_r` was not enlarged to make a difference appear.

**Palace against openEMS.** |S21| differs by 0.11 dB at 10 GHz (Palace −0.246 dB, openEMS −0.133 dB). The run's own
note names the cause: openEMS writes the housing, the pin and the copper as **perfect conductors** — an FDTD grid does
not resolve a skin depth — so its answer has no conductor loss, where Palace gives each metal its conductivity. |S11|
differs by more, and element order moves Palace's by as much (next section): at these settings neither solver's |S11|
is converged to better than a couple of dB, which is what a newcomer's first run in a minute or two buys.

## The settings traded away, and what they cost

Both setups are chosen so a first Simulate finishes in under two minutes. What the alternatives measured:

| Setting | Time | Size | \|S11\| at 10 GHz | \|S11\| at 18 GHz |
|---|---|---|---|---|
| Palace as shipped (Draft, element order 1) | 1 min 10 s | 4.0 GB | −16.71 dB | −40.75 dB |
| Palace, element order **2**, fillet | 10 min 43 s | 10.1 GB | −18.55 dB | −31.95 dB |
| Palace, element order **2**, no fillet | 8 min 32 s | 10.7 GB | −18.53 dB | −32.20 dB |
| openEMS as shipped (20 cells per wavelength) | 1 min 20 s | 360,360 cells | −17.25 dB | −19.03 dB |
| openEMS, **30** cells per wavelength | 1 min 21 s | 355,971 cells | −17.50 dB | −18.65 dB |

At element order 2 Palace's |S21| is **−0.207 dB** at 10 GHz and **−0.503 dB** at 18 GHz. **Is it converged?** Not at
the shipped settings: one step finer moves Palace's |S11| by 1.8 dB at 10 GHz and 8.8 dB at 18 GHz (where the match is
deepest, and a deep null is the most sensitive number a solve produces), and openEMS's by 0.25 and 0.38 dB. |S21| moves
by 0.04 dB (Palace) and under 0.01 dB (openEMS). Element order 2 needs 10 GB, which is most of a 16 GB laptop; *Standard*
quality (adaptive refinement) was not run. Nor was a full sweep: the shipped setups have five points so that each run
of the A/B is quick, and a finer sweep (Setup Analyses ▸ the setup's frequency) was not measured here. openEMS at 30 cells per wavelength still puts a 200 µm cell on the fillet, and its grid is no larger: the
cells beside the metal are set by its edges and the grading from them, not by the wavelength, so representing the
fillet would take a finer grid near the pin itself — or Palace.

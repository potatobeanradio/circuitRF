# Capacitance Extraction

A square pad beside a 50 Ω microstrip line on a two-layer FR-4 board, solved for its **capacitance** by
Palace's electrostatic solver in about fifteen seconds. Each answer is checked against a number you can work out
by hand: the pad against a parallel-plate capacitor, and the line against the line calculator.
The note **AN-03 Extracting capacitance** (**Help ▸ circuitRF Documentation ▸ Application Notes**) says when
this is the right tool and when it is not. **Test Pad Capacitance**, the next example, uses the same method on
a multilayer board with no nets.

## First: Simulate will ask you to install Palace

Opening this workspace needs no solver. The first time you press **Simulate** on a setup without Palace, the
run is refused with **Install Palace …**. That is the expected first step. Everything below can be looked at
before anything is installed.

## The board

`Pad and Line/3d/Pad and Line.c3d` is drawn in the 3D editor from seven variables (the **Variables** panel,
the *x* button):

| Variable | Value | What it is |
|---|---|---|
| `h` | 0.508 mm | FR-4, εr 4.4 |
| `t` | 0.035 mm | the copper |
| `w` | 0.92 mm | the line's width: 50 Ω on this laminate, from the line calculator |
| `len` | 20 mm | the line's length, and the board's |
| `brd` | 16 mm | the board's width |
| `a` | 2 mm | the pad's side |
| `gap` | 0.5 mm | copper to copper, line to pad |

The objects are `ground` (the bottom copper), `substrate`, `line` and `pad`. None of them carries a net.

**The two walls at the line's ends are zero-charge walls** (PMC in the air-box face menu). No field line ends
on one, so the line carries on through it as if it were infinitely long. That is what lets the answer be
divided by the length and read as a capacitance per metre, with no end effect in it. The other walls are 2 mm
of air beyond the board and zero-charge too. The ground plane's own bottom face is the box's floor.

## How the setups were made

Both setups are in **Simulate ▸ Setup Analyses…**. To make the first one yourself:

1. **Add**, then choose **Electrostatic** and **Palace**.
2. In the **Terminals** table press **+**, name the row `pad`, press its **Pick** button and click the pad in
   the 3D view. The row fills with the pad's piece of metal, and the pad is selected in the view.
3. **+** again, name it `line`, **Pick**, click the line.
4. Open the **Ground** list (▾). It lists every piece of metal not in a terminal. Choose `ground`.

The second setup, **Pad, line floating**, has the `pad` row only and the same ground. The line is in no
terminal and is not ground, so it **floats**: **Other metal** is left at *Float*, its default.

## Capacitance Extraction: the numbers

Measured once on an Apple M4 with 10 cores and 16 GB, macOS 27.0, Palace 0.18.1 on 10 processes and Gmsh
4.15.2, at the **Standard** preset. A time is the whole run's wall clock. A memory figure is Palace's own peak.

**Pad and line** took **14 s**, with a peak of **2.6 GB**. The result
(`results/Pad and Line Pad and line.palace_es.npy`) holds two matrices:

| | `C` (Maxwell) | | `C_mutual` (circuit) | |
|---|---|---|---|---|
| | **pad** | **line** | **pad** | **line** |
| **pad** | 532 fF | −31 fF | 501 fF | 31 fF |
| **line** | −31 fF | 2,460 fF | 31 fF | 2,429 fF |

`C` is the **Maxwell** matrix: the diagonal is a terminal's capacitance with every other terminal grounded, and
the off-diagonal entries are negative. `C_mutual` is what you would draw in a schematic: the diagonal is each
terminal's capacitor to ground, the row sum of `C`, and the off-diagonal is the capacitor between the two.
So the pad has **532 fF** to everything, made of 501 fF to ground and **31 fF** to the line.

**`Pad and Line.cdd`**, beside this file, shows both matrices side by side once **Pad and line** has run: the
columns headed *Maxwell C* are `C`, and those headed *Circuit C* are `C_mutual`.

### The pad against εA/d

A parallel-plate capacitor with the pad's area over the laminate gives ε₀·4.4·(2 mm)²/0.508 mm = **307 fF**.
The solve gives 532 fF with the line grounded. The parallel-plate formula therefore misses **42 %** of the pad's
capacitance. That share is the fringing field: from the pad's edges, from its top surface through the air, and to
the line. On a small pad over a thick laminate, fringing is not a correction. It is nearly half the answer.

### The line against the line calculator

The line's capacitance to ground is **2,429 fF** over 20 mm, which is **121.5 pF/m**. A lossless line's
capacitance per metre is C′ = √εeff/(c·Z₀). The line calculator (`circuitrf impedance --tech
tech/two-layer-fr4.ctech --layer "Top Copper (1 oz)" --width 0.92mm`, or the parameter dialog of an MLIN) gives
Z₀ 49.95 Ω and εeff 3.327 in its circuit-model column. That is **121.8 pF/m**, 0.3 % from the solve. Two
different methods agree on a number every RF designer already trusts, so the setup is right.

### The gap sweep

The coupling between pad and line falls quickly as the gap opens. Set `gap` in the Variables panel and press
**Simulate** again; the 3D view reshapes and the run re-meshes.

| `gap` | pad to line, `C_mutual` |
|---|---|
| 0.25 mm | 57 fF |
| 0.5 mm | 31 fF |
| 1 mm | 13 fF |

Each point took 14 s. A 3D setup has no sweep of a variable that changes the geometry, so the three
points are three runs. Each run writes the same result file, so copy it aside if you want to keep one.

### Floating versus ground

**Pad, line floating** gives the pad **531.48 fF**, against 531.87 fF with the line grounded. A floating conductor
carries no net charge, and removing it from the matrix is exact: C_pad − C_pl²/C_line = 531.87 − 31.00²/2,460.11 =
531.48 fF, the floating run's own number. Here it hardly matters, because the line takes only 31 fF of the pad's field. Where floating metal sits
between a conductor and its ground, it matters a great deal: **Test Pad Capacitance** shows a test pad reading
less than half its capacitance because its ground planes were left floating.

## The field

The 3D view's **Field Plots** group holds **E through the pad**: |E| for the pad's solution, on a clip plane
across the board through the pad's centre. Run **Pad and line**, tick the plot, and drag its position slider in
the Properties Inspector to move the cut along the line. The field is strongest in the laminate under the pad and
in the gap, and the fringing that the parallel-plate formula misses can be seen curling round the pad's edges.

## What is deliberately not here

**Results.** Run the setups yourself; each takes about fifteen seconds. The answers depend on the solver
version, and a committed result would go stale without anyone noticing.

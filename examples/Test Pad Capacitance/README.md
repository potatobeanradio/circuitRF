# Test Pad Capacitance

A test pad on the far side of a six-layer board, reached from a 50 Ω line on top through a via. The via keeps
an unused pad in every inner plane it passes, as board CAD does unless told otherwise. How much does the test
pad load the line, and how much does removing those pads save? Palace's electrostatic solver answers in under a
minute per board. A driven 3D run of the same board then shows where that answer holds.

The board has **no nets**, like a board imported from Gerber files. Every terminal is chosen by clicking the
metal, and the ground by choosing **Every body not in a terminal**. **Capacitance Extraction**, the example before
this one, introduces the method on a simpler board. The note **AN-03 Extracting capacitance** (**Help ▸ circuitRF
Documentation ▸ Application Notes**) explains it.

## First: Simulate will ask you to install Palace

Opening this workspace needs no solver. The first time you press **Simulate** on a setup without Palace, the
run is refused with **Install Palace …**. That is the expected first step.

## The board

12 × 8 mm of the technology `tech/six-layer-fr4.ctech`: FR-4 (εr 4.4) in five layers, 1.6 mm in all. **No
layer is marked as a ground reference**, and no piece of copper carries a net.

| Layer | What is on it |
|---|---|
| Top Copper | the 0.38 mm line, 50 Ω over Inner 1, end to end; a 0.6 mm pad at the via and at each stitching via |
| Inner 1 – Inner 4 | a ground plane each, with a 1.0 mm opening round the via; in **As built**, a 0.6 mm unused pad on the via inside each opening |
| Bottom Copper | the 1.5 mm test pad on the via, and a ground pour with a 2.5 mm opening round it |

A 0.3 mm via joins the line to the test pad, and eight stitching vias join the planes and the pour.

| Cell | What is different |
|---|---|
| **As built** | as above |
| **Fixed** | the four unused pads removed, and Inner 4's opening under the test pad enlarged to 2.5 mm |
| **Line only** | the via, its pads and the test pad cut away: the line by itself |
| **Compare** | a schematic of the line with each answer on it, and the driven run |

Each board cell holds its layout and a 3D view of it (**New 3D View from Layout**), and the setups are in the
3D view's **Simulate ▸ Setup Analyses…**.

## How the setup was made

1. **Add**, then **Electrostatic** and **Palace**.
2. In **Terminals** press **+**, name the row `TP`, press **Pick**, and click the test pad on the bottom of the
   board. The row takes the whole piece of metal the pad is part of: the pad, the via, the unused pads, the top
   pad and the line. They touch, so they are one conductor. The 3D view selects all of it.
3. Open the **Ground** list (▾) and choose **Every body not in a terminal**. The planes, the pour and the
   stitching vias are ground, and **Other metal** reads *Ground*.

**Test pad C, planes floating** in As built is the same setup with step 3 left out.

## Test Pad Capacitance: the numbers

Measured once on an Apple M4 with 10 cores and 16 GB, macOS 27.0, Palace 0.18.1 on 10 processes and Gmsh
4.15.2. A time is the whole run's wall clock and a memory figure is Palace's own peak. The electrostatic setups
run at **Standard**.

| Setup | Test-pad net | Time | Peak |
|---|---|---|---|
| As built ▸ Test pad C | **2.140 pF** | 44 s | 5.3 GB |
| Fixed ▸ Test pad C | **1.830 pF** | 48 s | 6.0 GB |
| Line only ▸ Line C | **1.450 pF** | 40 s | 5.9 GB |

The terminal is the whole net, line included. **The test pad's own share** is the difference from the line alone:

- **As built**: **0.689 pF** more than the line alone.
- **Fixed**: **0.379 pF**, 45 % less.

**What a designer does with the number.** Put it on the line in the schematic as a shunt capacitor, at the via,
and see what it costs at the operating frequency. At 2.4 GHz the as-built pad costs a few tenths of a dB and
returns a reflection a matching network has to absorb. If that matters, removing the unused pads and opening the
plane under the pad are two fabrication notes, and this is how much they buy.

## Ground, or floating

**As built ▸ Test pad C, planes floating** took 56 s and reads **0.920 pF**: less than half. With the ground box left
as it opens, the planes are in no terminal and are not ground, so they **float**: connected to nothing and with no
net charge. The only ground left is the metal box round the model, and the floating planes sit between it and the
pad. On a real board the planes are ground, so on a board with no nets, choose **Every body not in a terminal**.

## Compare

`Compare/schematic/Compare.csch` holds five copies of the 12 mm line, each from its own pair of ports. The line
is the line calculator's: Z₀ 50 Ω, εeff 3.33, and its loss at 2.4 GHz. The line's middle carries:

| Ports | On the line |
|---|---|
| 1, 2 | `C_built`, the as-built test pad from the electrostatic solve |
| 3, 4 | `C_fixed`, the fixed one |
| 5, 6 | TESTPAD, the closed-form model of the as-built pad, its via and its unused pads |
| 7, 8 | the driven 3D run of the as-built board, `Compare/data/As built driven.s2p` |
| 9, 10 | `C_built` behind `L_via`, the via inductance TESTPAD reports (0.526 nH) |

Simulate it and open `Compare.cdd` beside this file. It plots |S21| of all five.

**At 2.4 GHz** the electrostatic shunt C gives **−0.44 dB**, and the driven run **−0.69 dB**. Below about half the
stub's resonance the electrostatic number stands in for the driven run. Above that the via's inductance takes
over: the driven run dips to −29.6 dB at 7.4 GHz, where the stub resonates, and no capacitor can do that.
Ports 9 and 10 put the inductance back, and the dip returns at 8.4 GHz, a little higher because TESTPAD's inductance
is an estimate.

**The driven run** is `As built ▸ Driven (Draft)`: Palace at **Draft** with two refinement passes (first-order
elements), 0.5–10 GHz, lumped ports from the line's ends down to Inner 1. It took **7.3 min**, with a peak of
**7.8 GB**. Its file in `Compare/data` is that run's result, kept so Compare works without it. A new run writes to
`results/`, not there. **What Draft trades away:** at first order the mesh round a small pad and via is coarse,
and a coarse mesh overstates a small capacitor's loading. Draft with no refinement loaded the line about as much as
twice the electrostatic stub below 3 GHz. The two refinement passes close most of that gap, and take three to four times
as long. A
second-order driven run was not made, so how far this one still is from a converged answer has not been measured.

## What is deliberately not here

**Results**, other than the driven run's file in `Compare/data`. Run the setups yourself.

# Thermal: Output Wires

How hot do a power die's output bond wires run? Six 1 mil (25.4 µm) gold wires, wedge-bonded from the drain pad on a SiC die to
a copper package lead, carry the device's current. **Everything above the flange is embedded in a mould compound — the
overmold** — so the wires lose their heat into the mould, and through it to the flange, which is held at `Tcase` = 85 °C. The
wires are solved as one-dimensional conductors along their true 3D paths, with gold's electrical and thermal conductivity
following the temperature; the rest of the package is solved in 3D.

The chapter that teaches this is **Thermal** in **Help ▸ circuitRF Documentation**. It quotes the same numbers as this file.

**What to expect when you press Run** (an Apple M4, the Release build): `DcSweep` about **24 s**, `RfHarmonics` about **7 s**,
`FromHB` about **12 s** once the EM result exists, on a mesh of 123,416 tetrahedra. The EM setup takes **1 min 28 s** in Palace
and **5.8 GB**. Thermal needs **Gmsh**, and the EM setup **Palace**; the first run without either offers to install it.

## What is here

| Cell | View | What it is |
|---|---|---|
| **Pads** | layout, µm | The die: 100 µm of SiC on a 25 µm gold-tin die attach, with a 10 µm gold drain pad along its edge, and the lead finger (10 µm gold) — plus **`Pads.wBond`**, the six wires, 150 µm apart |
| **Die** | layout, µm | The same artwork without the wires, for *Drawn Wires* |
| **Output** | 3D view, µm | The die placed as `U1` on a copper–molybdenum flange; a copper lead on an alumina standoff under the finger; the mould over all of it; two ports, one probe per wire; the setups `EM`, `DcSweep`, `RfHarmonics` and `FromHB` |
| **Drawn Wires** | 3D view, µm | The same model with the six wires drawn in the 3D view instead of read from `Pads.wBond`; the setup `DcSweep` |
| **Amplifier** | schematic (test bench) | `Amplifier.csch`: the harmonic-balance drive sweep `FromHB` takes its currents from |
| **Amplifier Wires** | schematic (test bench) | The same drive with a wBond carrying `Pads.wBond`'s wires in place of the EM block: the wire temperature with no 3D run |

Every material is one of circuitRF's generic materials, from `tech/generic-materials.cmat`. The mould's glass transition is taken
as 150 °C, and every wire probe's *Limit* is set to it, so a run says when and where a wire first passes it.

## How it is modelled

- **The overmold.** The box `mould` (generic mould compound, 0.9 W/(m·K)) fills everything from the flange up to 600 µm. The
  wires run through it, and each wire element loses heat into the mould around it.
- **The wires** are the six wires of `Pads.wBond`, reached through the layout. Each is a chain of second-order line elements
  along its 3D path (feet included), its section πd²/4, its feet bonded perfectly to their pads — the pad's surface under a
  foot is one temperature and one potential with the wire's heel.
- **The pad and the lead are one potential each.** Their resistance at 20 °C is 0.202 mΩ (the drain pad) and 0.047 mΩ (the lead
  with its finger), against 46.8 mΩ for a wire, so the run solves no potential inside them and says so in its notes (below 1 %
  of a wire, *Balance ▸ EquipotentialBelow*).
- **The flange's bottom** is held at `Tcase`. Every other outer face is insulated.

## DcSweep: a DC current up to runaway

Port 1's current enters through the lead finger's outer edge and leaves through the drain pad's inner edge; `Idc` sweeps 6 → 18 A
for the six wires together.

| Idc | Hottest wire | An edge wire |
|---|---|---|
| 6 A | 119.3 °C | 116.5 °C |
| 10 A | 195.4 °C | 186.9 °C |
| 14 A | 385.6 °C | 364.4 °C |
| 18 A | runaway | runaway |

At DC the six wires share the current almost equally, and the centre ones run hottest, because the mould between their neighbours
is warmer. Every wire passes the 150 °C limit by 10 A.

**Runaway is an answer, not a failure.** Above about 16.79 A there is no steady state: gold's resistance rises with temperature,
the heat rises with it, and past that current nothing balances. The run brackets it and reports it — *"No steady state above
about 16.79 A: thermal runaway. The last converged point is 16.73 A (wire U1/wire/Out/3 at 944 °C)."* The 18 A point has no
temperature. (The gold table ends at 1,027 °C, just below melting; a state hotter than a table states is not taken as a steady
state.)

**What the two switches contribute** (the setup's *Balance*), at 10 A: with both on the hottest wire reads **195.4 °C**; with
k(T) off, **192.9 °C**; with σ(T) off, **157.7 °C**. Almost all of the temperature-dependence here is gold's resistance, not the
conductivities.

## RfHarmonics: RF current at 2 GHz

`Idc` = 4 A with a fundamental `I1` at 2 GHz stated as a **Peak** current, swept 0 → 12 A, and a second harmonic of 1 A stated as
**RMS**. At RF the current does not share equally: it divides by the wires' inductance, and **the two edge wires carry the
most**. At 8 A peak the edge wires reach **265.8 °C** and the centre ones **198.4 °C**; at 12 A, **576.6 °C** against **363.2 °C**.

## FromHB: the currents of a power amplifier

The **Amplifier** test bench is a generic GaN-like FET (circuitRF's Angelov model) biased in class AB, its drain on this view's EM result (the `SnP` block `XOUT`), fed
from 40 V through a choke on the lead side, into a 5 Ω load. **It exists to drive currents through the wires, not to be a good
amplifier:** nothing is matched. Its harmonic-balance sweep takes `Pin` from 10 to 28 dBm, and at every drive level the thermal
run takes each port's DC and harmonic currents (pin p is port p). Both ports are referenced to the flange, across the die and the
standoff, so the run solves them as a balanced pair.

**Run `EM` first.** `FromHB` reads the S-parameters `EM` writes; without them it refuses and says so.

- At `Pin` = 28 dBm the circuit gives **49.94 dBm** from **4.63 A**, and the edge wires reach **216.9 °C** (the centre ones
  **173.5 °C**).
- The limit sentence: *"Probe 'w1' reaches its limit of 150 °C at Pin ≈ 13.02 (Pout_dBm ≈ 47.22, Id_A ≈ 3.274, Eff ≈ 40.6)."*

One way only: the circuit saw the wires at the temperature their S-parameters were solved at; the thermal run reports what they
actually reach. The DC resistance of the EM block is its lowest frequency's (0.1 GHz), so the drain sits a few volts below the
lead at DC; the DC **current** through the wires, which is what heats them, is the circuit's own.

## Amplifier Wires: the quick check, with no 3D run

The **Amplifier Wires** test bench is the `Amplifier` bench's drive — the same FET, bias, choke, load and `Pin` sweep 10 → 28 dBm
— with a **wBond component carrying the six wires of `Pads.wBond`** where `Amplifier` has the EM block. Its temperature is
**solved** (the checkbox on its `Temp` row is clear), between 125 °C at the die end and 85 °C at the lead end, and every point of
the sweep reports **`WireTemp`**: the hottest wire, at its hottest point. It needs no EM run and no Gmsh: the whole sweep takes
**0.4 s**.

| Pin | WireTemp |
|---|---|
| 10 dBm | 163.9 °C |
| 20 dBm | 446.1 °C |
| 28 dBm | 486.5 °C |

Against `FromHB`'s 216.9 °C at 28 dBm, **the quick check runs hotter because no heat leaves through a wire's sides**: it has no
mould, so every watt flows along the wire to its ends, and it is an upper bound. The circuit is not quite `Amplifier`'s either —
its output network is the wires alone, over the flange as their ground plane, not the EM block of the whole package — so at 28
dBm it gives **51.37 dBm** from **5.41 A**. The component's `GroundPlane` is on: `Pads.wBond` stores it off, because the 3D EM
setup has its own flange, and a placed wBond needs a return path.

## Drawn Wires: one wire model, two ways to draw it

The same DcSweep with the wires drawn in the 3D view instead of read from `Pads.wBond`: the hottest wire at 14 A reads 385.6 °C
and the runaway is the same, to every printed digit. The two are one thermal problem.

## Checks that are not circuitRF's

**One isolated wire (brief 72's W1, a closed form).** A single wire of this span in AIR, its ends held at the heels' solved
temperature (98.8 °C), gold's resistance linear in temperature and its conductivity constant, carrying 2.31 A: its centre reaches
**602.3 °C**, and its own runaway is at **3.21 A**. The model's wire at the same current reads 385.6 °C: the overmold takes away
most of the rise. The two runaways are not the same limit — the closed form's is where a linear resistance goes infinite, the
model's a fold of gold's measured resistance just below melting.

**Would an outer EM loop change the RF answer?** The run shares each harmonic by the wires' inductance. Recomputing the share with
each wire's AC resistance at its solved temperature added moves any wire's current by at most **0.80 %** at 8 A peak: the wires'
reactance is fifty times their resistance.

## See it

`Output` carries four field plots — a section along wire 4 at 14 A DC (drawn when you open the results), across the six wires at DC
and at RF, and in plan through the loops at RF. Tick one in the tree to draw it. Each wire is coloured from its own solved
temperature along its length, over the mould around it.

## The settings chosen for speed, and what they cost

**The EM setup at the Draft preset.** *Standard* took **7 min 59 s** and **8.8 GB**, and read the wires' series inductance at
2 GHz as **231.2 pH** against Draft's **226.6 pH**; driven from it, `FromHB`'s edge wire at 28 dBm reads **213.3 °C** and `w1`
reaches its limit at `Pin` ≈ **13.06**. Draft is the setting for a run you repeat; Standard for the number you quote.

**The pad and the lead as one potential each.** With *Balance ▸ EquipotentialBelow* = 0 every conductor's potential is solved in
3D: the DcSweep takes **47 s** instead of 24 s, and the hottest wire at 14 A reads **386.0 °C** against 385.6 °C.

**Element order 1** and one element through thin solids (`MinThroughThickness` 1). The 10 µm gold pads set the element size where
they are; order 2 multiplies the unknowns by about eight.

## Things to try

- Change `Tcase`, or the sweep on a setup's page.
- Move the wires closer together in `Pads.wBond` (the *wBond* window) and run `RfHarmonics`: the edge wires' share grows.
- Right-click the lead's top face ▸ **Plot Temperature** after a run.

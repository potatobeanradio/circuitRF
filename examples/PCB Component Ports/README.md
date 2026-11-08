# PCB Component Ports

A 50 Ω microstrip line on a two-layer board with a three-part pi low-pass filter on it: a shunt capacitor, a series
inductor and a second shunt capacitor. The board is solved once with each part as a **component port**: a port across the
part's own pads that you never draw. From then on the parts live in a schematic. Change a value and the response
changes in a fraction of a second, with no new EM run.

The board is solved three ways, on the Planar MoM solver, on Palace and on EMerge, and a test bench lays the three answers
over the schematic alone.

## The cells

| Cell | Views | What it is |
|---|---|---|
| **Filter Board** | schematic, symbol, layout, 3D view, EM setup | The board. Its parts take their values from two cell parameters, `Cshunt` (1.5 pF) and `Lseries` (8.2 nH), and each has a 0603 footprint |
| **Bench** | schematic | The test bench: Filter Board as a circuit, and the three EM results, each closed by the same three parts |

## How the board was built

These are the steps to copy when you build your own.

1. **Schematic first.** Filter Board's schematic is the circuit: two 100 mil MLIN lines, `C1` and `C2` to ground and `L1` in
   series, with `IN` and `OUT` pins. Each part's `Footprint` is `smt:0603`.
2. **Design ▸ Update Layout from Schematic.** It places the lines and the land patterns, drops a ground via beside each
   capacitor's ground pad, and pours the ground plane on Bottom Copper under them. `C2` was then moved to the same side of the
   line as `C1`, so the two ground vias sit 2.4 mm apart. The Planar solver needs vias within a tenth of a wavelength of each
   other at the top of the sweep, which is 5 mm at 6 GHz. A top-side ground bar joins the two via pads.
3. **Ports where the signal enters and leaves.** `P1` and `P2` are ordinary edge ports drawn at the two line ends. Nothing is
   drawn for the three parts.
4. **LVS.** `circuitrf lvs "Filter Board"` matches. Each board pin sits on a short launch drawn over its line end, so the pin
   has copper of its own.
5. **The EM setup, `Filter Board Planar`** (`Filter Board/em`). Under **Component ports**, `C1`, `L1` and `C2` are ticked.
   **Check** builds each one before anything is meshed and posts its verdict to Messages. With the setup open, the layout
   shows each component port as a dashed gap mark with its number: 3, 4 and 5, after the two drawn ports.
6. **Design ▸ New 3D View from Layout.** The 3D view places the board's layout and brings its component ports with it, as
   a 3D setup. That setup was copied once more, so the view has two: **Palace** and **EMerge**.

## Run it

| Step | What | Time on a 10-core Mac |
|---|---|---|
| 1 | Simulate the EM setup **Filter Board Planar** | about 2.5 min |
| 2 | Open Filter Board's 3D view, run setup **Palace** | about 3.5 min |
| 3 | Run setup **EMerge** in the same view | about 10 s |
| 4 | Simulate **Bench**, then open `Bench.cdd` | under a second |

The first 3D run offers to install its solver. Each run also makes a cell named after it, such as
`Filter Board Planar_cosim`: the board's result with each part across its own port. It is ready to simulate, so you can
look at one solver's answer before the others have run. The Bench reads all three results, so run all three before
simulating it.

## What you will see

`Bench.cdd` plots |S21| four ways:

| | Schematic only | Planar MoM | Palace | EMerge |
|---|---|---|---|---|
| −3 dB | 1.98 GHz | 1.67 GHz | 1.70 GHz | 1.70 GHz |
| Deepest point | none below 6 GHz | 3.75 GHz, −25.3 dB | 3.75 GHz, −22.5 dB | 3.8 GHz, −22.3 dB |
| \|S21\| at 2 GHz | −3.1 dB | −7.0 dB | −6.3 dB | −6.4 dB |
| \|S21\| at 6 GHz | −28.1 dB | −17.2 dB | −14.8 dB | −14.7 dB |

**The schematic alone gets this filter wrong.** It knows nothing about the board. The pads add capacitance, so the cutoff
falls from 1.98 to 1.70 GHz. Each shunt capacitor reaches ground through its pad, its via and the ground bar, and that path's
inductance resonates with the capacitor at about 3.8 GHz. That is the notch, and above it the stopband rises again where the
schematic says it keeps falling. All three EM solvers show it.

**Palace and EMerge agree within 0.4 dB** at every frequency.

**Planar MoM follows them** to within 0.7 dB up to the cutoff and reads 2 to 3 dB deeper in the stopband. Most of that
difference sits at the two edge ports, which the two kinds of solver model differently. Planar removes its edge port's own
effect back to the end of the line. In 3D, the port is a lumped sheet from the line down to the ground plane, and its height
stays in the answer. Treat Planar and 3D as two models of the board, not as one checking the other.

## Things to try

- **Change a part.** Edit `Cshunt` or `Lseries` in the Bench's VAR block and simulate again. All four curves move, and no EM
  run is needed. Tuning and the Optimizer work on these variables the same way.
- **Make it an attenuator.** Replace the three parts on an EM result with resistors, 292 Ω, 17.6 Ω and 292 Ω for a 3 dB pi
  pad. The board's EM result is the same one, because the parts are not in it.
- **Turn a part around.** A component port follows its part. Move or rotate `L1` in the layout, and the next run, Check or
  mark uses its new place. The setup is not edited.

## What was traded for speed

- **Planar: Edge mesh off.** With it on, the mesh has about five times the unknowns and the run takes several times as long.
  On this board it moves |S| by at most 0.015.
- **Palace: one adaptive pass** (`AdaptiveMaxIterations` 0, inside the setup's Palace settings). The default three passes
  took over 30 minutes.
- **The air box is stated: 5 mm on the top and sides** of both 3D setups, about 10 times the substrate height. The default
  puts the top face one board-thickness above the copper, inside the line's fringing field, where it absorbs power that is
  not really lost. With it, Palace read |S11| −0.84 dB at 6 GHz where the 5 mm box reads −0.29 dB.
- **The parts are ideal**, and their bodies are not in any of the EM models.

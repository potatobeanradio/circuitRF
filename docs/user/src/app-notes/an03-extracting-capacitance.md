---
title: AN-03 — Extracting capacitance
slug: app-notes/an03-extracting-capacitance.html
doc-kind: Application Note
breadcrumb: Docs > App Notes > AN-03 Extracting capacitance
lede: When a small piece of metal on a line is a capacitor, an electrostatic solve gets that capacitance directly, in seconds, with no ports and no de-embedding. When to use it and when not to, which metal is ground and which floats, the Maxwell matrix against the circuit one, and the answer in a schematic, on the two shipped capacitance examples.
keywords: capacitance, capacitance extraction, electrostatic, Maxwell matrix, mutual capacitance, C_mutual, floating conductor, ground, test pad, test point, via stub, unused pads, antipad, Palace, terminal, picker, body, net-less board, Gerber, clip plane, shunt capacitor, application note
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#when">When a capacitance is the answer</a></li>
<li><a href="#examples">The two examples</a></li>
<li><a href="#terminals">Naming terminals on a board with no nets</a></li>
<li><a href="#ground">Ground or floating</a></li>
<li><a href="#maxwell">The Maxwell matrix and the circuit matrix</a></li>
<li><a href="#checks">Checking the answer by hand</a></li>
<li><a href="#field">Looking at the field</a></li>
<li><a href="#schematic">Putting the answer in a schematic</a></li>
<li><a href="#driven">The cross-check against a driven run</a></li>
<li><a href="#checklist">Checklist</a></li>
</ol>
</nav>

## When a capacitance is the answer {#when}

A test pad, a via stub, a connector pin, the pad under a part, the coupling between two nets: each is a small
piece of metal hanging on a line. While it is small against the wavelength, its effect on the circuit **is**
its capacitance. A rule of thumb is a tenth of a wavelength in the dielectric: a 1 mm pad on FR-4 is under
λ/10 up to about 14 GHz.

An **electrostatic** solve gets that capacitance directly. It is one solve per terminal, with no frequency
sweep, no ports and no de-embedding. It is fast, it settles quickly as the mesh is refined, and the answer is a
number you can put straight into a schematic as a shunt C.

A **driven** S-parameter solve gets the same physics indirectly. You fit C from Y₁₁/jω, or from how much S21
drops. That takes minutes to hours, and de-embedding the line around the pad brings an error of its own. A
driven solve is also only as good as its mesh, and a coarse one reads a small capacitor badly. The
[driven cross-check](#driven) below shows by how much.

**When it is the wrong tool:**

- It gives no **inductance**. A via stub is a capacitor behind an inductor, and above roughly half its
  resonance the inductor matters. A magnetostatic solve gives the inductance.
- It gives no **loss**, and no **frequency dependence**.
- Once the metal is a sizeable fraction of a wavelength it is a resonator, not a lumped C.
- **What counts as ground, and what floats, changes the answer.** [Ground or floating](#ground) shows how much.

The working pattern: cross-check once against a driven run, then use the cheap method for every design
iteration after that.

## The two examples {#examples}

**File ▸ New ▸ Examples** has both. Each `README.md` carries the same numbers as this note, and each workspace's
`expected-numbers.json` holds them for circuitRF's own tests. Every number here was measured on an Apple M4 with
10 cores and 16 GB, Palace 0.18.1 on 10 processes, at the **Standard** preset.

- **Capacitance Extraction**: a 2 mm square pad 0.5 mm from a 50 Ω microstrip line on 0.508 mm FR-4, drawn in
  the 3D editor. Each run takes about 15 s.
- **Test Pad Capacitance**: a six-layer board, 12 × 8 mm, with no nets. A 50 Ω line on top, a via down to a
  1.5 mm test pad on the bottom, an unused pad on the via in each of four inner ground planes, the planes'
  openings round the via, and eight stitching vias. Three boards (as built, with the pads removed and the
  opening under the test pad enlarged, and the line alone), and a schematic comparing them. Each electrostatic
  run takes under a minute.

## Naming terminals on a board with no nets {#terminals}

A board imported from Gerber files has no nets: every piece of copper is just a shape on a layer. An
electrostatic terminal is named by **picking the metal**, not by typing a net.

In the 3D view's **Simulate ▸ Setup Analyses…**, choose **Electrostatic** and **Palace**, then in the
**Terminals** table press **Add terminal** and the row's **Pick** button, and click the metal in the view. While
a Pick waits, the line under the table names the terminal it will fill, and says so when a click lands on
something that is not a conductor, such as the substrate. Pressing **Pick** again, or closing the setup window,
cancels it. The row takes the **whole piece of metal** the click touched. In Test Pad Capacitance one click on the bottom test pad takes
the pad, the via barrel, the four unused inner pads, the top via pad and the line, because they all touch: they
are one conductor, and they are at one potential. The **Conductor** box's ▾ lists every piece of metal, by its
net or, with none, by `body:` and its largest object, with its layers and object count. The
[setup reference](../reference/em-setup.html#package-rlc) has the rules for typing a name instead.

The **Ground** list offers the same pieces, and one more entry: **Every body not in a terminal**. On a board
with no nets that is usually the right ground. It takes every plane, pour and stitching via that is not in a
terminal.

## Ground or floating {#ground}

Metal that is in no terminal and is not the ground is decided by **Other metal**:

- *Float*, the default: the metal is connected to nothing, so it carries no net charge. circuitRF solves it as
  one more terminal and then removes it from the matrix. That is exact, not an approximation.
- *Ground*: the metal is ground. **Every body not in a terminal** sets this.
- *Refuse*: the run is refused, naming the metal.

**The choice can change the answer by more than a factor of two.** In Test Pad Capacitance, the test-pad net
reads **2.140 pF** with the planes grounded. Left at the default, the planes float, and the only ground left is
the metal box round the model. The same net then reads **0.920 pF**: the planes sit between the pad and its
ground and carry no charge. A board's planes are ground in the circuit, so they must be ground in the solve.

**Floating metal that shares little field barely matters.** In Capacitance Extraction the pad reads **532 fF**
with the line grounded and **531.48 fF** with the line floating. Removing the floating line from the matrix
gives C_pad − C_pl²/C_line = 531.87 − 31.00²/2,460.11 = 531.48 fF, the same number to the solve's last digit.

## The Maxwell matrix and the circuit matrix {#maxwell}

Every electrostatic result holds two matrices. In Capacitance Extraction, with the pad and the line as terminals
and the plane as ground:

| | `C`, Maxwell: pad | `C`, Maxwell: line | `C_mutual`, circuit: pad | `C_mutual`, circuit: line |
|---|---|---|---|---|
| **pad** | 532 fF | −31 fF | 501 fF | 31 fF |
| **line** | −31 fF | 2,460 fF | 31 fF | 2,429 fF |

`C` is the **Maxwell** matrix, which is what the solver reports. Its diagonal is a terminal's charge with that
terminal at 1 V and every other terminal at 0 V: its capacitance to **everything**. The off-diagonal entries are
negative: raising the line induces opposite charge on the pad.

A schematic wants **capacitors**: one from each terminal to ground, and one between each pair. The conversion is

- the capacitor between i and j is −C[i][j]: here **31 fF**;
- the capacitor from i to ground is the row sum of C: here 532 − 31 = 501 fF for the pad, and 2,460 − 31 =
  **2,429 fF** for the line.

That is `C_mutual`, which circuitRF also writes, so nothing needs to be converted by hand. Use C[i][i], the
Maxwell diagonal, only when everything else really is grounded. Use `C_mutual` when you draw the circuit.

## Checking the answer by hand {#checks}

Two checks in Capacitance Extraction need no solver at all.

**The pad against εA/d.** A parallel-plate capacitor with the pad's 2 × 2 mm area over 0.508 mm of εr 4.4
gives **307 fF**. The solve gives 532 fF, so the parallel-plate formula misses **42 %** of it: the field from
the pad's edges and its top surface. On a small pad over a thick laminate the fringing field is nearly half
the answer.

**The line against the line calculator.** A lossless line's capacitance per metre is C′ = √εeff/(c·Z₀). The
line calculator gives Z₀ 49.95 Ω and εeff 3.327 for the 0.92 mm line, so C′ is **121.8 pF/m**. The line's
capacitance to ground is 2,429 fF over 20 mm, or **121.5 pF/m**. The walls at the line's ends are zero-charge
walls, so the line behaves as if it carries on forever and no end effect is in the number.

**Coupling against distance.** Moving the pad away from the line, by changing the `gap` variable and running
again, gives a coupling of **57 fF** at 0.25 mm, 31 fF at 0.5 mm and **13 fF** at 1 mm. A 3D setup cannot sweep a
variable that moves geometry, so each point is its own run.

## Looking at the field {#field}

Both examples ship a field plot on a clip plane: **E through the pad** in Capacitance Extraction and **E through
the test pad** in Test Pad Capacitance. After a run, tick it in the Object Tree's **Field Plots** group. Its
**Solution** is a terminal: the field with that terminal at 1 V. To move the cut after the solve, select the plot
and drag the slider beside its position in the Properties Inspector. Nothing is solved again: the plane is
cut through the saved field. The field is strongest where the capacitance is: under a pad, in a gap, and in a
test pad's case in the thin laminate between the pad and the nearest plane.

## Putting the answer in a schematic {#schematic}

A capacitance from a net-wide terminal includes the line's own capacitance, which the line's model in the
schematic already has. So **subtract the net with the extra metal cut away**. In Test Pad Capacitance:

| Board | Test-pad net |
|---|---|
| As built | 2.140 pF |
| Pads removed, opening enlarged | 1.830 pF |
| The line alone | 1.450 pF |

The test pad adds **0.689 pF** as built and **0.379 pF** fixed. Each is a shunt capacitor from the line's midpoint
to ground. The **Compare** cell's schematic does exactly that, in its variables `C_built` and `C_fixed`.

## The cross-check against a driven run {#driven}

Compare's schematic has five copies of the 12 mm line, each with ports at its ends:

1. the line with a shunt `C_built`;
2. with a shunt `C_fixed`;
3. with the TESTPAD model of the as-built pad, its closed-form estimate;
4. the driven Palace run of the as-built board, as a Touchstone file;
5. `C_built` behind the via's inductance, `L_via`, from the TESTPAD estimate.

Its Data Display plots |S21| of all five. At 2.4 GHz, the shunt C from the electrostatic solve gives
**−0.44 dB** and the driven run **−0.69 dB**. Below about half the stub's resonance the electrostatic number
reproduces the driven one. Higher up, the via's inductance takes over and the driven run dips at the
stub's resonance, at 7.4 GHz, which a capacitor alone cannot do. Trace 5 shows the dip coming back when the inductance is
added.

The driven run is the expensive half. It took 7.3 min at **Draft** with two refinement passes, and its mesh is
first order. A driven solve at that setting still overstates a small capacitor's loading, so of the two
the electrostatic number, at Standard, is the one to trust. Run the driven solve once, to see that the two
agree where they should, and then iterate on the electrostatic one.

## Checklist {#checklist}

- Is the metal small against the wavelength? If not, solve it driven.
- Pick the terminal by clicking the metal, and check the row took the whole piece you meant.
- On a board with no nets, set the ground to **Every body not in a terminal**. Leaving the planes floating can
  more than halve the answer.
- Read `C_mutual` for a schematic, and `C` only when everything else is at 0 V.
- Subtract the net without the extra metal, so the line is not counted twice.
- Check once against a closed form or a driven run, then trust the cheap method.

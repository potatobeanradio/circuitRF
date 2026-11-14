---
title: Co-simulation: B1, SAR and the radiation pattern from a schematic
slug: reference/cosimulation.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Co-simulation
lede: Parts as ports. One EM run keeps each port's fields, and a schematic's fields analysis turns the waves its circuit delivers into B1, SAR and the radiation pattern — so tuning, optimizing and yield on fields start no EM run.
keywords: co-simulation, cosimulation, radiation pattern, far field, _cosim, gap port, part values, EM Field Data, EmFieldData, result record, cemr, port fields, per-port fields, PerPortFields, field set, superposition, recombination, fields analysis, type=fields, MRI coil, RF coil, tuning capacitor, matching network, capacitors as ports, B1pMean, B1pMin, B1pCoV, psSAR, SARwb, SarEff, Pabs, goal, tuning, optimizer, yield, multi-channel, RF shimming, parallel transmit, drive weights, quadrature, birdcage, norm=available, norm=accepted, research use
---

An MRI coil is a resonant conductor with many tuning, matching and decoupling capacitors next to lossy tissue. Solving
it with the capacitors inside the EM model means a new EM run for every capacitor value, and a high-Q coil rings for a
long time in FDTD. The method coil designers use instead is **co-simulation**: every capacitor gap and every feed is a
**port**, the EM run solves the bare conductors once and keeps each port's fields, and a circuit puts the capacitors
back. circuitRF does the second half in a schematic, so B1 and SAR follow every value in it — tuned, optimized or drawn
for a yield trial — in about a second and with no EM run.

<div class="callout note">
<span class="label">Research and engineering use</span>
<p>circuitRF's SAR is for research and engineering. It is not a compliance tool.</p>
</div>

The worked example is **SAR: MRI Loop Coil** (*File ▸ New ▸ Examples*): a 7 T loop with four ports, matched and optimized
against S11, B1 and psSAR goals in 4 s. What B1 and SAR are, and their units, is [SAR and B1](sar.html). An antenna board with parts on it works the same way: see
[The radiation pattern](#pattern), and the **Antenna Co-Simulation** example.

## 1. The EM side: ports for capacitors, fields kept {#em}

In the coil's 3D view:

1. Put a **lumped port** across every gap a capacitor will bridge, and one at each feed. Give them all the **same Z0**
   (50 Ω): the Touchstone records one reference impedance, so a block whose ports differ would be read in the wrong one.
2. In the openEMS setup's **SAR and B1** group, set the frequency, the masses and B1 as usual, and tick **Keep per-port
   fields** (`PerPortFields`).
3. **Simulate.** openEMS runs once per port, as any driven run does. Beside the `.sNp` the run writes a result record,
   `<stem>.cemr`, and a **field set**, a folder `<stem>.fields/`: each port's E and H over the tissue's box, per √W incident at that port and nothing
   incident anywhere else, with the cells' σ, ρ and volume and a manifest. They are float32, 24 bytes a cell per port and
   field; the loop coil's four ports at 298 MHz take 87 MB on the example's grid. The disk estimate before the run says how
   much of it the field set is.

**Without capacitors there is no resonance to ring down.** The example's four runs stop after about 3,700–3,900 steps
each; the same coil with lumped capacitors in the EM model needed 72,000 steps for one run, and it was still ringing.

## 2. The schematic side: the fields analysis {#schematic}

Place an **EM Field Data** block in a test bench and pick the run's `.cemr` for its **File**. Its pins are named after the
run's ports, in the run's order, so there is no port count to set. Draw the circuit around them: the capacitors, the match,
the feed. Then add a **Fields (SAR, B1, pattern)** analysis in the Analysis editor. Its **Block** control lists the schematic's EM
Field Data blocks, those inside sub-cells included, starting on the first and greyed when there is only one (an analysis reads one block, so a second block takes a second analysis), and its **Frequency** control lists what the block's record stores, with
**All** when it holds a far field. Frequency still takes typed text. **Per watt** and **Averaged SAR** are grey when the record holds no field set, since
they act only on SAR and B1:

<pre><code>analysis F1 type=fields block=EM1 freq=298 freqUnit=MHz drive=1 [norm=accepted|available] [average=yes|no]</code></pre>

| Key | | |
|---|---|---|
| `block` | the EM Field Data block's instance name (`X1.EM1` inside a cell) | required |
| `freq`, `freqUnit` | a frequency the record stores, or `all` for every frequency of its far field | required |
| `drive` | the circuit port that drives, by number or by instance name; every other circuit port is terminated in its Z0 | required |
| `weights` | with several drives, each one's relative complex amplitude, with no units ([multi-channel](#multichannel)) | 1 each, or a P1Tone's Pavl and Phase |
| `norm` | per watt **accepted** at the drive, or per watt **available** from it | accepted |
| `average` | `no` skips the cube-averaged SAR, the step that costs the most | yes |

**Simulate** solves the circuit once at that frequency, takes the incident wave the circuit delivers into each of the
block's ports, sums the stored fields with those weights, and computes SAR and B1 from the sum exactly as a 3D run does.
Fields are combined before SAR is formed, never SAR itself. The results land beside the field set, in
`<stem>.fields/cosim/<analysis>/`; the Messages pane's **Open in 3D view** opens the coil's 3D view with the recombined
result drawn and selected. Its solution is labelled `<f> GHz, fields 'F1' (circuit port 1 driven)`.

The Messages pane gives the run one line for the pattern's notes, what each metric means and why any was not published, with **Details…**
to read them. A check that fails is a warning of its own, and so is a drive port that delivers no power to the block, which leaves the results empty.
A frequency the block's record does not store is flagged under the **Frequency** control and refused if you run it.

**What can drive.** The **Drive port** control lists the schematic's Term, TermG and P1Tone by name and port number. **+** adds a port to drive together
with it, with an amplitude for each, and **x** removes one. They all drive alike, and a port has to be on the part of the circuit the block is connected to. A VTone, ITone, PnTone or MOD_Source is listed too, and
flagged: none of them is a circuit port, and a run refuses them, because the analysis sends one wave in from a port's Z0 at the frequency it recombines.

A fields analysis has no **Simulate by** choice, because the block already holds the EM result. On one, `model=em` and
`--em on` start no EM run of the schematic's layout; they switch only EM-capable parts the circuit places, as a part set
to EM is.

**Per watt available or accepted.** With `norm=accepted` (the default) the numbers are per watt that entered the
circuit at the drive, so a poor match does not show: B1 per √W accepted barely moves as the match changes. With
`norm=available` they are per watt the source could deliver, so a poor match shows as a weak B1, as it would on the
scanner. The loop coil example uses `available`.

**What the run checks**, each a note, and a warning when it fails:

- the summed field's absorbed power against the field set's own tissue matrix, to 0.1 %;
- the power the circuit delivers into the block against the block's own S-matrix, to 2 %: this catches a `.sNp` read
  between its points, or one that is not the run's;
- that no excitation absorbs more than it dissipates.

All three compare the field set with itself. **They cannot see a field that is wrong in the same way in E and in S** —
a grid too coarse, a run stopped too early. The checks hold the recombination to its inputs; the inputs are the 3D run's.

**The block works on the saved result.** It reads the record and the files the record names, and no `.c3d`, `.cem` or `.clay`,
so editing the setup, or deleting the 3D view, changes nothing it uses. Its properties show one line from the record: when, which
solver and quality, the sweep and the port count. **Run settings…** beside it shows the settings that run used, read-only, with
what differs from the setup now marked. A difference adds one note to a run and blocks nothing. A record from before settings were
kept reads *settings not recorded*.

**What the block refuses**, naming itself and the file: no such record; a record holding neither a far field nor a field set, whose
S-parameters belong in an SnP; a file the record names that is missing or truncated; and files from different runs, saying which. The
record, the `.sNp`, the `.npy` and the field set share one stamp. File times are not compared, so a copied or checked-out result
reads as current.

**A Touchstone file is not enough.** A Fields analysis on a plain SnP is refused. A run from before records has a `.sNp` and a `.npy`
and no `.cemr`: pick its `.sNp` in the block's file picker and it offers to write one beside it, as `circuitrf em --adopt` does, with
the data files untouched. A saved schematic with a Fields analysis on an SnP still opens; running it says so, and the Messages pane
offers **Change to EM Field Data** when the block's `.sNp` can be adopted.

Headless: `circuitrf fields bench.csch` runs every enabled fields analysis of a schematic (`-a` names some, `--set`
changes a value first) and prints the same lines; MCP's `run analysis=fields` does the same.

## 3. Tuning and optimizing on fields {#goals}

Seven functions read a fields analysis, wherever a `measure` or `goal` expression is written. `F` is the analysis's name.

| Function | Value | Per |
|---|---|---|
| `B1pMean(F, region)` | volume-weighted mean \|B1+\| over the region | T per √W |
| `B1pMin(F, region)` | the smallest \|B1+\| there | T per √W |
| `B1pCoV(F, region)` | the spread of \|B1+\| over its mean (a surface coil's brain reads about 1) | — |
| `psSAR(F, mass)` | peak spatial-average SAR of `mass` grams, the full cube average | W/kg per W |
| `SARwb(F, region)` | the region's absorbed power over its mass | W/kg per W |
| `SarEff(F, region, mass)` | `B1pMean / √psSAR` | T per √(W/kg) |
| `Pabs(F)` | the power absorbed in all tissue | W per W |

"Per W" is the analysis's own `norm`. **A region** is a string: omitted, the setup's own (*B1 mean over* for B1, *Region*
for SAR); `"all"`, every tissue; a tissue's path (`"head/brain"`), its last part (`"brain"`), a phantom's name (`"head"`)
or a material; `+` joins several (`"skin+skull"`). `"box(x0, x1, y0, y1, z0, z1)"` takes every cell whose centre lies
inside, each number with a length unit; B1 is taken over all of them (air too), SAR over the tissue. An unknown word is
refused, naming the tissues the field set has.

<pre><code>goal Match  = dB(SP1.S(1,1)) analysis=SP1 over=freq lo=298 MHz hi=298 MHz le -20
goal B1     = 1e6 * B1pMean(F1, "brain") analysis=F1 ge 0.2
goal SAR10g = psSAR(F1, 10) analysis=F1 le 2</code></pre>

The Goal editor lists all seven under *Fields (SAR, B1)* for each fields analysis. Each evaluation does only what its
caller needs:

- **The optimizer, yield, centering and DOE** solve the circuit and form the waves; `Pabs` is then exact from the tissue
  matrix, and B1 and `SARwb` read only their region's cells. Nothing is written.
- **`psSAR` is always the full cube average**, never a sample or a coarser grid standing in for it, so an evaluation that
  scores it recombines the whole field set. On the loop coil example a whole evaluation, average included, is about a tenth
  of a second; the run's notes name the goals that pay it.
- **A tuning slider moving** rewrites local SAR and B1, so an open 3D view's plot follows the slider; `psSAR` waits until
  the slider is released, and says so rather than show a stale value.
- **Simulate and the optimizer's final point** do everything and write it.

**No openEMS process starts in any of it.** Yield trials, the optimizer and the sliders all recombine the stored fields.
A re-run trial (`run analysis=yield trial=<n>`, or the Data Display's re-run) is written under its own label,
`F1 (trial n)`, beside the analysis's own results.

## 4. Several channels {#multichannel}

A coil with several feeds — a quadrature birdcage, a transmit array — is driven by naming several circuit ports and an
amplitude for each:

<pre><code>W2 = polar(1,-90)
analysis F1 type=fields block=EM1 freq=298 freqUnit=MHz drive=1,2 weights=1,W2
tune mag(W2) min=0.5 max=2 opt=1
tune phase(W2) min=-180 deg max=0 deg opt=1</code></pre>

Only the ratios between amplitudes matter, and power goes as amplitude squared: for a power ratio write `sqrt(P)` from watts or `10^(PdBm/20)` from dBm,
with any of them a variable. Leave a P1Tone's amplitude empty and it takes its own **Pavl** and **Phase**, so the P1Tones' settings set the ratios. That
needs every drive to be a P1Tone with an empty amplitude: Pavl is in watts and a relative amplitude is not, so a mix is refused. Results stay per watt
whatever the Pavl.

The circuit is solved once with every drive at its weight. **Per watt** then means per watt of the drives' total,
accepted or available, so "maximise `B1pMin` with `psSAR` at most so much per W total" is an ordinary optimizer problem:
a channel's amplitude and phase are the parts of a complex variable, and ordinary tunables.

## 5. The radiation pattern {#pattern}

A 3D run with component ports stores a pattern per port, and so does a [planar run](#planar). A fields analysis combines them for the parts in the schematic, so the
pattern of the antenna as built follows every part value with no new EM run.

1. In the EM setup, tick the parts in **Component ports**, turn on **Radiation pattern** and tick **Create SnP schematic when the run finishes**.
2. **Simulate.** The run makes a cell named after the setup with `_cosim` on the end, and writes the same circuit as `<setup>_cosim.cnl`. The cell holds an
   EM Field Data block on the run's record, a `Port` for each of the setup's drawn ports and each connector, and each part from its port's node to ground,
   with its model from the board's schematic. A part with no model there is a placeholder variable, named in a note on the sheet and by `check`.
3. **Set the values and simulate the cell.** It has an S-parameter analysis over the sweep and `analysis F1 type=fields block=BOARD drive=1` at the stored
   frequency nearest the sweep's centre, with a note naming every stored frequency. Its [Data Display](data-display.html) holds S11, a polar cut of
   `F1.farfield.U` and the realized gain.
4. **Change values** with a [tuning slider](tuning.html), a parametric sweep, `--set`, the optimizer or yield. None starts an EM run. The Fields
   analysis's `S` is at its own frequencies, so `F1.S` lines up with the pattern.

A cell of that name is **never overwritten**: edit it freely and run the setup again, and the block reads the new result. A run that kept neither a far field
nor a field set gets an SnP in place of the block. A run with no component ports makes the `_sim` cell described under
[A schematic to start from](em-setup.html#snp-schematic).

The worked example is **Antenna Co-Simulation** (*File ▸ New ▸ Examples*): a meander antenna solved once at five frequencies, then matched by the optimizer, with its pattern compared across the band and against an inductor's error.

To wire a circuit by hand, place an EM Field Data block as in [The schematic side](#schematic) and add a Fields analysis. `RealizedGainDbi(F1)`,
`GainDbi`, `DirectivityDbi`, `RadiationEfficiency`, `TrpDbm` and `PeakEirpDbm` read the pattern in a `measure` or `goal` line.

### A planar result {#planar}

A planar run with **Radiation pattern** on works the same way: place an EM Field Data block on its record and add a Fields analysis. The hemisphere stays a hemisphere.
A planar setup with **component ports** makes the `_cosim` cell and netlist as a 3D one does, each part across its own port, and the block's pins are its component ports
by part. Without them the block's pins are the setup's own ports and the circuit is yours to draw. A via anywhere stops a planar pattern, and most boards with parts have
vias: the run then says so, and its `_cosim` circuit reads the Touchstone through an SnP block instead.
To put the board into the schematic it was drawn from rather than a circuit drawn for it, tick
[Include pre-existing schematic components](em-setup.html#snp-schematic-parts) as well: the cell is your schematic, its copper replaced by the run's SnP block.

- **A port for a part is a component port, or a delta gap you draw.** A [component port](em-setup.html#component-ports) needs nothing drawn: the solver adds a strip
  across the part's gap in the solved problem and cuts it with an internal delta gap. A port you draw yourself has to sit on metal, because a delta gap cuts metal:
  draw the part's site as continuous metal, put a port on it and set its type to **Internal delta gap**. An **Internal** port goes down a via to the plane, and any
  via in the structure stops the run producing a pattern.
- **A planar and a 3D answer differ.** They are different solvers, and the difference grows with frequency; neither is a check on the other.
- **An edge port is recombined at its reference plane.** A circuit on it sees the de-embedded S, and the run keeps what relates that plane to the port's gap. The pattern
  still holds the current on any feed the solver grew.
- **Gain and efficiency read as the run's own do.** With nothing around the block they are the numbers the run wrote. With parts around it, what the parts dissipate is
  added to what the structure takes, and realized gain includes the mismatch at the circuit's drive.
- **Three power terms are omitted by name**: `PowerSurfaceWave`, `PowerConductor` with `PowerFilm`, and `PowerDielectricAndGround`. Each is quadratic in the currents,
  so a port's number from the run cannot be added up for a circuit. `PowerAccepted` and `PowerRadiated` are published, and their difference is every loss together.
- **Refused:** ports in a calibration group, and a result from before this was kept, unless every port is an internal gap. Run it again.

### What can be placed across a gap {#gap}

The N-port has one common reference, but a **gap port's negative end is a pad, not ground**. So where a part goes depends on the port's kind:

| On | Place | Because |
|---|---|---|
| a gap port | a part from the port's node to ground | the circuit's ground stands for that port's own pad, so the part bridges the pads |
| two gap ports | **not** a part between their nodes | it is in series with both ports' references, which is not the board; `check` warns, naming the part and both ports |
| a feed, or any port with a real return | any network, series or shunt | its reference is the board's real return |

A part with more pads connects each signal pin to its port's node and its ground pin to ground.

**A two-port file on a gap port.** A two-port part file goes with port 1 on the node and port 2 on ground, so its series impedance stands across the gap.
Any **shunt branch in the file does not reach the board's ground plane**: port 1's stands across the gap, and port 2's is shorted out. A file whose
pad-to-ground capacitance matters therefore shows it in the wrong place. The pads themselves are in the EM model, and so is their capacitance to the plane.

**The part's body is not in the EM model**, with this method or any other that uses a gap port.

**A part not fitted.** A part whose **Disable** is *Open* in the board's schematic leaves its port open in the generated netlist and cell, and one set to *Short* shorts its port. Neither gets a placeholder value.

### A cut cell instance's model {#cut-models}

A [cut cell instance](em-setup.html#cell-instances) — a device, or a PCell named in the setup — has a port at each
connected pin's face, and the netlist puts the instance's own model back on them. Its block's comment says where each
model came from and how each terminal was wired.

**Where the model comes from, in order:**

1. **The schematic the layout was made from.** An instance placed at the top of the layout is the schematic's part of
   that name, with its parameters as written: `W=w_in`, an expression of the schematic's VARs, which the netlist carries.
2. **A sub-cell's schematic,** for an instance placed inside another cell (`X1/Q3`): the part `Q3` in the schematic of
   the cell `X1` is. Its parameters are written as the **values** the design gives them there, each level's expressions
   evaluated in its parent's scope, because a flat netlist has no cell scope to evaluate them in. So **a VAR that drove a
   nested instance no longer tunes it in the netlist**: change the value in the netlist, or run the schematic itself. The
   comment names each value written this way.
3. **The PCell's own component**, for a generated cell with no schematic counterpart (a MIM capacitor placed by hand in
   the layout): the built-in component its generator makes, at the parameters the layout records for it.
4. **A placeholder** when none of these is found, one per port, with the reason in the comment, as for a footprint part
   with no model.

**Pins are matched by name.** The model's terminals — a sub-cell's pins, or a symbol's pin names — are matched to the cut
pins of the same name, and a cell whose layout pins are numbers is matched by number. When the names do not match, the
instance is a placeholder listing both sets, rather than a guess.

**Terminals with no port of their own:**

| The pin is | Its terminal goes |
|---|---|
| Routed | On its port's node |
| The reference of other pins' ports | On `0`, the N-port's reference. The comment says so: that is the result's reference, not the pin grounded in the chip |
| Grounded by the setup | On `0` |
| Abutting another cut instance | On one node shared with the pin it touches, `j_<path>_<pin>` |
| Floating, and on ground in the schematic | On `0` |
| Floating otherwise | On its own unconnected node, `nc_<name>_<pin>`, with a warning in the run's messages |

Instance names in the netlist are made from paths (`X1/Q3` is `X1_Q3`, `@2` is `at2`), and the comment names each path.

**The same decision closes an EM cell.** A schematic run with `model=em` puts its devices back on their ports this way,
from the schematic being run, so a device value that moves no copper re-evaluates with no new EM run:
[What the analysis runs on](em-cells.html#what-runs).

### Frequencies {#frequencies}

A pattern exists **only at frequencies the run solved**. Far-field phase across a coarse sweep is not safe to interpolate, so a frequency between stored points
is refused, listing them. `freq=all` takes every stored one. S comes from the same result, so a denser S-parameter analysis in the same schematic reads
between the Touchstone's points as any SnP does, while its pattern stays at the stored frequencies.

## 6. Working from saved results {#saved}

The block reads a result on disk, so **a result outlives its setup**: the setup can be edited, the 3D view deleted or the workspace moved, and the block
reads what it read.

- **A run that does not finish changes no result.** Results are staged and published only once the run has finished, so a failed, cancelled or refused run leaves
  the previous `.sNp`, `.npy`, field set and record as they were, and a schematic reading them goes on working. A solver's working files are not part of
  this: a run that starts clears them, so 3D field plots of the previous run can be gone after one that fails.
- **A run that finishes replaces the result of its name.** The setup's **Output name** decides that name for every file, so a new name keeps the earlier result.
- **A result's settings can be read back.** **Run settings…** on the block, or `circuitrf explain <name>.cemr`, shows what that run used and what differs from
  the setup now.

## Costs and limits {#limits}

| | Lumped capacitors in the EM model | Co-simulation |
|---|---|---|
| EM | one run per capacitor value; the loop coil's took 72,000 steps | one run per port, a few thousand steps each, once |
| a capacitor changed | a new EM run | one circuit solve and a recombination, about a second, no process |

- **Agreement.** On the loop coil, on the tutorial's own grid, the co-simulation reproduces the lumped-capacitor run to
  +1.5 % in B1 and +3.0 % in psSAR10g. About four fifths of that is the lumped run's own ring-down cut short: run to −60 dB
  it moves to within 0.3 % in B1 and 0.6 % in psSAR10g of the co-simulation.
- **One frequency per field set**, the SAR frequency: a fields analysis at any other is refused.
- **A parametric sweep** wraps a fields analysis over a far field and adds its axis to every cube. Make the sweep's axis the **Family** of a pattern cut for one curve
  per value, each named in the legend, and plot `F1.farfield.RealizedGainDbi` along it for the peak. The run summary states the memory its cubes take and warns above 2 GB, when
  naming one frequency in place of `freq=all` is the remedy. It is refused at a frequency with a field set: SAR and B1 volumes are written per run and the cube average
  is a long solve per run, so those stay a slider's or `--set`'s, one value at a time. A result holding both still sweeps its pattern at `freq=all`.
- **How far the pattern agrees.** Measured on a 50 mm strip dipole at 3 GHz with a feed and two gap ports, against a run with those parts in the EM model or
  those loads on the ports, on one mesh and within 20 dB of the peak. **Palace** and **openEMS** agree to 0.003 dB or better in peak gain and pattern, and within 2e-4 in S11,
  with the parts 5 nH and 1 pF or the loads 200 Ω and 100 Ω. **EMerge** at Standard reads 0.043 dB in peak gain, 0.079 dB in pattern and 3.8e-3 in S11.
  Its gain and pattern are within what the method asks, and its S11 across a change of load needs a fine port edge mesh: an `EdgeRefinement` of 0.02 gives 7.5e-4.
- **Near a resonance** (6.5 nH and 0.05 pF on the same dipole), Palace agrees to 0.009 dB and 1.6e-3 in S11 on its own mesh. Draft and Standard disagree with each other
  by far more than that: these comparisons show the method is right, not that the mesh is. A radiating dipole is a mild case, and a sharper resonance has not been measured.
- **The air box moves gain and efficiency more than S11.** On a 95 mm board at 868 MHz with first-order Palace elements, padding of 86, 130 and 170 mm read efficiency 100.5, 90.9 and 84.9 % and gain 2.09, 1.51 and 1.25 dBi, while S11 stayed within 0.1 dB and directivity within 0.15 dB. λ/4 passed the run's own power balance and was still too small. Check a pattern's gain at a second box size.
- **A cylindrical grid's** field set recombines as a Cartesian one does, but its volumes are not drawn in the 3D view.

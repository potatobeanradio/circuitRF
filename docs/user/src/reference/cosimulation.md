---
title: Co-simulation: B1 and SAR from a schematic
slug: reference/cosimulation.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Co-simulation
lede: Capacitors as ports. One EM run keeps each port's fields, and a schematic's fields analysis turns the waves its circuit delivers into B1 and SAR — so tuning, optimizing and yield on fields start no EM run.
keywords: co-simulation, cosimulation, EM Field Data, EmFieldData, result record, cemr, port fields, per-port fields, PerPortFields, field set, superposition, recombination, fields analysis, type=fields, MRI coil, RF coil, tuning capacitor, matching network, capacitors as ports, B1pMean, B1pMin, B1pCoV, psSAR, SARwb, SarEff, Pabs, goal, tuning, optimizer, yield, multi-channel, RF shimming, parallel transmit, drive weights, quadrature, birdcage, norm=available, norm=accepted, research use
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
against S11, B1 and psSAR goals in 4 s. What B1 and SAR are, and their units, is [SAR and B1](sar.html).

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
the feed. Then add a **Fields (SAR, B1)** analysis in the Analysis editor, whose **Block** control lists the schematic's EM
Field Data blocks, those inside sub-cells included, and still takes typed text:

<pre><code>analysis F1 type=fields block=EM1 freq=298 freqUnit=MHz drive=1 [norm=accepted|available] [average=yes|no]</code></pre>

| Key | | |
|---|---|---|
| `block` | the EM Field Data block's instance name (`X1.EM1` inside a cell) | required |
| `freq`, `freqUnit` | one of the run's SAR frequencies | required |
| `drive` | the circuit port that drives; every other circuit port is terminated in its Z0 | required |
| `weights` | with several drives, each one's complex amplitude ([multi-channel](#multichannel)) | 1 each |
| `norm` | per watt **accepted** at the drive, or per watt **available** from it | accepted |
| `average` | `no` skips the cube-averaged SAR, the step that costs the most | yes |

**Simulate** solves the circuit once at that frequency, takes the incident wave the circuit delivers into each of the
block's ports, sums the stored fields with those weights, and computes SAR and B1 from the sum exactly as a 3D run does.
Fields are combined before SAR is formed, never SAR itself. The results land beside the field set, in
`<stem>.fields/cosim/<analysis>/`; the Messages pane's **Open in 3D view** opens the coil's 3D view with the recombined
result drawn and selected. Its solution is labelled `<f> GHz, fields 'F1' (circuit port 1 driven)`.

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

The circuit is solved once with every drive at its weight. **Per watt** then means per watt of the drives' total,
accepted or available, so "maximise `B1pMin` with `psSAR` at most so much per W total" is an ordinary optimizer problem:
a channel's amplitude and phase are the parts of a complex variable, and ordinary tunables.

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
- **A cylindrical grid's** field set recombines as a Cartesian one does, but its volumes are not drawn in the 3D view.

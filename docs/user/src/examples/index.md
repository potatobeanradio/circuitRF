---
title: Example Workspaces
slug: examples/index.html
doc-kind: Examples
breadcrumb: Docs > Examples
lede: Every example workspace that ships with circuitRF, by topic, what each one shows, and where to open it — File ▸ New ▸ Examples.
keywords: examples, example workspaces, sample designs, tutorials, walk-through, File New Examples, open an example, starting point, demo, reference designs
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#where">Where to find them</a></li>
<li><a href="#linear">Linear and Calibration</a></li>
<li><a href="#design-flow">Design Flow</a></li>
<li><a href="#noise">Noise</a></li>
<li><a href="#nonlinear">Nonlinear</a></li>
<li><a href="#models">Models</a></li>
<li><a href="#layout">Layout and Interchange</a></li>
<li><a href="#pdn">Power Distribution Networks</a></li>
<li><a href="#planar-em">Planar EM and Boards</a></li>
<li><a href="#em3d">3D EM</a></li>
<li><a href="#antennas">Antennas and Arrays</a></li>
<li><a href="#sar">SAR</a></li>
<li><a href="#thermal">Thermal</a></li>
<li><a href="#transient">Transient</a></li>
<li><a href="#systems">Systems and Radio Links</a></li>
</ol>
</nav>

## Where to find them {#where}

circuitRF ships with complete, editable example workspaces: real designs with their cells, test benches, analyses and
Data Displays, each built to show one part of the application working end to end. They are loosely organized by topic:
the menu has a submenu for each, and the sections below follow the same topics, each example with a short abstract saying
what it builds and what it shows. An example that fits more than one topic is under the one it mostly teaches.

<div class="callout note">
<span class="label">Open an example from File ▸ New ▸ Examples</span>
<p>Choose <strong>File ▸ New ▸ Examples</strong>, then a topic and an example in it, and choose a folder: circuitRF copies the example there and
opens the copy, so you can edit and run it freely and the shipped original stays as it was. Each opens on its
<strong>README</strong>, which walks through it; each README is also here, with its pictures: select an example's title below. Results are not included — run the benches or the EM setups yourself;
a 3D setup whose solver is not installed offers to install it on the first Simulate. Headless, <code>circuitrf reference
examples</code> lists them and <code>circuitrf new example &lt;dir&gt; &lt;name&gt;</code> copies one.</p>
</div>

## Linear and Calibration {#linear}

### [S-Parameters](s-parameters/index.html) {#ex-s-parameters}

Four test benches driven by Terms, the element that sets an S-parameter port and its reference impedance. A potentially unstable Touchstone amplifier, placed as an SnP block and swept 1–5 GHz, carries measurement blocks that write gain, VSWR, Δ, Rollett K, μ and MSG as custom equations. The same metrics are built into the Data Display, so each hand-written formula can be checked against a built-in answer. The expressions show 1-based port numbers in `SP1.S(2,1)`, `x*x` in place of `x^2` over a cube, and one measure reading an earlier one. Two coupled inductors show how to extract a coupling factor by converting S to Z: k comes out flat at 0.5 across the sweep while |S21| moves. The FetStability bench is what the Two Port Stability template makes (New Cell ▸ Template), reading a two-port the FetBias bench generated: its Data Display opens with source and load stability circles at every frequency, μ and μ′, and the maximum gain, none of it written as an expression.

*Folder:* `S-Parameters` · *Topics:* S-parameters, Touchstone, SnP block, stability, Rollett K, mu, MSG, measurement expressions, coupled inductors, coupling factor, stability circles, Two Port Stability template

### [TRL and De-embedding](trl-and-de-embedding/index.html) {#ex-trl-and-de-embedding}

TRL calibration and de-embedding on synthetic measurements, made by circuitRF from cells you can open, so every answer can be checked against the truth. The first bench solves a Thru, a Reflect and a Line measured through two different, mismatched fixtures and writes the two fixture files. The second removes them from a measured stepped-impedance low-pass filter with Deembed blocks, the opposite of an SnP on the same file, and lays the result over the filter itself; it returns to within 2e-10 in S. The third de-embeds a GaN FET through a 6-port extrinsic network inside S-parameter and harmonic-balance runs, so Cgs, Cgd, gm and Rds read flat with frequency at the intrinsic plane while the raw device's bend. Its README sets out the cascade-matrix equations, the 20°–160° rule for the Line, how to wire a Deembed, why the FET's source needs a net of its own, and which benches to duplicate for real measured data.

*Folder:* `TRL and De-embedding` · *Topics:* TRL calibration, de-embedding, Deembed block, fixtures, Thru Reflect Line, cascade matrix, intrinsic FET, extrinsic network, Y-parameters, Touchstone

### [Smith Chart (narrowband match)](smith-chart/index.html) {#ex-smith-chart}

A device input impedance of about 8 − j12 Ω, given at three frequencies across 2.3–2.6 GHz, matched to 50 Ω at 2.45 GHz with a series inductor and a shunt capacitor in the Smith chart tool. The chart draws the match as two arcs — a constant-resistance circle for the inductor and a constant-conductance circle for the capacitor — with grippers at each node of the walk. Dragging the corner gripper changes the inductor's value and swings the band-edge load points much further than the centre one, making the bandwidth cost of a two-element match visible. Constant-Q arcs at the corner's loaded Q of 2.291, a swept band locus and a changeable design frequency show what sets that bandwidth. The status strip reports |Γ|, VSWR and mismatch at each frequency. Copy on the network strip pastes the network into a schematic, whose S-parameter run against a complex port impedance confirms the conjugate match independently.

*Folder:* `Smith Chart` · *Topics:* Smith chart, impedance matching, L-section, narrowband match, constant-Q, VSWR, reflection coefficient, conjugate match, S-parameters, grippers

## Design Flow {#design-flow}

### [Hierarchy](hierarchy/index.html) {#ex-hierarchy}

A 9 dB attenuator on a 50 Ω board, built as cells inside cells to follow hierarchy through schematic, layout and 3D. A pi-pad cell takes its attenuation as a cell parameter and computes its resistors in its own VAR block; a board cell places it twice, at 3 dB and 6 dB, with three microstrip lines, and a test bench simulates through both levels. Push Into Cell and Pop Out move between levels. Update Layout from Schematic made linked layouts at both levels, so the board places the pad's layout twice, a re-run places only the difference, and LVS compares each level, with the pad read flat. An Assembly 3D view places the board's 3D view, which places its own layout on the technology's stackup, and a coaxial launch cell twice. Pushing in reaches four levels: Assembly ▸ Board (3D) ▸ Board (layout) ▸ Pad (layout). The 3D views carry no EM setup; they are there to show the hierarchy.

*Folder:* `Hierarchy` · *Topics:* hierarchy, sub-cells, cell parameters, Push Into Cell, Update Layout from Schematic, linked layout, LVS, FlattenForLvs, 3D assembly, attenuator, netlist

### [Tuning and Optimization](optimization/index.html) {#ex-optimization}

Three small test benches, each with its tuning ranges, goals and algorithm already saved, so the Optimizer panel can run them at once. An L-section match of a 200 Ω load to 50 Ω uses Levenberg–Marquardt gradient optimization and stops exactly on its −20 dB goal, compared against a textbook preset that keeps some margin. A three-resonator bandpass filter is optimized by minimax, which levels the passband ripple against its limit at both band edges while meeting two stopband goals; its result lands near the textbook Chebyshev design. A FET amplifier is stabilized for μ ≥ 1.05 while its gain goal is recovered by tuning a complex source impedance by magnitude and phase as separate variables. Its stabilizing resistor's range is drawn too narrow on purpose, so the run rails, the ⚠ mark appears, and Widen extends the range until both goals are met. Presets, Push, Reset and Snapshot ghost traces in the Data Display are all exercised, and the example shows how the sweep grid limits what a goal can hold between points.

*Folder:* `Optimization` · *Topics:* optimization, tuning, Optimizer panel, goals, Levenberg–Marquardt, minimax, stability factor, presets, L-section, bandpass filter, complex variable

### [Yield, Corners and Centering](yield/index.html) {#ex-yield}

Four test benches for the Yield and Sensitivity panels, each with tolerances, specs and run settings saved, and each finishing in a second or two. A bandpass filter of ±2 % parts, with two correlated capacitors, is centred on tight optimizer goals and then judged against looser yield specs by Monte Carlo: 86.4 % of 500 trials pass. A feedback amplifier fails one of six temperature and supply corners generated in Corners mode, shows per-corner Monte Carlo yields, gains a statistical corner saved from its worst trial, and is then optimized across all corners. A bias divider's yield is centred with CMA-ES from about 70 % to above 95 %, verified on fresh trials, with an optional quadratic surrogate that cuts the simulation count tenfold. A final bench ranks an amplifier's and a spiral inductor's Touchstone entries in the Sensitivity panel, tying reciprocal pairs for the passive block. Yield displays, trial tables and Send to Tuning link results back to the design, and every run has a `circuitrf yield` equivalent.

*Folder:* `Yield` · *Topics:* yield, Monte Carlo, corners, tolerances, design centering, CMA-ES, surrogate, sensitivity, correlation, statistical corner, Touchstone

## Noise {#noise}

### [S-Parameter Noise](s-parameter-noise/index.html) {#ex-s-parameter-noise}

Three benches that tick Noise on an S-parameter analysis (`Noise=yes`), which adds the noise-wave correlation and, for a two-port, NF, NFmin, Te, Tmin, Rn and the optimum source Sopt and Zopt as cubes beside S. A matched 6 dB pad swept from 20 K to 400 K shows a passive part's noise following its own temperature, laid over 1 + (T/T0)(L − 1); it also shows why a design that states no temperature runs at 300 K rather than 290 K. A cascade of two amplifiers, read from Touchstone files with noise blocks, and a pad between them sits on Friis' formula at every pad loss, and shows why the low-noise stage goes first. A noise-matched LNA around a synthetic transistor two-port plots NF against NFmin, Sopt's locus and two constant-noise-figure circles on a Smith chart, with the cost of its stabilising resistor and match losses. Every result is checked against a closed form, each bench runs in about a second, and a run's noise exports as a Touchstone noise block.

*Folder:* `S-Parameter Noise` · *Topics:* noise figure, NFmin, noise parameters, Sopt, noise temperature, Friis formula, attenuator noise, noise circles, LNA, Smith chart, Touchstone noise block

### [Receiver Noise](receiver-noise/index.html) {#ex-receiver-noise}

The noise work of a 2.45 GHz receiver in seven benches, run in order, each building on the last. A one-stage FET LNA with a two-temperature noise model is noise-matched, with noise and gain circles and `NFpct` naming which part sets the noise: the stabilising resistor costs nearly as much as the transistor. The receiver as system blocks lies on Friis' formula at every LNA gain. A single-diode mixer gives SSB and DSB noise figure against LO drive from a harmonic-balance noise run, split by input sideband. The LNA under a 2.3 GHz blocker shows desensitisation, and a noise sourcepull maps NF and gain contours on the source plane at two drive levels. An envelope run takes an NR signal through the chain with a real mixer and a phase-noisy LO, splitting EVM into its noise and distortion parts across three regimes. The last bench is the varactor-tuned Colpitts VCO whose phase noise that LO carries, transferred with Copy as Phase-Noise Mask.

*Folder:* `Receiver Noise` · *Topics:* noise figure, LNA noise match, noise circles, Friis formula, mixer noise, SSB and DSB, blocker, desensitisation, noise sourcepull, EVM, phase noise, VCO

## Nonlinear {#nonlinear}

### [Harmonic Balance](harmonic-balance/index.html) {#ex-harmonic-balance}

A 48 V GaN power amplifier in two benches over one reusable device cell: an SDD FET whose drain current is an equation in the expression language, wrapped with one parameter, `Periphery_mm`. The single-tone bench at 2 GHz sweeps drive from 0 to 30 dBm, with four measurement blocks that show how to read a swept variable, V and I at the fundamental, DC at harmonic index 0 for drain efficiency and PAE, and harmonics as `dB()`, `mag()` and complex values on a Smith chart. It reaches about 39.5 dBm output and 43 % drain efficiency as the gain compresses. The two-tone bench drives a PnTone at 1990 and 2010 MHz for IM2, IM3 and IM5, addressed by mixing-product tags such as `"(2,-1)"`, and measures each carrier separately for efficiency. Outer sweeps of frequency and supply are declared and disabled, ready to produce a surface.

*Folder:* `Harmonic Balance` · *Topics:* harmonic balance, power amplifier, GaN, SDD, compression, PAE, drain efficiency, two-tone, intermodulation, IM3, mixing products, measurement blocks

### [Envelope (modulated)](envelope/index.html) {#ex-envelope}

The Harmonic Balance example's 48 V GaN amplifier driven by an NR 20 MHz, 64QAM carrier from a MOD_Source and solved with the Envelope (modulated) analysis. The Signal Designer, opened with Design signal…, edits the carriers from presets and shows the signal's spectrum, CCDF, PAPR and ideal ACLR. One 200 µs run records the envelope at every labeled net and the power at every IProbe: the delivered spectrum, ACPR1 and ACPR2, output power, gain, drain efficiency, NMSE, AM-AM and AM-PM, and the memory cloud, with a measure block adding PAE. A second bench wraps the run in a sweep of input power for ACPR against drive, where the AM-AM curves of every drive level lie on one curve. Its README also covers two carriers far apart, each given its own envelope fundamental and an IMD cube, against a single-envelope run, and a ready-made two-carrier case on a narrowband test amplifier.

*Folder:* `Envelope` · *Topics:* envelope simulation, modulated signal, NR, MOD_Source, Signal Designer, ACPR, ACLR, AM-AM, AM-PM, PAPR, power amplifier, two-carrier IMD

### [Pulsed Radar (pulsed CW)](pulsed-radar/index.html) {#ex-pulsed-radar}

A 48 V S-band GaN stage at 3 GHz driven by 100 µs pulses at 10 % duty from a pulsed-CW MOD_Source, solved with the Envelope analysis. The device is a synthetic 12 mm GaN HEMT built with Tools ▸ Model Composer…, with a three-pole thermal ladder and drain-lag trapping, in a matched, stabilised class-AB stage fed from a 470 µF storage capacitor that sags during each pulse. A harmonic-balance drive sweep gives the CW steady state: output power, gain, PAE and junction temperature rise. The 3 ms envelope run, three pulse periods, steps the thermal and trap states and the supply as memory, and gives what harmonic balance cannot: droop across each pulse top, phase droop, rise and fall times, and pulse-to-pulse spread from current collapse. Its Data Display plots output power and junction temperature over the run and a per-pulse table; at the design drive the first pulse droops 0.26 dB. Its README lists the settings traded away for speed and what is not modelled.

*Folder:* `Pulsed Radar` · *Topics:* pulsed CW, radar, envelope simulation, pulse droop, pulse-to-pulse stability, self-heating, thermal network, drain lag, current collapse, supply sag, GaN

### [Loadpull and Pursuit](loadpull/index.html) {#ex-loadpull}

Two ways to find a GaN device's optimum load. The Loadpull bench places the device between a SourceTuner and a LoadTuner, bias tees on, and sweeps the load over a polar grid read from a plain-text `.gam` file. At each grid point the engine walks a drive ladder up to 3 dB gain compression and reports the operating point there, which keeps the contours comparable point to point. Its README shows how the grid file's header tags decide whether its numbers are reflection coefficients or impedances, and why a variable's unit belongs in its unit column rather than in the expression. The LoadpullPursuit bench needs no grid: it starts from a guess, walks toward the optimum and draws two constant-VSWR rings around what it found, inside an outer sweep across 1.8–2.2 GHz, with a disabled supply sweep ready for a surface. Both benches carry measurement blocks reading the drain voltage by net name and harmonic index, because a loadpull result is a cube like any other.

*Folder:* `Loadpull` · *Topics:* loadpull, load-pull pursuit, optimum load, load tuner, source tuner, gamma grid, compression, contours, constant VSWR, GaN, harmonic balance

## Models {#models}

### [Model Composer](model-composer/index.html) {#ex-model-composer}

Three device models built from data with Tools ▸ Model Composer…, every data set synthetic so each model can be compared with the device it came from. A GaN FET is fitted from DC, pulsed and double-pulse I–V and two-temperature S-parameters: pads are removed with open and short dummies, network inductances fitted by the multi-bias method, self-heating typed, ANN drain and gate currents trained with junction temperature and drain lag as inputs, and ANN charges trained from both S sets. The exported model is placed as an 8-finger device in a class-AB power sweep at 2 GHz and a 49-load loadpull, and exported as Verilog-A for a side-by-side comparison bench. A spiral-inductor family is fitted to six turn counts with one law per element value, predicting a held-out spiral and placing a 3-turn coil in a low-pass filter. A third model imports a simulator's bias tables at the device plane. Every step also runs headless through `circuitrf model`.

*Folder:* `Model Composer` · *Topics:* Model Composer, device modelling, GaN FET, ANN model, de-embedding, pulsed I–V, self-heating, drain lag, loadpull, Verilog-A, spiral inductor, scalable model

### [Symbolically Defined Device (SDD)](symbolically-defined-device-(sdd)/index.html) {#ex-symbolically-defined-device-(sdd)}

A nonlinear GaN FET model written as equations rather than compiled code, built up in five benches so each equation's effect can be seen on its own. The first holds only the drain current as a function of both port voltages; the second adds the gate's forward diode and gate-drain breakdown, written once at each port with opposite signs; the third adds a gate and a drain charge on the weight-1 path, each a function of one voltage, so the current is their time derivative; the fourth makes the charges two-dimensional, the gate charge falling with drain voltage and a gate-drain charge between the ports; the fifth draws that gate-drain capacitor as a one-port SDD of its own and reads exactly what the fourth reads. Every one runs the Loadpull and Pursuit example's loadpull, and its display marks the maximum-power and maximum-efficiency points on the Pout and PAE contours with a summary table at each, so the optimum load can be watched moving as charge is added. A sixth bench's SDD reads the drain supply's current through a control reference and holds the quiescent current against a threshold shift, an active bias loop. The README explains every equation.

*Folder:* `Symbolically Defined Device (SDD)` · *Topics:* SDD, symbolically defined device, equation-defined device, FET model, GaN, drain current, gate current, breakdown, nonlinear capacitor, charge, weighting function, control current, bias loop, loadpull, MXP, MXE

## Layout and Interchange {#layout}

### [PDK PCells (Python artwork)](pdk-pcells/index.html) {#ex-pdk-pcells}

The artwork half of a process design kit, shipped inside the workspace: a Python file, `kit.py`, declares four parameterised cells — a microstrip line, a square spiral inductor, an octagonal spiral and a MIM capacitor — whose layout geometry is regenerated from their parameters rather than stored. Each places from one Library palette tile, as artwork on a layout or as its symbol on a schematic, exposes its parameters in the Properties Inspector, and offers drag handles that edit the parameter behind the shape. The MIM capacitor reports its capacitance as a readout derived from the drawn plates. A built-in serpentine thin-film resistor is checked four ways: the parameter readout, a simulation, a planar EM run of the film as a sheet, and LVS. The spiral resonator compares two routes to an LC resonance — all-EM with three analysis levels, and the coil's EM result composed with an ideal capacitor in a netlist — which agree to 0.9 %. The example also shows which GaAs metal is the air bridge, and why every generator draws on Metal1 by default.

*Folder:* `PDK PCells` · *Topics:* PCell, Python, PDK, parameterised cell, spiral inductor, MIM capacitor, thin-film resistor, microstrip, planar EM, LVS, GaAs MMIC, drag handles

### [Layout versus schematic](lvs/index.html) {#ex-lvs}

Three designs built to test whether artwork implements its schematic, each small enough to check by hand. A 3 dB resistive pi attenuator on a two-layer board is symmetric, grounded on drawn copper and stitched by vias, and LVS must report nothing. A copy with the same schematic carries six deliberate faults in its artwork — a swapped net, a missing part, an extra part, a short, an open and a wrong value — and its README states what the report should say about each, down to the short's path and coordinate. A Python script writes all six faults, or any one with `--only`. An MMIC bias tee on the shipped GaAs process adds what only a die can show: a ground reference that exists only in the stackup, parts that carry their generated parameters, and a MIM capacitor whose terminals sit on different layers. Its part table shows how drawing-grid quantisation sets the floor under LVS's 1 % property tolerance. A fourth bench runs the correct attenuator as an S-parameter sweep.

*Folder:* `LVS` · *Topics:* LVS, layout versus schematic, netlist comparison, short, open, property tolerance, terminal map, attenuator, MMIC, Python generator, circuitrf lvs

### [Artwork to Schematic](artwork-to-schematic/index.html) {#ex-artwork-to-schematic}

A small two-layer microstrip board delivered as a fabricator receives it — Gerber and Excellon files, a placement file and a bill of materials — imported with File ▸ Import ▸ Board and turned into a schematic by Design ▸ Create ▸ Schematic from Artwork…. Recognition reads ground on the bottom plane, the edge ports, seven microstrip lines, a bend, a tee with an open stub and three placed parts, with values from the BOM and cases from the land patterns, in seconds and with no EM run. The BOM leaves out one inductor's value on purpose, so it arrives as a global variable with a tune entry. Simulating the recognised board and tuning that variable in the Tuning panel takes S11 at 2 GHz from about −7.7 dB to the −15.8 dB of the schematic the board was drawn from, at 8.2 nH. Show in Artwork, Swap Line Type and the headless `circuitrf recognize` are also exercised, and the example shows why the imported stackup must be corrected before line impedances can be trusted.

*Folder:* `Artwork to Schematic` · *Topics:* Gerber import, Excellon, BOM, placement file, board import, schematic recognition, artwork to schematic, microstrip, tuning, circuitrf recognize

## Power Distribution Networks {#pdn}

### [Power Rail Integrity](power-rail/index.html) {#ex-power-rail}

A synthetic four-layer 30 × 20 mm board with one 3.3 V rail, set up for the four questions the railRF window answers. A regulator source feeds a 350 mA load through a ferrite bead and a 0.20 mm bottom-layer run, with thirteen decoupling capacitors placed as real footprint instances and a board netlist and placement projected from the layout. The DC solve reports 47.6 mV of drop against a 52 mV budget and ranks where it goes: almost half is one thin trace. Over 100 kHz to 100 MHz the rail impedance is judged against a flat 55 mΩ target, and a ranking re-solves the sweep without each part to show which capacitors matter. Nine identical 100 nF parts are worth three different amounts, because their mounting loops, read off the artwork, depend on via-in-pad versus fan-out. Parts can be unmounted without touching the artwork, Compare… checks a re-laid-out copy against the original, and the Fast and Accuracy models are compared on the same board.

*Folder:* `Power Rail` · *Topics:* power integrity, PDN, DC drop, IR drop, target impedance, decoupling capacitors, mounting inductance, ferrite bead, via-in-pad, railRF, four-layer board

## Planar EM and Boards {#planar-em}

### [Klopfenstein Taper vs MoM](klopfenstein-taper/index.html) {#ex-klopfenstein-taper}

A 50 to 100 Ω Klopfenstein taper, 15 mm long, solved two ways for one S11 plot. The schematic places an MKLOPF closed-form model between 50 Ω and 100 Ω Terms and sweeps 0.5–12 GHz in under a second; it reads its substrate from the workspace technology, the same `.ctech` the EM setup reads. The EM side runs the full-wave planar kernel over the drawn artwork, with an edge port at each end, in about a minute, from Simulate or `circuitrf em`. No results ship. The comparison teaches one trap: port 2 is referenced to 100 Ω, which the `.npy` carries per port while a `.s2p` header can state only one reference, so the `.npy` is the file to plot. Expect close agreement through the roll-off and a visible difference in where the deep nulls land above 8 GHz, where the EM sees the staircased flare, the substrate's dispersion and the de-embedding reference planes.

*Folder:* `Klopfenstein Taper` · *Topics:* Klopfenstein taper, impedance transformer, MKLOPF, microstrip, planar EM, method of moments, full-wave, edge port, reference impedance, closed form vs EM

### [Dividers and Couplers](dividers-and-couplers/index.html) {#ex-dividers-and-couplers}

The RF dividers, hybrids, couplers and baluns as built-in parts, each stated by its specs and drawn from the same plan its circuit model simulates, on 20 mil RO4350B at 2.4 GHz. The Wilkinson divider (MWPD) takes Z0, F and the resistor's pin span, with the widths and lengths shown as a readout; `K` makes it an unequal split and `N` a two-section broadband divider, and a four-way tree is three of them. The branch-line (MBLC) and rat-race (MRRC) hybrids, the 10 dB coupled-line coupler (MCLC) and the coupled-line balun (MCBL) are built the same way, each laid out by Update Layout from Schematic. Every EM cell has a Planar MoM setup (the balun's is refused) and a 3D view with EMerge and Palace setups, and a test bench beside it: the cell's circuit plus one SnP block per solver, each reading the Touchstone file its own run writes, with a lumped part's component port closed by that part. No results ship. Run the setups, simulate the bench, and open the cell's data display: the circuit model is drawn as a line and each solver as markers at the eleven frequencies it solved. The Gysel (MGPD), Bagley (MBPD) and tee (MTPD) dividers, the Schiffman phase shifter (MSPS), a meandered delay line (MDLY), the compact rat-race, the three-section and tandem couplers, the lumped Wilkinson and a 6 dB resistive pad (MRPAD) follow, each with an equations model in its bench, the same topology on ideal TLIN and CLIN lines or ideal parts, drawn dashed in its data display; most are solved on Planar MoM too, on a coarse mesh. The lumped parts' 0402s are placed by Place Site Parts, and the tandem, whose crossovers are bond wires, has no EM run. The README tables each part at 2.4 GHz and keeps the open points, such as the 3D solvers reading the coupler's coupling about 1 dB strong and microstrip's unequal mode speeds holding the balun's outputs about 170° apart.

*Folder:* `Dividers and Couplers` · *Topics:* Wilkinson divider, power divider, MWPD, isolation resistor, unequal split, multi-section divider, broadband, four-way divider, branch-line hybrid, MBLC, rat-race hybrid, MRRC, quadrature hybrid, coupled-line coupler, MCLC, directivity, Marchand balun, MCBL, Gysel divider, MGPD, Bagley polygon divider, MBPD, tee divider, MTPD, Schiffman phase shifter, MSPS, delay line, MDLY, meander, compact rat-race, multi-section coupler, tandem coupler, lumped Wilkinson, Place Site Parts, resistive pad, attenuator, MRPAD, equations model, TLIN, CLIN, quarter-wave, microstrip, Update Layout from Schematic, LVS, Planar MoM, EMerge, Palace, RO4350B, test bench, SnP, Data Display, component ports

### [Planar Filters](planar-filters/index.html) {#ex-planar-filters}

The microstrip filters, one cell per form, each a single built-in part that holds its own widths, lengths and gaps, worked out from the textbook closed forms on 20 mil RO4350B. Three are coupled-resonator filters (MCRC) at 2.4 GHz: Cohn's edge-coupled filter, the same design folded into tapped hairpins, and four open loops in Hong and Lancaster's cross-coupled square, with a transmission zero either side of the band. Four are ladders (MLAD): a stepped-impedance and an open-stub lowpass near 2 GHz, an end-coupled bandpass whose outer couplings are 0402 capacitors because no gap on this board couples strongly enough, and a lumped LC lowpass of sites alone, filled by Place Site Parts with 0402 parts. Two are coupled arrays (MCLA): Matthaei's interdigital filter and a tapped combline loaded by 0402 capacitors. Each cell is laid out by Update Layout from Schematic and is LVS clean, and its test bench lays the circuit model over the ideal prototype the design came from, circuitRF's Filter block at the same response, order, ripple and band. The edge-coupled, hairpin, interdigital and stepped-impedance cells, and the stub and LC lowpasses, also have a coarse Planar MoM setup, whose Touchstone file the bench reads. No results ship: run the setup where there is one, simulate the bench, and open the cell's data display. The README tables each filter's centre or cutoff, ripple and rejection on the prototype, the model and the solve.

*Folder:* `Planar Filters` · *Topics:* microstrip filter, bandpass, lowpass, MCRC, edge-coupled, parallel-coupled, hairpin, tapped feed, open-loop, cross-coupled, transmission zero, MLAD, stepped-impedance, Kuroda, stub filter, end-coupled, lumped LC, Place Site Parts, MCLA, interdigital, combline, Optimizer, ideal prototype, Filter block, Update Layout from Schematic, LVS, Planar MoM, RO4350B, test bench, SnP, Data Display, component ports

### [MMIC Wilkinson](mmic-wilkinson/index.html) {#ex-mmic-wilkinson}

The Wilkinson divider of the Dividers and Couplers example on a die: an MWPD at 20 GHz on the shipped 100 µm GaAs stack, every line on Metal1, and a thin-film resistor across the ring's gap, whose pin span the divider's `Gap` is set to so Update Layout lays it straight across. LVS matches the layout to the schematic. The test bench sets the circuit model beside an equations model, ideal lines of the closed-form impedances and lengths and an ideal resistor, so the table gives what the gold lines lose at 20 GHz. No results ship.

*Folder:* `MMIC Wilkinson` · *Topics:* Wilkinson divider, MWPD, MMIC, GaAs, thin-film resistor, TFR, Update Layout from Schematic, LVS, equations model, test bench, SnP, Data Display

### [RFIC Branch-Line](rfic-branch-line/index.html) {#ex-rfic-branch-line}

The branch-line hybrid of the Dividers and Couplers example on a silicon back-end: an MBLC at 60 GHz on the top metal of the shipped three-metal stack with Metal1 as its ground, a thin-film microstrip whose field stays in the oxide, out of the conducting silicon. The test bench sets the circuit model beside an equations model of ideal lines: the quadrature holds, and the thin oxide costs about 9 dB in conductor loss. LVS finds no error. No results ship.

*Folder:* `RFIC Branch-Line` · *Topics:* branch-line hybrid, MBLC, quadrature hybrid, RFIC, silicon, thin-film microstrip, ground reference, millimetre wave, 60 GHz, Update Layout from Schematic, LVS, equations model, test bench, SnP, Data Display

### [PCB Component Ports](pcb-component-ports/index.html) {#ex-pcb-component-ports}

A 50 Ω microstrip with a three-part pi low-pass filter, solved once with each part as a component port: a port across the part's own pads that is never drawn. The build is schematic-first. Design ▸ Update Layout from Schematic places the lines, 0603 land patterns, ground vias and pour; LVS confirms the match; the EM setup's Component ports list ticks the three parts; and Design ▸ New 3D View from Layout carries them into Palace and EMerge setups. The board solves on Planar MoM, Palace and EMerge, and a test bench closes each result with the same parts and overlays them on the schematic alone. The comparison shows what the schematic misses: pad capacitance lowers the cutoff from 1.98 to 1.70 GHz, and each capacitor's ground path resonates near 3.8 GHz, a notch every EM solver finds. Palace and EMerge agree within 0.4 dB. Changing a part value, tuning or optimizing redraws every curve without another EM run.

*Folder:* `PCB Component Ports` · *Topics:* component ports, co-simulation, low-pass filter, Update Layout from Schematic, LVS, Planar MoM, Palace, EMerge, ground via, land pattern, tuning

### [MMIC Component Ports](mmic-component-ports/index.html) {#ex-mmic-component-ports}

A small GaAs stage on the shipped 100 µm GaAs technology: an input line, a series MIM capacitor, a tee to a shunt spiral inductor, a three-pin device cell and an output line, laid out by Update Layout from Schematic with a backside via under the spiral's end and the device's source. The device is a cell of its own with a layout, a symbol and a small-signal model as its schematic, so its layout is three pads round something the EM cannot see. The EM setup cuts it out with `"*"` and cuts the MIM capacitor out by name: each leaves the solved problem, and each connected pin gets an Uncalibrated Port at its face, named `X1.G`, `X1.D`, `X1.S`, `C1.1` and `C1.2` in the Touchstone header. One Planar run of about two minutes writes the `_cosim` cell, which puts the device's and the capacitor's own models back on those ports. A Data Display lays it over the schematic run by circuit models alone: the EM answer reads about 1.4 dB less gain from 8 GHz up and a different input match, where the spiral's lumped model is past its validity.

*Folder:* `MMIC Component Ports` · *Topics:* component ports, cell instance, cut out, Uncalibrated Port, co-simulation, GaAs MMIC, MIM capacitor, spiral inductor, device cell, Planar MoM, Update Layout from Schematic

### [Via Contact Resistor](via-contact-resistor/index.html) {#ex-via-contact-resistor}

A thin-film resistor on a silicon back-end, with the film on a level of its own between Metal1 and Metal2 and contacted at each end through a Metal2 head and a via. A Metal1 line joins the input and passes under the film without touching it, and `circuitrf lvs` reads one resistor between two nets — a crossing that would short the film if it shared a level with metal. Switching the `Film` row from 50 Ω/sq to 500 Ω/sq changes the readout without changing the artwork. The substrate is stated by conductivity (10 kΩ·cm) rather than loss tangent, so its loss falls as 1/f. A planar EM setup solves all four levels at DC in a few seconds and reads 251.4 Ω against the part's 250 Ω, showing that a via-contacted film reads long by up to half a via cell at each end. The example also explains why a full-wave sweep of this stack belongs in 3D on Palace rather than in the planar solver, EMerge or openEMS.

*Folder:* `Via Contact Resistor` · *Topics:* thin-film resistor, TFR, resistive film, via contact, silicon back-end, sheet resistance, substrate conductivity, LVS, planar EM, Palace

### [Passivated Pad](passivated-pad/index.html) {#ex-passivated-pad}

A GaAs stackup with two dielectric covers over the top metal — 1 µm of conformal nitride passivation and 5 µm of polyimide — entered as a process states them, with a `PAD_OPEN` mask that cuts both away. Five numbered cells, each one EM setup, are run in order. A bare and a covered 50 Ω line show the covers raising the effective permittivity by 2.0 %. A probe pad seen through the opening is solved in 3D on EMerge, where the opening is cut from both covers, and in the planar solver, which keeps the covers as flat slabs and says so; the two read 144.0 fF and 140.5 fF. A through-silicon via on the silicon process passes through oxide and silicon as one via. A final 3D view places the die beside an alumina carrier with a bond wire landing on the opened pad, and `check` warns when a wire's ball lands under the covers instead. In 3D the passivation follows the metal's top and sides, and the polyimide fills what is left.

*Folder:* `Passivated Pad` · *Topics:* passivation, polyimide, pad opening, conformal dielectric, probe pad, through-silicon via, bond wire, GaAs MMIC, stackup, EMerge, planar EM, 3D view

## 3D EM {#em3d}

### [3D EM (Palace)](3d-em/index.html) {#ex-3d-em}

Three structures set up for Palace, the 3D finite-element solver circuitRF runs, each solving in minutes on a 16 GB laptop. Simulate offers Install Palace … the first time; Show 3D displays every model before anything is installed. A 1 mil gold bond wire between two pads gives S-parameters and series inductance beside the wBond wire kernel's answer, and its README traces the difference to where each puts its terminals. A via taking a microstrip through a ground plane is refused by the planar solver, with the message shown, and answered by Palace at the Draft preset, with the phase Draft trades away measured against element order 2. A lidded ceramic package gives capacitance and inductance matrices from electrostatic and magnetostatic solves, and its eigenmodes: the lid's first cavity mode at 23.65 GHz, checked against a closed form. Each preset's time, memory and answer is stated, so the example also shows when Draft, Standard or Accurate is enough. No results ship; tests hold the expected numbers.

*Folder:* `3D EM` · *Topics:* 3D EM, Palace, Gmsh, finite element, bond wire, via, capacitance matrix, inductance matrix, eigenmode, cavity resonance, package, mesh presets

### [3D Package (the 3D editor)](3d-package/index.html) {#ex-3d-package}

An MMIC die in a lidded ceramic package, drawn entirely in circuitRF's 3D editor. The package is drawn in mil and the GaAs die's layout in µm, each through its own technology, and the die is placed into the cavity as a cell instance with bond wires to the package's leads. The cavity's width, length and height are cell parameters, so changing one redraws the floor, walls and lid around parts that stay where they are. Its README lists every gesture used (box dimensions typed as expressions, a via array, Place Cell Instance…, ball–wedge wires, ports), and everything except Run works with no solver installed. Two Palace setups share an air box flush with the package's metal: a driven 2–30 GHz sweep, whose |S21| shows a narrow notch near 22 GHz, and an eigenmode solve that finds the lid's cavity resonance at the same frequency, within 0.3 % of a closed-form layered-cavity estimate. A table of the settings traded away shows why element order 2 and fine frequency steps are needed to see the resonance at all.

*Folder:* `3D Package` · *Topics:* 3D editor, MMIC package, bond wires, cell instance, cell parameters, cavity resonance, eigenmode, driven sweep, Palace, PEC air box, element order

### [3D Connector (booleans, fillets, STEP)](3d-connector/index.html) {#ex-3d-connector}

A coaxial connector launching onto a 50 Ω microstrip at a board edge, built from the 3D editor's geometry-kernel operations. The housing has its bore removed with Boolean ▸ Subtract… and the bore kept as the PTFE fill; a flange is exported from its own cell with File ▸ Export ▸ STEP…, imported back with File ▸ Import ▸ STEP… (its material matched by colour) and joined with Boolean ▸ Unite…; the centre pin's tip is rounded with Fillet… in Edge mode. OpenCASCADE builds all of it. Palace and openEMS each solve the launch with the fillet enabled and disabled, asking whether each solver sees a 0.1 mm rounding at all. Palace meshes it as a curved surface and finds it negligible up to 18 GHz; openEMS's grid cell is twice the radius, so it solves the edge as sharp, and `check` warns of that before the run. The example also tabulates what element order and grid density cost, and why the two solvers' |S11| and |S21| differ.

*Folder:* `3D Connector` · *Topics:* booleans, fillet, STEP import, STEP export, OpenCASCADE, coaxial launch, Palace, openEMS, staircasing, curved geometry, convergence

### [3D Wave Ports](3d-wave-ports/index.html) {#ex-3d-wave-ports}

How to set up and run a wave port, which feeds and measures a line in its own field at the air box's face, with no gap or port parasitic in the answer. Three cells use one. Launch is the 3D Connector's coax launch with its back end opened to the box face; on Palace and openEMS it shows what the lumped gap port was costing. Pair is an edge-coupled stripline whose four strip ends are each a terminal of a two-terminal wave port, solved on openEMS and held to the ideal coupled-line closed form for thru, near-end and far-end coupling. Coupled Microstrip has two strips of different widths in a closed housing, run on both solvers; Palace solves each face's two modes and converts them to one port per strip. The walk-through covers Make Port ▸ Wave, reading the voltage-path arrows and the reference conductor, and why Palace refuses a face whose two modes travel at one speed. It also explains what each solver does at the port: Palace solves the mode, openEMS extends the line and probes it.

*Folder:* `3D Wave Ports` · *Topics:* wave port, terminals, coupled lines, stripline, coupled microstrip, crosstalk, coaxial launch, modal conversion, Palace, openEMS, Data Display

### [3D Eigenmode (a cavity resonance)](3d-eigenmode/index.html) {#ex-3d-eigenmode}

Is there a cavity resonance in my band? A 50 Ω microstrip runs through an aluminium housing whose lowest mode falls inside 8–12 GHz. An eigenmode solve on Palace and on EMerge finds each mode's frequency, loaded Q, unloaded Q and where its energy sits, and the walk-through explains how to read the table: rows with a Q near 1 are the line itself, not resonances. A driven sweep in 5 MHz steps then shows the same mode as a sharp notch in |S21| at 9.955 GHz. A second cell adds one grounded post at the mode's field maximum; it shortens the cavity the mode sees and moves the resonance to 12.84 GHz, above the band, while its Q rises, which shows the mode moved rather than being damped. A saved field plot shows where each mode lives. The two solvers agree on the resonance to a fraction of a percent; the example explains why their loaded Q and notch depth differ, and why openEMS refuses an eigenmode setup.

*Folder:* `3D Eigenmode` · *Topics:* eigenmode, cavity resonance, housing, Q factor, unloaded Q, energy participation, grounded post, driven sweep, Palace, EMerge, X band

### [Capacitance Extraction](capacitance-extraction/index.html) {#ex-capacitance-extraction}

A square pad beside a 50 Ω microstrip on a two-layer FR-4 board, solved for its capacitance by Palace's electrostatic solver in about fifteen seconds. The walk-through builds an Electrostatic setup: terminals added in the Terminals table with Pick, the ground chosen from the list of remaining metal, and other metal left to float. Zero-charge (PMC) walls at the line's ends make the line effectively infinite, so its result reads as a capacitance per metre. The result holds the Maxwell matrix and the circuit (mutual) matrix a schematic wants, and its README shows how one becomes the other. Each answer is checked by hand: the pad against a parallel-plate estimate, which misses 42 % of it as fringing, and the line against the line calculator, which agrees to 0.3 %. A gap sweep shows the coupling falling as the pad moves away, and a floating-line run confirms that eliminating a floating conductor is exact. A field plot shows the fringing field round the pad.

*Folder:* `Capacitance Extraction` · *Topics:* capacitance extraction, electrostatic, Maxwell matrix, mutual capacitance, terminals, floating conductor, fringing, PMC wall, line calculator, Palace

### [Test Pad Capacitance](test-pad-capacitance/index.html) {#ex-test-pad-capacitance}

A test pad on the bottom of a six-layer FR-4 board, joined to a 50 Ω line on top through a via that keeps an unused pad in each inner plane. The board carries no nets, as if imported from Gerber files, so the terminal is picked by clicking the metal and the ground set to Every body not in a terminal. Palace's electrostatic solver gives the test pad's loading as built, and with the unused pads removed and the plane opening under the pad enlarged: the fix cuts the pad's own share by 45 %. A third run leaves the ground planes floating and reads less than half the capacitance, showing why the ground choice matters on a netless board. A Compare schematic places each extracted capacitance, the closed-form TESTPAD model and a driven 3D run on copies of the line. Below the via stub's resonance the shunt capacitor stands in for the full-wave run; above it the via's inductance takes over, which only the driven run, or the capacitance with an added via inductance, reproduces.

*Folder:* `Test Pad Capacitance` · *Topics:* test pad, via stub, electrostatic, capacitance extraction, multilayer PCB, netless board, floating ground, unused via pads, driven 3D, TESTPAD, Palace

## Antennas and Arrays {#antennas}

### [Patch Antenna](patch-antenna/index.html) {#ex-patch-antenna}

A 5.8 GHz rectangular microstrip patch with an inset feed, over a ground plane on a 30 mil laminate, set up for a full-wave planar solve. Its EM setup sweeps 5.3–6.3 GHz with resonance search and radiation pattern both on. Resonance search is the one sweep mode allowed to add frequencies between those requested, so points land where the resonance actually is rather than where the grid happened to fall. Open the `.cem` and press Simulate; no results ship with the example. Afterwards, look at the input match around resonance, the surface-current map and the pattern cuts. Fed at a single edge port, the patch is a one-port structure: its behaviour is entirely in S11 and in what the pattern shows is being radiated rather than reflected.

*Folder:* `Patch Antenna` · *Topics:* patch antenna, microstrip antenna, inset feed, planar EM, full-wave, resonance search, radiation pattern, surface current, S11, edge port

### [Antenna Co-Simulation](antenna-co-simulation/index.html) {#ex-antenna-co-simulation}

A meander antenna on a 45 × 95 mm FR-4 board, with a matching network of 0402 parts at its feed. EMerge solves it once at five frequencies with every part as a component port and the radiation pattern on, keeping a far field at each. A test bench wraps the saved run in an EM Field Data block with the parts around it, so S11, the pattern, gain, directivity and realized gain come from a schematic for any part values in under a second. The Optimizer matches the antenna in a handful of evaluations, raising realized gain by 2.8 dB while directivity barely moves: the parts decide how much power gets in, not the pattern's shape. A swept analysis shows realized gain against an inductor's error, and tuning sliders redraw the pattern as they move. A Palace run at one frequency checks the result: the match and part values agree, while gain and efficiency differ, so the gain is read as a trend rather than a design value. The air box's effect on gain is noted too.

*Folder:* `Antenna Co-Simulation` · *Topics:* antenna, co-simulation, EM Field Data, component ports, radiation pattern, realized gain, far field, matching network, optimizer, tuning, EMerge, Palace

### [Phased Array](phased-array/index.html) {#ex-phased-array}

A patch array at 5.9 GHz built from one EM simulation of a 5 × 5 sub-array, which ships with the example so nothing has to be solved first. A 16 × 16 transmit array in Feed mode steers with a `theta0` VAR through 6-bit phase shifters, tapers with a −25 dB Taylor weighting and drives every element through an identical amplifier channel, so a change of steering costs a back-substitution; a sweep of `theta0` draws the cut, the realized gain and every element's active reflection against scan, and the Phased Array tool drags the beam directly. An 8 × 8 receiver under a sky bright at the horizon gives its antenna and system temperature, G/T and SNR against scan, and an interferer swept in angle shows that the channels see the element's pattern while only the beamformer sees the array's. Four power amplifiers on a 1 × 4 array solved whole run in one harmonic-balance analysis and then under an NR 20 MHz signal, their active reflections drawn over the amplifier's loadpull contours and the radiated ACLR and EVM read in four directions.

*Folder:* `Phased Array` · *Topics:* phased array, antenna array, beam steering, taper, array factor, embedded element pattern, mutual coupling, active reflection, scan, G/T, receive array, interferer, harmonic balance, envelope, loadpull, power amplifier

### [Loop Sensor (coaxial port, connector on an antenna)](loop-sensor/index.html) {#ex-loop-sensor}

A printed split-ring loop sensor following a published open-access design, with a water sample in a reservoir over its capacitive gap, fed three ways. Ideal Port joins the loop's two arms with a lumped port. With Connector feeds it through a real-size edge-launch SMA built with the 3D editor's booleans, STEP import and fillet, its port a coaxial port across the PTFE ring at the connector's back face. Gap Fed feeds the same connector across a gap, as the 3D Connector example does. Palace and EMerge solve every fed board and openEMS the ideal port, each over a 1 MHz-step sweep. The resonance sits near 875 MHz on all of them; the connector moves it by a few MHz and about a dB in depth, and the gap port shifts it more than the whole connector does. Emptying the reservoir moves the resonance to about 2 GHz, which is the sensing mechanism. Two saved field plots show |E| concentrated in the gap and the water above it.

*Folder:* `Loop Sensor` · *Topics:* coaxial port, SMA connector, loop antenna, split-ring resonator, biosensor, water sample, connector effect, Palace, EMerge, openEMS, field plot

## SAR {#sar}

### [SAR: MRI Loop Coil](sar-mri-loop-coil/index.html) {#ex-sar-mri-loop-coil}

A 7 T (298 MHz) surface coil beside a three-shell head phantom, set up the way coil designers work: every capacitor gap is a 50 Ω lumped port. openEMS solves the coil once and keeps each port's fields. A test bench wraps the saved run in an EM Field Data block, adds the gap and match capacitors around it, and runs S-parameters plus a fields analysis that recombines the stored fields with the waves the circuit delivers, normalised per watt available. The Optimizer meets goals on S11, mean |B1+| over the brain and 10 g peak spatial-average SAR in about four seconds with no new EM run, and the Tuning panel redraws B1+ as a capacitor is dragged. Its README compares this coarse grid with a finer reference grid: B1 and psSAR10g agree within 3 %, while single-cell peak SAR reads low. Things to try include frequency-dependent tissues, a B1 uniformity measure and a yield analysis over capacitor tolerance. The SAR is for research and engineering use, not compliance.

*Folder:* `SAR MRI Loop Coil` · *Topics:* SAR, MRI coil, B1+, psSAR10g, head phantom, EM Field Data, co-simulation, per-port fields, optimizer, tuning, yield, openEMS

### [SAR: Phone Dipole](sar-phone-dipole/index.html) {#ex-sar-phone-dipole}

A phone-SAR system check: a 68 mm half-wave dipole 10 mm below a flat phantom of head tissue-simulating liquid, in a standard 1900 MHz system-check configuration. The liquid is a table of target values, so each band evaluates its own permittivity and conductivity. A band sweep, opened from 3D ▸ Draw ▸ Phantom ▸ Band Sweep… or run with `circuitrf em --bands`, makes one openEMS setup per band from a template and runs 1.75 GHz and 1.95 GHz at 0.25 W incident. The table reports accepted power, psSAR1g, psSAR10g and SAR per watt accepted beside published reference values, which a system check exceeds on purpose to give a large, repeatable number. It shows why accepted power differs between the bands when the dipole is matched at only one. The grid density comes from a convergence study. Things to try include Place Against Phantom… at another separation, a third band, and a device placed against the Generic Head. The SAR is for research and engineering use, not compliance.

*Folder:* `SAR Phone Dipole` · *Topics:* SAR, system check, dipole, flat phantom, tissue-simulating liquid, psSAR1g, psSAR10g, band sweep, accepted power, Place Against Phantom, openEMS

## Thermal {#thermal}

### [Thermal: Die to Heatsink](thermal-die-to-heatsink/index.html) {#ex-thermal-die-to-heatsink}

Power in, temperature out. A GaN-on-SiC transistor die is sintered to a copper–molybdenum flange, soldered to a two-layer board with a field of plated vias, and mounted on a thermal interface material over a heatsink held at 85 °C. A steady thermal solve sweeps dissipated power from 3 W to 11 W and reads junction temperature and junction-to-case and junction-to-heatsink thermal resistance through probes and measures. The modelling choices are shown: the transistor as one heat-source sheet, thin bond lines as contact resistances rather than meshed solids, and the heatsink as a fixed-temperature face. Rth rises with power because SiC's conductivity falls as it heats, which the run solves as a Newton loop over k(T). A one-dimensional hand check across the interface material matches exactly. The example shows how the definition of case temperature changes Rth_jc, what element order 1 costs against order 2, and how a disabled effective block replaces the via field with anisotropic conductivities, here about 14 K optimistic.

*Folder:* `Thermal Die to Heatsink` · *Topics:* thermal, junction temperature, Rth, heatsink, GaN, flange, thermal vias, contact resistance, effective block, temperature-dependent conductivity, probes, Gmsh

### [Thermal: Channel vs Surface](thermal-channel-vs-surface/index.html) {#ex-thermal-channel-vs-surface}

How hot is a GaN transistor's channel, and what does an infrared camera see? One Finger models a single gate finger's unit cell: the channel as a sub-micrometre volume heat source at the gate's drain-side edge, a field plate over it, a symmetry plane halving the model, and an IR spot disk on the passivation. The channel reads about 20 K hotter than a 3 µm spot sees, and the offset grows with spot size, so an IR reading means little without its spot. Element order 2 is kept on purpose: order 1 reads the channel-to-surface offset 9 % low. Eight Fingers places eight sources at their pitch on SiC over a carrier, gives the Rth matrix of self and mutual resistances, shows the middle finger running hotter than the edge one, and drives a radar pulse train through Z_th solved at the pulse harmonics. Each finger's Z_th is also fitted as a Foster network written as a `.cnl` you can simulate. The GaN/SiC interface resistance comes from the material library.

*Folder:* `Thermal Channel vs Surface` · *Topics:* thermal, channel temperature, IR thermography, GaN HEMT, field plate, Rth matrix, Z_th, pulsed operation, Foster network, symmetry plane, element order

### [Thermal: Output Wires](thermal-output-wires/index.html) {#ex-thermal-output-wires}

Six 1 mil gold bond wires carry a power die's drain current to a copper package lead, with everything above the flange embedded in a mould compound and the flange held at 85 °C. The wires are solved as one-dimensional conductors along their true 3D paths, with gold's resistivity and conductivity following temperature, while the package around them is meshed in 3D with Gmsh. Three thermal setups share one 3D view: a DC sweep that brackets thermal runaway (no steady state above about 16.69 A), an RF sweep at 2 GHz where the current divides by inductance and the edge wires run hottest, and a run that takes its currents from a power amplifier's harmonic-balance drive sweep through a Palace EM result of the package. Every wire probe carries a 150 °C limit, so each run reports where and when a wire first passes it. A second bench swaps the EM block for a wBond component: a sub-second upper-bound check with no 3D run. A twin view draws the wires in the 3D editor instead of reading them from the layout.

*Folder:* `Thermal Output Wires` · *Topics:* thermal, bond wire, wBond, thermal runaway, mould compound, harmonic balance, Palace, Gmsh, 3D view, temperature probe, power amplifier

## Transient {#transient}

### [Pulse Response (transient)](pulse-response/index.html) {#ex-pulse-response}

Two circuits driven by a 50 ps step and simulated in time, each drawn twice: once solved by circuitRF's own [transient](../reference/transient.html) engine and once by ngspice, the only difference being the analysis card's Solver. The first is a 5th-order Butterworth low-pass filter with a 1 GHz cut-off between 50 Ω terminations, whose measures read the output edge with the time-domain functions: a 409 ps rise time, the family's 12.7 % overshoot and a settling time to 1 %. The second is a lossless 50 Ω line, 1 ns long, into 100 Ω: the input steps to 0.5 V, and after the 2 ns round trip the reflection lifts it to 0.667 V, a reflection coefficient of exactly 1/3. Native and ngspice agree to the fourth digit on every measure. The README says how to install ngspice from Settings or the command line, where each run leaves the deck it ran, and how to run that deck by hand.

*Folder:* `Pulse Response` · *Topics:* transient, time domain, step response, rise time, overshoot, settling time, low-pass filter, Butterworth, transmission line, reflection, TDR, ngspice, SPICE

### [Buck Converter (transient)](buck-converter/index.html) {#ex-buck-converter}

A 12 V to 5.5 V buck converter switching at 1 MHz, in two benches. A voltage-controlled switch driven by a pulse source at a duty cycle set by a variable, a free-wheeling diode and a 10 µH, 1 µF filter feed a 5 Ω load, with an IProbe on the inductor current. The start-up bench begins from rest and runs 200 µs: the output overshoots by 34 %, settles to within 2 % in 35 µs and ends at 5.52 V with 40 mV of ripple. The steady-state bench takes two initial conditions from that run, the output voltage on the analysis card's `IC=` and the inductor's valley current on the inductor's own `IC`, and is steady from its first cycle, so 10 µs replaces 200 µs with the same ripple. Its README shows what happens when only one of the two stored energies is set.

*Folder:* `Buck Converter` · *Topics:* transient, switching converter, buck, DC-DC, voltage-controlled switch, VSwitch, ripple, start-up, settling, initial conditions, IC

### [Class-E with a Transient Seed](class-e-with-a-transient-seed/index.html) {#ex-class-e-with-a-transient-seed}

A 1 GHz class-E power amplifier whose FET is driven hard as a switch into the class-E shunt capacitor and series tank for 10 Ω, from 5 V. The harmonic-balance bench starts Newton from a transient run (`InitialGuess=transient`): the stage is simulated in time from its DC point at full drive until it settles, 43 periods, and harmonic balance converges from that start in 5 iterations, against 55 from the DC point with no drive stepping. It delivers 31.4 dBm at 87.9 % drain efficiency. The transient bench runs the same stage for 60 periods: the drain swings to 19 V while the switch is open and returns to zero before it closes, and its output power agrees with harmonic balance to 0.002 dB. The README covers fast bias, `InitialGuess=fallback`, and how `MaxHarm` moves the efficiency of a switching stage.

*Folder:* `Class-E with a Transient Seed` · *Topics:* class-E, switching power amplifier, harmonic balance, transient, transient seed, InitialGuess, convergence, drain efficiency, drain waveform, fast bias

## Systems and Radio Links {#systems}

### [RF Chain Budget](rf-chain-budget/index.html) {#ex-rf-chain-budget}

Four 2.45 GHz receive chains and a 26 GHz link as `.cchain` files in the RF Chain Budget window. The chains are a superhet (LNA, RF filter, mixer to a 350 MHz IF, IF filter, IF amplifier, attenuator), a four-element steered array into an N-way combiner, a front end with an interference canceller, and a dual-conversion receiver to 350 MHz and then 21.4 MHz. Every stage is a System block, and the window gives cascaded gain, NF, intercepts, compression, MDS, sensitivity, SNR and SFDR in closed form. Transfer to Schematic has already written each chain's benches: 1-Tone for harmonic-balance noise and compression, 2-Tone for intercepts, Envelope for SNR and EVM with an NR carrier, and Levels for the signal and IM3 at every node. The dual-conversion receiver's are written once per conversion, each fed the whole chain's level and noise at its IF, and a blocker on each mixer's image shows how much of it the filters reject. Further benches draw the array pattern against the closed-form array factor, compare four combiner variants, track blockers through the filters, run the canceller with real modulated signals and demodulate the superhet end to end. Each Data Display puts the simulated number beside the budget's. The array also shows why its NF is one element's while its SNR gains 6.02 dB. The 26 GHz link (NR band n258) puts a 16-element transmitter and a 200 m path in front of an 8-element receiver whose stage parameters are functions of the frequency: EIRP, the power at each element, SNR, the Shannon rate, DC power and energy per bit, swept over the carrier and both array sizes, with a TX bench for the PA's back-off against EVM and ACPR.

*Folder:* `RF Chain Budget` · *Topics:* cascade budget, noise figure, IP3, P1dB, sensitivity, SFDR, superheterodyne, dual conversion, image rejection, phased array, combiner, blockers, interference canceller, link budget, EIRP, path loss, frequency-scalable stages, Shannon rate, DC power, Transfer to Schematic

### [System Design (TX and RX)](system-design/index.html) {#ex-system-design}

A complete 2.4 GHz radio built from the System block library, with transmitter and receiver each drawn twice — direct conversion and superheterodyne — so the two architectures compare at the same band, power and parts. Every radio bench runs harmonic balance and sweeps its drive level, and each has a ready data display of output power, gain, compression, two-tone linearity and spectral purity. The transmitters reach +30 dBm into a deliberately mismatched antenna through a coupler and circulator, and show I/Q sideband rejection, LO feedthrough and passive intermodulation computed from the circulator's specification. The direct-conversion receiver simulates the DC offset produced by LO self-mixing. One SPDT T/R switch, set by its `State`, is shared by all five radio benches. One transmitter drives a nonlinear SDD FET final stage from System gain stages, and a cascade budget computes gain, IP3 and Friis noise figure in measurement expressions beside a real S-parameter run.

*Folder:* `System Design` · *Topics:* system design, transmitter, receiver, superheterodyne, direct conversion, zero-IF, harmonic balance, I/Q modulator, mixer, passive intermodulation, cascade budget, noise figure

### [Indoor IoT Link](indoor-iot-link/index.html) {#ex-indoor-iot-link}

A battery sensor sends 100 kb/s FSK at 868.95 MHz to a gateway two floors up, through a Propagation Channel. The path is ITU-R P.1238's office without line of sight at 28 m, with two floors and three walls on it, and its multipath is the office's own power-delay profile, six taps over 110 ns read from a CSV file, each fading as Rayleigh at walking pace. Each run is one drop of the shadowing and the fading, and a sweep over the drop is the Monte Carlo. The coverage bench sweeps an S-parameter run over 2,000 drops in two seconds: the received power's distribution and the outage below the gateway's sensitivity, 29 % with its exact interval, because the median sits only 3.8 dB above it. The second bench demodulates the FSK in an envelope run with noise over 50 drops and counts every bit, so bit errors can be plotted against each drop's received power. The run's notes say why the office fades this signal flat and why a 5 ms record sees a frozen channel.

*Folder:* `Indoor IoT Link` · *Topics:* propagation channel, indoor, P.1238, partitions, floors, tapped delay line, power-delay profile, Rayleigh fading, shadowing, drops, Monte Carlo, outage, outage_over, FSK, bit errors, sensitivity, IoT

### [LEO Satellite Downlink](leo-satellite-downlink/index.html) {#ex-leo-satellite-downlink}

A low-orbit satellite at 600 km sends a 16APSK single carrier at 20 GHz to a vehicle in a suburb, seen at 30° as it sets. The transponder is ordinary parts: an input filter, a travelling-wave tube amplifier as a gain block near compression, and an output filter. The Propagation Channel's satellite model gives the 1,075 km slant range, its 3.6 ms latency and the pass's 399 kHz Doppler; ITU-R P.618 adds rain, clouds and scintillation at 99.5 % availability and P.676 the gas; ITU-R P.681 puts the vehicle in a GOOD or a BAD shadowing state, drop by drop. One envelope run over 40 drops trains the receiver's equalizer on the preamble and pilot blocks, acquires and removes the Doppler, decides every symbol and reads MER, EVM, counted and estimated bit errors, and the fraction of drops shadowed too deeply to decode.

*Folder:* `LEO Satellite Downlink` · *Topics:* satellite, low earth orbit, LEO, slant range, Doppler, drift, P.618, rain, P.676, P.681, land mobile satellite, transponder, TWTA, 16APSK, single carrier, pilot equalizer, frequency offset, MER, BER, outage

### [mmWave Array Link](mmwave-array-link/index.html) {#ex-mmwave-array-link}

A 28 GHz base station with a 16 × 16 array sends NR 64-QAM to an 8 × 8 array 400 m away around a corner: 3GPP TR 38.901's street-canyon micro cell without line of sight, its multipath the report's clustered delay line CDL-C, 480 rays with directions at both ends. The arrays are built from one EM result of a patch scaled to 28 GHz, which ships with the example. In every drop the channel scores some five thousand beam pairs through that drop's rays, takes the strongest and reports what the codebook costs against the best any phase-only weights could do; the Phased Array tool's Channel tab draws the rays and lets you pick another pair. An envelope run with noise over 20 drops reads MER, EVM and bit errors through the chosen beams, equalized from pilots, and the outage. The README says what was traded to run it in a minute.

*Folder:* `mmWave Array Link` · *Topics:* millimetre wave, 28 GHz, phased array, beam selection, beam pair, codebook, TR 38.901, UMi, CDL-C, clustered delay line, NR, OFDM, pilot equalizer, EVM, BER, outage, drops

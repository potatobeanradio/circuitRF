# Findings from the example workspaces

What went wrong while authoring or reviewing an example, and what it turned out to be. A shipped
example is read as documentation, so a number in one is a claim; these are the ones that were not
true, and the traps that produced them.

Two CLI defects found the same way are recorded where the code lives, not here:
`src/Cli/RESOLVED.md` — `check` calling a `.cdd` unreadable, and a run verb's `-o` not creating
its own folder.


## System Design: what a second review found (2026-09-17)

Everything below came out of running the workspace rather than re-reading it: six benches, and then
every instruction the README gives a reader, executed as written. **Every headline number in the
file reproduces**, so what is listed here is the residue — four numbers that did not, one argument
that was right for the wrong reason, and three pieces of drawing that were unreadable.

**An agreement that was a coincidence.** The PIM section quoted the drive at the circulator from the
SHIPPED run (+28.68 dBm) and the IM3 reading from the run with six parts idealised, and put the two
side by side as arithmetic agreeing with the engine to 0.2 dB. They are different runs: ideal
amplifiers do not compress, so the level there is +29.22 dBm, and the reading is at the ANTENNA,
1.69 dB past the circulator. The product rides up 3 × 0.54 = 1.6 dB with the level and back down
1.69 dB through the output network — **two corrections of the same size, opposite sign, so the wrong
derivation lands on the right answer**. Done properly it is −110.0 predicted against −110.1 measured,
which is a better result than the one being claimed. Worth recognising as a shape: *a quantity read
at one node in one run, compared with an arithmetic chain evaluated at another node in another.*

**Four numbers that were not what the file produces.** The sideband rejection at the shipped
imbalance was quoted as 32.9 dBc in the prose and 33.0 in the table two lines above it — the file
says 33.027, and 32.853 is the `MaxMixOrder` 5 value, so an order-5 number had been left in the
order-4 text. The floor a suppressed PIM product falls to was quoted as −288 dBm and reads −284;
it is a floor, so it is now stated as one rather than to three figures. The switch was described as
specifying "0.53 dB" where the part specifies 0.5 and the measurement is 0.51. And the radians trap
— `Phase=90` read as 90 radians — was quoted at 12.5 dB of sideband rejection, which is the ideal
hybrid's answer; the bench gives 13.0.

**A section that had been pasted twice.** The last paragraph of the README repeated the passive-block
section in miniature and ended "The next section is what that cost this example" — there was no next
section, it was the end of the file. Replaced with a cross-reference to the section it duplicates.

**Three MEAS blocks overlapped their neighbours on the canvas**, which is a defect a reader meets
before any number: `TxFetFinal`'s `MeasPA` is 165 characters wide and ran 42 characters INTO
`MeasLin` and 6 into `MeasOut`, and `RxDirectConversion`'s `MeasGain` ran 8 characters into
`MeasLin`. The blocks were placed by eye on a zoomed-in canvas, where the collision is off-screen.
**The cheap check is arithmetic, not a screenshot**: a schematic text row is ~29.3 design-units per
character and ~60 units per line, so a block's right edge is `X + 29.3 × (widest row)` and its bottom
is `Y − 380 + 60 × (rows + 3)`. Every MEAS/VAR block in all seven schematics now clears its
neighbours by at least 300 units.

**`CascadeBudget`'s Friis sum was a single 275-character row** that ran off the right of the drawing
entirely, and the block's last rows fell outside the fit, so zoom-to-fit clipped them. The extents a
schematic reports are its component bounding box ± 400 units and **do not account for text**, which
is what lets a long measurement row leave the page silently. Rewritten as `F0`…`F8`, one term per
stage, summed in `Ftot` — `Ftot` and `NF_dB` come back bit-identical, and the split says something
the one-liner could not: the T/R switch, the preselector and the LNA are **96.8 %** of the noise
figure, and everything from the mixer onward is 1.7 %. That is the Friis result as a design
instruction, and it is now visible in the file rather than only in the prose.

**What was checked and was right**, since a review is only worth as much as its negative results:
all six benches converge and no block trips the passivity check; every cube a `.cdd` trace names
exists in the result it reads; no two plots overlap in any display and the secondary-axis traces are
on the secondary axis; the IM3 mixing indices land on the frequencies the prose claims for them
(2444 / 2439 / 4 / 373 MHz); the `+10` and `+6.9897` dBm constants match the 50 Ω and 100 Ω loads
they are used at; the Friis and IIP3 cascades match the component values stage for stage; the
`−110.1 dBm` PIM, the `−56.1 dBm` four-part variant, the `+30.34 / +29.99 / +30.50` switch triple, the
four I/Q-imbalance figures, the `2.2 dBc` raw-mixer feedthrough, the `−48.21 dB` Chebyshev image and
the `5.99 dB` reflective-stopband reading all reproduce exactly as written.

## System Design: three "passive" blocks that were not (2026-09-17)

circuitRF now checks the claim every System block but the amplifier makes about itself, and reports
`system.block-not-passive` to the Messages panel naming the instance. Turning it on found three
blocks in this workspace. The mechanism and the check are recorded in `src/Core/RESOLVED.md`; what
belongs here is what it cost the example and how each one was chosen.

| Block | Was | σ_max | Now | σ_max |
|---|---|---|---|---|
| `SW1` T/R switch | 0.5 dB IL, 28 dB iso, **18 dB RL**, reflective | 1.100 | 0.5 / 28 / **26 dB**, absorptive | 0.997 |
| `CR1` circulator | **0.4 dB** IL, 22 dB iso, 20 dB RL | 1.134 | **0.6 dB**, 30 dB iso, 32 dB RL | 0.990 |
| `SPL` in-phase RF splitter | 3.01 dB coupling, **0.2 dB IL** | 1.382 | 3.01 dB coupling, **3.01 dB IL** | 1.000 |

**Every one of them is a datasheet a real part could carry.** That is the finding: the blocks build
their S from real, in-phase amplitudes, so an insertion loss, a return loss and an isolation that are
each individually plausible add coherently. A ferrite circulator with 0.4 dB of loss and 22 dB of
isolation is an ordinary part, and in this model it is 1.10 dB over unity before its return loss is
even counted.

**The switch's off-state mattered more than its return loss.** `OffState=Reflective` gives an open
throw `S_pp = 1` exactly, and then any leakage into it puts the matrix over unity whatever else is
true — 0.5 dB / 28 dB isolation reads σ_max = 1.033 at INFINITE return loss. Absorptive was free
here: every throw in every bench is terminated externally in 50 Ω, so the two off-states produce
results identical to the last printed digit (measured).

**The splitter could not be fixed the way one would want**, and that is a theorem rather than a
limitation of circuitRF: a matched, lossless, reciprocal four-port must put 90° (or 180°) between its
two outputs. A real in-phase splitter is a Wilkinson — a THREE-port with a resistor in it — and this
family has no three-port divider, so the only in-phase split a `Coupler` can honestly represent is a
resistive one at 3.01 dB. Putting the quadrature on the RF side and the in-phase split on the LO was
tried and only moves the problem: exactly one of the two 3 dB splits in an I/Q demodulator is
in-phase, and that one has to be lossy.

**What it cost.** Transmitter output 30.01 → 29.27 dBm, `TxFetFinal` 31.46 → 30.10 dBm and its PAE
26.3 → 22.9 %, zero-IF receiver gain 35.25 → 32.43 dB. All of it power the old numbers were
manufacturing. **What it bought**, and the cleanest check that it was real: `CascadeBudget`'s
antenna-port match read **+1.17 dB out of band** across 509 of its 601 points and now reads −0.10 dB
or better at every one.

**Both transmitters output +30.0 dBm again.** `A4` carries 0.8 dB more gain in
`TxDirectConversion` (14 → 14.8) and 0.9 dB more in `TxSuperhet` (14 → 14.9). The LAST
amplifier is the right place for it, measured — the same 0.73 dB put on `A3`, on
`A1` or in the `AT1` pad costs a further 0.13-0.18 dB of IM3, because then every stage after it
works harder too:

| where the 0.73 dB goes | Ptot | IM3 | OIP3 |
|---|---|---|---|
| `A4` 14 → 14.8 | 29.992 | 33.891 | 43.934 |
| `A3` 14 → 14.8 | 29.983 | 33.749 | 43.854 |
| `A1` 20 → 20.8 | 29.981 | 33.716 | 43.835 |
| `AT1` pad 3 → 2.2 dB | 29.981 | 33.721 | 43.838 |

It is not free at any of them: the same amplifiers deliver 0.8 dB more, `OIP3` barely moves, and
`IM3 = 2·(OIP3 − Pout)` takes the difference — 35.2 → 33.9 dBc. Raising `A4`'s own `IP3` from 47 to
48 dBm recovers the linearity, at the price of specifying a better part; it is left at 47, so the
bench shows the trade rather than hiding it.

**The zero-IF receiver's 2.8 dB was deliberately NOT put back.** It is the resistive splitter's
dissipation, sitting after the LNA and the second gain stage; adding gain in front of it would move
the noise figure rather than fix it.

**The T/R switch's return loss is a VAR now** (`SWrl`, beside `SWil` and `SWiso`), because the three
are not independent: a symmetric two-port needs `RL ≥ −20·log₁₀(1 − 10^(−IL/20))`, which is 25.1 dB
at 0.5 dB of loss and 19.3 dB at 1 dB. Leaving one of them per-instance while the other two were
globals would have made that relationship unreachable from the place a user edits.


## System Design: an efficiency number that was three different quantities (2026-09-17)

`TxFetFinal`'s measurement block reported

```
  Ppa_W   = real(0.5*V("drn","(1,0)")*conj(I("Iout","(1,0)")))     ; ONE carrier
  PAE_pct = Ptot_W/PDC_W*100                                        ; TWO carriers, at the ANTENNA
```

so the display carried a "PA fundamental" of 1.01 W beside an antenna power of 1.40 W — **more RF
leaving the transmitter than left the drain**, through a filter, a coupler, a circulator and a
switch. Nothing flagged it, because each expression is individually correct; they answer different
questions and were read as if they answered one.

The label was wrong twice over. `Ptot_W/PDC_W` is neither PAE nor drain efficiency: it charges the
output network's 1.85 dB to the device and ignores the drive entirely. It read 18.3 %, the device's
real PAE is 26.3 %, and its drain efficiency is 26.4 % — three numbers within 8 points of each other,
any of which looks plausible on a slide.

Fixed by summing both carriers in `Ppa_W`, adding `Pin_W` off the gate-side `IProbe` that was
already on the schematic, and writing `PAE = (Ppa − Pin)/PDC`. (The three numbers above are what the
file produced at the time; the passivity fixes recorded in the entry above moved them to 13.3 %,
22.9 % and 23.0 % on the same three definitions. The relationship between them is the point, not the
values.) **The shape to be suspicious of is a
measurement that names a port in its expression and a different one in its identifier** — a
per-carrier quantity and a both-carrier quantity differ by exactly 3 dB, which is small enough to
look like a loss you had forgotten about.


## System Design: three README exercises that did not reproduce (2026-09-17)

Each was written from a real run and then invalidated by something the prose did not mention. All
three were caught by running the instruction as written rather than re-reading it.

- **"Set the four amplifier `IP3` fields to 200 and re-run: the IM3 bin holds −111.9 dBm."** It
  holds −56.2 dBm, because the two mixers are still nonlinear at `IIP3 = 15 dBm` and are then the
  loudest thing in that bin. Six parts have to be idealised, not four. The −111.9 figure was right;
  the recipe for reaching it was not.
- **"Set `IQgain` and `IQphase` to 0 and the sideband disappears into the solver's own floor — the
  cancellation is exact."** The cancellation IS exact; what the sideband falls to is 64.7 dBc, and
  that number is the harmonic-balance spectrum's truncation floor, not a floor of the solve. It
  reads 64.7 / 74.8 / 97.2 / 105.1 dBc at `MaxMixOrder` 4 / 5 / 6 / 7. Idealise the amplifiers as
  well and it goes to 165 dBc.
- **"A Chebyshev of the same order rejects about 143 dB out there."** Swapping the preselector's
  `Response` gives a front-end S21 of −47.5 dB at the image — 75.0 dB of image rejection against the
  elliptic's 49.1, not 94 dB better. The argument for `Astop` survives; the number did not. (75.7
  against 49.8 after the passivity fixes.)

**The general lesson is the one the third bullet makes twice: a suppressed quantity is not measured,
it is truncated.** `TxSuperhet`'s sideband rejection and LO feedthrough were quoted as 151 and
132 dBc; they read 141/122, 151/132 and 189/139 at `MaxMixOrder` 4, 5 and 6. Everything physical in
the same run — output power, gain, IM3, OIP3, T/R isolation — settles to better than 0.1 dB by
order 4. **If a rejection figure moves when you change the spectrum, it is a property of the
spectrum.** The README now says so instead of quoting it.


## System Design: what actually makes a system-level HB slow (2026-09-17)

It looked like a convergence problem and is not one. Instrumented with `--diag`:

```
TxDirectConversion, one point:  (3 tones, M=116, APFT S=464)
[HB trace] Pin_dBm  Iters  Converged  FinalResidual  Backtracks
[HB trace]      0.0      7  YES       5.555E-016           2
```

Seven Newton steps to 5.6e-16, one solve, no drive ramp. The cost is the **spectrum size**, and it
is cleanly cubic in it — a dense block Jacobian over (nonlinear nodes × mixing indices):

| `MaxMixOrder` | indices M | iterations | backtracks | time / iteration |
|---|---|---|---|---|
| 3 | 32 | 26 | 75 | ~10 ms |
| 4 | 65 | 17 | 35 | ~94 ms |
| 5 | 116 | 7 | 2 | ~470 ms |

`(65/32)³ = 8.4` against `94/10`; `(116/65)³ = 5.7` against `470/94`. **The iteration count goes the
other way**: a smaller spectrum is a worse-conditioned problem, so truncating harder is not simply
cheaper, and reading "26 iterations with 75 backtracks" as a convergence defect would point at the
solver instead of at the analysis setting.

Three tones is what costs: the LO is a tone, so every mixer bench here is 3-tone. `MaxHarm` is inert
at these settings — it never binds before `MaxMixOrder` does. Loosening `Tol` from 1e-8 to 1e-3 buys
one iteration, because the convergence is quadratic at the end.

The four 3-tone benches now run at order 4 and take 5-11 s each instead of 25-51 s. `TxFetFinal` is
2-tone and stays at 5: order 4 moves its output power 0.33 dB and its PAE 1.3 points, because it
carries a real compact model rather than behavioural tiles, and it costs 3 s either way.


## System Design: a passive cascade with |S11| > 1 (2026-09-17)

`CascadeBudget`'s antenna-port match plot reads **+1.17 dB out of band** — 15 % more power reflected
than arrives, from a switch and a filter with no gain between them. It reproduces in four lines:

```
Port:P1 ant 0 Num=1 Z=50 Ohm
Switch:SW1 ant 0 txarm 0 rxarm 0 State=2 Throws=2 IL=0.5 dB Isolation=28 dB OffState=Reflective Z0=50 Ohm RL=18 dB
R:Rtx 0 txarm R=50 Ohm
Filter:FPRE rxarm 0 n1 0 Response=Elliptic Form=Bandpass Order=3 F1=2400 MHz F2=2483.5 MHz Ripple=0.1 dB Astop=45 dB IL=2.0 dB
```

**Not a bug — `SwitchModel`'s own header says so**, and the arithmetic is worth writing down because
the symptom appears two components away from the cause. The switch stamps a real, frequency-flat
S-matrix: `S11 = S22 = 10^(−18/20) = 0.1259` and `S21 = S12 = 10^(−0.5/20) = 0.9441`, both real and
positive. Its port powers are fine (`|S11|² + |S21|² = 0.907`), so it measures as passive on its own;
but a symmetric reciprocal two-port's singular values are `|S11 ± S21|`, and `0.1259 + 0.9441 =
1.070`. Put a stopband behind it — `Γ_L ≈ 1` — and `Γ_in = S11 + S21·S12/(1 − S22)` = 1.145, which is
the +1.17 dB read off the plot.

A real part with 0.5 dB of loss and 18 dB of return loss is realizable; it just cannot have those two
in phase. The same magnitudes with `S11` in quadrature give singular values of 0.952 and are passive.
**The thing worth recognising is the shape**: an ideal-S-block tile with an in-phase reflection term
is slightly active by construction, and it only shows when something reflective is placed behind it.
Nothing inside a passband is affected.


## Every example: a size that was never in the repository (2026-09-17)

The System Design folder measured 8.0 MB on disk, of which 7.5 MB was six `.npy` result files and
12 KB was `.DS_Store`. **None of it was ever committed or ever shipped** — the repo-root
`.gitignore` excludes `examples/*/results/` and `.DS_Store`, and the `csproj` item group excludes
`results/**` from build and publish (`ExampleWorkspacesTests.TheItemGroupShipsTheExamplesAndNotTheirResults`).

Worth recording because the folder size on a working machine is not the artifact's size, and the
gates that make that true are easy to assume rather than check. Deleting them locally took the
working copy to 500 KB and cost nothing — except that it immediately exposed the `-o` folder bug in
`src/Cli/RESOLVED.md`, which had been masked the whole time by `results/` already existing.


## LVS: what building the proving designs found (2026-09-21)

`examples/LVS/` is brief 5 of the LVS series — a correct 3 dB attenuator, the same board with six
named faults in its artwork, an MMIC bias tee, and a bench that runs. It exists to be the ORACLE
the rest of that series is gated against, so everything below came out of making three designs
that circuitRF itself accepts rather than out of reading anything.

### A `Net` stamp on copper that touches unnamed copper is a spacing violation

**The headline, because it affects every board anyone draws with footprints on it.**
`DrcRegions.BuildConductors` groups a layer's geometry into conductors by the `Net` NAME stated on
each shape, and puts everything unnamed into connected components. Named and unnamed copper that
are physically CONTINUOUS therefore become two conductors, `MinSpacing` (default `NetScope.Any`,
every pair a candidate) inflates both and finds them overlapping, and the overlap is reported as a
clearance violation of zero — on artwork that is entirely correct.

It is not avoidable by stamping more: a land pattern lives in a shared cell and **can never carry a
board net** (R-ab2-2a — a `Net` inside a `C0402` would put every capacitor on the board on one
net), so the pad a named trace runs onto is unnamed by construction.

Six violations on a four-part board, each one a named trace against the unnamed stub that joins it
to a pad. **`examples/Power Rail` misses it only because `pcb-4layer-1p6mm.ctech` declares
`DrcRules: []`** — it stamps `+3V3` and `GND` on copper that touches unnamed lands throughout, and
nothing measures it. The trimmed 2-layer technology here declares ordinary width and spacing rules
and is the first board in the repository to have both.

Not fixed here: the LVS series' own scope says it changes no `DrcEngine` behaviour, and this is a
DRC defect rather than a fixture one. The fixture works around it by stamping only the board's
bottom ground pour, which is the one shape on its layer and so has nothing to overlap. **The fix,
when someone takes it:** build conductors from CONNECTIVITY and hang the stated name on the
component, rather than treating the name as the grouping key. A conductor is a connected piece of
metal; its name is a label on it.

### A land pattern's placement is `DeviceKind.Cell`, and its schematic component is not

`DeviceTypes.OfLayout` falls through to `DeviceKind.Cell` for any placement whose `CellRef`
resolves and which declares no `PartKind`, and **Update Layout writes no `PartKind`** — it writes
`SchematicId`, because "the schematic knows". The schematic side of the same part is a plain
`Resistor` symbol with a `Footprint` parameter, so `DeviceTypes.OfSchematic` answers
`DeviceKind.Resistor` with no cell directory at all.

`DeviceType.CouldBe` then compares `Cell` against `Resistor` — neither is `Unknown`, they are not
equal — and **vetoes the pair**. On the ordinary flow the brief itself calls the easy path (every
component carries a footprint and a designator, and the layout came from Update Layout), every
device on the board is type-incompatible with its own schematic component.

Recorded rather than fixed for the same scope reason, and because it is brief 7's decision which
way to take it. The two candidate fixes: treat `DeviceKind.Cell` as compatible with any kind when
only ONE side resolved a directory (it means "nothing more specific said", which is what `Unknown`
already means), or have the layout side read the kind through the resolved cell. Detail in
`src/Design/RESOLVED.md`.

### F5 splits the ground in two, and three was never reachable

The brief asks the deleted stitching via to leave **three** islands. It cannot, at any geometry:
`DrcConnectivity.FirstTouching` returns at most ONE piece per conductor, so on a two-layer board a
via barrel is an edge of degree two, and removing one edge of a tree splits it into exactly two
components. The fixture produces two, the gate asserts two, and the generator's own comment says
why so nobody "fixes" it later.

### The MMIC's parts are committed cells with a `PCellOrigin`, not kit-generated cells

A Python PCell kit's cell lands under `.generated-cells/<generator>_<hash>/` with a layout and a
`.ccell` and **no symbol view** — so `TerminalMap.Resolve` reaches its "this cell has no symbol
view" case and returns `None`. Brief 1's R-lvs1-5b (the PCell path writes the block) is not
implemented, so every placed kit part in the repository today is unmatchable by LVS.

An MMIC fixture built on a kit would therefore be measuring the absence of that writer rather than
the comparison, so `parts/` holds ordinary committed cell folders whose primary layout carries a
`PCellOrigin` and whose `.ccell` declares its terminals. The generator is `Bias tee.gen.py`, its id
is recorded in each cell, and the geometry is reproducible from the requested values. **When
R-lvs1-5b lands, this is the fixture that will prove it**: the same four parts, through a kit,
should produce the same four terminal maps.

### Two traps in authoring a workspace by hand, both silent

- **A sub-cell drawn on a different process needs its own `TechRef`.** Without it the cell resolves
  against the WORKSPACE default — here the board process — so a 10 µm spiral track is measured
  against a 0.15 mm copper rule and every part refuses, while the cell that PLACES them reports
  "drawn against a different technology and its layers have not been mapped". Both messages are
  correct and neither names the cause.
- **A cell placed in a schematic needs a symbol view.** `CellSymbolResolver` resolves the cell's
  primary `.csym`; with none, `GetEffectivePortDefs` falls back to built-in placeholder geometry
  and the instance's pins are not where the auto-generated symbol would draw them. The wires reach
  nothing, the extraction succeeds, the netlist is well formed — and the DUT is simply not in the
  circuit. It presents as S21 = 0 dB and S11 = 1 across the whole band, which reads as a broken
  model rather than a disconnected part.
- **Python's `json` escapes non-ASCII in lower case and `System.Text.Json` in upper.** A `.ccell`
  written by a script with `Ω` in it comes back `Ω` where circuitRF writes `Ω`, and the
  shipped-cell round-trip gate fails on a difference nothing can see.

### The property tolerances, measured (brief 5 R-lvs5-4)

**The deliverable the owner asked for, and the reason brief 10 depends on this brief.** The
tolerances are measured off `Bias tee/`, not chosen. Each part's geometry is solved for the value
the schematic asks for and then SNAPPED to the drawing grid (0.25 µm), and what is left is the
quantisation a correct design carries. That is the floor: a tolerance under it rejects good
artwork.

| part | dimension | requested | resolved | spread |
|---|---|---|---|---|
| `MIM-0P8P` | capacitance | 0.8 pF | 0.79844 pF | **0.195 %** |
| `MIM-4P0P` | capacitance | 4.0 pF | 3.99861 pF | 0.035 % |
| `SPIRAL-1N2` | inductance | 1.2 nH | 1.19985 nH | 0.013 % |
| `TFR-62R` | resistance | 62 Ω | 61.875 Ω | **0.202 %** |

**The two bounds, recorded so the number is not a magic one by the next release:**

- **Floor — 0.202 %**, the worst spread of a correct design on this grid. Anything at or under this
  fails artwork that is right.
- **Ceiling — 2 %**, the smallest error worth catching. A one-preferred-value slip is the smallest
  mistake anyone makes on a passive: 294 → 301 Ω is E96's next step at 2.4 %, and the E24 and E12
  steps above it are 5 % and 10 %. A tolerance at or over 2 % would pass a part swapped for its
  neighbour in the series.

**Proposed default: 0.5 % on every continuous dimension** — resistance, capacitance, inductance,
length, width. Comfortably above the observed 0.202 % and comfortably below the 2 % floor of a real
error, and round enough to read.

**Zero on everything discrete.** An integer (a turn count, a finger count, a multiplicity) and an
enumeration (a metal layer, a connection style) are exact on both sides or they are different, and
a tolerance there hides a class of error rather than absorbing noise. Likewise a DERIVED parameter
that both sides compute from the same geometry with the same generator: it agrees exactly, and a
non-zero tolerance would let a genuine geometry change through.

**They are provisional and can be tuned later** (owner). They live in one table, are overridable
per technology (brief 10 R-lvs10-3), and every report prints the tolerance it applied — so a wrong
default is visible rather than latent. `ProvingDesignTests.TheMeasuredSpreadStaysUnderTheProvisional
Tolerance` re-measures the spread on every run and fails if a generator change widens it past
0.5 %, which is what stops the floor and the default drifting past each other in silence.

# Findings from the example workspaces

What went wrong while authoring or reviewing an example, and what it turned out to be. A shipped
example is read as documentation, so a number in one is a claim; these are the ones that were not
true, and the traps that produced them.

Two CLI defects found the same way are recorded where the code lives, not here:
`src/Cli/RESOLVED.md` — `check` calling a `.cdd` unreadable, and a run verb's `-o` not creating
its own folder.


## Every design is a cell, and no example drives a run from a bare netlist (2026-09-29)

The owner found that Thermal Output Wires drove its `FromHB` setup from `Amplifier/Amplifier.cnl` —
a netlist no user can open as a drawing — and that several examples kept a `.clay` or `.c3d` in a
plain folder. Both teach the wrong way to build a design, because an example is read as the way to
build one. Nothing was re-run; every change is a rename, a new file or a repointed reference.

**Ten folders were not cells.** `3D Connector/Board`, `3D Package/Thru die`, `Thermal Channel vs
Surface/{One Finger, Eight Fingers}`, `Thermal Die to Heatsink/{Board, Die to Heatsink}` and
`Thermal Output Wires/{Die, Pads, Output, Drawn Wires}` had their views in the right sub-folders
and no `.ccell`, which is the one file that makes a folder a cell (`ProjectTreeNode`: "a cell folder
(has a .ccell at its root)"). Each now has the `.ccell` `circuitrf new cell` writes — byte for byte,
generated rather than typed. **The placed layouts stay cells of their own.** A 3D view CAN place its
own cell's layout (`C3dHierarchy.NewFromLayout` writes `CellRef ".."`), but every one of these is a
different physical part placed into an assembly — a board under a flange, a die in a package, a pad
frame under a mould — which is exactly what cell hierarchy is for.

**The amplifier is a test-bench cell now**, `Amplifier/schematic/Amplifier.csch`, drawn from the
netlist and proved the same circuit without simulating it: `circuitrf elab` of the old `.cnl` and of
the new schematic gives **13 of 13 elements identical** — same node numbering, same resolved values,
the SnP resolving to the same absolute path — and the measures and the sweep are the same lines.
`ThermalCircuitLink.Describe` resolves the link to `SW1` and `XOUT` with ports 1/2 on `drain`/`lead`.
**Three traps, each of which would have changed the committed numbers in silence:**

- **The registry's defaults are not the engine's.** A placed Angelov FET carries `Cgs` = 1 pF and
  `Cgd` = 0.1 pF; the model defaults both to 0, which is what the `.cnl` ran with. A P1Tone placed
  from the PowerAmplifier's template carries `Z[0]`/`Z[2]` too. So every instance carries ONLY the
  parameters the netlist stated — the extractor writes what is on the instance and nothing else.
- **`Temp` written explicitly pins the device** against the thermal run's ambient
  (`Temperature.ResolveDeviceC`: an explicit value wins). Left out, as the netlist left it out.
- **Two bases.** The SnP's `File` is workspace-relative in a schematic (`SchematicCircuit
  .ReferenceBaseOf`) and was `.cnl`-relative before, so `../results/…` became `results/…`; the
  link's `Schematic` is relative to the `.c3d`'s folder (`ThermalCircuitLink.SchematicPath`).

`circuitrf check` does NOT validate a thermal setup's circuit link — pointing it at a file that does
not exist still checks clean — so `Describe` is the evidence here, not a clean check.

**Two cells' views were named differently from the cell:** `Power Rail/Sensor board/layout/Board.clay`
and `LVS/Attenuator broken/*/Attenuator.*`. Both are renamed after their cell, with their generator
scripts. The board's companions carry the board's name (`P JOB`, `# Board:`), and
`NetlistBoardVerbTests` holds them to what `circuitrf netlist` projects, so they were re-projected —
an extraction, not a run; only those two lines changed. The broken attenuator's schematic is still
byte for byte the correct one's; only its file name moved.

**What was checked and left alone.** `.cdd` files at a workspace root are where the application
itself saves an authored Data Display (`ResultsWriter.AuthoredDisplayPath`: `<baseDir>/<key>.cdd`),
and a `.crail` beside its board's cell is where Save As opens (`RailRfViewModel.SuggestedSaveFolder`).
`3D EM/Package`'s second layout is a named second view the `.ccell` states as `PrimaryLayout`. A
`.csmith` is a workspace document, not a view.

**A results reference cannot exist in a fresh copy.** `EveryFileAnExampleNamesResolvesAgainstItsWorkspaceRoot`
now accepts a `results/` reference only when it is exactly what one of the example's own 3D views
writes (the thermal link's own `ResultPaths`/`IsResultOf`). `EveryDesignDocumentAnExampleShipsIsAViewOfACell`
holds the rule this entry is about.


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

**Fixed in brief 7** by the first of the two candidates: `DeviceKind.Cell` is compatible with any
kind where only ONE side resolved a directory, because it means "nothing more specific said", which
is what `Unknown` already meant. Where both sides resolve a directory the directory still decides.
A third candidate — resolve the schematic's `Footprint` into a cell directory so both sides have one
— was rejected and must stay rejected: it turns F6 into a type mismatch and loses the value that was
actually wrong. Detail in `src/Design/RESOLVED.md`.

### The MMIC does not compare clean, and the spiral inductor is why

`Bias tee` reports exactly one `lvs.net.short`. A spiral inductor is one continuous piece of metal,
so a galvanic extraction reads its two terminals as one net and the comparison correctly says that
two schematic nets are one piece of copper. Nothing here is a fixture defect — the missing rule is
that a recognised DEVICE's internal copper is not interconnect, which is brief 3's and brief 14's.
This cell is the first fixture in the repository that could show it: a board's two land-pattern pads
are separate copper, and a MIM capacitor's plates are on different conductors.

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


## 3D EM: what building the example measured (2026-09-26)

brief-em3d-30. Three cells (a bond wire, a via through a plane, a lidded package) and seven setups,
every one run on the F0 Mac (Apple M4, 10 cores, 16 GB, Palace 0.18.1 on 10 ranks, Gmsh 4.15.2). The
key numbers live in `examples/3D EM/expected-numbers.json`, which `Em3dExampleTests` re-runs and which
the README and `docs/user/src/reference/em-3d.md` must quote verbatim.

**No one preset is right for all of them, and the one that is right is decided by the metal, not by
the frequency.** Measured, per cell:

- *Bond wire* (a 25 µm wire in a 3 × 2 × 1.1 mm box): Draft takes 5 s but reads 813 pH and −0.514 dB
  at 10 GHz, 10 % short on inductance. Standard: 179 s, 3.1 GB, 906.1 pH, −0.765 dB — within 0.2 pH
  and 0.002 dB of F0's independent hand-built hex-foot model. Accurate: 512 s, 7.0 GB, 907.9 pH. The
  starting mesh around small metal is what refinement exists to fix.
- *Via* (10 × 5 mm board, 1.5 mm of air): Draft 62 s, 4.8 GB, |S21| within 0.1 dB of F0's order-2
  model but phase 12° off at 10 GHz and 23° at 20 GHz. Element order 2 on Draft's mesh: 392 s,
  9.2 GB, within 0.02 dB and 5°. Standard was already measured by brief 21 at 35 min. Draft ships;
  the README states both alternatives.
- *Package statics*: Draft is 2–3 s and badly wrong — C 831 fF against Standard's 586 fF (+42 %), L
  1.66 nH against 1.98 nH (−16 %). A static solve has no wavelength to size elements by, so the
  first mesh is coarse. Standard costs 8 s and 22 s.
- *Lid eigenmode*: Draft 13 s puts the cavity mode at 23.39 GHz with Q 176. **Standard was stopped
  after 28 minutes in its last refinement pass**: the time is Palace's quasi-Newton refinement of each
  eigenvalue (the gold's surface impedance makes the problem nonlinear in frequency), and it slows as
  the refined mesh grows — at 222k unknowns the linear solve inside it fell about 1 % per iteration.
  Draft with `ElementOrder: 2` and no refinement: 166 s, 1.7 GB, 23.655 GHz — 0.2 % from the
  thin-cavity closed form (23.68 GHz, base and air in series) — and Q 1,662, so **Draft's Q was ten
  times low**. That setting ships.

**The planar via's refusal is a `check` ERROR, not only a run refusal.** The brief expected it to be a
run refusal only. `check` on `Via planar.cem` runs the planar extraction and reports the refusal
sentence as its one error, and `em` refuses with the same sentence. Both are gated
(`Em3dExampleTests` gate 2 and gate 6). The README says the error is there on purpose.

**A workspace `check` never opens a `.cem`.** `check <workspace>` walks cells and their three view
folders and counts 11 documents here; none of the seven setups under the cells' `em/` folders is among
them, so the planar via's error does not appear in a workspace check at all. Gate 2 checks each `.cem`
by name. Not fixed here (this brief adds no capability); worth a `check` follow-up, since a user
running `check` on a workspace reasonably expects its EM setups to be in it.

**`explain`'s size estimate cannot see these problems.** It prices each region's volume at its largest
element, and says so; for the via it prints 0.07 GB against a measured 4.8 GB, for the bond wire
about 0 GB against 3.1 GB. Brief 21 already found that refinement IS the mesh on small metal, and the
after-Gmsh check is the real one. So gate 3 asserts the pre-run verdict against 16 GB as the brief asks,
and ALSO that every peak the README states is under 75 % of 16 GB, and gate 5 asserts that the run's
own after-meshing check raised no warning.

**F0's zero-thickness pads became 3 µm.** A zero-thickness conductor makes `check` warn that the
stackup "still needs a thickness", so the bond-wire and package technologies state 3 µm of gold on the
pads and ground. Both are sheets in the 3D model (thinner than a few skin depths) either way.

**Every document is ordinary.** The workspace and cells were made with `circuitrf new`; the
technologies, layouts, `.wBond` files and setups are hand-written JSON in the formats' own spelling,
checked with `check` and `explain`. The two package layouts differ only by a via from each die pad to
the floor: magnetostatics needs a closed current loop, and the same via would short an electrostatic
terminal to ground, so the inductance runs on `Package shorted.clay` (with its own stem-paired
`.wBond`).

## brief-em3d-70 — the 3D Connector example (2026-09-27)

**The coax end is a gap port, not the annular port the brief named.** A `.c3d`'s ports are rectangles (`C3dPort`
has no annulus; brief 22's `Em3dAnnulus` exists only for the `.cem` path and faces z only), and openEMS lowers only
rectangular lumped ports. So P1 bridges a 0.3 mm gap between the bore's floor and the pin's end. The gap is a small
capacitance across the port, the same in every run, so the A/B comparisons stand; the absolute |S11| carries it. The
brief's second external check — the closed-form coax Z₀ against the impedance Palace reports at the port — needs a
wave port (Palace's Z_PV), which would rule openEMS out; the README states the closed form as a reference only.

**The fillet is electrically negligible here, and the example says so rather than enlarging it.** Palace sees
≤ 0.25 dB in |S11| (at 18 GHz, 32 dB down), the same sign at element orders 1 and 2, while order alone moves |S11|
by up to 8.8 dB. openEMS's fillet cell is 110.7 µm (113.0 µm at 30 cells per wavelength — the grid near the pin is
set by grading, not wavelength), so it solves the tip as sharp; its variants differ because the fillet's faces add
grid lines (510,291 vs 449,748 cells), by about as much as one step of refinement.

**The recorded numbers came from a programmatic probe, not from `Launch.c3d` itself.** They are the shipped file's
because the probe's document and the authored one were shown to hand Palace (geometry, groups, kernel BREP hashes,
config) and openEMS (the whole CSX model) byte-identical inputs, with the fillet enabled and disabled. The first
authored file did NOT: see src/Design/RESOLVED.md on a VAR's unit scaling its names — the pin had sunk 200 µm.

**The regression table holds magnitudes only.** Gate 6 re-runs the four shipped runs and holds every |S11|, |S21|,
|S22| at every frequency (±0.05 / ±0.005 / ±0.05 dB). The table was built from the |S| in dB each run printed; the
Touchstones were not kept, so phase is not held. Re-recording it after a deliberate change is a copy of the run's
own output into `expected-numbers.json`.

**The shipped STEP file names the circuitRF version** in its FILE_NAME originating system, so gate 3 masks it
beside the time stamp — otherwise every release would fail it. The OCCT processor's version is not masked.

## brief-em3d-81 — the thermal examples (2026-09-28)

Two of the brief's three examples shipped: **Thermal Die to Heatsink** and **Thermal Channel vs Surface**. The third, *Thermal
Output Wires*, was dropped at the owner's request and is `docs/sonnet-briefs/brief-em3d-85-output-wires-fast.md`: the coupled
conductive-balance solve took minutes per point on models the wires are a small part of (never finished on four attempts,
iterative and direct), and ground-referenced ports are refused. Two defects it found are fixed (`src/Design/RESOLVED.md`).

**The thermal mesher sizes each solid uniformly from its thinnest dimension**, so a thin, wide layer is the whole cost. The
first *Die to Heatsink* (2 µm GaN over the die, 20 µm sinter, 50 µm solder, 35 µm copper over 12 mm) had Gmsh at 2.8 GB after
10 minutes, unfinished. What shipped: the bond lines as `ContactResistances` (t/k), no epitaxy at die scale, 70 µm copper,
`MinThroughThickness` 1, order 1 with `SizeFromSources` 12 — 53 s. The owner set the budget (about 6 min at most; 20 is too long)
and the operating point (85 °C heatsink, die surface under 225 °C): 3 → 11 W reads 207.7 °C (order 1) / 210.7 °C (order 2).

**The mesh check does not see the order-1 error.** `Mesh.Check` (sizes × 0.7) moved the die top +0.18 %, while order 2 on the
same mesh moved it +1.4 % (Rth_jc +2.8 %): the check refines the same elements. Both are in the README so nobody reads one for
the other. On *One Finger* order 1 reads the IR offset 9 % low, so that cell stays at order 2 (3.5 min).

**Order 2 on the die-to-heatsink mesh left 35 elements with a negative Jacobian** (Gmsh's "worst distortion −0.0116"); the run
did not report it. Not investigated — it did not move the energy balance (3E-13) — but a user never sees Gmsh's log.

**The brief's `ViasAsBlock` setup cannot exist**: an effective block's `Enabled` is a field of the `.c3d`, not of a setup, so
one document cannot carry both. The example ships the block disabled and the README gives the toggle and both answers. The
block read the die 13.7 K cool and was SLOWER (3 min 10 s against 53 s), on only 15 vias.

**"Case temperature" differs by definition**: Rth_jc is 5.82 K/W against the flange bottom's average and 5.41 K/W against its
hottest point at 11 W — both quoted.

**Eight Fingers runs at 0.25 W a finger** (1.25 W/mm) — its measured run. It was re-pointed to 1 W (5 W/mm, *One Finger*'s
density) and that run was stopped unfinished when the session's simulation time ran out; the README says to set `P_finger` to 1.
Its wall time was never recorded separately. Its Rth figures are per watt in the modelled half, as the run's notes say.

**The shipped library's thermal-era materials now state their electrical values** (2026-09-29). They stated thermal
properties only, so the Materials editor showed every electrical field of GaN and SiC blank and their role as "states
nothing" — correct for a thermal run, and a poor record to copy into another project. SiC (εr 9.7), GaN (9.5), CVD
diamond, the mould compound and the TIM gained εr and tanδ; the gold-tin, sintered-silver, CuW, CuMo and SAC records a σ₂₀;
each with its source prepended to the record's Source. The shipped library and both examples' copies stay identical. A
thermal run reads σ only for a bond wire or an electrothermal conductor and never εr, and neither example has either, so
no recorded number can move (the Benchmark re-run was not repeated). Resolves brief 85 §1d.

**Edit a `.cmat` as TEXT, not through `MaterialLibraryPersistence.SaveToFile`.** The committed files keep each table point
on one line (`{ "TempC": 20, "Value": … }`); the serializer writes each point over four, so a save rewrites ~750 lines
for a ten-key change. The Materials editor's Save does the same to a user's library.

Every number in both READMEs and `docs/user/src/reference/thermal.md` is in each example's `expected-numbers.json`;
`tests/Ui.Tests/Examples/ThermalExamplesTests.cs` holds the text, `check`, the vendor scan, and (Benchmark tier, not yet run)
re-runs the shipped setups against the recorded values.

## brief-em3d-85 — Thermal Output Wires, the third thermal example (2026-09-29)

Shipped: `Thermal Output Wires` — six 1 mil gold wedge–wedge wires from a drain pad on a SiC die to a copper lead, in a generic
mould compound over a CuMo flange at 85 °C; setups `EM` (Palace, Draft), `DcSweep`, `RfHarmonics`, `FromHB`, and a second cell
`Drawn Wires` with the same row drawn as `.c3d` wires. `check` is clean (0 warnings). Times are Release (the owner: users do not
care about the Debug build's): DcSweep 21 s, RfHarmonics 7 s, FromHB 12 s, EM 1 min 37 s in Palace.

**The die is the layout's own stackup, so `check` is clean.** Brief 81's Pads technology had one Pad Metal conductor and no
ground reference, and `check` warned three times. The technology is now Pad Metal (10 µm gold) over SiC (100 µm) over a 25 µm
gold-tin die attach marked ground reference; SiC and the attach exist only under a `Die` layer rectangle (`PresentWithLayer`, and
the attach draws on that layer), so the lead finger — Pad Metal outside the die — has no substrate under it and sits on the 3D
view's copper lead. A 5 µm backside was tried first: meshed at one element through its thickness over the whole die it took the
mesh from 21k to 95k nodes.

**A drawn wire's end is ON its pad; a `.wBond`'s is the foot's axis.** The `.wBond` states z above the ground reference's top,
at the pad top plus the default hexagon's half-height (π√3·d/12 = 11.518 µm for 1 mil); the drawn wires put their end points
at the pad's top surface. Both resolve to the same chain: Drawn Wires reproduces Output to every printed digit.

**The port must be on a conductor the wires land on, or bonded to one.** The RF plan assigned a port's harmonics to the array
ending on the port's positive conductor; port 2 is on the copper lead and the wires land on the gold finger bonded on top of it.
The plan now accepts an array on a conductor that shares mesh nodes with the port's (src/Design/RESOLVED.md).

**Numbers worth keeping.** The runaway: above 16.69 A for the six (the hottest wire 944 °C at 16.62 A). At 10 A, k(T) off moves
the hottest wire 2.6 K and σ(T) off 38.7 K. At RF the inductive share puts the most current on the edge wires (270.8 against
199.5 °C at 8 A peak). An outer EM loop (each wire's R_ac at its solved temperature in the share) would move a wire's RF current
by at most 0.83 %. Palace Standard against Draft: 10 min 10 s and 7.6 GB, 233.6 against 228.5 pH, 3.9 K on the FromHB edge wire.

**The circuit's DC path through an EM result is not the wires' resistance.** An SnP block holds its lowest frequency (0.1 GHz)
below its data (`OutOfRangePolicy.WarnClamp`), so at DC the six wires (≈ 8 mΩ) look like ~2 Ω and the drain sits ~6 V below the
lead. The DC CURRENT, which the thermal run takes, is the circuit's own. A finding for the owner; the README says it.

**Figures.** The four pictures in `docs/user/assets/fixed/output-wires-*.png` were drawn by a one-off patch (its README says
how); brief-em3d-88 is the brief that makes `render --field` draw them.

## The Hierarchy example (2026-10-04)

Built by `tests/Ui.Tests/Examples/HierarchyExampleAuthoring.cs` (`CRF_AUTHOR_HIERARCHY=1`) through the functions the
GUI's commands call: `WorkspaceCreate`, `CellCreate`, `SchematicToLayoutGenerator.Run` (Update Layout from Schematic)
with `DiskCellResolver`, and a third Update Layout run asserted to change nothing. `HierarchyExampleTests` is the
routine gate.

- **A cell parameter that values are written in terms of made the cell fail `check` and LVS on its own.** Fixed at the
  extractor rather than worked around in the example. See `src/Design/RESOLVED.md`, "A cell's own schematic binds its
  declared parameters at their defaults".
- **A VAR block inside a cell is cell-local.** `NetExtractor` turns a sub-cell's VARs into `Cell.Variables`, which are
  evaluated in each instance's scope. That is what lets Pad state `Rsh`/`Rse` once and resolve them per instance. It
  also keeps the schematic readable: the formulas written inline on each resistor overlapped one another on the canvas.
- **A board pin on a line's own artwork is on no net.** An MLIN placement is a DEVICE to LVS, and a device's internal
  copper is not interconnect, so Board's `IN`/`OUT` pins placed on the line ends read as "Open: … a piece with no pins".
  A 20 mil drawn launch at each edge, with the pin on it, is what a real board has anyway.
- **Pad needs `FlattenForLvs`.** Update Layout from Schematic draws Pad's ground vias and pour itself (designer feedback
  round 11), and that pour meets Board's on Bottom Copper away from every declared pin. LVS rightly refuses a
  hierarchical reading of a cell joined to its parent by undeclared metal. With Pad flattened, Board matches and keeps
  one accurate warning: its ground copper carries no pin, because Board's schematic states no ground net of its own.
- **Measurements belong on a Meas component.** `SchematicEditModel.Measurements` round-trips through the `.csch`, but
  it is not what the `.cnl` writer emits as `measure` lines; a Meas component is, and it is also what a user would draw.
- **A cell instance written headlessly needs `CellInterfaceHash`.** The GUI's placement records
  `PlacedCellRef.HashFor` beside the reference. An instance without one reads as "never recorded", so the authoring
  code's `Cell(...)` helper records the hash and seeds parameters from the `.ccell`, as placement does.
- **The 3D hierarchy has to place 3D VIEWS to be pushed into in place.** The first cut placed Board's LAYOUT in
  Assembly, and Push Into Cell on a layout instance leaves 3D for the layout editor. That placement is legitimate, but
  it showed no 3D sub-view. Board now has a 3D view of its own (its own layout as `L1` on a carrier; a cell's 3D view
  may place its own layout, never its own 3D view), and Assembly places that 3D view plus a Launch cell twice.
- **The 3D editor had no toolbar buttons for Push Into Cell / Pop Out**, only Ctrl/Cmd+] / [, the context menu, and
  a Pop Out button in the breadcrumb bar that appears only once you have pushed in. It now has the layout editor's ↓/↑
  pair, enabled on `C3dEditorViewModel.CanPushIn` (one instance selected) and `CanPopOut`. Gate: the push-in test in
  `ThreeD/HierarchyGateTests`.
- **The connector is the 3D Connector example's, copied.** A first attempt drew one from boxes and gave it no
  bore, so the pin sat on the body's face, shorted to it. The copy keeps that example's housing, PTFE-filled bore,
  STEP flange and filleted pin, and drops its board instance, ports and setups. Its "Connector alloy" is added to
  this workspace's technology BEFORE any layout is generated: a generated part is keyed on its technology's content,
  so a technology edited afterwards leaves every part in both layouts reading as out of date (the gate's "Update
  Layout again changes nothing" caught exactly that). Its z = 0 is the ground plane's top, so Assembly places it 35 µm
  up, on the Board 3D view's ground plane.
- **Pushed into a 3D child, only the child is drawn now.** brief-em3d-48 drew the parent around it, dimmed; the owner
  asked for the layout editor's behaviour, where the level above is not drawn at all.
- **X1 and X2 show no designator on Board's layout.** A module is not a part: its resistors carry designators, the
  module puts nothing on the silkscreen. Switched off per instance (`ShowRefDes = false`).
- **No EM setup on the 3D views, deliberately.** A land pattern is pads only, so a full-wave solve would see each
  resistor as an open.

## Thermal Output Wires: the FromHB gate ran on a results folder no circuit names (2026-10-05)

`ThermalExamplesTests.TheOutputWiresFromHB_ReproducesItsRecordedNumbers` (Benchmark tier) refused before solving:
*"No instance of the circuit uses this 3D view's EM result"*. Its `Run` helper passed a scratch folder as the results root,
so EM wrote its `.s2p` there, while the Amplifier's `XOUT` names `<workspace>/results/Output EM.palace.s2p` — where the
GUI and `circuitrf em` write. The example itself was fine (run headless on a copy: EM, then FromHB, edge wire 205.108 °C).
The gate now copies the example and runs it with that copy's own `results/`, and reproduces 205.108 °C and w1's limit at
13.1093 dBm.

## brief-em3d-117 — the 3D Wave Ports example (2026-10-06)

A new workspace, `examples/3D Wave Ports/`: the 3D Connector's Board, Flange, `flange.step` and technology copied byte
for byte (a test holds them so), its Launch with the back end open and P1 a coaxial wave port, and Pair, an edge-coupled
PTFE stripline pair with a two-terminal wave port at each end. Gate: `tests/Ui.Tests/Examples/Em3dWavePortsExampleTests.cs`.

- **Resolved by brief-em3d-121 (below).** **Neither cell's port can be made by hand yet; both are stated in the file, and the README says so.** brief 114 left a
  single wave port refusing an enclosed conductor ("… overlap across its region …; state its VoltagePath") and the editor
  has no field for a voltage path, so *Make Port ▸ Wave* on the coax face is greyed out. And when a stripline's ground
  planes are the air box's PEC faces, `C3dPorts.TerminalsFor` takes the two-conductor reading (two strips besides box
  faces) and the gesture makes ONE port from strip a to strip b, not two terminals; two drawn ground sheets do not help,
  since equal surfaces tie and a second ground is then "neither a terminal nor the reference". A united ground (one
  Boolean) would be one conductor, but openEMS writes a boolean as a polyhedron, which leaves out grid nodes on its faces
  (brief 42), so the ground planes would move. Brief 117 §8 says report, not patch: the owner decides whether 114's
  RayPath fallback extends to a single wave port, and whether box-face grounds count as the reference for terminals. Brief written: `docs/sonnet-briefs/brief-em3d-121-wave-ports-by-hand.md`.
- **openEMS does not resolve the Launch's coax at the connector's grid.** The pin is 3 cells across; the run's own line
  measurement reads 56.72 Ω and ε_eff 3.17 (closed form 50.02 Ω, 2.1). Brief 116's gate 8 found the error linear in the
  cell (50.88 Ω / 2.22 at r_i/10), and this is that line extended to r_i/1.4. Raising CellsPerWavelength barely moves the
  pin's cell (111 µm at 90, 2.3 M cells) because openEMS honours no mesh region; 20 cells across the pin needs 575, which
  is 137 million cells for this box. A cylindrical grid cannot serve a coax that meets a board. The README tells the
  reader Palace's answer is the one to trust on this cell; whether to ship the openEMS setup that way is the owner's call.
- **Pair's section is S = 0.4 mm, not the first choice of 0.3 mm**, because at 0.3 mm the grid's thirds rule is broken by
  grading in the gap and the odd mode runs slow (max |ΔS| 0.26 against Cohn; `src/Engine/RESOLVED.md`, "a thirds pair
  broken by grading"). At b 2 mm, W 1.65, S 0.4 mm, Cohn's Z₀e/Z₀o are 57.51/43.10 Ω and openEMS lands within 0.0027 of
  his ideal line over the whole matrix, 2–18 GHz. The default MinCell (a tenth of the 1.6 mm strip) equalled the feed's
  cell and raised a stream of "closer than MinCell" warnings, so the setup states MinCellUm 10, as brief 116's gate does.
- **The em-setup page had S21 and S31 swapped.** With terminals 1 and 2 at one end and 3 and 4 at the other, S21 is the
  near-end coupling and S31 the thru; it named them "thru, near-end". Corrected with the example's link.
- **The Palace gap port's deep 18 GHz null was the gap.** With the wave port, |S11| at 18 GHz is −19.53 dB on Palace where
  the lumped gap port read −40.75 dB. Palace's mode impedance at the Draft preset is 49.18–49.31 Ω.

## brief-em3d-121 — the 3D Wave Ports example made by hand (2026-10-06)

Both cells' ports are now made with the editor's gestures; the reasons are in `src/Design/RESOLVED.md` and
`src/Ui/ThreeD/RESOLVED.md` § brief-em3d-121. Gate: `tests/Ui.Tests/ThreeD/WavePortsByHandTests.cs`.

- **Pair's four terminals are what *Make Port ▸ Wave* writes** on the fill's xmin and then xmax face, re-made by
  `WavePortsByHandTests.AuthorTheExamplesPorts` (`CRF_AUTHOR_3D_WAVE_PORTS=1`). The only change in the file is each
  port's rectangle, now numbers (the gesture writes the face's DBU) where it was the VARs' expressions; the values are
  the same, so the lowered problem is identical (gate 3) and the recorded numbers stand.
- **The Launch's P1 states only its rectangle**: `Positive`, `Negative` and `VoltagePath` are gone, and the inferred
  path is exactly the one that was stated. The rectangle stays the 2 × 2 mm one the Port tool draws, the owner's
  choice: *Make Port ▸ Wave* on the bore's end face gives the 1.34 mm square round the disc, on which openEMS refused the
  run (its current probe had 106 µm of room where it needs one 153 µm cell) and Palace's Draft |S| moved by up to
  1.9 dB. The README teaches the Port tool for P1 and says what the face gesture gives.
- **Gate 4 re-run on the shipped files**: all three runs reproduce their recorded |S| within tolerance (largest difference 0.015 dB), so no number in the README or the new-user guide moved.
- **`TerminalWavePortTests.Gate5` failed before this brief**, from brief 117 on: it asserted that no shipped `.c3d`
  contains `Terminals` or `Reference`, and Pair does. It now asserts only that re-saving adds neither key to a file that
  lacked it.

## brief-em3d-118 — the 3D Eigenmode example (2026-10-06)

A new workspace, `examples/3D Eigenmode/`: *Cavity* (a 50 Ω microstrip through a lidded aluminium housing) and *Cavity
with post* (the same, plus one grounded post), each with an eigenmode setup *Modes* and a driven setup *Driven* on Palace,
and a saved *Cavity mode* field plot. Gate: `tests/Ui.Tests/Examples/Em3dEigenmodeExampleTests.cs`. The two `.c3d` files
were written as JSON (every object a Box, Sheet or Cylinder a user draws with the tools), not drawn by an authoring test.

- **The brief's example band, 2–12 GHz with the mode near 8 GHz, cannot be met with one post.** A 2D TM estimate
  (finite-difference Helmholtz, Dirichlet on the walls and the post) over square and rectangular cavities, posts 2–4 mm,
  clear of the line by 0.8 mm: one post at the mode's maximum raises the first mode by **at most ~30 %** (1.14–1.31×; the
  square is best; a bigger post must sit further from the centre to clear the line and gains nothing past ~3 mm). The
  brief's three conditions need ≥1.65× for 2–12 GHz. So the band is X band, **8–12 GHz**, where the closed form (10.13 GHz)
  is just above the middle and ×1.27 clears 12 GHz. Palace measured ×1.29 (9.956 → 12.84 GHz).
- **Iterations.** a = d = 21 mm square: Palace's mode 1 at 9.92 GHz (Q 499), but the run did not finish (the nonlinear
  refinement below). Suspected the square's degenerate TM210/TM120 pair and moved to **a 22 × d 20 mm**, which splits it
  by 0.9 GHz: no change, the cause was elsewhere. With the cause fixed: Cavity's mode 9.956 GHz, the post's 12.84 GHz,
  `post_d` 2.5 mm, `post_gap` 0.8 mm, laminate 0.254 mm εr 2.2, `w` 0.78 mm (Hammerstad: 50.16 Ω), h 4 mm.
- **Palace's eigen refinement never ended** with Count 3 and a lossy line: `src/Design/RESOLVED.md` § Palace eigenmode:
  `MaxIts`. The owner chose to write `MaxIts`; the example's **line is a perfect conductor** (a material stating neither σ
  nor εr, with the object's `Role: Conductor`), because a lossy zero-thickness sheet stalled the refinement even with
  `MaxIts` bounded. The housing and post stay aluminium, so wall loss is in every Q. The README says so.
- **Count 3 returns two rows with Q ≈ 1** in both cells (10.18/14.43 GHz, then 10.22/14.45 GHz with the post): the line
  between its ports. With the post they sort below the cavity mode, so *Cavity with post*'s cavity mode is **row 2**. The
  README teaches the reading (a resonance has Q in the hundreds or more), `expected-numbers.json` records `CavityMode`, the
  post cell's field plot names mode 2, and gate 5 asserts the recorded row is the first with Q above 10.
- **The cross-check holds**: Cavity's driven notch is at 9.955 GHz (−11.69 dB at the nearest point) against the
  eigenmode's 9.9564 GHz, inside the 5 MHz step. The driven mesh is not the eigen mesh (13,750 against 16,177 tetrahedra)
  and still agrees. With the post, |S21| stays above −0.093 dB over 8–12 GHz.
- **The post raised the Q** (539 → 2,480 loaded; Q_ext per port 1.3 k → 36 k): with the post the mode moves to the half of
  the housing away from the post, and less of its field is left at the ports. The README uses it as the evidence that the
  post shortens the cavity rather than damping the mode.
- **No εr VAR.** The brief lists "substrate εr" among the VARs, but a `.c3d` cannot bind a material's εr to an expression;
  a VAR that drove nothing would mislead, so εr is the technology material's and the README says where it lives.
- **Port rectangles are numbers**, as 3D Package's and 3D Wave Ports' are. While building this, `explain` reported "P1's
  rectangle has no area (0 × 0 DBU)" for a Rect written as expressions where `check` resolved it; fixed the same day
  (`src/Cli/RESOLVED.md`, "explain measured an unresolved port rectangle").

## 3D Wave Ports' Launch states an openEMS grading of 1.15 (brief-em3d-123, 2026-10-06)

Once a lumped port's extent stopped being a grid line inside a thirds pair, the openEMS grid round the connector's pin
lost the fine cells it had inherited from the board line's edge, and the Launch's coax wave port was refused: its current
probe needs a cell of clearance from the housing and had 0.87 of one. More cells per wavelength does not help — the cells
in the bore are set by the pin's lines and the grading — so the setup states `GradingRatio` 1.15, the largest that clears
(1.2 gives 0.99 cells). The README says why and what it costs (937,440 cells against 526,768). The 3D Connector keeps the
default grid on purpose, and its `check` now lists the bore as staircased; both are in its README. Detail:
`src/Engine/RESOLVED.md`, brief-em3d-123 R-2.

## Optimization (brief-tuneopt-12)

**Each bench was written as a `.cnl` and drawn with `netlist --to-schematic`**, so the drawing is the
extraction's own inverse and the `tune`/`goal`/`optimize` lines reach the `.csch`'s tuning block with no
hand-editing. The *Equiripple* preset was written by `opt --save-preset`, the same `TuningPresets.LockIn`
the Optimizer's Lock in calls; the gate holds it equal to the run's best point.

**The amplifier's range is too narrow on purpose, and the numbers were chosen for the lesson.** With
`Rstab` at 1–3 Ω the Auto run rails it at 3 Ω and misses both goals; the panel's Widen (doubling the
span on a linear range) gives 1–5 Ω, which rails again, and 1–9 Ω, which meets both. With a 50 Ω lower
bound on `Rshunt` both resistors railed together, which teaches nothing, so its range starts at 20 Ω.
Least squares and Levenberg–Marquardt approach a `ge` limit from the violating side and stopped 1e-8
short of "met" on an earlier version of this bench; Auto, Minimax and the population methods land
inside.

**A filter's passband goal is only held at the grid.** On its own 151 points the minimax optimum sits at
exactly −0.50 dB at both band edges and its deepest ripple; on 1,501 points it dips to −0.509 dB at
1.045 GHz. The README states this as the setting traded for speed.

## Capacitance Extraction and Test Pad Capacitance (brief-dfr31-2, 2026-10-29)

**Authored through the setup panel, not written by hand.** `tests/Ui.Tests/Examples/CapacitanceExamplesAuthoring.cs`
(skipped unless `CRF_AUTHOR_CAPACITANCE=1`) makes every electrostatic setup through `EmSetupEditorViewModel`: a terminal
row, its Pick armed, `PickConductor` on the object a click would hit, and the ground from the ground picker (an object's
piece of metal, or **Every body not in a terminal**). Test Pad Capacitance's boards are generated artwork with every pad
drawn on its own layer and no net anywhere, in a technology with no ground-reference layer, and each 3D view is
`C3dHierarchy.NewFromLayout`'s. Pad and Line's geometry is drawn in its `.c3d`; only its setups are made there.

**A 3D setup cannot sweep a variable that moves geometry.** The thermal sweep refuses one by name, and the static and
driven setups have no sweep of a variable at all, so the brief's three-point gap sweep is three runs of one setup with
`gap` changed in the Variables panel. The README and the test say so; the test sets the VAR on the loaded document,
which is what the Variables panel does. **`circuitrf em --set` does not reach a 3D view's VARs**: it sets a global of a
circuit-driven thermal run, and on an electrostatic `.c3d` run it is accepted and has no effect. Not changed here.

**A test pad's terminal is its whole net.** Clicking the bottom pad takes the line too, because the line, the via, the
unused pads and the pad touch. The stub's own capacitance is therefore the net less the net with the stub cut away,
which is why the example has a **Line only** cell. Measured: 2.140 pF as built, 1.830 pF fixed, 1.450 pF line only.

**Leaving the planes floating more than halves the answer** (0.920 pF against 2.140 pF): with Other metal at its
default, Float, on a board with no ground-reference layer the planes carry no charge and the only ground left is the
metal box round the model. That is the reason the README makes **Every body not in a terminal** step 3.

**Lumped ports on an absorbing face load themselves.** The first driven setup put the air box's x faces at zero padding,
so each port sheet lay in an absorbing face: |S11| was −9.5 dB and |S21| −3.6 dB at 0.5 GHz on a 12 mm 50 Ω line, 70 % of
the power lost. With 1 mm of air beyond the board's ends the line alone reads |S11| below −16 dB to 9 GHz.

**Draft overstates a small capacitor's loading in a driven run.** Below 3 GHz, Draft with no refinement loaded the line
like roughly twice the electrostatic stub; two refinement passes (still first order) closed most of the gap, for 7.3 min
against 2 min. At 2.4 GHz the shunt-C chain and the refined driven run agree within the brief's 0.5 dB. Above about half
the stub's resonance (≈7.5 GHz in the driven run) the via's inductance dominates, so Compare also carries the
electrostatic C behind TESTPAD's via inductance.

**`circuitrf em` printed "ground = the ground-reference conductors"** for an electrostatic run whose ground box is blank
and whose Other metal is Ground. It now prints "every body not in a terminal" (`src/Cli/CliEntry.cs`).

**Pad and Line.cdd is a Table of the four matrix columns, in the Engineering number format (`FormatString` `S`).** At the
default fixed format a capacitance in farads prints as 0.000. Scaling in the trace spec does not work:
`plot --trace cube=C[:,"pad"]*1e15` is accepted, drawn, and written to the `.cdd` with the factor gone, so the
table still reads 0.000. Not changed here. The table is stored at its minimum size, each column fitted to its header
and cells, and the box to the columns and rows, as a double-click on the gripper sizes it
(`CapacitanceExamplesAuthoring.PadAndLineTableAtItsMinimumSize`, run against a fresh result).


## Antenna Co-Simulation: one layer, no vias, five frequencies (2026-11-05)

Added to show EM Field Data plotting a pattern from a schematic, on a board much cheaper than a real one of the same outline.

**Which solver.** Palace at order 2 took 2 min 44 s for ONE frequency on 68,085 tets with all ten processes. EMerge at Accurate took 2 min 45 s for FIVE on 81,547 tets. Five stored frequencies decided it. The README tables EMerge against Palace at 868 MHz: S11 within 0.05 dB as drawn and 3 dB at the match, and EMerge's gain about 0.6 dB higher, as `antennas.md` warns its far field reads.

**The meander's length is what sets the match, and a first guess was badly off.** The first length tried, 148 mm, is near a parallel resonance at 868 MHz, which no L-network in range could match. At the Standard preset, 106 mm puts the series resonance near 790 MHz and 85 mm near 880 MHz. At Accurate, 98 mm leaves 868 MHz just above it, at about 20 + j33 Ω at the port with the inductor shorted.

**Component ports need solid conductors.** With `Conductors: Sheets`, every pad is refused as landing on no conductor. The setup stays at Auto, which makes 35 µm copper a solid at this frequency.

**A swept variable cannot also be optimized, and a swept analysis cannot be read by a scalar goal.** So the sweep drives `dL`, an offset added to `Lser`, and `F3` exists apart from `F1`. `dL` is limited to 2 nH each way so that the inductor stays positive at the optimizer's 2.57 nH.

**The stackup marks Top Copper as the ground reference.** A one-layer board has no other conductor, and with none marked `check` warns about microstrip parts the board does not use.

Gates: `tests/Ui.Tests/Examples/AntennaCoSimExampleTests.cs`, and `ExampleWorkspacesTests`, whose result-name check had not been told that an EM Field Data block names a `.cemr`.

**Connecting the schematic is what let `lvs` run on it, and that exposed a false error.** `C2` is drawn with **Disable** Open and its land pattern is on the board, so `lvs` reported a layout part with no counterpart. An unfitted site is an ordinary thing to build, so `lvs.device.not-fitted` reports it as a note and the pads stay copper. A P1 label added to the layout changes no solve, since a 3D view takes no port labels from a layout it places: the mesh and ports came out the same, and S magnitudes agreed to 2e-7.


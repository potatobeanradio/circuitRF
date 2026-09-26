# `src/Engine/Mom` — resolved briefs (detail, off the CLAUDE.md growth path)

Completed work's detail lands here instead of `CLAUDE.md`, which stays for durable, still-true
conventions only. Same pattern as `src/Ui/DataDisplay/RESOLVED.md` and `src/Ui/Layout/Em/RESOLVED.md`.

## Two user-report fixes in the planar port and via refusals (2026-09-18)

An experienced designer imported a two-layer reference board, pointed an EM setup at it, and
reported two things. Neither was wrong physics; both were the engine SAYING something wrong.

### 1. A via refusal printed its length in raw metres

`PlanarLevels.CheckOne` spelled the via's length `{ell:G4} m`, so a 1.4651 mm through-hole read
**"0.001465 m long"**. That number is the one quantity in an otherwise dense paragraph a reader has
to act on, and the designer read it as a units bug and went looking for a wrong stackup entry.

`CanRepresentVias` now takes a `SurfaceMesher.PlanarLengthFormat?`, defaulting to
`DefaultLengthFormat` (SI engineering notation → "1.465 mm"); `PlanarKernel.CanSolve` takes one too
and hands it down, and `EmRunService` / the EM panel pass the layout's own display unit through
`EmLengthFormat`, which is the rule every other length in an EM message has followed since
2026-08-15. On the reported board it now reads **"1465.072 µm"** — the `.clay`'s own unit.

The same pass replaced the remaining raw-metre spellings in user-facing EM refusals:
`PlanarKernel`'s via-crosses-an-interface message, `PlanarProblem`'s two level checks,
`LayeredMedium`'s grounded-slab check, `QuasiStaticKernel`'s four region/length refusals and
`RlgcExtractor`'s Wheeler-recession note. **The internal invariant guards were deliberately left
alone** — `Dcim`, `SpectralGreens`, `InteriorStaticGreens` and `SommerfeldIntegral` throw at `G6` on
purpose; those are programming errors, and an exact number is what a reader of one wants.

### 2. Two port labels on one terminal produced a complete, non-passive answer, silently

`PlanarPorts.ResolveAll` resolved each port independently and never compared the results. The
designer's board carried **P5 and P6 at bit-identical coordinates** and circuitRF wrote the file.

Reproduced on the shipped Klopfenstein taper by copying its port 2 onto itself as port 3: the clean,
passive two-port became a three-port with **40 of 47 rows NOT PASSIVE (worst σ_max = 1.0428)**, whose
own caveat says *"what produced the gain is not identified here"*, and whose two coincident ports
were even peeled by **different feed-lead lengths (179.58 mil against 89.58 mil)** for one piece of
metal. The unmodified example flags nothing, so the coincident port is the whole of the difference.

`ResolveAll` now refuses with `PlanarPortCollisionRefusedException`, which `EmRunService` reports as
a **Refused** (the user's geometry) rather than an `EngineError`, exactly as
`PlanarFeedClearanceRefusedException` is.

**The rule is INDISTINGUISHABILITY, not overlap, and that was earned twice.** A shared basis index
alone fails two legitimate fixtures: on `ModalErrorBoxTests`' deliberately coarse mesh the two ends
of a short line share their one interior rooftop (`bases=2`, both ports on basis 0) and are told
apart only by `Side`/`IncidenceSign`; and where a coupled pair's mesh is too coarse to separate the
modes, `GroupSeparationRemedyTests` has a far better-diagnosed refusal to make and must keep making
it. So the guard asks for same layer, same kind, same side, same incidence sign AND set-equal basis
indices — the case with no reading at all.

Gates: `tests/Ui.Tests/Em/EmRefusalOrderTests.cs`.

## MIM-14 — the floor was a stale measurement, and it is 200 (2026-09-16)

`brief-em-mim-14-refloor-the-full-wave-limit.md`. **`PlanarLevels.FullWaveCellOverSeparation` moves
from 40 to 200, the sign inversion MIM-12 measured is gone, and the spiral-plus-capacitor that was
refused at cell/separation 66.7 now runs.** This is the brief's Case A.

### The verdict, in one paragraph

MIM-12's ladder — the extracted series element correct to cell/separation 40, sign-INVERTED by 80,
noise at 200 — was taken on the kernel MIM-12a then repaired, and MIM-12a did not re-run it. Re-run
on the peeled kernel, **no rung from cell/separation 0.5 to 200 departs from the electrostatic mutual
capacitance of the same mesh by more than 10 %, and every point is passive** (worst σ_max 0.9993).
Two independent ladders agree: MIM-12's own design, with the straddling cell pinned and only the film
moving, and a second one with the film pinned at the shipped 0.2 µm and only the CELL moving. Every
table is in `HISTORY.md` §MIM-14. Most of the work was the fixture, exactly as the brief predicted.

### 1. Why MIM-12a's fixture could not grade anything — all four symptoms, diagnosed

Each of the four things that made MIM-12a stop has a cause, and none of them is the film:

- **"A plain through line comes back 8 % non-passive."** It is a **two-CELL line**. At two cells along
  a 600 µm conductor both de-embedding reference planes land on the same gridline and the de-embedded
  DUT has zero length; the run says so in its own port notes ("reference plane at x = 300 µm" from
  both ends). Given cells between the planes it is an ordinary matched section: `|S₁₁|` in the line's
  own reference is 0.980 at two cells across, 0.133 at four and **0.0131 at eight**, against
  `PlanarDeembedTests` T4_1's own 3e-2 gate.
  *One deviation from the brief's wording, stated rather than taken quietly:* it asks control 1 for
  `σ_max ≤ 1` and the shipped fixture asserts `≤ 1.001`, because at 600 µm / 8 cells it reads
  **1.00016**. Table 1 has the row that clears it outright — 900 µm at 8 cells reads 0.9992 — and the
  fixture was not moved onto it, because 1.6e-4 is four decades below the 8 % that stopped MIM-12a
  and the finding (it is the CELL COUNT between the reference planes, not the length) is the same
  either way.
- **…and the `|S₁₁|` that looks alarming in the PUBLISHED S is not a defect at all.** This line is
  67–83 Ω and the published S is renormalised to the port's 50 Ω, where a perfectly matched section
  legitimately reads 0.07–0.45. The imaginary part of `Z_c` is what does it: for a matched line the
  renormalised `S₁₁` numerator is `Γt² − Γ*`, which is `2j·Im(Γ)` at `t ≈ 1`. **Matched has to be
  asserted in the line's own reference**, which is what T4_1 does and what a reader of the published
  file cannot do.
- **"The reading is 6-10× below the electrostatic value at d = 20 µm."** The comparison was against an
  electrostatic instrument built WITHOUT `Dcim.ForStackAtFrequency`. That is MIM-12 step 0's own
  finding pointing the other way: MIM-8's fixture is right at 10 GHz and reads the plate capacitance
  as essentially ABSENT at 1 GHz — `C/(ε₀εᵣA/d)` = **0.033**, where the run's own fit reads 1.006
  (`HISTORY.md` §MIM-14 Table 6). Any measurement here that constructs its own kernel set measures a
  different instrument.
- **"It halves with every mesh refinement."** Not reproduced on a fixture fed on ONE level. Control 3
  moves **1.9 %** over three meshes.

**And a fifth thing, which is the one that actually matters.** The capacitance must be read as
`Im(−Y₂₁)/ω` and never as `−1/(ω·Im(−1/Y₂₁))`. Both are "the series element read from `−1/Y₂₁`"; only
the first is the quantity an electrostatic `C₁₂` is. `−Y₂₁` is the pi model's direct branch
ADMITTANCE, `G + jωC`; inverting first and reading the reactance gives `C(1 + 1/Q²)`. The fixture was
first built with the MIM plate's own 0.25 µm metal on the feeds as well, `Q` came out at 2.9, and the
two readings differed by **12 %** — enough to have put the floor an order of magnitude low.

### 2. The fixture, and the one thing about it that cannot be fixed

Both feeds arrive on the LOWEST level, which is the brief's own instruction and the topology the
shipped capacitor cell draws: port 1 into the bottom plate, the top plate strapped sideways on the
upper level and down a via onto a landing pad on the lowest one. **MIM-4 is built** — the brief says
"a de-embedded edge port above the lowest analysis level is precisely what MIM-4 has not built", and
that sentence is stale (`MultiLevelPortTests.M3_2_…DeembedsSinceMIM4…`) — but this topology needs
nothing it added, and it is what a user draws.

**The fixture carries ~437 pH of its own and it cannot be shortened.** The calibration refuses feeds
closer together than five substrate heights, so on a 103 µm substrate a de-embedded two-port of a
lumped element necessarily contains ~600 µm of line: at 150 µm feeds the run stops with *"Calibrated
port feeds are not isolated"*. **That is a structural property of de-embedding a lumped element in
this engine and it is worth knowing on its own.**

It is separable in closed form rather than by tuning. The branch is L in series with C, so
`1/Im(−Y₂₁) = 1/(ωC) − ωL` is exactly linear in ω; two-parameter least squares over 1 / 2 / 3 GHz
returns C free of the instrument and returns L. **Two things make that legitimate rather than
convenient**: L comes back at 431–444 pH on every rung of a pinned-artwork ladder where it is
determined, and the fit's own residual is 1e-5 — the three susceptances ARE one series L-C to five
decimals. Where the residual degrades (3e-3 at the last rung) it is because the rung has reached the
fixture's own self-resonance, and the residual is what says so.

**Reading one frequency instead measures the fixture.** At cell/separation 200 with a 4.35 pF plate
the fixture self-resonates at 3.65 GHz and the raw 3 GHz row reads **3.9×**. Read as a floor, that
number is a statement about 600 µm of line.

### 3. MIM-12's ladder axis is not clean, and the second ladder is what proves the point

Pinning the plate and moving only d makes `C ∝ 1/d`, so a rung's cell/separation and its CAPACITANCE
move together — and the larger the capacitance the more the fixture's own inductance matters. The
brief's own sentence "so cell/separation is the only axis" is false for that design, measurably:

- At **fixed d = 0.25 µm**, moving cell/separation 80 → 40 → 26.7 moves the reading by **4 %**.
- At **fixed cell/separation 40**, moving the frequency 0.2 → 3 GHz moves it by **60 %**.
- At **0.2 GHz**, where the structure is still lumped, cell/separation 40 reads within **2.9 %**.

So the second ladder pins the film at the shipped 0.2 µm and moves the CELL, which is the quantity the
constant is about. It reads 0.934 / 1.004 / 1.018 / 1.023 / 1.028 at cell/separation 200 / 100 /
66.7 / 50 / 33.3 — inside 10 % over a 6× range, crossing the control near 100 and departing slowly in
both directions, which is not the shape an unresolved separation makes.

**And the RAW per-frequency reading's error GROWS as the ratio FALLS**: at 3 GHz, 9.6 % at
cell/separation 200 against 20.8 / 23.2 / 24.1 / 24.9 % at 100 / 66.7 / 50 / 33.3. Refining the mesh
resolves the fixture's own inductance better, and the lumped-C reference does not have it.

**A reading that gets worse as the mesh is refined cannot be used to set a floor on how coarse the
mesh may be.** That is the whole argument for separating L, stated as a measurement.

### 4. What moved, and what deliberately did not

**One constant: `PlanarLevels.FullWaveCellOverSeparation`, 40 → 200**, with MIM-12's rows kept beside
the new ones in its doc comment. Four run-visible sentences take the new table —
`LevelSeparationVerdict`'s refusal, all three arms of `LevelSeparationNotes`, and `NonPassivityCause`'s
first arm — and each of them now says "unmeasured past 200" where it used to say "loses its magnitude
and then its sign", because the second is no longer true and the first is.

**The refusal stays.** MIM-9's instruction and this brief's: the floor moves, it does not disappear.
What it refuses is different in kind — past 200 nothing is measured on EITHER side, the two-port or
the cross-level fill, and `PlanarLevels.CanRepresentVias`' own argument covers that unchanged.

**The two constants now carry the same number and the middle arm of `LevelSeparationNotes` is
unreachable.** It is kept, with a comment saying so: they remain two independent measurements over two
different instruments, either can move on its own, and deleting the arm would leave a hole in the
scale for whoever moves one next.

**Case B was not built and is not needed.** The brief scopes a per-LEVEL `MinCellsAcrossConductor`
override for the case where the new floor lands between 40 and 67. It landed at 200, the blocked
design sits at 66.7, and at the mesher's own default of four cells across a 60 µm plate on the shipped
film is cell/separation 75 — also inside. **Building the override anyway would be adding a control
nothing now needs**, and the measurement that would have justified it (what the refinement costs in
unknowns) is not owed. The widened accelerated ceiling MIM-13 left open stays open and is untouched.

### 5. Two things measured on the way that are not about the film

- **`WorstLevelPair` grades a level pair on the cells of whichever level HAS any.** A problem whose
  upper level carries no polygon at all still produces a ratio — the through-line control was refused
  at cell/separation 750 with no cross-level block in the matrix. Not fixed here: it is a refusal
  firing where there is nothing to be wrong, which is a real defect, but it is `WorstLevelPair`'s and
  not this brief's, and every real multi-level run has metal on both levels.
- **The straddling cell takes its pitch from the NARROWEST metal on the two levels, not the widest.**
  At one cell across it is 40 µm — the feed's width — whether the plate is 60, 200 or 400 µm. This is
  the same shape as MIM-10 finding 3 (the shared tensor grid belongs to the whole run) and it is why
  `MinCellsAcrossConductor` is not OFFERED as a remedy for being past the floor. It does move the
  ratio the right way on this fixture — 1 → 2 → 4 is cell/separation 200 → 100 → 50 — so it is not an
  inert knob and the refusal does not call it one; what disqualifies it is that it is GLOBAL, which is
  exactly the gap the brief's Case B per-level override would have closed.

### 6. M5 — the acceptance is the structure that was refused, and it runs

`examples/PDK PCells/SpiralResonator` is MIM-10's own coil with a series 60 × 60 µm MIM capacitor on
its terminal — a second example layout beside `SpiralInductor.clay`, which is untouched. **The
capacitor is flat artwork rather than a `KIT_MIMCAP` instance**, so the gate needs no generator
process; the rectangles are the kit's own, computed from its published geometry at W = L = 60 µm.

The run: **three analysis levels, 1,451 cells, 2,456 unknowns (49 % of the dense ceiling),
cell/separation 127, and PASSIVE at every solved point.** Its |S11| null is at **2.77 GHz, −31.6 dB**,
with |S21| = −0.223 dB there, against MIM-10's composition route at 2.750 GHz, −0.083 dB, −29.6 dB.
**0.9 % in frequency and 0.14 dB** — inside the brief's 2 % and 0.3 dB. Before MIM-12a repaired the
kernel, the same structure put 15 of its 50 rows at |S11| above 0 dB and matched no deeper than
−15.3 dB at 2.24 GHz.

**The two routes are not the same circuit and the gate is stated as a band for that reason.** The
composition takes the coil's `.s2p` and an IDEAL 1.0838 pF; the all-EM run has the plate's own
capacitance to the backside metal, the MIM via's inductance and the Metal2 bridge in the matrix.
MIM-10's own closing section says those are missing from the cheap route and that 0.11 pF beside a
1 pF series element is a different resonator. **The 0.9 % is what they are worth**, and it is now a
measurement rather than a caveat.

**One thing about reading it: the resonance is the |S11| NULL, not the |S21| maximum.** On the
composed route the two coincide. Here |S21| is flat to 0.01 dB across the null while its loss slope
keeps falling, so its argmax lands at 2.85 GHz — a statement about the metal. The null is 12 dB deep
over the same span, and parabolic refinement of the three dB points around the grid minimum puts it
at 2.774 GHz.

**THE SWEEP IS NOT IN THE TEST SUITE, AND THAT IS A DEVIATION FROM THE BRIEF, STATED RATHER THAN
QUIETLY TAKEN.** M5 asks for it under `[Trait("Category", "Benchmark")]`. It does not fit: at 2,456
unknowns on three levels, with four calibration standards solved beside the DUT at every frequency,
a FIVE-point sweep in the Debug build a `dotnet test` run uses did not finish in an hour of wall
clock at 800 % CPU — larger on its own than the entire existing Benchmark tier, which the series'
own conventions say is where wall-clock-heavy work does NOT go. So the sweep is measured with the
CLI in Release and tabulated (`HISTORY.md` §MIM-14 Table 7, with the command that reproduces it),
and the test carries the half that actually moved and costs a second: the real `.cem` through the
real `EmRunService.Preflight` — the same call `circuitrf check` makes — not refused, with the plate
level in the run, the film in the medium, the via built as a chain across it, and the cell/separation
note naming the ratio and the 200 it is inside of. **A preflight that passes a setup the run refuses
is the drift R-aut4-2 exists to prevent, so this is not a weaker question about the thing that
changed.**

*(One number in that test is not the solve's: preflight reports cell/separation 60 where the solve
reports 127, because R-fed-1 grows a uniform feed lead on each port before the DUT is meshed and the
detail floor is a fraction of the artwork's extent. Both are inside 200. The test asserts its own
number and says why they differ, rather than asserting one figure for two meshes.)*

**A false start worth recording.** The first draft of the artwork drew the top plate 56 × 56 µm
inside a 60 × 60 bottom plate, i.e. an overlap 13 % smaller than the kit's, and the resonance came
back at ~2.95 GHz — 7 % high, exactly √(1.084/0.944). The plate that sets `C` is the TOP one and the
bottom is it grown by the enclosure, which is what `KIT_MIMCAP`'s own comment says and what a
hand-drawn copy of it is easy to get backwards.

### Gates

`Mim14DeembeddedFilmTests`. Four tests, one claim each:

- **T1** (Benchmark) — the three controls, all with no film in them: the through line passive and
  matched in its own reference, the d = 20 µm rung against the electrostatic value, and the reading
  not moving over three meshes.
- **T2** (Benchmark) — the ladder at the three rungs that decide it: 67 (where the real design sits),
  80 and 200 (where MIM-12 read the sign inverted). Positive, within 10 % of the electrostatic value
  on the same mesh, passive, and the fitted L the same at all three.
- **T3** (routine) — the constant is 200; two, three and four cells across a 60 µm plate on the
  shipped film all run, where the old floor refused every one of them; a 50 nm film at one cell across
  is still refused and still names the ratio. **ONE cell across is deliberately not in that loop** and
  for the same reason M9_3's fixture moved off it: at one cell across the straddling cell is the
  40 µm feed and the film is 0.2 µm, so cell/separation is 200 to within an ulp — the bound itself,
  where `>` decides on the last bit of a double rather than on anything measured. It lands inside
  today (199.99999999999517, because 103.2 µm − 103 µm is not exact) and that is luck, not a claim.
  Table 5 grades the rung; the test grades the three that are unambiguously inside.
- **T4** (routine) — `Im(−Y₂₁)/ω` against `−1/(ω·Im(−1/Y₂₁))` on a branch of known Q, and the L-C fit
  against an exact series L-C. No EM in it; this is the arithmetic the two Benchmark tests rest on.

Re-pointed rather than left red: `MimThinLayerTests` M9_1 (its refusing rung moves from 80 to 500, and
it now asserts that 80 RUNS), M9_2 (its ladder's last two rungs move to 500 and 1000), M9_3 (its
`PlanarLevelPair` fixture was `40 µm / 0.2 µm`, which IS the new floor to within an ulp — a
floating-point coin toss between two arms), and `MimCapacitorTests.M9_APlatePairPastTheFullWaveFloor…`
— whose 60 µm plate at the mesher's DEFAULT four cells across is cell/separation 75 and is now
permitted, so it meshes at one cell across instead. **That last one is the whole brief in one test:
the fixture that used to demonstrate the refusal now demonstrates the capability.**

M5's acceptance is `MimResonatorCompositionTests.MIM14_TheSpiralAndTheCapacitorArePREFLIGHTED_…`
(routine, ~0.2 s), beside MIM-10's own composition test so the two answers it compares are read from
one file. The sweep it does not run is in `HISTORY.md` §MIM-14 Table 7 — see the paragraph above for
why.

## MIM-13 — where the unknowns go on a spiral, and the two ceilings (2026-09-16)

`brief-em-mim-13-headroom-edge-mesh-and-ceilings.md`. All three items built. **Item 1 turned out to
be code, not wording, and one of the sentences it replaced was arithmetically false.**

### The verdict, in one paragraph

The brief's first action — capture the exact refusal from an edge-mesh-on spiral run before deciding
whether item 1 is code — was run headlessly on the shipped three-turn 10/8 µm spiral
(`examples/PDK PCells`). It meshes at **N = 9,316** over two metal levels and one via, is **REFUSED
against the DENSE 5,000**, and the refusal's closing clause read *"the accelerated solve would not
help either — this mesh is past its 12,000-unknown ceiling too"* **about a mesh of 9,316**. The
report of AIM "needing a multi-level kernel" is a SECOND, separate thing and it is a stale panel
message: `EmSetupEditorViewModel.AcceleratedSolveDisabledReason` disabled the checkbox outright,
citing an engine refusal in `PlanarSolve.SolveAt` that **P12 retired** when it built
`PlanarBorderedAimOperator` for exactly that class of mesh. So a user on a multi-level part could not
arm the accelerator at all, and was told why in a sentence that had stopped being true.

### Findings

**1. `acceleratedWouldFit` is false for TWO different reasons and the refusal said one sentence about
both.** `!accel && n <= AcceleratedUnknownCeiling && !problem.RequiresGeneralKernel` — so the
else-branch is reached both when N really is past 12,000 and when the mesh is MULTI-LEVEL, where
`UsesAcceleratedCeiling` withholds the wider ceiling by an open owner decision rather than because
the accelerator cannot run the mesh. `BuildRefusal` takes a `multiLevel` flag now and the two cases
get the sentence that is true of each. **The gate is an equality**: the same taper with and without
an explicit `MediumStack` meshes to the identical 6,543 unknowns, so the fixture isolates the ceiling
question from every other difference a second geometry would have brought.

**2. "matrix compression, which is not built" has been false since M5, in three places.** It was on
the dense-path clause of `BuildRefusal`, on its ACCELERATED clause — where it was being said about a
run that had the accelerator turned on — and it is the clause the brief quotes from
`PlanarSystem.GuardCeiling`. Every ceiling refusal now names BOTH numbers and which path each
governs, always, not only the one that happened to refuse: a reader given 30,000 and a single
ceiling cannot tell 2.5× over from 100× over, which is the whole distance between "coarsen this" and
"this tool cannot do it".

**3. The bulk/fan split is a MEASUREMENT and the obvious estimate is wrong.** `BuildGridLines` is
asked a second time with grading off, which is exactly the grid an edge-mesh-off run produces —
`hardX`/`hardY` (the conductor edges) do not depend on the edge mesh and the graded attractors feed
nothing else. Gated as an exact equality against a real edge-mesh-off mesh, and verified on the
spiral independently (121 × 101 lines with the fan, 57 × 53 without, against an edge-mesh-off run's
own 57 × 53). Estimating it instead — log_r of the pitch ratio times the attractor count — is wrong
wherever the marcher's rescale or the cap enforcement moved a line, which on real artwork is most of
them. Cost is one ungraded partition per axis, O(lines), no polygon scan, and only when grading is on.

**4. "Mostly rim" needed a measured quantity, and the brief's proposed one does not exist on the
artwork that needs it.** The brief names the pitch field's `climb` as the discriminator ("nothing new
is measured"). `climb` is only computed when a `PlanarCurrentModel` is on — the shipped spiral runs
at `None`, so there is no field and no `climb` — and it is in any case a statement about how far a
fan has to climb, not about how much of the artwork is rim. What is measured instead is
`PlanarMeshReport.RimFraction`: perimeter × bulk pitch ÷ area, over every conductor polygon's outer
AND hole rings, clamped to 1. On a strip of width w at pitch p it is exactly 2p/w. **Taken from the
POLYGONS, never from the mesh**, so it means the same thing with the edge mesh on and off — a
rim fraction read off realised cell sizes would fall the moment the fan refined the rim, which is the
one change it has to be independent of. Cost is one pass over vertices.

**5. The two populations are nowhere near the threshold, from either side.** Measured: the shipped
three-turn spiral **100.0%**, a 10 µm trace meshed 2 across **100.0%**, the 2026-08-14 Klopfenstein
taper **5.7%**, the shipped 5.8 GHz patch **16.7%**, a 40 mm plate **1.2%**. `MostlyRimFraction` = 0.5
is the literal reading of "mostly" and any value in 0.15–0.85 classifies all five the same way, which
is what makes the discriminator worth having rather than the constant worth arguing about. **The one
shape that lands near it is a single trace meshed 4 across, at 50.2%** — which is the honest answer
for that shape, not a misclassification.

**6. A warning that is flattened at the boundary is not a warning.** `PlanarMeshReport.Notes` is
`IReadOnlyList<string>` and both consumers did `EmFindings.AsNotes(report.Notes)`, so an `EmSeverity`
attached in the mesher could not have survived the trip to the Messages panel. The mesher's own list
is `List<EmFinding>` now (a string still converts implicitly, so every existing `notes.Add("…")` is
untouched), `Notes` is derived from it, and `PlanarMeshReport.AllFindings` is what
`EmRunService.Preflight` and `PlanarKernel.Solve` read — with a fallback to "every note is a Note" so
a report built by an older call site loses nothing.

### What was built

- `PlanarMeshReport.Budget` — cells, unknowns, per level, vertical, against BOTH ceilings with the
  governing one named and the overage as a ratio, plus the grid's bulk/fan split. Inserted at
  `Notes[0]` (a summary at the bottom of thirty sentences is not a summary) and emitted on an `Ok`
  mesh as well as a refused one, because seeing the headroom before it runs out is most of the point.
- `PlanarMeshReport.{Findings, AllFindings, CeilingUnknowns, AcceleratedCeiling, GridLinesX/Y,
  BulkGridLinesX/Y, RimFraction}`.
- `SurfaceMesher.RimFraction`, `SurfaceMesher.MostlyRimFraction`, `SurfaceMesher.BuildBudget`.
- `BuildRefusal(..., multiLevel)`; `SurfaceMesher.GuardCeiling` and `PlanarSystem.GuardCeiling` both
  name both ceilings. **`PlanarSystem.GuardCeiling` does NOT gain a remedy list** — it has no mesh
  geometry left to ask which knob binds, and naming one unconditionally from there is the
  2026-08-14 owner report's own defect in a second place. It points at the mesh report instead.
- The panel: `AcceleratedSolveDisabledReason`'s multi-level arm is gone (the control is live on a
  multi-level layout) and `AcceleratedSolveCeilingNote` says what it does and does not buy there —
  speed and memory, not headroom.

### Not done, on purpose

- **Neither ceiling moved.** The brief excludes it and the measurements that would inform it are
  their own work (`PlanarSystem.ResidentPhrase`, `PlanarBudgetTests`). What changed is only that a
  user can now see 1.86× over rather than infer hopelessness from one number.
- **`UsesAcceleratedCeiling` is untouched** — still `aimOn && !multiLevel`. P12's reason stands: the
  bordered accelerator's TIME is set by N_z rather than by N, so a ceiling stated in N alone would
  promise something a via field does not deliver. The refusal now says that in the user's words
  instead of leaving it as an unexplained withholding.

## MIM-12a — the thin region's own reflections are a closed form, and the fit was extrapolating them (2026-09-16)

`brief-em-mim-12a-transmitted-image-peel.md`. **MIM-12 step 0's finding 6 is built.** The kernel is
repaired; the electrostatic plate capacitance is now one number across the band and across two meshes;
the de-embedded full-wave ladder is NOT re-run and the refusal that rests on it has not moved.

### The verdict, in one paragraph

A thin region is a Fabry-Perot cavity. Its multiple reflections are a geometric series of images at
depths that are multiples of the film thickness, and **the DCIM sampling path never reached them** —
the series decays like `e^{−k_ρ d}` with d = 0.2 µm, i.e. it is structure out to k_ρ ≈ 5e6 m⁻¹, while
the widest path this repository walks (`CalibratedPathProduct` = 20 on a 106 µm stack) stops at
1.9e5. The fit therefore extrapolated the film's entire contribution, and a plate capacitance is a
`d/cell` difference of two such fits. `LayeredSpectralGreens.ThinRegionImagesAtHeights` derives the
series from the same generalised-reflection cascade `AsymptoticAtHeights` already takes,
`Dcim.FitAtHeights` peels it out of its samples before Prony runs, and `ShallowImageCore` integrates
it back over a cell pair in closed form — **MIM-8's own machinery, with exact images in it instead of
fitted ones.** The cross-level kernel goes from 2.7e-2 wrong at 1 GHz to **1.4e-6**, and MIM-8's
electrostatic instrument from **−0.54 / 1.34 / 1.60 / 1.00** at 1 / 2 / 3 / 10 GHz to **1.006 at all
four, on two meshes.** Every table is in `HISTORY.md` §MIM-12a.

### Findings

**1. The brief's premise is right about the crossing and wrong about the face, and the face is half
the error.** The brief's case is "one side of the difference has its leading term exact and the other
has none of it" — the same-level pairing's `2/(ε₁+ε₂)` being what `AsymptoticAtHeights` already
returns there. The LEADING term is exact; **the cavity's round trips are not.** A source on the floor
of the film sees `R_top e^{−2k_ρ d}` come back, which is the same unresolved structure one interface
over. Measured with the crossing alone peeled, the shipped capacitor's ladder read **0.843 at 1 GHz**
— still 16 % short — and the residual was the same-level block at 2e-3 … 2e-2 over the whole cell
range. **Both cases are built**, they share one derivation and one cavity, and `T3` is the
measurement that demanded the second.

**2. The vector half does not have this defect, and the reason is structural rather than a
tolerance.** The brief leaves `G_A` "unmeasured here, not excluded". Measured: on a non-magnetic stack
`R^h = 0` at every interface, so the TE cavity's round-trip factor `q` is **zero** — the series is ONE
exponential across the film and nothing at all on a face, and one exponential is exactly what a Prony
fit represents. The fitted `G_A^xx` is **4.7e-6** wrong at 1 GHz on the same pairing where `G_q` is
**3.7e-1**. **The defect is the geometric SERIES, not the crossing**, and MIM-8's decision not to build
the vector block's closed-form cell-pair integral stands. `T5`.

**3. The brief's own A_eff gate contradicts its own remainder measurement and cannot be met.** It asks
that the assembled series reproduce `A_eff` → 0.164856 to 1e-4, one paragraph after measuring the
peeled remainder at 1.1e-3 **of the kernel**. `A_eff` is the kernel times `4πR`, so the series alone
must miss it by exactly that: measured 0.165043 against 0.164856, **1.14e-3**. Reaching 1e-4 would
mean the remainder was not there, and the remainder being there is what the fit is still for. `T2b`
pins it at the achievable figure with that reasoning attached.

**4. The conditioning got WORSE while the answer got right, which is the sharpest available refutation
of MIM-12's own diagnosis.** MIM-12 could only say there was nothing for iterative refinement to
recover, because the answer was already wrong before the solve. The same 32-cell electrostatic system,
at the same order of conditioning, now produces the right answer — and the treated matrix is about
**10× worse conditioned** than the untreated one, because its cross-level entries now carry the
near-cancelling structure that IS the capacitor. The full-wave numbers moved the same way:
`cond(Z)` 1.443e9 → 1.96e9 and `min/max eig(Re Z)` 1.407e-11 → 9.8e-12. **An answer that improves by
two orders while its matrix gets harder is not a dynamic-range failure.** `T3` in
`Mim12KernelFitTests`.

**5. `DirectScalarKernel` now works, which is the brief's own completeness test.** The brief says so
in as many words: *"This phase should make that path work too, and if it does not, that is evidence the
peel is incomplete."* It read 0.75 at 1, 2 and 3 GHz and was 24 % low, because the images it subtracted
were the FIT's and the remainder it tabulated still ran 2.3e4 → 87 over the mesh's own ρ range. Those
images are now the derived series, and the two routes — one fitted, one integrated directly — agree to
**under 2 %** (1.006 against 1.015). `T5b` asserts the agreement rather than each number, because it is
the agreement that says the peel is complete.

**6. Everything outside the derivation is declined by name, and one of the declines is a real case.**
Scope is ONE thin region, with the pairing either across it or on one of its faces. Declined: a pairing
more than one region apart, a point in the INTERIOR of a region, a semi-infinite region (which is every
ordinary single-level run, and therefore the case that must cost nothing), the two VERTICAL components,
and **a face with a thin region on BOTH sides** — two coupled cavities, whose series is not the product
of theirs. The last one is reachable on a real stack: the shipped MIM technology's upper plate sits at
the bottom of a 2.8 µm region with the 0.2 µm film below it, so on a coarse enough mesh both are "thin"
and that pairing is declined. Its measured cost is confined to ρ ≲ 1 µm and it does not move the
ladder. `T4`.

### What was built

**`LayeredSpectralGreens.ThinRegionImagesAtHeights` + `ThinRegionImageSeries`** — the derivation, from
the cascade rather than transcribed. The standard four-term form of the equivalent line's voltage for a
source in region c collapses, at the cavity's two faces, to `(Z_c/2)(1+R_dn)(1+R_up)e^{−jk_z d}/(1 − g
e^{−2jk_z d})` with `g = R_dn R_up`; at k_ρ → ∞ every generalised reflection becomes the local Fresnel
coefficient, for exactly the reason the same-region branch gives, and the denominator expands to the
series. `LocalFresnel` is factored out of `AsymptoticAtHeights` so the two cannot drift; that refactor
is bit-identical (the wall branch's `m == 1 && IsWall(0)` is `IsWall(m−1)` by construction).

**`Dcim.FitAtHeights(…, transmitted)`** — the peel goes into the NUMERATOR, in the same units as the
two asymptotes, and into the far-field sum rule with them. **The fit is what had to change**, and that
is a finding rather than a design choice: subtracting an exact series from an inaccurate fit leaves the
fit's error standing on a remainder a thousandth its size, which is worse than useless.
`DcimModel.TransmittedImages` carries what was peeled, and `EvaluateAtHeights` adds it back — so the
model is always the WHOLE kernel and what the peel changes is which part of it is exact.

**`PlanarKernelTerms.FromDcimAtHeightsMinusShallowImages`** removes them unconditionally. A fitted
image might or might not be resolved by the mesh and `shallowDepthM` is that question; a peeled cavity
series is not a candidate for it.

**`PlanarKernelSet.Model(kernel, zA, zB, thinnerThanM)` and `GetWhole`.** The peeled fit is cached
under the threshold; with no peel both hand back the existing shared objects, which is what keeps every
other run bit-identical. `GetWhole` exists because the far view used to be `Get` — the same object, and
therefore free — and with a peel it is not: taking it from `Get` would pay for a second
`Dcim.FitAtHeights` AND quietly hand the far cell pairs the less accurate of two representations of one
kernel. A peeled pairing still costs **one** fit (`T6`).

### Bit-identity, and it is wider than the brief scoped it

The brief scopes the trigger to "a pairing that crosses a region AND whose crossed thickness the mesh
does not resolve", and finding 1 widened it to a face of such a region. The trigger is still MIM-8's
own — the cell against the separation, `PlanarFillSettings.ShallowImageCells`, one number for one
question — so **`ShallowImageCells = 0` is still exactly the pre-MIM-8 arithmetic** and MIM-8's
airbridge rows at cell/separation 1 are still bit-for-bit identical. What DID move, and is re-taken
rather than argued away, is `MimThinLayerTests.T1`'s cell/separation-20 digest: that row is the thin
cross-level block and changing it is the phase. **The whole of `Engine.Tests` was otherwise unchanged**
— every L8/L9 gate, the via physics, AIM, the loadpull tiers — and the only other failures were
MIM-12's own T1/T2/T3/T5b literals, which this phase is expected to replace and which are re-pointed
with the old numbers kept beside the new.

### What was NOT done — M4, the de-embedded ladder, and the floor does not move

> **Superseded at MIM-14 (2026-09-16).** The ladder WAS re-run, on a fixture that passes the three
> controls this section says the old one could not; the floor is 200 and the spiral-plus-capacitor
> runs. Every diagnosis below is still correct about MIM-12a's own fixture — see §MIM-14 for the
> cause of each of its four symptoms.

**`PlanarLevels.FullWaveCellOverSeparation` is still 40 and the refusal still fires.** The brief's M4
asks for MIM-12's own de-embedded two-port ladder to be re-run and the constant re-pointed. It has not
been, and the reason is a measurement rather than a shortage of time:

- **MIM-12's harness is not in the tree.** Its ladder was a scratch measurement; `HISTORY.md` §MIM-12
  records the numbers and not the fixture.
- **A fresh equivalent fixture cannot grade anything yet.** Two overlapping 80 × 80 µm plates with edge
  ports on the two levels, de-embedded: its series capacitance read from `−1/Y₂₁` is **6-10× below the
  electrostatic mutual capacitance of the same mesh** — and it is that low at **d = 20 µm,
  cell/separation 2**, where no film is involved and which this floor already admits. It also HALVES
  with every mesh refinement (0.0068 → 0.0045 → 0.0027 → 0.0014 pF over four meshes while the
  electrostatic value holds at 0.026-0.028 pF), and a plain single-level through line on the same
  port setup comes back 8 % non-passive. So the reading is governed by the port/de-embed fixture, not
  by the film, and building one that is not is fixture design rather than this brief's work.
- **This is on the record elsewhere already.** §MIM-3 finding 4 measured that a raw S reading in this
  engine is the PORT and not the structure (a matched 50 Ω line reads |S21| = 0.0706), and its own
  de-embedded instrument had to truncate the stack to get a port at all. MIM-8's gate is electrostatic
  for the same reason, in its own comment.

**So the floor stays where the last measurement put it, and it is now CONSERVATIVE rather than
current**: the kernel defect its rungs measured is gone, and how far a de-embedded two-port reaches on
the repaired kernel is unmeasured rather than known. MIM-9's instruction — leave the refusal standing,
a fix that removes it instead of moving it is how the next regime becomes silent — is followed. Every
run-visible sentence that quoted the old ladder now says that it predates the repair and has not been
re-run, and every one that quoted the electrostatic 1 % "AT 10 GHz only" now quotes 1.006 across the
band. The shipped spiral-plus-capacitor `.cem` was likewise not run.

### Gates

`Mim12aThinRegionPeelTests` — T1 (the derived coefficients against the quasi-static hand formula to
1e-6), T2 (the peeled remainder flat to 1 % over ρ ∈ [1 nm, 15 µm] at 1 and 10 GHz, against direct
Sommerfeld), T2b (the brief's own A_eff gate, at the figure that is achievable and why), T3 (the
same-level cavity, and the measurement that made it necessary), T4 (six declines, each naming itself),
T5 (the vector block has no series), T6 (the whole-kernel view is the same kernel and the same fit),
T7 (the accelerated path's removed list is the derived series). 9 tests, ~1 s.
`Mim12KernelFitTests` T1/T2/T3/T5b re-pointed; `MimThinLayerTests` T1's thin row re-taken and T9
strengthened to assert the new regime. Direct Sommerfeld integration is the acceptance instrument
throughout — `DcimModel.FitResidual` is blind to all of this and reports its own remainder as eight
decades smaller than it is.

## MIM-12 step 0 — the capacitor's digits are lost in the KERNEL FIT, not in the solve (2026-09-16)

`brief-em-mim-12-full-wave-thin-film-readback.md` §"The correction", step 0: *"The measurements above
cannot separate two different losses, and the remedies for them are different… Do this before
committing to anything below."* It has been done, and **it answers (b) and retires the brief's own
diagnosis with it.** Every table is in `HISTORY.md` §MIM-12.

**Nothing about a run's answer changed.** `PlanarFillSettings.DirectScalarKernel` is new and is OFF;
with it off not one entry of the scalar potential matrix differs. What changed is four sentences in
the code that were not true, and what is added is the measurement that says so.

### The verdict, in one paragraph

A plate pair's capacitance is what is left after the same-level and cross-level potential
coefficients nearly cancel — on the shipped 0.2 µm film under a 15 µm cell the difference is
`d/cell` = 1/75 of either. **The two pairings are two INDEPENDENT Prony fits**, so the capacitance
inherits `cell/d` times whatever relative error they carry. The exact kernel does not move with
frequency at all on this structure (5.656193e3 at 1 GHz against 5.655920e3 at 10 GHz by direct
Sommerfeld integration, a converged oracle); the FIT carries **8.3e-5 at 10 GHz and 2.7e-2 at 1 GHz**.
Multiply each by 75 and you get 0.6 % and 200 %, which is the measured 1.003 and the measured −0.545.
**Every bit of the answer's frequency dependence is fit error.**

### Findings

**1. It is (b), and the missing digits are fourteen decades from where the brief looked.** The brief's
step 0 separates "the SOLVE loses digits to conditioning", which iterative refinement recovers, from
"the matrix ENTRIES never carried the small quantity", which no refinement can repair. It is the
second — and the entries lose their digits to a fit whose own error is 1e-2, not to double precision
at 1e-16. **The brief's item 1 is inapplicable rather than merely unlikely**, and so is its item 3:
both are remedies for `cond(Z)`, and the answer is already destroyed where nothing is ill-conditioned.

**2. The two numbers the brief diagnoses from are correct, and they are not the cause.** `cond(Z)` is
1.443e9 at 0.5 GHz and `min/max eig(Re Z)` is 1.407e-11 — reproduced to three figures, and pinned in
`Mim12KernelFitTests.T3` so they stay on the record. The refutation is one row above them in the same
table: the ELECTROSTATIC instrument is a 32-cell system **conditioned at 575**, and at 1 GHz it
already reads −0.545. A factorisation that loses nine digits cannot be what breaks an answer that is
broken before the factorisation.

**3. The brief's exclusion 3 — "not the electrostatic fill, MIM-8 did what it says it did" — is true
only at 10 GHz, and this is the part worth carrying forward.** MIM-8's ladder is sound; its fixture
simply fits the kernel at the problem's own `MaxFrequencyHz` and never calls
`Dcim.ForStackAtFrequency`, which is what `PlanarFrequencyKernel.Fit` — i.e. every run — calls. On the
same 60 µm capacitor with the same instrument, `C/(ε₀εᵣA/d)` read 0.9995 at 10 GHz and
**1.60 / 1.34 / −0.54 at 3 / 2 / 1 GHz**. (Since §MIM-12a: 1.006 at all four.) The general shape of it: **a gate that constructs its own
kernel is a gate that can stop measuring what the application does**, and here the divergence is a
whole sign. `ValidatedCellOverSeparation`'s documentation now carries that condition, and so do the
three run-visible arms of `LevelSeparationNotes` that quoted its 1 % at a reader in the MMIC band.

**4. The low-frequency widening is RIGHT and removing it is not available.** The obvious reading of
finding 3 is "the widening broke the MIM run". It did not: on an ordinary one-level GaAs problem at
1 GHz `Dcim.ForStackAtFrequency` takes `G_q` from **46 % wrong to 4.3e-4**, and every GaAs run below
~30 GHz is in that regime because the default `PathExtent` of 300 reaches a product of 0.63 on a
100 µm slab. What the widening cannot do is carry a 0.2 µm film AND a 106 µm stack in one order
budget — a 530:1 range of scales in one fit of ten exponentials — and the small end is the whole
capacitor. **The defect is a property of the STACK, not of the knob**, which is why tuning the knob
is not the fix either: the window of `PathExtent` in which the capacitance is right is
frequency-dependent and only a factor of ~2 wide at 2 GHz (`HISTORY.md` Table 4).

**5. `FitResidual` reports its own remainder as eight decades smaller than it is, and cannot see
this.** At 1 GHz, ρ = 0.02 µm, the cross-level decomposition says its remainder is 8.9e-7; the true
remainder is **2.3e4**, on a kernel whose value there is 6.5e4. The residual measures the fitted
exponentials against the samples, and it is the SAMPLES that are of the wrong function — so the fit
is self-validating in a metric that is blind to the failure. Anything that wants to know whether a
fit is good enough here has to ask the direct integral.

**6. The remedy is the brief's item 2, one level upstream of where the brief puts it.** Item 2 says to
compute `P_self − P_cross` as one closed form rather than as two numbers subtracted. The purpose is
right and the location is not: the error is in the KERNEL, so an exact cell-pair difference would
integrate a wrong difference exactly. `PlanarFillSettings.DirectScalarKernel` (below) is the
measurement that establishes this, and it points at the structural cause —
**`LayeredSpectralGreens.AsymptoticAtHeights` returns zero coefficients for a CROSS-REGION pairing by
design** (MIM-8's own finding 1), so a thin cross-level pairing's entire near field rests on fitted
images with no closed form underneath it, while the same-level pairing beside it has an exact
`1/ρ`. Two decompositions of different exactness, subtracted. Giving the cross-region pairing its own
`k_ρ → ∞` asymptote is what would make the difference exact where the error is.

### What was built

**`PlanarFillSettings.DirectScalarKernel` / `ScalarTableSamples`, and `PlanarKernelSet.
GetDirectMinusShallowImages`** — R-zz-3's `DirectVerticalKernel` construction, one block over: keep
every part of the decomposition that is exact (the extraction coefficients, and MIM-8's list of
shallow images and their closed forms) and replace only the part that is fitted, with a radial table
of direct Sommerfeld integrations. **Off by default and bit-identical when off.**

**It is a measurement, not yet a fix, and that is stated rather than implied.** With it on, the
answer stops depending on the frequency and on the mesh — 0.75 at 1, 2 and 3 GHz on two meshes,
against −0.54 / 1.34 / 1.60 — and it is 24 % low. The residual is diagnosed: the images it subtracts
are the FIT's, and at 1 GHz the fit does not have the film's structure in it, so what is left still
runs 2.3e4 → 87 over ρ = 0.02 … 0.93 µm and a linear table at the mesh's own 0.3 µm spacing cannot
carry it. Finding 6 is where that goes next.

### What was NOT done, and the brief's gates that are therefore unmet

> **Superseded at MIM-14 (2026-09-16): the floor is 200.**

**The brief's five gates are not met and no floor moved.** `FullWaveCellOverSeparation` is still 40,
`ValidatedCellOverSeparation` is still 200, and a run past the floor still refuses — which is the
right state while the answer past it is still wrong. Specifically not done: the ladder re-run within
10 % at cell/separation 200, the control's 3.427/3.429/3.441 fF re-measurement (it is a single-level
structure and nothing here can have moved it), the bit-identity re-check on the airbridge fixture
beyond the scalar-matrix identity `T5` asserts, and the user's spiral-plus-capacitor resonance.

**Iterative refinement was not built** — finding 1 makes it inapplicable, and building it anyway
would put a mechanism in the tree that the measurement says cannot act here.

**The cross-region static asymptote was not derived.** It is finding 6, it is the real fix, and it is
a formulation change rather than a tuning one.

### Gates

`Mim12KernelFitTests` — T1 (every bit of the frequency dependence is fit error, against a
tail-converged oracle), T2 (MIM-8's own instrument, at the four frequencies, with the run's own fit),
T3 (conditioning is not the cause, and the brief's own two numbers pinned beside it), T4 (the
widening is what makes an ordinary run work), T5 (`DirectScalarKernel` off changes not one entry),
T5b (`Category=Benchmark`, 2 m 33 s — what the direct kernel fixes and what it does not).
Routine tier: 8 tests, 4 s.

## MIM-9 — the three notes that sent a user the wrong way (2026-09-16)

`brief-em-mim-9-thin-film-diagnostics.md`. **Every change here is a reporting change on top of an
answer that does not move**, plus one refusal. Nothing in the fill, the peel or the solve was
touched, and the whole of `Engine.Tests` is unchanged apart from two sentinels named below.

The defect cost a user days of work on the wrong thing, and the engine was not silent — it printed
more than thirty notes on the fixture. The sentences that fired **named the wrong cause**, and the
one sentence that could have helped **said the run was fine**.

### 1. Two constants, not one, because they measure two different things

`PlanarLevels.ValidatedCellOverSeparation = 200` certifies the cross-level FILL and the
ELECTROSTATIC plate capacitance — `PlanarFill.ScalarPotentialMatrix` driven by a 1 V / 0 V
instrument with no port in it. It is still true at 200 and no number in it changed.

`PlanarLevels.FullWaveCellOverSeparation = 40` is new and is a different quantity: the range a
DE-EMBEDDED s-parameter of a close level pair is measured over, which is what a user actually reads.
MIM-12's ladder — one plate pair, one set of feeds and ports, the straddling cell pinned at 40 µm,
only the film thickness moving — reads `C/(ε₀εᵣA/d)` = 1.13 at cell/separation 20 and 1.05 at 40,
then **−1.02 at 80 and −1.52 at 200**. The capacitor reads as an inductor.

**Quoting the first at someone reading the second is the whole defect.** The shipped note said "the
closest conductor levels are resolved by the mesh … the extracted plate capacitance within 1% of
ε₀εᵣA/d" at cell/separation 200, and every clause of it was TRUE. A run whose published capacitor
had the wrong sign was reading it. The reassuring arm is now bounded by 40, not 200, and the arm
between the two states what was validated and what was not in those words.

### 2. The floor is a REFUSAL — `PlanarSolve.LevelSeparationVerdict`

R-emsev-4, deferred by MIM-8 **conditionally**: past its own bound the answer was *unmeasured*
rather than *wrong*, and refusing on "unmeasured" invents a limit rather than reporting one
(R-prt-13). **The condition has been met from the other side.** The measurement exists and it does
not say unmeasured — it says the element inverts its sign between 40 and 80. So the refusal is
earned, in `PlanarLevels.CanRepresentVias`' own words: approximating it would give a plausible wrong
answer rather than an obvious failure.

It is a refusal rather than a note because of what the answer looks like past it: complete, smooth,
reciprocal, plausible, and **converged** — 2, 4 and 8 cells across give σ_max 1.7322, 1.7439, 1.7491,
moving slightly the wrong way. No mesh control reaches it.

Wired in two places and both matter: `VerticalRangeVerdict` (so `PlanarSolve.Run` refuses before a
matrix is filled) and `EmRunService.Preflight` (so `circuitrf check` refuses the same setup without
solving — a preflight that passes a setup the run refuses is the drift R-aut4-2 exists to prevent).
**The notes come back WITH the refusal**, because a refusal on its own takes away the sentence
explaining the scale it was measured on.

**The remedies it names are only the ones that act**, which is §3.5's trap and item 1's subject:
thicken the film, or take the upper level out of the run and model the part as a circuit element.
The three the old sentence offered were each measured inert on this very structure — Cells per
wavelength 20 → 400 leaves σ_max unchanged to four decimals (that knob sets the pitch ALONG the
current; the plate's own width sets it across), 2 → 4 → 8 cells across moves it the wrong way, and
there is no well-conditioned sub-band to narrow to.

**`FullWaveCellOverSeparation` is a measurement's name and MIM-12 will raise it.** Re-point the one
constant; a fix that removes the refusal instead of moving it is how the next regime becomes silent.

### 3. The NOT PASSIVE attribution follows the counters — and is written ONCE

The shipped sentence named the de-embedding as "the usual cause" on every non-passive run. On the
class of run that produced it the peel is innocent and **the engine already computes the number that
says so**: `DeembedErrorFloor` reads 1.6e-3 against a non-passivity excess of 0.73, three decades
apart. The identical structure with the second level removed — same artwork, feeds, ports and
technology — is passive at σ_max = 0.9986.

`PlanarSolve.NonPassivityCause(excess, peelErrorFloor, pair)` decides it from those counters. Three
arms: a conductor pair past the full-wave floor; the de-embedding, **kept verbatim in substance
because it is right for the case it was written for** (the shipped spiral's bottom decade, where the
floor really does reach the size of the excess) but now EARNED rather than assumed; and *not
attributed*, which quotes the number that exonerates the peel so a reader told "not the
de-embedding" does not go and check it anyway.

**The pair arm is pre-empted from `Run` today** — such a run refuses at §2 before a matrix is filled
— and it is there rather than deleted because MIM-12 raises the floor, and the band between the old
floor and the new one becomes runnable. Its gate drives the function directly.

**And it is written in exactly one place.** The guess existed TWICE, independently:
`PlanarSolve.PassivityNote` for the panel and `EmSnpProvenance.ValidityCaveats` for the `.s2p`
header. Correcting one leaves the other wrong in every file already on disk — and the file is the
copy that outlives the session and is the first thing anyone reads six months later. The engine
decides once, carries it on `PlanarSolveResult.NonPassivityCause`, and both read it.

**It is ASCII, and that costs the panel a subscript.** Touchstone is written in an encoding
`EmSnpProvenance` transliterates to; a σ or an a₂₁ arrives there as `?`, measured on that very line.
One function producing two spellings is two chances to drift.

### What is past the floor is the GEOMETRY against the film, not the technology

Worth knowing because the brief's own framing invites the opposite reading. The shipped MMIC
technology's 0.2 µm film does not by itself put a capacitor past the floor — **the straddling cell
does**, and it is set by every gridline the artwork puts inside the plate. Measured on this
repository's own fixtures at `CellsPerWavelength: 20`:

| drawn | largest straddling cell | cell/sep | |
|---|---|---|---|
| 10 × 10 µm plate, MIM via + Metal2 strap | 2.5 µm | 12.5 | runs |
| 60 × 60 µm plate, MIM via + Metal2 strap | 3.667 µm | 18.3 | runs |
| 60 × 60 µm plate, fed on Metal1, no via inside it | 60 µm | 300 | **refused** |

The via and the strap are what cut the cell: they are shapes on the shared tensor grid, so their
edges subdivide the plate underneath them. MIM-12's own fixture — a 60 µm plate whose straddling
cell it reports as 40 µm — is the third row's shape, not the second's. So a user with a strapped
capacitor may well keep running, and a refusal is not a statement about their process.

### What this cost elsewhere — two sentinels, no behaviour

`PeelConditioningTests` matched the peel-conditioning note by the bare token `DeembedErrorFloor`,
which the attribution now also names — deliberately, so a reader told the peel is or is not the
cause knows which diagnostic says so. Both sentinels were tightened to that note's own closing
clause, `"The DeembedErrorFloor result estimates"`, which is what those lines always meant. No
published number moved; `Engine.Tests` is otherwise unchanged at 2,519.

### Gates

`MimThinLayerTests.M9_1/M9_2/M9_3` and `NonPassiveCaveatTests`. M9_2 is the one worth knowing about:
it walks cell/separation 5 → 500 and asserts that **"resolved by the mesh" and the refusal cannot
both fire**, as an equality rather than two separate claims, so the two can never drift apart.
`NonPassiveCaveatTests.TheAttributionIsWrittenInExactlyOnePlace` is the source scan, on
`Authoring.cs`' terms: comment-stripped, over all of `src/`, exactly one file may carry the
attribution prose.

## MIM-10 — the LC resonator, closed without the capacitor in the EM (2026-09-16)

`brief-em-mim-10-lc-resonator-without-the-capacitor.md`. **The brief sized itself as documentation
and a kit change, and that sizing survived** — the one code change here is a reporting change on the
`.s2p`, and everything else is measurement and a page in the example workspace's `README.md`.

The recipe: EM everything except the 0.2 µm gap and put the closed-form capacitance in the circuit
beside the result. It is not a workaround invented because MIM-12 is weeks away — it is what a MMIC
designer does anyway, and it is now the second remedy MIM-9's own refusal names.

### The two gates, both measured, both reproduced from the REAL layout

The brief's step 0 was measured on a RECONSTRUCTION — the capacitor deleted and a port placed where
it had attached — and said so, asking that it be reproduced from the owner's own file before being
quoted. It was: the owner's `SpiralInductor.clay` with the `KIT_MIMCAP` instance removed, its feed
rect removed and port 2 reseated on the landing pad the capacitor was abutting reproduces the
brief's table **to every digit it printed** (σ_max 0.99775 / 0.99764 / 0.99748 at 1 / 2 / 3 GHz,
`−1/Y₂₁` 0.21 + 37.11j / 0.57 + 44.77j / 0.69 + 58.91j Ω). The reconstruction was right.

Composed with the kit's own 1.0838 pF through an ordinary `.cnl` — an `SnP` component, a `C`, two
ports and a `sparam` — that is a series resonance at **2.750 GHz, |S21| = −0.083 dB,
|S11| = −29.6 dB**. Tables in `HISTORY.md` §MIM-10.

### Three findings the measurement added

**1. The answer does not depend on the mesh, and the failure it replaces did.** The composed
resonance is 2.750 GHz / −0.083 dB / −29.6 dB on the mesh the user ran (transmission-line current
model, detail floor divisor 10, 2 cells across) and on the shipped example's defaults alike —
identical to three decimals, from EM sweeps whose own `−1/Y₂₁` differs by 1.5 %. A number that
survives its own mesh controls is what a designer can design against.

**2. The coil alone is NOT passive at the bottom of the band, and the brief's "passive at every
point" holds only from 1 GHz up.** A 0.5–6 GHz sweep returns σ_max = 1.016 at 500 MHz and 1.002 at
750 MHz on the user's mesh (1.013 at 500 MHz on the example's defaults), with NEGATIVE series
resistance at both. Everything from 1 GHz is clean and monotone. This is not new and not MIM-10's to
fix — it is the spiral's own bottom-of-band de-embedding conditioning — but a recipe that hands an
`.s2p` to an interpolating SnP component has to say so, because the interpolant reads every row it
is given and not only the ones near the answer. The README says to move the sweep's lower edge
rather than to ignore the rows.

**3. MIM-9's cell/separation table is a statement about a FIXTURE, not about a part.** Its second
row — a 60 µm plate with its MIM via and Metal2 strap, straddling cell 3.667 µm, cell/separation
18.3, runs — is true of that capacitor drawn on its own. The SAME capacitor at the end of this
spiral is meshed at 33.992 µm, i.e. **170**, and is refused. The straddling cell belongs to the
WHOLE run: one shared tensor grid, sized on a layout 500 µm across at 20 cells per wavelength.
**`DetailFloorDivisor` was the obvious explanation and is measured NOT to be it** — 10 and 200 give
33.992 µm alike. Which control does reach it was not chased here; it is MIM-13's subject, and the
refusal already names the knobs measured inert.

So the user's own resonator, which produced this round, is a **refusal** today rather than a wrong
answer — in 0.5 s, before a matrix is filled — and the refusal's remedy is this brief.

**And one trap the recipe walks straight into: an SnP's `File` must be QUOTED.** `CnlReader` resolves
a relative Touchstone path against the `.cnl`'s own folder only inside its `pexpr[0] == '"'` branch;
unquoted, the string reaches the model verbatim and is looked for relative to the process working
directory, so the same file "is not found" from one folder and loads from another. This is already
known and already handled where circuitRF writes the file — `CnlWriter.FormatParam` quotes it
unconditionally and says why — but a hand-authored `.cnl` is exactly what this recipe asks a user to
write, so the README states it beside the snippet and the gate's own `.cnl` is quoted.

### The code change: the port map goes on the `.s2p`

`EmSnpProvenance.PortMap`, written into the header beside the hashes:

```
! circuitRF-EM port 1: '1' on 'Metal1' at (-110, -125 um), edge, de-embedded, 50 Ohm
! circuitRF-EM port 2: '2' on 'Metal1' at (-155, -46 um), edge, de-embedded, 50 Ohm
! circuitRF-EM: port N of this file is the label named on the 'port N' line above. …
```

**MIM-9's own finding, one case further on.** The run already says all of this in its notes; MIM-10's
whole recipe is that the result is read by a NETLIST rather than by the person who ran it, often on
another machine and months later, where the notes are gone. Two ports of a reciprocal part swapped
— and a spiral and a capacitor are both reciprocal — gives a curve that is smooth, passive and
wrong, with no symptom to notice. The reference impedance rides the same line because Touchstone
states ONE `R` for the whole file, so a run whose ports differ has already lost that in the format.

Three things worth knowing about it:

- **The label names come from the extraction, index-aligned, never re-derived.**
  `EmPortExtractionResult.SourceLabels`' own note says why: a label whose text names no number is
  auto-numbered in document order, and a second copy of that ordering is free to drift.
- **It takes level NAMES rather than the `PlanarProblem`**, which is all it reads — so the gate
  drives the function directly with two ports and two labels, in 7 ms, instead of through a sweep.
- **ASCII, asserted rather than assumed.** And the same measurement caught a pre-existing one: every
  `.sNp` this application has ever written opened with `Generated by circuitRF ? EM:`, because the
  em dash in that line does not survive the encoding Touchstone is written in. Fixed in the same
  place, for the same reason MIM-9 fixed it on the caveat line.

Additive, and omitted when absent, so kernel A's files gain no byte.

### What this does NOT do, recorded rather than dropped

**The plate-level cut is still open, and the brief's reason for it being open is wrong.** MIM-10
lists it as blocked on internal ports; internal ports EXIST (`PlanarPortKind.Internal`,
`InternalDeltaGap`, and RP-2a/RP-2b's second cut in a named return conductor). What actually blocks
it is MIM-12: cutting at the PLATES rather than at the part puts both plate levels back in the
matrix, which is exactly the regime whose de-embedded series element loses its sign. Nothing about
ports is in the way. So the cut that keeps the capacitor's own parasitics — the bottom plate's
0.113 pF to the backside metal, the MIM via's inductance, the Metal2 strap — waits on MIM-12 and on
nothing else.

**The kit does not offer a schematic side with `C` as a capacitor's value, and that is not a kit
change.** `KIT_MIMCAP` ships a `.csym`, and `PCellKitSchematicParts` mounts a part around it that
deliberately carries no model and deliberately drops COMPUTED parameters — a value the generator
overwrites on every draw has no business in a field on a sheet, and the generator does not run for a
schematic instance. For the kit's number to BE the capacitor, a kit would have to ship a schematic
IMPLEMENTATION for a generator (a `<generator-id>.csch` beside the `.csym`) and the cell resolver
would have to descend into an in-memory kit part instead of a folder on disk. That is a capability
and a sizeable one, not the days this brief is. Today the number reaches the circuit the way the
README shows: it is in the capacitor's own parameter list, and it is typed into one line of the
`.cnl`.

### Gates

`MimResonatorCompositionTests`. Two tests, one claim each:
`MIM10_TheCoilAloneIsPassive_AndComposesWithTheKitsCapacitanceToASeriesResonance` (Benchmark — a
de-embedded full-wave sweep, 59 s) runs the real `EmRunService.Run` on the shipped example's own
artwork with the user's mesh controls written out where they can be read, and composes through
`CnlReader` → `Elaborator` → `SParameterEngine` rather than through arithmetic, because the brief's
own gate is the composed response and not an extracted L.
`ThePortMapNamesEachLabel_ItsLevel_ItsAnchorAndItsZ0_InAscii` is the routine one.

## PCAL7/LFP review — the band walk started one cell out on a high-side port (2026-09-15)

A review of the two briefs above, reading the code against what they say it does. One defect, two
wording fixes, and one stale assertion that had been left red on purpose. Everything else in
§PCAL7-OWNNET and §LFP was re-read and stands.

### 1. `FeedBands` returned NO BAND AT ALL for a port fed from the HIGH side — the defect

`PlanarPorts.FeedBands` walks the feed's own cross-section outward-in from the port's outermost
CELL, and it took that cell's index from `IndexOf(gLong, port.OuterEdgeM)`. **`IndexOf` names the
cell that STARTS at a gridline.** For a `MinX`/`MinY` port that is the feed's outermost cell, because
`PlanarPorts.Resolve` sets `edge = gLong[outer]` there. For a `MaxX`/`MaxY` port it sets
`edge = gLong[outer + 1]`, so the lookup names the cell just OUTSIDE the metal — the walk breaks at
k = 0 on a column with no metal in it, `bands` comes back empty, and `MeasureFeedClearance` loses the
whole exemption for that port.

**It hides behind an accident of every fixture in the repository.** `IndexOf` CLAMPS: when the
coordinate is at or past the grid's last line it returns `n − 1`, which IS the right cell. On a layout
whose outermost metal is this port's own feed — `Straight`, `ViaHop`, `Bend`, `CoupledPair`,
`PlanarLineFixtures.Taper`, every one of them — the grid ends exactly at the port, so the wrong
arithmetic lands on the right answer. Draw anything at all past the port and it does not.

**Measured** on a 700 µm line with a port at each end, `PlanarOwnNetFixtures.Mesh`:

| | port 1 (MinX) | port 2 (MaxX) |
|---|---|---|
| bare line, grid ends at the metal | 6 band columns | 6 band columns |
| one 10 µm island drawn at x = 800 µm | 6 band columns | **none — no band at all** |

What that costs is R-pcal7-1's whole protection: own-net metal is a neighbour now, and the band is the
only thing that keeps a taper's own flare, a pad and a bend's corner out of that class. With it gone
a high-side port on any part that is not the last thing on the board is refused by name, quoting its
own metal — which is the failure §PCAL7-OWNNET §4 records as the reason the band exists.

**Fixed** by correcting the index rather than the walk: on the high side, take the cell whose FAR
gridline is the drawn edge, unless the lookup already clamped onto the last cell. Gated by
`OwnNetFeedNeighbourhoodTests.TheFeedsOwnBand_IsFoundOnAHighSidePort_WhenTheGridRunsPastIt`, which is
red without the correction and green with it. `FeedBands` became `internal` for that gate and shed its
unused `PlanarConductors` parameter.

**The `Category=Benchmark` numbers are unmoved, structurally rather than by re-measurement.** The
correction can only act on a `MaxX`/`MaxY` port whose grid runs past it, and there is no such port in
the tree: the MMIC coil's two are `MinY` and `MinX`, so are every port of `Splay`, `Coupled`, `Hair50`
and `UnevenPads`, and `Straight`, `ViaHop`, `Bend` and `CoupledPair` put their high-side ports at the
grid's own end, where `IndexOf` clamps. The whole routine Mom tier (1,194 tests) and the whole
`Ui.Tests` EM set (734) are green.

**The gate asserts a BAND, not a clearance verdict, and that is deliberate.** Three behavioural
fixtures were built first and all three passed with the defect in place: R-fed-1 grows the lead so the
flare always begins at the end run's far boundary, and the clearance scan judges a cell by its
MIDPOINT, so on Manhattan artwork and on a staircased taper the flare never reaches the window the
band would have to rescue it from. The band earns its keep exactly where a CUT cell's `Region` reaches
past its grid rectangle (§PCAL7-OWNNET §4's own Klopfenstein measurement) — and no fixture in this
repository is that shape. Asserting the band directly gates the decision; inventing a fixture to make
a verdict move would have gated the fixture.

### 2. Two sentences that described the wrong thing

- **`PlanarFeedExtension.FeedNote`** split its leads into "changed cross-section" and "has a
  neighbour" with `anySection = leads.Any(l => !l.GrownForNeighbour)`, which counts a lead grown ONLY
  to match a peer's — R-pcal7-3's equalisation — as a report about that port's own cross-section. The
  lead's own clause already says what it is; the summary now excludes it from both halves.
- **`PlanarSolve.PassivityExcesses`** carried two `<summary>` blocks in one doc comment (the
  measurement's, then the note's, with the note left undocumented). Merged. No warning fired because
  `src/Engine` does not generate a documentation file — which is worth knowing, since
  `TreatWarningsAsErrors` is on there and gave no cover at all.

### 3. `PlanarRunTests.TheQuasiStaticZcCaveat_IsInTheRunsNotes` — re-pointed, not left red

§LFP §8 reported this as red at HEAD and left it alone, on the reasoning that re-pointing someone
else's assertion is how a note's wording changes twice without anyone deciding to. The reasoning is
sound and the outcome was not: `dotnet test tests/Ui.Tests` fails on it, so the routine gate for the
whole UI project is red for a reason unrelated to anything anyone is working on.

It is stale rather than in dispute. PEEL §6b rewrote `PlanarKernel.QuasiStaticNote` on 2026-09-14 on
an explicit owner instruction — plain terms, no block capitals — and re-pointed `PlanarDcPointTests`'
phrase; this assertion still asked for `"QUASI-STATIC"` and `"+6.3% at 20 GHz"` against a note that
says "quasi-static estimate" and "6% at 20 GHz". Re-pointed at the part of the note a rewrite is not
free to drop — that the reported Z_c is an ESTIMATE, and the frequency the caveat is worst at.

### 4. Read and NOT changed — two asymmetries worth knowing about before touching this again

- **`NeighbourhoodRun`'s band is ONE POLYGON's span; `FeedBands`' is the mesh's CONTIGUOUS METAL.**
  `FeedSpan` returns the first polygon whose span at a station contains the port's midpoint, so two
  ABUTTING polygons on the port's own level read as a feed plus a neighbour at zero clearance, while
  after meshing they are one unbroken band and are exempt. Nothing in `src/Design` unions a layer's
  polygons, so a feed drawn as two touching rectangles is reachable. **The disagreement runs the safe
  way** — a lead grown that nothing needed, peeled exactly, costing mesh rather than accuracy — which
  is the opposite direction from the one `NeighbourhoodRun`'s own doc comment discusses. Not fixed
  because merging the spans would move lead lengths on the fixtures whose nH values §PCAL7-OWNNET
  pins, and re-measuring them is the 5.5-minute benchmark tier.
- **Within one pass, a port's neighbourhood scan sees the leads of ports measured BEFORE it**
  (`PolysOn` reads `edited`), so the first pass is order-dependent. The fixed-point step then asks the
  question again from every port's new outer edge on the fully grown problem, which is what the
  verdict is actually taken from, so the asymmetry cannot survive into a published answer — it can
  only make a first-pass lead longer than it needed to be.

## LFP — the low-frequency wall is the PORT, measured; two routes refuted and the guard that was silent (2026-09-15)

`docs/sonnet-briefs/brief-em-lf2-port-discontinuity-as-a-lumped-element.md`. Owner report: commercial
planar MoM solvers do not have the wall PCAL7 left behind — the shipped `examples/PDK PCells` spiral
reads 31.6 nH at 320 MHz against 2.8-3.5 nH, and `DeembedErrorFloor`, the diagnostic built to say so,
under-predicts the error by 40×.

**Three of the brief's five milestones are NEGATIVE RESULTS and the fourth is what shipped.** M1
measured ε and it cannot be moved; M2 refuted R-lfp-2 four independent ways, so M3 never ran; M4 —
the floor — is built and gated; M5's own entry test PASSES on the first candidate and the port is
not built, which is where the brief itself scopes it (*"naming the shape is as far as this brief
goes"*).

### 1. M0 — the console, and rebuild it before reading anything below

A `dotnet` console referencing `src/Engine` only, holding a copy of `MmicCoilFixture`'s coordinates
and a uniform-line fixture, driving `PlanarFeedExtension.Extend` → `SurfaceMesher.Mesh` →
`PlanarPorts.ResolveAll` → `PlanarSolve.Run` and reading `PlanarFrequencyPoint.Calibrations`.
**Release, and that matters: the coil at two frequencies with its four standards is 5.5 s built
Release against minutes under `dotnet test`'s Debug.** Every number in this section came out of it.

**Three traps, each of which produced a wrong table first:**

- **The perturbation saturates, and the brief warned about it for good reason.** Sweeping η from
  1e-13 to 1e-8 the slope is linear to three digits; at 1e-6 the answer has already moved by a full
  unit of S and both frequencies report the same amplification, which is wrong and looks plausible.
- **A uniform-line control on this stack is degenerate at the obvious mesh.** A 700 µm × 10 µm line
  at `MinCellsAcrossConductor` = 2 meshes to **N = 10** — the transverse pitch binds and λ/20 does
  not, exactly PEEL §8's trap on a different fixture — and its |a₂₁| comes back 16× the coil's,
  because the gap is one cell wide and the cell is the whole line. `MeshFrequencyHz` = 1e12 gives
  N = 837 and an |a₂₁| within 12 % of the coil's, which is what makes the two comparable at all.
- **`PlanarDeembed.Apply`'s inverse needs its forward map written out to be trusted.** `raw = Γe +
  T(I − SΓi)⁻¹ S T` round-trips through `Apply` to 8e-10 at 320 MHz — limited by the a₂₁² division,
  not by the algebra — and §3's bound is worth nothing without that check.

### 2. M1 — ε IS MEASURED, IT IS 5.1e-10, AND IT IS NOT WHAT THE WALL IS MADE OF

Re-solving the coil's RAW s-parameters at one frequency with one knob moved at a time, calibration
out of the path (`Deembed: false`), N = 1,697. Max |ΔS| against the shipped defaults:

| knob | 320 MHz | 2.08 GHz |
|---|---|---|
| `Fill.SelfPanels` 4 → 6 | **5.08e-10** | **3.30e-09** |
| `Fill.NearNodes` 10 → 14 | **3.74e-10** | **2.43e-09** |
| `Fill.TouchPanels` 3 → 5 | 1.12e-10 | 7.26e-10 |
| `Dcim.FitTolerance` 1e-8 → 1e-6 | 7.15e-12 | 8.85e-11 |
| `Dcim.Samples` 512 → 384 / 768 / 1024 | 1.01e-12 / 2.58e-13 / 3.78e-13 | 7.78e-11 / 6.75e-11 / 4.58e-11 |
| `Dcim.MaxOrder` 14 → 18 | 1.63e-19 | 8.11e-11 |
| `Dcim.FarSamples` 192 → 384 | 4.09e-14 | 2.06e-11 |
| `Fill.FarNodes` 3 → 6 · `FarRatio` 4 → 8 | 4.13e-15 · 2.46e-15 | 2.69e-14 · 1.59e-14 |
| `Fill.MidNodes` 5 → 8 · `NearRatio` 1.6 → 3.2 · `RemainderNodesNear` 8 → 12 | ~5e-17 | ~1.5e-14 |
| `UseSymmetricFactorization` false (LU, not LDLᵀ) | 3.52e-17 | 1.57e-16 |
| `UseRadialTable` false · `TableCellFraction` 0.02 → 0.005 | ~5e-18 | ~6e-17 |
| `Fill.Parallel` false — the accumulation order | **exactly 0** | **exactly 0** |
| `Dcim.PathExtent` 300 → 3000 | **exactly 0** | **exactly 0** |

**So ε = 5.1e-10 at 320 MHz and 3.3e-9 at 2.08 GHz, and its dominant contributor is the fill's
SINGULAR quadrature — not the Green's-function fit, which is an order below it at the bottom of the
band.** Two entries are exactly zero and both are worth keeping: the accumulation order is
deterministic, so "reorder the arithmetic" is not a route; and `PathExtent` cannot be raised by hand
because LF1's widening has already taken it to 29,850 at this frequency.

**The decisive form, because a table of knobs invites the reply "but all of them together":** every
knob above tightened AT ONCE, through the full calibrated path, moves the coil's 320 MHz inductance
from **31.564227 nH to 31.564336 nH** — 1.1e-4 nH on a row that is 28 nH wrong — and its 2.08 GHz row
from 3.480158 to 3.480150 nH.

**ε IS NOT CONSTANT AND IT IS NOT 2e-8.** The brief infers ε ≈ 2e-8 from one published answer. Read
per point as |ΔS| ÷ `PeelAmplification` against a series 0.7 Ω + 2.9 nH truth, it is **2.0e-8 /
4.8e-8 / 7.6e-8 / 1.0e-7 / 1.3e-7** at 160 / 320 / 480 / 640 / 800 MHz — rising roughly as ω, and
**40× to 250× above the 5.1e-10 the knobs can reach.** The gap is the DISCRETISATION: the meshed
structure's own departure from the drawn one, which no knob touches and which §4 of the brief
measures REFINING as a net loss on, because the port's gap is one cell wide.

**M1's verdict is the one the brief said would send it onward: ε is a discretisation floor, a decade
of it is not for sale, and neither R-lfp-2 nor R-lfp-3 is avoidable.**

### 3. M2 — R-lfp-2 IS DEAD, FOUR WAYS, AND TWO OF THE FOUR KILL THE WHOLE FAMILY

**(a) The brief's own comparison, in the right units, and there is no crossover anywhere.** §5 puts
the dropped term `|a₁₁ + a₂₁ − 1|` beside the peel's error `ε·2/|a₂₁|²` and looks for a crossing.
The two are not in the same units: the first is an error in the BOX and the second an error in the
ANSWER. An error of δ in a₁₁ reaches the answer multiplied by the same `2/|a₂₁|²`, so **the
amplifier is common to both and cancels, and the comparison is `|a₁₁ + a₂₁ − 1|` against `ε`**:

| f (GHz) | 0.16 | 0.32 | 0.64 | 1.28 | 2.08 | 4.16 | 8.00 |
|---|---|---|---|---|---|---|---|
| \|a₁₁+a₂₁−1\|, coil | 8.86e-5 | 1.82e-4 | 3.68e-4 | 7.39e-4 | 1.20e-3 | 2.40e-3 | 4.61e-3 |
| ÷ ε (2e-8) | **4,430** | 9,120 | 18,400 | 36,900 | 60,100 | 120,000 | **230,000** |
| \|a₁₁+a₂₁−1\|, 10 µm line | 9.45e-5 | 1.96e-4 | 3.96e-4 | 7.95e-4 | 1.29e-3 | 2.59e-3 | 4.96e-3 |
| ÷ ε | 4,730 | 9,790 | 19,800 | 39,700 | 64,700 | 129,000 | 248,000 |

**At the very bottom of the band, dropping the box's non-series part throws away 4,400× more than
the S-domain peel's own error, and the ratio only worsens upward.** Both cross-sections, no
crossing, nowhere. **The brief's "the departure ∝ ω and therefore VANISHES exactly where the present
method fails" is the error**: the departure is ∝ ω only because `a₂₁` is, and measured against the
transmission it is being compared with, `|a₁₁ + a₂₁ − 1| / |a₂₁|` is a **CONSTANT 0.54-0.61 on the
coil and 0.65-0.74 on the line** across a 50× band. The box is never a pure series element, least of
all at the bottom.

**(b) No inversion of the raw S can beat `1/|a₂₁|²`, and that is a bound rather than an argument.**
R-lfp-2 proposes a different INVERSION of the same raw measurement, so its conditioning is bounded
below by the reciprocal of the FORWARD gain — how far the raw S moves when the DUT's own S moves —
evaluated at the TRUE answer. The 3×3 complex Jacobian of `raw = Γe + T(I − SΓi)⁻¹ S T` over
(S₁₁, S₂₁, S₂₂) at a series 1 Ω + 3 nH:

| f (GHz) | 0.16 | 0.32 | 0.64 | 1.28 | 2.08 | 4.16 | 8.00 |
|---|---|---|---|---|---|---|---|
| 1/σ_min(J) — the best ANY method can do | 1.62e8 | **4.27e7** | 1.10e7 | 2.66e6 | 9.26e5 | 1.65e5 | 2.16e4 |
| `2/\|a₂₁\|²` — what the peel does | 7.49e7 | **2.08e7** | 5.37e6 | 1.35e6 | 5.14e5 | 1.29e5 | 3.47e4 |

**The peel is within a factor of 2 of the information-theoretic optimum at every frequency.** The
amplification is not an artefact of doing the algebra in S; it is the ratio of the port
discontinuity's impedance to the DUT's, and the S-domain quotient merely reports it. This kills the
whole family — Z, Y, ABCD, lumped or not — and not only the series-element member.

**(c) A PERFECT error box buys 2 %.** The standards' whole share of the realised error is
`ConsistencyResidual × PeelAmplification` = **1.106e-2 at 320 MHz against a realised 0.499**. Even if
the box were supplied exactly, 0.49 of the 0.50 would remain.

**(d) The half of R-lfp-2 that is about supplying the box from up the band was already refuted, by
PEEL §5(b), and the brief did not notice.** Fitting `(1−a₁₁)/ω`, `a₂₁/ω`, `(1−a₂₂)/ω` from the
well-conditioned top and evaluating below gives |S₁₁| ≈ 1.0 at every frequency on three fixtures.
The reason is the same 1/ω one: the required RELATIVE accuracy on the leading coefficient tightens as
1/f.

**M3 was not attempted, on M2's instruction to itself** — *"if it does not exist, the route is dead
and the measurement says so in an afternoon"*.

### 4. M2(a) — THE IMPLIED SERIES ELEMENT IS REAL, AND THE BRIEF'S FORMULA FOR IT IS WRONG BY 56 %

The brief asks whether the implied Z's real part is genuine or an artefact of the same cancellation,
noting Q ≈ 9 "which is not a capacitor". **Both halves are the formula, not the physics.**
`Z = 2Z₀·a₁₁/a₂₁` assumes a series element between two ports at the SAME reference impedance. The
error box's external side is the raw Z₀ = 50 Ω and **its internal side is the line's own Z_c — 99.73
− j21.50 Ω on this port**, which is what `PlanarDeembed`'s own D7 header says it is. For a series Z
between unequal references the surviving relation is `a₁₁ = 1 − 2Z₀/(Z + Z₀ + Z_c)`, hence
`Z ≈ 2Z₀/(1 − a₁₁)`:

| f (GHz) | `2Z₀·a₁₁/a₂₁` (the brief) | C | Q | `2Z₀/(1 − a₁₁)` | C | Q | the run's own raw Z₁₁ |
|---|---|---|---|---|---|---|---|
| 0.16 | 1.12e5 − j6.02e5 | 1.653 fF | 5.4 | 299 − j4.098e5 | **2.4276 fF** | 1,370 | 229 − j4.111e5 |
| 0.32 | 3.43e4 − j3.20e5 | 1.553 fF | 9.3 | 184 − j2.049e5 | **2.4276 fF** | 1,114 | 115 − j2.055e5 |
| 1.28 | 2.44e3 − j8.23e4 | 1.512 fF | 33.8 | 97.9 − j5.122e4 | **2.4276 fF** | 523 | 28.8 − j5.138e4 |
| 2.08 | 9.92e2 − j5.07e4 | 1.510 fF | 51.1 | 86.9 − j3.152e4 | **2.4276 fF** | 363 | 17.8 − j3.162e4 |
| 8.00 | 1.53e2 − j1.32e4 | 1.511 fF | 86.1 | 73.9 − j8.195e3 | **2.4276 fF** | 111 | 4.9 − j8.210e3 |

**`2Z₀/(1 − a₁₁)` gives 2.4276 fF at all seven frequencies to FIVE DIGITS over a 50× band**, and its
reactance tracks the run's own raw Z₁₁ to 0.3-0.6 % at every point; the same measurement on the 10 µm
line gives **2.3234 fF**, likewise to five digits. The brief's formula drifts 9 % and reports a Q of
5.4 rising to 86. **So the gap's series capacitance is as real and as lumped as §5 claims — more so —
and its real part is a 1,100-Q loss term, not the Q ≈ 9 that looked like a cancellation artefact.**
None of which rescues the route: §3 is about conditioning, and a perfectly known element removed
perfectly still cannot beat the forward gain.

### 5. M4 — THE FLOOR, WHICH IS WHAT SHIPPED

`PlanarErrorBox.DeembedErrorFloor` becomes
`max(ConsistencyResidual, DutRawErrorFloor) × PeelAmplification`, with
**`PlanarErrorBox.DutRawErrorFloor` = 2e-8** a documented `const` beside it, and a companion
`FloorIsDutBound` saying which term won. No constructor churn, no new cube, no new threshold — PEEL's
0.05 and 0.25 are untouched and so is everything that reads them.

**The brief's literal remedy does not work and M1 is why.** It asks for "ε measured once under
R-lfp-1", and R-lfp-1's ε is 5.1e-10 — **below `ConsistencyResidual` on every fixture in the
repository**, so `max()` with it changes nothing and the guard stays silent. The constant that fires
is the DISCRETISATION share, which M1 also measured: **2.0e-8 / 4.8e-8 / 7.6e-8 / 1.0e-7 at 160 / 320
/ 480 / 640 MHz, and the SMALLEST is taken, because a floor must not over-claim.**

On the reported spiral, before and after:

| f | residual | amplification | floor WAS | floor IS | realised \|ΔS\| | door |
|---|---|---|---|---|---|---|
| 160 MHz | 5.365e-10 | 3.747e+07 | 2.010e-2 | **0.749** | 0.758 | **dropped** |
| 320 MHz | 1.065e-09 | 1.038e+07 | 1.106e-2 | **0.208** | 0.499 | flagged |
| 480 MHz | 1.579e-09 | 4.727e+06 | 7.463e-3 | **0.0945** | 0.359 | flagged |
| 640 MHz | 2.069e-09 | 2.684e+06 | 5.552e-3 | **0.0537** | 0.278 | flagged |
| 800 MHz | 2.528e-09 | 1.725e+06 | 4.361e-3 | 0.0345 | 0.226 | clean — and σ_max < 1 here |
| 2.08 GHz | 4.186e-09 | 2.569e+05 | 1.075e-3 | 5.14e-3 | — | clean; 3.480158 nH, unmoved |
| 8.00 GHz | 1.145e-07 | 1.734e+04 | 1.985e-3 | 1.985e-3 | — | the residual binds again |

**It still under-predicts, by up to 5×, and it says so.** It is a floor: 0.99× of the realised error
at 160 MHz, 0.19× at 640 MHz, never over.

**The band edge the remedy sentence offers had to learn a second law, and that is not cosmetic.**
The residual rises as ω against an |a₂₁|² that rises as ω², so the standards' share falls as 1/f;
the DUT's share is a constant over the same ω² and falls as **1/f²**. `MeasurePeelConditioning`
branches on `FloorIsDutBound`. Read under the 1/f² law all five of the spiral's low rows name the
same edge to 7 % — **663-671 MHz**, which is the run agreeing with itself, since 800 MHz is its first
passive row. Under the 1/f law the same five would span 552 MHz to **2.40 GHz**, the sentence would
quote the largest, and a user would throw away most of a band that answers.

**What no uniform line does, and the one that nearly does.** On every fixture PEEL measured, the
standards' residual (1.8e-8 to 5.6e-4) is the larger term, so those floors are unchanged to the last
bit and `PeelConditioningTests` re-solves all of them green with no re-blessed number. **The one that
comes within 10 % is GaAs 0.1 mm coarse at 10 MHz**, whose 1.82e-8 residual is just under the
constant: its floor moves 4.22e-2 → 4.63e-2, crosses no threshold and changes no verdict. It is
carved out into its own gate rather than left to be found, because "no uniform line moved at all"
was the easy claim and it is not quite true.

**Consequence for the shipped example, which is a behaviour change and not a gate:** `circuitrf em`
on `examples/PDK PCells/SpiralInductor/em/SpiralInductor.cem` now leaves the 160 MHz row OUT of the
`.sNp` and names it, so PCAL7 §11's non-passive caveat will read 3 rows (320-640 MHz, worst σ_max
1.0384) where it read 4. That is the guard doing what PEEL built it for — the worst row is the one
removed — and `DeembedOutsideCalibrationValidity` still puts it back bit-identically. **Not
re-measured end to end through the CLI**, on the standing instruction to keep EM runs short; the
engine-level measurement above is on the same mesh and reproduces PCAL7 §10's table row for row.

### 6. M5 — R-lfp-3's ENTRY TEST PASSES ON THE FIRST CANDIDATE, AND THE PORT IS NOT BUILT

The brief's own cheapest test of any candidate, and the one it says comes before all the rest:
**solve one uniform line at two frequencies a decade apart and read how much of the structure
survives to the raw measurement.** Run on the port kind this kernel ALREADY has that is the right
shape — `PlanarPortKind.Internal`, the metal against the GROUND PLANE through a via, which is
`PlanarDcSolve`'s conduction terminal one dimension over. Same 700 µm × 10 µm line, N = 837 and 860,
no calibration in either path:

| f | edge delta gap, raw \|S₂₁\| | dB/octave | ground-referenced, raw \|S₂₁\| | dB/octave |
|---|---|---|---|---|
| 100 MHz | 8.283e-07 (−121.64 dB) | — | **0.99395** (−0.05 dB) | — |
| 200 MHz | 1.657e-06 (−115.62 dB) | **+6.02** | 0.99393 | **−0.00** |
| 400 MHz | 3.313e-06 (−109.59 dB) | **+6.02** | 0.99378 | **−0.00** |
| 1 GHz | 8.285e-06 (−101.63 dB) | **+6.02** | 0.99310 | **−0.00** |

**A delta-gap-fed 700 µm piece of metal reads as −121.6 dB of insertion loss at 100 MHz, rising at
exactly 6 dB per octave** — §10.13(a)'s measurement, reproduced on the GaAs stack — **and a
ground-referenced port reads the same metal as 0.994 through, flat across the decade.** No error
box, no a₂₁, no 1/|a₂₁|², nothing to de-embed and no wall.

**So the owner's premise is confirmed and located.** The wall is a property of the DELTA GAP, not of
the method of moments, not of the layered Green's function, not of the calibration and not of this
part — and the fill, the excitation and the solve already handle a ground-referenced port correctly
at the bottom of a band.

**It is NOT a drop-in and nothing here says it is.** A via to the plane under each end is a different
circuit from a series-fed line; nobody measuring a spiral's two terminals can substitute one. What
R-lfp-3 needs is that terminal PAIR at a conductor's end FACE with the return through the medium
rather than down drawn metal. What this measures is that the pair is the half that matters, and that
whoever builds it has a working reference for the excitation, the sign convention and the
no-calibration path already in the tree (§"The internal port", 2026-08-25).

**Not built, and that is the brief's own scope** — *"Naming the shape is as far as this brief goes;
what it must satisfy is the test above."* It remains additive-and-off-by-default work touching the
port operator, the excitation, the calibration standards, the reference impedance and every recorded
number in `HISTORY.md`.

### 7. Gates

- **Routine tier** — `tests/Engine.Tests/Mom/LowFrequencyPortWallTests.cs`, 19 tests in **21 ms**.
  The floor is a pure function of three scalars already on `PlanarErrorBox`, so every DECISION —
  which term binds, what the floor reads, which threshold it crosses, which band edge the sentence
  can offer — is arithmetic and costs nothing. The spiral's four bad rows are asserted in BOTH
  columns: that the new floor fires is half the claim and that the old one did not is the other
  half, which is what makes it a test about a defect rather than about a threshold someone moved.
- **`Category=Benchmark`** — `tests/Engine.Tests/Mom/LowFrequencyPortWallPhysicsTests.cs`, 3 tests in
  **1 m 27 s**. The spiral's rows actually leaving by the guard's doors, PCAL7's 2.08 GHz row
  unmoved at 3.480158 nH, and §6's entry test.
- Unchanged and passing: `PeelConditioningTests` (11), the whole of `Engine.Tests`' Mom set (1,174
  routine), and `Ui.Tests`' EM set.

### 8. Reported to the owner, by file and line (no `CLAUDE.md` edit, per the standing rule)

- **`tests/Ui.Tests/Em/PlanarRunTests.cs:195` — `TheQuasiStaticZcCaveat_IsInTheRunsNotes` is RED at
  HEAD and was red before this work.** It asserts the note contains `"QUASI-STATIC"` and
  `"+6.3% at 20 GHz"`; PEEL §6b rewrote that note on 2026-09-14 to *"The reported Z_c is a
  quasi-static estimate … 6% at 20 GHz"*, and this assertion was not re-pointed with the other one
  that was. Nothing in this brief touches `PlanarKernel.QuasiStaticNote` or that file. Left alone
  rather than re-blessed, because re-pointing someone else's assertion is how a note's wording
  changes twice without anyone deciding to.
- **`docs/design/mom-engine.md` §10.13's closing paragraph** says the port change "would have an a₂₁
  that does not vanish with ω" in the conditional. §6 above measures it, on a port kind the tree
  already has. That paragraph can stop hedging.

## PCAL7-OWNNET — a port feed that runs beside its OWN net, so a spiral inductor simulates as drawn (2026-09-15)

`docs/sonnet-briefs/brief-em-pcal7-own-net-feed-neighbourhood.md`. **The brief's tag is `R-pcal7-n`
and there is already a §PCAL7 in this file** (the mode-separation refusal, 2026-09-13); they are
unrelated and the collision is the brief's, not a renaming of that work. This section is the
own-net one.

Owner report: a 3-turn `KIT_SPIRAL` on the shipped `examples/PDK PCells` GaAs 2LM stack, swept
0–8 GHz at 51 points with everything left at its default, publishes an OPEN CIRCUIT — every AC point
with a negative resistance, `|S₁₁|` over 1 from 0.32 to 1.12 GHz, and a reactance following no L law
at all. The one correct row is 0 Hz, which comes from `PlanarDcSolve` and never touches any of this.

**What the run said about it was two sentences that were actively misleading:**

> `note: Port 1's feed is clear: no other conductor within the 300 µm of line the calibration standard reproduces.`

Both feeds have metal **8 µm** away running parallel to them for hundreds of microns. PCAL2's
clearance check — the check that exists to refuse exactly this — reported them clear, because the
metal 8 µm away is **the port's own net**. On a spiral it always is.

### 1. The mechanism, and why the skip was there

`MeasureFeedClearance` classified neighbour metal and then

```csharp
int label = conn.LabelOf(ci);
if (mine.Contains(label)) continue;   // the port's own net, not a neighbour
```

so own-net metal was never measured, `Breached` came back false, and `PlanarSolve`'s driven-breach
branch never called `TryFormCalibrationGroup`. The port kept D6's **scalar** error box, measured on
an **isolated uniform line** of its own width. The peel then forms
`y_ij = (S_meas,ij − δ_ij·a₁₁)/(a₂₁(i)·a₂₁(j))` with `a₂₁² ~ 1e-4`, so an error box that is the wrong
structure becomes an open circuit — the same amplification `PlanarFeedExtension.cs`'s header already
documents for a taper.

**The skip's stated rationale is sound and does not cover this case.** *"A flare or pad on the port's
own net is R-fed-1's job, it grows a collinear lead and peels it exactly"* — true of metal **in line
with** the feed. A **parallel return run** of the same net is a different object, and it is what a
spiral inductor is made of.

### 2. What was NOT the cause — measured, and cheap to repeat

| control | L at 2 GHz | verdict |
|---|---|---|
| 0.7 mm straight Metal1 line | 1.391 nH | ✅ |
| the same line broken by a Metal2 underpass and two vias | 1.419 nH | ✅ multi-level + via path innocent |
| 90° bend, 0.6 mm | 1.529 nH | ✅ corners innocent |

The meshed coil is also **not severed** — one conducting component carrying both ports — and the
wrong answer is mesh-invariant: it converges, to the wrong number.

### 3. The A/B that isolates it, reproduced exactly

Two structures with the same arms and the same 50 µm gap, differing only in whether the coupling is
inside the calibration standard's 300 µm window at the ports (`PlanarOwnNetFixtures`, `Mesh`):

| fixture | before | σ_max | after | σ_max |
|---|---|---|---|---|
| `Splay` — feeds 340 µm apart, coupling starts 400 µm in | 3.2519 nH | 0.99448 | 1.5265 nH | 0.99977 |
| `Coupled` — coupled 50 µm all the way to the ports | **135.612 nH** | **1.00366** ❌ | **1.1646 nH** | 0.99982 ✅ |

A 116× error, produced by moving the coupling into the port's window and nothing else.

### 4. R-pcal7-1 — own-net metal is a neighbour, but its own FLARE is not

Deleting the skip outright is wrong and the measurement says so: it re-fires PCAL2's refusal on
**every taper and every pad in the repository**. `EmCeilingRefusalTests`' own 13.1 mm → 299 µm
Klopfenstein came back refused with *"port 2 has other metal 203.9 µm away"* — which is its own
flare, 4.8 mm of it, monotonically widening.

**The discriminator is not the NET, it is whether the metal is CONTINUOUS with the feed across the
section.** So the exemption is the run itself: `PlanarPorts.FeedBands` walks inward column by column
on the port's own level and, at each, takes the maximal unbroken band of cells containing the port's
profile. A flare is inside it; a conductor with a gap between it and the feed is not, whoever owns
it. Own-net metal outside the band lands in the DRIVEN class by construction — the port's own
conductor carries this very port — so the 5 h threshold it now takes is the one PCAL1 measured.

**Two traps in that walk, both found by a red test rather than by reading:**

- **The band is METAL, not CURRENT.** Asking `CarriesCurrent` punches a hole in the band at an
  obliquely CUT rim, where a conformal cell can have its rooftops declined for not being
  flow-simple, and everything beyond the hole reads as a neighbour.
- **The skip is BY GRID INDEX, not by coordinate.** A cut cell's `Region` is the metal it holds, and
  a **merged sliver's region covers more than its own grid rectangle** (R-cut-1). Comparing that
  region against the band's gridlines fails for a cell that IS in the band — on the taper above, the
  two cells at the flare's rim report metal out to 643.1 µm from a grid cell ending at 611.8.

**Where the feed ENDS, the band is empty from there inward, and that is not a technicality.** The
shipped spiral's port 2 sits on a 30 µm pad that stops, with the coil body starting 10 µm later and
reached through a via on another level. A band that resumed there would call 250 µm of coil "the feed
getting wider".

**Measured effect of R-pcal7-1 alone:** the reported spiral **refuses**, with PCAL2's own sentence,
instead of publishing an open circuit. `Straight`, `ViaHop` and `Bend` come back **bit-identical to
twelve significant figures** (1.391026245606 / 1.419003477165 / 1.528748572685 nH), which is
R-pcal4-1's own invariant and it holds.

### 5. R-pcal7-2 — grow the feed until its NEIGHBOURHOOD is clear, and peel it exactly

R-fed-1 with its trigger widened from *"the cross-section is not uniform for the end run"* to
*"…or something is inside the clearance for the end run"*. `PlanarFeedExtension.NeighbourhoodRun`
asks `MeasureFeedClearance`'s question of the **artwork**, because the lead has to be grown before
there is a mesh to ask; the post-mesh check then CONFIRMS, and where it still breaches that is the
refusal, with no loop.

**The length rule is `L ≥ endRun − u*`**, where `u*` is how far inward from the drawn edge the
offending metal first appears. It is exact, not a heuristic. On the reported artwork, unchanged:

| lead grown on each port | clearance verdict | L at 2 GHz | σ_max |
|---|---|---|---|
| none (today) | "feed is clear" (wrongly) | 33.47 nH, R = −13.78 Ω | 1.0126 ❌ |
| **262.5 µm** | **clear** | **3.538 nH, R = +0.57 Ω** | 0.99764 ✅ |

262.5 µm is `endRun` (3 h = 300 µm) less the scan's own last clear station, and port 1's obstruction
is the next turn — **8 µm across, 42 µm in**, which is the report's own geometry. At the next mesh up
the coil reads 3.582 nH, so it is converged to ~1 %; modified-Wheeler for 3 turns of 10 µm on an 8 µm
pitch with a 120 µm opening is 2.48 nH.

**Three things that make the rule work, and each was a defect before it was a line of code:**

- **The class is read off the artwork CONSERVATIVELY.** A polygon a port stands on takes the DRIVEN
  threshold; everything else takes the PASSIVE one. On the mesh the same question is asked of the
  CONDUCTOR — every polygon its component reaches, through touching metal and through vias — and
  there is no connectivity on a bare polygon list to ask it of. The two can disagree in exactly one
  direction: a port-carrying net drawn as several polygons whose offending piece carries no port is
  measured against the passive threshold here and the driven one there. **That is a lead not grown,
  never a lead grown wrongly** — the run then breaches the post-mesh check and is refused by name.
  A silently published wrong answer is not reachable from the disagreement.
- **One fixed-point step, and deliberately not a loop.** Every other port's lead is metal too, and a
  port whose obstruction is another port's PARALLEL feed can never be cleared by growing: the
  obstruction grows with it, forever. The leads are computed once, applied, and the question asked
  again from each port's NEW outer edge; a port still not clear has its neighbourhood term DROPPED
  rather than lengthened, keeps whatever its cross-section asked for, and goes to R-pcal7-3's group
  or to the refusal. That is what makes non-convergence detectable instead of infinite.
- **The note says WHICH shortfall grew the lead.** "60 µm on top of 239 µm it already had" means the
  metal changes width at the plane; a lead grown because a coil turn runs 8 µm away is a different
  fact with a different remedy.

### 6. R-pcal7-3 — the own-net calibration group, and its leads

`TryFormCalibrationGroup` **already declined own-net metal by name** — *"a standard reproducing it
would be two conductors the structure shorts together somewhere this profile cannot see"* — a
sentence that was unreachable, because the breach that would call it never fired. **The objection is
not borne out**: the error box is a local property of the cross-section and the excitation, and the
DUT's topology beyond the reference plane does not enter it.

| `Coupled` | L at 2 GHz | \|S₂₁\| | σ_max |
|---|---|---|---|
| decline standing | 135.612 nH | 0.0586 | 1.00366 ❌ |
| decline lifted | **1.1646 nH** | 0.9800 | 0.99982 ✅ |

against a two-wire estimate of ≈ 1.0–1.3 nH.

**A group's members are grown to ONE lead length.** `CommonPeelLength` refuses a group whose leads
differ — rightly: a mode is a combination of the group's conductors, so "how far has this mode
travelled" has one answer for the group or none — and R-pcal7-2 produces leads differing by microns
routinely. `PlanarFeedExtension.CoplanarFloors` grows the shorter members to the longest, which costs
accuracy nothing because a longer lead is still a uniform section of the same cross-section. The
`UnevenPads` fixture is the case: two feeds at one plane on 60 µm and 40 µm pads, leads 243.75 µm and
262.5 µm before, both 262.5 µm now — **100.87 nH and σ_max 1.0045 before, 1.1849 nH and passive
after.**

### 7. M1's question, and the oracle that settled it

**`Splay` passes today with no flag on it, and this work changes its published answer by 2×** —
3.2519 nH to 1.5265 nH. Both are passive, both are plausible, and nothing in the run says which to
believe. The brief's instruction was to settle it with a control before shipping, and to **stop and
report if the control says the grouped path is the worse of the two**.

**The control is a uniform coupled section cascaded with itself.** De-embedded, a section of length ℓ
cascaded with itself must equal the same section de-embedded at 2ℓ; the identity is a property of a
uniform line and of an exact de-embedding, so the residual between `T(2ℓ)` and `T(ℓ)²` is the
instrument's own error and needs no external model. Two coupled lines, ports at all four ends, 2 GHz:

| separation | ℓ | grouped | per-port | grouped σ_max | per-port σ_max |
|---|---|---|---|---|---|
| 18 µm | 400 µm | 7.70e-2 | 2.53e+0 | 0.99997 | 1.00501 |
| 18 µm | 800 µm | **1.30e-2** | 4.42e+0 | 0.99993 | 1.00180 |
| 50 µm | 400 µm | 6.82e-2 | 1.56e+0 | 0.99996 | 1.00373 |
| 50 µm | 800 µm | **1.24e-2** | 2.15e+0 | 0.99992 | 1.00369 |
| 350 µm | 400 µm | 7.27e-2 | 2.43e-1 | 0.99996 | 1.00681 |
| 350 µm | 800 µm | **1.20e-2** | 2.14e-1 | 0.99991 | 1.00136 |

**The grouped path is better at every separation and every length, by 1 to 2.5 orders of magnitude,
and it is passive at every point where the per-port path is not.** At 350 µm — `Splay`'s own
separation — it is 18× better. So `Splay`'s new 1.5265 nH is the better answer and the 3.2519 nH it
used to publish is the worse one. The control does not invert M3.

### 8. What this does NOT fix, and must keep refusing

`Hair50` — a 300 µm hairpin whose U-turn sits **inside** the 300 µm end run — refuses, and should.
No lead and no group can reproduce a feed that bends within the length the standard replaces; PCAL4's
own uniformity rule declines the group by name, and the decline travels with the refusal. It is a
strict improvement on the 159.297 nH it used to publish in silence.

`TryWidenForNeighbours`' own-net decline stays a decline and stays **unreachable**: `mine ⊆ driven`
by construction, so a passive breach is never own-net and the driven check above it fires first.
PCAL3's widened neighbour is *floating at zero net charge*, which an own-net conductor is not, and
inventing a third boundary condition is not this brief.

### 9. M4 — what a grown lead costs, and the ceiling

A lead adds gridlines to a tensor grid shared by the whole layout, and **it can go either way**:

| file | N without leads | with | Δ |
|---|---|---|---|
| `examples/Klopfenstein Taper` | 774 | 653 | **−15.6 %** |
| `examples/Patch Antenna` | 2,299 | 2,299 | 0 (no lead) |
| `examples/PDK PCells` spiral | 1,388 | 1,706 | **+22.9 %** |
| the same spiral at the default mesh | 23,357 | 25,120 | +7.5 % |

**Where a lead pushes the mesh past the ceiling, the refusal says which part of it is not the user's
artwork.** Every other remedy in that message is a setting or a piece of the drawn layout; a lead is
neither, and a user measuring their part against the refusal's numbers would be measuring a structure
that is not in their file. **Shortening it is not offered as a remedy** — it is the length that makes
the de-embedding valid, and trading that for mesh size silently is the trade this whole area exists
to stop. `EndRunHeights` was not touched for the same reason.

### 10. M5 — acceptance, on the file as a user would write it

`examples/PDK PCells` now carries `SpiralInductor/em/SpiralInductor.cem` and the two port labels the
top layout was missing, so the one PCell workspace circuitRF ships actually demonstrates EM on a
PCell. `circuitrf em` on it, 0–8 GHz, 51 points, nothing else edited, reading `Zs = −1/Y₂₁`:

| f (GHz) | reported | now | σ_max now |
|---|---|---|---|
| 0 | +1.765 + j0 Ω | **+1.774 + j0 Ω** | 1.00000 |
| 0.32 | −584.4 + j2115.6 (1052 nH) | −2.67 + j63.47 (31.6 nH) | 1.0384 ❌ |
| 0.48 | −282.4 + j1470.3 (487 nH) | −0.95 + j47.62 (15.8 nH) | 1.0152 ❌ |
| 2.08 | −16.5 + j386.5 (29.6 nH) | **+0.60 + j45.51 (3.48 nH)** | 0.99763 ✅ |
| 4.16 | — | +0.78 + j77.08 (2.95 nH) | 0.99724 ✅ |
| 8.00 | −1.2 + j221.3 (4.5 nH) | **+1.07 + j141.03 (2.81 nH)** | 0.99601 ✅ |

**2–8 GHz is right**: positive R, passive at every point, 2.81–3.48 nH of coil, landing on Wheeler at
the top of the band. That is this brief's gate and it is met.

**Below ~0.8 GHz it is still wrong**, and that is brief PEEL's `1/|a₂₁|²`, not this one's — improved
33× from the published 1052 nH at 0.32 GHz but not fixed. **It is also what stops the adaptive sweep
converging**: the run reports *"DID NOT CONVERGE … worst |ΔS| = 0.0203 against a tolerance of 0.001,
29 of 50 solved"*, and every one of those disagreements is in the bottom decade. The shipped `.cem`
keeps the reported 0–8 GHz sweep anyway, because narrowing it would hide the one thing this file is
now the acceptance fixture FOR — and the Touchstone says on its own face which rows are not an
answer. **Whether the shipped example should instead start above 1 GHz is the owner's call**, and it
is the only thing in this section that was decided on taste rather than on a measurement.

**A `.cem` that names only Metal1 publishes a clean, smooth, perfectly passive OPEN CIRCUIT.** The
coil's inner terminal escapes on Metal2 through two via posts, so a run whose analysis levels are
Metal1 alone drops the underpass and the two ports are not connected at all — `|S₂₁| = 4e-4`, with
one note among thirty saying two via shapes were ignored. Measured on this very file while writing
it. The shipped setup lists both levels, and `PdkPCellExampleTests` gates that it does.

### 11. R-pcal7-4 — a row that is not a network is named on the FILE's own face

PCAL2's finding one case further on. The run has always said `NOT PASSIVE` in its notes; a `.sNp`
carries no notes, and on this file the four rows below ~0.8 GHz are the decade a reader looks at
first. `EmSnpProvenance.ValidityCaveats` now emits a second caveat beside the validity one:

```
! circuitRF-EM caveat: 4 of these rows are NOT A PASSIVE NETWORK and should not be used:
  160 MHz to 640 MHz, worst sigma_max(S) = 1.0919. …Raise the sweep's lower edge, or read
  those rows as unanswered.
```

σ_max > 1 needs no threshold argued for: a passive structure cannot do it, so the excess is the
ANALYSIS. The note gained the same thing — the **frequencies**, not just the count, because raising
the sweep's lower edge is not an action anyone can take from "4 of 50".

**ASCII only, and that is not a style rule.** Touchstone is written in an encoding this writer
transliterates to: the first version of that line read *"worst ?_max(S) = 1.0919"* and *"divides by
a???"* on the file. A caveat the file cannot spell is one nobody can act on.

**Dropping those points instead was considered and NOT done.** PEEL's per-point guard already exists
and is the right shape, but it does not see this failure: `DeembedErrorFloor` reads **2.2e-2** at
160 MHz, under PEEL's own 0.05 budget, while the realised passivity excess is 9.2e-2 and the
inductance is 30× out. PEEL's law was calibrated on a uniform line where the de-embedded `S₁₁` must
be 0; on a high-Q coil read as `−1/Y₂₁`, a `|ΔS|` of 0.02 lands on a matrix whose own `S₁₁` is
0.9999 and is amplified again. **`DeembedErrorFloor` is a bound on `|ΔS|` and not on the quantity a
user reads.** Turning σ_max into a per-point DROP would change every run in this engine and is a
decision for the owner, not for this brief; what shipped is the sentence, in the notes and on the
file. **The brief's other candidate — "PEEL's `2βΔℓ` conditioning gate" — does not exist and cannot
be built as described**: PEEL §1 measured Δℓ INERT over a 30× range and re-attributed the amplifier
to `1/|a₂₁|²`.

### 12. Where the gates are, and why they are split

- **Routine tier** — `tests/Engine.Tests/Mom/OwnNetFeedNeighbourhoodTests.cs`, 16 tests in **1 s**.
  The DECISIONS: which metal is a neighbour, how long a lead is, which run is refused, what the note
  says, that a flare is still exempt, that the ceiling refusal names a grown lead. These are what the
  code changes actually do and what would catch a regression first.
- **`Category=Benchmark`** — `tests/Engine.Tests/Mom/OwnNetFeedPhysicsTests.cs`, 9 tests in
  **5 m 29 s**. The nH numbers, the bit-identity of the three clear fixtures, and the coupled-line
  control. Each solves a DUT and its standards; per-fixture cost measured at 3.1 / 102 / 6.3 / 14.5 /
  9.0 / 0.1 / 81.7 s for Straight / ViaHop / Bend / Splay / Coupled / Hair50 / UnevenPads.
- `tests/Ui.Tests/Em/NonPassiveCaveatTests.cs` (25 ms) and two new cases in
  `tests/Ui.Tests/Examples/PdkPCellExampleTests.cs`.

**The fixture mesh is `CurrentModel.TransmissionLine`, and that is load-bearing rather than tidy.** A
calibration GROUP stays on the MEASURED separation ladder by design (`SeparationPlan` declines the
quasi-static shortcut for one), so its standard is 60° of line — 9.78 mm at 2 GHz on this stack.
Under the per-axis rule a 10 µm conductor forces a 10 µm cell in BOTH directions, which makes that
standard **18,224 unknowns** and refuses every grouped fixture at the dense ceiling. Telling the
mesher the metal is a LINE puts λ along the current and the width across it, and the same standard is
**818**.

### 13. Traps and negative results, for whoever is next

- **A scratch console that runs `EmSetupPersistence.LoadFromFile` → … → `PlanarConductors.Of` and
  prints connectivity and unknown counts answers "is the mesh severed?" and "how many unknowns?" in
  0.6 s.** Every diagnosis above was reached with it before a single full-wave solve. Build it first.
- **A baseline is a `git worktree` at HEAD, never a `git stash`.** Every before/after number in this
  section is the same probe binary built against both trees, so "bit-identical to twelve significant
  figures" is a diff of two files rather than a claim.
- **`DeembedResidual` is anti-correlated with the truth here too**, as PEEL already recorded: on the
  reported file it reads 5.4e-10 at 160 MHz, the point that is 39× wrong, and rises through the band
  where the answer is right.
- **The brief's own `.cem` numbers (4,814 → 4,189 unknowns, 265 µm leads) were not reproduced
  exactly** — this work measures 1,388 → 1,706 and 262.5 µm on a mesh of its own choosing, because
  the reported `.cem` did not exist in the repository and had to be authored. The leads agree to one
  scan step, and the direction of the ΔN differs, which is what the brief itself warned was "a happy
  accident of this geometry and not a general claim".

## Why the edge mesh is unaffordable on a MMIC spiral, measured (2026-09-15)

Owner, on the PDK PCells example: the default mesh on a `KIT_SPIRAL` has far too many cells, edge
mesh has to be switched off to get it down, and the extra cells appear "in seemingly random
locations". Measured in a scratch harness on the shipped coil — 3 turns, 10 µm metal, 8 µm space,
120 µm opening, 270 × 250 µm envelope, Metal1 + Metal2 over 100 µm GaAs, 20 GHz. **Investigation
only; nothing in the mesher was changed.** The action half is
`docs/sonnet-briefs/brief-edge-mesh-cost.md`.

### The edge fan's cost GROWS as the user coarsens the mesh

| `MinCellsAcrossConductor` | edge ON | edge OFF | ratio | bulk ÷ c₀ |
|---|---|---|---|---|
| 4 (default) | **23,195** | 6,456 | 3.59× | 8.5× |
| 3 | 17,001 | 3,456 | 4.92× | 11.3× |
| 2 | 12,160 | 1,380 | **8.81×** | 17× |
| 1 | 10,392 | 228 | **45.6×** | 34.6× |

`c₀ = 3 % of the local conductor WIDTH`; the bulk pitch is `width ÷ MinCellsAcrossConductor`. So the
climb is `1 / (0.03 · MinCellsAcross)` — 8.3 / 11.1 / 16.7 / 33.3 at 4 / 3 / 2 / 1, measured as
8.5 / 11.3 / 17 / 34.6. **The edge cell ignores the control that sets the mesh density, so asking for
a coarser mesh lengthens the fan.**

Consequence: **`MinCellsAcrossConductor` barely works while the edge mesh is on.** 4 → 1 falls
23,195 → 10,392 (2.2×) with the fan on and 6,456 → 228 (**28×**) with it off. The mesher's own note
states the outcome and always has: `Narrowest conductor dimension 10 µm, meshed 9 cell(s) across
(target 4)` — nine where four were asked; seven where two were asked at `MinCellsAcross = 2`.

The dense ceiling is 5,000, and a two-level problem (`RequiresGeneralKernel`) cannot use the
accelerator's 12,000 — so the shipped default is **4.6× past a ceiling that does not move.**

### The λ knobs are structurally inert here, and the mesher says so

λ_g/20 is 209 µm at 20 GHz on this stackup and the **whole part is 250 µm across**. Every cell is set
by `narrowest / MinCellsAcrossConductor` = 10/4 = 2.5 µm. `CellsPerWavelength` and `MeshFrequencyHz`
move nothing at any value — 10, 20 and 40 GHz all give the same count, and the refusal says so in
capitals. `TransmissionLine` gives 1.4× and `Sheet` 1.2×; both act on the ALONG pitch only, so they
compose with anything done to the across fan.

### `EdgeCells` cannot change the finest cell — by design, and it is gated

23,195 / 18,861 / 18,861 / 6,456 at `EdgeCells` 3 / 2 / 1 / 0. **Only 0 does anything, and 0 is
`EdgeMesh = false` spelt differently (6,456 either way, bit for bit).** `GrowthRatioFor` clamps
`r = (h/c₀)^(1/n)` to `MaxGrowthRatio = 3`, so a climb of 8.5 takes two graded cells at `EdgeCells`
1 and 2 alike. **`EdgeCells` is a FLOOR on the fan's length, not a cap on its fineness** —
`SurfaceMesherEdgeCellsTests.RaisingEdgeCells_DoesNotSharpenTheFinestCell_ItWidensTheGradedBand`
gates precisely that, and `AnUnhonourableEdgeCellCount_IsReportedInTheMeshNotes` reports it. It is
undocumented in the tooltip, which is the actual gap.

**An arithmetic correction, recorded because it was stated once and is wrong:** the fan is *not*
longer than the trace. With the derived `r = 2.04` (not the nominal `EdgeGrowthRatio = 1.7`) the
three graded cells are 0.29 / 0.60 / 1.22 µm — 2.1 µm a side, so a 10 µm trace comes out
`0.29 0.60 1.21 2.44 | 2.45 | 1.56 0.78 0.38 0.29` — four fan cells each side and one bulk cell in
the middle. **Nine across, not "all fan".** The cost is real; the reason is the count, not
saturation.

### The "random locations" are the tensor product, and they are not random

One grid is shared by every layer (D8), so an x-attractor refines a column over the part's full
height:

```
edge ON : 188 x-lines × 160 y-lines = 29,733 rectangles, 12,294 on metal, N = 23,195
edge OFF: 113 × 105                 = 11,648 rectangles,  3,696 on metal, N =  6,456
x lines: -155 -154.7 -154.1 -152.88 -150.41 -147.93 -145.46 -142.99 -140.51 -138.04 -136.46 -135.68 -135.3 -135 …
```

A spiral has metal edges at ~14 distinct x and ~14 distinct y. The fan belonging to a VERTICAL turn's
edge lands in the middle of every HORIZONTAL run, refining it along the axis where nothing varies.
`PlanarEdgeReference`'s own doc already says this cannot be localised without T-junctions the rooftop
basis does not admit. **Not a defect to fix; a property to work within.**

### The detail floor is correctly inert on a part this small, and correctly explains itself

`detailFloor = min(λ_g/divisor, CapMinFractionOfExtent × min extent)`. The cap is 2 % of the smallest
extent = **5 µm on a 250 µm coil**, under the 10 µm trace, so nothing is floored and
`ShapesBelowDetailFloor` stays 0 at every divisor; λ_g/200 and λ_g/20 give identical meshes. **This is
right and is not a second defect** — there is no import detail on a drawn spiral to floor, the cap's
own comment gives the reason (a floor coarser than one cell "is declining to mesh the part"), and the
run says so unprompted:

> Detail floor 5 µm — λ_g/200 would be 20.87 µm, but this artwork is only 250 µm across, so the floor
> is held at 2% of that: nothing on this artwork is narrower than that, so it changed nothing here.

Worth knowing as a general fact: **`DetailFloorDivisor` cannot act on any part whose metal is wider
than 2 % of its own envelope**, which is most MMIC passives. Out of scope for the brief.

### There is no mesh-reduction pass, and that is the accurate answer

Everything that removes cells is pre-grid: the detail floor (inert here) and
`PlanarEdgeReference.LocalConductorWidth`'s per-attractor `c₀` (also inert here — every edge has the
same 10 µm width, so the local reference equals the global one). Sliver merging is conformal-only and
merges cut cells, not gridlines. **Nothing walks a finished grid and removes a line**, and adding
such a pass is not the cheap route — flooring `c₀` against the bulk pitch is, because it shortens
every fan at the source and can be made one-way-coarsening. That is the brief.

> **SUPERSEDED, 2026-09-15 — the floor was built, and the paragraph above named the wrong bulk.**
> Flooring `c₀` against *the axis's own bulk pitch* is what this section proposed and it is not what
> shipped: on any part whose ALONG pitch is set by λ rather than by the metal it makes `c₀` depend on
> Cells per wavelength — the coupling ANT-2's own "lever 2" was built with and removed for — and it
> coarsens the fan at a port's END FACE, which is measurably the wrong fan to touch. What shipped
> floors the edge FRACTION against `MinCellsAcrossConductor` instead. See **§EFAN** below for the
> measurement, the shipped rule, and the two things this paragraph got wrong.

## §EFAN — the edge fan now obeys the control that sets mesh density (2026-09-15)

> **Asked afterwards (2026-09-15): did this change the antenna mesh?** No — measured, not argued.
> The floor binds only when `1/(MinCellsAcross · 10) > 3%`, i.e. at **3 cells across and below**, and
> the shipped patch antenna runs at the default 4. Meshed through `PlanarKernel.Mesh` on
> `testdata/antenna/`, at HEAD:
>
> | Cells across | 8 | 6 | **4 (shipped)** | 3 | 2 | 1 |
> |---|---|---|---|---|---|---|
> | N | 2,619 | 1,944 | **1,611** | 1,250 | 1,029 | 680 |
> | finest edge cell | 50.4 µm | 50.4 µm | **50.4 µm** | 56 µm | 84 µm | 168 µm |
> | realised fraction | 3% | 3% | **3%** | 3.33% | 5% | 10% |
>
> **N = 1,611 is the number `reference/antennas.html` quotes**, so nothing an antenna run at 4 or above
> does has moved to the bit, and `AntennaExampleTests.TheExampleMeshesToTheUnknownCountThePageQuotes`
> is the standing gate on it. Below 4 the edge cell is coarsened — which is the point, it is what the
> user asked for by lowering the control, and the run now names the control that did it. The commit
> before this one (`0e4127bb`) touched only a brief and this file: **no code, so no mesh.**

The action half of the section above, and its first correction: the climb the graded fan makes is
`1/(EdgeFractionOfReference · MinCellsAcrossConductor)` on any geometry-limited mesh, so the user's
own density control made the fan LONGER the coarser they asked for.
`PlanarMeshSettings.MaxEdgeRefinement` caps it. **`PlanarMeshSettings` gains no field and the `.cem`
gains no key** — it is a resolution, and `MinCellsAcrossConductor` is already the control.

### 1. M1 — what the fan buys, against an oracle that is not this kernel

The instrument is `MeshFrequencyAccuracyTests`' shape with the swept quantity changed: force the edge
fraction so the climb `h/c₀` takes a stated value, solve the line de-embedded, and compare **Z_c,
ε_eff and the whole de-embedded S** against kernel A's own cross-section extraction of the SAME line
— `RlgcExtractor` over `BoundaryMesher`, at t = 1 µm for `PlanarGammaTests.KernelAEeff`'s own measured
reason. The S oracle is an ideal `R, L, G, C` line written out in the harness rather than taken from
`RlgcToSparams`, so it shares no code with what it judges.

**Both bands sit below their stack's quasi-static crossover on purpose** — FR-4 1–3 GHz against a
3.048 GHz crossover, GaAs 5–20 GHz against 26.07 GHz — because above it kernel A is not authoritative
and the comparison measures dispersion instead. Taking the FR-4 band at 2–10 GHz first is what made
its baseline read 3.0% against the 0.14% below; that is real dispersion, not mesh error.

| h/c₀ | 96 | 68 | 48 | 34 | 24 | 17 | 12 | 8.5 | 6 | 4 | 2 | 1 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **GaAs** N | 402 | 346 | 325 | 294 | 246 | 246 | 229 | 212 | 187 | 149 | 94 | 66 |
| Z_c vs A | +0.52% | +0.47% | +0.53% | +0.56% | +0.53% | +0.59% | +0.68% | +0.71% | +0.83% | +1.06% | +1.64% | +2.87% |
| ε_eff vs A | +1.06% | +0.95% | +1.02% | +1.04% | +0.92% | +0.96% | +1.01% | +0.91% | +0.90% | +0.93% | +0.75% | +0.64% |
| \|ΔS\| vs A | .0195 | .0188 | .0151 | **.0133** | **.0133** | **.0132** | .0174 | .0352 | .0587 | .100 | .223 | .456 |
| **FR-4** N | 356 | 325 | 304 | 275 | 229 | 212 | 212 | 195 | 172 | 136 | 85 | 59 |
| Z_c vs A | −0.24% | −0.17% | −0.13% | −0.19% | −0.09% | −0.01% | −0.04% | +0.13% | +0.31% | +0.60% | +1.35% | +2.90% |
| ε_eff vs A | −0.41% | −0.35% | −0.32% | −0.44% | −0.38% | −0.35% | −0.49% | −0.43% | −0.42% | −0.41% | −0.60% | −0.70% |
| \|ΔS\| vs A | .0161 | **.0139** | **.0138** | .0152 | .0153 | .0245 | .0351 | .0558 | .081 | .124 | .248 | .489 |

GaAs hero, 72 µm × 2 mm on 100 µm GaAs, 4 across; FR-4 hero, 2.9 × 20 mm on 1.6 mm FR-4, 4 across.
Sweeps at 2 and 1 across have the same shape and are not reproduced.

**The three questions the brief asked, answered:**

1. **Where does the curve flatten?** Z_c and ε_eff are flat from about **h/c₀ = 8.5** upward — the
   whole 8.5…96 range spans 0.24 percentage points on GaAs and 0.37 on FR-4 — and degrade steeply
   below 4. The de-embedded S is flat from about **12**.
2. **Is it the same on both stacks?** Yes. Both flatten in the same window and both knee between 8
   and 12, despite an 8× difference in substrate height and a 3× difference in εᵣ.
3. **Does 8.5 already sit on the flat part?** For the cross-section quantities the edge singularity
   actually sets — yes, just. For the de-embedded S — no: 8.5 reads 2.7× its own floor.

**Refining past ~34 makes the de-embedded answer WORSE, and that is not noise.** On GaAs the finest
rung is 0.0195 against 0.0132 at 17. The mechanism is already in this directory's own record: the
port's reference plane is one cell in from the drawn metal and the half-cell beyond is error box, so
the OUTERMOST cell sets `a₂₁`, and the diagonal of D6's error box divides by `a₂₁²`. Shrinking that
cell raises the amplification. **So the fan does two different jobs and they want different
refinements** — at a long rim it resolves the 1/√d transverse singularity (flat past ~8), at a port's
end face it sets the de-embedding's own conditioning (flat past ~17) — and nothing in the mesher
distinguishes them, because a port is not a mesh concept.

### 2. M2 — what shipped, and why it is NOT the brief's own formula

The brief asked for `c0_i = max(0.03·w_i, bulkAt(i)/MaxEdgeRefinement)` with `bulkAt(i)` the
attractor's own axis bulk. **That was measured and not taken.** What ships caps the FRACTION instead:

```
realised fraction = max( EdgeFractionOfReference, 1 / (MinCellsAcrossConductor · MaxEdgeRefinement) )
c0_i              = realised fraction · w_i          (one scale on c0, every reference alike)
```

The two coincide wherever the axis's bulk IS `w/MinCellsAcross`, which is the geometry-limited case
the brief's own derivation is written for. They differ where the bulk is set by λ, and there the
per-axis form goes wrong twice:

- **It makes `c₀` depend on Cells per wavelength.** That is exactly the coupling ANT-2's "lever 2"
  was built with and removed for — it broke the transmission-line mesh's own orthogonality gate, a
  50 mm line's y gridline count moving 9 → 8 when cells/λ went 20 → 5. Re-introducing it here would
  have re-broken the same gate for the same reason, one phase later.
- **It coarsens the end-cap fan, which §1 measured as the wrong one to touch.** On the FR-4 hero the
  along climb is 27.3 and the across climb 8.3; a per-axis floor at 10 leaves the rim alone and takes
  the port cell from 87 µm to 238 µm, for \|ΔS\| 0.0131 → ~0.05.

The shipped form has no λ in it at all, so neither happens: it is a statement about the metal and the
density asked of it, and the λ-bound end-cap fan is untouched at every setting.

**The rate is left at the one derived from the UNFLOORED c₀**, which is what makes the field
pointwise ≥ its old self. Re-deriving it against the raised c₀ gentles the ramp, so the fan reaches
the bulk further out and the field is FINER than today's over the band between the two fans' ends —
the invariant would be lost in the one place nobody looks.

**10 is the constant.** It sits on §1's flat part and is the largest round number that leaves the
shipped default bit-identical: the floor binds only when `1/(MinCellsAcross·10) > 3%`, i.e. at 3
cells across and below, so every number in `HISTORY.md` — all taken at 4 — is untouched to the bit.

### 3. The invariant is exact under the per-axis rule and has a measured residue under a pitch field

| | worst count rise, floored vs unfloored |
|---|---|
| `CurrentModel.None` (the shipped default) | **1.0000** — exact, over 5 fixtures × cells/λ {5,20,40} × 1…8 across |
| `Sheet` | 1.0329 cells / 1.0342 unknowns / 1.0122 gridlines (FR-4 taper, cells/λ 5, 3 across) |
| `TransmissionLine` | 1.0102 cells / 1.0118 unknowns / 1.0000 gridlines (the coil, cells/λ 20, 3 across) |

**The residue is the enforcement pass, not the floor.** `BuildGridLines` finishes by splitting any
cell longer than the cap, and with a field it reads that cap at the cell's own MIDPOINT — the only
place a single number can honestly describe a cell. Midpoint sampling of a varying field is not
monotone in the partition: a coarser cell whose midpoint lands in a FINER part of the field is split
into more pieces than the two finer cells it replaced were, because those two had their midpoints in
coarser parts and were let through. **The coarser partition is the one being CORRECTED more**, not
the one being meshed worse.

**The detail floor — the other one-way relaxation of this same field — is exactly monotone under a
field** (measured, 1.0000 on all three modes), because it coarsens the measured WIDTHS and therefore
the CAP as well as the fan. So this residue is specific to a change that moves the partition while
leaving the cap where it was, and it is not a general property of the field path.

`EdgeRefinementFloorTests` gates both halves separately: the per-axis rule exactly, in the routine
gate at 0.5 s, and the field modes at a stated 5% margin, `Category=Benchmark` at 27 s because a
pitch field is built per mesh and there are 720 of them.

### 4. M4 — the reported coil, end to end

The shipped `KIT_SPIRAL` at its defaults on the GaAs starter technology, 20 GHz, two levels, now a
permanent fixture (`MmicCoilFixture`). Unknowns, edge mesh ON, before → after:

| Cells across | per-axis rule | `Sheet` | `TransmissionLine` | edge mesh OFF (per-axis) |
|---|---|---|---|---|
| 4 (default) | 23,357 → **23,357** | 19,438 → 19,438 | 16,988 → 16,988 | 6,488 |
| 3 | 17,129 → 17,129 | 15,081 → 14,835 | 13,161 → 13,316 | 3,474 |
| 2 | 12,258 → **9,316** | 10,945 → 8,205 | 10,451 → 7,787 | 1,388 |
| 1 | 10,490 → **5,246** | 10,022 → 4,814 | 9,632 → 4,706 | 230 |

**The default is still 4.7× past the 5,000-unknown ceiling, and that is the honest result.** The
floor is inert there by construction. What changed is that the controls now do what they say: taking
Cells across 4 → 1 buys **4.5×** with the edge mesh on where it bought 2.2×, against 28× with it off
— the gap between the two is 6.3× rather than 12.7×.

**The residual gap is a COUNT and this constant governs a SIZE, so no value of it closes the rest.**
What is left is the fan's length in cells — two or three gridlines per attractor, times 28 rims,
each one a gridline across the whole tensor grid — and that does not scale with the bulk pitch at
all. `EdgeCells` is the control over the count and it is a FLOOR on the fan's length, which is true
today, was undocumented today, and is now in its tooltip.

The modes compose as expected, because they act on different things: the current model sets the ALONG
pitch and this floor the ACROSS fan. `TransmissionLine` + 1 across + edge mesh on is 4,706, under the
ceiling and with the edge mesh still doing its job; the same with the edge mesh off is 158.

**The two-via count is the difference from the investigation's own table.** That harness had no vias;
the two Metal1–Metal2 posts contribute exactly 162 vertical unknowns at the default mesh and nothing
else, so 23,357 here is 23,195 there. `MmicCoilFixture.Coil(withVias: false)` reproduces the older
numbers exactly, including the 188 × 160 and 113 × 105 gridline counts.

### 5. The traps

- **A self-comparison can only show convergence, not correctness, and here the two disagree.**
  Against the finest mesh the de-embedded S moves monotonically; against kernel A it has a MINIMUM at
  h/c₀ ≈ 17–34 and the finest mesh is on the wrong side of it. A brief that gated on \|ΔS\| against
  the finest rung alone would have concluded "always refine more".
- **`MaxCellEdge / MinCellEdge` is not the climb, on anything anisotropic.** It reads 96 on the GaAs
  hero, which is the ALONG bulk over the ACROSS edge cell — two different axes. The climb is per
  axis, and the two differ by 12× on that fixture.
- **The FR-4 hero is λ-bound across at 10 GHz and above**: λ_g/20 is 715 µm against 2.9 mm/4 = 725 µm,
  so `MinCellsAcrossConductor` changes nothing there at 4, 2 or 1 — three identical meshes, which
  reads as a broken control rather than a fixture that cannot exercise it. Take that band down to
  1–3 GHz and it binds.
- **The cell and unknown counts do not follow the gridline count.** A cell exists where a grid
  rectangle's CENTRE lands on metal, so removing one gridline moves every centre: on the coil the
  transmission-line mesh at 3 across loses a gridline and GAINS 71 cells. Assert a mesh invariant on
  the quantity it is actually a statement about.
- **Nothing about the detail floor was in scope and nothing about it changed.** `CapMinFractionOfExtent`
  holds it at 2% of the smallest extent — 5 µm on a 250 µm coil, under the 10 µm trace — so it is
  correctly inert on this class of part and correctly says so. It is not a second defect, and the
  general fact is worth knowing: **`DetailFloorDivisor` cannot act on any part whose metal is wider
  than 2% of its own envelope**, which is most MMIC passives.

## DCFLOAT — the DC point could not drive a port that is not referenced to the plane (2026-09-14)

Owner report. A 50 Ω trace on the 0.6 mm laminate starter stack, simulated twice — once as a 2-port
with edge ports, once as a 3-port with an internal delta gap in the middle — and compared by shorting
the gap port out in a schematic. The two agreed to six digits from 204 MHz up and disagreed
completely below, which is backwards from every intuition about a short piece of copper. Shorting the
gap gave |S₂₁| = 0 where the 2-port gave 1.

**The three rows that disagreed are not full-wave results.** They are below `Dcim.LowestFittableFrequency`
(7.952 MHz on this stack) where LF2 substitutes `PlanarDcSolve`, and they were bit-identical to each
other, which is what a substitution looks like. The defect is in the conduction solve.

### 1. WHAT IT PUBLISHED, AND WHY EVERY NUMBER OF IT FOLLOWS FROM ONE LINE

```
S₁₃ = 0.99997   S₂₂ = -0.99974   everything else 0
```

Port 2 shorted to the plane and isolated; ports 1 and 3 a perfect through. Reproduced in **30 ms** on
a plain 20 mm line with a gap at its midpoint, so none of the reported geometry is load-bearing.

`Network.Admittance` built Y by **Dirichlet excitation**: hold every port terminal at a fixed
potential, drive one to 1 V, solve the free nodes, read back the current leaving that terminal. The
short-circuit definition Yᵢⱼ = Iᵢ/Vⱼ with Vₖ = 0 requires each other port's **two terminals to be at
the same potential as each other**. Pinning them all to absolute zero is that condition **only when
every port's − terminal is one shared node.**

It is, for every kind but one. `Build` writes `_minus[p] = _groundNode` for an edge port and for a
via-to-plane `Internal` port. An `InternalDeltaGap`'s − terminal is **the far lip of the cut** —
signal metal. Pinning it to zero does not set V₃ = 0; it welds the trace to ground on the far side of
the gap, and the two lips stop being one port: current entering the near lip is under no obligation
to leave the far one. There was no constraint tying them.

With R_left = 1.132 mΩ and R_right = 1.510 mΩ on the reproducer, every published number follows:

| | published | correct |
|---|---|---|
| Y₁₁ | 1/R_left = 883 S | 1/(R_left+R_right) = 378.5 S |
| Y₂₁ | 0 | −378.5 S |
| rank of Y | 2 | **1** |

**The rank is the tell.** The true network has one independent current (I₁ = I₃, I₂ = −I₃), so
Y = g·uuᵀ with u = (1,−1,±1) and S is the 1/3–2/3 split of three matched sources meeting at a series
cut — which is exactly what the full-wave solve publishes at 204 MHz, and what makes the two halves
of a sweep meet. The extra rank was the current path that should not exist.

A second, independent contribution: reachability was asked on the **branch** graph
(`CompOf(_plus[i]) == driven`). A cut's two lips are in different conduction components *by
construction* — that is what a cut is — so every port on the far side was declared unreachable and
hard-zeroed. That is where S₁₂ = S₂₃ = 0 came from, rather than from the solve.

### 2. THE FIX: A SOURCE ACROSS EACH TERMINAL PAIR, AS ONE MNA SYSTEM

Each port contributes an ideal voltage source across its own pair: one extra unknown (the source
current) and one constraint row `v(+) − v(−) = Vₖ`. That enforces I₊ = −I₋ by construction and lets
the pair's common-mode potential float, saying nothing about where either terminal sits. A port whose
− terminal is the ground node reduces to the old Dirichlet condition exactly, so those kinds are
untouched — verified by running the same probe against the pre-change file.

Four things the formulation needs, each of which is a real case and not a hypothetical:

- **A port is a CONNECTION when asking what can reach what.** Islands are unioned over branches *and*
  active port pairs. Without it the far side of every cut stays hard-zeroed.
- **A datum per island.** The ground node is its own island's. An island that never reaches it — a
  trace over a plane it does not touch, driven only through gap ports — is floating, and the constant
  vector is a null vector of the whole system. One node of it is pinned; it appears in no current.
- **Exact zeros are still structural.** A conduction component holding only ONE port terminal sits at
  that terminal's potential and conducts nothing whatever the excitation, so such a port gets no
  source and an exactly-zero row and column. That is LF1's series-MIM-cap answer and it is preserved
  bit for bit (Y = 0, S = I). Counted over *terminals* now rather than over held nodes, because a
  delta gap contributes two and neither is ground.
- **A source loop is refused, not factorised.** Two ports sharing one terminal pair would assert two
  voltages across one piece of metal. No port kind this kernel builds can close one, so it is a guard
  with a note rather than a path.

The conductances run to ~1e6 S (`PecSheetResistance` is 1 µΩ/sq) against a source row of ±1, so the
source unknown is scaled by the mean branch conductance — a change of variable, so the answer is
unchanged, but it keeps the pivot search on one scale.

### 3. A SECOND KIND WAS WRONG THE SAME WAY, QUIETLY — AND THAT IS WORSE

`IsConductorReferenced` — an edge port with a named **return conductor**, i.e. every coplanar pair —
is floating too: `_minus` is the return strip's end cells. Its old answer was not catastrophic, which
is why it outlived the gap port's version: it published **the signal conductor's resistance alone,
with the return's contribution simply absent.** On a symmetric coplanar pair that is a clean factor
of two, and in S at 50 Ω on copper both answers read 0 dB. Nothing would have flagged it.

The series law is now exact, measured on a 10 mm pair at three return widths:

| return width | loop R | ratio to symmetric |
|---|---|---|
| = signal | 14.5959 mΩ | 1 |
| signal / 3 | 29.1918 mΩ | **2.000** |
| signal × 3 | 9.7306 mΩ | **0.667** |

R + 3R over R + R, and R + R/3 over R + R. Under the old code all three published 14.5959 mΩ, because
the return conductor never entered the answer at all.

`InternalDeltaGap` with a `Negative` (two cuts in one loop) is the third floating kind and is fixed by
the same change.

### 4. WHAT WAS **NOT** AFFECTED, VERIFIED RATHER THAN ASSERTED

`PlanarPortKind.Internal`, the via-to-plane port, references the ground node and was correct
throughout. Running the same probe against the pre-change source gives S identical to the new one:
−1/3 on the diagonal, +2/3 off it — the shunt junction of three lines meeting at a via. It is the
instructive contrast with the gap port, which puts the third port in **series** and gives +1/3, ∓2/3
on the same three lines. `PlanarDcPointTests` pins both, because a change that broke the reduction to
the Dirichlet case would otherwise be invisible.

### 5. TWO SMALLER THINGS IN THE BLAST RADIUS

- `_plus`/`_minus` held a `Find()` result from `Build` time, but a **later** port's `Tie` can union
  that root under a new one, leaving a stored id that is no longer a root and that the branch list
  never mentions. `held[rep[_groundNode]]` already went through `rep`; the port terminals did not.
  They do now. Reachable only when two ports share cells, which is why it had not bitten.
- The dead-short note named `port {j + 1}` — the row index, not the port number. They differ the
  moment a design numbers its ports anything but 1..n, and the message sent a reader to the wrong
  port. `Network` carries the numbers now.

### 6. WHAT THIS DOES NOT FIX

The reported sweep also has an **8–200 MHz hole** with nothing published in it, in both files: those
points are dropped by PEEL's own `DeembedErrorFloor` guard (§PEEL above), which is working as
designed and hits both setups equally. So on that stack a sweep from 1 MHz has three regimes —
substituted below 7.952 MHz, dropped to 204 MHz, solved above. The consequence for a schematic is
worth stating because it is how the second half of the report arrived: the `.sNp` reader splines
across that hole, and with the LF2 rows on the far side of it being *wrong*, the ring reached **+0.77
dB at 299 MHz and +0.36 dB at 1.37 GHz** in a shorted-gap schematic run — a passive structure
reported as a gain. It was not an EM result; every anomalous frequency was one the spline invented,
and the entries it interpolates cancel (two ~0.66 terms making ~0.99) so the reduction amplifies
whatever error the spline has. A gap-port 3-port wants a denser frequency grid than the 2-port it
replaces, independently of any of this.

## PEEL — the de-embedding peel's own conditioning, published and gated (2026-09-14)

`docs/sonnet-briefs/brief-deembed-peel-low-frequency.md`. Owner report: a 3.8 mm microstrip on the
0.6 mm laminate starter stack, 254 µm wide, swept from 1 MHz, publishes |S₁₁| = −1.37 dB at 9.98 MHz
where the equivalent closed-form line gives −60.2 dB. **It is not a refusal, not a warning and not a
note**, and the one sentence the run does emit down there — LF2's *"Below 7.952 MHz the full-wave fit
has no valid range"* — invites exactly the wrong reading, that 7.952 MHz is where trouble ends.

**The defect that reached a user is not the 59 dB. It is that the 59 dB arrived with no sentence
attached**, and that the one diagnostic anyone would have checked is anti-correlated with the truth.
That is what this section closes. The accuracy is not closed and the measurements below say why.

**The guard is PER POINT: a frequency the peel cannot answer is left out of the published sweep and
named, and every other frequency is published exactly as it was solved.** The first version refused
the whole run and that was wrong — §6 has the reasoning and the house rule it violated.

### 1. M1 — WHERE THE FLOOR IS, AND IT IS NOT WHERE THE BRIEF PUT IT

The brief attributed the amplification to the factor `(x₂² − x₁²) ≈ 2γΔℓ` that both halves of
`SolveErrorBox`'s `a₂₂²` quotient carry, i.e. **to the two standards' SEPARATION**, and said so in
italics: *"the ω is not in `a₂₁`, it is in `x₂² − x₁²`."* It fitted a law — error × 2βΔℓ flat to
within a factor of two over a 42× move in the error — and the fit is real. **The attribution is
wrong, and the experiment that settles it is one line of the brief's own "Must NOT".**

**Δℓ IS INERT.** On the reported cross-section with a 20 mm uniform line, second standards from 3× to
61× the short one (`PeelConditioningTests.ALongerSecondStandardDoesNotMoveIt_AndUpTheBandItIsWorse`):

| Δℓ | βΔℓ @ 10 MHz | standard N | \|S₁₁\| @ 10 MHz | βΔℓ @ 100 MHz | \|S₁₁\| @ 100 MHz |
|---|---|---|---|---|---|
| 20 mm (shipped) | 0.401° | 52 | 1.200e-2 | 4.011° | 1.682e-3 |
| 50 mm | 1.003° | 94 | 1.256e-2 | 10.03° | 2.521e-3 |
| 110 mm | 2.206° | 178 | 1.257e-2 | 22.06° | 4.096e-3 |
| 290 mm | 5.817° | 430 | 1.234e-2 | 58.15° | 8.822e-3 |
| 590 mm | 11.83° | 850 | 1.19e-2 | 118.3° | 1.69e-2 |

**A 30× longer second standard moves the 10 MHz answer by 5 %, and makes the 100 MHz answer TEN TIMES
WORSE.** The brief's "Must NOT" says the obvious move "is the obvious move, it works, and it is
exactly the 1/f_lo standard QSC existed to remove." Two of those three are right. **It does not
work** — and that matters more than being a correction, because a reader who believed it would have
spent the day proving it for themselves, which is precisely what that rule existed to prevent. The
rule stands; its stated reason is now the measured one.

**Why it gets worse going up: the two standards' `M₁₁` inconsistency grows with the length
difference** while the amplifier it is multiplied by does not move at all. Lengthening buys more
inconsistency and no conditioning.

**THE AMPLIFIER IS `1/|a₂₁|²`, AND RAW1 §6 / LF1 §5(a) ALREADY NAMED IT.** `PlanarDeembed.Apply`
forms `Y = (S_meas − a₁₁)/a₂₁²`. An edge port at the bottom of a band is a series gap capacitance —
RAW1 measured its 13.9 fF directly — so `a₂₁ ∝ ω` and `a₁₁ → 1`: the numerator is the difference of
two numbers that both approach 1 and the denominator is an ω². QSC's shorthand *"the `a₂₁ ∝ ω`
floor"* was correct as written and the brief's re-attribution away from it is the error.

**THE LAW.** With `ConsistencyResidual` the two standards' relative disagreement on `a₁₁`:

```
|ΔS| on a uniform line  =  ConsistencyResidual · |a₁₁| / |a₂₁|²
```

Measured against the uniform-line control — the only oracle here with no second model in it, because
a de-embedded S₁₁ on a plain line must be exactly 0 and is read at the line's own Z_c:

| stack / mesh | f | residual | \|a₂₁\|² | predicted | realised | ratio |
|---|---|---|---|---|---|---|
| board 0.6 mm, coarse | 10 MHz | 7.08e-08 | 5.90e-06 | 1.20e-02 | 1.20e-02 | **1.000** |
| | 100 MHz | 9.63e-07 | 5.70e-04 | 1.69e-03 | 1.68e-03 | 0.996 |
| board 0.6 mm, 4× mesh | 10 MHz | 2.97e-07 | 8.53e-07 | 3.48e-01 | 3.39e-01 | 0.973 |
| | 100 MHz | 2.76e-06 | 8.24e-05 | 3.36e-02 | 3.54e-02 | 1.055 |
| | 300 MHz | 9.04e-06 | 7.85e-04 | 1.15e-02 | 1.12e-02 | 0.972 |
| FR-4 1.6 mm, coarse | 10 MHz | 2.13e-06 | 2.64e-05 | 8.06e-02 | 6.84e-02 | 0.849 |
| | 100 MHz | 2.66e-05 | 2.74e-03 | 9.68e-03 | 8.21e-03 | 0.848 |
| FR-4 1.6 mm, 2× mesh | 10 MHz | 6.82e-06 | 7.53e-06 | 9.05e-01 | 6.82e-01 | 0.754 |
| | 100 MHz | 7.11e-05 | 7.94e-04 | 8.95e-02 | 9.16e-02 | 1.023 |
| | 1 GHz | 5.55e-04 | 7.54e-02 | 7.03e-03 | 6.15e-03 | 0.874 |
| GaAs 0.1 mm, coarse | 10 MHz | 1.82e-08 | 4.32e-07 | 4.22e-02 | 4.28e-02 | 1.015 |
| | 300 MHz | 6.45e-08 | 4.54e-04 | 1.42e-04 | 1.42e-04 | 0.999 |

**Three stacks, four mesh densities, five separations, three decades: ratio 0.73–1.06.** The residual
is ∝ ω and `|a₂₁|²` is ∝ ω², so the quotient is ∝ 1/ω — which is the brief's own measured −0.984
log-log slope, arrived at from the other end.

**And the 1/f is why refining the mesh makes it WORSE, which a discretisation error does not do.**
Both factors move the wrong way at once: on the board at 10 MHz, coarse → 2× → 4× takes `|a₂₁|²` from
5.90e-6 to 2.69e-6 to 8.53e-7 (LF2's "the gap is one cell wide, so refining the mesh makes it worse",
again) while the residual goes 7.08e-8 → 1.23e-7 → 2.97e-7. The floor goes 1.20e-2 → 3.72e-2 →
3.48e-1.

### 2. M1.1 — γ IS INNOCENT, AND SO IS EVERYTHING ELSE THE BRIEF CLEARED

Perturbing the two standards' raw S by a relative η and γ by the same η, and reading the change in the
de-embedded |S₁₁| (FR-4, 10 MHz): **amplification of an error in `m` = 1.62e+03; amplification of an
error in γ = 3.75e-06.** Eight orders apart. The floor lives entirely in `m`, i.e. in the standards'
own solved S, and nothing about γ's real or imaginary part reaches it down there. The brief's own
measurements clearing the calibration (ε_eff to 0.4 %, Z_c to 0.3 %, α to 1 %) are consistent with
this and were not re-measured.

### 3. M1.2 — THE TWO STANDARDS ALREADY SHARE THEIR MESH, SO M2(a) IS A NO-OP

`LongitudinalPartition` emits `endRun cells, N bulk cells, mirrored endRun` and accumulates the
gridlines from zero, so the long standard **is** the short one with bulk cells inserted in the middle:
head gridlines bit-identical, tail gridlines identical up to the shift, on both stacks checked. D7's
"the end effects cancel exactly" argument already holds on the mesh, and there is nothing for M2(a)
to build. **That was the brief's cheapest route and its most likely one** ("if it is the second, M2(a)
is the whole fix and the rest of this brief is a guard") — and it was already done, in 2026-08, by
L8d's own D4.

### 4. M1 — WHAT THE RESIDUAL ACTUALLY IS: `a₁₁` DEPENDS ON THE STANDARD'S OWN LENGTH

`a₂₂²` is `(m₂/x₂ − m₁/x₁)/(m₂x₂ − m₁x₁)` and that quotient is **exactly** the condition that the two
standards agree on `a₂₁²` — so they agree on it to 5e-15, by construction, and a "do the two
standards agree" check built on `a₂₁²` measures nothing. **All of the inconsistency lands in `M₁₁`**,
which is what `ConsistencyResidual` reports.

Solving `a₁₁` from each of five lines with `(a₂₁, a₂₂)` held at the two-line values, board 4× mesh at
10 MHz, as a deviation from the shortest line's answer:

| line length | a₁₁ − a₁₁(shortest) | \|S₁₁\| de-embedding the DUT with it |
|---|---|---|
| 3.64 mm | 0 | 5.68e-01 |
| 10.91 mm | 2.97e-07 | **1.81e-02** |
| 21.82 mm | 3.45e-07 | 9.42e-02 |
| 43.64 mm | 3.44e-07 | 9.15e-02 |
| 109.09 mm | 3.28e-07 | 5.47e-02 |
| *shipped: the average of the first two* | 1.48e-07 | 3.39e-01 |

**`a₁₁` is a function of the standard's own length, it saturates by about three times the short one,
and the SHORT standard is the outlier.** The peel needs `a₁₁` at the DUT's own length, to about
2e-8 absolute, and no line standard has that value. Two corollaries worth keeping:

- **The model is nearly right and the peel's demand is what is extreme.** The correction term
  `a₂₁²a₂₂x²/(1 − a₂₂²x²)` is 4.2e-4 on that fixture and the model reproduces it to ~3 parts in 10⁴.
  One more decade of model accuracy would close it; the peel wants five.
- **A single error box that fits both standards does not exist at these frequencies.** An `a₁₁` fitted
  to the short line de-embeds it to 1.2e-10 (machine zero — the algebra is exactly self-consistent,
  so nothing here is floating-point) and de-embeds the long one to 9.7e-1. **The averaging in
  `SolveErrorBox` is not the bug; it is the only defensible thing to do with two incompatible
  answers.**

### 5. M2 — THREE ROUTES, AND NONE OF THEM MOVED THE NUMBER

Reported including the ones that failed, because the next person will otherwise try them.

**(a) Make the difference cancel what it is supposed to cancel — ALREADY TRUE (§3).** Not built,
nothing to build.

**(b) Continue the error box in frequency instead of re-solving it — BUILT, MEASURED, MUCH WORSE.**
Fitting each of `(1−a₁₁)/ω`, `a₂₁/ω`, `(1−a₂₂)/ω` as `A + Bω²` from 1 GHz and 2 GHz and evaluating
below gives |S₁₁| ≈ **1.0 at every frequency** on all three fixtures, against 0.012–0.68 solved. The
reason is arithmetic and it rules out the whole family, not just this fit: the useful signal `1 − a₁₁`
is ∝ ω and the required *absolute* accuracy is ∝ ω², so **the required RELATIVE accuracy tightens as
1/f.** Extrapolating two decades down would need the leading coefficient right to ~1e-4, which no fit
over a solved band delivers. The brief's supporting evidence — that ε_eff and Z_c are constant to five
figures on the quasi-static path — is true and is about γ, which §2 shows is not the quantity in
trouble.

**(c) Add a REFLECT standard below the crossover — NOT BUILT, and §4 bounds what it could buy.**
The brief's premise for (c) was "`a₁₁` and `a₂₂` from an O(1) measurement, with no differencing of two
nearly-identical lines". The differencing is not the problem (§1), and the ceiling was measured
directly: **substituting the `a₁₁` the DUT itself needs — the best any standard of any kind could
supply — takes the error from 3.39e-1 to 6.9e-2 on the board and from 6.8e-1 to 2.0e-1 on FR-4, a
factor of ~5, not the decade the brief hoped for.** The rest is `a₂₁`/`a₂₂`. And a reflect supplies
`a₁₁` at its own length, which by §4 is not the DUT's. **It was not started, on the brief's own
instruction not to start it before M1 had spoken.** It remains the only untried route and it is worth
less than it looked.

**The thing that would close it is not in M2's list and is already written down.** LF2's closing
paragraph and LF1 §5(a) both name it: the enabling change is **the PORT**. `PlanarDcSolve` works at
0 Hz because it uses conduction terminals — the conductor's end cells against the ground node —
rather than a cut in the metal. A port of that shape at AC has an `a₂₁` that does not vanish with ω,
and `1/|a₂₁|²` is the whole amplifier. That is one change that removes this wall *and* lets a
sub-floor point be published without a calibration at all. It is still not built.

### 6. M3 — THE GUARD, WHICH IS WHAT SHIPPED

`PlanarErrorBox` gains two computed properties — no constructor churn, and every existing
construction site is untouched:

- **`PeelAmplification` = |a₁₁| / |a₂₁|²** — a property of the PORT, not of how well it was
  calibrated.
- **`DeembedErrorFloor` = `ConsistencyResidual` × `PeelAmplification`** — in |ΔS|, the units of the
  answer, so it reads against a tolerance directly.

`DeembedErrorFloor` is published per (frequency, port) in the `planar` diagnostics group beside the
two residuals, **unconditionally** — `CalQuasiStatic`'s own rule, so a reader of the file can always
ask the question and gets an answer rather than a missing cube to interpret. `ResultUnits` carries it
as dimensionless.

**`DeembedResidual`'s XML doc now says why it is not a quality measure**, with the reported numbers in
it (1.95e-10 at 10 MHz against 9.38e-9 at 500 MHz — 48× smaller exactly where the answer is worst) and
a pointer to the cube that is. `PlanarErrorBox`'s header already carried *"an honest measure of what
was discarded, not a proven predictor of accuracy"*; this is the concrete case it was hedging about.

**The two thresholds are anchored on what a perfect match gets published as**, which is the shape of
the report itself:

| | |ΔS| | as a return loss | what happens |
|---|---|---|---|
| `PlanarSolve.PeelErrorBudgetDS` | 0.05 | −26 dB | the point is NAMED in the run's notes and still published |
| `PlanarSolve.PeelErrorRefusalDS` | 0.25 | −12.0 dB | the POINT is left out of the sweep, and named by frequency |

−12 dB is what the reported file published for a −60.2 dB line. Because the control's realised |ΔS|
equals the floor to within 0.73–1.06, **a threshold on the floor IS a threshold on |ΔS|** — which is
what makes these measured rather than chosen.

**THE GUARD IS PER POINT, AND THE FIRST VERSION OF IT WAS NOT. That was wrong and the owner caught
it.** It refused the RUN, which costs a user every other frequency they waited for — on the reported
sweep, eight solved points discarded to suppress three, and the discarded ones are the *good* ones
because the wall is at the BOTTOM of the band. **This file already had the right rule written down**,
a few hundred lines further on where a far field that cannot be produced is reported as *"present and
refused — the sweep is not thrown away for a diagnostic it cannot produce"*. The same applies here
with more force, and it is now what the code does: the unanswerable points are removed from the
published sweep and named by frequency, and everything else is published exactly as it was solved
with nothing interpolated across the gap.

**The one case that is still a refusal is the sweep in which EVERY de-embedded point went.** There is
then no result to hand back, so nothing is lost by saying so — which is the whole distinction.

**Neither threshold is a `.cem` field or a panel control** (QSC's reasoning about its crossover,
unchanged), and **the way to get the dropped points into the file anyway is the flag that already
exists**: `DeembedOutsideCalibrationValidity` is named for what it does and already says so in the
run's notes and in the `.sNp`'s provenance. The claim is the same one — this calibration is not valid
at this frequency — and it did not deserve a second flag. The points it puts back are **bit-identical**
to the ones the filtered run kept, which is what says the drop is a filter and not a different solve.

**The note's denominator is the DE-EMBEDDED points, not the frequency list.** By the time the guard
runs, `freqs` has already had the 0 Hz row and LF2's substituted ones taken off its front, so quoting
it reported *"3 of 8"* to a user who asked for eleven.

**The remedy sentence names a band edge the user can type, derived from the run's own measurement.**
The floor goes as 1/f, so `floor · f` is the constant and the budget is met above `floor · f /
budget`, taken over every point and kept at its largest (a sweep that crosses a separation switch has
more than one constant in it). On the reported cross-section that reads **"this port meets 0.05 above
about 461.3 MHz"** — against the brief's own observation that the error "only falls under 1 dB
somewhere near 250 MHz". The sentence also says outright that **lengthening the standards does not
help**, because that is the move a user who reads it will otherwise make, and §1 is the measurement
behind the claim.

**The guard reads the sweep; it does not change it.** It is asked of the points the sweep already
produced, after the fact, so nothing in the hot path moved — `TheGuardChangesNoPublishedNumber`
asserts bit-identical s-parameters against a run with the escape hatch open, and the whole Engine and
Ui suites pass with no re-blessed number anywhere.

**LF2's note is re-pointed in two words and stays ONE sentence.** *"Below X the full-wave FIT — one of
this band's walls, not the band's own floor — has no valid range…"*. LF1's own ask was that an RF
designer will not read it if the text is too long, and `PlanarDcPointTests` asserts that as a rule
(`Assert.DoesNotContain(". ", note)`); appending a clause about the peel would have broken it for the
right reason and the wrong result. The peel's wall gets a sentence of its own instead.

### 6b. The wording of what a run says — owner instruction, 2026-09-14

Three notes were rewritten after the first version of this work shipped them, and the rule behind all
three is worth having in one place because every note in this engine is written by someone who has
just finished reading the code.

**Plain terms.** `fit` is this engine's word for the DCIM Green's-function fit and means nothing to
the person reading the run; `peel`, `error box`, `a₂₁` and `|ΔS|` are the same. The substitution note
now says *"the field solver has no valid range"*. It also used to end with *"one of this band's
walls, not the band's own floor"*, which was accurate and unreadable — the point it was making (that
this is not the only low-frequency limit) is made by the de-embedding note appearing beside it, which
is what the brief actually asked for.

**Short.** The first de-embedding note was eight sentences carrying the mechanism: `a₂₁ ∝ ω`, the
division by `a₂₁²`, why `DeembedResidual` is anti-correlated. **A designer would not have read any of
it, and a note that is not read is worth nothing however true it is.** It is three sentences now —
which points are missing, the band edge that gets them back, where the per-point number is — and the
mechanism lives in this file and in the source, which is where someone asking "why" looks. The gate
asserts a 400-character ceiling so it cannot grow back.

**No block capitals.** They read as shouting and they were everywhere in the first version.

**Nothing about a fixture.** The Z_c note said *"measured against the quasi-static answer on a 50 Ω
FR-4 line"* — a user cares about their own circuit, not the one the number was taken on. The
percentages stayed, because "reads slightly high" is not something anyone can design against; the
fixture moved into the doc comment.

The three, before and after:

| | was | is |
|---|---|---|
| LF2's substitution | *"Below 7.952 MHz the full-wave FIT — one of this band's walls, not the band's own floor — has no valid range, so 2 points … carry the 0 Hz conduction solve…"* | *"Below 7.952 MHz the field solver has no valid range, so 2 points … carry a DC solve — resistance only, no reactance…"* |
| the peel | 8 sentences, ~1,100 characters | *"3 point(s) (9.779 MHz to 44.72 MHz) were dropped: port de-embedding is not reliable that low, and the rest of the sweep is unaffected. De-embedding on this port is reliable above about 461.3 MHz — raise the sweep's lower edge to get them back. Longer calibration lines do not help. The DeembedErrorFloor result estimates the de-embedding error at every point."* |
| `PlanarKernel.QuasiStaticNote` | the formula, the √ε_eff(f) scaling, the fixture, and why a dispersive C is unavailable | *"The reported Z_c is a quasi-static estimate, so it reads slightly high as frequency rises — by roughly 0.4% at 1 GHz, 2% at 5 GHz and 6% at 20 GHz. Your s-parameters are not affected."* |

**One assertion was re-pointed and the reason is recorded at the assertion**: `PlanarDcPointTests`
finds the substitution note by a phrase, and the phrase changed. Everything else about that test is
untouched, including the single-sentence rule it enforces.

**This was scoped to the notes this work touched.** Plenty of other refusals in `PlanarSolve` still
shout; rewriting them is not this brief's job and each one carries tests that assert its wording.

### 7. M4 — THE REPORTED SWEEP, END TO END

1 MHz – 2 GHz, 11 log points, the reported cross-section — **the sweep as it was actually written.**
**It runs, and eight of its eleven rows are published.** Every point leaves by one of exactly four
doors and `TheReportedSweepEndToEnd_…` asserts that the four cover it:

| f | door | floor | \|S₁₁\| | \|S₂₁\| |
|---|---|---|---|---|
| 1 MHz | conduction (LF2) | — | 0.0001 | 0.9999 |
| 2.138 MHz | conduction | — | 0.0001 | 0.9999 |
| 4.573 MHz | conduction | — | 0.0001 | 0.9999 |
| 9.779 MHz | **LEFT OUT** | 2.36 | — | — |
| 20.91 MHz | **LEFT OUT** | 1.10 | — | — |
| 44.72 MHz | **LEFT OUT** | 0.516 | — | — |
| 95.64 MHz | flagged, published | 0.241 | 0.4922 | 0.8820 |
| 204.5 MHz | flagged, published | 0.113 | 0.2736 | 0.9670 |
| 437.3 MHz | flagged, published | 0.0525 | 0.1612 | 0.9888 |
| 935.2 MHz | published | 0.0241 | 0.1263 | 0.9922 |
| 2 GHz | published | 0.0105 | 0.1530 | 0.9870 |

With `DeembedOutsideCalibrationValidity` the three dropped rows come back and read |S₁₁| = 0.9906 /
0.9353 / 0.7651 on a uniform line. **That column IS the reported defect, reproduced** — 0.99 at
9.8 MHz where the right answer is 0 — and it is what is now left out of the file instead of being
published.

**There is no fifth door, and the fifth door — published, silent and wrong — is what this work
removes.** The floor column is the answer to "state the frequency below which this file is no longer
answered": **on this cross-section, about 440 MHz at a 0.05 budget and about 95 MHz at 0.25.** The
brief asked that this be said plainly if M2 achieved nothing, and M2 achieved nothing.

### 8. Traps found

- **THE UNIFORM-LINE CONTROL MUST BE READ AT THE LINE'S OWN Z_c, NOT RENORMALISED TO 50 Ω.** The
  first version of this measurement renormalised, and on the reported cross-section — 106 Ω — that
  adds a real, physical S₁₁ of up to 0.25 at 1 GHz which is not the instrument's error at all. It is
  invisible on the FR-4 hero, which is 50 Ω by construction, and that is why §QSC §5's own control
  curve (0.84 / 0.39 / 0.19 / 0.11) was unaffected by it. **A control that "must read exactly 0" only
  reads exactly 0 in the reference the algebra hands back.**
- **§QSC §5's FR-4 control fixture is DEGENERATE and its numbers should not be quoted as an
  instrument measurement.** The 6 mm line has `EndRunHeights` = 3 × 1.6 mm = 4.8 mm of end run at each
  end against a 6 mm total, so the two error boxes overlap inside the DUT. It reads 0.998 at 10 MHz
  where the same stack AT THE SAME COARSE MESH with a 40 mm line reads **0.068** — a factor of 15, all
  of it fixture. The measurements here use lines long enough that the two boxes do not meet.
- **"Do the two standards agree on `a₂₁²`?" is not a question — it is an identity.** `a₂₂²` is solved
  to make them agree, so the answer is always 5e-15 and a check built on it measures the floating-point
  arithmetic. The only honest agreement measure between the two standards is the `M₁₁` one, which is
  `ConsistencyResidual` and was already there.
- **The peel is NOT losing precision.** An `a₁₁` fitted to one standard de-embeds that standard to
  1e-10 through the full `Apply` + `Renormalise` path. Every decimal of the error is model, not
  roundoff — which is what rules out reordering the arithmetic as a fix.
- **A `record`'s computed property is the way to add a diagnostic to `PlanarErrorBox`.** Adding a
  constructor parameter would have touched three test files that build boxes by hand, for a quantity
  that is a pure function of the three already there.

### 9. Reported to the owner, by file and line (no `CLAUDE.md` edit, per the standing rule)

- **repo-root `CLAUDE.md`, the `Cli em` paragraph (around line 60).** It says nothing about a band
  having a usable BOTTOM. `em` now has a third outcome at low frequency — a refusal whose remedy is
  the sweep's lower edge — and the paragraph's list of refusals (`Refused`/`NoLayout`/`EngineError`)
  does not hint that one of them is about frequency rather than geometry. The brief flagged this line
  as stale before the work started and it still is.
- **`src/Design/Layout/Em/EmRunService.cs:559`** forwards this refusal under the diagnostic key
  `"port-clearance"`, because it reuses `PlanarFeedClearanceRefusedException` — which is the right
  exception (the claim is "this calibration is not valid here") but the wrong label. Cosmetic: the
  message the user reads is unambiguous and the key is not shown. Left alone rather than churned
  through the Ui tests for a string.

### 10. Gates

`tests/Engine.Tests/Mom/PeelConditioningTests.cs`, nine tests, all under a second each — no
`Category=Benchmark` was needed, and the brief's own note that a 5-point version of the reported file
is well under the routine tier is what made that possible.

- `TheErrorFloorPredictsWhatTheUniformLineControlActuallyReads` — four fixtures, the law asserted at
  every point whose floor is above a tenth of the budget (i.e. over the whole range the guard ever
  acts in), plus the DIRECTION assertion: the residual must grow up the band while the error it is
  read as a proxy for shrinks.
- `ALongerSecondStandardDoesNotMoveIt_AndUpTheBandItIsWorse` — the §1 table, both halves. The 100 MHz
  half is what stops someone reading the 10 MHz row as "harmless".
- `TheErrorFloorIsPublishedPerFrequencyAndPort` — the cube, its axes, and its recorded values on the
  reported cross-section.
- `APointThePeelCannotAnswerIsDropped_AndTheRestOfTheSweepSurvives` — the sweep survives, the
  missing frequencies are named, every survivor is on the right side of the wall, and the points the
  escape hatch puts back are bit-identical to the ones the filtered run kept.
- `ASweepWithNothingLeftIsRefused` — the one case that is still a refusal.
- `TheGuardChangesNoPublishedNumber` — bit-identical S with the hatch open, and no note on a band
  where the measured calibration belongs.
- `TheReportedSweepEndToEnd_…` — §7's table, the four doors, and both walls named separately.

Unchanged and passing: `RawSolveAndCalibrationRemedyTests`, `QuasiStaticPortCalibrationTests`,
`PlanarDcPointTests`, `PlanarDeembedTests`, `CoplanarDeembedTests`, `EmDeembedCeilingTests`, the whole
of `Engine.Tests` (2,443) and of `Ui.Tests`' EM set (3,443). **No assertion was re-pointed and none
was deleted** — LF2's note changed by two words inside its existing single sentence, which its own
test already allowed for.

## CL-review — the five smaller findings, fixed (2026-09-14)

Found reviewing the conductor-loss series. The calibration-standard defect is its own section below;
these are the rest, in the order they matter.

### 1. The `.snp` provenance stamp could not see a change to the SOLVER

`EmSnpProvenance` hashes the extracted `EmProblem` — *"everything the answer depends on and nothing
else"* — which answers **"did the DOCUMENT change"** and cannot answer **"did the PHYSICS change"**.
σ and thickness have been in `GeometryHash` since L9d and were simply never READ by the fill, so an
`.snp` written when kernel B's metal was a perfect conductor hashes to **exactly** what the same
design hashes to today. It went on reading as CURRENT while carrying numbers with 92-99% of the MMIC
starter's loss missing.

**CL7's floor covers part of this by accident** — a stackup with σ on its ground layer now hashes
differently, so those files do go stale. A stackup with a perfect plane does not, and CL1/CL3's strip
term moved its answer just the same.

`PlanarKernel.ModelRevision` is the fix: a plain-text token, stamped as a fourth header line
(`circuitRF-EM model: …`) by the PLANAR overload only, and compared as text in `Compare`. **Kernel A
gets none** — nothing in this series touched it, and stamping it would mark every cross-section
`.snp` in every workspace stale to record a change that did not happen.

- **An ABSENT token is a mismatch, not a free pass** — that is every planar `.snp` written before
  this, i.e. exactly the files whose metal was perfect, and the message says so in those words
  rather than leaving "a different setup" to be read as an edit nobody made.
- **Bump it when a change moves a published s-parameter for an unchanged document**; never for a
  performance change, a refactor or a refusal's wording, which are the changes that must not
  invalidate anyone's cache. The constant's own doc carries that rule.
- Gate: `ConductorLossProvenanceTests.AFileWrittenByAnEarlierPHYSICSReadsAsStale` (current / named
  earlier / no token at all) and `…TheCrossSectionKernelIsNotStamped`.

**The precedent is ANT-2's**, which broke `MeshHash`'s own omit-at-default rule on purpose for the
same reason and recorded *"every pre-ANT-2 planar `.snp` reads as stale once, correctly."*

### 2. `PowerDielectric` → `PowerDielectricAndGround` had no user-facing note

CL7 renamed a PUBLISHED cube. The rename is right and is recorded in §CL7, but nothing a user reads
said so, and a Data Display or script naming the old cube resolves to nothing rather than erroring.
`docs/user/src/reference/antennas.md` now carries the rename and what to do about it.

### 3. The same page still gave the PEC reason for refusing `FrontToBackDb`

*"There is no field behind an infinite plane, so the true ratio is infinite"* — which CL7 made false
for the COMMON case: a conducting plane leaks, so the true front-to-back of the structure the user
drew is finite. `PlanarMetrics.FrontToBackLossyFloorPreamble` corrects it in the engine and the user
page contradicted it. **A refusal whose stated reason has become false is worse than a missing
metric** (CL4 §6's own rule) — the page now says what is actually missing, which is a REGION.

### 4. `PlanarSolve.BandEdgeRemedy` quoted three stale constants, and they had acquired a DIRECTION

§CL3 §5 flagged them as *"Reported, not fixed"*. They were measured at PCAL7 on a different board
with PEC metal, and the sentence had come to read as *"narrow the band and the separation rises"*.
**Re-measured on this series' own coupled pair, at the same 200 MHz, with the shipped real metal:**

| | 200-400 MHz | 200-800 MHz | 100 MHz-1 GHz |
|---|---|---|---|
| edge mesh OFF (N = 48) | **0.405° refused** | 1.29° publishes | 1.31° publishes |
| edge mesh ON (N = 424) | **0.96° publishes** | **0.155° refused** | (standard over the ceiling) |

**So widening helps with the edge mesh off and hurts with it on.** The lever is real; its SIGN is
not a rule, which is §CL3 §5's own non-monotonicity finding arriving in the one place a user reads
it. The message now names both levers, says outright that neither has a fixed direction, and tells
the reader to try it and read the reported figure back. The accuracy figure behind *"narrowing is
free"* (0.1099 against 0.1091 max |ΔS|) was measured on the same PEC board and is DROPPED rather
than re-quoted — it supported a recommendation no longer being made.

Gates: `PlanarGroupSeparationTests.TheBandIsALever_AndTheRefusalQuotesTheMeasuredSizes` (the cheap
half plus the four figures the message quotes, so it cannot drift from the fixture again) and
`…TheEdgeMeshMovesTheSameSeparationTheOtherWay` (`Category=Benchmark` — the edge-meshed fixture is a
grouped de-embedded calibration on N = 424, ~2.5 min).

### 5. Three smaller ones

- **`PlanarConductorLoss.SheetAt` clamped an out-of-range level index**, so a mesh naming more levels
  than the problem declares read the LAST level's metal — a plausible loss figure for a pairing that
  is simply wrong, and the same class of silent substitution the name-first resolution exists to
  prevent. It refuses now, and `SheetTable` refuses a level that resolves by neither name nor index.
- **`PlanarPowerBudget.ConductorBoundClause` said the FR-4 2 GHz share is 6.5 %**; the series'
  own re-measurement and the user page both say 6.4 %.
- **The cross-section port list labelled near/far by the row's POSITION** while its default Z₀ came
  from `portNumber - 1`. D3 says near/far is a property of the port NUMBER, and the two agreed only
  for contiguous 1..N numbering — which the port-identity fix in this same series made possible to
  not have. The label follows the number now.

### Reported and NOT fixed: the repo-root `CLAUDE.md`'s `Category=Benchmark` count

It says **128 test methods repo-wide, 97 in `Engine.Tests`**. Counted mechanically (every
`[Trait("Category", "Benchmark")]`, with a class-level one expanded to the `[Fact]`/`[Theory]`
methods it covers) the figure is **397 — Engine 197, Ui 153, Harmonica 22, WBond 20, RfCore 4,
Core 1**. It has been stale for far longer than this series, which only moved it 122 → 128, and the
opt-in tier's stated runtime is optimistic in proportion. **Not edited here, and not edited there
either** — the standing instruction is that nothing writes to a `CLAUDE.md` from this kind of work.

## CL-review — a calibration standard was made of the wrong level's metal (2026-09-14)

Found reviewing the conductor-loss series, not by a failing test. **A defect introduced by CL1 and
made live by CL3**, invisible on every fixture the series used and on every single-level design.

### What was wrong

`PlanarConductorLoss.SheetTable` resolves a mesh's level to a problem level **by NAME first, by index
second**, and its own doc says why: a calibration standard's mesh carries exactly one conductor level
and numbers it 0 whatever level the port sits on, so an index lookup would hand a Metal-2 standard
Metal-1's metal. That is the right design.

**Nothing ever supplied the name.** `PlanarCalibration.BuildLine`'s `layerName` parameter defaults to
the placeholder `"Metal"` and both `BuildSet` overloads called it without one, so on a real technology
— whose levels are called `M1`, `M2`, `TopMetal` — the name match ALWAYS failed and the INDEX fallback
answered. Every calibration standard on every port was filled with **level 0's σ and thickness**.

- **Single-level problems are unaffected**, which is why it was invisible: index 0 and the one level
  are the same level by either route, and `PlanarLineFixtures.Problem` happens to name its layer
  `"Metal"` besides.
- **Multi-level problems with a port on level ≥ 1 got the wrong metal** — on the two-level MMIC
  fixture, 2 µm M1 in place of 3 µm M2, which at 30 GHz (δ = 0.45 µm) is a real difference in
  Re(Z_s) rather than a rounding one. It lands in the standard's own α, hence in the two-line γ, in
  the error box, and — through `PlanarQuasiStaticConductor`, which reads the same table off the same
  mesh — in the quasi-static γ below the crossover as well.
- **It was a hard zero before CL3**, because the fill read neither σ nor t. CL1 made the numbers
  load-bearing and CL3 turned them on; the placeholder has been there since long before either.

### The fix

`BuildSet` takes a `layerName` (defaulted to `PlanarCalibration.DefaultLayerName`, so every existing
caller is unchanged) and `PlanarSolve` passes `problem.Layers[port.LayerIndex].Name`;
`PlanarPortCalibrator` takes the same argument for the set it builds itself.
**Bit-identical wherever the old path was right** — one level resolves to index 0 by either route, and
a multi-level port on level 0 resolves to 0 by either route.

Gate: `PlanarSurfaceImpedanceTests.R_cl1_2d_AStandardIsFilledWithItsOwnLevelsMetal`, which asserts the
named set gets M2's Z_s **and** that the placeholder set gets M1's — the defect written down, so the
name cannot quietly stop being passed.

### What it says about the rule

The name-first resolution was written, documented and reasoned about correctly, and then the
production path never exercised it. **A fallback that is correct for the common case hides a lookup
that never succeeds** — nothing failed, nothing warned, and the index answer is always a plausible
metal. The general form: when a lookup has a fallback, assert somewhere that the LOOKUP succeeds, not
just that the answer is sane.

## CL7 — the ground plane reaches a user (2026-09-14)

`docs/sonnet-briefs/brief-conductor-loss-7-ground-reaches-a-user.md`. CL4 built a conducting ground
plane on the general kernel and CL6 built it on the one-slab kernel; both passed every gate and
neither reached a run, because `PlanarExtractor` wrote `Termination.Pec` into both media.
**It writes the real floor now.** Every shipped technology carries σ on its ground-designated
conductor, so every run on one gets a plane worth **21.1% of the conductor term on FR-4, ~11% on the
MMIC starter and 25.0% on a low-loss laminate** — and on the MMIC starter the conductor term is
92-99% of the line's loss, so this is about a tenth of the total loss of every GaAs line the tool
draws.

**The brief's two milestones both landed, and milestone 0 found something the brief did not
anticipate.** §1 is where the work is.

### 0. The change, which really is small

`PlanarExtractor.FloorFor(groundBand)` is the whole of it: `Termination.LossyGround(σ, t)` from the
band that was CHOSEN as the return plane, written into `GroundedSlab.Floor` and into
`BuildMediumStack`'s `LayerStack` alike — **both spellings, because the half-measure would have given
two physically identical designs different ground physics according to whether the stackup carried
one dielectric entry or two** (CL4 §9's refusal, and the reason CL6 exists).

**A PERFECT floor is returned as `Termination.Pec`, not as a perfect SPELLING of a conducting plane.**
The two are bit-identical in both kernels (CL4 §1, CL6 §2) and are NOT identical to
`EmSnpProvenance` or to record equality, so spelling "no metal" as `LossyGround(0, t)` would have
invalidated every cached `.snp` in every workspace to record a σ nobody supplied.

**`PlanarProblem.PerfectGround` is the oracle and it is permanent**, on `PlanarFillSettings.
PerfectConductor`'s own pattern — every CL4, CL6 and CL7 ground figure is a comparison against it.
It moves BOTH spellings together; that flag is not it and cannot be made to be, because it makes the
STRIP perfect and the plane is not in the fill (CL4 §8).

No `.cem` key, no `EmSetupPersistence` field, no UI control — series overview §5.

### 1. Milestone 0 — THE PLANE'S RETURN CURRENT, NOT ITS INDUCED CHARGE

**Candidate (a) shipped, and the brief's own statement of it is subtly wrong in a way that is worth
43% of the answer.** The brief proposes *"the plane's own INDUCED CHARGE, through the same TEM
identity"*. The loss integral is `½Re(Z_s)∫|K_g|²dA` and what it needs is the plane's CURRENT
density. **In an INHOMOGENEOUS line those are two different distributions**, and they part company by
a factor that is a property of the substrate:

    σ̂_g(k) = −ε*/(sinh kh + ε* cosh kh)      the ELECTROSTATIC induced charge
    K̂_g(k) = −e^{−kh}                          the MAGNETOSTATIC return current

The second does not see the dielectric at all — µᵣ is 1 everywhere, so a horizontal current at height
h over a PEC plane has exactly ONE negative image, which is `StaticGreens.VectorPotential`'s own
sentence one quantity over. The first is an image LADDER at ratio `Γ = (1−ε*)/(1+ε*)`, the same ratio
`StaticGreens.ScalarPotential` sums, and it is **more concentrated by exactly `A₀ = 2ε*/(1+ε*)` for a
filament: 1.630 on FR-4 and 1.856 on GaAs.**

Parseval turns the right one into a CLOSED FORM over the same cells — no ladder, no quadrature:

    S_g = ∫|K_g|² dA = Σ_ij q_i q̄_j K(d_ij) ,   K(d) = h / (π(4h² + d²)^{3/2})
    R_ground = Re(Z_plane(ω,σ,t)) · [ ΔS_g · Δℓ / |ΔT|² ]

**Measured on CL3 §0's own instrument** — α as kernel B's two standards see it with a conducting
floor minus the same two standards on a PEC floor, `PlanarFillSettings.PerfectConductor` held ON
throughout so the strip is perfect on both sides and what is left is the ground term alone, and the
separation plan's quasi-static index forced to −1 so every point takes the MEASURED two-line path
(comparing the supplier against a γ the supplier produced would be a tautology):

| stack, mesh | f | α_gnd fill | α_gnd supplier | supplier/fill | the CHARGE kernel | charge/fill |
|---|---|---|---|---|---|---|
| FR-4, cells/λ 10, no edge | 0.5 GHz | 4.974e-3 | 4.794e-3 | **0.964** | 7.489e-3 | 1.506 |
| | 1 GHz | 7.069e-3 | 6.780e-3 | 0.959 | 1.059e-2 | 1.498 |
| | 2 GHz | 1.034e-2 | 9.589e-3 | 0.927 | 1.498e-2 | 1.449 |
| | 3 GHz | 1.316e-2 | 1.174e-2 | 0.892 | 1.834e-2 | 1.394 |
| FR-4, cells/λ 20, edge 3 | 0.5 GHz | 5.033e-3 | 4.826e-3 | 0.959 | 7.511e-3 | 1.492 |
| | 2 GHz | 1.047e-2 | 9.652e-3 | 0.922 | 1.502e-2 | 1.435 |
| | 3 GHz | 1.333e-2 | 1.182e-2 | **0.887** | 1.840e-2 | 1.380 |
| GaAs, cells/λ 10, no edge | 5 GHz | 3.333e-1 | 3.212e-1 | 0.964 | 5.998e-1 | 1.799 |
| | 10 GHz | 4.734e-1 | 4.554e-1 | 0.962 | 8.503e-1 | 1.796 |
| | 20 GHz | 6.930e-1 | 6.432e-1 | 0.928 | 1.201 | 1.733 |
| | 25 GHz | 7.895e-1 | 7.192e-1 | 0.911 | 1.343 | 1.701 |
| GaAs, cells/λ 20, edge 3 | 5 GHz | 3.338e-1 | 3.282e-1 | **0.983** | 6.082e-1 | 1.822 |
| | 10 GHz | 4.799e-1 | 4.653e-1 | 0.970 | 8.623e-1 | 1.797 |
| | 20 GHz | 7.025e-1 | 6.573e-1 | 0.936 | 1.218 | 1.734 |
| | 25 GHz | 8.023e-1 | 7.349e-1 | 0.916 | 1.362 | 1.697 |

**0.887 – 0.983 across both starters, four frequencies and four meshes; the rejected kernel is
1.38-1.82× and is the closer of the two at NO point.** The supplier also lands on kernel A: FR-4 at
2 GHz reads 9.652e-3 against kernel A's 9.7858e-3, **0.986×** — where the charge kernel reads 1.53×.
Kernel A was not an input anywhere (R-qsc-2's first bullet, D7's rule); it is quoted here as the
oracle it is.

**The drift down each column is not noise and it is not a defect.** It tracks k₀H exactly as
§CL4 §7's own finding does: the full-wave ground term absorbs the surface-wave and radiated field,
which a quasi-TEM supplier structurally has no term for. FR-4 goes 0.964 → 0.887 over k₀H 0.017 →
0.10; GaAs goes 0.983 → 0.916 over 0.010 → 0.052.

**THE MESH ARGUMENT IS DIFFERENT HERE FROM CL3'S, AND THE DIFFERENCE IS THE FINDING.** CL3's whole
case for the static-charge supplier was that it *moves with the mesh the way the fill's term does* —
turning the edge mesh on raised both by ~32% on FR-4, where kernel A did not move at all. **The
GROUND term does not move with the mesh, in either instrument**: fill ×1.0127 and supplier ×1.0066
across the same edge-mesh change. That is a statement about the PLANE rather than a weak test — the
strip's current crowds at its edges and the mesh is what resolves that, while the plane's return
current has no edge to crowd at and is spread over a width set by h, which the strip's mesh does not
change. What is gated instead is that the RATIO holds: 0.927 coarse against 0.922 refined.

**MIM-4's interior route supplies NO ground term, and that is stated rather than defaulted.** A
standard on a buried level of a stratified stack is not one horizontal current one image-height above
one plane, so the closed form does not describe it and there is no image expansion to fall back on.
`PlanarQuasiStaticLine.GroundTermSupplied` is false there, `PlanarPortCalibrator.
QuasiStaticGroundTermMissing` says so without extracting the line, and `PlanarSolve`'s own
quasi-static calibration note carries the residue in the user's own words. **Below the crossover such
a point's α is low by the plane's share**; every point above it carries the term in full.

**The kernel's own oracles are asserted before anything is measured with it** (`GroundReachesAUser
Tests`): `∫K d²ρ = 1` to 1e-9 (the plane returns the whole current), a uniform filament integrating
to `R = Re(Z_s)/(2πh)` to 0.002% — which is also a strict UPPER bound on any distributed strip — and
the rejected ladder checked against a direct numerical Hankel transform of `|σ̂_g|²` to 1e-8 … 1e-7 on
both starters, so the decision is a comparison of two CORRECT kernels rather than of one correct one
and one bug.

### 2. Milestone 0b — the budget line is RENAMED, and here is what it was carrying

**Decision: rename and re-document, not split.** Splitting needs a second quadratic form in the
spectral domain — a 2D spectral integral per basis PAIR for `½∫Re(Z_s)|H_tan|²` over the plane —
which is a brief rather than a relabelling, and CL4's own "Must NOT" reserved the residual's
arithmetic. What this brief may not do is leave a line whose label names one mechanism while it
reports two.

`PlanarPowerBudget.DielectricW` → **`DielectricAndGroundW`**; the published cube
`PowerDielectric` → **`PowerDielectricAndGround`**; the caption and the metric note say what it
contains, and the antenna reference's budget table says why the two cannot be separated.

**The size, measured two ways.** The sharp form is R-cl2-3a's construction one surface over — tanδ = 0
with a PERFECT strip, where the plane is the only absorber in the model, so the whole residual is the
plane's:

| fixture | tanδ = 0, perfect strip: residual as a share of accepted | | realistic run: the PLANE's share of the renamed line |
|---|---|---|---|
| | PEC floor | real plane | |
| FR-4 line, 6 GHz, N = 115 | −6.4e-5 | **2.18%** | 720 nW of 169.5 µW = **0.42%** (tanδ = 0.02) |
| GaAs line, 30 GHz, N = 59 | 5.4e-4 | **58.5%** | 1.65 µW of 12.09 µW = **13.7%** (tanδ = 0.002) |

**On the MMIC starter the plane is a seventh of the line a user reads**, and on a tanδ = 0 substrate
it is all of it. On FR-4 the dielectric swamps it, which is the same ordering every figure in this
series has. The budget still closes as an identity to 1e-12.

### 3. R-cl7-2 — against kernel A, both surfaces lossy, ON THE ONE-SLAB PATH

CL4 §7 measured this on the GENERAL kernel with an explicit `LayerStack`, because that was the only
place a conducting floor could be spelled. **This runs on the one-slab kernel**, which is what an
ordinary microstrip now gets. γ from the two-line extraction directly, never through
`PlanarPortCalibrator`, so the QSC crossover is not in the path. The rows are the two where
k₀H ≤ 0.07, which is where CL4 established the two formulations agree to ≈6%.

| | k₀H | α_strip | α_ground | α_total | additivity | ground B/A |
|---|---|---|---|---|---|---|
| **FR-4 1.6 mm @ 2 GHz**, kernel A | 0.0671 | 3.6675e-2 | 9.7858e-3 | 4.6461e-2 | | (share 21.06%) |
| kernel B, σ = 5.8e7 | | 2.7425e-2 | **1.0392e-2** | 3.7813e-2 | 1.0001 | **1.0620** |
| kernel B, σ = 1.45e7 | | 2.7425e-2 | 2.0778e-2 | 4.8194e-2 | 1.0002 | 1.0615 |
| **GaAs 100 µm @ 10 GHz**, kernel A | 0.0210 | 3.8256 | 4.5310e-1 | 4.2787 | | (share 10.59%) |
| kernel B, σ = 4.1e7 | | 2.7977 | **4.7888e-1** | 3.2725 | 1.0012 | **1.0569** |
| kernel B, σ = 1.025e7 | | 2.7977 | 8.9486e-1 | 3.6847 | 1.0021 | 0.9834 |

Np/m. **It reproduces CL4 §7 to every digit that brief quoted** — 1.062 and 1.0569 — on a different
kernel, which is the cross-check that the one-slab floor lands on CL4's own answer rather than near
it. The 1/√σ signature across the 4× resistivity step is **1.9994** (FR-4) and **1.8687** (GaAs)
against √4 = 2, the second reproducing CL4's own figure exactly.

**The SHARE is not gated anywhere and must not be quoted.** Kernel B reads 27-28% where kernel A
reads 21.06% on the same row, and the inflation is exactly CL1's measured single-sheet strip deficit
in the DENOMINATOR. A first draft of `ThePublishedAnswerMoves_…` DID gate it and failed at 40.19% on
a 6 GHz FR-4 row — the brief's own "Must NOT", caught by the measurement rather than by reading.
What that gate asserts instead is the 1/√σ signature, which has no denominator to be wrong:
**×2.0023 at 2 GHz and ×1.9617 at 6 GHz** on the extractor's own problem with the technology's ground
σ quartered.

### 4. R-cl7-5 — the three checks CL4 carried none of

- **Passivity with a DIRECTION check.** σ_max(RawS) may only FALL when the plane becomes a conductor,
  gated on the RAW matrix for CL3 §9's reason (the de-embedded answer is already over 1 at the bottom
  of the band because D6's peel divides by a₂₁², so a de-embedded-only test would measure the peel).
  It falls at every point on both starters — FR-4 0.998021812 → 0.998021780 at 1 GHz, 0.988548637 →
  0.988545020 at 6 GHz; GaAs 0.999811841 → 0.999811704 at 10 GHz. **Reciprocity does not degrade at
  all** (|S₁₂−S₂₁| stays at 1e-18 … 6e-17): the floor enters through the Green's function, which is
  symmetric in source and observer.
- **The hero check.** Confirmed structurally rather than assumed, exactly as CL3 §4 did it: **0 of
  114 files** under `tests/Engine.Tests/{Linear,Nonlinear,HarmonicBalance,Loadpull}` reference
  `Planar` or `Mom` at all, and every one of them passed in the single run below.
- **The golden-move tabulation.** §5.

### 5. THE RE-BLESS WAS ZERO TESTS, AND THAT NEEDED PROVING RATHER THAN ACCEPTING

CL3 planned for ~90 files and moved two. **This one planned for a CL3-sized re-bless and moved
none**: `dotnet test tests/Engine.Tests` — **2,434 passed, 0 failed, 1 skipped** (the same
pre-existing `DataSetExportTests` skip §CL4 §10 recorded), 2 m 4 s; `dotnet test tests/Ui.Tests` —
**14,495 passed, 0 failed**, 9 m 30 s. Both run ONCE and triaged from the TRX.

**A zero re-bless is indistinguishable from a flip that did not happen, so the reason is measured:**

- **`tests/Engine.Tests` cannot reach the change at all.** The extractor is in `src/Design`; nothing
  under `tests/Engine.Tests` calls it, and every CL7 measurement there builds its floor by hand. That
  project's 2,434 green is the statement that the ENGINE did not move, which is what CL6's own
  bit-identity claim rests on.
- **47 files under `tests/Ui.Tests` do reach `PlanarExtractor`, and their numeric assertions are
  tolerance-based ranges** — `Assert.InRange(eeff, 3.0, 3.6)`, unknown counts, notes, refusals. The
  ground term moves |S₂₁| by 4e-4 dB on the shipped starter and crosses none of them.
- **`EmCliVerbTests`' byte-for-byte comparison is between the CLI and `EmRunService.Run`**, so both
  sides moved together and the identity held — which is the right outcome and not a gap.

**So the two-sided gate the brief asked for is built on the extractor's own problem rather than on a
recorded literal**, which §CL3 §11 already prefers ("asserted against a second ROUTE rather than
against a recorded literal"): `R_cl7_1` asserts the pre-CL7 answer is reproduced **bit for bit**
through the whole shipped path — 0/96 bits on both starters, over every perfect SPELLING of the floor
(σ = +∞, t = 0, σ = 0) and over `PerfectGround` — and `ThePublishedAnswerMoves_ByTheShareTheGround
PlaneIsWorth` asserts the move, its DIRECTION, its ADDITIVITY (1.000 ± 0.03) and its 1/√σ scaling on
the same mesh at the same frequency.

### 6. Traps and findings

- **The test harness's kernel cache was the defect CL6 §7 predicted, in a cache instead of a hash.**
  `PlanarLineFixtures.Kernel` keyed on `(height, εᵣ, tanδ, f)` and REBUILT a floorless slab to fit
  with, so two slabs differing only in the ground's metal were served one PEC kernel. **Every ground
  measurement came out EXACTLY 0.000 — a plausible, silent, perfectly stable zero**, and the first
  milestone-0 run reported it as four clean tables. The key is the `GroundedSlab` itself now; it is a
  record and `Floor` is part of its value equality, so it cannot go stale again when the next field is
  added. The production-side counterpart was closed at the same time (§7 below).
- **`PlanarFillSettings.PerfectConductor` had to be kept OUT of the ground term's own `if`.** CL3's
  supplier is built inside `if (settings.ConductorLoss is { } loss)`; reading the ground term there
  too would have made that flag turn the PLANE perfect on the quasi-static path and not in the
  full-wave kernel — the exact asymmetry CL4 §8 records eating its own first measurement, and it would
  have made the four-alpha instrument unmeasurable. `PlanarQuasiStaticGround` is a separate object for
  that reason and for no other.
- **A perfect floor produces NO `Ground` object rather than one whose R is zero.** A `+ 0.0` would put
  a PEC-ground run on `GammaAt`'s other spelling and move it by an ulp, which is exactly enough to
  stop `PerfectGround` being an oracle — CL3 §3's lesson, which had to be applied a second time to a
  second term in the same expression.
- **The extractor's own notes claimed a PEC in three places and two of them were user-facing.** The
  return-plane override note and the ground-outline note both said "laterally infinite PEC"; both are
  corrected, and a new note names the plane's σ and thickness — or says the plane is PERFECT and what
  that costs, because the two states are not distinguishable from any published number.
- **`PlanarProblem.PerfectGround` moves BOTH spellings of the medium** and a version that moved only
  the slab would have left a general-kernel run comparing against itself.

### 7. What was checked and did NOT need changing

- **The DCIM fit cache cannot confuse two floors.** `PlanarKernelSet.FitCache` is an instance field
  of a set built from one `LayeredSpectralGreens`, which holds the stack; `PlanarKernelPair.Fit`
  takes the slab directly. There is no static cache anywhere under `src/Engine/Mom` keyed on a
  medium. The brief named this as plumbing that "fails silently if missed"; it was already right, and
  the one that was not right was in the test harness (§6).
- **`PlanarSolve.DescribedByTheSlab` DID need it, and the fix is an equality rather than a
  predicate.** CL4 made it ask `IsConductor`; once CL6 gave `GroundedSlab` a floor of its own, "is
  this stack the slab" stopped being answerable without comparing the two floors — a stack with a
  copper floor and a slab with a PEC one are different electrostatic problems, and answering yes would
  put one's reference impedance on the other's image series. `SameFloor` treats every PERFECT spelling
  as one floor, because they are one physically and are bit-identical in both kernels.
- **`EmSnpProvenance` now hashes the ONE-SLAB floor.** §CL6 §7 found that a one-slab problem
  contributed only the slab's height, εᵣ, tanδ and µᵣ, so two runs differing only in the ground's σ
  would have shared a cached `.snp`. Appended only for a surface impedance, so **a PEC floor hashes
  exactly as it did** and no cached `.snp` in any existing workspace is invalidated —
  `ConductorLossProvenanceTests` asserts that against the pre-CL7 stamp rather than by inspection.
- **A plane that is SKIPPED or absorbed does not become the floor.** `FloorFor` reads the CHOSEN band
  and does not search for metal; on the 4-layer starter with levels straddling Inner 1, the floor is
  the bottom plane's 35 µm and not the skipped plane's 17.5 µm, and the existing warning is unchanged.

### 8. Gates

- `tests/Engine.Tests/Mom/GroundReachesAUserTests.cs` — **11 routine tests (~15 s) and 1 tagged
  `Category=Benchmark` in two shapes**: `R_cl7_2_…` (2 cases, 1 m 26 s together) and
  `R_cl7_3_…_RefinedMesh` (2 cases, 20 s). Measured, then split by cost tier — a `[Theory]`'s
  `InlineData` cases cannot be tagged individually, and the coarse half of milestone 0 is the one that
  would catch a supplier someone deleted rather than narrowed, so it stays in the default gate.
- `tests/Ui.Tests/Em/GroundPlaneMetalReachesARunTests.cs` — 8 tests, ~2 s. The extractor's own half:
  the floor arrives on five (technology, conductor) pairs, both spellings carry the same metal, the
  note says which state the run is in, a skipped plane does not become the floor, and the published
  answer moves by an accounted amount.
- `tests/Ui.Tests/Em/ConductorLossProvenanceTests.cs` — R-cl7-7's ground-σ half.
- **R-cl7-6** — the suite run ONCE per project and triaged from the TRX: §5.

### 9. The `CLAUDE.md` lines this brief made stale

Reported to the owner by file and line first, per the standing rule; **the owner then asked for them
to be fixed, and `src/Engine/Mom/CLAUDE.md` carries the corrections.** What was false, and what
replaced it:

- **§5's conductor-loss bullet** ended *"The GROUND PLANE is still PEC in every run anyone can
  make"*, and went on to say the termination *"is not wired to `PlanarExtractor`, because a
  conducting floor is expressible only through `MediumStack`, which forces the GENERAL kernel — and
  that kernel refuses a lossless dielectric outright."* **Every clause of that is now false** — CL5
  retired the refusal, CL6 gave the one-slab kernel its own floor, CL7 wired it. The ground plane is
  its own bullet now: what it is worth, where `FloorFor` decides it, why BOTH spellings of the medium
  get the same floor, and why `PerfectGround` rather than `PerfectConductor` is its oracle.
- **The same bullet's last clause** — *"A conducting floor's own dissipation **would** land in CL2's
  `P_dielectric` residual … recorded, not fixed"* — was conditional and is now actual. It moved to
  §3.7 with the measured size and the rename.
- **§5's validated-range table**, the `CL4's conducting floor` row, ended *"Deliberately not a
  refusal — nothing shipped can build the termination (§7)"*. The row is still not a refusal and the
  reason is now the real one: above k₀H ≈ 0.07 neither formulation is authoritative, so it is
  reported rather than resolved.
- **§5's quasi-static-γ bullet** described CL3's `Σ_levels` supplier alone. It carries CL7's ground
  half now, including the RETURN-CURRENT-not-CHARGE finding and the interior route's residue.
- **§3.7** — `PowerDielectric` is renamed, and the sentence *"What is published is the dielectric
  loss NOT carried away by a guided mode"* named one mechanism while the line reports two.
- **§7's θ-axis bullet** said the field below is *"identically zero by construction"*. True for a PEC
  floor; for a conducting one the model computes no transmitted field at all, so what is missing
  below is a REGION rather than a small number — CL4 §6's correction, which this brief makes reachable.
- **§7's `FrontToBackDb` refusal** carried the PEC reason first and the conducting-floor sentence as
  an aside. Since CL7 the conducting floor is what every run with a σ on its ground layer gets, so
  the two branches are re-ordered and the common one says so.
- **§7's refusal list** named `PowerDielectric`.

**Still outstanding and NOT edited here** (it is not this file): the repo-root `CLAUDE.md`'s
test-suite section counts **128 `Category=Benchmark` methods repo-wide**; this brief adds **2**
(`R_cl7_2_…` 2 cases at 1 m 26 s, `R_cl7_3_…_RefinedMesh` 2 cases at 20 s), so that count is **130**
and the tier grows by ~1 m 46 s.

## CL6 — the one-slab kernel gets the same floor (2026-09-14)

`docs/sonnet-briefs/brief-conductor-loss-6-one-slab-floor.md`. CL4 built a conducting ground plane
and §CL4 §9 recorded that **no run a user can make gets one**: a lossy floor was expressible only on
`LayerStack`, `PlanarProblem.RequiresGeneralKernel` turns on the moment a `MediumStack` is given, and
every single-level single-dielectric design — the FR-4 hero, the shipped patch, every ordinary
microstrip — would have been re-based from `Dcim.ValidatedRhoOverLambda`'s ≤ 6e-3 onto
`ValidatedRhoOverLambdaLayered`'s ≤ 1.6e-2 to gain a term worth 11-25% of the conductor loss. **That
trade is refused. The one-slab kernel has its own floor now and every run stays on the tier it was
measured on.**

**All four ordered measurements pass and nothing was widened.** `GroundedSlab.Floor` is the change;
the three reflection coefficients are re-derived for a terminated slab rather than a shorted one;
`LayerStack.FromGroundedSlab` passes the floor through, so D5's bridge means the same thing at every
σ instead of only at σ = ∞. **The extractor still writes a PEC floor and no run's answer moves** —
that is CL7's, deliberately, so the kernel change and the re-bless can be attributed separately.

### 0. The change, and what stays exactly as it was

`Z_s` is CL4's ONE-SIDED `η_c·coth(γ_c t)` read through `Termination.SurfaceImpedanceAt` →
`PlanarSurfaceImpedance.Plane`. **This brief writes no surface impedance of its own**, so the two
kernels cannot drift about what a ground plane's metal costs. The plane is still laterally infinite,
still unmeshed, still adds no unknown; only the terminating reflection changes. `FrontToBackDb` stays
refused for §CL4 §6's reason and was not re-opened.

Written with `ζ = Z_s/(ωµ₀)` — metres, so every term is homogeneous and no loose ω or µ₀ survives —
and in the cross-multiplied `T = tan(k_z1h)/k_z1` the file already used:

    Γ^h = [ k_z0(ζ + jT) − D ] / [ k_z0(ζ + jT) + D ] ,          D = 1 + j ζ k_z1² T
    Γ^e = [ (ε*k₀²ζ + j k_z1²T) − ε*k_z0 E ] / [ … + ε*k_z0 E ] , E = 1 + j ε*k₀² ζ T

At ζ = 0, `D = E = 1` and both collapse to the shipped `TeFrom`/`TmFrom` **term for term** — which is
what makes the bit-identity gate reachable rather than hopeful. Neither divides by k_z0, so the
branch point at k_ρ = k₀ is still finite (`Γ^h → −1`, `Γ^e → +1` there). Everything is still EVEN in
k_z1, so the k_z1 branch still cannot matter and there is still no cut at k_ρ = k₁ — §CL4 §3's
finding, said for one layer, and the reason DCIM's basis is untouched.

**A GroundedSlab's floor may only be a GROUND.** `SpectralGreens.CanSolveAt` refuses a PMC or an open
half-space by name and points at `LayeredSpectralGreens`, and refuses a NaN σ or t in the same words
`LayerStack.CanRepresent` uses. A ground plane is a PEC or CL4's conductor; anything else changes
what is BELOW the slab rather than what it is made of, and D2's geometry does not describe one.

### 1. The one real risk the brief named — and it landed on the ALGEBRAIC route

`Γ^q = Γ^e − (k₀²/k_ρ²)(Γ^e − Γ^h)`, and the shipped kernel meets that `k_ρ²` with an algebraically
cancelled identity so nothing is ever divided by a small number — *"writing it the obvious way is
fine at k_ρ ~ k₀ and quietly loses every digit as k_ρ → 0, which is precisely where the DCIM
sampling path starts."* That identity is derived from the shorted-slab algebra and does not survive
`Z_s ≠ 0`. The brief named `LayeredSpectralGreens.ReflectionTaylor`'s numerical contour extraction as
an acceptable fallback. **It was not needed.**

`Γ^e − Γ^h = 2(BD − AC)/[(B+C)(A+D)]` for the four quantities above, and `BD − AC` carries an exact
factor of k_ρ² through **one identity applied term by term**: `k_z1² − ε*k_z0² = (ε*−1)k_ρ²`. Four
terms, four applications, and the T² term needs the same identity twice
(`k_z1⁴ − ε*k₁²k_z0² = k_ρ²[(ε*−1)k_z1² − ε*k_z0²]`):

    (Γ^e − Γ^h)/k_ρ² = 2P/[(B+C)(A+D)]
    P = ε*ζ + j(ε*−1)T + j(ε*−1) ε*k₀² ζ² T − ζT²[ (ε*−1)k_z1² − ε*k_z0² ]

At ζ = 0 this is `P = j(ε*−1)T` and the whole expression is the shipped identity verbatim.

**Measured against the general kernel's own INDEPENDENT route** (numerical Taylor coefficients of the
same difference, taken by Cauchy integral off a small contour) at 10 GHz with a real floor, down
through the decade the DCIM path starts in — and with the naive division computed beside it, so the
gate cannot quietly stop demonstrating why any of this is necessary:

| k_ρ/k₀ | factored Γ^q, rel vs the general kernel | the NAIVE route's error |
|---|---|---|
| 1e-1 | 2.288e-15 | 1.935e-14 |
| 1e-3 | 1.437e-15 | 1.123e-10 |
| 1e-5 | 1.601e-15 | 1.987e-06 |
| 1e-7 | 1.470e-15 | 2.253e-02 |
| 1e-8 | 1.527e-15 | **1.457** |

The factored form is flat at ~1e-15 across eight decades while the naive one loses everything, and
`Γ^q(0)` and `Γ^q(1e-9 k₀)` come out **bit-identical**. GaAs is the same picture (worst factored
1.230e-14, worst naive 2.228).

**The ZERO's existence was never the question and the brief said so** — at k_ρ = 0 the two equivalent
lines ARE the same line whatever the floor is, because `Z_1^e = Z_1^h = √(µ/ε)` and `Z_s` is one
scalar for both. What had to be re-derived was a form that REALISES it in floating point, and the
table above is that form working.

### 2. R-cl6-1 — the PEC reduction, and why it is asked TWICE

**0 bits moved, out of every comparison made**, and the two halves answer different questions.

- **Against the pre-CL6 ARITHMETIC — 0/960 bits.** `OneSlabLossyFloorTests.PreCl6Kernel` is the file
  as it stood before CL6, transcribed verbatim including the association order of every product,
  because the claim is bit-identity and not closeness. Both starters × 2/10/20 GHz × 16 values of
  k_ρ/k₀ from 0 to 300, on Γ^h, Γ^e, Γ^q **and both dispersion residuals**. This is what says the
  ζ = 0 arithmetic IS the shipped arithmetic. It is written out in the test rather than reached for
  through the shipped class on purpose: a gate that asks the new code whether it agrees with itself
  is vacuous.
- **Against a PERFECT floor spelled as a conductor — 0/20 bits, eighteen times.** Both starters ×
  2/10/20 GHz × all three of `PlanarSurfaceImpedance.IsPerfect`'s spellings (σ = +∞, σ = 0, t = 0),
  through the **fitted product** at five ρ/λ each — so the poles, the asymptotic extraction and the
  image fit are all inside the comparison. This is §CL4 §1's own stronger claim: the new path is
  TAKEN and reproduces the old bits.

**Neither collapses a perfect floor onto the old code path to get there**, which the brief forbids and
§CL4 §1 refused for the same reason. The zero is structural: `Termination.SurfaceImpedanceAt` returns
exactly `Complex.Zero` on the perfect spellings, so every ζ-term below is a complex **zero added to
the shipped expression** rather than a different expression. The one place a guard was kept is
`IsDoublyDegenerate` (εᵣ = 1 observed exactly at k_ρ = k₀), and it is now conditioned on the floor
being perfect — with a real `Z_s` the general form reaches that point without a 0/0 and gives the
physically right pair (`Γ^h = −1`, `Γ^e = +1`) where the old unconditional guard gave −1 for both.

### 3. R-cl6-2 — D5's one-layer reduction, now at every σ

**The strongest oracle in the brief, and it cost nothing to build because CL4 built the other half.**
`GroundedSlab` with `Termination.LossyGround(σ, t)` against `LayerStack.FromGroundedSlab` of it —
the same floor on both sides, one solved by a closed form and one by a chain matrix.

| starter | floor | worst rel, k_ρ ≤ 40k₀ | worst anywhere (to 300 k₀) |
|---|---|---|---|
| FR-4 1.6 mm | PEC | 6.232e-14 | 8.862e-12 |
| FR-4 1.6 mm | **35 µm Cu** | **7.741e-14** | 8.740e-12 |
| GaAs 100 µm | PEC | 7.131e-14 | 1.265e-12 |
| GaAs 100 µm | **3 µm Au** | **6.871e-14** | 1.138e-12 |

D5's own un-widened tolerances (1e-13 inside 40k₀, 2e-11 past it, where both kernels are computing
the same exactly-Fresnel limit by two underflowing routes). **A real floor does not degrade the
reduction at all** — on GaAs it is marginally tighter than the PEC row — which is the direct
statement that the two derivations agree about the termination and not merely about the slab.

### 4. Measurement 3 — the DCIM fit stays inside the UN-WIDENED one-slab tier

**This is the whole point of the brief: the tier is what is being bought.** Error against direct
Sommerfeld integration of the SAME kernel, worst value inside `Dcim.ValidatedRhoOverLambda`, scaled
on the free-space kernel (|ΔG|·4πρ, what a MoM fill experiences). **Ceiling 6e-3, and nothing was
widened.**

| stack | f | G_q PEC → LOSSY | G_A PEC → LOSSY |
|---|---|---|---|
| FR-4 1.6 mm, 35 µm Cu | 2 GHz | 1.749e-5 → 2.647e-5 | 1.058e-6 → 1.059e-6 |
| | 10 GHz | 4.002e-5 → **3.984e-5** | 1.071e-6 → 1.083e-6 |
| | 20 GHz | 2.834e-4 → 4.676e-4 | 1.363e-6 → 1.363e-6 |
| GaAs 100 µm, 3 µm Au | 2 GHz | **5.693e-3 → 5.748e-3** | 2.185e-7 → **2.080e-7** |
| | 10 GHz | 1.509e-5 → 1.545e-5 | 1.352e-6 → 1.363e-6 |
| | 20 GHz | 6.340e-5 → **6.105e-5** | 1.777e-6 → **1.764e-6** |

**The tightest case in the table is GaAs at 2 GHz and it is NOT the floor's doing.** G_q is already at
5.693e-3 against the 6e-3 ceiling with a PEC floor — 5% of headroom, on the shipped kernel, before
this brief touched anything — and a real plane moves it by **+1.0%** to 5.748e-3. That cell is worth
knowing about on its own account: the one-slab tier's ≤6e-3 is a statement about the worst cell in
the span, and this is that cell. The floor did not create it and does not meaningfully move it.

**CL4's "better on four of six" does NOT reproduce here and is not claimed.** Across the twelve
kernel-cases the lossy fit is better in four and worse in eight, all of them by a few percent and all
of them far inside the ceiling; the worst it ever gets is 4.676e-4, thirteen times inside. The
far-field sum-rule residual stays at 1e-16 … 1e-22 exactly as before, and the image counts and fit
residuals are unchanged to within one image.

### 5. Measurement 4 — where the one-slab poles go, and the seam that answer introduces

`SpectralGreens.FindSurfaceWaveModes` bisects the SHORTED slab's own closed-form transcendental
equations, which stop being the dispersion relation the moment the short becomes a load. **A
conducting floor delegates to CL4's `SurfaceWavePoles` over the `LayerStack` the slab now maps to**,
rather than growing a second search — `PlanarMetrics` reads that search, and two searches disagreeing
about a pole would be a metric refusing on one path and not the other.

| stack | f | mode | PEC | with a real plane | Δ |
|---|---|---|---|---|---|
| FR-4 1.6 mm, 35 µm Cu | 1 GHz | TM₀ | 3.9785e-6 | 4.5521e-6 | 5.7e-7 |
| | 10 GHz | TM₀ | 7.6918e-4 | 7.9576e-4 | 2.7e-5 |
| | 20 GHz | TM₀ | 8.4007e-3 | 8.5408e-3 | 1.4e-4 |
| | 40 GHz | TM₀ | 1.2132e-2 | 1.2251e-2 | 1.2e-4 |
| | 40 GHz | **TE₁** | 1.5630e-2 | **1.5705e-2** | 7.5e-5 |
| GaAs 100 µm, 3 µm Au | 1 GHz | TM₀ | 6.2839e-10 | 5.0152e-8 | 5.0e-8 |
| | 10 GHz | TM₀ | 6.4456e-8 | 1.6764e-6 | 1.6e-6 |
| | 40 GHz | TM₀ | 1.4570e-6 | 1.5654e-5 | 1.4e-5 |

**The worst pole anywhere is 1.5705e-2 against `PlanarMetrics`' ceiling of 0.05** — the same number
§CL4 §4 reports, to every digit it quoted, which is the cross-check that the one-slab delegation
lands on CL4's own answers rather than near them. Mode COUNTS and NAMES agree between the two floors
at every frequency measured.

**A PERFECT floor deliberately keeps the shipped route, and the seam that leaves is measured rather
than waved at.** The two searches are not bit-identical, and DCIM subtracts each pole's residue in
closed form, so re-routing a PEC run would move G_A and G_q in the last digits and make R-cl6-1
unachievable for no physical gain. How far apart they actually are, on a PEC floor, both starters,
1/10/40 GHz: **worst 9.19e-18 relative**, and three of the seven poles agree exactly. That is one ulp
in an imaginary part, not a disagreement about physics.

**TE modes are re-indexed by +1 across the delegation.** `SurfaceWavePoles` numbers each polarisation
from 0 ascending in k_ρ; this kernel numbers TE_n from n = 1, because that is the n of its own cutoff
condition `U > (2n−1)π/2`. The names have to agree — they are what `Dcim` labels a pole term with.

### 6. The three things that had to NOT move, asserted rather than assumed

- **R-cl6-3 — the εᵣ = 1 image reduction.** Free space plus one NEGATIVE image, three slab heights ×
  six ρ/λ × both kernels at 10 GHz: **5.053e-10** worst relative / 7.072e-12 scaled at σ = +∞, and
  **1.168e-3 / 1.444e-4** with 35 µm copper. The second row is not a failure — it is the measured
  size of the imperfection, which is what a conducting floor is FOR: the image is no longer exactly
  −1 and the closed form no longer exactly applies. Its scaled figure reproduces §CL4 §2's
  **1.444e-4** exactly, on a different kernel and a different geometry sweep.
- **R-cl6-4 — the static routes are bit-identical at every σ. 0/28 bits moved** on both starters, at a
  REAL σ, over `StaticGreens.ScalarPotential`/`VectorPotential` and `PlanarKernelTerms.StaticScalar`/
  `StaticVector`'s own `Inverse` and `Constant`. It is structural: none of them reads a termination at
  all, and §CL4 §1 established that reading a conducting floor as a PEC there is the **ω → 0 limit
  rather than a simplification** (`Γ → −1` as ω → 0 with `Z_s → 1/(σt)` finite).
- **R-cl6-5 — `AsymptoticReflection` does not move.** 0 bits on both kernels, both starters, three
  frequencies. DCIM's first extraction is that constant and a silent move there would spoil every
  fit's decay without failing anything obvious. It is also still the LIMIT rather than merely an
  unchanged constant: on the conducting stack `|Γ(4000k₀) − Γ(∞)|` is 5.3e-8 (G_A, FR-4) and 7.3e-9
  (G_q), i.e. Γ walks into it from the lossy side too. The floor reaches the top only through
  `e^{−2k_ρh}`, which is why — and §CL4 §3's k_ρ → ∞ asymmetry between `Γ^e → −1` and `Γ^h → +1` sits
  four decades past anything reachable here, exactly as it does on the general path.
- **R-cl6-6 — `CanSolveAt`'s floor is unchanged.** `MinElectricalThickness` is about the static limit
  and the floor does not move it: the slab solves at k₀h = 1.01e-6 and is refused at 9.90e-7, with a
  PEC floor and with a conducting one alike. The two refusals ADDED (a non-ground termination, a NaN
  σ/t) are new names for cases that could not be spelled before, not a narrowing of an old one.

### 7. Traps and findings

- **`EmSnpProvenance` cannot see a ONE-SLAB floor, and this is §CL4 §8's own trap one kernel over.**
  The hash reads `p.MediumStack`'s terminations; a one-slab problem has `MediumStack == null` and
  contributes only `p.Slab`'s height, εᵣ, tanδ and µᵣ (`EmSnpProvenance.cs:188-191`). So two runs
  differing only in the GROUND's σ would share a cached `.snp`, silently and plausibly. **Not fixed
  here** — this brief's "Must NOT" reserves `src/Design`, and nothing shipped can build the
  termination until the extractor writes one. **It must be closed in CL7, before the extractor
  flips**, or the first user-visible conducting floor arrives with a stale-cache defect attached.
- **The pole search could NOT be delegated unconditionally**, and the reason is the gate rather than
  the physics: at 9.19e-18 the two searches agree to about one ulp, and DCIM subtracts each pole in
  closed form, so a PEC run would have moved in its last digits. A brief whose headline gate is
  bit-identity cannot afford a one-ulp improvement anywhere.
- **`DispersionResidual` was extended rather than left describing the shorted slab.** It is public and
  is what `LayeredGreensFunctionTests` checks a pole against; leaving it shorted would have made it
  quietly wrong for a conducting floor. The floor's terms are appended to the shipped expression, so
  a PEC slab's residual is bit-identical (it is in R-cl6-1's 0/960), and `sin(k_z1h)/k_z1` is written
  as `h·sinc(u)` so k_ρ = k₁ stays a non-event on R-lyr-3's own terms.
- **`GroundedSlab.Floor` is an init-only property, not a third positional parameter.** Every existing
  two-argument construction is unchanged, a floor is spelled `slab with { Floor = … }`, and record
  equality carries it — which is what keeps a cache key or a hash from confusing two slabs that
  differ only in their ground metal. A positional default was not available anyway: `Termination.Pec`
  is not a compile-time constant.
- **Measurement 3 is in the ROUTINE tier and that is a measurement, not a preference.** The full
  12-case sweep — 24 fits and 408 direct Sommerfeld evaluations — is **3.96 s**, under the repo's
  mechanical ~5 s `Category=Benchmark` threshold. CL4's counterpart was 5.0 s and was tagged; this one
  is cheaper because the one-slab reflection coefficient is a closed form rather than a cascade. So
  there is deliberately **no cheap counterpart beside it**: the expensive half IS what runs on every
  build, which is the outcome §CL4 §10's "a gate nobody runs is not a gate" was settling for.

### 8. What this still does not reach

**No run a user can make gets a conducting ground plane yet, and that is unchanged from §CL4 §9 by
design.** `PlanarExtractor.BuildMediumStack` still writes `Termination.Pec` and the extractor was not
touched; the 21.1% (FR-4) / ~11% (MMIC) / 25.0% (low-loss laminate) figures still stand for every run
anyone can make. **What CL6 removes is the obstruction, not the limit**: obstruction 1 was retired by
CL5, obstruction 2 — the 2.6× tier — is the one this brief exists to retire and the table in §4 is
its retirement, and obstruction 3, the CL3-sized re-bless, is real and is CL7's. A flip of that reach
carries its own passivity/reciprocity direction check, its own hero check and its own golden-move
tabulation, none of which this brief has.

### 9. Gates

`tests/Engine.Tests/Mom/OneSlabLossyFloorTests.cs`, **12 tests, ~8 s together, none tagged**
`Category=Benchmark` (the two largest are measurement 3 at 3.87 s and the perfect-spelling reduction
at 3.64 s; every other one is milliseconds). Measured, then left untagged on the repo's mechanical
~5 s rule rather than on a preference.

**R-cl6-7** — the four measurements tabulated whether they pass or refuse: §1 (and §2), §3, §4, §5.
All four pass.
**R-cl6-8** — `dotnet test tests/Engine.Tests`, run ONCE and triaged from the TRX: **2,421 passed, 0
failed, 1 skipped** (the same pre-existing `DataSetExportTests` skip §CL4 §10 recorded), 1 m 41 s.
**Nothing outside this brief's own files moved**, which is what says R-cl6-1 is saying what it claims
— the extractor still writes a perfect floor, so a move anywhere else would have meant the reduction
was not exact.

### 10. Reported to the owner, by file and line (no `CLAUDE.md` edit, per the standing rule)

- `src/Engine/Mom/CLAUDE.md` **§5's validated-range table**, the `CL4's conducting floor` row, ends
  *"Deliberately not a refusal — nothing shipped can build the termination (§7)"*. Still true, but the
  row now needs the one-slab half beside it: **the conducting floor is available on the one-slab
  kernel at `ValidatedRhoOverLambda`'s own ≤ 6e-3, measured, un-widened** (§4 above) — which is the
  whole content of CL6 and is what makes CL7 a decision about the re-bless alone rather than about a
  2.6× tier.
- `src/Engine/Mom/CLAUDE.md` **§5's same table** could also carry the tightest cell honestly: G_q on
  100 µm GaAs at 2 GHz is at **5.693e-3 of the 6e-3 ceiling with a PEC floor** and 5.748e-3 with a
  real one. That is the cell the ≤6e-3 figure is a statement about, and it pre-dates this brief.
- `src/Engine/Mom/CLAUDE.md` **§7's refusal list** needs one line: `GroundedSlab` now refuses a floor
  that is not a GROUND — a PMC or an open half-space — by name, pointing at `LayeredSpectralGreens`.
  It is a new name for a case that could not be spelled before, not a narrowing.
- **The repo `CLAUDE.md`'s test-suite section counts 128 `Category=Benchmark` methods repo-wide.
  CL6 adds NONE** (§7's last bullet), so that count is unchanged at 128.

## CL5 — the lossless-stack refusal belongs to the integrator, not to the fit (2026-09-14)

`docs/sonnet-briefs/brief-conductor-loss-5-lossless-predicate.md`. `SommerfeldIntegral.CanIntegrateLayered`
refused a guided stack with no loss in it, and that refusal reached the product through
`Dcim.FitAtHeights`, which borrowed `CanIntegrateInterior` as a precondition. **`FitAtHeights`
integrates nothing.** Two separable defects, both closed, and the second of them is why this brief is
in the conductor-loss series rather than in a tidy-up pile.

**It closes a LIVE defect, not a future one.** A two-level design on a tanδ = 0 substrate refused
today, citing an integrator its run never reaches — and the stackup editor's own readiness message
(`src/Design/Layout/StackupFieldReadiness.cs:82`) reads *"Use 0 for a lossless dielectric"*, so the
value the refusal fired on is the one the application invites. `TechModel.TanD` is a bare `double`
with no initialiser, so it is also the DEFAULT.

### 0. The two predicates, and which question each now asks

| | asks | called by |
|---|---|---|
| `SommerfeldIntegral.CanIntegrateLayered(LayerStack)` | open top, **and the CONTOUR's lossless-guided restriction** | `EvaluateLayered`, and `CanIntegrateInterior` |
| `SommerfeldIntegral.CanFitAtHeightsStructurally(g, z, z′)` | open top, and neither point inside a solid wall | **`Dcim.FitAtHeights`** |
| `SommerfeldIntegral.CanIntegrateInterior(g, z, z′)` | the UNION of the two | `EvaluateInterior` |

`CanIntegrateInterior` is now the union rather than the source of either, so the direct integrator
keeps exactly the refusals it had and the fit keeps only the ones it needs. The open-top check is
written once (`TopIsOpenHalfSpace`) and read from both, message unchanged.

**The one-slab `CanIntegrate(SpectralGreens)` needed no equivalent change and was checked rather than
assumed:** nothing borrows it. `Dcim.Fit(SpectralGreens, …)` never called it, and its only caller
outside this file is `tests/Engine.Tests/Mom/Support/SommerfeldRadialTable.cs:45` — the oracle table,
which is the direct integrator's own consumer.

### 1. The loss test is the STACK's now, and the old one could not see a conducting floor

`LayerStack.Dissipates` — any layer with tanδ > 0, or either `Termination.Dissipates`. The old test
was `stack.Layers.Any(l => l.Material.TanD > 0)`, and **a `Termination` is not a `MediumLayer`**, so a
CL4 conducting floor — the one thing in this series built specifically to dissipate — was invisible
to it.

`Termination.Dissipates` is **not `IsConductor` under another name**, and the pair is worth keeping
straight: `IsConductor` asks whether the end is opaque and an equipotential (a PEC answers yes, and
CL4's four narrowed call sites depend on that); `Dissipates` asks whether it takes power out of the
fields (a PEC answers no). The perfect-spelling half is read from
`PlanarSurfaceImpedance.IsPerfect(σ, t)` — a new overload of the private three-spelling test, which
`Sheet` and `Plane` now open with too — so **"does not dissipate" and "returns exactly
`Complex.Zero`" cannot drift apart.** A `HalfSpace` into a lossy medium counts as well, which is what
makes `LayerStacks.FilmOnSilicon` dissipate on its bottom termination.

### 2. R-cl5-1 — nothing that runs today moves, measured against HEAD rather than argued

A worktree at `e13f1b70`, the same dump test in both trees, diffed: **234 configurations, 2,070
doubles at full round-trip (`"R"`) precision, zero differences and zero refusals on either side.**
Six `LayerStacks` fixtures × 2/10/20 GHz × three kernels × three height pairings, through
`Dcim.FitAtHeights`, `Dcim.Fit` and `SommerfeldIntegral.EvaluateLayered`.

The zero is structural — a predicate was re-pointed and no arithmetic was touched — but it is the
kind of claim that is cheap to assert and cheap to be wrong about, and a worktree at HEAD costs one
build. **`git stash` was not used**; the working tree was never disturbed.

What is asserted permanently instead is the thing a future edit could break:
`R_cl5_1_TheSplitPredicateAgreesWithTheUnion_WhereverTheStackDissipates` — the narrowed predicate is
a SUBSET of the union, the two give the same verdict on every stack that dissipates, and the only
place they part company is a guided stack that does not.

### 3. R-cl5-2 — the ε-ladder, and the column that decided milestone 4

Both starters made lossless, both kernels, 10 GHz, `FitAtHeights(h, h)`, worst over
ρ/λ ∈ {1e-4, 1e-3, 1e-2, 0.1, 1.0} — the whole of `ValidatedRhoOverLambdaLayered` — scaled on the
free-space kernel. **The 1.6e-2 ceiling is CL4 §3's own and nothing was widened.** `vs-direct` is the
fit against `SommerfeldIntegral.EvaluateLayered`, an INDEPENDENT reference; `vs-tanδ=0` is each rung
against the lossless fit, which is the ladder's own continuity statement.

| stack / kernel | tanδ | pole \|Im\|/Re | direct | fit residual | vs-direct | vs-tanδ=0 |
|---|---|---|---|---|---|---|
| FR-4 `G_q` | 1e-3  | 3.846e-5  | yes | 3.831e-5 | 4.197e-5 | 3.503e-4 |
|            | 1e-6  | 3.846e-8  | yes | 3.892e-5 | 4.209e-5 | 7.078e-7 |
|            | 1e-9  | 3.846e-11 | yes | 3.945e-5 | 4.227e-5 | 4.210e-7 |
|            | 1e-12 | 3.846e-14 | yes | 3.814e-5 | 4.186e-5 | 6.893e-7 |
|            | 1e-16 | 3.846e-18 | yes | 3.861e-5 | **2.320e-1** | 3.903e-7 |
|            | **0** | 0         | **NO** | 3.871e-5 | — | 0 |
| FR-4 `G_A` | 1e-3  | 3.846e-5  | yes | 3.200e-6 | 8.828e-7 | 1.028e-4 |
|            | 1e-6  | 3.846e-8  | yes | 3.157e-6 | 8.458e-7 | 1.086e-7 |
|            | 1e-9  | 3.846e-11 | yes | 3.164e-6 | 8.853e-7 | 4.209e-8 |
|            | 1e-12 | 3.846e-14 | yes | 3.106e-6 | 8.530e-7 | 1.364e-8 |
|            | 1e-16 | 3.846e-18 | yes | 3.105e-6 | 8.463e-7 | 3.124e-9 |
|            | **0** | 0         | **NO** | 3.103e-6 | — | 0 |
| GaAs `G_q` | 1e-3  | 3.223e-8  | yes | 6.518e-6 | 1.483e-5 | 1.309e-4 |
|            | 1e-6  | 3.223e-11 | yes | 6.563e-6 | 1.494e-5 | 9.009e-7 |
|            | 1e-9  | 3.223e-14 | yes | 6.107e-6 | 1.603e-5 | 3.042e-7 |
|            | 1e-12 | 3.223e-17 | yes | 6.061e-6 | **6.946e-5** | 1.038e-7 |
|            | 1e-16 | 3.223e-21 | yes | 6.146e-6 | **1.069e-4** | 3.444e-7 |
|            | **0** | 0         | **NO** | 6.113e-6 | — | 0 |
| GaAs `G_A` | 1e-3  | 3.223e-8  | yes | 1.609e-7 | 1.324e-6 | 4.546e-7 |
|            | 1e-6  | 3.223e-11 | yes | 1.609e-7 | 1.324e-6 | 5.377e-10 |
|            | 1e-9  | 3.223e-14 | yes | 1.609e-7 | 1.323e-6 | 7.257e-10 |
|            | 1e-12 | 3.223e-17 | yes | 1.609e-7 | 1.324e-6 | 1.902e-10 |
|            | 1e-16 | 3.223e-21 | yes | 1.609e-7 | 1.324e-6 | 2.158e-10 |
|            | **0** | 0         | **NO** | 1.609e-7 | — | 0 |

**Three readings, and they are the whole of the brief's case.**

1. **The FIT does not notice.** Its own spectral residual is flat across twelve decades of tanδ —
   3.83e-5 → 3.87e-5 on FR-4 `G_q`, and 1.609e-7 UNCHANGED TO FOUR FIGURES on GaAs `G_A`. There is
   nothing in the DCIM path that degrades as the loss goes to zero.
2. **The ladder CONVERGES and then floors.** `vs-tanδ=0` falls by 2-5 decades from tanδ = 1e-3 to
   1e-6 and then sits at a few times 1e-7 (FR-4 `G_q`) or 1e-9 (FR-4 `G_A`) — **that floor is the
   fit's own reproducibility, not the physics**: below tanδ ≈ 1e-6 the two kernels differ by less
   than two Prony fits of the same kernel do. The gate asserts monotone convergence only where the
   column is still signal, and bounds the floor below that; asserting strict monotonicity all the way
   down fails, and it fails on noise.
3. **THE DIRECT INTEGRATOR IS THE ONE THAT DEGRADES, AND IT IS STILL ADMITTED WHILE DOING IT.** On
   FR-4 `G_q` at tanδ = 1e-16 the fit is 2.320e-1 from the direct integrator — **5,500× worse than
   the same comparison at 1e-3, and 14× PAST the 1.6e-2 ceiling** — with the fit itself unchanged, so
   it is the integrator that moved. GaAs `G_q` shows the same thing more gently (1.48e-5 → 1.07e-4).
   The contour is running into the pole, exactly as its refusal says; **the refusal simply fires a
   binary decade or four too late**, because `tanδ > 0` is a proxy for a continuous condition. That
   is milestone 4's evidence and it is why the answer there is NO — see §6.

   **Consequence for anyone writing a test:** `SommerfeldIntegral.EvaluateLayered` is not a
   trustworthy oracle on a nearly-lossless guided stack even though it accepts one. The ladder's
   assertion against it is therefore made only for tanδ ≥ 1e-9; below that the column is REPORTED and
   the continuity column is what carries the rung.

### 4. R-cl5-3 — the live defect closes, and this is the only gate driven through `PlanarSolve`

A two-level MMIC shape with a real through path: a 120 µm line on M1 with a 36 × 40 µm overlay pad on
M2, 3 µm above it — an overlap capacitor, which is the ordinary reason a design has two levels at
all. Every dielectric at tanδ, N = 118, 10 GHz, de-embedded through `PlanarSolve.Run`.

| tanδ | direct integrator | Z_c | S₁₁ | S₂₁ |
|---|---|---|---|---|
| **0** | **REFUSED** | 64.10 − 0.36j | 0.2853 + 0.4321j | 0.7135 − 0.4764j |
| 1e-16 | admitted | 64.10 − 0.36j | 0.2853 + 0.4321j | 0.7135 − 0.4764j |
| 2e-3 (the real GaAs figure) | admitted | 64.10 − 0.42j | 0.2859 + 0.4329j | 0.7129 − 0.4772j |

**Before CL5 the first row did not produce a wrong number — it THREW**, out of `Dcim.FitAtHeights`,
with the direct integrator's sentence. Worst |ΔS| between tanδ = 0 and 1e-16 is **3.57e-6**, against
**9.93e-4** between tanδ = 0 and the substrate's own loss — the ε agreement is **278× tighter than
the only scale on this problem that means anything**, which is how the gate states its tolerance
rather than picking a constant. The first row still reports `CanIntegrateLayered` = NO, asserted, so
the gate cannot pass for the wrong reason.

### 5. R-cl5-4 — the refusal still refuses, and the MESSAGE is asserted

`EvaluateLayered` and `EvaluateInterior` still throw on a lossless guided stack, and the test asserts
the REASON and not only the verdict: it must contain `real-k_ρ contour` and `principal value`, and it
must name `CanFitAtHeightsStructurally` as the thing that asks the narrower question. **This refusal
has been wrong in its attribution once** — its old sentence said *"Dcim has no such restriction"*,
which was true and was exactly what carried it into `Dcim.FitAtHeights` unread — and asserting the
sentence is what stops that recurring.

The message also stopped saying *"needs at least one LOSSY LAYER"*, which was the blind half: it now
names the three ways a stack can dissipate, including a conducting termination.

The two structural refusals it also carries are asserted to still fire, so this is a narrowing and
not a deletion: a solid wall on top, and a source inside a wall.

### 6. Milestone 4 — the contour indentation: NO, and the ladder is what makes that a decision

The standard remedy for a pole on the contour is an arbitrarily small indentation around it with the
residue added in closed form, and it would make the direct integrator exact at tanδ = 0 rather than
only in the limit. **It is declined, and recorded here because a refusal whose remedy is never
written down gets re-discovered.** Three reasons, in order of weight:

1. **Nothing needs it.** The direct integrator exists to CHECK the DCIM path, and at tanδ = 1e-3 it
   is healthy and is already an independent reference for a stack whose kernel is indistinguishable
   from the lossless one. The ladder's own continuity carries the rest.
2. **It would fix the measure-zero point and not the band that actually bites.** The pole-on-contour
   problem is CONTINUOUS and `tanδ > 0` is a binary proxy for it: on FR-4 `G_q` the integrator is
   formally admitted at tanδ = 1e-16 and is **5,500× worse there than at 1e-3, and 14× past the
   1.6e-2 ceiling** (§3's `vs-direct` column). An indentation at exactly zero leaves every
   tiny-but-nonzero tanδ admitted and silently degrading, which is the band a caller is actually
   likely to be in.
3. **The change that WOULD address (2) is a different one and is out of this brief's scope** — a
   pole-proximity refusal on `CanIntegrateLayered` (refuse when |Im k_ρ|/Re k_ρ is below a measured
   floor, rather than when tanδ is exactly zero). That WIDENS a refusal, would move verdicts
   R-cl5-1 pins as unchanged, and needs its own measurement of where the floor is. **Named, not
   built.**

### 7. THE CORRECTION TO §CL4 §9's OBSTRUCTION 1 — read this before sizing CL6 or CL7

§CL4 §9 weighed three obstructions to turning the conducting floor on in `PlanarExtractor`. **The
first of them is gone, and its example was never a casualty.** The inline correction block in §9 said
so when this brief was scoped; it is now MEASURED and shipped, and CL6/CL7 should be sized against
this paragraph rather than against §9 as originally written:

- **`CoplanarDeembedTests` was never refused.** Its fixtures are `EmMaterial(1.0, 0.0)`
  (`CoplanarDeembedTests.cs:45`, `:337`, `:476`) — at εᵣ = 1 there is no guided mode, so `guided` is
  false and `CanIntegrateLayered` admitted them before this brief and after it. The case that
  actually refused is **εᵣ > 1 with tanδ = 0**, which §9 did not name.
- **The refusal was the DIRECT integrator's** and the fit never shared it. §CL5 §0.
- **Obstructions 2 and 3 stand, unchanged.** The general path's accuracy tier really is ~2.6× looser,
  and the default flip really is a CL3-sized re-bless.

**One naming slip in obstruction 2, and it does not weaken it.** §9 writes *"`ValidatedRhoOverLambdaLayered`'s
≤1.6e-2 against `ValidatedRhoOverLambda`'s ≤6e-3"*. Those two constants are **both 1.0** — they are
the validated ρ/λ RANGE. 1.6e-2 and 6e-3 are the measured ERROR ceilings inside those ranges
(`Dcim.cs:246-284` and `:140-169`), and the 2.6× gap between them is real and is the substance of the
obstruction. CL5's own brief repeats the same conflation. **Nothing needs re-deciding; a reader
should just not go looking for two constants that differ.**

### 8. What this does NOT reach

**No run a user can make still gets a conducting ground plane** — `PlanarExtractor.BuildMediumStack`
still writes `Termination.Pec`, exactly as §CL4 §9 says, and this brief did not touch it. What
changed is that one of the three reasons not to is no longer there.

**The validated ranges were not widened.** `ValidatedRhoOverLambdaLayered`,
`ValidatedRhoOverLambdaAtHeights` and `ValidatedRhoOverLambdaInteriorHorizontal` are untouched, and
so is the 1.6e-2 ceiling every measurement above is taken against.

**No loss was added anywhere a run can see.** The ε-ladder is an ORACLE construction, built inside
the test by rewriting a fixture's tanδ; nothing in `src/` displaces a pole to make a contour work.

### 9. Gates

`tests/Engine.Tests/Mom/LosslessStackPredicateTests.cs`, **7 tests. 5 in the routine tier at 1.2 s
together**; 2 tagged `Category=Benchmark` — `R_cl5_2_TheEpsilonLadder` at **3 m 39 s** (two starters ×
two kernels × six rungs, with direct Sommerfeld integration at five ρ/λ each) and
`R_cl5_3_ATwoLevelDesignOnAnIdealSubstrateSolves` at **7.9 s** (three de-embedded two-level solves).
Measured, then tagged on the repo's mechanical ~5 s rule.

**Both expensive measurements keep a routine-tier counterpart, on CL4 §10's terms**, because the
cheap half is the one that catches a refusal someone re-introduced rather than measuring how good the
answer is: `R_cl5_2b` re-asks the ladder on one stack, one kernel and the near field with no direct
integration at all (0.83 s), and `R_cl5_3b` builds the CROSS-REGION fit a two-level ideal stack needs
— the exact `Dcim.FitAtHeights` call the de-embedded solve used to die inside — in 0.31 s.

**R-cl5-4's assertion is on the MESSAGE.** A verdict-only assertion would survive the mistake this
brief exists to correct.

**R-cl5-6** — `dotnet test tests/Engine.Tests`, run ONCE and triaged from the TRX:
**2,409 passed, 0 failed, 1 skipped** (the same pre-existing `DataSetExportTests` skip CL4 recorded),
**1 m 40 s**. Nothing outside this brief's own files moved, which is what says the split predicate is
narrower and not different. *(CL4 §10 recorded 2,407 on this same HEAD and this brief adds 5 routine
tests; 2,404 + 5 = 2,409, so one of the two counts is off by three. Not chased — the TRX names every
outcome and there are no failures in either.)*

### 10. Reported to the owner, by file and line (no `CLAUDE.md` edit, per the standing rule)

- `src/Engine/Mom/CLAUDE.md` **§7's oracle-trap list, line 1194**: *"The Sommerfeld oracle requires a
  **lossy** slab (tanδ > 0): a lossless one puts TM₀ exactly on the contour. `Dcim` has no such
  restriction."* **Still true and now narrower than it reads** — it is the DIRECT integrator's only,
  the loss may come from a termination rather than a layer, and §3's last column says the trap is
  worse than binary: at tanδ = 1e-16 the oracle is ADMITTED and 14× past the ceiling. A line that
  said so would have saved this brief.
- `src/Engine/Mom/CLAUDE.md` **§5, line 1085**, the conductor-loss bullet: *"a conducting floor is
  expressible only through `MediumStack`, which forces the GENERAL kernel — and that kernel refuses a
  lossless dielectric outright"*. **The second clause is now false.** The obstruction it names is the
  one CL5 removed; the bullet's conclusion (not wired to `PlanarExtractor`) is unchanged and rests on
  §CL4 §9's obstructions 2 and 3.
- `docs/user/src/reference/mom-engine.md` **lines 174-176**: *"the solver's general layered path,
  which is a looser accuracy tier and which **refuses a lossless substrate outright** … and would
  break runs on an air or zero-tanδ substrate that work now."* **Both halves of that are now wrong** —
  an air substrate was never refused (no guided mode) and a zero-tanδ one no longer is. The
  paragraph's conclusion still holds on the looser-tier argument alone. **Not edited here**, because
  the `.md` has an `.html` beside it and `docs/user` is already 78 files out of step with its own
  generator (§CL4 §11); it is one sentence and it is the owner's call whether it rides with CL6.
- The repo `CLAUDE.md`'s test-suite section counts **128 `Category=Benchmark` methods repo-wide**;
  this brief adds 2, so that count is **130**.
- **`ValidatedRhoOverLambdaLayered` and `ValidatedRhoOverLambda` are both 1.0**, and three places
  quote them as 1.6e-2 and 6e-3 — which are the measured ERROR CEILINGS inside those ranges, not the
  constants. §CL4 §9's obstruction 2, `brief-conductor-loss-5-lossless-predicate.md`'s "Must NOT",
  and CL4 §3's own prose. The 2.6× gap is real and nothing needs re-deciding; the names do not match
  the numbers.

## CL4 — the lossy ground plane, as an impedance termination (2026-09-14)

`docs/sonnet-briefs/brief-conductor-loss-4-lossy-ground.md`. CL1 put a surface impedance on the
signal metal and CL3 turned it on; the laterally infinite plane the Green's function terminates on
stayed a PEC, worth **21.1% of the conductor term on FR-4, ~11% on the MMIC starter and 25.0% on a
low-loss laminate** (series overview §2). This brief makes that plane a real conductor.

**The brief was allowed to fail and it did not: all four of its ordered measurements pass, and the
physics lands.** What it does NOT do is reach a user, and §9 is the measured reason — that is the
whole of what is partial here and it is stated rather than implied.

### 0. The change, which really is small

`TerminationKind.SurfaceImpedance`, built by `Termination.LossyGround(σ, t)`, with

    Γ_bottom^{e,h} = (Z_s − Z_line^{e,h}) / (Z_s + Z_line^{e,h}) ,
    Z_line^e = k_z/(ωε) ,  Z_line^h = ωµ/k_z  of the region next to the wall,
    Z_s = η_c·coth(γ_c t)                                                   (PlanarSurfaceImpedance.Plane)

at every spectral component `k_ρ`. **The plane is still laterally infinite, still unmeshed, still
adds no unknown, and R17's ceiling does not move.** `LayeredSpectralGreens.WallReflection` is the one
place it is written and it serves the floor and the ceiling alike; `SurfaceWavePoles` takes it as a
LOAD on the same equivalent line (`V = −Z_s·I` at a floor, `+Z_s·I` at a ceiling).

**Z_s is the ONE-SIDED form and that is the factor of two the brief named.** A strip is excited on
both faces, so CL1.1 halves both η_c and the coth's argument; the plane has air below it and carries
its return current on its upper face alone, so neither halving applies. Measured, rather than
asserted: `Re(Z_plane)·σδ → 1.000000` in the thick limit — which is exactly kernel A's own
`R_s = 1/(σδ)` — and `Z_plane/Z_sheet → 2.000` there and `→ 1.000` in the thin limit, where both walk
into the same `1/(σt)`.

**Four predicates stopped asking "is it a PEC" and started asking "is it a CONDUCTOR"**
(`Termination.IsConductor`): the ground-attachment refusal (§5), the far field's θ range (§6),
`PlanarPortCalibrator.DescribedByTheSlab`, and both static routes. A PMC and an open half-space are
still refused by every one of them.

### 1. R-cl4-1 — the PEC reduction, and the half of it that is about the calibration

**0 bits moved, out of every comparison made.** Both starters × 2/10/20 GHz × all three spellings of
a perfect conductor (σ = +∞, σ = 0, t = 0), against the CL3 kernel, on `G_q` and `G_A` at five ρ/λ
each. The zero is structural rather than lucky: `Termination.SurfaceImpedanceAt` returns exactly
`Complex.Zero` on `PlanarSurfaceImpedance.IsPerfect`'s own three spellings, and every consumer's
impedance branch then returns the PEC value EXACTLY instead of computing `(0 − Z)/(0 + Z)` — which is
−1 to the physics and **not to the last bit**, because complex division leaves an imaginary residue
of order 1e-17.

**The kind is deliberately NOT collapsed to `Pec` for a perfect spelling.** Collapsing would have
made this gate vacuous — it would be asserting that the old path is still the old path. The new path
IS taken and reproduces the old bits, which is the stronger statement and is CL3 §3's own lesson one
file over.

**And the quasi-static calibration path is bit-identical at every σ, asserted rather than assumed.**
`LayeredStaticGreens` and `InteriorStaticGreens` both read a conducting floor as a PEC, and that is
the ω → 0 LIMIT rather than a simplification: `Γ^e = (Z_sωε₁ − k_z1)/(Z_sωε₁ + k_z1) → −1` and
`Γ^h = (Z_sk_z1 − ωµ)/(Z_sk_z1 + ωµ) → −1` as ω → 0 with `Z_s → 1/(σt)` finite. Measured at a REAL σ
on both starters: **0/20 bits moved.** `PlanarPortCalibrator.DescribedByTheSlab` had to move with
them — left asking `Kind == Pec` it would have pushed a lossy-ground run off the one-slab image
series onto the interior route and changed the reference impedance of a calibration that reads no
termination at all.

**This is also the statement of what the brief does not reach.** Below `QuasiStaticCrossoverHz`
(3.048 GHz on FR-4, 26.07 GHz on the MMIC starter) the supplied γ gains no ground term, exactly as
CL3 §0 found it gains no strip term — and CL3's answer there (the standard's own static CHARGE,
`R = Σ Re(Z_s)·[ΔS·Δℓ/|ΔT|²]`) is a sum over MESHED levels and the plane is not one. The residue is
this brief's own headline number, unchanged: 21.1% / ~11% / 25.0% of the conductor term.
**R-cl4-3 below is read off the two-line extraction directly and never touches that path**, which is
what CL1's own gates did and is why the crossover does not bite on the measurement.

### 2. R-cl4-2 — the εᵣ = 1 image reduction survives

Free space plus one NEGATIVE image, three height pairs × six ρ/λ × both kernels, at 10 GHz:

| floor | worst relative | worst scaled |
|---|---|---|
| σ = +∞ | 1.233e-10 | 7.275e-12 |
| 35 µm copper | 5.838e-4 | 1.444e-4 |

The first row is §3.1's strongest oracle, intact. **The second row is not a failure — it is the
measured size of the imperfection**, which is what a conducting floor is for: the image is no longer
exactly −1 and the closed form no longer exactly applies.

### 3. Measurement 1 — CAN DCIM STILL FIT IT? Yes, and the reason is structural

The brief's own question, and it said to measure rather than argue. Spectral error against direct
numerical Sommerfeld integration of the SAME kernel, worst value INSIDE
`Dcim.ValidatedRhoOverLambdaLayered`, scaled on the free-space kernel. **Ceiling 1.6e-2, and nothing
was widened.**

| stack | f | G_q PEC → LOSSY | G_A PEC → LOSSY |
|---|---|---|---|
| FR-4 1.6 mm, 35 µm Cu | 2 GHz | 2.744e-5 → **2.530e-5** | 8.281e-7 → 8.280e-7 |
| | 10 GHz | 2.055e-5 → **2.035e-5** | 1.916e-6 → 1.194e-6 |
| | 20 GHz | 1.390e-4 → **5.055e-4** | 9.706e-5 → 9.722e-5 |
| GaAs 100 µm, 3 µm Au | 2 GHz | 1.025e-2 → **4.262e-3** | 1.135e-8 → 1.126e-8 |
| | 10 GHz | 1.445e-5 → **1.563e-5** | 8.755e-7 → 8.773e-7 |
| | 20 GHz | 6.042e-5 → **1.865e-5** | 9.218e-7 → 9.342e-7 |

**The lossy fit is BETTER than the PEC one in four of the six cases** and the worst it ever gets is
5.06e-4 — thirty times inside the ceiling. `Dcim.CanFit` answers yes throughout (a surface impedance
is not a half-space, so the denser-bottom refusal is not reached and does not need to be), and the
far-field sum rule's residual stays at 1e-16 … 1e-21 exactly as before.

**Why it survives, which is worth having written down.** The worry was that Γ_bottom stops being a
constant and becomes a function of k_ρ. It does — but it stays EVEN in k_z1, the property the whole
basis rests on: under `k_z1 → −k_z1` an impedance load's `Γ_bot → 1/Γ_bot`, exactly as an ordinary
Fresnel coefficient does, and the composite is invariant. **A surface impedance adds no branch
point**, and that is the difference from an open-below stack: the SIBC replaces the region below by a
CONSTANT rather than by a medium with its own `k_zb`, which is precisely the term §7's refusal is
about.

**One genuine asymmetry, found in the derivation and documented where it lives.** The k_ρ → ∞ limits
part company: `Γ^e → −1` (the electrostatic image survives) but `Γ^h → +1`, because `Z_line^h → 0`
while Z_s stays finite. The crossover is at `k_ρ ≈ √2/δ` — 2.1e6 rad/m for copper at 10 GHz, four
decades past the DCIM path's own reach — and it is exactly where the Leontovich condition stops being
valid anyway (an exact conducting half-space gives `Γ^h → 0` there, a matched load). It is invisible
to the fitted top-referenced route because the bottom wall reaches the top only through `e^{−2k_ρH}`,
which at that k_ρ on 1.6 mm FR-4 is `e^{−4800}`; `AsymptoticTopReflection` does not read the bottom
termination at all, for the same reason. The one arm that DOES read it —
`AsymptoticAtHeights`, for a source in the region directly above the floor — carries both values
rather than one.

### 4. Measurement 2 — WHERE DID THE SURFACE-WAVE POLES GO? Barely anywhere

`PlanarMetrics` refuses `PowerSurfaceWave` (and `PowerDielectric` with it) past
`|Im k_ρ|/Re k_ρ = 0.05`. **Nothing comes near it, and the ground's own contribution is three to four
decades under the ceiling.**

| stack | f | mode | PEC | with a real plane | Δ |
|---|---|---|---|---|---|
| FR-4 1.6 mm, 35 µm Cu | 1 GHz | TM₀ | 3.9785e-6 | 4.5521e-6 | 5.7e-7 |
| | 2 GHz | TM₀ | 1.6277e-5 | 1.7918e-5 | 1.6e-6 |
| | 5 GHz | TM₀ | 1.1852e-4 | 1.2557e-4 | 7.0e-6 |
| | 10 GHz | TM₀ | 7.6918e-4 | 7.9576e-4 | 2.7e-5 |
| | 20 GHz | TM₀ | 8.4007e-3 | 8.5408e-3 | 1.4e-4 |
| | 40 GHz | TM₀ | 1.2132e-2 | 1.2251e-2 | 1.2e-4 |
| | 40 GHz | **TE₀** | 1.5630e-2 | **1.5705e-2** | 7.5e-5 |
| GaAs 100 µm, 3 µm Au | 1 GHz | TM₀ | 6.2839e-10 | 5.0152e-8 | 5.0e-8 |
| | 10 GHz | TM₀ | 6.4456e-8 | 1.6764e-6 | 1.6e-6 |
| | 40 GHz | TM₀ | 1.4570e-6 | 1.5654e-5 | 1.4e-5 |

**The worst pole anywhere is 1.5705e-2, against a ceiling of 0.05, and the plane moved it by
7.5e-5.** On FR-4 the pole's loss is the DIELECTRIC's and the ground adds 0.1–1.7% of it; on GaAs the
ground raises it by one to two ORDERS of magnitude, because tanδ = 0.002 left almost nothing there —
and it is still four decades under the ceiling. **No antenna metric starts refusing on an ordinary
board.** The scan itself is unchanged: `SurfaceWavePoles.Lossless` strips a conducting floor to a
PEC exactly as it strips tanδ, so Ψ stays real on the real axis and the mode is still a genuine sign
change; the loss is put back by the secant refinement.

### 5. Measurement 3 — the ground-attachment basis: RE-STATED, not deleted

`PlanarProblem.CanSolve` refused a `PlanarVia.GroundTerminal` unless the bottom termination was a
PEC **by name**, because the half rooftop's lower terminal is that plane. An impedance floor makes
the word "perfect" false in that sentence but not the sentence's reason: **what the basis needs is a
terminal that exists, conducts, and sinks the return charge**, and a Leontovich plane is all three.
Electrostatically it is an equipotential (§1), which is the limit the attachment's own charge
conservation lives in, so the basis is unchanged and nothing about it was re-derived. The refusal now
asks `Termination.IsConductor`, so a PMC and an open half-space are refused exactly as before.

Measured on the MMIC two-level fixture with an interior via AND a backside ground via in one mesh,
10 GHz, N = 440, 2 attachment bases:

| bottom termination | `CanSolve` |
|---|---|
| PEC | yes |
| conducting plane, gold 3 µm | **yes** |
| PMC | NO — named |
| open to air | NO — named |

- **Reciprocity is still STRUCTURAL on the lossy fill**: worst `|Z − Zᵀ|/|Z|` came back **exactly
  0.00e+00**, not small — the same guarantee L9c's shared vertical-current sign convention gives on a
  PEC floor, so the floor's Z_s enters the cascade without touching the symmetry argument.
- **The floor is not a local change and the numbers say so**: the attachment rows move by 1.02e-2
  relative and the rest of the matrix by up to 2.45, because the termination is in the GREEN'S
  FUNCTION and every entry is an interaction through it. A change that had moved only the attachment
  rows would have been the wrong change.
- **No lumped term was added at the attachment's ground end.** The plane's loss arrives through the
  termination; putting a spreading resistance on the basis as well would double-count it.

### 6. Measurement 4 — `FrontToBackDb` stays REFUSED, with its reason CORRECTED

The brief asked for an explicit decision because *"a refusal whose reason has become false is worse
than a missing metric"*. The old sentence ends *"the field below the plane is not small — it is
identically zero by construction — so the true F/B is infinite"*. **The last clause is now false: a
real plane leaks, so the true front-to-back of the structure a user drew is finite.**

**The decision is: the metric stays refused and gets a second sentence.** What is still true is that
this model has no lower half-space AT ALL. A Leontovich surface impedance is a boundary CONDITION —
it replaces everything below z = 0 with `E_tan = Z_s(n̂ × H_tan)` and computes no transmitted field —
so the leakage is not small in the model, it is ABSENT from it. Carrying it needs a two-sided plane
(transmission through a finite-thickness conductor into a stated medium below), which is a different
termination and a different θ range, not a tolerance.
`PlanarMetrics.FrontToBackLossyFloorPreamble` is that sentence and
`FrontToBackRefusalFor` picks between the two on the problem's own termination. ANT-11's narrowing —
the tail coming from `PlanarFiniteGround.CanCorrect` — is untouched and still follows either.

`PlanarFarField.CanComputeMedium` moved the other way and admits a conducting floor, for the same
reason read forwards: **the θ range rests on the floor being OPAQUE, not on it being perfect.** The
pattern over 0…90° is still the whole of what the model radiates. A PMC or an open half-space
genuinely radiates downward and is still refused, in the same words.

### 7. R-cl4-3 — against kernel A, both surfaces lossy

The gate the brief called *"the one that says the term is right rather than merely present"*.
Uniform 50 Ω line at 10 GHz on both starters, `EdgeCells = 3`, cells/λ = 20, γ from the **two-line
extraction directly** — never through `PlanarPortCalibrator`, so the QSC crossover is not in the path
(CL1's own gates did the same and this is why the crossover does not bite). Kernel A is
`RlgcModel.RMatrix` with the ground at the same σ and at σ = ∞, so its sum over surfaces splits the
two terms exactly.

**Four alphas, one mesh, one pair of standards, {PEC, real} strip × {PEC, real} ground.** A1 is the
all-PEC extraction floor; every term is measured from it. `PlanarFillSettings.PerfectConductor` makes
the STRIP perfect and nothing else — the plane is in the Green's function, not the fill — which is
what makes `B1 − A1` the ground term on its own.

| | α_strip | α_ground | α_total | share |
|---|---|---|---|---|
| **FR-4**, kernel A | 8.1943e-2 | 2.1895e-2 | 1.0384e-1 | **21.09%** (overview: 21.1%) |
| **FR-4**, kernel B | 5.2046e-2 | **3.2353e-2** | 8.4383e-2 | 38.34% |
| ratio B/A | 0.6351 | **1.4776** | 0.8126 | |
| **GaAs**, kernel A | 3.8256 | 4.5310e-1 | 4.2787 | **10.59%** (overview: ~11%) |
| **GaAs**, kernel B | 2.7977 | **4.7886e-1** | 3.2725 | 14.63% |
| ratio B/A | 0.7313 | **1.0569** | 0.7648 | |

Np/m. **Kernel A reproduces the overview's own 21.1% and ~11% to the digit it quoted**, which is what
says both sides are measuring the same quantity. **Kernel B's strip term reproduces CL1's converged
0.63 and 0.73** on the general path, which says the harness is the same instrument.

**The ground term is REAL and not an artifact of differencing two kernels.** Two independent checks:

- **It scales as 1/√σ.** A 4× resistivity step multiplied it by **2.0033** on FR-4 and 1.8687 on GaAs,
  against √4 = 2 — the structural signature of Re(Z_s), and the same test CL1's own routine gate uses.
- **It is ADDITIVE.** `(α_strip + α_ground)/α_total` = 1.0002 … 1.0021 across every case, so the two
  surfaces are not double-counting each other through the solved current.

**Where the two formulations actually agree, and where they stop — one substrate, three frequencies,
w held at the 10 GHz 50 Ω width so the geometry cannot move:**

| | k₀H | kernel A α_ground | kernel B α_ground | B/A |
|---|---|---|---|---|
| GaAs 100 µm @ 10 GHz | 0.0209 | 4.5310e-1 | 4.7886e-1 | **1.057** |
| FR-4 1.6 mm @ 2 GHz | 0.0671 | 9.7858e-3 | 1.0392e-2 | **1.062** |
| FR-4 1.6 mm @ 10 GHz | 0.3353 | 2.1895e-2 | 3.2353e-2 | 1.478 |
| FR-4 1.6 mm @ 20 GHz | 0.6707 | 3.0967e-2 | 2.5149e-3 | 0.081 |

**Two independent formulations of ground loss agree to 6% wherever the substrate is electrically
thin, and the excess tracks k₀H rather than w/h** — the geometry is fixed down that FR-4 column, so
the two candidate explanations separate cleanly and the geometric one is refuted. That agreement is
R-cl4-3's answer: the term is right, not merely present.

**The divergence above it is not attributed, and it is not tuned away.** Two things are happening at
once and this measurement cannot separate them: kernel B's ground term absorbs the surface-wave and
radiated field, which Wheeler's incremental-inductance rule structurally has no term for; and the
two-line instrument itself degrades on an electrically thick radiating substrate — CL1 recorded that
*"the absolute α of a PEC FR-4 line out of this route is not a usable number; the difference is"*, and
by k₀H = 0.67 the DIFFERENCE stops being usable too (0.081 is not a physical number). **Neither
kernel is authoritative there**: kernel A's quasi-TEM rule has no radiation term and kernel B's
instrument has lost resolution, so the row is reported rather than resolved.

**The SHARE does NOT come out at 21.1% and cannot**, and the reason is CL1's rather than CL4's. On
the FR-4 2 GHz row — where the term itself agrees to 6% — kernel A reads 21.06% and kernel B reads
**27.48%**, because the numerator is right and the DENOMINATOR carries CL1's measured single-sheet
strip deficit (0.7478 there, 0.63 at 10 GHz, 0.73 on GaAs). The share is inflated by exactly that
factor and by nothing else: `1.062·g / (0.7478·s + 1.062·g)` with `g/s = 0.2669` is 27.49%.
**Quoting kernel B's share as the ground's share of the conductor term would be quoting CL1's
deficit back with the opposite sign.** The overview's 21.1% / ~11% / 25.0% stay what they are — a
statement about the PHYSICS, reproduced here by kernel A to the digit — and the gate is the TERM.

### 8. Traps and findings

- **The general kernel REFUSES a lossless dielectric, and that is what stopped CL1's own
  construction being reused.** CL1 measured α_c with `tanδ = 0` so that what was left was conductor
  plus radiation. A lossy floor can only be expressed through `MediumStack`, which sets
  `RequiresGeneralKernel`, and `SommerfeldIntegral.CanIntegrateLayered` (reached through
  `Dcim.FitAtHeights`) refuses a stack with no lossy layer anywhere — *"a lossless guided stack puts
  its surface-wave poles exactly on the real-k_ρ contour this integrator uses"*. So every CL4
  measurement carries the substrate's real tanδ and cancels it in the difference. §9 is the same
  fact with a much larger consequence.
- **`PerfectConductor` makes the STRIP perfect and NOTHING ELSE**, because the plane is not in the
  fill. That is exactly what makes B1 − A1 the ground term on its own — and it is also the mistake
  that ate the first measurement: subtracting a PEC-metal floor taken on the LOSSY-ground stack
  removes the very term being looked for, and reports a ground share of **−0.03%** with every gate
  still green. The floor must come from the PEC-ground kernel.
- **The provenance hash could not see the ground's metal.** `EmSnpProvenance` hashed
  `ms.Bottom.Material.EpsR`, which is `EmMaterial.Air` for a PEC and for a conducting plane alike, so
  two runs differing only in the ground's σ would have shared a cached `.snp`. The termination's
  KIND, σ and thickness are in the hash now. This is CL3's own finding one surface over — those two
  numbers used to ride along unused, and they do not any more. Nothing the shipped extractor
  produces is affected yet (§9); the hash is fixed before it can be.
- **A conducting floor's dissipation lands in CL2's `P_dielectric`, which is a RESIDUAL.**
  `P_conductor` is an integral over the fill's own basis functions and the plane has none, so what
  the plane absorbs arrives as everything-else-minus and is booked against the line labelled
  dielectric loss. On a low-tanδ substrate it would be most of what that line reads. **Not fixed** —
  CL4's own "Must NOT" reserves the residual's arithmetic, and splitting it out needs a second
  quadratic form in the spectral domain rather than a relabelling. Recorded on `DielectricW`'s own
  parameter documentation.

### 9. WHAT THIS DOES NOT REACH, and the measured reason — read this before quoting any number above

**No run a user can make gets a conducting ground plane.** The engine has the capability, every gate
above passes, and `PlanarExtractor.BuildMediumStack` still writes `Termination.Pec`. That is a
decision, not an oversight, and it is CL4's own partial result — stated here loudly because the
brief's "Must NOT" forbids shipping one silently.

**The obstruction is structural and was measured, not argued.** A conducting floor is expressible
only on `LayerStack`, and `PlanarProblem.RequiresGeneralKernel` turns on the moment a `MediumStack`
is given. So turning it on in the extractor moves **every single-level, single-dielectric run** —
the FR-4 hero, the shipped patch antenna, every ordinary microstrip — off L8's one-slab path and onto
the general one. Three consequences, in order of weight:

1. **The general path refuses a LOSSLESS dielectric outright** (§8's first trap). An air or
   `tanδ = 0` substrate runs today on the one-slab path and would become a REFUSAL. `CoplanarDeembedTests`'
   own fixtures are `EmMaterial(1.0, 0.0)`.

   > **CORRECTION — measured while scoping CL5 and now CLOSED BY IT (2026-09-14, §CL5 §7): this
   > consequence is overstated, its example is wrong, and the obstruction itself is gone.** Two things were checked rather than argued. **(i) The example is not a
   > casualty.** `CoplanarDeembedTests`' fixtures are εᵣ = 1, so `guided` is FALSE and
   > `CanIntegrateLayered` already admits them today — the case that actually refuses is εᵣ > 1 with
   > tanδ = 0, which nothing above names. **(ii) The refusal is the DIRECT integrator's and the fit
   > does not share it.** It reaches the product only because `Dcim.FitAtHeights` borrows
   > `CanIntegrateInterior` as a precondition; `FitAtHeights` integrates nothing, it samples in k_zm
   > and subtracts every pole through `PoleSum` first. Measured on FR-4 1.6 mm at 10 GHz: at
   > tanδ = 1e-16 the TM₀ pole sits **3.8e-18** off the real axis — numerically ON it — and
   > `FitAtHeights` returns residual **3.86e-5** against the lossy stack's 3.17e-5, while the
   > high-high fit reproduces the one-slab kernel to 7.6e-7 (G_A) / 1.0e-5 (G_q) of free space either
   > way. **Nothing in the DCIM path degrades as the loss goes to zero.** And the predicate cannot see
   > a dissipative TERMINATION: a 35 µm copper floor on a tanδ = 0 substrate puts the pole at
   > |Im k_ρ|/Re k_ρ = **2.607e-5** — three orders further off the axis than the tanδ = 1e-12 case
   > that is freely admitted — and is still refused by name. **The εᵣ > 1, tanδ = 0 refusal is
   > therefore a LIVE DEFECT today**, not only a future obstruction: a two-level design on an ideal
   > substrate refuses, citing an integrator its run never reaches. Consequences 2 and 3 below are
   > unaffected and stand as written.
   >
   > **CL5 SHIPPED and this obstruction is retired.** `Dcim.FitAtHeights` asks
   > `SommerfeldIntegral.CanFitAtHeightsStructurally` now; the contour's refusal stays on
   > `CanIntegrateLayered` for the direct integrator's own callers; and a two-level design on a
   > tanδ = 0 guided stack solves through `PlanarSolve`. **Size CL6 and CL7 against §CL5 §7, not
   > against this numbered list.** One naming slip in consequence 2 is corrected there too:
   > `ValidatedRhoOverLambdaLayered` and `ValidatedRhoOverLambda` are BOTH 1.0 — 1.6e-2 and 6e-3 are
   > the error ceilings inside those ranges, and the 2.6× gap between them is real.
2. **The validated range is 2.6× worse on the general path** — `ValidatedRhoOverLambdaLayered`'s
   ≤1.6e-2 against `ValidatedRhoOverLambda`'s ≤6e-3 (§5) — so every one-slab run would be re-based
   onto a looser tier to gain a term worth 11–25% of the conductor loss.
3. **It is a CL3-sized re-bless**, and CL4 carries none of CL3's gates: no passivity/reciprocity
   direction check, no hero check, no golden-move tabulation. A default flip of that reach is its own
   brief.

**The half-measure was considered and REFUSED**: emitting a conducting floor only on problems already
on the general path (multi-level, or a multi-dielectric stackup). It is worse than a uniform limit,
because two physically identical designs would then get different ground physics according to whether
the stackup happened to carry one dielectric entry or two — a user adding a solder mask would
silently change the ground model. A stated limit that is the same everywhere beats a correction that
appears and disappears.

**What would lift it**, in the order it would have to happen: give the ONE-LAYER `SpectralGreens` the
same impedance floor (the composite reflection is still EVEN in k_z1, so the basis holds — §3 — but
it needs σ and t on `GroundedSlab`, which is D2's own type and is a key in the fit cache, the
provenance hash and `DescribedByTheSlab`); then flip the extractor with a CL3-shaped re-bless. That
is a brief, not a follow-up.

### 10. Gates

`tests/Engine.Tests/Mom/PlanarLossyGroundTests.cs`, 12 tests. **7 in the routine tier at 4 s
together**; 5 tagged `Category=Benchmark` at **5 m 03 s** (M3's two starters 57 s + 1 m 57 s, M4's
two N = 440 multi-level fills 1 m 20 s, M5's three-frequency sweep 44 s, M1's full six-case fit sweep
5.0 s). Measured, then tagged on the repo's mechanical ~5 s rule rather than on a preference.

**The two measurements whose refusal would matter most keep a routine-tier counterpart**, because a
gate nobody runs is not a gate: `M1b` re-asks measurement 1 on one stack at one frequency against the
same un-widened `ValidatedRhoOverLambdaLayered`, and `M4a` is measurement 3's refusal TABLE on its
own — the expensive half only measures how far the matrix moved, while the cheap half is what catches
a refusal someone deleted instead of narrowing.

**R-cl4-4** — all four measurements tabulated whether they pass or refuse: §3, §4, §5, §6.
**R-cl4-5** — `dotnet test tests/Engine.Tests`, run ONCE and triaged from the TRX:
**2,407 passed, 0 failed, 1 skipped** (a pre-existing `DataSetExportTests` skip), 5 m 08 s. Nothing
outside this brief's own file moved, which is what says the four narrowed predicates (§0) are
narrower and not different.

### 11. Reported to the owner, by file and line (no `CLAUDE.md` edit, per the standing rule)

- `src/Engine/Mom/CLAUDE.md` **§5**, the conductor-loss bullet, currently ends *"**The GROUND PLANE
  is still PEC** (CL4, not run) — 21% (FR-4) / ~11% (GaAs) / 25% (low-loss laminate) of the conductor
  term."* CL4 HAS run. The sentence that replaces it has to say both halves: the termination is built
  and measured, and **the shipped extractor still writes a PEC floor** (§9), so the 21/11/25% figures
  stand for every run anyone can make.
- `src/Engine/Mom/CLAUDE.md` **§5's validated-range table** could take a row for the plane's own
  `Z_s` — it has no constant of its own, which is the point, but the ≤6% agreement against kernel A
  at k₀H ≤ 0.07 and the divergence above it is a validated RANGE and belongs in that table.
- `src/Engine/Mom/CLAUDE.md` **§7**, the antenna-metrics bullet: `FrontToBackDb` *"always, in this
  kernel — the field below a laterally infinite PEC is identically zero so the true ratio is
  infinite"*. Still true for a PEC floor and **false for a conducting one** (§6); the code now carries
  two sentences and §7 carries one.
- `src/Engine/Mom/CLAUDE.md` **§7**, the same bullet's neighbours: the ground-attachment refusal and
  the far field's *"A stack that is not PEC below"* both now say CONDUCTOR rather than PEC.
- The repo `CLAUDE.md`'s test-suite section counts **124 `Category=Benchmark` methods repo-wide**;
  this brief adds 4 methods (5 cases, 5 m 03 s), so that count is **128**.
- **`docs/user` is broadly out of date with its own generator, and it is not this brief's doing.**
  `tools/DocGen/check-docs-current.sh` would fail today: regenerating touches **78 files** that
  reproduce at HEAD with no source change of any kind — the drift families the owner's own note
  records, plus pages whose `.md` was edited without a regeneration (CL3's conductor-loss rewrite of
  `mom-engine.md` is one). **Only `docs/user/reference/mom-engine.html` is carried here**, the one
  page whose source this brief edited, and it is stable across two consecutive DocGen runs.
  `assets/js/search-index.js` was deliberately NOT carried: it is an aggregate over every page, so
  regenerating it would commit other pages' un-regenerated content while leaving their own HTML
  stale. A full `check-docs-current.sh` clean-up is a decision about 78 files and is the owner's.

## QSC — a quasi-static port calibration, so the bottom of the band stops costing a metre of line (2026-09-14)

`docs/sonnet-briefs/brief-quasistatic-port-calibration.md`. RAW1 §6 closed with the low-frequency
wall still standing and named its cause: **the wall is the price of MEASURING γ from two full-wave
line standards (D5), and nothing else.** The two lines' length difference has to sit inside TRL's
usable interval βΔℓ ∈ [20°, 160°], so it scales as 1/f_lo — on the reported board a 3.8 mm microstrip
swept from 100 MHz wanted a **161.5 mm** standard at **79,055 unknowns** against a 12,000 ceiling, and
the run refused. The DUT itself is N = 1,144 and solves fine down there.

**The reported file runs now**, 100 MHz – 6 GHz, 11 points, no refusal and no accelerator.

### 1. The hole in RAW1 §6(a), which is why this is not a two-line change

**Supplying γ does NOT by itself remove the long standards.** γ is ALREADY an input to
`PlanarDeembed.SolveErrorBox` — look at its signature. One standard gives two complex equations
(`m11`, `m21`) for three complex unknowns (`a11`, `a21²`, `a22`), and no reciprocity or symmetry
argument closes that gap, so the error box still needs TWO lines however γ arrives.

**What a known γ buys is that Δℓ need not be ELECTRICALLY long.** The [20°, 160°] interval exists to
protect the γ EXTRACTION, where `Acosh`'s branch structure makes βΔℓ = nπ a genuine singularity and
where α is two orders below β so its extracted sign is noise. The error box's own conditioning is a
different and far gentler thing. So Δℓ is sized from the SUBSTRATE and the MESH instead of from λ,
both standards become small and frequency-independent, and the sub-band ladder collapses to one
separation because there is no βΔℓ window left to cover.

### 2. R-qsc-2 — the supplier is the STANDARD's own electrostatics, and the decision is structural

The two candidates were kernel A's per-unit-length cross-section solve (`RlgcExtractor.Extract`) and
the standard's own electrostatics. **The standard's, and four of the reasons are structural rather
than numerical — this was not settled by whose number was closer.**

- **`PlanarDeembed`'s own D7 rule already says so**, one quantity over: *"Kernel A is the ORACLE for
  Z_c, never an input. Reading Z_c or C_pul off QuasiStaticKernel and feeding it into B would make the
  phase table's own 'A and B agree on a uniform line' gate a tautology and would import A's
  discretisation error into B's answer."* A γ taken from A is that sentence about γ.
- **De-embedding is ALREADY part quasi-static.** `PlanarDeembed.CapacitancePerMetre` runs an
  electrostatic solve on the two standards and supplies C_pul for Z_c = γ/(jωC_pul). What this adds is
  ONE further solve of the same kind — the same geometry with the dielectric removed — read as
  L = μ₀ε₀/C₀. An existing route is extended by its other half; nothing new is introduced.
- **Kernel A cannot represent what a standard can.** A coplanar standard's C_pul is a MODAL
  capacitance (RP-2c), a widened one carries a FLOATING conductor (PCAL3), and a group's is a MATRIX
  (PCAL4/D7'). All three ride on `PlanarStandard`'s own `ModePotential`/`ModeWeight`/
  `FloatingPotential` and are honoured here for free, because it is the same solve. Kernel A would
  need a cross-section it does not have and a WIDTH that is not defined for a feed that is not a plain
  rectangle.
- **It is self-consistent in the discretisation.** C and C₀ come off the SAME mesh, so ε_eff = C/C₀ and
  Z_c = √(L/C) share their error and it cancels to first order in the ratio — which is exactly the
  argument D7 already makes for DIFFERENCING two standards rather than solving one.

`PlanarQuasiStaticLine` is the whole of it: `C` and `C₀` per metre by D7's own differencing,
`L = μ₀ε₀/C₀`, `γ = jω√(LC)`, and `Z_c` left on D7's existing `γ/(jωC_pul)` spelling so **only γ's
source changed**.

**C stays COMPLEX all the way to γ, and that needed a change.** `PlanarKernelTerms.StaticScalar` is
built on `GroundedSlab.EpsComplex`, so the charge already carries tanδ; D7 was dropping it with a
`.Real` because Z_c takes a double. `PlanarDeembed.StaticCapacitance` /`CapacitancePerMetre` and
`PlanarStaticAim.TotalCapacitance`/`ModalCapacitance` are now `.Real` wrappers over complex bodies —
the SAME bodies, so every existing caller is bit-identical. Without it a lossy board would publish
α = 0 exactly, which is a plausible wrong number and would read as a regression against the measured
path beside it in the same `.npy`.

**The metal stayed a PERFECT CONDUCTOR here while this brief shipped**, exactly as the full-wave fill
was by default (CL1's `PlanarFillSettings.ConductorLoss` was off), so γ carried the dielectric's
attenuation and no conductor term. A Wheeler term would have been a claim kernel B's own matrix does
not make, and the two paths would then have disagreed in α across the crossover by whatever the sheet
model is worth.

> **THAT PARAGRAPH WAS TRUE ONLY WHILE `ConductorLoss` WAS OFF, AND CL3 FLIPPED IT — 2026-09-14.**
> The metal is a real conductor here now and **R comes off THIS SOLVE'S OWN CHARGE**:
> `R = Σ_levels Re(Z_s)·[ΔS_level·Δℓ/|ΔT|²]` with `S = Σ|q|²/A` and `T = Σq` differenced across the
> same two standards C is, and `γ = √((R + jωL)(jωC))`. **Not Wheeler** — the paragraph above stays
> right about kernel A for the reason it gives, and §CL3's own measurement makes the point a second
> time with numbers: the static-charge supplier tracks the fill's own α_c to 0.96-1.15× on every mesh
> tried, while kernel A's Wheeler term over-reads it by 1.32-1.76×. It costs no extra solve, because
> the second moment comes off the charge vector the C differencing already computes.
>
> **The three consequences CL3 §0 named are all closed.** The error box's ≈ e^{−α_c·Δℓ} and the
> `√(1 + R/(jωL))` correction to `PlanarDeembed.CharacteristicImpedance`'s γ/(jωC′) both come back
> automatically once γ carries R (measured on the GaAs starter: arg Z_c moves from −0.054° to
> −0.681° at 2 GHz), and `planar.Gamma` no longer steps at the crossover — α_c agrees across the
> overlap to **−3.07 % … −1.05 %**.
> `TheQuasiStaticPathAgreesWithTheMeasuredOneWhereBothRun` compares **Re(γ) and arg Z_c** now beside
> its β, |Z_c| and max|ΔS|; both comparisons were vacuous under PEC metal and are not now.
> `PlanarFillSettings.PerfectConductor` reproduces every number in this section bit for bit —
> `GammaAt` deliberately keeps its `jω√(LC)` spelling rather than the algebraically identical
> `√(jωL·jωC)` so that the oracle is exact to the LAST BIT and not to the last digit of the physics.
> `RESOLVED.md` §CL3.

### 3. M1 — the crossover, measured per stack, then fitted to ONE law

M1 measured quasi-static γ and Z_c against the MEASURED two-line values from `PlanarPortCalibrator.At`,
per frequency, on three stacks at two mesh densities. The calibrator was built at `(f, f)` so each
point's own separation sits near 60° and conditioning is never the variable.

**The reported board — 0.6 mm low-loss laminate, εᵣ ≈ 4, w = 254 µm — refined mesh** (quasi-static minus measured):

| f | Δβ | ΔZ_c | ε_eff measured (quasi-static 2.786) |
|---|---|---|---|
| 100 MHz | +6.97% | +7.15% | 2.452 |
| 300 MHz | +0.98% | +0.73% | 2.734 |
| **1 GHz** | **+0.20%** | **−0.10%** | 2.775 |
| 3 GHz | −0.14% | −0.43% | 2.794 |
| 6 GHz | −0.35% | −0.63% | 2.806 |
| 10 GHz | −0.77% | −1.05% | 2.830 |
| 20 GHz | −2.12% | −2.35% | 2.909 |

FR-4 1.6 mm, same shape, dispersing earlier as `h√εᵣ` predicts: +4.78% at 100 MHz, +0.09% at 1 GHz,
−2.61% at 6 GHz, −5.83% at 20 GHz.

**TWO DIFFERENT ERROR SOURCES, AND THEY MUST NOT BE CONFLATED — the disagreement is smallest in the
MIDDLE of the band and grows in both directions.**

- **Above ~5-10 GHz it is real dispersion**, ε_eff rising, quasi-static under-predicting β. That is
  what the measured calibration is for, and it is why this is a second path and not a replacement.
- **Below ~300 MHz it is the MEASURED value that is wrong, not the quasi-static one.** Proven by
  refinement rather than asserted: on the default mesh the 100 MHz disagreement is **+17.8%** and on
  the refined mesh **+7.0%** — the measured value moves TOWARD the quasi-static one as the mesh
  tightens, and ε_eff_meas goes 2.02 → 2.45 against a static 2.786 it must approach as f → 0. **So the
  two-line calibration at the bottom of the band is not merely expensive, it is inaccurate**, which is
  a stronger argument for this work than the one it was briefed on.

**GaAs 0.1 mm is the outlier and is NOT papered over.** It agrees to 0.5-1.3% at 6-20 GHz but its
MEASURED values at 100-300 MHz are nonsense — ε_eff_meas ≈ 12.3-12.5 against a substrate εᵣ of 12.9
and a quasi-static ε_eff of 8.21 — and 1 GHz reads +8.2%. That is not dispersion and not a
conditioning slope. It is the same mechanism as the paragraph above, at its extreme: a stack whose
standards are tiny in absolute terms, where the measured route is worst and the quasi-static one is
the answer. It is recorded rather than tuned around, and it is the reason the crossover for GaAs comes
out at 26 GHz — the whole shipped band is on the quasi-static side of it.

**The law.** Dispersion in a microstrip groups on `h·√(εᵣ−1)/λ₀`, which is what lets one constant
cover a 1.6 mm board and a 0.1 mm MMIC. M1's two readable crossovers are ratios of **0.0295** (FR-4,
≈ 3 GHz) and **0.0347** (the reported board, ≈ 10 GHz);
`PlanarCalibrationSettings.DispersionCrossoverRatio` is **0.03**, the conservative end of that pair.

| stack | h | εᵣ | by dispersion | by thickness | crossover |
|---|---|---|---|---|---|
| FR-4 starter | 1.6 mm | 4.4 | 3.048 GHz | 3.75 GHz | **3.048 GHz** |
| the reported board (low-loss laminate) | 0.6 mm | 4.0 | 8.654 GHz | 9.99 GHz | **8.654 GHz** |
| GaAs starter | 0.1 mm | 12.9 | 26.07 GHz | 60.0 GHz | **26.07 GHz** |
| an AIR slab | 5 mm | 1.0 | **+∞** | 1.20 GHz | **1.20 GHz** |

**THE SECOND COLUMN IS NOT BELT AND BRACES, AND THE AIR ROW IS WHY.** The fit above measures
DIELECTRIC dispersion — ε_eff climbing from (εᵣ+1)/2 toward εᵣ — and at εᵣ = 1 there is none, so that
term alone answers "+∞". **That is true of the MODE and false of the STRUCTURE**: in a homogeneous
medium every bound mode is exactly TEM, but a coplanar pair 5 mm above its plane in air at 10 GHz is a
sixth of a free-space wavelength thick, radiates, and its measured β sits **3.6 %** off k₀ — which the
two-line calibration captures and a quasi-TEM γ cannot. That is not a hypothetical: nine
`CoplanarDeembedTests` cases failed the moment the dispersion term was allowed to answer alone, the
first of them on the identity that says the calibration is exact at its own standard's length
(|S₁₁| = 7.3e-2 where the requirement is machine zero). Its fixtures are `EmMaterial(1.0, 0.0)`.

So the crossover is the LOWER of two limits, the second being an absolute electrical thickness
`h/λ₀ ≤ PlanarCalibrationSettings.ElectricalThicknessCrossoverRatio` = **0.02**, chosen to be INERT on
every stack M1 measured (the dispersion term binds first on all three, by 1.2× to 2.3×) so it moved no
number recorded here. **Being a MINIMUM it can only LOWER a crossover** — i.e. only ever move a point
from the quasi-static path back onto the measured one, which is the conservative direction.

**The general lesson, which is this brief's own trap repeated:** a crossover fitted to one family of
stacks answers confidently about a family it was never shown. FR-4, a low-loss laminate and GaAs are
all εᵣ ≥ 3.7; εᵣ = 1 is not an exotic corner here, it is what every coplanar fixture in this directory
uses.

**Conservative is the right direction here and the reason is asymmetric.** Below the crossover the
quasi-static path is not merely cheaper but MORE accurate; above it the measured one is right. A
crossover placed low costs a little accuracy and a lot of standard, in a region where both are
affordable — a crossover placed high publishes dispersion that is not there. **It is deliberately NOT
a user setting**: a knob invites someone to put it in the wrong place and there is no way for them to
know, because both failure modes publish a smooth, plausible, wrong phase.

### 4. M3 — how short Δℓ may be, and THE SURPRISE IS THE OTHER DIRECTION

`PlanarCalibration.SeparationPlan` is the one place that decides what a band builds: the measured
ladder over the part ABOVE the crossover (`SuggestDeltas` asked with the CROSSOVER as its lower edge,
not the user's), plus ONE short separation for the part below. Three shapes, and the first is what
makes every run that ships today bit-identical — a band entirely at or above the crossover gets
`SuggestDeltas`' own array, entry for entry, and no quasi-static entry at all.

**A band that STRADDLES the crossover changes at its top as well as at its bottom, and that is worth
stating rather than discovering.** The measured ladder is drawn from the CROSSOVER, so a 1–20 GHz
sweep on FR-4 calibrates its 5/10/15/20 GHz points against `SuggestDeltas(3.05 GHz, 20 GHz)` rather
than `SuggestDeltas(1 GHz, 20 GHz)` — a different separation set, a different error box, and not
bit-identical. That is deliberate: a separation exists to cover a sub-band, and below the crossover no
measured separation is used at all, so building the ladder from the user's f_lo would mesh and solve
standards that no frequency will ever read. It is what a 100 MHz–6 GHz sweep was paying 79,055
unknowns for.

Measured on the FR-4 fixture (6 mm line, coarse mesh, bulk cell 1.5 mm), sweeping the separation from
24 substrate heights down to below one bulk cell. `margin` is
`RejectedResidual / ConsistencyResidual` — the a₂₂ SIGN margin, which is the one failure mode that
does not degrade smoothly:

| Δℓ | βΔℓ @ 2 GHz | standard N | max\|ΔS\| vs measured @ 1 GHz | worst margin |
|---|---|---|---|---|
| 24 h = 38.4 mm | **170.1°** | 234 | 1.21e-3 | **2.5e+01** |
| 12 h = 19.2 mm | 85.0° | 143 | 5.40e-4 | 3.6e+02 |
| **6 h = 9.6 mm (shipped)** | 45.8° | 101 | 6.00e-4 | 2.9e+02 |
| 3 h = 4.8 mm | 26.1° | 80 | 8.46e-4 | 3.1e+02 |
| 1.5 h = 2.4 mm | 13.1° | 66 | 1.28e-3 | 3.5e+02 |
| 0.75 h = 1.2 mm | 6.5° | 59 | 1.69e-3 | 3.3e+02 |
| 0.375 h = 0.6 mm | 6.5° | 59 | 1.69e-3 | 3.3e+02 |

**Three findings, and the first is the one the brief expected to go the other way.**

- **SHORTER is nearly free and LONGER is what breaks it.** The error grows as ~1/Δℓ going down, from
  6.0e-4 at 6 h to 1.7e-3 at one bulk cell — a factor of three over a 16× shorter standard, against a
  de-embedding residual floor two to three decades below. Going UP, the 24 h separation reaches
  βΔℓ = 170° at 2 GHz, which is D6's own denominator zero at βΔℓ = nπ, and the a₂₂ sign margin
  collapses by an order and a half. **A supplied γ frees Δℓ from the usable interval's LOWER end; it
  leaves the upper end exactly where it was.** So `QuasiStaticSeparationM` caps the substrate term at
  the same electrical length `SuggestDeltas` aims a measured separation at, evaluated at the
  CROSSOVER — the highest frequency that separation ever serves. **h cancels in that cap**: βΔℓ there
  is a pure function of εᵣ, ≈ 58° on FR-4 and ≈ 50° on GaAs, so the cap is inert on any ordinary board
  and binds only on a substrate close enough to air that the crossover runs away.
- **THE MESH IS ALREADY A FLOOR AND IT IS EXACT.** The last two rows are identical because
  `LongitudinalPartition` realises a separation as a whole number of bulk cells, so a target under one
  cell simply becomes one cell. `QuasiStaticSeparationMinBulkCells` = 4 makes that explicit and puts
  it where D7's capacitance DIFFERENCING needs it — C_pul is (C₂ − C₁)/Δℓ, and two standards differing
  by one cell differ by one cell's worth of discretisation error as well as by a length.
- **The a₂₂ sign is never decided by noise on the shipped separation.** Worst margin across the whole
  sweep at or below 6 h is **2.93e+02**; on the reported file's own run it is **1.8e+02** (port 2 at
  100 MHz) to **9.3e+03**. `DeembedRejected` and `DeembedResidual` are both published per point, so
  this is checkable on any file rather than only in a test.

**A number that CHANGES and that a reader will notice: `DeembedResidual` is larger on this path.**
~1e-5 to 1e-3 on the fixture, against ~1e-15 on the measured path. **That is not a regression and the
old number was the misleading one.** On the measured path γ is EXTRACTED from the very two standards
the M₁₁ consistency equation then checks, so the check is nearly a tautology. With γ supplied the
residual becomes an honest measure of the quasi-TEM assumption on that cross-section — a better
diagnostic and a worse-looking number. The margin RATIO is what to read.

### 5. M2 — the overlap, at every frequency and not just its ends

FR-4 6 mm line, coarse mesh, DUT N = 24, band 500 MHz – 6 GHz so the measured ladder is still
affordable at its own bottom. Longest standard **206 unknowns measured against 101 quasi-static**.
Both paths de-embed the same DUT solve; the difference is γ's source and the standards' size.

| f | Δβ | ΔZ_c | max\|ΔS\| | βΔℓ measured | βΔℓ quasi-static |
|---|---|---|---|---|---|
| 500 MHz | +1.040% | +1.039% | 5.8e-3 | 35.6° | 11.4° |
| 750 MHz | +0.197% | +0.197% | 2.6e-3 | 53.8° | 17.2° |
| 1 GHz | −0.133% | −0.133% | 6.0e-4 | 72.0° | 22.9° |
| 1.5 GHz | −0.334% | −0.334% | 3.7e-3 | 34.4° | 34.3° |
| 2 GHz | −0.675% | −0.676% | 9.1e-3 | 46.1° | 45.8° |
| 3 GHz (≈ crossover) | −1.174% | −1.176% | 2.2e-2 | 69.4° | 68.6° |

**The two paths agree to ~1 % across the whole overlap and the disagreement is smallest in the middle
of it — the same shape M1 measured, on a different fixture and through the whole de-embedding rather
than on γ alone.** At the crossover it reaches the 1 % budget the crossover was drawn at, which is
what says the constant is in the right place. Δβ and ΔZ_c track each other to the fourth digit because
Z_c = γ/(jωC_pul) with the SAME C_pul on both paths, so Z_c's disagreement IS γ's by construction —
worth knowing before reading the two columns as independent evidence.

**The absolute error of either path is far larger than the difference between them, and that is the
pre-existing low-frequency floor rather than anything here.** On the same uniform line a de-embedded
S₁₁ must be exactly 0, and it reads 0.844 at 100 MHz, 0.393 at 300 MHz, 0.189 at 690 MHz, 0.110 at
1.28 GHz and 0.073 at 2.46 GHz on that deliberately coarse 24-unknown mesh. That is the series
delta-gap's own `a₂₁ ∝ ω` amplification (`CLAUDE.md` §5, "~22× at 2 GHz, growing as f⁻²"), it is
identical on both paths, and **this brief neither improves nor worsens it**.

### 6. M5 — the reported file, end to end

`extract1` from the RAW1 report: a 3.8 mm microstrip trace on 20 mil laminate, a 254 µm port on P1 and
a 558.8 µm pad on P2, 100 MHz – 6 GHz, 11 points, via the `em` CLI verb on the `.cem` itself.

**It runs.** 4 min 12 s wall, 10 cores, **Debug** — the owner's build, and the one quoted here rather
than a Release figure nobody measured. (`CLAUDE.md` §8 already records this code running ~4× slower in
Debug at N = 4,836; that is context for the number, not a second measurement.)

| | port 2's standards |
|---|---|
| RAW1, as reported | **1,526 / 79,055 / 21,349 / 6,600** — refused |
| now | **1,526 / 3,289** |

The short standard is **bit-identical**, which is the construction working as intended: only the
separations changed. The whole run's four standard meshes are 871 / 1,039 / 1,526 / 3,289 = **6,725
unknowns against the 108,530 port 2 alone used to ask for**, and 5.88× the DUT's own N = 1,144.

**S₂₁ against the ideal-MLIN reference**, both at 50 Ω:

| f | S₂₁ sim | S₂₁ ideal | Δ | S₁₁ sim | S₁₁ ideal |
|---|---|---|---|---|---|
| 100 MHz | −0.324 dB | −0.006 dB | **−0.318 dB** | −12.2 dB | −40.2 dB |
| 690 MHz | −0.027 dB | −0.039 dB | +0.012 dB | −26.2 dB | −23.5 dB |
| 1.28 GHz | −0.091 dB | −0.097 dB | +0.006 dB | −17.9 dB | −18.2 dB |
| 2.46 GHz | −0.312 dB | −0.282 dB | −0.030 dB | −12.1 dB | −12.9 dB |
| 4.23 GHz | −0.782 dB | −0.686 dB | −0.096 dB | −8.1 dB | −8.9 dB |
| 6 GHz | −1.296 dB | −1.140 dB | −0.156 dB | −6.1 dB | −6.7 dB |

**The stated tolerance: 0.16 dB in S₂₁ from 690 MHz to 6 GHz, and 0.32 dB at 100 MHz.** Two separate
things are in that column and neither is the calibration:

- The slow rise to 0.16 dB at the top is the STRUCTURE, not the reference. The artwork is not an ideal
  MLIN — 40 corners, a bend, and a 558.8 µm pad on P2 — and both curves already say so: S₁₁ is −6 dB
  at 6 GHz on the ideal line too. |ΔS| grows monotonically with frequency, which is radiation and the
  discontinuities, not a calibration error.
- **The 100 MHz point is the pre-existing `a₂₁ ∝ ω` floor and the diagnostics say so directly.** Its
  `DeembedResidual` is the SMALLEST of the sweep (1.05e-7 on port 2, against 2.9e-6 at 4 GHz), its
  margin is 1.8e+02, and its ε_eff and Z_c are the ones that are RIGHT: quasi-static gives
  ε_eff = 2.787 and Z_c = 105.7 Ω on the 254 µm trace against closed-form microstrip values of 2.777
  and 106 Ω — **0.4 % and 0.3 %** — where RAW1 §6 records the two-line route giving 2.45 and 99 Ω on
  the same trace, i.e. **12 % and 6.6 % out.** The calibration at 100 MHz is the best one in the
  sweep; what is large there is the peel's amplification of it. §5's uniform-line control is the
  cleanest statement of that floor: on a plain line, where a de-embedded S₁₁ must be exactly 0, both
  paths read 0.84 / 0.39 / 0.19 / 0.11 at 100 / 300 / 690 MHz / 1.28 GHz on a deliberately coarse
  24-unknown mesh, and they read it IDENTICALLY.

  > **SIZED AND GATED BY §PEEL, 2026-09-14 — this paragraph named the floor and declined to size it,
  > and it must not be left standing as though the sizing were unknown.** It is
  > `ConsistencyResidual · |a₁₁| / |a₂₁|²`, which is published now as `DeembedErrorFloor` and tracks
  > the control's realised |ΔS| to a ratio of 0.73-1.06 over three stacks, three mesh densities and
  > three decades. A POINT whose floor reaches 0.25 is left out of the published sweep and named
  > rather than published; the rest of the sweep is unaffected. **Two corrections to what is implied
  > above:** the amplification is `1/|a₂₁|²` and NOT the standards' separation — a 30× longer second
  > standard moves it by under 6 % and is ten times worse at 100 MHz — and **this
  > section's own FR-4 control fixture is degenerate**, its 6 mm line being shorter than the two end
  > runs the standards reproduce, so its 0.84 / 0.39 / 0.19 / 0.11 overstate the instrument's error.
  > On a 40 mm line of the same stack at the same coarse mesh the 100 MHz reading is **8.2e-3**, two
  > orders below the 0.84 quoted above.

**THE ACCELERATOR DID NOT ENGAGE, and it was checked rather than assumed.** The run's notes carry no
AIM line and no GMRES; every standard is under the 5,000 dense ceiling. That closes RAW1's "separately
still open" GMRES failure as a side effect, and it was never a separate defect: the `.cem` never asked
for the accelerator, `PlanarKernel` turned it on itself in the
`catch (PlanarAcceleratorWouldFitException)` around `PlanarSolve.Run` because a CALIBRATION STANDARD
had crossed the dense ceiling, and the DUT at N = 1,144 had always been comfortably dense. **It will
still engage on its own merits for a DUT genuinely over 5,000 unknowns**, which is the case this does
not touch.

The `.npy` reads `CalQuasiStatic = 1` and `CalibrationUsable = 1` at all 22 (freq, port) slots, ε_eff
and Z_c constant across the sweep (they are frequency-independent by construction on this path, which
is itself a readable signature of which path ran), and α rising linearly from 0.257 to 15.4 dB/m —
constant tanδ, which is what the complex C is there to produce.

### 7. M4 — the ceiling refusal learns about the crossover

RAW1 §4's own defect, one phase on: that refusal used to blame the mesh and offer "coarsen it", RAW1
re-pointed it at the lower band edge, and with this path in place **a band edge below the crossover
binds nothing at all.** Printing "raise the lower band edge" there would be the same defect a third
time.

`PlanarCalibration.MeasuredBandBottomHz` is what every band-edge quantity is now asked of — the user's
edge or the crossover, whichever is higher, clamped to the band — and `PlanarSolve.BandEdgeRemedy`
branches on WHICH KIND of standard is over the ceiling:

- a MEASURED standard still names a band edge, but the ladder is quoted from the crossover
  (*"Below 3.048 GHz this port is calibrated quasi-statically on short, frequency-independent
  standards, so the measured ladder is drawn from 3.048 GHz rather than from 100 MHz"*), and when no
  edge inside the sweep helps it says outright that raising it below the crossover changes nothing;
- the SHORT quasi-static standard says **"The band edge is NOT the remedy here"** and names the port's
  TRANSVERSE mesh, which is what actually sets its size — because a standard reproduces the DUT's
  transverse gridlines verbatim (D4) and its length no longer moves with frequency at all.

`LongestStandardLengthM`, `StartFrequencyThatFits` and `SuggestDeltas` are **unchanged** and still
describe the measured ladder, which is still what they describe;
`RawSolveAndCalibrationRemedyTests` passes unaltered for that reason. What changed is which band the
caller asks them about.

### 8. Traps found, and the two that cost the most

- **`Termination`'s constructor is private and PEC/PMC already carry `EmMaterial.Air`.** Building the
  air-filled `LayerStack` for L = μ₀ε₀/C₀ by `t with { Material = EmMaterial.Air }` does not compile,
  and the reason is the right one: only a HALF-SPACE termination has a material to empty. A PEC floor
  is still a PEC floor with the dielectric gone, which is exactly what the identity asks for.
- **A model fitted to the real stack is the wrong Green's function for an air-filled one.**
  `CapacitancePerMetreComplex`'s `InteriorStaticModel` parameter is IGNORED when `airFilled` is set,
  and the fit is redone. Reusing it would have produced a complete, plausible, wrong [L] on every
  general-stack board — and nothing would have failed.
- **A GROUP must not take this path, and the decline is in `SeparationPlan` rather than at each call
  site.** PCAL4's modal error box separates N modes by the DIFFERENCE of their electrical lengths over
  Δℓ (`ModeSeparationFloorDegrees`, 0.5°, asked at setup and again per point). A separation sized from
  the substrate rather than from λ drives every one of those differences toward zero at the bottom of
  a band, so the quasi-static path would turn PCAL4's refusal from a rare event into the normal case.
  Supplying the modal γ's quasi-statically would remove that objection — `PlanarModalMedium` already
  computes them — but it is a different error box and a different measurement.
- **`SelectSeparation` must be offered the MEASURED separations only, above the crossover.** The
  quasi-static entry is the last one in the plan and is a fraction of a degree up there, so leaving it
  in the candidate list lets a frequency just above the crossover score it as "closest to the
  interval's centre" on a log measure and calibrate against a standard with no phase in it.
- **One plan, drawn once.** `PlanarSolve` computes `SeparationPlan` and hands the SAME value to both
  `BuildSet` and the calibrator. Two evaluations of the same function with the same arguments agree
  today, and PCAL6/R-pcal6-3 is what happens when they stop: a run solves one standard and calibrates
  against another, silently.
- **`PlanarPortCalibrator`'s `separations` parameter is the A-vs-B seam at the CALIBRATOR level and is
  what lets the overlap gate and the Δℓ sweep name a separation directly.** Not on
  `PlanarCalibrationSettings`, not in the `.cem`, not reachable from the panel — the same status
  `UseRadialTable = false` and `UseSymmetricFactorization = false` have.
- **A new `throw` below the UI firewall trips `UserFacingTextGateTests`, and that is the gate doing
  its job.** `PlanarQuasiStaticLine`'s non-positive-capacitance check is an INTERNAL INVARIANT with no
  user remedy — the two standards differ only in the bulk cells between their planes, so a
  non-positive difference means the electrostatic solve did not converge rather than that the geometry
  is unusual — so it is allowlisted in `tests/Firewall.Tests/user-facing-text-allowlist.txt` with that
  reason written beside it. Anything a user could ACT on belongs in a `CircuitRF.Diagnostics.Diagnostic`
  instead.
- **`PlanarCalibrationSettings.QuasiStaticBelowCrossover` is the SETTINGS-level one, and it had to
  exist.** Off reproduces the pre-QSC answer bit for bit, which is `IncludePassiveNeighbours`' and
  `IncludeDrivenGroups`' own sentence and exists for the same two reasons: every measurement here was
  taken against it, and **the engine's own ceiling refusals need a run that still REACHES a ceiling**.
  Three `EmDeembedCeilingTests` cases and `PlanarP2MemoryWinsTests`' pinned digest are gated on it
  now — see §10. **It is NOT a crossover knob**: on or off, its off-state restores a refusal rather
  than a wrong number, and it has no `.cem` field. The crossover itself stays measured, fixed per
  stack and unsettable, for the reason §3 gives.

### 9. What was NOT measured, so nothing is read into the silence

- ~~**No conductor term in the quasi-static γ.** α is the dielectric's alone, matching kernel B's own
  default PEC metal. Whether CL1's surface impedance should also enter γ on this path is a question
  for whoever turns `ConductorLoss` on by default, and the two would then have to be made to agree
  across the crossover.~~ **ANSWERED at CL3 (2026-09-14)**, and it was answered by measurement rather
  than by preference — three candidate suppliers, two of them measured against the fill's own α_c on
  four meshes. The supplier is the standard's own static CHARGE and the two paths agree across the
  crossover to a few per cent. §CL3 §1.
- **No general-stack (MIM-4) measurement.** The air-filled route for a `LayerStack` is written and
  compiles, and it reuses `InteriorStaticImages.FitScalar` on the emptied stack, but every number in
  this section is on a one-slab problem. A buried-level port below its crossover is untested.
- **No coplanar (RP-2c), widened (PCAL3) or multi-level standard was run through this path.** They are
  handled by construction — the mode potential, weight and floating vectors ride along into both
  solves — but "by construction" is not "measured".
- **The crossover was fitted to two readable points, not to a swept family.** M1 measured three stacks;
  GaAs's measured values at the bottom of its band are too poor to read a crossover off at all (§3), so
  the constant rests on FR-4 and the reported board. A third stack of a different εᵣ family would
  either confirm the `h√(εᵣ−1)` grouping or move the constant.
- **No timing comparison against the path this replaces**, because on the file that motivated it that
  path does not run. The size comparison (§6) is the honest one.

### 10. Gates

`tests/Engine.Tests/Mom/QuasiStaticPortCalibrationTests.cs` — eleven tests, ~2 s, routine tier (no
`Category=Benchmark`; nothing here is a timing measurement and the overlap comparison runs on the
coarse fixture deliberately, because the ALGEBRA is what is being compared and a coarse mesh tests it
just as hard).

- the crossover per stack, its order across three stacks, and the reported band sitting under its
  own board's;
- a band entirely above the crossover producing `SuggestDeltas`' array **entry for entry**, on three
  bands — which is what makes every run that ships today bit-identical by construction rather than by
  tolerance;
- a real coupled pair forming a calibration GROUP and being declined by name, keeping the measured
  ladder entry for entry;
- the longest standard being the SAME length at five different lower band edges, against the old
  scaling's 161.5 mm at 100 MHz;
- the overlap, at every frequency and not only its ends, on β, Z_c and the de-embedded s-parameters;
- the a₂₂ sign margin, with its own recorded number (2.93e+02 worst at or below the shipped
  separation) **and** the assertion that a 24 h separation is at least 5× worse, which is what the
  upper cap exists for;
- `CalQuasiStatic` reading 1 below the crossover, 0 above it and NaN with nothing calibrated, in one
  sweep — and the note that says what the flag means;
- both branches of the re-pointed ceiling refusal, asserted on the sentences that would otherwise
  name a remedy that cannot bind.

plus one that the switch of §8 gives the measured ladder back, entry for entry.

`RawSolveAndCalibrationRemedyTests` passes **unaltered** (§7), and so does
`PlanarGroupSeparationTests.AGroupedSweepThatCalibratesTodayIsBitIdentical`, whose pinned literals are
a grouped sweep — the case §8's decline keeps on the measured ladder.

**Four existing tests HAD to change, and none of them is a regression.** All four gate a refusal or a
pinned literal on a run that this phase deliberately stops producing:

- `EmDeembedCeilingTests`' three cases (`P11_ADenseDeembeddedRun_IsRefusedAtSetup`,
  `LF3_ADenseCeilingTheAcceleratorWouldClear`, `P11_TheSameRunAccelerated_IsNotRefusedAtSetup`). Their
  fixture is a 13.1 mm → 299 µm taper on 0.508 mm laminate swept from 400 MHz / 1 GHz — entirely below
  that stack's 10.9 GHz crossover, so its standards now come out at **N = 1,498 / 2,386** against a
  5,000 dense ceiling and nothing refuses. That IS the phase. They run with
  `QuasiStaticBelowCrossover = false`, which is what the switch is for; the refusals themselves are
  unchanged and still fire on a band above the crossover or a port wide enough to cross on its
  transverse mesh alone.
- `PlanarP2MemoryWinsTests.P2_6_TheSweepsPublishedSMovesOnlyByM2sRescaling`. Its 1 GHz point is below
  FR-4's crossover, so the pinned digest — an anchor for P2/P5/P7's own bit-identity claims — would
  move. Same switch, same reason, and the same shape as the `WidenForStack = false` line LF1 already
  put beside it. **Its LF1 point-by-point comparison needed the switch on BOTH sides**, because QSC
  draws the measured ladder from the crossover: without that, the 5-20 GHz points differ for a reason
  that has nothing to do with `WidenForStack` and the identity it asserts would be about two changes
  at once.
- Nine `CoplanarDeembedTests` cases failed first and were **not** re-pointed — they found a real
  defect in the crossover law, and §3's second limit is the fix. They pass unaltered.

## RAW1 — "-80 dB through a short transmission line": de-embedding is no longer optional (2026-09-14)

Owner bug report on a real board — a 3.8 mm microstrip trace on 20 mil RO4350, 100 MHz – 6 GHz, EM
run reported about **-80 dB S21**. The kernel was working correctly. The `.cem` carried
`"Deembed": false`, and with de-embedding off kernel B published the RAW delta-gap solve.

### 1. The raw solve at an EDGE port is an OPEN CIRCUIT, not a degraded answer

This is the whole finding, and the previous wording of it everywhere in the engine — "includes the
port discontinuity", "for diagnostics only" — reads as *degraded*, which is why nobody caught it.

An edge port's cut sits ONE CELL INSIDE the drawn metal (`PlanarPortResolution`: "an edge port has a
feed outside its cut"). The delta gap is therefore a SERIES source whose outer terminal is a single
isolated sliver of copper, and the port sees that sliver's fringing capacitance in series with
everything else. Measured, on the reported file and on synthetic controls:

| case | kernel / path | result |
|---|---|---|
| the reported file | planar, raw | S11 = 0.99999 − j0.00087, S21 = **−107 dB** |
| one plain 3.83 × 0.254 mm rectangle | planar, raw | S21 = **−115 dB** |
| the same rectangle at **7.66 mm** | planar, raw | S11 moves in the **4th decimal** |
| the same rectangle | planar, de-embedded | S21 = **−0.11 dB** at 100 MHz |
| the same rectangle | quasi-static (kernel A) | S21 = **−0.005 dB** at 100 MHz |

Converting the raw S11 gives Y11 = jωC with **C = 13.9 fF** at the 254 µm port and **31.8 fF** at the
558.8 µm one — a ratio of 2.29 against the port-width ratio of 2.20. It is the sliver and nothing
else. **Doubling the line's length changes the raw answer in the fourth decimal**: the raw path
carries no information about the structure at all.

`PlanarExcitation.RawScattering`'s own doc comment has said "**R-prt-4: this is never the answer**"
since `d726f5df`. The `.cem` published it as the answer anyway.

### 2. Why the switch existed, and why removing it loses nothing

PCAL2/R-pcal2-3 added `EmSetup.Deembed` for exactly one reason, stated in its own doc comment: the
mesh-ceiling refusal recommended "turn de-embedding off and read the raw solve" in prose, and *a
refusal that names an unreachable remedy is not a remedy*. **The remedy was the defect.** Once it is
deleted (§3) the switch has no purpose left.

And it never had a second case: de-embedding applies only to `PlanarPortKind.Edge`
(`IsDeembeddable`), so on a design of internal delta gaps or via ports turning it off already changed
nothing. **The switch's two settings were "no effect" and "an open circuit."**

What was done:

- `EmSetup.Deembed` and the panel checkbox are **gone**. `CemFile.Deembed` is still DESERIALISED, so
  a legacy document opens rather than being refused, and sets `EmSetup.LegacyRawSolveRequested`;
  `EmRunService` warns and runs de-embedded. It is never written back, so the field leaves the file
  on the next save. That is a deliberate single exception to this format's byte-identical round-trip
  rule — the field no longer has a meaning to preserve.
- `PlanarSolveSettings.Deembed` in the ENGINE is untouched and stays the way the far-field and
  resonance paths ask for a current distribution without paying for calibration (191 references
  across the test suite). What is removed is a `.cem`'s ability to publish that path's s-parameters.

### 3. Five refusals recommended the raw solve; a source scan found the fifth

Four were in `PlanarSolve` (the RP-2c third-conductor refusal, both branches of the standard-ceiling
refusal, and the interior-fit-residual refusal). The fifth — `PlanarPort.RefusalFor`, PCAL2's own
clearance refusal, which called it one of "the two ways past" — was found only because
`RawSolveAndCalibrationRemedyTests.NoRefusalInTheMomEngineRecommendsReadingTheRawSolve` scans the
source rather than asserting on one message. **Write the gate as a scan when the defect is a phrase.**

### 4. The ceiling refusal named the wrong cause and three inert remedies

It blamed the port's WIDTH and offered "coarsen the mesh". Measured on the reported file, against
the 79,055-unknown standard that refused:

| change | standard N |
|---|---|
| as reported | 79,055 |
| `EdgeMesh` off | **79,055 — unchanged** |
| Auto off, 6 cells/λ, 2 cells across width, edge mesh off (DUT N 1,144 → 290, a **4×** cut) | 13,723 — **still refuses** |
| P2's 558.8 µm pad narrowed to the 254 µm trace width | 31,644 — **still refuses** |
| **lower band edge 100 MHz → 1 GHz** | **fits** |

The binding input is the BOTTOM OF THE SWEEP, which the message never mentioned. `SuggestDeltas`
sizes each separation at `TargetElectricalDegrees` (60°) of electrical length at its own sub-band's
geometric mean, so length ∝ 1/f_lo, and the separation COUNT steps with the band ratio
(`DesignBandRatioPerSeparation` = 4). At 100 MHz – 6 GHz that is **3 separations and a 161.5 mm
longest standard — 42× the 3.8 mm DUT.**

`PlanarCalibration.LongestStandardLengthM` and `StartFrequencyThatFits` now compute this, and
`PlanarSolve.BandEdgeRemedy` prints it: the band, the separation count, the longest standard's
length, and the band edge that would fit, rounded UP onto the 1-2-5 ladder. The mesh remedy is still
mentioned, but as the weak one it is, with the reason.

`StartFrequencyThatFits` scans UPWARD rather than bisecting — the separation count is a step function
of the band ratio, so the predicted size falls in jumps and a bisection can settle above the first
frequency that would have worked. It scales from the standard that was ACTUALLY built (N per metre)
rather than predicting from scratch, and takes only RATIOS of `LongestStandardLengthM`, which is what
cancels `BuildLine`'s end-run length floor instead of leaving it in an absolute estimate.

### 5. `CalibrationUsable` read 1 for a run that calibrated nothing

In the published `.npy`, `Gamma`, `Zc`, `Eeff`, `Cpul`, `AttenDbPerM` and `CalElectricalDeg` were NaN
at all 11 frequencies — and `CalibrationUsable` was **1.0 at all 11**. That is the one flag a reader
checks before trusting the file, and it is how this survived inspection.

It asked only whether the FREQUENCY was above zero. LF1 left it that way deliberately, in a comment
saying re-pointing it was "a separate decision about a cube people already read". This is that
decision. It now also asks whether anything was calibrated, which is what the cube's name implies.
`RawSolveAndCalibrationRemedyTests` asserts BOTH directions in one run pair — a fix returning NaN
unconditionally would pass the half that matters most and silently break every de-embedded run.

### 6. STILL OPEN — low frequency is blocked by HOW THIS KERNEL DE-EMBEDS, not by its field solve

Worth being precise, because LF1-LF3 (2026-09-13) had just made the low-frequency *field solve* work
and this looks like a regression of it. It is not. The DUT solves fine at 100 MHz: the same fixture
de-embedded gives S21 = −0.11 dB, ε_eff = 2.45, Z_c = 99 Ω. ~~and Z_c and ε_eff agree with the
closed-form microstrip values (106 Ω, 2.777) to 0.1%.~~ **That last clause is wrong on its own
numbers and was corrected at §QSC**: 99 against 106 is 6.6 % and 2.45 against 2.777 is 12 %, not
0.1 %. The 0.1 % figure appears to have been carried over from a higher-frequency point. §QSC §3
measures where the discrepancy comes from — it is the MEASURED value that is wrong at the bottom of
the band, and it moves toward the quasi-static one as the mesh is refined — and the same trace
calibrated quasi-statically reads ε_eff = 2.787 and Z_c = 105.7 Ω, i.e. 0.4 % and 0.3 %. **What does not fit is the calibration
standard.**

**And that is an architectural choice, not a law of MoM.** Owner's observation, and it is correct:
low-frequency planar structures are not normally hard. The usual approach to an edge port on a
uniform feed is a QUASI-STATIC port calibration — solve the feed's transverse cross-section for its
per-unit-length L, C, R, G, get γ and Z_c analytically, and subtract a known electrical length. That
cost is set by the cross-section only. It does not scale with wavelength, so it has no
low-frequency wall at all.

circuitRF instead MEASURES γ from two full-wave line standards (D5/D6). That is a real advantage at
the top of the band — it captures dispersion, the port's own discontinuity and higher-order effects
without assuming quasi-TEM — but it buys that with a cost that scales as 1/f_lo, because the two
lines' length difference has to be a measurable fraction of a wavelength. **The low-frequency wall is
the price of the measured calibration, and nothing else.** At 100 MHz the wall is a 161.5 mm standard
against a 3.8 mm DUT.

The good news is that the machinery for the standard approach is already in this repository:
`RlgcExtractor.Extract` is kernel A's per-unit-length cross-section solve, and
`PlanarDeembed.CapacitancePerMetre` already runs an electrostatic solve on a standard for
Z_c = γ/(jωC_pul). What is missing is using a quasi-static γ at the bottom of the band instead of a
measured one.

**Two candidate fixes, in the order I would take them:**

**(a) A quasi-static port calibration below a crossover frequency. BUILT — see §QSC below, which
supersedes every sentence that used to be in this paragraph.** It removes the wall rather than moving
it: the reported file now runs from 100 MHz, and its port 2's longest standard went from **79,055
unknowns to 3,289** while its short standard stayed bit-identical at 1,526.

Three things that were written here as a sketch and that §QSC settled by measurement, kept because
they are the things a reader of this paragraph would otherwise re-derive wrongly:

- **Supplying γ does NOT by itself remove the long standards.** γ is already an input to
  `PlanarDeembed.SolveErrorBox`, and the error box still needs two lines — one line gives two complex
  equations for three complex unknowns. What a known γ buys is that **Δℓ need not be ELECTRICALLY
  long**, because the 20°-160° usable interval protects the γ EXTRACTION and nothing else.
- **The supplier is the STANDARD's own electrostatics, not `RlgcExtractor`** — R-qsc-2, decided on
  four structural grounds in §QSC §2 rather than on the numbers, which is where the sketch above
  guessed wrong by naming kernel A first.
- **The a₂₂ sign margin does not collapse as Δℓ shrinks; it collapses as Δℓ GROWS.** The worry
  recorded above is real but points the wrong way — measured at a few hundred all the way down to one
  bulk cell, and at **25** when the separation is long enough to reach βΔℓ = nπ. §QSC §4.

**(b) Mesh each standard for ITS OWN sub-band. NOT taken, and (a) removed the need for it** — the
24× it estimated below is the same order (a) actually delivered, for a change that would have had to
re-open D4's verbatim rule. Kept as a measured decomposition, not as a plan.
Cheaper to implement, and it does not remove the
wall, only lowers it by roughly an order. A measured decomposition (Fr4Line fixture, DUT N = 144,
bulk cell 939.4 µm):

| f_lo | longest standard | N | cells | its electrical length AT f_lo |
|---|---|---|---|---|
| 100 MHz | 161.5 mm | 3,085 | 1,638 | **31.3°** |
| 500 MHz | 40.3 mm | 892 | 477 | 39° |
| 1 GHz | 27.2 mm | 654 | 351 | 52.6° |
| 2 GHz | 16.8 mm | 467 | 252 | 65.1° |

- **NOT "the standard is over-meshed".** Its longitudinal fill is the DUT's bulk cell repeated, which
  works out at ≈ 33 cells/λ at 6 GHz against a setting of 20 — only ≈ 1.6× loose. Coarsening it buys
  1.6×, not the 7× needed. (I assumed this was the answer before measuring; it is not. Measure the
  decomposition before choosing between "too many cells" and "too long a line".)
- **The real inefficiency: every standard is meshed for the GLOBAL top frequency, but each one only
  ever SERVES its own sub-band.** `SelectSeparation` picks, per frequency, the separation whose βΔℓ
  is nearest √(20·160) ≈ 56.6°, so the 161.5 mm standard exists solely to calibrate the bottom
  ≈ 100–400 MHz — where λ_g ≈ 0.93 m and it is a third of a wavelength. It is meshed with ~172
  longitudinal cells; at 20 cells/λ for ITS OWN sub-band it needs about **7**. `BuildSet` →
  `BuildLine` takes no frequency at all, which is why.

Estimated ≈ 24× on that standard (79,055 → ≈ 3,300 on the reported file), which clears the ceiling
with room. Two honest caveats before anyone implements it: D4's verbatim rule must still hold for the
TRANSVERSE gridlines and for the end runs (that is where the port's evanescent field lives) — only the
middle fill is free; and the jump from fine end-run cells to a coarse middle needs grading, since the
de-embedding peel divides by a₂₁ ∝ ω and the standard's own discretisation error propagates. It needs
a measured convergence check, not just the code change.

~~Separately still open: at a 1 GHz lower edge the reported file clears the ceiling and then fails
GMRES — 400 iterations to a relative residual of 1.36e-7 against a 1e-8 tolerance. Different problem,
not triaged here.~~ **CLOSED at §QSC, and it was never a different problem.** The `.cem` never asked
for the accelerator; `PlanarKernel` turned it on by itself in the
`catch (PlanarAcceleratorWouldFitException)` around `PlanarSolve.Run`, because a CALIBRATION STANDARD
had crossed the 5,000 dense ceiling. The DUT is N = 1,144 and was always comfortably dense. With the
standards sized quasi-statically the largest is 3,289, nothing crosses the dense ceiling, AIM never
engages and there is no GMRES to fail — checked on the run rather than assumed (§QSC §6).

### Gates

`tests/Engine.Tests/Mom/RawSolveAndCalibrationRemedyTests.cs` (the source scan, the band-edge
arithmetic, the `CalibrationUsable` pair, and the raw-is-an-open contrast) and
`tests/Ui.Tests/Em/PortClearanceRefusalTests.cs` (the legacy `.cem` warns and runs de-embedded; the
field is dropped on save; no `Deembed` property survives on either `EmSetup` or the view model).
`PlanarFeedClearanceTests.TheRefusalNamesThePortTheDistanceAndTheOneWayOut` was re-pointed — it used
to REQUIRE the raw-solve recommendation to be present.

## CL3 — the metal is a real conductor by default (2026-09-14)

`docs/sonnet-briefs/brief-conductor-loss-3-default-and-gate.md`. CL1 built the surface-impedance term
and left it behind a null `PlanarFillSettings.ConductorLoss`; CL2 read it back out as `P_conductor`.
**Neither changed a number anyone runs.** This brief flips the default, absorbs the consequences, and
puts a gate under the result.

**The brief planned for a wide re-bless — "roughly 90 files under `tests/Engine.Tests` touch
`Planar*`, and every golden taken on a structure with finite σ moves". TWO TESTS MOVED, both in one
file.** §6 is the reason and it is worth reading before the next brief in this area plans a re-bless
from a file count.

### 1. Milestone 0 — where the quasi-static path's γ gets its conductor term, MEASURED

This gated the flip and it is the only part of the brief that could have stopped it. `RESOLVED.md`
§QSC shipped saying *"the metal is a PERFECT CONDUCTOR here, exactly as the full-wave fill is by
default"*, and below a per-stack crossover a port is calibrated from a γ supplied by the standard's
own electrostatics. **On the shipped MMIC technology the crossover is 26.07 GHz, so that is not a
corner of the band — it is all of it**, and α_c is 92-99 % of a GaAs line's loss. Turning the fill's
term on and leaving that γ lossless would have published an α four times too small, smooth and
plausible, at every frequency anyone runs on the substrate class this series exists for.

**The instrument: α_c as kernel B's own two LOSSY standards see it** — the lossy two-line extraction
minus the PEC one, which is §CL1's own construction. Both candidate suppliers were measured against
that, on the same standards, at four frequencies and four meshes.

| supplier | FR-4 overlap, 4 meshes | GaAs coarse | GaAs refined |
|---|---|---|---|
| **the standard's own static CHARGE** (shipped) | **0.963 – 0.990×** | **0.999 – 1.010×** | 1.059 – 1.147× |
| kernel A's Wheeler term | 1.32 – 1.76× | 1.52 – 1.72× | 1.20 – 1.37× |

**The static charge tracks the fill's own term AND MOVES WITH THE MESH THE WAY IT DOES.** Turning the
edge mesh on raises both by ~32 % on FR-4 (α_c 1.073e-2 → 1.421e-2 in the fill, 1.040e-2 → 1.371e-2
in the supplier); kernel A does not move at all across that change, because it is a different
discretisation of a different formulation. **That is the whole argument.** This is not a better model
of the metal — it is the SAME model the fill loaded, read off the same mesh, so the two agree across
the crossover by construction rather than by calibration.

    R   = Σ_levels Re(Z_s(ω, σ, t)) · [ ΔS_level · Δℓ / |ΔT|² ]
    S   = Σ_cells |q|²/A   (C²/m²)        T = Σ_cells q   (C)        Δ = long − short
    γ   = √( (R + jωL)(jωC) )

The derivation is the TEM identity `K = v_p·q_s`: `P′ = ½Re(Z_s)∫|K|²dx` and `P′ = ½R|I|²`, so
`R = Re(Z_s)·∫|q_s|²dx/(∫q_s dx)²` and v_p cancels. Differencing the two standards turns the surface
integrals into per-metre ones and cancels both end effects exactly — **one quantity over from D7's
own C_pul, off the same two solves.**

**It costs no extra solve at all**, which is the practical half of the decision. The charge VECTOR
was already being computed and summed away; `PlanarDeembed.StaticChargeComplex` is the existing body
with the summation lifted out of it, and `PlanarStaticAim.ChargeComplex` is the same lift on the
accelerated route. Both readings above are the sums of those, in the same order, so **every existing
caller is bit-identical**.

**Kernel A stays the ORACLE and is still not an input** — R-qsc-2's first bullet and `PlanarDeembed`'s
D7 rule, unchanged, and the brief's "Must NOT" reserved that decision for the owner if the
measurement had gone the other way. It did not: kernel A is also the LESS accurate of the two here.
Its 20-76 % over-read is not an error in kernel A — it is §CL1's measured 0.63 (FR-4) / 0.73 (GaAs)
single-sheet deficit seen from the other side.

**What this does NOT claim.** The static distribution is the UNLOADED one, so its edge crowding is
not regularised the way the loaded EFIE's is (series overview §1). That is where the refined-mesh
GaAs over-read of 6-15 % comes from, and it is reported rather than tuned. It is **bounded, not
divergent**, because the singularity is resolved on the same mesh the fill's own term is integrated
on — which is exactly why "the same mesh" is load-bearing and not a convenience.

**Third candidate, declined:** leaving γ lossless and saying so. Legitimate on the brief's terms, and
refused because §0's own arithmetic makes it a 4× error on GaAs rather than a caveat.

### 2. The flip, and the ONE place it lives

`PlanarSolve.Run` builds `ConductorLoss = PlanarConductorLoss.For(problem)` unless
`PlanarFillSettings.PerfectConductor` is set. **That is the only scope in which the flip can be
expressed**: σ and t live on the `PlanarProblem`, not on a mesh and not on a fill setting, so
`PlanarFillSettings.Default` cannot carry a real conductor and there is nothing to flip on it.
`PlanarKernel.Solve` funnels through `Run`, so the shipped path — the Simulate button, `circuitrf em`
and `EmRunService` alike — is covered by one line.

**`PerfectConductor` is a SECOND flag rather than "leave `ConductorLoss` null", and it had to be.**
Null used to mean PEC; it now means "not yet resolved". Those were the same state until this brief
and are not afterwards, and one nullable field cannot hold both. It stays permanently, on the pattern
of `UseSymmetricFactorization = false` and `UseRadialTable = false` — **every CL1 and CL2 accuracy
figure is a comparison AGAINST it**, and a measurement whose reference cannot be reproduced is not a
measurement. It reaches no `.cem` key and no UI control (series overview §5).

**A caller that supplies its own `ConductorLoss` keeps it**, which is the seam every CL1 gate drives
the fill through and is how a test compares two metals on one mesh.

### 3. THE ONE TRAP, and it cost a gate before it was found

**A metal DECLARED perfect on the stackup and a metal asked to be perfect with the flag are the same
physical claim, and they were not producing the same bits.** `PlanarQuasiStaticLine.GammaAt` has two
spellings — `jω√(LC)` when there is no conductor term and `√(jωL·jωC)` when there is — which are
identical mathematically and **not to the last bit**. A declared-PEC stackup still built a
`PlanarQuasiStaticConductor` whose R was identically zero, which put it on the second spelling and
moved the answer by an ulp. One ulp is exactly enough to stop the flag being an oracle.

`PlanarConductorLoss.IsPerfectOn(mesh)` is the fix and it is frequency-independent, because
`PlanarSurfaceImpedance`'s own PEC test is (σ ≤ 0, σ = +∞, t ≤ 0). **The general lesson: when a new
code path is added beside an old one for the same physical case, the question is not whether the two
agree to the tolerance of the physics — it is whether the OLD one is still taken.**

### 4. The triage, and every move accounted for

`dotnet test tests/Engine.Tests` — **2,392 tests, 1 m 26 s, 2 failed**, both in
`PlanarGroupSeparationTests`, both on one fixture (PCAL4's coupled pair on 0.9 mm FR-4, 35 µm copper).

| test | class | what moved |
|---|---|---|
| `AGroupedSweepThatCalibratesTodayIsBitIdentical` | **expected move** | 8 de-embedded S entries, **4.8e-4 to 2.8e-3 relative**. Re-blessed. |
| `TheSameFrequencyIsRefusedInOneSweepAndAcceptedInAnother` | **UNEXPECTED — a finding** | a run that REFUSED now publishes. §5. |

Nothing was load-dependent and nothing else in the suite moved. The re-blessed literals are asserted
as exact equality **beside the pre-CL3 ones**, which are now asserted against `PerfectConductor` — so
the re-bless is a two-sided gate that says the move came from the metal and not from something else
in the same commit, and the per-entry move is range-asserted at 1e-4 … 1e-2 rather than merely
recorded.

`tests/Ui.Tests/Em`: **698 tests green**, including the `.cem` round trip and `EmCliVerbTests`'
byte-for-byte comparison of the CLI `em` verb against `EmRunService.Run`. R-cl3-2 asked for the
provenance claim to be asserted rather than assumed and
`tests/Ui.Tests/Em/ConductorLossProvenanceTests.cs` now does: σ and thickness have been in
`EmSnpProvenance.GeometryHash` since L9d (`EmSnpProvenance.cs:219`), nothing had ever tested it, and
it matters now in a way it did not before — those two numbers used to ride along unused, so a cached
`.snp` taken with the wrong metal was still the right `.snp`.

R-cl3-4, the five heroes: **unchanged, and confirmed structurally rather than assumed** — no test
under `tests/Engine.Tests/{Linear,Nonlinear,HarmonicBalance,Loadpull}` references `Planar` or `Mom`
at all, and all of them passed in the run above.

### 5. The unexpected move: CONDUCTOR LOSS MAKES A CALIBRATION GROUP'S MODES MORE SEPARABLE

PCAL4's modal error box is extracted from the eigenvectors of the two standards' cascade, and
`ModeSeparationFloorDegrees` refuses a run whose modes cannot be told apart. **The measured
separation is |Δγ|·Δℓ, and γ is complex:** the even and odd modes carry different current
distributions, so with real metal they differ in α as well as in β and the product grows.

Measured on the fixture at 200 MHz, PEC against 35 µm copper, over a ladder of bands:

| band | PEC | real copper |
|---|---|---|
| 100 MHz – 1 GHz | 1.63° | 1.31° |
| **200 MHz – 1 GHz** | **0.405° — REFUSED** | **0.56° — publishes** |
| 200 – 800 MHz | 1.16° | 1.29° |
| 200 – 600 MHz | 0.82° | 0.96° |
| **200 – 400 MHz** | 0.255° — refused | **0.405° — refused** |
| 200 – 300 MHz | 0.305° — refused | 0.081° — refused |

**It is not monotone**, which is the part worth keeping: loss adds |Δα|·Δℓ but it also changes Δℓ's
own selection and the two modes' β, so a band can move either way. The gate's own fixture was
re-pointed one rung narrower (200 – 400 MHz, which refuses at 0.405° with real metal) rather than
pinned with `PerfectConductor` — **the brief's own rule, and the right one: a gate that flipped the
PEC flag would be testing a refusal no user can reach.**

**Reported, not fixed:** `PlanarSolve.BandEdgeRemedy`'s refusal text quotes three measured constants
("the same 200 MHz point reads 0.26° over a 200 MHz - 800 MHz band and 1.15° over a 200 MHz - 400 MHz
one"; "turning the edge mesh on moved that same point from 0.19° to 2.94°"). Those were measured at
PCAL7 on `testdata/portcal/coupled-pair` — a different geometry from this unit fixture — with PEC
metal, and the table above says such numbers move. **They are a user-facing remedy carrying measured
sizes, so they are now describing a state the code is not quite in.** Re-measuring them needs a run
of that board through `EmRunService`, which is a `tests/Ui.Tests` job and outside this brief.

### 6. WHY THE RE-BLESS WAS TWO TESTS AND NOT NINETY

The brief planned this as a wide re-bless and it was not one. Three reasons, and they are worth
knowing because the next brief in this area will make the same estimate:

- **Most `Planar*` tests never reach `PlanarSolve.Run`.** They build a `PlanarFillSettings` and call
  `PlanarFill.Fill` or construct a `PlanarSolveContext` directly, which is white-box by design — the
  flip lives in `Run` because that is the only scope holding a `PlanarProblem`, so those tests stay
  on PEC metal and SHOULD.
- **`PlanarSolve.Run`'s two-argument overload builds an all-PEC dummy problem**
  (`new PlanarConductorLayer("Metal", [], 0, 0)`), so every caller of it is PEC by construction.
- **The move is 1e-3-scale on FR-4 and the surviving goldens are tolerance-based.** Only
  `PlanarGroupSeparationTests` asserts exact equality against recorded literals.

**The corollary is uncomfortable and should be said: the suite's coverage of the shipped path with
real metal was thin, and this brief is why it is not any more.** The gates below run the whole
de-embedding rather than the fill.

### 7. R-cl3-3 / milestone 5 — the phase gate, one sentence per starter

Read from the **published, de-embedded S₂₁ of a uniform 50 Ω line of known length** — never from the
calibration's own γ (R-cl3-6), because on the MMIC starter all three frequencies are below the
crossover and the calibration's γ is a SUPPLIED one, so reading α off it would be reading this
brief's own arithmetic back to itself. Ground held PEC in BOTH kernels, since CL4 has not run. Two
runs of one sweep on one mesh differing only in `PerfectConductor`, so α_c is a difference and every
bias the two share cancels.

**FR-4 starter — 1.6 mm, 35 µm Cu, w = 3020.28 µm, 1.5 λ_g line, N = 954:**

| f | α PEC | α lossy | α_c (B) | α_c (A) | B/A | total B/A | path |
|---|---|---|---|---|---|---|---|
| 2 GHz | 6.793e-1 | 7.061e-1 | 2.680e-2 | 3.668e-2 | 0.731 | **0.986** | quasi-static |
| 10 GHz | 4.071 | 4.124 | 5.217e-2 | 8.194e-2 | 0.637 | 1.185 | measured |
| 20 GHz | −21.34 | −21.27 | 7.322e-2 | 1.159e-1 | 0.632 | −3.077 | measured |

**MMIC starter — 100 µm GaAs, 3 µm Au, w = 70.72 µm, 1.0 λ_g line, N = 586:**

| f | α PEC | α lossy | α_c (B) | α_c (A) | B/A | **total B/A** | (PEC) | path |
|---|---|---|---|---|---|---|---|---|
| 2 GHz | 1.283e-1 | 1.796 | 1.668 | 1.996 | 0.836 | **0.852** | 0.061 | quasi-static |
| 10 GHz | 5.890e-1 | 3.452 | 2.863 | 3.826 | 0.748 | **0.787** | 0.134 | quasi-static |
| 20 GHz | 1.160 | 5.531 | 4.371 | 5.287 | 0.827 | **0.863** | 0.181 | quasi-static |

**The band, stated from the measurement: α_c out of kernel B is 0.63 to 0.84 of kernel A's.** It is
not a tuned number — at 10 GHz it reads **0.637 (FR-4)** and **0.748 (GaAs)** against §CL1's
independently measured **0.63** and **0.73** for the same quantity. CL1 measured it by extracting γ
from two bare standards; this measures it through the whole shipped de-embedding. **The two
instruments share no algebra and they agree**, which is what makes the 16-37 % under-read the
single-sheet model's own structural limit rather than an error somewhere in this brief.

**And the headline, on the substrate class the series exists for: a MMIC line's TOTAL α goes from
0.06-0.18 of kernel A's to 0.79-0.86.**

**FR-4's total-α column stops at 2 GHz and the reason is pre-existing.** At 10 and 20 GHz a 1.6 mm
FR-4 line is h/λ₀ = 0.05 and 0.11; it radiates, its ports couple through a surface wave, and the
two-line calibration's error scales as f² (`CLAUDE.md` §5). At 20 GHz the peel over-corrects far
enough that |S₂₁| > 1 and α comes out NEGATIVE — **identically so on the PEC oracle, measured at
three line lengths (0.5 / 1.5 / 2.5 λ_g) and two mesh densities (cells/λ 20 and 30)**, so no amount
of refinement moves it and it is not this brief's. The DIFFERENCE is still good there, which is
§CL1's own sentence ("the absolute α of a PEC FR-4 line out of this route is not a usable number;
the difference is") measured a second time through a different instrument.

### 8. R-cl3-5 — the overlap gate compares α and arg Z_c now

`QuasiStaticPortCalibrationTests.TheQuasiStaticPathAgreesWithTheMeasuredOneWhereBothRun` is the only
test in the suite that puts the two calibration paths side by side on one geometry, and after this
brief it is the only thing that would notice an α that steps at the crossover. It compared β, |Z_c|
and max|ΔS| and **not α**, because under PEC metal both paths carried the identical dielectric α and
the comparison was vacuous.

**Its harness had to be given real metal first**, and that is a finding rather than a chore: it
builds its calibrator by hand so it can name a separation plan, so it passed `null` fill settings and
would have gone on measuring two lossless paths while every real run moved.

FR-4 6 mm line, 500 MHz – 3 GHz, both quantities as the DIFFERENCE against a PEC run of the same
path so the shared dielectric term and extraction bias cancel:

| f | Δβ | **Δα_c** | Δ|Z_c| | **Δ arg Z_c** | max\|ΔS\| |
|---|---|---|---|---|---|
| 500 MHz | +0.982 % | **−3.069 %** | +0.982 % | **+0.034°** | 5.59e-3 |
| 750 MHz | +0.151 % | −2.328 % | +0.151 % | +0.020° | 2.40e-3 |
| 1 GHz | −0.172 % | −2.047 % | −0.172 % | +0.019° | 8.10e-4 |
| 1.5 GHz | −0.366 % | −2.152 % | −0.366 % | +0.034° | 3.96e-3 |
| 2 GHz | −0.703 % | −1.797 % | −0.704 % | +0.054° | 9.40e-3 |
| 3 GHz (≈ crossover) | −1.196 % | −1.051 % | −1.198 % | +0.110° | 2.28e-2 |

**Thresholds taken from that spread with margin (8 % and 0.25°), not chosen first.** The quasi-static
supplier reads a little LOW throughout, which is the static charge distribution against the solved
one, and the sign is stable.

**§0's second consequence closed for free.** `PlanarDeembed.CharacteristicImpedance` is D7's
`γ/(jωC′)`, so a γ carrying R gives Z_c its `√(1 + R/(jωL))` correction with no new code. Measured on
the GaAs starter's quasi-static line, arg Z_c: **−0.054° → −0.681° at 2 GHz**, −0.054° → −0.269° at
10 GHz. The magnitude moves 0.01 %, which is why the old |Z_c|-only gate would never have seen it —
**the larger of the two errors was the one nothing looked at**, exactly as §0 said.

### 9. Milestone 4 — passivity and reciprocity got stricter

Gated on the RAW matrix as well as the de-embedded one, deliberately: the de-embedded answer on this
stack is already over 1 at the bottom of the band for a reason that predates the series (D6's peel
divides by a₂₁²), so a de-embedded-only test would be measuring the peel. The raw σ_max is a
statement about the FILL, which is the only thing CL1's term touches.

| f | σ_max(RawS) PEC | with real copper | \|S₁₂−S₂₁\| PEC | lossy |
|---|---|---|---|---|
| 2 GHz | 0.993634026 | 0.993625006 | 2.86e-17 | 3.88e-17 |
| 6 GHz | 0.976628000 | 0.976486210 | 6.94e-16 | 4.81e-16 |
| 10 GHz | 0.925436599 | 0.924835293 | 1.89e-15 | 1.46e-15 |

**σ_max only ever falls**, which is the structural statement (real metal absorbs), and reciprocity
does not degrade at all — Z_s enters the operator symmetrically, against the same Gram, mirrored with
the rest of the fill.

### 10. Milestone 8 — the cost, and it is unmeasurable as predicted

FR-4 hero at the shipping mesh, N = 552, **Debug** (which is what `dotnet test` builds — CL1's own
figures are Debug too, so the two are comparable). Best of 5 for the fill and factor, best of 3 for
the sweep:

| | fill | factor | 5-point de-embedded sweep |
|---|---|---|---|
| PEC | 226.7 ms | 304.8 ms | 7.15 s |
| real copper | 229.7 ms | 305.9 ms | 6.95 s |

+1.3 % on the fill, +0.4 % on the factor, and the SWEEP came out **faster** with the term on — i.e.
all three differences are run-to-run noise, and the sign of the third says so better than any
tolerance would. The brief's own tell ("if it is measurable, the Gram matrix is being rebuilt per
frequency") does not fire: `PlanarFillCores.GramBuilt` reads False on the PEC run and True on the
lossy one, once per mesh, and CL1's `Cost_TheGramIsBuiltOncePerMesh_NotPerFrequency` counter still
holds.

### 11. Gates

- `tests/Engine.Tests/Mom/ConductorLossDefaultTests.cs` — **6 routine tests (~12 s) and 2 tagged
  `Category=Benchmark`** (R-cl3-3's two starters, 31 s together; measured, and tagged by the repo's
  mechanical ~5 s rule rather than by preference).
  - **R-cl3-0** milestone 0's decision as a gate, three meshes: the static supplier within
    0.90-1.20× of the fill's own α_c, kernel A over-reading by more than 1.15×, **and the static one
    the closer of the two at every point** — which is the comparison the decision was actually taken
    on, rather than two separate tolerances that could both pass on the wrong answer.
  - **R-cl3-3** the phase gate, tabulated above.
  - **R-cl3-4** passivity and reciprocity, above.
  - `ThePerfectConductorFlagIsBitIdenticalToPecMetal` — the flag against a PEC-DECLARED stackup,
    exact equality through fill, standards, quasi-static γ, error box and renormalisation. Asserted
    against a second ROUTE rather than against a recorded literal, which is the stronger form of the
    same statement.
  - `TheQuasiStaticGammaCarriesTheConductorTerm` — structural: C, C₀ and ε_eff bit-identical between
    the two, R > 0, α up, |arg Z_c| up.
- `tests/Engine.Tests/Mom/QuasiStaticPortCalibrationTests.cs` — R-cl3-5, §8.
- `tests/Engine.Tests/Mom/PlanarGroupSeparationTests.cs` — the two re-blessed gates, §4 and §5.
- `tests/Ui.Tests/Em/ConductorLossProvenanceTests.cs` — R-cl3-2.

### 12. What was NOT measured, so nothing is read into the silence

- **No general-stack (MIM-4) or coplanar/widened/group standard was run through the new conductor
  term.** The arithmetic rides on the same `ModePotential`/`ModeWeight`/`FloatingPotential` C does
  and is split per LAYER for a multi-level standard, but "by construction" is not "measured" — the
  same sentence §QSC §9 wrote about the same code path. **One deliberate asymmetry inside it:** the
  mode WEIGHT is applied to Σq and NOT to Σ|q|²/A, because the weight combines two conductors'
  charges with a SIGN to pick out a mode, while the loss integral is a sum of squares over metal and
  every conductor the mode drives dissipates. A signed weight under a square would cancel the return
  conductor's own loss.
- **The accelerated (AIM) static route is written and compiles but no number here is on it.**
  `PlanarStaticAim.ChargeComplex` is the same lift as the dense one and every existing reading is a
  sum of it, so the shipped answers are bit-identical; the CONDUCTOR term over an accelerated
  standard is untested.
- **No thick-metal question was re-opened.** The 16-37 % under-read is the series overview §3's
  deferred decision and this brief measured it a second way rather than acting on it.
- **CL4 has not run**, so the ground plane is still PEC and the pages say so with the measured size.

## CL2 — `P_conductor` stops being an identical zero (2026-09-14)

`docs/sonnet-briefs/brief-conductor-loss-2-power-budget.md`. ANT-5 itemised where a driven port's
accepted power goes and had to book one of its four terms as a hard zero. CL1 made it computable;
this brief computes it, takes it OUT of the dielectric residual, and repairs the two notes that
described the state the code is no longer in.

### What was built

- **`PlanarConductorPower`** (new file) — `PlanarConductorLossInputs(Loss, Gram, Levels)`, the three
  things the integral needs carried as ONE object, plus `PlanarConductorPower(TotalW, SheetW,
  BarrelW)`. The sheet term is the quadratic form `½ Re(Z_s)·xᴴGx` against the SAME Gram the fill
  multiplied Z_s by — `PlanarFillCores.Gram`, not a rebuild — and the barrel term is the elementary
  `½Re(Z_barrel)|I|²` over each vertical basis, which has no Gram row at all. **One route, on
  purpose:** a second statement of the same number is how a factor of two gets in.
- **The residual moved with it.** `PlanarPowerBudget.For` now reports
  `P_dielectric = accepted − radiated − surface wave − conductor`.
- **`ConductorModelled`** on the budget, because the two zeros are different facts and the number
  cannot tell them apart: an all-PEC stackup reports 0 W with the term switched fully on.
- **Plumbing**: one optional `PlanarConductorLossInputs?` through `PlanarPowerBudget.For`,
  `PlanarMetricContext` and `PlanarSolve.FarFieldAt`, built once per run by a local function in
  `PlanarSolve.Run` (a local FUNCTION rather than a local, because `dut.Cores` is lazy and a PEC run
  must not be made to build cores it never needs). **Null is bit-identical to ANT-5.**

### The gate the brief did not ask for, and it is the strongest one here

R-cl2-3's stated premise — *the MMIC starter patch, where the conductor term dominates the
dielectric term by ~30×* — **is not what a power budget measures, and the ratio was nowhere near
30× on any fixture tried.** The overview's 30× is a ratio of per-unit-length ATTENUATIONS on a
MATCHED line, which weighs `∫R_s|J|²` against `∫G|V|²` with `|V|` and `|I|` locked together by Z₀.
This budget drives one port at 1 V into a structure whose other port is a transparent zero-volt gap,
so voltage and current are a standing wave and the same two mechanisms are weighted quite
differently. Measured, 3 µm gold on 100 µm GaAs, conductor ÷ dielectric:

| fixture | tanδ | conductor | dielectric | ratio |
|---|---|---|---|---|
| 70.72 µm line, 30 GHz | 6e-4 | 64.85 % | 25.66 % | **2.53×** |
| 70.72 µm line, 30 GHz | 2e-3 | 40.57 % | 53.49 % | 0.76× |
| 70.72 µm line, 10 GHz | 6e-4 | 32.56 % | 67.35 % | 0.48× |
| half-λ patch, 60 GHz | 6e-4 | 6.57 % | 3.65 % | 1.80× |

**So the sign check was re-drawn on tanδ = 0 with REAL metal**, where the conductor term is the only
absorber in the model and the dielectric residual must therefore vanish:

| fixture | N | accepted | conductor share | dielectric residual |
|---|---|---|---|---|
| GaAs line, 10 GHz | 24 | 449.8 nW | **99.73 %** | −1.71e-4 |
| GaAs line, 30 GHz | 59 | 9.074 µW | **87.23 %** | +6.85e-5 |
| GaAs patch, 60 GHz | 237 | 12.93 mW | 6.82 % | +2.40e-4 |

That is a gate on the conductor term's own **MAGNITUDE**, not merely on the sign of the arithmetic
around it — it is the residual of three independent routes (½Re(Y_jj) from the factorisation, ∫U dΩ
plus the pole residues from the spectral kernel, and this brief's quadratic form) and the conductor
term carries 87-99.7 % of it. The milestone-2 failure the brief warns about — the term added BESIDE
the residual rather than taken out of it — reads as a residual of −87 % to −99.7 % of accepted here,
not as a fraction of a per cent. **The residual's SIGN at tanδ = 0 is noise** (the first row is
negative), which is why the gate is two-sided at the fill's own documented accuracy (1e-2 on 100 µm
GaAs) rather than at `> 0`.

### R-cl2-2 — the two routes, and the gap is the reference route's quadrature EXACTLY

The shipped quadratic form against a direct `∫Re(Z_s)|J|²dS` over `PlanarCurrentDensity.Compute`'s
map (test-only; it does not ship). The map is the current at each cell's CENTRE, so the direct sum
is a **midpoint rule**, and on one cell carrying rooftops I_m and I_n the exact integral exceeds it
by exactly `Δ·|I_m − I_n|²/(12·L)`. Summing that closed form over the mesh reproduces the observed
gap to **3.5e-15 / 6.7e-14 / 1.3e-15** relative (GaAs line, FR-4 patch, FR-4 line) — so the two
routes differ by the reference route's quadrature and by **nothing else**. A dropped off-diagonal, a
missing ½ or a wrong Z_s is not expressible in that form.

The gap itself is 8.91 % / 3.24 % / 0.85 % on those three, and **it falls like √(cells), not like
cells²** — measured on the GaAs line at cells/λ 10 / 20 / 40 / 80 (16 / 20 / 36 / 72 cells): 8.91 /
7.07 / 4.45 / 3.16 %. The deficit is dominated by `|I_m − I_n|²` at the strip's own RIM, where the
transverse current's 1/√d edge behaviour is never resolved by a uniform mesh, so the jump between
neighbouring coefficients does not shrink the way it does mid-strip. **That is a property of the
test-only reference route**, and it is why R-cl2-2's band is per cent rather than per mille.

### R-cl2-4 — the efficiency moved the predicted way

Half-guided-wavelength patch, edge-fed at x = 0, N = 237, with and without the fill's term. The
quasi-static port-calibration crossover is stated beside each because `P_accepted` comes through the
port even though nothing in the conductor term does:

| starter | f | crossover | η_rad PEC | η_rad with Z_s | Δ | conductor |
|---|---|---|---|---|---|---|
| FR-4 1.6 mm, 35 µm Cu | 2.4 GHz | 3.048 GHz (**below**) | 41.210 % | 40.931 % | **−0.279 pp** | 0.67 % of accepted |
| GaAs 100 µm, 3 µm Au | 60 GHz | 26.07 GHz (**above**) | 67.854 % | 63.734 % | **−4.121 pp** | 6.06 % of accepted |

### The notes, and one deliberate deviation from the brief

Milestone 3 says `BoundNote`'s clause (2) is *retired* when the term is real. **It could not be
retired outright**, because `PlanarFillSettings.ConductorLoss` still defaulted to OFF until CL3: a
shipped run at the time genuinely had PEC metal, and deleting the clause would have been the same
defect inverted — a note describing a state the code is not in, half the time. So:

> **CL3 (2026-09-14) flipped that default and the per-run construction is why nothing here had to
> change.** `BoundNoteForRun` already asks the RUN which state it is in, so the shipped note simply
> stopped taking the PEC branch; the branch itself stays reachable and correct, because
> `PlanarFillSettings.PerfectConductor` and an all-PEC stackup both still reach it. Deciding the
> sentence per run rather than per default is what made a flip of the default a no-op here.

- **`ConductorNote`** (the const the registry carries) states what the term IS, that it is exactly
  zero when the metal is a perfect conductor **by either route**, and what the sheet model cannot
  carry — overview §3's thickness-blindness and CL1's 0.63 (FR-4) / 0.73 (GaAs) under-read against
  kernel A, in a sentence.
- **`BoundNote`** keeps clause (1) and defers clause (2) to the run.
  **`PlanarPowerBudget.ConductorBoundClause`** is that clause, per run: ANT-5's own sentence
  (unchanged, plus the MMIC 92-99 % figure beside the FR-4 6.5/3.0/2.1 %) where the metal is
  perfect, and where the term is live the old correction is **retired by name** and replaced by
  CL1's measured residual under-read, which is smaller and in the same direction rather than absent.
  `BoundNoteForRun` is the two together, and is what `PlanarSolve` prints.
- **`Caption`** says which zero a zero is (`— NOT MODELLED, the metal is a perfect conductor in this
  run`) and breaks the conductor term into sheet + barrels whenever there are barrels.

### Traps found

- **A midpoint-rule reference route is not a second opinion until you can predict its error.** The
  first version of R-cl2-2 gated at 10 % and a refinement ladder was written to argue the gap was
  second order. It is not — it falls like √(cells) — and the ladder would have shipped a claim the
  measurement refutes. The closed form is what turns the same measurement into evidence.
- **The lossless power balance is a magnitude gate on any term that dominates it**, and is far
  sharper than the sign check the brief asked for. Worth reaching for whenever a new loss term lands
  in this budget.
- **Two zeros that are not the same fact.** `ConductorW == 0` means "perfect metal" and "the fill
  had no term" alike, and only the second one makes the efficiency read high. The bool is carried
  rather than inferred for the same reason `PlanarConductorLoss` is a nullable data object rather
  than a bare bool in CL1.
- **`dut.Cores` is LAZY and the budget's inputs must not force it.** Reading `dut.Cores.Gram` into a
  plain local at the top of `PlanarSolve.Run` would build the geometric cores on every run,
  including one that refuses before it ever fills.

### Gates

`tests/Engine.Tests/Mom/PlanarConductorPowerTests.cs`, **14 tests, ~5 s, all in the routine tier** —
nothing here crosses the ~5 s `Category=Benchmark` threshold, because every fixture is a single
frequency on a coarse mesh and no gate needs a calibration.

- **R-cl2-1** the lossless balance with the term switched ON and the metal declared PEC (σ = +∞,
  the spelling CL1 found produces a NaN if the three spellings are not asked in one place), on
  1.6 mm FR-4 and an 8 mm εᵣ = 2.2 slab: conductor **exactly** 0.0 (sheet and barrel), residual
  5.6e-5 / 1.6e-4, surface wave carrying 19.9 % / 32.0 %.
- **R-cl2-2** the two routes, three fixtures; **R-cl2-2b** the barrel arm on a two-level via mesh
  (N = 101, 3 vertical bases: 1.224 µW sheet + 5.889 nW barrels, and the two arms are exact);
  **R-cl2-2c** the stored upper triangle counted twice off the diagonal and once on it, against a
  HAND-BUILT current vector so it fails on the arithmetic alone, plus the phase-rotation invariance
  that says only Re(Z_s) is read; **R-cl2-2d** the closed-form midpoint deficit above.
- **R-cl2-3a/3b** as tabulated above, including the budget closing as an identity to 1e-12.
- **R-cl2-4** as tabulated above, plus the two notes describing the state each run is in.
- `PlanarMetricsTests.PowerConductorIsPresentAndZero_WithItsNote` was re-pointed — it pinned
  "IDENTICALLY ZERO", which is the sentence CL2 exists to remove. It now asserts the metric is
  still present, that a run with no conductor model reports the zero AS not-modelled, and that the
  note carries both the definition and the model's own limits.
- The whole of `tests/Engine.Tests/Mom` is **1,086 passed** (1 m 15 s) and `Firewall.Tests` passes;
  `PlanarConductorPower`'s two internal-invariant exceptions are on
  `tests/Firewall.Tests/user-facing-text-allowlist.txt`.

## CL1 — the surface-impedance term in the full-wave fill (2026-09-14)

`docs/sonnet-briefs/brief-conductor-loss-1-surface-impedance.md`. Kernel B's metal was a PERFECT
CONDUCTOR: `PlanarConductorLayer.SigmaSm` and `ThicknessM` reached `PlanarProblem`, entered the
provenance hash, and were never read by the fill. On the MMIC technology this repository ships that
omits 92-99% of the line's loss. This brief reads them, behind
`PlanarFillSettings.ConductorLoss`, **default off**.

### What was built

- **`PlanarSurfaceImpedance`** (new file) — `Sheet(σ, t, ω) = (η_c/2)·coth(γ_c t/2)` for a sheet
  carrying current on both faces, `Barrel(σ, ℓ, A, P, ω) = (ℓ/P)·η_c·coth(γ_c·A/P)` for a via post,
  and `SkinDepthM`. Derivation and both limits in the file header.
- **`PlanarGram`** (new file) — `⟨f_m, f_n⟩` over the rooftop basis, symmetric sparse CSR over the
  upper triangle, built once per mesh off `PlanarFillCores.Gram` (lazy, so a run that never asks for
  loss pays nothing at all — `GramBuilt` is the counter).
- **`PolygonIntegrals.AreaSecondMoments`** — `∫∫u²`, `∫∫uv`, `∫∫v²` over a polygon by the same signed
  fan `Area`/`AreaMoment` already use. The only new closed form in the brief.
- **Three fill seams, one function.** `PlanarFill.AddSurfaceImpedance` after the direction blocks and
  before `MirrorLowerToUpper` in both `Fill` and `FillMultiLevel`; `PlanarEntryFill.At` adds
  `PlanarFill.SurfaceEntry`, which calls the same `SheetTerm`. In the AIM path the term reaches
  `nearExact` only — never `AimEntry` — because Z_s is not a Green's-function interaction and the
  grid product must not claim it. `PlanarAimBordered.FillBorder` adds the barrel on the Z_zz
  diagonal.
- **`PlanarDcSolve.ViaSigmaFor` is now public** and the DC point's private `ViaSigma` delegates to
  it. There is one via-conductivity resolution, not two.

### The result that decides the series: R-cl1-9's edge convergence

α_c of a uniform, re-bisected 50 Ω line at 10 GHz, tanδ = 0, γ from the two-line extraction, the PEC
run's α subtracted as the floor. Ground held PEC in BOTH kernels (kernel A via
`EmGroundPlane(0, ∞)`), so kernel A's sum over surfaces isolates the strip exactly.

**FR-4, 1.6 mm, 35 µm Cu, w = 3020.28 µm.** Kernel A: R_strip = **8.194 Ω/m**, Z₀ = 50.00 Ω,
ε_eff = 3.2999, α_c = 8.1943e-2 Np/m = 0.0071 dB/cm.

| EdgeCells | N | α_c Np/m | dB/cm | ÷ kernel A | step |
|---|---|---|---|---|---|
| 0 | 148 | 4.1076e-2 | 0.0036 | 0.5013 | — |
| 2 | 292 | 5.1480e-2 | 0.0045 | 0.6282 | 25.33% |
| 3 | 365 | 5.1819e-2 | 0.0045 | 0.6324 | 0.66% |
| 5 | 493 | 5.1038e-2 | 0.0044 | 0.6228 | 1.51% |
| 8 | 715 | 5.1546e-2 | 0.0045 | 0.6290 | 0.99% |

**GaAs, 100 µm, 3 µm Au, w = 70.72 µm.** Kernel A: R_strip = **382.571 Ω/m**, Z₀ = 50.00 Ω,
ε_eff = 8.0651, α_c = 3.8256 Np/m = 0.3323 dB/cm.

| EdgeCells | N | α_c Np/m | dB/cm | ÷ kernel A | step |
|---|---|---|---|---|---|
| 0 | 24 | 2.2264 | 0.1934 | 0.5820 | — |
| 2 | 142 | 2.8090 | 0.2440 | 0.7343 | 26.17% |
| 3 | 161 | 2.7947 | 0.2427 | 0.7305 | 0.51% |
| 5 | 262 | 2.7855 | 0.2419 | 0.7281 | 0.33% |
| 8 | 445 | 2.7816 | 0.2416 | 0.7271 | 0.14% |

**Both starters' kernel-A figures reproduce the series overview's own (8.19 and 382.56) to the digit
it quoted**, which is what says the two sides are measuring the same quantity.

**α_c CONVERGES.** Turning the edge mesh on at all is worth 25-26%; past that each rung moves under
1.6% (FR-4) and under 0.6% (GaAs), and GaAs is still falling monotonically at 0.14% from rung 5 to 8.
The log-divergence a PEC-current `∫R_s|J|²` would have is not present — loading the operator does
what the series overview said it would.

**And the loaded sheet lands at 0.63 (FR-4) / 0.73 (GaAs) of kernel A, converged.** That deficit is
not a convergence artifact and it is not tuned away. It is the single-sheet model's own structural
limit, stated in `PlanarSurfaceImpedance`'s header before it was measured: one unknown per location
cannot hold two independent face currents, and a real strip's loss includes sidewall current and a
substrate-side face carrying far more than its air-side one. Cross-checked against a uniform-current
line: on GaAs, Re(Z_s) = 1.4573e-2 Ω/sq over w = 70.72 µm gives R = 206.1 Ω/m, i.e. α = 2.061 Np/m —
so kernel B's loaded sheet computes an **edge-crowding factor of 1.35** where kernel A's thick strip
has **1.857** (the overview §3's own back-out). The ratio of those two is 0.727, which is the table's
own number. **Nothing was adjusted to make that come out.**

**Reading for the series:** the model is well posed and converges, so R-cl1-9's own stated
alternative — "comes back mesh-dependent, therefore there is a measured structural argument for thick
metal" — did NOT happen. What did happen is a converged 27-37% under-read against an independent
oracle, which is a calibration question or a thick-metal question, and is the owner's to decide at
CL3 rather than this brief's.

### Traps found

- **σ = +∞ is a third spelling of PEC, and it is the one that produces a NaN.** The rest of the
  kernel means PEC by σ ≤ 0 or t ≤ 0; kernel A spells a perfect ground as `EmGroundPlane(0, ∞)`, so
  an A-vs-B comparison hands the sheet an infinite conductivity as a matter of course. Then δ → 0 and
  η_c = (1+j)/(σδ) is ∞·0 = NaN — **not a small loss but a solve that produces nothing**, and R-cl1-4
  caught it as 302 moved bits on a 59-unknown line. `PlanarSurfaceImpedance.IsPerfect` is the one
  place all three are asked.
- **The coth needs a SMALL-argument branch as well as the large one, and the brief only named the
  large one.** `(e^{2z}+1)/(e^{2z}-1)` at z ~ 1e-6 is cancellation, not arithmetic: at t/δ = 1e-6 it
  returned an imaginary part 4.1e-11 of the real one where the physics says (t/δ)²/6 = 1.7e-13, i.e.
  pure noise — and the thin-sheet asymptote is exactly the limit the DC-continuity gate lives in. The
  Laurent series is used below |z| = 0.1; its first omitted term there is ~2e-14 against a value of 10.
  The large-argument branch (|Re z| ≥ 30) is the brief's, and it is a precision question rather than
  an overflow one: 105 µm copper at 40 GHz is t/δ = 318 and the naive form does not overflow until 710.
- **A HALF-WAVELENGTH two-line separation sits exactly on the extraction's singularity.** The
  two-line γ is acosh of a quantity whose distance from 1 scales with (γΔℓ)², so βΔℓ = 180° is
  degenerate. Measured on FR-4 at 10 GHz: the PEC line's extracted α came back at 0.551 Np/m — nearly
  seven times the conductor term being looked for — and the difference between the PEC and lossy runs
  was **NEGATIVE**, i.e. adding loss removed it. A quarter wavelength is the target.
- **…and the crude ε_eff = (εᵣ+1)/2 is not good enough to hit that target on FR-4.** It sized Δℓ at
  4.97 mm; the extraction came back βΔℓ = 245°, `Usable = false`, on the wrong branch. `Gamma`'s
  `expectedBetaDeltaL` argument exists for this, and kernel A's own ε_eff is what to feed it — it is
  the oracle the measurement is against anyway and is never an input to kernel B.
- **`PlanarCalibration.BuildLine` has a LENGTH FLOOR**, and two targets below it return two standards
  of the SAME length. `Gamma` refuses a zero Δℓ, so it surfaces — but nothing else would have noticed.
  Measure the long standard FROM the short one's actual length, not from the same wavelength scale.
- **The FR-4 PEC floor is larger than the conductor term and its SIGN moves** (−0.55, −0.14, −0.70,
  −0.21 Np/m across the edge rungs) while the DIFFERENCE is stable to ~1%. That is not a contradiction:
  the floor is a systematic bias of the extraction on a radiating 1.6 mm substrate, and it cancels
  almost exactly between two runs that differ only by a ~1e-4 relative perturbation of the operator.
  **The absolute α of a PEC FR-4 line out of this route is not a usable number; the difference is.**
- **A calibration standard's mesh hard-codes `LayerIndex = 0` and `layerName = "Metal"`**
  (`PlanarCalibration.cs:435` and `:387`). An index lookup for Z_s would therefore hand a Metal-2
  standard Metal-1's metal on a multi-level problem — silently, as a plausible loss figure. The sheet
  table is resolved by layer NAME first and by index second, which makes the two agree wherever the
  names do; it does **not** fix the case where `BuildLine` is left on its `"Metal"` default for a
  level named something else. Reported rather than fixed here: changing what `BuildLine` names its
  level is a calibration-path change, not a fill change.
- **Two same-direction rooftops on one CUT cell get strips that are equal element for element**,
  because `RooftopSupport.Build` takes its breakpoints from the region and the flow direction alone
  and not from which side the shared face is on. That is what makes pairing them by index exact
  rather than an alignment assumption — and `PlanarGram.CellIntegral` asserts the counts rather than
  trusting it.
- **The second moments are taken about the CELL'S CENTRE, not the origin.** A cell 60 mm down a taper
  has coordinates ~1e-1 and extents ~1e-5, so `∫u²` about the origin would be a difference of numbers
  1e8 times the answer.

### Cost — unmeasurable, as predicted

FR-4 hero at the shipping mesh, N = 1,368, 720 cells, **Debug** (which is what `dotnet test` builds):

| | fill | factor |
|---|---|---|
| PEC | 353.4 ms | 937.3 ms |
| with the term | 345.5 ms | 935.8 ms |

Both differences are inside the run-to-run noise. The Gram itself builds in 1.65 ms and holds 2,664
entries (1.95 × N) at 47.0 KB. `Cost_TheGramIsBuiltOncePerMesh_NotPerFrequency` is the COUNTER that
keeps it that way, rather than a wall clock.

**Gram nonzero counts measured:** 1.71 × N (short FR-4 line), 1.90 × N (conformal disc, 66 pairs
touching a cut cell), 1.91 × N (conformal taper, 150 such pairs), 1.95 × N (FR-4 hero). O(N) with a
constant under 2 everywhere, which is what the pairing predicts: a rooftop meets at most one
same-direction neighbour per cell.

### Gates

`tests/Engine.Tests/Mom/PlanarSurfaceImpedanceTests.cs`, 29 tests — **25 in the routine tier at ~4 s
together**, and 4 (two `[Theory]` methods × two starters) tagged `Category=Benchmark` at 1 m 40 s.
R-cl1-7 and R-cl1-9 are the tagged ones (measured 1.1 + 7.9 s and 10.4 + 72.6 s), which is the
repo's mechanical ~5 s rule rather than a preference — the brief hoped they would fit the routine
gate and they do not. `R_cl1_7b` is their routine-tier counterpart: it runs the whole path with the
term on and asserts the two structural facts (the term can only ADD loss, and α_c ∝ 1/√σ at fixed t
— measured **1.9957** for a 4× resistivity step against √4 = 2).

- **R-cl1-1** both asymptotes to 1e-12 (exactly 0.00 relative at every rung tried), the PEC zero, the
  ω = 0 limit.
- **R-cl1-2** ⟨f_m, f_n⟩ against a bilinear-mapped Gauss quadrature of the rooftop read through
  `PlanarBasisFunctions.Evaluate` (the definition the fill never calls): **worst 5.0e-15** relative
  over line, conformal taper and conformal disc. Plus symmetry, a positive diagonal, and the uniform
  mesh's hand-computed 2/3 and 1/6.
- **R-cl1-3** every Gram nonzero has a slot in the accelerator's near set — **0 missing** — and
  `PlanarEntryFill.At` is bit-identical to `PlanarFill.Fill` on every entry carrying the term.
- **R-cl1-4** PEC reproduction on the FR-4 line and a taper, both spellings (σ = ∞, t = 0):
  **0 bits moved** out of 2·N² compared.
- **R-cl1-5** the via barrel walks into `PlanarDcSolve`'s own `ℓ/(σA)`: 1.18e-10 relative at 1 kHz on
  a 100 µm gold post, and 7.4e-12 on every vertical basis of a real two-level meshed via.
- **R-cl1-6** the DC limit, tabulated down the band on both starters. At the DCIM fit floor
  (k₀H < 1e-4, i.e. ~3 MHz on 1.6 mm FR-4 and ~48 MHz on 100 µm GaAs) Re(Z_s)/(1/σt) is **1.0039** and
  **1.0000** — both inside the brief's 1%, so LF2's conduction-substitution band is not a step in α.
- **R-cl1-8** with the term off the Gram is not so much as BUILT, and nothing else moved: the whole
  of `tests/Engine.Tests/Mom` is **1,047 passed** (1 m 17 s), the Firewall suite passes, and the
  solution builds with no new warning. `PlanarGram`'s one internal-invariant exception is on
  `tests/Firewall.Tests/user-facing-text-allowlist.txt` — it is a message about two supports of
  different cells being paired, which no user will ever read.

## PCAL7 — the mode-separation refusal is drawn on the RIGHT quantity, and so is the other one (2026-09-13)

`docs/sonnet-briefs/brief-portcal-7-separation-gate.md`. PCAL6 ended by pointing at a different
remedy from the one it tried — **draw the refusal on the quasi-static separation and demote the
measured one to a diagnostic** — declined to take it on one data point, and wrote this brief to get
the measurement first. The measurement was made, and **it refutes the brief's own title.** The gate
does not move. What changed is the sentence a refused user reads, which named a remedy that does not
bind on the case that actually fires.

### The one instruction, honoured: every candidate is scored on the ANSWER

R-pcal7-1 exists because PCAL6 failed by inferring an improvement from a reported quantity. So the
whole of M1 is **93 de-embedded points scored against the kernel-A oracle**, each with its own
A-vs-B floor re-measured on the same geometry, and not one conclusion below rests on a residual.

**The harness is the shipped path with the gate lifted** — committed technology, generated cells,
`EmRunService.Run` (the call the Simulate button and `circuitrf em` both make) in Release, kernel B
against kernel A on the same file, `PlanarCalibrationSettings.ModeSeparationFloorDegrees`
temporarily set to 1e-9 so a refused run can be scored at all. That constant is the only edit, and
it is the only way to see the answer behind a refusal: the floor has no `.cem` field, by design.
**It was validated before it was trusted**, exactly as PCAL6's was: `coupled-pair` 1–7 GHz comes
back 0.0303 / 0.0296 / 0.0331 / 0.0374 / 0.0408 / 0.0434 / 0.0454 against a floor of 0.0521 — every
digit of §PCAL4's published table.

### M1 — the measured separation swings 9× and the answer does not move

The lever PCAL6 identified is Δℓ, and through the shipped path Δℓ is chosen from the SWEEP's band —
so a band scan at one frequency is a Δℓ ladder made of ordinary runs. `coupled-pair` as committed
(254 µm lines 246 µm apart on 0.9 mm FR-4), at its own mesh, scored at **200 MHz**:

| band | Δℓ (mm) | measured ° | electrostatic ° | null-space gap | max \|ΔS\| | floor | ×floor |
|---|---|---|---|---|---|---|---|
| 200 MHz alone | 153.4 | 2.42 | 4.32 | 3.4e-6 | 0.11021 | 0.1488 | 0.741 |
| 200–400 MHz | 109.2 | 1.15 | 3.07 | 1.8e-5 | 0.10987 | 0.1482 | 0.741 |
| 200–500 MHz | 98.1 | 0.83 | 2.75 | 4.3e-5 | 0.10969 | 0.1485 | 0.738 |
| 200–600 MHz | 88.7 | 0.57 | 2.49 | 1.1e-4 | 0.10947 | 0.1478 | 0.740 |
| **200–700 MHz** | 81.4 | **0.36 — refused** | 2.28 | 8.4e-5 | **0.10926** | 0.1480 | 0.738 |
| **200–800 MHz** | 77.6 | **0.26 — refused** | 2.18 | 1.8e-4 | **0.10914** | 0.1480 | 0.737 |

**The measured separation falls by 9.3× and the de-embedded answer improves by 1%.** The two rows a
run refuses on today are the two BEST answers in the ladder. The same shape holds at 100 MHz (six
bands, measured 0.92°–1.89°, answer 0.12976–0.12987 — flat to four digits) and at 300 MHz.
`NullSpaceGap` rises 50× across the ladder while the answer is flat, which is PCAL1 §5 measured again
on the modal quantities: **it does not predict the de-embedding error, and it does not predict
indeterminacy either.**

So M1's question — *does the oracle error ever go bad while the quasi-static separation is healthy?*
— is answered **no, on this family**, and the brief's candidate (a) survives that test.

### M2 — and then the same family produced the two cases that kill (a), (c) and (d)

A gate is decided by what it lets through, so the family was extended until something published
nonsense. Two did, and **they fail through different quantities:**

| case | measured ° | electrostatic ° | null-space gap | max \|ΔS\| | floor | ×floor |
|---|---|---|---|---|---|---|
| **pair 4.4 mm apart, 500 MHz, 500 MHz–2 GHz** | **0.026** | 0.517 | 9.7e-9 | **0.9986** | 0.0732 | **13.6** |
| same, 500 MHz–1.8 GHz | 0.041 | 0.551 | 8.2e-10 | 0.9987 | 0.0734 | **13.6** |
| same, 500 MHz–1.5 GHz | 0.069 | 0.581 | 7.5e-10 | 0.0530 | 0.0735 | 0.72 |
| **equal-width TRIPLE, 246 µm, 200 MHz, 200–800 MHz** | 0.53 | **0.28** | 0.579 | **1.1131** | 0.1480 | **7.5** |

Read the first three rows together: the failure is a **cliff at a measured separation of about
0.05°**, not a slope — 0.069° is fine and 0.041° is 13.6× the floor, on the same metal at the same
frequency with a 5 % change of Δℓ. `|ΔS| > 1` on a matrix whose entries cannot exceed 1 is the modal
assignment having swapped, which is exactly the indeterminacy `ModeSeparationFloorDegrees` exists
for. **Its electrostatic separation reads 0.517°, over the floor** — so this run passes the setup
guard and is caught only by the per-frequency one.

The triple is the mirror image: **its electrostatics reads 0.28°, under the floor, while its measured
separation reads 0.53°, over it.** It is caught only by the setup guard.

| candidate | what the measurement says |
|---|---|
| **(a) gate on the quasi-static separation** | **publishes the 13.6×-floor case.** Refuted. |
| **(b) keep the gate, lower the floor** | the cliff is under 0.05° on the symmetric pair, but the ASYMMETRIC pair (254/508 µm, 4.4 mm apart, 500 MHz) degrades smoothly and crosses its own floor at a measured **0.33°** — 0.57° → 0.795× floor, 0.49° → 0.804, 0.39° → 0.838, 0.36° → 0.945, 0.33° → **1.067**. No single lowered floor is right on both geometries, which is the brief's §4(b) objection, now measured instead of asserted. |
| **(c) gate on `NullSpaceGap`** | **9.7e-9 on the 13.6×-floor case** — four orders BELOW what it reads on rows whose answers are perfect. Refuted. |
| **(d) refuse only when BOTH are under the floor** | the AND is never satisfied by either catastrophe (one has 0.517°, the other 0.53°). **Publishes both.** Refuted. |
| **what ships** | the OR of the two, spelled as two gates in two places — and it catches both. |

**The gate stays where it is. So does the other one.** No candidate in the brief's own table is safe,
and the arrangement the brief proposed to simplify away is the only one in the set that catches
everything found.

### R-pcal7-5, answered the other way: the per-frequency guard is not a second spelling

§5 argued that if the gate moved to the quasi-static quantity the per-frequency refusal would be
redundant and must be DELETED. It is the reverse that is true, and the 0.026°/0.517° row is the
proof: **the setup guard passes that run.** `GuardModeSeparation` asks a property of the
cross-section, which is why it is the trustworthy one and also why it cannot say whether a
particular pair of standards will manage to measure it — PCAL6/M5 already wrote that sentence, and
this is what it costs if you act on it. Two questions, two places, both load-bearing.
`ThePerFrequencyGateRefusesAGroupTheSetupGatePassed` and
`TheSetupGateRefusesAGroupThePerFrequencyGateWouldHavePassed` gate it structurally, on one fixture,
by placing a floor between the two readings at a frequency where they disagree in each direction.

### R-pcal7-3 — the population, and it is empty because nothing moved

No run's s-parameters change: the only code change is a message and one number added to a note.
Bit-identity is by construction, and `AGroupedSweepThatCalibratesTodayIsBitIdentical` still holds its
pre-PCAL6 literals to the last bit. **The populations that would have moved are worth recording
anyway**, since they are what the decision was taken against: candidate (a) would have newly
REFUSED 2 of the 93 points measured (the triple at 200 MHz, and a pair 3.6–4.4 mm apart at 100 MHz)
and newly PUBLISHED 8, two of which are the catastrophes above.

### The honest cost of keeping the gate: it over-refuses, and by how much

Of the 8 points in the family that the shipped floor refuses, **6 had answers comfortably inside
their floor** (0.72–0.74× it — in three cases the best answer of their own ladder), one was
marginally over (1.07×), and one was the 13.6× catastrophe. That is a real price and it is not hidden
here. The only lever left that would reduce it is the floor's VALUE, which R-pcal7-4 puts outside
this brief; the measurement above is what a future brief on it would start from, and its warning is
that 0.33° is already bad on an asymmetric pair while 0.069° is still fine on a symmetric one, so the
value cannot be chosen from one geometry.

### R-pcal7-7 — the refusal named a remedy that does not bind, and now names two that do

`GuardModeSeparation` asks the quasi-static question at every REQUESTED frequency before a standard
is solved (PCAL6/R-pcal6-7), so a group that reaches the per-frequency refusal has, on every
non-adaptive sweep, already been found separable in principle. **Its remedy sentence — "separate the
feeds" — was therefore advice for the case that cannot reach it.** It now branches on the pair of
numbers it already prints, and when the electrostatic figure is over the floor it names the two
levers PCAL7 measured:

- **the BAND**, because Δℓ is chosen from it — the ladder above is that lever, 0.26° to 1.15° on the
  same metal at the same frequency, with the published s-parameters moving 1%;
- **the MESH**, and this is the bigger of the two. The same fixture over the same decade band at
  200 MHz reads **0.19° with the edge mesh off and 2.94° with it on** — the refusal and a clean
  publish. The measured separation is taken off the STANDARDS' discretisation; the electrostatic one
  barely moves (4.70° against 4.82°), which is what makes the pair of numbers readable.

The old sentence survives for the branch it belongs to: an adaptive sweep solves points the setup
guard never saw, and the quasi-static separation is not monotone in frequency.

### A thing the harness established about this repository's own fixture, worth knowing

**`GroupSeparationRefusalTests`' refusal was an artefact of the mesh it chose for speed.** That test
ran the owner's sweep shape at `CellsPerWavelength = 2, EdgeMesh = false` "because what is gated is a
set of DECISIONS" — and at that mesh the de-embedded answer is max |ΔS| **0.994 against an A-vs-B
floor of 0.995**. Nothing there is measurable by either kernel; the refusal is correct and is about
the discretisation. At the fixture's own mesh the identical sweep publishes at every point, 0.1102
against a floor of 0.1483 at 200 MHz. Cells-per-wavelength is inert on this geometry (2, 3 and 4 give
bit-identical answers with the edge mesh on) — **the edge mesh is the whole variable.** The test is
`GroupSeparationRemedyTests` now and says both halves, the second one tagged `Category=Benchmark`
because it is ~27 s in Release.

### What was changed in the code

- `PlanarSolve.RecordGroupDiagnostics` — the per-point `MODAL CALIBRATION` line carries the
  electrostatic separation beside the measured one, on every run and not only on a refusal. Free: the
  group's modal medium is extracted once and cached.
- `PlanarSolve`'s per-frequency refusal — the remedy branches on which of the two numbers is under
  the floor, as above.
- Nothing else. No gate moved, no floor moved, no standard grew, no solve was added.

### Gates

`tests/Engine.Tests/Mom/PlanarGroupSeparationTests.cs` (10 tests, ~19 s) — PCAL6's eight, unchanged
and still passing on their own literals, plus the two that hold PCAL7's finding.
`tests/Ui.Tests/Em/GroupSeparationRemedyTests.cs` — the routine refusal-and-remedy gate (0.9 s) and
the `Category=Benchmark` publish (1 m 4 s under a test build).

**The 93-point table is a harness and not a test**, per the standing rule: committed technology,
generated two- and three-conductor cells, the real `EmRunService.Run` in Release, kernel A on the
same files, the floor fixture being the same cross-section with its conductors moved 9 mm apart. It
is reproducible by setting `ModeSeparationFloorDegrees` to a negligible value and sweeping
(widths, gap, frequency, band) — the band being how Δℓ is reached from outside the engine.

## PCAL6 — which separation a grouped port calibrates on, and the brief that was wrong about its own defect (2026-09-13)

`docs/sonnet-briefs/brief-portcal-6-separation-selection.md`. The brief opened on the last wall
§LF3 left standing: a four-port grouped board where **200 MHz is refused in one sweep and accepted
in another**, same metal, same ports, same group, same floor. Its own §4 warned that the obvious fix
— make `PlanarCalibration.SelectSeparation` separability-aware — was refuted by measurement, and
made isolating why (M1) a milestone in its own right. **It was right to, and the isolation refutes
the brief's framing as well as the obvious fix.**

### M1 — the measured separation is not |Δγ|·Δℓ, and the variable is the SHORT standard

The brief's §4 offered three answers and asked M1 to pick one. The answer is **(1) — the measured
separation is wrong** — and the ladder says where it breaks, which is not where §4 looked.

**The Δℓ ladder, short line and frequency held, on the series' own coupled pair (254 µm lines
246 µm apart on 0.9 mm FR-4, ports at all four ends), at 200 MHz.** `ModeSeparationDegrees` against
the same quantity taken from the group's own electrostatics, which is Δβ·Δℓ with Δβ a property of
the cross-section and therefore exactly proportional to Δℓ:

| Δℓ (mm) | quasi-static ° | measured ° | ratio | measured β per mode | quasi-static β |
|---|---|---|---|---|---|
| 10.53 | 0.288 | 4.924 | **17.1** | 0.285, 6.760 | 6.911, 7.389 |
| 20.11 | 0.554 | 3.948 | 7.13 | 3.417, 6.837 | 6.911, 7.392 |
| 30.64 | 0.847 | 3.075 | 3.63 | 5.117, 6.865 | 6.911, 7.394 |
| 45.00 | 1.247 | 2.469 | 1.98 | 5.927, 6.883 | 6.911, 7.395 |
| 54.58 | 1.514 | 2.148 | 1.42 | 6.205, 6.890 | 6.911, 7.395 |
| 70.86 | 1.967 | 1.655 | 0.84 | 6.491, 6.897 | 6.911, 7.396 |
| 100.54 | 2.795 | 0.829 | 0.30 | 6.763, 6.904 | 6.911, 7.396 |
| **130.22** | 3.622 | **0.182** | **0.05** | 6.906, 6.907 | 6.911, 7.397 |
| 171.39 | 4.770 | 1.102 | 0.23 | 6.911, 7.021 | 6.911, 7.397 |
| 220.22 | 6.132 | 2.380 | 0.39 | 6.913, 7.101 | 6.911, 7.397 |

**Read the last two columns, not the first three.** The quasi-static β per mode is stable to four
digits across the whole ladder, as it must be. The MEASURED ones are not: mode 0 climbs from 0.285
to 6.913 and mode 1 from 6.760 to 7.101 against a truth of 7.397, and **the two extracted curves
CROSS at Δℓ ≈ 130 mm.** The near-zero separation there — which is what a run refuses on — is two
corrupt numbers happening to be equal. It is not a degeneracy, and no property of the metal is
involved.

**So Δℓ is not the variable. The short standard is.** Δℓ held at 100 mm, ℓ₁ laddered, three
couplings (0.27 / 1 / 2 substrate heights) and two frequencies; the column that orders every row is
the short standard's own ELECTRICAL length:

| β·ℓ₁ | ℓ₁ (mm) | ratio at 200 MHz | at 400 MHz | at 500 MHz | at 1 GHz |
|---|---|---|---|---|---|
| 1.5° | 3.83 | **0.27-0.30** | — | — | — |
| 3.0° | 3.83 | — | 0.59-0.73 | — | — |
| 3.8° | 3.83 | — | — | 0.83 | — |
| 4.2° | 10.53 | 0.83-0.93 | — | — | — |
| 7.6° | 3.83 / 7.66 | — | — | 0.97 | 0.97 |
| 8.0-8.5° | 20.11 / 10.53 | 0.90-0.96 | 0.97-0.98 | — | — |
| 14-16° | 35.43 / 20.11 | 0.92-1.04 | 1.00 | 1.00 | — |
| 20-41° | 50.75 / 30-60 | 0.92-1.04 | 1.00 | 1.00 | 1.01 |

The knee is at about 8° and the plateau from about 14°, on every coupling and at every frequency
tried. The shipped short standard is `ShortLineHeights` = 3 substrate heights, which on 0.9 mm FR-4
is **1.5° at 200 MHz** — the worst row in the table.

**Why the short standard and not Δℓ, structurally.** `ShortLineHeights` sizes ℓ₁ in substrate
heights because that is the scale the two error boxes' evanescent fields decay on, and a SCALAR
calibration needs nothing more: D6 reads ℓ₁ only to fix a gauge. The modal one does — the `T(ℓ₁)·F`
equation, (4) in `PlanarModalCalibration`'s own header, is what separates the modes' own gauges,
and as `E(ℓ₁) → I` it stops
distinguishing them. The failure is frequency-dependent because D6's peel divides by a₂₁² (LF1 §5a),
so the same geometric shortfall is amplified 25× from 1 GHz to 200 MHz.

### What the brief got wrong about itself, in as many words

- **§4's option (2) is refuted on the CODE, not by measurement.** It reads: *"`BuildSet` builds
  element 0 from `SuggestLengths(slab, fLo, fHi)`, which also depends on the band — so changing
  `fLo` moved the short line as well as the deltas. The two runs above are therefore not a clean
  comparison of Δℓ."* `SuggestLengths` returns `s.ShortLineHeights * slab.HeightM` for its first
  element. **It does not depend on the band at all.** The two runs in §4's own table were a clean
  comparison of Δℓ, and their result — the longer separation measuring 2.8× worse — is real and is
  the ladder's Δℓ = 130-171 mm region.
- **§3's candidate count is quoted against the wrong constant.** The brief says
  `n = ceil(log(fHi/fLo) / log 8)`; `SuggestDeltas` divides by `DesignBandRatioPerSeparation` = 4,
  not by `BandRatioPerSeparation` = 8. Every number in §3's own table is right — they were computed
  with 4 — so only the formula in the prose is wrong.
- **§4's headline framing ("a longer Δℓ should separate the modes proportionally better") is
  sound as physics and useless as a rule**, because the reported quantity is not the modes'
  distance. Measured at 200 MHz: at Δℓ = 54.6 mm the separation reads a comfortable 2.15° with the
  even mode's β **10 % wrong**, and at Δℓ = 171 mm it reads 1.10° with that same β exact. **The
  candidate that clears the floor is not the accurate one**, which is the strongest possible
  argument against a separability-aware selection and is why `SelectSeparation`'s scoring is
  unchanged.

### M2/M3 — the remedy was built, measured against the oracle, and REMOVED

**This is the part of PCAL6 that failed, and the failure is worth more recorded than quietly
rewritten.** The remedy M1 pointed at — grow a group's short standard until it carries real phase,
as a one-shot recovery on a sweep that would otherwise refuse — was built
(`PlanarCalibrationSettings.GroupShortLineDegrees`, a typed refusal, a retry in
`PlanarKernel.Solve`), it did clear the refusals, and then it was measured against the cross-section
oracle and it **makes the published s-parameters worse at every frequency.** It is gone.

`coupled-pair` through the real `em` verb in Release, max |ΔS| against kernel A on the same file,
with the floor re-measured by moving the conductors 9 mm apart exactly as §PCAL4 measured it:

| f | shipped (no regrow) | regrown to 8° | regrown to 20° | floor |
|---|---|---|---|---|
| 100 MHz | **0.1298** | 0.2813 | **1.8749** | 0.2682 |
| 200 MHz | **0.1102** | 0.1581 | 0.1582 | 0.1483 |
| 300 MHz | **0.0743** | 0.1091 | 0.1092 | 0.1064 |
| 500 MHz | **0.0481** | 0.0690 | 0.0691 | 0.0743 |
| 1 GHz | **0.0320** | 0.0414 | 0.0414 | 0.0539 |

At the 20° target and a 100 MHz band bottom the answer is not merely degraded, it is **nonsense** —
|ΔS| of 1.87 on a matrix whose entries cannot exceed 1, non-passive at σ_max 1.032 — and the run's
own residuals agree: cascade 5.7e-5 and null-space gap 3.2e-4 at 100 MHz, three to four orders worse
than without the regrow. The standards go from 9.14× the DUT to **55.21×** to buy that.

**The mistake was an inference, and it is the one PCAL1 §5 and brief 4 both warn about in advance.**
M1's ladder measured that a longer short standard makes `ModeSeparationDegrees` agree with the
quasi-static truth, and that is true and reproducible. It does not follow that the ERROR BOX
improves, and it does not: the separation and the box are different quantities, the separation gets
better while the box gets worse, and PCAL1 already measured that these residuals do not predict the
de-embedding error. **A separation that reads low does not mean the answer is bad, and lengthening
the standard until it reads high does not make the answer good.**

What survives is the DIAGNOSTIC. The per-frequency refusal now reports the quasi-static separation
and the short standard's length beside the measured separation, because that pair is the only thing
that distinguishes "these modes are degenerate" (both small) from "this standard could not measure
them" (measured small, quasi-static large). It is reported and not acted on.

### The accuracy question the removal raises, and it is measured: a group is fine at low frequency

Removing the remedy leaves the refusal in place, so the obvious question is whether grouped
de-embedding is worth reaching for down there at all. It is. Same harness, same oracle, same floor:

| f | grouped `coupled-pair` | floor (well-separated, ungrouped) |
|---|---|---|
| 100 MHz | **0.1298** | 0.2682 |
| 200 MHz | **0.1102** | 0.1483 |
| 300 MHz | **0.0743** | 0.1064 |
| 500 MHz | **0.0481** | 0.0743 |
| 1 GHz | **0.0320** | 0.0539 |

**Below the floor at every frequency down to 100 MHz.** The error does grow toward the bottom of the
band, by 4× from 1 GHz to 100 MHz — but the floor grows faster, so that growth is §LF1 §5(a)'s peel
amplification acting on ANY de-embedded port and not something the grouping adds. On the committed
fixture at its shipping mesh the mode-separation refusal never fires over that band at all (1.05° at
100 MHz against the 0.50° floor), and neither does a ceiling: ten points in 27 s.

**The harness is validated rather than asserted.** It reproduces §PCAL4's published numbers to the
last digit — 1 GHz 0.0303, 2 GHz 0.0296, 3 GHz 0.0331, 4 GHz 0.0374, 5 GHz 0.0408, 6 GHz 0.0434,
7 GHz 0.0454, floor 0.0521 — before any new frequency was asked for.

### So what is still open, and it is NOT what this brief thought

The user-facing complaint PCAL6 opened on — a group whose modes are separable is refused at the
bottom of a band — **is diagnosed and not fixed.** What the measurements above add is that the
refusal is also, on the evidence available, *unnecessary*: at 100 MHz on the shipping fixture the
measured separation reads 1.05° where the electrostatics says 4.7°, and the answer there is 0.1298
against a floor of 0.2682. A separation that under-reads by 4× produced an answer comfortably inside
the floor.

That points at a different remedy from the one this brief tried — **draw the refusal on the
quasi-static separation, which M1 measured to be the reliable one, and demote the measured
separation to a reported diagnostic** — and it is deliberately NOT taken here. It moves a safety
gate on ONE data point, and the gate exists because at equal eigenvalues the modal basis is decided
by round-off. It needs its own measurement: a family of groups spanning the floor, each de-embedded
answer scored against the oracle, to establish whether a small MEASURED separation ever corresponds
to a bad answer.

**That is `docs/sonnet-briefs/brief-portcal-7-separation-gate.md`**, written rather than left as a
sentence here, and it carries the one instruction this brief earned the hard way: score the ANSWER
against the oracle, never a reported quantity.

> **PCAL7 ran that measurement the same day and the remedy pointed at above is REFUTED — do not take
> it.** Ninety-three de-embedded points against the oracle say the paragraph above is right about its
> own family and wrong as a general rule: a pair 4.4 mm apart at 500 MHz reads a measured separation
> of 0.026° with an ELECTROSTATIC separation of 0.517°, over the floor, and the answer behind that
> refusal is max |ΔS| 0.999 against an A-vs-B floor of 0.073. Drawing the refusal on the
> quasi-static quantity publishes it. The single data point this section rested on was real and is
> reproduced — the measured separation under-reading by 4× with a perfectly good answer — but it
> generalises to a FAMILY and not to the gate. See §PCAL7 above; both gates stay.

### M4 — one decision point, and a group's choice no longer remembers the sweep

`PlanarPortCalibrator.SelectedIndexAt` is now the only place that answers "which separation does
this frequency use", and all four sites R-pcal6-3 names ask it — including `NeededAt`, which decides
which standard meshes are SOLVED at all. **There was a live violation**: the setup guard predicted β
from `PlanarCalibration.EstimateBeta` while the sweep predicted it from the previous solved point,
so a run could report about one standard and calibrate against another.

For a GROUP the input is the quasi-static modal β — a property of the cross-section and of that
frequency alone, built once from the two extreme standards' electrostatics at no Green's-function
cost, and measured at 0.1-0.8 % against the full-wave answer over 200 MHz – 1 GHz, against the
15-20 % `EstimateBeta` is known to run low by. **A port that is not in a group keeps `ExpectedBeta`
exactly**, which is both the brief's declared scope and what makes every ungrouped run bit-identical
by construction. The BRANCH continuation stays history-dependent everywhere and has to: it is a
continuation by definition, and L9e/M1 already recorded that predicting it from the pre-solve
estimate is a coin flip on the 2π branch.

**§5's defect is therefore fixed for groups and recorded for everything else.** A single-port
calibration still picks its separation from the previous point's measured β, so two sweeps
containing one frequency can still publish two different S there. Nothing in this brief's scope
touches it; it is `SelectedIndexAt`'s own doc comment, where the next person to look will find it.

### M5 — the setup guard, and the question R-pcal6-7 asked

R-pcal6-7 offered three ways out and the measurement takes the third and then the second.

- **Its first option is backwards.** "Bring the quasi-static quantity into agreement with the
  measured one" assumes the measured one is right. It is the quasi-static one that is right — it is
  stable to four digits across a ladder over which the measured one swings by 17× — so there is
  nothing to bring into agreement, and the gap is stated rather than closed: **at the shipped short
  standard the two disagree by 3.4× at 200 MHz on the routine fixture, and by 24× on the same
  fixture run over the owner's own sweep shape (0.192° measured against 4.701°).** That is what the
  per-frequency refusal now reports, in the
  same sentence, because the pair of numbers is what separates "these modes are degenerate" from
  "this standard could not measure them".
- **Its second option binds, and it is free.** Asking only at `fLo` is genuinely not enough, and not
  for a subtle reason: Δβ is exactly proportional to frequency, so if Δℓ were fixed the band's
  bottom would provably be the worst point — but `SuggestDeltas` hands out one separation per
  sub-band and the selection steps DOWN to a shorter one as the frequency rises, dropping the
  product by most of that step at every switch. On the series' own pair over 100 MHz – 1 GHz, where
  the candidates are 171.0 mm and 54.1 mm and the switch falls at 298 MHz: **2.38° at the band's
  bottom against 2.24° just above the switch.** A band with four candidates has four such steps.
  `GuardModeSeparation` now asks at every requested frequency and names the one it found; the
  question is arithmetic on an electrostatic solve the run already owes.
- **What it still cannot do is predict the per-frequency refusal**, and that is the honest statement
  rather than a hedge. The setup guard's quantity is a property of the cross-section and does not
  move when the standards do — which is what makes it the trustworthy one, and what makes it unable
  to say whether a particular pair of standards will manage to measure it.

### The refusal's own sentence, and what was left alone

**The remedy sentence is unchanged** (R-pcal6-6). It still says "separate the feeds", which is the
right advice when the refusal is about the metal — and the quasi-static number now printed beside
the measured one is how a user tells that case from the other. When the two disagree the advice is
narrower and this write-up is where it lives: raise the sweep's lower edge. On the series' own
fixture a band starting at 300 MHz clears the floor where one starting at 200 MHz does not, and the
answer up there is measurably good.

### Gates

`tests/Engine.Tests/Mom/PlanarGroupSeparationTests.cs` (8 tests, ~19 s) —
`AGroupedSweepThatCalibratesTodayIsBitIdentical` asserts EXACT equality against eight S entries
measured on the pre-PCAL6 tree in a worktree at HEAD, which is R-pcal6-1 and the reason §LF1 §5(b)'s
objection no longer applies; `TheSameFrequencyIsRefusedInOneSweepAndAcceptedInAnother` is §1 rows 1
and 4 reduced to a fixture (100 MHz – 1 GHz publishes 200 MHz at 1.63°, 200 MHz – 1 GHz refuses the
same point at 0.405° while its electrostatics says 2.839°); `GenuinelyDegenerateModesStillRefuse`,
`TheSeparationAFrequencyUsesDoesNotDependOnTheSweep`,
`TheWorstQuasiStaticSeparationIsNotAlwaysAtTheBandsBottom`, `TheSetupGuardAsksAtEveryFrequency`, and
the two ladder measurements that hold M1's finding. `tests/Ui.Tests/Em/GroupSeparationRefusalTests.cs`
is the end-to-end through `EmRunService.Run` on the owner's own sweep shape: it REFUSES, it carries
both numbers, and it writes no file.

**One of those tests is 13.6 s, over this repository's ~5 s `Category=Benchmark` threshold, and it is
deliberately untagged.** It is the brief's central claim and the only routine coverage of it; tagging
it would put the defect back out of reach of a plain `dotnet test`, which is what §8 asked it not to
be. The cost is dominated by the 171 mm calibration standards a decade band needs at 200 MHz, and it
is stated here rather than hidden.

**The accuracy measurements above are a harness and not a test**, per the standing rule — committed
fixtures through the real `em` verb in Release, against kernel A on the same files. They are
reproducible from `testdata/portcal` by setting the `.cem`'s frequency and toggling `AnalysisKind`
between `Planar` and absent; the floor fixture is `coupled-pair` with its second conductor moved
9 mm in y.

## LF3 — two refusals that name a remedy now apply it (2026-09-13)

Owner report, on the board of §LF2: a run refused with PCAL5's severed-conductor message, which names
two remedies. The owner picked the expensive one. On that board the edge mesh is **1,197 unknowns
against 304** — and every calibration standard reproduces the DUT's gridlines verbatim, so it is 4x
the standards too — while staircasing the same artwork is **311**, a 2 % change. Then the run refused
again, on a de-embedding ceiling whose own first remedy is "turn the accelerated solve on".

**A run that already knows the answer should not be asking a person to type it**, especially when the
sentence is about a calibration standard, which is not a thing the user drew. Both are now applied by
`PlanarKernel.Solve` — which is where the settings live — and both say what they did.

**The severed retry evaluates the refusal's remedies and keeps the CHEAPEST that works**, rather than
taking them in order. Staircase, edge mesh, and both together; each is meshed and re-asked, and only
a candidate that actually restores conduction is eligible. Ordering by guess would take the edge mesh
on a board where staircase alone was enough, which is the mistake this exists to stop. It is not a
quality trade either way: a cut cell that carries no rooftop is a MESHING artefact, staircasing
removes it and the edge mesh resolves it, and neither approximates the severed answer — which is
simply wrong. Nothing is kept if none of the three clears it, and `PlanarSolve.Run` then raises
PCAL5's refusal exactly as before.

**Both remedies are needed, which is why the candidate list is not just the cheap one.** On the
owner's board staircase alone clears it (311). On `ConformalConductionTests`' mitred pair NEITHER
alone does **once the feed leads are grown** — conformal 308, staircase 310, edge 1,245, all severed —
and staircase-plus-edge-mesh at 1,006 is what conducts. That distinction is worth stating on its own:
`TheEdgeMeshRestoresConduction_UnderEitherBoundaryModel` measures the RAW problem, where the edge mesh
alone is enough at N = 876. **The geometry a run actually meshes is the ground-path- and feed-extended
one**, and a remedy that binds on the drawn artwork can fail on it. The gate is written on the
extended problem and through `PlanarKernel.Mesh` (which passes
`PlanarEdgeReference.LocalConductorWidth` — a bare `SurfaceMesher.Mesh` silently measures a different
mesh), and it asserts the run landed on the cheapest working candidate rather than naming one.

**The ceiling retry is keyed on a TYPE, not on a sentence.** `PlanarAcceleratorWouldFitException`
is raised only in the one recoverable case — dense, past the dense ceiling, inside the accelerated
one, single-level — and `PlanarKernel.Solve` catches that alone, turns the accelerator on and re-runs
once. Every other ceiling case keeps the full refusal it had, because there is nothing to recover
with; `P11_ADenseDeembeddedRun_IsRefusedAtSetup_AndTheRefusalNamesTheAccelerator` now asks at 400 MHz
instead of 1 GHz so its standard is past the accelerated ceiling too, which is what keeps it testing
the refusal rather than the retry. The repeated work is setup up to the first oversized standard —
sub-second — against a run the user would otherwise have restarted by hand.

**Measured end to end on the reported board**, as `EmRunService.Run` performs it: 0 Hz + 500 MHz +
1 GHz went from two consecutive refusals to **Ok in 6.5 s**, recovering onto staircase (311 against
304) and reporting the DC point at 7.753 mΩ. 300 MHz – 1 GHz in 8 points is Ok in 16.2 s.

**What is still refused on that board, and it is neither of these.** The authored 0–1 GHz sweep now
reaches PCAL4's mode-separation guard at 200 MHz (0.178° against a 0.50° floor) — the third wall.

> **Diagnosed by §PCAL6, 2026-09-13, and the guess above is wrong — but it is still REFUSED.** The
> selection rule is innocent: `ModeSeparationDegrees` is not the modes' distance at the bottom of a
> band, because the SHORT standard — 3 substrate heights, which is 1.5° of line at 200 MHz — cannot
> separate them at any Δℓ. The refusal now reports the quasi-static separation beside the measured
> one, so a user can see which of the two refusals they have. Regrowing the short standard until the
> two agree was built and then REMOVED: measured against the cross-section oracle it makes the
> published s-parameters worse at every frequency, and nonsense at the bottom of a decade band.

Gates: `ConformalConductionTests.AMeshThatSeversAConductorRemeshesItselfOnTheCHEAPESTRemedyThatWorks`
and `…ARunThatWasNEVERSeveredSaysNothing_AndTheRecoveredOneMatchesItExactly`,
`EmDeembedCeilingTests.LF3_ADenseCeilingTheAcceleratorWouldClear_IsSignalledForTheCallerToFix`.

## LF2 — a point below the fit's floor carries the conduction answer (2026-09-13)

LF1 left `Dcim.CanFitAtFrequency` refusing below k₀H = 1e-4 and named two ways out: raise the
sweep's lower edge, or ask for 0 Hz. The owner asked for a third — publish an UNCALIBRATED full-wave
point down there, since a port discontinuity is a reactance and therefore has nothing left to remove
at low frequency, so the calibration looks like the part that should be dropped rather than the
frequency. It is a good argument. It was measured, it is wrong, and the measurement is the reason
this change is what it is. `docs/design/mom-engine.md` §10.13 carries the tables; the short version:

- **The raw uncalibrated answer is the PORT, at every frequency.** Raw |S₂₁| on the 20 mm hero line
  rises at exactly 6 dB per octave (−63.38 / −57.36 / −51.32 dB at 50 / 100 / 200 MHz) against a
  de-embedded −0.050 / −0.021 / −0.016 — a series capacitance and nothing else. A 20 mm piece of
  copper reads as −60 dB of insertion loss at 10 MHz. **And the gap is one cell wide, so refining
  the mesh makes it worse**: at 100 MHz the 94-unknown mesh is 17.7 dB further from the truth than
  the 24-unknown one. It is not a degraded version of the right answer and it cannot be corrected for.
- **Below ~20 MHz the binding constraint is not the fit anyway.** The calibration standard's N goes
  as 1/f — measured 7,507 / 14,955 / 29,858 / 49,724 at 20 / 10 / 5 / 3 MHz against a 94-unknown
  DUT — so de-embedding is refused an octave or more above the kernel's own 2.98 MHz floor.
- **The fit does go jagged below the floor, and that is the least of it.** The line-to-ground
  capacitance (the one quantity the gap does not corrupt) is flat to five digits above k₀H ≈ 5e-5
  and non-monotonic below, +8.0 %/−2.8 % coarse and +6.5 %/−1.7 % on a 5× finer mesh — same shape,
  so it is the kernel. That break is within 1.2× of the 6e-5 `MinElectricalThicknessForWidenedFit`'s
  header records, found by a completely different route.

**So the points below the floor take the conduction answer, and the refusal moves behind a flag.**
`PlanarSolveSettings.SubstituteConductionBelowFitFloor` defaults true; false restores L9e/D8's
refusal verbatim, which is what a caller MEASURING the fit wants. The split happens beside LF1's own
0 Hz split, above everything else, so **every remaining point's arithmetic is bit-identical** — the
gate asserts that as exact equality against a sweep that never saw a sub-floor frequency, not as a
tolerance. One conduction solve serves 0 Hz and every substituted point, because solving it twice
would be two chances to disagree.

**`Dcim.IsBelowFitFloor` is the only spelling of the question.** `CanFitAtFrequency` asks it and so
does the partition; two spellings would be two answers waiting to disagree at the boundary, which is
the one place it would matter. `Dcim.LowestFittableFrequency` is beside it for the same reason — the
refusal and the note quote the same number.

**What the substitution omits is reactance, and that is why it is defensible rather than a fudge:
its error SHRINKS as the frequency falls while the fit's grows.** The neglected term is ωL/(2Z₀) —
on a 20 nH trace, −44 dB of S₁₁ and 0.36° of S₂₁ phase at 5 MHz, −64 dB and 0.036° at 500 kHz.

**It also closes L8e's 6 Hz hole a second time and from further up.** That point now never reaches
`SpectralGreens` at all, so it cannot reach the array-dimension throw; `PlanarBudgetTests.T4_5`
asserts that on the COUNTERS (no kernel fitted, imaginary part exactly zero) rather than on the
clock, and keeps D8's refusal beside it under the flag.

**The note is one sentence, and the mesh clause is in it on purpose.** The conduction answer is read
on whatever mesh the sweep was given, and a mesh pinned for a microwave run is a crude resistor
ladder: measured against `R_s·L/W` on the 20 mm hero, **0.750× at a 2 GHz mesh**, 0.857× at 5 GHz,
0.929× at 10 GHz, 0.982× at 40 GHz. LF1 §4's "~11 % on a coarse mesh" is the same effect measured on
a less coarse one. A user who pins 2 GHz for an RF sweep and then reads a resistance off it is out
by a quarter, and nothing else in the run would tell them.

**Not done, and it is the thing worth doing next.** The enabling change for everything above is the
PORT, not the kernel and not the calibration. What makes 0 Hz work is that `PlanarDcSolve` uses
conduction terminals — the conductor's end cells against the ground node — rather than a cut in the
metal. A port of that shape at AC has an a₂₁ that does not vanish with ω, which is exactly what
LF1 §5(a) names as the thing that would close the de-embedding amplification and records as not
built. It is the same change twice: it removes the amplification that §10.13(d) measures on today's
100 MHz runs (phase slope 11 % low at 200 MHz, 53 % low at 100 MHz, sign-inverted at 50 MHz), and it
is what would let a sub-floor point be published without a calibration at all.

**One hole this change opened, found by the owner and closed: the severed-conductor check moved
above the split.** PCAL5's check sat below it, so a sweep with no fitted point in it — 0 Hz alone, or
a band entirely under the floor — returned early and never asked. That is the worst place to skip it:
the conduction solve answers a disconnected port with EXACTLY zero by design (§LF1 §4), so a severed
mesh would publish a clean, exact, entirely wrong open circuit with nothing anywhere to say so. The
question needs only the problem, the mesh and the ports, none of which a frequency changes, so it is
now asked once for every sweep shape at no cost. `ConformalConductionTests.ASeveredConductorRefuses…`
is a Theory over all four shapes, because all four leave `PlanarSolve.Run` by different routes.

Gates: `PlanarDcPointTests` (four LF2 tests beside LF1's), `PlanarBudgetTests.T4_5`, and
`ConformalConductionTests.ASeveredConductorRefusesWHATEVERShapeTheSweepIs`.

## LF1 — the DC point, and the low-frequency end of the band (2026-09-13)

Owner report, two refusals on one real board (a 1.4 mm four-layer PCB, four ports, 100 MHz – 1 GHz):
a DC point in the EM setup was refused outright, and so was the sweep's own lower edge at 100 MHz.
The ask was that a low-frequency run simply work, with at most a brief note saying what the engine
did — "an RF designer won't read it if the text is too long."

### 1. The 100 MHz refusal was L9b's R-dcm-4, and its stated reason for not fixing it is measurably wrong

R-dcm-4 recorded the right fix and declined to make it, in these words: *"a frequency-aware path
extent IS the right fix, and it is NOT a one-line change — the sample budget has to rise with the
extent (a wider path at a fixed `Samples` is a sparser one), and `DcimSettings.Samples` is what L8a's
whole accuracy table is calibrated against."*

**The sample budget does not have to rise. It has to stay exactly where it is.** Sweeping
(`PathExtent`, `Samples`) together against `SommerfeldIntegral.Evaluate` on five grounded stacks
(FR-4 at 1.4 / 1.6 / 0.2 mm, GaAs 0.1 mm, alumina 0.635 mm), worst |ΔG_q| over ρ/H ∈ [0.02, 100] as a
fraction of the free-space kernel — the SCALED measure a fill experiences:

| f (FR-4 1.4 mm) | PathExtent | 512 samples | 1024 | 2048 |
|---|---|---|---|---|
| 300 MHz | 682 | **4.3e-7** | 9.3e-7 | 5.0e-6 |
| 100 MHz | 2045 | **3.3e-7** | 5.9e-7 | 5.0e-6 |
| 30 MHz  | 6816 | **1.9e-7** | 3.4e-7 | 9.7e-7 |
| 10 MHz  | 20449 | **6.7e-8** | 4.1e-7 | 6.9e-6 |

512 is the best column at every extent and every frequency tried, and the fit RESIDUAL degrades by
one to two orders as samples rise. The mechanism is the fit rather than the sampling: Prony raises its
order until the residual meets tolerance, and over-sampling a smooth function drives the
linear-prediction least squares toward rank deficiency, so the extra rows buy noise. **So the widening
is free** — 11–27 ms per kernel across the whole band, against 10–30 ms for the shipped default.

### 2. The target is not a new number — it is the product the shipped default already delivers

`PathExtent` is in units of k₀ and the stack's image structure lives at k_ρ ~ 1/H, so what decides
whether the fit sees the stack is `PathExtent·k₀H`. **What that product means geometrically is the
near-field REACH in substrate heights**: the path stops at k_ρ = PathExtent·k₀, so the finest
structure it ever saw is ρ = 1/(PathExtent·k₀) = H/product. `PathExtent` = 300 on 1.6 mm FR-4 at
2 GHz is a product of **20**, i.e. ρ = H/20 — and every accuracy figure recorded for this kernel was
measured in that neighbourhood. `Dcim.CalibratedPathProduct` = 20 holds that reach fixed whatever the
frequency, which is what "the same fit, lower down the band" has to mean.

Scaled |ΔG_q| on FR-4 1.4 mm, default against widened:

| k₀H | f | default (product) | widened to 20 |
|---|---|---|---|
| 6.7e-2 | 2.28 GHz | 4.7e-5 (20) | 4.7e-5 — unchanged, already there |
| 8.8e-3 | 300 MHz | 6.6e-5 (2.6) | 2.2e-6 |
| 2.9e-3 | 100 MHz | **9.6e-3** (0.88) | 1.2e-7 |
| 1.0e-3 | 34 MHz | **2.6e-2** (0.30) | 8.0e-7 |
| 3.0e-4 | 10 MHz | **2.5e-2** (0.09) | 9.1e-8 |
| 1.0e-4 | 3.4 MHz | **6.8e-3** (0.03) | 1.1e-8 |

**It only ever widens**, so every frequency where the default already reaches 20 takes bit-identical
arithmetic. On the two starter substrates that is FR-4 at 2 GHz and above — and **NOT GaAs at any
shipped frequency**: 100 µm GaAs at 2 GHz is a product of 1.26, barely above R-dcm-4's own guard, and
its scaled error there is **3.9e-3 against 4.7e-8 widened**. That is five orders, on a substrate this
kernel ships accuracy figures for, and it is the single largest accuracy change in this work.

`PlanarP2MemoryWinsTests.P2_6` is the gate that says so: it runs its pinned sweep through
`DcimSettings.WidenForStack = false` (which exists only for that purpose), reproduces P4/P5/P7's
literals bit for bit, and then measures the shipped path point by point — 5/10/15/20 GHz move by
**exactly 0**, and the 1 GHz point (product 10.1) by 9.1e-7.

### 3. What the widening cannot reach, and why the floor is where it is

Below k₀H ≈ 8e-5 the failure moves from the path into the FIT: the remainder left after the direct
term, the quasi-static constant and the poles is small enough that Prony is fitting roundoff. Measured
on all five stacks, widened throughout, and the break is in the same place on all five — which is what
makes it a property of k₀H rather than of a stack:

| k₀H | 1.0e-4 | 8e-5 | 6e-5 | 4e-5 | 3e-5 | 1e-5 |
|---|---|---|---|---|---|---|
| scaled &#124;ΔG_q&#124; | ≤4e-7 | ≤3e-7 | 1.6e-4 | 3.4e-4 | 1.4e-2 | 3.5e-2 |
| fit residual | ≤3e-6 | ≤2e-6 | 5e-4 | 1.4e-3 | 2.9e-2 | 1.3e-1 |

`Dcim.MinElectricalThicknessForWidenedFit` = 1e-4, the first decade above the break — **3.4 MHz on
1.4 mm FR-4, 48 MHz on 100 µm GaAs**. `MinElectricalThicknessForFit` (1e-6) is kept for the separate
thing it describes: the point where the full-wave correction is entirely below roundoff and a 6 Hz
point ends in a raw array-dimension throw.

### 4. The DC point is not a limit taken inside the sweep — it is a different solve

**The full-wave kernel has no ω = 0 case and not for a tolerance reason.** The MPIE splits into a
vector-potential term scaling with ω and a scalar-potential term scaling with 1/ω, so at ω = 0 the
system is undefined, and so is every ingredient: the Green's function is written in k₀, the DCIM path
is a multiple of k₀, and the radial table is sized against a wavelength that does not exist.

**What DC actually is, is a conduction problem.** At ω = 0 the charge term enforces ∇·J = 0 and the
vector potential contributes nothing, leaving E = Z_s J on the metal with a divergence-free current —
Ohm's law on a resistor network, with an exact answer and no fit in it. `PlanarDcSolve` builds that
network **on the rooftop basis**, one resistor per basis function, which is the same statement
`PlanarConductors` already makes ("the basis list already IS the conduction graph of the meshed
structure"). Nothing re-derives connectivity from the drawn polygons; that second answer would
disagree with the solve's wherever the mesher staircased, cut or dropped a sliver.

**The one place it departs from the AC port model, and it has to.** A delta gap at a conductor's end
face drives the structure against the plane THROUGH THE FIELD, and at DC there is no field to drive it
through. Modelled as a source in series with its own rooftop — which is what the AC excitation vector
is — every line would read as an open at both ends and a solid piece of copper would publish S = I.
What an edge port IS at DC is the end of that conductor against its reference, so the terminals are
the conductor's end cells and the ground node. The kinds that genuinely ARE a cut in metal (the
internal delta gap; the via-to-plane port, whose gap is at the foot of the via) cut the network.

**A port with no DC path gets EXACTLY zero, and that is a graph question answered on the graph.** It
does not come out of the solve that way — the island's free nodes settle at the driven potential to
~1e-16 and the current is that residual times some thousands of siemens, so it reads ~1e-12 S, which
is indistinguishable from a real, very large leakage resistance. The series-MIM-cap case is the whole
reason this file exists, so the component structure decides it and the numerical solve is asked only
about the entries it can determine.

**Two things are explicit rather than hidden.** The reference plane sits at the port's outermost cell
rather than on the gridline one half-cell in (worth ~11 % of a 20 mm line's resistance on a coarse
mesh); and R-fed-1's grown feed lead is peeled as the series resistance it is at DC, through
`Y′ = (I + Y·R)⁻¹·Y` — which is defined for a SINGULAR Y, and therefore for the open-port case whose
Z does not exist.

`PecSheetResistance` is **1 µΩ/sq, and the limit is the S CONVERSION rather than the network.** The
nodal solve is happy at any value; `Y → S` inverts `I + Z₀·Y`, and at 1 nΩ/sq that matrix has entries
of ~7e9 on a 50 Ω port, costing ten digits and publishing |S₂₁| = 0.99997 — a worse answer than the
larger resistance gives.

### 5. What is still in the way at the bottom of a band, measured and NOT fixed here

Both refusals the owner reported are gone, and on their own board a DC + 500 MHz + 1 GHz sweep now
writes a `.s4p` whose 0 Hz row reads S₂₁ = S₄₃ = 0.99992 (7.75 mΩ of trace) and exactly 0 between the
two isolated arms. **Two further walls sit above it, both pre-existing, both about DE-EMBEDDING rather
than about the kernel, and both reachable now only because the kernel's own refusal moved out of the
way.**

**(a) The peel's own conditioning.** The port is necessarily a series delta gap, so a₂₁ ∝ ω and D6's
peel divides by a₂₁². Measured on the 20 mm FR-4 hero line, de-embedded, edge mesh off — the phase
slope must be flat in frequency for a quasi-TEM line, and the amplification is `1/|a₂₁|²` read off the
run's own error box:

| f | 1/&#124;a₂₁&#124;² | S₂₁ phase (deg/GHz) | error vs the plateau |
|---|---|---|---|
| 2 GHz | 6.9 | −34.42 | — |
| 1 GHz | 24.7 | −34.41 | 0 % |
| 500 MHz | 96 | −34.23 | 0.6 % |
| 300 MHz | 265 | −33.37 | 3 % |
| 200 MHz | 597 | −31.30 | 9 % |
| 100 MHz | 2,413 | −18.53 | **46 %** |

The calibration's own extracted ε_eff degrades with it (3.30 at 1 GHz → 3.05 at 100 MHz → 2.80 at
50 MHz), and the box's consistency residual stays at 1e-14 throughout, so the ALGEBRA is exact and what
is amplified is a modelling difference between the standard and the DUT. **σ_max ≤ 1 at every point,
so R-prt-15 does not catch it.**

**No note was written for it, deliberately.** The amplification is computable per point but is not a
predictor across mesh settings: with the edge mesh ON the same fixture reads 1/|a₂₁|² = 1.25e5 at
100 MHz with a 14 % phase error, and 312 at 2 GHz with none — so any threshold on it both over- and
under-warns. A note keyed on a quantity that does not track the error would be the "name a remedy that
does not bind" failure one step over. What WOULD close it is a port model whose a₂₁ does not vanish
with ω; `CLAUDE.md` §5 already records that a true edge port would remove it and is not built.

**(b) The two-line calibration's own cost and conditioning at low frequency.** A standard is
`TargetElectricalDegrees` = 60 long and reproduces the DUT's transverse gridlines verbatim, so its N
grows as 1/f: on the owner's board the 100 MHz standard is **9,690 unknowns against a DUT of 311**,
past the dense 5,000 ceiling (it runs with the accelerated solve on, whose ceiling is 12,000). And a
calibration GROUP's modes separate as f·Δℓ, so the same board refuses at 200 MHz (0.178°) and 250 MHz
(0.413°) against `ModeSeparationFloorDegrees` = 0.5 — **while PASSING at 100 MHz**, because
`PlanarCalibration.SelectSeparation` picks a longer standard there. That last one looks like a defect
rather than a limit.

> **§PCAL6 (2026-09-13) took it, and the guess in this paragraph was wrong — but so was PCAL6's own
> remedy, and those points are still refused.** The selection rule is not what refuses them; the
> MEASUREMENT is. `ModeSeparationDegrees` at 200 MHz reads a fraction of the modes' actual distance,
> and what governs it is the SHORT standard's own electrical length — 3 substrate heights is 1.5° at
> 200 MHz — rather than Δℓ. Growing that standard until the measurement agrees was built and then
> removed: it makes the de-embedded answer worse, measured against the cross-section oracle.
> `SelectSeparation` is unchanged and the refusal stands, now reporting both numbers.

## PCAL5 — a calibration group's feed leads, a cut cell's clearance, and a severed conductor (2026-09-12)

Owner report: a two-port coupled section on a real board — an imported PCB, two 254 µm traces
246 µm apart mitring at 45° into 558.8 µm pads — would not simulate. It refused unless "de-embed
outside the calibration's validity" was switched on, and with it on it published **S₁₁ = −0.02 dB and
S₂₁ = −52.6 dB at 1 GHz on a 3.83 mm through line, non-passive at 51 of 51 points.** The PCAL series
had just shipped and its own fixtures all pass; this is what none of them could reach.

**Three independent defects, each of which alone leaves the file unusable.** They were separated by
bisecting the board rather than by reading the code, and each half works on its own:

| what was run | S₁₁ @1 GHz | S₂₁ @1 GHz | passive |
|---|---|---|---|
| one real arm alone — trace + mitre + pad, 2 ports, no neighbour | −17.90 dB | −0.08 dB | 51/51 |
| two straight coupled lines, same dims, 4 ports, no pads | −19.09 dB | −0.11 dB | 51/51 |
| an isolated 254 µm × 3.83 mm line (the textbook answer for a 117 Ω line) | −18.47 dB | −0.07 dB | 51/51 |
| **the board as drawn** | **−0.02 dB** | **−52.6 dB** | **0/51** |
| the board with all three fixed | −19.42 dB | −0.08 dB | 51/51 |

### 1. A calibration group whose members grew a feed lead was declined, and it should not have been

R-pcal4-6 declined any group with a grown lead, reasoning that R-fed-2's peel is
`S_ij *= exp(γ_iℓ_i + γ_jℓ_j)` — one γ per PORT — while a group's region carries one per MODE. The
algebra is right and **the geometry is not**: the members of a group share a reference plane and
therefore the same cross-section question, so `PlanarFeedExtension` grows them all a lead, and those
leads are collinear, equal in length and side by side at the group's own separation. **Together they
ARE a uniform N-conductor section of exactly the cross-section the group's standard reproduces**, so
the peel is a matched length of the GROUP's modes.

The machinery was already there and one line was missing. `PlanarFeedExtension.Peel` is called on
`PlanarDeembed.ApplyBlocks`' output — i.e. in the MODAL basis, before `ModalToTerminal` — so index
`slot[k]` is mode k, and `gam[slot[k]]` was simply never assigned for a grouped port. It is
`gc.Box.Gamma[k]` now.

**The gate that replaces the decline: every member must peel the SAME length**
(`PlanarFeedExtension.CommonPeelLength`). A mode runs on all of the group's conductors at once, so
"how far has this mode travelled" has one answer for the group or none, and `Peel` is index-wise and
can express nothing else. Unequal leads also mean the grown region is not one cross-section — past
the shorter lead's end the other conductor is not there — so the arithmetic and the geometry fail
together.

**That case is NOT reachable by drawing, and the measurement is worth recording because it is why
there is no fixture for it.** A group already requires its members to share a reference plane; the
plane is one cell in from the lead's outer end; and `Extend` quantises the lead by the uniformity
scan's own step. Two pads differing by 300 µm in length produced leads of 2151.563 µm and
1856.25 µm — outer ends 4.69 µm apart — and the run was declined by the PLANE test first. Aligning
the drawn edge to the lead's own end to the nanometre did not close it either. The gate is a guard
on `Peel`'s precondition, unit-tested exactly, and it is what would stop a silent wrong peel if the
plane tolerance were ever loosened.

**`PlanarFeedExtension.PeelLengthM` exists because the subtraction now has two callers** — the peel
and the group's gate — at different times in the run. Two spellings of it would be two chances for
the gate and the peel it gates to disagree, silently, since both produce plausible lengths.

**R-pcal4-1's bit-identity holds and was checked rather than argued**: all five committed PCAL
fixtures (`coupled-pair`, `separated-pair`, `coupled-asym`, `coupled-triple`,
`coupled-pair-passive`) produce Touchstone files identical to HEAD's, line for line, built from a
worktree at HEAD. A group that grew no lead reads peel = 0 for every member and `Peel` returns its
argument untouched.

**The new fixture is `testdata/portcal/pad-coupled-pair`, and the reason it had to exist is the
finding under this one**: every PCAL fixture is a straight line with its ports at the drawn ends, so
**not one of them ever grows a lead**, so R-pcal4-6's decline was never once exercised against a
group it should have allowed. A port that lands on a PAD is what a real board is made of.

### 2. A cut cell's GRID RECTANGLE is not its metal, and the clearance check was reading the rectangle

`PlanarPorts.MeasureFeedClearance` measured every cell by `PlanarCell.XMin…YMax`, whose own doc says
that for a cut cell it BOUNDS the metal rather than equalling it. At a shallow oblique rim the
overhang is most of a cell, so where a port's profile edge falls under it the gap computes as
**exactly zero** — metal that is not there, at a distance that cannot be argued with.

Measured on the owner's file, one setting changed and nothing else:

- `BoundaryCells: Conformal` → *"Port 3's feed clearance is 0 substrate heights — 0 µm to the nearest
  other conductor"*, and the run refuses.
- `BoundaryCells: Staircase` → *"Port 3's feed is clear"*.

It reads `Region` now, for both the transverse test and the longitudinal station. **`Region` is null
on every Manhattan cell, so a staircased mesh is bit-identical** — the same rule R-cut-2 holds
everywhere else.

### 3. A conductor the mesh has SEVERED now refuses, because nothing else could see it

A rooftop exists only where the shared edge of two adjacent cells is swept by metal on both sides
(R-cut-4's `Anchored` test, all-or-nothing over a support's strips). At an oblique rim a coarse mesh
can fail that right across a conductor — and **every observable a solve publishes stays healthy**:
the matrix is well formed, the solve converges, the answer is smooth, and it is **passive at every
frequency**, so R-prt-15's own gate is silent too. What is published is an OPEN CIRCUIT.

`PlanarConductors.FindSeveredConductors` asks whether two ports standing on ONE drawn polygon can
reach each other through the basis graph, and `PlanarSolve` refuses before it fills anything.

**Asking it of the PORTS and the ARTWORK, rather than of the basis graph alone, is the whole design.**
A conformal taper routinely meshes with dozens of cut cells that carry no basis at all — slivers of
its own metal that R-cut-4 declines to drive — and those are separate one-cell "conductors" in the
basis graph on every such run (`PlanarConductors.CarriesCurrent`'s own note records this, and PCAL2's
clearance check was already burned by it once). A whole-graph comparison would fire on all of them.
The port question fires only where the answer actually changes.

**It under-reports on purpose**: two ports on polygons that merely touch are physically one conductor
and are not grouped, and a one-port polygon has nothing to be disconnected from. An under-report
leaves today's behaviour.

**The remedy the refusal names had to be measured, and the obvious one is INERT.** "Raise Cells per
wavelength" is the sentence anyone would write and it does nothing here: the pitch at a mitre is set
by the metal's own width and by the detail floor, so on the engine fixture the mesh is bit-identical
at cells/λ 5, 10, 20 and 40 — severed at all four — and raising `MinCellsAcrossConductor` from 2 to 4
does not clear it either. What was measured to restore conduction is **the edge mesh**, under either
boundary model, and — where the cells are cut — staircasing them. The refusal says both, and says
outright that Cells per wavelength is not a remedy. That is the fourth time this directory has had to
be told to check whether a named remedy binds.

**One thing it turned up that is worth knowing separately**: on a coarse enough mesh a STAIRCASED
mitre severs too, not only a conformal one. The engine fixture is severed with the edge mesh off
under both boundary models. So this is not a conformal-cells defect with a staircase escape hatch; it
is a coarse-rim defect that conformal cells reach sooner.

### 4. What the owner had to do, and what it cost

The board could only be run by switching on "de-embed outside the calibration's validity", which is
the setting that exists to publish a known-bad answer with a caveat on the file. It did exactly that.
The three fixes together mean the same file now runs with the setting OFF and no caveat — which is
the outcome the whole PCAL series was for.

---

## TRP, peak EIRP, and Stop — antenna feedback, 2026-09-11

User feedback relayed by the owner: "do we have TRP, peak EIRP?", and — separately — an EM run that
should be able to finish early without losing what it has solved.

---

### 1. TRP and peak EIRP: two metrics, and one SETTING the registry had never needed

**Why they could not simply be added.** Every other entry in `PlanarMetrics.Registry` is a RATIO — a
directivity, an efficiency, a gain — and a ratio is invariant in the excitation, which is why the
1 V delta gap this kernel drives with has never had to mean anything. TRP and peak EIRP are absolute
powers. There is nothing in a delta-gap solve to derive a watt from, and publishing them against the
gap's own accepted power would put a number in dBm that is an artefact of the excitation.

So the reference is an INPUT: `PlanarMetricSettings.ReferenceInputPowerDbm`, reaching the engine from
`EmSetup.ReferenceInputPowerDbm` (nullable + omitted at default in the `.cem`, so a file written
before it existed loads and re-serialises byte-identically) via `EmRunService`, with a box on the EM
panel gated on the pattern being on.

```
TrpDbm      = P_ref + 10·log10(η_rad · (1 − |Γ|²))
PeakEirpDbm = P_ref + 10·log10(4π·U_peak / P_accepted · (1 − |Γ|²))
            = TrpDbm + DirectivityDbi          ← an identity, not a coincidence
```

The identity holds because TRP·D expands to `P_ref·m·(P_rad/P_acc)·(4π·U_peak/P_rad)`, whose radiated
power cancels. Two separate registry entries compute it from two different intermediates, so an
arithmetic slip in either breaks the gate.

**0 dBm is the default, and it is doing real work.** At 1 mW conducted, peak EIRP in dBm is
numerically the realized gain in dBi and TRP in dBm is the total efficiency in dB — so the two cubes
read as quantities an antenna engineer already has before anybody sets anything, which is also the
convention the over-the-air report this request came from is written in. Set it to a radio's own
conducted power and both become directly comparable against that radio's measured report.

**They refuse on exactly `RealizedGainDbi`'s predicate, and that is structural.** What a transmitter
delivers into an antenna is its AVAILABLE power less the mismatch, so the mismatch factor is in both
of them — and it is the one quantity here that needs the port's own published Γ rather than the raw
delta-gap self-admittance (ANT-12's finding: the raw one reads a matched antenna 15 dB low). The
range check that was inline in `RealizedGainDbi` is now `MismatchUsable`, shared by all three; three
copies of one range test is three places for one of them to drift.

`ReferenceInputPowerDbm` is published as its own cube for `PowerAccepted`'s reason: **a dBm whose
reference is not in the file cannot be reproduced from the file.** It publishes even when the two
that use it refuse.

Gate: five tests in `PlanarMetricsTests` — the identity, the 0 dBm reading, that the reference shifts
BOTH absolutes by exactly itself and moves NOTHING else (invisible on a plot, since every curve keeps
its shape), the staged refusal, and the reference publishing on its own.

#### 1a. Where the reference LIVES — the setting is not the only place it can be set

Owner, immediately after: *"should we put the Reference input power on the trace card, not in the EM
setup? It's a post-processor number."* Correct, and the re-run cost is the whole argument — nothing in
the solve depends on the reference, and the correction from one to another is a subtraction and an
addition, both exact, so charging a user an hours-long sweep for a dB offset charges the price of a
simulation for a shift.

**It is in both places, and they are not duplicates:**

| | |
|---|---|
| `EmSetup.ReferenceInputPowerDbm` | what the RUN bakes in — the `.npy`'s value and what a headless run reports in its notes. |
| `Trace.ReferenceInputPowerDbmOverride` | reads the SAME solved data against another reference, with no re-run. |

**Publishing the reference as a cube is what makes the second one exact**, which is the second thing
that cube has now bought. The display reads what the level is currently against, subtracts it and adds
the override; without it the shift could only be a guess.

**Detection is a RELATIONSHIP, not a name list** — `RfCore.Data.LevelReference`, mirroring
`NetworkMetrics.IsNetworkParamCubeSpec`: a cube is a re-referenceable level when its own unit is `dBm`
AND its group publishes `ReferenceInputPowerDbm`. So the generic Data Display needs to know nothing
about this kernel's metric names, and anything that later opts in by carrying the unit and the sibling
cube gets the control for free. It cost the fix ANT-7 §8 had already recorded as missing: **the metric
registry has carried a `Unit` per metric since ANT-5 and the publish threw it away.** `AddMetrics` now
sets it, and `AddFarField` sets `V` / `W/sr` on the three pattern cubes.

Two placement decisions worth keeping:

- **The offset is applied in `Trace.RectY`**, the one funnel every displayed value passes through —
  curve, table cell, marker readout, pattern radius — so a re-referenced trace cannot be one number on
  the plot and another in its own info box.
- **The reference is stamped BEFORE the bind**, at the top of `SetCubeDataFromCore` and before its
  `IsCubeBound` guard: it shifts the VALUE, and `SetCubeData` builds the path, so a reference handed
  over afterwards would not be in the geometry until the next rebuild — the same trap the whole-plane
  back branch hit.

**The label states the reference ALWAYS, not only when overridden**, and in `TraceLabeler` rather than
`RectYLabel` so the rectangular Y axis and the polar label strip cannot say different things. A picture
of a level carries no file; without the suffix there is no way to tell an overridden trace from an
un-overridden one.

### 2. Stop: finish now and keep what is solved

`RunControl` has always documented that **cancelling abandons the run** — a sweep's per-point results
stack along an axis of known length, so a half-finished one has no shape to publish in. That is right
for "I did not mean to start this" and wrong for "this has found what I needed": on an EM run the
resonance search keeps adding full-wave points long after the resonance is on screen, and nothing
outside it can see how many more it intends to take.

`RunControl.StopRequested` / `RequestStop()`, beside the token and read at the same boundaries.
**Advisory** — nothing throws, nothing is checked automatically, and an engine that ignores it is
correct — which is what makes it safe to hang on the one control object every engine already takes.
**A `Child()` does not inherit it**: the outer loop stops adding POINTS, the inner one always finishes
the point it was given, or a sweep would end up with a shorter axis inside a longer one.

Three places read it in `PlanarSolve`, and what each publishes is forced by what it can honestly
publish:

| path | on stop |
|---|---|
| fixed grid | the frequency axis is the PREFIX that was solved. A shorter sweep is a sweep; inventing the rest would publish an interpolation nobody asked for. |
| adaptive | the full REQUESTED grid, modelled from the solved nodes — which is what that path does at every budget. The note gives the disagreement actually reached rather than the tolerance asked for. |
| resonance search | `PlanarResonanceSearch.Search(…, stopped:)`, checked where the point budget already is, because they are the same kind of limit. Reported through `StoppedEarly`, SEPARATE from `CapBound`: "you set the cap too low" and "you pressed Stop" are different things to tell someone. |

**The note is mandatory and that is the point.** A stopped run is a complete, ordinary result
downstream — same DataSet, same cubes, same `.snp` — so nothing else distinguishes it. A stop inside
`Locate` keeps the bracket it reached, whose ends are SOLVED points, so the resonance reported from it
is real and its `LocatedToHz` says how tightly it was pinned (measured: 3.9 MHz against a converged
sub-kHz).

UI: `RunCancellation` gained an optional `stop` action, and the progress row's context menu a **Stop**
item ABOVE Cancel — hidden rather than greyed where none exists, since only the EM run offers one and
a permanently-disabled item teaches nothing.

Gate: three tests in `ResonanceSearchTests` — that a stop ceases probing within one probe, keeps what
it found and says so; that a stop which never fires is byte-identical to no stop predicate at all
(the property that makes the parameter safe to have added to every caller); and that a stopped
fixed-grid sweep publishes its prefix and names what is missing.

### 3. The far-field block's progress bar completed every time it was looked at

Reported alongside: with the resonance search on, the sweep row's point count "sat still for a VERY
long number of work cycles" and then climbed again.

The count was telling the truth — no new POINT is solved in the far-field block — but the stage row
beside it was no help either. `FarFieldAt` began a stage PER PATTERN with a total of the PORT count,
so on a one-port it read 1 of 1, finished, and began again, over and over, for however many minutes
the block took. **A bar that completes every time it is looked at says nothing about a block that is
running for minutes.**

The adaptive path's far-field block is now ONE stage counted in PATTERNS, which is the honest
denominator — it is the one part of a sweep whose cost is a known number of equal pieces.
`FarFieldAt` takes `ownStage`, false when the caller is counting. The outer counter is deliberately
left alone: it counts points solved, and none are. (The climb the reporter saw afterwards was the
search's own probes, which do solve.)

## RP-2c: de-embedding a coplanar edge port — the STANDARD changed, the algebra did not (2026-09-10)

`brief-em-return-plane-2c-coplanar-deembedding.md`, the arrival of the refusal RP-2a left behind.
**Scope stayed where RP-2a's did — `src/Engine/Mom` only.** Gate:
`tests/Engine.Tests/Mom/CoplanarDeembedTests.cs`, 21 tests, ~8 s.

### Gate 2's number, first, because §5 asks for it first

**|S₁₁| at the two calibration lengths is 2.7e-16 … 4.0e-15 across five geometries** — L8d's own
microstrip figure (8.5e-16) and the measurement that says the standard IS the port's neighbourhood.
Four equations fix four unknowns, so it is machine zero or it is nothing; a microstrip standard for a
coplanar port would put it at the 1e-2 level.

| W | S | h | cells/λ | across | DUT N | standard N | worst \|S₁₁\| |
|---|---|---|---|---|---|---|---|
| 0.3 mm | 0.15 mm | 2.5 mm | 20 | 12 | 104 | 48 / 62 | **1.13e-15** |
| 0.3 mm | 0.15 mm | 2.5 mm | 20 | 24 | 194 | 90 / 116 | **3.97e-15** |
| 0.3 mm | 0.15 mm | 5.0 mm | 20 | 6 | 59 | 43 / 51 | **1.81e-15** |
| 0.6 mm | 0.10 mm | 5.0 mm | 20 | 12 | 104 | 76 / 90 | **1.19e-15** |
| 0.2 mm | 0.60 mm | 5.0 mm | 20 | 12 | 104 | 104 / 132 | **9.29e-16** |

### THE FIXTURES ARE CHOSEN, AND THAT IS THE ONE THING TO READ BEFORE TRUSTING THE TABLE

On other geometries the same measurement sits at **1e-6 rather than 1e-15**, and the cause is not the
coplanar standard. A standard is mirror-symmetric by construction, so its raw `S₁₁` and `S₂₂` must be
equal; where they are not, `SolveErrorBox` averages them (the right use of a known symmetry) and the
difference comes back as **exactly** the de-embedded |S₁₁| — the two track each other one for one on
every fixture measured.

**The control settles the attribution: the same mesh driven by ordinary GROUND-REFERENCED ports shows
the same |S₁₁ − S₂₂| to within a couple of per cent** (1.14e-6 vs 1.17e-6 at 30 mm; 1.96e-6 vs
1.70e-6 at 40 mm; 9.67e-7 vs 9.69e-7 at 30 mm / across 24). So it is a property of the fill and the
solve on that geometry, not of the two-cut incidence column and not of the coplanar cross-section.
`CoplanarDeembedTests.TheGate2Floor_IsARawMirrorAsymmetryTheGroundReferencedPortShowsToo` asserts that
ratio, so the claim is runnable rather than prose.

What it is *not*, ruled out by measurement rather than by argument:
- **not the factorisation** — `UseSymmetricFactorization` true and false give the identical 2.871e-6;
- **not non-determinism** — the same mesh solved twice in one process is bit-identical, and the cap-1
  and automatic-parallelism answers agree to the last bit;
- **not the quadrature settings** — extraction order Constant vs Linear and `UseRadialTable` on vs off
  move it by 0.1%, not by decades;
- **not gridline round-off in the standard's own partition** — a fixture whose x grid is *exactly*
  mirror-symmetric (residual 0.000e0) still reads 1.17e-6.

It is left as a bounded, named floor rather than chased: it is off RP-2c's path, and P5 already
records that per-entry agreement at 1e-12 is unattainable in this fill for absolute-coordinate
reasons. **A gate 2 fixture must be checked against its own raw |S₁₁ − S₂₂| before its |S₁₁| is read
as an accuracy statement.**

### What was built

**`PlanarPortCrossSection`** — the transverse metal profile at a conductor-referenced EDGE port's
reference plane, captured from the DUT's own mesh at the cut (gridlines, a metal flag per interval,
the two driven conductors' index ranges, and how many conductors cross the plane), trimmed to the
outermost metal. It hangs off `PlanarPortResolution` as a nullable companion exactly as RP-2a's
`PlanarPortTerminal` does, so every port that predates it reads the same bits. **Only an EDGE port
gets one** — an internal delta gap has no feed, no error box and no standard, so its resolution is
RP-2a's bit for bit.

**`PlanarCalibration.BuildCoplanarLine`** — D4 over that profile: the same longitudinal partition
(end run, bulk fill, mirrored end run) with cells emitted only where the profile says metal, so no
basis crosses the slot. **The standard's own two ports are two-cut EDGE ports at one station**,
resolved through `PlanarPorts` like any other, which is what puts RP-2a's skew, level and
same-conductor checks on the standard as well as on the DUT (R-rp2c-2).

**D7's electrostatics became the MODE's.** A microstrip standard's C_pul is the whole sheet at 1 V
over the plane. A coplanar port drives the voltage BETWEEN two conductors, so the problem is +½ V on
the signal and −½ V on the return and the capacitance is `(Q⁺ − Q⁻)/2` per volt — the average rather
than one plate's charge, so the answer does not depend on which conductor the user named as the
return. `PlanarStandard` carries the per-cell potential and weight; `StaticCapacitance` and
`PlanarStaticAim.ModalCapacitance` take them. **Totalling the sheet at 1 V instead measures the COMMON
mode** — a complete, plausible reference impedance for a mode the port does not drive.

### The algebra was not re-derived (R-rp2c-3), and here is the check that says so

γ from ½·tr(M), the branch continuation and the T-matrix cascade are untouched. The evidence that
they did not need touching is gate 3: **the two-line trace and a travelling-wave fit that shares no
algebra with it agree on β to 2.0e-5 … 1.1e-4** (across = 24 / 12 / 6), inside L8d's own
2.5e-4 … 3.9e-3 microstrip band.

### Gate 4 — the closed form, with the feed removed

A CPS line in air against `Z = η₀·K(k)/K(k′)`, de-embedded:

| across | N | Z_c | closed form | ratio | β/k₀ | C_pul ÷ closed form |
|---|---|---|---|---|---|---|
| 6  | 59  | 206.80 Ω | 198.21 Ω | 1.0433 | 0.9651 | 0.9250 |
| 12 | 104 | 200.40 Ω | 198.21 Ω | 1.0110 | 0.9649 | 0.9544 |
| 24 | 194 | 196.41 Ω | 198.21 Ω | **0.9909** | 0.9649 | 0.9737 |

**Read the last three columns together, not the ratio alone.** The medium is air, so β must be k₀
*exactly* and C_pul must be `1/(cZ_c)` *exactly*; the solve gets both low and **the two errors partly
cancel in their quotient**. Quoting 0.99× as the accuracy of this kernel on a coplanar line would be
reporting a cancellation. The transverse refinement is what moves them, and β/k₀ barely responds to it
at all (0.9651 → 0.9649) — that is longitudinal discretisation, and it reaches 0.981 at cells/λ = 40.
None of it is the calibration's: the travelling-wave oracle reads the same β to 1e-4.

### The mixed run, and what a coplanar standard costs (R-rp2c-4)

One solve, one medium, port 1 to the plane and port 2 to a drawn strip: it runs, and the two ports get
differently-shaped standards (Z_c 294 Ω and 202 Ω — a calibration that had quietly shared one standard
would report one number twice). **One DCIM fit still serves the DUT and every standard**, because the
fit's key is (slab, frequency) and a standard's SHAPE is not in it — which is what keeps two shapes
affordable.

**The cost is unknowns, measured on ONE port at ONE target length so nothing else differs: the
coplanar standard is N = 269 (154 cells) against the single-conductor standard's N = 94 (56 cells) —
2.86× the unknowns and 2.27× the fill-and-solve time.** Sub-quadratic because at these sizes the cost
is the cores and the quadrature rather than the factorisation. On the whole mixed run the standards
came to 4.07× the DUT's N against 2.39× for the same geometry with both ports on the plane.

### §5's third question: the coupling residual is 35-140× SMALLER with coplanar grounds

L8d identified the drift away from the calibration lengths as direct radiative and surface-wave
coupling between the ports — f², and not monotone in length. Asked again of a coplanar pair, **against
a microstrip control on the same slab, the same mesh and the same frequency** (the comparison is
controlled; the earlier L8d numbers are on FR-4 and are not):

| f | coplanar pair | microstrip control | ratio |
|---|---|---|---|
| 5 GHz | 8.2e-6 | 1.1e-3 | 134× |
| 10 GHz | 2.3e-5 | 3.2e-3 | 139× |
| 20 GHz | 1.6e-4 | 5.4e-3 | 35× |

The rough f² scaling survives; the magnitude does not. The coplanar mode's return current is beside
the line rather than in the plane below it, so much less of the field goes around the structure — and
a de-embedded coplanar answer is correspondingly better than the "few 1e-3 at 2 GHz, few 1e-2 at
10 GHz" §10.6 records for microstrip.

### What is refused, and why each is a refusal rather than a guess

- **A THIRD conductor crossing the reference plane.** The standard drives the port's PAIR; a CPW's far
  ground strip has no stated potential, and the two reasonable readings — bonded to the return, or
  floating at zero net charge — are far apart. Refused at calibration setup, naming the count and four
  remedies (join the grounds before the plane, move the station, cut an interior gap, or read the raw
  solve). **This is the narrowing worth knowing about: a three-conductor CPW whose port names one
  ground is not de-embedded today**, and the object it needs is a multiconductor reference impedance
  (a bordered electrostatic solve with a floating conductor at zero net charge, under the accelerator
  as well as the dense path), not a wider tolerance.
- **A conformal cut cell at a conductor-referenced port.** The single-conductor path absorbs a cut by
  re-centring the cross-section on the metal's own extents; re-centring two conductors independently
  moves the SLOT between them, and the slot is most of what sets a coplanar line's impedance. The
  remedy named is the setting that caused it.
- **An EDGE pair whose two cuts are on different LEVELS**, even when both were stated. RP-2a permits
  that for an internal delta gap and is right to — it has no standard. An edge port's standard is a
  uniform line on ONE level (D3), and building it on the signal's level anyway would put the return
  conductor beside the signal instead of under it: a plausible s-parameter set for a line nobody has.
  The remedy named is the internal delta gap, which supports it today.

### Three things that had to change around the edges, each of which would have failed silently

- **`SameCrossSection` compares the whole cross-section**, not just the driven run. Two ports whose
  signal conductors match cell for cell can still need different standards — a different slot, a
  different return width, a return on the other side — and sharing one would calibrate port 2 against
  port 1's line.
- **`CheckFeedClearance` asks about the cross-section's span**, not the signal run's. Asked of the
  signal alone it reports the port's own return conductor as un-removed neighbouring metal, on every
  coplanar port, always — the same unclearable-warning failure the 2026-08-12 fix removed for a
  different reason.
- **`PlanarFeedExtension` grows BOTH conductors, by the larger shortfall.** Growing the signal's lead
  alone skews the pair, and the port is then refused at resolution — with a message about the user's
  own artwork, which is no longer what moved it. If either terminal's lead is obstructed, neither
  grows and the note says why.

### The one structural change on the shipped path, and its gate

D4's longitudinal partition was lifted out of `BuildLine` into `LongitudinalPartition` so the coplanar
builder could use it rather than copy it — a second copy of that arithmetic is a second chance for the
two standards to discretise a length differently. `CoplanarDeembedTests.Gate1` writes the pre-RP-2c
body out as a named reference and compares every gridline **bit for bit**, the way RP-2a's own gate 1
wrote out the pre-RP-2a incidence loop; a ground-referenced standard also still carries no mode
potential and no cross-section, so its electrostatics is the shipped all-ones route untouched.

**Two RP-2a tests asserted the refusal this brief removes and were rewritten rather than deleted** —
`TwoCutPortTests.Gate6_…ResolvesSinceRp2cBuiltItsStandard` now asserts the capability and its
cross-section, and `PlanarPortTests.T0_6` asserts the part neither phase changed (a conductor
reference with no return point is refused; there is no nearest-conductor search). Nothing else was
un-refused: the via-to-plane port still takes no reference, and RP-2a's skew, level, same-row and
same-conductor checks all still fire — now on edge ports too, which is where they had never run. One
refusal was ADDED rather than removed (the level-spanning edge pair above), which is the shape a
capability arriving usually has: the new path has a boundary of its own.

## RP-2a: the two-cut port — what it costs, and the factor of four nobody would have seen (2026-09-10)

`brief-em-return-plane-2a-two-cut-port-kernel.md`, built on the measurement in the section below.
**Scope was the engine only** — `PlanarPort`, `PlanarPortResolution`, `PlanarExcitation` — and it
stayed there: nothing in `src/Design`, nothing in the `.clay`, no UI. Gate:
`tests/Engine.Tests/Mom/TwoCutPortTests.cs`, 22 tests, ~1 s.

### The bit-identity gate, run before a line of feature code was written (R-rp2a-7)

An existing de-embedded two-port line (`Fr4Line`, coarse mesh, 1/2/5 GHz) and a three-port fixture
mixing two edge ports with an internal delta gap were solved and their s-matrices dumped as the raw
bit patterns of all 42 doubles, before the change and again after. **Identical, every bit.** That is
the whole L8/L9 acceptance set's protection and it is why the two weights are written the way they
are: `PositiveWeight` **is** `IncidenceSign` for a ground-referenced port — the same value, not a
rounded relative of it — and the negative block is guarded by `Negative is not null` rather than
added unconditionally, because `x + 0` is `x` for every double **except −0**, and an unconditional
empty sum would silently turn a −0 component into +0.

That one-off comparison cannot live in the tree (there is no "before" to compare to once the change
lands, and a committed golden of raw doubles would be a claim about this machine's vectorisation
rather than about this code). What is committed instead is the same claim made **in process**: the
pre-RP-2a arithmetic written out as a named reference and compared bit for bit at each of the three
sites, including `Solve`'s Y assembly rebuilt from the solution's own current vectors.

### THE NORMALISATION IS A FACTOR OF FOUR, NOT TWO — and it is the whole reason §2a exists

The brief costed the wrong normalisation at a factor of two in `Z_c`. **It is four**, and the
arithmetic is worth stating because the same reasoning fixes the sign of the whole thing:

With weight `w` on the two blocks, the two gap voltages add around the loop, so the impressed EMF is
`2w` — and the port current is read back through the *same* column, `i = w(I⁺ − I⁻) = 2w·I_line` when
the return conductor carries the whole return. So `Z₁₁ = v/i = Z_true/(4w²)`, and **only `w = ½`**
makes the port report the impedance it is actually looking into. `w = 1` reports a **quarter** of it.
The single-cut ground-referenced port is the same formula with one gap — `EMF = w`, `i = w·I_line`,
`Z₁₁ = Z_true/w²` — so its `w = 1` is correct and the two constants are not the same number by
coincidence.

`w = 1` produces a complete, plausible s-matrix that is **reciprocal and passive**, so gates 3 and 4
do not catch it. Only an external oracle does.

### Gate 2: coplanar strips against conformal mapping — and it landed at 0.9999 on the first try

The fixture is a **coplanar-strips line in AIR** (ε_eff = 1 exactly, β = k₀ exactly) with the ground
plane put ~10 transverse extents below so its image is a perturbation, driven by ONE two-cut internal
delta gap at the centre. The port is a series source, so it looks into two open-circuited CPS stubs in
series: `Z₁₁ = −2j·Z_c·cot(βℓ/2)` with `Z_c = η₀·K(k)/K(k′)`, `k = S/(S+2W)` — exact for
zero-thickness perfect conductors in a homogeneous medium (its CPW dual satisfies
`Z_CPW·Z_CPS = (η₀/2)²` identically), and dependent on nothing in this repository but an AGM.

Measured, `Im(Z₁₁)` ÷ closed form, across eleven combinations:

| W | S | h | f | θ = βℓ/2 | cells/λ | min cells | N | ratio |
|---|---|---|---|---|---|---|---|---|
| 0.3 mm | 0.15 mm | 2.5 mm | 10 GHz | 1.0 | 10 | 3 | 34 | **0.9999** |
| 0.3 mm | 0.15 mm | 2.5 mm | 10 GHz | 1.0 | 40 | 3 | 90 | 1.0083 |
| 0.3 mm | 0.15 mm | 2.5 mm | 10 GHz | 1.0 | 10 | 6 | 120 | 0.9669 |
| 0.3 mm | 0.15 mm | 2.5 mm | 10 GHz | 1.0 | 40 | 8 | 374 | 0.9630 |
| 0.3 mm | 0.15 mm | 2.5 mm | 10 GHz | 0.4 | 10 | 3 | 24 | 1.0284 |
| 0.3 mm | 0.15 mm | 2.5 mm | 10 GHz | 1.4 | 10 | 3 | 44 | 1.0982 |
| 0.3 mm | 0.15 mm | 2.5 mm | 4 GHz | 1.0 | 20 | 4 | 90 | 1.0518 |
| 0.3 mm | 0.15 mm | 2.5 mm | 20 GHz | 1.0 | 20 | 4 | 90 | 0.9373 |
| 0.3 mm | 0.15 mm | 5.0 mm | 10 GHz | 1.0 | 20 | 4 | 90 | 0.9991 |
| 0.6 mm | 0.10 mm | 2.5 mm | 10 GHz | 1.0 | 20 | 4 | 90 | 1.0176 |
| 0.2 mm | 0.60 mm | 5.0 mm | 10 GHz | 1.0 | 20 | 4 | 90 | 0.9485 |

**Every one inside ±10%, and `w = 1` would have put every one at 0.25.** `Re(Z₁₁)` is under 0.05% of
`|Im|` on the well-resolved cases, as an open stub in a lossless medium must be. The residual spread
is discretisation, the finite ground plane and open-end fringing; it is not a scaling question, and
the closed form's own idealisation (zero-thickness metal, no ground plane) accounts for the sign of
the drift at the extremes. The θ = 1.4 rung is the loosest because it is nearest the λ/4 pole, where
`cot` is small and everything is amplified.

**Do not read the 0.9999 as accuracy.** It is one rung of a spread that runs 0.937–1.098, on a
34-unknown mesh; the *conclusion* it supports is which of two normalisations is right, and that
question only needs a factor of two to be resolvable.

### The audit was complete, and one of the three sites went away

R-rp2a-9 said the one-signed-block assumption lives in exactly three places in `PlanarExcitation` and
**that is where it was** — no fourth site turned up anywhere in `src`. `Solve`'s Y assembly is now a
call to `PortCurrent` rather than a second copy of the same loop, which is that file's own headline
rule (a second copy of the sign convention is where it drifts) and leaves two sites, not three.

Everything else keys off `PlanarPortResolution`, and R-rp2a-10's "grow, do not fork" is what made the
rest cheap: the negative terminal's counterparts hang off a nullable `PlanarPortTerminal`, so a
ground-referenced resolution is the record it always was, field for field, and every consumer that
never asks reads the same bits. **`PlanarPortTerminal` deliberately carries no `Side`, `Direction`,
`Z0` or `IncidenceSign`** — a port has one polarity and one reference impedance, and a second copy of
either is how the two would drift apart.

Nothing gates on `Kind == InternalDeltaGap` had to be touched: `PlanarFeedExtension`,
`PlanarGroundPath` and `PlanarMeshPitchField` all gate on `Edge` or `Internal`, and
`IsDeembeddable` already excluded an interior cut from every calibration path.

### The two cuts landed on one station on every fixture tried

§6 asks whether the mesher's own gridlines ever force a skew on correct input. **They did not, on any
of the eleven CPS geometries, the FR-4 mixed fixture or the two-level fixture.** The reason is
structural rather than luck: the longitudinal gridlines are a property of the whole mesh, not of a
conductor, so two points at one `x` see the same candidate set — and both cuts are chosen by the same
nearest-usable-gridline rule. The skew refusal fires only when the two points genuinely disagree, or
when the mesh offers one conductor a rooftop at a gridline and not the other, which refining fixes.

### What the pair checks refuse, and why each is a refusal rather than an adjustment

- **Different stations** — a port plus a length of line, which solves to a complete and plausible
  s-matrix for a structure nobody drew. The refusal names both coordinates and the distance.
- **Levels inferred apart** — L9d/D2's reason with two chances instead of one. Stating BOTH levels is
  what permits a port whose cuts span levels; inferring them and landing apart is refused.
- **One rooftop row** — the two blocks cancel exactly, which is a short across the port.
- **One conductor** — an ordinary internal delta gap wearing a costume. Tested by 4-connectivity over
  the mesh's own cells on the cut's level, deliberately **in-plane only**: two conductors joined by a
  via elsewhere are not caught, and are not claimed to be. That is a short in the structure, which the
  solve reports as one, rather than a port that resolved to the wrong thing.

`CoplanarGround` and `SecondConductor` are **one mechanism and one code path** (R-rp2a-12); both enum
members survive because the note reads differently — a ground strip against a second signal line — and
because a coplanar calibration will read them differently again.

### The refusal that stays

A conductor-referenced **EDGE** port is refused, and the refusal names the missing capability rather
than a phase (R-mom-17): a coplanar calibration standard — a coplanar line with its own `Z_c`, `β` and
static capacitance. `PlanarCalibration` builds uniform single conductors over the plane and
`PlanarPortResolution`'s cross-section fields describe one conductor, so calibrating against those
would publish s-parameters that are plausible and referenced to nothing. The refusal offers the remedy
that works today — cut it as an interior gap, which has no feed and needs no standard. An internal
(via-to-plane) port takes no reference at all: its negative terminal *is* the plane, by construction.

## RP-2's first measurement: the mesh cannot span a slot, and never will (2026-09-10)

`brief-em-return-plane-2-per-port-reference.md` asks, before any solver work, whether the surface
mesher produces cells spanning a slot between two conductors — R-rp2-3, on which the whole of that
brief rests. **It was measured, the answer is no, and the reason is structural rather than an
omission.** The brief was too large for one round on top of that, so it is superseded by RP-2a/2b/2c
(see `docs/sonnet-briefs/`); this section is the measurement and the audit those three are built on.

### The measurement

`CoplanarSlotMeshTests` meshes a conductor-backed CPW cross-section — centre strip, two slots, wide
grounds — at both the coarse and the shipping mesh settings, over slot widths from 1 mm down to
50 µm. **Bases spanning a slot: zero, in all sixteen cases.**

| W | S | settings | N | min cell edge | grid rows in slot | cells in slot | bases spanning slot |
|---|---|---|---|---|---|---|---|
| 2.9 mm | 1 mm | coarse | 86 | 600 µm | 2 | 0 | **0** |
| 2.9 mm | 1 mm | shipping | 585 | 85.2 µm | 6 | 0 | **0** |
| 2.9 mm | 0.3 mm | coarse | 86 | 600 µm | 1 | 0 | **0** |
| 2.9 mm | 0.3 mm | shipping | 585 | 85.2 µm | 3 | 0 | **0** |
| 2.9 mm | 0.1 mm | coarse | 86 | 600 µm | 1 | 0 | **0** |
| 2.9 mm | 0.1 mm | shipping | 585 | 85.2 µm | 2 | 0 | **0** |
| 1.0 mm | 50 µm | coarse | 184 | 250 µm | 1 | 0 | **0** |
| 1.0 mm | 50 µm | shipping | 1,119 | 29.4 µm | 2 | 0 | **0** |

Note the 50 µm slot at the coarse settings: the slot is **one twelfth of the bulk cell size** and
still owns a grid row of its own. That is the finding, not a coincidence.

### Why it is structural, and why refining cannot change it

Three facts of `SurfaceMesher`, composed:

1. **Every polygon edge is a HARD gridline** (`CollectBoundaryLines`), so both edges of a slot are
   gridlines regardless of the requested cell size.
2. **A cell exists only where the grid row's CENTRE is inside metal** (`RowSpans`, and the conformal
   path's equivalent). The row between two hard lines that bound a slot has its centre in the slot.
3. **A basis is a pair of GRID-ADJACENT cells** (`cellAt[iy*nx+ix]` vs `ix+1`), so two conductors
   with an empty column between them are not adjacent and produce no basis.

A slot of nonzero width therefore always contains at least one metal-free grid row, and refining the
mesh **adds** rows to the slot — it can never remove the last one. This is not a threshold that a
finer mesh crosses. **R-rp2-3 as literally written — "the gap is across the SLOT and the mesh must
carry cells that span it" — cannot be satisfied by this mesher, and making it so would mean a new
basis family over non-metal (a slot/magnetic-frill basis), which is a far larger question than a
per-port reference.**

The test asserts the state of the world rather than a wish, and it is worth keeping in that form: a
basis appearing across a slot would mean two conductors shorted through the mesh, which is worse than
a missing feature.

### What this leaves reachable, and it is the whole capability

A conductor-referenced port does **not** need a basis across the slot. It needs **two cuts, one in
each conductor**, driven against each other — the incidence column carries a signed block on the
signal conductor's row and the opposite block on the return conductor's row. Each cut is an ordinary
delta gap in its own metal, so R-rp2-3's real requirement ("a real gap in the mesh, not a pair of
points") is met per conductor, and **no new basis family and no mesh change are involved**. That is
what RP-2a builds, and it is the same `Y = BᵀZ⁻¹B` with a two-entry column instead of a one-entry one.

The normalisation of those two blocks is the part that must be **measured, not reasoned into place**:
±1 on both blocks impresses twice the loop voltage that ±½ does, and the two differ by a clean factor
of two in `Z_c` — a complete, plausible, wrong answer of exactly the shape this file already records
for the internal port's sign. RP-2a gates it against a CPW closed form.

### The audit: where `GroundPlane` was assumed rather than checked

The brief asks for this specifically, and the answer is better than expected — the "one signed row
per port" assumption is spelled in **one file, three times**, all in `PlanarExcitation`:

- `RightHandSide` — `foreach (int m in port.BasisIndices) rhs[m] = port.IncidenceSign;`
- `Solve`'s Y assembly — `sum += col[m]` over one index set, times one sign.
- `PortCurrent` — the same sum, times the same sign.

Everything else keys off `PlanarPortResolution`, whose shape is the real constraint: `BasisIndices`,
`WidthM`, `ReferencePlaneM`, `OuterEdgeM`, `TransverseLines` and `LongitudinalRunM` are all
**singular** — one conductor, one cut, one cross-section. `PlanarCalibration` copies
`TransverseLines` verbatim into its standards (D4), so a second conductor is invisible to the
calibration path until that record grows a second set. **That, not the excitation, is the reason
RP-2c (de-embedding a coplanar EDGE port) is a separate brief from RP-2a.**

Two prose sites state the plane-as-negative-terminal rule and become false for a mixed run:
`PlanarExtractor.cs`'s "That plane is the negative terminal of every port in this run and is not
selectable per port" (both spellings, overridden and inferred), and `PlanarPort.cs`'s D3 header.
No *code* was found that silently assumes it beyond the three lines above.

## The internal port — a port from the metal to the ground plane (2026-08-25)

The third of §10.6's port types, and the one the design note had written down as *not* built with a
costing attached. That costing was wrong in the encouraging direction, and the reasons are worth
keeping.

**`PlanarPortKind.Internal`.** The port is placed on the metal and means "here, referenced to
ground"; its + terminal is the metal, its − terminal is the stackup's ground plane. It drives the
ground-attachment bases of the via under it — every cell of that footprint, since they are one
conductor at one potential.

**Findings.**

1. **The excitation is D1's after all, and the design note's stated reason for doubting that was
   about the wrong integral.** §10.6 recorded that an attachment basis "has a different
   normalisation… `NetCharge` is exactly −1, and L9c's `∫∇·f dS = 0` explicitly does not hold for
   it. The incidence matrix and the reaction integral both need their own derivation." The charge
   facts are true and are properties of the FILL, which was already built and gated; a delta gap's
   reaction is an integral against the CURRENT, and the attachment basis carries unit total current
   across the connection it spans exactly as a rooftop does across its shared edge. So
   `⟨f_m, E^imp⟩ = v` holds exactly, with no footprint area and no quadrature in it, and the port is
   one more ±1 row of the same incidence matrix. **No new basis, no new excitation, no new algebra.**

2. **THE SIGN WAS DERIVED WRONG AND THE MEASUREMENT CAUGHT IT.** The first implementation took
   `IncidenceSign = −1`, from a written derivation of which lip of the gap is the + terminal (the
   impressed field points from + to −, so the + lip is the one on the −f side, so a downward-flowing
   positive current puts + on the metal…). It produced a complete, plausible s-matrix with |S₁₃|
   right to two figures and **every term through the port turned by π**. The convention is `+1` —
   the same "current flows into the structure" rule every other port here uses.
   **A port's sign is unobservable through any termination**: every reduction carries `S_i3·S_3j`
   and both factors flip together, so no amount of loading the port could have shown it. What shows
   it is a structure whose answer is known independently — a short line with a via to ground at its
   centre is three 50 Ω ports meeting at one node above the plane, S_ii = −1/3 and S_ij = **+2/3**.
   That gate exists (`InternalPortTests.ASmallStructureAtLowFrequency…`) and it is the reason the
   released sign is right.

3. **A centred internal port measures S₁₃ = +S₂₃**, against the centred delta gap's S₁₃ = −S₂₃.
   The two gates sit beside each other on purpose: it is the measurement that distinguishes a
   ground-returning port from a series one, and a magnitude plot never would.

4. **Shorting the port reproduces the plain board**, to < 1e-9 — reducing the 3-port with Γ₃ = −1
   against an ordinary 2-port solve of the same artwork. A port is a gap with a source in it, so
   terminating that gap in a short is the structure with no port on it: an end-to-end oracle for
   everything between the incidence row and the published matrix, needing no external data.

**The via is the SOLVER's to build (owner, same day).** Requiring the user to draw a via first was
the original build and was wrong for the workflow: the port is placed on the metal, and whether the
drawing happens to have a via there is a question about the drawing. `PlanarGroundPath` runs before
meshing, beside R-fed-1's feed extension and by the same three rules — a drawn via wins, a missing
one is built, and what was built is reported by port and by size. The problem reaches the mesher **by
reference** when nothing was added, so a board that draws its own vias is bit-identical.

**Its size is a PROCESS dimension, not a mesh cell**, and that choice is load-bearing: the built
via's inductance is part of the port's answer, so sizing it from the mesh would make the answer a
function of *Cells per wavelength* — refining the mesh, the one thing a user does to converge a
result, would move it for a reason unrelated to convergence. The order is the technology's default
via drill, then its default pad, then (Ui-side, reported as the rule of thumb it is) a quarter of the
substrate height. The stackup's own `Via` entries carry a fill, a wall and a span but **never a
diameter**, so "this technology declares no via" is not a reason to refuse the port.

**Cost of the four physics gates: they are `Category=Benchmark`** (5.5–11.5 s each, measured). Not
because they measure time — they assert physics — but because a via-bearing fill costs seconds per
frequency whatever the mesh: the DCIM fit count for a problem carrying vertical current is a fixed
per-frequency cost, so shrinking the board does not shrink it. The default gate keeps the resolution,
the refusals and a solve-free incidence-row wiring test, which is where a change to this area breaks
first.

**`src/Engine/Mom/CLAUDE.md` §3.4 still says "`PlanarPortKind` — TWO port types" and lists the
kinds; that sentence is superseded by this entry.** It was left alone deliberately (this repository's
standing instruction is that findings go here, never into a `CLAUDE.md`).

### What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarPort.cs` — the third enum value, `Direction`/`IncidenceSign` for it,
  `PlanarPortResolution.FootprintAreaM2`, `TryResolveInternal` (footprint walk by 4-connectivity,
  two refusals), and its own `Describe`.
- `src/Engine/Mom/PlanarGroundPath.cs` — new: the path the port grows for itself.
- `src/Engine/Mom/PlanarKernel.cs` — one call, before the feed extension, on both mesh paths.
- `src/Engine/Mom/PlanarFeedExtension.cs` — gated on `Kind != Edge` rather than on the gap alone.
- `src/Engine/Mom/PlanarSolve.cs` — the not-de-embedded note now lists the two internal kinds apart.
- Ui: `EmPortExtraction` (kind, the built path and its width, notes and refusals),
  `EmSetupModel.DeclaresInternalPort`, `EmRunService.InternalPortNeedsFullWave`, the panel row and
  its type list, `PlanarPortKindNameConverter`, and the layout mark
  (`LayoutRenderer.DrawInternalPortMarker` — a ring with a ground symbol; the mark channel now
  carries the port KIND rather than only an anchor).
- Tests: `tests/Engine.Tests/Mom/InternalPortTests.cs`, `tests/Ui.Tests/Em/InternalPortUiTests.cs`.
- Docs: `docs/design/mom-engine.md` §10.6, the user chapters `mom-engine.md` / `em-setup.md`, and a
  new one — `docs/user/src/reference/stackup.md`, which is where "what is my port's negative
  terminal" is now answered, with a cross-section figure generated from the shipped technology.

## The internal delta-gap port (2026-08-24)

The second of §10.6's two v1 port types, listed as "later" in the design note from the first draft
and as deliberately out of scope in this directory's own `CLAUDE.md` §7 until now. Both statements
were removed rather than softened; the design note's own §10.6 carries the full write-up.

**It cost almost nothing in the fill or the excitation, and that is the point.** D1 already defines a
port as a delta gap across the shared edge of two adjacent cells driving the rooftop row that spans
it — nothing in that says the cells have to be the two outermost ones. `PlanarPortKind` picks WHICH
shared edge: `Edge` marches in from the named side as before, `InternalDeltaGap` scans the run's
interior gridlines and takes the one nearest the placed point that has metal (and a rooftop) on both
sides. Everything downstream — `PlanarExcitation`, `Y = BᵀZ⁻¹B`, the s-matrix — is untouched.

**Three findings worth keeping.**

**1. A delta gap is a SERIES source, and the first gate asserted the wrong identity.** A gap at the
exact centre of a uniform line is mirror-symmetric about its own cut, so the obvious oracle is
"it couples equally to both ends", S₁₃ = S₂₃. The solve returned S₁₃ = **−**S₂₃, equal and opposite
**to sixteen digits**. That is correct and the oracle was wrong: a series gap pushes current one way
along the conductor, into the line on one side of the cut and out of it on the other, so the two
halves are driven in ANTIPHASE. A SHUNT port — current injected against the ground plane — would be
symmetric. The difference is a hard π, invisible in a magnitude plot, and the test now asserts the
antisymmetry at 1e-6 (it holds far tighter than that) precisely because a later change to the
incidence sign would otherwise pass. `CLAUDE.md`'s low-frequency-floor note already said the port is
"necessarily a **series** delta-gap"; this is the same fact arriving from the other direction.

**2. `IndexOf` CLAMPS, so "is the port on the metal?" needs the grid extent as well.** The transverse
index lookup returns the nearest cell for an out-of-range coordinate rather than a miss — right for an
edge port, whose label may legitimately sit just off the end face it names and whose longitudinal
coordinate is not read at all. For an internal port it is wrong in the worst way: a point metres from
the artwork clamps onto the outermost row, finds metal there, and cuts a gap the user never asked
for — a complete, plausible s-matrix for a structure nobody drew. The internal branch now tests both
axes against the grid's own extent before asking about metal. *(The pre-existing "outside the meshed
region entirely" refusal on the edge path is, for the same reason, effectively unreachable. Left
alone: the Ui-side extractor refuses an off-metal label before the engine sees it, and widening the
edge path's behaviour is a change to shipped port resolution that wants its own measurement.)*

**3. De-embedding stays ONE code path, via an identity error box.** An internal port takes
`PlanarSolve.IdentityBox` (a₁₁ = 0, a₂₂ = 0, a₂₁ = 1) and `Z_c = Z₀`, which makes
`PlanarDeembed.Apply` and `Renormalise` the identity on its row and column. This is not "de-embedding
an internal port": it is arithmetic that provably changes nothing, and it was chosen over partitioning
the matrix because a unit a₂₁ also leaves a de-embedded NEIGHBOUR's mixed terms untouched — the
partitioned alternative would have needed its own proof of exactly that. Gated by solving the same
problem with `Deembed` on and off and requiring < 1e-12; tolerant rather than an equality only because
the ON path still passes through an LU of the identity matrix, so asserting bit-identity would be
asserting a property of NumFlat's LU.

## `brief-em-deembed-ceiling-closeout.md` — the de-embedded path's OWN dense ceiling (2026-08-14)

Closes `brief-em-aim-ceiling.md`'s own §13 closing subsection ("The limitation this surfaced"): the
accelerated ceiling (`SurfaceMesher.AcceleratedUnknownCeiling = 12,000`) moved for the DUT's own
N×N basis system, but `PlanarDeembed.StaticCapacitance` — the m×m CELL system D7's reference
impedance needs, computed once per calibration standard — is a separate, always-dense computation
that was never wired to refuse honestly. On the owner's own reported taper (`brief-em-aim-ceiling.md`
§0; Z1 = 6.92 Ω, Z2 = 100 Ω, L = 28.575 mm), turning Accelerated solve on as the panel's own refusal
instructed produced a mesh that passed (N = 6,581) and a DUT Z-matrix that actually solved, then a
throw twenty REAL MINUTES later out of `PlanarDeembed.CapacitancePerMetre`, once the calibration
standard's own static-capacitance solve (N = 6,466 — 98% of the DUT's own N, because D4 makes a
standard reproduce the DUT's transverse gridlines verbatim) finally ran into
`PlanarFill.BuildCores`'s dense guard. R17's own contract has never been "there is a ceiling" — it
is "surface the predicted N before solving, and refuse politely above it", and this run predicted,
passed, and failed late — the one thing that contract exists to prevent.

**No physics changed.** D6/D7's algebra, `SpectralGreens.cs`, `Dcim.cs`, `SingularExtraction.cs` and
`PlanarAim.cs` are all untouched. This is a refusal-timing fix, a message-accuracy fix, and one
measurement that came back negative.

### C1 — refuse at setup, not twenty minutes in

`PlanarSolve.cs`'s `if (st.Deembed)` block already assembled every calibration standard's own
`Mesh.Bases.Count` into a `sizes` list and reported the ratio against the DUT's own N — the
prediction R17 wants was present, correct, and simply unenforced. A loop added right after that
list is built (before any calibrator's raw solve, before any dense fill) now checks each standard's
own N against `SurfaceMesher.UnknownCeiling` and throws `InvalidOperationException` naming:

- **why the accelerator does not help** — D7's static ω → 0 capacitance solve is a structurally
  different m×m system over cells, not the N×N frequency-domain basis system
  `PlanarFillSettings.Aim` covers, so turning it on (which the panel just told this same user to do)
  does not move THIS ceiling;
- **the actionable remedy, with what it costs** — turn de-embedding off and read the raw solve; those
  s-parameters include the port discontinuity rather than being the structure's own response
  (`docs/design/layout-view.md` §10.6: "A raw port excitation includes the port discontinuity;
  reporting those s-parameters as the structure's response is simply wrong"), so this is named as a
  diagnostic-only fallback, not a second success case;
- **both N's** — the DUT's own mesh count and every calibration standard's own count, not just one.

No mesh remedies (`Lower Cells per wavelength`, `turn the edge mesh off`, …) are offered — the
parent brief's own §0 already measured them inert on this class of geometry (width-ratio-driven N
growth), and naming an inert remedy is the exact defect `brief-em-aim-ceiling.md` closed once
already for the dense mesh refusal.

**Gate 1**, `EmCeilingRefusalTests.Gate1_TheOwnersReportedTaper_DeembedOn_AcceleratedOn_RefusesAtSetup_NotAfterADenseFill`:
the owner's exact reported taper, de-embedding ON, accelerated ON, through the real entry point
(`PlanarSolve.Run`) — now refuses in **88 ms** (measured), naming the standard's N, the reason the
accelerator does not help, and the de-embed-off remedy with what it costs. Before this brief the
identical call sequence appeared to succeed and only failed twenty real minutes in.

### C2 — the guard at `StaticCapacitance`'s own call site was measuring the wrong allocation

`PlanarFill.BuildCores`'s shared `GuardCeiling(n)` quotes an **n×n dense complex matrix** because
that is what its OTHER callers (`PlanarFill.Fill`, `PlanarSystem.Build`) go on to allocate.
`PlanarDeembed.StaticCapacitance` never builds one: `PlanarFill.ScalarPotentialMatrix` returns an
**m×m** `Mat<Complex>` over CELLS (D4's potential-coefficient matrix P), and `StaticCapacitance`
then builds a *second* m×m `Mat<Complex>` from it and factors that — so the megabytes the shared
guard's own message would quote are not the cost of what is actually about to be allocated at this
call site. This is §7's own "quotes 381 MB against a real run cost of ~607 MB" defect in a second
location.

Measured on a real calibration standard (`EmDeembedCeilingTests.C2_MVsN_OnARealCalibrationStandard_IsMeasuredNotAssumed`,
a 6 mm FR-4 line's own port, coarse mesh): **m = 32 cells, n = 52 bases, m/n = 0.615** — a standard
is long and thin (few cells across the port width, many along its length), which is why the ratio
sits above the square-grid asymptote of 0.5 rather than at it; either way m is always strictly under
n. On a synthetic mesh sized to straddle the dense ceiling the OLD way but not the new one
(`EmDeembedCeilingTests.C2_TheGuardAtThisCallSite_QuotesCellsAndTheRealMegabytes`, m = 4,500 cells,
n = 8,855 bases): the shared guard's own n×n formula would quote **1,196.5 MB**; what this call site
actually holds (two m×m complex matrices) is **618.0 MB** — corroborated by a real
`GC.GetAllocatedBytesForCurrentThread()` measurement on an actual solve
(`C2_TheFormula_IsCorroboratedByAMeasuredAllocation`), not left as arithmetic alone.

**Fix**: `PlanarDeembed.GuardCapacitanceCeiling(mesh)`, called at the top of `StaticCapacitance`
before `PlanarFill.BuildCores` runs. **The threshold is UNCHANGED** — it still asks
`n > SurfaceMesher.UnknownCeiling`, exactly what `BuildCores`'s own shared guard asks, per the
brief's own instruction not to tighten or loosen the shared question for every caller. Only the
MESSAGE differs: it names the cell count, the two-m×m-matrix accounting, and says explicitly that
this solve never builds the n×n matrix the shared guard's own wording describes.

### C3 — is the ω → 0 static system real? Measured, and the answer is no

The premise this milestone existed to test: if `PlanarKernelTerms.StaticScalar`'s output is
genuinely real (as its own doc comment's "there is no logarithm and no linear term" language might
suggest), `StaticCapacitance` could move from `Mat<Complex>` + a general LU to `Mat<double>` + a
symmetric factorisation — roughly 4× less memory, i.e. 2× the reachable linear dimension, which
could be the difference between refusing the owner's own 6,466-cell standard and running it
comfortably.

**Measured, not assumed, and it fails.** `StaticScalar`'s image ratio is
`k = (1 − εᵣ*)/(1 + εᵣ*)`, and `εᵣ* = εᵣ(1 − j·tanδ)` is complex for any lossy substrate — which is
every starter this repository ships (`Fr4Starter`: εᵣ = 4.4, tanδ = 0.02; `GaAsStarter`: εᵣ = 12.9,
tanδ = 0.002). `EmDeembedCeilingTests.C3_TheStaticScalarKernel_HasAMateriallyNonzeroImaginaryPart_OnALossySlab`:

| slab | tanδ | `Inverse.Imaginary / Inverse.Real` |
|---|---|---|
| FR-4 | 0.02 | **1.63e-2** |
| GaAs | 0.002 | **1.86e-3** |

Neither is floating-point noise (machine epsilon would read ~1e-16) — both scale with the
substrate's own tanδ, because they come directly from it. Carried through an actual solve on a real
calibration standard (`C3_TheDiscardedImaginaryPart_IsMaterial_OnARealCalibrationStandard`): the
total `StaticCapacitance` sums before its own `.Real` truncation has `|Im/Re| = 1.76e-2` — consistent
with the term-level ratio, confirming the discarded part is not merely an input-level artifact that
cancels during the solve.

**Why this closes the door on the representation win, not just narrows it**: the per-cell-pair
remainder term (`PlanarKernelTerms.Remainder`, the convergent image series) mixes powers of the
SAME complex `k` with REAL, ρ-dependent weights that differ per cell pair — so the resulting matrix
is not of the form `A_real·(1 + jα)` for one global loss factor α (which is the one shape where
`Re(A⁻¹b) = Re(A)⁻¹b` would hold). The matrix's complex structure is genuinely entry-dependent.
Converting to `Mat<double>` from `Re(A)` would therefore change the published C_pul, not merely
its representation — failing R-dcl-5's bit-comparison requirement outright, not by a measurable-but-
small margin. **No representation change was made.** `_cPerMetre`'s existing once-per-calibrator
cache (`PlanarPortCalibrator`'s own `if (double.IsNaN(_cPerMetre))`) already holds this cost to one
solve per standard per sweep and needed no change.

### C4 — the decision

**The de-embedded path's effective ceiling is `SurfaceMesher.UnknownCeiling` (5,000), asked of every
calibration standard's own N, exactly as it is asked of the DUT's dense path** — the accelerated
ceiling never applies to this step and, per C3, cannot be widened by a cheaper representation
either. For the class of geometry that motivated the accelerator (width-ratio-driven N growth), a
standard reproduces the DUT's own transverse gridlines verbatim (D4), so **a wide-port DUT's
calibration standard is typically comparable in size to the DUT itself** — on the owner's own file,
98%. This is not a separate, narrower "wide port" concept: it is the same N the DUT's own mesh
report already predicts, applied a second time to the standard.

**What this brief delivers**: the run now tells the user this honestly, in seconds, with a remedy —
turning the accelerated solve on genuinely helps the DUT's own Z-matrix and is offered as it should
be; de-embedding a wide-port part past the dense ceiling is not currently possible, and the run says
so instead of appearing to succeed and failing twenty minutes later.

**If `PlanarDeembed.StaticCapacitance` is ever accelerated, it is its own brief, not built here.**
It would have to compress an **m×m system over mesh CELLS**, under a **static (ω → 0), genuinely
complex kernel** (C3: not reducible to a real one for any lossy substrate) — not the **N×N system
over rooftop BASES** under a **frequency-dependent DCIM-fitted kernel** that
`PlanarFillSettings.Aim` already accelerates. AIM's own near/far split and auxiliary-grid
interpolation are built around the basis geometry and the DCIM fit's height dependence; neither
transfers directly to a cell-indexed static Green's function whose singular structure is the
`(1 + k)/(4πρ)` term plus a convergent image series, not DCIM's exponential fit. Whether a
hierarchical/low-rank compression that stays genuinely complex is worth its own engineering cost
is undecided and unstarted.

### What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarSolve.cs` — a setup-time refusal loop in the `if (st.Deembed)` block, right
  after the existing `sizes`/`totalN` computation.
- `src/Engine/Mom/PlanarDeembed.cs` — `StaticCapacitance` calls a new private
  `GuardCapacitanceCeiling(mesh)` before `PlanarFill.BuildCores`; same threshold, corrected message.
- `tests/Ui.Tests/Em/EmCeilingRefusalTests.cs` — new gate 1 test; the existing
  `…ACTUALLYSOLVES…` benchmark test's doc comment now states explicitly (§10.6) why the DUT's raw
  solve it gates is not this user's published success case.
- `tests/Engine.Tests/Mom/EmDeembedCeilingTests.cs` — new: the C2 m-vs-n measurement and guard-message
  gates, and the C3 negative-result measurements.
- Nothing in `PlanarDeembed.cs`'s D6/D7 algebra, `SpectralGreens.cs`, `Dcim.cs`,
  `SingularExtraction.cs` or `PlanarAim.cs` changed.


---

# P1 — honest memory accounting for the planar solver (2026-08-29)

`docs/sonnet-briefs/brief-em-p1-honest-memory-accounting.md`. **This brief measured and re-worded.
It changed no arithmetic** — no fill, no factorisation, no Green's function, no s-parameter moved by
a bit, and no ceiling constant moved at all.

## What was wrong

`SurfaceMesher`, `PlanarSystem` and `PlanarFill` each carried their own copy of R17's refusal and
all three quoted `16·N²` — "381 MB at the ceiling". That is exactly the dense matrix and it is
silent about everything live beside it. §7 of `CLAUDE.md` already carried the open item "381 MB
quoted against ~607 MB real — owner's call". **607 was itself an underestimate.**

The same class of defect sat in `PlanarAimReport.ApproximateBytes`, which counted the accelerator's
own arrays and stopped before the sparse LU it builds from them.

## What was measured

Every number below is in `HISTORY.md` §P1 with its table.

- **One dense frequency point holds 3.52× the 16·N² that was quoted**, and the ratio is FLAT across
  N = 552 / 1,980 / 4,836 because every term is O(N²) with a fixed coefficient. The split is one
  matrix, **two** further full matrices for the LU factors, and the cached cores at just over half a
  matrix. At the ceiling that is **1,338 MB, not 381**.
- **`NumFlat.LuDecompositionComplex` is not a packed in-place LU.** It holds `L` and `U` as two
  separate full `Mat<Complex>` of stride n — confirmed by reflection over its fields AND by
  measurement — beside the matrix `PlanarSystem` keeps for the life of the point. That is the single
  largest term in the accounting and it is the one nobody had counted.
- **The transient m×m `P` is real and is not the peak.** The fill builds it, uses it and drops it
  before anything is factored; at m ≈ N/1.95 it is a quarter of a matrix against the factorisation's
  two.
- **The accelerated point's missing term was the sparse LU**, not the near field: at the accelerated
  ceiling its factors are comparable to everything else the operator holds put together.

## Three findings the brief did not anticipate

1. **`PreconditionerNonZeros` was never the factor's fill-in.** The brief describes it as the
   fill-in "reported but never added". It is `csc.NonZerosCount` — the near MATRIX's own non-zero
   count, which (every near pair being stored exactly once) is the near ENTRY count a second time,
   under a name that reads like the factor's. There was no fill-in number to add; `FactorNonZeros`
   (`SparseLU.NonZerosCount`, L and U together) is new.

2. **`CLAUDE.md` §8's "the accelerator's own working set stays under 200 MB even at that ceiling"
   was FALSE as shipped, and releasing `_nearExact` is what makes it true.** Counted honestly with
   the exact near entries still held — which is what shipped until this brief — the operator at
   N = 11,959 holds ~269 MB. Freeing them brings it to 196.2 MB. The sentence in the refusal and in
   §8 survives, but it survives *because of* this brief rather than in spite of it, and it is now
   tight rather than comfortable.

3. **The brief's own gate could not live in the routine tier as written.** "Within 20% of the
   measured resident delta" needs `GC.GetTotalMemory`, which is PROCESS-wide, and xUnit runs test
   classes concurrently in one process — another class's collection lands inside the measurement
   window. The first version of that test read **0.925 alone and −0.245 in a full-suite run**. It is
   not a flake to be re-run; it is the wrong instrument for a parallel suite. So the resident
   comparison is asserted in the `Category=Benchmark` measurement (which carries a "measure this one
   alone" note, following the precedent `HISTORY.md` already sets for the L8d cost table), and the
   ROUTINE gate counts the same quantity by walking the operator's own object graph and adding up
   every array it holds — load-immune, and independent of the report's arithmetic in the way that
   matters, since it counts the arrays that exist rather than re-deriving them from the report's own
   fields. **It agrees to 0.3%** (reported 9.7 MB against 9.7 MB walked at N = 552).

4. **Reading the exact entries back out of CSparse's CSC — the brief's own suggestion for keeping
   `NearExactAt` alive — is strictly worse than keeping the array.** The CSC stores a 4-byte row
   index per entry that the CSR's shared column index already provides, so holding the CSC to serve
   a diagnostic costs 4 B/entry MORE for the same numbers. `PlanarAimSettings.KeepNearExact`
   (default false) keeps the array instead, and `NearExactAt` throws by name when it was not kept —
   a silent zero there is indistinguishable from "this pair is not near", which is the question the
   caller is asking.

## A measurement trap worth the paragraph, because it produced a wrong table first

**The large object heap does not compact by default**, so a released n×n matrix leaves committed
space the next allocation lands in. A per-phase `GC.GetTotalMemory(true)` DELTA therefore reads the
matrix LOW (it lands in the transient `P`'s grave) and the factorisation HIGH, and a ladder that
measures several N in one process reads the second rung's cores as **negative** — which is what the
first version of this table printed. Cumulative live from ONE baseline, paired with
`GC.GetTotalAllocatedBytes`, are both exact and are what the tables use; the same effect made the
AIM ladder's second rung read 40% low until every operator was kept reachable for the whole test.

The brief's own opening scratch measurement is the same artifact: `.Lu()` "added 530 MB" to a 137 MB
matrix (3.87×) is the LIVE figure with the factorisation's released scratch still committed. What is
RETAINED is 2× the matrix; the extra ~0.6× is real, transient, and belongs in a note rather than in
a refusal.

`Process.PeakWorkingSet64` **reads 0 on macOS** — the platform does not track it — so the tables
report `WorkingSet64` and say so.

## What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarSystem.cs` — new `CoreBytes(n, cellCount)`, `FactorBytes(n)`,
  `ResidentBytes(n, cellCount)` and `ResidentPhrase(n, cellCount)`. `GuardCeiling` gained an optional
  cell count. `PlanarSweepResult.ResidentBytes` added beside `MatrixBytes`.
- `src/Engine/Mom/PlanarFill.cs`, `src/Engine/Mom/SurfaceMesher.cs`, `src/Engine/Mom/PlanarSolve.cs`
  — the three refusals and the WARN note quote `ResidentPhrase`; the cell count is threaded to each
  from the mesh the caller already has.
- `src/Engine/Mom/PlanarAim.cs` — `ApproximateBytes` → `ResidentBytes` (renamed because it no longer
  approximates anything), plus `PeakBuildBytes`; `FactorNonZeros` and `NearExactRetained` added to
  the report; `PlanarAimSettings.KeepNearExact` added; `_nearExact` released after `FactorNear`.
- `tests/Engine.Tests/Mom/PlanarMemoryAccountingTests.cs` — new. Two `Category=Benchmark`
  measurements (the two tables, and the brief's own resident-delta assertion) and four routine
  COUNTER gates: the AIM report against every array the operator actually holds (and not vacuously
  so — without the sparse LU the same comparison misses by more than the band allows), the exact
  near entries released by default with the answer bit-identical either way, `CoreBytes` reproducing
  `PlanarFillCores.CoreBytes`, and all three refusals quoting the same sentence.
- `tests/Ui.Tests/Em/EmCeilingRefusalTests.cs` — the expected sentence updated, still asserted, and
  now also pinned against `PlanarSystem.ResidentPhrase` itself so the gate cannot drift from the
  code.
- `docs/design/mom-engine.md` §10.7 and `src/Engine/Mom/CLAUDE.md` §7/§8 — corrected in place.

## Out of scope by instruction, and left alone

`UnknownCeiling` and `AcceleratedUnknownCeiling` are untouched (P7 and P8 own those, once the memory
is what it will be). `PlanarSystem`'s factorisation is untouched (P7). **The 3.52× is a fact about
the code as it stands, and P2 and P7 are the briefs that move it.**


---

# P2 — four mechanical memory wins on the dense path (2026-08-29)

`docs/sonnet-briefs/brief-em-p2-cheap-memory-wins.md`. Four allocations removed. Three are
**bit-identical**, measured against a worktree of the pre-P2 tree rather than argued; the fourth
(M2) is a different rounding of the same system and moves a published s-parameter by ≤ 1 ulp.

## How the bit-identity claims were actually made, since the method matters

"Bit-identical" compares this build to the build before the change, and no single tree holds both.
So: `git worktree add … HEAD --detach`, drop the same fixture-driven digest test into both trees,
run, compare. The digests are then LITERALS in `PlanarP2MemoryWinsTests` — a test that recomputed
its own expected value from the code under test asserts nothing.

Running the pre-P2 worktree TWICE — once untouched, once with M2's six lines applied and nothing
else — separated the one milestone that moves arithmetic from the three that do not. That is what
lets `P2_6` assert "M1, M3 and M4 together moved not one bit of a published s-parameter" as a
measurement instead of a hope.

## Findings

**1. An O(N²) array held nothing but the outer product of an O(N) vector.** `BuildDirectionCores`
cached `mMom * nMom` per same-direction basis pair — the extracted constant term's vector core,
which factors. Its two neighbours in the same family (`∫∫ w w /R`, `∫∫ w w ln r`) genuinely do not
factor, which is why they are cached and this one need not have been. Removing it is **24% off the
cached cores at every N** and 3.5% off a whole dense frequency point, and it is exactly bit-identical
because the product formed at the point of use has the same two operands.

**2. `PlanarSolveResult.CoreFillCount` could not have caught the duplicate core build, because it
never counted builds.** It is `1 + standards` — derived from the number of standard MESHES. Over the
sweep it read 5 while the code was calling `BuildCores` 7 times, the extra two being
`StaticCapacitance` re-coring meshes whose contexts already held identical cores. A counter derived
from what SHOULD happen cannot notice what does. `PlanarCoreBuildCounter` counts the builds; the old
counter is unchanged, which is what the brief required.

**3. M4's saving is real but much narrower than the brief assumed, and the reason is structural.**
Through `PlanarSolve.Run` the calibration band IS the sweep's own frequency range, so over a full
sweep every separation is selected at some frequency and lazy coring saves nothing: 4 of 4 standards
cored on a 1–20 GHz sweep whether it has 5 points or 21. The saving appears only when the band is
wider than the frequencies actually stepped — a single-frequency check, an interrupted run, or a
direct `PlanarPortCalibrator` caller — where it is large: 2 of 4 standards cored, 50.5 kB of
240.8 kB, i.e. **79% of the standards' cores never built**. The unconditional part is that the
CONSTRUCTOR now cores 0 of 4 rather than 4 of 4.

**4. M3 and M4 pull against each other, by design, and the longest standard is the price.** D7's
`CapacitancePerMetre` differences `Standards[0]` and `Standards[^1]` — the two EXTREME lengths, not
the one this frequency solved. Handing it the contexts' cores (M3) therefore BUILDS the longest
standard's cores on every de-embedded run even when no frequency selected it. That is correct for
the static differencing and it is why the measurement above reads 2 cored rather than 1. P11 changes
that solve.

**5. The de-embedding ceiling's own refusal was undercounting, and M2 made the sentence false as
well.** It said "the two m×m complex matrices this solve holds at once (the potential-coefficient
matrix and its own copy)". M2 removed the copy; and P1 had already established that NumFlat's LU
holds `L` and `U` as two further full matrices, so "two" was low before M2 and wrong after it. It now
says three (P, L, U) and quotes 3·16·m².

**6. A geometry-only core is no longer empty, and the gate had to say what it actually meant.**
`AimAcceleratorTests.T1b` asserted `geom.CoreBytes == 0`. The accelerator's cores now carry the same
O(N) `VMoment` vector (8·N bytes, and `PlanarEntryFill` reads it instead of deriving a second copy of
the identical array). The assertion became `== 8·N`, which states the real claim — nothing QUADRATIC
— exactly rather than by proxy.

## Numbers worth keeping

| N | cells | cores before | cores after | resident before | resident after |
|---|---|---|---|---|---|
| 552 | 297 | 2.43 MB | 1.85 MB | 16.38 MB | 15.80 MB |
| 1,980 | 1,053 | 30.92 MB | 23.45 MB | 210.39 MB | 202.92 MB |
| 4,836 | 2,565 | 184.09 MB | 139.50 MB | 1,254.68 MB | 1,210.09 MB |

P1's flat resident-to-matrix ratio **3.52× → 3.39×**; the ceiling refusals quote **1,290 MB** at
N = 5,000 rather than 1,338. The ceiling CONSTANT is untouched — that is still P7's decision.

## What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarFill.cs` — `VXArea`/`VYArea` removed; `PlanarFillCores.VMoment` (one
  `double[N]`) added, built by a shared `BasisMoments` that both core builders call.
  `AddDirectionBlock`, `HorizontalVectorEntry` and `PlanarEntryFill` form the product at use.
  `PlanarCoreBuildCounter` added; `PlanarFillSettings.CoreBuilds` carries one.
- `src/Engine/Mom/PlanarSystem.cs` — `CoreBytes` is `8·(2·sp + 2·vp + n)`.
- `src/Engine/Mom/PlanarDeembed.cs` — `StaticCapacitance` solves `P q = ε₀·1` and takes optional
  cores; `CapacitancePerMetre` takes two; the ceiling refusal names three m×m.
- `src/Engine/Mom/PlanarSolve.cs` — `PlanarSolveContext.Cores` is a thread-safe `Lazy` (the R17
  guard stays eager), plus `CoresBuilt`/`CoreBuildMs`; `PlanarPortCalibrator` gained
  `CoredMeshCount`/`CoreBuildMs` and hands its contexts' cores to `CapacitancePerMetre`; the run's
  reported `CoreBuildMs` is summed from the contexts rather than measured around a constructor.
- `tests/Engine.Tests/Mom/PlanarP2MemoryWinsTests.cs` — new, 7 routine tests.
- `tests/Engine.Tests/Mom/AimAcceleratorTests.cs` (T1b) and `EmDeembedCeilingTests.cs` (C2) — the two
  assertions whose subject genuinely changed.
- `docs/design/mom-engine.md` §10.7 and `src/Engine/Mom/CLAUDE.md` §7 — the 3.39× / 1,290 MB in place.

## Not done, on purpose

Storing `P` packed — the brief's own fifth candidate. It halves a transient 4N², and it touches every
`p[a, b]` reader in three fills, so it is a milestone with its own bit-identity gate rather than a
few lines. `PlanarAim.cs`, the LU and every quadrature untouched, as instructed.

---

# P3 — the multi-level fill scales like the single-level one (2026-08-29)

`docs/sonnet-briefs/brief-em-p3-multilevel-fill-scalability.md`. **Every number is in
`HISTORY.md` §P3; this is the narrative.**

## What was asked, and what turned out to be true

The brief read `FillMultiLevel` and saw per-entry work the single-level fill hoists — a locked
kernel-set lookup and a fresh `PlanarKernelTerms` per cell pair and per basis pair, three more locked
caches per entry, an array per horizontal entry — and predicted its parallel scaling "will be far
worse". Milestone 1 measured before anything was touched: **the multi-level fill already scaled
5.5–5.9× on ten cores, the same as the single-level fill's 5.4–5.9×.** The locks were uncontended
hits on small dictionaries, ~100 ns between ~200 µs of quadrature; the allocations were ~150 bytes a
pair against the same 200 µs. The hoist was still the right thing to do — it is bit-identical, it
takes ~8× the allocation out of every fill, and it is the shape the single-level fill already has —
but the speed it bought came from somewhere the brief did not look.

## Findings

**1. The via-bearing fill's cost was in an arm that is not O(N²).** `MixedEntry` — one entry per
via basis × horizontal basis — enumerated the horizontal cell's quadrature nodes through an
iterator, once per VIA node. On a near or intermediate pair that is ~1,500 enumerators per entry;
236 MB of them per fill on the N = 514 fixture, and ~30 s of its 58 s vector phase. Inlining the
enumeration (same nodes, weights and nesting order — bit-identical) is **19% off the serial fill
and 16% at cap 10** on that fixture, 16% / 22% on the FR-4 hero with three vias. The block is
O(N·N_z), but a via cell is large, so most horizontal cells count as near or intermediate to it and
take the full graded rule. Its remaining cost is a quadrature-order question and out of scope.

**2. The strided writes were not the fall-off, and the fall-off is the box.** Milestone 4 moved
every fill to the contiguous (lower) triangle with a column-wise mirror. The serial time on the
256 mm line is unchanged to 0.6% (70.9 → 71.3 s); 25 M cache-missing writes at 100 ns is 2.5 s of a
71 s fill, and could never have produced a 40% loss at ten cores. What does: the machine has
**4 performance + 6 efficiency cores**. Efficiency is 89–95% at cap 4 on every fixture, before and
after; the loss appears exactly when the cap admits efficiency cores, and every cap-10 speedup
measured (5.2–6.4×) solves `4 + 6f` to f ≈ 0.2–0.4 — an efficiency core's share of a performance
core. That is why `CLAUDE.md` §6's Amdahl fit gave three different serial fractions: there is no
serial fraction. "Hardware, not scheduling" stands; "memory bandwidth" is struck from it.

**3. Exactly the pairings the loops visit must be resolved, and no others.** `PlanarKernelSet.FitCount`
is asserted by three tests, and a hoist that resolved every (layer, layer) for every kernel would fit
pairings the loops never asked for — a layer carrying only x̂ rooftops and one carrying only ŷ never
pair in the vector block. The horizontal table is therefore built per direction from the layers
that carry it; the ẑẑ table is keyed on the ORDERED span pair the `i ≤ j` loop produces, because
`AveragedTerms(si, sj)` is not asked in canonical order and the digests were taken on what it was
asked; and the mixed table covers every (span, horizontal layer). FitCount is unchanged on every
fixture.

**4. The allocation that remains is the via z-integral's, not the fill's.** With a per-arm counter,
the horizontal and mixed arms now allocate 0 bytes across a whole serial fill. The 27 MB left beyond
the two matrices on the N = 514 fixture is 12.6 MB of per-pairing radial tables (which `Fill` also
builds per call) and 14.4 MB from three ẑẑ entries — `ViaZIntegral.PrismCore` allocates its node
arrays per entry, ~4.8 MB each. O(N_z²), the via rule's own, left alone.

## How the gates were made

The pre-P3 build lives in a git worktree at `HEAD` with P1 and P2's uncommitted diff applied and
`PlanarFill.cs` replaced by its pre-P3 copy; the same test file runs in both trees. Digests are
literals (seven of them — P2's own hero digest is re-asserted, since milestone 4 touches `Fill`).
The routine allocation gate computes its allowance from the settings — one `MaxTableSamples`-capped
table per (kernel, level, level), plus O(N) — and **the pre-P3 worktree fails it by 2.7×**, which is
what makes it a gate rather than a description. A first draft asserted a growth LAW (double the
line, ~4× the pairs, the extra allocation must not follow) and could not discriminate: on the
fixtures a routine test can afford, the pairs grow only 1.8× and the pre-P3 extra was dominated by
the O(N·N_z) mixed-arm iterators, so both trees passed. The allowance form replaced it.

Timing tests are `Category=Benchmark`, and were run ALONE and detached — a first attempt inside the
shell's ten-minute limit was killed with no output. The three multi-level fixtures at four caps plus
warm-ups are ~12 minutes; the 256 mm line ~5.

## What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarFill.cs` — `MultiLevelPairings` resolves everything per pairing before
  `ForRows`; `FillMultiLevel`'s loops read it. `HorizontalVectorEntry` (cached halves, no array),
  `MixedEntry`/`MixedHalf` (inline node enumeration for a whole-rectangle half),
  `CellPairPotential` (cached pulses), `SingularPrismPart` (asymptote passed in).
  `MirrorLowerToUpper` is the one mirror; every fill writes the lower triangle.
- `tests/Engine.Tests/Mom/PlanarP3MultiLevelFillTests.cs` — new: four routine gates, three
  Benchmark measurements.
- `src/Engine/Mom/CLAUDE.md` §6 — the "core heterogeneity or memory bandwidth" clause narrowed in
  place, dated. `docs/design/mom-engine.md` §10.7 — a `P3 is done` note.

## Not done, on purpose

No quadrature rule, table spacing or kernel evaluation was changed; `ForRows` is still the only
parallel loop. The mixed block's quadrature ORDER (the actual cost of a via-bearing fill at small N)
and `PrismCore`'s per-entry arrays are recorded above and left for a brief of their own.

# P4 — the four ramp combinations of a cell pair are one pass (2026-08-29)

`docs/sonnet-briefs/brief-em-p4-vector-block-moment-cache.md`. **Every number is in `HISTORY.md`
§P4; this is the narrative.**

## What was asked, and what turned out to be true

The brief's premise is right and is now built: a cell's two ramps are `w_A = (u − u₀)/A` and
`w_B = (u₁ − u)/A = Δ·p − w_A`, so every (half, half) integral over a cell pair is a linear map of
four primitives per kernel and per flow direction — seven per pair in all, one outer pass. The
core build's quadrature count fell 6.6–7.3× and the per-frequency vector remainder's 5.8–6.4×;
wall clock followed at 3.6–4.0× and 2.7–3.0× on the three series fixtures. Three things in the
brief did not survive contact with the code, and each changed the design.

## Findings

**1. The ŷ block integrates the same cell pair in both orientations, so the primitives are per
ORDERED pair over a band, not per unordered pair "with the same packed index as S0".** The outer
Gauss rule and the inner closed form are not interchangeable to 1e-12 — swapping which cell is
integrated numerically moves a touching pair by its own quadrature error, ~1e-6 — and the row
loops integrate the lower-indexed BASIS's cells as the outer domain. In the x̂ block that always
puts the lower-indexed cell outside. In the ŷ block a rooftop one row down and one column to the
LEFT has the lower basis index, so cell pair (c, c′) is integrated with c outside by one basis pair
and with c′ outside by another. An unordered cache, as the brief specified, would therefore have
changed the orientation of a fraction of the ŷ entries and failed the brief's own 1e-12 gate on
them — not a sign error, a quadrature-tolerance one, and exactly the kind of "smooth, plausible,
wrong" the brief warned about. The fix is structural: `RampTopology.MinInner[a]` is the smallest
inner cell any pair with `a` as outer can ask for (a suffix minimum over basis positions), the
pass runs `c ≥ MinInner[a]` — a band `n_x` wide below the diagonal — and orientation is preserved
exactly. The band costs ≈ 10–20% over the triangle on the series fixtures (0.56–0.60 m² against
0.5), and the 1e-12 gate holds at 8e-13.

**2. The brief's storage arithmetic was off by the basis-pair-to-cell-pair ratio, so the
primitives are transient rather than stored.** "7 × 2 doubles per cell pair against today's
2 + 3 + 3" compares a per-cell-pair count with per-BASIS-pair triangles, and there are ≈ 2
same-direction basis pairs per unordered cell pair. Holding the primitives would be 14 doubles per
cell pair against the ≈ 6 P2's layout holds — 2.3× the cached cores, ≈ +250 MB at the ceiling —
in a series whose first two briefs exist to take memory off. So the primitives are never
persisted: each outer cell's pass assembles them straight into the existing per-basis-pair
triangles. That needed one more idea, because a basis pair draws on two outer cells (its A cell
and its B cell) and a cell-parallel pass would have two threads adding into one entry. The A
cell's contributions go to the entry itself and the B cell's to a transient second triangle of
the same size, and a row pass adds them; every slot has exactly one writer, in a fixed order over
inner cells, so R-fil-11 holds and the build is deterministic. The resident cores are the P2
layout to the byte (`P1_5`, `P2_2` pass unchanged). The fill does the same with four transient
`Complex` triangles for the remainder sums, ≈ 195 MB at N = 4,933 — inside the fill phase, whose
high-water mark P1 showed is below the factorisation's.

**3. The scalar block came along for free, bit-identically.** The pulse×pulse primitive `Q00` is
the scalar block's own `S0`, so the one cell pass serves D4's triangle as well as both directions
of D5 — the brief's "≈ 0.5 m²" was the vector count alone and the total is now ≈ 0.6 m² for
everything. Accumulating `Q00` with the pulse path's own expressions in the pulse path's own
order makes `S0`/`SLog` bit-identical, which is asserted exactly and is what keeps every
capacitance and static-limit gate, and the ẑẑ block that shares `S0`, untouched.

**4. What "bit-identical" could and could not mean here, and what was re-pinned.** Four
combinations of seven primitives are different floating-point operations from four quadratures
summed, so the vector block's last bits move — the brief anticipated this and the gate is 1e-12 on
the assembled matrix, held against the pre-P4 arithmetic kept in the tree as
`BuildCoresByHalves`/`FillByHalves` (a runnable reference rather than a digest printed once
against a tree that no longer exists; the internals are not visible to the test project, which is
why it is a public pair of methods with "reference" in their documentation). Three things ARE
bit-identical and are asserted so: `S0`/`SLog`; every entry touching a cut basis, because those
pairs never left the four-call path (milestone 5's gate, read as the cut PAIRS rather than the
whole conformal matrix, which contains whole pairs too); and `PlanarEntryFill.At` against `Fill`,
because both assemble from the same primitives with the same `Combine` in the same order — A half's
two inner cells ascending, then B, then A + B — which is the order the dense pass's slots
accumulate in. The consequence is that every digest P2 and P3 pinned moved and was re-printed on
this tree, with a note at each literal naming the 1e-12 gate as the bridge. That is not a
loosening of a gate: the digests pinned "nothing moved" through P2's and P3's changes, and the
1e-12 comparison is the equivalent statement for a change that moves association by design.

**5. Why the seconds lag the counts.** A seven-primitive core pass evaluates six inner closed forms
per node against a ramp×ramp call's four, so it costs ~1.5 calls and 6.6× in passes is ~4.4× at
best in seconds (3.6–4.0× measured). A seven-sum remainder pass costs about one call — the `rem`
evaluation dominates — but the fill also carries the scalar block's own remainder over the OTHER
kernel, the radial tables and the row passes, none of which P4 touches, so 5.9× in vector passes
is 2.7–3.0× on the fill. The AIM near fill at N = 3,731 went 2.5× (23.7 → 9.3 s), less than the
dense fill because a near-field row visits each cell pair from fewer rooftops than a full
triangle does.

## How the gates were made

Before any code changed, the P3 tree's `Fill` was dumped to disk for the hero, the 60 mm taper
and the 16 mm conformal taper, and its core-build, fill and AIM near-fill times recorded alone on
the machine. The P4 tree was compared against those dumps entry by entry (max 8e-13 relative; zero
cross-direction entries moved; 1e-16 relative to the largest entry). The same three numbers came
back from the in-tree reference, which is what the permanent test holds. The pass counters are
asserted as ratios against the reference's own count so the mesher's cell count is not a hidden
parameter of the gate.

## What changed, for a reader who only wants the code

See `HISTORY.md` §P4's "What was changed in code". The one-sentence version: `BuildCores` and
`Fill` each run ONE cell-parallel pass over ordered cell pairs that serves the scalar block and
both flow directions, scattering through per-basis-pair slots with one writer each; the four-call
path survives for cut pairs and as the reference; `PlanarEntryFill` memoises the same primitives
per ordered pair.

## Not done, on purpose

The multi-level fill's horizontal remainder still takes four calls per pair per frequency (its
core build got P4's factor; its per-pairing remainder tables are the reason it was not widened
into here — the brief names `AddDirectionBlock` and `PlanarEntryFill`). No quadrature rule,
clustering or closed form changed; the ẑ blocks were not touched. The 256 mm line's LU was scaled
from P1's measurement, not re-timed.

# P5 — translation classes: every cell pair that is a translate of another is one integral (2026-08-29)

`docs/sonnet-briefs/brief-em-p5-translation-class-memo.md`. **Every number is in `HISTORY.md`
§P5; this is the narrative.**

## What was asked, and what turned out to be true

The brief's premise holds exactly: on a tensor-product grid with separation-only kernels, an ordered
cell pair's seven P4 primitives depend on six numbers and a rule, and the seven fixtures reuse each
class 2.5× to 30× — the brief's table reproduced to the pair under its own counting method, all
seven rows. The class table is now the production layout: one `int` per ordered band pair, the
seven primitives once per class, no per-basis-pair triangles at all. The core build fell 3.7–41× in
wall clock and the per-frequency fill 2.8–5.8× beyond P4; AIM's near fill on the 256 mm line 6.8×.
Four things in the brief did not survive contact with the code, and one of them changed the gate.

## Findings

**1. Exact `==` on the gridline differences does not hold, so the spacing classes are quantised at
1e-12 relative — as the brief allowed, and SAID here as it asked.** The mesher writes a bulk
gridline as `a + len·i/n` and the marcher rescales the graded runs, so the hero's x axis carries 15
exactly-distinct spacings for what are 6 classes at 1e-12; the in-class spread reaches 4.0e-13 on
the GaAs line and 3.7e-13 on the 60 mm taper. A class is represented by its smallest member. Nothing
else in the key is a double: each axis's spacing LIST between the two cells is hash-consed element by
element into an integer id, and a list read downward in index shares the id of the equal list read
upward, which is what makes a symmetric line's two graded ends one class.

**2. The brief's "1e-12 relative per entry" is not a property the reference itself has, and the
gate is on the diagonal scale instead.** Every value in P4's matrix is computed at the pair's
ABSOLUTE coordinates, and the corner-summed closed forms lose about 1e-16 · (x/w) of their own value
there — measured before any P5 code existed, on the P4 tree: the self core of a 0.5 mm cell moves
2e-13 relative when the same cell is placed at x = 1 m instead of at the origin, the far core of two
cells 0.25 m apart 5e-13. A class value is computed with the outer cell at the origin, so it is the
MORE stable of the two; the two disagree by ~1e-13 of the near cores. D4's assembly then takes
signed second differences of those cores, and for an aligned far pair of an x̂ and a ŷ rooftop the
four terms cancel to 1e-10 … 1e-14 of the diagonal, so a fixed 1e-13 · |Z_ii| disagreement reads as
1e-8 to 1e-1 RELATIVE on entries that are themselves cancellation residue: on the GaAs line 0.118 on
an entry that is 1.2e-14 of the largest, on the 256 mm line 2e-3 on one that is 6e-13 of it, and
millions of entries per matrix over 1e-12 by that measure. Measured on all seven fixtures against
P4's own arithmetic: max |Δ_ij| / √(|Z_ii||Z_jj|) between 6e-14 and 4.7e-13, max |Δ| / |Z_max| ≤
3e-13, and 7e-8 relative at worst on any entry at or above 1e-6 of the largest. The gate that ships
is |Δ_ij| ≤ 1e-12 · √(|Z_ii||Z_jj|) per entry — the scale on which the factorisation reads the
entry — and the solved currents of `Z x = e_k` on the coarse hero agree to ~1e-12 relative through
the LU. The per-entry relative figures are reported beside the gate rather than tuned away. The
only bit-identity P5 keeps is the one that stands alone: the scalar core of every pair with a cut
cell (its own row, the same conformal call in the same orientation). An entry touching a cut BASIS
is not bit-identical any more, because its scalar block also sums whole-cell pairs, which are
classed — P4's cut-pair bit-identity claim is therefore re-pointed at P4's own retained arithmetic
(`PlanarP4MomentCacheTests` runs on `BuildCoresByPairs`/`FillByPairs` now) rather than weakened.

**3. Orientation is preserved and the 180° rotation is folded, but the RULE had to go into the
key.** P4's lesson — the outer Gauss rule and the inner closed form are not interchangeable to
1e-12 — means a class never swaps outer and inner; what the brief's "(Δx, Δy) ≥ 0 lexicographically"
folds is the rotation about the outer cell, under which a rising ramp becomes a falling one, so a
rotated member reads the representative's (A, B) halves swapped: `Combine(outerB ^ rot,
innerB ^ rot, …)`, one xor, no arithmetic. The trap the brief did not name: two equal cells offset
by (4, 4) cells have τ = 4·√(w²+h²)/√(w²+h²) = 4 in exact arithmetic — exactly `FarRatio` — and
floating point decides per pair which side each lands on, so a class carrying its representative's
rule would move such members by the rule change (~1e-6). The τ band, computed from the member's own
floats exactly as `RuleFor` does, is the low two bits of the key, and the class counts came out
16–17% above the brief's unordered counts for that and for the band's 20% extra ordered pairs.

**4. The representative is synthetic — a pure function of the key — and that is what keeps AIM
bit-identical to the dense fill and R-fil-11 intact.** A first-visited member as representative
would make AIM's row-parallel near fill scheduler-dependent in the last bit, and would make the
dense build and the per-entry fill integrate different members of the same class. The class
primitives are integrated on outer cell [0, w_a] × [0, h_a] and inner cell at the class's signed
offset, every dimension the representative spacing of its class, the rule the class's band. `At`
against `Fill` stays bit-identical (`P4_3`, `AimAcceleratorTests.T1`) because both read the same
class through one `WholeVectorEntry`.

**5. The memory win has a break-even, and it is stated rather than assumed.** The class layout holds
4 bytes per ordered band pair (≈ 0.6 m²) plus 112 bytes per class (seven primitives × two kernels),
where P4 held ≈ 24 m² bytes of triangles; the two meet at a reuse of ≈ 3×. Above it the win is real
— the 256 mm line 83 → 25 MB, the tapers 4.5–5×; below it the layout is slightly LARGER (the hero
1.8 → 2.6 MB, the GaAs line 3.6 → 5.9 MB, both trivial in absolute terms), and a mesh with no
translation reuse at all would hold up to 2.9× P4's bytes. A hybrid that scattered the class table
into P4's triangles when the count came out unfavourable was considered and not built: it keeps a
second production layout alive in every reader for a case none of the seven fixtures approaches.
The a-priori figure the ceiling refusals quote (`PlanarSystem.CoreBytes`) stays P4's formula,
documented as a conservative quote rather than a reconstruction; `PlanarFillCores.CoreBytes` reports
what was allocated, classifier tables included.

**6. The GaAs line's 2.1× is a MESHER finding, recorded for a separate brief and not fixed here.**
Its x grid is asymmetric: the −x end carries the graded fan (2.16, 6.47, 19.4, 58.3, 175 µm into a
208 µm bulk of 8 cells) and the +x end carries NO fan at all — 32 cells of 2.16 µm, i.e. 69 µm of a
2 mm line meshed at the edge-cell pitch. The hero's x axis shows the same asymmetry in a milder
form (86.6, 174.8, 352.7 → 711.6 µm on the left; 538.9, 173.2, 86.6, 86.6 on the right), which is
the "end grading is not exactly mirror-symmetric" `CLAUDE.md` §3.5 already records as the reason a
plain line's two ports do not share a calibration. On a symmetric grid the two ends would be one
class family and both lines would reuse more.

## How the gates were made

Before any code changed, the P4 tree's `Fill` was dumped to disk for all seven fixtures, its core
build, fill and AIM near-fill times recorded alone on the machine, the brief's class counts
reproduced with the brief's own method, and the translation-sensitivity of the closed forms probed
on a hand-built row of cells at four offsets. The class-layout matrices were compared against those
dumps entry by entry, which is where finding 2 came from; the same comparison, against the retained
`BuildCoresByPairs`/`FillByPairs` in the tree, is what the permanent tests hold. Class counts are
literals in the routine test (`P5_1`, hero and 60 mm taper) and in the Benchmark one (all seven).

## What changed, for a reader who only wants the code

See `HISTORY.md` §P5's "What was changed in code". The one-sentence version: `PairClassifier`
(new file) keys every ordered band pair on grid indices; `BuildCores` classifies the band, sorts the
keys, integrates one synthetic representative per class and keeps rows for what no class serves;
`Fill` evaluates the remainder once per class and assembles every entry from the class table
through `WholeVectorEntry`, which `PlanarEntryFill.At` shares; P4's per-pair pass survives as
`BuildCoresByPairs`/`FillByPairs`, the reference.

## Not done, on purpose

The multi-level fill reads its cores through the class table (so its core build got the full
factor) but its per-frequency horizontal remainder is still four calls per pair, as after P4 — the
brief names `Fill` and `PlanarEntryFill`. No quadrature rule, panel or closed form changed. The
mesher's end asymmetry (finding 6) is recorded, not touched — the brief forbids changing the mesher
to increase reuse. The 256 mm line's LU was not re-timed; the per-point crossover is scaled from
P1's measurement and says so.

# P6 — AIM's frequency-independent state, built once per mesh (2026-08-29)

`docs/sonnet-briefs/brief-em-p6-aim-frequency-independent-state.md`. Measured tables in
`HISTORY.md` §P6; this is what was learned.

**1. The brief's premise was stale by two phases, and the measurement was taken before the code was
touched.** It quotes §12's "25 s per frequency at N = 3,731"; on the tree P6 started from that build
was 1.75 s, because P4 and P5 had already taken the near fill from 23.7 s to 1.07 s. What P6 could
remove per frequency was therefore the projection (0.14 s), the near set (in the 0.15 s "tables"
figure) and the singular-core share of the near fill — measured afterwards at 0.3 s of the 1.07 —
not twenty seconds. The structural claim stood (the dense path runs its singular quadrature once per
mesh, the accelerator ran it once per frequency), and the honest result is **1.2–1.4× per point**,
recorded as such rather than as the order of magnitude the premise implied.

**2. The mirror index the brief listed for the geometry was measured and left out.** At 4 B per near
entry it is 18.2 MB at N = 11,959 and took the resident set from 196.2 to 214 MB — past the "under
200 MB at the ceiling" sentence `CLAUDE.md` §8 stands on — to save a rebuild that is one binary
search per lower-triangle entry: 1 / 5 / 17 ms per frequency at the three N. The transpose position
is found inline instead. A memory series should not spend 18 MB to buy 17 ms.

**3. The near cores' store is a flat array, not dictionary nodes, because the memory gate would not
have seen them otherwise.** `P1_3` counts the arrays an operator holds by walking its object graph;
values held in `ConcurrentDictionary` nodes are invisible to it, and its element sizing put every
struct at 0 bytes. The store is chunked arrays of 168-byte `CellPairMoments` addressed through a
key-to-slot index, the walker now sizes a struct element with `Unsafe.SizeOf`, and the report's
208 B per class is checked rather than asserted. The count itself is the P5 compounding the brief
anticipated: ~9–10 thousand classes at N = 552 and at N = 11,959 alike, 2–2.5 MB, against the
~130 MB the brief's per-cell-pair arithmetic would give at the ceiling.

**4. Splitting the near fill's timer showed that the AIM correction, not the remainder quadrature,
is now the largest per-frequency term** — 43–46% of the build at N ≥ 3,731, ahead of the sparse LU.
`AimEntry` does `(M+1)⁴ = 256` complex multiply-adds per near pair per block; only the kernel values
depend on frequency, and two stencils share at most `(2M+1)² = 49` distinct grid offsets, so the sum
could be 256 real products into 49 offset weights and 49 complex multiplies. Recorded for P7/P8;
not built here.

**5. The time crossover is N ≈ 1,100, measured, not the 3,700 of §12 or a scaled estimate.** One
dense point (P5 fill + LU) against one accelerated point at N = 552 / 994 / 1,912: 0.24 / 0.54 / 2.03 s
against 0.37 / 0.71 / 0.96 s. Below the crossover the accelerator is 1.3–1.5× slower and the dense
path stays the shipped default.

## How the gates were made

Before any code changed, a throwaway test on the committed tree dumped the whole accelerated matrix,
every near exact entry, the solved current and the iteration count at three frequencies on the
coarse fixture; the same test on the finished tree produced a byte-identical file. That is what
licenses storing the stencils as `double` and moving the cores across the seam without a tolerance.
The permanent gates are bit-identity between the four-argument `Build` (still geometry-and-operator
from scratch) and an operator over a shared geometry at five frequencies, the same through
`PlanarSolveContext`, the once-per-sweep counters, and the report's accounting reconstructed term by
term. The timings are one Benchmark method, run twice per rung, both readings recorded.

## What changed, for a reader who only wants the code

`PlanarAimGeometry` (new) holds the grid, the stencils, the near set and a `PlanarEntryCores` (new,
the frequency-independent half of `PlanarEntryFill`) warmed over the near set; `PlanarAimOperator`
is built per frequency over it and holds the grid kernels, the near entries' remainders and assembly,
the correction and the sparse LU. `PlanarSolveContext.AimGeometry` is lazy beside `Cores`.
`PlanarAimOperator.Build(cores, …)` still works and builds both, so every existing caller and gate is
unchanged.

## Not done, on purpose

The mirror index (finding 2). The correction's restructuring (finding 4). The projection order,
pitch, near radius, tolerance and preconditioner are untouched, as the brief requires; the
multi-level refusal moved to `PlanarAimGeometry.Build` with its text unchanged. `AimAccuracyTests`
was not re-run: it reads only the four-argument `Build`, shown bit-identical, and the owner asked
that the Benchmark tier not be spent where a cheaper measurement answers.

---

# P7 — the dense factorisation (brief-em-p7-symmetric-inplace-factorisation.md, 2026-08-29)

## What was asked, and what turned out to be true

Replace NumFlat's general LU on the dense planar path with an unpivoted, in-place, blocked, parallel
complex-symmetric LDLᵀ; gate it on a residual rather than on faith; keep the LU reachable as the
oracle. All five milestones landed. **The factorisation is 11.5× faster at N = 4,836 in the shipped
configuration and holds a third of the memory**, and no fixture came within three and a half decades
of the brief's own "stop and report" residual, so no pivoted alternative was needed.

The full tables are in `HISTORY.md` §P7. What follows is what a reader would otherwise have to
rediscover.

## Findings

**1. `dotnet test` builds DEBUG, and that INVERTS this particular comparison.** Every other timing in
this area compares managed code against managed code, so the build configuration has always cancelled
and nobody has had to think about it. It does not cancel here: NumFlat's LU is native and moves 1%
between configurations, while `SymmetricFactorization` is ordinary C# and runs **11× slower**
unoptimised. In Debug the top rung reads 143.8 s at cap 1 against an LU of 40.7 s — "the replacement
is three and a half times slower" — and in Release the same code reads 13.1 s against 41.2 s. **Any
in-suite benchmark that compares a managed implementation against a native one is measuring the build
configuration**, and this is the first one in this area that does. `PlanarP7FactorCostTests` prints
its own configuration above its table and asserts only what holds in both.

**2. A synthetic decaying matrix is not a stand-in for Z when the thing being timed is the LU.** A
complex-symmetric fixture with a `1/(1+|i−j|)` off-diagonal decay timed NumFlat's LU at **97.0 s** at
N = 4,836, reproducibly, against **41.2 s** on a real filled Z of the same size — while
`SymmetricFactorization` read 13.1 s on both. The synthetic fixture is fine for the correctness
tests, which is where it stayed; the cost table is measured on real matrices.

**3. The brief's 1e-12 residual gate cannot be applied to its own worst-conditioned fixture, and the
reason is the fixture.** At 120 MHz — just inside `Dcim.CanFitAtFrequency`'s floor — the residual is
5.5e-12 for the LDLᵀ and **3.8e-12 for the general LU on the same matrix**. That is the MPIE's
low-frequency breakdown (the vector term vanishing like ω against a scalar term growing like 1/ω), a
property of Z that a pivoted factorisation misses by the same margin. Holding the unpivoted form to a
bound the pivoted reference also misses would be measuring the fixture — this area's most expensive
recurring mistake. The gate is therefore in two parts: the three series fixtures keep the brief's
1e-10 / 1e-12 verbatim, and **every** fixture is held to 1e-8 absolute *and* to within 10× of the
general LU's own residual on the same matrix, which is the question actually being asked. Measured
ratio: **1.4–2.0× on all five**.

**4. The live-heap trap P1 documented is about SCOPE, not about the counter.** A first version of the
memory table measured a whole frequency point cumulatively (cores → fill → factor) and read a
**negative 188 MB** at the largest rung, even with a blocking compacting collect at each end. Scoped
to one factorisation on a fresh copy, with the baseline taken immediately before that copy, the same
counter is exact to the last megabyte: 357.0 MB against 356.9 MB of matrix, and 1,070.6 against
3 × 356.9 for the LU. **`GC.GetTotalAllocatedBytes` is not the safe alternative it looks like** — it
is process-wide and counts every thread, so the LDLᵀ column picks up the trailing update's own
`Parallel.For` task objects: 0.2–0.9 MB, independent of N, i.e. noise.

**5. Only the LOWER triangle is ever read, and the fill computes exactly that.** `PlanarFill.Fill`
and `FillMultiLevel` both write the lower triangle and `MirrorLowerToUpper` copies it, so the
factorisation reads precisely the entries that were computed — the multi-level path included, whose
symmetry `CLAUDE.md` describes as "structural" rather than bit-for-bit. There is no
symmetrisation and no perturbation anywhere in this change. It also means that O(N²) mirror write is
now arguably dead on the dense path; establishing that is its own piece of work (`PlanarEntryFill`,
the AIM path and several tests still read `Z[i,j]` for arbitrary (i,j)).

**6. Packing the triangle would RAISE the peak, not lower it.** 8·N² is the steady state a packed
factor would hold, but the matrix arrives from the fill as a full N×N array and packing it needs a
transient 24·N² — higher than the 16·N² the in-place full-lower form never leaves. A refusal is about
the peak, so full-lower in place is the right choice unless the FILL is changed to build packed,
which the brief forbids.

**7. `PlanarSystem.Matrix` had to become an exception rather than a comment.** The factorisation
overwrites the lower triangle and leaves the UPPER one untouched, so a stale read of a symmetric
matrix returns entirely plausible numbers from the previous frequency. Six call sites in the tests
held their own reference to the matrix they had wrapped; all six now pass `matrix.Copy()`, in the
test, which is where a reader can see what the copy is for.

## How the gates were made

The oracle is the code being replaced: `PlanarFillSettings.UseSymmetricFactorization = false` selects
NumFlat's LU, and every accuracy gate solves the same Z both ways. Bit-identity is not available and
was not asked for — two different factorisations of one matrix do different arithmetic — so the gates
are the solved current vector to 1e-10 relative, the residual to 1e-12 on the series fixtures, and
the published de-embedded s-parameters to **1e-9 absolute** over a 5-point sweep on three fixtures
(worst measured 7.4e-13). Everything routine runs on the COARSE mesh, deliberately: a factorisation
cannot tell a coarse mesh's Z from a shipping mesh's, so a finer mesh is a bigger matrix rather than
a harder question. `Category=Benchmark` carries exactly one method, the cost measurement, which is
the one place a large N genuinely is the subject.

**The whole-sweep digest gate was strengthened rather than re-pinned.** `P2_6` hashes the published S
of a de-embedded sweep against a literal, and P4 and P5 each moved that literal when they moved the
last bits. P7 moves it too — but a bare re-pin accepts any change, including a real one, so the test
now runs the same sweep through BOTH factorisations and asserts that **NumFlat's path still
reproduces P5's literal bit for bit**. That is the load-bearing half: it says the factorisation moved
and nothing else did — not the fill, the cores, the calibration, the peel or the renormalisation.

Two structural properties are asserted rather than measured: caps 1 / 2 / 4 / unbounded and a shared
`PlanarParallelBudget` produce **bit-identical** factorisations (R-fil-11's rule — the trailing
update's parallelism is over destinations each written by one iteration), and the P-right-hand-side
substitution is bit-identical to P separate solves.

## What changed, for a reader who only wants the code

`SymmetricFactorization.cs` is new and self-contained. `PlanarSystem` holds its matrix privately,
throws from `Matrix` once factored, and gained `Factor()` (which replaces `_ = system.Lu` as the way
to force and time the factorisation) and `Factorization` / `LastResidual`. `IPlanarOperator` gained a
default `Solve(IReadOnlyList<Vec<Complex>>)` that `PlanarSystem` overrides, so `Y = BᵀZ⁻¹B` streams
the factor once for all P ports. `PlanarFillSettings` gained `UseSymmetricFactorization` (true) and
`TrackFactorizationResidual` (false). `PlanarSystem.FactorBytes` now means the shipped path;
`LuFactorBytes` is its old body under its own name; `SymmetricFactorBytes` is `16·N`.

## Not done, on purpose

No native BLAS (the root `CLAUDE.md` requires asking, and the brief forbids it). No pivoting —
Bunch–Kaufman stays the NAMED follow-up and `FactorPanel` refuses by name on an exactly zero pivot
rather than falling back quietly. No vectorisation of the trailing update: it is scalar managed C#
and NumFlat's native LU still beats it per flop, so a `Vector128`/`Vector256` complex multiply-add
and destination-column register blocking are an obvious further 2–4× — recorded, not built, because
the brief's target was met by 11.5×. `PlanarDeembed.StaticCapacitance` and `ChargeSolver` both factor
a symmetric matrix over CELLS and would take the same treatment; they are outside this brief's scope
and are now the only general LUs left on a planar run. **`UnknownCeiling` was re-asked and NOT
moved** — 1 GB now buys N = 6,968 against 4,454, so memory no longer binds at 5,000, but the fill's
time and a mesh's accuracy at that size have not been measured and the constant is the owner's.

# P8 — AIM's near radius had no physical floor (2026-08-29)

**Symptom.** `brief-em-aim-ceiling.md`'s A1b ladder — refine the mesh at a fixed 64 mm footprint —
climbed GMRES 21 → 143 → 372 iterations over cells/λ 80 → 120 and did not converge at all at 140
(N = 13,967, residual 9.1e-5 against a 1e-8 tolerance). The A1a ladder, growing the board's LENGTH at
the shipping resolution, was flat over the same N range. `AcceleratedUnknownCeiling = 12,000` was set
with margin under A1b's failure.

**Cause, in one sentence.** `PlanarAimSettings.NearRadiusFactor` measures the exact near field in
**largest basis supports**, and refining a mesh shrinks the largest support — so the near field
shrinks in METRES (8.93 h at cells/λ = 20, 1.28 h at 140, and the same at every board length to four
figures) while the coupling it has to span does not, because the grounded slab's image sits at a fixed
depth 2h. The scalar kernel is `1/ρ − 1/√(ρ² + 4h²)` plus smooth terms: past ρ ≈ 2h the residue falls
like `2h²/ρ³` rather than `1/ρ`, so **2h is where the coupling stops being long-ranged** and a near
field narrower than it is missing the dominant part.

**Fix.** `PlanarAimGeometry.NearRadiusM = max(NearRadiusFactor · maxSpan, NearRadiusMinM)`, where
`NearRadiusMinM` is null by default and derives `2·h`. The A1b ladder becomes 21 → 28 → 36 and the
failing rung converges; the same ladder at 16 mm is 2, 4, 6, 12, 14, 10, 10 against 2, 4, 6, 12, 46,
144, 273. `4h` was never needed — the brief's own stopping rule is "flat at 2h".

## Four things worth keeping

**1. It is not only the preconditioner — the brief's premise was half right.** The brief said "nothing
about the AIM projection's accuracy is at stake; the preconditioner is what degraded". But the near
radius is also the boundary between entries computed EXACTLY and entries the projection approximates,
so shrinking it degrades the OPERATOR as well. `|ΔI|` against the dense solve on the same mesh:
4.90e-7 at cells/λ = 20, **5.93e-4** at 140 — a 1,200× loss, of which the floor recovers 17×
(3.58e-5). Both measured at the same converged GMRES residual, so it is the operator and not the
stopping rule. A ladder that only watched iteration counts would have missed this.

**2. The obvious alternative explanation was checked and ruled out, cheaply.** A preconditioner that
silently failed to factor would leave GMRES unpreconditioned and produce exactly this iteration
climb. `PlanarAimReport.FactorNonZeros` (P1's field) is non-zero on every rung including the one that
fails, so the factorisation succeeds throughout — it is a good factorisation of a near field that is
too narrow. Printing that one existing column cost nothing and settled it before any code changed.

**3. The floor's real cost is the sparse LU's FILL-IN, not the near set, and it is
aspect-ratio-dependent.** Widening the radius grows the near matrix ~1.5× at the top rung; what it
does to the factor depends on how much of the board the radius spans. On a 64 mm line the fill-in
ratio is 1.13× and the floor is a NET WIN (build + solve 11.0 s → 7.1 s, because the solve collapses).
On a 16 mm line the 3.2 mm radius covers the whole 2.9 mm width and a fifth of the length, the band is
wide against a small N, fill-in is 2.93× and the same change is a net LOSS (12.1 s → 19.0 s). Both are
correct answers; the short board is the artefact. Memory grows either way — 303 → 426 MB at
N = 10,708.

**4. The slab height is a REQUIRED argument, on purpose.** `PlanarAimGeometry.Build` refuses a
non-positive `slabHeightM` by name rather than defaulting to "no floor", because the failure of a
forgotten plumb is silent: the geometry takes the pre-P8 radius and returns a complete, plausible
answer that merely takes 20× longer to reach, or does not converge. Making it required turned all six
call sites into compile errors. `PlanarSolveContext` takes it as a trailing optional — the DENSE path
has no near radius to floor, and 35 dense call sites should not have to carry it — and throws when
`Fill.Aim` is set without one.

**5. It changes no answer anyone currently gets.** The floor binds on no mesh either starter
technology produces at a shipped resolution — R/h is 2.68 to 8.92 on the FR-4 hero at cells/λ 20 and
40, and **4.17 to 50.03 on the GaAs starter**, where a 72 µm conductor on a 100 µm slab has cells that
are large against h by construction. Only the PCB case, whose conductor is nearly twice its slab
height, can be walked into the floor, and only by asking for several times the default resolution.
`HISTORY.md` §P8 §M3 carries the table.

## What the ceiling question became

`AcceleratedUnknownCeiling = 12,000` is **untouched** (the brief forbids moving it, and the decision is
the owner's) — but its basis has changed, and the recommendation is to leave it there for a NEW reason.
A1a, the healthy construction it was set from, is unaffected by the floor and stands exactly as
measured. A1b, the failing construction it took its margin from, no longer fails to converge. What
replaced the convergence risk is a BUILD one: at N = 10,708 the accelerator holds 426 MB on a refined
mesh against 188 MB at N = 12,894 on a coarse one, and one rung further — N = 13,967, the near matrix
8.9% dense — **CSparse's exact sparse LU had not returned after 8 minutes of CPU and 1.43 GB, and was
stopped there**, where the unfloored one factors in 5 s. So the old failure was a GMRES that did not converge and threw; the new one would
be a preconditioner that never finishes building. **N alone no longer bounds the accelerated path** —
near entries per row does. `HISTORY.md` §P8 §M5 carries the sentence that would move the constant,
written out and not applied, together with the density guard it would need beside it.

That is also why `AimCeilingTests.A1_LadderByResolution` is now pinned to the pre-P8 radius
(`NearRadiusMinM: 0`): it exists to record the "before", and on the shipped default its top rung would
never finish. `PlanarP8NearRadiusFloorTests` is where the shipped behaviour is measured.

## Not done

`NearRadiusMinM` is exposed on `PlanarAimSettings` and reachable from no UI — nothing persists it and
`EmRunService` only ever passes `PlanarAimSettings.Default`. That is deliberate: it is a derived
physical quantity, not a knob a user should be tuning, and its one non-default use in the tree is the
`0` that turns the floor off so a test can measure the pre-P8 behaviour.

---

# P9 — adaptive frequency sampling on by default: measured, and the recommendation (2026-08-29)

**A decision brief. Nothing was flipped, and one thing turned out already to have been.**

## What was asked, and what turned out to be true

The brief asks whether `PlanarAdaptiveSettings` should be on by default, on the premise that the
feature ships off. **That premise was two days stale in the half that matters.** The ENGINE default
is still `PlanarSolveSettings.Adaptive = null`, but the shipping USER default was flipped on
2026-08-26: `EmSetupModel.AdaptiveSampling = true`, translated by `EmRunService` into
`PlanarAdaptiveSettings.Default`. Every EM run a user starts today already samples adaptively. So
the live question is not "should it be turned on" but **"was turning it on right, and are 1e-3 and
5 the right numbers"**. The measurements answer both.

## The recommendation

**Leave it on. Keep `Tolerance = 1e-3` and `InitialPoints = 5`. Leave the ENGINE default null.**
Four reasons, each measured:

1. **It never costs accuracy that matters.** On the panel's own default grid the realised worst
   |ΔS| against a fully-solved sweep is at worst **1.42e-3** across five fixtures. The one deep
   feature in the set — a −7.2 dB notch — was recovered **exactly, in all 33 configurations run**,
   at every tolerance and every seed count.
2. **It costs about 1% when it saves nothing.** Two of five fixtures solve every point anyway and
   read 0.99× — the overhead is the probes' interpolant evaluations, and the calibration replays are
   cache hits by construction.
3. **`InitialPoints = 5` is right and raising it is a pure loss.** From 5 to 17 seeds the realised
   error is unchanged to two figures; from 33 up the solved count climbs and the error does not.
4. **`Tolerance = 1e-3` is the right point on the curve.** 1e-2 saves more but its realised error
   reaches 5.6e-3, which is visible on a −40 dB return-loss plot; 1e-4 gives back most of the saving
   (1.72× where 1e-3 gets 3.67×) for an error nobody is reading.

**The engine default stays `null`, deliberately.** It is not the user-facing default and never was;
it is what makes every measured number in L8c/L8d/L9d and P1–P8 reproducible at full precision, and
R-adf-1's bit-identity gate is written against it. Flipping it would change what every engine test
measures and buy a user nothing.

## What the owner should know before agreeing, because it is not what the design note promises

**§10.7's "typically cuts solve count by 5–10×" is not what the shipped default sweep gets. On
1–20 GHz at 101 points the saving is between 1.0× and 1.73×**, and on two of five fixtures it is
exactly nothing. The explanatory number is one column wide: **adjacent points of that grid already
differ by 0.13 to 0.63 in |ΔS| — 130× to 630× the tolerance.** Adaptive sampling can skip a point
only when the interpolant predicts it inside the tolerance, and a grid whose neighbours are that far
apart is not oversampled with respect to a 1e-3 criterion at all.

**The lever that actually delivers the 5–10× is a FINER grid, which inverts the usual advice.** On
the FR-4 hero, a **401-point** adaptive sweep costs **3.01 s** and solves 81 points; a **101-point**
non-adaptive sweep of the same band costs **4.25 s** and solves 101. Four times the frequency
resolution, in less time. With adaptive sampling on, the solved count is set by the STRUCTURE and
not by the grid, so **asking for more points is close to free and asking for fewer buys almost
nothing**. The user documentation currently advises the opposite ("the useful move when in doubt …
is to reduce the number of requested points"); that sentence is now measurably backwards.

## Four things worth keeping

**1. The narrow-resonance failure mode the brief names could not be constructed, and the reason is
structural — it is the same fact as the small saving.** For a distributed structure the transmission
phase rotates at `2π√ε_eff·L/c` per Hz. For a resonance of quality factor Q to be under-resolved the
grid step must exceed about `f₀/2Q`, and at that step the background's own adjacent-point difference
is `≈ π·(L/λ_g)/Q` — below a tolerance τ only when `L/λ_g < τ·Q/π`, i.e. shorter than λ/45 at
τ = 1e-3 and the measured Q ≈ 70. A structure that short cannot host a distributed resonance.
**Whenever a resonance is sharp enough to fall between two solved points, the background is already
forcing refinement to the grid floor, and the resonance is bracketed on the way down.** Read the
other way, that is exactly why so few points can be skipped. Both halves were measured across two
grids, three tolerances and six seed counts; the argument is supported by the measurements, not
proved by them.

**2. The real accuracy caveat is a magnitude one, not a missed-feature one: `Tolerance` is a LOCAL
stopping test, not a global error bound.** On a 1001-point grid at tol 1e-3 the realised worst |ΔS|
is **1.01e-2 — ten times what was asked for**. L9e's `T1_2` already gates this at `worst < 10 × tol`;
these numbers sit at that gate rather than inside it. Anyone quoting the tolerance to a user as an
error bar would be overstating it by up to an order of magnitude.

**3. Adaptive sampling cannot rescue a notch that falls between two REQUESTED frequencies, and the
user doc's own framing of "the problem it solves" says that it can.** R-adf-2 publishes exactly the
grid the user asked for and never inserts a point. The measured fixture makes this concrete: its
notch is 80 MHz wide (Q ≈ 70 — and sweeping tanδ over 1e-5…2e-2 and the coupling gap over
0.15…2.4 mm moves the depth but not the width, so 80 MHz is what this structure class produces), and
the panel's default grid steps 190 MHz. On that grid the notch is lost with the feature on or off;
what the published curve reports as its deepest point is a different feature 5.6 GHz away.

**4. The memory the adaptive branch retains is real but never the binding term.** It keeps every
solved point's kernel, raw matrix and per-port basis currents alive at once, because the calibration
is replayed from a fresh branch state after each insertion — `16·N·P` bytes per solved point, ~4 MB
at N = 1,278 and ~16 MB at N = 5,000. Measured peak managed heap on the taper was **217 MB adaptive
against 227 MB off**: the transient fill and factorisation dominate by two orders of magnitude, and
the difference is GC timing. It is worth knowing about only if the retention ever meets a mesh where
the matrix itself is not the peak.

## How the gates were made

**No new engine test.** The brief gates a flip, and nothing was flipped; `AdaptiveSweepTests` is
unchanged. Every number above came from a standalone Release harness run one fixture at a time —
`dotnet test` builds Debug, which would have inverted the timings, and the benchmark tier's own
warning is that these measurements read ~2× slow alongside each other.

**One UI gate was missing and is now there.** Milestone 3 requires that the panel show a point was
modelled rather than solved. `WorkspaceViewModel.EmRunSummary` already does — but its only test
covered the `adaptive: false` path, so the branch that reports the solved count was ungated.
`EmRunProgressTests.WithAdaptiveSamplingOn_TheFinishedRow_SaysHowMANYPointsWereSOLVED` now asserts
the requested count, the solved count and the word "modelled" all reach the row.

## What changed, for a reader who only wants the code

```
tests/Ui.Tests/EmRunProgressTests.cs   + the adaptive branch of the run-summary row
docs/design/mom-engine.md              §10.7's "rational" and "5-10x" corrected in place
docs/user/src/reference/mom-engine.md  the interpolant, what a notch between samples does, the advice
src/Engine/Mom/HISTORY.md              §P9 — every table
```

No engine source was touched.

## Not done, on purpose

**The default was not flipped in either direction** — the brief forbids it before the owner answers,
and the user-facing default is already where the recommendation says it should be.

**`PlanarAdaptiveSettings` is still reachable only as a whole.** `EmSetup.AdaptiveSampling` is a
bool; `Tolerance`, `Interpolant` and `InitialPoints` are not persisted and no panel exposes them.
Given that `InitialPoints` measurably buys nothing and `Interpolant` was decided by measurement at
L9e, exposing them would be offering knobs whose right settings are already known. If anything here
deserves a control it is the tolerance, and only alongside a plainer statement of what it is not.

# P10 — M2's fan-out is not starving the thread pool (2026-08-29)

`brief-em-p10-fanout-starvation.md` was written as a hypothesis to test, not a finding, and the test
refutes it at milestone 1. **Nothing in the solver's parallelism was changed.** Milestones 2 (one
shared row queue drained by `cap` long-lived workers) and 3 (re-measure the overlap) are conditional
on the instrument showing a stall — it does not — so neither was built. Every table lives in
`HISTORY.md` §P10.

## What was asked

`PlanarFill.ForRows` takes a permit from `PlanarParallelBudget` in `Parallel.For`'s `localInit`. When
`PlanarFanOut` runs K solves at one frequency, each inner loop asks for up to `cap` workers, so K·cap
pool threads are requested and (K−1)·cap of them should sit blocked in `Enter()`. The .NET pool
injects threads at ~1–2 per second once its minimum is exhausted, so the run should stall for seconds
at every frequency while the pool grows — and if it does, part of the 1.09–1.15× ceiling §6 attributes
to hardware is really scheduling.

## Findings

1. **The consequence does not happen, and the wall clock is the proof.** On the brief's own fixture
   (FR-4 80 mm line, N = 1,980, de-embedded — 5 solves, `cap` = `ProcessorCount` = 10) the fill
   reaches all 10 permits **within ~30 ms of starting** and holds them for the whole fill phase. Peak
   threads parked in `Enter()`: **3**, against the 40 the hypothesis predicts. Pool thread count
   11 → 12 over the point.

2. **The mechanism IS real — it is simply gated by the pool's size, and it costs nothing.** Force the
   pool large first (`SetMinThreads(64)`) and the predicted picture appears in full: 34 threads parked
   at once, 20 thread-seconds parked, 151% of the budget's own core-seconds. **The point still takes
   1.25–1.47 s, exactly as it does with the default pool (1.23–1.37 s), and utilisation goes UP
   (84.6% against 78.7%).** A parked thread burns no CPU and holds no permit; what it costs is a pool
   thread, not time. **This is the measurement that settles the phase** — if injection were the stall,
   pre-supplying the threads would have removed it.

3. **`Parallel.For`'s unmet demand queues; it does not block.** This is why the brief's
   multiplication never materialises on a healthy pool, and it is not obvious from reading `ForRows`.
   `Parallel.For` is a replicating task: a worker queues one replica when it starts and work remains,
   and a replica becomes a thread only when the pool has one to give. The trace shows this directly —
   `PendingWorkItemCount` sits at 3–5 through the entire fill phase while parked threads stay at 0–2.
   K concurrent loops produce K·cap **queued work items**, not K·cap parked threads.

4. **The cap can never outrun the pool's minimum, which is the structural reason it works.**
   `PlanarSolve` materialises a null cap as `Environment.ProcessorCount`, and the pool's minimum
   worker count is also `ProcessorCount`. The fill therefore needs no injected thread to reach its
   cap. **A cap BELOW `ProcessorCount` parks the most threads** (at cap 4: pool 12, eight surplus
   threads, 8 parked, 111% of the budget's core-seconds) and still costs no time.

5. **The pool does grow across a sweep, and it is still not the fan-out's doing.** Over five points
   the pool went 11 → 12 → **24** in one jump, with parked threads spiking to 13. The same sweep
   started with a 64-thread minimum ran **3.05 s** against **3.01 s**. Utilisation over the sweep is
   75%, and the shortfall is the sequential Green's-function fit (0.11 s per point, which the trace
   resolves exactly) and `SymmetricFactorization`'s serial panel steps between its parallel trailing
   updates — both visible in the trace as permits-held-zero with an empty queue.

6. **§6's M3 paragraph did not become false and was not touched.** Its fall-off is attributed to core
   heterogeneity on a 4 + 6 box, and P10 removes the one competing explanation that was still open.

## How the gates were made

**No new timing test.** These are machine measurements; a threshold on them would measure the box.
What is gated instead is the instrument itself, in `ParallelBudgetTests`, both routine and together
under half a second:

- `P10_TheBudgetsCounters_SeeTheFillTheyExistToMeasure` — the counters are wired to the **budgeted**
  branch of `ForRows`, so a future re-measurement is not measuring nothing. It asserts only that they
  MOVE: they are process-wide, another test's fill can touch them at any instant, and an equality
  against zero would be a race against the runner rather than a statement about this code.
- `P10_APermitAlwaysComesBack_SoOneBudgetSurvivesASecondFill` — `PlanarParallel.cs`'s header claims a
  permit always comes back, which is what makes the scheme deadlock-free, and nothing tested it. A
  leaked permit is invisible in one fill and fatal in the next, so the gate is a second fill through
  the same **cap-1** budget, on a background thread with a 60 s join, so a regression fails rather
  than hangs.

`REmp8_TheBudgetPath_IsBitIdenticalToTheOrdinaryCappedPath` and R-emp-13's two sweep tests are
unchanged and still pass — the brief requires bit-identity to survive, and since `ForRows` was not
touched it survives by construction rather than by measurement.

Everything else came from a standalone Release harness, one fixture at a time. `dotnet test` builds
Debug, which changes the cost of the arithmetic relative to the scheduling under study.

## What changed, for a reader who only wants the code

```
src/Engine/Mom/PlanarParallel.cs               PlanarParallelBudget: + WaitingThreads, HeldPermits,
                                               EnterCount, TotalWaitSeconds, ResetCounters
tests/Engine.Tests/Mom/ParallelBudgetTests.cs  + the two P10 gates above
src/Engine/Mom/HISTORY.md                      §P10 — the five tables
```

`ForRows`, `PlanarFanOut` and the budget's semantics are untouched. No answer moved and no
provenance hash changed.

## Not done, on purpose

**The shared row queue was not built.** It is milestone 2, conditional on a stall, and there is none
to remove: `cap` long-lived workers draining one queue would arrive at the same `cap` permits' worth
of arithmetic the present scheme already keeps busy from ~30 ms in. It would also have to re-earn
R-emp-13's bit-identity, which the current scheme gets for free because a row is still written once
by one worker.

**No second cap, and no attempt to size the pool.** Both were forbidden by the brief, and the
measurements say neither would buy anything: the run is invariant to the pool's size across a 5×
range of it.

**The parked threads were not eliminated for their own sake.** They are surplus pool threads holding
no permit, and they cost about a thread stack each. If that is ever worth reclaiming it is a memory
argument on a host that already runs a large pool, not a performance one, and this phase measured no
evidence for it.

---

# P11 — the calibration standards' static capacitance, accelerated (2026-08-29)

`brief-em-p11-accelerated-static-capacitance.md`. **The last step of a de-embedded run that was
always dense is not any more.**

D7 references every de-embedded s-parameter to the line's own `Z_c = γ/(jωC_pul)`, and `C_pul` comes
from differencing two calibration standards' static capacitances. Each of those solved `P q = ε₀·1`
over CELLS with a dense m×m complex LU. Turning the accelerator on moved the DUT's ceiling and left
that one where it was, so a wide-port run whose DUT solved comfortably could still be refused —
measured on the 2026-08-14 owner report, whose wide port's calibration standard meshed at N = 6,466
against a 5,000-unknown dense ceiling.

**`P` is exactly the scalar block M5 already projects.** The accelerated charge stencil is a ± pair of
cell pulses per basis; a cell-pulse operator is the same stencil with one cell and `sign = +1`. So
this is not a second accelerator — `PlanarStaticAim` is the same projection, the same near-set rule,
the same grid FFT and the same sparse-LU-preconditioned GMRES, over CELLS, with one grid kernel and
no ω.

## What was found

1. **The near RADIUS is the only knob that acts, and it must be read in M5's unit, not the natural
   one.** Sizing it from the CELL span — the obvious choice for a cell-pulse operator — halves M5's
   radius on the same mesh, because a basis spans two cells. Measured against the dense solve as the
   relative error in `C_pul` (FR-4 hero's 30.8/90.9 mm standards / GaAs hero's 1.14/3.02 mm), with
   `NearRadiusFactor` in BASIS supports: `3 → 2.69e-7 / 8.88e-7`, `4 → 3.69e-7 / 1.61e-7`,
   `5 → 1.98e-7 / 2.89e-8`, **`6 → 1.04e-7 / 2.02e-9`**, `8 → 3.39e-8 / 7.07e-15`, at 99 / 132 / 164 /
   196 / 256 near entries per row. The cell-span reading would have left GaAs at 8.88e-7 — inside the
   brief's 1e-6 gate by 12%, which is not a margin. So the pitch is sized from the largest CELL span
   (the source support, which is what a stencil has to enclose) and the radius from the largest BASIS
   support (the kernel's own range, which is what P8's 2h floor is about); the file header derives
   both.

2. **The projection ORDER is inert here.** M = 2 / 3 / 4 / 5 give `1.05e-7 / 1.04e-7 / 1.08e-7 /
   1.08e-7` on FR-4 and `2.15e-9 / 2.02e-9 / 2.00e-9 / 2.00e-9` on GaAs. Raising it is not a remedy
   for this operator; widening the near field is, and the non-convergence refusal names both in that
   order.

3. **The GMRES tolerance is not what limits this — the projection is.** The brief asked for the
   tolerance that delivers 1e-6 on the DIFFERENCED result to be measured and set from the
   measurement. It is: `1e-6 → 9.88e-8`, then `1e-8`, `1e-10`, `1e-12` and `1e-14` give `1.04e-7`
   **identically in every printed digit**. `StaticTolerance` ships at **1e-10**, two decades under
   where the answer stopped moving so a standard pair whose lengths are close (a larger
   `C/(C₂ − C₁)` than the 1.5-1.9 these fixtures measure) still has the solve's own contribution well
   under the projection's — and deliberately not tighter, because a tolerance GMRES cannot reach
   turns into a refusal. Cost at the shipped value: 3-4 iterations.

4. **A finer auxiliary grid buys nothing here, which is the opposite of M5's own N-ladder — and the
   two knobs are not independent.** At the shipped radius the pitch ladder is flat below 0.5 cell
   spans (`0.125 → 1.19e-7`, `0.25 → 1.11e-7`, `0.5 → 1.04e-7`) and degrades above it
   (`0.75 → 1.60e-7`, `1.0 → 3.32e-7`). At a radius of 3 supports the SAME ladder has the opposite
   sign (`0.5 → 2.69e-7`, `0.25 → 1.31e-6`, `0.125 → 2.45e-6`). What that says is that refining the
   grid cannot compensate for a near field too narrow to hold the coupling — P8's finding in another
   coordinate.

5. **The setup refusal this brief was told to re-word was UNREACHABLE, and no test had ever seen its
   message.** `brief-em-deembed-ceiling-closeout.md`'s C1 put a de-embedding-specific ceiling check in
   `PlanarSolve.Run` AFTER the calibrators were constructed — but `PlanarPortCalibrator`'s constructor
   builds one `PlanarSolveContext` per standard, and that constructor's own eager
   `SurfaceMesher.GuardCeiling` throws first, with a correct sentence about a mesh that names neither
   de-embedding, nor which port, nor a remedy. (`EmDeembedCeilingTests` gates C2's `StaticCapacitance`
   guard message, which is a different one; the brief's "EmDeembedCeilingTests asserts the sentence"
   was not the case.) The check now runs BEFORE the calibrator is built, on a standard set the
   calibrator is then handed rather than rebuilding, and two tests assert it —
   `EmDeembedCeilingTests.P11_ADenseDeembeddedRun_IsRefusedAtSetup_AndTheRefusalNamesTheAccelerator`
   and its accelerated counterpart.

6. **The dense refusal's first remedy is now the accelerator, not turning de-embedding off.** It used
   to have to say the accelerated solve "will NOT help here"; since the standards' static solve is
   accelerated too, it does help, and the sentence says so and quotes the accelerated ceiling.

## The taper, re-run (the brief's M4)

Reconstructed at the reported geometry (13.1 mm into 299 µm over 28.575 mm on 20 mil RO4350B,
cells/λ 5, edge 3, mesh frequency 500 MHz, 1-5 GHz), straight-flanked rather than Klopfenstein, which
lands at N = 4,239 rather than the reported 7,749 — the exact count was never the point, the width
RATIO is, and the failure mode reproduces exactly: **the WIDE port's calibration standards mesh at
N = 1,498 / 7,603 / 4,273**, one of them past the dense ceiling on its own.

| | before P11 | after |
|---|---|---|
| dense + de-embedded | refused at setup | refused at setup, and the sentence now names the accelerator |
| **accelerated + de-embedded** | **refused at setup** | **runs**: 3 points (1 / 3 / 5 GHz) in **107 s** |

The wide standard's static solve on its own (m = 3,864 cells, n = 7,603): **9.7 s and 222 MB**, at
607 near entries per row and 15.7% fill, against **683 MB** for the three m×m complex matrices the
dense route holds — which the ceiling refuses outright rather than allocating. 9.24 s of the 9.7 s is
the sparse LU of the near matrix; the GMRES solve itself is 72 ms at 4 iterations.

What the user now gets, at the published planes: `|S₁₁|` −1.24 / −2.13 / −1.23 dB and `|S₂₁|`
−6.10 / −4.22 / −6.28 dB at 1 / 3 / 5 GHz, `|S₁₁|² + |S₂₁|²` = 0.998 / 0.991 / 0.990 (passive), with
port 1's `Z_c` measured at 6.59 → 7.00 Ω against the 6.92 Ω the taper was drawn for and port 2's at
82 → 84 Ω against 100 Ω. The port-2 figure is not a P11 result: that port is 2 basis functions wide
and the run raises its own feed-clearance note about it.

## What the gates are

`PlanarP11StaticAimTests`, 12 tests, **0.93 s together** — all routine, none tagged. Deliberately on
the COARSE standards and small uniform plates: every claim is that the accelerated OPERATOR agrees
with the dense one on the same mesh, which a coarse mesh tests exactly as hard.

- the accelerated PRODUCT against `PlanarFill.ScalarPotentialMatrix`'s own `P x` (asked of the
  operator, not only of the solved capacitance, because a solve can absorb a product error into a
  charge vector whose SUM still looks right);
- `C_pul` and each standard's total against the dense solve, to 1e-6;
- the tolerance ladder, kept as a test rather than a note so the default's justification is re-run;
- the self-kernel sentinel moved ×0.1 and ×4 — the near-set completeness gate, since G(0) is
  arbitrary and only legitimately so if every overlapping-stencil pair is near;
- `PlanarStaticLimitTests`' plate-over-ground oracle and its quadrature-separability oracle, through
  the accelerated path, plus the sharper form of the first (accelerated vs dense at each spacing);
- `Aim = null` is today's code asserted as EXACT equality, not to a tolerance;
- an accelerated call with no slab height refuses (a silent dense fallback would reinstate the
  ceiling invisibly);
- the near field is O(m) in entries per row — a COUNTER, not a stopwatch.

`EmDeembedCeilingTests` gains the two setup-refusal gates in item 5 above, on the owner's own taper
shape. `EmCeilingRefusalTests.Gate1_…` is inverted from "refuses at setup" to "no longer refuses at
setup", proved with a **pre-cancelled `RunControl`**: the first cancellation checkpoint is one
Green's-function fit into the first frequency, so an `OperationCanceledException` is positive proof
that setup completed, at a fraction of a second where solving that taper de-embedded is ~40 s per
point.

## No second implementation

The shared parts were MOVED, not copied: `AimProjection` (the Vandermonde inverse, the moment match,
the moment quadrature, the near set) and `AimGridFft` (the circulant embedding and the cyclic
convolution) came out of `PlanarAim.cs` unchanged, and `PlanarAimGeometry`/`PlanarAimOperator` call
them. The near set's exact entries are `PlanarPulsePotential`, which is `PlanarEntryFill`'s own
`P(a, b)` carved out whole — same class-keyed memo, same singular cores — so the near field is
literally the dense path's arithmetic rather than a second formulation of it.

## What changed, for a reader who only wants the code

```
src/Engine/Mom/PlanarStaticAim.cs   NEW — PlanarStaticAim, PlanarStaticAimReport
src/Engine/Mom/PlanarAim.cs         + AimProjection, AimGridFft (moved out of the two AIM classes)
                                    + PlanarAimSettings.StaticTolerance (1e-10, measured)
src/Engine/Mom/PlanarFill.cs        + PlanarPulsePotential (carved out of PlanarEntryFill.P)
                                    + PlanarEntryCores.PrepareScalarPair
src/Engine/Mom/PlanarDeembed.cs     StaticCapacitance: + the accelerated route and a slabHeightM
                                    parameter; GuardCapacitanceCeiling is now public and takes the
                                    route's flag
src/Engine/Mom/PlanarSolve.cs       the standards' ceiling refusal moved BEFORE the calibrator is
                                    built (it was unreachable) and re-worded;
                                    PlanarPortCalibrator takes a pre-built standard set
```

## Not done, on purpose

**The static accelerator does not share the DUT's `PlanarAimGeometry`.** It builds its own
`PlanarEntryCores`, so a mesh that has both pays two class-core warm-ups. Sharing would force the
LONGEST standard's basis geometry to be built even when no frequency ever selects it — which is
exactly the lazy build M4 of the sweep-performance brief put in — and the class store is cheap.
Measured cost of the duplicate on the taper's wide standard: the near fill is 219 ms of a 9.7 s
solve.

**`AimEntry`'s `(M+1)⁴` lookups per near pair were not restructured.** §8's own P6 note records the
same hotspot on M5's operator; here it is 93-354 ms against a sparse LU of 1.6-9.2 s, so it is not
what to fix first on this path.

# P12 — multi-level and vias under AIM, as a bordered system (2026-08-29)

`brief-em-p12-aim-bordered-vias.md`. The tables are in `HISTORY.md` §P12; what is here is the
narrative, the two findings that are not in a table, and the ceiling sentence the brief hands back
to the owner.

## What was refused, and what the obstacle actually was

`PlanarAimGeometry.Build` threw on any mesh carrying a ẑ basis and `PlanarSolveContext.SolveAt` threw
on the general kernel whenever `Aim` was set, so a board with one ground via ran at the **dense**
5,000-unknown ceiling however much of it was ordinary horizontal metal. The refusal's stated reason
— "a different grid kernel per height pairing and a projection with a derivative in it" — is a true
statement about PROJECTING the ẑ bases. It was never an argument for projecting them.

R-via-5 already orders every horizontal rooftop before every vertical one, so the matrix is
`Z = [Z_hh Z_hz; Z_zh Z_zz]` with `N_z` in the tens, and the three blocks have different economics:

- **`Z_hh`** is the operator M5 already accelerates, one pairing at a time. Every level shares the
  SAME auxiliary grid — the grid is in-plane and the levels differ only in z — so a second level
  costs one more kernel table and one more FFT hat pair per component, `L(L+1)/2` for L levels. The
  scatter and the gather become per level. **The stencils, the moments and the near set are unchanged
  objects**: they are in-plane, and the near-set rule (radius ∪ stencil overlap) is in-plane too, so
  it is a valid superset for a cross-level pair with no argument needed.
- **`Z_hz`, `Z_zh`, `Z_zz`** are dense, filled through a new internal seam onto `PlanarFill`'s own
  `MixedEntry`, `SingularPrismPart` and cell-pulse potential. Not a re-derivation — the same
  functions, called per entry instead of per row.

## Findings

1. **The gate that means something is EXACTNESS, not a tolerance — and the near radius is the knob
   that produces it.** Every `|ΔI|` against a dense solve mixes two things: whether the border and
   the multi-level near assembly are the dense fill's arithmetic, and how good the projection is on
   that mesh. Widening `NearRadiusFactor` until every pair is in the near set removes the second
   entirely, and the bordered operator then has to reproduce `FillMultiLevel`'s matrix to round-off.
   It does: **1.1e-15** entry-wise, **4e-11** in the solved current, on the MMIC two-level fixture
   with its via and on the FR-4 hero with a backside ground via. That is the claim worth making, and
   it needs no tolerance.

2. **The brief's "8.7e-7 at the shipped defaults" is not transferable, and quoting it at a different
   fixture would have graded P12 on somebody else's mesh.** It is `AimAccuracyTests`' figure for the
   32 mm FR-4 hero at the SHIPPING mesh. Measured on a cells/λ = 80, un-edge-meshed two-level rung
   the bordered operator reads 3.3e-6 — and the **single-level control**, the same mesh character
   with no via in it at all, going through the shipped `PlanarAimOperator` against the shipped
   single-level dense fill, reads **2.7e-5** where the shipping mesh reads 4.9e-7. **55× of spread
   from the mesh alone, on a path P12 does not touch.** On the shipping mesh the ground-via fixture
   reads **3.97e-7**, inside the brief's number. Every accuracy figure in `HISTORY.md` §P12 is
   therefore printed with its mesh, and the control table is printed beside it.

3. **`N_z ≪ N_h` keeps the border cheap in MEMORY unconditionally and cheap in TIME only
   conditionally, and the two must not be stated together.** `Z_hz` is `16·N_h·N_z` bytes — 0.5 MB at
   N = 15,192 — and stays negligible however the fixture grows. Its BUILD is `N_h × N_z` graded 4-D
   quadratures, and at a fixed via count that is flat in N (0.43 → 0.56 s across an 8.8× N ladder).
   Grow the via footprint WITH the part and `N_z` reaches 140, at which point the border is **28.6 s
   of a 34 s point** while the accelerated near fill is 0.62 s. The dense path pays the same
   `N_h × N_z` mixed entries, so this is not a regression against it — but it is what a claim about
   a via FIELD would have to be measured on, and the brief's "later brief with its own measurement"
   is the right disposition.

4. **`N_hh⁻¹ Z_hz` must not be stored, and that decides the preconditioner's shape.** §11's flat
   iteration count is a finding about the horizontal operator; a preconditioner with nothing at all
   in its `N_z` rows would be steering GMRES past a short circuit, so the border is folded in
   EXACTLY, by block elimination on an `N_z × N_z` Schur complement `S = Z_zz − Z_zh N_hh⁻¹ Z_hz`.
   The obvious implementation keeps `W = N_hh⁻¹ Z_hz` and applies the inverse with one sparse solve;
   `W` is 38 MB at N_h = 12,000, N_z = 200, on a working set whose whole point is to be small. So `S`
   is built one column at a time and discarded, and the apply pays a SECOND sparse substitution
   instead — a fraction of a percent of an iteration that already runs three FFTs over the padded
   grid. Measured: GMRES 4 → 6 iterations from N = 1,728 to 15,192.

5. **A (level, level) pairing can legitimately have no vector kernel at all, and the null must be
   handled rather than asserted away.** `MultiLevelPairings.Resolve` fills `TermsA[la, lb]` only when
   some direction has bases on BOTH levels, so a level carrying only x̂ rooftops paired with one
   carrying only ŷ has a scalar kernel and no vector one. D5 makes the vector block identically zero
   there, so the entry is the scalar half alone — which is exactly what `PlanarEntryFill.At` would
   have returned had it existed for that pairing. The bordered operator computes it directly rather
   than dereferencing a null it "cannot" reach.

## The ceiling — the brief's own hand-back, written out and NOT applied

`SurfaceMesher.AcceleratedUnknownCeiling` = 12,000 is still **single-level only**. The measurement
that would justify widening it is made (`HISTORY.md` §P12 Table 4): the length ladder is healthy to
**N = 15,192**, near entries per row 488 → 517, GMRES 4 → 6, residual 3.6e-9, a whole frequency point
~2.5 s and 327 MB against 4,927 MB for a dense point of the same size. Two lines change:

```csharp
// src/Engine/Mom/PlanarSolve.cs — PlanarSolveContext's constructor
-        bool accelerated = Settings.Aim is not null && levels is null;
+        bool accelerated = Settings.Aim is not null;
```

…except that it is not that line any more, and finding out why turned up a live defect. The brief
names `SurfaceMesher.GuardCeiling`'s `accelerated` argument as the second place; its three callers
(`PlanarKernel.Solve`, `PlanarKernel.SolveWithReport`, and `EmSetupEditorViewModel`'s pre-solve mesh)
all passed `Aim is not null` with **no level condition at all**, while `PlanarSolveContext`'s
constructor asked `Aim is not null && levels is null`. **So the report a user reads before pressing
Simulate has been judging a via-bearing accelerated mesh against 12,000, and the run then refused it
at 5,000** — quoting the dense ceiling, which is neither the number the report used nor a remedy.
Reachable before P12 and reachable after it; invisible only because the accelerator refused a via
mesh outright a few lines later and THAT was the message the user saw.

The two are now one function, and the owner's decision is one line in one place:

```csharp
// src/Engine/Mom/SurfaceMesher.cs
    public static bool UsesAcceleratedCeiling(bool aimOn, bool multiLevel) => aimOn && !multiLevel;
//                                                                        => aimOn;   ← the flip
```

`PlanarSolveContext`, `PlanarKernel`'s two mesh calls and the EM panel all read it. **Making them
agree does not pre-empt the decision** — it keeps today's shipped behaviour (a via mesh judged at
5,000) exactly as it is, and removes a report that contradicts the run.

**A fifth site was found by asking the same question of the whole file rather than of the four the
defect named**: de-embedding's setup-time refusal in `PlanarKernel.Solve` sized a port's calibration
STANDARDS against `fillSt.Aim is not null && !general` — the same decision, spelled out a second
time. It agrees with the function today, which is why nothing was visibly wrong with it, and it is
latent rather than reachable (a via mesh between the two ceilings is refused by the mesh verdict
before de-embedding is reached at all). It is now the function call too, for the reason the function
exists: the flip above must not leave a standard judged against a ceiling its own DUT is not judged
against. `PlanarDeembed.GuardCapacitanceCeiling` is NOT a sixth — it takes `accelerated` as an
argument stating which ROUTE ran (P11: the accelerated static solve holds no m x m), and its caller
`PlanarDeembed.StaticCapacitance` is inside the `Aim is { }` branch that already settled it.

**The reason it is not taken here is finding 3, not doubt about the ladder.** A ceiling stated in N
alone is a promise about a mesh whose `N_z` is unstated, and the border's time is set by `N_z`. A
board with one ground via at N = 15,192 costs 2.5 s a point; the same N with a 140-basis via field
costs 34 s, of which 28.6 s is the border — legitimate, converging, and not what "12,000 accelerated
unknowns" would lead anyone to expect. Widening the ceiling on a bound in N alone, or on a bound in
N and `N_z` together, is the owner's call.

(P8's own caveat still stands beside this one: N no longer bounds the accelerated working set on a
REFINED mesh, and moving 12,000 for either reason wants a check on near entries per row rather than
a bigger integer.)

## Not done, on purpose

- **The ẑ bases are not projected and the mixed kernel is not projected.** The brief forbids it and
  nothing measured here argues for it: at a real via count the border is 2% of a point.
- **`Z_zh` is not stored.** Z is complex-symmetric bit for bit, so the transpose is read out of
  `Z_hz` rather than held.
- **No mirror index for the near set.** P6's own 18 MB decision is unchanged; the bordered operator
  finds each transpose position by the same binary search.
- **The dense multi-level path is untouched.** `Aim = null` reaches `PlanarSystem.BuildMultiLevel`
  exactly as before, and every published multi-level number stands.

## What was changed in the code

```
src/Engine/Mom/PlanarAimBordered.cs   NEW — PlanarBorderedAimOperator, PlanarBorderedAimReport
src/Engine/Mom/PlanarAim.cs           PlanarAimGeometry gains the horizontal/vertical split and
                                      asserts R-via-5's prefix instead of refusing a via mesh;
                                      PlanarAimOperator now carries the refusal and names the
                                      bordered operator
src/Engine/Mom/PlanarFill.cs          MultiLevelPairings + Resolve made internal; SingularPrismPartOf,
                                      MixedEntryOf, CellPairSpanOf wrappers; PlanarPulsePotential
                                      takes an explicit remainder; PlanarEntryFill takes a prebuilt
                                      PlanarPulsePotential
src/Engine/Mom/PlanarSolve.cs         the general-kernel refusal replaced by the bordered operator;
                                      LastBorderedAccelerator; the standards' ceiling decision reads
                                      SurfaceMesher.UsesAcceleratedCeiling instead of restating it
tests/Engine.Tests/Mom/PlanarP12BorderedAimTests.cs   NEW — 3 routine tests (3 s), 5 Benchmark (31 s)
tests/Firewall.Tests/user-facing-text-allowlist.txt   two retired messages out, five new ones in
```

# MIM-3 — thin dielectric layers: the verdict (brief-em-mim-3-thin-layer-gate.md, 2026-08-30)

**The question.** A MIM capacitor puts two meshed conductor levels 0.05–0.5 µm apart.
`LayerStack.CanRepresent` accepts any positive thickness, so nothing refuses the structure — which
is the dangerous configuration: a complete, plausible answer with no evidence behind it. The brief
was measurement-first, and the measurement had to come before any fix.

Every table is in `HISTORY.md` §MIM-3. Run against a post-MIM-6 build, so the plate gap is the
0.2 µm the shipped technology states rather than the 3.2 µm a pre-MIM-6 tree would have measured.

## The verdict, in one paragraph

**The kernel is not the problem and the quadrature is.** `Dcim.FitAtHeights` at height pairs
straddling the capacitor dielectric is *flat in the separation* — worst 4.2e-3 of the free-space
kernel at 0.05 µm against 6.4e-3 at 3 µm, the interconnect spacing L9c already measured — so no
ρ/λ constant moves and none was moved. What degrades is the **cross-level block of the fill**,
which loses four decades between `cell size / level separation` of 1 and 20: 2.3e-7 at 1, 4.1e-3
at 5, 3.9e-2 at 10, 1.5e-1 at 20 and 4.9e-1 at 50, while the same-level block stays at 3e-6. The extracted plate
capacitance follows that ladder rung for rung — within 10% of ε₀εᵣA/d while cell/separation ≤ 5,
1.46× at 12.5, and the **wrong sign** at 25. So the shipped defaults hold over a stated range, the
range is a **mesh** condition rather than a stackup one, and outside it a NOTE reports it:
`PlanarLevels.ValidatedCellOverSeparation = 5.0` and `PlanarSolve.LevelSeparationNotes`.

**The shipped MIM technology sits outside it.** A 10 × 10 µm plate pair 0.2 µm apart meshes at
2.5 µm — cell/separation = 12.5 — so its plate capacitance reads about 1.5× high and the note
fires on it. That is stated rather than fixed: see *Not done, on purpose*.

## Findings

**1. There is no thin-layer KERNEL failure, and the control rungs are what say so.** The brief's
suspicion was §L8c's — DCIM's fitted images stop being smooth on the mesh's own scale once an image
sits closer to the metal plane than a cell is wide. Measured, that does not happen here: the scaled
error is flat from 0.05 µm to 3 µm on all four components and inside L9b's own ≤1.6e-2 envelope
throughout. The εᵣ = 1 control (same geometry, no contrast) is flat too, so the flatness is not two
effects cancelling. **A ladder without a rung in the already-measured regime could not have said
this** — the 2 µm and 3 µm rungs are what turn "0.05 µm gives 4e-3" from a number into a verdict.

**2. The failure is the recorded trap, at a scale nobody had asked it at.** §3.5 already says
*"cross-level entries have no 1/ρ but still carry logarithms"* and that *"different levels ⇒ smooth
⇒ plain quadrature"* is a trap. At d ≪ cell the cross-level kernel carries a peak of width d inside
a cell of width h, and the rule integrates over it. The trap was recorded for surface waves and the
mixed kernel's 1/k_ρ² tail; the plate regime is the same trap with the peak made arbitrarily narrow
by the stackup.

**2b. And the coarsest rungs are stated for what they are.** At cell/separation 20 and 50 the
forced-high reference is itself only 9× and 5.7× better than the shipped rule (against 150× at 5
and seven decades at 1) — the same narrow peak is starting to defeat the expensive rule too. Those
two rungs support "the error is large and grows" and not their own third significant figure.
**Nothing in the verdict rests on them**: the range is drawn at 5, where the reference has 150× of
headroom, and the 25/50 rungs of the physics ladder need no precision to say "wrong sign".

**3. Nothing downstream can see it.** Reciprocity holds to 1e-19 and passivity to 1e-5 at every
rung, *including the ones whose extracted capacitance has the wrong sign*. §10.9's oracle-free
self-consistency set is structurally blind to this: the matrix is still complex-symmetric and the
structure still does not create energy — what is wrong is a magnitude inside it. This is §L8c's
converged-looking-but-wrong mode, one tier down in z, and it is the whole reason the brief was a
measurement rather than a test.

**4. RAW S CANNOT CARRY A CAPACITANCE IN THIS ENGINE, and this is now measured on a known-good
line.** Two instruments were built and discarded before the third: a one-port shunt cap reading
`Im(Y₁₁)/ω` (negative at d ≤ 0.1 µm) and a series two-port reading `−Im(Y₂₁)/ω` (0.50 → 0.18 fF
over a 40× change in d where ε₀εᵣA/d spans 120 → 3 fF). The control that settled it is one line: a
70 µm GaAs microstrip, 800 µm long at 10 GHz — a matched ~50 Ω line — reads **|S₂₁| = 0.0706** raw.
The port is a series delta gap with `a₂₁ ∝ ω` (§5), so a raw reading is the port and not the
structure, at any topology. **The series topology's usual argument does not save it**: "Y₂₁ isolates
the through element because port discontinuities are shunt" is true of an ideal error box and
useless against an `a₂₁` two orders below unity.

> **This independently reproduces MIM-2's finding (b) — and it confirms MIM-2's RETRACTION of it,
> not the finding.** The series brief's rule (*never read a small element's value off raw S*) is
> stronger than it looked: it is not only that a ~0.3 fF discontinuity masks a small element, it is
> that a raw reading of ANY reactive element in this engine is dominated by the port. Correcting the
> instrument moved a 0.31 fF answer to 44 fF against a 30 fF closed form.

**5. De-embedded, the plate capacitance is there and is right where the mesh resolves the gap.**
With the stack truncated at the upper plate (so `LevelIsOnSlabTop` is satisfied) and the same
structure minus its lower plate subtracted as a baseline, `(C − baseline)/(ε₀εᵣA/d)` is
0.89 / 0.99 / 1.10 at cell/separation 1.25 / 2.5 / 5. The baseline itself — feed, port, plate to
ground — is 4.93 fF and frequency-stable to 8% from 20 to 140 GHz, while the plate pair is not, and
destabilises in the same order the fill ladder degrades.

**6. Kernel A settles the closed form.** On the equivalent cross-section it reproduces ε₀εᵣW/d to
**1.007 at d = 0.05 µm**, rising monotonically to 1.16 at 2 µm exactly as a fringing correction
should, and is mesh-converged to five digits. It shares no code with kernel B. So a 0.05 µm gap of
εᵣ 6.8 is not intrinsically hard, and there is no argument that the closed form is the thing in
error. The brief called this a free second opinion; it was the load-bearing one.

**7. An observation that is NOT a MIM-3 finding.** The mixed component at a same-level pairing fits
with zero images and an infinite residual, and its strict relative error is 2.07 at every rung —
*d-independent to four figures*, and worse (1.58e+1) on the εᵣ = 1 control. It is a property of that
pairing, not of thin layers, its scaled error is ≤ 7.5e-4 throughout, and the fill only asks the
mixed kernel about a via against a horizontal basis. Recorded so it is not re-found as a MIM result.

## What was built

- **`PlanarLevels.ValidatedCellOverSeparation = 5.0`** — 5 is where the two independent ladders
  agree (the fill's ≤ 4.1e-3 and the extracted capacitance's ≤ 10%), not a round number chosen
  first. Its doc comment carries both ladders.
- **`PlanarSolve.LevelSeparationNotes`** — a NOTE, never a refusal, for R-prt-13's reason: the
  answer is still produced, still reciprocal, still passive, and what is unreliable is a magnitude.
  A refusal would also take away every multi-level run whose ratio is fine. It is asked **per
  adjacent level pair, over the cells on those two levels only** (R-zz-1's discipline — a per-mesh
  question would grade a plate pair on some unrelated conductor's cell), and it **names the binding
  quantity**: the transverse pitch is `width / MinCellsAcrossConductor` and does not respond to λ,
  so it says outright that Cells per wavelength and Mesh frequency change nothing here rather than
  offering two inert knobs (§3.5's trap, and the reason `BuildRefusal` asks `waveBinds`). It also
  scopes the damage — single-level results are unaffected.
- **`MimThinLayerTests`** — 6 routine tests, ~1 s total: the cross-level block on a fixed input
  against literals (P3/P4's pattern), plus the note's firing threshold, its wording, its per-pair
  scoping, its silence on a single-level problem, and its wiring through `VerticalRangeVerdict`.
  `T7` carries the accuracy statement and is `Category=Benchmark` (1 m 5 s in Debug).

## Not done, on purpose

- **The quadrature was NOT changed.** The brief allowed "a bounded quadrature/fit change with
  milestones 1–3 rerun after it" and it is not bounded: the cross-level near rule would need the
  same singularity-extraction treatment the same-level one has (§3.3's three singular pieces), for
  a peak whose width is a stackup parameter rather than a mesh one, and then all three ladders plus
  every bit-identity digest in `PlanarP3/P4/P5` re-pinned. The brief's own instruction applies — *if
  the fix is not cheap, stop and report; the ladder itself is this brief's deliverable.*
- **No existing gate was loosened, and none needed to be.** `AimAccuracyTests`' 8.7e-7 and the L9
  gates are untouched, and so are `Dcim.ValidatedRhoOverLambdaAtHeights`, `…Layered` and
  `…InteriorHorizontal` — Table 1 says they are right where they are.
- **The shipped MIM technology was not re-dimensioned to pass its own note.** Widening the plates
  or thickening the dielectric would move cell/separation under 5 and silence the note, and it would
  be tuning the exemplar to the tool. The plate gap is what the process states.
- **The mesher was not taught to refine across a thin gap.** That is the actual remedy — a
  transverse pitch derived from the nearest level separation as well as from the conductor width —
  and it is a `SurfaceMesher` change with its own unknown-count consequences (a 10 µm plate at
  cell/separation = 5 needs 0.4 µm cells, i.e. 25 across instead of 4, and the shared tensor grid
  spreads that across the whole domain). Named here, not built; the note names it as the binding
  quantity so a user is not left guessing.
- **A de-embedded reading on the shipped stack is still MIM-4's.** Milestone 3 had to truncate the
  stack at the upper plate to get a de-embedded port at all; on the real MIM stack both plate levels
  are buried and `LevelIsOnSlabTop` refuses. That is §7's "all of Part B", unchanged.

---

# MIM-4 — the interior-height static Green's function (brief-em-mim-4-interior-static-greens.md, 2026-08-30)

§7's "all of Part B", built. Three refusals fenced the de-embedding path in, and every realistic MIM
run — whose feed arrives on upper metal — hit one of them: `PlanarSolve`'s throw for a de-embeddable
edge port off the slab top, `PlanarExtractor`'s refusal of more than one dielectric under the lowest
level, and `LayeredStaticGreens` / `DcimModel.Evaluate` / `SommerfeldIntegral.EvaluateLayered`
refusing interior sources. **The first two are retired; the third is narrowed to what is still true
of those three classes.**

## The verdict, in one paragraph

At ω = 0 the layered problem is a **Sturm-Liouville problem in z**, not a reflection referenced to a
region, and that reframing is the whole of milestone 1:
`d/dz(P dG̃/dz) − k²P G̃ = −2k·δ(z−z′)` with `P = ε*` (scalar) or `1/µ` (vector), solved as
`G̃ = −2k·ψ↓(z_<)ψ↑(z_>)/W`. Source and observer may sit anywhere. The spatial inverse is a
**two-level Prony image fit** — `∫e^{−bk}J₀(kρ)dk = 1/√(ρ²+b²)`, so a fit of the spectral remainder
IS an image expansion — with the sum rule imposed exactly. It reproduces the shipped one-slab series
to 1e-10 out to ρ = h, agrees with direct Hankel integration to 1e-8 at interior heights on a
0.2 µm-over-100 µm thin-film stack, and evaluates in **0.34 µs per ρ against the shipped image
series' own 2.5 µs**. `C_pul` through it reproduces the shipped `C_pul` to **3.5e-12** where both
apply, and a uniform line on a buried level de-embeds to a matched section. The shipped on-slab-top
path is untouched.

## Findings

**1. The inter-region scale factor has an exact cancellation, and without it the formulation is
numerically dead at small k.** Matching ψ↓ across interface *i* divides by `1 + Γ↓_{i−1}τ_i²`, which
is ψ↓'s own value at the top of a layer — and over a PEC floor that vanishes as k → 0 (Γ↓(0) = −1
through any stack, so it is `1 − e^{−2kd}`), while the numerator vanishes with it. 0/0 at k = 0
exactly, small/small beside it, with relative error the reciprocal of the layer's electrical
thickness. Substituting the reflection recursion collapses it analytically:

```
    1 + Γ↓_i = (1 + r_i)(1 + Γ↓_{i−1}τ_i²) / (1 + r_i Γ↓_{i−1}τ_i²)
```

so the vanishing factor cancels and what is left is `τ_{i+1}(1+r_i)/(1 + r_iΓ↓_{i−1}τ_i²)`, whose
denominator is bounded below by `1 − |r_i| > 0` for every passive stack. Gated at k·H = 1e-9 on a
cross-region pair, to 1e-15 absolute.

**2. Every exponential is written as a decay over its own region, and that matters more here than in
the full-wave kernel.** `e^{−k(z_hi−z)}` and `e^{−k(z−z_lo)}`, never `e^{+kz}`. The static integrand
is killed by nothing but those exponentials, so k runs out to tens of reciprocal layer thicknesses on
a thin-film stack — 1e8/m on the shipped MIM stackup — and the obvious form
`ψ↓ψ↑/(Pτ(1−Γ↓Γ↑τ²))` overflows. The same-region product is expanded so the layer's own τ cancels
against the Wronskian's algebraically rather than by division; the four terms that come out are the
four classical distances (direct, floor image, ceiling image, one round trip), each with a
non-negative exponent by construction.

**3. THE FIT'S SPECTRAL RESIDUAL DOES NOT BOUND ITS SPATIAL FAR FIELD, and the sum rule is only half
of why.** Measured, not assumed. With an unconstrained least squares the spectrum was exact to
**2.4e-15** and the spatial function was **8.5e-6** wrong at ρ = 1000 h. Two separate causes, found
in order:

- The 1/ρ tail's coefficient is `c_∞ + Σa`, which over a ground plane is exactly **zero** — a
  grounded structure's potential falls as a dipole. An unconstrained fit gets that cancellation only
  to its own residual. It is now imposed exactly, by eliminating one column
  (`a_p = rule − Σ_{j≠p}a_j`, the deepest image, so `φ_j − φ_p` is `φ_j` almost everywhere and the
  conditioning is the unconstrained problem's). Verified to 1e-16 — **and it did not move the far
  field at all**, which is what pointed at the second cause.
- The far field is set by the **second moment `Σ a b²`**, and the images cancel by a factor of ~45
  there. That is a k-derivative-like quantity at k = 0, and both Prony grids are uniform, so their
  first step IS their resolution near k = 0: the spatial far field at ρ is governed by k ≈ 1/ρ, which
  for any ρ past a few stack heights falls INSIDE that first step. A third sample block — geometric,
  40 points over five decades, added to the AMPLITUDE least squares only (Prony needs uniform
  samples, so it never sees it) — cut the far-field error **10–60×**, to 5e-7…7e-6.

What is left is a fractional error in that 45× cancellation, and it is negligible in the only terms
that matter: at ρ = 1000 h the kernel has fallen 1.6e9 from its ρ = h value, so 1e-6 of it is
**4e-14 of the near-field scale that sets a capacitance**. Constraining the second moment as well as
the sum is the named remedy if it ever binds; it needs `R″(0)` to more digits than a difference
quotient gives, and it is not built.

**4. Fit versus quadrature: quadrature is the loser, by four to six orders of magnitude, and it stays
as the oracle.** The brief asked for the decision to be measured. Per ρ: the image model costs
**0.34 µs** (9–27 images), `PlanarKernelTerms.StaticScalar`'s own shipped series costs **2.5 µs**
(130 images on GaAs), and `InteriorStaticGreens.PotentialByQuadrature` costs **1–270 ms**, rising
with ρ because the J₀ partition does. The fit costs 16–78 ms **once** per level. That is not close,
and the fill's radial table wants ~10⁴–10⁶ samples. The reference integrator is kept because it
shares no approximation with the model — its own partition is geometric as well as at the Bessel
zeros, because the integrand carries two length scales that differ by three orders of magnitude on a
thin-film stack and a uniform partition sized for the finer one costs a quarter of a million panels —
and it **refuses by name** rather than grinding when the partition it would need is absurd.

**5. The fit recovers the exact physical images where they exist, which is the strongest single
check that it is an image expansion and not a curve fit.** On the FR-4 slab the fitted depths come
back at b/2h = 1.000, 2.000, 3.006, … with amplitudes matching `−(1+K)(1−K)K^{n−1}` (−0.6035, 0.3800,
−0.2466 against −0.6036, 0.3800, −0.2393); the tail of the 130-term series is carried by a handful of
complex images. Nobody told it about `2nh`.

**6. THE C_pul ROUTE MUST BE CHOSEN BY COMPARING THE MEDIUM, NOT THE LEVEL'S HEIGHT — and the case
that proves it is the one this brief created.** A single level over a STRATIFIED sub-feed region sits
at the top of its medium AND at `slab.HeightM`, because the extractor's sizing slab is built from
that same distance. A height test would answer "on the slab top" there and put a two-dielectric
board's published reference impedance on a one-dielectric image series — plausibly and wrongly, which
is the exact failure the retired refusal existed to prevent. `PlanarPortCalibrator.DescribedByTheSlab`
compares structurally instead: one layer, of the slab's own material and height, PEC below,
half-space above, level on its top surface. `PlanarProblem.LevelIsOnSlabTop` still says something
true and now gates nothing.

**6b. That test also changes an answer the old code got wrong, and the change is deliberate and
named.** A MULTI-LEVEL problem whose port sits on the slab top but has a dielectric ABOVE that level
— an encapsulation, an interlayer — used to pass `LevelIsOnSlabTop` and take the one-slab image
series, which ignores everything above the metal. It now takes the interior route in the real stack.
Measured on the MMIC two-level fixture (100 µm GaAs + 3 µm εᵣ 2.7): the M1 port's Z_c is 43.34 Ω
where the one-slab series had it elsewhere, and the M2 port's is 48.78 Ω. **This is the one place
this brief perturbs a previously-passing de-embedded result**, and it is a correction rather than a
regression: the old value was the electrostatics of a bare slab in air, which that structure is not.
No existing gate moved — the whole `Category=Benchmark` multi-level and MIM tier passes — and the
alternative (keeping the height test so the wrong answer stays bit-identical) was rejected because
finding 6 shows the height test is not a correct rule in the first place.

**7. A stratified medium turns the general kernel on at ONE level too.** Before this brief that case
could not arise — a stratified region under the lowest level was refused at extraction, and with one
level there is nothing above it — so `PlanarExtractor` attached an explicit `MediumStack` only for a
multi-level problem. Carrying the layers without that change would have handed L8's one-slab kernel a
stack it does not describe. `generalMedium = levels.Count > 1 || mediumStack.LayerCount > 1`.

**8. The extractor's `GroundedSlab` is now purely a SIZING object where the region is stratified, and
the right average for that job is the series-capacitance equivalent.** It still sets the calibration
standards' geometry, the branch-continuation β seed, the accelerated near-radius floor and the mesh —
none of which is the published reference impedance any more. `h/ε_eff = Σ d_i/ε_i` is the exact
electrostatic equivalent of layers in series, reduces to the single layer's own εᵣ bit for bit, and
is what a wide line over the real stack converges to: **21.3% / 10.3% / 3.2% / 1.1%** difference from
the true stratified `C_pul` at W/h = 0.5 / 2 / 8 / 24. The note says out loud that the number is for
sizing and never for the reference impedance.

**9. Comparing two independently calibrated runs is not a de-embedding gate, and the first version of
G2 was one.** A₂₁'s SIGN is a continuation carried from the previous frequency; at a single frequency
there is nothing to continue from, so two `PlanarSolve.Run`s of the same cross-section landed on
opposite signs and their S₂₁ differed by exactly π (expected `<0.902, −0.425>`, got
`<−0.877, 0.461>`, magnitudes agreeing to 0.6%). Their Z_c also differed by 5.6%, because a standard
reproduces its DUT's own longitudinal gridlines (D4) and the two DUTs are different lengths. Neither
is a fact about the interior electrostatics. The cascade identity is gated the way `T4_2` gates it on
the slab top: ONE calibration, applied to both lines.

## What was built

| Where | What |
|---|---|
| `Mom/InteriorStaticGreens.cs` | **New.** The ω = 0 spectral kernel at arbitrary (z, z′) for a general `LayerStack` — the cascade (`Build`), `Spectral`, `AsymptoticConstant` (the exact 1/ρ coefficient), `SnapToInterface`, `CanEvaluateAt` (the one refusal left: a point inside a solid wall), and `PotentialByQuadrature`, the reference inverse transform |
| `Mom/InteriorStaticImages.cs` | **New.** `InteriorStaticModel` (an exact 1/ρ term plus complex images, `Evaluate`, `SmoothAtZero`, `Residual`) and the two-level Prony fit with the constrained amplitude solve and the low-k block |
| `Mom/SingularExtraction.cs` | `PlanarKernelTerms.StaticScalarAt` — `StaticScalar`'s shape for any level at any height. `StaticScalar` untouched |
| `Mom/PlanarDeembed.cs` | `CapacitancePerMetre`'s `LayerStack` + `levelZ` overload. The `GroundedSlab` overload is byte-identical |
| `Mom/PlanarSolve.cs` | The buried-level throw **retired**; `PlanarPortCalibrator` takes `mediumStack`, chooses its C_pul route via `DescribedByTheSlab`, and reports `InteriorFitResidual`. `InteriorCPulResidualCeiling = 1e-6` is what is refused in its place |
| `Design/Layout/Em/PlanarExtractor.cs` | The stratified-sub-feed refusal **retired** — the layers are carried, the sizing slab is the series equivalent, and a note says which is which. `generalMedium` attaches the stack at one level too |
| `Mom/LayeredMedium.cs`, `Dcim.cs`, `SommerfeldIntegral.cs`, `PlanarStaticAim.cs`, `PlanarProblem.cs` | Refusal wording swept: each now names the object that answers, and `DcimModel.Evaluate`'s stale "no static Green's function at interior heights in this repository" clause is gone |
| `tests/Engine.Tests/Mom/InteriorStaticSpectralTests.cs` | **New**, 13 routine tests: symmetry, flux continuity across every interface, the wall refusal, the exact reductions, split invariance at 1e-14, `1 + Γ_e`, agreement with `LayeredStaticGreens`, and an INDEPENDENT finite-volume solve of the same ODE gated on Richardson behaviour as well as on error |
| `tests/Engine.Tests/Mom/InteriorStaticImageTests.cs` | **New**, 14 routine tests: the shipped series, the sum rule, `LayeredStaticGreens`, direct Hankel integration at interior heights and on the thin-film stack, the vector reduction, the extraction constants, and the integrator's own refusal |
| `tests/Engine.Tests/Mom/InteriorCPulTests.cs` | **New**, 6 routine tests: the two kernels on the one problem where both apply, split invariance, the series-capacitance limit (both halves), the route selection, and every stack's fit residual against the ceiling |
| `tests/Engine.Tests/Mom/InteriorDeembedGateTests.cs` | **New**, 2 `Category=Benchmark` (45 s together): a uniform line on a buried level de-embeds to a matched section, and the cascade identity there |
| `tests/Ui.Tests/Em/StratifiedSubFeedExtractionTests.cs` | **New**, 4 routine tests: the stratified region extracts, both layers reach the medium, the sizing slab is the series equivalent and the medium is not, the note replaces the refusal, and one dielectric is unchanged |
| `tests/Engine.Tests/Mom/MultiLevelPortTests.cs` | `M3_2` inverted rather than deleted — the port that was refused now de-embeds, and M1 and M2 must not come back with the same Z_c |

## Not done, on purpose

- **The second-moment constraint** (finding 3). Named, measured, not built: the absolute error it
  would remove is 4e-14 of the near-field scale.
- **A static kernel for a CROSS-LEVEL cell pair.** `PlanarStaticAim.Build` and
  `PlanarDeembed.StaticCapacitance` take ONE kernel at ONE height pair; a multi-level static solve
  needs a per-pairing SET, the way `PlanarKernelSet` carries the full-wave one. A calibration standard
  is always single-level (D3), so nothing reaches it — the refusal is narrowed to say that instead of
  "this repository does not have it".
- **`LayeredStaticGreens` still refuses interior heights**, and correctly: its own partition (two
  closed terms, and a decay length its quadrature is sized from) is referenced to the top half-space.
  It is an oracle, `InteriorStaticGreens` reproduces it wherever both are defined, and rewriting it
  would delete the independent check.
- **A de-embedded reading of the shipped MIM capacitor's own capacitance.** MIM-4 removes the port
  refusal that blocked it; what still binds is MIM-3's MESH condition, and the shipped technology's
  default mesh sits outside it (`ValidatedCellOverSeparation`). That is gap 3's business, reported as
  a note, and no constant was moved here.

## The mesh frequency looked inert — because on that geometry it IS, and the report said otherwise (2026-09-09)

**Reported symptom.** Cells per wavelength held constant, Mesh frequency lowered, Mesh pressed each
time: the rendered mesh never changed and the panel reported 1,997 cells at every setting.

**Diagnosis — the plumbing is fine and the mesh is correct.** Measured on the reported `.cem` (one
polygon, FR-4, εᵣ 4.4, sweep 0.5–10 GHz, cells/λ = 5), driving `SurfaceMesher.Mesh` directly at 500
MHz, 1, 2, 5, 10 and 20 GHz: **1,997 cells and 3,835 unknowns at every one of them.** The cell size
is `Math.Min(hWave, narrow / MinCellsAcrossConductor)`, the narrowest conductor run is 360 µm, so the
transverse pitch is 90 µm — while the λ_g/5 cap at 10 GHz is 2.858 mm, **31.8× coarser**. The cap
never enters the minimum, and the mesh frequency (and cells per wavelength with it) cannot move it.
This is R-msh-4 working as designed.

**The actual defect is the reporting, and it is the same one twice.**

1. **The λ_g note claimed a cap that did not bind.** It read "Cell size capped at λ_g/5 = 2.858 mm …
   Changing it changes the unknown count" unconditionally — false on both halves here: nothing was
   capped at 2.858 mm (the largest cell built is 185 µm), and changing the frequency changes the
   count by zero. `BuildRefusal` had already been rewritten for exactly this, on exactly this
   geometry class (2026-08-14, the Klopfenstein taper) — but a refusal only fires past the unknown
   ceiling, and this mesh is 3,835 unknowns and *solves*. So the mesh that runs was still being told
   the inert story. The note now asks `capBinds` — `BuildRefusal`'s own question — and where the cap
   does not bind it says so, quotes the narrowest run and the pitch it forces, and names the knobs
   that are inert rather than the ones that are not.

2. **`PlanarMeshSummary` divided λ_g by the CAP.** `λ_g/{GuidedWavelengthM / MaxCellSizeM}` is
   `CellsPerWavelength` by construction, so the panel printed "max cell 185 µm (λ_g/5 at 10 GHz)" —
   an honest max cell beside a ratio describing a different length. It now divides by
   `MaxCellEdgeM`, the cell that was actually built (λ_g/77 here). Where the cap binds the two agree
   and nothing changes.

**The user's real question was "then how DO I coarsen it", and there is an answer.** Measured on
the same file:

| setting | cells | unknowns | verdict |
|---|---|---|---|
| as set in the `.cem` | 1,997 | 3,835 | Warn |
| **edge mesh off** | **1,171** | **2,215** | **Ok** |
| staircase instead of conformal | 1,988 | 3,838 | Warn |
| edge mesh off + staircase | 1,161 | 2,214 | Ok |

**The edge mesh is the one mesh control still live on a geometry-bound mesh — 41% of the cells and
42% of the unknowns, and it takes the budget verdict from Warn to Ok.** So the non-binding note names
it (when it is on) rather than sending the user straight to "redraw your artwork": the first version
of this note listed only the geometry remedies, which is accurate and useless. `BuildRefusal` had
always listed it; the note had not, because the note had never distinguished the two cases at all.

For scale, the part is 6.07 × 1.96 mm with a 360 µm narrowest run — **0.042 λ_g across at the 1 GHz
mesh frequency**, 0.42 λ_g at the sweep's 10 GHz top. An electrically small structure is resolved by
its geometry, never by λ, which is why the note now states the λ_g count outright.

**`MinCellsAcrossConductor` was measured as a hypothetical control and is NOT worth exposing.**
Temporarily rebuilt at 2, 3 and 4 on this file: 1,374 / 1,141 / 1,997 cells. Note it is **not
monotonic** — 2 costs more than 3, because the grid-line placement interacts with the edge grading —
so a user turning it down would sometimes get a denser mesh, which is exactly the class of confusion
this whole entry is about. The best case is ~1.75×, against the edge mesh's 1.7× from a control that
already exists and behaves monotonically. D3's "exactly three controls" stands.

**Left alone, deliberately.** The "Narrowest conductor dimension 360 µm, meshed 1 cell(s) across
(target 4)" note pairs the measured narrowness with `MinCellsAcrossRun`, which is the minimum run
over the *whole* artwork — on conformal or staircased curved metal a run of 1 comes from a boundary
tip, not from the narrowest conductor. `PlanarMeshReport`'s own parameter doc already calls that out
as honest information rather than a defect, so it is recorded here and not changed.

**Gates.** `MeshFrequencyTests.WhereTheCapDoesNotBind_TheNoteSaysSo_AndNamesTheKnobsAsInert`
reproduces the complaint (identical N at 1 and 10 GHz on a 360 µm line) and pins both wordings;
`PlanarMeshOverlayTests.Panel_MeshBuildsTheSurfaceMesh_…` pins the summary's ratio to
`MaxCellEdgeM`.


## Cells across the conductor becomes a user control (2026-09-09)

Same owner report as the entry above, one step on: having been told the wavelength knobs were inert,
the answer to "then how do I get a coarser mesh" was for a long time "you cannot". The transverse
pitch is `min(λ_g/CellsPerWavelength, narrowest/MinCellsAcrossConductor)` and the second term was a
compile-time constant of 4.

**Owner's decision: mesh density is the user's responsibility. A tool that will not give a bad mesh
when one is asked for is deciding on the user's behalf. Warnings are allowed; refusals are not.**
So `PlanarMeshSettings.MinCellsAcrossConductor` is an ordinary setting (default 4, `.cem` under the
omit-at-default rule, a panel field, and a term in `EmSnpProvenance.MeshHash` appended only when off
its default so no existing `.snp` reads as stale). 1 is reachable. Nothing clamps above 1 and nothing
refuses.

**What was measured first, and what it changed.** On a 200 µm × 10 mm FR-4 trace, de-embedded, S21
moves under 0.001 dB from 1 cell across to 8 — **accuracy is not what 4 was buying**. But the cell
count is **not monotonic** in it: 8 → 220 cells, 6 → 200, 4 → 180, 3 → 160, 2 → 180, 1 → 200.

**The cause was mis-diagnosed first, and the correction matters.** The original write-up blamed the
edge fan legitimately spending back what the bulk saved. Challenged and refuted: re-measured per axis
on one rectangle, the y-steps at 1 across read `5.94, 17.87, 53.61, 87.13, 5.94, 5.94, 5.94, 5.94,
5.94, 5.94` — the fan grades correctly away from the NEAR edge and **collapses into six uniform
finest-size cells approaching the FAR edge**. That is the `BuildGridLines` marcher defect recorded in
`brief-em-transmission-line-mesh.md` §0d (the half-step look-ahead is clamped to the interval end,
where the size field is `c0`), and coarsening the bulk pitch lengthens the collapsed run. With a
Lipschitz-consistent marcher the same ladder is **monotonic and cheaper at every rung**: 264 / 220 /
198 / 176 / 154 / 154. A straight line DOES coarsen monotonically once the marcher is right.

**Fixture trap found while re-measuring:** `PlanarMeshSettings.Default with { EdgeMesh = false }` is
inert — `Default` has `Auto = true`, and `Resolved` collapses Auto to the default edge mesh. Any
fixture varying the edge mesh must set `Auto: false`. `MinCellsAcrossConductor` survives Auto by
design, so it takes effect either way; a real `.cem` carries `Auto: false` and is unaffected.

That did not stop it shipping; it decided what the mesher REPORTS. Off its default the report always
states the pitch the setting produced; lowered with the edge mesh on it says the count may not have
fallen, names the grading defect as the reason, and names the edge mesh as what removes the fan; at 1 it states plainly that a rooftop spans a cell PAIR, so a
one-cell-wide conductor carries no transverse basis at all and the edge singularity is unresolved —
**reported, not refused**, because a line whose current is uniform across it is a legitimate thing to
ask for.

**Together with the edge mesh it is decisive.** On the reported connector cutout:

| across | edge mesh on | edge mesh off |
|---|---|---|
| 4 (default) | 1,997 cells / 3,835 N / Warn | 1,171 / 2,215 / Ok |
| 3 | 1,141 / 2,157 | 651 / 1,211 |
| 2 | 1,374 / 2,608 | 341 / 614 |
| **1** | 1,385 / 2,621 | **113 / 182** |

1,997 → 113 cells, 17.7×.

**A trap for anyone writing a test here:** do NOT assert that lowering it lowers the cell count. The
first version of the engine test did, and failed — with the edge mesh on, this fixture goes 10 grid
lines to 11 when the pitch is coarsened 4×. Assert the PITCH (or run with the edge mesh off, where
the pitch governs directly).

**It survives `Auto`**, unlike cells/λ and edge cells, and the argument is the owner's rather than
the taxonomy's: it IS a resolution, so the taxonomy would have Auto reset it, but a number the user
typed being discarded because a checkbox is ticked is the silently-ignored-setting failure the
`BoundaryCells` and `MeshFrequencyHz` paragraphs in that file already exist to prevent.

**D3's "exactly three controls" is now six**, and `PlanarMeshSettings`' class comment says so
explicitly rather than continuing to claim three. D3's REASONING still governs what may be added: a
control earns its place by being a modelling or responsibility decision that is the user's to make.

**Still open, deliberately:** whether the DEFAULT moves from 4 to 3 (cheapest rung on both fixtures,
inside §10.5's own 3–5 range) — that moves every recorded number and is a separate act. Written up as
M1 of `docs/sonnet-briefs/brief-em-transmission-line-mesh.md`, which also carries the two other
findings from this investigation: the mesh is **already anisotropic** (60:1 on a 200 µm × 50 mm
trace, so "rectangular cells" is not a new capability — DIRECTION is what is missing), and a real
grading defect in `BuildGridLines` (**110 consecutive 3 µm cells** at the far end of a 50 mm trace,
because the marcher's half-step look-ahead is clamped to the interval end where the size field is
`c0`). A Lipschitz-consistent marcher fixes it — 184 → 79 grid lines — but it moves de-embedded S21
by 0.063 dB, so it needs a convergence study before it can ship. Both are in that brief.

**Gates.** `MeshFrequencyTests.CellsAcrossTheConductor_SetsTheTransversePitch_AndIsNeverClampedOrRefused`
and `MeshFrequencyUiTests.MinCellsAcross_RoundTrips_OmitsAtItsDefault_AndMovesTheStalenessHash`.


## M0 — the grading marcher crawled into the far end of every interval (2026-09-09)

`brief-em-transmission-line-mesh.md` M0. The biggest cell-count finding of that whole investigation,
and it is a defect rather than a control: **a graded fan silently stopped grading whenever it
APPROACHED an attractor, and became a uniform run at the finest cell size instead.**

### The mechanism

`BuildGridLines` graded through `BoundaryMesher.PartitionFractions`, whose step is a half-step
look-ahead **clamped to the end of the interval**:

```
s = min( h(x), h(min(length, x + s/2)) )
```

The far end of an interval is very often an attractor — a conductor edge is both a hard gridline and
an edge-mesh attractor — and the size field AT an attractor is `c0` by definition. So the first step
whose look-ahead reaches the end reads `c0`, and so does every step after it. With the derived growth
ratio at its 3.0 ceiling (r − 1 = 2) the clamp fires the moment the remaining distance falls below
half a cell, which on a graded fan is immediately.

The NEAR end always graded correctly, because the field GROWS away from an attractor and the
look-ahead therefore never bound. **Only the approach failed** — which is why nothing caught it: a
test written on the first cells of an interval passes throughout.

Measured, 100 µm × 50 mm FR-4 at 10 GHz, cells/λ = 20: **184 gridlines in x, of which 110 were
consecutive 3 µm steps over the last 330 µm.** The near end read 3, 9, 27, 81, 243, 714 µm — a
textbook ratio-3 fan — and then the far end read 714 µm followed immediately by 6 µm, with nothing
in between.

### The fix — closed form, no look-ahead, no clamp

`SurfaceMesher.PartitionGraded`. The field `h(x) = c₀ + g·d` is Lipschitz in `g`, so the largest step
whose own far end the field still admits can be SOLVED for:

```
for each attractor at distance d AHEAD of x:
    s ≤ (d ≤ c₀)  ?  c₀  :  (c₀ + g·d) / (1 + g)
s = min(h(x), those)
```

The second branch is the `s` satisfying `s = h(x + s)`. Once `d ≤ c₀` that formula returns MORE than
`d` — the step crosses the attractor — and the binding constraint becomes the field's own floor, so
the branch is `c₀` there. The two agree at `d = c₀`, so the constraint is **continuous**, which is
what preserves the translation invariance the continuous size field was chosen for in the first place
(see `Mesh`'s growth-ratio derivation for the knife edge a discontinuity introduced last time).
**Attractors BEHIND need no term at all** — stepping forward only increases their distance.

Same rescale, same `minCells` floor, same `(0,1]` fraction contract as the marcher it replaces.
**`BoundaryMesher.PartitionFractions` is untouched** — it is kernel A's cross-section mesher and every
kernel-A number in `HISTORY.md` sits on it.

On the same trace: **184 → 79 gridlines**, tail reading 714.6, 232, 77.5, 25.8, 8.6, 2.9 µm.

### What it does to cell counts — and it is NOT uniformly cheaper

| fixture (FR-4, 10 GHz unless stated, cells/λ = 20, edge mesh on) | before | after |
|---|---|---|
| 100 µm × 50 mm | 1,656 cells / N 3,119 | **711 / 1,334** |
| 100 µm × 50 mm, cells/λ = 10 | 1,314 / 2,473 | **414 / 773** |
| 200 µm × 10 mm | 180 / 331 | 198 / 365 |
| 2.9 mm × 20 mm (hero) | 297 / 552 | 297 / 552 — unchanged |
| 2.9 mm × 20 mm at 20 GHz | 708 / 1,345 | 720 / 1,368 |
| 72 µm × 2 mm GaAs at 20 GHz | 414 / 773 | **162 / 297** |

**The brief's own sentence "monotonic and cheaper at every rung" is wrong about the second half, and
its own table said so** — 264/220/198/176/154/154 against 220/200/180/160/180/200 is MORE cells at
8, 6, 4 and 3 across. The correct statement is: a properly graded down-fan costs more cells than the
old "one big step, then two crawl cells" wherever the interval was too short for the crawl to run,
and dramatically fewer wherever it was long enough for the crawl to dominate. **Long thin artwork —
PCB traces, which is the geometry the brief is about — is entirely the second case.**

### Two non-monotonicities closed, and they were ONE bug

**`MinCellsAcrossConductor`** (200 µm × 10 mm, across = 8/6/4/3/2/1):

| | 8 | 6 | 4 | 3 | 2 | 1 |
|---|---|---|---|---|---|---|
| before | 220 | 200 | 180 | 160 | **180** | **200** |
| after | 264 | 220 | 198 | 176 | 154 | 154 |

**`MeshFrequencyHz` on a narrow conductor** — recorded in `CLAUDE.md` §6 as a measured negative
result ("on a narrow conductor, lowering the mesh frequency RAISES N"). 72 µm × 2 mm GaAs, sweep top
20 GHz, N at f_mesh = 20 / 10 / 5 GHz:

| | 20 GHz | 10 GHz | 5 GHz |
|---|---|---|---|
| before | 773 | 705 | **2,014** |
| after | **297** | **229** | **212** |

**That was never the edge fan legitimately spending back what the bulk saved** — the explanation both
CLAUDE.md and the test carried. It was this marcher: coarsening the bulk pitch widens the gap the
crawl has to cover, so the wasted cells grow. Both ladders are monotonic now.

### The accuracy question — measured once, in a scratch harness, and it is decisive

The brief flagged that the marcher moves de-embedded S21 by 0.063 dB on the 200 µm × 10 mm fixture at
2 GHz, and hypothesised that the OLD mesh — carrying 110 extra cells right at the port, where the edge
singularity is — might be accidentally MORE accurate there. **It is not.** Convergence study, same
fixture, de-embedded, mesh sized at 10 GHz, dense path:

| cells/λ | N before | S21 dB @2 GHz | N after | S21 dB @2 GHz |
|---|---|---|---|---|
| 20 | 331 | **−2.5523** | 365 | **−2.4908** |
| 40 | 841 | −2.4983 | 586 | −2.4940 |
| 80 | 1,164 | −2.4974 | 1,045 | −2.4956 |
| 100 | 1,402 | −2.4973 | 1,266 | −2.4900 |
| 120 | 1,572 | −2.4957 | 1,504 | −2.4893 |
| 160 | 1,997 | −2.4958 | 1,980 | −2.4908 |
| 200 | 2,456 | −2.4951 | 2,456 | −2.4910 |

Both sequences settle inside a ±0.005 dB band around ≈ −2.493, which is this fixture's own mesh
sensitivity at that refinement. **At the SHIPPING mesh the old marcher is 0.057 dB outside that band
and the new one is inside it** — as |ΔS| against each sequence's own finest rung, **8.2e-3 before,
1e-4 after**, and the 8.2e-3 is past this kernel's own stated de-embedding accuracy (~6.0e-3 at
10 GHz, §5). Same story on S11 @2 GHz (−3.6348 before vs −3.7188 after, against a limit of ≈ −3.712).

**At 10 GHz the hypothesis is not refuted, merely small**: limit ≈ −2.190, old reads −2.1777
(|ΔS| 1.3e-3) and new −2.1693 (|ΔS| 2.9e-3), so the extra port cells DO buy something there — about
0.008 dB, an order below what they cost at 2 GHz, and both are inside 6.0e-3.

Read per unknown rather than per rung the answer is not close: the new marcher reaches its converged
value at N = 365, and the old one is still 0.007 dB out at N = 841.

**cells/λ = 240 and above cannot be measured on the dense path** — de-embedding's own calibration
standard needs 5,720 unknowns there and is refused by `UnknownCeiling`. 200 is the finest rung.

### What MOVES, and has not been re-measured

Every kernel-B number taken on a mesh with the edge mesh ON is potentially affected; the fixtures
above are the ones measured. Specifically **`CLAUDE.md` §5's mesh-frequency table** (FR-4 hero
1–20 GHz: N = 1,345 / 552 / 348 with worst |ΔS| 2.97e-3 / 1.50e-2 / 1.58e-1) was taken on the old
marcher — its N column is now 1,368 / 552 / 314 and **its |ΔS| column has not been re-run**, because
that measurement is `MeshFrequencyAccuracyTests`, `Category=Benchmark`. The qualitative conclusion it
supports (halving is defensible, quartering is not) is not in doubt; the digits are stale.

**A mesh with the edge mesh OFF is bit-identical** — no attractors ⇒ `BuildGridLines` takes its
ungraded branch ⇒ the marcher is never reached. Asserted on three fixtures against counts pinned from
the pre-M0 tree.

### The named trap

**A marching mesher's step must be consistent with the field at the step's OWN far end, and a
look-ahead clamped to an interval boundary is not that.** The clamp reads the field at a point the
step does not reach, and where that boundary is itself an attractor it reads the field's global
minimum. The failure is invisible in every observable a mesher publishes — the tiling is exact, the
cells are valid, the solve is smooth — and shows up only as a cell count that will not fall when the
pitch is coarsened. If a grading knob ever again "does nothing" or "does the opposite", measure the
STEPS along one axis before believing any explanation about fans trading against bulk.

### Gates

`tests/Engine.Tests/Mom/MeshGradingTests.cs`, 15 tests, **29 ms**, no solve anywhere:
no collapsed run · every adjacent cell pair inside the derived growth ratio (the fan grades
approaching an edge as well as leaving one) · cell count non-increasing over the cells-across ladder ·
translation invariance at 3.7 mm in coordinates as well as counts · bit-identity to the pre-M0 mesh
with the edge mesh off. **9 of the 15 fail on the pre-M0 marcher**, verified by pointing
`BuildGridLines` back at it and running them — a gate nothing can fail is not a gate.


## M2 — the transmission-line mesh: the two settings become orthogonal (2026-09-09)

**Owner report, made three times before it was understood, and the third time was the one that
landed.** Lowering *Cells per wavelength* did not reduce the cell count. Lowering *Mesh frequency*
did not reduce it either. Not "reduced it less than expected" — **did not move it at all, at any
value.**

### The cause, and it is one line of arithmetic

```
hx = min(λ_g/CellsPerWavelength, narrowX/MinCellsAcrossConductor)
hy = min(λ_g/CellsPerWavelength, narrowY/MinCellsAcrossConductor)
```

**The SAME min in both axes.** On any artwork whose metal is narrower than a λ cell the geometry term
wins in *both* directions, and the two wavelength controls are then structurally inert — nothing the
user types can reach the mesh.

Reproduced on the owner's own `.cem` (a connector cutout, 6.07 × 1.96 mm, 370 µm narrowest in x and
360 µm in y, one polygon with a right-angle bend in it, only **5** hard/attractor grid lines per
axis so the count really is the bulk pitch):

| Transmission line | cells/λ | mesh frequency | grid | cells | N |
|---|---|---|---|---|---|
| off | 20 | 10 GHz | 77 × 39 | 1,838 | **3,521** |
| off | 20 | 100 MHz | 77 × 39 | 1,838 | **3,521** |
| off | 10 | 10 GHz | 77 × 39 | 1,838 | **3,521** |
| off | 5 | 10 GHz | 77 × 39 | 1,838 | **3,521** |
| off | 5 | 100 MHz | 77 × 39 | 1,838 | **3,521** |
| **on** | 20 | 10 GHz | 49 × 39 | 1,052 | **1,971** |
| **on** | 10 | 10 GHz | 45 × 38 | 920 | **1,715** |
| **on** | 5 | 10 GHz | 44 × 38 | 888 | **1,652** |
| **on** | 20 | 100 MHz | 44 × 38 | 889 | **1,652** |

The setup shipped with `CellsPerWavelength: 5` **and** `MeshFrequencyHz: 100 MHz` — both knobs already
at the floor. λ_g/5 at 100 MHz on that stack is ~0.37 m against 360 µm of metal: four orders of
magnitude apart, so the λ term could never bind. **A note already said so** (the non-binding-λ note,
shipped 2026-09-09). A note about a dead control is not a working control.

### The fix is the owner's own sentence

> "The 2 settings can be orthogonal to each other. For a transmission line running east-west, the
> `narrowest_metal / CellsAcross` setting is a north-south setting and the `CellsPerWavelength` is an
> east-west setting. It doesn't have to be one or the other."
> "CellsPerWavelength should always be in the direction of the current."
> "Make sure to follow the bends of the geometry to guess which way the current is going."

That is also the physics. **Across** a line the current carries the 1/√d edge singularity and the
width must be resolved. **Along** it the current varies on the scale of a wavelength, so
λ_g/CellsPerWavelength is the whole requirement, and taking a min with the narrowness there refines
for a singularity that is not present.

`PlanarMeshSettings.TransmissionLineMesh` (default **off**; `.cem` under the omit-at-default rule, a
panel checkbox, one undo entry, and a term in `EmSnpProvenance.MeshHash`).

### Why it is a FIELD and not one angle — "follow the bends"

D8 is one tensor-product grid and a cell cannot rotate. **But the grid lines need not be uniform**, and
that is enough: an east-then-north L-bend wants a coarse x pitch over its horizontal arm and a fine
one only where the vertical arm stands, and the transpose in y. Both are an ordinary non-uniform
tensor grid. So every point of the metal states what it needs in x and in y, and a column takes the
finest need in it (`PlanarMeshPitchField`).

Nothing about `PlanarCell.IX/IY`, the rooftop cell pair, or `RectangleIntegrals`' axis-aligned closed
forms is touched. Route C — cells that actually rotate — remains a second mesher and is not this.

**The direction at a point is the direction of the LONGEST local chord**, and the width is the chord
across it. The first version took the *shortest* chord as the transverse direction, which is right in
the middle of a strip and catastrophic near a rim: a scan line nearly tangent to an edge cuts a chord
one sampling step long, so the "width" collapsed to ~1 µm and a plain 10 mm line meshed at **2.27
million cells**. A setting turned on to make the mesh smaller made it 11,000× bigger.

### Three guards, each of which was needed because it failed first

1. **It may only COARSEN.** Every pitch is floored at the one the per-axis rule would have used. A
   local width is a sampled quantity and must never be allowed to drive the mesh finer than a direct
   measurement of the polygons.
2. **The floor is applied BEFORE the aspect cap, and the order is load-bearing.** Capping the along
   pitch at 64× an *unfloored* rim-sliver width pinned it to 476 µm against a λ cell of 715 µm — so
   *Cells per wavelength was still inert with the setting on*, which is the entire bug.
3. **`minCells` is 1 on the field path.** `PartitionGraded`'s floor works by *discarding* the marched
   partition for a uniform one. Any single number computed from a varying field is wrong somewhere:
   the finest value re-subdivided the whole interval at the narrowest neck (65 cells where the field
   asked for 20, i.e. exactly the mesh the setting was turned on to avoid), and the field's integral
   over-counts. The enforcement pass already guarantees the cap cell by cell against the field's own
   value, so the floor is redundant rather than merely awkward. **This was the single biggest of the
   three** — it alone took the owner's file from 3,235 to 1,971.

The field is **Lipschitz-smoothed** at the edge fan's own growth ratio, and that is not optional: a
column-wise minimum is a step function, and L8b already paid for a discontinuous size field once
(moving the same rectangle 3.7 mm changed the mesh by 33%).

### The honest cost: the edge fan grows

**It is asserted on the PITCH, not on the cell count**, and the difference is a finding. A coarser bulk
gives the graded fan at every conductor edge further to climb, and the fan's ratio is clamped at 3×
per cell, so each attractor costs about log₃(coarser/finer) extra cells. On artwork that is mostly rim
and hardly any bulk — an 8-segment taper — that outweighs the bulk saving and the count rises
slightly: **728 → 858 / 805 / 741 at cells/λ 20 / 10 / 5**. Every cell is the same size or larger; there
are simply more fan cells. The mesher says so in its own note whenever the bulk pitch spans more than
3× across the artwork, and names the edge mesh as what removes the fan.

### The ports are a CHECK, not the source — a deliberate reversal of the brief

The brief (and the owner's first suggestion) took the direction FROM the port orientation, and for a
straight line that is exactly right. **It cannot survive "follow the bends":** two ports state one
direction between them, and an east-then-north bend's two ports state 45° — the direction of neither
arm. Its area moment says 45° too, so the two agree and agreement proves nothing. So the metal is
measured per point, and the ports are used only to report a *disagreement*: a port sitting on a face
the local field says is along the current rather than across it means one of the two is wrong, and the
user is the one who can tell which.

`SurfaceMesher.Mesh` takes the ports as an optional trailing argument, and **`PlanarKernel.Mesh` and
the EM panel both pass them** — the panel's pre-solve unknown count and the run's must not disagree,
which is exactly the defect P12 fixed for the accelerated ceiling.

### Gates

`tests/Engine.Tests/Mom/TransmissionLineMeshTests.cs`, 10 tests, **2 s**, no solve anywhere. The first
two are the bug itself, written to fail if the two settings ever go back to competing — including an
**equality** assertion that the knob is inert with the setting off, so if that ever stops being true
someone is told rather than left with a pointless control. Then: it only ever coarsens the bulk cell ·
off is bit-identical, ports or no ports · it survives `Auto` · the aspect cap binds on the field ·
translation invariance at 3.7 mm · and the bend test, which asserts **each arm keeps its own
transverse resolution** rather than a cell count, because that is the property "following the bend"
actually means.

**A fixture note worth keeping:** a straight x-directed line does NOT show this defect — it is 10 mm
long measured along x, so `narrowX/4` is 2.5 mm and the λ cap does bind in x. The bug needs artwork
narrow in **both** axes, which is why the fixture here is a right-angle bend. A 10 mm line is also
fan-dominated (about 10 of its 15 x lines), so the bulk pitch cannot show through it at all; the
orthogonality test uses 50 mm.

---

## M4 — the LOCAL edge reference length (2026-09-09)

**Owner report, the fifth round of one complaint**, and the first four rounds answered the wrong
half of it. The complaint, in the owner's own terms: there is a floor on the cell count that no
setting reaches; 1 cell across the conductor still renders more than 20 across the wide metal; a
mesh frequency of 0.001 GHz still renders more than 10 cells along the current. And, once the file
was measured: *the narrow trace's edge mesh continues into the wider transmission line even though
it is not needed for accuracy there.*

### What the owner's own file measured

The owner's own connector-cutout `.cem` (a top-to-inner transition), driven headlessly through
`EmSetupResolver` + `PlanarExtractor` + `SurfaceMesher` (a scratch console, not a test):

| setting | cells |
|---|---|
| as saved (TLM on, edge on, across = 1, cells/λ = 2, f_mesh = 1 MHz) | **513** |
| cells/λ 2 → 5 → 10 → 20 | 513 / 513 / 513 / 513 |
| f_mesh 1 MHz → 1 GHz → 5 GHz → 10 GHz | 513 / 513 / 513 / 520 |
| **edge mesh OFF** | **71** |
| across 1 → 2 → 4 → 8 | 513 / 630 / 881 / 2,028 |

**Three separate facts, and only one of them was a defect.**

1. **The two λ knobs are genuinely inert here and the mesher is right to say so.** At 1 MHz
   λ_g = 143 m against a 6.065 × 1.963 mm part — **4.2e-5 λ_g across**. Nothing about that is
   fixable; wavelength is five orders of magnitude from binding.
2. **"1 cell" is unreachable, and not because of any setting.** A tensor grid must carry a gridline
   on every axis-parallel conductor edge or the metal is not where it was drawn. This artwork
   contributes **4 distinct vertical-edge X coordinates and 9 horizontal Y** — a floor of 4 × 9
   lines before any control is consulted. Measured floor with every knob at its coarsest: 71 cells.
   (It also carries two **5 µm slivers** — hard Y lines at 848/853 µm and 1213/1218 µm — which pin
   `MinCellEdgeM` at 5 µm no matter what. That is artwork, not meshing.)
3. **The 513 was the EDGE FAN, essentially in its entirety**, and that is what nobody had measured
   in four rounds. Dumping the attractors settles it: all 4 X attractors and 5 of the 9 Y attractors
   raise a fan, each ~6–7 gridlines wide (10.6, 31.8, 95.3, … / 10.8, 32.3, 96.9, 290.7, 872.1 µm),
   and 4 × 7 ≈ 29 against a `gridX` of 30. **The bulk pitch contributes a handful of lines; the fans
   contribute the rest.**

### The defect, stated exactly

`EdgeReferenceLength` returned **one scalar for the whole layout** — 3% of the narrowest conductor
*anywhere* — and that one cell was then placed at **every** conductor edge. So a single narrow
feature makes the fan at every wide trace's rim ten times finer than that rim needs, and a fan's
length is `log_r(bulk/c₀)` cells, so the surplus is spent on gridlines that **cross the entire
tensor grid**. On the owner's file: 3% of 360 µm = 10.8 µm applied to metal 3.6 mm wide.

`PlanarEdgeReference.CellSize` is not an escape — it measured the identical 513, because with the
transmission-line mesh on `min(hx, hy)` is pinned to the same global minimum.

### The fix

**`PlanarEdgeReference.LocalConductorWidth`**, now what `PlanarKernel.Mesh` and the EM panel's
preview both pass. Each attractor carries its own `c₀`, 3% of the metal measured **perpendicular to
that edge, at that edge** (`SurfaceMesher.LocalWidthAt`, `EdgeAttractor`, `GradedAttractor`).

Three things about it are load-bearing:

- **The grading rate `g` is UNCHANGED** — still derived from the global narrowest conductor. The
  size field is still `h(x) = min_i [c₀_i + g·|x − a_i|]`; only its floors move. A single `g` is
  also what keeps the field Lipschitz at one rate, which is what L8b's translation invariance rests
  on.
- **Every `c₀_i` is floored at the global `c₀`, so the mode may only COARSEN.** `h_local ≥ h_global`
  pointwise ⇒ the cell count is bounded above by `ConductorWidth`'s, structurally. This matters
  because a local width is a **sampled** quantity: without the floor a sampling artefact on unseen
  artwork could refine the mesh of someone who reached for the setting to shrink it. Same invariant,
  same reason, as `PlanarMeshPitchField`'s own floor.
- **The width is `min(perpendicular run, the edge's own length)`, and the second term is the
  end-cap case rather than a guard.** On a W × L patch the two side edges' perpendicular run is W —
  the scale the 1/√d crowding decays over, and correct. The two END CAPS' perpendicular run is L,
  and taking it would size a long line's end-cap fan on the line's *length*; the crowding there is
  set by the width. `min` is W in both cases and needs no test for which kind of edge it is.
  Sampled at 5 interior points and reduced by **MEDIAN, not minimum** — a rim carries notches,
  treads and corner slivers, and the minimum lands in one of them.

Measured on the owner's file: **513 → 420** as saved, **641 → 540** with the transmission-line mesh
off, **1,829 → 1,698** at the shipped defaults. A uniform line is **bit-identical**, as it must be.

### Two traps this hit, both caught by its own gates

- **A new enum value falls into the WRONG BRANCH of a two-way `?:` and nothing says so.**
  `EdgeReferenceLength` read `kind == ConductorWidth ? narrowest : cellSize`, so
  `LocalConductorWidth` silently took the **CellSize** arm — deriving `g` from a `c₀` four times
  finer, and making a **uniform line mesh differently from itself** (198 → 176 cells) when a uniform
  line has no local widths to find. It is now written as `kind == CellSize ? cellSize : narrowest`,
  so the DEFAULT arm is the geometry one. The gate that caught it is
  `OnAUniformLine_TheLocalReferenceChangesNothing`, which exists precisely because a uniform line is
  the one fixture where the answer is knowable in advance.
- **A pointwise coarser FIELD does not give pointwise coarser CELLS.** `PartitionGraded` rescales
  each interval to land exactly on its endpoints (`xs[i] * length / last`), so a coarser field can
  leave a slightly shorter remainder cell against a hard gridline — measured at 4% below the global
  mesh's finest cell. The **count** is the invariant; `MinCellEdgeM` is not, and the gate says so
  with a 10% band rather than pretending otherwise.

### What it does NOT do, and cannot

**It does not stop a fan crossing the part**, which is the other half of what the owner described.
An x-attractor refines a **column over the full height of the grid** — that is what a tensor product
is. Localising it in the other axis means merging cells back afterwards, which produces T-junctions,
which the rooftop basis (a pair sharing a cell edge) and `RectangleIntegrals`' axis-aligned closed
forms do not admit. That is a second mesher, not a setting. This makes each fan **shorter**; it does
not make it **narrower**.

**The user's own remedy is still the large one on this file: the edge mesh off, 513 → 71.** And it
is the consistent setting for what was asked — 1 cell across already means no transverse basis
function and no edge singularity resolved, so paying 442 cells for a fan that resolves it anyway is
buying nothing.

### The notes, and why they were part of the defect

The mesher **had been saying** cells/λ and mesh frequency were inert on this artwork, and that the
edge mesh was the one live control. Nobody read it: it was one clause in the middle of a 90-word
paragraph, under three other paragraphs. **Owner instruction: an engineer does not read a paragraph
in a side panel.** Every note in `SurfaceMesher` and `PlanarMeshPitchField` was cut to its numbers
and its remedy — the reasoning belongs in the source, which is where it now is exclusively — and the
panel's notes became **ONE `SelectableTextBlock` per list rather than an `ItemsControl` of them**,
because an `ItemsControl` selects one LINE at a time and copying a mesh report out meant dragging
each note separately. `NotesText` / `MeshNotesText` / `PlanarMeshNotesText` join with a blank line.

**Diagnosis that is built and then not read is the same defect as diagnosis that is not built.**
This one cost four rounds.

## The via electrical-length refusal names the frequency that would pass (2026-09-09)

An internal port on a 355.6 µm stackup was refused at a 20 GHz sweep top: k·ℓ = 0.3127 against
`PlanarLevels.MaxElectricalLength`'s 0.30. The refusal explained the bound thoroughly and then said
only "Lower the sweep's top" — leaving the reader to find the number by bisecting whole EM runs, with
nothing on screen saying whether the geometry was 4% over the line or 400% over.

k is proportional to frequency, so the answer is a plain ratio and exact: `CheckOne` now takes the
sweep top the wavenumber was computed at (`PlanarKernel.MidpointRuleVerdict` already had it) and
appends "(to 19.19 GHz or below, from 20 GHz)". Zero omits the phrase, which is what a unit test
constructing a bare wavenumber wants.

The other two remedies are unchanged and are the ones to reach for when the sweep top is not
negotiable: split the via across intermediate meshed levels, or make the span shorter — for a
ground-attached port, that means a ground-designated conductor closer to the signal level, since the
span is the stackup's own.

## ANT-2 — the detail floor, and an edge fan whose grading rate is local (2026-09-10)

`brief-antenna-2-mesh-detail-floor-and-edge-fan.md`. Two changes to `SurfaceMesher`, neither
antenna-specific: both fix every **imported** board, which is where sub-wavelength artwork comes
from. The antenna-specific mesh change is ANT-3.

### What was wrong

An imported patch board asked for **704,482 unknowns** on the default mesh. Two of its three causes
are the general ones:

1. **The narrowest metal was 310 µm of connector via land and aperture-rounded corner** — λ_g/265 on
   that board, sitting ~9 mm from anything electrically interesting. Every width measurement in the
   mesher (`MeasureNarrowness`'s global 5th percentile, `PlanarMeshPitchField`'s local across-chord,
   `LocalConductorWidth`'s per-edge run) answers *how narrow is the metal* and **none of them had any
   notion of a feature being too small to matter electrically**. Deleting only those features moved
   the same mesh to 51,031 — a **14×** swing bought by geometry no field responds to.
2. **The edge fan was 61 % of the mesh on a patch** (12,596 → 4,854 when the edge mesh went off), and
   on an antenna switching it off is not available: the radiating edges set the resonant frequency.
   `LocalConductorWidth` had already given each attractor its own `c₀`; its own doc comment named the
   two things it did not change, and the first of them is the whole of this: *"the grading rate g is
   unchanged — it is still derived from the global narrowest conductor."*

### M1 — the detail floor

`PlanarMeshSettings.DetailFloorDivisor`, the **eighth** control, **default λ_g/200 and ON**. Metal
narrower than λ_g ÷ this does not get to drive the cell pitch. It is applied in three places and it
had to be all three or it is only a third made: the global narrowness measurement (**per SHAPE, before
the minimum** — which is what lets a 310 µm land stop sizing the mesh while a wider conductor beside
it goes on being measured as drawn), the pitch field's local across-chord, and each attractor's own
per-edge width.

**It also suppresses the FAN of an edge shorter than the floor, and that is where nearly all of the
win is.** A feature the mesher has just decided is not worth sizing the mesh on was still asking for
four graded fans, and a fan is charged across the whole tensor grid. **Only the fan goes — the hard
gridlines stay**, so R-msh-1's exact tiling is untouched and the feature is still meshed. This is
`CapMinFractionOfExtent`'s own argument (an edge the mesh cannot represent earns no fan) restated in
wavelengths instead of in fractions of a bounding box, which is the only form of it that means the
same thing on a 1.7 GHz board and a 40 GHz one.

- **λ-relative, never absolute.** An absolute number in µm is a different decision at 1.7 GHz and at
  40 GHz and would be wrong at one of them. Taken at the same λ_g the cell-size cap uses, at the same
  `MeshFrequencyHz`. **With no sweep frequency there is no floor at all** and the report says so.
- **It floors and never refines**, so every field it touches is pointwise ≥ what it was and no
  configuration gets worse. Same invariant `LocalConductorWidth` already states for its own `c₀`.
- **It survives `Auto`**: Auto means *choose the resolution for me*, and which geometry is
  electrically real is not a resolution.
- **The report names the floor, how many shapes fell below it, and the pitch it displaced** — and
  says so explicitly when nothing fell below it, because "the floor is on and it changed nothing
  here" is the question a user reading the count will ask next.
- **`EmSnpProvenance.MeshHash` carries it UNCONDITIONALLY**, breaking that function's own
  omit-at-default rule on purpose. That rule is sound for a control whose default reproduces the
  older behaviour, which is every other one; **this is the first whose default is ON**, so a `.snp`
  stamped before ANT-2 describes a mesh built with no floor, and on any artwork carrying sub-λ_g/200
  detail that is a different mesh. Every pre-ANT-2 planar `.snp` reads as stale once, correctly.

#### It is capped at a fraction of the artwork's own extent, and that is not a guard

The floor's premise is that a feature is small **relative to the part**, so it has no meaning at all
when the part is itself orders of magnitude below a wavelength — and that is an ordinary structure
here, not a pathology. `MimThinLayerTests`' plates are **0.8 µm square at 10 GHz on GaAs**, where
λ_g/200 is 41.8 µm: fifty times the whole thing. Uncapped, the floor discarded every width on the
artwork and the mesh came back **empty** (`'rowCount' must be a positive value` out of the fill).
`CapMinFractionOfExtent` is already this file's constant for *one cell at the finest mesh this kernel
can afford*, and the argument carries over unchanged: a floor coarser than one such cell is not
declining to size on a detail, it is declining to mesh the part.

### M2 — a fan that is short because its grading rate is local

`GradedAttractor` carries its own `Growth`. The fan's length is `log_r(bulk/c₀)`, and `r` was one
global number derived against `min(hx, hy)` — which, **with the transmission-line mesh on, is the
FINEST pitch the field asks for anywhere**. So the fan was rated for a target it had already passed
and then had to climb to the actual bulk at that rate. On the fixture below, at the feed's own rim:
`c₀` = 10.5 µm against a 4.107 mm along-pitch is a climb of 391×, and at `r` = 2.05 that is **eight
cells** — eight gridlines each crossing 41 × 49 mm of artwork, for one conductor edge. Exactly the
"about eight graded steps" the brief predicted.

Each attractor now derives `r` from its **own `c₀`** against the **bulk cap where its own fan
starts**. Both `c₀` and `g` are floored at the global pair, which makes
`h(x) = min_i[c₀_i + g_i·|x − a_i|]` pointwise ≥ the old field — `h` is non-decreasing in `c₀_i`, and
in `g_i` wherever the field binds (`d ≥ c₀`) — so the count is bounded above by today's structurally,
not by hope. Measured on the same board with the floor off: **14,709 → 11,754 unknowns and a longest
fan of 6 → 4**, from this alone.

**With no pitch field this changes nothing at all, to the bit**: the local bulk *is* `hMax` there, so
every attractor derives the ratio the global one already had. Every number in `HISTORY.md` taken on
the per-axis rule is untouched, and `LocalEdgeReferenceTests`' pinned 198 still reads 198.

#### The bulk a fan is rated against is the cap WHERE IT STARTS, not the axis's coarsest pitch

Both were built and measured. Using the axis's coarsest pitch is worse twice over. It drives `r` to
its 3× clamp on any board where the two differ, so **the fan stops responding to
`MinCellsAcrossConductor` at all** — `TransmissionLineMeshTests.OnAStraightLine_TheTwoAxesAreGoverned
ByDifferentSettings` catches that as a y gridline count that will not move when Cells across does
(8 → 8). And it over-states the climb: a connector land's fan reaches its own ~80 µm neighbourhood in
three cells and never climbs to the 2.9 mm bulk out on the patch, but that reading called it eight.
The two variants mesh within 4 % of each other on the patch (11,754 against 11,305), so this is a
correctness-of-reporting and orthogonality choice rather than a cell-count one. The **reported** fan
length uses the same local bulk, for the same reason — a number describing a climb no fan makes is
the class of note this file keeps having to fix.

#### Neither of §4's secondary levers is here, and both were measured rather than skipped

- **Lever 1 — a λ-relative floor on the finest edge cell — cannot be sized.** The value that closes
  the patch board is **λ_g/540**, and that is **5× coarser than the legitimate 6 µm edge cell of a
  plain 200 µm × 10 mm line at 10 GHz**, whose bit-identity is one of this brief's own gates. No
  number does both, because the quantity that is actually wrong is the **ratio** `bulk/c₀` — 391 on
  the feed rim against 8.3 on the plain line — and not the absolute size of either.
- **Lever 2 — a fan-length cap — was BUILT as `c₀ ≥ bulk / MaxGrowthRatio^EdgeCells`, and removed.**
  Once the rate is derived against the real bulk it changes **not one cell** on the board this brief
  is about (11,754 / 5,371 / 4,048 across the floor ladder, identical with and without it), while it
  does couple `c₀` to `CellsPerWavelength` — through the bulk, which varies with cells/λ via the
  end-cap direction blend — and that is what broke the orthogonality gate. **A lever that buys nothing
  and costs a guarantee is not a backstop.**

#### So the overrun is REPORTED instead of hidden

`GrowthRatioFor` clamps `r` at 3, so a climb steeper than `3^EdgeCells` cannot be made in `EdgeCells`
cells and the fan simply runs longer — the 349.3 µm feed rim's own 10.5 µm edge cell against a λ_g/20
along-pitch of 4.107 mm is a 391× climb and takes 5. The mesher now says the number and why, rather
than coarsening the edge cell to make it come out right. The old `EffectiveEdgeCells` note is re-pointed at the realised fans for the
same reason: computed from `c₀` against `hx`/`hy`, it stated a length no fan on the artwork had.

### A regression this brief introduced, found by measurement and now gated

**`c₀` is ZERO when the edge mesh is off, and the per-edge branch computes `max(c₀, 0.03·w)` — which
is `0.03·w` even at `c₀ = 0`.** That had always been true and had always been harmless, because the
"is anything graded" gate read a single global growth rate (`ratio − 1` is negative there). A
**per-attractor** rate makes that gate read the attractors themselves, so **an edge mesh the user had
turned OFF came back on**: 40,952 unknowns with it off against 11,754 with it on — not merely wrong
but the wrong way round. Any change that moves a decision from a single scalar onto a list must
re-ask which of that list's fields are still meaningful when the control is off.
`DetailFloorTests.WithTheEdgeMeshOff_NoFanIsBuilt_UnderAnyReference`.

**And the gate is stated as "every reference gives the same mesh", not as a count comparison against
the edge mesh being on**, because that comparison is not an invariant: with the transmission-line
mesh on, switching the edge mesh OFF drops the pitch field's own Lipschitz rate to `MinGrowthRatio`
(there is no fan ratio left to borrow), which smooths the field harder and can legitimately produce
*more* cells. Pre-existing, and the mesher already says so in its own note.

### The defect in §7 — a refusal that named the one knob that works and told the user not to turn it

On the measured board the refusal said, in capitals:

> …the narrowest conductor run is 310.102 µm, and meshing it 4 cells across forces a 77.526 µm pitch
> over all 55610 µm × 49400 µm of the artwork … **LOWERING CELLS PER WAVELENGTH OR MESH FREQUENCY
> WILL NOT REDUCE THIS COUNT.**

while the pitch **field** it had just built spanned 78 µm to 3.485 mm, and lowering cells/λ was
exactly what took the same board from 23,416 unknowns to 1,909. The sentence is correct for the
per-axis rule and **false for the field** — a field's finest pitch is local to the narrowest neck,
and λ still sets the pitch along the current everywhere. It now branches on **which pitch rule
actually produced this mesh**, not on the settings, and the per-axis wording is untouched because
there it is true. The same sentence was live in two other places on the same page — the `capBinds`
note and the pre-grid `MaxGridCells` refusal — and all three branch the same way now. This is the
third time this class of defect has been found in this file (owner reports 2026-08-14 and 2026-09-09,
both recorded above); the rule it keeps breaking is **never name a remedy without asking whether it
binds**.

### Measured — the fixture, and the numbers

`brief-antenna-0-overview.md`'s board reconstructed from its own description (41.3 × 49.4 mm patch on
203.2 µm FR-4 εᵣ 4.4 / tanδ 0.02, a 349.3 µm inset feed, a rounded coax pad and eight 310 µm ground
via lands, 55.61 × 49.4 mm overall). **Not the owner's file** — that was supplied for the
investigation and is not in the repo — but it reproduces its headline number closely enough to be the
right thing to measure on: **744,545 unknowns on the default mesh against the reported 704,482.**

λ_g = 82.14 mm at 1.740 GHz, so λ_g/500 = 164 µm and λ_g/200 = 411 µm.

**Unknowns, ANT-2 applied, across the floor ladder:**

| configuration | off | λ_g/1000 | λ_g/500 | **λ_g/200** | λ_g/100 |
|---|---|---|---|---|---|
| default (TL off, 4 across, edge on) | 744,545 | 744,545 | 744,545 | **402,525** | 104,469 |
| **TL on, 4 across, edge on** | 11,754 | 11,754 | 11,754 | **5,371** | 4,048 |
| TL on, 4 across, edge off | 2,683 | 2,683 | 2,683 | **2,388** | 1,775 |

**What each half is worth, on the row a user actually runs** (TL on, 4 across, edge on):
**14,709 → 11,754** from M2's per-attractor rate (1.25×, longest fan 6 → 4; the baseline is the same
code with the rate forced back to the global one), then **11,754 → 5,371** from M1 at the shipped
λ_g/200 (2.19×, fan → 3). **2.74× together**, and 5,371 unknowns sits under the accelerated ceiling
of 12,000 where 14,709 did not.

M2 is worth comparatively little here and it is worth saying why: the count is dominated by the
*number* of fans rather than their length. The nine connector shapes raise **18 of the 23
x-attractors and 18 of the 24 in y**, and at the floor-off setting they account for **72 of the 150 x
gridlines and 62 of the 123 in y**. M1's fan suppression is what removes them.

**§3's convergence check — de-embedded |S₁₁| at the 1.740 GHz resonance** (one port, TL on, 4 across,
edge mesh off so that every rung is under the ceiling and the comparison is like for like;
accelerated solve, 8 frequencies 1.60-2.00 GHz, ~114 s a rung):

| floor | unknowns | \|S₁₁\| at 1.74 GHz | Zin |
|---|---|---|---|
| off | 2,802 | 0.7516 | 315.7 + 106.7j |
| λ_g/1000 | 2,802 | 0.7516 | 315.7 + 106.7j |
| λ_g/500 | 2,802 | 0.7516 | 315.7 + 106.7j |
| **λ_g/200** | **2,691** | **0.7527** | 314.7 + 110.5j |
| λ_g/100 | 1,858 | 0.7533 | 316.2 + 110.0j |

**The whole ladder moves the answer by 1.7e-3**, which is well inside this kernel's own de-embedding
residual (~4e-3 at 2 GHz on 1.6 mm FR-4, §5). **The connector detail is not electrically live**, which
is the answer §3 asked for and the opposite of the surprise it warned might come back. The first three
rungs are bit-identical because they are the same mesh: at λ_g/500 the floor is 164 µm against 310 µm
of connector detail and does not reach it.

**So the default is λ_g/200 rather than the λ_g/500 first written.** λ_g/500 is a default that does
nothing on the board it was written for. **λ_g/100 was measured and NOT taken**: it buys a further
1.4× and doubles the amount of genuinely drawn metal a floor may coarsen, on the evidence of one
board. λ_g/200 is 411 µm at 1.74 GHz on FR-4 and 10.4 µm at 40 GHz on GaAs, and floors nothing either
starter technology draws at a shipped resolution.

**On §4's own target row** — the brief's board with the connector already deleted, TL on, 4 across,
edge on — this is **2,227 before ANT-2, 2,114 with M2, and 1,164 at the shipped λ_g/200** (the floor
reaches the 349.3 µm feed there and takes its fan from 5 cells to 3), against **712** with the edge
mesh off. The brief asked for "near" edge-off and this is **1.63×**, not 1×.

**The remaining factor is not a defect and cannot be removed by shortening fans.** The five surviving
x-attractors are the feed's own end face, both patch edges and the notch — every one a real conductor
rim — and at `EdgeCells` = 3 each costs about three gridlines by definition. The base x grid on that
board is only 15 lines, because the transmission-line mesh makes the along pitch λ_g/20 over 55 mm;
five three-cell fans on a 15-line grid is the whole of the ratio. Going below it is asking for fewer
graded cells, which is `EdgeCells` and not a mesher change.

---

## ANT-3 — the SHEET intent, for metal that is a radiator rather than a line (2026-09-10)

`brief-antenna-3-mesh-sheet-intent.md`. Mesher-only, on ANT-2's own testing rule: no EM solve
anywhere, no de-embedded point. Gates: `tests/Engine.Tests/Mom/SheetMeshTests.cs` (11 tests, ~3 s)
and `tests/Ui.Tests/Em/SheetMeshUiTests.cs` (14 tests, 121 ms).

### What was wrong, and it was not a bug

`PlanarMeshSettings.TransmissionLineMesh` asks *which way is the current going* and **declines when
it cannot tell** — which is right, and ANT-3 does not weaken it. A wide radiating sheet is the case
it declines by construction, and it is also the case where the question does not apply: on a patch
the current varies on the scale of a wavelength in **both** directions, a half-cosine along the
resonant dimension and near-uniform across the width, with no singularity in the interior to
resolve.

A patch fails the direction test twice over. It is a one-port, so the port vector is unavailable and
the area moment is the only source; and the area moment of a 41.3 × 49.4 mm rectangle reports the
**longer side**, which on the measured board is perpendicular to both the feed and the resonant
dimension. That the directed mesh still helped enormously there is a measure of how bad the per-axis
rule was, not evidence that the direction was right.

### M1 — one three-way control, not a second boolean

`PlanarCurrentModel { None, TransmissionLine, Sheet }` **replaces** the `bool
TransmissionLineMesh` in `PlanarMeshSettings`, in place, at the same position. Two independent flags
that can both be true is a state nothing downstream could act on, and the two intents are mutually
exclusive by construction. `None` is the default, so every number in `HISTORY.md` stays reproducible.
It **survives `Auto`** on the settled taxonomy — `Auto` chooses a *resolution*, and this says what
the resolutions are FOR.

**The persistence migration is the part that is a decision.**

- The new key `PlanarMesh.CurrentModel` is written, omitted at its default, so a pre-ANT-3 `.cem`
  gains no byte and re-serialises byte-identically.
- The legacy `PlanarMesh.TransmissionLineMesh` is **read and never written**. Alone, `true` reads as
  `TransmissionLine` and `false` as `None`; a file carrying it comes back out on the new key, which
  is the whole of the migration.
- **A file carrying BOTH and disagreeing is a refusal naming both keys.** The obvious alternative is
  a precedence rule ("the new key wins") and it is the wrong answer for a reason this area has
  already paid for: a precedence rule means a user who set one control watched the other one silently
  win, which is the exact defect the transmission-line mesh was built to fix. Agreement is not a
  conflict and loads quietly.
- **`EmSnpProvenance.MeshHash` keeps the transmission-line term's exact BYTES** — `|tline=True` —
  rather than re-spelling it as the enum. That value's meaning did not change, only its name in C#,
  so an `.snp` stamped under the boolean must go on reading as current; a tidier
  `|model=TransmissionLine` would have marked every one of them stale for no reason a user could
  see. `Sheet` gets its own term, `|sheet=True`.

The panel's checkbox becomes a three-item combo, and the row reads **"This metal is …"** and
**every label names the STRUCTURE rather than the algorithm** — "Unstated", "Transmission line",
"Radiating sheet". Someone who knows they have drawn a patch can answer that; the same person has no
view on a pitch field. `None` is "Unstated" rather than "Off" for the same reason: nothing is
switched off, the question simply has not been answered.

### M2 — the field without a direction

Same `PlanarMeshPitchField`, same `FieldSamples` grid, same longest-chord width estimator, same
Lipschitz lower envelope at the edge fan's own growth ratio, same one-way floor at the per-axis rule.
The direction term drops out and nothing else does: per metal sample,
`h = max(acrossFloor, min(hWave, localWidth/MinCellsAcrossConductor))`, contributed to **both**
`needX[ix]` and `needY[iy]`.

Three things worth knowing:

- **The width estimator is REUSED, not replaced.** The temptation is to take the shortest chord now
  that there is no "along" — and it is the same trap L8b's own note records: a scan line nearly
  tangent to a rim cuts a chord one sampling step long, which collapsed the "width" to ~1 µm and
  meshed a plain 10 mm line at 2.27 million cells. The estimate does not become safer because the
  intent changed.
- **The aspect cap cannot bind, structurally.** Both axes are asked for the same number at every
  point, so the requested aspect is exactly 1:1 — `WorstAspect` is 1 and `Capped` is never true. On
  the same fixture the directed field asks for ~4,000:1 and is clamped to 64. What a realised CELL
  comes out at is still the product of two independent axis fields, which is the tensor product and
  is true of every mode here.
- **The port check is the directed mode's and only its.** It asks whether a port sits on a face the
  metal runs *into*, which is a question about a direction; asking it under `Sheet` would report a
  "disagreement" on every patch, about an answer nothing used.

It **never declines**, so the note is the only way a user sees that it acted — and the note carries
the three things §4 asks for by name: the bulk pitch in each axis, where the width floor bound, and
the worst aspect.

### "Where the floor bound" is a question about the OUTCOME, not the request

Written first as `localWidth/MinCellsAcrossConductor < hWave` — i.e. *did this sample's width term
come out finer than the λ cap*. That counts every sample the per-axis floor then raised straight back
to the λ cap, which is most of a rim on wide metal: **73 % of a 20 × 30 mm plate**, whose x pitch came
out at λ_g/20 exactly. A user reading "the width set the pitch on 73 % of this plate" beside a pitch
that IS the wavelength cap has been told something false. The predicate is `hSheet < hWave`, which
says only what actually happened; the same plate now reports the width binding where it genuinely
does and the note's other branch — "nothing on this artwork is narrow enough, so the bulk pitch is
the whole answer" — fires on a bare patch, where it is true.

### §5's expected range: MISSED by 1.14×, and the whole of the miss is the edge fan

The brief's arithmetic: λ_g at 1.74 GHz on εᵣ 4.4 is ≈ 83 mm, λ_g/20 ≈ 4.2 mm, so a 41.3 × 49.4 mm
patch is 10 × 12 cells in the bulk, and with a bounded fan on four rims and a locally resolved
349 µm feed the expectation is **order 500-900 unknowns**. Measured on the ANT-2 reconstruction of
that board (which is a fixture, not the owner's file — its per-axis count is 402,607 where the real
board's was 704,482), one frequency point, `LocalConductorWidth`, cells/λ = 20, 4 across, λ_g/200:

| board | per-axis rule | TransmissionLine | **Sheet** | Sheet, edge mesh off |
|---|---|---|---|---|
| as imported (connector present) | 402,607 — refused | 2,710 | **3,231** | 1,519 |
| connector deleted | 17,277 — refused | 1,320 | **1,029** | 521 |

**1,029 against an expected 500-900, and 521 with the edge mesh off — which lands inside the range
exactly.** So the estimate is right about the bulk and does not account for the fan's
tensor-product cost: the longest fan is 3 cells, as ANT-2 intended, but each of those cells is a
gridline across the whole part and there are six real conductor rims (four patch, two feed). Per §5
this is **reported, not tuned** — nothing about the default was changed to close it, and the number
to argue with is the number above.

**The bulk arithmetic came out as predicted, by a route worth recording.** The feed's x columns are
*not* refined to its width: `needX` is floored at the per-axis rule's own `hx`, which on this board is
`narrowX/4` = 2.577 mm (the feed's x-runs are ~10.3 mm, not its 349 µm width), so the feed gets ~4
columns in x and 4 rows in y — 16 cells, not the ~400 an isotropic reading of the feed would have
cost. The one-way floor produces the physically right answer there for a reason that is not about
physics at all, and it is worth knowing that it does: had the artwork put the feed *inside* the
patch's own x range, the column-wise minimum would have charged its width across the patch too.

### The one-way invariant: TRUE on the pitch, and NOT true on the cell count

§6 asks that "for every fixture in the mesher's existing set, Sheet's cell count is ≤ the per-axis
rule's. Assert it, do not assume it." **Asserted, and it fails — on the 8-segment taper, 702 cells
under the per-axis rule against 810 under Sheet.** The cause is not new and is not the sheet's: a
coarser bulk gives the graded edge fan further to climb, its ratio is clamped at 3× per cell, and on
artwork that is mostly rim and hardly any bulk the extra fan cells outweigh the bulk saving. The
transmission-line mesh's own version of this gate had to be stated the same way for the same
measurement (728 → 858 on the same taper), and the mesher reports the trade in its notes.

**With the edge mesh off, all three modes give 679 on that taper.** So the invariant is gated in the
three forms that are true:

1. **On the PITCH, unconditionally** — the largest cell Sheet produces is never smaller than the
   per-axis rule's, on every fixture at cells/λ 20, 10 and 5. This is the structural one.
2. **On the cell count with the fan removed, exactly** — every fixture, every cells/λ.
3. **On the cell count with the fan on, within the same 1.25× the sibling intent's gate allows.**

### A plain line as a sheet: coarse along, unchanged across — and not gridline-identical

§6's safety property, and it holds in the form that matters. Measured on the 200 µm × 10 mm FR-4
line: **23 x gridlines under the per-axis rule against 15 under Sheet, y identical at 10, cells
198 → 126.** Two things that "exactly the per-axis rule" does not literally mean:

- **The x count differs because `BuildGridLines` drops its per-interval minimum-cell floor whenever
  a field is present** (that floor throws the marched partition away for a uniform one, and a single
  number computed from a varying field is wrong somewhere — the trap is recorded in that method). The
  *cap* is identical; the marcher simply places fewer, larger cells under it.
- **The fan's interior y gridlines move by ~1.5e-5 relative** — 94.1257 µm against 94.1226 µm —
  because with a field the fan grades toward the LOCAL bulk cap rather than one global `hy` (ANT-2's
  per-attractor rate). The gate is therefore the transverse line count and the **finest** transverse
  cell, which are what say the 1/√d edge current is still resolved; the fan's internal spacing is
  not.

On the 2.9 mm line the y count itself drops 10 → 9, because there the width term (725 µm) and the λ
cap (714.6 µm) are within 1.5 % of each other and the field's own grading is free to lose one interior
line. The finest transverse cell is unchanged.

### What was NOT done

- **No auto-detection of the intent**, per §7. It is a statement about what the current does, which
  is a modelling decision and therefore the user's; a detector would guess "sheet" on a wide bend and
  under-resolve it silently, which is exactly what the direction decline exists to prevent.
- **The transmission-line decline is untouched.** Gated: on an L-bend the directed field still keeps
  each arm's own transverse resolution, which a single averaged direction cannot.
- **Sheet does not skip the rim**, per §7's named trap. Gated as "the fan is still built under Sheet,
  and turning the edge mesh off still changes the mesh" — the two radiating edges set the effective
  length, hence the resonant frequency, hence every number downstream.
- **No default was changed**, and no accuracy measurement was taken: this brief's rule is mesher-only,
  and the question of whether a sheet mesh gives a *better* resonant frequency than a directed one is
  ANT-9's (the resonance sweep), on a real solve.

---

## ANT-4 — the far field (brief-antenna-4-far-field.md, 2026-09-10)

The pivot of the antenna series: the first thing in this directory that looks **away** from the
metal. `PlanarFarField.cs` + `PlanarFarFieldTests.cs`; the sweep, the `PlanarSolveSettings` and one
new `DataSet` group are the whole of the plumbing.

### The verdict, in one paragraph

**The brief's premise held completely and the phase cost what it said it would.** The far field
needs the spectral Green's function at exactly one point per direction, which `SpectralGreens` and
`LayeredSpectralGreens` already return in closed form, and the rooftop's 2-D transform is
elementary — so the pattern is an **exact sum over N basis coefficients with no quadrature error of
its own**, and every oracle before the power balance is independent of the MoM solve. All five
oracle families passed on the first run: the εᵣ = 1 image reduction, the shorted-stub dipole oracle
over three substrates at every degree, the transform against quadrature at 1e-12, the stratified
two-section cascade, and the one-slab reduction of the general path. **Nothing on this path touches
`Dcim` or `SommerfeldIntegral`, and a source scan holds that shut.**

### The derivation, and the two places it could not be written the obvious way

The whole of it is in `PlanarFarField.cs`'s header. Two results are worth repeating here because
they are what a reader will otherwise re-derive wrongly:

- **The stationary-phase constant was PINNED, not quoted.** Applying the asymptotic operator to
  `F = 1/(2jk_z0)` must reproduce `e^{−jk₀r}/4πr`, because that is the Sommerfeld/Weyl identity this
  whole directory rests on. That fixes `C(r,θ) = j k₀cosθ e^{−jk₀r}/(2πr)` with no saddle-point
  formula transcribed from anywhere.
- **NEITHER ELEMENT FACTOR MAY BE WRITTEN WITH A 1/cosθ OR A `Z^h = ωµ/k_z` IN IT.** The obvious
  spelling of `f_TM` carries a 1/cosθ from `Ẽ_θ` and a cosθ from `C`; the obvious spelling of `f_TE`
  carries `Z^h`. Both are infinite at θ = 90°, which is a point the θ axis **contains**. Written as
  `f_TM = 2V_i^e e^{jk_z0 z}/η₀` and `f_TE = 2I_i^h e^{jk_z0 z}` every quantity is finite there.
  `BothElementFactors_AreFiniteAtGrazing` is what keeps the obvious spellings from creeping back.

**The general kernel forces a two-spelling split for `f_TE`, and it is not stylistic.**
`LineResponse` forms the region's characteristic impedance explicitly on exactly one of its two
paths: `Z^h` multiplies the SAME-region voltage and divides the CROSS-region current, and it is
infinite at grazing. So a level on the stack's top surface reads `f_TE` off the **current** (which
`LineResponse` computes with no impedance at all) and a buried or covered level reads it off the
**voltage** (whose cross-region path never divides by the top region's Z either). The two are
algebraically identical and are gated against each other.

**A second consequence of the same asymmetry: the observation height must be STRICTLY above the top
interface.** The line current is discontinuous at a shunt source, and `I_i(z′|z′)` is its two-sided
average, not the up-going wave — so evaluating at the interface where the metal sits would read
`f_TE` off the wrong side of a step. One free-space radian above the stack is used; any height gives
the same answer because the propagator is removed exactly, so this is a choice of conditioning.

### The θ axis is 0…90°, and the axis is where that is said

With a laterally infinite ground plane the field below is **identically zero by construction**, not
small. A 0…180° axis half full of those zeros invites a polar plot that reads as a measurement of a
very good antenna, and nothing in a plot can say otherwise. `PlanarFarFieldGrid.MaxThetaDeg` is the
one constant a later finite-ground phase moves; the θ values themselves are DATA, so extending the
axis is not a rewrite. A θ beyond it is refused with its reason rather than filled.

### Refused by name, and why guessing would have been worse

Each is representable in the types and genuinely not computed. All four are `EmSuitability`
verdicts, so the SWEEP still ships and the reason arrives as a note — present and refused.

- **A CUT (conformal) boundary cell.** A cut cell's metal is not its rectangle, so the rectangle's
  transform is not its own. Using it would be a smooth, plausible, wrong pattern. `Staircase` (the
  default) meshes what this can transform.
- **A VERTICAL (via or ground-attachment) basis.** A z-directed current radiates, but through a
  SERIES voltage source on the TM line rather than a shunt current one — a different element factor,
  a different transform, and its own oracle. **This is the one refusal with a real functional cost
  and it is worth naming: it rules out a probe-fed patch**, which `brief-antenna-0-overview.md` §2
  calls the cleanest feed this kernel can express, because `PlanarGroundPath.Extend` builds a via for
  an internal port. Edge-fed and inset-fed patches are unaffected. Dropping the via current silently
  would have been a pattern right in shape and wrong in level on exactly the structures where it
  matters.
- **A stack that is not PEC below.** The θ range rests on it.
- **A top half-space that is not free space.** The pattern is written in k₀ and η₀.

### Measured cost — reported, not made into a timing test

Scratch harness, Release, 10 cores, the measured board's own stackup (41.3 × 49.4 mm patch on
203.2 µm FR-4 at 1.74 GHz), a random unit current vector so the timing is the transform's alone:

| N | 1°×1° hemisphere (32,760 directions) | 2°×2° (8,280) |
|---|---|---|
| 262 | 0.46 s / 0.83 s | 0.04 s / 0.21 s |
| 1,237 | 0.68 s / 3.87 s | 0.17 s / 0.98 s |
| **3,017** | **1.69 s / 9.51 s** | 0.43 s / 2.41 s |
| 6,110 | 3.42 s / 19.56 s | 0.97 s / 4.93 s |

(parallel / single core). **Exactly linear in N and in direction count**, as an O(N) direct sum must
be, and the brief's own estimate ("N ≈ 4,000 over a 1°×1° hemisphere is seconds") is confirmed. A
single de-embedded planar point is tens of seconds, so a pattern is a fraction of one frequency
point's own solve. **Nothing was optimised**: the exact per-basis closed form is what ships, and the
gridline-table hoist that would make it ~10× faster was not needed and therefore not written.

### Power balance — reported before it is gated

On a 4 mm FR-4 stub at 5 GHz, port 1 driven at 1 V: **65.11 µW accepted, 718 nW radiated into the
upper hemisphere (1.1 %), 64.39 µW unaccounted.** The unaccounted term is dielectric loss plus
surface-wave power together; itemising them is the metrics phase's. **The copper term is identically
zero in this kernel** — the metal is a perfect conductor and `SigmaSm` is carried through the whole
pipeline and never read by the fill — so the balance closes *optimistically*, which is expected
rather than a defect and is exactly why the gate is the inequality `∫U dΩ ≤ P_accepted`.

`PlanarFarFieldPattern.RadiatedPowerW` is the one quantity in the file that is **not** exact: it is a
trapezoid in θ with the sinθ Jacobian and a periodic rectangle in φ over a sampled grid. Everything
else is closed form.

### Traps found on the way

- **A grid whose φ list is not ascending is refused**, and it caught a test of mine that wrote
  `[23, 337, 61, 299]` meaning "these four angles" — the validator is the reason that was a refusal
  rather than a silently mis-weighted power integral.
- **A relative tolerance at θ = 90° compares two spellings of nothing.** The εᵣ = 1 oracle produces
  1e-34 there and the code produces exactly 0; the comparison has to be against the pattern's own
  peak, not against the local value. The same trap will bite every metric that normalises per
  direction.
- **The far-field verdict must be taken at the LOWEST requested frequency, not at the top of the
  sweep.** The spectral kernel's only frequency-dependent refusal is its electrical-thickness FLOOR,
  so checking `fHi` passes a run that then throws at its first pattern.
- **An adaptively sampled sweep may never solve a requested pattern frequency.** A pattern needs
  basis currents and an interpolated s-parameter has none, so the pattern is taken at the nearest
  SOLVED point and the substitution is reported. Forcing the sampler to solve the requested points
  was rejected: it would make the adaptive answer depend on whether a far field was asked for.
- **The pattern belongs to the structure AS MESHED**, feed lead and all. R-fed-1 grows a uniform lead
  for the calibration and the s-parameters have it de-embedded away; a pattern cannot, because the
  lead's current is real current and it really radiates. Said in a note rather than left to be
  discovered.

### What was built

- `src/Engine/Mom/PlanarFarField.cs` — `PlanarFarFieldGrid`, `PlanarFarFieldPattern`,
  `RooftopSpectrum` (the closed-form transform), `FarFieldElementFactors` (the layered element
  factors), `PlanarFarFieldSettings`, `PlanarFarFieldSet`, `PlanarFarField`.
- `PlanarSolveSettings.FarField` (null = off, so every measured number in §L8c/§L8d/§L9d is
  reproducible by leaving it null), `PlanarSolveResult.FarField`, and the patterns computed inside
  both sweep drivers from the solution columns already in hand — **no second fill, no second
  factorisation.**
- `PlanarKernel.AddFarField` — group `"farfield"`, cubes `Etheta`/`Ephi`/`U`, axes
  `[freq, theta, phi, port]`. **R-res-6 for the sixth phase running: no new result type.** The port
  axis is present from the first commit even though every antenna fixture here is a one-port — it is
  free at construction and retro-fitting an axis onto a shipped cube is not.
- `tests/Engine.Tests/Mom/PlanarFarFieldTests.cs` — 32 tests, **638 ms**, all in the routine tier.

### Not done, on purpose

- **Vertical-current radiation** — see the refusal above. It is the one that costs a real
  capability.
- **The cut-cell transform** — the brief allowed refusing it for v1 and it is refused.
- **No far-field `measure` in the TestBench grammar.** An EM run is a `.cem` run producing a
  `DataSet`, not a TestBench analysis; these are diagnostics cubes in the group pattern kernel B
  already established.
- **No optimisation.** See the cost table: the direct sum is exact and fast enough, and an FFT or an
  interpolation over directions would trade the exactness away for a cost that has not been shown to
  matter.

---

## ANT-5 — directivity, gain, efficiency, beamwidth, and the staged front-to-back refusal
### (brief-antenna-5-metrics.md, 2026-09-10)

The numbers an antenna is judged by. `PlanarMetrics.cs` (the registry), `PlanarPowerBudget.cs` (the
itemisation and the surface-wave launch), `PlanarBeamwidth.cs` (the cut derivation and its refusals),
plus the sweep wiring and one group of cubes that already existed.

### The verdict, in one paragraph

**The conventions half of the brief held exactly and cost what it said. The loss-itemisation half is
where it was wrong twice, and both corrections are improvements rather than reductions.** §3's
"three of the four are already computable" is two computed plus a RESIDUAL, because over a laterally
infinite lossy substrate a volume dielectric-loss integral already contains the whole surface-wave
term; and §5's characterisation of the two power-balance regimes is inverted — surface-wave power
grows with substrate thickness, so on a thin board radiation dominates and the surface wave is a few
per cent, not the other way round. The surface-wave term itself, which the brief called "the term a
laterally-infinite substrate model captures uniquely well", came out right **on the first run**: the
pole-residue derivation, its constant and its sign are pinned by the lossless power balance closing
to **5.6e-5** on the shipped FR-4 cross-section. 26 tests, **3 s**, all routine tier.

### The four conventions, and why naming them was the actual deliverable

Every metric here has at least two defensible definitions. The registry's notes carry all four:

| quantity | taken as | the trap |
|---|---|---|
| Directivity | 4π·U_peak / P_radiated | none; it is the unambiguous one |
| **`GainDbi`** | D·η_rad = 4π·U_peak / P_accepted | **EXCLUDES mismatch** |
| **`RealizedGainDbi`** | that × the mismatch factor | **INCLUDES mismatch** |
| η_rad | P_radiated / P_accepted | the denominator is ACCEPTED, never incident |

- **R-ant-4. Neither is called just "gain", and each note names the other.** Asserted structurally —
  there is no cube named `Gain` and both notes cross-reference — because the failure mode is a user
  comparing one against a datasheet that quotes the other and concluding a tool is broken.
- **R-ant-5. P_accepted is ½Re(Y_jj) of the RAW self-admittance**, the matrix the transformed currents
  actually came out of. **The de-embedded `S` cube describes a different structure** with the feed
  leads removed, and weighing a pattern of one structure against the power accepted by another is a
  mistake nothing downstream could see. `PowerAccepted` is therefore published as a cube of its own,
  which the brief's table did not list: an efficiency whose denominator is not published cannot be
  reproduced.
- **R-ant-6. One mismatch factor, written as accepted-over-AVAILABLE power**,
  `4·Re(Z₀)·Re(Y)/|1 + Z₀Y|²`. Equal to 1 − |Γ|² for a real reference and to the power-wave form for
  a complex one, and written this way because it is a function of the same Y_jj and Z₀ everything
  else reads — so "the two gains differ by exactly the mismatch factor" is structural rather than two
  estimates happening to agree. On a multiport it is the reflection with the OTHER ports SHORTED,
  which is exactly the excitation the pattern belongs to.

### The surface-wave launch — the one piece of new physics, and it closed first time

The derivation is in `PlanarPowerBudget.cs`'s header. The shape of it: the complex power an impressed
surface current delivers is `−½∫E·J*dS`, which Parseval plus the transmission-line map turns into a
k-plane integral of `Σ_{L,L′} Ĵ*·V^p(z_L|z_{L′})·Ĵ` — a second, matrix-free statement of ½Re(Y_jj). A
guided mode is a simple pole of `V^p`; it must decay as it propagates, so the pole is BELOW the real
axis, and Sokhotski–Plemelj picks up `−jπ·β·Q(β, φ)` per mode:

    P_sw = Σ_modes β · Σ_j Im[Q_p(β, φ_j)] / (4·N_φ)

Three places this could have been written wrong and looked right, all of them stated in the file:

- **The residue is taken in k_ρ, not in w = k_ρ².** `V` is literally a function of `w`, so in `k_ρ` it
  is EVEN with poles at ±k_p and `R_k = R_w/(2k_p)`. A contour integral in k_ρ gets that for free; an
  analytic substitution is where a factor of 2 goes missing invisibly.
- **The contour radius is a fraction of the distance to the nearest other singularity, never a fixed
  number.** A near-cutoff mode sits essentially ON the branch point at k₀ — the measured 203 µm
  prepreg board's TM₀ is at (k_p − k₀)/k₀ = 1.4e-5 — and a mode too close for a simple pole to be
  separable from the continuum is REFUSED by name rather than given a number.
- **Ĵ is evaluated at Re(k_p)**, which makes this a small-loss expansion; the worst pole's
  |Im k_ρ|/Re k_ρ is reported with the answer and refused past 0.05.

**The one-slab branch of the line voltage is written as `(Z₀^p/2)(1 + Γ^p)` and that is an identity,
not an approximation** — the voltage a shunt source sees at the interface is `Z_up ∥ Z_down`, and
`SpectralGreens`' cross-multiplied Γ is exactly `(Z_down − Z₀)/(Z_down + Z₀)`. Gated against the
general cascade rather than asserted: **they agree to 5.7e-14** over k_ρ/k₀ ∈ [0, 10] on both
polarisations. R-ant-2 still has `PlanarProblem.RequiresGeneralKernel` choose which one a problem
takes.

**The φ rule is the periodic rectangle and is converged at 90 samples to eleven digits** (checked at
90, 180, 360 and 1,440). The default is 360 only because it costs nothing.

### Two places the brief was wrong, and the corrections

**1. `PowerDielectric` cannot be a third independent integral.** The brief asks for the volume loss
`½∫ωε₀ε″|E|²dV` alongside the surface-wave residue, and for the four terms to sum to P_accepted. They
cannot both be true: over a **laterally infinite** lossy substrate the guided mode decays as
`e^{−2αρ}/ρ`, and `∫ρdρ` of that is finite and equals *everything the mode ever carried* — so the
volume integral already CONTAINS the whole surface-wave term and adding them double-counts. What is
disjoint, and what ships, is

    PowerDielectric = PowerAccepted − PowerRadiated − PowerSurfaceWave

the dielectric loss **not** carried away by a guided mode. On a real finite board that is exactly the
distinction that matters, because the guided part instead reaches the edge and radiates.

**R-ant-3 follows: a residual cannot gate itself, so the LOSSLESS case is the gate.** With tanδ = 0,
PEC metal and a PEC floor there is nowhere for accepted power to go but the upper hemisphere and the
guided modes, so the residual must be ZERO — and the three quantities forced to meet there share no
implementation at all: ½Re(Y_jj) from the MoM factorisation, ∫U dΩ from the far field, and the pole
residues from the spectral kernel. Measured, lossless:

| substrate | N | radiated | surface wave | residual / accepted |
|---|---|---|---|---|
| **1.6 mm εᵣ 4.4** (the FR-4 starter cross-section) | 45 | 80.1 % | **19.9 %** | **5.6e-5** |
| **8 mm εᵣ 2.2** (5× thicker, low permittivity) | 49 | 68.0 % | **32.0 %** | **1.6e-4** |
| 203 µm εᵣ 4.4 (the measured board's prepreg) | 45 | 96.7 % | 2.7 % | 6.0e-3 |
| 100 µm εᵣ 12.9 (GaAs) | 45 | 96.4 % | 2.0 % | 1.6e-2 |

**2. §5's two regimes are inverted.** The brief expects "the thin FR-4 case, where surface-wave and
dielectric loss dominate". Lossless thin FR-4 books **2.7 %** into the surface wave and 96.7 % into
radiation; the surface-wave share grows with `k₀h√(εᵣ−1)`, so it is the THICK substrate that is the
surface-wave regime. The gate therefore runs on the two top rows, where the share is a real fraction
in both (20 % and 32 %) so a sign or factor error in the residue cannot hide, and the thin rows are
reported rather than gated.

**The residual is the MATRIX FILL's accuracy, not the metrics'.** Attributed rather than assumed. It
does **not** move when the far-field θ grid is refined 10× (1° → 0.1° moves the 203 µm case from
6.09e-3 to 5.97e-3 and stops), it does not converge cleanly with mesh density, and it tracks how hard
the DCIM fit is: ~1e-5 on FR-4, ~1e-2 on GaAs — which `GroundedSlab`'s own comment already calls "the
harder case for DCIM". And the argument closes: in a lossless medium Re(V^p) is identically zero for
k_ρ > k₀ away from the poles (Z₀ and Γ are both purely imaginary there), so radiation and the poles
are the only real-power channels there are, and any gap is the matrix.

### The cavity model agrees, and the agreement is the MODEL's error not the mesh's

An independent analytic oracle, written from the two-slot construction (TM₁₀ cavity, magnetic walls,
`M_s = −2E₀ŷ` on both radiating edges in phase) and sharing nothing with the MoM or with
`SpectralGreens`:

    U ∝ sinc²(k₀W sinθ sinφ/2) · cos²(k₀L sinθ cosφ/2) · (cos²φ + cos²θ sin²φ)

On a 29.18 × 36.47 mm edge-fed patch on the 1.6 mm FR-4 starter at 2.4 GHz (h/λ₀ = 0.013):

- **Directivity: 6.423 dBi against the cavity model's 5.950 — Δ 0.47 dB.**
- **And it is MESH-INDEPENDENT to 0.006 dB** from N = 237 to N = 3,831 (6.423 / 6.427 / 6.428 /
  6.429 dBi), so the 0.47 dB is the cavity model's own — it has no surface wave and no feed.
- **Cut shapes: the H-plane agrees to 0.10 dB and the E-plane to 0.57 dB** out to θ = 80°, the
  E-plane being the one the infinite ground plane changes most.
- Radiation efficiency reads 41 % there and is mesh-stable to 0.4 pp (41.21 / 41.34 / 41.20 / 40.99 %). **The realized gain is NOT**
  (−3.85 dBi at N = 237 against −1.25 at N = 536): it inherits the edge port's own Z_in convergence,
  3.2 Ω against 6.6 Ω. Worth knowing which numbers are robust to the mesh and which are not.

### The beamwidth's cut, and the three things that went wrong deriving it

`BeamwidthDeg` is per cut, and R-ant-7 is that the cut is either named or derived-and-REPORTED, never
defaulted to φ = 0. The derivation reads the major axis of the current moment's polarization ellipse,
`φ_c = ½atan2(2Re(M_x M_y*), |M_x|² − |M_y|²)`, and refuses when there is no dominant linear axis
(`minor/major` past 0.25 — a circularly polarized structure reads **1.0 exactly**).

- **It must be evaluated at the PEAK DIRECTION, not at k = 0.** At k = 0 the transform is the plain
  dipole moment and the sum is the structure's total current moment, which **cancels to round-off on
  anything electrically long**: on a 3 λ_g line the integral of a standing wave over whole periods is
  zero, so the axis would be read off noise. At the peak the transform is by construction the current
  that made the largest field there, so it cannot vanish — and for a broadside peak the two agree,
  which is why a patch is unaffected either way.
- **An axis is a LINE, so fold it toward the peak's azimuth.** Unfolded, the 3 λ_g line (peak at
  θ = 31°, φ = 0°) reported φ = 180.00° — the right plane, back to front, because the transverse
  rooftops across the line's width give the ellipse a cross term of round-off size and a SIGN. The
  cut's own peak then landed in the negative-θ branch.
- **A φ-grid tolerance must be half the FINEST step, not the coarsest.** A half-circle grid's 225°
  wrap-around gap is the symptom, not a step; counting it made such a grid look finely sampled and
  accepted a back azimuth 45° from where the cut needed one.

**And the cut dominates the answer**, which is the whole reason it may not be guessed: on the measured
patch the **E-plane reads 157° and the H-plane 84°**. The E-plane figure is also a good illustration
of the infinite ground plane — the pattern only reaches zero at exact grazing, so the half-power point
sits near 78° where a finite board puts it nearer 40°.

### The staged front-to-back refusal, and the proof that it is one predicate

`FrontToBackDb` is in the registry from this phase, refuses with its own sentence naming the reason
(the ground plane and every dielectric layer are laterally infinite, so there is no lower hemisphere)
and the phase that supplies it, and — the part that makes the staging real — **its `Evaluate` is
written and already correct**. `FrontToBack_BecomesAvailableOnTheOnePredicate_AndItsValueIsAlreadyRight`
hands the same registry entry a pattern whose θ axis reaches 180° and gets 20.000000 dB out of a
hand-built 100:1 ratio. The finite-ground phase moves `PlanarFarFieldGrid.MaxThetaDeg` and nothing in
the picker, the exporter or the cube emission changes. `LayeredMedium.CanHost`'s rule holds: the
refusal is narrowed, never deleted.

`DirectivityPeakPhiDeg` is refused the same way at a broadside peak — every azimuth names the same
direction there, and reporting the first grid value would look like a measurement of a direction. The
θ cube says where the peak is, so the refusal is informative rather than a hole. (On the edge-fed
patch the peak is at θ = 1°, not 0: the feed breaks the x symmetry, and the azimuth is therefore
published.)

### Measured cost — reported, not made into a timing test

Release, 10 cores, 1°×1° hemisphere, the 2.4 GHz patch. **The whole registry costs 20-40 ms** against
a 287-2,429 ms pattern and a 58-1,961 ms solve, from N = 237 to N = 3,831 — roughly **1 %** of the
pattern at the top end, the surface-wave integral being nearly all of it. **That is why
`PlanarFarFieldSettings` has no switch to turn the metrics off**: every one of them is a post-process
of a pattern the run has already paid for, and a pattern with no numbers attached is half an answer.

### Other traps and decisions worth having written down

- **An efficiency above 1 REFUSES and names both numbers; it is never clamped.** A clamped efficiency
  reads as 100 % and hides exactly the kind of level error the itemisation exists to catch. The
  tolerance is 2e-3 — an order of magnitude above the worst measured lossless residual — and it exists
  only because the numerator is a quadrature and the denominator comes out of a factorisation.
- **Refusing `PowerSurfaceWave` must take `PowerDielectric` with it.** The residual's meaning depends
  on the term taken out of it, so publishing accepted − radiated under the dielectric name would be
  the double count the note warns about. The combined remainder is left for the caller to form from
  the two cubes that are published.
- **A barely bound mode keeps its energy in the AIR, so a lossy substrate does not make its pole
  lossy.** 1.6 mm FR-4 reads |Im k_ρ|/Re k_ρ = 1.2e-4 at tanδ = 0.02 and still only 3.9e-3 at
  tanδ = 1. Reaching the 0.05 residue ceiling takes a mode genuinely inside the dielectric — 8 mm
  εᵣ = 10 at tanδ = 0.3 puts TM₀ at k_ρ/k₀ = 2.65 and |Im/Re| = 0.20. **The ceiling is reachable but
  fires for no ordinary board**, which is where a ceiling belongs.
- **A metric is published as ONE cube over the whole sweep or not at all.** A `DataCube` has no
  missing-value concept, so a cube with a refused point would carry either a NaN — a hole that reads
  as data — or a fabricated number. Availability depends on the medium and the grid, both constant
  across a sweep; the one frequency-dependent case (the pole's loss ceiling) is named rather than
  half-published.
- **The beamwidth cut axis must AGREE across the set.** A derived cut is derived per pattern, and a
  structure whose dominant axis moves with frequency or differs between driven ports has no single
  plane to put on one axis. Averaging one in would be the guess §4 forbids, so a disagreement refuses
  the beamwidth for the whole set and says to name a cut.
- **`tests/Firewall.Tests` was ALREADY RED at ANT-4's commit**, and that is worth recording because it
  is the failure mode the user-facing-text gate exists to catch: ANT-4 added three
  `throw new …Exception("…")` sentences in `PlanarFarField.cs` and did not list them in
  `user-facing-text-allowlist.txt`, so `NoNewUserFacingTextIsAddedBelowTheUiFirewall` failed on 3 of
  them before this phase added a fourth. All four are internal invariants no user reads — a solution
  and a mesh that are not the same solve, the two spectral-kernel choices disagreeing, a merged cell
  the closed-form rooftop transform cannot describe — so all four are allow-listed, which is what that
  gate's own message says to do when a plain exception really is right.
- **ANT-4's interim `PlanarFarField.PowerBalanceNote` is RETIRED**, one day after it landed, because
  its own closing sentence ("the unaccounted term … which this phase does not itemise") stopped being
  true. `PlanarPowerBudget.Caption` is its strict superset, and carrying both would have put two
  sentences that contradict each other into one notes list.

### What was built

- `src/Engine/Mom/PlanarMetrics.cs` — `PlanarMetric`, `PlanarMetricAxis`, `PlanarMetricSettings`,
  `PlanarPatternPeak`, `PlanarBeamCut`/`PlanarBeamCuts`, `PlanarMetricContext`,
  `PlanarMetricDefinition`, `PlanarMetricOutcome`, `PlanarMetricReport`, **`PlanarMetrics.Registry`**
  and `PlanarMetricSet`.
- `src/Engine/Mom/PlanarPowerBudget.cs` — `PlanarSpectralVoltages` (the problem's own kernel at an
  arbitrary complex k_ρ and level pair), `PlanarGuidedModePower`, `PlanarSurfaceWavePower`,
  `PlanarSurfaceWaveLaunch`, `PlanarPowerBudget`.
- `src/Engine/Mom/PlanarBeamwidth.cs` — the cut derivation, the fold, and the four refusals.
- `PlanarFarFieldSettings.Metrics`, `PlanarSolveResult.Metrics`, the reports built beside each pattern
  in both sweep drivers (the only place the pattern, its currents and its raw admittance are in hand
  at once), and `PlanarKernel.AddMetrics` — **13 metrics in ONE registry**, published as cubes in
  ANT-4's `"farfield"` group, **and no new result type for the seventh phase running.**
- `tests/Engine.Tests/Mom/PlanarMetricsTests.cs` — 26 tests, **3 s**, all routine tier.

### Not done, on purpose

- **No independent volume integral for the dielectric loss.** Refuted above: over a laterally infinite
  substrate it is not a disjoint channel from the surface wave, so it would double-count rather than
  cross-check. The lossless balance is the cross-check, and it is a stronger one.
- **No axial ratio or polarization sense** — ANT-6's, and the circular-polarization case is exactly
  where this phase's beamwidth REFUSES and points at it.
- **No front-to-back number.** Staged, not omitted.
- **Nothing reaches the CLI or the GUI**, because ANT-4's far field does not either: the registry is
  what makes the picker, the listing and the exporter cheap when a presentation phase arrives.

## ANT-6 — polarization: Ludwig-3 co/cross, axial ratio, sense
### (brief-antenna-6-polarization.md, 2026-09-10)

Short arithmetic over ANT-4's `E_θ` and `E_φ`, one file, `PlanarPolarization.cs`, plus one extracted
function in `PlanarBeamwidth.cs`, one setting, and four cubes in ANT-4's own `"farfield"` group.

### The verdict, in one paragraph

**Every definitional decision the brief asked for was makeable and none of them needed an oracle,
because the whole phase is a ROTATION plus two rotation INVARIANTS — so the interesting work was the
§4 measurement, and it came out better than the brief expected in one direction and blocked in the
other.** The floor is real, it is **≈ −74 dB on the shipping default and it does NOT fall with mesh
refinement**, which is the signature that says "floor" rather than "accuracy"; with the edge mesh off
it is 27 dB lower and *falls* as the mesh coarsens, which is a different mechanism entirely. And the
staircase-vs-conformal half of §4 **cannot be measured today**, for two independent reasons that
between them cover every case: on a rectangular patch the two boundary models produce a
**bit-identical** mesh (R-cut-2 — a Manhattan mesh has no cut cells to differ about), and on artwork
where conformal cells *do* something, ANT-4's cut-cell refusal fires by name. Reported, not
worked around. 24 tests, **0.3 s**, all routine tier; nothing in this phase needs a solve except the
four tests that use one for realism.

### The decomposition, derived rather than transcribed — and the one thing that pins the signs

The brief's §1 gives the Ludwig-3 form and then says, correctly, **do not lift the algebra from
here**. Derived against ANT-4's own convention it comes out the same, and the reason to *believe* that
is not the agreement but the reduction: ANT-4 stores its pattern on the standard triad
(θ̂ = (cosθcosφ, cosθsinφ, −sinθ), φ̂ = (−sinφ, cosφ, 0)), so at θ = 0

    ê_co = θ̂cos(φ−φ₀) − φ̂sin(φ−φ₀) = (cos φ₀, sin φ₀, 0)      for EVERY φ

which is the azimuth φ₀ itself — the property Ludwig-3 exists to have, and what a range's two probe
orientations do. Nothing about the layered medium enters.

- **The rotation identity is NOT sufficient as a gate and is still worth having.**
  |E_co|² + |E_cross|² = |E_θ|² + |E_φ|² holds to **4.3e-16 … 6.1e-16** at every φ₀ tried, which says
  no level can move when the reference angle changes — but it is exactly the statement a SIGN error
  could not break. What pins the signs is the exact one below.
- **The gate that pins them: an x̂-directed element has zero Ludwig-3 cross-pol in the principal
  planes, and non-zero on the diagonals.** From ANT-4's own form, E_cross ∝ J̃_x·sinφcosφ·(f_TM − f_TE)
  about φ₀ = 0 — the element factors cancel out of the *statement*, so it holds for every θ and every
  stack and needs no substrate physics. Measured on 1.6 mm FR-4 at 5 GHz: the principal planes read
  **−324.7 dB** below peak co-pol at worst over every θ, and θ = 45°, φ = 45° reads **−20.85 dB**.
  **The diagonal half is what makes it non-vacuous** — a decomposition that returned zero everywhere,
  or swapped co for cross, passes the first half alone.
- **The exactness is a property of the ANGLE's floating-point representation, not of the algebra, and
  only φ = φ₀ gets it.** There `sin(0)` is exactly 0 and the cross component is an exact zero. At
  φ = 90° and 180° the mathematical zero is a *cancellation of two round-off-sized terms*, because
  π/2 and π are not representable — `cos(π/2)` is 6.1e-17 — so both of E_cross's terms are ~1e-17 of
  the peak and the result lands near **−358 dB** instead of at the sentinel. That is the right answer,
  and the test asserts a structural zero rather than an exact one at those two azimuths.

### R-ant-9 — φ₀ comes from ONE place, and it is the beamwidth cut's own axis

The brief's §2 asks for two sources and a refusal. The implementation adds a third requirement that
the brief does not state and that this directory's own habits demand: **the derived φ₀ must be the
same number ANT-5's derived beamwidth plane is.** "The dominant current axis" is one physical
quantity, and computing it twice would be two definitions of it — the thing
`PlanarCurrentDensity`'s header exists to prevent. So `PlanarBeamwidth.AxisAtPatternPeak` was
extracted from `PlanarBeamwidth.Cuts` and both callers read it; the test asserts equality rather than
agreement.

- The brief's §2 says "weighted by |J|²". That is satisfied in substance: the ellipse's principal
  values are quadratic in the current MOMENT, and the axis is the major axis of that |M|² form. A
  literal per-basis |J|² tensor has no well-defined cross term — x and y rooftops are not co-located —
  which is why the moment form is the one that exists.
- **φ₀ → φ₀ + 180° changes the SIGN of both components and neither magnitude**, so ANT-6 deliberately
  does NOT fold its axis toward the peak the way ANT-5's cut must (there the fold decides which half
  of a θ sweep is called positive). **This is load-bearing rather than tidy**: the measurement below
  reports φ₀ = 180.00° at one mesh density and 0.00° at the two others, on the same symmetric patch,
  because the transverse rooftops give the ellipse's cross term a round-off magnitude and a SIGN — the
  same trap `CLAUDE.md` already records for the beamwidth cut. Both answers are the same plane and the
  reported dB are identical.
- The ambiguity refusal reuses `AxisAmbiguityRatio` and the measured value is exact: a circularly
  polarized current reads **minor/major = 1.000000000000**. Its sentence points at `AxialRatioDb`, and
  that is the whole reason §3 put the axial ratio in this phase — **the refusal has somewhere to
  point.**
- **A derived φ₀ must AGREE ACROSS THE SWEEP or the co/cross pair is refused for the whole set**, in
  the same shape and for the same reason ANT-5's cut axis is: a `DataCube` has no missing-value
  concept, and a cube whose φ₀ changed halfway along its own frequency axis would be two quantities
  under one name with nothing in the axis to say so. `AxialRatioDb` and `PolarizationSense` are
  published either way, being rotation invariants.

### R-ant-10 — the sense, derived from the triad, and the axial ratio's stable spelling

**Sense conventions differ by a sign between disciplines**, so it is derived here rather than quoted.
For E(t) = Re{(Aθ̂ + Bφ̂)e^{jωt}}, (E × dE/dt)·r̂ = ω·Im{A·conj(B)} — constant in time, as it must be —
and IEEE right-handed is that being positive. The check that this is the right hand and not the left
is that (θ̂, φ̂, r̂) is right-handed exactly as (x̂, ŷ, ẑ) is, so the rule reads "x̂ − jŷ along +ẑ is
RHCP", which is the textbook statement in this time convention.

- **`PolarizationSense` is the normalised Stokes V**, `2·Im{E_θ·conj(E_φ)}/(|E_θ|²+|E_φ|²)` ∈ [−1, 1].
  Its sign is the sense and its magnitude is how circular the direction is — strictly more than a ±1
  carries, because a nearly linear direction reads ≈ 0 instead of being assigned a sense it does not
  have. Gated against the **time-sampled rotation of the real field vector**, which shares no algebra
  with it: two orthogonal elements in quadrature give s₃ = **+1.000000000000** / **−1.000000000000**
  with the oracle's own sign at ±4.688, both signs, axial ratio 0 dB to 1e-9.
- **The axial ratio is written as `AR = (T + √(T² − 4·Im²)) / (2·|Im|)`, and that is not a
  simplification.** The obvious spelling, (|E_R|+|E_L|)/||E_R|−|E_L||, subtracts two nearly equal
  magnitudes exactly where a nominally linear antenna's answer lives, and loses half its digits there.
  The stable form is exact instead: measured against 20·log₁₀(1/ε) at ε = 1e-1, 1e-3, 1e-4 it agrees
  to **0.00e+00 dB** at every rung. The exact relation to the sense, |s₃| = 2·AR/(AR²+1), holds to
  **1.1e-16** across the whole ellipse family — which is what says the two cubes are the ellipse's
  shape and its direction of travel rather than two estimates of one number.
- **Pure linear reads a NAMED SENTINEL of 100 dB, not ∞, not a NaN and not a plausible clamp.** ∞ or a
  NaN in a Real cube breaks every autoscaled plot and several export formats; a clamp at, say, 40 dB
  would make "linear" indistinguishable from "quite linear", which is what the brief's §6 forbids.
  100 dB is one part in 10⁵ of cross-circular amplitude — past any antenna, any measurement and any
  mesh — so it cannot be misread as a computed value, and the cube's note says exactly what it means.
- **The dB floor is −400 dB and it took two goes to size.** −300 dB was the first choice and it was
  wrong: a structure's *grazing* co-pol over a PEC plane is a genuine round-off zero that lands near
  −320 to −340 dB, and −300 dB clipped it — i.e. the floor was clipping real output. −400 dB (1e-20 of
  a volt against a pattern peaking at order 1) can be reached by nothing but an exact zero, so
  round-off zeros are now reported as the arithmetic produced them and only the exact ones floor.
  **A sentinel that sits above real data is not a floor, it is a clamp.**

### §4's measurement — the cross-pol floor IS the mesh, and the number does not improve

Symmetric edge-fed patch, 29.18 × 36.47 mm on the 1.6 mm FR-4 starter at 2.4 GHz, one frequency
point, staircase and conformal both run at every rung. The figure is the worst
`CrossPolLudwig3Db − peak CoPolLudwig3Db` over the two principal planes.

| cells/λ | edge mesh | N | y grid mirror-symmetric | principal-plane cross-pol | D |
|---|---|---|---|---|---|
| 10 | off | 58 | **yes** | **−105.3 dB** | 6.416 dBi |
| 20 | off | 237 | **yes** | **−87.6 dB** | 6.423 dBi |
| 40 | off | 955 | **yes** | **−78.0 dB** | 6.427 dBi |
| 10 | **on (the default)** | 178 | **NO** | **−75.0 dB** | 6.431 dBi |
| 20 | **on (the default)** | 387 | **NO** | **−73.9 dB** | 6.429 dBi |
| 40 | **on (the default)** | 1,139 | **NO** | **−73.5 dB** | 6.429 dBi |

Four things, and the second is the one worth keeping:

1. **With the edge mesh ON — the shipping default — the grid of a perfectly symmetric rectangular
   patch is NOT mirror-symmetric about the patch's own centre line**, at any density. That is §4's
   geometric hypothesis confirmed directly, and its mechanism named: the graded fan is marched from
   each rim independently and one march's partition is not the mirror of the other's.
2. **The floor there is ≈ −74 dB and it is FLAT in the mesh** — 1.5 dB across a 6.4× range of N. **A
   number that does not improve when the mesh is refined is a floor**; that is the whole distinction
   §4 asked for, and it is the answer to quote.
3. **With the edge mesh off the grid IS exactly symmetric, and then the number is a different
   quantity**: 27 dB lower at the coarsest mesh and *rising* with N (−105 → −88 → −78 dB). Nothing
   geometric is left, so what is being measured is round-off accumulating over the O(N) transform sum
   and the dense solve — it gets worse as there is more of it. **Both mechanisms are real and they
   push in opposite directions with mesh density**, which is exactly why the note tells a user to
   refine and watch whether the number MOVES rather than whether it falls.
4. **The floor is invisible in every other metric.** Directivity moves **0.015 dB** across all six
   rows. A user reading a −74 dB cross-pol as their antenna's has nothing else on the screen to warn
   them, which is why `MeshFloorNote` ships attached to both cross-pol cubes rather than living in a
   document.

**Staircase vs conformal could not be measured, and the reason is structural rather than a shortfall
of effort.** On the rectangular patch the two produce a **bit-identical** mesh with **0 cut cells** at
every rung — R-cut-2's own statement, that a Manhattan mesh is bit-identical under conformal cells —
so the comparison is vacuous there, not merely close. On artwork where conformal cells do anything (an
8-segment oblique taper: 138 cut cells, N = 1,936 against 1,942) **ANT-4 refuses the far field by
name**. Between them those two cases cover every input: the comparison needs the cut-cell transform,
which is named as not built. **§4's consequence for the series stands unchanged and untested by this
phase** — staircase quantisation of a 41.3 mm patch at a 2.4 mm cell is a ~6 % length error and
therefore ~6 % in resonant frequency, which is the headline number for an antenna — and this phase
does NOT flip `PlanarBoundaryCells.Staircase`, per §6.

### What was built

- `src/Engine/Mom/PlanarPolarization.cs` — `PlanarPolarizationReference` (φ₀, where it came from, and
  the ellipse it was read off), `PlanarPolarizationPattern` (the per-direction arrays, the captions,
  and `PrincipalPlaneCrossPolDb`), `PlanarPolarization` (the four rules, the derivation, the
  arithmetic, the `Cubes` table carrying each cube's unit and note), `PlanarPolarizationSet` (the
  set-wide reference-angle agreement and its refusal).
- `src/Engine/Mom/PlanarBeamwidth.cs` — `AxisAtPatternPeak` extracted so ANT-5 and ANT-6 read ONE
  derivation of the dominant current axis.
- `PlanarMetricSettings.PolarizationReferencePhiDeg` — null means derive-and-report. It names a
  reference DIRECTION and changes no cube's MEANING; a second *definition* of cross-pol would be a
  second cube (R-ant-8).
- `PlanarSolveResult.Polarization`, the pattern's polarization built beside its metrics in the one
  place the pattern and its currents are in hand together, and `PlanarKernel.AddPolarization` — **four
  cubes in ANT-4's `"farfield"` group on ANT-4's own `[freq, theta, phi, port]` axes, and no new
  result type for the eighth phase running.** The run's notes gain the polarization caption, the
  reference angle's provenance and the mesh-floor sentence.
- `tests/Engine.Tests/Mom/PlanarPolarizationTests.cs` — 24 tests, **0.3 s**, all routine tier.

### Not done, on purpose

- **No fifth cube for the principal-plane cross-pol ratio.** It is computed and it is in the caption
  and in this write-up, but the brief lists four cubes and the ratio is a REDUCTION of two published
  ones rather than a new quantity. A user subtracts two cubes; a plot of the ratio is ANT-7's.
- **No second definition of cross-pol, and no mode that could become one.** Ludwig's first and second
  are each a second CUBE if they are ever wanted (R-ant-8).
- **The co/cross cubes are ABSOLUTE dB (re 1 V of the r-normalised pattern), not normalised to peak
  co-pol.** A self-normalising cube would hide its own level, which R-res-8 forbids, and would mean
  something different at every frequency. The familiar "cross-pol is X dB below peak co-pol" is one
  subtraction of two published cubes, and the run's caption states it outright so the headline number
  is not left as an exercise.
- **No conformal-vs-staircase comparison.** Blocked structurally, above, and the blocking refusal is
  ANT-4's own, named.
- **`PlanarBoundaryCells.Staircase` is NOT flipped.** §6, and its recorded reason — reproducibility of
  every measured number in this directory — stands. The argument for conformal on antenna work is
  recorded here and ANT-12 should pick it for the shipped example.
- **Nothing reaches the CLI or the GUI**, because ANT-4's far field and ANT-5's metrics do not either.
  `PlanarPolarization.Cubes` is what makes a picker, a listing and an exporter cheap when ANT-7
  arrives: the note a panel must say in words is already written next to the name.

---

## ANT-9 — finding a high-Q resonance, and the never-add property narrowed (2026-09-10)

`brief-antenna-9-resonance-sweep.md`. **§2 was an explicit owner decision and it was taken: build the
opt-in search.** The alternative the brief offered — publish an *estimated* f₀ from a coarse pass and
make the user re-run with a seeded grid — was declined, and is not built.

### The two problems, still separate

1. **The criterion was fine and the grid was wrong.** Nothing here touches the refinement criterion.
   It is still an ERROR — a freshly solved point against the interpolant's prediction there — and
   never a fit residual; L7b-b and L8a each measured why, and a search that quietly moved it onto
   "how well does the model fit its own nodes" would have been that mistake a third time.
2. **The report was reading as a success.** Fixed independently of the search, per §4.

### What the search actually does

`src/Engine/Mom/PlanarResonanceSearch.cs`, pure — no solve, no kernel, no calibrator, the same rule
`PlanarAdaptiveSweep` follows. `PlanarAdaptiveSettings.Search` is **null by default**, and with it
null not a line of it runs.

- **Seeding costs no solve.** Im(Z_in) is read off the interpolant refinement already built and its
  sign changes are the candidates.
- **Every reported number comes off SOLVED points.** f₀ is the root between the two solved bracket
  ends, dX/df is that bracket's own secant, R is Re(Z) interpolated across it. The interpolant
  proposes; it never disposes. A reported f₀ that was really a spline's opinion about a region it had
  no samples in would be invisible — a plausible number, to four figures, wrong.
- **Two stages, in this order and not interleaved.** Stage A locates every resonance; stage B spends
  what is left resolving the curve around them on the sampler's own |ΔS| criterion. Locating is what
  the mode is FOR, so it takes the budget first — a cap that binds then leaves a full resonance list
  with a coarse curve, rather than a partial list.

### Q is ONE formula, and the sign is the label

At a crossing Z is real. The textbook writes series and parallel differently — `Q = ω₀(dX/dω)/2R` and
`Q = ω₀(dB/dω)/2G` — but at the crossing `Y = 1/R` exactly and `dB/dω = −(1/R²)·dX/dω`, so the second
reduces to the first with the sign the falling slope supplies. Therefore

> **Q = ω₀·|dX/dω| / 2R = f₀·|dX/df| / 2R in BOTH cases**, and the sign of dX/df is not part of the
> magnitude at all — it is the *label*, series or parallel.

One formula, no branch, and the branch that would have existed is exactly the thing most likely to be
written backwards. Verified against an analytic parallel tank: Q = 79.999 against a true 80, R exact.

For a one-port this is the resonator's **own** Q — radiation plus loss, everything inside Z_in — and
not the loaded Q of a matched system. The run's note says that in those words rather than leaving it
to be guessed from the letter Q.

### THE FINDING: regula falsi is the wrong bisection here, and it gives a WRONG Q

§3 says "bisect toward the crossing", which invites probing at the interpolant's own root estimate.
That was written first and it is wrong, for a reason specific to a resonance.

**X(f) through a resonance is very nearly linear**, so the first regula-falsi probe lands on the root
immediately and to many digits. That sounds ideal. It is not: it moves ONE bracket end onto the root
and leaves the other where it started, so **the bracket never shrinks**. Measured on the analytic
Q = 214 case:

| probe rule | added solves | final bracket | f₀ error | **Q reported** |
|---|---|---|---|---|
| regula falsi + midpoint safeguard | **24 — the whole cap** | ~60 MHz | exact | **281 vs a true 214, +31 %** |
| plain midpoint bisection | **15** | 122 kHz | 2.2e-10 | **213.99, 2.6e-5** |

Two separate costs, and the second is the dangerous one. The safeguard (any step that fails to halve
the bracket forces the next probe to the midpoint) meant two probes per halving, so the run burned its
whole 24-point cap — but worse, it *terminated* on a bracket 60 MHz wide, and **the secant of a 60 MHz
bracket is not dX/df at f₀**. It reported Q = 281 beside an f₀ that was exact to ten digits. A wrong Q
next to a right f₀ is the most credible-looking wrong answer this phase could have produced.

Plain midpoint halves the bracket every time: cost is exactly `ceil(log2(width/tolerance))` probes —
six from a 10 MHz grid at the default 1e-4 — which is both predictable and *cheaper* than the
safeguarded version, and it ends on a bracket tight enough that its secant IS the local derivative and
its endpoints ARE a proven interval. The interpolant still chooses which interval to open in; it just
no longer chooses where inside it to land.

### The second runaway: stage B has no grid to stop it

Phase 1's refinement terminates because it runs out of GRID — there is always a point it cannot bisect
past. Stage B has no grid, and the split-both-halves recursion is 2^k, so it consumed the entire
remaining cap on every sharp resonance and made "cap bound" mean nothing. Two bounds fixed it:

- a floor at **half the resonance's own half-power bandwidth** — scaling it to f₀ instead would spend
  the whole budget on a high-Q feature and almost nothing on a broad one, which is backwards;
- a **window of ±1.5 bandwidths**, with sub-intervals that fall outside it dropped rather than
  subdivided. Without that filter one grid interval straddling the window edge refines its far half to
  the floor as well.

At most six intervals, so stage B cannot swallow the cap. It can afford to be that thrifty because
stage A has already paid for dense coverage of the core: bisection leaves every probe it took inside
the original bracket, clustering geometrically on f₀. What stage B adds is the **flanks**.

### THE LIMIT, and it is the brief's own premise returning

**The search can resolve a resonance the solved points straddle in SIGN. It cannot conjure one whose
entire reactance excursion falls between two neighbouring solved points.** Measured while building the
driver gate: a 22 mm FR-4 line referenced to 15 Ω, swept 2–6 GHz in 9 points, has Im(Z_in) *negative at
all nine* — the crossing near 3.6 GHz lives entirely inside one 500 MHz step, and the spline through
nine negative values stays negative. Thirteen points straddle it and the search finds it at once.

This is ANT-9 §1's own diagnosis reappearing one level down, and it is not a defect to be tuned away:
detection needs a sign to bracket. The "no resonance found" note therefore says outright that it is
**not proof there is none**, and names the same remedy a non-converged sweep gets — a finer requested
grid. What the mode removes is the need for that grid to resolve the resonance; what it still needs is
for the grid to *notice* it.

### The report (§4), which happens whether or not the search is on

- **Leads with CONVERGED / DID NOT CONVERGE**, then the counts. It used to lead with the saving and
  bury the tolerance failure in the last clause — which is exactly how "44 of 51 point(s) were SOLVED"
  came to read as success next to a |ΔS| twenty times its tolerance.
- **`PlanarSolveResult.AdaptiveConverged`** carries the same verdict as a `bool?`, so a caller does not
  have to parse prose. `worstStopped > Tolerance` is the whole test: an interval that stopped inside
  the tolerance converged, one that stopped above it ran out of grid or budget.
- **Above 80 % solved the note says the adaptive path saved little here**, and at 100 % that it saved
  nothing. A percentage presented as a saving when it is not one is the same failure as a control that
  silently does nothing.
- **A non-converged run names what would help in the same sentence** — a finer requested grid, and, if
  the search is off, the search.

### Gates

`tests/Engine.Tests/Mom/ResonanceSearchTests.cs`, 11 tests. **Six are analytic and run in under 4 ms
in total**, which is the whole reason the search takes a `PlanarResonanceProbe` delegate: in
`PlanarSolve.Run` it is a fill-factor-excite-de-embed cycle costing a minute, in a test it is an RLC
evaluated in nanoseconds, and the search cannot tell the difference. A resonance search gated only on
solved structures compares one estimate against another and can tell they disagree but never which is
wrong.

| gate | result |
|---|---|
| high-Q found, f₀ and Q to a stated accuracy | f₀ 2.2e-10, Q 2.6e-5, 15 added solves |
| parallel resonance labelled, one formula | Q 79.999 / 80, R exact |
| two-resonance span returns both | **three** found — both tanks to 0.05 %, plus the genuine series antiresonance between them at R = 0.4 Ω |
| no resonance terminates and says so | 0 added, note leads "NO RESONANCE was found" |
| cap binds and is reported | 2 of 2, named, with what would lift it |
| added points inside the span, ascending | held |
| search OFF adds nothing, flags nothing, deterministic | held |
| search ON leaves the user's grid untouched | 11 requested + 4 added, all 11 solved points bit-identical to the search-off run |
| report leads with convergence | asserted as a PREFIX, not by splitting on the first full stop — the next thing the note says is a |ΔS| with a decimal point in it |

The driver gates are 3.0–4.6 s and are **deliberately untagged**: they are under the ~5 s `Benchmark`
threshold, they assert no timing, and they are the central invariant of the phase, so they belong in
the routine gate. B2 was trimmed from a 13-point grid to 11 to keep it there (5.3 s → 4.6 s).

**B2 was vacuous twice before it was right**, and both reasons are worth knowing. First it used the
matched 50 Ω fixture referenced to 50 Ω: Im(Z_in) is identically ~0, the search correctly finds
nothing, adds nothing, and a test of "added points are flagged" passed with no added point in it.
Then it used a mismatched line on too coarse a grid — the limit above. `Assert.NotEmpty(flagged)` is
what now stops it passing empty.

### Not done, on purpose

- **The real-board measurement from §1 has NOT been re-taken.** The imported patch board is not in
  this repository — it arrived through an owner report — so the 44-of-51 figure cannot be reproduced
  here. Everything the phase claims is gated on the analytic resonator and on the mismatched-line
  driver case instead. Pointing this at the board's `.cws` is one CLI run.
- **No far-field pattern at a found frequency.** `farWanted` is keyed on requested-grid indices and
  is untouched by the search, so a pattern is still only produced where one was asked for. Computing
  one at f₀ would be genuinely useful and is a different phase's decision.
- **The search reads ONE port.** Im(Z_in) of two ports crosses zero in two different places, and
  reporting the union as though it were a mode list would be inventing structure.
- **No mesh or physics change.** §6. The mesh is still sized at `MeshFrequencyHz`, not at the
  frequencies the search visits.

## ANT-11: a finite ground plane — the outline is read, and the estimate is REFUSED (2026-09-10)

`brief-antenna-11-finite-ground-and-front-to-back.md`, the last brief in the antenna series and the
most research-shaped. **Its §2 was built. Its §3 (the UTD edge-diffraction estimate) was measured and
refused. Its §4 (extending the θ axis to 180°) therefore did not happen, and the front-to-back refusal
was NARROWED instead of activated.** The brief's own §7 sanctions that outcome and names the one thing
that had to happen either way — §2's ground-size note — which did.

Gates: `tests/Engine.Tests/Mom/PlanarFiniteGroundTests.cs` (12 tests, 158 ms),
`tests/Engine.Tests/Mom/UtdHalfPlaneMeasurementTests.cs` (3 tests, 5 s),
`tests/Ui.Tests/Em/FiniteGroundOutlineTests.cs` (6 tests, 75 ms).

### 1. The measurement that refused §3, and it is the whole phase

§3's model is "the infinite-ground pattern illuminates the edge, the edge re-radiates". Its input is
therefore the primary pattern **at grazing** — `F(θ = 90°, φ_rim)`, the direction the rim lies in from
the source. **That quantity is not small. It is zero, exactly.** Measured on the element factors
directly, at θ = 90°, one conductor level on the slab's top surface:

| stack | kernel | \|f_TM\| | \|f_TE\| |
|---|---|---|---|
| FR-4 1.6 mm @ 5 GHz | one-slab | 1.22e-16 | 0 |
| FR-4 1.6 mm @ 5 GHz | general | 0 | 0 |
| FR-4 1.6 mm @ 1.74 GHz | both | 1.22e-16 / 0 | 0 |
| FR-4 203 µm @ 1.74 GHz | both | 1.22e-16 / 0 | 0 |
| GaAs 100 µm @ 30 GHz | both | 1.22e-16 / 0 | 0 |

The 1.22e-16 is `cos(π/2)` in floating point, not a physical value — it is now exactly zero in both
kernels (see §3 below). **Two independent routes to the same zero**: the TM factor carries a `cos θ`,
and the TE factor carries `(1 + Γ^h)`, which vanishes at `k_ρ = k₀` — L8a's own theorem, the same
identity that made DCIM's far-field failure structural. Stated without reference to either spelling:
**at grazing the top half-space's TM characteristic impedance vanishes and its TE characteristic
admittance vanishes, so a horizontal current at any height launches nothing along the surface.** That
form is why it also holds in the GENERAL stratified kernel, whose element factors come from a cascade
traversal and share no algebra with the one-slab expressions.

End to end on a real pattern, 203 µm FR-4 at 1.74 GHz, 5° × 15° grid, per-row peak relative to the
pattern peak:

| θ | max_φ U | dB rel peak |
|---|---|---|
| 0° | 3.485e-7 W/sr | 0.00 |
| 45° | 2.737e-7 | −1.05 |
| 80° | 2.115e-7 | −2.17 |
| 85° | 2.080e-7 | −2.24 |
| **90°** | **2.379e-35** | **−281.66** |

**The pattern is alive to within 2.2 dB of broadside one row in from the horizon, and is a hard
structural zero at it.** So the illumination is not "weak" or "under-resolved"; there is nothing there.

**And the one current direction whose grazing field does not vanish is the VERTICAL one** — a probe, a
monopole, an IFA — which `PlanarFarField.VerticalBasisRefusal` refuses by name. So the set of
structures whose pattern this kernel will compute and the set whose rim illumination is non-zero **do
not intersect**. That is a statement about the two refusals, not about any board, and it is why this is
not a "the measured example is a hard case" result.

**Why that is worse than inaccuracy, and why building it anyway would have been the wrong call.** An
estimate that is identically zero PASSES the brief's own strongest self-test — §5's "as the ground
outline grows, the corrected pattern must converge to the primary one and the F/B must grow without
bound" — **vacuously, at every ground size**, because corrected ≡ primary and F/B ≡ ∞ always. It would
have shipped as a capability that answers nothing, with a gate that cannot tell.

### 2. R-fg-5 — UTD is NOT the asymptotic method the brief budgeted its validity range for

§3.2 expected the validity range to come from UTD's high-frequency asymptotics: "on a ground plane of
a fraction of a wavelength it degrades… find where it breaks and refuse past it." **That premise is
false for the diffraction coefficient, and this is a correction to the brief rather than a
confirmation of it.** For a straight PEC edge — exterior wedge angle 2π, which is what the rim of a
thin plane is — the Kouyoumjian-Pathak coefficient WITH its transition function is not an
approximation to Sommerfeld's exact half-plane solution; it **is** that solution, rewritten:

| k·ρ | ρ/λ | soft worst | hard worst |
|---|---|---|---|
| 0.2 | 0.032 | 7.2e-15 | 7.1e-15 |
| 1.28 | 0.204 | 7.2e-15 | 7.2e-15 |
| 10 | 1.592 | 7.4e-15 | 7.2e-15 |
| 120 | 19.10 | 1.5e-14 | 1.5e-14 |

719 observation angles × 5 incidences × 18 distances × both polarizations, absolute against unit
incident amplitude (the diffracted field passes through zero, so a relative measure would divide by
it). Worst anywhere, in the C# gate: **1.59e-14** — `PlanarFiniteGround.MeasuredUtdHalfPlaneAgreement`
is 2e-14 and the test also asserts < 1e-12, so a tolerance loose enough to admit a genuinely asymptotic
coefficient cannot pass it.

**Recorded because it redirects a future taker's budget.** A refusal written against k·ρ for the
coefficient would be a refusal against nothing. What does bound a finite plane is elsewhere — see §6.

### 3. A live ANT-4 defect the same measurement turned up: NaN in the grazing row

**A buried conductor level over a stratified stack returned NaN at exactly θ = 90°, which is the last
row of the DEFAULT hemisphere grid.** `CanCompute` said Ok. The NaN reached `U`, then
`RadiatedPowerW`, then every metric built on either — directivity, both gains, radiation efficiency and
beamwidth all read NaN — and the only visible symptom was the scale caption reading *"integrates to
NaNW over the hemisphere"*. Measured on a 203.2 µm + 500 µm stack at 1.74 GHz.

Cause: that configuration reads `f_TM` off the CROSS-REGION voltage, whose generalised transmission
factor is `2/((1+Γ) + (Z_a/Z_b)(1−Γ))`. At grazing `Z_b` is the top region's TM characteristic
impedance, which is exactly zero. `SpectralGreens.ZRatio` is cross-multiplied *precisely* so that a
vanishing or diverging Z cannot produce a NaN — and the transmission factor then divides by the ratio
again and loses the guard. CLAUDE.md's existing trap ("`LineResponse` forms `Z^h` on exactly one of its
two paths… the other choice is a NaN at grazing") had the right shape and covered `f_TE` only; the TM
cross-region path was the case nobody asked.

Fixed as an **explicit limit in `FarFieldElementFactors.At`** — both factors return exactly zero at
grazing — rather than in `LineResponse`, because that is where the answer is known (§1's theorem), it
covers all four combinations of kernel and level position at once, it makes the one-slab and general
kernels agree exactly instead of 1.2e-16 apart, and it leaves L9a's pinned bit-identity untouched.

**THE GUARD HAS TO BE ON THE SINE, AND THE FIRST ATTEMPT ON THE COSINE CHANGED NOTHING AT ALL.**
θ = 90° arrives as `90 * Math.PI / 180`, which is `Math.PI / 2` exactly — and `Math.Cos(Math.PI / 2)`
is **6.1e-17**, not 0, because π/2 is not representable. `Math.Sin(Math.PI / 2)` IS exactly 1.0. So the
degeneracy must be detected in the spectral variable it lives in (`k_ρ` reaching `k₀`, which is what
makes `k_z0` exactly zero), never in the cosine. The test that caught it asserts the whole chain is
finite, not merely that a guard exists.

### 4. What WAS built — §2's outline, and the note

`PlanarGroundOutline` + `PlanarGroundExtent` (`src/Engine/Mom/PlanarGroundOutline.cs`), carried on
`PlanarProblem.GroundOutline` as an optional field, populated by `PlanarExtractor`.

- **R-fg-1 — a DESCRIBED BOUNDARY, never artwork.** Nothing meshes it, stamps it or solves with it;
  `RequiresGeneralKernel` deliberately does not read it. Gated as bit-identity: the same problem with
  and without an outline gives an identical pattern (every direction, exact equality) and identical
  values for every metric but front-to-back, whose SENTENCE is the one thing an outline may change.
- **R-fg-2 — it is the RETURN PLANE's own pour, not "artwork on any ground layer".** On the 4-layer
  starter, which has two designated planes, pooling them would report a 140 mm bottom pour's size for a
  run whose fields return through a 70 mm inner one — nearly 2× wrong, and it is the easy mistake
  because both are "the ground layer" in the technology. Selection is by band identity, made after
  R-em-4 has resolved the plane, and the note reports the two counts separately.
- **R-fg-3 — three measures, each named, and none called "the size."** On the measured board at
  1.74 GHz: bounding box **0.4063 λ₀** a side, equal-area diameter **0.4585 λ₀** (13% larger), margin
  beyond the patch **0.0599 λ₀**. **The box reads SEVEN times the margin**, and the margin is the
  electrically meaningful one — edge effects are set by how far the plane reaches beyond the currents,
  not by its absolute size. A negative margin (metal overhanging the pour) is reported as negative, not
  clamped: it means the artwork claims a return that is not under it.
- **The note is on every run that has an outline, not only a far-field one.** The analysis terminates
  on an infinite plane whatever was asked for, so a user reading Z_in off a 0.4 λ₀ plane is as entitled
  to the sentence as one reading a directivity. It states the physical size, the electrical size at
  BOTH ends of the sweep (a 1–20 GHz sweep spans a factor of twenty in every one of these numbers), the
  margin, and the three one-directional ways the published numbers are optimistic.
- **There is deliberately NO "your ground plane is big enough" verdict.** Neither measurement here
  yields a size threshold, so printing one would be a rule of thumb wearing a measurement's clothes.

### 5. R-fg-6 — the refusal narrowed, and the ANT-5 staging held

ANT-5 shipped `FrontToBackDb` present-and-refused with a sentence whose tail named "the finite-ground
phase" as the thing that would supply it. **That was a promise and it cannot be kept as written.** It
narrows twice over: the tail is now a function of the PROBLEM (`PlanarFiniteGround.CanCorrect`), so an
outline present and an outline absent get different sentences and the present one quotes the outline's
own size in λ₀; and it names a measured reason plus what would lift it, instead of a phase.

**The staging paid off exactly as designed.** The narrowing moved **one predicate** in ANT-5's registry
entry. Nothing changed in the metric's `Evaluate`, the registry's shape, the cube list, the exporter,
the CLI, the Data Display picker — or in `PolarPatternAngle.HemisphereNote` and
`PatternSurfaceGrid.ThetaAxisName`, ANT-7's and ANT-10's hemisphere notes, which derive from the axis
and were **not edited at all** (`src/Render` is untouched by this phase; their comments still name a
finite-ground phase as the thing that would move the axis, which remains true of whoever takes it).

`PlanarFarFieldGrid.MaxThetaDeg` is **still 90**, deliberately: extending the axis with nothing to fill
it would restore precisely the half-plot of structural zeros ANT-4 §4 exists to prevent.

### 6. What a future taker must measure — the two limits that DO bind, and neither is measured here

1. **The illumination.** It has to come from the **surface field at the rim** — a spatial-domain
   quantity (DCIM / `SommerfeldIntegral`), not a far-field one, which means importing a validated-range
   refusal ANT-4's R-ant-1 was built to avoid. At the measured board's rim, ρ_e ≈ 0.2 λ, that is inside
   `Dcim.ValidatedRhoOverLambda` = 1.0.
2. **The diffraction coefficient for the rim of a GROUNDED DIELECTRIC SLAB**, not of a bare conductor.
   The PEC half-plane coefficient (§2, exact) is the wrong canonical problem for the part of the
   illumination the surface wave carries, and using it would be the smooth-plausible-wrong failure this
   directory keeps refusing.
3. **Multiple (rim-to-rim) diffraction across the plane.** The neglected second-order term scales as
   `|D|/√w ~ 1/√(2π k w)`, which at the measured board's `k·w = 2.56` is of order **0.25** — the same
   order as the term that would be kept, i.e. ~2 dB. **That is an analytic estimate and it is labelled
   one.** The direct measurement was attempted and is DEGENERATE exactly where it matters: the
   rim-to-rim direction lies ON both of the far edge's shadow boundaries, where an individual cotangent
   is infinite and KP's own small-argument transition limits are needed — machinery only a shipped
   estimate would require. The attempt returned identically zero through a null-guard, which is worth
   knowing about before anyone repeats it.
4. **And the mechanism that actually dominates on a thin substrate is not representable at all**: the
   surface wave reaching a BOARD edge and diffracting. Every dielectric layer here is laterally
   infinite (`PatternedDielectric.Deactivate`'s own sentence, the overview's §2), so there is no board
   edge for it to reach, and meshing the ground plane does not create one — which is the brief's §1
   argument that the expensive path fixes the secondary mechanism and leaves the primary one exactly as
   absent as it is today.

### 7. Not done, on purpose

- **The ground plane is not meshed.** §6 of the brief, and §1's own argument for why taking it would be
  a different-size project that buys part of one mechanism.
- **No corrected pattern, no corrected metric set, no `FiniteGround*` cubes.** There is nothing to put
  in them; a labelled trace of zeros is worse than none.
- **§5's "known analytic case" gate (published UTD results for a source over a finite circular ground
  plane) was NOT run**, and no number was invented for it. That reference data is not in this
  repository, and the gate that replaced it — the KP coefficient against Sommerfeld's **exact** closed
  form — is strictly stronger about the thing it can reach, while saying nothing about the assembly.
- **The real imported patch board is still not in this repository**, so every number above is on
  hand-built fixtures or on the 4-layer starter, as ANT-9's own §"Not done" records for the same reason.

## ANT-12 — three metric defects the shipped example found, and the far field's first user (2026-09-10)

`brief-antenna-12-user-docs-and-example.md`. The brief is a docs-and-example phase, and the example is
what turned up the engine work: **ANT-4 through ANT-11 had never been run end to end by a user**, and
three of the numbers they publish do not survive that. Gates:
`tests/Engine.Tests/Mom/PlanarMetricsTests.cs` (30 tests, 3 s) and
`tests/Engine.Tests/Mom/PlanarPolarizationTests.cs` (25 tests, 0.3 s), both routine tier.

### 1. `RealizedGainDbi` published a number that was 15 dB wrong on a matched antenna

R-ant-6 read the mismatch factor off the **raw self-admittance** on the stated grounds that it is "the
same Y_jj and Z₀ everything else reads". That is right for `RadiationEfficiency` and both
directivity-shaped ratios, and it is the one place it is wrong, for a reason the note could not have
seen without a run: **every other metric here is SCALE-INVARIANT in the excitation, and the mismatch
factor is not.** η, D and G are ratios of two things the same 1 V delta gap produced; the mismatch
factor compares an absolute admittance against Z₀.

At a de-embedded EDGE port the raw admittance is the **delta gap's**, not the antenna's — the gap's own
series parasitic is precisely what the error box removes — so the raw Γ sits near 1 whatever the
structure does. Measured on the shipped 5.8 GHz patch at its best-matched grid point:

| | |
|---|---|
| published, de-embedded S₁₁ | **−14.31 dB** → mismatch factor 0.963, −0.16 dB |
| raw self-admittance Y₁₁ | 163 µS → 4·Re(Z₀)·Re(Y)/\|1+Z₀Y\|² = **0.032**, −15.0 dB |
| `GainDbi` | 4.66 dBi |
| `RealizedGainDbi`, as published | **−15.05 dBi** |
| realized gain, correctly | **4.50 dBi** |

**The raw number is not merely pessimistic, it is impossible**: for a lossless 50 Ω lead terminated in
the de-embedded 252 Ω the input Re(Y) is bounded into [3.9 mS, 100 mS], and 163 µS is two orders below
that floor. So it cannot be a port admittance at all.

**Refused, not fixed.** `PlanarMetricContext` gained `PortReflection` (nullable); `MismatchFactor` is
now `1 − |Γ|²` from that and nothing else, and `RealizedGainDbi` refuses while it is null — the
FrontToBackDb staging reused verbatim, one predicate, picker and exporter still plumbed. Nothing
supplies it yet, because the de-embedded S is produced one step AFTER the pattern is taken and both
sweep drivers would have to be re-ordered to hand it over; the refusal names that as what would lift
it. **The refusal carries the arithmetic and it is EXACT**, not an approximation:
`GainDbi + 10·log₁₀(1 − |S₁₁|²)` from the two published results.

### 2. The beamwidth refused a whole sweep over two names for ONE plane

`PlanarMetricSet.From` compared `CutsPhiDeg` with `SequenceEqual`. A cut runs from −θ_max through
broadside to +θ_max with the negative half on the φ + 180° branch, so **90° and 270° sample the same
two half-planes and give the same beamwidth** — but they are different doubles, and the whole sweep's
`BeamwidthDeg` was refused.

**The cause was upstream, and it is worth naming.** `PlanarBeamwidth.Cuts` folds the derived current
axis to face `context.Peak.PhiDeg`. At θ_peak = 0 every azimuth names the same direction — which is
exactly why `DirectivityPeakPhiDeg` refuses there — so the fold was conditioned on a quantity the run
itself declines to report, and it flipped at the two frequencies where the peak search landed on a
different grid azimuth. **A broadside peak now takes the canonical representative in [0, 180)**, and
the set comparison is modulo 180° as well, because a plane is a line.

### 3. The Ludwig-3 pair refused on 89.999° disagreeing with 89.999°

`PlanarPolarizationSet.From` compared derived reference angles at **1e-9°**, which is exact equality on
the output of an eigen-decomposition. On an ordinary symmetric patch the axis wobbles by round-off
across frequency, the pair was refused for the whole sweep, and the refusal printed its own two
angles as `89.999°` and `89.999°`.

`ReferenceAgreementDeg` is **0.01°, sized from what the decomposition does with it**: a reference off
by δ leaks co-pol into cross at 20·log₁₀(sin δ), which at 0.01° is −75 dB — some thirty dB below the
≈ −45 dB cross-pol floor the MESH itself sets (ANT-6 §4). Two decompositions that close are
indistinguishable in the result they would produce. A reference that genuinely rotates still refuses,
and the comparison is modulo 180° for the same reason as the cut's: φ₀ and φ₀ + 180° give the same
|E_co| and |E_cross|.

### 4. What the example run confirmed, and one thing it refuted

- **The far field is right on an antenna, against an independent oracle.** Resonance search f₀ =
  5.81306 GHz against the cavity model's 5.7968 GHz — **+0.28 %** — with Q = 35.1 at R = 38.9 Ω and a
  −10 dB bandwidth of 91 MHz. D = 6.70 dBi at broadside, η_rad = 62.6 %, E-plane 3 dB beamwidth 146°.
- **A pattern costs ~6.4 s at N = 1,611 on a 1°×1° hemisphere**, against ~4.6 s for the de-embedded
  solve it rides on — measured as the difference between a 21-point sweep with one pattern (1 m 37 s)
  and the same sweep with 21 (3 m 45 s). Cheap enough that `EmRunService` asks for a pattern at every
  requested frequency rather than at `freqs[0]`, which is the bottom of the sweep and the one
  frequency nobody wants (22.6 % efficiency there against 62.6 % at resonance).
- **REFUTED: `brief-antenna-0-overview.md` §2's "probe-fed is a particularly clean fit".** It is the
  one feed whose pattern this kernel cannot compute. An internal port drives ground-attachment bases,
  and `PlanarFarField.VerticalBasisRefusal` fires by name — measured on the same patch with the feed
  line removed and one `Internal` port at the inset point. The s-parameters are computed normally. So
  the set of feeds this kernel patterns is exactly the in-plane ones, and ANT-12's user page says so
  rather than repeating the overview.

## ANT review — realized gain wired, and a Can-list claim its own phase had refuted (2026-09-10)

A review pass over the whole ANT-1…ANT-12 series. Four things; two of them were statements that
contradicted a measurement taken in the same phase, which is the class of defect this directory
cares most about.

### 1. `RealizedGainDbi` is PUBLISHED, and the fix was an ORDERING, not an arithmetic

ANT-12 §1 correctly refused the metric rather than publishing a number read off the raw delta-gap
self-admittance (15 dB low on a matched antenna), and left it present-and-refused because "the
de-embedded S is produced one step AFTER the pattern is taken and both sweep drivers would have to be
re-ordered to hand it over". **Re-ordering one of them is three lines, and the other needed none.**

- The non-adaptive driver now calls `DeembedAt` BEFORE `FarFieldAt` for the same point and hands the
  published `S[j, j]` over. Nothing else about the point moves: the raw solve, the currents and the
  calibration run in the same order at the same frequencies, so R-adf-1's bit-identity is untouched.
- The adaptive driver needed no re-ordering at all — its far-field block already runs after the
  refinement loop, and `byIndex[i].S` is the replayed, de-embedded s-matrix of exactly that point.
- `PlanarMetricContext.PortReflection` stays nullable, because a caller that builds a context by hand
  (every gate in `PlanarMetricsTests`) has no s-matrix to give. That path still refuses, with the same
  exact-substitute sentence.

**The staging held in the direction it was built for.** ANT-5 wrote the entry, ANT-12 pointed its
availability at one predicate, and publishing it moved that predicate and nothing else — not the
registry's shape, not the cube list, not the exporter, not the CLI. Verified end to end on the shipped
5.8 GHz example through `circuitrf em` as a process (4 m 27 s): f₀ = 5.8131 GHz, D = 6.70 dBi,
G = 4.66 dBi, η = 62.6 %, and `RealizedGainDbi` now published at G + 10·log₁₀(1 − |S₁₁|²) = 4.50 dBi
against a published S₁₁ of −14.3 dB.

**`farY` was dead.** Both drivers populated a `Dictionary<int, Mat<Complex>>` of raw admittances that
nothing ever read — the residue of an earlier attempt at this same wiring. Removed.

### 2. `CLAUDE.md`'s R-ant-6 still stated the formula ANT-12 refuted

The standing-memory entry read "written as the accepted-over-available ratio
`4·Re(Z₀)·Re(Y)/|1 + Z₀Y|²` from the same Y_jj and Z₀ everything else here uses" — which is the exact
spelling ANT-12 measured to be 15 dB wrong, over the exact admittance it measured to be the wrong one.
The correction lived only in `RESOLVED.md` and in the code. **A `CLAUDE.md` rule that survives its own
refutation is worse than no rule**: it is the file a future implementer reads first, and this one told
them to re-introduce the defect. R-ant-6 now states `1 − |Γ|²` off the published de-embedded S_jj,
says why this is the ONE metric here not weighed against the raw admittance (every other is
scale-invariant in the excitation; the mismatch factor compares an absolute admittance against Z₀),
and names the refuted spelling as refuted so it cannot come back by symmetry with its neighbours.

### 3. The user pages said a uniform cover layer "is supported" — the same phase measured that it is not

`docs/user/src/reference/antennas.md` §Cannot and `docs/user/src/reference/mom-engine.md` both carried
**"A uniform cover layer is a fair model of a real one and is supported"**, while the worked example
three screens below on the same page said a 0.5 mm εᵣ 3.0 radome gives **bit-identical** s-parameters
because `BuildMediumStack` terminates the medium in air at the topmost analysis level. One page, two
answers, and the wrong one is on the Can list.

This is precisely what `brief-antenna-12` §1a wrote its "do not write the superstrate onto the Can list
from the refusals alone" rule to prevent: the sentence was drafted from what the refusals ALLOW, the
measurement was then taken and recorded honestly in `src/Design/RESOLVED.md` §ANT-12, and the drafted
sentence was never taken back out. Both pages now say the cover layer is discarded, that the run warns
by name, and that the published answer is the bare board's. `docs/user/reference/*.html` and
`assets/js/search-index.js` were patched to match rather than regenerated — **the next `DocGen` run
should be allowed to rewrite these three pages**, and its figure diff read against the three known
nondeterministic families first.

### 4. A duplicated comment block in `SurfaceMesher.Mesh`

ANT-2's fifteen-line detail-floor rationale was pasted twice, once before the artwork-bounds guard and
once after it, the second copy carrying the extent-cap paragraph the first lacked. The first copy was
removed; nothing else in the function changed.

### Still open, and deliberately reported rather than fixed here

- **A dielectric above the topmost analysis level is still dropped.** It is warned, not modelled, and
  the limit is in the EXTRACTION rather than in the physics — a uniform superstrate is an ordinary
  layered medium. Lifting it means `BuildMediumStack` carrying the bands above the top level and
  terminating the half-space above THEM, which changes answers and therefore needs ANT-4 §5.3a's
  stratified dipole oracle run over a covered stack before it can be trusted. Named as not built.
- **`brief-antenna-12` §3b's imported-board regression fixture was not shipped.** ANT-1/2/3 are gated
  by hand-built fixtures in code instead, which is sound and is what the anonymisation rule pushes
  toward, but the brief's gate ("the imported regression fixture extracts the metal it should, meshes
  under the ceiling, and carries no personal path or name") has no artefact behind it and that was not
  recorded anywhere.

---

## An imported patch board, run end to end — the azimuth seam (2026-09-10)

Owner request: take the four-layer FR-4 import that ANT-1 was written from, run it with the series
landed, and report and fix what the run turns up. Every number below is from that board — a 41.3 ×
49.4 mm inset-fed patch over a 70 × 70 mm plane 300 µm below it, εᵣ 4.4, tanδ 0.02 — through
`Cli em` in Release on a scratch copy.

**The series itself held.** ANT-1's stroke outlining put the feed back (`1 width-bearing Path
shape(s) were outlined into conductor artwork, contributing 4.217 mm² of metal`), ANT-2's detail
floor took the connector's 310 µm via lands out of the pitch decision, and ANT-3's sheet intent
meshed the patch as a radiator. What the run produced was one defect, in ANT-5/6's φ lookup.

### `PlanarMetrics.Nearest` measured an azimuth on a straight line, and the tolerance measured it round

`BeamwidthDeg` was **not published at all**, with:

> The beamwidth cut at φ = 359.989° cannot be taken on this grid … The nearest sampled azimuths are
> 359° and 180°, which is further than half a grid step (0.5°) from what the cut needs.

On the default 1°×1° hemisphere, φ = 0° is **0.011° away** from what the cut needed and 359° is
0.989° away. The lookup was `Nearest`, a linear `|values[i] − want|` scan, so it chose 359°; the
tolerance three lines later measures the miss with `Delta`, which **wraps**, and rejected the snap it
had just been handed. **Neither the grid nor the tolerance was ever wrong — the lookup and its own
test measured two different distances.**

**Why it fires on the commonest antenna this kernel has.** The cut axis is derived from the current
moment (R-ant-7/R-ant-9), a patch fed along x puts that axis at 0° or 180°, and round-off decides
which side of the seam it lands on — 179.99° here, whose front half is 359.989°. A fraction the other
way and it publishes. So this is a **coin flip on every x-fed patch**, and the two ANT-12 findings
above did not cover it: those were a SET comparison refusing two names for one plane, this is one
point refusing before a set exists.

`PlanarMetrics.NearestAzimuth` is the wrapped-delta twin, used at the three φ lookups —
`PlanarBeamwidth.Cuts`' front and back, and `PrincipalPlaneCrossPolDb`'s φ₀ and φ₀ + 90°, which has
the same latent failure one quadrant over. **It is a second method rather than a fix to `Nearest`,
because `Nearest` is also asked for a θ**, which is not cyclic and where wrapping 180° onto 0° would
be an outright wrong answer. `AntipodalIntensity`'s φ lookup takes it too; that one is safe today
only because `(φ_peak + 180) % 360` is exactly a grid value on a uniform grid, which is a property of
the grid rather than of the code.

Gated by `PlanarMetricsTests.ACutAcrossTheAzimuthSeam_SnapsToTheNearestGridline`, a hand-built
pattern with no solve, at 359.99° / 0.01° / 179.99°, asserting the snap AND that the pair either side
of the seam reports the same beamwidth — they are one plane.

### What else the board said, and none of it is a defect

- **The detail floor SATURATES on this board at divisor ≈ 60**, and the run says why rather than
  going quiet: λ_g/60 would be 1191 µm, but the floor is separately held at 2 % of the artwork's own
  extent, so 988 µm is where it stops and divisors 60/50/40/30/20 are one mesh. The unknown count
  reads 1,697 (edge mesh off) or 6,109 (on) at all five.
- **`MostCircular` reporting θ = 89° on a linear patch is not noise.** It was checked against the
  cube: that direction is 32.6 dB below the pattern peak, not the −282 dB the grazing row carries, and
  an axial ratio of 2.97 dB there is a real near-grazing ellipse. No level guard was added.
- **Cells per wavelength is inert on this artwork with the edge mesh off**, 10/14/20 giving one
  answer. That is correct and is not the dead-knob defect `PlanarCurrentModel` was added for: the
  imported outline's own boundaries already subdivide the grid more finely than λ_g/10 = 7.1 mm, so
  the λ cap never binds. With the edge mesh on it moves the mesh at every value.

---

## The radiation efficiency, in percent and in decibels (owner, 2026-09-11)

Asked for on 2026-09-11: publish the radiation efficiency as a PERCENTAGE, and publish a decibel
form of the same quantity beside it, that being how an antenna designer reads a loss.

`RadiationEfficiency` now publishes **100 · P_radiated / P_accepted** with unit `%`, and
`RadiationEfficiencyDb` publishes **10·log₁₀(P_radiated / P_accepted)** with unit `dB`. Both come out
of `PlanarPowerBudget.RadiationEfficiency`, which is unchanged and is still a fraction — this is a
change to what is PUBLISHED, not to any arithmetic, and `PowerRadiated / PowerAccepted` still closes
on it exactly.

**Why a second CUBE rather than a transform on the first.** The Data Display's dB transforms are
dB20 (a field) and dB10 (a power) applied to a cube's own numbers, and the cube's own numbers are now
a percentage: dB10 of 92 is +19.6 dB, not −0.36 dB. Publishing the decibel form is the only way both
readings are available and neither is a trap. The unit travels with the cube (`AddMetrics` has
carried it since ANT-7 §8), so a plot of the percentage is labelled `(%)` rather than leaving 92 to
be read as a ratio.

**One bound, one refusal.** Both entries take the same `EfficiencyAvailability` predicate, extracted
from the old inline lambda — an η above 1 is refused rather than clamped in both scales, because in
dB it would read as a positive gain out of a passive structure. Two copies of a range test is two
places for one of them to drift, which is the rule `MismatchUsable` already states next door.

**The name is `RadiationEfficiencyDb`, not `…dB`** — the registry's own spelling everywhere else
(`FrontToBackDb`, `TrpDbm`, `DirectivityDbi`), and the cube name is what a `measure` line and a trace
spec type.

Callers that read the cube as a fraction have to divide by 100; the ones in this repository are
`PlanarMetricsTests` (the η·P_accepted = P_radiated identity and the TRP arithmetic) and
`AntennaExampleTests`, both of which now assert the percentage's unit and the dB cube's agreement
with it as well.

## Stop was advisory in four places it should have been binding — 2026-09-11

Owner report, the day after the Stop control shipped: Stop was selected from the Messages progress
bar during an EM run and the solver went on solving many more frequencies and would not stop.

Stop was read in the right places on the SEARCH (`PlanarResonanceSearch` checks it before every
probe, in both stages) and at the top of the fixed-grid point loop. What it was not read at was the
work in between — and all of it is full-wave points.

### 1. The refinement BATCH, which is where the reported wait came from

`PlanarSolve`'s adaptive refinement loop read the stop once per ROUND:

```
while (work.Count > 0 && solved.Count < budget && control?.StopRequested != true)
    foreach (var p in probes) { ...; Solve(p.Mid); }      // <- no stop check
```

A round is not one solve. Every interval that fails its tolerance splits in two, so the probe list
doubles each round and a late one is sixteen or thirty-two full-wave points at tens of seconds each.
Pressing Stop inside such a round meant waiting for every remaining probe in it. Measured on the
gate's own fixture, with the stop armed at the fourth solve: **9 points solved before, 6 after** —
and that is a 17-point grid on a coarse mesh, where a round is small.

Breaking mid-round is well formed: `taken` carries only the probes that were solved, the replay runs
over `solved` as it then stands, and each taken probe's error test reads its own solved matrix.

### 2. The SEED loop, with a floor of two

Seeds are full-wave points like any other. The floor is two rather than zero because everything
below — refinement, the search, and the interpolant the requested grid is published from — needs at
least two nodes to be a curve rather than a value.

### 3. The fixed grid, with a floor of one

A stop can genuinely arrive with nothing solved: the mesh and the core fill run before the first
point and on a large board are minutes of their own. Verified before the fix — the run did not
throw, it published **zero points**, which is not "keep what you solved" but an empty answer, and
`EmRunService.ResolveSnpPath` is predictable by design, so it would have been written straight over
whatever `.snp` the last good run left there.

### 4. The FAR FIELD — it took three cuts, and the middle one is the instructive failure

The block had no stop check either, and because its stage row walks the frequencies
(`far field (N pattern(s)) — 5.8 GHz`) it also LOOKED like a run still solving them.

**Cut 1** declined the remaining patterns outright, on the reasoning that a pattern is work and Stop
declines work. Refused the same day: stopping an EM simulation must still produce far-field output
that can be plotted, and a Stop returning s-parameters alone hands back the half nobody on an
antenna was waiting for.

**Cut 2** took the whole block regardless of the stop, on the reasoning that it **solves nothing** —
every pattern is an exact sum over the basis currents of a point that is ALREADY SOLVED, so it is
the PROCESSING of what the run has rather than more of the work Stop declines. That premise is true.
**The conclusion does not follow, and the number that refutes it was already written thirty lines
away in `EmRunService`**: ANT-12 measured a 1° × 1° hemisphere at N = 1,611 costing **~6.4 s per
pattern against ~4.6 s for the de-embedded solve it rides on**. At one pattern per solved point the
far field is therefore the LONGEST block in the run, not a tail on it. Reported 2026-09-11 on a
101-point patch antenna (N = 2,705): Stop pressed ~25 minutes in, with the block at **71 of 101**
and still climbing — the same "I pressed Stop and it kept going" for the second time, now caused by
the fix for the first.

**"It solves nothing" answers the wrong question.** Stop is not about what KIND of work is
outstanding, it is about how long the user waits after pressing it. A block that is minutes long is
work whatever it is made of.

**Cut 3, shipped:** the stop is read at the PATTERN boundary, with a floor of ONE. That floor is
what survives of the refusal of cut 1 — a stopped antenna run still publishes a far field somebody
can plot — and everything above it is what the stop is for. `farPatterns` is keyed by index and
everything downstream walks its KEYS, so a short set was already well formed; the fixed-grid path
has always published a subset this way.

Which of the two happened has to be SAID, because the cubes are indistinguishable — a far field over
every solved point and one over the first few of them differ only in how many slices the frequency
axis carries. The CUT SHORT note names the count, the band actually covered, and the fact that
patterns are taken in ASCENDING frequency, so what is missing is the top of the band — on a resonant
structure quite possibly the resonance itself.

### The convergence verdict a stopped run must not claim

`worstStopped` is a maximum over the intervals refinement actually reached. On a run cut short that
is a maximum over a set that stopped growing, and `converged = !(worstStopped > tolerance)` read it
as CONVERGED — on the first gate run, a sweep stopped during SEEDING (`worstStopped` still 0)
reported "refinement had already met its tolerance". `refinementStopped` is now recorded where it
happens, `AdaptiveConverged` is **null** on such a run, and the note reports the number it has
together with what it is a maximum over. The "what would help: a finer grid" advice is suppressed
there too: the remedy on a stopped run is not to change the sweep, and sending someone to do that
costs them a re-run to discover it was never needed.

### A bare counter is not readable, and the far-field row proved it

Same report. The two rows read:

```
EM 'square_patch_antenna_gerber' 101 point(s) solved — stopping
EM 'square_patch_antenna_gerber' — far field (101 pattern(s)) — 7.3 GHz (stopping) 71 / 101
```

Three numbers, **two of them the same by coincidence** — the sweep's 101 is the requested frequency
grid, the far field's 101 is its pattern count, equal only because `EmRunService` asks for a pattern
at every requested frequency — and the one the eye lands on, the trailing `71 / 101`, carries no
noun at all. The reasonable reading, and the one reported, was that the second row was the resonance
search announcing a total it cannot possibly know.

Two changes, and they are separable on purpose:

- **`RunProgress.StageUnit`** — a stage declares what ONE of its sub-units IS (`BeginStage("far
  field", n, "pattern(s)")`), and the row renders `71 / 101 pattern(s)`. It survives every
  `TickStage(nextLabel:)` relabel because the unit belongs to what is being COUNTED, not to the
  label that happens to be showing. Empty is the default, so every caller that does not set one
  renders exactly as before. It does not fight `FormatCounter`'s fixed-width right alignment: the
  suffix is constant for the whole stage, so the `/` and the denominator stay pinned, inset by one
  constant word.
- **The count came OUT of the label.** Saying `101` twice on one row is what made the reader look
  for two different meanings. The label is the changing part of the stage row by design, so it is
  now just `far field — 7.3 GHz`.

`replaying calibration` and the per-point `far field at <f>` block declare `point(s)` and `port(s)`
for the same reason.

### And the rows say "stopping"

Part of "would not stop" is that nothing on screen said otherwise. Work in flight still has to
finish, so the bars go on moving — correctly — and the only evidence the button had been heard was
one Messages line already scrolling away above them. `ReportEmProgress` now takes the run's own
`StopRequested`: on the sweep row it rides in the TRAILING counter (`3 / 101 — stopping`), never in
the text left of the bar, because text that grows to the left moves the bar; on the stage row, whose
label is the changing part by design, it goes in the label.

### Two things a stop must not do silently

A stop arriving after refinement — during the far field, or just before the search — used to set
nothing at all, so the run published a complete-looking result with no sentence saying the button had
been pressed. Worse, the search's own guard (`control?.StopRequested != true`) declined it silently:
a user who asked for a resonance search and got a result with no resonances in it had no way to tell
that from a run that looked and found none. Both are said outright now, and `stoppedEarly` is set
once more before publishing so the STOPPED EARLY sentence is never missing.

### Gates

`ResonanceSearchTests` — `AStoppedAdaptiveSweep_TakesNoFurtherProbesInTheRoundItWasStoppedIn`
(verified to catch the regression: reverting the one check takes it from 6 solved to 9),
`AStopAskedForBeforeTheFirstPoint_StillPublishesOne`, and
`AStoppedRun_KeepsTheFarFieldItTook_AndStopsTakingMore` (a stop while SOLVING still yields a
pattern — the floor; a stop landing inside the block leaves it strictly shorter than the free run's,
every pattern in it whole, and the CUT SHORT note present).
`EmRunProgressTests` covers the two rows' "stopping" readout, that the sweep row's left text and bar
position do not move when it appears, and that the stage counter carries its declared noun while a
stage declaring none renders exactly as before. All routine-tier — the slowest is 3.9 s.

---

## The far field ran BEFORE the search, so a found resonance had no pattern (2026-09-11)

Owner report on an 81-point patch with adaptive sampling, the resonance search and the radiation
pattern all on. Two separate things came out of it.

### The pattern at the resonance — a real hole, now filled

`PlanarSolve.Run` takes its far field over the grid and only THEN runs the resonance search. The
three pattern stores were keyed by grid INDEX, and a found point is by definition not on the grid, so
it had nowhere to put one. The run therefore published a pattern at every point the sampler happened
to solve and **none at the frequency the search went and found** — which on an antenna is the one
frequency both switches were turned on for.

Filled additively: a second set of stores keyed by FREQUENCY, one pattern per located resonance taken
after the search, merged into the published set by a stable sort. Two things this deliberately is
not:

* **Not "move the far-field block below the search".** That block maps each REQUESTED far-field
  frequency onto the nearest solved point, so with the found points in the solved set a requested
  grid frequency would start being answered by a pattern taken somewhere the user never asked for,
  silently, and the pattern set would stop being reproducible from the request alone.
* **Not a pattern AT f₀.** f₀ is a root located BETWEEN solved points; a pattern is an exact sum over
  basis currents and there are none there. It is taken at the nearest SOLVED frequency — inside the
  search's own reported bracket — and the offset is reported against that resonance's half-power
  bandwidth.

The currents and raw admittance of a search-added point were being dropped on the floor
(`Probe` stored only Raw/Kernel/Time); `extraCurrents`/`extraY` keep them, which is what makes any of
this possible.

### The three numbers that read as one number

The report was "why does it think it needs an extra 75 points?" It did not. The far-field row counts
PATTERNS — one per already-solved point, zero new solves — and it sat under a sweep row reading
"75 point(s) solved", so the two 75s read as 150 points. The resonance search, the only block that
adds a frequency at all, had not started; it runs last and is capped.

The counter was already honest and that was not enough: nothing had said the block was coming. The
start line now names the blocks after the sweep, in the order they run, one short clause each —
and quotes **no duration**, because a full-wave point's cost belongs to the machine.

Worth knowing, and NOT a defect: adaptive sampling saved 6 points of 81 here. Refinement bisects grid
indices and stops only when the interpolant's prediction at a midpoint matches the solve to within
the tolerance; across a patch resonance adjacent samples of an 81-point grid differ by far more than
that, so nearly every interval splits to the floor. Adaptive pays off when the grid OVERSAMPLES
relative to how fast S moves. `EmRunSummary` already says this at the end of the run.

### Gates

`ResonanceSearchTests.AFoundResonance_GetsAFarFieldPatternOfItsOwn_AtAFrequencyNotOnTheGrid` — the
gate is a far-field frequency that is **not on the requested grid**, which can only have come from
the search, and which must lie inside a reported resonance bracket. "More slices than before" would
have passed on an extra grid point. Measured on the mismatched-line fixture: 11 grid slices plus one
at 3.5750 GHz for the resonance located at 3.56511 GHz. Routine tier.
`EmPanelDeclutterTests.RunStartText_NamesTheBlocksThatFollowTheSweep_AndQuotesNoDuration` holds the
plan clauses and scans the sentence for any spelling of a duration.

## PCAL4 — a calibration GROUP and a modal error box; the coupled pair the series opened on now runs (2026-09-12)

`docs/sonnet-briefs/brief-portcal-4-modal-error-box.md`, the last of the series and the expensive
one. The case: two ports on conductors that are coupled AT the reference plane. PCAL1 measured it at
**22.6 dB out in S₂₁ at 1 GHz and non-passive at 48 of 51 points**; PCAL2 made it a refusal; PCAL3
could not reach it, because reproducing the neighbour in the standard does not give D6's per-port
SCALAR error box a second mode to describe.

**It works, and on all three committed fixtures the de-embedded answer is BELOW the kernel-A-vs-
kernel-B agreement floor at every frequency**, against a gate that only asked for within 0.005 of it:

| fixture | what it is | A-vs-B floor | before (PCAL2's refusal, overridden) | **after** |
|---|---|---|---|---|
| `coupled-pair` | two 254 µm lines, 246 µm apart (s/h = 0.27), 4 ports | 0.0521 | 0.985 | **0.0454** |
| `coupled-asym` | 254 µm beside 508 µm, same gap, 4 ports | 0.0521 | — | **0.0456** |
| `coupled-triple` | 254 / 432 / 660 µm, same gaps, 6 ports | 0.0521 | — | **0.0469** |

Per frequency on the pair, against that 0.0521 floor: **1 GHz 0.0303 · 2 GHz 0.0296 · 3 GHz 0.0331 ·
4 GHz 0.0374 · 5 GHz 0.0408 · 6 GHz 0.0434 · 7 GHz 0.0454.** S₂₁ was 22.64 dB adrift at 1 GHz and is
**0.04 dB**; S₁₁ was 19.01 dB adrift and is 2.08 dB, against the floor's own 3.13 dB there.

Everything below was measured with the committed fixtures through the real `circuitrf em` verb
against the cross-section kernel on the same files, in Release — not a test, per the standing rule
about measuring with a harness rather than the `Benchmark` tier. The floor was re-measured for each
fixture by moving its conductors 9 mm apart and comparing the two kernels there; all three read
**0.0521**, which is PCAL1's own 0.0522 to the last digit it quoted.

### 1. The algebra, and the one structural fact a simulator has that a lab does not

Real multiline TRL needs a reflect standard because its two error boxes are unrelated. **D4 builds
both of a standard's boxes as exact mirror images**, so with X the left box's 2N×2N wave cascade and
F the forward/backward flip, `Y = F X⁻¹ F` — exactly, as the general reverse-a-network identity. That
is what turns multimode TRL from an optimisation into a closed form here, and it is asserted rather
than assumed (`PlanarModalCalibrationTests.TheMirrorErrorBoxIsTheFlippedInverse`, 1e-16).

Two similarity statements with the SAME W = X·blkdiag(Ψ,Ψ) follow, and between them they are the
whole method:

    M ≡ T(ℓ₂)T(ℓ₁)⁻¹ = W·blkdiag(E(Δℓ), E(Δℓ)⁻¹)·W⁻¹            E(ℓ) = diag(e^{−γ_m ℓ})
    T(ℓ₁)·F          = W·[[0, E(ℓ₁)], [E(ℓ₁)⁻¹, 0]]·W⁻¹

- **D5' — γ is still a closed form and still needs no eigensolver.** det M = 1 makes M's
  characteristic polynomial PALINDROMIC, so substituting s = μ + 1/μ = 2cosh(γΔℓ) reduces degree 2N
  to degree N. At N = 1 that reduction *is* `cosh(γΔℓ) = ½tr(M)`. Coefficients by
  Faddeev–LeVerrier, roots by Durand–Kerner, both written here for the reason L8a's Bessel functions
  and D5's own `acosh` were. **The constant term of the reduced polynomial is c_N and not c_N·q₀**,
  and the factor of two between them is invisible in every other property the polynomial has — it
  cost the first day of this work and is now its own gate.
- **D6' — the eigenvectors leave 2N scalars free; the second statement pins N of them** (one ratio
  per mode) and says that **4N²−2N other entries must vanish**, which is `CascadeResidual`.
- **The last N are pinned by RECIPROCITY**: the box is reciprocal only for the per-mode rescaling
  `B² = (A12⁰)⁻¹(A21⁰)ᵀ`, **and that right-hand side must come out DIAGONAL** — forced by Ψ being
  complex-orthogonal, and its off-diagonal is `GaugeResidual`. That prediction was not obvious in
  advance and it holds to 1e-14 on synthetic data and to 1e-10 on the fixture.
- **(5) leaves a SIGN per mode and no bilinear condition can ever remove it** — reciprocity, the
  symmetry of A22 and the de-embedded line are all quadratic in the modal gauge. It is removed by the
  only linear comparison available (§3) and its margin is reported.

### 2. WITHOUT THE PRE-PEEL THIS FAILS OUTRIGHT, AND THAT IS THE FINDING WORTH MOST HERE

Forming T from S divides by S_RL. A port is a series delta gap, so `a₂₁ ∝ ω`, and at the bottom of a
band |S_RL| ~ 1e-4 and T ~ 1e4. M = T₂T₁⁻¹ then has O(1) entries computed as a cancellation of order
|S_RL|^{−2N}: **1e8 at N = 1, which is D5's own documented low-frequency amplification and
survivable, and 1e16 at N = 2, which is every digit a double has.**

Measured, on the fixture, before and after:

| at 1 GHz | palindrome | cascade | gauge | null-space gap | ε_eff reported | worst σ_max | non-passive |
|---|---|---|---|---|---|---|---|
| no pre-peel | 0.732 | 1.00 | 1.19 | 0.954 | **1.56 and 6.66** | **1.9081** | **4 / 7** |
| pre-peel | 2.7e-12 | 1.4e-10 | 1.4e-10 | 2.5e-9 | 2.718 and 3.073 | 1.0023 | 1 / 7 |

**The cure is a SIMILARITY, so it costs no accuracy at all.** Peel a crude per-conductor scalar box
off BOTH standards first: with X̂ block-diagonal and its mirror Ŷ = F X̂⁻¹F, the peeled pair is
`T̃(ℓ) = X̂⁻¹T(ℓ)Ŷ⁻¹ = (X̂⁻¹X)·T_line(ℓ)·(F(X̂⁻¹X)⁻¹F)` — the same structure with X replaced by X̂⁻¹X, so
every eigenvalue and therefore every γ_m is untouched, and the true box is recovered at the end as
X̂·X̃. The crude box is **D5 and D6 on the 2×2 sub-matrix of (port k at each end)** — the scalar
calibration of each conductor pretending the others are not there. It is not a physical description
of a coupled pair and does not have to be: **a preconditioner only has to be well SCALED.**

The synthetic gate is unchanged by it (machine precision either way), which is the point — it is a
conditioning fix, not an arithmetic one, and the gate that would have caught its absence is the
FIXTURE.

### 3. D7' — the reference impedance, and the R-gen-3a trap seen from the other side

`Z_c,m = γ_m/(jωC_m)` with `C_m = (Tvᵀ[C]Tv)_mm`, which at one conductor is D7 exactly. [C] and [C₀]
come from DIFFERENCING the two standards' own static capacitance MATRICES (N drives, one
factorisation per mesh per medium), [L] = μ₀ε₀[C₀]⁻¹, and Tv from
`ModalDecomposition.VoltageModalMatrix` — **the same GEVD kernel A's own general modal path uses**,
extracted for this rather than copied.

**The gauge requires `Ti = (Tvᵀ)⁻¹`, and that is NOT R-gen-3a's reporting convention.** Under the
strict biorthogonal normalisation the modal-to-terminal wave map is complex-ORTHOGONAL, which is what
makes (5)'s right-hand side diagonal; R-gen-3a instead takes `Ti_m = Tib_m/‖Tib_m‖²`, which for a
symmetric pair is exactly **2×** and is the convention a coupled-line designer and kernel A both
mean by Z_e and Z_o. Both are correct and they are for different purposes. **Publishing the gauge's
own value is R-gen-3a's own trap from the other side** — a reference impedance that is a plausible
number in units nobody else uses, and that would disagree with kernel A by a factor of two on this
very fixture. `PlanarModalMedium.ReportedZcScale` carries e_m and the run's notes print
`Z_c,m·e_m`.

Two further normalisations do NOT matter and nothing tries to fix them: Tv's column LENGTHS (scaling
column m by c scales C_m by c² and Z_c,m by 1/c², and the modal wave amplitude is invariant — the
columns are normalised to largest-entry 1 purely so the REPORT reads in ohms rather than at 1e8),
and the overall scale of the modal gauge.

**Against kernel A on the pair, which is a genuine cross-check because nothing here reads it:**

| | odd mode | even mode |
|---|---|---|
| Z_c, this file (1 GHz) | 71.53 Ω | 157.51 Ω |
| Z_c, kernel A | 65.11 Ω | 154.08 Ω |
| ε_eff, this file | 2.718 | 3.073 |
| ε_eff, kernel A | 2.5065 | 3.0358 |

+9.9 % and +2.2 % on Z_c, +8.4 % and +1.2 % on ε_eff — and **the odd mode is the worse one in both,
which is the A-vs-B floor's own signature**: the odd mode lives in the gap, where a 35 µm metal
against a zero-thickness sheet differs most (PCAL3 §5 measured the same thing from the other end).

**The SIGN of each mode is decided by the one linear comparison available**: A21 in the terminal
basis is Ψ·A21ˢ, and a feed region connects each port predominantly to its own conductor, so row m of
A21ˢ follows column m of Tv. The decision is taken on the PHASE of that correlation relative to the
mode with the largest one, because every mode's correlation carries the same unknown common factor.
It is a binary choice with a reported margin on [0, 1]; **measured at 0.95–0.995 on every fixture and
every frequency**, i.e. nowhere near the coin toss.

### 4. R-pcal4-4's separability, as a number rather than a caveat

γ is full-wave and C is quasi-static, so the de-embedding's accuracy and the reference impedance's
are two different things and the run reports them separately. On the pair the two routes' β disagree
by **at most 0.74 % (at 2 GHz)**, and the lossless modal reduction of [C] discards at most 5.9e-11 of
its own diagonal. A run where those are large has a perfectly good de-embedding and a reference
impedance that is out by about that much; one figure of merit would hide which.

### 5. What the passivity gate did NOT reach, and the two experiments that say why

Gate 1 asked for the answer to be passive across the band. **It is passive at 5 of 7 points and sits
0.23 % and 0.06 % over at 1 and 2 GHz** (σ_max 1.0023 and 1.0006 on the pair; 1.0022/1.0006 asym;
1.0027/1.0006 triple), against 0 / 7 for the same geometry with its conductors 9 mm apart. The
engine's own 1e-3 tolerance flags one point of the seven.

Two experiments, and they point in opposite directions, which is what makes them worth having:

- **Conductor thickness 35 µm → 1 µm.** |ΔS| collapses from **0.0454 to 0.0197** (and to
  0.0053–0.0070 above 3 GHz), so what is left of the |ΔS| residue IS the A-vs-B thickness gap and not
  this calibration. **And σ_max is bit-identical** — 1.0023 and 1.0006 at 1 and 2 GHz either way.
- **Mesh, cells/λ 5-across-2 → 10-across-4** (N 298 → 324, standards to N = 922). σ_max moves
  1.0023 → **1.0026**, i.e. not down.

So the passivity excess is **neither the A-vs-B floor nor discretisation**. It scales as f⁻² and
vanishes above 3 GHz, which is D6's own 1/a₂₁² amplification of the raw solve at the bottom of a
band, on a port region that now carries two modes instead of one. It is reported rather than fixed,
and it is smaller than the |ΔS| the run is already inside.

### 6. R-pcal4-7 — the cost, and it is the surprise of this phase

**A calibration group costs no more MESH than PCAL3's widened profile**, because the metal in the
standard is the same metal. On the pair, both are `N = 298 / 584 / 402 / 350 / 636 / 454` against a
DUT of 298 — **9.14× the DUT's unknowns, the identical figure PCAL3 records**, and 2 calibrations
over 4 de-embedded ports rather than PCAL3's 2 over 2.

Wall clock, 7 points, Release, same machine: **`coupled-pair-passive` (PCAL3, 2 ports) 9.6 s ·
`coupled-pair` (PCAL4, 4 ports) 10.4 s · `coupled-asym` 13.0 s · `coupled-triple` (6 ports, DUT
N = 447) 23.0 s.** So the group is **~8 % over PCAL3 for twice the ports.**

What a group actually adds is (a) N port excitations on a mesh that was going to be solved anyway —
multi-RHS on one factorisation — and (b) a SECOND electrostatic medium, because [L] = μ₀ε₀[C₀]⁻¹
needs the air-filled solve as well as the dielectric one. That doubles the electrostatic step, which
is not where a de-embedded run's time is.

**Members of one group share ONE standard**, which is load-bearing rather than tidy: without it an
asymmetric pair builds and solves the identical 2N-port mesh once per port (measured: 4 calibrations
and 12 standard meshes instead of 2 and 6). The two ENDS of a line still do not share, for the reason
already on the record — their end grading is not exactly mirror-symmetric.

### 7. R-pcal4-6 — what is declined, and every decline is by name

- **A neighbour whose port is at a different station.** The commonest shape on a real board, and the
  one `testdata/portcal/offset-pair` is: a group is one standard cut at ONE plane, and two ports that
  do not share a plane do not share the modes at it.
- **A neighbour carrying no port, beside one that does.** PCAL3's widened conductor floats at zero
  net charge and a group's conductors are all driven; one standard cannot hold both rules, and the
  reference impedance it measured would belong to neither structure.
- **Metal on the port's own net**, a coplanar or conformally cut port, a neighbour that is not
  uniform over the run the standard reproduces, and a group past
  `MaxCalibrationGroupSize` (3) — each by name.
- **A grown FEED LEAD on any member.** R-fed-2 peels a lead as one propagation constant per port; a
  group's port region has one per mode, and the lead is not a matched section of any of them because
  the second conductor is not beside it there. Peeling it with a modal γ would remove a length of
  line that is not there.
- **MODES TOO CLOSE TO SEPARATE — and this one is a REFUSAL, at SETUP.** At zero separation the
  cascade's two eigenvalues coincide, the null space of (M − μI) is a plane rather than a line, and
  which line in it is which mode is decided by round-off. The question is asked of the
  ELECTROSTATICS (`PlanarPortCalibrator.QuasiStaticModeSeparationDegrees`), which is available before
  a single Green's function has been fitted, and it falls through to PCAL2's own refusal — whose
  remedy, separate the feeds, is the remedy here too. **Measured:** three conductors of EQUAL width at
  246 µm refuse at **0.369°** against the shipped floor of 0.5°; making them 254 / 432 / 660 µm takes
  the same geometry to **0.61° at 1 GHz and 1.93° at 7 GHz**, which runs (cascade residual 8.8e-8 at
  the bottom, 1.2e-12 at the top). The pair itself runs from 2.55° to 8.57°.

**A refusal discards the run's notes, so the decline now travels WITH the refusal** rather than being
collected and thrown away — that was true of PCAL3's declines too and is fixed for both.

### 8. R-pcal4-5 — kernel A is still not an input, and the case for taking it got WEAKER

Kernel A solves a coupled cross-section exactly and in milliseconds, and it would hand over γ_m, Tv
and Z_c,m complete. **Gate 1 IS kernel A**, so taking it makes the gate a tautology — `PlanarDeembed`'s
own header, unchanged. And the case is weaker here than in the scalar case rather than stronger: the
one quantity a coupled cross-section makes genuinely hard is **γ_m**, which this file measures
full-wave and which kernel A does not compute at all (its γ is quasi-TEM). What is taken from the
electrostatics is only what D7 already takes — a capacitance, on kernel B's own mesh, from kernel B's
own static Green's function — and §3's table is what that separation buys: an independent comparison
instead of an assumption.

### 9. R-pcal4-1's byte-identity, and how it was proved

A `git worktree` at the pre-change HEAD, one build, both untouched fixtures run on each side.
`separated-pair`'s `.s4p` is **identical on every line but `circuitRF-EM written:`** and its `.npy`
is identical **with no exclusion at all**; `coupled-pair-passive` (PCAL3's own gate) likewise. The
mechanism is not a tolerance: the grouping is attempted only on a DRIVEN breach, which since PCAL2 is
a refusal, so a clear feed never reaches any of it — and `PlanarDeembed.Apply` is untouched, with
`ApplyBlocks` a separate method, because the same arithmetic over 1×1 matrices is the same arithmetic
in a different ORDER and a different order is a different last bit.

### 10. The tests that had to change, and why none of them is a regression

`coupled-pair` was PCAL2's own refusal fixture and is PCAL4's gate fixture; it cannot be both. Four
`PortClearanceRefusalTests` gates and one `PassiveNeighbourStandardTests` gate move to
**`offset-pair`** — the same two conductors at the same 246 µm, with the neighbour's ports at a
different station, so no one plane exists for a shared standard and PCAL2's refusal is reached with
its wording, its diagnostic id and its writes-no-file half all unchanged. Three engine-side gates in
`PlanarFeedClearanceTests` and one in `PlanarPassiveNeighbourTests` keep their synthetic driven pair
and turn `IncludeDrivenGroups` OFF explicitly, because what they assert is PCAL2's rule and it still
holds for everything PCAL4 declines. That is the whole of the fallout across `Engine.Tests` (972 Mom
tests) and `Ui.Tests`.

### 11. Two defects this phase found in code it did not write

- **The `"planar"` diagnostics cubes filed a port's γ, Z_c and C_pul by the calibration's POSITION IN
  A LIST rather than by its port number.** That was always an assumption — an internal delta gap owns
  no calibration and is skipped, so the list has never been one entry per port — and PCAL4 is where it
  becomes wrong rather than merely fragile: a run with one group and one ordinary port hands back ONE
  calibration, whose numbers would have been filed under the group's first port. `PlanarKernel` maps
  by `PortNumber` now.
- **A port calibrated as part of a group has no per-port γ, Z_c or C_pul**, because those quantities
  belong to the group's MODES and there are as many as there are conductors. Its slot in those cubes
  is filled with **NaN** rather than left at zero: a zero in a diagnostics cube reads as a
  measurement. The per-mode numbers are in the run's notes; a `mode` axis for them would be a cube
  whose length varies per group, which a `DataCube` has no way to express.

### 12. What was NOT measured, so that nothing is read into the silence

- **A group on another conductor level, or a group of conductor-referenced (coplanar) ports.** Both
  declined by name; PCAL1 measured no multi-level case at all, and a coplanar group is
  `mom-engine.md` §10.6's differential-port work rather than this.
- **A group larger than three.** The algebra is written for any N and the cap is a cost gate; nothing
  above three has been solved, and the two things that would decide it are the standard's size and
  whether N modes stay separable.
- **A group whose members' modes CROSS inside the band.** The branch assignment scores every
  permutation against the prediction rather than following an ordering, which is what R-pcal4-3 asks
  for, and it is gated on synthetic data — but no fixture here has a crossing in it.
- **A widened (PCAL3) profile and a group in the same standard.** Declined by name; §7.
- **Whether the passivity excess of §5 is also present in the SCALAR path at the same separation.**
  The scalar path refuses that geometry, so there is nothing to compare against.

## PCAL3 — the calibration standard contains the passive neighbour now (2026-09-12)

`docs/sonnet-briefs/brief-portcal-3-passive-neighbour.md`, following PCAL2 directly below. The finding
it acts on is PCAL1's §3: a neighbour that carries no port is **not** benign — at the 246 µm the series
opened on it is 18.0 dB out in S₁₁ at 1 GHz and non-passive at 3 of 7 points — but it is a *milder and
different* failure, needing about half the clearance a driven one does, and it leaves **one driven mode**
at the reference plane. So D6's per-port scalar error box still describes it, and the whole fix is to put
the metal in the standard rather than to ask the user to move it.

**It works, and the number is the one gate 1 asked for at six of seven frequencies.** On the committed
fixture (`testdata/portcal/coupled-pair-passive`, the coupled pair with ports 3 and 4 deleted), against
kernel A's exact four-port reduced with the neighbour's ports terminated in Γ = +1:

| | max \|ΔS\| | worst ΔS₁₁ | worst ΔS₂₁ | worst σ_max | non-passive |
|---|---|---|---|---|---|
| kernel-A-vs-B floor (s = 9 mm) | 0.0522 | 3.13 dB | 0.35 dB | 0.9995 | 0 / 7 |
| **before** (PCAL2's refusal, overridden) | 0.8734 | 17.98 dB | 8.17 dB | 1.0059 | **3 / 7** |
| **after** | **0.0898** | 2.66 dB | 0.64 dB | 0.9995 | **0 / 7** |

Per frequency, against that 0.0522 floor: **1 GHz 0.0421 · 2 GHz 0.0408 · 3 GHz 0.0411 · 4 GHz 0.0448 ·
5 GHz 0.0521 · 6 GHz 0.0898 · 7 GHz 0.0405.** Six of the seven are *at or below* the floor; the seventh
is §4's subject and is the one thing this phase did not close.

Everything below was measured with a scratch console harness in Release against `PlanarSolve.Run` and
`QuasiStaticKernel.Solve` directly — not a test, per the standing rule about the `Benchmark` tier — on
the overview's own settings (cells/λ 5, cells across 2, transmission-line current model, accelerated
solve, adaptive sampling off so every published point is a solved point).

### 1. What was built, and the one place the machinery did NOT already exist

`PlanarPortNeighbourhood` on `PlanarPortResolution`, `PlanarPorts.TryWidenForNeighbours`, and
`PlanarCalibration.BuildNeighbourLine`. The profile machinery has been multi-conductor since RP-2c —
`PlanarPortCrossSection` carries `Lines[]` plus `IsMetal[]`, D4 builds the standard from the DUT's own
gridlines verbatim, and `SpanLoM`/`SpanHiM` already tell `CheckFeedClearance` that everything inside a
profile is reproduced. **What was missing was any way for a conductor OUTSIDE the profile to get
INSIDE it**, and that is the whole of the new code. The coplanar builder's mesh loop and the widened
one's are now one shared `BuildProfileMesh`; the coplanar path emits the same cells and bases in the
same order it always did.

**It is a separate field rather than a reuse of `CrossSection`, and that is load-bearing.** A non-null
`CrossSection` is what *makes* a port conductor-referenced: it dispatches to the two-cut standard, drives
±½ V on two conductors, and changes `IsConductorReferenced`. None of that is true here — this port drives
one cut against the ground plane and the neighbour is driven by nothing — so reusing the record would
have meant telling the two cases apart at every site that reads it.

**The widening is attempted only on a PASSIVE breach**, which since PCAL2 is a refusal. That is the whole
of R-pcal3-4's proof that nothing passing today changes: a clear feed measures `Breached = false` and
never reaches the call.

**The clearance predicate clears itself.** `MeasureFeedClearance` reads `Neighbourhood?.SpanLoM/SpanHiM`
the same way it reads a coplanar cross-section's, so after widening the same function reports the feed
clear — and, just as important, a SECOND neighbour still outside the widened span is still measured, from
the widened edge, and can still refuse the run.

### 2. R-pcal3-3 — what the neighbour does at the standard's ends: FLUSH AND OPEN, measured

The DUT's neighbour carries on past the port; the standard's has to stop somewhere. Two of the brief's
three were measured (`PlanarCalibrationSettings.NeighbourExtensionCells`, which is how they were
measured and which ships at 0):

| the standard's neighbour | max \|ΔS\| | worst ΔS₁₁ |
|---|---|---|
| **flush with the driven line, open at both ends** (`0`) | **0.0898** | 2.66 dB |
| extended 2 bulk cells past each reference plane (`2`) | 0.7997 | 19.15 dB |
| extended 6 bulk cells (`6`) | 0.7996 | 15.86 dB |

**Extending it is barely better than not reproducing the neighbour at all** (0.87), and the reason is
that it puts an open-circuited stub a few cells long immediately *outside* the reference plane — i.e.
inside the error box — which the DUT does not have. Flush is also what this fixture's own DUT is:
its neighbour ends one cell past the reference plane, exactly as the driven line does. **Shorting the
neighbour to ground was not measured**; a standard containing a ground attachment is a structure this
builder does not make, and §4 says why it would not have helped anyway (a shorted strip of length L
resonates at λ/2 too).

What is NOT negotiable, and is true by construction here: **both standards of a pair treat the neighbour
identically** — the same number of extension cells on the short line and on the long one — or D5's γ is
measuring the difference between two end treatments rather than the length between two planes.

### 3. The neighbour's ELECTROSTATIC boundary condition is a third answer, and it is worth 12.3 %

D7 takes Z_c = γ/(jωC_pul) and C_pul from the difference of two standards' static capacitance. With a
second conductor in the standard there are three complete, plausible readings, and they are far apart.
Measured on the widened standard set at s/h = 0.27 (C_pul, pF/m):

| the neighbour is… | C_pul | vs the answer |
|---|---|---|
| at the line's own potential (what a single-conductor standard does: the whole sheet at 1 V) | 74.139 | **+48.0 %** |
| held at 0 V (a *grounded* pour — a different structure) | 57.094 | **+14.0 %** |
| **floating at zero net charge** (an unconnected trace, which is what it is) | **50.089** | — |

The spread collapses with separation (grounded is +0.4 % at s/h = 1.5, the whole sheet still +88 %), which
is exactly the signature the de-embedded answer showed: with the neighbour GROUNDED the residual error was
0.1201 / 0.0622 / 0.0822 at s/h = 0.27 / 0.50 / 1.00 — coupling-dependent; with it FLOATING it is
0.0898 / 0.0928 / 0.0900 — **flat**, which is what a residual that no longer depends on the coupling looks
like.

**Floating is a CONSTRAINT, not a potential, which is why it could not ride on `ModePotential`.** The
conductor's potential is an unknown fixed by Q = 0. `PlanarStandard.FloatingPotential` carries its mask
and `PlanarDeembed.StaticCapacitance` spans it with **one extra right-hand side and no extra
factorisation**: solve for the unit potential and for the mask, then take the combination whose net charge
on the mask is zero. `PlanarStaticAim.ModalCapacitance` does the same on the accelerated route. Null is
RP-2c's arithmetic bit for bit and the second solve is not run at all.

**The whole-sheet reading is the one a naive implementation gets**, because it is what `potential = null`
already means, and at +48 % it renormalises every published s-parameter while looking entirely normal.

### 4. THE ONE THING THIS PHASE DID NOT CLOSE: the widened standard is a RESONATOR

**The 6 GHz point in the table at the top is not noise and it is not the mesh. It is the standard's own
neighbour ringing.** The reproduced neighbour is open at both ends, so at βL = nπ its standing wave
dominates the standard's 2-port and the two-line cascade stops describing a single mode.

Three measurements pin it, and the third is the one that settles it:

1. **It moves with the standard's LENGTH, not with anything else.** `TargetElectricalDegrees` sizes Δℓ,
   and on a 25-point 1–7 GHz sweep the spike sits exactly at the selected long standard's λ/2:

   | target | standards (mm) | predicted λ/2 of the long one | observed spike |
   |---|---|---|---|
   | 40° | 5.54 / 18.47 / 11.08 | 7.56 GHz (out of band) | none; a smooth climb to 0.104 at 7 GHz |
   | 60° (shipped) | 5.54 / 25.87 / 12.93 | **6.48 GHz** | **6.50 GHz, 0.219** |
   | 80° | 5.54 / 31.41 / 16.63 | **5.04 GHz** | **5.00 GHz, 0.210** |

2. **Past its own resonance the error COLLAPSES**, which no ordinary error source does: at 80° the sweep
   reads 0.210 at 5.00 GHz and then **0.016 / 0.007 / 0.012 / 0.015** at 5.5–6.5 GHz — an order of
   magnitude *below* the A-vs-B floor.
3. **It is non-monotone in the mesh** (0.0898 / 0.2251 / 0.1230 at cells/λ 5-across-2, 10-across-4,
   20-across-8) while every off-resonance point moves ~1 %. A sharp feature sampled at a fixed grid
   frequency does that; a discretisation error does not.

**It is the INSTRUMENT, not the design.** The DUT's neighbour is 3.83 mm and resonates near 22 GHz; the
standard's is 12.93 mm because `SuggestDeltas` chose that length.

**And it is essentially unavoidable at this band ratio, which is why it is reported rather than fixed.**
A standard needs Δℓ ≈ λ_g/6 at the band's geometric mean, which on a 7:1 band is 0.44 λ_g at the band's
TOP — so the long standard is already near half a wavelength there before `ShortLineHeights` is added. Two
fixes were costed and **not taken**:

- **Resonance-aware separation selection.** `SelectSeparation` is a pure function of the Δℓ set and the
  predicted β, so penalising a candidate whose L is near nπ costs nothing at run time. It would have
  worked at 40° and at 80° — and **it cannot work on the shipped 60°**, because on this fixture the two
  long standards come out at 25.87 and 12.93 mm, a ratio of 2.0006, so they resonate at the *same*
  frequency. Shipping a lever that does not move the gate fixture is the "name a remedy without asking
  whether it binds" defect this area has already recorded three times.
- **One extra separation per widened port**, chosen to break that ratio. It works, and it costs another
  standard mesh and its `PlanarSolveContext` on a step that R-pcal3-5 already shows doubling.

So `PlanarPortCalibrator.NeighbourResonanceDegrees` computes βL from the **measured** γ over the two
standards a frequency actually reads, and `PlanarSolve` names every point within
`NeighbourResonanceGuardDegrees` (**25°**) of nπ in a note. **n = 0 is excluded and that is not
pedantry** — below its first half wave a standard's βL passes through every small angle on the way up, and
a window that admitted n = 0 flagged 1 GHz, where the answer is at the floor. 25° is measured: the error
is at the floor out to ~27° below nπ and climbs to 0.07 at 20°, 0.145 at 6° and 0.219 at the resonance.
It over-flags by one point on this fixture (7 GHz reads 14° past nπ and is at the floor) and the window is
left symmetric rather than fitted to one geometry.

### 5. Two things that were NOT the explanation, and were checked rather than assumed

- **Conductor thickness.** Kernel A meshes 35 µm of metal and kernel B a zero-thickness sheet, and PCAL1
  recorded thickness as part of the A-vs-B floor — so at 246 µm, where the gap capacitance is large, it
  was the obvious suspect. **Measured false**: at t = 1 µm the floor falls to 0.0423 and the residual
  excess at s/h = 0.27 is 0.0306 against 0.0376 at 35 µm. It moves both numbers and explains neither.
- **The mesh.** Off-resonance, refining from cells/λ 5-across-2 to 20-across-8 moves the answer from
  0.0421 to 0.0430 at 1 GHz. A finer mesh has nothing to find here.

### 6. R-pcal3-5 — what the wider standard cost, reported before it was accepted

On the fixture, per port: standards of N = 149 / 292 / 201 become N = 298 / 584 / 402 — **4.57× the DUT's
unknowns becomes 9.14×**, and a 7-point sweep goes from 3.44 s to 6.84 s. That is close to exactly double,
which is what taking one more conductor of the same width into the profile should cost, and it is on the
step that already dominates a de-embedded run.

**It was not judged unreasonable, and the comparison that decides it is not "before".** The alternative to
a widened standard on this geometry is not a cheaper answer — it is PCAL2's refusal and no `.sNp` at all.
The cost note is the one already printed on every de-embedded run; nothing new reports it.

A profile is never wider than it has to be: the outward walk stops at the first gap that reaches
`PassiveNeighbourClearanceHeights`, so a neighbour already clear is left outside and costs nothing.

### 7. R-pcal3-2 — what is declined, and why the decline is not a silent null

Three cases fall through to PCAL2's refusal, each named:

- **A neighbour that bends, ends or changes width** inside the run the standard reproduces. The uniformity
  check is over D4's own end run — `EndRunCellsFor` cells of the DUT's own longitudinal run — deliberately
  the same region the port's OWN conductor is held to, because two conductors of one standard held to two
  different uniformity rules is a rule nobody can state.
- **A neighbour carrying a port.** Brief 4, and the boundary is asserted rather than left to prose.
- **Metal on the port's own net reaching the plane as a separate run**, which the standard would reproduce
  as two conductors the structure shorts together somewhere the profile cannot see.

**A fourth case is the caller's, not the function's.** `TryWidenForNeighbours` returns a null with NO
reason when it finds nothing within the threshold crossing the port's own plane on the port's own level —
which is the ordinary answer for a clear feed. Whether that leaves a breach standing is a question about
the CLEARANCE, which the function does not measure, so `PlanarSolve` writes that sentence, where both
halves are in hand: the breaching metal is on another level, behind the end face, or begins further in.
Returning a decline from inside the function instead made the "nothing to widen" case claim a failure,
which is what its own test caught.

**A conformally cut port is declined too**, by name: its profile is the METAL's extents rather than the
grid's (R-cut-4), so a neighbour's gridlines would not line up with it.

### 8. How R-pcal3-4's byte-identity was proved

The same way PCAL2 proved its own, and for the same reason a `git stash` is the wrong instrument: a
`git worktree` at the pre-change HEAD, one build, `separated-pair` run on both sides. The two `.s4p`
files are **identical on every line but `circuitRF-EM written:`**, and the `.npy` is identical with no
exclusion at all. The engine-side half is `AClearFeedIsUntouched_AndItsStandardIsTheOneItAlwaysWas`,
which asserts on the standard's COORDINATES because that is what D4 is a construction of.

### 9. What was not measured, so that nothing is read into the silence

- **A neighbour on another conductor level.** PCAL1 measured none, so there is no evidence either way;
  this phase declines it by name rather than attempting it.
- **A ground pour that is actually grounded.** PCAL1's "pour" was a 5 mm-wide trace with no port and no
  via, i.e. floating, and §3's boundary condition is right for it. A pour with a via to the reference
  plane is a *grounded* conductor and §3's table says the two readings differ by 14 % at s/h = 0.27. The
  mesh can tell them apart — a ground attachment basis is visible in `PlanarConductors` — and nothing
  here does.
- **More than one neighbour, or neighbours on both sides.** The walk handles both and neither was
  measured; the fixture has one.
- **A widened COPLANAR port.** Declined by name: that standard already drives two conductors at ±½ V and
  a third piece of metal beside them has no stated potential, which is the ambiguity the third-conductor
  refusal names one step further out.

## PCAL2 — a clearance breach is a REFUSAL now, and the threshold is two numbers (2026-09-12)

`docs/sonnet-briefs/brief-portcal-2-refuse-not-warn.md`, following PCAL1 directly below. The finding
it acts on: a port whose feed had a neighbour was de-embedded against an isolated-line standard, which
on the series' coupled pair is **22 dB of error in S₂₁ at 1 GHz and non-passive at 48 of 51 points** —
and the answer was published with a note attached to it. **A `.s4p` on disk carries no notes.** The
next person to open it saw a plausible curve.

### 1. What refuses, and what does not

`PlanarSolve.Run` now throws `PlanarFeedClearanceRefusedException` at SETUP — before the first
Green's-function fit — when any de-embedded port's feed has another conductor closer than that
neighbour's class requires. `EmRunService` catches it beside the mesh-ceiling refusal and returns
`EmRunStatus.Refused` with `em.refused.port-clearance`, so `circuitrf em` exits 1 with the run
service's own sentence and **writes no `.sNp`** — the GUI and the CLI take one decision from one
place, which `Gate5_TheThresholdAndThePredicateExistExactlyOnce` asserts as a comment-stripped source
scan rather than as prose.

**Passivity was deliberately NOT made a refusal.** It is a symptom with several causes, some of them
legitimate at the 1e-3 level, and refusing on it would block runs that are fine. The refusal is on the
*cause* — a measurable geometric fact about the port — and `NOT PASSIVE` goes on being reported as the
diagnostic it is.

### 2. The threshold is TWO numbers, on `PlanarCalibrationSettings`, and it is not `EndRunHeights`

`DrivenNeighbourClearanceHeights = 5.0` and `PassiveNeighbourClearanceHeights = 2.0`, both PCAL1's
recommendation. They are separate from `EndRunHeights` (3.0, unchanged) and the separation is
load-bearing in two independent ways:

- **`EndRunHeights` is a length ALONG the feed** and sizes every calibration standard — already 4.57×
  the DUT's unknowns on the fixture and 6.51× on the board. Raising it to buy clearance would pay for
  the clearance in solve time.
- **The clearance is a distance ACROSS**, and costs nothing. So the scan region stays the standard's
  own run (`EndRunHeights × h` longitudinally) while the threshold applies laterally. Letting the
  scan's LENGTH grow with the threshold instead was tried and rejected: at 5 h the scan reaches 4.5 mm
  down a clean feed and finds ordinary circuit structure the standard never sees — on `separated-pair`
  it reports the coupled section itself, 4 mm away, as the port's neighbour. That is the unclearable
  warning the 2026-08-12 midpoint fix already had to cure once.

### 3. THREE classes of nearby metal, and two of them are not neighbours at all

`PlanarConductors` (new) labels every mesh cell by connected conductor, **from the BASIS list rather
than from the polygons** — a rooftop basis exists for exactly those adjacent cell pairs that are both
metal, so the basis list already is the conduction graph of the meshed structure, vias included. A
second answer derived from the drawn polygons would disagree wherever the mesher staircased or cut.

| the nearest metal | verdict |
|---|---|
| the port's OWN conductor | not a neighbour — `PlanarFeedExtension`'s job, and it peels its lead exactly |
| inside the port's declared cross-section (a coplanar return) | not a neighbour — all of it is reproduced in the standard |
| a separate conductor carrying a port | driven, 5 h |
| a separate conductor carrying none | passive, 2 h |

**The own-net rule is not a nicety; without it the refusal refuses every taper in the repository.**
R-fed-1 sizes its lead to `EndRunHeights`, which is now SMALLER than the clearance threshold, so the
DUT's own flare begins inside the scanned region by construction.

### 4. The finding that cost the most: a cell with no basis is not a conductor

**Measured, not reasoned about.** The conformal mesh of the Klopfenstein taper `EmCeilingRefusalTests`
runs on has 3,504 cells and **33 connected components** — 32 of which are single cut cells that no
rooftop pairs with, slivers of the taper's OWN metal that R-cut-4 declines to drive. Labelled as
one-cell conductors carrying no port, they made PCAL2 refuse that taper at 0.13 substrate heights from
its own artwork, with a message naming metal the user would have had to hunt for.

A cell in no basis carries no unknown, no current and no contribution to any answer: it is not in the
solve. So it cannot make a calibration wrong, and `PlanarConductors.CarriesCurrent` excludes it. This
is the one defect in this phase that a synthetic fixture would never have produced — it needed a
conformal mesh of real artwork.

### 5. The two `.cem` settings

- **`Deembed`** (null = ON). `PlanarSolveSettings.Deembed` had existed since L8d with **no `.cem`
  field to reach it**, so the mesh-ceiling refusal's own recommended remedy — "turn de-embedding off
  and read the raw solve instead" — could not be followed from the GUI or from `circuitrf em` at all.
  A refusal that names an unreachable remedy is not a remedy.
- **`DeembedOutsideCalibrationValidity`** (null = off). Named for what it DOES rather than for the
  check it suppresses, because the check is not the thing being turned off: the arithmetic still runs
  outside its validity and the answer is still wrong in the way the refusal describes.

Both follow the omit-at-default rule, so every `.cem` on disk gains no byte, and both are on the
Solver options group of the EM panel with the override disabled outright when de-embedding is off.

### 6. The caveat rides on the FILE, because the file is what outlives the notes

`EmProvenanceStamp.CaveatPrefix` (`! circuitRF-EM caveat: `) and `EmSnpProvenance.ValidityCaveats` /
`ReadCaveats`. A run that de-embedded outside validity stamps one line naming every breached port and
its margin in substrate heights; every other run stamps none, so an ordinary `.sNp` is byte-identical
to one written before PCAL2. It reads back off the file, which is the assertion `Gate3` makes — a
caveat nothing can read back has the same defect one step further on.

**It is deliberately NOT in the provenance HASH.** Adding `Deembed` to `MeshHash` or `PortHash` would
mark every `.snp` in existence stale and would itself break the byte-identity R-pcal2-6 asks for. The
consequence, stated rather than hidden: a raw-solve `.snp` reads as current against a de-embedding
setup, the same gap `AdaptiveSampling` and `DispersionCorrection` already have.

### 7. No error bound, and the reason is PCAL1's

R-pcal2-5's margin is reported as **s/h on every de-embedded port, breached or not** — a pass/fail with
no distance is how a threshold change becomes invisible. There is no error estimate beside it and none
was attempted: `PlanarDeembed.SolveErrorBox`'s arguments are the two standards and the DUT is not an
input, so both residuals are bit-for-bit identical between the run that is 22 dB wrong and the run at
the floor, and σ_max is non-monotonic in the error. Geometry is the only honest quantitative thing to
report here.

### 8. The fixture moved, and that was the owner's call

**`testdata/portcal/separated-pair` held its neighbour 4 mm away, which is 4.16 substrate heights on
its own 0.9 mm stackup — inside the shipped driven threshold of 5.** So the brief's own R-pcal2-4
(5 h) and its own gate 2 (`separated-pair` runs clean) could not both hold. PCAL1's measured
requirement at h = 0.9 mm is ≈ 3.9 h, so the fixture was fine on its own stackup and refused by a
threshold sized to cover the 0.225 mm case as well (≈ 5.5 h there).

Put to the owner with three options (ship 4 h and leave the fixture; ship 5 h and widen it; ship 5 h
and let it refuse). **Decision: ship 5 h and widen the fixture**, to 6 mm — 5746 µm edge to edge =
6.38 h. The re-measured numbers replaced the tables that quoted the old geometry in
`brief-portcal-0-overview.md` §1b, `an01-ports-and-coupling.md` §3 and `testdata/portcal/README.md`,
and the AN-01 figure fixture was redrawn to the same dimensions.

### 9. How R-pcal2-6's byte-identity was actually proved

Not by assertion. A `git worktree` at the pre-change HEAD was built, handed a copy of the **widened**
layout, and run; the post-change build was run on the same geometry; the two `.s4p` files are
**identical on every line but `circuitRF-EM written:`**, which the file carries by design. (A `git
stash` would have been the full suite twice plus a restore with a half-finished change at risk — a
worktree costs one build.) That comparison is a manual step and stays one: there is no golden
Touchstone in `testdata/portcal` by policy. What is gated on every run is the clearance DECISION on
the committed geometry, asked of the same function the solver asks, at no solve cost —
`Gate2_TheSeparatedPairClearsBothThresholds_WithItsMarginStated`, which asserts the margin as a number
so a threshold change cannot pass unnoticed. The end-to-end clean run is beside it in the
`Category=Benchmark` tier at 40 s.

### 10. The one existing test that had to change, and why it is not a regression

`CoplanarDeembedTests.Gate5_AMixedCalibrationRuns_AndItsCostIsReported` builds an all-plane run purely
as a COST BASELINE against a mixed one. PCAL2 refuses that baseline **correctly**: treating port 2 as a
plain microstrip puts the return strip 150 µm (0.03 h) from a feed the standard reproduces as an
isolated line — the test's own comment already said the clearance warning "would be right to fire".
Nothing reads that run's s-parameters, only its standard count and its clock, so it now asks for
`DeembedOutsideCalibrationValidity: true` explicitly. That is the whole of the fallout across
`Engine.Tests` (2,220 tests) and `Ui.Tests`.

## PCAL1 — how much clearance a calibrated port actually needs (2026-09-12)

`docs/sonnet-briefs/brief-portcal-1-investigation.md`. Measurement and a decision; **no production
code changed.** What the series opened on is in `brief-portcal-0-overview.md`: an edge port whose
feed has a neighbour is de-embedded against a standard that is an *isolated* uniform line, and on a
coupled pair that is 22 dB of error in S₂₁ at 1 GHz, published with a note.

Three things were unknown and each of them sized a different fix: how much clearance is enough and
what it scales with; whether a passive neighbour fails like a driven one; and whether the error is
predictable from something the solver already computes. All three now have numbers.

### 0. How it was measured, and why the geometry is the coupled pair rather than `fedpair`

The brief's R-pcal1-1 says "take the `fedpair` geometry and sweep the feed separation". **It was swept
on the plain coupled pair instead, and that is deliberate.** §2 of the same brief forbids building the
oracle out of kernel B, and requires kernel A where the geometry allows it. `fedpair` has bends, so
kernel A cannot solve it and the only available oracle would have been another kernel-B answer — the
one thing the brief rules out. The plain pair is a **uniform cross-section at every separation**, so
kernel A is exact at every point of the sweep and the oracle is the same instrument throughout. The
price is that the neighbour runs the DUT's whole length rather than only the feed; that makes the
measurement *pessimistic* about clearance, never optimistic, and §1b of the overview already shows the
feed region is what governs.

Everything below is a scratch console harness (`Release`, outside the repo) calling
`PlanarKernel.Solve` and `QuasiStaticKernel.Solve` directly — not a test, per the standing rule about
measuring with a harness rather than the `Benchmark` tier. **Adaptive sampling was turned OFF** so that
every published point is a solved point and a |ΔS| is a measurement rather than a property of an
interpolant. Seven frequencies (1…7 GHz), cells/λ 5, cells across 2, transmission-line current model,
accelerated solve — the overview's own settings.

**Gate 0, run before anything new was measured:** the harness reproduces the overview's §1 table to the
digit (1 GHz: S₁₁ −19.09/−0.08, S₂₁ −0.11/−22.75, σ_max 0.9992/1.0008; 7 GHz: −6.52/−4.01,
−1.88/−13.07, 1.0041). The committed fixture run through the real `circuitrf em` verb gives the same
N = 298 and the same σ_max 1.0041, so the harness and the product path are the same solve.

### 1. R-pcal1-1 — the clearance law, and the floor that has to be subtracted first

**There is an A-vs-B agreement floor, it is not a mesh artefact, and every threshold below is stated
relative to it.** At a separation where the pair is effectively two isolated lines, kernel A and
kernel B still differ by **|ΔS| ≈ 0.052** (h = 0.9 mm, w = 254 µm) — 3.1 dB in S₁₁ at 1 GHz, where
|S₁₁| is small and a fixed absolute error is enormous in decibels. Refining the mesh does not move it:

| cells/λ | cells across | N | worst σ_max | max ΔS₁₁ dB | max ΔS₂₁ dB | max ΔS |
|---|---|---|---|---|---|---|
| 5 | 2 | 298 | 0.9995 | 3.13 | 0.35 | 0.0522 |
| 10 | 4 | 324 | 0.9995 | 3.24 | 0.37 | 0.0546 |
| 20 | 4 | 324 | 0.9995 | 3.24 | 0.37 | 0.0546 |
| 20 | 8 | 376 | 0.9995 | 3.23 | 0.38 | 0.0542 |

(The mesh study above is at s = 9 mm, where the pair is well past any clearance threshold.)

Conductor thickness accounts for part of it and only at the top of the band: dropping t from 35 µm to
1 µm takes the 7 GHz floor from 0.048 to 0.016 and leaves the 1 GHz floor at 0.042. That is a change
to kernel A's geometry alone — kernel B meshes a zero-thickness sheet and reads `ThicknessM` only for
the loss model — which is the point: the floor is the two kernels modelling the same drawing
differently.
What remains is a quasi-TEM cross-section against a full-wave sheet, and it is a property of the
comparison, not of the de-embedding.

**The sweep, driven pair (four ports), h = 0.9 mm, w = 254 µm, 3.83 mm long:**

| s (µm) | s/h | s/w | worst σ_max | non-passive | max ΔS₁₁ dB | max ΔS₂₁ dB | max ΔS | worst at |
|---|---|---|---|---|---|---|---|---|
| 246 | 0.27 | 0.97 | 1.0041 | 6 / 7 | 19.01 | 22.64 | 0.985 | 1 GHz |
| 450 | 0.50 | 1.77 | 1.0038 | 7 / 7 | 18.67 | 30.11 | 0.978 | 1 GHz |
| 900 | 1.00 | 3.54 | 1.0062 | 5 / 7 | 17.92 | 23.13 | 0.922 | 1 GHz |
| 1350 | 1.50 | 5.31 | 1.0092 | 3 / 7 | 15.34 | 10.26 | 0.692 | 1 GHz |
| 1800 | 2.00 | 7.09 | 1.0105 | 2 / 7 | 8.69 | 3.27 | 0.477 | 1 GHz |
| 2700 | 3.00 | 10.63 | 1.0047 | 1 / 7 | 2.80 | 0.35 | 0.169 | 1 GHz |
| 3150 | 3.50 | 12.40 | 1.0024 | 1 / 7 | 3.01 | 0.35 | 0.095 | 1 GHz |
| 3600 | 4.00 | 14.17 | 1.0010 | 1 / 7 | 3.09 | 0.35 | 0.055 | 1 GHz |
| 4050 | 4.50 | 15.94 | 1.0002 | 0 / 7 | 3.11 | 0.35 | 0.052 | — floor |
| 5400 | 6.00 | 21.26 | 0.9995 | 0 / 7 | 3.12 | 0.35 | 0.052 | — floor |
| 10800 | 12.0 | 42.52 | 0.9995 | 0 / 7 | 3.13 | 0.35 | 0.052 | — floor |

The transition is bracketed on both sides, with passing and failing separations either side of it, and
**the error is worst at the bottom of the band in every single case** — which is the 1/a₂₁²
amplification D6's own passivity note names, and it means a spot check at the design frequency is the
worst possible place to look.

### 2. R-pcal1-2 — it scales with SUBSTRATE HEIGHT, and not with line width

Defining the threshold as *the smallest s at which |ΔS| is back within 0.005 of that case's own
well-separated floor*:

| case | h | w | neighbour | s_threshold | **s/h** | s/w |
|---|---|---|---|---|---|---|
| driven | 0.9 mm | 254 µm | 254 µm | 3.5 mm | **3.9** | 13.8 |
| driven | 0.9 mm | 1016 µm | 1016 µm | 3.35 mm | **3.7** | 3.3 |
| driven | 0.225 mm | 254 µm | 254 µm | 1.24 mm | **5.5** | 5.0 |
| passive | 0.9 mm | 254 µm | 254 µm | 1.9 mm | **2.1** | 7.5 |
| passive | 0.9 mm | 254 µm | 5 mm pour | 2.0 mm | **2.2** | 7.9 |
| passive | 0.225 mm | 254 µm | 254 µm | 0.40 mm | **1.8** | 1.6 |

**Line width is inert.** A 4× change in w at fixed h moves the threshold by 5 % (3.9 → 3.7 s/h) while
moving s/w by 4.2×. The neighbour's own width is inert too: a 5 mm pour behaves like a 254 µm trace to
within 2 % at every separation (0.878 vs 0.871 at s = 246 µm, 0.189 vs 0.181 at s = 900 µm).

**And the collapse under s/h is good but NOT exact, which is the negative half R-pcal1-2 asks for.**
Over a 4× change in h the driven threshold moves from 3.9 to 5.5 substrate heights — a 1.4× spread,
against 4.5× for s/w and 3× for absolute length and 3× for s/λ_g. So h is the controlling length and
the others are not, but "s/h" is a good variable rather than an exact invariant. With two usable
substrate heights an exponent cannot be claimed and none is claimed here. The *passive* family
collapses much better (1.8 vs 2.1 over the same 4×), which is consistent with the driven error
carrying the second port's box and the mutual terms as well as the port's own neighbourhood.

**A third substrate height was tried and measured nothing — recorded rather than dropped.** At
h = 3.6 mm with w = 254 µm the A-vs-B floor is **0.535**, and the answer is non-passive at 4 of 7
points at *every* separation out to s/h = 7. A 254 µm line on a 3.6 mm substrate is w/h = 0.07 and the
slab is 0.15 λ₀ thick at 7 GHz; neither kernel is describing the same object any more, so there is no
oracle and therefore no measurement. It is also a reminder that a neighbour is not the only thing that
can make this kernel non-passive, which the overview's §6 already declines to claim.

### 3. R-pcal1-3 — a passive neighbour is NOT benign; it is about twice as tolerant

Same metal, same separations, ports removed from the neighbouring line so it is present and undriven.
The oracle is kernel A's exact four-port reduced with ports 3 and 4 terminated in Γ = +1 — and that
oracle is sound, because its own well-separated floor is the same 0.052 the four-port comparison has.

| s/h | driven, max ΔS | passive, max ΔS | driven non-passive | passive non-passive |
|---|---|---|---|---|
| 0.27 | 0.985 | 0.871 | 6 / 7 | 3 / 7 |
| 0.50 | 0.978 | 0.616 | 7 / 7 | 2 / 7 |
| 1.00 | 0.922 | 0.181 | 5 / 7 | 1 / 7 |
| 1.50 | 0.692 | 0.079 | 3 / 7 | 0 / 7 |
| 2.00 | 0.477 | 0.058 | 2 / 7 | 0 / 7 |
| 3.00 | 0.169 | 0.053 | 1 / 7 | 0 / 7 |

At the separation the series opened on, the passive case is **18.0 dB wrong in S₁₁ and 8.1 dB wrong in
S₂₁ at 1 GHz** and non-passive at 3 of 7 points. So brief 3's premise is confirmed in the direction
that matters — it is a *different and milder* failure, needing roughly **half** the clearance — but its
optimistic reading ("if a passive neighbour is benign, brief 3 is unnecessary") is refuted. A passive
neighbour at s/h < 1, which is an entirely ordinary PCB spacing, is catastrophic.

### 4. R-pcal1-4 — which neighbour classes are actually distinct

Four candidate cases, and only **three** of them are distinct:

1. **A separate net carrying a port** (driven). Two modes at the reference plane, a scalar error box.
   Worst case, threshold ≈ 4–6 h. Brief 4.
2. **A separate net carrying no port** (passive trace). One driven mode. Threshold ≈ 2 h. Brief 3.
3. **A ground pour beside the feed — NOT a distinct case.** Measured at 20× the line width and it is
   case 2 to within 2 % at every separation. The neighbour's width does not enter. Brief 3 covers it
   with no special handling, which is a scope *reduction* for that brief.
4. **A flare or pad on the port's OWN net — distinct, and already handled.** `PlanarFeedExtension`
   grows a collinear uniform lead and peels it exactly, and the clearance warning is deliberately
   silent on an extended taper (`PlanarFeedExtensionTests.TheClearanceWarningFallsSILENTOnAnExtendedTaper_ButNotOnARealNeighbour`).
   Measured here on a 254 µm line with a 1016 µm × 500 µm pad at each port end, ports on the pads:
   **passive at every frequency** (worst σ_max 0.9992), no clearance note, no passivity note, and the
   response tracks the bare line's at the top of the band. So the original board's port 2 reporting
   "other metal 0 m away" is not a third mechanism: it is one of `PlanarFeedExtension`'s own declines —
   a lead that would have run into other metal on the same level — which leaves the flare inside the
   required run. The blocker there is a *neighbour*, so it reduces to case 1 or 2.

### 5. R-pcal1-5 — neither residual predicts the error, and it is structural, not bad luck

`ConsistencyResidual` and `RejectedResidual` are **bit-for-bit identical** between the run that is
22 dB wrong and the run that is at the floor:

| f | s = 246 µm (ΔS = 0.985) | | s = 7.2 mm (ΔS = 0.052) | |
|---|---|---|---|---|
| | consistency | rejected | consistency | rejected |
| 1 GHz | 4.7e-12 | 2.237e-5 | 3.6e-12 | 2.237e-5 |
| 4 GHz | 2.7e-12 | 1.157e-4 | 2.7e-12 | 1.157e-4 |
| 7 GHz | 2.5e-13 | 1.050e-3 | 3.1e-13 | 1.050e-3 |

**And they must be.** `PlanarDeembed.SolveErrorBox`'s arguments are the two calibration standards'
s-matrices and nothing else — the DUT is not an input. The standards are isolated lines of the port's
own cross-section, which do not change when the DUT's neighbour moves, so the residual is *blind by
construction* to the thing being asked about. This settles the area's standing question in the
direction it always suspected: **it is an honest measure of what was discarded, and it is not a
predictor of accuracy — here it is not even correlated with it.** (It is not useless: a degenerate
isotropic mesh tried during this work drove `RejectedResidual` to 1.09, i.e. the sign was decided by
noise, and that *was* a true report of a broken calibration.)

**σ_max is a good detector and a useless estimator.** Spearman ρ between max |ΔS| and σ_max is 0.62
(driven), 0.39 (passive), 0.72 pooled — and worse than the number suggests, because it is
**non-monotonic exactly where it matters**: the worst case measured (ΔS = 0.985) reports σ_max = 1.0041
while a case with *half* the error (ΔS = 0.477) reports 1.0105. As a detector it is much better: in
both families every separation whose error exceeded ~3× the floor was flagged non-passive at one point
or more, and every separation at the floor was not. The misses are in the band between 1× and 1.6× the
floor (passive at s/h = 1.5 is 1.5× the floor and passes the passivity check).

**So brief 2 cannot report an error bound.** Nothing the solve already computes tracks the magnitude.
What it *can* report quantitatively is the geometric margin — the neighbour's distance in substrate
heights — because that is the variable the error actually follows.

### 6. R-pcal1-6 — the decision

**The engine's present threshold is the right VARIABLE and the wrong NUMBER, and it is the wrong
number for a second reason: it is one number where two are needed.**

`PlanarSolve` calls `CheckFeedClearance` with `EndRunHeights × h` = 3 h = 2.7 mm on this stackup. Three
substrate heights is a length in the right units — the measurement confirms h is the controlling length
— but at *exactly* 3 h the driven pair is still 0.169 out in |ΔS| (3.3× the floor) and non-passive.
Driven needs ≈ 4 h at 0.9 mm and ≈ 5.5 h at 0.225 mm; passive needs ≈ 2 h at both.

| brief | verdict | what changes |
|---|---|---|
| **2 — refuse, not warn** | **Write it. Unchanged in shape, and now carries numbers.** | The clearance threshold must become **its own setting, separate from `EndRunHeights`** — today they are the same constant, so raising the clearance would lengthen every calibration standard and standards already dominate the run. Recommended value: **5 substrate heights for a neighbour that carries a port, 2 for one that does not.** R-pcal2-5's "report the margin" should report **s/h**, and R-pcal2-5's optional error bound is **not available** — §5 above. |
| **3 — passive neighbour in the profile** | **Write it, second. Premise confirmed, optimistic reading refuted.** | Its own clearance target is ≈ 2 h, not brief 4's. **A ground pour is not a separate case** and needs no special handling — scope reduction. Its gate should be the passive fixture at s/h ≈ 0.27, which is 18 dB wrong today. |
| **4 — modal error box** | **Write it, third, and it remains the expensive one.** | It is not made unnecessary by 3: the driven case needs 2–3× the clearance of the passive one, so a design that 3 makes safe can still be refused for a driven neighbour. Its gate stays the overview's own coupled pair against the kernel-A oracle. |

**No brief was deleted and no fifth one is warranted** — the three neighbour classes that remain map
one-to-one onto briefs 2, 3 and 4 as they already stand.

**What was NOT measured, so that nothing is read into the silence:**

- **A neighbour on another conductor level.** Every case swept here is co-planar with the port. There
  is no evidence either way about a trace or plane on a different level inside the clearance distance.
- **Frequency as an independent variable.** The whole sweep is 1–7 GHz on one stackup, and the error
  is worst at the bottom of it in every case — which is D6's own 1/a₂₁² and is expected. Whether the
  threshold in s/h itself moves with frequency was not separated out.
- **A third substrate height.** 3.6 mm was tried and could not be used (§2); the law rests on 0.225 mm
  and 0.9 mm, which is the 4× the brief asked for and no more.
- **A narrow line below 254 µm.** At w = 64 µm the transmission-line mesher's aspect-ratio cap raises
  the longitudinal pitch as the artwork widens, and past s = 3.6 mm the 3.83 mm line resolves to one
  cell and the port refuses by name ("only one cell long in the direction current would flow"). Raising
  cells/wavelength does not move it and lengthening the line does not either. That case is bracketed
  only up to s/h = 3, where it agrees with the others; the mesher interaction is worth knowing about
  on its own account.

### 7. The fixture

`testdata/portcal/` — a workspace, a 0.9 mm FR-4 two-layer technology, and two cells: `coupled-pair`
(the failing case: refuses nothing today, warns on all four ports, non-passive at 6 of 7 points) and
`separated-pair` (the same coupled section with 4 mm of isolated feed at each port: no clearance note,
no passivity note). Both carry a `.cem` at the overview's own mesh settings, so
`circuitrf em testdata/portcal/<cell>/em/<cell>.cem` reproduces the measurement with no harness.
`testdata/portcal/README.md` says which brief consumes which.

---

# MIM-8 — the cross-level fill, at the cell/separation a real MMIC meshes at (brief-em-mim-8-cross-level-quadrature.md, 2026-09-15)

**MIM-3's deferral, arriving.** MIM-3 measured the cross-level block losing four decades between
cell/separation 1 and 20, drew the range at 5, and stopped — its own instruction. What came back was
a user's design on the shipped MMIC technology: a 1.0838 pF series MIM capacitor extracting as
0.002 pF, an open. Putting the plate level into the run made it *worse*. No setting reached it,
because the mesh pitch there is `width/MinCellsAcrossConductor` — the metal's own width — so neither
frequency knob acts, and every MMIC on that technology is past the bound.

Every table is in `HISTORY.md` §MIM-8.

## The verdict, in one paragraph

**The peak is a FITTED IMAGE, not an extracted asymptote, and R-fil-8 had already named the
condition.** The brief proposed subtracting "the two-parallel-rectangles potential integral at
separation d" — the asymptote — and that term does not exist here:
`LayeredSpectralGreens.AsymptoticAtHeights` returns zero coefficients for a cross-REGION pair by
design, and a plate pair straddling the dielectric is one. What carries the peak is a DCIM image at
0.128 µm, i.e. **0.05 of a 2.5 µm cell**, which `FromDcimAtHeights` treats as smooth —
`PlanarKernelTerms.SmallestImageDepth` exists precisely because that treatment is conditional on the
depth being large against a cell. So MIM-8 subtracts those images' static parts and integrates them
in closed form (`ShallowImageCore`, `RectangleIntegrals.Corner0AtComplexOffset`), which is
`FromDcimAtHeightsMinusStaticAsymptotes`' pattern applied one term over. **The capacitor the user
drew now reads 1.0891 pF against 1.0838 pF, where it read −0.3255 pF**, and with their 3.8 nH spiral
it resonates at 2.474 GHz against a design intent of 2.48.

## Findings

**1. Only the SCALAR block needed it, and that was measured rather than assumed.** Z = scalar half
plus vector half exactly, so the vector half is a difference. The vector half carries **1.7e-6 of a
3.2e-1 cross-level error** at the worst rung — five decades down — although its own fit has an image
at 0.08 of a cell and therefore the same peak. At these dimensions the scalar entry is larger by
~1/(ω²ε₀µ₀A_cell), which on a 2.5 µm cell at 10 GHz is 2.6e5. The ramp-weighted moments at a complex
offset that the vector block would have needed were measured to be unnecessary, not skipped.

**2. THE SAME-LEVEL BLOCK IS IMPLICATED TOO, and that contradicts the brief's Must-NOT.** The brief
says "§MIM-3 measured the same-level block at 3e-6 and flat across the whole ladder: it is not
implicated". It is flat over the range MIM-3 *drew* — and MIM-3's own Table 2 has it at **9.4e-2 at
cell/separation 50**. Measured directly: treating only the cross-level pairings leaves the plate
capacitance at 2.119 of the closed form at cell/sep 50; treating every scalar pairing gives 1.014.
Reaching the brief's own milestone-3 gate ("to ratio 50") requires both. The Must-NOT's *purpose* is
kept — the same-level arithmetic is untouched wherever no image is shallow, which is every pairing
the mesh resolves — but its premise was stated too strongly and is corrected here rather than worked
around.

**3. Bit-identity is narrower than "no thin film", and an AIRBRIDGE proves it.** The acting condition
is whether the mesh resolves the pairing's images — cell against level separation, the quantity
MIM-3's note already reports — not whether a film is present. On the shipped technology's own
airbridge fixture (6 µm of air, no film anywhere): at cell/separation 5 the treatment fires and the
cross-level block improves **8.9e-5 → 1.7e-6**; at 2.5 it fires and no measured quantity moves; at
1.25 it does not fire and **not one of 4.5 million entries differs**. So the gate is "a run whose
mesh resolves its own gap is filled bit-identically", and MIM-7's interconnect-only airbridge at its
own default mesh is *not* such a run. Where it acts it improves; it is simply not identity.

**4. `ValidatedCellOverSeparation` moved 5 → 200, after re-measuring, not to accommodate anything.**
Both of MIM-3's ladders were re-run on MIM-3's own fixtures. Ladder 1 (the fill against forced-high)
reaches MIM-3's own 4.1e-3 criterion at about 500 instead of 5. Ladder 2 (the plate capacitance
against ε₀εᵣA/d) is within 1% out to cell/separation 300 **with the separation held at the shipped
0.2 µm and the plate grown**, so the ratio is the only thing moving. 200 is where both are measured
and both hold — MIM-3's own rule for drawing it. The sharpest single statement: one 60 × 60 µm plate
pair on four meshes read 1.086 / 4.470 / −0.261 / −0.046 before and **1.003 on all four** after.

**5. A capacitance is now readable without a port, and that is what made the ladder cheap.** MIM-3
needed a de-embedded two-port, had to truncate the stack at the upper plate to get a port at all, and
recorded that **raw S cannot carry a capacitance in this engine** (a matched 50 Ω line reads
|S₂₁| = 0.0706). `PlanarFill.ScalarPotentialMatrix(cores, set, levels)` is `FillMultiLevel`'s own
first half **lifted out rather than copied**, so `PlanarStaticLimitTests`' 1 V / 0 V instrument reads
the arithmetic a solve uses, with no port in it to be wrong, in under a second a rung. That is why
MIM-8 could run ladders MIM-3 could only sample.

**6. What is LEFT is a SEPARATION limit, not a mesh one, and it is stated rather than absorbed.**
Shrinking d also raises cell/separation, and there the capacitance does depart — 1.014 / 0.973 /
0.948 / 0.819 at d = 0.05 / 0.025 / 0.0125 / 0.005 µm. It is not a mesh condition: at a fixed
d = 0.0125 µm it reads **0.948 at every pitch from 2.5 µm down to 0.31 µm**, cell/separation 200
through 25. The floor sits below the 0.05 µm MIM-3's kernel tier was measured over, and it was not
diagnosed: the oracle MIM-3 used is validated to ρ/λ ≥ 1e-3, which at 10 GHz over GaAs is 8.35 µm,
and cannot be asked about a peak 10 nm wide. The shipped 0.2 µm dielectric is four times above it.

**7. The complex closed form is where a continuation can go wrong, and the failing side is the
reference.** A fitted depth is complex, so `Corner0AtOffset` had to be continued off the real axis.
Branch selection is the whole content: the two `asinh` arguments stay in the right half-plane by
construction, and the `atan` term takes whichever of `z` and `1/z` has modulus ≤ 1. Against adaptive
quadrature it holds to ~1e-15 out to 45° off the axis. **Nearer the imaginary axis the quadrature is
what fails** — `c² ≈ −1` puts a near-pole on the real (u,v) domain — and subdividing the reference
walks it toward the closed form (1.7e-1 → 6.6e-5 over 1 → 64 sub-rectangles a side) while the closed
form, being one evaluation, does not move.

## What was built

- **`RectangleIntegrals.Corner0AtComplexOffset` / `InverseAtComplexOffset`**, and
  **`ShallowImageCore.CellPairMean`** — the cell-pair mean of `1/√(ρ²+b²)` at a complex depth, outer
  Gauss on the fill's own graded rule and inner closed form, exactly as `ViaZIntegral.MeanAtOffset`
  does it at a real one.
- **`PlanarKernelTerms.FromDcimAtHeightsMinusShallowImages` + `ShallowImageSplit`**, and
  **`PlanarKernelSet.GetMinusShallowImages`** sharing the fit. The two halves of the split travel
  together because assembling one without the other is a different kernel, not a worse answer.
- **`PlanarFillSettings.ShallowImageCells = 0.5`**, chosen from the ladder: 0.125 misses the
  0.128-cell image at cell/separation 5 and that rung does not move; 0.25 through 2 are
  indistinguishable from cell/separation 5 up. **0 is the pre-MIM-8 arithmetic, bit for bit** — it is
  what `MimThinLayerTests.T1`'s off-rows pin, using MIM-3's own literals unchanged.
- **A near/far tier on the CELL PAIR.** A peak of width |b| is only unresolved for a pair whose own
  ρ range reaches it, so past `FarRatio` the pair takes the untreated decomposition. Every number in
  both ladders is identical with it and without it; on the fixture measured it removes 98% of the
  cross-level closed-form calls and none of the same-level ones, which is where the remaining 1.8-2.4×
  of fill time is.

## Not done, on purpose

- **The vector block was not treated** — finding 1 measured it at five decades below the scalar one.
- **The near/far tier was not tightened** to the pair's own ρ_min against the removed depth, which
  would reach the SAME-level pairs where the remaining cost is. The fill is 5% → 9% of one frequency
  point at N = 4,556 and nothing that works today gets slower, so it does not bind yet.
- **The separation floor below 0.05 µm was not diagnosed** (finding 6). It needs an oracle the
  kernel tier does not have, and it is a MIM-3 Table 1 question rather than a quadrature one.
- **No existing gate was loosened.** `AimAccuracyTests`' 8.7e-7, the L9 gates and
  `Dcim.ValidatedRhoOverLambda*` are untouched. The only pinned literal that moved is
  `MimThinLayerTests.T1`'s cell/separation-20 digest, which the brief names, and its pre-MIM-8 value
  is still pinned beside it.
- **`R-emsev-4`'s refusal was not built**, per `brief-em-run-severity-and-check.md`'s own
  instruction: it is conditional on MIM-8 being declined. The note past the bound now says
  *unmeasured* rather than *wrong*, which is what the measurement supports.

## MIM-8 follow-up — the ACCELERATED path was filling a kernel with the images missing (2026-09-15)

**Found in review, one commit after MIM-8 landed. It is worse than the defect MIM-8 fixed, and on the
same fixtures.**

`MultiLevelPairings.Resolve` stores two views of every scalar pairing: `TermsQ`, with the shallow
images subtracted, and `TermsQFar`, with nothing subtracted. They are complete decompositions of one
kernel **only when the subtracted images are put back in closed form**, which `ScalarPotentialMatrix`
does and which the accelerated operator did not. `PlanarAimBordered` read `TermsQ`/`RemQ` in three
places — the FFT grid kernel table, the near set's exact entries, and the dense via border — and
added nothing back, so every one of them was evaluating a kernel with its dominant term deleted.

**Measured on MIM-3's own plate ladder (0.2 µm, cell/separation 12.5).** The cross-level pairing's
image is amplitude 0.144 at 0.128 µm. At ρ = 0.5 µm the untreated kernel is 22,116 and the
shallow-subtracted one is **−74.8**; at ρ = 10 µm, 1,070 against −74.7. Against the dense
`ScalarPotentialMatrix` the on-demand operator's worst entry was **0.78 of the block's largest** —
compared with the 0.11 cross-level error MIM-8 exists to remove.

**Why no gate saw it.** `AimAccuracyTests`' stacks resolve their own fitted images, so
`split.Removed` is empty there, `TermsQ` and `TermsQFar` are the *same object*, and the two paths
agree trivially. Every fixture that is in the regime — the MIM plates, the coarsely meshed airbridge
— is tested only through the DENSE fill. **A branch whose off-state is the identity is a branch no
existing test exercises**, which is the general shape of this and worth remembering: MIM-8's own
bit-identity discipline is what made the accelerated path look untouched.

**The fix puts the tier where the dense fill has it.**

- The **grid table is sampled from `TermsQFar`**, the whole kernel. It has nowhere to put a closed
  form, and it does not need one: its table is evaluated at ρ = h·√(dp² + dq²), where a peak of width
  |b| ≪ h is smooth anyway. The near correction is `nearExact − AimEntry` over the same table, so it
  cancels exactly and the treated near entries below stay consistent with it.
- **`PlanarPulsePotential` carries the split**, so the near field and the border take the dense
  fill's own near/far choice: the treated view plus the closed-form images for a pair whose ρ range
  reaches the peak, the untreated view for one whose does not. The tier is read off
  `PairClassifier.BandOf(key) < 3`, which is `RuleFor`'s own arithmetic and is `τ < FarRatio` exactly
  — the same test `ScalarPotentialMatrix` makes per pair — rather than a second evaluation of τ that
  could disagree with the class the cores were taken on.
- The closed form is **memoised per translation class**, like everything else in that type. Exact
  here: `1/√(ρ² + b²)` is isotropic, so the integral depends on the pair's shape and offset and on
  nothing else, and a 90° class rotation maps onto itself.
- **Passing a non-empty `shallow` without the untreated view now throws.** The alternative is what
  this entry is about: silently dropping the images from every far pair.

**Gate:** `MimThinLayerTests.T9` — the on-demand operator against the dense matrix on a fixture where
`ShallowQ` is NOT empty (asserted, or the comparison proves nothing). 6.3e-16 after, 0.78 before.

**Left alone:** `PlanarStaticAim` passes no split — its terms come from `StaticScalarAt`, a different
decomposition MIM-8 did not touch — so its grid table is still the whole kernel it always was.
`PlanarPulsePotential.TermsFar` is named and documented for the case where that stops being true.

## Feed band: a port face proud of the copper behind it (2026-09-24, round-7 field report)

An imported Gerber board with a placed part's footprint pad lying over the board's own pad: the
footprint pad (550 µm, the port's face) sat 15.5 µm lower than the board's pad, so it stood 12.5 µm
proud of it on one edge. `FeedBands` demanded metal on EVERY profile cell in each column, so the band
"ended" at the first column where that sliver was empty, and the port's own pad beside the profile came
back as a DRIVEN neighbour 0 µm away — the run was refused, with the port on the true edge or not.
Moving the label never helped because an edge port resolves to the END of the run under it anyway (the
note prints the label's coordinates; the resolution note prints the metal edge it actually used).

- The band now continues while ONE UNBROKEN run of metal overlaps the profile, and ends when no metal
  meets it or the metal that does is split by a gap — so the pad that stops and the coil beyond it
  (the case the rule was written for) still end it. 145 feed/port/calibration tests unchanged;
  `OwnNetFeedNeighbourhoodTests.AFootprintPadProudOfTheBoardPadOnOneSide_IsNotANeighbour` fails on the
  old rule with the field report's exact "0 m away" signature.
- `PlanarFeedClearance` now carries WHERE the nearest neighbour is (`NearestXM/NearestYM`) and the
  refusal prints it. "0 µm away" with no location sent the reviewer looking outside the solve region.

## The far-field metrics stage, factored away from the transform — brief-em3d-31 (2026-09-26)

**What moved.** `PlanarKernel`'s three publishers (`AddFarField` → `AddPattern`, `AddMetrics`, `AddPolarization`)
moved unchanged into `FarFieldStage`, which also assembles the three sets (`Assemble`) and evaluates one
(frequency, port) (`Evaluate`). `PlanarSolve` calls it; so do the 3D backends (src/Design/Em3d/Em3dRadiation.cs).

**How a pattern with no currents reaches the registry.** `PlanarMetricContext` has a second constructor taking a
pattern and `FarFieldExternalTerms` (accepted power, and a solver's own verdicts). `Problem`/`Mesh` are null then,
and exactly three things change behaviour, each keyed on that: the budget (surface-wave and conductor terms
refused by name through `PlanarPowerBudget.ConductorVerdict`, new and null for kernel B), the dominant axis
(`PlanarBeamwidth.AxisFromPattern` — the far field's own polarization at the peak: the (E_x, E_y) ellipse when
the field is mostly lateral, the peak's own azimuth when it is mostly vertical, which is a dipole along z), and
the hemisphere front-to-back sentence. The efficiency ceiling reads `FarFieldExternalTerms.EfficiencyTolerance`
when set, because a 3D solver's two powers come from different integrals.

**Byte identity was dumped FIRST.** `testdata/em3d/farfield/planar-line-golden.npy` was written from the pre-split
code before a line of the split existed (a de-embedded two-port FR-4 line, two frequencies, 22 cubes);
`FarFieldStageTests` holds every cube of it to exact equality, and it held.

**A trap for whoever widens this.** The beamwidth cut and the Ludwig-3 reference are derived per pattern and must
AGREE across the sweep. A pattern that is azimuthally symmetric at its peak (a z-directed dipole: the whole
horizon is the peak) derives its axis from whichever azimuth the peak search lands on, which moves with
frequency, so both are refused for the set with the existing "do not agree" sentence. That is correct — naming
the cut is the remedy — but it will read as a bug on the first vertical radiator anyone runs.

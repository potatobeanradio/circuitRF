# Brief CL3 — turning it on: the goldens, the docs, the phase gate

**Series:** [`brief-conductor-loss-0-overview.md`](brief-conductor-loss-0-overview.md) ·
**Tags:** `R-cl3-n` · **Area:** `src/Engine/Mom`, `docs/user/src/reference` ·
**Depends on:** CL1, CL2

**Read first:** CL1's `RESOLVED.md` §CL1 — **in particular R-cl1-9's table**, because if it did not
converge this brief does not start; **`RESOLVED.md` §QSC and
[`brief-quasistatic-port-calibration.md`](brief-quasistatic-port-calibration.md), plus
`PlanarQuasiStaticLine.cs`'s header and `PlanarPortCalibrator.At` — a calibration path landed after
CL1 that this brief's own accuracy gates are read through, and §0 below is what it changes**;
`tests/Engine.Tests/Mom/QuasiStaticPortCalibrationTests.cs`; the repo `CLAUDE.md`'s test-suite
section in full; `docs/user/src/reference/mom-engine.md:110-150`.

---

## The problem

CL1 and CL2 shipped behind `PlanarFillSettings.ConductorLoss = false`. Nothing the user runs has
changed. This brief flips it, absorbs the consequences, and puts a gate under the result.

**It is wide, not deep.** Roughly 90 files under `tests/Engine.Tests` touch `Planar*`, and every
golden taken on a structure with finite σ moves. That is the whole cost of this brief and it should
be planned as a re-bless, not as a debugging session.

## 0. What moved under this brief before it started: the quasi-static port calibration

**`brief-quasistatic-port-calibration.md` landed after CL1, on 2026-09-14, and it changed how a port
is de-embedded at the bottom of a band.** Below a per-stack crossover a port is no longer calibrated
from two full-wave line standards: γ and Z_c are supplied from the standard's own ELECTROSTATICS
(`PlanarQuasiStaticLine`), where the metal is a perfect conductor by construction and there is no
`Z_s` anywhere in the solve. `PlanarPortCalibrator.At` still solves both standards FULL-WAVE — so
after this brief's flip their `S` carries α_c — but hands `PlanarDeembed.SolveErrorBox` a γ that
does not.

`PlanarCalibration.QuasiStaticCrossoverHz`, per stack (§QSC §3), against R-cl3-3's own frequencies:

| stack | crossover | at 2 / 10 / 20 GHz |
|---|---|---|
| FR-4 1.6 mm starter | **3.048 GHz** | quasi-static / measured / measured |
| low-loss laminate 0.6 mm | 8.654 GHz | quasi-static / measured / measured |
| **MMIC starter, 100 µm GaAs** | **26.07 GHz** | **quasi-static / quasi-static / quasi-static** |

**So on the one substrate class this whole series exists for, every frequency anyone runs is
calibrated with a lossless-conductor γ.** §QSC says the same thing from the other side — *"the
crossover for GaAs comes out at 26 GHz — the whole shipped band is on the quasi-static side of
it."* On a MMIC that is not a small correction: α_c is 92-99% of the line's total loss (overview
§0), and at the bottom of a band it is effectively all of it. **The sizings in this section are
DERIVED from §CL1's and the overview's own measured numbers, not measured here** — they are what
milestone 0 has to replace with measurements: on the GaAs starter's 50 Ω line R_dc = 1/(σtw) puts
α_c at ≈ 1.15 Np/m as f → 0, against a dielectric α_d ≈ 1.7e-4 Np/m at 100 MHz — four decades
down.

**Three consequences, and not one of them is loud.**

1. **The error box absorbs the difference.** `SolveErrorBox` is solved against a SUPPLIED γ, so a γ
   short by α_c makes the assumed `L(Δℓ)` wrong by `e^{−α_c·Δℓ}` and that lands in `a11`/`a21²`.
   Its size is `α_c·Δℓ` with Δℓ = `QuasiStaticSeparationM` (6 substrate heights, so 0.6 mm on GaAs)
   — about **0.015 dB at 10 GHz** — small, smooth, and in the direction that makes a DUT look
   better than it is.
2. **Z_c is short of its own R correction, and the error box's own gate looks at the wrong half of
   it.** `PlanarDeembed.CharacteristicImpedance` is D7's `γ/(jωC′)`, so a γ carrying R would give
   Z_c its correct complex part **for free** — the fix, if there is one, is entirely in γ's source.
   The scale is `√(1 + R/(jωL))`. On the GaAs starter R/(ωL) is ≈ 0.013 at 10 GHz (0.6% in
   magnitude, 0.4° in phase) and ≈ 0.38 at 100 MHz — **3.5% in magnitude and about 10° in PHASE**.
   QSC's overlap gate compares **|Z_c| only**, at a 1.5% threshold, so the larger of the two errors
   is the one nothing looks at.
3. **γ itself is a published result.** `planar.Gamma` and `planar.Zc` are diagnostic cubes and
   `planar.CalQuasiStatic` already says which path produced each point. After the flip those cubes
   carry α_c above the crossover and not below it — **a step, at the crossover, in a quantity that
   is physically smooth**, on a sweep that straddles it.

**The existing overlap gate cannot see any of this.**
`QuasiStaticPortCalibrationTests.TheQuasiStaticPathAgreesWithTheMeasuredOneWhereBothRun` compares
β, |Z_c| and max|ΔS| at 1.5% / 1.5% / 3e-2 — **α is not compared at all, and neither is arg Z_c**,
because with PEC metal both paths carried the identical dielectric α and the comparison would have
been vacuous. It is vacuous
no longer, and R-cl3-5 is what makes it look.

**None of this is a defect in QSC.** `PlanarQuasiStaticLine`'s header states the assumption by name
and says why — *"the metal stays a PERFECT CONDUCTOR here, exactly as the full-wave fill is by
default (CL1's `PlanarFillSettings.ConductorLoss` is off)"*. **This brief is what makes that sentence
false**, and milestone 0 is where it is answered rather than left to be discovered in a re-bless.

## Milestones

0. **Decide where the quasi-static path's γ gets its conductor term, and MEASURE the decision.
   This milestone GATES the flip; it is not a follow-up to it.** Three candidates, and QSC's own
   R-qsc-2 doctrine already rules on two of them:
   - **Kernel A's Wheeler term.** `PlanarQuasiStaticLine`'s header rejects it by name and D7's rule
     is the reason — kernel A is the ORACLE for these quantities, never an input, and a γ taken
     from A would import A's discretisation into B's answer and make milestone 5's A-vs-B gate a
     tautology. It would also over-read by the 27-37% §CL1 measured between the loaded sheet and
     kernel A, so the two calibration paths would disagree in α across the crossover by exactly the
     single-sheet model's own deficit.
   - **CL1's own `PlanarSurfaceImpedance.Sheet`, over the standard's static current distribution.**
     Self-consistent in the discretisation — QSC's own fourth argument, C and C₀ off the same mesh
     — and it is the same object the fill loads, so no second loss model enters the repository.
     What it cannot reproduce for free is kernel B's **edge crowding**: §CL1 measured the loaded
     EFIE's own factor at **1.35** on GaAs against a uniform-current 1.0, and a static distribution
     is not the solved one. Measure that gap rather than assuming it is the whole story.
   - **Leave γ lossless below the crossover and SAY SO.** This is a legitimate outcome, provided the
     run's notes and `planar.CalQuasiStatic` carry it and milestone 6's page states it with its
     measured size. **What is not legitimate is publishing an α that steps at the crossover with
     nothing saying it does** — QSC's own "Must NOT" already forbids presenting a quasi-static
     answer as a measured one, and this is the same rule one quantity over.

   Measure the first two against the measured path **in the overlap region**, which is what the
   overlap exists for: on FR-4 the crossover is 3.05 GHz and both paths run on either side of it.
   **Record the decision and its numbers in `RESOLVED.md` before touching the default**, because
   after the flip every re-bless in milestone 3 is taken through whichever path this chose.

1. **Flip the default.** `PlanarFillSettings.ConductorLoss = true`. The flag stays, permanently, as
   the PEC oracle — on the pattern of `UseSymmetricFactorization = false` and
   `UseRadialTable = false`, and for the same reason: every CL1 and CL2 accuracy gate compares
   against it.
2. **Triage the suite ONCE, from the TRX.** Run `dotnet test tests/Engine.Tests` once, read
   `tests/Engine.Tests/TestResults/last-run.trx`, and **classify every failure before changing
   anything**:
   - *Expected move* — a golden on lossy metal. Re-bless, and record the size of the move.
   - *Unexpected move* — a golden that should not have been sensitive (a reciprocity check, a
     passivity check, a mesh count, a PEC fixture). **Each one is a finding**, not a re-bless.
   - *Load-dependent* — attribute it per the repo `CLAUDE.md`'s three-check procedure before
     blaming the change.
   **Do not re-run the suite to find out what broke.** The TRX carries every failure message, stack
   and captured stdout.
   **The de-embedding families are where the two paths of §0 show up and they should be read
   together, not file by file:** `QuasiStaticPortCalibrationTests`, `PlanarDeembedTests`,
   `CoplanarDeembedTests` (whose fixtures are `EmMaterial(1.0, 0.0)` — an AIR slab, crossover
   1.20 GHz, so most of them are on the quasi-static side), `PlanarCalibrationGroupTests` and
   `PlanarModalCalibrationTests` (a calibration GROUP declines the quasi-static path by name and
   keeps the measured ladder, so those move for the ordinary reason and not for §0's).
3. **The re-bless, with the moves tabulated.** Every re-blessed golden gets its delta recorded in
   `RESOLVED.md`. A golden that moved by an implausible amount is the most likely place a sign error
   survives every gate CL1 and CL2 ran, because those gates were all on uniform lines and simple
   patches. **A bend, a stub, a spiral and a MIM fixture are where a wrong `Z_s` looks different.**
4. **Passivity and reciprocity get stricter, not looser.** Adding loss cannot make a passive
   structure active. If `PlanarPassiveNeighbourTests` or the reciprocity gates move in the wrong
   direction, stop — that is a real defect and this brief's re-bless would bury it.
5. **The A-vs-B agreement gate.** §10.9's kernel-A-versus-kernel-B comparison on a uniform line
   currently diverges on loss *by construction* and its text says so. It now compares two models
   that both have conductor loss. **Re-state what it gates**, and report the new agreement on ε_eff
   AND on α, on both starters, at the frequencies §5 already uses.
6. **The user-facing page.** `docs/user/src/reference/mom-engine.md:130-143`'s
   "Conductor loss, in the full-wave kernel" entry leaves the "Cannot" list. What replaces it is not
   a triumphal note but an honest one: conductor loss IS modelled, on the signal metal; the ground
   plane's share is or is not modelled depending on whether CL4 ran; and R-cl1-9's convergence
   result is what the accuracy claim rests on. **If CL4 has not run, the ground-plane omission is
   stated with its measured size** — 21% (FR-4) / ~11% (GaAs) / 25% (low-loss laminate) of the
   conductor term, overview §2.
7. **The sentence that was wrong anyway.** Whether or not it was already fixed per overview §4, this
   page must end up stating the FR-4 figure as an FR-4 figure and carrying the MMIC row beside it.
8. **Cost.** Fill and factor wall clock, and a full sweep, before/after on the hero, in `HISTORY.md`.
   Expected: unmeasurable. If it is measurable, the Gram matrix is being rebuilt per frequency.

## Gates

- **R-cl3-1** — the whole of `tests/Engine.Tests` green, every move accounted for in one of the
  three triage classes, and every re-bless justified by a number.
- **R-cl3-2** — `tests/Ui.Tests/Em` green: the `.cem` round trip, `EmRunService`, and the CLI `em`
  verb's byte-for-byte comparison against `EmRunService.Run`. **Nothing in this series adds a `.cem`
  key**, so the round trip must be bit-identical, and `EmSnpProvenance` already hashes σ and
  thickness (`EmSnpProvenance.cs:219`) so cached results invalidate correctly with no change. Assert
  that rather than assuming it.
- **R-cl3-3 — the phase gate, and it is one sentence per starter.** On the FR-4 hero and on the
  MMIC starter, a de-embedded uniform 50 Ω line's α from kernel B agrees with kernel A's total α
  within a stated band, at 2 / 10 / 20 GHz, **with the ground-plane term treated the same way in
  both kernels**. State the band from the measurement; do not choose it first and tune to it.
  Overview §0's α_c and α_d tables are the numbers to land beside.
- **R-cl3-4** — the five heroes' S-parameter regressions unchanged, because none of them routes
  through kernel B. Confirm rather than assume.
- **R-cl3-5 — the overlap gate compares α now.**
  `QuasiStaticPortCalibrationTests.TheQuasiStaticPathAgreesWithTheMeasuredOneWhereBothRun` gains
  `Re(γ)` **and `arg Z_c`** beside its β, |Z_c| and max|ΔS| comparisons, **with thresholds taken
  from the measurement rather than chosen first**. It is the only test in the suite that puts the two
  calibration paths side by side on one geometry, and after this brief it is the only thing that
  would notice an α that steps at the crossover. If milestone 0 chose the third option, this gate
  records the step's measured size instead of forbidding it — it does not get deleted.
- **R-cl3-6 — every accuracy number says which calibration produced it.** R-cl3-3's α at 2 / 10 /
  20 GHz, and every golden in milestone 3 whose move is attributed to conductor loss, names the path.
  `PlanarCalibrationSource` is already on `PlanarPortCalibration` and `planar.CalQuasiStatic` is
  already in the `.npy`, so this costs nothing to report and is unrecoverable afterwards if it is not.
  **Read α from the de-embedded S₂₁ of a line of known length, NOT from the calibration's own γ**:
  below the crossover the latter is the supplied quasi-static one and would report α_c ≈ 0 whatever
  the fill did — which reads as a total failure of CL1 rather than as the calibration artefact it
  would actually be. On the MMIC starter all three of R-cl3-3's frequencies are below the crossover,
  so this is not a corner case there; it is the whole measurement.

## Must NOT

- Re-run the full suite to discover a failure. Read the TRX.
- Run two test processes at once — they share `bin/` and the TRX and corrupt each other.
- Re-bless a golden whose move you cannot explain.
- Gate on `Category=Benchmark`. Measure a new slow test and tag it only if it is at or above ~5 s.
- Add a user control, a `.cem` key, or a way to turn conductor loss off from the GUI.
- Claim CL4's accuracy. If the ground plane is still PEC, the page says so with a number.
- **Re-point the quasi-static γ onto `RlgcExtractor`.** QSC's R-qsc-2 and `PlanarDeembed`'s own D7
  rule both refuse kernel A as an input, and milestone 5's A-vs-B gate would become a tautology. If
  milestone 0's measurement says A is the only supplier that closes the gap, **that is a finding to
  report and the decision is the owner's** — it is not this brief's to take quietly.
- **Raise the crossover to dodge §0.** It is measured, fixed per stack, and deliberately not a user
  setting (§QSC §3) precisely because both of its failure modes publish a smooth, plausible, wrong
  phase. Lowering it is the conservative direction but brings back the long standards the whole of
  QSC exists to remove — on the reported board a 161.5 mm standard at 79,055 unknowns against a
  12,000 ceiling.
- **Turn `PlanarCalibrationSettings.QuasiStaticBelowCrossover` off to make a golden behave.** It is
  the pre-QSC oracle, on exactly the terms `ConductorLoss` is the pre-CL1 one, and a test that flips
  it is testing a path no user runs.

## On completion

- `src/Engine/Mom/RESOLVED.md` `§CL3`: the triage table, every re-blessed golden with its delta, the
  A-vs-B agreement, the phase-gate sentences, the cost measurement, **and milestone 0's decision with
  the numbers it was taken on**.
- `src/Engine/Mom/RESOLVED.md` §QSC's own "the metal stays a PERFECT CONDUCTOR here" paragraph, and
  `PlanarQuasiStaticLine.cs`'s matching header paragraph, are **stated against a default this brief
  flips**. Both already carry a forward pointer to this milestone; update them to what was decided
  rather than leaving a note describing a state the code is no longer in.
- `docs/design/mom-engine.md` §10.9's blockquote is rewritten — it is a design note and is edited
  normally. `docs/user/src/reference/mom-engine.md` per milestones 6 and 7.
- **No `CLAUDE.md` edit.** By this point `src/Engine/Mom/CLAUDE.md` §5's conductor-loss bullet, §7's
  entry and §3.7's metrics text are all stale. **Report all of them to the owner by file and line**
  in the completion message. This is the brief where that report is most important, because after
  CL3 the standing project memory says the kernel does something it no longer does.

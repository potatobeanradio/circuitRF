# Sonnet Brief — PCAL-6: which calibration separation a grouped port uses

**Read `brief-portcal-0-overview.md` first**, and `brief-portcal-4-modal-error-box.md` — this brief is
about the input PCAL4's modal extraction is handed, not about the extraction itself. The write-ups
that bound it are `src/Engine/Mom/RESOLVED.md` §PCAL5, §LF1 §5(b) and §LF3.

**Scope: the rule that picks Δℓ for a calibration GROUP, and the refusal that rule can cause.** No
change to the modal error box, to the clearance rules, to the floor itself, or to anything a
single-port calibration does. One decision is in scope — `PlanarCalibration.SelectSeparation` and
who asks it.

> **The obvious fix is refuted by measurement (§4).** Do not start by making the selection
> separability-aware. M1 is an isolation milestone and it exists because the first thing this brief
> tried was the obvious fix and the data says it would not have bound.

---

## 1. What the owner sees, measured

A four-port imported PCB, 0.9 mm of εᵣ 4.4 between the signal level and the return plane, four edge
ports of which two (1 and 3) are close enough to be calibrated together as one group. The sweep is
0 Hz + 100 MHz … 1 GHz in 100 MHz steps. Everything below is `EmRunService.Run` on that file, with
LF3's mesh and accelerator recoveries already in place — so these are the refusals that remain after
the two that were fixed:

| sweep (linear, GHz) | points | outcome | what the refusal reported |
|---|---|---|---|
| 0 … 1, 11 pts | 0, 100, 200, … 1000 MHz | **Refused** 51.9 s | at 200 MHz, 0.178° against a 0.50° floor |
| 0.1 … 1, 4 pts | 100, 400, 700, 1000 MHz | Ok 39.6 s | — |
| 0.15 … 1, 4 pts | 150, 433, 717, 1000 MHz | Ok 29.6 s | — |
| 0.2 … 1, 4 pts | 200, 467, 733, 1000 MHz | **Ok** 25.6 s | — |
| 0.25 … 1, 4 pts | 250, 500, 750, 1000 MHz | **Refused** 6.5 s | at 250 MHz, 0.344° |
| 0.3 … 1, 4 pts | 300, 533, 767, 1000 MHz | Ok 9.2 s | — |
| 0.5 … 1, 3 pts | 500, 750, 1000 MHz | Ok 5.2 s | — |

**Read rows 1 and 4 together. 200 MHz is refused in one sweep and accepted in another.** Same board,
same ports, same group, same floor. That is the shape of the defect: the outcome at a frequency is
not a property of that frequency.

---

## 2. There are TWO separation checks, and it is the second one that fires

They are easy to confuse — they refuse the same thing with different words, at different times,
against different arithmetic.

| | setup guard | per-frequency refusal |
|---|---|---|
| where | `PlanarSolve.GuardModeSeparation`, called once at `PlanarSolve.cs:2095` | `PlanarSolve.cs:2234` |
| asked at | `fLo` only — the band's bottom | every solved point |
| quantity | `PlanarPortCalibrator.QuasiStaticModeSeparationDegrees` — from the ELECTROSTATIC modes, before any Green's-function fit | `PlanarModalErrorBox.ModeSeparationDegrees` — the smallest \|(γ_i − γ_j)·Δℓ\| off the SOLVED cascade's Durand–Kerner roots |
| exception | `PlanarFeedClearanceRefusedException`, "would be calibrated together … at *f* the closest pair differs by …" | `PlanarFeedClearanceRefusedException`, "are calibrated together … and at *f* their N modes are not separable …" |

**On this board the setup guard passes and the sweep is refused in the middle of it**, 51.9 s in.
That is worth fixing on its own terms whatever else changes: R-pcal4-6's whole argument for the
quasi-static guard is that a group which cannot be calibrated should be refused *before* the sweep is
paid for, and here it was not. Either the quasi-static estimate disagrees with the measured quantity
by more than the floor, or asking only at `fLo` is not enough. **M1 must say which.**

---

## 3. The rule, and how few candidates it chooses from

`PlanarCalibration.SelectSeparation(deltaLM, expectedBeta)` picks the index minimising

```
|log( βΔℓ / √(UsableLo · UsableHi) )|      = |log( βΔℓ / 56.6° )|
```

— the candidate whose predicted electrical length is closest, in log, to the geometric centre of
TRL's usable interval. **It is a statement about ONE mode's length. Modal separability is not an
input to it and never has been.**

The candidates come from `PlanarCalibration.SuggestDeltas`, which emits
`n = ceil(log(fHi/fLo) / log 8)` separations, one centred on each sub-band's geometric mean. Measured
on this stack:

| band | n | candidate Δℓ |
|---|---|---|
| 100 MHz – 1 GHz | 2 | 171.0 mm, 54.1 mm |
| 200 MHz – 1 GHz | 2 | 101.7 mm, 45.5 mm |
| **300 MHz – 1 GHz** | **1** | **55.5 mm** |
| 100 MHz – 20 GHz | 4 | 156.8, 41.7, 11.1, 2.9 mm |

**A decade of band gets two candidates and a 3.3:1 band gets one.** Any fix phrased as "choose a
better candidate" has almost nothing to choose from, and on the 300 MHz – 1 GHz band it has no choice
at all. That is a hard constraint on the solution space, not a detail.

What each candidate costs is already severe and must not be forgotten: on this board de-embedding
already builds **6 standard meshes totalling 62.64× the DUT's unknowns**, solved at every frequency.
Adding a candidate adds a mesh to every grouped port.

---

## 4. The obvious fix is refuted, and isolating why is milestone 1

The obvious fix is: for a group, choose among candidates that clear the floor, and only then optimise
electrical length. `ModeSeparationDegrees` is \|Δγ·Δℓ\|, so a longer Δℓ should separate the modes
proportionally better.

**Measured, it does the opposite.** At 200 MHz:

| band the standards were built for | Δℓ chosen | predicted βΔℓ | measured separation |
|---|---|---|---|
| 100 MHz – 1 GHz | **171.0 mm** | 67.5° | **0.178° — refused** |
| 200 MHz – 1 GHz | **101.7 mm** | 40.1° | **≥ 0.50° — accepted** |

The **longer** separation measured **2.8× worse**. If the reported quantity were \|Δβ\|·Δℓ it would
have been 1.68× *better*. So one of these is true, and M1 must establish which:

1. **The measured separation is wrong at the longer Δℓ** — a 2π unwrap or a mode-assignment failure
   in the Durand–Kerner roots / null-space step, in which case `ModeSeparationDegrees` is reporting a
   number that is not the modes' distance and the selection rule is innocent. **If this is the
   answer, fixing it is the whole fix** and no selection rule changes.
2. **The selection rule is innocent and the short line is the variable.** `BuildSet` builds element 0
   from `SuggestLengths(slab, fLo, fHi)`, which *also* depends on the band — so changing `fLo` moved
   the short line as well as the deltas. The two runs above are therefore **not a clean comparison of
   Δℓ**, and the brief says so rather than resting on it.
3. Something else, which M1 names.

**R-pcal6-2. Nothing in the selection rule changes until §4 is explained.** The experiment is cheap
and does not need a sweep: hold the short line and the port fixed, build long lines at a ladder of
Δℓ, and measure `ModeSeparationDegrees` at one frequency across that ladder. If it is monotone in Δℓ,
(2) is the answer. If it is not, (1) is, and the ladder shows where it breaks.

---

## 5. A second defect, independent of the first: the choice depends on sweep HISTORY

```csharp
private double ExpectedBeta(double fHz) =>
    double.IsNaN(_prevBeta) ? PlanarCalibration.EstimateBeta(_slab, fHz)
                            : _prevBeta * (fHz / _prevF);
```

The first point of a sweep uses the electrostatic estimate; every later point extrapolates from the
**previously solved** point. `SelectSeparation` is then asked with that number, so **the standard a
frequency calibrates against depends on which frequencies preceded it.** Two sweeps containing
200 MHz can publish two different S-matrices there, and neither is marked.

This is reachable today (rows 1 and 4 of §1 differ in exactly this way, among others) and it is
fixable on its own. It is in scope because any rule written on top of a history-dependent input
inherits the same problem.

---

## 6. Requirements

**R-pcal6-1 — A run that succeeds today must be bit-identical after this brief.** The selection may
differ only where the current choice leads to a refusal. This is the invariant that makes the change
safe to ship: §LF1 §5(b) declined this work on the grounds that it "changes de-embedded answers for
every grouped port", and confining it to runs that currently *stop* is what removes that objection.
Assert it as exact equality on a grouped fixture that passes today, not as a tolerance.

**R-pcal6-2 — Isolate before changing.** §4. A remedy that does not bind is this directory's most
expensive recurring mistake and it is written into three separate refusals already.

**R-pcal6-3 — One place decides the separation, and every caller asks it.** `SelectSeparation` is
asked at four sites today: `QuasiStaticModeSeparationDegrees` (`PlanarSolve.cs:527`), `ModalAt`
(:561), `At`/`GammaBest` (:685) and — critically — **`PrepareAt`, which decides which standard meshes
are SOLVED at all**. A group-aware rule reachable from three of the four means a run solves one
standard and calibrates against another, silently. Whatever replaces it is asked by all of them or by
none.

**R-pcal6-4 — The separation a frequency uses must not depend on the frequencies before it**, or the
dependence must be deliberate, documented and reported. §5.

**R-pcal6-5 — If the fix needs a candidate the set does not contain, `SuggestDeltas` grows and the
cost is stated in the same breath.** Each extra separation is another standard mesh solved at every
frequency, on a port whose standards already run 62.64× the DUT. A candidate added for a band that
does not need it is a permanent tax on every grouped run.

**R-pcal6-6 — The floor does not move and a refusal stays a refusal.**
`ModeSeparationFloorDegrees = 0.5` exists because at equal eigenvalues the null space of (M − μI) is
a plane and no arithmetic recovers which line in it is which mode. When no candidate clears it, the
run still refuses — and with the same remedy it names today.

**R-pcal6-7 — The setup guard must catch what the sweep would refuse.** §2. Today it passes a run
that dies 51.9 s later. Either the quasi-static quantity is brought into agreement with the measured
one, or the guard is asked at more than `fLo`, or the discrepancy is measured and stated as a known
gap. Silently passing is not one of the three.

---

## 7. Milestones

**M1 — isolate §4.** The Δℓ ladder at fixed short line and fixed frequency; `ModeSeparationDegrees`
and `NullSpaceGap` across it, plus `PalindromeResidual` and `CascadeResidual`, which the file header
already names as the first things to look at when a group's answer is wrong. **Report before
changing anything.** If the answer is (1), M2 and M3 collapse into "fix the measurement".

**M2 — decide, and write down the decision.** Selection rule, measurement fix, wider candidate set,
or a combination — with the §4 evidence attached.

**M3 — implement, through one decision point (R-pcal6-3).**

**M4 — the history dependence (§5).** Separable from M1–M3 and may land first.

**M5 — the setup guard (R-pcal6-7).**

---

## 8. Gates

- **Bit-identity.** A grouped fixture that calibrates today produces the same S to the last bit.
  `PlanarCalibrationGroupTests` has the fixtures.
- **The defect itself, as a unit test rather than a board.** A grouped fixture where the same
  frequency is accepted in one sweep and refused in another — §1 rows 1 and 4 reduced to a fixture.
  If that cannot be built, say so and explain why, because it means the mechanism is still not
  understood.
- **The candidate ladder from M1**, kept as the measurement it is.
- **End to end, through `EmRunService.Run`**, on a four-port grouped fixture over a decade band with
  0 Hz in it — the shape of the owner's file. Not `Category=Benchmark`: it is seconds, and it is the
  only gate that exercises the setup guard, the per-frequency refusal and LF3's two recoveries
  together.
- **No new timing assertions.** If cost has to be shown, assert a COUNTER (standard mesh count,
  unknowns) — the standing rule in this repository.

---

## 9. Non-goals

- The modal error box, the clearance rules, `MaxCalibrationGroupSize`, and every single-port
  calibration path.
- The de-embedding cost itself. A standard's N growing as 1/f is real and documented
  (`docs/design/mom-engine.md` §10.13c) and it is a different piece of work.
- The low-frequency port model. §10.13's own conclusion is that the enabling change down there is a
  port whose a₂₁ does not vanish with ω; that is not this brief.

---

## 10. On completion

Write the findings to **`src/Engine/Mom/RESOLVED.md`** as a `## PCAL6` section — **not to
`CLAUDE.md`**, which stays for durable, still-true conventions only. Correct
`brief-portcal-0-overview.md`'s table in place, and correct §LF1 §5(b) and `docs/design/mom-engine.md`
§10.13, both of which currently describe this as open. If M1 refutes this brief's own framing, say so
in the write-up in as many words — a brief that turned out to be wrong about its own defect is worth
more recorded than quietly rewritten.

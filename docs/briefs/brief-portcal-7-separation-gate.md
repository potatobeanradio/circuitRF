# Sonnet Brief — PCAL-7: the mode-separation refusal is drawn on the wrong quantity

**Read `brief-portcal-6-separation-selection.md` first, and `src/Engine/Mom/RESOLVED.md` §PCAL6
before anything else** — this brief exists because PCAL6 diagnosed a defect, built a remedy for it,
measured the remedy against the oracle, and **removed it**. The diagnosis stands. The remedy was
wrong. What follows is the second attempt, and its whole design is shaped by how the first one
failed.

**Scope: which quantity the per-frequency mode-separation refusal is drawn on.** No change to the
modal error box, to the floor's VALUE, to the clearance rules, to `SelectSeparation`, or to anything
a single-port calibration does. No new calibration standards — this brief must cost nothing.

> **The one instruction that matters more than the rest: measure the ANSWER, not a proxy.**
> PCAL6 failed because it measured that a remedy made a *reported quantity* agree with the truth and
> inferred that the de-embedded s-parameters had improved. They had got worse — by 14× at the bottom
> of a decade band. Every requirement below is written to stop that happening twice.

---

## 1. What the user sees, and it is unchanged since PCAL6

A four-port imported PCB, 0.9 mm of εᵣ 4.4, four edge ports of which two pairs are close enough to be
calibrated together. A sweep whose lowest AC point is 200 MHz is **refused**:

> Ports 1, 3 are calibrated together as one group, and at 200 MHz their 2 modes are not separable:
> the closest pair differs by 0.192° of electrical length over the calibration separation, against a
> floor of 0.50° (the group's own electrostatics puts the same quantity at 4.701°, on a short
> standard of 3830 µm).

**Read the parenthesis.** That clause is PCAL6's own contribution and it is the whole of this brief:
the two numbers in it differ by 24×, and PCAL6 established which of them is trustworthy. A sweep
starting at 300 MHz or above clears the floor today, which is the workaround and is not a fix.

---

## 2. What PCAL6 established, and what it did not

**Established, and not to be re-litigated:**

- `ModeSeparationDegrees` — the smallest |(γ_i − γ_j)·Δℓ| off the solved cascade's roots — **is not
  the modes' distance** at the bottom of a band. Laddered at 200 MHz on the series' own pair it reads
  between **0.05× and 17×** the quasi-static truth depending on Δℓ alone, and the near-zero readings a
  run refuses on are two corrupt curves *crossing*.
- The **quasi-static** separation (`PlanarPortCalibrator.QuasiStaticModeSeparationDegrees`) is stable
  to four digits across that same ladder, as a property of the cross-section must be.
- What corrupts the measured one is the SHORT standard's own electrical length — 3 substrate heights
  is 1.5° of line at 200 MHz — not Δℓ.
- Growing that standard **fixes the reported separation and breaks the answer.** Removed.

**NOT established, and it is this brief's question:** whether a small MEASURED separation, on a group
whose quasi-static separation is healthy, ever corresponds to a bad de-embedded answer.

There is **one** data point, and it says no. On the committed `coupled-pair` at its shipping mesh,
100 MHz: measured separation **1.05°** (under-reading a quasi-static 4.7° by 4.5×), de-embedded
answer **0.1298** in max |ΔS| against a well-separated floor of **0.2682** — comfortably good. One
point is not a basis for moving a safety gate, which is why this is a brief and not a change.

---

## 3. Why the gate might legitimately stay where it is

The counter-argument is real and must be answered by measurement rather than waved past.

`ModeSeparationFloorDegrees` exists because **at equal eigenvalues the null space of (M − μI) is a
plane and no arithmetic recovers which line in it is which mode.** That is a statement about the
SOLVED cascade — the thing the measured separation describes — and *not* about the cross-section.
A group whose modes are 4.7° apart in principle but whose two standards produced eigenvalues 0.19°
apart has an extraction that is genuinely ill-conditioned, whatever the electrostatics says. Moving
the gate to the quasi-static quantity would let exactly that case publish.

So the question is not "which number is more accurate" — PCAL6 settled that — but **"does the
ill-conditioning the measured number reports actually reach the answer?"** The single data point says
it did not on that one run. It may on others.

---

## 4. The candidates, so this is not a one-option brief

| | what it does | what makes it doubtful |
|---|---|---|
| **(a) Gate on the quasi-static separation** | the refusal becomes a statement about the metal, which is what its remedy sentence already says | lets a genuinely indeterminate extraction publish (§3) |
| **(b) Keep the gate, lower the floor** | cheapest possible change | the under-read factor is not a constant — 0.05× to 17× — so no lowered floor is right at two frequencies at once |
| **(c) Gate on `NullSpaceGap`** | it is the DIRECT measure of "is this eigenvector determined", and it is already computed and reported | PCAL1 §5 measured that residuals of this family do not predict the de-embedding error; this would be predicting INDETERMINACY rather than error, which is a different claim and has to be shown |
| **(d) Gate on both: refuse only when the measured AND the quasi-static separation are under the floor** | keeps every refusal that is about the metal, drops the ones that are about the instrument | the same objection as (a), narrowed; needs the same measurement to justify |

**(a) and (d) are the favoured pair and (d) is the conservative form of (a).** (b) is almost certainly
dead on the numbers above and is listed so nobody re-derives it. **M2 picks with the evidence, not
before it.**

---

## 5. One structural consequence, which is a simplification

**If the gate moves to the quasi-static quantity, the per-frequency refusal becomes redundant and
must be DELETED rather than left as a second spelling.** PCAL6's M5 already makes
`PlanarSolve.GuardModeSeparation` ask the quasi-static question at **every requested frequency**, at
setup, before a single solve is paid for. A per-frequency guard asking the identical question of the
identical quantity is a second answer waiting to disagree with the first — which is exactly the class
of defect R-pcal6-3 was written for, and PCAL6 found a live instance of it.

So under (a) the measured separation stops being a gate and becomes what it always should have been:
a reported diagnostic, already in the run's `MODAL CALIBRATION` note per frequency. Under (d) the
per-frequency guard survives with an extra condition. **Either way there is one question and one
place that asks it.**

---

## 6. Requirements

**R-pcal7-1 — Every candidate is scored on the DE-EMBEDDED ANSWER against the kernel-A oracle, and
nothing is decided on a reported quantity.** This is PCAL6's failure written as a rule. A change that
makes `ModeSeparationDegrees`, `NullSpaceGap` or any residual look better is not evidence of anything
until the s-parameters have been compared to the oracle on the same file.

**R-pcal7-2 — Kernel A is the ORACLE and must not become an INPUT.** Overview §3b and R-pcal4-5.
Scoring kernel B's answer against kernel A is what PCAL4's own gate 1 does and is allowed; reading
γ_m, Tv or Z_c off kernel A and feeding it into the de-embedding is the forbidden shortcut, and it
will look tempting again here. The quasi-static separation this brief may gate on comes from **kernel
B's own standards' electrostatics**, which is what D7 already takes, and not from kernel A.

**R-pcal7-3 — A run that publishes today must be bit-identical, and the runs that would newly REFUSE
must be found and reported rather than discovered by a user.** Moving a gate usually only admits more
runs, but not here: the measured separation *over*-reads at small Δℓ by up to 17×, so a run that
passes today on an inflated number would refuse on an honest one. **That population is not
hypothetical and M2 must size it**, on the committed fixtures at minimum. If it is non-empty, the
brief reports it and the decision is taken knowingly.

**R-pcal7-4 — The floor's VALUE does not move.** 0.5° is not this brief's subject; which quantity it
is compared against is.

**R-pcal7-5 — One question, one place that asks it.** §5. If the per-frequency guard survives it
survives with a stated reason; if it does not, it is deleted rather than commented out.

**R-pcal7-6 — Cost is zero, or the brief stops.** No new standard, no extra solve, no longer line.
Every candidate above is arithmetic on quantities the run already computes. A remedy that costs
standards is PCAL6's remedy again and it is refuted.

**R-pcal7-7 — When a group is still refused, the refusal names a remedy that BINDS.** Today's sentence
says "separate the feeds", which is right when the refusal is about the metal. When the two numbers
disagree the binding remedy is different — raise the sweep's lower edge — and the refusal should say
so, because that one actually works: the same fixture clears the floor from 300 MHz up.

---

## 7. Milestones

**M1 — the measurement, and report before changing anything.** A family of grouped fixtures spanning
the floor, each scored against the oracle. The family has to vary the measured separation
INDEPENDENTLY of the quasi-static one, which PCAL6 showed how to do: Δℓ alone moves the measured
number over two orders while the cross-section's own separation does not move at all. So sweep
(gap, frequency, Δℓ) over the committed uniform-cross-section fixtures — `coupled-pair`,
`coupled-asym`, `coupled-triple` — and for each point record: the measured separation, the
quasi-static separation, `NullSpaceGap`, and **max |ΔS| against kernel A, with the A-vs-B floor
re-measured on the same geometry.**

The single question M1 answers: **plotted against the measured separation, does the oracle error ever
go bad while the quasi-static separation is healthy?** If it never does, (a) is safe. If it does, the
measured separation is carrying real information and (d) or (c) is the answer.

**M2 — decide, with the table attached, and size R-pcal7-3's population.**

**M3 — implement, through one decision point, deleting whatever becomes redundant.**

---

## 8. Gates

- **The M1 table, kept as the measurement it is.** It is the brief's whole product and it will not
  fit in the routine test tier; it is a harness, per the standing rule.
- **Bit-identity** on a grouped fixture that publishes today, as exact equality — PCAL6's
  `AGroupedSweepThatCalibratesTodayIsBitIdentical` is the pattern and its literals are already in the
  tree.
- **The thing the user actually asked for**: the owner's sweep shape — a decade band with 0 Hz in it,
  on a four-port grouped board — publishes, and its 200 MHz row is scored against the oracle rather
  than merely produced. `tests/Ui.Tests/Em/GroupSeparationRefusalTests.cs` is the test that has to
  change, and its name with it.
- **A group that is genuinely degenerate still refuses**, and a test says so by construction rather
  than by luck — PCAL6's `GenuinelyDegenerateModesStillRefuse`, which must keep passing.
- **No new timing assertions.** Assert a counter if cost has to be shown; §R-pcal7-6 says there is
  none to show.

---

## 9. The harness, which already exists in outline

PCAL6's accuracy measurements were made with committed fixtures through the real `em` verb in
Release, and **the harness was validated before it was trusted**: it reproduces §PCAL4's published
1–7 GHz numbers to the last digit, floor included. Rebuild it the same way —

- the fixture's `.cem`, with `Frequency` rewritten and `AnalysisKind` set to `Planar` for kernel B or
  removed for kernel A;
- `dotnet run -c Release --project src/Cli -- em <cem>`, which writes to a predictable path;
- the floor for a fixture is that fixture with its second conductor moved 9 mm in y, both kernels,
  same frequencies;
- the score is max |ΔS| over the matrix, per frequency.

**Validate it against §PCAL4's numbers again before quoting a new one.** That step is what made
PCAL6's negative result trustworthy rather than arguable.

---

## 10. Non-goals

- The modal error box, the floor's value, `SelectSeparation`, `MaxCalibrationGroupSize`, and every
  single-port calibration path.
- **§LF1 §5(a)'s peel amplification.** It is the reason low-frequency de-embedding degrades at all,
  it is measured, and the enabling change is a port whose a₂₁ does not vanish with ω. Not this brief,
  and a group is already below the single-port floor at every frequency down to 100 MHz.
- The de-embedding standard's own 1/f cost (§10.13c). Below ~20 MHz de-embedding stops for that
  reason and this brief does not reach it.
- Making the short standard longer, in any form, for any reason. Refuted; see §PCAL6.

---

## 11. On completion

Write the findings to **`src/Engine/Mom/RESOLVED.md`** as a `## PCAL7` section — **not to
`CLAUDE.md`**, which stays for durable, still-true conventions only. Correct §PCAL6's own closing
paragraph, which names this as open, and `brief-portcal-0-overview.md`'s table.

**If M1 shows the gate is where it belongs, say so and stop.** A brief that concludes "the existing
behaviour is correct and here is the measurement that proves it" is a complete and valuable result,
and it is the outcome this brief is most at risk of talking itself out of.

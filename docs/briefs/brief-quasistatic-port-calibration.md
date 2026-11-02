# Brief QSC — a quasi-static port calibration, so the bottom of the band stops costing a metre of line

**Tags:** `R-qsc-n` · **Area:** `src/Engine/Mom`

**Read first**, end to end, before planning anything: `src/Engine/Mom/RESOLVED.md` §RAW1 (the bug
report this came out of, and §6 in particular — its option (a) is this brief, and its own sketch of
that option has a hole this brief closes); `PlanarCalibration.cs` in full, especially `BuildSet`,
`SuggestLengths`, `SuggestDeltas`, `LongestStandardLengthM`, `SelectSeparation`, `Gamma`,
`GammaBest` and `PlanarCalibrationSettings`' two `BandRatio` constants; `PlanarDeembed.cs`'s
`SolveErrorBox`, `CapacitancePerMetre` and `CharacteristicImpedance`; `RlgcExtractor.cs` (kernel A's
per-unit-length solve — the oracle AND, probably, the supplier); `src/Engine/Mom/RESOLVED.md` §LF1,
§LF2 and §LF3, so this is not confused with them.

---

## The problem

**A 3.8 mm microstrip cannot be simulated from 100 MHz because de-embedding it needs a 161.5 mm
calibration standard.** Not because the field solve struggles down there — LF1-LF3 fixed that, and
the same trace de-embedded from 100 MHz gives S21 = −0.11 dB, ε_eff = 2.45, Z_c = 99 Ω, with Z_c and
ε_eff matching the closed-form microstrip values to 0.1%. The DUT is fine. The CALIBRATION ARTEFACT
is 42× the size of the DUT, at 79,055 unknowns against a 12,000 ceiling, and the run refuses.

The owner's framing is the right one and should be taken at face value: low-frequency planar
structures are not normally hard. **This wall is the price of ONE design decision** — that γ is
MEASURED from two full-wave line standards (D5/D6) rather than supplied analytically. The measured
route earns its cost at the top of the band, where it captures dispersion and higher-order effects
with no quasi-TEM assumption. At the bottom it buys nothing and costs `1/f_lo`, because the two
lines' length difference must sit inside the usable interval βΔℓ ∈ [20°, 160°].

### The hole in RESOLVED §6(a), closed

§6(a) said "supply γ quasi-statically and the long standards go away." **That does not follow, and
anyone implementing it from that sentence will find out the hard way.** γ is ALREADY an input to
`PlanarDeembed.SolveErrorBox` — look at its signature. Supplying it from elsewhere changes nothing
about the error box, which still needs TWO lines: one line gives two complex equations
(`m11`, `m21`) for three complex unknowns (`a11`, `a21²`, `a22`), and no reciprocity or symmetry
argument closes that gap.

**What a known γ actually buys is that Δℓ no longer has to be ELECTRICALLY long.** The 20°-160°
interval exists to protect the γ extraction, where `Acosh`'s branch structure makes βΔℓ = nπ a
genuine singularity and where (per `Gamma`'s own header) α is two orders below β and its extracted
sign is noise. The error box's own conditioning is a different and far gentler thing. Measured on a
synthetic box with γ supplied exactly, relative error in `a21`, median of 40 noise draws:

| βΔℓ | Δℓ @ 200 MHz | noise 1e-6 | noise 1e-4 |
|---|---|---|---|
| 160° | 413 mm | 1.1e-6 | 9.8e-5 |
| **60°** (today's target) | 155 mm | 4.0e-7 | 6.4e-5 |
| 20° | 51.7 mm | 1.0e-6 | 1.2e-4 |
| 10° | 25.8 mm | 2.0e-6 | 2.6e-4 |
| **3°** | **7.75 mm** | 5.5e-6 | 7.2e-4 |
| 1° | 2.58 mm | 2.3e-5 | 2.0e-3 |

**A 20× shorter standard costs ~14× in error, not a cliff.** The degradation is ≈ 1/Δℓ — ordinary
conditioning, not the denominator zero the usable interval was drawn around. And the noise floor to
judge it against is small: the shipped run's own `DeembedResidual` on a working 100 MHz-6 GHz sweep
reads **1.3e-10 to 5e-13**.

So the design is: **supply γ and Z_c quasi-statically below a crossover, and then size Δℓ from the
SUBSTRATE and the mesh rather than from the wavelength.** Both standards become small and
frequency-independent, and the sub-band ladder collapses to one separation, because there is no
βΔℓ window left to cover.

Expected on the reported file: port 2's standards go from `N = 1,526 / 79,055 / 21,349 / 6,600` to
roughly two standards near the short one's own size, comfortably inside the ceiling.

### The GMRES failure this also explains

At a 1 GHz lower edge the reported file clears the standard-size ceiling and then fails with *"the
accelerated solve did not converge: 400 iteration(s) reached a relative residual of 1.36E-007 against
a tolerance of 1.00E-008"*. That looked like a separate defect. It is not.

The `.cem` never asked for the accelerator — `AcceleratedSolve` is absent, so false. `PlanarKernel`
turns it on BY ITSELF, in the `catch (PlanarAcceleratorWouldFitException)` around
`PlanarSolve.Run`: a CALIBRATION STANDARD past the 5,000 dense ceiling but inside the accelerated
12,000 re-runs the whole sweep with `Aim = PlanarAimSettings.Default`. The DUT is N = 1,144 and has
always been comfortably dense. **The accelerator — and therefore GMRES, and therefore that
non-convergence — exists in that run solely because of the calibration standard's size.** At a 2 GHz
lower edge the standards stay under the dense ceiling, AIM never engages, and the run completes.

So this brief should remove that failure as a side effect, and **M5 must check that it did** rather
than assume it. It will not remove it for a geometry whose DUT is genuinely over 5,000 unknowns,
where AIM engages on its own merits.

## What supplies γ and Z_c

Everything needed is already in the repository, which is the main reason to prefer this to §6(b):

- `RlgcExtractor.Extract` — kernel A's per-unit-length cross-section solve. This is both the
  candidate supplier and, independently, the oracle.
- `PlanarDeembed.CapacitancePerMetre` — already runs the electrostatic solve on a standard, and
  already supplies `C_pul` for `Z_c = γ/(jωC_pul)`. **De-embedding is therefore ALREADY part
  quasi-static today**; this brief extends that to γ rather than introducing a new idea.
- The quasi-TEM route itself is the ordinary one: `C` with the dielectrics, `C₀` with them replaced
  by air, `L = μ₀ε₀/C₀`, `γ = jω√(LC)` plus the loss terms, `Z_c = √(L/C)`. Kernel A's notes already
  report `[C]` and `[C₀]` being filled, so both halves exist.

**Decide, and record the decision, whether γ comes from `RlgcExtractor` or from the standard's own
electrostatics.** They are not the same: the first is the DUT's drawn cross-section, the second is
the standard's. R-qsc-2 is where that is settled by measurement, not by preference.

## Milestones

**M1 — the oracle and the crossover. DONE (2026-09-14). The answer is GO.**

Measured: quasi-static γ and Z_c from `RlgcExtractor.Extract` on the equivalent cross-section,
against the MEASURED two-line values from `PlanarPortCalibrator.At`, per frequency, on three stacks,
at two mesh densities. The calibrator was built at `(f, f)` so each point's own separation sits near
60° and conditioning is never the variable. **Kernel A must be given a THIN conductor** (`t = 1e-6`):
kernel B models an infinitely thin sheet, and 35 µm of copper is a ~5% Z_c offset that reads exactly
like a systematic disagreement. That confound cost one full run; do not re-introduce it.

RO4350 0.6 mm, w = 254 µm — the reported board — **refined mesh** (quasi-static minus measured):

| f | Δβ | ΔZ_c | ε_eff measured (quasi-static 2.786) |
|---|---|---|---|
| 100 MHz | +6.97% | +7.15% | 2.452 |
| 300 MHz | +0.98% | +0.73% | 2.734 |
| **1 GHz** | **+0.20%** | **−0.10%** | 2.775 |
| 3 GHz | −0.14% | −0.43% | 2.794 |
| 6 GHz | −0.35% | −0.63% | 2.806 |
| 10 GHz | −0.77% | −1.05% | 2.830 |
| 20 GHz | −2.12% | −2.35% | 2.909 |

**Two DIFFERENT error sources, and they must not be conflated — the disagreement is smallest in the
MIDDLE of the band and grows in both directions.**

- **Above ~5-10 GHz it is real dispersion**, ε_eff rising, quasi-static under-predicting β. That is
  what the measured calibration is for, and it is why this brief is a second path and not a
  replacement.
- **Below ~300 MHz it is the MEASURED value that is wrong, not the quasi-static one.** Proven by
  refinement rather than asserted: on the default mesh the 100 MHz disagreement is **+17.8%** and on
  the refined mesh **+7.0%** — the measured value moves toward the quasi-static one as the mesh
  tightens, and ε_eff_meas goes 2.02 → 2.45 against a static 2.786 it must approach as f → 0.
  **So the two-line calibration at the bottom of the band is not merely expensive, it is inaccurate**,
  and that is a stronger argument for this brief than the one it was written on.

Same shape on FR-4 1.6 mm (+4.78% at 100 MHz, +0.09% at 1 GHz, −2.61% at 6 GHz, −5.83% at 20 GHz) —
it disperses earlier, as `h√ε_r` predicts, so its crossover is lower.

**Crossover, per stack, from the measured tables:** ≈ **3 GHz** for FR-4 1.6 mm and ≈ **10 GHz** for
RO4350 0.6 mm, taking ~1% as the budget. Both are far above the reported file's whole band, so the
quasi-static path covers the entire problem this came from.

**GaAs 0.1 mm is the outlier and M2 must not paper over it.** It agrees to 0.5-1.3% at 6-20 GHz but
its MEASURED values at 100-300 MHz are nonsense — ε_eff_meas ≈ 12.3-12.5 against a substrate ε_r of
12.9 and a quasi-static ε_eff of 8.21 — and 1 GHz reads +8.2%. That is not dispersion and not a
conditioning slope; it looks like a branch or degeneracy failure on a stack whose standards are tiny
in absolute terms. **Diagnose it before building on top of it**: if the measured low-frequency value
can be that wrong on a shipped starter stack, the same mechanism may be reachable elsewhere.

**M1 also settled a question that was not asked of it — see "The GMRES failure" below.**

**M2 — γ and Z_c supplied analytically, standards still long.**
Change only the SOURCE of γ, not the standard geometry, so the two paths are comparable on identical
meshes. Gate on the de-embedded s-parameters agreeing with today's to the tolerance M1 measured.
`R-qsc-2` settles `RlgcExtractor` vs the standard's own electrostatics here, on the numbers.

**M3 — short standards.**
With γ supplied, re-target Δℓ. `SuggestDeltas` currently returns a λ-scaled ladder; below the
crossover it should return ONE separation, sized from the substrate height and the port's own bulk
cell. Sweep Δℓ down and find where the error box stops being recoverable **on real meshes**, which is
what the synthetic table above only predicts. Two failure modes to watch, and the second is not in
that table:
- conditioning of `a21`/`a11`, which degrades smoothly;
- **the `a22` SIGN SELECTION**, which does not. `SolveErrorBox` picks the branch by the redundant
  `M11` equation, and `DeembedRejected` is the margin it wins by — already only **2.5e-6 at 100 MHz**
  against 3.5e-4 at 6 GHz on a working sweep. A shorter Δℓ shrinks that margin. If it approaches
  `DeembedResidual`, the branch is being chosen by noise, and a wrong `a22` is a smooth, plausible,
  WRONG answer of exactly the kind this kernel's headers keep warning about. **Gate on the margin,
  not only on the residual.**

**M4 — the sub-band ladder collapses, and the ceiling refusal learns about it.**
With one short separation below the crossover, `LongestStandardLengthM` and
`StartFrequencyThatFits` (added at RAW1) describe a regime that no longer exists down there.
`PlanarSolve.BandEdgeRemedy` must not keep telling users to raise their lower band edge when the
quasi-static path would have run. Re-point it; do not leave it printing advice for the old path.

**M5 — end to end on the reported file.**
`extract1` from the RAW1 report: 3.8 mm microstrip, 20 mil RO4350, 100 MHz-6 GHz, a 558.8 µm port
pad on P2. It must RUN, and its S21 must agree with the ideal-MLIN reference
(−0.006 dB at 100 MHz, −1.14 dB at 6 GHz) to a stated tolerance. Anonymise the fixture before
committing anything derived from it.

**Report explicitly whether the accelerator was auto-engaged** (see "The GMRES failure" above). The
expected answer is no — with short standards nothing crosses the 5,000 dense ceiling — and if it is
yes, say so rather than leaving the 1 GHz non-convergence quietly unresolved.

## Gates

- **The overlap region is the real gate.** Above the crossover, the two-line path must be
  BIT-IDENTICAL to today — this brief adds a path, it does not re-point the existing one. Below it,
  the quasi-static path must agree with the two-line path where both run, to the tolerance M1
  measured, at every frequency in the overlap and not just at its ends.
- Every stack in M1 and both directions of the crossover.
- `RawSolveAndCalibrationRemedyTests` still passes unchanged — in particular the band-edge
  arithmetic, which M4 must re-point rather than delete.
- The `a22` sign margin, per M3, as its own assertion with its own recorded number.
- No new `Category=Benchmark` test unless a measured one crosses ~5 s. The short standards should
  make this path FASTER than the one it replaces; if it is not, say so with the number.

## Must NOT

- **Do not re-point the high-frequency path onto quasi-static γ.** The measured calibration is right
  up there and the whole L8/L9 acceptance set rests on it. This is a second path below a crossover,
  and a run that crosses the crossover must be able to say which path each point took.
- **Do not make the crossover a user setting in this brief.** Measure it, fix it per stack, report
  it in the notes. A knob invites someone to put it in the wrong place, and there is no way for them
  to know.
- **Do not present the quasi-static answer as the measured one.** The run's notes must say which
  calibration produced each point, and the `.npy` diagnostics must carry it — RAW1 §5 is what
  happens when a diagnostic flag does not distinguish "measured" from "not measured".
- Do not touch `src/Core`, `src/Engine`'s non-Mom code, `src/Ui` or `RfCore`.
- Do not re-introduce any raw-solve escape hatch. See RAW1 §1-§3.

## Cost

M1 is measurement only and is most of the value — if it shows quasi-static γ disagreeing materially
at frequencies where the standard is already affordable, **stop and report**, because the crossover
would then be too low to help and §6(b) becomes the better option instead. M2 and M3 are contained
changes inside `PlanarCalibration` and `PlanarDeembed`. M4 touches one message. M5 is a run.

## On completion

- `src/Engine/Mom/RESOLVED.md` gains a `§QSC` section: M1's tables in full, the chosen crossover per
  stack and why, the Δℓ floor M3 measured, the `a22` margin, and every trap found.
- `src/Engine/Mom/RESOLVED.md` §RAW1 §6 is updated — its option (a) sketch has the hole this brief's
  own problem statement documents, and it must not be left standing as guidance.
- `docs/design/mom-engine.md`'s de-embedding section gains the two-path description. It is a design
  note, not a `CLAUDE.md`, and is edited normally.
- **Do not write to any `CLAUDE.md`.** Report stale lines to the owner by file and line instead.

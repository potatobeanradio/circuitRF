# Brief SAR-10 — B1 and SAR as goals: tuning, optimizer, yield and multi-channel drive

**Series:** `brief-sar-0-overview.md` · **Tag:** `R-sar10-<m>`
**Depends on:** SAR-9, the tuning and optimizer series (TO) and the yield series (YA)
**Area:** the goal grammar and measurement functions (`src/Core` expressions, `reference goals`), the evaluation
service (TO-2), the tuning window and optimizer panel (`src/Ui`), the yield runner, `docs/design/sar.md` §12

---

## 1. Goal

With SAR-9 a field set is a function of the schematic's values, so the existing tuning, optimizer and yield machinery
can work on it. A coil designer can then:
- turn a capacitor knob and watch B1+ and SAR move;
- ask the optimizer for the capacitor values that maximise SAR efficiency subject to a match;
- ask yield how capacitor tolerance spreads psSAR10g;
- for a multi-channel coil, choose channel amplitudes and phases (RF shimming).

## 2. Requirements

**R-sar10-1 — Field measurements in expressions.** Functions over a `fields` output (SAR-9), usable in `measure` and
`goal` lines:
- `B1pMean(F, region)`, `B1pMin(F, region)`, `B1pCoV(F, region)` (coefficient of variation);
- `psSAR(F, mass)` and `SARwb(F, region)`;
- `SarEff(F, region, mass)` = B1pMean/√psSAR, and `Pabs(F)`.

A region is a named phantom layer or tissue set, or a box. Each is normalised as SAR-6 R-sar6-3 says, and states it.
`reference goals` documents them.

**R-sar10-2 — Cheap where it can be.** Tuning and optimizer iterations use SAR-9's fast path: B1 statistics and local
SAR on a region. `psSAR` with averaging runs only on goals that need it, and the optimizer is told that it is the
expensive goal (TO-6's cost hint, if it exists; otherwise evaluated every k-th iteration and reported). The 10 g
average is **never** approximated silently: a goal on psSAR is always the full average at the points it reports.

**R-sar10-3 — Tuning window.** A field plot of the recombined set updates live as a tuning knob moves, on the clip
plane shown, using the fast path. The full-average readouts update when the knob is released.

**R-sar10-4 — Multi-channel drive (RF shimming).** A schematic with several sources driving several coil ports exposes
each source's amplitude and phase as tunables (`tune mag(V1) …`, `tune phase(V1) …`; D18's complex parts). Goals such
as "maximise B1pMin subject to psSAR ≤ x per W total" are then ordinary optimizer problems. A quadrature birdcage is
the worked case.

**R-sar10-5 — Yield.** Tolerances on the tuning capacitors (`dist=gauss sd=2%`) and a yield spec on psSAR10g, B1pMean or
S11 run as any yield run, each trial recombining fields with no EM run. The worst trial's field set can be opened in
the 3D view.

## 3. Not in this phase

Virtual observation points and Q-matrix compression; time-varying pulse design.

## 4. Gates (minimal)

- `FieldGoalTests`: each function on a synthetic field set against a hand value.
- `LoopCoilOptimizeTests`: from 4.5 pF, the optimizer finds a capacitor value giving S11 below −15 dB at 298 MHz and
  the B1pMean the SAR-9 fixed data says, within 2 %. Fixed per-port data, no openEMS.
- `CapacitorYieldTests`: 200 trials on fixed data finish with zero openEMS launches (counter) and report psSAR10g's
  spread.

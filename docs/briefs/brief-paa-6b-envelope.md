# Brief PAA-6b — brute-force envelope: the modulated signal each direction receives

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa6b-<n>` · **Depends on:** PAA-6 · **Design note:** §2.1, §2.3, §7
**Area:** `src/Design/Em3d/FieldCosimulation*.cs` (a new `FieldCosimulation.Envelope.cs` beside `.Hb.cs`), `src/Engine/Envelope/`
(recording a named block's terminal voltages and port currents from the solution, no change to the solve; the per-branch metric
functions of `EnvelopeDataSet`, reused), the `fields` analysis keys in `src/Core/Netlist/AnalysisDirectiveSchema.cs` and
`docs/design/analysis-authoring.md` §2.4, `testdata/phased-array/hb-1x4/`, `src/Design/RESOLVED.md`.

## The gap

PAA-6 forms an array's pattern from a harmonic-balance solution, so a compressed PA's effect on the beam is seen — for one CW tone.
A phased array carries a modulated signal, and what matters to a system designer is the signal each direction **receives**: its
spectrum, ACLR and EVM in the beam and off it. Distortion does not steer with the carrier. Each PA compresses under a different
active load, so the intermodulation products leave the elements with phases the steering did not set, and they add up differently
in space from the wanted signal. The envelope analysis already solves the PAs under a modulated drive and already computes ACLR,
EVM, PSD and CCDF at an IProbe. Nothing turns the envelope's waves at an EM block into the field in a direction, and nothing shows
each PA's active load moving with the signal.

A full pattern at every envelope step cannot be the answer: a 200 µs record at four steps per sample is tens of thousands of steps,
times about 1,400 directions on the record's 5° grid (and ~65,000 on a 1° grid), times two polarizations. So this brief publishes
the radiated envelope in a **few named directions** plus **one time-averaged pattern**, and decides how those directions are named
before anything is built (D-6b-1).

## Decisions

**D-6b-1 — Directions are named, never the whole sphere per step.** A new key `dirs=` takes `peak` (the default) or a
semicolon-separated list of `theta,phi` pairs in degrees (`dirs=peak;30,0;-20,0;0,90`). `peak` is the maximum of the
time-averaged pattern (R-paa6b-3), so it is the beam as the signal actually forms it, not the steering command. A run names at
most 16 directions; more is refused with the count, because each costs a full envelope of the far field and its spectra. A
direction outside the record's grid (θ beyond its last θ, or below the horizon of a hemisphere record) is refused naming the
range. A direction between grid points is read with `ElementPattern`'s interpolation (PAA-2) for a PhasedArray at a level other
than exact, and refused for an exact sum or an EM Field Data block, whose stored patterns are only safe on their own grid, naming
the nearest grid direction.

**D-6b-2 — Polarization.** Each direction's envelope is published as both components (E_θ, E_φ). The spectral and modulation
metrics are taken on the co-polar component in Ludwig's third definition, as `farfield.CoPolLudwig3Db` defines it, with the
co-polar reference the record's own (its polarization caption). A direction where the co-polar component is more than 20 dB below
the cross-polar at the time average says so in a note, since its metrics then describe the weaker component.

**D-6b-3 — The element is narrowband across the modulation.** Each element's stored pattern is taken at the record frequency
nearest the carrier (at `harmonic=k`, nearest k·Fc), and held flat across the envelope's bandwidth. On PAA-1's records (one
frequency) there is no other choice; on a multi-frequency record the pattern's own variation across the band is out of scope
("Not here"). The run's notes state the frequency used and the modulation bandwidth against it.

**D-6b-4 — The waves are the envelope's own, not I = Y·V.** Unlike PAA-6, the envelope solves the block's port currents as
unknowns (an SnP block is a rational fit with one branch per port, `EnvelopeSnpFit`; a PhasedArray its wave constraints), and the
current at an instant depends on the block's response across the band, not at the carrier alone. So the runner records the named
block's terminal voltages and port branch currents at every step, at harmonic k, the way it already records labeled nets and
IProbes — an internal probe list handed to the runner by the fields analysis, not a user setting (`Probes=` stays deprecated).

## Requirements

**R-paa6b-1 — `waves=` takes an envelope analysis.** `analysis F1 type=fields block=EM1 waves=ENV1 [harmonic=k] [dirs=…]` runs
ENV1 with the block recorded (D-6b-4) and forms, at every envelope step, the incident and reflected wave at each port from the
recorded voltage and current in the port's Z0 (peak envelopes, so |a|²/2 watts, CosimWaves' scale as in PAA-6). The incident waves
are the weights of equation (1) (an EM Field Data block, `PointOf`'s sum) or (2) (an Elements-mode PhasedArray), evaluated only in
the named directions. `harmonic=` defaults to 1. Refused: an envelope run on more than one fundamental (`Envelope=auto` that gave
widely spaced carriers or an LO their own), naming the carriers and `Envelope=single` as the remedy where it applies, since a pattern of a mixing lattice is a later brief; `SaveWaveforms=no`
is overridden for the block's own record and the note says so. Everything PAA-6 refuses (a Feed-mode array, a plain SnP, a
harmonic the record does not hold) is refused the same way, through the same functions.

**R-paa6b-2 — The radiated envelope in each direction.** Cubes in a `radiated` group over a `direction` axis (labels `peak`,
`30,0`, …, values the direction's index, with `theta` and `phi` cubes over it in degrees):
- `Etheta`, `Ephi` `[time, direction]`, complex, r-normalized (r·E with e^{−jk₀r} removed, as `farfield.Etheta`), at the PAs'
  actual drive.
- `EIRP_t_dBm` `[time, direction]`: the instantaneous EIRP from both components.
- `PSD` `[direction, freq]`, `ACLR`, `PAPR_dB`, `CCDF`, `Pavg_dBm`, `EVM_pct`/`EVM_dB` (and the other modulation metrics the source's
  signal family has) `[direction, …]`, on the co-polar component, computed by **the same functions `EnvelopeDataSet.Build` applies
  to an IProbe's branch**, handed the co-polar envelope as a branch. No second ACLR or EVM implementation. The reference signal for
  EVM is the source's own, as for an IProbe.

**R-paa6b-3 — One time-averaged pattern.** `farfield.U` (and the metrics `Em3dRadiation.Publish` derives from U alone: directivity,
average EIRP, TRP, its peak's direction) from the power-averaged field, Σₜ(|E_θ|² + |E_φ|²)/N over the measured window
(the envelope's own settle skip applied) on the record's grid. `Etheta`/`Ephi` are **not** published for it, since a phase has no
time average. This is the pattern `dirs=peak` reads. It costs one pass over the steps per grid direction; the run says its size
before it starts, as R-cp5-4's sweep size does, and warns above the same threshold.

**R-paa6b-4 — Each PA's load moving with the signal.** In the `array` group: `GammaActive`, `a`, `b` and `Pdel` over
`[time, element]` (each element's active reflection at every step, b/a with a the wave from the circuit into the block; NaN where
|a| is below 1e-6 of its RMS, where the ratio means nothing), and their time averages `GammaActive_avg` (power-weighted:
Σ b·a*/Σ |a|²) and `Pdel_avg` `[element]`. A Smith chart trace of `F1.array.GammaActive` with `time` as X and `element` the family
draws each element's cloud, labelled by element (PAA-6's labelled family), and overlays the PA's loadpull contours unchanged.
`ActiveGamma(F1, n)` returns the power-weighted average for an envelope source and says so in its note; `ActiveVswrMax(F1)` the
worst over elements and over steps.

**R-paa6b-5 — Inside a sweep.** A `parametric_sweep` around ENV1 (drive level, theta0) gives every cube above per point, with
ENV1's sweep axes, exactly as PAA-6 does for HB (the same stand-in mechanism); a sweep around F1 runs ENV1 at each of its points.
The time axis stays the innermost: `radiated.ACLR[theta0, Pin, direction, …]`. Memory is said before the run, from the steps,
directions and points.

**R-paa6b-6 — The bench.** In `testdata/phased-array/hb-1x4/`: `Array Envelope.cnl`, the PAA-6 bench driven by **one** `MOD_Source`
(the *Envelope* example's NR 20 MHz signal at 5.9 GHz) through an ideal 4-way `Combiner` into the four PhaseShifters, so every
element carries one signal as a beamformer would (four sources with one seed would also be one signal, but not one architecture);
ENV1 over a record long enough for ACLR (the shortest the *Envelope* example's ACLR is stable at, measured, not guessed) swept over
`Pin` at two levels (backed off and compressed) and theta0 ∈ {0°, 30°}; `F1 waves=ENV1 dirs=peak;0,0;-30,0;60,0`; and
`Array Envelope.cdd`: each element's Γ_act cloud over the loadpull contours, the PSD in the beam against off it, ACLR and EVM
against direction, and the time-averaged pattern's cut. **No EM run.**

## Gates

1. **A CW envelope is HB.** An envelope driven by an unmodulated carrier (a `MOD_Source` with a constant envelope, or the
   envelope's own CW drive) on the PAA-6 bench, after its settle, gives at every step the field PAA-6's `waves=HB1` gives in the
   same directions, and the same `GammaActive`, to the envelope's Newton tolerance (relative, in the field).
2. **One element alone is that PA's own signal.** With three of the four elements' channels switched off (the Combiner's `IL[k]`
   at 200 dB on those paths), the radiated co-polar envelope in every named direction is the driven PA's output envelope times one fixed
   complex factor per direction: its ACLR and EVM equal those `EnvelopeDataSet` reports at that PA's output IProbe to 0.01 dB and
   0.01 pp. This holds the reuse of the metric functions and the direction sum independently of the array.
3. **Backed off, the beam carries the source's signal.** At a drive 30 dB below compression, the in-beam EVM is the source signal's
   own (the *Envelope* example's ideal figure) to 0.1 pp, and the in-beam ACLR the source's ideal ACLR to 0.5 dB.
4. **The averaged pattern is the average.** `farfield.U` of R-paa6b-3 equals the mean over steps of |E|² formed from each step's
   waves, at three grid directions, to 1e-12 relative; and its total radiated power equals the mean of the per-step radiated
   powers to the same.
5. **Directions.** `dirs=` with 17 directions is refused with the count; a direction off the grid on an EM Field Data block is
   refused naming the nearest grid direction; `dirs=peak` on the theta0 = 30° point finds the beam within one grid step of the
   PAA-6 pattern's peak at the same drive.
6. **Recorded, not reported:** what the compressed, steered point shows: in-beam ACLR against ACLR at the other named directions,
   and each element's Γ_act spread over the record. Stated in the completion note and `RESOLVED.md` as a measurement; no gate is
   written on which way it goes, because that is the physics this brief exists to show, not a property of the code.
7. UiSnapshot PNGs, looked at, of the Γ_act clouds over the contours and of the PSD in the beam against off it.

## Simulation budget

- No EM run. Tests run envelope records of the shortest length each claim needs, and every routine test stays under ~5 s; gate 3's
  ACLR needs the longest record and is measured first, and if it cannot be met in 5 s it is checked against a shorter record's
  wider tolerance and the full figure is recorded in `RESOLVED.md`, not a `Category=Benchmark` test.
- The committed fixture results are sized before they are written (R-paa6b-3's sentence); keep them under ~3 MB together.
- One test per claim; run the named test classes only.

## Not here

The element pattern's variation across the modulation bandwidth (D-6b-3: a multi-frequency record's patterns interpolated per
envelope frequency bin); a pattern per mixing product of a multi-fundamental envelope; receive (an incident modulated wave through
the array, PAA-7's reciprocity applied to an envelope); digital predistortion per element; thermal coupling between PAs; Feed-mode
envelopes at large N (they run, as any circuit; their cost is PAA-8's).

## Completion note (2026-11-14)

**Built.** `waves=` takes an envelope analysis, with a new `dirs=` key (`FieldsAnalysis.DirsExpr`, in the `.cnl`, the `.csch` and
the directive schema). `FieldCosimulation.Envelope.cs` runs the envelope with the block recorded — its nodes join the recorded
voltages, and its port branch currents are recorded beside the IProbes (`EnvelopeParams.Blocks`, `EnvelopeEngine.IProbesOf`; the
envelope's `RationalSnpModel` now exposes `PortBranchIndices`) — through `EnvelopeRunner.Record`, which returns the stitched record
and its settle skip. Each step's waves come from what the envelope solved (D-6b-4). The field is linear in them, so each port's
(or element's) pattern is read once, on the grid and in the named directions, and a step costs a weight vector and a dot product.
Published: `radiated.Etheta/Ephi/EIRP_t_dBm [time, direction]`, `radiated.theta/phi`, `radiated.MeasureFrom_s`, and every
per-branch metric `EnvelopeDataSet.Build` forms (PSD, ACLR, PAPR, CCDF, EVM, MER, NMSE, AM-AM/AM-PM, constellation) on each
direction's Ludwig-3 co-polar envelope handed over as a branch (`EnvelopeRunResult.WithBranches`), minus the gain/efficiency cubes
that mean nothing of a direction; `farfield.U` as the power-averaged pattern with TRP, directivity, peak EIRP and its direction;
`array.a/b/GammaActive/Pdel [time, element]` with `GammaActive_avg` (power-weighted) and `Pdel_avg`. `ActiveGamma(F1, n)` reads the
average and `ActiveVswrMax(F1)` the worst over elements and steps. A sweep around the envelope gives everything per point (PAA-6's
stand-in mechanism, generalised); a sweep around F1 runs the envelope at each point. φ₀ is derived from the strongest step's pattern
through `PlanarPolarization.ReferenceAngle`, the same derivation a published pattern makes. A family over `direction` is labelled
per curve, as one over `element` is. Refused: more than 16 directions, a direction past the pattern's θ, a direction off the grid
of an EM Field Data block or an exact array (naming the nearest), a multi-fundamental envelope, a carrier harmonic with no record
frequency within 2 % — all but the last two before the envelope runs.

**The bench** (`testdata/phased-array/hb-1x4/Array Envelope.cnl`, `.cdd`): one NR 20 MHz 64QAM source through an ideal 4-way
Combiner into the PAA-6 phase shifters and amplifiers; 40 µs, swept over Pavg ∈ {−8, 22} dBm and theta0 ∈ {0, 30}°; F1
`dirs="peak;0,0;-30,0;60,0"`. Its result is **not committed** (34 MB of time cubes; README and `.gitignore`).

**Gates** (`tests/Ui.Tests/PhasedArray/PhasedArrayEnvelopeTests.cs`, 5 tests, NR 10 MHz 60 kHz at 40 µs):
1. `ACwEnvelope_IsHarmonicBalance` — a CW envelope at 22 dBm against PAA-6's waves=HB1 on the same bench (a P1Tone of the same
   power): the field's shape agrees to 9.9e-9 of its peak and Γ_act to 3.8e-9; the level is 4.4e-6 apart in every direction at once
   (the two sources); `dirs=peak` lands on HB's beam (25°).
2. `OneElementAlone_…` — three channels at 200 dB: every direction's ACLR equals PA1's output IProbe's to 0.007 dB, EVM to 0.005 pp.
3. `BackedOff_…` — at −38 dBm the beam's EVM is the source's to 0.002 pp and its ACLR the amplifiers' to 0.01 dB.
4. `TheAveragedPattern_…` — `farfield.U` equals the mean of each step's |E|²/2η₀ at three grid directions to 4e-13.
5. `Directions_…` — 17 directions and an off-grid one are refused before the run, naming the count and the nearest grid direction.
6. Recorded (`src/Design/RESOLVED.md`): at 22 dBm steered to 30°, in-beam ACPR1 −26.3 dBc and EVM 7.6 %; at 0° and at −30°
   ACPR1 −20 dBc and EVM 19 %; at 60° −26.1 dBc and 7.8 %.
7. UiSnapshot of `Array Envelope.cdd`, looked at (session scratchpad `paa6/ecdd/snap.png`, not committed).

**What changed from the brief, and why.**
- **Gate 3 compares the beam's ACLR with the amplifiers', not with the source's ideal.** 30 dB below compression this amplifier
  still adds distortion (beam −67.5 dBc against the source's −90.8 at −8 dBm), so the gate runs 60 dB below; and even there the
  chain's own frequency response across the band moves ACPR1 0.6 dB from the source's ideal, linearly, in the amplifiers' outputs and
  the beam alike. EVM is still compared with the source's.
- **Gate 1's level** is checked to 1e-5, not the Newton tolerance: a uniform 4.4e-6 difference between a MOD_Source's CW and a P1Tone
  of the same power, with the shape at 1e-8.
- **Gate 4's second half** (total radiated power equals the mean per-step radiated power) is not a separate test: the radiated power is
  a linear quadrature of U, so it follows from the first half; the per-step patterns it would need are what the design avoids forming.
- **The record length is 40 µs** (ACLR stable to 0.2 dB against 200 µs; EVM 0.4 pp low), and the routine tests take 1–10 s rather
  than under 5: EVM needs a whole symbol after the settle, and a bench with ENV1 and F1 runs the envelope twice.
- **A run with ENV1 and F1 runs the envelope twice** (F1 runs it with the block recorded), as PAA-6's HB benches do.

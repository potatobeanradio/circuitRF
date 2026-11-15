# Brief PAA-6 — brute-force harmonic balance: a PA on every element

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa6-<n>` · **Depends on:** PAA-3 (PAA-1's E3 fixture) · **Design note:** §2.3, §7
**Area:** `src/Design/Em3d/FieldCosimulation*.cs`, `src/Engine/HarmonicBalance/` (reading a block's terminal values
from a solution, no change to the solve), the `fields` analysis keys in `docs/design/analysis-authoring.md` and the
MCP `reference analyses` topic, the Smith chart and loadpull contour plots in `src/Render/DataDisplay`,
`src/Design/RESOLVED.md`.

## The gap

For a small array the most faithful simulation is the most direct: one EM result with a port per element, one power
amplifier instance on every port, the whole thing in harmonic balance, so each PA is loaded by the waves its neighbours
couple back into its element and compresses accordingly. The circuit already solves — an EM block is an N-port in HB's
linear partition — but the `fields` analysis forms its pattern from a linear solve of its own
(`SParameterEngine.BlockWaves`), so a compressed PA's effect on the beam is invisible, and nothing reports what each PA
was loaded with or where that load sits on the PA's loadpull contours.

## Requirements

**R-paa6-1 — The pattern from HB's waves.** A `fields` analysis takes `waves=<HB analysis>` and, optionally,
`harmonic=<k>` (default 1). It reads, from the named HB analysis of the same bench, the block's terminal voltages at
harmonic k, and forms the terminal currents from the block's own linear stamp at that frequency (I = Y·V; no new unknown,
no change to HB). The waves at the block's Z0 are then the weights of equation (1), or of (2) for an L0/L1 array, and
everything after is the existing `PublishFarField` path. The frequency `k·f₀` must be one the record holds, else refused
naming the nearest held frequency. `waves=` names an HB analysis or nothing; any other kind is refused. Works on an
EmFieldData block and on an Elements-mode PhasedArray alike.

**R-paa6-2 — Inside a sweep.** When a `parametric_sweep` wraps the HB analysis (drive power, steer angle), the `fields`
analysis forms a pattern at every point, aligned with the sweep's axes, exactly as CP-5 did for linear sweeps.

**R-paa6-3 — Each PA's loading.** Cubes in the `array` group over the element axis, at each sweep point: `GammaActive`
(the reflection the block presents at each port, `b/a` with **a** the wave from the circuit into the block), delivered
power, and accepted power. Measurement functions `ActiveGamma(F1, n)` and `ActiveVswrMax(F1)` read them. The PA's own
output power and PAE stay the bench's ordinary measurements on each PA instance; nothing here re-derives them.

**R-paa6-4 — The loci on the PA's contours.** A Data Display Smith chart trace of `F1.array.GammaActive` against a swept
variable draws one locus per element, labelled by element number; a loadpull contour plot (`.spl`) accepts the same trace
as an overlay, so every element's active load is seen over the PA's Pout and PAE contours. No new plot type: a trace on
two existing plots.

**R-paa6-5 — The bench, as a shipped fixture.** Under `testdata/phased-array/hb-1x4/`: PAA-1's E3 record (1×4 along the
strongly coupled axis), four instances of the *Harmonic Balance* example's FET amplifier (designed at 2 GHz) with its
matching retuned for 5.8 GHz, the contours from the *Loadpull* example's bench retargeted to the same PA at 5.8 GHz
(a circuit loadpull, not an EM run), each PA driven from its own P1Tone
through a `PhaseShifter` (PAA-4) whose `Phase` is `steer(theta0, 0, f0, x_n, 0)` written per element, the HB analysis
swept over `theta0` and drive power, a `fields` analysis with `waves=HB1`, and a `.cdd` with the realized gain cut, the
Γ_act loci, and each PA's Pout and PAE against `theta0`. **No EM run in this brief**: the record is PAA-1's.

## Gates

1. **Small signal equals linear.** At a drive 40 dB below compression, the HB-waves pattern equals the linear `fields`
   analysis on the same bench to 1e-9 (relative, in the pattern), and `GammaActive` equals equation (3) from the record's S
   and the linear waves to 1e-9.
2. **Each PA sees its active load.** At a compressed point, replace the block with four ideal one-port loads at the HB
   run's `GammaActive` values: each PA's output power and PAE match the array run to HB's own convergence tolerance.
3. **Harmonics.** `harmonic=2` on a record that does not hold 2·f₀ is refused naming the nearest held frequency.
4. **Sweep.** Over `theta0` ∈ {0°, 15°, 30°, 45°}, the bench returns four patterns and four Γ_act sets; the loci trace
   draws four curves of four points.
5. UiSnapshot PNGs, looked at, of the Γ_act loci on the Smith chart and on the PA's loadpull contours.

## Not here

Feed-mode HB at large N (it runs, as any circuit; its cost is PAA-8's to report); a pattern per mixing product of a
multi-tone run; thermal coupling between PAs.

## Completion note (2026-11-14)

**Built.** `analysis F1 type=fields block=EM1 waves=HB1 [harmonic=k]` (`FieldsAnalysis.Waves`/`HarmonicExpr`, in the `.cnl`, the
`.csch` and the directive schema, so the MCP `reference analyses` topic lists both; `freq=` is no longer required with `waves=`).
`FieldCosimulation.Hb.cs` runs the named HB analysis, reads the block's terminal voltages at harmonic k from its back-solver, and
forms the currents from the block's own stamp at k·f0, the block alone in a small MNA with its nodes held at HB's voltages (I = Y·V;
nothing added to HB). The waves go through the existing far-field path: `PointOf` for an EM Field Data block, PAA-3's `RunArray`
(equation (1) at exact, (2) otherwise) for an Elements-mode PhasedArray. A `parametric_sweep` around HB1 gives a pattern at every
point: F1 runs that sweep itself with HB1's entry stood in for, so the result has HB1's sweep axes; a sweep around F1 itself runs HB
at each of its points. Each run adds the array group over the element axis — `a`, `b`, `GammaActive` (b/a, a from the circuit into
the block), `Pacc` (per watt, as before) and `Pdel` (the watts HB delivers into each element) — for an EM Field Data block as for an
array. `ActiveGamma(F1, n)` is a new measurement (complex); `ActiveVswrMax(F1)` now answers for a block as for an array; both refuse
under a sweep naming `F1.array.GammaActive`. A Smith chart trace of `F1.array.GammaActive` with `element` as the family draws one
coloured locus per element, labelled `element = n`, and the same trace overlays a loadpull contour plot (no new plot type). Refusals:
waves= naming nothing or another kind of analysis, a multi-tone analysis, a harmonic above MaxHarm or below 1, a harmonic the
record does not hold (naming the nearest it does), a Feed-mode array.

**The bench** (`testdata/phased-array/hb-1x4/`, its README): four copies of the Harmonic Balance example's FET amplifier, matching
retuned (shunt C + series L in, a 2.43 nH drain feed resonating the drain capacitance out), each from its own P1Tone through a
PhaseShifter written `Phase=-steer(theta0,0,f0,x_n,0)`, on PAA-1's E3 record (`../results/e3x.cemr`); HB1 swept over theta0 ∈ {0, 15,
30, 45}° and Pin ∈ {−20, 16, 22} dBm; F1 waves=HB1; Pout and PAE per PA as measures; `PA Loadpull.cnl` the Loadpull example's bench
retargeted to the PA, its `.spl`; `Array Bench.cdd` with the loci, the loci over the Pout/PAE contours, the cut per theta0, the peak
realized gain and each PA's Pout and PAE against theta0. The whole 12-point run takes about half a second. At 22 dBm and 45° the inner
elements' PAE falls from 68 % to 37 % while the outer ones' falls to 44 % and 51 %.

**Gates** (`tests/Ui.Tests/PhasedArray/PhasedArrayHbTests.cs`, 5 tests, ~3 s):
1. `SmallSignal_…` — at −60 dBm, steered to 30°, the HB-waves pattern equals the linear fields analysis's to 7.4e-10 of its peak, and
   GammaActive equals (3) from the record's S and the linear waves to 2.4e-10.
2. `Compressed_…` — at 22 dBm and 30°, with the block replaced by four `Z_Port` loads presenting the GammaActive impedance at f0 (and
   HB's own V/I at each harmonic), each PA's Pout and PAE match the array run to 4.4e-12 relative and 1.6e-10 pp (Tol = 1e-9 A).
3. `ASecondHarmonic…` — harmonic=2 is refused: "harmonic 2 of 'HB1' is 11.8 GHz … the nearest frequency it holds is 5.9 GHz".
4. `ASweepAroundTheHbAnalysis…` — four patterns, GammaActive [theta0 4, f 1, element 4], and the Smith trace four loci of four points
   labelled element = 1…4. Plus `WavesAndHarmonic_Survive…` (the .cnl and the schematic record round trip).
5. UiSnapshot of `Array Bench.cdd`, looked at (session scratchpad `paa6/cdd/snap.png`, not committed): the loci on the Smith chart,
   the loci over the Pout and PAE contours with MXP/MXE, the cuts per theta0 labelled in degrees, and the three trends.

**What changed from the brief, and why.**
- **5.9 GHz, not 5.8.** PAA-1 reduced the fixtures to 5.9 GHz, and R-paa6-1's own rule refuses a harmonic the record does not hold.
- **Gate 1 at −60 dBm, not 40 dB below compression.** The difference there is the amplifier's own nonlinearity (it is biased near
  pinch-off and its gain moves with drive): 1.1e-5 at −18 dBm, falling as the drive squared to 7.4e-10 at −60. The method itself adds
  nothing measurable.
- **`Phase=-steer(...)`, not `steer(...)`.** PAA-4's PhaseShifter delays by Phase; without the minus the beam goes to −theta0.
- **Gate 2's loads present HB's own impedance at the harmonics.** An ideal one-port at Γ_act alone would load the harmonics with the
  fundamental's impedance while the array loads them with the record's (held) S, and the two would differ by more than HB's tolerance.
- **The loadpull grid is offset 15° from the real axis and reaches |Γ| = 0.55.** On the example's grid this PA's loadpull fails at
  Γ = 0 exactly and at five other loads, and one load ran to a 201 dBm root (`src/Design/RESOLVED.md`); the offset grid converges at
  45 of 48 and covers every active reflection. Its drain supply is the load tuner's, since a loadpull counts DC power only there.
- **The pattern cut is the radiated intensity, not a realized-gain cut**: a pattern cube in realized gain does not exist; the peak
  realized gain is plotted against theta0 beside it.
- **Fixed on the way:** the headless Data Display (`render`, UiSnapshot) could not read a `.spl`/`.lpcwave` at all; a family over a
  swept angle (theta0) drew in one colour with no legend; its labels read in radians.
- **An envelope source** (`waves=ENV1`) is `brief-paa-6b-envelope.md`: the waves are within reach, what to publish per direction is the work.
- **Not done:** the schematic's Fields editor has no control for `waves=`/`harmonic=` (they survive an edit in it, and the `.cnl`
  carries them); the retuned amplifier is potentially unstable (μ just below 1, as the original is).

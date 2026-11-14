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

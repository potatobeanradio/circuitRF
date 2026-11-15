# Brief PAA-4 — Feed mode: element scope, weights, quantisation and the channel cell

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa4-<n>` · **Depends on:** PAA-3 · **Design note:** §4.1–§4.5
**Area:** `src/Core/Expressions/` (element scope, the weight functions), `src/Core/Elaboration/` (placing the channel
cell), `PhasedArrayModel`, `src/Core/Devices/System/` (a `PhaseShifterModel`), `ComponentTypeRegistry`, `src/Core/RESOLVED.md`.

## The gap

A large array cannot have a pin per element, and a system designer wants the per-element chain — LNA or PA, attenuator,
phase shifter — drawn once, with the mixer, filter and common amplifiers outside the array in an ordinary System
schematic. Steering and tapering need a per-element expression that knows the element's position, and those expressions
must stay sweepable: a sweep of the steer angle, the taper level or the phase-shifter bit count is an ordinary
`parametric_sweep`. None of this exists: there is no per-element scope, no steering function, no quantisation, and no
way to place a cell once per element.

## Requirements

**R-paa4-1 — Feed mode.** `Mode=feed` gives the component one pin, `F`. Inside, element n is driven through an ideal
matched weight `wₙ = Amp·exp(j·Phase)·exp(−j·2πf·Delay)`, quantised (R-paa4-4), then through the channel cell if one is
named (R-paa4-5), then into the array network of PAA-3. The feed's split is the ideal N-way of the `Combiner`
(`CombinerModel`'s S, matched and lossless at the common port), so the noise and power conventions of the Chain Budget's
arrays hold unchanged. N up to 4096; above that refused, naming the limit.

**R-paa4-2 — Element scope.** `Amp`, `Phase`, `Delay` and their `[n]` overrides are evaluated once per element in a
scope layered over the instance's, binding `n`, `row`, `col` (1-based) and `x`, `y` (metres from the array centre,
unit-bearing lengths) by D6's convention. The same scope binds those names in the parameters of the channel cell's
instance. A reference to any of the five outside those places is refused by name, with the sentence saying where they
are valid. Cycle detection walks the element scope as an ordinary scope; it adds no edges back. `Phase` takes degrees as
its site unit, as the Combiner's `Phase[k]` does.

**R-paa4-3 — The weight functions.** Standard functions, valid anywhere, whose position arguments default to `x`, `y`
in element scope and are required elsewhere:

- `steer(theta0, phi0, f0[, x, y])` — phase in degrees, `−360·f0/c·(x·sinθ₀cosφ₀ + y·sinθ₀sinφ₀)`;
- `steerDelay(theta0, phi0[, x, y])` — seconds, `−(x·sinθ₀cosφ₀ + y·sinθ₀sinφ₀)/c`;
- `taylor(sll, nbar)`, `chebyshev(sll)`, `cosq(q)` — separable over rows and columns (they read `row`, `col`, `Rows`,
  `Cols` from element scope), normalised to 1 at the peak; refused outside element scope.

Each in `docs/design/expressions.md`'s function table, with its unit behaviour stated (θ₀ unit-bearing or radians as
any trig argument). `steer()` and `steerDelay()` are also what PAA-5's symbol and PAA-9's tool recognise to draw and drag
the beam, so their VAR arguments are discoverable from the parsed expression, not from text.

**R-paa4-4 — Quantisation.** `PhaseBits` rounds each phase to `360/2^b` degrees (0 = continuous); `AttenStep` rounds each
amplitude's attenuation from the largest to the step in dB (0 = continuous); both after the overrides and before the
channel. The quantised weights are in the `array` group as `wQuantised` beside the requested `w`, so a plot shows both.

**R-paa4-5 — The channel cell.** `Channel=<cell>` names a two-port cell (its first two ports in, out); elaboration places
it N times as hierarchical instances `PA1.ch[n]`, with element scope bound in its parameters, and their measurements and
node paths are reachable as any sub-instance's (`V(PA1.ch[3].out)`). A channel with other than two ports is refused
naming its ports. A channel whose resolved parameters do not depend on `n`, `row`, `col`, `x`, `y` is marked
**identical** on the elaborated netlist — the fact PAA-8's fast path reads — and `explain` says which.

**R-paa4-6 — `Off`.** An element in `Off` gets weight zero and its array port terminated in Z0; its channel is still
placed (a channel's noise still reaches the feed through its own path) unless the channel is identical, in which case it
is omitted and `explain` says so.

**R-paa4-7 — A PhaseShifter System block.** An ideal matched two-port: `Phase` (degrees), `IL` (dB), `Bits`, `Delay`
(seconds), with the System palette's conventions and a symbol of its own. Lossless and reciprocal at `IL=0`; its noise is
its loss, as every passive System block's. For channel cells and Elements-mode benches.

**R-paa4-8 — It is a System block among System blocks.** Feed mode composes with the Amplifier, Mixer, Filter,
Attenuator, Combiner and the rest in every analysis they run in: S-parameters, S-parameter noise, HB, multi-tone HB and
envelope. A mixer before the array puts the array at the mixer's output frequency, and the pattern is formed at the
frequency the block sees.

## Gates

1. **Steering.** A 16×16 Feed-mode array on the E5 fixture with `Phase=steer(theta0,0,5.8 GHz)` points its beam at
   `theta0` ∈ {0°, 30°, 45°} within PAA-1's pointing tolerance, and a `parametric_sweep` of `theta0` returns one pattern
   per point.
2. **Element scope equals overrides.** `Phase=steer(…)` and the same values written as 256 `Phase[n]` overrides give
   the same pattern to 1e-12; a reference to `row` in a VAR is refused naming where it is valid.
3. **Taper.** `Amp=taylor(-30,4)` on a 32×32 isotropic-element array gives a first sidelobe within 0.5 dB of −30 dB in
   both principal planes.
4. **Quantisation.** `PhaseBits=3` on a 1×16 line puts quantisation lobes where the closed form for a 45° step puts
   them; `PhaseBits=0` is identical to no field.
5. **Channel cell.** A channel of an Amplifier (G 20 dB) and an Attenuator (3 dB) gives a realized gain 17 dB above the
   same array with no channel, to 1e-9, and is marked identical; a channel whose attenuator is `3+0.1*n dB` is not.
6. **System chain.** Port → Mixer → Filter → Feed-mode PhasedArray with an Amplifier channel runs S-parameters, HB and
   S-parameter noise; the noise figure at the Port matches the Chain Budget's closed form for the same chain with the
   array's gain, to 1e-6 dB, with `Coupling=off`.
7. **PhaseShifter.** `Phase=90` gives S21 = −j at `IL=0` and `Bits=2` rounds 100° to 90°.

## Not here

Receive waves and the array's own noise temperature (PAA-7); the fast path (PAA-8); the symbol (PAA-5).

## Completion note (2026-11-14)

**Built.** `Mode=feed` (`Elaborator.PhasedArray.cs`): one pin, `F`, and behind it three ordinary elaborated parts — `PA1.feed`, the
beamformer (`PhasedArrayFeedModel`: the Combiner's ideal N-way split, S[0,n] = w_n/√N, stamped sparse, passive while ‖w‖ ≤ √N), the
channel cell placed once per element as `PA1.ch[n]` when `Channel=` names one, and `PA1`, the array network on internal element
nodes, which a `type=fields block=PA1` analysis still names. Up to 4096 elements. `Amp`, `Phase` (degrees), `Delay` (seconds) and
their `[n]` overrides are evaluated once per element in **element scope** (`ElementScope`: `n`, `row`, `col`, `x`, `y`), then quantised
(`PhaseBits`, `AttenStep`); the fields analysis adds `array.w` and `array.wQuantised`. The weight functions `steer`, `steerDelay`,
`taylor`, `chebyshev`, `cosq` (`Evaluator.Array.cs`, line tapers in `ArrayWeights`), opaque to the site-unit rule so a unit-bearing θ0
does not decide the result's unit. A name of the five outside element scope is refused with where it is valid. An element switched off
has weight zero (its port a matched Z0 termination); an identical channel of an Off element is omitted, a differing one kept.
Identical is decided from the cell's expressions and written on the model, in the run's notes, `check` and `explain --analysis`.
`BeamNfDb(F)`: the noise figure from the driving port to the beam's peak (`SParameterEngine.BlockFunctionalNoise`). The
**PhaseShifter** System block (`PhaseShifterModel`, palette **Phase Shifter**, `PS`, its own symbol): `Phase`, `IL`, `Bits`, `Delay`,
`Z0`. The parameter editor lists the new rows; the components and expressions pages say both.

**Gates** (`tests/Ui.Tests/PhasedArray/PhasedArrayFeedTests.cs`, 8 tests):
1. 16×16 on the 5×5 record, `Phase=steer(theta0,0,f0)`: pointing error 0.056°, 0.110°, 0.350° at 0°, 30°, 45° (beamwidths 6.1°, 7.2°,
   8.7°; gated at a tenth of the beam — the embedded pattern pulls the peak towards boresight); a sweep of theta0 gives three patterns.
2. The same phases as 256 `Phase[n]` overrides: 1.1e-14 of the peak; `bad = 10*row` used in Phase is refused naming Amp, Phase, Delay
   and the channel cell.
3. `Amp=taylor(-30,4)`, 32×32 isotropic elements: −30.243 dB in both principal planes; the circuit's own `SllDb` on the patch −30.257 dB.
4. `PhaseBits=3`, 1×16 at 10°: the p = −1 lobe at u = 0.812 (closed form 0.749, inside its main lobe ±0.123), −16.96 dB (closed form
   −16.90); the continuous line −21.8 dB there; `PhaseBits=0` identical to none.
5. Amplifier 20 dB + Attenuator 3 dB channel: realized gain +17.000000000000 dB; identical; `3+0.1*n` is not, and says it reads n.
6. Port → Mixer → Filter → Feed array with an Amplifier channel runs HB (two tones, IF and LO) and S-parameters with noise, and the
   same array runs in an envelope run; `BeamNfDb` on Amp 15 dB/2 dB → 2 dB loss → array with a 12 dB/3 dB channel at T0,
   `Coupling=off`: 2.183439017 dB against the cascade's 2.183438944 dB (7e-8).
7. PhaseShifter `Phase=90`: S21 = −j to 1e-12, matched and reciprocal; `Bits=2` rounds 100° to 90°; `Phase=90 deg` the same.
Plus `explain`/`check` as a process: identical, the omitted channel of element 6, the quantisation.

**What changed from the brief, and why.**
- **Gate 1 steers at 5.9 GHz, not 5.8**: the fixtures hold 5.9 GHz only, and a beam steered at 5.8 and read at 5.9 squints by design
  (0.75° at 45°). PAA-1 set no pointing tolerance as a number; the gate holds a tenth of the beamwidth.
- **Gate 4's lobes are held to the closed form's main lobe, not a point**: on 16 elements the quantisation lobes' sidelobes overlap
  and move the peak 0.06 in u (no θ0, periodic-error ones included, does better than 0.04); the level agrees to 0.06 dB.
- **Gate 6's 1e-6 dB comparison is on the chain after the mixer.** The small-signal noise figure is S-parameter noise, and a mixer
  has no conversion path without an LO (`MixerModel`), so nothing crosses it there; the Chain Budget's own mixer benches use HB noise
  and agree to ~3e-4 dB, not 1e-6. The chain with the mixer runs all three analyses; the NF is gated on Amp → matched loss → array,
  with a matched attenuator for the filter so every stage is matched as Friis assumes, and a 4×1 column, because the patch's peak sits
  0.16° off boresight across its H-plane and a row there reads 6e-6 dB higher — the split's isolation noise radiated at a peak the
  weights are not phased to, which is the beam's, not an error.
- **The PhaseShifter's Phase is a delay** (gate 7: `Phase=90` is −j), opposite in sign to an array weight's (R-paa4-1, `e^{jφ}`). A
  channel steering through it writes `Phase=-steer(…)`; its parameter row says so.

**Found and fixed on the way.**
- **`ArrayBeam` cut a boresight beam along whatever azimuth its peak landed on**, so `SllDb` of a 32×32 Taylor array read −45 dB (a
  diagonal) or −32.4 dB depending on where the overview's grid started the search. A peak within a cut step of boresight is now cut
  in the requested scan's plane, or φ = 0. PAA-2's 64×64 reference cuts itself the same way (it agreed before by the same accident).
- **The beamformer's common-port row was a dense row**, which the MNA's minimum-degree ordering on AᵀA turns into a dense LU: a
  48 × 48 feed sat minutes in the factorization. The sum is now a chain of auxiliary unknowns (`src/Core/RESOLVED.md`); the 32 × 32
  gate went from 6 s to 0.8 s.
- **A block's own noise read as radiated**: the beam functional must use the element's whole terminal current, its Bosma Norton
  current included; without it a noiseless chain read 0.098 dB. `src/Engine/RESOLVED.md`.

**Not here.** The parameter editor does not yet grey the weight rows in Elements mode (D2); the dynamic symbol is PAA-5 (Feed mode
draws the one-pin square); the noise of a receiving array, PAA-7; the fast path for identical channels, PAA-8. At 4096 elements the
array network is still a dense N×N per record frequency (268 MB each); PAA-8 owns its cost. Measured once by hand (a scratch run under
an 8 GB watchdog, not a test): on the one-element record, steered to 30° with `taylor(-30,4)`, a 48 × 48 fields run took 2.2 s and a
64 × 64 one 12.1 s (Debug), pointing error 0.045° and 0.025°, `SllDb` −29.8 and −29.9 dB.

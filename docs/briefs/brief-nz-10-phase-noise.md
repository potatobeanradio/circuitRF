# Brief NZ-10 — oscillator phase noise

Series: `brief-nz-0-overview.md`. Depends on NZ-4 (device noise), NZ-5 (HB noise) and NZ-9 (the oscillator solution).

**Why.** An oscillator's phase noise sets a receiver's reciprocal mixing and a transmitter's close-in spectrum. NZ-7
lets a user impose a measured phase-noise mask on a source. This brief computes the mask from the oscillator's own
circuit, so the effect of the resonator's Q, the device's flicker noise and the bias can be designed instead of
assumed.

**Risk (from NZ-0).** High. A phase-noise curve can look smooth and be wrong by tens of dB. Every gate is a closed form,
and a smooth curve is not evidence.

## What to build

`analysis OSC1 type=osc … PhaseNoise=yes PnStart=100 Hz PnStop=10 MHz PnNpts=61 PnLog=yes`

### The method, in our own words

1. **Far from the carrier.** The conversion matrix about the NZ-9 solution, driven by every noise source (NZ-5's
   cyclostationary sources, passive parts by Bosma), gives the noise sidebands at carrier ± f_m. That is NZ-5 at offset
   f_m from the oscillation, nothing new.
2. **Close to the carrier.** An autonomous circuit has no restoring force on its phase. So the conversion matrix
   becomes singular as f_m → 0, and the phase noise rises without bound, as it physically does.
   - The close-in part is computed from the solution's sensitivity of its frequency to a perturbation at each noise
     source: how far the oscillation's frequency moves per unit of injected current at the fundamental and at each
     harmonic. That comes from the outer (A, f) Newton's Jacobian of NZ-9, which already holds that sensitivity.
   - White sources then give a 1/f_m² slope. Flicker sources, through the same sensitivity at baseband, give 1/f_m³.
3. **Joining the two.** The two descriptions overlap in a band where both are valid. Join them there by a stated rule,
   written in the design note. The rule is not tuned per circuit. The singular point itself is never regularised by a
   small constant.

### Results

| Cube | Axes | Unit |
|---|---|---|
| `PN` | [offset] | dBc/Hz, single-sided L(f_m) at the oscillator's output port |
| `AN` | [offset] | dBc/Hz, amplitude noise |
| `PNc`, `PNpct` | [offset, part] | each part's contribution, NZ-1's layout |
| `PNcorner` | scalar | Hz, the flicker corner where the 1/f³ and 1/f² asymptotes cross |

`PN` can be pasted into a source's `PhaseNoise=` mask (NZ-7) in one action. That is the system-level link this series
exists for: a Data Display context action **Copy as phase-noise mask** writes the `PhaseNoise="…"` string.

## Gates

1. **Linear resonator, white noise.** The NZ-9 gate-1 oscillator with one white noise current across the tank:
   - `PN` follows the closed form for that tank's loaded Q and the injected density, to 0.5 dB over the 1/f² region;
   - the slope is −20 dB/decade to 0.1 dB/decade.
2. **Flicker.** Adding a flicker source to the active element gives a −30 dB/decade region below the corner the closed
   form predicts, to ±20 % in the corner frequency.
3. **Far-out floor.** Far from the carrier, `PN` + `AN` equals the NZ-5 HB noise of the same circuit with the
   oscillation held by a matching drive, to 0.1 dB.
4. **Contributions.** They sum to the total, as in NZ-1.
5. **No key, no change.** An `osc` analysis without `PhaseNoise=yes` is byte-identical.

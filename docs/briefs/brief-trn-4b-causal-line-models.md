# Brief TRN-4b — causal loss in the planar line models, and lossy lines in transient

**Series:** TRN-0 … TRN-9 · **Tag:** `R-trn4b-<n>` · **Depends on:** TRN-4 · **Design note:** §5.3
**Area:** `src/Core/Devices` (MLIN, SLIN, CPWG, TLIN, the microstrip tapers and the shared loss code), `src/Engine/Transient`
(`TransientRealization`, `TransientLines`), `src/Engine/RESOLVED.md`, `docs/design/transient.md`,
`docs/user/src/reference/components.md`.

## The gap

TRN-4 refuses every lossy or dispersive line in a transient run, because none of the shipped models can be realized
causally within |ΔS21| ≤ 1e-3: the causality correction alone moved S21 by 1.3e-3 to 2.1e-2 at a 5 GHz band edge and
2.8e-3 to 0.22 at 20 GHz. With MaxStep left automatic the fit band ends at ten times the highest source frequency, so an
ordinary 1-2 GHz bench with microstrip cannot run in transient at all.

The fault is in the frequency-domain models, not in the transient. Each gives its line a loss with no matching phase:

- **Conductor loss** rises as √f (skin effect, with roughness), but the series reactance it comes with — the conductor's
  internal inductance, equal to the skin-effect resistance — is missing.
- **Dielectric loss** rises with tanD·f while Er stays constant at every frequency. A real dielectric whose tanD is
  flat over many decades has a permittivity that falls slowly (logarithmically) with frequency; constant Er with
  constant tanD is not a physical material.

A response with loss and no matching phase is not causal, and no causal realization — the transient's, or any
time-domain solver's — can reproduce it. Making the models causal makes them more physical in every analysis, and lets
the transient realize them.

## What was measured first (step 1)

A scratch harness evaluated the shipped models (MLIN, SLIN, CPWG; three geometries each: a 0.254 mm Er 3.66 board, 0.8 mm
FR4 with tanD 0.02, GaAs/LTCC thin film) with the two missing phases added — the conductor's internal reactance equal to
its loss above its DC floor, and the dielectric's phase pinned at a reference frequency f_ref,
Δβ_d = −(2/π)·α_d·ln(f/f_ref), the mid-band form of a wideband Debye dielectric — and realized each as TRN-4 would:
a delay τ and a rational residual. Two findings set the method:

- **Taking the residual's minimum phase is not enough**: dispersion (ε_eff(f)) is a phase with no magnitude, which a
  minimum-phase residual drops. The residual must be fitted directly, phase included.
- **The delay must leave some rotation in the fit.** With τ at the smallest phase delay in band the residual is barely
  causal and a 40-pole fit misses by 1.4e-2 on FR4 at 20 GHz; with τ = τ_min − 1.5/f_max (≥ 0) the same line fits at
  order 32 within 6.8e-4. Order is chosen by the realization's own |ΔS21| on a dense grid, not the fit's error on its
  samples (which over-fits between samples near DC).

With both, every case meets |ΔS21| ≤ 1e-3 at f_max = 5 GHz and 20 GHz (largest 8.6e-4, orders 3-40). At 50 GHz the
0.8 mm FR4 microstrip (7.9e-3) and coplanar (2.0e-3) lines still miss. |ΔS11| is set by the characteristic impedance,
which the dispersive models vary with frequency and the realization holds: up to 2.6e-2 on 0.8 mm FR4 at 20 GHz, ≤ 2e-3
elsewhere at 20 GHz.

## Requirements

**R-trn4b-1 — Causal conductor loss.** MLIN, SLIN and CPWG add to their phase constant the conductor loss above its DC
floor: Δβ_c(f) = α_c(f) − α_c(0⁺), α_c(0⁺) the model's own conductor loss at 1 Hz (0 for a √f law; the thin-metal floor
for a thickness-aware one). TLIN's physical form adds its `Ac` term (∝ √f) the same way. The microstrip tapers (MTAPER, MKLOPF), whose sections
compute the same losses, take the same phase.

**R-trn4b-2 — Causal dielectric loss.** The same models add Δβ_d(f) = −(2/π)·α_d(f)·ln(f/f_ref): Er and TanD are the
values at f_ref, where the line is unchanged; below it the permittivity is slightly higher and above it slightly lower.
f_ref is 1 GHz for MLIN, SLIN and CPWG (a named constant, stated in the docs) and `F` for TLIN, whose losses are already
stated at `F` (the electrical form's `A`, ∝ f, and the physical form's `Ad`).

**R-trn4b-3 — One place.** The phase is computed by one shared function and carried in `MicrostripLineParameters` beside
the lossless β, so `LineAt`, the stamp and MMITER's corner section (`MicrostripLineModel.Propagation`) read the same
value, and Z0/ε_eff — what the line calculator reports — are unchanged.

**R-trn4b-4 — Lossy lines in transient.** A line that does not pass TRN-4's pure-delay test is realized as a delay τ =
max(0, τ_min − 1.5/f_max) and a rational residual H̃(f) = e^(−γl)·e^(j2πfτ) fitted directly over DC to f_max, its order
the lowest whose realization meets 3e-4 on a dense grid (to 40). Zc is held at the middle of its range over the band.
The residual's states are extra linear unknowns at each end, driven by the delayed wave. Accepted when |ΔS21| ≤ 1e-3
up to f_max (the TRN-4 tolerance); the note gives |ΔS21|, |ΔS11| and the order; refused by name otherwise, with the
measured error.

**R-trn4b-5 — Coupled lines stay as they are.** MCLIN, BCLIN and MBCLIN keep their loss models and their transient
refusal; making them causal is a later brief.

## Gates

1. **Causality of every shipped single-line model.** For MLIN, SLIN, CPWG and lossy TLIN at three geometries each, the
   transient realization meets |ΔS21| ≤ 1e-3 at f_max = 5 GHz and 20 GHz (the step-1 table, now through the shipped
   code). A model whose loss has no matching phase fails this gate; that is its purpose.
2. **The pin.** At f_ref each model's S-parameters equal today's to the conductor term alone (dielectric change 0);
   a lossless line is bit-identical to today's.
3. **Lossy line against the frequency domain.** A step through a 25 mm lossy microstrip in 50 Ω: its transform equals
   the S-parameter engine's S21 of the same line within 1e-3 plus the integration's own error up to f_max.
4. **Re-baselined, deliberately.** Every existing test whose expected value moves is updated with the new number, and
   the size of the move is recorded in `src/Engine/RESOLVED.md`.

## Not here

Causal coupled lines; a causal (complex, frequency-dependent) characteristic impedance; a per-substrate f_ref parameter;
the full wideband Debye permittivity inside the quasi-static formulas (the mid-band form is first order in tanD).

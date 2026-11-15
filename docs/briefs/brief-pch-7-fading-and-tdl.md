# Brief PCH-7 — fading, Doppler spectra, tapped delay lines

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch7-<n>` · **Depends on:** PCH-6 · **Design note:** §2.4, §4.1, §4.3, §8.1
**Area:** `src/RfCore/Propagation/` (generator, TDL tables, power-delay-profile reader), `PropagationChannelModel`,
`EnvelopeChannel`, `testdata/propagation/`, `src/RfCore/RESOLVED.md`.

## The gap

PCH-6's channel is deterministic. Small-scale fading — a tap whose amplitude is Rayleigh or Rician and whose phase turns
at the Doppler rate — is what makes a link's EVM and BER statistical, and what standard channel profiles specify.

## Requirements

**R-pch7-1 — The generator.** A tap's complex gain as a sum of sinusoids (note §4.3): frequencies from its Doppler
spectrum, random phases, all from stateless draws `Hash(Seed, Drop, stream, k)` (the yield engine's; moved down to RfCore
if it cannot be reached). Evaluated at any t without state. `Sinusoids=` the count, default the smallest that passes the
gates with margin.

**R-pch7-2 — Distributions.** `Fading=rayleigh|rician|nakagami`, `KFactor` (a line-of-sight component at its own Doppler
and phase), `m`. Per-tap overrides from a profile or a file.

**R-pch7-3 — Doppler spectra.** `classical`, `flat`, `gaussian`, `los`; maximum Doppler from `Velocity` and the carrier,
or `fD`.

**R-pch7-4 — Profiles.** `Multipath=tdl Profile=`: 3GPP TR 38.901 TDL-A…E (normalised delays scaled by `DelaySpread`;
K for D and E), 3GPP TS 36.104 EPA/EVA/ETU, ITU-R M.1225 pedestrian and vehicular A/B, 3GPP TR 38.811 NTN-TDL-A…D. Tables
embedded with their document edition.

**R-pch7-5 — User profiles.** `Multipath=file File=pdp.csv`: delay, power (dB), optionally phase, K, Doppler spectrum per
tap; or a sampled impulse response (delay, complex gain), taken as static taps or as a profile with `Fading=` applied.
Units in the header; a profile that does not normalise is normalised and says by how much.

**R-pch7-6 — Reported.** The drop's tap gains over the record, RMS delay spread, f_D, T_c and coherence bandwidth
(note §2.4), H(f, t) on the band; a power-delay-profile plot and a Doppler-spectrum plot in the Data Display defaults.

## Gates

1. **Distributions.** Over 10⁵ samples at a fixed seed the envelope passes a stated goodness-of-fit test against Rayleigh,
   Rician (K = 3, 10) and Nakagami (m = 2).
2. **Correlation.** The classical spectrum's sample autocorrelation follows J₀(2πf_D τ) to 0.02 over its first three zeros.
3. **Level crossings.** Level-crossing rate and average fade duration at −10 dB within 5 % of their closed forms.
4. **Profiles.** Each embedded profile's powers sum to 1 to 1e-12 and its RMS delay spread equals the scaled
   `DelaySpread` to 1e-9.
5. **Stateless.** Drop 37 alone equals drop 37 inside a 100-drop sweep, bit for bit; adding a tap leaves the others'
   gains unchanged.
6. **Inter-carrier interference.** An NR signal through flat Rayleigh with `genie`: EVM rises with f_D/Δf_sc and matches the
   independent script's prediction within 10 % at three Dopplers.

## Not here

Angles and arrays (PCH-8). Drawing shadowing and line-of-sight states (PCH-10).

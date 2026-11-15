# Brief PCH-2 — atmosphere, rain and satellite paths

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch2-<n>` · **Depends on:** — (PCH-1's model contract if built first) ·
**Design note:** §2.3, §3.2
**Area:** `src/RfCore/Propagation/`, `testdata/propagation/`, `tests/RfCore.Tests/Propagation/`, `src/RfCore/RESOLVED.md`.

## The gap

Above a few GHz and on every satellite path the loss that sets the margin is atmospheric: gas, cloud, rain at a
percentage of the time, scintillation. A satellite link's distance, latency and Doppler follow from its orbit and
elevation. circuitRF has a dB/km term and nothing else.

## Requirements

**R-pch2-1 — Gas.** ITU-R P.676: specific attenuation of oxygen and water vapour by the line-by-line method, and the
path attenuation for a terrestrial path and a slant path; the approximate method where its validity allows, the choice
stated in the result.

**R-pch2-2 — Rain.** P.838 specific attenuation (k, α by frequency and polarization tilt); P.530 terrestrial rain
attenuation exceeded p % of the time; the conversion between percentages the recommendation gives.

**R-pch2-3 — Earth–space.** P.618: rain attenuation at p %, scintillation, gas (P.676), clouds (P.840) and their
combination at p %; depolarization from rain where it gives it. Inputs a map would give (rain rate R₀.₀₁, rain height,
water-vapour density, cloud liquid water) are parameters with stated defaults; nothing reads an ITU map (note D10).

**R-pch2-4 — Ionosphere.** P.531 Faraday rotation and the scintillation index for links below ~3 GHz, as inputs for a
polarization rotation and a fading depth.

**R-pch2-5 — 3GPP TR 38.811 large-scale.** Line-of-sight probability by elevation, shadow fading and clutter loss for dense
urban, urban, suburban and rural, at S and Ka band as the report gives them.

**R-pch2-6 — Orbit geometry.** For a circular orbit of altitude h and an elevation ε: slant range, the free-space loss over
it, the latency, the range rate and its rate over a pass (Earth's rotation optional), hence ν₀ and its drift at a carrier.
A geostationary link has zero range rate. Inputs out of range (ε < 0, h ≤ 0) refuse.

**R-pch2-7 — Availability.** Every time-percentage model takes availability as `Availability` (%), converted once, so a
user never sets p and its complement inconsistently.

## Gates

1. **Published examples.** P.676, P.838, P.618 (and any other with published validation data) reproduce it to the
   precision printed.
2. **Independent scripts** for the rest, to 1e-9 dB.
3. **Geometry.** Slant range at ε = 90° equals h exactly; at 600 km and ε = 0 the maximum range rate equals the orbital
   velocity times R_E/(R_E + h), to 1e-9 relative; geostationary range rate is 0.
4. **Validity.** Each model refuses one point past each limit.

## Not here

Rain and scintillation time series (a drop holds them constant, note §2.4). The land-mobile state model (PCH-10).
Any user interface.

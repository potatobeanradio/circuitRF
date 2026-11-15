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

## Completion note (2026-11-15)

**Built.** `src/RfCore/Propagation`: `ItuP676` (Annex 1 line by line, 44 oxygen and 35 water vapour lines; terrestrial
§2.1; slant §2.2.1 with its 922 layers, station heights by eqs (16a)-(16d), bending), `ItuP835` and `ItuP453`
(`Atmosphere.cs`), `ItuP838`, `ItuP530`, `ItuP840` (`Rain.cs`), `ItuP618` (rain, scintillation, total), `ItuP531`,
`Tr38811`, `SatelliteGeometry` (`Satellite.cs`); `RfCore.Statistics.GammaFunction`. In the registry: the added losses
`Availability`, `Gas`, `Rain` (`fixed`; `drawn` refused until PCH-10), `Clouds`, `Scintillation`, `Ionosphere` and
their inputs, terrestrial or Earth-space by the model; and the `satellite` model (altitude, elevation, pass, TR 38.811
scenario and state), whose result carries the slant geometry (`PathLossResult.Geometry`: range, latency, range rate,
Doppler and its drift).

**Gates** (`tests/RfCore.Tests/Propagation/AtmosphereAndSatelliteTests.cs`, 5 tests; with PCH-1's, 14 tests in ~90 ms):
1. Published examples (a `FixtureFact` over SG3's workbook): P.676, P.838, P.840, P.618 rain, scintillation and total,
   every case, to 1e-12 relative (largest difference ~5e-15); the slant path to 1e-9 dB (9e-13).
2. Independent script: P.530, P.531, TR 38.811 and the satellite model with every atmospheric term, through the
   registry, to 1e-9 dB (`testdata/propagation/reference`).
3. Geometry: zenith range = altitude; the horizon range rate at 600 km = v·R/(R + h); geostationary range rate 0; and
   range rate, drift and Doppler against finite differences of the simulated orbit.
4. Validity: 23 refusals, one past each limit, plus Earth-space effects on a terrestrial path and `Rain=drawn`.
Plus: the availability is the one time percentage (gate on P.530 and P.618), and the Nakagami fade inverts P(m, x).

**What changed from the brief, and why.**
- **P.676's approximate method is not built**: its coefficients are data files integral to the recommendation, not
  redistributable. Annex 1 does the slant path through P.835's reference atmosphere.
- **Published examples exist for P.676, P.838, P.840 and P.618 and are used, but not committed** (their terms of reuse
  are not stated): the gate is a `FixtureFact`. P.530, P.531, TR 38.811 and the geometry are gated by the script.
- **P.531's fade depth** is the Nakagami intensity at 100 − availability % — the recommendation gives the distribution,
  not a percentage.
- **P.618's scintillation includes p = 0.01 %**, where the published examples evaluate it.

Findings in `src/RfCore/RESOLVED.md`.

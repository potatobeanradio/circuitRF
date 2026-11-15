# Brief PCH-1 — large-scale models, terrestrial and indoor

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch1-<n>` · **Depends on:** — · **Design note:** §3.1, §3.3
**Area:** `src/RfCore/Propagation/` (new), `testdata/propagation/`, `tests/RfCore.Tests/Propagation/`, `src/RfCore/RESOLVED.md`.

## The gap

The only path loss circuitRF computes is free space plus a dB/km term (`ChainLink`). A terrestrial or indoor link is
designed against a path-loss model with its shadowing spread and, for a cellular scenario, a line-of-sight probability
and delay and angle spread statistics. None of these exist, so neither the Chain Budget nor a component can state the
loss a real link sees.

## Requirements

**R-pch1-1 — The model contract.** A model is a pure function from (frequency, geometry, its own parameters) to a result
carrying: the median loss L (dB), its breakdown by term, the shadowing σ (dB), and where the model gives them the
line-of-sight probability, the Rician K and the delay and angle spread statistics (median and spread of each, in the
units the source document uses). Each model carries its document and edition as a constant and **refuses outside its
validity range** with a sentence naming the range and the value. No model extrapolates silently.

**R-pch1-2 — Simple models.** `free` (ITU-R P.525), `logdist` (free space to d₀, then exponent n, σ), `multislope`
(breakpoints with an exponent per segment, continuous at each breakpoint), `tworay` (direct plus ground-reflected ray
with the Fresnel reflection coefficient of a ground of stated permittivity and conductivity, either polarization).

**R-pch1-3 — 3GPP TR 38.901 scenarios.** `UMa`, `UMi` (street canyon), `RMa`, `InH` (mixed and open office), `InF` (SL,
DL, SH, DH, HH): line-of-sight and non-line-of-sight path loss including the breakpoint distance, line-of-sight
probability, σ, and the large-scale parameter statistics table (delay spread, the four angle spreads, K) for each,
over 0.5–100 GHz with each scenario's own height and distance limits. `Los=auto` reports the probability; drawing the
state is PCH-10's.

**R-pch1-4 — ITU-R indoor and short-range.** P.1238 (distance power-loss coefficient and floor penetration loss by
environment and band; the site-general model) and P.1411 (site-general line-of-sight and non-line-of-sight below rooftop,
the street-canyon corner model).

**R-pch1-5 — ITU-R point-to-area.** P.1546 from its tabulated field-strength curves (land, sea, mixed paths; time and
location percentages), the curves embedded as a resource with their source edition. If the curves cannot be embedded
under their terms of use, stop and report.

**R-pch1-6 — Terrain profile.** A CSV reader for distance, height and optional clutter class, with units in the header.
P.526 diffraction over it (single knife-edge, the multiple-edge method, smooth spherical earth) and P.1812 over it. A
profile that does not start at the transmitter and end at the receiver is refused.

**R-pch1-7 — Added losses.** Partition list (dB each), clutter (P.2108), building entry (P.2109), vegetation (P.833),
polarization mismatch and pointing loss, each a line of the breakdown.

**R-pch1-8 — A model table.** One registry of model names → their parameters (name, unit, default, range, help), used by
PCH-3's editor, PCH-6's component and `reference`, so a model is described once.

## Gates

1. **Published examples.** Every model with validation examples published alongside its recommendation reproduces them
   to the precision printed.
2. **Independent scripts.** Every other model against `testdata/propagation/<model>_reference.py` output, a script
   written from the document alone, to 1e-9 dB (the script and its document edition committed).
3. **Validity.** Each model refuses one point beyond each of its limits, with the range in the sentence.
4. **Continuity.** `multislope` and every 38.901 breakpoint are continuous to 1e-12 dB across the break.
5. **Registry.** Every model's parameters appear in the registry with a unit and a range; a model with an unregistered
   parameter fails a scan test.

## Not here

Atmosphere, rain and satellite paths (PCH-2). Drawing shadowing and line-of-sight states (PCH-10). Any user interface.
Bundled terrain or clutter data (note D10).

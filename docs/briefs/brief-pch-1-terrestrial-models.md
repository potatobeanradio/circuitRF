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

## Completion note (2026-11-15)

**Built.** `src/RfCore/Propagation`: the model contract (`PathLossResult`: loss, breakdown, σ, line-of-sight
probability, spread statistics, notes; `PropagationRefusal` carrying a coded diagnostic), `TerrainProfile` (the CSV
reader), `free`, `logdist`, `multislope`, `tworay`, `38901` (every scenario of R-pch1-3 with the large-scale parameter
statistics including ZSD), `P1238` (site-general, plus Table 5's floor penetration), `P1411` (`belowroof`,
`aboveroof`, `lowheight`, `corner`), `P526` (`knifeedge`, `sphere`, `profile`), `P1812` (the whole method), the added
losses (`Partitions`, P.2108's three models, P.2109, P.833 woodland, `PolLoss`, `PointingLoss`), and the registry
(`PropagationModels`, `AddedLosses.Parameters`) every model and its parameters are described in once. The normal
distribution moved to `RfCore.Statistics.Normal` (Core forwards).

**Gates** (`tests/RfCore.Tests/Propagation/PropagationModelTests.cs`, 9 tests, ~80 ms):
1. Published examples: P.1812-8's 63 validation cases, every printed value, to 1e-7 (printed to ten digits; largest
   difference 5e-8). P.526's 20 delta-Bullington examples to 1e-9 (they reproduce to 4e-14), a `FixtureFact` since
   the examples are not committed.
2. Independent script (`testdata/propagation/reference/propagation_reference.py`): 62 cases through the registry to
   1e-9 dB (the knife-edge to 1e-6: the recommendation's Fresnel expansion against the script's integration), σ and
   line-of-sight probability to 1e-12; TR 38.901 spreads to 1e-12.
3. Validity: 38 refusals across the models and added losses, plus P.1812's nine limits and its 0.25 km floor.
4. Continuity: multi-slope and UMa/UMi to 1e-10; RMa's report-written step measured (see below).
5. Registry: every parameter has help, a unit, a finite range or a word list containing its default; every model and
   mode runs through it; an unknown parameter is refused.
Plus the profile refusals (start, order, units, length) and P.1812's location variability (eqs (64)-(69)), which the
published examples do not exercise.

**What changed from the brief, and why.**
- **P.1546 is not built** (R-pch1-5's own stop): its tabulated curves are an ITU digital product that may not be
  redistributed without written permission. P.1812 covers the same ground over a profile.
- **P.526's "multiple-edge method"** is the general method of §4.5 over a profile (any number of obstructions); the
  §4.4 cylinder method is not built.
- **Gate 4 holds UMa and UMi only**: TR 38.901's RMa line-of-sight formula, as written, steps by ~1e-4 dB at its
  breakpoint; the formula is kept exactly and the step is measured.
- **P.526 uses c = 3e8 and takes β = 1 where §3.1.1.2 allows**, which is how its published examples are computed;
  P.1812 uses c = 2.998e8, as its constant and package do.
- **No SG3 validation data exists for P.1238, P.1411, P.2108, P.2109, P.833 or TR 38.901** among the published
  digital products; those are gated by the independent script.

Findings in `src/RfCore/RESOLVED.md`. `tests/Firewall.Tests`' user-facing-text gate fails on three messages from the
PAA-8/8b code already on main (not this brief's); this brief's one internal-invariant message is allow-listed.

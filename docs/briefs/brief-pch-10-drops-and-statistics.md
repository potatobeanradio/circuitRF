# Brief PCH-10 — drops, reuse, statistics, scenario drops, satellite states

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch10-<n>` · **Depends on:** PCH-7 (PCH-8, PCH-9 for arrays) · **Design note:**
§4.4, §8
**Area:** `src/RfCore/Propagation/` (scenario procedure, P.681 states), `PropagationChannelModel`, `CircuitEvaluation`
(reuse beside PAA-8b's), `Evaluator.AxisFunctions`, `testdata/propagation/`, `src/Engine/RESOLVED.md`,
`src/RfCore/RESOLVED.md`.

## The gap

A link is specified by outage and availability, which come only from many realizations. PCH-6/7 draw the small-scale
fading; nothing draws shadowing or the line-of-sight state, nothing generates a scenario's clusters, and a sweep over
realizations re-solves a nonlinear transmitter that has not changed.

## Requirements

**R-pch10-1 — What a drop draws.** Shadowing X (the model's σ), line-of-sight state from its probability when `Los=auto`, rain
attenuation when `Rain=drawn` (a time percentage drawn, the attenuation exceeded for it; note §3.2),
the small-scale realization (PCH-7/8), and for `Multipath=scenario` the clusters — all stateless, by stream (note §8.1).

**R-pch10-2 — Scenario drops.** `Multipath=scenario`: the 3GPP TR 38.901 procedure for the ends' geometry — correlated
large-scale parameters, cluster delays, powers and angles, rays and their coupling, cross-polar ratios, initial phases —
for each PCH-1 scenario. Gated against an independent implementation written from the report alone (gate 3).

**R-pch10-3 — Satellite states.** ITU-R P.681's state model: line of sight, shadowed, blocked, each with its
lognormal-shadowed Rician statistics and state durations; a drop draws the state sequence over the record (or one state
when the record is much shorter than a state) and reports it.

**R-pch10-4 — Reuse across drops.** In a sweep over `Drop`: with no channel cycle and a transmit side whose resolved
parameters hash the same (PAA-8's hash), the transmit side's recorded waves are reused and only the receive side is solved;
a transmit side whose beam changes with the drop is solved once per distinct beam and cached by its weights; a receive side
on the fast path is not stepped. Counters: sides solved, reused, cached beams.

**R-pch10-5 — Statistics.** `outage_over(x, threshold)`: the fraction of drops below a threshold, with its binomial
interval; the existing `cdf`, `pctl_over`, `mean_over`, `std_over`, `yield_over` work over a drop axis unchanged.

**R-pch10-6 — Cost recorded.** A drop's cost at 64, 1024 and 4096 elements, linear and nonlinear receive sides, recorded
in `RESOLVED.md` (no timing test). If a large nonlinear receive array is impractical across drops, the docs say so.

## Gates

1. **Reuse is exact.** A 50-drop sweep with reuse equals the same sweep with reuse off, to 1e-12, and the counters show 1
   transmit solve against 50.
2. **Shadowing and state.** Over 10⁴ drops the shadowing sample has the model's σ (goodness of fit at a fixed seed) and the
   line-of-sight fraction equals the probability within its interval.
3. **Scenario.** For UMi and InH, the drawn delay spread, angle spreads and K over 10⁴ drops match the independent
   implementation's distributions (stated statistic, fixed seeds) and cluster powers sum to 1.
4. **Satellite states.** State occupancy and mean durations over 10⁴ drops match P.681's parameters within their intervals.
5. **Fading BER.** Flat Rayleigh BPSK over drops: average BER against ½(1 − √(γ/(1+γ))) within the interval at three SNRs.
6. **Outage.** `outage_over` of received power at the Rayleigh 99 % margin is 1 % within its interval.

## Not here

Beam tracking; time series of rain and scintillation within a drop.

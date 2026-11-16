# Brief PCH-11 — the Chain Budget's Link bench

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch11-<n>` · **Depends on:** PCH-3, PCH-4, PCH-7, PCH-10 · **Design note:** §9.3
**Area:** `src/Design/Chain/ChainBenches.cs`, `src/Render/DataDisplay/ChainBudgetDisplay`, `src/Ui/Chain`,
`docs/design/chain-budget.md`, `src/Design/RESOLVED.md`.

## The gap

Every number of a chain's budget is checked by a bench, except the link: its received power and, after PCH-3, its
margins are closed forms with nothing simulated behind them.

## Requirements

**R-pch11-1 — The bench.** For a chain with a link, the transfer adds `<chain> Link`: the TX bench's PA at its back-off
with the link's transmit gain, a `PropagationChannel` with the chain's model, availability and fading, pin ends with the
link's antenna gains, the receive chain, and a modulated source (the Demo bench's signal, or PCH-5's single carrier for a
satellite link).

**R-pch11-2 — Margins by Monte Carlo.** A drop sweep of the received power through the chain's linear analysis, sized to
≥ 100/(1 − p) drops; measurement rows: the outage at the budget's margin, the received-power percentile at the availability,
each beside the budget's number and the difference, as every bench's display does.

**R-pch11-3 — The signal at two drops.** An envelope run with `Equalizer=pilot` at the median drop and at the outage-
percentile drop picked from that sweep: EVM, counted and semi-analytic BER at each.

**R-pch11-4 — Window.** The Link bench appears in the transfer list; the Blocks view's propagation glyph opens it.

## Gates

1. **Margin.** On a Rayleigh 99 % link the Monte Carlo outage at the budget's margin is 1 % within its interval.
2. **Satellite.** On a geostationary link at 99.9 % with `Rain=drawn`, the drop sweep's rain attenuation at the 99.9th
   percentile equals the budget's rain margin within its interval; with `Rain=fixed` every drop's rain term equals it to
   1e-9 dB.
3. **Transfer.** The bench passes `check` and simulates; its display's rows are budget, simulated and difference.

## Not here

A bench that emits PhasedArrays (`phased-array.md` §11).

## Completion note (2026-11-15)

- R-pch11-1: `ChainBench.Link` (`<chain> Link`) for a chain with a link and no receive array; `ChainLinkBench` maps the link onto a
  Propagation Channel and picks the signal (NR at the link's modulation, a single carrier on a satellite link or for BPSK/8PSK).
- R-pch11-2: an S-parameter run swept over 100/(1 − p) drops (1,000 to 100,000); each drop's received power is the antenna power
  plus the channel's `Gain_dB`, read by the new `A.link("CH1", "Gain_dB")` accessor; rows for the outage (with its interval),
  the received power at the availability, the margin and the rain, each budget, simulated and difference.
- R-pch11-3: an envelope run with `Equalizer=pilot` at the sweep's median and outage-percentile drops, picked when the bench is
  written by evaluating each drop's channel (the same draws as the run); EVM beside the budget's SNR, BER counted and estimated.
  An envelope probe past a channel read as carrying no RF and was blanked; the threshold now allows for the channel's gain.
- R-pch11-4: listed with the benches Transfer writes; the Blocks view's propagation card opens the written bench.
- Gates in `tests/Ui.Tests/Chain/ChainLinkBenchTests` (gate 2 over 10,000 drops rather than the bench's 100,000). `Rain=drawn` is
  now accepted by the budget (taken as `fixed`). Findings in `src/Design/RESOLVED.md` and `src/Engine/RESOLVED.md`.

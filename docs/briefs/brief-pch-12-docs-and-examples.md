# Brief PCH-12 — user pages, format references, examples

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch12-<n>` · **Depends on:** all · **Design note:** all
**Area:** `docs/user/src/reference/` (new `propagation-channel.md`; `chain-budget.md`, `phased-array.md`,
`signal-designer.md`, the envelope page, the components page), the `reference` resources (analyses, components),
`examples/` (three new workspaces), `docs/design/propagation-channel.md`.

## The gap

A component with this many models is unusable without a page that says which to pick and what each result means, and a
link is easiest learned from a working one.

## Requirements

**R-pch12-1 — The user page.** What the channel is and is not (distributions, not a site); choosing a model by
environment and band (a table); pin and instance ends; the regime readouts and what they mean for a result; fading and
drops; equalizer choices; beam selection including how to pick another beam in the tool; satellite links; the far-field
warning. Each model's parameters and validity from the registry, not retyped.

**R-pch12-2 — Pages touched.** The Chain Budget page (Propagation, margins, Link bench); the Phased Array page (channel
ends, the rays and Beams list); the envelope page (`Equalizer`, BER, CFO); the Signal Designer page (single carrier).

**R-pch12-3 — Examples.** Under `examples/`, each with a README naming what it shows and the settings it traded for run
time:
- **mmWave Array Link** — a 16×16 transmit array with PAs to an 8×8 receive array, 3GPP TR 38.901 UMi, CDL-C, automatic
  beam pair, an NR signal, EVM and BER over drops.
- **LEO Satellite Downlink** — a 600 km pass at a stated elevation, P.618 at 99.5 %, P.681 states, its Doppler and
  drift, a single-carrier 16APSK signal through a transponder model.
- **Indoor IoT Link** — P.1238 with partitions, TDL from a user power-delay profile, an FSK or chirp signal, outage over
  drops.

**R-pch12-4 — Design note.** Brought up to what was built, each section's status.

## Gates

1. Each example passes `check` and simulates headlessly at its shipped settings within the example budget.
2. The user page's model table lists every registered model (a scan test).
3. Figures checked with Svg.Skia renders, looked at.

## Not here

Anything new in the engine.

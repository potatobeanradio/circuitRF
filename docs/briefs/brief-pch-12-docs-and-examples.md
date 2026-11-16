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

## Completion note (2026-11-16)

- R-pch12-1: `docs/user/src/reference/propagation-channel.md`, in the reading order after the Chain Budget: what the channel
  is and is not, a choosing table by environment and band, every model's table and parameter tables read from the registry
  (`{{table: propagation-models}}`, `{{table: propagation/<model>}}`, `{{table: propagation/losses}}`, `DocTables`), ends,
  regime readouts, fading, drops, the far end, beams and the Channel tab, satellite links, the far-field warning. Each registry
  model gained a one-line `Validity` (`ModelDescriptor.Validity`), written from the ranges its code refuses outside.
- R-pch12-2: Chain Budget (the Propagation section's four groups, the margins' formulas, every readout cube), Phased Array
  (the tool's Channel tab; §11 as a channel end), Simulations' envelope (the single carrier's acquisition; the genie through a
  channel), Signal Designer (a single carrier through a link), Components (`Drift0_Hzps`), CLI (`env` evaluates measures).
- R-pch12-3: *Indoor IoT Link* (P.1238 with floors and partitions, a TDL from `profiles/office_pdp.csv`, FSK; 2,000 drops of
  coverage in 2 s, 50 of bit errors in 3 s), *LEO Satellite Downlink* (20 GHz, 600 km at 30°, P.618 at 99.5 %, P.681 suburban,
  the pass's −399 kHz Doppler and −991 Hz/s drift, 16APSK through IMUX/TWTA/OMUX; 40 drops in 20 s), *mmWave Array Link* (a
  28 GHz patch element solved once, 532 unknowns in seconds, reduced to a 5° grid and shipped in `results/`; 16 × 16 to 8 × 8
  through UMi NLOS and CDL-C, auto beam pair, NR 64-QAM; 20 drops in about a minute). Built from netlists by
  `tests/Ui.Tests/Examples/PropagationExamplesAuthoring` (CRF_AUTHOR_PCH_EXAMPLES=1). Each README states its trades: the
  mmWave element amplifiers are linear with one compressing pre-driver; the TWTA has no AM/PM.
- R-pch12-4: the design note's status, and a status-by-section table of what each brief built and where it differs.
- Gates: 1 `PropagationExamplesTests` (every bench passes `check` and is its netlist; the shipped runs' times are in the
  READMEs); 2 `PropagationChannelPageTests`; 3 no new figure (tables only); the three benches' schematics rendered and looked at.
- Fixed on the way: a single carrier's offset beyond a fraction of a bin read as noise (the delay taken a record away, and no
  acquisition: `src/RfCore/RESOLVED.md`); a channel into an array crashed any noisy envelope run (`src/Engine/RESOLVED.md`);
  `env` evaluated no measure line (`src/Cli/RESOLVED.md`); `FskTests.F1`'s refusal text, stale since PCH-5. Recorded, not
  fixed: an envelope run's device pass is quadratic in interface nodes, so 256 nonlinear element amplifiers are impractical.

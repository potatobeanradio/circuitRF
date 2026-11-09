# Brief NZ-11 — the "Receiver Noise" example, and the docs pass

Series: `brief-nz-0-overview.md`. Last. Depends on whichever of NZ-1…10 have landed: each bench below names its brief,
and a bench whose brief has not landed is left out, never stubbed.

**Why.** PR #50's "S-Parameter Noise" example teaches the noise analysis on its own terms. Once the series lands, there
is no worked design showing how the pieces fit: an LNA, then a mixer, then a whole receiver chain with a real LO. The
docs would describe each feature where it lives, with no page tying the noise work together.

## The example workspace: `examples/Receiver Noise`

Benches numbered in run order, each running headless in a few seconds:

| Bench | Shows | Brief |
|---|---|---|
| 1 LNA Noise Match | A one-stage LNA on a built-in FET with `Tg`/`Td`: NF, NFmin, Sopt, the noise-gain-stability chart, and which part dominates (NFpct as a bar trace) | 1, 2, 4 |
| 2 Receiver Budget | The chain from system blocks: LNA, filter, mixer stand-in and IF amplifier with `NF=`, cascade NF against Friis as a measurement | 3 |
| 3 Mixer Noise | A diode or FET mixer: SSB and DSB NF against LO drive, and the image's share of the noise | 5 |
| 4 LNA Under a Blocker | NF against blocker power | 5 |
| 5 Noise Sourcepull | NF and gain contours on the source plane, under drive | 6 |
| 6 Receiver EVM | An NR carrier through bench 2's chain with a phase-noise LO and thermal noise: EVM against input power, the constellation, and the noise/distortion split | 7, 8 |
| 7 VCO Phase Noise | An oscillator: Fosc against tuning voltage, its phase noise, copied as the mask bench 6's LO uses | 9, 10 |

The README opens with the run order, per the examples' own convention, and states for each bench what to look at and
what number to expect. Every number quoted in the README is asserted by the example's test, so the README cannot drift
from the result.

## The docs pass

- **Reference ▸ Simulations** gains a short **Noise** overview: one paragraph per analysis that computes noise, what
  each answers, and a link to its section. A user looking for "noise figure" lands there from search.
- **The conventions table** (T0, single-sided, absolute, ambient vs part temperature, the seed) lives once, on that
  overview, and every other section links to it instead of restating it.
- **`docs/PRD.md` and `docs/Development_Plan.md` §11** record what landed and what stays deferred: transient noise,
  noise in SDD, and a nonlinear device's own noise in envelope, if NZ-7's follow-up has not happened.
- **`reference` text for MCP** gains every key the series added. A test diffs the key lists of
  `AnalysisDirectiveSchema` against the reference text, if such a test does not already exist.

## Gates

1. The example's test runs every bench headless and asserts each README number.
2. `circuitrf check` passes on the workspace with 0 errors, and its warnings are the ones the README explains.
3. A UiSnapshot of each bench's Data Display is looked at.

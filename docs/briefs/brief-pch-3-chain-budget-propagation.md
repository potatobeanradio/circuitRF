# Brief PCH-3 — the Chain Budget's propagation and margins

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch3-<n>` · **Depends on:** PCH-1, PCH-2 · **Design note:** §9.1, §9.2
**Area:** `src/Design/Chain/ChainLink.cs`, a new `src/Design/Chain/ChainPropagation.cs`, `ChainDesignIo`, `ChainBudget`
(readouts and `ToDataSet`), `src/Cli/Chain.cs`, `src/Ui/Chain`, `src/Ui/Views/Chain`, `docs/design/chain-budget.md`,
`src/Design/RESOLVED.md`.

## The gap

The Chain Budget's link is a transmitter, free space and a dB/km term. A link budget is closed against a path-loss model,
and its margin is stated at an availability: shadowing, fading and rain margins, the SNR the modulation needs, and the
margin left. None of that can be written today, so a budget's received power is a best case with no probability
attached.

## Requirements

**R-pch3-1 — Propagation replaces Path.** `ChainLink.Path` becomes the Propagation section: `Model=` any PCH-1/PCH-2
model, its parameters from the registry (R-pch1-8) edited with the stage rows' own editor, every one a number, an
expression in `freq` or a table, as the link's inputs are today. `Model=free` with `Distance` and `Attenuation` reads
exactly as today's link: every existing chain's numbers unchanged.

**R-pch3-2 — Breakdown.** Free space, the model's excess, gas, rain, clutter, partitions, building entry, vegetation,
polarization and pointing, each a row of the budget's link readout and a cube in `ToDataSet`.

**R-pch3-3 — Margins at an availability.** `Availability` (%), `Fading=none|rayleigh|rician|nakagami` with `KFactor`/`m`,
σ from the model or stated. Note §9.1's M_shadow, M_fade, the composite margin (default; lognormal-shadowed fading by
numerical integration) and the sum as the conservative option, and rain at the availability (P.530 or P.618 by path
type).

**R-pch3-4 — Required SNR and link margin.** A target BER and the modulation (from the chain's signal, or stated) give
the required Eb/N0 and SNR, uncoded, closed form; with the chain's noise figure and bandwidth, the sensitivity; the link
margin = received power − margins − sensitivity, and whether the availability is met. A readout of the distance at which
the margin is zero.

**R-pch3-5 — Satellite links.** `Model=satellite` takes altitude and elevation (R-pch2-6) for the distance and reports
latency and Doppler; P.618 for the atmosphere.

**R-pch3-6 — Sweeps.** Distance, elevation, availability and frequency are sweepable through `ChainBudget.Sweep` as any
input is: margin against distance is one sweep.

**R-pch3-7 — Window and Blocks view.** The Propagation section in the link editor, grouped as the registry groups; a
propagation glyph between the transmitter and the chain in the Blocks view with the total loss and margin beneath it.
UiSnapshot PNGs of both, looked at.

**R-pch3-8 — Format.** The `.cchain` version rises only for a chain using a new field, as version 2 did; an older reader
refuses such a chain rather than dropping fields.

**R-pch3-9 — CLI.** `circuitrf chain` prints the breakdown and margins; `-o budget.npy` carries them.

## Gates

1. **Unchanged.** Every chain under `testdata/chain/` gives byte-identical budgets.
2. **Margins.** Rayleigh M_fade = 20.0 dB at 99 % and 30.0 dB at 99.9 % (to 0.01 dB); lognormal σ = 8 dB at 90 % gives
   10.25 dB; the composite margin against an independent numerical integration (`testdata/chain/link_reference.py`
   extended) to 1e-6 dB.
3. **Required SNR.** QPSK at BER 10⁻⁶ against the closed form to 1e-9 dB.
4. **Satellite.** A geostationary downlink at 20 GHz, 99.9 %, matches the independent script's total to 1e-6 dB.
5. **Format.** A version-1 chain saves byte for byte; a chain using `Model=38901` writes the new version.

## Not here

The Link bench that checks the margins by simulation (PCH-11). Coded error rates.

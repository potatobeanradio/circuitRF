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

## Completion note (2026-11-15)

**Built.** `src/Design/Chain/ChainPropagation.cs`. The link's `Path` is now the Propagation section: `Model=` is any
model in the registry, and its parameters and added losses use the registry's own names. A number may be written in
its own unit, as an expression in `freq` or as a table, and is read at the carrier. The section also has the heights,
`Fading=none|rayleigh|rician|nakagami` with `KFactor`/`m`, `Sigma`, `Margin=composite|sum`, `TargetBer` and
`Modulation`. In `ChainLink`, the received power is EIRP minus the breakdown plus the receive gain, and a `TargetBer`
sets the required SNR. `ChainBudget` adds the sweeps `Distance`, `Elevation` and `Availability`, and the sweep
quantities `PathLoss_dB`, `Margins_dB`, `Sensitivity_dBm` and `LinkMargin_dB`. `ToDataSet` writes the breakdown both as
scalars and as `Loss_dB` over a `term` axis, along with the margins, the required SNR, the link margin, its
availability verdict and the zero-margin distance, plus a satellite path's slant range, latency and Doppler. Format
version 3 (`ChainDesignIo`). `circuitrf chain` prints the propagation, satellite, margins, required and link-margin
lines. In the window, the Link tab's Propagation section is grouped into Path, Large scale (the model's own),
Margins and Added losses (collapsed), with list values as pickers. Below them are the Margins and Path loss tables
and a tab for each new sweep. The Blocks view has a propagation card before IN showing the model, loss, margins and
link margin. UiSnapshot gained `--chain-tab` and `--chain-scroll`.

**Gates** (`tests/Ui.Tests/Chain/ChainPropagationTests.cs`, 8 tests, ~0.5 s):
1. Unchanged: all 350 cubes of the five shipped chains and the link reference match bit for bit
   (`testdata/chain/unchanged_budgets.json`, written before the change).
2. Margins: Rayleigh is −10·log10(−ln p), which is 30.00 dB at 99.9 % and 19.978 dB at 99 % (see below). Lognormal
   σ = 8 dB at 90 % is 10.25 dB. Six shadowing, fading and composite cases match `link_reference.py`'s own integration
   to 1e-6 dB.
3. Required SNR: QPSK at 10⁻⁶, 16-QAM and 64-QAM match the closed forms to 1e-9 dB, and the target sets the sensitivity.
4. Satellite: a GEO downlink at 20 GHz and 99.9 % matches the script to 1e-6 dB on path loss, received power, margin,
   latency and link margin.
5. Format: a version-1 chain saves byte for byte, `Model=38901` writes version 3, and version 4 is refused.

There are also tests for the distance sweep against single budgets and the zero-margin crossing, for the CLI's text and
`.npy`, and for the window's model picker (rows rebuilt, values kept, one undo step).
`ChainLinkBudgetTests`' "newer version" assertion now names `CurrentFormatVersion + 1`.

**What changed from the brief, and why.**
- **Rayleigh at 99 % is 19.978 dB.** That is the note's 20.0 to one decimal, but it is 0.022 dB from 20.00, so the
  "to 0.01 dB" tolerance holds against the formula, not against the rounded figure.
- **Rain is in the received power, not a second margin.** The registry adds it at the availability. Subtracting it
  again would count it twice. The margins block reports it.
- **The modulation "from the chain's signal"** is the Demo bench's 64-QAM. Blank means that.
- **The Blocks glyph is a card in the Blocks tab.** The bench drawing in the Schematic tab is unchanged. The clipboard
  picture of the lineup (`ChainBlocksGraphicExport`) does not draw the card yet.

Findings in `src/Design/RESOLVED.md`.

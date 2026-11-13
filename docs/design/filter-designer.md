# circuitRF — Filter Designer

**Status:** Built · **Date:** 2026-11-13

The Filter Designer is a tool window (Tools ▸ Filter Designer) and a `filter` verb. It synthesises
a doubly terminated LC ladder from the ideal Filter's own prototype and draws the response with the
Data Display's plots. The ideal Filter stays a transfer function.

## What is reused

| Need | Reused |
|---|---|
| Prototype polynomials | `FilterPrototype` / `FilterNetwork`, including the new Gaussian family and the bandstop map |
| Continued fraction | `MatchPrototypes.Extract` |
| Preferred numbers | `PreferredValues.Snap`, ratio distance, the same spans the Smith chart ships |
| Real parts | `VendorLibraryIndex` plus a ratio-nearest pick in `FilterValues` |
| Response plots | `PlotControl` on a `DataDisplayViewModel`, group delay from `RFNetwork.GroupDelay` via the Match Designer's trace helper |
| Ladder check | `SParameterEngine` on the ladder's own `.cnl` |

## The mathematics

All-pole families (Butterworth, Chebyshev, Bessel, Gaussian) yield a g-vector by extracting the
input impedance `(E ± αF)` of the prototype. Butterworth's g-values are checked against
`g_k = 2 sin((2k−1)π / 2n)`, not against the extractor.

Denormalisation, with `R` the source and `ω` the cutoff:

- Lowpass: series `L = g R / ω`, shunt `C = g / (R ω)`.
- Highpass: each inductor and capacitor exchanged, `C = 1/(g R ω)`, `L = R/(g ω)`.
- Bandpass: each arm a resonator at `ω0 = √(ω1 ω2)`, bandwidth `ω2 − ω1`.
- Bandstop: `s → BW·s / (s² + ω0²)`. A series arm becomes a parallel LC in the through path; a
  shunt arm becomes a series LC to ground. `F1` and `F2` are the stopband edges. Order 3 is six
  elements.

The extracted remainder is a resistance after an even number of removals and a conductance after
an odd number. The load the ladder prints is that ratio times `R`.

Gaussian is the partial exponential `Σ α^k/k! · Ω^{2k}`, with `α` chosen so the sum at `Ω = 1` is
2. `E` is the Hurwitz factor scaled to `E(0) = 1`, so `|S21| = 1/|E|` and the cutoff is −3.01 dB
at every order. `F` is the Feldtkeller step Bessel already uses.

Inverse Chebyshev and elliptic put zeros on the axis. Each finite zero is removed as a tank — a
parallel LC in the through path, or a series LC to ground — after a partial removal at infinity
when that zero is not yet a pole of the open-circuit reactance. An even order of either family
keeps a finite floor at infinite frequency. A ladder of inductors and capacitors is an open there,
so that order is refused and the window says why. The ideal block still has the response.

`NetworkForm.Bandstop` is appended, so existing Match payloads keep their meaning. Match refuses
the value by name.

## The window and the verb

`FilterDesignerViewModel.Design` is the rebuild. It evaluates `FilterNetwork` for the ideal curve,
builds the ladder, snaps it, elaborates the `.cnl` and runs `SParameterEngine`. Both curves go on
one magnitude plot; phase and group delay go on the second.

`circuitrf filter` parses the specification and calls `FilterLadder.Synthesize` and
`FilterValues.Snap`. It writes no second synthesis. Exit 0 with a ladder, 1 when the family has
none, 2 when an argument does not parse.

Apply to Filter writes the specification onto a placed Filter through one `SetParametersCommand`.
The snap stays in the netlist, not on the block.

## Documentation

User page `docs/user/src/reference/filter-designer.md`, registered in `_nav.txt` immediately after
Match. The Filter entries in `components.md`, `system-components.md` and `dynamic-symbols.md` name
bandstop and point at the designer. The parameter descriptions live on the registry, which is what
the parameter editor and `{{table: components/Filter}}` both read.

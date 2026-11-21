# circuitRF — Filter Designer

**Status:** Built · **Date:** 2026-11-13 · elliptic ladder rebuilt 2026-11-21 · placing and the tile (FD-P) 2026-11-21

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
  shunt arm becomes a series LC to ground. `F1` and `F2` are the passband edges either side of the rejected band (inverse Chebyshev:
  the Astop edges); the Designer's minimum order maps Fs1/Fs2 inward through the same transform. Order 3 is six
  elements.

The extracted remainder is a resistance after an even number of removals and a conductance after
an odd number. The load the ladder prints is that ratio times `R`.

Gaussian is the partial exponential `Σ α^k/k! · Ω^{2k}`, with `α` chosen so the sum at `Ω = 1` is
2. `E` is the Hurwitz factor scaled to `E(0) = 1`, so `|S21| = 1/|E|` and the cutoff is −3.01 dB
at every order. `F` is the Feldtkeller step Bessel already uses.

Inverse Chebyshev and elliptic put zeros on the axis, and are held as ROOTS (`FilterRoots`), not
expanded polynomials. Elliptic zeros and poles are closed form (`EllipticPrototype`): the
selectivity modulus from the nome — whichever of `q`, `q'` is smaller, so `k'` is never `√(1−k²)` of
a `k` near 1 — reflection zeros `cd(u_i K, k)`, transmission zeros `1/(k ζ_i)`, poles
`j·cd((u_i − j v0) K, k)` with complex-argument Jacobi functions by the addition formulas, and
`v0` from Carlson's `R_F`. Each pole is then polished by Newton on `D² + ε²c² x^r N²` evaluated as
products, in double and then in double-double (`Dd`, `DdComplex`). The ideal block evaluates
products too, so its response exists at every order the ladder does.

**Types b and c** (even order) are a Möbius map in `x = Ω²` that keeps the passband edge, sends the
largest transmission zero to infinity, and (c) the smallest reflection zero to DC. Every zero and
pole maps in closed form. b's load ratio solves `4r/(1+r)² = 1/(1+ε²)`; c's is 1. Type a at even
order has no ladder and is refused naming b and c. `EllipticType.Auto` is a at odd order, c at
even. The `Filter` block carries `EllipticType` (appended, default `A`).

**The ladder** is one-pass zero shifting on the input function `f = (1+S11)/(1−S11)` EVALUATED AT
POINTS, never divided as polynomials: at each zero `jω_k`, `f(jω_k)` is purely reactive (no power
reaches the load), so the partial element is `Im f/ω_k`; the residue of the next pole is
`1/f'(jω_k)`, from a derivative carried alongside every removal step. The remainder after the last
finite zero is one element and the load (odd), or two and the load (b/c), solved from `f(0)` and one
more point. All of it runs in double-double. Zero order: centre-out (lowest zero in the middle
section), ascending, descending, then every permutation for up to six zeros; the first all-positive
ladder whose S21 matches the prototype to 1e-8 (double-double ABCD chain, a log sweep plus every
reflection zero, the midpoints between them and both sides of each transmission zero) is the
answer. Nothing else is ever returned. The normalised ladder is cached per specification, so a
change of form, frequency or impedance only re-denormalises.

Gate: `testdata/filter-elliptic/` — an independent 120-digit mpmath reference (polynomial roots,
polynomial zero shifting) for 654 ladders, orders 3–21, ripple 0.01–1 dB, Astop 20–80 dB, types a/b/c,
both first elements, plus inverse Chebyshev; every row matches to 1e-8 (worst 1.1e-10) and every
row the reference cannot realise is refused. Orders 22–31 are checked against the ideal block by an
independent nodal solve.

`NetworkForm.Bandstop` is appended, so existing Match payloads keep their meaning. Match refuses
the value by name.

## The window and the verb

`FilterDesignerViewModel.DesignAsync` is the rebuild: it reads the boxes on the UI thread after an
80 ms debounce, evaluates `FilterNetwork` for the ideal curve, builds and snaps the ladder,
elaborates the `.cnl` and runs `SParameterEngine` on the thread pool, and applies the result back on
the UI thread — only the latest request's, a superseded one is cancelled or dropped. Both curves go on
one magnitude plot; phase and group delay go on the second. `FilterOrder.Minimum` is the closed-form
order for Butterworth, Chebyshev, inverse Chebyshev and elliptic against the mask (for elliptic
Auto/b/c, the smallest order whose type as built meets the stop edge); the window can
take that or a typed override. `FilterLadderDrawing` places by ARM — `FilterRealization.Arms` names the elements each arm became —
and reads the arm's own topology: a spine element or chain, a through-path L∥C (C on the spine, L
on a rail above), the three-branch bandpass/bandstop box ((series L–C) ∥ L ∥ C: pair above, L on
the spine, C below), adjacent shunt legs, or a stub column whose parallel sections are legs either
side of it joined by bars. The pane and its Copy wire from one routine,
`MatchLadderLayout.WireSegments`, which only ever fills a rail between its own drops.

`circuitrf filter` parses the specification and calls `FilterLadder.Synthesize` and
`FilterValues.Snap`. It writes no second synthesis. Exit 0 with a ladder, 1 when the family has
none, 2 when an argument does not parse.

Apply to Filter writes the specification onto a placed Filter through one `SetParametersCommand`.
The snap stays in the netlist, not on the block.

## Placing, Synthesize…, and the Designer's settings on the block (FD-P, 2026-11-21)

**One parameter list, two writers.** `FilterDesignerViewModel.AppliedParameters` is the only place
the window turns a design into Filter parameters. Apply to Filter writes it onto the target; Place
component writes it onto a fresh block. A parameter a Filter does not carry yet (placed before it
existed) is added with the registry's own unit and dimension.

**The Designer's settings live on the block, and nothing downstream sees them.** Appended after
`EllipticType`: `Ap` (blank = Ripple), the six mask edges `Fp`, `Fs`, `Fp1`, `Fs1`, `Fs2`, `Fp2`
(the other form's edges are written blank), `OrderMode`, `First`, `Values`, `LSeries`, `CSeries`
and `Parts` (`element=library: series|part; …`). `ComponentTypeRegistry.IsFilterDesignerParameter`
names them and `NetExtractor` drops them, so the netlist line is identical with or without them and a
blank edge never reaches the `.cnl` as `Fs=` — the empty-value trap. The edges are written in the
window's frequency unit and the unit rides on the parameter, so Seed reopens in that unit.
`SeedFrom` puts the stored edges in their boxes first, then the block's own cutoff over the edge it
names, so an edit to `F1` in Properties still wins; each missing setting falls back to what Seed
derived before (Ap from Ripple, edges from the cutoff, Specify for the order). A library series is
found again by `FilterLibrarySeries.Key` after loading the workspace's libraries; one that is not
there is said in the status line.

**Placement is the paste ghost.** `SchematicViewModel.ArmFragmentPlacement` hands a fragment to
the private paste path (renaming, grid snap, nudge, one undo step, Escape) and raises
`PlacementActivationRequested`, which `SchematicDocument` turns into `IHierarchyHost.BringToFront`.
It arms at the pointer if it is over the canvas, otherwise at the view centre. Place filter's
fragment is `MatchSchematicCopy.Build` without its two `TermG`s, plus a stub and an anchored net
label at each end (`FilterPlacement.LadderFragment`; `IN`/`OUT`, numbered when taken). A standalone
window places into the workspace's last active schematic, read when the button is pressed.

**Replace this Filter joins by name.** The ladder is wider than the block, so drawing it where the
block was would lay its spine over the wires that ran into the block and short a series element.
`FilterReplaceCommand` removes the block, puts a free-standing label on each pin (the net's existing
label name when it has one), and pastes the ladder below with its ends on the same names: one undo
step, and undo reinserts the block at its own index so the sheet saves byte for byte.

**Synthesize…** is the Properties button (renamed) and a schematic context-menu item shown on a
Filter only (`SchematicViewModel.SynthesizeTarget`). Both call `FilterDesignerWindow.Show`, which
keeps one window per Filter (`FilterDesignerWindows<T>`) and brings an open one forward.

**The tile.** `Form` offers Bandstop and the glyph strikes the middle wave; a test holds that a
`SetParametersCommand` to Bandstop redraws it. `EllipticType`, `OrderMode`, `First` and `Values` are
pickers. Rows the `Response` or `Form` does not read are greyed with a reason
(`ParameterEditorViewModel.ConfigureFilterRows`, the test pad's mechanism). The docs show the four
forms (`filter-form-glyphs`).

## Documentation

User page `docs/user/src/reference/filter-designer.md`, registered in `_nav.txt` immediately after
Match. The Filter entries in `components.md`, `system-components.md` and `dynamic-symbols.md` name
bandstop and point at the designer. The parameter descriptions live on the registry, which is what
the parameter editor and `{{table: components/Filter}}` both read.

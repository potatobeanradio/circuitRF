# circuitRF — LC Filter Designer

**Status:** Built · **Date:** 2026-11-13 · elliptic ladder rebuilt 2026-11-21 · placing and the tile (FD-P) 2026-11-21

The LC Filter Designer is a tool window (Tools ▸ LC Filter Designer) and the `filter lc` verb. It synthesises
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

`LcFilterDesignerViewModel.DesignAsync` is the rebuild: it reads the boxes on the UI thread after an
80 ms debounce, evaluates `FilterNetwork` for the ideal curve, builds and snaps the ladder,
elaborates the `.cnl` and runs `SParameterEngine` on the thread pool, and applies the result back on
the UI thread — only the latest request's, a superseded one is cancelled or dropped. Both curves go on
one magnitude plot; phase and group delay go on the second. `FilterOrder.Minimum` is the closed-form
order for Butterworth, Chebyshev, inverse Chebyshev and elliptic against the mask (for elliptic
Auto/b/c, the smallest order whose type as built meets the stop edge). It is only where the
search starts (see "The mask guarantee" below); the window can take that or a typed override. `FilterLadderDrawing` places by ARM — `FilterRealization.Arms` names the elements each arm became —
and reads the arm's own topology: a spine element or chain, a through-path L∥C (C on the spine, L
on a rail above), the three-branch bandpass/bandstop box ((series L–C) ∥ L ∥ C: pair above, L on
the spine, C below), adjacent shunt legs, or a stub column whose parallel sections are legs either
side of it joined by bars. The pane and its Copy wire from one routine,
`MatchLadderLayout.WireSegments`, which only ever fills a rail between its own drops.

`circuitrf filter lc` parses the specification and calls `FilterLadder.Synthesize` and
`FilterValues.Snap`. It writes no second synthesis. Exit 0 with a ladder, 1 when the family has
none, 2 when an argument does not parse.

Apply to Filter writes the specification onto a placed Filter through one `SetParametersCommand`.
The snap is not written onto the block; with Values other than Exact the block simulates as the
snapped ladder (see Vendor library below).

## Placing, Synthesise…, and the Designer's settings on the block (FD-P, 2026-11-21)

**One parameter list, two writers.** `LcFilterDesignerViewModel.AppliedParameters` is the only place
the window turns a design into Filter parameters. Apply to Filter writes it onto the target; Place
component writes it onto a fresh block. A parameter a Filter does not carry yet (placed before it
existed) is added with the registry's own unit and dimension.

**The Designer's settings live on the block, and nothing downstream sees them.** Appended after
`EllipticType`: `Ap` (blank = Ripple), the six mask edges `Fp`, `Fs`, `Fp1`, `Fs1`, `Fs2`, `Fp2`
(the other form's edges are written blank), `OrderMode`, `First`, `Values`, `LSeries`, `CSeries`
and `Parts` (`element=library: series|part; …`). `ComponentTypeRegistry.IsLcFilterDesignerParameter`
names them and `NetExtractor` drops them, so the netlist line is identical with or without them and a
blank edge never reaches the `.cnl` as `Fs=` — the empty-value trap. The edges are written in the
window's frequency unit and the unit rides on the parameter, so Seed reopens in that unit. The one
exception (2026-11-23): a locked block whose `Values` is not Exact is emitted as its ladder, read from
`Values`, `First`, `LSeries`, `CSeries` and `Parts` (see Vendor library below); the `Filter` line itself,
for Exact, is unchanged.
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

**Synthesise…** is the Properties button (renamed) and a schematic context-menu item shown on a
Filter only (`SchematicViewModel.SynthesizeTarget`). Both call `LcFilterDesignerWindow.Show`, which
keeps one window per Filter (`LcFilterDesignerWindows<T>`) and brings an open one forward.

**A designed Filter is locked** (owner decision, 2026-11-21). `FilterPlacement.IsDesigned` is true
when `Ap` or a mask edge is non-blank, which only Apply / Place component write. `IsLocked` is that
and `Synthesis` not `Off`: such a Filter's rows are all read-only and greyed except `IL` (the
Designer does not write it), pickers included, and the schematic's inline label editor refuses the
same parameters (`IsLockedParameter`, checked in `BeginInlineEditForHit`), so a change goes through
Synthesise…. A Filter that is not locked hides the Designer rows.

**Use Synthesis unlocks it** (2026-11-22). A checkbox beside Synthesise… in Properties,
`ParameterEditorViewModel.FilterUseSynthesis`, backed by `Synthesis` (appended, blank, dropped from the
netlist with the other settings). Unticking writes `Off` as one undo step: the block is a plain
Filter (every row editable, so a variable or a sweep can drive it) and keeps the design for
Synthesise… to reopen on. Ticking writes nothing and opens the Designer; Apply writes `On`. Locking on
the tick alone would hold values that may have been edited away from the design, which is the drift
the lock exists to prevent. Blank on a designed block reads as On, which is what a block applied before
the setting existed meant. Rows follow the
window's card order (`ParameterEditorViewModel.FilterRowOrder`), and the same ordered list is what
the editor's rebuild check compares, so a reorder does not rebuild on every edit.

**The window's actions are a toolbar** (Design as a word, then Apply, Place filter, Place component and the
Replace toggle as glyphs). The netlist is `circuitrf filter lc -o`, not a toolbar button. The mask-label toggle is gone; the
labels are always drawn. The Ripple box is disabled unless the response is Chebyshev or elliptic.

**The tile.** `Form` offers Bandstop and the glyph strikes the middle wave; a test holds that a
`SetParametersCommand` to Bandstop redraws it. `EllipticType`, `OrderMode`, `First` and `Values` are
pickers. Rows the `Response` or `Form` does not read are greyed with a reason
(`ParameterEditorViewModel.ConfigureFilterRows`, the test pad's mechanism). The docs show the four
forms (`filter-form-glyphs`).

## Documentation

User page `docs/user/src/reference/lc-filter-designer.md`, registered in `_nav.txt` immediately after
Match. The Filter entries in `components.md`, `system-components.md` and `dynamic-symbols.md` name
bandstop and point at the designer. The parameter descriptions live on the registry, which is what
the parameter editor and `{{table: components/Filter}}` both read.

## Vendor library, placed and simulated (2026-11-23)

**Vendor library is a Values entry** (`FilterSeriesKind.VendorLibrary`, appended so a stored `Values`
keeps its meaning). Choosing it indexes the workspace's libraries (`LoadLibraries`, once per window);
the Inductors and Capacitors pickers show only while it is chosen (`ShowLibraryPickers`), and the table
offers per-element parts only then. There is no Load button. An IEC series ignores the library picks,
and Apply writes `LSeries`/`CSeries`/`Parts` blank unless Values is Vendor library. A block applied
before the entry existed (an IEC `Values` beside library names) reopens on Vendor library, which is
what its library picks meant.

**One value rule.** `FilterValues.Choose` is what the table, a placed ladder and a simulated block
build each element with: the hand-picked part, else the nearest part of the element's series, else the
exact value. `FilterPart.Vendor` carries the `VendorPartRef` so the part can be placed and simulated.

**Place filter / Replace this Filter place the parts.** `FilterPlacement.LadderFragment` takes the
element-to-part map and turns each generic L or C into a SpiceModel carrying the part's `File`, `Name`
and `Glyph` (`VendorPartPlacement.Parameters`) and its case-size footprint — what a palette drop of
the part builds. The glyph is the built-in L or C, so the pins land where the generic symbol's did.

**A designed block simulates as its ladder when Values is not Exact** (`FilterBlockLadder`, in
`src/Design/Filters`). `NetExtractor` emits, for a locked Filter (`Synthesis` not `Off`), one cell
`LcFilter_<instance>` holding the ladder — a vendor part through `EmitSpiceModel`, the path a placed
library part takes, any other element as an ideal L or C — and one instance of it on the block's two
nets. Exact keeps the ideal `Filter` line, which is the exact ladder's response. A ladder that cannot
be built (a non-numeric field, a library part the workspace no longer holds) is a Messages line and
the ideal block. The parts inside the cell are removed from `PlacedVendorParts`, so they offer no
`.Part` tunable of their own. `IL` is not modelled by the ladder.

**Two window fixes.** The mask overlay is set on `MagnitudeContainer.Overlay` as well as on the
control, so Export and Copy (which compose from containers) draw it. `MatchSchematicCanvas.FitOnResize`
(off by default, on in this window) re-fits the ladder whenever the pane changes size.

## The mask guarantee (2026-11-23)

The Designer never returns a filter outside its own mask. `FilterMask` (`src/Core/Systems`) holds the
mask (`FilterMaskSpec`, the four edge boxes in Hz with Ap and As), the check
(`WorstMissDb`: each band sampled log-spaced out to a decade past its outer edge; loss above Ap in a
passband or below As in a stopband is a miss, 1e-6 dB tolerance for the ideal, 1e-3 dB for a ladder
netlist whose values are written to finite digits) and the search, `FilterMask.Find`:

- Orders from the closed-form minimum (computed at Ap, which bounds every ripple at or under it) up
  to 31 in Minimum, or the typed order alone in Specify. Even inverse Chebyshev and even type-a
  elliptic are skipped.
- For inverse Chebyshev and elliptic, design floors As + {0, 1, 2, 3, 5, 7, 10, 15, 20, 30, 40} dB.
  At a shallow floor those ladders can need a negative element (no zero order is positive); a
  deeper floor fixes it. `Astop` on the block is the floor built at, `AsMask` the mask's.
- At each order and floor, the edges as typed first, then `Balance`: the edges widened or narrowed
  about the centre (`ScaleEdges`) by a bisection that equalises the passband's miss with the
  stopband's. That spends the slack Ap leaves over the Ripple (the reported Chebyshev case: built at
  0.1 dB against Ap 3 dB, the formula's order 5 missed) and the slack a deeper floor leaves.
- The ladder must synthesise, and with an E-series or a vendor library the snapped ladder must meet
  the mask as well (`accept`, coarse sample first). A snapped rejection moves to the next order, for
  `SnapOrderWindow` (6) orders past the first exact success, then gives up and says the series is
  too coarse.

A Minimum that finds nothing shows the mask alone and the reason. A Specify design that misses is
drawn with the miss in the status line, and `MaskMet` false blocks Apply, Place filter, Place
component and Replace this Filter. Reopening a block whose mask edges are all stored shows the mask,
not the shifted cutoff.

## The search for a passing design (2026-11-23)

`FilterValueSearch.Run` (`src/Design/Filters`) is what Search under a snapped miss runs. It walks
`FilterMask.Candidates` (the same enumeration `Find` takes its first from: orders, floors, and the
given then balanced edges, lazily) for `OrderWindow` (6) orders past the first, fitting values to at
most `CandidatesPerOrder` (4) designs an order, for each `(inductor series, capacitor series)` pair
it is handed. An IEC series is handed as a `FilterLibrarySeries` too (`FilterValues.IecSeries`,
library name `IEC 60063`, the span `Snap` uses), so both cases are one search.

The fit is coordinate descent over each element's nearest value and `Neighbours` (3) either side:
single elements first, then adjacent pairs when single moves stall. The score is the worst miss
plus a quarter of the mean excess over the grid, because the worst miss alone is flat wherever one
frequency dominates. Responses come from `FilterLadder.InsertionLossDb`, a small nodal solve of the
same circuit `ToCnl` writes, matched to the engine to 1e-6 dB by test, which is what makes thousands
of evaluations affordable. A coarse pass (24 points a band) is confirmed on the full 400-point grid.

The view model pins the result (`Spec.Pinned`): `Compute` builds that exact design instead of
searching, while `SpecSignature()` is unchanged, and the picks are `_partOverrides` for every
element. `FilterValues.Choose` honours an IEC pick when it is from the chosen series, so the block's
`Parts` (`element=IEC 60063: E12 inductors|10nH`) is what `FilterBlockLadder.Build` and `SeedFrom`
read back. A pick from a series other than the chosen Values is never written.

## Library parts as their models (2026-11-23)

With Vendor library, the Designer's ladder netlist is `FilterLadderCircuit.ToCnl` (`src/Design/Filters`):
`FilterLadder.ToCnl` with each element that has a library part written as a `SpiceModel` line naming
it, read back through `NetExtractor.ResolveNetlistSpiceModels` against the workspace — the resolution a
placed part gets. The curve, the 400-point mask check and Apply/Place all use that circuit, so the
window cannot show a pass the placed ladder does not have. A part that is a model folder keeps its
stated value.

The search and the snapped acceptance inside the mask search cannot afford a netlist per candidate.
`FilterLadderCircuit.PartImpedance` measures each part once, in series between two 50 ohm ports
(Z = 2·z0·(1/S21 − 1)), cached per part and frequency set, and `FilterLadder.InsertionLossDb` takes an
optional per-element impedance in place of the ideal L or C. The candidate is judged on 24 points per
band first and 400 only when that passes. Pairings of series past ten are shortlisted by how close
each series' nearest parts are to the ideal impedance at the passband centre
(`FilterValueSearch.Shortlist`).

## The mask as optimizer goals (2026-11-23)

`FilterMaskGoals.For` (`src/Design/Filters`) turns a `FilterMaskSpec` into `OptimizationGoal`s on
`dB(SPn.S(out, in))`: `ge −Ap` over each passband and `le −As` over each stopband, over
`FilterMask.Bands` — the extents `CheckFrequencies` samples, now its single source — cut to the first
S-parameter analysis's sweep. The ports are the sheet's lowest two numbers (1 and 2 when stated). The
Designer's toolbar button hands the mask to `WorkspaceViewModel.SendFilterMaskGoals`, which finds the
workspace window holding the placement schematic, replaces the goals an earlier send made
(`mask_pass…`, `mask_stop…`) in one `SetTuningSetupCommand`, and shows the Optimizer on that bench.

## A ladder that misses, placed for the Optimizer (2026-11-24)

Place filter is gated on `CanPlaceLadder` (a ladder and a schematic), not on the mask; Place component
and Apply keep `CanPlace`, which requires it. The Designer holds the fragment's own part objects
(`Remember`): `SchematicPasteCommand` takes the fragment's objects over and renames them in place, so
the references give the names they were placed under. Send mask to Optimizer passes
`FilterMaskGoals.LadderTunables` of those still on the sheet — `Name.L`/`Name.C` with `opt`, a ×½..×2
log range and the E-series as `Discrete`, or `Name.Part` (kind `same`) for a library part — and
`SendFilterMaskGoals` adds them only when the sheet is the bench's top frame, since a key inside a cell
needs its cell's prefix.

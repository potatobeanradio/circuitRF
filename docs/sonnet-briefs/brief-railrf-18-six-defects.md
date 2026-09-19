# Brief 18 — the six defects documenting it found, and none of them reported a failure

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail18-n` · **Phase:** corrective, across P0–P2b
**Area:** `src/Design/Layout/Pdn/`, `src/Design/RailRf/`, `src/Render/Renderers/`, `src/Ui/RailRf/`
**Depends on:** 1–17 · **Blocks:** nothing
**Found by:** [brief 17](brief-railrf-17-docs-and-example.md), recorded in `src/Design/RESOLVED.md`,
`src/Render/RESOLVED.md` and `src/Ui/RESOLVED.md` under *railRF brief 17*

---

## 0. What this brief delivers

Six fixes. Every one of them was found by **driving the feature as a user** — writing the chapter and
authoring the `Power Rail` example — rather than by a test, and that is the point worth holding on to:
**the suite is green on all six today.** Each produces a complete, plausible answer with nothing
reporting a failure, which is the exact shape this series' own standing rules were written against.

| | Defect | Where | Visible as |
|---|---|---|---|
| `R-rail18-1` | the rail's minimum feature width reads **zero** on any multilayer board | `PdnMeshExtractor` | *"the rail's narrowest copper, 0 mm"*, and `MaxCells` silently choosing the mesh |
| `R-rail18-2` | the Fast model reports **no plane capacitance** and blames the user's stackup | `PdnGraphExtractor`, `RailDcResult` | a false sentence about the stackup, on the default model, on every board |
| `R-rail18-3` | the class map paints the **reference over the rail** | `RailMapScene` | one flat rectangle on any board with a plane |
| `R-rail18-4` | the map legend's three labels **overlap** on a small canvas | `RailMapRenderer` | unreadable text in the map and in every exported picture |
| `R-rail18-5` | the parts table fills from the **BOM alone** | `RailRfViewModel.RebuildParts` | an empty Parts pane on a document whose parts drive the whole curve |
| `R-rail18-6` | the `DC` / `frequency` strip **sets nothing** | `RailRfWindow` | a tab that lights a button and changes no content |

**Each one ships with a test that fails at HEAD.** A corrective brief whose gate passes before the fix
has not gated the fix — it has gated something adjacent to it. Write the test first, watch it go red,
then fix.

---

## 1. `R-rail18-1` — a pooled area measurement cannot use a summed total

### What is wrong

`PdnMeshExtractor.MinimumFeatureWidthDbu(IReadOnlyList<PdnRegion>)` pools every layer's copper into one
`Paths64` and hands it to the `Paths64` overload, which measures by morphological opening. That
overload's `LosesArea` compares

```
Math.Abs(Clipper.Area(opened))  <  total * 0.999
```

where `opened` is the **union's** area and `total = Math.Abs(Clipper.Area(all))` is the **sum of the
per-path areas**. Wherever two pooled paths overlap, the sum exceeds the union, the comparison is
unsatisfiable at every width, and the bisection bottoms out on its floor of 1 DBU.

**A rail on more than one layer crosses itself at every via**, so this is the ordinary case, not a
corner. Measured on the `Power Rail` example:

| pooled set | measured |
|---|---|
| one 5 mm square | 4.92 mm |
| **the same square, listed twice** | **0 mm** |
| the example's TOP copper alone | 0.346 mm |
| the example's BOT copper alone | 0.199 mm |
| **TOP and BOT pooled** | **0 mm** |

### Why it matters

`baseDeltaDbu` becomes `max(1, 0/3) = 1 DBU`, so `PdnGrid.Build`'s `MaxCells` coarsening decides the
accurate mesh instead of R-rail3-14's *"three cells across the narrowest copper"*. The answer is not
wrong — the cap produces a workable mesh, and the example's accurate run agrees with its fast one to
1.6 % — but **the stated rule is not the rule that ran**, and the provenance says so in words nobody
reads as a fault: *"the rail's narrowest copper, 0 mm, at 3 cells across it"*, `CellSizeMetres = 1e-9`.

### `R-rail18-1a` — the fix is per layer, not a bigger union

The quantity wanted is *"the narrowest copper this rail has anywhere"*. Copper on two drawing layers is
not one 2-D shape and must not be measured as one — unioning the pooled set would make a trace crossing
a plane read as plane-wide. **Measure each layer's own unioned copper and take the minimum across
layers.**

### `R-rail18-1b` — the `Paths64` overload states its precondition and enforces it

Its two other callers (`PdnCopperClassifier`, `PdnGraphExtractor`) each pass ONE region's already-unioned
copper, which is why neither has ever been wrong. That is a precondition the signature does not state.
Either union defensively inside it, or assert non-overlap — **decide, and write down which**, because a
future caller that pools will otherwise reproduce this defect exactly.

### Gate

`tests/Ui.Tests/RailRf/PdnMeshExtractorTests.cs`:

- The two-line oracle: one square measures its own width; **the same square listed twice measures the
  same width**, not zero. This is the whole defect in two lines and it is the test that must go red
  first.
- On a two-layer rail that crosses itself, the measurement is the **narrower layer's** width.
- End to end: an accurate run on the `Power Rail` example reports a `CellSizeBasis` naming a real
  width, and `CellSizeMetres` equal to that width over `CellsAcrossMinimumFeature` — **not** 1e-9.

---

## 2. `R-rail18-2` — a check that exists to catch a wrong stackup tells everyone theirs is wrong

### What is wrong

`RailDcResult.PlaneCapacitanceLine` is there because §2.4 calls plane capacitance the number *"a
designer recognises a wrong one instantly and would never notice buried in a curve"*, and it is printed
even on a DC run — where nothing was stamped from it — precisely so the stackup gets checked.

`PdnMeshExtractor` fills `PlaneSeparationMetres`, `PlaneCapacitanceFarads` and
`PlaneOverlapSquareMetres`. **`PdnGraphExtractor` fills none of them.** So on the DEFAULT model, on
every board, the line reads:

> Plane capacitance: none. The stackup states no dielectric between this rail's copper and its
> reference, so ε₀εᵣA/h has no h — state the dielectric entries between them.

which is a statement about the user's stackup and is untrue of any stackup that states one. The
`Power Rail` example's states three; the same board through Accuracy reports **1.03 pF over 1.12 cm² at
εr 4.3**.

### `R-rail18-2a` — the fast model computes the same three numbers

It has everything it needs and no mesh to get it from. `PdnGraphExtractor` already holds `railCopper`
and `refCopper` per layer; the overlap is **one Clipper intersection per (rail layer, reference layer)
pair**, and `h` and the medium come from `PdnStackupGeometry` / `PdnCavity.MediumBetween` exactly as
they do on the mesh side. Neither reading may invent its own arithmetic: `PdnCavity.CapacitanceFarads`
is the one expression and both call it.

**The two need not agree to machine precision and must agree to a stated tolerance.** The mesh sums
per-cell overlap on a discretised grid; the graph intersects the polygons exactly. State the tolerance
in the gate rather than discovering it.

### `R-rail18-2b` — "not computed" and "not stated" are different sentences

Even with 2a done, `PlaneCapacitanceLine` may not reach its stackup sentence through `<= 0`: a model
that did not compute the number must say **that**, and never blame the stackup for the model's own
omission. Carry the distinction on the provenance (a nullable, or an explicit basis enum in
`PdnPlatingBasis`' style — **a defaulted number and an uncomputed one are not the same state**), and
keep the original sentence for the case it was written for, which is a stackup that genuinely states no
dielectric between the pair.

### Gate

`tests/Ui.Tests/RailRf/PdnFastExtractorTests.cs`:

- The `Power Rail` stackup in **Fast** reports a plane capacitance, and it agrees with the same board's
  **Accurate** figure inside the tolerance the brief states.
- A stackup with **no** dielectric between rail and reference still gets the original sentence, from
  both models.
- A model that does not compute it produces neither — the sentence names the model, and a source scan
  finds no second spelling of the stackup sentence.

---

## 3. `R-rail18-3` — the tab that exists to make a misclassification visible shows nothing

### What is wrong

`RailMapScene.BuildClass` copies `result.Classification` in order. Both extractors build that list
**rail copper first, reference copper last** — `PdnGraphExtractor` runs one `Classify` pass over
`railCopper`, then one over `refCopper`. `RailMapRenderer.Draw` walks `scene.Regions` in list order, and
every region is **opaque paint**, which is R-rail8-10's own rule so that nothing in the map depends on
the page background.

So the reference is painted last, over everything beneath it. On a board whose reference is a PLANE —
every board this feature is for — the class tab is one flat rectangle in the `Spreading` colour.

### Why it matters more than it looks

§2.9 rule 2 is the safety argument for having a fast model at all:

> A silent misclassification is the one failure mode of this design … **Drawing it is what makes it
> neither.**

The picture that rule depends on is the one that shows nothing on the ordinary case.

### `R-rail18-3a` — order the SCENE, not the extractors

Emit the reference regions first in `BuildClass`. Do not reorder the classification passes: the
extractor's order is a property of how those two passes are written, and the map's is a property of what
a reader needs to see. Coupling them would make a later change to either one silently move the picture.

### Gate

`tests/Ui.Tests/RailRf/RailZMapTests.cs` (or beside it):

- On a board with a plane over a trace, **every `IsReference` region precedes every non-reference one**
  in `scene.Regions`.
- The rendered oracle, on `RailCopyTests`' own terms: the trace's class colour is present in the SVG —
  **paired with the same scene built the old way, asserted to fail there.** A colour-presence assertion
  that passes either way has gated nothing.

---

## 4. `R-rail18-4` — a box in DBU with text in points

`RailMapLegend.Box` is a `Bbox` in **DBU**; `RailMapRenderer.Font` is sized in **screen points**. The
legend carries three strings — the minimum, the caption and the maximum — so once the map is drawn small
enough they overlap. On the `Power Rail` example that begins below roughly a 600 px canvas, which is why
`FigureCatalog`'s railRF rows capture at 1600 rather than the width the panes alone would need.

It is not only a window problem: `RailReportPage` and `RailGraphicExport` draw the same legend, so an
exported `.svg`/`.pdf` of a small map carries the same overlap into a file somebody keeps.

**Three answers and the brief must pick one and say why**: widen the box to the measured text, shrink the
font to fit the box, or drop the caption below a threshold. Measuring the strings is unavoidable in all
three — `SKFont.MeasureText` — so the decision is about what gives way, not about whether to measure.

### Gate

At a ladder of canvas widths spanning the one the example needs, **the three label rectangles do not
intersect**, and the map still carries its minimum and maximum. A gate that only checks the widest case
is the case that already passes.

---

## 5. `R-rail18-5` — the parts table is of the BOM, and should be of the RAIL

### What is wrong

`RailRfViewModel.RebuildParts` returns early unless `Bom is { Refusal: null }`, and its one `Parts.Add`
sits inside a `foreach` over `bom.Rows`. **`RailSpec.Parts` — the document's own part list — is never
read.**

Everything else in the window uses exactly those rows: `BuildSweepRequest` resolves `rail.Parts` through
`RailPartResolver`, so every resonance on the curve, every anti-resonance attribution and every row of
the removal ranking comes out of parts the table does not list. The `Power Rail` example is that
document, and its figures show it: thirteen capacitors driving the answer, and a Parts pane with no rows.

**R-rail11-8 makes this the ordinary P1 case, not a corner** — *"§6 makes P1 artwork-OPTIONAL, so in P1
that number is TYPED"*.

### It is also the two halves disagreeing again

`src/Cli/RESOLVED.md` records review round 2 finding the verb counting bias-curve coverage over the whole
shared library rather than the board's parts, and states the rule it broke: *"a verb disagreeing with the
window about one document"*. The verb was moved onto `RailSpec.Parts`. **The window's table was left on
the BOM**, so the two now disagree in the other direction: `circuitrf rail` on the example prints *"0 of 4
part number(s) modelled from a file, 1 with no bias curve"* while the Parts pane is empty for that file.

### `R-rail18-5a` — the rail's rows are the subject; the BOM enriches them

The table lists **the selected rail's parts**, keyed by refdes, from `RailSpec.Parts`. Where a BOM is
imported it supplies the part number, the placement supplies the position, and the library supplies the
model — each filling a column, none deciding whether a row exists. `RailPart.Origin` already distinguishes
`Typed` from `Bom` and is what the row shows.

**Note what this also corrects:** today's table lists every refdes in the BOM, which is the whole board's
parts rather than this rail's. Nobody has reported it because the pane is usually empty, but a table
headed by the rail selector that lists another rail's decoupling is the same class of defect.

A refdes the BOM names and the rail does not is **not a row** — it is not on this rail. A refdes the rail
names and the BOM does not is a row with an unresolved part number, which is the state
`RailPartRowViewModel.UnresolvedText` already exists to say.

### Gate

`tests/Ui.Tests/RailRf/RailWindowTests.cs`:

- The `Power Rail` document with **no BOM** lists its thirteen parts.
- Import a BOM naming parts on another rail: they do **not** appear.
- **R-rail10-8's rule, asserted directly:** the window's part count and bias-curve coverage for one
  document equal what `circuitrf rail` prints for the same file. That is the assertion that would have
  caught both halves of this.

---

## 6. `R-rail18-6` — a tab strip that sets nothing

`SelectedResultsTab` is an `[ObservableProperty]` whose only reader is `RailRfWindow.SyncTabs`, which
assigns the two `ToggleButton.IsChecked` values. Every card in the results column lives in ONE
`ScrollViewer` and is gated on its own `Has…` property. **Pressing `frequency` lights a button and
changes nothing else.**

Everything is present, so nothing is hidden and nothing is wrong — it simply is not what a tab means, and
a control that looks like it filters and does not is a control a user stops trusting.

### `R-rail18-6a` — partition the cards, or remove the strip

Two honest answers and the brief must choose:

- **Gate each card on the tab.** `DC`: Stackup, Drop, Breakdown, Via check. `frequency`: Against the
  target, Anti-resonances, If this part came off, On an aggressor line, Plane resonances. Each card's
  visibility becomes `Has… && tab == …`, so a card with nothing to say still does not appear.
- **Remove the strip** and let the one list stand, which is what it is today.

**The recommendation is to gate**, because the column is long enough that a reader looking for the mask
verdict scrolls past four DC cards to reach it, and because §11.3 gives the strip as a control rather
than as decoration.

*On an aggressor line* is a **frequency** finding — it is a coincidence between a swept peak and an
aggressor — even though it reads as a DC-side observation on the example today.

### Gate

`tests/Ui.Tests/RailRf/RailWindowTests.cs`: with the tab on `DC` the frequency cards are not visible and
the DC ones are; with it on `frequency`, the reverse. Then **`DocRailFixtures.Impedance` goes back to
selecting the tab** instead of scrolling to a named card, and its comment — which currently explains why
it must scroll — comes out with it.

---

## 7. `R-rail18-7` — the red test already in the tree

`CliStructuredOutputTests.DiagnosticIds_AreTheCommittedSet_UniqueAndCaseDistinct` **fails at HEAD.**
`CliDiagnostics` declares `rail.frequency-flags.not-in-this-phase` — added by review round 2 — and the
committed list does not carry it. Verified with `git show HEAD:` on both files rather than by inference.

One line. It is here because a corrective brief that leaves a red gate red has not finished, and because
brief 1 §5 records this happening once already: *"A stale `ExpectedIds` list was already red before this
change."* Twice is a pattern, so **add the assertion's own fix to the checklist any brief adding a
diagnostic id follows**, the way a row in `DocumentKinds.Classify` already obliges an arm in `check`.

---

## 8. Scope

- **No new features.** Six fixes and a stale list. Nothing here adds a control, a column or a number that
  did not exist.
- **The refdes/placement join is NOT in this brief.** `PdnExtractionRequest.Pads` is filled by nothing in
  the application, so every port is coordinate-anchored and **a rail chain is unsolvable** — recorded in
  `src/Cli/RESOLVED.md` and in `railrf.md` §6.1. It is larger than all six of these together, it changes
  what a document can express, and bundling it would make this brief unshippable. **Its own brief.**
- **No re-tuning of the `Power Rail` example.** `R-rail18-1` changes the accurate mesh on it and
  `R-rail18-2` adds a line to its report. If a number in `examples/Power Rail/README.md` or in
  `docs/user/src/reference/railrf.md` moves, **update the document rather than the example** — the
  example was authored so both targets are just met, and re-tuning it to preserve a printed number would
  lose the property it was built for. Regenerate the figures and classify the churn per
  `R-rail17-7` before reporting.
- **`R-rail18-3` and `R-rail18-4` change pictures**, so both must be checked with a faithful rasteriser
  before they are called done — not with a thumbnailer, which crops.

**On completion:** record findings in the `RESOLVED.md` beside whatever was touched, and strike each
defect from brief 17's entries there rather than leaving two records of one thing. Never in a CLAUDE.md.

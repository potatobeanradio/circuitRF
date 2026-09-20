# Brief — railRF: power integrity on real board shapes, from DC up — the series

**Status:** unstarted, briefs 1-17 · **Date:** 2026-09-18 · **Design note:** [`docs/design/railrf.md`](../design/railrf.md) rev 4, approved
**Area:** `src/Design/RailRf/`, `src/Design/Layout/Pdn/`, `src/Design/Layout/Interchange/`,
`src/Engine/Pdn/`, `src/Render/`, `src/Ui/RailRf/`, `src/Cli/`
**Requirement tag for the series:** `R-rail<n>-<m>`, scoped per brief (`R-rail3-5` is brief 3's fifth)

---

## 0. The short answer

railRF takes artwork that already exists — Gerbers with a drill and a board netlist, or a `.kicad_pcb` —
plus a stackup, a parts list, load currents and a target, and answers three questions no tool on a board
designer's desk answers together: **where does the supply voltage go on the way to the load, what
impedance does the load actually see, and which capacitors are earning their place.** Then it does the
same to a second board and shows the difference.

The design note is the specification and this series does not restate it. What this overview does is
**fix the boundaries between the briefs**, and record the handful of things that came out of reading the
code rather than out of the note.

### The one rule the whole series is built on

> **The extraction produces an ELABORATED NETLIST. It never produces a second simulator.**

`src/Design/Layout/Pdn/` turns copper into a `CircuitRF.Core.Elaboration.ElaboratedNetlist` — ordinary
`ResistorModel`, `InductorModel`, `SeriesRlcModel`, `CapacitorModel`, `SnpModel`, `PortModel` instances
on ordinary nodes. Everything downstream is already built: `MnaSystem` and CSparse solve it,
`SParameterEngine` sweeps it, the `DataSet`/`DataCube` model carries the answer, the Data Display plots
it, `.npy`/MATLAB/Touchstone export it, and `circuitrf` runs it headlessly.

That is not tidiness either. It is what makes the two speeds of §2.9 safe to have — **Fast and Accurate
are two readings of the same geometry into the same currency, not two simulators** — and it is what lets
a PDN model be *inspected* as a netlist. Write a second solve path and the DC answer and the AC answer
drift apart silently, which is precisely the failure §2.8 exists to prevent.

The corollary is a scope rule with teeth, and brief 3 gates it with a comment-stripped source scan:
**nothing under `src/Design/Layout/Pdn/` or `src/Engine/Pdn/` builds a matrix, factorises anything, or
owns a result type.**

---

## 1. Seven things that are not obvious, resolved here once

These came out of reading the code before writing the briefs. Each is restated where it bites.

### 1a. `src/Engine` cannot see `src/Design`, and §5 of the note is fine anyway

The reference graph is `Core → Engine → Design → Render → Ui`, and `Cli` sits beside `Ui` on
`Core/Engine/Design/Render`. So **`src/Engine/Pdn/` may not name a `Technology`, a `LayoutView`, a
`RailDocument` or anything else from `src/Design`.** Everything the note places there — the mode
eigensolve, the mask arithmetic, the removal ranking, the coincidence check, the A/B diff — takes numeric
arrays and a `DataSet` and returns a `DataSet`, which is what the note's own "no domain types" already
says. Anything that needs a `Technology` lives in `src/Design/Layout/Pdn/` instead.

The one place this bites is the mode solve (brief 15): it needs the mesh's own adjacency, not the
stackup, so it takes the adjacency the extractor already built and never the geometry it came from.

### 1b. The galvanic connectivity walk is already written, and it is `internal` to `src/Design`

`src/Design/Layout/Drc/DrcConnectivity.cs` partitions flat per-layer geometry into electrically-joined
pieces, **bridging layers through the stackup's own via spans** (`StackupLayer.SpanFromLayer` /
`SpanToLayer`). That is exactly §2.8's "click any copper and everything galvanically joined to it
highlights", and exactly what brief 3's extractor needs to find a rail's region set.

It is `internal`, which is not an obstacle: `src/Design/Layout/Pdn/` is the same assembly. **Do not copy
it, do not make it public, and do not write a second walk.** A rail whose island structure the DRC and
railRF disagree about is a bug neither of them reports.

What it does *not* do is name a net. Net identity comes from the board netlist or the `.kicad_pcb`
(brief 2); `DrcConnectivity` answers only "which shapes are joined to which".

### 1c. The clipboard path is entirely written, and the failure mode is an omission

`PlotExporter.SetClipboardDataAsync` already performs the one multi-format P/Invoke session Windows
requires, `LayoutClipboard` already frames the page from the **painted** extent and already reads
`LayerDef.Visible`, `SvgFontNormalizer` already repairs every emitted SVG. Brief 9 writes none of it.

The one thing it does write is an entry in an **explicit overlay parameter list** — the EM mesh, the
current-density map and the DRC markers were each added to that list by hand as they arrived. **An
overlay nobody added is silently absent from the copy**, the picture is still produced and still looks
right, and nothing reports a failure. That is why brief 9's gate asserts railRF's overlay in the real
SVG text rather than trusting the wiring.

### 1d. The board view is `LayoutCanvas` itself, and the seam it hangs on is real and documented

`ILayoutCanvasOverlay` (`src/Ui/Controls/ILayoutCanvasOverlay.cs`) exists, wBond implements it, and its
own doc comment already records four of the five traps §11.6 lists — the held-key latch that survives a
`LostFocus`, `ContentBounds()` and Zoom to Fit, the DBU coordinate boundary, and the rule that an overlay
never touches the layout model or the path cache. **railRF adds an implementation and no navigation code
at all** (brief 8).

The fifth trap is railRF's own and is worse here than it was in wBond: **this window's whole left column
is editable rows**, so the keyboard gate has to be wider, not narrower. Brief 8 `R-rail8-4`.

### 1e. `.crail` is a document type, and a document type has eight registration points

`.charm` (harmonicaRF) and `.wbond` are the precedent. A new extension has to appear in
`App.axaml.cs`'s dispatcher, `WorkspaceViewModel`'s open routing, `WorkspaceScanner`'s `NodeKind` map,
the macOS `Info.plist` files, the WiX `.wxs`, the Linux mime file, and `src/Cli/DocumentKinds.cs`.
**Three parity tests already hold those lists shut against each other** — a type declared to the
operating system with no case in the dispatcher launches circuitRF and opens nothing, which reads to a
user as a broken file. Brief 1 does all eight in one change and runs those tests.

### 1f. The components railRF needs all exist, and one of them is the answer to "how do I stamp a mesh"

`ResistorModel`, `InductorModel`, `CapacitorModel`, `SeriesRlcModel`, `ParallelRlcModel`, `SnpModel`,
`BeadModel`, `PortModel`. A mesh cell edge is a `SeriesRlc`; a cell's shunt is a capacitor with a
conductance across it; a vendor part with its own `.sNp` is a `SnpModel`; a protection FET's on-resistance
at DC is a resistor. **No new `ComponentModel` is needed anywhere in this series**, which is a scope
statement as much as a convenience: a new device type would need a golden-reference test and a factory
registration, and railRF needs neither.

### 1g. Four questions in the design note are still open, and none of them blocks a brief

They block **gates**, not work. Each brief that touches one states its fallback here and its refusal in
the brief:

| Open | What it blocks | What the brief does until it closes |
|---|---|---|
| **Q-18** — how much of the reference package can we have | The importer gates in §7 — "real bytes, not invented ones" | **DEFERRED TO MANUAL TESTING** (owner, 2026-09-18). Briefs 2 and 3 build against **synthetic fixtures of the right shape** and carry a `FixtureFact`-guarded second gate that runs when real files land. The synthetic ones are committed; real ones arrive anonymised. **The FIXTURES defer safely; the FORMAT SHAPE does not** — see §1h. |
| **Q-20** — what a regulator needs typed | The rail-chain finding, not the chain | Brief 1 carries `InputCurrent` and `MinimumInputVoltage` as typed per-regulator fields, both nullable. Brief 5 reports the input rail's drop always, and reports *that the drop broke the rail downstream* only where the minimum is stated — **never a defaulted one**. |
| **Q-21** — which netlist flavour the example is | Nothing, if it is IPC-D-356 | `BoardNetlistFile` reads IPC-D-356 today. Brief 2 changes nothing about it; if the example turns out to be another flavour that is a **fourth reader and a brief of its own**, and it is much cheaper to know before brief 2 starts than at its gate. |
| **Q-22** — is the plating thickness stated anywhere | The via current limit's accuracy, not the flag | Brief 6 reads it from the stackup's via entry where one states it and otherwise takes it as a **typed setting shown on every flag**, with the drill-size table as a sanity band beside it. Every flag says which basis produced it. |

The other three have honest fallbacks. Q-18's fallback is a reader gated against bytes we wrote ourselves,
which proves only that **the reader parses its own output** — so it gets a section of its own below.

### 1h. Q-18 is two asks, and only one of them defers safely

**The owner's decision is that the reference package arrives during manual testing, after the briefs are
implemented** (2026-09-18). That is workable, and this section is what makes it workable rather than a
gamble. Q-18 bundles two different things:

| | What it is | Deferring it |
|---|---|---|
| **(a) The fixtures** | A complete package to gate against and commit anonymised | **Defers cleanly.** Brief 2 `R-rail2-13`'s guarded second gate is exactly the mechanism, and `RfCore.Tests`' proprietary loadpull fixtures already prove the pattern works on a fresh clone. |
| **(b) The format SHAPE** | What a real placement file, BOM and netlist actually *look like* — column names, header form, units, whether there is a header at all | **Does not defer cheaply.** A shape surprise is not a bug fix; it is a change to `BomRow`, to the refdes join, or to recognition — and those propagate into brief 3, brief 7's parts table and brief 16's matcher. |

**The cheap way to close (b) without closing (a): the first ~20 lines of one file of each kind.** Not a
package, not anonymised, not committed — just enough text to see the header, the delimiter, the units and
the column names. That is minutes of the owner's time and it removes almost all of the late-surprise risk.

**If even that is not available before brief 2 starts, brief 2 is built to absorb the surprise instead** —
`R-rail2-14` lists the four shape assumptions that must not be baked in. Absorbing it costs a little more
code up front and turns a redesign into an adjustment.

**Q-21 rides on the same decision.** It asks which netlist flavour the example is, and the answer is a line
of text from the file's head. If it is not IPC-D-356 that is a fourth reader and a brief of its own — a
schedule fact, not a design one, and one worth knowing before P0's gate rather than at it.

---

## 2. The briefs

| Brief | Phase | What it delivers | Gate |
|---|---|---|---|
| [1 — the document](brief-railrf-1-document.md) | P0 | `RailDocument`, the rail set, sources/loads as pad anchors, targets, band, aggressors, `.crail` and its eight registration points. No solve, no UI. | `tests/Ui.Tests/RailRf/RailDocumentTests.cs`, the three document-type parity tests |
| [2 — the companion readers](brief-railrf-2-companion-readers.md) | P0 | Placement (pick-and-place), BOM, and the part library. The origin refusal, the description parse, `L = 1/((2πf₀)²C)`. | `RailReaderTests.cs` |
| [3 — the netlist contract and the accurate DC extractor](brief-railrf-3-mesh-extractor.md) | P0 | `PdnNetlist`, the resistive mesh over real copper, the rail region walk, the one-write-path scan. | `PdnMeshExtractorTests.cs` — closed-form `R = L/(σWT)` to 1 % |
| [4 — the fast graph extractor](brief-railrf-4-fast-extractor.md) | P0 | The trace/pour classification, the graph reduction, and the fast-vs-accurate agreement gate. | `PdnFastExtractorTests.cs` — 5 % on a trace path, refusal on a pour |
| [5 — the DC solve](brief-railrf-5-dc-solve.md) | P0 | Multi-source, multi-load, the drop field, the ranked breakdown, the rail chain and its cycle refusal. | `PdnDcSolveTests.cs` — superposition, and a hand-solved two-rail ladder |
| [6 — the via current check](brief-railrf-6-via-check.md) | P0 | Per-via current, the annulus limit, the table as a sanity band, the provenance on every flag. | `PdnViaCheckTests.cs` — parallel-resistance closed form, asymmetric split against the mesh |
| [7 — the window](brief-railrf-7-window.md) | P0 | `RailRfWindow` on the Match Designer's chrome: spec column, parts table, results panel, status strip, Settings. | `RailWindowTests.cs` |
| [8 — the board view](brief-railrf-8-board-view.md) | P0 | `RailLayoutOverlay` on `ILayoutCanvasOverlay`: the drop map, the classification map, and navigation parity. | `RailBoardViewTests.cs` — **viewport equality** against a layout editor view |
| [9 — copy to clipboard](brief-railrf-9-copy.md) | P0 | `RailGraphicExport` on `PlotExporter.SetClipboardDataAsync`. No clipboard code. | `RailCopyTests.cs` — the overlay asserted in the real SVG text |
| [10 — the `rail` CLI verb](brief-railrf-10-cli-verb.md) | P0 | `circuitrf rail`, per `docs/design/cli.md`. Repeatable `--source`/`--load`, `--rail`, `--fast`/`--accurate`, `-o`. | `RailCliVerbTests.cs` — byte identity against the in-process run |
| [11 — the parts over frequency](brief-railrf-11-part-models.md) | P1 | ESR from dissipation factor, bias derating, the part's own Touchstone, typed mounting inductance, and the *indicative* marking. | `RailPartModelTests.cs` |
| [12 — Z(f), the mask and the rankings](brief-railrf-12-impedance.md) | P1 | The sweep at each observation port, the mask, the aggressor lines, the coincidence check, the anti-resonance table, the removal ranking. | `PdnImpedanceTests.cs` — a published measured PDN |
| [13 — the distributed low band](brief-railrf-13-distributed.md) | P2a | R+L mesh, spreading inductance, mounting inductance computed from via geometry. | `PdnDistributedTests.cs` — closed-form partial inductance for a via pair |
| [14 — the cavity](brief-railrf-14-cavity.md) | P2b | The shunt branch, adaptive sampling around narrow resonances, the lumped limit. | `PdnCavityTests.cs` — `1/(jωC)` to 1 % below the first mode |
| [15 — modes and maps](brief-railrf-15-modes-and-maps.md) | P2b | The eigensolve, the mode list, the field maps, the impedance-map overlay. | `PdnModeTests.cs` — first six rectangular modes to 2 %, monotone in cell size |
| [16 — A/B](brief-railrf-16-ab-comparison.md) | P3 | Two designs matched by net/refdes/part number, both curves, the delta trace, the per-part mounting table, both rankings, the report. | `RailComparisonTests.cs` — two synthetic boards differing in one known way |
| [17 — docs, example, closeout](brief-railrf-17-docs-and-example.md) | — | The user chapter, the figures, an example workspace, and the design note's own status. | DocGen, reported |
| [18 — six defects](brief-railrf-18-six-defects.md) | — | The six brief 17 found by driving the feature as a user: the zero minimum-feature width, the Fast model's false stackup sentence, the class map's paint order, the legend overlap, the BOM-only parts table, the inert results tab. | **each ships a test that fails at HEAD** |

### Round two — the first outside user (2026-09-20)

An experienced board designer ran railRF on the shipped `Power Rail` example and reported nineteen
things. Briefs 19-25 are what came of that. **Every one of them is a defect or a gap nothing in the
suite reported**, which is the same shape brief 18 records: a window can be green and still be
unusable.

| Brief | Delivers | Gate |
|---|---|---|
| [19 — unreachable states](brief-railrf-19-unreachable-states.md) | a rail can be removed (**one mis-click disables Run, Compare and Export permanently**); the pick list highlights what it selects; the breakdown list locates its copper | each fails at HEAD |
| [20 — layer visibility](brief-railrf-20-layer-visibility.md) | railRF's own per-layer checkboxes, and a `.ctech` display edit that reaches every viewer **without a save** | the second toggle is the one that catches it |
| [21 — two numbers, one name](brief-railrf-21-two-numbers-one-name.md) | the plane-pair map and the rail curve are named apart; the breakdown states its own total; the legend has a size floor | naming, not arithmetic — nothing numeric moves |
| [22 — import](brief-railrf-22-import.md) | the folder asked first; no silent return from a visible control; a PDF BOM refused with the answer | one report in it is unreproduced and must be reproduced first |
| [23 — mount and unmount](brief-railrf-23-mount-and-unmount.md) | depopulate without touching the `.clay`, and compare against the run you just did | cross-checked against Q2's own removal ranking |
| [24 — the part library](brief-railrf-24-part-library-editor.md) | `.crlib` becomes a document that can be opened | it opens — which it does not today |
| [25 — a series element](brief-railrf-25-series-element.md) | a ferrite in the middle of a rail: **a second rail node**, partitioned off the artwork | a closed-form oracle, and every existing answer unchanged bit for bit |

**19-24 are window-level and independent of each other.** 25 is a model change and is the largest
of the seven; it blocks
[footprint brief 5](brief-footprint-5-power-rail-example.md)'s series-element requirement and
nothing else.

The [SMT footprint series](brief-footprint-0-overview.md) came out of the same report — its brief 5
rebuilds this example on real footprints, and needs 23.

### Dependency order

```
1 ──┬── 2 ──┬── 3 ── 4 ──┬── 5 ── 6
    │       │            │
    │       └── 11 ──────┼── 12 ── 13 ── 14 ── 15
    │                    │
    └── 7 ── 8 ── 9      └── 16
              10 (after 5)                    17 (last)
```

- **1 blocks everything.** It is the document every other brief reads and writes.
- **3 blocks 4**, because the fast extractor's gate is agreement with the accurate one and the gate needs
  the reference to exist first.
- **7 blocks 8 blocks 9.** The window hosts the canvas; the canvas holds the overlay; the copy draws it.
- **10 needs 5**, not the window — that is the point of the CLI.
- **12 needs 11 and 5**; **13, 14, 15** are a chain on 12; **16** needs 12 and can be taken before 13.
- **7, 8, 9** are independent of **3, 4, 5, 6** and can run in parallel with them against a stub result.

---

## 3. What this series does NOT do

Restated from §2.7 of the note because a scope boundary that lives only in a design document is a
boundary nobody reads at implementation time:

- **No full-wave solve, ever.** railRF does not call `src/Engine/Mom` and does not build an `EmProblem`.
  §3 of the note is the argument; the practical rule is that a `using CircuitRF.Engine.Mom` anywhere in
  `src/Design/Layout/Pdn/` or `src/Engine/Pdn/` is a review failure.
- **No transient.** The output is Z(f) and a DC operating point.
- **No thermal model.** Everything is computed at 20 °C and every report says so. The via flag is a rule
  with a stated basis, not a temperature.
- **No regulator forward transfer, and no simultaneous multi-rail solve.** The chain is DC and solved in
  dependency order; a cycle in that order is a **refusal naming the two rails**, and that refusal is
  load-bearing (note §9). It is not a limitation to be lifted when someone asks.
- **No new `ComponentModel`, no new analysis directive, no new result type.** §1f.
- **No new artwork importer.** Gerber, Excellon, DXF, board netlist and `.kicad_pcb` are built.
- **No optimiser.** railRF measures. The minimum-cost search over a priced library is explicitly not v1
  (Q-4).

---

## 4. Standing rules for every brief in this series

1. **Every number carries its provenance.** Which model produced it (Fast or Accurate), which reference
   option was used (as imported / filled / infinite), whether an ESR was measured or defaulted, whether a
   capacitance was derated or marked, which basis a via flag used. On the plot, on the table, in the
   status strip, and in the provenance of every export. This is not decoration — §9 of the note is a list
   of risks every one of which is *silent* without it.
2. **A refusal names the flag that answers it.** The placement origin, the Excellon format, the plating
   thickness, a cycle in the rail order, a rail with no reference layer. The house spelling is already
   set by `convert` and `em`; follow it exactly.
3. **An unstated value is never a defaulted one.** A load with no current is an observation port, not a
   zero-current load. A regulator with no minimum input voltage produces no headroom finding. A part with
   no bias curve is counted and reported, not quietly derated.
4. **No timing tests.** Assert a counter or a structural property, never wall clock. The status strip's
   elapsed-time readout is a display, not an assertion.
5. **The firewall.** `src/Design`, `src/Engine`, `src/Render` and `src/Cli` reference no UI framework.
   `tests/Firewall.Tests` fails the build otherwise, and it is instant to run.

---

## 5. Build and test

Briefs 1-2 and 7-10 are reachable from `tests/Ui.Tests` and `tests/Firewall.Tests`. Briefs 3-6 and 11-16
add `tests/Engine.Tests` only where the arithmetic genuinely lives there — most of this series' numerics
live in `src/Design` and are tested from `tests/Ui.Tests`, which is where `src/Design`'s existing tests
already are.

```
dotnet test tests/Ui.Tests       --no-build --filter "FullyQualifiedName~Rail"
dotnet test tests/Ui.Tests       --no-build --filter "FullyQualifiedName~Pdn"
dotnet test tests/Firewall.Tests --no-build
```

One project path per invocation — this SDK's `dotnet test` rejects two (`MSB1008`).

**Do not run the full solution suite for this work.** Nothing in briefs 1-10 can reach `src/Core`,
`src/Engine`'s existing analyses or `RfCore`; the root run is minutes of wall clock and its likely
failures are known flakes. Read `tests/<Project>.Tests/TestResults/last-run.trx` for failures rather than
re-running — every project writes one on every run, with each test's name, outcome, failure message and
captured stdout.

**On completion of each brief:** record findings in the RESOLVED.md beside the code —
`src/Design/RESOLVED.md` for 1-6 and 11-16, `src/Ui/RESOLVED.md` for 7-9, `src/Cli/RESOLVED.md` for 10,
`src/Render/RESOLVED.md` for anything that lands there. **Never write findings into a CLAUDE.md.**

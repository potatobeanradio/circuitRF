# Brief — LVS: layout versus schematic — the series

**Status:** **BUILT** — briefs 1-15, all shipped 2026-09-21 · **Date:** 2026-09-21 ·
**Design note:** [`docs/design/lvs.md`](../design/lvs.md) rev 3, BUILT
**Area:** `src/Design/Layout/Extraction/` (new), `src/Design/Layout/Lvs/` (new),
`src/Design/Layout/Pdn/`, `src/Design/Layout/Drc/`, `src/Design/Cells/`, `src/Design/Schematic/`,
`src/Cli/`, `src/Ui/Layout/`, `src/Ui/Views/Lvs/`, `examples/`
**Requirement tag for the series:** `R-lvs<n>-<m>`, scoped per brief (`R-lvs3-5` is brief 3's fifth)

> **Two tag spaces, deliberately distinct.** The design note numbers its own requirements
> `R-lvs-1 … R-lvs-55` with no brief number in them. A brief's own requirements always carry the
> brief number: `R-lvs3-5`. When a brief cites `R-lvs-34` it means the note; when it cites
> `R-lvs3-5` it means brief 3. Do not renumber either to match the other.

---

## 0. The short answer

LVS answers one question — **does the artwork implement the drawing** — and reports every place it
does not, with a marker, a path and the designer's own names for the parts involved.

The design note is the specification and this series does not restate it. What this overview does
is **fix the boundaries between the briefs** and record what came out of reading the code rather
than out of the note.

### The one rule the whole series is built on

> **LVS is a fourth reader of copper the repo already partitions, and a comparison against a netlist
> the schematic side already extracts. It builds neither of those.**

`DrcConnectivity` partitions. `NetExtractor` + `Elaborator` extract the schematic. `CellPins` +
`LayoutInstanceTransform` project pins. railRF already joins the first to the third. What this
series adds is: a **terminal map** (brief 1), a **device reading** of the partition (brief 3), a
**canonical netlist** both sides reduce to (brief 4), and a **comparison** (brief 7).

The corollary has teeth, and brief 3 gates it with a comment-stripped source scan: **nothing under
`src/Design/Layout/Lvs/` unions geometry, walks vias, resolves a cell pin, or transforms a
placement.** Every one of those has exactly one implementation and it is somewhere else.

---

## 1. Nine things that are not obvious, resolved here once

Each is restated where it bites.

### 1a. The precedence rule that railRF depends on would make LVS useless, silently

`PdnLayoutNets`' governing rule is *net(pad) = the schematic's binding, else the net stated on the
copper*. Right for railRF, which wants the best available answer. **Catastrophic here**: LVS would
ask the schematic what the layout says and get the schematic's own answer back, every net would
match, every design would pass, and there would be no symptom at all.

Brief 2 therefore makes the artwork-only mode an explicit `PinNaming.ArtworkOnly` **enum**, not four
nullable delegates, so a future caller cannot half-supply the schematic and re-enter the trap
without writing the word.

### 1b. The connectivity walk is `internal` and stays that way

`DrcConnectivity` is `internal` to `CircuitRF.Design` and all three callers — DRC, railRF, LVS — are
in that assembly. **Do not copy it, do not make it public, do not write a second walk.**
`PdnRailRegions`' header already says this for railRF and the reasoning is unchanged: a board whose
connectivity two of the three disagree about is a bug none of them reports.

### 1c. The pad projection has one implementation and `CellPins` exists because it nearly had four

`CellPins.Resolve` is two branches — persisted pins first, else re-invoke the generator — and the
second branch only fires on a generated cell written before pins were persisted. Three callers had
each grown a private copy and the copies that omitted the second branch looked correct on every
freshly-regenerated cell. A fourth copy here would be wrong on exactly the same cells, silently.
Brief 2 promotes `PdnLayoutPads.PadsOf` rather than writing an LVS pad walk.

### 1d. `src/Design` cannot see `src/Ui`, and nothing here needs to

Reference graph: `Core → Engine → Design → Render → Ui`, with `Cli` beside `Ui`. Every extraction
and comparison type in this series lives in `src/Design`; the panel (brief 12) is the only thing
above the wall, and it consumes a finished `LvsRunResult`. The CLI verb (brief 11) and the GUI
panel call the identical function — that is the gate, not a convention.

### 1e. `LayoutDesignFlatten` destroys exactly the thing LVS is built on

It returns `IReadOnlyList<LayoutShape>` — clones, in world space, with no instance path. Correct for
Gerber (no hierarchy) and for DRC (rules are about geometry). Useless here: a device is a placement
and the report names it by designator. Brief 3 adds a **tagged** flatten beside it; it does not
change the existing one, which two shipped consumers depend on byte for byte.

### 1f. Reduction is ON, and that reverses the design note's first draft

The note originally proposed no automatic series/parallel reduction. The owner's answer was *use
what is common and expected*, and what is common is reduction on by default. Brief 6 builds it,
bounded by the note's R-lvs-35 table, and every finding **un-reduces** so a report never names a
merged group where a designator would do.

### 1g. Ground is in the stackup, not on the drawing, and the shipped MMIC technology proves it

`mmic-GaAs_2LM_100um.ctech`'s `Backside Metal` conductor has `DrawingLayers: []`. Read naively,
every backside via terminates in mid-air and every grounded device on the starter MMIC technology
reads as open. Brief 3 makes `IsGroundReference` the global reference whether or not it draws —
**and says so on every run that relies on it**, because it is the one inference in the whole
extraction that the user cannot see on their own screen.

### 1h. There is no shipped design that exercises this, so brief 5 builds one

`examples/` holds six workspaces and none of them has a schematic and a layout that correspond
part-for-part. `circuitRF_demo` is a scratchpad and several of its benches report nonsense. **Brief
5 is not documentation; it is the oracle every comparison brief is gated against**, and it is also
what the owner has asked for in order to settle the property tolerances (brief 10), which cannot be
chosen in the abstract.

### 1i. Gate on counters, never on wall-clock

The structural properties are *one extraction per distinct cell, not per placement* and *one
indexed query per pin, not a scan*. Both are counters. `WireSweepCounters` is the in-repo precedent
for the shape and brief 9's gate copies it. No new timing test is added by this series.

---

## 2. The briefs

| # | Brief | Delivers | Depends on |
|---|---|---|---|
| 1 | [the terminal map](brief-lvs-1-terminal-map.md) | `.ccell` `Terminals`, the four-rule derivation, `check` validation, the three authoring writers | — |
| 2 | [the shared extraction](brief-lvs-2-shared-extraction.md) | `Layout/Extraction` namespace; railRF and LVS read copper through one API; indexed piece lookup; retained merge edges | — |
| 3 | [the layout netlist](brief-lvs-3-layout-netlist.md) | tagged flatten, device tiers 0-2, pins→nets, ground, `LvsNetlist` from a `.clay` | 1, 2 |
| 4 | [the schematic netlist and the canonical type](brief-lvs-4-schematic-netlist.md) | the same `LvsNetlist` from a `.csch`; one `DeviceType` resolver both sides call | 1 |
| 5 | [the proving designs](brief-lvs-5-proving-designs.md) | a correct board + a broken copy + an MMIC cell, in `examples/`, asserted finding by finding | 1 |
| 6 | [reduction](brief-lvs-6-reduction.md) | parallel and series collapse on both netlists through one function; `--no-reduce` | 4 |
| 7 | [the comparison](brief-lvs-7-comparison.md) | anchoring, colour refinement, automorphism handling, divergence at the point of divergence | 3, 4, 6 |
| 8 | [findings, shorts and opens](brief-lvs-8-findings.md) | `LvsRunResult`, the `lvs.` diagnostic catalogue, short paths, open islands, markers | 7 |
| 9 | [hierarchy](brief-lvs-9-hierarchy.md) | per-cell extraction with a content-keyed cache, boundary stitching, undeclared contact, `--flatten-cell` | 7, 8 |
| 10 | [properties and tolerances](brief-lvs-10-properties.md) | value comparison, the tolerance table, derived parameters, merged multiplicity | 6, 8, 5 |
| 11 | [the CLI verb](brief-lvs-11-cli-verb.md) | `circuitrf lvs`, `--json`, exit codes, MCP | 8 |
| 12 | [the GUI surface](brief-lvs-12-gui.md) | the panel, markers, cross-probing, waivers | 8, 11 |
| 13 | [assemblies and the wBond](brief-lvs-13-assemblies.md) | multi-die, multi-technology, bondwire feet | 9 |
| 14 | [geometric device recognition](brief-lvs-14-recognition.md) | tier 3, `DeviceRules` in `.ctech`, default off | 3 |
| 15 | [docs and the shipped example](brief-lvs-15-docs-and-example.md) | user docs, the example workspace row, the design note's status flip | all |

**The whole series ships together** (owner, 2026-09-21). The ordering is a build order and no brief
is a place to stop; what it buys is that briefs 2, 7 and 9 each have an oracle that already works.

---

## 2A. Traceability — every requirement in the note, and the brief that builds it

**Said once, here.** Scattering 55 citations through the briefs would put the map in fifty places
and keep it in none; this table is the map, and §6's gate checks it covers `R-lvs-1` … `R-lvs-55`
with no hole. A note requirement with no brief is a feature nobody is building.

| Note | Brief | Note | Brief | Note | Brief |
|---|---|---|---|---|---|
| R-lvs-1 | 0, 3, 14 | R-lvs-20 | 13 | R-lvs-39 | 6, 10 |
| R-lvs-2 | 3, 6 | R-lvs-21 | 13 | R-lvs-40 | 6, 11 |
| R-lvs-3 | 2 | R-lvs-22 | 13 | R-lvs-41 | 10 |
| R-lvs-4 | 2 | R-lvs-23 | 13 | R-lvs-42 | 10 |
| R-lvs-5 | 2, 3 | R-lvs-24 | 3 | R-lvs-43 | 10 |
| R-lvs-6 | 3 | R-lvs-25 | 13 | R-lvs-44 | 2, 8 |
| R-lvs-7 | 14 | R-lvs-26 | 4 | R-lvs-45 | 8 |
| R-lvs-8 | 3 | R-lvs-27 | 4 | R-lvs-46 | 9 |
| R-lvs-9 | 1 | R-lvs-28 | 4 | R-lvs-47 | 2 |
| R-lvs-10 | 1 | R-lvs-29 | 4, 11 | R-lvs-48 | 3, 9 |
| R-lvs-11 | 1 | R-lvs-30 | 4 | R-lvs-49 | 7 |
| R-lvs-12 | 1 | R-lvs-31 | 4, 7 | R-lvs-50 | 9 |
| R-lvs-13 | 3 | R-lvs-32 | 7 | R-lvs-51 | 2, 7, 9 |
| R-lvs-14 | 3 | R-lvs-33 | 7 | R-lvs-52 | 12 |
| R-lvs-15 | 3 | R-lvs-34 | 6 | R-lvs-53 | 12 |
| R-lvs-16 | 3 | R-lvs-35 | 6 | R-lvs-54 | 1, 11 |
| R-lvs-17 | 9 | R-lvs-36 | 6 | R-lvs-55 | 11 |
| R-lvs-18 | 9 | R-lvs-37 | 4, 6 | | |
| R-lvs-19 | 9 | R-lvs-38 | 6, 8 | | |

And the note's §9 gap table, which is the other list that must not lose an entry:

| Gap | Closed by | Gap | Closed by |
|---|---|---|---|
| G1 nothing stamps `Net` | 3 (nothing required; `Net` names and anchors only) | G8 no layout-side value | 10 |
| G2 flatten loses identity | 3 | G9 merge edges discarded | 2, 8 |
| G3 no terminal map | 1 | G15 `SchematicId`/refdes unverified | 3 |
| G4 ground undrawn | 3 | G16 four type namespaces | 4 |
| G5 instance arrays | 3 | *policy:* multi-technology | 13 |
| G6 wBond has no pad binding | 13 | *policy:* undeclared contact | 9 |
| G7 `PieceAt` is a scan | 2 | | |

The note's §10 validation list, in the same spirit: the correct-vs-broken workspace is **brief 5**;
the `--flat` equivalence gate is **brief 9** gate 1; the `--no-reduce` gate is **brief 6** gate 10;
the generated scale fixture is **brief 9** gate 7.

---

## 3. Where the code goes

```
src/Design/Layout/Extraction/      NEW — what railRF and LVS share (brief 2)
    CopperPieces.cs                  was Pdn/PdnLayoutNets.cs' PdnCopperPieces
    LayerRegions.cs                  was PdnMeshExtractor.BuildLayerRegions
    PlacedPins.cs                    was PdnLayoutPads
    PlacedPin.cs                     was PdnAttachments' PdnPad + PinSource + PinNaming
    Regions.cs                       was PdnRailRegions.Walk
    PieceIndex.cs                    NEW — the broad phase PieceAt never had

src/Design/Layout/Lvs/             NEW — the comparison (briefs 3-10, 13, 14)
    LvsNetlist.cs                    LvsDevice, LvsNet, LvsTerminal, DeviceType
    LayoutRead.cs                    .clay  -> LvsNetlist          (brief 3)
    SchematicRead.cs                 .csch  -> LvsNetlist          (brief 4)
    LvsReduce.cs                     both -> reduced               (brief 6)
    LvsCompare.cs                    the refinement                (brief 7)
    LvsFindings.cs                   the diagnostic catalogue      (brief 8)
    LvsRun.cs                        the one entry point everything calls
    LvsWaiver.cs                     persisted on the .clay        (brief 12)
    DeviceRecognition.cs             tier 3                        (brief 14)

src/Design/Layout/TerminalMap.cs   NEW — the map and its derivation (brief 1)
src/Cli/Lvs.cs                     NEW — parsing, refusals, reporting (brief 11)
src/Ui/Views/Lvs/                  NEW — the panel (brief 12)
tests/Ui.Tests/Lvs/                NEW — every gate in this series
```

`src/Design/Layout/Lvs/LvsRun.Run(...)` is **the** entry point. The CLI calls it, the panel calls
it, and every test that is not a unit test calls it. There is no second path into the comparison.

---

## 3A. The series' own gate

**`R-lvs0-1`** §2A's first table covers `R-lvs-1` … `R-lvs-55` with no hole and names no brief
that does not exist. A test asserts it: parse the note's requirement numbers, parse the table,
compare the sets. A requirement added to the note and not to the table fails here, which is the
only way a note and a plan stay in step.

**`R-lvs0-2`** Every brief listed in §2 exists, and every link in §2 and §2A resolves.

---

## 4. Scope for the series

- **No auto-repair, no back-annotation of extracted nets into the schematic.** The note's §11.
- **No ERC, no antenna or density rules.** Those are DRC's.
- **No parasitic extraction.** That is the EM engine.
- **No matching rule language.** The matching rules are the algorithm; every knob on them is a way
  to make a wrong design pass.
- **No change to `DrcEngine`'s behaviour, `PdnBoardPads`' behaviour, or any shipped example's
  numbers.** Brief 2 is gated on railRF answering identically, to the number.

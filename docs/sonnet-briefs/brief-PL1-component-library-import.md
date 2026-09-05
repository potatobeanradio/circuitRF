# Sonnet Brief — Phase PL1: component library import (footprint + symbol, one entry point)

**Design:** `docs/design/layout-view.md` §8 (interchange), §9 (layout pins);
`docs/design/data-model.md` §5. **Consumes L4d (`.kicad_pcb` import) directly** — §5 of this brief is
mostly a new entry point onto `PcbReader`, not a new reader.

**Scope is import only, and it is import of a PART, not a board.** A component is a symbol plus a
footprint plus the map between them. A phase that lands only one of the three has landed nothing
usable — see §4.

**Test loop** (root `CLAUDE.md` §"Layout/UI work") — two commands; this SDK rejects more than one
project path per invocation:
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. The format list

A component library arrives as a folder of subfolders, each holding one target format's symbol and
footprint files. There is no single format common to all of them, so the question is coverage rather
than standardisation.

**This phase reads four formats: `.kicad_sym`, `.kicad_mod`, `.lib` and `.lbr`.** Between them they
cover the text formats a library folder holds; what they leave out is proprietary binary. Everything
beyond that list is PL2.

**R-PL1-1. Convert to native `.csym` + `.clay`. Do not render the foreign file live.** A footprint
earns its keep here only when its pad copper lands on real technology layers — that is what makes it
DRC-able and, far more to the point for this tool, **meshable by the EM extractor**. A live renderer
over a foreign file yields a picture the extractor cannot touch. These files are static — there is no
upstream to stay in sync with — so nothing is lost by converting once.

**R-PL1-2. Keep the source file, though.** Copy the file(s) the import read into the cell folder and
record them in `CcellFile.ImportedFrom` (`CcellImportProvenance` — `Source`, `Definition`,
`ContentHash`; all three already exist), so the cell names the file and the definition it was built
from and the bytes are there for a later re-import.

## 2. The one UI entry point

**R-PL1-3. `File ▸ Import ▸ Component…`, one menu item, and it accepts a FILE or a FOLDER.** Add it
to the existing Import submenu in `src/Ui/Views/WorkspaceWindow.axaml` (and its two siblings,
`Views/Shared/TornOffFileMenuView.axaml` and the toolbar block at ~line 477) beside `GDSII…`,
`DXF…`, `Board…`, `Gerber…`. Command `ImportComponentCommand` on `WorkspaceViewModel`, following
`ImportBoardCommand`'s shape exactly.

**One menu item, not one per format.** Which format a given library folder happens to hold is what the
scan answers; it is not something to ask before the scan has run.

**R-PL1-4. Pointed at a FOLDER, scan it recursively and present what was found.** The importer walks
the tree, classifies every file by content (never by parent folder name — §10), and shows one list:

```
This folder holds 6 component formats. 3 can be imported:

  ▸ symbol + footprint + pin map   (.kicad_sym, .kicad_mod ×3)      ← preselected
    symbol + footprint + pin map   (.lbr)
    footprint only                 (.kicad_mod ×3)

  Not read: 4 binary formats, 1 three-dimensional model.
```

Rank by completeness first (symbol + footprint + map beats footprint alone), then by reader
confidence. Preselect the top row. **Name what was skipped, with a count and a reason**, so a folder
whose symbol circuitRF cannot read says so rather than appearing to hold none.

Pointed at a single file, skip the chooser and import it.

**R-PL1-5. `ImportFolder.UniqueName` already answers where the cells land.** Do not invent a second
grouping rule. Importing the same part twice yields `PartName_2`, exactly as a board import does.

## 3. What a successful import produces

One cell folder per component (`CellFolder.CreateCellFolder`), containing:

- `symbol/<name>.csym` — `SymbolPersistence.SaveToFile`
- `layout/<name>.clay` — `LayoutPersistence.SaveToFile`, with `LayoutView.Pins` populated
- `.ccell` — `PrimarySymbol`, `PrimaryLayout`, `NumPorts`, `ImportedFrom`
- the source file(s), copied (R-PL1-2)

**R-PL1-6. No schematic view, and no netlist.** An imported part has no internal circuit. It is a
symbol, a footprint, and terminals. Anything more is invention.

**R-PL1-7. Metadata that exists goes onto the cell as parameters, read-only where possible.** Every
format in this phase carries a manufacturer name, a part identifier, a description and a datasheet
URL as free-text properties. Carry them through as `CcellParameter`s with
`ShowOnSchematic = false`. **Never parse them, never infer a model from them.**

## 4. The pin↔pad map — the invariant the phase exists to preserve

This is the centre of gravity. Geometry without it is two unrelated drawings.

All three formats state the map explicitly and all three state it the same way — a symbol pin has a
**name**, a pad has an **identifier**, and a third record joins them:

- `.kicad_sym`: `(pin … (name "VDD1") (number "1"))` — the join is inside the pin.
- `.lib`: `X VDD1 1 0 0 300 R …` — name then pad identifier, positionally.
- `.lbr`: `<connect gate="G$1" pin="AI" pad="5"/>` inside `<deviceset><devices><device>` — a
  separate table, and the only one of the three that can express a pin bonded to several pads.

**R-PL1-8. `PortIndex` is assigned ONCE, from the map, and both views use the same numbering.**
`CsymPin.PortIndex` *i* and `LayoutView.Pins[i]` must be the same terminal. Order by pad identifier
using a natural (numeric-aware) sort so `2` precedes `10`, and put non-numeric identifiers last in
their own stated order. Write `CsymPin.Name` = the symbol's pin name, `LayoutPin.Name` = the pad
identifier — **both strings are kept, neither is derived from the other**: they differ, and both are
needed when assigning an EM port.

**R-PL1-9. Pad identifiers are STRINGS, not integers.** A part's pads may run `1`…`8` plus a named
one such as `EPAD`. A reader that parses pad identifiers as integers drops the named pad silently —
which on an RF part is the terminal whose grounding decides the answer.

**R-PL1-10. Symbol pin ORDER is not pad order.** A symbol may declare its pins in any order — say 1,
8, 3, 5, 6, EPAD, 7, 4, 2 against a footprint numbered 1…8 plus EPAD. Building `NumPorts` by walking
symbol declaration order and assuming it matches the footprint is the defect this rule exists to
prevent, and a fixture whose symbol order matches its pad order cannot catch it. **The gate fixture
must have a scrambled order.**

**R-PL1-11. A pin with no pad, or a pad with no pin, is REPORTED, never dropped and never invented.**
Both occur: `.lbr` writes `GND@1` / `GND@2` for one logical pin bonded to two pads (the `@n` suffix
belongs to the format, not to the name), and a footprint may carry mounting or shield pads no symbol
pin references. Import both sides in full, join what joins, and say in one
Messages line how many of each were left unjoined.

## 5. `.kicad_mod` — almost entirely already built

`PcbSexpr.Parse` already tokenizes this grammar. `PcbReader.ReadFootprint` (≈`PcbReader.cs:828`)
already reads `footprint`/`module`, `pad`, and every `fp_*` graphic, including pad shape, drill,
layer-set expansion, local-angle recovery and back-side mirroring. **Almost nothing in §5 is new
code.** Two things stand between that and reading a standalone footprint file:

**R-PL1-12. The root-tag guard.** `PcbReader.Read` refuses anything whose root is not `kicad_pcb`
(`PcbReader.cs:126`). Add a sibling entry point — `PcbReader.ReadFootprint(text, dbuPerMicron)` —
that accepts a root of `footprint` **or** `module` and shares everything below. Do not relax the
existing guard: a board reader that silently accepts a footprint is a worse diagnostic, not a better
one.

**R-PL1-13. A footprint file has no `(layers …)` table, and R-L4d-16 needs one.** Pad layer specs are
wildcards (`*.Cu`, `*.Mask`, `*.Paste`) expanded against the board's own table. Synthesise a
**two-copper-layer** table — front and back, plus the technical names already transcribed in
`PcbLayerNaming.Technical` — and say so. This is the correct answer, not a convenient one: a library
footprint genuinely describes a two-sided world, and expanding `*.Cu` to thirty inner layers would
invent copper the part does not have.

**R-PL1-14. Everything L4d already established still holds and must not be re-derived**: exact units
at 1 DBU = 1 nm via `Math.Round` and never a cast (R-L4d-2); Y down in the source, up in `.clay`
(R-L4d-3); pad-with-no-copper imported as the aperture it is (R-L4d-19); unknown tokens reported once
with a count. Dispatch on tokens present, never on the version stamp — both the `(module …)` and
`(footprint …)` epochs are live, and both must import.

**R-PL1-15. Pads become `LayoutPin`s (R-L4d-17's mechanism, now the whole point).** `Name` = pad
identifier, `WidthDbu` = the pad's extent across its facing direction, `Layer` = its copper layer,
`OutwardDeg` = the direction away from the footprint's centroid, snapped to the nearest 90° when the
pad sits on a package edge. A pad the EM port picker cannot select is a footprint that cannot be
simulated.

## 6. `.kicad_sym` and `.lib` — the symbol side

`.kicad_sym` is the same S-expression grammar, so `PcbSexpr` serves it unchanged. `.lib` is an older
one-record-per-line text format from the same lineage (`DEF`/`F0`-`F3`/`DRAW`/`X`/`P`/`S`/`A`/
`ENDDRAW`/`ENDDEF`) and needs a small reader of its own.

**R-PL1-16. Both emit `KitSymbolPin` + `KitSymbolShape` (`src/Core/Pdk`), and `src/Ui`'s
`KitTemplateSymbol.BuildFromDrawing` turns those into a `Symbol`.** That split already exists for the
same reason it is right here: the reader lives on the far side of the UI firewall and
`SymbolPrimitive` lives beside the renderer. `src/Design` already references `src/Core`, so the
readers can live in `src/Design/Layout/Interchange/` beside the footprint side with no new project
reference and no firewall change. **Do not build a second neutral symbol model.**

**R-PL1-17. The scale is exact, not fitted — 1 symbol-editor local unit = 1 mil.** `SymbolModel.cs`
states 100 local units = one connection-grid square P. The older symbol epoch is in mils on a 100-mil
pin grid; the newer is the same drawing in millimetres, related by exactly 0.0254. So the newer
converts as `mm / 0.0254` and the older 1:1, and pins land on the connection grid **exactly** — no
rounding, no fitting.

*Verify P's nominal pitch against `BuiltInSymbols` in the spike (§11) before relying on this.* If it
does not hold, fall back to `KitTemplateSymbol.ChooseKitScale`, which already chooses a scale from a
whole drawing at once — but say in the completion note that the exact mapping was refuted, because a
fitted scale means imported and hand-drawn symbols no longer share a grid.

**R-PL1-18. The symbol's Y flips too, and this is the phase's most likely bug.** `.csym` is **+y
down** (`SymbolModel.cs`: "+x right, +y down (screen convention)"); the source symbol formats are +y
**up**. So both halves of this import negate Y — but for opposite reasons, and a developer who
reasons "`.clay` is Y-up so the symbol must be too" gets it backwards. A symbol imported without its
own flip renders upside down **while the footprint beside it looks perfectly correct**, which is
exactly the failure that survives review. Gate it separately from the footprint flip, with a symbol
whose art is asymmetric on both axes.

**R-PL1-19. A pin's stated coordinate is its FREE END, not where it meets the body.** Measured in all
three formats: the pin carries a length and the body edge sits one length inward. circuitRF's
`CsymPin.LocalX/Y` is the connection point, so the free end is what to write — but the body geometry
must not be shifted to match. Getting this wrong yields a symbol whose pins float one pin-length off
the box, which looks like a scale bug and is not one.

**R-PL1-20. Read the graphic body, do not substitute a generated box.** `KitTemplateSymbol.Build`
(pins only) exists and is the fallback, not the target: these files carry polylines, rectangles, arcs
and polygons that distinguish a part from a rectangle. Use `BuildFromDrawing`. Where the file has a
true arc, note `KitSymbolArc`'s own warning — its angles are counter-clockwise-on-screen and
circuitRF's are clockwise, and the sign flip is the consumer's job.

## 7. `.lbr` — the XML library, and the only route to part D

One XML file carrying `<layers>`, `<packages>`, `<symbols>` and `<devicesets>`. Units are
millimetres throughout; layers are **numbered** with a name table at the head of the file.

**R-PL1-21. Read the file's own layer table; never hard-code the numbers.** The numbering is
conventional (copper low, silkscreen/documentation/keepout in the 20s–50s, symbol graphics in the
90s) but the table is present in every file and is authoritative. Map copper by table position the
way `PcbLayerNaming` already maps it for export; route the rest through the shared layer-mapping
dialog like every other importer.

**R-PL1-22. A pin's `length` is a WORD, not a number.** `length="middle"` — the format's four named
lengths are a fixed enum in tenths of an inch. Parsing it as a number yields zero, which collapses
every pin onto the body edge and produces a symbol that is wrong but not obviously wrong.

**R-PL1-23. A deviceset may declare several gates and several devices; this phase handles one of
each and says so.** `<gate>` is a symbol section (a multi-section part), `<device>` is a package
variant. **Import the first, report the count of the rest by name, and do not silently pick.**
Multi-section parts and package-variant selection belong in their own phase.

**R-PL1-24. Descriptions carry HTML.** `<description>` holds escaped markup. Strip tags to plain
text for the cell's description parameter; do not render it, and do not store the markup.

## 8. Density variants

A land pattern is commonly written three times — a nominal one and two siblings suffixed `-M` and
`-L`. These are density levels of one pattern, not three parts, and `.kicad_sym`'s `ki_fp_filters`
names the set.

**R-PL1-25. Import all variants as sibling layout views in ONE cell, and make the nominal one
`PrimaryLayout`.** A cell folder's `layout/` subfolder already holds several `.clay` files with one
primary — the mechanism exists. Do not create three cells, and do not import one and discard the
others.

## 9. Units, layers and the technology

**R-PL1-26. Reuse the shared layer-mapping dialog, with "Add to technology" preselected** — the same
default L4b set and L4d followed.

**R-PL1-27. No stackup, and nothing invented in its place.** Unlike a board file, a component file
states no stackup, no permittivity and no thickness. Import onto the destination technology's existing
layers and say in one Messages line what an EM run will still need (R-L4d-6).

## 10. Classification and refusal

**R-PL1-28. Classify by CONTENT, never by the containing folder's name and never by extension
alone.** Sniff the first bytes: an S-expression root tag, `EESchema-LIBRARY`, an XML declaration and
the XML library's own element structure. `convert`'s import classifier already follows this pattern.

**R-PL1-29. A refusal names the formats circuitRF DOES read, by extension** — "…circuitRF reads
`.kicad_sym`, `.kicad_mod`, `.lib` and `.lbr`; this folder also holds 4 binary formats" — so the
message says what would work rather than only what did not.

**R-PL1-30. Do not route to `.dxf` as a fallback**, even though circuitRF reads it. A DXF beside a
part is a dimensioned **drawing**: layers are bare numbers, units are inches, annotation text sits as
TEXT entities interleaved with the copper, and there are **no pad identifiers and no pin names in the
file**. It cannot supply the pin↔pad map, so it is reported as skipped rather than offered.

## 11. The spike, before the phase

**R-PL1-31. Half a day, before §5–§7 are designed.** With a throwaway reader, dump and record:

1. The connection-grid pitch `BuiltInSymbols` actually uses, against R-PL1-17's exact-mil claim.
2. What `PcbReader.ReadFootprint` produces when handed a synthetic two-layer table — specifically
   whether `*.Cu` / `*.Mask` / `*.Paste` expand as R-PL1-13 predicts.
3. Which pad shapes the readers must handle (`rect`, `roundrect`, `oval`, `circle`, `custom`), and
   whether any needs geometry `LayoutShape` cannot express.
4. What a multi-gate or multi-device definition looks like in each format (R-PL1-23).

If (2) does not hold, resize §5 before building it rather than discovering it mid-phase.

## 12. Fixtures

**R-PL1-32. Every committed fixture is SYNTHETIC, and this is not negotiable.** A real library file
carries a manufacturer name, a part identifier, a description and a datasheet URL in every format, and
none of that belongs in this repository. Author small files that follow each grammar, with invented
part and terminal names, and commit those. See also `feedback-no-personal-paths-in-repo`.

**R-PL1-33. Write from the public format documentation; do not read any originating tool's source.**
The standing §8 rule for GDSII applies unchanged, and two of this phase's formats belong to GPL
tools. Root `CLAUDE.md` §Licensing.

## 13. Scope guardrails

- **Import only.** No writer of any of these formats, not even a partial one.
- **No board import changes** beyond `PcbReader`'s additive footprint entry point.
- No multi-gate/multi-section parts, no package-variant selection UI beyond R-PL1-25's sibling views.
- No 3D models. A `.step`/`.stp` beside the part is out of scope and is reported as skipped.
- No binary formats. Not this phase, not as a stretch goal.
- No netlist, no schematic view, no simulation model — see R-PL1-6.
- No new mesher or EM work: an imported footprint is ordinary artwork the existing path handles.
- Don't touch `src/Core` (beyond consuming `Pdk`'s existing neutral symbol types), `src/Engine`,
  `RfCore`.

## 14. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); documented gate green; no existing test regresses.
2. **Folder scan (R-PL1-4)** — a synthetic tree of subfolders, two holding readable files and three
   not, yields one ranked list; the unreadable ones are counted and named by category; nothing is
   classified by folder name or by extension (R-PL1-28).
3. **Pin↔pad map (R-PL1-8/10)** — a fixture whose symbol pin order is **scrambled** relative to its
   pad order imports with `CsymPin.PortIndex` *i* and `LayoutView.Pins[i]` naming the same terminal.
   A reader that walks declaration order fails this test.
4. **String pad identifiers (R-PL1-9)** — a fixture with pads `1`…`8` plus a non-numeric thermal pad
   imports nine terminals, sorted `1`…`8` then the named one.
5. **Unjoined terminals (R-PL1-11)** — a pin bonded to two pads and a pad no pin references both
   import and are both reported; neither is dropped, neither is invented.
6. **Footprint units (R-PL1-14)** — `−1.234567 mm` lands on exactly `−1234567` DBU. The negative case
   is the test.
7. **Two flips, separately (R-PL1-18)** — one fixture, asymmetric on both axes in **both** views;
   the footprint's handedness and the symbol's handedness are asserted independently. Flipping either
   one alone fails.
8. **Exact symbol scale (R-PL1-17)** — a pin at 100 mil in the older epoch and the same pin at
   2.54 mm in the newer both land on exactly the same local coordinate, on the connection grid.
9. **Pin free end (R-PL1-19)** — a pin whose stated point is one length outside the body imports with
   its connection point at the stated point and the body unmoved.
10. **Body graphics (R-PL1-20)** — a symbol carrying a polygon and an arc imports both; the arc's
    sweep direction is asserted, not just its presence.
11. **Both footprint epochs (R-PL1-14)** — `(module …)` and `(footprint …)` fixtures both import;
    neither is refused for its version.
12. **Named pin length (R-PL1-22)** — an `.lbr` fixture using each of the four named lengths places
    all four pins correctly; a numeric parse would collapse them.
13. **Layer table read, not assumed (R-PL1-21)** — an `.lbr` fixture whose layer table is reordered
    imports identically to one in conventional order.
14. **Multi-gate reported (R-PL1-23)** — a two-gate fixture imports gate one and names the other; it
    is not silently merged and not silently dropped.
15. **Density variants (R-PL1-25)** — a part with three land-pattern variants yields **one** cell
    with three `.clay` views and the nominal one as `PrimaryLayout`.
16. **No stackup invented (R-PL1-27)** — the destination technology's stackup is unchanged and one
    Messages line names what EM still needs.
17. **Provenance (R-PL1-2)** — `ImportedFrom` round-trips and the source file is present in the cell
    folder.
18. **Refusal names the alternatives (R-PL1-29)** — a binary input is refused with a sentence naming
    the four readable extensions.
19. **Counters only** — entities read, shapes produced, terminals joined. **No wall-clock assertion
    anywhere** (root `CLAUDE.md`; `feedback-no-new-timing-benchmark-tests`).
20. **Firewall** — `tests/Firewall.Tests` green; the new readers reference no Avalonia.

## 15. On completion

Write a **"Phase PL1 — COMPLETE"** entry at the top of `src/Design/RESOLVED.md` — **not**
`CLAUDE.md`, and not any `CLAUDE.md` (`feedback-resolved-md-instead-of-claude-md`).
Call out:

1. **The spike's four findings as measurements**, especially whether R-PL1-17's exact-mil mapping
   held. If it did not, say so plainly and say what a fitted scale costs.
2. **How much of §5 was genuinely new code** versus reused from L4d — the honest number, because it
   sizes PL2.
3. **The two-flip trap**: how each flip was proven, and which fixture would have missed it.
4. **What an imported part still cannot do** — no netlist, no model, no multi-gate, no package
   variant chooser — stated as limitations, not omissions.
5. **Whether the folder chooser's ranking (R-PL1-4) held up** against a folder holding several
   formats at once, and what it ranked badly.
6. Whether PL2's breadth is now worth building, **on measured evidence** rather than on §1's format
   list — see that brief's own §1, which argues it adds no coverage.

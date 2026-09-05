# src/Design — resolved findings (detail, off the CLAUDE.md growth path)

## Phases PL1/PL2 — post-implementation review (2026-09-05)

Both phases re-read against their briefs, and the whole path exercised on library folders shaped the
way a user's are rather than the way the fixtures are: many parts under one root, each written out
once per target format into a sibling folder, and pin names that repeat. Every finding below is a
defect the committed gates passed over. All four are fixed.

### 1. A pin NAME was being used as a pin IDENTITY, and it fails on nearly every real part

`ComponentTerminals.Build` keyed the pin↔pad join on the symbol pin's NAME. R-PL1-11's "a pin bonded
to several pads appears once in the symbol" was implemented as "a name seen twice is one pin seen
twice", and those are not the same statement. **A real part declares `VSS` seven times and `VDD` six,
each its own pin on its own pad** — so six of those seven lost their symbol side, were counted as
"pads referenced by no symbol pin", and, worse because nothing reported it, **all seven declarations
were given the FIRST one's `PortIndex`**. That breaks R-PL1-8's whole invariant on the majority of
parts with more than one supply pin.

Measured, on a 144-pad part whose imported section declares 72 pins: **65 of them joined before the
fix, 72 after** — all of them. On a 6-terminal part, 5 → 6.

**The identity is the DECLARATION, not the name** (`ComponentTerminal.SymbolPinIndex`, and
`ComponentImport.BuildSymbol` keys its `PortIndex` lookup on it). The genuine bonded case — the XML
library's `GND@1`/`GND@2`, one logical pin drawn twice — is separated by the format's own suffix and
nothing else, so `ComponentSymbolPin.Bonded` / `ComponentConnect.Bonded` record that the suffix was
there before it is stripped, and only bonded declarations sharing a name collapse to one terminal.

**Why the fixtures could not catch it**: `WIDGET9`'s nine pins have nine distinct names, and every
other fixture in both phases is the same shape. A part whose pin names are all distinct cannot
distinguish "keyed on the name" from "keyed on the declaration". `REPEAT6` exists for exactly that
and declares `GND` three times (`Gate5b`); `Gate5` holds the bonded case shut from the other side.

### 2. One candidate could be built out of two different parts' files

`ComponentFolderScan` ranked the WHOLE scanned tree as a single pool. A library folder holds many
parts, and one part is routinely written out once per target format into a subfolder of its own, so:

- a symbol in one folder paired with **every** footprint in the tree — one cell came out carrying
  another part's land patterns, reported only as "these land patterns are not density levels of one
  pattern";
- a multi-file set (`.p`/`.d`/`.c`, the `.hkp` four) collected every such file in the tree into ONE
  candidate, and the reader takes the first file of each kind — **so pointing at a folder of four
  parts imported one and silently dropped three.**

Measured, on a root holding four parts in eight formats each: **13 candidates before, most of them
mixtures of two or more parts; 23 after, each one a single component.**

The fix is a grouping pass ahead of the ranking (`RankGrouped`). A group is not a directory — a
symbol's land patterns genuinely sit in a child folder — so **a directory that directly holds a file
which can BEGIN a component is a group root, and every other file joins the nearest group root above
it.** Groups keep their candidates together and are ordered by the best candidate in each, so the
preselected top row is still the best reader rather than whichever folder sorts first
(`Gate2c`, `Gate2d`). Each row now names its folder (`ComponentCandidate.Location`), because a part
written out in eight formats produces rows that are otherwise indistinguishable.

### 3. The folder walk's ceilings were silent, and unstoppable

`MaxDepth`/`MaxFiles` made the walk finite, which is not the same as stoppable: twenty thousand file
opens on a network share is minutes of apparently-hung window, and a scan that stopped at a ceiling
reported a short list indistinguishable from a small folder. `Scan` now takes a `CancellationToken`
and a progress callback and reports `Truncated`; the UI runs it behind the same live progress row and
Cancel the Gerber import uses, and says when it was cut short (`Gate2e`, `Gate2f`).

### 4. The chooser was skipped on a candidate the user had not pointed at

R-PL1-4's "pointed at a single file, skip the chooser and import it" was implemented as "skip whenever
the scan found one option", which imports something other than what was clicked and also removes the
only route to the folder picker. It now skips only when the clicked file IS the whole of the only
candidate.

### What the review did NOT find

**The readers themselves hold up, and that is the useful half of the answer.** One nine-terminal part
written out in all seven grammars produces the *same* terminal table from every one of them — same
order, same pad identifiers, same pin names, both `symPinNum` indirections followed — and 23 of 23
candidates across four parts import with no refusal and no exception. Multi-section parts remain the
one visible limitation, and they are named rather than merged (R-PL1-23).


## Phase PL2 — COMPLETE: component library import, breadth (2026-09-05)

`docs/sonnet-briefs/brief-PL2-component-library-breadth.md`. Five more formats behind PL1's single
**File ▸ Import ▸ Component…** — no new menu item, no new cell shape, no second import path. The
classifier is the only thing widened (§5); everything below `ComponentPart` is PL1's, unchanged.

Gate: `dotnet test tests/Ui.Tests` and `tests/Firewall.Tests` (10), both green. 41 new tests in
`ComponentImportBreadthTests`, covering the brief's §7 items 1-16.

### 1. All five landed — and the return went flat before the first one, which the owner overrode

**R-PL2-3 asked for the return to be measured after each format and for the phase to stop when it
went flat. It was flat at the start.** PL1's own completion note (§6, above) already concluded PL2
was speculative breadth, and PL2 §1 concedes the same in its opening paragraph: every format here
sits alongside one PL1 already reads, so no part becomes reachable that was not reachable before.
That was put to the owner before a line was written, with the sizing below, and the decision was to
build all five as insurance. **Recorded because "we measured and built anyway" is a legitimate
outcome and "we never measured" is not.**

What PL2 does buy, and it is the honest whole of it:

- **A library folder that happens to hold none of PL1's four now imports** instead of being refused
  by name (R-PL2-1). That is the entire value proposition, and it is about which format a folder was
  assembled with, never about which parts exist.
- **The chooser's "text formats circuitRF has no reader for" count drops sharply** on a folder
  carrying several of these, because five of its formats stop being noise and one — the encrypted
  `.hkp` twins — stops being reported at all (R-PL2-7).

**The cost was HIGHER per format than PL1's, not lower, and for a reason worth keeping.** PL1's §2
records that its footprint half cost 94 lines because `.kicad_mod` *is* the board format and
`PcbReader.ReadFootprint` already existed. **No PL2 format shares that lineage.** Each states its own
pads, outlines and layer numbering, so each needed its own footprint geometry reader as well as its
own symbol reader and its own map join. `ComponentArtwork.cs` (305 lines) exists purely to stop that
being written five times: readers emit neutral pads/paths/circles and it alone builds the
`PcbFootprintCell`, synthesises the layer table and converts to DBU.

Measured, in lines:

| Format | Reader | Lines |
|---|---|---|
| shared | `ComponentArtwork` + `ComponentFootprintBuilder` | 305 |
| `.p`/`.d`/`.c` | `ComponentRecordsReader` | 547 |
| `.hkp` set | `ComponentHkpReader` | 619 |
| `.PLX`/`.DSL` | `ComponentPlxReader` + `ComponentPlxSexpr` | 370 + 180 |
| `.cxf` | `ComponentCxfReader` | 359 |
| `.scr` | `ComponentScrReader` | 417 |

### 2. The `symPinNum` indirection (R-PL2-12), and its undocumented twin in the `.hkp` set

**The brief flags this for `.PLX` only. It is present in the `.hkp` set too, and the brief does not
say so** — which is the single most useful thing this phase learned.

- **`.PLX`/`.DSL`**: `compDef` states one `compPin` per pad, each carrying a `pinName` and a
  `symPinNum` that is *not* the pad number. Followed always. **The `padPinMap` cross-check never
  disagreed on a well-formed file** — it is a redundant restatement, and every file that parsed at
  all had the two in agreement. It is kept because a disagreement is the exact signature of the bug
  this rule exists to prevent, and `Gate9b` proves the refusal fires by feeding it a fixture whose
  two spellings contradict each other on one pad.
- **The `.hkp` twin**: the part file states the map as three parallel lists (SwapIDs, PinNames,
  PinNumbers) joined by POSITION, **ordered by pad**; the symbol file numbers its own pins in
  DRAWING order and carries its own name/number text records keyed by that ordinal. **The two orders
  are different.** Indexing the part file's lists by a symbol pin's ordinal yields a fully populated,
  correctly-shaped, wrongly-wired part. The symbol file's own text records are authoritative for the
  symbol; the two maps are cross-checked as sets and a genuine contradiction is a refusal.

**How both are proven**: `Gate3a` runs one fixture part through all five grammars and asserts one
map string. The fixture's symbol is drawn in the order ALPHA, DELTA, BETA, GAMMA, THERMAL while its
pads number 1, 2, 3, 4, TPAD — so an ordinal join produces `1=ALPHA 2=DELTA 3=BETA 4=GAMMA` and the
correct answer is `1=ALPHA 2=BETA 3=GAMMA 4=DELTA`. Nothing but that assertion separates them.

**The indirection is frequently the identity**, which is the trap inside the trap: a part whose
symbol happens to be drawn in pad order has `symPinNum == padNum` throughout, and a reader that
notices this on the file in front of it and takes the shortcut is correct until it is not.

### 3. Was the `.hkp` symbol grammar worth its cost? Yes — and for a reason that is not its content

It is the most work of the five (two grammars behind one extension, four files, a two-step padstack
name indirection) and it is the only format here whose cell file **names its layers semantically** —
`ASSEMBLY_OUTLINE`, `SILKSCREEN_OUTLINE`, `PLACEMENT_OUTLINE`. Every other format in this phase
numbers its layers and ships no legend, so every other reader carries a `RoleOf(int)` table of fixed
meanings plus an R-PL2-14 report for the rest.

**That makes this format the only one whose layer assignment is not a claim.** If a future format is
being sized, that property is worth more than its richness: a numbered-layer format's `RoleOf` table
is the part of its reader that can be quietly wrong for years, because artwork on the wrong
documentation layer still looks like artwork.

The symbol grammar specifically was worth it for a second reason — being self-sufficient, it is what
makes the cross-check in §2 possible at all. A format that stated its map once would have offered no
way to catch the ordinal trap.

### 4. The separate S-expression tokenizer (R-PL2-11): 180 lines, 71 of them code

`ComponentPlxSexpr.cs` is 180 lines total and **71 non-blank, non-comment lines**. The brief
estimated ~150 and asked for the measurement so the decision could be re-judged rather than
re-argued: it came in under, and the two dialect quirks that forced it are both inside the ATOM,
which is the one part of `PcbSexpr` a caller cannot parameterise — commas *inside* coordinate atoms
(`(pt 0, -100)`) and unit words *after* numbers (`(pinLength 300 mils)`). Widening `PcbSexpr` would
have put a foreign dialect's quirks into the reader L4d's board import depends on, to save 71 lines.

### 5. The handedness rule here is the INVERSE of PL1's, and that is the sharpest edge in the phase

`PcbUnits.Y` negates because the board format is +y down. **Every format in this phase is already
+y up**, so the footprint half passes Y through untouched — and calling `PcbUnits.Y` out of habit
mirrors the whole land pattern. The reason is structural rather than incidental: the board format is
a PCB editor's own on-disk frame (screen convention), while these are library artwork in the
drafting convention, which is what `.clay` uses.

`Gate3c` holds it shut over a fixture whose pads sit at +30 and +10 mil and nowhere below the axis —
a land pattern symmetric about its X axis imports identically whether the flip happened or not,
which is PL1 §3's trap pointing the other way. The symbol half is unaffected: readers hand over +y up
and `ComponentImport.FlipY` does the `.csym` flip downstream, exactly as PL1's readers do.

### 6. Two things the format documentation does not tell you, both found by replaying counts

- **The `.d`/`.c` decal header declares TWO counted text runs, and they are not adjacent.** `labels`
  precede the drawn pieces and `texts` follow them. Reading only the first — the obvious mistake,
  since the two are spelled identically — leaves the cursor `2 × texts` lines short, and the pad
  stacks are then parsed out of the middle of a free-text label. This is R-PL2-4's exact failure mode
  and the geometry that comes out of it looks entirely plausible.
- **A `.scr` restates its whole `Connect` map once per land pattern it edits.** A part with three
  density variants states the same joins three times. They are one map; `ComponentTerminals` reads
  the table as a set of joins, and a triplicated entry misreports the pin count of every part that
  ships variants (`Gate12b`).

### 7. R-PL2-18 vs. the file formats' own magic — resolved, and worth knowing

**Three of these five formats carry a commercial product's name inside the banner a reader must
match to classify the file at all** — root `CLAUDE.md`'s "Commercial Vendor References" rule forbids
that name anywhere in the repo, including as a string literal, and names `.kicad_pcb` as the only
standing exception.

Resolved without an exception: **the banners are matched on their vendor-free substring**, which is
just as specific in practice. `-LIBRARY-PART-TYPES-`, `-LIBRARY-PCB-DECALS-` and
`-LIBRARY-SCH-DECALS-` for the triple (anchored to a `*`-prefixed first line); `_LIBRARY_ASCII` and
`_INTERMEDIATE_ASCII` for the two S-expression extensions, which is also exactly what distinguishes
them from each other and therefore all the banner was ever needed for. The synthetic fixtures carry
invented prefixes in the same slot and classify identically.

**The obvious gate for this — scanning new files against a list of tool and manufacturer names — is
itself forbidden**, and that is not a technicality: the rule says "not even as a glossery of names to
filter out", so a test storing that list is the leak it claims to prevent. It was written that way
first, and removed. (It was also blunt enough to be wrong twice over: an early draft matched the
plural of "pad" against this codebase's own vocabulary, and the word "librarian" against a
pre-existing note about a site librarian handing out a starter workspace.)

What replaced it asserts the property that makes a leak impossible rather than enumerating leaks:
`Gate15a` requires every banner constant to BEGIN with the separator that follows a product word, so
none of them can spell one; `Gate15b` requires each fixture's banner to carry the invented prefix and
to still classify, which is the proof the product word was never needed; `Gate15c` pins the
extensions and the family names.

### 8. What still cannot be imported, by category

- **Proprietary binary containers** — schematic and PCB library binaries, and the packaged project
  files that wrap them. Reported as binary formats in the skipped summary. Out of scope by §5, not
  by accident.
- **Three-dimensional models** — reported as such, by count.
- **Dimensioned drawings** — PL1 R-PL1-30 stands and PL2 §5 restates it: they carry no pad
  identifiers and no pin names, so they cannot satisfy the pin↔pad invariant and are listed in the
  skipped summary rather than offered as a component that would import wired to nothing.
- **Every PL1 limitation, unchanged and now five times as reachable**: no netlist and no simulation
  model, first section only for a multi-section part, first device variant only, no stackup, no
  derived symbols. PL1 §6 named multi-section parts and a package-variant chooser as the cheaper
  next step *because they affect files circuitRF can already read*; landing five more readers has
  made that argument stronger, not weaker — there are now more paths to a part whose second section
  is reported rather than imported.
- **No writer of any of these formats.** §5, and unchanged from PL1.

### 9. One reader-independent fact worth not re-investigating

Two formats describing the same part can disagree about the NAME of a pin, with neither being wrong.
Where a part has two pins that genuinely share a name, each exporter invents its own disambiguating
suffix, and two of them assign that suffix to opposite pins. Both files are internally consistent —
the cross-checks in §2 correctly stay silent — and the pads, coordinates and electrical map agree
exactly. **This is a property of the source files, not a reader defect, and it is not fixable from
this side.**


## Phase PL1 — COMPLETE: component library import (footprint + symbol + the map) (2026-09-05)

`docs/sonnet-briefs/brief-PL1-component-library-import.md`. One menu item — **File ▸ Import ▸
Component…** — that takes a file or a folder, scans for the files an import can use, and writes ONE
cell: a `.csym`, one `.clay` per density variant, the pin↔pad map shared by both views, the free text
the file states as read-only parameters, and a copy of the bytes each view was built from.

Gate: `dotnet test tests/Ui.Tests` — 11,797 tests green, 47 of them new — and `tests/Firewall.Tests`
(10, green). An earlier run of the same suite reported three failures on paths this change does not
touch (`LayoutSnapPrewarmTests`, `SharedLibraryConcurrencyTests`, `BrokenInstanceVisibilityTests`);
each passes in isolation and each counts filesystem calls against `CellStat.Freshness`'s time window,
so they are load-dependent rather than caused here. Recorded because the same three will surface again
under load.

### What the code does

| Piece | Where | Does |
|---|---|---|
| `ComponentClassifier` | `src/Design/Layout/Interchange` | classifies one file from its first 8 KB |
| `ComponentFolderScan` | " | walks a folder, classifies, returns ranked candidates + a skipped summary |
| `ComponentSymbolSexprReader` | " | `.kicad_sym` → `ComponentSymbolDrawing` (mils, +y up) |
| `ComponentSymbolLegacyReader` | " | `.lib` → the same |
| `ComponentLibraryXmlReader` | " | `.lbr` → symbol, package and the separate pin↔pad table |
| `PcbReader.ReadFootprint` | " | `.kicad_mod` (both epochs) → `PcbFootprintCell` |
| `ComponentTerminals.Build` | " | assigns `PortIndex` once, for both views |
| `ComponentRead` | " | reads one candidate into one `ComponentPart` |
| `ComponentImport` | `src/Ui/Layout` | reconciles layers, writes the cell folder |
| `ComponentImportChooserDialog` | `src/Ui/Views/Dialogs` | shows the ranked list |

### 1. The spike's four findings, as measurements

**(1) R-PL1-17's exact-mil mapping HELD, and nothing is fitted.** `SymbolModel.cs` states 100 local
units per connection-grid square P and `DsnSymbolReader.PinGrid` is `100.0`, so one local unit is one
mil. The scale handed to `KitTemplateSymbol.BuildFromDrawing` is the literal `1.0`
(`ComponentImport.SymbolScale`) — not `ChooseKitScale`, not `ChooseScale`, not clamped. The two symbol
epochs of the gate fixture agree exactly: a pin stated at `2.54 mm` and the same pin stated at `100`
mil both land on local 100, every pin coordinate is a whole multiple of 100, and the two epochs' nine
pins compare equal on X, Y and PortIndex (gate 8). The fallback the brief allowed for — a fitted scale,
at the cost of imported and hand-drawn symbols no longer sharing a grid — was not needed.

*Not covered by that:* a part drawn on a 50-mil pin grid would collide two pins on circuitRF's 100-mil
connection grid. `ComponentImport.BuildSymbol` counts the pins the snap moved and reports the count, so
it is not silent, but no fixture exercises it — the count is a report rather than a tested behaviour.

**(2) The synthesised two-copper-layer table expands the wildcards exactly as R-PL1-13 predicted.**
`PcbReader.SynthesiseFootprintLayerTable` declares `F.Cu`/`B.Cu` as `signal` plus
`PcbLayerNaming.TechnicalRows` as `user`; `ExpandLayerSpec` then resolves `*.Cu` → 2, `*.Mask` → 2,
`*.Paste` → 2, and a through-hole pad lands on exactly `F.Cu`, `B.Cu` and `Drill` (gate 11c). The
existing `ExpandLayerSpec` needed no change — it already keys on the table's TYPE word rather than on a
name or an ordinal range, which is what made a synthetic table work.

**(3) Every pad shape the S-expression format can state is already expressible.** Read out of
`PcbReader.BuildPadShape`: `circle`, `rect` (cardinal and non-cardinal), `oval`, `roundrect` (including
`chamfer`), `trapezoid` (including `rect_delta`) and `custom` (anchor UNION every primitive, plus each
primitive's own pen width) all map onto `LayoutShape`. **Nothing was found that `LayoutShape` cannot
express.** The XML library's pad vocabulary — `round`, `square`, `octagon`, `long`, plus
`<smd roundness>` — is likewise covered.

**(4) Multi-section and multi-variant are handled by REPORTING, and both paths are exercised.** The XML
fixture states two `<gate>`s and two `<device>`s; the second of each is named in Messages and neither is
merged nor dropped (gate 14). The S-expression symbol format's `_<unit>_<style>` sub-symbol suffix is
read the same way — unit 0 and unit 1 at style 1 are the drawing, everything above is named.

### 2. How much of §5 was genuinely new code

**Almost none, and the honest number is 94 lines.** `git diff --stat` on the two files §5 touches:
`PcbReader.cs` +82 (the `ReadFootprint(text, dbuPerMicron)` entry point, its result record, the
two-root-tag guard and `SynthesiseFootprintLayerTable`) and `PcbLayerNaming.cs` +12 (exposing the
technical rows the writer already transcribes). **Zero lines of the existing footprint reader
changed** — `ReadFootprint(PcbNode, Ctx)`, `ReadPad`, `BuildPadShapes`, `Drill`, `ExpandLayerSpec` and
every `fp_*` graphic are called unmodified, and `PcbReader.Read`'s own root-tag guard was left as it
was (gate 11b asserts the board reader still refuses a footprint).

**This sizes PL2 downward, not upward.** The footprint half of a new format is the cheap half only
where that format's footprints are already the board format's; the ~3,300 lines this phase added are
almost entirely the symbol side, the pin↔pad join, the classifier and the folder scan — none of which a
new format reuses beyond `ComponentPart` and `ComponentTerminals`. Per format, expect roughly the size
of `ComponentSymbolLegacyReader` (275 lines) for a simple text grammar and of
`ComponentLibraryXmlReader` (697) for one that carries its own packages and layer table.

### 3. The two-flip trap — how each was proven, and what would have missed it

**Both halves of this import negate Y, for different reasons.** The FOOTPRINT flips because its source
is +y down and `.clay` is +y up (`PcbUnits.Y`, unchanged from L4d). The SYMBOL flips because the source
symbol formats are +y **UP** and `.csym` is +y **DOWN** (`SymbolModel.cs`: "+x right, +y down (screen
convention)"). Reasoning "the layout is y-up so the symbol must be too" gets the symbol backwards, and
it then renders upside down while the footprint beside it renders correctly.

Proven as two independent tests over ONE fixture asymmetric on both axes in both views:

- `Gate7a` asserts the footprint's L-shaped silkscreen outline coordinate by coordinate — 14 numbers,
  Y negated and X untouched.
- `Gate7b` asserts, separately, that a symbol pin stated at `+7.62 mm` lands at `−300` and that the
  symbol's asymmetric corner mark keeps its handedness.

**What would have missed it:** any fixture whose art is symmetric in one axis — a plain rectangular
body with pins on the left and right. It imports identically whether the flip happened or not. The same
applies to the arc: `Gate10` asserts the SWEEP DIRECTION (`−90°` in the file becomes `+90°` here).

A third part of the same trap: **the arc's ANGLES must NOT be negated alongside its coordinates.**
`KitSymbolArc` states its angles counter-clockwise while circuitRF's arc primitive measures them
clockwise, and `KitTemplateSymbol.Convert` already flips both fields — which is the sign change
negating Y calls for. Flipping them in `ComponentImport.FlipY` as well would cancel it out and leave a
correct-looking arc drawn from the wrong end. `FlipY` therefore negates `Cy` only.

### 4. What an imported part still cannot do — limitations, not omissions

- **No netlist, no schematic view, no simulation model** (R-PL1-6). `Gate17c` asserts the `schematic/`
  folder is empty and `PrimarySchematic` is null. Placing one and elaborating will not produce a device.
- **No multi-section parts.** The first section is imported; the rest are named.
- **No package-variant chooser.** The first `<device>` is imported and the rest are named. Density
  variants of ONE pattern are the exception and become sibling `.clay` views (R-PL1-25).
- **No stackup, and nothing invented in its place** (R-PL1-27). `ComponentImport.ImportResult` has no
  stackup field, the destination technology's own is untouched (`Gate16`), and one Messages line states
  what an EM run still needs.
- **No derived (`extends`) symbols.** Reported; only what the definition states itself is read.
- **No binary formats, no 3D models, and no DXF fallback** (R-PL1-30) — a dimensioned drawing is
  classified as one and listed in the skipped summary.
- **No writer of any of these formats** (§13).
- A pin bonded to several pads gets one symbol pin on the FIRST of them; the rest are terminals with
  copper and no drawn pin, reported as such.

### 5. The folder chooser's ranking — UNVERIFIED against real files, and that must stay visible

> **ANSWERED 2026-09-05, and the predicted cost was real** — see "Phases PL1/PL2 — post-implementation
> review" at the top of this file, §2. The paragraph below correctly names the opposite error
> ("a folder holding two unrelated parts in the same format is offered as one candidate") and
> understates it: for a multi-FILE set the reader takes the first file of each kind, so the other parts
> were dropped with nothing said. Grouping now happens before ranking. §6's own conclusion below still
> stands on its own terms.

**§15's question 5 cannot be answered from what this phase tested, and is not.** The ranking is
exercised only against the synthetic tree `Gate2` builds (`toolA`…`toolE`, five subfolders, two
readable). What that test DOES establish is the property the ranking rests on: **classification is by
content, never by name**, proven the hard way — the footprint sits in a folder called `symbols` under
the name `part.txt` and is found, while the file named `part.kicad_sym` holds prose and is reported as
unreadable text.

Two decisions inside the ranking that only real files would settle:

- **Candidates are grouped by FORMAT FAMILY, not by folder.** A symbol file and the footprint files it
  pairs with may sit in sibling folders, so grouping by directory would split one importable component
  into two incomplete candidates. The cost is the opposite error: a folder holding two unrelated parts
  in the same format is offered as one candidate.
- **Confidence order is S-expression, then XML, then the older text symbol format**, on the ground that
  the first reuses L4d's whole footprint path. That is a claim about reader maturity, not about files.

### 6. Is PL2's breadth worth building? Not on this evidence

PL1 ships `.kicad_sym`, `.kicad_mod`, `.lib` and `.lbr`. PL2's own §1 already concludes its breadth buys
nothing beyond that, and this phase produced no evidence to the contrary — it produced no evidence about
real files at all. The measurement that would change the answer is the one §5 above says is missing: run
`ComponentFolderScan.Scan` over several real component folders and count how many rank
`SymbolFootprintAndMap` at the top. Until that number exists, PL2 is speculative breadth, and the
cheaper next step is one of PL1's own limitations — multi-section parts, or a package-variant chooser —
both of which affect files circuitRF can ALREADY read.

### Two smaller things worth knowing

**A `.lbr`'s DOCTYPE would have read as a corrupt file.** `XDocument.Parse` prohibits DTDs outright and
throws "For security reasons DTD is prohibited" on the second line of an ordinary library.
`ComponentLibraryXmlReader` parses through an `XmlReader` with `DtdProcessing.Ignore` and a null
`XmlResolver` — tolerated, never resolved, and no entity in it can expand.

**The XML format is recognised by its STRUCTURE, not by its root element's name.** `<library>` wrapping
`<packages>`/`<symbols>`/`<devicesets>` is the test, in both the classifier and the reader, so a file
whose root element is named anything at all still imports. The fixture's root is `partlib`, which is
what proves it.

## A technology imported from Gerber greeted the user with 22 validation messages describing 2 facts (2026-09-04)

Owner report: opening a `.ctech` imported from a real 21-file Gerber set filled the Technology
editor's banner with a wall of warnings, and the Messages panel with the same wall on every workspace
load. Measured on the reported file: **22 messages, 2 facts.** Three separate causes, in the order
they were found.

### 1. Every layer claimed the Gerber suffix "art", and that is not a cosmetic clash

The board was written as twenty `.art` files (`GERB_01_Top_Layer.art`, `GERB_02_Layer_2.art`, …) — an
ordinary convention where the extension means "artwork" and the STEM says which layer. R-L4g-7 records
the source extension as the minted layer's `GerberSuffix` *unconditionally*, so all twenty claimed
"art", and the pairwise collision check reported that nineteen times.

**A `GerberSuffix` is a layer ALIAS, and a shared extension cannot be one.** The nineteen warnings were
the small half of it; the alias was already broken in both directions:

- `GerberExport.Write` names each file `<cell>.<suffix>` and disambiguates a repeat as
  `<cell>.<suffix>_<layer>_<datatype>` — so the very names R-L4g-7 exists to preserve were not
  preserved for nineteen of the twenty layers anyway.
- `GerberLayerIdentity.SuffixOwner` (rung 2 of the identification cascade) is a `FirstOrDefault` over
  the destination technology's layers. **A re-import of that same set against that technology would
  have identified all twenty files as "Top Copper"** — every one of them then falling into
  `BuildSourceLayers`' "resolved to the same technology layer as an earlier file" branch.

Fixed at the source: `GerberImport.AliasableExtensions` records the extension only for an extension
that identifies exactly ONE file in the set. Everything else is left unset, where the export's own
`G{layer}_{datatype}` fallback is at least unique. The rule is per-extension, not per-set — a real
board of many `.art` files beside one `.rou` drill file keeps the `.rou` alias, which is what the
reported file's re-import now produces. A set of distinct extensions (`.gtl`/`.gbl`/…) is untouched,
which is what L4h's byte-identity round trip runs on, and R-L4g-7's own gate
(`EachImportedLayerCarriesItsSourceExtensionAsItsGerberSuffix`) is unchanged.

### 2. The via reported three problems, none of which could be answered

A Gerber set with no job file carries **no stackup information anywhere in the files**, so the import
mints a `StackupKind.Via` entry (the drill layer must be marked as one) into a stackup with no
conductor entries at all. `TechValidation` then reported the span-from end, the span-to end, and
`Fill = Plated` with no wall thickness — three messages, all of them unfixable until conductors exist,
and none of them naming the reason they cannot be fixed.

The import is right not to invent a substrate (its own comment says so, and that stands). The
validator now says the one true thing — the stackup names a via but no conductor layers — and skips
the three checks that condition makes unanswerable. **Scoped to that condition only:** a technology
that HAS conductors and names the wrong one is a typo and is still caught per end.

### 3. One message per shared alias, not one per additional claimant

`ValidateInterchange` reported collisions pairwise: first-vs-second, first-vs-third, … Now it groups
by the shared VALUE and reports it once, naming up to three layers and counting the rest
(`20 layers ("Top Copper", "Inner 1", "Inner 2" and 17 more) share the Gerber suffix "art"`). A PAIR —
the case someone actually mistyped — still names both, which is what the existing gate asserts.

### `Validate` grew a sibling, and kept its own shape

`TechValidation.Analyze` returns `TechProblem(Area, Message)`, attributing each problem to the editor
tab whose fields would fix it. `Validate` is now a projection to the messages alone, unchanged in
signature — the ~30 existing call sites and tests were not touched. What the editor does with the area
is in `src/Ui/RESOLVED.md`.

### Measured

The reported file, before: **22**. After: **2** (one Stackup, one Interchange; none on Layers). A
fresh import of the same folder: **1** — the stackup one, which is a true statement about what a
Gerber set without a job file can carry.

Gates: `tests/Ui.Tests/TechValidationNoiseTests.cs` (all three collapses, plus the two scope limits),
and two additions to `tests/Ui.Tests/GerberImportTests.cs` for the shared-extension rule.


## `CellHierarchy.InstanceBbox` takes an optional layer filter, and a filtered answer is never cached (2026-09-04)

Added for the clipboard's graphic export, which sizes its page from what will be PAINTED and therefore
must not be sized by a layer the user turned off (owner report; the full account is in
`src/Ui/RESOLVED.md`'s layout-editor misc-round entry).

**Null is the default and means "measure everything", which is what every interactive caller wants.**
The spatial index, hit-testing and the renderer's LOD decision all cull against where geometry IS, not
against what is currently painted — a hidden layer's shapes still occupy their coordinates, and a
picking query that pretended otherwise would be wrong in a way that only shows up when a layer is
toggled. An EXPORT is the one caller with the opposite need.

**A supplied filter bypasses the `ShapesBbox` memo.** That cache is a `ConditionalWeakTable` keyed on
the `LayoutView` REFERENCE alone — it has no room for "which layers were asked about" — so storing a
filtered result would hand a hidden-layer-less bbox to the spatial index the next time anything asked
without a filter, and the geometry would simply stop being pickable. The filtered path unions directly
and stores nothing. It is O(shapes) rather than O(1), which is why the parameter exists at all instead
of being unconditional: the memo is there because a generated cell can hold a six-figure via field and
`InstanceBbox` is called per placement, per frame.


## `LayoutPersistence.LoadFromFile` is interruptible, and the hooks are on the shape loop (2026-09-04)

An overload takes a `CancellationToken` and an `Action<int,int>` progress callback, for the caller
that has moved the read onto a background thread and owes the user a progress row and a Cancel (see
`src/Ui/RESOLVED.md`'s "The whole UI crawled…" entry for what asked for it).

**Both hooks land on the SHAPE LOOP, and the placement is the finding.** Reading a layout is not
proportional to how big the file looks: `LayoutClipper.EnsureValidHoles` runs over every shape, and on
a Gerber-imported board — thousands of composited pours, each with hundreds of holes — that is seconds
to tens of seconds, against a JSON parse measured in hundreds of milliseconds. So the loop is both the
only place a cancel can land promptly and the only phase with an honest denominator; the parse ahead of
it is one indeterminate step.

Every 256 shapes, not every shape: at these counts a callback and a token read per shape would cost
more than the normalization they are reporting on, and a progress bar cannot show more than a few
dozen steps anyway.

**Cancelling throws rather than returning a half-built view.** A partially loaded layout is
indistinguishable from a corrupt one to everything downstream, and this is a document the user is
waiting to see — not a partial result worth salvaging.

`Deserialize(string)` and the parameterless `LoadFromFile(string)` are unchanged in behaviour; the
version check moved into a private `ParseFile` so the interruptible path could put a cancellation point
between the parse and the shape loop without a second copy of that rule.

## The moved-cell forwarding record, and why the redirect lives in `ExternalCellRef` (TM2, 2026-09-04)

`brief-tree-move-2-moves-across-a-shared-library.md`. The `src/Ui` half — the report, the three surfaces,
the adoption gesture and the measurement — is in `src/Ui/RESOLVED.md`; this is what landed on the
framework-free side, and it is the half that makes `circuitrf convert` and `circuitrf em` resolve a
moved reference with no code of their own.

### `Workspace/MoveRedirects.cs` — reading the record, not just writing it

TM1 already wrote `.cmoves`. TM2 adds `Resolve` (longest-prefix match, chained, hop-capped and
cycle-guarded), `RootAbove` (which root owns a reference) and `CanRecord` (whether the safety net can be
laid at all), plus an atomic replace on `Append` and a memo on both read paths.

**`RootAbove` cannot be `WorkspaceRootFinder.WorkspaceDirOf`, although R-tm2-8 says it can.** That helper
walks up for a `.cws` and answers null for a bare-directory library — and a bare-directory library is the
case the whole feature is about (`WorkspaceScanner.ResolveLibrary` accepts one; it is R-tm2-5's decisive
reason for `.cmoves` being a file of its own rather than a `.cws` section). Using it would have produced
something that passed every test written against a workspace and did nothing in the field. `RootAbove`
walks up for a **`.cmoves`** and **stops at the first `.cws`, inclusive**: that directory is a workspace
root, and a root above it owns a different tree and cannot have recorded a move of this cell. That stop is
also what bounds the walk on a path with no project above it.

**`CanRecord` is a real write, not an attribute read** — SL2 R-sl2-1's rule, because a share ACL, a POSIX
mode and a read-only mount are all invisible to `File.GetAttributes`. It additionally opens an existing
`.cmoves` for write, which is the case a create-a-probe-file test misses entirely: the DIRECTORY is
writable and the FILE is not, so the probe says yes and the record is then lost. It leaves nothing behind
— in particular no empty `.cmoves` for a move that is refused for some other reason.

**`WorkspaceLock` is NOT the instrument R-tm2-15 asks for**, and §8 predicted this. It is advisory by
design — `Take` *overwrites* a lock someone else holds, and its own doc comment says treating it as
authoritative would produce a stale file that locks out a team — and it is per-workspace, with no notion
of a library root that is not a workspace. Gating a write on it would be reading it as the thing it
refuses to be.

### `Workspace/ExternalCellRef.cs` — the one resolution point, now with two extra jobs

The redirect goes in `ResolveCellDir` and nowhere else. That is this type's own standing rule — a call
site that splits the reference forms itself is a call site that will be missed — and it is what gives the
CLI the behaviour for free. **Checked rather than assumed:** every stored-reference resolution site in the
repo does route through it, including `CellLayoutResolver`, `PcbExport`, `GdsiiExport` and `DxfExport` on
this side. The list is in `src/Ui/RESOLVED.md`.

**The order is existence-then-redirect, and the existence answer is now RETURNED.** `ResolveCellDir` has
to ask `Directory.Exists` for R-tm2-8's step 2, and `CellSymbolResolver` was asking the identical question
three lines later — so the four-argument overload hands the answer back rather than letting the caller
re-ask. Asking twice cost a **fifth** filesystem round trip per referenced component per edit in the
uncached world, which is exactly the number SL4 R-sl4-6's gate pins, exactly so it cannot drift up one
call at a time. It went red, which is the gate doing its job. With the cache on, both the old and the new
code cost 4 cold and 0 warm.

`MoveRedirects.Resolve`'s own existence checks go through `CellStat` too, so a redirect's cost is counted
rather than invisible — and safely, since `CellStat` never caches a negative and a dead-end rung of a
chain must be re-asked.

## What a cell reference costs, counted — and an advisory lock that claims no authority (SL4, 2026-09-03)

`brief-shared-library-4-concurrency-and-latency.md`. The `src/Ui` half of this — the measurement table, the
tree's referenced-subtree rule, and the design decisions behind both — is in `src/Ui/RESOLVED.md`; this is
what landed on the framework-free side.

### `Cells/CellStat.cs` — the counting seam, and a cache that is opt-in per CALL SITE

Every filesystem call on a cell reference's resolution path goes through one type, so its cost is a **number**
(`CellStat.Calls`) rather than an intuition. That is the brief's own rule (R-sl4-6) and the repo's: a timing
assertion measures the machine, flakes under parallel test load and inverts under a debug build; a call count
describes the algorithm and reads the same everywhere. Measured: **4 calls per referenced component per edit**
— `Directory.Exists` on the cell folder, `Directory.Exists` + `Directory.GetFiles` on its `symbol/`, and the
primary's mtime, all before the symbol cache can be consulted, because the mtime IS its key — and 6 when the
folder holds more than one symbol.

Positive answers are then cached for **`CellStat.Freshness` = 2 s** (R-sl4-7), which is the one guarantee the
shared-library series traded away and is stated on that field in full. **A negative is never cached**
(R-sl4-8): a cell folder that was not there, a `symbol/` with no `.csym` in it, an mtime for a file that is
not present. Caching "not found" for even a second turns a share that blinked into a design full of
Not-Found glyphs that persist after the network recovers.

**The trap, and it is a boundary rather than a value.** `CellFolder.ResolvePrimary` is shared by
`CellSymbolResolver.Resolve` AND by the project tree's own scan (and by every cell node view model, three
times each). Caching *inside* it silently applied a bound justified for a network wire to a file the user had
just written themselves — `WorkspaceScannerTests.Rescan_ContradictionAppearsWhenPrimaryFileDeleted` and its
restore twin caught it. `ResolvePrimary` therefore takes `useStatCache:` (default **false**, which is exactly
the pre-SL4 behaviour) and only the reference resolver passes true. Both callers still COUNT — the counting
seam and the caching policy are separate questions, and conflating them is what went wrong the first time.

Dropped by `WorkspaceRootFinder.InvalidateCache`, which now clears four memos on one lifecycle: the walk-up,
`ExternalCellRef`'s alias table, `WorkspaceWritability`'s probe and this. A memo with a lifecycle of its own
is the one that goes stale.

### `Workspace/WorkspaceLock.cs` — advisory, and it must read as advisory

`.crf-open.json` beside the `.cws`: user, host, pid, time. Written when the workspace is opened **and is
writable** (a read-only workspace takes none and needs none — nobody can write it), removed on close, and
released only when it is ours.

- **No open file handle** (R-sl4-4). `CrashReporter` holds one with `FileShare.Read` so an exclusive open by
  a probe proves ownership, and the single-instance check uses the same idiom; both are right **locally**.
  Those guarantees do not survive SMB, NFS or a dropped connection, and a handle-based lock over a share
  fails in the direction that produces a confident false statement about another person.
- **Two independent staleness rules, and the host scoping is load-bearing.** Rule one is *this host* plus a
  pid that is not running; rule two is age, 8 hours. Scoping rule one to this host is not tidiness — a lock
  from another machine, checked against local pids, reads as abandoned whenever that pid happens to be free
  here, which is a confident "they have gone" about a session that has not. That is the exact false statement
  R-sl4-2 exists to bound, and it was observed once during the work before the scoping was added.
  "Cannot tell whether a process is running" reads as ALIVE, deliberately.
- **A malformed lock file is no evidence at all**, and is ignored rather than treated as a refusal. Refusing
  to open a workspace over an unreadable file is precisely the stale-file failure the design forbids.

### `Workspace/WorkspaceWritability` — read-only by CHOICE reuses read-only by permission

`OpenReadOnlyThisSession(root)` marks a workspace and everything beneath it unwritable for this session,
checked before the memo and before the probe. It is a **prefix** rule, not set membership, because the
question is asked about a document's own directory far more often than about the root (R-sl2-4) and marking
only the root would leave every file inside it saveable.

This deliberately reuses SL2 rather than adding a second concept. Everything a read-only workspace does — the
`.cws` write choke point skipping silently, Save disabled with a reason, Save As on quit, the provenance
band, the generated-cell wipe not running, the PCell refusal naming the workspace — is already built and
already tested, and "a workspace we have chosen not to write" wants identical behaviour from every one of
them. A parallel flag would have been the one that is true in fourteen places.

## Writability is DISCOVERED, and `.cws` writes now have a choke point (2026-09-03)

`brief-shared-library-2-read-only-workspaces.md` R-sl2-1/-2/-3/-6. Two changes here; the behaviour that
hangs off them is in `src/Ui/RESOLVED.md`.

**`WorkspaceWritability` sits beside `WorkspaceRootFinder`, not in `src/Ui`,** for the ordinary reason:
`src/Cli` writes workspaces too and cannot reference Avalonia. It answers "can a file be created in this
directory?" by creating one and deleting it. `File.GetAttributes` reports the DOS read-only bit and says
nothing about a share ACL, a POSIX mode or a read-only mount option; `Directory.Exists` says nothing at
all. **The only portable answer is to try**, which is why there is no cheaper implementation waiting to
replace this one.

Its memo is dropped by `WorkspaceRootFinder.InvalidateCache` rather than on a lifecycle of its own —
that call already drops the ancestor walk-up and `ExternalCellRef`'s alias table, and a third memo that
had to be invalidated separately would be the one that went stale.

**`WorkspacePersistence.SaveToFileAtomic` returns `bool` and is now the guard.** It skips the write and
returns `false` when the containing directory is unwritable. The guard is at the LOWEST level on purpose:
there were fifteen call sites and no choke point (reads have had one — `TryLoadCws` — since the
beginning), and a rule fifteen callers have to remember is a rule that is true in fourteen places. A
sixteenth site inherits it without knowing the rule exists.

**A trap for anyone adding a `.cws` writer:** `SaveToFile` (non-atomic) is still public and is NOT
guarded — it exists for test fixtures and for the doc-fixture generator, which build throwaway workspaces
under a scratch directory. Production code must use `SaveToFileAtomic`;
`ReadOnlyWorkspaceTests.EveryCwsWriteInProductionCodeGoesThroughTheChokePoint` is what says so.

**`FileOptions.DeleteOnClose` is not a crash guarantee on Unix — measured.** It is a kernel flag on
Windows; on Unix .NET emulates it by unlinking at handle close, and a `SIGKILL` closes no handles, so
a process killed mid-probe leaves the file. `Probe` therefore sweeps stale `.crf-write-probe-*` files
(age cut-off five minutes, so a concurrent probe is never touched) rather than trusting the flag. It
matters because the project tree hides only `.DS_Store` and `*.source`, not dotfiles generally.

**A second trap, in the probe's failure mode:** `AtomicFile.WriteAllText` has never created the target
directory, so a `.cws` write into a directory that does not exist used to throw. It now returns `false`
silently instead, because a probe of a non-existent directory answers "read-only" — the same answer, for
the same underlying reason, delivered quietly. A caller that depended on the exception must check the
return value.

## `${NAME}` in a stored cross-workspace path, and why it lives here (2026-09-03)

`brief-shared-library-1-reaching-the-library.md` R-sl1-5/-8. `PathTokens` expands `${NAME}` from the
environment in the three `.cws` fields that name a location OUTSIDE the workspace —
`ReferencedWorkspaces[].Path`, `LibraryRefs`, `KnownFiles` — so a librarian can hand out a starter
workspace whose library reference works on every engineer's machine. One user's `Z:\eda\stdlib` is
another's `\\server\eda\stdlib` and a third's `/Volumes/eda/stdlib`; the alias indirection already meant
each user repaired that once, but a site-wide `.cws` template was impossible.

**It is in `src/Design/Workspace/`, not in `src/Ui`, and that is the load-bearing part of the decision.**
`ExternalCellRef.ResolveOtherRoot` already re-implements `WorkspaceRefs.Resolve`'s rule in three lines
rather than calling it, and its own comment says why: `WorkspaceRefs` is in `src/Ui`, on the far side of
the firewall, and a headless `circuitrf convert` or `em` run resolves these references too. A token
expander sitting in `src/Ui` would resolve a tokenised alias in the GUI and silently fail to in the CLI —
the two would disagree about what the same `.cws` means. Gated by a test that resolves a tokenised
`ws://` reference through `src/Design` types alone.

**Three traps, all of which produce a plausible wrong answer rather than an error:**

- **An unset variable must NOT expand to empty.** `Environment.GetEnvironmentVariable` returns null, and
  substituting empty turns `${CRF_LIB}/stdlib/v2.3/.cws` into `/stdlib/v2.3/.cws` — a ROOTED path that
  resolves to somewhere real on some machines and reports a missing folder on others. `TryExpand` returns
  false with the offending token, callers report a broken reference naming it, and nothing is ever
  half-expanded (an unset token in the middle leaves the whole string untouched).
- **One syntax on every platform.** `${NAME}` only — never `%NAME%`, never bare `$NAME`. A `.cws` travels
  between machines; a per-platform spelling resolves on the machine that wrote it and nowhere else.
- **A `CellRef` is never expanded.** It is the workspace-relative remainder and has no business naming a
  machine — a token there would be a second place a cross-workspace path can hide, which is exactly what
  the `ws://` alias form exists to prevent. `ExternalCellRef.ResolveCellDir` expands the alias's stored
  PATH and leaves the remainder verbatim, in both the `ws://` and the plain relative form.

Nothing ever WRITES a token: circuitRF writes a plain path, and a token is what a librarian or a site
template types by hand — the same treatment R-mw2-5 gives the raw relative `CellRef` (resolve it, never
produce it). There is deliberately no token *definition* mechanism: the environment is where a site
already configures this on all three platforms, and a second definition site would need precedence rules
of its own.

## The interchange stack moved here, and `circuitrf convert` is what it bought (2026-09-02)

The layout interchange readers and writers — GDSII, DXF, Gerber, Excellon and `.kicad_pcb`, ~16,700
lines across 61 files — moved from `src/Ui/Layout/Interchange` to `src/Design/Layout/Interchange`,
namespace and all. The `em` verb's own carve-out (`brief-cli-em-verb.md` R-emcli-1/R-emcli-4) is the
precedent it followed, including the rule that the namespace changes with the project.

The point of the move is the CLI: `src/Cli` cannot reference `src/Ui`, so a headless conversion had
to have the readers on this side of the wall. `src/Ui/RESOLVED.md` §"A headless import verb, and what
moving L4e-L4g to `src/Design` would cost" scoped exactly this and left it unattempted; the numbers
below are what it actually cost.

### 1. What had to move with it, and the one thing that could not

Seven `src/Ui/Layout` files went too, all framework-free as written: `LayoutFragment`,
`LayoutLayerMapping`, `FallbackPalette`, `LayoutViewport`, `PinInference`, `LayoutDesignFlatten` and
`LayoutTextFlatten`. None of them needed an edit beyond its namespace line, and none of their five
other consumers in `src/Ui` needed one either — `src/Ui/GlobalUsings.cs` already carries
`CircuitRF.Design.Layout`, so a type moving INTO that namespace is invisible to every file that used
it. The whole `using` churn across `src/Ui` was one added line for `…Layout.Interchange`.

**`LayoutTextOutline` was the one genuine obstacle, and `src/Ui/RESOLVED.md`'s scoping said it was:
it depends on Skia, so "GerberExport must NOT move".** That prediction was half right. SkiaSharp is
explicitly ALLOWED across the firewall (`tests/Firewall.Tests`: "headless 2D graphics is not a UI
framework"), so glyph geometry crosses fine; what does not is `SkiaFonts`, which loads the embedded
IBM Plex faces through Avalonia's `AssetLoader` and needs a live app host. So the split is not
import-here/export-there. It is **one line lower down**: `LayoutTextOutline` moved with everything
else and gave up only its font SOURCE, now a
`Func<LabelFontStyle, SKTypeface>? TypefaceSource` that `src/Ui` fills in from a `[ModuleInitializer]`
(`UiTypefaceInstaller`) and that falls back to `SKTypeface.Default` when nothing did.

A module initializer rather than a call from `App.Initialize` because `src/Ui` has three entry points
(circuitRF, harmonicaRF, wBond) and a startup step that must run in all three is a startup step
somebody eventually forgets in one.

**The consequence is real and is reported rather than hidden:** a label flattened headlessly is a
different SHAPE from the same label flattened in the app, because the glyph outlines come from a
different face. `LayoutTextOutline.HasEmbeddedTypefaces` is false in that case and `convert` prints a
note whenever it flattened a label without them.

`ResolveLabelAnchor` moved out of `LayoutRenderer` into `LayoutTextOutline` for the same reason it
was shared in the first place: the renderer draws a label with it and the flattener places glyphs
with it, and the property worth protecting is that those two can never disagree. One copy, in the
project both callers reach.

### 2. `convert` is one import and one export, and the intermediate is a real cell

Every reader lands on a cell folder plus a technology and every writer starts from one, so the
N x N table of conversions is not N x N pieces of code. A conversion whose target is `.clay` stops
after the import; every other one runs the import into a scratch directory and exports out of it
(`--keep-cells` keeps that directory, which is the way to see what a conversion understood).

Two things the GUI answers with a dialog had to be answered another way:

- **The layer-mapping dialog** — handed a null callback, every importer already falls through to
  `LayoutLayerMapping.BuildChoices`, which is the same default the dialog pre-selects. Nothing to
  decide; the CLI just does not pass one.
- **The drill-format prompt is a REFUSAL, not a default.** Leading versus trailing zero suppression
  differ by four orders of magnitude on identical text (L4f §2), so `convert` prints the inference,
  its evidence and the artwork cross-check, names the three flags that answer it, and exits 1 having
  created nothing. `--accept-inferred-drill-format` takes the inference as it stands.

### 3. A null destination technology silently drops every layer — measured, not reasoned

The first working conversion wrote Gerber files named `via.G-2_0` from a technology with **zero**
layers. The cause is not in the move: every importer reconciles the file's layers against the
DESTINATION technology and returns the ones it would ADD, and handed a `null` destination there is
nothing to compare against, so `LayersToAdd` comes back empty and the layers arrive as bare numeric
keys with no names, no colours and no `GerberSuffix`. A re-export then names its files from a
synthetic suffix.

The fix is one line — `destTech ??= new Technology { Name = name }` — and the reasoning is that an
EMPTY technology is the honest destination for a conversion that has none: every source layer is then
an unmatched row, which is exactly what it is. **This is the failure mode to remember whenever a
headless caller reuses an importer**, because nothing errors and the result looks structurally fine.

### 4. GDSII is the one format that cannot carry names through, and that is the format's doing

The same fix does nothing for GDSII, deliberately. `GdsiiImport` does not apply the
NoMatch → AddToTechnology default that DXF, board and Gerber import all do (L4b's own divergence, and
it was reasoned about name-keyed formats). GDSII identifies a layer by a NUMBER, so an import has
nothing to name it with: numbers come through exactly, names do not. `--tech` pointing at the
technology those numbers belong to is the answer, and it is documented as such rather than papered
over. The gate asserts a non-empty layer table for every source EXCEPT gdsii, and says why.

### 5. Two smaller things the matrix exposed

- **`$MODEL` is DxfReader's own name for model space**, not something anyone typed, and it reached
  the Gerber writer as a file stem: `$MODEL.gbr`. A DXF's drawing is named after the file, so that is
  what `convert` calls it; `--name` overrides.
- **A `--to clay` result is not shaped the same way for every source.** Gerber import puts its whole
  result inside an `ImportFolder` of its own (R-L4g-13) while the others create cells directly under
  the parent. That is a real difference between the importers, and `convert` does not normalize it
  away — the gate searches recursively rather than pretending otherwise.

### 6. The firewall's text gate fired, and it was right to

23 exception messages appeared "below the UI firewall" the moment the code crossed it — unchanged
sentences that have been in the tree since the importers were written. They are all format invariants
(a truncated GDSII record, a shape type no writer has a case for, an unbalanced macro expression),
which is the deliberate plain-exception case `user-facing-text-allowlist.txt` describes, so they were
added there under a heading that says they moved rather than being authored.

### 7. What the gate proves, and what it does not

`tests/Ui.Tests/ConvertCliVerbTests.cs`, 32 tests, 7 s, untagged and in the routine gate. It launches
the built `CircuitRF.Cli.dll` as a real process (EmCliVerbTests' pattern, for its recorded reason) and
checks all 24 ordered format pairs plus byte identity against the in-process `GdsiiExport` and
`GerberExport` calls the GUI's own File ▸ Export makes.

**A GDSII file is not byte-comparable raw**, and the first version of this gate only looked like it
was: BGNLIB and BGNSTR record when the library and each structure were written, so two writes of the
same design differ at byte 21 unless they land in the same second. It passed for an afternoon and
then failed on a second boundary. Masked by record type and named, the way `EmCliVerbTests` names the
Touchstone provenance line — everything else still compares byte for byte, which is the point.

**It proves the two sides agree and nothing more.** The matrix's sources are built by `convert`
itself, so a pair is tested against our own writer's output, not against a third party's dialect —
the same limitation L4h's round-trip gate states about itself (R-L4h-16), and for the same reason.

**Stale after this change:** the root `CLAUDE.md` source map still describes `src/Ui` as the home of
the layout interchange code and lists seven CLI verbs. Neither is true now. Left for the owner.

## MIM-7 — a dielectric that is patterned with its plate, so ONE MMIC technology serves both (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-7-one-technology.md`. The extraction half is here; the shipped-file
merge, the editor row and the documentation are `src/Ui/RESOLVED.md` §MIM-7. **`src/Engine` is
untouched — the refusal, the via z-integral and the kernel are exactly as they were.**

### The premise that was actually wrong

circuitRF shipped two MMIC technologies that differed only by a capacitor module, and MIM-2 measured
two real reasons for the split (`src/Ui/RESOLVED.md` §MIM-2): a capacitor dielectric between the
interconnect metals makes every Metal1-Metal2 airbridge post cross a dielectric interface — which
`PlanarKernel.CanSolve` refuses for the WHOLE RUN — and it sits on a Metal1 line as superstrate, so
Z₀ falls 2.8%.

**Both costs come from the film being in the medium of EVERY run, including runs with no capacitor in
them — and the 2.5D premise does not require that.** It forces "laterally infinite per RUN"; it says
nothing about which runs a patterned film belongs to. Physically the nitride exists under the plates
and nowhere else, and the honest per-run proxy for "this run has capacitors in it" was already being
computed: **is the plate conductor among the run's levels?** The extractor's default level selection
is "every non-ground conductor that carries artwork", so an interconnect-only layout answers no with
no configuration at all. It is also the kernel's own suggested remedy, verbatim in its refusal text:
*"…or remove the interface if it carries no physics."*

### The field, and the two halves of the rule

`StackupLayer.PresentWithLayer` (`string?`, a conductor entry's NAME) — additive, nullable, no
`.ctech` `FormatVersion` bump, meaningless on a non-Dielectric entry: the `SheetAt`/`SpanFromLayer`
pattern. `TechValidation` requires an existing, non-ground Conductor and refuses the field on a
non-Dielectric entry. **"Name the conductor directly ABOVE" is a RECOMMENDATION, not a validation
rule** — a tie further away is expressible and honoured, only harder to read — so it is stated in the
field's own documentation, the editor's tooltip and the user page rather than failed.

When the named plate is not in the run:

1. the film's band enters the medium as **air** — εᵣ 1, tanδ 0, µᵣ 1, thickness untouched, so every
   band above it keeps the height the process states;
2. **`SheetAt = Top` on the conductor whose band sits directly BENEATH the film is treated as unset
   for that run.**

**(2) is what makes the gate bit-identity rather than "close", and it is not a convenience.** MIM-6
put Metal1's sheet on the top of its band expressly so a plate gap reads 0.2 µm; with no film there
is no gap to read, and the pre-MIM-6 placement is the established baseline for interconnect. Without
the revert the same airbridge would extract at z = 103/106 instead of 100/106 — a plausible answer,
3% out, to a question about a stack with no capacitor in it.

**A tie naming a conductor the stackup does not have leaves the film ACTIVE**, with a note. The other
choice would let a typo silently thin the medium, which is the failure the mechanism exists to
prevent. It is not a refusal, because the extraction is still a valid one — validation is where the
typo is called an error.

### BOTH extractors read it, and finding that was the one surprise

The brief named `PlanarExtractor`. Implementing only that left nine `Ui.Tests` failures, four of them
the acceptance tests: **`CrossSectionExtractor` builds its own layered medium from the same stackup**,
so a film left switched on there is exactly MIM-2's second cost — measured, not argued:
`Mmic_LineOnMetal1_...` came back at Z₀ 48.25 Ω against the hand-built 49.62, and the 72 µm line's
ε_eff at 8.54 against a (6, 8.5) band. Both pass with the tie honoured there too.

Its version of "in this run" is a set of one: a uniform-line cross-section refuses multi-level
geometry outright, so the question is "is the plate THE signal conductor". There is no sheet surface
to revert — that kernel models real metal of real thickness and never reads `SheetAt` (MIM-6's own
recorded decision).

So the rule lives in one file, `Em/PatternedDielectric.cs`, against this area's standing rule that
the two extractors restate the stackup rules rather than call each other. **That rule is about the
cross-section extractor's REDUCTION test and its refusals**, which must never appear on the planar
acceptance path. This is the opposite shape: one paragraph of policy and one sentence of user-facing
text — and the sentence is the reason. Two copies of the "your medium lost a layer" note would drift
into two accounts of one decision.

Mechanically both callers rebuild rather than patch: deactivating changes materials and z, and the
bands already in hand are re-resolved by their stackup INDEX, which the rebuild preserves. The
`Technology` object the caller passed in is never mutated (it is a live document, re-extracted at
every frequency of a sweep) — the affected entries are cloned field for field.

### The gates

- **`MimCapacitorTests.AnAirbridgePost_SolvesOnTheOneTechnology_AndExtractsIdenticallyToTheModuleFreeStack`**
  — the brief's own gate, and it flipped a test that asserted the refusal. Level names, every level z
  and thickness, every medium region's thickness/εᵣ/tanδ/µᵣ, the slab, the via's indices and its
  footprint areas: all compared with `Assert.Equal` on doubles, no tolerance. The comparison
  technology is DERIVED from the shipped one by removing the module, not restated.
- **`MimCapacitorTests.TheCapacitorRun_IsWhatTheRetiredSecondTechnologyProduced`** — the ACTIVE side,
  as literals captured from `MmicGaAsMim()` before the merge, because the object they came from no
  longer exists. 103 / 103.2 / 106 µm, medium 103 µm εᵣ 12.9 | 0.2 µm εᵣ 6.8 | 2.8 µm air, the plate
  via 1→2 at 3.6e-11 m².
- **`PatternedDielectricTests`** — the mechanism on a probe technology built in the test, so the
  assertions are about the rule rather than about what circuitRF happens to ship: both extractors,
  the note, the broken tie, named analysis levels overriding artwork, and the schema half
  (validation, `.ctech` round trip and absence when unset, merge conflict description, editor row).

`dotnet test tests/Ui.Tests` 10,364 passed / 0 failed; `tests/Firewall.Tests` 10/0.

### What did NOT come out bit-identical, and why it cannot

Two measured residuals, both outside the brief's stated gate and both stated rather than tuned away:

- **A Metal2 line's CLOSED-FORM substrate is 102.75 µm instead of 103** (−0.24%), with ε_eff a shade
  higher. `SubstrateResolver` sums dielectric bands and has no notion of an analysis level, so it
  cannot ask the tie's question — and teaching it would not close this anyway: skipping the film
  gives 102.55 µm, further away. The missing 0.25 µm is the plate METAL, and no closed-form path
  counts a metal band. Pinned in
  `MimCapacitorTests.TheClosedFormPathDoesNotReadTheTie_AndTheOnlyCostIsAMetal2LineBy025Micron`.
- **A run whose LOWEST analysis level is Metal2 gets a sizing εᵣ of 9.78 instead of 9.58** (+2.1%).
  `slabBands` sums the dielectric bands under the lowest level; the deactivated film is still a
  0.2 µm dielectric band and the plate's 0.25 µm is a conductor band, which that sum never counts —
  the same structural gap as above. Since MIM-4 the slab is a SIZING object only (calibration-standard
  geometry, the β seed, the near-radius floor, the mesh), never the published reference impedance.
  A Metal1-fed run — every de-embedded one, until MIM-4's ports move — is unaffected: its slab is the
  GaAs alone, bit for bit.

## MIM-4 — the stratified sub-feed refusal, retired (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-4-interior-static-greens.md`, gap 4 of the MIM series. The engine
half is `src/Engine/Mom/RESOLVED.md` §MIM-4; what changed HERE is `PlanarExtractor`.

**What was refused.** More than one dielectric entry between the ground plane and the lowest analysis
level: *"L9's Green's function handles a stratified medium happily — what does not is the
de-embedding … Merge the layers under the feed into one substrate entry, or wait for a static Green's
function at interior heights."* That merge was **a change to the physics offered as a workaround** —
two dielectrics in series under a trace are not one dielectric of either εᵣ — and the only reason for
it was that `C_pul` came from an image series over one grounded slab. MIM-4's
`InteriorStaticImages` removes the reason.

**What it does now.** The layers are carried at their stated thicknesses (`BuildMediumStack` always
built them; nothing there changed), and a note replaces the refusal.

**Two things worth keeping.**

1. **The `GroundedSlab` is now a SIZING object where the region is stratified, and the right average
   for that job is the series-capacitance equivalent** — `h/ε_eff = Σ d_i/ε_i`. It still sets the
   calibration standards' geometry, the branch-continuation β seed, the accelerated near-radius floor
   and the mesh; none of those is the published reference impedance any more. It reduces to the single
   layer's own εᵣ, bit for bit, when there is one, and it is what a wide line over the real stack
   converges to: 21.3% / 10.3% / 3.2% / 1.1% difference from the true stratified `C_pul` at
   W/h = 0.5 / 2 / 8 / 24. The note says out loud that the number is for sizing and never for the
   reference impedance — a number the user can see and misread is exactly the shape of thing that
   gets trusted silently.
2. **A stratified medium turns the general kernel on at ONE level too.** The explicit `MediumStack`
   used to be attached only when `levels.Count > 1`. Before this brief that was sufficient — a
   stratified region under the lowest level was refused, and with one level there is nothing above it
   in the stack, so a one-level problem was always one dielectric. Carrying the layers without also
   changing this would have handed L8's one-slab kernel a stack it does not describe:
   `generalMedium = levels.Count > 1 || mediumStack.LayerCount > 1`.

**Held by** `tests/Ui.Tests/Em/StratifiedSubFeedExtractionTests.cs` — it extracts, both layers reach
the medium, the sizing slab is the series equivalent while the medium is not, the note carries the
layer names and says what the effective εᵣ is and is not for, and a ONE-dielectric region is
unchanged (one layer, the slab's own material bit for bit, no note, and still the one-slab kernel
path).

## MIM-6 — the level reference surface: a conductor's sheet learns which surface of its band it sits on (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-6-level-reference-surface.md`, the fifth gap of the MIM series —
MIM-2's own finding 1. Extraction only; `src/Engine` untouched (a level's `ZM` is already arbitrary
there), and kernel A's cross-section path untouched (it models real metal thickness and has no sheet
to place).

### The problem

`PlanarExtractor` placed every conductor level's zero-thickness sheet at the BOTTOM of its stackup
band and absorbed the band's own z range into the dielectric ABOVE it. Both rules are right for
everything that came before — together they are what makes a microstrip's height come out as the
substrate thickness. Between two capacitor plates they are wrong: the lower plate's whole metal
thickness lands INSIDE the gap. The shipped MIM technology extracted its levels at
z = 100 / 103.2 / 106 µm, so the solver saw a **3.2 µm plate separation where the process states
0.2 µm — 16×** — with the whole 3.2 µm carrying the capacitor dielectric's εᵣ.

Not fixable by authoring: the gap is `Metal1.Thickness + MIMDielectric.Thickness`, `TechValidation`
requires a positive thickness on every band, and Metal1's sheet was pinned 100 µm above ground by
the microstrip case.

### The shape, and why the two halves are ONE choice

`StackupLayer` gains `SheetAt` (`ConductorSheetSurface?` — `Bottom`/`Top`), additive and nullable,
no `.ctech` `FormatVersion` bump, meaningless on a non-Conductor entry: the `Fill`/`SpanFromLayer`
pattern already in `TechModel.cs`.

**The absorption direction is not a second setting — it follows the surface, and that pairing is
load-bearing.** `PlanarProblem.CanSolve` refuses a level that is not on an interface of its own
medium (L9c's first earned refusal). Sheet at the bottom + band absorbed upward puts the sheet on an
interface; sheet at the top + band absorbed downward puts it on an interface. Either half alone does
not: a sheet moved to the top of its band while the band still went to the dielectric above would
land 3 µm inside a region, and every MIM extraction would refuse.

`Band` gained a `SheetM` alongside `BottomM`/`TopM`, and every z decision in the file — level z, the
ground-band query, the slab height, the slab-band window, the medium's cut set, `topOfInterest`, the
level ordering, the ungrounded refusal's "dielectric under this level" — reads `SheetM`.
`BottomM`/`TopM` stay the band's own extent, which is what the absorption arithmetic and the
conductor's reported `ThicknessM` are written in. **`SheetAt` chooses where the sheet is, never how
thick the metal is.**

`BuildMediumStack`'s absorption is one added branch: when an interval's midpoint is inside no
dielectric, find the CONDUCTOR band it is inside and ask its surface — `Top` takes the dielectric
whose top is this interval's bottom, anything else keeps the pre-existing "dielectric whose bottom is
this interval's top". That is why `Bottom` and unset are bit-identical rather than merely equivalent:
the old expression is still literally the else branch.

### The gate, measured

On the shipped MIM technology (`Metal1 = Top`), the series capacitor:

| | Before | After |
|---|---|---|
| Levels (Metal1 / MIM Metal / Metal2) | 100 / 103.2 / 106 µm | **103 / 103.2 / 106 µm** |
| Region between the plates | 3.2 µm at εᵣ 6.8 | **0.2 µm at εᵣ 6.8** |
| Region under Metal1 | 100 µm GaAs | **103 µm GaAs** |
| Slab height | 100 µm | 103 µm |

The airbridge post's kernel refusal is UNCHANGED and was re-measured, not assumed: the post now runs
z = 103 → 106 µm rather than 100 → 106, and it still straddles the plate dielectric's upper interface
at 103.2, so `PlanarKernel.CanSolve` refuses it by the same sentence with a different z in it.

### The notes now name the surface, and that is not decoration

A level at 103 µm on a conductor whose band runs 100–103 is either a mistake or a deliberate
reference-surface choice, and the run notes are the only place a user can tell which — the panel's
own stackup readback is bound to the CROSS-SECTION readback, which a full-wave run does not produce.
The multi-level note now reads `103 µm (top of 'Metal1'), 103.2 µm (bottom of 'MIM Metal'), …`.

### `SubstrateResolver` is deliberately NOT taught the field — the decision, with its measurement

The closed-form microstrip path sums dielectric thicknesses. On the MIM technology it and the EM
extractor now disagree about a Metal1 line's substrate by exactly one metal thickness: **100 µm
against 103**. Measured cost of teaching it the field instead: a 70 µm line's static Z₀ would go
**49.42 → 50.06 Ω, +1.3%**.

**Not taught, because the two numbers answer different questions.** Hammerstad-Jensen models real,
finite-thickness metal and takes that thickness as its own parameter `t`; its h is the physical
substrate — ground plane to the underside of the metal — which is what the process states. The
extractor's h is where a ZERO-thickness sheet was placed, a discretisation position rather than a
dimension. Feeding the sheet position to the closed form would count Metal1's 3 µm twice and move
every Metal1 microstrip on this technology to agree with a discretisation artifact. The discrepancy
is bounded by one metal thickness by construction and the run prints the number it used. Recorded in
`MimCapacitorTests.AMetal1Microstrip_ResolvesDifferentlyOnTheTwoTechnologies_ButAgainstTheSamePlane`,
which asserts both heights side by side so the divergence stays deliberate.

### What this brief does NOT claim

**No capacitance accuracy.** A 0.2 µm gap against micron cells is exactly MIM-3's unmeasured regime;
this fixes the geometry so MIM-3's ladder measures the real one. The raw-solve gate is still a
with-via/without-via comparison carrying no magnitude band (|S21| 3.92e-4 with the plate via against
1.49e-4 without, ratio 2.64 — re-measured at the new geometry), because a raw port's own
discontinuity dominates any absolute number, which is MIM-2's finding-2 retraction.

### Tests

`tests/Ui.Tests/Em/SheetReferenceSurfaceTests.cs` is the mechanism: unset ≡ explicit `Bottom` as a
WHOLE-EXTRACTION identity (levels, medium regions, slab, vias, polygon count and every note) over all
three shipped technologies; clearing the shipped `Top` restores 100 / 103.2 / 106 exactly; a purpose-
built stackup where the intervening dielectric is THINNER than the metal below it, so the absorption
direction is visible rather than a rounding difference; `SheetAt` on a non-conductor entry ignored;
`.ctech` round trip (and absent from the file when unset); the merge clone and its conflict
description. `MimCapacitorTests` carries the shipped technology's own numbers.


## MIM-1 — region vias: drawn via artwork beyond the point `ViaShape` (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-1-region-vias.md`, gap 1 of the MIM series. Extraction and
reporting only; `src/Engine` untouched, and every §7 via refusal still fires unchanged.

### What was wrong, and why it was silent rather than refused

`PlanarExtractor`'s classification loop recognised a via-bound drawing layer in exactly one place:
inside its `if (s is ViaShape)` branch. Every other shape fell through to `binding`, the layer→z-band
map — and `BuildStack` builds that map from **non-Via entries only**, because a via contributes no
thickness and has no z band of its own. So a rectangle or polygon drawn on a via layer missed the map
and landed in `ignoredOther`.

**The counter it landed in is what made the failure worse than a drop.** `ignoredOther`'s note says
the shape is *"not bound to a stackup conductor or via entry"* — which is exactly the wrong advice
for artwork on a layer that IS bound, and sends the user to the technology editor to redo something
already done. The same silence swallowed a drawn backside-via slot or bar.

This is the same map-vs-branch split that made `BuildVias` unreachable at L9's phase gate. It is
worth stating once more: the two bindings answer different questions (where a layer sits in z, versus
which two conductors a via joins), keeping them apart is right, and the cost of keeping them apart is
that every new shape kind has to be routed to the second one deliberately.

### What it does now

- **A filled region on a via-bound layer becomes a `PlanarVia` footprint**, through the conductor
  path's own shape→`PlanarPolygon` conversion — outer ring plus holes, the layout's own flatten
  tolerance, the same degenerate-ring floor. Reused rather than restated: a via footprint and a
  conductor footprint are resolved onto the same tensor grid, and two conversions that could drift
  apart would show up as a via meshing to a slightly different set of cells than the metal it lands
  on.
- **The footprint is NOT squared.** The equal-area square (side = 0.886 × drill) exists so a round
  barrel *nobody drew* does not contribute a hard gridline per facet. A drawn outline already is the
  footprint, so it goes to the mesher as it stands.
- **Span, conductivity and the ground rule come from the stackup entry**, identical to the point
  path, and a region via participates in the same `noSpan` / `unknownLevels` / `notAdjacent` /
  `toGround` / `wrongGround` accounting — counted in SHAPES, because a shape is what the user drew
  and can go and look at.
- **Nothing on a via-bound layer falls into `ignoredOther` any more.** A `PathShape` there gets its
  own sentence (a centreline encloses no area; draw the region), and so does a region that flattens
  to nothing.

### The one design decision worth the words: regions are GROUPED PER STACKUP ENTRY

Every region on one via entry becomes **one `PlanarVia` carrying several footprint polygons**, not
one `PlanarVia` each. The obvious reason is that the span, conductivity and ground rule all come from
the entry, so per-shape vias would be N identical records. The real reason is correctness:

`SurfaceMesher` scans every grid cell against a via's polygon list and **stops at the first polygon
that covers it**. Two overlapping footprints inside one `PlanarVia` therefore give a shared cell
**one** vertical basis. As separate `PlanarVia`s they would give it **one each**, silently doubling
the vertical current in the overlap — and a plate connection drawn as two overlapping rectangles is
an ordinary thing to draw, not a corner case. `TwoOverlappingRegions_GiveTheirSharedCellsOneVerticalBasisEach`
pins it as a counter that is independent of the mesh pitch: no cell index appears twice,
and the meshed footprint is the union (60 × 40 µm) rather than the sum (2 × 40 × 40 µm).

**The same hazard exists on the point path and was left alone**, deliberately — it is pre-existing
behaviour, changing it would move existing runs, and the brief forbids touching the point path. It
was *measured* while sizing the structural gate below: a 2 × 2 array of nominally touching point
vias overlaps by 0.37 nm (see the next section), and the meshed footprint comes out at
1600.0591 µm² against the true union — i.e. the overlap strip is counted twice, exactly as the
first-cover argument predicts.

### The structural gate, and why it compares AREA rather than a basis list

The brief asks that a region via covering the cells of an N×N array of touching point vias yield the
same vertical basis functions. **That cannot be a basis-list comparison, and asserting it would be
asserting something false.** L9c's own mesher finding is that a via footprint must contribute HARD
gridlines or the via vanishes silently — so N×N touching footprints put N−1 interior gridlines per
axis into the shared tensor grid that one large footprint does not. Those lines *subdivide* the
covered cells; they do not move the covered boundary. Measured: 943 unknowns (4 vertical) for the
single region against 943 (4 vertical) for the drawn 2 × 2 array on this fixture.

The grid-independent statement of the same claim is **the plan-view area the vertical bases cover**
— still a cell counter (one basis per covered cell, summed over the cells' own areas), never an
S-parameter. The gate is in two halves:

| | Fixture | Claim | Result |
|---|---|---|---|
| A | 2 × 2 drawn squares vs one drawn rectangle over their union | covered area equal, **to the bit**; the single footprint needs no more unknowns | 1600 µm² both, N = 943 both |
| B | 2 × 2 point vias vs the same region | covered area equal to the equal-area square's own DBU rounding | −3.6926 × 10⁻⁵ relative, **predicted exactly** |

**Half B's discrepancy is predicted rather than bounded**, which is the part worth keeping. A point
via's square is 0.886 × drill and a drill is an integer number of DBU, so the square that gets meshed
has side s′ ≠ the nominal s and the array covers n²s′² against the region's (ns)². On this fixture
s′ − s = +0.37 nm, so nominally touching point vias in fact *overlap*, and 1 − (s′/s)² reproduces the
measured area difference to 12 decimal places. If the two ever disagree by anything that rounding
does not account for, one of the two paths has a real defect. (The overlap also costs N: 1096
unknowns with 16 vertical bases, against the drawn array's 943 with 4 — a sub-nanometre sliver run,
and a good illustration of why the point path snaps nothing.)

### Milestone 4's assumed paths, checked rather than assumed

- **A Via stackup row binds a drawing layer and states its span** — real, `ShowsDrawingLayerPicker`
  is `Kind == Via` and a Conductor row deliberately does not show that control (it binds through the
  layer table). Verified against a live `TechEditorViewModel` over the MMIC starter, including that
  the picker's option list actually contains the layer the extractor keys on.
- **A rectangle drawn on that layer reaches the extractor** — real, and is now the main body of tests.
- **`EmDiagnostics`' via count includes region vias** — **this path does not exist.** `EmDiagnostics`
  is the EM run service's REFUSAL family (`em.run.cancelled`, `em.layout.not-found`, …); it has no
  via counter and no counter of any other extraction quantity. The via count a user actually sees is
  carried in the run's NOTES, which `EmRunService` concatenates from the extractor and the mesher.
  Nothing was built: the smallest version is a test that both note sources count a region via, which
  is what `TheRunsOwnViaCount_IncludesRegionVias` asserts. Growing a diagnostic for a *quantity*
  would be the first non-refusal member of that family and is a decision for whoever converts the
  next family, not a side effect of this brief.

### Tests

`tests/Ui.Tests/Em/RegionViaExtractionTests.cs`, 13 methods, all routine tier (~70 ms). Point-via
bit-identity is asserted with `BitConverter.DoubleToInt64Bits` against the documented rule restated
in the test, not read back from the object under test.

The terminal resolution (`SpanFrom`/`SpanTo` → a level pair or one of five counters) is now a single
local function both artwork kinds call. That is not tidiness either: "the artwork says WHERE, the
stackup says WHICH TWO CONDUCTORS" only holds if the answer cannot depend on how the via was drawn,
and a second copy of that block is exactly how it would stop holding.

## R-em-4's ground query returns null for TWO reasons, and the note claimed the wrong one (2026-08-30)

`PlanarExtractor` resolves the EM ground as **the highest ground-designated conductor BELOW the
lowest analysis level**. When that query comes back empty and `Stackup.Bottom == Ground`, it fell
back to the bottom of the stack and said:

> No conductor layer in technology 'X' is marked as a ground reference, so the ground plane was taken
> from Stackup.Bottom = Ground at the bottom of the stack.

**That sentence is only true for one of the two ways to reach it.** The query is scoped to
conductors *below the signal*, so it also returns null on a stackup that HAS a designated ground
sitting *above* — and there the message is flatly false, contradicted by a ticked checkbox on the
Stackup tab the user is looking at.

**It survived because no shipped technology could reach the false branch.** Every PCB starter was
2-layer and the MMIC's ground is its backside metal, so the only ground candidate was always the
bottom conductor: "none below the signal" and "none at all" were the same statement. The first
technology with an INNER ground plane (`pcb-4layer_FR-4_62mil_1oz`, added the same day) made them
different, and a trace on a lower layer was told its technology designates no ground at all. Worse
than the wording: the run **succeeded**, solving against a reference further away than the real one,
so there was no refusal to prompt anyone to look.

The fallback now asks which case it is and names the planes it did find, says why they cannot serve
(a port returns through a plane BENEATH the conductor it feeds), and states the cost — the reading
will be a higher impedance than the real structure. The original sentence is kept verbatim for the
genuinely-undesignated case.

### Two neighbouring messages were wrong in the same way — advice for a situation that was not this one

- **The zero-height slab refusal** said *"Check the stackup order in the technology editor."* The
  commoner way to arrive there is a correctly-ordered board whose BOTTOM conductor is being treated
  as the signal: it rests on the `Stackup.Bottom = Ground` boundary, so the slab has zero height and
  nothing is misordered at all. That case is now named, with the two things that actually help
  (mark it as a ground reference, or move the trace up a layer).
- **The no-signal-conductor refusal** said *"Draw the artwork on a conductor layer, or bind the layer
  it is on to a conductor entry."* When every shape is on a ground-designated conductor the layer IS
  bound — the advice sends the user to redo something already done. Reachable on any stackup with
  more than one plane (a 4-layer board whose only artwork so far is an inner pour), so it now says
  the plane is not meshed and points at the "Ground reference" tick.

**None of the three was found by reading the extractor.** They were found by running it on each
conductor of the new 4-layer technology in turn and printing the result — a scratch xunit probe, run
once and deleted. A message can only be checked against the state that reaches it.

Gated by `tests/Ui.Tests/Em/FourLayerGroundReferenceTests.cs`, which drives the extractor rather than
scanning source, and includes the negative: the 2-layer starter must reach neither new branch.

## A board outline refused the EM run, and the dielectric binding was the workaround (2026-08-30)

User proposal: remove the dielectric's "Drawing layer" control from the `.ctech` editor, since the
binding is never used except under the hood. **The premise was wrong and the conclusion was right,
for a reason neither of us had.**

### What the binding actually did

Nothing electrical: `PlanarExtractor.BuildMediumStack` reads only `Epsr`/`TanD`/`Mur`/`ThicknessDbu`
and every dielectric is a laterally infinite slab, so a dielectric bound to `(none)` is everywhere.
Every other consumer filters it out — `WBondClearance` reads `DrawingLayers` only after
`if (sl.Kind != StackupKind.Conductor) continue`, `PcbLayerNaming`/`DrcConnectivity`/`GerberExport`
take conductors and vias, `PcbWriter` writes dielectric thickness with no layer reference, and in
`PlanarExtractor` a dielectric-bound and an unbound shape reach the same `ignoredOther`.

Its ONE effect was in `CrossSectionExtractor.Classify`, and it was not subtle. Measured on the MMIC
starter with a Metal1 trace plus a die outline on `Substrate`: binding kept → `Ok=True` with a note;
binding removed → **hard refusal.** The field was the difference between the run working and failing.

### The defect underneath: the refusal fired on the normal case

Sweeping every layer of the shipped 2-layer PCB starter, one shape at a time beside a solvable trace:

| Layer | Result |
|---|---|
| Top Copper, Bottom Copper, Drill | Ok |
| Soldermask Top / Bottom, Silk Top / Bottom, **Outline** | **REFUSED** |

**Every PCB layout has a board outline**, so the failing case was the normal one — and the refusal's
advice was *"add this drawing layer to a conductor entry's DrawingLayers list"*, i.e. declare your
board outline to be copper. The dielectric-`DrawingLayers` binding was a narrow escape hatch from
this, applied only where the MMIC starter tripped over it.

### The discriminator was available and was not being asked for

A layer the technology **declares** but binds to no stackup entry is the technology stating the layer
is not metal. Silk, soldermask and outline are exactly that. A layer the technology **does not
declare at all** — a foreign import, a hand-edited file — is the case nobody has said anything about,
and there the original reasoning holds in full.

So the refusal is narrowed, not deleted: declared-but-unbound is ignored with a note that names every
distinct layer once and still offers the fix (*"If one of them IS metal, bind it to a conductor entry
on the Stackup tab"*); undeclared still refuses, now pointing at the Layers tab rather than telling
anyone to call it copper. Ignoring is REPORTED, never silent — a trace genuinely drawn on a forgotten
layer is still visible in the run's own output.

With the workaround unnecessary, the editor's dielectric picker is gone
(`StackupLayerRowViewModel.ShowsDrawingLayerPicker`, via only). **The model field stays**: shipped and
user `.ctech` files carrying a dielectric binding still parse, validate, round-trip through
`TechnologyMerge`, and take their original more-specific "substrate extent" note — removing a control
must not rewrite anyone's file. `IsSingleDrawingLayer` is deliberately left answering `true` for a
dielectric, because the CARDINALITY rule did not change.

Gated by `tests/Ui.Tests/Em/UnboundLayerArtworkTests.cs` (10 tests), including both halves that make
this safe rather than merely permissive: the MMIC die outline extracts with the binding removed, and
a file that still carries one behaves exactly as before.

**None of this was visible by reading the extractor.** It came from running it on each layer in turn
and printing the verdict — a scratch xunit probe, run once and deleted. The same method found the
ground-reference bug above. A refusal can only be checked against the state that reaches it.

## A laterally-finite dielectric cannot be drawn, because the kernel cannot represent one

Asked while the section above was being investigated: how does a user simulate a MIM cap built on a
GaAs substrate, if the dielectric is always everywhere? Surely the nitride must be drawn on a layer.

**It cannot be, and no binding would have helped** — this is a formulation limit, not a missing
feature. `BuildMediumStack` produces a `LayerStack` of `MediumLayer(thickness, material)`: a 1-D
stack of laterally infinite slabs, and the DCIM Green's function is derived from exactly that stack.
Unknowns live on conductor surfaces and via barrels only. A nitride island under a top plate needs
either volume-equivalent currents inside the dielectric (a VIE) or a surface-equivalence formulation
on its boundary, and neither exists in `src/Engine`'s planar kernel.

Drawn dielectric geometry is therefore ignored — before the change above it fell into
`PlanarExtractor`'s `ignoredOther`; it is now named in the declared-but-unbound note. Reported, but
inert either way. Note also that the MMIC starter's own `Cap Dielectric` and `Nitride` drawing layers
are bound to no stackup entry at all: they are artwork/DRC/GDS layers, and their presence must not be
read as EM support.

What actually works, best first: a **lumped C in the schematic** (C = ε₀εᵣA/d from the process's
capacitance density, with the EM run covering the interconnect around it — the normal MMIC flow); or
**stating the inter-metal dielectric as nitride** in the stackup and meshing both metal levels, which
gets the plate overlap right out of the solve but puts every airbridge and crossover in the same run
in nitride instead of air; or **splitting the run**, EM for the passive interconnect and lumped caps
combined in the schematic.

## Union was quadratic in the operand count, which made the Gerber importer's own advice unusable (2026-09-03)

`LayoutBooleans.Combine` folded every boolean **linearly**: `acc = acc op operand[i]`, one full
Clipper2 `BooleanOp` per operand against an accumulator that had already absorbed everything before it.
For Intersection, Difference and Xor that shape is required — Difference is not commutative, so those
operands must be applied in selection order. For **Union** the same shape is pure cost: operand N is
clipped against a result carrying N-1 operands' worth of contours, so the total is quadratic.

**This is not a theoretical complaint — the codebase routes users into it by name.** `GerberImport`
tells anyone importing a vector-filled pour that their layer "arrived as N separate strokes ... use the
editor's Merge action to turn them into one region before setting up EM ports". On an owner-supplied
4-up RF panel that is **46,721 strokes on one copper layer**, and Union on it ran for **over forty
minutes without finishing** (killed, not completed). The advice was not actionable on the exact file
class that triggers it.

Union is associative, so it is reduced as a **balanced tree** — `(A∪B)∪(C∪D)` rather than
`((A∪B)∪C)∪D`. That is a change of ORDER, not of semantics, and every step stays a real pairwise
`BooleanOp` between two already-resolved regions.

| | 76,517 operands, 10 layers |
|---|---|
| linear fold | >45 min, never finished |
| balanced tree | **9.8 s** |

Result: 76,517 shapes collapse to 2,478 (top copper: 47,530 strokes → 190 polygons).

### The obvious faster version is WRONG, and a test caught it

The first attempt was the one-call form: concatenate every operand's `Paths64` into a single subject
set and resolve it in one `BooleanOp(Union, all, empty, NonZero)` — which is exactly what `Repair`
already does for one self-intersecting shape. It is 40 s, still a huge win, and it produces the wrong
answer for any operand carrying a hole: **under NonZero a hole contour from one operand cancels another
operand's fill where they overlap**, so a union that should have closed a hole punches one instead.
`PcbImportTests.ACustomPad_IsOneUnionedRegion_IncludingEachFilledPrimitivesPen` failed immediately —
one region came back as two. Union two resolved regions at a time; never a raw pile of contours.

### Merging a hatched pour is the right move for the MODEL and does not make rendering faster

Worth stating because the import message implies otherwise. The unioned panel renders **slower** than
the unmerged one (240 ms vs 126 ms/frame at Zoom-to-Fit), because a hatched pour's union has a
comb-shaped boundary: 2,478 shapes carrying **308,326 outer vertices plus 771,663 hole vertices**, one
polygon of 12,335 vertices with 15 holes. The artwork really is that complicated; the strokes were
hiding it in a form Skia happened to rasterize cheaply. Merge for editability and for a meshable
conductor — which is what the import message actually claims — not for frame rate.

## Gerber import: a six-layer board that imported as nothing (2026-09-04)

A user's real board came back with **every artwork layer refused** and only the drill data through —
`"This Gerber file declares no %MO*% unit (and no G70/G71)"`, once per file, twenty times. Six
separate defects, found from that one file set. The first is the blocker; the rest were sitting
behind it.

### 1. One `%…%` block may hold SEVERAL commands, and only the first was read

```
%FSLAX45Y45*MOMM*%
%IR0*IPPOS*OFA0.00000B0.00000*MIA0B0*SFA1.00000B1.00000*%
```

This is the original RS-274X spelling — commands separated by `*` inside one `%…%` — and it is still
what several exporters emit. `ExtendedCommand` split the body on `*` and then used `segments[0]` and
nothing else, so `FS` was read and `MO` was silently dropped. The refusal that followed was accurate
about its own state and useless about the cause: the file DOES declare its unit, on the same line.

The loop now runs every segment. **`%AM` is the one command that legitimately consumes the rest of
the block** — its primitives are themselves `*`-separated — so it ends the loop rather than being
one of the iterations.

### 2. `%IR` was an unrecognized command

`%IR0*%` is the identity, and a file that emits the command at all almost always carries the
identity. Counting it as unknown put one noise line on every file of a real set while saying nothing.
It now joins `%MI`/`%SF`/`%AS`/`%LM`/`%LR`/`%LS`: identity accepted silently, non-identity refused by
name.

### 3. A numbered mid layer was not read as copper at all

The set names its outer copper "Top Layer"/"Bottom Layer" — both already in the rung-3 table — and
the four between them "Layer 2".."Layer 5", which matched nothing. **That is not a labelling
nuisance: only conductors enter the stackup and the copper order**, so four sixths of the board
quietly left the part of the import the EM path reads, and the run reported "2 of 2 copper layers".

The new row is the last in the table, so every function row wins first, and it matches only when a
NUMBER follows the word — a name that merely contains "layer" is not promoted to copper. **"Layer 2"
counts the whole stack from the top and is therefore the FIRST inner layer**, which is where the -1
comes from; "inner 2" already counts only the mid layers and keeps its number. Both spellings now go
through one `NumberAfter` helper with a per-row offset.

Guessed inner layers also needed an ordering tiebreak. They all share one `SideRank`, so they fell
back to file NAME — which orders "Inner 10" before "Inner 2". It is deliberately **not** a
`CopperIndex`: the number came from a file name, and the import's report must go on calling that
stack order a guess.

### 4. A drill DRAWING landed on the drill layer

`..._Drill_Drawing.art` matched the plain `drill` row. It is a dimensioned fabrication sheet whose
tool legend sits beside the board, so the drill layer's extent ran from -37.5 mm to 222 mm on a
111.8 mm board. A `drill` + `drawing|map|legend|chart` row now takes it first, as "Drill Map".

### 5. A ROUT FILE HAS NO HITS, so the artwork cross-check agreed with itself

The strongest evidence available for a drill file's format is whether its holes land inside the
artwork — and `CrossCheckExtents` counted `Hits` only. A rout file commonly holds routed SLOTS and
not one plain hit, so it reported "all 0 hits fall inside the artwork extent", `Agrees` came back
true, and the wrong-format retry below it never ran. The file's four slots landed at Y 231..318 mm on
a 55 mm board and nothing said so. Slot vertices are now counted the same way — a slot is cut through
the same copper a hole is.

### 6. The width of the coordinate words IS the digit format, and nothing was reading it

The same file: `METRIC`, no format statement, coordinates like `X0056999Y0318200`. Defaulted to the
classic metric 3:3 that is **six** digits, its seven-digit words were read ten times too large.

There is a new evidence rung for this, `DrillFormatEvidence.CoordinateWidth`, and it needs BOTH of
two conditions — neither alone would do:

* **every coordinate word is the same width** — which a trailing-suppressed file cannot produce;
* **at least one of them carries a leading zero** — which a leading-suppressed file cannot produce.

Together they are close to proof that the file suppresses nothing and writes each coordinate at its
full field width, and that width is then the whole format: the integer half keeps the unit's
conventional size (3 covers 999 mm, 2 covers 99 inch, no board needs more) and the measured total
settles the decimals. That reproduces every format in circulation from the width alone — 6 digits of
mm is 3:3 and 7 is 3:4; 6 of inch is 2:4 and 7 is 2:5.

It settles the SUPPRESSION question too, because a word already at the full width parses to the same
integer under either convention (`ParseCoordinateWord` pads only up to that width). Recorded as
settled rather than defaulted, which is what stops the import raising a prompt about a file that left
nothing open. Four coordinate words is the floor at which "they are all the same width" stops being a
coincidence a two-hole file could produce by accident.

### The drill-format prompt asked the same question once per file

Reported separately by the same user. A set's drill files come out of one exporter in one format, so
the second dialog is the one a user answers without reading. `DrillFormatChoice` gained
`ApplyToAll`, the prompt is told how many files remain so the checkbox appears only when there is
something to apply it to, and **a CLI `--drill-*` flag now sets it implicitly** — a flag is a
statement about the run, not about one file, which also stops the same refusal printing once per
file. A null `Override` carried this way accepts each later file's OWN inference rather than forcing
this file's format onto it: the user confirmed an inference, and only what they actually CHANGED is
worth propagating.

### Two smaller things the same log exposed

* The composite-polarity paragraph was added to BOTH `CompositeReason` and `Diagnostics`, and the
  orchestrator prints both lists — six duplicate paragraphs on a six-layer board.
* The minted `StackupKind.Via` entry named no span, so the technology validator reported "spans an
  unknown conductor layer" twice per drill file. It now names the topmost and bottommost conductor
  entries **when the stackup has any**. A set with no job file has no conductor entries to name, and
  inventing two would be a substrate invented under another name — that import already says, in
  words, that the technology is incomplete.

### Verified against the file set, not only against the tests

All 20 artwork files import (3,284 shapes, 6 copper layers). Every copper layer's extent is
0.30..111.50 × 0.30..54.71 mm inside a board outline of 0..111.80 × 0..55.00, and the routed slots
moved from Y 231..318 mm onto the board at 23.18..31.82 mm. Nothing from that set is committed: it
names a vendor, a customer and a real filesystem path in its own header comments, and every fixture
here stays hand-authored.

## Vias carved back out of a composited pour (2026-09-04)

The follow-on the note above left open. On the six-layer board every copper layer paints in clear
polarity, so every layer was composited — and compositing unions each via pad into the pour around
it. Pairing looks for a discrete `CircleShape` flash, found none, and returned **zero vias from 1,555
holes**. That is not a labelling problem: `ViaShape` on a via-bound layer is what `PlanarExtractor`
reads as a via (L9d/D5), so the board simulated with no vias in it at all.

### The pads were never gone — the reader just threw them away

Compositing is the LAST thing `GerberReader` does. Until then every flash is still a separate painted
object, so the pad's real diameter is sitting right there. `GerberReadResult.CompositedFlashes` now
carries them, and pairing treats them exactly as it treats a surviving flash. Nothing is invented: the
pad size is the file's own aperture, not a drill diameter plus an assumed annular ring.

They are EVIDENCE, not artwork — their copper is already inside the pour in `Shapes`. So claiming one
obliges the caller to cut the same disc back out, which is what `GerberImport.CarveClaimedPads` does.

### The invariant that makes it safe

**Carve + via pad = the copper that was there.** A pad is offered only if it survived compositing
WHOLE — tested at its centre and eight points around the rim, against the NonZero winding of the
composited paths. A dark flash a later clear object ate (an antipad on a plane layer is exactly this)
is not a pad any more, and pairing a hole to one would put copper back where the artwork deliberately
removed it.

Measured on the real board, per layer, against the identical artwork imported with no drill file:
five layers conserve to **0.00e+00** and the top to **4.2e-05** relative. That residual is the
measurement's own: the carve subtracts a FLATTENED disc while the check adds back exact πr², and for
the ~111-segment circles `CircleTolDbu` produces the inscribed-polygon deficit is 5.3e-4 per pad
against 5.8e-4 observed. `CarvingAViaOutOfAPour_LeavesTheLayersCopperUnchanged` pins it.

### Two cross-layer leaks the area measurement found, both older than this work

Neither was visible before, because nothing had ever compared the copper in to the copper out.

- **A pad claimed on one layer, a via landing on another.** `LandingLayer` is `landingLayer ??
  pad.Layer`, and `PickFlash` will take a flash on ANY layer when the landing layer has none — so a
  via whose top pad was missing paired with the INNER one, and 4.5 mm² left an inner plane and
  reappeared on the top. A composited pad may now only be claimed where the carve and the via's pad
  cancel, i.e. on the layer the via actually lands on. A surviving discrete flash may still come from
  anywhere, as before: consuming one removes that shape, so the asymmetry is at least visible.
- **A SOLDER MASK OPENING IS NOT A VIA PAD.** The ranking's last resort is "a flash on any layer at
  all", which on this board took the mask clearance around each mounting hole — six 4.6 mm openings
  became 4.6 mm COPPER pads, sitting on a pour that has a deliberate hole exactly there, ~100 mm² of
  copper that is not on the board. `Pair` now takes the set's copper layers and no other layer's
  flash can be a pad. Null or empty means the caller could not say and every layer stays eligible,
  so no existing caller changes behaviour.

Result on the board: **1,475 vias** from 1,555 hits, 80 unpaired — the mounting and tooling holes,
which genuinely have no copper pad.

### Cost notes

One boolean per LAYER, not one per pad: a pour here carries hundreds of thousands of vertices and a
difference per pad would be a thousand passes over all of it. The carve is restricted to the
composited shapes BY REFERENCE rather than by layer, because two files can land on one layer and a
boolean over everything on it would re-polygonise a neighbour's untouched artwork into the pour.

**The nine-point containment test was the part worth being suspicious of** — ~1,500 pads × 9 points
against a pour of tens of thousands of vertices is the shape of something that quietly costs seconds
per layer. Measured instead of assumed, in Release, warm, per file: the WHOLE read of the heaviest
copper layer — compositing included, which dominates it — is **304 ms**, and all six copper layers
together are **978 ms** of a ~13 s board import. The bounding-box prefilter is what makes it a
non-issue; without one it would not be. Do not remove it.

## Reading a `.clay` back was dominated by `EnsureValidHoles` — two box prefilters, 5.4x (2026-09-04)

Found while moving the Gerber import off the UI thread (`src/Ui/RESOLVED.md` has the user-facing
half). `LayoutPersistence.FromFileModel` runs `LayoutClipper.EnsureValidHoles` over every shape on
load — deliberately, per S3.1a R10b: a hand-edited or otherwise not-Clipper2-produced shape may carry
an invalid hole, and the loader enforces validity rather than trusting it. The comment there calls it
"a no-op for the overwhelming common case (no holes, or holes already valid)", and on hand-drawn
layouts it is. It is not a no-op on Gerber-imported artwork.

### Measure in RELEASE, and measure PER SHAPE

The first measurement of this was `dotnet run` and `dotnet test`, i.e. **Debug**, and read 17.4 s. The
Release figure for the same file is **1.69 s** — this is a tight managed loop over `long[]`, which is
about the worst case for a Debug build, while the Gerber parse beside it is string and file work and
barely moves between the two. Quoted together, Debug made the load look like ten times the import
when in the shipped app the two are about equal. Nothing here is measurable with `dotnet test`; a
scratch harness built `-c Release` is.

Per shape, on the 28 MB `.clay` a real 20-layer board imports to (3,284 shapes, 1,573 of them holed,
3,591 holes between them), it is not spread out at all — **six shapes are 1.67 s of the 1.69 s**, and
the worst one alone is 750 ms:

| holes | outer ring vertices | hole vertices | before | after |
|---|---|---|---|---|
| 228 | 1,751 | 21,772 | 750 ms | 155 ms |
| 55 | 1,562 | 12,220 | 230 ms | 25 ms |
| 33 | 1,518 | 11,868 | 182 ms | 50 ms |
| mean over all 1,573 | 390 | - | - | - |

The mean shape has 2.3 holes. **The cost lives entirely in the composited copper pours**, and any
attempt to reason about this from the average is reasoning about the wrong shape.

### What the three terms actually cost, and which one a box can help

For the 228-hole pour: point-in-outer is `holeVerts x outerV` = 38M; hole-vs-outer crossing is another
38M segment-pair tests; and **hole-vs-hole is `sum over pairs of h_i x h_j` = ~233M**, the largest of
the three, because `HolesAreValid` tested every PAIR of holes against every other in full.

Two prefilters, and they are prefilters — every reject is a case where no segment pair can possibly
meet, so the answer is unchanged:

- **Hole vs hole: one ring-box overlap test per pair.** The holes of a pour are disjoint by
  construction, so essentially every pair dies here — ~26k box tests instead of ~233M segment tests.
- **Hole vs outer: reject the OUTER's segments against the HOLE's box.** This is the part that is
  easy to get backwards and worthless if you do. A hole lies inside the outer ring's box, so
  rejecting the hole's few segments against the outer's box discards nothing; it is the outer's
  thousands of segments that have to be thrown away against the hole's small box. `RingsIntersect`
  therefore puts the LONGER ring on the outside of the loop and rejects its segments against the
  shorter ring's box — legitimate because `SegmentsIntersect` is symmetric in its two segments.

Boxes are computed once per ring into a `RingInfo`, not per pair; that is what makes the pair reject
O(1).

**Result: 1.69 s -> 0.31 s for the check, and ~1.9 s -> ~0.45 s for the whole `LoadFromFile`
(Release).**

### The remaining term is not a box problem

What is left is `PointInOrOnRing` — ~155 ms of the 0.31 s, nearly all on that one pour. A ray cast has
to see every segment the ray can cross, so there is nothing to reject. **Gating `OnSegment` behind the
segment's own box was tried and measured no better** (0.34 s against a 0.30-0.34 s spread, i.e. inside
the noise): the test is three multiplies on values already in registers, so four integer compares and
a branch buy back about what they cost. Cutting this further needs an INDEX over the outer ring —
segments bucketed by y, so a cast at height `py` visits one band instead of all N — with a build cost
of its own. Not done.

### The one place a box reject DID change an answer, and it was not the boxes

Caught by the differential gate on trial 115 of 3,000, not by reading the code.

**A ring with a repeated consecutive vertex has a ZERO-LENGTH segment, and `OnSegment` answers true
for every point against one** — its window is `0 <= dot <= lenSq`, and such a segment has `lenSq = 0`
and `dot = 0` for all points. `SegmentsIntersect`'s collinear branch then returns true, so the
unfiltered `RingsIntersect` calls such a ring intersecting against *anything at all*, wherever the two
rings are. The box reject correctly says "these are nowhere near each other" and returns false — which
would have turned every shape carrying a duplicated vertex from "re-derived through Clipper on load"
into "loaded as it stands", silently, under a performance edit.

Preserved rather than corrected: `RingInfo` carries `HasZeroLengthSegment`, computed in the same pass
as the box, and `RingsIntersect` answers that case before the boxes get a say. Whether that repair
*should* happen is a question about R10b and is left open — but it is now visible, which it was not.

### The gate

`tests/Ui.Tests/LayoutClipperHoleValidityTests.cs` is DIFFERENTIAL: it carries `BruteForce`, the
pre-change algorithm verbatim and unfiltered, and asserts the two agree on every case. Nine named
cases (touching holes, a hole touching the outer ring, boxes that overlap where the edges do not,
duplicated vertices, empty and degenerate rings, a 400-gon with 60 holes) plus a 3,000-trial
randomized corpus on a fixed seed. Half the trials are laid out adversarially on a coarse lattice —
that is what produces the exactly-touching and exactly-collinear configurations — and half place
holes in distinct cells of an interior grid, because an all-adversarial corpus answers "invalid" to
almost everything and would exercise only one branch; the test asserts that split rather than
assuming it. `HolesAreValid` was made `internal` so the corpus can drive it on ring arrays directly.

## §5C.2a — cross-workspace technology agreement (2026-09-04)

`ExternalWorkspaceGate` was refusing more than it had to, and had two cases it refused with advice the
user could not act on.

**"The same technology" now means the same layer table over the keys the referenced cell actually
OCCUPIES** (R47h), not over both tables entire. The hazard R47 names is a key being *reinterpreted*, and
only a key something is drawn on can be. Comparing the whole table refuses two projects sharing a metal
stack that differ in their documentation layers, which is the ordinary case and no hazard at all.
`CellHierarchy.OccupiedLayerKeys` is the walk: transitive through instances, and **it counts a via's
`LandingLayer` as a second key on the same shape** — reading only `LayoutShape.Layer` would have missed
where a via's copper actually lands, which is exactly the field `ViaShape`'s own doc comment warns about.
It reads no coordinates, since a transform moves a shape and never changes its layer; that is what makes
it cheap enough to run per placement with no cache.

**The cost, and where it is paid.** A permit is now a statement about the referenced cell's contents at
one moment, and that cell lives in another workspace where it can grow a shape on a disagreeing key
afterwards. `AuditPlacedExternalRefs` re-asks the whole question and stores nothing — so a fix on either
side clears the warning with no bookkeeping — and it is **reported, never enforced**: the geometry is
already placed and built on, and withdrawing it would be a worse failure than the reinterpretation.

**Two null cases were being refused with a repair that does not exist** (R47i). A technology that is not
there cannot give a key a different meaning:
- *No HOST technology* — the host already renders on generated fallback colours, so nothing is lost by the
  cell arriving. It now returns `AdoptTheirTechnology`, and the caller adopts.
- *No EXTERNAL technology* — its shapes were authored against no layer table, so there is no author's
  meaning for the host's table to contradict. Permitted.

Both used to print "(no technology)" on one side of a refusal naming two files, one of which did not exist.

**A fallback that must not be inverted:** a referenced cell whose `.clay` cannot be READ falls back to
comparing the WHOLE table, not to comparing nothing. "Compare nothing" would turn a broken file into a
silent permit, which is the one direction this gate must never fail in.

### The occupied-key walk was exponential (2026-09-05, same day, owner-reported)

Dropping a large library cell into a workspace hung the UI for ~60 s before the "Add Cell to
Workspace" dialog appeared. It was `CellHierarchy.OccupiedLayerKeys`, added the previous day.

**The defect, in one line: it carried only the DFS-PATH set, not a VISITED set.** `ResolveForWalk`'s
`visiting` argument is a path (added before recursing, removed after) because that is what cycle
detection needs, and it was the only set the walk had — so a shared sub-cell was re-walked once per
PATH reaching it, and every walk re-enumerated all of its shapes. Measured on a synthetic DAG (depth
5, fan-out 7, 43 unique cells, 2,000 shapes/leaf): **5,764 ms**. Deduped: **32 ms**, and 13 ms warm.
At depth 7 / fan-out 9 the old form has 4.8 M path-visits and does not finish in useful time; the new
one is 326 ms cold. The drop path also asked the question twice (once for the Reference refusal, once
inside `CrossWorkspaceCellCopy.Plan`); it now asks once.

**Why deduping is CORRECT here and is not for the bbox walk beside it.** `CellBboxRecursive` cannot
dedupe: a bbox depends on the transform chain that reached it, so one sub-cell down two paths is
genuinely two answers. A layer key is not transformed — a rotation moves a shape, it never changes
its layer — so a second visit can only re-derive what the first contributed. The answer is a set
UNION over reachable cells, and a union needs each cell once.

**Cycles stopped mattering; depth started.** A union over a graph is well defined however the edges
run, and the visited set terminates it, so `Cyclic` from `ResolveForWalk` is now the ORDINARY signal
for a shared sub-cell and is simply skipped. Depth is the opposite: a chain past `MaxDepth` is
truncated, and a SHORT key set is a permit the gate did not earn. That case returns **null** —
"unknown", never "none" — and `ExternalWorkspaceGate` falls back to comparing the whole table.

Own-shape keys are additionally memoized per `LayoutView` reference, on `_shapesBboxCache`'s exact
terms (a resolver hit returns the same instance; a file change makes a new one). Unlike the bbox memo
this one is unconditionally safe to share, since it depends on neither depth nor path — and it matters
because the R47h re-check runs on the process-wide live-refresh tick, where a generated cell's
six-figure via field would otherwise be re-enumerated every time.

---

## A via's span reached the technology but never the interchange writers (2026-09-05)

Reported from a public forum: there is no way to place a blind or buried via, because a via can be
assigned a layer but nothing says where it ends.

**Half of that is a documentation gap and half was a real bug.** The span HAS been expressible since
the via primitive landed — `StackupLayer.SpanFromLayer`/`SpanToLayer` on a `StackupKind.Via` entry
(R-via-3), edited in the technology editor's Stackup tab as `Spans: <conductor> → <conductor>`, with
the via's DRAWING LAYER selecting the entry. One entry per span, each on its own drawing layer, is the
mechanism; the shipped `pcb-4layer_FR-4_62mil_1oz` technology already ships a blind stitching via
beside its PTH, and the MMIC technology ships three entries. Nothing in the editor said so.

### What was actually broken

`DrcConnectivity` and `PlanarExtractor.BuildVias` both read the span. **Every interchange writer
invented its own answer instead**, and both inventions reach a fab:

- **`PcbWriter.WriteVia` wrote `from` = the pad's copper and `to` = `OppositeCopper(...)`.** Every via
  it wrote was a through via. A blind or buried via left circuitRF as a hole drilled clean through the
  board, silently. The IMPORT side has always refused to pretend in the other direction (a blind via
  it reads is reported as degraded), which is what made the asymmetry visible once looked at.
- **`GdsiiWriter`/`DxfWriter`/`GerberExport` keyed the pad off `ViaShape.LandingLayer`**, which
  `CommitViaPlacement` has never set — it writes Layer, X, Y, PadSize, DrillSize and nothing else. So
  every via drawn in the editor exported as a bare barrel with no annular ring (GDSII, DXF), or
  flashed its pad into the DRILL layer's own Gerber file (Gerber). **`GerberLayerOf`'s own doc comment
  asserted the opposite** — "the layout editor's Via tool always sets one" — and that claim is what
  had kept the case looking covered. Copper in a drill file is the exact fabrication bug the paragraph
  above it says L4h fixed; it was only ever fixed for vias that came from an IMPORT.

A fourth, in the editor: **the Via tool was enabled whenever the stackup had any via entry**, and
placed on `CurrentLayerKey` — which after `RebuildAvailableLayers` is the technology's FIRST layer,
i.e. copper. A via there belongs to no entry, so it has no span and is inert in DRC net extraction, in
the planar extractor, and in every export. It drew perfectly and did nothing.

### The fix

`src/Design/Layout/ViaSpanResolver.cs` — one answer to "which two conductors does this via join?",
plus `Explain(...)`, which writes the failure sentence once so the tool tooltip, the inspector and
three export diagnostics all say the same thing about the same state. Consumers: `PcbWriter`
(real span + the `blind` kind atom + a note when it falls back to through), the three pad writers
(`PadLayer` = the shape's `LandingLayer` when an importer set one, else the span's TOP conductor),
`ViaToolAvailability`, and a read-only "Spans" row in the properties inspector.

**Two traps worth keeping.**

- `SpanFromLayer`/`SpanToLayer` carry NO ordering promise — a hand-authored technology may name them
  either way round. The resolver takes the direction from `Stackup.Layers`' own top-to-bottom order
  (R-em-3), never from which field said what. Writing them in the field order produces a layer pair a
  board reader silently mis-orders.
- Arming the Via tool moves the current layer to the sole via layer when the stackup has exactly one,
  and deliberately does NOT choose when there are several — because with several entries the drawing
  layer IS the span choice, and picking one silently is how a blind via becomes a through via again.

### Still open

Nothing on the span itself — the import half landed in
`docs/sonnet-briefs/brief-via-span-import.md` (below). Still unchanged and still out of scope there:
`ViaShape` carries ONE landing layer, so a through via in a 4-layer board cannot state a pad on every
copper layer it passes.

---

## Importing a via SPAN, not just a via (brief-via-span-import.md)

The read half of the above. Two defects, and the brief is right that only the first is about blind
vias — but the SECOND is the one that hit every board anyone ever imported.

**(a) `PcbReader.ReadVia` discarded the pair it had just read.** It identified a blind/buried via
correctly, placed it on its top span layer and recorded a `Degraded` count. `specs[0]`/`specs[1]` were
read and dropped.

**(b) `PcbStackupMapping.Build` emitted no `StackupKind.Via` entry at all** — its `KindOf` maps only
`copper` and `core`/`prepreg`, everything else counted as ignored. So an imported board's technology
had ZERO via entries, and every imported via, THROUGH VIAS INCLUDED, resolved no span. Re-exporting
one wrote it as an unspanned through via with a note.

### The shape it took

`src/Design/Layout/Interchange/PcbViaSpanMapping.cs` is the read-side counterpart of
`ViaSpanResolver`: N vias in, one `StackupKind.Via` entry per DISTINCT span out, each binding a drill
layer of its own, plus the map that tells `PcbImport` which layer to move each via onto. The span
travels from reader to importer on `PcbImportedShape.SpanFromName`/`SpanToName` — the same route
`LandingLayerName` already took, and for the same reason: a span is a process parameter, so it must
not land on `ViaShape`.

`PcbImport.ImportResult` carries `ViaEntries` as **its own field, never folded into `Stackup`**, and
that separation is the whole reason the graft works. Both appliers
(`WorkspaceViewModel.ApplyImportToTechnology`, `Cli/LayoutConvert.MintTechnology`) refuse an imported
stackup when the destination already declares one — right for a substrate, wrong for a via entry,
which declares a drill and cannot invalidate anything. Folding the entries into the stackup would have
made a blind via importable only into a technology with no stackup, which is the one case nobody has.

### Five things measured rather than assumed

- **The brief's estimate of the hard part was right.** The graft mechanism was already wired end to
  end on both paths; what was missing was only the thing that CONSTRUCTS the entry. No new plumbing.

- **A span must be named against the stackup that will be IN FORCE, not the one the file brought.**
  `PcbImport` computes that the same way both appliers do (`destTech.Stackup.Layers.Count > 0` →
  the destination's) and resolves the two source copper names to drawing-layer KEYS, then to whichever
  conductor entries claim those keys. Naming the file's own `F.Cu`/`In1.Cu` into a technology whose
  conductors are called `Top`/`Inner 1` produces an entry `ViaSpanResolver` reads back as **no span at
  all** — the same null the change exists to remove, arrived at by a longer route.

- **`LayoutFragment.ApplyReconciliation` adds a layer only when some SHAPE was already on it**, so
  neither a minted drill layer (nothing is on it until the vias move) nor a span's own conductor (an
  inner plane a blind via lands on, with no artwork on this board) would ever reach the technology.
  Both are added explicitly in `PcbImport`. A via entry binding a layer the technology does not
  declare resolves nothing, so this is not cosmetic.

- **The span layer names must be the SPEC AS WRITTEN, not the layer table's canonical name.** At the
  20171130 epoch a renamed layer's user name occupies the canonical slot, and entities may reference
  either — so canonicalizing a via's `(layers …)` pair mints a SECOND source layer for copper the
  board's own geometry is already on. `PcbReader.SpecNameOf` keeps the spec whenever it is what
  matched.

- **Gate 5 was already green before the change, and the brief's premise for it is wrong.** It expects
  a re-exported import to report one `GerberExport.UnspannedViaPads` per via. It reports zero, and did
  before: `PcbImport.ResolveViaLayers` (then `ResolveViaLandingLayers`) has always set
  `ViaShape.LandingLayer` from the file's own `(layers …)` first entry, and `ViaSpanResolver.PadLayer`
  takes an explicit landing layer ahead of the span. The counter only ever fired for a via that
  carries NEITHER — an EDITOR-drawn one, which is the case `ViaSpanTests` already gates. The test is
  kept as a regression guard; it is not evidence of the defect it was written to describe.

### The one behaviour change outside the import

`(layers …)` is now the span, and the kind atom is only a cross-check. Where a file contradicts itself
— `(via blind … (layers "F.Cu" "B.Cu"))`, which `testdata/pcb-samples/via.kicad_pcb` carries — **the
pair wins**, because it is the specific half and it is what becomes a stackup entry; the overruled
word is reported by count rather than dropped. A via stating no `(layers …)` at all now takes the
outermost declared copper pair, which is what an unqualified via MEANS in this format, rather than
being left spanless for a writer to guess at a second time.

### Gates

`tests/Ui.Tests/PcbViaSpanImportTests.cs` (10), against
`testdata/pcb-samples/via-blind.kicad_pcb` — four copper layers, a real stackup, two vias sharing a
blind Top→In1 span and one through via. Every assertion goes through `ViaSpanResolver.Resolve`, never
through a layer name, or it would pass on a coincidence of naming while the resolver still answered
null. Gate 2 (round trip) runs the real `circuitrf convert` as a separate process and compares the
`(via …)` lines against the source file's, so a graft that works only in `WorkspaceViewModel` cannot
pass it. **Verified as a negative control**: neutering `PcbViaSpanMapping.Build` turns 8 of the 10
red.

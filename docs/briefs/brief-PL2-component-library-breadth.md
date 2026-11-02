# Sonnet Brief — Phase PL2: component library import, breadth

**Depends on PL1** (`brief-PL1-component-library-import.md`) and adds **no new UI, no new cell
shape, and no new neutral model**. Every reader here lands on PL1's `File ▸ Import ▸ Component…`
entry point, PL1's folder classifier, PL1's pin↔pad invariant and PL1's cell-folder output. If this
phase grows a second import path, it has gone wrong.

**Do not start this phase until PL1's completion note exists** — PL1 §15.6 is explicitly asked to say
whether this phase is worth building, on measured evidence. Read that answer first.

**Test loop:**
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. What this phase buys, honestly — and what it does not

**PL2 adds no coverage PL1 does not already have.** Every format below sits alongside one PL1 already
reads, so a part reachable through a PL2 format is reachable through a PL1 one. That is the first
thing to say, because a table of formats is a tempting way to justify this phase and it does not
justify it.

The argument is about which format a given library folder happens to contain, not about which parts
are reachable:

**R-PL2-1. Which formats a library folder holds is chosen when it is assembled, not by circuitRF.** If
a folder holds none of PL1's four, PL1 refuses by name (R-PL1-29) and the fix is to obtain the library
again in a different format. Each reader here removes one class of that round trip.

**Size this phase as insurance, and build it in coverage order, stopping when the return goes flat.**
§3's ordering is that order. It is legitimate to land the first two items and close the phase; it is
not legitimate to land the last two and skip the first two.

**R-PL2-2. Nothing here is a fallback for a PL1 failure.** If two formats for the same part are both
present and readable, PL1's chooser ranks them and the user picks. A reader that quietly retries in
another format after one fails hides a reader bug behind an apparent success, and the two results
will differ in ways nobody looks for.

## 2. The formats, and what each one actually carries

All five carry the complete chain — symbol, footprint, and the pin↔pad map. That is why they are
here and `.dxf` is not (PL1 R-PL1-30).

| Extension(s) | Files per part | Shape |
|---|---|---|
| `.hkp` set | 4 | keyword records, **two grammars** |
| `.p` / `.d` / `.c` | 3 | count-driven records |
| `.PLX`, `.DSL` | 1 | S-expression |
| `.cxf` | 1 | flat tab-separated `KEY=VALUE` |
| `.scr` | 1 | **a command script**, not a data file |

## 3. Build order

**R-PL2-3. In this order, and measure the return after each.**

1. **`.p` / `.d` / `.c`** — plain records, no interpreter, and the simpler join of the two multi-file
   sets. Least work of anything here.
2. **`.hkp` set** — the richest content of the five, and the hardest of the text formats (§4.2).
3. **`.PLX` / `.DSL`** — one reader, two extensions, because they are the same dialect (§4.3). Two
   formats for one reader's cost.
4. **`.cxf`** — trivial (§4.4), and its units are exact.
5. **`.scr`** — the only one needing an interpreter (§4.5). Last, and droppable.

## 4. Per-format notes and the trap in each

### 4.1 `.p` / `.d` / `.c`

Three files that must be read **together**: `.p` holds the part type and, in its `GATE` block, the
pad-identifier → pin-name map; `.d` holds the footprint decals; `.c` holds the schematic decal.
Units are mils throughout.

**R-PL2-4. Records are COUNT-DRIVEN, not line-scannable.** Each entity's header line declares how
many of the following lines belong to it — an outline says how many vertices follow, a part type says
how many attributes and how many gates. A reader that scans for keywords line by line appears to work
on a simple part and silently mis-associates geometry on a complex one, which is the worst possible
failure mode: a plausible footprint with a few strays. Consume by declared count; when a count and
the file disagree, refuse the file and say which entity.

**R-PL2-5. The alternate decals are colon-separated in the part-type line** — `NAME:NAME_M:NAME_L`.
That is the same density-variant set PL1 R-PL1-25 already handles; feed it the same way, and note
that the separator makes a decal name containing a colon unrepresentable. Report rather than guess.

### 4.2 The `.hkp` set

Four files: a part record (the map, plus every metadata property), a cell/footprint library, a
padstack library, and a symbol library. Units are mils in all four, stated two different ways
(`per_inch` scaling in one, a units keyword in the others) — read the declaration, never assume.

**R-PL2-6. Two different grammars share the `.hkp` extension, in the same folder.** The symbol file
is `*KEYWORD` records with coordinates in angle brackets (`<300,-100>`); the cell, padstack and part
files are dotted-depth records where the **number of leading dots is the nesting level** (`.PACKAGE_CELL`
/ `..PIN` / `...XY`). One extension, two parsers. Dispatch on the first non-comment character of the
file, not on the file's name — the names are not part of any specification and have already been
observed to differ between downloads.

**R-PL2-7. Ignore the encrypted twins.** Each of the four files ships beside an encrypted sibling of
the same name. Skip them silently in the classifier — do not report them as unreadable formats,
because the plaintext original sits right there and reporting both doubles the chooser's noise for no
information.

**R-PL2-8. The cell file repeats padstack definitions verbatim.** Measured: the same padstack
declared once per pin that uses it, byte-identical. Deduplicate by name; do not create N identical
padstacks and do not treat a repeat as a redefinition conflict.

**R-PL2-9. All density variants live in ONE cell file.** Unlike every other format here, the
nominal footprint and its two siblings are three `PACKAGE_CELL` blocks in a single file — so the
reader returns a list, and PL1 R-PL1-25's sibling-view handling takes it from there. A reader that
returns the first block and stops loses two thirds of the file with no error.

### 4.3 `.PLX` / `.DSL`

**R-PL2-10. One reader serves both.** They are the same S-expression dialect from a shared lineage —
identical section tags (`padStyleDef`, `patternDef`, `symbolDef`, `compDef`), identical header
(`(asciiHeader (fileUnits MIL))`), differing only in the first line's format banner. Write one
reader, dispatch on the banner for the format's name in messages, and share everything below.

**R-PL2-11. Do not reuse `PcbSexpr`.** It looks like the same grammar and is not: this dialect puts
**commas inside coordinate atoms** (`(pt 0, -100)`) and **unit words after numbers**
(`(pinLength 300 mils)`). Both would need `PcbSexpr`'s tokenizer changed, and changing it puts a
foreign dialect's quirks into the board reader that L4d depends on. A separate ~150-line tokenizer is
cheaper than that risk. State the measured line count in the completion note either way.

**R-PL2-12. The pin↔pad map here has a SECOND indirection, and it is the phase's worst trap.**
`compDef` lists one `compPin` per pad identifier, and each carries **both** a `pinName` and a
`symPinNum` — the symbol's own pin ordinal — and `symPinNum` is *not* the pad number. For example:
pad `2` is symbol pin 9; pad `4` is symbol pin 8; pad `EPAD` is symbol pin 6. A reader that
joins the symbol to the footprint by ordinal produces a fully populated, correctly-shaped, **wrongly
wired** part. There is also a redundant `padPinMap` inside `attachedPattern`; **cross-check the two
and refuse on disagreement** rather than picking one — this is a free consistency check the format
hands us and it is exactly where this class of bug shows up.

### 4.4 `.cxf`

Flat, tab-separated, one record per line: `COMPONENT`, `PACKAGE`, `SYMBOL`, `PAD`, `PIN`, `LINE`,
`RECTANGLE`, `ARC`, `POLYGON`, `TEXT`, each a run of `KEY=VALUE` fields. The simplest of the five and
the fastest to land.

**R-PL2-13. Coordinates are in nanometres, which is circuitRF's DBU exactly** — a pad at `-2.140001`
mm is written `XM=-2140001`. So the conversion is the identity at the default resolution, with no
rounding at all. Assert that in the gate against a second format of the same part rather than trusting
the arithmetic.

**R-PL2-14. `FORM=` and `LAYER=` are small integer enums with no in-file legend.** Do not guess a
shape from an unmapped `FORM` value; import the ones observed, and report an unrecognised value by
number with a count, exactly as every other importer here reports an unknown token.

### 4.5 `.scr`

**R-PL2-15. This is a command script, and it must be INTERPRETED, not parsed.** It is a sequence of
imperative commands with mutable state: `Edit` opens a target, `Layer` sets the current layer for
everything that follows, `Grid` sets units, `Change` mutates a default that subsequent commands
inherit. `Layer` commands are interleaved with the geometry throughout — hundreds of each in one file
— so the geometry is meaningless without replaying the state machine. A reader that pattern-matches
`Smd` and `Wire` lines and ignores the `Layer` lines between them puts every shape on one layer.

**R-PL2-16. `Connect` is the pin↔pad map** and it appears once per terminal. `Pin` and `Smd` give the
two sides.

**R-PL2-17. Refuse on the first command the interpreter does not model, naming it.** An unknown
command in a *data* format costs one skipped entity; an unknown command in a *script* may have
changed state that silently corrupts everything after it. This is the one place in PL1/PL2 where
"report and continue" is the wrong policy, and the reason must be in the code comment.

## 5. What stays out

- **No new UI.** PL1's menu item, chooser and refusal text are widened by registering new formats in
  its classifier, and in no other way.
- **No binary formats.** Not this phase, not as a stretch goal. What remains unread after PL2 is
  proprietary binary containers and three-dimensional models.
- **No `.dxf` route.** PL1 R-PL1-30 stands: those files carry no pad identifiers and no pin names, so
  they cannot satisfy PL1's pin↔pad invariant and must not be offered as if they could.
- **No writers**, of any format, in either phase.
- No multi-gate parts, no netlists, no simulation models (PL1 R-PL1-6).
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 6. Naming — read this before writing a line

**R-PL2-18. Refer to every format by its EXTENSION and nothing else.** Root `CLAUDE.md`
§"Commercial Vendor References" forbids naming the tools, the manufacturers or the parts anywhere in
the repo, including comments, test names, fixture filenames and Messages strings. Say "the `.hkp`
set", "the `.p`/`.d`/`.c` triple", "this dialect".

**R-PL2-19. Every fixture is synthetic** (PL1 R-PL1-32). A real file carries manufacturer names, part
identifiers, descriptions and datasheet URLs in every format, and none of that belongs here. Author
small files following each grammar with invented names. **Grep the diff for the banned vocabulary
before proposing a commit** and report what was removed.

## 7. Gate (acceptance)

Per format landed, plus the phase-wide items:

1. Builds green (`TreatWarningsAsErrors=true`); documented gate green; nothing regresses.
2. **One entry point** — every format imports through PL1's `Import ▸ Component…` and appears in its
   folder chooser, ranked. No new menu item exists.
3. **PL1's invariants hold per format** — for each reader: string pad identifiers, scrambled symbol
   order, both Y flips, unjoined terminals reported. Reuse PL1's gate fixtures' *shape*, authored in
   each grammar.
4. **Count-driven records (R-PL2-4)** — a `.d` fixture whose declared vertex count exceeds the lines
   present is refused, naming the entity; one that is merely long imports correctly.
5. **Two grammars, one extension (R-PL2-6)** — both `.hkp` grammars import from the same folder;
   swapping the two files' names changes nothing.
6. **Encrypted twins invisible (R-PL2-7)** — a folder containing all eight `.hkp` files reports four
   formats, not eight.
7. **Padstack dedupe (R-PL2-8)** — a cell fixture repeating one padstack nine times yields one.
8. **All variants (R-PL2-9)** — a three-`PACKAGE_CELL` fixture yields one cell with three layout
   views.
9. **The second indirection (R-PL2-12)** — a `.PLX` fixture whose `symPinNum` disagrees with its pad
   numbering imports **wired by the map**; a fixture whose `compPin` and `padPinMap` contradict each
   other is refused.
10. **One reader, two extensions (R-PL2-10)** — the same content under both banners produces
    byte-identical cells.
11. **Exact nanometre units (R-PL2-13)** — a `.cxf` pad and the same pad from a millimetre format
    land on the identical DBU coordinate, negative case included.
12. **Script state (R-PL2-15)** — a `.scr` fixture with interleaved layer changes puts each shape on
    its own layer; collapsing them fails.
13. **Unknown command refuses (R-PL2-17)** — a `.scr` carrying one unmodelled command is refused by
    name and creates nothing.
14. **Counters only** — no wall-clock assertion anywhere.
15. **Naming (R-PL2-18)** — a source scan asserts the banned vocabulary appears in no new file,
    comments stripped first (the `brief-harmonicarf-h8` lesson: an unstripped scan passes on a
    comment it should have caught).
16. **Firewall** — `tests/Firewall.Tests` green.

## 8. On completion

Write a **"Phase PL2 — COMPLETE"** entry at the top of `src/Design/RESOLVED.md` — **not**
`CLAUDE.md`. Call out:

1. **Which formats were actually landed and which were dropped**, with the return measured after each
   (R-PL2-3). Dropping the tail is a legitimate outcome; recording *why* is what makes it one.
2. **The `symPinNum` indirection (R-PL2-12)** — how it was proven, and whether the `padPinMap`
   cross-check ever disagreed.
3. **Whether the `.hkp` symbol grammar was worth its cost** — it is the richest content of the five and
   the most work, and the honest answer sizes any future format.
4. **The measured line count of the separate S-expression tokenizer (R-PL2-11)**, so the decision not
   to widen `PcbSexpr` can be re-judged rather than re-argued.
5. **What still cannot be imported and why**, by category and count, phrased as a limitation.

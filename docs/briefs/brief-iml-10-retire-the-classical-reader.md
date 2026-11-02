# Brief IML-10 — Retire the rule-based schematic reader

**Series:** `brief-iml-0-overview.md` (§5, L6) · **Tag:** `R-iml10-<m>`
**Depends on:** IML-9, and IML-7's numbers at or above the rule-based reader's on every committed fixture
**Area:** `src/Design/Schematic/Recognition/` (deletions), `src/Design/Imaging/` (kept — verify), the schematic
`.stroke` symbol templates, tests under `tests/Ui.Tests/Imaging/` and `tests/Ui.Tests/ImageRecognition/`,
`docs/design/image-to-circuit.md`, `src/Design/RESOLVED.md`, design note §12

---

## 1. Goal

One reader (L6). Remove the rule-based front half of the schematic reader now that nothing calls it, keep every piece
something else still uses, and leave the test suite measuring the reader that ships.

## 2. Requirements

**R-iml10-1 — Scan before deleting.** For each candidate in overview §5, a reference scan (`grep` over `src/`,
`tools/`, `tests/`) lists its callers. A file goes only when its callers are the rule-based schematic reader itself.
Known shared code that **stays**: the IM-1 raster core (`src/Design/Imaging/`, used by the layout tracer, the plot
reader and now IML-7's assembly); `ImageKind` (routing in the dialog and `recognize`); the stroke text reader and its
glyphs where AS-10's silkscreen reader uses them; `ValueGrammar`, `KnownWords`, `LabelAssociation` (used by IML-7);
`SchematicImageEmit`, `SchematicImageDrawing`, `SchematicImageOverrides`, `SchematicPasteOutcome`, the report types.
The list of what went and what stayed, with each "stayed because …", is design note §12.

**R-iml10-2 — Delete.** Remove the files and the schematic symbol `.stroke` templates the scan clears, their
`EmbeddedResource` items, and options that no longer mean anything (`SchematicImageOptions` fields only the old reader
read — their JSON names are still ignored on read, IML-9 R-iml9-2's trap). `PictureGrid` stays only if IML-8's
no-reference-part fallback uses it.

**R-iml10-3 — Tests follow the reader.** Tests of deleted classes are deleted with them. Tests that held *behaviour*
(a picture reads as this circuit) are kept and re-pointed at the new reader — their pictures are the regression set.
Per-heuristic facts (a plate rule, a hatching rule) go with their code. Nothing is skipped to make the suite green: a
fact the new reader fails is listed in design note §12 with the picture and the reason, and either fixed in a follow-up
training round or removed with the owner's agreement.

**R-iml10-4 — The record.** `docs/design/image-to-circuit.md` §9–§13 and §17–§26 get one line at their head pointing to
`schematic-vision.md` and saying the schematic reader they describe was replaced. `src/Design/RESOLVED.md` gets one
entry: what the rule-based reader taught that the new one still relies on (the emit's as-drawn rule, the junction-dot
rule, pin markers are not junctions, the size frame's history), so the lessons outlive the code.

## 3. Gates

- Build green with warnings-as-errors where the repo uses them; Firewall green.
- The image test classes (targeted run): green, with the count of deleted / re-pointed / new tests in design note §12.
- `git grep` for each deleted type's name returns only history documents (briefs, RESOLVED.md, design notes).

## 4. Not in this phase

Anything in the layout-picture or plot readers. Removing `src/Design/Imaging` code that a layout or plot reader uses.

## 5. Traps

- **The silkscreen reader shares glyph code** with the schematic text reader — delete by caller, not by folder.
- **`InternalsVisibleTo` test helpers** (`SchematicPictures`, `SymbolPictures`) may be used by layout tests too.

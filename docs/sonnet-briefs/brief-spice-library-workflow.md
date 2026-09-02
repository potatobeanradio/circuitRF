# Sonnet Brief — SPICE library workflow: the include closure, `.lib` sections, and multi-definition import

**Design:** `docs/design/spice-models.md` — §4.1/§4.2 (importing several definitions), §8.11 (what a
`.lib` section is), §10 (choosing one), §11–§12 (the file closure and the archive).
**Sibling brief:** `brief-spice-behavioural-sources.md` — `E`/`G … VALUE={…}`, nonlinear charge and
`.func` inlining, which is what makes these files' *devices* importable at all. **No dependency
either way.** That brief reaches the nonlinear solver and gates on `Engine.Tests` (~3½ min); this one
is file handling and dialogs and gates in well under a minute.
**Code:** `src/Ui/Archive/` (`WorkspaceArchiveScanner`, `DocumentFileRefs`, `WorkspaceArchiveWriter`),
`src/Ui/Schematic/` (`SpiceCellImport`, `SubcircuitCellBuilder`, `SpiceModelPeek`,
`SpiceModelSymbolProvider`), `src/Ui/Views/Dialogs/SpiceCellPickerDialog.*`,
`src/Core/Netlist/Spice/SpiceNetlistReader.cs` (the two public entry points only).

**One sentence:** a SPICE deck is rarely one file, rarely offers one alternative, and rarely holds
one part — and circuitRF today archives only the entry point, cannot be asked for a section, and lets
a shared core permanently block a library's second part.

---

## 0. Why this is separate, and what it is measured against

The measurements behind both briefs come from four real library files read through
`SpiceNetlistReader` and `SpiceCellImport` on 2026-09-01; `spice-models.md` §8 records them in full
and **the files themselves must never enter the repository** (`docs/design/pdk-import.md` §0 — cite a
count, never an identity).

**Nothing here waits on the device work, and none of it is waited on.** Two of the three milestones
are demonstrable today on an ordinary file:

- The archive gap is live right now — a `.lib` that `.include`s a sibling, referenced from a
  schematic, arrives at the recipient broken **whether or not** its subcircuits are importable.
- The sibling-import collision is reproducible today with any two-definition file sharing a core;
  it is not specific to the measured files.
- Section selection is demonstrable on a two-section fixture. (None of the four measured files uses
  a `.lib` section — the format feature the extension is named after is the one thing they do not
  exercise.)

Their *visible effect on the measured files* does wait on the sibling brief, because those files'
devices are refused for unrelated reasons. Do not let that reorder the work: these are cheap,
independently correct, and independently testable.

---

## 1. Structural facts

1. **The reader is already complete for `.lib` sections, and has exactly one consumer.**
   `Session.Run` tracks `.LIB <name>`/`.ENDL` framing above conditionals, records the names into
   `SpiceNetlistResult.Sections` grouped by file, and skips every section when none was requested
   (sections are alternatives; choosing one nobody asked for is a guess). `.LIB <file> <section>`
   reads one out of another file. `PdkCorners` uses all of it. **But `ReadFile` and `Read` both
   hard-code `section: null`** and nothing outside `PdkCorners` reads `Sections`. M3 is plumbing and
   UI, not parsing.

2. **One refused element refuses the whole subcircuit, and refusal propagates up through `X` calls.**
   That rule is correct and stays (a netlist with a line missing is not a smaller circuit, it is a
   different one that elaborates and produces numbers). It is also why fact 0's table is so flat:
   a single unsupported line anywhere in a dependency chain refuses everything above it.

3. **An external SPICE file already archives — as exactly one file.** `DocumentFileRefs` finds a
   `SpiceModel`'s `File` parameter (workspace-relative, `RefBase.Workspace`, matching
   `SnpPathPolicy`), `WorkspaceArchiveScanner.AddExternalFiles` offers it ticked-by-default, and
   `WorkspaceArchiveWriter` repoints it at `external/<name>`. But `DocumentFileRefs` walks documents
   **as JSON** and never opens the deck, so a `.include`d sibling is invisible; and `external/` is
   flat, so a relative `.include` would not resolve even if the sibling were ticked.
   `SpiceNetlistResult.FilesRead` already records the transitive set. Nothing asks for it.

4. **Nested definitions already import as several cells; siblings cannot import at all.**
   `SubcircuitTranslation.Dependencies` is transitive and leaf-first, and `SubcircuitCellBuilder.Write`
   writes one cell per dependency plus the chosen one — the right model, because a cell instance
   references a cell *folder* and there is nowhere else for a nested definition to live. But the
   picker is single-select (`SpiceCellPickerDialog` uses `SelectedItem`), and `Write` **throws** when
   a planned folder already exists ("A cell named '…' already exists here, and '…' needs it because
   it calls that subcircuit"). Two of the four measured files have two top-level parts over a shared
   core — 4 shared cells in one, 1 in the other — so importing one variant permanently blocks the
   other. Never overwriting is right; refusing *identical* content is not.

5. **A written cell records nothing about where it came from.** No source file, no definition name,
   no content hash. So "is the cell on disk the same definition?" can only be answered by a content
   diff until provenance is stored.

6. **`.func` and global `.param` are read and then discarded by BOTH consumers.**
   `SubcircuitTranslator.TranslateAll` takes `Library.Cells`, `ModelCards` and `IncompleteCells` and
   never touches `result.Functions` or `result.Variables`; `SubcircuitCellBuilder` mentions neither;
   `SpiceModelNetlist` copies the cell's own `Variables` and no functions. Three of the four files
   declare 8, 102 and 9 `.func`s and are built out of them. **The fix belongs to the sibling brief**
   (it inlines them, which is also what makes the flat `TestBench.Functions` namespace stop
   mattering) — recorded here because it is the same "a cell cannot hold that" question M2 raises,
   and because a cell-scoped `.param` is already safe for exactly the reason M2 is worth doing.

---

## 2. Milestones

All three are independent of each other. Suggested order is M1 first: it is the one whose failure is
live today and silent.

### M1 — archive the whole deck, not the entry point

- `WorkspaceArchiveScanner`: when an external reference is a SPICE deck, read it through the
  existing `SpiceModelPeek` mtime cache and take `SpiceNetlistResult.FilesRead` as its closure.
- Archive the closure as **one option row with its own subtree**, rooted at the deepest common
  ancestor of the closure, at `external/spice/<group>/…`, preserving relative structure — so every
  `.include` inside it resolves after the copy exactly as it did before, and repointing rewrites
  **only the entry point**. That is the property that makes this robust rather than clever.
- A closure of one file stays one row and behaves exactly as it does today (no regression to the
  common case).
- Skip any closure member that already lives inside the workspace or inside an included kit —
  the same rule `AddExternalFiles` already applies to the workspace.
- The row's detail says how many files travel and which document pulled it in.
- Ticked by default, unchanged: it is small, and without it the recipient's design is missing a
  piece of itself.
- **Check the extension gate.** `DocumentFileRefs.TryResolve` rejects an extension outside
  `[2,12]` characters. `.lib`, `.sp`, `.spi`, `.inc`, `.cir`, `.mod`, `.txt` all pass; confirm
  rather than assume, and add a test for the shortest one.

### M2 — importing several definitions from one file

A multi-definition file already imports as several cells for **nested** definitions — one per
transitive dependency, leaf-first — and that is the right model, because a cell instance references
a cell folder and there is nowhere else for a nested definition to live. **Siblings are the gap, and
the second one cannot be imported at all**: the picker is single-select, and
`SubcircuitCellBuilder.Write` refuses when a planned folder exists, so a shared core blocks the
second variant permanently. Two of the four files have exactly that shape (4 shared cells and 1).

- **Multi-select picker.** N chosen definitions, one shared dependency plan, each shared core
  written once. Keep the refusal semantics for everything the plan does *not* own.
- **Reuse an identical existing cell** rather than refusing, on **proven content identity only**.
  If it differs, keep today's refusal and say it may have been edited since. Never overwrite —
  that rule does not bend.
- **Record provenance** on a written cell (source file, definition name, content hash). It records
  nothing today, so reuse can otherwise only be a content diff.
- **Decide the folder layout deliberately.** Importing one file's top-level part writes **30 cell
  folders flat** into the parent directory. A per-import subfolder fixes the clutter and changes the
  `"../../" + name` reference convention every written cell depends on. Measure the churn before
  choosing; do not change it as a side effect.
- Tests: importing sibling A then sibling B succeeds and yields **one** copy of the shared core;
  importing B when the core on disk has been edited refuses with a message naming the edit; a
  multi-select import writes the same bytes as two sequential imports would have.

### M3 — choosing a `.lib` section

- `SpiceNetlistReader.ReadFile(path, section = null)` and `Read(text, …, section = null)` — the
  parameter is already threaded through `Session.Run`; only the public entry points hard-code null.
- `SpiceCellImport.Scan` returns the file's `Sections` alongside its candidates. A file that
  declares alternatives is scanned once whole (which is exactly the pass that collects the names,
  because it skips their contents) and re-scanned for the chosen section.
- Import picker: a **Section** combo above the definition list, shown only when the file declares
  any. `SpiceModel` parameter panel: the same, as a `Section` parameter beside `File` and `Name`.
- **The section is part of the reference.** `SpiceModelSymbolProvider.RefFor` must include it in the
  `spicemodel://` reference, or `CellSymbolResolver`'s cache returns the previous section's symbol
  when only the section changes.
- Default is no section — today's behaviour exactly — and a file with unchosen sections keeps saying
  so in its notes rather than picking one.

---

## 3. Tests

**Every fixture synthetic** — the repository commits no third-party kit data
(`docs/design/pdk-import.md` §0). `Ui.Tests/` for the archive, the picker and the cell builder;
`Core.Tests/Netlist/` for the reader's section entry points.

- **M1** — a three-file deck (entry point `.include`s a sibling in a subdirectory, which `.include`s
  a third) referenced from a schematic outside the workspace: the plan offers **one** row whose
  closure is 3; the written archive contains all three at their original relative offsets; the
  `.csch` names the entry point only; and **extracting the archive and re-reading the deck through
  `SpiceNetlistReader` yields the same `Library.Cells` as reading the original** — that is the
  assertion that actually proves the includes resolve, rather than that three files were copied.
  Plus: a single-file reference still produces one row at `external/<name>`, byte-identical to
  today; a closure member inside an included kit is not duplicated; a `.sp` extension resolves.
- **M2** — importing sibling A then sibling B succeeds and yields **one** copy of the shared core;
  importing B when the core on disk has been **edited** refuses, with a message that says so rather
  than the generic already-exists sentence; a multi-select import of A and B writes the same bytes as
  two sequential imports would have; a genuinely different definition under a name already in the
  workspace is still refused. Plus the case that must not regress: importing a single definition with
  nested dependencies is byte-identical to today.
- **M3** — a two-section fixture: reading with no section reads nothing and notes both; reading
  section A reads A's definitions only; the `spicemodel://` reference differs between sections
  (guarding the symbol cache); requesting a section the file does not declare reports what it
  *does* offer.
- Regression: all existing `Spice*Tests` and `WorkspaceArchive*Tests` in `Ui.Tests` and
  `Core.Tests` green and unchanged.

---

## 4. Gates

`dotnet test tests/Core.Tests` then `dotnet test tests/Ui.Tests` — two invocations, this SDK rejects
two project paths in one. **Nothing here should need `Engine.Tests`**; if a change reaches it, that
is a signal the boundary with the sibling brief has been crossed. **Run once and read
`TestResults/last-run.trx` for failures** — never re-run to find out what broke.

For M1, verify the archive end to end rather than by inspection: extract the written `.zip` to a
temporary directory and re-read the deck from there.

No new `Category=Benchmark` timing test.

---

## 5. On completion

Findings to **`src/Ui/RESOLVED.md`** (the archive, the picker, the cell builder) and
**`src/Core/RESOLVED.md`** (the reader's section entry points). **Never to any `CLAUDE.md`.** Update
`docs/design/spice-models.md` §4.1's open question about folder layout with whatever was decided, and
§12 with what the archive actually does now. Do not commit; the owner commits.

Before any commit is proposed, grep the diff for vendor, product, part and simulator-dialect names —
`docs/design/pdk-import.md` §0 — and report what was removed.

---

## 6. Out of scope, deliberately

- **Everything in `brief-spice-behavioural-sources.md`** — the element letters, the SDD, the branch
  row, charge, `.func` inlining. If a milestone here starts needing one of those, stop: the split is
  wrong and it is worth saying so rather than merging them back quietly.
- **A general SPICE-deck importer.** This reads what a file needs in order to DEFINE A DEVICE.
  Analysis decks, `.control` blocks and testbenches stay named-and-skipped.
- **Changing what `Copy to Workspace as Cell…` offers in the project tree.** `.lib` and `.txt` are
  picker-only by an explicit decision (`ModelCardCellBuilder.IsSpiceCellFile`); leave it.
- **Editing a kit's own files inside an archive.** A kit travels as its own row and its internal
  references are written against its folder; the closure walk skips anything already inside one.

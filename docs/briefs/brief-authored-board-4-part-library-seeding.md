# Brief 4 — the library already knows which rows are missing

**Series:** [authored board](brief-authored-board-0-overview.md) · **Tag:** `R-ab4-n` · **Phase:** P2
**Area:** `src/Ui/RailRf/PartLibraryEditorViewModel.cs`, `PartLibraryCoverageContext.cs`,
`src/Ui/Views/RailRf/PartLibraryEditorView.axaml*`, `src/Ui/ViewModels/WorkspaceViewModel.cs`
**Depends on:** [railRF 24](brief-railrf-24-part-library-editor.md) · **Blocks:** nothing
**Independent of briefs 1-3** — it can land in any order.

---

## 0. What this brief delivers

`.crlib` is the one companion in this whole series that is already solved for authoring: railRF
brief 24 shipped a real document — double-click from the project tree, a grid, undo, Save As,
revision control — over a format `PartLibraryIo` both reads and writes.

The gap is one step wide. **`AddRow` adds an empty row** (`PartLibraryEditorViewModel.cs:716`) while
`PartLibraryCoverageContext` already knows, by resolved path rather than by string comparison,
exactly which part numbers the design asked this library about and did not find. A user who has
just imported a thirteen-part board types thirteen part numbers that the application could have
listed.

---

## 1. `R-ab4-1` — add the rows this design asked for

**`R-ab4-1a`** One command in the Part Library editor, beside Add. It adds a row per **missing**
part number from `PartLibraryCoverageContext.PartNumbers` — the ones `PartLibrary.Coverage` already
distinguishes as uncovered — in the design's own document order, with repeats collapsed.

**`R-ab4-1b`** The button names the design and the count: *"Add the 9 parts `Sensor board.crail`
asks for"*. The subject is already carried on the context (`Subject`) for exactly this, and without
it a user with two boards in a workspace cannot tell which one they are about to seed from.

**`R-ab4-1c`** Disabled, not hidden, when the count is zero, with the reason in its tooltip: a
library that covers its design is the good state and a control that vanishes on success reads as a
control that broke.

**`R-ab4-1d`** No coverage context resolves — the library belongs to no `.crail` in this workspace —
and the command is absent. That is `For`'s own null, and it is an ordinary state for a shared library
being edited on its own.

---

## 2. `R-ab4-2` — a seeded row states the part number and nothing else

**`R-ab4-2a`** Every field except `PartNumber` is left null. `PartLibraryRow`'s own summary is the
rule: *every electrical field is optional, because a real maintained table is partly populated and
§9's whole point is that partial population must be VISIBLE rather than filled in.*

**`R-ab4-2b`** This matters more than it looks. `PartLibrary.Coverage` counts a row as covered once
it exists, so a seeded row carrying a guessed capacitance moves a part out of the uncovered count
while contributing a wrong number to the answer. **A seeded row must therefore still read as
incomplete** — it is counted in `PartsWithoutBiasCurve` and in the strip's own headline, and the
grid marks it.

**`R-ab4-2c`** `DielectricClass` in particular stays null. Brief 11's ESR fallback keys on it and a
null one produces *a part marked as having no class* rather than *a part quietly given X7R's
dissipation factor* — that record's own words, and the seeding must not undo them.

---

## 3. `R-ab4-3` — where a BOM was supplied, the row is not empty

This is the one place fields can be filled honestly, because they came from a file the user handed
over rather than from an inference.

**`R-ab4-3a`** Where the design was imported with a BOM, a seeded row takes `Description`,
`Footprint` and the value from the matching `BomRow`, plus whatever `BomRow.Parsed`
(`BomDescriptionParse`) recognised in the description — which already includes the dielectric class
and the voltage rating, and which railRF already shows *beside the description it came from, for
correction*.

**`R-ab4-3b`** It carries that provenance the same way: a field filled from a BOM reads as filled
from a BOM, so a wrong parse is correctable rather than mysterious. The parts table already does
this and the pattern is reused rather than re-invented.

**`R-ab4-3c`** No BOM, no fields. R-ab4-2a — an empty row, honestly empty.

---

## 4. `R-ab4-4` — one undo, and a library that does not exist yet

**`R-ab4-4a`** The whole batch is **one** undo entry, through the existing
`PartLibrarySnapshotCommand`. Nine rows that take nine undos to remove is the Match Designer's
slider defect in a new place.

**`R-ab4-4b`** A `.crail` naming no part library at all offers to create one — `New ▸ Part Library`,
seeded in the same act. It writes through `PartLibraryIo.SaveToFile` and lands in the workspace
exactly where the New Cell dialog's own default would put it, because a file appearing somewhere the
user did not name is the surprise `clone` refuses to cause.

**`R-ab4-4c`** Nothing here reaches outside the workspace and nothing here fetches a part's data from
anywhere. circuitRF holds no parts database and this brief does not start one.

---

## 5. Gate

`tests/Ui.Tests/RailRf/PartLibrarySeedingTests.cs`.

1. **Only the missing ones.** A library covering four of thirteen seeds nine, in document order,
   repeats collapsed (R-ab4-1a).
2. **A seeded row is empty but for the part number**, and **still counts as incomplete** — assert the
   strip's own uncovered/no-bias-curve numbers after seeding (R-ab4-2a, R-ab4-2b). This is the row
   that will be got wrong, because the obvious implementation makes the count look better.
3. **`DielectricClass` is null and the ESR fallback still reports no class** (R-ab4-2c).
4. **With a BOM, the fields arrive, with their provenance** (R-ab4-3a, R-ab4-3b).
5. **One undo removes all nine** (R-ab4-4a).
6. **Zero missing disables rather than hides; no context removes** (R-ab4-1c, R-ab4-1d).
7. **The created library lands where New Cell would put it, and the `.crail` now resolves it**
   (R-ab4-4b).

## 6. Scope

- **No parts database, no network, no vendor lookup.** R-ab4-4c.
- **No guessed electrical fields.** R-ab4-2.
- **No change to `PartLibrary` or `PartLibraryIo`.** The format is unchanged; this is an editor
  gesture over it.
- **No automatic seeding.** It is a command a user presses. A library that grew rows on open is a
  library whose diff nobody trusts.

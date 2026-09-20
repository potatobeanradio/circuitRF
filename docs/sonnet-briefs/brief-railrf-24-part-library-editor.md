# Brief 24 — the part library is a document, and documents can be opened

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail24-n` · **Phase:** feature
**Area:** `src/Ui/RailRf/PartLibraryEditorViewModel.cs` (new),
`src/Ui/Views/RailRf/PartLibraryEditorView.axaml*` (new),
`src/Ui/ViewModels/WorkspaceViewModel.cs` (document routing),
`src/Design/RailRf/PartLibraryIo.cs` (read only)
**Depends on:** 1-18 · **Blocks:** nothing
**Found by:** a first-time designer's pass, 2026-09-20 — *"how do i open it .. nothing happens with
double click on it"*

---

## 0. What this brief delivers

`PartLibraryIo` reads and writes `.crlib`, `PartLibrary` models it, `RailPartResolver` resolves
against it, and the shipped example ships one at `examples/Power Rail/parts/decoupling.crlib`.

**Nothing in the application can open it.** Grep the whole of `src/Ui` for `crlib` and you get four
hits: two comments, a fixture path, and an options field. There is no editor, no double-click
handler, and no menu row.

So a user who wants to know why `C10` models the way it does — which is the question the whole Q2
answer rests on — has a JSON file and a text editor.

---

## 1. `R-rail24-1` — a document like the others

### `R-rail24-1a` — opened the way everything else is

Double-click in the project tree, and a **Part Library** row on the same menu the other document
types are on. It routes through the workspace's own open-or-activate path, so it gets one session
per path, a dirty mark, Save, Save As, undo/redo and revision control by construction — not four
partial reimplementations of them.

### `R-rail24-1b` — a grid, because it is a table

One row per `PartLibraryRow`, columns for the fields that exist
(`src/Design/RailRf/PartLibrary.cs:70`): part number, description, footprint, dielectric class,
voltage rating, capacitance, self-resonant frequency, stated inductance, ESR, model reference.
Add and remove rows. No invented fields.

### `R-rail24-1c` — the bias curve is a sub-editor

`PartLibraryRow.BiasCurve` is a list of `(bias, capacitance)` points and is the reason a ceramic's
derating is honest rather than a rule of thumb. It gets a small editor of its own on the selected
row, and a plot, because a monotonic curve with one transposed point is invisible in a grid and
obvious on a chart.

---

## 2. `R-rail24-2` — the editor says what the library does NOT know

The library's own model already computes things it should be showing, and today nothing shows them:

**`R-rail24-2a` — the inductance disagreement.** `PartLibraryRow.DerivedInductanceHenries` derives
L from C and the self-resonant frequency, and `InductanceDisagreement` compares it with the stated
L against `PartLibrary.InductanceTolerance` (5 %). `PartLibrary.InductanceDisagreements()` returns
the list. **Show it on the row**, where it can be fixed, rather than only in a run's notes.

**`R-rail24-2b` — the ESR provenance.** `EsrProvenance` distinguishes a stated ESR from a class
default, and `RailPartModel.IsIndicative` is true for the latter. A row modelled from a dielectric
class rather than from data says so on its face. That is the difference between a number somebody
measured and a number railRF assumed, and it is the thing a reader most needs to know.

**`R-rail24-2c` — coverage against a document.** `PartLibrary.Coverage(referencedPartNumbers)`
already answers *how many of this board's parts does this library know, and which of them have a
bias curve*. Where the editor was opened from a `.crail`, show it: *"13 referenced, 11 known, 6 with
a bias curve"* is the first thing a user of a library wants and it is already computed.

---

## 3. `R-rail24-3` — a refusal is a refusal

`PartLibraryRow.Refusal()` and `PartLibrary.Refusal()` exist and state what makes a row or a file
unusable. The editor surfaces them per row and per file, in the shape every other railRF refusal
takes (R-rail7-4: the sentence shows in the strip and turns the offending control red, rather than
hiding behind a disabled button).

**`R-rail24-3a`** Saving a file with a refused row is **allowed** — an editor that will not let you
save work in progress is an editor people work around — and the refusal travels with the file, so
a run against it says the same thing.

---

## 4. `R-rail24-4` — the footprint column

`PartLibraryRow.Footprint` is a `string?` that nothing writes and nothing reads.

**`R-rail24-4a`** With the [footprint series](brief-footprint-0-overview.md) landed, it becomes a
picker over `FootprintCatalog` rather than a free-text box — the same rows, the same metric twin,
the same ambiguity refusal for a four-digit token that names a case in both schemes.

**`R-rail24-4b`** Without that series, it stays free text and is shown. A field that exists and is
invisible is a field that silently disagrees with whatever else claims to know a part's package.

---

## 5. Gate

`tests/Ui.Tests/RailRf/PartLibraryEditorTests.cs`.

1. **It opens.** Double-clicking `examples/Power Rail/parts/decoupling.crlib` in the project tree
   opens a Part Library document. **Fails at HEAD: nothing is registered for the extension.**
2. **Round trip.** Open, change one field, save, reload, compare — and a file opened and saved with
   no edit is byte-identical, so opening a library does not rewrite it.
3. **One session per path.** Opening twice activates the same document.
4. **Dirty and undo.** An edit marks it dirty and undoes.
5. **The disagreement shows.** A row whose stated L and derived L differ by more than the tolerance
   is flagged; one within it is not (R-rail24-2a).
6. **Indicative ESR is marked.** A row with no stated ESR shows as class-default (R-rail24-2b).
7. **Coverage.** Opened against the shipped `.crail`, the counts match
   `PartLibrary.Coverage`'s own (R-rail24-2c).
8. **A refused row saves.** It saves, and a run against the saved file reports the same refusal
   (R-rail24-3a).
9. **The bias curve edits and plots.** Adding a point changes the derated capacitance the resolver
   computes.

## 6. Scope

- **No new format and no format change.** `PartLibraryIo` is read-only from this brief's point of
  view; if a field needs adding, that is a different brief with a format revision in it.
- **No vendor library shipped.** The example's `decoupling.crlib` stays synthetic.
- **No part search, no supplier lookup, no import from a distributor.** A library is a file.

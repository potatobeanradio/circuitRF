# Brief AS-8 — Design ▸ Create Schematic from Artwork…

**Series:** `brief-artsch-0-overview.md` (§3, D4, D5, D8, D10, D11, D12, D15, D16) · **Tag:** `R-as8-<m>`
**Depends on:** AS-6 (AS-7 is independent of this phase)
**Area:** `src/Ui/Views/WorkspaceWindow.axaml` (Design menu), new `src/Ui/Recognition/`
(`CreateSchematicFromArtworkDialog.axaml[.cs]`, `CreateSchematicFromArtworkViewModel.cs`,
`PartsTableRowViewModel.cs`, `ArtworkCrossProbe.cs`), `src/Ui/ViewModels/WorkspaceViewModel*.cs` (the command),
`src/Ui/Layout/LayoutEditorViewModel*.cs` (selection outline, highlight), the parameter editor (the `FromArtwork`
row), `src/Design/Layout/Em/EmRunService.cs` (the refusal pointer), `docs/user/src` (the page)

---

## 1. Goal

The command a user reaches from a focused layout: one dialog that shows what was recognised — above all the parts
table — lets them correct it, and writes a schematic that opens ready to simulate and tune.

## 2. Requirements

**R-as8-1 — The menu row.** **Design ▸ Create Schematic from Artwork…**, directly below Update Schematic from
Layout, enabled when a saved layout view is focused (a scratch layout is refused as Update Schematic from Layout
refuses it). No gesture by default. It never runs except from the user's invocation (the L5 rule: no save, open or
activation hook).

**R-as8-2 — The dialog.** Non-modal, sized for the table. Top to bottom:
- **Target**: *New cell* with a name box (default `<cell>_model`, validated live by `NameValidator`), and *This
  cell's schematic* — the second option **shown only when the artwork cell has no schematic view** (D4). When the
  name names a cell whose schematic carries `ArtworkSource`, the button reads **Replace** and the replace is
  confirmed in the button's own flyout ("Replaces <cell>'s schematic; a checkpoint is taken first").
- **Scope**: *Selection* (enabled when the layout has a selection; default then) or *Whole layout*.
- **Options** row (compact, each a combo or box with a tooltip): Ground (Auto / pick on layout — a pick tool that
  returns a point), Ground vias (*Model as VIAGND* / *Plain GND*), Coplanar lines (*Auto* / *Microstrip* /
  *GCPW*, with the factor in an expander), Frequency start / stop / points.
- **Companion files**: BOM… and Placement… pickers. A placement file that does not state its origin shows the
  three-way origin choice **with nothing pre-selected** (railRF's rule, the same control), and recognition waits
  for it.
- **Parts table** (R-as8-3).
- **Report strip**: one line per report class with its count; a class with anchors expands to its items.
- Buttons: **Create** (or Replace), **Export Parts…** (CSV), **Import Parts…** (CSV), Cancel.
Recognition runs when the dialog opens and again (debounced, cancellable, with a progress strip) whenever scope,
options or companion files change. Edits in the table are not lost by a re-run: they are held as the overrides a
parts CSV would carry (R-as4-8) and re-applied.

**R-as8-3 — The parts table.** The `PartsTable` columns; `Kind`, `Value`, `Model` editable (Kind a combo of
R-as4-2's kinds, Value a text box with unit parsing and a red border on a dimension mismatch, Model a combo of
*Ideal* / files found / *Browse…*). Unknown values show their variable name greyed in the Value cell. Sorting by
any column; a filter box. Confidence as a small coloured dot with the evidence in its tooltip — no prose under the
table. **Selecting a row highlights the part's pads on the layout** (the `RailPartMarks` resolution, drawn as the
editor's existing highlight overlay) and pans to it if it is off screen.

**R-as8-4 — Create.** Calls `ArtworkRecognition.Run` with the dialog's state (the same entry point the CLI calls —
D2). On success: the new schematic opens **focused**; the report goes to **Messages** (one line per class,
expandable to its anchors); the Tuning panel, which follows the focused schematic, lists the unknown-value
variables. On refusal: the sentence in the dialog's status line, the dialog stays open.

**R-as8-5 — Cross-probe (D12).**
- A Messages report item with an anchor: double-click selects and zooms the artwork there.
- A `FromArtwork` component in the schematic: context menu **Show in Artwork** opens the source layout (from the
  provenance block) and highlights the anchor (a line element's polyline, a part's pads).
- The parameter editor shows a **Models existing artwork** check box for `FromArtwork` components; clearing it is
  one undo step and hands the component back to layout sync (D5).

**R-as8-6 — The MoM refusal points here.** When an EM run is refused at the unknown ceiling
(`EmRunService`'s `Refused` outcome carrying the mesh-budget sentence), the refusal gains one sentence: *"A board
of ordinary lines and parts can be modelled without EM: Design ▸ Create Schematic from Artwork (circuitrf
recognize)."* Only on that refusal, not on any other.

**R-as8-7 — User page.** `docs/user/src`: a reference page for the command (target, scope, options, the parts
table, what is recognised and what is not, the report, Show in Artwork, the `FromArtwork` flag and the L5 commands, and **what happens to an IC**: its RF pins become ports, and the
device itself can be brought in afterwards with Import Component — a link to that page),
and a row in the Design-menu table. Edit sources only; **do not run DocGen**.

## 3. Not in this phase
The swap command (AS-11); OCR (AS-10).

## 4. Gates (minimal tests, run only these classes)
View-model tests, headless (the GUI cannot be launched from an agent's shell):
- `CreateSchematicFromArtworkViewModelTests` — the artwork-cell option is absent when the cell has a schematic view
  and present when it has none; a replace target relabels the button; a table edit survives a re-run caused by an
  option change; a placement file with no stated origin blocks recognition until a choice is made, and no choice is
  pre-selected; Create calls the same entry point as the CLI with the same options (a recorded fake).
- `ArtworkCrossProbeTests` — Show in Artwork resolves the provenance's `.clay` and the anchor; a component without
  `FromArtwork` has no menu row.
- `EmRunServiceTests` (existing) — one case: the unknown-ceiling refusal carries the pointer sentence; another
  refusal does not.

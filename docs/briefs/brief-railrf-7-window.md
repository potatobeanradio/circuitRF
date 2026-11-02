# Brief 7 — the window: the Match Designer's chrome, and four things that are new

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail7-n`
**Area:** `src/Ui/RailRf/`, `src/Ui/Views/RailRf/` · **Depends on:** 1, 2 · **Blocks:** 8, 9
**Design note:** [`railrf.md`](../design/railrf.md) §11.1–§11.5, §2.3

---

## 0. What this brief delivers

One resizable non-modal window, opened per railRF document, plus the import path that gets a board into
one. §11 is *"deliberately short on invention: it names what is taken, and only describes what is
genuinely new."* This brief is the same.

| Piece | File |
|---|---|
| `RailRfWindow` | `src/Ui/Views/RailRf/RailRfWindow.axaml` + `.axaml.cs` |
| `RailRfViewModel` (+ partials) | `src/Ui/RailRf/RailRfViewModel*.cs` |
| `RailPartRowViewModel`, `RailSourceRowViewModel`, `RailLoadRowViewModel`, `RailAggressorRowViewModel` | `src/Ui/RailRf/` |
| `RailImportDialog` | `src/Ui/Views/RailRf/RailImportDialog.axaml` |
| The Properties-panel summary + **Open railRF…** | `src/Ui/RailRf/RailPropertiesPanel*.cs` |

---

## 1. `R-rail7-1` — taken from the Match Designer, and "taken" means the same classes

§11.1 is explicit: *"Not 'inspired by' — the same classes, the same conventions, and where possible the
same controls."* Read `src/Ui/Views/Match/MatchDesignerWindow.axaml` before writing any XAML; it has had
roughly ten rounds of refinement and every one of them is a decision already made.

- **Two-tier chrome.** `Border Classes="pane"` for a region, `Border Classes="card"` for a group inside
  it, so a group reads as a thing sitting *on* the panel rather than a rectangle drawn on it. One tile
  border, 6 px corners.
- **Sentence-case headings**, never shouted.
- **Label left, value right**, numbers in one right-aligned column whether the row is settable or
  read-only. Settable values are `ctl:InlineEditText Classes="val"` — unit handling, validation and
  formatting come for free, an `InlineEditText` at rest is a bare `TextBlock`, and a disabled one is
  visibly dimmed by the window's own `:disabled` style because Avalonia does not dim a bare `TextBlock`.
- **The golden-ratio opening size**, non-modal, resizable, opened per document, with a minimum that can
  actually show the specification column. `MatchWindowPlacement` is the precedent.
- **Compact sliders** (the negative-margin trick), so a slider in a row does not make the row tall.
- **Plots are `PlotControl`s** in rectangular mode, fed from a `DataSet` — **never a bespoke chart.**
- **A docked list panel, not a modal sheet**, for the results lists, so a user can click through rows and
  watch the map and the curve change.

### `R-rail7-2` — the owner's two rules for this window (§11.2)

- **Centred text in comboboxes and on buttons.** `HorizontalContentAlignment="Center"` on every push
  button and on the combobox's selection *and* its items. The one exception is already an owner decision
  in the Match Designer and it stays: **a click-to-sort column header stays left-aligned with the column
  under it**, because any centring there is a misalignment the user can see.
- **Clean and simple by default.** The reference-plane options, the via limit basis, the meshing density
  and the temperature note live behind **`Settings`**, not on the face of the window.

---

## 2. `R-rail7-3` — the layout, and the four things in it that are the design

§11.3's sketch is the specification. Three columns under one title bar, one status strip, one bottom bar.

**1. The centre is the board, and it is the layout editor's own canvas.** Not a schematic, not a sketch,
not a picture of a layout — `LayoutCanvas` itself. Brief 8 owns it entirely; brief 7 hosts it and wires
the `[copper│drop│|Z|│class]` tab strip, which **switches the overlay and never the geometry.**

**2. The status strip carries the model.** *Fast model · 4.1 ms · 20 °C · reference as imported · 3 parts
with no bias curve.* Always on screen, so nobody reads a fast answer as an accurate one, and the elapsed
time makes the cost of `Accuracy` obvious **before** it is pressed.

**3. `Accuracy` is a button on the bottom bar, next to Run** — the one control that changes what the
numbers mean, in the place the user looks when the design is settled. **Never entered automatically, and
never left silently** (§2.9).

**4. Sources and loads are lists, not fields**, each with add and remove, each row a **refdes and a pin**
rather than a coordinate, and the **rail selector above them** says which rail the window is currently
showing. **A load row with no current reads *observe***, because that is what it is.

### `R-rail7-4` — the status strip states numbers, and so do the refusals

§11.1: *"refusals that appear there with numbers in them"* — *"the placement file does not state its
coordinate origin; pass `--origin` or set it here"* is a sentence this UI must be able to say plainly,
**with the affected input turning red.**

Every refusal in briefs 2-6 surfaces here in that shape: the placement origin, the Excellon format, a rail
with no reference layer, a cycle in the rail order, Fast above its shunt-band threshold, an unresolved via
span. Each names the control that answers it and turns that control red.

---

## 3. `R-rail7-5` — the Fast edit loop is the default, and it is what makes this window feel different

§2.3 step 6, and §2.9:

> In **Fast** mode — the default — the result follows the edit: change a value, swap a part number, delete
> a part, change the source ESR or the load current, **and the numbers move as you type.**

So the view model re-extracts and re-solves on every committed edit, on a background dispatch with
coalescing, and the status strip's elapsed time is what tells the user it happened. **`Accuracy` is
explicit and stays explicit**: pressing it runs the mesh, and the window then shows **both** results (brief
4 `R-rail4-5` — the fast curve stays on the plot beside the accurate one).

Two guards, both of which are the kind of thing that only appears under real use:

- A re-solve in flight when another edit arrives is **cancelled**, not queued. `RunControl` is the
  mechanism, as `em` and `render` already use it.
- **The model kind on screen always matches the numbers on screen.** A strip reading *Fast* over an
  Accuracy result, for even one frame, is the exact failure `R-rail4-2` exists to prevent. Set them
  together or not at all.

---

## 4. `R-rail7-6` — the import lands in a cell, and that checkbox is ON by default

§2.3 step 1, an owner decision in rev 3:

> the artwork imports, and **by default it lands in the open workspace as an ordinary circuitRF cell with
> a layout view** — a checkbox on the import dialog, on by default.

The consequences are all the good ones and are worth listing in the dialog's own comment, because someone
will eventually wonder why the default is not the cheap path: the artwork is saved with the design rather
than re-imported every session, it opens in the layout editor, DRC runs on it, `circuitrf render` draws it
headlessly, revision control keeps it, and railRF's document holds a **reference** to that cell rather than
a private copy of the geometry.

Unchecking it gives the throwaway behaviour for a quick look. **With no workspace open, railRF offers to
create one** (`WorkspaceCreate.Create` — the same function the GUI's own New Workspace command calls)
rather than silently falling back to the throwaway path.

### `R-rail7-7` — the dialog asks exactly the two things that must not be guessed

**Nothing pre-selected on either**, and both are refusals headless (brief 2, and `convert`'s existing
Excellon rule):

- **The placement origin** — symbol origin, body centre, or pin 1. Q-14: *there is no house convention to
  learn*, and *a default here is the guess the refusal exists to prevent.*
- **The Excellon coordinate format**, where the drill file does not state it. Already `convert`'s rule;
  reuse its wording rather than inventing a second sentence for the same refusal.

Everything else the dialog needs takes **the same default `convert` takes** — the layer mapping above all.
Two surfaces asking the same question with two different defaults is a defect that only shows up when
someone compares their answers.

---

## 5. `R-rail7-8` — step 2, and the thing that has already caught real problems

§2.3:

> railRF highlights everything galvanically connected to your pick — through vias, across layers — so you
> immediately see whether the rail is one region or three islands joined by a 20 mil neck. *That alone has
> caught real problems.*

Brief 3's `PdnRailRegions` computes it; brief 8 draws it. Brief 7 owns the **picking**: from a list where a
board file or a board netlist names the nets, by clicking the pour where neither does.

Then **confirm the reference layer, which railRF proposes and never assumes** (§2.2, Q-8). The window shows
the proposal *and why*, and the user confirms. A pre-selected combo that a user tabs past is not a
confirmation, so the Run button is disabled until the reference has been affirmatively set — which is the
one place this window deliberately costs the user a click.

**Where a regulator is recognised from the BOM, railRF offers its output net as the next rail and
pre-fills the row that makes it a load on this one** (§2.3). Offered, not created: the rail chain is a
modelling statement and it is the user's.

---

## 6. `R-rail7-9` — the parts table lists what it could not resolve AS unresolved

§2.3 step 3: refdes, part number, value, model source, derated value, position, computed mounting
inductance. **Anything railRF could not resolve is listed as unresolved rather than defaulted.**

Three columns carry provenance rather than data, and all three exist because §9 says their absence is
silent:

- **model source** — library row / attached Touchstone / R-L-C / subcircuit, and which won (brief 2
  `R-rail2-11`).
- **derated** beside **marked**, and which was used (Q-12). The count with no bias curve is on the status
  strip.
- **ESR** — measured / stated / class default, with class-default rows marked **indicative** (Q-15).

§9: *"the parts table's count of how many parts are modelled from a file is a headline number and not a
detail."* Put it on the status strip beside the bias-curve count.

---

## 7. `R-rail7-10` — where it opens from, and the Properties panel

§11.4. From the **Tools** menu for a new document, and by double-clicking a `.crail` in the project tree
(brief 1 `R-rail1-11` wired the routing). A selected railRF document's Properties panel shows a compact
summary — **the rail, its reference, its source count and load count, worst drop and worst margin** — and
an **Open railRF…** button, following the Match and wBond panels exactly.

Two traps already paid for in those panels and both apply here:

- **The project-tree dirty mark is PUSHED onto a node that a window-activate rescan then rebuilds.** Set it
  through the same path those panels do; a second path loses it on the next activate.
- **A combo whose `ItemsSource` binding attaches after the code-behind's handler runs drops a selection set
  first** — the wBond round-6 blank-Group-combo bug. Set `ItemsSource` before `SelectedItem`, always.

---

## 8. Tests — `tests/Ui.Tests/RailRf/RailWindowTests.cs`

These follow the Match Designer's own test shape: view-model driven, with the window constructed only
where chrome is the subject.

- **`R-rail7-2`**: a XAML scan asserts `HorizontalContentAlignment="Center"` on every `Button` and
  `ComboBox` in the window, **and its absence on the sortable column headers.** Both halves — the
  exception is an owner decision and a test that only checks the rule would delete it.
- **`R-rail7-3`**: the source and load lists add and remove; a load row with no current renders *observe*;
  the rail selector switches which rail the whole window shows.
- **`R-rail7-5`**: an edit re-solves; a second edit mid-solve **cancels** the first; the status strip's
  model kind and the results' model kind are equal after every transition, asserted on the transition
  rather than at rest.
- **`R-rail7-6`**: the import checkbox defaults on; with no workspace open the create-workspace offer
  appears; unchecking produces a document whose `ArtworkCellRef` is a throwaway.
- **`R-rail7-7`**: the origin combo has **no selection** on open, and Run is refused with the sentence
  naming the flag until one is chosen.
- **`R-rail7-8`**: Run is disabled until the reference layer is affirmatively set. The proposal is shown
  with its reason.
- **`R-rail7-9`**: an unresolved part renders as unresolved, and no numeric column is populated for it.
- **`R-rail7-4`**: each of the six refusals renders in the status strip with its number in it and turns
  its own control red.

---

## 9. Scope

- **No canvas, no overlay, no navigation.** Brief 8. This brief hosts `LayoutCanvas` and wires the tab
  strip; every gesture and every pixel of overlay is brief 8's.
- **No clipboard.** Brief 9.
- **No extraction, no solve.** Briefs 3-6. This window calls them.
- **No second plotting path.** `PlotControl` in rectangular mode, fed from a `DataSet`. Brief 12 builds
  the `DataSet`.
- **No settings the note did not ask for.** §11.2's list behind `Settings` is the whole list.

**On completion:** record findings in `src/Ui/RESOLVED.md`. Never in a CLAUDE.md.

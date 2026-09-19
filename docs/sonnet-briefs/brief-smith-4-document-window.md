# Brief 4 — the document, its window, and every editable value in it

**Series:** [Smith Chart](brief-smith-0-overview.md) · **Tag:** `R-smith4-n` · **Phase:** P1
**Area:** `src/Ui/Smith/`, `src/Ui/Views/Smith/` · **Depends on:** 1 · **Blocks:** 5, 6
**Design note:** [`smith-chart.md`](../design/smith-chart.md) §5.1, §5.2, §5.3, §5.8, §5.9, §3.1

---

## 0. What this brief delivers

The shell around the two panes: a docked document, the window's regions and chrome, the generator panel,
the Tools menu entry and the On Launch row. **The chart pane and the network pane are stubs here** —
briefs 5 and 6 fill them — so this brief's gate is about the document behaving like a document.

### `R-smith4-1` — it is a document, not an application

Owner instruction. `SmithChartDocument : Document, IActivatableDocument, IFileBackedDocument,
IEditHistoryDocument`, modelled on `src/Ui/DataDisplay/DataDisplayDocument.cs` — **read that file before
writing this one.** harmonicaRF, wBond and railRF each open a window of their own and each has a reason;
this has neither, and there is **no standalone `smithRF` binary and none is proposed.**

What that inheritance buys, none of which is re-implemented:

- a document tab, a `•` dirty mark, Save / Save All / close-time prompting (`IFileBackedDocument`);
- **Ctrl/Cmd+Z routed by the shell** (`IEditHistoryDocument`). That interface exists because a *floating*
  Data Display once undid an edit in an unfocused schematic — on macOS the menu bar is app-global, the
  same `NativeMenu` is attached to every torn-off window, and the shell's Undo command fires from
  whichever window is key. A document that cannot be reached that way has the same bug;
- tear-off and floating, the Window Layout, and restoration of open documents on reopen;
- a project-tree node with double-click to open, and the single-instance rule every document type has —
  a second open **focuses the first**, never opens a second view of one file;
- **needing no workspace.** A scratch `.csmith` opens with nothing else loaded, on harmonicaRF's terms,
  and Save As gives it a home.

`R-smith4-2` — complete `WorkspaceViewModel.OpenSmithPath`, the stub brief 1 left. It reads through
`SmithDesignIo`, opens the document, and reports a read refusal in the Messages panel rather than throwing.

---

## 1. `R-smith4-3` — layout

§5.2 of the note has the sketch. Three regions in one document, with draggable splitters whose positions
persist in the document's `View` block:

- **left column — the generator**, narrow: the frequency table, Import `.s1p`, Conjugate, chart Z₀, design
  frequency, and (brief 9) the sweep and Q rows, and (brief 8) the overlays list;
- **centre — the chart**, taking the large majority of the area. **The chart is the tool.** Stub here;
- **bottom — the network strip**, tall enough for one row of symbols plus the selected element's sliders.
  Stub here;
- **a status strip across the foot**, stating numbers.

---

## 2. `R-smith4-4` — chrome, borrowed and not invented

`docs/design/match.md` §9 and `railrf.md` §11.1-§11.2 settled these over roughly ten rounds of owner
review. Take them; do not re-decide them. `src/Ui/Views/Match/MatchDesignerWindow.axaml` is the reference.

- **Two-tier chrome**: `Border.pane` for a region, `Border.card` for a group inside it. One tile border,
  6 px corners.
- **Sentence-case headings**, never shouted.
- **Label left, value right**, with the numbers forming one right-aligned column whether the row is
  settable or read-only. A disabled value is visibly dimmed.
- **Centred text in comboboxes and on buttons** (`HorizontalContentAlignment="Center"`). The one standing
  exception is a click-to-sort column header, which stays left-aligned with its column.
- **Compact sliders** (the negative-margin trick) so a slider in a row does not make the row tall.
- **A status strip that states numbers**, and refusals that appear there *with numbers in them*, with the
  offending input turning red.
- **Advanced settings behind a Settings button**, not on the face of the window.

### `R-smith4-5` — EVERY editable value is an `InlineEditText`

Owner instruction, and the word is *every*. Not "most", and not "the ones in the specification column".

> the generator table's f, R and X cells · the chart Z₀ · the design frequency · the sweep's start, stop
> and point count · the constant-Q value · every parameter value beside every slider · a TLIN's F_ref ·
> each element's instance name · each slider's range endpoints · an overlay row's label

The reason is the owner's: the control already carries the Match Designer's specification pane, railRF's
source/load/aggressor rows and harmonicaRF's readout strip, so **the user has already learned it and we
have already debugged it.** What comes with it, free and identically:

- **The three-key contract, the same everywhere in this application** — Return commits and sets
  `e.Handled` (or the hosting window's default button swallows it), LostFocus commits, Escape reverts.
  The control's own doc comment says why it is worth naming: *getting any one of the three wrong is an
  edit the user loses by clicking away*, and it is reported months later as "sometimes it doesn't take".
- **Double-click opens**, never single-click — a single click opens an editor the user only meant to click
  past.
- **The unit is part of the text and is not selected.** `Text` carries the whole `"1.5 nH"`; opening
  pre-selects only `"1.5"`.
- **`HorizontalContentAlignment` is honoured by the resting text and the open box together**, which is
  what makes the right-aligned value column work.
- **`Watermark`** for an empty optional field, dimmed and never committed.

**`R-smith4-6` — hosting.** The control has two, and its own remarks name them: the Designer's rows **swap
the box in place** because a row is a fixed grid cell; the readout strip **floats** its box in a `Canvas`
overlay because its columns are width-shared. The generator table is the only grid of cells here and its
three columns are **fixed-width by construction**, so it takes the in-place hosting. **Nothing in this
series needs the floating overlay**, which is the more delicate of the two. If you find yourself reaching
for it, the column widths are the thing to fix.

**Do not write a fourth inline editor anywhere in this window.** That is the trap this requirement exists
to close.

---

## 3. `R-smith4-7` — the generator panel

- The table is rows of **f / R / X**, all three `InlineEditText`, with `[+]` and `[−]`. Rows are kept
  sorted by frequency; a duplicate frequency is refused in the status strip naming the frequency (brief 1
  `R-smith1-3` already has the rule — call it, do not restate it).
- **Import `.s1p`…** calls brief 1's `SmithGeneratorImport`, **replaces** the table, and records
  `SourcePath`. A **Re-import** button repeats the read. The path is shown as provenance; nothing resolves
  it at load, so a moved file cannot stop a document opening.
- **Conjugate** is a single button, one click, that negates every row's X. It is **one undo entry** and it
  is not a persistent flag.
- **Chart Z₀** and **design frequency** sit under the table. A design frequency outside the table's span
  turns red and the strip names the span — brief 2's refusal, surfaced, not re-derived.

### `R-smith4-8` — the status strip

For the design frequency: load impedance in `R + jX`, Γ in polar and rectangular, VSWR, and the
conjugate-match mismatch in dB. **Every number is the one the evaluator produced**, formatted by
`MatchValueFormat`, never re-derived for display.

---

## 4. `R-smith4-9` — the menus, and the On Launch row

**Tools ▸ Smith Chart** creates a new scratch document, beside *harmonicaRF*, *Match Designer* and
*railRF*. **Both surfaces** — the macOS `NativeMenu` in `WorkspaceWindow.axaml` and the in-window Tools
menu below it — are hand-maintained and must be edited together; the file already says so.

The document contributes to the shell's menus rather than owning a menu bar:

- **File** — New Smith Chart, Open, Save, Save As, Close: the shell's own.
- **Edit** — Undo, Redo, Cut/Copy/Paste routed to whichever pane has focus (brief 7).
- **View** — Zoom to Fit (chart), Zoom to Fit (network), and show/hide for grippers, targets, Q arcs and
  labels.
- **Insert** — the element vocabulary, mirroring brief 6's Add menu, so every element is reachable from
  the keyboard.

### `R-smith4-10` — the On Launch row, and the ordinal contract

Owner instruction: `Settings ▸ General ▸ On Launch Action` gains a **Smith Chart** row that opens a new
scratch `.csmith` at startup. This is possible *because* of `R-smith4-1` — every existing member of that
list is a document, and railRF and wBond are absent from it for that reason.

**The trap, already documented beside the code:** `AppPreferences.LaunchAction` is serialized as an
**ordinal**, and `SettingsView.LoadGeneralPrefs` fills `LaunchActionCombo.ItemsSource` from a bare
`string[]` whose **index is cast directly to the enum**. So:

- `NewSmithChart` is **appended** to `LaunchAction`, never inserted;
- `"Smith Chart"` is **appended** to that array, never reordered;
- both are edited in the **same change**, because reordering either silently changes what every
  already-saved `preferences.json` means;
- `ExecuteLaunchActionAsync` and `ApplyOnLaunchActionForNewWorkspace` each gain the case.

---

## 5. The gate

`tests/Ui.Tests/Smith/SmithWindowTests.cs` — one test per claim:

1. **The document round-trips through the shell**: open a `.csmith`, edit, dirty mark appears, Save
   writes, close prompts, reopen matches.
2. **A second open of the same path focuses the first** rather than opening a second document.
3. **A scratch document opens with no workspace loaded**, and Save As gives it a path.
4. **Undo routing**: the shell's Undo reaches this document through `IEditHistoryDocument` when it is the
   key window — the regression is the floating-Data-Display one named in `R-smith4-1`.
5. **The `LaunchAction` ordinal contract** (`R-smith4-10`): the combobox array's length and order asserted
   against the enum. This is the test that stops the next person getting it wrong quietly.
6. **Both Tools menus carry the entry** — a source scan over `WorkspaceWindow.axaml` asserting the native
   and in-window surfaces agree, following the comment already in that file.
7. **Every editable value is an `InlineEditText`** (`R-smith4-5`): a source scan over
   `src/Ui/Views/Smith/` asserting no bare `TextBox` carries a two-way `Binding` to a design value.
   Comment-stripped, on `AuthoringCliVerbTests`' own precedent.
8. **Import, Re-import and Conjugate**: the table is replaced, the path recorded, and Conjugate is **one**
   undo entry that a single Undo unwinds completely.

---

## 6. What this brief must NOT do

- **No chart, no trajectories, no grippers.** Brief 5. The centre region is a placeholder.
- **No network drawing, no sliders, no mirror button.** Brief 6.
- **No clipboard.** Brief 7.
- **No overlays list content, no sweep row, no Q row** beyond the panel space for them. Briefs 8 and 9.
- **No second window class.** If a `Window` subclass appears in `src/Ui/Views/Smith/`, `R-smith4-1` has
  been missed.

---

## 7. On completion

Findings to `src/Ui/RESOLVED.md`. **Never a `CLAUDE.md`.**

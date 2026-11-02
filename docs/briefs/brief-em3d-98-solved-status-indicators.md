# Brief 98 — "Solved": show which 3D-view setups have a current result

**Tag:** `R-em3d98-n` · **Series:** standalone (follows 87's inputs manifest and 93's Model flag).
**Area:** `src/Design/ThreeD/C3dRunDocument.cs`, `C3dPersistence.cs` (`SerializeForRun`), `src/Design/Layout/Em/EmRunService.cs`
(`RunThreeDView`), `src/Design/Em3d/Em3dRunService.cs`, `src/Design/Thermal/ThermalRunService*.cs`, a new
`src/Design/ThreeD/C3dSolveStatus.cs`, `src/Ui/ThreeD/C3dEditorViewModel.Simulate.cs` (+ `C3dSetupItem`),
`src/Ui/ThreeD/C3dEditorDocument.cs`, `src/Ui/ViewModels/ProjectTree/ProjectTreeItemViewModel.cs`,
`src/Ui/ViewModels/WorkspaceViewModel.ThreeD.cs` (`RunC3dSetupAsync`), `src/Ui/ViewModels/Dock/CircuitRfDockFactory.cs`,
`src/Ui/Styles/CircuitRfResources.axaml` (the document tab template), `src/Cli/Explain*.cs`, `src/Cli/Find.cs`,
`docs/user/src/reference/em-3d.md`, `thermal.md`, `docs/design/cli.md`
**Depends on:** 87 (inputs manifest), 93 (Model flag) · **Blocks:** —

## Why

A 3D solve takes minutes to hours. Today a user who reopens a `.c3d` cannot tell whether a setup's result still matches
the model without opening its fields and looking for the stale banner. So they either re-run something that was already
current or trust something that is not. The owner wants a persistent, visible "solved" mark: shown when a current result
exists, still there after the document or workspace is closed and reopened, and gone as soon as the user edits the model.

**The hard part already exists.** Brief 87 keeps, beside every successful run, the document it solved (`document.c3d`,
through `SerializeForRun`) and the SHA-256 of every file it read (`inputs.json`). `C3dRunDocument.Check(runDir, document,
path)` answers "is this result current?", including unsaved edits and edits to placed layouts, nested views, the
technology and material libraries. This brief **surfaces that answer**. It adds no second staleness rule anywhere: every
surface below reads one function (§3).

Because the check compares content and not an edit counter, **undoing back to the solved model makes the result current
again.** That is intended. Do not "fix" it.

## 1. `R-em3d98-1` — display state never makes a result stale

`C3dPersistence.SerializeForRun` (`C3dPersistence.cs:119`) leaves out `FieldPlots` and every `Transparency`, but keeps
`Hidden`. Toggling an object's visibility therefore marks every result stale today. The owner has settled this:
**visibility never changes a result's status; `Model` (brief 93) does.**

- **Audit, don't just patch `Hidden`.** Enumerate every property `C3dDocument` serializes, on every element type (objects at
  every depth, instances, ports, probes, heat sources, groups, field plots, anything else), and classify each as **model**
  (changes what a solver sees) or **display** (doesn't). `SerializeForRun` strips every display property. Record the
  classification as a comment beside `SerializeForRun`, so the next property added has to be put in one of the two lists.
  Expected display set: `Hidden`, `Transparency`, `FieldPlots`, and anything the audit finds that matches (view or camera
  state, tree expansion, colours if they are per-object display only). `Model` is model state.
- **Normalise the stored side, or every existing result goes stale on upgrade.** Records written before this brief contain
  `Hidden`. In `C3dRunDocument.Check`, when the stored text differs from `SerializeForRun(current)`, load the stored text
  as a document, put it through `SerializeForRun`, and compare again. That costs nothing for the common equal case and makes
  old records compare on the new terms. Brief 87's "re-hash only when time or size moved" cache stays as it is.
- The stale banner (`FieldsStaleText`) and `circuitrf render --field`'s stale note use the same `Check`, so both stop
  reporting visibility edits with no further change.

## 2. `R-em3d98-2` — a run records how it ended, per solver

Today the record is kept **only on success** (`EmRunService.cs:566`), which leaves two errors:

- **False "current" after a cancelled re-run.** A cancelled run leaves the previous record in place. If the model is
  unchanged, `Check` says "current" while the run directory may hold half-overwritten results. This is the one error this
  brief must never produce.
- **Both setups lose a complete leg.** With Palace complete and openEMS cancelled, the run returns `Cancelled`
  (`CancelledKeeping`) and neither solver's directory gets a record, though Palace's result is whole.

Add **`status.json`** to each run directory (one per solver leg, and the thermal run's own directory), written by the run
services in `src/Design`, so the GUI's Simulate and `circuitrf em` keep the same record through the one door, as brief 87
requires:

| Field | Meaning |
|---|---|
| `State` | `running` · `complete` · `cancelled` · `notConverged` · `failed` |
| `Started`, `Finished` | UTC; `Finished` null while running |
| `Detail` | one sentence for the tooltip, e.g. `openEMS stopped at −28 dB against −40 dB: not converged` |
| `Pid` | the process that wrote `running` (so a live run elsewhere is told apart from a crashed one) |

- **`running` is written before the solver touches the directory**, atomically (`AtomicFile`), and the final state on every
  exit path, including exceptions and `OperationCanceledException`. A directory left at `running` whose `Pid` is not alive
  is read as **interrupted**, which counts as partial (§3).
- **`notConverged` comes from each solver's existing signal only**: openEMS's `Converged` per port
  (`Em3dRunService.cs:1096`), and whatever Palace and the thermal solver already report. Find and reuse them; invent no
  new convergence criterion. A not-converged run still keeps its inputs record, because its result exists and is the
  model's.
- **Keep each leg's inputs record when that leg completed**, not only when the whole run returns `Ok`. A Both run with
  Palace complete and openEMS cancelled keeps Palace's record and marks openEMS `cancelled`.
- **No `status.json` (a run from before this brief):** if `document.c3d` is present, the run is `complete` (the record
  was only ever kept on success), with no duration.

## 3. `R-em3d98-3` — one function answers it

New `src/Design/ThreeD/C3dSolveStatus.cs`:

```
C3dSolveStatus.Of(C3dDocument document, string documentPath, string resultsRoot, CancellationToken ct)
    → IReadOnlyList<SetupSolveStatus>   // one per (setup, solver leg)
SetupSolveStatus(string Setup, SolverKind Solver, SolveState State, bool Partial,
                 string? StaleWhat, DateTime? Solved, TimeSpan? Took, string? Detail)
SolverKind  { Fem, Fdtd, Thermal }     // Palace, openEMS, thermal
SolveState  { NotRun, Current, OutOfDate }
```

- Run directories come from `Em3dRunService.RunDirectory` / `ThermalRunService.RunDirectory` on `C3dSetups.ForRun(s, path)`,
  exactly as `C3dEditorViewModel.RunDirectoriesOf` does now. Move that enumeration into `src/Design` and have the editor
  call it, so there is one list of "where a setup's results live".
- `State` comes from `C3dRunDocument.Check` (§1). No record and no result → `NotRun`.
- `Partial` is `cancelled`, `notConverged`, `failed` with a result present, or interrupted (§2). A partial result can
  still be `Current` or `OutOfDate`; the two are independent.
- **An open document is checked against its in-memory model** (unsaved edits count, as the banner does now). **A closed
  one is checked against its file on disk.**
- It runs off the UI thread and takes a `CancellationToken`. Nothing in it touches Avalonia.
- The active setup also matters (§4), and it is part of the document from now on:
  - `.c3d` gains `"ActiveSetup": "<name>"`, written **only when it is not the first setup** (so no existing file changes).
  - **Display state, so `SerializeForRun` strips it** (§1's audit list). Choosing a setup must never make a result stale.
  - Choosing a setup marks the document dirty, as any saved setting does. It is not an undo entry.
  - On open, a missing or unknown name falls back to the first setup, as `ActiveSetupName` does today
    (`C3dEditorViewModel.Simulate.cs:223`).

## 4. `R-em3d98-4` — the glyphs

One glyph per solver kind: **◆ FEM (Palace)**, **▲ FDTD (openEMS)**, **● Thermal**. Draw them as **vector paths in an
Avalonia control** (`SolveBadges`), never as Unicode characters: font fallback for ◆▲● differs between Windows, macOS
and Linux in size, baseline and occasionally emoji presentation. The control takes the statuses and the active setup.
Each solver kind shows **at most one glyph**, chosen by this table, top row first:

| Situation, for this solver kind | Glyph |
|---|---|
| The active setup uses it and is `Current` | solid, filled |
| The active setup uses it and is `OutOfDate` | solid, hollow (the active setup wins even when another setup is current) |
| Otherwise, any other setup of this kind is `Current` | faded, filled |
| Otherwise | none |
| The glyph shown comes from a `Partial` result | a small `*` after it (ASCII, drawn as text) |

A Both setup contributes to two kinds. There is deliberately no "faded hollow": an out-of-date result for a setup the user
isn't using is noise here, and the setup list (§5) already says it in words.

- **Theme colours, not literals:** `SolveBadgeBrush` and `SolveBadgeFadedBrush` in both themes. The faded brush must stay
  readable on the dark theme's tab and tree background. A grey chosen on white tends to disappear there; check both.
- **Shape and fill carry the meaning; colour carries none**, so the glyphs read the same to a colour-blind user.
- **Tooltip on each glyph**, in words: `FEM (Palace), setup 'EM1': solved 14:32, took 18 min`, or `… out of date: 'Board.clay'
  has changed since`, plus `Detail` when partial. For a faded glyph, name the setup it comes from.

## 5. `R-em3d98-5` — where the glyphs and words appear

1. **The setup list** (`C3dSetupItem` and the setup analysis cards). Each setup row gets a status line in words:
   `Solved 14:32 (18 min)`, `Solved 14:32 (18 min), cancelled at 12 of 40 points`, `Out of date: 'Board.clay' has changed`,
   `Not run`. A Both setup shows a line per solver. This is the primary surface, because it is where the user decides
   whether to run.
2. **The document tab.** `SolveBadges` after the name, in the tab template (`CircuitRfResources.axaml`,
   `DocumentTabStripItem`). **`C3dEditorDocument.Title` stays exactly as it is.** The Window menu, the save-before-close
   prompts and `ShellHeader` all read `Title` and trim the `•` unsaved mark from it, so adding text there would leak into
   every one of them. Bind the badges to a separate property. The `•` unsaved mark stays at the front and the solver
   glyphs go after the name, drawn noticeably bigger than the `•`, so the two circles aren't confused. **If the Dock
   tab template can't host a control without restyling Dock's tabs wholesale, stop and report** rather than doing that.
3. **The workspace tree** (`ProjectTreeItemViewModel`), on `.c3d` rows. Same control, same table, statuses from the
   on-disk check (§3).
4. **A floating window's title**, only where the window shows a `.c3d` as its active document (`CircuitRfDockFactory`
   sets it from `active.Title`; find where it follows a change of active document). The title bar is drawn by the
   operating system, so this one is **text**: `Connector.c3d — solved (FEM, thermal)`, listing the kinds whose glyph
   would be **solid filled**, with `partial` named where it applies: `— solved (FEM, partial)`. Nothing is added when no
   kind is solid filled. **The main window's title is unchanged**: it names the workspace (`WorkspaceViewModel.cs:399`),
   which holds many documents, so a suffix there could not say which document it meant.

## 6. `R-em3d98-6` — when it is checked

The owner wants the document and the workspace on screen as fast as possible. **No check ever delays an open.**

- **On workspace open:** after the tree is shown, one background pass over the tree's `.c3d` rows, at low priority, filling
  glyphs in as each finishes. No spinner, no placeholder glyph.
- **On document open:** after the first frame, in the background.
- **On edit:** debounced (~500 ms of quiet) and in the background. A large document's `SerializeForRun` must not run on
  every keystroke or drag tick. A drag in progress never triggers one.
- **When any run finishes, for any setup.** Today `RunFinished()` refreshes only the active setup's banner. A setup run
  from its own editor's Simulate button (`C3dEditorViewModel.Simulate.cs:337`), which may not be the active one, must
  refresh its own status and the badges too. Running a non-active setup this way stays supported.
- **When the app saves a file in the workspace** (a layout, a technology, a material library): re-check the open `.c3d`
  documents and the tree. The hash cache means only changed files are re-hashed.
- Cache results per `(document path, its file time and size)` for the tree, for the session.

## 7. `R-em3d98-7` — confirm before re-running a current result

In `RunC3dSetupAsync`, before the run starts: if the setup being run (active or not) has every leg it runs `Current` and
**not** `Partial`, ask in one line:

> Palace result for 'EM1' is current (solved 14:32, took 18 min). Run again?  **[Run again]** **[Cancel]**

- A Both setup names both legs.
- **A partial or out-of-date result runs with no question**, because there's no complete current result to lose.
- If the status isn't known yet (the background check hasn't finished), compute it for this one setup synchronously
  first. It is one setup's check, and the confirmation must not be skipped just because the badges were slow.
- **`circuitrf em` never asks.** It prints one stderr line, `note: the result for 'EM1' was already current; running
  again`, and runs.

## 8. `R-em3d98-8` — headless

- `explain x.c3d` gains a **Solved** section: one line per (setup, solver) with state, partial, solved time, duration and
  what changed. It appears in `--json` as well.
- `find` gains the same per-`.c3d` summary on its rows (state per setup only, no detail).
- Both call `C3dSolveStatus.Of`; neither holds logic of its own, per the CLI rule (`docs/design/cli.md`).

## 9. Gate

No solve in any gate. Build run directories by hand (`document.c3d`, `inputs.json`, `status.json`) in a temp workspace.

1. `SerializeForRun`: toggling `Hidden` on an object, an instance and a port, and changing `ActiveSetup`, leaves its output
   unchanged; toggling `Model` changes it. One test, one row per property in the audit's display list.
2. Upgrade: a run record whose stored `document.c3d` contains `"Hidden": true` checks as `Current` against the same model.
3. `C3dSolveStatus.Of` on a document with setups EM1 (Palace, complete), EM2 (Both: Palace complete, openEMS cancelled) and
   T1 (thermal, no run) returns `Current`; `Current` + `Current` with `Partial` on openEMS; and `NotRun`.
4. An edit to a placed `.clay` makes EM1 `OutOfDate` with `StaleWhat` naming it; restoring its bytes makes it `Current`
   again.
5. `status.json` left at `running` with a dead `Pid` reads as `Partial` (interrupted); with this process's pid, as running.
6. No `status.json` but a `document.c3d`: `complete`, `Took` null.
7. Run-service wiring (no solver): a run cancelled through the leg's cancellation path leaves `status.json` at `cancelled`
   and does **not** leave a previous record able to read as `Current`. Use the existing fake-engine seams if there are any,
   and stop and report if driving the cancel path needs a real solve.
8. The badge table: a unit test over the pure function that picks each kind's glyph (solid/faded/hollow/none, `*`), one row
   per table line, including "active out of date while another setup is current".
9. `Title` unchanged: a `.c3d` with a current result still has `Title == "Connector.c3d"` (and `"• Connector.c3d"` when
   dirty).
10. `explain x.c3d --json` as a process carries the Solved section for the gate-3 document.
11. The `em3d-solve-badges` catalog row builds and captures a non-empty image at its stated size. Look at the PNG and
    report what was seen, in each theme captured.

Run only these test classes (`--filter`), never the full suite. The GUI can't be launched from the build shell: say so in
the report, and say the pixels were not seen.

## 10. Decisions

**Settled (owner, 2026-10-02):** a visibility toggle never changes a result's status, while `Model` does · checks run in
the background and never delay an open · bold glyph for the active setup's current result, faded for any other setup of
that kind that is current · a `*` marks a cancelled, interrupted or not-converged result · one-line confirmation before
re-running a current result · the active setup is saved in the `.c3d` · glyphs ◆ FEM, ▲ FDTD, ● thermal, after the name ·
a non-active setup may still be run from its own editor.

**Open, built as recommended unless the owner says otherwise:** D1, `explain`/`find` carry the summary but `check` does not
(it reports soundness, not results) · D2, the floating window title lists only solid-filled kinds and never says "out of
date".

## Docs — `R-em3d98-9` (part of the brief, not an afterthought)

The owner asked for the user docs to **show the glyphs and explain them**. A user who meets a faded ◆* in a tab has to be
able to look up what it means.

- **A new section in `docs/user/src/reference/em-3d.md`, "Is this solved?"**, which `thermal.md` links to rather than
  repeating. It covers:
  - what "solved" means: a result exists and nothing the run read has changed since, including placed layouts, nested
    views, the technology and material libraries;
  - that it survives closing and reopening the document and the workspace;
  - what makes a result out of date (any model edit, including `Model`) and what never does (visibility, transparency,
    field plots, choosing the active setup);
  - that undoing back to the solved model restores "solved";
  - each place it appears (setup list, tab, workspace tree, floating window title) and the confirmation before re-running;
  - the same answer headlessly, from `explain` and `find`.
- **A legend figure drawn by the real control**, never a hand-drawn picture of it: a new `FigureCatalog` row
  (`src/Ui/Diagnostics/FigureCatalog.cs`), e.g. `em3d-solve-badges`, cited as `{{ui: em3d-solve-badges}}`. It is
  `SolveBadges` laid out as a legend grid: one column per kind (◆ FEM, ▲ FDTD, ● thermal), one row per state (solid
  filled, solid hollow, faded filled, and each with `*`), each cell labelled in words. Build it from fixture statuses (no
  run directories needed: the control takes statuses). Capture it in both the light and dark themes if the catalog
  supports a theme per row, since the faded brush is the thing most likely to fail in one of them.
- **Beside the figure, the §4 table in words**, so the page still explains it to a reader who can't see the figure (screen
  reader, text-only build): which glyph is which solver, filled vs hollow vs faded, and the `*`.
- **A second figure of a tab and a setup row in context**, if the catalog can capture the 3D editor with fixture statuses
  (`DocEm3dFixtures` builds 3D documents already): `em3d-solved-tab` showing `Connector.c3d ◆ ●` and the setup list's
  status lines. If capturing the tab strip needs a full shell the catalog can't build, leave this figure out and say so in
  the report. The legend figure is the required one.
- `docs/design/cli.md`: the `explain`/`find` additions, with an example of the Solved section's text and JSON.
- **Edit doc sources and add the catalog rows only; don't run DocGen.** The owner regenerates `docs/user` at the end of a
  series. Do verify that the new rows build and capture: render just those figures through the catalog's own path in a
  test (gate 11) and look at the PNG, rather than running DocGen.

## On completion

`src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md`, never CLAUDE.md. Do not commit unless the owner asks.

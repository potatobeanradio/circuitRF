# Sonnet Brief — VAR component (UI): multi-line variable editor flyout in the schematic editor

**Depends on** `brief-var-component-core.md` (SymbolKind.Var, registry entry, node-less symbol, extractor
routing). This brief adds the **authoring UX**: place a VAR and edit its variables, with a **mini multi-line
text flyout** (paste many `name = expression` lines at once) **and** a fall-back to the traditional one-at-a-time
parameter-row editor.

## Required reads first
- The existing **component parameter editor** UI (how double-clicking a component opens its parameter editor /
  property flyout, and how rows map to `EditableComponent.Parameters`). Find it under
  `src/Ui/Views/...`/`src/Ui/ViewModels/...` (the schematic parameter-edit flyout) and follow its idiom.
- The §2.8 inspector idiom (`AnalysisEditorDialog.axaml` / `SpBodyView.axaml`) for styling consistency:
  opacity-tiered labels, rounded rows, `CrfWarningBrush` for errors, IBM Plex, compact spacing.
- How a VAR symbol is drawn (the core brief made it node-less; confirm it renders as a labeled box — a simple
  "VAR" glyph/box with the variable list summarized, like other label-bearing components).

## Behavior
A VAR's "parameters" ARE its variables (from the core brief: each `EditableParameter` row = one variable;
order preserved). The editor must offer **two entry modes** over that same list:

### Mode A — multi-line text (primary; the paste affordance)
A flyout with a single multi-line `TextBox` where each line is `name = expression` (optionally `name =
expression  // unit` or a unit column — keep it simple: `name = expression`, unit optional via a trailing
token or just omit units in text mode and let them default). On commit (apply/close):
- Parse each non-empty line as `name = expression`. Trim; ignore blank lines; allow `#`/`//` comment lines
  (skip them).
- Rebuild the VAR's `Parameters` list from the parsed lines, **preserving order**.
- **Validation (inline, non-blocking):** a line that doesn't contain `=` or has an empty name → highlight/flag
  with `CrfWarningBrush` and a short message; don't lose the user's text. Duplicate names within the box → warn
  (the core extractor also warns, but warn here too so it's caught at author time). Optionally validate each
  expression parses (reuse the existing expression `Parser`/preview used elsewhere — if there's a live `≈`
  preview pattern in the inspector, mirror it per line; otherwise just parse-check).
- Round-trips: opening the flyout serializes the current `Parameters` back to `name = expression` lines (one per
  row, in order) so the text view always reflects the model.

### Mode B — traditional rows (one variable at a time)
The standard parameter-row editor (name / expression / unit columns + add/remove row), exactly like other
components' parameter editors — since a VAR's `Parameters` is the same list type, **the existing parameter-row
editor should already work** on a VAR with minimal/no change. Provide a toggle (segmented `.active` buttons or a
small "Text / Rows" switch) between Mode A and Mode B operating on the **same** underlying `Parameters` list.

**Make Mode A the default** for VAR (the paste-many use case is the point), with Mode B one click away.

## Wiring
- When the selected/edited component is `SymbolKind.Var`, the parameter editor opens the **VAR editor** (Mode A
  default) instead of the generic parameter grid — branch on `SymbolKind.Var` where the editor is launched.
- Edits push through the **existing schematic undo/dirty pipeline** (the parameter editor already does this for
  normal components — route VAR edits the same way so undo/redo + the dirty dot work).
- The on-schematic label for a VAR should show something useful (e.g. the instance name + a compact summary like
  `VAR (3)` or the first var) — follow how other components show their type/instance labels; don't overflow the
  canvas with the full list.

## Palette
VAR appears in the Library Palette via the core brief's registry entry (SearchTerms include "var", "variable",
"sweep"). Confirm it's placeable from the palette and that placing it creates an empty VAR (no default rows),
then the editor opens (or is one double-click away) for the user to add variables.

## Tests
Mostly manual (view code), but add headless where possible:
- **ParseLines_RoundTrips:** a helper that parses multi-line text → `(name,expr)` list and back is covered by a
  unit test (pull the parse/serialize logic into a small testable static so it's not trapped in code-behind):
  `"Pin = -10\nGain = 2*Pin\n# comment\n\nBad line"` → two valid vars + one flagged line; serialize back yields
  the two `name = expression` lines.
- **Duplicate/empty-name flagged** by the same helper.

## Gate
Build 0W/0E. Manual: place a VAR from the palette; open its editor; paste several `name = expression` lines;
apply → another component referencing one of those names resolves on run (end-to-end with the core brief);
switch to Rows mode and edit one variable → reflected back in Text mode; undo reverts a VAR edit; the dirty dot
appears. Two VARs in one cell both contribute variables.

## On completion
Note in `src/Ui/CLAUDE.md`: VAR uses a dedicated editor with a multi-line `name = expression` text mode
(paste-many, default) and a traditional row mode over the same `Parameters` list; parse/serialize is a testable
static; edits flow through the standard schematic undo/dirty pipeline. Completes the VAR component.

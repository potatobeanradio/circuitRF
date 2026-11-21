# Brief FD-P — placing the designed filter, launching the designer from a Filter, and a complete Filter tile

**Status:** proposed · **Date:** 2026-11-21 · **Branch:** new, from `rf-filter-designer`
**Area:** `src/Ui/Filters`, `src/Ui/Views/Filters`, `src/Ui/ViewModels/SchematicViewModel*`,
`src/Ui/ViewModels/ParameterEditorViewModel*`, `src/Design/Schematic/ComponentTypeRegistry.cs`,
`src/Design/Schematic/BuiltInSymbols.cs`, docs.

**Read first and follow:** `CLAUDE.md` (scoping test runs, reading TRX, commercial names, commits
only on request), `docs/design/filter-designer.md`, `docs/design/match.md` §11 (Flatten) and the
paste-ghost notes at the top of `SchematicViewModel.PasteGhost.cs`. Findings go to
`src/Ui/RESOLVED.md`; never quote the owner.

---

## 0. What already exists (verified in the code, 2026-11-21)

| Ask | State today |
|---|---|
| Place the ladder / place a Filter from the designer | **Missing.** The window has Design, Apply to Filter (only when opened on a placed Filter), Save netlist…, and Copy on the schematic pane (clipboard only). |
| A button on the Filter that launches the designer | **Exists in part.** The Properties panel shows *Open Filter Designer…* for a Filter (`ParameterEditorView.axaml`, `OpenFilterDesignerCommand`). There is no entry on the schematic's right-click menu, and the label does not say "synthesize". |
| Bandstop on the Filter tile | **In the code, but not seen by the owner.** `Form` offers `Bandstop` (`NetworkFormOptions`), and `BuiltInSymbols.MatchWaveStack` strikes the middle wave for it (since `a4eb5336d`). This has to be reproduced before anything is changed (§3.1). |
| Filter fields | `EllipticType` was appended in the elliptic work but has **no picker** (`NamedParamOptions` lacks it), so it is a free-text box. The mask (Ap, the stop edges) is not stored on the block at all, so reopening the designer has to guess Fs. |

## 1. "Place filter" and "Place component"

Two buttons in the designer's footer, beside *Apply to Filter*:

- **Place filter**: the realised LC ladder, at the snapped values the table shows.
- **Place component**: one `System ▸ Filter` component carrying the specification.

### 1.1 Which schematic

- Opened on a placed Filter: that Filter's schematic (`FilterDesignerViewModel.Schematic`).
- Opened standalone (Tools ▸ Filter Designer): the workspace's active schematic document
  (`WorkspaceViewModel`'s last active schematic). Pass it in as a `Func<SchematicViewModel?>` so
  it is read when the button is pressed, not when the window opened.
- No schematic: both buttons disabled, and the tooltip says why.
- No design (a refused specification): both buttons disabled, with the refusal as the tooltip.

### 1.2 How it lands: the paste ghost, not a new placement path

`SchematicViewModel` already has the right mechanism. A paste arms a ghost that follows the
pointer on the connection grid; a click places it as one undoable step, and Escape drops it. Make
the existing private `ArmPasteGhost` reachable through one new public entry point,
`ArmFragmentPlacement(SchematicEditModel fragment)`. It reuses the paste path's instance renaming
and grid snapping, and focuses the schematic window. The designer adds no placement code of its own.

- **Place filter**: the fragment is `MatchSchematicCopy.Build(Ladder)`, the same drawing the pane
  shows. The topology gate already proves it extracts to exactly the ladder netlist.
  **Open decision:** the copy ends in two `TermG` terminations. In a real design the filter
  connects to other parts, so the default should be **no terminations**, with the two ends left as
  short wire stubs carrying net labels `IN` / `OUT`. A "with terminations" choice keeps today's
  testbench form.
- **Place component**: a single `EditableComponent` of `SymbolKind.Filter`, with the registry's
  default parameters overwritten by `FilterAssignments()` (the same function Apply uses, so the two
  cannot disagree), plus the mask fields of §3.3.
- When the designer was opened **on** a Filter, *Place filter* offers *Replace this Filter*
  (Match's Flatten has the same `ReplaceInPlace` idea). The ladder goes where the block was, its two
  ends wired to the block's former nets, and the block is removed, all as one undo step. This is
  **phase 2**: the plain placement ships first.

### 1.3 Tests

- `ArmFragmentPlacement` then a click: the components are in the model with unique names, there is
  one undo entry, and undo restores the sheet exactly.
- Place filter: after placement, `NetExtractor` on the sheet contains the ladder: the same node map
  as `TheCopiedSchematicIsTheLadder`, ends on the `IN`/`OUT` nets.
- Place component: the placed Filter's parameters equal `FilterAssignments()`, and simulating it
  gives the designer's ideal curve (`FilterNetwork.At`) to 1e-9.
- Buttons are disabled with a reason when there is no schematic or no design.

## 2. "Synthesize…" on the Filter

- Rename the Properties-panel button to **Synthesize…** (tooltip: *Opens the Filter Designer on
  this Filter*), and update the panel text above it.
- Add **Synthesize…** to the schematic context menu for a selected Filter, calling the same
  `OpenFilterDesignerRequested` path. One command, two entry points, the way Match does Flatten.
- Opened this way, the designer is seeded from the block (`SeedFrom`, which since FD-B puts each
  edge in the right box), *Apply to Filter* writes back, and *Place filter* offers replace (§1.2).
- Reopening the window on the same Filter brings the existing window forward instead of opening a
  second one bound to the same instance. Check how `FilterDesignerWindow.Show` behaves today.
- Tests: the context menu has the item only for a Filter; the command raises the request with the
  selected component; a second invocation does not open a second window.

## 3. The Filter tile: bandstop and the missing fields

### 3.1 Reproduce the bandstop symbol first

The code path exists, so the owner's report means one of these, and the fix depends on which:

1. **The build being run predates `a4eb5336d`** (main/master, not `rf-filter-designer`). Then
   there is nothing to fix; confirm with the owner.
2. **The glyph does not redraw when `Form` changes** in the Properties panel: the render model is
   not invalidated for a parameter-driven glyph. Test: place a Filter, set `Form = Bandstop`
   through `SetParametersCommand`, and assert the component's rendered primitives are those of
   `PrimitivesForFilter(Bandstop)` (middle wave struck, outer waves clear).
3. **The palette/library tile or the docs only ever show the default bandpass glyph**
   (`BuiltInSymbols` line 273, `DocSymbolGlyph`, `SymbolArtworkGenerator`). Then the fix is
   presentation: the docs' Filter table gets the four forms (the Match docs already draw form
   variants), and the palette tooltip lists them.

Write the test for (2) whatever the outcome, because it holds the behaviour in place.

### 3.2 Pickers and descriptions

- `NamedParamOptions(Filter, "EllipticType")` returns `A, B, C, Auto` (the parser accepts them; see
  `EllipticTypes.TryParse`).
- The existing descriptions already say which `Response`/`Form` reads which field. If the parameter
  editor can grey a row out by condition, grey out `Ripple` (Butterworth, Bessel, Gaussian,
  InvChebyshev), `Astop` (all-pole families), `EllipticType` (non-elliptic), `Fc` (band forms) and
  `F1`/`F2` (lowpass/highpass). If it cannot, leave the rows and do not add a mechanism for this
  brief alone.

### 3.3 Mask fields, so the designer round-trips without guessing

Append, with defaults that leave existing designs unchanged and the model unaffected (the block
ignores them, as it ignores a parameter the family does not read):

| Parameter | Meaning | Default |
|---|---|---|
| `Ap` | Passband attenuation the order was sized for, dB | the `Ripple` value |
| `Fs` | Stop edge, lowpass/highpass | empty (designer derives it as today) |
| `Fs1`, `Fs2` | Stop edges, bandpass/bandstop | empty |

*Apply to Filter* and *Place component* write them; `SeedFrom` reads them before falling back to
the derivation it does now. **Open decision:** whether these stay hidden from the schematic label
(recommended: shown in Properties only, like `Ripple`).

The elaborator must not try to evaluate an empty `Fs` as an expression. Add the new names to the
`ResolveEnumNamedParameters` passthrough or give them numeric defaults, whichever the registry's
convention for "unset" is.

### 3.4 Tests

- Pickers: `NamedParamOptions` returns the four letters; an old `.csch` Filter without
  `EllipticType`/`Ap`/`Fs*` loads, simulates and round-trips unchanged (byte-identical save).
- Round-trip: designer → Apply → reopen, for each form, gives back all four edges and Ap exactly
  (not derived).
- Glyph test from §3.1.

## 4. Docs

`docs/user/src/reference/filter-designer.md` (the two buttons and Synthesize…), `components.md` /
`system-components.md` (new fields, bandstop glyph, Synthesize…), the registry descriptions that
feed `{{table: components/Filter}}`, and `docs/design/filter-designer.md`. Regenerate with
`dotnet run --project tools/DocGen -- --page …`. If the Filter docs figure changes, recapture it
with `--figure`.

## 5. Phasing (each phase ends green on its own tests)

1. §3.1 reproduce, plus the glyph test; §3.2 pickers. Small, and settles the "bandstop" report.
2. §1 Place component and Place filter (no replace), with `ArmFragmentPlacement`.
3. §2 Synthesize… in the context menu and the rename.
4. §3.3 mask fields and the exact round-trip.
5. §1.2 Replace this Filter.
6. Docs.

Run scope: `dotnet test tests/Ui.Tests --filter "FullyQualifiedName~Filter|FullyQualifiedName~Match|FullyQualifiedName~Paste|FullyQualifiedName~ParameterEditor"`
while working; `tests/Core.Tests` (registry and elaboration) and `tests/Firewall.Tests` at the end.

## 6. Decisions needed from the owner before phase 2

1. Place filter: ends as `IN`/`OUT` net-labelled stubs by default (recommended), or with the two
   `TermG` terminations as the pane shows them?
2. Mask fields (§3.3) on the block: acceptable as appended, Properties-only parameters?
3. Is the bandstop symbol missing in a build of `rf-filter-designer`, or in another build? (§3.1)

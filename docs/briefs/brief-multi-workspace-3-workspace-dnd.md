# Sonnet Brief — MW3: Workspace-to-workspace drag and drop

**Read `brief-multi-workspace-0-overview.md` first. Land MW1 and MW2 before this** — MW3 is the *gesture*
that produces what MW2 defined, and it has nothing to fall back on if "Reference" is not yet real.

---

## 1. What the user does

Drag a cell (or a file) from workspace A's Project Tree and drop it on workspace B's Project Tree. Window
B comes forward and asks:

```
  Amp  →  RfFrontEnd

  ( ) Copy the cell into RfFrontEnd
        Sub-cells:  (•) Copy them too      ( ) Keep them referenced in AmpProject
  ( ) Reference AmpProject's cell from RfFrontEnd
        (adds "AmpProject" to RfFrontEnd's referenced workspaces — MW2 §2)

  [ Cancel ]  [ OK ]
```

**R-mw3-1. The sub-cell choice is nested under Copy and disabled under Reference**, because a referenced
cell's sub-cells are *always* by reference (MW2 R-mw2-17) — there is no third combination, and offering
one would imply a mode that does not exist.

**R-mw3-2. Remember the last choice for the session and pre-select it**, so moving six cells is six
confirmations and not six decisions. Do **not** persist it across launches: the right answer depends on
what the user is doing that day, and a silently remembered "Reference" would be a nasty surprise months
later.

---

## 2. The drag already works; almost nothing new is needed on the wire

The Project Tree is **already** a drag source for cell nodes, and the payload it emits is exactly what a
cross-window drop needs:

- `new CellDragPayload(cellVm.AbsolutePath).Serialize()` → `"circuitrf-cell:<absolute path>"`, put on
  `DataFormat.Text` (`ProjectTreeView.axaml.cs:250-268`). **It already carries an absolute path**, so a
  drop in another window knows precisely which cell in which workspace it came from — nothing to add.
- It travels on the **native platform pasteboard by deliberate design** (`CellDragPayload.cs:5-9`: an
  in-process format leaves nothing on `NSPasteboard` and crashes AppKit), which is exactly what makes a
  cross-window drag work at all.
- The tree is **already** a drop target, for OS file drops:
  `DragDrop.SetAllowDrop(this, true)` + `OnFileDragOver`/`OnFileDrop` → `tool.AddKnownFile(path)`
  (`ProjectTreeView.axaml.cs:38-40`, `:330-345`).

**R-mw3-3. Extend the existing handlers; do not add a second drop path.** `OnFileDragOver` currently
answers `DragDropEffects.None` to anything that is not an OS file list — teach it `CellDragPayload`, and
`OnFileDrop` to route it. One `AllowDrop` surface, one pair of handlers.

**R-mw3-4. Verify a same-window drop is unchanged.** Dragging a cell within one workspace's tree does
nothing today and must go on doing nothing — the drop handler compares the payload's owning workspace
(the R-mw1-5 ancestor walk-up) against its own, and returns `DragDropEffects.None` when they match.

---

## 3. Focus, and the prompt

The owner asked for window B to take focus and prompt. Two ordering constraints, both real:

**R-mw3-5. Activate the target window from the DROP handler, not from drag-over.** Raising a window
mid-drag on macOS puts a newly-key window under the cursor and the drag can end up delivered to the wrong
control — and Dock's own restack-on-drag was already disabled process-wide for a closely related reason
(`App.axaml.cs:56-68`).

**R-mw3-6. The prompt is a modal dialog owned by window B**, shown after the drag operation has completed
and returned. Showing a modal from inside a drop handler while the platform drag loop is still unwound is
how a drag-drop deadlock is written.

---

## 4. Copy — what it actually has to do

`DuplicateCellAsync` (`WorkspaceViewModel.cs:10442`) already copies a cell folder recursively and fixes up
the primary-view naming. It is the right starting point and it is **not sufficient**, because it copies
within one workspace where relative references still resolve from the copy's new sibling position.

**R-mw3-7. A cross-workspace copy must rewrite the copied cell's own `CellRef`s.** Every sub-cell
reference inside the copied cell is relative to *its* folder; after the copy the base and the depth have
both changed. Under the two sub-cell modes:

- **Copy sub-cells too** — copy the whole reachable sub-tree into B, then rewrite each level's `CellRef`
  to the new relative position. Use the corrected resolved-path matching MW2 R-mw2-15 installs, never the
  last-segment name match.
- **Keep them referenced** — rewrite each sub-cell `CellRef` into the **alias form** MW2 §2 defines
  (`ws://<alias>/…`), creating the alias in B's `.cws` if it is not there yet. Never write a raw `../../`
  path: MW2 R-mw2-5 exists precisely so this gesture cannot be the thing that introduces a second
  convention. These are external references and are therefore **subject to every MW2 rule**, including the
  same-technology refusal (R-mw2-7). If that refusal fires, this mode is unavailable for this cell and the
  dialog must say so rather than producing a broken copy.

**R-mw3-8. A `pdk://` reference inside a copied cell is the trap.** It is not a path and is not rewritten,
so the copy resolves in B **only if B has imported the same kit** (MW1 R-mw1-5 scopes kit resolution to
the referencing document's own workspace). Detect it **before** copying and say so:

> `Amp` uses parts from kit `<name>`, which `RfFrontEnd` has not imported. Copy it anyway (the parts will
> show as unresolved until you import the kit), or reference the cell instead?

Silently producing a cell full of pin-less placeholders is the outcome this requirement exists to prevent.

**R-mw3-9. Name collisions are resolved by asking**, with the existing `InputNameDialog` and
`NameValidator` (`WorkspaceViewModel.cs:10452-10457`) — never by auto-suffixing. `Amp_2` appearing in
someone's project without their say-so is worse than a second dialog.

**R-mw3-10. A cross-workspace copy is a FILE operation and is not undoable.** Say so in the prompt's
confirmation line (the copied path is reported through `Messages.Success` as `DuplicateCellAsync` already
does). Do not build a file-level undo for this; per-document undo is not the right shape for a directory
tree, and a half-working undo here is worse than none.

---

## 5. Files, not just cells

The owner asked for "cells **and files**". The tree's drop side already handles an OS file list
(`ProjectTreeView.axaml.cs:340-345`).

**R-mw3-11. A non-cell file dragged between trees is copied into the target workspace**, with the same
collision prompt and no Reference option — a loose `.s2p`, `.npy` or `.ctech` has no reference semantics
in a `.cws`, and offering "Reference" for one would be a fourth path-convention to maintain
(`DocumentFileRefs.RefBase` already has three: document-relative, workspace-relative, results-relative).
Drop it into the folder node it was dropped on, or the workspace root.

**R-mw3-12. A `.cws` dropped on a tree opens that workspace in a new window** (MW1's `File ▸ New Window`
path), and is never copied. Copying a workspace into a workspace is not a thing.

---

## 6. Gate

`tests/Ui.Tests` (do not touch `src/Core`, `src/Engine`, `RfCore`):

1. **Payload round-trip across workspaces**: `CellDragPayload` from A's tree, parsed at B's drop handler,
   resolves to A's cell folder — and the same-workspace case returns `DragDropEffects.None` (R-mw3-4).
2. **Copy with sub-cells**: the copied sub-tree in B resolves entirely within B; assert no `CellRef` in
   the copy still points into A.
3. **Copy keeping sub-cells referenced**: every sub-cell `CellRef` resolves back into A, and the whole
   thing renders. Add the negative: with differing technologies, the mode is refused (R-mw3-7 → MW2
   R-mw2-7).
4. **The kit trap** (R-mw3-8): copying a kit-bearing cell into a workspace without that kit prompts, and
   choosing "copy anyway" yields cells that report `NotFound` rather than throwing.
5. **Name collision prompts** rather than auto-suffixing (R-mw3-9).
6. **A `.cws` drop opens a window and copies nothing** (R-mw3-12).
7. **The alias is created once and reused**: referencing a second cell from the same workspace adds a
   second `CellRef`, not a second alias entry (R-mw3-7 → MW2 §2).

**Manual, and it is the one that matters:** two real workspaces, a real cell with sub-cells and a kit
part, all four combinations of the dialog, on macOS — because §3's focus ordering is a platform behaviour
that no headless test observes.

---

## 7. On completion

Findings to `src/Ui/RESOLVED.md` — **never `CLAUDE.md`**. Add the gesture to
`docs/design/ui-design.md` alongside the existing palette→canvas drag idiom.

**Report, do not silently absorb:**
- Any platform difference in §3's focus/prompt ordering. If one platform needs a different sequence, that
  is a finding, not a per-platform branch to bury.
- Whether R-mw3-7's "keep sub-cells referenced" mode turned out to be worth having, measured on a real
  hierarchy. It is the mode most likely to be theoretically nice and practically unused, and dropping it
  would simplify the dialog to a single question.

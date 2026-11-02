# Brief 6 — the context menu: delete, add a via, and the two meanings of "plated"

**Series:** [stackup rendering](brief-stackup-render-0-overview.md) · **Tag:** `R-stk6-n`
**Area:** `src/Ui/Controls/StackupCanvas.cs`, `src/Ui/Views/Layout/TechEditorView.axaml(.cs)`, `src/Ui/Layout/TechEditorViewModel.cs`
**Depends on:** brief 2 · **Independent of:** 3, 4, 5, 7

---

## 0. What this brief delivers

Right-click on the drawing, get a menu that depends on what is under the pointer.

| Under the pointer | Menu |
|---|---|
| A conductor | Delete Conductor · ─ · Copy |
| A dielectric | **Add Via** · Delete Dielectric · ─ · Copy |
| A via | Plated hole (✓) · Fill ▸ (Plated / Solid) · Delete Via · ─ · Copy |
| Nothing | Copy |

*Copy* is brief 7's and appears here as a placeholder item, so the menu is built once. If brief 7 has
not landed, ship the item disabled rather than absent.

---

## 1. One `ContextMenu`, built on `Opening`

### R-stk6-1 — the pattern, which was already got wrong once

`LayoutCanvas` and `LayoutEditorView.axaml` carry the fix and its reasoning:

> ONE `ContextMenu` instance, declared once in the view's AXAML on the control. The canvas never
> constructs a `ContextMenu` of its own. Every right-click used to build and manually open its own,
> **which stacked.**

Follow it exactly:

- `TechEditorView.axaml` declares `<ctrl:StackupCanvas.ContextMenu><ContextMenu Opening="OnStackupContextMenuOpening"/></ctrl:StackupCanvas.ContextMenu>`.
- `StackupCanvas.OnPointerPressed` records the right-click point in a `ContextMenuTarget` property and
  does **not** open anything — Avalonia opens the menu itself and raises `Opening`.
- The `Opening` handler calls `ConsumeContextMenuTarget()`, hit-tests the scene, and **rebuilds
  `menu.ItemsSource` fresh**. No pending target ⟹ `e.Cancel = true`.
- A right-click also **selects** what is under it (brief 3), before the menu builds. A menu acting on
  something that is not visibly selected is a menu whose target the user has to guess.

### R-stk6-2 — every item is an existing command or a new one on the view model

Nothing in the code-behind mutates `Working`. Items bind to `TechEditorViewModel` methods, which is
where `CommitEdit` lives.

---

## 2. Delete

### R-stk6-3

`TechEditorViewModel.RemoveStackupLayer(row)` already exists, already snapshots, already commits, and
already produces the description `"Remove stackup layer {name}"`. The menu item calls it. That is the
whole implementation, and undo/redo comes with it.

The item is **labelled by kind** — "Delete Conductor", "Delete Dielectric", "Delete Via" — because that
is what the owner asked for and because a menu that says "Delete" over a picture with three kinds of
thing in it is a menu people misfire.

**No confirmation dialog.** Undo is the confirmation, and it is already there.

Deleting a conductor that a via's span names leaves that via unresolvable. **That is not this brief's
problem to fix and it must not be fixed silently**: `TechValidation` already reports it, the card list
already flags it, and brief 1 `R-stk1-6` draws it as a refusal marker rather than omitting the barrel.
Deleting the via automatically, or silently re-pointing its span, would make a deletion do two things.

---

## 3. Add Via

### R-stk6-4 — the span comes from the dielectric that was clicked

The owner: *"If user right-clicks on a dielectric there's also 'Add Via' menu option… It will create a
via with the via's span set to the next highest and next lowest conductors for the dielectric that the
user right-clicked on."*

So: from that dielectric's index in `Stackup.Layers`, walk **up** to the nearest `Conductor` and **down**
to the nearest `Conductor`, skipping other dielectrics and skipping via entries (which are outside the
order entirely). Those two names are `SpanFromLayer` and `SpanToLayer`.

**If either walk finds no conductor, the item is disabled** with a tooltip saying which side has none —
a dielectric above the top metal or below the bottom one has nothing to span to, and a via with one end
unset is a broken entry the user did not ask for. Disabled-with-a-reason, never enabled-then-refused.

New method beside `AddStackupLayer`:

```csharp
internal void AddViaSpanningAround(StackupLayerRowViewModel dielectricRow)
```

It reuses `NextFreeStackupName(StackupKind.Via)` — which exists because clicking "＋ Conductor" twice
used to produce two entries with one name — and takes the same `Plated`/`Fill`/`WallThicknessDbu`
defaults the "＋ Via" button's entry takes, so a via added from the drawing and a via added from the
button are the same entry. One `CommitEdit`, description `"Add via spanning {from} to {to}"`.

**The new via is then selected** (brief 3), which scrolls the card list to it — the user's next move is
to set its drawing layer and wall thickness, and those are on the card.

---

## 4. Plated, and Fill: two fields, two items, never merged

### R-stk6-5

This is the one place in the series where doing what was literally asked would produce a wrong control.
The overview states it (§3b); here is the resolution.

`StackupLayer` carries **two independent fields**:

| Field | Question it answers | Values |
|---|---|---|
| `Plated` (`bool?`) | Is this hole **metal at all**? | null/true = plated · false = a bare hole (a mounting hole, a routed cutout, an unplated castellation) |
| `Fill` (`ViaFillKind?`) | Which **fill model**? | `Plated` = hollow barrel with a wall · `Solid` = filled. **Both are conductive.** |

`ViaFillKind.Plated` and `Plated` are different things with the same word, and the model's own comments
say so at length. The consequence of merging them is not cosmetic:
`PlanarExtractor.BuildViaBinding` turns every via entry into a conductive `PlanarVia`, so a
millimetre-scale unplated hole modelled as a barrel **shorts every layer it passes through and the run
completes cleanly**.

So the menu carries both, spelled as the card already spells them:

```
☑ Plated hole                 ← toggles StackupLayer.Plated
  Fill  ▸  ● Plated (hollow barrel)
           ○ Solid (filled)   ← sets StackupLayer.Fill
```

**`Fill` is hidden — not disabled — when `Plated` is false**, exactly as the card hides the Fill combo
behind `IsVisible="{Binding IsPlated}"`. A fill model for a hole that is not metal is not a meaningful
choice.

Each is one `CommitEdit` with its own description (`"Make Via1 an unplated hole"`,
`"Set Via1 fill to Solid"`), so undo steps through them one at a time.

### R-stk6-6 — the drawing follows

Brief 1 `R-stk1-7` already draws all three states differently. Toggling either field from the menu
therefore changes the picture immediately through brief 2's `StackupChanged`, with nothing extra to
wire. Assert it — it is the cheapest possible check that the two halves are actually connected.

---

## 5. Every remaining parameter, from the drawing

### R-stk6-8 — "edit all conductor, dielectric and via parameters from the rendering"

That was the ask, and briefs 4, 5 and this one do not yet meet it. Brief 4 covers every **typed**
value; brief 5 covers the via span by drag; §4 above covers `Plated` and `Fill`. What is left is the
handful of parameters that are a **closed choice**, not text — and a closed choice belongs in a menu,
not in a text box.

Add them to the same menu, each bound to the row VM property the card already binds:

| Kind | Item | Row VM property |
|---|---|---|
| Conductor | ☑ Ground reference | `IsGroundReference` |
| Conductor | Metal thickness goes to ▸ (the layer above / the substrate below) | `SelectedSheetAt`, rendered through the existing `SheetAtLabel` converter |
| Conductor | Drawing layers ▸ (checkable, one item per layer) | `SetDrawingLayerChecked` |
| Dielectric | Patterned with ▸ ((none) + one item per conductor) | `SelectedPresentWith` |
| Via | Drawing layer ▸ ((none) + one item per layer) | `SelectedDrawingLayerChoice` |

Three constraints, all of which the card already honours and none of which may be relaxed here:

- **The labels are the card's labels, verbatim.** "Metal thickness goes to:" was deliberately reworded
  away from an earlier spelling that could not be answered without knowing what a conductor's z *band*
  is — a word the control never defined. Do not re-invent the wording in a second place; if it is worth
  changing it is worth changing in both.
- **The tooltips come with them.** These five carry long tooltips on the card because they are the
  fields nobody can answer from the name alone. A menu item with no explanation is the version of this
  control that gets set wrong.
- **Drawing layers is a submenu and it is BOUNDED.** A real process carries several hundred drawing
  layers — the reason the card's own picker is filtered, bounded and virtualized. A flat submenu of 300
  items is unusable. Show only the layers already bound plus a short list, and end the submenu with
  "More… " which selects the entry and lets the card's own filtered picker do the rest. **Say in the
  item that the full picker is on the card**; do not silently truncate.

Each is one `CommitEdit` through the existing property setter, so undo and the live redraw follow with
nothing extra.

## 6. The gate that holds the whole series

### R-stk6-7 — the one-write-path source scan

By this brief the canvas has acquired every category of mutation the series adds. Write the test that
proves it never grew a second write path, modelled on the comment-stripped source scan
`tests/Ui.Tests/AuthoringCliVerbTests.cs` uses for the same purpose against `src/Cli/Authoring.cs`:

**Scan the comment-stripped source of `src/Ui/Controls/StackupCanvas.cs` and every stackup-canvas file
in `src/Ui/Views/Layout/` and assert none of them contains:**

- an assignment into `Working.Stackup`, `Stackup.Layers`, or any `StackupLayer` property,
- a call to `UndoRedo.Execute`, `CommitEdit`, `SnapshotJson` or `ApplySnapshot`,
- a call to `TechPersistence.Serialize` or `Deserialize`.

Everything goes through `TechEditorViewModel` and `StackupLayerRowViewModel`. A rule stated only in a
comment is a rule the next change does not know about; this is what makes it a rule.

**Strip comments before scanning.** This brief's own prose quotes the forbidden strings, and so do the
doc comments the implementation will carry — an unstripped scan fails on its own documentation. That
lesson is already recorded from harmonicaRF H8.

---

## 7. Tests

`tests/Ui.Tests/Stackup/StackupContextMenuTests.cs`:

| Tag | Test |
|---|---|
| `R-stk6-1` | Two successive right-clicks produce one menu, not two stacked ones; the items are rebuilt each time; a right-click with no recorded target cancels `Opening`. |
| `R-stk6-1` | A right-click selects what is under it before the menu opens. |
| `R-stk6-3` | Each Delete item is labelled by kind, removes exactly that entry, pushes one undo entry, and the undo restores it. Deleting a span-referenced conductor leaves the via entry untouched and validation reports it. |
| `R-stk6-4` | Add Via on each dielectric of the shipped four-layer technology produces the expected conductor pair. On a dielectric with no conductor above (or below), the item is disabled. |
| `R-stk6-4` | A via added from the menu and one added with the "＋ Via" button differ only in `SpanFromLayer`/`SpanToLayer` and name. |
| `R-stk6-5` | The Plated-hole toggle writes `Plated` and never `Fill`; the Fill items write `Fill` and never `Plated`; the Fill submenu is absent when `Plated` is false. One undo entry each. |
| `R-stk6-6` | Toggling either field changes the scene — the barrel's drawn form differs across all three states. |
| `R-stk6-8` | Each of the five items writes its own field and only its own; the labels match the card's, character for character (scan the AXAML); the drawing-layers submenu is bounded on a technology with 300 layers. |
| `R-stk6-7` | **The source scan.** |

---

## 8. Scope

No new value editing (brief 4), no drag (brief 5), no actual clipboard work (brief 7). The Copy item is
a placeholder here.

**On completion:** record findings in `src/Ui/RESOLVED.md`. Never in a CLAUDE.md.

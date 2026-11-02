# Brief 4 — editing values on the drawing, with the editor that already exists

**Series:** [stackup rendering](brief-stackup-render-0-overview.md) · **Tag:** `R-stk4-n`
**Area:** `src/Ui/Controls/StackupCanvas.cs`, `src/Ui/Views/Layout/TechEditorView.axaml(.cs)`
**Depends on:** brief 2 (brief 3 recommended first, for the hit plumbing) · **Independent of:** 5, 6, 7

---

## 0. What this brief delivers

Double-click a name, a thickness, a σ, an εr, a tanδ, a µr or a via wall thickness **on the drawing**,
get a text box in place, type, press Return. The card below updates, the drawing redraws, undo undoes
it.

### The instruction that governs this brief

The owner: *"We have already implemented (and debugged) an inline text editor for the Schematic editor
(and Match Designer), so reuse those as much as possible so we don't [have] to debug this one."*

That editor is `src/Ui/Controls/SchematicInlineEditBox.cs`. **Use it as-is.** It is a `TextBox`
subclass carrying three things that took real debugging to get right, and all three are exactly what a
box floated over a Skia canvas needs:

- **`StyleKeyOverride => typeof(TextBox)`.** Avalonia resolves a templated control's implicit
  `ControlTheme` by the control's *own* type and does not fall back to a base type, so a `TextBox`
  subclass finds no theme, gets no template, builds no visual children and **measures zero high** —
  while remaining focusable, holding its text, and reporting `IsVisible = true`. Nothing throws and
  nothing warns. This was reported three times before it was found. It is already fixed inside the
  class; a hand-rolled box here would re-earn it.
- **`MarginFor(screenX, screenY, fontSize)`** — the placement that puts the box's text left edge and
  **baseline** exactly on the Skia text underneath it, using an `AscenderRatio` measured from the
  typeface rather than assumed.
- **`WidthFor(text, fontSize)`** — a Skia measurement of the actual string, through
  `InlineEdit.WidthFromMeasuredText`'s shared slack-and-floor rule. Not a character count: "Ω", "µ" and
  "∞" are wrong by a different amount at every font size.

### R-stk4-1 — the one precondition

`SchematicInlineEditBox.WidthFor` measures with `SkiaFonts.PlexRegular`. **The label the box opens over
must be drawn in that same face**, or the box does not line up with the text it replaces. Brief 1
`R-stk1-9` already requires it; restate it here because this is where it would be noticed and this is
where it would be "fixed" by nudging the margin instead.

What is **not** reused, deliberately, and what the class's own comment says is not reusable: the
hosting. Which surface the box floats over, what a hit means, and what a commit writes are genuinely
different in each host. Those are §1–§3 below.

`FontSizeAt` / `LabelWorldSize` on that class are the schematic's own zoom arithmetic — do not call
them. The stackup canvas has no zoom; pass the scene's own label font size straight to `Open`.

---

## 1. Hosting

### R-stk4-2

`TechEditorView.axaml`: the drawing's `ScrollViewer` content becomes a `Panel` holding the
`StackupCanvas` and one `ctrl:SchematicInlineEditBox x:Name="StackupInlineEdit"` — exactly as
`SchematicView.axaml` hosts its own. One box, reused, hidden at rest. Never one per label.

The box is positioned by `Margin` in the panel's coordinates, so the panel and the canvas must share an
origin. Inside a `ScrollViewer`, a margin is in **content** coordinates, not viewport coordinates —
which is what you want: the box scrolls with the drawing. Verify it rather than assuming it; if it does
not, close the editor on scroll rather than chasing the offset.

### R-stk4-3 — the gesture

**Double-click**, on a `Label` hit whose `StackupField` is not `None`. Single click selects (brief 3);
a single click that opens an editor is an editor the user only meant to click past —
`InlineEditText`'s own doc comment records that finding.

A double-click on a band but not on an editable label does nothing extra (it has already selected).

### R-stk4-4 — open

```
var hit = scene.HitTest(x, y);
if (hit is not { Kind: StackupHitKind.Label, Field: not StackupField.None }) return;

string seed = /* the row VM's staged string for that field */;
box.Open(seed, hit.Rect.Left + pad, labelBaselineY, scene.LabelFontSize);
box.SelectValueOnly();
box.Focus();
```

`SelectValueOnly` pre-selects the numeric part and leaves the unit standing, so typing replaces the
number and keeps the "µm" — which is what the owner meant by reusing the editor that "selects units
within the text properly".

**Seed from the row VM's staged string, never from a freshly formatted number.** `StagedThicknessText`,
`StagedSigmaSm`, `StagedEpsr`, `StagedTanD`, `StagedMur`, `StagedWallThickness`, `StagedName` are the
strings the card shows. Formatting a second time in a second place is how the drawing and the card come
to disagree about a trailing zero, and then an edit that touches nothing writes a changed file.

---

## 2. Commit — through the row VM, never around it

### R-stk4-5 — the series' governing rule, at its sharpest point

A commit does exactly two things:

```csharp
row.StagedThicknessText = box.Text ?? "";   // the same property the card's TextBox binds
row.CommitThickness();                      // the same method the card's LostFocus handler calls
```

and nothing else. No `Working.Stackup.Layers[i].ThicknessDbu = …`, no `CommitEdit` call of its own, no
`UndoRedo.Execute`.

`StackupLayerRowViewModel` already owns, per field: the parse, the unit resolution through
`LayoutUnits` in the technology's `DefaultDisplayUnit`, the invariant-culture rule, the error text
(`ThicknessError`, `HasThicknessError`), the `needsvalue` state, the no-op check, and the `CommitEdit`
that pushes the undo entry and fires `ApplySnapshot`. All of that is why the field mapping table is the
whole of this section:

| `StackupField` | Staged property | Commit method |
|---|---|---|
| `Name` | `StagedName` | `CommitName()` |
| `Thickness` | `StagedThicknessText` | `CommitThickness()` |
| `Sigma` | `StagedSigmaSm` | `CommitSigmaSm()` |
| `Epsr` | `StagedEpsr` | `CommitEpsr()` |
| `TanD` | `StagedTanD` | `CommitTanD()` |
| `Mur` | `StagedMur` | `CommitMur()` |
| `WallThickness` | `StagedWallThickness` | `CommitWallThickness()` |

A bad value therefore behaves on the drawing exactly as it does on the card: the commit is refused or
staged, the card shows the error, and the drawing shows the old value. **Do not add a second refusal
message on the canvas.** One source of truth for the rule means one source of truth for what it says
when it is broken.

`CommitEdit` no-ops when nothing changed, so opening an editor and pressing Return without typing costs
no undo entry — for free, and only because this path goes through it.

### R-stk4-6 — the three keys, and `Esc`'s two jobs

The contract every inline editor in this application honours, and the class's own comment says it is the
host's to wire because the control raises nothing and handles no key itself:

- **Return** commits and sets `e.Handled`.
- **LostFocus** commits. This is the one that costs the user an edit if it is missed.
- **Escape** reverts — closes the box, writes nothing.

And the collision with brief 3: **while the box is open, `Esc` reverts the edit and the selection
stands.** A second `Esc`, box closed, clears the selection. The canvas's `Esc` handler checks
`box.IsVisible` first and returns if it is set.

`R-stk4-7` — **a commit closes the box.** `ApplySnapshot` rebuilds every row VM and brief 2's
`StackupChanged` rebuilds the scene, so a box left open is pointing at rects that no longer exist.
Close it before the commit, not after.

`R-stk4-8` — **a scene rebuild from anywhere else closes the box too.** An undo, a redo, an edit
committed from the card below, or brief 6's Add Via: all of them invalidate the label the box is sitting
on. Subscribe to `StackupChanged` and close.

---

## 3. Tests

`tests/Ui.Tests/Stackup/StackupInlineEditTests.cs`:

| Tag | Test |
|---|---|
| `R-stk4-3` | A single click on a value label does not open the box; a double-click does. A double-click on a `None` label does not. |
| `R-stk4-4` | The box opens seeded with the row VM's staged string, character for character, for each of the seven fields. |
| `R-stk4-4` | `SelectValueOnly` leaves the unit unselected on a thickness ("1.6 mm" selects "1.6"). |
| `R-stk4-5` | **The byte gate.** Editing a thickness through the canvas and editing it through the card's own text box produce a `TechPersistence.Serialize` output that is **byte-identical**, and **one** undo entry each. Repeat for all seven fields. This is the test that holds the whole series' rule; write it first. |
| `R-stk4-5` | An unparseable value leaves the model unchanged, pushes no undo entry, and sets the row's existing error property — the same three outcomes the card produces for the same input. |
| `R-stk4-6` | Return commits; losing focus commits; `Esc` reverts and leaves the model untouched. |
| `R-stk4-6` | With the box open, `Esc` closes it and leaves `SelectedStackupLayerName` set; a second `Esc` clears it. |
| `R-stk4-7` / `R-stk4-8` | The box is closed after a commit, after an undo, and after an edit committed from the card. |

---

## 4. Scope

- The seven fields in the table. **`Span` is not typed** — it is a pair of conductor names, dragged in
  brief 5 and chosen from the card's combos. `SheetAt`, `PresentWithLayer`, `IsGroundReference`,
  `Plated`, `Fill` and the drawing-layer bindings are not free text either: combos and checkboxes on
  the card, and brief 6 puts the via ones in the context menu.
- No drag, no menus, no clipboard.
- Do not modify `SchematicInlineEditBox`, `InlineEdit` or `InlineEditText`. If something genuinely does
  not generalise, **stop and report it** rather than editing a control three other surfaces depend on:
  the schematic page's own inline editing has already been taken out once, silently, by a one-line
  change to a shared control.

**On completion:** record findings in `src/Ui/RESOLVED.md`. Never in a CLAUDE.md.

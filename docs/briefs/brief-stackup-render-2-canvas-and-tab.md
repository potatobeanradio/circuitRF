# Brief 2 — the canvas in the tab, and the tab's re-layout

**Series:** [stackup rendering](brief-stackup-render-0-overview.md) · **Tag:** `R-stk2-n`
**Area:** `src/Ui/Controls/`, `src/Ui/Views/Layout/TechEditorView.axaml`, `src/Ui/Layout/TechEditorViewModel.cs`
**Depends on:** brief 1 · **Blocks:** briefs 3–7

---

## 0. What this brief delivers

The drawing, in the tab, repainting live — and **read-only**. No selection, no editing, no drag, no
menus. Those are briefs 3–7 and each of them is small precisely because this one carries the hosting.

Plus the tab re-layout the owner asked for, which is what makes room for it.

---

## 1. `StackupCanvas`

`src/Ui/Controls/StackupCanvas.cs`, modelled directly on `WBondProfileCanvas` — read that file first;
it is the smallest complete example of this pattern in the repo.

### R-stk2-1 — the shape

```csharp
public sealed class StackupCanvas : Control
{
    public static readonly DirectProperty<StackupCanvas, TechEditorViewModel?> ViewModelProperty = …;
    public StackupRenderTheme Theme { get; set; } = StackupRenderTheme.Fallback;

    protected override Size MeasureOverride(Size available);   // → (available.Width, scene.Height)
    public override void Render(DrawingContext context);       // → context.Custom(new StackupDrawOperation(…))
}
```

- Drawing goes through an `ICustomDrawOperation` that leases `ISkiaSharpApiLeaseFeature` and calls
  `StackupRenderer.Draw` — `WBondProfileCanvas.ProfileDrawOperation` is the template, including
  `Equals(other) => false`.
- **The scene is built in `MeasureOverride` and cached**, keyed on `(technology snapshot, width)`. It is
  read by `Render` and, from brief 3 onward, by every pointer handler. One build per layout pass, not one
  per frame and not one per hit-test.
- `MeasureOverride` returns the scene's intrinsic height (brief 1 §2). That is what makes the
  `ScrollViewer` in §2 scroll.

### R-stk2-2 — theme resolution follows `LayoutCanvas`

Resolve `StackupRenderTheme` from `ThemeService` exactly as `WBondProfileCanvas.RefreshTheme` resolves
`LayoutRenderTheme`, subscribe to the theme-changed event, and **unsubscribe on detach**. A handler left
on a detached canvas repaints a document it no longer shows and keeps that document alive — the bug
`WBondProfileCanvas`'s own `ViewModel` setter carries a comment about.

### R-stk2-3 — live repaint, from one signal

`TechEditorViewModel.ApplySnapshot` is the **single** choke point for a committed edit, an undo and a
redo (its own doc comment says so, and that is why `TechLiveChanged` only fires from there). Add one
event beside it:

```csharp
/// <summary>Raised after every committed edit, undo and redo — the stackup drawing's cue to rebuild
/// its scene. Fired from ApplySnapshot for the same reason TechLiveChanged is: it is the one place
/// Working is replaced.</summary>
public event Action? StackupChanged;
```

The canvas subscribes, invalidates its cached scene and calls `InvalidateMeasure()` **and**
`InvalidateVisual()` — measure as well as visual, because a thickness edit changes the scene's height
and a visual-only invalidation repaints the old height.

That single wire satisfies the owner's "must be updated live (both in the scroll view and in the image
rendering)" for **every** edit path at once: a card's text box, a card's combo, an Add button, an undo,
a redo, and every edit briefs 4–6 add. There is no second place to remember.

### R-stk2-4 — the canvas must survive a null and an empty technology

`ViewModel` is null before the data context binds, and a brand-new technology has an empty stackup.
Both draw the background and nothing else — never an exception, never a zero-size measure that collapses
the splitter.

---

## 2. The tab, re-laid out

Current structure of the Stackup `TabItem` (`TechEditorView.axaml` ~515–890), top to bottom:

1. `Grid` docked Top — boundary combos, the three Add buttons, the stack-height/count summary
2. `Grid` docked Top — the "Filter by stackup layer name…" box and its count
3. `ListBox Classes="rows" x:Name="StackupList"` — filling the rest

### R-stk2-5 — the new structure

1. The boundary/Add/summary grid — **unchanged, still docked Top**
2. **NEW: the drawing**, in a `ScrollViewer` (vertical auto, horizontal disabled)
3. **NEW: a `GridSplitter`**
4. The filter row — **moved down, below the splitter**
5. `StackupList`

The owner asked for the filter box to move down with the card list, because it filters the cards and has
nothing to do with the drawing. It goes below the splitter for the same reason: it belongs to the
bottom pane.

`DockPanel` cannot express a splitter between two proportional panes, so **the tab's content becomes a
`Grid` with `RowDefinitions="Auto,*,Auto,Auto,Auto"`** (header grid / drawing / splitter / filter /
cards) — or `Auto,*,Auto,Auto,2*` if the cards get the star. Pick one and say why in a comment; the
proportions below are the requirement, the row spelling is not.

### R-stk2-6 — the default split

The owner: *"The height of the current scroll view should be reduced. Perhaps only as tall as what a
conductor entry is currently using."*

So the **card list opens at roughly one conductor card tall** and the drawing takes the rest, with the
splitter available for anyone who wants it the other way round. Do not hard-code a pixel height for
"one conductor card": measure it, or state it as a constant derived from the card's own row count and
say in a comment that it tracks the card template. A magic 180 that stops matching the card after the
next template change is exactly the kind of silent drift this tab has already had.

Both panes have a stated minimum height so the splitter cannot be dragged to annihilate either.

### R-stk2-7 — the drawing scrolls, and only vertically

A 20-layer stack does not fit. Vertical `ScrollViewer`, `HorizontalScrollBarVisibility="Disabled"` —
the scene fits the pane's width by construction (brief 1 §2), so a horizontal scrollbar would mean the
scene got its own width wrong.

The existing tunnelling Page-Up/Page-Down handler in `TechEditorView.axaml.cs` scrolls "the pane". With
two scrollable panes it has to pick one: **it keeps scrolling the card list**, which is what it scrolls
today and what those keys mean in every other tab of this editor. Test it (`R-stk2-10`) — this is the
kind of change that quietly re-points a keystroke.

---

## 3. The conductor card gets shorter: the drawing-layer picker becomes 3 × 4

### R-stk2-8

The owner: *"Make the drawing layers scroll view (for conductors) 3 elements wide with a height to only
support 4 elements. This will reduce the overall height of the conductor entry by 2 rows."*

Today (`TechEditorView.axaml` ~868): a single-column `ListBox Classes="rows"` of `DrawingLayerCheckItem`
checkboxes at `MaxHeight="140"`, inside the `AllowMultipleDrawingLayers` panel.

Change it to a **3-column, 4-row grid**: an `ItemsPanelTemplate` of `UniformGrid Columns="3"`, with the
`MaxHeight` set to exactly four rows of the item height. Twelve layers visible at once instead of four,
in a third of the vertical space.

Three things not to break, all of which are already load-bearing on this control:

- **It stays virtualized-or-bounded.** The comment at the top of the file records that a bare
  `ItemsControl` in a `ScrollViewer` is not virtualized in Avalonia, and a real process carries several
  hundred drawing layers. A `UniformGrid` items panel is **not** virtualizing. Bounded height plus the
  existing `DrawingLayerFilter` is what keeps it affordable — verify against a technology with a few
  hundred layers before declaring this done, and if it is not affordable, use a wrapping virtualized
  panel instead and say so.
- **The filter box above it stays.** Three columns does not make a 300-layer list findable.
- **`DrawingLayerSummary` stays.** It is what tells the user what is checked when the checked items have
  scrolled out of view.

### R-stk2-9 — and the card list's own item height falls

Two fewer rows per conductor card is the point of `R-stk2-8` — it is what makes `R-stk2-6`'s
one-card-tall default a useful amount of list. Assert it: a conductor card measures shorter after this
change than before, on the shipped four-layer technology.

---

## 4. Tests

New: `tests/Ui.Tests/Stackup/StackupCanvasTests.cs`, `tests/Ui.Tests/Stackup/TechEditorStackupTabLayoutTests.cs`.

| Tag | Test |
|---|---|
| `R-stk2-1` | The canvas measures to the scene's intrinsic height; a technology with more layers measures taller. |
| `R-stk2-3` | Editing a thickness through `StackupLayerRowViewModel.CommitThickness()` raises `StackupChanged` exactly once; so does an undo, and so does a redo. |
| `R-stk2-3` | The canvas rebuilds its scene on `StackupChanged` — the scene object after the edit is not the one before it, and its height reflects the new thickness. |
| `R-stk2-4` | A null view model and an empty stackup both render without throwing and measure non-zero. |
| `R-stk2-5` | The Stackup tab's visual tree holds, in order: the header grid, the drawing's `ScrollViewer`, a `GridSplitter`, the filter box, the card `ListBox`. |
| `R-stk2-6` | Both panes honour their minimum heights; the default split gives the card list its stated opening height. |
| `R-stk2-8` | The drawing-layer picker's items panel has 3 columns and its `MaxHeight` admits exactly 4 rows. |
| `R-stk2-9` | A conductor card's measured height is below a stated ceiling on the shipped four-layer technology. |
| `R-stk2-10` | Page Down with focus in the tab scrolls the card list, not the drawing. |

`TechEditorNarrowWidthTests` and `TechEditorLayerColumnLayoutTests` already exist and already exercise
this view — **run them** (`--filter "FullyQualifiedName~TechEditor"`) and fix what this change breaks
rather than adjusting their expectations without reading them.

---

## 5. Scope

- **Read-only.** The canvas handles no pointer event in this brief. Adding "just a click" here is what
  makes brief 3 impossible to review.
- The Layers, DRC and Interchange tabs are untouched.
- No model change, no `.ctech` change.
- `tech-editor-stackup.svg` **will** change, because the tab has changed. Do not regenerate it here —
  brief 8 regenerates every affected figure in one pass, so the churn is one reviewable change rather
  than six.

**On completion:** record findings in `src/Ui/RESOLVED.md`. Never in a CLAUDE.md.

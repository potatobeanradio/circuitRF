# Sonnet Brief — Data Display fix: right-click stacks multiple context menus

Same class of defect as the layout-canvas fix that just landed
(`brief-L1-fix-context-menu-stacking.md`), in `src/Ui/DataDisplay/`. **Read that brief first** — the
reasoning is identical and is not repeated here.

**The good news: this codebase already contains three correct implementations of the two patterns needed,
two of them in the very files being fixed.** This is a convergence job, not a design job. Do not invent
anything.

---

## 1. Audit — four sites, three broken

| Site | Where | Status |
|---|---|---|
| Main plot menu | `PlotControl.cs` ~1120 | ✅ **CORRECT** — `_contextMenu ??= BuildContextMenu(); RefreshAddMarkerSubmenu(); _contextMenu.Open(this);` |
| Marker info box menu | `MarkerInfoBoxView.axaml.cs` 77–79 | ✅ **CORRECT** — one instance, `Opening` → `RebuildContextMenu`, assigned to `ContextMenu` |
| **Marker menu** | `PlotControl.ShowMarkerContextMenu` ~1691, opened 1740 | ❌ `new ContextMenu()` per call |
| **Trace header menu** | `PlotControl.ShowTraceHeaderContextMenu` 1786, opened 1793 | ❌ `new ContextMenu()` per call |
| **Table menu** | built `BuildTableContextMenu` 1796, called + opened 1070–1071 | ❌ fresh menu per call |

Every broken site constructs a `ContextMenu`, calls `Open(this)`, and never closes or reuses it — so each
right-click stacks another popup and leaks the menu's `MenuItem` `Click` closures.

The Data Display *views* (`PlotCanvasView`, `PlotContainerView`, `DataDisplayView`, `TabHeaderView`,
`PlotInspectorView`) construct no menus at all. The defect is confined to these two files.

## 2. The two correct patterns, both already present

**Pattern A — static content: cache the instance.** Exactly what the main plot menu does one thousand lines
above the broken sites, in the same file:

```csharp
_contextMenu ??= BuildContextMenu();
_contextMenu.Open(this);
```

**Pattern B — dynamic content: one instance, rebuilt on `Opening`.** Exactly what `MarkerInfoBoxView` does:

```csharp
var menu = new ContextMenu();
menu.Opening += (_, _) => RebuildContextMenu(menu);
ContextMenu = menu;
```

**And `PopulateMarkerMenu` is already repopulation-safe** — line 121 is `menu.Items.Clear()` before it adds
anything, and every item it adds is freshly constructed. It was written to be called repeatedly on the same
menu. That is why Pattern B works today in the info box, and it means **the shared helper needs no change at
all**.

## 3. Fix, site by site

### 3.1 Table menu — Pattern A, a one-line fix

`BuildTableContextMenu` mirrors `BuildContextMenu` in every way except that its **call site forgot the
cache**. Add a `_tableContextMenu` field and change line 1070 to `_tableContextMenu ??= BuildTableContextMenu();`.

If its content turns out to depend on click context, use Pattern B instead — but check first; the builder
takes no arguments, which strongly suggests it does not.

### 3.2 Marker menu — Pattern B

`ShowMarkerContextMenu(marker, trace)` builds a menu whose contents genuinely depend on which marker and
trace were clicked, so it cannot be a static cached instance and cannot be XAML-declared.

Hold one `_markerContextMenu` field. On right-click: record `marker` and `trace` in fields, then
`_markerContextMenu ??= new ContextMenu()`, call the **existing** `MarkerInfoBoxView.PopulateMarkerMenu` on
it (it clears itself), and `Open(this)`. **`Close()` before `Open()`** so a second right-click while the menu
is up replaces rather than re-opens.

Better still, if it fits without disturbing the pointer routing: attach the menu once via `Opening` exactly
as `MarkerInfoBoxView` does, so Avalonia owns open/close/light-dismiss. Prefer that; fall back to the
explicit `Close`/`Open` form if the right-click routing in `OnPointerReleased` makes `Opening` awkward.

### 3.3 Trace header menu — Pattern B

Same treatment, with its own `_traceHeaderContextMenu` field and its own populate step. Its content depends
on the clicked `Trace`.

### 3.4 Do not re-subscribe `Click` on reused `MenuItem`s

The trap from the layout brief applies here too: build **fresh** `MenuItem` objects on every populate and
clear the menu first. Reusing an item and adding another `Click` handler makes the action fire N times on the
Nth opening — worse than the bug being fixed. `PopulateMarkerMenu` already does this correctly; match it in
the trace-header and table builders.

## 4. Scope guardrails

- These two files only: `src/Ui/DataDisplay/Controls/PlotControl.cs` and
  `src/Ui/Views/DataDisplay/MarkerInfoBoxView.axaml.cs` (the latter likely needs **no change** — verify and
  leave it alone if so).
- **No changes to menu contents, item ordering, enablement, or any command behaviour.** This is purely about
  instance lifetime.
- Do not touch `MarkerInfoBoxView.PopulateMarkerMenu`'s body — it is the shared contract for both surfaces
  and is already correct.
- Do not touch the layout editor, the symbol editor, or `SchematicCanvas`. If the `new ContextMenu` audit
  turns up further sites outside these two files, **report them and stop** — they get their own brief.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 5. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **No `ContextMenu` is constructed per right-click.** Assert structurally: after N right-clicks on each of
   the three surfaces (marker, trace header, plot table), the number of `ContextMenu` instances created is
   ≤ 1 per surface. If that cannot be reached headlessly, assert that no `new ContextMenu()` remains inside a
   right-click handler path in either file — the invariant that makes stacking impossible.
3. **Ten consecutive right-clicks on a marker leave exactly one menu open**, and the same for a trace header
   and the plot table.
4. **Content is still correct per click** — right-click marker A, dismiss, right-click marker B: the menu
   shows B's items, not A's. This is the regression risk of caching, and `PopulateMarkerMenu`'s
   `Items.Clear()` is what makes it safe.
5. **Click handlers fire exactly once** — open a marker menu five times and invoke the same item; assert the
   action ran once (§3.4).
6. **Both surfaces still agree** — the menu from the plot canvas and the menu from the marker info box offer
   identical options, which is the stated purpose of the shared helper.
7. **Marker vs. empty-area routing is unchanged** — right-clicking a marker still gives the marker menu;
   right-clicking empty plot area still gives the main plot menu.

## 6. On completion

Add a "Data Display fix (context menu)" note at the top of `src/Ui/CLAUDE.md` recording: the four sites and
which one was already correct; the **two patterns — cached instance for static content, single instance
rebuilt on `Opening` for dynamic content** — and that both already existed in these files; that
`PopulateMarkerMenu` was already repopulation-safe (`Items.Clear()` first) and needed no change; and the
result of the wider `new ContextMenu` audit.

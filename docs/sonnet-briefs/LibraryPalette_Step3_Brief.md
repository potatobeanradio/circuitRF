# Library Palette — Step 3: responsive grid + header (category filter + search) (Claude Code / Sonnet)

Turn the inert tile list into the real Palette panel: a **width-driven responsive grid** of tiles + a
**header** with a **category ComboBox** and a **search field**. **This brief is ONLY step 3** — layout +
filter + search + dock/tear-off hosting. **No placement state machine, no arming, no drag-and-drop, no MRU
persistence** — those are steps 4+. Read `library-palette.md` §4 + §5 first. Sub-gated; **report and stop
between every layer.** Firewall green.

> Read first: `docs/design/library-palette.md` §4 (layout — width-driven column count, one rule for dock +
> tear-off, scrollable), §5 (header — category ComboBox incl. virtual All/Common/Recently-Used, search by
> type/category/name, no parameter search). Context code: `src/Ui/ViewModels/Dock/PaletteTool.cs` (the host —
> currently `Items => LibraryCatalog.AllItems`; extend with filter/search state), `src/Ui/Controls/
> PaletteTile.axaml(.cs)` (step 2 — the tile to lay out in the grid), `src/Ui/Schematic/LibraryCatalog.cs`
> (`AllItems`, `Common`, `ByCategory`, `RecentlyUsed(mru)`, `Search(query, category?)` — the filter/search
> source from step 1), `src/Ui/ViewModels/Dock/ProjectTreeTool.cs` (the dock-tool + header pattern; how a tool
> exposes filter state + a header view), the Project Tree view (its header row with controls — mirror for the
> Palette header), the dock tear-off + resize machinery (existing). Design docs win on any conflict.

## The spine (do not violate)
- **Width-driven column count, ONE rule** (§4): `columns = max(1, floor(availableWidth / tileWidth))`. The
  dock default width yields ~2 columns; tearing off + widening adapts automatically. **No docked-vs-floating
  special-case.** Use a wrap/uniform-grid panel that reflows by width (e.g. a `WrapPanel` of fixed-size tiles,
  or an items panel that computes columns from width) — scrollable vertically.
- **Filter + search compose** (§5): the category ComboBox selects a (virtual or real) category; the search
  field filters within it (or All). Both drive the displayed item set via `LibraryCatalog`.
- **Virtual categories** in the ComboBox: **All · Common · Recently Used · Lumped · Transmission Line ·
  Microstrip · Sources · Data Files · Terminals** (whatever real categories exist). **Recently Used** uses an
  MRU list — for step 3 supply an **empty/in-memory** MRU (the persistent store is step 4); the category still
  appears and works against whatever list is supplied.
- **No parameter search** (§5) — type name / display name / category / aliases only (that's what
  `LibraryCatalog.Search` already does).
- **No placement/arming/DnD/MRU-persistence** (steps 4+) — tiles still don't *do* anything on click yet.
- **Scope fence (step 3):** grid layout + header filter/search + dock/tear-off. NO placement state machine,
  NO arming, NO drag-and-drop, NO MRU persistence.

---

## LAYER 1 — filter/search state on the PaletteTool + the displayed item set

1. Extend `PaletteTool` with **filter state**: a selected **category** (an enum incl. the virtual
   All/Common/RecentlyUsed) and a **search query** string. Expose a **`DisplayedItems`** (observable) computed
   from `LibraryCatalog`:
   - category All → `AllItems`; Common → `Common`; RecentlyUsed → `RecentlyUsed(mru)` (mru empty for now);
     a real category → `ByCategory(cat)`;
   - then apply `Search(query, realCategoryOrNull)` (search composes — search within the selected real
     category, or across All for the virtual ones; keep it simple and consistent with the step-1 helper).
   - Recompute `DisplayedItems` when category or query changes (MVVM observable).
2. Expose the **category list** for the ComboBox (the virtual + real categories, display-named).

**Layer 1 gate:** changing the category or the query updates `DisplayedItems` correctly (All/Common/a real
category; search "cap" narrows to the capacitor; search within a category composes); headless-ish VM check.
Report.

---

## LAYER 2 — the header (category ComboBox + search field)

The Palette panel header (mirror the Project Tree header pattern):
1. A **category ComboBox** bound to the category list → sets `PaletteTool`'s selected category.
2. A **search TextBox** (with a magnifier/clear affordance) → sets the query (live filter as you type).
3. Compact, themed, sits above the grid; empty-result state ("No matching components.").

**Layer 2 gate:** the header shows the category ComboBox + search field; selecting a category filters the
grid; typing in search filters live; clearing search restores; an empty result shows the message. Report.

---

## LAYER 3 — the responsive grid + dock/tear-off

1. The grid binds to `DisplayedItems`, rendering a `PaletteTile` each, in a **width-driven reflow panel**
   (`columns = max(1, floor(width / tileWidth))`), **scrollable** vertically. Uniform square tiles.
2. Host in the **`PaletteTool` dock panel** (already a Tool); ensure it's in the dock layout (near Project
   Tree / Properties). **Tear-off** → resizable window; widening reflows columns (the one width rule — verify
   it adapts on resize, no special-case).
3. Default dock width → ~2 columns (a consequence of the width rule + the default region width, not a
   hardcoded 2).

**Layer 3 gate:** the Palette shows a reflowing grid of filtered tiles; narrowing → fewer columns (min 1),
widening (incl. torn-off + resized) → more; vertical scroll when overflowing; docked default ≈2 wide. Report
(screenshot description).

## Acceptance (step 3)
1. `PaletteTool` carries category + search state and computes `DisplayedItems` via `LibraryCatalog`
   (virtual + real categories, composing search; Recently-Used against a supplied [empty] MRU).
2. A header (category ComboBox + search field) drives the filter; the grid is a width-driven reflow
   (`max(1, floor(width/tileWidth))`), scrollable, dock + tear-off-resizable with one rule.
3. `dotnet build`/`dotnet test` green; firewall green; **no placement, arming, drag-and-drop, or MRU
   persistence** (steps 4+); nothing else regresses.

## Guardrails
- **One width rule** for columns (dock + tear-off); no docked-vs-floating special-case.
- **Filter + search compose** via `LibraryCatalog`; **no parameter search**.
- **Virtual categories** (All/Common/Recently-Used) + real ones in the ComboBox; Recently-Used MRU is
  supplied (empty for now) — persistence is step 4.
- **No placement/arming/DnD/MRU-persistence** — tiles are still inert on click.
- **Scope fence:** layout + header + filter/search + hosting only.
- Sub-gate the three layers; report and stop between each.
- Update `library-palette.md` §10 status (step 3 done) and `src/Ui/CLAUDE.md` (Palette grid is width-driven;
  header category+search via LibraryCatalog).

*Exit: the Palette is a real, filterable, responsive grid — category ComboBox + search, width-driven columns,
dock + tear-off — ready for the placement state machine (step 4) to make tiles actually place components.*

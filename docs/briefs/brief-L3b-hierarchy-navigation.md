# Sonnet Brief — Phase L3b: hierarchy navigation, edit-in-place, and cell-reference tracking

**Design:** `docs/design/layout-view.md` §7 (hierarchy), plus `schematic-hierarchy-navigation.md` — the
schematic solved this problem already and its nav-frame model is the thing to mirror, not re-derive.
**Consumes L3a and its follow-ups.**

**Scope is L3b: push in, edit, come back, and keep cell references honest.** Flatten and group-into-cell are
**L3c**.

**Test loop** (root `CLAUDE.md` §"Fast test loop"): two commands — this SDK rejects multiple project paths
in one invocation, and the `Category!=Nightly` filter is what actually does the work:
```
dotnet test tests/Ui.Tests --filter "Category!=Nightly" --no-build
dotnet test tests/Firewall.Tests --no-build
```
~28 s. Without the filter, `Ui.Tests` alone is still ~5 minutes. The **full unfiltered suite still gates
completion**.

---

## 1. Navigation

Reuse the schematic's nav-frame model, `IHierarchyHost`, and its breadcrumb affordance. The layout side
should differ only in what it resolves (`CellLayoutResolver`, already built in L3a) and what it draws.

- **Push in**: double-click a selected instance, plus a menu/keyboard command. Opens the sub-cell's layout in
  the same document frame with a breadcrumb showing the path.
- **Pop out**: returns to the parent, restoring the parent's **viewport and selection** — a pop-out that
  dumps the user at a default zoom in a large layout is disorienting and is the detail most likely to be
  skipped.
- **Push into an array** enters the sub-cell once. There is no per-placement context; R-L3a-5 already
  established that an array is one object.
- **Unresolvable instance**: push-in is disabled with a reason (R13a), since there is nothing to enter.
- **Depth**: the breadcrumb is the depth indicator; R-L3a-2's render depth cap is unrelated and stays.

**Match whatever the schematic already does for edit-in-place versus open-as-separate-document.** Do not
introduce a second model — check `schematic-hierarchy-navigation.md` and follow it exactly, including how it
handles a sub-cell that is already open in another tab.

## 2. Editing a sub-cell must refresh its parents

This is the part with no schematic analogue worth copying, because the schematic has no equivalent of L3a's
per-cell geometry cache.

**R-L3b-1. Editing a sub-cell invalidates the cached instance geometry in every open layout that references
it.** R-L3a-3 caches a sub-cell's per-layer paths so a 50×50 array costs one build. That cache is now stale
the moment the sub-cell changes, and a stale one renders the *old* geometry — silently, and convincingly.

Use the seam shape that already exists twice in this codebase: `TechnologyCache`'s explicit
invalidate-and-notify (L0c) and the live-tech push (the L1-fix brief). **Explicit invalidation, no
`FileSystemWatcher`** — that has been ruled out deliberately twice and the reasoning is unchanged.

Invalidate on **both** paths, because they are genuinely different:
- **In-session edit** — a sub-cell edited in another open document, or in place via push-in. Fires on the
  edit, not on save, so the parent updates live.
- **On-disk change** — the sub-cell's file is saved by another surface, or resolved for the first time.

Also invalidate the **L2b spatial index** entries for affected instances: a sub-cell whose extent grew or
shrank changes its instances' bboxes, and R-L3a-4 already noted that `EnsureFresh` depends on resolution
state as well as shape count. A stale bbox means an instance that culls wrongly or cannot be clicked.

## 3. Saving

Mirror the schematic's `HierarchySaveTests` behaviour exactly.

- Saving while pushed in saves the **sub-cell's** file, not the parent's.
- Dirty state belongs to the cell being edited; a dirty sub-cell must block a silent close of the *parent*
  document too, since closing the parent is how the user would lose it.
- The project tree's dirty dot and Save-All sweep must see a sub-cell dirtied through push-in.
- Popping out of a dirty sub-cell does **not** silently discard — follow the schematic's prompt behaviour.

## 4. `CellUsageScanner` — the gap L3a left open

L3a explicitly deferred this: Remove Cell and Rename Cell do not currently see `.clay` instance references,
so a cell can be removed while layouts still instantiate it, or renamed leaving dangling `CellRef`s.

**Check this first — it may be nearly free.** `src/Ui/CLAUDE.md` records that
`CellUsageScanner.RewriteCellReferences` matches the last path segment of `"CellRef"` (PascalCase), and
`LayoutInstance.CellRef` serializes under exactly that name. **If the matching is generic rather than
`.csch`-specific, adding `.clay` to the scanned extensions may be the entire change.** Verify before writing
anything new; if a real difference exists, say what it is.

Then:
- **Remove Cell** counts `.clay` instance references alongside `.csch` ones and warns with the count and the
  referencing cells, matching existing behaviour.
- **Rename Cell** rewrites `.clay` `CellRef`s.
- Both must handle a `.clay` open in an editor: rewrite the in-memory document too, or force-close it —
  whichever the schematic already does.

## 5. Scope guardrails

- No flatten, no group-into-cell (L3c).
- No changes to instance rendering, arrays, or the instance cache **mechanism** — only its invalidation.
- No net propagation across hierarchy, no LVS, no schematic-to-layout (L5).
- No new navigation model: if the layout needs something the schematic's nav frames cannot express, stop and
  report rather than inventing a parallel system.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 6. Gate (acceptance)

Full unfiltered suite for gate 1; the two-command filtered loop above for iteration.

1. Builds green (`TreatWarningsAsErrors=true`); full `dotnet test` green; no existing test regresses —
   including the schematic's hierarchy-navigation and `HierarchySaveTests`.
2. **Push in / pop out** — double-clicking an instance enters its sub-cell; the breadcrumb shows the path;
   popping out restores the parent's **viewport and selection**.
3. **Nested depth** — a three-level push works, and each pop returns to the correct level with its own
   viewport.
4. **Unresolvable instance** — push-in disabled with a reason.
5. **Parent refresh (R-L3b-1)** — with a parent open showing a 50×50 array, edit the sub-cell and assert the
   parent's rendering changes **without** a reload. Off-screen pixel comparison before and after. This is the
   headline test: a stale cache renders the old geometry convincingly, so nothing else will catch it.
6. **Bbox and index refresh** — grow a sub-cell's extent and assert its instances' index bboxes update: the
   instance is hit-testable over its new extent and culls correctly at its new size.
7. **Save while pushed in** writes the sub-cell's file, not the parent's; the parent is unmodified on disk.
8. **Dirty propagation** — a sub-cell dirtied via push-in blocks a silent close of the parent, appears in
   Save All, and shows the tree dirty dot.
9. **Remove Cell** counts `.clay` references and warns; **Rename Cell** rewrites them, verified by reopening
   the referencing layout and confirming the instance still resolves.
10. **Rename with the layout open** leaves the open document consistent — no dangling `CellRef` in memory.

## 7. On completion

1. Add a "Phase L3b — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out: which schematic navigation
   pieces were reused versus newly written; **R-L3b-1**, its two invalidation paths, and that it covers the
   L2b index bboxes as well as the path cache; the save and dirty-propagation semantics; and **what
   `CellUsageScanner` actually needed** — if adding `.clay` to the extension list was the whole fix, say so,
   because that is useful to know for L4's importers.
2. Report back before L3c (flatten one level / all levels, group-into-cell) is briefed.

# Brief hier4 — Hierarchy breadcrumb bar

**For:** Claude Code (Sonnet) · **Phase:** 6i hierarchy navigation, step 4 of 4
**Design authority:** `docs/design/schematic-hierarchy-navigation.md` (§2.3, §6). Read it first.
**Prereq:** hier1 + hier2 + hier3 landed and green.

## Goal
Show the current hierarchy path as a clickable **breadcrumb bar** in the schematic view, e.g.
`TB ▸ X1(AmpStage) ▸ X3(Bias)`. Clicking a crumb pops directly to that level; a **Pop to Top** affordance
pops all the way to base. Visible only when the active document's nav depth > 0.

## Scope (do exactly this)

### A. Data the breadcrumb binds to
hier2 added `SchematicDocument.NavFrames` (read-only list of `(Session, Label)`) and
`ActiveViewModelChanged`. The breadcrumb needs, per crumb: a **display text** and the **frame index** to
pop to. Suggested crumb text:
- Frame 0 (base): the base cell/tab name.
- Frame k>0: `"{Label}"` where Label is the instance designator pushed through (e.g. `X1`); optionally
  `"{Label} · {cellName}"` if the session's cell name is readily available (derive from its `.csch` path,
  two levels up = cell dir name). Keep it short.

If binding directly to `NavFrames` is awkward (records/tuples bind poorly in Avalonia), add a small
**`IReadOnlyList<BreadcrumbItem> Breadcrumbs`** projection on `SchematicDocument` (or a tiny VM) where
`BreadcrumbItem { int FrameIndex; string Text; bool IsCurrent; }`, rebuilt on each push/pop/popTo and
surfaced via `PropertyChanged`. The current (last) crumb has `IsCurrent = true` (inert, emphasized).

### B. View (`src/Ui/Views/Content/SchematicView.axaml` + `.axaml.cs`)
1. Add a thin **breadcrumb `Border`** docked `Top` **below the toolbar** (a second `DockPanel.Dock="Top"`
   inside the existing `DockPanel`, above the canvas `Grid`). `IsVisible` bound to "nav depth > 0"
   (expose `SchematicDocument.CanPopOut` or a `HasHierarchy` bool; reuse `CanPopOut`).
2. Inside, an `ItemsControl` with a horizontal `StackPanel` panel, items = `Breadcrumbs`. Each item:
   - a `Button` (link-styled: flat, no border, accent foreground) showing `Text`, `Command`/click → pop to
     `FrameIndex`; disabled when `IsCurrent`.
   - a separator glyph `▸` between items (e.g. a `TextBlock` in the template, hidden for the last item, or
     a separate run — keep it simple; a separator before every non-first crumb is fine).
   - The current crumb rendered emphasized (bold) and non-interactive.
3. Add a **Pop to Top** control at the left (a small home/up icon button, `Kind="Home"` or
   `"ArrowCollapseUp"`, `ToolTip.Tip="Pop to Top"`), visible with the bar, → pop to frame 0. (Equivalent
   to clicking the base crumb; include both for discoverability.)
4. **Click handling:** crumb click → call the workspace hierarchy service `PopToLevel(doc, frameIndex)`
   (hier3 §A.5) via the injected `IHierarchyHost` on the `SchematicDocument` (same reference hier3 used).
   Pop to Top → `PopToLevel(doc, 0)`.
5. Rebuild/refresh the bar on `ActiveViewModelChanged` (subscribe in the same place hier2's rebind
   subscribes) and when DataContext changes.

### C. Styling
- Match the toolbar's visual weight (same `Border` border-brush/thickness conventions; small padding).
- Link buttons: reuse a flat/transparent button style; accent foreground for clickable crumbs, default
  foreground bold for the current crumb. No RGB literals — use `DynamicResource` theme brushes
  (`SystemAccentColor`, base foreground) consistent with the rest of the view.
- Keep it single-line; **overflow handling for very deep paths is deferred** (design §8) — a simple
  left-to-right row is fine for v1.

## Constraints / rules
- The breadcrumb only **navigates** (pops) — it never creates frames. Pushing is hier3.
- No new tab/window. Popping uses the hier3 service so session retirement (clean+unreferenced) happens.
- Visible only at depth > 0; at base, the bar is collapsed (`IsVisible=false`) so a flat schematic looks
  exactly as today.
- Firewall unaffected.

## Tests (add; keep green)
- If you added the `Breadcrumbs` projection on `SchematicDocument`, test it headless: base → empty/0
  crumbs (or one inert crumb), after `PushIn` ×2 → 3 crumbs with correct `Text`/`FrameIndex`/`IsCurrent`;
  clicking (calling `PopToLevel`) crumb index 1 leaves 2 crumbs.
- Full suite green; report count.

## Done when
- A breadcrumb bar appears when pushed in, shows the full path, pops to any level on click, has a Pop to
  Top control, and disappears at base.
- Full suite green; report the number.

## Notes
- This is the last hierarchy-navigation brief. After it: hierarchy **navigation** is complete. Hierarchical
  **net extraction** (simulating across cell boundaries) remains a separate future phase — see
  `docs/design/schematic-hierarchy-navigation.md` §4 for the resolution rule it must follow.

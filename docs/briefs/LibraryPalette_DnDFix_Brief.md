# Library Palette — Fix v2: DnD root cause (Button eats the drag) + real grid tightening (Claude Code / Sonnet)

Two items still broken. **Root causes now identified from the code — this is NOT another instrument-first
round; the diagnosis is below, implement the fix.** **(2)** Drag-and-drop dead because the tile glyph is
wrapped in a **`Button`**, which captures the pointer and consumes the press→move gesture before the tile's
drag handler can start `DoDragDropAsync` — so no drag begins, no DragOver fires, no ghost, no drop. **(3)** The
grid still looks identical because the spacing change was a **1px margin tweak** (invisible) and the real
width is driven by the fixed `Width="68"` wrapper around a 60px button; the border may also reference a
non-existent brush. Sub-gated; report between layers. Firewall green.

> Context code: `src/Ui/Controls/PaletteTile.axaml` (the glyph is inside `<Button Command="{Binding
> ArmCommand}">` — **this Button is the DnD blocker**; `StackPanel Width="68"`, `Margin="2 3 2 3"`; border
> uses `DockBorderSubtleBrush` — verify it resolves), `src/Ui/Controls/PaletteTile.axaml.cs` (drag handlers on
> the outer UserControl — `OnTilePointerPressed/Moved/Released` + `DoDragDropAsync`; the `Console.Error` logs
> Sonnet added — **remove them**), `src/Ui/ViewModels/Dock/PaletteTool.cs` (`PaletteTileVm.ArmCommand` →
> `PlacementService.Toggle`), `src/Ui/Controls/SchematicCanvas.cs` (`OnPaletteDragOver`/`OnPaletteDrop` —
> already correct + sets the ghost; **remove its `Console.Error` logs** once DnD works),
> `src/Ui/Views/Palette/PaletteToolView.axaml` (the `WrapPanel`). Design: `library-palette.md` §3 (tile) / §7
> (DnD).

## Root-cause diagnosis (confirmed from code — implement, don't re-instrument)
- **DnD:** the tile's clickable element is a **`Button`** (for click-to-arm via `ArmCommand`). Avalonia's
  `Button` **captures the pointer on press** and handles pointer-move/release for its click; the tile's
  drag-source handlers live on the **outer UserControl**, so the press is owned by the Button and the
  outer `PointerMoved` never sees an owned press→threshold gesture → `DoDragDropAsync` is never called → the
  drag never starts → canvas `DragOver`/`Drop` never fire (which is why **no ghost appears anywhere**).
  **Click-to-arm (Button) and drag-source (UserControl) are fighting over the same pointer; the Button wins.**
- **Grid:** `Margin="2 3 2 3"` was a 1px change (imperceptible). The slot width is dominated by the fixed
  `Width="68"` StackPanel wrapping a `Width="60"` button (8px dead) + WrapPanel default item spacing. And the
  per-tile border brush `DockBorderSubtleBrush` may not exist in the resource set → border silently absent.

## The spine
- **Make the tile ONE pointer owner** — the tile is *both* the arm target and the drag source. Remove the
  `Button`; use a plain `Border`/`Panel` root with manual pointer handling: press+release-without-drag =
  **arm toggle** (call `ArmCommand`/`PlacementService.Toggle`); press+move-past-threshold =
  **`DoDragDropAsync`**. No nested Button to capture the pointer.
- **Real tightening** — make the slot meaningfully smaller (drop/reduce the fixed 68→~60–62; shrink margins to
  a visible degree) and use a **resolvable** border brush so the definition border actually renders.
- **Remove all `Console.Error` DnD instrumentation** once it works.
- **Scope fence:** the tile control (arm+drag unification) + grid spacing/border. No other changes.

---

## LAYER 1 — fix DnD: unify arm + drag on one pointer owner (no Button)

1. **Replace the `Button`** in `PaletteTile.axaml` with a non-capturing root — a `Border` (themed, with the
   armed/unarmed visual states as style classes on the Border, mirroring the current armed accent) containing
   the `PaletteGlyphControl`. Keep the caption below.
2. **Manual pointer handling** in `PaletteTile.axaml.cs` on the tile root:
   - **PointerPressed:** record press position + pointer; do **not** capture (capturing can also interfere
     with `DoDragDropAsync`); set a `_dragArmed` candidate.
   - **PointerMoved (pressed, past `DragThreshold`):** call `DragDrop.DoDragDropAsync(...)` with the
     `PaletteDragPayload` (as today). Mark that a drag happened so the release doesn't also arm.
   - **PointerReleased (no drag occurred):** treat as a **click → arm toggle** (call the VM's `ArmCommand` or
     `PlacementService.Toggle(kind, portCount)` directly). This replaces the Button's Command.
   - Maintain the `IsArmed` visual on the Border via the existing `Classes.armed` binding.
3. **Remove** the `Console.Error` logs in `PaletteTile.axaml.cs`.
4. Verify the canvas side already works (it does — `OnPaletteDragOver` sets the ghost + `OnPaletteDrop` calls
   `CommitPlacement`); **remove** its `Console.Error` logs once the drag reaches it.

**Layer 1 gate:** clicking a tile arms it (toggle, depressed visual) — click-to-arm still works without the
Button; **dragging a tile to a schematic now starts a drag**, shows the ghost (with pins, from the prior
fix) following the cursor, and dropping creates a connected component at the drop point (undoable). Foreign
drags ignored. Instrumentation removed. Report.

---

## LAYER 2 — real grid tightening + a resolvable definition border

1. **Shrink the slot meaningfully:** reduce/remove the fixed `Width="68"` (e.g. size to content or ~60–62) and
   reduce the tile margins to a **visibly** tighter value; adjust the button/glyph so the tile reads compact
   (e.g. 56–60px cell). The width-driven column rule still holds — just with a smaller per-slot footprint, so
   more columns fit and horizontal gaps shrink visibly.
2. **Definition border that renders:** use a resource that **exists** in the app's theme set for the subtle
   border — e.g. `SystemBaseLowColor`/`SystemChromeMediumColor` (verify against the app resources) instead of
   `DockBorderSubtleBrush` if that key doesn't resolve. A 1px low-contrast border (optionally a faint
   background) so each tile reads as a defined cell; the armed accent still overrides it.
3. **Verify visually** at the default dock width and widened — the grid should look clearly tighter and each
   tile clearly bordered (a real, noticeable change, not 1px).

**Layer 2 gate:** the grid is **noticeably** tighter (smaller slots, less horizontal gap, more columns at the
same width) and each tile has a **visible** subtle border; armed state still reads via the accent. Report
(screenshot description with the before/after sense of density).

## Acceptance
1. DnD works: the tile (no longer a Button) is one pointer owner — click arms, drag starts `DoDragDropAsync`,
   the canvas shows the ghost + drops a connected component; click-to-arm unaffected; instrumentation removed.
2. The grid is visibly tighter with a rendering subtle per-tile border (resolvable brush); armed accent intact.
3. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **One pointer owner per tile** — remove the nested `Button`; manual press→arm / press-move→drag; don't
  capture the pointer in a way that blocks `DoDragDropAsync`.
- **Click-to-arm must still work** (now via PointerReleased-without-drag → Toggle).
- **Make the grid change visible** — not a 1px tweak; shrink the fixed width + margins; **use a brush that
  resolves** for the border.
- **Remove all `Console.Error` DnD logs** (tile + canvas).
- **Scope fence:** tile arm+drag unification + grid spacing/border only.
- Sub-gate the two layers; report between each.
- Update `library-palette.md` + `src/Ui/CLAUDE.md` (the tile is a single pointer owner doing both arm + drag;
  the Button-eats-drag gotcha; final tile metrics).

*Exit: drag-and-drop places components (the Button-vs-drag pointer conflict resolved), and the tile grid is
genuinely tighter and well-defined — the Palette is finished.*

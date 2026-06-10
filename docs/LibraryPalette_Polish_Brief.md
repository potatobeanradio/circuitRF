# Library Palette — Polish & Bugfixes: ghost pins, DnD broken, grid tightness (Claude Code / Sonnet)

Three items from testing: **(1)** the placement ghost should include the component's **pins** (both arm-ghost
and DnD); **(2)** **drag-and-drop is broken** — no ghost during drag, drop creates no instance; **(3)** the
tile grid should be **visually tighter** (horizontal spacing) with a **subtle border** per tile for definition
(HIG). Sub-gated; **DnD is instrument-first**; report between layers. Firewall green.

> Context code: `src/Ui/Renderers/SchematicRenderer.cs` — the placement-ghost render in `DrawOverlay`
> (`overlay.Ghost is { } ghost` → `DrawSymbol(..., ghostPaint)`; **only draws SymbolLine primitives, no port
> markers** — that's issue 1); `DrawPortMarkers` (how real components draw pin squares — the pattern to mirror
> for the ghost); `SchematicOverlay.Ghost` (carries `Symbol`/`X`/`Y`/`Rotation`/`MirrorX`),
> `src/Ui/Schematic/SchematicOverlay.cs` (the Ghost record — confirm fields; may need PortCount). DnD:
> `src/Ui/Controls/PaletteTile.axaml.cs` (drag source — `DoDragDropAsync` + `DataTransfer` +
> `PaletteDragPayload.Format`), `src/Ui/Controls/SchematicCanvas.cs` (`OnPaletteDragOver`/`OnPaletteDrop` +
> `DragDrop.SetAllowDrop(this, true)` + `AddHandler(DragDrop.DropEvent, …)`; drop reads
> `e.DataTransfer.Items` / `TryGetRaw(PaletteDragPayload.Format)` → `CommitPlacement`),
> `src/Ui/Schematic/PaletteDragPayload.cs` (`DataFormat.CreateInProcessFormat`). Tile spacing:
> `src/Ui/Controls/PaletteTile.axaml` (StackPanel `Width="68" Margin="3"`, 60×60 button), the WrapPanel in
> `src/Ui/Views/Palette/PaletteToolView.axaml`. `SymbolPortDefs.For(symbol, portCount)` → local pin coords for
> the ghost pins. Design: `library-palette.md` §3 (tile) / §6 (ghost).

## The spine
- **Ghost shows pins** (issue 1) — both the arm-ghost and the DnD path render the component **with its port
  markers**, so the user sees where pins will land. Mirror `DrawPortMarkers`' pin squares in ghost color.
  *(Tiles stay glyph-only — this is the schematic ghost, not the palette tile.)*
- **DnD must work** (issue 2) — instrument first (DnD plumbing is framework-internal): confirm whether the
  drag **starts**, whether `DragOver`/`Drop` **fire** on the canvas, whether the **payload round-trips**
  (`TryGetRaw` returns non-null), and whether `CommitPlacement` is reached. Then fix per the finding.
- **Tighter grid + subtle border** (issue 3) — reduce horizontal inter-tile spacing; add a subtle per-tile
  border (HIG definition) that doesn't fight the armed-state highlight.
- **Scope fence:** these three. No new Palette features.

---

## LAYER 1 — ghost includes pins (arm + DnD share one ghost render)

1. In `SchematicRenderer.DrawOverlay`, after the ghost `DrawSymbol(...)`, **also draw the ghost's port
   markers**: for the ghost's `Symbol` (+ port count), get the local pin coords (`SymbolPortDefs.For`) and draw
   a small square at each (via `LocalToPixel` with the ghost's X/Y/Rotation/MirrorX), in **ghost color**
   (`theme.GhostBody`, matching the dashed body). Mirror `DrawPortMarkers`' geometry (PortBoxHalf), but use the
   ghost paint, not the connected/unconnected paints (the ghost isn't placed yet).
2. If `SchematicOverlay.Ghost` lacks the port count needed for variadic symbols (ZPort/Sdd), add it to the
   Ghost record + populate where the ghost is set (from `PendingPlacement.PortCount`). For fixed-pin built-ins
   `SymbolPortDefs.For(symbol, 0)` suffices.
3. Because both the arm-ghost and the DnD ghost (once issue 2 is fixed) use the **same** `overlay.Ghost` render
   path, pins appear for both automatically.

**Layer 1 gate:** arming a component shows the dashed ghost **with pin squares** at each terminal; rotating the
ghost moves the pins correctly; a 3-terminal device (FET) shows all three pins. Report.

---

## LAYER 2 — INSTRUMENT then FIX drag-and-drop (issue 2)

The drop wiring looks correct (SetAllowDrop + DragOver/Drop handlers + format check + CommitPlacement), so
**diagnose at runtime before changing code**:
1. **Instrument** (log/report, no fix yet):
   - **Does the drag start?** Log in `PaletteTile.OnTilePointerMoved` — is the threshold crossed, is
     `DoDragDropAsync` reached, does it return an effect?
   - **Do canvas events fire?** Log in `OnPaletteDragOver` / `OnPaletteDrop` — are they called at all? Is
     `e.DataTransfer.Formats` containing `PaletteDragPayload.Format`?
   - **Does the payload round-trip?** In `OnPaletteDrop`, does `TryGetRaw(PaletteDragPayload.Format)` return
     non-null? Is `_editContext` non-null at drop time?
   - **Is `CommitPlacement` reached** with sensible world coords?
   **Report findings.**
2. **Likely candidates** (confirm against instrumentation — don't patch blind):
   - **In-process format mismatch:** `DataFormat.CreateInProcessFormat<PaletteDragPayload>` creates a *new*
     format identity per call if not a shared static — confirm both sides reference the **same**
     `PaletteDragPayload.Format` static (they appear to, but verify the format equality at runtime).
   - **`DoDragDropAsync` not awaited / pointer not captured:** the drag may abort immediately if the press
     args are stale or the pointer isn't the drag pointer.
   - **Drop target not actually hit:** the canvas draws via `ICustomDrawOperation`; confirm the control
     receives drag events (it's `Focusable`, `SetAllowDrop` true) — a transparent/hit-test gap could mean
     DragOver never fires. Check `IsHitTestVisible`/background.
   - **`e.DataTransfer.Items` vs `TryGetRaw`:** confirm the read API matches what `DoDragDropAsync` populated
     (the newer `DataTransfer` API pairing).
3. **Fix** per the finding. Also: during a successful drag-over, **set `overlay.Ghost`** at the drag-over
   world position so the ghost (now with pins, L1) follows during DnD too (the design wants a ghost; "no ghost
   during drag" is part of the report). Clear it on drop/leave.

**Layer 2 gate:** dragging a tile onto a schematic shows the ghost (with pins) following during drag; dropping
creates a connected, auto-named component at the drop point (undoable); the instrumentation findings are
reported; foreign drags still ignored. Report.

---

## LAYER 3 — tighter grid + subtle per-tile border (issue 3, HIG)

1. **Tighten horizontal spacing:** reduce the tile's outer `Margin` (currently `3` all-round on the 68-wide
   StackPanel) — e.g. tighter horizontal margin so tiles sit closer; keep enough vertical rhythm for the
   caption. Adjust the WrapPanel/tile slot so columns pack tighter (the width-driven column rule still holds,
   just with a smaller per-slot footprint).
2. **Subtle border:** add a subtle border around each tile's glyph button (a 1px low-contrast border, e.g.
   `SystemBaseLowColor` / a chrome divider brush) for visual definition (HIG) — must **not** fight the armed
   highlight (when armed, the accent background/​border takes over; when not armed, the subtle border shows).
   Consider a faint rounded border + very subtle background so tiles read as distinct cells.
3. Keep the caption legible; verify the grid looks tight and defined at the default ~2-column dock width and
   when widened.

**Layer 3 gate:** the tile grid is visually tighter (less horizontal gap) with a subtle border giving each
component definition; armed state still reads clearly (accent overrides the subtle border); looks clean at
dock width + widened. Report (screenshot description).

## Acceptance
1. The placement ghost (arm **and** DnD) renders the component **with pin markers** in ghost color; rotation
   moves pins correctly; variadic devices show all pins.
2. Drag-and-drop works: ghost follows during drag, drop creates a connected auto-named component (undoable);
   root cause diagnosed via instrumentation and reported; foreign drags ignored.
3. The tile grid is tighter horizontally with a subtle per-tile border (HIG), armed state still clear.
4. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **Ghost pins reuse the geometry source** (`SymbolPortDefs.For` + `LocalToPixel`), in ghost color; mirror
  `DrawPortMarkers`, don't re-derive pin positions.
- **DnD: instrument before fixing** — confirm drag-start / event-firing / payload round-trip / CommitPlacement
  reached; report, then fix per finding; add the drag-over ghost.
- **Tiles stay glyph-only** (the pins are on the schematic ghost, not the palette tile).
- **Tighter spacing keeps the width-driven column rule**; the border must not fight the armed highlight.
- **Scope fence:** ghost pins + DnD fix + grid polish only.
- Sub-gate the three layers; report and stop between each.
- Update `library-palette.md` + `src/Ui/CLAUDE.md` (ghost shows pins; DnD root cause + fix; tile spacing/border).

*Exit: the placement ghost shows pins (arm + DnD), drag-and-drop places components with a following ghost, and
the tile grid is tight and well-defined — the Palette feels finished.*

# Library Palette — Step 5: system drag-and-drop (Claude Code / Sonnet)

The second placement path: **drag a tile onto a schematic and drop to place** — converging on the **same
commit** as the click-arm path (step 4). **This brief is step 5.** Docs are step 6. Read `library-palette.md`
§7 first. Sub-gated; report between layers. Firewall green.

> Read first: `docs/design/library-palette.md` §7 (system DnD — palette drop-type, schematic accepts it, DnD
> and click-arm converge on one commit; rotation during raw OS drag uses last-used rotation). Context code:
> `src/Ui/Schematic/PlacementService.cs` (armed state — `Pending`, `Toggle`, `Disarm`, `Rotate`),
> `src/Ui/Schematic/PendingPlacement.cs` (`{Kind, PortCount, Rotation}`), `src/Ui/ViewModels/
> SchematicViewModel.cs` (step-4 **commit-at-snapped-position** logic — the place+connect+autoname+defaults
> path; reuse it for the drop), `src/Ui/Controls/SchematicCanvas.cs` (pointer + where to add Avalonia
> `DragDrop` handlers — `DragDrop.DropEvent`/`DragOver`), `src/Ui/Controls/PaletteTile.axaml(.cs)` (the tile —
> add the drag *source*), `src/Ui/Schematic/ComponentTypeRegistry.cs` (`InstancePrefix`/`DefaultParameters`).
> Design docs win on any conflict.

## The spine (do not violate)
- **DnD and click-arm converge on ONE commit** (§7) — the drop calls the **same** step-4 place+connect routine
  (auto-name + defaults + on-`P` connectivity union). If step 4's commit is inline in pointer handling,
  **extract it into a reusable method** (e.g. `SchematicViewModel.CommitPlacement(kind, portCount, rotation,
  worldX, worldY)`) and have **both** the click path and the drop call it. Do NOT duplicate the commit.
- **Palette drop-type** — drag carries a circuitRF-specific payload (the catalog item: `SymbolKind` +
  `PortCount`); the schematic canvas **registers** this drop-type and ignores foreign drags.
- **Drop commits at the drop point** (grid-snapped); the OS drag image is the "ghost" (no live ghost-follow
  needed during a raw drag). **Rotation = last-used** (`PlacementService.Pending?.Rotation` or a remembered
  default) since raw OS drag can't rotate mid-drag (§7).
- **Reuse the on-`P` connectivity union** on commit (same as step 4) — pins landing on wires connect.
- **Don't disturb click-arm** — DnD is additive; the step-4 arm/ghost/rotate/stay-armed path stays.
- **Scope fence (step 5):** the drag source + the drop target + converging on the shared commit. No new
  placement semantics, no docs (step 6).

---

## LAYER 1 — extract the shared commit + the drop target

1. **Extract** step-4's commit into a reusable `SchematicViewModel.CommitPlacement(SymbolKind kind, int
   portCount, SymbolRotation rotation, double worldX, double worldY)` (if not already a clean method): places
   the `EditableComponent` (auto-name + defaults), runs the on-`P` connect, one undoable command. The
   click-arm path now calls this too (no behavior change — verify step-4 click still works identically).
2. **Drop target:** `SchematicCanvas` handles Avalonia `DragOver` (accept only the palette drop-type; show a
   copy cursor) and `Drop` (read the payload → `CommitPlacement` at the snapped drop world-position, with
   last-used rotation). Foreign drag payloads are ignored.

**Layer 1 gate:** `CommitPlacement` is the single shared commit (click-arm still works via it); the canvas
accepts the palette drop-type and ignores others (headless/manual check of the handler wiring). Report.

---

## LAYER 2 — the drag source on the tile + end-to-end drop

1. **Drag source:** `PaletteTile` initiates an Avalonia `DragDrop.DoDragDrop` on drag-start, carrying the
   palette payload (`SymbolKind` + `PortCount`) under the registered drop-type; the OS drag image is the tile
   glyph (or a simple representation).
2. **End-to-end:** dragging a tile onto a schematic and dropping places a connected component at the drop
   point (snapped), auto-named with defaults, undoable — via the shared `CommitPlacement`. Drop works on any
   schematic (active tab or torn-off). Rotation = last-used.
3. Click-arm and DnD coexist: arming still ghost-follows + rotates + stays-armed; DnD is the one-shot
   alternative.

**Layer 2 gate:** drag R from the Palette → drop on a schematic → a connected, auto-named R appears at the
drop point (undoable); dropping where a pin meets a wire connects; click-arm placement still works unchanged;
foreign drags are ignored. Report.

## Acceptance (step 5)
1. A shared `CommitPlacement` backs both click-arm (step 4) and drop; no duplicated commit logic.
2. `PaletteTile` is a drag source (palette drop-type payload); `SchematicCanvas` accepts the drop and commits
   at the snapped drop point with last-used rotation, connecting via the on-`P` union; foreign drags ignored.
3. DnD and click-arm coexist; drop works on tabbed + torn-off schematics.
4. `dotnet build`/`dotnet test` green; firewall green; **no docs yet** (step 6); nothing else regresses.

## Guardrails
- **One commit** — extract + share `CommitPlacement`; DnD and click-arm both call it; reuse the on-`P` union.
- **Palette drop-type** — schematic accepts only it; ignore foreign drags.
- **Last-used rotation** for drop (raw OS drag can't rotate mid-drag).
- **Additive** — don't disturb step-4 click-arm.
- **Scope fence:** drag source + drop target + shared commit only.
- Sub-gate the two layers; report between each.
- Update `library-palette.md` §10 status (step 5 done) and `src/Ui/CLAUDE.md` (DnD converges on
  `CommitPlacement`; palette drop-type).

*Exit: components can be dragged from the Palette and dropped onto any schematic, converging on the same
place+connect commit as click-arm — both placement paths complete; only docs (step 6) remain.*

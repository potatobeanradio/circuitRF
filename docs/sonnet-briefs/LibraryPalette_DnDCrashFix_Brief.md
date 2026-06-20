# Library Palette — DnD crash fix (macOS pasteboard) + visible tile border (Claude Code / Sonnet)

Two precise fixes, **root causes confirmed from the crash trace + code — implement directly.** **(1)** DnD
**crashes on macOS** because the payload uses an **in-process DataFormat**, which puts nothing on the native
NSPasteboard — AppKit then throws `'There are 0 items on the pasteboard, but 1 drag images'`. Fix: carry the
payload as a **text pasteboard format** (mirror `SchematicClipboard`, which uses `DataFormat.Text` and works
cross-platform). **(2)** The tile border is invisible because `SystemBaseLowColor` (~12% opacity) is too faint
against the panel background — use a more visible subtle brush. Sub-gated; report between layers. Firewall
green.

> Root cause 1 (crash) — from the macOS crash trace: `NSDraggingSession … 0 items on the pasteboard, but 1
> drag images`. `PaletteDragPayload.Format = DataFormat.CreateInProcessFormat<PaletteDragPayload>(...)` is an
> **in-process-only** format; on a real macOS system drag the managed object is never written to the
> NSPasteboard, so AppKit has a drag image but no pasteboard item → uncaught NSException → app terminates.
> The established working pattern in this codebase is `src/Ui/Clipboard/SchematicClipboard.cs`, which carries
> its primary payload as **`DataFormat.Text`** (a JSON string) — that lands on the native pasteboard and
> round-trips on every platform.
>
> Context code: `src/Ui/Schematic/PaletteDragPayload.cs` (the in-process format — **replace**),
> `src/Ui/Controls/PaletteTile.axaml.cs` (`OnTilePointerMoved` builds the `DataTransferItem` — set a **text**
> format instead), `src/Ui/Controls/SchematicCanvas.cs` (`OnPaletteDragOver`/`OnPaletteDrop` read the format —
> read **text**, parse, reject non-matching), `src/Ui/Controls/PaletteTile.axaml` (border `Style
> Selector="Border#TileGlyph"` uses `SystemBaseLowColor` — too faint).

## The spine
- **Carry the palette payload as text** (`DataFormat.Text`), serialized as a compact string the drop side
  parses — exactly how `SchematicClipboard` carries JSON. No in-process format (it crashes macOS DnD).
- **Distinguish palette drags from foreign text:** prefix/format the string so the canvas only accepts its own
  (e.g. `"circuitrf-palette:<kind>:<portCount>"`); foreign text drops are ignored.
- **Visible border:** use a brush with enough contrast to actually see (e.g. `SystemBaseMediumLowColor` or a
  fixed subtle gray), not `SystemBaseLowColor`.
- **Scope fence:** the DnD format + the border brush. No other changes. (The L1 single-pointer-owner tile and
  the grid tightening from the prior round are good — keep them.)

---

## LAYER 1 — fix the macOS DnD crash: text-format payload

1. **`PaletteDragPayload`:** remove the `CreateInProcessFormat` format. Add a compact **string serialization**:
   `string Serialize()` → e.g. `"circuitrf-palette:{Kind}:{PortCount}"`, and `static bool TryParse(string,
   out PaletteDragPayload)` that accepts only strings with the `circuitrf-palette:` prefix and parses
   kind+portCount (reject anything else). (`SymbolKind` parses via `Enum.TryParse`.)
2. **Drag source** (`PaletteTile.OnTilePointerMoved`): instead of `transferItem.Set(PaletteDragPayload.Format,
   obj)`, set **`DataFormat.Text`** to `payload.Serialize()`. Keep the rest (`DataTransfer`, `DoDragDropAsync`
   with `DragDropEffects.Copy`) unchanged.
3. **Drop target** (`SchematicCanvas`):
   - `OnPaletteDragOver`: accept when the transfer **has text** that `TryParse` succeeds on (read
     `DataFormat.Text` via the transfer's text accessor; parse; if it's a palette string → `Copy` + set the
     ghost; else `None`). (Avoid heavy parsing on every DragOver if needed — but correctness first.)
   - `OnPaletteDrop`: read the text, `TryParse`; if it's a palette payload → `CommitPlacement(kind, portCount,
     rotation, wx, wy)`; else ignore. Clear the ghost on drop/leave (already wired).
4. Confirm the read API matches how text is carried (mirror `SchematicClipboard`'s `TryGetTextAsync` / the
   `DataTransfer` text accessor used elsewhere). Foreign text drags (random text) must be ignored (the prefix
   guards this).

**Layer 1 gate:** on macOS, dragging a tile to a schematic **no longer crashes**; the ghost follows during
drag and dropping creates a connected component at the drop point (undoable); click-to-arm still works;
dragging foreign text onto the canvas is ignored (no crash, no placement). Report (incl. that the NSException
is gone).

---

## LAYER 2 — make the tile border visible

1. In `PaletteTile.axaml`, change the unarmed border brush from `SystemBaseLowColor` to a **visibly
   contrasting subtle** brush — try `SystemBaseMediumLowColor` (or a fixed subtle gray like `#33808080` if the
   theme resource is still too faint) so each tile reads as a defined cell against the `SystemChromeLowColor`
   panel. Keep it subtle (HIG) but **actually visible** at a glance.
2. Verify in both light and dark variants (the brush should read in both); the armed accent still overrides.

**Layer 2 gate:** each tile now shows a **visible** subtle border (light + dark); the grid reads as defined
cells; armed accent intact. Report (screenshot description confirming the border is now visible).

## Acceptance
1. macOS DnD no longer crashes: the payload travels as `DataFormat.Text` (palette-prefixed string), drop
   parses + `CommitPlacement`s, foreign text ignored; click-to-arm unaffected; ghost follows during drag.
2. The tile border is visibly rendered (subtle but seen) in light + dark; armed accent intact.
3. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **Text pasteboard format, not in-process** — the in-process format is what crashes macOS DnD; mirror
  `SchematicClipboard`'s `DataFormat.Text` approach.
- **Prefix-guard the payload** so foreign text drags are ignored (no crash, no stray placement).
- **Border brush must be visible** — `SystemBaseLowColor` is too faint; use a contrasting subtle brush, verify
  light + dark.
- Keep the prior fixes (single-pointer-owner tile, grid tightening).
- **Scope fence:** DnD format + border brush only.
- Sub-gate the two layers; report between each.
- Update `library-palette.md` + `src/Ui/CLAUDE.md` (palette DnD uses a text format — in-process formats crash
  macOS system DnD; the working pattern is `DataFormat.Text` like SchematicClipboard).

*Exit: drag-and-drop works on macOS without crashing (text-format payload), and the tile grid shows visible
per-tile borders — the Palette is finished.*

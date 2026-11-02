# circuitRF — Esc-key behavior fix (Schematic + Symbol editors) (Claude Code / Sonnet)

Two prior attempts failed because the fix was sought in the wrong place. **The Escape logic in the VM is
*structurally* right** (`if (HasActiveOperation) cancel→Select; else deselect;`) — the bug is that **canceling
placement never disarms the app-level `PlacementService`**, so an ARMed palette item stays armed after Esc.
The palette ARM state does NOT live in the schematic VM's tool enum — it lives in a separate app-level
`PlacementService.Pending` — so "fixing Esc" inside the VM's tool state leaves the real armed state untouched.
Plus two smaller gaps (inline-edit Esc doesn't return to Select; confirm DnD-cancel). Small, surgical,
**verify on disk + by runtime check**. Firewall green.

## Required behavior (the spec)
**Schematic & Symbol editors, identical rules:**
- **Esc cancels any in-progress action** — ARM ghost placement, drag-and-drop placement, live wiring, zoom-box,
  inline text editing (and in the symbol editor: drawing/pin ops) — **and then selects the Select tool.**
- **Esc deselects** all selected objects **iff** no action was in progress (idle in Select).

## Root causes (confirmed from code)
1. **ARM not disarmed (the main bug).** Palette ARM sets `PlacementService.Pending` (app-level). The canvas
   VM reflects it as `ActiveTool == Tool.Place`. On Esc, `SchematicViewModel.OnKeyDown` →
   `SetSelectTool()` sets `ActiveTool = Select` + `CancelCurrentOp()` but **never calls
   `_placementService.Disarm()`**. `Pending` stays non-null → the app is still armed, the palette tile stays
   highlighted, and other canvases/tabs remain armed. (`OnSvcPropertyChanged` only reacts to `Pending`
   *changes*, so a stale non-null `Pending` is never re-cleared.) `PlacementService.Disarm()` already exists.
2. **Inline-edit Esc doesn't return to Select.** `SchematicView.axaml.cs.OnInlineEditKeyDown` handles Esc by
   `Vm.CancelInlineEdit()` + `DismissInlineEditBox()` but does not set the Select tool (spec says it should).
3. **DnD-cancel unverified.** During palette drag-and-drop, the OS owns the drag; Esc is usually consumed by
   the platform to cancel the drag, which should fire `OnPaletteDragLeave` (clears the ghost). **Confirm**
   this at runtime; only add handling if the ghost survives a canceled drag.

## Spine
- **Disarm the placement service whenever placement is canceled.** The single correct place: when the
  schematic leaves `Tool.Place` due to Esc/cancel, call `_placementService?.Disarm()`. Do it so EVERY cancel
  path (Esc, tool switch, successful/aborted placement) goes through one method — no scattered Disarm calls.
- **After canceling any action, ActiveTool = Select** (both editors) — already true except the inline-edit
  path (fix #2).
- **Don't disturb** the working deselect-when-idle branch, the symbol editor's already-correct text/pin/draw
  Esc branches, or the placement-service↔tool sync (`OnSvcPropertyChanged`). Avoid a feedback loop: Disarm
  sets `Pending = null` → `OnSvcPropertyChanged(null)` calls `SetSelectTool()` again — make this idempotent
  (calling `SetSelectTool` when already in Select must be a cheap no-op, and Disarm-when-already-null a no-op).
- **Scope fence:** Esc semantics + placement disarm-on-cancel only. No drag/connectivity/other changes.

## LAYER 1 — schematic: disarm placement on cancel (the main fix)
1. Route placement cancel through the service. Simplest correct approach: in `SchematicViewModel`, make the
   placement-cancel path call `_placementService?.Disarm()`. Concretely — when Esc (or any cancel) takes the
   tool out of `Tool.Place`, disarm. Implement WITHOUT a feedback loop:
   - Add a guard so `Disarm()` → `OnSvcPropertyChanged(Pending=null)` → `SetSelectTool()` doesn't re-enter
     infinitely. `Disarm()` is already a no-op when `Pending` is null, and `SetSelectTool` setting
     `ActiveTool = Select` when already Select won't loop — verify the chain settles in one pass.
   - Ensure the Esc path that currently calls `SetSelectTool()` also disarms when it was in `Tool.Place`. You
     may either (a) have the Escape case call `_placementService?.Disarm()` before/after `SetSelectTool()`
     when `ActiveTool == Tool.Place`, or (b) call Disarm inside the place-exit logic. Pick the single-source
     option that also covers a plain tool-switch away from Place.
2. Confirm the ghost is cleared (`CancelCurrentOp` already nulls `Overlay.Ghost`).

**Layer 1 gate:** ARM a palette item → press Esc → (a) ghost gone, (b) tool = Select, (c) **palette tile no
longer highlighted / `PlacementService.Pending == null`**, (d) other tabs/canvases no longer armed. Report,
incl. confirming `Pending` is null after Esc.

## LAYER 2 — schematic: inline-edit Esc returns to Select; confirm the other actions
1. `SchematicView.axaml.cs.OnInlineEditKeyDown` Escape branch: after `Vm.CancelInlineEdit()` +
   `DismissInlineEditBox()`, also call `Vm.SetSelectTool()` so the tool is Select per spec.
2. Confirm (no code change unless broken) Esc cancels + returns to Select for: live wiring (`Tool.Wire`,
   partial `_wirePoints`), zoom-box (`Tool.ZoomBox`), rubber-band/drag/segment-drag (the `HasActiveOperation`
   flags). These already route through `SetSelectTool()`/deselect correctly — verify, don't churn.
3. Confirm idle-in-Select Esc still deselects (the `else Selection.Clear()` branch).

**Layer 2 gate:** Esc during inline edit cancels the edit AND selects the Select tool; Esc during
wiring/zoom-box/drag cancels and returns to Select; Esc when idle with a selection clears the selection.
Report the matrix.

## LAYER 3 — DnD-cancel verification (instrument, then fix only if needed)
1. Start a palette drag-and-drop; press Esc mid-drag. Observe: does the OS cancel the drag and does
   `OnPaletteDragLeave` fire (ghost cleared)? Report what happens.
2. **Only if** the ghost survives a canceled drag: clear it (null `Overlay.Ghost`) when the drag ends without
   a drop. No speculative handling otherwise.

**Layer 3 gate:** report DnD-cancel behavior; ghost is cleared after a canceled drag (already, or via the
minimal fix). Report.

## LAYER 4 — symbol editor: confirm parity (likely already correct)
`SymbolEditorViewModel.OnKeyDown` already: text-typing Esc → `CancelOp()` + `ActiveTool = Select`; pin Esc →
`ActiveTool = Select` (resets pin state via `OnActiveToolChanged`); general Esc → `if (hasActiveOp){ CancelOp();
ActiveTool = Select; } else ClearSelection();`. Verify each in-progress action (two-point draw, multi-point
draw, drag, rubber-band, text, pin) cancels to Select, and idle Esc clears selection. Fix only genuine gaps.

**Layer 4 gate:** symbol-editor Esc matrix verified (every action cancels→Select; idle clears selection).
Report.

## Acceptance
1. Schematic: Esc cancels ARM (and **disarms `PlacementService`** — tile un-highlights, all canvases
   disarm), DnD, wiring, zoom-box, inline edit, drag — each returning to Select; idle Esc deselects.
2. Symbol editor: same semantics (cancel any action → Select; idle → deselect).
3. No feedback loop between Disarm and the tool/service sync; no regression to placement, drag, wiring, or
   the palette arm/DnD flows.
4. `dotnet build`/`dotnet test` green; firewall green.

## Guardrails
- **The missing piece is `PlacementService.Disarm()` on cancel** — the VM tool state alone is not the armed
  state. Route every placement-cancel through one path that disarms.
- **No Disarm↔sync feedback loop** — Disarm-when-null and SetSelectTool-when-already-Select must be no-ops;
  confirm the chain settles in one pass.
- After canceling ANY action, ActiveTool = Select (fix the inline-edit path).
- Don't churn the already-correct branches (deselect-when-idle, symbol text/pin/draw Esc) — verify, minimal
  edits.
- **Verify on disk after editing, and confirm `Pending == null` post-Esc at runtime** (prior attempts
  reported "fixed" without the armed state actually clearing).
- **Scope fence:** Esc semantics + disarm-on-cancel only.
- Sub-gate the layers; report between each.
- Update `library-palette.md` (Esc disarms the app-level PlacementService) + `src/Ui/CLAUDE.md` (Esc contract
  for both editors; the ARM-lives-in-PlacementService gotcha).

*Exit: Esc cancels whatever the user is doing — including disarming an ARMed palette item app-wide — and
returns to Select; with nothing in progress, Esc clears the selection. Same in both editors.*

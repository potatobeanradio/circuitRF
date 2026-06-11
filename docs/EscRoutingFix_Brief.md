# circuitRF — Esc routing fix: instrument focus, then one authoritative handler (Claude Code / Sonnet)

**Simplest failing case:** click the Wire tool button, press Esc → Select tool is NOT activated. The VM logic
is correct (`if (HasActiveOperation) SetSelectTool()`), so this is a **keyboard focus / event-routing** bug:
after clicking a toolbar Button, the Esc KeyDown does not reach the handler that calls `SetSelectTool()` —
OR it reaches a handler that no-ops. Two prior fixes missed it because they edited the Esc *logic*, not the
*routing*. **This brief is INSTRUMENT-FIRST** — three failed rounds means we confirm at runtime which handler
sees the key (and whether `SetSelectTool` runs) before changing anything. Firewall green.

> Context (all read): `src/Ui/Views/Content/SchematicView.axaml(.cs)` — toolbar Buttons (`OnWireTool` does
> `Vm.SetWireTool(); SchematicCanvasCtrl.Focus();`), the View's `protected override OnKeyDown` (claims "works
> regardless of which toolbar item is focused" — it does NOT), the `InlineEditBox`. `src/Ui/Controls/
> SchematicCanvas.cs` — `Focusable=true`, instance `KeyDown += OnKeyDown` → `_editContext.OnKeyDown(...)`.
> `src/Ui/ViewModels/SchematicViewModel.cs` — `OnKeyDown` (Esc → `HasActiveOperation?SetSelectTool():
> Selection.Clear()`), `SetSelectTool` (`ActiveTool=Select; CancelCurrentOp();`).

## The likely root cause (confirm by instrumenting)
The canvas key handler lives on the **canvas**; the View's override lives on the **UserControl**. After a
toolbar Button click, focus is on the **Button** (the explicit `Focus()` races the Button's own focus and
often loses), so:
- the **canvas** `OnKeyDown` never fires (canvas is in a different visual subtree, not in the Button→root
  bubble path), and
- the **View** `OnKeyDown` override should fire on bubble — so either it isn't, or it is and `SetSelectTool`'s
  effect isn't taking. We must SEE which.

## LAYER 0 — INSTRUMENT (no behavior change): log every Esc path
Add temporary `Debug.WriteLine`/`Console.Error` logging (the user runs from a terminal, so stderr is visible):
1. In **canvas** `OnKeyDown`: log `"[Esc] canvas OnKeyDown key={e.Key} focusOnCanvas={IsFocused}"` at entry
   (for any key; note Escape).
2. In **View** `OnKeyDown` override: log `"[Esc] view OnKeyDown key={e.Key} handled={e.Handled}
   inlineVisible={InlineEditBox.IsVisible} hasActiveOp={Vm?.HasActiveOperation}"`.
3. In **VM** `OnKeyDown`: log `"[Esc] vm OnKeyDown key={key} activeTool={ActiveTool} hasActiveOp=
   {HasActiveOperation}"` and in `SetSelectTool` log `"[Esc] SetSelectTool: tool now {ActiveTool}"`.
4. Repro: click Wire button, press Esc. **Report the exact log lines** (which handlers fired, in what order,
   what `e.Handled` was, whether `SetSelectTool` ran and what `ActiveTool` became).

**Layer 0 gate:** report the captured log for "click Wire → Esc". This tells us precisely where the chain
breaks. No fix yet.

## LAYER 1 — one authoritative, focus-independent Esc/shortcut handler
Based on Layer 0, install a SINGLE handler that sees the key regardless of focus, and remove the competing/
dead path. The robust pattern (do this unless Layer 0 shows a simpler cause):
1. In `SchematicView` constructor, register a key handler on the UserControl that catches the bubbling key
   even if a child marked it handled:
   `AddHandler(InputElement.KeyDownEvent, OnViewKeyDownTunnel, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);`
   Handle Esc (and the S/W/F/Z tool shortcuts) here, gated by `!InlineEditBox.IsKeyboardFocusWithin` (so the
   inline TextBox keeps its own Esc/Enter). This fires no matter which child (button or canvas) has focus.
2. Make the View's Esc the single source: `if (Vm.HasActiveOperation) Vm.SetSelectTool(); else
   Vm.Selection.Clear();` then `e.Handled = true`. Keep the canvas `OnKeyDown` for canvas-focused
   shortcuts BUT ensure the two don't both fire for the same press (the tunnel handler with
   `handledEventsToo` will see it first; mark handled to stop the canvas re-processing — or have the canvas
   skip Esc/S/W/F/Z now that the View owns them). Avoid double-handling (e.g. Esc cancelling then a second
   handler clearing selection).
3. Ensure `SetSelectTool` actually flips the tool and the toolbar button visual updates
   (`UpdateToolButtonStates` is driven by the `ActiveTool` PropertyChanged — confirm it fires).
4. After the toolbar Button handlers call `SetXTool()`, keep `SchematicCanvasCtrl.Focus()` (harmless), but the
   tunnel handler no longer depends on it.

**Layer 1 gate:** click Wire → Esc → **ActiveTool == Select** (verified via the log + the toolbar Select
button shows active); same for: arm placement (also disarms `PlacementService` per the prior brief — keep
that), wiring mid-draw, zoom-box, drag, rubber-band. Idle Esc (Select, selection present) clears the
selection. Inline-edit Esc still cancels the edit and returns to Select (don't let the new tunnel handler
double-fire there). Report.

## LAYER 2 — remove instrumentation + parity check
1. Remove all Layer 0 logging.
2. Confirm the same authoritative-handler pattern for the **Symbol Editor** (`SymbolEditorView` / its canvas):
   click a drawing-tool button, press Esc → returns to Select; verify it's focus-independent too. Apply the
   same tunnel-handler fix there if the symbol editor has the same toolbar-focus gap.

**Layer 2 gate:** instrumentation gone; both editors: clicking any tool button then Esc returns to Select
(focus-independent); idle Esc deselects. Report.

## Acceptance
1. Click Wire (or any tool) button, press Esc → Select tool active — regardless of keyboard focus.
2. Esc cancels every in-progress action (arm+disarm, DnD, wiring, zoom-box, inline edit, drag) → Select; idle
   Esc deselects. Both editors.
3. No double-handling (cancel-then-clear in one press); inline TextBox keeps its own Esc/Enter.
4. `dotnet build`/`dotnet test` green; firewall green.

## Guardrails
- **Root cause is focus/routing, not Esc logic** — instrument to confirm WHICH handler sees the key before
  editing; don't re-tweak the (correct) VM branch blindly again.
- **One authoritative handler** that's focus-independent (`handledEventsToo: true` on the UserControl), gated
  to exclude the inline TextBox; eliminate the dead/duplicate path so Esc isn't handled twice.
- Keep the prior `PlacementService.Disarm()`-on-cancel behavior.
- Verify at runtime (the log) that `ActiveTool` becomes `Select` — not just that code "looks right."
- **Scope fence:** Esc/shortcut key ROUTING (both editors) only. No drag/connectivity/other changes.
- Sub-gate; report between each layer.
- Update `src/Ui/CLAUDE.md`: schematic/symbol shortcuts are handled by a focus-independent UserControl-level
  tunnel handler (toolbar buttons steal focus, so canvas-only KeyDown is insufficient).

*Exit: pressing Esc returns to the Select tool from any state and regardless of which control has focus, in
both editors — proven by the runtime log showing ActiveTool flip to Select.*

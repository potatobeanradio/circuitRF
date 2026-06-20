# circuitRF — Pin select: STOP fixing hit-math, instrument the upstream gates (Claude Code / Sonnet)

Three rounds have now "fixed" pin selection and it STILL doesn't work. That is proof the bug is **NOT in the
hit-test math** — `HitTestPin`, `PinToolPress`, the zoom-scaled tolerance, and the renderer highlight are all
correct on disk. The click is dying **before** it reaches `PinToolPress`, in a gate we have never actually
measured. **Do not touch `HitTestPin` / tolerance again.** This brief is pure instrumentation: log the three
upstream gates, run the exact repro, and REPORT. No fix until the log tells us where the click dies. Firewall
green.

## Why the previous fixes couldn't have worked
`PinToolPress` runs only if **both** are true at click time:
1. `ActiveTool == Tool.Pin`  — set by clicking the Pin toolbar button.
2. `!IsLocked`  — `OnPointerPressed` guards `if (ActiveTool == Tool.Pin) { if (!IsLocked) PinToolPress(...); }`.
And the Pin toolbar button itself is gated `IsEnabled="{Binding ViewModel.IsEditable}"` (= `!IsLocked`).
**Leading hypothesis:** the symbol opens **locked** (`EditableSymbol.UserEditable == false` →
`IsLocked == true` → `IsEditable == false`). Then the Pin button is DISABLED — the user can't arm the Pin tool
— so every click falls through `ActiveTool == Tool.Select` → `SelectToolPress`, which hit-tests *primitives*,
never pins. Result: "cannot select a pin," exactly as reported, with perfect hit-math. We have never verified
`UserEditable`/`IsLocked` on the path the user actually opens. Second possibility: the tool isn't switching to
Pin, or the press isn't reaching the VM. The log will disambiguate.

## INSTRUMENT (no behavior change) — add these logs, build, run, report
Add temporary `Console.Error.WriteLine` lines (user runs from a terminal; stderr is visible):

1. **Lock/editable at construction** — in the `SymbolEditorViewModel` constructor, after `_isLocked` is set:
   `Console.Error.WriteLine($"[PIN-GATE] ctor UserEditable={editableSymbol.UserEditable} IsLocked={_isLocked} IsEditable={IsEditable}");`

2. **Tool switches** — in `OnActiveToolChanged(Tool value)`:
   `Console.Error.WriteLine($"[PIN-GATE] ActiveTool→{value} IsLocked={IsLocked}");`

3. **Every press into the VM** — at the TOP of `OnPointerPressed(double lx, double ly, …)` (before the tool
   branches):
   `Console.Error.WriteLine($"[PIN-GATE] OnPointerPressed lx={lx:F1} ly={ly:F1} ActiveTool={ActiveTool} IsLocked={IsLocked} clickCount={clickCount}");`

4. **Pin branch reached** — inside the `if (ActiveTool == Tool.Pin)` branch in `OnPointerPressed`, BOTH sides:
   - before the lock guard: `Console.Error.WriteLine($"[PIN-GATE] Pin branch entered; IsLocked={IsLocked}");`
   - if `IsLocked` skips `PinToolPress`, log: `Console.Error.WriteLine("[PIN-GATE] BLOCKED by IsLocked — PinToolPress NOT called");`

5. **Inside PinToolPress** — first line:
   `Console.Error.WriteLine($"[PIN-GATE] PinToolPress lx={lx:F1} ly={ly:F1} pinCount={EditableSymbol.Pins.Count} CanvasZoom={CanvasZoom:F3}");`
   and after the hit test: `Console.Error.WriteLine($"[PIN-GATE] HitTestPin→{hit} (tol={12.0/Math.Max(CanvasZoom,1e-6):F1})");`

6. **Canvas routing** — in `SymbolEditorCanvas.OnPointerPressed`, right before
   `_viewModel.OnPointerPressed(...)`:
   `System.Console.Error.WriteLine($"[PIN-GATE] canvas press → VM (left={props.IsLeftButtonPressed})");`
   and confirm `_viewModel.CanvasZoom` is being set (log its value where you set it).

## The exact repro to run (report the full [PIN-GATE] log for each step)
1. Open a symbol the way the user does — **create a New Symbol via the project tree / cell right-click**
   (NOT a built-in locked one). Report the `[PIN-GATE] ctor …` line — **is `IsLocked` true or false?**
2. Click the **Pin** toolbar button. Is it enabled/clickable? Report the `ActiveTool→Pin` line (did it fire?).
3. Place a pin (click empty canvas) — does a pin appear? Report.
4. Click directly on that pin. Report every `[PIN-GATE]` line for that click: did `OnPointerPressed` fire with
   `ActiveTool=Pin`? Did the Pin branch enter? Was it BLOCKED by IsLocked? Did `PinToolPress` run? What did
   `HitTestPin` return, and what were `pinCount`/`CanvasZoom`/`tol`?

## What the log will tell us (and the fix that follows — do NOT pre-apply)
- **`ctor … IsLocked=True`** on a user-created symbol → the open path is wrongly marking it locked
  (`UserEditable=false`). FIX THERE: ensure new/user `.csym` opens with `UserEditable=true`; the New-Symbol
  creation and the load path must set it. (This is the most likely outcome.)
- **`ActiveTool→Pin` never logged** → the Pin button is disabled (because IsEditable=false) or unbound. Same
  lock root cause, or a binding gap.
- **`OnPointerPressed … ActiveTool=Select`** even after clicking Pin → tool didn't switch; investigate the
  toolbar command / `SetActiveToolCommand`.
- **`BLOCKED by IsLocked`** → confirms the lock gate; fix the lock state, not the hit-test.
- **`PinToolPress` runs, `HitTestPin→-1` with a huge tol** → only THEN is it a geometry/coord issue (e.g.
  `CanvasZoom` is 0/stale, or pin coords differ from click space). Report `pinCount`, `CanvasZoom`, click
  vs. `Pins[0].LocalX/Y`.

**Gate:** report the full `[PIN-GATE]` transcript for the 4-step repro. State the single line where the click
dies. Propose the one-line fix for THAT gate. Do not modify hit-test math. Then STOP for review.

## Guardrails
- **No changes to `HitTestPin`, tolerance, or the zoom conversion** — they are correct; changing them again is
  the trap.
- Instrument-only this pass; the fix is a separate, reviewed step targeting the gate the log identifies.
- Leave the round-2 zoom-tolerance fix in place (it was a real, separate bug for zoomed-out clicking).
- Report the literal log lines, not a paraphrase.

*Exit: a `[PIN-GATE]` transcript that pinpoints the exact gate where a pin click dies (almost certainly
`IsLocked=true` on a user symbol disabling the Pin tool), so the next step fixes that gate in one line instead
of re-perfecting correct hit-math a fourth time.*

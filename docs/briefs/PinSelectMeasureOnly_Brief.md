# circuitRF — Pin select: MEASUREMENT ONLY, change nothing else (Claude Code / Sonnet)

We have now "fixed" pin selection three times and it still fails. The hit-test code on disk is correct
(`PinToolPress` uses raw click coords; `HitTestPin` tol = max(12/zoom, PinGrid*0.5); round-2 cruft removed).
That it STILL doesn't work proves the click is **not reaching `PinToolPress`**, or the pin we click is **not
in the list `HitTestPin` scans**. We will not guess again. **This task adds logging ONLY and runs ONE repro.
Change no logic. Report the literal output. Then STOP.**

## Add these logs (and NOTHING else)
All as `Console.Error.WriteLine` (user runs from a terminal; stderr shows):

1. **Constructor** (`SymbolEditorViewModel`, after `_isLocked` set):
   `Console.Error.WriteLine($"[PIN] ctor UserEditable={editableSymbol.UserEditable} IsLocked={_isLocked} pins={editableSymbol.Pins.Count}");`

2. **Tool change** (`OnActiveToolChanged`):
   `Console.Error.WriteLine($"[PIN] tool→{value} IsLocked={IsLocked}");`

3. **Canvas press** (`SymbolEditorCanvas.OnPointerPressed`, immediately before
   `_viewModel.OnPointerPressed(...)`):
   `System.Console.Error.WriteLine($"[PIN] canvas press screen=({pos.X:F1},{pos.Y:F1}) world=({ScreenToWorldX(pos.X):F1},{ScreenToWorldY(pos.Y):F1}) zoom={_zoom:F3} left={props.IsLeftButtonPressed} vm={( _viewModel is null ? "NULL" : "ok")}");`
   Also log that `CanvasZoom` is pushed: wherever the canvas sets `_viewModel.CanvasZoom = _zoom;`, add
   `System.Console.Error.WriteLine($"[PIN] set CanvasZoom={_zoom:F3}");` — and if it is set NOWHERE, say so in
   the report (that would mean `CanvasZoom` stays 1.0 and the tol math uses the wrong zoom).

4. **VM press entry** (top of `OnPointerPressed`, before the tool branches):
   `Console.Error.WriteLine($"[PIN] vm press world=({lx:F1},{ly:F1}) ActiveTool={ActiveTool} IsLocked={IsLocked}");`

5. **Pin branch** (inside `if (ActiveTool == Tool.Pin)`):
   - first line: `Console.Error.WriteLine($"[PIN] pin-branch IsLocked={IsLocked}");`
   - in the `if (!IsLocked)` — if false, log `[PIN] BLOCKED: IsLocked swallowed the click`.

6. **PinToolPress** (first line) and **HitTestPin** (just before `return -1;` and at each `return i;`):
   - press: `Console.Error.WriteLine($"[PIN] PinToolPress world=({lx:F1},{ly:F1}) pins={EditableSymbol.Pins.Count} CanvasZoom={CanvasZoom:F3} tol={Math.Max(12.0/Math.Max(CanvasZoom,1e-6), PinGrid*0.5):F1}");`
   - per-pin in the scan loop:
     `Console.Error.WriteLine($"[PIN]   pin[{i}]=({pins[i].LocalX:F1},{pins[i].LocalY:F1}) dist={dist:F1}");`
   - on hit `return i`: log `[PIN] HIT i={i}`. On miss: `[PIN] MISS (nearest dist={minDist:F1} tol={tol:F1})`.

7. **Renderer pin source** (in `SymbolEditorRenderer.DrawPinMarkers`, first line):
   `System.Console.Error.WriteLine($"[PIN] render draws {pins.Count} pins; selIdx={overlay.SelectedPinIndex}");`
   and for each pin drawn: `[PIN]   draw pin[{i}] local=({pin.LocalX:F1},{pin.LocalY:F1}) screen=(…)`.
   **This is the unturned stone:** confirm the pins the RENDERER draws (from `RenderSymbol`/`ToSymbol()`) are
   the SAME coordinates `HitTestPin` scans (from `EditableSymbol.Pins`). If the drawn pin and the scanned pin
   differ, that's the bug.

## The ONE repro to run (report the full [PIN] transcript verbatim)
1. Create a **New Symbol** from the project tree / cell (the path the user actually uses).
   → report the `[PIN] ctor …` line. **Is IsLocked true?**
2. Click the **Pin** toolbar button. Is it enabled? → report `[PIN] tool→Pin …` (did it fire?).
3. Click once on empty canvas to place a pin. → report every `[PIN]` line. Did a pin get placed
   (`render draws 1 pins`)? Where (local coords)?
4. Click directly on that just-placed pin (same spot). → report every `[PIN]` line for this click:
   - did `canvas press` fire with `left=true vm=ok`?
   - did `vm press` show `ActiveTool=Pin`?
   - was it `BLOCKED: IsLocked`?
   - did `PinToolPress` run? what `CanvasZoom`/`tol`?
   - what were the per-pin `dist` values, and HIT or MISS?
   - did the renderer's drawn pin coords match the scanned pin coords?

## Gate
Report the literal transcript for all 4 steps. State the SINGLE line where it breaks, chosen from these
mutually-exclusive outcomes:
- (A) `IsLocked=true` → the symbol opens locked; fix the open/create path (a separate task).
- (B) `tool→Pin` never logged / button disabled → tool can't arm (also lock/binding).
- (C) `vm press ActiveTool=Select` after clicking Pin → tool didn't switch.
- (D) `BLOCKED: IsLocked` → lock gate swallowed it.
- (E) `PinToolPress` runs, `CanvasZoom` is 0 or 1.0-stale making `tol` wrong → zoom not pushed to VM.
- (F) `MISS` with sane tol but the scanned pin coords ≠ the drawn pin coords → two different pin lists.
- (G) `HIT i=…` logged but selection still not visible → renderer/overlay highlight issue, not hit-test.

Do NOT propose or apply a fix in this task. Just identify which of A–G it is, with the transcript as proof.
Then STOP for review.

## Guardrails
- **Logging only. Zero logic changes.** No new fields, no behavior, no "while I'm here" fixes. If you spot
  something, note it in the report — do not change it.
- Report the LITERAL log lines, not a paraphrase or a conclusion drawn without them.
- Leave the existing pin code exactly as-is.
- `dotnet build` green; firewall green.

*Exit: a verbatim [PIN] transcript that identifies which of A–G is the actual failure, so the next task fixes
the ONE real gate instead of a fourth guess. The leading suspect is (A)/(D) — the symbol opening locked — but
the log decides, not us.*

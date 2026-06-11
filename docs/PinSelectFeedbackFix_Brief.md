# circuitRF — Pin "select" actually works; fix the FEEDBACK (Claude Code / Sonnet)

**The measurement settled it: pin selection WORKS.** The `[PIN]` transcript proves the full chain functions —
do NOT touch `HitTestPin`, `PinToolPress`, the tolerance, the lock gate, or the place/select logic again. They
are correct. The remaining problem is that a selected pin gives **no usable feedback**, so it reads as "can't
select."

## Proof from the transcript (read before doing anything)
```
1st click: PinToolPress pins=0 → MISS → places a pin → render draws 1 pins; selIdx=0
2nd click: PinToolPress pins=1 → pin[0]=(-200,-200) dist=1.4 → HIT i=0 → render draws 1 pins; selIdx=0
```
HIT fires, `_selectedPinIndex=0`, the renderer receives `selIdx=0`. Selection is happening. What's missing:
1. **No visible selection affordance.** `SymbolEditorRenderer.DrawPinMarkers` draws a selected pin by swapping
   the fill to `theme.SelectionBox` and thickening the stroke — both at the pin's ~5px radius. On a tiny dot
   that is nearly indistinguishable from an unselected pin. The user cannot SEE that the pin is selected.
2. **The inspector pin fields are read-only and easy to miss.** `SymbolPrimitiveInspectorViewModel.SetPinView`
   populates X/Y/PortIndex, but the View shows them as read-only `TextBlock`s. Selecting a pin produces no
   editable, obvious confirmation. (This is the unfinished L4 inspector half.)

So "cannot select a pin" = selection is invisible + uneditable, NOT a hit-test failure.

## The fix (feedback only — no hit-test changes)

### Layer 1 — visible pin selection highlight on the canvas
In `SymbolEditorRenderer.DrawPinMarkers`, make a selected pin unmistakable. Draw, for the pin whose index ==
`overlay.SelectedPinIndex`, a **selection ring**: a stroked circle concentric with the pin at radius `r +
~6px`, in the System Accent color (use `theme.Accent`/`SelectionBox`), stroke width ~2px, optionally a
translucent accent fill halo. This is in addition to the dot, so it's obvious at any zoom (radius in
screen-px, like the markers already are). Mirror the schematic's selection look. Keep the normal pins as-is.
**Gate:** clicking a pin shows a clear accent ring around it; clicking another pin moves the ring; clicking
empty (placing) shows the ring on the new pin. Report.

### Layer 2 — finish the inspector pin section: editable + obvious
The Properties pane already switches to the symbol inspector (`PropertiesTool.SetActiveSymbolEditor`) and
`SetPinView` runs on pin selection. Make the pin section EDITABLE and clearly a "Pin" panel:
- A header "Pin" (already `TypeName="Pin"`).
- **PortIndex** — editable integer; commit via `RemapSymbolPinCommand` (the VM already has
  `SelectedPinPortIndex` + `OnSelectedPinPortIndexChanged` doing exactly this — bind the inspector field to it,
  or commit through the same command).
- **X, Y** — editable; commit via `MoveSymbolPinCommand`, P-snapped on commit, live re-render.
Replace the read-only `TextBlock`s in the pin section of `SymbolPrimitiveInspectorView.axaml` with edit
controls (numeric `TextBox`es; integer box for Port — no spinner per the round-2 HIG note). Keep the 300px
budget.
**Gate:** selecting a pin shows the editable Pin panel (Port/X/Y); editing Port re-maps undoably; editing X/Y
moves the pin (P-snapped) live + undoably. Report.

### Layer 3 — confirm move still works (it does in the log; just verify with feedback)
With the ring visible, verify: select pin → drag → it moves live (P-snapped) → commits undoably; the ring
tracks it. (No code change expected — the move path already works per the transcript; this is a visual
confirm.)
**Gate:** drag a selected pin; ring + pin move together; undo restores. Report.

## After it works — remove instrumentation, resume round-2
- Remove ALL `[PIN]` `Console.Error.WriteLine` lines (constructor, OnActiveToolChanged, canvas press +
  CanvasZoom set, VM press, pin-branch, PinToolPress, the HitTestPin per-pin/HIT/MISS lines, and the renderer
  `DrawPinMarkers` lines).
- Resume the remaining round-2 layers (editable primitive fields per type incl. Sine set + Stroke ComboBox,
  editable polyline coords, outline selection highlight for primitives, rotate-about-bottom-left with exact
  undo, integer port-count box, no toolbar scrollbars).

## Guardrails
- **DO NOT modify the pin hit-test / tolerance / place-select logic** — the transcript proves it works;
  changing it again is the trap that cost three rounds.
- The fix is FEEDBACK: a visible selection ring + an editable, obvious pin panel.
- Pins stay on P; Port/X/Y edits undoable; 300px/HIG (integer box, no spinner for Port).
- Remove all `[PIN]` instrumentation when done.
- `dotnet build`/`dotnet test` green; firewall green.
- Update `docs/design/symbol-editor.md`: selected-pin highlight ring; editable pin inspector (Port/X/Y).

*Exit: clicking a pin shows an obvious accent ring AND an editable Pin panel (Port/X/Y) in the Properties pane;
dragging moves it; the hit-test code is untouched because it was never the problem. Instrumentation removed.*

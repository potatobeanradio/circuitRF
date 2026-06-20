# circuitRF — Pin select/move: zoom/tolerance fix (Claude Code / Sonnet)

**You're stuck on L4 because the pin logic is correct — the bug is a coordinate-space/zoom mismatch in the hit
tolerance, the same class as earlier bugs.** Your instrumentation should show `[L1-HitTestPin] MISS
nearest=<big> Tol=15` even when clicking right on the visible pin. Root cause confirmed by comparing the two
canvases. **STOP tuning the pin logic; fix the tolerance's coordinate space.**

## Root cause (decisive — from comparing SchematicCanvas vs SymbolEditorCanvas)
- `SymbolEditorCanvas.OnPointerPressed` passes ONLY world coords to the VM:
  `_viewModel.OnPointerPressed(ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y), …)`.
- `SymbolEditorViewModel.HitTestPin` uses a **fixed 15 *world-unit*** tolerance, and the VM has **no knowledge
  of zoom**.
- Pins RENDER at `r = max(3, zoom*5)` **screen px**, but the hit zone is `15 world units = 15*zoom screen px`.
  When the symbol is zoomed to fit (pins sit on the P=100 grid, so the symbol spans hundreds of world units
  and fits at `zoom < 1`), 15 world units collapses to a few screen px — **smaller than the drawn dot**, so a
  click on the visible pin lands outside the 15-unit world tolerance and misses. (At `zoom=0.1`, Tol=15 world
  = 1.5 screen px.)
- The schematic editor doesn't have this bug because `SchematicCanvas.OnPointerPressed` passes screen coords
  too (`…, pos.X, pos.Y`) and exposes `CurrentZoom`, so its hit-tests use screen-space tolerances.

## The fix (mirror the schematic — give the VM the zoom; scale the tolerance)
Make the pin pick radius a constant **screen-pixel** distance converted to world units via the live zoom.
1. **Expose zoom to the VM.** Add to `SymbolEditorViewModel` a settable `public double CanvasZoom { get; set; }
   = 1.0;` (or a method `SetCanvasZoom(double)`). In `SymbolEditorCanvas`, set it whenever zoom changes —
   after `ZoomToFitInternal`, in `OnPointerWheel`, and in `SyncFromVm`/attach — e.g. `_viewModel.CanvasZoom =
   _zoom;`. (Mirror `SchematicCanvas.CurrentZoom`.) Guard against zoom ≤ 0.
2. **Zoom-correct the pin tolerance.** In `HitTestPin`, replace the fixed `const double Tol = 15.0;` with a
   screen-pixel pick radius converted to world units:
   `double pickPx = 12.0; double tol = pickPx / Math.Max(CanvasZoom, 1e-6);`
   so the clickable region is ~12 screen px regardless of zoom (a bit larger than the drawn dot — good). Keep
   the nearest-pin scan; return the nearest within `tol`.
3. **Same correction anywhere else a fixed world tolerance is hit-tested against screen-rendered handles** —
   check the gripper (`GripperHalfSize = 7.0` world units in `HitTestGripper`) and primitive hit-test
   (`HitTestTopmost` BaseTol=6 world) for the identical zoom bug; if they're also hard to hit when zoomed out,
   apply the same `px / zoom` conversion. (Pin is the reported one — fix it first, then sweep these.)

## Verify (use the instrumentation already in place)
- With the Pin tool active and the symbol zoomed to fit, click a pin → log shows
  `[L1-HitTestPin] HIT idx=… dist=… Tol=…` with Tol now scaling up as you zoom OUT (e.g. Tol≈120 world at
  zoom 0.1), and the pin selects.
- Drag it → `[L1-PinMove]` / `[L1-PinRelease]` fire; the pin moves live (canvas already invalidates on move),
  snaps to P, commits `MoveSymbolPinCommand` (undoable).
- Zoom in close and click a pin → still hits (Tol shrinks in world units but stays ~12 px).
- Place a new pin (click empty) still works (that path doesn't use Tol).

## After it works — remove instrumentation, finish L4 properly
- Remove the `[L1-…]` `Console.Error.WriteLine` lines from `SnapToP`, `HitTestPin`, `PinToolPress`, the pin
  move/release, and the draw-commit (they were the round-2 L1 probes).
- Then complete L4's inspector half: pin **PortIndex**, **X**, **Y** editable in the
  `SymbolPrimitiveInspectorViewModel` (PortIndex → `RemapSymbolPinCommand`; X/Y → `MoveSymbolPinCommand`,
  P-snapped on commit), live + undoable, as the round-2 brief specifies.
- Resume the remaining round-2 layers.

## Guardrails
- **Don't keep tuning the pin select/move logic — it's correct.** The defect is the world-space fixed
  tolerance vs. screen-rendered pins; fix the coordinate space (zoom-scaled pick radius).
- Mirror the schematic's approach (VM knows the zoom; tolerance is a screen-pixel pick radius); don't invent a
  new scheme.
- Pins still snap to the connection grid P on move; everything undoable.
- Update `CanvasZoom` on every zoom change (fit, wheel, initial attach) so the tolerance is always correct.
- `dotnet build`/`dotnet test` green; firewall green.

*Exit: clicking a pin selects it and drags it at any zoom level (tolerance is a constant screen-pixel pick
radius, not a fixed world distance); instrumentation removed; pin Port/X/Y editable in the inspector; L4
done.*

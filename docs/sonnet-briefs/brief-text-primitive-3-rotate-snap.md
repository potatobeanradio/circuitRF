# Brief: text-primitive (3/5) — rotate-in-place + snapping

Third of the 5-brief Text sequence. Briefs 1 (model/geometry) and 2 (rendering) are landed. This brief
makes the editor's rotate key spin a text **in place** (H→V→upside-down→V→H about its center), and makes
newly-placed text **snap visibly** to the grid. Small, focused.

Size: **S**. Files: `src/Ui/Schematic/SymbolGeometry.cs`,
`src/Ui/Commands/Symbol/RotateSelectionCommand.cs`, `src/Ui/ViewModels/SymbolEditorViewModel.cs`.

## Why this works (the payoff from brief 1)

`RotateSelectionCommand` rotates each primitive 90° CW about the selection-bbox **center**, and (brief 1)
`BboxOf(text)` is centered on `TextCenter`. So for a lone text the pivot **is** its center. If, on each
90° step, we (a) rotate the anchor point about the pivot — which `RotateBy90About` already does — and
(b) advance the text's own `Rotation` by 90°, the center stays put and the glyphs spin. Mathematically:
`A' = c + Rot(90, A−c)` and `Rotation += 90` ⇒ `TextCenter` recomputes to exactly `c`. For multi-selections
the text orbits the group center and spins — correct rigid-group behaviour.

## 1. `SymbolGeometry.cs` — `RotateBy90` advances text orientation

Replace the `TextPrimitive` arm:
```csharp
            case TextPrimitive t:
                (t.AnchorX, t.AnchorY) = R(t.AnchorX, t.AnchorY);
                break;
```
with:
```csharp
            case TextPrimitive t:
                (t.AnchorX, t.AnchorY) = R(t.AnchorX, t.AnchorY);
                // Advance in-place orientation so glyphs spin; combined with the anchor rotating about
                // the pivot, a lone text's center stays fixed (rotate-in-place).
                t.Rotation = t.Rotation switch
                {
                    SymbolRotation.R0   => SymbolRotation.R90,
                    SymbolRotation.R90  => SymbolRotation.R180,
                    SymbolRotation.R180 => SymbolRotation.R270,
                    _                   => SymbolRotation.R0,
                };
                break;
```

## 2. `RotateSelectionCommand.cs` — snapshot/restore includes `Rotation`

In `SnapshotRestore`, replace the `TextPrimitive` arm:
```csharp
            case TextPrimitive t:
            {
                double anx = t.AnchorX, any = t.AnchorY;
                return () => { t.AnchorX = anx; t.AnchorY = any; };
            }
```
with:
```csharp
            case TextPrimitive t:
            {
                double anx = t.AnchorX, any = t.AnchorY;
                var rot = t.Rotation;
                return () => { t.AnchorX = anx; t.AnchorY = any; t.Rotation = rot; };
            }
```
(Undo must restore the orientation as well as the position, or repeated rotate→undo would drift the angle.)

## 3. `SymbolEditorViewModel.cs` — new text snaps its top-left

The "doesn't snap" symptom is the legacy default: `VAlign = Baseline` makes the snapped anchor the text
**baseline**, so the glyphs float above the grid line by the ascent. New text should anchor its
**top-left** at the snapped point. Add `VAlign = SymbolTextVAlign.Top` to **both** places that build the
placement `TextPrimitive` (so the live preview matches the committed result):

In `RebuildOverlay` (the typing preview):
```csharp
            else if (_isTypingText)
                inProgress = new TextPrimitive
                {
                    Content   = (_textBuffer.Length > 0 ? _textBuffer : "") + "|",
                    AnchorX   = _textAnchorX,
                    AnchorY   = _textAnchorY,
                    FontSize  = CurrentFontSize,
                    FontStyle = CurrentFontStyle,
                    Align     = SymbolTextAlign.Left,
                    VAlign    = SymbolTextVAlign.Top,
                };
```

In `CommitText`:
```csharp
            Execute(new PlaceSymbolPrimitiveCommand(EditableSymbol, new TextPrimitive
            {
                Content   = _textBuffer,
                AnchorX   = _textAnchorX,
                AnchorY   = _textAnchorY,
                FontSize  = CurrentFontSize,
                FontStyle = CurrentFontStyle,
                Align     = SymbolTextAlign.Left,
                VAlign    = SymbolTextVAlign.Top,
            }));
```

The anchor is already `SnapToP(click)` at placement, and `MoveSymbolPrimitivesCommand` snaps the drag
**delta**, so a text placed on-grid stays on-grid when dragged — no move-path change needed. (Per-text
anchor/VAlign editing comes with the inspector in brief 5; legacy text keeps `Baseline` and renders
unchanged.)

## Verification (runtime)

1. Place a Text, Select it, press **R** repeatedly → it cycles horizontal → vertical → upside-down →
   vertical → horizontal, pivoting about its center (it does **not** wander/orbit). Undo reverses each step.
2. Newly-placed text sits with its **top-left on the grid intersection** you clicked (visibly snapped),
   at the active snap mode (P / p / off). Dragging it keeps it on-grid.
3. A multi-selection (text + a shape) rotates as a rigid group: the text orbits the group center and its
   glyphs spin.
4. Legacy text (loaded from an older .csym, `VAlign=Baseline`) is unchanged until explicitly edited.

## Acceptance

- `R` rotates a lone text in place through the 4 orientations about its center; undo restores orientation.
- New text anchors top-left and visibly snaps; existing text unchanged.
- Rotation is captured/restored by the rotate command's undo.

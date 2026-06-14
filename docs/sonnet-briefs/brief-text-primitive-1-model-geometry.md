# Brief: text-primitive (1/5) — model + geometry foundation

First of a 5-brief sequence reworking the symbol Text primitive. **This brief is regression-safe and
adds no visible behaviour** — it only adds fields and makes geometry rotation/anchor-aware so the later
briefs (rendering, rotate-in-place, snapping, inline edit, inspector) have a foundation. Existing text
(Rotation R0, VAlign Baseline) keeps an identical bounding box, so nothing changes on screen yet.

Size: **M**. Files: `src/Ui/Schematic/SymbolModel.cs`, `src/Ui/Schematic/SymbolGeometry.cs`,
`src/Ui/Schematic/SymbolPersistence.cs`.

## The anchor/rotation model (read first — the rest follows from this)

A text primitive has an **anchor point** `(AnchorX, AnchorY)` and an unrotated box of size `(W, H)`
(W = content advance, H = ascent+descent, from the existing approximation constants). Two new fields plus
the existing `Align` define the model:

- `Align` (Left/Center/Right) + new `VAlign` (Baseline/Top/Middle/Bottom) = **which point of the box the
  anchor is** (e.g. Left+Top = box top-left corner; Center+Middle = box center; Left+Baseline = legacy).
- `Rotation` (R0/R90/R180/R270) = the box spins about its **center** `C`.
- The anchor is the chosen corner/edge of the **rotated** box, so:
  `C = Anchor − Rot(θ, anchorOffset)` and `Anchor = C + Rot(θ, anchorOffset)`,
  where `anchorOffset` is the anchor's offset from center in the *unrotated* text frame and `Rot` is the
  same screen-Y-down CW rotation used by `RotateBy90` (`R(x,y) = (−y, x)`).

This is what makes later work clean: rotate-in-place keeps `C` fixed and re-derives `Anchor`; snapping
snaps `Anchor`; rendering draws glyphs centered at `C` rotated by θ.

## 1. `SymbolModel.cs`

Add the vertical-anchor enum near `SymbolTextAlign`:
```csharp
public enum SymbolTextVAlign { Baseline, Top, Middle, Bottom }
```

Extend `TextPrimitive` (keep existing members; `SymbolRotation` is the existing enum in this namespace,
used by components):
```csharp
public sealed class TextPrimitive : SymbolPrimitive
{
    public string Content   { get; set; } = "";
    public double AnchorX   { get; set; }
    public double AnchorY   { get; set; }
    public double FontSize  { get; set; } = 12.0;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolFontStyle FontStyle { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolTextAlign Align     { get; set; }

    // ── NEW (default values preserve legacy rendering for old .csym files) ──
    /// <summary>Vertical anchor reference. Baseline = legacy behaviour (anchor on the text baseline).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolTextVAlign VAlign { get; set; } = SymbolTextVAlign.Baseline;

    /// <summary>In-place orientation; the box spins about its center. Default R0.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolRotation Rotation { get; set; } = SymbolRotation.R0;

    /// <summary>When true, a rotated cell *instance* in the schematic auto-flips this text 180° as
    /// needed so it never renders upside-down/mirrored. When false (default), it rotates rigidly with
    /// the instance. The symbol editor always shows the literal authored rotation regardless.</summary>
    public bool ForceReadable { get; set; }
}
```

## 2. `SymbolGeometry.cs`

Add text metric + anchor/center helpers (framework-free; reuse the existing `TextAdvance`,
`TextAscentFrac`, `TextDescentFrac` constants). Place them near `BboxOf`:

```csharp
    /// <summary>Unrotated text box size (W = content advance, H = ascent+descent), font-approximated.</summary>
    public static (double W, double H) TextBoxSize(TextPrimitive t)
    {
        double w = t.Content is { Length: > 0 } c
            ? c.Length * t.FontSize * TextAdvance
            : t.FontSize * TextAdvance * 2;          // empty: same minimum as old BboxOf (halfW*2)
        double h = t.FontSize * (TextAscentFrac + TextDescentFrac);
        return (w, h);
    }

    /// <summary>Anchor offset from box center in the UNROTATED text frame (screen Y-down).</summary>
    private static (double ox, double oy) TextAnchorOffset(TextPrimitive t)
    {
        var (w, h) = TextBoxSize(t);
        double ox = t.Align switch
        {
            SymbolTextAlign.Center => 0.0,
            SymbolTextAlign.Right  => +w * 0.5,
            _                      => -w * 0.5,      // Left
        };
        double oy = t.VAlign switch
        {
            SymbolTextVAlign.Top    => -h * 0.5,
            SymbolTextVAlign.Middle =>  0.0,
            SymbolTextVAlign.Bottom => +h * 0.5,
            _                       => -h * 0.5 + t.FontSize * TextAscentFrac,  // Baseline (legacy)
        };
        return (ox, oy);
    }

    // CW 90° steps in screen (Y-down) coords — matches RotateBy90's R(x,y)=(−y,x).
    private static (double x, double y) RotStep(double x, double y, SymbolRotation r) => r switch
    {
        SymbolRotation.R90  => (-y,  x),
        SymbolRotation.R180 => (-x, -y),
        SymbolRotation.R270 => ( y, -x),
        _                   => ( x,  y),
    };

    /// <summary>The text box center C, derived from the anchor: C = Anchor − Rot(θ, anchorOffset).</summary>
    public static (double cx, double cy) TextCenter(TextPrimitive t)
    {
        var (ox, oy) = TextAnchorOffset(t);
        var (rx, ry) = RotStep(ox, oy, t.Rotation);
        return (t.AnchorX - rx, t.AnchorY - ry);
    }

    /// <summary>Sets AnchorX/Y so the box center is (cx, cy): Anchor = C + Rot(θ, anchorOffset).</summary>
    public static void SetTextCenter(TextPrimitive t, double cx, double cy)
    {
        var (ox, oy) = TextAnchorOffset(t);
        var (rx, ry) = RotStep(ox, oy, t.Rotation);
        t.AnchorX = cx + rx;
        t.AnchorY = cy + ry;
    }
```

Replace the `TextPrimitive` case in **`BboxOf`** (it currently handles only horizontal Align and no
rotation) with a center+rotated-extent computation:

```csharp
            case TextPrimitive t:
            {
                var (cx, cy) = TextCenter(t);
                var (w, h)   = TextBoxSize(t);
                // R90/R270 swap the footprint.
                bool swap = t.Rotation is SymbolRotation.R90 or SymbolRotation.R270;
                double halfW = (swap ? h : w) * 0.5;
                double halfH = (swap ? w : h) * 0.5;
                ax = cx - halfW; ay = cy - halfH;
                bx = cx + halfW; by = cy + halfH;
                break;
            }
```
(The existing min-half-size clamp below the `switch` still applies.)

**Why this is regression-safe:** for legacy text (Align=Left, VAlign=Baseline, Rotation=R0),
`TextCenter` → `(AnchorX + W/2, AnchorY − descentOffset)` and the box → exactly the old
`[AnchorX, AnchorX+W] × [AnchorY−ascent, AnchorY+descent]`. Identical bbox.

In **`Clone`**, copy the new fields on the `TextPrimitive` arm:
```csharp
        TextPrimitive t => new TextPrimitive
            { Content = t.Content, AnchorX = t.AnchorX, AnchorY = t.AnchorY,
              FontSize = t.FontSize, FontStyle = t.FontStyle, Align = t.Align,
              VAlign = t.VAlign, Rotation = t.Rotation, ForceReadable = t.ForceReadable },
```

(Note: `RotateBy90`'s text arm and `RotateSelectionCommand`'s text snapshot are intentionally **not**
touched here — they change in brief 3, which is where rotate gains the in-place behaviour. Leaving them
as-is now keeps this brief inert.)

## 3. `SymbolPersistence.cs`

Bump the format version (new fields default gracefully on older files; the loader already accepts
older-or-equal versions and rejects only newer):
```csharp
    public const int CurrentFormatVersion = 6;   // was 5: TextPrimitive VAlign/Rotation/ForceReadable
```

## Verification

- Build clean. Open an existing symbol that has text → it renders and selects exactly as before
  (bbox unchanged). Rotating it still does the *old* thing (anchor orbits) — that's expected; brief 3
  changes it.
- Save a symbol → reopen → text identical; new file shows `"FormatVersion": 6` and (for non-default
  text) `VAlign`/`Rotation`/`ForceReadable` fields.
- Quick unit sanity (if convenient): for a Left/Baseline/R0 text, `BboxOf` matches the pre-change result.

## Acceptance

- `TextPrimitive` has `VAlign` (default Baseline), `Rotation` (default R0), `ForceReadable` (default false).
- `SymbolGeometry` exposes `TextBoxSize`, `TextCenter`, `SetTextCenter`; `BboxOf` is rotation- and
  VAlign-aware; `Clone` copies the new fields.
- `.csym` version is 6; legacy text loads and renders unchanged.

---

### The remaining briefs (for context; written one at a time after each lands)

2. **Rendering** — `SchematicRenderer.DrawSymbol` + `SymbolEditorRenderer`: draw glyphs centered at
   `TextCenter`, rotated by `Rotation` (composed with component rotation/mirror for instances), honoring
   `ForceReadable` for schematic instances (auto-flip 180° when upside-down/mirrored); editor always
   shows literal rotation. Needs a `normalizeReadability`-style flag on the text draw path.
3. **Rotate-in-place + snapping** — `RotateBy90` advances `Rotation` and keeps `C` fixed (via
   `SetTextCenter`); `RotateSelectionCommand` snapshots/restores `Rotation`; placement/move snap the
   anchor; new text defaults to Align=Left, VAlign=Top.
4. **Double-click inline edit** — add an inline TextBox to `SymbolEditorView` (mirroring SchematicView),
   double-tap a text primitive → edit Content → commit via `SetTextPrimitiveCommand`.
5. **Inspector** — add anchor (Align×VAlign 3×3 + Baseline), Rotation, and `ForceReadable` controls to
   the text-primitive inspector.

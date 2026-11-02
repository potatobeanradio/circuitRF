# Brief 1 — the scene: one layout pass, two readers

**Series:** [stackup rendering](brief-stackup-render-0-overview.md) · **Tag:** `R-stk1-n`
**Area:** `src/Render/Renderers/` · **Depends on:** nothing · **Blocks:** every other brief in the series

---

## 0. What this brief delivers

Three framework-free types in `src/Render`, and **no UI whatsoever**:

| Type | File | What it is |
|---|---|---|
| `StackupScene` | `src/Render/Renderers/StackupScene.cs` | The *layout*: where every band, barrel, label and gripper lands, as rectangles in scene coordinates. A pure function of `(Technology, width, options)`. |
| `StackupRenderer` | `src/Render/Renderers/StackupRenderer.cs` | `Draw(SKCanvas, StackupScene, StackupRenderTheme, StackupOverlay?)`. Paints the scene. Computes nothing. |
| `StackupRenderTheme` | `src/Render/Renderers/StackupRenderTheme.cs` | Resolved colours, on `LayoutRenderTheme`'s exact pattern (`FromTheme(ColorTheme, ColorVariant)` + a `Fallback`). |

`src/Render` references `src/Design`, so `Technology`, `Stackup`, `StackupLayer`, `LayerDef` and
`Rgba` are all already in scope. SkiaSharp is allowed across the UI firewall; Avalonia is not, and
`tests/Firewall.Tests` will fail the build if anything here reaches for it.

### R-stk1-1 — the scene is computed ONCE and read TWICE

The control that follows in brief 2 has to answer "what is under this pointer?" for clicks, drags,
grippers, context menus and double-click-to-edit. **It answers it by reading the same
`StackupScene` the renderer drew**, never by re-deriving band positions.

A second copy of the placement arithmetic is the defect that does not appear until the geometry
changes: the picture moves, the hit-test does not, and the user clicks a band and selects its
neighbour. Nothing throws.

So `StackupScene` is a **record of rectangles with names**, and it is public:

```csharp
public enum StackupHitKind { Band, ViaBarrel, ViaGripTop, ViaGripBottom, Label, Empty }

/// <summary>One thing the pointer can be over, and what editing it would mean.</summary>
public sealed record StackupHit(
    StackupHitKind Kind,
    string LayerName,          // StackupLayer.Name — the key everything else resolves by
    StackupField Field,        // which value a Label hit would edit; None otherwise
    SKRect Rect);

public enum StackupField { None, Name, Thickness, Sigma, Epsr, TanD, Mur, WallThickness, Span }

public sealed class StackupScene
{
    public static StackupScene Build(Technology tech, float width, StackupSceneOptions options);

    public float Width  { get; }
    public float Height { get; }                      // the INTRINSIC height — what the ScrollViewer scrolls
    public IReadOnlyList<StackupHit> Hits { get; }    // topmost LAST; hit-test walks it backwards
    public IReadOnlyList<StackupBand>  Bands  { get; }
    public IReadOnlyList<StackupBarrel> Barrels { get; }
    public IReadOnlyList<StackupLabel>  Labels  { get; }
    public StackupHit? HitTest(float x, float y);
    public SKRect? RectOf(string layerName);          // what brief 3's selection outline frames
}
```

`HitTest` walks `Hits` from the end so a via gripper beats a via barrel beats a band, which is the
z order they are drawn in. State that ordering in the type's own doc comment — it is the whole
disambiguation rule and it is invisible from the call site.

---

## 1. The picture: `DocStackupFixtures` is the specification

The owner named the user-doc figures as the visual target. `src/Ui/Diagnostics/Fixtures/DocStackupFixtures.cs`
is what draws them, and its conventions are the ones to carry across — **read it before writing a line
of this**. It draws in Avalonia controls on a `Canvas` and is not reusable as code; it is entirely
reusable as a specification.

What carries over unchanged:

- **Bands top to bottom in `Stackup.Layers` order**, which *is* z. Vias are not bands.
- **A conductor takes its fill from the drawing layer it is bound to** (`DocStackupFixtures.Metal`:
  first `DrawingLayers` key that resolves against `tech.Layers`, else a copper fallback). `R-stk1-5`.
- **A dielectric is neutral grey** — `Color.FromArgb(60, 120, 130, 145)` — because it is not something
  anyone draws on. The owner asked for this explicitly: dielectrics stay grey.
- **The ground-designated conductor is the one band with a heavy edge.** Every other distinction in
  the picture is a label.
- **A band's own name sits ON the band in fixed dark ink**, not the theme's foreground: the band's
  colour is the technology's and is identical in both variants, so a theme-coloured name vanishes into
  copper in the dark variant. This is a real bug that was already found once — do not re-introduce it.
- **A via is drawn ACROSS the bands it spans, at its own x**, never as a band in the list. It has no z
  band of its own; it is a connection between two of them.
- **The boundary conditions are drawn as notes above and below the stack**, because they are properties
  of the stack rather than of any band.

What is new here and is not in the figures: relative heights within a kind (§3), via wall thickness
and plated/solid (§4), grippers (§5), and the no-overlap guarantee (§6).

---

## 2. Scene coordinates and the width

The scene's x axis is the control's pixel width; there is no zoom and no pan (series overview §4).
`Build` takes the available width and lays out three columns:

```
│← gutter →│←──────── band column ────────→│← gap →│←──── label column ────→│
  24 px      ~55% of usable width             18 px   the rest
```

Band column and label column both **flex with the width** rather than being the constants
`DocStackupFixtures` uses, because this lives in a dockable pane a user resizes. Floor the band column
so a very narrow pane degrades predictably rather than collapsing (`R-stk1-2`): below a stated minimum
usable width the label column is dropped and the specs move onto the band, and below a second minimum
the scene renders a single line saying the pane is too narrow. Both thresholds are constants in the
scene, both are tested.

`Height` is the sum of the band heights plus the two boundary notes plus the footer padding. It is
**intrinsic**: brief 2's control reports it as its desired height and a `ScrollViewer` scrolls it.
Nothing in this series compresses the drawing to fit a viewport.

---

## 3. Band height: relative within a kind, and honest when it cannot be

### R-stk1-3 — the rule

Per the owner: a conductor half as thick as another conductor draws half as tall; likewise dielectric
against dielectric; **never** conductor against dielectric, because those are routinely three orders of
magnitude apart (3 µm of metal on 100 µm of GaAs; 35 µm of copper on 1.6 mm of FR-4).

So each kind gets its **own** band-height range, and the two ranges are separate and deliberately
overlapping in absolute pixels:

| Kind | Height range |
|---|---|
| Conductor | `ConductorMin` … `ConductorMax` |
| Dielectric | `DielectricMin` … `DielectricMax` |

Within a kind, given that kind's thinnest `tMin` and thickest `tMax` present in the stack:

- **If `tMax / tMin` is within the range budget** — i.e. exact proportionality fits between the kind's
  min and max height — **use exact proportionality.** `h(t) = HeightMin * t / tMin`. Two conductors at
  a 2:1 thickness ratio then draw at exactly 2:1, which is what was asked for.
- **Otherwise compress**, monotonically: `h(t) = HeightMin + (HeightMax - HeightMin) * log(t/tMin) / log(tMax/tMin)`.
  The thickest still draws tallest, the thinnest still draws shortest, every ordering is preserved, and
  nothing is a hairline.

### R-stk1-4 — the compression is scene METADATA, and is never drawn

When a kind fell back to compression, the scene carries a flag —
`StackupScene.ConductorsCompressed`, `StackupScene.DielectricsCompressed`. **Nothing draws it.**

**The picture stays clean** (owner, 2026-09-13): no note, no asterisk, no legend, no "not to scale"
caption. The flags exist so the §3 tests can assert which branch was taken, and so a caller outside
the picture (a documentation figure's own caption, say) can ask — not so the renderer can annotate
itself. A whole line of chrome qualifying a subtlety most stackups never hit is a poor trade against a
drawing that reads at a glance.

What carries the honesty instead is already on every band and costs no extra ink: **the real thickness
is printed**, which is the number a reader actually needs, and the ordering is always truthful because
§3's function is monotone in every case. The chapter explains the height rule once
(brief 8 `R-stk8-3`); the picture does not restate it.

### Tests (`StackupSceneTests`)

- **Monotone within kind, always**: over every shipped technology and over a generated stack with
  ratios from 1:1 to 10⁴:1 — `t1 > t2` ⟹ `h1 >= h2`, for both kinds.
- **Exactly proportional when it fits**: a stack of two conductors at 2:1 draws at 2:1 to within half a
  pixel, and `ConductorsCompressed` is false.
- **Compressed when it cannot fit**, and the flag is set: `MmicGaAs`'s dielectrics.
- **Never compared across kinds**: adding a 1.6 mm dielectric to a stack does not change any
  conductor's height by one pixel.

---

## 4. Vias: the barrel, the wall, and plated vs solid

### R-stk1-6 — the barrel spans exactly the two conductors it names

`SpanFromLayer` and `SpanToLayer` are `StackupLayer.Name` values of **conductor** entries. The barrel
runs from the top of the higher band to the bottom of the lower one — `DocStackupFixtures` already does
exactly this. A via whose span names a layer that is not in the stack, or names only one end, is
**drawn as a refusal marker in the label column rather than omitted**: an unresolvable via that simply
does not appear is a stackup the user cannot tell is broken from the picture. The card list already
flags it; the drawing must not disagree by staying silent.

### R-stk1-7 — the wall is relative to the other vias, and plated ≠ solid ≠ unplated

Three drawn states, from the two independent model fields (overview §3b):

| `Plated` | `Fill` | Drawn as |
|---|---|---|
| `true` / null | `Plated` | A **hollow barrel**: two metal walls with the hole between them. Wall pixel width scales across the stack's own vias exactly as §3 scales band heights — proportional within budget, compressed and monotone otherwise, with its own min and max. |
| `true` / null | `Solid` / null | A **filled barrel**, solid metal edge to edge. |
| `false` | (any) | An **outline only**, no metal fill — a hole, drawn as one. Its label says so. |

`WallThicknessDbu` is the wall, **not** the hole radius. That confusion is documented on the model
field already and a drawing that gets it backwards makes it permanent.

### R-stk1-8 — lateral position

Each via is drawn at an x within the band column. The default is the existing spread —
`DocStackupFixtures` uses `BandLeft + BandWidth * (0.68 - 0.18 * slot)`, deliberately on the
right-hand side so a barrel never runs through the band names on the left. Generalise it: lanes spread
across the right-hand fraction of the band column, wrapping to a second pass rather than running off
the edge when there are more vias than lanes.

**Brief 5 makes that position user-settable and persists it.** Brief 1 only has to take an *optional*
per-via lane from `StackupSceneOptions` and honour it, so brief 5 is a field and a drag and nothing
else. Do not add the model field here.

---

## 5. Grippers

`StackupScene` places a small gripper rect at the top and bottom of every via barrel and emits them as
`ViaGripTop` / `ViaGripBottom` hits. Brief 5 drags them; brief 1 only places them.

They are placed and hit-tested here rather than in the control for `R-stk1-1`'s reason, and they are
**subtle**: the owner's word. Drawn only when the overlay says this via is selected or hovered
(`StackupOverlay.HoverLayer`, `StackupOverlay.SelectedLayer`), never on every via at rest.

The gripper hit rect is **larger than the drawn glyph** — a few pixels of slop on each side, stated as a
constant. A 4-pixel target that is exactly 4 pixels to hit is a target nobody hits.

---

## 6. Text: measured, padded, and never overlapping

### R-stk1-9 — no two label rects in a scene may intersect

The owner asked for padding around every label and no overlapped text. That is a *checkable* property,
so make it one.

Every label is measured with Skia against the face it will be drawn in — `SkiaFonts.PlexRegular`,
which is what `SchematicInlineEditBox` measures with and therefore what brief 4's inline editor will
line up against (`R-stk1-10`, below). The measured rect is inflated by a stated padding constant on
all four sides, and that inflated rect is what goes into `Labels` and into `Hits`.

Placement rules, in priority order:

1. A band's **name** goes on the band, left-aligned in the band column, if the band is tall enough for
   the padded text. Otherwise it moves to the label column ahead of the spec.
2. A band's **spec** (`3 µm thick, σ = 5.8e+07 S/m` / `100 µm thick, εr = 12.9, tanδ = 0.006`) goes in
   the label column on the band's vertical centre line.
3. When two label-column entries would collide — which happens the moment two thin bands are adjacent —
   they are **pushed apart symmetrically about their midpoint and joined to their bands with leader
   lines.** A leader is how a cross-section drawing has always solved this.
4. A via's name goes beside its barrel, and is pushed the same way.

**The test is the property, not the algorithm**: over every shipped technology, plus a generated stack
of 20 alternating bands at the thinnest thicknesses the unit can express, **no two entries in
`scene.Labels` intersect**. That test is what makes any future change to the placement safe.

### R-stk1-10 — editable values are their own labels, with their own hit rects

Brief 4 double-clicks a number and edits it. That needs the *value* to be separately addressable, not
the sentence it sits in. So a band's spec is emitted as **several** labels — a static piece, a value
piece, a unit piece — and only the value pieces carry a `StackupField` other than `None`.

Which fields are editable from the drawing, per kind:

| Kind | Editable labels |
|---|---|
| Conductor | `Name`, `Thickness`, `Sigma` |
| Dielectric | `Name`, `Thickness`, `Epsr`, `TanD`, `Mur` |
| Via | `Name`, `WallThickness` (plated fill only) |

`Span` is a pair of conductor names and is not free text — it is dragged (brief 5) and chosen from the
card's combo boxes, never typed into. Emit it as a `None` label.

Numbers are formatted **exactly as the card formats them** — `LayoutUnits.Format` in
`Technology.DefaultDisplayUnit` for a thickness, `StagedSigmaSm`'s own formatting for σ, invariant
culture throughout — so opening an editor over a value seeds the same string the card shows. A drawing
that rounds differently from the field it edits is an edit that changes a value the user did not touch.

---

## 7. Theme

`StackupRenderTheme.FromTheme(ColorTheme theme, ColorVariant variant)` plus a `Fallback`, on
`LayoutRenderTheme`'s exact pattern — same file shape, same `SK(ColorRole.…)` helper. Roles needed:
background, band edge, dielectric fill, ground-reference edge accent, label ink, on-band ink (fixed
dark, per §1), leader line, selection outline (reuse `ColorRole.LayoutSelection` so a selected band and
a selected shape in the layout editor are the same colour), gripper, and drag-ghost.

**Add roles to `ColorRole` only if none fits.** A new role has to be added to the shipped
`Default.ccolor` in `src/Render/Assets/Color/` in the same change, or it resolves to nothing, silently.

---

## 8. Firewall and tests

- `tests/Firewall.Tests` must stay green with no change: nothing added here may reference Avalonia,
  transitively included.
- New test file: `tests/Ui.Tests/Stackup/StackupSceneTests.cs`. It needs no window and no app host —
  building a scene is a pure function, which is the point.
- **Determinism gate (`R-stk1-11`)**: `StackupScene.Build` called twice on the same technology and
  width produces scenes whose `Hits`, `Bands` and `Labels` are element-wise equal, and
  `PlotExporter.BuildSvgString`-equivalent rendering of the two is byte-identical. A layout pass that
  depends on iteration order or on a dictionary's hash order produces a picture that moves between
  runs, and brief 7's clipboard export and brief 8's doc figures both depend on it not doing that.
- **No timing tests.** Assert structure and counts, never wall clock.

---

## 9. Scope

- No Avalonia, no control, no pointer handling, no view model. Brief 2.
- No model change. Brief 5.
- No clipboard. Brief 7.
- Do not touch `DocStackupFixtures` or any committed `.svg` figure. Brief 8 decides whether the doc
  figures adopt this renderer, and it does so with a measured diff in hand.

**On completion:** record findings in `src/Render/RESOLVED.md`. Never in a CLAUDE.md.

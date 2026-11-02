# Brief — harmonicaRF R8A: contours, iso-lines, and the Settings dialog

**Read first, in this order:**
`src/Ui/Views/Dialogs/HarmonicaAppearanceSettingsView.axaml` (all 91 lines) and its `.axaml.cs`
(`RefreshFromEditor` ~160, `OnFadeChanged` ~176, `OnIsoLabelsChanged` ~185),
`src/Ui/Views/Dialogs/HarmonicaAdvancedSettingsView.axaml` (all of it) and its `.axaml.cs`,
`src/Ui/Views/Dialogs/HarmonicaSettingsDialog.axaml`,
`src/Ui/Renderers/HarmonicaRenderTheme.cs:79–160` (the fade constants),
`src/Ui/Harmonica/HarmonicaColorEditor.cs:125–150`,
`src/Ui/Harmonica/Renderers/HarmonicaPanelRenderer.cs` (`DrawSmithPanel` ~186–265, `LayerAKey` ~395–425,
`DrawContours` ~629–663),
`src/Ui/DataDisplay/Renderers/ContourRenderer.cs:231–385` (`DrawIsoLines` and its label walk),
`src/Ui/DataDisplay/Renderers/PlotRenderer.cs:243–252` (the one call site),
`src/Ui/DataDisplay/ViewModels/PlotInspectorViewModel.cs:1241`,
`src/Ui/DataDisplay/Models/ContourData.cs:75–110`,
`src/Harmonica/ContourGrid.cs:46–70` (the D5 hole doctrine) and `625–712` (`InSupport`, `Raster`,
`Contours`, `RefineOptimum`'s own `InSupport` call at ~576),
`src/Harmonica/CircuitModel.cs:410–430` (the contour-kernel settings).

**Do NOT update any `CLAUDE.md`.** Record what is worth keeping in `src/Ui/RESOLVED.md` and
`src/Harmonica/RESOLVED.md` only. **No screenshot verification is required or wanted for this brief** —
gate on the tests §7 names and on the source facts each section states.

Tag new comments `R8A §n`.

---

## 0. What this brief is

Six owner items, all about the contour/iso-line layer and the dialog that configures it. Two of them
are real defects with root causes already located in this brief; four are defaults and layout.

| § | item | kind |
|---|---|---|
| 1 | Iso-line fade defaults become 0.01 / 3.00 | default |
| 2 | Every `§` reference disappears from the Settings dialog | text |
| 3 | Fade, iso-line labels and Tickle default move to the Advanced tab | layout |
| 4 | Iso-line labels never render — **two independent bugs**, one per app | defect |
| 5 | RBF default becomes Multiquadric / smooth 0.1 / epsilon 0.5 | default |
| 6 | Iso-lines must span a hole instead of breaking at it | defect + a doctrine reversal |

---

## 1. The iso-line fade defaults

`src/Ui/Renderers/HarmonicaRenderTheme.cs:103–104`:

```csharp
public const double DefaultIsoAlphaFloor    = 0.15;   →  0.01
public const double DefaultIsoAlphaExponent = 2.0;    →  3.00
```

That is the whole change. Both constants are already the single source: `HarmonicaColorEditor`'s two
properties fall back to them (`:132`, `:139`), `HarmonicaRenderTheme.Resolve` clamps against them
(`:153–154`), and `CharmAppearance.IsoAlphaFloor`/`IsoAlphaExponent` are `double?` whose null means
"take the built-in". So **a document that never touched the sliders picks the new values up on load,
and a document that did keeps its own** — which is the correct behaviour and needs no migration code.

Check the slider ranges still contain the new defaults before you finish: `AlphaFloorSlider` is
`Minimum=0 Maximum=1` (0.01 is fine) and `AlphaExpSlider` is `Minimum="0.05" Maximum="4"` (3.00 is
fine). `HarmonicaColorEditor.IsoAlphaExponent`'s setter clamps to `[0.05, 8.0]` — leave it.

---

## 2. Remove every `§` from the Settings dialog

The owner's words: *"Remove all '§' text from the harmonicaRF settings dialog. User does not care what
§ is."* This is about **user-visible text only** — the `§` cross-references inside C# and XAML
*comments* are the repo's own brief-numbering convention and stay exactly as they are.

Exactly three user-visible strings carry one today:

| file | line | now | becomes |
|---|---|---|---|
| `HarmonicaAppearanceSettingsView.axaml` | 49 | `Iso-line fade (§7.2)` | `Iso-line fade` |
| `HarmonicaAppearanceSettingsView.axaml` | 69 | `Tickle default (R-h9r2-18a)` | `Tickle default` |
| `HarmonicaAdvancedSettingsView.axaml` | 41 | `Contour surface (§3)` | `Contour surface` |

Line 69 has no `§` in it but is the same defect — an internal brief code leaking into the UI — and the
owner's complaint covers it. Both dialogs are moving in §3 anyway; make the text change as part of
that move rather than twice.

**Then grep the whole dialog tree for a fourth**, because this list was built by grep and a future one
must not reappear: `grep -n "§\|R-h[0-9]\|R7[A-D]\|R8[A-C]" src/Ui/Views/Dialogs/Harmonica*.axaml` must
return only lines inside `<!-- -->` comment blocks when you are done.

---

## 3. The tab split

Three groups move out of **Appearance** and into **Advanced**, in this order, at the bottom of the
Advanced tab under the existing "Contour surface" group:

1. **Iso-line fade** — the `α floor` slider row, the `exponent` slider row (`AlphaFloorSlider`,
   `AlphaFloorLabel`, `AlphaExpSlider`, `AlphaExpLabel`).
2. **Iso-line labels** — the `IsoLabelsCheck` checkbox.
3. **Tickle default** — the explanatory paragraph, `TickleDefaultEnabledCheck`,
   `TickleDefaultDbmBox`.

Appearance keeps the variant radios, the role list, the hex/pick/reset row, and the
Import/Export/Reset All footer — and loses both of its `<Border>` separators, which existed only to
divide off the moved groups.

**The move is a move of markup AND of the handlers that back it.** Those handlers live in
`HarmonicaAppearanceSettingsView.axaml.cs` — `OnFadeChanged`, `OnIsoLabelsChanged`,
`OnTickleDefaultChanged`, `OnTickleDefaultDbmKeyDown`, `OnTickleDefaultDbmLostFocus`, and the slices of
`RefreshFromEditor` that seed those five controls. Move them verbatim into
`HarmonicaAdvancedSettingsView.axaml.cs`; do not rewrite them.

**The one thing that will break if you move markup without moving state.** The Appearance view is
constructed with a `HarmonicaColorEditor` and a `HarmonicaViewModel` (see its `Initialize`/ctor and
`HarmonicaSettingsDialog.axaml.cs`, which wires `AppearanceTab` and `AdvancedTab` separately). The
Advanced view today is wired with the view-model only. The fade sliders and the labels checkbox both
write through `HarmonicaColorEditor` (`_editor.IsoAlphaFloor`, `_editor.ShowIsoLineLabels`), and
`ShowIsoLineLabels` additionally mirrors onto `_vm.ShowIsoLineLabels` (`.axaml.cs:187–188`) — **both
writes are required; dropping the second is how the toggle silently stops repainting.** So the
Advanced view now needs the editor too: extend its initialisation to take the same
`HarmonicaColorEditor` the Appearance tab gets, and have `HarmonicaSettingsDialog` hand the same
instance to both. One editor, two tabs — never two editors.

Tickle default goes through `HarmonicaTickleDefaults` (`src/Ui/Harmonica/HarmonicaTickleDefaults.cs`),
which is per-user state and touches neither the editor nor the view-model; it moves cleanly.

Grow `HarmonicaSettingsDialog`'s `Height` if the Advanced tab now overflows 580 — the Advanced grid is
a fixed `RowDefinitions` list, so add rows rather than nesting a second grid.

---

## 4. Iso-line labels do not render — TWO bugs, not one

The owner reports one symptom in two apps. They are unrelated defects with different causes. Fix both;
do not look for one shared cause.

### 4.1 harmonicaRF — there is no label-drawing code at all

`grep -n "showIsoLineLabels\|ShowIsoLineLabels" src/Ui/Harmonica/Renderers/HarmonicaPanelRenderer.cs`
returns five lines: the parameter on `DrawSmithPanel` (`:190`), it being forwarded (`:263`), the
private overload's parameter (`:286`), **its use as a field of `LayerAKey` (`:324`, `:420`)** — and
nothing else. `DrawContours` (`:629–663`) strokes polylines and returns.

So the toggle's entire observable effect today is **busting the Layer-A raster cache**. The menu item
works, `CharmAppearance.ShowIsoLineLabels` round-trips through `.charm`, the checkbox reads and writes
— and nothing is ever drawn. This is not a rendering bug to find; it is a feature that was wired end to
end except for the drawing.

**Do not write a second label placer.** `ContourRenderer.DrawIsoLines` already contains a working one
(`src/Ui/DataDisplay/Renderers/ContourRenderer.cs:290–381`): world-unit arc walk, per-ring stagger,
padded centred background box, baseline offset from the font metrics. Extract it as an `internal
static` helper on `ContourRenderer`:

```csharp
internal static void DrawIsoLineLabel(
    SKCanvas canvas, IReadOnlyList<...> pts, PlotRenderer.TransformSet tf,
    double level, double spacingWorld, int ringIndex,
    SKFont font, SKPaint labelPaint, SKPaint bgPaint, SKPaint bgStroke);
```

and call it from **both** `DrawIsoLines` (unchanged behaviour — this is a pure extraction there) and
harmonicaRF's `DrawContours`. Both renderers already use the same `PlotRenderer.TransformSet` type;
harmonicaRF calls `tf.PrimaryToCanvas`, Data Display calls `tf.ToCanvas(..., useSecondary: false)`, and
those are the same map on a Smith plot — pass a `Func<double,double,SKPoint>` projector into the helper
rather than picking one and making the other caller wrong.

harmonicaRF's own additions on top of the extracted helper:

- Label colour and background come from the harmonicaRF theme, not Data Display's `ContourData` —
  use `theme.Isoline` for the text and `theme.Background` for the box, so a user who recoloured
  iso-lines gets labels that match.
- **The label alpha must be the SAME ramped alpha the polyline got.** `DrawContours` already computes
  it (`IsoLineAlphaRamp.AlphaByte(...)` then `ScaleAlpha`); pass that byte through. With §1's new
  floor of 0.01 the low-rank contours are nearly invisible, and a fully-opaque label on an invisible
  line is exactly the artifact the fade exists to avoid.
- **Spacing.** harmonicaRF has no `LabelSpacing` setting and is not gaining one. Use the Γ-plane value
  §4.2 establishes below (`0.35` world units), as a `private const double IsoLabelSpacingGamma` in
  `HarmonicaPanelRenderer` with a comment pointing at §4.2's arithmetic.
- Labels are part of the **Layer A** raster (they depend on contours and chart size, not on marker
  positions), which is why `ShowIsoLineLabels` is already in `LayerAKey`. Draw them inside
  `DrawContours`, i.e. inside the cached layer. Leave the key alone — it is already correct, and was
  the only part of this feature that ever shipped.

### 4.2 Data Display — the default spacing is 5× larger than any Smith polyline

`ContourRenderer.DrawIsoLines` places labels by walking arc length **in world units**:

```csharp
double spacingW    = Math.Max(labelSpacing, 1e-6);          // :290
double targetArcW  = startFrac * spacingW;                   // :333, startFrac ∈ [0.15, 0.85]
...
while (targetArcW <= segEnd) { ...draw...; targetArcW += spacingW; }   // :345
```

`labelSpacing` arrives from `ContourData.LabelSpacing`, whose default is **30.0**
(`ContourData.cs:104`), and `PlotInspectorViewModel:1241` seeds a new contour trace with
`(plane == SurfacePlane.Z) ? 150.0 : 30.0` — so a **Γ-plane (Smith) contour gets 30.0 world units.**

On the Γ plane the entire world is the unit disc. The longest closed polyline that can exist there is
the rim, arc length 2π ≈ 6.28; a realistic iso-line runs 1–3. The first label is wanted at
`targetArcW ≥ 0.15 × 30 = 4.5` world units and every subsequent one 30 further on. **`segEnd` never
reaches `targetArcW`, the `while` body never executes, and not one label is drawn** — for every
contour on every Smith plot, at any zoom, regardless of `DrawLabels`. On a rectangular dB-vs-frequency
plot the axis spans hundreds of world units and 30 is sensible, which is why this was never noticed.

Two changes, both required:

**(a) A Γ-plane default that is in the right unit.** `PlotInspectorViewModel:1241` becomes a
three-way choice, with the Γ case at **0.35** — one label per ~1.1 rad of a rim-scale ring, ~5–6
labels around a full circle, which is the density the rectangular default achieves on its own axis:

```csharp
LabelSpacing = plane switch
{
    SurfacePlane.Z     => 150.0,
    SurfacePlane.Gamma => 0.35,    // R8A §4.2 — Γ world is the unit disc; 30.0 is 5× the longest
    _                  => 30.0,    //            polyline that can exist here, so nothing was drawn
};
```

Also fix `ContourData.LabelSpacing`'s own `= 30.0` default: it is the value a contour built by any
path that bypasses `PlotInspectorViewModel` gets. Leave the number at 30.0 but **have `DrawIsoLines`
never trust it blindly** — that is (b), and it is the part that actually makes this robust.

**(b) A spacing larger than the path can never silently produce zero labels.** Before the walk,
measure the polyline's own world arc length (you are already walking it; hoist a first pass or
accumulate `segLen` up front). If `spacingW > totalArcW`, place **exactly one** label at
`startFrac × totalArcW` instead of skipping the ring. Rationale, and put it in the comment: a user who
sets a large spacing is asking for *fewer* labels, never *none*; "none" is indistinguishable from
"labels are broken", which is precisely the report this section is answering.

`DataDisplayConfig.LabelSpacing`'s own `= 1.0` (`DataDisplayConfig.cs:311`) is a persistence default on
a different type and is **not** the one in play — do not change it while you are in the file.

---

## 5. The RBF defaults

`src/Harmonica/CircuitModel.cs`, the harmonicaRF settings record:

```csharp
public RbfKernel ContourKernel  { get; init; } = RbfKernel.Multiquadric;   // already correct — leave
public double    ContourSmooth  { get; init; } = 1e-3;   →  0.1
public double?   ContourEpsilon { get; init; } = null;   →  0.5
```

Mirror the two on `ContourGrid` (`src/Harmonica/ContourGrid.cs:89–91`), which carries its own copy as
the value used when nothing has set one; `Build` overwrites both from `ctx.Model.Settings` every run
(`:168–169`), so the copies only matter for a grid built outside a context — but they must not
disagree.

**`ContourEpsilon` stops being null-means-auto for harmonicaRF, and the dialog must say so.**
`HarmonicaAdvancedSettingsView.axaml:71` gives `ContourEpsilonBox` `PlaceholderText="auto"` and its
tooltip reads *"leave blank for Rbf2D's own auto value"*. That behaviour survives — a user may still
clear the box to get `Rbf2D`'s auto epsilon — but the **default is now 0.5, not blank**, so the box
comes up with `0.5` in it on a new document. Keep the placeholder and the tooltip; they now describe
an opt-in rather than the default.

`CharmIo` needs nothing: `ContourSmooth` deserialises as `s?.ContourSmooth ?? defaults.ContourSmooth`
(`:333`) and picks the new default up for any file that never wrote one. `ContourEpsilon` is
`s?.ContourEpsilon` with **no** `?? defaults` (`:334`) — change it to `?? defaults.ContourEpsilon` so an
older `.charm` that predates the field lands on 0.5 like a new one. A file that explicitly persisted
`null` and a file with no field at all are indistinguishable in that serializer, and treating both as
"take the default" is the behaviour every neighbouring field already has.

**Data Display's own contour defaults are NOT in scope** (`ContourData.InterpKernel`,
`DataDisplayConfig.InterpKernel` — both already Multiquadric; their smooth/epsilon live on the trace
card). The owner's item sits in a harmonicaRF round and names the Advanced tab's controls. If you
believe Data Display should follow, say so in your report; do not change it.

---

## 6. Iso-lines must span a hole

### 6.1 What is being reversed, and by whom

`ContourGrid`'s class comment (`:50–57`) states the current doctrine in bold:

> **Holes are thrown out, never extrapolated into (D5).** … an RBF over a scatter with a hole punched
> in it rings, and will happily invent an efficiency ridge where there is no data. … This is a
> correctness requirement rather than cosmetics: an invented ridge inside a hole is exactly the
> artifact this tool must never produce.

The owner is overruling that, with his reasoning stated:

> "the surface model still exists, so make sure that the surface model still covers the area over the
> hole so that the iso-lines still render near the hole. Yes, this is a form of 2D extrapolation, but
> it is more visually appealing and doesn't lose much fidelity. (Also the user knows the surface may
> be suspect there anyway because they can see the hole rendering.)"

**Implement it, and rewrite that class comment to record the reversal rather than deleting the old
text** — the argument against is still true, and the mitigation (the hollow hole dot is drawn, so the
suspect region is visibly marked) is what makes the trade acceptable. `DrawGridPoints` already draws
that dot (`HarmonicaPanelRenderer.cs:669–698`, `gp.IsHole` → hollow); note in the comment that the
reversal **depends** on it, so nobody later removes the dots and quietly leaves the extrapolation
unmarked.

### 6.2 The mechanism

`InSupport` (`ContourGrid.cs:644–656`) is two independent clips:

```csharp
if (!InsideHull(hull, re, im)) return false;                  // convex hull of CONVERGED points
foreach (var p in _points) { if (!p.IsHole) continue;
    if (dr*dr + di*di < holeRadius*holeRadius) return false; } // a disc around each hole
```

The **hull clip stays** — outside the measured grid there is genuinely nothing, and a contour running
off into unmeasured Γ is a different and worse artifact. Only the **per-hole disc** goes, and only for
the rendering path.

### 6.3 What must NOT change: the optimum

`RefineOptimum` calls `InSupport` too (`:576`), with the same hull and the same hole radius, so that
MXP/MXE can never be reported at a Γ where nothing converged. **That call keeps the disc.** Reporting
"the optimum load is here" from a point the solver never reached is a wrong *number*, not a cosmetic
gap, and nothing in the owner's item asks for it.

So the disc becomes a parameter rather than a constant:

```csharp
public bool InSupport(double re, double im, IReadOnlyList<Complex> hull, double holeRadius,
                      bool excludeHoleDiscs = true)
```

- `Raster(GridMetric, int resolution)` → new optional `bool excludeHoleDiscs = false`. Its own two
  call sites split: `Contours(...)` passes **false** (extrapolate — this is what the user sees);
  anything feeding an optimum search passes **true**.
- `RefineOptimum` and the seed scan around `:547–576` keep **true**, explicitly, at the call site, with
  an `R8A §6` comment saying why the two paths differ. A reader who finds two `InSupport` calls with
  different flags and no explanation will "fix" one of them.

`HoleRadiusFactor` and the public `HoleRadius` property stay — the hollow-dot renderer and the tests
both read them, and the optimum path still needs the radius.

### 6.4 The thing that will look wrong afterwards, and is not

With the disc gone, the raster is `fit.Evaluate(...)` everywhere inside the hull, so an iso-line will
now run *through* a hollow hole dot. That is the requested behaviour. What you must check is that the
RBF's ringing near a hole cluster does not produce a **closed contour that exists only inside the
hole** — a small invented island with no measured point in it. If it does at the shipped default
document's own hole cluster, report the picture in words and the level values; do not tune
`ContourSmooth` to hide it (§5 already set that number for a different reason).

---

## 7. Gates

Run, in this order:

```
dotnet build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Harmonica.Tests --no-build
dotnet test tests/RfCore.Tests --no-build
```

New tests, all in the routine (untagged) tier — none of this is near 5 s:

1. **`HarmonicaRenderThemeDefaultsTests`** (or extend the nearest existing appearance test):
   `DefaultIsoAlphaFloor == 0.01`, `DefaultIsoAlphaExponent == 3.00`, and — the part that matters —
   a `CharmAppearance` with both nulls resolves through `HarmonicaAppearanceBridge` to exactly those,
   while one carrying 0.15/2.0 explicitly still resolves to 0.15/2.0. That is the "an existing
   document keeps its own" claim, tested rather than asserted.
2. **A source-scan test for §2**, in the shape `tests/Ui.Tests` already uses for source scans (see
   `brief-harmonicarf-h8`'s own precedent, and **strip `<!-- -->` and `//` comments first** — a
   previous round's scan test failed on its own comments): no `Harmonica*SettingsView.axaml` or
   `HarmonicaSettingsDialog.axaml` non-comment line contains `§` or a brief code.
3. **A source-scan test for §3**: `HarmonicaAppearanceSettingsView.axaml` contains none of
   `AlphaFloorSlider`, `AlphaExpSlider`, `IsoLabelsCheck`, `TickleDefaultEnabledCheck`,
   `TickleDefaultDbmBox`, and `HarmonicaAdvancedSettingsView.axaml` contains all five. Crude, and the
   only mechanism available — `tests/Ui.Tests` may not instantiate an Avalonia control (its `.csproj`
   comment states the ban), which is why every dialog defect in this file's history was caught by the
   owner and not by the suite.
4. **`ContourIsoLabelPlacementTests`** — the real gate for §4, and it is a *pure* test because the
   extracted helper's placement arithmetic is separable from Skia. Assert against the extracted
   spacing walk, not the renderer: a ring of world radius 0.6 (arc 3.77) with `spacing = 0.35` yields
   ≥ 8 label anchors and every anchor lies within 1e-9 of the ring; **the same ring with
   `spacing = 30.0` yields exactly 1** (the (b) fallback), and — the regression that pins the actual
   bug — a pre-fix walk with `spacing = 30.0` yields **0**. Write that last one as the documented
   old behaviour in a comment, not as a live assertion of broken code.
5. **`ContourGridHoleSpanTests`** — build a small grid with a deliberate hole in the interior,
   `Raster(metric, excludeHoleDiscs: false)` has **no NaN cell inside the hull**, `Raster(...,
   excludeHoleDiscs: true)` has at least one, `Contours(...)` returns at least one polyline whose
   points straddle the hole centre (two points on opposite sides within one raster cell of the hole's
   own Γ), and **`RefineOptimum` still refuses to place an optimum inside the hole disc** — assert
   the returned optimum's distance from every hole exceeds `HoleRadius`.
6. **`HarmonicaContourSettingsDefaultsTests`** — `CircuitModel`'s default settings give
   `ContourSmooth == 0.1` and `ContourEpsilon == 0.5`; a `.charm` round-trip that never wrote either
   field comes back with both (this is the `?? defaults.ContourEpsilon` change in `CharmIo:334`, and
   it is the only way to catch it).

Report in your write-up: whether §6.4's invented-island case appeared on the shipped default document,
and the label count §4.2's new 0.35 actually produces on a real Γ-plane contour set.

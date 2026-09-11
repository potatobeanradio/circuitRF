# src/Render — resolved briefs (detail, off the CLAUDE.md growth path)

## ANT-10, 2026-09-10 — the 3D pattern viewer

`brief-antenna-10-pattern-3d.md`. A surface r(θ, φ) = the pattern in dB above a floor, over the upper
hemisphere, coloured by the same value, rotatable. Five files: `PatternSurface.cs` (the camera, the
named views, the grid, the mesh), `SurfaceResolve.cs` (cube → grid), `Renderers/SurfaceRenderer.cs`
(the canvas), plus the `PlotType` member and the CLI/inspector spellings.

### §2's gate — `PlotType`, not a new document kind, and the reason is `Table`

The brief asked for this to be answered **from the code and reported before the surface was built**,
because "`PlotType` today means *2D axes* throughout" and a fourth kind might need an `IsPlanar`-style
predicate threaded through a dozen call sites.

It does not, and **`PlotType.Table` is why**: it has no x axis, no y axis and no `Axes.Window` in the
sense the other three use one, and it has worked that way since long before this brief asked the
question. Every seam a plot kind passes through is an **early return or an additive `switch` case**,
never a two-armed `if` needing a third arm:

| seam | what `Table` does | what a 4th kind costs |
|---|---|---|
| `PlotRenderer.Draw` | early `return` **before** `BuildTransforms` | one more early return |
| `AxesRenderer` grid dispatch | `switch`, Table absent | one `case` |
| `Plot.Autoscale` | `if (PlotType == Table) return;` | one more |
| `Plot.SetAxesViewport` | `case PlotType.Table:` | one `case` |
| `PlotCanvasGeometry`, `PlotLabelStrips` | guarded by `IsComplex()` → false | **nothing** |
| `PlotTypeExtensions.IsRect` / `IsComplex` | both **positive** predicates over named members | **nothing** — a new member is false for both |
| `PlotComposer`, `PlotExporter` | 1 branch each | 1 each |
| `PlotControl` | 6 early-intercept blocks at the top of each handler | the same shape |

**The `IsPlanar` predicate the brief feared already exists, in the form of the two existing predicates
returning false.** `Table` costs 55 sites across `src`, the great majority of them booleans and
labels (`IsTablePlot`, `"+ Summary"`), not structure.

**One real trap, and it is the exception that proves the rule.** `Plot.SupportsComplex` is defined
`=> PlotType != PlotType.Rect` — the one **negatively**-defined predicate, so a new member inherits
`true` by accident. All twelve call sites are inside `Plot.cs`, in autoscale and window-sizing paths
that the new kind exits before reaching (the `Autoscale` early return is what makes that true, so it
is not luck — but it is the thing to route deliberately rather than inherit).

### The family mechanism would have drawn 101 of 181 θ rows, silently

A two-open-axis spec (`farfield.U[0, :, :, 1]`) already resolves today: the positional convention
makes the earlier kept axis a curve FAMILY. So the grid the surface needs was reachable with no new
resolve at all — and it was rejected, because **`Trace.MaxFamilyCurves` is 101** and ANT-11's 0…180°
θ axis at 1° is **181**. A surface missing everything past θ = 101° is a smooth, plausible, wrong
picture, which is the class of failure this whole series keeps refusing to draw.

`SurfaceResolve` pulls a rank-2 slice directly instead, and **finds θ and φ by NAME** on ANT-7's own
`PolarPatternAngle.TryDegreesPerUnit` test — the same rule the polar cut already applies to decide
whether an axis can be an angle at all. Two consequences worth keeping: the positional convention
never decides which of θ and φ is which, and a cube written `[phi, theta]` is read through the sliced
result's own axis NAMES rather than by position, because assuming the order would draw a transposed
surface of a different antenna.

### Triangles, not quads, and the seam rules

- **Two triangles per (θ, φ) cell.** A cell of a star-shaped surface is only *near*-planar, and a
  non-planar quad filled as one path folds visibly wherever the pattern turns quickly — which is at a
  null, the one feature the picture exists to show. Triangles also sort independently, which is what
  makes §6's "a lobe behind the origin" case come out right.
- **A hairline stroke in the FILL's own colour on every facet, and it is not decoration.** Two
  antialiased triangles sharing an edge each cover about half of the boundary pixel, so the background
  shows through between them as a lighter seam over the whole surface. The stroke closes it without
  drawing a wireframe on top of the data.
- **The φ seam closes on an OPEN axis and must not be closed again on a CLOSED one.** A grid sampled
  0…350° in 10° steps wraps; one sampled 0…360° already carries the duplicate column. Both are legal
  cubes, and the difference between them is a slit in one picture or a doubled seam drawn over itself
  in the other. `PatternSurfaceGrid.PhiWraps` decides it from the axis rather than assuming.
- **The degenerate triangles are real and are dropped, not drawn.** At θ = 0 every φ collapses to one
  point; so does any row the dB floor clamps to radius 0. On the 1° dipole with a −30 dB floor that is
  three θ rows, and dropping them accounts for exactly the 1,440 triangles by which the measured count
  falls short of the arithmetic one — a useful check that nothing real is being dropped.
- **Orthographic, and the basis is well conditioned at the poles.** `Right` does not depend on
  elevation, so a broadside view is an ordinary case here rather than a gimbal lock to special-case.

### The colour ramp default is NOT the contour renderer's own, and it was measured

§7 forbids a third colour ramp; `ContourColorMap` and `ContourColormaps.Sample` are reused unchanged,
so there is still one ramp family. **The DEFAULT could not be inherited.** A contour is drawn INSIDE
a framed chart, so a ramp ending at white or at black is bounded by the axes whatever the theme — the
contour's own default is `Bone`, black→white. A surface is not framed: it is a free shape on the page,
and **a facet the colour of the background has no edge at all**. Rendered on both themes, the shipped
`GistHeat` put a near-white peak on a white page and the surface almost vanished.

Of the thirteen ramps exactly two — **`Cool` and `Winter`** — have neither end at white nor near-black.
`Cool` is the default: its floor (cyan) is also bright enough to read on the dark theme, where
`Winter`'s pure blue is dim. Every other ramp stays available, is persisted in the `.cdd`, and is
reachable from `--color-map` and the inspector.

*(Unrelated and pre-existing, found while checking this: `plot --variant dark` draws dark grid and
text on a WHITE page. Every plot type does it, the polar plot included — it is the page background,
not the theme reaching the renderer. Not touched here.)*

### Frame cost, measured once (§6), and what the brief's own figure was

Release, drawing alone, a scratch harness rather than a Benchmark test:

| grid | triangles | full | decimated |
|---|---|---|---|
| 1° × 1° | 63,000 | 55.2 ms — **18 fps** | 4,050 tri, 4.4 ms — 225 fps |
| 2° × 2° | 15,660 | 14.1 ms — 71 fps | (not decimated) |
| 5° × 5° | 2,520 | 3.0 ms — 338 fps | (not decimated) |

**The brief's "1° × 1° is 16,200 triangles" is about 4× low** — 16,200 is the QUAD count of a 90 × 180
grid, i.e. 2° in azimuth. Over a full 360° at 1° it is 64,800 quads.

So the decimation §6 asks for is genuinely needed, and it is wired to the `PlotDetail` parameter that
`PlotRenderer.Draw` has always carried and that nothing had ever lowered: `PlotControl` sets `Quick`
while the pointer is down and `Full` on release. **The stride is derived from the grid's own size, not
from the detail level alone** — a 30° × 45° pattern is 8 cells and decimating it would turn a pattern
into a triangle — and **both endpoints of both axes are always kept**, so the silhouette and the peak
direction do not move between the two pictures. `Cli plot` and `Cli render` always draw the full grid.

Cost is set by the triangle count and barely by the canvas: halving the canvas in each direction moved
55.2 ms to 50.3 ms.

### What is reused rather than restated

- **The radius is ANT-7's.** `PolarPatternScale.Radius` maps dB to [0, 1] against the plot's own floor
  and reference, clamping below the floor rather than dropping — so a null and a clipped value look
  the same in 3D as they do on the cut, which is the point of not having a second scale.
- **The value is the trace's own `RectY`.** One transform, so a surface and a polar cut of the same
  trace can never be two different quantities.
- **The captions are `PatternCaption.Lines`**, including §4's hemisphere note, which is built from the
  cube's own axis and never written as a constant — hand it a 180° cube and the sentence changes and
  the surface closes underneath, with nothing to edit when ANT-11 lands.
- **The trace identity is `TraceLabeler.ComputeMinimalLabels`**, the label strip's own. A surface has
  no label strips (`PlotLabelStrips` is `IsComplex()`-only), so the identity goes in the caption block
  — which is also where it has to go for a second reason: the scene draws its own z axis at the top of
  the plot box and a label placed there collides with the letter.

### Two decisions that are deliberately literal

- **The principal planes are named by their own φ, not "E-plane" and "H-plane".** Which cut is the
  E-plane is a property of the antenna's polarization; the cube does not say it, ANT-6 computes it
  separately and can disagree, and a view button naming the wrong plane is a caption that is
  confidently wrong — worse than one that is merely literal.
- **One surface per plot.** Two opaque surfaces in one scene occlude each other and neither can be
  read; the comparison a user wants between two patterns is two cuts on one polar plot, which ANT-7
  already draws. A second surface trace is left resolved and undrawn rather than refused, so switching
  the plot back to Polar restores both curves.

### The dB options are live on a surface with no `--radial`

Found by the CLI gate. `--db-floor`/`--db-ring`/`--db-ref`/`--db-unit` were refused unless
`--radial db` was given — but they set the pattern SCALE, and a surface has one by construction (its
radius is dB above a floor and there is no linear reading of it to switch to). The rule is now "this
plot has no pattern scale at all", and the inspector follows it: the dB controls show on a surface,
the radial-MODE checkbox does not.

### Gates

`tests/Ui.Tests/DataDisplay/Pattern3DTests.cs` (13) — geometry, not pixels, all through the real
projection: an isotropic hemisphere is the unit hemisphere to 10 digits under `Broadside` (where the
basis makes X² + Y² + Depth² = x² + y² + z² a closed form, so it pins radius, direction, basis and
zoom at once); a half-wave dipole peaks at the horizon, nulls at the zenith, is rotationally
symmetric in its VALUES and projects onto exactly `ThetaCount` concentric rings; a lobe behind the
origin is painted facet 21 of 862 at depth −0.864 and, with the camera turned 180°, facet 830 of 862
at +0.835.

`tests/Ui.Tests/Cli/Pattern3DCliTests.cs` (14) — `plot` and `render` byte-identical on all four named
views and on an arbitrary rotation and zoom, both themes genuinely different pictures, three small
panes, and the refusals.

## Owner report, 2026-09-08 — the Polar grid only ever looked right at unity

`AxesRenderer.DrawPolarGrid` took its ring radii from `axes.Ticks(true).MinorX`. That tick set is
built for a RECTANGULAR axis, and `MinorX` is the lattice with every MAJOR multiple **removed** —
correct when the majors are drawn separately as their own darker gridlines, and wrong as a set of
radii, because nothing draws them here. The ring sequence therefore had a hole in it wherever a
major tick fell.

It went unnoticed because the unit circle is the one window where the arithmetic comes out even:
width 2 gives a 0.2 minor spacing and a major multiplier of 2, so the surviving radii are 0.2, 0.6,
1.0 — three evenly spaced rings with the outermost exactly at the frame. Nothing about that is a
property of the code; it is a coincidence of that one window. Off unity it failed three different
ways at once:

| window | rings drawn |
|---|---|
| ±1 | 0.2, 0.6, 1.0 — even, and the boundary is on the lattice |
| ±1.2 | 0.2, 0.6, 1.0 — **no ring at the frame at all**, and nothing says the radius is 1.2 |
| ±1.5 | 0.2 … 1.4 with **1.0 missing**, six rings, no frame |
| ±2 | 0.2 … 1.8 with **1.0 missing**, eight rings, no frame |
| ±3 | 0.5, 1.5, 2.5 — half-integers only, no frame |

**The rings are computed from the framed radius now, not from the tick set.** `PolarRings(rMax)`
picks the nearest 1/2/5 decade to `rMax / 5`, every multiple of it is drawn, and the boundary ring
at exactly `rMax` is always drawn and always labelled — so the ring COUNT is roughly the same at
every scale, which is the property that was missing. A window is a square centred on the origin on
every complex plot type (`Plot.SquareCentredOnOrigin` serves both `Plot.Autoscale` and the
axis-limits flyout), so its half-width IS that radius; it is taken from the X edges because the
rings are drawn in X pixels.

Three details are load-bearing and each came from a case that looked wrong on screen:

- **The nice-number thresholds are nudged in by a tolerance**, because the mantissa is a QUOTIENT and
  a decade boundary does not survive one: `rMax = 1.5` gives `m = 2.9999999999999996` and
  `rMax = 0.75` gives `m = 1.4999999999999998`, so a bare `m < 3` / `m < 1.5` takes the finer branch
  and the plot comes back with seven rings and a tick comb at twice its neighbours' density — for a
  radius one digit long.
- **A multiple landing within two fifths of a step of the frame is dropped**, not drawn beside it.
  `rMax = 12.5` has 12 on its lattice, half a step from the edge, and two circles that close read as
  a rendering fault rather than as a grid. A quarter of a step was not enough for that case.
- **Minor radii are short ticks ACROSS the two diameters, not rings of their own.** Five
  subdivisions of five rings is twenty-five circles, which stops reading as a grid and starts
  reading as shading; on the axes they give the same fine reading at a fifth of the ink. They are
  suppressed below 4 canvas pixels of spacing — absolute pixels, because this is a question about
  what the eye can separate, and a comb finer than that is a grey band however thin its strokes.

The radius numbers now carry the axis's SI prefix through the same `EngineeringFormat` path the
rectangular ticks use (one group for the whole axis, never one per ring — `G4` printed `0.0002` on a
milli-scale plot), sit clear of the tick comb rather than on the axis line, and are dropped
**outwards-in** where the lattice is too tight to label completely, so the boundary number — the one
that says what scale the plot is at — is the one that survives.

### The outline was clipped where it touched the box — on the Smith chart too

Both grids clip to `PlotRenderer.ViewportClipRect`, and both draw an OUTLINE whose radius is on the
boundary that clip is cut to, so the outer half of that stroke was taken away. Measured as ink
across the stroke on a 900 px polar render: 224 and 232 at 45° and 135°, **112 at the top and 94 at
the bottom** — half a line, exactly at the four points where the disc is tangent to the box.

**The Smith chart had a second, worse instance** running all the way round rather than at four
points: with the window at exactly the unit square the grid is also clipped to a `ClipPath` of the
unit circle, and the `r = 0` circle IS that circle — so the chart's outline was half-width
everywhere, which is why it read lighter than the constant-R circles inside it.

**Widening both clips fixes the outline and is the wrong fix**, which the first attempt did and an
owner caught the same day: the reactance arcs then ran past |Γ| = 1 by their own thickness. An arc
meets the boundary at a shallow angle, so a stroke's width of RADIAL slack shows up as a much longer
tail along the edge — an arc leaves the disc visibly where a circle concentric with it would not.

**The outline is separated from the grid it bounds instead.** The arcs keep the exact clips they
always had; the outline is drawn afterwards, outside them, under a box one stroke wider — it is the
only thing that needs the slack, and it needs it precisely because it sits on the boundary. Polar
got the same split, where it also stops the diameters' square end caps poking a pixel past the
frame. After: 222 / 210 / 213 around the polar ring, and 23 near-white antialiasing pixels beyond
the Smith disc against 0 before, none of them an arc.

### Every crossing on the Smith grid was darker than the lines that made it

The constant-R and constant-X arcs were each stroked at `MinorTransparencyScale`, so an overlap
composited: two 50 % strokes read 75 %, three read 87.5 %. Sampled on a 900 px render, a plain arc
is **208** and the r=1 × x=1, r=1 × x=2, r=2 × x=1 and r=0.5 × x=1 crossings were **185, 185, 185,
187** — so the grid was darkest exactly where it is busiest, and the chart read stippled rather than
ruled.

**The family is drawn into one `SaveLayer` at full opacity and composited once.** Inside the layer
an overlap is the same colour as a single stroke, because opaque over opaque is opaque; the layer's
own alpha is then the transparency that was wanted. Every one of those samples reads 207-208 now.

**The outline and the real axis stay out of the layer, at full strength.** They are the chart's frame
and its reference rather than grid, they are meant to be the darker lines, and putting them inside
would have faded them to the arcs' tone.

### The Smith chart's numbers did not shrink with the chart

Reported the same day, against wide axis limits: past about ±5 the grid numbers ran together into a
blob. The grid of a Smith chart is FIXED in the Γ plane — its arcs ARE the unit disc's — so unlike a
polar plot's rings it cannot be re-latticed for a wider window. Zoom out and the whole chart is just
a smaller object in a bigger box. Every part of it scaled with the window except the numbers, whose
size came from the CANVAS (`FontSizeTicks * lw`), so the anchors closed on each other at constant
text size: crowded at ±2, illegible by ±5, a smudge at ±10.

**The text scales with the disc, and what still will not fit is dropped.** Both halves are needed.
The scale is `2 / Window.Width` — the disc's radius runs as 1/(half-window), so that IS the chart's
own zoom, and it is exactly 1 at the unit window, where the picture is byte-identical to before
(checked at two canvas sizes). It comes from the window's WIDTH rather than from the measured disc
radius so that panning, which moves the disc without resizing it, cannot change the size of a
number. Below `SmithLabelMinScale` (0.45) it stops shrinking, because a face much under half the
tick size is texture rather than text — and past that floor no size makes room, so a label landing
on one already placed is dropped instead.

Three details:

- **The thinning test is a rect overlap, not `DrawPolarGrid`'s one-dimensional "taken so far" edge.**
  A Smith chart's numbers crowd in two dimensions: the reactance labels come down the outside of the
  disc towards the same r → ∞ point the resistance numbers run into.
- **The placement ORDER is the priority, and the unit circle goes first.** Ascending order alone
  dropped `1` — the chart's own centre — because `0.5`'s label reaches it first. r = 1 and x = ±1 are
  placed before anything else; the rest run outwards-in from the roomy r = 0 side, so what is lost is
  what piles into r → ∞.
- **Under `SmithLabelMinDiscFaces` (4) font sizes of disc radius the grid carries no numbers at
  all.** A number is about two font sizes wide at the floor scale, so a disc smaller than that is one
  a single label would very nearly span; thinning would leave one arbitrary survivor sitting on a
  smudge, and nothing is the better answer.

The old `axes.Window.Width > 3` rule — which dropped the two 10s and nothing else — is gone: it was a
two-step approximation of a continuous problem, and at ±2 it threw away numbers that now fit,
because the text is half the size there.

harmonicaRF's Smith panels draw through this same code with the default unit window, so they are
untouched.

### A Y-axis label rendered a rectangle — and THREE renderers draw one

Skia draws a glyph the typeface lacks as NOTDEF — a box — and substitutes nothing. Checked against
the shipped `cmap`s rather than assumed: of the characters these labels can carry, **IBM Plex lacks
U+25B8 `▸`, U+2220 `∠`, U+2225 `∥`, U+25CF `●` and U+25B2 `▲`, and HAS U+2192 `→`**. So in
`WSProbe GATE→DRAIN ▸ block` the arrow was never the problem and the group separator beside it
always was — the report called it "the -> glyph", which is what a small right-pointing triangle
looks like.

`RendererText` already splits a string into runs by coverage and falls back to DejaVu; the marker
info box and the Table have used it for `∠` since they were written. Three places draw a Data
Display axis label and **none of them did**:

| renderer | what it draws | reached by |
|---|---|---|
| `AxesRenderer.DrawTitleAndAxisLabels` | the title, the X label(s), the Y label in the Skia margin | a Rect plot's first left/right trace, or a custom label |
| `AxisLabelControl` (`src/Ui`) | the per-trace label STRIPS beside the plot | **every Smith/Polar trace, and Rect traces past the first** |
| `PlotComposer` | those same strips, in an exported document | Export / `render --data` |

**Fixing only the first fixed nothing the reporter could see**, and that is the finding: a WSProbe
trace's label lands in the strips, which are a different renderer in a different project, reached
through an Avalonia custom draw operation rather than through `AxesRenderer` at all. All three go
through the fallback now. The title's shrink-to-fit resizes both faces together — measuring with one
size and drawing with another is how a centred string ends up off-centre.

Two text sites were checked and deliberately left alone: `ContourRenderer`'s level labels and marker
letters are CENTRE-aligned and numeric, and `RendererText`'s run splitter is exact only for
left-aligned text; and `PlotRenderer`'s VSWR readout can only emit `∞`, which Plex covers.
`HarmonicaPanelRenderer`'s own rotated Y2 label has the same shape and the same gap, but it draws
harmonicaRF's labels rather than Data Display ones — it is not this report and was not changed.

The gate is `tests/Ui.Tests/DataDisplay/AxisLabelGlyphFallbackTests.cs`, which asserts the coverage
facts above against the shipped faces, asserts the fallback changes the drawn PICTURE for the
reported label while leaving an ASCII one byte-identical (a splitter that measured with DejaVu and
still drew with Plex would leave the box on screen and pass every width assertion), and scans the
two renderers that cannot be driven without a canvas. **Load a face for an assertion through
`SkiaFonts.RealFace`, never `RealPlexRegular`** — the latter hands back the lazy cached instance the
renderers draw with, and disposing it takes the typeface out from under the whole process; it
crashed the test host here before the test ever ran.


## Owner report round, 2026-09-08 — the plot scales and the wsp matrix's indices

Three reports whose fixes live below the firewall. `src/Ui/RESOLVED.md` carries the two that do not.

### A Polar plot could not be scaled inside the unit circle

`Plot.AutoscaleEnforceUnityMinimum` — on by default, never set anywhere — unioned every complex-plane
autoscale with the unit square. That is right for a Smith chart, whose grid IS the unit disc: a Smith
chart framed smaller than |Γ| = 1 has no boundary. It is wrong for a Polar plot, which carries
whatever the trace is — ohms, siemens, a loop gain — and the floor made every small quantity a dot on
the origin, which looks exactly like a trace that is not being drawn. The WSProbe chapter's `1/H0`
locus (about 0.05 S) beside `1/Y0` (10-30 Ω) is the case that surfaced it.

**`UnityMinimumApplies` is the property AND `PlotType != Polar`.** Two consequences of taking the
floor away that were not obvious until it was gone:

- **`paddingComplex` existed and was applied to nothing.** The complex branch of `AutoscaleCore` read
  neither `padX` nor `padY` — the union with unity always left slack, so nothing missed them. Without
  the floor a locus would have touched the frame, so the Polar branch now runs `EnsureMinExtent` and
  the same `InflateRect` padding the Rect branch has always used. The constant was already there and
  already 0.02.
- **The radius numbers were gated on `axes.Window.Width < 8`** in `AxesRenderer.DrawPolarGrid`, which
  is safe only while a Polar plot cannot frame much more than the unit circle. A 59-unit-wide window
  drew a grid of unlabelled rings with nothing anywhere to say what scale it was at. The gate is now
  the canvas-width one alone.

### The `wsp` matrix's `row`/`col` read 0-based everywhere a user met them

`WspCubePacker` writes those axes with 1-based VALUES (`k + 1`) precisely so the reference document's
`wsp(r, c)` needs no index arithmetic. Only `i`/`j` — the S/Y/Z port axes — were being READ that way,
so the trace card offered `mag(SP1.wsp[:, 0, 0])` for the first element and the Y-axis label read
`SP1.wsp(row=1,col=1)`.

**Four sites, and they must move together** — the same rule `i`/`j` are already under:
`SliceTokenParser` (typed slice), `Trace.BuildPickerYExpression` (what the picker writes back),
`TraceLabeler.BuildCubeQuantity` (positional, so it reads `(2,1)` exactly as `S(2,1)` does), and
`Evaluator.ResolvePin`, which is the measure-line and expression path and resolves by axis VALUE
rather than by arithmetic. `TraceResolve.ApplyPinnedAxisDisplay` skips them for the same reason it
skips `i`/`j`: the label is positional, so a display token built there could never be used.

Note this is only ever the `wsp`/`wsp_passive` matrices — nothing else in the repo names an axis
`row` or `col`, and both cubes are 1-based by construction.

**A WSProbe TRACE was never affected**, and that is worth separating: `WspMetrics.PinToken` returns
null for `row`/`col`, so a `1/H0 @ GATE` trace never printed them at all. This was the plain-cube
route — picking `SP1.wsp` out of the analysis group and pinning the two axes by hand.

Gate: `tests/Ui.Tests/DataDisplay/WspMatrixIndexingTests.cs` and `PolarScaleTests.cs`.


## RND-1 — the render layer below the UI firewall (2026-09-07)

`docs/sonnet-briefs/brief-render-1-render-layer-below-the-firewall.md`. `CircuitRF.Render` exists,
`src/Ui` and `src/Cli` both reference it, `tests/Firewall.Tests` gates it, and the application looks
and behaves exactly as it did. Every visible feature is RND-2 onward.

### The measured closure, against the brief's prediction

§2's table predicted the renderers reach four `src/Ui` namespaces and named the types. The compiler was
used as the oracle rather than a grep: the files were moved, and each error was resolved by moving the
one file it named or by reporting what blocked it. The result is **larger than the table in eight files
and smaller in one**, and the differences are worth knowing about because each one says something about
where the seam actually is.

**Came down, and the table did not name them:**

| File | Why the renderer needs it |
|---|---|
| `LayoutGridMath.cs` | the grid pitch and the ruler tick step ARE the drawing |
| `LayoutShapeEditing.cs` | `XyOf` / `IsClosed` — how a shape's vertex list is read |
| `LayoutScaleHandles.cs` | `Build(bb)` — where the eight scale grips sit |
| `LayoutPortDirection.cs` | the conductor lookup a port arrow's direction and length come from |
| `LayoutSnapFeatures.cs` | declares `SnapCandidate` and `SnapFeatureKind`, which the snap overlay draws |
| `LayoutSnapQuery.cs` | `SnapQueryCounters` is in `LayoutSnapFeatures.QueryNear`'s signature |
| `LayoutSnapCandidateSet.cs` | in `LayoutSnapQuery`'s |
| `LayoutFlattenToPolygon.cs` | `LayoutPortDirection` measures a port's edge on the flattened outline |

None of them is a view model, a command or a canvas — they are geometry the renderer and the editors
share, which is the same category the brief's own table is made of. **`LayoutSnapFeatures.cs` is the
one worth a second look**: what the renderer needs from it is two type declarations at the top of the
file, and what came with them is a 615-line snap INDEX plus its two query files. R-rnd1-2 forbids
reshaping a file on the way down, and splitting a type out of one is reshaping it, so the whole file
moved. If `src/Render` is ever felt to be carrying editor machinery, this is the thing to look at
first — and the fix is a file split done deliberately, not a move done quietly.

**Did NOT come down, and the table said it would:**

- **`HarmonicaRenderTheme.cs`.** The table records it as Avalonia-free (true) and says to move it as a
  neighbour. It has a `ToPlotTheme` that returns `CircuitRF.Ui.DataDisplay.RenderTheme` — and the Data
  Display is **RND-4's** carve-out, explicitly separated so a hard half cannot hold an easy half
  hostage. Moving it now would either drag `RenderTheme` across (RND-4's job) or split the file
  (R-rnd1-2 forbids it). Nothing in `src/Render` references it; every caller is under
  `src/Ui/Harmonica/`. **It should move with RND-4, in one step, with the Data Display's own renderers.**
- **`ClipboardRenderPolicy.cs`.** Listed in §2's `CircuitRF.Ui.Theming` row. It reads
  `AppPreferencesIo` — the per-user preference store the same row says stays in `src/Ui` — and no
  renderer names it; its callers are the three clipboard copy paths. It stayed, on `AppPreferences`'
  own terms.

### R-rnd1-3: the one thing that would not come cleanly, and what was done instead

**`CellPins.Resolve` reaches `PCellRegistry`**, and the brief's grep did not see it because the grep was
over the renderers and this is one level down. `CellPins` itself had to move — `LayoutRenderer`
draws a sub-cell instance's pin markers through it, and so do `LayoutPortDirection` and
`LayoutSnapFeatures`, which the renderer also reads.

Behind `PCellRegistry` sit the six built-in generators, `Wire/`'s out-of-process worker transports and
Python-interpreter discovery, and **`PCellTrust`, which reads a per-USER preference through
`AppPreferencesIo`** — plus `GeneratedCellStore`, which reaches `CircuitRF.Ui.Schematic`. So the whole
of `src/Ui/Layout/PCells` (26 files, 6,783 lines) is Avalonia-free at the source level and could
*compile* below the wall, but two of its files are exactly the kind the brief says stays.

**What was done: `CellPins.GeneratorSource`, the same seam `LayoutTextOutline.TypefaceSource` already
is**, installed from `src/Ui` by a module initializer (`UiPCellGeneratorInstaller`, beside
`UiTypefaceInstaller`, which exists for the same reason and is documented there). Every frame the
application draws resolves generators exactly as before.

**What a headless process therefore loses, stated precisely rather than as a caveat:** `CellPins`
answers in two branches — the cell's own persisted pin list, else re-invoke its generator. A generated
PCell cell is an ordinary cell folder on disk and its pins ARE persisted, so branch one answers for
every such cell written since pins were persisted. Only a cell written BEFORE that loses its pin
markers, and it renders without them rather than with wrong ones. **The follow-up that would close it
is to move the built-in generators to `src/Design/Layout/PCells`, where `PCellContract` and the
`PCellGenerator` delegate already live** — blocked today by exactly two files, `Wire/PCellTrust.cs` and
`GeneratedCellStore.cs`.

### R-rnd1-4 — the fonts, and why the fix is not "it now works headlessly"

`SkiaFonts.Load` went through `Avalonia.Platform.AssetLoader`, which throws
`InvalidOperationException: Unable to locate 'Avalonia.Platform.IAssetLoader'` with no live app host —
and it **caught that and returned `SKTypeface.Default`**. Nothing errored; the picture was simply drawn
in a different typeface at different metrics, and reported as a success.

The nine faces `SkiaFonts` actually loads are `EmbeddedResource`s in this project now, read with
`Assembly.GetManifestResourceStream`, which needs no platform at all — the same fix `ShippedTechnologies`
and the `reference` pages in `src/Design` already use, for the same reason.

- **Nine faces, not the folder.** The folder is 15 MB; the faces `SkiaFonts` names are 3.8 MB of it.
  The rest exist for Avalonia's own `FontFamily` resources and `WindowsClipboard`'s RTF font table,
  which reach them through the `AvaloniaResource` link. Embedding all of them would have put 15 MB in
  two assemblies. **The consequence is a rule: add a face to `SkiaFonts` and add it to the `.csproj`
  in the same edit** — a named-but-not-embedded face resolves to `SKTypeface.Default`, silently.
- **`src/Ui` LINKS the files back, it does not copy them.** Its include is a blanket `Assets\**`, so
  the two link items had to be added deliberately, and the published output was checked rather than the
  `dotnet run` one: this is the class of build-file change that works in one and fails in the other,
  which is how `tools/pcell-python` shipped broken once.

### R-rnd1-5 — the theme, which is the same bug in a different costume

`ThemeResolver`'s chain is workspace dir → user themes dir → built-in provider → `ColorTheme.BuiltIn`,
and step 3 was installed only by `App.axaml.cs` (and the two standalone `App`s), out of
`avares://CircuitRF.Ui/Assets/Color/`. With no app host it was **never installed**, so a theme name that
resolves in the GUI fell silently through to `BuiltIn`.

The provider now DEFAULTS to a framework-free reader over this assembly's own resources, and the three
`App` classes no longer register one — a second copy of the same file reached a second way, shadowing a
default that works everywhere, is the arrangement that produced the bug.

**One trap this created, and it bit immediately:** `ThemeResolverTests` restored the provider by
installing a no-op (`SetBuiltInProvider(_ => null)`), which was correct when the default WAS nothing and
is now a way for one test to make every later test in the process resolve every theme to `BuiltIn`.
`ThemeResolver.ResetBuiltInProvider()` exists for that, and its doc comment says why.

### What gate 2 can be at RND-1, measured rather than assumed

§5.2 asks for byte identity between a render driven from a project with no Avalonia reference and the
GUI's own clipboard export. **The literal cross-process form is not constructible until RND-2**, for two
independent reasons, and both were measured:

1. **There is no second process to run it in.** `src/Cli` references `CircuitRF.Render` from this brief
   on, but has no verb that draws.
2. **A same-process comparison proves nothing.** Both sides call the same `SchematicRenderer.Draw` in
   `CircuitRF.Render` with the same `SkiaFonts`, so a typeface difference — the one failure mode gate 2
   names — cannot appear between them.

So the gate was built in the form that does bite, in
`tests/Ui.Tests/Render/RenderLayerBelowFirewallTests.cs`: **Skia's SVG device writes the typeface's
family name into every text run, so the exported file SAYS which face drew it.** The application's own
clipboard export is asserted to name `IBM Plex Sans` and NOT `SKTypeface.Default`'s family — which is
gate 3's claim through the production path, and is exactly what a headless render could not have
satisfied before R-rnd1-4. A vacuity guard asserts that `AssetLoader` still fails in that process, so
the assertion is known not to be true for the wrong reason.

**Two measurements taken while building it, both worth keeping:**

- **In-process byte identity IS available for the schematic and the symbol, and is NOT for the layout.**
  Two identical renders come back byte-identical for the first two; the layout differs in exactly one
  thing — Skia's `clipPath` id (`cl_3` vs `cl_4`), from a counter its SVG device does not reset per
  canvas. It appears there and not in the other two because only the layout renderer emits a clip.
  `LayoutClipboardExportTests`' note about that counter is right but narrower than it reads. The id is
  normalised only where the raw bytes actually differ.
- **The PDF needed NO exclusion.** §5.2 predicted a `SKDocumentPdfMetadata` creation date would have to
  be excluded by name; two renders of every fixture came back byte-identical without one. The stripper
  is written and applied only on a difference, so if a stamp ever appears it is handled and named rather
  than silently tolerated.

### The finding about a shipped verb, and why it turned out NOT to be optional

**R-rnd1-4 says: if wiring `LayoutTextOutline.TypefaceSource` to the new embedded loader would make
`convert`'s label flattening match the GUI's, say so and leave it — a finding, not this brief's
business. The measurement inverted that. LEAVING it is what changes behaviour.**

The seam was filled by `UiTypefaceInstaller` in `CircuitRF.Ui`, from `SkiaFonts.Plex*`. Before
R-rnd1-4 those properties fell back to `SKTypeface.Default` in every process, so the application and
`circuitrf convert` both flattened labels against the substitute and **agreed by accident**. Fixing
`SkiaFonts` moved the application's side to real IBM Plex and left the CLI's on the default, and
`ConvertCliVerbTests.ConvertingAClayToGerber_WritesWhatTheApplicationsOwnExportWrites` — which
compares the two byte for byte — failed on the label's flattened coordinates (`X35400` vs `X37012`).

So the installer moved to `src/Render` as `RenderTypefaceInstaller`, and the two sides agree again,
on the real face. `convert` now produces the glyph outlines the application produces. **Three things
had to be got right and each is a trap on its own:**

1. **A `[ModuleInitializer]` in a referenced-but-untouched assembly never runs.** .NET loads an
   assembly on first use, and `convert` reaches `GerberExport` in `CircuitRF.Design` without ever
   naming a `CircuitRF.Render` type. `CliEntry.Run` calls `RenderTypefaceInstaller.Install()`
   explicitly; the call is what loads the module, and it is greppable where a load-order accident is
   not.
2. **The installer reads `SkiaFonts.RealPlex*`, not the public properties.** Those honour
   `SkiaFonts.TestOverrideTypeface`, and label GEOMETRY is now compared against a separate process
   that has no override to read. `LayoutTextOutline` keeps its OWN override, which is the one the
   label tests actually mean.
3. **`CA2255`.** A `[ModuleInitializer]` in a library is a warning, and this project builds with
   `TreatWarningsAsErrors`. Suppressed at the declaration with the reasoning, which is that the rule's
   concern — a library surprising its consumer with work at load time — is the opposite of what this
   does.

**The wider point, and the follow-up it implies:** `LayoutTextOutline.TestOverrideTypeface` exists
because the embedded faces could not load headlessly. That reason is gone. What the field now selects
is a DIFFERENT FACE rather than "a face at all", so ~11 more test classes had to join
`LayoutTextOutlineTypefaceCollection` to stop them changing each other's glyph geometry. **The real
end state is to delete that override and let those tests use the real face** — a change to a lot of
fixtures, and its own piece of work.

### Two smaller things

- **`src/Design` grants `InternalsVisibleTo("CircuitRF.Render")`**, for the grant `src/Ui` already had
  and for the same reason: the renderers reach `CellHierarchy.LayoutBaseDirOf`,
  `LayoutFlattener.FlattenOpenEdgeList` and `LayoutTextOutline.ResolveLabelAnchor`, all `internal`.
- **`ComponentPreviewRenderer` split where it was already split** (R-rnd1-6): the `SKBitmap` halves are
  `CircuitRF.Render.ComponentPreviewRaster`, and `src/Ui` keeps a `ComponentPreviewRenderer` holding
  only `Wrap` and the two `Render*` methods. The Render-side class is deliberately NOT called
  `ComponentPreviewRenderer` — `src/Ui` has a `global using CircuitRF.Render`, and two types of one name
  in scope is an ambiguity error at every call site.


## RND-2 — what the `render` verb needed from this project (2026-09-07)

`brief-render-2-render-verb.md`. The verb itself is `src/Cli/Render.cs` and its findings are in
`src/Cli/RESOLVED.md`; three things landed here.

- **`SvgFontNormalizer.cs` came down from `src/Ui/Diagnostics`**, beyond RND-1's measured closure. It is
  framework-free and it was already on every SVG path in the repository — the three clipboard exports,
  the plot exporter and wBond's all pass Skia's output through `RepairPositionLists`, because Skia
  writes each text run's per-glyph position list with a trailing separator that Firefox reads as invalid
  and drops. Leaving it up there would have made the headless SVG differ from the application's by
  exactly that defect: correct in Chrome and Safari, unreadable in Firefox, reported as a success.
  `SvgPostPass` stayed — it is the docs generator's size pass, reaches `System.Xml.Linq`, and is not a
  renderer's concern.
- **`LodPixelThreshold` and `MergeShapeCountThreshold` grew the "off" branch the other six tier knobs
  already had.** `LayoutRenderOptions` documents that "a NEGATIVE value disables the tier outright,
  which is how an export pins exact vector geometry", and those two read `> 0 ? value : default` — so a
  caller asking for the tier to be OFF silently got the DEFAULT, in the one direction where the mistake
  produces a plausible picture of less geometry than the document holds. `EffectiveLodPixelThreshold`
  and `EffectiveMergeShapeCountThreshold` are the fix; nothing passed a negative before, so no existing
  caller changed behaviour.
- **`LayoutRenderResult.VerticesEmitted`**, on `PathsConstructed`' own terms and for its own reason —
  see that field's doc comment for what it deliberately does not count, and `src/Cli/RESOLVED.md` for
  the measurement that made it necessary.

`InternalsVisibleTo("CircuitRF.Cli")` was added for `MeasureLabelWorldBbox` (a label's stored bbox is
its ANCHOR, and framing on that is what cropped ports off a pasted page) and
`LayoutRenderDetail.ToleranceDbu` (the effective decimation tolerance the verb REPORTS). A second copy
of either measurement would be free to drift, silently.


## Post-RND-5 review — the LOD budget disagreed with the renderer about an undeclared layer (2026-09-07)

Found while reviewing the whole render series, not looked for. Older than the series: it moved here
from `src/Ui/Renderers` unchanged.

`LayoutRenderDetail.CanAffordOutlines` estimates a frame's outline cost by summing the vertices on
layers that will be painted. It built a set of **visible** keys from the technology and skipped any
shape whose key was not in that set — so a shape on a layer the technology **does not declare at all**
was not counted.

`LayoutRenderer.Draw` draws that shape. An undeclared key resolves through `FallbackPalette.For`,
whose `Visible` is `true`, and it is painted like any other. So the two disagreed, in the direction
that costs: the budget undercounted, and it undercounted **worst on exactly the documents where an
undeclared key is ordinary** — an import, which is also the only kind of document dense enough for the
budget to matter. The answer was "outlines are affordable" about a frame they were not affordable for,
and the symptom is a slow frame rather than a wrong picture, which is why nothing reported it.

It is `HiddenLayers` now — the keys the technology declares and hides — and the walk skips only those.
Null still means "no technology resolved, so nothing is hidden", unchanged. The rule is the one
`DocumentExtents.LayerVisibility` already followed and `LayoutRenderer.Draw` already implements: **a
key the technology does not mention is visible**, in all three places.

`src/Cli/RESOLVED.md`'s post-series section records the sibling defect this was found beside — the
`render` verb's own layer report and layer selection could not see those layers either.

---


## RND-4 — rendering a `.cdd` (2026-09-07)

`docs/sonnet-briefs/brief-render-4-data-display.md`. `circuitrf render <path.cdd>` draws a data
display, and the Data Display's models, renderers and trace resolution live in
`CircuitRF.Render.DataDisplay`. The verb's own half is `src/Cli/RenderDataDisplay.cs` +
`src/Cli/CddSources.cs`; `docs/design/cli.md` §13.7 and `docs/design/data-display.md` §2.9 are the
standing description.

### R-rnd4-1's measurement, in full — the finding this brief existed to produce

The brief's §2 predicted "the models and renderers can come down; the view models must not", and asked
whether trace resolution lives inside the 3,738-line `TraceRowViewModel`. **It does not, and that is
the whole answer to the question of whether this was bounded.**

**Where trace resolution actually lived, measured before any code moved:**

| Kind of trace | Resolver | Where it was |
|---|---|---|
| plain cube, expression, versus, network-parameter substitution, family | `SetCubeDataFrom` + `SetCubeDataFromCore` and ~12 private helpers | `PlotInspectorViewModel`, **all `static`, all taking a `DataSet`** |
| network element (S/Z/Y), derived scalar, stability circle | `Trace.BuildPath` → `BuildCubePath`/`BuildMatrixPath`/`BuildDerivedPath` | `Models/Trace.cs` — **a model, not a view model** |
| loadpull contour | `RebuildContour` (~90 lines) | `TraceRowViewModel`, entangled with a library lookup and three picker lists |
| summary table column | `RebuildSummary` + two column builders (~150 lines) | `PlotInspectorViewModel`, same shape |

So the 3,738-line view model is the trace CARD — pickers, combo synchronisation, undo — and it holds
exactly one resolver, whose ~90 lines of arithmetic are separable from the ~40 that find the surface.
`SetCubeDataFrom` was already cut as a static seam for harmonicaRF (R-h7-5). **The extraction was
bounded, and nothing in it required `DataDisplayViewModel` or `DisplayWindowViewModel` to come down.**

**The Avalonia surface, re-measured (the brief's own numbers were one round stale):**

| File | What it named |
|---|---|
| `Models/Plot.cs` | `Rect` × 47, `Size` × 1 |
| `Models/Trace.cs` | `Rect` × 27 |
| `Models/Axes.cs` | `Rect` × 14, `Color` × 5 |
| `Models/Misc.cs` | `Color`/`Colors.` × 35 |
| `Models/Marker.cs` | `Avalonia.Point` × 2, fully qualified |
| `Renderers/PlotRenderer.cs` | `Avalonia.Rect` × 7, fully qualified |
| `Renderers/RenderTheme.cs` | `Media.Color`, `Styling.ThemeVariant`, `Threading.Dispatcher`, `Application.Current` |

`TraceLabeler` and `AxesRenderer` named Avalonia only in COMMENTS — the brief counted them. Everything
else is `Rect`, `Point` and `Color`: value types with framework-free equivalents.

### What moved, and the four extractions

Fourteen `Models`, eight `Renderers`, eight parsers/resolvers, and `ComplexStringHelper` moved whole,
namespace `CircuitRF.Ui.DataDisplay` → `CircuitRF.Render.DataDisplay`, with a single `global using` in
`src/Ui/GlobalUsings.cs` and its mirror in `tests/Ui.Tests` — RND-1's precedent, and the reason the
~200 files that name these types were not touched. **All 12,885 `Ui.Tests` passed unchanged after the
move**, which is the evidence that it was mechanical.

Then four extractions, each a function BOTH sides now call: `TraceResolve`, `ContourResolve`,
`SummaryResolve`, `PlotConfigLoader` — plus `DataSourceView`, `PlotComposer`, `PlotDocumentWriter`,
`PlotCanvasGeometry`, `PlotLabelStrips`, `DataDisplayJson`. `PlotExporter` is now ~40 lines of
`Place(container)` plus its dialog and clipboard plumbing; `ExportAsync` had its own single-container
copy of the bounding-box fit and calls the shared composition instead.

### Five things that had to change, and why each was load-bearing

1. **`PlotRect`/`PlotPoint`, not `Rect`/`Point`.** src/Ui consumes this namespace through a global
   using and is full of `Avalonia.Rect`; a same-named type would have turned every one of those files
   into CS0104. The prefix is what keeps them apart, and a plot rectangle is not a control rectangle.
   The semantics are Avalonia's exactly — **including `Union`'s empty-operand special case**, which
   `Plot.Autoscale` depends on (it starts from `default(PlotRect)`) and which
   `tests/Ui.Tests/Render/PlotGeometryParityTests.cs` holds over all 49 ordered pairs of a fixture set.

2. **The trace-colour LUT is written out as explicit ARGB, not swapped for `SKColors.*`.** The two
   libraries disagree on one name: Avalonia's `Transparent` is `#00FFFFFF`, Skia's is `#00000000`. That
   is invisible while the colour is transparent — and not invisible at all once
   `RenderTheme.ToSKColor` overrides the alpha with the trace's own opacity, where the same stored
   choice renders white on one and black on the other. Only the colour INDEX is persisted, so no
   `.cdd` moved.

3. **`RenderTheme.GetTransparentAccent` did NOT come down.** It reads `Application.Current`'s resource
   dictionary on the UI thread — and the measurement is why it stayed: no renderer ever called it. Its
   three callers are `PlotControl`, `DragSelectOverlay` and `MarkerInfoBoxView`, all drawing SELECTION,
   which R-rnd4-7 lists among the things that do not come out in an export. It is
   `src/Ui/DataDisplay/PlotAccentColor.cs`.

4. **`AppSettings` came down and gained `Current`.** `AxesRenderer` read
   `AppSettingsViewModel.Instance.AlwaysDisplayDataSourcePrefix` and `PlotExporter` read three more.
   `AppSettingsViewModel` now wraps `AppSettings.Current` rather than its own `Load()`, so the Settings
   dialog and a headless render read one object. It has no disk persistence today, which is what keeps
   a CLI run reproducible; **if that changes, `RenderDataDisplay.Draw` is where a headless render would
   start depending on a preference file, and must not.**

5. **`DataDisplayJson`.** The `.cdd` serializer options were `DataDisplayViewModel.JsonOpts`. A second
   copy without `JsonStringEnumConverter` would read every `PlotType`, `FreqUnit`, `MatrixType` and
   `ContourColorMap` as its default — a Smith plot opening as a Rect one, which draws and is wrong.

### Two things the per-kind gate found that reading would not have

§5.2 asks for one fixture per trace kind. Two of the eight failed, and both were the same shape: a plot
that draws, exports cleanly, and is missing a curve.

- **The virtual `Z`/`Y` cubes** are materialized on the first read of a source's `DataSet`, inside
  `DataSourceEntryViewModel.Data`. A simulated S-parameter run carries `S` and `Z0` and no `Z` at all,
  so a trace on `SP1.Z` resolved to nothing headlessly.
- **The `NetworkView` SNP** is built from a grouped run's own S cube, likewise in the view model. A
  simulated run has no SNP by design, so `PlotConfigLoader`'s `snp is null → continue` guard dropped
  **every derived trace** — Max Gain, µ, µ′, |Δ|, passivity, and every stability circle — as the
  display opened.

Both are `DataSourceView` now, called by the library entry and by `CddSources`.

### What the gates measured

- **Byte identity against `PlotExporter`**, SVG and PDF, over eight trace kinds: a cube slice, an
  expression, a "plot versus", a derived metric, an S→Z conversion, a stability circle, a loadpull
  contour and a summary-table column. **The only normalisation is Skia's SVG element id counter**,
  which is per PROCESS and in hex — the test process has emitted other SVGs, so its ids are past
  `cl_29` while a freshly started CLI's start at `cl_3`, and they are not the same LENGTH. The PDF is
  compared with **no exclusion at all**: `PlotDocumentWriter` writes no CreationDate, so two runs of
  one composition are identical. (The written PDF's metadata `Title` is the output file's own name,
  which is what the GUI's Export writes; the test renders the application's side through the same
  writer with the same title rather than excluding bytes.)
- **The composition survived being parameterised** (R-rnd4-6): rendering one display at 792×612 and at
  1584×1224 puts every path coordinate at exactly 2×, which a re-fit would not — a re-fit changes the
  pad, and the pad is what would move a dragged marker info box away from its place.
- **The firewall row still passes.** `CircuitRF.Render.dll` references no Avalonia.

### A trap worth naming: `run.npy` is both a file name and the sentinel

`DataSourceRef.Selected` is the literal string `"run.npy"`, and `DataSourceLibraryViewModel.ResolveAbs`
short-circuits on it. So a results file actually CALLED `run.npy` cannot be selected — `ResolveAbs`
returns the (still null) `SelectedDataSourceAbs` instead of the path, `SelectedEntry` stays null, and
Add Trace silently does nothing. The flat results directory names a run after its schematic, so this
does not arise in practice; it cost an afternoon in a test fixture, and `CddSources` locates the
document's recorded `SelectedDataSource` as a NAME rather than through the sentinel branch for exactly
this reason.

### And one that only a byte-identity gate could have found: `SkiaFonts.TestOverrideTypeface`

`ScalarCubeTests` swaps the face every Data Display renderer draws text with, for one test, and
restores it in a `finally`. That is correct within its own class and no protection at all against a
class running CONCURRENTLY in another xUnit collection — and the window is invisible until something
compares rendered BYTES. `RenderDataDisplayCliTests` draws the same display in this process and in a
fresh CLI process, and the CLI has no override to read, so a plot drawn inside that window comes back
in **Helvetica** on one side and **IBM Plex Sans** on the other. Reliably green alone, reliably red
beside its neighbours — the shape of every shared-static hazard.

`tests/Ui.Tests/SkiaFontsTypefaceCollection.cs` is the sibling of RND-1's
`LayoutTextOutlineTypefaceCollection` (they are *different* statics: one is the face a layout LABEL
is flattened with, the other the face a PLOT draws with). `ScalarCubeTests`,
`PanAndMarkerLabelTests` — which renders pixels and was silently party to it — and
`RenderDataDisplayCliTests` are in it.

---

## WSP-4 — the WSProbe metrics, and the four things that were not obvious (2026-09-08)

`brief-wsprobe-4-ui-symbol-and-data-display.md`. The Data Display half landed here rather than in
`src/Ui` because `src/Cli`'s `plot` verb builds the same trace: the metric table, its gating, the
value production and the readouts are in `src/Render/DataDisplay/` and both consumers call them.

**A probe trace is a cube trace, and that decided everything downstream.** Its `CubeName` is the run's
own `…wsp` matrix and its values are substituted at `TraceResolve.SetCubeDataFromCore`'s single
interception point — the one a renormalized S/Z/Y cube already used. The slice, the family/slider
mechanism, the markers, the Table, the export, `.cdd` persistence and `render --data` then needed no
probe-specific path at all, and a swept `wsp` (`{Pin, freq, row, col}`) becomes a metric cube over
`{Pin, freq}`, which the family marker `~` already draws one curve per sweep point of. R-wsp4-10 cost
nothing because of this; a parallel resolve path would have cost it twice and drifted.

### 1. Case is load-bearing in the document's metric names, and folding it is a silent wrong answer

The first `WspMetrics` name table canonicalised to lowercase-alphanumeric and threw at class-init on a
duplicate key. The duplicates are real quantities: the document writes **`LGF`** for one probe's
forward synthetic-circulator loop gain (Eq. 99) and **`LGf`** for a probe PAIR's
feedback-as-synthetic-FET loop gain (Eq. 149), and the same collision exists for `LG_H`/`LGH` and
`LG_MF`/`LG_MR` against `LGM`. Had the dictionary been built with an overwrite instead of an `Add`,
`metric=lgf` would have resolved to whichever came last and drawn a plausible curve.

The lookup now resolves the exact spelling first, then the enum member name case-SENSITIVELY, then a
small alias table for the names a shell cannot type (`1/H0` → `invH0`), and only then the loose form —
built from names that have **no** collision under it, so `lgf` resolves to neither rather than to one.
A name that collides is reachable only by its own spelling, which is the honest behaviour.

### 2. Skia's SVG device silently drops a stroke's path effect

`SKPathEffect.CreateDash` on a `DrawLine` renders on the PNG and PDF backends and produces **nothing
at all** in the SVG — no `stroke-dasharray`, no path, no error. The margin threshold line was missing
from every `.svg` while the solid floor line beside it was present. `DrawWspMarginReferenceLines`
emits the dash as segments instead, so the three exports agree — which is the property
`render`'s own byte-identity gate rests on. Anything else in the renderers that reaches for a path
effect will have the same problem.

### 3. A margin level is stated in dB and the axis may not be

`Trace.WspMarginLevelInDisplayUnits` first passed a dB level straight through for `dB20`, `dB10` and
`dB` alike. `dB10`/`dB` render `10·log10` of the same ratio, so the −12 dB floor belongs at **−6** on
that axis; unconverted, both reference lines sat below every curve. Visibly wrong on a linear axis,
and quietly wrong on a `10·log10` one — which is exactly where a reader would take a margin off the
drawing and believe it. The level is now converted per transform (overview D-16 fixes the margin's own
convention at `20·log10`).

### 4. The virtual reduced-two-port groups must be appended AFTER the analysis groups

`DataSourceView` adds one network group per probe (`<analysis> ▸ WSProbe <label> ▸ reduced 2-port`,
carrying `S`/`Y`/`Z`/`Z0`) so that µ, µ′, K, |Δ|, MAG/MSG and both stability circles apply to the
document's own two-port reduction (Eq. 44) through the code that already computes them — the cheapest
new capability in the series, and free only because it is an ordinary network group.

`DataSetBuilder.FindCubeSpec` answers with the FIRST group carrying an `S`, and that has to keep being
the run's own: a network view built from a probe's reduction would quietly replace the amplifier's own
S-parameters in every metric that takes one. The groups are therefore materialized after the
`Z`/`Y` pass, and a test asserts `FindSCubeSpec` still names the analysis group.

**The scattering conversion is `WspMatrix.ScatteringOfY`**, the same one WSP-3 scatters a probe pair's
blocks with (Eq. 142/143) — not a second S-from-Y — so a circle read off the reduction and a pair
block's own S-parameters agree by construction.

---

## WSP-4 (finished) — the Envelope sub-card, and the rest of the virtual network groups (2026-09-08)

R-wsp4-9 and the second half of R-wsp4-6, the two things the entry above records as absent. Four
findings.

### 1. A virtual network group must FILE the matrix the library returned, not a round trip of it

`AddNetworkGroup` first built `S` from whatever it was handed and then derived `Y` and `Z` back out of
that `S`. For a group whose source quantity IS `Y` — `wsp_ymatrix` over a probe set (Eq. 185) — that
puts a Y→S→Y round trip between the library call and the cube, and the group's own `Y` then differs
from `WspGlobal.Ymatrix` in the last few digits: measured `0.02666666666666666` against
`0.026666666666666717`.

The failure is not the error, it is what the error invites. R-wsp4-14(a)'s gate is **bit identity with
the library**, so a test comparing the group against `WspGlobal.Ymatrix` fails, and the only fix
available to it is a tolerance — which then hides a second implementation for good. The cube the
function returned is now filed unconverted and only the other two are derived, so the gate can stay
exact. `wsp_block_breakout` returns S and is filed as S by the same rule.

### 2. The pair blocks cannot be materialized eagerly, and the brief's own sentence says so

The reduced two-port is one group per probe: N groups. A probe PAIR is `N(N−1)` ORDERED pairs and four
blocks each, so eager materialization is `4N(N−1)` groups of four cubes — on the 30-probe matrices §8's
NDF work contemplates, thousands of cubes nobody asked for, each the size of the sweep.

R-wsp4-6 reads "a second picker (\"with probe\") **enables** the pair group", and that is the design
rather than a phrasing: `DataSourceView.EnsureWspPairBlockGroups` is called when the card's picker
names a pair, and a `.cdd` that names one re-creates it by the same call. It is idempotent, so changing
the pair adds the new blocks and leaves the old ones — which are still true. The FULL probe set's `[Y]`
is materialized eagerly, because it is the one set that needs no picker at all.

**The arrow is part of the identity.** `A→B` and `B→A` bracket two different two-ports (Fig. 40 is
GEN → LOAD), so they are two groups; the gate asserts their `S` actually differs, rather than trusting
the naming.

### 3. `wsp_block_design` is INVARIANT to the frequency it is given, on the default RC pair

Written expecting the opposite. `WspPair.BlockDesign` turns each side's bidirectional impedance into a
parallel RC pair at `freqHz` (`ImmittanceModels.ZToPc`) and then renormalises to that pair **at the
same `freqHz`** — so the frequency cancels and the reference is the impedance it came from, whatever
was passed. A test written to catch a frozen frequency therefore passed under the frozen frequency too,
which is how this was found.

It stops cancelling the moment the design use the document describes is taken up (p. 88: "any RC pair
may be replaced by the user's own"), so each point is still handed its own frequency. The consequence
worth carrying forward is the other way round: **a wrong frequency here would be invisible on this
path**, so nothing downstream may take the frequency broadcast on trust. `FreqPerBlock` is written to
broadcast along the freq axis's own POSITION for that reason, rather than assuming it is outermost.

### 4. The envelope cube has four grid axes, always, and θ is in degrees

The expression engine returns `{…, gS, gL, …}` — one flat, index-valued Γ axis per side, labelled
`0.9@60`. The card cannot use that shape: [E] Fig. 6–9 read the margin **against phase**, and an
ordinal axis cannot be read that way. So `WspEnvelopeSource` lays the same flat grid out over `rhoS`,
`thetaS`, `rhoL`, `thetaL`, with θ carried in degrees and ρ as the ladder's own magnitudes. The samples
underneath are the library's, in the library's own grid order, which is what the gate asserts.

**All four axes are present whichever side is pulled** — an off side contributes two length-1 axes
rather than disappearing. A trace's slice is matched by axis NAME, so a rank that changed when a ladder
was typed would leave the slice describing a cube that no longer exists. That also decided
`TraceRowViewModel.SyncWspSlice`: a probe metric whose axis NAMES differ from the trace's current slice
re-authors it, and only then — an ordinary edit (a different probe, a new Z0) leaves a slice the reader
has arranged exactly as it was.

**The `plot` verb had the same defect and it was the same one line.** It built the slice from
`WspSource.LeadingAxes`, which for an envelope metric pins `freq` — an axis `SMenv` does not have —
and leaves the four grid axes unpinned. It now slices against the cube the resolve already produced,
and picks the pulled side's θ as x when there is no frequency axis.

### Not built, deliberately: the θS × θL grid as a COLOURED map

R-wsp4-9 asks for the stability map "drawn as a θS × θL grid coloured by the unstable count". The count
is produced and is an ordinary real cube over the grid axes, so it draws through the family mechanism
(one curve per θL) and reads in the Table. A filled raster of the grid would need the Data Display's
**heatmap fill**, which `brief-dd-loadpull-contour-ux-round8` §3 deliberately withholds from the UI as
experimental — the picker offers only None/Topography, and the machinery is kept only so a saved
`.cdd` still loads. Adding a second, rectangular-grid raster path beside a withheld one is not this
brief's call to make. The readout names the first flagged termination and how many there are, which is
the reading the map exists for.

## Ports: six defects in the marker and the excitation, over one session (2026-09-09)

Six owner reports, all about the same port. R1 and R2 are contradictory as stated, and the first
attempt at each broke the other — that is the interesting part and it is recorded rather than tidied
away. R5 found that the layout editor and the EM engine were deriving "how wide is this port" two
independent ways and disagreeing by a whole edge.

### What moves is the MARKER, not the label

`X`/`Y` never changed in any of these. An edge port's plane bar and arrow are drawn at the conductor
END (`PortHint.PlaneX`/`PlaneY`) and sized to that conductor's width, all of it re-derived every
frame from `LayoutPortDirection.LookupFor`. Every defect below is that derivation reading something
it should not.

### R1 — a placed port moved when a layer was switched on

Toggling a layer's visibility moved a placed port. `LookupFor` resolved the conductor by taking the
FIRST entry of `LayoutHitTest.HitStack`, and two things were wrong with borrowing a CLICK's ordering:

1. **`HitStack` skips layers marked not `Visible`/`Selectable`** — right for a click, and it made a
   port's geometry a function of view state.
2. **ZOrder-descending puts a POUR ahead of the trace lying on it** whenever the pour's layer draws
   on top. Fixing (1) alone would have traded an unstable answer for a stably wrong one.

Measured on the reporting board (a Top Copper trace at `ZOrder` 0 crossing an `Inner 1` pour at
`ZOrder` 10), sweeping 2,539 port positions over the region where both carry metal: **337 moved when
the pour was switched on; 0 after.** Worst case the plane jumped 4.81 mm, the width went 1.04 mm →
10.27 mm, and the direction flipped R0 → R90.

### R2 — a drag was attracted to hidden layers, and R1's first fix is what caused it

The first fix made the lookup ignore visibility entirely. That satisfies R1 and **directly breaks
R2**: on the same board, with the pour hidden, **9,483 sampled positions resolved onto the invisible
pour**. Dragging a port anywhere over it measured metal that was not on screen.

The two requirements cannot both be met by one rule, and they do not have to be — **they are
questions about different MOMENTS**:

- A port **COMMITS** to a conductor layer at a GESTURE (placement, or a move). That asks about
  VISIBLE metal, so nothing invisible can attract it. `LabelShape.PortLayer` records the answer.
- **At rest** it asks only about the layer it committed to, ignoring visibility — so no toggle moves
  it.

A port is never resting and being dragged at once, so the rules never meet. After it:
`onHidden=0` (was 9,483) and `moved=0 of 2,539`. Null `PortLayer` — every `.clay` written before —
takes the VISIBLE half, which is the safe one: such a port is never attracted to metal the user
cannot see, and it commits the first time it is touched.

**The visibility gate is `Visible` alone, not `Visible && Selectable`.** `HitStack` needs both
because it answers "what did the user CLICK"; this asks "what metal is on screen", which is the gate
`LayoutSnapQuery` already applies to every snap feature. **And the SMALLEST conductor wins, not the
topmost** — which is what `ConductorUnderShape` (the shapes-only form, used by the clipboard and
`DocumentExtents`) has always returned, so the two forms no longer disagree about where a port is.

*Worth knowing:* the geometry snap was never the problem. It gates every feature source on `Visible`
and offered **0 candidates from the hidden pour out of 4,316** on that board. Measuring it first is
what kept the fix off the snap query.

### R3 — the arrow did not turn until the mouse was released

The reseat rule (`ReseatMovedPortDirections`) ran only at COMMIT, so the arrow held its old angle for
the whole gesture and snapped round on release — the one moment it is no longer any use for aiming.
`LayoutPortDirection.Reseat` is now the single derivation, called by both the live drag-override
clone and the commit, with the commit's own guards (a selection carrying geometry or an instance is
moving the CONDUCTOR too, so nothing is re-seated) mirrored in the preview.

**That exposed a latent bug worth its own note.** `SetShapeFieldCommand` always notified `Full`, and
`LayoutEditorViewModel`'s change handler CLEARS the `.cem`'s published internal-port marks on any
kind but `Updated`, because anything else can renumber ports. Re-seating a port renumbers nothing —
so committing such a drag wiped the marks and every internal port in the drawing flashed to an edge
port's bar-and-arrow. It has an optional `LayoutChangeInfo` now; the reseat sites pass
`Updated([i])`. Caught by `InternalPortDragRenderTests`, which the layer stamp made fire on every
port drag rather than only on a direction change.

### R4 — a port dropped on one feature of a polygon drew itself on another

**Nothing was snapping, and this one is independent of the other three.** Every part of an edge
port's plane came from the conductor's BOUNDING BOX: `PlaneOf` returns a box edge and `SpanAt`'s
default cut is taken at that box edge. For a straight run of metal the box IS the conductor and both
are exact — the case they were written for. **A real imported polygon is not one feature.** The
reporting board's Top Copper is a SINGLE polygon carrying three:

| x | y extent | height |
| --- | --- | --- |
| 103.5–103.8 mm | 41.44–42.66 | 1.21 mm (blob) |
| 105.25–105.6 mm | 42.17–42.77 | 0.60 mm (narrow trace) |
| 106.0–109.63 mm | 41.60–43.40 | 1.80 mm (rectangle) |

Its box spans all three and describes none of them. A port facing R90 anywhere on that polygon had
its plane placed at the BOX's bottom edge — a y only the left blob reaches — so the marker was drawn
over the left blob wherever on the metal the port was dropped. The one port that always looked right
is the one sitting exactly on the box's own `MaxX`.

`FaceAlong` walks the shape's own flattened outline from the anchor, opposite the direction, and
returns the local face; `FromOutline` picks the direction from the nearest of the four. Plane, width
AND the arrow's length clamp are all measured there now. `Resolve`'s own inference was still calling
`FromBbox` directly and had to be routed through `DirectionAt` — deriving it a second way is exactly
how a placed port comes to disagree with its own marker.

**`FaceAlong` reads the RUN the port is in, not the nearest crossing**, and that is not a detail: a
port sitting EXACTLY on an end face — which is where a user puts one — has a crossing at distance
zero on BOTH sides, so R0 and R180 tie at 0 and the tie-break picks the wrong one. Reading the run
gives the near face 0 and the far face the conductor's length, which is the difference between "this
port faces the end" and "this port faces backwards".

*Not a bug, and it surprised me first:* a port on the narrow trace facing its lower edge measures the
CONTIGUOUS metal at that edge, which on a connected polygon can be the whole run. That is the honest
answer for a shared face — the defect was measuring a run 2,000 DBU away, not measuring a wide one.

`EmPortExtraction` reads none of this — it re-derives the side from exact flattened geometry and
refuses rather than guessing, and a port over metal on more than one conductor level is still ITS
refusal to make ("a port's LEVEL is part of its identity"). All of the above picks a stable conductor
to draw a marker against; it does not decide what runs.

### R5 — the bar overlapped artwork it does not touch, and the EXCITATION was somewhere else entirely

The port was moved onto the wall of a NOTCH — the shape a connector cutout makes. Three separate
things were wrong there, and the third is the serious one.

**(a) The bar measured the metal BEHIND the face, not the face.** `SpanAt` cuts a scanline just
inside the face and keeps the contiguous run of METAL it crosses. Where the face is a conductor's end
that is the same answer, which is every case it was written for. At a notch the metal keeps going
past the face: the boundary at x = 103.935 mm runs y 41.437 → 42.290 (0.853 mm), but above 42.290 the
conductor turns and carries on right, so a scanline one part-in-a-thousand inside the wall stayed in
metal to y = 42.650 and reported **1.213 mm — 42% too long, centred 0.18 mm above the port**.
`EdgeAt` measures the boundary chain lying ON the face instead. Where both apply they agree, so it is
a refinement and not a second opinion; it is also better at something the scanline could not do at
all — two fingers ending on the same face line are two edges, and the port gets the one it is
standing on.

**(b) The excitation was on the wrong edge.** `PlanarPorts` marched in from the mesh's own boundary
and stopped at the first metal it met, reading only the port's TRANSVERSE coordinate. Correct while
the row crosses one run of metal — every uniform feed, every fixture in `PlanarPortTests` — and
silently wrong the moment it crosses two. Measured on this board: **both ports resolved to
plane = 109.6192 mm, width = 1.800 mm.** They drove the same edge, 5.7 mm from where one of them was,
with no refusal and nothing on screen to say so. Reading the port's longitudinal coordinate and
taking the run it is ON fixes it; a label beyond the metal still lands on the run it is beyond, which
is what preserves the documented "may sit just off the end face it names".

**(c) Then it still drove 1.213 mm, and an EDGE is where the metal STOPS.** The transverse walk asked
only "is there a rooftop straddling the plane here", which stays true past the end of the wall.
Driving those rooftops injects current into the middle of unbroken metal — a delta gap, not an edge
feed. One extra lookup (the cell just outside the face must be empty) settles it; it cannot fail at
the seed, because `outer` is by construction the outermost metal column of the seed's own run. The
METAL walk is narrowed by the same test deliberately, rather than letting the difference fall into
`UndrivenMetalM`: that field means "a conformal cell declined to pair here" and carries that
explanation in words. Metal that never ended at this face is not undriven — it is not this port's
cross-section.

The sequence, on the reporting board: `plane 109.6192 / w 1.800` → `103.9245 / 1.213` →
**`103.9245 / 0.853`**, which is the wall, and which is what the editor now draws for the same port.
**The marker and the excitation agreeing is the point** — before this they were two independent
derivations of "how wide is this port" and they disagreed by 42% on one and by a whole edge on the
other.

### R6 — only one of the port's two end segments appeared

Both were being drawn. The marker is the layer's own colour, tinted by
`PortMarkerContrastTintAmount` for contrast with the BACKGROUND and with nothing else — so where it
crossed that same layer's fill it was not faint, it was **invisible**. A differential render (the
frame without the port is the oracle; a pixel probe cannot tell a glyph from the artwork under it)
measured the serif hanging out over background at 12 changed pixels and the one buried in metal at
**zero**. A port whose plane ends inside metal — a notch, a tee, a pad on a pour — could only ever
show the end that happened to stick out, which is exactly "one segment".

A background-coloured halo under the whole path fixes the bar, both serifs and the arrow together:
35 and 22 changed pixels after. **Applied to all three port kinds**, because the two INTERNAL ones
sit on the metal by definition and had the same defect more completely than the edge port that
exposed it.

Gates: `tests/Ui.Tests/Layout/LayoutPortStableUnderLayerVisibilityTests.cs` (R1–R3, including the
live drag preview asserted mid-gesture and the commit agreeing with it),
`tests/Ui.Tests/Layout/LayoutPortOnMultiFeaturePolygonTests.cs` (R4/R5a, on a scale model of that
polygon — plus the notch, two fingers on one face, and a straight run proving the box arithmetic's
own case is unchanged), `tests/Ui.Tests/Layout/LayoutPortGlyphReadsOverMetalTests.cs` (R6, the
differential render), and `tests/Engine.Tests/Mom/PlanarPortOnANotchTests.cs` (R5b/c, which also
pins that a uniform feed resolves exactly as it always did).


---

## A port's glyph draws above ALL geometry, in ONE colour, with its name on top (2026-09-09)

Owner report, the same day as the halo above and superseding it, in three parts: the port name was
being **bisected by a line from the marker** and should render on top; a **horizontal line was
appearing in a colour that is not the port's tinted layer colour**; and **a port renders higher than
any geometry**, because seeing it is the whole point of the glyph.

**All three were one defect, plus the wrong fix for it.**

`DrawLayer` does not paint a layer's geometry as it walks the layer's shapes: fills, the hairline
elision tier and outlines are each batched into one path and painted **after every shape has been
visited**. A label, though, was drawn inline the moment the loop reached it. So every port went
**underneath its own layer's artwork** — and an edge port is the case where the two coincide
exactly, since its reference-plane bar lies along the conductor outline that then covered it. That
outline is the layer's raw, untinted colour: the reported horizontal line. On a port under metal from
a HIGHER-ZOrder layer the glyph disappeared outright.

The "one of two end segments" report above was read as a contrast problem and answered with a
background-coloured halo laid under every glyph. The measurement was right and the diagnosis was
not — the serif over metal changed zero pixels because it was **behind** the fill, not because it
was too close to it in colour. The halo then became visible in its own right, as the pale line
beside the plane bar, and it was a second colour in a mark that has to read as one object.

**What changed.** `DrawLayer` collects every `IsPort` label into a per-frame list instead of drawing
it, and `DrawPortGlyphs` paints them after every layer, every instance, the mesh overlay, the
placement ghosts and the rulers — and before the transient interaction chrome (selection outlines,
handles, marquee, snap marker), which is about the gesture in progress and has to stay grabbable.
The halo is gone from all three port kinds, the leader is solid rather than alpha-thinned, and the
NAME now takes the marker's own `TintForContrast` colour instead of the layer's raw one.

**Painting the name last is not, on its own, enough to put it on top once the whole glyph is one
colour.** A port's arrow arrives AT the reference plane and its name is centred on the anchor, so on
an edge port naming the end it sits on, the two occupy the same few pixels — tint over tint is a
blob, not a label. `DrawPortGlyphs` therefore clips the marker pass out of the glyphs' own outlines,
outset by `PortNameKnockoutGapDevicePixels`, so a stroke visibly passes BEHIND the text. The outline
is built through `LayoutTextOutline.ResolveLabelAnchor`, the one place a label's anchor becomes an
aligner and a baseline offset, so the hole cannot drift from the glyphs that fill it. A
background-coloured knockout would have been simpler and would have reintroduced exactly the second
colour the report was about.

Two passes rather than one per port, markers then names, so a name is above every marker in the
frame and not merely above its own.

**Ports inside a placed INSTANCE are still not drawn at all** — `DrawInstances` skips `LabelShape`
outright, a documented gap from L3a, and nothing here changes it.

Gates: `tests/Ui.Tests/Layout/LayoutPortGlyphOnTopTests.cs` — the glyph survives metal drawn over it
on a higher layer (≥ 90 % of its bare pixel area); every pixel it changes is its tint or a blend
toward it, in both themes, which is what excludes a halo and an untinted line; and no marker pixel
lands inside a letter, with the arrow tip asserted to be inside the name's own bbox so that claim
cannot pass vacuously. `LayoutPortGlyphReadsOverMetalTests` still holds the outcome the earlier
report asked for; its rationale is rewritten to name the real cause.

*Trap for whoever writes the next port render test:* a port with a STATED direction and no conductor
under it still draws a marker, at a stand-in width (`LayoutPortDirection.Resolve`'s own documented
branch). Dropping the polygon is therefore not how you get a name-only reference frame — clearing
`PortDirection` as well is.

## The internal port's mark: no ground symbol, and a ring the metal can hold (2026-09-09)

Two owner reports about the SHUNT port's glyph, one session after the six defects above.

**The ground symbol is gone.** The mark was a ring with a stem and three narrowing bars hanging below
it — the schematic convention for "and its other terminal is the plane". That is true of every
internal port and never varies, so the bars put ink on every one of them without distinguishing any
of them, and over dense artwork they read as clutter rather than as information. The ring alone still
carries the part that varies: where the port is, and — once there is a mesh — how big the footprint
it drives turned out to be. `InternalPortStemOverWidth`, `InternalPortGroundBarsOverWidth` and
`InternalPortGroundPitchOverWidth` went with them.

**The ring was drawn wider than the metal.** `RingOverWidth` is 0.55 of the port width, i.e. wider
than the conductor's own half-width of 0.5, so on a plain trace the glyph crossed the outline at four
points and read as a circle with a line through it. Sizing it off the width alone was the deeper
error: a port on a small pad, or near the end of a short stub, is bounded ALONG the trace as well.
`InternalRingRadius` now measures the metal's own span through the label in BOTH directions
(`LayoutPortDirection.SpanAt`, the same measurement the delta gap's width uses) and clamps the glyph
to 0.9 of the smaller half-span — just inside the outline rather than tangent to it, since a stroke
drawn exactly on an edge is drawn on top of that edge and cannot be seen. Where the outline cannot be
walked (an instance's artwork, which answers with a bounding box) the width is the bound, which is
the same answer for the straight run of metal that case usually is.

**Only the GLYPH is clamped.** Once a mesh exists the ring is the footprint the solver resolved, and
that is a dimension: it is reported at its real size whatever that is, which is the whole point of
drawing it there.

Gate: `tests/Ui.Tests/Layout/LayoutInteriorPortPlacementTests.cs` renders the metal alone and the
metal with an internal port, and asserts every differing pixel is inside the conductor's own rows.
The baseline may not contain the port with no mark declared — that renders as an EDGE port, whose bar
and arrow are a second difference that masks the one being measured.

## An edge port's mark is at the conductor end — and that had no escape hatch (owner, 2026-09-09)

`PortHint` gained `Interior`, and `LayoutPortDirection` gained `InferredKind`/`MarkAtAnchor` beside
it. The report and the full reasoning are in `src/Ui/RESOLVED.md`; what belongs here is why the
drawing side had to change at all.

**A `.clay` carries no port TYPE, deliberately** — the same artwork can be gapped in one EM setup and
edge-driven in another — so `MarkKindOf` fell back to `PlanarPortKind.Edge` for a label no `.cem` had
claimed. An edge port's bar and arrow are drawn at `hint.PlaneX/PlaneY`, the conductor END, so a port
standing in the middle of a rectangle was drawn, outlined and picked at a face it was nowhere near.
Measured on a 20 × 2.9 mm rect: label (10000, 1450), mark (0, 1450), with geometry snap ON and OFF
alike. Dragging it moved the label and left the mark; the pick region, which follows the mark, handed
the press to the rectangle instead, so the artwork moved and the port did not.

**The fallback now infers from the one thing the layout does know — where the label is.** At a face it
is an edge port and the bar belongs there, which is the 2026-08-09 request ("I can't tell from the
port glyph where the actual reference plane is") and is unchanged. Clear of both ends it is drawn at
its own anchor. `MarkKindOf` returns **null** for "nothing has spoken" rather than `Edge`, because the
two are different questions and conflating them is what removed the escape hatch.

**`Interior` is computed in `Measured`, from the shape's OUTLINE, against both ends.** `FaceAlong` in
the direction and in `Opposite(dir)`, falling back to the box only where there is no outline to walk —
on a notched polygon the bounding box's ends are 6 mm from the wall a port is standing on, which is
the same reason `Measured` stopped taking its plane from the box.

**Three consumers had to move with it or the marks would drift apart**: `DrawPortMarker`,
`DrawSelectionOutlines`/`BuildOutlinePathForSelection` (which now take `PlanarPortKind?` rather than a
pre-computed bool) and `LayoutHitTest.PortPickBbox`'s `atAnchor`. `MarkAtAnchor` is the single place
all three ask. `DocumentExtents` and `LayoutClipboard` box the mark's own centre for the same reason —
a square around the plane bounds empty metal while leaving an interior ring off the page.

**The ghost passes `statedKind: null`, not `Edge`.** A ghost is not placed yet, so no setup can have
claimed it, and it must show what a click will actually land.

## A 3D radiation pattern drew one filled path per triangle, at 17 fps (owner, 2026-09-11)

Reported: rotation, and even panning and zooming the Data Display, crawled whenever a `.cdd` carried
a 3D pattern plot. Asked for >30 fps on a pan.

**One plot was the whole frame.** Measured in Release from a scratch harness over the reported
document (a 91 × 360 far-field grid, the nine plots at their authored sizes): the Surface3D plot cost
**56.05 ms** and the other eight cost **2.5 ms between them**. A pan redraws every plot on the tab, so
the frame was ~58 ms — 17 fps, which is the report exactly.

**`DrawFacets` issued one `DrawPath` per facet: 64,060 antialiased `StrokeAndFill` calls.** The cost
is per-triangle setup, not fill area — it barely moves with resolution (45.4 ms at 520 × 321 against
48.6 ms at retina 1040 × 642), so a smaller plot would not have helped. Four alternatives, measured at
retina on the same mesh:

| | 520 × 321 | 1040 × 642 |
|---|---|---|
| path, AA `StrokeAndFill` (what shipped) | 45.4 ms | 48.6 ms |
| path, no AA, `Fill` only | 10.5 ms | 11.4 ms |
| paths bucketed into 256 colours, AA | 15.5 ms | 23.7 ms |
| **one `DrawVertices` for the whole mesh** | **4.2 ms** | **5.6 ms** |

**`DrawVertices` is what the surface draws with now, and the plot went 56.05 → 5.09 ms** (Quick,
the decimated mesh an active rotation draws, went 3.76 → 0.71 ms). Three triangles' worth of vertices
per facet, each carrying that facet's own colour, so the shading stays flat per triangle rather than
becoming a Gouraud blend.

**But a vector device records `drawVertices` as NOTHING.** Verified directly rather than assumed: the
same mesh that rasterises correctly writes a 150-byte SVG holding an empty `<svg>` element, and a
565-byte one-page PDF with nothing on it. An export that silently loses its surface is R-rnd4-4's
worst case — it opens, it prints, and it looks like a run that came back empty. So the path fill
stayed, and `PlotDocumentScope` decides between the two.

**It is a DOCUMENT scope, not a "vector device" flag, and it is ambient rather than a parameter.**
Ambient because the alternative — a `bool` threaded through `PlotComposer.Render` and
`PlotRenderer.Draw` — can be forgotten by the next writer, and the symptom of forgetting it is a blank
export rather than a compile error. The scope is entered by `PlotDocumentWriter`, which is the one
place in the repo that builds a plot document; the only raster outside it is `PlotExporter`'s
clipboard bitmap, which enters it itself. "Document" rather than "vector" because a PNG export is
raster and could take the mesh, but it is written once and read at whatever size the reader likes, so
it gets the exact fill too. The mesh is for the frame that has 16 ms to be in.

**The hairline stroke went with the paths and is not missed.** It existed to close the seam two
antialiased triangles leave along a shared edge, each covering about half the boundary pixel; a non-AA
mesh tiles shared edges exactly and shows no background through them anywhere. What the mesh does cost
is a slightly harder silhouette — 930 pixels of a 667,680-pixel frame differ by more than a rim's
worth, all of them on the outline.

**`SKBlendMode.Modulate` over a WHITE paint, and it has to be spelled out.** `DrawVertices` combines
each vertex colour with the paint's shader through that mode, and with no shader the paint's own
colour stands in — so the default black paint multiplies the entire surface to black. It draws a
solid black lobe with a correct colour bar beside it and reports nothing.

**The mesh build is memoised per thread, which is the other half of a pan.** `PatternMesh.Build`
projects and depth-sorts 64,060 facets in 3.8 ms, and a pan asks for the identical answer every frame
— same grid, same scale, same camera. The key is everything the build reads: the grid by reference
(it is rebuilt only when the trace resolves, and `Plot.RenderSnapshot`'s `MemberwiseClone` carries the
same one), the scale by value (it is a record), the camera and the stride. Thread-local and one entry:
the screen draws on the compositor thread and an export on its own, so a shared slot would have the
two evicting each other every frame, and a lock would put the export's 50 ms inside the frame's
budget. The vertex and colour scratch arrays are kept with it — 2.3 MB a frame otherwise — and reused
**only at the exact length**, because `SKVertices.CreateCopy` takes the whole array rather than a
count and an over-long buffer would draw its stale tail as triangles.

Gates: `tests/Ui.Tests/DataDisplay/Pattern3DDrawPathTests.cs` — the SVG still carries >4,000 `<path>`
elements, the PDF grows by >100 kB when a surface is on it, and the mesh and the paths raster to the
same silhouette (drawn area within 2%), the same colours (mean |Δ| < 2 of 765) and the same region
(fewer than 1% of pixels more than a rim apart). Nothing here times anything.

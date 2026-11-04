# src/Render — resolved briefs (detail, off the CLAUDE.md growth path)

## A binary simulation dataset (.ds) as a fifth source kind at the seam (brief-binary-dataset-data-display, 2026-10-31)

`PlotSourceFile.LoadWithNotes` reads a `.ds` — by its extension, or by CONTENT for an unknown one, so a renamed file
is still one (`.npy` and Touchstone extensions are never sniffed) — through `BinaryDatasetReader`, then gives it the
same two lines a `.npy` gets (`MaterializeNetworkParamCubes`, `NetworkViewOf`). `Load` is now `LoadWithNotes` minus the
notes; every existing caller is unchanged.

- **Axis order needed no reshaping.** A `.ds` S cube is `[Vds, Vgs, freq, i, j]`, sweeps outermost, and
  `ParametricSweepEngine` PREPENDS its axis per nesting level, so circuitRF's own swept S cube has the same shape. The
  trace card pins `Vds`/`Vgs` on it as on a sweep of our own (UiSnapshot: two Fix rows, freq as X).
- **`PortZ` → `Z0` (`DataSourceView.AddZ0FromPortZ`).** A group with `S` and a `PortZ [sweep…, port]` that holds one
  impedance per port at every point (1e-9 relative) gets `Z0 [port]`, and the `PortZ` cube is REMOVED rather than kept
  beside it: kept, an export of the loaded source would write `PortZ[p]` twice — once from the cube, once from the
  `Z0` the writer turns back into `PortZ` beside an `S`. That removal is also what makes a circuitRF S result
  round-trip through `.ds` as `S` + `Z0` exactly. A `PortZ` that varies is kept, adds no `Z0` (so no `Z`/`Y`), and is a
  note.
- **Notes are sentences, one per kind**: all text variables in one line (`SP1.PortName[1]` …), one line per group whose
  `PortZ` varies. `BinaryDataset.ToDataSet` gained the optional `skipped` list `Arrays` already took, so the reader is
  called once.
- `plot`/`render --data` needed nothing: both load through `CddSources.LoadResult` → `PlotSourceFile`. Gate:
  `MissingVerbsCliTests.Plot_ABinaryDataset_IsTheSameBytesAsTheInProcessRenderOfTheDisplayItBuilt`.
- **Trap for anyone writing the example:** `plot --trace …,i=2,j=1` on a SWEPT S cube leaves the sweep axes free and is
  refused as three kept axes; a swept `.ds` is plotted with a slice (`cube=SP1.S[0,0,:,2,1],y=db20`). And `y=db` is
  refused as ambiguous on purpose — `db20`.

## A Smith marker on a simulated trace jittered while tuning (2026-10-25)

Owner report: during a tuning drag, a marker on a Smith chart jumped a few pixels up and down; a Rect
marker did not. **A marker on a cube trace on a complex plane is resolved in two dimensions** —
`CubeMarkerIndex` picks the sample nearest its stored `PositionStatic` (Re, Im), because Smith/Polar
`Points` carry no x. So when new data moved the curve, the marker re-snapped to whichever sample now lay
nearest its OLD point: it hopped along the curve, and its readout changed frequency. A Rect marker stores
its x and has no such failure.

`Trace.SetCubeData` now reads each locus marker's x sample before the data is replaced and puts it back on
that sample's new point afterwards (`LocusMarkerSamples`/`ReanchorLocusMarkers`), matched by x value and by
which half of a whole-plane cut it is on. A point cloud (no x order), a family and a free marker are left
alone. Test `PlotsAndTuningRound26Tests.ASmithMarker_RidesItsOwnSample_WhenTheDataUnderItMoves`.

**The same session crashed the app on a non-finite sample.** `Trace.PathBoundingRect` counted ±∞/NaN
points, so a tuning step whose result had one gave an autoscaled window that was not finite; the `.cdd`
dirty-check serializes the axes on every redraw and System.Text.Json throws on ±∞. The extent now skips
non-finite points, the axes window is written through `Finite()` as the marker fields already were, and a
locus marker whose sample comes back non-finite stays put. Test
`ANonFiniteSample_LeavesTheWindowFinite_AndTheMarkerOffIt`.

## A dashed closed shape exported SOLID, and the only warning was native console noise (2026-09-21)

Owner report: a run printed `Unsupported path effect in addPaint.` repeatedly — no newline between
repeats, no file, no shape, no stack.

**It is a third member of the family recorded below in "Two constructs that are right on a raster
canvas and do not exist in SVG" — a Skia call the raster backend honours and the SVG device cannot
express.** Skia's SVG device writes `drawRect`/`drawRRect`/`drawOval`/`drawCircle` as `<rect>`,
`<ellipse>` and `<circle>` elements, then calls `addPaint` to attach the stroke. A path effect
cannot be carried on a primitive element, so `addPaint` prints that line and emits the element
**without the effect**. `drawPath` has no such branch: the device flattens the effect into the
path's own geometry, so the dash lands in the emitted `<path d="…">` as real segments.

So a crossing marquee, a drag ghost, a broken-bitmap placeholder, an interface-changed mark, a
not-fitted part outline and a selection box all exported as SOLID outlines — each of which means
something different from the dashed one it is supposed to be. Raster and PDF never had it: both
devices convert the primitive to a path internally before applying the effect.

**The fix is a route, not a redraw.** `Renderers/DashSafeShapes.cs` adds `DrawRectDashSafe`,
`DrawRoundRectDashSafe`, `DrawOvalDashSafe` and `DrawCircleDashSafe`; each falls straight through to
the plain primitive when `paint.PathEffect is null` and builds a one-shot `SKPath` only when there
is an effect to preserve. Nothing that was already correct changes shape or cost, and the on-screen
path pays nothing. The path is built per call rather than cached deliberately — a cached instance
would have to be thread-static, because layout tiles render in parallel, and the allocation is far
below the cost of the dash geometry Skia is about to build from it.

**Twenty-one call sites across nine files were converted**, in `SchematicRenderer` (9),
`LayoutRenderer.Instances` (3), `SymbolEditorRenderer` (2), and one each in `BitmapCache`,
`LayoutRenderer`, `WBondRenderer`, `RailMapRenderer`, `HarmonicaCanvas`, `WBondProfileCanvas` and
`HarmonicaPanelRenderer`. Sites whose paint is unconditionally solid were left on the plain
primitive: the `DashSafe` name is a claim about the paint, and using it where the claim is not true
costs the next reader a check.

**The scan is the part that protects the future**, and is why the test does more than exercise the
helper. `NoEffectCarryingPaintReachesABareClosedPrimitive` reads every `.cs` under `src/Render`,
`src/Ui`, `src/Cli` and `src/Design` that mentions `PathEffect` and fails on any bare closed
primitive handed a paint known to carry one. Three things it needs in order to be worth running:

- **It is scope-aware.** A paint name is live only from its declaration until the brace depth falls
  back below it. Without that, five solid paints sharing a common name (`stroke`, `paint`, `edge`)
  in a file that also holds one dashed paint all read as offenders.
- **Comments are stripped first**, since several of these files DISCUSS a dash they do not draw.
- **A draw call is rejoined across lines before the paint is looked for.** Five multi-line calls
  exist today (none of them offenders, checked by hand); without the rejoin the rule the scan
  actually enforces is "no offender that fits on one line", which is not a rule anyone writing code
  knows they are subject to.

**Both halves of the defect are asserted, not just the repair.** The export test proves the dash
survives THROUGH the helper and is silently lost WITHOUT it — proving only the helper's half would
pass vacuously the day Skia starts emitting `stroke-dasharray` on a primitive element, and would
stop guarding anything from that day on. The discriminator is the **subpath count**, not the element
name: a bare rounded rect already comes out as a `<path>` because Skia has no rounded-rect element,
but it is a single solid outline through the same `addPaint`. Only the flattened form is many
disjoint subpaths.

---

## Schematic wires were invisible in the user-doc figures, and nothing was missing from them (2026-09-20)

Owner: four of the six Smith Chart figures draw the components with nothing connecting them —
`smith-window`, `smith-trajectories`, `smith-constant-q`, `smith-copied-schematic`, light and dark
alike — while `smith-network-strip` and `smith-network-mirrored` are fine.

**Every figure contained both wire paths, and they were being PAINTED WITH THE PRECEDING ELEMENT'S
PAINT.** On the owner's machine WebKit renders some of the stroked `<path>` elements Skia's SVG
device writes using the paint of the element before them. In these figures the wires follow a run of
84 grid-line paths carrying `stroke="#AAA" stroke-opacity="0.1373"`, which composites to `#EFEFEF`
over the canvas — so the wire drew at the right place and the right width in a colour one step from
the background.

**The measurement that settled it** was taken off the owner's own screenshot rather than argued:
the band is `#EFEFEF`, flat, neutral, 20 device pixels tall at 2× — a 10 px stroke — and a real grid
line sampled beside it is the same `#EFEFEF`. A red stroke at any alpha keeps R above G and B; this
was perfectly neutral, so no red was being painted at all.

### Everything that looked like the answer first, and why each is worth knowing

- **The figure files were right.** All six held both wire paths, well formed, inside their clips,
  covered by nothing, with valid colours.
- **The pages were right.** Every inline `<svg>` block matched its own `.svg` byte for byte — which
  is what the last report of this shape turned out to be (`src/Ui/Diagnostics/RESOLVED.md`,
  2026-09-17), so it is the first thing to check and it was clean here.
- **Every renderer available locally drew them**: Skia, `qlmanage`, and an offscreen `WKWebView` on
  the owner's own machine. Only Safari and Finder's Quick Look — the on-screen, GPU-composited
  hosts — showed the fault, which is why six rounds of reasoning about the file went nowhere.
- **A minimal SVG of the same shape drew correctly in the owner's Safari**, including 55 grid paths
  followed by a wide red stroke. The reduction does not reproduce it; the real document does.
- **Sub-pixel contrast was measured, was real, and was NOT the cause.** A 1 px stroke centred at
  `y = 710.05` splits ~0.45/0.55 across two rows while one at `y = 92.59` puts 0.91 on one row, and
  those two cases are exactly the four bad figures and the two good ones. It is a genuine
  coincidence and it cost a whole wrong fix (a `MinWirePx = 2.0` floor, reverted): widening a stroke
  that is not being painted in its own colour just makes a wider invisible line. **A correlation
  that partitions the evidence perfectly is not a mechanism.**

### What was tested in the real document, which is the only place it reproduces

Probes built by patching the shipped `smith-window.svg` and handed to the owner to open:

| variant, inserted at the same point in the same group | result |
|---|---|
| the stroked `<path>` we emit today | grey |
| same, `stroke-width` 10 | grey |
| same, plus `stroke-opacity="1"` | grey |
| same, wrapped in its own `<g>` | grey |
| a `<line>` element instead of a `<path>` | grey |
| a filled `<rect>` | **correct colour** |
| a filled `<path>` (a stroke turned into its own outline) | **correct colour** |

**Fills survive there; strokes do not.** So `SchematicRenderer.DrawWire` strokes the polyline to
DEFINE its shape and fills the resulting outline to DRAW it — `SKPaint.GetFillPath`, falling back to
stroking if the conversion fails. Same move `e670f8c9` made for the Smith arc family when Skia's SVG
device dropped everything inside a `SaveLayer`. A wire now emits as
`<path fill="#A43F81" d="…Z" />` with no stroke at all. Identical pixels; the change is only in how
the geometry reaches the canvas.

### Regenerating the figures without the churn

31 figures and 6 pages changed. The affected set is predictable in advance — every figure holding a
path painted `#A43F81` (light) or `#D67AB2` (dark) — so DocGen was run once, its output classified
by whether those paths actually moved from stroked to filled, the other 35 files it rewrote restored
from HEAD, and the pages re-inlined from the figure files the way `Placeholders.ReadInline` does
rather than re-run. Checking afterwards that no page's inline block differs from its own `.svg` is
the cheap proof it was done right.

**`docs/user/assets/figures/pin-port-term{,-dark}.svg` are orphans** — no catalog row, no page names
them, so they do not regenerate and still carry stroked wires. Left alone; noticed only because they
were the one exception to the predicted set.

## Smith Chart, round four — overlays are Data Display trace configs now (2026-09-19)

The half of brief 12 that landed below the firewall. The window's half is in `src/Ui/RESOLVED.md` under
the same date, and `docs/design/smith-chart.md` §5.7, §7 and §9.4 record the design's own decisions.

### `PlotConfigLoader.LoadTrace` — a pure extraction, and the point of the round

`LoadPlot` was the only code that turns a `TraceConfig` into a `Trace`, and it was welded to a
`PlotContainerConfig` and to building a whole new `Plot`. The per-trace body is now
`LoadTrace(cfg, plotType, freqUnit, sources)` and `LoadPlot` is the loop that calls it. **No behaviour
change** — the `.cdd` suite is the gate, and it ran green with nothing moved.

It exists because a `.csmith` now stores its overlays as trace configs too. A second loader there would
be a second reading of the same thirty fields, free to drift silently; the symptom would be a setting
that survives in a `.cdd` and vanishes from a `.csmith`.

### `SmithOverlayResolver` is gone, and almost none of it was replaced

What it did — locate a Touchstone, parse an `S11`-style quantity, set a renormalization, exclude a row
from the autoscale — a trace config **says**, and `LoadTrace` has honoured since the Data Display's
first release. What is left is `SmithOverlayMigration`, which is the brief-8 mapping and nothing else,
plus two small files that are not resolution:

- **`SmithOverlays`** — the JSON a `.csmith` stores a config as (through `DataDisplayJson.Options`, so
  one spelling), the fingerprint of a whole list, and the trace's **key**: the name a marker on it is
  stored against, derived from the config alone so it is the same string across a rebuild *and* across
  a reorder. A key that moved on a reorder would put a reading on the wrong curve.
- **`SmithDocumentSources`** — an `IPlotDataSources` over a document folder, with the host's library
  consulted first. This is what keeps brief 8's relative-reference convention working now that
  resolution goes through the `.cdd`'s own loader, and what lets a scratch `.csmith` with no workspace
  draw a Touchstone sitting beside it. `circuitrf smith` uses the same type, with the workspace's
  `results/` as a second search directory, so the verb and the window resolve one document's overlays
  through one loader.

### `PlotSourceFile` — the third copy of "read one result file" was not written

`CddSources.LoadResult` was the second, and `SmithDocumentSources` would have been the third. It is one
function in `src/Render/DataDisplay` now and `CddSources` forwards to it. The two lines under `.npy`
are not optional and each was found missing by a gate rather than by reading the code:
`MaterializeNetworkParamCubes` (without it a trace on a virtual `Z` cube resolves to nothing) and
`NetworkViewOf` (without it every derived trace is dropped as the display opens, because a simulated
run has no SNP by design).

### `RestoreMarkers` now clears before it restores

That line used to be genuinely unnecessary: the whole trace set was rebuilt on each refill, so the
markers went with the objects that carried them. An **overlay is now the same `Trace` object from one
rebuild to the next**, so its markers survive the refill and re-attaching the document's added a second
copy of each — on every keystroke. This is the shape of defect that only appears once an object
outlives the loop that used to recreate it.

### `TraceConfig.ExcludeFromAutoscale`, and `Plot.AllowUserTraces`

Both new, both default to the value every existing file and every existing plot already had — false.
The first makes §5.7's autoscale exclusion persistable (and gives the Data Display's own trace card a
control for a flag that had none); the second splits `Plot.IsFixedReadout`'s two meanings so a plot can
keep its type fixed while opening its trace set. A plot with nowhere to write a user-added trace down
must leave `AllowUserTraces` alone, which is why it is opt-in rather than inferred.

## Smith Chart, round three — the glyph was on the wrong side of the real axis (2026-09-19)

The half of that round that landed below the firewall. The window's half is in `src/Ui/RESOLVED.md`
under the same date, and `docs/design/smith-chart.md` §9.3 records what changed in the design's own
decisions.

### The generator glyph was drawn at the conjugate of the generator

Owner report: the generator impedance renders at the conjugate of what the Generator table says.

It did, and on purpose — §3.4 specified a faint, un-selectable **conjugate-match target** at
Γ(conj(Z_gen(f))), on the reasoning that landing that frequency's load point on it *is* the conjugate
match. That reasoning is sound and the mismatch column still reports exactly that. What it costs is
the thing the report is about: conj(Z_gen) is Z_gen mirrored about the real axis, so the glyph sits at
the right magnitude with the wrong sign on its reactance, and **nothing in the picture says which one
it is**. The one glyph the generator table could be checked against was the one place the table's own
numbers were not. `SmithChartScene.ConjugateTargets` is `GeneratorPoints`, the trace is `Zgen`, and
`SmithChartSettings.ShowTargets` keeps its name so existing `.csmith` files still read.

The gate carries a generator with a REACTANCE (12 − 8.5j Ω) for the obvious reason: a real Z_gen is
its own conjugate and could not tell the two placements apart.

### A load point was dropped silently when its row could not be evaluated

Every generator-table frequency gets a load point — that is what the table is for, and the swept band
ADDS frequencies to that set rather than replacing it. The only thing that can stop one is a file
element whose Touchstone does not span that row, which `BuildScene` caught and skipped with a comment
saying it dropped "that row and no other". A chart with three generator rows and two load points on it
looks exactly like a chart with two rows, so the dropped frequencies are now named in the status
strip.

### The frequency labels were placed by a rule that could not see the other points

The label stubs fanned radially outward from the centre of the chart, each one a little longer than
the last. That spaces the labels from EACH OTHER, which was the problem it was written for — a
generator table is a few frequencies a few percent apart and the boxes are fifty pixels wide — but it
says nothing about where the other LOAD POINTS are, and a locus running outward from the centre puts
every label straight over the next point along it.

The placement is vertical now and chosen per point: a box hangs above its own glyph when the other
load points are below it and below when they are above — the owner's own rule for two points,
generalised through the mean — then is pushed one row further out until it clears every glyph and
every box already placed. The push is BOUNDED (six rows): a chart zoomed until the points are a pixel
apart has no placement that clears, and a label marching off the canvas is worse than a slight
overlap. `SmithChartChrome.LabelDirection` is the side choice on its own so it can be checked without
a canvas — canvas Y grows downward, which is the part that is easy to get backwards and impossible to
see in a screenshot of a chart whose points happen to be nearly level.

The box is still `ContourRenderer.DrawIsoLineLabel`'s, which is what §5.4's reuse is about. What
changed is that the stub handed to it is now in CANVAS coordinates with an identity projection, so the
arithmetic above is in pixels and the placer still draws the box.

### The constant-Q chrome

**`Q=1.75`, and no background plate.** The plate was near-opaque and on a light theme read as a white
patch punched out of the grid directly under the arc — more conspicuous than the grid lines it was
hiding. **The grab ring is drawn only while an arc is being DRAGGED**: on hover it appeared within
eight pixels of either arc and glided along it, which over a chart crossed by two arcs reads as a
circle chasing the cursor. `SmithGripperOverlay`'s hit test is untouched, so the arcs are grabbed
exactly as before.

### `Trace.ExcludeFromAxisLabels`

New, default false, set on every trace `SmithPlotBuilder.CubeTrace` produces and left clear on the
overlays. It governs both halves of a complex plot's axis labelling — `PlotLabelStrips.For` (the
per-trace Y strips) and `AxesRenderer.DrawComplexXLabels` (the `freq (a to b)` rows) — plus
`PlotCanvasGeometry.BottomLabelExtraLogical`, which sizes the canvas for those rows and must agree
with the renderer or the chart carries a band of empty space under it. That third one was also
counting CONTOUR traces the renderer has always filtered out; the two now agree about those too.
`src/Ui/RESOLVED.md` has the two reports it came from.

### Sizes

The generator glyphs are half the size they were (3.0 → 1.5) and the load points a tenth smaller
(2.0 → 1.8, and 3.6 for the design frequency's). The generator table is a few frequencies a few
percent apart, so the glyphs land almost on top of one another and the cluster read as one blob.


## `PlacedPlot` gained an overlay, because a copy composed from containers had none (2026-09-19)

brief-smith-7-clipboard.md, the half that landed below the firewall. The reasoning is in
`src/Ui/RESOLVED.md` under the same date; what matters here is the shape.

`PlotControl.Overlay` (the R-smith5-6 seam) reaches the on-screen frame only. The export path
composes from `PlotContainerViewModel`s through `PlacedPlot`, and `PlotComposer.Render` called
`PlotRenderer.Draw` with no overlay argument — so an overlay a host draws on every frame was absent
from every exported picture, silently, with the picture still produced and still looking correct.

`PlacedPlot.Overlay` is an `Action<SKCanvas, TransformSet, RenderTheme>` and **not** the
`IPlotOverlay` the control takes: that interface is the INPUT seam as well as the draw one and lives
above the firewall with the rest of it, while a composition needs only the drawing. The canvas and
the transform stay arguments of the frame, never remembered — `ContourRenderer` once drew every
contour on every Smith plot to the first target it had been handed. Null on every other plot in the
application, which is the whole of the old behaviour.


## railRF, fourth pass: a plot that kept a third of its width, and a legend pinned to the screen (2026-09-19)

The two items of that round that landed below the firewall. The window's half is in
`src/Ui/RESOLVED.md` under the same date.

### The Rect viewport charged a Y-label column per TRACE, including the ones it would never draw

Owner report: the |Z| plot in railRF's results panel does not use the width available to it.

`Plot.SetAxesViewport`'s Rect case sized the left margin as
`clamp(0.10 + LeftAxisTraces.Count * 0.05, 0.13, 0.40)` — one column per left-axis trace, which is
exactly right when `AxesRenderer.DrawTitleAndAxisLabels` is drawing one rotated label per trace.
It is not drawing them when the plot has a Y label of its own: that branch is
`else if (!plot.CustomYLabelOn)`, and with a plot-wide label exactly ONE label is drawn. railRF's
plot carries thirteen traces of the one quantity (ports × two model kinds, plus masks and aggressor
harmonics), so it pinned the margin at the 0.40 clamp for twelve labels that do not exist and left
the curves 55 % of the panel.

The count is now the number of label COLUMNS — `YLabel`/`Y2Label` non-empty is one — which also
covers the Rect CONTOUR plot, whose Y label is likewise plot-wide ("Imaginary (Ω)"). Nothing else
about the arithmetic changed.

**And the viewport has to be recomputed after the label is set.** `SetAxesViewport` runs off the
`Traces` collection changing, and railRF sets `CustomYLabelOn` after the last trace is added — so
the margin kept the width it was given while the column stack was still thirteen deep. One explicit
call in `RailRfViewModel.RebuildImpedancePlot` closes it; without it the fix above is invisible on
the first paint and appears only once an aggressor line happens to be added afterwards.

### The legend's text was the one thing in a world-geometry plate that was screen-fixed

Owner report: the drop map's legend text stops growing when you zoom in far.

`RailMapRenderer.LayOutLabels` fitted the three strings with `scale = Math.Min(1f, available / 2f /
perSide)` — a shrink with a ceiling at `LegendSizePx`. The plate is world geometry, deliberately
(§11.6 trap 4: it has to be framed by Zoom to Fit), so zooming in grew its border, its ramp and the
gaps between the labels while the labels stopped at 11 px. The further in a reader went, the smaller
the legend read, and no zoom brought it back.

The clamp is gone and the size is fitted to TWO constraints, the smaller winning: the bar's width as
before, and the LABEL BAND — what the plate has left under the ramp — which is new and is what stops
a legend whose strings happen to be short from sizing itself off a width it cannot use and drawing
through the plate's own floor. `LegendSizePx` is now only the size the strings are measured at.

### The legend is draggable, and the offset is view state

`RailMapScene.WithLegendMovedBy` translates the plate and re-unions `Bounds` with it, so a plate
dragged clear of the copper is still framed by Zoom to Fit. It is applied to the SCENE and not
inside `LegendFor`, because where the plate goes by default is a fact about the map and where the
user has since pushed it is not — a default that depended on a drag would move every time the
content did.

The gesture is `RailLayoutOverlay`'s, and it is the second thing that file consumes a pointer event
for (the pour pick was the first). A press is taken only INSIDE the plate; everything else is
declined, because anything an overlay consumes never reaches the canvas's marquee, pan and hit test,
and §11.6 is that those keep working over the map. The drag is measured from the press rather than
from the previous move, so a coalesced frame costs nothing and the plate cannot creep by
accumulating its own rounding. The offset is **not persisted**: it is view state exactly as the
pan and the zoom of that panel are, and writing it into the `.crail` would dirty the document on a
gesture that changed no input and no number. The window's report page reads it, so a copied picture
has the plate where the window has it.


## railRF map: 64,907 draw calls a frame, and the image that could not replace them (2026-09-19)

The follow-on to the section below, and the half that was actually costing the frame rate. Caching
the tile plan removed the per-frame REBUILD; the per-frame ISSUE remained, and it was one
`canvas.DrawRect` per extraction cell — 64,907 of them on the owner's sensor board with Accuracy on,
re-issued on every frame AND on every pointer move, since `LayoutCanvas.OnPointerMoved` ends in an
unconditional `InvalidateVisual`.

**The CPU measurement said the problem was not there, and it was wrong in a way worth recording.** A
headless raster render of that scene is 21 ms/frame at 2800×1800 in Debug — about 50 FPS, against a
report of one. The gap is the BACKEND: the window draws through `SkSurfaces::WrapCAMetalLayer`, and
Ganesh's per-operation CPU cost (clip stack, op bounds, chaining, resource lookup) is tens of times
the raster backend's, which just blits. A `sample` of the running application settled it — the render
thread pegged at 100%, 3,409 of 3,943 samples inside ONE Skia function under the canvas-playback
chain, and the Metal frames in the low tens. Nothing about that is visible from a raster probe, and
two earlier `sample` runs caught the app idle and said nothing at all, which is its own lesson about
profiling an interactive window.

Fixed by issuing each layer's tiles as ONE operation: a triangle list, two triangles per tile, built
once per (scene, theme) in world DBU and drawn under the viewport's own matrix. Vertices are stored
relative to the layer's own corner because a vertex is a `float` and a board's DBU coordinates run
past the 24-bit mantissa's exact range.

**An IMAGE was the first attempt and it is the interesting failure.** Pre-painting each layer at the
pitch of its own finest tile and blitting it is fewer calls still (three sheets, 0.8 MB total), and it
measured PIXEL-PERFECT on the board it was written against — 0 of 20,160,000 bytes. It is wrong
anyway: a sheet has to assume the tiles lie on a lattice, and `RailMapScene`'s interpolated branch
takes `half = step / 2` in INTEGER division, so on an odd step every tile is one DBU narrower than its
own spacing. Invisible per tile, it accumulates across a few hundred into a whole-tile shift — 0.53%
of the picture wrong by up to 225 levels on the first fixture it met. A triangle list assumes nothing:
each tile is two triangles at its own corners, so tiles that do not abut do not abut here either.

**Two traps, both of which produce a picture whose COVERAGE is exactly right:**

- `SKBlendMode.Modulate` MULTIPLIES the vertex colour by the paint's. Handed the shared tile paint —
  whose `Color` is whatever the last rectangle set, and black on a frame that drew none — the whole
  map renders black with its coverage pixel-for-pixel correct. The mesh has its own white paint now,
  and the gate compares colours rather than coverage because of this.
- The equality gate was VACUOUS on its first pass. A `MinTilesToBatch` threshold meant every layer of
  the test fixture fell under it, so the batched path the test names was never run and the test passed
  against the black map. The threshold is gone — the two forms draw the same picture, so it bought
  nothing — and the gate now asserts the render is painted before it asserts the two agree.

Not on by default. The report and the clipboard go through `VectorPage` to SVG and PDF, where the map
stays vector; the window opts in with `batchTiles: true`. Same split as `render --detail screen`
versus `--detail full`.

Verified on the owner's board, rects versus batch, 2800×1800:

| viewport | painted px | bytes differing |
|---|---|---|
| fit | 1,761,734 | 0 of 20,160,000 |
| 8× on the copper | 4,711,457 | 0 of 20,160,000 |
| 40× on the copper | 4,176,260 | 0 of 20,160,000 |

Gate: `RailBoardViewTests.TheBatchedMapIsPixelIdenticalToTheRectangleMap`, confirmed to catch the
black-map regression by reintroducing it rather than by assuming it would.

**Still open, and deliberately not touched here:** `RailLayoutOverlay.DropReadoutAt` scans all 64,907
tiles on every pointer move and `RailDcResult.VoltageAt` then scans every cell on the layer. Measured
at 2.2 ms per move in Debug — real, bounded, and nowhere near the frame cost, so it is recorded rather
than optimised. `LayoutCanvas.OnPointerMoved`'s unconditional `InvalidateVisual` is the other half of
why a bare cursor cost a full frame; it belongs to the layout editor, not to railRF.


## railRF map: the per-frame work that was a pure function of the scene (2026-09-19)

Reported by the owner: panning and zooming the board is very slow with the drop map on, and under
one frame a second with the accurate reading on as well. Scroll-wheel zoom with it.

`RailMapRenderer.DrawTiles` rebuilt a dictionary of per-layer tile lists, and re-evaluated
`theme.Ramp(scene.Normalise(tile.Value))` for every tile, on EVERY FRAME. Both are a pure function of
the scene and the theme, and a drop map is one rect per extraction cell — 64,907 of them on the
shipped Power Rail example at the accurate reading. It also drew every tile whatever the viewport,
so a zoomed-in pan paid for the whole board.

The grouping and the colours are now computed once per (scene, theme) into a
`ConditionalWeakTable<RailMapScene, TilePlan>`, and tiles outside the visible world rect are skipped.
Keyed on the scene by REFERENCE because a scene is immutable and is replaced wholesale whenever the
result changes (`RailLayoutOverlay.Invalidate` drops it), so a live scene is exactly the right
lifetime and a dead one must not be held.

**No pixel changes, and that was a constraint rather than a hope**: same grouping in the same
emission order, same colours, same rects from `RectOf` off the live viewport. `RailCopyTests`'
byte-identity gate passes untouched, which is the check that matters — it compares the window's
clipboard copy against the report's own render.

Measured, Debug, 900×700, twenty frames of a pan:

| | tiles | before | after |
|---|---|---|---|
| Power Rail example, Fast, full extent | 11,920 | 4.53 ms/frame | 3.57 ms/frame |
| Power Rail example, Accurate, full extent | 64,907 | 16.90 ms/frame | 11.35 ms/frame |
| synthetic, zoomed 20× | 102,400 | 13.42 ms/frame | 0.44 ms/frame |

**It does not account for one frame a second**, and `src/Ui/RESOLVED.md`'s own entry for this pass
says where the remaining gap was and was not looked for. What is settled is that the scene is built
once and cached (60 ms accurate, 6.7 ms fast) and a pan does not rebuild it, and that the draw is now
bounded by what is on screen rather than by what the board holds.


## The port markers were missing from the one tab the window opens on (2026-09-19)

Owner report on the shipped Power Rail example: *"I don't see those (U2, U1, U3) in the artwork …
why do they appear listed on the railRF window?"*

`RailMapScene` drew the source, load, observation and flagged-via glyphs on the DROP, CLASS and |Z|
maps and not on `BuildCopper`'s — which is the tab the window opens on and the one showing the bare
artwork. Nothing in a `.clay` labels a refdes (it carries geometry, not components), so those
glyphs ARE the only thing that says where a port is; withholding them on the tab that shows the
copper is withholding them exactly where the question gets asked.

Same `MarkersOf`, and the same bounds rule as the other three: the marker reach is unioned into
`Bounds` SEPARATELY from the map's, which is §11.6 trap 4 — a glyph outside the copper's own bbox
is precisely what Zoom to Fit cuts off.


## railRF brief 18 — the class map's paint order, and a box in DBU with text in points (2026-09-18)

`brief-railrf-18-six-defects.md`, R-rail18-3 and R-rail18-4. Both were found by capturing brief 17's
figures rather than by a test, and **the suite was green on both**. Brief 17's own entry here is
superseded and removed rather than left as a second record.

### 1. `R-rail18-3` — the tab that exists to make a misclassification visible showed nothing

`RailMapScene.BuildClass` copied `result.Classification` in order, and both extractors build that list
**rail copper first, reference copper last** (`PdnGraphExtractor` runs one `Classify` pass over
`railCopper`, then one over `refCopper`). `RailMapRenderer.Draw` walks `scene.Regions` in list order
and every region is **opaque paint** — R-rail8-10's own rule, so that nothing in the map depends on
the page's background.

So the reference was painted last, over everything beneath it. On a board whose reference is a PLANE —
every board this feature is for — the class tab was one flat rectangle in the `Spreading` colour.

That matters more than a cosmetic ordering usually does, because §2.9 rule 2 is the safety argument
for having a fast model at all: *"A silent misclassification is the one failure mode of this design …
**Drawing it is what makes it neither.**"* The picture that rule depends on showed nothing on the
ordinary case.

**`BuildClass` orders the SCENE; the extractors are untouched.** `result.Classification` is emitted
`OrderBy(c => c.IsReference ? 0 : 1)` — a stable sort, so within each half the extraction's own order
survives. The extractors' order is a property of how those two passes are written; the map's is a
property of what a reader needs to see, and coupling them would let a later change to either silently
move the picture. `RailMapRegion` gained `IsReference` to carry it, defaulted so the copper tab's
construction is unchanged.

*Consequence worth knowing:* both hit-tests over `scene.Regions` (`RailLayoutOverlay`'s readout and
its context menu) pick the smallest bbox with a strict `<`, so a tie now resolves to the REFERENCE
rather than the rail. On a plane reference the plane is the largest region and never wins a tie;
recorded because the tie-break moved.

### 2. `R-rail18-4` — a box in DBU with text in points

`RailMapLegend.Box` is a `Bbox` in DBU, placed below the map and sized as a fraction of the content;
`RailMapRenderer`'s font is sized in screen POINTS. So the plate shrinks with the canvas and the three
strings — the minimum, the caption and the maximum — do not, and below a certain size they overlap. It
was never only a window problem: `RailReportPage` and `RailGraphicExport` draw the same legend through
the same renderer, so an exported `.svg`/`.pdf` of a small map carried the overlap into a file
somebody keeps.

**Of the three possible answers the fix is SHRINK THE TEXT TO FIT THE BOX**, and the other two are
each giving up something that is not theirs to give:

- **Widening the box** cannot be done in the renderer — the box is world geometry and
  `RailMapScene.Bounds` frames it, so a wider plate would draw outside what Zoom to Fit framed, which
  is §11.6 trap 4 and the very thing this legend's placement exists to satisfy. Widening it in the
  SCENE would make a world extent depend on a screen font size.
- **Dropping the caption** below a threshold is the one that looks cheapest and is not available at
  all. The caption carries the MODEL KIND, and §2.9 rule 1 says every result names the model that
  produced it — a picture copied out of the window is a result that has left the window behind, so a
  plate without it is a fast answer that cannot be told from an accurate one. **This was written with
  a 7 px legibility floor first, and `RailZMapTests.ACopyTakenWithTheImpedanceMapShowing_…` rejected
  it immediately**: that test asserts `"Accurate model"` is in the exported SVG, and at that export's
  plate width the caption had been dropped. The gate was already there; the threshold was the mistake.

So all three labels always appear and the text scales without a floor. Small text is recoverable — the
exports are vector and a window can be widened — where overlapping text is not, and a missing model
name is not either.

Two structural notes on the implementation. `RailMapRenderer.TryPlate` now owns the plate's screen
geometry, so the drawing pass and anything asking where the labels land read one rectangle rather than
two copies of the same arithmetic. And `LayOutLabels` constrains **each side of the centred caption
separately** — a total-width test is not enough, because one long end label and one short one fits by
total and still runs into a centred caption. Widths are measured once at `LegendSizePx` and scaled: a
glyph advance is linear in text size, so one measurement answers every size and the arithmetic stays
deterministic across the two renders §11.7 compares.

Gates: `RailCopyTests` (reference regions precede rail regions; and a RASTER oracle — a pixel on the
trace comes back the trace colour, paired with the same scene built the old way where it comes back
the reference's). `RailBoardViewTests` (a ladder of canvas widths from 160 to 1600 px: no two label
rectangles intersect at any of them, all three are drawn at all of them, the narrow rungs really are
shrunk, and wherever the renderer shrank, the OLD full-size layout really did overlap).

**The raster is load-bearing and the first version of that oracle was an SVG scan, which gated
nothing.** An SVG records draw COMMANDS rather than a result, so every region's fill appears in the
file whether or not something was painted over it — the colour-presence assertion passed in both
orders. Anything checking overdraw here has to rasterise.

---

## railRF brief 9 — where the rail map lands in `LayoutRenderer.Draw` (2026-09-18)

`LayoutRenderOptions` gained `RailMap` + `RailTheme`, and `Draw` paints them. Two ordering decisions,
both of which would be wrong the obvious way round. The rest of the brief's findings are in
`src/Ui/RESOLVED.md`.

**It is painted in SCREEN space, after the path-space transform is restored** — beside the EM mesh
inset, not beside the planar mesh overlay. `RailMapRenderer` does its own world→screen through the
`LayoutViewport`, which is exactly how `LayoutCanvas` calls it after handing the Skia lease to
`ILayoutCanvasOverlay.Draw`; drawing it inside the path-space transform would apply the scale twice.
That the copy and the live canvas call the same function at the same point is the whole reason a
copied board cannot disagree with the one on screen.

**Rulers move to the deferred pass whenever a rail map is present.** The map is opaque paint by design
(R-rail8-10: nothing in it may depend on the page's background), so a ruler drawn in the in-band pass
would be buried by it — and §9B.9 is that a ruler is document content that comes out in a slide. So
`Draw` skips its own ruler pass when `RailMap is not null` and calls `DrawRulersOnTop` after the map,
which is the same two-step `DeferRulers` gives a HOST that paints an overlay of its own. The host's
own `DeferRulers` still wins: when it is set, the rulers are the host's to draw and this does nothing.

**No companion `ShowRailMap` flag**, unlike `ShowPlanarMesh` and `ShowDrcMarkers`. Those exist because
a mesh or a marker set is computed and then shown or hidden; a rail scene is built for the tab that is
showing, and there is no state in which one exists and is not wanted. The scene is the switch.


## railRF brief 8 — the board maps: what the scene had to decide (2026-09-18)

`RailMapScene`, `RailMapRenderer` and `RailMapTheme`, plus twelve `Rail.*` colour roles in
`ColorRole`, `ColorTheme.BuiltIn` and the shipped `Default.ccolor`. Below the firewall so the window
and a headless report draw with the same code, which is the reason RND-1 put this project here. The
overlay and the gestures are `src/Ui`'s — see that project's `RESOLVED.md`.

### The legend is WORLD geometry, and that is what makes the trap detectable

§11.6 trap 4: "a drop map is co-extensive with the copper, so this looks harmless — until a legend, a
source marker or a flagged-via callout sits outside the copper's own bbox and Zoom to Fit cuts it
off." A plate drawn in screen space could not be framed at all, and §11.7 point 1 frames a clipboard
page from what is PAINTED. So `RailMapLegend.Box` is a `Bbox` in DBU, placed below the map, and
`RailMapScene.Bounds` is the union of everything.

**It is sized off the content's LONGER side, not off each axis, and that is not cosmetic.** A supply
trace is routinely 30 mm long and 0.4 mm wide; a plate 7.5 % of 0.4 mm tall is 30 µm — unreadable, and
small enough to sit inside the margin Zoom to Fit already leaves on the short axis of a 75:1 shape.
The first version sized it per axis and the gate's own NEGATIVE could not be made to fail: returning
the copper bbox from `ContentBounds()` still framed the legend, because letterboxing swamped it. The
test now also sizes its canvas to the copper's own aspect for the same reason — a fit on a canvas whose
aspect is unrelated to the content hides the very margin the gate is about.

### The drop map has one interpolation rule and two regimes, and the branch is not two arithmetics

R-rail5-2 puts the field's interpolation in exactly one place — `RailDcResult.VoltageAt` — so the window
and the headless report cannot differ at the same coordinate. That method is a LINEAR SCAN by design,
so sampling a 96-across grid through it costs O(samples × nodes).

That is nothing in the regime it actually runs in (the fast model's graph is a few hundred junctions)
and would be minutes on the accurate model's mesh. The scene therefore branches: **where the
extraction's own cells are at least as dense as the sampling grid, the cells ARE the tiles**, and their
values are read straight off `NodeVoltages`. That is not a second rule — `VoltageAt`'s own contract is
that an exact hit on a cell centre answers that cell's value — it is the same answer without the scan.
The sparse regime interpolates.

### `SamplesAcross` is fixed, and it has to be

96 across the map's longer side, independent of the viewport. Brief 9's clipboard gate and brief 17's
documentation figures both require that rendering the same result twice gives the same bytes, and a
grid that tracked the window size would break both.

### Greyscale font edging, and the reason is §11.7 rather than taste

`RailMapRenderer.Font` sets `Edging = SKFontEdging.Antialias` and `Subpixel = false`. Skia's default
spreads a glyph's coverage unevenly across the red, green and blue channels on the assumption that the
pixels it lands on are a particular physical stripe order. This picture does not stay on a screen:
§11.7 copies it out as PDF, SVG and a bitmap that may be rescaled, and the same renderer draws the
headless report. **It was found by the opacity gate**, which saw per-channel differences no amount of
page showing through could explain — the same class of defect as a map that depends on the background,
and free to avoid here.

### What a two-render comparison can and cannot say about §11.7 point 2

The rule is that "nothing in the picture depends on the page's background being any particular
colour", and the case that decided it is the stackup renderer's drill bore: it uses the background
colour as PAINT to cut a hole, and not painting the bore did not make it transparent, it showed the
dielectric behind it.

**A pixel comparison cannot catch that on its own, and it is worth knowing why.** Rendering over two
backgrounds, every antialiased edge differs — it is by construction a blend of paint and page — and so
does a translucent fill, and so does a cut hole. The gate that survives is two-part:

* every differing pixel must be explained by a COMMON amount of page showing through on all three
  channels (`f + (1−α)(bg − f)`), within one LSB of Skia's own byte rounding; and
* **every colour the theme can paint is asserted opaque at the source**, ramp included and sampled
  across its whole range — because an interpolation through a translucent stop is opaque at both ends
  and not in the middle.

The first catches paint that reads the background; the second catches paint that lets it through. A
cut hole remains indistinguishable from unpainted page from pixels alone, which is why
`RailMapRenderer`'s header states the rule as a prohibition on the technique (no background reads, no
`SKBlendMode.Clear`/`DstOut`/`SrcOut`, no alpha-to-reveal) rather than relying on the test to find it.

### The `Rail.*` roles

Twelve, added to `ColorRole.All`, `ColorTheme.BuiltIn` (light and dark) and the shipped
`Default.ccolor` in the same change — a role absent from `BuiltIn` resolves to flat grey and one absent
from the file is invisible in the Settings editor. The ramp is **blue → amber → red with the midpoint
as a stop of its own**: a two-stop cold-to-hot interpolation through sRGB passes through a muddy band
exactly where §2.4's "where the colour changes fastest" is being read.

## Same report, round 2 (2026-09-17) — the fix for the pan made the scroll-wheel zoom worse

The first round took Zoom to Fit from 72 ms a frame to 23. The reply was that panning was "a little
faster but not much", and that scroll-wheel zooming from Zoom to Fit was still slow. Both halves of
that were right, and measuring the GESTURES rather than steady-state frames is what showed why.

**Two things the first round's numbers had missed.**

1. **A steady-state frame is not a gesture.** Rendering the same viewport five times measures a warm
   cache. A zoom gesture is the opposite: `LayoutCanvas.ZoomFactor` is 1.15, which is 0.20 of an
   octave, and the widening ladder's rungs are eighth-octaves — 0.125. **So every wheel click crosses
   at least one rung and invalidates every widened outline in the frame.** Before round 1 only the
   637 OPEN paths used that cache; after it, all 16,000 did. Scroll-zoom went from 77 ms a frame to
   159, with the first frame of a gesture at 322.
2. **The viewport is in LOGICAL pixels and Skia rasterizes at the device scale.** On a 2x display a
   1700x1000 canvas is 3400x2000 real pixels — 4x the area, and every number in round 1 was taken at
   1600x1000 with no scaling. Every measurement below is at 3400x2000, which is what a full-screen
   window on the reporting machine actually costs. (Debug vs Release barely matters here: 68.4 ms
   against 60.6 on the same frame, because the work is inside Skia, not in managed code.)

**Two routes, because neither one wins both.** The hairline tier draws a centreline at Width + the
pen. It can do that by filling a per-shape outline built at that width, or by stroking a batch of
cached centrelines with a paint of that width — the same region either way. They differ only in where
the stroker runs:

| | pan at fit | zoom in | zoom out | worst frame |
|---|---|---|---|---|
| HEAD before round 1 | 93.4 | 77.2 | 39.4 | 353 |
| round 1 — fill only | 33.5 | 159.1 | 126.7 | 346 |
| stroke only | 58.7 | 45.8 | 29.4 | 148 |
| **both, chosen per frame** | **31.0** | **47.1** | **29.6** | **152** |

The rule is in `LayoutPathCache.BeginElisionFrame`: **stroke while anything is missing, fill once
everything is there, and only ever BUILD while the key is holding still.** A gesture therefore costs
no rebuilds at all — measured over six wheel clicks on a 12-shape fixture, one frame rebuilt 12
centrelines (the click that crossed a zoom OCTAVE, which is the centreline cache's only key) against
the 288 paths the fill route alone would have rebuilt.

**Switching routes mid-gesture is invisible, and that is asserted rather than argued.** Full-frame
diff of a stroke-route frame against the settled fill-route frame of the same viewport, at 3400x2000:
at Zoom to Fit **not one pixel of 6,800,000 differs by more than 32/255**, the largest single-channel
difference is 28, and no pixel changes luminance by more than 96. At half fit, 19 pixels exceed 32.

**Three things worth not re-deriving:**

- **The settle is spread on purpose** (`LayoutFrameCounters.WidenRebuildBudget`, 2,000 a frame).
  Rebuilding the whole widened set in the first settled frame is 322 ms — a visible hitch landing
  exactly when the user stops scrolling. Budgeted it is ~6 frames of ~120 ms, every one of them
  drawing correctly through the stroke route, and then 31 ms from there on. A pan never re-triggers
  it; only a zoom does.
- **`Simplify` pays for itself and must not be dropped from the widened build.** It is only ever
  FILLED, never stroked, so skipping `Simplify`/`AsWinding` looks free — and it halves the build
  cost. But the unsimplified path is one contour per segment plus a wedge per join, and filling that
  took the settled frame from 30.2 ms to 47.1. Tried, measured, reverted.
- **`NoteFrameElisionKey` is called ONCE per frame, and is handed `scaleUm * dbuToUm`, not
  `vp.Zoom`.** Asked per LAYER, the first layer saw the key change and updated it, so every layer
  after it in the same frame saw a key that already matched and went on building — the gesture paid
  the full rebuild anyway. And the two spellings of the zoom are the same quantity but not the same
  double, so near a rung they can choose different ones, which would put the frame and its layers on
  opposite sides of "has this changed".

**A fixture that zooms IN leaves the tier it is testing.** The gate here zooms OUT for that reason: a
25,400 DBU trace passes one device pixel by the third wheel click going in, after which the frames
are exercising the ordinary outline tier and the assertion is measuring nothing.


## User report, 2026-09-17 — an imported panel had no FPS at Zoom to Fit, and was fast either side of it

The report is the diagnosis, and it is worth reading literally: fast zoomed IN, fast zoomed OUT, no
frame rate in the middle. A cost that appears only in a band is a TIER that stops applying there, not
a cost that scales with what is on screen.

**Reproduced on the file** (16,917 shapes, 12 MB, 234,723 stored vertices, no instances, 11 layers all
visible at 0.35 fill opacity), at 1600x1000, Release, medians of five steady-state frames:

| zoom | before | after |
|---|---|---|
| fit x0.25 | 35.8 ms | 35.0 ms |
| fit x0.5 | 38.4 ms | 25.7 ms |
| **Zoom to Fit** | **72.4 ms** | **23.1 ms** |
| fit x1.5 | 65.8 ms | 22.0 ms |
| fit x2 | 55.8 ms | 21.0 ms |
| fit x3 | 8.6 ms | 7.9 ms |
| fit x4 | 4.3 ms | 3.6 ms |

**Where the time was.** Instrumenting `DrawLayer`'s four paint points separately: at fit, 58.3 ms of
the 75.5 ms frame was ONE call — `canvas.DrawPath(strokeBatch, strokePaint)`, with 314,978 points in
it. Not the fills (1.7 ms for the merged aggregate, 7.9 ms for every individual shape together), not
path construction (`PathsConstructed` is 0 on every steady-state frame; the cache was working).

**Why that batch was so big.** Every path on this board is 127,000 DBU wide, which at the fit zoom of
3.28e-6 device pixels per DBU is 0.42 of a pixel. That is under `MinVisibleFillDevicePixels`, so
`mustOutline` — the visibility floor, which exists so hairline geometry cannot be erased by the
frame-wide outline decision — was true for essentially every shape, and each one put its full stroked
outline into the batch. The tier built to absorb exactly this, the hairline widened fill, refused
them: its gate carried `IsOpenCentreline`, and **8,108 of this file's 8,745 paths are CLOSED** (board
outlines, courtyards, keepouts, table rules, drill-chart glyphs — a CAM tool writes all of those as a
closed centreline). 637 open ones went through the tier; the rest were stroked one by one.

**Why it is a band and not a slope.** Zoomed out, the shapes become sub-pixel by BBOX and the LOD tier
takes them as minimal rects before the floor is ever consulted. Zoomed in, 127,000 DBU passes one
device pixel wide at about fit x2.4 and `mustOutline` goes false. Between those two is the whole
visible board at a sub-pixel width — which is where Zoom to Fit puts it, on any imported board whose
traces are fine relative to its extent.

**The fix is a deletion**: `&& IsOpenCentreline(thinPath)` came off the hairline gate. The guard has
been redundant since `NormalizeOutlineWinding` landed on 2026-09-12. What it protects against is two
independently-built rings of OPPOSITE winding cancelling under NonZero inside the shared `elided`
path; `AsWinding` (holes wound against the ring that encloses them) plus `NormalizeOutlineWinding`
(every outer contour positive) leave no opposite winding in the frame for a batch to cancel, and the
widened path is built by the same `BuildShapePath` and inherits both. The substitution is exact for a
ring as well as for a capsule: fill-plus-outline grows the outer boundary by half the pen and shrinks
the hole by half the pen, which is what stroking the centreline at Width + the pen produces.

`NestedClosedHairlinePaths_KeepTheirInteriors_WhateverTheirWinding` — the test written to gate the
guard, whose fixture is the exact case measured to break without it — **passes unchanged**, and now
does so on the harder route: its two rings are 0.82 device pixels wide, so they go through the tier
and batch together, and the interior still comes back empty. That test is worth more now than when it
was written; its own comment says so.

**Verified for lost geometry, not just for speed.** Full-frame pixel diff of before against after at
seven zoom levels: at fit, 0.12% of pixels differ by more than 32/255 and **114 pixels of 1,600,000
lost ink** — antialiased edges of the bounded over-cover, and zero at four of the seven levels. Side
by side the two frames are indistinguishable.

**Two things that were measured and are NOT the answer**, recorded so they are not tried again:

- *Draw-call count.* Removing `IsRingGeometry` from the merge tier as well takes the fitted frame from
  8,558 draw calls to 1,982 — and from 72.4 ms to **74.2 ms**. Skia's cost here is painted area and
  edges fed to the rasterizer, not calls issued. The guard stays, and `IsOpenCentreline`'s doc comment
  now records this A/B so the next reader does not repeat it.
- *The tiled raster cache.* It is off on this file: `DefaultTileShapeCountThreshold` is 20,000 and the
  board is 16,917 shapes. Lowering the threshold would have masked the band rather than removed it,
  and it only ever helps a pan at a settled zoom.

**Exports are unchanged by construction.** The gate requires `opts.PathCache`, and every one-shot
render — `circuitrf render`, the SVG/PDF exports — passes null. The byte-identity gates were never at
risk.

**Attribution note on the test run.** `dotnet test tests/Ui.Tests --filter FullyQualifiedName~Layout`
(3,155 tests) failed one test on each of three runs — a DIFFERENT one each time, one of them on a
clean tree, and each passing in isolation. Load-dependent flakes in that filter set, not this change.


## `LayoutPortDirection` moved DOWN to `src/Design`, and one overload could not come with it (2026-09-14)

Part of moving a port's TYPE onto its label — `src/Design/RESOLVED.md` has the bug and the decision.
What is worth recording here is the boundary, because it is the second time a framework-free type in
this project turned out to be needed below it.

**Why it had to move.** `EmPortExtraction` — and through it `EmRunService`, which is what `circuitrf
em` and Simulate both call — has to ask *"is this port standing in the interior of its conductor, or
at an end of it?"* and get the SAME answer the drawing gives. `src/Design` cannot reference
`src/Render`, so the alternative was a second interior test in the extractor, which is the second copy
every other note in that file exists to prevent. The file's own header already claimed *"the renderer,
the editor and the EM extractor all read this"*; the move is what makes that sentence true.

**Cost: almost nothing.** Both `src/Render` and `src/Ui` already say `global using
CircuitRF.Design.Layout`, and nothing qualified the type, so all 34 referencing files were untouched.
`CellPins` and `LayoutFlattenToPolygon` came along (both framework-free, both needed by it);
`CellPins` needed one `using CircuitRF.Design.Layout.PCells` for the `PCellGenerator` delegate.

**`LayoutHitTest` did NOT come along, and that is the whole reason for the split.** It measures a
label with real Skia glyph metrics off `LayoutRenderer.MeasureLabelWorldBbox`, which is render-layer
code by any reading. It is used by exactly one thing in `LayoutPortDirection`: the
`LookupFor(LayoutView, Technology?, string, long)` overload, which enumerates candidate shapes through
the hit stack. So that one overload — plus its `PinAt`/`ScaleWidth`/`TransformDirection` helpers —
stayed here as **`LayoutConductorLookup.LookupFor`**, and the ~37 call sites were renamed. Nothing
below the wall wants it: a lookup over a plain shape list (`LayoutPortDirection.LookupFor(shapes)`) is
all the extractor ever needs, and the view form exists for the EDITOR, which has a technology, layer
visibility and placed instances to resolve through.

**Three approaches were tried and rejected before that**, each for a stated reason:

- *Move `LayoutHitTest` too.* It compiles nowhere below the wall until `MeasureLabelWorldBbox` moves,
  and that lives on an 11,000-line renderer.
- *Inject the hit stack through a static delegate*, like `LayoutTextOutline.TypefaceSource` and
  `CellPins.GeneratorSource` already do. Rejected because what is lost when it is unset here is not a
  degradation, it is the whole answer — the lookup would return nothing — and because a module
  initializer in `src/Render` is not guaranteed to have run before a test touches `src/Design`. Those
  two existing hooks each have a precise, documented, harmless fallback; this one would not.
- *Let `EmPortExtraction` do its own interior test* from the polygons it already has. That is a second
  answer to the question, free to disagree with the drawing in marginal cases, which is the bug.

## The `.cem` no longer tells the renderer what a port is

`LayoutRenderOptions.InternalPortMarks` and `MarkKindOf` are **gone**. `DrawPortMarker` reads
`label.PortKind ?? InferredKind(hint)` — spelled out rather than calling
`LayoutPortDirection.KindOf`, only because the hint is already resolved there and `KindOf` would
resolve it twice.

Two pieces of machinery disappeared with it, and both are worth knowing were there:

- **The `original`-not-`shape` dance in the layer pass.** Marks were keyed on the label's exact DBU
  anchor, and a live move drag renders a translated CLONE while the model stays untouched until commit
  (R-L1c-3) — so from the first pixel of a drag no anchor matched and every internal port fell through
  to Edge for the whole gesture (owner report, 2026-08-25). Asking the STORED shape was the fix. A
  field on the label needs no fix: `LayoutGeometry.Clone` and the renderer's own two field-by-field
  `LabelShape` copies carry `PortKind`, so the preview is right by construction. **Those three copies
  are the trap to remember** — a new `LabelShape` field that is not added to all of them is dropped
  silently, and only during a drag.
- **`DrawSelectionOutlines`' `internalPortMarks` parameter**, for the same reason, feeding
  `BuildOutlinePathForSelection` → `LayoutHitTest.PortPickBbox`. The outline and the pick region read
  the shape's own field now.

---

## brief-stackup-render-8, 2026-09-13 — a scene may be built with NO boundary conditions

`StackupSceneOptions.ShowBoundaryConditions` (default true) is the one thing brief 8 needed from the
scene, and it exists for exactly one caller: the `stackup-mim` documentation figure, which is a
**window** on five of the MMIC process's seven bands. "Top: Open — free space above" is true of the
stack and false of a slice of it; a picture that printed it would be claiming to show the whole
sandwich.

**It is a scene option and not a renderer flag**, because it changes the LAYOUT rather than the paint:
the two notes occupy vertical space above and below the bands and everything else in the scene is
measured from them. With it off, `TopPad` is where the first band starts and the footer row (an
unresolvable via's refusal) is measured from the bottom of the label column instead of from the
bottom note.

Nothing else in `src/Render` changed for brief 8. The documentation figures now call
`StackupScene.Build` and `StackupRenderer.Draw` directly, which is the point — see
`src/Ui/Diagnostics/RESOLVED.md` for what that comparison found, including the one dark-variant
contrast issue it surfaced in the TAB (a dielectric's on-band name is `StackupOnBandInk`, fixed dark,
over a themed `DielectricFill`) and why brief 8 left it alone.

---


## brief-stackup-render-5-drag.md, 2026-09-13 — what the drags needed FROM the scene (two things)

The gesture code is in `src/Ui`; the detail is in `src/Ui/RESOLVED.md`. What landed here:

- **`StackupScene.BandColumn`** — the column the bands and the barrels share, published rather than
  re-derived. The lateral drag turns a pointer x into the fraction it stores, and a second copy of
  the column arithmetic would write a number that does not put the barrel back under the pointer.
  R-stk1-1 from the input side.
- **`StackupOverlay.DragInsertY`** — R-stk5-3's insertion line, as a y rather than a rect, because
  the line spans `BandColumn` and the scene already knows how wide that is. Drawn in the SELECTION
  colour rather than the ghost's: the ghost is a translucent copy of what is moving and the line is a
  decision about where it goes, and the one thing that must never happen is the two reading as one
  smear.

`StackupScene.LaneCentre` also now falls back to `StackupLayer.DrawLaneFraction` when the caller's
options name no lane — options first, since those carry a drag in flight. That is why the clipboard
export and the documentation figures honour a user-placed barrel with no code of their own. The
field's own rules are in `src/Design/RESOLVED.md` (STK5).

## 2026-09-13 — the stackup scene: one layout pass, two readers (brief-stackup-render-1-scene.md)

`StackupScene` + `StackupRenderer` + `StackupRenderTheme`, headless, below the firewall. The scene is
a record of named rectangles; the renderer paints them and computes nothing, and brief 2's control
will hit-test the same rects rather than re-deriving band positions. Gate:
`tests/Ui.Tests/Stackup/StackupSceneTests.cs`, 51 tests over all five shipped technologies.

### The owner removed the leader lines, so the placement has to carry the correspondence alone

R-stk1-9's rule 3 pushes two colliding labels apart and joins each back to its band with a leader.
That is what this drew first; the owner looked at it and asked for the angled callout lines to go. So
there is no `StackupLeader` type, no leader pass, and no `Stackup.Leader` colour role.

**What replaced them is not nothing, and the test says which properties are real.** The label column
follows the stack's own order, and a group whose name is not on its band LEADS with that layer's name.
What is NOT asserted is that a label is the nearest one to its own band — symmetric push-apart
re-centres a whole cluster about the mean of its members' ideal centres, so a label genuinely can end
up further from its band than a neighbour's is. Measured on the 20-hairline case, not argued.

### A drilled hole is a VOID, not the bands showing through

Owner, 2026-09-13: the plated barrel "renders strangely… 2 vertical lines with a gap". It was drawn
as two metal walls with nothing between them, so the dielectric showed through the bore and the eye
read two separate thin vias rather than one barrel with a hole in it. **A drill removes material**, so
the bore is now painted in the pane's own ground first and the walls go on top of it — the convention
every cross-section drawing of a plated through-hole uses — with one outline around the whole barrel
binding the two walls into one object. An unplated hole is the same void with no walls at all, which
is exactly what it is.

The barrel also widened from `DocStackupFixtures`' 16 px to 22, with the wall scaling over 3-6 px
rather than 2-5. At the old proportions the bore dominated the walls and the barrel still read as two
lines. **It is not to scale against the bands and never could be** — a 0.3 mm drill through a 1.6 mm
board is a tenth of this picture's own vertical compression — so the width is chosen to be legible and
the real wall thickness is printed beside it.

### The spec says as little as it can, and µr at 1 is not drawn at all

Owner, 2026-09-13: keep the text short to save space in the image. Every word in a spec is drawn on
every band, so the picture pays for each one. Gone: **"thick"** (a length with a unit on a band of a
cross-section is its thickness — it said so nine times on a seven-band stack), **"barrel"** from
"plated barrel", and "ground **reference**". Gone too are the sentence's commas, for the separate
reason below.

**µr is printed only when it is not 1.** That is the engineering convention and it saves a whole
quantity on very nearly every dielectric anyone draws. The consequence is real and is the trade: with
no label there is nothing for brief 4 to double-click, so a non-magnetic dielectric's µr is edited
from the card only — which always shows the field. A magnetic one has both. `MurIsLabelledOnlyWhenItIsNotOne`
pins both halves so this cannot be "simplified" into always-hidden.

### "Half as thick draws half as tall" is anchored at the THICKEST, not the thinnest

The brief writes the proportional branch as `h = hMin · t/tMin`. Every anchor in
`[hMin/tMin, hMax/tMax]` is exactly proportional and satisfies the range; this one uses `hMax/tMax`,
which spends the whole budget. **The reason is the commonest stack in the application**: a two-layer
board whose two copper layers are the same weight and whose one substrate has nothing to be
proportional against. Anchored at the thinnest, every kind with one distinct thickness draws at its
FLOOR — 1.6 mm of FR-4 four pixels taller than 35 µm of copper. Anchored at the thickest it draws at
110 px against 36, which is the picture `DocStackupFixtures` already makes.

The ranges are conductor 16-36 px and dielectric 20-110 px, and **they were chosen against the
shipped technologies rather than picked**: the 4-layer board's conductors are exactly 2:1 (1 oz outers,
half-ounce inners) and its dielectrics 5.25:1, so both stay in the proportional branch at budgets of
2.25 and 5.5. The MMIC's 12:1 conductors and 500:1 dielectrics compress, which is the case R-stk1-4's
flags exist for.

### Three things the brief specified that the measured text would not support

Each was found by rendering the picture and looking at it, not by reasoning about it.

- **A spec is several labels AND every label is padded, so the pieces cannot abut.** Padding every
  piece on all four sides puts adjacent rects of one sentence in contact, which is the overlap the
  rule forbids. The horizontal padding is therefore small (1.5 px) with a strictly positive
  `PieceGap` on top, and **the sentence's commas are gone** — a comma as its own piece takes a leading
  gap and reads as "0.0006 , tanδ". Quantities are separated by a wider gap instead.
- **Labels WRAP, because a label that runs off the right edge is a value nobody can read and brief 4
  cannot edit.** A dielectric's full spec is nine pieces; a via's span names two conductors. The span
  is emitted as three pieces (from, arrow, to) so a narrow column can break between the names, and the
  top boundary note as two so it can break before its aside.
- **The label column's width is a CONSTRAINT on the band column, not its leftover.** `WidestToken`
  measures the widest unbreakable piece the scene can emit — names and formatted values, which are the
  only two that can be long — and the band column yields to it down to its floor, dropping the label
  column entirely if even that will not do. Without this, "Top Copper (1 oz) → Bottom Copper (1 oz)"
  ran past the right edge on every PCB technology at 460 px. It is not an absolute guarantee and
  cannot be: a name longer than the pane has nowhere to go, and truncating it would print a value that
  is not the value.

### The narrow mode puts the specs BELOW the stack, not on the bands

§2 says the specs move onto the band when the label column is dropped. That was written before the
text was measured. A dielectric's spec wraps to three lines in a 290-pixel column against a band that
is 21 pixels tall, so every spec lands over a band that is not its own and **the picture asserts a
correspondence that is false** — verified by rendering it. Beneath the stack, in stack order, each
group leading with its layer's name, costs vertical space in a pane that scrolls anyway and says
nothing untrue. A band's name is never on-band in that mode, for the same reason.

### A null `Fill` draws SOLID here and reads as Plated on the card

R-stk1-7's table is explicit: `Plated`/null + `Fill` `Solid`/null is a filled barrel. That is also the
only defensible drawing — a hollow barrel needs a wall thickness and an entry with no fill model
stated has none, so drawing one means inventing the number the look exists to show.
**`StackupLayerRowViewModel.RefreshFromModel` defaults a null `Fill` to `Plated` instead**
(`SelectedFill = Layer.Fill ?? ViaFillKind.Plated`), so on a hand-authored technology that omits the
field the card's combo box and the drawing disagree. Every shipped technology states it, so nothing
ships wrong today. **Brief 6 owns the plated↔solid menu and should settle which default is the real
one** — not by changing the drawing to match the card, but by deciding what a missing `Fill` means and
saying it in one place.

### `StackupField` was already taken, and the second one broke the build

`CircuitRF.Design.Layout.StackupField` has named the six numeric stackup fields since
`StackupFieldReadiness`. Declaring the brief's enum of the same name in `CircuitRF.Render` made
`StackupField` an ambiguous reference in **every** `src/Ui` file — both namespaces are global usings
there — and `StackupLayerRowViewModel` stopped compiling. The existing enum is widened with `None`,
`Name` and `Span` instead; `Problem`'s own `_` arm answers null for all three, which is correct rather
than an omission. `None` is deliberately value 0 so `default(StackupField)` is not `Thickness`.

The same trap one level up: the test namespace is `CircuitRF.Ui.Tests.StackupRender`, not
`…Tests.Stackup` as the folder is. A namespace segment named `Stackup` shadows the `Stackup` TYPE for
every file under `CircuitRF.Ui.Tests`, and two existing test files stop compiling.

### `Default.ccolor` carries 18 of ~60 roles, and the new ones were not added to it

The brief says a new role must go into the shipped `.ccolor` "or it resolves to nothing, silently".
**It does not**: `ColorTheme.Resolve` falls back to `ColorTheme.BuiltIn` for any role a theme omits,
and the shipped file has omitted every `Layout.*`, `Harmonica.*` and `Match.*` role since they were
added. `Default.ccolor` is a partial overlay, not the palette; `BuiltIn` is the palette, and that is
where the eight `Stackup.*` defaults went. `ColorThemeTests.DefaultPresetFile_MatchesBuiltIn` passes
through the same fallback, which is why it never caught the drift and should not be read as proof the
file is complete.


## 2026-09-12 — a 3D pattern is 11 MB of SVG, and a FIGURE of one is now 750 KB

Found building the user-doc antenna figures. A document draws the surface as one filled PATH per
facet, because `drawVertices` is not recorded by a vector device (ANT-10's note below). The 91 x 360
grid an EM far-field run publishes is 64,800 facets at about 170 bytes each: an **11 MB** SVG,
against 340 KB for the largest figure in the whole user documentation, and comparable in PDF.

`PlotDocumentScope.Enter` now takes `budgetSurfaces`, and `StrideFor` applies the existing facet
budget when it is set. The antenna figure is **750 KB**, a 14x reduction, and is not distinguishable
from the full one — `PatternMesh.Build` keeps the endpoints of both angle axes at every stride, so
the silhouette, the peak direction and the nulls do not move. That is what makes it a tessellation
choice rather than a change to what the picture claims.

**Only `UiArtworkGenerator` sets it, and the distinction is deliberate.** A user-doc figure is placed
at one width on one page, where a 1° facet is far under a pixel. An ordinary export is archival — the
reader opens it at whatever size they like — and `circuitrf render`'s own `--detail full` already
states this repository's rule for a document: what is STORED is what is drawn, with the LOD tiers an
opt-in. So an export still writes every facet, and `Pattern3DDrawPathTests` still gates it at over
4,000 paths.

**Left for the owner, not decided here:** an ordinary SVG or PDF export of a far-field pattern is
still that 11 MB file, and it is large enough to be awkward in an illustration tool. Whether an
export should take a budget too, or offer the choice the way `render --detail` does, is a product
question rather than a figure one.

Full detail stays the rule for a LIVE frame, where it is nearly free: one `drawVertices` call puts
all 64,800 facets up in 5.6 ms, which is why the budget was only ever reached during a drag.


## User report, 2026-09-12 — Top Copper went missing when zoomed out, "like some kind of XOR"

**It was a boolean cancel, and the description was exact.** On an imported Gerber board, zooming out
made pads and trace stubs on Top Copper disappear; what vanished was precisely the INTERSECTION of a
trace with the pad it ran onto, leaving the rest of both shapes drawn. Reproduced headlessly with
`circuitrf render --detail screen` against `--detail full` (13,013 pixels of copper turned to
background at a 30 mm window, 947 at full extent) and isolated to ONE tier by disabling each of the
seven in turn: the L2c merge tier, and nothing else.

**Root cause: a `PathShape`'s outline was the one shape kind with no fixed absolute winding.**
`BuildPathOutline` strokes a centreline and unions the result with `SKPath.Simplify`, which is
`SkPathOps` and always answers in EVEN-ODD — a rule under which the direction a contour runs in
carries no meaning, so what comes back is whatever the union happened to produce. `AsWinding` then
re-winds it so a HOLE reads as a hole, which makes the path correct *on its own*; it says nothing
about which way the OUTER contour runs. Measured on this file: from the same builder, a 2-point
centreline strokes to a POSITIVE outer contour and a 3-point one to a NEGATIVE one.

Every other shape kind does have a fixed direction — `Rect`/`RoundedRect`/`Circle`/`Via` from Skia's
own primitives, `Polygon`/`Curve` from `NormalizeOuterWinding`, which deliberately skips `Path`
because a stroked centreline has no outer-ring vertex list to take a signed area from. So inside any
batched non-zero `SKPath` a negative outline cancelled the material it overlapped: +1 and -1 sum to
zero and the intersection is not drawn.

**Why only when zoomed out.** `DrawLayer` batches a layer once more than
`MergeShapeCountThreshold` of its shapes are ON SCREEN, so zooming out is what puts the trace and the
pad into one path. Zoomed in, each shape composites alone and any winding fills identically. The same
file therefore read correctly up close and wrong from further away — and a placed cell shows it at
every zoom, because `CompileCell` always merges.

**The fix: `NormalizeOutlineWinding`, immediately after `AsWinding`.** The convention is a POSITIVE
signed area in path space, which is what `AddRect`/`AddCircle` produce and what
`NormalizeOuterWinding`'s "reverse unless `SignedArea < 0` in DBU" already resolves to once path
space's Y flip is applied. A negative outline is reversed with Skia's own `AddPathReverse` over the
WHOLE path — the reason that method already gives: every contour flips together, so a hole keeps its
relationship to the ring it belongs to. The sign is read from the summed signed area of all contours,
which after `AsWinding` is the outer contours' sign, since a hole runs against the ring enclosing it
and is strictly smaller. Curved segments are measured across their endpoints: an approximation of the
area, an exact answer for the sign.

**What this did NOT change, and what still differs between the tiers.** A merged fill composites an
overlap ONCE where the individual tier composites it twice, so at partial `FillOpacity` a
trace-over-pad overlap reads lighter when batched. That is the merge tier working as designed
(`IsRingGeometry`'s own note says the same about same-layer overlap) and it is the whole of the
remaining `--detail screen` vs `--detail full` difference on this board — after the fix, zero pixels
of geometry are missing at any zoom tested.

Gate: `tests/Ui.Tests/LayoutOutlineWindingTests.cs` — the reported geometry to scale, through the
merge tier, through a placement, at the source with no renderer around it, plus the
drawn-individually control that was never wrong. Verified to fail before the change: 3 of the 4 red,
the control green.

## User report, 2026-09-12 — "the top layer is being masked by the layer below" (placed cells only)

**The direction of the convention is not the bug, and it is the opposite of the one in the report.**
Every part of circuitRF agrees that a HIGHER `ZOrder` paints LATER and therefore sits on TOP:
`docs/design/layout-view.md` §2.1 calls the field paint order, `LayoutRenderer` sorts its layer
buckets ASCENDING and draws them in that order, `LayoutHitTest` cycles `ZOrder` DESCENDING for
"topmost first", the layer picker lists ascending, and every shipped `.ctech` is authored to it —
Top Copper carries the highest number in its file (8 of 8 on the 2-layer PCBs, 11 of 11 on the
4-layer, and the MMIC's MIM plate metal takes 10 for the same reason, stated in its own comment).
Nothing anywhere reads the field the other way round.

**What was actually wrong: a placed cell did not sort at all.** `CompileCell` buckets a sub-cell's
geometry into a `Dictionary<LayerKey, …>`, so `CompiledCellGeometry.Layers` came back in
FIRST-ENCOUNTER order — the order the shapes happen to sit in the `.clay` — and `DrawInstances`
painted it that way. A cell whose bottom-copper shape is stored after its top-copper one therefore
painted the bottom layer over the top one. Reproduced and pinned: two overlapping opaque rects, the
same two layers, differing only in which line comes first; the flat render is right both ways and
the PLACED render was right for one storage order and inverted for the other.

That is why it reads as intermittent to a user and unreproducible to anyone drawing a test case by
hand — it is invisible on top-level geometry (always sorted), and on a placed cell it depends on a
file's line order, not on any setting. It reaches everything whose artwork lives in a cell: a
schematic-generated layout (which has no top-level shapes at all), an imported board placed as a
cell, and PCell artwork.

**Sorted in `DrawInstances`, NOT in `CompileCell`** — the compile cache is keyed on the cell and the
detail tolerance only, never on the technology, so an order baked into the compiled geometry would
survive a `ZOrder` edit in the technology editor and repaint nothing.

**Ties now break on the layer key, at both levels.** `List<T>.Sort` is not stable and both sides sort
a list built from a Dictionary walk, so two layers sharing one `ZOrder` painted in an order no
technology author could see or control. The shipped technologies have no duplicates; a hand-edited or
imported one can.

Gate: `tests/Ui.Tests/LayoutInstanceLayerOrderTests.cs` — both storage orders, flat and placed, plus
the equality of the two. Verified to fail before the change (the placed render came back blue, the
`ZOrder`-1 layer, where flat came back red).

**Unchanged and worth knowing, because it is the next thing a similar report will turn out to be:**
instances are drawn AFTER every top-level layer, whatever their layers' `ZOrder`, and each placement
is drawn whole before the next one starts. So a top-level shape on the topmost layer still sits under
any instance artwork that overlaps it, and one footprint's bottom-layer copper still paints over a
neighbouring footprint's top-layer copper. Bitmaps are the mirror exception, always first (R-bmp-2).
Both are deliberate; neither is what this report was.


## Owner report, 2026-09-11 — the layout ruler's y-axis labels were cut off past ~3.5 characters

The left ruler writes its label HORIZONTALLY across a strip that was the same 22 px the top ruler is
TALL. At the 10 px label size that holds about three and a half digits, so "-1000" — not an edge
case, since the canvas pans through zero in one gesture and a layout is routinely drawn in both
signs — lost its last character with nothing to say it had.

**Two changes, and the second is why the first can stay small.** `VerticalThickness` is now 26 px,
sized to hold exactly four characters at the base size and no more (four digits measure 24.00 px in
IBM Plex Sans at 10 px; the strip carries a 1 px pad each side). Past four characters the label is
SHRUNK to fit rather than the strip widened: the ruler is chrome beside the drawing, and the owner
asked for room for four and not for a column.

**The horizontal ruler takes the vertical one's size, and only the vertical labels are measured.**
`ComputeLabelFontSize` walks the Y ticks alone — a horizontal label has the whole tick spacing (60 px
at least) to sit in and can never run out of room, so measuring it would only ever produce a second,
larger answer, and a window whose two rulers are lettered differently reads as a rendering fault. The
two controls need no channel between them to agree: `LayoutEditorView.SyncRulers` and
`WBondProfileView` hand BOTH strips the canvas's own viewport, so each computes the same number from
the same input. That is also why the visibility test inside the helper reads `vp.Height` and not the
control's own `size.H` — for the horizontal strip those differ by two orders of magnitude.

Skia's advance widths scale linearly with text size, so ONE measurement at the base size gives the
exact factor and no search is needed. The result is quantised down to a quarter point, because
panning changes the visible label set continuously and an unquantised size jitters every frame. The
floor is 7 px: below that, clipping and illegibility cost the same and the larger of the two is the
better answer, so a nanometre-unit view of a whole board still truncates — deliberately.

**The two XAML hosts repeat the 26 as a literal** (`LayoutEditorView.axaml`, `WBondProfileView.axaml`,
each with a corner box that must match its ruler or the strips misalign). A strip drawn narrower than
the renderer believes would clip the label the fit rule just made room for, silently, so
`tests/Ui.Tests/LayoutRulerLabelFitTests.cs` compares the XAML against the constant.

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

## A marker stands on a SAMPLE, never on an interpolated point (2026-09-11)

An EM run was stopped from the Messages progress bar after six frequency points. The s-parameter
plot, with the trace card's marker shape set to Square, drew about a hundred markers — and the owner
read the picture the only way it can be read: a hundred markers is a hundred simulated points.

**Neither half was a bug on its own, which is why nothing had caught it.** An adaptively sampled
planar sweep publishes the WHOLE requested grid and models every point it did not solve (R-adf-2, and
the Messages row already said "6 solved by the full-wave kernel and the rest modelled from those").
The renderer, for its part, drew a marker at every point the trace had, which is correct for every
other source there is. Put together they assert ninety-four solves that never happened, in the one
piece of plot furniture whose entire meaning is "a sample is here".

**The line was never the problem and is unchanged.** The modelled points are the run's own answer at
the frequencies the user asked for; withdrawing them would be a different claim, not a truer one. What
is withdrawn is only the assertion that each one was computed — which is exactly the division the
owner asked for: smoothing may be in the line, it may not be a marker.

**The mask is a CUBE, not a note beside the data.** `RfCore.Data.SampleProvenance` owns the one
spelling (`PointSolved`, rank 1, on the swept axis, 1 = solved / 0 = modelled) and the planar kernel
emits it into its own diagnostics group beside `PointAddedBySearch`. A note could not have worked:
the window reads no engine object at all — the run writes `results/<key>.npy` and the Data Display
opens that file — so anything that does not survive the round trip is something nobody ever sees. It
is emitted **unconditionally**, all ones when nothing was modelled, for ANT-9's own reason: a reader
gets "all of them" rather than a missing cube to interpret.

**AN S-PARAMETER TRACE IS NOT A CUBE TRACE, and covering only the cube path fixed nothing.** The
first cut wired the mask through `TraceResolve` alone; the owner tested it and reported no change at
all, which is exactly right — `PlotInspectorViewModel.AddTrace` seeds a NETWORK-bound trace from any
source carrying an S network (`new Trace(snp, MatrixType.S, …)`), and a network-bound trace never
goes near `SetCubeData`. It draws through `BuildMatrixPath` off `Trace.Data.Frequencies`. So the mask
rides **the `SNP` itself** (`SNP.SolvedMask`), filled in `DataSetBuilder.ToSnp` — the one place a
DataSet becomes a network, and therefore the one place the two facts are in the same hand. No
stamping site to forget: `Trace.PointIsSolved` reads the cube-path mask first and the network's own
second, and `BuildMatrixPath`/`BuildDerivedPath` now record the sample map the cube path already
kept. The lesson is not about this feature — it is that "which code path does the WINDOW actually
take" is a question to answer before writing the fix, not after shipping it.

**The mask is found by axis NAME, LENGTH and VALUES, in any group.** The name alone is not enough —
one result can carry several axes called `freq` of different lengths (a far-field grid is one), and a
mask applied to the wrong axis would hide markers at frequencies that WERE solved, which is the same
class of silent wrongness this whole change exists to end. It is also cleared by every `SetCubeData`
that does not carry one, so a mask cannot outlive the cube it described.

**A source that says nothing is unchanged**, which is every Touchstone file and every circuit
analysis: `Trace.PointIsSolved` returns true with no mask, for a family curve, and for any point whose
sample cannot be identified.

Gates: `tests/Ui.Tests/DataDisplay/SolvedPointMarkerTests.cs` — **both trace kinds**, cube-bound and
network-bound: the point list is still the whole published grid while the marked set is exactly the
solved one, the mask survives `ResultsWriter` → `DataSetImporter` → `ToSnp`, re-binding to an
unmasked source clears it, and the RENDERER is checked on the bitmap for each kind — marker ink at
every solved point's own canvas position and at no other (verified to go red when the rule is
removed). The engine half is `tests/Engine.Tests/Mom/SolvedPointCubeTests.cs`.

**An existing result file cannot be repaired.** The mask is written by the run; a `.npy` from before
this change carries none, so it draws every point as a sample exactly as it used to. The sweep has to
be re-run for the distinction to exist at all.

**What is still not distinguishable, deliberately: the `.sNp`.** Touchstone holds S and nothing else,
so the file a schematic references carries no mask and every point in it is drawn as a sample. The
`.npy` is the file that can answer the question, and it is the one the EM run opens a Data Display
over.

## RF1 — a flat document paid 1 MB a frame to be told it has no instances (2026-09-12)

`docs/sonnet-briefs/brief-rasterfill-1-instance-query-on-flat-documents.md`, the first of the
raster-fill series. `LayoutRenderer.Draw` queried the spatial index **twice** per frame. The second
call is the combined overload, which returns every entry in the rect — **shapes included** — so on an
imported board (52,230 shapes, **zero instances**, which is the normal shape of a Gerber import) it
built a 51,378-entry list and sorted it every frame to produce the number zero.

`Draw` now skips that call when the index's own two-part predicate says the instance side is empty
**and clean** — `LayoutSpatialIndex.InstanceSideIsEmptyAndClean`, described in `src/Design/RESOLVED.md`
beside the index's other entries.

### What it actually buys, which is not what the brief sized it at

The brief costed this call at **12.42 ms per frame**. That figure is a **mean including the GC of the
list it throws away**. Timed best-of-30 on the same board and viewport, the traversal and the sort
together are **~1.8 ms**, before and after — pre-sizing a list does not make a tree walk faster. The
12 ms was garbage collection, and what the fix removes is the garbage.

Board A (52,230 shapes, 11 layers, board fit), Release scratch harness against a CPU `SKSurface`:

| all layers visible | before | after |
|---|---|---|
| allocation per frame (1600x1000 **and** 3200x2000) | 4,345 KB | **3,319 KB** (-1,026 KB, -23.6%) |
| repaint 1600x1000 | 284-295 ms | 281-285 ms |
| pan 1600x1000 | 282-285 ms | 282-286 ms |
| repaint 3200x2000 (2x DPI) | 437-452 ms | 431-457 ms |
| one zoom step in (2x), 1600x1000 | 137-139 ms | 135-136 ms |

**The real frame is time-neutral and that is expected**: it is rasterization-bound (overview S1c — the
two copper pours are 77% of it), so removing 1 MB of allocation from it does not move the clock. The
overview's S1b table is otherwise **re-measured unchanged** (it reads 288 / 282 / 135 and 441 / 459 /
812; this harness reads 284 / 282 / 137 and 452 / 451 / 828), so **briefs 3 and 4 are still sized
against a valid baseline.**

### The number the fix really moves is frame-time CONSISTENCY

With every layer hidden — the do-nothing floor, a diagnostic configuration and not a user state:

| | before | after |
|---|---|---|
| allocation per frame | 4,332 KB | **3,307 KB** |
| minimum frame at board fit | **4.3 ms, or 19.4 ms** | **6.6 ms, invariant** |

The before column is **bimodal**, and that is the finding: whether a gen0 collection lands inside the
sampled frame decides between 4.3 ms and 19.4 ms. It is also configuration-sensitive in a way real
work never is — give that same before build a larger gen0 budget (`DOTNET_GCgen0size`) or server GC
and its 19.4 ms becomes 4.4 ms, while the after build reads 6.5-6.8 ms under **all three** GC
configurations. The overview's 19.1 ms floor is the GC-landed mode.

**The after build's minimum floor frame is genuinely ~2 ms SLOWER, and the cause is the fix itself.**
This was chased rather than waved away, because doing less work and getting a slower frame is not
something to record as noise. Order-independent, reproducible across 7 launches each way, and
bisected: the index changes alone cost nothing (4.2 ms vs 4.4 ms), and a build that **evaluates the
guard and then runs the query anyway** measures 4.5 ms — so the predicate is free and the slowdown
comes from *not allocating*. Fewer gen0 collections leave the bump allocator walking colder pages for
the ~822 KB `live` list the very next statement builds. It does not appear on any frame that draws
anything, and it is an artefact of a floor test, not a cost the application pays.

**A methodological trap worth keeping**: the floor frame's wall clock is bimodal *per process launch*
— the same binary on the same input measured x2 0.4 ms apart across two launches. Single-run A/B
comparisons of it are worthless; only the allocation counter is stable to the byte (identical in
every launch), which is exactly why the brief put the gate on allocation and not on a clock.

### Two things the brief asserts that measurement refuted

**R-rf1-4 was measured and rejected — the comparison delegate stays.** The brief expected a struct
`IComparer` sorted through `MemoryExtensions.Sort<T, TComparer>` to remove ~800,000 indirect calls.
It is **~2.5x slower** at every result size (51,378 entries: 10.1 ms against 4.0 ms; 24,352: 1.27 ms
against 0.79 ms; 9,846: 0.47 ms against 0.22 ms). `List<T>.Sort(Comparison<T>)` reaches an introsort
specialized on the delegate; the generic-comparer span sort did not specialize the same way here. The
rejection is recorded in the code beside the sort so nobody "optimizes" it back.

**The naive one-part guard's failure is NOT observable the way the brief describes.** The brief says
skipping on `view.Instances.Count == 0` alone would leave stale entries "to be drawn and to widen
`Extent`". Neither half holds as written:

- **Not drawn.** The renderer draws what the *query returned*, and a guard that skips the query
  returns nothing — so `InstancesDrawn` is 0 either way.
- **`Extent` does not shrink on delete in the first place**, with this guard or without it.
  `RemoveEntry` deliberately never shrinks an ancestor node's bounds ("no bounds-shrinking /
  rebalancing here" — an over-large bbox costs query efficiency, never correctness, and R-L2b-2's
  churn-triggered rebuild is the backstop). Asserting the brief's wording fails **identically with
  this change reverted** — verified, not assumed.

The guard is still the right one, and the one-part version is still wrong: what it actually breaks is
that the **eviction never happens** and `_instancesDirty` stays set indefinitely. That is what gate 2
pins, on `InstanceRefreshCount`. Getting there took two wrong assertions, both recorded in the test:
`InstancesDrawn` does not discriminate, and **querying the index to look for the stale entry repairs
it** — the combined query refreshes on the way in, so the probe destroys the state it came to observe.
Both wrong versions were confirmed to pass with a naive guard installed; the final one goes red.

### Gates

`tests/Ui.Tests/LayoutFlatDocumentInstanceQueryTests.cs` — six, and **not one asserts a millisecond
figure**: the query count (the whole brief in one assertion), the eviction above, an instance-bearing
document whose counters and pixels are untouched, the frame-one extent flicker S3 names, an allocation
ceiling, and the ordering contract over randomised rects of wildly different selectivity. Gates 1 and 2
were both verified to go **red** against a deliberately-installed one-part guard.

The class sits in `CellStatGlobalsCollection` rather than the typeface one: its fixtures carry no
label, but every render in it resolves placed cells and each test invalidates the resolver, which is
the process-global traffic that collection exists to serialize.

## RF2 — a pinch gesture was a 100% cache miss, and bucketing it up needed a finer ladder than the brief thought (2026-09-12)

`docs/sonnet-briefs/brief-rasterfill-2-widen-key-bucketing.md`, the second of the raster-fill series.
`DrawLayer` computed the stroke-elision tier's widening allowance straight from the zoom —
`ceil(2 device px / devicePxPerDbu)` — and that number is `LayoutPathCache`'s key for the widened
outline (`Entry.WidenedAtDbu`). At board fit on the reported board `devicePxPerDbu` is ~3.08e-5, so
the key is ~64,935 and a **0.1% zoom change moves it by ~65**. Different key, total miss, every
hairline path on every visible layer rebuilt — on a gesture that produces such a frame dozens of times
a second. `LayoutRenderDetail.ToleranceDbu` two lines away had been bucketed to a zoom octave for
exactly this reason since L2c; the widening never was.

It now goes through `LayoutRenderDetail.WidenDbu`, which buckets it to a rung of an **eighth-octave**
ladder. `src/Cli` and the GUI share it, since both draw through `LayoutRenderer`.

### The direction is UP, and that is the part worth writing down

`ToleranceDbu` buckets **down** and is safe doing so: a finer decimation tolerance than was asked for
only tightens an error bound. The obvious symmetry is a trap, because this number is not an error
bound — it is a **visibility substitution**. The widened fill stands in for a fill plus the pen that
would have outlined it, so a widening that came out *smaller* than that pen draws hairline artwork
thinner than the frame meant to, and sub-pixel geometry starts to disappear. That is the exact defect
the tier exists to prevent (it is why the closed drill-chart glyphs vanished when outlines were first
dropped wholesale). So the bucket is always >= the raw allowance, and the price is paid as an
over-cover instead.

### What the over-cover costs, measured — and why the brief's own premise was wrong

The brief expected whole-octave buckets, on the reasoning that "the over-cover is at most one further
device pixel, so the change is expected to be imperceptible", and asked for a differential render
rather than an argument. The differential render refutes it. Oracle: ink coverage (1 - luminance,
averaged over the frame) of the substitution against the real fill-plus-outline at the same zoom, on
**separated** one-mil traces — an abutting pour hides this, because its strokes overlap and only the
pour's rim moves, where a routed layer's traces each get visibly fatter.

| ladder | widening, worst zoom in a bucket | ink vs the true pen | verdict |
|---|---|---|---|
| whole octave | up to 2.000x the pen | **45.8%** | plainly visible |
| half-octave | 1.414x | 19.0% | plainly visible |
| quarter-octave (the brief's fallback) | 1.189x | 8.6% | still visible |
| **eighth-octave (shipped)** | **1.090x** | **4.3%** | a 0.18 device-pixel step in drawn width |

4.3% is the band `DefaultHairlineWidthDevicePixels`' own note already calls antialiasing-level, and it
is what a viewer would see as a POP on the one frame that crosses a rung — inside a bucket the drawn
widening drifts continuously with the zoom and there is nothing to see at all.

**Going two halvings past the brief instead of one costs almost nothing**, which is what made the
choice easy: against the gesture that provoked the work (0.1% zoom a frame) an eighth-octave bucket
rebuilds the working set once every ~90 frames rather than every frame, and against a brisk pinch (an
octave in a second, ~1.2% a frame) 8 times rather than 60. Sixteenths would halve the step again and
double the rebuilds, and **the rebuild is the expensive event this exists to make rare** — so the
ladder is not a free knob to turn down.

### Before and after

The reported board is not in the repo, so this is a synthetic stand-in of the same shape: 21,600
abutting one-mil scanline strokes over two pour layers, 1600x1000, board fit (a 1-mil trace is
0.813 device px there, so the whole document is in the hairline tier). Release scratch console against
a CPU `SKSurface`, warm cache, mean of 12 frames.

| | before | after |
|---|---|---|
| repaint, zoom fixed | 10.4 ms, **0** paths built | 10.2 ms, **0** |
| pan, zoom fixed | 11.2 ms, **0** | 9.9 ms, **0** |
| **zoom 0.1% a frame** | **23.0 ms, 54,630 paths a frame** | **8.2 ms, 0 paths a frame** |
| zoom 1.2% a frame | 18.3 ms, 49,705 a frame | 10.8 ms, 7,982 a frame |

**The +121% here against the brief's +17% on the real board is not a discrepancy**, and the brief's
figure is the one to quote. This fixture rasterizes in 10 ms where board A's frame is 288 ms and 77%
rasterization (overview S1c), so the same absolute rebuild is a much larger share of it. The counters
are the part that transfers: a micro-zoom frame rebuilds **nothing**, where it used to rebuild
everything.

### Gates

Six, in `tests/Ui.Tests/LayoutHairlineFillTests.cs`, and **not one asserts a duration**: the key is
constant across a bucket and steps at the rung; the widening is never narrower than the raw allowance
at any zoom across eleven decades (and never more than an eighth-octave wider); a micro-zoom frame
reports `PathsConstructed == 0`; crossing a rung rebuilds once and the frame after it rebuilds
nothing; a pan still rebuilds nothing; and the differential render above, bounded at 6% — which is
what separates the shipped ladder from the next coarser one, so a quiet re-coarsening goes red.

Verified to bite, not assumed: with the old raw expression restored, the two counter gates fail. With
a quarter-octave ladder installed, the ladder gates fail.

### Two things found on the way

- **`OctaveMantissas.Length` IS `BucketsPerOctave`.** The first draft had the count as its own
  constant beside an eight-entry mantissa table, and the measurement sweep that produced the table
  above silently measured a ladder that was neither four- nor eight-per-octave — a four-entry count
  read against an eight-entry table gives plausible-looking numbers for a ladder nobody chose. Tying
  the count to the table's length is why the second sweep's numbers are the ones recorded.
- **The placed-cell path has the same defect and is deliberately untouched.**
  `LayoutRenderer.Instances.cs`' `CompiledChunk.Elided` and `CompiledLayerGeometry.Coarse` are both
  keyed on `grow`, a raw float function of zoom, so a continuous zoom misses them every frame too. It
  is out of this brief's scope (one expression in `DrawLayer`) and it is not a mechanical extension:
  `grow` also decides which chunks *collapse*, so bucketing it changes a tier decision and not just a
  cache key. It costs per chunk rather than per shape, and the reported board has no instances, so it
  was never in these measurements.

### One test failure attributed, not fixed

`LayoutFlatDocumentInstanceQueryTests.FlatDocument_SteadyStateFrame_IssuesExactlyOneSpatialQuery` (the
RF1 gate) failed once under the full `Layout|Render` filter and passes alone and on a re-run of the
same filter (3,451/3,451). Nothing here touches the spatial index or that guard. Load-dependent, and
recorded rather than chased.

## RF4 (= L2d) — the tiled raster cache, and the three things that were not the seam (2026-09-12)

`docs/sonnet-briefs/brief-rasterfill-4-tiled-raster-cache.md`, the last of the raster-fill series and
the tier `docs/design/layout-view.md` §5.3 deferred "until measured". The measurement is below and
that deferral is now discharged; background/incremental rendering stays deferred and unmeasured.

`LayoutTileCache` (new) is the cache; `LayoutRenderer.Tiles.cs` (new) is what goes into a tile.
`LayoutCanvas` owns one per open document. **The tier is off unless the CALLER supplies a cache**, so
every export, every one-shot render and every existing test is untouched by construction — which is
what makes R-rf4-8 and gate 7 structural rather than a flag someone has to remember.

### The condition the brief opened with, discharged: the shortfall is undiminished

The brief forbids building this before re-measuring after briefs 1, 2 and 3. Re-measured on the
reported board itself (52,230 shapes, 11 layers, zero instances — the overview's §1a counts to the
shape), Release scratch console against a CPU `SKSurface`, fitted to the board with every layer
visible:

| board A, pan | overview §1b (pre-series) | after RF1+RF2+RF3 | **with RF4** |
|---|---|---|---|
| 1600×1000 | 282 ms (3.5 FPS) | **300 ms (3.3 FPS)** | **9.0 ms (111 FPS)** |
| 3200×2000 (2× DPI) | 459 ms (2.2 FPS) | **408 ms (2.4 FPS)** | **7.0 ms (143 FPS)** |
| repaint 1600×1000 | 288 ms | 300 ms | **9.5 ms** |
| repaint 3200×2000 | 441 ms | 419 ms | **7.9 ms** |

RF1 and RF2 were never going to move this and did not: RF1's own note already records the frame as
rasterization-bound, and RF2 is about a zoom gesture. **RF3 removes the geometry at the source but
only on IMPORT** — a board already sitting in a workspace still has its 41,420 strokes, which is the
case this tier exists for and the reason the brief calls it "the only thing that helps a board already
in a workspace". Board B (33,283 shapes, the same class) goes from 105 ms to 5.2 ms at 1× and 194 ms
to 5.5 ms at 2× DPI.

**Against §5.1's targets: pan and repaint now meet 60 fps at both DPIs, on both raster-filled boards,
with room to spare. A continuous ZOOM gesture is unimproved** — see the R-rf4-4 decision below, which
is a deliberate choice with a measurement behind it and not an oversight.

### How a tile is placed, which is the whole of R-rf4-1

The grid is laid over `u = (dbu - origin) * zoom` — the frame's device coordinate with its
translation removed — so tile `tx` covers `u ∈ [tx*512, (tx+1)*512)`, a function of the origin and the
zoom alone and therefore **identical at every pan position**. A pan slides across tile boundaries
instead of re-anchoring the grid. Gate 1 is one assertion on that: a pan back across ground already
rasterized reports `TilesBuilt == 0` and blits what is on screen. Measured on board A, the pan that
re-visits its own ground builds 0 tiles and blits 12 (1×) or 35 (2× DPI) per frame.

A tile is blitted at a **whole device pixel**, and the frame's own matrix is snapped with it. A raster
reusable across a pan cannot follow the pan's sub-pixel phase; snapping per tile would put a one-pixel
step down every seam, so the frame's translation is rounded ONCE and every tile offsets from it by an
exact multiple of the pitch. The whole committed layer — and the rulers, ports, selection outlines and
handles drawn over it, which share the snapped matrix — is then displaced together by under half a
pixel, and a drag-pan advances it in whole pixels with no jitter (the displacement is `frac(transX)`,
and a pan changes `transX` by whole device pixels). Leaving the overlays on the unsnapped transform
was the first draft and is worse: a selection outline half a pixel off the shape it is around is
something a user can see, in exchange for agreeing with a frame nobody is drawing.

### R-rf4-7 is the hard part, and the honest answer has three parts

The seam approach is **rasterize a padded region and blit only the core** (8 device pixels of pad, 6.3%
more area). A tile clipped at its own edge antialiases every shape against that edge and two such tiles
composited leave a lighter line down the join; with the padding, the pixels at the core's edge were
produced with the neighbouring geometry present. **That part works, and it is measured rather than
argued:** on every fixture the differing pixels are no denser at a seam than away from one, and with
the padding set to 0 the gate goes red at **2.6% of pixels on the seams against 0.014% off them**.

What full-frame bit identity actually turns out to be, after three separate causes were found and two
of them fixed:

| geometry | tiled vs un-tiled |
|---|---|
| axis-aligned edges | **bit-identical**, at every pan offset, across seams |
| a diagonal edge | every edge pixel differs a little — one triangle, 1,185 of 1,600,000 px, max delta 23 |
| a curve | same — one circle, ~600 of 1,600,000 px, all on the curve |
| board A (round caps everywhere) | 45–345 px of 1,600,000 (**0.003–0.022%**) at whole-pixel pan offsets |

**Neither residual is a seam and neither is fixable here.** A curve is tessellated *adaptively at the
current transform* — which this renderer relies on deliberately (`LayoutRenderer`'s own header says so)
— and a tile's transform carries a different translation. A diagonal is clipped to the device bounds
before rasterizing, and a clipped diagonal's fixed-point slope is not quite the unclipped one; more
padding does not change it, and the difference is identical at every pan offset. So the gates assert
**identity for axis-aligned geometry** and a **bound plus a not-at-the-seams profile** for the rest,
and this note says that rather than claiming more.

### The three things that were NOT the seam, all of which looked like one

Each was found by a pixel comparison and none of them would have been visible in a screenshot.

* **Compositing into a transparent tile is not associative in bytes.** The first draft cleared each
  tile to transparent and composited the tile onto the frame's background. That is exact in real
  arithmetic and is not in 8-bit premultiplied: a 35%-opacity layer is stored as `round(C * 0.35)`, and
  eleven of them over one another accumulate an error the un-tiled frame never pays, because it starts
  from an opaque background and never holds a partially transparent intermediate. **0.2–0.8% of pixels
  differed, by up to 34 of 255, spread evenly over the frame.** The fix is that a tile starts from the
  same opaque background the frame does — which makes the arithmetic identical step for step and takes
  the figure to 0.017%.
* **The grid had to come inside the tile, and then be in the right place.** An opaque tile would
  otherwise paint over the frame's grid. R-rf4-2 puts the grid outside the cache and this is the one
  place that requirement has to be read for what it is *for*: chrome describing a gesture in progress,
  which a stale tile would freeze. A grid is nothing of the kind — its pitch is a function of the snap
  step and the zoom, both of which are in the tile key, and its dots sit at document multiples of that
  pitch. The first draft folded the frame's whole-pixel snap into the tile's grid viewport as well as
  into the blit, **applying it twice** and putting the grid a couple of hundred pixels off the metal:
  nothing threw, and **5.6% of the frame differed**, which is about what a whole grid in the wrong place
  comes to. Nothing in a tile may reference the pan; that is the rule, and the grid viewport is derived
  from the tile's own grid position only.
* **A label's stored bbox is a zero-size POINT, and a port's is unbounded.** The per-tile shape filter
  first asked `LayoutGeometry.BboxOf`, whose label answer is the anchor — correct for the marquee
  predicate it exists for, and here it dropped exactly the text whose anchor fell outside a tile while
  its glyphs reached well inside, which showed up as a four-pixel diagonal of a glyph stroke missing
  and nothing else anywhere. Switching to `LayoutSpatialIndex.ConservativeBboxOf` (defined as
  never-too-small, which is the right property for this question) fixed it and **immediately made
  things worse**, because a PORT's conservative box is `Everywhere`: every tile then collected every
  port, the frame's top pass drew each glyph once per tile, and a glyph drawn over itself antialiases
  darker. `DeferredPort` now carries the shape index and the gather dedupes on it.

### R-rf4-4: measured both, shipped "re-tile on settle"

Tiles are keyed on the **exact** zoom. A frame at a zoom the previous frame was not at finds no tiles
and draws live, exactly as today; the first frame that repeats a zoom is the one that tiles. This needs
no gesture plumbing at all — which matters, because there are several ways to zoom and only some of
them are a gesture — and it is the only one of the two answers that can satisfy R-rf4-7.

The alternative was measured rather than argued away. Rasterizing at the octave base and scaling the
blit within the octave, board A:

| into the octave | live frame (1×) | scaled blit | ink error | **edge sharpness retained** |
|---|---|---|---|---|
| ×1.09 | 333 ms | 4.0 ms | 0.48% | **88.4%** |
| ×1.41 | 265 ms | 2.3 ms | 1.65% | **84.4%** |
| ×1.99 | 167 ms | 2.3 ms | 3.24% | **79.3%** |

**The ink error is not the number that decides it.** At 0.5–3% it sits in the same band RF2's own
differential render called antialiasing-level. What decides it is the last column: a mid-octave frame
has lost 12–21% of the frame's edge energy, and the artwork in question is one-mil traces, where the
edge IS the trace. So the gesture would be 70–100× cheaper and would show a visibly softer picture of
exactly the geometry the user is looking at, and no frame during it would be one the renderer would
otherwise have drawn.

**What that costs, stated plainly: a continuous pinch on this board is exactly as slow as it was.** A
discrete zoom (a wheel click, a zoom box, a keyboard step) pays one live frame and is fast from the
next one — the settled frame at 2× measures 2.2 ms at 1× and 5.4 ms at 2× DPI. **The obvious follow-up
is a hybrid** — scaled tiles *while the zoom is moving* and exact tiles once it settles, so the
softness lasts only as long as the gesture — and it is deliberately not built here: it is the riskier
half of an already high-risk brief, and it is now a decision with numbers behind it rather than a
guess.

### Memory: 96 MB cap, 16.6 MB and 43.4 MB held

R-rf4-5, and the owner asked directly during the instance-raster work whether that tier's problem was
memory. It was not, and this one has not made the answer yes. The cap is on BYTES rather than a tile
count, because a tile's size follows the device scale and counting tiles would cap two different
amounts of memory on two different displays. Board A, one viewport's worth in steady state:

| | tiles held | held | cap |
|---|---|---|---|
| 1600×1000 | 15 | **16.6 MB** | 96 MB |
| 3200×2000 (2× DPI) | 40 | **43.4 MB** | 96 MB |

96 MB is 48 tiles at 528 square in RGBA8888 — about four viewports at 1× and two at 2× DPI, so a pan
has somewhere to pan back to.

**A fourth thing that would have been silent, and it is a use-after-free rather than a wrong pixel:**
a `Put` can EVICT, and an eviction disposes the tile's `SKImage`. Storing each freshly built tile as it
was built therefore freed images that earlier tiles of the same frame were still waiting to be
blitted — reachable only with a cache too small to hold one viewport, which is exactly what an
undersized cache is. The frame now builds every tile, blits every tile, and only then stores them, so
an eviction can reach only a tile this frame has finished with. Gate 8 pins it by rendering through a
two-tile cache and requiring the same bytes a cold cache produces.

An `SKImage` owns native pixels and every eviction path disposes it;
`LayoutCanvas` disposes the whole cache when the document rebinds and when the canvas detaches, and
re-creates it on re-attach (a dock rebuild would otherwise leave the tier permanently off with no
symptom but a slow frame).

### Invalidation rides `LayoutChangeInfo`, and has to drop BOTH ends of a move

R-rf4-6. No second notification path: `LayoutCanvas.OnModelChanged` already calls
`LayoutPathCache.Apply` and now calls `LayoutTileCache.Apply` beside it, inside the `RenderLock` the
frame itself holds. A theme, technology or layer-visibility change needs no call at all — all three are
folded into the tile's `VisualKey`, so the affected tiles are simply not found.

**The subtlety is that `LayoutChangeInfo` carries INDICES, not geometry, and by the time a subscriber
is told, the shape has already moved.** The destination comes from the shape's current bbox; the
SOURCE is findable only by asking which tiles drew that index, so each tile records its own padded
candidate set (about 12 bytes per candidate, counted against the cap). Invalidating only the
destination leaves the old pixels behind — the ghost-left-behind defect `LayoutPathCache`'s comment
documents, one level up and inside an image where it is harder to see; only the source leaves a hole.
Gate 9 asserts both, the second by rendering the edited document from a warm cache and from a cold one
and requiring the same bytes.

### Two scope lines, both deliberate, both stated as tests

* **A document with placed cells does not tile.** `DrawInstances` paints screen-space chrome inline
  with a placement's geometry — PCell pins, the interface-changed mark, the moved-cell mark — and
  R-rf4-2 forbids baking a fixed-device-size glyph into a raster. Separating them is a larger job than
  this brief, and a document with placements already has a raster tier of its own: the instance raster
  tier this one was modelled on. Every board in the class RF4 was written for has zero instances.
  `ADocumentWithPlacedCells_DoesNotTile` is the gate, so this cannot be broken silently.
* **The merge tier's decision is the FRAME's, handed down.** Whether a layer uses the batched path is a
  per-frame question (its candidate count in the viewport), and a tile holds only the shapes that reach
  it — so a tile left to decide for itself would composite a layer differently from the frame it stands
  in for, because per-shape fills darken where they overlap and one merged path does not. The frame
  decides, `DrawLayer` is told, and the decision is part of the tile key so a pan that genuinely changes
  it rebuilds rather than showing something stale.

### Gates

`tests/Ui.Tests/LayoutTileCacheTests.cs` — twelve, and **not one asserts a millisecond figure.** That
is the owner's call on the brief's §3 standing conflict: L2c's completion note asked for routine 500k
*timing* coverage as part of L2d's gate, and the standing instruction since is that a timing test
measures the machine; the structural property goes on a counter and the 500k timing sweep stays where
it already lives, in `Category=Benchmark`, needing no new test to be run.

The twelve: a steady-state pan builds nothing; a zoom gesture builds nothing until it settles and then
builds once; bit identity with the tier disabled, both by supplying no cache and by a negative
threshold; bit identity with it enabled at 28 pan offsets chosen to put geometry across seams; the
bounded, not-at-the-seams profile for curves and diagonals; a moved selection reuses every tile and
still tracks; a dragged shape renders live at its dragged position; memory under the cap with eviction
disposing; exact invalidation at both ends of a move; the grid inside the tile and in the same place as
the metal; a document with placed cells refusing; and a comment-stripped source scan proving the tile
file draws no geometry of its own — it runs the caller's committed-geometry pass, the same rule the
instance raster tier states, so the blitted and the live pixels cannot become two definitions of one
thing.

Verified to bite, not assumed: with the tile padding set to 0 the seam gate goes red; with an
axis-aligned-only fixture it does not, which is why the diagonal and curve fixture exists and is
recorded here — an axis-aligned rectangle cannot feel a clip on a pixel boundary, so a rectangles-only
seam test would have passed a genuinely broken implementation.

Gate 7 needed no new test: `RenderCliVerbTests`' byte-for-byte comparison already covers it and is
green, and `render --detail full` cannot reach the tier at all — it supplies no cache, and a negative
`DetailPixelThreshold` refuses independently.

## Owner report, 2026-09-12 — "a shape partially outside the viewport doesn't render at all"

> "When some shapes are partially outside the viewport, the entire shape doesn't seem to render. If I
> pan a little more to get the full shape into view, then it will render." Reproduced on the
> raster-fill board above with **Top Copper alone visible**, which is the detail that made it findable.

RF4's defect, and **two** of them: one that loses pixels and one that hid it.

### The tile was rasterized from the FRAME's candidates, not from its own region

A tile's core reaches up to a full tile — 512 device pixels — BEYOND the viewport, because the grid is
anchored in document space and the viewport lands wherever it lands on it. The first version filtered
the frame's candidate list (the viewport plus an eight-pixel margin) down to each tile, so **the part
of a tile that was off-screen when the tile was built contained nothing at all** — and a cached tile
keeps that hole for as long as its key is valid. Panning onto that ground showed blank, and panning
further eventually invalidated the tile and the content appeared.

A tile is a render of a document REGION, so it has to ask the spatial index about that region.
`LayoutRenderer.Draw`'s candidate → live → by-layer → resolved pipeline is now a local function
(`ResolveRegion`) called once for the viewport and once per tile build. Measured on the reported
board, Top Copper alone, warm cache at one pan against a cold cache at the next:

| pan | before | after |
|---|---|---|
| 1% of a screen | 0 px | 0 px |
| 2% | 24 px | **0** |
| 4% | 3,133 px (0.20%) | **0** |
| 6% | 6,233 px (0.39%) | **0** |
| 10% | 16,884 px (1.06%) | **0** |
| 15% | 46,417 px (2.90%) | **0** |

**The error grows with the pan distance**, which is the signature: the further you pan, the more of a
cached tile's never-rasterized margin you are looking at.

### And the reason it took a single layer to see it: the key churned

The tile key carried two things that move with the VIEWPORT — the frame's per-layer candidate COUNT
(folded in to carry the merge tier's decision) and the frame's resolved layer LIST (whose membership
changes as layers scroll in and out). At a zoomed-in view on an eleven-layer board both move on nearly
every pan frame, so **every tile was invalidated and rebuilt every frame**. That is a cache doing
negative work — and it refilled the holes before anyone could see them. With one layer visible the key
stopped moving, the tiles were genuinely reused, and the defect became plain.

Both are gone. `VisualKeyFor` now hashes the TECHNOLOGY's own layer table rather than the frame's
resolved list (a layer the technology does not declare falls back to `FallbackPalette.For`, a pure
function of the key, so nothing is lost), and the merge decision comes from `LayerMergeMap` — the
layer's **document-wide** visible shape count, one pass over the shape list on a frame that actually
builds a tile, never on a frame that only blits. Measured on the reported board at 4× zoom, a 25% pan:
**12 tiles rebuilt before, 3 after** — the three that are genuinely new.

**Where the document-wide merge count diverges** from the un-tiled frame is a zoom deep enough that a
layer's VISIBLE count falls below `DefaultMergeShapeCountThreshold` while its total stays above: the
un-tiled frame darkens same-layer overlap there and the tiled one does not. That is the price of a
tile being a function of the document rather than of the viewport, and it is the right trade — the
alternative is a key that cannot survive a pan.

### What this says about the earlier gates, which all passed

Every gate in the file passed throughout, and none of them could have caught this: gate 1 asserts
counters on a pan and never compares the pixels; gate 4 compares a tiled frame against an un-tiled one
**at the same viewport**, where the frame's candidate set is exactly right and the hole is off-screen.
The defect needs a pan ACROSS two frames with the tile surviving between them, and nothing was testing
that. Two tests now do:

* `PanningOntoGroundACachedTileCovers_ShowsWhatIsThere` — warm at one pan, render at the next, compare
  against a cold cache at that same pan. **Both halves are load-bearing**: without also requiring that
  some of the offsets were served entirely from tiles that already existed, a frame that rebuilt
  everything would match for the wrong reason, which is precisely what hid this.
* `PanningAtAFixedZoom_DoesNotInvalidateTilesItStillNeeds` — the churn, as its own gate, because it is
  a performance defect in its own right as well as a mask.

Verified to bite: restricting a tile's query back to the viewport's candidates makes the first go red;
mixing any viewport-dependent term into the key makes the second (and gate 1) go red.

### A third oracle, kept because it found nothing and that is worth knowing

Rendering the same world region inside a viewport enlarged by 600 pixels on every side and cropping
back is a culling oracle that does not care which code path is at fault. Post-fix it reads **0.000% at
every zoom tested, including 10×**, where the UN-tiled renderer reads 0.98% to 29.9% — not a bug
there, but the merge tier and the outline budget legitimately deciding differently for a bigger
viewport. It is a neat confirmation that a tile is now a pure function of its document region and of
the zoom, and of nothing else.

### Cost of the fix

Board A, all layers, Release scratch console: pan **9.1 → 10.4 ms** at 1600×1000 and **7.1 → 9.1 ms**
at 3200×2000 — the per-tile spatial query, paid on build frames only. Against 300 ms and 408 ms
un-tiled, and the identity figures above are unchanged.

---

## Two constructs that are right on a raster canvas and do not exist in SVG (2026-09-13)

Both were reported as figure bugs — *"the smith chart rendering does not show the axis grid lines"*
and *"I can't see the port arrowheads … also I don't see the port side lines"* — and both are the
same class of defect: a Skia call that the raster backend honours and the **SVG device cannot
express**, so the application looked correct and every export was wrong. Neither had ever been
caught, because nothing compared a vector export against what the canvas draws.

Both premises are now pinned by tests that assert the Skia behaviour directly
(`tests/Ui.Tests/VectorExportClipAndLayerTests.cs`), because a fix that rests on an undocumented
backend limitation is worth nothing if the limitation is misremembered.

### `SaveLayer` content does not reach the file at all

`AxesRenderer.DrawSmithGrid` drew the constant-R and constant-X family into one `SaveLayer` at full
opacity and composited the layer once — which is what fixed the stippled crossings recorded above,
and is the natural way to say "an overlap is the same colour as a single stroke".

**An `SKSvgCanvas` handed one circle inside a `SaveLayer` emits an empty `<svg>`.** So every
exported Smith chart carried its outline, its real axis and its grid numbers over a blank disc: the
user-doc figures, `circuitrf plot`, `circuitrf render --data`, and the application's own File ▸
Export. Verified by counting: `plot-smith-data.svg` held exactly **one** `<ellipse>` — the r = 0
outline — where the grid is about 27 arcs.

**The replacement needs no layer, and it is a property of Skia rather than a trick: one `DrawPath`
fills its path once however much that path overlaps itself, while N draws composite N times.**
Measured on two crossing circles stroked at 50 % black — one path reads **127** at the crossing,
two draws read **63**. So the whole family is accumulated as its own stroked OUTLINE
(`SKPaint.GetFillPath`) into one `SKPath` and filled once, at the alpha the layer used to carry. A
masked arc is trimmed by `SKPath.Op(…, Intersect)` against the same even-odd exclusion region the
clip used — the region is identical, it just has to be applied to the geometry instead of to the
canvas so everything can share one draw.

**The raster output is unchanged**, which is the thing to check when replacing a compositing
mechanism: the grey-tone histogram of a 520 px light-theme Smith render is the same before and
after to within antialiasing noise (the three real populations, 160 / 184 / 208, come back at
411 / 1011 / 1265 px against 411 / 1012 / 1333).

### `SKClipOperation.Difference` survives — INVERTED

`LayoutRenderer.DrawPortGlyphs` knocks the port's NAME out of the marker pass so the text reads on
top of an arrow that arrives at the same anchor. It did that with a `Difference` clip.

**SVG has no difference operator.** Skia's device writes any clip as a `<clipPath>` holding the raw
path, so on the way out the knock-out becomes an **intersect**: the marker is drawn only where the
name is. In `mom-bend-ports.svg` the whole glyph — the reference-plane bar spanning the port width,
its serifs and the direction arrow — was clipped down to the inside of the numeral "1", which is why
a reader could see neither the arrowhead nor the port's width. On screen it was correct, which is
what kept it hidden.

**An even-odd exclusion says the same thing in a form SVG has**: an enclosing rectangle plus the
hole, filled `EvenOdd`, clipped `Intersect` — the emitted clip carries `clip-rule="evenodd"` and
means what it meant here. `LayoutRenderer.ExclusionOf` builds it, and the rectangle is the union of
the canvas clip and the hole's own bounds, because a hole reaching outside the rectangle would read
as one more crossing and be filled IN rather than cut out. `AxesRenderer.EvenOddExclusionPath` was
already doing this for the Smith masks; nothing else in `src/Render` used a difference clip.
*(That helper is gone as of 2026-09-14 — the Smith masks are an angular trim now; see the
crossings entry at the end of this file. `LayoutRenderer.ExclusionOf` is the surviving
example.)*

**The bug's reach was every port in every export**, not just the one figure that surfaced it: the
same regeneration redrew `ports-edge`, `ports-internal`, `ports-internal-gap`,
`ports-gap-mesh-width`, all three AN-01 coupled-pair figures and both antenna patch figures.

### One more instance, found and deliberately NOT fixed

`ContourRenderer.DrawTopoMapFill` composites its colour bands the same way, through one
`SaveLayer` at `TopoLayerAlpha` — so **a contour trace whose fill is set to Topo map exports with
no fill at all**, by the same mechanism. It is left alone here for two reasons: no shipped figure
selects that fill, so there is nothing to check a change against by eye; and the obvious
replacement is not free of risk. The bands are NESTED `>=`-threshold regions painted over one
another, so they cannot share one path the way the Smith arcs can — each has its own colour. Making
them disjoint annuli (`Geq(k)` minus `Geq(k+1)`) and painting each once at the layer's alpha is the
arithmetic equivalent, but adjacent annuli then share an antialiased boundary and each contributes
partial coverage to it, which shows as a light hairline seam between every pair of bands. Anyone
fixing it should build a figure that actually selects Topo map first, and check the seams.

### What to take from it

A vector export is a **different renderer**, and the parts of Skia it does not implement fail
silently and selectively. Anything that reaches for a layer, a blend mode or a non-intersect clip in
`src/Render` should be assumed absent from an export until a test says otherwise — the two in
`VectorExportClipAndLayerTests` are the pattern.

---

## Stackup cross-section: the via lane spread never measured a via's NAME (2026-09-13)

Owner, testing the Stackup tab by hand: *the name of a via is sometimes drawn partly on a conductor
and partly on the dielectric below it, and it sometimes lands on the next barrel's metal.* Two
defects with one cause each, both in `StackupScene`, both invisible on the two-layer starter
technology because it carries exactly one via.

### The lane spread was three fixed fractions of the column

`LaneCentre` placed slot 0 at `0.68 × bandWidth`, slot 1 at `0.50`, slot 2 at `0.32`, then wrapped to
a second pass shifted left by `1.25 × BarrelWidth`. A via's NAME is drawn immediately to the right of
its own barrel (`ViaNameGap`), so the gap between barrel *i* and barrel *i-1* is the space that name
has to live in — and a fraction of the column knows nothing about how long anything is called. Four
vias on a 900 px pane put ~100 px between lanes, which holds "Via1" and does not hold "Thermal via".

`DefaultLaneCentres` now packs right to left with a **measured** step —
`BarrelWidth + ViaNameGap + nameWidth + NameZoneGap` — and, when the column cannot hold every step,
**compresses them uniformly** rather than running off the edge. The names then overlap, which is the
owner's own stated fallback; a barrel outside the band column would be worse, because it could not be
seen, hovered or dragged back, and R-stk1-8 exists to stop exactly that. `LaneStepFraction` and
`LaneCount` are gone; `LaneMinStep` is the floor the compression cannot go below.

This is also why the barrels are now placed in two passes: **which vias are drawable has to be known
before any of them is placed**, because the step for barrel *i* depends on the whole list.

### The name's ideal y was the barrel's middle, which is a boundary as often as not

A barrel spans from one conductor's top to another's bottom, so its own midpoint is very often inside
the dielectric — but on a stack with several layers it lands on a conductor/dielectric boundary just
as easily, and the text then sits half on metal and half on substrate and reads as neither.

`ViaNameAnchorY` picks a band the barrel CROSSES that is tall enough to hold the text, preferring a
dielectric (taller on nearly every stack, and it carries no on-band name of its own), then a
conductor, then falling back to the barrel's middle. `Separate` still runs afterwards and can still
push a name off that centre — R-stk1-9's no-overlap guarantee outranks this, and does so only when
two names' x intervals overlap, which the new spread avoids unless the column is too narrow.

### "ground ref" became "gnd"

`GroundReferenceText`. The label column WRAPS, and two words took a whole wrapped line on a narrow
pane for something the band's own heavy edge has already said.

Gates: `StackupSceneTests.AViasNameSitsInsideOneBand_OnEveryShippedTechnology`,
`AViasNamePrefersADielectricBandOverAConductor`, `ViaNamesAreClearOfTheNextBarrel_WhenTheColumnHasRoom`,
`WithNoRoomForEveryName_TheBarrelsStillStayInsideTheBandColumn`, `TheGroundReferenceIsLabelledGnd`.

## A via drag now previews the barrel itself (2026-09-13)

Owner: *during drag of the via using the grippers, I only see a non-detailed ghost.* It was a
translucent rectangle — `StackupOverlay.DragGhost`, filled in `StackupRenderTheme.DragGhost` — and
for a BAND reorder that is the right picture, because a band IS a rectangle. A via is not: most of
what it is lives in the detail the ghost threw away — two metal walls, the bore cut through the
material, the outline binding them into one object, a gripper at each end.

**`StackupOverlay.DragVia` carries a whole `StackupBarrel`**, built from the one being dragged with
only its rect and its grippers changed, and `StackupRenderer` draws it through **`DrawBarrel`, the
same method it draws a real barrel with**. That extraction is the point: a preview painted by a
second piece of code is a preview that can come to differ from what it previews.

**`DragViaSource` names the original, which is then omitted** — matched by REFERENCE, because two via
entries may share a name and the scene is not rebuilt until the release. Without it a via reads as
two vias during a span or lane drag, and a RETRACTING gripper drag leaves the old barrel's tail
sticking out past the new end. `Barrels(scene, overlay)` is the one sequence the bore clip, the
barrel pass and the gripper pass all read, so the preview cannot be metal in one of them and a hole
in another.

Two smaller consequences: the preview's grippers are drawn unconditionally (the ends are what the
gesture is aiming), and `OutlinedRectOf` makes the SELECTION outline follow the preview — the press
that starts a via drag selects it, so an outline left framing the hidden original would read as the
via having been left behind.

Gates: `StackupDragTests.AViaDragPreviewsTheBarrelItself_AndHidesTheOneItCameFrom` (three gestures)
and `ThePreviewIsPaintedAsARealBarrel_AndTheOriginalIsNotPaintedAtAll`, which measures a raster
rather than the overlay record — the overlay carrying the right numbers while the renderer draws a
rectangle anyway is exactly the defect this is about.

## An on-band name was measured against the wrong box (2026-09-13)

Owner: *on the shipped PCB 4-layer FR-4, the 8 mil Prepreg (top) moves its name over to the right-hand
list, and there is enough space for it.* There was.

The rule was `FaceHeight(nameFont) + 2 * LabelPadY <= band.Rect.Height`, and the three numbers are
22.90 against a band that draws 20.95 — short by 1.95 px, for text whose own face box is **16.90**.
`LabelPadY` is the label COLUMN's padding, and it is there for one reason: R-stk1-9's guarantee that
no two separately-placed label rects touch. **A name on its own band has no neighbour to be kept from**
— only two band edges to stay inside — so it was being held to a clearance that answers a question it
does not ask.

`OnBandPadY = 1.5` replaces it, and **it is the threshold AND the padding the run is then emitted
with**. That is the part worth keeping: an on-band name's rect is inside its band *by construction*,
which is what keeps R-stk1-9 true of a rect that `Separate` never sees. Two numbers — a loose
threshold and a padding chosen separately — would be two chances to emit a name overlapping the band
above it. `PieceRun` takes the padding as a parameter for this; `LabelPadX` is unchanged, because
horizontal crowding is a real adjacency.

Not smaller than 1.5: the ground reference's edge is `StackupRenderer.GroundEdgeWidth` centred on the
band's own edge and reaches half of that inward.

**What still moves off, and should:** the 1 oz inner planes at 18.00 px, and anything whose name is
wider than the zone left of the leftmost via barrel — the MMIC's "MIM Dielectric" is exactly that
case, at 20.00 px tall and 90.56 px wide. Gates:
`StackupSceneTests.AnOnBandNameIsInsideItsBand_OnEveryShippedTechnology` (the property) and
`TheFourLayerBoardsEightMilPrepregKeepsItsOwnName` (the reported case, named, with the inner planes
asserted still in the column so the threshold cannot simply have been removed).

## The via spec row lost two things it did not need (2026-09-13)

Owner, on row width: drop the word *wall*, and shorten the span's conductor names.

**`plated = 25 µm`, not `plated wall 25 µm`.** "wall" was the only word on that row naming a FIELD
rather than saying something about the via — and on a barrel already drawn as two walls with a bore
between them, it was telling the reader what they were looking at. The `=` is the spelling `σ`, `εr`
and `tanδ` already use on a band, and the value stays its own piece so brief 4 can still double-click
it. A via that is NOT a plated barrel has no wall to state and keeps the bare word (`solid`,
`unplated`): `plated =` with nothing meaningful after it would be worse than either.

**`ElideSpanName`** cuts a span's conductor name with an ellipsis at whichever comes first — an
opening bracket, or a space at or after `SpanNameMinChars = 10`. So "Bottom Copper (1 oz)" draws as
"Bottom Copper…" and "Inner 1 (Ground Plane)" as "Inner 1…". Three rules matter more than the number:

- **At a space, never mid-word.** "Bottom Copper…" still names a layer a reader recognises; "Bottom
  Cop…" is a string that could belong to two of them. A name with no space past the budget and no
  bracket is therefore returned WHOLE — there is nowhere to cut it that leaves it readable, and the
  label column wraps, which is a worse look but not a wrong one.
- **A bracket cuts wherever it falls, even before the budget.** What follows one is a qualifier
  rather than part of the identity — "(1 oz)", "(Ground Plane)" — so the budget does not apply to it.
  The first cut tried was space-only, and it gave "Inner 1 (Ground…": six more characters than
  "Inner 1…" and a bracket left hanging open. A bracket at index 0 is refused (it would empty the
  name) and the space rule takes over; the length guard means none of this runs on a short name at
  all.
- **`WidestToken` measures the ELIDED spelling.** It is what sizes the band column against the label
  column, and measuring the full names there would yield width to a token the scene never emits —
  which is the whole of what the elision was for.

It is display-only and safe to be: `StackupField.Span` is a pair chosen from the card's combo boxes
and brief 4 refuses to type it, so no elided string is ever parsed back. `UnresolvedSpanText`
deliberately does NOT use it — a refusal has to quote the exact name that failed to resolve, or it
sends the reader looking for a layer that was never spelled that way.

Gates: `StackupSceneTests.ASpanNameIsElidedAtAWordBoundaryOrNotAtAll`,
`TheDrawingShowsTheElidedSpan_NotTheFullConductorName`, `ARefusalQuotesTheSpanNameWhole`,
`APlatedWallIsWrittenAsAnEquality_AndTheWordWallIsGone`, `AViaWithNoWallKeepsTheBareWord`.

## The label column's vertical padding was three times what it needed (2026-09-13)

Owner: *too much vertical padding in the text on the right side — reducing it will make it easier to
align the text row with its cross-section band.* Both halves were right, and the second is the
mechanism.

`LabelPadY` was 3, so every line of every group carried 6 px it did not need. That is not just
wasted ink: every CLUSTER of groups was that much taller, and `Separate` re-centres a cluster about
the MEAN of its members' ideal centres — so a taller cluster displaces each of its members further
from its own band. The padding was buying misalignment.

**It is not what keeps two labels apart**, which is what lets it be small: `LabelGap` (2) separates
two GROUPS and `LineGap` (1) two wrapped lines of one, and both are strictly positive on their own
account — R-stk1-9 rests on those. What the padding still buys is a pixel of slop around the glyphs
for brief 4's double-click, which is why it is 1 and not 0.

Measured over the five shipped technologies plus a 20-band hairline stack, at 900 px and 620 px —
distance from a thickness label's centre to its band's centre:

| `LabelPadY` | mean | worst |
|---|---|---|
| 3 (before) | 39.4 | 116.0 |
| 2 | 26.6 | 77.3 |
| 1.5 | 20.6 | 68.5 |
| **1 (now)** | **14.8** | **62.0** |

Shipped technologies alone at 900 px, which is the case the gate pins: mean **4.25**, worst **11.58**
(was 29.1). `ASpecRowLinesUpWithItsBand_OnEveryShippedTechnology` holds worst ≤ 20 and mean ≤ 8 —
bounds chosen to fail at the old padding rather than merely to pass at the new one — and
`TheLabelPaddingIsNotWhatKeepsTwoLabelsApart` holds the reason it is allowed to be this small.

The residual at narrow widths is inherent and not a padding problem: below ~620 px the column wraps
a dielectric's spec onto three lines, groups get tall, and clusters displace. That is what
`LabelColumnDropWidth` exists for.

## The Smith grid punched a hole out of every crossing (2026-09-14)

Reported as *"the Smith Chart axes grid has gaps where the grid lines overlap — looks almost like
an XOR."* It was exactly an XOR, and it was the cost of the fix recorded directly above.

`AxesRenderer.DrawSmithGrid` accumulates the constant-R and constant-X family into ONE path and
draws it ONCE — it has to, because N draws composite N times and the grid then goes dark wherever
it is busiest, and it may not use a `SaveLayer` because Skia's SVG device drops one. That path held
each arc's **stroked outline** (`SKPaint.GetFillPath`) and was FILLED with a winding rule. The arcs
that carry a mask were trimmed with `SKPath.Op(…, Intersect)`.

**A pathops result does not carry the contour orientation `AddCircle` plus the stroker produce, and
it is not even the same orientation from one call to the next.** Under a winding fill two pieces of
opposite sign CANCEL where they overlap, so every crossing between such a pair was erased: a white
rhombus the size of the two stroke widths. At the 0.5 default `GridThicknessFactor` that is a 31-px
scatter of single-pixel pinholes across the whole chart — small enough that the change's own
histogram check (three grey populations, 411 / 1011 / 1265 px) missed it by 68 px of one tone. At a
heavier grid it is unmistakable.

**The family is now the arcs' CENTRELINES, stroked in one draw.** A stroke has no such failure
mode: Skia strokes the whole path in one pass and orients its own output consistently, and one
`DrawPath` is one coverage mask however much the path overlaps itself. The masks are applied as an
**angular trim** — `AccumulateArc` removes the spans of a circle that fall inside the masking
circles, which is exact for circles and needs no pathops at all. The masking circles here are
pairwise TANGENT (an x-arc and its conjugate meet only at Γ = 1, as do two r-circles), so removing
the union of their spans says exactly what the even-odd exclusion region used to say; that is why
the trim points did not move.

Three things worth keeping:

- **It is also three times faster.** A full 520 px Smith plot draws in **0.56 ms** against
  **1.68 ms** — no `GetFillPath` and no path ops at all. The obvious alternative fix, accumulating
  by `SKPathOp.Union` so orientation stops mattering, is correct and costs **12.7 ms**: it was
  measured and rejected. `SKPath.Simplify` is not a fix at all — it normalises a path while
  PRESERVING what its fill rule draws, so it preserves the holes.
- **The export improves too.** The grid leaves as one `<path fill="none" stroke=… stroke-opacity=…>`
  with a subpath per arc, rather than a filled outline path; overlaps inside one SVG element
  composite once there as well. `VectorExportClipAndLayerTests` was looking for the filled form and
  now looks for the stroked one.
- **Butt caps, not `StrokePaint`'s square ones.** A trimmed arc's end is a cut, and a square cap
  pushes it half a stroke past the circle it was cut at.

The gate is `tests/Ui.Tests/Render/SmithGridCrossingTests.cs`: one test pins the Skia premise
(a pathops piece cancelling an ordinary ribbon it overlaps, where a stroked centreline cannot), one
walks eight crossings computed from Γ = (z−1)/(z+1) and asserts none is blank, and one asserts a
crossing reads the same tone as a plain arc — which is the property the one-path construction
exists for, and the one a naive "just draw each arc" fix would break. The disc's centre and radius
are MEASURED off the render rather than re-derived from the viewport arithmetic, so the test says
nothing about plot-area layout.

**Any regenerated figure carrying a Smith chart will change** — the crossings fill in.

## The drag/placement ghost's outline was dashed; it is the committed outline now (2026-09-15)

Owner report: dragging out a rect, circle or polygon in the layout editor showed a ragged, broken
edge that changed the instant the gesture ended. `LayoutRenderer.DrawGhostShape` stroked the
in-progress primitive with a DASHED, alpha-220, `StrokeWidth = 0` hairline, where `DrawLayer` gives
the committed shape a SOLID outline `GeometryStrokeDevicePixels` (2 device pixels) wide at full alpha.
Two differences at once — dash, and half the width — so the ghost's edge could not have matched.

The fix is to use the committed paint, and it **retires R-dgf-3's last "provisional" marker on
purpose**. That brief had already raised the ghost's FILL from a fixed alpha=60 to the layer's own
`FillOpacity` for exactly this reason, and kept the dash as the surviving signal that a shape was not
yet placed. The edge is the part a user judges a placement by, so carrying the signal there is the
most expensive place to carry it: a placement preview exists to show what will land. Provisional-ness
is already carried by the gesture — the shape is under a held pointer and follows it. Nothing else
changed: committed geometry still strokes in `DrawLayer`, and the overlays that genuinely are chrome
rather than content (marquee, selection outline, a stale instance) keep their own dashes.

**The old gate asserted the bug.** `LayoutWindingNormalizationTests.DrawingGhost_EdgeShowsDashVariation_NotAUniformSolidStroke`
required >40 of on/off contrast variation along the edge, so it had to be replaced rather than kept —
it is now `DrawingGhost_EdgeIsSolid_AndIsTheCommittedShapesOutline`, which renders the same shape
committed and as a ghost and asserts the edge band matches pixel for pixel (worst difference measured
0 of an allowed 8; reintroducing the dash measures 197.3, verified by actually putting it back rather
than assumed).

**Its solidity half needed a per-COLUMN oracle, and a per-pixel one would have been vacuous.** The
sampled band straddles the edge, so it necessarily contains pure-background rows above the stroke and
fill rows below it: a plain min-over-the-band reads 0.0 whether the stroke is dashed or solid, which
is how the first attempt at this assertion failed against a correct renderer. Taking each column's
MAXIMUM first isolates the stroke, and that figure is constant to within antialiasing along a solid
line and collapses to the fill's contrast at a dashed one's gaps. The retired test had the same blind
spot; its threshold happened to be met by the background rows.

**Not changed, and still dashed: the SYMBOL editor's in-progress primitive**
(`SymbolEditorRenderer`, the `overlay.InProgressPrimitive` branch). The report was about the layout
editor, and that one differs by more than a dash — it draws in `theme.GhostBody` rather than the
primitive's own colour, so matching it to its committed appearance is a separate change.

## A plot's ANNOTATION traces were treated as data by every marker gesture (2026-09-19)

A plot can carry traces that are not data — a target ceiling, a vertical line marking a frequency
something else happens at. They are `Trace`s because the Data Display has no bespoke chart to draw
them any other way, and they carry two points rather than the sweep's grid. Two reports, one cause:

- **The multi-marker readout.** `GetCubeMultiMarkerLine` reads every other trace at the marker's own
  **X sample** and answers `"NaN"` where the other trace's `_cubeXValues` is a different length —
  which is exactly what a two-point annotation is. On railRF's |Z| plot: eleven `NaN` rows beside
  two readings.
- **Add Marker.** `RefreshAddMarkerSubmenu` lists one row per trace, so the same eleven were offered
  as places to put a marker and buried the two that answer anything.

`Trace.IsAnnotation` is the trace's own statement, and it is the Data Display's word for *drawn like
a trace, not treated as one by any marker*. It is honoured in both multi loops of
`BuildMarkerBoxLines`, in `RefreshAddMarkerSubmenu` (both the listing **and** the enablement count,
so a plot of only annotation greys the item rather than opening an empty submenu), and in
`TryAddMarkerNearPoint` — the last of those because filtering the menu alone would leave a
double-click near an aggressor line putting a marker on a trace the menu deliberately does not
offer.

Two things worth keeping:

- **Not "drop any row that reads NaN".** That would hide a genuine NaN in a real curve, which is a
  reading a user needs. The flag says the row is not a reading at all.
- **Filtering inside `BuildMarkerBoxLines` rather than at the caller is load-bearing** — the
  renderer MEASURES the info box with that function and then DRAWS it with the same one, so a filter
  applied at one of the two sizes the box wrong.

## A trace cut TO the window cannot also decide it (2026-09-19)

`Trace.ExcludeFromAutoscale`, honoured in `Plot.AutoscaleCore`. railRF marks each aggressor
frequency with a two-point vertical trace spanning the current Y window; with that trace in the
autoscale, the window it was drawn from becomes the window it produces and every curve is squashed.

That was held by **order** — the lines were added after the autoscale, and `RailRfViewModel.Response.cs`
said so in capitals — which works exactly until something else autoscales. The Plot Inspector does,
on every edit, which is what turning it on for railRF exposed.

**Separate from `IsAnnotation` on purpose.** A target ceiling is annotation and *should* frame the
plot: a ceiling off the top of the window is a verdict the reader cannot see.

## The Plot Inspector assumed every trace came from the data-source library (2026-09-19)

Turning the inspector on for a window that publishes its own `DataSet` emptied every curve on the
first edit — 425 points to 0, no error on the card, no message anywhere. `RebuildAndNotify`
re-resolves each cube trace from its **source**, and the source lookup was hard-wired to
`LibraryDataSources` — files on disk. railRF has no library; it publishes a fresh `DataSet` per
solve, the way harmonicaRF does. (That is exactly why harmonicaRF has its own trace picker instead.)

`IPlotDataSources` was already the answer and already had a second implementation — `circuitrf render`
resolves against the files a *caller* named (`src/Cli/CddSources.cs`). So:

- `PlotInspectorViewModel.SetDataSources(IPlotDataSources)`, defaulting to the library as before.
  It does **not** replace the library everywhere: the library still drives what an *Add trace* can be
  seeded from and what a source alias reads as, neither of which a fixed-trace-set plot has.
- `ResolveOrRebuild` replaces the three open-coded resolves. An **annotation** trace takes
  `BuildPath` instead of a library lookup: it has no source, it was handed its values directly, and
  `BuildPath` re-derives its points from what it holds — which re-applies its `Transform`, and that
  is the whole mechanism by which a ceiling stored in ohms follows the curve it is judging into dB
  and back.
- `Plot.IsFixedReadout` — *the inspector may restyle this plot but not re-aim it*. **Hidden, not
  greyed, throughout**: a permanently grey control is one the user goes on trying, and several of
  these are comboboxes that would sit empty at the width of the panel. It governs four things, and
  they are one fact rather than four — each re-aims a trace (or the plot) at something the owner's
  next rebuild aims straight back:
  - the **plot type** header (`CanChangePlotType`). Not merely cosmetic: `SetPlotType` restructures
    the trace list and the owner's rebuild does not put the type back, so a stray click leaves the
    plot broken with no way to say so.
  - the **trace set** (`CanEditTraceSet`) — `+ Trace` and every card's trash.
  - each trace's **data selection** (`TraceRowViewModel.CanPickTraceData`) — the source combo, and
    `vs X` via `ShowVersusRow`.
  - the card's **identity row** (`ShowIdentityRow`), hidden whole rather than control by control so
    its two star columns cost no width: the "(load a file…)" group combo, the quantity picker, the
    matrix type and the right-axis toggle.

  It governs **nothing** about how a trace looks — transform, colour, line, symbols, markers — which
  is the whole point of still opening the panel.
- `RebuildTraces` gives no card to an annotation trace, and `ReloadTraceCards()` is how a host that
  produces its own trace list says it has replaced them. In the Data Display a trace only ever
  arrives *through* the panel, so the two stayed in step with no synchronisation at all — which is
  why railRF's inspector opened on zero cards over a plot holding thirteen traces.

---

## The Smith Chart's plot half arrived here in SMITH-10 (2026-09-19)

`brief-smith-10-cli-verb.md` R-smith10-3. `src/Render/Smith/` holds `SmithPlotBuilder`,
`SmithChartScene`, `SmithMarkerBridge`, `SmithOverlayResolver` and `SmithChartChrome`, so
`circuitrf smith` draws a `.csmith` with the code the window draws it with.

**The four that moved needed no change at all.** They referenced `CircuitRF.Design.Smith`,
`CircuitRF.Render.DataDisplay`, `RfCore` and SkiaSharp and nothing else — no Avalonia, no view
model — and had simply been written in `src/Ui` because that is where the window is. Namespace
`CircuitRF.Ui.Smith` → `CircuitRF.Render.Smith`, two global-using lines, and the 201 existing Smith
tests passed unchanged. **Framework-free is not the same as below the firewall, and only the second is
enforced**: `tests/Firewall.Tests` can say that nothing here references Avalonia, but nothing can say
that a file in `src/Ui` which happens to need none is in the wrong project. That is only ever found by
the first caller from underneath.

**`SmithChartChrome` is the one that had to be carved.** `SmithGripperOverlay` drew the arrowheads,
the load-point label boxes, the generator anchor and the gripper rings AND owned the gesture that
mutates the design. Only the drawing is a picture, so the drawing is here and takes an explicit
`SmithChromeState` (hovered node, dragged node, hovered/dragged Q handle) instead of reading a view
model. An export passes `SmithChromeState.None`.

**It is chrome and it belongs in the export**, which is not obvious: `PlacedPlot.Overlay` exists
because of exactly this — without it, an overlay a `PlotControl` draws on every frame is silently
absent from every export, the picture is still produced, it still looks correct, and the frequency
labels the user exported it for are gone. A headless verb drawing only the traces would have
reproduced that defect deliberately.

**They are `public` here where they were `internal` there.** Three assemblies consume them now
(`src/Ui`, `src/Cli`, `tests/Ui.Tests`), two of them through `InternalsVisibleTo`, and a public method
cannot take an internal type — `SmithChartChrome.Draw(…, SmithChartScene, SmithDesign, …)` is what
forced the decision. It is also the honest one: this is a cross-assembly API now.

**Nothing here caches a canvas, a transform or a theme**, which is the same rule `ContourRenderer`'s
own header states after it once drew every contour on every Smith plot to the first target it had been
handed.

---

## The Smith Chart's drag glitch was an autoscale, not a race (2026-09-19)

Reported by the owner: the trace in the Smith chart glitches now and then during a drag — mostly
smooth, occasionally not — and dragging the constant-Q line does the same.

**Two independent causes, both in `Plot`, and both about the same premise having stopped being true.**

**1. `OnTracesChanged` autoscales, and a Smith refill is a Clear plus N Adds.** Every pointer move of
a gripper or constant-Q drag calls `SmithPlotBuilder.Fill`, which clears the trace collection and
re-adds every trace — so `Plot`'s own `CollectionChanged` handler ran `Autoscale()` **N + 1 times per
frame, the first of them on an empty plot**. `Fill`'s `autoscale: false` argument guarded only the
explicit window assignment at the bottom of the method, so during a drag the window the user had
panned to was being replaced by whatever the collection events had just fitted. It reads as
"occasional" because a fit only *moves* the window when the geometry's extent crosses what the
current one already holds — which is exactly what a drag makes happen now and then.

Fixed with `Plot.BeginTraceBatch()`, a scope that suppresses the per-item handler and, on close, does
what the handler does **minus** the autoscale (the secondary axis and the viewport are structural and
cost nothing). The caller decides the window; a refill that wants a fit asks for one. `Fill` is the
only holder.

**2. `RenderSnapshot` shared the trace COLLECTION with the live plot.** Its own remark said the
traces could be shared because "their geometry is rebuilt only on a structural change, never during a
pan". The Smith Chart broke that premise: it *is* a structural change, on every pointer move. A frame
the compositor happened to execute mid-refill therefore walked a list with some of its traces
missing. The snapshot now takes a shallow copy of the collection — a handful of references per frame,
no handler attached, so it still cannot trigger an autoscale. The `Trace` objects stay shared, which
is safe because a rebuild replaces them rather than mutating them.

**The general lesson is the second one.** `RenderSnapshot` is the right idea; what made it correct was
an assumption about how often traces change, written in a comment rather than enforced anywhere. Any
new caller that rebuilds traces under a pointer needs both halves of this.

## The admittance grid is the impedance grid reflected (2026-09-19)

`Plot.ShowSmithAdmittanceGrid`, drawn by `AxesRenderer.DrawSmithGrid`'s own accumulation with one sign
flipped. Constant-g circles and constant-b arcs are what constant-r and constant-x become under
y = 1/z, which in the Γ plane is **Γ → −Γ** — so the same two value tables, the same masking tables
and the same one-path-one-draw accumulation produce both families. Deriving g/b circles independently
would be a second set of formulas for one picture, and the two would disagree the first time either
was touched.

**The real axis and the unit circle are their own reflections** and are drawn once; the admittance
pass skips r = 0 for that reason. The admittance family goes down **first**, under the impedance one,
because it is the reference grid the work is read against. It carries **no numbers**: the impedance
labels already crowd in two dimensions (that is what the placement/thinning pass above them is for),
and a second set mirrored into the same disc would collide with them by construction.

It is a `Plot` property rather than an `Axes` one, for `ShowPolarAngleLabels`' reason: a plot keeps one
`Axes` per plot type and **swaps the whole object** when the type changes, so a per-type setting
silently reverts on a trip through another type and back.

## A free marker's glyph, and the load label that flickered (2026-09-19)

Two owner items over the Smith Chart tool, both in `src/Render` because both are what the picture
SAYS rather than what the document holds.

**A freely-placed marker draws the loadpull contour marker's ring, and a snapped one draws the
triangle.** `Marker.SnappedToCurve` is the new flag and it is set by the DRAG, in
`PlotControl.MoveMarkerToCanvasPoint`, on every pointer move rather than only when it changes: "am I
on a curve" is a fact about where this drag just put the marker, not a mode anybody turned on. An
un-shifted move clears it, and so does `MarkerInfoBoxViewModel.ChangeToTrace`, which now also carries
`FreePosition` and `PositionStatic` across — re-pointing a free marker at another curve used to
rebuild it as an ordinary trace-bound marker and resolve it by frequency, which MOVED it somewhere
arbitrary. `MarkerRenderer.DrawSymbol` reads the two flags through one predicate shared with
`SymbolHitRadius`, so the target and the glyph cannot disagree about which one is drawn.

**The load-point labels flicked between two positions during a gripper drag**, which is the second
complaint about the same placement pass (the first was a label landing on a glyph). The cause is
structural: the pass tried the box one whole box-height further out at a time and stopped at the
first row that intersected nothing — a DISCRETE decision recomputed from scratch on every frame. A
load point moving half a pixel could flip the answer between two rows, so the label jumped about
fourteen pixels each way, twenty times a second, while the drag wandered across the threshold.

`SmithChartChrome.LabelOffset` replaces it with the **maximum of continuous demands**: each obstacle
asks for exactly enough clearance to be cleared, and each ask FADES IN over `LabelApproachBand`
pixels as the obstacle comes into horizontal range. A maximum of continuous functions is continuous,
so the box slides out as an obstacle approaches and slides back as it leaves. The bound is still
there — a chart zoomed until the points are a pixel apart has no placement that clears, and a label
marching off the canvas is worse than a slight overlap.

**And the constant-Q grab ring is gone.** It was drawn at the point of the arc NEAREST THE POINTER,
which during a drag is within a pixel or two of the pointer itself — so what it looked like was a
circle stuck to the mouse cursor. Its earlier HOVER form had already been withdrawn for the same
complaint one revision before, which is the tell: the handle has nowhere to sit that is not under
the cursor. The arcs are still grabbed and dragged exactly as before; what says the drag is working
is that both arcs and the `Q=` readout move.

## brief-footprint-4b — the L3a label gap is closed WITHOUT touching the instance compile

`LayoutRenderer.Instances` skips a `LabelShape` inside a placed instance outright, and this file's
own note has recorded that as an L3a gap since it was written. The reference-designator work is what
finally needed it — and the answer is not to teach the compile about text.

The compile builds a sub-cell ONCE into a reusable per-layer aggregate `SKPath` in cell-local space
(R-L3a-3), shared by every placement. Per-placement text cannot live in a shared path, so the skip is
not an oversight; it is the shape of the cache. That constraint forces the right design anyway: the
designator is the PARENT's data — it belongs to the placement, not to the cell — so **the parent
draws it**, in a deferred pass copied from the port glyphs, which exist for precisely this. Both
label skips (`Instances.cs`, `LayoutRenderDetail`) stay exactly as they are.

**The tile cache needed nothing, and the reason is worth knowing rather than assuming.** A port is
collected inside `DrawCommitted` and therefore has to survive being rasterized into a tile, which is
what `DeferredPort.Index` and its de-duplication exist for. A designator is collected OUTSIDE that
closure, from the frame's instance candidates, because `TryDrawTiled` already declines to tile a
document with placed cells — so there is no tile for a designator to be baked into. Collecting it
inside the closure would have needed the same index-dedup machinery for no benefit.

**Below the geometry it names would be worse than not drawing it.** The pass runs after every layer,
every instance, the mesh overlay and the rulers, and BELOW `DrawPortGlyphs` — a port renders higher
than anything by owner instruction (2026-09-09), and a designator is content rather than a marker.

**The legibility floor drops, where a committed label's floor GROWS, and that difference is
deliberate.** `EffectiveVisibleLabelHeightDbu` floors an authored label up to a readable size because
that label is model data a user typed and would otherwise be invisible. A designator is derived, it
is drawn at a fixed 0.8 mm, and there are as many of them as there are parts — floored up at
board-fit zoom they would bury the artwork they name. Under the floor it is dropped.

**`DocumentExtents` had to be told.** The brief assumed `render --fit` would grow for free because
`DocumentExtents` already measures labels. It measures labels in `view.Shapes`, and a designator is
in none — it is derived per frame. Without an explicit ask, a fit crops the designators off the top
row of a board. It asks `FootprintLabel` rather than estimating, so the page is framed on the same
measurement the renderer draws with.

## Current-density heat map painted outside the drawn metal (2026-09-22)

After an EM run the heat map coloured cells beyond the artwork. Those cells are the uniform
calibration lead `PlanarFeedExtension` grows at a port whose drawn feed is shorter than the
calibration's run: the kernel meshes and solves the EXTENDED problem, and the map is indexed by that
mesh, so the lead's cells were coloured like any other. The currents on the lead are real and stay
in the map (and in its normalisation); only the display leaves them out now.
`PlanarCurrentDensity.MarkDrawnMetal` records a per-cell `OnDrawnMetal` mask (metal centroid inside a
drawn polygon on the cell's level; null when nothing grew), and
`LayoutRenderer.ShowCurrentDensityOutsideDrawnMetal` (default false) is the switch that decides
whether the heat-map pass skips the unmasked cells. The plain mesh overlay still draws the lead's
cell boundaries.

## A 3D setup's section and isometric outline — brief-em3d-5 (2026-09-25)

`Em3dSectionScene` (the cut, or the projected outline) and `Em3dSectionRenderer` (the drawing) draw
an `Em3dProblem` for `render --section` / `--iso`. Findings:

- **Framed on the content, not the air box.** The generator pads the box by an eighth of the longest
  wavelength, which for F0 case B's 100 MHz start is ~375 mm around a via a few millimetres across. A
  picture framed on the box is a picture of a dot. The frame is conductors, sheets, ports and anything
  not as wide as the box, padded by 15 % of its largest side and clipped to the box; a face outside
  the frame is labelled at the frame's edge with how far beyond it lies. A slab (a box as wide as the
  air box) contributes its height only, and the air above the stack contributes nothing.
- **The half-open rule needs the crossing test to be "strictly above".** A solid is present where
  bottom ≤ plane < top. For a polygon cut by a line that means an edge crosses when exactly one end is
  STRICTLY above the line: with "at or above", a rectangle cut exactly at its bottom edge has every
  vertex "above" and produces no interval, so the rule silently inverts at the one height a caller asks
  about. The mesh cut classifies a vertex the same way. The plane is snapped first to any boundary
  within 1e-9 of the box's largest side, so `z=543um` lands on the copper's bottom bitwise whatever the
  DBU-to-metre conversion's last bit did.
- **Mesh-cut loops close exactly because each crossing is keyed by its EDGE**, computed from the
  edge's lower-indexed vertex, so both triangles sharing an edge produce one point. Keying by
  coordinates would need a tolerance, and would fuse the distinct crossings that meet at a vertex
  lying exactly on the plane.
- **Paint order is construction order.** The problem resolves overlap by order (the later solid owns
  the volume), so painting in order shows exactly the solid that owns each point, and a plated via's
  air bore paints over its barrel. Air paints the page background; with `--background transparent` it
  is not painted, so the bore then shows the barrel behind it (said on `Em3dRenderStyle`).
- **A grey base fill does not turn in hue.** Dielectrics are the stackup theme's dielectric fill
  rotated 47° per material; at that fill's own saturation two substrates came out the same grey, so the
  saturation has a floor. The index is the material's position in the PROBLEM, so a material keeps its
  colour from one view to the next.
- **A darker shade of a light fill vanishes on the dark variant.** Isometric dielectric edges are the
  stackup theme's band edge, not a shade of their fill.
- **Byte identity, SVG and PDF.** PDF is byte-identical between the CLI process and an in-process
  render. SVG differs only in Skia's `clipPath` ids (the counter recorded above), and the gate applies
  `RenderCliVerbTests`' one named exclusion and nothing else.
- **The isometric outline has no hidden-line removal and says so in its caption.** Sharp edges
  (> 30° between faces) and silhouette edges are drawn from the one tessellation
  (`Em3dTessellation`, `src/Engine/Em3d`); an extruded polygon's are drawn from its rings, because
  nothing in this repo triangulates a polygon with holes, and `Em3dTessellation.Of` says so for that
  primitive rather than returning half a solid.


## Fields in the 3D view: the ParaView reader — brief-em3d-29 (2026-09-26)

`src/Render/Scene3D/Fields/`. The brief's expectations, each checked against Palace 0.18.1's own output:

- **`.pvd` → per-step `.pvtu` → one `.vtu` per MPI rank: confirmed.** But **no node is shared, even
  inside one piece**: every cell carries its own nodes (1,180 points for 118 order-2 tets), because a
  Nédélec field is not continuous across a face. There is no global ID. So the merge is concatenation
  with an offset, and the only de-duplication is TOPOLOGICAL — matching a face's corners by exact float
  coordinates (every cell writes a shared mesh vertex identically), used only to find a solid's boundary
  faces. Values are never merged: each side of a material face keeps its own.
- **High-order Lagrange cells: confirmed**, VTK types 71 (tet) and 69 (triangle), 10/6 nodes at order 2
  and 4/3 at order 1 (Draft). Node order verified against the points: corners, then edges
  (0,1),(1,2),(0,2),(0,3),(1,3),(2,3) — (0,1),(1,2),(2,0) for a triangle. Order 3+ is refused by name
  rather than drawn through its corners. The geometry is CURVED (Gmsh order 2), so edge nodes are not
  midpoints.
- **Appended binary, possibly zlib: half right.** The GEOMETRY is inline base64 (`format="binary"`, a
  UInt32 byte count base64-encoded on its own, then the data's own base64 — two streams, not one); the
  FIELD arrays are appended RAW. Nothing is compressed. A compressed file is refused by name.
- Complex fields are `X_real`/`X_imag` pairs (E, B, J_s, Q_s, and `E0_<k>` port modes on a wave port).

**Drawing vs. reading a value.** The display splits each order-2 cell into linear sub-cells through its
own nodes (8 tets / 4 triangles) and slices those exactly. The value under the cursor is the element's
own P2 interpolant, with the curved geometry inverted by Newton. Against Palace's own `probe-E.csv`
(3 probes × 3 modes of the committed cavity): **sampled 2–5e-8 relative** (Float32 storage), **drawn up
to 5.70 %** on that deliberately coarse fixture (118 tets, ~λ/3). The drawn tolerance in the gate is that
measurement; the linear interpolant's error falls with h².

**Memory.** Every array read through a `FileStream` allocated its 64 KB buffer: 1.0 MB for a 48 KB step,
21× what it kept. `RandomAccess` on a `SafeFileHandle` reads straight into the destination: 1.85× on the
fixture (the pooled header dominates at that size) and **1.022× on F0 case A** (146,769 cells, 1.47 M
nodes: 60.6 MB allocated for 59.3 MB kept).

**The brief's TE101 claim needed a qualifier.** "|E| is zero on the PEC walls" holds only on the four
walls E is tangential to (x and z); on the y-walls TE101's E_y is NORMAL and is not zero. On the tangential
walls the tangential E is 2e-16 of the peak (Palace zeroes those DOFs), and the normal part the coarse
mesh carries is 5.2 %, inside the display tolerance above.

**J_s.** Palace writes `J_s` on every boundary face whenever it writes B; an electrostatic run writes
neither, so |J_s| is not offered there (gate 7). The n × H fallback the brief allowed was not built: the
pinned Palace never reaches it.

**Fixtures** (`testdata/em3d/fields/`, 784 KB): the PEC cavity of `PalaceEigenTests.Cavity` at
MaxElementWavelengths 0.3, no refinement, on TWO ranks, with three `Domains.Postprocessing.Probe` points;
and the parallel plate of `PalaceStaticTests` (electrostatic) at EdgeRefinement 1. Both are Palace's
output as written, lowered by circuitRF's own writers.

### openEMS's field dump (brief-em3d-29 §6)

Read off a real run of the pinned openEMS and its own source, not assumed: a DumpBox with `DumpType 10`
(frequency-domain E) and `FileType 0` writes VTK, not HDF5 — so no native reader and no stop (the brief's
condition). Per frequency: `<name>_f=<Hz>_abs.vtr` and `_arg.vtr`, each component's MAGNITUDE and PHASE,
which is the complex field exactly (`VtrReader`, Re = |E|cos∠E), plus 21 real snapshots at fixed phases,
which carry nothing more and are not read. XML RectilinearGrid, INLINE binary, zlib-compressed
(`vtkZLibDataCompressor`, UInt32 header `[blocks, block size, last size, compressed sizes…]` base64 on its
own, then the blocks' base64; each block its own zlib stream → `ZLibStream`).

**Cell centres, not nodes (`DumpMode 2`).** The first run used node interpolation, and the stripline's
grid has a z-line ON the ground and ON the strip: a node there averages E across the metal, so the ground's
nodes read about half the gap's field and the strip's read its upper and lower fields cancelling. At the
cell centres the gap's cell holds the gap's field. Each grid cell is six tetrahedra sharing the grid's
nodes, so the same slicer, sampler and GPU pass draw it — no special case.

**Gate 9's reference needed both ports.** The stripline (Z0 ≈ 51 Ω on 50 Ω ports, plus the lumped ports'
parasitics) carries a small standing wave: |V1|/h and |V2|/h differ by 13 %, and the field at the centre
(1.842e-9) sat between them. The voltage at the centre is taken from both ends by the TEM line's own
equation, V(ℓ/2) = (V1 + V2)/(2 cos βℓ/2), β = ω√εr/c: **0.96 %** from the dumped |E|. The port voltages
are the port probes transformed with openEMS's own normalisation (2·Δt·Σ u e^{−jωt}), the one its dump uses.

## Face IDs, selection passes and partial uploads — brief-em3d-43 (2026-09-26)

- **A vertex carries its FACE (stride 20 → 24, the face last so no older offset moved), and a vertex the
  tessellation SHARES between faces is emitted once per face.** Flat interpolation takes the face from one
  vertex; a box corner shared by three faces would give a triangle its neighbour's face. Shading was
  already flat (derivative normals), so nothing looks different; the scene simply holds more vertices.
  A sheet is face 0 ("surface"); a sweep's or sphere's unnamed faces are one face, `FaceUnknown`, with no
  feature edges.
- **The ID pass writes (object, face) to RG32Uint**; `Scene3DPicking.PairAtPixel` is its software twin and
  `IdAtPixel` is now its object half, so brief 28's gate 9 compares exactly what it did.
- **The selection is a uniform list of 64 (object, face) pairs** (`SelectionLimit`). Beyond it the
  selection is still whole — tree, Properties, commands — but only the first 64 highlight, and the status
  line says so. A per-object selection-bit buffer would lift that, at a storage buffer on three backends.
- **Selected edges and the on-top face are two pipelines with the depth test OFF**, drawn last and only
  for selected objects; `fs_edge`/`fs_top` discard everything unselected. An edge vertex carries its two
  faces packed in 16 bits each — so a solid with more than 65,534 faces loses its outline, not its pick.
- **B's list is a CPU ray over a BVH of object bounds**, built once per scene (a scene is immutable, so it
  is keyed by the scene object) and counted. Face mode keeps a face crossed twice (enter, leave); a ray
  down the shared diagonal of a face's two triangles is one crossing (same face, same depth, deduped).
- **An edit uploads only what changed** (`Scene3DPatch`): when two scenes' layouts match, each object's
  bytes are compared and only differing ranges go up. A RENAME changes no byte and uploads nothing; a
  move uploads that object's vertices and edges. Three things make layouts match: the editor FIXES the
  scene origin while the content stays within its own size of it, the tessellation cache hands back the
  same mesh for an unchanged primitive (records compare by value, so even a re-lowered but equal box is a
  hit), and object IDs are construction order. Metal patches with a blit on the queue (ordered after
  every frame already committed); D3D11 with UpdateSubresource (scene buffers are DEFAULT now, not
  immutable); Vulkan waits idle and copies from staging, as a whole upload already did.

## Snapping: the pick patch, feature tables, the query — brief-em3d-44 (2026-09-26)

Built: `Scene3D/Edit/Scene3DFeatureTable.cs`, `Scene3DIdPatch.cs`, `SnapQuery3D.cs`, `GeometrySnap.cs` (the
one snap radius), the pick size on `Camera3D.ViewProjectionMatrix` and `Scene3DFramePlan`, and
`RayHits.BatchesNearRay`. Gates: `tests/Ui.Tests/ThreeD/Snap3DGateTests.cs`. **No pixel was seen**; the
Metal patch was driven offscreen and agrees with the CPU patch texel for texel except on an edge.

- **The patch is the 1 × 1 ID pass with a wider window.** The pick matrix maps the N × N pixels round the
  cursor (N odd) onto the clip square, so its CENTRE texel is still the cursor's pixel: a backend with a
  1 × 1 target given the same matrix reads exactly the old answer. That is why D3D11 and Vulkan were left
  at 1 × 1 (they compile and have never run, and a blind read-back change there is unreviewable): their
  `MaxPickSize` is 1 and the pane renders the patch on the CPU instead, from the objects near the ray.
- **Depth is VIEW depth, taken from the pick pass's own point target** (`Camera3D.ViewDepth`), not the
  depth buffer: the RGBA32Float target already carried the fragment's point, and view depth is one measure
  in perspective and orthographic, where NDC z is not.
- **The brief's visibility rule, read literally, is vacuous.** "Visible if it lies on a face in the patch"
  — but every candidate COMES from a face in the patch. It is applied per PIXEL instead: a feature is
  shown when, at its pixel or one of the eight round it, the patch holds nothing, one of the feature's OWN
  faces (a corner's faces, an edge's two, a centre's one), or something no nearer than it within a pixel's
  worth of depth. That keeps the brief's intent — an edge between a front face and a back face is visible
  through the front one — and hides a corner of a big face that a small object in front covers.
- **X-ray, defined**: on when the clip plane is on or the patch holds a translucent object. Then the
  TRANSLUCENT objects the cursor's ray passes near (the B hierarchy, radius-padded) give every feature,
  with no visibility test; opaque objects never X-ray. A dielectric's far corners are seen through it.
- **A tie needs a tolerance.** Two corners one behind the other land on one screen point, but their float
  distances differ in the last bits, so a strict "equal distance → smaller depth" never fired and the FAR
  corner won half the time. `SnapQuery3D.TiePixels` (0.05 px) makes them a tie.
- **Feature tables are doubles in world metres**, built from the tessellation's own `Point3`s (the
  elaboration's metres unchanged), never from the scene's scene-local floats — a snapped corner must come
  back as its DBU exactly. A sweep or sphere (no named faces) has none; a curved face (a cylinder's side)
  has no centre, which would lie inside the solid.
- **Instances share a table, not a tessellation.** The scene is still flattened (the per-batch transform
  is brief 46/48's), so 900 array elements are 900 meshes; the builder keys their table by (file, object,
  rotation) and gives each element its translation, and a hover offsets only the elements in the patch.
  Tables are also kept per MESH (a ConditionalWeakTable), so an edit re-tables only the edited object.
- **Zero allocations per hover** on the CPU path held once `RayHits`' BVH query took a caller's stack; it
  allocated one per call before (B still passes a fresh one — B is a key press, not a hover).

## The drawing plane's grid — brief-em3d-45 (2026-09-26)

Built: `Scene3D/Edit/DrawingPlane.cs`, `PlaneGrid.cs`, `DrawGeometry.cs`; `Scene3DPipeline.Grid` and
`Scene3DBuffer.None` in `Scene3DFramePlan`; `vs_grid`/`fs_grid` in `scene.wgsl` (regenerated by
`tools/ShaderGen`); the grid pipeline on Metal, D3D11 and Vulkan.

- **The grid pipeline has NO vertex input on any backend** — Metal a pipeline with a nil vertex
  descriptor and `drawPrimitives` with no buffer bound, D3D11 a null input layout and `SV_VertexID`,
  Vulkan an empty vertex-input state. The quad's six corners come from the vertex index (bits of 0x16 and
  0x34), its size and place from the uniform block. So a draw with `Buffer == None` must be dispatched
  before any "no buffer, skip" test in a backend's loop — every backend's loop had one.
- **The uniform block grew to 1,072 bytes** (a 36-float grid block after the field block). Vulkan's ring
  slot (`UniformStride`) had to grow with it, 1,024 → 1,280: it is a hand-picked constant, not derived.
  D3D11 and Metal size from `UniformBytes`.
- **The phase trick keeps the shader's floats small.** A fragment's plane coordinate plus a phase — the
  scene origin modulo one MAJOR cell, computed in doubles — is the world coordinate less a whole number of
  major cells, so minor and major lines land on exact multiples without the shader ever holding a large
  world coordinate.
- **The spacing band is the 1-2-5 sequence's own**: one minor cell ≥ 8 px at the focus means ≤ 20 px, well
  inside the brief's 8–40. The focus is where the line of sight through the camera target meets the plane,
  falling back to the target dropped onto it when that is edge-on or absurdly far.
- **Metal ran it; D3D11 and Vulkan compile but have not run** (as for briefs 43/44). The Metal pipeline
  builds in every Metal test; a one-off offscreen render with the grid on changed ~17 % of a 320 × 200
  frame, and edge-on it is a faint line.

## The per-batch transform and the move gizmo — brief-em3d-46 (2026-09-26)

Built: `Scene3DDraw.Transform`, `Scene3DPreview` and the plan's transform slots (`Scene3DFramePlan`), the WGSL
`mx` block and its three backends, and `Edit/GizmoGeometry.cs`. Gates 1 and 8 in
`tests/Ui.Tests/ThreeD/OperationsGateTests.cs`. **No window was seen from the agent's session.**

- **This is brief 43 §5's per-batch transform, which brief 43 left unbuilt.** A draw carries a slot index
  (0 = identity); the plan writes the slots (16 floats each, the camera's column-major layout) and counts
  `TransformBytes` = 64 × non-identity draws — the gate's "≤ 64 × selected batches per move". A backend
  sets the slot only when it differs from the previous draw's.
- **It is a second UNIFORM binding, not a WGSL immediate (push constant).** The first attempt used
  `var<immediate>`; naga 30 writes that to HLSL as `ConstantBuffer<T>`, which is Shader Model 5.1 syntax and
  D3D11's `vs_5_0` does not compile — the Windows backend would have failed at start-up. So: `@group(0)
  @binding(1)` → Metal `[[buffer(2)]]` (setVertexBytes per change), D3D11 `cbuffer : register(b1)`
  (UpdateSubresource per change), Vulkan descriptor set 0 binding 1 as a DYNAMIC uniform whose offset is
  re-bound per change, into a per-frame region of 257 slots × 256 bytes (256 is the spec's cap on
  minUniformBufferOffsetAlignment). `tools/ShaderGen` states the binding for all three.
- **A preview's moving objects are left out of the ID pass** (and out of the CPU snap patch's visibility), so
  the snap finds what lies under the cursor, not the object being dragged.
- **Duplicate and Array preview by drawing the originals again** under each copy's transform
  (`KeepOriginal`), so the document is untouched until Accept. The preview draws at most
  `MaxPreviewCopies` = 256 copies; an accepted array writes them all.
- **Metal ran it; D3D11 and Vulkan compile but have not run.** The Metal pipeline compiled the new shader in
  the existing Metal test, and a one-off offscreen render (not committed) with every object under a
  translation moved the drawn picture's centroid 41 px as predicted, 512 transform bytes.
- **The gizmo is laid out in screen pixels from the projected axis directions**, a constant size; an axis
  whose projected step is under 20 % (pointing at the viewer) offers no arrow and no squares that use it.
  Plane squares win a hit over arrows; they sit 16–30 px out along both arms, so the two never overlap.

## Cylinder cap centres in Vertex mode — brief-em3d-47 (2026-09-26)

`Scene3DObject.CapCentres` (world metres; set by `Scene3DBuilder` for an `Em3dCylinder`, copied by the builder's
object copy) and `Scene3DFaces.NearestVertexOnScreen` offer ONLY those two points on a cylinder, whatever face the
cursor is on: a cylinder's tessellation vertices are not design (R-em3d47-5). This applies in the read-only viewer
too, so a via's rim points are no longer vertex candidates there either. Snapping is unchanged (a cap's centre was
already its face centre).

## Instanced drawing: elements, their pick, the triangle budget — brief-em3d-48 (2026-09-26)

Built: `Scene3DElement`/`Scene3DInstanceGroup` and the owned-prefix counts on `Scene3DModel`, the builder's
`RunTracker` and `Scene3DInstancing` option, element slots and `ChooseDetail` in `Scene3DFramePlan`, the `mx.id`
offset in `scene.wgsl`, per-draw transforms in all three backends' ID pass, and `Scene3DPatch.ElementTransforms`.
Gates 1, 2 (CPU, software AND a real Metal ID pass) and 9 in `tests/Ui.Tests/ThreeD/HierarchyGateTests.cs`.
**No window was seen from the agent's session.**

- **The pick encoding is NOT the brief's reference packing** (draw index in the top bits). Element objects are
  MATERIALISED — a `Scene3DObject` per element's object, with a dense 32-bit id — because the whole viewer and
  editor (visibility arrays, selection, hover, the tree, snapping) index objects by `id − 1`; a virtual id
  would have touched every one of them. An element owns no bytes: its objects' ids follow every object that
  does (`GeometryObjectCount`), each element's in one contiguous block mirroring the prototype's, so the ID
  pass writes `v.id + mx.id.x` — the ELEMENT's object — with one per-draw offset. The documented limits
  (`C3dEditorViewModel.DefaultMaxChildObjects` = 2²⁰ per child, `DefaultMaxPlacementObjects` = 2²⁴ per
  placement) are the view's per-object records, refused at placement with both numbers.
- **A per-draw slot is now 80 bytes** (the mat4 and a vec4u); `TransformFloats` = 20. Slot 0 identity, then one
  per element and one per element's BOX (written once per scene in `Size()`), then a preview's copies, then
  (only while a preview moves an element) that element under each copy. Vulkan's ring GROWS
  (`EnsureTransformRing`, device idle, binding 1 rewritten) — its fixed 257 slots could not hold a 20 × 20 array.
- **One draw per element**, when the prototype's opaque triangles are one contiguous index range and every
  OPAQUE object of the element is drawn; a translucent one is sorted and drawn on its own either way (the
  "whole element" test once required translucent objects too, and the hidden outermost dielectric — which
  every element inherits from the prototype — made every element fall back to per-object draws).
- **Every CPU reader of the vertex buffer adds `Scene3DBatch.Offset`**: `Scene3DPicking`, `RayHits`,
  `Scene3DIdPatch`, `Scene3DFaces.TrianglesAt`. A new reader that forgets it picks the PROTOTYPE's triangles
  under an element's id — right object, wrong place — so grep for `scene.Vertices[` when adding one.
- **`Scene3DPatch.ElementTransforms` counts new TRANSLATIONS**, not changed indices: a column added to a 20 × 20
  array renumbers every element after it (their ids move, their translations do not), and counting by index
  said 400.
- **`Accumulator.Object` copies a `Scene3DObject` field by field.** `Context` (the dimmed parent) was silently
  dropped there until the push-in test looked; a new object field must be added to that copy.
- **The triangle budget** (default 20 M, `Lod3DPreference`, 3D ▸ Triangle Budget…) boxes the elements farthest
  from the eye first; boxed elements are neither drawn nor picked, and `Viewer3DViewModel.LodText` says how
  many. It lives in the 3D menu, not Settings, so no Settings figure moves.
- **Metal ran it** (the new gate's offscreen ID pass names element [>5]'s object); D3D11 and Vulkan compile only.

## brief-em3d-49 — the editor's air box and face-boundary tints (2026-09-26)

- **An air-box face is picked LAST, and not by the GPU.** It encloses everything, so in the ID pass the near face
  stands in front of every solid seen from outside. `Scene3DObject.PickLast` keeps it out of the GPU pass (it stays
  non-`Pickable`); `Scene3DPicking.PickLastHits` casts the cursor ray at the six faces on the CPU (twelve triangles).
  The pane takes it only when the ID pass found nothing pickable, and B appends it after every solid hit.
- `Scene3DBuildOptions.EditorBoundaries`: absorbing untinted (alpha 0), PEC grey, PMC orange, symmetry hatched (line
  primitives on the face); the read-only viewer keeps brief 28's tints. `FaceTints`: each face boundary drawn as a
  translucent sheet lifted 1e-4 of the box's size along the face's outward normal — coplanar would z-fight the solid.
  Tints are named `boundary:<object>/<face>` and the air-box toggle leaves them alone.


## 3D editor bugs round 1 (owner, 2026-09-26) — the drawing grid was a patch, not a plane

**Two causes, and fixing the obvious one alone would not have been enough.** Brief 45's grid was a quad on the plane
centred on the focus with a half-size of 0.6 × the view's diagonal at the focus, alpha faded to zero from 0.45 to 1.0
of that radius — a patch by construction. But the quad was also drawn through the camera's projection, whose near
and far planes BRACKET THE SCENE'S SPHERE (`Camera3D.DepthRange`); so even a bigger quad would have been cut off just
past the scene, and a small scene gets a small grid. A larger quad also costs precision: the fragment's world point is
interpolated from vertices ~1e4 × the camera distance away, which puts ~7 % of a minor cell of jitter on the lines
near the focus.

**What it is now (`PlaneGrid.Fill`/`WriteRay`, `vs_grid`/`fs_grid`):** the six vertices cover the VIEWPORT (clip
±1, z 0.5 w — never depth-clipped), and each fragment casts its own ray at the plane: origin `gq + x·gox + y·goy`,
direction `gd + x·gdx + y·gdy` (perspective: from the eye; orthographic: along the view from the view plane), built
from the camera's own frustum (`ProjectionMatrix`'s fov, aspect and ortho half-height, with `flipY` applied to the y
terms exactly as `WriteViewProjection` applies it). The hit is exact per pixel at any distance. The fragment writes
its own depth, `clamp(vp·hit)`, so the plane in front of the near plane or beyond the far plane still draws and still
hides behind a solid. Uniform block 1,072 → 1,152 bytes (five vec4s; Vulkan's 1,280 stride still covers it).

**Decimation is per fragment, not per frame.** Level k is every `majorEvery^k` minor lines; a level's weight depends
only on its cell's size in pixels THERE (0 below 4 px, the minor alpha by 10 px, full by `majorEvery` × that), and a
fragment evaluates the three levels from the finest that reaches 4 px — anything coarser is a subset already at full
weight. Because the weight is a continuous function of cell size, the change of level across the plane is seamless,
and far away it fades where even the coarsest drawn level crowds. The CPU-chosen minor spacing at the focus is
unchanged (status line, gate 7). Fine phase (mod one major cell) for levels 0–1; a coarse phase (mod `majorEvery⁶`
minor cells, in the unused `gax.zw`) for the rest, so coarse lines still pass through the world origin's multiples.

**The obliqueness fade had to shrink** from `smoothstep(0.01, 0.12, cos)` to `smoothstep(0, 0.035, cos)`. At the old
value a 26°-pitch perspective view (default fov 0.7 rad) lost the top quarter of the viewport even with the plane
reaching it — the density weights already thin the lines where they crowd, so the fade only has to cover the last two
degrees before the horizon. An exactly edge-on plane (orthographic Front) draws nothing, as the degenerate quad did.

**Trap for a pixel test:** `DrawingGridSettings.Dark` comes from the app's theme variant; headless it is false (light
lines) while `Viewer3DViewState.Background` defaults DARK — dark-grey lines on a dark-grey ground read as "no grid" in
a Metal read-back. The Metal gate sets `Dark = true`.

Gate: `tests/Ui.Tests/ThreeD/GridAndWireframeTests.cs` — every sampled clip position's ray lands on the plane where
the written view-projection maps it back (perspective both y conventions, orthographic), perspective hits beyond the
far plane exist; on Metal the top and bottom tenths of a perspective view both hold grid pixels.

## 3D editor bugs round 1 (owner, 2026-09-26) — an object with no material was invisible

It was not a rendering fault: `C3dElaborator.Run.Document` REFUSES an object with no material (or one its technology
does not define) and `continue`s before lowering it, so it never reached `Em3dProblem`, the scene, the ID pass or the
snap — the tree listed it and nothing else could touch it. The refusal is right for a solver and stays.

**Now:** the elaborator still refuses it, but lowers it into `C3dElaboration.UnassignedSolids`/`UnassignedSheets`
(material "", order 0, no net, no material key — the solver lists are untouched) and records its PROVENANCE, which
is what the editor's face names, exactness and instance lookups read. `DisplayExtent()` adds them for the editor's
framing; `Extent()` (what an air box pads) does not. The editor appends them to the scene's problem LAST and gives them
no instancing (so no run of array elements is broken), and passes `Scene3DBuildOptions.Wireframe`.

**How the pick pass sees a wireframe (the choice):** its triangles stay in the scene as a TRANSLUCENT batch whose
vertex colour has alpha exactly 0, so the GPU ID pass, the CPU pick, `RayHits`, B-cycling and the snap all reach its
faces exactly as any object's. The consequence, accepted: an invisible face in front occludes a pick behind it (B
cycles past it, as it does past a dielectric). `fs_color` treats alpha 0 as "draw only to show hover (Object mode,
cyan 0.18), a hovered face (white 0.25) or a selected face (magenta 0.4)"; otherwise it discards. The edges are an
always-drawn `Lines` batch of the object's own FEATURE edges in the theme's ink colour (`Accumulator.Object`'s new
`wireEdges`), plus four side generators for a cylinder, whose only feature edges are its two rims. Kind is `Body`, not
`Dielectric` — otherwise the Dielectrics visibility toggle would hide it and `OutermostDielectric` could pick it
and start it hidden.

**A second place that assumed "selectable ⇒ in `e.Solids`":** `BoundsDbu` (the Move/Rotate/gizmo pivot) looked only at
the solver lists and returned null for a selected wireframe; it now includes the unassigned lists.

Gate: same file — a box, a cylinder, a sheet with no material and a box of an undefined material are wireframes in the
scene (alpha-0 translucent triangles, 12/4 edge lines), still in `Refusals` and absent from `Solids`; a click selects
one in Object and Face mode and `BoundsDbu` answers for it; on Metal edges are drawn, faces are not filled (drawn
pixels < ¼ of the silhouette) and the ID pass names it at a pixel inside a face.

## 3D editor bugs round 2 — a polyhedron had no edges, so a material-less one vanished (2026-09-26)

`Scene3DBuilder`'s feature edges (the dark outline of every solid, and the WHOLE drawing of a material-less wireframe
object) paired each triangle edge with its neighbour by mesh vertex INDEX. `Em3dTessellation.Polyhedron` repeats each
corner once per face — it adds a vertex per face corner — so by index no two faces ever shared an edge, and every
polyhedron had no edges at all. A box moved at a vertex becomes a polyhedron: filled, it merely lost its outline;
material-less, drawn as edges only, it disappeared and only its hover highlight showed. The edges are now keyed by the
corners welded by position, as `Scene3DFeatureTable` (the snap's features) has always welded them. The engine's
tessellation is untouched. Anything else that repeats corners per face (a prism's walls) gains its outline too.


## 3D editor bugs round 3 (owner, 2026-09-26)

### 3D editor bugs round 3 — the grid vanished below a ~10 µm scale: a perspective zoom went THROUGH the plane

- **Not precision, not the shader.** A float emulation of `fs_grid` and a real-Metal offscreen render both drew the
  grid cleanly down to ~10 pm/px with the target on the plane. It reproduced only in PERSPECTIVE with the orbit
  target BELOW the drawing plane (plane on a substrate's top face, target at the scene's centre): at a 52 µm
  distance (63 nm/px, a ~7 µm scale bar) the eye passed under the plane, which was then behind it and — correctly for
  a perspective ray (`gq.w > 0.5 && t <= 0` discards) — not drawn. `Camera3D.ZoomAt` never changes the target's
  DEPTH (it keeps the cursor's point on the focal plane through the target), so nothing stopped the dolly.
  Orthographic draws the plane wherever the eye is and never showed it.
- **Fix: `PlaneGrid.SeatTargetOnPlane`**, called by `Viewer3DViewModel.Zoom` before a zoom IN when the pane has a
  visible drawing grid: if the cursor's ray meets the plane in front of the eye but nearer than the target's depth,
  the target moves along the line of sight to that depth with the eye fixed — the picture is identical (perspective
  depends only on eye, direction, fov) — and the zoom then approaches the plane by ×0.88 a notch, never reaching it.
  The orbit centre therefore lands on the plane under the cursor. Objects are NOT considered (only the plane).
- Side effect seen while probing: `PlaneGrid.Focus` falls back to the target dropped onto the plane when the plane is
  behind the eye, and `MetresPerPixel` clamps that depth to 1e-6 × Distance, so the status line read "Grid 1 nm" in
  that state. Unreachable now from a zoom; left as is.

### 3D editor bugs round 3 — minor grid spacing: less dense far out, the snap step close in

- `PlaneGrid.MinPixels` 8 → 14 (a 1-2-5 step then spans 14–35 px at the focus, was 8–20); the shader's per-fragment
  decimation moved with it (`grid_weight`: nothing under 6 px, faint minor alpha by 14 px, full by major-every × that;
  `k0` from 6 px). `MaxPixels` is now `MinPixels × 10` (was the brief's 40).
- `DrawingGridSettings.SnapDbu` (set from `C3dEditorViewModel.SnapPitch` in `ApplyDrawingGrid`): once the snap step
  spans MinPixels it IS the minor spacing (every crossing is a landable point) until it spans major-every ×
  MinPixels, then it is divided by major-every so the snap step becomes the MAJOR lines — repeated while it divides
  into whole DBU. A snap step that does not divide falls back to the display unit's own steps below it.
- The three generated shaders were regenerated with `tools/ShaderGen` (naga 30.0.1); a merge that also changes
  `scene.wgsl` must re-run it rather than 3-way-merge `scene.metal/.hlsl/.spv`.

### Selected object faded, two-pixel outline

- **No backend draws a line wider than one pixel** (Metal has no line width at all; D3D11 likewise; Vulkan's
  `wideLines` is optional). So "thicker outline" cannot be a pipeline setting. `Scene3DFramePlan` now draws each
  selected object's edge batch `EdgePasses` (4) times; passes 1-3 use a transform slot that COPIES the draw's own
  slot (matrix + array id offset) and puts the pass number in the slot's spare `id.y`. The vertex shader offsets
  the clip position by `(id.y & 1, id.y >> 1)` pixels × `w` — a 2 × 2 brush, i.e. 1 px → 2 px. The clip units per
  pixel ride in the uniform block's old `pad0`/`pad1` (now `ppx`/`ppy`, floats 29/30) — no block size change, no
  backend change. A slot's offset copies are made once a frame however many draws share it (all plain objects
  share slot 0 → 3 extra slots total); preview copies each get their own three.
- **Faded selection = routing, not just a shader alpha.** The opaque pipeline has no blend and writes depth, so an
  Object-mode-selected object is taken OUT of the opaque draws (and out of an array element's one-draw fast path,
  `AnyFaded`) and sorted into the translucent list; `fs_color` drops its alpha to `min(a, 0.5)`
  (`Scene3DFramePlan.SelectedAlpha`). The ID pass is unchanged, so picking and snapping still see it. Only the first
  `SelectionLimit` (64) are faded — the same entries the shader's `is_selected` can see; faded beyond that would
  draw at alpha 1 with no depth write. Face and Vertex mode do not fade (their selection is a face/vertex, drawn
  on top already). The read-only viewer shares the plan, so its selection fades too.
- Draw counts changed: a Face-mode selection is now `EdgePasses + 1` draws, not 2
  (`C3dEditorGateTests.Metal_…`, which runs on the real Metal device and now also exercises the Object-mode path).

### Metal over dielectric at a coincident face (owner change, em-3d.md §6.3a)

- Every solid is drawn WHOLE (no booleans in the view), dielectrics translucent, so a metal inside a dielectric already
  showed through. The case the precedence rule changes is the COINCIDENT face — a pad flush with a substrate's top, a
  trace ending on the board edge. There the depth test is `LessEqual` on every backend and the translucent pass comes
  after the opaque one, so the dielectric's face passed wherever its interpolated depth came out equal or a float step
  nearer — speckled tint over the metal, and (the ID pass has the same rule) a pick that could name the substrate.
- `Scene3DDraw.Behind`, set by the frame plan on the Translucent and Pick draws of `Scene3DKind.Dielectric`/`Air`
  objects (`Scene3DFramePlan.IsBehind`), asks for a polygon offset: `BehindDepthBias` 4 / `BehindSlopeScale` 2. Metal
  sets it per draw on the encoder (`setDepthBias:slopeScale:clamp:`, only on change), D3D11 swaps to a second rasterizer
  state, Vulkan binds a static twin of the Translucent and Pick pipelines with `depthBiasEnable` (no dynamic state, so
  no new command to load). The CPU picks (`Scene3DPicking.Pick`/`PairAtPixel`, `Scene3DIdPatch.Render`) add
  `BehindNdc` (1e-6, relative for the ray) to the same objects, so the gates and the D3D11/Vulkan patch fallback agree
  with the GPU. `MetalPrecedenceTests.TheViewport_…` fails with the bias at 0 and passes with it.
- A shader-side bias was considered and not used: the vertex does not carry its object's kind, and alpha alone cannot
  tell a dielectric from a port sheet or a face tint (which is already lifted off its face and must not be pushed back
  behind it).
- The viewer OPENS with the outermost dielectric hidden (`InitiallyVisible` false for it) — a test of the substrate's
  draw has to show it first.
- D3D11 and Vulkan compile; only Metal ran (the gates' plan counters and the CPU picks are backend-free).

## brief-em3d-67 — named edges, Edge mode's hover and snapping to curves (2026-09-27)

- **An edge is a run, built beside the feature table** (`Scene3DEdges`, `Scene3DFeatureTable.Named`): a managed
  object's segments grouped by face pair and connectivity; a kernel object's taken from the worker's edge table. The
  table is still keyed by MESH, so the names that come with a mesh's first build are its names; a moved object has a
  new mesh. Numbering needs the object's own frame, which only the editor knows, hence `Scene3DBuildOptions.OwnFrame`.
- **Managed tessellation leaves T-junctions where a face's loop has a collinear vertex.** The ear clipper drops the
  degenerate ear, so the face's boundary skips the vertex its neighbour uses; the feature table then sees two rims
  (`side0|`) instead of one shared edge. Pre-existing and harmless for drawing; it means such edges do not chain or
  carry both faces' names. The tangent-chain gate uses a box drawn as two halves (every loop a plain rectangle).
- **A circle's seam vertex is not offered as a snap.** It is where OCCT happened to start the curve, and at tier 1 it
  beat the circle's centre (tier 2, the face-centre tier) whenever the rim was inside the radius — so the centre of a
  small bore could never be reached. An open edge ending at the same vertex still offers it.
- **Edge mode needs no new GPU mode.** The shaders know three modes; Edge writes Vertex's value, so nothing is
  highlighted on the GPU and the 2D overlay draws the hovered and selected edges. The frame plan skips its selection
  pass for Edge as it does for Vertex. No shader changed.
- **Two ghost kinds for the fillet preview**: `Pickable` (translucent, still hovered and selected — the target whose
  edges the panel lists) and `Inert` (drawn as it is, never picked — the result seen through it). The pick patch's
  nearest surface is the translucent target's, which encloses the result, so edges stay pickable through the preview.

## Silk drawn for contrast against the canvas, stored colour untouched (2026-09-27)

A board designer reported Silk Top unreadable on the light canvas. The shipped technologies store
Silk Top #F2F2F2 and Silk Bottom #C8C8C8 (the ink's colour); the Gerber importer stores a dark Silk
Top and a light Silk Bottom. No stored colour reads on both the white and the dark canvas, so
`LayerCanvasContrast` resolves what a silk layer is DRAWN in: when its Rec. 601 luminance is within 96
of the background's, it is blended toward black (light canvas) or white (dark canvas) until the
difference reaches 170. Hue and alpha are kept; the `.ctech` is never written.

- **Silk only, deliberately.** Copper and process colours are chosen hues that separate layers (a kit
  can carry hundreds); a luminance guard would recolour them by a rule nobody chose. Silk is a neutral
  and has nothing but luminance to be seen by.
- **Identified by structured fields, never the display name**: `Interchange.PcbLayerName`
  `F.SilkS`/`B.SilkS`, a Gerber file function starting `Legend`, or purpose `silkscreen`.
- **Applied where each frame builds its layer map** (main draw, instance draw, snap overlay) and to the
  designator colour, so ghosts, placements and ref-des text agree. One comparison per layer per frame;
  a clone only for a silk layer that actually moves. Nothing per shape.
- **The tile key hashes `IsSilkscreen`** alongside the stored colour and the background — the drawn
  colour is a pure function of those three, and the interchange field is not otherwise in the key.
- **The Layers panel swatch still shows the STORED colour** — it is what the colour field edits.
- CLI `render` and the GUI share the path, so they agree. Doc figures whose light variant draws silk
  will change on the next DocGen run (silk used to vanish there — `DocFootprintFixtures`' remark).
- Gate: `tests/Ui.Tests/Render/SilkCanvasContrastTests.cs`.

## Designer feedback 02 — a board from its layout drew no dielectric, and took seconds to draw at all (2026-09-27)

- **No dielectric.** The viewer opens with the OUTERMOST dielectric hidden (`InitiallyVisible` false) — right for a
  `.cem`, where the substrate is a slab the size of the solve region and the traces are what a user came to see. The
  3D editor used the same rule, and a board placed from its layout (New 3D View from Layout) is bounded to the board:
  a two-layer board's only dielectric IS its outermost, and a four-layer board lost its core, so the copper drew
  floating in air. `Scene3DBuildOptions.HideOutermostDielectric` (default true, the viewer's) is passed false by the
  editor, which now shows every dielectric. Gate: `LayoutBoardViewTests.ABoardPlacedFromItsLayout_OpensWithItsDielectricShown`.
- **Seconds before the first picture.** Measured with a scratch harness (Release, this Mac) on the round-7 board
  (691 solids, 228k triangles): elaborate ~1.0 s + scene ~1.5 s cold before; the largest single cost was brief-67's
  NAMED EDGE RUNS (`Scene3DEdges.OfSegments`), made eagerly for every object in the build — ~0.6-2 s depending on load,
  about as much as all the rest of the scene. A pour with ~18,000 hole vertices is ~18,000 side faces to pair and walk.
  Only Edge mode, a fillet and a kernel snap read them, one hovered object at a time. `Scene3DFeatureTable.Named` is
  now a `Lazy` made on first read (the kernel's table stays eager — it is cheap and its snap reads it), and the snap's
  "is this a kernel table" test reads `NamedFromKernel` instead of making the runs to ask. With the merge memo in
  `src/Design`, the same cold build measured ~0.7 s + ~0.6 s under heavy machine load. Gate:
  `LayoutBoardViewTests.TheFirstScene_MakesNoNamedEdgeRuns_UntilOneIsRead` (a per-table flag, not a timing).
- **"Nothing, for a while".** The status line that says "Generating the 3D problem…" is hidden by default since editor
  round 2, so a build that takes seconds read as an empty grid. The canvas now shows "Building the 3D view…" while
  a view that has drawn nothing is building (`Viewer3DViewModel.IsBuildingFirstScene`). Reopening a view is still a
  cold build — the elaborator's child cache lives with the editor.

## brief-em3d-70 — every kernel solid was missing from a section (fixed, 2026-09-27)

`render --section xz@y=0` of the 3D Connector's launch drew the bore's fill and the board and NOTHING of the housing,
flange or pin — the three kernel solids (`Em3dShapeSolid`), with no note saying so; the iso view drew them. The cause
was not where the plane fell (moving it off the axis changed nothing): `Em3dSectionScene.MeshCut` chains crossing
points by the mesh's EDGE keys, which assumes a welded mesh, and a kernel solid's tessellation arrives face by face,
each face with its own copies of the nodes it shares with its neighbours. No loop ever closed, so each kernel solid
contributed nothing. `MeshCut` now welds by exact coordinate first — the kernel writes a shared edge's nodes to
identical coordinates, which is the same weld `CsxcadWriter` already does for openEMS's PLY. An analytic primitive
(the bore, an `Em3dCylinder`) was always cut by formula, which is why it alone survived. Gate:
`tests/Ui.Tests/Em3d/Em3dSectionMeshCutTests.cs`.


## 3D vector copy and drawing export (2026-09-27)

The owner asked for the 3D view in vector form: Copy as Vector and Export as Vector beside the picture commands, and an
Export Drawing… that puts several views and sections on one sheet. `Em3dSectionScene.Outline`
(`Renderers/Em3dOutline.cs`), `Em3dDrawingSheet` (`Em3dDrawing.cs`), `Em3dDrawingExport` and `Em3dText` are the new code.

- **The isometric beside it is untouched, on purpose.** `Em3dSectionScene.Iso` (`render --iso`, the doc figures) is a
  fixed +x +y +z viewer drawn as an isometric DRAWING (true projection × 1.2247) and wire-framed. The new outline is a
  separate function over any `Em3dProjection`; `Em3dView.Iso` still comes out byte for byte as before (checked by
  rendering iso and sections of the 3D Package and the Via example to SVG, PDF and PNG before and after — identical — and by
  `Em3dRenderExplainTests`). The app's own Standard View *Isometric* is NOT the CLI's: the camera's iso looks from
  +x −y +z. A drawing's views use the camera's angles (`Em3dProjection.Standard` mirrors `Camera3D.SetStandardView`), so
  *Top* on paper is *Top* on screen.
- **One edge rule for every primitive.** The outline takes every solid and sheet as `Em3dTessellation` triangles, WELDS
  them by position (a kernel solid's display mesh repeats a vertex per face; unwelded, every triangle edge reads as open
  and is drawn), and draws an edge when it is open, turns more than 30°, or is a silhouette. A box is clipped to the frame
  first, as the isometric clips it.
- **Hidden edges** are found by sampling each edge against the projected triangles in a bucket grid and bisecting each
  change of state. The containment test INCLUDES the triangle's boundary: strict containment let an edge that lies exactly
  under the diagonal of the face in front of it leak through, and let a box's bottom outline show through its own top in
  a Top view. What keeps an edge from hiding behind the faces it bounds is the depth tolerance (1e-7 of the picture's
  span), not the boundary. Above 400,000 triangles nothing is hidden and the caller gets a note.
- **Text as outlines** (`Em3dText.Draw`): every label of a 3D picture now goes through one helper. Off, it is exactly
  `SKCanvas.DrawText` (the CLI's bytes are unchanged); on, it draws `SKFont.GetTextPath` placed by the same advance-width
  alignment. That is the fix for SVGs opening in a substitute font: Skia's SVG device writes `font-family` and the reader's
  machine supplies the face. Drawings and vector copies default to outlines; the dialog can turn it off.
- **One round scale per sheet.** `Em3dDrawingSheet.Layout` sizes each grid column to its widest view and each row to its
  tallest (uniform cells left a flat package's side views as thin strips in cells as tall as its top view), picks the
  column count giving the largest scale, and rounds DOWN to 1, 2 or 5 × 10ⁿ paper to model (`RoundScale`).
- The legend lists a material once: conductors are drawn in their LAYER colours, and one material on three layers read as
  three materials.

## brief-em3d-75 — temperature on the field path (2026-09-28)

A thermal run's field is one more quantity on brief 29's path: `FieldRun` lists `postpro/paraview/thermal` as
`FieldProblemKind.Thermal` (timestep = the 0-based sweep point), `T_C` is the known array "Temperature, °C", and the cell
array `material` is bookkeeping like `attribute`. No second reader.

- **The temperature is not a signed quantity.** A real scalar in `Value` mode was `Signed` (a potential, drawn on a
  diverging map about zero); `FieldQuantity.IsTemperature` excludes `T_C`, so it takes a sequential map (`ColorMap3D.Inferno`,
  new) between `FieldColorScale.MinMax` — the TRUE minimum and maximum (percentile 100, "maximum" in the legend, D9). The EM
  default stays the 99th percentile; the gate draws both on one field to show the percentile would have clipped the hot node.
- **Choosing the hot node for that gate takes care.** On the six-tetrahedra-per-cube block a corner on the cube diagonal is
  in six drawn triangles, 1.04 % of a 4 × 4 × 4 block's exterior vertices — so the 99th percentile WAS that node. A corner off
  the diagonal is in four (0.69 %).
- **Recipes (gate 7).** A slice (`FieldSlicer`) or a gather (`RegionBoundary`, `Boundary`, the new `Exterior`) now records,
  per drawn vertex, the mesh nodes it came from and the slice's edge fraction (`FieldRecipe`). `FieldSurface.Revalue` re-reads
  another array of the same mesh through it, so a thermal sweep step reads one array (`FieldStep.WithArraysOf` keeps the mesh
  already read) and cuts nothing. `ILinearTets` gained two default members (`Nodes`, `MeshSize`) so a synthetic set of
  tetrahedra needs no change; `FieldMeshTets` implements them, and a constrained call on the struct does not box.
  `Scene3DFieldGeometry.GeometryVersion` stays put across a revalue.
- **What gate 7 does not claim.** `FieldVertex` interleaves position and value (36 bytes) and all three backends bind one
  field stream, so a revalued step re-uploads the unchanged positions with the new values. Splitting the stream touches
  every backend's pipeline and the compiled SPIR-V; not done here.
- **Wires (R-em3d75-5).** `FieldWires.Colour` colours the scene's OWN triangles of a wire: each vertex is matched to its ring
  by its exact scene-local float position (the scene converts the ring's doubles the same way), an end cap's centre to its
  ring, and ring k takes the 3D arc length of path vertex k. The scene re-orders a solid's vertices per (vertex, face), so an
  index-based ring map would be wrong; position is exact.
- `FieldFaces.OnFace` cuts a region's boundary down to one face by matching the mesher's triangles to the display's (the
  centroid within 1 % of the object's size of a display triangle whose normal agrees) — two tessellations of one face.

## brief-em3d-82 — E and H on a picked face or sheet (2026-09-28)

A picked face is a fourth place an EM field is drawn, beside the clip plane, the selected region and the conductors.
`FieldFacePainter` (`FieldFacePaint.cs`) routes each face by what the MESH knows about it, not only by its scene kind: a
name with a 3-D group is a region (its own side, `RegionBoundary` → `OnFace`, exactly temperature's path) even when the
scene calls it a conductor; a sheet goes to `OnSheet`; a name with only a 2-D `Conductor` group is a void.

- **A void's face is cut from the boundary of the UNION of every region, not from each neighbour's own boundary.** The two
  differ exactly at a coplanar interface beside the metal — a substrate's top where the trace is not. That interface is on a
  region's own boundary, and near the trace's edge its triangles' centroids are within `OnFace`'s 1 %-of-the-object
  tolerance, so the per-region version would paint a strip of the substrate/air interface onto the trace. In the union it is
  an interior face and never reaches `OnFace`.
- **`OnSheet` matches tet faces by CORNERS, with a tight tolerance** (1e-6 of the scene's diagonal from the view model), not
  `OnFace`'s centroid-within-1 %: a thin tetrahedron just off the sheet has all three corners within 1 % of a small sheet
  and would be taken. The sheet's corners are imprinted, so they lie on it to Float32. Its side is the side of the tet's
  fourth corner along the sheet's own (area-weighted) normal; each sheet triangle is emitted once, from the side asked for.
  A cell with fewer than three corners in the sheet's grown box is rejected before any distance is computed.
- **openEMS has no regions**, so every face is SAMPLED (`FieldFaces.Sampled`): split in four until its longest edge is within
  the grid's IN-PLANE spacing (a substrate's thin cell across the face says nothing about how finely to split it), each vertex
  read by `SampleOffFace`. **The nudge leaves the whole dump cell the face is in, not one ULP**: the dump is at cell centres
  (DumpMode 2) and the grid puts a line on every metal face, so a face lies midway between an air-side and a metal-side dump
  node, and a point a hair off it interpolates halfway to the metal's value. `FieldSampler.CellBounds` gives the located
  tetrahedron's box, which on `VtrReader.Mesh`'s six-tets-about-the-diagonal split IS the grid cell's; the point moves to
  where it leaves that box along the chosen side, i.e. onto the first dump node's plane. Gate 5 reads E(z = 2.5) off a face at
  z = 2 with dump nodes at 1.5 and 2.5, to a few Float32 ULPs.
- **Which side of a solid openEMS reads is a deviation from the brief's wording.** The brief says "a face of a solid:
  outward". That is right for a conductor, but for a dielectric, the air or a body it would show the NEIGHBOUR's side, while
  Palace's picture of the same face (`RegionBoundary`) is the region's own — the two solvers would disagree on one face by
  the permittivity ratio in E_n. `Paint` reads outward from a conductor and INWARD into a region; the draw nudge is outward in
  both cases, so the paint sits in front of its face.
- **H from openEMS is an opt-in (owner: `OpenEms.SaveH`, off by default — twice the disk).** `CsxcadWriter` writes an
  `hfield<k>` DumpBox (DumpType 11) beside each `efield<k>`, same box, DumpMode 2 and FileType; a setup without it writes the
  bytes it wrote before (the goldens under `testdata/em3d/openems-goldens` are untouched). `FieldStep.OpenOpenEms` reads the
  H pair beside the E pair into the same step — refusing by name an H dump on a different grid — so
  `FieldQuantity.Offered` lists |H| with no change of its own.
- `FieldStep.InMemory` (internal) builds a step from a synthetic mesh with its arrays loaded — the gates use it rather than
  reach into `_loaded`.

## brief-em3d-84 — the field plot's resolution below the firewall, and a headless field section (2026-09-28)

- **`FieldPlotResolver` is the 3D view's, the 3D editor's and `render --field`'s one resolution** — moved
  unchanged from `Viewer3DViewModel.Plot.cs` and `C3dEditorViewModel.FieldPlots.cs` with
  `FieldPlotRequest`, `FieldDiscovery`, `FieldSolutionItem` and `PaintedFieldFace`. Brief 83's
  `FieldPlotTests` pass unchanged. It also carries the SCENE ORIGIN rule (the centre of the elaboration's
  display extent): a ClipPlane plot's plane is a `float` offset from that origin, and a mesh node exactly
  on the plane counts with the negative side, so a different origin can cut a different set of triangles.
  The viewer's private `PickSolution` helper is `PlotSolution` now, so a scan can hold that `src/Ui`
  defines none of the resolution. The colour-map rule is `ColorMap3D.For`; the stale comparison is
  `C3dRunDocument` in `src/Design`.
- **Skia blends vertex COLOURS; the GPU blends the FIELD.** On the committed cavity's coarse slice (144
  triangles over the whole cut) one triangle's corners read 1,115 and 1,972 V/m; a plain `DrawVertices`
  pixel at x = a/4 inverted through viridis to 1,881 V/m where the field is 1,787 and the slice's own
  linear value 1,771. A PNG triangle is now subdivided until each piece spans ≤ 1/32 of the range, its
  corners carrying the components interpolated as the GPU interpolates them: the pixel reads 1,774.5.
- **Subdividing some triangles and not their neighbours leaves T-junction pinholes** — single background
  pixels where the two edges' coverage differs. The slice's own triangles are drawn flat underneath first.
- **The data is not zero on a PEC wall.** Palace's node-averaged output holds 88 V/m on the cavity's x = 0
  wall (3.4% of the top), so "the pixel is the map's bottom colour" holds to the data, not to zero; gate 2
  asserts the first pixel inside the wall reads the data there and sits in the bottom 5% of the map.
- **Thinning buys little on an FEM slice.** A 120,000-cell second-order connector run sliced through its
  middle gives 3,784 / 8,681 / 11,565 triangles (x / y / z) at ~28 bytes of SVG each (119 / 227 / 330 kB).
  Merging neighbours of one colour step took 11,565 to 9,147 pieces at 256 steps and to 8,908 at 64 —
  about a fifth of the bytes either way, because the mesh is graded to the field's gradients. 256 steps
  kept; `VectorTriangleLimit` is 50,000 (~1.4 MB), where size, not fidelity, is the problem.
- **Thinning must not enumerate a hash set.** `HashCode` is seeded per process, so a `HashSet<(ulong,
  ulong)>`'s order — and the loops chained from it, and the SVG's bytes — would move from run to run. The
  boundary edges are a list in the order the triangles gave them. Vertices are welded by the mesh edge
  they lie on (`FieldRecipe`), never by position; a cut exactly at a node (t = 0 or 1) keys as that node.
- **A vector device records `drawVertices` as nothing** (the data display's finding, above) — so SVG and
  PDF take one colour per triangle, its centroid's (the channels' mean, then the quantity's reading).
- **`FieldPicture.Paint` takes a typeface.** Export picture keeps `SKTypeface.Default`; the section passes
  `SkiaFonts.PlexRegular`, because `render`'s bytes may not depend on the machine's fonts.
- **With a field, only conductors are filled** (they carry none); dielectrics are outlined in the label
  ink over the field, air is left to the field, and the Materials legend gives way to the field's.


## A plated via drawn as a tube, and Copy as Vector in perspective (2026-09-29)

- **A plated via looked filled.** The solver gets a plated barrel as a copper cylinder plus a coaxial, higher-precedence
  AIR cylinder (R-em3d3-5d), and the pads the barrel passes through as whole polygons; the precedence rule subtracts the
  air from both. A surface renderer draws each solid's own surface and hides air, so the view showed a solid rod under an
  unbroken pad. `Scene3DBores` (view only) carves what the rule would: a conductor cylinder with a coaxial bore of higher
  precedence is tessellated as a tube (`Em3dTessellation.OfTube`), and an extruded conductor a bore passes RIGHT through,
  wholly inside it, gains a hole at the tube's own vertices. The problem is untouched, bit for bit. Faces keep their
  numbers (the bore walls are −1), so a face picked on a pad or barrel is named as before. A bore that stops part-way,
  clips an edge or overlaps another hole is not carved.
- **Copy as Vector was orthographic by design** and the view it copied was often in perspective. `Em3dProjection` now
  takes an eye (`WithEye`): a point is scaled by Focal / its distance ahead of the eye, about the eye's axis, so the focal
  plane (the orbit centre) projects exactly as orthographically and the scale-bar window frames both alike. Its depth is
  `eye·T − Focal²/ahead` — affine in 1/distance, which is what keeps the hidden-edge test's barycentric interpolation exact
  on a projected triangle. Silhouettes are judged from the eye at each edge's midpoint; edges and triangles nearer than
  `NearFraction` × Focal are clipped/left out rather than folded through infinity. Orthographic behaviour and bytes are
  unchanged (no eye: every new branch returns the old expression).


## A temperature section, headlessly (brief-em3d-88, 2026-09-29)

- **A wire is painted from its chain, and the cut is a chain of CAPSULES.** Each chain segment's cut by the plane is
  drawn exactly for the cylinder along it: chords perpendicular to the projected axis, half-width √(r² − d²·sin²φ). That
  alone left half of every crossing blank on *Thermal Output Wires*' x = 820 µm plane: the loop top crosses it FLAT, so the
  segments there run nearly along the plane's normal, and a finite segment's chords are only those of its OWN axis points —
  a thin band of the circle. Each node's sphere (a disc of √(r² − d²)) fills that and the notch at every bend. **The discs
  go UNDER the strips**: a disc is one colour, and over a strip's gradient they read as a staircase along a wire lying in
  the plane.
- **`FieldSection.Cut` ranges a temperature by `FieldColorScale.MinMax`** (the view's D9 rule), no longer `Auto`, whose
  0-to-99th-percentile put 85 °C at a third of the way up the map. The wires then extend it (`Em3dSectionThermal.WithWires`):
  on *Output Wires* at 8 A RF the slice reaches 237 °C and the edge wires 270.8 °C. This point's range, not the sweep's
  union (owner decision Q1). **No mirroring**: the 3D view reflects a temperature across the document's symmetry planes;
  the section draws the modelled part only (*Eight Fingers* is the half at y ≥ 0).
- **The thermal page** draws no port, no air-box edge or face label and no air-box caption; every metal is outlined in the
  label ink and none filled. The boundaries are cut by the plane from `ThermalLowerings.FacePieces` (the mesher's and the
  editor's tint's own placement), drawn in the tints' blue/green and labelled in the band beside the frame side they are
  nearest; `*exposed*` and a face the plane misses go in the caption. A value is shown at the point: a swept variable by
  its axis value, anything else through the document's resolution, else as written.
- **`--labels`** writes a material once per object, on its largest cut, at the grid point furthest from the edges where
  the label's box fits inside and clears the labels already placed; ink over a background halo, so it reads over any field.
- **`--tight`** is `Layout(…, tight: true)`: no legend column, no band, no caption; the legend is Export picture's painter
  translated into the frame's top-right corner. The CLI sets the page height from the frame's aspect.
- **`--axes` / `--scale-bar` reuse `Em3dDrawingSheet.DrawChrome`**, the vector export's painter, translated into the frame,
  with a new optional light backing panel: a thermal section's bottom corners are the flange at the map's darkest, where
  the chrome's dark ink vanished. `ScaleBarLength` moved from `Viewer3DOverlay` (Ui) to `Em3dDrawingSheet`, so the view's
  bar and `render`'s round the same way; one DIP of the view is the page's text size / 11. A section looks along its
  normal (XZ looks along +y, so y points into the page).

## brief-em3d-90 — a face boundary's tint is picked, in front of its face, without entering the ID pass (2026-09-29)

**Tints are NOT drawn in the GPU's ID pass, deliberately.** That pass is also the snap's patch (`Scene3DIdPatch`) and the
face a drawing tool lands on (`LastPick`). Made pickable there, a tinted face would snap to the tint's corners — lifted off
the face, so every snapped point on a tinted face would come back inexact (≈) — and a heat source drawn on it would find
no face. So `Scene3DObject.Pickable` still excludes them, and a new flag, `Scene3DObject.Tint`, lets the CPU reach them:
- **hover** (`Viewer3DViewModel.TintUnderCursor`, Object and Face mode) ray-tests only the tints (`Scene3DPicking.TintHits`,
  gated on `Scene3DModel.HasTints`) and takes the nearest when it stands at or in front of the ID pass's object;
- **B** (`RayHits.Collect`) lists tints in Object and Face mode (face 0: a tint has no faces of its own) and
  `TintsFirst` moves a tint ahead of any non-tint hit it trails by no more than the tie.

**The priority is by kind, not by the lift.** The builder lifts each tint 1e-4 of the BOX's size off its face so it never
z-fights; the tie (`Scene3DPicking.TintTie`) is that lift, recorded on the scene (`Scene3DModel.TintLift`), so equal depth
resolves to the tint by rule, and a tint hidden behind a solid by more than the lift is never taken. A first version tied
at 2e-3 of the scene's diagonal — 260 µm in the test scene, larger than its 40 µm model — which would have let a tint
BEHIND a solid win. The lift has to be the measure because the scene's bounds include the air box.

**Observed, not changed:** because the lift is relative to the air box, a small model in a large box floats its tints
visibly off the face — 7.5 µm above a 20 µm part in the test fixture, whose thermal setup still builds an EM-sized box. A
camera fitted to the CONTENT alone can put its near plane between the face and its tint, and then neither draws nor picks
the tint. The owner may want the lift taken from the content instead; that trades against z-fighting when the box is
drawn, and was left for a decision.

**An EM face boundary's tint and a thermal one are built by the same code** (`FaceTints` → one `Scene3DObject` each), so
both follow the same rule; their rows differ only in name (`boundary:obj/face` is the EM row's own name, a thermal row is
`thermal:obj/face` and its tint `boundary:thermal:obj/face`). `Scene3DBuilder.TintLabel` names either for B's readout.

## brief-em3d-92 — per-object transparency: the draw order, the drawing's occluders, and what the iso does not show (2026-09-29)

**The draw order was already back to front, and nothing was added to it.** `Scene3DFramePlan.Plan` collects every
translucent batch (and an opaque one faded by selection), keys each on its object's CENTROID's view depth
(`-dot(centroid − eye, forward)`, view depth rather than eye distance so an orthographic eye inside the scene still sorts
right) and draws them farthest first, with depth test on and depth write off. So a conductor made see-through simply joins
that list: `Scene3DBuilder` packs its alpha and sets `Translucent`, and the plan moves it from the opaque pass to the sorted
one. A trace on a substrate sorts correctly from both sides (the trace's centroid is nearer from above, farther from below).
**Artefacts that remain, since order-independent transparency was out of scope:** the sort is per OBJECT, so two
translucent objects that interpenetrate, or a large one enclosing a small one (a see-through lid's centroid can be nearer
than a die inside it from some angles), can draw in the wrong order; and one object's own triangles are not sorted, so a
see-through solid's far faces blend over or under its near ones in index order. A translucent object writes no depth, so an
object behind it is never cut away — it only blends in the wrong order in those cases.

**Where the alpha comes from.** `Scene3DBuildOptions.Transparency` answers per elaborated name a `Scene3DTransparency`
(the object's own percentage, null for its kind's, and the opacity its instances multiply on); `C3dTransparency.Alpha`
turns that and the kind's alpha into one byte, rounded half away from zero (60 % is 102). It replaces the kind's alpha
BEFORE the context, ghost and wireframe looks, which keep their own and win. `Scene3DObject.Transparency` keeps the pair,
so the vector export (which has only the scene, not the elaboration) can paint and occlude from it.

**Instance runs must be split by transparency.** An array element draws its prototype's VERTICES, colour and alpha
included, and the editor's `InstancingFor` keyed a run on cell, view and rotation only — so two placements of one cell at
different transparencies would have drawn the second in the first's alpha. The key now carries the instance's
transparency.

**The drawing's occluders: one decision, one place.** `Em3dDrawingExport.OutlineOptions` is the only reader of
`Em3dDrawingRequest.TransparentObjectsOcclude`; false (the default) puts every object stating a transparency above 0 (or
sitting in an instance that does) in `Em3dOutlineOptions.NonOccluding`, which the `Occluder` leaves out of its triangles.
Its own edges are still drawn and still hidden by opaque things in front. A default-transparency dielectric still
occludes, as before. An outline view has no fills at all, so "its fill painted over them" applies to sections only; there
the fill takes the alpha (SVG `fill-opacity`, which Skia spells as the float it holds, `0.40000001`) and the edge stays
opaque. The PDF stays vector: a translucent fill is an ExtGState alpha, not an image.

**`render --iso` does not change with transparency, and that is not a bug here.** `Em3dSectionScene.Iso` is a wire-frame
with no fills and no hidden-line removal, so there is nothing an alpha could act on. It is NOT the renderer Export Drawing
uses (that is `Em3dSectionScene.Outline` + `Em3dDrawingSheet`); the brief assumed it was. A section, a field plot's context
and a surfaces plot do honour it.

## 3D editor bugs round 9 — coincident faces, and a lumped port drawn as what it is (2026-09-29)

- **The tie order at a coincident face is now four levels** (`Scene3DDepthTie`): dielectric/air < metal < via metal <
  port surface (owner's rule for the view; the first step is round 3's em-3d.md §6.3a). `Scene3DDraw.Behind` became
  `Scene3DDraw.Tie`; `Scene3DFramePlan.TieOf` is the one place a kind maps to a tie, and `DepthBias(tie)` the one place a
  tie maps to a polygon offset. Opaque draws now carry it too (only Translucent and Pick did), and an array element holding
  a via is drawn object by object, since its one-draw fast path carries one offset.
- **The via speckle was a via's top ring and bore wall lying exactly on the pad's**: `Scene3DBores` carves the pad with a
  hole at the tube's own vertices, so the surfaces coincide, but the tube's wall is one quad the via's full height and the
  pad's is one quad its thickness — different triangles, so interpolated depths differ by a float step either way. The
  scratch A/B on the Metal device (`RenderPixels`, the owner's own board) reproduced the screenshot exactly with the via
  tie at 0 and was clean with it at −4.
- **A bias TOWARDS the eye must not be slope-scaled.** The first cut used −4 constant / −2 slope for a via, and the barrel's
  silhouette — a wall seen edge-on, where the slope term is unbounded — came through the copper above it as a dotted trail
  below every hole. Only `Behind` (pushed away) keeps the slope factor; the others are constant-only, which covers the few
  float steps two triangulations of one plane differ by.
- **Vulkan's depth bias is dynamic state now** (`VkDynamicState.DepthBias`, `vkCmdSetDepthBias`, on every pipeline so no
  bind disturbs it): four ties across three triangle pipelines would have been twelve static twins. Clamp 0 needs no
  device feature. D3D11 keeps one rasterizer state per tie. Only Metal ran; D3D11 and Vulkan compile.
- **A rectangular lumped port is a checkerboard over its own rectangle** (`Scene3DBuilder.Checkerboard`), outlined, with a
  flat arrow in its plane pointing to the + edge, centred across its width. The arrow sized from the port's LONGER side
  stood ~7× the height of a 900 × 100 µm port off into space. The arrow's triangles come after the cells in the port's one
  translucent draw, which writes no depth, so they paint over the cells like a texture and cannot fight them — and an
  opaque object in front still hides both. Per-cell colours needed `Builder.Object(vertexRgba:)`; nothing recolours a
  scene's vertices afterwards, so that is safe. A wave port keeps its voltage-path arrow and a coaxial port its annulus.
  The SVG/PDF vector export still paints a port in the object's one colour.
- **Not fixed, and worth knowing:** with the camera inside the scene's sphere, `Camera3D.DepthRange` puts near at
  far × 1e-4 on a standard-Z float buffer, so two faces a few µm apart can fight when zoomed deep into a large scene. The
  tie order does not address that; reversed-Z would.

## A non-plated hole drew no hole in the substrate (2026-09-30)

A layout via row with plating off is an air cylinder through its span in the 3D problem (`Em3dLayoutSolids`), but the
3D view — Show 3D View and the 3D editor alike, both through `Scene3DBuilder` — carved bores out of METAL only
(`Scene3DBores`), and a surface renderer draws the substrate's own surface and hides air: the slab looked solid. A
dielectric is now carved too, by a BARE bore only (no metal cylinder around it). A plated via's bore is not carved out
of the substrate, because its barrel hides that and a board of several thousand vias would otherwise triangulate one
slab with several thousand holes. A box-shaped slab is carved as its rectangle extruded, its faces renumbered back to
the box's (`BoxFace`) so a face picked on it is named as before. View only: the problem is unchanged.
Gate: `PlatedViaAndPerspectiveTests.ANonPlatedHole_IsDrawnAsAHoleInTheSubstrate_AndABoxSlabKeepsItsFaces`.

## A flattened layout's copper drew in the stackup edge ink (2026-09-30)

Before a flatten, a layout instance's conductors draw in their drawing layer's colour. After it they are document objects
with no drawing layer, and `Em3dSectionRenderer.ObjectColours` gave anything without a layer or a wire the stackup edge
ink — a grey that is no material's colour. The owner expected each object to take its material's colour. A conductor with
no drawing layer now takes its material's stated `Color`, else `C3dMaterialRole.ImpliedColour` — the one table the
Materials editor's list swatch also reads, so the swatch is the colour on the canvas. A hand-drawn Copper box changes the
same way. A material the technology does not define still gets the ink.
**Trap found on the way:** `render` of a `.c3d` handed `ObjectColours` the layout setup's technology, which is null for a
`.c3d`, so the CLI and the 3D view could colour one document differently; it now passes the elaboration's technology.
Gates: `HierarchyGateTests.Flatten_APlatedViasAirCoreNeedsNoAirInTheTechnology_AndARefusalReachesTheDialog` (the colour)
and `TransparencyTests.TheVerb_AsAProcess_MatchesTheRenderer_…` (process vs in-process, byte for byte).

## A field plot's drive, applied per plot (brief-em3d-100, 2026-10-02)

**openEMS field strengths were referred to nothing.** `FieldRun.OpenOpenEms` drew the frequency-domain dump raw: a DFT of
the response to a Gaussian pulse, whose magnitude depends on how much energy the pulse has at that frequency. An openEMS plot
and a Palace plot of one structure disagreed by an arbitrary, frequency-dependent factor. Each openEMS step now carries
`FieldPortDrive.DumpScale` = 0.5 / v_inc × √(2·R·PalaceDrive.IncidentPowerW). The ½ is the dump's ×2 against the probes'
DFT (OpenEmsFarField's header). v_inc = (U + Z₀·I)/2 comes from port k's own probes in `p<k>/`, run through
`FdtdPortTransform.Dft`, the function S is formed with. The step is loaded through `FieldStep.Open(pvtu, toMetres,
dumpScale)`, and E and H both take the scale. That expression is the only `0.5 / v` in `src/Render/Scene3D/Fields`, and a
source scan holds it there. Dividing by v_inc also sets the phase reference to the incident wave, as Palace's is. A run
missing the driven port's probes or its kept document is drawn as written, with no unit and the line
`Drive: not referred, this run kept no port record (relative values)` (D3).

- **The scale goes on the plot's own values, never the shared step.** Brief 96 shares one `FieldStep` among every plot of a
  solution. So `FieldDrive.Read` gives one factor per plot and quantity, k^exponent (`FieldNames.DriveExponent`: ½ for
  amplitudes, 1 for S/U_e/U_m, 0 for the indicator and a port's mode field E0_k, null and "not referred" for an unknown
  array). That factor multiplies the surfaces `FieldSurfacePlot.Em` / `FieldSection.Cut` have just built, and the hover's
  sampled channels. The range group key includes the factor, so two drives never share a range.
- **A power or energy density's dB is now 10·log₁₀, and a field strength's stays 20·log₁₀**
  (`FieldColorScale.DbPerDecade`). The brief's one rule, that a k-fold drive moves every scaled array by 10·log₁₀(k) dB,
  requires it. Before this, S, U_e and U_m read 20·log₁₀ (twice the dB). The shader's dB is still 20·log₁₀:
  `FieldUniforms.Write` hands it a power's range doubled, so no shader changed.
- **The picker label names the port, not a power** (`SolutionLabel`). The drive is the plot's, on its own legend line
  (`LegendLines(..., drive)`), which the view, Export picture and `render --field` all print.

## Reference images in the scene and the pictures (brief-em3d-101, 2026-10-03)

- **`Scene3DDepthTie.Underlay` (−2), below `Behind`**: an image sheet gives way to EVERY face on its plane — a polygon traced
  on the image is drawn over it and is what the ID pass (and the CPU pick, through `TieNdc`) finds. **`FaceImage` (2), between
  `Via` and `Port`**: an image on a face wins over the face (any kind of object's) and loses to a port or a field plot there.
  Port and Field moved up one step each; only the order matters. `DepthBias` now gives the slope factor to every tie BELOW
  `None` (it was `== Behind`), so the underlay is pushed away like a dielectric.
- **An image sheet is an ordinary scene object** (`Scene3DObject.Underlay`) whose COLOUR draw is replaced by its image's
  (`Scene3DImageBatch.Surface`): its own triangles stay in the ID pass, the selection's edges and Face mode's on-top pass.
  The frame plan swaps the draw in the opaque or the translucent pass by the batch's own translucency (its transparency, or
  a PNG with any texel below 255).
- **An image on a face is a RECORD drawn over the face, the way a face boundary's tint is** (`Tint`, named
  `C3dImages.FacePrefix + object/face`): its triangles are the face's own, re-emitted, so a click selects the record and B
  steps to the face. Its alpha is its own transparency times its instances' opacity — never its object's (a lid at 80 %
  carries a marking at 0 %: the image writes depth with its FaceImage bias, so the lid's translucent face drawn later fails
  the depth test where the image is).
- **The face frame rule** (`C3dImages.FaceFrame`, `src/Design`, shared by the scene, the vector export and `render`): from the
  face's triangles — area-weighted centroid, outward normal (the tessellation winds counter-clockwise from outside), up =
  world +z projected into the face (+y when |n·z| > 0.999), right = up × normal. A face is flat when the sum of its
  triangles' area vectors is (to 1e-6) its area; otherwise it is "curved" and refused. A fitted image is the largest
  aspect-kept rectangle centred on the centroid inside the face's bounding rectangle in that frame.
- **One placement path for face images**: `Scene3DFaceImages.Place` finds the face's triangles by NAME in the object's own
  tessellation (`C3dImages.IsFace`: `zmax` covers an operation's `zmax#1`, `zmax#2`) and places the picture. The builder calls
  it; `Scene3DFaceImages.Of` calls it for `render`, tessellating as the view does. An object carrying one is never an array
  element (`Instancing` → null), which is slower and never wrong.
- **Vector pictures** (`Em3dSceneImages`): each image is drawn by Skia's `DrawImage` under the affine map of its corners on the
  page, clipped to its sheet's outline (even-odd) or its face's triangles (winding, each triangle oriented the same way on the
  page first), BEFORE every line. Sections draw none. A sheet seen edge-on contributes nothing. `render --iso` puts image
  sheets back into the problem a run leaves them out of and grows the box to frame them (`WithImageSheets`), and the section
  renderer's legend skips the empty material such a sheet has.
- **SVG ids are per process**: Skia numbers clip paths and images per process, so a picture made in a test process that drew
  others first differs from the CLI's in its `cl_N` ids only — the image gates compare with RenderCliVerbTests' `StripSkiaIds`.

### Review of brief-em3d-101 (2026-10-03)

- **A face image turned away from the viewer is not drawn in a vector picture** (`Em3dSceneImages.OfFaces`). The vector
  pictures have no depth test for images, so a box's `zmin` image was painted across its footprint in the Top view and in
  `render --iso`, mirrored. The cull reads the face's projected triangles' signed area (the tessellation winds them
  counter-clockwise from outside), which is right in perspective as well as orthographic. An image behind ANOTHER solid is
  still drawn; only the face's own solid hides it.
- **`Em3dSectionScene.Project` (the `render --iso` outline) is a mirror image of the view it names**: +x runs right, where a
  viewer at +x +y +z sees it run left (right × up points away from the viewer, and `Em3dSectionRenderer.Looking` uses the same
  axes). Lines cannot show it; a picture can, so every image in `render --iso` reads mirrored. Left as it is, because changing
  it flips every existing iso picture: `ProjectIsMirrored` says so, and `OfFaces(mirrored:)` culls by it. Changing the
  projection is the owner's call.
- **A broken image path is read again once its file changes** (`Scene3DTextures.Get`). `BitmapCache` keeps a failed decode as
  null, so a file copied in after the scene first asked for it stayed the checker until Refresh Image — while the Inspector and
  `check`, which read the header afresh, said it was fine. The retry is keyed on the file's write time (null when absent), so a
  path that stays broken costs one `File.Exists` per scene build and never decodes again.
- **A face image's downsample note** is made by the one `Scene3DTextures.DownsampleNote`; only the sheet branch made one before.

### brief-em3d-101 follow-ups (2026-10-03)

- **A 3D texture's file is no longer decoded through `BitmapCache`**, which keeps every decode for the session: an 8000 × 6000
  photo's full decode (~192 MB) stayed in memory after its texture was capped at 4096 px. `Scene3DTextures.Decode` decodes,
  reduces and lets go. A file over the cap is decoded at the codec's own reduced scale where that scale is still at least the cap
  (a JPEG's 1/2, 1/4, 1/8), so the full-size decode is often never made. The vector pictures (`Em3dSceneImages.Pixels`) decode the
  same way, and the editor's pixel-size reads go through the header-only `C3dImages.PixelSize`. `BitmapCache.Holds` is the gate's
  check.
- **`render --iso` clipped a face image across a drilled hole** that the 3D view clipped around. `Scene3DFaceImages.Of`
  tessellated the solid's own primitive, while the view's builder draws the surface `Scene3DBores` carves. It now carves the same
  way, and the face numbering is unchanged (`Carved` keeps the primitive's).

### An image sheet on a solid's face was invisible (2026-10-03)

- **A sheet's image lying exactly on another object's face never showed**: an image sheet ties `Underlay` (R-em3d101-4b), below
  every face on its plane, so a picture placed on a housing's face lost the depth tie to that face everywhere. That is right for
  tracing and wrong for a label, so it is now per sheet: `C3dImage.InFront` (written only when true; Properties ▸ In front of
  faces, or the sheet's menu) makes `TieOf` give the sheet `FaceImage` instead, and the image's draw takes its object's tie rather
  than a hard-coded `Underlay`. The vector pictures needed nothing: `Em3dSceneImages` already draws every image after the fills.
- **An In-front image still flickered at some angles and zooms.** It and its face are two triangulations of one plane, and seen
  obliquely their interpolated depths drift further apart than `FaceImage`'s constant offset (8 steps) covers: on an image
  sheet turned onto a box's face, 2,250 Metal views (±77°, five zooms, both projections) lost 17 % of the image's pixels to
  the face, half the views fighting. `FaceImage` now takes a slope factor towards the eye (`DecalSlopeScale`), CLAMPED
  (`DecalBiasClamp`, 2e-4): unclamped it fixed every view but let the sheet through the box along its silhouette when seen
  from behind, the via-trail artefact that kept the slope off every tie above `None`. Clamped: 2 px in one view, on a face
  three pixels across. The clamp reaches every backend — Metal's `setDepthBias:…:clamp:`, D3D11's `DepthBiasClamp`, and
  Vulkan's `vkCmdSetDepthBias` clamp, which needs the device's `depthBiasClamp` feature; the backend now enables it when the
  device has it and otherwise drops the slope (the previous behaviour). A face image on a face benefits identically.
- **Ports and field plots take the same clamped slope.** A port's surface ties `Port` and a field plot `Field`, both above `None`
  with the constant alone, and both lie on faces as an image does. A lumped port on a box's face, the same sweep: 14 % of its
  pixels went to the face in 63 % of the views; with the clamped slope none, and silhouette pixels from behind 382 → 1,728 over
  all the views. A field plot needs a solved field, so its share is unmeasured — the same arrangement, given the same bias. A
  via still takes no slope: its barrel's walls are what made the unclamped one draw a trail, and a via is not drawn on a face.

## A drawn sphere's scene kind and face (brief-em3d-102, 2026-10-03)

- **Checked first, as the brief asked: a drawn sphere never reaches `KindOf`'s `Em3dSphere → Wire` fallback.** Every
  `.c3d` object gets an origin from the elaborator (Conductor / Body / Air by role), so a sphere is drawn as its material's
  kind and colour, exactly as a box of that material is. The fallback is left alone; it serves a problem with no origins.
- **Face 0 is given per object, in the builder**: a sphere whose provenance names one face (`surface`) has its tessellation's
  triangles re-tagged from −1 to 0 (`NamedSurface` / `AsFaceZero`, cached under its own key so the ball's untouched mesh is
  never shared with it). A ball keeps `FaceUnknown`. With every triangle on one face there is no feature edge, no corner and
  no face centre (the face is not planar), so it draws no facet lines. The curved-face refusals (Map Image, Drawing Plane from
  Face, a port, Align) needed no new code: they read a face's summed normal, which on a closed sphere cancels.
- **Its centre is `CapCentres`' one entry**, so Vertex mode offers it as it offers a cylinder's cap centres, and nothing else.
- **A section through the centre is the tessellated default** (`MeshCut`) and closes, though the equator's vertices lie
  exactly on the plane: the crossing test is strict on one side, so each equator vertex counts as below and every edge to
  the band above is cut once.

## Smooth shading normals (brief-em3d-104, 2026-10-04)

- **The rule** (`ShadingNormals`, the only place a shading normal is made). Per scene vertex, over the triangles that use it:
  faces never share a scene vertex (the builder already splits by (mesh vertex, face)), so an edge between faces is always
  sharp; within a face, two triangles sharing an edge through the vertex join one group unless they turn by more than
  `Em3dSectionScene.SharpEdgeDegrees` (30°, the feature-edge constant); a group is what those joins reach. A split vertex is
  DUPLICATED (position, id, colour, face identical; the copies follow every original, vertices ascending, groups in first-use
  order) and only the indices of its later groups' triangles change. The welded, unfaced path takes the same rule.
- **Weights are Max's (1999), not the brief's area weights**: each corner contributes its unit triangle normal times
  sin α / (|e₁| |e₂|). Area weighting cannot meet the brief's own gate 1: a cylinder's side vertex is a corner of one half of
  the facet behind it and both halves of the facet ahead, so the facets weigh 1 : 2 and the normal leans 1.9° off radial.
  Angle weighting fixes the cylinder but leans 0.3° beside a sphere's poles (gate 4 asks 1e-3). Max's weights give each
  cylinder facet 1 / (w h) however the quad is split, and are exact for a vertex whose neighbours lie on a sphere.
- **Degenerate triangles** (height below 1e-5 of the longest edge) contribute nothing and split nothing: one joins its first
  non-degenerate edge neighbour, so it can never bridge a crease. A group with no contribution takes its first triangle's
  plane normal, else +z.
- **"Ball bonds are unfaced" was not so.** A ball (sphere, truncated sphere) is a solid on the FACED path with
  `FaceUnknown`; the unfaced path is ports, face tints, face images and the air box's faces. A ball flattened on its pad
  splits only along its cut's rim (sphere vs flat), 32 vertices.
- **The per-draw transform is rigid**, so the realistic shader may rotate a normal by `MX.m`'s upper 3 × 3: an element's slot
  is a translation, and a preview's copies come from `C3dTransform`, orthogonal by construction (a rotation, possibly with a
  mirror, which maps an outward normal to the mirrored outward normal). The one scale (`BoxSlot`, the unit box an element
  over budget is drawn as) draws lines, which carry no shade stream.
- **A round bond wire stays faceted.** Its section is the hexagon the generator sweeps (`Em3dWireSection`); 60° between sides
  is a crease, so every wire vertex splits and the realistic view will show a hexagonal prism, not a smooth wire.
- **Duplication on the shipped examples** (scene vertices without → with the split):

  | Example | Vertices | Added |
  |---|---|---|
  | 3D Connector / Flange, Launch | 166, 699 | 0 |
  | 3D EM / Bond wire 3D | 216 → 284 | +68 (31 %) |
  | 3D EM / Package C, L, lid modes | 244, 566, 306 | +128 each (52 %, 23 %, 42 %) |
  | 3D EM / Via 3D | 1,114 | 0 |
  | 3D Package / Package | 2,108 → 2,392 | +284 (13 %) |
  | Thermal Channel vs Surface (both) | 72, 216 | 0 |
  | Thermal Die to Heatsink | 9,798 → 9,800 | +2 |
  | Thermal Output Wires / Output, Drawn Wires | 480 → 972 | +492 (103 %, the hexagonal wires) |

  The planar `.cem` examples make no 3D problem and were not counted. The added vertices cost the default view 24 bytes each
  (it draws them, identical); the shade stream is 16 bytes a vertex and only exists while the realistic view is on.

## The realistic view: the reference, the prefilter, the plan (brief-em3d-106, 2026-10-04)

- **`Look/Pbr.cs` and `Look/ToneCurve.cs` are THE reference**: GGX with α = roughness², height-correlated Smith visibility,
  Schlick Fresnel (F0 from the IOR, 0.04 at 1.5), split-sum IBL, SH9 irradiance, a clear-coat lobe taking its energy from
  the body, the key light direct (roughness floored at 0.045 for it only), the Khronos PBR Neutral curve, the sRGB encode.
  `fs_pbr` is that file in WGSL; `RealisticViewTests.Gate5` holds every shared constant equal. The diffuse term is weighted by
  1 − (F0·A + B), so a white rough dielectric in a white furnace reflects 1.000 (gate 3).
- **Base colours stay LINEAR in the table.** The brief says the shader decodes sRGB; `AppearanceColour` is already decoded
  once by the resolver (brief 105), so the table holds linear values and the shader does no decode — the result is the same.
- **The prefilter** (`Look/EnvironmentPrefilter.cs`): every environment is first drawn into a **512 × 256** lat–long image
  (a studio 2 × 2 samples a texel; an `.hdr` box-averaged down, or bilinear up). From it: the **SH9** coefficients, each basis
  function integrated in CLOSED FORM over each texel's θ-band × φ-span (exact for constant texels, so a uniform environment's
  l ≥ 1 bands vanish to rounding); **level 0**, a 256² octahedral map (+z at the centre), 2 × 2 samples a texel; levels
  **1–4** (128², 64², 32², 16², roughness 0.25/0.5/0.75/1) by GGX importance sampling with N = V = R and the **fixed
  Hammersley sequence, 128 points** (bit-reversed radical inverse), each sample reading a 2 × 2 box pyramid of level 0 at
  lod = ½·log₂(Ω_sample/Ω_texel) + 1 (filtered importance sampling). The split-sum table is **32 × 32, 512 Hammersley samples
  an entry**, at texel centres, shared by every environment. Rows run in parallel, each written by itself, so the bytes are
  the same twice and on another thread (gate 4). The five levels are the five MIPS of one texture, so an explicit-lod sample
  at roughness·4 interpolates between roughness levels.
- **Rotation is the shader's**: a lookup turns by −rotation about +z (`env_dir`), so a rotation drag recomputes nothing.
- **The key light**: a studio's key softbox direction with a direct irradiance (`StudioPreset.KeyDirect`); an `.hdr` has none
  (its light is all in the map) and takes the SH l = 1 band's dominant direction for brief 107 to shadow from.
- **Transmission** is the brief's coverage 1 − T·(1 − F) times the attenuation colour's mean, with the absorbed share glowing
  in the attenuation colour lit by the environment — for a white attenuation this IS the brief's formula; a tinted one dims
  and tints what shows through, which single-source alpha blending cannot do per channel.
- **The plan** (`Scene3DFramePlan`): `Plan` was split into `ColourDefault` (the old body, byte-for-byte), `ColourRealistic`,
  `Selection` and the pick pass. Each object is classified once per scene (`ChromeOfObject`): a chrome row, a MATERIAL (an
  appearance slot and not a wireframe — a wireframe object has no material and draws as the default view draws it), or
  neither. `Scene3DDraw.Object` was added so the gates (and `ChromeOf`) can attribute a draw; no backend reads it. A
  realistic `export` plan writes hover 0 and no selection into the uniforms and draws no outline.
- **`Scene3DShadeVertex.Slot` now carries a bit**: `StatedAlpha` (0x100) marks a vertex whose colour alpha is a STATED
  transparency (or a dimmed context part), which the realistic shader multiplies on (overview D12); a kind's default alpha
  is ignored there, since the appearance's Transmission decides. Only the realistic pipelines read the stream.
- **Visual check**: three pictures (Studio on the theme, High key on the environment, Dark on a gradient) of gold, copper,
  red plastic, a clear-coated laminate and tinted glass read as intended. A glass box shows a bright line along an edge seen
  through it at a grazing angle — what a per-object-sorted raster glass does, left as it is.

### Brief-106 review fixes (2026-10-04)

- **An array element's kind-default-translucent part vanished.** An element is drawn whole from its group's
  `OpaqueFirst..OpaqueCount`, which holds only the DEFAULT-opaque batches. A dielectric is translucent by its kind's default
  and so is outside that range, but with no `Transmission` the realistic view draws it OPAQUE — so it was skipped by the
  translucent pass too, and every element's copy but the prototype's disappeared. `ColourRealistic` now draws an element's
  translucent batches through `OpaqueRealistic` after the whole draw (which adds only those it draws opaque). Gate:
  `RealisticViewTests.Gate1_AnArrayElementsKindDefaultTranslucentPart_IsStillDrawn`. The original gate skipped element
  objects (`o.Element < 0`), because a whole draw is attributed to object 0 — which is how this got through.
- **An `.hdr` brighter than a half overflowed to +∞.** An unclipped sun easily exceeds 65,504; `(Half)` of it is +∞, and a
  linear lookup touching it gives ∞·0 = NaN. `ToHalf` clamps to the largest finite half.
- **An unreadable `.hdr`'s fallback was re-made on every Look change.** It carried Studio's key, which `KeyOf` (comparing on
  the file) never matched, so each exposure or rotation edit ran `For` again and uploaded a NEW environment object (the
  session compares by reference). Its key is now `hdr:<full path>|unread`.

## Shadows, contact shading and the ground (brief-em3d-107, 2026-10-04)

- **`Look/Shadows.cs` is the reference** for every constant the shader shares (`ShadowsOcclusionExportTests` holds them equal,
  the Poisson taps included; brief 106's constant scan counts brief 107's eight as known).
- **The shadow map.** Orthographic along the key light, a SQUARE window (square texels) over the casters' bounds in the light's
  frame, grown by `WindowMargin` and the filter's radius; depth over the casters' range with the same pad. Casters are the
  material objects drawn whose resolved Transmission is below 0.5 (owner decision D1); chrome and fields never cast. The filter
  is 16 FIXED Poisson taps, each a bilinear comparison, of radius `PenumbraReach` (0.06) × the scene's bounding radius ×
  tan(half the key softbox's mean angle): Studio 46.5°, High key 60°, Dark 35°. An `.hdr` has no direct key and casts nothing.
- **Acne without new bias literals.** The pass is drawn with `DepthBias(Behind)` (`Scene3DFramePlan.ShadowBias`), and the
  receiver's own depth is carried to each tap along its plane: a tap offset o (world, in the light's right/up) compares at
  z + g·o, g = −(n·right, n·up) / (n·forward), with |n·forward| floored at `MinSlopeCos`; the lookup point is also moved 1.5
  texels along n. Without the plane slope a tilted receiver shadows itself across the filter's width.
- **Two shader bugs found on the way.** (1) A receiver BEYOND the map's far plane (the ground under everything) has its depth
  clamped to 1, and the plane slope then pushed half the taps above 1 — compared against an empty texel (1.0) they read as
  shadow, a 50 % plateau far outside any shadow. Each tap's reference is now clamped to 1. (2) The occlusion's normal at the
  picture's border took a clamped neighbour (the pixel itself), fell back to the view direction, and the flat ground then
  occluded itself in the outer three columns (alpha 37, 25, 10 in a transparent picture). A neighbour off the picture is
  now no neighbour.
- **The occlusion kernel and its radius.** Screen-space and horizon-based: a depth prepass of the opaque `Pbr` draws (and the
  ground) writes each pixel's depth ALONG THE VIEW'S RAY into an R32 float target, and a point is rebuilt from the plan's ray
  per clip position (`PlaneGrid.Ray`, factored out of the grid's `WriteRay`) — 4 bytes a pixel, where world positions in
  RGBA32F would have been 16. `Occlusion.Directions` (8) directions, each walked in `Steps` (4) steps out to the radius,
  keep their highest horizon sine less `Bias` (0.1), fading with distance²/radius²; the radius is `RadiusFraction` (0.08) ×
  the scene CONTENT's bounding radius (not the camera's, which a drag enlarges), in world units, capped at `MaxPixels` (64).
  The directions and steps are turned per pixel by a fixed 4 × 4 interleave (never per frame), which the 4 × 4 depth-aware
  blur (`BlurReach`, 0.25 of the radius) averages away exactly. It darkens only the environment's light; the key's direct
  light is the shadow's.
- **The ground** is a full-screen pass casting each pixel's view ray at its plane, as the drawing grid does, writing its true
  depth clamped: a disc four scene radii across meets the camera's near and far planes (which bracket the scene sphere) and
  would otherwise be cut. It draws black with alpha = the light it loses: the key light's share of a level floor's light
  (key irradiance on +z against the environment's SH irradiance on +z) times the shadow, plus the rest times the occlusion,
  fading from `FadeStart` (0.35) to its rim. It lies `Drop` (1e-4) scene radii under the lowest drawn point.

## The appearance swatch, and a look asked again without the builder (brief-em3d-108, 2026-10-04)

- **`Look/AppearanceSwatch`** shades a 96 × 96 sphere with `Pbr.Shade`, THE reference, so the Materials editor's swatch is what
  the realistic view draws. The sphere is analytic: each pixel's normal is the unit sphere's (a rim pixel whose centre falls just
  outside the disc takes the nearest point on it), so there is no mesh, no rasteriser and no GPU, and a swatch draws with no 3D
  view open. It is seen orthographically from the iso camera's direction (1, −1, 1), lit by the default studio at the default
  Look, over an 8-pixel checker so transmission reads as see-through; the rim is antialiased by coverage, not by extra samples.
- **Its cost is a counter, never a timing**: `Evaluations` adds one per shaded pixel (about 6,500 of the 9,216). Gate 1 holds it
  deterministic (twice, and on another thread), holds a metal's swatch apart from a dielectric's of the same base colour, and holds
  the brightest pixel strictly falling across roughness 0, 0.25, 0.5 and 1 — compared in floats before quantisation, since at
  roughness 0 and 0.25 the key light's peak already reaches 255.
- **`Scene3DModel.AppearanceRequests`** keeps the question the builder asked the resolver for each object that owns geometry, so
  `Scene3DLooks.Restyle` can ask again through a map (a previewed material, an override) with no builder: a copy of the scene with a
  new table, the objects whose row moved copied with their new slot, and only their shade vertices rewritten (the `StatedAlpha`
  bit kept); an element takes its prototype's row. `Scene3DLooks.Intern` is the one interning rule — the builder calls it too — so
  the identity restyle gives the builder's table exactly.
- **`Scene3DModel` copies by `MemberwiseClone`** (`WithLooks`): the five fields a restyle replaces have explicit backing fields so
  the copy can set them; every other array is shared. `Scene3DObject.Copy` is the same for an object whose slot moved, so the
  scene it came from keeps its own.

## Field plots in the realistic view: Exact, Lit, Glow and opacity (brief-em3d-109, 2026-10-04)

- **Exact needed one shader edit, not none.** `fs_field`'s body moved into `field_colour` (the same operations, so the same pixels:
  gate 1 compares the realistic view's field pixels with the default view's byte for byte), and its alpha is now `1 − lk1.w`, the
  Look's `1 − FieldOpacity`. The look block is zero outside the realistic view, so the default view's alpha is still exactly 1.
- **Lit is not "add the sheen in linear light".** That is what the brief described, but it fails the brief's own gate 2: a
  saturated orange (1, 0.5, 0) under a 0.1 linear sheen turns 7.2° toward red in HSV (30° → 22.8°), and gate 2 allows 6°. The sheen
  is applied as a lift toward white in DISPLAY space, `c + k (1 − c)`, with k what adding the sheen in linear light does to a grey of
  the colour's own luminance. A grey gets exactly the linear sum; every channel only rises; the HSV hue is exactly the map's, because
  max − min and each difference scale by the same (1 − k). The sheen itself is the brief's: F0 0.04, roughness
  `Pbr.FieldSheenRoughness` (0.3), the environment through the split-sum lookup plus the key light's GGX highlight, never shadowed or
  occluded, measured as an exposed luminance. `Pbr.FieldSheen` and `Pbr.LitField` are the reference; gate 2 checks the GPU against
  them within 2/255 from above.
- **The Lit field's normals are `FieldShading.Normals`** — brief 104's `ShadingNormals` on the field's own triangles, each plot's range
  welded by exact position first (a field buffer is an unindexed triangle list). A clip-plane slice comes out with the plane's normal
  (all its triangles are coplanar), so there is no second path for it. **A Faces plot does not take the shade stream's normal**, as
  the brief asked: a painted face's triangles are the solver mesh's (`FieldFacePainter` cuts them from a region's boundary), so no
  shade-stream vertex corresponds to one; the same function on the same face gives the same smooth normal. The sign means nothing (the
  mesh's winding): `fs_field_lit` turns every normal toward the viewer. They are made only when a frame draws Lit, once per field
  geometry version (`Viewer3DSession.FieldNormalUploads`; `FieldShading.Built` counts the work), on the render thread.
- **Glow is CPU-side.** `RealisticLook.SurfaceExposure` (2^(EV + `GlowDim`), −2.5 EV) goes into the look block's exposure, which
  `fs_pbr` and the environment backdrop read and `fs_field` never does; a theme, solid or gradient background is a DISPLAY colour
  and is darkened by decode × 2^GlowDim × encode (`RealisticLook.Backdrop`). Chrome brought back with a `Show…` key and reference
  images are drawn by the default view's pipelines and are not dimmed.
- **Below 100 % a covered object is drawn.** A Surfaces or Faces plot normally replaces the faces it is painted on
  (`FieldCovered`), which would leave nothing to show through; `Viewer3DViewState.FieldShowsThrough` (realistic, opacity < 1) turns
  that off, so the copper is drawn under a 50 % field (gate 4) and casts its shadow as usual.
- **The indicator is one spelling.** `RealisticLook.FieldIndicator` / `FieldIndicatorText` compose `Lit Fields`, `Blended Fields` and
  `Lit, Blended Fields` from three constants; the live overlay, `FieldPicture.Paint` (Export Picture, Copy) and the CLI schema's text
  all ask there, and gate 5 scans `src` for a spelled-out label. It is painted at `IndicatorSize` 10 in the legend ink at alpha
  `IndicatorAlpha` 160, `IndicatorGap` 4 under the stack, right-aligned with it, or alone in the top right corner.
- **What ran.** Metal: every pixel gate (1–4, 6). D3D11 and Vulkan were compiled only; their two new pipelines (FieldBlend and
  FieldLit, the normals at the shade stream's slot) are unverified at runtime, as briefs 62, 101, 106 and 107 recorded. `render --field
  --look realistic` does not exist yet (brief 110), so its half of gate 5 waits for that brief; the label it must carry is already the
  one function it will call.

## The realistic view on the CPU: `RealisticPicture` and the shared rasteriser (brief-em3d-110, 2026-10-04)

`render x.c3d --look realistic` draws through `Scene3D/Look/RealisticPicture.cs`.

- **It executes a `Scene3DFramePlan`, not a scene.** The GPU backends read the same plan. The CPU does what
  `MetalViewer3DBackend.RenderInto` does with it:
  - the shadow pass (`ShadowDraws` at `ShadowSize`, with the shadow bias);
  - the occlusion: the `Pbr` draws' and the ground's ray distance, then the horizon pass and its blur;
  - the colour draws in order.

  Every decision (visibility, pipelines, translucent sort, shadow window, occlusion reach, ground, backdrop ray) is
  the plan's, read back from its uniform block at the offsets the plan writes. `Pbr.cs` shades each fragment
  (`Pbr.Shade`, `FieldSheen`, `LitField`). The WGSL's own arithmetic is transcribed line for line: `shadow_vis`,
  `fs_ao`/`fs_ao_blur`, `ground_hit`, `fs_backdrop`, `field_colour`, `fs_color`.
- **`Pbr.Radiance`/`Shade` take `occlusion` and `shadow`** (default 1, which changes nothing). `fs_pbr` had always
  multiplied them in; the C# reference had not.
- **The targets are quantised as the GPU's are.** The colour target rounds to 1/255 on every write, and blends read
  the 8-bit value back. The occlusion's raw and blurred targets are R8. Depth is float32, with the polygon offset
  emulated per triangle: the constant times 2^(exponent(max z) − 23), plus the slope times the steepest depth change
  per pixel, then clamped. Without that offset a field lying on a face would fight it.
- **A run of opaque draws (`Pbr`, `Opaque`, `Field`) is resolved by depth first and shaded once per pixel.** This is
  exact: those pipelines neither read nor blend the target, and a fragment the shader discards (the section plane,
  `fs_color`'s alpha-0 wireframe face) is decided before the depth write, as the GPU decides it. Blended draws run
  fragment by fragment in order.
- **Front faces** are counter-clockwise in NDC (y up), which is negative area in sample space. **The near plane** is
  clipped in clip space (z ≥ 0). A clipped piece keeps its corners as weights over the original triangle, so
  attributes interpolate through it; attributes are perspective-corrected with 1/w.
- **`SoftwareRaster` (`Scene3D/SoftwareRaster.cs`) is the one rasteriser.** It was factored out of
  `Em3dSurfaceField` (brief 89), which keeps its inclusive edge rule byte for byte. The recorded hash of a Faces plot
  picture made before the brief still matches. `RealisticPicture` uses its `TopLeft` rule: with brief 89's inclusive
  rule a translucent surface would be blended twice along its own triangles' seams. Rows are bounded so a band of
  rows can rasterise on its own thread. Each pixel belongs to one band, which visits the draws in the plan's order,
  so the bytes do not depend on the thread count (`CRF_RASTER_THREADS`). Counters are summed per band with integer
  adds.
- **The tolerance, and why the edge band is excluded.** `RealisticPictureTests` compares the Metal picture with
  this one on the same plan. A pixel is excluded when any of its 8 neighbours shows a different surface
  (`RealisticFrame.Surface`: an object id, `FieldSurface`, or 0). At a silhouette the two rasterisers' coverage
  rules differ by a sample, and one side is background. **Measured on the first run:** 100.00 % of the other
  ~168,000 pixels were within 4/255, and every field pixel within 1/255 (worst 1), for Exact orthographic and Lit
  perspective alike. The worst excluded pixels differ by 84–118, all on silhouettes. The gate allows 99 %.
- **Supersampling and the picture size** go through `PictureResample.FactorFor`, now shared with Export Picture
  (the view model's `SupersampleFor` calls it).
- **Cost:** Debug build, 1600×1200 at 2× (3200×2400 drawn), 4096² shadow map: ~7–8 s wall clock and ~60 s of CPU
  on the shipped Package and Launch examples. No timing test holds this; counters do.
- **One place for the picture camera:** `Camera3D.SetPictureCamera`, which Go to Picture View and `render` both
  call. **One place for the Theme background:** `Viewer3DViewState.ThemeBackground`. `FieldPictureShot.Typeface`
  lets `render` set the legend in the embedded face.

## glTF export: `GltfExport` and `GlbWriter` (brief-em3d-111, 2026-10-04)

- **Why it lives in `src/Render`.** It writes what the view draws: the triangles, face splits, shading normals
  (`ShadeVertices`, made by `ShadingNormals`) and appearance slots of a `Scene3DModel`. All of that is here, and
  `src/Design` cannot reference `src/Render`. It draws nothing. File ▸ Export ▸ glTF… and `convert … .glb` both call
  `GltfExport.Build`. `GlbWriter` is only the container (a 4-byte-aligned JSON chunk and BIN chunk, one buffer view
  per accessor).
- **Content** is decided by `GltfExport.Exported`: shown, not a chrome row (`Scene3DFramePlan.ChromeOfObject`), not a
  tint, not a wireframe (no material), not the dimmed parent of a pushed-in child, and with an appearance slot. The
  view's own chrome table decides, so nothing restates what counts as air, a port or a boundary. Headlessly, what is
  shown is `DocumentVisibility`: each object's kind default, less every object the document hides (found through its
  provenance, as `ApplyHiddenFlags` finds it in the editor).
- **Frames.** Meshes are scene-local (with Assembly, in their instance's frame). The root node carries the origin's
  translation in double and the Z-up → Y-up turn, so positions stay small floats as the view keeps them.
- **Assembly mesh sharing compares within a tolerance, not by rounding.** STEP rounds to 0.1 nm in DBU doubles, but a
  scene's vertices are single precision, and an array element drawn as prototype + offset differs from an owned copy
  by float noise. In the first run, 0.1 nm rounding split three instances into two meshes. Now meshes with the same
  structure (cell, object, materials, triangles) are shared when every position agrees within a millionth of the
  scene's size and every normal within 1e-3.
- **Materials are keyed by (slot, name, alpha, thickness, two-sided)**, not by slot alone. The name is the
  material's, with `+override` where the resolver, asked again without the document's statement, gives different
  values. A stated transparency (beyond the brief) is written as `alphaMode: BLEND`, since the view draws it. A sheet
  is `doubleSided`. The volume thickness is the object's smallest bounding-box extent, so it is per object.
  Extensions are written only where a value differs from glTF's default, in `extensionsUsed` and never
  `extensionsRequired`.
- **The field subdivision rule (R-em3d111-2c).** The view colours each pixel from the value interpolated across the
  triangle. A vertex colour is interpolated colour, so on a coarse mesh the two differ. Each field triangle is split
  into four at its edge midpoints until no edge's endpoint colour positions (`FieldColorScale.Position`, 0 to 1)
  differ by more than 1/16, at most four levels (256 triangles from one). Midpoints interpolate the CHANNELS
  (real and imaginary parts), as the GPU interpolates them, so a new vertex's colour is exactly the fragment's
  colour there. A triangle still too wide at the cap is counted (`FieldCapped`), which happens on a dB scale near its
  floor, and the note says so. Colours are the map's sRGB bytes decoded to linear (`LinearColour`).
- **A Faces plot stands in for no object.** It paints a face over its object, so the object stays in the file. Only
  what the plot's `Covered` set names is left out, as the view leaves it out.
- **Only Surfaces and Faces plots are offered.** A ClipPlane plot is a section through the volume, and the CLI path
  resolves plots through `render --field`, which builds a section a different way.
- **Camera.** A node under the root, in the scene-local frame. Its rotation maps glTF's camera axes (x right, y up,
  looking down −z) onto the view's (`CameraRotation`, tested). An orthographic camera's `znear` is clamped to 0,
  since glTF does not allow a negative one.
- **Test note:** in .NET 10, `Vector3.Transform(v, q)` with the file's −90° quaternion read as floats moved x by
  ~2e-8 of a 1.5 mm coordinate. The orientation test applies the file's quaternion in double instead. The file's
  positions were exact.

## Looking down a plated via showed the substrate, not the plating (2026-10-04)

- **Symptom.** In the 3D view, a plated through-hole seen from above showed the laminate across the hole; the copper
  barrel's wall could not be seen.
- **Cause.** `Scene3DBores` carved a dielectric only for a BARE bore (a non-plated hole). The 2026-09-30 reasoning was
  that a plated via's barrel hides the substrate, and it does hide the slab's sides. But the slab's top face spans the
  inside of the tube, and from above that face is all you see.
- **Fix.** A plated via's bore carves the dielectric too, at the barrel's OUTSIDE radius (`Bore.Outer`), which the tube
  then fills. Not at the bore's own radius: there the slab's hole wall would coincide with the tube's inner wall, one
  surface drawn twice in two colours. View only, as before; the solver's problem is untouched.
- **Cost.** One ring per via in each carved slab's caps. `Em3dPolygonTriangulation`'s z-order index keeps the ear
  clipping from going quadratic on a slab with many holes.
- Gate: `PlatedViaAndPerspectiveTests.ANonPlatedHole_IsDrawnAsAHoleInTheSubstrate_AndABoxSlabKeepsItsFaces`, now also
  asserting the plated case.

## The Data Display table printed a category axis' positions, not its labels (2026-10-05)

An X column showed `Axis.Values` only, so a labelled category axis — a wBond's `wire array` (`W1:G1`), a DC `node`,
`branch` or `opvar` — read 0.000, 1.000 with nothing saying which row was which (the `node` special case only made them
integers). The trace already carried the labels (`TraceExpression` hands `XAxis.Labels` to `SetCubeData`; only the marker
readout used them). `TableColumn.XLabels` now maps each X value to its label when the axis states one per value, all
distinct, and is not a frequency, `harmonic` or `mixIndex` axis — a two-tone product folds onto its frequency, and the
number is the point there. Plots' X ticks are unchanged. Gate: `TableColumnPlanTests.ALabelledCategoryAxis_ShowsItsLabels`.

A bare name on a swept per-category cube — `WireTemp[IDC, wire array]` — took the positional two-axis rule (last = X,
earlier = family): it plotted along the array names with one curve per sweep point, a table of 101 one-number columns.
`CubeTraceSpecParser` now swaps the two when the last is a CATEGORY axis (a label per entry, not freq/harmonic/mixIndex,
no Hz unit) and the earlier is not; the family path's X column carries labels too. A trace saved with the old roles keeps
them (the stored slice wins) until it is re-entered. Gate: `BareMeasurementNameTests.BareSweptCategoryCube_SweepIsX_CategoriesAreTheCurves`.

## Loadpull contours default to thin-plate, not multiquadric (2026-10-07)

The owner found thin-plate draws visibly smoother contours than multiquadric on every loadpull dataset seen to date, so
`ContourDefaults.Kernel` is now `RbfKernel.ThinPlate`. That is the one constant the runtime `ContourData`, the persisted
`ContourTraceConfig` and the trace card's view model all read. Smoothing 0.1 and ε 0.5 are unchanged. Thin-plate has no
shape parameter, so ε only applies after a switch to multiquadric or Gaussian, and with its linear tail smoothing 0.1
overshoots by +0.031 dB on the reference bowl (`Rbf2D.RequiresPolynomialTail`). **A saved `.cdd` is not migrated**:
`DataDisplayJson` writes every property, so a trace saved as `"InterpKernel": "Multiquadric"` stays multiquadric. Only a
new contour trace, or a file with no kernel field, picks up the new default. The MXP/MXE markers come out of the same fit,
so on a new trace they can land on a slightly different grid point than they did before. `Rbf2D`'s own parameter default
and harmonicaRF's `ContourKernel` stay multiquadric; neither is the Data Display. Gate:
`DataDisplayRound12Tests.TheContourRbfDefaultsAreSmooth0Point1AndEpsilon0Point5`.

## A trace expression calling an axis function is evaluated whole (brief-yield-5, 2026-10-08)

`TraceExpression` evaluates per X sample with each reference bound to one scalar — which cannot work for
`mean_over`, `histogram`, `cdf` and the rest (`Evaluator.AxisFunctions`): each reads a whole axis. An
expression that calls one now takes `TryEvaluateWhole`: each reference is bound as the CUBE its slice
leaves (any number of kept axes; a bare name is the whole cube) and the expression is evaluated once.
The result must keep exactly one axis, which is the trace's X — a histogram's `bin`, the `freq` a mean
over trials leaves. A rank-0 result or one with several axes is refused with the axes named, because a
trace drawn from either would be an empty or arbitrary picture.

## A stroke-font label is drawn and measured from `StrokeText` (brief-silkscreen-stroke-font, 2026-10-08)

`DrawLabelText` strokes a stroke label's centre lines (round caps and joins, at its pen) and `MeasureLabelWorldBbox`
returns `StrokeText.Bounds` for it — the same geometry a Gerber export writes, so the selection box, the hit test and
`DocumentExtents` follow with no change of their own. Only a Sans label still reaches Skia's glyph metrics.

**The technology now reaches the label draw.** A default pen is never below a plain minimum-width rule on the label's
layer, so the pen depends on the technology; `DrawLayer`, `DrawDesignators`, `DrawPortGlyphs` and `PortNameKnockout`
take it (the ghost path already had `ghostTech`). `MeasureLabelWorldBbox` takes it optionally; its callers that have no
technology measure with the plain `Height / 6.5` pen, which differs only on a layer whose rule raises the pen.

**A port's name knockout for a stroke label** is its centre lines widened to the pen plus the knockout gap on each
side, not glyph outlines — the hole is exactly the ink plus the gap.

**Two label clones in the renderer copy field by field** (the sub-pixel height floor for a committed label and for the
ghost); both carry `Font` and `StrokeWidth` now. A clone that dropped `Font` would draw a Sans label in the stroke font
only when it was small enough to need the floor.

## Yield display review fixes (brief-yield-16, 2026-10-08)

- **Pass and fail were one red band (R-ya16-2).** A pass drew in the trace's colour at 35 % alpha, and the first wheel
  colour is pure red — next to the limit red. `RenderTheme.PassColor` is a neutral grey per theme and
  `TrialRenderer.ElementPaint` uses it for every pass, whatever the trace colour.
- **A run at each corner (R-ya16-3).** Its cubes are `[corner, trial, …]`. `SpecLineResolve.GoalsOf` reads the LAST
  axis's labels, so a stacked spec line is found. `TraceStatistics.Build` refuses a statistic while a `corner` axis is
  free (`CornerRefusal`) instead of `Operand` pinning it at index 0, and a yield sensitivity refuses such a result.
  The yield display, the statistics table (its toolbar tooltip now says why) and `ResultContributions`
  (`yield.contrib.corner-stacked`) refuse it by name — chosen over building per corner, which would have meant a
  family, a histogram and a table per corner per goal for a display whose job is one run. `ResultContributions.
  StackedCorners` is the one detector, reading `trials.status`.
- **A measurement family drew no nominal (R-ya16-9).** The trace spells it bare (`S21dB`), the result stores
  `nominal.measurements.S21dB`. `TrialResolve.NominalOf` qualifies a bare name only when the measurements group holds
  it AND that nominal exists, so a default-group cube keeps `nominal.<name>`.
- **Contribution shares above 100 % (R-ya16-10, D-a).** `ResultContributions.Store` adds `contrib:<g>:shown` (clamped)
  and builds `:cumulative` from it, renormalised to end at 1; `contrib:<g>` keeps β·r. The Pareto's bars read
  `:shown`. `Contributor.ShownShare`/`ShownCumulative` are the one clamp the Pareto, the Yield panel and the CLI share.
- **A histogram's spec line could be framed out (R-ya16-12).** `Plot.AutoscaleCore` unions every vertical spec line's
  X into the trace's box; it only runs for an autoscaled axis, so a fixed range stays. Beyond a fixed range the line
  is an arrow at the edge with its label (`StatisticsRenderer.DrawSpecLines`).
- **Per-trial goal values had no unit (R-ya16-13).** None of the shipped goals writes a unit on its LIMIT (`-1`, not
  `-1 dB`), so the limit alone was not enough: `GoalResiduals.ValueUnit` takes the limit's unit, else dB for an
  expression that is one whole `dB`/`dB20`/`dB10` call (dBm for `dBm`), else a named measurement's unit.
  `StatisticalDataSet` writes it on `goal:<g>:worst` and `:margin`; the histogram's axis then reads `bin (dB)`.
  The yield-sensitivity COMPANION's `bin` label is unchanged — its operand `stat + 0*pass` drops the stat's unit in
  the evaluator's sum, which is the expression engine's rule and outside this brief.
- **The statistics table clipped its own headers.** `StatisticsTablePreset` gave every column 90 px and the row
  column 160, and its headers are cube spellings (`statistics.sigma_to_limit`). Each column is now sized by
  `TableRenderer.FitWidth` — the measurement `CalcFitWidth` (the auto-fit) makes, extracted so a preset with the
  text but no resolved plot can use it — over its header and its formatted values, and the row column over the
  quantity names; the shipped `BandpassYield.yield.cdd` is regenerated from the preset (only those widths moved).

## The yield display of a run at each corner: one tab per corner (2026-10-09)

Supersedes the R-ya16-3 refusal above for the yield display (the Data Display's own statistics-table button and
`ResultContributions.Of` still refuse a stacked result, since neither has a corner to pick). The refusal reached the
GUI as a Messages line the user did not see, after a button that looked live. Now `YieldDisplayPreset.Build` writes
one tab per corner, in the run's order, opening on the lowest `yield.yield`; each tab is `Compose(ds, src, k)`,
which decides what to plot on `ResultContributions.CornerOf(ds, k)` and pins `corner` by name on every trace.
- **A pinned corner broke three readers silently, each the same way:** the pass cube is `[corner, trial]`, so
  `TrialResolve`'s colour-by failed its rank-1 check and drew nothing coloured; `TraceStatistics.Build` refused a
  yield sensitivity outright; and `TraceGoalReader` read the corner index into the goal expression, so no spec line
  matched a corner's family. The first reduces by the trace's pins, the second slices at its corner, the third
  drops a pinned corner (a goal holds at every corner).
- **A Table column's header is its whole expression,** so twelve columns each read `statistics.mean["t85_Vdd3p0", :]`
  and truncated to noise. `TableRenderer.SharedPinnedCorner` says a corner every picker column shares once, on the X
  column; the draw path now uses `BuildColumns`' header rather than recomputing `CubeShorthand`.
- **Two files, not one:** a plain yield run and a run at each corner write the same `.yield.npy`, so the corner
  display is `<design>.yield.corners.cdd` — each display's traces fit one shape of the file.
- **A corner run without MC wrote an EMPTY display** (no `trials` group, `Compose` returned nothing, the file was
  written anyway). The panel now disables the button with the reason, and the workspace refuses by name as a backstop.
Gate: `tests/Ui.Tests/Statistics/CornerYieldDisplayTests.cs`.

## A material-less STEP part was invisible in the realistic view, but for its hover highlight (2026-10-09)

Reported against an imported STEP part with no material: it seemed to vanish and reappear while zooming and panning. The
camera, depth range and geometry were all sound (a scratch harness compared the part's stored bounds against its
tessellation: identical). What came and went was the **hover highlight**: wheel-zoom and pan move the part under and off
the cursor. With hover gone, the part was invisible, and only in the realistic view.

A material-less object is a WIREFRAME (3D editor bugs round 1): faces at alpha 0, its edges as `SceneLines` in the ink.
The realistic view classified every `SceneLines` batch outside a chrome row as the **Edges** row, which `ShowEdges`
shows and which is OFF by default. So the realistic view hid the wireframe's entire drawing. This is round 2's "only its
hover highlight showed" again, in the second colour pass. `ColourRealistic` now always draws a wireframe object's
line batches, and `ChromeOf` no longer calls them Edges.

**Consequence worth knowing:** those were the only scene lines outside a chrome row (a solid's feature edges are
`EdgeBatches`, drawn only as the hover/selection outline). So `ShowEdges` currently brings back nothing.
`RealisticViewTests.Gate1` no longer requires an Edges draw from its fixture. It still asserts that the key adds
exactly the Edges row's draws, which is now none, and that a wireframe's lines are in the bare realistic frame.

## A STEP part's lead ends flickered on the mould's side wall (2026-10-09)

On a saw-cut DFN, the lead frame is cut flush with the mould compound, so each lead end lies exactly in the
mould's side wall. In the 3D editor, those copper faces z-fought with the mould. **Cause:** round 3's "metal wins
over dielectric" tie (`Scene3DFramePlan.TieOf`) keyed on `Scene3DKind.Dielectric`. But `C3dElaborator` gives every
.c3d object that is neither a conductor nor air the origin `Em3dObjectKind.Body`, so a .c3d dielectric reaches the
scene as `Scene3DKind.Body` and took the metal's tie (None). Two equal ties at one plane fight. The round-3 and
round-9 gates built their problems with no origins, so this path was never exercised. A `Body` now takes its tie
from its `Role`: a dielectric or air pushes away from the eye, as a `Dielectric` does. Picking uses the same
`TieOf`, so a click on the lead end finds the copper. Gate:
`MetalPrecedenceTests.TheViewport_GivesALeadEndOverAMouldBodysSideWall`.

## A STEP piece's top edge was not drawn (2026-10-09)

The owner reported that the hover/selection outline of an imported DFN lead had no line along its top. **Cause:** a solid's
feature edges were found in its display tessellation: an edge was drawn only where two triangles of different faces shared
both end points, welded by exact position. The kernel meshes each face separately. On this piece the vendor B-rep is
degenerate. The lead's upright leg has zero thickness: its two side faces lie in one plane, and its top face is a line with
no area. That face meshed to no triangles, and the two coincident faces split the top edge at different points. So no two
triangles met there, and the edge was dropped. The kernel itself reported both top edges (`face3|face5`, `face3|face7`).
**Fix:** `Scene3DBuilder.Accumulator.KernelEdges`. An `Em3dShapeSolid` drawn from its own `Display` mesh now draws the
B-rep's edges, each with its two faces packed as before. A seam (one face on both sides) is skipped, as the mesh path
skipped it. A hand-built shape with no edges keeps the mesh path. On the owner's piece this went from 13 edges to 16,
including the top. Gate: `KernelEdgeTests`: a box whose top face has no triangles draws all twelve edges. Without the fix
it draws eight.

## Realistic + perspective: streaks over a board's copper that moved with the camera (owner report, 2026-10-12)

An imported 50 mm board, Realistic view, perspective: dark streaks across the top copper that shifted while orbiting
down; orthographic was clean. **Cause:** the live view sizes the depth range from the scene's BOUNDS, which include
the EM air box (here a sphere of radius 103 mm around a board 0.58 mm thick). A close look at the board puts the eye
inside that sphere, and `Camera3D.DepthRange` then took near = far × 1e-4 (0.018 mm). A standard-Z float buffer
resolves about z² · 2⁻²⁴ / near — tens of µm across the board — which is the 35 µm between the copper's top and the
substrate under it. The occlusion prepass flipped between the two, and the horizon pass read that as relief and
darkened it. Orthographic depth is linear, so it never showed there. Round 9 noted this limit and left it.

**Fix:** `Camera3D.NearFloor`, set by `Scene3DFramePlan.DepthCamera` on the copy a frame is drawn with: the eye's
distance to the box of what that frame DRAWS (visible objects, minus chrome the realistic Look leaves out — the air
box — plus array elements and a drag's preview copies), times the cosine of the frustum's corner-ray angle, times
0.9. No drawn point can be nearer along the view than that. On the owner's framing the near plane went from 0.018 mm
to 38 mm. A frame showing a solver overlay (mesh, mesh section, grid) keeps the sphere's rule, since those reach the
air box's walls; so does a default-view frame that draws the air box, which still includes it in the box. Verified
on the Metal device with the owner's own document opened through `C3dEditorViewModel`: the streaks reproduced at the
owner's framing before the fix and were gone after. Gate:
`EditorRound2ViewportTests.Perspective_InsideTheAirBoxSphere_TheNearPlaneIsTheDrawnBoardsDistance`.

Reversed-Z would remove the limit for every case, including an eye inside a drawn air box; it touches all three
backends and the CPU mirror, so it was not done here.

## A click on a trace beside a ground pour answered with the pour (designer round 21, 2026-10-18)

On a Gerber-imported board no end of the input trace could be chosen for a port: every click on the
trace placed the port on the ground pour's clearance edge beside it. The trace's end was covered by a
placed footprint pad, so `LayoutConductorLookup.MergedWithOverlapping` unioned the overlapping copper —
and its operand filter is a BOX test, so the pour (whose box is the whole board) went into that union
too. The union then had fewer pieces than operands, and the piece answered was the first one whose box
enclosed the click and the trace's box. The pour's box always does; whenever Clipper emitted the pour
first, the click answered with the pour, and `NearestBoundaryPoint` snapped onto the pour's outline.
Measured on the reported board: every point along the trace centre line answered with the 1,320-vertex
pour; afterwards the trace (merged with its pad) and its end face.

The piece answered must now also CONTAIN the point (edge inclusive, holes respected —
`ContainsEdgeInclusive`). The same helper replaced `InstanceCopperAt`'s own test, which counted a point
inside a hole as on the copper. Gate: `DesignerRound21PortEdgeTests` (the pour's clearance is a notch
reaching the board edge, as on the board; with a hole instead, Clipper happens to emit the trace first
and the old code passed).

**A second cause on the same board, at the trace's other end:** the smallest-shape rule considered
every VISIBLE layer, so the solder-mask opening over the device pin (and elsewhere a footprint's
courtyard) — smaller than the trace under it — won the click and the port seated on the mask layer.
A gesture (`onLayer` null) now considers only the drawing layers the stackup binds to a conductor or
via entry; a technology with no such bindings keeps every visible layer, and a committed port
(`onLayer` given) is untouched. Gate: `DesignerRound21PortEdgeTests.ASolderMaskOpeningOverTheEnd_…`.

## `SchematicOverlay.Empty` had null selection sets (2026-10-19)

`Empty = new()` was declared at the top of the record, above the two static empty sets its property initialisers
read (`HashSetEmpty`, `EmptySegments`). Static initialisers run in textual order, so `Empty` was built while both were
still null, and `DrawOverlay` threw a `NullReferenceException` on `SelectedComponentIds.Count`. Nothing showed it for
as long as only the editor drew overlays: its view model rebuilds the overlay with every set filled in before the
first frame. The Import Schematic dialog's preview, a `SchematicCanvas` with no edit session, drew its zoom box as
`Empty with { RubberBand = … }` and threw on every frame of the drag. `Empty` is now declared last. Gate:
`ImportSchematicPreviewNavigationTests.TheEmptyOverlay_DrawsARubberBand`.

## One far-out sample erased a trace's whole line in the window (2026-10-20)

A power sweep run before MC-25 had one unconverged point where `DE` read −9.9e11. In the window the trace's markers drew
and its line did not draw at all; the `render` verb, on the CPU raster, drew the same `.cdd` correctly. Skia's GPU
canvas gives up on a path with a coordinate that far out, and the markers survived because each is its own small path.
`TraceRenderer.ClippedPolyline` now clips every segment to a box four canvas sizes wide before it reaches the path
(Liang–Barsky, in double precision), so the part of each segment inside the plot is drawn exactly and nothing beyond
the box reaches Skia. Gate: `OutOfRangeLineTests` checks the path's bounds, because a CPU-raster pixel test passes
without the fix.

## A trace colour now knows the canvas it is drawn on (2026-10-21)

A `.cdd` stores an INDEX into `TraceProperties.ColorLUT`, and Black (index 0), Indigo, Purple, Teal and
Green read on the white canvas and disappeared on the dark one. `RenderTheme.TraceColor` mirrors
a colour darker than mid lightness about mid (`l -> 100 - l`, hue and saturation kept) when the theme's
own BACKGROUND is dark. Dark is decided from the background's Rec. 601 luminance rather than the
`DarkMode` flag, so a custom background gets the same treatment, the test `LayoutRenderer.Snap`'s snap
marker already uses. Alpha 0 is returned as is: the palette's "Clear" is `#00FFFFFF` and would otherwise
mirror white to black.

`RenderTheme.ToSKColor` became an INSTANCE method for this change, not for style. Every trace colour in the
renderers already went through it, so naming the theme at each of the 24 call sites is what makes a site
nobody thought about fail to compile; a static version rendered unmapped, silently. The transform must stay
in that one function. **SkiaSharp's HSL triple is degrees and 0..100, not 0..1**: the first build wrote
`FromHsl(h, s, 1 - l)` and turned black into `#020202` instead of white, which the gate caught on its first
run.

The LIGHT theme returns every stored colour unchanged, so no `.cdd` and no light render moved. Contour
colours (`cd.LineColor`, colormap derived) deliberately do not pass through: `ContourRenderer` enforces its
own luminance guard. Gate: `tests/Ui.Tests/DataDisplay/DarkTraceColourTests.cs`, whose render-level case
is the one a missed call site fails.

**Follow-up, same day: three adjustments on merge.**
- **The inspector's colour swatches paint the DRAWN colour** (`ColorItem` holds a light and a dark brush and
  follows `ActualThemeVariant`). The first version kept the stored colour on the grounds that a swatch should
  agree with the file, but a `.cdd` stores the palette INDEX, which a theme-dependent swatch does not
  contradict; a black swatch beside a trace drawn white was the one place the two disagreed.
- **Only a palette entry adapts.** A colour set directly (`LineColorStorage` — Harmonica's per-theme trace
  colours, the thermal plot's) draws exactly; those were already chosen for their canvas.
- **HSL lightness is not perceived lightness**, so pure Blue (HSL 50) escaped the mirror at 1.9:1 on the dark
  canvas, as hard to see as Black was. After the mirror a palette colour is lightened, hue and saturation
  kept, until it clears 3:1 (WCAG's floor for graphical objects) against the background.

Four `Harmonica` tests fail on this machine with the transform disabled as well
(`HarmonicaBackdropCacheTests` x3, `PowerSweepPanel_TheY2Label_...`), so they are NOT this change: the
probe that disables the transform leaves them red.

## brief-emi-7 — EMerge's fields through the Palace reader (2026-10-22)

`FieldRun.OpenEmerge` is `OpenPalace`'s body (now the shared `OpenParaview`) with the solver "EMerge", metres, and
`FieldDrive.Emerge` — Γ from the port voltages the driver measured in each step's own solve, Z0 from the run's kept document
or problem.json, scale 1 (the driver already wrote Palace's incident power). No reader changed: the driver writes the
layout `VtuReader` reads. `FieldPlotResolver.RunDirectories`/`Discover` take an optional solver (the plot's, by display
name) so a comparison's FEM legs are each drawable; callers in the 3D view, the editor and `render --field` pass
`request.Solver`. Detail in `src/Design/RESOLVED.md` "EMI-7".

## A seam down a kernel cylinder in the realistic view (2026-10-22)

A filleted pin drawn by the geometry kernel showed a line along its length, through the fillet, in the
realistic view only. **The kernel tessellates each face on its own and repeats the vertices along a
periodic face's parametric seam** (a cylinder's side, a torus fillet), so the two columns of triangles
either side of the seam shared no vertex. `ShadingNormals` smooths only across SHARED edges, so it
treated the seam as an open edge and gave each side its one facet's normal: half a facet off radial
each way, an ~11.6° jump at the default deflection. The default view takes its normal from
screen-space derivatives and never showed it.

**Fix: the faced path in `Scene3DBuilder.Object` keys its scene vertex by the corner WELDED BY
POSITION** (the `welded` map it already built for the feature edges) plus the face. The seam's
duplicates are bit-identical, so exact welding finds them. Faces still never share a vertex, so an
edge between faces stays sharp. Pole duplicates on a kernel sphere now weld into one vertex too, which
is what a pole's normal wants. Gate: `EdgesFilletsChamfersTests.AKernelCylindersSeam_ShadesSmooth`.

**Then the edge where the fillet meets the cylinder.** With the seam welded, a second line remained at the
fillet's boundary. That edge was sharp by rule: faces never share a vertex, so each face's boundary normal
came from its own triangles, and the fillet's first facets lean toward the axis while the side's stay
radial — a jump of about a facet's half-angle at a tangent join. **`ShadingNormals.Compute` now takes an
optional `weld`** (each vertex's representative at its point). It groups a POINT's corners, and the 30°
crease rule decides whether two faces join there, exactly as it decides inside a face. Each face keeps its
own vertex (face picking still reads it) and takes the group's normal. `Scene3DBuilder.Shade` passes the
weld by position whenever `SplitShadingCreases` is on, so `split: false` is unchanged. This matches
`Em3dOutline`, which already welds by position before applying the same 30° rule to its feature edges.
**Trade-offs:** a face boundary that turns by less than 30° now shades smooth, because the mesh cannot tell
a tangent join from a shallow real crease (the kernel reports no edge continuity). The boundary ring's
normal is the average of both faces' facets, so it leans a few degrees toward the coarser face's facets
(6.4° on the gate's pin) — continuous, where it used to jump.
## Scalar Table columns vanished on reopen; one-point traces drew nothing (2026-10-21)

A rank-0 cube (an envelope run's Pout, gain, efficiency) saves an EMPTY `CubeSlice`. `PlotConfigLoader`
treated a trace as cube-bound only when the slice was non-empty, so every scalar Table column was dropped as
the display opened. Loading it as null instead resolved to `<invalid>`, since null means "no slice chosen".
Now a non-null `CubeName` alone makes a trace cube-bound, and its slice loads as an empty array.
A one-point trace, such as a single-carrier ACLR against its one carrier, had two defects:
- Autoscale skipped its zero-size bounding box, so the window kept a stale range.
- A line through one point is a lone MoveTo, so nothing drew.

`AutoscaleCore` now keeps a zero box when the trace has a finite point (`Trace.HasFinitePoint`). The line
renderer now draws a dot in the line's colour when exactly one point is plottable and markers are off.
Gate: `ScalarAndSinglePointTraceTests`.

## A dense trace froze the window: draw only what is on the canvas (2026-10-21)

An envelope run keeps every step: 786,000 points per probe for 100 us at 0.127 ns. A Data Display plot of
50 ns of it built and stroked a path through all of them on every redraw, about 0.7 s a frame on the UI
thread. The display and the main window both stopped responding, while the other plots (a few thousand
points) stayed quick. The same plot wrote a 24 MB SVG headlessly.

Traces above 20,000 points now go through `TraceRenderer.AppendLargePolyline`:
- Off-canvas points are dropped, except the neighbour on each side of every visible stretch. This is
  pixel-exact: 0 of 484,704 pixels differ, the redraw takes 0.05 s, and the SVG is 24 KB.
- Only when more than 200,000 points remain ON the canvas (a whole record zoomed out) do consecutive
  points in one pixel column also collapse to first/min/max/last.

The collapse is not pixel-exact: on a 65,000-point spectrum it moved anti-aliased steep edges by up to a
pixel (370 pixels). That is why it engages only where the full path would stall, and a spectrum keeps its
original pixels. Traces under 20,000 points take the original per-point path unchanged.

The single-point dot added earlier the same day now uses its own plain fill paint. `DashSafeShapesTests`
caught the dash-capable stroke paint reaching `DrawCircle`.

## SVG text drew in a substituted font outside circuitRF: `--outline-text` (2026-10-23)

An SVG written by `render` or `plot` and opened in a browser or another tool showed its labels in a serif
whenever IBM Plex Sans was not installed there. Skia's SVG device writes live `<text font-family="IBM Plex
Sans">`, and a viewer without the family substitutes one silently, so the file looked right only on machines
that happened to have the font. Reproduced in WebKit on a Mac without Plex installed.

- **No Skia flag to use.** Skia's SVG canvas can convert text to paths (`kConvertTextToPaths_Flag`), but
  SkiaSharp 3.119.4 exposes only `SKSvgCanvas.Create(bounds, stream)`, and the native library exports only the
  flagless `sk_svgcanvas_create_with_stream` (checked with `nm`). So `SvgTextOutliner` converts the finished
  document instead, as `SvgFontNormalizer` repairs it.
- **Embedding the font (`@font-face` with the TTF inline) was tried first and fixes browsers**, but importers
  that ignore `@font-face` still substitute. Outlines need nothing from the viewer, so that is what the flag does.
- **Faces come from `SkiaFonts`' real embedded faces**, not the test-override face: a run drawn in an
  overridden face carries that face's family name, which does not resolve, so it stays as text.
- **Outlined vs live text through one rasteriser** differs only by a hairline of antialiasing around every glyph
  (outline fill against hinted glyph raster), with no displacement.

## Stability circles changed shape with the zoom (2026-10-24)

A FET's load stability circles drew as a fan sweeping up to the right at one zoom and as a flat band
through the chart centre at another, from the same data. Near the singular point (|S22|² ≈ |Δ|²) a
stability circle is nearly a straight line with a radius of ~1,800 in Γ, which is over a million pixels
at a 1600 px plot, and `TraceRenderer.BuildPath` put every circle of the sweep into ONE path with
`AddCircle`. **Skia's CPU raster drew that path correctly at 400 and 800 px and dropped or bent its
circles at 1600** — two circles in one path are enough (the near-singular one and an ordinary one); each
drawn as its own path is correct at every size, and a single huge circle is too. This is the
`ClippedPolyline` failure again, reached through a circle instead of a polyline, and on the CPU raster
as well as the GPU. `TraceRenderer.AppendClippedCircle` keeps each circle to the same box: one that fits
is still `AddCircle`; a larger one contributes only its arcs inside the box, sampled in double precision
(`TransformSet.ToCanvasPrecise`) with a 0.05 px chord sag. Gate: the second test in
`OutOfRangeLineTests`, which draws those two circles at 1600 px and fails without the fix.

## A marker at the top edge lost its glyph (designer round 27, 2026-10-25)

The marker glyph is clipped to the plot box so a marker PANNED out of view stops at the axes (the 2026-08-21 fix). The
triangle and name sit ABOVE the data point, so a marker on a trace running along the top edge had its point in view
and its whole glyph clipped away. `MarkerRenderer.GlyphClip` raises the clip's top by the glyph's height when the data
point is inside the box, and returns the box unchanged otherwise. Gate:
`PanAndMarkerLabelTests.MarkerGlyph_PointInViewAtTheTopEdge_DrawsAboveTheBox` (fails with the plain box clip).

## A trace's i/j pin is a port NUMBER, looked up at every resolve (brief DFR27-2, 2026-10-25)

The `S` cube's port axes now carry the Terms' own numbers (see `src/Core/RESOLVED.md`), so a pin has to be looked up
by number, not position. A pinned `i`/`j` `AxisSlice` keeps storing **number − 1** in `Index`, the convention the spec
shorthand, `TraceGoalReader` and the labels already read back as `Index + 1`. `PortSlices.TryToPositions` turns that
into a position on the cube being resolved, in `TraceResolve.SetCubeDataFromCore` next to the labelled-pin rebind.
Only the local copy is positional; `t.Slice` keeps the number, so after a re-run that drops the Term the next draw is
invalid ("Port 9 is not a port of this result … (it has 1, 2, 10)"). Before, the resolve clamped an index past the
end onto the last port, which is how a deleted Term went on showing data.

- `SliceTokenParser` checks the number against the axis values when the axis is not 1…N. `TraceExpression` turns the
  token into a position with `PortSlices.PinPosition`. The picker's S(i,j) items carry the axis's numbers, and
  `BuildCarriedSliceFromCube` no longer clamps a port pin.
- A marker's per-port Z0 lookup used the pin as an array index. `Trace.SourcePortNumbers` (stamped from the Z0 cube's
  `port` axis beside `SourceZ0PerPort`) maps the number back to its entry.
- **Only port-NUMBER axes are translated**: an `i`/`j` axis whose values are distinct positive integers. The Signal
  Designer's 0-based `i` axis, and the test fixtures with a single `i = 0`, keep the positional reading.
- Gate: `Ui.Tests/PortNumberTraceTests` (`S[:, 10, 9]` reads 1009 from a 100·i + j cube; `S[:, 4, 3]` refused naming
  1, 2, 9, 10; one trace resolved against a re-run without port 9, then one renumbered 1…4, is invalid both times).

## A trace's own label (brief DFR27-3, 2026-10-25)

`Trace.Label` (persisted as `TraceConfig.Label`, written only when set) replaces the generated name wherever a trace
is named. Two methods carry it to nearly every reader. `TraceLabeler.ComputeMinimalLabels` feeds the legend, the Rect
Y-axis titles and the label strips. `Trace.ReadoutDescription` feeds every marker-box value row, multi and delta rows,
the stem readout and the marker editor line. Table headers take it in `TableRenderer.BuildColumns`, which also feeds
Copy Table Data; the tab-text export takes it in `PlotExporter.WriteTabText`.

- **It is not `QuantityName`.** That one replaces only the quantity and keeps the transform suffix and the source
  prefix. A label is the whole name, so a labelled trace gets no source component even when the source is forced on.
- **`Description`/`ShortDescription` are untouched.** `BuildPickerYExpression` reads `ShortDescription` as an
  expression fallback, so a label there would be parsed as a cube spec.
- **`RectYLabel` still appends its status suffixes** (`<invalid…>`, `dimension mismatch`, `@ Z0=`) after a label,
  because they say something is wrong or re-referenced, not what the curve is called.
- Contour and summary-column traces ignore it: `PlotConfigLoader` returns before reading it, and both have their own
  titles. A pattern cut's back branch (`plot`, `cut=` on a dB polar plot) keeps its own generated label, as before.
- **The legend had no hit-testing.** Its row arithmetic moved into `LegendRenderer.Layout`, which both `Draw` and the
  new `HitEntry` call (legend Rename…). Renders of unlabelled documents (inside legend with a marker box, below
  legend, table, in PNG and SVG) were byte-identical between this build and the pre-change `HEAD` build.
- **A trace-card edit had no undo path.** The container now hands its inspector `PushUndo` and `MarkerBoxesStale`.
  The second hook exists because a marker info box lays out its text once, so only a rebuild picks up a new name.
- The Label box's watermark is `TraceLabeler.QuantityFor`, which ignores the label, so it always shows the name
  being replaced.
- `PlotInspectorView.FocusSpecTextBox` focused the first visible `TextBox` bound to the row. On a network trace,
  whose spec box is hidden, that would have been the new Label box, so it now skips it by name.
- CLI: `SplitFields` keeps quotes in a field, so `label=` strips one surrounding pair.
- Gate: `Ui.Tests/DataDisplay/TraceLabelTests` (byte-for-byte round trip through `BuildTraceConfig` and the loader;
  legend, minimal labels, marker box and table header) and `MissingVerbsCliTests.ALabel_IsWrittenToTheCdd_CommaAndAll`.

## Legends: Below spans the plotting region; Floating is a canvas box (2026-10-25)

- **Below** used to lay its cells from the canvas edge (`pad`) to the far edge, reaching under the Y-axis labels. It is
  now laid across `PlotRenderer.PlotRegionX`, the plot box on Rect or the disc on Smith/Polar, read from the viewport
  BEFORE any band is taken out of it. Bands only take height, so there is no circularity: the region decides the
  columns, the columns decide the band, and the band decides the viewport's bottom. The width-only `BelowBandPx`
  overload (`PlotCanvasGeometry`, before a Smith canvas exists) uses a square canvas, which has the same disc width.
- **`LegendPlacement.Floating`** (appended; stored by name) is the Inside box lifted onto the Data Display canvas.
  `Plot.LegendPos` is in the same logical space as `Marker.InfoBoxPos` and is persisted as `LegendLeft`/`LegendTop`,
  written only when floating; paste offsets it like an info box. `Draws` is false for it, so the plot neither draws
  it nor reserves a band; `Floats` says it is the host's to draw.
- Its size is `MeasureFloating` at `LineWidthFor(the plot canvas's logical size)`, the unit the docked box is
  measured at, so lifting it keeps its text size. It is not cut to 60 % of the plot width as the docked box is.
  `DrawFloating` scales that box to the pixels it is shown at, as `DrawInfoBox` does, so it zooms with the canvas.
- Exports: `PlacedPlot.Legend`, set by `PlotPlacement` (headless, with `DefaultFloatingPosition` for a file that
  names Floating without a position) and by `PlotExporter` (from the box). `PlotComposer` frames and draws it over its
  plot and under the marker boxes, the order the window stacks them in.
- The Inside path draws through the same `Layout`/`DrawFrame`/`DrawRows` as before. Unlabelled Inside renders were
  byte-identical before and after (DFR27-3's comparison); Below changes on purpose.
- **A docked legend vanished when the Data Display was zoomed out.** Every path returned early below a 4 px font. The
  docked font is in screen pixels and scales with the zoom, so zooming out dropped the legend, and a Below band with
  it, which moved the plot box, while the axes and curves were still drawn. The cutoff is gone and the legend draws at
  any size, as the axis text does.
- Gate: `Ui.Tests/DataDisplay/FloatingLegendTests`.

## A plated via's pads speckled through its bore; a copper layer's top edge drew a lit dashed line (2026-10-25)

Three artefacts on a 10-layer board in the 3D editor, two in the default view and one in the realistic view.

- **The pads were carved at the BORE, so every one lay on the barrel.** `Scene3DBores` carved a dielectric at the barrel's
  outside (2026-10-04) but left metal at the bore, so each pad's hole wall coincided with the tube's inner wall and each
  outer pad's cap with the tube's end ring — nine layers of coincident faces per via, kept apart only by the via's
  constant depth bias (−4). Perspective views held; an ORTHOGRAPHIC view at an angle did not (the editor opens
  orthographic): inner-layer copper speckled through the bore, and the end ring broke up against the bottom copper.
  Metal is now carved at the barrel's outside too, so the bore shows the plating alone and the rings meet the pads edge
  to edge. A pad too narrow for the barrel's outside keeps the bore's hole rather than being drawn over it. **A via on the
  board's edge still speckled at its end ring**: half its circle lies outside the plane, so no hole fitted and the plane
  was drawn whole over it. A bore that clips a polygon's edge, or one of its holes, is now subtracted from it with
  Clipper2 (`Scene3DBores.Notched`, in picometres, every original and ring vertex mapped back exactly so the notch meets
  the tube's own vertices); each wall on one of the primitive's own edges keeps that edge's face. What still
  coincides — a pad's hole wall on the tube's OUTER wall — is visible only through a translucent dielectric, and no
  sweep view showed it fighting.
- **The realistic dashes were the bias itself, not a crack.** Every slab's side wall is one quad with the same endpoints
  as its neighbour's, so there is no T-junction. A copper layer reaching the board's side is embedded in its slab, and
  its top face meets the slab's wall along that edge; `Behind`'s polygon offset pushed the wall back by 4 steps plus
  2 × its slope, and the hidden top face won every pixel centre within that band — lit from above in PBR, so a light
  dashed line along each copper's top. The default view had the same band, invisible because a conductor's top and side
  are one flat colour there. The leak scaled exactly with the slope factor (2 → 14,919 px over 15 views, 1 → 7,765,
  0.5 → 3,885, … 1/32 → 235), and at 0 the coincident copper side faces fought the wall again; 1/128 was already
  fight-free on Metal. **`BehindSlopeScale` is now 1/16**, 8× that, for rasterisers with coarser sub-pixel precision
  (Vulkan only guarantees 4 bits). The shadow pass, which took `DepthBias(Behind)`, keeps its 2 as `ShadowSlopeScale`:
  acne wants the larger factor and nothing leaks there.
  **Amended 2026-10-26 (brief-em3d-138, below):** 1/16 did not close it. The dots it left were the same leak, and carving
  the walls is what closed them.
- **Seen first through a build that had not been rebuilt.** A before/after A/B restored the fixed source and rendered
  with `--no-build`, so a later sweep drew the OLD geometry and looked like a new artefact; every rebuild was clean. An A/B
  that swaps a source file needs its rebuild in the same step as the swap.
- **`tools/UiSnapshot --c3d`** came out of this: the editor's own view model on the platform's GPU backend, the stored
  camera from `.cwsuser`, Export Picture's `CapturePicture` — the owner's exact view, reproducible from a shell. A scratch
  harness built from the problem directly drew the realistic view in the wrong colours (no per-user appearances), which is
  why the tool goes through the view model.

Gates: `MetalPrecedenceTests.TheRealisticView_ShowsNoHiddenCopperTop_AlongTheBoardsSide` (the CPU realistic picture, which
applies the same polygon offset: 318 px came through at slope 2, none at 1/16) and
`PlatedViaAndPerspectiveTests.APlatedBarrel_IsDrawnAsATube_ThroughAHoleInItsPad` (the pad's hole at the barrel's outside,
the narrow pad's at the bore, the edge pad's notch).

## A "vs" trace broke as re-runs added sweeps; one seed rule for every default (2026-10-26)

Field report on the PowerAmplifier example: Gt_dB vs Pout_dBm plotted over Pin; adding an RFfreq sweep pinned it at
1 GHz; adding VDD made the trace invalid, and a fresh card could not plot it either.

- **The re-run reseed** (`PlotInspectorViewModel.ReseedSliceIfCubeShapeChanged`) handed the whole `Y vs X` text to
  `CubeTraceSpecParser`, which does not read `vs`. The bare name was refused, the carry pinned every new axis at index 0,
  and the spec was rewritten with brackets, so the next reshape could not re-read it either. It now splits on
  `VersusSpec` first.
- **A bare X side on three or more axes** asked the parser for its whole-cube reading, which is not one X. That failed,
  so the X fell through to `TraceExpression` and was refused as "more than one X axis". `VersusResolver` never used
  that reading (a bare X inherits the Y side's roles by name), so it now resolves the name alone via
  `CubeTraceSpecParser.TryParseBareName`.
- **`SeedSlice`** (this project) is now the one default: freq is X when present, else with two or more sweeps the
  innermost is X and the next one out the family, else the first plottable axis. It was `BuildSeedSlice`, used only by
  "+ Trace"; a signal picked on a card opened on the OUTER sweep. The trace card's `BuildDefaultSlice`, the carry
  (`BuildCarriedSliceFromCube`: shared axes keep their roles, a new axis takes the seed's role unless the old slice
  already fills it) and a bare spec on three or more axes all use it, so `plot --trace "cube=Gain vs Pout"` reads a
  3-sweep cube the way the card does.
- Gate: `Ui.Tests/PlotVersusSweepGrowthTests` (Pin → +RFfreq → +VDD, and a fresh card on all three).
  `PlotVersusCardTests.TheSummaryTracksTheYSidesRoles_IncludingAPinnedValue` now sets its starting roles itself, since
  its [Pin, RFfreq] fixture seeds RFfreq as X.

## A board's side walls: carved where copper reaches them, drawn with no bias (brief-em3d-138, 2026-10-26)

Amends "The realistic dashes were the bias itself" above. Windows screenshots of a ten-layer board showed a dotted line of
light pixels along each inner copper layer at the board's side, and they looked like a D3D11 fault. **They were the same
leak, at 1× on every platform**: Metal drew 335 dots against Windows' 352, each one the lit copper top's own colour, and a
high-DPI (2×) screen hides them because each dot covers a quarter of the area. Lowering the slope factor only thins the band
(side-wall dots at 1/16 → 1/32 → 1/64 → 1/128: 195, 97, 49, 26), and at 0 the copper's side faces fight the wall. **Any face
that ends on a biased face leaks.**

- **`Scene3DWalls` carves; it doesn't bias.** It is a sibling of `Scene3DBores`, run after it on the bores' output. Where a
  prism conductor (extruded polygon or box) has an edge collinear with a slab's outline edge, on the same side, that part of
  the wall is not drawn, because the copper's side face is that surface. The wall round it coincides with nothing, so it
  draws with no polygon offset. Its triangles come last in the mesh, and `Accumulator.Finish` gives them a second batch
  right after the object's first, with `Scene3DBatch.Free` set. `Scene3DFramePlan.TieOf(scene, batch)` makes a free batch
  `None`, and every colour/translucent/ID/moved-preview draw, `Scene3DPicking` and `Scene3DIdPatch` read the tie through
  it. The shadow pass keeps `ShadowBias` for every draw. Side-wall dots on that board at its stored camera: **195 → 0**.
  Two orthographic and two perspective views at other yaws also count 0. Every pixel that changed was a dot: 203 in the
  realistic view, and in the default view the 132 columns where the hidden top thickened a band's edge by one pixel.
- **A conductor just above or below a slab leaks too.** After the first carve, 26 dots remained, all along the bottom
  copper. That copper lies UNDER the bottom slab and touches it only along a line, and its top face (on the slab's bottom
  cap) ends on the wall the same way. Such a conductor is a cut of no height: it carves nothing, but it frees the wall
  next to it and adds its vertices to the wall's.
- **Freeing the whole wall leaked the other way.** Cull is off on every backend, so a free wall seen from behind met its
  slab's still-biased cap along the rim. On the 1/16-px band of that shared edge the back face won, which showed as 12
  dark dots along the board's far top edge. **Only the wall round each conductor is free.** That region is the cut grown
  by the conductor's thickness (at most a quarter of the slab), and not grown along the wall for a cut of no height,
  because there the band lies on the cap's rim and only the conductor covers it. The rest of a rebuilt wall keeps its
  slab's bias. A free piece meets a biased piece in the same plane without overlapping it, so that seam can't leak.
- **The wall is rebuilt as strips.** Heights come from the slab, each conductor and each free band. Each strip is
  zippered between the breaks its two lines need: the caps' own vertices on the slab's top and bottom, every conductor
  vertex on the wall's line at each height the conductor reaches, and every strip end above or below. So no conductor
  vertex is a T-junction on a wall piece. **The caps are not touched**, so a conductor vertex on the slab's top or bottom
  line is a T-junction on the cap's rim. Splitting the cap triangle there into a fan was tried and drew a dot of its own,
  because the fan's slivers take a different slope-scaled bias. The T-junction it repaired drew nothing in any view
  rendered.
- **Conservative where it can't see the faces.** A wall keeps today's treatment (uncarved and biased) if any of these
  touches it: a prism lying against it from outside; any other conductor shape whose bound reaches it (a sweep, a STEP
  body, a sheet standing on its edge); or a vertical cylinder whose drawn ring meets what is left of the wall by more than
  a point at a notch's end. That last rule lets a barrel standing in the notch `Scene3DBores` cut at the board's edge leave
  the carve alone, while a cylinder tangent to the wall stops it.
- **Edge lines and snap.** A carved slab's edge lines are found on its UNCARVED surface (`Accumulator.Object`'s
  `edgeMesh`), so the rim round the copper is not an edge line and the cap/wall rim still is. The count is unchanged.
  Snap features (`Scene3DFeatureTable`) are built from the carved mesh, so they gain the copper/dielectric boundary on the
  wall, which is a real edge.
- **Clip plane.** Caps are drawn from back faces, and the carved slab is no longer closed. Where the wall is missing the
  copper fills that volume, and its back faces are the cap there. CPU realistic pictures clipped on x through the copper
  band and on y beside it matched the uncarved slab's pixel for pixel, except for the old dots.
- **Hiding the copper shows the slot.** The wall stays carved when a conductor is hidden or drawn as a wireframe, because
  visibility is a view toggle and the carve is built with the scene. `Scene3DBores` behaves the same way for a barrel's pads.
- **Cost.** A carved slab costs one more tessellation (its own surface, then the carve), once per prototype and never per
  element. `HierarchyGateTests.Gate1` now counts it. A slab edit that keeps the carve's shape still patches.

Gates: `MetalPrecedenceTests.TheRealisticView_ShowsNoHiddenCopperTop_AlongTheBoardsSide` (0 copper-top pixels at three
yaws, where it allowed 10; 21 came through at −40° with the carve off) and `…_DrawsTheCoppersSide_AllAlongItsBandOnTheWall`;
`BoardSideWallTests` (the strips and copper sides tile the wall with no copper vertex mid-edge; a tangent cylinder leaves the
wall biased; a carved slab's two batches, their ties, picks on each, the edge-line count, and the patch).

## Designer round 28 — tick labels that all read "-250", and a field plot blaming the wrong setting (2026-10-26)

- **A flat trace at the dB floor labelled every y tick "-250".** `EnsureMinExtent` opens a zero-height extent to 0.5
  around −250, so the ticks fall 0.1 apart, and the axis's three significant digits print each one as "-250".
  `EngineeringFormat.DigitsToResolve` raises a LINEAR axis's digits to what its step needs: ⌊log₁₀ max|v|⌋ −
  ⌊log₁₀ step⌋ + 1, capped at 15, never fewer than the axis's own. The draw loop and `MaxYTickLabelWidth` use the
  same count. Every axis three digits already resolved is unchanged; the 1,127 plot tests run unchanged.
- **A field plot of a setup whose last run FAILED said "saved no fields — set Save fields at".** A failed run leaves
  its directory (Gmsh's files), and `Discover` counted any directory as a run. A directory whose `status.json`
  reads failed or cancelled no longer counts, and the sentence is "No finished run of setup … yet". The tree row's
  detail text carries the warning's tooltip too, not only the 12-px glyph, which the designer had not noticed.

## Impedance report: a Summary style for a large board (brief-impedance-9, 2026-10-27)

`TraceImpedanceReportDocument.Pdf(report, style)`; the style-less `Pdf(report)` is Full and is byte-identical to the
report before this change on every map whose markers all found room (checked against PDFs written by the previous
build for three synthetic reports, and by the panel's and the CLI's existing gates).

- **What the Summary carries.** The same summary page; per layer with traces, the map and one findings page: the
  traces and junctions counted in a line, the findings grouped by kind × severity × width class (owner decision D2
  (a)) with Open and Accepted counts, the worst value and the trace ids that fit a line, a Total row, then the 20
  worst open findings in Full's own sentences (fails first, then Z0 excursion, then stretch length). A layer with no
  trace reviewed has no pages: on a 10-layer board three plane layers were six pages of "no traces". The worst list
  is in the small type so twenty fit under a dozen groups on one page; at body size they wrapped and every layer
  spilled a page.
- **Grouping reads numbers, not sentences.** `TraceIssue` gained `SpanLength`, `WorstOhms`, `Coverage` and `S11Db`,
  set by the analysis where it writes the sentence, so ranking and "worst" never parse text.
- **Width class is the survey's rule.** `TraceImpedanceAnalysis.WidthClasses(traces, dbuPerMicron)` classes each
  reported trace by its dominant width (its pieces clustered by the survey's own `Cluster`). It is drawn from the
  report's traces only; a survey draws from every trace on the layer, so a class straddling a scoped-out width can
  merge differently.
- **Markers.** A Summary map has one marker per trace, at its worst finding, carrying its count; markers anchored
  within 12 pt of a seat (failing ones and the most findings take seats first) are one badge, bounded by the seat
  so a dense board is many neighbourhood badges rather than one chain across the page. Ids only on failing traces.
  On BOTH maps, markers the placer still had to put on top of each other merge into a badge and the lot is placed
  again (up to 8 rounds). A Full badge reads the finding numbers it stands for ("232, 234–235, 239 +4"). A map with
  no overlap is placed once, exactly as before, which is what keeps Full byte-identical.
- **Page counts without writing.** `PageCount(report, style)` runs the same composition on an `SKNoDrawCanvas`,
  skipping the maps and the stackup (one page each whatever is on them): 714 ms for a 531-page Full on board C.
  The panel shows both counts and pre-selects Summary past `SummaryAbovePages` (50), with a note naming Full's
  length while Full is long; the CLI keeps Full (D1 (a)) and warns `impedance.report.long` with both counts.
- **Board C, whole board, no scope** (1,800 traces, 7,711 findings): Full would be 531 pages; the Summary measured
  29 before the two page fixes above; they remove the plane layers' six pages and the three continuation pages
  re-measured on the small layers, so it is at most 20. Every layer's groups summed to its findings exactly. The
  PDF was 57.8 MB, almost all of it the maps' copper, which Full draws too.


## Review markers on the canvas, and one clustering (brief-review-6, 2026-10-28)

`LayoutOverlay.ReviewMarkers` draws through `DrawFindingMarkers`, the DRC and LVS routine, in a role of its own,
`Layout.ReviewFinding` (purple), because both panels' markers can be on screen together and a review finding is not
an error. Accepted findings use `Layout.DrcWaived`. `Default.ccolor` carries no `Layout.*` roles, so the new role
lives only in `ColorTheme.BuiltIn` (both variants), as the DRC roles do.

The impedance report's seat clustering (brief-impedance-9) is now `MarkerSeats.Group`, and the report calls it, so
the canvas and the PDF share one. On the canvas the anchors are marker centres in DEVICE pixels (reach 14 px), so
markers separate as you zoom in. A marker at least 24 px each way on screen is drawn as its region and never seated,
and the selected marker is never folded into a badge. Badges are drawn in device pixels (`canvas.Scale(1/scaleUm)` at
the anchor) because a font sized in path units is a tiny float at board zoom. A two-point marker ring is a line (a pad
to its nearest via): `DrawFindingMarkers` skips rings under three points, so the review draws those lines itself.

## The Layout Review report (brief-review-7, 2026-10-28)

`LayoutReviewReportDocument` (`src/Render/Renderers`) is the one composition the Review panel's Export PDF and
`circuitrf review -o` both call. **Page furniture is `ReportFurniture`**, factored out of
`TraceImpedanceReportDocument` with the code moved verbatim (page, colours, fonts, header/footer, wrap/clip/paragraph,
table header and row, marker, check, badge, scale bar, the orange notice); the impedance writer's private methods now
delegate to it, so the two reports cannot drift apart. Clustering is `MarkerSeats.Group`, as on the canvas.

- **One map PAGE per check, a panel per layer — not a page per layer.** The brief asked for both one map page per
  layer and at most about two pages per check; on board C those disagree: edge-copper has two findings on each of ten
  copper layers, which was eleven pages, and the whole review 39 against the gate's 19. A check's layers now share its
  map page (1 = the whole page, 2 side by side, up to 12 on a 4 × 3 grid, `PanelsPerPage`), so every check is two
  pages and board C is 1 + 9 × 2 = 19. Past 12 layers a check gets a second map page.
- **The length is planned, not discovered.** `CheckPlan.ListedOpen` decides how many findings the findings page lists
  (20, or fewer when sentences are long) before any map is drawn, measuring with the list's own font from a fixed list
  top; the map numbers exactly those. Without it, a page that ran out of room would have left map numbers pointing at
  nothing. The page never continues, so `PageCount` is `1 + Σ (⌈layers / 12⌉ + 1)`.
- **Copper is decimated to the page.** Every map drew its layer's full union; board C's PDF was 198 MB. A vertex
  within 0.2 pt of the last drawn one, or a ring under a third of a point both ways, is skipped (copper and the lower
  layer's outline only — never a finding's own ring): 25 MB, and the maps look the same at any reasonable zoom.
- **A marker sits ON its region.** At the bounding box's centre a band round the board's edge (edge-stitching) put
  its marker in the middle of the board; when the centre is not inside the region, the marker goes to the region's
  vertex nearest it. A two-point ring (a pad to its via) is a line and keeps its midpoint.
- **IBM Plex has no ⌀.** The via-in-pad sentence writes a drill as ⌀300 µm, which drew a box; the report writes Ø
  (`Printable`). The canvas and the CLI keep ⌀.
- **The visible date is the day, not the minute.** The PDF's metadata carries the run's time; the page text carries
  only the date, so the process-vs-in-process gate differs only in the metadata dates it strips.

Board C, every check, Release: 53.8 s and 2.04 GB for the run with `-o`, of which laying out and writing the
19-page, 25 MB PDF is 2.2 s; the run without `-o` measured the same within run-to-run noise.

## Designer round 30 — no pages for an impedance layer with nothing reviewed, in Full too (2026-10-28)

`TraceImpedanceReportDocument.Compose` skips a layer with no trace and no junction reviewed in BOTH styles; brief
impedance-9 had done it for Summary only. A Full report of a 10-layer board reviewed on its two outer layers was
22 pages, 18 of them the empty map and empty table of the eight inner layers and of Bottom Copper (whose region
selected no trace); it is 4. The summary page's By layer table
still has a row per layer, with its pours and out-of-scope counts.

## The hover tint at a coincident face — investigated, not changed (2026-10-28)

Report: hovering a face or object of a board in the 3D view breaks the highlight up as the view zooms, the way the
board-side copper did before brief-em3d-138. The hover is a tint in the shader by object id; it moves nothing, so any
fight it shows is one that already exists between two equal-coloured surfaces. Measured on a 10-layer imported board
with `tools/UiSnapshot --c3d … --hover <name> --live` (added for this: `--live` plans the picture as the live view does,
so the realistic view draws the hover), comparing pixels the hover tints against an unhovered render:

- A colour-pass-only tie for the hovered object (`Hovered`, between `Via` and `FaceImage`; the ID pass unchanged) with
  the decal's clamped slope removed the few specks a hovered copper band showed at 1× (4–8 px), but brought back the
  hidden copper top along the band as a dotted white line, and added 55–179 speck pixels to a hovered slab's rim.
- The same tie with the constant only was pixel-identical to no change in every view tried.
- Both were reverted. A layer's copper on this board is disjoint islands (zone-fill pieces do not overlap), so
  same-tie coplanar copper is not the mechanism here; at 1× a copper band is about one pixel tall and its specks are
  coverage, not depth. The owner's own view of it is needed before changing the tie order.

**Resolved, by geometry (2026-10-28).** The owner's screenshot found it: a via the board edge cuts through (DFR28-1) is
built as an extruded clipped disc, not a cylinder, so `Scene3DBores` never saw it as a bore and the pads, planes and
slabs it passes through were drawn whole — on every layer a straight side face lay on the via's flat face. One colour
unhovered; tinted, the copper broke through the via differently at each zoom, from above and below and in both views.
`Scene3DBores.Of` now takes the via barrels by name (`Scene3DBuilder` passes the origins) and treats an edge-cut barrel
as a SHAPED bore: its outline is cut out of every conductor and slab it passes right through, as a round barrel's
outside already was. Counted on the owner's board, via hovered, two pitches × six zooms, realistic: 1,052–8,979
untinted pixels on the via's face before, 169–515 after, which are the grid's lines and the measure's own edges. The
bias variants are not needed; `Scene3DFaceImages` still calls `Of(problem)` without the via names (a face image on a
pad an edge via passes through would still be drawn uncut).

## The drawing grid wins over a face in its plane; the hover wins over the grid (2026-10-28)

`fs_grid` solves the plane's depth per pixel while a face lying in the plane (a board's bottom copper on the XY plane at
0, seen from below) is rasterised, so the two fought with the zoom. The grid's depth now comes towards the eye by two
pixels' worth of the plane's own depth slope (`fwidth`) plus 4e-7: a face in the plane loses to it, geometry genuinely
in front still hides it, and only a two-pixel sliver where a solid passes through the plane shows lines past its edge.
Regenerated with `tools/ShaderGen`. The hovered object is then drawn a second time right after the grid
(`Scene3DFramePlan.HoverOverGrid`): it lands on its own depth and passes LessEqual, so its tint covers the grid's lines
wherever it is the nearest surface; the grid writes no depth, so nothing else changes. The grid moved before the lines
and overlays in both colour passes so they still draw over it and over the second draw.

## DFR32-2 — port names at a fixed screen size, never on top of each other; the bar is the 3D sheet (2026-10-30)

- **Placement is `LayoutRenderer.PlacePortNames`** (`LayoutRenderer.PortNames.cs`), one pass per frame shared by the
  knock-out, the names and their leaders, and by a port ghost. Each name is 11 pt (a ruler's default readout), upright,
  just outside the mark's box (`LayoutPortDirection.MarkerBbox` of the bar actually drawn) on the side current comes
  from. In document order, a name whose box meets an earlier one steps outward by half its height until clear; past
  its own height it gets a half-weight leader to the mark. It reads neither the pan nor the tile gather order, so the
  same layout at the same zoom places the same way, and `render` draws through it. The stored `Height` is not read
  (GDSII/DXF still write it). With no viewport scale (≤ 0) a name is drawn where and as it was stored.
- **The dashed leader from the label's stored anchor to the plane is gone**: the name is beside the mark now, so the
  anchor is not drawn and a line to it pointed at nothing.
- **Two tests pinned the old position and were updated, not loosened**: `LayoutPortDirectionTests…ARRIVESAtTheReferencePlane`
  (the metal side past the bar is now empty, where the centred name used to paint) and
  `LayoutPortGlyphOnTopTests.TheNameIsNeverCrossedByItsOwnMarker` (now the placement's geometry: name box and mark box
  disjoint, and the name inked there).
- **Not changed:** the port's pick region and selection box are still the mark (`MarkerBbox`), and `DocumentExtents`
  still frames on the stored label, so a fit can leave margin a fixed-size name does not need.
- **The edge bar is `EdgePortSheet.Bar`** (src/Design, DFR32-2): on an edge longer than 4 × the height to the return,
  or with a stated Width, it is the 3D sheet's width, centred on the port, and the arrow scales with it.

## Slow pan and zoom on a board with ports: each port's conductor was a polygon union, every frame (2026-10-31)

**Measured** with a scratch harness drawing a field-test board (4-layer evaluation board, 2,696 shapes, 14 edge
ports, traces crossing a same-layer pour) through `LayoutRenderer.Draw` with the canvas's path and tile caches, in
Debug, at 1600 × 1000, panning a few pixels per frame:

| Zoom | ports drawn | ports hidden / removed | with `LayoutConductorCache` |
|---|---|---|---|
| fit | 52 ms | 16 ms | 17 ms |
| ×4 | 37 ms | 2.5 ms | 2.7 ms |
| ×16 | 34 ms | 0.1 ms | 0.3 ms |

A profile put 98 % of a ports-drawn frame in `LayoutPortDirection.Resolve`, which calls the conductor lookup's
`MergedWithOverlapping`, which calls `LayoutBooleans.Union`. Copper that overlaps is one conductor (round 7), so every
port's trace was unioned with the pour it crosses. `PlacePortNames` and `DrawPortMarker` each resolve the port. The
lookup's memo lived for one frame because `LookupFor` was called once per frame. The port labels' text was not the
cost.

**Fix.** `LayoutConductorCache` (`src/Render/Layout`) keeps the answers per (point, layer) across frames, and
`LayoutRenderOptions.ConductorCache` hands it to `Draw`. It rebuilds when the view, the technology INSTANCE (a new
`DrawTechnology` clone on any layer toggle), the base directory or `CellLayoutResolver.Generation` changes.
`LayoutCanvas` clears it on `Model.Changed` (beside the path and tile caches) and on a `Technology` change. The view
model keeps its own for `PortMarkerRegion`, which a click, the hover tip and the context menu ask per port, and for
the port-drag edge snap. Every export and one-shot render passes none and builds the lookup per frame as before. A
port being dragged asks at a new point each frame and pays for that one point. The first frame after an edit still
pays the full cost once (~35–55 ms on this board). Test: `LayoutConductorCacheTests` (counters, plus an identical
picture with and without the cache).

## An edge port's highlight and hit region spanned the whole edge, not its drawn bar (2026-10-31)

A port standing on the long SIDE of a trace (a component pad's terminal on the side of an 11.25 mm line) drew a
0.41 mm bar but was highlighted and picked by a box the length of the side. The bar goes through
`EdgePortSheet.Bar` (a stated `Width`, else 2 × H on an edge longer than 4 × H) in `DrawPortMarker` and
`PlacePortNames`, but `LayoutHitTest.PortPickBbox` measured `MarkerBbox` from the unnarrowed hint, whose width is the
whole edge `EdgeAt` found.

**Fix.** `PortPickBbox` takes the technology and DBU scale and narrows an edge port's hint through the same
`EdgePortSheet.Bar` before `MarkerBbox`. Both callers pass them: `LayoutEditorViewModel.PortMarkerRegion` (hit test,
hover, context menu) and `LayoutRenderer.DrawSelectionOutlines` (the highlight), so the two stay one rectangle. Test:
`PortSheetWidthTests.ThePickRegion_SpansTheDrawnBar_NotTheEdge`.

## A port snaps as a sheet does (2026-11-01)

- **A measurement could not start or end on a port** — hovering a lumped port's corner showed no snap marker at
  all. A port was built with no snap features (`Scene3DFeatureRef` default), and its triangles are drawn with no
  face (`Scene3DVertex.NoFace`), so `SnapQuery3D` skipped every port pixel in the patch.
- **The features come from the port's own surface** (`PortSheet`, a terminal strip, or a cylinder gap's patch),
  built as a sheet's one-face table — never from the drawn checkerboard mesh, whose cells and arrow would add
  corners that are not the port's.
- **The ID pass still writes NoFace for a port**, which keeps Face mode, face hover and face images off it. The
  snap query alone reads a NoFace pixel of an object with a one-face table as face 0 (`SnapQuery3D.FaceOf`), both
  when it collects the patch's pairs and in the visibility test.
- **A port's corner is an EXACT document point** (`C3dEditorViewModel.ToDocumentPoint`): a port is declared in the
  document's own DBU, never through an instance placement, and has no provenance entry to say so.
- Test: `PortSnapTests`.

## Filled vias and conductor boxes carve the copper they pass through (2026-11-01, owner report)

- **Symptom:** in a `.c3d` holding an imported board, every filled via's cap speckled against the top copper (plain view),
  and a conductor `Box` (a connector's ground strap) spanning the board's copper height streaked against the copper's top
  face. The hover tint showed the same.
- **Cause:** the cap and the copper top are one plane drawn by two triangulations, kept apart only by `Scene3DDepthTie.Via`'s
  constant bias (a box conductor ties `None`, so not even that). Seen obliquely the two interpolate depths further apart than
  the constant covers. Plated vias were already carved (`Scene3DBores`, 2026-10-25), but a filled via has no air core and
  so was never a bore.
- **Fix:** `Scene3DBores.Of` takes a filled via cylinder (named a via by the builder, with no coaxial air bore) and every
  conductor `Em3dBox` as a `Metal` bore. A metal bore carves a conductor it passes right through regardless of precedence
  (metal over metal draws the same either way), and a dielectric only under the usual precedence rule. Boxes go through
  the shaped (Clipper2 notch) path, since a strap usually crosses the board's edge.
- **Trade-off, unchanged in kind:** carving is baked into the scene's meshes, and visibility toggles at draw time, so hiding
  a filled via or a strap in the Object Tree leaves its hole or notch in the copper. The plated and edge-cut vias already
  behaved this way.
- Gate: `PlatedViaAndPerspectiveTests.AFilledViaAndAConductorBox_CarveThePadTheyPassThrough`.

## Snap tracking in the layout editor; one reference store for both editors (2026-11-02)

- **What:** the 3D view's snap tracking (rest on a snap point to make it a reference, then lock onto the lines through
  it) now works in the layout editor too. `LayoutSnapTracker` (`src/Render/Layout`) tracks the horizontal and vertical
  lines through each reference. The point lands where a line crosses an outline edge near the cursor
  (`LayoutSnapQuery.EdgesNear`, whose `layers` may now be null for every visible layer), else on the grid. Two
  references' vertical and horizontal lines give their crossing. A point candidate (every kind but `Nearest`) always
  wins. A tracked point is `SnapFeatureKind.Tracking`, appended last so no query priority moves, with its own glyph
  (circled plus) and the overlay's `SnapTracking` (references and dashed lines), drawn in the 3D view's colours.
- **One store:** the dwell (500 ms), the second dwell that releases, the 10 s forgetting and the cap of 2 live once, in
  `SnapReferences<T>` (`src/Render/SnapReferences.cs`). `SnapTracker3D` was moved onto it, so the two editors cannot
  drift apart on timing.
- **Where it plugs in:** `LayoutEditorViewModel.UpdateSnapMarkerCore` ends with `ApplySnapTracking`, which replaces
  `_currentSnapCandidate` with the tracked point and marks it a real target. Every drawing tool reads that one
  candidate (`SnapDrawPoint`, the ruler, port and via paths), so none needed a change. Active only with a tool other than
  Select armed and geometry snap on. Hovering in Select would acquire references nobody asked for. References clear on
  a tool change and when a two-point or multi-point shape is finished. `LayoutCanvas` ticks a 100 ms timer only while a
  dwell is pending or a reference is held.
- `tools/UiSnapshot` gained `--wait ms` (real time with the dispatcher pumped) so a dwell can be shown headlessly.
- Gates: `LayoutSnapTrackingTests` (end to end through the Rect tool, and the pure crossing/grid/point-wins rules),
  `SnapTrackingTests`.

## A marker on a labelled axis reads the label (designer feedback round 34, 2026-11-03)

A capacitance matrix plotted against Terminal j put "Terminal j=3" in the marker box, while the axis below it said
"C209 to C210". The generic X row of `Trace.BuildCubeMarkerBoxLines` printed the numeric axis value and only the
mixIndex branch used `_cubeXLabels`. A labelled axis now prints the label.

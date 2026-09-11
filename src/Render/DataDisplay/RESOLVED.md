# src/Render/DataDisplay — resolved briefs (detail, off the CLAUDE.md growth path)

## Antenna display feedback, 2026-09-11 — six reports, and three of them were the same bug

User feedback on the antenna work, relayed by the owner. Written up together because the causes
overlap: two of the six reports are one gate, and two more are the same missing information in two
places.

---

### 1. "I didn't get a U output from the simulation, only Etheta and Ephi"

**`U` was there the whole time.** `PlanarKernel.AddFarField` publishes `Etheta`, `Ephi` and `U`
together, unconditionally — verified directly against the reporter's own `.npy`, which carries all
three at `[51, 91, 360, 1]`.

What they saw was the **trace-card picker**, on a polar plot, with `U` greyed out and the two complex
cubes enabled. `TraceRowViewModel.RebuildSignalsCore`:

```csharp
bool isComplexPlot = _parent.PlotType is PlotType.Smith or PlotType.Polar;
bool isEnabled     = !isComplexPlot || cube.DataKind == DataKind.Complex;
```

Right for a Smith chart and for a LINEAR polar plot, both of which draw a locus in a complex plane.
Wrong for the polar plot's **dB radial mode**, which draws a PATTERN: the angle is the trace's own
swept axis and the radius is its value in dB, so a real cube is not merely allowed there, it is every
quantity an antenna pattern is actually plotted from — `U`, `GainDbi`, the Ludwig-3 pair. The gate is
now `… && !_parent.IsPolarDbPlot`, and the picker rebuilds when that mode flips (the six radial
properties all end in `ApplyRadialChange`, which re-frames; only this one changes what may be
OFFERED, so only this one rebuilds).

**A greyed row with no reason is what turned a gate into a wrong conclusion.** The reader had no way
to tell "this cube is missing" from "this cube cannot be drawn HERE". Every cube item now carries a
`DisabledReason` naming the plot kinds that CAN draw it.

### 2. The dB-radial switch was unreachable, which is why the picker gate mattered at all

Found while fixing §1, and it is the more serious half. The inspector row holding **dB radial** was
gated on `HasPatternScale`, which is `IsPolarDbPlot || IsSurfacePlot` — so on a LINEAR polar plot, the
state every polar plot is created in, the row was hidden **including the switch that leaves that
state**. A polar plot could not be turned into a pattern plot from the GUI at all. Every `.cdd` in
the wild carrying a pattern plot had been authored by the CLI.

**A control that ENTERS a mode cannot be gated on already being in it.** The row is now gated on
`HasPatternControls` (`IsPolarPlot || IsSurfacePlot`); the dB sub-controls inside it keep their own
tighter `HasPatternScale`.

### 3. "The 3D pattern icon is only visible after you add a polar plot"

Exactly right, and the cause is that the plot-type row carrying that icon lives on the **inspector**,
which needs a plot selected before it shows anything. Every other plot kind the inspector can switch
to has a matching **Add** button on the Data Display toolbar; the 3D pattern did not, so the only
route to one was "add a polar plot, then convert it" — which is not a route anybody finds.

One more toolbar button (`AddSurfacePlotCommand`, the same `AddPlot`), plus the sizing rule the
converted case already had: a surface takes the RECTANGULAR aspect, not the square one, because the
scene is square but the colour bar beside it and the caption under it are not. Added in
`DataDisplayViewModel.AddPlot` as well as in `PlotContainerViewModel.CoerceAspectForPlotType` so an
ADDED 3D pattern opens at the size a CONVERTED one lands at.

### 4. "Why do we force the user to have two traces?"

ANT-7's own note said a cut is two traces and that it was "forced": the two halves are different
SLICES of the cube (φ and φ + 180°), so there is no one slice a single trace could carry, and
synthesising a signed-θ axis would mean a derived cube and a second resolve path.

**That reasoning is still correct and the conclusion did not follow.** What was actually needed is
for ONE trace to hold TWO gathers of the same cube — an ordinary second rank-1 read with one index
moved, taken beside the first, in the same resolve. No derived cube, no second path.

`Trace.PatternWholePlane` (off by default, persisted, in the copy constructor). When set on a polar
pattern plot, `TraceResolve.TryWholePlaneCompanion` finds the pinned azimuth **by name** and its
companion **by value** — nearest to φ + 180° modulo 360, refused when the nearest is more than half a
grid step away, because a silently-wrong companion draws a smooth, plausible, wrong pattern. The path
build walks the back branch OUTWARDS-IN at negative angles and then the front branch outwards, so the
single point list runs −θ_max → 0 → +θ_max in drawing order; any other order joins the halves with a
chord that is not in the data. `PatternDbValues` yields the back branch too, or a whole-plane cut
whose peak is behind broadside would be normalised against its front half alone.

Gated as EXACT equality against the two-trace pair's own points (`AntennaFeedbackTests`), so the
simple spelling can never quietly become a different curve from the one every older `.cdd` carries.
**Both ship**: the two-trace form is still the one for halves that want telling apart.

CLI: `--whole-plane` on `plot`, which turns the back-branch trace it already synthesised into a flag
on the front one.

### 5. "The y-axis label freq= does not respect the plot's frequency units"

`TraceResolve.ApplyPinnedAxisDisplay` formatted every pinned axis from the cube's own raw values and
had never needed to know what the plot displayed frequencies in, so a GHz cut plot labelled its
traces `freq=1.74e+09 Hz`. It now takes the plot's `FreqUnit` — and, per the owner, an axis actually
named `freq` drops the prefix entirely, because "1.74 GHz" already says it is a frequency. A SECOND
Hz-unit axis (an LO beside an RF) keeps its name, since there the name is the only thing separating
two tokens that would otherwise read identically.

Formatted `0.######`, not `G6`: G6 turns 5e9 Hz into `5E+09 Hz`, which is the same unreadable thing
one unit further along.

**Found while verifying it: the headless strips were showing something else entirely.** The window's
label strip reads its `AutoLabel` (`TraceLabeler.ComputeMinimalLabels`); `PlotComposer` read
`Trace.Description`, which for a cube trace is `Expression ?? CubeName` and nothing else. Two cuts of
one pattern, differing only in pinned φ, therefore printed the SAME strip — "farfield.U" twice — in
every exported and CLI-drawn picture, while the window told them apart correctly. RND-4's rule is
that a headless render produces what the window produces. `PlacedLabelStrip` now carries the
`AutoLabel`, computed in `PlotLabelStrips.For` — the same call, from the same place the strip SET is
decided, so the two can no longer answer differently.

### 6. "Add the angle around the circle's edge"

`Plot.ShowPolarAngleLabels`, off by default. Spokes every 30° inside the disc (skipping 0/90/180/270,
which the two diameters already draw at axis weight) and the bearing printed outside it.

**Two things worth keeping.** The label ring follows the plot's OWN angular convention rather than
imposing one — a PATTERN reads 0° at the top increasing clockwise, a LOCUS reads 0° at the right
increasing counter-clockwise, because there the angle is the phase of a complex number, and printing
one convention's numbers around the other's plot would be worse than printing none. And turning it on
**shrinks the disc** rather than overprinting it: the room comes out of `PlotRenderer.ComputeViewport`
(`ComplexAngleLabelMargin`), which is the one place that makes the clip, the transform, the ring
lattice and the boundary ring all agree about where the edge is. On a normalised pattern the outer
ring is exactly where the peak sits, so an overprinted bearing lands on the one sample the reader came
for.

CLI: `--angle-labels`. **Not a dB option** — a locus has a bearing too — so it is refused on its own
terms rather than with `DbOptions`.


## ANT-7, 2026-09-10 — the dB polar mode, and the cuts

`brief-antenna-7-pattern-plots.md`. Presentation of ANT-4's patterns and ANT-5's metrics in the Data
Display. The brief's own summary was right: most of it already worked, one thing did not, and the one
that did not is the one an antenna engineer looks for first.

---

### 1. §1's three claims, measured. One of them was half true, and it changed the work

The brief asked for each to be confirmed on a real `"farfield"` cube before building on it. The
fixture is a real solve — `tests/Ui.Tests/DataDisplay/PatternFixture.cs`, the coarse FR-4 line every
port test in `Engine.Tests` is built on, with a 30°×45° pattern grid — because the axis names, the
units, the θ range and each cube's `DataKind` are all decisions `PlanarKernel.AddFarField` made, and a
hand-built fixture restating them would only agree with itself. **It costs 296 ms**, which is why it is
in the routine gate rather than tagged.

| claim | verdict |
|---|---|
| Rectangular cuts work today with no changes at all | **True.** θ on x in degrees, dB on y, four samples. |
| Pinning the extra axes is already expressible | **True**, all three halves of it: an int pins, a Range keeps and narrows, a missing entry defaults to pin-index-0. |
| The polar radial axis is already general | **True of the AXIS. Not true of the gloss beside it.** |

**The third is the one that mattered.** `AxesRenderer.PolarRings` is genuinely scale-free — it returns
the same five-ring lattice at 1, at 12.5 and at 0.004, and nothing in it is locked to |Γ| ≤ 1. But the
brief's gloss, "a linear-magnitude pattern renders on it today", is false of a pattern cube:
`Trace.BuildCubePath`'s complex-plot branch required a COMPLEX cube and returned no points for a real
one — and **every pattern quantity worth plotting is real** (`U`, the dBi metrics, ANT-6's Ludwig-3
pair). What did render on it was a complex cube's Re/Im locus, which is not a pattern at all and is
exactly the "derived complex expression" §2 refuses.

So the mode had to ADD the real-cube path, not re-label an existing one. Measured and asserted in
`PatternPlotTests.ThePolarRadialAxisIsGeneral_ButARealPatternCubeDrewNothingOnIt`.

---

### 2. The shape of the mode: the trace maps, the plot resolves, the grid labels

Three pieces, and the split between them is the design.

**`PolarPatternScale` (`PolarPattern.cs`) is the resolved scale**, an immutable record: the reference
at the outer ring, the floor at the centre, the ring step, whether it is normalised, and the unit.
`Radius(db)` returns 0…1.

**`Plot` resolves it and pushes it onto every trace** (`Plot.RefreshPolarPattern`). This is the part
that could not live on a `Trace`, and the reason is ordering: a trace builds its own path the moment
its data is set (`Trace.SetCubeData` calls `BuildCubePath` itself), which is before the plot knows
what else is on it — and **a normalised reference is a property of the whole plot**. An E-plane cut
and an H-plane cut each normalised to its OWN peak is a lie about their relative levels: both would
touch the outer ring whatever the antenna does. So the scale is resolved at the two points every
caller already passes through after resolving traces — `Autoscale` and `RestoreAxesFromConfig` — and
`RefreshPolarPattern` is the FIRST statement of `Autoscale`, before the Table return and before the
per-axis flags, because a pinned window is the common case on a pattern plot and the POINTS still
have to be rebuilt.

**The trace maps to Cartesian, and everything downstream is untouched.** `BuildCubePath` and
`BuildFamilyPath` gain one branch each that writes `Points` as (r·cos, r·sin) on the unit disc, in the
same world coordinates a Γ locus uses. Autoscale, the viewport clip, marker hit-testing, the composer
and all three export backends therefore needed no pattern-specific code at all. The alternative
considered — a non-linear pre-transform inside `TransformSet` — would have touched every renderer in
the file and needed an inverse for panning and hit-tests.

`AxesRenderer.DrawPolarGrid` takes an optional `PolarPatternScale`. Null is every polar plot that
existed before, unchanged; non-null swaps the 1/2/5 lattice for a decibel one and the SI-prefixed tick
text for the scale's own labels. **The boundary ring moves from `pxMax` to world radius 1**, which is
what makes a panned or zoomed pattern plot keep its outer ring on its own reference.

---

### 3. The floor is the point, and it is a clamp rather than a skip

§2's rule — *values below the floor drawn AT the floor, not omitted* — is enforced by having no "skip
this point" branch at all: `PolarPatternScale.Radius` clamps to 0 and the only thing that still skips
a sample is a non-finite VALUE, exactly as on a rectangular plot. A gap in a pattern trace reads as a
null in the antenna, and a real null and a clipped value must not look the same.

The null case is the gate that distinguishes the two, and it is the one that would have passed
silently otherwise: a pattern with an exact zero at θ = 45° still produces one point per sample, with
the null AT the centre and the shoulders either side near the rim.

`DbFloor`'s own −250 dB clamp is what stops an exact zero (−∞ dB) from becoming the reference. It was
already there for the matched-line S(1,1) case and it does this job for free.

**The other end of the disc is symmetrical and was not in the brief.** An ABSOLUTE reference below the
data flattens samples onto the outer ring; the scale counts them and the caption says so
(`AboveReferenceCount`), rather than letting a clipped peak read as a measured one. Normalised, the
count is zero by construction.

---

### 4. What the plot states, and what it deliberately does not restate

§4 lists five things. **Three of them were already on the plot** and adding them again would have been
a second copy of one fact: the cut's φ, the driven port and the frequency are the trace's own PINNED
AXES, and `TraceLabeler.ComputeMinimalLabels` already writes every one of them into the label strip —
`U(freq=5e+09 Hz,phi=90 deg,port=2) dB`. Confirmed rather than assumed
(`TheCutThePortAndTheFrequency_AreAlreadyInTheTraceLabel`).

**ANT-5 attaches no NAME to a cut.** A derived plane lands on the beamwidth cube's own `cut` axis as a
φ in degrees, and the "derived" flag lives in the run's notes; no cube in the `"farfield"` group names
a plane in words. So the φ IS the cut's name here, and §4's "its name if ANT-5 derived one" has no
other answer to give.

What `PatternCaption.Lines` adds is the two nothing else says:

1. **normalised or absolute, with the reference value** — and the compass orientation beside it;
2. **the θ span and what it means**, from `PolarPatternAngle.HemisphereNote`, which reads the trace's
   own axis ends. `θ 0…90°` says the lower hemisphere is not modelled; an axis reaching 180° says both
   are. Asserted by handing the renderer a cube whose θ reaches 180° and checking the sentence changes
   — which is what ANT-11 will do for real, with no edit here.

These REPLACE the per-trace row `DrawComplexXLabels` used to draw, and that is a fix rather than a
trade: for a cube-bound trace `Trace.MinFreq`/`MaxFreq` are simply the first and last value of its X
axis, so on a pattern the row read **"freq (0 to 90 GHz)"**. (That wrongness applies to any cube-bound
trace on a Smith or Polar plot and is not repaired here.)

**`PlotCanvasGeometry.BottomLabelExtraLogical` had to learn the same row count.** It mirrors
`DrawComplexXLabels`' arithmetic term for term — that mirroring is what the file exists for — and a
row the canvas is not made tall enough for is a clipped sentence. At a bare square canvas the caption
falls off the bottom entirely, which is what the test's `Svg` helper demonstrates by asking for the
composed height.

---

### 5. 0° at the top, clockwise — and why it is not a setting

The polar plot's own convention is the complex plane's: 0° at the right, counter-clockwise, because
the point IS the complex number. A pattern is not a complex number, and that convention puts an
antenna's zenith at three o'clock.

`PolarPatternAngle.Point` fixes the mapping at **0° at the top, increasing clockwise** — the compass
convention, which is what both an elevation cut (θ from zenith) and an azimuth cut (φ from a bearing)
are read on. It is deliberately not a flag: a plot whose orientation has to be read off a control
before the picture means anything is worse than one convention stated on the plot, and the caption
states it.

**A trace whose X axis is not an ANGLE is refused, not drawn.** `TryDegreesPerUnit` accepts a `deg` or
`rad` unit, and an unstated unit only on an axis NAMED `theta`, `phi`, `cut`, `az` or `el`. Anything
else sets `PatternAxisInvalid` and the label says `<invalid: a dB polar plot sweeps an ANGLE>`, in the
shape `RectValueInvalid` already had. Guessing on an unnamed, unitless axis is how a Pin sweep ends up
drawn as a bearing.

---

### 6. A `port` axis integer is a PORT NUMBER — and it could not be `i`/`j`'s subtraction

§3's trap. `SliceTokenParser` already read `i`, `j`, `row` and `col` as 1-based and resolved them as
`index = n − 1`, which is exact because an S matrix's ports are 1…N by construction.

**A `port` axis is not.** It carries the numbers of the ports that were actually DRIVEN
(`PlanarFarFieldSet.PortNumbers`), which need not start at 1 and need not be contiguous. So the number
is looked up in the axis's own VALUES — `SliceTokenParser.Parse` gained a `double[]? axisValues`
overload, filled by both of its callers — and a port the run does not have is refused BY NAME, listing
the ports it does, instead of resolving to a neighbour.

**This is also what makes the change safe for harmonicaRF**, whose `port` axis holds 0, 1, 2 … rather
than port numbers (`HarmonicaDataSet`, the intrinsic-plane cubes). A subtraction of one would have
shifted every existing `V_intr[1,:]`; a lookup of the value 1 in `[0, 1, 2]` is the index 1, which is
exactly what it always meant. Asserted both ways, and the whole Harmonica suite is green.

It DOES change kernel B's own `[freq, port]` diagnostics cubes (`planar.Gamma`, `planar.Zc`, …), where
`[:, 1]` now means port 1 rather than index 1. That is the fix, not a side effect: those axes carry
port numbers too, and the two spellings disagreeing was the defect.

---

### 7. The CLI gained the same thing, because it writes the same document

`plot` gains `--radial`, `--db-floor`, `--db-ring`, `--db-ref` and `--db-unit`, all of which are five
fields on the plot container and none of which draws anything — `src/Cli/PlotVerb.cs` stays argument
parsing, refusals and reporting. The gate is the contract that verb already had: byte identity between
the picture `plot` draws and the picture `render` draws from the document `plot` itself wrote.

Byte identity alone would be satisfied by two empty pictures, so
`TheDataDisplay_ResolvesTheDocumentTheVerbWrote` loads the written `.cdd` through `PlotConfigLoader` —
the loader the WINDOW uses — and checks each point against the cube read directly, compass mapping and
all.

The two trace spellings are shorthand over the general slice mechanism and produce ordinary bracket
text before anything else sees them: `cut=<deg>` pins `phi` and sweeps `theta`, `cut=all` keeps every
`phi` as a family. **`cut=` and `freq=` resolve by VALUE and the verb PRINTS what it landed on** — a
cut silently moved to a neighbouring sample is a plot of a different plane, and a mark read without its
scale is the defect `sweep-unit-scale-and-mark` already records elsewhere.

Every dB flag is refused when `--radial db` was not given, rather than doing nothing — `render`'s own
rule, that a flag which did nothing leaves a caller with a picture it cannot tell from the one it asked
for. A positive `--db-floor` is refused naming the sign convention rather than being flipped: the floor
is measured RELATIVE to the outer ring, and a caller who typed 40 may have meant a 40 dB disc or may
have meant something else entirely.

---

### 8. Found and NOT fixed: ANT-4's and ANT-5's cubes carry no `Unit`

`PlanarKernel.AddFarField` and `AddMetrics` set no `DataCube.Unit`, so `farfield.U` (W/sr),
`farfield.GainDbi` and `farfield.DirectivityDbi` all come back with an empty one. **ANT-6's four cubes
DO carry theirs** (`"dB"`, `"1"`).

The consequence here is small and visible: with nothing to read, a pattern plot's radial numbers fall
back to a bare `dB`, and `dBi` has to be said with `--db-unit` or the inspector's unit box. That is
honest — a bare `dB` where `dBi` would be a claim — but it is a gap in ANT-4/ANT-5 rather than a
property of the display, and setting those units is a one-line change per cube in the engine that this
brief is not the place to make. `Trace.CubeValueUnit` is stamped by `TraceResolve` from the cube and
will pick them up the day they are set, with `PatternDbUnit` turning a linear unit into `dB(W/sr)`
when a dB transform is applied to it.

---

### 9. Gate summary

`tests/Ui.Tests/DataDisplay/PatternPlotTests.cs` (15) and
`tests/Ui.Tests/Cli/PatternPlotCliTests.cs` (10), about 1.4 s together, none tagged.

- §1's three claims, on the real `"farfield"` cube, each reported.
- The ring lattice, floor and label text on a high-directivity pattern (cos⁴⁰ θ), a near-isotropic one
  (1 dB of variation — which is why the floor is a control: a 40 dB disc spans 0.025 of the radius
  there and a 3 dB disc spans 0.333), and one with a deep null.
- Normalised and absolute both render and both say which; an absolute reference below the data reports
  the count it flattened.
- Two cuts share one reference (port 2 of the fixture is 20 dB down and stays 20 dB down).
- A cut trace and a full-pattern trace resolve from the Data Display AND from `Cli plot`, and the
  CLI's picture is byte-identical to rendering its own document — both spellings.
- The port axis takes a 1-based port number, refuses one the run lacks by name, and leaves a 0-based
  `port` axis meaning what it always meant.
- The hemisphere note changes when the axis does.
- Both themes draw, with all four rings, every ring's dB number, and the reference sentence in the SVG.

---

## A polar cut drew a QUARTER of the disc — the back branch (2026-09-10)

Found running the imported 1.74 GHz patch board end to end (owner request; `src/Engine/Mom/RESOLVED.md`
carries that run's engine half). Owner instruction to fix, after it was reported as a design question.

### What was wrong

`cut=<deg>` pins one φ and sweeps θ 0…90°, so on the compass mapping the curve occupies **one
quadrant** — top to right — and nothing occupies the other three.

**`PlanarBeamwidth` has defined a cut as BOTH azimuths since ANT-5**, in its own words: *"a cut runs
from −θ_max through broadside to +θ_max, and the negative half is the φ + 180° branch — so BOTH have
to be sampled"*, and it refuses when the grid carries only one of them. So the number and the picture
were about different things: **a beamwidth read off the plot was half the one the metric published.**

ANT-7 §4 had already anticipated the right shape — it asks for the hemisphere note because *"a polar
plot occupying a half-disc will otherwise be read as a rendering bug"* — but §3 spelled a cut as "pin
freq and φ, sweep θ", and §8's write-up never returned to the second branch. Nothing was measured
wrong; one half of the plane was simply never drawn.

### The shape of the fix, and the two things that are forced

`Trace.MirrorPatternAngle` — one bool, read at exactly one place (`PatternPoint` negates the angle
before `PolarPatternAngle.Point`), persisted as `TraceConfig.MirrorPatternAngle`, **absent and
therefore false in every `.cdd` written before it**, which is the picture those files already drew.

- **TWO TRACES, not one trace with a signed axis.** The halves are different SLICES of the cube — φ
  and φ + 180° — so no single slice could carry both. Synthesising a signed-θ axis needs a derived
  cube and a second resolve path, which is what ANT-7 §3's "there is ONE plotting path" exists to
  prevent. The back branch is therefore an ordinary cube trace through the ordinary mechanism; it
  takes the front branch's COLOUR, because the two halves are one curve, and keeps its OWN label,
  because `phi=180 deg` is what tells the reader where the negative half came from.
- **The ANGLE is mirrored and the VALUE never is.** The radius stays the branch's own dB against the
  plot's shared reference, so an asymmetric pattern reads as asymmetric. Gated by resolving the same
  slice twice, mirrored and not, and asserting one is the other reflected in x.

`Cli plot` adds the back branch **only on a dB-radial polar plot**, and that is not caution: on a
LINEAR polar plot nothing reads the flag, so the second trace would draw a duplicate curve on top of
the first; on a RECT plot the negative half is a question about the x axis rather than about an angle.
`cut=all` is excluded because a family already carries every azimuth, the back branch among them.

### The caption had to learn it, and the hemisphere statement did NOT change

`PolarPatternAngle.HemisphereNote` gains `bothBranches`. The hemisphere half is untouched — both
branches are θ from the zenith and both are above the plane — but the SPAN is now −90…90°, and
"θ 0…90°" printed under a half-disc invites the reading that the other half is a different quantity.
It is the same θ at the opposite azimuth, and the sentence now says so. **The flag is asked of the
PLOT, not of the trace**, so either authoring order gives one sentence and the existing dedupe still
collapses the pair to one line.

### A second defect, caught while building the first

**`DataDisplayViewModel.BuildTraceConfig` did not write the field back.** The renderer reads it, so a
`.cdd` from `Cli plot` draws the whole plane in the window — and the first SAVE returned it to a
quarter-disc with nothing said. **A field the window can DRAW but not SAVE is worse than one it
cannot draw**, because nothing about the moment it is lost is visible. Gated through
`BuildTraceConfig` and the loader's own JSON rather than a hand-copied pair of fields, which would
agree with itself and prove nothing.

### Two presentation defects the first half-disc exposed

Both were pre-existing shapes that only a two-trace cut could reach.

- **The caption CLIPPED rather than shrank.** `AxesRenderer` shrinks a caption line to fit and floors
  that at 0.5× — so a sentence long enough to need more than half was cut mid-word at both ends, which
  is what a folded-in "the negative half is the φ + 180° branch…" produced at 792 points. The
  statement is its OWN LINE now, and it is also a different statement: the θ AXIS still runs 0…90°,
  which the hemisphere line reports, and what spans −90…90° is the COMPASS.
  *Not changed, and visible in the picture:* each caption line shrinks INDEPENDENTLY, so a short line
  between two long ones renders larger than both. One size for the whole block would read better and
  would move the bytes of every shipped pattern figure; left alone deliberately.
- **The Y-axis label strip printed the quantity twice.** `PlotLabelStrips` emits one strip per trace,
  and the back branch is a second trace carrying the same quantity on the same axis — one curve in two
  traces, not two quantities. Excluded like a contour trace is, **unless it is the only thing on that
  side**, where the reader must still get an axis name.

The trace card gains the checkbox to match (`ShowPatternMirror`, live only on a dB-radial polar plot
on a cube-bound trace — the same gate `PlotVerb` applies, for the same reason), so the window can
author a plane and not only render one.

---

## The 3D surface, as first used in anger — four reports (2026-09-11)

All four came from the owner driving ANT-10's surface on the imported patch board, and the first is
the one that matters.

### 1. `Etheta` drew a uniform pink hemisphere, and a hemisphere is not an error shape

`Etheta`/`Ephi` are COMPLEX cubes. With no transform `Trace.RectY` returns the LINEAR magnitude —
volts, peaking near 0.01 on a real patch — and the surface reads its values as **dB** against a peak
reference with a −25 dB floor. A span of 0…0.01 "dB" puts every direction on the outer radius in the
top colour: **a perfect hemisphere, which is what an isotropic radiator over a ground plane looks
like.** Nothing in the picture could have said otherwise, which is the whole reason it had to be a
refusal rather than a nicety.

`Trace.PatternValuesCanBeDb` decides it, and **the test is exact rather than a guess about
magnitudes**, which matters because the obvious heuristic ("these numbers are too small to be dB")
would have been a rule of thumb wearing a measurement's clothes:

- a dB transform, or one baked into an expression → dB, drawn;
- `Mag`/`Real`/`Imag`/`Phase`, **and `None` on a COMPLEX cube** (which `RectY` resolves to the
  magnitude) → provably not dB, **refused by name** with the flag that answers it;
- `None` on a REAL cube → left alone, because that is how an already-dB cube (`GainDbi`,
  `CoPolLudwig3Db`) is legitimately plotted.

**So a linear REAL cube — `U` with no transform — is still drawable and still wrong.** Named here
rather than guessed at: the fix for that case is a unit on the cube, which §8 above already records
as missing. The surface records `Trace.SurfaceComplexSource` before flattening its values to
`double`, because after that line the trace cannot tell the two kinds apart.

Applied to the polar cut as well, where the same category error draws a CIRCLE.

### 2. The refusal was drawn over the scene furniture

The ground disc, the axes and their letters were drawn whatever the trace resolved, so the sentence
landed on top of them, struck through by an axis arm. They are the SURFACE's frame of reference; with
no surface they are furniture around an empty scene, and they are now skipped.

### 3. A 3D plot could not be SELECTED

`PlotControl.OnPointerPressed`'s surface branch takes the left press for its rotate drag and marks it
handled, so it never reached `PlotContainerView` and the container's click-to-select never armed. The
note beside `_surfaceRotating` had reasoned that there is "no axis window here for the move/select
conflict of `Axes.LockedPanning` to be about" — **true about the axis window and wrong about the
container**, whose SELECT gesture is on that same press.

**Selection is not a drag**: it is decided by a press that does not move, so the two can share the
gesture. The press now selects through the container's own `RequestSelectOnly`/`RequestToggleSelect`
— the same commands the container calls, not a second selection path, which is what keeps Ctrl/Cmd
toggling identical to every other plot type — and then takes the rotate. Gated by a source scan in
`PlotPanLockDefaultTests`, that file's own house pattern for control wiring it cannot instantiate.

### 4. The colour ramp was clipped at the inspector's right edge

Eight controls in one row were wider than the inspector. Split on the owner's own suggestion, and the
two rows divide cleanly: the first answers *which way am I looking*, the second *what is in the
scene*.

### Reported and NOT reproduced: "cannot rotate in realtime due to poor FPS"

**Measured in a scratch harness** (Release, `SurfaceRenderer.Draw` on the default 1°×1° hemisphere,
91 × 360, 20 frames each, camera rotated between frames):

| detail | stride | facets | ms/frame | fps |
|---|---|---|---|---|
| `Quick` (pointer down) | 4 | 3,822 | 5.50 | 182 |
| `Full` (on release) | 1 | 61,920 | 54.84 | 18 |

and `Quick` is **not fill-rate bound** — at 1520 × 1120 it is 6.87 ms (146 fps) and at 2400 × 1800
it is 8.09 ms (124 fps), so a Retina canvas at any plausible container size stays well inside a
frame. Those are ANT-10 §6's own claimed numbers, reproduced. **The drag path is therefore not slow
in the renderer**, and the cause is somewhere this harness does not reach — `PlotDetail.Quick` not
arriving, or the compositor being starved by something else on the tab (this repository has a
recorded finding that one slow frame starves the whole window). Left open and reported rather than
guessed at.

---

## Antenna display feedback, round 2 (owner, 2026-09-11)

Six reports against the pattern display, and four of them landed below the firewall. Detail on the
two that are pure view-model/XAML is in `src/Ui/RESOLVED.md`; the efficiency cube's own change is in
`src/Engine/Mom/RESOLVED.md`.

### 1. A 3D pattern plot could not be picked up and moved

A 3D pattern plot could not be dragged anywhere: every press rotated it.

`PlotControl.OnPointerPressed` took **every** left press on a `Surface3D` plot as the start of a
rotate and set `e.Handled`. A handled press never reaches `PlotContainerView`, which is what moves a
plot — so there was no press anywhere on the plot that could move it, and no combination of
modifiers either. The rotate gesture was added in ANT-10 and the collision was never noticed because
nothing else on that plot type wants a press.

The fix is a hit test, `SurfaceRenderer.HitsPattern`, and the interesting part is that it had to be
**the renderer's own arithmetic** rather than a plausible rectangle: the scene square is what is left
of the canvas after the title, the caption block and the colour bar are taken out, and the caption
block's height depends on the trace LABEL (which depends on the alias resolver and on the
show-source preference). So `Draw`'s layout was extracted into `LayoutOf` and both callers read it;
a second copy would be a pointer that rotates a pattern it is not over, which is invisible until
somebody drags.

**The square, not the lobe silhouette.** The ground disc and the scene axes are the surface's frame
of reference and turning the object by them is what a reader expects, and a pattern with a deep null
has canvas inside its own outline that belongs to it.

**A plot that resolved no surface never rotates** — the owner's own second sentence, and it falls out
of `FirstSurface(plot) is null` rather than being a special case.

### 2. The Y-axis label toggled when the back-half checkbox moved

Ticking the back-half checkbox made the Y-axis label appear and disappear with it, and the report
asked whether that was intended.

No. `PlotLabelStrips.Labelled` filtered on `Trace.MirrorPatternAngle` directly — "a back branch is
one curve with its front half, so it does not name the axis twice" — so ticking the box DELETED that
trace's strip whatever the trace was. The intent was right and the test was wrong: the ordinary way
to build a cut is two traces pinned at φ and φ + 180°, whose labels DIFFER, and those are two
genuinely different slices the reader needs both of.

It now drops a back branch only when its label is **already on that axis**, which is the
one-curve-in-two-traces case the rule was written for and is the only case where anything is being
said twice. Stable under the checkbox in both directions.

**Deduping on the label alone was tried and reverted before it shipped** — `Add Trace` clones the
selected trace, so two ordinary traces of one quantity are extremely common, and giving them one
strip between them is a worse bug in a much commoner place. `DataDisplayMultiFileUiTests
.SwitchingTraceSource_RefreshesLabelStripsImmediately_AliasQualified` is what caught it.

### 3. The three sentences under every pattern plot are gone

All the text under a far-field plot — on both the polar cut and the 3D pattern — was reported as
distracting and asked to go, with the polar plot to carry its angle axis instead: θ or φ, whichever
it is swept in.

ANT-7 §4 put five statements there: normalised or absolute, the reference value, 0° at the top, that
a cut is a plane, and what the θ range means. **What it cost is worth recording rather than hiding**,
because §4 was not arbitrary: the reference sentence is the one thing on a normalised pattern that
says a 0 dB peak is 0 dB relative to itself. It is not lost —
`PolarPatternScale.ReferenceCaption()` still composes it, the rings still carry their own numbers,
and the unit is on the outer one, which is where a reader looks for a level anyway. The rest was
restating the label strip beside the plot (the cut, the port, the frequency) or the picture itself.

What replaces it is the one thing the picture could NOT say: **which angle the compass is**. A θ cut
and a φ cut are the same disc with the same rings and different meanings. A 3D surface gets nothing
at all — its angles are the scene's own drawn axes — but it keeps the trace IDENTITY line, which is
the label a surface has no strip for rather than a caption.

### 4. θ and φ, from one mapping

`AxisSymbols.Display` is the whole of it, and it lives here because BOTH halves read it: the trace
card's axis rows (`src/Ui`) and the pinned-axis tokens the Y-axis label and the marker readouts are
built from. Two copies would drift, and the entire point of a symbol is that the card and the axis
agree. **Display only** — a spec, a slice, a `.cdd` and a refusal all keep the ASCII name, which is
what a user types.

`cut` is deliberately NOT mapped to φ: it is a beamwidth cut's plane and putting two different axes
under one symbol on one card would be worse than the ASCII name.

### 5. "port=1" on a one-port antenna

Dropped when the axis has ONE value — the test is the axis LENGTH, not the value, because "port=2"
on a two-port run is the whole point of the token. Confined to `port`: a single-frequency sweep still
names its frequency, since WHICH frequency a pattern was taken at is the first thing a reader needs.

**Two traps, both hit while building it.** The suppression is an EMPTY token in the pinned-axis map
rather than a missing entry — `TraceLabeler` reads a missing entry as "the owner resolved nothing"
and falls back to the raw INDEX, so the first attempt printed `port=0`. And the skip has to happen
**before** the bracket is opened: `first` is what decides whether a closing paren is written, so a
`continue` after `sb.Append(first ? '(' : ',')` produced `farfield.GainDbi(` with nothing in it.

### 6. A percentage says so on the axis

`RadiationEfficiency` is published in percent now (see `src/Engine/Mom/RESOLVED.md`), and
`TraceLabeler.BuildCubeQuantity` appends ` (%)` to a cube whose unit is `%` and which is drawn
untransformed. It is in the LABELLER rather than in `RectYLabel` because that one label reaches the
rectangular Y axis, the polar label strip and the marker readouts, and a unit on one of the three and
not the others is the drift the labeller exists to prevent. A transform replaces the scale and
therefore the unit, so `dB10 (%)` is never written — which is also why the decibel form is its own
published cube rather than a transform of this one.

### 7. The angle label printed over the 180° bearing

With "Angles" on, the θ (deg) label under the plot was drawn on top of the 180° bearing.

Two things are drawn under a pattern plot and neither knew about the other. The bearings live in the
margin the **viewport** reserves for them (`PlotRenderer.ComplexAngleLabelMargin` shrinks the disc,
so "180" sits *below* `vpBottom`); the caption is placed from `vpBottom` too, with only its own line
height for an offset. Measured at the 420-point default box: the bearing baseline is 406.7 and the
caption was at 404.9 — the same place.

`AxesRenderer.PolarBearingDropPx` is the fix and it is `DrawPolarBearings`' own arithmetic for the
one bearing that matters, so the two cannot drift: `gap + 2·halfH` plus the glyph's descent. The
caption now starts below that. With "Angles" off it returns zero and nothing moves.

**The height reservation is the other half, and forgetting it would have traded an overprint for a
line off the bottom of the canvas.** `PlotCanvasGeometry.BottomLabelExtraLogical` mirrors the
renderer's layout and now mirrors this too — and while there, it gained the bearings' own margin in
its `natural` term, which it had never accounted for: with "Angles" on the disc is *smaller*, so
there is more room under it than the no-bearings formula assumed.

Gated by `AntennaFeedbackRound2Tests.TheAngleLabel_ClearsTheBearingRing_WhenAnglesAreOn`, which reads
the two baselines out of the real SVG the application's own writer produces — and which was verified
to fail (413.0 against 406.7) with the offset removed, rather than assumed to catch it. The
conditionality is asserted on `PolarBearingDropPx` directly and NOT by comparing the two pictures'
y values: with the bearings off the disc keeps the margin they would have taken, so the label sits
*lower* in absolute terms (436.1 against 431.1) while nothing at all has been added to it.

---

## A farfield.U plot could not be built from the Plot Inspector (2026-09-11)

One report, one root cause, and three things around it that made it hard to see.

### THE BUG: the spec box printed text the spec box refused

Two 3D plots in the reporter's own `.cdd`, the same expression on both, one `<invalid>`. The working
one was authored through the PICKER and carries a `CubeSlice`; the broken one carries only the
expression, copied from the other's spec box — and that text does not parse:

```
dB10(farfield.U[42, :, ~, 0])   →   "Port 0 is not a port of axis 'port' (it has 1)."
```

`SliceTokenParser` reads an integer on a `port` axis by matching the axis's own **values** — a port
NUMBER, never an index — and `Trace.BuildPickerExpression` was writing the **index**. So the card
displayed a spec that its own parser rejected. **On a one-port antenna that is every trace**, which
is why it took a second plot to surface: the picker path never reads the text it shows.

The `i`/`j` axes beside it were already right (`s.Index + 1`), which is what made this look
consistent at a glance. They can do the arithmetic because they are 1-based by construction and the
parser reverses it; a `port` axis carries whatever port numbers the run drove, so the VALUE is
resolved from the cube — `Trace.PinnedAxisSpecToken`, stamped by `TraceResolve` in the same walk that
stamps the display tokens, with `index + 1` as the fallback when nothing was resolved.

**The gate is the round trip, not the spelling**: `BuildPickerExpression` → `TryParse` → the same
slice back, which is the invariant that was silently false.

### Should farfield.U always be plotted as dB10?

**Not always dB10 — always dB, and which dB depends on the quantity.** That is exactly why a picker
is needed rather than a constant: `U` is a radiation intensity and its dB is 10·log₁₀,
`Etheta`/`Ephi` are FIELDS and theirs is 20·log₁₀, and `CoPolLudwig3Db`/`AxialRatioDb` are already in
dB and take none. One rule for the three is wrong twice, by a factor of two in dB, silently.

`TraceRowViewModel.DefaultPatternTransform` decides in that order: already-dB (by unit, or by this
repository's own `…Db`/`…Dbi` cube-naming, which is what carries the answer for a file written before
ANT-7 §8 published units), then field-vs-power by unit, then by `DataKind` — complex is a field, real
is an intensity. Exact for every cube that matters.

It applies on **every plot type**, not only a pattern plot — a follow-up asked for the dB default to
apply the first time `farfield.U` is picked on a trace card, wherever that card is. The **group**
decides, not the bare name: a cube called `U` outside `farfield` is not necessarily a radiation
intensity.

### The transform picker was missing on a pattern plot

There was no transform picker for `farfield.U` anywhere. The combo was `IsRectOrTablePlot`-gated, so
on the one plot type whose radius **must** be decibels
the only way to set a transform was to type it into the spec box — the box that was printing an
unparseable spec. It is shown on a pattern plot now, offering the three dB entries, plus `None` for
REAL data (which is how an already-dB cube is legitimately drawn) and nothing else: a magnitude or a
phase cannot be a radius on a dB scale.

### The legend stopped rendering when the Data Display zoomed out

Reported 2026-09-11. `barW` was gated on `baseSize >= 4f` — a rule about when TEXT is worth the ink,
applied to the whole colour bar — so a zoomed-out plot silently lost the one thing that says whether
a colour is the peak or the floor. The bar is drawn at every canvas size now and its numbers stop shrinking at
`MinLegendTextPx` rather than vanishing: small is legible once the user zooms back in, absent is a
picture whose meaning changed.

`Plot.SurfaceShowLegend` (default true, persisted, "Legend" on the Plot Inspector's 3D row) is a
setting because the bar is the one piece of chrome that costs the scene its width.

### The identity line moved from under the scene to the title

Asked for on 2026-09-11: remove the bottom caption, make its text the plot's DEFAULT title, and let
the user's own axes title override it. `SurfaceRenderer.TitleOf` — the user's title when
`CustomTitleOn`, the
trace's identity otherwise. Keyed on the FLAG rather than on the text being blank, so an empty custom
title is how the line is turned off; a blank falling back to the default would make that unreachable.

Nothing is drawn under a 3D pattern now, and `capH` is zero rather than a bare `2·lw` when the
caption block is empty, so the scene gets that space back.

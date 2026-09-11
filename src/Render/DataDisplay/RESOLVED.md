# src/Render/DataDisplay — resolved briefs (detail, off the CLAUDE.md growth path)

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

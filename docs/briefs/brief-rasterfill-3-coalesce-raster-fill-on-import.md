# Brief — RF3: a painted pour should arrive as a region, not as 29,000 scanlines

**Series:** `brief-rasterfill-0-overview.md` §1a and §2.
**Scope:** `src/Design/Layout/Interchange` — the Gerber (and board) readers, plus one new geometry
helper. **Nothing in `src/Render` changes.**
**This is the cheaper of the two ways to attack the 77%.** Read §2 of the overview for why it comes
before the tiled raster cache.

---

## 0. The finding

`GerberReader.Regions.cs:276` states the reader's fidelity rule:

> primitive-for-primitive — a stroke stays a `PathShape`, a circle flash stays a `CircleShape`

That is the right default and it is not what is being questioned. What it does not anticipate is
**raster fill**: a CAM tool that expresses a solid copper pour by *painting* it with thousands of
abutting parallel strokes rather than emitting a region (G36/G37). On board A:

| | |
|---|---|
| Strokes 25,400 DBU (1 mil) wide | **46,823** |
| "bottom copper" | 28,921 strokes, **142,029 mm** of centreline, median **2 vertices** each |
| "gnd" | 12,499 strokes, **179,382 mm** of centreline |
| Scanline pitch | 22,860 DBU against a 25,400 DBU width — abutting, with 10% overlap |
| Stroked area vs board area | 4,556 mm² of "gnd" strokes over a 2,600 mm² board |

Two layers hold a pour each. They are **77% of a 283 ms frame** (overview §1c), and the cost is
rasterization of painted area, which no renderer tier can reduce.

**The renderer is not the only victim.** Every consumer sees 41,420 strokes where the board has two
pours: `PlanarExtractor` now correctly treats a width-bearing path as metal (ANT-1), so the EM mesher
meshes all of them; the DRC engine evaluates clearances against all of them; the exporters write all
of them back out. One filled region is the shape every one of those wants.

Board C in the overview is the control: **2,603 dense polygons in a 27 MB file render in 13.5 ms**,
because `LayoutRenderDetail`'s vertex-decimation tier is built for exactly that. The target shape is
already a shape this codebase is fast at.

## 1. The approach

**Detect a painted region, replace it with the region it paints.** Concretely: for each layer, take the
strokes whose stroked outlines overlap one another, union them, and emit the union as `PolygonShape`s
(with holes) instead of the strokes.

**R-rf3-1. The union is computed in ONE pass, never pairwise.** Pairwise `SKPath.Op` over 29,000
shapes is quadratic and will not finish. Build a single `SKPath` containing every candidate's stroked
outline — which is what `LayoutRenderer.BuildPathOutline` already does per shape — and take
`SKPath.Simplify` over the whole thing. Simplify resolves overlapping contours to their non-self
-intersecting union in one pass, which is precisely the operation wanted, and it is already the
primitive the renderer's own merge tier leans on. SkiaSharp is permitted in `src/Design`
(`tests/Firewall.Tests`), and `LayoutTextOutline` is the existing precedent for doing geometry with it
on that side of the line.

**R-rf3-2. Read the union back as `PolygonShape` with holes, in DBU.** A pour with cutouts is one
outer contour plus inner ones; the `.clay` polygon model already carries `Holes`. Contour direction
decides which is which — do not guess from area.

**R-rf3-3. Coalescing is grouped by everything that makes two strokes interchangeable**, and by
nothing else: same layer **and** same polarity. Gerber dark/clear (LPD/LPC) is not decoration — a
clear stroke *removes* copper, and unioning it with a dark one paints the hole solid. If the reader's
current model does not carry polarity to this point, that is a reason to stop and report, not to
assume every stroke is dark.

**R-rf3-4. Coalesce only where it is a simplification, and decide it by counting, not by heuristic.**
Compare the candidate group's stored vertex count against the union's. Replace only when the union is
materially smaller — a single trace is a `PathShape` and must stay one. This makes the tier
self-limiting: an ordinary board with no painted pours has no group that passes, and nothing about it
changes. **Do not gate on a stroke-count threshold alone**; a 300-stroke pour deserves the same
treatment as a 30,000-stroke one, and a 30,000-trace board with no overlap deserves none of it.

**R-rf3-5. The geometry is exact to a stated tolerance, and the tolerance is stated.** DRC measures
clearances against these shapes and EM meshes them. The union of stroked outlines is exact in
principle; what is not exact is the arc flattening in a round end cap, which already has a tolerance
concept in the model (`PathShape.FlattenTolDbu`). Use it, carry it, and say in the import report what
it was.

**R-rf3-6. The import reports what it did, per layer, as a normal import note.** "layer *X*: 28,921
strokes coalesced into 7 regions" is the line. A silent structural change to someone's artwork is not
acceptable even when it is an improvement — and this is the line that makes the whole feature
diagnosable from a log when a board comes back looking wrong.

**R-rf3-7. It can be turned off.** One flag on the import, off-by-default meaning "coalesce", and the
`convert` verb exposes it. A user who needs the primitives exactly as authored — comparing against the
CAM source, chasing an import bug — must be able to get them, and the tests in R-rf3-9 need it to pin
the un-coalesced geometry they compare against.

**R-rf3-8. `convert`'s existing byte-identity gates move with it, they do not get an exemption.**
`tests/Ui.Tests/ConvertCliVerbTests.cs` compares the CLI's output byte-for-byte against the in-process
`GdsiiExport`/`GerberExport` the GUI calls. Both sides change together here, so the gate stays
meaningful — but the *expected* bytes change, and that must be a deliberate, reviewed update rather
than a re-baseline.

## 2. What not to do

- **Do not do this in the renderer.** A render-time coalescing cache leaves EM, DRC and export looking
  at 41,420 strokes, and adds a second geometry pipeline to keep in step with the first. If the
  decision is that stored geometry must not change, the answer is brief 4, not this brief done in the
  wrong place.
- **Do not union across polarity.** R-rf3-3. It fills holes with copper, silently, and the picture
  looks plausible.
- **Do not union across layers**, however identical the geometry looks. Board A's two pours have the
  same bounding box and different layers, and they are different copper.
- **Do not use pairwise boolean ops.** R-rf3-1. It will appear to work on a test fixture and hang on a
  real board.
- **Do not infer "this is a pour" from stroke width, aperture name, or layer name.** Width is how the
  raster was painted, not what it means; names are not portable. Overlap and vertex count are
  measurable and are the whole test.
- **Do not discard `End` (cap style) when building the outline.** A butt-capped and a round-capped
  scanline union to different regions at the pour boundary.
- **Do not re-emit the result as one giant polygon when the pour is disjoint.** Separate regions are
  separate shapes; merging them creates a shape whose bbox defeats culling for all of them.

## 3. Gates

None of these assert a millisecond figure; each asserts geometry or a counter.

1. **Area is preserved.** A synthetic pour — a rectangle painted as *N* abutting strokes — coalesces to
   a region whose area equals the rectangle's, to the R-rf3-5 tolerance. Vary the overlap from 0 (just
   touching) to 50%.
2. **Holes survive.** A pour painted around a cutout produces a polygon with a hole of the right area,
   not a solid one. **Write this before the union code** — it is what R-rf3-3 fails.
3. **Clear polarity is not absorbed.** A dark pour with a clear stroke across it coalesces to a region
   with a slot, or refuses; it never coalesces to a solid.
4. **Rendered output is unchanged within tolerance.** Pixel-coverage comparison of the coalesced
   document against the un-coalesced one (R-rf3-7's flag gives both), at two zooms. The same oracle
   `LayoutRenderDetail`'s density gate uses.
5. **A board with no painted fill is bit-for-bit unchanged.** R-rf3-4's counting rule means an ordinary
   import must produce an identical `.clay`. Assert the file, not the picture.
6. **A single trace stays a `PathShape`.** The degenerate case of gate 5, called out because it is the
   one a wrong threshold breaks.
7. **Shape count collapses on the real class of input.** On a raster-fill fixture, assert the post
   -import shape count is below a fixed ceiling. A counter, not a clock — and it is the gate that
   proves the feature did anything.
8. **The import note is emitted** (R-rf3-6) and names the layer and both counts.
9. **DRC agrees.** A clearance the DRC engine reports on the un-coalesced document is reported
   identically on the coalesced one. This is the assertion that makes R-rf3-5's tolerance claim real
   rather than stated.

## 4. Fixtures

**Do not commit a vendor board.** Build the fixtures synthetically — a painted rectangle, a painted
annulus, a painted region with a clear slot, and one ordinary traces-and-pads board — which is also
the only way gates 1–3 can assert an exact expected area. The real boards this was measured on stay
outside the repo, and nothing in these briefs, the tests or the code names them.

## 5. On completion

1. Record in **`src/Design/RESOLVED.md`**: the shape-count and vertex-count before/after per layer on
   the raster-fill class, the R-rf3-1 one-pass-union decision and why pairwise is not viable, the
   polarity trap (R-rf3-3), and the coalescing rule from R-rf3-4 with the measurement behind it.
2. Note the changed expectations in `tests/Ui.Tests/ConvertCliVerbTests.cs` and **why** (R-rf3-8), so
   the next reader does not take it for a re-baseline.
3. **Do not write any of this to a `CLAUDE.md`.** The sibling `RESOLVED.md` is where findings go.
4. Re-measure the overview §1b table on a coalesced board and report it — it is what decides whether
   brief 4 is still worth building.

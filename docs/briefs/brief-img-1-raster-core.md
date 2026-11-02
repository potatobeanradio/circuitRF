# Brief IM-1 — The raster core

**Series:** `brief-img-0-overview.md` (D1, D2, D16, D17) · **Tag:** `R-im1-<m>`
**Depends on:** —
**Area:** new `src/Design/Imaging/`, new `docs/design/image-to-circuit.md`, new `tests/Ui.Tests/Imaging/`

---

## 1. Goal

Pixels in, geometry out — with no circuit knowledge at all. Every later phase reads a picture only through this
project folder, so one decoder, one clustering and one skeleton serve the layout reader, the schematic reader and the
text reader alike.

## 2. Requirements

**R-im1-1 — Decode.** `RasterImage.Decode(ReadOnlySpan<byte>)` and `.Load(path)` through `SKCodec`: PNG, JPEG, BMP,
GIF (first frame), WebP. EXIF orientation applied (`SKCodec.EncodedOrigin`). Alpha is composited onto **white** (a
screenshot with a transparent background is a drawing on paper). Output: an sRGB RGBA8 raster plus the file's stated
resolution, if any (PNG `pHYs`, JFIF density) — recorded, never acted on (D6 (5)). An undecodable input returns a
refusal naming the formats that are read and saying *export it as PNG*; TIFF and HEIC are named in that sentence
because they are what a phone or a screenshot tool most often produces. Above 50 megapixels the raster is reduced
(area-averaged) to 50 and the factor is returned for the note (D16).

**R-im1-2 — Colour.** sRGB → CIELAB. `ColourClusters.Find(raster, maxK = 8)`: k-means++ with a **fixed seed** on a
deterministic stride sample (at most 250 k pixels), k chosen by the elbow of within-cluster error, then clusters closer
than ΔE 10 merged. Anti-aliased edge pixels are not forced into a cluster: each pixel is described as a mix of its two
nearest cluster colours, which gives one **coverage map** (0…1 per pixel) per cluster. Coverage, not labels, is what
contours are traced on — that is where sub-pixel accuracy comes from.

**R-im1-3 — Line art.** `LineArt.Binarise` (Sauvola adaptive threshold, window and k as options) for pictures that are
dark strokes on a light ground; `Invert` detection when the ground is dark (the share of dark border pixels).

**R-im1-4 — Morphology and components.** Open/close with a disc of a stated radius; despeckle below a minimum area;
connected components (two-pass union–find, 8-connected for foreground, 4 for background) with area, bounding box,
centroid, hole count and border contact.

**R-im1-5 — Distance and stroke width.** Exact Euclidean distance transform (the separable lower-envelope method).
`StrokeWidth.Estimate`: the mode of 2·DT sampled on the skeleton — the one number the schematic reader scales every
tolerance by.

**R-im1-6 — Skeleton and its graph.** Zhang–Suen thinning, spurs shorter than one stroke width pruned. `SkeletonGraph`:
nodes at end points (degree 1) and junctions (degree ≥ 3, adjacent junction pixels merged), edges as pixel chains;
each edge's chain simplified to a polyline (Douglas–Peucker at 0.5 px) carrying the mean stroke width along it.

**R-im1-7 — Contours.** Marching squares on a coverage map at 0.5, giving **sub-pixel** closed polygons; holes nested
by containment and returned as Clipper2 paths (the library the layout booleans already use). Simplification at a stated
tolerance (default 0.35 px), then **snapping**: an edge within the angle tolerance (default 3°) of 0°/90° — and of 45°
when asked — is rotated onto it about its midpoint and its neighbours re-intersected, so a rectilinear trace comes back
rectilinear rather than as a 0.4° parallelogram. Corners within 2° of 90° become exact.

**R-im1-8 — Primitives fitted.** `Fit.Circle` (algebraic fit refined by geometric least squares) with a residual, so a
via or drill comes back as a circle, not a 40-gon; `Fit.Segment` (total least squares) with its straightness residual.
`Segments.FromSkeleton`: long straight runs of skeleton edges, merged across junctions when collinear.

**R-im1-9 — Determinism (D17).** No result depends on thread scheduling or on a system font. Parallel loops, where
used, partition the raster into fixed bands and combine in band order. A gate asserts the same bytes from two runs and
from two thread counts.

**R-im1-10 — Coordinates.** Pixel space is x right, **y down**, origin at the top-left pixel's corner, pixel centres at
½. Every later phase converts to layout (y up, DBU) or schematic coordinates through one `PixelFrame` (scale, offset,
flip) defined here, so the flip is written once.

**R-im1-11 — Design note.** `docs/design/image-to-circuit.md`: §1 what it is for (from the overview §0–§1), §2 the
pipelines (D3), §3 the decisions table with the *why* of each, §4 the raster core. Later phases add their sections.

## 3. Not in this phase
Any interpretation of what the pixels mean (IM-2 onwards); the GUI.

## 4. Gates (minimal tests, run only these classes)
Pictures drawn **in memory** with SkiaSharp (anti-aliased) — never a committed file:
- `RasterDecodeTests` — a rotated-EXIF JPEG comes back upright; a transparent PNG composites onto white; an
  undecodable byte string returns the refusal naming the formats; a 60 MP raster is reduced with its factor.
- `ColourClustersTests` — a three-colour drawing gives three clusters with the same seed twice; two colours ΔE 6 apart
  merge.
- `ContourTests` — a 37.3 × 12.6 px rectangle drawn anti-aliased comes back within 0.25 px on every edge, exactly
  rectilinear; a rectangle with a hole comes back with its hole; a 1.5° rotated rectangle is **not** snapped when the
  tolerance is 1°.
- `SkeletonGraphTests` — a drawn T gives three edges and one junction; a 3 px stroke reads stroke width 3 ± 0.5.
- `FitTests` — a drawn circle of radius 6.4 px fits within 0.2 px.
- `RasterDeterminismTests` — the R-im1-9 identity.

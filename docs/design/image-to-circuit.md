# Create from Image

**Series:** `docs/sonnet-briefs/brief-img-0-overview.md` (decisions D1–D18) · **Code:** `src/Design/Imaging/` (the
raster core), later `src/Design/Layout/Recognition/Image/` and `src/Design/Schematic/Recognition/` · **Builds on:** the
whole Create Schematic from Artwork series (`docs/design/artwork-to-schematic.md`), the bitmap primitives, the drawn
netlist (`src/Design/Schematic/NetlistSchematic.cs`), AS-10's stroke-glyph reader and the `convert` pipeline.

This note records what the feature is for, the pipelines, the decisions and why, then one section per phase. IM-1
wrote §1–§4; each later phase adds its section.

---

## 1. What it is for

Create Schematic from Artwork turns copper into a schematic. Most of the circuits a user wants to start from are not
copper — they are **pictures**: a datasheet's application circuit, a figure in an application note, a screenshot of
another tool, a die plot, a board rendered by a Gerber viewer. This series reads such a picture and makes:

- **a schematic** — whether the picture is a schematic or a layout;
- **a layout** — when the picture is a layout.

It is a best attempt, like the artwork command: the user cleans up a little afterwards, with the source picture lying
under the result so cleaning up is comparing in place. Many pictures hold no circuit at all, and those fail gracefully
with a sentence saying what was seen.

**It is feasible for the pictures most users will actually have, with no machine learning and no new native
dependency:**

| Picture | Expectation | Why |
|---|---|---|
| A layout rendered by a tool | **Good.** Copper comes back to within ½ pixel; the rest is the artwork pipeline. | Flat colours, sharp edges, rectilinear geometry — classical colour separation, sub-pixel contours and snapping. |
| A machine-drawn schematic | **Good topology, partial values.** Wires, junctions and the common RF symbols read reliably; text when it is clean, a tunable variable when not. | One stroke width, straight orthogonal wires, a small symbol vocabulary. |
| A photograph of a real board | **Partial at best** (optional IM-13). | Perspective, glare, solder mask over copper, parts covering pads. |
| A hand-drawn schematic | **Not a goal.** | Lines are not straight, symbols are not standard. |

What makes it cheap is that the hard back halves already exist: a traced layout feeds the existing artwork
recognition, a recognised schematic is a `TestBench` the existing `NetlistSchematic.Build` draws, an unknown value is
the existing global-variable-plus-`tune` rule, and text is the existing stroke-glyph matcher fed with a skeleton's
centre lines. The one thing a picture never states is **scale**, and D6 treats that the way the repo treats an
unstated Excellon format — never a silent guess.

## 2. The pipelines (D3)

```
picture (file │ clipboard │ placed bitmap)
   └─ IM-2  ImageSource → decoded, oriented, flattened onto white; kind = schematic │ layout │ none
        │
        ├─ layout picture ── IM-3  trace → LayoutShapes on technology layers (+ the underlay)
        │                        ├─ Make Layout    → .clay  (new cell, or into the layout the bitmap sits in)
        │                        └─ Make Schematic → IM-4 → RecognitionInput → ArtworkRecognition (AS-3…AS-6, unchanged)
        │
        └─ schematic picture ─ IM-7 wires/regions → IM-8 symbols → IM-9 text → IM-10 TestBench → NetlistSchematic.Build

every reader of pixels ── IM-1  src/Design/Imaging/ (no circuit knowledge)
surfaces ── IM-5 the dialog · IM-6 entry points · IM-11 `convert` / `recognize` / MCP
```

**A schematic picture does not make a layout directly.** That is Create Schematic from Image followed by the existing
Update Layout from Schematic; the dialog says so in one line.

## 3. The decisions, and why

| | Decision | Why |
|---|---|---|
| **D1** | No machine learning, no native dependency, all in-house: decoding through SkiaSharp's `SKCodec`, every algorithm written here under MIT. The symbol classifier and the text reader sit behind interfaces. An OS text-recognition service is deferred, not rejected. | Nothing to train, ship or license; nothing that behaves differently per platform. The interfaces are where an extension would plug in. |
| **D2** | Everything that decides anything lives below the firewall (`src/Design/Imaging/`, `…/Layout/Recognition/Image/`, `…/Schematic/Recognition/`); `src/Ui` holds the dialog, overlay and entry points only. | A rule living only in the dialog is a rule the CLI does not apply. IM-11's gate is byte identity between GUI, CLI and MCP. |
| **D3** | The pipelines above; the artwork recognition and `NetlistSchematic.Build` are reused unchanged. | Two readers of copper would be two answers; a second schematic writer would drift. |
| **D4** | The source picture is copied into the target cell folder as `<cell>.source.<ext>` and referenced by path, never embedded; provenance records the original file NAME (no path), SHA-256, pixel size and options. | Documents reference images by path (the bitmap primitives' rule); no personal paths in a workspace; a re-run compares the hash. |
| **D5** | The result carries the source picture as a locked underlay at 35 % opacity, placed so every recognised item lies on what it was read from. A setting turns it off. | Cleaning up becomes comparing in place, not flipping between windows. A bitmap is already excluded from export, DRC and EM. |
| **D6** | Scale is never a silent guess. Evidence, strongest first: the placed bitmap's own placement; two picked points and a typed distance with an SI unit; agreeing land patterns (≥ 3 within 3 %); a line of stated Z0; the file's resolution metadata (offered, never chosen). The GUI may default to an inferred scale one click from being changed; the CLI needs `--scale`, and `--scale auto` is refused when the land-pattern evidence is not met. | A wrong scale is off by orders of magnitude while looking fine. A screenshot's 72/96 dpi describes a screen. |
| **D7** | Layers come from colour: at most eight CIELAB clusters, deterministic k-means, ΔE < 10 merged; each mapped by the user to a technology layer, Drill, Board outline, Silkscreen or Ignore. The border-majority cluster defaults to Background. Mappings save as per-user presets. | A picture's colours are its only layer information; pictures from one source always use the same colours. |
| **D8** | The technology is the user's; the report says the stackup was not read from the picture. | A picture says nothing about the stackup. |
| **D9** | A schematic picture can hold R, L, C, ground, port/terminal/connector, transmission line, short and open stubs, diode, and multi-pin devices (cut out as artwork D9). An unmatched two-pin symbol is kind `?`. | The common RF vocabulary; the cut-out is what matching work needs. |
| **D10** | Text is designators, values, net labels and port names only, read by the AS-10 stroke matcher on the skeleton under a grammar; an unread value becomes a global variable with a `tune` entry. | Prose is not needed; a grammar is what makes a stroke matcher reliable; an unknown is a knob, not a failure. |
| **D11** | A T-junction connects; a four-way crossing connects only at a junction dot (option); equal net labels connect; a wire end within half a stroke gap of a pin connects; dangling ends are listed. | The conventions of machine-drawn schematics. |
| **D12** | The schematic is drawn as the picture draws it (exact placement hints to `NetlistSchematic.Build`); Tidy is one combo away. | The user compares the result against the picture under it. |
| **D13** | Ports from port symbols, terminals, connectors and port-like net labels; else the left and right dangling extremes. No port is not a refusal for a schematic picture. | A drawn circuit is useful before it has ports. |
| **D14** | Default target: a new cell. Started on a placed bitmap: over that bitmap, in that document, as one undo step. | The owner's choice; an editor command is not a file write, so the artwork replace rule is not engaged. |
| **D15** | No new verb: picture → layout is a `convert` import; picture → schematic is a `recognize` input; `--overlay out.png`; the MCP tools return the overlay as image content. | The format is the contract; an agent with vision sees what was read and corrects it through the parts CSV or the `.cnl`. |
| **D16** | Refused only when undecodable (naming the formats read, saying *export it as PNG*), smaller than 64 px a side, holding no drawing, a layout with no scale, or nothing recognised. Everything else is a finding with a count. Above 50 MP the picture is reduced with a note. | Graceful failure: each refusal says what to do next. |
| **D17** | Same picture, same options → same bytes on every platform: seeded clustering, no order-dependent parallelism, no system font in any decision. | IM-11's byte identity and IM-12's committed fixtures depend on it. |
| **D18** | Non-goals: hand drawings, vector sources, multi-page documents, handwriting, trained models, stackup inference, coupled lines, device models, and (until IM-13) photographs. | Scope. |

## 4. IM-1 — the raster core

`src/Design/Imaging/`: pixels in, geometry out, with **no circuit knowledge at all**. Every later phase reads a picture
only through this folder, so one decoder, one clustering and one skeleton serve the layout reader, the schematic reader
and the text reader alike. Gates: `tests/Ui.Tests/Imaging/` (pictures drawn in memory with SkiaSharp, anti-aliased —
never a committed file).

### 4.1 Decode and coordinates (R-im1-1, R-im1-10) — `RasterImage.cs`, `PixelFrame.cs`

- `RasterImage.Decode(ReadOnlySpan<byte>)` / `.Load(path)` through `SKCodec`, accepting PNG, JPEG, BMP, GIF (first
  frame) and WebP only — anything else, including a format Skia happens to decode, gets the refusal, so what is read
  does not vary with the Skia build. The refusal names the formats read, names TIFF and HEIC (what phones and
  screenshot tools produce), and says *export it as PNG*.
- Decoded into sRGB RGBA8 (Skia converts an embedded profile), **alpha composited onto white**, then turned upright
  from `SKCodec.EncodedOrigin`. `SKCodec` reports the orientation and leaves the pixels alone, so the eight EXIF cases
  are applied here, on the bytes.
- The stated resolution (PNG `pHYs` in metres, JFIF density in dpi or dpcm, the BMP header) is parsed from the file
  bytes and **recorded, never acted on** (D6 (5)).
- Above 50 megapixels the raster is area-averaged (box filter with fractional edge weights, one destination row at a
  time so the working set is a row) to at most 50 MP, and `ReductionFactor` says by how much.
- **Pixel space is x right, y down, origin at the top-left pixel's corner, pixel centres at ½.** `PixelFrame` (scale,
  offset, flip) is the one conversion out of it; `YUp` lays a picture on a layout with its bottom-left at a point, and
  carrying a Clipper2 path through a flip reverses it so an outer boundary stays positive.

### 4.2 Colour (R-im1-2) — `CieLab.cs`, `ColourClusters.cs`, `CoverageMap.cs`

sRGB → CIELAB (D65, table-driven per channel byte). `ColourClusters.Find(raster, maxK = 8)`:

1. **Sample**: a stride over the pixel index with at most 250 k samples. The stride is bumped until it is coprime with
   the width — a stride sharing a factor with it samples the same columns on every row and misses a thin vertical
   line entirely. Samples are deduplicated by (colour, edge flag) into weights, in sorted key order.
2. **k-means++** for each k = 1 … maxK with a fixed seed and a PRNG of our own (SplitMix64), so the sequence does not
   change with the runtime's `System.Random`. An empty cluster restarts on the farthest sample — a deterministic
   choice, not a draw.
3. **The elbow** reads an error that treats an **edge pixel** (ΔE > 8 against a 4-neighbour) as a *mix* of two
   centres, scored by its distance to the segment between them, and a **flat pixel** by its distance to the nearest
   centre. Without the first, the pink of an anti-aliased red-on-white edge always looks worth a fourth cluster; without
   the second, a flat grey region between black and white would be absorbed as "a mix". k is the smallest whose mean
   error is at or below the noise floor (ΔE² 4), else the first after which one more cluster removes less than 5 % of
   everything clustering removed.
4. **Refine** each centre to the mean of its flat members alone, so anti-aliasing does not pull a colour toward the
   background (the gate checks a red and a blue come back as their exact bytes).
5. **Merge** clusters closer than ΔE 10, closest pair first.
6. **Every pixel as a two-colour mix**: its nearest cluster, and the cluster whose segment with it passes closest to the
   pixel; the mix fraction is measured **in the sRGB bytes**, the space Skia and most tools blend in, so a coverage of
   0.5 sits on the drawn edge. Stored as three bytes a pixel (a, b, fraction in 1/255ths) rather than eight float maps;
   `Coverage(k)` materialises one map on demand. Clusters are ordered largest share first; `BorderShare` names the
   background (D7's default).

### 4.3 Line art, morphology, components, distance (R-im1-3 … R-im1-5)

- `LineArt.Binarise`: Sauvola (`T = m·(1 + k·(s/R − 1))`, window 25, k 0.34, R 128) from integral images; a grey level
  at or below 64 is always foreground, because Sauvola reads the inside of a large solid dark area as background (m and
  s both collapse there). A dark ground is detected from the share of dark border pixels and inverted first.
- `DistanceTransform`: Felzenszwalb–Huttenlocher's separable lower envelope, **exact** integer squared distances,
  measured between pixel centres, with **outside the picture counted as background** — the edge of the picture is the
  paper.
- `Morphology`: erosion and dilation by a disc are thresholds of the distance transform (to background, and to
  foreground), so the disc is true at any radius. Open, close, despeckle.
- `Components.Label`: two-pass union–find, 8-connected foreground / 4-connected background, labels renumbered in raster
  order of first pixel. A component's holes are the bounded background components whose **first pixel's upper
  neighbour** belongs to it — what is above a hole's top edge is the shape enclosing it, never one sitting inside it.
- `StrokeWidth.Estimate`: the mode of `2·DT − 1` over the skeleton's path pixels. The `−1` is because the transform
  measures to the nearest background pixel's *centre*, half a pixel beyond the stroke's edge: a 3 px stroke centred on
  a row has background centres 2 px either side. The mode is a ¼ px histogram's fullest bin, refined to the mean of the
  samples within ½ px of it. An even width reads about 1 px narrow (the skeleton sits on one of the two middle pixels);
  the schematic reader uses it as a scale for tolerances, where that is well inside the slack.

### 4.4 Skeleton and its graph (R-im1-6) — `Skeleton.cs`, `SkeletonGraph.cs`

- Zhang–Suen, each sub-iteration deciding on its input and deleting afterwards, so the result does not depend on visit
  order.
- **Staircase corners removed** before the walk: Zhang–Suen leaves a pixel at the inside corner of a diagonal step
  whose two 4-neighbours already touch diagonally; it has three skeleton neighbours without being a junction. Removing
  it never disconnects anything.
- Nodes: end pixels (≤ 1 neighbour) and junction pixels (≥ 3), adjacent junction pixels merged into one node at their
  mean. Edges are walked from every node pixel, 4-neighbours first; a closed loop with no node is anchored at its first
  pixel in raster order.
- **Spurs** (end-to-junction branches shorter than the stroke width) are pruned, and a node left with two edges is
  dissolved into one edge, until nothing changes. A drawn T is three edges and one junction.
- Each edge's chain is simplified (Douglas–Peucker, 0.5 px) from node centre to node centre, records which chain index
  each vertex stands on, and carries the mean `2·DT − 1` along it.

### 4.5 Contours (R-im1-7) — `Contours.cs`

- **Marching squares at 0.5** on a coverage map sampled at pixel centres, padded with 0, so every ring closes. A saddle
  cell is decided by its corners' mean. Segments are oriented by one local rule (from the crossing where a clockwise
  walk round the cell leaves the inside to where it re-enters), so outer boundaries and holes come out with opposite
  windings in one pass.
- **The crossing is placed exactly on a clean step.** Linear interpolation between two centres is off by up to
  0.09 px, periodically along a slanted edge — enough to read a rectangle rotated 1.5° as 0.9° on its 20 px sides, which
  a 1° snap tolerance then snapped. For a box-filtered step at e, the two coverages straddling the crossing sum to
  `1.5 − (e − first centre)` whichever pixel the edge is in, so where the centre beyond each is fully out and fully in,
  the edge is placed by that sum; elsewhere (a feature thinner than two pixels, a corner) it falls back to
  interpolation. The 37.3 × 12.6 px rectangle gate comes back within its 0.25 px on every edge.
- **Nesting**: the largest ring is an outer boundary, so its winding names the outer sign; each hole goes in the
  smallest outer ring containing one of its vertices (no rings cross). Regions are returned as Clipper2 paths, outer
  positive and holes negative, ordered top-left first.
- **Simplify then snap**: Douglas–Peucker (0.35 px) on the closed ring, cut at its first vertex and the vertex
  farthest from it. Each edge is refitted (total least squares) to the contour points it stands for, less those within
  1 px of its ends, which belong to the corners as much as to it. Then, in this order:
  1. a short edge (< 2 px) between two longer ones meeting near a right angle is an anti-aliased corner's rounding and
     is dropped **before snapping** — its direction is noise, and a noisy 1 px edge within the tolerance of an axis
     would otherwise be snapped;
  2. an edge within the angle tolerance (3°) of 0°/90° (and 45° when asked) is snapped onto it about its fitted
     centroid, with an exact unit direction;
  3. a short unsnapped edge left between two snapped, non-parallel ones is dropped; neighbouring edges on one line
     (within ½°, offsets within 0.5 px) are joined;
  4. a run of unsnapped edges meeting within 2° of a right angle shares one orientation (the length-weighted mean mod
     90°), so its corners are exact. **A snapped neighbour does not fix that orientation**: an early version let it,
     and a spuriously snapped corner edge squared a 1.5°-rotated rectangle onto the axes — snapping by another name,
     past the stated tolerance;
  5. vertices are re-intersected, an axis-aligned line giving its coordinate exactly so a snapped corner is exact
     rather than nearly so; two lines that are parallel or meet more than 3 px from where the contour turned keep the
     turn, projected onto each.

### 4.6 Fitted primitives (R-im1-8) — `Fit.cs`, `Segments.cs`

- `Fit.Circle`: Kåsa's algebraic fit on centred points, refined by Gauss–Newton on the radial distances, with the RMS
  radial residual — a via comes back as a circle, not a 40-gon (radius 6.4 px within 0.2 px on the raw ring).
- `Fit.Segment`: total least squares (the scatter's principal direction), endpoints at the extreme projections, RMS
  perpendicular residual as its straightness.
- `Segments.FromSkeleton`: each edge cut where its polyline turns by more than 3°, each run refitted to its skeleton
  pixels, a run ending on a node reaching the node's centre; runs meeting at a node on one line (within the angle, and
  within half a stroke width laterally) merged greedily by angle — a wire with a T part-way along is one wire. Runs
  shorter than two stroke widths are not reported.

### 4.7 Determinism (R-im1-9, D17) — `Bands.cs`

Parallel loops (the coverage assignment, Sauvola, both passes of the distance transform) cut their rows or columns
into bands of a **fixed** size (64) that does not depend on the thread count, and each band writes only its own slice;
nothing is reduced in completion order. Every sort has a total tie-break; every dictionary that is iterated is iterated
in sorted key order. The CIELAB table is computed with `Math.Pow`/`Math.Cbrt` once per channel byte: two platforms whose
libm disagreed in the last place would disagree in a table entry, never in an order. `RasterDeterminismTests` runs
clustering, coverage, contours, the threshold, the transform, the skeleton graph and the segments on 1 and 4 threads
and twice, and compares the bytes.

## 5. IM-2 — the picture: where it came from, what kind it is, where it is kept

`src/Design/Imaging/ImageSource.cs`, `ImageKind.cs`, `ImageKeep.cs`, `ImageProvenance.cs`; `src/Cli/DocumentKinds.cs`,
`PictureKinds.cs`. Gates: `tests/Ui.Tests/Imaging/{ImageKind,ImageSource,ImageKeep}Tests.cs` and two cases in
`CheckAndExplainCliVerbTests`.

### 5.1 One record for three origins (R-im2-1) — `ImageSource`

- **File** — the bytes read once. **Bytes** — a clipboard picture (the GUI encodes the clipboard bitmap as PNG) with a
  suggested name, `pasted_image.png` when none. **Placed bitmap** — a `BitmapShape` (its `.clay`, DBU rect and layer) or
  an `EditableBitmap` (its `.csch`, rect and rotation).
- A placed bitmap's path resolves as the primitive's own does: `RefPath.Resolve(documentDir, ref)`, a rooted reference
  winning. (Both loaders already make the path absolute on load; an in-memory shape with a relative reference resolves
  the same way.) A missing file, or an empty reference, is refused with the sentence naming **Resolve Path…** — the
  command that answers it.
- The record keeps the **original bytes** beside the decoded raster: `ImageKeep` copies what the user gave, not a
  re-encoding, and the SHA-256 is of those bytes so a re-run on the same file compares equal. The original file
  **name** is kept, never its path. `Format` is what the bytes announce (`RasterImage.DetectFormat`, signatures only),
  so a PNG named `.dat` is kept as `.png`.

### 5.2 What kind of drawing it is (R-im2-2, R-im2-3) — `ImageKind`

Measured features, in the order they are taken; every threshold is a named constant in `ImageKind.cs`.

| Feature | Measured how | Threshold and why |
|---|---|---|
| Size | the decoded raster | under **64 px** a side: *too small to read (w × h px)* — below it a stroke and a letter are a few pixels (D16) |
| Flat share | pixels within **3 sRGB levels** of their right and lower neighbours on every channel | rendered drawings measure above 0.85 (the gate's layout 0.986, schematic 0.980); a Gaussian-noise gradient 0.0001. Below **0.6** the picture is graded like a photograph |
| Colours | `ColourClusters.Find` (IM-1) | one cluster: *a blank picture* |
| Ink | pixels whose cluster is not the background (the border-majority cluster) | under **0.05 %** of the picture: *a blank picture* — 120 pixels of a 600 × 400 picture is dust, not a drawing. The colour mask rather than a grey threshold, so a light copper colour is ink |
| Filled share | ink pixels deeper than **max(3 px, 0.6 % of the shorter side)** from the background | a schematic's strokes are 1/150–1/400 of its side and never that deep (the gate's: 0); a layout's pads and pours mostly are (0.85) |
| One-width share | skeleton length (pixels) on edges whose mean width is within **±30 %** of the dominant width | one pen draws a schematic (1.0); a layout's pads and traces spread |
| Straight-run share | `Segments.FromSkeleton` runs longer than **10 stroke widths**, over the skeleton length | below **0.3**, no line work — a letter's stem is short; a wire is long |
| Text share | ink in components **5 px to max(14 px, 5 % of the height)** tall, no wider than 3 heights, with a like-height neighbour on the same row within 1.2 heights | above **0.6** with no line work: *a page of text*. The first pass of IM-9's grouping |
| Chromatic ink | ink clusters with CIELAB chroma above **20** | two or more lean layout — a weak vote, because a schematic tool may draw wires and symbols in two colours |

**None**, in order: too small; blank; **photograph-like** when the flat share is below **0.2** whatever else it reads,
or below 0.6 with no line work; a page of text. The 0.2 floor exists because below it nothing is flat, the ink mask is
the noise itself, and the skeleton of noise is a mesh whose merged junction nodes join into long, perfectly straight
two-point edges — the Gaussian-noise gradient measured a straight-run share of 0.34 and would otherwise have passed as
line work.

**Schematic or layout** is a weighted vote, each feature linear across a span about its centre: ink share (centre 0.11,
span 0.05, weight 1), filled share (0.3, 0.2, weight **1.5** — the strongest single tell), one-width share (0.6, 0.2,
weight 1, schematic-positive), chromatic ink (weight 0.5). The sign picks the kind; confidence is
`0.5 + 0.5·|score|`. The gate's layout reads 0.78, its schematic 0.94.

**The override** (R-im2-3): `ImageKindResult.Force(kind)` sets Schematic or Layout, keeps the reading it overrode in
`Suggested` and its evidence, and is recorded as `KindForced` in the provenance. A forced read of a None picture is not
refused for its kind; D16 refuses it later only if nothing at all is recognised. `ImageKind.TryParseOverride` reads
`--image-kind auto|schematic|layout`.

### 5.3 Where the picture is kept (R-im2-4, D4) — `ImageKeep`

`ImageKeep.Into(cellDir, cellName, source)` writes the original bytes to `<cell>.source.<format>` at the cell folder's
top (a sibling temporary renamed into place). The same name holding the **same hash** is reused and nothing is written;
a different hash moves to `<cell>.source-2.<format>`, `-3`, … each checked the same way, and no existing file is ever
overwritten. `KeptPicture.RefFrom(documentDir)` is the stored relative reference a document uses —
`../amp.source.png` from `amp/schematic/`. Nothing before the user's Create calls it, so a cancelled dialog leaves
nothing behind; reading and classifying write nothing (the gate checks the folder stays empty).

### 5.4 Provenance (R-im2-5) — `ImageProvenance`

The kept copy's reference, the original name, the hash, the pixel size, any reduction factor, the kind and whether it
was forced, the scale and its evidence (IM-3), the layer map (IM-3), the reading options, the circuitRF version (AS-6's
reading of it) and the time. Stored as an **`ImageSource` block** beside AS-6's `ArtworkSource` in the `.csch`, and the
same block at the top of a `.clay` (`ClayFile.ImageSource` ↔ `LayoutView.ImageSource`). Optional and null by default,
so no `FormatVersion` bump and every existing file re-serialises byte for byte. Its presence is what marks a result as
this command's to replace (AS D4's rule).

### 5.5 The CLI names a picture (R-im2-6)

`DocumentKinds` gains **`picture`**: by extension (`.png`, `.jpg`/`.jpeg`, `.bmp`, `.gif`, `.webp`), and by signature
when a NAMED path's extension is missing or wrong — checked before `convert`'s classifier, which reads text formats.
A BMP's two-byte signature is confirmed by its info-header size, or every file starting "BM" would be a picture.
`check x.png` reports `check.picture.kind` (or `check.picture.no-drawing` with the phrase) as an INFO and exits 0; an
undecodable picture is `check.file.unreadable` carrying the decoder's own refusal. A folder walk skips pictures, as it
skips Touchstone: a workspace's screenshots are not documents. `find` lists every picture in a workspace with its kind
(`--no-analyses` lists the paths alone, since each kind is a decode). `explain x.png` prints the format, the size (and
any stated resolution, never used as a scale), each measurement with the way it leans, and the kind; on a schematic it
also prints the `ImageSource` block. None of them reads past IM-2. Every other verb that switches on the kind refuses a
picture by name (`render`, `recognize`, `lvs`, the run verbs); `read`, whose catch-all reads a document's text, gets an
arm of its own (`read.file.picture`) — without it a picture that used to be refused as an unknown extension would have
been returned as its bytes decoded as text.

# Brief IML-7 — The reader on the models

**Series:** `brief-iml-0-overview.md` (§2, L5, L6) · **Tag:** `R-iml7-<m>`
**Depends on:** IML-6, **after the owner's go at its checkpoint**
**Area:** `src/Design/Schematic/Recognition/` (new: `VisionReading.cs`, `VisionAssembly.cs`, `VisionPins.cs`,
`VisionText.cs`, `VisionClasses.cs`; changed: `SchematicImageRecognition.Read`, `SchematicImageReport`),
`src/Design/Imaging/` (reused, unchanged), `tests/Ui.Tests/ImageRecognition/`, design note §9

---

## 1. Goal

Replace the front half of `SchematicImageRecognition.Read`: the picture goes through the two networks and a classical
assembly, and comes out as the **same** `SchematicImageResult` the emit, the drawing, the dialog, the CLI and MCP
already consume. Every entry point changes at once because there is one reader (L5).

## 2. Requirements

**R-iml7-1 — Two passes.** `VisionReading.Read(ImageSource, options, RunControl)`:
1. **pass 1** — the picture downscaled to ≤ 768 px on its long side; the picture head gives *schematic or not* and the
   median part size. *Not a schematic* below the head's threshold → the existing refusal sentence
   (`SchematicImageRecognition.NotASchematic`) with the evidence "the reader found no schematic drawing in it";
2. **resample** so the median part body is the trained 48 px (Lanczos, the existing `Resample`), capped at the IMF
   pixel budgets;
3. **pass 2** — the perception net over the resampled picture, **tiled** at 768 px with 128 px overlap when larger;
   detections merged across tiles by centre-distance NMS; masks stitched by taking each pixel from the tile whose
   centre is nearest.
Cancellation is checked between tiles and between stages (Esc, IMF-9); a cancelled read returns `Cancelled` as now.

**R-iml7-2 — Parts.** Peaks of each class heat map above a per-class threshold (stored in the model's metadata, chosen
on val in IML-6), non-maximum suppression across classes (one part per place), body box from the size head, turn and
mirror from the orientation head. Each part keeps its **confidence** (the peak height) on `RecognisedSymbol`.
`VisionClasses` maps the class to the record the emit already understands (`ImageSymbolKind`, `Variant`, the
circuitRF kind from `classes.json`); `ignore` classes (controller, probe) become report lines, never parts. A test
holds that every class in the shipped model maps.

**R-iml7-3 — Pins.** `VisionPins`: for each part, its class's canonical pin layout (`classes.json`) is turned and
mirrored into the body box to give **expected** pin points; each is matched to the nearest pin-heat-map peak within
half the body's short side, refined by the peak's offset; an unmatched expected pin falls back to where the wire mask
meets the body edge nearest it, else the expected point itself, reported. Perimeter-pin boxes (`ic-box`, `block`) take
every pin peak on or just outside their outline, ordered as IMF-8's SnP rule orders them, numbered from pin-number
words when read.

**R-iml7-4 — Wires and nets.** The wire mask (upsampled, thresholded) → IM-1 `Skeleton` → `SkeletonGraph` → polylines
squared to the picture's dominant axes (the existing `Segments` helpers). Connections: wire ends to pins within one pen
width (pen measured from the mask); T-joins connect; an X-crossing connects only where the junction map has a peak,
and never where the crossing map has one; a wire end with nothing near it is a dangling end, as now. The result is
the existing `WireGraph`-shaped record the emit consumes (`SchematicImageRead` with its wire nodes and pins) — adapt
the record, do not fork the emit.

**R-iml7-5 — Words.** Text-line regions → oriented crops (rotated per the reading-direction head) → the text net →
strings with per-character confidence → `KnownWords` snapping and `ValueGrammar` (both kept) → `ImageWord`s with their
quads. Association — designator, values, net names, port names to parts, wires and terminals — reuses
`LabelAssociation`'s distance-and-grammar rules on the new boxes; a class-name line (`R` above `R1`) is recognised as
the part's class name and not taken as a designator. Unread values stay tunable globals (D10).

**R-iml7-6 — Confidence and the report.** `SchematicImageResult` gains a per-part and a whole-reading confidence
(replacing `ReadingPlausibility`'s hand-fitted score in the same slot, so the dialog's and Paste's code paths need no
new branch); the report says which model version read the picture and lists parts below a review threshold — the
ones IML-9 selects after a paste.

**R-iml7-7 — No silent fallback (L6).** With the placeholder model embedded (`ShippedModels.IsPlaceholder`), schematic
pictures are refused in one sentence. There is no path back to the rule-based reader; IML-10 deletes it.

## 3. Gates

- `tests/Ui.Tests/ImageRecognition/VisionReadingTests` (no data home): IMF-1's 17 committed style pictures and IM-12's
  committed fixtures read through the new reader — each at least as right as the rule-based reader read it (the
  `Holds` ratchet table carried over), the target all exact. The picture that is not a schematic is refused.
- `VisionAssemblyTests` — hand-built model outputs (no network): a crossing with and without a dot, a T-join, a pin
  matched off its peak, a perimeter box, a cut part at the edge.
- `VisionClassesTests` — every shipped class maps.
- `ImageFieldTests` / `FieldScore` over the data home (skipped with a reason when absent): the numbers go into design
  note §9 against overview §4.
- The existing back-half gates stay green: emit, drawing, paste, dialog, CLI, MCP tests (scoped run — the image test
  classes only, per the owner's targeted-tests rule).

## 4. Not in this phase

Size-matched placement (IML-8). UI changes (IML-9). Deleting the old reader (IML-10).

## 5. Traps

- **Coordinates.** The emit works in the read picture's pixels with `ToSource`/`FromSource`; pass 2 runs on a
  resampled picture. Every box, pin and quad goes back through one transform, tested once.
- **Tiles double-count** a part on a seam; merge before NMS thresholds, not after.
- **The text crop for a vertical label** must be rotated before the text net, and its quad kept in picture
  coordinates — the association step measures distances in the picture, not in the crop.

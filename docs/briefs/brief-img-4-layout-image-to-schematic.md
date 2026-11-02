# Brief IM-4 — A layout picture made into a schematic

**Series:** `brief-img-0-overview.md` (D3, D5, D8, D14) · **Tag:** `R-im4-<m>`
**Depends on:** IM-3
**Area:** `src/Design/Layout/Recognition/Image/ImageRecognition.cs` (new), `src/Design/Layout/Recognition/
ArtworkRecognition.cs` (`RecognitionInput.FromTrace`), new `src/Design/Layout/Recognition/Silkscreen/
RasterSilkscreenEvidence.cs`, `RecognitionProvenance.cs`, design note §7

---

## 1. Goal

The traced layout goes through the **unchanged** artwork recognition. This phase is glue, and the measure of it is how
little of the artwork pipeline it touches.

## 2. Requirements

**R-im4-1 — One input conversion.** `RecognitionInput.FromTrace(ImageTraceResult, technology)` builds the input
`ArtworkRecognition.Recognize` already takes — the traced shapes as the flattened artwork, the `LayoutView` the trace
built, the technology, no `.cem`, no placement or BOM unless the user gives one (they still can). Nothing in AS-3 … AS-6
gains an image branch; if a stage needs something from the picture, it arrives through an existing seam
(`IPartEvidenceSource`, `RecognitionOptions`) or the phase stops and reports why.

**R-im4-2 — The entry point.** `ImageRecognition.Run(ImageSource, ImageTraceOptions, RecognitionOptions, target)`:
trace (IM-3) → recognise (AS) → emit (AS-6) → write. The target is a **new cell holding both views** (D14): the traced
`.clay` with its underlay, and the recognised `.csch` with the same picture under it, placed by the AS drawing's own
artwork hints so each part sits near its copper. `ImageRecognition.Circuit` is the no-write form the CLI's read-only
default and the dialog's preview call.

**R-im4-3 — Ground without a plane.** A picture usually shows the top copper only. When the mapped layers have no
copper on the technology's reference conductor, the reference plane is **implied** (the trace review's implied-plane
rule already reads a microstrip over an undrawn plane) and ground is the largest top-side pour, as AS D6 already says;
when there is no pour either, ground is reached only through drill circles that land on nothing on any mapped layer —
each is a via to the implied plane, which is AS D7's VIAGND. The report says once that the reference plane was implied.

**R-im4-4 — Silkscreen from pixels.** `RasterSilkscreenEvidence : IPartEvidenceSource`: the skeleton strokes IM-3 kept
for a *Silkscreen* cluster, converted through the frame into the centre-line form `StrokeGlyphs` already reads, so the
AS-10 matcher, its designator rules, its association and **Learn These Glyphs** apply unchanged. A stroke width under
1.5 px is reported as too thin to read, and the evidence source contributes nothing.

**R-im4-5 — Provenance chain.** The schematic's provenance names the traced `.clay` as its artwork (so AS-8's **Show in
Artwork** opens it) and carries the `ImageSource` block too; the `.clay` carries the `ImageSource` block. A re-run from
the same picture replaces both under AS D4's rule, with one checkpoint covering both.

**R-im4-6 — The report** is the trace's lines followed by the recognition's, one report, one Messages post.

## 3. Not in this phase
The dialog (IM-5). Any change to AS-3 … AS-6.

## 4. Gates (minimal tests, run only these classes)
- `ImageRecognitionTests` — a picture drawn in memory from one of AS-9's round-trip boards (`ArtworkRoundTripBoards`:
  top copper and drills only, at 20 µm/px) recognises the **same topology and part kinds** as that board's artwork does,
  with the reference plane implied; the new cell holds both views and both underlays.
- `RasterSilkscreenEvidenceTests` — `C12` drawn in a stroke font at 3 px stroke reads as a designator and is associated
  to the pair beside it; at 1 px it is reported too thin.
- A source scan (comment-stripped): no file under `src/Design/Layout/Recognition/` other than `Image/`, `Silkscreen/RasterSilkscreenEvidence.cs` and the one
  `FromTrace` names `ImageSource`.

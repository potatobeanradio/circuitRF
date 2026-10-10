# Brief IM-2 — The picture: where it came from, what kind it is, where it is kept

**Series:** `brief-img-0-overview.md` (D3, D4, D16) · **Tag:** `R-im2-<m>`
**Depends on:** IM-1
**Area:** new `src/Design/Imaging/ImageSource.cs`, `ImageKind.cs`, `ImageProvenance.cs`; `src/Cli/DocumentKinds.cs`
(classify a picture); design note §5

---

## 1. Goal

One record for "the picture the user gave us", whichever way they gave it, a first read of **what kind of drawing it
is** — with the graceful *nothing here* answer — and the one rule for where the picture is kept.

## 2. Requirements

**R-im2-1 — `ImageSource`.** Three origins, one record:
- **File** — a path; the bytes read once.
- **Bytes** — a clipboard picture (the GUI hands PNG bytes it encoded from the clipboard bitmap), with a suggested name.
- **Placed bitmap** — a `BitmapShape` (its `.clay`, its DBU rect, its layer) or an `EditableBitmap` (its `.csch`, its
  rect and rotation). The path resolves exactly as the bitmap primitive resolves its own (relative to the document,
  else absolute); an unresolved path is the refusal the primitive's *Resolve Path…* would answer, and says so.
The record carries the decoded raster (IM-1), the SHA-256 of the original bytes, the original file name (never a path),
and the placement when there is one.

**R-im2-2 — Kind.** `ImageKind.Classify(raster) → { Kind: Schematic | Layout | None, Confidence, Evidence }` from
measured features, not a model:
- the **filled share**: a layout is mostly filled regions, a schematic mostly thin strokes;
- the **stroke-width spread** on the skeleton: a schematic has one dominant width (± 30 %), a layout many;
- the **straight-run share**: the fraction of skeleton length in straight runs longer than 10 stroke widths;
- the **colour count** after clustering and the **gradient energy** (a photograph has many colours and smooth
  gradients; a drawing has few and sharp edges);
- the **text share**: small components in rows (the IM-9 grouping's first pass).
`None` carries the reason as a short phrase: *a photograph-like picture with no straight line work*, *a page of text*,
*a blank picture*, *too small to read (w × h px)*. Each threshold is a named constant with its measured reason in the
design note.

**R-im2-3 — The override.** The kind is a suggestion: the dialog and the CLI (`--image-kind`) can force `Schematic` or
`Layout`, and a forced kind is recorded in the provenance. A forced read of a `None` picture is not refused for its
kind; it is refused later only if nothing at all is recognised (D16).

**R-im2-4 — Keeping the picture (D4).** `ImageKeep.Into(cellDir, cellName, source)` copies the original bytes to
`<cell>.source.<ext>` (PNG for clipboard bytes) and returns the relative path the underlay and the provenance use. An
existing file of that name with the **same hash** is reused; with a different hash, `<cell>.source-2.<ext>`, and so on.
Never called before the user's Create — a cancelled dialog leaves nothing behind.

**R-im2-5 — Provenance.** `ImageProvenance`: original file name, hash, pixel size, any reduction factor, kind (and
whether forced), the scale and its evidence (IM-3), the layer mapping or the reading options, the circuitRF version,
the time. Stored where AS-6 stores `ArtworkSource` — a sibling block (`ImageSource`) in the `.csch`, and the same block
in a traced `.clay`. Its presence is what marks a result as this command's to replace (AS D4's rule).

**R-im2-6 — The CLI recognises a picture as a picture.** `DocumentKinds` classifies the five extensions (by content
when the extension is missing or wrong, through `RasterImage`), so `check`, `find` and `explain` name a picture instead
of calling it unreadable: `check x.png` reports its kind and confidence as an INFO and exits 0; `find` lists pictures
in a workspace with their kinds; `explain x.png` prints IM-2's evidence. None of them reads beyond IM-2.

## 3. Not in this phase
Tracing (IM-3), any schematic reading (IM-7), the GUI.

## 4. Gates (minimal tests, run only these classes)
- `ImageKindTests` — a generated layout picture (filled rectangles in two colours) reads `Layout`; a generated
  schematic (thin orthogonal strokes, a zig-zag, two plates) reads `Schematic`; Gaussian-noise colour gradients read
  `None` with the photograph phrase; a white raster reads `None`, *a blank picture*; a forced kind is recorded.
- `ImageSourceTests` — a placed `BitmapShape` resolves relative to its `.clay` and carries its DBU rect; an unresolved
  path refuses naming *Resolve Path…*.
- `ImageKeepTests` — same hash reused, different hash suffixed, nothing written before the call.
- `CheckAndExplainCliVerbTests` (existing) — one case each: a `.png` classifies as a picture; a PNG renamed `.dat` does too.

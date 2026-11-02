# Brief IMF-2 — The picture's own scales, and tolerances that follow them

**Series:** `brief-imf-0-overview.md` (F1, F2, L5) · **Tag:** `R-imf2-<m>`
**Depends on:** IMF-1
**Area:** `src/Design/Schematic/Recognition/` (`PictureProfile.cs` new, `SchematicImageOptions.cs`,
`SchematicImageReading.cs`, the tolerance sites in `WireGraph.cs`, `SymbolRegions.cs`, `TextRegions.cs`,
`LabelAssociation.cs`), `src/Design/Imaging/` (resampling), design note §18

---

## 1. Goal

Every tolerance in the reader is in stroke widths `w`. That is right for wire geometry and wrong for anything sized by
the drawing: a symbol body, a label's distance, a plate's length. A heavy-pen picture (`w` = 8 px) draws its symbols
only 10–20 `w` long; a light-pen picture at low resolution draws them 40 `w` long with 7 px type. The reader measures
the picture's own scales first, and each tolerance uses the one it is about.

## 2. Requirements

**R-imf2-1 — `PictureProfile`.** Measured once, before wires are traced, from the binarised picture and its skeleton:
- **`w`** — the pen, as now (the run-length mode, §9.1).
- **`h`** — the text cap height: the median height of compact components of glyph shape (height 2–20 `w`, aspect
  0.3–1.2, ink fill 0.15–0.6) that occur in rows of two or more.
- **`s`** — the symbol scale: the median length of the straight orthogonal runs that end in bare paper at both ends
  or close small cycles (plates, bars, box sides) — the strokes `MaxSymbolStroke` exists to catch. When too few exist,
  `s` = 3 `h`.
- **`g`** — the drawing grid, when `PictureGrids.Detect` finds one (§16), else the modal spacing of parallel wires.
- **Resolution class** — `Low` when `w` ≤ 2 px or `h` < 12 px, else `Normal`.

Each is reported with how it was measured (and its sample count) in `SchematicImageReport` and the overlay legend.

**R-imf2-2 — Tolerances by what they measure.** Every option in `SchematicImageOptions` is documented as one of: a
**pen** tolerance (in `w`: residuals, dot sizes, attach distance, wire capsule), a **symbol** tolerance (in `s`:
`MaxSymbolStroke`, the merge gap, the region grow, the hop span), or a **text** tolerance (in `h`: `MinTextHeight`,
`MaxTextHeight`, `LabelGap`, `TextContact`). The default value of each is unchanged **at the profile circuitRF's own
screenshots have** — so `ImageScreenshotPasteTests` and the IM-12 round trips are untouched by construction — and it
scales with its unit elsewhere. Write the mapping as a table in design note §18; a reviewer checks each option against
it.

**R-imf2-3 — Small pictures are resampled.** A datasheet screenshot at 1× is typically `Low` (a 1–2 px hairline,
7–9 px type), so this is the common case, not the exception. A `Low` picture is read at ×2 or ×3 (to bring `w` to ≥ 3 px and `h` to
≥ 18 px), Lanczos-resampled by SkiaSharp before binarisation; every coordinate reported, the overlay and the frame
are mapped back to the file's pixels (`ToSource`, as the deskew already does). The resampled reading is the one used;
the report says so in one line.

**R-imf2-4 — Colour and fills (F2).** Datasheets colour their text (blue), fill their amplifier triangles (grey,
cyan) and shade their IC boxes (a light gradient); evaluation boards colour not-populated parts (green). Today the
automatic kind reads three coloured 2× datasheet crops as **layouts** (overview §1.1), and the "keep the darkest
colours" normalisation (§9.1) can drop a coloured wire or label along with a fill.
- **Kind.** `ImageKind` weighs **line-art evidence** before colour area: when most ink, at every colour, is thin
  strokes (median run ≤ 3 `w`) with orthogonal straight runs and glyph rows, the picture is a schematic whatever its
  fills. Hold it on the 2× crops (ds-c, ds-f, ds-g) and on IM-12's layout pictures (still layouts).
- **Fills are not ink.** A filled area larger than 3 `s` (a shaded triangle, a box gradient) is taken out of the
  ink before skeletonising and kept as a **fill** on the region it lies in: the outline around it stays ink, the fill
  never makes a wire, a junction or a glyph. A small filled area (a diode's triangle, a dot) is ink as now.
- **Colour is not darkness.** Every stroke-like colour (thin runs, any hue) is kept as ink; only fills and a pale
  background tint are dropped. A coloured label reads as a label; a green not-populated part reads as a part
  (reported as drawn in a second colour — IMF-8 decides whether it means "not fitted").

**R-imf2-5 — Options expose the profile.** `SchematicImageOptions.Profile` (`Auto` default; or a stated
`PictureProfile`) — the dialog's Advanced row shows the measured values and lets a user type one; the CLI takes
`--pen <px> --text-height <px> --symbol-scale <px>`; MCP the same keys. A stated value replaces only itself.

## 3. Not in this phase
Changing what any stroke or region *is* (IMF-3/4) — this phase changes only the units; the field scores may barely
move, and that is expected. Choosing between several readings (IMF-9).

## 4. Gates (minimal tests, run only these classes)
- `PictureProfileTests` — `w`, `h`, `s` measured within 15 % on `ds-rf-amp` at 1× and 2×, `heavy-iec` and `low-res`;
  `ds-rf-amp` 1× and `low-res` are `Low` and resampled; circuitRF's own `term-s2p-screenshot.png` profiles to the
  defaults' reference profile.
- `ImageKindTests` — `ds-gain-block` at 2× (cyan triangle, blue text) classifies as a schematic; IM-12's dark layout
  picture still classifies as a layout.
- `ImageDatasheetStyleTests` — `ds-gain-block`'s triangle fill makes no wire and no junction. Turn that fact on.
- The existing `ImageScreenshotPasteTests`, `ImageRoundTripTests`, `ImageForeignDrawingTests` stay green unchanged.

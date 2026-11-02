# Brief PTU-6 — Drag a box: read only this plot, or ignore this legend

**Series:** `brief-ptu-0-overview.md` · **Tag:** `R-ptu6-<m>` · **Depends on:** PTU-2 (the toolbar it sits in)
**Area:** `src/Ui/Recognition/ImageOverlayCanvas.cs`, `src/Ui/Recognition/ImageSourceViewModel.cs`,
`src/Ui/Recognition/PlotSessionViewModel.cs`, the canvas's floating toolbar in `ImageReadingPanel.axaml`

---

## 1. What is wrong

- A picture holding **several plots** (a Smith chart beside a dB plot) either finds none of them or several, and the
  user has no way to say "this one".
- A **legend drawn inside the plot** is read as traces.

Both have a manual answer that needs no new recognition: a box the user drags. The reader already accepts the second
one — `PlotReadingOptions.Hints` (`PlotReading.cs`, per plot number) carries `TraceHints.Exclude` boxes into
`PlotTraces.Find` — it is simply not wired to the dialog (IM-14 left it open).

## 2. Requirements

**R-ptu6-1 — Two canvas tools, plot mode only.** Two toggle buttons in the canvas's floating toolbar (beside Zoom to
Fit, editor toolbar idiom): **Read only here** (`CropFree` or similar) and **Ignore here** (`SelectionRemove` or
similar). With one on, a drag on the canvas draws a dashed box (`ScopeRect` already draws one — reuse its drawing);
release commits it and turns the tool off. Esc cancels. Tooltips say what each does in one sentence.

**R-ptu6-2 — Read only here crops.** The picture is cropped to the box (a new `ImageSource` from the cropped raster,
named `<name> (cropped)`; a SkiaSharp crop and re-encode in the Ui is fine) and **replaces** the current picture in
the session (PTU-1's Replace path), so the plot is read as if the user had supplied only that part. A "Restore whole
picture" action undoes it (keep the original `ImageSource` on the row). Coordinates are picture pixels, not screen.

**R-ptu6-3 — Ignore here excludes.** The box goes into that plot's `TraceHints.Exclude` (the plot whose frame contains
the box's centre; a box outside every frame is refused in the status line) and the picture is re-read with the hints
passed through `PlotReadingOptions.Hints`. Excluded boxes are drawn hatched on the canvas (the `Ignored` class's
look) and each can be removed by selecting it and pressing Delete. Exclusions are cleared by Replace.

**R-ptu6-4 — Nothing new in `src/Design`.** If the hints do not reach `PlotTraces.Find` through the options the
runner already builds, add them at the dialog's call site only. **If it needs a reader change, stop and report.**

## 3. Gates

- `PlotBoxToolTests` (new): a crop box produces a picture of the box's size and replaces the session's picture;
  Restore brings the original back; an ignore box lands in the right plot's `Hints[n].Exclude` and a trace drawn only
  inside it is gone after the re-read (in-memory drawn plot with a fake legend sample inside the frame); a box outside
  every frame is refused.
- One headless render with an ignore box drawn.

## 4. Out of scope

Finding plots or legends automatically (overview §5). `TraceHints.Follow` (click-to-follow) — leave it unwired.

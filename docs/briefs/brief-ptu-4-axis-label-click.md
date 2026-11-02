# Brief PTU-4 — Click an axis label, type its value

**Series:** `brief-ptu-0-overview.md` · **Tag:** `R-ptu4-<m>` · **Depends on:** PTU-1
**Area:** `src/Ui/Recognition/ImageOverlayCanvas.cs`, `src/Ui/Recognition/PlotSessionViewModel.cs`
(`PlotFrameRowViewModel`, `Clicks`), `src/Ui/Recognition/PlotTracesPanel.axaml`

---

## 1. What is wrong

An axis label the reader boxed but could not read is drawn as an amber Unknown box. Clicking it selects it and
**nothing visible happens** — there is nothing to edit. Meanwhile, when an axis is uncalibrated the only way to answer
is the typed row under the plot, `80px=0,560px=-40`: **two pixel coordinates the user has no way to know**. The
machinery underneath is fine — `PlotSessionViewModel.Clicks` turns that text into `AxisClicks` and the reading uses
it. Only the way in is wrong.

## 2. Requirements

**R-ptu4-1 — A label click asks for its value.** In plot mode, selecting an overlay item that is a **tick label of a
rectangular plot** (read or unread — a misread `-72 25` must be answerable too) shows a small inline editor next to
it on the canvas, or, if an inline editor is awkward in `ImageOverlayCanvas`, a row at the top of the Plots section:
`y label at 312 px  [ -10      ] dB   Set`. Pre-filled with what was read, if anything. Enter/Set commits; Esc closes.
Which axis: the label's centre left of (or right of) the frame is **y**, below (or above) it is **x**. The label's
pixel is its box's centre along that axis. Find how labels appear in the overlay (class `Text`, or `Unknown` with a
box) and their frame index; add a Ui-side lookup, not a reader change.

**R-ptu4-2 — Two values calibrate the axis.** A committed value becomes one `<px>px=<value>` pair in that frame's
`XAxisText`/`YAxisText`; the second committed label on the same axis completes the pair and the existing path takes
over (re-read, calibration line updates). A third replaces the older of the two. An x value takes a unit
(`2.4 GHz`); a bare x number uses the unit of the other x labels if one was read, else is refused in the status line
("Say the unit: 2.4 GHz"). The typed rows stay as they are (they are also the CLI's grammar) and show the pairs the
clicks produced, so the two ways in agree.

**R-ptu4-3 — The axis row says how to answer.** When `NeedsX`/`NeedsY`, the row's placeholder text becomes
`Click two x labels on the picture` (resp. y) instead of a pixel example; the pixel grammar stays in the tooltip.

**R-ptu4-4 — A calibrated axis can be corrected.** If a frame's axis was calibrated from misread labels, the clicked
pairs must override it. Check how the reading treats `AxisClicks` when it also read labels: if clicks already win,
nothing to do; if they are ignored, make the dialog pass them in a way that wins **using an option the reader already
has** (the CLI's axis flags). **If it needs a reader change, stop and report** — do not change `src/Design`.

## 3. Gates

- `PlotAxisLabelClickTests` (new): a label left of the frame commits to y, below to x; two y commits produce the
  `YAxisText` the `Clicks` parser accepts and `NeedsY` goes false after the re-read; a third commit replaces the
  older; an x number without a unit is refused when no x unit was read. In-memory drawn plot, labels left unreadable
  by drawing them in a way the fixtures already use for an unread label (or by clearing the read value in the test).
- One headless render with a label selected and its editor open.

## 4. Out of scope

Better OCR of tick labels (overview §5). Smith/polar charts (their frequency rows are separate and unchanged).

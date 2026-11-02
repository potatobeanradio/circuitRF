# Brief PTU-3 — The canvas and the traces table know about each other; the overlay stops hiding the colour

**Series:** `brief-ptu-0-overview.md` · **Tag:** `R-ptu3-<m>` · **Depends on:** PTU-1
**Area:** `src/Ui/Recognition/ImageOverlayCanvas.cs`, `src/Ui/Recognition/PlotSessionViewModel.cs`,
`src/Ui/Recognition/PlotTracesPanel.axaml` (+ `.axaml.cs`), `src/Ui/Recognition/ImageSourceViewModel.cs`

---

## 1. What is wrong

**(a) No link.** Clicking a trace on the canvas sets `ImageSourceViewModel.SelectedItem` (`ImageOverlayCanvas`'s
pointer-release path → `Overlay.HitTest`) and nothing else: the traces table neither scrolls to that trace's row nor
marks it. Choosing in a row does not mark the trace on the canvas either. The parts table already does this for
schematic/layout pictures (`SelectedRow` ↔ `Overlay.ItemForPart`) — copy that pattern.

**(b) The overlay hides what the user must read.** An unassigned trace is `Unknown`, so the canvas draws it in
**amber at 1.75 px over the picture's own curve**, with a label `T37 unassigned` beside it. With 20–50 traces the plot
area is amber lines and amber words, and the curve's real colour — the one thing that says whether it is S11 or S21 —
is underneath.

## 2. Requirements

**R-ptu3-1 — Canvas → table.** Selecting a trace on the canvas (an overlay item of class `Trace`, gap pieces
included — map a gap to its trace) selects that trace's row: `PlotSessionViewModel.SelectedTrace` (new), the row
highlighted with the same selected-row brush the parts table uses, and **scrolled into view** (`BringIntoView` on the
row's container from the panel's code-behind, as `PartsTablePanel` does if it does; otherwise a `ListBox` with
`SelectedItem` + `ScrollIntoView`). Find how a `Trace` overlay item's `Id` names its trace (the overlay is built from
the reading in `src/Design/Imaging/Plots/`); add a lookup helper on the Ui side, not in the reader.

**R-ptu3-2 — Table → canvas.** Clicking anywhere on a row (not just its combo boxes) selects the trace: the canvas sets
`SelectedItem` to it and brings it into view (`ImageOverlayCanvas.BringIntoView` already exists). Selection is one
state — set from either side, shown on both, with a guard against the two setters re-entering each other.

**R-ptu3-3 — The picture's colour stays visible.** In plot mode the canvas draws a trace so the curve under it can
still be seen and its colour judged:
- an unselected trace: a **thin (1 px) line in a neutral contrasting colour, dashed**, offset nothing — not amber, not
  the trace's own colour. Unassigned is said by the table, not by painting the curve amber.
- the selected trace: the accent colour at 2 px, so the user sees which pixels the row means.
- labels: **only the selected trace's** label is drawn (`T7 · S21 · dB`); the others draw none. A hovered trace may
  show its label too if hover is already plumbed; do not add hover plumbing for this.
- the Unknown legend toggle (`ShowUnknowns`) keeps working for non-trace unknowns (labels, markers).

**R-ptu3-4 — The row says the colour it read.** The row's swatch (`Swatch`, already the curve's read RGB) grows from a
16 × 3 bar to a 14 × 14 square with a 1 px border, so a pale or yellow trace is visible against the panel.

## 3. Gates

- `PlotTraceSelectionTests` (new): selecting a trace overlay item selects its row; selecting a row sets
  `SelectedItem` to its overlay item; a gap item selects its trace's row; no re-entrant loop (set each side twice, the
  other side's setter runs once each). Use an in-memory drawn plot with two traces (the existing PlotRecognition
  fixtures draw one).
- One headless render with a trace selected; check the curve colours are readable under the overlay and the row is
  highlighted and in view.

## 4. Out of scope

Clicking an axis label (PTU-4). Merging rows (PTU-5). The overlay drawn into the exported overlay PNG
(`ImageOverlay.Picture.cs`) — the CLI's picture keeps today's look unless the change falls out for free.

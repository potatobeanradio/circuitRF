# Brief PDG-3 — Plot digitiser: points that snap to the traced curve

**Series:** PDG-1 … PDG-3 (plot digitiser improvements) · **Tag:** `R-pdg3-<n>` · **Written:** 2026-10-18
**Follows:** `brief-ptu-3-canvas-table-link-and-overlay.md` (canvas ↔ table), `brief-ptu-6-drag-a-box.md` (canvas tools)
**After:** PDG-1, which decides which y axis a trace's value is read on. If PDG-2 has landed, the readout also shows
the fit; if it has not, leave that line out.

---

## 1. What this is

The owner asked for points that align to the plot's scale, and chose this reading of it (2026-10-18): **a point placed
on the picture snaps to the nearest pixel of a traced curve, and shows its axis values.** It answers "what does this
curve read at 2.4 GHz?" and "where is the −3 dB point?", without exporting a file and reading it back.

There is no such readout today. Hovering the canvas tells the user nothing about values. A click on a trace selects
its row (PTU-3), and a click on a tick label calibrates an axis (PTU-4).

## 2. What to build

### R-pdg3-1 — The probe, as a pure function: `src/Design/Imaging/Plots/TraceProbe.cs`

```csharp
public sealed record TraceProbeHit(int Trace, int Plot, double Px, double Py, double X, double Y, Complex? Value,
                                   double? FrequencyHz, double DistancePx);
public static class TraceProbe
{
    /// Nearest traced curve to a picture point, within maxPx; null when none.
    public static TraceProbeHit? Nearest(PlotSession s, int picture, double px, double py, double maxPx);
    /// A rectangular trace's value at an axis x (hertz on a frequency axis); null outside its range.
    public static TraceProbeHit? AtX(PlotSession s, int trace, double x);
}
```

- **Snapping is to the curve, not to a sample.** Project the point perpendicularly onto each segment of the trace's
  polyline (`Curve.Points`, picture pixels), take the nearest, and interpolate along that segment: x through the
  x calibration, y through **the trace's own axis** (PDG-1). The readout is therefore continuous, not stepped to pixel
  columns.
- A **chart trace** reads Γ (complex) at the projected point, and its frequency interpolated between the two
  samples' frequencies when the trace has one (`PlotTraceRead.Frequency`). Otherwise `FrequencyHz` is null and the
  readout says *no frequency*.
- Only traces of non-excluded plots on that picture. The nearest wins; equal distances go to the selected trace's plot.
- `AtX` interpolates linearly between the bracketing samples, in log x on a log axis. It never extrapolates.

### R-pdg3-2 — The dialog: hover readout, and a Point tool

- **Hover, always on** in plot mode, without disturbing the existing click behaviour: when the pointer is within 8
  screen px of a curve, draw a small crosshair at the snapped point and show a readout beside it:
  `T3 · 2.437 GHz · −12.31 dB` (or `T3 · Γ 0.41 ∠ −63° · 2.40 GHz`). Values use the axis's display unit and a
  precision of about one pixel's worth (`PerPixel`). Never print more digits than the picture resolves. With a PDG-2
  fit set, add `fit −12.29 dB`.
- **Point tool**: a toolbar toggle beside PTU-6's box tools, with the same arming and Escape-cancels behaviour
  (`ImageOverlayCanvas` ~line 518-633, `PlotBoxTool`). It should be one more mode of that mechanism, not a parallel
  one. While armed, a click **pins** a point at the snapped location. A click far from every curve is refused in the
  status line, and the tool stays armed.
- **Points list**: a small pane section under Traces listing pinned points (`P1 · T3 · 2.437 GHz · −12.31 dB`), each
  with ✕. Delete removes the selected point on the canvas as the box does, and **Copy** copies them all as
  tab-separated text with a header row (`point, trace, x, x_unit, y, y_unit`). Pins are drawn on the canvas as
  numbered dots. They are session state only: re-reading the picture keeps them, re-snapped by their picture
  position, and they are not written to any file.
- **Type an x** in the Points section (`2.4 GHz`, parsed by the same grammar as the axis rows) to pin the selected
  trace's value at that x through `AtX`. That is the exact way to ask "what is S21 at 2.4 GHz", where a click only
  gets close.

### R-pdg3-3 — The CLI equivalent

`recognize … --probe 2.4GHz` (repeatable) prints, for every rectangular trace whose range covers that x, its value
there through `TraceProbe.AtX`. It is also in the JSON as `probes[]`. MCP `PlotOptions` gains `probe`. It is small,
and it keeps the CLI no weaker than the dialog. `cli.md` §26.9.

## 3. Gates — one test per claim, `tests/Ui.Tests/PlotRecognition/TraceProbeTests.cs`

Build small `PlotSession`s by hand (a frame, a linear calibration, a two-segment trace), except where noted:

1. A point off a segment projects onto it, and its x/y are the interpolated axis values, not the nearest sample's.
2. The nearer of two traces wins, and nothing within `maxPx` gives null.
3. A right-axis trace (PDG-1) reads its value through `Y2`.
4. A chart trace reads Γ, and frequency when it has one, else null.
5. `AtX` on a log x axis interpolates in log x and is null outside the range.
6. View model: pin, delete and copy give the expected TSV. Escape disarms the tool.
7. CLI: `--probe` on a `PlotPictureSet` dB(S21) picture returns S21 at that frequency within the round-trip bound.

Run **only** `--filter "FullyQualifiedName~TraceProbeTests"` and the one CLI case. Never the full suite. Read
failures from the TRX.

## 4. Rules

- No reader change, and no change to what the Touchstone or CSV contain.
- At most two rendered dialog pictures, via `brief-ptu-0-overview.md` §4.
- Docs: `create-from-image.md` `#plots` (hover, Point tool, Points list), `docs/design/image-to-circuit.md` §15,
  `cli.md` §26.9. No DocGen. Findings go in `src/Ui/RESOLVED.md` / `src/Design/RESOLVED.md`. No commit unless the
  owner asks.

# Brief DFR26-6 — Plots and tuning: a legend, arrow keys that do what a user tries, a curve that changed on its own

**Series:** designer feedback round 26 (2026-10-24), see DFR26-1 for the list. · **Tag:** `R-dfr26-6-<n>`
**Area:** `src/Ui/Views/Tuning/TuningToolView.axaml.cs` (key handling ~120-160), `src/Ui/Tuning/TuningPanelViewModel.cs`,
`src/Ui/DataDisplay/Controls/PlotControl.cs` (marker keys ~505), `src/Ui/DataDisplay/ViewModels/PlotContainerViewModel.cs`
(`StepSelectedMarkers`), `src/Ui/DataDisplay/ViewModels/PlotInspectorViewModel.cs`, `src/Render` plot renderers, the
tuning re-run path (`src/Ui/Tuning/TuningPush.cs`, `TuningSurfaceRelay.cs`). Read `src/Ui/DataDisplay/CLAUDE.md` first.
Findings go in `src/Ui/RESOLVED.md`.
**Tests:** new `tests/Ui.Tests/DataDisplay/PlotsAndTuningRound26Tests.cs`, plus the existing tuning and marker classes.

---

## 0. The gap

1. **A curve changed while a different circuit was being tuned.** The designer had two independent two-port circuits
   in one schematic (each a 50 Ω through line with a shunt series L-C to ground; ports 1-2 on one, 3-4 on the other)
   and one S-parameter analysis plotting S21 and S43. Tuning the inductor of the first circuit, the S43 curve — whose
   circuit had not changed — was redrawn with a different shape (its notch gone, a smooth slope instead) until a full
   re-simulate restored it. After updating to 1.5 they could not make it happen again. Nothing known changed there in
   1.5, so treat it as an intermittent fault, not as fixed.
2. **No legend.** They asked for a legend on S-parameter plots, as their other tool draws one (a box listing each
   trace's name beside a sample of its line). Today a rectangular plot carries the trace names on its y-axis label.
3. **Arrow keys.** They could not move "the marker in the parameter plot" with the arrow keys. The owner's reading:
   the **Tuning window** — Up/Down should choose which parameter, Left/Right should change its value. Today, with a
   slider focused, all four arrows change the value (`TuningToolView.axaml.cs` ~151); nothing moves between
   parameters. Data Display already steps a SELECTED marker on a Rect plot with the arrows
   (`PlotControl.OnKeyDown` → `StepSelectedMarkers`), so if the designer meant a plot marker, the gap is getting a
   marker selected and the plot focused, or a plot type that does not step (Smith, Polar).
4. **A "line marker" for several traces at one x** was asked for. The Multi-marker already does it; they did not find
   it.

## 1. What to do

### R-dfr26-6-1 — Find the cross-circuit redraw, or prove the path cannot do it

Build the designer's circuit (two independent L-C shunt notches, 1.45 nH / 1.7 pF and 2 nH / 0.7 pF, four 50 Ω ports,
1-6 GHz), plot `dB(S(2,1))` and `dB(S(4,3))`, and drive the tuning path programmatically: many rapid value changes on
the first inductor, including changes that arrive while a previous tuning run is still in flight. After each settled
update, S43's points must be bit-identical to the untuned run. Look specifically at: results of two in-flight runs
being merged, a cube replaced while a trace is mid-rebuild (`ReseedSliceIfCubeShapeChanged`), and anything in the
tuning fast path that re-solves only part of the matrix. Write the cause in RESOLVED; if no failure appears in a few
thousand updates, say so, keep the test, and stop.

### R-dfr26-6-2 — A legend

A plot setting **Legend: Off / Inside / Below**, in the plot inspector beside the other plot-level settings, saved in
the `.cdd`. Each entry is the trace's colour and line style and its display name (the same text the y-axis label uses).
Default **Off** for Rect (the names are already on the axis); default **Below** for Smith and Polar, whose axes carry no
trace names — check what they show today first, and if they already name the traces, default Off there too. Drawn by
the `src/Render` plot renderer, so `render`/`plot` on the CLI draw it as well.

### R-dfr26-6-3 — Tuning window: Up/Down choose, Left/Right change

With focus anywhere in the tuning rows: **Up/Down** move to the previous/next parameter (focus its slider, scroll it
into view); **Left/Right** step the value (Shift = ten steps, as today); PageUp/PageDown unchanged. A text box being
edited keeps its own arrow-key behaviour. Say it in the window's existing tooltip for the slider — no new text under
the rows.

### R-dfr26-6-4 — Plot markers answer the arrows when a user expects them to

- A marker just added from a menu (not by clicking) is selected and its plot has keyboard focus, so the arrows work at
  once.
- Smith and Polar plots step a selected marker along the trace's samples too (the same `StepMarkerAlongX` order: by
  frequency).
- The Multi-marker's menu entry carries a tooltip saying what it is for ("one x, every trace's value"), so it is found
  when someone looks for a line marker.

## 2. Tests

1. Two independent circuits, the first tuned 1,000 times with overlapping runs: the second's points never change.
2. Legend Below on a Smith plot draws one entry per trace; Off draws none; the setting round-trips through `.cdd`.
3. Tuning rows: Down moves focus to the next row, Right steps its value, Up/Down at the ends do nothing.
4. A marker added from the menu steps with Right without a click first; a Smith marker steps in frequency order.

Run the new class and the existing `*Tuning*` and `*Marker*` Data Display classes only.

## 3. Text

`docs/user/src/reference/data-display.md` (legend; arrow keys on markers, Smith included),
`docs/user/src/reference/optimization.md` or wherever the Tuning window is described (grep for it) — the keys.
Regenerate those pages only.

## 4. Out of scope

A draggable legend, legend fonts, or per-trace legend visibility.

# Brief PCD-10 — Markers on the response, and the parameters as dimensions on the canvas

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd10-n`
**Area:** `src/Ui/PCellDesigner/`, `src/Ui/Views/PCellDesigner/`, new `src/Design/PCellDesigner/PCellDimensions.cs`,
`src/Ui/Theming/AppPreferences.cs` (one preference), `tools/UiSnapshot/PCellDesignerSnapshot.cs`,
`docs/user/src/reference/pcell-designer.md`, `docs/design/pcell-designer.md`
**Depends on:** PCD-6 (the window), PCD-2 (offsets), PCD-3 (`StretchBinding`).

---

## 0. What this adds

The designer's response plot gets the Data Display's markers, kept across the re-run every edit makes. The canvas draws
each cell parameter as a dimension line labelled `name = value unit`, so a designer sees which parameter is which length.

## 1. Requirements

- **`R-pcd10-1` — markers.** The response `PlotControl` is wired to its Data Display container as the LC Filter
  Designer's plots are: add, drag and step a marker, with its readout box. On a re-run each marker moves to the new
  trace with the same name, at the same x. A marker whose trace is gone (the EM overlay, cleared by an edit) is kept
  aside and comes back with that trace.
- **`R-pcd10-2` — dimensions.** `PCellDimensions.Of` (Design layer) returns one line per use of a cell parameter,
  each with its subject (the parameter, or the expression that uses it), its two end points and its label:
  - a `StretchBinding`: from its anchor vertex to its first moving vertex, projected on its axis;
  - an `Offset` whose `Dx`, `Dy` or `Angle` names a parameter: from the anchor pin to the part's pin;
  - a primitive parameter that names one (`TL1.L = L1`, `W = W`): the span of the primitive's own PCell handle for
    that parameter, once per expression.
  A label is `name = value unit` for a bare parameter, `expression = value unit` when the expression combines terms.
- **`R-pcd10-3` — the toggle.** **Dimensions** on the toolbar, on by default and remembered as a preference; the lines
  are drawn over the canvas as the layout editor draws its rulers, and follow every re-plan.

## 2. Decisions

- **D1 — Rulers, not a new drawing.** A dimension is drawn as a `RulerAnnotation` (its caption the label) by
  `LayoutRenderer.DrawRulersOnTop`, through an `ILayoutCanvasOverlay`. The ruler's own readout (the drawn length)
  stays above the caption: for an offset or a stretch it is the measured span, which is worth seeing beside the value.
- **D2 — The display unit** is the bare parameter's own unit, else the unit of the first parameter the expression
  names, else the site's unit (`deg` for an `Angle`).
- **D3 — Markers by name.** Trace names are stable (`S21`, `EM S21`), so the name and the marker's own x are enough to
  put it back; nothing else is stored.

## 3. Tests

`tests/Ui.Tests/PCellDesigner/Pcd10*.cs` on `PadTee` from `examples/PCell Designer/`: markers survive a re-run; the
dimensions of a stretch binding, an offset and a primitive parameter, with their labels following a value change.

## 4. Possible enhancements

- **Clicking a dimension selects its parameter row.** Useful on a cell with many parameters; the overlay already
  receives the press, but the parameter table is not a selectable list yet.
- **`circuitrf pcell render --dimensions`**, so documentation figures can show them headless. The same rulers can be
  added to the rendered layout; the window's snapshot covers the user page for now.
- **Arrowheads.** The ruler's end ticks read as a dimension already; arrowheads would need a ruler style of their own.

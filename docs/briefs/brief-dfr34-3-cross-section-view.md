# Brief DFR34-3 — a cross-section through any two points

**Series:** designer feedback round 34. · **Tag:** `R-dfr34-3-<n>`

An electrostatic run on a test point and its via produced a field. The picture that explains the result is a vertical
section through the pad, the via and the planes, with the field on it and the geometry cut away in front. That
picture was made by hand for a report. In the application it cannot be made easily. A field plot is axis-aligned
(`C3dFieldPlot.Axis` + `Offset`), and the toolbar's clip plane is on an axis or perpendicular to the view. The pad and
the via in this board are not on one X or Y line, so no axis plane passes through both.

Read first: `C3dFieldPlot` (`src/Design/ThreeD/C3dFieldPlots.cs`), `FieldPlotResolver` and `FieldSurfacePlot`
(`src/Render/Scene3D/Fields`), `ClipPlane3D` (the shader's clip, capped by back faces), and
`C3dEditorViewModel.FieldPlots`. `render x.c3d --field <plot>` draws a field plot headlessly (`cli.md` §13.8.1) and must
keep drawing the same picture.

## R-dfr34-3-1 — a field plot on a vertical plane through two points

`C3dFieldPlot` gains a plane stated by two points in plan (x, y) and spanning the whole height, besides `Axis` +
`Offset`. Existing documents read unchanged. Sampling on that plane runs through the same resolver; the plot's own
axes are distance along the line and z.

## R-dfr34-3-2 — Section Through… in the 3D view

A command (Field Plots context menu, and the 3D view's context menu with a field present): pick two points with the
existing snapped point picking (object centres and edges snap, so a via's axis and a pad's centre are one click each).
It makes the plot of R-1, sets the clip plane to the same plane (keeping the side away from the camera), and turns the
camera to look straight at it. One undo removes all three.

## R-dfr34-3-3 — the clip plane takes any plane

`ClipPlane3D` gains an arbitrary normal (the `View` axis already stores one), so the geometry is cut at the plot's
plane. Its toolbar control shows "Section" when set this way.

## Gates

- On the Capacitance Extraction example: a section through the pad's centre and the line's far end draws the field on
  a plane at that angle. `render --field` writes the same picture headlessly, compared with the in-process renderer.
- An axis-aligned plot written before this brief reads and draws byte for byte as before.
- UiSnapshot of the two-point pick and of the resulting view, inspected.

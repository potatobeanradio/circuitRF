# Brief DFR31-3 — Field plots: show where the cut plane is, and say why the Solution list is empty

**Series:** designer feedback round 31 (2026-10-29). · **Tag:** `R-dfr31-3-<n>`
**Area:** `src/Ui/Views/ThreeD/` (the 3D viewer's overlay drawing, and the field-plot Properties panel),
`src/Render/` `FieldPlotResolver`, the `.c3d` `FieldPlots` model in `src/Design/ThreeD/`.
Findings go in `src/Ui/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/ThreeD/`. Headless render with `tools/UiSnapshot --c3d`, looked at before reporting.

---

## 0. Why

The round-26 workspace has a clip-plane E-field plot showing the field around the test pad. Making the same plot on
another board, before solving, shows three gaps in the panel:

1. **Nothing shows where the plane is.** The Properties panel shows "Across: Y" and an offset in µm. On a board, a
   number does not tell anyone whether the plane passes through the pad. A crosshair, or any marker of the plane in
   the 3D view, is what is needed.
2. **Nothing says the plane can be moved after the solve.** The solve saves the whole field, and the plot reads a
   plane from it, but the panel gives no hint of this. It reads as if a plane chosen wrongly would mean solving again.
3. **The Solution list does not explain itself.** Before the electrostatic setup has run, the list shows only
   "Other frequency…" and Quantity offers only Electric field: the plot falls back to the driven choices. In the
   round-26 workspace, after its run, the same list offers "Terminal TP (1 V, others 0 V)". The panel's first line
   ("No finished run of setup … yet — Simulate to draw this plot") is correct, but nothing connects it to the list.

## 1. Requirements

**R-dfr31-3-1 — The plane is drawn while its plot is selected, and while its offset is being dragged.** Draw a
translucent rectangle spanning the air box on that axis, with its outline, plus a short label giving the axis and
offset in display units. Draw it whether or not a solution exists. The point is to place the plane BEFORE solving.
Use the selection colour of the 3D view's other overlays. Hide it when the plot is deselected, and give the plot a
toggle so it can stay on.

**R-dfr31-3-2 — Pick the plane.** A "Through…" button next to the offset arms a one-shot pick: clicking an object in
the 3D view sets the offset to that object's centre on the plot's axis. The usual wish is a plane through a specific pad;
that should be one click, not a coordinate typed from the 2D layout's status bar.

**R-dfr31-3-3 — The offset slider spans the geometry, not the air box**, and stays live after a solve. Moving it
redraws the field without re-running (it already does: confirm it, and say so in the panel's tooltip: "The field is
saved for the whole volume; move the plane at any time").

**R-dfr31-3-4 — The Solution list states its source.** When the plot's setup has no finished run, the list holds one
disabled row that says what will appear: "Terminals of the electrostatic setup, once it has run" (or "Frequencies…"
for a driven setup). The Quantity list shows the quantities that setup type will offer (for electrostatic: E, |E|,
potential V), disabled until a run exists. Never show the driven setup's choices for a static setup.

## 2. Gates

1. Headless render: a `.c3d` with a clip plot selected shows the plane rectangle at the stated offset, before and
   after a solve.
2. Pick: clicking a via sets the offset to the via's centre on the axis.
3. A static setup with no run shows the disabled "Terminals …" row, and no frequency rows.

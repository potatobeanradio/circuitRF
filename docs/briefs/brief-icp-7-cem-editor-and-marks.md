# Brief ICP-7 — The instance tree in the Component ports section, and the cut drawn on the layout

**Series:** ICP-0 … ICP-9 · **Tag:** `R-icp7-<n>` · **Depends on:** ICP-1, ICP-2
**Area:** `src/Ui/Layout/Em/EmSetupEditorViewModel.ComponentPorts.cs`, `EmComponentPortsHost.cs`, its view;
`src/Ui/ViewModels/WorkspaceViewModel.ComponentPortMarks.cs`, `LayoutComponentPortMark`, the layout renderer's mark overlay;
`src/Ui/ThreeD/C3dEditorViewModel.cs` (its component-port rows); `tools/UiSnapshot`.

## The gap

The Component ports section (LCP-3) lists footprint parts as flat rows with Z0, width and reference, and the layout marks
(LCP-4) draw a dashed bracket across each part's gap. A cell instance appears in neither, a nested one has nowhere to go, and
nothing on the canvas shows what a run will leave out.

## Requirements

**R-icp7-1 — A tree, not a list.** Footprint parts stay flat rows, as today. Cell instances appear under their parent
instance (`X1` ▸ `Q3`), each row showing path, cell, a check box, and the verdict line from ICP-2's build (built, or why
not). `*` keeps its row; its tooltip says it takes devices and footprint parts, not copper. A by-cell entry is one row per
cell ("every KIT_FET — 4 placed"). Checking a parent disables the rows under it (ICP-0 D8) rather than letting the run
refuse.

**R-icp7-2 — Per-instance options.** Port at (Pin / Cell), Reference (Ground, Common, or one of its pins, from its pin
list) with a per-pin row under the instance for its own reference, Port off, or Ground (ICP-4), Z0, the nets or layers
kept out of the cut (ICP-2 `Keep`), and the keep-out box (shown in µm, edited as four numbers; empty means the cell's own box). Every edit writes through
`EmComponentPort.Override` (LCP-3's one entry-writing rule) as one undo entry. Picker buttons and their glyphs follow the
panel's existing rules; no control shifts when an option appears.

**R-icp7-3 — Marks on the layout.** From the open `.cem`, built off-thread as LCP-4's are (`ScheduleComponentPortMarks`,
`LayoutView.EditCount` guarding stale builds): what will be left out drawn as a hatched outline (the instance's shapes'
outline in Pin mode, the box in Cell mode), and at each built port the edge-port arrow at the cut face with its number.
Every pin carries its class from ICP-2's per-pin record — a routed pin its port arrow, a grounded pin a ground mark,
an abutting pin a join mark to the pin it touches, a floating pin a warning marker — and a pin left out shows a small
marker whose tooltip is the reason. This is where the ports can be seen landing before a run (ICP-0 § Feedback). A press on a mark selects its instance. Marks follow the
instance when it moves, and draw nothing extra when no `.cem` names it.

**R-icp7-4 — `.c3d` rows.** The `.c3d` editor's component-port rows take the same tree for its layout instances, with the
layout instance as the first level when the view places more than one.

**R-icp7-5 — Off the UI thread.** ICP-2 (R-icp2-8) moved Refresh's build to a worker and shared one cached build between
Refresh, Check, the marks and the mesh preview. The tree and its option edits here read that cache and never build on the UI
thread; an option edit invalidates it through the setup it keys on.

**R-icp7-6 — The solver's geometry, previewed.** A toggle on the layout (beside the marks) draws exactly what
`EmGeometry.ForSetup` hands the extractor for the open `.cem` — the cut applied, kept shapes kept, the solve region
clipped — over the layout dimmed, with each cut-out instance shaded in its outline or box. It reads ICP-2's cached build
(R-icp2-8) and so costs nothing a Refresh has not already paid; it builds nothing of its own, so it cannot show different
metal from the run. The mesh preview, where it is already shown, shades the cut-out instances the same way.

## Gates

1. View-model tests: tree shape for a two-level fixture; a parent checked disables its children; each option writes one
   entry and one undo step.
2. UiSnapshot PNGs, looked at: the section with a three-instance tree and one instance refused; the layout with a Pin-mode
   and a Cell-mode cut and their port arrows (`--clay … --component-ports <cem>`, as LCP-4 added).
3. A stale mark build is discarded after an edit.
4. The solver-geometry preview draws the same shapes `EmGeometry.ForSetup` returns (compared shape for shape), and a
   UiSnapshot PNG of it with one Pin-mode and one Cell-mode cut, looked at.

## Not here

Dragging a port or the box on the canvas (LCP's D2: a component port moves with its instance; the box is typed).

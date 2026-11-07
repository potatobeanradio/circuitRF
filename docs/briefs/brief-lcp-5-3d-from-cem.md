# Brief LCP-5 — A 3D `.cem` on a layout builds component ports

**Series:** LCP-0 … LCP-8 · **Tag:** `R-lcp5-<n>` · **Depends on:** LCP-1
**Area:** `src/Design/Layout/Em3d/Em3dGenerator.cs`, `src/Design/ThreeD/C3dHierarchy.cs`, `C3dProblemAssembly.cs`,
`src/Design/Layout/Em/EmRunService.cs`, the generator's other callers.

## The gap

A 3D `.cem` on a layout and a `.c3d` build their problems through different code. `C3dComponentPorts.Build` needs a `C3dDocument` and a
`C3dElaboration`, whose conductors have the names the ports land on, and `Em3dGenerator.Generate` has neither, so it refuses
(`Em3dGenerator.cs:307`). A `.cem` whose `LayoutRef` names a `.c3d` already works; a `.cem` on the layout does not.

## Approach

**A setup naming component ports runs through an in-memory 3D view of its layout**, by the door `RunThreeDView(..., fromCem: true)`
already offers. That reuses landings, verification, shell bridges and the co-simulation data, and means a `.cem` and a `.c3d` of one
layout build identical ports by construction. Refactoring `Build` onto the generator's output was the alternative, and would leave two
lowerings that have to agree. A setup with no component ports stays on the generator, byte for byte.

## Requirements

**R-lcp5-1 — The in-memory view.** A `C3dHierarchy` function building the document `NewFromLayout` builds, for exactly one setup, with no
scan of other `.cem` files, nothing written and the path it would have had (a relative `CellRef` must resolve). A `.clay` not saved in
a cell is refused with the sentence `NewThreeDViewFromLayoutForLayoutAsync` already uses.

**R-lcp5-2 — A `.cem` is stricter than a `.c3d`.** `TranslateWith` passes `portsEach: true`, which leaves a bad port out and moves a port
to where the plane starts, because a `.c3d` port is a rectangle a user can move afterwards. A `.cem` user cannot, so its drawn ports stay
strict here. Pass strict for this door and clear `ComponentPorts` on the clone given to the generator.

**R-lcp5-3 — Solve Region.** A placed cell does not carry a `.cem`'s region. A 3D `.cem` with both `SolveRegion` and `ComponentPorts` is
refused by name, before meshing. Silently dropping a region would solve a different problem from the one on screen.

**R-lcp5-4 — One question, one answer.** Replace the refusal with one function answering "the problem this setup makes", which each
caller uses: `Viewer3DViewModel.cs:275`, `:287`, `:336`, `EmSetupEditorViewModel.GeometryCheck.cs:30`,
`Em3dRunService.Palace.cs:988`, `:1003`, `Em3dRunService.cs:226`, `Check.cs:1310`, `Em3dSetupSource.cs:179`. **Show 3D** on such a `.cem`
draws the sheets and landings, as a `.c3d` does. `MeshCurrency` must agree, or a finished run reads as stale.

**R-lcp5-5 — Results stay where a `.cem`'s go.** `EmRunService.ResolveSnpPath`, the Output name and `.cemr` behave as for any `.cem`. A
schematic's reference to the result survives a re-run.

**R-lcp5-6 — Reachable from what exists.** The CLI refusal from LCP-2 and `ComponentPortsOnLayoutRefusal` both go. A setup's 3D port
table (`Ports3D`) applies to its drawn ports as today, not to component ports.

**R-lcp5-7 — Marks on the layout.** LCP-4 draws a `.cem`'s component ports on its layout from
`LayoutComponentPortsHost.PrepareMarks`, which builds none for a 3D `.cem` while `Inapplicable` refuses it. Once the ports build, it
returns a `LayoutComponentPortMark` per port whose gap point and direction are the sheet's, so a 3D `.cem` draws the same marker a
Planar one does.

## Gates

**No Palace.** Assert the assembled problem, which needs no mesh:
1. A `.cem` and a `.c3d` built from one layout with one setup yield identical `Em3dProblem` ports, solids and landings.
2. A setup with no component ports yields the generator's problem unchanged.
3. Each refusal: a loose `.clay`, a region beside component ports, a drawn port a strict build refuses.
4. `em --dry-run` and `explain --component-ports` on a 3D `.cem` report the ports, writing nothing.
5. `MeshCurrency` does not read stale across a no-op.

`Viewer3D` gets a `tools/UiSnapshot --c3d`-style render, looked at. A real solve, if wanted at all, is one capped hand run, not a gate:
the existing `[PalaceFact]` on the `.c3d` path already covers what the solver does with these ports.

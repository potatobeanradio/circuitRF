# Brief DFR36-1 — see the mesh before solving, from a 3D view

**Series:** designer feedback round 36. · **Tag:** `R-dfr36-1-<n>`
**Area:** `src/Ui/ThreeD/`, `src/Ui/Viewer3D/`, `src/Ui/ViewModels/WorkspaceViewModel.ThreeD.cs`, `src/Ui/Views/ThreeD/C3dEditorView.axaml`.
Findings go in `src/Ui/RESOLVED.md`. No new meshing code: `RunControl.MeshOnly` and `em --mesh-only` already mesh a setup without solving (`Em3dMeshOnlyReport`).

## The gap

Whether a mesh fits the geometry, or is overkill on a large ground plane, can only be judged by looking at it, and a 3D view shows a mesh only after a run has finished and left fields:
`Viewer3DViewModel.CanShowMesh` is `MeshAvailable && FieldsAvailable`. On a board that takes hours to solve, or does not fit in memory, that is never. The mesh exists minutes in, or could with no solve at all. The planar setup panel already has a mesh-only button (`MeshRequested`); the 3D side is reachable only as `circuitrf em --mesh-only`.

## Decision for the owner before building

**D1. May the mesh toggle work with no fields?** `CanShowMesh` was tied to fields on an owner request of 2026-09-28. Recommended: `CanShowMesh => MeshAvailable`, with the tooltip saying which run or mesh-only pass made it. The toggle is then enabled by any mesh on disk for the active setup, including one a cancelled run left behind.

## Requirements

- **R-dfr36-1-1** A **Mesh** button beside Run Analysis on the `.c3d` toolbar, using a reserved slot so nothing shifts, and a **Mesh Only** item under Simulate. It meshes the active setup with `RunControl.MeshOnly = true` through `RunC3dSetupAsync`'s path: same scratch folder, memory ceiling, Stop and progress row. A setup whose solver has no Gmsh mesh, or a planar or thermal setup, greys it with a reason, using `MeshOnlyRefusal`'s wording.
- **R-dfr36-1-2** When it finishes, the view turns the mesh on (`ShowMeshWhenAvailable`) and Messages states tetrahedra, the pre-mesh estimate and the memory a solve would need, from `Em3dMeshOnlyReport`.
- **R-dfr36-1-3** A mesh that is current is reused, as `--mesh-only` reuses it, and Messages says so. A mesh that no longer matches the setup or geometry is shown only with the existing stale wording, never as if current.
- **R-dfr36-1-4** The same from a `.cem` 3D setup's panel: its existing mesh button runs mesh-only for a 3D setup, and Show 3D displays the result.
- **R-dfr36-1-5** Docs: `reference/em-setup.md` and `reference/drawing-in-3d.md`, in present tense.

## Gates

- View model: after a mesh-only pass with a stand-in mesher, `CanShowMesh` is true, `FieldsAvailable` false and no result record or `status.json` is written.
- A Mesh click on a planar setup is refused with its reason, and starts no process.
- UiSnapshot `--c3d` of a shipped example with its mesh on and no fields: PNG looked at, and listed in the note. A toolbar picture before and after the button is added shows nothing else moved.

## Not in this brief

A per-region element order (Palace's order is one number for the whole problem), and any new mesh-sizing rule for large planes. Once this ships, how much of a mesh sits on a large plane can be measured from the picture before anything is changed.

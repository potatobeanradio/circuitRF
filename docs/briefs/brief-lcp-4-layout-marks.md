# Brief LCP-4 — Component ports drawn on the layout

**Series:** LCP-0 … LCP-8 · **Tag:** `R-lcp4-<n>` · **Depends on:** LCP-1, LCP-3
**Area:** `src/Ui/Layout/LayoutEditorViewModel.cs`, `src/Render/Renderers/LayoutRenderer.cs`, `src/Ui/Controls/LayoutCanvas.cs`,
`EmSetupEditorViewModel`.

## The gap

Component ports are not in the `.clay`, so the layout has nothing to draw. A setup's ports would be invisible where a user is looking at
the part. A `.c3d` draws its component ports in the 3D view; the 2D view has no equivalent.

## Requirements

**R-lcp4-1 — Pushed by the open `.cem`.** The `.cem` editor pushes its built ports to the layout editor, as it already pushes
`PlanarMeshReport` and `PlanarReferencePlanes`. The layout holds no opinion about them. Closing the `.cem` clears them, so a layout with
no `.cem` open shows none. The docs say so (LCP-8). With two `.cem` editors open on one layout, the most recently active one's marks
show.

**R-lcp4-2 — Same symbol, one difference.** Draw through `DrawInternalGapMarker` with the port's number, so a user reads it as what it is
on either solver. A 3D `.cem`'s ports draw the same marker across the gap. Add one read-only cue so it does not read as a label that can
be dragged; settle it from a UiSnapshot rather than in advance. The strip itself is not drawn as copper: it is not in the layout.

**R-lcp4-3 — Not editable.** A mark is not hit-tested as a port. There is no drag, no Port tool interaction, no Properties page and no
delete. Clicking it selects the part under it, because that is the thing that moves it. A hover line may name the port and part.

**R-lcp4-4 — Follows the part.** R-em-17 applies: marks are cleared on an edit, never left stale over edited artwork. They rebuild after
the edit settles, off the UI thread, debounced. On a large board a drag must not stall: no rebuild during the drag.

**R-lcp4-5 — Refused parts** are not drawn. Why is in Messages and the list marks them (LCP-3), so a part with no mark has an answer one
glance away.

**R-lcp4-6 — Pictures and exports are untouched.** `render`, GDSII and other exports stay as they are. Marks are editor overlay state and
never reach a written file, by the same rule the mesh overlay follows.

## Gates

No solves. A view-model test that an edit clears the marks and a settled edit restores them at the part's new place; a test that a mark
takes no drag; a renderer test that a layout with no pushed ports draws byte-identically. **Visual:** `tools/UiSnapshot` of marks on an
axis-aligned, a rotated and a three-pad part, looked at and listed in the closing note.

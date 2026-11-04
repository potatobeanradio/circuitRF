# Brief DFR34-5 — a 3D view that will not build, and new parts placed far from the board

**Series:** designer feedback round 34. · **Tag:** `R-dfr34-5-<n>`

Two smaller items from the same field test. Each needs one decision or one piece of evidence before code.

## 1. "The 3D view did not finish building — Retry" until the workspace was reopened

**The gap.** After a port was added in 2D, the 3D view showed the stalled-build bar. Retry kept failing; closing and
reopening the workspace fixed it. The crash trail at the time shows GPU context loss ("OpenSharedResource failed")
from docking and undocking a laptop. Round 34 made GPU loss recovery retry with backoff and observe those faults, but
the stalled bar is not raised by GPU state. It means no scene arrived within 30 s while a build was still running
(`Viewer3DViewModel.IsFirstSceneStalled`).

The likely cause, not confirmed: every build takes `lock (_elaborating)` (`C3dEditorViewModel` ~244) and checks
cancellation only once it holds the lock. If one elaboration hangs, every Retry queues behind it until the editor and
its lock are thrown away, which is what reopening does.

**R-dfr34-5-1 — Retry never waits on a build it cancelled.** Cancellation is checked inside elaboration and assembly,
not only before the lock. Retry builds with a fresh elaborator if the previous one has not released the lock within
1 s, and the old build's result is discarded when it finishes. **R-dfr34-5-2 — the bar's Retry also reconnects the
pane** when the pane has a GPU fault, and the pane's own fault text gets a Reconnect link, so "close and reopen" is
never the only way back.
**Evidence first:** a `dotnet-stack` dump from a hung build, from the field machine, which settles whether it is a
hang inside elaboration or a lost wake-up. Ask before guessing.

## 2. Update Layout from Schematic puts new parts at the origin

**The gap.** A cell cropped from a Gerber import keeps the board's coordinates, about 160 mm from the origin. A
schematic made for that cell, with no `ArtworkSource`, placed its two new parts in rows starting at (0, 0)
(`SchematicLayoutPlanner.PlaceNewInstances`), 160 mm away. A managed ground via placed beside one of them stayed
there after the parts were dragged onto the board, because a ground via follows its part only on the next update.

**R-dfr34-5-3 — new parts go beside the existing artwork** when the schematic has no `ArtworkSource`: the target
layout's artwork bounding box is passed as `below:`, as `PlaceAtArtwork` already does. An empty layout is unchanged.
**R-dfr34-5-4 — a managed ground via moves with its part** when the part is moved in the layout editor (the via's tag
names its part and pin), so it is not left behind until the next update.

`SchematicLayoutPlanner*` is being changed by other work at the time of writing; start §2 after that lands.

## Gates

- A fake elaborator that blocks: Retry produces a scene from a second build within 2 s, and the blocked build's late
  result is not shown (R-1).
- The cropped-board fixture: new parts are placed within the artwork's bounding box plus one row, not at the origin
  (R-3); dragging a part carries its ground via (R-4).

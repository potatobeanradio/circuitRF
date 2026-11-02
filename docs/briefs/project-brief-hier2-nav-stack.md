---
name: project-brief-hier2-nav-stack
description: Brief hier2: Document navigation stack (SchematicDocument frame stack + active VM retarget) — completed 2026-06-13
metadata:
  type: project
---

Brief hier2 complete. Navigation stack + active-VM retarget in SchematicDocument and SchematicView.

**Why:** Enables in-place hierarchy navigation — a single tab pushes into sub-cells without opening new tabs.

**How to apply:** hier3 (actions + wiring) calls `PushIn`/`PopOut` on the active document; hier4 (breadcrumb) binds to `NavFrames`.

**Canvas zoom/pan flag:** `SchematicCanvas.Model` setter always sets `_needsInitialFit = true`. On retarget, the AXAML `Model="{Binding Model}"` binding fires before `SyncFromVm()` can preserve the flag — so zoom resets to fit on every push/pop. Flagged per brief instructions ("if the canvas ties transform to the model identity, flag it rather than hack"). Fix deferred to hier3+.

[[project-brief-hier1-session-registry]]

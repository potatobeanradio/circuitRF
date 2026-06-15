---
name: project-brief-7.1d-3b-stale-marker-guard
description: Brief 7.1d-3b: stale-marker guard in MarkerEditorViewModel — completed 2026-06-14
metadata:
  type: project
---

Phase 7.1d-3b complete. Added `MarkerIsLive` liveness check to `MarkerEditorViewModel`.

**Why:** Ctrl+Z can remove a marker while its editor flyout is still open (light-dismiss only, not closed by undo). Without the guard, setters silently mutate a detached marker object; redo resurrects it with phantom edits.

**How to apply:** The fix is a single `private bool MarkerIsLive` property + `if (!MarkerIsLive) return;` guard at the top of all nine model-mutating handlers. Read-only display properties (`OwnDataLine`, `OwnZ0Line`, `MultiLines`) are already `_parent`-null-guarded and safe on detached markers — no change needed there. Undo coverage for marker edits intentionally left as-is (owner MINIMAL scope decision).

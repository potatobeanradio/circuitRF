---
name: project-brief-net-label-live-drag-follow
description: Brief net-label-live-drag-follow: anchored net labels track their wire live during drag — completed 2026-06-14
metadata:
  type: project
---

Brief net-label-live-drag-follow: anchored net labels now track their wire continuously during whole-wire drags, segment drags, and component-move wire-follows.

**Why:** Previously labels only snapped into place on drag release; the live wire moved but the label lagged behind until commit.

**How to apply:** Three files changed:
- `SchematicOverlay.cs` — `NetLabelDragPositions: IReadOnlyDictionary<string,(double X,double Y)>?` property added after `WireDragPoints`
- `SchematicViewModel.cs` — `BuildNetLabelDragPositions(livePts)` helper added near `LiveConnectionDots`; wired into both `RebuildDragOverlay` (sets `NetLabelDragPositions = BuildNetLabelDragPositions(wireOverrides)`) and `HandleSegmentDragLive` (same for `wireDragPoints`)
- `SchematicRenderer.cs` — net-label draw loop checks `overlay?.NetLabelDragPositions` before using committed `lbl.X/Y`; uses override position when present

Gate: all 1188 tests green; build clean.

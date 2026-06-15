---
name: project-brief-netlist-pin-label-priority
description: Net naming priority fix — Pin beats coincident label (ground → Pin → label → auto) in AssignNetNames — completed 2026-06-15
metadata:
  type: project
---

Bug fix for silent port disconnection when a cell's interface Pin shared a net with a user net label.

**Root cause:** `NetExtractor.AssignNetNames` applied names in the order ground → labels → Pin (only if unnamed) → auto. A label on the same net as a Pin shadowed the Pin's port name, so `Cell.Ports` (built from Pin names) didn't match the net name seen by the elaborator → floating internal node → singular MNA → "Regularization engaged" per frequency.

**Fix:** Changed the Pin block from `if (!rootToName.ContainsKey(root))` (fill-only) to `rootToName[root] = portName` with a ground guard (skip if existing == "0"). New priority: **ground → label loop (label-vs-label conflict detection unchanged) → Pin block (overrides labels) → auto**.

**Files changed:**
- `src/Ui/Schematic/NetExtractor.cs` — `AssignNetNames`: Pin block now overrides labels; emits "tied to ground" conflict if Pin net is "0"
- `tests/Ui.Tests/NetExtractorPinTests.cs` — 4 new tests (Pin_WithCoincidentLabel_PinNameWins, Label_WithoutPin_NamesNet, TwoDifferentLabels_StillConflict, Pin_OnGroundNet_Warns)
- `tests/Ui.Tests/NetExtractorHierarchyTests.cs` — 1 new test (CellPinWithCoincidentLabel_BindsThroughToParent) + Pin(num, cx, cy, name) overload
- `src/Ui/CLAUDE.md` — Net-name priority rule documented in Net extractor invariants section

**Rule (permanent):** net-name priority is ground → Pin → label → auto; a Pin owns its net's name; labels are cell-local. A Pin coincident with a differently-named label is NOT a conflict — the Pin silently wins.

**Gate:** Build 0W/0E; 1216 tests pass (was 1211).

**Why:** The elaborator's positional port-binding step matches `Cell.Ports` names to body net names. If a label shadowed the Pin name, the body net was named after the label (not the port), the bind step found no match, and the port floated.

**How to apply:** Never add a guard that allows labels to override Pins in `AssignNetNames`. The label-vs-label conflict detection runs in the label loop (before Pins); do not add pin-vs-label conflicts.

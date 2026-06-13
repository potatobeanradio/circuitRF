---
name: project-brief-hier-extract-netextractor
description: Brief hier-extract — hierarchical extraction in NetExtractor — completed 2026-06-13
metadata:
  type: project
---

Hierarchical net extraction is now implemented in `NetExtractor`. **Completed 2026-06-13.**

- **`ICellResolver` interface** (`src/Ui/Schematic/ICellResolver.cs`) — framework-free seam; `WorkspaceViewModel` will implement for registry-else-disk resolution (brief-run-wire).
- **`ExtractionResult.Library`** — `Library("netlist")` field, empty for flat schematics.
- **`Extract` signature** — now `Extract(model, testBenchName="tb", cells=null)`; null cells = flat back-compat.
- **`ExtractModel`** — private helper containing the union-find/naming/emit pipeline; shared by top and sub-cells.
- **`EmitCellInstance`** — resolves, cycle-guards, dedupes by name, port-count guards, binds nets positionally.
- **`NetForPort`** — static helper shared by `EmitInstance` (primitives) and `EmitCellInstance`.
- **6 tests** in `tests/Ui.Tests/NetExtractorHierarchyTests.cs`: flat regression, single 2-port cell, reuse/dedupe, nested (leaf-first), cycle, port mismatch.

**Why:** Gate tests 41 NetExtractor tests pass; full suite 1165 green; firewall green.

**How to apply:** Next step is brief-run-wire (ICellResolver implementation in WorkspaceViewModel) and brief-cnl-cells (emit Library in CnlWriter via the existing `Write(tb, library)` overload).

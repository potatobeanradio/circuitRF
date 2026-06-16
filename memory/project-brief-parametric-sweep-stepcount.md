---
name: project-brief-parametric-sweep-stepcount
description: Parametric sweep Start/Stop/Step|Npts/log CNL forms + SweepExpander moved to Core — completed 2026-06-16
metadata:
  type: project
---

Implemented Start/Stop/Step and Start/Stop/Npts (lin/log) support for parametric sweeps.

**Why:** Needed compact CNL form (not 121-number Values= list) for sweep specs that are authored in the UI editor, and needed CNL reader to accept the compact form for hand-written netlists.

**Key changes:**
- `SweepExpander` + `SweepAxisMode` moved from `src/Ui/Schematic/` → `src/Core/Design/SweepExpander.cs` (Core firewall — CNL reader is in Core and needed the expander)
- `SweepSpec` redesigned: `{ Start, Stop, StepOrCount, Mode: SweepAxisMode, Kind: SweepKind }` (no Variable field)
- `ParametricSweepAnalysis`: spec constructor expands eagerly + stores `Spec?` for round-trip; array constructor keeps `Spec = null`
- CNL reader `TryParseParametricSweepDirective`: parses `Values=` (list, Spec=null) and `Start= Stop= (Step=|Npts=) [log|log=true]` (spec retained); bare `log` via `HashSet<string> bare`
- CNL writer: emits compact `Start=/Stop=/Step=|Npts=` when `psa.Spec != null`
- `SweepAxisRowViewModel`: `BuildSpec() → SweepSpec?` (null for List mode); `FromPsa` restores from spec
- `AnalysisEditorViewModel.BuildAnalyses()`: uses spec constructor for StepSize/PointCount axes
- 6 gate tests in `tests/Core.Tests/Netlist/SweepSpecCnlTests.cs`

**How to apply:** Sweep analyses created via UI or via Start/Stop/Step CNL form carry `Spec != null` and round-trip compactly. Values= list form still works and is backward compatible.

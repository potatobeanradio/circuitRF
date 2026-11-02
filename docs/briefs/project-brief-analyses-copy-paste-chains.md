---
name: project-brief-analyses-copy-paste-chains
description: CloneAnalysis handles PSA + copy expands chains + paste remaps InnerAnalysisName — completed 2026-06-17
metadata:
  type: project
---

CloneAnalysis handles ParametricSweepAnalysis; Copy expands base selections to whole chains; Paste remaps InnerAnalysisName and re-targets lone sweeps. 8 gate tests.

**Why:** Duplicating/pasting a sweep crashed (missing arm). Copy silently dropped the sweep wrappers. Paste broke inner links on name collision.

**How to apply:** CloneAnalysis is the single clone point — callers pass optional newInnerName. ExpandSelectionToChains is internal on AnalysesListViewModel. PasteAnalysesCommand takes optional retargetInner.

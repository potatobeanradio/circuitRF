---
name: project-brief-loadpull-ui-07-extraction-and-run
description: Loadpull UI 07 — confirm LP/LPP extraction + .cnl round-trip + end-to-end Run; path-base convention; completed 2026-06-23
metadata:
  type: project
---

# Loadpull UI 07 — extraction + .cnl round-trip + end-to-end Run — COMPLETE 2026-06-23

Closed the loop so an authored Loadpull / Loadpull-Pursuit runs from the GUI. As the brief predicted,
**almost everything was already wired** — this brief is confirm + tests + docs.

**Confirmed already-done (no code change needed):**
- **Extractor:** `NetExtractor.Extract` copies every `model.Analyses` entry into `tb.Analyses` (LP/LPP are
  `Analysis` subclasses → carried as-is).
- **Writer:** `CnlWriter.FormatLoadpullAnalysis` / `FormatLoadpullPursuitAnalysis` emit both directives
  (`type=loadpull` / `type=loadpull_pursuit`) with the full key set (Tone/ToneUnit/MaxHarm/LoadTuner/
  SourceTuner/Grid/Sweep/TuneHarm/Compression/GainType/Pin*/Tickle/MaxIter/FFTOverSample/Tol/DriveStepping/
  GuardHarmonic; + pursuit EffType/ZsourceOBO/SearchMethod/OutputGrid/VSWR1/VSWR1_resolution/VSWR2/
  VSWR2_resolution/keepNonconvergingPoints/nonconvergentVSWR/CreateLoadpullResult/LoadpullResultZsource).
  `enabled=false` appended by the Write loop.
- **Reader:** `CnlReader` parses every emitted key back (matching spellings + defaults); `ParseEnabledToken`
  reads enabled. Round-trip is lossless.
- **Run dispatch:** `SchematicRunService` dispatches `LoadpullAnalysis`→`LoadpullEngine`,
  `LoadpullPursuitAnalysis`→`LoadpullPursuitEngine`, passing `nl.GlobalsWithExplicitUnit` (brief 04b).
- **Path resolution** was already correct after the brief-06 follow-up fix: pickers store relative to the
  **workspace root** = the `netlist.cnl` directory = `CnlReader._sourceDirectory` (the reader's resolution
  base). Bases match → relative `.gam` paths resolve correctly regardless of schematic location.

**Added:**
- `docs/design/net-extraction-and-run.md` §3.1 — documents the single path-resolution base convention
  (Grid/OutputGrid/SnP File resolve against the netlist dir; pickers store relative to the workspace root).

**Gate (7 tests):**
- `tests/Core.Tests/Netlist/LoadpullCnlRoundTripTests.cs` (6): LP all-fields round-trip; LPP all-fields
  round-trip; LPP no-OutputGrid→null; relative Grid resolves against the netlist dir; relative OutputGrid
  resolves against the netlist dir; absolute Grid preserved verbatim.
- `tests/Engine.Tests/Loadpull/LoadpullCnlWriterRunTests.cs` (1, end-to-end): read `testdata/Hero3/
  hero3.cnl` → run (baseline) vs `CnlWriter.Write` → `CnlReader.Read` → run; identical sweep shape + Pout
  at every converged grid point (proves the GUI write→read→run plumbing reproduces the hand-authored run;
  engine math unchanged).

Build 0W/0E; Core 382(+6) / Ui 1450 / Engine 442(+1) / Firewall 4 — all green.
**Loadpull UI series (briefs 01–07 + 04b) COMPLETE.**

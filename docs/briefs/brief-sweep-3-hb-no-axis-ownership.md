# Sonnet Brief — Sweep Fix 3/5: demote HB-internal sweep to "no axis ownership" (one sweep model)

**Goal.** There are two parallel sweep systems and they confuse users and developers:
- **`ParametricSweepEngine`** — generic: overrides any `GlobalVariable`, re-elaborates per point, **stacks a
  correctly-named axis**, nests. (The keeper.)
- **HB-internal sweep** (`HbAnalysisParams.SweepVar*` + HB's own loop) — redundant, no nesting, and (pre-Brief-1)
  mislabeled its axis. The drive amplitude does NOT need its own bespoke loop; with the VAR work, the drive
  magnitude / available power is just a variable.

**Decision (owner-confirmed): `ParametricSweepEngine` is the single nested-sweep authority. HB stops owning a
swept result axis.** HB keeps only its internal *continuation* stepping for convergence (warm-start from the
previous point within a single Newton-hard solve), but it no longer emits a swept output axis — the outer
`ParametricSweepEngine` supplies all swept axes.

## What "no axis ownership" means precisely
HB's `Run`/`RunTwoTone` should produce a **single-operating-point DataSet** (no swept axis): `[node, harmonic]`
(single-tone) / `[node, mixIndex]` (two-tone), scalar `Converged`/`Residual`, `I:<branch>` over `[harmonic]` /
`[mixIndex]`. When the user wants to sweep the drive (or anything), they wrap the HB analysis in a
`ParametricSweepAnalysis` over the drive variable — `ParametricSweepEngine` re-elaborates per point and stacks the
named axis. This is exactly the no-sweep branch the builders already have.

## Required reads
- `src/Engine/HarmonicBalance/HbEngine.cs` — the sweep loop in `Run` and `RunTwoTone`, `HbAnalysisParams`
  (`SweepVar*`, `HasSweep`, `SweepValues()`), `UpdateSweepPoint`, `ReEvaluateGlobals`, and the no-sweep branch in
  `BuildSingleToneDataSet`/`BuildTwoToneDataSet`.
- `src/Engine/ParametricSweepEngine.cs` — `Run`/`RunInner` (the keeper path).
- The HB **directive** `HarmonicBalanceAnalysis` (`SweepVarName/SweepStart/Stop/Step`) and the `.cnl` reader /
  Edit-Analysis UI that populate them — to retire/redirect those fields (Brief 4 builds the replacement UI).

## Changes

### A. HbEngine: collapse to single-point production
- Make `Run(p)` and `RunTwoTone(p)` always execute the **single-point** path (the existing `isSweep == false`
  behavior), regardless of `p.HasSweep`. Simplest: ignore `p.SweepVar*` for axis production and always build the
  no-swept-axis DataSet.
- **Preserve convergence continuation** only if it still has meaning without an internal sweep: with no internal
  sweep there is exactly one Newton solve, so the warm-start-from-previous-point logic is moot — remove the
  internal sweep loop, keep `InitialGuess` cold-start (DC seed). `RunSinglePoint` already does exactly this — Run
  can converge toward that shape. (Don't break `RunSinglePoint`; LoadpullEngine depends on it.)
- Remove the now-dead `UpdateSweepPoint`/`ReEvaluateGlobals` **HB-internal** sweep usage from the Run loops.
  NOTE: `ParametricSweepEngine` re-elaborates from the TestBench per point, so the per-point variable application
  HB used to do is now done by re-elaboration upstream — confirm `ToneSourceModel` picks up the swept value via
  the fresh elaboration (it does: overrides land in `GlobalVariables` → scope → model). Keep `UpdateSweepPoint`
  only if `RunJacobianDiagnostic` still needs it; otherwise delete.
- Delete the `"Pin"`/sweep-axis code paths in both builders (the Brief-1 rename makes this a clean excision):
  remove the `isSweep == true` branches; keep only the single-point cube construction.

### B. HarmonicBalanceAnalysis directive: retire the internal sweep fields
- Mark `SweepVarName/SweepStartExpr/SweepStopExpr/SweepStepExpr` **obsolete** (keep the properties for `.cnl`
  back-compat read, but the engine ignores them). Add an XML-doc note: "Deprecated — wrap the analysis in a
  ParametricSweepAnalysis to sweep. Retained for .cnl read compatibility; not used by the engine."
- `.cnl` reader: if an HB directive still carries `Sweep=...`, **auto-translate** it to a wrapping
  `ParametricSweepAnalysis` at read time (so old netlists keep working): create the HB analysis without the
  sweep, plus a `ParametricSweepAnalysis(name+"_sweep", sweepVar, expand(start,stop,step), innerName=HB)` and
  make the sweep the runnable analysis. If that translation is non-trivial in the current reader, instead emit a
  one-time warning ("HB Sweep= is deprecated; use a parametric sweep") and ignore it — **flag which you did**.

### C. Workspace run path
- Where the workspace decides how to run an HB analysis (the dispatch that calls `HbEngine` vs
  `ParametricSweepEngine`): ensure a bare HB analysis runs single-point, and a sweep is expressed as a
  `ParametricSweepAnalysis` wrapping it (Brief 4 wires the UI to build that). Confirm `RunResultsWriter`/the
  results path is unaffected (it just writes whatever DataSet comes back).

## Tests (`tests/Engine.Tests`)
1. **Hb_AlwaysSinglePoint:** `HbEngine.Run` on an analysis that previously had `SweepVar` set → produces a
   `[node, harmonic]` DataSet with **no** swept axis (HB no longer owns the axis).
2. **Sweep_Wraps_Hb:** a `ParametricSweepAnalysis` over the drive variable wrapping the HB analysis → result has
   the drive axis (correct name) and each slice equals a single-point HB at that drive value. This is the new
   canonical "power sweep."
3. **Cnl_BackCompat:** an old `.cnl` HB directive with `Sweep=` either runs (auto-translated to a parametric
   sweep) or warns-and-ignores — assert the chosen behavior.
4. **RunSinglePoint_Unbroken / Loadpull_Unbroken:** `RunSinglePoint` and a small loadpull smoke still pass
   (they must not regress — LoadpullEngine drives HB per point itself).

## Gate
Build 0W/0E; tests green. Manual: a plain HB run yields a single-point cube; wrapping it in a parametric sweep
over the drive (or any VAR) yields the swept cube with the right axis name; an existing 7.3b-style HB sweep now
goes through the parametric path and is self-describing. No duplicate/confusing "two ways to sweep."

## On completion
Note in `src/Engine/CLAUDE.md`: HB no longer owns a swept result axis — it always produces a single operating
point; all sweeping (drive/power/geometry/bias, nested) goes through `ParametricSweepEngine`. The HB directive's
`Sweep*` fields are deprecated (kept for `.cnl` read back-compat, {auto-translated|ignored-with-warning}).
`RunSinglePoint` (loadpull) is unchanged. Next: Brief 4 (Edit Analysis UI for nested sweeps).

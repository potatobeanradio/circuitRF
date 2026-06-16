# Sonnet Brief — Sweep Fix 1/5: kill the hardcoded "Pin"/"dBm" HB result axis

**Goal.** The HB engine labels its swept result axis **`"Pin"` with unit `"dBm"`** no matter what variable was
actually swept. Make the axis self-describing: name = the actual swept variable, unit = neutral/derived. This is
the smallest, highest-value fix — it lets the user reconcile what a 7.3b HB sweep actually produced.

**Root cause (confirmed).** In `src/Engine/HarmonicBalance/HbEngine.cs`, both DataSet builders hardcode:
```csharp
var pinAxis = new Axis("Pin", sweepVals, "dBm");   // BuildSingleToneDataSet AND BuildTwoToneDataSet
```
The swept *variable* is already generic (`HbAnalysisParams.SweepVarName` flows through `UpdateSweepPoint` →
globals → `ToneSourceModel.ReevaluateFromGlobals`). Only the **axis label** is a sentinel.

## Change
In `BuildSingleToneDataSet` and `BuildTwoToneDataSet`, thread the actual sweep variable name through and use it:
- Pass `string sweepVarName` (from `p.SweepVarName`) into both builders (add a param; both are private static).
- Replace `new Axis("Pin", sweepVals, "dBm")` with `new Axis(sweepVarName ?? "sweep", sweepVals, "")`.
  - Unit: **empty string** for now (the engine does not know the variable's unit — that's the design-layer's
    job; see Brief 5 where the UI captures a unit). Do **not** assume "dBm". If `sweepVarName` is null (no-sweep
    path doesn't build a sweep axis anyway), the fallback name is unused.
- Update the two call sites in `Run`/`RunTwoTone` to pass `p.SweepVarName`.
- The `[node, harmonic, Pin]` comments and any local var named `pinAxis` → rename to `sweepAxis` for clarity
  (cosmetic but removes the "Pin" smell from the code).

**Do not** change the `I:`/`V`/`INl` cube *contents* or the harmonic/node axes — only the swept axis's name+unit.
The diagnostic `Console.Error` lines that print `{p.SweepVarName}=...` are already generic — leave them.

## Note on this being interim
Briefs 3–4 will **remove HB's internal sweep axis ownership entirely** (the outer `ParametricSweepEngine` will
own all swept axes with correct names+units). This brief is the immediate de-sentinel so existing HB-internal
sweeps stop lying about their axis. Keep the change minimal so Brief 3 can cleanly excise it.

## Tests (`tests/Engine.Tests`)
1. **HbSweep_AxisNamedAfterVariable:** run a single-tone HB with `SweepVarName="Vdrive"` over a few values →
   the result DataSet's swept cube axis is named `"Vdrive"` (not `"Pin"`), unit empty. Two-tone path likewise.
2. **HbSweep_NoSweep_NoAxis:** a no-sweep HB still produces `[node, harmonic]` cubes with no swept axis
   (regression — unchanged).
3. Existing HB tests that asserted an axis named `"Pin"` (if any) → update them to the variable name; if a test
   *depended* on the literal "Pin", that dependency was the bug — fix the test.

## Gate
Build 0W/0E; tests green. Manual: re-run the 7.3b HB sweep → the produced `.npy`'s swept axis now carries the
variable name you actually swept, so the cube/family picker shows the real axis. (This is the grounding the user
asked for.)

## On completion
Note in `src/Engine/CLAUDE.md`: the HB result's swept axis is named after `HbAnalysisParams.SweepVarName` (unit
empty), not the legacy hardcoded `"Pin"/"dBm"`. HB-internal sweep axis ownership is slated for removal in the
parametric-sweep consolidation (Briefs 3–4).

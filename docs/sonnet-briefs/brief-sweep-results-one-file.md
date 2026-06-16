# Sonnet Brief — Sweep results: write ONE file per sweep tree, under the inner analysis name

**Problem (confirmed).** A single HB sweep produces **two** `.npy` files: `HB1.npy` and `HB1_sweep_Pin.npy`.
The Sweep-4 UI builds two analyses — the inner `HB1` (HarmonicBalanceAnalysis) and the wrapping
`HB1_sweep_Pin` (ParametricSweepAnalysis) — and `SchematicRunService.RunNetlist` runs **every enabled analysis in
`tb.Analyses`**, so both execute and both get written by `RunResultsWriter`. The inner `HB1.npy` is a
**single-point** HB run (HB no longer self-sweeps) — not useful to the user, and confusing.

**Desired scheme (owner-specified).** A parametric sweep is the runnable artifact; its inner analysis is
plumbing. So:
- An analysis that is **referenced as the `Inner` of some `ParametricSweepAnalysis`** (transitively) must **not**
  be run or written on its own.
- The outermost sweep writes its result under the **root inner analysis's name** — e.g. the `HB1` tree writes
  **`HB1.npy`** (not `HB1_sweep_Pin.npy`). One file regardless of nesting depth (1 sweep or N nested sweeps → one
  `HB1.npy`).

## Where to change it — `SchematicRunService.RunNetlist` (`src/Ui/Schematic/SchematicRunService.cs`)
The dispatch loop is `foreach (var analysis in tb.Analyses) { if (!analysis.Enabled) continue; … }`. Two edits:

### 1. Skip analyses that are the inner of a parametric sweep
Before the loop, build the set of analysis names referenced as an inner (transitively) by any
`ParametricSweepAnalysis`:
```csharp
// Names that are wrapped by a parametric sweep — these run only via their wrapping sweep.
var innerOfSweep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var a in tb.Analyses)
    if (a is ParametricSweepAnalysis ps && !string.IsNullOrEmpty(ps.InnerAnalysisName))
        innerOfSweep.Add(ps.InnerAnalysisName);
```
(One level is enough to collect the set: in a chain `SW_Vgg → SW_Pin → HB1`, both `SW_Pin` and `HB1` appear as
some sweep's `InnerAnalysisName`, so both land in the set. Only the outermost — referenced by nobody — survives.)

In the loop, skip them:
```csharp
foreach (var analysis in tb.Analyses)
{
    if (!analysis.Enabled) continue;
    if (innerOfSweep.Contains(analysis.Name)) continue;   // runs only via its wrapping sweep
    …
}
```

### 2. Name the sweep's result after its ROOT inner analysis
When dispatching a `ParametricSweepAnalysis`, the `AnalysisResult.Name` should be the **root inner** analysis
name, not the sweep's own name. Add a helper that walks `InnerAnalysisName` down to the first non-sweep analysis:
```csharp
private static string RootInnerName(ParametricSweepAnalysis sweep, TestBench tb)
{
    Analysis? cur = sweep;
    var guard = 0;
    while (cur is ParametricSweepAnalysis ps && guard++ < 64)
        cur = tb.Analyses.FirstOrDefault(a => a.Name == ps.InnerAnalysisName);
    return cur?.Name ?? sweep.Name;
}
```
In the `ParametricSweepAnalysis psa` case of `RunTypedAnalysis` (or where the `AnalysisResult` is constructed),
use `RootInnerName(psa, tb)` for the result name instead of `psa.Name`. So the dispatch produces one
`AnalysisResult("HB1", <swept DataSet>)` and `RunResultsWriter` writes a single `HB1.npy`.

(Keep `DeduplicateName` — if two distinct sweep trees somehow resolve to the same root name, the existing `_2`
suffix guard still applies.)

## Edge cases to handle
- **Disabled inner is irrelevant** — the skip is by name membership in `innerOfSweep`, independent of the
  inner's own `Enabled`. Good (the inner being enabled was what caused the double-write).
- **An analysis that is BOTH standalone-desired AND wrapped:** not supported by this scheme (an inner is plumbing
  only). If a user genuinely wants the single-point HB too, they'd add a separate HB analysis. Acceptable —
  matches the owner's "reduce files to search through" intent. Don't add a flag for it now.
- **Sweep with a missing inner** (`InnerAnalysisName` not found): `ParametricSweepEngine.Run` already throws a
  clear error; the dispatch catches it into the per-analysis error list. Leave as-is.
- **Raw-directive sparam path** (the `tb.RawDirectives` loop) is unaffected — parametric sweeps are typed
  analyses, not raw directives.

## Tests (`tests/Ui.Tests` — headless, `SchematicRunService`)
1. **SingleSweep_WritesOneResult_NamedAfterInner:** a tb with `HB1` (HB) + `HB1_sweep_Pin` (ParametricSweep,
   Inner=HB1) → `RunNetlist` returns exactly **one** `AnalysisResult` named `"HB1"` (not `"HB1_sweep_Pin"`, no
   second result for the bare HB).
2. **NestedSweep_WritesOneResult:** `HB1` + `SW_Pin`(Inner=HB1) + `SW_Vgg`(Inner=SW_Pin) → one `AnalysisResult`
   named `"HB1"`, carrying both swept axes.
3. **StandaloneAnalysis_StillRuns:** a plain HB with no wrapping sweep → one result named after it (regression —
   the skip set is empty).
4. **MixedAnalyses:** a standalone S-param `SP1` plus a wrapped `HB1` tree → two results: `SP1` and `HB1`
   (the inner HB and the sweep wrapper don't produce extra files).

## Gate
Build 0W/0E; tests green. Manual: re-run the attached `HBTest` sweep → exactly **one** `HB1.npy` appears under
`results/<schematic>/` (no `HB1_sweep_Pin.npy`), and it contains the swept cube. Reload it in the data display
and confirm the Pin axis is present.

## On completion
Note in `src/Ui/CLAUDE.md`: a parametric-sweep tree writes a single results file named after its **root inner
analysis** (`HB1.npy`, not `HB1_sweep_Pin.npy`); analyses referenced as the `Inner` of a sweep are not run/written
standalone. Reduces the file count the user searches through.

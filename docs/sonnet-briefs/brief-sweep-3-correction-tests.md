# Sonnet Correction Brief v2 — Sweep-3 test migration (COMPLETE map; supersedes v1)

## Read this first — two corrections to my earlier brief

My previous correction brief got two things wrong, and you correctly noticed one of them. Fixing the record:

1. **`Hero2ParametricSweepTests` do NOT already pass.** I was wrong that they were clean. Their `Cnl` constant
   nests an **inner HB `Sweep="Pin:..."`** under the outer Vgg parametric sweep, and they assert a 4-D cube
   `[Vgg, node, harmonic, Pin]`. Brief 3 removed HB's inner Pin axis, so the cube is now 3-D `[Vgg, node,
   harmonic]`. These tests must be migrated too (see §C).
2. **The blast radius is bigger than v1 said** — it includes the **export round-trip tests, the golden
   *generators*, Hero4/Hero5, two-tone measurements, and one diagnostics warning-string test.** Full list below.

**But the core principle is unchanged and still correct: the ENGINE IS RIGHT. Do not modify `HbEngine.cs`,
`ParametricSweepEngine.cs`, `HbLinearBackSolver.cs`, `HbRunResult.cs`, or any non-test engine file to make a test
pass.** Every failure below is a test that encodes the *old* HB-internal-sweep contract. Your instinct to "make
them expect the Pin axis again" is the wrong direction — that would resurrect the behavior Brief 3 deliberately
deleted. The fix is always: **drive the sweep through `ParametricSweepEngine`** (or scope the test to
single-point), then reshape indexing.

If any test truly cannot pass without an engine change, STOP and flag it to the owner — do not change the engine.

## Root cause (one sentence)
Brief 3 made `HbEngine.Run` always single-point (2-D `[node,harmonic]`, scalar `Converged`, back-solver
`SweepCount==1`); ~26 tests still call `engine.Run(p)` with a sweep and read a swept axis / swept payload that no
longer exists.

## The canonical migration pattern (apply everywhere)
**Old (broken):**
```csharp
var p  = HbEngine.Resolve(hba, netlist.ResolvedGlobals);   // hba has Sweep="Pin:..."
var ds = new HbEngine(netlist, tb).Run(p);
var sweepVals = ds["Converged"].Axes[0].Values;            // swept axis — gone
var v = (Complex)ds["V"][nodeIdx, k, si];                  // [node,harmonic,Pin] — now [node,harmonic]
```
**New (correct):** wrap the HB analysis in a `ParametricSweepAnalysis` over the drive variable and run the sweep
engine. The drive variable is whatever the HB `Sweep=` used to name (e.g. `Pin`, or `Vdrive`). The CNLs already
declare a global for it (e.g. `Pin = -20` with `Vs_mag = sqrt(8*10^((Pin-30)/10)*50)`), so the sweep just
overrides that global per point.
```csharp
// Build a parametric sweep over the same variable/values the HB Sweep= used.
double[] vals = /* the Pin points, e.g. linspace(-20,-9,step 1) */;
var sw = new ParametricSweepAnalysis("SW", "Pin", vals, hba.Name);
tb.Analyses.Add(sw);                                        // inner hba must also be in tb.Analyses
var ds = ParametricSweepEngine.Run(sw, lib, tb);
var sweepVals = ds["Converged"].Axes[0].Values;            // now the Pin axis (prepended → axis 0)
var v = (Complex)ds["V"][si, nodeIdx, k];                   // swept axis is FIRST now
var iBranch = (Complex)ds["I:M1:d"][si, k];                 // branch cube: [Pin, harmonic]
```
**Axis order flips from last to first.** Old swept cube `V[node,harmonic,si]` → new `V[si,node,harmonic]`. Branch
`I[harmonic,si]` → `I[si,harmonic]`. `Converged` is still 1-D but now the *prepended* sweep axis. Reshape every
index accordingly.

**Removing the dead `Sweep=` from the HB line is optional** (the engine ignores it). Cleaner to delete it so the
test reads true, but not required.

## Per-file work

### A. `EngineDiagnosticsChannelTests.Hb_MaxIterOne_EmitsOneConvergenceSummaryWarning` — TRIVIAL, do first
This is NOT a sweep-shape issue. Brief 3 changed the single-point non-convergence warning text from
`"HB did not converge at N of M sweep point(s) ..."` to `"HB did not converge (‖F‖=... after N iterations); ..."`.
The test asserts the substring `"sweep point(s)"`, which no longer appears.
**Fix the test assertion**, not the engine: assert it contains `"did not converge"` (already checked) and drop /
replace the `"sweep point(s)"` assertion with something in the new message, e.g. `Assert.Contains("did not
converge", ncWarn)` only, or `Assert.Contains("‖F‖", ncWarn)`. (The other test in this file, the S-param
floating-node one, is unrelated and should still pass — if it fails, that's a separate signal, flag it.)

### B. `NpyRoundTripTests` (11) — single-point is fine; just drop the sweep
The shared `GetHero2Result()` does `Resolve(...) with { SweepStop=-14.0, SweepStep=2.0 }` then `Run(p)`, and the
linnet tests iterate `si` over `p.SweepCount` / `GetBSrc(si,...)` / `GetINl(si,...)`. The round-trip oracle does
**not need a sweep** — it validates bitwise export/import of whatever DataSet + payload it's given. Simplest
correct fix:
- Change `GetHero2Result()` to a **single-point** run: drop the `with { SweepStop, SweepStep }`, just
  `Resolve(hba, …)` then `Run(p)`. The back-solver/payload is then `SweepCount==1`.
- The linnet tests already loop `for si in 0..SweepCount` — with `SweepCount==1` they run one iteration and still
  validate the round-trip. Confirm `ILinearNetworkPayload.SweepCount` reports 1 (not the old swept count) and
  `GetBSrc(0,…)`/`GetINl(0,…)` work. The basic round-trip tests (DataKind/shape/axes/values) don't touch the
  sweep at all and will pass once the run produces a valid single-point DataSet.
- If any linnet dimension assertion hard-codes a multi-point sweep, relax it to `SweepCount` (which is now 1).
Do **not** route this through ParametricSweepEngine — the export payload (`LinearPayload`/`BackSolver`) comes from
`HbRunResult`, which only the direct `HbEngine.Run` path produces. Single-point is the right scope here. (A
sweep-aware exported payload is a future item, same note as the back-solver.)

### C. `Hero2ParametricSweepTests` (4) — convert the inner HB Pin sweep to a nested parametric sweep
The fix that matches the new architecture: the inner `Pin` sweep becomes its **own** `ParametricSweepAnalysis`
nested *inside* the Vgg (and Vdd) sweeps, so the Pin axis is produced by the sweep engine, not HB.
- In the `Cnl` constant: change the HB line to single-point (`analysis HB1 type=hb Tone=2e9 MaxHarm=3 Tol=1e-6` —
  drop `Sweep="Pin:..."`), and insert a Pin parametric sweep as the innermost wrapper:
  ```
  analysis HB1   type=hb              Tone=2e9 MaxHarm=3 Tol=1e-6
  analysis SW_Pin type=parametric_sweep Var=Pin Values=-20,-19,-18 Inner=HB1
  analysis SW1   type=parametric_sweep Var=Vgg Values=-3.0,-3.2    Inner=SW_Pin
  ```
- Now the cube axis order is `[Vgg, Pin, node, harmonic]` (outer→inner sweeps prepend, then the HB single-point
  axes). **This changes the expected axis positions** in the asserts:
  - `SingleLevel_VggSweep_PrependsSweepAxis`: axes become `[Vgg(2), Pin(3), node, harmonic]`. Update asserts:
    `Axes[0].Name=="Vgg"`, `Axes[1].Name=="Pin"`, then `node`, `harmonic`. (The test's current "harmonic last,
    Pin last" expectation is the old HB-inner-sweep ordering — replace it.)
  - `TwoLevel_VggVdd_PrependsTwoAxes`: with the Pin sweep nested innermost, axes become `[Vgg, Vdd, Pin, node,
    harmonic]`. Update the rank (≥5 still holds) and the per-axis name asserts.
  - `SingleLevel_PositionalSlice_WorksAtEachVggPoint` and `SingleLevel_DcDrainCurrent_ShiftsWithVgg`: reshape the
    positional indices to the new order (`vCube[vggIdx, pinIdx, node, harm]`; `iDrain[vggIdx, pinIdx, harm]`).
  - `CnlRoundTrip_ParametricSweepDirective_Parses`: still valid; just ensure SW1.Inner is now `SW_Pin` if you
    renamed, or keep SW1→HB1 and add the Pin sweep as a sibling — **pick the nesting that the test asserts and
    make them consistent.** Cleanest: SW1(Vgg) → SW_Pin(Pin) → HB1.
- These tests then genuinely exercise nested parametric sweeps end-to-end (which is the whole point of Brief 3 +
  the sweep consolidation).

### D. `Hero2Tests` (4), `Hero2RegressionTests` (3) — apply the canonical pattern
`SimpleSweep`, `JacobianFd_…`, `ConvergenceTargets_B1`, `HbSolve_Hero2_…`, `Hero2_PhysicsAnchors`, and the V/INl
`RunAndCompare` golden tests: migrate each to run the Pin sweep via `ParametricSweepEngine` and reshape indexing
to swept-axis-**first** (`ds["V"][si, idx, k]`, `ds["I:M1:d"][si, k]`). For `JacobianFd_…`, get `Vstar` by slicing
the parametric `ds` at the chosen drive point (`ds["V"][si, .., ..]`) and call `engine.RunJacobianDiagnostic(p,
Vstar, driveValue)` on a single-point `HbEngine` — the diagnostic is single-point and stays on HbEngine. Goldens
are unchanged (see E).

### E. `Hero2GoldenGenerator` + `Hero5GoldenGenerator` — regenerate via the parametric path
These **write** the golden CSVs the regression tests read. They currently run `engine.Run(p)` swept and read
`ds[...][idx,k,si]`. Migrate them to `ParametricSweepEngine.Run(sw, lib, tb)` and the swept-axis-first indexing,
exactly like the regression tests — so the generator and the consumer agree on shape. The **CSV content** (freq,
Pave, Re, Im rows) should come out numerically identical to before (same physics, same points), so the existing
committed golden CSVs remain valid and the regression tests in D still match them. Run the generators once to
confirm the CSVs are byte-stable (or re-commit if only formatting changed). If the numbers shift beyond noise,
STOP — that means the migration changed physics and is wrong.

### F. `Hero4Tests`, `Hero5*` (gate/integration/generator), `TwoToneMeasurementsTests`, `Hero2MeasurementTests`
Same canonical migration (two-tone variants read `[node, mixIndex, si]` → `[si, node, mixIndex]`). For two-tone,
wrap the two-tone HB analysis in the `ParametricSweepAnalysis` over the drive variable; the mixIndex axis stays as
HB's inner single-point axis, the drive axis is prepended by the sweep engine. `TwoToneMeasurementsTests` /
`Hero2MeasurementTests` read measurement selectors over the swept cube — reshape the same way (swept axis first).
Confirm the measurement library's mixIndex selectors still find the right axis by name (they should — name-based,
not positional).

### G. Leave alone
`NoSweepHbTests` (already single-point contract), `HbSweepAxisNameTests.HbSweep_NoSweep_NoAxis`, and
`ParametricSweepDcSParamTests` (Brief 2) — do not touch. The two `HbSweepAxisNameTests` swept tests: migrate per
§C's pattern (parametric sweep over `Vdrive`, assert axis 0 == `Vdrive`).

## Order of operations (fastest path to green)
1. §A diagnostics warning string (1 line).
2. §B `NpyRoundTripTests` (drop the `with{Sweep…}` → single-point). Clears 11 at once.
3. §E golden generators → regenerate; confirm CSVs stable.
4. §C/§D/§F migrate the swept reads to the parametric path + reshape indices.

## Hard rules (unchanged)
- Engine files are correct — do not edit them to satisfy a test. Flag, don't patch, if a test seems to need it.
- Sweeps run only through `ParametricSweepEngine`. Never resurrect HB-internal sweeping.
- Golden CSV *numbers* must not change (only the generator's plumbing). If they do, the migration is wrong — stop.
- Swept axis is prepended (first), not appended (last). Reshape every index.

## Gate
`dotnet build` 0W/0E; `dotnet test tests/Engine.Tests` fully green. The four `Hero2ParametricSweepTests` now
assert `[Vgg, Pin, …]` nesting; `NpyRoundTripTests` pass single-point; goldens regenerate byte-stable.

## On completion
Note in `src/Engine/CLAUDE.md`: all swept HB results (single- and two-tone) come from `ParametricSweepEngine`
(swept axis prepended, named after the sweep variable); HB-internal sweeping is fully retired. The exported
linear-network payload / back-solver is single-point per HB run; a sweep-aware exported payload is a known
follow-up. Tests and golden generators were migrated to the parametric path; golden CSV numbers unchanged.

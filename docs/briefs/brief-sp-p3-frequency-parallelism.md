# Sonnet Brief — SP-P3: the S-parameter sweep runs its frequencies in parallel

**Design:** `docs/design/linear-engine.md` §6 (sparse solve), §11 (performance), `docs/design/ui-architecture.md`
(the run service, `RunControl` progress and cancellation). **Code:** `src/Engine/SParameterEngine.cs`
(`Run`, `RunWavePath`, `RunLegacyPath`, `StampAll`, `CollectPortsAndBranchLabels`,
`ResolveSParamControlBranches`), `src/Engine/RunControl.cs` (`Tick`, `Child`, `Progress`),
`src/Core/Elaboration/Elaborator.cs` / `ElaboratedNetlist.cs` (`AddWarningOnce`, `Dispose`),
`src/Core/Devices/*.cs` — the models whose `Stamp` writes state: `ChainModel`, `InductorModel`,
`PortModel`, `P1ToneModel`, `SnpModel`, `ToneSourceModel`, `IProbeModel`, `VdcModel`, `ZPortModel`
(`LastBranchIndex` / `PortBranchIndices`), `SddModel` (`ControlBranchIndices`, `ControlBias`),
`src/Core/Devices/External/ExternalDeviceModel.cs` (a worker PROCESS — one per kit, not per thread).

**One sentence:** every frequency point is independent, the loop in `RunWavePath` is a plain serial
`for`, and the machine has cores to spare — run contiguous frequency chunks on separately elaborated
copies of the netlist and assemble the `S` cubes by index, keeping the shared-netlist models exactly
as stateful as they are today by never sharing them.

**Why (S-parameter engine performance review, 2026-08-30).** Release, M4 (4 performance + 6
efficiency cores), running slices of one sweep on independently elaborated netlists:

| Circuit | serial | 8 threads | ratio |
|---|---|---|---|
| Hero 1, 20,001 points | 118.6 ms | 42.3 ms | **2.8×** |
| 200-node ladder, 2,001 points | 200.5 ms | 64.0 ms | **3.1×** |
| 2000-node ladder, 401 points | 398.8 ms | 131.4 ms | **3.0×** |

4 threads already gave 2.5–2.8×; 10 gave no more than 8. The limiter is allocation: with server GC
or a 64 MB gen-0 budget the 200-node case reached 3.8×. **Do SP-P2 first** — it removes ~30 % of
the per-point garbage and this brief's numbers improve with it. Elaboration is cheap enough to
repeat per thread: 57 µs for Hero 1, 1.7 ms for the 200-node ladder, measured.

**Structural facts.**

1. **`Stamp` is not re-entrant across threads on one netlist.** Nine models write
   `LastBranchIndex`/`PortBranchIndices` during `Stamp`; `SnpModel` lazily loads `_snp`;
   `MicrostripLineModel`/`MicrostripKlopfModel`/`MatchModel`/`MicrostripBendModel` accumulate
   warnings for `DrainModelWarnings`; `SddModel` carries `ControlBranchIndices` resolved once per run
   and `ControlBias`. The values written are the same on every thread (same topology), which makes
   the race benign in practice and still a race. **Do not share the netlist.** One
   `ElaboratedNetlist` per worker, from `new Elaborator(lib).Elaborate(tb)`, is the whole
   thread-safety story — and it is what the measurement above used.
2. **`SParameterEngine.Run` takes an `ElaboratedNetlist`, not a `(Library, TestBench)`.** The callers
   that can re-elaborate are `SchematicRunService.RunTypedAnalysis`, `Cli sparam`,
   `ParametricSweepEngine.RunInner`, `TerminationProbe`, `MatchDesignerViewModel.RunResponse` — all
   have `lib`/`tb` in hand or one call away. Add an overload
   `Run(Library lib, TestBench tb, string? baseDirectory, double[] freqs, settings, control, degreeOfParallelism)`
   that elaborates per worker; keep the existing `Run(netlist, …)` serial and byte-identical (a
   caller holding one netlist gets what it always got). Do not try to clone an `ElaboratedNetlist`.
3. **A netlist with an external-device model must stay serial.** `ExternalDeviceModel` talks to one
   worker process per kit; instances are slots in that process and a sweep already leaks one per
   point unless disposed (`ElaboratedNetlist.Dispose` doc). Elaborating T copies would create T× the
   instances and serialize on the channel anyway. Detect (`netlist.Components.Any(c => c.Model is
   ExternalDeviceModel)`) on a first elaboration and fall back to the serial path. Same for
   `SddModel` with `ControlRefs` — not unsafe, but `ResolveSParamControlBranches` runs per netlist and
   the test surface is small; keep it serial in this brief, note it in §6.
4. **Nonlinear devices: the DC operating point is solved ONCE per netlist** before the loop
   (`Run`, "hasNonlinear"). T copies would solve it T times — 30–50 µs each on the heroes, fine — but
   each copy must reach the same `dcNodeVoltages` or the chunks disagree. `NonlinearDcEngine` is
   deterministic for a given netlist, so they do; add the test in §4 that proves it rather than
   assuming.
5. **Chunks are contiguous ranges, not strided.** `sMatrices[fi]` is written by index, so a chunk
   `[lo, hi)` writes its own slice and nothing else; results assemble with no merge step. Contiguous
   ranges also keep any per-model frequency-local caching (SP-P1's interpolator is stateless per
   evaluation, but a model that caches "last ω" would thrash on striding). Chunk count = worker
   count; T = `min(Environment.ProcessorCount, freqCount / 64, settings cap)` — a sweep under ~256
   points is not worth the elaboration and thread start (Hero 1's per-point cost is ~5 µs; 64 points
   is ~0.3 ms, comparable to one elaboration).
6. **Warnings and progress must not change meaning.** `AddWarningOnce(key, …)` is per netlist:
   collect each worker's `Warnings`/`Notes` and merge them into the CALLER's netlist (or the first
   copy's) by key, first occurrence wins, in chunk order, so the report is deterministic and
   identical to the serial one for every fixture in the suite. `RunControl.Tick()` is thread-safe
   (`Interlocked`) and already contracted as once per frequency — call it from each worker; use
   `control.Child()` if a worker needs its own stage label, and check cancellation per point as now.
   A cancellation must stop every chunk and surface as the same exception the serial path throws.
7. **Regularization retry stays per point** — a chunk that hits `SingularMatrixException` retries
   that point exactly as the serial loop does; the warning key dedupes across workers by fact 6.
8. **Determinism.** The result of the parallel path must be bit-identical to the serial path for
   the same inputs (each point's arithmetic is unchanged; only who computes it moves). That is the
   test, not a tolerance.

---

## 1. M1 — the parallel overload

In `SParameterEngine`: `Run(Library, TestBench, baseDirectory, freqs, settings, control, maxDegree)`
→ elaborate once; decide serial (fact 3, fact 5's floor, `maxDegree == 1`) → call the existing
`Run(netlist, …)`; else elaborate T−1 more copies, `Parallel.For` over chunks with
`MaxDegreeOfParallelism = T`, each worker running the existing per-point body on its own
`MnaSystem` and its own netlist (refactor `RunWavePath`/`RunLegacyPath` to take a `[lo, hi)` range
and the shared `sMatrices` array; the bodies do not otherwise change), then merge warnings (fact 6)
and build the `DataSet` exactly as the tail of `Run` does today. Dispose the extra netlists.

## 2. M2 — callers

`SchematicRunService.RunTypedAnalysis` (the GUI's S-parameter run), `Cli sparam`, and
`ParametricSweepEngine.RunInner`'s S-parameter case call the new overload. `TerminationProbe` and
`MatchDesignerViewModel.RunResponse` are small-circuit, many-call paths — leave them on the serial
overload; measure one of them if you doubt it and record the number. A settings knob for the
degree (`AnalysisSettings.MaxParallelism`, default 0 = automatic) so a user on a laptop can pin it
and a test can force `1` or `2`.

## 3. Tests

- Bit-identity: for every S-parameter fixture the suite already runs (Hero 1, Hero 1B, the wave-port
  and legacy-port cases, the nonlinear-linearized ones, a regularization-retry case), run serial and
  with `maxDegree = 3` on a 301-point grid (so chunks are uneven) and `Assert.Equal` every `S` entry
  and the `Z0` cube. Same warnings list, same order.
- Cancellation: a `RunControl` cancelled after N ticks stops the run with the same exception type
  the serial path throws, and no chunk keeps running (assert on `Completed` not advancing after).
- Fallback: a netlist with an `ExternalDeviceModel` (use the `tools/DeviceWorkerExample` fixture the
  external tests already use) takes the serial path — assert through a counter or the notes, not
  timing.
- Progress: `Completed == freqCount` at the end, and it never exceeds it.
- Nothing timing-based (no new `Category=Benchmark` tests); measure with a scratch harness.

## 4. Gates

`dotnet test tests/Engine.Tests` and `dotnet test tests/Ui.Tests` green (run once, TRX for
failures — the Ui suite because `SchematicRunService` changes). Scratch measurement on the three
fixtures above with the default degree; report serial vs parallel, and GC count per run
(`GC.CollectionCount(0)`) before/after, because that is the number that explains whatever scaling
you get.

## 5. On completion

Findings — the ratio achieved per fixture, the chosen floor and degree formula and why, which
callers were left serial and the measured reason, and whether any model turned out to hold shared
static state that fact 1 missed — to **`src/Engine/RESOLVED.md` §SP-P3**. **Never to any
`CLAUDE.md`.** Do not commit; the owner commits.

## 6. Out of scope, deliberately

Making `Stamp` re-entrant (it is a cross-cutting change to every model for no gain over elaborating
per worker). Parallelism inside one point (the LU is far too small). SDD control-current netlists and
external-device netlists (serial here; a follow-up once this path has been used). HB, loadpull and
the parametric-sweep OUTER loop, which have their own briefs and their own state.

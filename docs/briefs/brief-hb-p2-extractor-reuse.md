# Sonnet Brief — HB-P2: the linear extractor outlives one solve, and the post-solve re-evaluation goes

**Design:** `docs/design/harmonic-balance.md` §3 (the interface, from the linear engine), §9
(recovering the full solution — "reusing the per-harmonic factorization already computed"),
`docs/design/harmonicarf.md` §2.1 (the levers, in order: the pre-terminated network, never
re-elaborate, warm-start everything). **Code:** `src/Engine/HarmonicBalance/HbLinearExtractor.cs`,
`HbEngine.cs` (`Run`, `RunSinglePoint`), `HbNewton.cs` (`ComputeDevicePortCurrents`,
`RunDevicePass`), `src/Engine/MnaSystem.cs` (`Factorize`, the AMD ordering),
`src/Engine/Loadpull/LoadpullEngine.cs` and `LoadpullPursuitEngine.cs` (the callers that solve
hundreds of times against one topology).

**One sentence:** a `RunSinglePoint` currently rebuilds, re-orders and refactors every harmonic's MNA
system — ~45 % of a warm single-tone solve and 75 % of its garbage — although only the source vector
changes between Pin steps; keep the extractor across solves and re-extract the linear network only
when a termination actually changes, and stop re-running the whole device evaluation after
convergence just to re-house currents the last Newton pass already computed.

**Why (HB performance review, 2026-08-29).** Measured on the Hero fixtures (Release, M4,
single-threaded; ±30 % run-to-run):

| Hero 2 single-tone K=7, N=2, warm | µs | KB allocated |
|---|---|---|
| `RunSinglePoint` total | 285–440 | **441** |
| `new HbLinearExtractor` + `ExtractDC` + `Extract`×K | **90–214** | **328** |
| same K `Extract` calls on an extractor whose LU cache is already warm (sources only) | 24–45 | — |
| `HbNewton.Solve`, one iteration | 117–158 | 35 |
| `ComputeDevicePortCurrents` (after convergence, `Run` path only) | **112–138** | — |
| `NonlinearDcEngine.Run` (cold seed only) | 30–49 | 35 |

Hero 4 (two FETs): extractor 92 µs of a 235 µs warm solve; `ComputeDevicePortCurrents` 114 µs of a
393 µs `Run`. The loadpull engine calls `RunSinglePoint` for **every Pin step** of every Γ point —
Hero 3 is 640 solves — and each call constructs a fresh extractor although the Γ point's tuner
override, the only thing that changes `Y_NN`, was set once for the whole ladder.

**Structural facts.**

1. **`Y_NN(ω)` depends on topology and element values; `I_src(ω)` depends on the drive.** The
   extractor already separates them — `Extract(ω)` step 1 (Z-column extraction, N solves, cached in
   `_luCache` by ω) and step 2 (one solve for `V_oc`, every call). The cache is correct; it is just
   thrown away with the extractor after every `RunSinglePoint` (`HbEngine.cs:1160`) and every `Run`
   (`HbEngine.cs:258`).
2. **Even with a warm cache, step 2 restamps the entire linear partition** (`BuildMna(omega,
   zeroDrive:false)`) to obtain the RHS. A source-only RHS assembly — stamp only the components that
   contribute to `b` (`AddCurrentInjection`/`AddSourceValue` callers), with the branch numbering fixed
   by the first stamp — is what "24–45 µs" should become. `BuildSourceRhs(ω)` already exists for the
   back-solver snapshot and does the same full restamp; it is the natural place to make cheap.
3. **`MnaSystem.Factorize` recomputes the AMD ordering for every new `MnaSystem` instance**
   (`MnaSystem.cs:191`, `_amdPerm` is per instance; `BuildMna` news one per call). The sparsity
   pattern is the same at every ω and every sweep point of one topology. The ordering belongs to the
   extractor (or to a pattern key), computed once, handed to `Factorize`.
4. **What invalidates the cache is a value change in the linear partition.** Today those happen
   through exactly three doors: `TunerModel.SetHarmonicOverride` / `SetSourceDrive` (loadpull,
   pursuit, harmonicaRF — mutate in place), `P1ToneModel`/`PnToneModel` drive changes
   (`UpdateSweepPoint`), and re-elaboration (`ParametricSweepEngine`, which builds a **new netlist**,
   so a per-netlist extractor is naturally fresh there). A drive change alters `I_src` only. A tuner
   override alters `Y_NN` at one harmonic only — the other harmonics' LUs stay valid.
5. **`ComputeDevicePortCurrents` is the last Newton pass again.** It IFFTs `V`, evaluates every
   device at every sample, FFTs per port — exactly `RunDevicePass` minus the Jacobian buckets — to
   produce `I:instance:terminal` cubes whose values are, by construction, `INl` re-housed per port
   (`HbNewton.cs`, the C2 comment says so). The final iteration's `RunDevicePass` already holds
   `res.I[p]` per port per sample; keeping a `[device][port][t]` buffer from that pass makes the
   post-solve step an FFT per port (~1 µs each) instead of a full evaluation. The two-tone
   (`ComputeDevicePortCurrents2D`) and n-tone (`ComputeDevicePortCurrentsNd`) twins have the same
   shape and the same fix.
6. **The control-current path is the exception that proves the rule.** With `cc != null`, the
   post-solve currents are evaluated at the *converged* `_c_ref` (`cRefTimePost`), which the last
   Newton pass — one iterate behind on its seed — did not use. Keep the re-evaluation for
   `cc != null` only; the fast path is `cc == null`, which is every built-in hero and every loadpull.

**Sequencing.** M1 extractor lifetime + invalidation (the big one; loadpull, pursuit and harmonicaRF
all benefit without changing). M2 source-only RHS and AMD reuse (makes M1's warm path what it should
be). M3 the post-solve currents.

---

## 1. M1 — the extractor lives on the engine

`HbEngine` owns one `HbLinearExtractor` per **(settings identity, interface set)**, created lazily and
reused by `Run` and `RunSinglePoint`:

```
private HbLinearExtractor? _extractor;
private AnalysisSettings?  _extractorSettings;   // the settings the cached one was built with
public  void InvalidateLinear()                  // drop every cached LU; next Extract refactors
public  void InvalidateLinear(double omega)      // drop one harmonic's entry (a tuner override)
```

Rules:

- `Run(p, warm)` and `RunSinglePoint(p, warm, settingsOverride)` take the engine's extractor when the
  effective settings are reference-equal to `_extractorSettings`, else build and cache a new one.
  (`RunSinglePoint`'s per-call `new AnalysisSettings {…}` copy for `MaxIter` defeats reference
  equality — hoist that copy so it is made once per distinct `p.MaxIter`, or compare the fields the
  extractor reads: `Gmin`, both regularization modes, `InductanceRegR`, `Gmax`.)
- **`SetHarmonicOverride` callers invalidate.** `LoadpullEngine` sets the override once per Γ point
  and then runs the ladder; add `_hbEngine.InvalidateLinear(k·ω₀)` beside every `SetHarmonicOverride`
  / `SetSourceDrive` that changes an impedance (a drive change does not need it — `I_src` is
  re-extracted every call anyway). `LoadpullPursuitEngine` and harmonicaRF's `HarmonicaContext` get
  the same one-liners. Grep for every caller of `SetHarmonicOverride`; the list is short.
- **Safety net, not the primary mechanism:** `Extract(ω)` on a cache hit verifies that the stamped
  matrix is the one it factored. Cheapest correct check: the extractor keeps, per ω, a
  `long` version stamp incremented by `InvalidateLinear`; a caller that forgets to invalidate is a
  bug this brief must make *loud*, not silently absorb — so in DEBUG builds, additionally re-stamp
  and compare the CSC values to the cached `G` on every hit and throw on mismatch. Release skips it.
- The `HbLinearBackSolver` handed out in results keeps a reference to the extractor; it must keep
  working after a later invalidation for the harmonics it already solved. Its `_cache` is keyed by
  (k, sweepIdx) and holds solution *vectors*, not LUs, so this is already true — assert it with a
  test rather than assuming.

Two-tone and n-tone (`RunTwoTone`, `RunMultiTone`) take the same engine extractor. They have no
`RunSinglePoint`, so the win there is only across the warm-started sweeps HB-P3 adds; wire it anyway
so there is one code path.

## 2. M2 — cheap `I_src`, ordering computed once

- **Source-only RHS.** Give `HbLinearExtractor` a `BuildRhsOnly(ω)` that stamps only components whose
  model contributes to the RHS at ω (sources: `VdcModel`, `ToneSourceModel`, `P1ToneModel`,
  `PnToneModel`, `TunerModel`'s bias/drive branches — enumerate from the model types that call
  `AddSourceValue`/`AddCurrentInjection` today, and assert that set in a test by stamping the full
  MNA with a counting context). Branch indices must match the full stamp's: stamp through a context
  that allocates branches for every branch-allocating component in the same order but writes only
  RHS entries. `Extract` step 2 and `BuildSourceRhs` both use it.
- **AMD once.** `HbLinearExtractor` computes the ordering on the first factorisation and passes it to
  every subsequent `Factorize` (add an optional `int[]? ordering` parameter to `MnaSystem.Factorize`;
  `_amdPerm` stays as the fallback). The pattern is identical at every ω and after every
  `InvalidateLinear` (a value change, never a topology change); document that a topology change
  needs a new extractor, which it already gets (new netlist).

## 3. M3 — port currents from the last pass

`RunDevicePass` gains an optional output `double[][,]? portITime` (`[device][port, t]`) filled from
`res.I[p]` in the sample loop (it is already in hand; one store per port per sample). `HbNewton.Solve`
returns the buffer from its final evaluation in `SolveResult`. `ComputeDevicePortCurrents(…)` takes it
and, when `cc == null`, only FFTs per port; when `cc != null` it keeps today's re-evaluation. Same for
the 2D and Nd twins (they have no control-current path, so they always take the fast route).

## 4. Tests

`tests/Engine.Tests/HarmonicBalance/HbExtractorReuseTests.cs` (add):

1. **Same answer, one factorisation.** `hero2_convergence.cnl`: 20 warm `RunSinglePoint` calls at
   rising `Pavl_dbm` (via `UpdateSweepPoint`) produce interface `V` bit-identical to 20 calls on 20
   fresh engines, and the extractor factorised each harmonic **exactly once** (a public static
   diagnostic counter on `MnaSystem.Factorize` — `CircuitRF.Engine` has no `InternalsVisibleTo` for
   its tests, so `internal` is not an option; document the counter as test-facing).
2. **A tuner override refactors one harmonic.** Loadpull-style: `SetHarmonicOverride(1, Z)` +
   `InvalidateLinear(ω₀)` → the next solve refactors exactly one ω and its `V` equals a fresh
   engine's to 1e-12.
3. **A forgotten invalidation is loud in DEBUG.** Override without invalidating → the DEBUG check
   throws with the harmonic named. (Skip in Release with a reason string.)
4. **Loadpull is unchanged.** `Hero3LoadpullTests`' golden and `Hero3BPursuitTests` pass as they are,
   and the factorisation counter over a full Hero-3 run equals `(K+1) × Γ-points`, not
   `(K+1) × solves`.
5. **`BuildRhsOnly` equals the full stamp's RHS** at every harmonic of Hero 2, 4, 5 to 1e-15, and the
   counting context shows no admittance was stamped.
6. **AMD is computed once per extractor** (counter), and the factorisation with a supplied ordering
   solves to the same `x` as without (1e-12).
7. **Back-solver survives invalidation.** Take a `BackSolver` from a result, invalidate, run another
   solve, then read `GetNodeVoltage` from the old back-solver: unchanged.
8. **Port currents from the last pass equal the re-evaluated ones** to 1e-13 on Hero 2 and Hero 4
   (single-tone), Hero 5 (two-tone), `hero5_3tone` (n-tone); and the control-current fixture in
   `SddControlCurrentHbJacobianTests` still takes the re-evaluation path (assert via the counter on
   device `Evaluate` calls after convergence).
9. **Allocation is asserted as a byte COUNT with a ceiling, not a time:** one warm `RunSinglePoint`
   on Hero 2 allocates under **150 KB** (was 441 KB; the extractor's 328 KB is what leaves). Use
   `GC.GetAllocatedBytesForCurrentThread` around the call after a warm-up call.

## 5. Gates

```
dotnet build
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Ui.Tests --no-build       # harmonicaRF drives RunSinglePoint through the UI tests
dotnet test tests/Firewall.Tests --no-build
```

Run each ONCE; read the TRX. Grep the diff for vendor or product names before finishing.

## 6. On completion

Findings — the measured warm-solve time and allocation before/after on Hero 2 / Hero 4 / a full
Hero-3 loadpull, the list of `SetHarmonicOverride` call sites that needed an invalidation, whether
the DEBUG check ever fired during the suite — to **`src/Engine/RESOLVED.md` §HB-P2** (loadpull-side
notes to `src/Engine/Loadpull/RESOLVED.md` if one exists there, else the same section). Update
`harmonicarf.md` §2's table if the numbers moved. **Never to any `CLAUDE.md`.** Do not commit; the
owner commits.

## 7. Out of scope, deliberately

- harmonicarf.md §6.2's pre-terminated (N_nl + N_term)-port network, which closes tuner ports
  algebraically and needs no refactor at all on a marker move — a bigger change, and harmonicaRF's
  own brief; this brief's invalidate-one-harmonic is the 80 % version.
- Caching across `ParametricSweepEngine` points (a new netlist per point; topology-hash caching is
  its own decision).
- The nonlinear-DC seed's dense `double[size,size]` Newton (`NonlinearDcEngine.cs:292`) — 30–49 µs
  here, but O(n³) in the full node count; a scaling note, not a hero-size cost.
- Anything in the Newton loop itself (HB-P1, HB-P3, HB-P4).

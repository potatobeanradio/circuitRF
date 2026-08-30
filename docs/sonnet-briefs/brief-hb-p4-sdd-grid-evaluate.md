# Sonnet Brief — HB-P4: the SDD evaluates the whole time grid in one call

**Design:** `docs/design/harmonic-balance.md` §4 (the `(i, q, dg, dc)` contract — one sample at a
time is how it is *defined*, not how it must be *executed*), `docs/design/expressions.md` §12 (the
three-tier derivative scheme; the SDD's forward-mode AD), `docs/design/harmonicarf.md` §2.2 (the
batched external-device path — H0 — which established that the HB inner loop may ask a model for the
whole grid at once). **Code:** `src/Core/Expressions/CompiledSddExpr.cs` (`EvalDual`, the register
program), `SddRegisterCompiler.cs`, `Dual.cs`, `src/Core/Devices/SddModel.cs` (`Evaluate`),
`src/Core/ComponentModel.cs` (`PrefersBatchEvaluate`, `EvaluateBatch`, `NonlinearResult`),
`src/Engine/HarmonicBalance/HbNewton.cs` (`RunDevicePass`, `GatherPortVoltages`), `HbNewton2D.cs`,
`HbNewtonNd.cs`.

**One sentence:** for every built-in hero the device evaluation is ~70 % of a Newton iteration and
85 % of a two-tone one, and it is spent running an identical instruction sequence once per time
sample through a register machine whose operands are 136-byte `Dual` structs, allocating six arrays
per sample; execute the compiled program once per *grid* with structure-of-arrays registers
(value[S] + grad[n][S]), vectorised across samples and allocation-free, and let the HB loops call it
through the batched door H0 already opened.

**Why (HB performance review, 2026-08-29).** Measured, Release build, M4, single-threaded:

| | per iteration | of which device `Evaluate` |
|---|---|---|
| Hero 2 single-tone, 32 samples, 1 FET | 117–158 µs | **55–116 µs** (~3.5 µs/sample) |
| Hero 4, two FETs, 16 samples each | 117 µs | ~2 × 55 µs |
| Hero 5 two-tone, 32×32 = 1,024 samples | 4.2 ms | **3.5 ms** |
| 6-tone order 3, 756 APFT samples | — | 2.6 ms (dwarfed by the Jacobian there — HB-P1) |

The FFTs total under 6 µs per iteration; `BuildJ` + solve under 35 µs. After HB-P2 removes the
extractor rebuild, `Evaluate` is what remains of a single-tone solve. A warm loadpull step is
~100–130 samples' worth of evaluation; harmonicaRF's frame budget (harmonicarf.md §2) is set by it.
`src/Core/RESOLVED.md`'s register-machine work already took the big drain equation from ~9–12 µs to
~3 µs per evaluation; this brief is the next factor, and it comes from the *shape* of the loop, not
from the arithmetic inside one evaluation.

**Structural facts.**

1. **The program is the same for every sample.** `CompiledSddExpr` (no-conditional case) runs
   `RInstr[] _code` over a `Span<Dual>` slot file: seed the port voltages, copy the parameter duals,
   execute, read the root. Nothing in the instruction stream depends on the sample. That is exactly
   the SIMD-across-samples shape: each register becomes `value[S]` and `grad[k][S]` for `k < n`, each
   instruction a loop over `S` that the JIT vectorises (or that is written with `Vector<double>`
   explicitly). Transcendentals (`tanh`, `exp`, `log`, `sqrt`, `pow`) stay a scalar loop over `S`
   — they are the residue, and at 32 samples they are ~30 % of the work; measure before promising a
   factor.
2. **`Dual` is a 136-byte value type** (`double Value`, a 16-lane `InlineArray` gradient, `int N`)
   whose operators loop only to `N` (2–3 for a FET) — correct, but every register read/write moves
   136 bytes to use 24. The SoA layout removes that traffic entirely: a register of `n=2` gradient
   lanes over `S=32` samples is 3 × 32 doubles, contiguous.
3. **Allocation per sample today:** `SddModel.Evaluate` news `i`, `q`, `dg`, `dc` (+ control arrays),
   `EvalDual` news `grad` per equation, plus the `NonlinearResult`'s `Terms` list when `w ≥ 2` terms
   exist. ~1 KB per sample, ~33 KB per single-tone iteration, 723 KB per two-tone iteration. Not the
   dominant cost (allocation is fast) but it is why `EvaluateNonlinear` is 33 KB of garbage and why
   the two-tone point is 4.9 MB; the grid call returns into caller-owned buffers.
4. **The batched door exists and has a contract.** `ComponentModel.PrefersBatchEvaluate` +
   `EvaluateBatch(IReadOnlyList<double[]> points)` (H0, harmonicarf.md §2.2) is what
   `RunDevicePass` already uses for external device workers, gated bit-identical against the scalar
   path. Built-in models return `false` and take the scalar loop. The SDD opting in is the *engine*
   side of this brief in one line — but `EvaluateBatch` returns `IReadOnlyList<NonlinearResult>`
   (per-sample arrays again), so a second, allocation-free signature is needed for the SoA result:
   `EvaluateGrid(ReadOnlySpan<double> portV /* [port][t] */, GridResult into)` with
   `GridResult` holding `I[port][t]`, `Q[port][t]`, `Dg[port,port][t]`, `Dc[port,port][t]` and the
   per-`w` bucket arrays. `EvaluateBatch` can be a thin adapter over it for callers that want the
   list.
5. **The conditional case falls back.** `CompiledSddExpr` with an `if`/ternary uses the tree walker
   (`_root`, `SddCompiler.Eval<Dual>`) because the active branch is per-sample. Keep that path as the
   scalar fallback (`PrefersGridEvaluate == false` for those equations), and make the register path
   the grid one. A later brief can lift conditionals into masked selects; not this one.
6. **`AdWarnings.CurrentModel` and the domain-clamp warnings** (design §11 — `log`/`sqrt` of a
   non-positive argument clamps and warns, naming the model) are set per `EvalDual` call. The grid
   evaluator must produce the same warning, once per grid call, naming the model — not once per
   sample and not zero times. `AdWarnings` is thread-affine today; a parallel grid (fact 8) must not
   lose a warning.
7. **The oracle is the scalar path, bit for bit.** The register program is unchanged; only the
   operand layout and loop order change, and IEEE addition/multiplication are deterministic per
   element. Every value, gradient and charge must equal the scalar path's **bit-for-bit** — not to a
   tolerance — on every hero SDD across the whole grid, with `Vector<double>` FMA use ruled out
   (no `FusedMultiplyAdd`; the scalar path does not fuse). If a transcendental's vector form differs
   from `Math.X` in the last bit, use `Math.X` in a loop — bit identity is the gate, speed is second.
8. **Parallel across samples is a second lever for large grids only.** 1,024 (two-tone) or 756
   (n-tone) samples split across cores is free once the evaluator is allocation-free and warning-safe;
   32 samples is not worth a fork/join. Gate on `S ≥ 256`; measure that the gate is right.
9. **`FetModelBase`/built-in closed-form models** already return their derivatives without AD; give
   them the same `EvaluateGrid` (a loop, no vectors) so the engine has one call shape, and so their
   per-sample arrays go too.

**Sequencing.** M1 the SoA register evaluator in `CompiledSddExpr` (Core; bit-identity gate). M2
`SddModel.EvaluateGrid` + `GridResult`, the engine's three `RunDevicePass` twins calling it (Engine).
M3 parallel samples for `S ≥ 256`. M4 built-in closed-form models on the same door.

---

## 1. M1 — `CompiledSddExpr.EvalDualGrid`

```
public void EvalDualGrid(
    ReadOnlySpan<double> portV,     // [nV][S] row-major, S samples
    int S,
    Span<double> value,             // [S]
    Span<double> grad,              // [n][S]
    GridScratch scratch,            // caller-owned register file, sized once per (expr, S)
    string modelName)
```

- `GridScratch` holds `double[regCount][(1+n)·S]` (value lanes then gradient lanes), grown on demand,
  never shrunk, owned by the `SddModel` instance (one per device — devices are evaluated one at a
  time per thread; M3 gives each worker its own).
- Each `RInstr` opcode gets a grid kernel: add/sub/mul/div/neg/const/copy over `(1+n)·S` lanes with
  the dual rule (`d(ab) = a·db + b·da` lane-wise), `Vector<double>` on the sample loop; unary math via
  `_mathFns[i]` in a scalar loop over `S` with the derivative rule applied lane-wise afterwards.
  Mirror the scalar `Dual` operators' clamps exactly (`ExpCap`, `LogFloor`, the domain warnings).
- The conditional path (`_root != null`) throws `NotSupportedException` from `EvalDualGrid`;
  `CompiledSddExpr.SupportsGrid` tells the model which door to use.
- Control currents (`_nC > 0`): the seeds are per-sample too (`cRefTime[ci, t]`); pass them as a
  second `[nC][S]` span. The control-current HB path (`cc != null`) keeps the scalar loop in M2 if
  this proves awkward — it is rare and its own brief — but the evaluator should not preclude it.

## 2. M2 — `SddModel.EvaluateGrid` and the engine loops

- `ComponentModel` gains `virtual bool PrefersGridEvaluate => false` and
  `virtual void EvaluateGrid(ReadOnlySpan<double> portV, int S, GridResult into)` whose base
  implementation loops the scalar `Evaluate` (so every model supports the call). `SddModel`
  overrides both: `true` when every compiled equation `SupportsGrid`, running `EvalDualGrid` per
  equation directly into `into`'s arrays. `w ≥ 2` terms: `GridResult.Terms[w]` with `Value[port][t]`
  and `Jac[port,port][t]`, filled the same way.
- `HbNewton.RunDevicePass`, `HbNewton2D.EvaluateNonlinear2D`, `HbNewtonNd.EvaluateNonlinearNd`:
  when `ec.PrefersGridEvaluate && cRefTime is null`, gather port voltages as `[port][t]` (transpose
  of today's `GatherPortVoltages`), call `EvaluateGrid` once, and run the existing `PortAdd`/
  `PortAdd4` accumulation over the SoA result. The scalar and `EvaluateBatch` paths stay as they are
  for models that do not opt in. `GridResult` buffers live on the engine side, reused across
  iterations (sized by `S` and the device's port count).
- `HbNewton.ComputeDevicePortCurrents` and its twins use the same call (they are the same loop; after
  HB-P2 M3 they read the last pass's buffer instead, so this is transitional — keep it simple).

## 3. M3 — parallel samples

In `EvaluateGrid` (SDD override), when `S ≥ GridParallelThreshold` (256, a named constant), split
the sample range across `Environment.ProcessorCount` chunks with `Parallel.For`, each chunk on its
own `GridScratch` (a small pool on the model). Warnings: collect per chunk and emit once after the
join, through the same `AdWarnings` sink. The engine side is unchanged.

## 4. M4 — built-in models

`FetModelBase` and any other built-in `ComponentModel` with a closed-form `Evaluate`: override
`EvaluateGrid` with a plain loop writing into `GridResult` (no per-sample arrays). `PrefersGridEvaluate
=> true`.

## 5. Tests

`tests/Core.Tests/Expressions/CompiledSddGridTests.cs` (add):

1. **Bit identity.** For every SDD in the hero fixtures (Hero 2's GaN HEMT, Hero 4's, Hero 5's, the
   control-current fixture's, the `w ≥ 2` fixture's) and for grids of S = 1, 7, 32, 33, 1024 (not
   only powers of two — the vector tail matters): `EvalDualGrid` values and gradients equal
   `EvalDual` per sample **`BitConverter.DoubleToInt64Bits`-equal**. Voltages spanning the saturating
   region (Vgs from −6 to +1, Vds from 0 to 100) so the clamps fire.
2. **Warnings once per grid.** A `log` of a negative argument at three samples of a 32-grid: one
   warning, naming the model, same text as the scalar path's.
3. **Conditional equations decline the grid** (`SupportsGrid == false`, `EvalDualGrid` throws
   `NotSupportedException`), and the model's `PrefersGridEvaluate` is false for that device.
4. **No allocation per call.** Second call of `EvalDualGrid` on the same `GridScratch` allocates
   zero bytes (`GC.GetAllocatedBytesForCurrentThread` delta == 0).
5. **Control-current seeds per sample** produce the scalar path's `grad[n + k]` bit-for-bit.

`tests/Engine.Tests/HarmonicBalance/HbGridEvaluateTests.cs` (add):

6. **HB answers bit-identical.** Hero 2, Hero 4, Hero 5 (two-tone), `hero5_3tone`: interface `V`
   and `INl` after `Run` equal the scalar-path result (force the scalar path with a test-visible
   switch on the model) **bit-for-bit**, and the existing goldens pass unchanged.
7. **The grid path is the one taken:** counter on `EvaluateGrid` == iterations × devices, counter on
   scalar `Evaluate` == 0, on the fixtures whose SDDs have no conditionals; the control-current fixture
   takes the scalar path (counter shows it).
8. **Parallel equals serial** bit-for-bit at S = 1,024 (two-tone) with the threshold forced to 1 and
   to `int.MaxValue`; warnings are emitted exactly once either way.
9. **Allocation asserted as a byte COUNT:** one `EvaluateNonlinear` on Hero 2 allocates under
   **4 KB** (was 33 KB); one `EvaluateNonlinear2D` on Hero 5 under 64 KB (was 723 KB).

Speed is reported in the completion note (the scratch harness), not asserted in a test — per the
no-new-timing-benchmarks rule.

## 6. Gates

```
dotnet build
dotnet test tests/Core.Tests --no-build
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Ui.Tests --no-build        # harmonicaRF's solve pool and frame scheduler
dotnet test tests/Firewall.Tests --no-build
```

Run each ONCE; read the TRX. Grep the diff for vendor or product names before finishing. The SDD
scalar path (`EvalDual`, `EvalDouble`, `Dual`) is the reference implementation and is **not modified**
by this brief — the grid evaluator is added beside it.

## 7. On completion

Findings — the measured per-sample cost before/after per hero SDD, the transcendental share of the
grid kernel, whether any vector transcendental had to be replaced by `Math.X` for bit identity, the
parallel threshold that measured right, per-iteration and per-point times on the four fixtures — to
**`src/Core/RESOLVED.md` §HB-P4** (Core side) and **`src/Engine/RESOLVED.md` §HB-P4** (engine side).
Update harmonicarf.md §2's table. **Never to any `CLAUDE.md`.** Do not commit; the owner commits.

## 8. Out of scope, deliberately

- Masked-select lifting of `if`/ternary equations into the grid path — a later brief once M1's
  numbers are in.
- The external-device worker protocol (`EvaluateBatch` over IPC) — already batched (H0); unchanged.
- Vector transcendental libraries or any native dependency — ask before adding (root `CLAUDE.md`).
- The nonlinear-DC engine's per-iteration `Evaluate` (one sample per iteration — nothing to batch).
- Anything in the Jacobian, the solve (HB-P1), the extractor (HB-P2), or convergence (HB-P3).

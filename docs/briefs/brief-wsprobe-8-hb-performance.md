# Sonnet Brief — WSP-8: making the WSProbe fast under harmonic balance

**Read `brief-wsprobe-5-harmonic-balance.md` first** — this brief is its fast path, and WSP-5's
straightforward implementation is the oracle every change here is measured against. The owner asked
for this brief (2026-09-08) because the probe has a reputation elsewhere for slowing simulations
down, and pointed the concern at harmonic balance specifically rather than at S-parameters. The
S-parameter case is indeed cheap here — two back-substitutions per probe per frequency on a
factorisation the sweep already pays for (WSP-1 R-wsp1-3) — and nothing in this brief touches it.

**Scope.** Restructure WSP-5's per-`(operating point, ssfreq)` work so that **no sparse factorisation
and no sparse solve happens per operating point**, cache the linear-partition data across the drive
sweep, reuse the sparsity pattern across frequencies, and parallelise over the tickle grid. Results
must equal WSP-5's straightforward path to 1e-12 relative; the gates are **counters** (owner rule: no
timing tests), with the measurement done in a scratch harness and the numbers recorded in
`src/Engine/RESOLVED.md`.

---

## 1. The cost model — where the time goes

For one HB operating point, `M` tickle frequencies, `N` probes, `N_int` interface (nonlinear-facing)
nodes, sideband order `K_ss`, and a linear partition of `n` MNA unknowns:

| Work item (WSP-5 straightforward) | Per operating point | Kind |
|---|---|---|
| `Y_NN(ω_ss + kω0)` for `k = −K_ss … K_ss` | `M · (2K_ss + 1)` sparse **factorisations**, each followed by `N_int` solves | sparse, size `n` |
| conversion matrix `J_ss` build + dense LU | `M` dense LUs of size `n_c = N_int(2K_ss + 1)` | dense |
| probe Norton vectors (`V_oc` per injection) | `M · 2N` sparse solves at `ω_ss` | sparse |
| dense solves for the `2N` injections | `M · 2N` dense back-substitutions | dense |
| back-solve for the probe readings | `M · 2N` sparse solves at `ω_ss` | sparse |

A drive sweep of `P` points multiplies every row by `P`. For a 16-transistor amplifier (`N_int = 32`,
`K = 7`, `K_ss = 7`), 991 tickle points, 41 drive points: **`41 × 991 × 15 = 609,465` sparse
factorisations** of the whole linear partition — against **991** for the S-parameter sweep of the
same circuit. That ratio, roughly `P·(2K_ss + 1)`, is the slowdown the owner has seen elsewhere, and
it comes entirely from work that **does not depend on the operating point**: the linear partition is
the same at every drive level. The dense work (`n_c = 480`: ~40 ms per LU, `2N = 64` back-substitutions
of `480²`) is small by comparison and genuinely per operating point.

So the design is: **do the sparse work once per tickle frequency for the whole sweep, and make the per
operating point work dense-only.**

---

## 2. Everything operating-point-independent, precomputed once per `ω_ss`

**R-wsp8-1. Transposed solves give every reading at once.** At `ω_ss`, with the linear partition
factored (`M(ω_ss)`), the probe readings are `2N` entries of the solution vector — `vP_j = x[nG_j − 1]`,
`iS_j = x[br_j]` — for a right-hand side that is the probe injection `b_p` plus the interface current
`B_int·(−I_nl,0)`. Write `R` for the `2N × n` selection of those rows. Then

```
r = R·M⁻¹·b_p + R·M⁻¹·B_int·(−I_nl,0)  =  W[:, p] + T·(−I_nl,0)
```

and **`R·M⁻¹` is `2N` rows of `M⁻¹`**, obtained by `2N` solves with `Mᵀ` (`SparseLU.SolveTranspose`
exists). From those rows:

- `W` (`2N × 2N`): `W[j, p] = y_j · b_p` — `b_p` has one nonzero (`+1` at `nG_p` for the shunt
  injection, `−1` on branch row `br_p` for the series one), so each entry is one lookup. `W` is the
  `wsp` of the linear partition with the devices removed (interface open).
- `T` (`2N × N_int`): `T[j, n] = y_j[node_n]` — one lookup each.

Likewise `N_int` transposed solves with `e_{node_n}` give the interface rows of `M⁻¹`, and from them

- `Z_NN(ω_ss)[n, m] = y_n[node_m]`, hence `Y_NN(ω_ss) = Z_NN⁻¹` (the extractor's own Z-column
  route, but from rows), and
- `V_oc[n, p] = y_n · b_p` — the open-circuit interface voltage of **every** probe injection, one
  lookup each — hence `I_src[:, p] = −Y_NN(ω_ss)·V_oc[:, p]`.

Per `ω_ss` that is **one factorisation and `N_int + 2N` transposed solves**, replacing `1 + 2N`
forward solves per operating point plus the `N_int` Z-column solves. Everything stored is small:
`Y_NN` (`N_int²`), `I_src` (`N_int × 2N`), `W` (`4N²`), `T` (`2N·N_int`).

**R-wsp8-2. The other sidebands need only `Y_NN`.** For `k ≠ 0`, `Y_NN(ω_ss + kω0)` is all the
conversion matrix uses; `N_int` transposed (or forward) solves per new frequency, stored as `N_int²`
complex numbers **keyed by frequency**.

**R-wsp8-3. Per operating point, per `ω_ss`, dense only:**

```
J_ss   ← from cached Y_NN(ω_k) (all k) and this operating point's two-sided spectra G⁽²⁾, C⁽²⁾, Dw⁽²⁾
LU(J_ss)                                            one dense factorisation
for each injection p (2N of them, as ONE multi-RHS solve):
    V      = J_ss⁻¹ · (−I_src[:, p] in the k=0 block)
    I_nl,0 = −(Y_NN(ω_ss)·V[:, 0] + I_src[:, p])
    r[:, p]= W[:, p] + T·(−I_nl,0)
wsp(·, ·) ← r                                       (WSP-1 R-wsp1-5's fill)
```

**Zero sparse work.** The counter gate (R-wsp8-9(a)) asserts exactly that.

---

## 3. Reuse across the drive sweep, and across tickle frequencies

**R-wsp8-4. Cache lifetime = the sweep.** The per-`ω_ss` data of §2 lives in the `HbLinearExtractor`
(whose lifetime already spans a sweep — HB-P2) keyed by frequency, and is invalidated by the same
mechanism the extractor already uses for its factorisations: the stamped matrix is compared **bit for
bit** against the one the cached data was computed from, on the next stamp at that frequency. A
loadpull tuner override or a swept linear element changes the matrix and the entry is recomputed;
nothing else can go stale, and no caller has to remember to invalidate. `ParametricSweepEngine` must
hand the same extractor to every operating point (it does for `hb` today; assert).

**R-wsp8-5. Sideband coincidence.** `Y_NN(ω)` is keyed by the actual frequency, so when tickle
frequencies are spaced by a divisor of `f0` the sidebands of one point are the sidebands of others:
a grid `SSStep = f0/m` has only `M + 2K_ss·m` distinct sideband frequencies instead of `M(2K_ss+1)`
(for `f0 = 2 GHz`, `M = 991`, step 10 MHz, `K_ss = 7`: 3,791 instead of 14,865 — 3.9×). **The grid
is never altered to achieve this**; the run summary reports `distinct sideband frequencies: 3,791 of
14,865 (grid aligned to f0/200)` so the designer can see what an aligned step buys, and the user
docs say so in one sentence. (The AUT-8 rule: a frequency the user wrote is a frequency the engine
uses.)

**R-wsp8-6. One `MnaSystem` across frequencies.** The extractor's `_mnaCache` is keyed **per
omega**, so a 3,791-frequency sweep would build the sparsity pattern and AMD ordering 3,791 times.
For the small-signal sweep use **one** `MnaSystem` for all sideband frequencies (the pattern and
ordering are topology properties and the SP-P2 cache verifies the sequence at every stamp anyway);
`PatternBuilds` for the whole sweep must be 1 (R-wsp8-9(c)). Factorisations are still one per
distinct frequency; the entries store only the small dense results, not the LU, so the memory of
§4 is what it is regardless of `n`.

**R-wsp8-7. `SSMaxHarm`.** Already in WSP-5: `K_ss < K` cuts both the sideband factorisations and
`n_c` linearly/cubically. Document the accuracy trade in the design note with a measured example
(the `f0/2` parametric case needs `K_ss ≥ 1`; the low-drive limit needs `K_ss ≥ 0`; a hard-driven
class-F stage's `1/H0` near `2f0` moved by X % between `K_ss = 3` and `7` — measure it, do not
guess), and keep the default at `K`.

---

## 4. Memory, and what to do when it is not enough

Per distinct sideband frequency: `N_int²` complex (`16 kB` at `N_int = 32`). Per `ω_ss`: `W`, `T`,
`I_src` — `4N² + 2N·N_int + 2N·N_int` complex (`~100 kB` at `N = N_int = 32`). For the example of
§1: ~3,800 sideband entries × 16 kB + 991 × 100 kB ≈ **160 MB**. Acceptable; not free.

**R-wsp8-8.** `AnalysisSettings.WspCacheBudgetMB` (default 512). When the projected cache exceeds it
the engine keeps the sideband `Y_NN` entries (the expensive ones to recompute) and recomputes the
per-`ω_ss` `W`/`T`/`I_src` per operating point (one factorisation + `N_int + 2N` transposed solves,
still far below the straightforward path); when even the `Y_NN` set exceeds the budget it falls back
to per-operating-point sideband extraction and **says so once** (`wsprobe.hb-cache-over-budget`, with
the sizes). The budget is a setting so a 64 GB workstation can raise it; never silently.

---

## 5. Parallelism

**R-wsp8-9(parallel).** The tickle grid is embarrassingly parallel: chunk `ω_ss` contiguously,
give each worker its own elaborated copy and its own `MnaSystem`/extractor cache (the SP-P3 pattern,
which already exists for the S-parameter sweep and already knows which netlists may not be copied —
external devices, control-referencing SDDs), and let each write its slice of `wsp[·, ssfreq, ·, ·]` by
index. The two-sided spectra of the operating point are read-only and shared. `MaxParallelism`
governs it exactly as for S-parameters; results are bit-identical to the serial path by construction
(each point's arithmetic is unchanged; nothing is merged).

Do **not** parallelise across operating points: the drive sweep warm-starts each HB point from the
previous one, and that ordering is the convergence story (`DriveLadder`, `HbDriveRamp`). The
small-signal work of point `i` may overlap the HB solve of point `i+1` as a later refinement; not in
this brief.

---

## 6. Measuring it (scratch harness, not the test tier)

Owner rules: measure with a scratch harness (`feedback-scratch-harness-not-benchmark-tests`), in a
Release build, never add a timing test. Record in `src/Engine/RESOLVED.md` a table like:

| Circuit | `N_int` | `K_ss` | probes | `M` | drive points | straightforward (s) | this brief (s) | sparse factorisations (before → after) |
|---|---|---|---|---|---|---|---|---|
| Hero 2 (1 FET) | 2 | 5 | 2 | 401 | 41 | … | … | 180,851 → 401 + 2·5·(f0/Δ) |
| 4-stage line-up | 8 | 7 | 8 | 991 | 41 | … | … | … |
| 16-FET amplifier (fixture from WSP-7) | 32 | 7 | 32 | 991 | 41 | … | … | 609,465 → 3,791 |

and the time split between dense and sparse work after the change, so the next person knows what
the remaining cost is (it should be the dense `J_ss` LU, `P·M` of them, and that is the honest floor
for this formulation).

---

## 7. Gates

`tests/Engine.Tests/HarmonicBalance/WSProbeHbPerformanceTests.cs` — counters and equality only.

**R-wsp8-9(a) — zero sparse work per operating point.** On WSP-5's Hero 2 fixture with a 41-point
drive sweep and 401 tickle points: sparse factorisations and sparse solves counted **after the first
operating point** are 0; the first operating point pays exactly `distinct(ω_k)` factorisations and
`distinct(ω_k)·N_int + M·2N` transposed solves.

**(b) — identity with the straightforward path.** `wsp` from this brief equals WSP-5's
straightforward implementation (kept as `WspHbReference` in the test project, or behind an internal
switch) to 1e-12 relative at every `(drive, ssfreq, row, col)`; the two-tone oracle of WSP-5 gate
(d) still passes against the fast path.

**(c) — pattern reuse.** `MnaSystem.PatternBuilds == 1` across the whole small-signal sweep;
`HbLinearExtractor.Factorizations` equals the number of distinct sideband frequencies.

**(d) — sideband coincidence.** With `SSStep = f0/200` the distinct-frequency count is
`M + 2K_ss·200` and the summary line reports it; with an unaligned step it is `M(2K_ss+1)` and the
grid is untouched (the `ssfreq` axis equals the requested grid exactly).

**(e) — invalidation.** Change a linear element between two operating points (a swept `R` in the
linear partition): the affected `Y_NN` entries are recomputed (count increases by exactly the number
of distinct frequencies) and the result equals a fresh run.

**(f) — budget fallback.** Set `WspCacheBudgetMB = 1`: results identical, the diagnostic emitted
once, the counters show the fallback's per-operating-point work.

**(g) — parallel identity.** `MaxParallelism = 4` gives a bit-identical `wsp` to the serial run and
each worker's counters sum to the serial counters.

## On completion

Findings and the measurement table to `src/Engine/RESOLVED.md`; **never a `CLAUDE.md`**. Append **§12**
"Performance under HB" to `docs/design/stability-wsprobe.md` (§9 margin, §10 HB, §11 NDF), and one paragraph to the user page's
§9 (WSP-7) saying what an aligned tickle grid and `SSMaxHarm` buy.

# Sonnet Brief — wBond MoM WM-3: making the kernel fast, and honest about what it costs

**Design:** `docs/design/mom-wirebond-kernel.md` §3 (sizing), §4.1 (why quasi-static is cheap), RW2
(report N before solving). **Prerequisite: WM-1 and WM-2 are landed and green, and WM-2 §9 item 5's
baseline measurements are recorded in `src/WBond/Mom/RESOLVED.md`.**

This brief has one job: **make kernel W1 as fast as it can honestly be made, and make it say what it
will cost before it costs it.** It changes no physics. Every WM-1 and WM-2 oracle must still pass, to
the same tolerance, after every milestone here.

**Where findings go: `src/WBond/Mom/RESOLVED.md`. Do not write in any `CLAUDE.md`.**

---

## Gate command

```
dotnet test tests/WBond.Tests --no-build
dotnet test tests/Ui.Tests    --no-build
```

plus, at the end of the brief and **only** at the end:

```
dotnet test tests/WBond.Tests --settings circuitrf.benchmark.runsettings
```

Note the mechanism: `--filter "Category=Benchmark"` **silently matches nothing** in this repo,
because this SDK's VSTest ANDs a command-line filter with the project's own `TestCaseFilter`
(`Category!=Benchmark`), producing an impossible AND. `--settings` is the only working opt-in. That is
verified, not assumed.

---

## 0. The rules of this brief

### 0.1 No unmeasured optimisation ships

Every milestone below is: **measure → change → measure → record the pair.** A milestone whose measured
improvement is under 1.2× is **reverted**, not kept "because it should be faster". Record the reverted
ones too — a negative result you can point at is worth more than a plausible-sounding change nobody
can defend later.

### 0.2 Every measurement is taken alone

`dotnet test tests/WBond.Tests --settings circuitrf.benchmark.runsettings --filter
"FullyQualifiedName~<class>"`, with `--no-build`, and nothing else running. A benchmark sharing a run
with others reads more than twice slow; this repo has mis-measured a number that way twice
(L9d's 71.9 s first read as 16.79 s). **Say in your report that you measured alone.**

### 0.3 The design note's sizing table is optimistic and you are going to have to say so

`mom-wirebond-kernel.md` §3 predicts ~3 s per frequency for the 200-wire case at ~6,000 unknowns. The
repo's own measured complex LU is **55.8 ms at N = 600** (`ImpedanceReduction`'s own note), which
scales cubically to **~29 s** at N = 4,800 — roughly ten times the note's estimate. Managed
`System.Numerics.Complex` arithmetic is the reason.

**Do not treat that as a failure of this brief.** Treat it as the number, measure it properly, and put
the corrected sizing table in `RESOLVED.md` with the measured coefficient. The design note stays as
written — it is the historical record of what was proposed, and correcting it in place would erase the
finding.

### 0.4 The knob that dominates everything is segments per wire, and it enters cubically

`N_s = wires × segments-per-wire`, and the per-frequency cost is `O(N_s³)`. Halving the segment count
is an **eightfold** speedup — larger than every other milestone in this brief put together. That is
why §5 exists, why §6's cost predictor matters more than it looks, and why "just use more segments"
must never be the tool's silent default.

### 0.5 What this brief does not do

- **No incremental / drag-interactive path.** `IncrementalFill` and `QualityLadder` exist for the
  *analytic* model, which is what the editor drags against. The MoM kernel is a Run-it-and-wait
  analysis, not a 60 fps one. Do not build a rank-update path for it.
- **No accuracy change.** If a milestone changes any WM-1 or WM-2 oracle's value beyond its stated
  tolerance, it is wrong, not faster.
- **No retarded kernel, no surfaces, no ACA.** §11 names them.

---

## 1. The baseline table — take this first, before changing anything

Three sizes, from `tests/WBond.Tests/TestDesigns.cs`:

| name | wires | arrays | seg/wire | `N_s` |
|---|---|---|---|---|
| **S** | 8 | 2 | 24 | 192 |
| **M** | 40 | 4 | 24 | 960 |
| **L** | 200 | 8 | 24 | 4,800 |

For each, measured alone, record:

| | S | M | L |
|---|---|---|---|
| `L` fill (parallel) | | | |
| `P` fill (parallel) | | | |
| `G` (Cholesky of `P` + solves) | | | |
| `K̃`, `W`, `H` | | | |
| **setup total** | | | |
| per-frequency assembly of `M̃` | | | |
| per-frequency factorisation | | | |
| **201-point sweep, serial** | | | |
| peak working set | | | |

**Size L may exceed a sensible test runtime at 24 segments/wire.** If it does, take it once, by hand,
outside the test suite, record the number in `RESOLVED.md`, and put size L into the Benchmark tier
**at a reduced segment count** (8/wire, `N_s` = 1,600) so there is a repeatable large-N gate that does
not cost an hour. Say exactly what you did.

---

## 2. M1 — form the inverses explicitly and make every product a scatter-add

**Expected: the largest single win in the setup phase, and it is pure bookkeeping.**

WM-1 §7 computes `G`, `K̃`, `W`, `H` by triangular solves — `O(N_n² N_r + N_s² N_r)`, roughly two dense
factorisations' worth. But `Ã = A R` has **at most two nonzeros per row**, and `R` has exactly one per
row, so every one of those products is an O(1)-per-entry index expression once the relevant inverse
exists:

```
G[a,b]   = Σ_{m ∈ a} Σ_{n ∈ b} Pinv[m,n]                          (scatter-add over merged nodes)
K̃[p,q]  = Ginv[s_p,s_q] − Ginv[s_p,e_q] − Ginv[e_p,s_q] + Ginv[e_p,e_q]
W[p,t]   = Ginv[s_p,t] − Ginv[e_p,t]
H[i,j]   = Ginv[i,j]                                              (the leading T × T block)
```

where `s_p`, `e_p` are segment *p*'s start and end **reduced** node indices.

So: Cholesky-factor `P`, form `Pinv` **in place over `P`** (`CholeskyFactor` gains an `InvertInPlace`
— an SPD inverse from an existing factor is `N³/3`, not another full factorisation), scatter-add to
`G`, Cholesky-factor `G`, form `Ginv` in place, then fill `K̃`, `W` and `H` with the four-term
expressions above.

Cost goes from `~2 N³` to `~⅔ N³` plus `O(N_s²)` of trivial arithmetic. Memory is unchanged — both
inverses overwrite their originals, and `P`/`Pinv` is released after `G` is built.

**Gate:** WM-1 §9.8's structural tests and WM-2 §6.2/§6.3 must be unchanged to their existing
tolerances. A four-term expression with one index swapped produces a plausible finite wrong answer —
which is exactly what §6.3's 0.1 % end-to-end gate is for.

---

## 3. M2 — a complex-symmetric factorisation

`M̃(ω)` is complex **symmetric** (not Hermitian), so `CholeskyFactor` does not apply, but an
unpivoted `LDLᵀ` does: `N_s³/3` against `ComplexLu`'s `2N_s³/3`. **Expected 2× on the dominant
per-frequency cost.**

Write `ComplexLdlt` next to `ComplexLu` in `src/WBond/`, with the same shape (`Factor(Complex[], int)`
/ `Solve(Complex[])`), plus a multi-RHS `Solve(Complex[] rhs, int cols)` so the `T` port solves share
one triangular sweep.

**Unpivoted `LDLᵀ` on a complex symmetric matrix is not unconditionally stable**, and pretending
otherwise is how a solver produces silent garbage at one frequency in a sweep. Two guards:

1. **Detect it.** Track the smallest `|d_k|` against the largest; if the ratio falls below a declared
   threshold (start at 1e-12), **fall back to `ComplexLu` for that frequency point** and record a note
   on the result.
2. **Prove it does not happen in practice.** A Benchmark-tier test that runs a 201-point sweep on size
   M with both factorisations and asserts `max |Z_ldlt − Z_lu| / |Z_lu| < 1e-9` at every point.
   `M̃ = −ω²L + K̃ + jωD` with `L` and `K̃` real symmetric and `D` having positive real part is
   well-behaved for real bond geometry — but "should be" is not a measurement, and this test is what
   turns it into one. **Record how many points, if any, took the fallback.**

**If the measured win is under 1.5×, revert it** and say so. A second factorisation path is real
maintenance cost and it has to earn its keep.

---

## 4. M3 — parallelism, in the two places it belongs

### 4.1 Across frequency — the big one for sweeps

Frequency points are completely independent: same `L`, same `K̃`, same `W`, same `H`; only `D(ω)`,
`M̃`, its factorisation and `T` solves are per-point. `Parallel.For` over the frequency grid is nearly
linear speedup.

**The constraint is memory, not cores.** Each thread needs its own `M̃` — `16·N_s²` bytes — plus the
factorisation's workspace. At size M that is 14.7 MB per thread (fine); at `N_s` = 4,800 it is 369 MB
per thread (**not** fine on 10 cores).

So the degree of parallelism is computed, not assumed:

```
threads = clamp( 1, Environment.ProcessorCount,
                 floor( settings.SolveMemoryBudgetBytes / (bytesPerThread) ) )
```

with `SolveMemoryBudgetBytes` defaulting to something defensible (state your reasoning — a fraction of
`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` is one honest choice) and **the chosen thread count
reported in the result's notes**. A user whose 200-wire sweep ran single-threaded should be able to
see why in one line.

Record the measured speedup at sizes S, M and reduced-L.

### 4.2 In the fills

WM-1 already specifies `Parallel.For` over rows for both fills, upper triangle only. **Verify it is
actually on** (the wire-basis default is `parallel: false` — a copied default is an easy miss) and
measure the speedup. If either fill is serial, that is a one-line fix worth several×.

---

## 5. M4 — the segmentation ladder, and a default that is defensible

Given §0.4, the segment count is the user's real cost knob and it must be presented as one. Replace a
bare integer with a named ladder in `WireMomSettings`, plus the escape hatch:

| level | segments/wire | when |
|---|---|---|
| **Fast** | 8 | a first look, or a wire count that would otherwise not solve |
| **Balanced** (default) | *from WM-2 §6.5's convergence data* | the shipped default |
| **Accurate** | 2 × Balanced | confirming a Balanced answer |
| explicit | any | the override |

**The Balanced value is whatever WM-2 §6.5 measured, not 24 because this brief said so.** If WM-2's
convergence table says 24 is not converged at 40 GHz, Balanced is larger and the cost table says what
that costs. Put the ladder's three measured (accuracy, cost) pairs in `RESOLVED.md` so the choice is
inspectable.

**Add a convergence-vs-cost test** at the Benchmark tier: size S at 8 / Balanced / 2×Balanced, three
frequencies, recording both `max|ΔS|` and wall clock. That is the table a user needs and it is the
table that justifies the default.

---

## 6. M5 — tell the user what it will cost, before it costs it

This is the milestone that makes the kernel feel finished, and it falls out of §1's measurements.

Calibrate a cost model from the baseline table:

```
setupSeconds       ≈ a·N_s²  +  b·N_s³
perFrequencySeconds ≈ c·N_s³
sweepSeconds       ≈ setupSeconds  +  points · perFrequencySeconds / threads
```

Fit `a`, `b`, `c` from the measured sizes, store them as named constants **with the machine and date
they were measured on in the comment** (this repo already documents measured constants that way —
`PotentialCoefficients.FarThresholdFactor` is the model), and extend WM-1's mesh report with:

- predicted setup time,
- predicted per-frequency time,
- **predicted total sweep time for the requested grid, at the thread count §4.1 will actually choose**,
- predicted peak memory including the per-thread `M̃`.

Then two things the prediction unlocks:

1. **`WireMomSettings.SegmentsForBudget(design, points, seconds)`** — invert the model and answer
   "what segments-per-wire fits in 30 seconds?". Surface it in the refusal of WM-1 §8 so the message
   names a number the user can act on rather than a direction: *"…try 11 segments per wire, which
   fits in 30 s."*
2. **A slow-run warning, not a refusal**, when the prediction exceeds a threshold (start at 60 s):
   *"This sweep is predicted to take about 14 minutes (4,800 unknowns × 201 points, 3 threads).
   Fast segmentation would take about 1 minute."*

A prediction that is systematically wrong is worse than none, so **gate it**: a routine test asserting
the predicted sweep time for size S is within 2× of the measured one. Two× is a deliberately loose
band — this is a user-facing estimate on unknown hardware, not a contract.

---

## 7. M6 — reuse the plan across frequency grids

Split the kernel into `WireMomPlan` (everything frequency-independent: mesh, `L`, `K̃`, `W`, `H`,
notes, cost model) and `Solve(plan, freqs, ct)`. A caller that re-exports the same design on a
different grid, or that solves the same design at two grids for a convergence check, pays setup once.

**This is a small change with a real payoff and one trap:** the plan must be invalidated when anything
it was built from changes. Key it on the design and the settings, and do **not** build a mutation-aware
cache — `WireMesh`'s own doc comment records the lesson (a snapshot that silently goes stale is worse
than a rebuild). Hand the plan out explicitly and let the caller hold it; do not hide it in a static.

**Also honour `CancellationToken`** throughout the frequency loop and between setup stages. A
fourteen-minute run that cannot be cancelled is a bug regardless of how fast the arithmetic is.

---

## 8. M7 — measure whether a library beats the hand-written solver

`ComplexLu` and `CholeskyFactor` are straightforward managed implementations, and the repo already
depends on **NumFlat** (in `src/Engine`, for dense linear algebra). Measure NumFlat's complex LU
against `ComplexLu` at `N_s` = 960 and 1,600.

**Three rules if it wins:**

1. A **`PackageReference`** to `src/WBond` does not break the "leaf project" property — that comment
   forbids `ProjectReference`s, which are what would close the `Core → WBond` cycle. Re-read the
   csproj comment and confirm before you add anything.
2. `tests/Firewall.Tests` must stay green — it asserts the *UI* firewall, which a numerics package
   does not touch, but confirm rather than assume.
3. **If it turns out to pull a native (non-managed) binary, stop and ask.** The repo root `CLAUDE.md`
   lists native dependencies under **Ask before** — cross-platform risk — and that applies here in
   full. Report the finding and the measured win, and let the owner decide.

**If it does not win by ≥1.5×, do not add it**, and record the measurement so nobody re-runs this
experiment in a year.

---

## 9. The targets

Stated so there is something to hit and something to report a shortfall against. **These are targets,
not gates** — a shortfall that is measured, explained and recorded is an acceptable outcome; an
unmeasured one is not.

| | setup | per frequency | 201-point sweep |
|---|---|---|---|
| **S** (`N_s` = 192) | ≤ 100 ms | ≤ 2 ms | **≤ 0.5 s** |
| **M** (`N_s` = 960) | ≤ 2 s | ≤ 120 ms | **≤ 5 s** (parallel over frequency) |
| **reduced L** (`N_s` = 1,600) | ≤ 6 s | ≤ 550 ms | **≤ 20 s** (parallel) |
| **L** (`N_s` = 4,800) | report only | report only | **report only, and predict it accurately** |

The size-L row is deliberately not a target. `mom-wirebond-kernel.md` §3 wanted the 200-wire array to
solve; at Balanced segmentation it will not, in any reasonable time, on this formulation and this
arithmetic. **The v1 answer to the 200-wire array is Fast segmentation plus an accurate prediction and
an honest warning**, and saying so plainly in `RESOLVED.md` is part of this brief's deliverable.

---

## 10. Benchmark-tier accounting

The repo's opt-in tier is currently ~40 minutes across 122 methods. **Say exactly what you added and
what each measures**, and keep it small — this brief should add on the order of **5–8 methods and
under 10 minutes**, not thirty:

- the size-M and reduced-L sweep-cost measurements (§1, §9);
- the `LDLᵀ`-vs-`LU` agreement sweep (§3);
- the frequency-parallel speedup measurement (§4.1);
- the convergence-vs-cost ladder (§5);
- the NumFlat comparison (§8), if you run it.

Everything else in this brief — the four-term index expressions of §2, the cost-model accuracy gate of
§6, the plan-reuse test of §7 — belongs in the **routine** tier at size S, where it costs milliseconds.
**Correctness gates do not belong behind an opt-in flag.**

---

## 11. Named, and deliberately not built

Record each of these in `RESOLVED.md` with the one-line reason, so the next person does not
re-discover them:

- **ACA / low-rank compression of `L` and `K̃`.** The genuine answer to `N_s` > 5,000, and
  `mom-wirebond-kernel.md` §7.3 already names it as "the first place ACA compression genuinely earns
  its keep". It is a project, not a milestone, and it should be decided with real numbers from this
  brief rather than in advance.
- **Iterative solve (GMRES) instead of a factorisation.** `src/Engine/Mom/PlanarGmres.cs` exists and is
  the precedent. It trades a cubic factorisation for `O(N²)` per iteration, which wins only if the
  iteration count stays low — and with `T` right-hand sides per frequency and a matrix that changes
  every frequency, the trade is much less favourable here than in the planar kernel. Measure before
  believing.
- **Single precision.** Rejected: `P` and `G` are inverted, and the low-frequency conditioning of
  §WM-2 §5 is already the delicate part.
- **The retarded kernel (W2).** `mom-wirebond-kernel.md` §4.2 and RW3: the same kernel object with a
  `Retarded` flag, adding `e^{−jkR}` to the mutual terms. It is a small formulation delta and a large
  cost delta — **every matrix becomes frequency-dependent, so every structural speed argument in these
  three briefs evaporates**, and a 201-point sweep costs 201 fills instead of one. It is the correct
  next phase and it needs its own brief, its own cost story, and its own gates.

---

## 12. Report back

In `src/WBond/Mom/RESOLVED.md`:

1. **The §1 baseline table and the same table after every milestone.** Before/after pairs, measured
   alone, on the stated machine.
2. **Which milestones were kept and which were reverted, with the measured factor for each** —
   including the reverted ones.
3. **The corrected sizing table** replacing `mom-wirebond-kernel.md` §3's estimate, with the measured
   cubic coefficient and the machine it was measured on.
4. **The cost model's fitted constants** and the measured accuracy of its predictions at all three
   sizes.
5. **The Balanced segment count** and the convergence-vs-cost table that justifies it.
6. **What you added to the Benchmark tier**, method by method, with each one's measured duration and
   the new tier total.
7. **The honest statement about size L** (§9) — what the 200-wire array actually costs, at what
   segmentation it becomes practical, and what the user is told before they wait.

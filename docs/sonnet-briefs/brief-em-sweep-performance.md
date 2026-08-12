# Brief — EM sweep performance: the mesh frequency, cores, and the fill's cost

**A FOLLOW-UP to L8/L9. It is not a slice of either, it adds no physics, and it must not change a
single published number.** Every milestone here is a cost reduction or a control over one; the one
that *can* change an answer (M0) is the one whose own gate is an accuracy measurement.

It touches **no Green's function, no DCIM fit, no extraction algebra, no de-embedding algebra, no
basis function and no closed-form integral.** If you find yourself editing `SpectralGreens.cs`,
`Dcim.cs`, `SingularExtraction.cs` or `PlanarDeembed.cs`, you are in the wrong brief.

Read first, in this order:

- `src/Engine/Mom/CLAUDE.md` §**L8c**, specifically **"Tier 8 — the cost, and it is a FINDING rather
  than a pass"** (the per-frequency cost table, and the two named-but-not-built savings), and
  §**L8d**'s **"Tier 7 — the cost"** (where de-embedding's 4.4× goes).
- `src/Engine/Mom/CLAUDE.md` §**L9e**, **"M1 — the calibrator collision"**. That section is the
  structural precondition for M3 and it already did the hard half.
- `src/Engine/Mom/PlanarFill.cs` **`ForRows` (~line 1519)** and `PlanarFillSettings`'s own
  `Parallel` parameter. This is the only parallelism that exists today.
- `src/Engine/Mom/PlanarSolve.cs` **lines ~255–275** (`PlanarPortCalibrator.At` and `_rawCache`),
  **~730** (the standards loop) and **~762** (the frequency loop). Those three are the sequential
  ones.
- `src/Ui/Layout/Em/CLAUDE.md` §**"Conformal (cut) boundary cells — the FOURTH mesh control"**. M0
  adds a fifth, and that section is the template for how a mesh control is added *correctly* —
  especially the `MeshHash` line.

Nothing below is repeated from those.

---

## §0 — the measurement, taken on a real user design

`taper.clay` (one MKlopf taper, Z1 = 50 Ω, Z2 = 12 Ω, L = 50.8 mm, RO4350B 20 mil) at the user's own
`.cem`: `CellsPerWavelength = 5`, `BoundaryCells = Conformal`, `EdgeCells = 3`, 1–20 GHz × 51 points.

| | N | |
|---|---|---|
| **DUT** | **1,750** | 995 cells, 160 conformal cut cells, 25 slivers merged |
| standards, port 1 (50 Ω, 1.06 mm feed) | 28 / 34 | negligible |
| standards, port 2 (12 Ω, **6.71 mm** feed) | **2,047 / 2,137** | *each larger than the DUT* |

**One de-embedded point at 20 GHz: 238.3 s measured**, including the one-time geometric cores. The
user's reported ~2 h over 51 points (≈ 141 s/point amortised) is consistent with that.

**~75% of the run is calibrating port 2, not solving the taper.** The engine's own note says it:
*"De-embedding costs 2 calibration(s) over 2 port(s), 4 standard mesh(es) of N = 28 / 34 / 2047 /
2137 against the DUT's N = 1750 — 2.43× the DUT's unknowns, solved at every frequency alongside
it."* A standard is a uniform line of the port's own width; the 12 Ω end is 6.71 mm wide and 23
basis functions across, and the two ports cannot share a calibration because their widths differ.

**Machine: 10 physical cores.** `Environment.ProcessorCount` = 10.

**Also measured, and it bounds M0's prize:** the mesh is sized at the sweep's top. The engine's note
reads *"Cell size capped at λ_g/5 = 1.567 mm — λ_g = 7.835 mm in εᵣ = 3.66 at 20 GHz, the highest
frequency of the sweep. Widening the sweep upward will change this, and with it the unknown count."*
The transverse pitch is *not* λ-driven here — *"Narrowest conductor dimension 1.189 mm, meshed 4
cell(s) across (target 4)"* — so `MinCellsAcrossConductor` binds across the taper and λ binds along
it. **M0's saving is therefore axial only, and the brief must not claim it is quadratic.**

---

## §1 — THE OBVIOUS ANSWER IS WRONG, AND THIS IS WHY

"The sim is slow, let the user pick how many cores" implies the solver is single-threaded. **It is
not, and it never was.**

`PlanarFill.ForRows` wraps every one of its ten call sites in `Parallel.For` whenever
`PlanarFillSettings.Parallel` is true — which is its default — and there is **no
`MaxDegreeOfParallelism` anywhere in the repository** (grep it; zero hits in `src/`). So the 2-hour
run above already saturated all 10 cores inside the fill. **A core-count cap on its own can only
ever make it slower.**

What is genuinely sequential, and what M2/M3 are for:

| | file / line | |
|---|---|---|
| the frequency loop | `PlanarSolve.cs` ~762 | `foreach (double f in freqs)` |
| the calibration standards, per frequency | `PlanarSolve.cs` ~730 | `for (int j = 0; j < calibrators.Count; j++)` |
| the standards *within* one calibrator | `PlanarSolve.cs` ~264–268 | `s0`, then each `sl[i]` |
| the dense LU | `PlanarSystem.Lu` | NumFlat, single-threaded |

**So the control the user asked for is still the right control — but it is a control over parallelism
that does not exist yet, not over the parallelism that does.** M1 builds the seam; M2 and M3 build
the thing worth capping.

---

## M0 — the mesh frequency parameter

**The prize, and it is available with no numerics.** The mesh is sized from the top of the sweep.
Sizing it at 10 GHz on the §0 design roughly halves the axial cell count; N falls with it and the
fill falls as N². **Measure the real ratio; do not assume 4×** — §0 records that the transverse pitch
is set by `MinCellsAcrossConductor`, so only one axis responds.

### R-emp-1 — it is a MESH SETTING, so it lives on `PlanarMeshSettings`

`PlanarMeshSettings.MeshFrequencyHz` (`double?`, **null = the sweep's own top**, which is today's
behaviour exactly). Not a new argument to `PlanarExtractor.Extract`, and **not** a change to
`PlanarProblem.MaxFrequencyHz` — see R-emp-3 for why that distinction is load-bearing.

`SurfaceMesher.Mesh` derives λ_g from it:

```csharp
double lambdaG = settings.MeshFrequencyHz is { } f && f > 0
    ? (problem with { MaxFrequencyHz = f }).GuidedWavelengthM
    : problem.GuidedWavelengthM;
```

**That `with` pattern already exists** — `PlanarKernel.cs:197–199` does exactly this to get λ_g at
the sweep's actual top. Reuse it; do not add a second way to re-derive a guided wavelength.

### R-emp-2 — the REPORT must say what the mesh was sized at, or the note becomes a lie

`PlanarMeshReport.FrequencyHz` is currently `problem.MaxFrequencyHz` (`SurfaceMesher.cs:568`) and the
note at `SurfaceMesher.cs:421` quotes it as *"the highest frequency of the sweep"*. Both must become
the **mesh** frequency, and the note's wording must change with it. Leaving either alone produces a
report claiming a mesh was sized at 20 GHz when it was sized at 10 — the exact class of silently
wrong statement this codebase keeps finding.

Add a second note whenever `MeshFrequencyHz` is below the sweep's top, quantifying the trade in the
unit the user set:

> The mesh was sized at 10 GHz, not at the sweep's 20 GHz top. At 20 GHz the cells are λ_g/2.5
> rather than the λ_g/5 you asked for. Raise Mesh frequency, or raise Cells per wavelength, if the
> top of the band matters.

Effective cells/λ at the sweep top is `CellsPerWavelength × MeshFrequencyHz / sweepTop`.

**A refusal is NOT specified here, and adding one without measuring it first is forbidden by house
rule.** M0's gate below is what decides whether a floor is earned; if it is, state it in effective
cells/λ (a physical quantity), never in hertz.

### R-emp-3 — `MaxFrequencyHz` still means THE SWEEP'S TOP, and three consumers depend on that

`PlanarProblem.MaxFrequencyHz` currently answers two unrelated questions and M0 separates them. After
M0 it answers exactly one — *how high does this sweep go* — and these three consumers must keep
reading it, unchanged:

1. **`PlanarKernel.CanSolve` (~line 115–117)** → `MidpointRuleVerdict`, i.e. R-via-6's electrical via
   bound `k·ℓ ≤ 0.30`. That bound is about the sweep's top frequency and has nothing to do with the
   mesh. Pointing it at the mesh frequency would let a user silently widen a physics refusal by
   turning down a performance knob.
2. **`PlanarKernel.cs:197`** — λ_g at the sweep's actual top, for the ρ/λ validated-range note.
3. **`EmSnpProvenance.cs:112`** — already in the **geometry** hash. Leave it there.

### R-emp-4 — `MeshHash`, and this is the line that is easy to forget

`EmSnpProvenance.MeshHash(PlanarMeshSettings)` **must include `MeshFrequencyHz`.** An `.snp` produced
at one mesh frequency is not current for another, and the hash is the only thing that can say so.
`src/Ui/Layout/Em/CLAUDE.md` records this exact trap for `BoundaryCells` — *"leaving it out would
have been precisely the staleness failure R-em-20 exists to prevent, in one line that is easy to
forget"* — and the same test shape applies: assert the new term moves the hash **and** that every
existing term still does, so it did not displace one.

### R-emp-5 — `.cem`, `Auto`, and the panel

- **`CemPlanarMesh.MeshFrequencyHz` is nullable and omitted at its default**, exactly like
  `BoundaryCells` and `DirectVerticalKernel` beside it, so every existing `.cem` gains no byte and
  re-serialises byte-identically. This is an asserted property of the format, not a nicety.
- **It survives `PlanarMeshSettings.Resolved`'s `Auto` collapse, and setting it does NOT clear
  `Auto`** — same reasoning `BoundaryCells` already carries: `Auto` decides cells/λ and edge cells,
  and has no opinion about *which frequency* to size at. Clearing `Auto` here would silently pin the
  cell size the moment a user touched the mesh frequency.
- **It is stored in hertz and edited in the panel's own frequency unit** through the existing
  staged-text committer (`CommitMeshField`), not as a raw double — every other dimensioned field in
  that panel goes through `LayoutUnits`/the frequency spec's unit and this must not be the exception.
- One undo entry per commit, and it calls `InvalidateMesh()` — the panel must not go on reporting an
  N produced at another mesh frequency.
- Blank means "max sweep", and the placeholder says so.

### M0's own gate — the accuracy measurement, and it is the deliverable

**Without this the parameter is a foot-gun.** On the §0 design, and on §10.7's own FR-4 hero:

- solve the full band with the mesh sized at the sweep top (today's behaviour) — the reference;
- solve it again with the mesh sized at 1/2 and at 1/4 of the sweep top;
- report **worst |ΔS| against the reference, per decade of the band**, alongside N and wall clock for
  each.

Report the table. If halving the mesh frequency costs less than the de-embedding residual L9d already
measured (~1e-2 on a two-level structure; L8d measured 6.0e-3 at 10 GHz on 1.6 mm FR-4), say so —
that is the number that makes the parameter defensible, and it is the number a user needs to choose
a value. **If it costs more than that, say so too**; a measured "this knob is not safe below X" is a
legitimate outcome and outranks the saving.

---

## M1 — the core-count control, and the seam it needs

### R-emp-6 — where it is STORED is not where it is SHOWN

**Store it in `AppPreferences`; show it in the EM Setup panel.**

Core count is a property of the machine, not of the design. A `.cem` travels with the workspace, and
opening a colleague's setup must not pin your core count to theirs — the same reasoning that keeps
`AppPreferences.HarmonicaKitFolders` and the wirebond defaults per-user. The control belongs in the
EM Setup panel because that is where the user is standing when the cost lands, with a one-line note
that it is a machine setting and not part of the design.

**The owner's reversal point is one field**: if a per-setup throttle is wanted instead, move it to
`EmSetupModel` under the same omit-at-default rule — but then R-emp-7 below becomes mandatory rather
than merely correct.

### R-emp-7 — it must NEVER enter any provenance hash

R-fil-11 already guarantees the answer is independent of scheduling: parallelism is over ROWS of the
packed upper triangle, every entry is written exactly once, and the result does not depend on how the
scheduler interleaved. So core count cannot change a number, and marking an `.snp` stale because a
user moved a slider would be a straightforward lie. **Assert it: same problem, same mesh, same ports,
different core count → identical `GeometryHash`/`MeshHash`/`PortHash`.**

### R-emp-8 — and it must never change the ANSWER, asserted as bit-identity

The strongest available gate, and it is cheap: fill the same matrix at `MaxDegreeOfParallelism` = 1,
2 and unbounded and compare **every entry for bit-identity**. R-fil-11 says this must hold; nothing
currently tests it, and M2/M3 make it much easier to break.

### The plumbing

- `PlanarFillSettings.MaxDegreeOfParallelism` (`int?`, null = unbounded), threaded into `ForRows`:
  `Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = … }, row)`.
- `PlanarSolveSettings.MaxDegreeOfParallelism` (`int?`), for M2/M3. One number drives both; a user
  setting "4" means four cores total, not four per level of nesting. **Nested `Parallel.For` under a
  cap is the trap** — see R-emp-10.
- `EmRunService` reads the preference and populates both. Today it builds `solveSettings` from
  `PlanarSolveSettings.Default with { … }`; add one more `with` term.

**Insertion position:** `PlanarFillSettings` is a positional record and there is **no
`new PlanarFillSettings(` anywhere in the repo** — every site is `Default` plus a name-based
`with { }`. Adding a parameter mid-list is therefore safe, and the CLAUDE.md note recording that for
`DirectVerticalKernel` applies unchanged. Verify the grep is still zero before relying on it.

### The panel control

Built from `Environment.ProcessorCount`:

- **`Automatic (10 cores)`** — the default, and it maps to `null`, i.e. today's unbounded behaviour;
- then 1, 2, 4, 8 … powers of two up to the count, plus the count itself when it is not one.

---

## M2 — fan out the DUT and the standards at each frequency

Five independent solves per frequency on the §0 design (N = 1,750 / 2,047 / 2,137 / 34 / 28), each a
fill plus a factorisation plus back-substitutions, sharing nothing but the **read-only** kernel fit
and the **read-only** cores.

**Expected: 3–4× on a 10-core box, not 5×.** The work is badly unbalanced — two solves are 96% of it
— so the span is the largest standard, not the mean. Say the measured number, not the ideal one.

### R-emp-9 — the standards' loop is the one to parallelise, and it is already independent

`PlanarPortCalibrator.At` (`PlanarSolve.cs` ~255–275) solves `s0` and each `sl[i]`, then caches under
`_rawCache[fHz]`. **The solves are independent; only the write into `_rawCache` and the branch
continuation that follows are not.** Fan out the solves, join, then do the cache write and the
continuation on one thread exactly as today.

`PlanarFrequencyKernel` and `PlanarFillCores` are read-only once built and are safe to share. **The
fit cache is not** — see R-emp-11.

### R-emp-10 — nested parallelism under a cap needs ONE budget, not two

The fill is already `Parallel.For` over rows. Fanning out five solves that each fan out over rows is
nested parallelism, and `MaxDegreeOfParallelism` on the outer loop does not bound the inner one — TPL
will happily run 5 × 10 workers.

**Recommendation: use a single `SemaphoreSlim` sized to the cap, acquired by the innermost work**, or
give the fill's own `ForRows` a smaller cap while an outer fan-out is active. Whichever is chosen,
state it once in `PlanarFillSettings`'s own doc comment; do not leave two independent caps that a
reader has to multiply in their head.

---

## M3 — parallelise across frequencies

**This is the big one, and L9e already did the hard half.** From `src/Engine/Mom/CLAUDE.md` §L9e's
own M1: the expensive part of a frequency point (fill + factor + back-substitute, on every standard
mesh) depends **only on the frequency**, while the γ 2π-unwrap and the a₂₁ sign are **continuations**
that depend on the order. L9e separated them precisely so an adaptive scheme could insert a point
mid-band: `_rawCache` holds the raw standard matrices per frequency, and `RestartBranchContinuation()`
replays the ordered half **at zero extra solves**, measured bit-identical against a straight-through
sequential sweep.

**That is exactly what cross-frequency parallelism needs, and it is why M3 is tractable at all.**
Solve the frequencies in any order, in parallel; then replay the branch continuation sequentially
over the sorted set, as the adaptive path already does.

### R-emp-11 — the fit cache is SHARED MUTABLE STATE and must be made thread-safe

`PlanarKernelSet`'s fit cache is shared across every `For()` view — L9d made it so deliberately,
because a de-embedded solve touches five meshes per frequency and per-view caching turned 9 fits per
frequency into 9 per mesh. **Concurrent frequencies will race it.**

`Dcim.FitAtHeights` is ~90 ms and a direct table is *seconds*; the engine already carries a
per-key build gate for exactly this (see §"Two robustness fixes" in the G_A^zz section of
`src/Engine/Mom/CLAUDE.md`). **Extend that pattern — a per-key gate object, double-checked, with the
shared dictionary read under one lock on both sides — rather than putting a lock around the whole
fit, which would serialise the very thing M3 exists to parallelise.**

**`PlanarKernelSet.FitCount` must still be correct under concurrency**, or D7's own counter gate —
the one that catches a fit-per-mesh regression — silently stops meaning anything. Make it interlocked.

### R-emp-12 — memory is the binding constraint, and it is why the cap exists

N = 2,137 → 16·N² ≈ 73 MB of matrix; five meshes in flight ≈ 130 MB per frequency, plus cores. Ten
concurrent frequencies is ≈ 1.3 GB on the §0 design and scales as N² — at R17's 5,000-unknown ceiling
a single frequency's matrix is 400 MB and ten concurrent is not runnable.

**Report the projected working set in the panel's own run note** (the run already reports N and the
standards' N), so a user choosing 8 cores can see what they are asking for. **And derive a safe
default cap from it** rather than from `ProcessorCount` alone: `Automatic` should mean
`min(ProcessorCount, memory budget / per-frequency footprint)`.

### R-emp-13 — the ORDER of published points is the user's, and adaptive sampling still owns it

R-adf-2/3 already guarantee the published grid is the grid that was asked for and that a solved point
carries the solver's own matrix byte for byte. **M3 must not weaken either.** Assert it: the same
sweep run at cap = 1 and at cap = 8 produces **bit-identical** `DataSet`s, adaptive on and off.

---

## M4 — per-cell-pair moment caching

Already named in `src/Engine/Mom/CLAUDE.md` §L8c as the first of the two savings that would make an
interactive sweep possible, and not built:

> *"Each same-direction basis pair currently integrates four cell pairs independently, and adjacent
> rooftops share cells, so the same cell pair is integrated up to four times. Caching the 2×2
> ramp-moment form per cell pair is a ~4× saving on the dominant fill term. It costs `4 × M²/2`
> complex, which is affordable at the hero (2 MB) and is NOT at the ceiling (200 MB) — so it wants to
> be blocked, which is why it was not done here."*

**The ~4× is an estimate, not a measurement.** M4's first act is to measure the actual redundancy on
the §0 design (count distinct cell pairs against cell-pair integrations performed) and report it
before building the cache.

**Blocked, not global**, for the stated memory reason: process the matrix in row blocks and keep a
cache scoped to the block, so the footprint is `4 × blockSize × M` rather than `4 × M²/2`.

**It interacts with M1/M2/M3 and that interaction is the trap.** A shared mutable cache under
`ForRows`' existing row-parallelism is a race, and the natural fix (a lock) can easily cost more than
the saving. **Prefer a per-worker cache with no sharing at all** — the redundancy being exploited is
between *adjacent rows*, which the row partitioner keeps together — and measure whether that captures
most of the win before reaching for anything shared.

**R-emp-14 — bit-identity again.** A cached moment and a freshly integrated one must agree to the
last bit, or every measurement in `src/Engine/Mom/CLAUDE.md` moves. If the cache changes the
association order of a sum, it is not a cache; it is a new formulation. Assert entry-level bit
identity against `MaxDegreeOfParallelism = 1` with the cache off.

---

## M5 — the FFT accelerator (AIM), and it is INVESTIGATE-THEN-DECIDE

This is the architectural answer to "another tool solved a comparable problem in under 10 minutes",
and it is the only milestone here that could close that gap rather than narrow it. **It is also the
only one that is a research commitment, so it gets a decision gate before it gets an
implementation.**

### The correction that matters before anyone starts

The usual framing — *"they use a uniform grid, so the matrix is block-Toeplitz and the matrix-vector
product is an FFT"* — is **true and not applicable here as stated.** A Toeplitz structure needs
translation invariance, and this mesher's grid is edge-graded and conformally cut, so it is *not*
uniform even in the interior. Building the accelerator on raw Toeplitz would mean giving up edge
grading and cut cells — i.e. giving up exactly what buys accuracy at low N (L8b's own control: 4.437%
→ 0.431% for 3.6× the unknowns, and the uniform ladder needs ~20× the unknowns to catch it).

**AIM (the Adaptive Integral Method, a.k.a. pFFT) is the technique that fits.** It projects arbitrary
basis functions onto a *separate* uniform auxiliary grid, does the far-field matrix-vector product by
FFT on that grid, and keeps a **sparse near-field correction** computed exactly. The mesh stays graded
and conformal; only the auxiliary grid is uniform. Cost goes from O(N²) fill + O(N³) solve to
O(N log N) per iteration plus a sparse near-field fill.

**The near-field correction reuses the existing singular-integral machinery unchanged** — L8c's
closed forms, `SingularExtraction`, `RectangleIntegrals`, `PolygonIntegrals` — which is what makes
this plausible rather than a rewrite. The far field is where the fitted DCIM kernel is smooth and
where a projection is legitimate.

### R-emp-15 — it needs an ITERATIVE SOLVER, and that is the same objection that deferred ACA

`src/Engine/Mom/CLAUDE.md` §L9e's Tier 5 deferred ACA on two grounds, and the second applies here
verbatim: *"a compressed matrix needs a solver that consumes it: an iterative one, whose convergence
on a MoM system is not guaranteed and is its own research item."*

**So M5's decision gate is the iterative-convergence question, not the FFT.** Measure it first, and
it can be measured *today* against the existing dense matrices with no accelerator at all:

1. Fill the §0 design's DUT and its two large standards densely, as today.
2. Solve each with GMRES (restarted, no preconditioner; then with a diagonal preconditioner; then
   with a near-field-block ILU) to the tolerance the de-embedding actually needs.
3. **Report iteration counts and whether they grow with N and with frequency.**

**If GMRES needs O(N) iterations, AIM buys nothing** — O(N log N) per iteration × O(N) iterations is
worse than the direct solve, and that is the honest outcome. If it converges in tens of iterations
with a near-field preconditioner, the accelerator is worth building.

**Do this measurement before writing one line of the projection.** It is a day's work against a
multi-week phase, and it is decisive. Precedents for that ordering, all in this codebase and all
correct: L7b-b's Route B, L9c's amplitude cap, L9e's ACA, the edge-mesh brief, the ground-via chain.

### R-emp-16 — the two accuracy gates it would have to pass

If M5 proceeds, it is a new formulation of the same matrix and it inherits the whole existing oracle
ladder rather than a new one:

- **Against the dense fill on the same mesh**, entry by entry, on a fixture small enough to fill both
  ways. L8c reached 5.0e-6 against its own independent oracle; the accelerated product must be
  compared against the *dense* product, and the target is the fill's own accuracy, not the kernel's.
- **The de-embedded S of §10.7's hero and of the §0 design**, against the dense result, across the
  band. The de-embedding divides by `a₂₁²` and amplifies error at the low-frequency end (L8d measured
  ~22× at 2 GHz on a 20 mm FR-4 line) — so the gate must be taken across the band, not at one point.

### R-emp-17 — and the near-field radius is the one free parameter

AIM's accuracy is governed by the projection order and by how many cells around each basis are
treated exactly. **Report both as a measured trade**, in the shape L8c's own extraction-order table
and L9e's `ViaZNodes` table already use: sweep it, tabulate error against cost, and pick the default
from the table rather than from a reference.

---

## Deferred, with the measurement, and NOT to be revisited without a new one

- **ACA / matrix compression.** Measured: far-field blocks of a real N = 790 fill need **53–62% of
  min(m,n)** rank at 1e-3 *with a full pivot* — structural, because blocks reachable under R17's
  ceiling are not many wavelengths apart. AIM does not have this problem (it does not rely on any
  block being low-rank), which is why M5 is AIM and not ACA.
- **An iterative solver on its own, without an accelerator.** At N = 1,750 the fill dominates the LU
  (L8c measured 114× at N = 552 and still 1.8× at N = 4,933); replacing an O(N³) step that is not the
  bottleneck buys nothing. It becomes interesting only *as part of* M5.
- **Persisting the DCIM fit across runs.** `Dcim.Fit` is ~0.2 s per frequency regardless of N,
  i.e. **0.14%** of a 141 s point. There is nothing there.
- **Lowering `MinCellsAcrossConductor` below 4.** It is auto-derived rather than a user control for a
  reason, and §0 shows it is what sets the transverse pitch on this design. It is not a performance
  knob.

---

## Guardrails

- **No physics.** Nothing in `SpectralGreens.cs`, `Dcim.cs`, `SommerfeldIntegral.cs`,
  `SingularExtraction.cs`, `PlanarDeembed.cs`, `PlanarCalibration.cs` or any basis function changes.
  M5 is the sole exception and only in *how* the product is formed, never in what it is a product of.
- **No refusal is widened, narrowed or re-worded** except M0's own new note. In particular
  `Dcim.ValidatedRhoOverLambdaAtHeights`, `PlanarLevels.MaxElectricalLength` and
  `SurfaceMesher.UnknownCeiling` are byte-identical. R-emp-3 exists so M0 cannot widen R-via-6 by
  accident.
- **No published number moves**, except where M0's own gate measures and reports that it does.
  Every existing `Category=Benchmark` accuracy measurement in `src/Engine/Mom/CLAUDE.md` must still
  produce the number recorded there.
- **`.cem` round-trips byte-identically** for every file written before this brief.
- **The routine `Engine.Tests` tier is already over its ~60 s ceiling** (L9e recorded 992 in 65 s and
  why it is left visible). Every sweep, ladder and cost run added here is `Category=Benchmark`. The
  routine contribution should be the bit-identity assertions and the `.cem`/hash round trips, which
  are milliseconds.

---

## Gates

1. **M0 accuracy** — the table under M0's own gate, on two designs, reported. This is the deliverable
   of M0, not a pass/fail.
2. **M0 round trip** — a pre-brief `.cem` gains no byte; a set `MeshFrequencyHz` survives `Clone`
   (which drives undo snapshots and would silently lose it) and survives `Resolved`'s `Auto` collapse.
3. **M0 staleness** — `MeshHash` moves with `MeshFrequencyHz`, and every other mesh term still moves
   it.
4. **M0 report** — `PlanarMeshReport.FrequencyHz` is the mesh frequency, the λ_g note quotes it, and
   the under-resolution note fires below the sweep top and not at or above it.
5. **R-emp-7** — core count moves no provenance hash.
6. **R-emp-8** — the same matrix filled at cap 1 / 2 / unbounded is **bit-identical**, entry by entry.
7. **R-emp-13** — the same sweep at cap 1 and cap 8, adaptive on and off, produces bit-identical
   `DataSet`s.
8. **R-emp-11** — `PlanarKernelSet.FitCount` is still exactly one fit per pairing per frequency under
   concurrency, and the D7 counter gate still holds.
9. **M4 bit-identity** — cached moments equal freshly integrated ones to the last bit.
10. **The cost table**, taken **ALONE** (L8d's own standing warning: a benchmark sharing a run read
    more than twice as slow, and that number reached the design note before it was checked). On the
    §0 design, one de-embedded point and the full 51-point sweep, at: today; M0 at half the sweep top;
    M0 + M2; M0 + M2 + M3 at caps 1/2/4/8/10; and M4 if built. **Report wall clock and peak working
    set for each** — R-emp-12 is why the second column is not optional.
11. **M5 decision** — the GMRES iteration-count measurement of R-emp-15, reported, **before** any
    projection code exists.

---

## Suggested order

**M0 first, alone, and ship it.** It is the only milestone with no concurrency risk, it is the only
one a user can act on today, and its own gate is the accuracy measurement that tells everyone else
what the mesh is allowed to be. On the §0 design it is a 2–3× available immediately.

**Then M1 + M2 together** — the seam and the first thing worth capping, small and self-contained.

**Then M3**, which is where the large multiple is, and which is the first milestone that can break
determinism. Gates 6, 7 and 8 exist for it.

**Then M4**, whose measured redundancy decides whether it is worth its own complexity.

**M5's gate 11 can be run at any point and should be run early** — it is a day, it needs nothing
from the other milestones, and its answer decides whether a multi-week phase exists at all.

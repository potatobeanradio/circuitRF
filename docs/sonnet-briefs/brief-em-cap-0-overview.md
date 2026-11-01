# Brief series CAP — planar solver capacity: more unknowns on the same machine

**Goal.** The planar (MoM) dense path should solve **about 44,000 unknowns on a 16 GB machine**
after CAP-1…3, and CAP-4 decides whether it goes to **about 60,000**. Today it refuses above
**10,000** unless a memory limit is typed in, and even with one it stops near **27,000** on 16 GB.

**Why the number is low today — measured, not guessed.** One dense frequency point holds, per
matrix entry (`PlanarSystem.ResidentBytes`):

| Term | Bytes per entry (N² basis) | Note |
|---|---|---|
| The matrix, full square, complex double | 16 | P7's LDLᵀ reads and writes only the LOWER triangle; the upper half is dead weight from the moment the fill mirrors into it |
| Cached geometric cores (quoted figure) | ~6 | `PlanarSystem.CoreBytes` — a conservative a-priori quote; the production `Classes` layout actually allocates 3×–12× less on real artwork |
| Diagonal D, vectors | ~0 | 16·N |
| **Total** | **~22** | → 26,900 unknowns in 16 GB with nothing else running; ~23,300 with 12 GB free |

**The target per entry is 8 bytes** (complex double, lower triangle only, cores negligible), which
is 44,700 unknowns in 16 GB. CAP-4's experiment targets **4 bytes** (lower triangle in complex
single, double-precision answer recovered by refinement).

**Time grows as N³ and none of this changes that.** Measured 37 s to factor N = 11,210 (Release,
10 cores). Scaled: ~3.5 min at 20,000, ~9 min at 27,000, **~40 min per frequency point at 45,000**.
The adaptive sweep (`PlanarAdaptiveSweep`) is what makes a large run usable; every brief that raises
the ceiling must also make the per-point time visible before the run starts.

## The briefs, in order

| Brief | What | Answers change? | Capacity on 16 GB after it |
|---|---|---|---|
| `brief-em-cap-1-ceiling-from-machine-memory.md` | The default dense ceiling comes from the machine's memory, not the 10,000 constant | No | ~23,000 |
| `brief-em-cap-2-lower-triangle-storage.md` | Store only the lower triangle, in a block-column layout the factorisation already walks | No — bit-identical is the gate | ~33,000 |
| `brief-em-cap-3-cores-off-the-peak.md` | Make the cores' resident cost honest and small during the factorisation | No | ~44,000 |
| `brief-em-cap-4-mixed-precision-experiment.md` | EXPERIMENT: factor in complex single, refine to double | Within the hero tolerance, or it does not ship | ~60,000 |

CAP-2 and CAP-3 are independent of each other; both depend on CAP-1 only for the ceiling being
memory-derived (so their savings show up as capacity without a further constant to move). CAP-4
depends on CAP-2's storage type.

## Rules for the whole series

- **The accelerated (AIM) ceiling is NOT this series' subject.** `AcceleratedUnknownCeiling` is set
  by GMRES convergence, not memory (`SurfaceMesher.cs` records why). Leave it, and leave
  `UsesAcceleratedCeiling` alone.
- **No new timing tests** — measure with a scratch harness and record the numbers in
  `src/Engine/Mom/RESOLVED.md`; tests assert counts, bytes, and equality of answers.
- **Run only the targeted test classes** named in each brief, never the full suite.
- **No long EM runs.** A ladder that needs a point at 40,000 unknowns runs ONE frequency point, and
  only once the smaller rungs have passed.
- Findings go to `src/Engine/Mom/RESOLVED.md` (and `src/Design/RESOLVED.md` for CAP-1's run-service
  side), never `CLAUDE.md`. Update `src/Engine/Mom/CLAUDE.md` §8's shipped numbers only where a brief
  changed them.

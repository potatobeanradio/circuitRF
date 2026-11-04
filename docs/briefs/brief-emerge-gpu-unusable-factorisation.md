# Brief EMG-1 — a point sweep whose GPU factorisation is unusable keeps its answer

**Tag:** `R-emg1-n`
**Area:** `src/Design/Em3d/Solvers/EMerge/circuitrf_emerge.py`, `EmergeResults.cs`, `Em3dRunService.Emerge.cs`
**Depends on:** EM-TFR's fixture (`testdata/em-tfr/zigzag/thru`), for the reproduction

---

## The gap

EMerge's GPU sparse solver factorises in single precision and checks its own result. When the check fails it logs *"Matrix is so
singular that it is broken on single precision"* and EMerge solves with that factorisation anyway.

**The two sweep paths treat that line differently.** `circuitrf_emerge.py` runs an adaptive sweep when
`adaptiveSweep` is on and the sweep has more than `initialSamples` (5) frequencies, and a point sweep otherwise.

- **Adaptive:** `adaptive_sweep` watches for the line and solves the sweep again on the CPU, on the same mesh, at the first one
  (`GpuStalled`, `Heard.unusable`). The result is the CPU's, and a note says so.
- **Point:** `sim.mw.run_sweep()` is not watched. The driver counts the lines afterwards, adds a warning ("these S-parameters may be
  noise") and **writes the file**.

So a sweep of five frequencies or fewer publishes an answer the solver has said it does not trust, and the same mesh with a sixth
frequency does not. Nothing about the warning stops that file being used as a result, or as an SnP block in a schematic.

## What was measured

The metal-only 2×-thru of the EM-TFR fixture, Generated route, `Conductors: Sheets`, wave ports, `EMerge.EdgeRefinement` 0.05: 12,209
tetrahedra, 81,824 unknowns, which `device=auto` sends to the GPU (from 40,000).

| Frequencies | Device | Point | max \|ΔS\| against the CPU | \|S21 − S12\| |
|---|---|---|---|---|
| 1, 10, 40 GHz | auto | 1 GHz | **0.118** | 0.167 |
| | | 10 GHz | 4.5e-4 | 2.2e-4 |
| | | 40 GHz | 1.6e-7 | 5.0e-5 |
| 1, 10, 40 GHz | gpu | 1 GHz | **0.446** | 0.189 |
| | | 10 GHz | 4.0e-4 | 1.4e-4 |
| | | 40 GHz | 2.2e-7 | 5.0e-5 |
| 1, 2, 5, 10, 20, 40 GHz | auto | all six | ≤ 1.5e-10 | ≤ 4.5e-5 |

- Both three-point runs flagged one factorisation of three and warned. The six-point run flagged, moved to the CPU, and matches it.
- **`auto` and `gpu` take the same path on this mesh and read 0.118 and 0.446 at 1 GHz.** Two runs of one input differ.
- A six-square film between the same feeds, at 76,870 and 106,784 unknowns, read 464 − 256j and 202 − 134j Ω at 1 GHz where 300 Ω
  is right, with σ_max(S) of 1.11 and 1.66. At 343,742 unknowns the run flagged two of three.
- **Not established:** which frequency each flag belongs to (the driver counts them), and why low frequency is the bad end. The 10 GHz
  point is unflagged, if the count is what it appears to be, and is 4e-4 off where brief EMI-9 measured 1.5e-6 on the Cavity.

## What to build

### 1. Measure first: is the flag enough?

Everything below rests on the flag catching every bad point. Record per frequency, on the point path, whether its factorisation was
flagged and its |ΔS| against the CPU, on the reproduction, on the two film meshes above, and on the Cavity. Put the table in
`src/Design/RESOLVED.md`.

**Use the sweep's own tolerance as the line: 1e-3.** An unflagged point past it means the flag is not enough; see *Stop and report*.

### 2. A point sweep moves to the CPU at the first flag (`R-emg1-1`)

The same behaviour as `adaptive_sweep`, through one function rather than a second copy. Generalise it so that both paths are "run this
sweep on the GPU, watched; on a flag, restore the band, empty the dataset, clear `forced_solver` and run it again on the CPU", and
report `gpu-stalled` with `unusable: true`, which `EmergeStageTracker`, `EmergeResults` and the run's note already read.

**The whole sweep moves, not the flagged point.** One file comes from one device, and the point the flag belongs to is not known.

**Check that the raise reaches the driver on this path.** `GpuStalled` is raised from the log sink, and `guard_solves`' comment notes
that EMerge may solve serially or in threads. If a point sweep's workers swallow it, let the sweep finish, ask `heard.unusable`, and
re-run then. That costs the GPU pass, about 10 s on the reproduction, and is the same result.

### 3. An explicit `device=gpu` behaves the same (`R-emg1-2`)

The adaptive path does not distinguish `auto` from `gpu`, so neither does this. Asking for the GPU does not make its noise an answer,
and the note says the result is the CPU's and why.

### 4. Fields follow the sweep (`R-emg1-3`)

A saved field comes from a direct solve at that frequency, after the sweep. On a run still on the GPU that solve can flag too: the same
rule applies to it, so a field file is never written from a flagged factorisation. Check first whether this can happen; if the driver
already solves those on the sweep's device, it can.

### 5. If the CPU cannot do it (`R-emg1-4`)

The CPU's direct solve can need more memory than the GPU's, and EMI-10's ceiling can stop it. Then the run is **refused and writes no
Touchstone**, naming both facts. A refusal is better than publishing what the solver flagged. The warning "may be noise" then has no
remaining path to a written file, so remove it, and gate that.

### 6. Docs

`docs/user/src/reference/em-solvers.md`, where EMerge's Device is described: a sweep whose GPU factorisation is unusable is solved on
the CPU, whatever its length.

## Gates

One test per claim. No CI machine has this GPU solver, so what needs one is opt-in and skips with a reason when it is absent.

1. **The point path is guarded.** `AGpuSweepStoppedForAnUnusableFactorisation_IsReadBackWithItsReason` in `EmergeGpuTests` covers the
   read-back with `"adaptive": true`. Add the same for `"adaptive": false`: the note and the progress row, and no "may be noise" warning.
2. **The reproduction, live** (`Category=Benchmark`, skips without the GPU solver). The thru above at 1, 10 and 40 GHz on `auto`: the run
   reports `device: cpu`, carries the note, and its S equals a CPU run's to 1e-9 at all three points. This also catches state left by
   the interrupted sweep.
3. **A refusal writes nothing** (`R-emg1-4`). Asserted on the run service with a stubbed driver result, as `EmergeGpuTests` does.
4. **A clean GPU point sweep is untouched.** A mesh that flags nothing stays on the GPU, with no note.

```
dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~EmergeGpuTests"
```

Findings go in `src/Design/RESOLVED.md`.

## Stop and report if

- **An unflagged point is past 1e-3 against the CPU.** Then the flag is not sufficient, and the fix is a different one: a second check
  of the result, or a higher bar for the GPU. Every flagged-bad point measured so far is non-reciprocal by at least 0.13, against about
  5e-5 on a good one, so |S − Sᵀ| is the first candidate, but it is not to be added unless this measurement asks for it.
- A sweep re-run on the CPU in the same process differs from a fresh CPU run by more than 1e-9.
- The CPU re-run costs more than the point sweep would have alone by more than the GPU attempt itself.

## Not in this brief

- **Why single precision breaks at low frequency**, or conditioning the system. This brief stops publishing the result.
- **The 40,000-unknown threshold** for `auto`. If the measurement says low-frequency sweeps should start on the CPU, that is a finding
  to report, not a change to make here.
- **Palace's GPU path**, which is a different solver and a different check.

# Brief EMI-12 — CORVID, EMerge's iterative solver, as a remedy and a setting; and a memory check that matches EMerge 3.0.10

**Series:** EMI · **Tag:** `R-emi12-<n>` · **Depends on:** EMI-5 (run and results), EMI-10 (the memory ceiling), the EMerge 3.0.10 re-pin
(`src/Design/RESOLVED.md`, "EMerge 3.0.10") · **Area:** `src/Design/Em3d/Solvers/EMerge` (`EmergeMemory`, `EmergeSettings`,
`Em3dRunService.Emerge`, `EmergeStageTracker`, `circuitrf_emerge.py`), `src/Design/Em3d/Em3dMemory.cs` and the run's memory gate
(`Em3dRunService.MemoryGate`), the `.cem`/`.c3d` EMerge section, `explain`/`em --dry-run`, `docs/user/src/reference/em-setup.md` and
`em-solvers.md`, `src/Design/RESOLVED.md`.

## The gap

**An EMerge run that does not fit in memory has one remedy, and it changes the answer.** After meshing, `EmergeMemory.Verdict` prices the
run and offers first-order elements, which move a resonance by a few steps of a fine sweep. Palace's verdict also offers its iterative
linear solver, a remedy the run takes itself without changing the design (`Em3dMemoryRemedy.Iterative`, `MemoryGate.UseIterative`);
EMerge's has nothing of the kind.

EMerge has one now. Its CORVID solver (EMerge 3.0.5; its `corvid` extra since 3.0.10, which circuitRF's recipes install) factors only the
first-order (Whitney) part of the system, about a fifth of the unknowns, and solves the full second-order system iteratively on top of it.
Upstream reports, on four models of 0.15M to 1.25M unknowns, a whole-run peak 20 to 69 % lower than with SPARTA and S-parameters within 2e-7
of it; SPARTA is usually faster when a model fits. Since 3.0.5 it runs on models with thin conductor sheets and inside adaptive mesh
refinement, and `model.set_solver(em.solvers.Corvid)` switches the simulation to the iterative method by itself.

**And the memory check itself is out of date.** Its fast line (134 MB + 24.1 kB per unknown) was calibrated on EMerge 3.0.0b2's macOS sparse
solver (EMI-3). On 3.0.10 the CPU runs SPARTA, and 3.0.8 cut EMerge's own memory (32-bit indices in the system matrix's assembly, buffers
freed after use). Measured on an M4, whole process tree, SPARTA on the CPU:

| Problem | Unknowns | Measured peak | The check's estimate |
|---|---|---|---|
| 3D Eigenmode Cavity, driven | 54,090 | 1.22 GB | 1.44 GB |
| 3D Wave Ports Launch | 151,670 | 2.5 and 3.4 GB (two runs) | 3.79 GB |
| 3D Wave Ports Coupled Microstrip | 337,980 | 4.0 and 5.1 GB (two runs) | 8.28 GB |

An estimate that errs high is the safe one, but this one errs by up to 2×: on a 16 GB machine a run of about 450,000 unknowns is asked about
(the 75 % warning) though it would use well under half the memory, and the question offers a remedy that changes the answer. The two runs
of one problem differing by a third also says the measurement needs its method fixed before a model is fitted to it.

## Requirements

**R-emi12-1 — The memory model, measured on 3.0.10.** Re-measure the peak resident memory of the whole process tree (the driver and
everything it starts), the way `EmergeRun` measures a run, for each solver a run can use: SPARTA on the CPU, SPARTA on the GPU (an arm64 Mac;
does the GPU's memory count against the machine's on unified memory? say what was measured), CORVID, and SuperLU (an eigenmode run's, and
an install with no fast solver). At least three sizes per solver, from about 50,000 to the largest that fits on the measuring machine,
each run twice; explain the spread if it is more than 10 %. Fit each line (fixed + per unknown) so the estimate is at or above every
measurement and no more than 1.3× the largest. Record the table in `src/Design/RESOLVED.md` and in `EmergeMemory`'s comment, as EMI-3's is.
`EmergeSpeedTier` decides the line today; the solver the run will use is what should, so say how the check knows it before the solve.

**R-emi12-2 — CORVID as a remedy the run takes itself.** When the direct solve does not fit and CORVID would, the verdict offers it the way
Palace's offers its iterative solver: first, with its estimate, as a button in the panel's question and the choice `--force` takes on the CLI
(say which). The EMerge check happens after the driver has meshed, in the driver's own process, so the answer has to reach the running
driver before it solves. Decide how, and record why:
- the driver, started for a run that may ask, waits after reporting `meshed` for one line on its standard input (`solve`, `iterative`,
  `stop`): no second mesh, and a refusal still stops the process tree; or
- the run starts the driver again with the remedy as an argument: simpler, and it meshes twice.

A run that took the remedy says so in its Notes with the estimate it was started on, and run.json's `sparseSolver` says CORVID.
An eigenmode run is not offered it (EMerge's eigensolver does not use CORVID; check that this is still so on 3.0.10).

**R-emi12-3 — CORVID as a setting.** The EMerge section of a setup states its linear solver, direct (the default, unchanged) or iterative,
as Palace's `LinearSolver` does: read and written in the `.cem` and the `.c3d`'s embedded setups, in the setup editor beside the others, in
`explain` and `em --dry-run` (which price the iterative solve too), and in the provenance only if it can move an answer (CORVID's tolerance
does: say what EMerge's default is and whether circuitRF states one). The driver selects it with `set_solver(em.solvers.Corvid)` and the
thread count with the run's Cores, as SPARTA's.

**R-emi12-4 — What CORVID costs and gives, measured.** On the Launch (wave ports), the Coupled Microstrip (a terminal group), a model with
thin lossy sheets (EM3D-139's line as a sheet) and the largest model that fits SPARTA on the measuring machine: solve time, peak memory
and |ΔS| against SPARTA on the same mesh, for a point sweep and an adaptive one. If CORVID's iterations fail to converge on any of them,
say on which and what EMerge reports, and make that a refusal naming the direct solver, not a result.

**R-emi12-5 — Docs.** `em-setup.md` (the setting, with the measured trade), `em-solvers.md` (EMerge's memory, the remedy), the memory
check's own sentences.

## Gates

1. The memory model holds every R-emi12-1 measurement: estimate ≥ measured and ≤ 1.3× the largest, per solver, as a routine test on the
   recorded numbers (no timing test, no new benchmark: the numbers are data).
2. A run whose direct solve does not fit and whose iterative solve does is offered CORVID, and a run started on it reports CORVID in
   run.json and its Notes (a routine test on a canned mesh report, as EMI-10's are).
3. CORVID against SPARTA on the Launch and the Coupled Microstrip: max |ΔS| ≤ 1e-6 (Category=Benchmark, skipped without EMerge).
4. The setting round-trips through the `.cem` and the `.c3d`, and `explain` prices both solvers.

## Out of scope

CORVID on Palace or openEMS (they have their own iterative solvers), the GPU for CORVID, and choosing CORVID automatically without asking.

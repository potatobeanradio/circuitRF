# Brief EMI-5 — Running EMerge: the process, progress, cancellation, and results as a `DataSet`

**Tag:** `R-emi5-n` · **Series:** EMI (see `brief-emi-0-overview.md`) · **Depends on:** EMI-2, EMI-4
**Area:** the EMerge descriptor's `Execute`, new `src/Design/Em3d/Solvers/EMerge/EMergeRun.cs` and its log/result
readers, `Em3dRunService` (through the seam only), `C3dSolveStatus`, `src/Cli/CliEntry.cs` (`em --solver emerge`),
the MCP `run` tool, the 3D editor's Simulate. Findings in `src/Design/RESOLVED.md`.

---

## 1. `R-emi5-1` — the process

- **Command:** `<env python> -I <runDir>/driver.py <runDir>/problem.json` (EMI-4's constant driver and per-run data),
  working directory `<runDir>`, started with an argument list, never a shell, through the same process helper Palace
  uses.
- **Environment:** cleared of `PYTHON*`, `VIRTUAL_ENV`; thread variables from EMI-3 R-emi3-5; a non-interactive
  plotting backend and off-screen 3D so nothing can open a window (the driver never calls EMerge's viewers either).
  EMerge's update check is opt-in and off by default; the driver does not turn it on, and **nothing in a run reaches a
  network**.
- **Files:** EMerge writes nothing by default unless asked to save; the driver writes only into `<runDir>`. A run
  directory holds `driver.py`, `problem.json`, `emerge.log` (stdout+stderr), and the result files below.

## 2. `R-emi5-2` — progress and cancellation

- **Stages** from EMerge's log lines, parsed by a reader written against the pinned version's actual output (record
  the lines in `RESOLVED.md`): meshing (Gmsh's own stage lines, as `GmshLogProgress` reads them), assembly, each
  adaptive-sweep iteration ("N samples, estimated error E (tol T)" is a natural progress line: show the error against
  the tolerance), each solve, post-processing. The tetrahedron count is in the log; carry it into the summary.
- **Cancellation kills the process tree.** EMerge can start worker processes (frequency-parallel solves); killing only
  the parent leaves them running. Use the same tree-kill Palace's MPI launch needs and test it with a fake child that
  spawns a grandchild.
- A cancelled run writes no result (the existing rule).

## 3. `R-emi5-3` — memory admission

Estimate unknowns before the run from the mesh (EMerge's own guide: ~0.2 MB per unknown, falling to ~0.17 MB on
large problems) and gate it through the existing `MemoryGate` exactly as Palace's estimate is. Calibrate the constant
on R-emi3-1's measurements and say where it came from in a comment.

## 4. `R-emi5-4` — results

**R-emi5-4a — the driver writes plain arrays, circuitRF does the rest.** For Driven: `freq.npy` (the setup's own
grid — with the adaptive sweep, the rational model is evaluated on exactly that grid), `S.npy` (complex, `[f, i, j]`),
`Z0.npy` (each port's reference) and `run.json` (EMerge version, the sparse solver it used, tets, unknowns, samples
solved, adaptive error reached, timings). circuitRF builds the `S` `DataSet`, the Touchstone with the run's provenance,
and the `.npy`, through the same writers Palace's run uses — so the files land where `EmRunService.ResolveSnpPath`
says, with the `emerge` token. **EMerge's own Touchstone writer is not used**: one owner for port normalisation and
provenance.

**R-emi5-4b — Eigenmode:** `modes.npy` (complex frequencies) and per-mode Q. Filter what is not a mode before
reporting: EMerge returns null-space solutions near 0 Hz (the probe saw three at ~0.05 GHz with Q ≈ 0.2); drop
eigenvalues below the setup's search start by a margin, and **say how many were dropped** in a Note. Report the table
Palace's eigenmode run reports (f, Q), and leave "Q unloaded" and "energy participation" **absent**, not zero, when
EMerge does not provide them (compute them in EMI-7 from fields if it is cheap; otherwise the docs say Palace reports
them and EMerge does not).

**R-emi5-4c — the summary line** a user reads first, like Palace's `CompletionSummary`: solver and version, sparse solver
and tier, tets, samples solved for the band, wall time, peak memory.

## 5. `R-emi5-5` — the surfaces

- **GUI:** an EMerge setup's Simulate runs it; the 3D editor's solve status (`C3dSolveStatus`) knows the third solver.
- **CLI:** `em x.c3d --setup S --solver emerge`; `--solver` takes any registry token (EMI-1).
- **MCP:** `run analysis=em solver=emerge`.
- The CLI-vs-GUI byte-identity gate (`EmCliVerbTests`' pattern) extended to an EMerge run — **Skipped when EMerge is
  not installed**.

## 6. Tests (targeted)

1. Log reader: canned EMerge log → stages, adaptive iterations, tets (no process).
2. Result reader: canned `freq/S/Z0.npy` + `run.json` → the `DataSet` and Touchstone, byte-compared with a golden.
3. Eigen filter: canned complex frequencies with near-zero entries → the dropped count and the kept table.
4. Tree-kill with a fake process pair.
5. Memory estimate as a pure function of unknowns.
6. With EMerge installed only (Skipped otherwise): the Cavity *Driven* on EMerge lands its notch at 9.955 GHz on the
   5 MHz grid; CLI and GUI write the same bytes.

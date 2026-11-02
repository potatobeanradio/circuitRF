# Brief 21 — a Palace run you can watch

**Series:** [3D EM, second series](brief-em3d-20-overview.md) · **Tag:** `R-em3d21-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §5.3, §12 (problem size on a laptop);
[`em-3d-f0-findings.md`](../design/em-3d-f0-findings.md) §4
**Area:** `src/Design/Em3d/PalaceRun.cs`, `src/Design/Em3d/PalaceLogProgress.cs` (new),
`src/Design/Em3d/Em3dRunService.cs`, `src/Engine/Em3d/PhysicalCores.cs` (new),
`src/Engine/Em3d/Em3dSizeEstimate.cs`, `src/Design/Layout/Em/EmSetupPersistence.cs` (`CemPalace`),
`src/Ui/Layout/Em/EmSetupEditorViewModel.Palace.cs`, `src/Cli/CliEntry.cs` (`RunEm`)
**Depends on:** — · **Blocks:** 22, 23 (they reuse the progress parser)

---

## 0. What this brief delivers

Today a Palace run shows a bar that ticks once per line of solver output. That number means nothing,
and a two-minute run looks exactly like a hung one. After this brief:

```
Meshing (Gmsh)                                    model.msh: 146,769 tetrahedra
Solving: refinement pass 1 of 3                   953,946 unknowns
Sweep: sampling   error 7.5e-3 → tolerance 1e-4   ▓▓▓▓▓▓░░░  (log scale)
Sweep: evaluating 137 of 200 frequencies
Reading results
Done in 2 min 27 s · peak memory 7.4 GB · 12 frequency samples · 0 refinement passes
```

Every figure on those lines is something Palace printed.

---

## 1. `R-em3d21-1` — progress from Palace's own log

**`R-em3d21-1a` A pure parser, one line in, zero or one event out** (`PalaceLogProgress`). It takes no
process, no file and no clock, so it tests against committed logs with nothing installed. It
recognises:
- the mesh summary (the `elements` row);
- the unknown count (`ND (p = n): N`);
- `Adaptive mesh refinement (AMR) iteration k:`;
- `Greedy iteration k (n = m): … error = e`;
- `Adaptive sampling converged with n frequency samples`;
- `It k/N: ω/2π = f GHz`;
- the closing `Elapsed Time Report` and `Peak Memory` tables.

Anchor each pattern on the wording in F0's committed logs (`testdata/em3d/f0/*/palace*/palace.log`,
Palace 0.18.1). Those logs are the fixtures.

**`R-em3d21-1b` Solve passes are numbered as Palace runs them.** A setup with
`AdaptiveMaxIterations = N` makes up to **N + 1** solves. The first solve is on the initial mesh, and
the log's `AMR iteration k` line appears *after* solve k. The stage reads *pass k of N + 1*, and a run
that converges early says so ("refinement converged after pass 2"). Do not invent a total Palace did
not state.

**`R-em3d21-1c` The sampling phase has no known total, so it shows convergence, not a percentage.**
Its fraction is `log(e₁/e) / log(e₁/tol)`: the greedy error against the setup's `SweepAdaptiveTol`, on
a log scale, clamped to [0, 1]. It is labelled as convergence and never as a percentage done. The
online phase (`It k/N`) does have a total, and shows one.

**`R-em3d21-1d` Unknown wording degrades rather than lies.** If a run's log matches none of the
patterns within the first mesh-summary window, or a pattern stops matching mid-run, progress falls
back to **indeterminate** with the line count, and a note says the log format was not recognised. It
never shows a wrong fraction. That is the path a new Palace version takes before it is validated.

**`R-em3d21-1e` Gmsh's stage** reports Gmsh's own `Info : Meshing 3D...` and `Info : Done meshing 3D`
lines and the final element count. A re-run that reuses the mesh (series 1 brief 7 gate 5) says
**"mesh reused"** instead of showing a meshing stage.

**`R-em3d21-1f`** The events drive `RunControl.Stage` and the stage fraction. The CLI prints stage
changes to **stderr** only, because stdout is the result (cli.md). `--json` output is unchanged.

---

## 2. `R-em3d21-2` — before the run: will it fit?

**`R-em3d21-2a`** Before Gmsh starts, the run service compares `Em3dSizeEstimate`'s Palace memory
figure against the machine's physical memory. It reads that with `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`,
or the OS's own figure if that one is capped by a container limit; state which in a comment.

**`R-em3d21-2b` A warning, never a refusal.** The estimate errs high by construction (it uses the
highest per-unknown figure F0 measured), so refusing on it would refuse runs that fit.
- **Above 75 % of memory:** a warning naming the estimate, the machine's memory, and the remedies in
  order of effect: the `Draft` preset, fewer refinement passes, a smaller air box. Each remedy comes
  with the estimate it would give.
- **Above 150 %:** the warning says the run will very likely swap or be killed, and the panel asks for
  confirmation before starting. The CLI needs `--force`.

**`R-em3d21-2c` Watch the real figure while it runs.** Sample the process tree's resident memory once
a second, and show it in the stage line ("7.1 GB in use"). If the tree reaches 90 % of physical
memory, add a one-time note naming it. Do not kill the run: the user decides.

---

## 3. `R-em3d21-3` — MPI ranks: physical cores (D2)

**`R-em3d21-3a`** The default rank count becomes the **physical** core count (`PhysicalCores.Count`).
Today it is `Environment.ProcessorCount`, which is logical. Open MPI's default slot count is physical
cores, so on an x86 machine with SMT, a "use every core" run is refused for lack of slots (series 1
review). Read the physical count per OS:
- macOS: `sysctl hw.physicalcpu`;
- Linux: unique `(physical id, core id)` pairs in `/proc/cpuinfo`, or `/sys/devices/system/cpu/*/topology`;
- Windows: `GetLogicalProcessorInformationEx` with `RelationProcessorCore`.

If the count cannot be read, fall back to `ProcessorCount / 2`, with a minimum of 1, and say so in the
run's notes.

**`R-em3d21-3b`** An explicit `EmSolveCores` still wins. If it asks for more than the physical count,
the run is **refused, naming the physical count**. It does not pass `--oversubscribe`, because
oversubscribed MPI ranks on a laptop are slower, not faster.

**`R-em3d21-3c`** Apple Silicon has no SMT, so on this machine physical equals logical and the change
is invisible here. The gate is therefore a unit test on each OS's parser against a captured sample
output, plus a live check on the machine running the tests.

---

## 4. `R-em3d21-4` — quality presets, measured

**`R-em3d21-4a`** `CemPalace.Quality` is `Draft | Standard | Accurate`. It is nullable, and null means
`Standard`. **`Standard` resolves to exactly today's `PalaceSettings.Default`**, so no golden moves. An
explicit field (`ElementOrder`, `AdaptiveMaxIterations`, …) overrides the preset's value for that
field. `PalaceSettings.Resolve` applies the preset first, then the fields.

**`R-em3d21-4b` Starting values**, to be confirmed or changed by the measurement in 4c:
- `Draft`: element order 1, no refinement, sweep tolerance 1e-3.
- `Accurate`: order 2, up to 3 refinement passes at tolerance 0.005, sweep tolerance 1e-5.

**`R-em3d21-4c` Measure once, commit the table.** Run F0 case A (the hexagon with foot) and case B
through circuitRF at each preset. For each run record:
- wall time;
- Palace's own peak memory;
- the maximum |ΔS21| (dB) and ∠ΔS21 (°) against `Accurate` over the band.

This machine takes about 3 × 2 × 2–7 min. Run it once, alone, per the standing short-runs practice,
and log the commands. The table goes in:
- `src/Design/RESOLVED.md`;
- the `.cem` reference page (the generated description of `Quality`);
- the panel's tooltip for the preset picker.

The panel prints what the user trades: "Draft: about 10× faster on the F0 wire; |S21| within 0.x dB
of Accurate". **If Draft's deviation on either case is worse than 0.5 dB or 5°, change Draft's values
and measure again** rather than shipping a preset that misleads.

---

## 5. `R-em3d21-5` — the completion summary

**`R-em3d21-5a`** On success, one summary line goes to the Messages panel and to CLI stderr: wall time,
Palace's own peak memory (the `Total HWM` from the `Peak Memory` table), initial and final
tetrahedra, unknowns, refinement passes, sweep samples, and the preset. They come from the parser,
not from a second read of the log.

**`R-em3d21-5b`** The same fields extend the `.sNp` provenance header series 1 brief 7 R-em3d7-5d
defined. Add fields only; do not rename any.

---

## 6. `R-em3d21-6` — two small behaviours a demonstration trips over

**`R-em3d21-6a` `--solver` on a planar `.cem` is refused** (series 1 review leftover). The refusal
names `Solver3D` as the field to set. It used to convert the run silently.

**`R-em3d21-6b` Stop on a 3D run.** Palace cannot finish early and keep what it has. For a Palace run,
the panel's Stop is labelled **Cancel** and does what Cancel does. It never shows a Stop that behaves
like Cancel.

---

## 7. Gate

`tests/Ui.Tests/Em3d/PalaceProgressTests.cs`, plus `tests/Engine.Tests/Em3d/PhysicalCoresTests.cs`.
Nothing here needs Palace except gate 8.

1. **Parser on F0's logs.** For `A palace-round-amr` (2 refinement passes) and `B palace` (200-point
   online phase), the parser's event sequence equals a committed expected sequence: stage names,
   pass numbers, greedy errors and the final summary fields.
2. **Pass numbering.** `AdaptiveMaxIterations = 2` produces "pass k of 3". A log that converges after
   pass 2 reports convergence and never shows "3 of 3".
3. **Convergence fraction.** A synthetic greedy sequence gives the log-scale fraction and is clamped
   at 1.
4. **Unknown wording.** A log whose lines were reworded falls back to indeterminate with the note.
   Zero fractions are emitted.
5. **Memory warning.** With physical memory injected as 16 GB, F0 case A's estimate crosses 75 % and
   the warning names the `Draft` estimate. With 64 GB, no warning.
6. **Physical cores.** Each OS parser, on its captured sample, gives the known count. On the test
   machine the count equals the OS's own figure.
7. **Presets.** Null and `Standard` resolve to `PalaceSettings.Default`, field for field. An explicit
   `ElementOrder` beats `Draft`'s. Every existing Palace golden is byte-identical.
8. **Live stages.** *(Palace.)* A real run of the smallest existing gate case emits, in order: meshing,
   pass 1, sampling, evaluating, reading. Gate this as a counter of stage changes, not on timing.
9. **CLI split.** `em` prints stages on stderr, and its stdout is byte-identical to before.
10. **`--solver` on planar** is refused with exit 1, and nothing runs.

## 8. Owner check (pixels not seen from this session)

- The stage line and the convergence bar during a real run: does it read as progress?
- The preset picker's tooltip and the memory warning's wording.

## 9. Scope

- No new problem types (briefs 22, 23).
- No change to what Palace is asked to solve at `Standard`.

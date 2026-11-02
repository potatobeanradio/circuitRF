# Brief EMI-10 — A memory ceiling and a free-disk floor for every solver process

**Tag:** `R-emi10-n` · **Series:** EMI (see `brief-emi-0-overview.md`) · **Depends on:** EMI-5 (the EMerge run), nothing
else · **Origin:** owner request, 2026-10-22, after an EMerge run filled the disk through swap and crashed the machine
**Area:** `src/Design/Em3d/Em3dMemory.cs` (`ProcessTreeMemory`, `Em3dMemoryVerdict`), `src/Design/Em3d/PalaceRun.cs`
(`RunProcess`, `ProcessWatch`), `src/Design/Em3d/OpenEmsRun.cs` (its own process loop), `Em3dRunService` (`RunContext`,
`MemoryGate`), Settings ▸ Solvers, `circuitrf em`, the MCP `run` tool, `explain`. Findings in `src/Design/RESOLVED.md`.

---

## 0. What exists today, and why it did not stop the crash

| Guard | Where | What it does | Gap |
|---|---|---|---|
| Size check before the solve | `MemoryGate.Admit` with each solver's `Em3dMemoryVerdict` | warns past 75 % of physical memory, asks (GUI) or needs `--force` (CLI) past 150 % | an **estimate**, taken once. EMerge's is taken when the driver reports its mesh, so **meshing itself is unguarded**; an estimate that is low is never corrected |
| Sampling during the run | `PalaceRun.RunProcess` → `ProcessTreeMemory.ResidentBytes`, once a second | a stage detail and, at 90 % of physical memory, a **note**: "it was not stopped" | **never stops anything**; polls once a second |
| openEMS | `OpenEmsRun` has its own process loop | none | **no sampling at all** |
| Windows | `ProcessTreeMemory.ResidentBytes` returns null | none | **no sampling at all** — and EMerge runs natively on Windows |

What happened (2026-10-21): in an EMerge driver run, Gmsh's curvature sizing on an imported part's trimmed curve asked for
elements of essentially zero length. The 1D mesh grew by **about 8 GB in under half a second**, macOS swapped, the swap files
filled the startup disk, and the machine crashed. The cause was fixed in the driver (`Mesh.MeshSizeMin`, EMI-4), but nothing
would stop the next runaway, from any solver.

Three facts decide the design:

1. **The sample measures resident pages only** (`ps -o rss` on macOS, `/proc/<pid>/statm` on Linux). Once a process swaps, its
   resident size stops growing while its real footprint keeps growing, so **a ceiling on resident size near physical memory
   would never trip** — exactly when it is needed. The measure must count compressed and swapped pages.
2. **A once-a-second poll is far too slow** for a runaway of gigabytes per second, and spawning `ps` ten times a second is
   too costly. The measure must be cheap enough to read every 100 ms.
3. **The machine died of a full disk, not of a full memory.** On macOS and Windows swap grows on the system volume until the
   disk is full. A free-disk floor is the guard against that exact failure, whatever the memory figure says.

## 1. `R-emi10-1` — measure the footprint, cheaply, on every platform

Replace `ProcessTreeMemory.ResidentBytes` with a **footprint** for the whole tree, readable every 100 ms:

- **macOS** — each process's physical footprint, which includes compressed and swapped pages: `proc_pid_rusage` (flavor
  `RUSAGE_INFO_V2` or later, field `ri_phys_footprint`) from the system's `libproc`, and the tree from `proc_listchildpids`
  (or one `ps -A -o pid=,ppid=` per second for the tree, the footprints read every 100 ms by pid).
- **Linux** — `/proc/<pid>/status`: `VmRSS` + `VmSwap` per process; the tree as today.
- **Windows** — each process's commit charge (`PROCESS_MEMORY_COUNTERS_EX.PrivateUsage`), the tree from a **Job Object** the
  solver is started into (`Em3dProcessLauncher`), which also gives the tree's total in one call
  (`JOBOBJECT_EXTENDED_LIMIT_INFORMATION.PeakJobMemoryUsed` / `JobObjectBasicAccountingInformation`).

**Owner decision O1** — these are calls into the operating system's own libraries by P/Invoke (`libproc`, `kernel32`,
`psapi`), not a shipped native binary, so they do not add a native dependency in the sense the root `CLAUDE.md` asks about.
*Recommended:* proceed; confirm before starting.

Keep the resident figure for the stage detail and the completion line (`peak memory …`), which users already read, and add
the footprint beside it only where the two differ by more than 10 %.

**Gate:** `ProcessTreeMemory` parses each platform's raw form from canned text/structures (a canned `/proc/<pid>/status`
with `VmSwap`), and on the machine running the tests, a child that allocates and TOUCHES 600 MB reads ≥ 550 MB footprint.

## 2. `R-emi10-2` — the ceiling, enforced in the one process loop

**One loop.** `OpenEmsRun`'s own process loop moves onto `PalaceRun.RunProcess` (it keeps its decay check and its abort file
through the `ProcessWatch` line callback and a new per-tick callback). After this every solver and mesher process circuitRF
starts — Gmsh for Palace, Palace, openEMS, EMerge, and their probes — runs through one loop with one ceiling. In-process
solvers (the planar kernel, the thermal solver) are out of scope: circuitRF cannot stop its own process, and they have their
own estimates.

**The ceiling.** `ProcessWatch` gains `CeilingBytes` (null = none). The loop reads the tree's footprint **every 100 ms**
(not the current 1 s) and, at or above the ceiling, **kills the whole tree** (the existing `Kill(p)`, `entireProcessTree:
true`) and returns a new outcome, `ProcessRun.StoppedForMemory` (the footprint reached, the ceiling, and the stage it was in).
A run stopped this way writes no result, as a cancelled one does.

- **Default: this machine's physical memory** (`MachineMemory.PhysicalBytes`; the Linux subsystem's for a WSL run, through
  the existing `Em3dMemoryScope`). Past it the operating system is swapping, and the run is either about to be very slow or
  about to take the machine with it.
- **A run the user forced past the size check** (§0's 150 % question, or `--force`) **keeps no memory ceiling**, because the
  user chose swap knowingly — but it keeps the disk floor (§3). *Owner decision O2; recommended as written.*
- **A WSL run** is already bounded by the subsystem's own memory limit (`.wslconfig`), which the subsystem enforces; the loop
  does not sample `wsl.exe` today (`sampleMemory: false`) and keeps it that way. Say so in the docs.
- **On Windows the Job Object enforces the ceiling itself** (`JOB_OBJECT_LIMIT_JOB_MEMORY`): the kernel refuses the
  allocation and the process fails, and circuitRF reports it as stopped for memory from the job's own notification, so a
  burst between two polls cannot overshoot. On macOS and Linux the 100 ms poll is the guard; at 8 GB per 0.5 s it can overshoot
  by ~1.6 GB, which is why the default sits at physical memory and not above it.

**The sentence.** One per solver, built from what the run knows, e.g.: *"EMerge was stopped while meshing: it reached 15.3 GB,
this machine's memory limit for a solver (16 GB). Nothing was written. A mesh this size usually means the mesher ran away on
one curve; check the run's log for the object it was meshing, or coarsen the mesh: …"* — the remedies are the solver's own
`Em3dMemoryRemedy` list from its verdict where it has one. While **meshing**, the sentence says a runaway is the likely cause;
while **solving**, it says the problem is larger than this machine and names the remedies.

**Status.** `EmRunStatus.Refused` with a new diagnostic source, `memory-ceiling`, so the CLI exits 1 (not 2: nothing in the
solver failed) and the Messages panel shows it as the run's own refusal. The 90 % note stays, now worded as a warning that the
ceiling is near.

**Gate (counters, never timings):** with an injected footprint reader (a seam on `ProcessWatch`), a stand-in process
(`NonWindowsFact`, the tree-kill test's shell-script pattern) is killed within one poll of a reading at the ceiling, its
grandchild included, and the run returns `StoppedForMemory` with the reading; a forced run with a reading above the ceiling
is not stopped. One real test: a child that touches memory past a 300 MB ceiling is stopped, and the result says so.

## 3. `R-emi10-3` — the free-disk floor

At the same 100 ms tick (disk reads every second are enough), the loop checks free space on:

- **the volume swap grows on** — macOS: the startup volume (`/System/Volumes/VM` shares its container); Windows: the system
  drive (the page file's); Linux: none (swap is a fixed partition or file, so a full disk is not how Linux fails — its OOM
  killer is), and
- **the run directory's volume**, because a solver writing field files (Palace's measured 137 MB per frequency per port) can
  fill a disk too.

Below the floor **the tree is stopped, whatever the memory figure**, with its own sentence naming the volume, its free space
and the floor. **Default floor: 10 GB**, or 5 % of the volume if that is smaller (a small disk). *Owner decision O3 —
recommended as written.* The floor applies to forced runs too; it is the guard against the failure that happened.

Before the run starts, a volume already below the floor refuses the run with the same sentence, so nothing is started that
would be stopped at once.

**Gate:** with an injected free-space reader, the tree is stopped when either volume reads below the floor, and the refusal
names that volume; a run starting below it is refused before the process starts (a counter of processes started stays 0).

## 4. `R-emi10-4` — the user's own limit (optional, small)

A per-MACHINE preference, never in the `.cem`/`.c3d` (R-emp-6's rule, as the core cap): **Settings ▸ Solvers ▸ Memory limit**
— *Automatic (this machine's 16 GB)* or a number of GB below physical memory. It lowers the ceiling of §2; it cannot raise it
past physical memory (raising past it is what forcing a run does). The disk floor is not a setting.

As the core cap does, the run takes it as an ARGUMENT (`RunContext.MemoryCeilingBytes`), filled by the GUI from
`AppPreferences` (`em_memory_limit_gb`) and by the CLI from **`circuitrf em --memory-limit <GB>`**; a headless run with no
flag uses the default. The MCP `run` tool takes `memoryLimitGB`. `explain` on a 3D setup reports the ceiling and the floor
that a run would use, and where each came from.

**Gate:** the preference round-trips through `AppPreferencesIo` (the existing test seam, never the real file); the CLI flag
reaches `RunContext`; a limit above physical memory is refused by the panel and by the flag with the same sentence.

## 5. Docs

`docs/user/src/reference/em-setup.md`'s memory paragraph (the 75 %/150 % check) gains what happens DURING a run: the ceiling,
the floor, what forcing a run changes, the WSL case. `docs/user/src/reference/settings.md` gets the Memory limit row;
`docs/user/src/reference/cli.md` and `docs/design/cli.md` the flag. No DocGen (the owner's, at the end of the series).

## 6. Not in this brief

- Lowering a solver's memory (that is each solver's own settings and remedies).
- A memory estimate for EMerge before meshing — `explain`'s EMerge size row is deliberately unavailable (EMI-4: the shell
  estimate read 6–15× high). The ceiling is the guard instead.
- Stopping an in-process solver.

## 7. Standing rules

The series' (overview §6): no vendor or EDA tool names; findings in `src/Design/RESOLVED.md`, never a `CLAUDE.md`; tests one
per claim, targeted classes only, no timing assertions, **no test may need a solver installed to pass**; never quote the owner.
Any manual check that runs a real solver runs it under the scratch watchdog (poll 0.1 s, stop at ~3–4 GB or < 10 GB free) until
this brief's ceiling exists.

# Brief 136 — Palace on a GPU: a CPU/GPU device setting for a user's own GPU build

**Tag:** `R-em3d136-n`
**Status:** Written 2026-10-15. D1 decided (per machine). Implemented 2026-10-15 (R-em3d136-1 to 5b); the field test (R-em3d136-5c) is pending.
**Area:** `src/Design/Em3d/PalaceConfigWriter.cs` (the two `Solver` blocks), `src/Design/Em3d/Em3dRunService.cs`
(the process count), `src/Design/Em3d/SolverDiscovery.cs` (the capability probe), `src/Design/Em3d/PalaceRun.cs` and
`Wsl/WslPalaceRunner.cs` (the launch), `src/Ui/Theming/AppPreferences.cs` + `src/Ui/Layout/Em/` (the setting),
`src/Cli/CliEntry.cs` (`em`), `src/Cli/Solver.cs`, `src/Cli/ExplainEm3d.cs`, the MCP `run`/`solver` tools,
`docs/user/src/reference/em-setup.md`. Findings in `src/Design/RESOLVED.md`.
**Depends on:** nothing.
**Out of scope:** an install recipe for a GPU build of Palace (§6).

---

## 0. Why

A user asked on GitHub (discussion #10) whether Palace's GPU and multi-GPU acceleration can be chosen from circuitRF
when setting up a 3D FEM run. Today it cannot:

- **`PalaceConfigWriter` always writes `"Device": "CPU"`**, in both `Solver` blocks: the driven/eigenmode one
  (`PalaceConfigWriter.cs:332`) and the electrostatic/magnetostatic one (`:588`). Nothing reads a setting.
- **The process count is one MPI rank per physical core** (`Em3dRunService.cs:714`, `maxCores ?? cores.Count`), which is
  the wrong count for a GPU run.
- **The Palace circuitRF installs is a CPU build.** Every recipe builds `+superlu-dist+sundials+slepc+libxsmm+gslib`
  with no GPU variant. A user with their own GPU build can already select it in Settings ▸ Solvers, but they still
  get `"CPU"`.

This brief serves that user: someone who **brings their own GPU build of Palace**. A reply on the discussion said this
option is planned and asked the user to test it, since no machine here has a GPU Palace supports (the owner's Mac
cannot run Palace's GPU backends and the owner's Windows is an ARM64 VM). **The gates in this brief are therefore
everything short of a real GPU run**: the JSON written, the `-np` chosen, the refusals and the probe's parse. The real
run is the field tester's (§5).

### What Palace 0.18.1 does (read from its source at tag `v0.18.1`, 2026-10-15)

Each fact below decides part of this brief:

| Fact | Where | Consequence |
|---|---|---|
| `Solver.Device` is `"CPU"` (default), `"GPU"` or `"Debug"`. GPU support is a **compile-time** build option. | `palace/utils/configfile.cpp:1470`, `docs/src/guide/parallelism.md` | A run setting alone is not enough: the build must have it. |
| **A CPU-only build given `"GPU"` does not fail.** It prints a warning ending in `reverting to CPU!` and runs on the CPU. | `palace/main.cpp:51-68` (`ConfigureDevice`) | **This is the trap.** Without a check, the user believes a run used the GPU when it did not. R-em3d136-3 refuses before the run; R-em3d136-5 checks after. |
| A GPU build prints `Detected N <backend> device(s)` in its startup banner. A CPU build prints no such line. | `main.cpp:134-143` (`PrintPalaceInfo`) | **One line answers both questions**: does the build support GPUs, and how many does it see. |
| The banner then prints MFEM's `Device configuration: …` line. | `main.cpp:144-148` | After a run, this says what was actually used. |
| The banner comes **after** the `--dry-run` exit. | `main.cpp:206-223` | A dry run cannot answer. The probe has to be a solve. |
| MPI ranks are assigned to GPUs round-robin per node (`local rank % ngpu`). | `palace/utils/device.cpp:21-34` | One rank per GPU is the natural count. More ranks than GPUs puts two ranks on one GPU. |
| Partial assembly is used when the element order is **not below** `PartialAssemblyOrder`, default **1**. | `configfile.hpp:1158`, `fem/bilinearform.cpp:118-131` | **Already on at every order circuitRF writes (1 and 2).** Palace's GPU guidance asks for partial assembly, and its default already gives it. **Write no `PartialAssemblyOrder` key** (R-em3d136-1c). |
| `Solver.Backend` (libCEED) defaults to the right backend for the device. | `parallelism.md` | Write no `Backend` key. |

---

## 1. `R-em3d136-1` — the setting, and the JSON it writes

**R-em3d136-1a — per MACHINE, not per design.** The device is an `AppPreferences` entry (`em_palace_device`, values
`cpu`/`gpu`, absent = `cpu`). It is **not** a `.cem`/`.c3d` key. The reasoning is `EmMaxCores`', already written in
`AppPreferences.cs:288-296`: a `.cem` travels with the workspace, so opening a colleague's setup must not switch your
run to a GPU you do not have, or your GPU off. Like the core cap, it **enters no provenance hash**: the answer is the
same problem solved on different hardware.

> **Owner decision D1 (2026-10-15): per machine, exactly like Cores.** The first plan put the device in the
> `.cem`/`.c3d` Palace settings. That was rejected for the reason above.

**R-em3d136-1b — carried to the run as an argument, like `maxCores`.** `Em3dRunService.Run` and its callers gain
`PalaceDevice device = PalaceDevice.Cpu` beside `int? maxCores` (`EmRunService.Run`/`RunThreeDView` →
`Em3dRunService.Run`). Do not put it in `PalaceSettings`: that record is what a `.cem` resolves to, and its defaults
"live here and nowhere else" (`EmSetupPersistence.cs:300-303`). New enum `PalaceDevice { Cpu, Gpu }` in
`src/Design/Em3d`.

**R-em3d136-1c — the writer.** `PalaceConfigWriter.Write` takes the device and writes `"Device": "GPU"` or `"CPU"` in
**both** `Solver` blocks. Grep for `"Device"` and confirm there are exactly two. Write **no** `PartialAssemblyOrder`
and **no** `Backend` key (§0's table). Put the reason in a comment beside the `Device` line, citing
`bilinearform.cpp`, so nobody later "adds" partial assembly.

**R-em3d136-1d — the plan's hash.** The mesh-reuse check and anything else that hashes `ConfigJson` will now see a
different file for the same setup. **The mesh does not depend on the device.** Confirm that switching CPU↔GPU reuses
the mesh. If the reuse key includes the config JSON, exclude the device from that key, and say so in
`src/Design/RESOLVED.md`.

## 2. `R-em3d136-2` — how many processes

**R-em3d136-2a** — in GPU mode, `processes = detected GPU count` (from the probe, R-em3d136-3). When the Cores cap is
set, it still caps the count: `min(gpus, maxCores)`. Note it in the run log as a **Note**:

> Palace ran on 2 GPU(s), one process each.

**R-em3d136-2b** — `RankRefusal` (`Em3dRunService.cs:1723`) still applies to a capped count. Its sentence is about
cores, so in GPU mode it must not fire on a count derived from GPUs (two GPUs on a one-core VM is not a reason to
refuse). Write the rule, then test it (§4).

**R-em3d136-2c** — `OMP_NUM_THREADS=1` stays as it is (`PalaceRun.cs:211`, `WslPalaceRunner.cs:221`). It is not
related to the device.

## 3. `R-em3d136-3` — the probe: does this Palace do GPUs, and how many does it see

**R-em3d136-3a — a new `SolverCapability.Gpu`.** It is required only when the device preference is GPU. For a CPU
run, nothing changes and no probe is added.

**R-em3d136-3b — the probe reuses the existing one-element electrostatic solve.** `PalaceProbe` already runs one for
wave ports (`SolverDiscovery.cs:841-860`). Run the same solve, with `"Device": "GPU"`, and read its stdout:

- a line matching `^Detected (\d+) \S+ devices?` → the build supports GPUs, and `N` is the count;
- no such line → a CPU build;
- `N = 0` → a GPU build that sees no GPU.

**The parse must not name a backend.** Match `\S+`, never the backend's own word. This repo carries no vendor or
product names (CLAUDE.md), and the regex needs none. The same goes for every user-facing sentence: say "GPU".

**R-em3d136-3c — the verdict carries the count.** `SolverCapabilityVerdict` (or a field beside it) records the
detected GPU count so R-em3d136-2a can read it without a second probe. Cache it the way the existing verdicts are
cached; check how before adding anything.

**R-em3d136-3d — refusals, before any meshing**, through the same readiness path as the eigenmode refusal
(`SolverDiscovery.cs:720-732`):

- CPU build: "Palace at '<path>' was built without GPU support, so it would run on the CPU. Choose CPU under
  Device, or point circuitRF at a GPU build of Palace in Settings ▸ Solvers (see '<manual install section>')."
- `N = 0`: "Palace at '<path>' supports GPUs but found none on this machine. …"

Wording is the implementer's. These are the facts each sentence must carry. `Describe(SolverCapability.Gpu)` and
`ProbeKind` gain their rows.

**R-em3d136-3e — Windows (WSL).** The subsystem runner probes and launches inside the Linux distribution. Confirm the
probe goes through the same runner a run would use (`Wsl/SolverDiscovery.Subsystem.cs`). A GPU visible to the Linux
subsystem is the user's own configuration, so circuitRF reports what Palace reports and does nothing else.

## 4. `R-em3d136-4` — where the setting appears

**R-em3d136-4a — GUI.** A **Device** row (CPU / GPU) directly under **Cores** in the EM Setup panel
(`EmSetupEditorView.axaml:1716`), bound through a preference class shaped like `EmSolveCorePreference`. Show it only
where a Palace run is possible (follow the `Cores` row's own visibility). **GPU is selectable even when the current
Palace cannot use it.** The refusal (3d) says why at run time. A disabled item would need a note to explain itself,
and the owner wants no notes under values.

**R-em3d136-4b — Settings ▸ Solvers.** The Palace row reports the probe's answer once it has been asked: "GPU: 2
detected" or "GPU: not in this build". Only probe from Settings when the user has chosen GPU. A CPU user should pay
nothing.

**R-em3d136-4c — CLI.** `em` gains `--device cpu|gpu` (default `cpu`; an unknown value is a refusal listing the two).
Headless there is no preference file to read, and a build machine must say what it means. `solver list` / `solver`
report the GPU line when asked (`--capability gpu`, or whatever spelling `solver` already uses for eigenmode: match
it). `explain` on a 3D setup reports which device a run would use and why.

**R-em3d136-4d — MCP.** `run analysis=em` takes `device`; `solver` reports the GPU capability. Both go through the
CLI's code, with no logic of their own (the `Authoring.cs` rule).

## 5. `R-em3d136-5` — after the run: check what Palace actually did

**R-em3d136-5a** — after a GPU run, read Palace's log for its `Device configuration:` line. If the first backend it
names is `cpu`, or the log contains `reverting to CPU`, the run used the CPU. Add a run **Warning** (not a Note):

> Palace was asked to use a GPU but ran on the CPU; see its log in <runDir>.

The probe should prevent this. The check exists because the probe ran against an earlier binary, or because the
environment differs between the probe and the run. It costs one read of a file the run already reads for progress
(`PalaceLogProgress`).

**R-em3d136-5b — memory.** The memory admission (`memory.Admit`) measures host RAM and stays as it is. GPU memory is
not estimated. When Palace runs out of it, Palace's own error must reach the user as the run's failure message, not
a generic "Palace failed". Check how a non-zero exit is reported today, and make sure the tail of Palace's stderr is
in it.

**R-em3d136-5c — the field test.** When 1-5a are in, ask the discussion #10 user for a run on their hardware. Owner
posts; do not post. What to ask for:
- `circuitrf solver --capability gpu` output;
- one driven example (an `examples/` `.c3d`) run with `--device cpu` and `--device gpu`, with the two S-parameter
  files and the two wall times;
- the Palace log of the GPU run.

The two results must agree to the solver's tolerance (`|ΔS|` below ~1e-4, per the owner's practice for small entries).
Record the outcome in `src/Design/RESOLVED.md`.

## 6. Not in this brief

- **A GPU install recipe.** It would need the GPU toolkit on the user's machine and a GPU architecture chosen at build
  time, and would put GPU toolkit and vendor names into the recipes. Revisit only after the field test, and only on
  the owner's word.
- **The direct linear solver on a GPU.** `LinearSolver = Direct` writes SuperLU (`PalaceConfigWriter.cs:483`). It is
  not refused in GPU mode. Whether it is worth anything there is a field-test question, so record what the tester
  sees.
- **openEMS.** It is unaffected. In a "both solvers" run, the device applies to the Palace leg only.

## 7. Tests (targeted classes only: `--filter` on the new and touched classes, never all of `Ui.Tests`)

One test per claim:

1. `PalaceConfigWriter` writes `"Device": "GPU"` in the driven, eigenmode and static configs when asked, and still
   `"CPU"` by default. Existing goldens are unchanged.
2. The writer emits no `PartialAssemblyOrder` and no `Backend` key.
3. The probe parse: a banner with `Detected 2 X devices` → supported, 2; no line → unsupported; `Detected 0 …` →
   supported, 0. Use canned stdout, not a real Palace.
4. The process count: GPU, 2 detected, no cap → 2; cap 1 → 1; and `RankRefusal` does not fire on a GPU count above the
   core count.
5. The refusal: a GPU run against a probe answering "unsupported" is refused before meshing, and the sentence names
   the path.
6. The post-run check: a canned log containing `reverting to CPU!` produces the warning.
7. `em --device` parsing, including the refusal for an unknown value.
8. The preference round trip (`em_palace_device`), absent → CPU.

Gate: those classes plus `tests/Firewall.Tests`. No real GPU run is possible here (§0). Say so in the completion
note rather than implying one happened.

## 8. Docs

`docs/user/src/reference/em-setup.md`: a short **GPU** section covering what Device does, that it needs the user's
own GPU build of Palace (link Palace's own install documentation for building one, and name no build flags or vendor
toolkits here), one process per GPU, and the CLI flag. Update the MCP reference text for `run`/`solver`. **No DocGen**:
the owner regenerates at the end of a series.

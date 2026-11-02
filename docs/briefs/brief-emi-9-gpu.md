# Brief EMI-9 — EMerge on a GPU

**Tag:** `R-emi9-n` · **Series:** EMI (see `brief-emi-0-overview.md`) · **Depends on:** EMI-3, EMI-5
**Area:** the EMerge recipes (optional GPU steps), the EMerge probe, a per-machine device preference beside Palace's
(`em_palace_device`, brief-em3d-136), the run's solver selection in the driver, Settings ▸ Solvers, `em --device`, the
MCP `run`/`solver` tools, `docs/user/src/reference/em-solvers.md`. Findings in `src/Design/RESOLVED.md`.

---

## 0. What exists, and what was measured (2026-10-21)

EMerge can hand its sparse factorisation to a GPU solver in two ways:

| GPU | Solver | Licence | How it installs | Testable here |
|---|---|---|---|---|
| Apple silicon | an open GPU sparse direct solver from EMerge's author (`sparta-solver`, from its upstream repository) | AGPL-3.0 | **built from source: needs a Rust toolchain** (measured: 22 s) | **yes** |
| one vendor's discrete GPUs (Windows/Linux x64) | that vendor's sparse direct solver library, through EMerge's `cudss` extra; upstream calls it a first test implementation | proprietary, free to download | wheels from PyPI, no compile | **no** (no such GPU here — field test, as brief 136) |

The Apple-silicon solver's own README says its backend for other GPUs is planned and not yet written; do not offer it
anywhere but Apple silicon until upstream ships one.

**Licences: no obligation reaches circuitRF.** Both are installed by the user's machine from their own upstream, into
circuitRF's solver home, and run inside EMerge's separate process; circuitRF redistributes nothing, modifies nothing,
and links nothing — the `em-3d.md` §7.1 arrangement. AGPL's extra condition applies to a modified version offered over
a network, which this is not. The consent dialog states each licence in one sentence, as it does for the rest.

**Measured on the 3D Eigenmode Cavity, driven, adaptive sweep, Apple M4 (10 cores, 16 GB):**

| Mesh | Single-threaded default | Fast CPU solver (EMI-3) | GPU solver |
|---|---|---|---|
| 9.8k tets | 48.7 s | 4.0 s | **3.1 s** |
| 20k tets | 166 s, 5.3 GB | 8.0 s, 3.5 GB | **4.8 s**, 3.0 GB |
| 42k tets | — | **21.7 s**, 12 samples | 39.4 s, **60 samples, did not converge** (error 1.8e-2 against 1e-3) |

Per factorisation the GPU was ~2.7× faster at 42k tets (0.66 s against 1.8 s). But it **factorises in single
precision**, and at 42k tets its answers were noisy enough that the adaptive sweep's rational fit never settled: it
solved 60 points and stopped at its cap. The notch it reported was still right (9.955 GHz, −13.45 dB). **That is the
problem this brief has to solve before the GPU can be the default.**

## 1. `R-emi9-1` — make the GPU's answers good enough for the adaptive sweep (measure first)

With the scratch harness, on the Cavity at the three meshes above:
- turn on the GPU solver's double-precision iterative refinement (it has one; check whether EMerge's call already
  enables it and with how many steps), and its hybrid mode if EMerge exposes one;
- record per run: samples to converge, error reached, solve time, and |ΔS| against the fast CPU solver's result.

**Go:** a setting under which the GPU converges in about as many samples as the CPU and stays faster. **No-go at some
size:** find the crossover and use it (R-emi9-3). Record the table in `RESOLVED.md` either way.

## 2. `R-emi9-2` — install: automatic when the tools are there

The same rule as EMI-3: **no question asked.** The macOS arm64 recipe gains an optional GPU step (EMI-3's `optional` +
`when`): when a Rust toolchain is found (`cargo` on `PATH` or in `~/.cargo/bin`, plus Apple's command-line tools), it
builds the GPU solver at a **pinned commit** into the environment. A failed build is a Warning quoting its last line,
not retried until the toolchain changes.

**R-emi9-2b — when Rust is missing: one button, and the instructions as well.**
- **Settings ▸ Solvers, EMerge row: Enable GPU solving…** (shown only on Apple silicon, only when the GPU solver is not
  installed and Rust is not found). It opens the existing consent dialog with the plan: download Rust's **minimal**
  toolchain with Rust's official installer **into circuitRF's EMerge solver home** (`RUSTUP_HOME`/`CARGO_HOME` set
  there, `--no-modify-path`), build the GPU solver, then **delete the toolchain**. The user's own shell, `PATH` and
  any Rust they install later are untouched; nothing is left behind but the built solver; uninstalling EMerge removes
  it. Measure and show the download size and time. Progress runs in the existing install runner.
- If Apple's command-line tools are also missing (Rust needs their linker), the button first offers **Install Apple's
  command-line tools…**, which starts Apple's own installer (`xcode-select --install`); when that finishes, Enable GPU
  solving… appears.
- The row's status line still says what is missing in one sentence, and the docs (EMI-8) give the manual route for a
  user who prefers to install Rust themselves — after which circuitRF builds the GPU solver automatically the next time
  it runs EMerge (EMI-3 R-emi3-3's mechanism), with no button needed.
- CLI: `circuitrf solver install emerge --gpu` does the same as the button (a build machine has no dialog); MCP `solver`
  reports whether GPU solving is enabled and the command that enables it.

The x64 recipes gain the vendor GPU extra **only when such a GPU is detected** (a large download that helps no one
else); detection must not name the vendor in any user-facing text ("a supported GPU"). It is a field-test item (§5).

## 3. `R-emi9-3` — when a run uses the GPU

**Per machine, like Palace's device (brief 136, D1): never in the `.cem`/`.c3d`.** A preference `em_emerge_device`:
`auto` (default), `cpu`, `gpu`.

- **`auto`** uses the GPU for a **driven** run when the GPU solver is installed and the problem is in the range §1
  showed it wins (by unknowns; the crossover from §1, with the refinement setting §1 chose), else the fast CPU solver.
  Eigenmode runs stay on the CPU (EMerge's eigensolver does not use it). This is how "maximum speed by default" is met
  without a setting.
- **A driven GPU run whose adaptive sweep stops at its sample cap** without converging is re-run on the CPU solver
  automatically, and the run says so as a Note naming both sample counts. The user always gets a converged answer.
- `gpu` forces it (and still reports a non-converged sweep as a Warning, without re-running); `cpu` never uses it.

The run summary (EMI-5 R-emi5-4c) names the device actually used, read back from `run.json`, not assumed.

## 4. `R-emi9-4` — the surfaces

- **Settings ▸ Solvers, EMerge row:** the status line's speed tier gains the GPU ("GPU solver installed" / "GPU solving
  available after installing Rust"). A **Device** choice (Auto / CPU / GPU) beside the existing Cores row for EMerge,
  bound like `EmSolveCorePreference`.
- **CLI:** `em --device auto|cpu|gpu` applies to EMerge as it does to Palace. `solver list` reports the GPU solver.
- **MCP:** `run` takes `device`; `solver` reports it.

## 5. Tests (targeted) and the field test

1. Recipe: the GPU step runs when the fake toolchain is present and is skipped (recorded, with the remedy) when not.
2. Device policy as a pure function: (problem type, unknowns, installed, preference) → device.
3. Fallback: a canned `run.json` with `samples == max` and `converged: false` on the GPU triggers exactly one CPU re-run
   and the Note.
4. Preference round trip; absent → `auto`.
5. With the GPU solver installed only (Skipped otherwise): the Cavity at 20k tets on the GPU matches the CPU result
   within the |ΔS| threshold.

**Field test (owner arranges; do not post):** someone with a supported discrete GPU on Windows or Linux runs the
Cavity driven with `--device cpu` and `--device gpu` and sends both result files and timings. Record the outcome in
`src/Design/RESOLVED.md`.

## 6. Docs

`em-solvers.md`'s speed section (EMI-8 R-emi8-1) gains the GPU per platform: what it needs, that it is used
automatically when it is faster, that eigenmode runs do not use it, and how to force CPU or GPU. No vendor names.

# Brief EMI-3 — Maximum speed by default: the fast sparse solver, built automatically when it can be

**Tag:** `R-emi3-n` · **Series:** EMI (see `brief-emi-0-overview.md`) · **Depends on:** EMI-2
**Area:** the EMerge recipes and `SolverInstaller` (a new optional-step kind), the EMerge probe, the run's environment
(EMI-5), the Settings row, `docs/user/src/reference/em-solvers.md` (the speed section is EMI-8's; this brief supplies
its facts). Findings in `src/Design/RESOLVED.md`.

---

## 0. The owner's rule

**Users get maximum speed without being asked.** If the machine has the tools to build what is needed, circuitRF builds
it automatically. A user without them gets a message saying exactly what to install, and circuitRF does the rest once
the tools appear. No second consent: the build is part of the install the user already agreed to.

## 1. What "fast" is, per platform (verify each row before relying on it)

EMerge picks its sparse direct solver from what is importable, in a fixed preference order. What the default install
gets, and what makes it fast:

| Platform | Baseline install gets | Fast needs | Compile? |
|---|---|---|---|
| Windows x64, Linux x64, macOS x64 | the vendor-licensed multi-threaded direct solver, pulled in by EMerge's own dependencies | nothing more | no |
| macOS arm64 | **single-threaded SuperLU** (measured, EMI-0 §2) | the macOS sparse-solver package (`emerge-aasds`, from its upstream repository) | **yes, a small C library: Apple's command-line tools** (measured: 3 s build; 12× faster at 9.8k tets, 21× at 20k) |
| Linux arm64 | single-threaded SuperLU | an MIT-licensed direct solver published as wheels for every platform (`rslab`) — **measure that it is faster than SuperLU before adopting it** | no |
| Windows arm64 (x64 under emulation) | as Windows x64, emulated | — | no |

GPU solving is EMI-9.

**R-emi3-1 — measure first.** With a scratch harness (not a `Benchmark` test), on the 3D Eigenmode Cavity driven at
two meshes, record per platform available here: the solver EMerge chose, the solve time, and the peak memory, for
each candidate. Adopt a candidate only where it wins. Put the table in `src/Design/RESOLVED.md`; EMI-8 turns it into
the user's page. If `rslab` also beats SuperLU on macOS arm64, it is the no-compiler fallback there too.

## 2. `R-emi3-2` — the optional build step

**R-emi3-2a — a new recipe step property, `optional` with a `when` prerequisite.** The step runs only when its
prerequisite is met (e.g. `xcode-select -p` succeeds and `clang` runs). When it is not, the step is **skipped**, not
failed, and the install records `accelerator: missing` with the prerequisite's remedy. Extend
`recipe.schema.json`, `SolverRecipe` and `SolverInstaller` for it; every existing recipe is unchanged.

**R-emi3-2b — the macOS arm64 recipe** gains: Download the fast-solver package's source at a **pinned commit** (checked
after download like Palace's clones), then `uv pip install` it into the environment. Its prerequisite is Apple's
command-line tools, the same entry the Palace recipe already names, with the same remedy (`xcode-select --install`).

**R-emi3-2c — Linux arm64 (and any platform R-emi3-1 adopts it on)** adds the wheel-only solver to the lock file. No
prerequisite.

## 3. `R-emi3-3` — finishing later, automatically

When the probe (EMI-2 R-emi2-3) reports `accelerator: missing` and the prerequisite is **now** met, circuitRF runs the
recipe's optional steps **automatically** at the next point it was going to talk to EMerge anyway: the start of an
EMerge run (before meshing), or Settings ▸ Solvers being shown. The run log says so as a **Note**: "Built EMerge's faster
sparse solver (3 s)". It never asks. If that build fails, the run proceeds on the baseline and the failure is a
**Warning** quoting the build's own last line; it is not retried on every run (record the failed attempt against the
prerequisite's state, and retry only when that state changes).

## 4. `R-emi3-4` — the message for a user who cannot get there

Exactly one place shows it persistently: the EMerge row's status line in Settings ▸ Solvers, e.g. *"Baseline sparse
solver, one core. Install Apple's command-line tools (xcode-select --install) and circuitRF will add the faster one
automatically."* An EMerge **run** on the baseline adds one **Note** with the same sentence (not a Warning: the answer is
right, only slower), once per session, not per run. The install summary says it too. The docs page (EMI-8) is linked
from the row.

**A button as well as the sentence.** Beside that status line, **Install Apple's command-line tools…** starts Apple's
own installer (`xcode-select --install`). When it completes, circuitRF builds the faster solver automatically
(R-emi3-3) — the user presses one button and nothing else. The same pattern serves EMI-9's GPU solver.

## 5. `R-emi3-5` — threads and processes

- The run sets the thread count from the existing **Cores** preference (`EmMaxCores`; all physical cores when unset)
  through the environment variables the chosen solver reads, and through EMerge's own configuration where it has one.
  Measure that it takes effect (R-emi3-1's harness: one core vs all).
- Eigenmode is single-threaded in EMerge 3.0 whatever is installed (ARPACK on SuperLU). Say so once in the Eigenmode
  run's Notes; do not try to work around it.
- Frequency-parallel workers (EMerge can solve sweep points in parallel processes) multiply memory. **Default off**;
  R-emi3-1 measures whether workers × threads beats threads alone at the shipped examples' sizes, and only then is a
  default chosen. Record the measurement either way.

## 6. Tests (targeted)

1. Schema: an `optional` step with an unmet `when` is skipped and recorded as `accelerator: missing`; a met one runs.
2. `SolverInstaller` with a fake prerequisite flipping from unmet to met runs the optional steps once, then not again.
3. A failed optional build is not retried until the prerequisite's recorded state changes.
4. The Settings sentence and the run Note come from one function (a source scan or a shared constant).
5. The thread environment for a run is derived from the Cores preference (a pure function, tested without a process).

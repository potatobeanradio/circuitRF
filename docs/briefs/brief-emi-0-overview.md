# Brief EMI-0 — EMerge as a third 3D solver: overview

**Series:** EMI-0 overview · EMI-1 the solver seam · EMI-2 install and Settings · EMI-3 maximum speed · EMI-4 the
lowering · EMI-5 run and results · EMI-6 Compare · EMI-7 fields and radiation · EMI-8 docs, example and validation · EMI-9 GPU · EMI-10 memory ceiling.
Wave ports are their own sub-series, **EMW-0…** (`brief-emw-0-overview.md`). · **Tag:** `R-emi<n>-<k>` · **Origin:**
owner request, 2026-10-21

---

## 1. Why

The owner wants circuitRF to work with the open EM solvers its users want to run, and to make integrating each one a
contained piece of work. The 3D editor draws a `.c3d`; the solver that runs it should be the user's choice. Palace
(FEM) and openEMS (FDTD) are in. **EMerge** — an open FEM solver for the time-harmonic Helmholtz problem, distributed
on PyPI — is the third, and the first one added after the code was written around exactly two.

So this series does two things, in this order:

1. **Makes "a 3D solver" a thing the code has a seam for** (EMI-1), with Palace and openEMS moved onto it unchanged.
   Today the solver is `Em3dSolver { None, Palace, OpenEms, Both }` (`src/Design/Layout/Em/EmSetupModel.cs:38`), read
   in 96 places across 23 files, and `Both` hard-wires a two-solver comparison
   (`Em3dRunService.RunBoth`, `Em3dComparison`). A third solver added to that enum would add a third arm to every one
   of those branches and a fourth and fifth value to `Both`.
2. **Adds EMerge on that seam** (EMI-2…EMI-8), and replaces `Both` with **Compare**: any set of the installed solvers
   that can solve the problem (EMI-6).

## 2. What EMerge is, as measured (2026-10-21)

Probe on the 3D Eigenmode example's **Cavity** (22 × 20 × 4 mm aluminium housing, 0.254 mm PTFE-glass laminate, a
50 Ω PEC line between two lumped ports), written by hand as an EMerge 3.0.0b2 script, on an Apple M4 (10 cores,
16 GB), macOS 27.0. Palace's numbers are the example's own committed ones (same machine); openEMS was run then through
circuitRF.

| | Palace 0.18.1 | EMerge 3.0.0b2 | openEMS 0.37 |
|---|---|---|---|
| Cavity mode | 9.95642 GHz | **9.95651 GHz** | (no eigensolver) |
| Its loaded Q | 539 | 620 | — |
| Eigenmode run | 3 min 42 s, 3.7 GB | 2 min 27 s, 1.9 GB, **one thread** | — |
| S21 notch frequency | 9.955 GHz | 9.955 GHz | 9.955 GHz |
| S21 notch depth | −11.69 dB | **−13.3 dB** (−13.33 / −13.22 / −13.44 at 9.8k / 20k / 42k tets) | −11.54 dB |
| Worst S21 off the notch | within −0.15 dB | −0.11 dB | −0.14 dB |
| Driven run | 34 s, 4.3 GB | **4–18 s**, 1.9–6.2 GB (by mesh) | 14 s, 0.08 GB |

- **The resonance agrees to 1e-5.** The **notch depth** differs from both other solvers by ~1.6 dB and does not move
  with the mesh, so it is a modelling difference, most likely in how EMerge's lumped port loads the line. The owner
  judged it acceptable for adding EMerge. **It stays an open item** carried by EMI-8 (§5 there), not a closed one.
- **Driven is fast** because EMerge's adaptive (rational-fit) sweep needed 12–13 solves for the 801-point band, and its
  macOS sparse solver uses every core. **Eigenmode is single-threaded** (ARPACK on SuperLU) whatever is installed.
- **The fast CPU solver is most of the speed.** Forcing EMerge's single-threaded default on the same driven runs took
  48.7 s (9.8k tets) and 166 s (20k tets) against 4.0 s and 8.0 s — 12× and 21× — with identical S.
- **Install:** a fresh environment and `pip install emerge==3.0.0b2` took **72 s and 837 MB, with nothing compiled** —
  version 3.0 ships compiled wheels for macOS arm64/x64, Linux x64/arm64 and Windows x64. Gmsh arrives as a pip
  dependency. **The default macOS sparse solver is single-threaded SuperLU**; the fast one is a separate package built
  from source (3 s, needs Apple's command-line tools). EMI-3 owns that.
- **The API is under active development.** 3.0 renumbered the solver enum, changed the far-field return type and axis
  order, and changed the PCB port API. Everything circuitRF writes is pinned to one validated version (EMI-2
  R-emi2-6).

## 3. Decisions this series is built on

**D1 — EMerge runs as a separate Python process, installed by circuitRF into its own solver home, never shipped.**
`docs/design/em-3d.md` §5 and §7.1 apply unchanged: circuitRF writes an input file (here a generated Python script),
starts a separately installed program, and reads what it writes. EMerge is GPL-2.0; circuitRF redistributes nothing and
copies no EMerge source. **Nothing in circuitRF imports or ports EMerge code**, and that includes its port-mode solver.

**D2 — circuitRF provisions its own Python, through `uv`, inside the solver home.** No system Python is read, needed
or changed; no `PATH` edit; uninstall is deleting the home. `uv` (MIT/Apache-2.0) is a single static binary per
platform with a published checksum, so it fits the existing Download/Extract/Run recipe steps (EMI-2).

**D3 — Maximum speed is the default and needs no question.** When the machine can build a faster solver, the install
builds it inside the one consent the install already asks for. When it cannot, the user gets one clear message naming
what to install, and circuitRF finishes the job automatically the next time it sees the tools (EMI-3).

**D4 — EMerge is not the default solver** for a new setup. Palace stays the FEM default while the pinned EMerge is a
pre-release and the open item in §2 is unresolved; revisit when both are settled.

**D5 — Compare replaces Both** (EMI-6). A setup either names one solver or asks to **Compare** a chosen set; a solver
that is not installed, or cannot solve this problem, is shown disabled with the reason. `"Both"` in an existing file
keeps meaning Palace + openEMS forever.

**D6 — Wave ports are a sub-series (EMW).** EMI-4 lowers lumped ports and refuses a wave port **by name**, pointing at
the solver that can run it. EMW adds wave ports and terminal groups and may reuse `ModalTerminalTransform`
(`src/Engine/Em3d`), whose inputs EMerge can supply. See `brief-emw-0-overview.md`.

## 4. Order and dependencies

| Brief | Builds | Depends on |
|---|---|---|
| EMI-1 | The solver seam: identity, capabilities, discovery, readiness, plan/execute per solver; Palace and openEMS moved onto it **byte-identically** | — |
| EMI-2 | The EMerge recipes (uv + Python + pinned, hash-locked EMerge), discovery and probe, Settings ▸ Solvers row, `solver install emerge`, uninstall | EMI-1 |
| EMI-3 | The fast sparse solver per platform, built automatically when the tools exist, the message when they do not, threads | EMI-2 |
| EMI-4 | `Em3dProblem` → the EMerge script; `EMerge` settings in `.cem`/`.c3d`; refusals; `check`/`explain` | EMI-1 |
| EMI-5 | The run: process, progress, cancellation, memory, S/eigen results → `DataSet` + Touchstone; CLI and MCP | EMI-2, EMI-4 |
| EMI-6 | Compare: N solvers, a reference, the generalised comparison, the setup UI, CLI/MCP, format | EMI-1, EMI-5 |
| EMI-7 | Field plots and radiation patterns from an EMerge run | EMI-5 |
| EMI-8 | User docs (including "getting the most speed"), the example, the validation set, the open notch item | all |
| EMI-9 | GPU solving: built automatically when the tools exist, used automatically when faster, CPU fallback when the sweep does not converge | EMI-3, EMI-5 |
| EMI-10 | A memory ceiling and a free-disk floor for every solver and mesher process (added 2026-10-22, after a run filled the disk through swap) | EMI-5 |

EMI-2/EMI-3 and EMI-4 are independent once EMI-1 is in. EMW-1 (the spike) can start any time after EMI-5.

## 5. Owner decisions — recommendations given, still open unless marked

- **O1 — The pinned version.** EMerge 3.0 is in beta (3.0.0b2, 2026-10-21). *Recommended:* implement against 3.0.0b2;
  re-pin to 3.0.0 final before release; never ship a recipe that installs a beta without the owner's word.
- **O2 — Windows on ARM64.** EMerge publishes no Windows ARM64 wheel. *Recommended:* the Windows ARM64 recipe installs
  the x64 Python and EMerge and runs them under the system's x64 emulation — the owner's only Windows machine is an
  ARM64 VM, so this is also how Windows gets tested at all.
- **O3 — The macOS fast-solver package declares MIT in its metadata but its repository has no licence file.**
  *Recommended:* the owner asks upstream to add one (owner posts; do not post). Not blocking: circuitRF builds it from
  upstream on the user's machine and redistributes nothing.
- **O4 — Compare's reference.** *Recommended:* the user picks the reference among the chosen solvers; the default is
  Palace when chosen, else the first chosen (EMI-6).
- **O5 — GPU solving: answered, in this series (EMI-9),** because no licence obligation reaches circuitRF (the user's
  machine installs it from upstream; circuitRF redistributes nothing). Measured on Apple silicon: 1.3–1.7× faster than
  the fast CPU solver up to 20k tets, but at 42k tets its single-precision answers kept the adaptive sweep from
  converging. EMI-9 measures the fix first and falls back to the CPU automatically.

## 6. Standing rules for every brief in the series

- **No vendor or EDA tool names** anywhere, including user docs and run messages. Name a GPU as "a GPU". A pip package
  name appears only where a recipe or a lock file has to spell it; prose says what it does ("the faster sparse solver").
- **No outside tool or website** is cited in briefs, code comments, commits or docs, beyond the solver's own upstream.
- Findings go in the nearest `RESOLVED.md` (`src/Design/RESOLVED.md` for most of this), never a `CLAUDE.md`. Never
  quote the owner.
- Tests: one per claim, targeted classes only — never the full suite or all of `Ui.Tests`; read the TRX, never re-run
  to find a failure. A performance claim is gated by a COUNTER, never a timing. **No test may need EMerge installed to
  pass**; one that runs EMerge reports Skipped with a reason when it is absent (`FixtureFact`'s pattern).
- Keep EM runs short and specific. Measure with a scratch harness, not new `Benchmark` tests.
- DocGen is the owner's, at the end of the series; edit doc sources only.

# Brief MC-16 — Example workspace, user pages, series acceptance

**Series:** `brief-modelcomposer-0-overview.md` · **Tag:** `R-mc16-<m>` · **Depends on:** all
**Area:** a new `examples/Model Composer/` and its row in `examples/examples.json`; `docs/user/src/reference/`
(`model-composer.md`, `file-formats.md`, `components.md`, `cli.md`, `ai-agents.md`); `docs/user/src/_nav.txt`;
`docs/design/model-composer.md` (final state)
**Read first:** `examples/README.md` (adding an example)

---

## 1. Goal

A user opens an example, sees a model built end to end from data, and can repeat it on their own device. The
series proves itself on synthetic ground truth here and, locally, on real data.

## 2. Requirements

**R-mc16-1 — The example workspace** `examples/Model Composer/`:
- **`synthetic-gan`**: a `.cmc` over data generated from a synthetic device. The data is an SDD
  truth inside the GaN network with a pad model — DC I–V, pulsed I–V from three quiescent points (one set
  double-pulse, with its pre-pulse stated), multi-bias S at two temperatures — and is **labelled as synthetic in the data set notes**. The document carries a fitted network,
  ANN currents, adjoint-ANN charges, self-heating and drain-lag. The exported `.cmodel` comes with an 8-finger
  instance in a class-AB power-sweep testbench and a loadpull testbench.
- **`passive-spiral`**: a family of spiral S-parameter blocks over turns, a fitted family cell, and a schematic
  using it at a turn count between two measured ones.
- **`simulated-tables`**: a `.cmc` importing `.npy` tables of the kind another simulator writes (I, Y at a low
  frequency, intrinsic voltages), so the cross-check of D6 is visible.
- Data files are small: ≤ 200 bias points, ≤ 50 frequencies. Any analysis setting reduced for size or speed is
  stated in the example's notes, with the value a real job would use.
- A row in `examples.json`.

**R-mc16-2 — User pages** (sources only; the owner runs DocGen):
- `reference/model-composer.md` — what it does; the steps; the data kinds and their conditions; the reference-plane
  rule; the six-pin network rule; methods and their defaults; charge conservation and its three checks;
  temperature and the single-temperature rule; traps as a setting, and what each kind of pulsed data allows (double-pulse, ordinary, one trap level, none);
  periphery scaling; export; passive models; the
  CLI spelling.
- `file-formats.md` — `.cmc` and `.cmodel`.
- `components.md` — `ComposedFet`.
- `cli.md` — `model`.
- `ai-agents.md` — the agent recipe.
- `_nav.txt` gains the page.

Owner rules: no change history in the pages; no vendor names; no "the design's own".

**R-mc16-3 — Design note final state.** `docs/design/model-composer.md` reflects the decisions as built, every
deviation from this series with its reason, and the open items: power-law and unit-width scaling, multi-time-constant
traps, transient-data trap estimation, waveform (large-signal) data as a training source ([P1] §6.7), and noise.

**R-mc16-4 — Acceptance on synthetic truth** (`ModelComposerAcceptanceTests`, routine tier unless it measures over
~5 s, in which case it is `Category=Benchmark` with a routine-tier wiring test beside it). From the raw synthetic
files to an exported model, with no hand edits:
- DC I–V within 1 % of range;
- S-parameters `|ΔS|` 95th percentile ≤ 0.02 at the measurement plane;
- HB power sweep at the truth's class-AB bias: Pout within 0.1 dB and PAE within 1 pp up to 3 dB compression;
- two-tone IM3 within 1 dB down to 20 dB below compression;
- charge-conservation check (b) ≈ 0, and (c) below its threshold;
- `N = 2·N0` scaling consistent with the truth scaled the same way.

The thresholds are this brief's proposal. If the synthetic truth shows they are wrong, change them **once**, with
the reason in `RESOLVED.md`.

**R-mc16-5 — Acceptance on real data (local only).** A `FixtureFact` test that runs the same flow on a local data
folder when present, and is skipped with a reason otherwise. Its results are reported to the owner in chat, never
committed, and never named in commits or docs.

## 3. Not in this phase
New capability. Anything found missing is reported, not built.

## 4. Gates
- `ModelComposerAcceptanceTests` (R-mc16-4).
- `ModelComposerRealDataTests` (R-mc16-5, `FixtureFact`).
- `ExampleWorkspaceLoadsTests` — each example opens, `check` reports 0 errors, and its testbenches run.
- The `docs/user/src` sources build their nav (the existing nav test), with no DocGen run.

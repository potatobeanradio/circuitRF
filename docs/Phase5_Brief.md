# Phase 5 — Implementation Brief: the `DataSet`/`DataCube` Result Model, Generic Sweep & Export (Claude Code / Sonnet)

**Goal:** finish the **`DataSet`/`DataCube` result model** (the primitives exist in RfCore; one real gap
remains), **retrofit every analysis to return it** (replacing the ad-hoc per-engine result structs), build the
**measurement evaluator** (composable cube-algebra: `measure Name = expr` over analysis-qualified accessors,
with the FOMs as `.cnfunc` macros — not built-ins), add the **generic parametric sweep** (a sweep axis across
the cubes), and add **`.mat`/`.npy` export**. This is the unification phase — every result becomes one model —
and it is the seam splotRF consumes in Phase 7.

> Read first: root `CLAUDE.md`, **`src/Core/Data/CLAUDE.md`** (the full, authoritative `DataSet`/`DataCube`
> spec — accessors, slicing, transforms, reductions, DataKind, export, retention), `docs/design/data-model.md`
> §3.3/§7 (the result model), `docs/design/measurements.md` (measurements become cubes in the DataSet),
> and the existing ad-hoc result types you will retrofit (below). Design notes / the Data CLAUDE.md win
> over this brief.

## Context — what's done and what this phase actually is
The original `Development_Plan.md` Phase 5 row ("generic sweep + DataSet + loadpull experiment + export,
gated by Hero 3 contours") **predates Phase 4b**. The loadpull/sourcepull experiment, the `Tuner`, the Γ/Z
grid, harmonic loadpull, and the pursuit search are **already built and Hero 3 already runs from the CLI**.
What remains — and is the real Phase 5 — is the **result-model unification**: the cube *primitives* exist,
but nothing returns them yet.

**The primitives ALREADY EXIST in RfCore** (`RfCore/src/Data/`, the shared home — verified):
- **`DataCube.cs` — essentially complete and matches the spec.** `DataKind`, named unit-bearing `Axis`,
  flat `Complex[]`/`double[]` buffer + strides, the positional indexer (`int` pins+drops, `Range` keeps,
  bare-element when all pinned), transforms (`Real/Imag/Mag/Phase/DB10/DB20/DB`-alias/`Conj` with the
  correct 10-vs-20 split and Complex guards), reductions (`Max/Min/Peak/At`, Real-cube guard), and the
  `SliceResult` discriminated union. **Treat as done — verify against the CLAUDE.md examples, don't rebuild.**
- **`DataSet.cs` — mostly there, with two real gaps.** It has cube registration, the `S`/`Y`/`Z(i,j)`
  port-number accessors, and a `DataSetBuilder.FromSnp` (the S-param→DataSet starting point). **Gap 1:**
  the `V`/`I` node accessors throw `NotImplementedException` — `LabelIndex` needs a **node-name registry**
  (the node axis currently stores indices, with no name→index map). **Gap 2:** only the SNP-wrapping build
  path exists; the engine-result retrofit does not.

Every engine still returns its **own ad-hoc result type** (`HbResult`, the S-parameter result,
`LoadpullResult`, `LoadpullPursuitResult`, etc.) — exactly the "no analysis may invent its own result
struct" rule the Data CLAUDE.md forbids. **And the measurement layer (`docs/design/measurements.md`) is
not built at all** — FOMs are currently computed ad-hoc inside each engine/test (loadpull's internal
Pout/Gt/DE, Hero 5's IM3 in test code), with no general measurement evaluator that reads operands from the
result cubes and writes a named FOM cube back. **So Phase 5's real work is: the node-name registry +
finishing the `DataSet` node accessors (5-1), the RETROFIT (5-2…5-3, 5-5), the MEASUREMENT EVALUATOR (5-4),
the generic sweep (5-6), and export (5-7)** — NOT building the cube primitives from scratch.

**The Data CLAUDE.md is a complete spec.** This phase is implementation-of-an-approved-design, not new
design — follow that CLAUDE.md exactly (it is owned by circuitRF and consumed by splotRF; its contract is
load-bearing). The types live in **RfCore** (`RfCore/src/Data/`, shared with splotRF) — confirmed; build
there.

## Working style
Mostly clear-spec execution (Sonnet's strength), but the **retrofit touches every engine** — proceed
**sub-gated**, one analysis at a time, keeping the existing tests green at each step. The result *values*
must not change — this is a re-housing of the same numbers into cubes, so every existing hero regression
(Hero 1–5, loadpull, pursuit) must still pass after its engine is retrofitted. Diagnostics over grinding;
flag design questions.

## Scope — sub-gated

### 5-1 — Verify the primitives + close the `DataSet` node-accessor gap
The primitives already exist in `RfCore/src/Data/` — do NOT rebuild them. This step is verify + finish:
- **`DataCube.cs` — verify, don't rebuild.** Confirm it matches `src/Core/Data/CLAUDE.md` by writing the
  CLAUDE.md examples as tests (slice: `int` pins+drops / `Range` keeps / **end-exclusive** / bare-element
  when all pinned; transforms incl. the **`.DB10()`/`.DB20()`/`.DB()`** split; reductions with the
  Real-cube guard; DataKind honesty). If a test reveals a spec gap, fix the minimal thing and flag it.
  (Heads-up: the CLAUDE.md uses lowercase `.real()/.dB20()`; the code uses PascalCase `.Real()/.DB20()` —
  C# convention. That's fine; just note the accessor names in the retrofit.)
- **`DataSet.cs` — close the node-name gap (the one real piece of new work in 5-1).** It already has cube
  registration, the `S`/`Y`/`Z(i,j)` port accessors, and `DataSetBuilder.FromSnp`. But the `V`/`I` node
  accessors throw `NotImplementedException` — `LabelIndex` needs a **node-name registry** so
  `ds.V("X1.drain", harmIdx, ..)` resolves a node *name* to its axis index. Implement it: the node axis
  must carry the user-facing node/branch names (a parallel `string[]` name array on the axis, or a
  name→index map on the cube/DataSet), populated by the HB retrofit (5-3). Then `V`/`I` resolve name→index
  and slice the remaining axes positionally (**every remaining bracket slot is an axis INDEX, never a
  physical value**). This is the accessor 5-3/5-4 depend on, so do it first.
- Tests: the DataCube algebra (above) + the `V`/`I` name-resolution path on a small synthetic DataSet.

### 5-2 — Retrofit the linear/S-parameter engine to return a `DataSet`
- S-parameter result → the `S` cube `{freq, i, j}` (port numbers). Y/Z likewise.
- **Hero 1 must still pass** (the <1e-6 match) reading from `ds.S(...)`.

### 5-3 — Retrofit the HB engine (single- and multi-tone) to return a `DataSet`
- `V`/`I` cubes `{node, harmonic, Pin}` (single-tone) / `{node, mixIndex, Pin}` (two-tone), DC included.
- Honor the **node-retention policy** (Data CLAUDE.md): record only user-named nodes + measurement-
  referenced nodes; currents by instance+port are always kept. (The measurement layer, 5-4, defines the
  referenced set — so the retention scan reads the measurements' resolved paths.)
- **Heroes 2, 4, 5 must still pass** reading V/I from the cubes; the FD-Jacobian and IM3-slope checks
  unaffected (they're engine-internal).

### 5-4 — The measurement evaluator (`docs/design/measurements.md` rev 3 — currently unbuilt)
Build the measurement layer per `measurements.md` (rev 3 — **composable cube algebra, NOT a built-in FOM
library**). This is real, designed-but-unimplemented work: it turns the cubes into the FOMs users read, and
it canonicalizes the quantities the engines currently compute ad-hoc.
- **Directive:** `measure Name = expression` at the TestBench level (measurements.md §1, §7). **No `@
analysis` field** — each operand names its analysis **inline** as a dotted qualifier (`HB1.V(...)`,
  `SP.S(1,1)`), so one measurement may reference **multiple analyses**. The result cube is added to the run's
  `DataSet` under `Name`. Validate at elaboration: unknown qualified analysis, or an operand that can't
  resolve in that analysis's result → user error with the offending name, never a silent zero.
- **Engine surface is SMALL — primitives only (measurements.md §3.1):** (a) qualified cube accessors
  `A.V(nodePath, harm, slices…)` / `A.I(branchRef, harm, slices…)` / `A.S(i,j)` — these just select the
  analysis's `DataSet` and call the **same cube accessors 5-1 builds**; (b) **cube-valued arithmetic** —
  extend the Phase-1 expression engine so operands can be `DataCube`s and operators/functions broadcast
  **element-wise** (cube×cube, cube×scalar); (c) element-wise helpers `conj/re/im/mag/phase/log10/ln/dB/
  dB10/dBm` + sweep reductions `max/min/peak/at`. **Do NOT build `Pout`/`PAE`/`IMn` as built-ins.**
- **FOMs are pure macros in a `.cnfunc` standard library:** ship `stdlib.cnfunc` with `func` definitions
  (`Pout_W`, `Pout_dBm`, `Pin_del_W`, `Gain_dB`, `DE`, `PAE`, `IM3_dBc`, …) built from the primitives, per
  measurements.md §3.2. These are **ordinary Phase-1 user functions whose parameters bind to cube slices** —
  reuse the existing user-function machinery (now over cubes); no FOM-specific engine code. The user can read
  and override them. `.cnfunc` is a loadable function-definition file (distinct extension from `.cnl`).
- **Current is addressed by NAMED BRANCH, never by net (measurements.md §2.3):** build BOTH (a) component-
  terminal references `A.I(X1.M1:d)` (the `instance:terminal` form, `:` separator) and (b) an explicit
  **`IProbe`** component (a named zero-volt series ammeter, `A.I(IP1)`) that adds one branch current to the
  MNA solve. Both build now. The current-direction sign is **not special** — `conj(-1*A.I(...))` is plain
  scalar×cube; the engine has no current-direction awareness.
- **Operand resolution by absolute downward path** against the elaborated node/branch map (measurements.md
  §2.2/§2.4) — reuse the 5-1 name registry; no up/sideways reach. Result cube `DataKind` follows from the
  expression (helpers set Real/Complex; measurements.md §4).
- **`Pdc`/`DE`/`PAE` read the HB DC (k=0) component** — because the *user writes index 0* in the
  measurement (e.g. `A.V(vdd,0,All)`), capturing the drive-dependent self-bias shift. No hidden "find the
  supplies" logic; the user names the supply node and its `IProbe` current.
- **Cross-check oracle (the payoff):** a macro `Pout`/`DE` computed from the retrofitted `V`/`I` cubes must
  equal the value the engine computed internally (loadpull's own Pout/Gt/DE, Hero 2/5's FOMs). Wire this as
  a test at a hero point — disagreement means the retrofit (or a macro) is wrong. Independent check that the
  cube re-housing preserved the physics (same discipline as the FD-Jacobian/brute-force oracles).
- Tests: cube-valued arithmetic (broadcast cube×scalar/cube×cube); the helpers + reductions; `A.V`/`A.I`/
  `A.S` accessors incl. the `instance:terminal` and `IProbe` current paths; the `.cnfunc` macros producing
  correct Pout/Gain/PAE at a hero point; multi-analysis-in-one-measurement; unresolved-path/unknown-analysis
  errors.

### 5-5 — Retrofit loadpull + pursuit to the `DataSet`
- `LoadpullResult` → cubes with the termination axis (Γ/Z grid) × Pin × {node,harmonic} — the contour
  data a Smith-chart plotter consumes. Pursuit's `LoadpullPursuitResult` keeps its structured search
  outputs (MXP/MXE/Zsource/recommended `.gam`) but the **loadpull data it carries** becomes a `DataSet`
  (the generic follow-on `LoadpullResult` → cubes).
- The loadpull contour FOMs (Pout/DE/PAE over the grid) are now expressible as **measurements** (5-4) over
  the loadpull DataSet — so the engine's internal compression-stop FOMs and the measurement-library FOMs
  share one definition; cross-check they agree.
- **Heroes 3 / 3B must still pass**; the loadpull contour data is now sliceable cubes (`ds` over the
  termination grid), which is the Hero-3 contour-from-CLI exit criterion in cube form.

### 5-6 — Generic parametric sweep (the sweep axis)
- A top-level **sweep wrapping any analysis** adds a **sweep axis** across the cubes within the one
  DataSet (Data CLAUDE.md: "a sweep wrapping an analysis adds a sweep axis"). This generalizes the
  per-analysis `Sweep=` (HB power sweep, loadpull grid) into a uniform mechanism: sweep any §8 variable,
  re-run the inner analysis per point, stack results along the new named axis.
- Confirm it composes with the existing inner sweeps (e.g. a frequency sweep wrapping an HB power sweep)
  without double-counting an axis.

### 5-7 — Export (`.mat` / `.npy`) — small design note first
- Write a short **`docs/design/data-export.md`** (the Data CLAUDE.md references it; it doesn't exist) —
  the `.mat` mapping (each named cube → a named variable/struct field; whole DataSet → one `.mat`) and the
  `.npy` mapping (**whole DataSet as ONE packed structured/record array** with axis metadata — fixed in
  PRD §11, not one file per cube). Flag for owner review before implementing.
- Implement both exporters; CLI command to export a run's DataSet. Tests: round-trip a DataSet → `.mat` →
  load in Octave/NumPy (or a format-conformance test if no interpreter in CI), and `.npy` structured-array
  conformance.

## Acceptance
1. `DataCube` verified against `src/Core/Data/CLAUDE.md` (the examples as passing tests); the `DataSet`
   `V`/`I` node-name accessor implemented (node-name registry) — no remaining `NotImplementedException`.
2. **Every analysis returns a `DataSet`** — S-param, HB (1- and 2-tone), loadpull, pursuit — and **no
   ad-hoc result struct remains** as the public return (the "one result model" rule). Internal scratch
   types are fine; the *returned* contract is the DataSet.
3. **Measurement evaluator built** (composable cube algebra, measurements.md rev 3): qualified accessors
   `A.V/I/S` (incl. `instance:terminal` and `IProbe` current paths), cube-valued arithmetic, the
   element-wise helpers + reductions, and the `.cnfunc` FOM macros (Pout/Gain/PAE/DE/IMn) — **no built-in
   FOMs**. Each macro FOM matches the engine/hand value at a hero point (the cross-check oracle);
   `Pdc`/`PAE` read the HB k=0 component because the user writes index 0. Multi-analysis measurements work.
4. All hero regressions still pass reading from cubes: Hero 1 (<1e-6), Heroes 2/4/5 (HB), Heroes 3/3B
   (loadpull/pursuit) — **values unchanged**, just re-housed; FOMs via the measurement layer match.
5. Generic sweep adds a sweep axis across the DataSet; composes with inner sweeps.
6. `data-export.md` written + approved; `.mat` and `.npy` exporters implemented (`.npy` = one packed
   structured array); CLI export command; conformance tests.
7. `dotnet build`/`dotnet test` green; nothing in Phases 1–4 regresses.

## Guardrails
- **Implement the approved design — do not redesign.** `src/Core/Data/CLAUDE.md` is the authoritative spec;
  follow it exactly. Any deviation from cube shape / DataKind / axis semantics / accessor behavior is a
  flagged, reviewed change (splotRF consumes this contract in lockstep — root CLAUDE.md "Ask before").
- **Values must not change** — this is a re-housing of existing numbers into the cube model. Each hero
  regression must pass with identical numbers after its engine is retrofitted. If a number moves, stop and
  report — it means the retrofit altered a calculation, which it must not.
- Retrofit **sub-gated, one analysis at a time**, keeping tests green between steps.
- Slice API is **index-only** (every bracket slot an axis index, never a physical value) and ranges are
  **end-exclusive** (NumPy/C#, NOT MATLAB) — per the CLAUDE.md; don't conflate with the HB internals'
  1-based MATLAB pseudocode.
- DataKind honest: never store a real quantity as complex-with-zero-imag.
- Honor the HB node-retention policy (named + measurement-referenced nodes) — the measurement layer (5-4)
  defines the referenced set, so retrofit HB (5-3) and measurements (5-4) coordinate on it.
- Measurements are **composable cube algebra** (measurements.md rev 3): extend the Phase-1 expression engine
  to cube-valued operands; FOMs are `.cnfunc` macros (pure user functions over cubes), **not built-ins**. Do
  not build a second expression evaluator and do not hardcode `Pout`/`PAE`/`IMn`. Current is by named branch
  (`instance:terminal` or `IProbe`), never by net; the current-direction sign is plain arithmetic the macro
  writes, not engine-special.
- The types live in RfCore (`RfCore/src/Data/`, shared with splotRF — confirmed). Do NOT rebuild the
  existing `DataCube`/`DataSet`; verify and extend. splotRF consumes this in lockstep, so any cube-shape /
  DataKind / axis-semantics change is a flagged, reviewed decision.
- Update `src/Core/Data/CLAUDE.md` (mark implemented + note the node-name registry), the root CLAUDE.md,
  and write `data-export.md`.

*Phase 5 exit (one result model across all analyses, generic sweep, export) makes the engine half complete
and hands Phase 7 (splotRF Data Display) a single contract to consume. Remaining: Phase 6 (GUI), Phase 7
(splotRF integration), Phase 8 (hardening).*

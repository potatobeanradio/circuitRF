# Phase 5 — Corrective Brief: Linear Back-Solve, Branch-Current Addressing, Optional Sweep Axis
(Claude Code / Sonnet) — do BEFORE 5-6

Three coordinated corrections to the 5-3 / 5-5 retrofit, discovered during owner review. They are
**entangled in the same cube-construction + accessor code**, so do them together, then 5-6 (composable
nested sweep) builds on the corrected base. All three are "the retrofit baked in an assumption the general
result model must not have."

> Read first: `src/Core/Data/CLAUDE.md` (the result-model contract — note it currently *describes* `V` as
> `{node, harmonic, Pin}` as if all three axes are always present; that wording is part of what changes),
> `docs/design/measurements.md` rev 3 (§2.3 current-by-branch, §5 retain V *and* I), `docs/design/
> harmonic-balance.md` (the linear/nonlinear partition, `Y_NN`). Code: `src/Engine/HarmonicBalance/
> HbEngine.cs` (`HbResult`, `Run`, `SweepValues()`, the DataSet build), the loadpull/pursuit DataSet build,
> and `tests/Engine.Tests/HarmonicBalance/Hero2Tests.cs` (the forbidden node-current access). Design notes
> win; this is implement-the-decision, not redesign.

## Background — why these three, why now
The HB/loadpull retrofit (5-3/5-5) shipped working cubes, but baked in three assumptions the model must not
have, all exposed by owner review:
1. **The linear partition is eliminated, not solved** — only interface-node `V` and nonlinear `INl` are
   stored; a measurement on a *linear-interior* node/branch (a matching-network inductor, an internal node)
   cannot be answered, and there is no linear-network description retained to compute it. (measurements.md
   §2/§5 promises V and I "for every node/terminal.")
2. **Current is addressed by net/node** — tests read `ds["INl"][drainIdx, …]` as "drain current," which is
   forbidden (measurements.md §2.3: current is a **branch** property, never a net). It only works because
   Hero 2's drain has a single device terminal; it breaks the moment a node has two.
3. **The Pin sweep axis is hardwired as a required 3rd axis** — `SweepValues()` fabricates a single dummy
   `0` when there is no sweep, so the cube is always `[node, harmonic, sweep]`. A no-sweep run, or a future
   axis-light DC analysis, should not carry a fake degenerate axis.

5-4 (measurements) **cannot be built correctly without #1 and #2** — measurements.md's first example is
`HB1.I(X1.M1:d)` and FOMs on linear-network points need #1. So these precede further work.

## Correction 1 — Linear back-solve (Option A: lazy reconstruction)
Make linear-partition voltages and branch currents accessible by **lazily back-solving the linear network**
from the converged interface solution.
- **Retain the per-harmonic linear-network description in the result.** After HB converges, the linear
  partition's MNA description (the stamp that was reduced into `Y_NN`) plus the converged interface-node
  voltage spectra are sufficient to recover every linear-interior node voltage and every linear branch
  current. Keep a reference to that per-harmonic linear system on the analysis result (the thing the owner
  noticed is missing — "a linear network description in the DataSet").
- **Lazy + cached.** When a measurement/accessor asks for a linear-interior `V(node)` or `I(branch)` not in
  the stored cubes, back-solve the linear system for that harmonic (the system is already factored per
  harmonic during HB — reuse the factorization; one extra solve per harmonic per sweep point), cache the
  result. Do not eagerly expand and store the whole interior (that is the memory cost measurements.md §5
  defers — lazy IS that optimization, done from the start).
- The interface `V` and the device currents stay as-is (already correct); this adds the interior on demand.
- Verify: a measurement on a matching-network branch current and an internal node voltage in a hero returns
  the right value (cross-check: at the interface, the back-solved current must match the device current
  already stored, by KCL).

## Correction 2 — Branch-current addressing (the only current path)
Current is addressed by **named branch**, never by net (measurements.md §2.3). Build both forms; remove the
node-current path from the public/measurement/test surface.
- **Component terminal — `instance:terminal`** (the `:` form): `I(X1.M1:d)`, `I(Lmatch:1)`. Resolve against
  the elaborated branch map; each `ComponentModel` declares its terminal names. Build now.
- **Explicit `IProbe`** — a named zero-volt series ammeter component; `I(IP1)` reads its branch current.
  Adds one branch to the MNA solve. Build now. (`IProbe` `.cnl` grammar: settle minimally at bring-up;
  semantics = ideal 0 V series element exposing its branch current.)
- **`ds.I(...)` / `A.I(...)` resolve ONLY a named branch** (terminal or probe). **Node/net current access is
  not a public path.** If `INl` (device injected current at an interface node) remains, it is an *internal
  diagnostic* cube, not the measurement/test-facing current — and accessing current via the node axis must
  not be how `I(...)` works.
- The current-direction sign is **not special** — `conj(-1*I(...))` is plain scalar×cube; the engine has no
  current-direction awareness (measurements.md §2.3).
- **Fix the forbidden test access:** `tests/Engine.Tests/HarmonicBalance/Hero2Tests.cs` (and any sibling)
  currently does `-(Complex)ds["INl"][drainIdx, 1, si]` to get drain current — rewrite to the proper branch
  accessor `ds.I("M1:d", …)` (or the hero's actual instance:terminal). The test must obtain current by
  branch, exactly as a user would; node-indexed current access is removed from test code too.

## Correction 3 — Optional sweep axis (axes follow the analysis, not a template)
The result's axis set is determined by **what the analysis actually produced**, never a fixed template.
- **No sweep → no sweep axis.** Remove the `SweepValues() → yield return 0` dummy. A single-operating-point
  HB run yields `V` = `[node, harmonic]` (2 axes), not `[node, harmonic, 1]` with a fake `Pin=0`.
- **Accessors are axis-count-agnostic.** `ds.V(name, harm, …sweepSlices)` / `ds.I(branch, harm, …)` resolve
  name → harmonic → then apply whatever trailing slice args correspond to **however many** sweep axes exist
  (zero, one, or — after 5-6 — several). No accessor or engine code may assume exactly one sweep axis or a
  fixed 3-axis shape.
- **Update the Data CLAUDE.md wording:** it currently describes `V` as `{node, harmonic, Pin}` as if Pin is
  always present, with 3-slot slice examples. Reword to: the base HB cube is `{node, harmonic}` (single-tone)
  / `{node, mixIndex}` (two-tone); **sweeps add axes** (Correction 3 + 5-6); with no sweep there is no sweep
  axis. Keep the index-only / end-exclusive slice semantics unchanged.
- **Forward-compatibility note (do NOT build):** a future v2 DC analysis would have neither a harmonic nor a
  sweep axis (`V` = `[node]`). Out of scope now, but the axis-count-agnostic construction + accessors must
  not preclude it. Mention as a note; build nothing for it.

## Then — Phase 5-6: composable nested parametric sweep (the follow-on gate)
On the corrected base, build the generic sweep as a **composable wrapper, N nested sweeps → N axes**:
- A sweep **wraps an analysis (or another sweep)** and **prepends one named axis** to every cube in the
  DataSet, re-running the inner thing per point and stacking results. Recursive/composable — NOT a single
  hardcoded outer loop. Zero sweeps → zero added axes (Correction 3); N nested → N added axes.
- Netlist syntax: an ordered list of sweep specs (outer→inner); each names a variable and its values.
- Composes with the existing inner analysis sweeps (e.g. an outer temperature sweep × the HB power sweep)
  without double-counting an axis.
- Verify: a 2-variable nested sweep over a hero produces a cube with two added axes, correctly ordered, and
  the measurement accessors slice them positionally (axis-count-agnostic, Correction 3).

## Acceptance
1. **Linear back-solve:** linear-interior `V(node)` and `I(branch)` resolve via lazy, cached back-solve from
   the retained per-harmonic linear system; interface back-solved current matches the stored device current
   (KCL cross-check) at a hero point.
2. **Branch-current addressing:** `I(instance:terminal)` and `I(IProbe)` both work; `I(...)` resolves only a
   named branch; node-indexed current is not a public/measurement/test path; Hero 2 test rewritten to the
   branch accessor.
3. **Optional sweep axis:** no dummy sweep axis; a no-sweep HB run yields a 2-axis `V`; accessors handle
   0/1/N sweep axes; Data CLAUDE.md wording corrected; v2-DC noted (not built).
4. **5-6 composable sweep:** N nested sweeps → N axes; recursive wrapper; composes with inner sweeps; a
   2-variable nested sweep verified on a hero.
5. **Values unchanged:** every hero regression (Hero 1–5, loadpull, pursuit) passes with identical numbers;
   the FOM measurement-vs-engine cross-check (5-4) still holds. `dotnet build`/`dotnet test` green; Phases
   1–4 intact.

## Guardrails
- Implement the decisions; do not redesign. Lazy back-solve (Option A), not eager expansion. Current by
  named branch only. Axes follow the analysis (no fixed template). Sweep composable from the start.
- **Values must not change** — corrections re-house/extend access; they must not alter any computed number.
  A moved number = stop and report.
- Reuse the per-harmonic linear factorization for the back-solve; don't refactor or re-stamp the linear
  network from scratch.
- `INl` may persist as an internal diagnostic, but it is not the current accessor — keep it out of the
  public/measurement path.
- Sub-gate: Correction 1 → 2 → 3 → 5-6, tests green between; focused filters, report-and-stop per step,
  don't run the full suite into the output limit.
- Update `src/Core/Data/CLAUDE.md` (axis-set wording + branch-current accessor), `src/Engine/HarmonicBalance/
  CLAUDE.md` (linear back-solve, optional sweep axis), and the loadpull CLAUDE.md as touched.

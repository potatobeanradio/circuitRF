# Phase 1 — Implementation Brief (for Claude Code / Sonnet)

**Goal of Phase 1:** the *spine*. Read a hierarchical, parameterized circuit from a `.cnl` netlist,
resolve all variables and cell parameters with cycle detection, flatten the hierarchy into an
elaborated netlist with numbered nodes, and print it from the CLI. **No numerics yet** — no MNA, no
solve, no S-parameters. This phase produces the *data structures and the expression engine* the rest
of the simulator is built on.

> Read first, in this order: root `CLAUDE.md`, then `src/Core/CLAUDE.md`, then
> `docs/design/data-model.md` (§1–§3, §8, §10) and `docs/design/expressions.md` (whole doc).
> Those are authoritative. Where this brief and a design note disagree, **the design note wins** —
> and stop and flag it rather than guessing.

---

## Scope — build exactly these, nothing beyond

1. **Expression engine** (`src/Core`, pure managed C#, no UI/platform deps) — per `expressions.md`:
   - Tokenizer (§3) → **Pratt parser** for the precedence table (§4) → AST (§5) → evaluator.
   - **Never string substitution.** Parse once, evaluate many (cache the AST on its owner).
   - Value model **Real / Complex / Bool** (§6) with the promotion rules; ordering comparisons
     real-only; equality on Real and Complex; `if(cond,then,else)` and `? :` short-circuit to the
     selected branch and take that branch's kind.
   - Constants `j`, `pi`, `e`; the built-in function set (§7).
   - **Units** at the assignment level as a linear scale (§8 table); `dB`/`dBm` are *not* units.
   - **User-defined functions** as named lambdas, arbitrary arity (§11).
   - **Cycle detection** (§10): resolving-stack + memoization; report the chain (`a → b → a`);
     never hang. Spans variables, cell-parameter defaults, instance overrides, and the
     user-function call graph.
   - Error handling per §15 (cycle, unresolved name, type error, arity/unknown function,
     domain/numeric) — always name the offending text; never swallow into a silent zero/NaN.
   - **Do NOT build AD/FD** (§12) this phase — that is Phase 3. Build the AST/evaluator it will
     later reuse, but no derivatives now.

2. **Design-layer model** (`src/Core`) — per `data-model.md` §2:
   - `Library`, `Cell`, `Instance`, `TestBench`, `ParameterDeclaration`, `ParameterAssignment`,
     `Variable`. (Analyses/measurements: parse and attach to `TestBench` as data, but they are
     **not executed** this phase. A minimal `Analysis`/`Measurement` record that round-trips is
     enough; no solving.)
   - Honor the invariants: `Cell` is reusable and never holds analyses; analyses + measurements
     attach to `TestBench`. `ComponentModel` is the device base name (no `IDevice`).

3. **Elaboration** (`src/Core`) — per `data-model.md` §3:
   - Flatten depth-first; resolve parameters/variables via the expression engine with the
     **structural scope chain** (`expressions.md` §9): override → parent scope, default → own scope;
     local-then-global resolution; no upward/sideways reach.
   - Produce `ElaboratedNetlist` (`ElaboratedComponent` list + `NodeMap`); **ground = 0**; internal
     nets uniquified by instance-path prefix; node names carry the instance path (`X1.drain`).
   - Resolved parameter values are **kinded** (`Real` or `Complex`), not forced complex.
   - Compute `NonlinearComponents`/`NonlinearNodes` sets (cheap; based on `ComponentModel.Kind`).
     There are no nonlinear models yet, so these will be empty — wire the mechanism, not the models.

4. **`.cnl` reader** (`src/Core`) — per `data-model.md` §10:
   - Parse comments (`;`), variable assignments, `define … parameters … end` cell blocks, primitive
     lines, and cell-instance lines.
   - **Skip unknown header/comment lines** so real-world exports load.
   - **Analysis/measurement directive grammar is DEFERRED** (data-model §10): parse the lines into
     opaque/round-trippable records, but do not invent the full directive grammar — flag if a test
     needs it.
   - The legacy-dialect `if…then…else…endif` → `if(cond,then,else)` importer is **not** required this
     phase unless a committed fixture needs it; if so, flag before building.

5. **CLI** (`src/Cli`) — a headless driver that reads a `.cnl`, elaborates it, and **prints the
   elaborated netlist** (components with instance paths, resolved kinded parameter values, node
   numbers). This is the Phase-1 test harness and the visible proof the spine works.

**Explicitly OUT of scope this phase:** MNA/`MnaSystem`, any solve, S-parameters, DC, HB, the
`DataSet`/`DataCube` runtime (the result model is Phase 2+), device numeric behavior (`Stamp`/
`Evaluate`), AD/FD, RfCore (Phase 1 does **not** depend on it), and the GUI.

---

## Acceptance criteria (the exit gate)

1. A hierarchical, parameterized circuit **round-trips**: `.cnl` file → design model → elaboration →
   printed elaborated netlist, with parameters resolved top-down correctly (an instance override in
   the parent scope reaches the child, e.g. `X1 … C2=C2` passes the parent's `C2` down).
2. **Cycle-detection fixtures pass:**
   - The valid multi-hop chain from `recursion.log` (`C2 = gizmo`, `gizmo = funtimes`,
     `funtimes = 2`) **resolves to `2`**.
   - A synthetic cyclic fixture (`a = b`, `b = a`) is **reported as a cycle with its chain, and does
     not hang**.
3. Unit tests cover: tokenizer/parser (precedence incl. `^` right-assoc and `-2^2 == -4`), the
   Real/Complex/Bool value model and promotion, `if`/ternary, user functions, the units table,
   scope resolution (override-in-parent vs default-in-own), and the error cases in `expressions.md`
   §15.
4. `dotnet build` and `dotnet test` are green on the dev machine. (CI on 3 OSes can follow; not a
   blocker for the phase exit, but stand it up if convenient.)

---

## Test fixtures (`testdata/`)

- Use the worked `.cnl` example in `data-model.md` §10 (the `MyPiCell` Pi-network instanced twice)
  as the primary round-trip fixture.
- Add the `recursion.log` valid-chain fixture (resolves to `2`) and the synthetic `a=b,b=a` cyclic
  fixture (must be reported).
- The original Swift prototype's legacy `.log` netlists (in the uploads) are **reference only** — do
  not transliterate them; author clean `.cnl` fixtures.

---

## Guardrails / working style

- **No C# outside the Phase-1 scope above.** If the work seems to need an out-of-scope piece, stop
  and flag it — don't build ahead into Phase 2.
- **The Swift prototype is reference, never a transliteration source.** circuitRF deliberately
  departs from it (managed AST not `NSExpression`; structural scope not string-triple keys; engine-
  owns-matrix; `i,q,dg,dc` contract). The design notes capture the intended departures.
- Keep `src/Core` free of `src/Engine`/`src/Ui` dependencies (one-way layering).
- When a design note leaves something open or a fixture needs a deferred piece (e.g. directive
  grammar), **flag it for an Opus/Chat design decision** rather than improvising the design.
- Update `src/Core/CLAUDE.md` at the end of the phase if implementation reality diverged from it.

---

*Phase 1 builds the spine with no numerics. Phase 2 (linear engine: MNA, DC, S-parameters, and the
RfCore extraction) follows once this round-trips and the fixtures pass. See `docs/Development_Plan.md`
§4 for the full roadmap and the Opus-vs-Sonnet / Chat-vs-Code split.*
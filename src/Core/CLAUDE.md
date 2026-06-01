# Core — local conventions

Standing instructions for `src/Core` (the design layer, the elaboration layer, the expression
engine, and the `ComponentModel` types). Read with the root `CLAUDE.md`. Design notes:
`docs/design/data-model.md`, `docs/design/expressions.md`. `Data/` and (numeric `ComponentModel`
behavior) the engine have their own notes.

## What lives here
- **Design layer:** `Library`, `Cell`, `Instance`, `TestBench`, `ParameterDeclaration`,
  `ParameterAssignment`, `Variable`, `Analysis` subtypes, `Measurement`. Editable, serializable
  (`.cnl` + JSON), human-readable.
- **Elaboration layer:** flatten hierarchy → `ElaboratedNetlist` (`ElaboratedComponent` list +
  `NodeMap`), resolving parameters/variables and numbering nodes.
- **Expression engine:** tokenizer → Pratt parser → AST → evaluator. Serves variables, cell
  parameters, the SDD, and measurements.
- **`ComponentModel`** base + `Devices/` (the numeric behaviors; their stamping/evaluation contract
  is detailed in `data-model.md` §5 and exercised by the engine).

## Key type distinctions — do not blur
- **`Cell` vs `TestBench`.** A `Cell` is a **reusable** definition (ports, parameter interface,
  contents) that gets instanced. A `TestBench` is the **one** thing you simulate (top cell +
  globals + analyses + measurements). **Analyses and measurements attach to the `TestBench`, never
  to a `Cell`.**
- **`ComponentModel`, not "Device".** The single base for passive **and** active parts. "Device" is
  reserved for its RF meaning (an active part). A resistor and a FET are both `ComponentModel`s.
- **Three layers flow one way:** design → elaboration → numeric. Nothing in `src/Core` may depend
  on `src/Engine` or `src/Ui`. The GUI edits the design layer; it never hands a design-layer object
  to the engine — always elaborate first.

## Expression engine — non-negotiables
- **Never string substitution.** Real tokenize → Pratt-parse → AST → evaluate. (This replaces the
  prototype's NSExpression/NSPredicate/regex path.)
- **Parse once, evaluate many.** Cache the AST on the owning `Variable`/parameter/SDD/`Measurement`;
  the SDD hot path (per time sample × Newton step × sweep point) must allocate no garbage.
- **Kinded values: Real / Complex / Bool.** A resolved variable or parameter is Real **or** Complex
  (not forced complex) — most component values are Real, impedances Complex. Ordering comparisons
  are **real-only**; SDD equations are real-only time-domain (no `j`).
- **Cycle detection is mandatory** and spans variables, cell-parameter defaults, and instance
  overrides. Report the offending chain (e.g. `a → b → a`); never recurse without the in-progress
  guard. Fixture `recursion.log` (a valid multi-hop chain → resolves to `2`) and a synthetic
  cyclic fixture (`a=b, b=a` → must be reported) are the Phase-1 tests.
- **Scope is structural,** not string-keyed: globals at the base; each cell instance pushes a scope
  binding parameters. **Override expression evaluates in the PARENT scope; default in the cell's
  own scope.** Resolution is local-then-global; no upward/sideways reach.

## Elaboration
- Flatten depth-first; primitives emitted, cells recursed with a fresh scope.
- Resolve every expression to a kinded value (units applied here), with cycle detection.
- Number nodes: ports map onto parent nets; internal nets uniquified by instance-path prefix;
  **ground = 0**. Node names carry the instance path (`X1.drain`) — this is what measurement paths
  resolve against, so keep it stable.
- Compute `NonlinearComponents` / `NonlinearNodes` (the HB partition seed) here.
- Numbering is stable + unique; the fill-reducing permutation for the solve is the **engine's** job,
  not the elaborator's.

## `.cnl` reader
- Vendor-neutral hierarchical netlist; maps directly onto the design layer. JSON carries the same
  logical model.
- **Skip unknown header/comment lines** so real-world exports import cleanly; committed fixtures are
  clean `.cnl`.
- The **VendorA importer** (a separate front-end, Phase 2) translates legacy `if…then…else…endif`
  → canonical `if(cond,then,else)` at import time, so the engine grammar stays single-form. (Native
  `.cnl` only ever uses the canonical form.)
- Analysis/measurement **directive grammar is deliberately deferred** (data-model §10) — nail it
  down before implementing those lines; the circuit/cell/variable lines are settled.
- **SnP / frequency-domain N-port reference node (N-or-N+1 rule).** A frequency-domain N-port
  component (SnP block, impedance block, TLIN, user freq model) lists either **N nets** (each port
  referenced to ground, node 0) **or N+1 nets**, in which case the **last net is the common
  reference node** for all ports (the floating-block case). The reader validates node count against
  `NumPorts`: `== NumPorts` or `== NumPorts + 1`, else error. The reference node is recorded on the
  component (a `ReferenceNet`, ground when absent) and the model uses it in its own stamp
  (linear-engine §4.1). This rule does **not** apply to 2-terminal R/L/C. SnP line fields:
  `File` (relative paths resolved against the `.cnl` file's dir; absolute as-is), `Type`
  (v1: `"touchstone"` only — **hard-error on any other value**; extensible, future `"datacube"`),
  `InterpMode` (`"spline"` default | `"linear"`), `InterpDom`, `ExtrapMode` (`"clamp"` default |
  `"extrapolate"`); `Temp` and passivity/noise flags are parsed and ignored.

## Phase 1 deliverable — COMPLETE (2026-05-30)
Expression engine + elaboration + `.cnl` reader, validated by the cycle-detection fixtures.
**Phase 1 does not need RfCore** — it is built and tested standalone.

### Implementation notes (reality vs. design)
- `if(...)` keyword is handled in the **Parser** (produces `ConditionalExpr`), not in `Evaluator.EvalCall`.
- `Evaluator.InjectResolved(scopeDebugName, name, value)` lets the Elaborator inject
  pre-resolved override values into the memo cache without round-tripping through `ToString()`
  (avoids breakage on Complex values).
- Left-assoc operators use `rbp = lbp + 1` in the Pratt table; right-assoc (`^`) uses `rbp = lbp - 1`.
- Analysis/measure `.cnl` lines → `RawDirective { Kind; RawLine }` on `TestBench`. Typed `Analysis`
  subclasses (`SParameterAnalysis`, etc.) are defined but not populated by the Phase-1 reader.
- Top-level instances in a `.cnl` (outside any `define` block) live directly on `TestBench.Instances`
  (no synthetic wrapper cell).
- `ComponentModel.Stamp` uses `object` placeholders for `mna` and `c` — types resolved in Phase 2.

## Phase 2 Step 1 deliverable — COMPLETE (2026-05-31)
SnP/Touchstone block end-to-end: `.cnl` reader parses `SnP:` lines, elaboration creates `SnpModel`,
`SnpModel.Stamp` performs Z-expansion into a real `MnaSystem`. RfCore is now a dependency of Core.
Gate: 4 tests in `tests/Engine.Tests/Linear/SnpStampTests.cs` — all green.

### Implementation notes (reality vs. design)
- `ValueKind.String` was added to `Value` (storage-only; no operators, no coercions). This is the
  mechanism for SnP configuration params (`File`, `Type`, `InterpMode`, `ExtrapMode`). The tokenizer
  lexes `"..."` as `StringLiteral`; the parser produces `StringLiteralExpr`; the evaluator returns
  `Value.String(...)`. A String value is a type error in any arithmetic context.
- `Instance.RefNetBinding` (nullable string net name) carries the N+1 floating reference node for
  frequency-domain N-ports. `null` = ground. The elaborator resolves it to `ElaboratedComponent.ReferenceNode`.
- `IMnaContext` interface lives in `CircuitRF.Core` (not Engine) because `ComponentModel.Stamp` must
  be able to call it. `MnaSystem` in `CircuitRF.Engine` implements it.
- `ComponentModel.Stamp(IMnaContext mna, ElaboratedComponent c, double omega)` — `object` placeholders
  replaced with real types.
- The elaborator now resolves parameters **before** creating the model (order swapped). This allows
  `ComponentModelFactory.TryCreate(typeName, params)` to construct `SnpModel` with its file path
  and settings baked in.
- `CnlReader` stores `_sourceDirectory` (from `ReadFile`); relative `File=` paths are resolved to
  absolute at parse time and re-stored in the `ParameterAssignment.Expression` as a string literal.
- `TokeniseLine` in CnlReader now respects quoted regions so `key="value with spaces"` stays one
  token.

## Ask before
- Changing the `.cnl` or JSON format (round-trip + interop).
- Changing the scope/binding rule or the kinded-value model (ripples into the engine and SDD).
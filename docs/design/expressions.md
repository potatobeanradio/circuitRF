# circuitRF — Expression Engine Design

**Status:** Draft (rev 2) for review · **Date:** 2026-05-30
**Reads with:** `docs/design/data-model.md` (§2.2 parameters/scope, §5 SDD, §8 expression overview), `docs/design/measurements.md` (measurement operands), `docs/PRD.md` (§7).
**Defers to:** `docs/design/harmonic-balance.md` (how `dg`/`dc` feed the Jacobian), `docs/design/measurements.md` (cube-operand accessors).

One expression engine serves four consumers. This note specifies its pipeline, grammar, value model, scope and cycle handling, user-defined functions, and the automatic-differentiation scheme that yields the SDD's `dg`/`dc`. It defines *behavior*, not C#. No code is written until this is approved.

---

## 1. Consumers — one engine, four uses

| Consumer | What it evaluates | Result | Differentiated? |
|---|---|---|---|
| **Global variables** | a `TestBench` variable's expression | Real or Complex | no |
| **Cell parameters** | a parameter default or an instance override | Real or Complex | no |
| **SDD device equations** | `i = f(v)`, `q = f(v)` over port voltages | Real (time-domain) | **yes** — `dg = di/dv`, `dc = dq/dv` |
| **Measurements** | a performance expression over result cubes | Real or Complex | no |

The first two are the elaboration-time use (resolve a circuit to numbers). The SDD use is the numeric-layer hot path (evaluated per time sample, per Newton step). The measurement use extends operands to cube quantities (§13). The grammar, operators, and functions are identical across all four; only the available *operands* and whether *derivatives* are taken differ. A resolved variable or parameter carries a **kind** — `Real`, `Complex`, or (for a few component parameters like a file path or a mode selector) **`String`** — the numeric kinds matching the `DataKind` distinction the result model uses (§6); most component values (`R`, `L`, `C`) are Real, impedances are Complex, and a handful of parameters (`File`, `Type`, `InterpMode`, … on the SnP block) are String.

---

## 2. Pipeline — parse once, evaluate many; never string substitution

```
source text → Tokenize → Parse → Ast → (bind to scope) → Evaluate → Value
```

The engine builds a real **AST** and evaluates it. It does **not** do string substitution. This is a deliberate departure from the prototype, which resolved variables by `replacingOccurrences` and evaluated via `NSExpression`/`NSPredicate` + regex — Apple-only, and fragile (it had to sort variable names longest-first to avoid substring collisions, and had no cycle guard). The circuitRF engine is **pure managed C#** with no platform dependency.

An expression is **parsed once** into an AST and cached; evaluation re-runs the AST against a scope. For the SDD this matters: the device equation is parsed when the model is built, then evaluated thousands of times (every time sample × Newton iteration × sweep point) without re-parsing.

---

## 3. Lexical grammar (tokens)

- **Number** — decimal, optional exponent: `50`, `1.0`, `1e-9`, `2.5E3`.
- **String literal** — a double-quoted run of characters: `"touchstone"`, `"path/to/file.s2p"`, `"spline"`. Lexes to a `String` value (§6); **storage only**, no string operators. Used for component parameters that are genuinely textual (the SnP block's `File`, `Type`, `InterpMode`, `InterpDom`, `ExtrapMode`).

  **SnP `File` path resolution:** `File` may be a relative or absolute path. The Elaborator resolves a relative path against the **workspace root** (`Path.GetDirectoryName(CurrentWorkspacePath)`) — the same directory that holds the `.cws` and the `results/` folder. Absolute paths are used unchanged. Cross-platform: `Path.IsPathRooted`/`Combine`/`GetFullPath`, with `\`→`/` separator tolerance so a Windows-authored netlist ports to macOS/Linux without edits. When no workspace is open (CLI runs or in-memory elaboration with `BaseDirectory = null`), a relative path is left as-authored and resolves against the process CWD (legacy behavior).

  **Parameter Editor (Browse / Show):** the Browse picker stores a **workspace-root-relative** path (forward-slash, portable) when the picked file is within the workspace subtree or ≤ 2 directories above the root; otherwise it stores the absolute path (`SnpPathPolicy.ToStored`). The **Show** (reveal-in-OS) command resolves a relative `File` against the same workspace root — identical to the engine's resolution base — so what Browse stores is what Show reveals and what the engine loads.
- **Identifier** — `[A-Za-z_][A-Za-z0-9_]*`, used for variable/parameter/argument names and function names. Hierarchical paths in measurements use `.` between identifiers (`X1.drain`) — see §13.
- **Imaginary unit** — `j` is a reserved constant equal to `(0, 1)`; imaginary values are written `j*4`, `2 + j*3` (matching the prototype's `j*` convention). `pi` and `e` are reserved constants.
- **`freq` — reserved injected keyword.** Lowercase `freq` is the **simulator's current stamping frequency** (in Hz), injected into scope by the engine when it evaluates a frequency-dependent component value (the `Z_Port` impedance expression, linear-engine §4; any future `Z(freq)`/`Y(freq)` model). It is **read-only and reserved** — a user may **not** name a variable `freq`. It is in scope only for component-value expressions the engine stamps per-frequency; it is **not** available in SDD time-domain equations (those see port voltages `_v`, not frequency) nor in ordinary variable/parameter expressions evaluated at elaboration. Distinct from the user-set **`Freq`** parameter (capital F) on a voltage source, which names the tone a source drives at (linear-engine §4.4); the commensurability check (harmonic-balance §3) verifies each source's `Freq` lands on the grid the engine stamps `freq` at.
- **Reserved names.** Constants (`j`, `pi`, `e`), the injected `freq`, and all built-in function names (`sin`, `cos`, …, `real`, `imag`, `abs`, `mag`, `phase`, `phase_rad`, `polar`, … §7) are reserved — a user variable/parameter/argument may not shadow them.
- **Operators** — `+ - * / ^`, `< <= > >= == !=`, `&& || !`, `? :`, and `( ) ,`.
- **Whitespace** separates tokens and is otherwise ignored.

Units are **not** lexical tokens of the expression grammar — they attach at the assignment level (§8).

---

## 4. Operators & precedence

Lowest-binding (evaluated last) to highest-binding (tightest):

| Level | Operators | Associativity | Notes |
|---|---|---|---|
| 1 | `? :` (ternary) | right | `cond ? a : b`; equivalent to `if(cond, a, b)` |
| 2 | `\|\|` | left | logical OR |
| 3 | `&&` | left | logical AND |
| 4 | `==`  `!=` | left | equality (works on Complex) |
| 5 | `<`  `<=`  `>`  `>=` | left | ordering — **real operands only** (§6) |
| 6 | `+`  `-` (binary) | left | |
| 7 | `*`  `/` | left | |
| 8 | `+`  `-`  `!` (unary prefix) | right | |
| 9 | `^` (power) | right | binds tighter than unary minus: `-2^2 == -(2^2) == -4`; `2^3^2 == 2^(3^2)` |
| 10 | function call, `( )`, atoms | — | highest |

A **Pratt (precedence-climbing) parser** is the recommended implementation — it expresses this table directly and is small.

---

## 5. AST node types

- `Number(double)` — real literal.
- `Const(name)` — `j`, `pi`, `e`.
- `Ref(name)` — a variable, parameter, or function argument; resolved against the scope (§9). In measurements, also an accessor call like `V(path)` (§13).
- `Unary(op, x)` — `-x`, `+x`, `!x`.
- `Binary(op, a, b)` — `+ - * / ^`.
- `Compare(op, a, b)` — `< <= > >= == !=`.
- `Logic(op, a, b)` — `&& ||`.
- `Conditional(cond, then, else)` — from `if(...)` or `? :`.
- `Call(name, args)` — built-in or user-defined function.

---

## 6. Value model

Evaluation produces a value of one of four kinds:

- **Real** — a `double`. The kind of arithmetic on real operands (component values like `R`, `L`, `C`).
- **Complex** — `System.Numerics.Complex`. Produced when the imaginary unit `j` (or any complex value) enters, or by an operation on reals that is mathematically complex (e.g. `sqrt` of a negative). Impedances and the `Z[i,j]` block resolve here.
- **Bool** — the result of comparisons and logical operators; consumed only by `if`/`? :` conditions.
- **String** — a string literal (§3). **Storage only** — there are no string operators, no concatenation, no string comparison, and no implicit string↔number coercion. A `String` exists solely so a component can carry a genuinely textual parameter (the SnP block's `File`, `Type`, `InterpMode`, `InterpDom`, `ExtrapMode`). It flows through the same elaboration path as any other parameter (resolve → store on the `ElaboratedComponent`), so the elaborator, factory, and `Instance` need no special case.

**A resolved variable or parameter is therefore Real, Complex, *or* String** — it carries its kind. Numeric values use the same `Real`/`Complex` `DataKind` the result model uses (data-model §7) rather than forcing every `R=50` to be complex-with-zero-imaginary; this honesty lets a model validate its inputs (a resistor rejects a complex `R`) and lets the design layer round-trip `50` rather than `50+j0`.

**Promotion rules:**
- Real literal → Real; `pi`/`e` → Real; `j` → Complex; a quoted literal → String.
- Real ∘ Real → Real for `+ - * / ^`, **unless** the operation is mathematically complex (`sqrt(-x)`, non-integer power of a negative) → then Complex in a parameter/variable context, or a domain error in the real-only SDD context (below).
- Real ∘ Complex → Complex; Complex ∘ anything → Complex.
- Most functions preserve kind for real input (`sin`, `tanh`, `exp`, `abs`, …); `sqrt`/`log` of a negative real promote to Complex (parameter context) or error (SDD context).
- **String participates in no operators.** A `String` reaching any arithmetic, comparison, logical, or function operator is a type error (§15). It can only be produced by a string literal and consumed by a component reading its own textual parameter.

**Rules:**
- A **parameter, variable, or measurement that resolves to `Bool`** is a type error (a condition can't be a component value).
- A **`String` reaching a numeric stamp is a type error.** `String` is valid only where a component explicitly asks for a textual parameter (`R = "foo"` fails: a resistor needs a Real). The validity restriction mirrors how `Bool` is valid only as an `if` condition.
- **Ordering comparisons** (`< <= > >=`) require **Real** operands; a Complex operand is an error (complex numbers are unordered). **Equality** (`== !=`) is defined on Real and Complex.
- `!`, `&&`, `||` require `Bool` operands.
- `if(cond, then, else)` / `cond ? then : else`: `cond` is `Bool`; the result is the **selected** branch (the other is not evaluated — short-circuit, which also matters for AD, §12). The result kind is that branch's kind.

For the enumerated string parameters (`Type`, `InterpMode`, `ExtrapMode`), the **component** validates the allowed values (e.g. `SnpModel` rejects a `Type` other than `"touchstone"`) and may narrow the string to an internal enum after retrieval — the value model only needs to *carry* the string; whether a model then narrows it to an enum is the model's business. `File` is free-form.

**SDD constraint (real-only context).** SDD equations operate on **real** time-domain port voltages and must produce **real** current/charge. This is the context where `sqrt`/`log` of a negative is a domain error rather than a promotion, and where `j` is disallowed (a complex time-domain current is physically meaningless). The `j` constant and complex promotion are for frequency-domain parameter expressions (impedances, the `Z[i,j]` block), not for an SDD current/charge equation. `String` never appears in an SDD equation.

---

## 7. Constants & built-in functions

**Constants:** `j = (0,1)`, `pi`, `e`.

**Built-in functions** (each defined on Complex unless noted; those used in SDD equations must stay real-valued for real inputs):

- Trig: `sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `atan2(y,x)`.
- Hyperbolic: `sinh`, `cosh`, `tanh`.
- Exp/log/power: `exp`, `log` (natural), `log10`, `sqrt`, `pow(x,y)` (same as `x^y`), `abs`.
- Misc: `min(a,b)`, `max(a,b)`, `sign(x)`.
- Conditional: `if(cond, then, else)`.
- **Complex extraction** (Complex-or-Real → **Real**): `real(z)`, `imag(z)`, `abs(z)` (modulus; `|x|` for a real argument), `mag(z)` (**alias of `abs`** — RF spelling of the same modulus), `phase(z)` (angle in **degrees**), `phase_rad(z)` (angle in **radians**).
- **Complex construction** (Real, Real → **Complex**): `polar(mag, phase_deg)` = `mag·(cosθ + j·sinθ)` with θ in **degrees**. The inverse of `mag`/`phase`; e.g. `polar(0.1, 10)` is the phasor 0.1∠10°. (No `polar_rad` — degrees is the designer-facing entry convention; `phase_rad` exists only as the radian extractor.)

The extraction/construction family is consistent with the result-model **cube transforms** of the same name (`.real`/`.imag`/`.mag`/`.phase` in `measurements.md`): same operation, same units (phase in degrees), one on a scalar value, the other on a whole cube. **`phase`/`phase_rad` and the `.phase` cube transform must agree on units (degrees for `.phase`/`phase`).** Note `abs` is the canonical modulus (extending its real `|x|` meaning to complex), with `mag` a pure alias.

`dB(...)` and `dBm(...)` are **measurement** functions (§13), not general-expression built-ins, because they are logarithmic and tied to power/wave semantics. They are also why `dB`/`dBm` are **not** unit suffixes (§8).

The set is intentionally close to what other tools' equation-defined devices provide, so hero SDD equations transcribe cleanly (§14). Functions beyond that common set are allowed for non-hero use but should be added knowingly.

---

## 8. Units

Units attach at the **assignment level**, not inside the expression grammar: an assignment is `name = <expression> [unit]`, and the unit **scales the resolved value** by a linear factor. This matches the prototype (`Parameter.UnitString` scaling the evaluated expression) and the `.cnl`/imported-netlist lines (`L=L1 nH`, `Z=50 Ohm`, `M=0.5 pH`).

A representative scale table (extensible):

| Domain | Units (→ SI factor) |
|---|---|
| Generic SI prefixes | `T`=1e12, `G`=1e9, `M`=1e6, `k`=1e3, `m`=1e-3, `u`=1e-6, `n`=1e-9, `p`=1e-12, `f`=1e-15 |
| Frequency | `Hz`=1, `kHz`, `MHz`, `GHz`, `THz` |
| Inductance | `H`=1, `mH`, `uH`, `nH`, `pH`, `fH` |
| Capacitance | `F`=1, `mF`, `uF`, `nF`, `pF`, `fF` |
| Resistance | `Ohm`=1, `kOhm`, `MOhm` |
| Length (TLIN) | `m`=1, `mm`, `um`, `mil`=2.54e-5 |
| Angle | `deg`=π/180, `rad`=1 |

**Logarithmic units are not unit-suffixes.** `dB`/`dBm` are not linear scale factors, so they are handled by functions (`dB(...)`, `dBm(...)`), never as a trailing unit on a value.

**Deferred:** units *inside* expressions (per-term units, unit algebra/checking). v1 uses a single assignment-level unit. This is the PRD §7 "units-in-expressions … grows later" item.

---

## 9. Scope & name resolution

Scope is **structural**, not string-keyed. This replaces the prototype's `(name, subcircuit-name, subcircuit-instance)` triple keys (which it had to sort longest-first to avoid collisions) with a scope chain that is correct at arbitrary depth by construction.

**Scopes:**
- **Global scope** — the `TestBench`'s global variables. Visible everywhere (PRD §7: globals usable anywhere).
- **Cell-instance scope** — created when an instance of a cell is elaborated. It binds the cell's **parameters** to values and holds the cell's own **variables**.

**Binding a cell instance `X` of cell `C` inside parent `P`** (this is the crux of hierarchical parameter passing, data-model §2.2):
- For each parameter of `C`: its value is the **instance override expression evaluated in `P`'s scope** if `X` overrides it, otherwise the **default expression evaluated in `C`'s own instance scope**. *(Override → parent scope; default → own scope. This is the rule that makes `X1 … C2=C2` pass the parent's `C2` down.)*
- `C`'s own variables are evaluated in `C`'s instance scope.
- `C`'s component values and the overrides it passes to *its* sub-instances are then evaluated in `C`'s instance scope.

**Resolution order for a name inside a cell:** local cell variables/parameters first, then global. A cell does **not** see its parent's local variables (only what was passed in as parameters) or any sibling's — this is the encapsulation that keeps a cell's meaning independent of where it is instanced.

---

## 10. Cycle detection (required)

A name's expression may reference other names; circuitRF **must detect and reject cycles** (PRD §7; the `recursion.log` fixture is named for this). Algorithm:

1. Maintain a **resolving stack** of names currently being resolved.
2. To resolve `name`: if `name` is already on the stack → **cycle**; report the chain from the first occurrence to the repeat (e.g. `a → b → a`). Otherwise push `name`, evaluate its expression (which may recurse into other names), pop, and **memoize** the result.
3. Memoization makes resolution O(graph) and avoids re-evaluating shared sub-expressions.

This spans the whole dependency graph: global variables, cell-parameter defaults, and instance overrides are all named expressions resolved by the same mechanism, so a cycle through any mix of them is caught.

Test fixtures: `recursion.log` carries a valid multi-hop chain (`C2 = gizmo`, `gizmo = funtimes`, `funtimes = 2`) that must resolve to `2`; a companion synthetic fixture introduces a true cycle (`a = b`, `b = a`) that must be reported, not hang.

---

## 11. User-defined functions

A user may define a function whose body is an expression in the same language:

```
gm(vgs, vth) = beta * tanh(vgs - vth)
```

- Stored as a named lambda with an ordered parameter list and arbitrary arity (PRD §7).
- A call binds arguments positionally into a fresh local scope whose parent is the definition's scope (so a user function may reference globals, not the caller's locals).
- User functions compose with built-ins and with each other; recursion among user functions is subject to the same cycle detection (§10) applied to the call graph (a user function that calls itself unboundedly is rejected).

---

## 12. Differentiation for the SDD (`dg`, `dc`)

The SDD must return exact derivatives so harmonic balance gets a correct conversion-matrix Jacobian (data-model §5; harmonic-balance note). Three tiers, as decided:

### 12.1 Built-ins → closed form
Native models (FET/BJT/diode) supply analytic derivatives directly; they do not go through the expression-AD path. Best accuracy, continuous by construction.

### 12.2 SDD default → forward-mode automatic differentiation
For an SDD with port voltages `v = [v₁ … v_N]`, evaluate each port's current `i_p(v)` and charge `q_p(v)` carrying a **dual value**: `(value, grad[N])`, where `grad` is the gradient w.r.t. all port voltages.

- **Seeding:** `v_q` enters with `grad = e_q` (unit vector). One forward pass then yields, for each port current, both its value and the full row `[∂i_p/∂v₁ … ∂i_p/∂v_N]` → the conductance matrix `dg`. The same for charge → capacitance matrix `dc`. (Forward-mode with N-wide duals gives the whole Jacobian in one evaluation pass — appropriate since N, the device port count, is small.)
- **Propagation (chain rule):** each operator and function carries its derivative through the dual. Representative rules: `d(a·b)=a·db+b·da`; `d(a/b)=(b·da−a·db)/b²`; `d(xⁿ)=n·xⁿ⁻¹·dx`; `d(sin)=cos·dx`, `d(cos)=−sin·dx`, `d(tan)=(1+tan²)·dx`, `d(tanh)=(1−tanh²)·dx`, `d(exp)=exp·dx`, `d(log)=dx/x`, `d(sqrt)=dx/(2√x)`, `d(abs)=sign(x)·dx`.
- **Conditionals:** `if(cond, then, else)` evaluates `cond` (a `Bool`, no derivative), then propagates the **active branch's** value *and* dual. A piecewise SDD is therefore differentiable within each branch; the derivative is one-sided at a switching boundary, where a hard conditional may be genuinely discontinuous. This is the precise sense in which AD "handles `if`" — it differentiates the branch that is taken.

### 12.3 SDD fallback → finite difference
User-selectable per SDD model, for equations where the user prefers it or AD misbehaves at a boundary. Central difference (the prototype's `StepSize = 1e-4` is the known-good reference), with a configurable step (absolute, or relative to the operating point). FD shares the discontinuity caveat at switching boundaries (a difference straddling the boundary returns a large spurious slope) — so it is a fallback, not the default.

**Guidance (not a restriction):** for models that must stay smooth for tough convergence, prefer **soft switching** (`tanh`) over a hard `if`. Hard conditionals remain allowed.

---

## 13. Measurement extension (cube operands)

Measurements use this same engine, with operands extended from scalars to **cube quantities** and a set of **accessor functions** whose arguments are hierarchical paths or indices: `V(path)`, `I(path)`, `S(i,j)`, plus spectral/FOM functions (`harm`, `tone`, `Pout`, `PAE`, `IMn`, `dB`, `dBm`, reductions like `max`). Paths (`X1.drain`) resolve against the elaborated node map. The full accessor/function library, result typing (`Real`/`Complex`), the `@ analysis` binding, and IMn extraction are specified in `measurements.md`; this note owns only the core grammar those extensions build on.

---

## 14. Transcribability to other tools' equation-defined devices

The Hero-2/4/5 FET is an SDD whose *identical equations* are transcribed into other tools' equation-defined devices to generate the golden references (PRD §4). So the v1 function/operator set is kept within what those tools express for algebraic `i = f(v)` models (arithmetic, `tanh`/`exp`/`log`/trig, conditionals). Functions beyond that common core are permissible for non-hero models but must not be used in a hero SDD, or the cross-tool comparison breaks. This is a constraint on *which* features a hero model uses, not on the engine's capability.

---

## 15. Error handling

All reported with the offending text/name, never a silent zero or NaN swallow:
- **Cycle** — the dependency chain (§10).
- **Unresolved name** — the name and the scope it was sought in.
- **Type error** — `Bool` where Complex required (or vice-versa); ordering comparison on a complex operand; non-Bool `if` condition.
- **Arity / unknown function** — wrong argument count; call to an undefined function.
- **Domain/numeric** — `log(0)`, `sqrt` of negative in a real SDD context, division by zero: reported with context rather than propagated as Inf/NaN into the solver, where possible.

---

## 16. Deferred (grows later)

Per PRD §7's "built to extend without breaking v1 files": units *inside* expressions and unit algebra/checking; vectors/arrays as first-class operands in the *core* language (measurements already operate on cube quantities as an extension, §13); string operations; user-definable operators. None of these are needed by the five heroes.

---

## 17. Implementation notes

- **Lives in `src/Core`** (the elaboration layer depends on it; the SDD in the numeric layer reuses it). Pure, no UI, no platform API.
- **Pratt parser** for the precedence table (§4); a small hand-written tokenizer.
- **Parse-once / evaluate-many**: ASTs are cached on the owning `Parameter`/`Variable`/SDD/`Measurement`; the SDD's per-sample evaluation must allocate no garbage on the hot path (reuse dual buffers sized to N ports).
- **Replaces** the prototype's `NSExpression`/`NSPredicate`/regex path and its `evaluate_if_else` (`if…then…else…endif`). The legacy-dialect importer translates any legacy `if…then…else…endif` it encounters into the canonical `if(cond, then, else)` during import, so the engine grammar stays single-form.
- **Phase 1 deliverable** alongside elaboration: tokenizer, Pratt parser, evaluator, scope chain, cycle detection, user functions, and the units table — validated on the `.cnl` fixtures (the `recursion.log` chain resolving to `2`, and the synthetic cyclic fixture being reported). AD/FD for the SDD lands in Phase 3 (it is only needed once nonlinear devices exist), but the AST and evaluator it builds on are Phase 1.

---

## 18. Summary & open items

**Decided here (for review):**
- One managed AST engine (tokenize → Pratt-parse → evaluate), never string substitution.
- Value model: Real/Complex **kinded** numeric values (matching the result-model `DataKind`) + Bool conditions; ordering comparisons are real-only; SDD equations are real-only.
- Operator/precedence table (§4); constants `j`/`pi`/`e`; built-in function set (§7).
- Units are an assignment-level linear scale; `dB`/`dBm` are functions, not units; units-in-expressions deferred.
- Structural scope chain with override-in-parent / default-in-own-scope binding; local-then-global resolution; cycle detection with chain reporting.
- User-defined functions as named lambdas, arbitrary arity, cycle-checked.
- SDD derivatives: closed-form (built-ins) / forward-mode AD with N-wide duals (SDD default, differentiates the active `if` branch) / finite-difference (per-model fallback).

**Open items:**
1. FD step policy — absolute `1e-4` (prototype) vs relative-to-operating-point — confirm during Phase 3 against the hero SDD.
2. **Resolved** in `harmonic-balance.md` (§16, behavior specified in §4/§11): `log`/`sqrt`/etc. domain errors in an SDD during an overshooting HB iterate **clamp and warn** rather than hard-erroring, so continuation is not killed by a transient out-of-domain iterate; the warning is surfaced obviously to the user, naming the model and the offending operation.
3. Exact units table contents (§8) — the list is representative; finalize the full set when the `.cnl` reader is built.

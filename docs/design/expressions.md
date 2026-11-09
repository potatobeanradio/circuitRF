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

- **Number** — decimal, optional exponent: `50`, `1.0`, `1e-9`, `2.5E3`. An exponent is taken only when digits
  follow it, so `2e` is a number with an unknown suffix, not an empty exponent.
- **Quantity (unit literal)** — a number with a unit glued to it, **no whitespace**: `10um`, `1mil`, `2.4GHz`,
  `1.5nH`, `50Ω`, `10µm`, `1e-3mm`, `2u`, `48V`. It is ONE token (so a rename of a variable called `mil` can never
  touch the `mil` in `10mil`), the suffix being the whole identifier run after the number — `10mils` is the unknown
  suffix `mils`, never `10mil` and a name `s`. The suffix is normalised (`Ω`→`Ohm`, `µ`/`μ`→`u`) and must be a
  linear-scale unit of §8's table — including the bare SI prefixes — or one of the scale-1 base symbols `V`, `A`,
  `W`. An unknown suffix is a parse error naming it and the length units; a logarithmic one (`0dBm`, `3dB`) is a parse
  error saying a dB value is not a multiplier. `10j` keeps its meaning (the implicit imaginary unit is checked first;
  no unit starts with `j`). **Whitespace separates:** `2 GHz` is not a literal — it stays the assignment-level
  spelling of §8. A name that spells a unit is still a name where it is not glued to a number: `2*mil` references a
  variable `mil`. Brief: `brief-units-in-expressions.md`.
- **String literal** — a double-quoted run of characters: `"touchstone"`, `"path/to/file.s2p"`, `"spline"`. Lexes to a `String` value (§6); **storage only**, no string operators. Used for component parameters that are genuinely textual (the SnP block's `File`, `Type`, `InterpMode`, `InterpDom`, `ExtrapMode`).

  **SnP `File` path resolution:** `File` may be a relative or absolute path. The Elaborator resolves a relative path against the **workspace root** (`Path.GetDirectoryName(CurrentWorkspacePath)`) — the same directory that holds the `.cws` and the `results/` folder. Absolute paths are used unchanged. Cross-platform: `Path.IsPathRooted`/`Combine`/`GetFullPath`, with `\`→`/` separator tolerance so a Windows-authored netlist ports to macOS/Linux without edits. When no workspace is open (CLI runs or in-memory elaboration with `BaseDirectory = null`), a relative path is left as-authored and resolves against the process CWD (legacy behavior).

  **Parameter Editor (Browse / Show):** the Browse picker stores a **workspace-root-relative** path (forward-slash, portable) when the picked file is within the workspace subtree or ≤ 2 directories above the root; otherwise it stores the absolute path (`SnpPathPolicy.ToStored`). The **Show** (reveal-in-OS) command resolves a relative `File` against the same workspace root — identical to the engine's resolution base — so what Browse stores is what Show reveals and what the engine loads.
- **Identifier** — `[A-Za-z_][A-Za-z0-9_]*`, used for variable/parameter/argument names and function names. Hierarchical paths in measurements use `.` between identifiers (`X1.drain`) — see §13.
- **Imaginary unit** — `j` is a reserved constant equal to `(0, 1)`; imaginary values are written `j*4`, `2 + j*3` (matching the prototype's `j*` convention). `pi` and `e` are reserved constants.
- **`freq` — reserved injected keyword.** Lowercase `freq` is the **simulator's current stamping frequency** (in Hz), injected into scope by the engine when it evaluates a frequency-dependent component value (the `Z_Port` impedance expression, linear-engine §4; any future `Z(freq)`/`Y(freq)` model). It is **read-only and reserved** — a user may **not** name a variable `freq`. It is in scope for component-value expressions the engine stamps per-frequency, and — as the run's whole frequency axis, a 1-D cube rather than a scalar — in **measurement** expressions (`measurements.md`); it is **not** available in SDD time-domain equations (those see port voltages `_v`, not frequency) nor in ordinary variable/parameter expressions evaluated at elaboration. Distinct from the user-set **`Freq`** parameter (capital F) on a voltage source, which names the tone a source drives at (linear-engine §4.4); the commensurability check (harmonic-balance §3) verifies each source's `Freq` lands on the grid the engine stamps `freq` at.
- **Reserved names.** Constants (`j`, `pi`, `e`), the injected `freq`, and all built-in function names (`sin`, `cos`, …, `real`, `imag`, `abs`, `mag`, `phase`, `phase_rad`, `polar`, the WSProbe derived metrics `wsp_*`/`y_to_*`/`z_to_*`/`enc`/`_dB`, … §7) are reserved — a user variable/parameter/argument may not shadow them.
- **Operators** — `+ - * / ^`, `< <= > >= == !=`, `&& || !`, `? :`, and `( ) ,`.
- **Whitespace** separates tokens and is otherwise ignored.

A unit attaches in two places: glued to a number as a **Quantity** token (above), or at the assignment level (§8).

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

- `Number(double, unit?)` — real literal. A unit literal carries its unit, and its value is **already base SI**
  (`10um` is `Number(1e-5, "um")`), so every consumer that only needs the number reads it unchanged. The unit is read
  by the unit-bearing test (§8) and by printers that write an expression back (`FreqDeferral.Render` writes `10um`).
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

**WSProbe derived metrics** (measurement context only, over a run that carries a `WSProbe`): the
reference document's own function library, registered under the document's own names and argument
order. The full table — equations, arguments and return shapes — is in
`docs/design/stability-wsprobe.md` §5.2; the names are

`wsp_yparam`, `wsp_zparam`, `wsp_H0`, `wsp_Y0`, `wsp_ZG`, `wsp_ZL`, `wsp_YG`, `wsp_YL`, `wsp_zop`,
`wsp_yop`, `wsp_loopgain`, `wsp_nodal_gamma`, `wsp_rY`, `wsp_iY`, `wsp_rH`, `wsp_iH`, `wsp_SM_Y0`,
`wsp_SM_H0`, `wsp_stability_margin`, `wsp_sm_z`, `wsp_sm_y`,
`wsp_unstable_freq_kurokawa`, `encirculations`, `enc`, `_dB`, `wsp_zsrc`, `wsp_zprc`,
`wsp_impedance`, `wsp_gain`, `GainDEFs`, `wsp_rc_renorm_s`, `wsp_zo_renorm_s`, and the twelve
immittance conversions `z_to_pr/pc/pl/sr/sc/sl` and `y_to_pr/pc/pl/sr/sc/sl`.

**Network metrics** (measurement context only; brief-tuneopt-9): `mu(S)`, `mu_prime(S)`, `K(S)`,
`delta_mag(S)`, `max_gain(S)` (MAG/MSG in dB), `max_gain_lin(S)` (the same as a power ratio) and
`passivity(S)` (σ_max of the whole network), each also as `f(S, in, out)` with an ordered 1-based
input/output port pair — required on an N-port, implied (1, 2) on a two-port; `passivity(S, in, out)` is
that of the extracted two-port, not of the network. `S` must be an analysis's own S cube (`SP1.S`): the
metric needs that run's per-port reference impedances, which it renormalizes to a uniform real reference
first exactly as the Data Display does (`NetworkMetrics`), so a sliced or computed cube is refused. Each
returns `{…, freq}`. `group_delay(z)` is −dφ/dω along `freq` of a complex cube, phase unwrapped, in
**seconds**; `group_delay(S, in, out)` is the delay from port `in` to port `out` after the same
renormalization. `vswr(Γ)` is (1 + |Γ|)/(1 − |Γ|) element-wise, +∞ at and beyond |Γ| = 1. A name such
as `K` stays usable as a variable or measurement: only a CALL `K(…)` is the function.

**Noise** (measurement context only; `docs/design/sparameter-noise.md`): `nf(S[, Γs][, port])` is the noise figure in
dB from port 1 to `port` (2), `{…, freq}`: with no Γs from port 1's own Z0, `1 + Cs[k,k]/(kT0|S[k,1]|²)`; with Γs (a
number, or one value per point of `S`'s leading axes) through the noise parameters, NaN where |Γs| ≥ 1. `te` is the
same as T0·(F − 1) in K, `rn(S)` is Rn/R. `ns_circle(S, NFdB[, npts])` is the constant-NF circle, `{…, freq, point}`, a
closed loop of `npts` (101) complex Γ points, NaN below NFmin; `ns_circle(NFdB, NFmin, Sopt, rn[, npts])` draws it from
noise parameters (numbers or equal-length cubes, whose axes lead), and the argument count tells the forms apart. All
the S forms read the `Cs` cube of the analysis that owns `S`, found as the network metrics find Z0; the two-port from
port 1 to port k is the {1, k} submatrices of S and Cs (every other port is in its own noiseless Z0), and Γ is on
Re Z0 of port 1 of each sweep slice. Refused with a coded diagnostic: not a two-port for `rn`/`ns_circle`
(`measure.noise.not-two-port`), a run without `Noise=yes` (`measure.noise.not-run`), an output port outside 2..N
(`.port`), neither form of `ns_circle` (`.circle-form`), and malformed arguments (`.source`, `.figure`,
`.circle-points`, `.parameter`, `.cube-shape`).

**Field measurements** (measurement context only, over a `type=fields` analysis; brief-sar-10): `B1pMean(F, region)`,
`B1pMin(F, region)`, `B1pCoV(F, region)`, `psSAR(F, mass)`, `SARwb(F, region)`, `SarEff(F, region, mass)`, `Pabs(F)`. `F` is
the analysis's NAME, read as a name and never evaluated; a region is a string (a tissue, tissues joined by `+`, `"all"`, or
`"box(x0, x1, y0, y1, z0, z1)"`), optional where the setup's own applies; a mass is in grams. Each returns one Real, per watt
as the analysis normalises. The full table and the regions are `sar.md` §12.1. Over the same analysis's far field (brief-cosim-pattern-4):
`RealizedGainDbi(F)`, `GainDbi(F)` and `DirectivityDbi(F)` at the peak or `(F, theta, phi)` in that direction in degrees, `RadiationEfficiency(F)`,
`TrpDbm(F)` and `PeakEirpDbm(F)`, each the cube of its name; gain is per watt accepted whatever `norm=` says (`sar.md` §11.11).

They are reserved like every other built-in (§2). Three of the WSProbe functions are worth knowing about from the
grammar's side alone: **`_dB` is a legal identifier** (the tokenizer has always accepted a leading
underscore) and is `10·log10|M|`, a POWER dB, which is why it is not `dB`; the `y_to_*`/`z_to_*`
family returns **farads and henries**, never the document's picofarads and nanohenries; and the
frequency argument of `wsp_zsrc`, `wsp_zprc` and the six frequency-dependent conversions **may be
omitted**, in which case the cube's own `freq` (or `ssfreq`) axis is used.

**Distributions** (parameter and variable expressions; not in device equations — `docs/design/yield.md` §7). The SPICE
dialect's statistical functions, with its argument meaning; real-valued:

| Call | Draw | Outside a trial |
|---|---|---|
| `agauss(nom, dev[, k])` | normal, σ = dev/k (dev absolute, at k σ; k defaults to 1) | `nom` |
| `gauss(nom, rel[, k])` | normal, σ = nom·rel/k (relative) | `nom` |
| `aunif(nom, dev)` | uniform on nom ± dev | `nom` |
| `unif(nom, rel)` | uniform on nom·(1 ± rel) | `nom` |
| `limit(nom, dev)` | nom − dev or nom + dev, equally likely | `nom` |
| `limit(x, lo, hi)` | — a clamp, `min(max(x, lo), hi)`; three arguments is never a distribution | the clamp |

`validrange(x, lo, hi)` (not a distribution) is `x`, unchanged; when `x` lies outside `[lo, hi]` it posts one
elaboration warning naming the instance, the parameter, its value and the range (`Evaluator.Warn`, which the
elaborator points at the netlist's warnings). A fitted model — a passive family cell (brief-modelcomposer-12) — wraps
its geometry parameter in it. Not a clamp: outside the range the model still answers, and says it is extrapolating.

- **Outside a Monte Carlo trial a distribution is its first argument, evaluated alone** — its kind and unit are that
  argument's, and the spread is not even read, so a spread naming something that does not resolve costs an ordinary
  run nothing. The unit rules (§8) read the same view: a distribution is unit-bearing exactly when its nominal is,
  and an absolute spread beside a unit-bearing nominal takes the site unit like any operand that must agree with it.
- **Inside a trial** the evaluator is handed the trial's draws (`Evaluator.Statistics`, an `IStatisticalDraws` the
  elaborator passes down — never a global). k = 0 is no spread, the reading a kit relies on to switch mismatch off;
  `sigmascale` multiplies every spread.
- **Process or mismatch is the scope's, not the function's**: evaluated in the testbench's global scope, a draw is
  shared by every instance in a trial; in a cell instance's scope (`Scope.InstancePath`), it is drawn per instance.
- A VAR holding a distribution is an expression, so it is not tunable — `explain --tunables` says so when asked.

The set is intentionally close to what other tools' equation-defined devices provide, so hero SDD equations transcribe cleanly (§14). Functions beyond that common set are allowed for non-hero use but should be added knowingly.

---

## 8. Units

Units attach at the **assignment level** — an assignment is `name = <expression> [unit]`, and the unit **scales the resolved value** by a linear factor — and, since 2026-09-27, **inside an expression as unit literals** (§3: `10um + 1mil`), scaled to base SI at parse time. This matches the prototype (`Parameter.UnitString` scaling the evaluated expression) and the `.cnl`/imported-netlist lines (`L=L1 nH`, `Z=50 Ohm`, `M=0.5 pH`).

A representative scale table (extensible):

| Domain | Units (→ SI factor) |
|---|---|
| Generic SI prefixes | `T`=1e12, `G`=1e9, `M`=1e6, `k`=1e3, `m`=1e-3, `u`=1e-6, `n`=1e-9, `p`=1e-12, `f`=1e-15 |
| Frequency | `Hz`=1, `kHz`, `MHz`, `GHz`, `THz` |
| Inductance | `H`=1, `mH`, `uH`, `nH`, `pH`, `fH` |
| Capacitance | `F`=1, `mF`, `uF`, `nF`, `pF`, `fF` |
| Resistance | `Ohm`=1, `kOhm`, `MOhm` |
| Length | `metre`=1, `mm`, `um`, `cm`, `nm`, `mil`=2.54e-5, `in`/`inch`=2.54e-2 |
| Angle | `deg`=π/180, `rad`=1 |

**The length base symbol is `metre`, NOT `m` — and this table used to say otherwise, which is where
the bug came from.** The SI-prefix row and the length row live in **one flat dictionary**
(`Units._scales`), so a length row claiming `m`=1 collided head-on with the prefix row's `m`=1e-3. The
implementation resolved the collision in favour of milli and left length with no scale-1 symbol at all
— so `BaseUnit("mm")` returned `"m"`, whose scale is 1e-3, and the "reduce a unit to a scale-1 base"
property that `ParametricSweepEngine` relies on was false for every length. Fixed 2026-08-07
(brief-core-length-units.md): **`m` stays milli**, because a hand-authored `C=1m` must keep meaning one
millifarad in every netlist dialect there is; the metre gets its own spelling. `nm` and `cm` were also
absent from this table entirely and had been parked in `_identityUnits` (multiplier exactly 1), so
`L = 1 cm` evaluated to 1.0. See `src/Core/CLAUDE.md` for the measured before/after table.

**Adding a length unit is one row in `_scales` plus one row in `_baseUnitMap` mapping it to `metre`.**
Forgetting the second is silent: the unit scales correctly at a use site and then re-scales itself on a
parametric sweep's re-attach. That is exactly what happened to `mil`.

**This is not the only unit system in circuitRF, and the others are deliberately separate — do not
unify them.** `LayoutUnits` (`layout-view.md` §1.1, DBU-based, `decimal` arithmetic, `1 mil = 25400 DBU`
exactly) and `WBondUnits` (nanometre-based, same discipline) are **integer/decimal** systems answering a
different question — *exact database coordinates* — while this table is a **double-precision multiplier**
applied to a resolved expression value. Neither routes through `Units`. Consolidating them would trade a
fixable bug for an unfixable rounding class. `MicrostripSubstrateInjection.SchematicLengthUnit` is the
one sanctioned translation point between the two vocabularies, and it is a **name** mapping, not a
numeric conversion.

**Logarithmic units are not unit-suffixes.** `dB`/`dBm` are not linear scale factors, so they are handled by functions (`dB(...)`, `dBm(...)`), never as a trailing unit on a value.

**Var-unit-wins (no double-scaling).** A unit applies *once*. When an evaluation **site** carries a unit (e.g. a tone field `Freq = RFfreq GHz`, or a swept override injected with a base unit) **and** the expression references a variable that already declares its own unit, the site unit is **skipped** — the variable's unit was already applied when it resolved. Without this, `Freq = RFfreq GHz` where `RFfreq = 2 GHz` would scale by 1e9 twice. The rule is implemented in `Evaluator.Eval(expr, scope, unit)`: it skips the site `unit` when the expression references any unit-bearing name in scope; a bare number and a unit-less reference still take the site unit — on the result when nothing in the expression is unit-bearing, or as operands beside a unit-bearing term (below). This is what makes a mixed-unit compound resolve exactly (each reference contributes its own unit). **A binding's own unit is a site too** (fixed 2026-09-27): `Evaluator.Resolve` — and `FreqDeferral`, which inlines a binding the way `Resolve` would have evaluated it — skip a variable's declared unit when its expression references a unit-bearing name, so `b = 2*a [mil]` with `a = 10 [mil]` is 20 mil. Until then `Resolve` applied a binding's unit unconditionally and `b` came out 2.54e-5 times too small, on a schematic VAR and a `.c3d` VAR alike.

**Unit literals are unit-bearing** (2026-09-27, `brief-units-in-expressions.md`). An expression is UNIT-BEARING —
and its site unit is skipped — when it contains a unit literal (§3) or references a unit-bearing name. A name is
unit-bearing when it declares a unit, or when it declares none and its own expression holds a literal (followed through
other unit-less names): `w = 10mil` with no unit field is base SI all the same. A unit-less name that merely references
a unit-bearing name is still NOT unit-bearing — that was the rule before literals existed, and following it would change
the value of existing documents. The test is `Evaluator.IsUnitBearing`; every var-unit-wins site calls it.

**A bare operand beside a unit-bearing one takes the site unit** (owner decision, Q1 (b)). In `cav_w + 40` typed in mil,
the 40 is forty MIL. Until 2026-09-27 it was forty metres — "the unit trap" — and the only advice was to write the 40 as
a name. The rule is applied as an AST transform before evaluation (`Evaluator.ScaleBareOperands`), never a text rewrite,
to the operands of `+`, `-`, a comparison, the two branches of a conditional, and `min`/`max`. A **multiplicative**
operand stays dimensionless: `2*w` is twice w, `w/4` a quarter of it. **Powers are tracked**: a literal or a
unit-bearing name is power 1, `*` adds powers, `/` subtracts them, `^` and `pow` by a constant multiply, `sqrt` halves,
`abs`/`real` keep — and a bare operand is scaled by `site^power`. So `sqrt(w*w + 25)` adds 25 square mil and
`w/h + 1` adds exactly 1 (power 0). Refusing every bare literal beside a non-unit power was the alternative; it would
refuse the ordinary `w/h + 1`, which tracking gets right. Where the power cannot be known (a unit-bearing argument of
any other function, `w^n` with a non-constant `n`) the bare operand is left in base SI, which is what it meant before.
The transform applies only where var-unit-wins has already decided not to scale the result, and only for a linear
site unit other than 1 (a site in `metre`, `Hz`, `V` or `dBm` has nothing to scale by). With **no** site unit a bare
number is base SI, as it always was.

**`m` is milli here too.** `2m` is 2e-3 — in a length, two millimetres. That is deliberate (brief-core-length-units §5
q1) and has a cost a reader must know: the metre is `2metre`. The 3D view refuses a whole typed `2m` in a length field
(naming `2mm` and `2metre`), and warns about an `m` literal inside a length expression.

**Not this phase:** unit algebra and dimension checking across dimensions — `1GHz + 1mil` is accepted and meaningless.

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

**A cell's own variables can be set from the instance (brief-sdl-6 D7).** For each of `C`'s variables that `X`
overrides, the override expression is evaluated in `P`'s scope and replaces that variable's expression in `X`'s scope
only — exactly as a parameter override does. `C`'s other variables that depend on it (`L = L90 - w50/2`) follow through
ordinary evaluation, per instance. This applies to every hierarchical instance. Two refusals hold it honest: an
override naming something `C` neither declares (parameter or variable) nor reads is an **error**, never ignored; and a
name that is **both** a parameter and a variable of `C` cannot be overridden — the error names both. Without an
override, such a name is the VARIABLE's value: the elaborator binds parameter defaults, then the cell's variables, then
the overrides, each replacing the last.

**Resolution order for a name inside a cell:** local cell variables/parameters first, then global. A cell does **not** see its parent's local variables (only what was passed in as parameters) or any sibling's — this is the encapsulation that keeps a cell's meaning independent of where it is instanced.

### 9.1 Design-time value preview (the `=` / `≈` editor hint)

The Add/Edit Analysis dialog (and the parameter editor) show a small live hint next to each expression
field — e.g. a tone-frequency field reading `RFfreq` shows `= 2e9`, a `2.4` with a `GHz` unit dropdown
shows `= 2.4e9`. This is a **convenience preview**, evaluated by `AnalysisPreviewHelper` against a
**flat, read-only design-time mirror** of the schematic's variables (`DesignScope`), *before* any
elaboration. It is not the authoritative resolved value — the elaborator (§9) is — and it never blocks
editing or raises an error; an expression it can't resolve simply shows no hint.

**What it does.** It collects every named parameter expression in the model into a single flat scope
(last-wins on duplicate names; SDD/FetSdd equation slots are excluded), evaluates the field expression
against it, and renders the result.

**Two scope modes** (the only difference is whether variable *units* are bound):
- **Unit-aware** (`DesignScope.BuildResolved`) — binds each variable's declared unit, converted from
  editor glyphs (`Ω`, `µH`) to engine ASCII (`Ohm`, `uH`). A reference to a unit-bearing variable then
  resolves to its true base-unit value (`Cval = 1 pF` → `1e-12`; `RFfreq = 2 GHz` → `2e9`). The
  **var-unit-wins** rule (§8) applies each variable's unit exactly once, so a *mixed-unit
  compound* like `RFfreq + Voff` (GHz + MHz) is **exact**, not a single-multiplier approximation. This
  is the mode the analysis-editor field previews use.
- **Unit-stripped** (`DesignScope.Build`) — binds bare expressions (no unit). Used by the
  parametric-sweep row, which intentionally treats the field as a raw **coefficient** and applies its
  own range unit on top; binding units here would double-scale it.

**Honest prefix.** The leading symbol tells the truth about the displayed digits:
- `= value` — the shown digits reconstruct the value (to ~1e-12 relative, so plain floating-point
  representation noise does not force a `≈`).
- `≈ value` — the value had to be rounded to fit the display budget (G4 → G6 → G8 ladder; if none
  round-trips, it is shown at G4 with `≈`).
- `unknown: <name>` — a referenced name is not present in the flat scope (no value to prefix).
- *(empty)* — a bare literal (no `= 2.5` noise), a blank field, or anything otherwise unresolvable.

**Limitations (why a preview may be approximate or absent).** The scope is a *flat* mirror, so it does
**not** model: cell hierarchy or per-instance parameter overrides (§9 proper); parametric sweeps;
post-run measurement context (`HB1.V(...)`, §13); or SDD device equations (evaluated in the SDD's own
context, §12). An expression that depends on any of those resolves only approximately or not at all. An
unrecognised unit binds *raw* rather than throwing, so such a field degrades to a unit-stripped preview
rather than disappearing. Treat the hint as a sanity check, not a contract — the elaborated value is
authoritative.

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

Measurements use this same engine, with operands extended from scalars to **cube quantities** and a set of **accessor functions** whose arguments are hierarchical paths or indices: `V(path)`, `I(path)`, `S(i,j)`, plus spectral/FOM functions (`harm`, `tone`, `Pout`, `PAE`, `IMn`, `dB`, `dBm`, reductions like `max`), and **`at(x, "axis", index)`** — which pins one axis of a cube BY NAME (negative index counts from the end), the shape-independent counterpart of a positional slice. Like `dB`/`dBm`, `at` is a *measurement* function rather than a general built-in: it is meaningless on a scalar, and it is an error there rather than a no-op. Paths (`X1.drain`) resolve against the elaborated node map. The full accessor/function library, result typing (`Real`/`Complex`), the `@ analysis` binding, and IMn extraction are specified in `measurements.md`; this note owns only the core grammar those extensions build on.

**Not part of this grammar: the Data Display trace spec's `vs` separator** (`Gain vs Pout`, plot-versus.md). It is split off *above* the expression engine — before any parsing — and each side is then handed to this engine unchanged. `vs` is therefore not an operator, has no precedence within an expression, and is invisible to every other consumer; a variable or cube may still be named `vs` (it must be written bracketed in a trace spec to be read as data).

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

## 15A. Locale — the language is invariant, in every country, forever

The expression language is a **formal language**, like C# or a SPICE deck. It does **not** follow the
user's locale, and it will not start to when the UI is localized. What it does do — since
2026-09-21 — is accept **both spellings of a decimal point**, the same way on every machine.

- **`1.5` and `1,5` are the same number, in `en-US`, in `de-DE`, everywhere.** Much of the world
  writes one and a half with a comma, and a parameter field is somewhere a person types, not a file
  format. Accepting the second spelling is not locale sensitivity: the reading does not depend on
  the machine's culture, which is the property this section exists to protect.
- **Inside `(…)` or `[…]`, `,` is still the argument separator and nothing else.** `max(1,5)` is the
  larger of 1 and 5, not `max(1.5)`. There the ambiguity is real and nothing in the text resolves
  it, so a decimal point inside an argument list must be written as a point.
- **The rule is one line: a comma between two digits, at bracket depth 0, outside a string literal.**
  It is total because the grammar has no comma at depth 0 at all — every separator comma sits inside
  brackets, and `Parser.Parse` requires EOF after one expression, so `1,5` was a hard parse ERROR
  before this. Widening it cannot change what any existing text means.
- **A grouped number is refused in every locale.** `1.234,5` and `1,234.5` are 1234.5 to some readers
  and something else to others, with nothing in the text to say which, so neither is read. circuitRF's
  fields carry engineering units; none of them needs a thousands separator.
- **The canonical spelling is still the point.** What is stored in a `.csch`, written to a `.cnl` or
  printed by the CLI is `1.5`, so a design authored on one machine reads identically on every other.
- **Localizing the UI does not change any of this.** What a German user *reads* in a status line is
  display text and correctly follows their locale (`2,5 GHz`); what they *type* is read by the same
  rule everywhere.

**Why the earlier rule was narrowed.** This section used to say `1,5e9` parses nowhere, ever, and
gave the reason: a grammar cannot have both `if(a,b,c)` and a comma decimal, because `f(1,5)` would
be two things at once. That argument is correct — **but only inside the brackets**, which is where
the ambiguous `f(1,5)` lives. It was being applied to the whole grammar, and the cost fell on the
people it was least defensible for: a user in a comma-decimal region typing `4,4` into a stackup's
εr watched the field silently revert to its old value with no message
(`brief-localization-groundwork.md` §2.3 records that symptom, and R-loc-1's remedy was to make the
*rejection* consistent rather than to accept the value).

**What has not changed, and is the real hazard.** A *locale-sensitive* parser is still forbidden.
With the parser at `de-DE`, `abs(-3.5)` would evaluate to **35** and `7.5-2.25-1.25` to **-275** — no
exception, no warning, a plausible number, a different circuit. Every literal is still parsed with
`CultureInfo.InvariantCulture`; the comma is rewritten to a point *before* that, by
`NumericText.NormalizeDecimalSeparator`, identically on every machine.

**The gate** is `tests/Core.Tests/Expressions/ExpressionCultureInvarianceTests.cs`, which drives the
v1 language surface — literals, exponents, precedence, the standard functions, comparisons, `if()`,
nesting — under `de-DE` and `fi-FI` and demands results **bit-identical** to `en-US`, plus both
halves of the comma rule: a depth-0 comma decimal read the same everywhere, an argument-list comma
still separating, and a grouped number refused. It is a deliberate foreign-locale pass because the
default run cannot substitute for it — `tests/TestCulture.cs` pins the whole suite to `en-US`,
precisely so no test is accidentally asserting the runner's locale.

**A unit suffix is a row FIELD, not part of the expression** (§8), which is why `60u` is a parse
error here. That is unrelated to locale, and the decimal-comma widening is not a reason to revisit
it — but it is pinned in the same file, so that "make the parser more forgiving about what follows a
number" cannot arrive as a fix and quietly change it.

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
   **Length is now settled** (brief-core-length-units.md, 2026-08-07): base symbol `metre`, with
   `nm`/`cm`/`mil`/`in`/`inch` all carried and all mapped to it, and `m` reserved to the SI prefix milli.
   Every other domain is still "representative" — and the lesson from length generalises: **a domain
   needs a scale-1 base symbol that is not also an SI prefix, plus a `_baseUnitMap` row for every
   non-prefixed member of that domain** (`mil` had neither and re-scaled itself on every parametric
   sweep). `Ohm`/`Hz`/`H`/`F`/`V`/`A`/`W` are all safe on the first count; the second is worth checking
   before any new non-prefixed unit is added anywhere.

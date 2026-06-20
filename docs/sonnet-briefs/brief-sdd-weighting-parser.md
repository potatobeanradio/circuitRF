# Brief #3 (SDD weighting): the SDD parser — `I[p,w≥2]` + `H[w]=expr`, drop the `w≥2` gate

Design ref: `docs/design/sdd.md` §2, §3, §6. Depends on briefs #1 (equivalence test) and #2 (engine
generalization — `WeightedTerm`/`NonlinearResult.Terms` + `ComponentModel.Weight` are landed). This brief
makes the engine's `w≥2` machinery **reachable from a netlist**: parse `I[p,w]` for arbitrary `w`, parse the
`H[w]=expr` weighting parameters, store per-`(p,w)` ASTs on `SddModel`, override `Weight(w,ω)` to evaluate
`H[w]`, and remove the factory's `w≥2` hard-error.

Core only, framework-free. Build **0W/0E**. Brief #1's two facts and brief #2's stub tests must stay green.

---

## 1. `SddModel.cs` — store the general `I[p,w]` set + the `H[w]` expressions

Keep `_currentAst` (w=0) and `_chargeAst` (w=1) exactly as the fast path. Add:
- `private readonly IReadOnlyList<(int W, Expr Ast)>[] _higherAst;` — per port (index = port−1), the list of
  `(w, expr)` for `w≥2` on that port. (Most ports have none → empty list.)
- `private readonly IReadOnlyDictionary<int, Expr> _weightAst;` — `w → H[w]` AST, for `w≥2` only.

Extend the ctor to take both (the existing 5-arg shape gains two params; the factory is the only caller).

**`Evaluate` — emit `w≥2` buckets.** After the current/charge loops, build the `Terms` list: for each port `p`
and each `(w, ast)` in `_higherAst[p]`, run the **same dual-AD path** the current/charge use
(`SddEvaluator.EvalDual(ast, _params, vArr, _name)` → `(val, grad)`), and accumulate into per-`w`
`WeightedTerm`s:
```csharp
// group by w across ports: Value[p] = I[p,w](v), Jac[p,q] = ∂I[p,w]/∂v_q
// (one WeightedTerm per distinct w, ports that don't define that w contribute 0)
```
Return `new NonlinearResult(i, q, dg, dc, terms)` (the 5-arg ctor) when any `w≥2` exist; otherwise the 4-arg
fast path (Terms = []), so existing SDDs are byte-identical.

**`Weight(int w, double omega)` — override for `w≥2`.** `w=0`/`w=1` fall through to `base.Weight` (1, jω). For
`w≥2`, evaluate the `H[w]` expression as **Complex** at this frequency:
```csharp
public override Complex Weight(int w, double omega)
{
    if (w < 2) return base.Weight(w, omega);
    if (!_weightAst.TryGetValue(w, out var ast))
        throw new InvalidOperationException($"SDD '{_name}': I[p,{w}] used but H[{w}] is not defined");
    return EvalWeight(ast, omega);
}
```
**`H[w]` is Complex (it needs `j`), so it must NOT use the real-only `SddEvaluator`** (that path bans `j`). Use
the general `Evaluator` instead, which is Complex-capable (`Value.J`, Complex `Value` arithmetic):
```csharp
private Complex EvalWeight(Expr ast, double omega)
{
    var scope = new Scope("Hw");
    scope.Bind("freq", (omega / (2 * Math.PI)).ToString("R", CultureInfo.InvariantCulture));
    foreach (var kv in _params)                       // scope vars (tau, etc.) available to H[w]
        scope.Bind(kv.Key, kv.Value.ToString("R", CultureInfo.InvariantCulture));
    var v = new Evaluator().EvalExpr(ast, scope);     // Value (Real or Complex)
    return v.Kind == ValueKind.Complex ? v.AsComplex() : new Complex(v.AsReal(), 0);
}
```
`freq` is Hz (`ω/2π`), matching the design doc and the HB `freq` convention. `ω` passed in is the per-harmonic
`k·ω₀` (the engine already calls `Weight(w, k·ω₀)`), so `H[w]` lands at the right frequency per harmonic with
no caching concern here (brief #2's `hkCache` handles per-solve caching).

> Note the two evaluators are deliberately different: **`I[p,w]` (time-domain, voltage-controlled, real,
> dual-AD) → `SddEvaluator`; `H[w]` (frequency-domain, `freq`-controlled, Complex) → `Evaluator`.** Don't cross
> them.

## 2. `ComponentModelFactory.CreateSddModel` — parse `I[p,w≥2]` and `H[w]`

**(a) Drop the hard-error.** Remove:
```csharp
if (w >= 2)
    throw new InvalidOperationException($"SDD '{sddName}': weighting w≥2 (H[w]) not supported in v1 …");
```
For the two-index `RxCurrentEq` match: `w==0`→current, `w==1`→charge (unchanged), **`w≥2`→a new
`higherAst[p-1]` list** (validate `p` range via the existing `ValidateAndBind` port check, then add `(w,
Parser.Parse(expr))`).

**(b) Parse `H[w]=expr`.** Add a regex `RxWeightFn = ^H\[(\d+)\]$`. For each match: `w = group1`; require
`w≥2` (error on `H[0]`/`H[1]` — those are built-in and not user-redefinable); store
`weightAst[w] = Parser.Parse(value.AsString())`. The value arrives as a String (the elaborator must pass
`H[w]` through as expression text — see §3).

**(c) Cross-validation.** After parsing: every `w≥2` referenced by some `I[p,w]` must have a matching `H[w]`
declared, else error: `SDD '{name}': I[p,{w}] references weighting H[{w}] which is not defined`. (An unused
`H[w]` with no `I[p,w]` is a harmless no-op — warn or ignore.)

**(d)** Pass `higherAst` + `weightAst` into the extended `SddModel` ctor.

## 3. Elaborator + reader — route `H[w]` through as expression text

`H[w]=expr` must reach the factory as a **String** `Value` (like the `I[p,w]` equations), not a resolved
number. Confirm/extend:
- **CnlReader** `SddAssignmentHeader` regex already accepts `(I|Q|F|In|Nc)\[…\]` and `C(port)?\[…\]`; **add
  `H\[\d+\]`** so the boundary scanner treats `H[2]=…` as an SDD equation assignment (not split into nets).
- **Elaborator `ResolveSddParameters`** already passes `I[p,w]` through as String expression text; route
  `H[w]` the same way (it's an expression of `freq`/scope vars, resolved per-frequency in the engine, not at
  elaboration). `freq` must NOT be required to resolve at elaboration time — keep `H[w]` as raw text.

## 4. Tests (`tests/Core.Tests/Devices` + `tests/Engine.Tests`)

- **Parse:** `SDD:X1 a 0  I[1,2]=_v1  H[2]=2` → model has a `w=2` bucket; `Evaluate` returns a `WeightedTerm`
  with `W=2`, `Value=[_v1]`, `Jac=[[1]]`; `Weight(2, ω)=2+0j` (constant real H).
- **Complex H[w]:** `H[2]=j*2*pi*freq` → `Weight(2, ω) ≈ jω` (within fp tol). Confirms the Complex `Evaluator`
  path and the `freq=ω/2π` binding.
- **Missing H:** `I[1,2]=_v1` with no `H[2]` → factory errors clearly (cross-validation §2c).
- **Redefine built-in:** `H[1]=…` → error (built-in, not user-redefinable).
- **End-to-end equivalence:** an SDD nonlinear cap written via the **user weight** — `I[1,2]=Q(_v1)` with
  `H[2]=j*2*pi*freq` — gives the same HB spectrum (and S-param) as the same device via the built-in
  `I[1,1]=Q(_v1)` (`w=1`). This is the payoff: a user-defined `H[w]≡jω` reproduces the charge path, proving
  the whole `w≥2` pipeline from netlist → engine. (Mirror brief #1's HB harness; assert per-harmonic
  `|V_userW − V_w1| < tol`.)
- **Regression:** brief #1's two facts + the existing SDD HB/parse tests stay green (no `H[w]` → fast path).

## Gate
Build 0W/0E; all green. With this landed, the full SDD weighting feature is reachable from a netlist:
`I[p,0]`/`I[p,1]`/`Q[p]` built-ins plus user `I[p,w≥2]` + `H[w]=expr` of `freq`. Update
`src/Core/.../CLAUDE.md`: SDD now accepts arbitrary weighting `I[p,w]` with user `H[w]=expr` (Complex, in
`freq`); `H[0]=1`/`H[1]=jω` are built-in and not redefinable; `I[p,w]` uses the real dual-AD evaluator, `H[w]`
uses the Complex general evaluator.

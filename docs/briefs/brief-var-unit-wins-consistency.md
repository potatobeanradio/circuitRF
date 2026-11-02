# Sonnet Brief — Apply var-unit-wins consistently (fix swept-frequency double-unit)

**Bug (verified).** A parametric sweep over a frequency variable runs at the wrong frequency because
the unit is applied **twice**. With `RFfreq` swept via `Unit=GHz`, Brief-2 materialization pre-scales
the range to base Hz (`[1,5.5,10] → [1e9,5.5e9,1e10]`); the engine injects `RFfreq=1e9` as a
**unit-less** override; then **both** use sites apply GHz again:
- HB tone `Tone="RFfreq" ToneUnit=GHz` → `RFfreq` isn't flagged unit-bearing (override dropped the
  unit), so `FreqUnit.ResolveHz` doesn't trigger var-unit-wins and ToneUnit applies: `1e9×1e9 = 1e18`.
- `P1 Freq=RFfreq GHz` → `Elaborator.ResolveP1ToneParameters` → `Evaluator.Eval("RFfreq", scope, "GHz")`
  applies the unit unconditionally: `1e9×1e9 = 1e18`.

Both double-apply *consistently*, so commensurability passes and the solve runs — at **1e18 Hz instead
of 1e9**. The plot axis still reads `1–10 GHz` (axis carries base values tagged `Hz`), masking it. The
no-sweep case keeps `RFfreq=2` unscaled → GHz applied once → correct 2 GHz. (Latent in the no-sweep
VAR-unit case too: `RFfreq=2 GHz` + `Freq=RFfreq GHz` already yields `2e18`.)

**Owner decision (LOCKED).** Apply **var-unit-wins everywhere**: a variable's unit is applied exactly
once. A bare/compound reference to a variable that already carries its own unit must **not** re-apply a
site unit — in component-parameter resolution, identical to the rule the HB tone / S-param sweep
already use via `FreqUnit.ResolveHz`. Keep base values + axis unit tags so marker units still work.

This makes "the unit lives in one place" true across the VAR, the sweep's `Unit`, and every use site,
swept or not. It also fixes the latent no-sweep `2e18` case.

---

## Part A — Mark the swept override as unit-bearing (`src/Engine/ParametricSweepEngine.cs`)

The HB tone / S-param paths already do var-unit-wins (`FreqUnit.ResolveHz` consults
`netlist.GlobalsWithExplicitUnit`). The reason it doesn't fire during a sweep is that the override
variable is injected **unit-less**, so the Elaborator never marks it. Fix: inject the override with the
swept dimension's **base** unit (scale-1 → marks it without re-scaling), sourced from the same
effective unit Brief 2 used to materialize the values. Use the **same** source to tag the axis (this
supersedes the marker brief's `origVar.Unit`-only source so it also works for a unit-less VAR swept
with `Unit=GHz`).

```csharp
// Effective unit = the unit Brief 2 scaled by: the sweep's Spec.Unit, else the VAR's declared unit.
string effUnit  = sweep.Spec?.Unit is { Length: > 0 } su ? su : (origVar?.Unit ?? "");
string baseUnit = Units.BaseUnit(effUnit);   // "GHz"→"Hz", "mV"→"V", ""→"" (scale-1 symbol)

// … in the loop:
var overrideVar = new Variable(
    sweep.SweepVarName,
    val.ToString("G17", CultureInfo.InvariantCulture),
    baseUnit);                                // base unit (scale 1) → value unchanged, but MARKED
```

- `Units.BaseUnit` (added in the marker brief) returns a **scale-1** symbol, so `Scale(baseUnit)==1` and
  the override resolves to the same base value (e.g. `1e9`) — but now `v.Unit` is non-empty, so the
  Elaborator's existing `MarkGlobalHasUnit` flags it in `GlobalsWithExplicitUnit`.
- When `effUnit` is empty (no sweep unit, no VAR unit), `baseUnit==""` → override stays unit-less
  (unmarked), the values were never scaled, and the use sites apply their unit once — unchanged,
  correct.
- **Replace the existing comment** ("do NOT add `origVar.Unit`, which would double-apply") — that was
  about the *display* unit (`GHz`, scale ≠ 1). Adding the *base* unit (`Hz`, scale 1) is correct and is
  exactly what makes var-unit-wins fire.
- Tag the axis from the same `baseUnit`: `new Axis(sweep.SweepVarName, sweep.SweepValues, baseUnit)`
  (was `Units.BaseUnit(origVar?.Unit ?? "")`).

With this alone, the **HB tone** (and S-param sweeps) resolve correctly during the sweep — no
`ResolveHz` change needed, because `RFfreq` is now in `GlobalsWithExplicitUnit` and the existing
var-unit-wins returns `eval(RFfreq)=1e9`.

## Part B — var-unit-wins in `Evaluator.Eval(expr, scope, unit)` (`src/Core/Expressions/Evaluator.cs`)

This is the component-parameter path (`P1 Freq=RFfreq GHz`, and any `value unit` override). Skip the
site unit when the expression references a variable that carries its own unit — mirroring
`FreqUnit.ResolveHz` ("any referenced var has a unit → result is already unit-applied").

```csharp
public Value Eval(string expression, Scope scope, string? unit = null)
{
    var ast = Parser.Parse(expression);
    var raw = EvalExpr(ast, scope);
    // var-unit-wins: if any referenced variable declares its own unit, that unit was already applied
    // when the variable resolved (Resolve → ApplyUnit with the binding's unit); do NOT re-apply the
    // site unit. Matches FreqUnit.ResolveHz, using per-binding scope units.
    if (!string.IsNullOrEmpty(unit) && ReferencesUnitBearingVar(ast, scope))
        return raw;
    return ApplyUnit(raw, unit);
}

private static bool ReferencesUnitBearingVar(Expr ast, Scope scope)
{
    foreach (var name in AstWalker.CollectRefs(ast))      // existing Core helper
    {
        var found = scope.Lookup(name);                   // (expression, unit, owningScope)?
        if (found is not null && !string.IsNullOrEmpty(found.Value.unit))
            return true;
    }
    return false;
}
```

- Adjust `found.Value.unit` to the actual `Scope.Lookup` return shape (same tuple destructured in
  `Resolve`: `var (expression, unit, owningScope) = found;`).
- The variable's **own** unit is still applied in `Resolve` (unchanged) — that's the single application.
  Only the *site* unit on a reference to it is skipped.
- Guarded by `!string.IsNullOrEmpty(unit)`, so the no-site-unit path (e.g. preview `Eval(expr, scope)`)
  is untouched. A literal (`Freq=2 GHz`, no var ref) still applies the unit. A reference to a *unit-less*
  variable (`Freq=RFfreq GHz`, `RFfreq=2`) still applies the unit. Only a reference to a *unit-bearing*
  variable skips it.
- This also fixes prefixed-unit doubles generally (e.g. `C=Cval pF` where `Cval = 1 pF` was
  `1e-12×1e-12`; now `1e-12`).

**Consistency note:** Parts A+B make all three resolution surfaces agree — HB tone / S-param
(`FreqUnit.ResolveHz` + `GlobalsWithExplicitUnit`) and component params (`Evaluator.Eval` + scope
units). No change to `ResolveHz` itself.

---

## Tests

Core (`tests/Core.Tests`):
1. **Eval_VarUnitWins_SkipsSiteUnit:** scope binds `X → ("2","GHz")`; `Eval("X", scope, "GHz") == 2e9`
   (one application), not `2e18`. With `X → ("2","")` (unit-less): `Eval("X", scope, "GHz") == 2e9`
   (site applies). Literal: `Eval("2.4", scope, "GHz") == 2.4e9`.
2. **Eval_VarUnitWins_Compound:** `X → ("2","GHz")`; `Eval("X*2", scope, "GHz") == 4e9` (any ref has a
   unit → skip site unit). `Eval("Y*2", scope, "GHz")` with unit-less `Y=2` → `4e9` (site applies).
3. **Eval_PrefixedUnitDouble_Fixed:** `Cval → ("1","pF")`; `Eval("Cval", scope, "pF") == 1e-12`.

Engine (`tests/Engine.Tests`):
4. **Sweep_FreqVar_NoDoubleApply (the regression):** the reported netlist (VAR `RFfreq=2`, `Tone="RFfreq"
   ToneUnit=GHz`, `P1 Freq=RFfreq GHz`, outer sweep `Var=RFfreq Start=1 Stop=10 Npts=3 Unit=GHz`). At
   each swept point assert the HB `p.ToneHz` and the elaborated `P1.FreqHz` equal the **nominal** value
   in Hz: `[1e9, 5.5e9, 1e10]` — NOT `[1e18, …]`. Both must agree (commensurability holds at the right
   frequency).
5. **Sweep_Override_Marked:** after injecting a swept point for a `Unit=GHz` sweep, the elaborated
   netlist's `GlobalsWithExplicitUnit` contains the swept variable; a unit-less sweep (no `Unit`,
   unit-less VAR) does not.
6. **Sweep_Equals_NoSweep_AtSamePoint:** add a sweep point at exactly `2 GHz` (e.g. `Values=2 Unit=GHz`
   or a 1-point spec); assert a measurement (e.g. `Pout_dBm`) at that point equals the no-outer-sweep
   single run at `RFfreq=2`. (Direct end-to-end equivalence the user expects.)

## Gate
Build 0W/0E (TreatWarningsAsErrors) in Core/Engine; full suite green — and scan for any test that
encoded the old double-apply behavior (none should exist; a failure there means the test asserted a
bug). **Manual (the user's two netlists):** the with-outer-sweep run now solves each point at the
displayed frequency (`1 GHz` point → 1e9 Hz, not 1e18); at `RFfreq=2 GHz` the swept and non-swept
results match; marker/axis units still read GHz.

## On completion
Update `src/Core/CLAUDE.md`: var-unit-wins now applies in **`Evaluator.Eval(expr, scope, unit)`** too —
a site unit (`value unit` override) is **skipped** when the expression references a variable that
declares its own unit (consistent with `FreqUnit.ResolveHz`; the variable's own unit is still applied
once in `Resolve`). Update `src/Engine/CLAUDE.md`: `ParametricSweepEngine` injects each swept override
with the dimension's **base** unit (scale-1, sourced from `sweep.Spec.Unit ?? origVar.Unit`), marking
it in `GlobalsWithExplicitUnit` so var-unit-wins fires during sweeps; the swept axis is tagged from the
same base unit. Together these fix swept-frequency double-unit application (sim ran at 1e18 Hz instead
of 1e9) and the latent no-sweep `value=2 GHz` VAR + `… GHz` use-site double.

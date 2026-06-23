# Sonnet Brief — Analysis-editor frequency preview: mirror engine var-unit-wins

**Follow-up to `brief-analysis-freq-expr-unit.md` (land that first).** That brief fixed storage and
engine resolution but left the editor's `≈` preview computing through `FreqUnitHelper.ToHzExpr`, which
does not honor var-unit-wins. This brief fixes the preview only — no engine/storage/format change.

**Bug (precise mechanism).** The HB tone and S-param Start/Stop/Step previews compute
`Prev(FreqUnitHelper.ToHzExpr(coeff, fieldUnit))`. `ToHzExpr` always applies the **field** unit to a
symbolic coefficient. But `AnalysisPreviewHelper.ComputePreview` evaluates against `DesignScope`, which
binds every name to its **bare expression with `unit: null`** (deliberate — previews show raw numeric;
the engine `Units` table is ASCII-keyed vs. the glyph ComboBox). So a VAR `RFfreq = 2 GHz` is stored as
`Expression="2"`, `Unit="GHz"` and resolves to **2** in the preview scope. Net result:

- Field unit == referenced VAR's unit (or VAR has no unit): preview matches the engine. E.g.
  `RFfreq=2 GHz` + field `GHz` → `2 × 1e9` = ≈ 2e9 ✓; `RFfreq=2` + field `GHz` → ≈ 2e9 ✓.
- **Field unit ≠ the referenced VAR's unit:** preview applies the *field* unit while the engine applies
  **var-unit-wins** (the VAR's own unit, field ignored). E.g. `RFfreq=2 GHz` + field accidentally `MHz`
  → preview `2 × 1e6` = ≈ **2e6**, but the engine runs at **2e9**. The preview lies for exactly the
  unit-mismatch foot-gun the feature is meant to handle.

There is **no** double-application (no "2e21") — `DesignScope` strips the VAR unit, so only one
multiplier is ever applied. The fix is to apply the **right** one.

**Owner decision (LOCKED).** The preview must mirror the engine: if the expression references a variable
that declares its own frequency unit, preview using **that** unit (ignore the field dropdown); otherwise
apply the field unit. Frequency units (`Hz`/`kHz`/`MHz`/`GHz`) are ASCII — no glyph/UnitNormalizer issue
— so `FreqUnit.Multiplier` applies directly to a VAR's stored unit string.

This intentionally does **not** reuse `FreqUnit.ResolveHz`. `ResolveHz` assumes a referenced var's value
is *already in Hz* (true for the engine's `ResolvedGlobals`, false in the preview where `DesignScope`
left it raw). The preview must therefore apply the VAR's unit itself.

---

## Part A — `AnalysisPreviewHelper.ComputeFreqPreview`

Add to `src/Ui/ViewModels/AnalysisPreviewHelper.cs`:

```csharp
/// <summary>
/// Frequency-field preview that mirrors the engine's var-unit-wins rule.
/// Evaluates the raw coefficient against DesignScope (units stripped), then applies the unit of the
/// first referenced variable that declares a frequency unit; if none, applies the field unit.
/// Returns "≈ <Hz>" / "≈ unknown: X" / "" exactly like ComputePreview.
/// </summary>
public static string ComputeFreqPreview(string coeff, string fieldUnit, SchematicEditModel model)
{
    string expr = coeff.Trim();
    if (expr.Length == 0) return "";

    try
    {
        var ast   = Parser.Parse(expr);                       // Core
        var scope = DesignScope.Build(model);
        double v  = new Evaluator().Eval(expr, scope).AsReal(); // raw (units stripped by DesignScope)

        // var-unit-wins: first referenced name whose model param carries a non-empty unit.
        string? refUnit = null;
        foreach (var name in AstWalker.CollectRefs(ast))       // Core
        {
            string? u = LookupParamUnit(model, name);
            if (!string.IsNullOrEmpty(u)) { refUnit = u; break; }
        }

        double hz = v * FreqUnit.Multiplier(refUnit ?? fieldUnit);  // Core FreqUnit
        return "≈ " + FormatReal(hz);
    }
    catch (UnresolvedNameException ex) { return $"≈ unknown: {ex.Name}"; }
    catch { return ""; }
}

private static string? LookupParamUnit(SchematicEditModel model, string name)
{
    foreach (var c in model.Components)
        foreach (var p in c.Parameters)
            if (p.Name == name && !string.IsNullOrEmpty(p.Unit))
                return p.Unit;       // display string; frequency units are ASCII (Hz/kHz/MHz/GHz)
    return null;
}
```

Notes:
- **Bare-number coefficients:** unlike `ComputePreview` (which suppresses literals), DO show a preview
  here when `fieldUnit != "Hz"` — `2.4` + `GHz` should read `≈ 2.4e9`, confirming the unit applied. If
  `fieldUnit == "Hz"` and the coeff is a bare number, return `""` (no `≈ 2.4` noise), matching existing
  behavior. (`v` is already computed; just gate the empty-return on `IsBareNumber(expr) && fieldUnit ==
  "Hz"`.)
- `refUnit` is matched by the **flat** name lookup (consistent with `DesignScope`'s flat namespace). If
  a non-frequency unit somehow comes back (e.g. a VAR declared in `V`), `FreqUnit.Multiplier` returns
  `1.0` — acceptable; using a non-frequency var as a tone is user error.
- **Known approximation (document inline):** for a *mixed-unit compound* expression (e.g.
  `RFfreq + Voff` where only `RFfreq` has a unit) the single-multiplier result is approximate — this
  matches the engine's own var-unit-wins approximation for compound expressions. Realistic cases (bare
  reference, homogeneous scaling like `2*RFfreq`) are exact.

## Part B — HB tone previews

`src/Ui/ViewModels/HbBodyViewModel.cs` — replace every tone preview computation that goes through
`ToHzExpr` with `ComputeFreqPreview`:
- `OnToneCoeffChanged(v)`  → `TonePreview  = AnalysisPreviewHelper.ComputeFreqPreview(v, ToneUnit, _model);`
- `OnTone2CoeffChanged(v)` → `Tone2Preview = AnalysisPreviewHelper.ComputeFreqPreview(v, Tone2Unit, _model);`
- `OnToneUnitChanged(value)`  → after the existing `Rescale`, set
  `TonePreview = AnalysisPreviewHelper.ComputeFreqPreview(ToneCoeff, value, _model);`
- `OnTone2UnitChanged(value)` → likewise with `Tone2Coeff`, `value`.
- Non-frequency previews (`MaxHarmonic`, `Tol`, `GuardHarmonic`, `Lambda`, `MaxIter`) keep using
  `Prev(...)` unchanged.
- `FromAnalysis`: no preview call needed (the `OnXChanged` partials fire as the staged props are set),
  but if any explicit preview seeding exists there, route it through `ComputeFreqPreview` too.

## Part C — S-param Start/Stop/Step previews

`src/Ui/ViewModels/FrequencySpecViewModel.cs` — same substitution:
- `OnStartCoeffChanged(v)` → `StartPreview = AnalysisPreviewHelper.ComputeFreqPreview(v, StartUnit, _model);`
  (and `OnStopCoeffChanged`/`OnStepCoeffChanged` with their units).
- The existing `OnStartUnitChanged`/`OnStopUnitChanged`/`OnStepUnitChanged` handlers: after the
  `Rescale`, recompute the preview via `ComputeFreqPreview(StartCoeff, value, _model)` (etc.).
- The seed constructor's preview lines (`StartPreview = Prev(FreqUnitHelper.ToHzExpr(...))`) →
  `StartPreview = AnalysisPreviewHelper.ComputeFreqPreview(startExpr, startUnit, _model);` (and
  Stop/Step). `NumPointsExpr` preview stays on `Prev`.

## Part D — Retire `ToHzExpr` from preview paths

After B and C, `FreqUnitHelper.ToHzExpr` has **no remaining callers** (storage was moved off it in the
prior brief). Delete `ToHzExpr` (and any now-unused private helpers), or, if you prefer to keep the
file stable, mark it `[Obsolete]` and assert no callers. Keep `Multiplier`/`Rescale`/`Split` (still
used for the unit-dropdown rescale and legacy numeric display). Confirm the build has zero references to
`ToHzExpr`.

---

## Tests (`tests/Ui.Tests`)
1. **FreqPreview_VarUnitWins_DifferentFieldUnit:** model with VAR `RFfreq` (`Expression="2"`,
   `Unit="GHz"`); `ComputeFreqPreview("RFfreq", "MHz", model)` → `"≈ 2e9"`-class (within G4 format),
   NOT `2e6`. This is the regression for the foot-gun.
2. **FreqPreview_VarUnitWins_SameFieldUnit:** same VAR, `ComputeFreqPreview("RFfreq","GHz",model)` →
   `≈ 2e9` (unchanged from today).
3. **FreqPreview_UnitlessVar_FieldApplies:** VAR `RFfreq` (`Expression="2"`, `Unit=""`);
   `ComputeFreqPreview("RFfreq","GHz",model)` → `≈ 2e9` (field unit applies).
4. **FreqPreview_NumericShowsWithUnit:** `ComputeFreqPreview("2.4","GHz",model)` → `≈ 2.4e9`;
   `ComputeFreqPreview("2.4","Hz",model)` → `""` (no noise on a plain Hz literal).
5. **FreqPreview_Unknown:** `ComputeFreqPreview("Nope","GHz",model)` → `"≈ unknown: Nope"`.
6. **FreqPreview_Compound_Homogeneous:** VAR `RFfreq=2 GHz`; `ComputeFreqPreview("2*RFfreq","MHz",model)`
   → `≈ 4e9` (var-unit-wins, exact for homogeneous scaling).
7. **No_ToHzExpr_Callers:** a guard (reflection or grep-style test, or confirmed at review) that no VM
   computes a preview via `ToHzExpr`.

## Gate
Build 0W/0E (TreatWarningsAsErrors). All Ui tests green. **Manual:** VAR `RFfreq = 2 GHz`; HB tone
`RFfreq`, set the field unit to `MHz` → the `≈` preview reads ≈ 2e9 (matching the actual run), not
2e6; set it back to `GHz` → still ≈ 2e9. Type `2.4` with `GHz` → preview ≈ 2.4e9. S-param Start
`f_lo` (a VAR in GHz) with field `MHz` → preview reflects the VAR's GHz value.

## On completion
Note in `src/Ui/CLAUDE.md`: the analysis-editor frequency previews now mirror the engine's
var-unit-wins rule via `AnalysisPreviewHelper.ComputeFreqPreview` (a referenced variable's own
frequency unit overrides the field-unit dropdown; `DesignScope` still resolves the raw value, the
helper applies the unit). `FreqUnitHelper.ToHzExpr` is retired — no remaining callers. Non-frequency
parameter previews are unchanged (raw numeric, units still deferred — the documented general
limitation).

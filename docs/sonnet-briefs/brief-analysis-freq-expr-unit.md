# Sonnet Brief — Carry frequency expression and unit as separate fields (HB tone + S-param sweep)

**Bug (two symptoms, one root cause).** The analysis editors bake the unit multiplier into the
expression string via `FreqUnitHelper.ToHzExpr` (`src/Ui/ViewModels/FreqUnitHelper.cs`). For a numeric
coefficient this yields a clean literal (`2.4` + `GHz` → `2.4e9`), but for a **symbolic** coefficient
(a VAR or expression) it yields `(RFfreq) * 1000000000`. That string then breaks in two independent
places:

- **Display round-trip.** `HbBodyViewModel.FromAnalysis` reopens the dialog with
  `FreqUnitHelper.Split(hb.ToneExpr)`. `Split` can only decompose a plain number, so it returns
  `("(RFfreq) * 1000000000", "Hz")` — the unit is lost and the raw baked string is shown.
- **`.cnl` serialization.** `CnlWriter.FormatHbAnalysis` emits `Tone=(RFfreq) * 1000000000`
  **unquoted**. `CnlReader.TokeniseLine` splits on whitespace, so the HB parser keeps only
  `Tone=(RFfreq)` and drops `* 1000000000`. The engine then evaluates `(RFfreq)` → 2 → **f0 = 2 Hz**,
  producing the user-visible failure: `Commensurability check failed: source 'P1' Freq=2E+09 Hz is not
  on the HB tone grid {f0=2 Hz, MaxHarm=5}`.

The S-parameter editor has the **identical latent bug** via `FrequencySpecViewModel.Build`
(`ToHzExpr(StartCoeff, StartUnit)` → `FrequencySpec.StartExpr`, emitted unquoted as `start=…`).

**Owner decision (LOCKED).**
1. Stop baking. Store the frequency as a **raw expression** plus a **separate unit** field. The engine
   (HB) / `FrequencySpec.Expand` (S-param) computes `value = eval(expr) × multiplier(unit)`.
2. **Keep the existing UI controls** — the number-box + unit-dropdown pair for HB tone, and the
   coeff-box + unit-dropdown per S-param Start/Stop/Step. This brief fixes the *storage* underneath; it
   does **not** switch to a single text box.
3. **Resolution rule ("var-unit-wins"):** if the expression references any global that was declared
   with an explicit unit, the resolved value is already in Hz → use it as-is and **ignore** the field
   unit. Otherwise multiply by the field unit. This makes both of these resolve to **2 GHz**:
   `RFfreq = 2` (no unit) + field `GHz`, **and** `RFfreq = 2 GHz` (unit on the VAR) + field `GHz`.
4. **Format change approved.** Extend the `.cnl` HB/S-param directives and the `.csch`/clipboard JSON
   with the unit fields. **Back-compatible:** absent unit ⇒ `Hz` (so every existing numeric file
   resolves ×1, unchanged).

Scope is **HB tone + S-param sweep only.** Loadpull/LPP `ToneExpr` share the pattern but have no
dropdown-based body editor in this pass — left as a flagged follow-up (see end). This brief is
independent of any sweep-range work (that is Brief 2 — `brief-sweep-range-units.md`).

---

## Part A — Core: shared unit helper + `globals-with-unit` provenance

**A1. New `src/Core/Expressions/FreqUnit.cs`** (framework-free; the single home for the rule so HB and
S-param resolve identically):

```csharp
public static class FreqUnit
{
    public static readonly string[] Units = ["Hz", "kHz", "MHz", "GHz"];

    public static double Multiplier(string? unit) => unit switch
    { "kHz" => 1e3, "MHz" => 1e6, "GHz" => 1e9, _ => 1.0 };  // null/"Hz"/"" → 1

    /// <summary>
    /// Resolve a frequency field to Hz. If the expression references any variable in
    /// <paramref name="globalsWithUnit"/>, the result is already in Hz (var-unit-wins) and the field
    /// unit is ignored; otherwise multiply by Multiplier(fieldUnit). A pure numeric literal references
    /// nothing → field unit applies.
    /// </summary>
    public static double ResolveHz(string expr, string? fieldUnit,
        IReadOnlyDictionary<string, Value> globals,
        IReadOnlyCollection<string>? globalsWithUnit)
    {
        // Build scope + evaluator exactly as the existing resolvers do (bind Real globals; inject
        // resolved values). Parse once; evaluate.
        var ast  = Parser.Parse(expr);
        double v = EvalReal(ast, globals);          // see note below
        if (globalsWithUnit is { Count: > 0 })
        {
            foreach (var name in AstWalker.CollectRefs(ast))   // existing AstWalker
                if (globalsWithUnit.Contains(name))
                    return v;                                   // var-unit-wins
        }
        return v * Multiplier(fieldUnit);
    }
}
```

- `EvalReal` mirrors the existing `HbEngine.Resolve.Num` body: `BuildScope`/`BuildEvaluator` over
  `globals`, `Parser.Parse`, `EvalExpr`, take `.AsReal()` (Complex → `.Real`). Centralize it here; HB
  should call `FreqUnit` rather than keep a private copy. Catch/parse failures: throw — callers decide
  the fallback (HB already wraps in try/catch with a default).
- `AstWalker.CollectRefs(ast)` already exists (Phase-3 SDD scope injection). Confirm it returns every
  `RefExpr` name; if its current entry point differs, add a thin `CollectRefs(Expr)` overload.

**A2. `ElaboratedNetlist` — record which globals carried an explicit unit.** Add a
`HashSet<string> GlobalsWithExplicitUnit` (Ordinal) with a `MarkGlobalHasUnit(string)` setter and a
read-only accessor. In `Elaborator` (`src/Core/Elaboration/Elaborator.cs`), where `ResolvedGlobals`
is populated (the `foreach (var v in tb.GlobalVariables)` loop after `BuildGlobalScope`), also call
`netlist.MarkGlobalHasUnit(v.Name)` when `!string.IsNullOrEmpty(v.Unit)`. (Cell-scoped variables are
out of scope — analysis directives resolve against globals.)

## Part B — Core model: separate unit fields

**B1. `HarmonicBalanceAnalysis` (`src/Core/Design/Analysis.cs`).** Add init-only:
- `public string ToneUnit { get; init; } = "Hz";`
- `public string[] ToneUnits { get; init; } = [];` (parallel to `ToneExprs`; entry *i* is the unit for
  `ToneExprs[i]`. Empty or shorter ⇒ missing entries default `"Hz"`.)

`ToneExpr`/`ToneExprs` now hold the **raw coefficient expression** (e.g. `"RFfreq"`, `"2.4"`), not a
baked Hz value.

**B2. `FrequencySpec` (`src/Core/Design/Analysis.cs`).** Add `StartUnit`/`StopUnit`/`StepUnit` (string,
default `"Hz"`) to both expression constructors (keep existing positional ctors working — add the units
as optional trailing params, default `"Hz"`). The `double`-based back-compat ctor sets all three to
`"Hz"` (its inputs are already Hz). Change `Expand` so each resolved value goes through
`FreqUnit.ResolveHz(expr, unit, globals, globalsWithUnit)`:
- Add an optional param: `Expand(IReadOnlyDictionary<string,Value>? globals = null,
  IReadOnlyCollection<string>? globalsWithUnit = null)`. Replace the private `ResolveExpr` numeric/eval
  body with a call to `FreqUnit.ResolveHz` (keep the fast numeric path inside `ResolveHz`). Step in
  StepSize mode is scaled the same way; PointCount's count is **not** a frequency (unchanged).
- `SParameterAnalysis.Expand` gains the same optional `globalsWithUnit` param and forwards it to each
  segment.

## Part C — `.cnl` reader/writer

**C1. `CnlWriter` (`src/Core/Netlist/CnlWriter.cs`).**
- `FormatHbAnalysis`: emit the tone **quoted** with a unit key:
  `Tone="{hb.ToneExpr}" ToneUnit={hb.ToneUnit}`. For multi-tone, emit each
  `Tone[i]="{expr}" ToneUnit[i]={unit}` (unit defaults `Hz` when the array is short).
- `FormatSParameterAnalysis`: emit `start="{f.StartExpr}" startUnit={f.StartUnit}
  stop="{f.StopExpr}" stopUnit={f.StopUnit}` and, in StepSize mode,
  `step="{f.StepExpr}" stepUnit={f.StepUnit}`.
- **Defensive hardening (do regardless):** quote every expression-valued analysis field that can contain
  whitespace/operators — also the loadpull/LPP `Tone=` emissions. Quoting is free; the readers already
  strip surrounding quotes. This closes the unquoted-whitespace corruption for any directly-typed
  expression like `2 * f0`.

**C2. `CnlReader` (`src/Core/Netlist/CnlReader.cs`).**
- `TryParseHbDirective`: read `ToneUnit` (default `"Hz"`) into `ToneUnit`; collect `ToneUnit[i]`
  parallel to the existing `Tone[i]` loop into `ToneUnits`. The kv loop already strips surrounding
  quotes — confirm `Tone="…"` round-trips to the unquoted expression.
- `TryParseSparamDirective` (the S-param parser): read `startUnit`/`stopUnit`/`stepUnit` (default
  `"Hz"`) and strip quotes from `start`/`stop`/`step`. **Verify** the S-param parser strips surrounding
  quotes the same way the HB parser does; if not, add it. Absent unit keys ⇒ `"Hz"` (back-compat: old
  files with baked numeric `start=2e9` resolve ×1).

## Part D — Engine: HB resolution + unit-aware commensurability

**D1. `HbEngine.Resolve` (`src/Engine/HarmonicBalance/HbEngine.cs`).** Replace the tone branch of the
local `Num` usage with `FreqUnit.ResolveHz`, passing `globals` and the netlist's
`GlobalsWithExplicitUnit`. `Resolve` is `static` and currently takes only `(hba, globals)` — add the
set as a third param (`Resolve(hba, globals, globalsWithUnit)`) and thread it from the two callers:
`SchematicRunService.RunTypedAnalysis` (`HbEngine.Resolve(hba, nl.ResolvedGlobals,
nl.GlobalsWithExplicitUnit)`) and `ParametricSweepEngine.RunInner` (use the per-point
`netlist.ResolvedGlobals` + `netlist.GlobalsWithExplicitUnit`). Non-tone numeric fields (MaxHarm, Tol,
…) keep the existing plain `Num`. Keep the try/catch default behavior on parse failure.

**D2. Unit-aware commensurability message (`HbEngine.CheckCommensurability` and
`CheckCommensurabilityMultiTone`).** When a source frequency is off-grid, before throwing, test whether
the ratio `source/f0` (or `f0/source`) is within tolerance of a power of 1000 (i.e.
`round(log10(ratio))` is a nonzero multiple of 3 and `10^that` reproduces the ratio to ~1e-6). If so,
append to the message: `" — this looks like a frequency-unit mismatch (off by ~1000×ⁿ); check the Tone
unit and your variable's units."` Still throw (refuse to run). This turns the cryptic failure into an
actionable one and would have explained the original bug (2 vs 2e9 = exactly 1e9).

## Part E — UI view-models (keep the two controls)

**E1. `HbBodyViewModel` (`src/Ui/ViewModels/HbBodyViewModel.cs`).**
- `BuildAnalysis`: store the **raw** coefficient and the unit separately. Single-tone:
  `ToneExpr = ToneCoeff`, `ToneUnit = ToneUnit`. Multi-tone: `ToneExprs = [ToneCoeff, Tone2Coeff]`,
  `ToneUnits = [ToneUnit, Tone2Unit]`. Stop calling `ToHzExpr` for storage.
- `FromAnalysis`: set `ToneCoeff = hb.ToneExpr`, `ToneUnit = hb.ToneUnit` directly (no `Split`). Same for
  Tone2 from `ToneExprs[1]`/`ToneUnits[1]`. **Legacy nicety (optional but recommended):** when
  `hb.ToneUnit` is `"Hz"`/empty **and** `ToneExpr` parses as a plain number, run the old
  `FreqUnitHelper.Split` to recover a pretty `(coeff, unit)` for display (so a legacy `Tone=2.4e9`
  still shows as `2.4 GHz`). Never `Split` a non-numeric expression.
- Previews: the `≈` preview must reflect the **resolved** value including var-unit-wins. Update
  `Prev`/`AnalysisPreviewHelper` usage so a symbolic coeff whose VAR carries a unit previews the var's
  Hz value (field unit ignored), and a numeric coeff previews `coeff × Multiplier(unit)`. The schematic
  edit model already exposes VAR rows + units (see `GetKnownVarNames` in `SweepAxisRowViewModel`); reuse
  that to know which refs have units. Keep it best-effort (preview only).

**E2. `FrequencySpecViewModel` (`src/Ui/ViewModels/FrequencySpecViewModel.cs`).**
- `Build`: store raw coeff + unit into the `FrequencySpec` (`StartExpr = StartCoeff`,
  `StartUnit = StartUnit`, etc.) instead of `ToHzExpr`. Construct `FrequencySpec` with the new unit
  params.
- ctor (seed path): set `_startCoeff = seed.StartExpr`, `_startUnit = seed.StartUnit` directly (drop the
  `Split` call), with the same legacy-numeric pretty-`Split` nicety as E1. Same for Stop/Step.
- Previews: same var-unit-wins treatment as E1.

**E3. `FreqUnitHelper` (`src/Ui/ViewModels/FreqUnitHelper.cs`).** `ToHzExpr` is no longer used for
storage. Keep `Multiplier`/`Rescale`/`Split` for the unit-dropdown rescale and legacy display; have
`Multiplier` delegate to `Core` `FreqUnit.Multiplier` to avoid drift (or leave as-is and add a comment
that the two must agree). Do **not** leave `ToHzExpr` wired into any Build path.

## Part F — JSON DTO (`.csch` / clipboard / `.canl`)

`AnalysisSerialization.cs` (`src/Ui/Schematic/`):
- `CschAnalysis`: add `ToneUnit` (string?) and `ToneUnits` (string[]?). `FrequencySpec` DTO: add
  `StartUnit`/`StopUnit`/`StepUnit` (string?).
- `ToDto` (HB arm): write `ToneUnit = hb.ToneUnit`, `ToneUnits = hb.ToneUnits.Length > 0 ? … : null`.
  FrequencySpec arm: write the three units.
- `FromDto`: read them back with `?? "Hz"` defaults (back-compat for DTOs written before this change).
  HB arm: `ToneUnit = dto.ToneUnit ?? "Hz"`, `ToneUnits = dto.ToneUnits ?? []`. FrequencySpec arm: pass
  the units into the `FrequencySpec` ctor.

---

## Tests

Core (`tests/Core.Tests`):
1. **FreqUnit_ResolveHz_NumericTimesUnit:** `ResolveHz("2.4","GHz",{},∅) == 2.4e9`; `"2","Hz" == 2`.
2. **FreqUnit_VarUnitWins:** globals `{RFfreq:2e9}`, `globalsWithUnit={RFfreq}` →
   `ResolveHz("RFfreq","GHz",…) == 2e9` (field GHz ignored). With `globalsWithUnit=∅` →
   `ResolveHz("RFfreq","GHz",{RFfreq:2}) == 2e9` (field applies to the unit-less var).
3. **Hb_Cnl_RoundTrip_SymbolicTone:** write a HB analysis `ToneExpr="RFfreq" ToneUnit="GHz"` via
   `CnlWriter`, read back via `CnlReader`; assert `ToneExpr=="RFfreq"`, `ToneUnit=="GHz"` (no
   `* 1000000000` mangling). Include a `2 * f0` expression to prove quoting survives whitespace.
4. **Sparam_Cnl_RoundTrip_SymbolicStart:** same for an S-param segment `start="f_low" startUnit="GHz"`.
5. **Cnl_BackCompat_NumericTone:** an old-style `Tone=2.4e9` (no `ToneUnit`) reads as
   `ToneExpr=="2.4e9"`, `ToneUnit=="Hz"`; `FrequencySpec` old `start=2e9` → `StartUnit=="Hz"`.
6. **FrequencySpec_Expand_AppliesUnit:** a PointCount segment `start="1" GHz`, `stop="5" GHz`, n=5 →
   `[1e9,2e9,3e9,4e9,5e9]`; var-unit-wins path covered.

Engine (`tests/Engine.Tests`):
7. **Hb_Resolve_VarToneGHz:** globals from a VAR `RFfreq=2` (no unit) + `ToneExpr="RFfreq"
   ToneUnit="GHz"` → `p.ToneHz == 2e9`. And VAR `RFfreq=2 GHz` (unit) + same tone → `2e9` (field
   ignored). This is the regression for the reported bug.
8. **Hb_Commensurability_UnitMismatchMessage:** f0 resolves to 2 (or 2e15) while a P1Tone source is at
   2e9 → the thrown message contains the "frequency-unit mismatch" hint.

UI (`tests/Ui.Tests`):
9. **HbBody_RoundTrip_KeepsCoeffAndUnit:** build from `(ToneCoeff="RFfreq", ToneUnit="GHz")` →
   `BuildAnalysis` → `FromAnalysis` → coeff `"RFfreq"`, unit `"GHz"` (Issue 1 regression). Numeric
   `("2.4","GHz")` round-trips too; legacy `ToneExpr="2.4e9"` displays as `("2.4","GHz")`.
10. **AnalysisSerialization_RoundTrip_Units:** `CschAnalysis` ToDto→FromDto preserves `ToneUnit`/
    `ToneUnits` and the FrequencySpec units; a DTO missing them loads with `"Hz"`.

## Gate
Build 0W/0E (TreatWarningsAsErrors). All Core/Engine/Ui tests green; Hero fixtures still load/run
(numeric tones unchanged → byte-identical results). **Manual repro:** VAR `RFfreq = 2`, HB Tone field
`RFfreq` with unit `GHz`, OK → reopen dialog shows `RFfreq` + `GHz` (not `(RFfreq) * 1000000000` + Hz);
Run → f0 = 2 GHz, commensurability passes, sim runs. Then set the VAR to `RFfreq = 2 GHz` and the field
unit to `MHz` → still resolves to 2 GHz (var-unit-wins), no error.

## On completion
Update `src/Core/CLAUDE.md` and `src/Ui/CLAUDE.md`: frequency analysis fields (HB `Tone`/`Tone[i]`,
S-param `start/stop/step`) now store a **raw expression + a separate unit** (`ToneUnit`,
`startUnit`/…); resolution is `eval(expr) × Multiplier(unit)` with **var-unit-wins** (a referenced
variable that declares its own unit overrides the field unit), centralized in
`Core/Expressions/FreqUnit.cs`. `.cnl`/`.csch` carry the unit keys (absent ⇒ `Hz`, back-compatible);
`CnlWriter` now quotes expression-valued analysis fields. `HbEngine.Resolve` takes
`GlobalsWithExplicitUnit`; commensurability errors flag suspected 1000× unit mismatches. The old
`FreqUnitHelper.ToHzExpr` baking path is retired from storage.

**Flagged follow-up (not in this brief):** loadpull/LPP `ToneExpr` still bakes via the analysis editor
and should get the same `(expr, unit)` treatment in a later pass.

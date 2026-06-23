# Sonnet Brief — Parametric-sweep range units (general Units; default from swept VAR)

**Bug.** A parametric sweep over a unit-bearing VAR silently drops the unit, so the sweep runs at the
wrong magnitude. Two reasons:

- **UI drops it.** `SweepAxisRowViewModel` (`src/Ui/ViewModels/SweepAxisRowViewModel.cs`) declares a
  `_unit` field ("Optional display unit") but **never applies it**: `BuildValues`/`BuildSpec` resolve
  Start/Stop/Step to plain doubles via `TryResolve` and ignore `_unit`. So a row "1 … 5" with unit GHz
  materializes `[1,2,3,4,5]`, not `[1e9,…]`.
- **Engine drops it.** `ParametricSweepEngine.Run` (`src/Engine/ParametricSweepEngine.cs`) injects each
  point as `new Variable(sweep.SweepVarName, val.ToString("G17"))` — **unit = null** — so even the
  original VAR's declared unit (e.g. `RFfreq = 2 GHz`) is discarded; the elaborator resolves the
  override as a bare number in base units.

Net effect: sweeping `RFfreq` (a GHz frequency VAR) over `1 … 5` runs at **1 … 5 Hz**. This is the same
class of unit bug as `brief-analysis-freq-expr-unit.md`, reached through the sweep path, and it applies
to sweeps wrapping **any** analysis (DC / SP / HB / LP / LPP) since they all wrap the one
`ParametricSweepAnalysis`.

**Owner decision (LOCKED).**
1. The sweep range carries a **unit**, applied at build so `SweepValues` stays **materialized in base
   units** (preserves the "values always materialized" design; the engine injection needs no change for
   correctness).
2. Sweep units are **general** (V, A, W, F, Hz, dBm, Ω, SI prefixes …) — use the existing Core `Units`
   table, **not** the Hz/kHz/MHz/GHz frequency dropdown.
3. **A sweep *writes* the variable** (its declared value is overridden), so the range needs its own
   unit. The unit **defaults to the swept VAR's declared unit** and is user-overridable. So sweeping
   `RFfreq` (GHz) over `1 … 5` with a blank unit inherits GHz → `1e9 … 5e9`; sweeping `Vds` inherits V.
4. **Format change approved.** Persist the unit on `SweepSpec` and in `.cnl`/`.csch` so the row
   round-trips as "1 GHz", not "1000000000". **Back-compatible:** absent unit ⇒ base (existing files
   unchanged).

Depends on `brief-analysis-freq-expr-unit.md` only for the *general* `Units` table (already in Core) —
land that brief first, but this one touches different files and can be reviewed independently.

---

## Part A — Core model: unit on `SweepSpec`

`SweepSpec` (`src/Core/Design/Analysis.cs`) currently stores `Start`/`Stop`/`StepOrCount` as the
user's raw coefficients (doubles) plus `Mode`/`Kind`. Add:
- `public string Unit { get; } = "";` (empty ⇒ base units / scale 1).

Constructor gains a trailing optional `string unit = ""`. The **spec constructor** of
`ParametricSweepAnalysis` (the arm that calls `SweepExpander.ExpandSweep`) applies the unit when
materializing:
- Resolve `double m = Units.Scale(spec.Unit)` (treat empty/unknown as 1.0; `Units.Scale` already exists
  and covers SI prefixes + identity units — confirm it returns 1.0 for `""`).
- Multiply **Start and Stop by `m`**. Multiply **Step by `m` only in StepSize mode**; in PointCount mode
  the count is dimensionless (do **not** scale). Log kind: scale Start/Stop (and Step if StepSize), not
  the count. So: `ExpandSweep(spec.Start*m, spec.Stop*m, spec.Mode==StepSize ? spec.StepOrCount*m :
  spec.StepOrCount, spec.Mode, spec.Kind)`.

`SweepSpec.Start`/`Stop`/`StepOrCount` keep storing the **coefficients** (unscaled) so the editor and
writer can re-emit "1 GHz". Only `ParametricSweepAnalysis.SweepValues` (the materialized array) is in
base units. The result sweep axis (`new Axis(sweep.SweepVarName, sweep.SweepValues)`) is therefore in
base units — correct and consistent with everything downstream.

## Part B — Engine: keep injection raw (now correct), add a guard comment

`ParametricSweepEngine.Run`: **no behavioral change needed** — because `SweepValues` are now base-unit
numbers, injecting them as a unit-less `Variable` is correct. Add a short comment at the injection site
noting that `SweepValues` are pre-scaled to base units (so the `unit = null` override is intentional and
must NOT be "fixed" by re-applying a unit). Do **not** pass `origVar.Unit` here — that would
double-apply.

(If a future explicit-list `Values=` path needs units, that's out of scope; `Values=` stays base-unit
numbers, see Part C.)

## Part C — `.cnl` reader/writer

`CnlWriter.FormatParametricSweepAnalysis` (`src/Core/Netlist/CnlWriter.cs`), the `Spec` arm: after
`Start=`/`Stop=`/(`Step=`|`Npts=`), emit `Unit={spec.Unit}` when `spec.Unit` is non-empty. (The
explicit-list `Values=` arm is unchanged — base-unit numbers.)

`CnlReader.TryParseParametricSweepDirective` (`src/Core/Netlist/CnlReader.cs`), the spec form: read
`Unit` (default `""`) and pass it into the `SweepSpec` ctor. Absent `Unit` ⇒ `""` ⇒ scale 1
(back-compat: existing files with base-unit Start/Stop expand identically). Keep `Start`/`Stop`/`Step`
parsed as doubles (coefficients).

## Part D — UI: wire the unit; default from the swept VAR

`SweepAxisRowViewModel` (`src/Ui/ViewModels/SweepAxisRowViewModel.cs`):
- **Apply the unit at build.** In `BuildValues` and `BuildSpec`, after resolving Start/Stop/Step,
  multiply by `Units.Scale(EffectiveUnit)` (Start/Stop always; Step only in StepSize mode; never the
  PointCount count). `BuildSpec` stores the **coefficients** plus `EffectiveUnit` on the `SweepSpec`
  (so the materialized values come out base-unit via Part A, and the row round-trips).
- **`EffectiveUnit`** = `Unit` if the user set one; else the swept VAR's declared unit. Look the VAR up
  by `VarName` in the schematic edit model (the same source `GetKnownVarNames` already reads — extend it
  / add a `GetVarUnit(name)` helper that returns the VAR row's unit string, or `""`). Expose
  `EffectiveUnit` so the AXAML can show the inherited unit (e.g. placeholder/greyed default) rather than
  leaving the box blank-looking.
- **Restore (`FromPsa`)** : set `vm.Unit = spec.Unit` (and keep restoring Start/Stop/Step coefficients
  as today). List-mode rows have no unit (base values).
- **Preview** (`Preview` getter): `BuildValues()` already feeds it; since values are now base-unit, the
  preview shows the true swept range (e.g. "5 pts: 1e9 … 5e9"). Confirm `Fmt` renders large values
  sensibly (it switches to G4 above 1e6 — fine).
- Note `TryResolve` already evaluates VAR/expression coefficients via `AnalysisPreviewHelper` — keep
  that (a coefficient may itself be an expression like `f_lo`); the unit multiplies the resolved
  coefficient. (`f_lo` resolving to a unit-bearing var is an edge case; for the sweep range the chosen
  field/inherited unit governs — do not apply var-unit-wins here. Document this in the row's summary.)

## Part E — JSON DTO (`.csch` / clipboard / `.canl`)

`AnalysisSerialization.cs` (`src/Ui/Schematic/`): the `CschAnalysis` PSA fields already carry
`PsaStart`/`PsaStop`/`PsaStepOrCount`/`PsaMode`/`PsaKind` (per `brief-sweep-revamp-1-persistence`). Add
`PsaUnit` (string?). `ToDto` (sweep arm): write `PsaUnit = spec.Unit`. `FromDto`: read `?? ""` and pass
into the `SweepSpec` ctor. Absent ⇒ base (back-compat).

---

## Tests

Core (`tests/Core.Tests`):
1. **SweepSpec_AppliesUnit_StepSize:** `SweepSpec(1, 5, 1, StepSize, unit:"GHz")` via the
   `ParametricSweepAnalysis` spec ctor → `SweepValues == [1e9,2e9,3e9,4e9,5e9]`. Empty unit → `[1..5]`
   (back-compat).
2. **SweepSpec_AppliesUnit_PointCount:** `SweepSpec(1, 5, 5, PointCount, unit:"GHz")` →
   `[1e9,2e9,3e9,4e9,5e9]` (count not scaled).
3. **Sweep_Cnl_RoundTrip_Unit:** write a spec-form parametric sweep with `Unit=GHz`; read back;
   `spec.Unit=="GHz"` and Start/Stop coefficients unchanged; materialized values are base-unit. A file
   with no `Unit=` reads `Unit==""` and expands to base.

Engine (`tests/Engine.Tests`):
4. **Sweep_OverFrequencyVar_BaseUnits:** VAR `RFfreq = 2 GHz`; a parametric sweep of `RFfreq` over
   `1 … 3 GHz` wrapping a trivial analysis → at each point `netlist.ResolvedGlobals["RFfreq"]` is
   `1e9/2e9/3e9` (not `1/2/3`). This is the regression for the dropped-unit bug.
5. **Sweep_Hb_FrequencySwept:** the integration case — a parametric sweep of a frequency VAR used as the
   HB `Tone` (after Brief 1) runs at the correct GHz frequencies and passes commensurability at every
   point (no `f0=N Hz` failure).

UI (`tests/Ui.Tests`):
6. **SweepRow_DefaultsUnitFromVar:** with a known VAR `RFfreq` declared as GHz and the row's `Unit`
   blank, `EffectiveUnit=="GHz"` and `BuildValues()` returns base-unit values. Setting `Unit="MHz"`
   overrides → values scale by 1e6.
7. **SweepRow_RoundTrip_Unit:** `BuildSpec` → `FromPsa` preserves `Unit` and the Start/Stop/Step
   coefficients; the displayed row reads "1 GHz", not "1000000000".
8. **AnalysisSerialization_RoundTrip_PsaUnit:** `CschAnalysis` ToDto→FromDto preserves `PsaUnit`; a DTO
   without it loads as base.

## Gate
Build 0W/0E (TreatWarningsAsErrors). All Core/Engine/Ui tests green; existing sweep fixtures (base-unit
Start/Stop, no `Unit=`) expand byte-identically. **Manual repro:** VAR `RFfreq = 2 GHz`; add a
parametric sweep of `RFfreq`, Start 1, Stop 5, Step 1, unit left blank → preview shows `1e9 … 5e9`;
Run an HB (Tone `RFfreq`) wrapped by the sweep → each frequency point solves at the right GHz value.
Reopen the editor → the row shows "1 … 5 GHz" (inherited), not raw Hz.

## On completion
Update `src/Core/CLAUDE.md` and `src/Ui/CLAUDE.md`: parametric-sweep ranges now carry a **unit**
(general `Units` table), defaulting to the **swept VAR's declared unit** and user-overridable; the unit
is applied at materialization so `SweepValues` are base-unit (engine injection stays unit-less and is
now correct). `SweepSpec.Unit` round-trips through `.cnl` (`Unit=`) and `.csch` (`PsaUnit`), absent ⇒
base (back-compatible). Fixes parametric sweeps over a unit-bearing VAR (e.g. frequency) silently
running at the wrong magnitude.

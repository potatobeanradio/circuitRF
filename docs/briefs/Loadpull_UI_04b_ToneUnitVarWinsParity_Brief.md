# Brief — Loadpull UI 04b: tone-unit parity with HB (var-unit-wins) — PREREQUISITE

**Goal:** Make the Loadpull **and** Loadpull-Pursuit **tone** resolve to Hz with the **exact same
var-unit-wins rule HB uses**, so a VAR with *or without* a unit works as the tone with no glitches —
matching the behavior the owner stabilized for HB. Today the loadpull/pursuit engines resolve the tone with
a plain evaluator (no field unit, no `globalsWithUnit`), so a unitless VAR (`RFfreq = 2`) becomes **2 Hz**
instead of 2 GHz. This brief brings them to HB parity.

**Do this FIRST in Track B** — briefs 04 (serialization), 05 (LP authoring), and 06 (LPP authoring) all
depend on the tone carrying a unit.

**Scope honesty:** this touches the **directive-resolution layer** — the model (`Analysis.cs`), the two
`Resolve` methods, their call sites, and the `.cnl` reader/writer. It does **NOT** touch the numeric core
(the 2-D sweep, HB solves, pursuit search are unchanged). It mirrors the already-tested HB path one-for-one,
so it is low-risk despite touching engine files.

**Reads with:** `src/Core/Expressions/FreqUnit.cs`, `src/Engine/HarmonicBalance/HbEngine.cs`
(`Resolve` + the `ToneHz` local), `src/Engine/Loadpull/LoadpullEngine.cs` (`Resolve`),
`src/Engine/Loadpull/LoadpullPursuitEngine.cs` (the `PursuitParams` resolution), and `Analysis.cs`.

## The HB pattern to mirror (the source of truth)

```csharp
// FreqUnit.ResolveHz: var-unit-wins. If expr references a VAR in globalsWithUnit, the resolved
// value is ALREADY in Hz (field unit ignored); otherwise value × Multiplier(fieldUnit). A pure
// literal references no var, so the field unit always applies.
public static double ResolveHz(string expr, string? fieldUnit,
    IReadOnlyDictionary<string, Value> globals,
    IReadOnlyCollection<string>? globalsWithUnit) { … }

// HbEngine.Resolve(hba, globals, globalsWithUnit):
double ToneHz(string expr, string unit)
{
    try { return FreqUnit.ResolveHz(expr, unit, globals, globalsWithUnit); }
    catch { return 1e9; }
}
```
HB carries `ToneExpr` + `ToneUnit` (default `"Hz"`) on the model, and the caller passes
`globalsWithUnit` from **`ElaboratedNetlist.GlobalsWithExplicitUnit`** (grep the HB call site to confirm the
exact property name and source).

## Changes

### 1 — Model (`src/Core/Design/Analysis.cs`)
Add a `ToneUnit` field to **both** loadpull analysis types, next to `ToneExpr`, defaulting to `"Hz"`
(default preserves back-compat: an existing Hz-literal tone resolves to the same value):
```csharp
public string ToneExpr { get; init; } = "0";
public string ToneUnit { get; init; } = "Hz";   // ← add to LoadpullAnalysis AND LoadpullPursuitAnalysis
```

### 2 — Loadpull engine resolve (`LoadpullEngine.Resolve`)
- Add a parameter `IReadOnlyCollection<string>? globalsWithUnit = null` (same signature shape as
  `HbEngine.Resolve`).
- Replace the plain tone line:
  ```csharp
  double tone = Num(lpa.ToneExpr, 1e9);                       // ← remove
  ```
  with the HB-style var-unit-wins resolution:
  ```csharp
  double tone;
  try   { tone = FreqUnit.ResolveHz(lpa.ToneExpr, lpa.ToneUnit, globals, globalsWithUnit); }
  catch { tone = 1e9; }
  ```
  Leave every other field resolving through the existing `Num(...)` (only the **tone** is frequency-unit
  sensitive; Pin/Compression/etc. are dBm/dB/counts, not frequencies).

### 3 — Pursuit engine resolve (`LoadpullPursuitEngine` — the `PursuitParams` builder)
Find the method that resolves a `LoadpullPursuitAnalysis` into `PursuitParams` (per
`src/Engine/Loadpull/CLAUDE.md`: `Resolve(LoadpullPursuitAnalysis, globals) → PursuitParams`). Apply the
identical change: add the `globalsWithUnit` parameter and resolve its tone via
`FreqUnit.ResolveHz(lpp.ToneExpr, lpp.ToneUnit, globals, globalsWithUnit)`. If the pursuit delegates tone
resolution to `LoadpullEngine.Resolve` internally, fix it once there and thread `globalsWithUnit` through.

### 4 — Call sites pass `globalsWithUnit`
Every caller of the two `Resolve` methods must pass `globalsWithUnit` from
`ElaboratedNetlist.GlobalsWithExplicitUnit` (the same source HB's caller uses). Call sites include the CLI
run dispatch, any engine tests, and the GUI run pipeline (the GUI side is wired in brief 07 — coordinate;
but do the CLI + test call sites here so the existing loadpull regressions can use VAR tones). The
default-null parameter keeps old call sites compiling, but update them to pass the set so VAR tones work
everywhere, not just where a literal is used.

### 5 — `.cnl` reader (`CnlReader`)
The loadpull / loadpull_pursuit `Tone=` directive must capture an **optional unit token** into `ToneUnit`,
exactly like the HB `Tone=` parse. Grep the HB tone-parse: `Tone=2 GHz` → `ToneExpr="2"`, `ToneUnit="GHz"`;
`Tone=2e9` → `ToneExpr="2e9"`, `ToneUnit="Hz"`; `Tone=RFfreq GHz` → `ToneExpr="RFfreq"`, `ToneUnit="GHz"`.
If the loadpull reader currently ignores/drops a trailing unit token, route it to `ToneUnit`; default
`"Hz"` when absent. **Confirm** the existing Hz-literal Hero3 `.cnl` tones still resolve to the same Hz
(back-compat: default `"Hz"` × literal = unchanged).

### 6 — `.cnl` writer (`CnlWriter`)
Emit the tone with its unit (`Tone=<expr> <unit>`) for loadpull/pursuit, matching the HB writer. (Brief 07
adds the rest of the directive emission; if 07 runs first, fold this into it — just ensure the unit token
is emitted and read back.)

## Tests (mirror the HB tone-unit tests — these are the regression guard)
For **both** `LoadpullEngine.Resolve` and the pursuit resolve:
- **Unitless VAR + field GHz** (the glitch case — MUST pass): `globals = {RFfreq = 2}` (no unit, not in
  `globalsWithUnit`), `ToneExpr = "RFfreq"`, `ToneUnit = "GHz"` → resolved tone = **2e9 Hz**.
- **Unit'd VAR (var-wins, no double-scale):** `globals = {RFfreq = 2e9}`, `globalsWithUnit = {RFfreq}`,
  `ToneExpr = "RFfreq"`, `ToneUnit = "GHz"` → tone = **2e9** (not 2e18).
- **Literal + field unit:** `ToneExpr = "2"`, `ToneUnit = "GHz"` → 2e9.
- **Hz back-compat:** `ToneExpr = "2e9"`, `ToneUnit = "Hz"` → 2e9.
- **Reader/writer round-trip:** `Tone=2 GHz` and `Tone=RFfreq GHz` round-trip `expr`+`unit` losslessly;
  `Tone=2e9` round-trips with unit `Hz`.

## Verify
1. `dotnet build` zero warnings; `dotnet test` green incl. the new resolve + round-trip tests.
2. Firewall passes (`FreqUnit` is Core, already referenced by the engine — no new framework dependency).
3. A loadpull whose `Tone` is a **unitless VAR** plus a GHz field unit runs at the correct frequency
   (spot-check via the CLI on a Hero3-style netlist with `RFfreq = 2` + `Tone=RFfreq GHz`).

## Downstream impact (do not skip)
- **Brief 04** (serialization): the DTO must carry `LpToneUnit` and map it in `ToDto`/`FromDto`. (Brief 04
  is updated to do this and to reference this brief.)
- **Briefs 05 / 06** (LP / LPP authoring): the tone field is a **coefficient + unit pair** (mirror
  `HbBodyViewModel`'s `ToneCoeff` + `ToneUnit` + `FreqUnitHelper` + `ComputeFreqPreview`), NOT a single
  combined expression string. `BuildAnalysis` sets both `ToneExpr` and `ToneUnit`. (Briefs 05/06 are updated
  to reflect this.)

# Sonnet Brief 4a — bare measurement name as a trace expression (`PDC`, not `measurements.PDC`)

Goal: a user can type a **bare** measurement name — `PDC` — into a trace's spec field and get a valid
trace, for both rank-0 (scalar) and rank-1 (swept) measurement cubes. Today rank-0 bare names fail with
"No cube references found. Use the form CubeName[:, 0, …]." and the user is forced to type
`measurements.PDC`. Data Display only; tiny, surgical.

Read first: `src/Ui/DataDisplay/CubeTraceSpecParser.cs` (the bare-name branch),
`ViewModels/TraceRowViewModel.cs` `CommitSpec`, and `RfCore/src/Data/DataSet.cs` `BareResolve`.
Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

## Root cause (verified on disk)

`RfCore.DataSet.BareResolve` **already** resolves a bare token in the default group then the
`measurements` group (MEAS-brief Part 1 has landed: `MeasurementsGroup = "measurements"` + the
default→measurements lookup are present; the old sole-populated-group fallback is gone). So
`ds.Contains("PDC")` / `ds["PDC"]` now succeed for a measurement cube. The remaining failure is in the
UI spec parser, and only for rank 0:

- `CommitSpec("PDC")` calls `CubeTraceSpecParser.TryParse("PDC", ds, …)`.
- In the bare-name branch (`bracketPos < 0`), after confirming `ds.Contains("PDC")`, it builds
  `bareTokens = Enumerable.Repeat(":", bareCube.Rank)` and recurses on the synthesized string.
- **Rank 1:** synth = `PDC[:]` → parses fine → `CommitSpec` sets `CubeName`/`Slice` → single-slice path. ✔
- **Rank 0:** `Repeat(":", 0)` is empty → synth = `PDC[]` → the recursive parse does `"".Split(',')` →
  one empty token → `tokens.Length (1) != cube.Rank (0)` → "Expected 0 axis token(s), got 1" → TryParse
  returns false → `CommitSpec` clears `CubeName`/`Slice`, keeps `Expression = "PDC"` → `TrySetCubeData`
  takes the Expression path → `TraceExpression` finds no `Name[` ref → **"No cube references found."** �’

## Fix — `CubeTraceSpecParser.TryParse`, bare-name branch
In the `if (bracketPos < 0) { … }` block, right after `var bareCube = ds[cubeName];` (i.e. after
`cubeName`/`transform` are set and before the `bareTokens`/synth lines), insert a rank-0 short-circuit:

```csharp
var bareCube = ds[cubeName];
if (bareCube.Rank == 0)        // scalar cube — no axes to slice
{
    slice = Array.Empty<AxisSlice>();
    return true;               // cubeName + transform already assigned above
}
var bareTokens = Enumerable.Repeat(":", bareCube.Rank).ToArray();
// … existing synth + recursive TryParse for rank ≥ 1 (unchanged) …
```

(Bonus robustness — the bracketed empty form `PDC[]`: in the normal bracket path, where it computes
`tokens` from `sliceStr.Split(',')`, treat an all-whitespace `sliceStr` as **zero** tokens so a rank-0
cube validates:
```csharp
var tokens = string.IsNullOrWhiteSpace(sliceStr)
    ? Array.Empty<string>()
    : sliceStr.Split(',').Select(t => t.Trim()).ToArray();
```
Then `tokens.Length == cube.Rank` holds for rank 0. Optional but makes `PDC[]` behave like `PDC`.)

After this, `CommitSpec("PDC")` succeeds: `CubeName = "PDC"`, `Slice = []` → `TrySetCubeData`'s rank-0
branch (already added by the scalars-on-Table brief) calls `SetScalarCubeData`, and the label round-trips
as bare `PDC` (via `BuildPickerExpression`'s rank-0 case, also already added).

## Out of scope (note, don't fix here)
A bare measurement name inside a **multi-cube** expression (e.g. `mag(PDC)` or `PDC*2`) still won't
resolve: `TraceExpression` only recognizes `Name[` references and builds its candidate list from
*qualified* names. Making bare names work inside expressions is a separate enhancement — call it out in
the brief's completion note but do not attempt it here. The single-token `PDC` case (the user's request)
is fully handled by the fix above.

## Tests — `tests/Ui.Tests` (CubeTraceSpecParser + a CommitSpec-level check)
1. **BareScalar_Resolves:** a DataSet with `measurements` group cube `PDC` (rank-0 real) →
   `CubeTraceSpecParser.TryParse("PDC", ds, …)` returns true, `cubeName == "PDC"`, `slice` is empty.
2. **BareScalar_WithTransform:** `TryParse("dB10 PDC", …)` (or `mag PDC`) → true, empty slice,
   `transform == dB10` (resp. `Mag`).
3. **BareRank1_Resolves:** a rank-1 `measurements` cube `Gain[freq]` → `TryParse("Gain", …)` → true,
   one `KeepAsX` slice entry (regression guard for the rank-1 path).
4. **EmptyBracketScalar (if the bonus is applied):** `TryParse("PDC[]", …)` → true, empty slice.
5. **CommitSpec_BareScalar_NoExprError:** on a cube-bound trace, `CommitSpec("PDC")` for a scalar
   measurement leaves `ExpressionError` null and binds `CubeName == "PDC"` with an empty slice (no
   fall-through to `TraceExpression`).

## Gate (manual)
Run a sim that defines `PDC` (scalar) and a swept measurement (rank-1). In Data Display, type `PDC`
into a trace's spec field on a **Table** → it binds and shows the value (no error). Type the rank-1
measurement name bare on a **Rect** → it plots. Neither requires the `measurements.` prefix.

## On completion
Note in `src/Ui/DataDisplay/CLAUDE.md`: bare measurement names (default- or measurements-group cubes)
are accepted in the spec field for all ranks; rank-0 binds via an empty slice. Bare names inside
multi-cube expressions remain qualified-only (future work in `TraceExpression`).

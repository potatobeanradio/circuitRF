# Sonnet Brief — 7.3b: family role — one Trace renders N curves (hard cap 101)

**Context.** Second 7.3 brief (after 7.3a axis-role picker). Adds the **family** role: a single `Trace` whose one
"family" axis is **iterated**, rendering **N curves** — one entry in the trace list, one style definition, one
delete. The user never materializes N traces. Canonical case: DC curve-tracer (`Id{Vds,Vgs}` → Vds = X, Vgs =
family). Performance guardrail: a **hard cap of 101 curves** (owner will tune later — see "Cap" below).

## What exists (from 7.3a + 7.2c)
- `AxisRole { PinToIndex, KeepAsX }` and `AxisSlice` in `Trace.cs`; per-axis role rows
  (`AxisRoleRowViewModel`) and the owner-side N-D slice resolver (name-matched `cube[args]`).
- `DataCube` slicing: int pins, `Range.All` keeps; `.Axis(name)`, `Axes[d].Values/Labels/Length`.
- `Trace.SetCubeData(...)` fills a **single** `Points` list from injected 1-D arrays; `BuildCubePath` maps it.

## 1. The cap — single named constant (easy to find)
Add **one** clearly-named constant the owner can edit for perf testing. Put it at the top of `Trace.cs` (or a tiny
`DataDisplayLimits` static if you prefer — but one obvious place):
```csharp
// ── Performance guardrail (Phase 7.3) ────────────────────────────────────────
// Max number of curves a single family trace will render. Hard cap for now;
// raise/lower here when running performance tests. Beyond this, the family is
// clamped to the first N indices and a one-time Message is emitted.
public const int MaxFamilyCurves = 101;
```
Reference `Trace.MaxFamilyCurves` everywhere the cap is enforced (do not hardcode 101 elsewhere).

## 2. Model — family descriptor + N curves on ONE Trace
Extend the role enum and let a trace hold N point-sets:
```csharp
public enum AxisRole { PinToIndex, KeepAsX, FamilyIterate }   // add FamilyIterate

// In Trace: exactly one axis may be KeepAsX and AT MOST one may be FamilyIterate (this brief: ≤1 family).
public bool IsFamily => Slice is not null && Slice.Any(s => s.Role == AxisRole.FamilyIterate);

// When IsFamily, the owner injects N curves instead of one.
// Each entry: the family index value (for legend) + its 1-D Points.
public sealed class FamilyCurve
{
    public double  AxisValue { get; init; }     // family-axis value at this index (legend)
    public string? AxisLabel { get; init; }     // Axis.Labels[k] if present, else null
    public List<System.Numerics.Vector2> Points { get; } = new();
}
public List<FamilyCurve> FamilyCurves { get; } = new();   // empty unless IsFamily
public string? FamilyAxisName { get; set; }               // the iterated axis (legend title)
```
Keep the existing single-curve `Points` for non-family traces unchanged.

## 3. Owner-side resolution — slice once per family index
In the N-D resolver (7.3a), branch when a family axis is present. Let `fDim` = the `FamilyIterate` axis, `xDim` =
the `KeepAsX` axis; all others pinned. Iterate the family axis indices, **capped**:
```csharp
var fAxis = cube.Axes[fDim];
int count = Math.Min(fAxis.Length, Trace.MaxFamilyCurves);
if (fAxis.Length > Trace.MaxFamilyCurves)
    messages.AddOnce("family-cap", $"Family '{fAxis.Name}' has {fAxis.Length} values; " +
        $"showing the first {Trace.MaxFamilyCurves}. Adjust the limit or pin/reduce the axis.");

var curves = new List<Trace.FamilyCurve>(count);
for (int k = 0; k < count; k++)
{
    var args = BuildArgs(cube, slice, familyIndex: k);  // family axis = int k, X = Range.All, others = pin
    var sliced = cube[args].Cube!;                       // rank-1
    var c = new Trace.FamilyCurve { AxisValue = fAxis.Values[k], AxisLabel = fAxis.Labels?[k] };
    // fill c.Points from sliced via the SAME transform/plotType mapping as SetCubeData
    curves.Add(c);
}
trace.SetFamilyData(curves, xAxisName, xUnit, fAxis.Name, plotType, freqUnit);
```
Add `Trace.SetFamilyData(IReadOnlyList<FamilyCurve> curves, string xAxisName, string? xUnit, string familyAxisName,
PlotType, FreqUnit)` that stores them and runs the per-curve point mapping (reuse the exact `BuildCubePath`
value/transform logic per curve — factor the inner value-map into a helper both `SetCubeData` and the family
path call, so dB20/dB10/Smith/Polar behavior is identical).

**Messages sink:** route the cap warning through the same display→workspace seam used by the 7.2e Z0 warning
(`AddOnce`-style, once per trace/source). If no sink is reachable at the resolver, surface it the same way 7.2e
did — match the existing pattern, don't invent a new dependency.

## 4. Rendering — draw N curves with auto style progression
Where the renderer draws a trace's `Points`, when `trace.IsFamily` iterate `FamilyCurves` and draw each with an
**automatic color/style progression** so curves are distinguishable:
- Derive each curve's color by stepping the trace's base palette index across the family (reuse the existing
  color-index progression used when adding multiple traces — `TraceProperties` color stepping; apply
  `+k` per curve, wrapping the palette). Keep line/marker style from the trace; vary **color** primarily.
- One legend entry per family with the **family axis name as the legend title** and each curve labeled by its
  `AxisLabel ?? AxisValue` + unit (e.g. `Vgs = 0.6 V`), per §2.7. (Minimal-label policy from 7.2c-b: the family's
  shared identity factors into the title; per-curve label is the family value.)
- Markers on a family: **out of scope this brief** — disable marker add on a family trace (guard `IsFamily`,
  like the existing `IsCubeBound` marker guards) and note it as a follow-on.

## 5. Picker (TraceRowViewModel / AxisRoleRowViewModel)
Add **Family** as a third role on the axis-role toggle (X / Pinned / Family). Constraints this brief:
- exactly one `X`; **at most one** `Family`; all others pinned.
- selecting Family on an axis that was X flips appropriately (auto-resolve like 7.3a's X handling).
- a Family axis hides its pin picker (it's iterated).
Write the role back into `Trace.Slice` and `RebuildAndNotify`.

## 6. Persistence (`.cdd`)
`Slice` already serializes per-axis `{AxisName, Role, Index}` — `FamilyIterate` round-trips as just another role
value (enum). **Do not** serialize `FamilyCurves`/`Points` (derived; rebuilt on load from the source like every
other cube trace). Confirm a family trace saves + reloads and re-expands to N curves.

## Tests (`tests/Ui.Tests`, headless)
1. **Family_RendersNCurves:** `Id{Vds(20), Vgs(5)}`, Vds=X, Vgs=Family → `FamilyCurves.Count == 5`, each has 20
   points; values match `cube[.., g]` per g.
2. **Family_Cap101:** a family axis of length 250 → `FamilyCurves.Count == 101` and the cap Message fires once.
   Changing `Trace.MaxFamilyCurves` changes the count (constant is the single source of truth).
3. **Family_LegendLabels:** each curve's label resolves to `AxisLabel ?? AxisValue`+unit; the family axis name is
   the legend title.
4. **Family_Roundtrips_Cdd:** save+reload a family trace → re-expands to the same N curves.
5. **OneTraceOneDelete:** a family is a single `TraceRowViewModel`; removing it removes all N curves.

## Gate
Build 0W/0E; tests green. Manual: from a 2-axis sweep cube, set X + Family → one trace draws a fan of curves with
stepped colors and a family legend; a >101 family clamps to 101 with one Message; save/reload preserves it;
editing the single trace's style restyles the whole family.

## On completion
Note in `src/Ui/CLAUDE.md`: a family trace is ONE `Trace` with a `FamilyIterate` axis rendering N curves
(`FamilyCurves`), auto color-stepped, family axis name as legend title, per-curve label = family value; hard cap
`Trace.MaxFamilyCurves = 101` (single constant, clamps + one Message past it); markers on families deferred.
This completes Phase 7.3 (DC/S-param `ParametricSweepEngine.RunInner` dispatch remains separate, gated engine
work).

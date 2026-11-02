# Sonnet Brief — SnP fixes: elaboration parse crash + symbol pin leads

Two issues found testing the shipped SnP component.

1. **(BUG — blocks all SnP sims) Elaboration parse crash on the `File` path.** Running an S-parameter
   sim with an SnP fails: `Elaboration failed: Parse error at position 0: Unexpected token '/'`. Root
   cause confirmed below. One-branch fix in `Elaborator.cs`.
2. **(Visual) SnP symbol has no pin leads.** The pins sit on the body edges with no line steps connecting
   the rounded-rect body out to the pin tips. Draw short lead lines from the body edge outward to each
   pin tip (like every other symbol's leads).

Build 0W/0E (TreatWarningsAsErrors) after each part.

---

## PART 1 — Fix the elaboration parse crash (the blocker)

### Root cause (confirmed)
`Elaborator.ResolveParameters` has dedicated branches for SDD/Z_Port/V_1Tone/V_nTone/P1Tone, but **no
branch for SnP**. SnP falls through to the generic path:
```csharp
var result = new Dictionary<string, Value>(StringComparer.Ordinal);
foreach (var ov in inst.Overrides)
    result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit);  // ← evaluates EVERY override as an expression
return result;
```
That calls `_evaluator.Eval(...)` on **every** override — including `File=/Users/.../amp.s2p`. The
expression parser reads the leading `/` of the absolute path as a division operator at position 0 →
`Parse error at position 0: Unexpected token '/'`. (`InterpMode=Cubic` and `ExtrapMode=NearestEdge`
would also wrongly evaluate as expressions and throw "unknown name", but the `File` path crashes first.)

`CreateSnpModel` (ComponentModelFactory) expects these as STRING values, not evaluated:
- `File` → `ValueKind.String` (the path, used verbatim)
- `InterpMode` → `ValueKind.String` ("linear" → Linear, else CubicSpline)
- `ExtrapMode` → `ValueKind.String` ("extrapolate" → WarnExtrapolate, else WarnClamp)
- `NumPorts` → `ValueKind.Real` (the only numeric one)

So the generic eval is simply wrong for SnP. The other string-param devices (SDD, Z_Port) already avoid
this by storing their string/equation params as `new Value(ov.Expression)` (raw string) instead of
evaluating them.

### Fix
Add an SnP dispatch in `ResolveParameters` (next to the other branches):
```csharp
if (inst.Reference.Equals("SnP", StringComparison.OrdinalIgnoreCase))
    return ResolveSnpParameters(inst, parentScope);
```
Add the resolver. String-valued params are stored raw (NOT evaluated); only genuinely numeric params
(`NumPorts`, and any future numeric ones) are evaluated:
```csharp
// SnP: File / InterpMode / ExtrapMode / PinConfig / RefNode are STRINGS — store raw, never Eval()
// (a file path like "/Users/…/x.s2p" is not an expression). Only NumPorts is numeric.
// String params the factory reads verbatim; PinConfig/RefNode are UI-only but harmless to carry.
private static readonly HashSet<string> _snpStringParams =
    new(StringComparer.OrdinalIgnoreCase) { "File", "InterpMode", "ExtrapMode", "PinConfig", "RefNode" };

private IReadOnlyDictionary<string, Value> ResolveSnpParameters(Instance inst, Scope parentScope)
{
    var result = new Dictionary<string, Value>(StringComparer.Ordinal);
    foreach (var ov in inst.Overrides)
    {
        if (_snpStringParams.Contains(ov.Name))
        {
            result[ov.Name] = new Value(ov.Expression);   // store the raw string; do NOT parse/eval
        }
        else
        {
            // NumPorts and any other numeric override — evaluate normally.
            try { result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit); }
            catch { /* skip unresolvable; factory will error if a required numeric is missing */ }
        }
    }
    return result;
}
```
> This mirrors `ResolveSddParameters`/`ResolveZPortParameters`, which already store their non-numeric
> params as `new Value(ov.Expression)`. The factory's `CreateSnpModel` already reads `File`/`InterpMode`/
> `ExtrapMode` as `ValueKind.String` and `NumPorts` as `ValueKind.Real`, so no factory change is needed.

> NOTE on the user's netlist values: `InterpMode=Cubic` and `ExtrapMode=NearestEdge` are not the strings
> `CreateSnpModel` switches on ("linear"/"extrapolate"), so they fall through to the CubicSpline / WarnClamp
> defaults — functionally fine (Cubic IS the default), just be aware the factory only special-cases
> "linear" and "extrapolate". No change required for this bug; flag if you think the SnP UI should emit
> the exact strings the factory matches.

**Part 1 tests:**
- Regression test: elaborate a testbench containing
  `SnP:S1  n1 n2 0  NumPorts=2  File=/abs/path/x.s2p  InterpMode=Cubic  ExtrapMode=NearestEdge`
  and assert it does NOT throw, the resulting `ElaboratedComponent.Parameters["File"]` is the verbatim
  path string, `["NumPorts"]` is Real 2, and the model is an `SnpModel` with `PortCount == 2`.
- Add a Windows-style path case too (`File=C:\data\x.s2p`) to be safe — backslashes shouldn't be parsed
  either (they're stored raw now, so this is covered, but assert it).
- The full netlist from the report (SnP + two Ports + sparam analysis) elaborates and runs end-to-end.

---

## PART 2 — Draw pin leads on the SnP symbol

Currently the SnP symbol places pins directly on the rounded-rect body edges with no connecting lead
lines. Add short lead segments stepping outward from the body edge to each pin tip, so SnP looks like
the rest of the library (R/L/C/Term/etc. all have visible leads from body to pin tip).

In the SnP symbol builder (`BuiltInSymbols.PrimitivesForSnp` — the per-(N, refNode, cfg, pitch) builder
added in the SnP brief), for EACH pin (signal pins and the reference pin), add a `LinePrimitive` from the
point on the body edge nearest the pin out to the pin tip:
- **Left-side pin** at tip (−X_tip, y): body left edge is at `−W/2`. Lead = `L(−W/2, y, −X_tip, y)`.
- **Right-side pin** at tip (+X_tip, y): `L(+W/2, y, +X_tip, y)`.
- **Top pin** at tip (0, −Y_tip): `L(0, −H/2, 0, −Y_tip)`.
- **Bottom pin** (incl. reference) at tip (0, +Y_tip): `L(0, +H/2, 0, +Y_tip)`.

where `W`/`H` are the body width/height used by the builder and `(±X_tip, …)`/`(…, ±Y_tip)` are the pin
tip coordinates already computed for the layout (the mid-edge positions for 1/2/3-port; the pitch-spaced
positions for N≥4). The lead simply connects the body edge to the existing tip — do NOT move the pin
tips (they must stay on grid and match `GenerateSnpPorts`).

Notes:
- Use the normal stroke tier and the default `SymbolColorRole.SymbolLine` (same as other symbols' leads),
  via the existing `L(...)` helper.
- Leads must be drawn for the placeholder→resolved transition correctly: the **placeholder** (no valid
  file, plain 200×200 RRect) has no pins and therefore no leads — unchanged.
- The body rect itself is unchanged; you're only ADDING lead lines. Pin tips, `GenerateSnpPorts`, and the
  glyph BB already account for the tips, so the BB still bounds everything (leads are inside the tip
  extent).
- Keep the port-number text primitives where they are (just inside the body edge).

**Part 2 check (visual):** place a 2-port SnP → each of the two pins shows a short lead from the body
edge to the pin tip; a 4-port (each layout) shows leads to all four pins; with Reference node on, the
reference pin also gets a lead; the placeholder (no file) is still a bare rounded square with no leads.

## Gate
Build 0W/0E. Tests green. Verify on disk:
- The reported netlist (SnP + 2 Ports + sparam) elaborates without the parse error and runs an
  S-parameter sweep using the Touchstone data.
- SnP symbols render with visible pin leads from the body to every pin tip (signal + reference),
  placeholder unchanged.

**On completion:** note in `src/Core/Elaboration/CLAUDE.md` (or the elaborator's notes) that SnP joins
SDD/Z_Port as a device whose string params (File/InterpMode/ExtrapMode/PinConfig/RefNode) are stored raw
and never expression-evaluated — adding a new string-valued primitive param requires the same treatment
or it will crash on non-expression values.

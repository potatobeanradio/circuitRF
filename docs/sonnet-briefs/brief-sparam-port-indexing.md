# Sonnet Brief — 1-based port indexing for S/Y/Z `i`/`j` axes (trace card + measurements)

## Problem
The `i`/`j` axes of a parameter cube store **1-based port numbers** in their `Values` (`[1, 2, …]`),
but the bracket shorthand and labels render the **0-based positional index**, so S11 shows as
`SP1.S[:, 0, 0]` / `SP1.S(i=0,j=0)` — reads like S00. Measurement bracket indexing has the same 0-based
behavior, and the measurement `S(i,j)` accessor has a latent axis-order bug.

## Goal
On the `i`/`j` port axes only, a bracket integer is a **1-based port number**, everywhere:
`SP1.S[:, 2, 1]` = S21, `SP1.S[:, 1, 1]` = S11. Identical in the trace card and in measurement
expressions. All other axes (freq, harmonic, sweep, node/branch) are unchanged. Internally the slice
`Index` stays 0-based — only the string boundary (generate/parse) and the measurement evaluator change.

Rule (document + comment): **an axis named `i` or `j` uses 1-based port numbers in bracket indices**
(these names are unique to S/Y/Z parameter cubes; their `Values` are `1..nPorts`).

Scope: `src/Ui/DataDisplay/Models/Trace.cs`, `src/Ui/DataDisplay/Models/TraceLabeler.cs`,
`src/Ui/DataDisplay/SliceTokenParser.cs`, `src/Core/Expressions/Evaluator.cs`, tests. Design docs are
handled separately. Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

---

## 1. `Trace.cs` — `BuildPickerExpression` (generate bracket form)
Replace:
```csharp
            var parts = Slice.Select(s =>
                s.Role == AxisRole.KeepAsX         ? ":"
                : s.Role == AxisRole.FamilyIterate ? "~"
                : !string.IsNullOrEmpty(s.Label)   ? $"\"{s.Label}\""
                :                                    s.Index.ToString());
```
with:
```csharp
            var parts = Slice.Select(s =>
                s.Role == AxisRole.KeepAsX         ? ":"
                : s.Role == AxisRole.FamilyIterate ? "~"
                : (s.AxisName is "i" or "j")       ? (s.Index + 1).ToString()   // 1-based port number (S[:, 2, 1] = S21)
                : !string.IsNullOrEmpty(s.Label)   ? $"\"{s.Label}\""
                :                                    s.Index.ToString());
```

## 2. `TraceLabeler.cs` — `BuildCubeQuantity` (legend `(i=…,j=…)` form)
Replace:
```csharp
                    sb.Append(s.AxisName);
                    sb.Append('=');
                    sb.Append(s.Index);
```
with:
```csharp
                    sb.Append(s.AxisName);
                    sb.Append('=');
                    // i/j are S/Y/Z port axes — show 1-based port numbers (i=1 ⇒ port 1).
                    sb.Append(s.AxisName is "i" or "j" ? s.Index + 1 : s.Index);
```

## 3. `SliceTokenParser.cs` — integer-token branch (parse trace-card text)

> This classifier is shared by **both** `CubeTraceSpecParser` (single-cube picker text) **and**
> `TraceExpression` (multi-cube expressions) — both pass `axis.Name` — so this one change makes
> `SP1.S[:, 2, 1]` mean S21 in the picker spec box *and* inside expressions like
> `dB20(SP1.S[:, 2, 1]) - dB20(SP1.S[:, 1, 1])`.
Replace:
```csharp
        // Integer index (pins/removes the axis).
        if (int.TryParse(tk, out int index))
        {
            if (index < 0 || index >= axisLength)
            { error = $"Index {index} out of range for axis '{axisName}' (0..{axisLength - 1})."; return new Token(Kind.Invalid); }
            return new Token(Kind.PinIndex, Index: index);
        }
```
with:
```csharp
        // Integer index (pins/removes the axis).
        if (int.TryParse(tk, out int index))
        {
            // S/Y/Z port axes (i, j) use 1-based PORT NUMBERS, not 0-based indices: S[:, 2, 1] = S21.
            if (axisName is "i" or "j")
            {
                if (index < 1 || index > axisLength)
                { error = $"Port {index} out of range for axis '{axisName}' (1..{axisLength})."; return new Token(Kind.Invalid); }
                return new Token(Kind.PinIndex, Index: index - 1);
            }
            if (index < 0 || index >= axisLength)
            { error = $"Index {index} out of range for axis '{axisName}' (0..{axisLength - 1})."; return new Token(Kind.Invalid); }
            return new Token(Kind.PinIndex, Index: index);
        }
```
(Ranges `a..b` and `:` on `i`/`j` keep their current index/whole-axis meaning — port-number semantics
apply to single-integer pins, the only case the picker emits. Note this in the doc.)

## 4. `Evaluator.cs` — measurement bracket + `S(i,j)` accessor

**4a. `ResolvePin`** — make the Real branch port-aware. Replace:
```csharp
        if (v.Kind == ValueKind.Real)
            return (int)v.AsReal();
        throw new ExpressionException(
            $"Index for axis '{axis.Name}' must be ':', a name, an integer, or a range — got {v.Kind}.");
```
with:
```csharp
        if (v.Kind == ValueKind.Real)
        {
            int n = (int)v.AsReal();
            // S/Y/Z port axes (i, j) use 1-based PORT NUMBERS resolved by axis value: S[:, 2, 1] = S21.
            if (axis.Name is "i" or "j")
            {
                for (int k = 0; k < axis.Values.Length; k++)
                    if ((int)Math.Round(axis.Values[k]) == n) return k;
                throw new ExpressionException(
                    $"Port {n} not found on axis '{axis.Name}'. Available ports: " +
                    $"[{string.Join(", ", axis.Values.Select(x => ((int)Math.Round(x)).ToString()))}].");
            }
            return n;
        }
        throw new ExpressionException(
            $"Index for axis '{axis.Name}' must be ':', a name, an integer, or a range — got {v.Kind}.");
```

**4b. `EvalQualifiedAccessor` — `S` branch** (already `-1` for ports, but indexes positionally as
`[pi, pj, All]`, which on a `[freq, i, j]` cube pins **freq**). Route through `DataSet.S` (name-keyed
port lookup, keeps freq + sweep, order-independent). Replace:
```csharp
        // ── S(portI, portJ) — S-parameter pair ───────────────────────────────
        if (accessorName == "S")
        {
            if (cl.Args.Length != 2) throw new ArityException(cl.Name, 2, cl.Args.Length);
            int pi = (int)EvalExpr(cl.Args[0], scope).AsReal() - 1;
            int pj = (int)EvalExpr(cl.Args[1], scope).AsReal() - 1;
            var sc = ds["S"];
            return SliceToValue(sc[new object[] { pi, pj, Range.All }]);
        }
```
with:
```csharp
        // ── S(portI, portJ) — S-parameter pair (1-based port numbers) ─────────
        if (accessorName == "S")
        {
            if (cl.Args.Length != 2) throw new ArityException(cl.Name, 2, cl.Args.Length);
            int pi = (int)EvalExpr(cl.Args[0], scope).AsReal();
            int pj = (int)EvalExpr(cl.Args[1], scope).AsReal();
            // DataSet.S resolves i/j by axis value (1-based) and keeps freq (+ sweep), order-independent —
            // unlike the previous positional slice, which assumed [i, j, freq] and pinned freq instead.
            return new Value(ds.S(pi, pj));
        }
```

---

## Tests
Trace card (`Trace` / `CubeTraceSpecParser` / `SliceTokenParser`):
1. **Generate_S11:** slice {freq:X, i:Pin0, j:Pin0} on `SP1.S` → `BuildPickerExpression()` == `"SP1.S[:, 1, 1]"`.
2. **Generate_S21:** {i:Pin1, j:Pin0} → `"SP1.S[:, 2, 1]"`.
3. **Legend_S21:** `TraceLabeler` quantity for the S21 slice contains `"(i=2,j=1)"`.
4. **Parse_S21:** `CubeTraceSpecParser.TryParse("SP1.S[:, 2, 1]", ds, …)` → slice has i.Index==1, j.Index==0.
5. **RoundTrip:** parse `"SP1.S[:, 2, 1]"` → regenerate → identical string.
6. **PortOutOfRange:** `"SP1.S[:, 3, 1]"` on a 2-port → false, error mentions `(1..2)`; `"SP1.S[:, 0, 1]"` → false.
7. **NonPortUnchanged:** an HB `V` cube `[:, "Vout", 1]` still parses the harmonic `1` as 0-based index.
8. **MultiCubeExpr:** `TraceExpression.TryEvaluate("dB20(SP1.S[:, 2, 1]) - dB20(SP1.S[:, 1, 1])", ds, …)`
   succeeds and equals dB20(S21) − dB20(S11) over freq (shared `SliceTokenParser` path).

Measurements (`Evaluator`):
8. **Bracket_S21:** `SP1.S[:, 2, 1]` equals `ds.S(2,1)` element-wise over freq.
9. **Bracket_S11:** `SP1.S[:, 1, 1]` equals `ds.S(1,1)`.
10. **Accessor_eq_Bracket:** `SP1.S(2,1)` equals `SP1.S[:, 2, 1]` (and is S21, not freq-pinned).
11. **Bracket_PortOutOfRange:** `SP1.S[:, 5, 1]` on a 2-port → ExpressionException naming available ports.
12. **SweptS:** on a swept `S` `[sweep, freq, i, j]`, `SP1.S[0, :, 2, 1]` pins sweep index 0 (0-based) and
    returns S21 over freq (sweep stays index-based; only i/j are ports).

## Gate (manual)
Trace card: add an S trace → Y-axis label / spec reads `SP1.S[:, 1, 1]`; set `j` port→2 → `SP1.S[:, 1, 2]`
= S12; typing `SP1.S[:, 2, 1]` selects S21. Measurement: `measure s21_db = dB(SP1.S[:, 2, 1])` matches
`dB(SP1.S(2,1))`.

## Notes / out of scope
- Ranges/`:`/`~` on `i`/`j` keep index/whole-axis meaning; only single-integer pins are port numbers.
- `Y`/`Z` measurement accessors (`SP1.Y(i,j)`) still hit the generic positional accessor; not produced as
  cubes today. The bracket path (`ResolvePin`) already handles `i`/`j` on any cube, so `SP1.Y[:, 2, 1]`
  would be port-correct if a Y cube existed. Routing the `Y`/`Z` accessors through `DataSet.Y`/`Z` is a
  trivial future parity follow-up.

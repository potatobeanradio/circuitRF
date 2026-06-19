# Sonnet Brief — bracket-index syntax in measurement expressions (`HB1.V[:, "Vout", 1]`)

Add numpy-style **bracket indexing** to the Core expression grammar so measurement equations accept the
same cube shorthand the trace card emits. Today measurements only accept the function-call accessor
(`HB1.V("Vout", 1, All)`); pasting a trace-card expression (`HB1.V[:, "Vout", 1]`) fails with
"Unexpected character '['". After this change both forms work and evaluate identically.

Semantics (measurement context): a bracket index is **positional** in cube-axis order. Per axis token:
`:` keeps the axis (whole), an integer or `"label"` **pins and drops** the axis, `a:b` keeps a range.
A fully-pinned index → scalar. `~` (the trace-card family marker) has no meaning here → a clear error.
This is exactly what the accessor does — `V[:, "Vout", 1]` ≡ `V("Vout", 1, All)` — so copy/paste from
the card "just works".

Scope: `src/Core/Expressions/{Token.cs, Ast.cs, Parser.cs, Evaluator.cs, AstWalker.cs}` + tests + a
`measurements.md` note. Purely additive — `[`/`]`/`~` were previously tokenizer errors, so no existing
expression changes behavior. Architectural firewall is clean: all of this is Core over RfCore slicing,
no UI dependency. Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

Read first: the five files above, and note the existing `EvalQualifiedAccessor` label-resolution + the
`cube[object[] args]` indexer (accepts `int`, `Range.All`, `System.Range`) → `SliceResult` →
`SliceToValue`.

## 1. `Token.cs` — three new tokens
Add to `TokenKind`:
```csharp
    // Indexing
    LBracket, RBracket, Tilde,
```
Add three switch cases in `NextToken` (next to the existing `'(' => …` line):
```csharp
            '[' => Advance(TokenKind.LBracket, start),
            ']' => Advance(TokenKind.RBracket, start),
            '~' => Advance(TokenKind.Tilde,    start),
```
(`Colon`, `Comma`, `StringLiteral` already exist; `1:4` already tokenizes as Number, Colon, Number.)

## 2. `Ast.cs` — the index node
```csharp
/// <summary>Kind of one token inside a cube index: ':' (whole), a pin (int/label), or 'a:b' (range).</summary>
public enum IndexTokenKind { Whole, Pin, Range }

/// <summary>One positional token of a cube index. Pin uses A; Range uses A (start) and B (end-exclusive).</summary>
public sealed record IndexToken(IndexTokenKind Kind, Expr? A = null, Expr? B = null);

/// <summary>Positional cube index: Target[token, token, …]. Mirrors the trace-card slice shorthand.</summary>
public sealed record IndexExpr(Expr Target, IndexToken[] Tokens) : Expr;
```

## 3. `Parser.cs` — postfix `[...]` (tightest binding)
Bracket indexing is a postfix that binds tighter than any infix op — apply it to the prefix result
before the infix loop. In `ParseExpr`:
```csharp
    private Expr ParseExpr(int minBp)
    {
        var left = ParsePrefix();
        left = ParsePostfix(left);     // NEW — cube indexing binds tightest
        while (true)
        { … existing infix loop unchanged … }
    }
```
Add the two helpers:
```csharp
    // Postfix cube indexing: Target[token, token, …]  (positional, numpy-style).
    private Expr ParsePostfix(Expr left)
    {
        while (Current.Kind == TokenKind.LBracket)
        {
            Advance();                              // consume '['
            var tokens = new List<IndexToken>();
            if (Current.Kind != TokenKind.RBracket)
            {
                tokens.Add(ParseIndexToken());
                while (Current.Kind == TokenKind.Comma)
                {
                    Advance();
                    tokens.Add(ParseIndexToken());
                }
            }
            Expect(TokenKind.RBracket, "]");
            left = new IndexExpr(left, [.. tokens]);
        }
        return left;
    }

    private IndexToken ParseIndexToken()
    {
        // ':'                 → keep whole axis
        if (Current.Kind == TokenKind.Colon)
        {
            Advance();
            return new IndexToken(IndexTokenKind.Whole);
        }
        // '~'                 → trace-card family marker; meaningless in a measurement
        if (Current.Kind == TokenKind.Tilde)
            throw new ParseException(
                "'~' (curve family) has no meaning in a measurement; use ':' to keep an axis " +
                "or a name/index to fix it.", Current.Position);

        // expr                → pin (int index or \"label\")
        // expr ':' expr       → range
        var first = ParseExpr(0);
        if (Current.Kind == TokenKind.Colon)
        {
            Advance();
            var second = ParseExpr(0);
            return new IndexToken(IndexTokenKind.Range, first, second);
        }
        return new IndexToken(IndexTokenKind.Pin, first);
    }
```
Notes: `ParseExpr(0)` for a bound stops at `:` on its own (the infix table returns "not an op" for
`Colon`, and ternary `:` only fires after a `?`), so `1:4` parses as a Range and `"Vout"`/`k` as a Pin.
The accessor (`HB1.V`) is produced by the existing `Identifier` prefix case, so `HB1.V[…]` arrives here as
`IndexExpr(CallExpr("HB1.V", []), …)`; a bare `PDC[…]` arrives as `IndexExpr(RefExpr("PDC"), …)`.

## 4. `Evaluator.cs` — evaluate the index
Add the switch arm in `EvalExpr`:
```csharp
        IndexExpr       ix => EvalIndex(ix, scope),
```
Add the method (place near `EvalQualifiedAccessor`):
```csharp
    // ── Positional cube index: Target[token, …]  (numpy-style; mirrors the accessor) ──────────
    private Value EvalIndex(IndexExpr ix, Scope scope)
    {
        var target = EvalExpr(ix.Target, scope);
        if (target.Kind != ValueKind.Cube)
            throw new ExpressionException(
                $"'[...]' indexing requires a cube (e.g. HB1.V[...]); got {target.Kind}.");
        var cube = target.AsCube();

        if (ix.Tokens.Length != cube.Rank)
            throw new ExpressionException(
                $"Cube index has {ix.Tokens.Length} token(s) but cube has {cube.Rank} axis/axes " +
                $"[{string.Join(", ", cube.Axes.Select(a => a.Name))}]. Brackets are positional " +
                "(cube-axis order): ':' keeps an axis, a name/index fixes it, 'a:b' is a range.");

        var args = new object[cube.Rank];
        for (int d = 0; d < cube.Rank; d++)
        {
            var tok  = ix.Tokens[d];
            var axis = cube.Axes[d];
            switch (tok.Kind)
            {
                case IndexTokenKind.Whole:
                    args[d] = Range.All;
                    break;
                case IndexTokenKind.Range:
                    int lo = (int)EvalExpr(tok.A!, scope).AsReal();
                    int hi = (int)EvalExpr(tok.B!, scope).AsReal();
                    args[d] = new Range(lo, hi);
                    break;
                default: // Pin
                    args[d] = ResolvePin(EvalExpr(tok.A!, scope), axis);
                    break;
            }
        }
        return SliceToValue(cube[args]);
    }

    private static object ResolvePin(Value v, RfCore.Data.Axis axis)
    {
        if (v.Kind == ValueKind.String)
        {
            string label = v.AsString();
            if (axis.Labels is null)
                throw new ExpressionException(
                    $"Axis '{axis.Name}' has no name labels — cannot resolve \"{label}\".");
            int idx = Array.FindIndex(axis.Labels, s => s.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                throw new ExpressionException(
                    $"'{label}' not found on axis '{axis.Name}'. Available: [{string.Join(", ", axis.Labels)}].");
            return idx;
        }
        if (v.Kind == ValueKind.Real)
            return (int)v.AsReal();
        throw new ExpressionException(
            $"Index for axis '{axis.Name}' must be ':' , a name, an integer, or a range — got {v.Kind}.");
    }
```
This reuses the existing `cube[args]` indexer (int = pin-and-drop, `Range.All` = keep, `Range` = range) and
`SliceToValue`. A fully-pinned index yields a bare element → `SliceToValue` returns a scalar `Value`, so
`DC1.I["Iout"]` is a scalar exactly like `DC1.I("Iout")`.

## 5. `AstWalker.cs` — collect refs through the index
Add a case in `Walk` so variable refs inside index tokens (e.g. `V[:, "n", k]`) are tracked:
```csharp
            case IndexExpr ix:
                Walk(ix.Target, refs);
                foreach (var tok in ix.Tokens)
                {
                    if (tok.A is { } a) Walk(a, refs);
                    if (tok.B is { } b) Walk(b, refs);
                }
                break;
```

## Semantics to document (and test)
- **Positional & fragile.** Brackets address axes by position in cube-axis order, so a pasted index
  breaks if the cube's axis order changes (e.g. adding a sweep). The accessor (`V("Vout", 1, All)`) is
  name-keyed and robust. Both are supported; the card regenerates the bracket for the current shape.
- **`:` keeps, pin drops.** `V[:, "Vout", 1]` on `[sweep, node, harmonic]` → keep sweep, drop node, drop
  harmonic → a 1-D cube over sweep — identical to `V("Vout", 1, All)`.
- **Multi-`:` keeps all those axes** (no family concept in measurements): `V[:, :, 1]` → 2-D `[sweep,
  node]`. (In the trace card the same string is a *family*; that divergence is expected and only affects
  multi-`:` specs.)
- **Fully pinned → scalar** (`DC1.I["Iout"]`, `DC1.V["Vout"]`).
- **`~` → parse error** with the family message above.

## Tests (`tests/Core.Tests` or where measurement-accessor tests live)
Build a small DataSet via `MeasurementContext` with an `HB1` group whose `V` is `[sweep, node, harmonic]`
(node labels incl. `Vout`) and `I` is `[sweep, branch, harmonic]` (branch labels incl. `Iout`), plus a
no-sweep `DC1` (`V[node]`, `I[branch]`).
1. **Bracket_Accessor_Parity:** `HB1.V[:, "Vout", 1]` evaluates equal (cube values) to
   `HB1.V("Vout", 1, All)`; likewise `HB1.I[:, "Iout", 1]` ≡ `HB1.I("Iout", 1, All)`.
2. **Bracket_Scalar:** `DC1.I["Iout"]` and `DC1.I[0]` evaluate to the scalar probe current (equal to
   `DC1.I("Iout")`).
3. **Bracket_Range:** `HB1.V[:, "Vout", 1:3]` keeps a 2-axis result `[sweep, harmonic(2)]`.
4. **Bracket_Index_Pin:** integer pin works (`HB1.V[0, 1, 1]`), dropping all axes → scalar.
5. **Bracket_FullExpression:** `0.5*HB1.V[:, "Vout", 1]*conj(HB1.I[:, "Iout", 1])` parses and evaluates
   (the original failing case); `re(...)` wraps to a real result.
6. **Bracket_TokenCountMismatch:** `HB1.V["Vout", 1]` against a rank-3 cube throws with the axis-list
   message.
7. **Bracket_UnknownLabel:** `HB1.V[:, "nope", 1]` throws with an "Available: […]" node list.
8. **Bracket_Tilde_Errors:** `HB1.V[~, :]` throws the "'~' (curve family) has no meaning…" parse error.
9. **NoRegression:** a representative existing accessor/arithmetic expression set still parses+evaluates
   unchanged.

## Gate (manual)
In a measurement (MEAS rows / `.cnl`), `Pout_W = 0.5*re(HB1.V[:, "Vout", 1]*conj(HB1.I[:, "Iout", 1]))`
evaluates and appears in the `measurements` group. Copy a trace-card expression verbatim into a
measurement and confirm it resolves.

## On completion
Update `docs/design/measurements.md`: the expression surface now also accepts the **positional bracket
index** (`HB1.V[:, "Vout", 1]`) as an alias for the name-keyed accessor (`HB1.V("Vout", 1, All)`) — `:`
keeps an axis, a name/index fixes (drops) it, `a:b` is a range, all-pinned is a scalar, and `~` is
rejected. Note the positional-fragility tradeoff and that multi-`:` keeps all those axes (no family in a
measurement). Cross-reference `docs/design/trace-card.md` (§5 shorthand) since the two now share a surface
syntax.

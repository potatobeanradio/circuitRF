# Renderers — local conventions for `src/Ui/Renderers/`

Read with root `CLAUDE.md` and `src/Ui/CLAUDE.md`.

## Symbol stroke join / cap — single switch point

Symbol rendering uses round stroke joins and caps everywhere **except** wires.

The constants live on `SchematicRenderer`:

```csharp
public const SKStrokeJoin SymbolStrokeJoinStyle = SKStrokeJoin.Round;
public const SKStrokeCap  SymbolStrokeCapStyle  = SKStrokeCap.Round;
```

Both `SchematicRenderer` (schematic view) and `SymbolEditorRenderer` (symbol editor) must read these
constants — they are the **single switch point** for join style across both rendering contexts. When
you add a new paint for symbol geometry, include these two properties. Do NOT apply them to
`wirePaint` — wires stay miter-joined.

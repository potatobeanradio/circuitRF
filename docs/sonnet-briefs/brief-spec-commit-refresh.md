# Sonnet Brief — trace-card refresh after a manual spec edit + Table false "needs complex" warning

Two small fixes.

- **Fix 1 (card refresh):** `TraceRowViewModel.CommitSpec` (`src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs`).
- **Fix 2 (false warning):** `TraceExpression.TryEvaluate` (`src/Ui/DataDisplay/TraceExpression.cs`).

Build 0W/0E.

---

## Fix 2 — Table plot wrongly rejects a real-valued expression

In `TraceExpression.TryEvaluate`, Step 7:
```csharp
// Smith/Polar require a complex result.
if (!plotType.IsRect() && realValues != null)
{
    error = "Smith/Polar needs a complex expression; result is real-valued.";
    return false;
}
```
`!plotType.IsRect()` is true for BOTH the complex plots (Smith/Polar) AND **Table** — so a real-valued
expression like `mag(V[:, "Vout", 1])` is rejected on a Table plot, even though Table happily shows real
values. The guard should fire only for the genuinely complex-only plots.

**Fix:** use `IsComplex()` (Smith || Polar) instead of `!IsRect()`:
```csharp
// Only the complex-locus plots (Smith / Polar) require a complex result; Rect and Table accept real.
if (plotType.IsComplex() && realValues != null)
{
    error = "Smith/Polar needs a complex expression; result is real-valued.";
    return false;
}
```
(`PlotTypeExtensions.IsComplex()` already exists = `t == Smith || t == Polar`.)

**Check:** on a Table plot, committing `mag(V[:, "Vout", 1])` no longer warns and shows the real values;
on a Smith/Polar plot a real-valued expression still warns as before.

---

## Fix 1 — Trace card doesn't update after the user edits & commits the spec

**Symptom:** editing the spec text (e.g. `V[:, "Vout2", 2]` → `V[:, "Vout2", 1]`, or →
`mag(V[:, "Vout", 1])`) and committing updates the plot but NOT the trace-card comboboxes (the harmonic
pin combo, the transform combo, the axis-role rows).

**Cause:** `CommitSpec` sets `_trace.Expression = text` and calls `RebuildAndNotify()`, but never
re-derives the picker state (`CubeName` / `Slice` / `Transform`) from the typed text. The axis-role rows
and transform combo are built from that picker state, so they stay stale. `RebuildAndNotify` →
`RefreshDescription` re-syncs the transform combo and raises display flags, but it deliberately does NOT
call `RebuildAxisRoles()`.

**Fix:** in `CommitSpec`, after setting the expression, try to parse the text as a single-cube picker
spec via `CubeTraceSpecParser.TryParse`. If it parses, back-populate `CubeName` / `Slice` / `Transform`
(so the axis-role editor + transform combo reflect the new spec) and rebuild the axis-role rows. If it
does NOT parse as a single spec (a genuine multi-cube expression like `mag(V[...]) + mag(W[...])`), clear
the single-cube picker identity so the card shows it as a free expression (no axis-role rows). Either
way, refresh the card.

Current method:
```csharp
public void CommitSpec(string text)
{
    if (!_trace.IsCubeBound) return;
    _trace.Expression      = text;
    _trace.InvalidSpecText = null;
    _trace.ExpressionError = null;
    _parent.RebuildAndNotify();
}
```
Replace with:
```csharp
public void CommitSpec(string text)
{
    if (!_trace.IsCubeBound) return;

    _trace.Expression      = text;
    _trace.InvalidSpecText = null;
    _trace.ExpressionError = null;

    // Re-derive the picker state from the typed text so the card's comboboxes (harmonic/node pin,
    // transform, axis-role rows) track the edit. A single-cube spec like `V[:, "Vout2", 1]` or
    // `mag(V[:, "Vout", 1])` parses to (CubeName, Slice, Transform); a multi-cube expression does not,
    // in which case we drop the single-cube identity and present it as a free expression.
    var ds = _parent.LibraryEntries
        .FirstOrDefault(e => string.Equals(e.FilePath, _trace.SourcePath, StringComparison.OrdinalIgnoreCase))
        ?.Data;
    if (ds is not null &&
        CubeTraceSpecParser.TryParse(text, ds, out var cubeName, out var slice, out var transform, out _))
    {
        _trace.CubeName  = cubeName;
        _trace.Slice     = slice;
        _trace.Transform = transform;
    }
    else
    {
        // Not a single-cube picker spec (e.g. multi-cube expression) — keep Expression as the source of
        // truth and drop the single-cube identity so the axis-role editor shows nothing stale.
        _trace.CubeName = null;
        _trace.Slice    = null;
    }

    _parent.RebuildAndNotify();   // re-evaluates via Expression; RefreshDescription re-syncs transform combo + flags

    // Rebuild the axis-role rows for the (possibly new) cube/slice — RefreshDescription does NOT do this.
    RebuildAxisRoles();
    OnPropertyChanged(nameof(IsCubeBoundTrace));
    OnPropertyChanged(nameof(ShowAllNodesToggleVisible));
}
```

Notes / things to verify on disk:
- `CubeTraceSpecParser.TryParse(text, ds, out cubeName, out slice, out transform, out error)` is the
  inverse of the shorthand and already accepts `:`, quoted labels (`"Vout2"`), and integer indices, and
  enforces exactly one X axis. It returns `false` for multi-cube expressions (no single `Cube[...]`
  prefix / multiple refs) — that's the branch that clears the identity. Confirm the signature matches
  (it does in the current file).
- `e.FilePath` / `e.Data` are the same `DataSourceEntryViewModel` members used by `RebuildAxisRolesCore`
  and `TrySetCubeData`. Reuse exactly those.
- After `RebuildAndNotify()`, `_trace.Slice` drives `RebuildAxisRolesCore`, which reads the cube axes and
  rebuilds the pin comboboxes; the transform combo is re-synced by `RefreshDescription` →
  `SyncTransformItem` (already called inside `RebuildAndNotify`). So calling `RebuildAxisRoles()` here is
  the missing piece.
- `Expression` stays set even when the spec parses to a single cube — that's fine: `TrySetCubeData` takes
  the Expression path first and returns, and the equivalent `CubeName`/`Slice` only feed the picker UI.
  Keeping both in sync means a subsequent picker edit (`FlushSliceAndRebuild`) rebuilds `Expression`
  from the slice correctly.

**Checks:**
- Edit `V[:, "Vout2", 2]` → `V[:, "Vout2", 1]`, commit: the harmonic pin combo in the card moves to index
  1 (the axis-role row reflects the new index).
- Edit to `mag(V[:, "Vout", 1])`, commit: the transform combo shows `mag`; the node/harmonic pin combos
  reflect `"Vout"` / `1`.
- Edit to a true multi-cube expression `mag(V[:, "Vout", 1]) + mag(V[:, "Vout2", 1])`, commit: plot
  updates; the card shows no stale single-cube axis rows (free-expression presentation); no crash.

## Gate
Build 0W/0E. Both manual check sets pass. Existing tests green; if there's a `CommitSpec`/`TryEvaluate`
test, extend it: (a) Table + real expression no longer errors; (b) committing a single-cube spec
re-populates `CubeName`/`Slice`/`Transform`.

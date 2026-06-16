# Sonnet Brief — Trace expressions: function-call transforms + full element-wise expressions

**Goal.** Evolve the cube-trace spec from `transform CubeName[slice]` (space-separated) to **function-call /
expression syntax**, and allow **full element-wise expressions** over one or more cube slices, evaluated with the
existing circuitRF expression engine. Examples:
- `mag(V[:, 0, 0])` (was `mag V[:, 0, 0]`)
- `conj(V[:, 0, 0])`
- `mag(V[:, 0, 0]) + mag(V[:, 0, 1])` — element-wise sum of two slices
- `dB20(V[:, 0, 1]) - dB20(V[:, 0, 0])` — gain in dB

Element-wise only (no matrix math). Gentle, brief error with explanation on bad syntax or mismatched dimensions
(same UX as the current invalid-spec hint: keep the user's text + ` <invalid>`, blank the column, subtle
selectable reason in the trace card).

## Why a new evaluator wrapper (not extending the shared parser)
The circuitRF expression engine (`src/Core/Expressions`) is **scalar** (`Value` = real/complex/string) and its
`Parser` has **no `[...]` indexing** (`:` is only the ternary colon). Extending the shared grammar to understand
`Cube[:, 0, 0]` would touch SDD/measurement/global evaluation — too invasive and risky.

**Approach (LOCKED): pre-resolve cube references to placeholder variables, then evaluate the existing scalar
expression once per X-sample.** This reuses `Parser` + `Evaluator` **untouched** and isolates all cube/vector
logic in the data-display layer. New code lives in a `TraceExpression` evaluator beside `CubeTraceSpecParser`
(which it supersedes for parsing — see Migration).

### Pipeline
Given a trace string `S` and the source `DataSet ds`:
1. **Extract cube references.** A cube ref is `CubeName[ token, token, … ]` where each token is `:` (the X axis),
   a quoted label `"Vout"`, or an integer. Scan `S` for these (a balanced-bracket scan keyed off a cube name in
   `ds.Cubes.Keys` followed by `[`; reuse the per-token validation already in `CubeTraceSpecParser`). For each
   distinct ref, resolve it to a **1-D `Complex[]`** via the existing slice resolver
   (`PlotInspectorViewModel.TrySetCubeData`'s `cube[args]` → rank-1 → `ComplexValues`/`RealValues`→Complex) and
   record its X-axis (name, unit, values) for the column-0 axis.
2. **Validate dimensions.** Every ref must resolve to the **same X length** (and ideally the same X axis). If two
   refs have different lengths → invalid: *"mag(V[:,0,1]) has 6 points but V[:,0,0] has 11 — slices must share the
   same swept axis."* If they have the same length but different X axis names, allow it but use the first ref's X
   axis for the column (note in the error only if lengths differ).
3. **Substitute placeholders.** Replace each cube ref in `S` with a generated variable name `__c0`, `__c1`, …
   (track ref-string → placeholder so identical refs share one). The transform/aggregate words (`mag`, `dB20`,
   …) are **function calls** in the resulting string — leave them as-is; they parse as `CallExpr`.
   Result: a pure scalar expression string, e.g. `mag(__c0) + mag(__c1)`.
4. **Parse once** with `Parser.Parse(substituted)` → `Expr`. Parse failure → gentle error
   (*"Couldn't parse 'mag(V[:,0,0]) +': unexpected end of expression."* — surface `ParseException.Message`).
5. **Register element-wise functions** on a fresh `Evaluator`: `mag`, `dB`, `dB10`, `dB20`, `phase`, `real`,
   `imag`, `conj`, plus pass-through of the engine's built-ins (`sqrt`, `abs`, `sin`, …) which already operate on
   the scalar `Value`. The transform functions:
   - `mag(z)=|z|`, `phase(z)=arg(z)°`, `real(z)=Re`, `imag(z)=Im`, `conj(z)=conj`,
   - `dB(z)=dB10(z)=10·log10(|z|)`, `dB20(z)=20·log10(|z|)`.
   (Match `CubeTransform` semantics exactly — same dB20/dB10 distinction.)
6. **Evaluate per X index.** For `i` in `0..n-1`: bind each placeholder `__ck` to `arrk[i]` (a complex `Value`)
   via `Evaluator.InjectResolved`/a `Scope`, evaluate the AST → scalar `Value`. Collect into the result array
   (Complex if any element is complex / a complex-producing op; else Real). Non-finite results → that point is
   dropped (same as `BuildCubePath`).
7. **Feed the Trace.** Produce the same `(xValues, complexValues?, realValues?, xAxisName, xUnit)` shape
   `Trace.SetCubeData` already consumes. The expression **replaces** the per-trace `Transform` + single-cube
   binding — see Model.

### Plot-type interaction
- **Rect / Table:** the expression yields a scalar-per-X (real after a transform like `mag`/`dB20`, or complex if
  the user writes a bare `V[:,0,0]` or `conj(...)`). Table shows it; Rect plots Y vs X.
- **Smith / Polar:** require a complex result (a bare cube ref or `conj`); a real-valued expression (e.g.
  `mag(...)`) → gentle error *"Smith/Polar needs a complex expression; mag(...) is real-valued."* (mirror the
  existing complex-cube gate).
- The existing `CubeTransform` combo becomes redundant for expression traces — see Model/UX.

## Model changes (`Trace`)
- Add `public string? Expression { get; set; }` — the user's trace expression string (the new source of truth for
  cube traces). When set, it supersedes `CubeName`/`Slice`/`Transform` for value production. Keep `CubeName`/
  `Slice`/`Transform` for back-compat / simple-picker authoring, but when `Expression` is non-null the owner
  resolves via the `TraceExpression` evaluator instead of `TrySetCubeData`'s single-slice path.
- `Trace.CubeShorthand` (the header/label) returns `Expression` when set, else the existing
  `transform(Cube[...])` form. **Change the shorthand serializer to function-call syntax**: emit
  `mag(V[:, 0, 0])` not `mag V[:, 0, 0]` (and `V[:, 0, 0]` for `CubeTransform.None`). This makes the default
  label round-trip through the new parser.
- `InvalidSpecText`/`SpecError` (already added for the inline editor) carry the error; on invalid expression,
  keep the user text + ` <invalid>`, blank cells/no points, show the reason.

## Owner-side resolution
In `PlotInspectorViewModel` (where `TrySetCubeData` runs), branch: if `trace.Expression` is set, call
`TraceExpression.TryEvaluate(trace.Expression, entry.Data, plotType, out xVals, out cz, out rz, out xName,
out xUnit, out error)`; on success `trace.SetCubeData(...)` and clear `InvalidSpecText`; on failure set
`InvalidSpecText = trace.Expression`, `SpecError = error`, clear points. Keep the single-slice path for traces
that still use `CubeName`/`Slice` without an `Expression` (picker-authored).

## Inline editor wiring
The inline trace editor (trace card field + Table header double-click — see the layout/bugfix brief) commits its
text to `trace.Expression`, then triggers the owner resolve above. The axis-role picker (`AxisRoles`) still
authors a single slice; when the user picks via the combos, set `trace.Expression = trace.CubeShorthand` (the
function-call form) so the two stay in sync and the text field shows the editable expression.

## Migration of `CubeTraceSpecParser`
`CubeTraceSpecParser.TryParse` (single `transform CubeName[slice]`) is now a **special case** of the expression
path. Either: (a) keep it for the simple picker path and add the new `TraceExpression` for the text path, or
(b) reimplement the simple path on top of `TraceExpression`. **Recommend (a)** — least churn; the picker emits a
single-ref expression and `TraceExpression` handles both. Update `CubeTraceSpecParser` callers only where the
header/label format changes to function-call syntax.

## Tests (`tests/Ui.Tests` / headless — pure evaluator)
1. **Expr_SingleTransform:** `mag(V[:, 0, 0])` on a known cube → array equals `|V[node0,h0,:]|` per X.
2. **Expr_Sum:** `mag(V[:, 0, 0]) + mag(V[:, 0, 1])` → element-wise sum; length = X length.
3. **Expr_dBGain:** `dB20(V[:, 0, 1]) - dB20(V[:, 0, 0])` → correct dB difference.
4. **Expr_BareRefComplex:** `V[:, 0, 0]` (no transform) → complex array; usable on Smith.
5. **Expr_DimMismatch:** two refs with different X lengths → invalid, error names both lengths.
6. **Expr_ParseError:** `mag(V[:,0,0]) +` → invalid, gentle message; `foo(V[:,0,0])` (unknown func) → invalid.
7. **Expr_FunctionCallSyntax:** `Trace.CubeShorthand` of a `mag` transform emits `mag(V[:, 0, 0])` and
   re-parses identically (round-trip).
8. **Expr_RealOnSmith:** `mag(...)` on a Smith plot → gentle "needs complex" error.

## Gate
Build 0W/0E; tests green. Manual: in the Table/trace card, type `mag(V[:, 0, 0]) + mag(V[:, 0, 1])` → a new
column computes the element-wise sum; `conj(V[:, 0, 0])` works on Smith; a dimension mismatch or typo shows a
gentle selectable reason and blanks the column; the default picker-authored label now reads `mag(V[:, 0, 0])`.

## On completion
Note in `src/Ui/CLAUDE.md`: cube traces accept full **element-wise expressions** over cube slices
(`TraceExpression`), reusing the circuitRF scalar expression engine evaluated per X-sample with cube refs bound
as placeholder variables; transforms are function calls (`mag(...)`, `dB20(...)`, `conj(...)`). Trace labels use
function-call syntax. Invalid syntax / mismatched slice dimensions surface as the gentle ` <invalid>` hint.
Matrix math is out of scope (element-wise only).

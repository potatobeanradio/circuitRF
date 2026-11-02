# Sonnet Brief — Named-axis broadcasting for cube measurements + measurement resilience

**Bug.** A measurement that mixes an accessor quantity with a swept-variable reference fails the moment
a *second* parametric sweep is added, and the failure wipes out **all** measurements.

Repro: HB at tone `RFfreq`, inner sweep `Pin`, outer sweep `RFfreq`. Measurements like
`IRL_dB = Pin_deliv_dBm - Pin_avail_dBm` where `Pin_deliv_dBm` comes from `HB1.V/I(...)` (carries both
sweep axes → rank-2 `[RFfreq, Pin]`) and `Pin_avail_dBm = Pin` (the swept-variable reference, injected
by `MeasurementEvaluator` as a rank-1 `[Pin]` cube). The subtraction throws
`Cube rank mismatch: 2 vs 1`.

**Root cause 1 — no broadcasting.** `DataCube.ElementWise` (RfCore `src/Data/DataCube.cs`) calls
`RequireSameShape`, which demands identical rank/lengths and then zips flat buffers positionally. There
is no alignment by axis name and no replication across missing axes. With one sweep both operands are
`[Pin]` (match); add an outer sweep and the accessor cube is `[RFfreq, Pin]` while the swept-var cube is
`[Pin]` → mismatch. (It is also a latent correctness hazard: two same-length cubes with axes in a
different order zip *silently misaligned* today.)

**Root cause 2 — all-or-nothing measurements.** `MeasurementEvaluator.Evaluate` `throw`s on the first
failing measurement; `SchematicRunService` wraps the whole batch in one try/catch, so the line copying
the *successful* measurement cubes into the grouped result never runs. Every measurement disappears —
which is why the Data Display shows "No cube references found" for previously-working plots (the named
cubes are simply absent).

**Owner decision (LOCKED).**
1. Implement **named-axis broadcasting** in `DataCube` cube–cube arithmetic (RfCore, shared with
   splotRF — approved). Align operands by axis *name*; replicate each across axes it lacks; the result
   axis-set is the union. Keep a **fast path** for structurally-identical operands so existing math is
   byte-identical and only previously-erroring (or silently-misaligned) cases change.
2. Make measurement evaluation **resilient**: a failing measurement is reported and skipped, the rest
   still evaluate and emit.

Once both land, the doubly-swept measurements come out as `[RFfreq, Pin]` and the Data Display plots
them as a family (inner axis `Pin` = X, outer axis `RFfreq` = curve family) — the accessor-notation
promise restored.

---

## Part A — RfCore: named-axis broadcasting in `DataCube` (`src/Data/DataCube.cs`)

Replace the body of the private `ElementWise(DataCube a, DataCube b, realOp, complexOp)` (used by the
`+ - * /` cube–cube operators) with a fast path + a broadcast path. All helpers live **inside**
`DataCube` (they need `_strides`, `_realData`, `_complexData`).

```csharp
private static DataCube ElementWise(DataCube a, DataCube b,
    Func<double,double,double> realOp, Func<Complex,Complex,Complex> complexOp)
{
    // Fast path: identical axes by name+order+length → existing tight zip (byte-identical result).
    if (SameShapeByName(a, b))
        return ZipIdentical(a, b, realOp, complexOp);   // = the CURRENT ElementWise body, unchanged

    // Broadcast path: align by axis name; union axis-set; replicate across missing axes.
    Axis[] axes   = UnionAxes(a, b);                    // throws on an incompatible shared axis
    int[]  rstr   = ComputeStrides(axes);
    int    rank   = axes.Length;
    int    total  = 1; foreach (var ax in axes) total *= ax.Length;
    int[]  posA   = MapPositions(a, axes);              // posA[e] = index of a.Axes[e] within `axes`
    int[]  posB   = MapPositions(b, axes);
    bool   cplx   = a.DataKind == DataKind.Complex || b.DataKind == DataKind.Complex;
    var    idx    = new int[rank];

    if (!cplx)
    {
        var buf = new double[total];
        for (int f = 0; f < total; f++)
        {
            Decode(f, rstr, idx);
            buf[f] = realOp(a._realData![OperandFlat(a._strides, posA, idx)],
                            b._realData![OperandFlat(b._strides, posB, idx)]);
        }
        return new DataCube(axes, buf, noCopy: true);
    }
    else
    {
        var buf = new Complex[total];
        for (int f = 0; f < total; f++)
        {
            Decode(f, rstr, idx);
            int ia = OperandFlat(a._strides, posA, idx);
            int ib = OperandFlat(b._strides, posB, idx);
            var ca = a.DataKind == DataKind.Complex ? a._complexData![ia] : new Complex(a._realData![ia], 0);
            var cb = b.DataKind == DataKind.Complex ? b._complexData![ib] : new Complex(b._realData![ib], 0);
            buf[f] = complexOp(ca, cb);
        }
        return new DataCube(axes, buf, noCopy: true);
    }
}

private static bool SameShapeByName(DataCube a, DataCube b)
{
    if (a.Rank != b.Rank) return false;
    for (int d = 0; d < a.Rank; d++)
        if (a.Axes[d].Name != b.Axes[d].Name || a.Axes[d].Length != b.Axes[d].Length) return false;
    return true;
}

// result = (operand with more axes).Axes, then any axis from the smaller operand not present by name.
// Shared axes must agree in length (and coordinates — same provenance); else a genuine misalignment.
private static Axis[] UnionAxes(DataCube a, DataCube b)
{
    var (big, small) = a.Rank >= b.Rank ? (a, b) : (b, a);
    var result = new List<Axis>(big.Axes);
    foreach (var sx in small.Axes)
    {
        int j = result.FindIndex(ax => ax.Name == sx.Name);
        if (j < 0) { result.Add(sx); continue; }
        if (result[j].Length != sx.Length)
            throw new ArgumentException(
                $"Cannot align axis '{sx.Name}': lengths {result[j].Length} vs {sx.Length}.");
        // coordinates should match (shared sweep provenance); verify and throw if they don't:
        for (int k = 0; k < sx.Length; k++)
            if (Math.Abs(result[j].Values[k] - sx.Values[k]) > 1e-12 * (1 + Math.Abs(sx.Values[k])))
                throw new ArgumentException($"Cannot align axis '{sx.Name}': differing coordinates.");
    }
    return result.ToArray();
}

private static int[] MapPositions(DataCube op, Axis[] resultAxes)
{
    var pos = new int[op.Rank];
    for (int e = 0; e < op.Rank; e++)
        pos[e] = Array.FindIndex(resultAxes, ax => ax.Name == op.Axes[e].Name);  // always ≥ 0 (union)
    return pos;
}

private static void Decode(int flat, int[] strides, int[] idx)
{
    int rem = flat;
    for (int d = 0; d < strides.Length; d++) { idx[d] = rem / strides[d]; rem %= strides[d]; }
}

// Sum over the operand's own axes; axes it lacks never contribute → natural broadcast (replication).
private static int OperandFlat(int[] opStrides, int[] posInResult, int[] resultIdx)
{
    int f = 0;
    for (int e = 0; e < opStrides.Length; e++) f += resultIdx[posInResult[e]] * opStrides[e];
    return f;
}
```

- `ZipIdentical` is the **current** `ElementWise` body verbatim (the `RequireSameShape` + flat zip) —
  extract it under that name so the fast path stays byte-identical.
- `RequireSameShape` is now unused (grep confirms `ElementWise` was its only caller) — delete it, or
  leave dead with a comment.
- **Scalar cubes fall out for free:** a `DataCube.Scalar` (rank 0) has no axes, so `OperandFlat`
  returns 0 for every element → the scalar broadcasts against any shape.
- **Result axis order** is the higher-rank operand's order (`[RFfreq, Pin]` for the repro), preserving
  the outer→inner sweep ordering the Data Display's family-curves relies on. The unary maps
  (`Real/Imag/Mag/Conj/Log10/DB…`) and the `cube × scalar` operators are unchanged.

## Part B — Measurement resilience (`src/Engine/MeasurementEvaluator.cs` + `SchematicRunService.cs`)

**`MeasurementEvaluator`** — change `Evaluate`/`EvaluateInto` to collect per-measurement errors instead
of throwing on the first one:

```csharp
public IReadOnlyList<string> EvaluateInto(DataSet ds)
    => Evaluate((m, result) => ds.Add(m.Name, ToCube(m, result)));

private IReadOnlyList<string> Evaluate(Action<Measurement, Value> emit)
{
    var errors = new List<string>();
    if (_tb.Measurements.Count == 0) return errors;
    // … (unchanged scope/eval/swept-var-cube setup) …
    foreach (var m in _tb.Measurements)
    {
        Value result;
        try { result = eval.Eval(m.Expression, mScope, m.Unit); }
        catch (Exception ex)
        {
            errors.Add($"Measurement '{m.Name}': failed to evaluate '{m.Expression}': {ex.Message}");
            continue;  // skip: do NOT bind or emit. Later measurements referencing this name will
                       // fail with an unresolved-name error and be reported too (informative cascade).
        }
        mScope.Bind(m.Name, result.ToString()!);
        eval.InjectResolved("measurements", m.Name, result);
        emit(m, result);
    }
    return errors;
}
```

Changing the return type `void → IReadOnlyList<string>` is source-compatible for any caller that ignores
it. (`ToCube` can still throw for an unsupported kind — wrap the `emit` call site or let that propagate
as today; prefer wrapping so one bad kind doesn't escape the resilient loop.)

**`SchematicRunService`** (the measurements block) — emit successful cubes even when some fail, and
surface each failure:

```csharp
if (tb.Measurements.Count > 0)
{
    try
    {
        var measDs    = new DataSet();
        var measErrors = new MeasurementEvaluator(tb, nl, analysisResults).EvaluateInto(measDs);
        foreach (var kv in measDs.Cubes)
            grouped.AddToGroup("measurements", kv.Key, kv.Value);   // now reached even if some failed
        foreach (var e in measErrors) errors.Add($"measurements: {e}");
    }
    catch (Exception ex) { errors.Add($"measurements: {ex.Message}"); }  // safety net for unexpected throws
}
```

---

## Tests

RfCore (`tests/RfCore.Tests/DataCubeTests.cs`):
1. **Broadcast_SubsetAxis_Replicates:** `A=[RFfreq(3),Pin(4)]` (values 0..11), `B=[Pin(4)]`;
   `A - B` → `[RFfreq, Pin]`, each row `r` equals `A[r,:] - B`. Symmetric: `B - A` → negation.
2. **Broadcast_ResultAxisOrder:** result of `[RFfreq,Pin] op [Pin]` has axes `["RFfreq","Pin"]`
   (higher-rank order), values/labels from the rank-2 operand.
3. **Broadcast_ScalarCube:** `DataCube.Scalar(2.0) * [Pin(4)]` → `[Pin]` scaled by 2.
4. **Broadcast_Complex:** real `[Pin]` × complex `[RFfreq,Pin]` → complex `[RFfreq,Pin]`, element-correct.
5. **Broadcast_IncompatibleSharedAxis_Throws:** `[Pin(4)]` op `[Pin(5)]` → `ArgumentException`
   ("Cannot align axis 'Pin'").
6. **FastPath_IdenticalShape_Unchanged:** two `[RFfreq,Pin]` cubes → identical to pre-change result
   (regression guard that the fast path is byte-identical).

circuitRF Engine (`tests/Engine.Tests`):
7. **Measurement_NestedSweep_BroadcastsSweptVar:** a netlist with HB + inner `Pin` + outer `RFfreq`
   sweeps and measurements `Pin_avail_dBm = Pin`, `Pin_deliv_dBm = …HB1.V/I…`,
   `IRL_dB = Pin_deliv_dBm - Pin_avail_dBm`. Run via the sweep engine + `MeasurementEvaluator`; assert
   `IRL_dB` is `[RFfreq, Pin]` with the correct per-(RFfreq,Pin) value (no throw). This is the
   end-to-end regression for the report.
8. **Measurement_Resilient_OneBadDoesNotNukeRest:** three measurements where the middle one references
   an undefined name; assert the other two cubes are emitted and exactly one error string is returned
   naming the bad measurement.

## Gate
Build 0W/0E (TreatWarningsAsErrors) in **both** RfCore and circuitRF; all RfCore + Engine tests green;
splotRF unaffected (fast path byte-identical; only previously-erroring cases change). **Manual:** the
reported schematic — HB at `RFfreq`, inner `Pin` sweep, outer `RFfreq` sweep — runs with **no**
measurement error; in the Data Display every measurement (`Gt_dB`, `Pout_dBm`, `Eff`, `IRL_dB`, …)
plots as a **family of curves over RFfreq** with `Pin` on X; the previously-broken single-sweep plots
recover (refresh/re-resolve if needed). Introduce a deliberate typo in one measurement → that one
reports an error, the rest still plot.

## On completion
Update `src/Core/Data/CLAUDE.md` (the `DataCube` contract): cube–cube `+ - * /` now **broadcast by axis
name** — operands are aligned by name, each replicated across axes it lacks, the result axis-set is the
union (higher-rank operand's order, shared axes must agree in length/coordinates). A fast path keeps
identical-shape math byte-identical. This is what lets a measurement that references a swept variable
(rank-1) combine with an accessor quantity carrying additional sweep axes (rank-N). Note in
`src/Engine/CLAUDE.md`: `MeasurementEvaluator` is now resilient — a failing measurement is reported and
skipped (returns the error list) rather than aborting the batch; `SchematicRunService` emits the
successful measurement cubes and reports per-measurement failures. Fixes nested-sweep measurements
throwing "Cube rank mismatch" and all measurements vanishing on a single failure.

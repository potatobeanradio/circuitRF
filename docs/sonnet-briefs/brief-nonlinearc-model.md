# Brief #2: NonlinearCModel + PolynomialFit (Core)

Design ref: `docs/design/nonlinear-in-linear-engines.md` §4 (read §4.1–4.2; the §4.1 Jacobian shapes were
corrected to 1-port/1×1 — use those). Depends on brief #1 (the `StampLinearized` seam) being landed.
This brief adds the model + the CV fit routine + factory wiring + unit tests. No symbol/palette/editor yet
(briefs #3/#4). Core-only; no Avalonia. Build **0W/0E**; add tests; report count; newest-first changelog.

Three files: `src/Core/Devices/NonlinearCModel.cs` (new), `src/Core/Expressions/PolynomialFit.cs` (new),
`src/Core/Devices/ComponentModelFactory.cs` (wire it in).

---

## 1. `NonlinearCModel` (new, `src/Core/Devices/NonlinearCModel.cs`)

2-terminal polynomial nonlinear capacitor. **`PortCount = 1`** (one differential port = 2 nets, matching
the `Nodes[2p],Nodes[2p+1]` convention), **`Kind = Nonlinear`**. Coefficients are captured at construction
(Evaluate has no access to `ElaboratedComponent`, so unlike `CapacitorModel` it can't read params at stamp
time — the factory resolves `C0…Cn` and passes the array in, exactly like SDD captures its ASTs).

Math (design §4.1): with `Vd = v[0]`,
  C(Vd) = Σ Cₖ·Vdᵏ        (Horner)
  Q(Vd) = Σ Cₖ·Vd^(k+1)/(k+1)   (Q(0)=0)
Return single-port Jacobians: `I=[0]`, `Q=[Q(Vd)]`, `Dg=[[0]]`, `Dc=[[C(Vd)]]`. The 4-corner stamp in the
DC engine / `StampLinearized` distributes +/− across the two nets.

```csharp
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// 1-D polynomial nonlinear capacitor: C(V) = Σ Cₖ·Vᵏ, with charge Q(V) = ∫₀ⱽ C(u)du =
/// Σ Cₖ·V^(k+1)/(k+1). Capacitance depends only on its own terminal voltage (V = V(n+) − V(n−)).
/// One differential port (PortCount = 1, two nets). Nonlinear: contributes nothing at DC (open),
/// drives the charge balance in HB, and linearizes to jω·C(V_bias) in the linear engines
/// (via ComponentModel.StampLinearized). See docs/design/nonlinear-in-linear-engines.md §4.
/// </summary>
public sealed class NonlinearCModel : ComponentModel
{
    private readonly double[] _c;   // [C0, C1, …, Cn]; lowest power first. Never empty.

    public NonlinearCModel(double[] coefficients)
        => _c = coefficients is { Length: > 0 } ? coefficients : [0.0];

    public override int       PortCount => 1;
    public override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>Small-signal capacitance C(V) = Σ Cₖ·Vᵏ (Horner).</summary>
    private double CapAt(double v)
    {
        double acc = 0.0;
        for (int k = _c.Length - 1; k >= 0; k--) acc = acc * v + _c[k];
        return acc;
    }

    /// <summary>Charge Q(V) = Σ Cₖ·V^(k+1)/(k+1), Q(0)=0 (Horner on the integrated coefficients).</summary>
    private double ChargeAt(double v)
    {
        // Q(V) = V · Σ_{k} (Cₖ/(k+1)) · Vᵏ  → Horner over bₖ = Cₖ/(k+1), then × V.
        double acc = 0.0;
        for (int k = _c.Length - 1; k >= 0; k--) acc = acc * v + _c[k] / (k + 1);
        return acc * v;
    }

    // Pure capacitor: no DC/conduction contribution. Linear engines call StampLinearized instead.
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        double vd = v[0];
        double cap = CapAt(vd);
        return new NonlinearResult(
            i:  [0.0],
            q:  [ChargeAt(vd)],
            dg: new double[1, 1] { { 0.0 } },
            dc: new double[1, 1] { { cap } });
    }
}
```

(Confirm the `NonlinearResult` ctor param order/names against `ComponentModel.cs`: `NonlinearResult(double[]
i, double[] q, double[,] dg, double[,] dc)`. The collection-expression `[0.0]` builds a `double[]`.)

---

## 2. `PolynomialFit` (new, `src/Core/Expressions/PolynomialFit.cs`)

Least-squares fit of C(V) data to a polynomial, returning coefficients **lowest-power-first**
(`[C0,…,Cn]`) — the order NonlinearC expects (note numpy `polyfit` is highest-first; callers using numpy
must reverse, as in the design's example). Self-contained normal-equations solve — **no NumFlat
dependency** — fine for the low orders (≤ ~6) this feature uses.

```csharp
namespace CircuitRF.Core.Expressions;

/// <summary>
/// Least-squares polynomial fit. Fit(v, c, order) returns coefficients [a0, a1, …, a_order]
/// (lowest power first) minimizing Σ (Σ aₖ·vᵢᵏ − cᵢ)². Solves the normal equations VᵀV a = Vᵀc
/// via Gaussian elimination with partial pivoting. Used by the schematic editor's CV→coefficients
/// "Apply" (docs/design/nonlinear-in-linear-engines.md §4.2). UI→Core; engine never fits.
/// </summary>
public static class PolynomialFit
{
    /// <param name="v">bias points (V)</param>
    /// <param name="c">measured capacitance at each v (F)</param>
    /// <param name="order">polynomial order n (≥0); needs at least order+1 distinct points</param>
    /// <returns>order+1 coefficients, lowest power first</returns>
    public static double[] Fit(double[] v, double[] c, int order)
    {
        if (v is null || c is null) throw new ArgumentNullException();
        if (v.Length != c.Length)   throw new ArgumentException("v and c must be the same length");
        if (order < 0)              throw new ArgumentOutOfRangeException(nameof(order));
        int m = v.Length, n = order + 1;
        if (m < n) throw new ArgumentException($"need ≥ {n} points to fit order {order}, got {m}");

        // Vandermonde V (m×n): V[i,k] = v[i]^k.
        var vand = new double[m, n];
        for (int i = 0; i < m; i++)
        {
            double p = 1.0;
            for (int k = 0; k < n; k++) { vand[i, k] = p; p *= v[i]; }
        }

        // Normal equations: A = VᵀV (n×n), b = Vᵀc (n).
        var a = new double[n, n];
        var b = new double[n];
        for (int r = 0; r < n; r++)
        {
            for (int s = 0; s < n; s++)
            {
                double sum = 0.0;
                for (int i = 0; i < m; i++) sum += vand[i, r] * vand[i, s];
                a[r, s] = sum;
            }
            double bsum = 0.0;
            for (int i = 0; i < m; i++) bsum += vand[i, r] * c[i];
            b[r] = bsum;
        }

        return SolveGauss(a, b, n);
    }

    private static double[] SolveGauss(double[,] a, double[] b, int n)
    {
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (Math.Abs(a[piv, col]) < 1e-300)
                throw new InvalidOperationException("PolynomialFit: singular normal matrix (degenerate/duplicate data?)");
            if (piv != col)
            {
                for (int k = 0; k < n; k++) (a[col, k], a[piv, k]) = (a[piv, k], a[col, k]);
                (b[col], b[piv]) = (b[piv], b[col]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r, col] / a[col, col];
                for (int k = col; k < n; k++) a[r, k] -= f * a[col, k];
                b[r] -= f * b[col];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int k = r + 1; k < n; k++) s -= a[r, k] * x[k];
            x[r] = s / a[r, r];
        }
        return x;
    }
}
```

---

## 3. Factory wiring (`ComponentModelFactory.cs`)

NonlinearC needs construction-time coefficients, so it's a **parameterized** type (like SDD). Add
`"NonlinearC"` to `_parameterizedTypes`, a dispatch line in `TryCreate(typeName, parameters)`, and the
creator that reads consecutive `C0,C1,…` Real params:

```csharp
// in _parameterizedTypes set:
{ "SnP", "Mutual", "SDD", "Z_Port", "V_1Tone", "V_nTone", "Tuner", "P1Tone", "NonlinearC" };

// in TryCreate(typeName, parameters), alongside the other parameterized dispatches:
if (typeName.Equals("NonlinearC", StringComparison.OrdinalIgnoreCase))
    return CreateNonlinearCModel(parameters);

private static NonlinearCModel CreateNonlinearCModel(IReadOnlyDictionary<string, Value> parameters)
{
    // Read C0, C1, … consecutively; stop at the first absent index. Absent ⇒ implicitly 0
    // (so trailing zeros may be omitted). No C0 at all ⇒ degenerate 0 F cap (allowed; warns elsewhere).
    var coeffs = new List<double>();
    for (int k = 0; ; k++)
    {
        if (!parameters.TryGetValue($"C{k}", out var val) || val.Kind != ValueKind.Real) break;
        coeffs.Add(val.AsReal());
    }
    return new NonlinearCModel(coeffs.Count > 0 ? coeffs.ToArray() : [0.0]);
}
```

(The Method-2 CV-data param — a String — is ignored here by design; only `C0…Cn` feed the model. CV
persistence/parse lands in brief #4.)

---

## Tests (Core test project)

1. **PolynomialFit recovers known coefficients.** Sample a known cubic `C(V)=c0+c1V+c2V²+c3V³` at ≥6 points,
   `Fit(v, c, 3)` returns `[c0,c1,c2,c3]` within 1e-9 (relative). Over-determined exact-fit case.
2. **PolynomialFit matches the design's varactor reference.** `v=[0,1,2,3,4,5]`,
   `c=[10e-12,8.5e-12,6.2e-12,4.1e-12,2.5e-12,1.8e-12]`, `Fit(v,c,3)` equals
   `numpy.polyfit(v,c,3)[::-1]` (hardcode the reference coeffs in the test) within tolerance.
3. **PolynomialFit guards.** Mismatched lengths → ArgumentException; `m < order+1` → ArgumentException;
   duplicate/degenerate points (singular) → InvalidOperationException.
4. **NonlinearC C(V)/Q(V).** For `coeffs=[c0,c1,c2,c3]` and several Vd: `Evaluate(...).Dc[0,0]` == analytic
   `C(Vd)`; `.Q[0]` == analytic `Σ cₖ·Vd^(k+1)/(k+1)`; `.I[0]==0`, `.Dg[0,0]==0`. Check Q(0)=0.
5. **Constant-C ⇒ linear-C charge.** `coeffs=[C0]` (C1..=0): `Dc[0,0]==C0` at all Vd, `Q[0]==C0·Vd`.
6. **Factory.** `TryCreate("NonlinearC", {C0:1e-12, C1:2e-13})` yields a `NonlinearCModel` whose `Evaluate`
   reflects those coeffs; absent C2.. ⇒ treated as 0; `IsPrimitive("NonlinearC")` true.

(The full-pipeline regression anchor — a constant-C NonlinearC giving the same S-parameters as a linear C —
needs the symbol/factory/netlist path; it lands with brief #3 once NonlinearC is placeable. Note that here.)

---

## Out of scope: symbol/palette/`SymbolKind` (brief #3), CV editor + `.csch` CV persistence (brief #4).

# Brief #2 (SDD weighting): result-model + engine generalization for arbitrary `H[w]`

Design ref: `docs/design/sdd.md` §3, §4, §6. Depends on brief #1 (the equivalence test — must stay green
byte-for-byte). This brief lifts the hardwired `w=0` (current) / `w=1` (jω·charge) pair into the general sum
`i_p = Σ_w H[w](ω)·FT{I[p,w]}`, so `w≥2` user weighting functions become expressible. **`w=0/1` stays the
fast path** — when no `H[w≥2]` is present, every engine takes the existing branch and behaves identically.

Core/Engine only, framework-free. Build **0W/0E** (nullable-on-property → locals). This is the engine half;
the SDD parser half (parsing `I[p,w≥2]` + `H[w]=expr`, dropping the factory's `w≥2` hard-error) is brief #3.
So **nothing here is reachable from a netlist yet** — it's exercised by direct unit tests with a stub
nonlinear model that supplies a `w≥2` bucket. Keep brief #1's two facts green throughout.

---

## 1. `ComponentModel.cs` — extend the result + add the weighting hook

**(a) `NonlinearResult` — carry optional higher buckets.** Keep `I/Q/Dg/Dc` as the `w=0`/`w=1` fast path.
Add an optional list of higher-weight buckets, each `(int W, double[] Value, double[,] Jac)`:
```csharp
public readonly struct WeightedTerm(int w, double[] value, double[,] jac)
{
    public int       W     { get; } = w;     // weighting index ≥ 2
    public double[]  Value { get; } = value; // per-port time-domain value of I[p,w]
    public double[,] Jac   { get; } = jac;   // ∂Value[p]/∂v[q]
}
```
Add a second `NonlinearResult` ctor that also takes `IReadOnlyList<WeightedTerm>? terms` and a
`public IReadOnlyList<WeightedTerm> Terms { get; } = terms ?? [];` property. The existing 4-arg ctor sets
`Terms = []`. **Existing devices (NonlinearC, the current SDD) are unchanged** — they return no higher terms.

**(b) Weighting hook on `ComponentModel`.** Add:
```csharp
/// <summary>H[w](ω): w=0→1, w=1→jω (built-in); w≥2 from the model's user-defined H[w] expression.</summary>
public virtual Complex Weight(int w, double omega) => w switch
{
    0 => Complex.One,
    1 => new Complex(0, omega),
    _ => throw new NotSupportedException($"{GetType().Name}: H[{w}] is not defined")
};
```
SDD overrides this for `w≥2` (brief #3). Every other device inherits the built-ins and never sees `w≥2`.

**(c) Generalize `StampLinearized`.** Replace the `y = Dg + jω·Dc` line with the bucket sum
`Y_pq(ω) = Σ_w Weight(w,ω)·D[w]_pq |_bias`:
```csharp
var r = Evaluate(bias);
// w=0 (Dg) and w=1 (Dc) are the fast path; Weight(0,ω)=1, Weight(1,ω)=jω.
... for each (p,q):
    Complex y = new Complex(r.Dg[p,q], 0) + Weight(1, omega) * r.Dc[p,q];
    foreach (var t in r.Terms) y += Weight(t.W, omega) * t.Jac[p, q];
    if (y == Complex.Zero) continue;
    // same 4-corner AddBlockAdmittance as today
```
(`Weight(1,ω)·Dc = jω·Dc` reproduces today exactly; `Weight(0,ω)` is folded as the real `Dg`.)

## 2. `HbNewton.cs` — apply `H[w](ω_k)` per row-harmonic

The seam is `EvaluateNonlinear` (FFT boundary) + `BuildF` (residual) + `BuildJ` (Jacobian). Today `iNl` enters
`BuildF` directly and `qNl` enters as `j·kω₀·qNl[n,k]`; `BuildJ` uses `G` directly and `C` rotated by
`[[0,−kω],[kω,0]]`. Generalize:

**`EvaluateNonlinear`:** in addition to accumulating `res.I`→`iTime`, `res.Q`→`qTime`, `res.Dg`→`dgTime`,
`res.Dc`→`dcTime`, accumulate each `res.Terms[*]` into a per-`w` time buffer `wTime[w][node,t]` and its
`Jac`→`dwTime[w][n,m,t]`. FFT each present `w` to `wNl[w][n,k]` (to K) and `Dw[w][n,m,k]` (to `Kj=2K`), same
as `q`/`C`. Return them alongside `(iNl,qNl,G,C)` (extend the tuple/record). Also capture, per nonlinear
component, **which `w`s it supplies and its `Weight(w,ω)`** — the engine needs the model handle to call
`Weight`. Simplest: collect a `set of active w` and, per `w`, a delegate or the owning `ec.Model` so
`BuildF`/`BuildJ` can call `Weight(w, k·ω₀)`. (One model per `w` bucket is the common case; if multiple
nonlinear devices define the same `w` with *different* `H[w]`, that's a per-device weight — keep the
contribution per-device rather than merged. For v1 a single SDD is the case; structure it per-device-bucket
but don't over-engineer.)

**`BuildF`:** after the `iNl` and `if(k>0) jkω₀·qNl` terms, add `Σ_w Weight(w, k·ω₀) · wNl[w][n,k]`.
`Weight(0,·)=1` and `Weight(1,·)=jkω₀` mean the existing two terms ARE this sum's `w=0,1` members — leave them
as the fast path and only loop the `w≥2` buckets.

**`BuildJ`:** the `G` block is `w=0` (`Weight=1`), the `C` block is `w=1` (`Weight=jkω₀`, the existing
rotation). For each higher `w`, add the same §7 conversion block built from `Dw[w]` (the `D[w]_{k−i}`,
`D[w]_{k+i}` difference/sum lookups via `SafeGet`), then **complex-multiply that block by
`Hₖ = Weight(w, k·ω₀)`** before stamping:
```
given conversion block d = [[d00,d01],[d10,d11]] from Dw[w], and Hₖ = (a,b):
  block += [[a·d00 − b·d10, a·d01 − b·d11], [b·d00 + a·d10, b·d01 + a·d11]]
```
Apply the **same `ConversionWeight(k, k∓i)` amplitude weights** the `G`/`C` terms use, and the **same guard
harmonic** zeroing, and the **same Maas DC special-cases** (they act on the assembled `a00..a11` after all
buckets are summed — so sum every bucket's contribution into `a00..a11` first, then apply guard + DC cases +
`Y_NN`, exactly as now). Sanity: with no `w≥2` buckets the assembled `J` is identical to today.

**Cache `Weight(w, k·ω₀)`** once per solve (it depends only on the harmonic grid, not the Newton iterate) —
a `[w][k]` table built before the loop, not called in the hot path.

## 3. `NonlinearDcEngine.cs` — don't assume reactive buckets vanish at DC

DC is `ω=0`: `Weight(0,0)=1` (current contributes), `Weight(1,0)=0` (charge drops — today's behavior). For
each higher bucket `w`, add `Weight(w,0)·Value[p]` to the DC current and `Weight(w,0)·Jac` to the DC
conductance stamp. Most physical `H[w]` vanish at DC (`Weight(w,0)=0` → no-op), but the engine must **ask**
rather than assume — so a `w` with nonzero DC weight biases correctly. (Today the engine reads only `I`/`Dg`;
add the `Terms` loop scaled by `Weight(w,0)`.)

## 4. Tests (`tests/Engine.Tests`)

Use a **stub nonlinear model** (1-port) that returns a `w=2` bucket with a known `Value`/`Jac` and a known
`Weight(2,ω)` (e.g. `H[2]=jω`, making it a second capacitor; or `H[2]=1`, a second conductance):
- **Regression:** brief #1's two facts stay green (no `w≥2` present → identical).
- **`StampLinearized`:** a stub with `H[2]=jω` and `Jac=C` stamps `jω·C` — equals a `w=1` charge stub with the
  same `Dc` (proves the bucket path == the fast path when `H[2]≡H[1]`).
- **HB residual/Jacobian:** a stub with `H[2]=jω` (bucket) must give the same HB spectrum as the same device
  expressed via `q` (`w=1`) — the general path reproduces the charge path. Reuse the
  `CompareJacobianNumerical` FD oracle already in `HbNewton` to check the analytic `J` of a `w≥2` bucket
  against finite differences (this is the strongest correctness check — the FD oracle doesn't know about
  buckets, it just perturbs `V`).
- **DC:** a stub with `H[2]=1` (nonzero at DC) contributes to the DC operating point; one with `H[2]=jω`
  (zero at DC) does not.

## Gate
Build 0W/0E; brief #1 green; the FD-Jacobian oracle agrees for a `w≥2` bucket. Next (brief #3): the SDD parser
— parse `I[p,w≥2]` + `H[w]=expr`, store per-`(p,w)` ASTs, override `Weight` to evaluate `H[w]` at `freq=ω/2π`,
drop the factory's `w≥2` hard-error — making all of this reachable from a netlist.

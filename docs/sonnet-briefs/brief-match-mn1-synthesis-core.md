# Sonnet Brief — Match MN-1: the synthesis core

**Design:** `docs/design/match.md`. This brief implements **§4 (the synthesis), §5 (inductive
terminations), §6 (response shapes), §7 (the data model and the rebuild)** as pure algorithm under
`src/Core/Match/`. **It has no UI, no component model, no stamping and no schematic.** MN-2 adds the
`ComponentModel`, the symbol and the palette entry; MN-3 the Designer window; MN-4 the probe; MN-5 the
flatten. §12 names them so nothing here is orphaned.

**This brief is self-contained.** Every algorithm you need is written out below, including the ones the
design doc states only in prose. You do not need to read any other implementation. If something here
contradicts `match.md`, `match.md` wins on *intent* and this brief wins on *detail* — and you say so in
your report.

**Where findings go: `src/Core/Match/RESOLVED.md`, which you create in this brief.**
**Do not write in any `CLAUDE.md`** — not the repo root one, not `src/Core/CLAUDE.md`. Every
measurement, every place this brief was wrong, every trap: all of it goes in `RESOLVED.md`, in the
style of `src/Core/RESOLVED.md` (read its first 40 lines for the shape).

---

## Gate command

```
dotnet test tests/Core.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

Run as separate commands — this SDK rejects more than one explicit project path per invocation
(`MSB1008`). **You need neither `Engine.Tests` nor `Ui.Tests`: this brief adds files under
`src/Core/Match/` and touches nothing else. If you find yourself editing `src/Engine`, `src/Ui` or any
existing file outside `src/Core/Match/`, stop and report — you have left this brief's scope.**

### Test-cost discipline

Everything in this brief is closed-form algebra on ≤ 8 elements and degree-≤ 12 polynomials. **No test
in this brief has any excuse to take a second**, let alone the ~5 s that would earn a
`[Trait("Category","Benchmark")]` tag. The one thing that could get slow is §8's solution enumeration
at n = 6 with every transform combination: **measure it and put the number in your report.** If it
exceeds 1 s, say so and say why — do not tag it and move on.

`tests/Core.Tests` references **only** `src/Core`. It cannot see `SParameterEngine`. That is deliberate
here: §11's response oracle is a **test-local ABCD cascade**, which is an independent implementation
and therefore a better check than asking our own engine whether our own synthesis is right.

---

## 0. Read this before planning anything

### 0.1 What is being built, in one paragraph

A bandpass LC impedance-matching network is synthesised *directly* — no optimiser — from a band, two
terminations and a network order. The defining property is **absorption**: each termination's single
reactive element becomes an element of the network itself, so a transistor's Cgs or Cds is part of the
filter rather than something tuned out. The synthesis produces a lowpass prototype (a vector of
g-values), maps it to a bandpass ladder, and then offers **Norton transforms** — element-value changes
that provably do not alter the two-port's transfer function — so the user can slide values into ranges
they can build. This brief produces all of that as a library: given a `MatchDesign`, return a ladder,
the available transforms, and the list of solutions.

### 0.2 The three facts that shape every decision here

1. **`g₁ = Q·w` is *set*, not derived.** The whole synthesis is "make a ladder whose first element is
   prescribed and whose response has shape X". Everything downstream follows from that one constraint.
2. **A Norton transform must not change the response.** If applying one moves S21 by more than 1e-9,
   the implementation is wrong, not the tolerance. This is the single most valuable test in the brief
   (§11.3) and you should write it early — it catches sign errors, propagation-direction errors and
   net-renaming errors all at once.
3. **The ladder is derived at load, never stored.** Only the *inputs* and the *user's choices*
   persist. This is why §10's rebuild exists and why transforms are keyed by element name (§7.3).

### 0.3 Terminology, fixed

| term | meaning |
|---|---|
| **arm** | one L+C resonant pair of the bandpass ladder — either a *series* arm (L and C in series, in the through path) or a *shunt* arm (L and C in parallel, to ground) |
| **element** | one L or one C. An arm has two elements |
| **absorbed element** | the element of an end arm that the external termination supplies. Not part of the component |
| **analysis end** | the end whose Q pins `g₁`. Default: the higher-Q end |
| **far end** | the other one |
| `w` | fractional bandwidth `(ω₂−ω₁)/ω₀`, `ω₀ = √(ω₁ω₂)` |
| `N` | a Norton transform's turns ratio. Impedances on the propagation side scale by `N²` |

---

## 1. The data model — `MatchDesign.cs`, `MatchEmbedding.cs`

```csharp
public enum ReactanceKind    { None, C, L }
public enum TerminationTopology { Series, Parallel }
public enum ResponseShape    { ChebyshevFano, ChebyshevTwoEnded, Butterworth, Bessel }
public enum AnalysisEndChoice{ Highest, Term1, Term2 }
public enum TransformForm    { Pi, T }

public sealed record Termination(
    double R, ReactanceKind Kind, TerminationTopology Topology, double Value,
    bool Probed = false, DateTime? ProbedAtUtc = null);

public sealed record TransformRecord(
    string ElementA, string ElementB, TransformForm Form, double N, bool Locked);

public sealed class MatchDesign      // JSON-serialized, base64'd onto the instance
{
    public int Version { get; init; } = 1;
    public double F1, F2;
    public int Order;
    public ResponseShape Response;
    public double RippleDb = 0.1;      // real-to-real prototype only, §2.2
    public Termination Term1, Term2;
    public AnalysisEndChoice AnalysisEnd;
    public double QAdjust;                  // 0 = none
    public bool AllowNegativeComponents;
    public bool LinkTransforms = true;
    public List<TransformRecord> Transforms = [];
    public List<string> AppliedSolutions = [];   // fingerprints, for MN-3's badges
    public string? BasisFingerprint;
    public double PlotBandFraction = 0.10;
    public int PlotPoints = 401;
}
```

`MatchEmbedding.TryEncode/TryDecode` mirror `WBondEmbedding` exactly — base64 of UTF-8 JSON,
`TryDecode` returning false rather than throwing. **Read `src/WBond/WBondEmbedding.cs` and follow it**;
this is a deliberate copy of an established pattern, not a place to invent.

**`NMin`/`NMax` are NOT in `TransformRecord` and must not be added.** They are derived during §10's
rebuild. See §7.2 for why a stored bound is worse than no bound.

### 1.1 The Q helper every other section calls

```
  C_eq(kind, value, ω₀) = kind == C ? value
                        : kind == L ? 1/(ω₀²·value)
                        : 0                              // None

  Q(term, ω₀) = topology == Parallel ? ω₀·R·C_eq
                                     : 1/(ω₀·R·C_eq)
```

`C_eq` is the whole of `match.md` §5. Everything downstream sees only `C_eq` and `Q`, and therefore
supports inductive terminations without knowing it does. **Do not special-case `Kind == L` anywhere
except in `C_eq` and in the two places §5.1 and §7.3 name.**

`Kind == None` means a purely resistive termination — `Q = 0`, nothing to absorb. Do **not** encode
this as `Value == 0`; the reference implementation did and could then not distinguish "no reactance"
from "a very small capacitance", which forced it to silently rewrite a series termination to parallel.

---

## 2. `MatchSynthesis` — the Chebyshev/Fano closed form

Given `n`, the analysis-end `Q`, and `w`:

```
  θ = π/(2n)
  c = (2/(Q·w))·sin θ

  n = 2 :  r = 0.5
  n ≥ 3 :  r = the first real root of P_n(c)   (coefficients DESCENDING):
      n=3:  16, 16, 3+12c², −3−4c²
      n=4:  16,  8, 2+8c²,  −1−2c²
      n=5:  256, 512, 16(21+20c²), 16(5+12c²), 5(1+12c²+16c⁴), −(5+20c²+16c⁴)
      n=6:  1024, 1536, 256(3+4c²), 128(1+3c²), 4(3+32c²+48c⁴), −2(3+16c²+16c⁴)
      no real root  ⇒  NO SOLUTION at this order. Return it as a value, not an exception.

  d      = √(c²/4 + r)
  sinh_a = d + c/2
  D      = sinh_a / (sin θ /(Q·w)) − 1

  g[0] = 1
  g[1] = Q·w
  for j = 2 … n:
      z    = j−1
      cz²  = cos²(zθ)
      k²   = [ sin²(zθ)·cz² + (cz² + D²·sin²(zθ))·sin²θ·(1/(Q·w))² ]
             / [ sin((2z−1)θ)·sin((2z+1)θ) ]
      g[j] = 1 / (g[j−1]·k²)
  g[n+1] = Q·w / (D·g[n])
```

`Q_far = g[n]·g[n+1]/w`, **inverted when the far arm is a series arm.**

Root-finding: build the companion matrix and take eigenvalues, or use any standard real-root routine;
"first real root" means the first with `|Im| < 1e-12` in the order your routine returns them, and you
must **sort ascending and document which you took** — the reference takes the first from its own
companion-matrix ordering, and if your ordering differs you will get a different (and possibly worse)
member of the family. Record what you chose in `RESOLVED.md` with the n=4 value that reproduces §11.1.

### 2.1 `ChebyshevTwoEnded` — the doubly-prescribed prototype

**This is not a ripple setting and it is not a lesser version of §2.** It is a different, closed-form
prototype that takes **both** end Q's as inputs, so `Q_far` comes out exactly equal to the real far
termination's and **§5's excess element is never needed.** What it gives up is Fano optimality.

```
  θ = π/(2n)
  x = (1/(Q_far·w) + 1/(Q_ana·w))·sin θ
  y = (1/(Q_far·w) − 1/(Q_ana·w))·sin θ

  g[0]     = 1
  g[1]     = 2·sin θ /(x − y)
  g[r+1]   = 4·sin((2r−1)θ)·sin((2r+1)θ)
             / ( g[r]·[ x² + y² + sin²(2rθ) − 2·x·y·cos(2rθ) ] )       r = 1 … n−1
  g[n+1]   = 2·sin θ / ((x + y)·g[n])
```

Two identities fall out of `x ∓ y` and are worth asserting as tests, because they are what the
prototype is *for*:

```
  g[1]              == Q_ana·w        (to 1e-12 relative)
  g[n]·g[n+1]/w     == Q_far          (to 1e-12 relative)
```

`Q_far` here is the **real** far termination's Q, supplied as an input — not something read back out.
Both Q's must be non-zero; if either end is resistive, this prototype does not apply and §2.2 does.

### 2.2 The real-to-real prototype

When **neither** end has a reactance to absorb (`Kind == None` at both ends) there is no Q to prescribe
and neither §2 nor §2.1 has an input. The component is then a plain bandpass transformer, and the
prototype is the standard equal-ripple filter table with a ripple specification (default 0.1 dB):

```
  B   = ln( coth(L_Ar/17.37) )        γ = sinh(B/(2n))        θ = π/(2n)
  a_k = sin((2k−1)θ)                                          k = 1 … n
  b_k = γ² + sin²(kπ/n)                                       k = 1 … n

  g[0] = 1
  g[1] = 2·a_1/γ
  g[k] = 4·a_{k−1}·a_k / (b_{k−1}·g[k−1])                     k = 2 … n
  g[n+1] = (n even) ? coth²(B/4) : 1
```

Gate it against a published Matthaei 0.1 dB-ripple table (n = 2 … 6) — the values are in every filter
text and this is the one prototype in the brief with an external reference to check against.

### 2.3 Which prototype runs when

```
  both ends resistive            → §2.2 (ripple), any order 2…6
  ChebyshevFano                  → §2
  ChebyshevTwoEnded              → §2.1
  Butterworth | Bessel           → §3
```

Selecting `ChebyshevTwoEnded` with a resistive end is not an error — fall through to §2.2 and say so in
the result, so MN-3 can label the plot honestly.

---

## 3. `MatchPrototypes` — the general route (Butterworth, Bessel)

This is `match.md` §6.2, and it also **re-derives the Chebyshev case as a permanent cross-check**
(§11.2). Work in the lowpass prototype domain, with polynomials in `s` and the band edge at `Ω = 1`.

```
  Chebyshev :  Num = K + ε²·|T_n|² ,   Den = 1 + ε²·|T_n|²        free: (K, ε)
  Butterworth: Num = K + ε²·|Ωⁿ|²  ,   Den = 1 + ε²·|Ωⁿ|²         free: (K, ε)
  Bessel     : Den = |θ_n(jΩ/α)|²  ,   Num = Den − C              free: (α, C), 0 < C ≤ 1
```

`θ_n` is the reverse Bessel polynomial, `θ_n(0) = 1`:
`a_k = (2n−k)! / (2^(n−k)·k!·(n−k)!)`, normalized by `a_0`.
`|F|²` means `F(s)·F(−s)` — an even, real polynomial in `s`. Substitute `Ω = s/j`, i.e.
`Ω^k = (−j·s)^k`, and take the real part.

**Extraction:**

```
  1. d  = the Hurwitz (LHP-root) factor of Den, made monic
     nn = the Hurwitz factor of Num, made monic, then scaled by √|lead(Num)/lead(Den)|
  2. Choose the sign of Γ = ±nn/d so that the FIRST extracted element is the required kind:
        Y = (d − sign·nn) / (d + sign·nn)
     shunt-element-first needs deg(numerator) = deg(denominator) + 1;
     series-element-first needs the other. Try both signs and take the one that fits.
  3. Cauer extraction, n times:
        g_k = lead(num)/lead(den)
        if g_k ≤ 0 → the member is NOT realizable. Return null. This is the decisive test.
        rem = num − g_k·s·den
        (num, den) = (den, rem)
     After n steps both must be degree 0; g_{n+1} = num/den.
```

### 3.1 The trap that will cost you an afternoon if you skip it

**Trim leading polynomial coefficients by a tolerance RELATIVE to the polynomial's scale, never by
exact zero.** Step 2's subtraction is designed to cancel the leading coefficient; in floating point it
leaves ~1e-16 instead of 0, a degree-3 polynomial then reports degree 4, the degree test in step 3
fails, and **every** extraction returns null with no diagnostic. Use:

```csharp
static double[] Trim(double[] a, double tol = 1e-9) {
    double m = a.Max(Math.Abs); int i = 0;
    while (i < a.Length - 1 && Math.Abs(a[i]) < tol * m) i++;
    return a[i..];
}
```

This is not hypothetical — it is exactly what happened while the design doc's §6 numbers were being
produced, and the symptom was a clean, silent, total failure.

### 3.2 Choosing the member of the family

Two free parameters, one constraint (`g₁ = Q·w`), so a **one-parameter family** survives.

```
  for each candidate value of the shape parameter (ε for Cheb/BW, α for Bessel):
      solve the other parameter (K, or C) so that g₁ == Q·w      — monotone 1-D root find
      reject if extraction returned null
      reject if Q_far < Q_actual(far end)                        — the FEASIBILITY constraint
      score by worst in-band |Γ| of the resulting BANDPASS ladder (§4, §11's ABCD oracle)
  return the best-scoring feasible member; if none is feasible, return "infeasible" WITH the
  maximum Q_far the family could reach, so the caller can say why.
```

**The feasibility constraint is not optional and it is not always slack.** At n = 6 on §11.1's problem
the best-return-loss Butterworth member has `Q_far = 0.483` against a required `0.638` and must be
rejected; a different member reaches `0.642` and is accepted at a worse return loss. A search that
optimises without the constraint will produce designs that cannot absorb the far termination.

For the root-find on `(K | C)`: **do not assume the direction.** `g₁` increases with `K` for
Chebyshev/Butterworth and *decreases* with `C` for Bessel. Bracket by scanning a grid first, find a
sign change, then bisect. A bisection that assumes a direction silently returns "no solution" for
Bessel — which is how the design doc's first Bessel answer came out wrong before it was rechecked.

---

## 4. `MatchLadder` — bandpass transformation and absorption

```
  arm orientation, for network order n:
      S[0] = S[1] = (analysis-end topology == Series)
      for j = 2 … n:  S[j] = !S[j−1]
      S[n+1] = S[n]
      S[j] == true → arm j is a SERIES arm; false → SHUNT

  g_freq[j]      = g[j]/(w·ω₀)                                   j = 1 … n
  g_imp[j]       = S[j] ? g_freq[j]·R_ana : g_freq[j]/R_ana
  g_imp[0]       = S[0]   ? 1/(g[0]/R_ana)   : g[0]·R_ana        (= R_ana, since g[0] = 1)
  g_imp[n+1]     = S[n+1] ? 1/(g[n+1]/R_ana) : g[n+1]·R_ana      (= R_far)

  series arm j:  L = g_imp[j],  C = 1/(ω₀²·L)
  shunt  arm j:  C = g_imp[j],  L = 1/(ω₀²·C)
```

The ladder is built **from the analysis end**; if the analysis end is Term2, reverse the element list
at the end so index 0 is always the Term1 side.

**Order parity.** A series-absorbing end needs a series arm there, a shunt-absorbing end needs a shunt
arm, and arms alternate — so with a reactance at both ends, a *mixed* pair (one series, one parallel)
forces **even** `n` and a *like* pair forces **odd** `n`. Expose `ValidOrders(term1, term2)` returning
`{2,4,6}`, `{3,5}` or `{2,3,4,5,6}` (either end resistive). MN-3 drives its order picker from this.

**Element naming is part of the contract**, because §7.3 keys transforms by name and MN-5 writes these
names into a real schematic:

```
  L1, C1, L2, C2, …   numbered left to right in the FINAL (Term1-first) order,
                      L and C numbered independently
  the two absorbed elements are additionally FLAGGED  IsAbsorbed = true
  the excess element of §5 is named  CFano / LFano
  the Q-adjust element of §6 is named CDetune / LDetune
  Norton products are named  <type><n>_N<k>_<1|2|3>
```

Absorbed elements keep ordinary names — the flag, not the name, is what MN-2 reads to decide what
**not** to stamp.

### 4.1 Verify absorption before going further

After building the ladder, assert that the analysis-end arm's absorbed element equals the
termination's own value **to 1e-12 relative**. It does, by construction — and if it does not, every
later section is built on sand. Make this the first test you write.

---

## 5. Excess reactance at the far end

`Q_far` (the synthesised far-end Q, §2) versus `Q_actual` (the real far termination's, §1.1):

- `Q_far / Q_actual ≤ 1.02` → nothing to do.
- `Q_far > Q_actual` → the design wants more absorbed reactance than the load provides. **Split the far
  arm's absorbed element into the load's own value plus a real added element.** With `R_far` the far
  port resistance and `X_tot` the far arm's absorbed element value:

```
  shunt far end:  X_new = (Q_far − Q_actual)/(R_far·ω₀)          added element is in PARALLEL
                  X_old = X_tot − X_new
  series far end: X_new = 1/((Q_far − Q_actual)·R_far·ω₀)        added element is in SERIES
                  X_old = X_tot·X_new/(X_new − X_tot)
```

  Insert the added element (named `CFano`/`LFano`) adjacent to the absorbed one, wired **in parallel**
  (shunt end: same two nets) or **in series** (series end: mint one net, e.g. `<net>_fano`, and chain
  them). The arm's total is unchanged, so the response is unchanged — **assert that** (1e-12).

- `Q_far < Q_actual` → **not absorbable.** Return a refusal carrying *which end*, `Q_far`, `Q_actual`
  and the ratio. MN-3 renders it. Never silently proceed.

The rule is written in **Q**, not in `C_eq`, and that is deliberate: in `C_eq` the inequality direction
differs between the series and shunt cases (series elements combine reciprocally), and Q's own
inversion for series topologies already absorbs that flip. Writing it in `C_eq` means four cases;
writing it in Q means one.

---

## 6. Q-adjusted solutions

Inflating the analysis-end Q above its true value adds an element at the analysis end and generally
**lowers the order needed**, at a modest cost in return loss.

```
  bracket on the analysis end's equivalent capacitance C_eq:
      series analysis end : hi = C_eq,                lo = hi/10
      parallel            : lo = (C_eq > 0 ? C_eq : 1e-15),  hi = lo·10
  15 bisection steps:
      Q(guess) = parallel ? ω₀·R_ana·guess : 1/(ω₀·R_ana·guess)
      synthesise at Q(guess); enumerate transforms (§8)
      if ANY transform set is valid:  record guess; move the bracket toward MORE detune
            (series: lo = guess ;  parallel: hi = guess)
      else:                           move the other way
  final Q = max(Q(recorded guess), Qmin);  if ≤ 0 use 0.01
```

Then re-synthesise at that Q and add the extra analysis-end element `CDetune`/`LDetune`, by the same
parallel/series split arithmetic as §5 but at the near end. Offer the result as an extra solution
tagged `QAdjust = <value>`.

`Qmin` is a caller-supplied setting (default 2.0).

---

## 7. `NortonTransform` — the element-value degrees of freedom

### 7.1 Thresholds and direction

For a candidate pair at ladder positions `(i, j)`:

```
  Component1Shunt = element[i] is a shunt element
  NGreaterThan1   = (Component1Shunt && !analysisIsTerm1) || (!Component1Shunt && analysisIsTerm1)

  Z1 = the SERIES element's value,  Z2 = the SHUNT element's value
  if the series element is a capacitor:  Z1 ← 1/Z1 ;  Z2 ← 1/Z2      (work in impedance)

  NThreshold  = NGreaterThan1 ? (1 + Z1/Z2) : Z2/(Z1 + Z2)
  PropagateRight = (NGreaterThan1 && !Component1Shunt) || (!NGreaterThan1 && Component1Shunt)

  range, positivity enforced (default):
      NGreaterThan1 : [1, NThreshold]      else [NThreshold, 1]
  range, AllowNegativeComponents:
      NGreaterThan1 : [1, 10]              else [1e-3, 1]
```

**Keep `N` strictly inside the threshold** (e.g. clamp to `NThreshold·(1 − 1e-9)`) rather than letting
it land exactly on it, where one of the three product values goes to infinity. If `NMin ≥ NMax` after
clamping, the pair is not usable — drop it rather than repairing the bound.

### 7.2 Why the range is recomputed, never stored

A pair's threshold depends on the element values *at the moment that transform is applied*, which
depends on every earlier transform. A stored bound goes stale against the elements it bounds, and a
stale bound silently permits a negative element — worse than no bound. Recompute during §10's
sequential rebuild, where the state it depends on exists.

### 7.3 Which pairs are transformable

Scan `j = 1 … count−4` over the ladder's **element** list (index 0 and `count−1` are the port
resistances). Let `shunt(k)` be "element k is a shunt element" and `type(k)` its L/C type.

```
  opposite(a,b) := shunt(a) != shunt(b)

  candidate (j, j+2)   if type(j)==type(j+2) && opposite(j,j+2)   && movable(j,j+2)
        move offset = opposite(j,j+1) ? (0,−1) : (1,0)
  candidate (j, j+3)   if type(j)==type(j+3) && opposite(j,j+3)   && movable(j,j+3)
                          && type(j+1)==type(j+2)          ← only if what is between them matches
        move offset = (1,−1)
  candidate (j+1, j+2) if type(j+1)==type(j+2) && opposite(j+1,j+2) && movable(j+1,j+2)
        move offset = (0,0)
  candidate (j+1, j+3) if type(j+1)==type(j+3) && opposite(j+1,j+3) && movable(j+1,j+3)
        move offset = opposite(j+1,j+2) ? (0,−1) : (1,0)
  (skip any pair already recorded)
```

**`movable(a,b)`** is where this brief *generalises the reference implementation, and you must not copy
the reference's version.* The rule protects the **absorbed** end elements, which must not be moved or
transformed — moving one breaks the absorption. The reference expresses it as *"allowed if the pair's
type is L, otherwise neither index may be an absorbed element"*, which is correct **only because in
that implementation the absorbed element is always a capacitor.** With inductive terminations
(`match.md` §5) that is false. The general rule is:

```
  movable(a,b) := type(a) != typeOfAnyAbsorbedElement   ||   (a and b are both non-absorbed)
```

i.e. **a transform on elements of a different type from the absorbed one is always allowed; otherwise
neither element may be an absorbed one.** State in `RESOLVED.md` that you implemented the general form
and add a test with an inductive termination that would pass under the reference's rule and fail under
the correct one.

**Conflicts.** Two transforms conflict when they would need the same element, before or after their
moves. For each recorded pair `i` and every other `k`:

```
  conflict if  t_i.first == t_k.second  ||  t_i.last == t_k.first
  else, with  pos_x = (t_x.first + move_x.first, t_x.last + move_x.last):
  conflict if  pos_i.last == pos_k.first  ||  pos_i.last == pos_k.last
```

### 7.4 Applying one transform

```
  A. make the pair adjacent, by moving at most two elements:
     gap = j − i
     gap == 1 : nothing to do
     gap == 2 : move exactly one element. If element i and element i+1 have the SAME orientation
                (both shunt or both series) → move element i right to i+1; otherwise move element j
                left to j−1. When an element moves, SWAP its nets with the neighbour it displaced.
     gap == 3 : move element i right by one AND element j left by one, each swapping nets with the
                neighbour it displaced.
     Track the index permutation — later transforms are stored against names (§1), but the ladder
     array is rebuilt positionally within one application, and the caller needs the mapping.

  B. remove the two elements. Identify:
     SeriesElement = the one NOT connected to ground;  ShuntElement = the one that is.
     Z1 = SeriesElement value, Z2 = ShuntElement value; invert BOTH if the pair's type is C.

  C. compute the three new values (all of the pair's own type):
       Pi , N > 1 :  v1 = N·Z1/(N−1)          v2 = N·Z1     v3 = N²Z1Z2/(Z1+(1−N)Z2)
       Pi , N < 1 :  v1 = N²Z1/(1−N)          v2 = N·Z1     v3 = N·Z1Z2/(N·Z1+(N−1)Z2)
       T  , N > 1 :  v1 = Z1+(1−N)Z2          v2 = N·Z2     v3 = N(N−1)Z2
       T  , N < 1 :  v1 = N²(Z1+Z2) − N·Z2    v2 = N·Z2     v3 = (1−N)Z2
     invert v1..v3 back if the type is C
     if the FIRST of the original pair was the shunt one, SWAP v1 and v3

  D. nets:
       Pi : n1 = [a, gnd]      n2 = [a, b]        n3 = [b, gnd]
       T  : n1 = [a, t]        n2 = [t, gnd]      n3 = [t, b]     with t a newly minted net
     where a,b are the SeriesElement's two nets. (π is shunt-series-shunt; T is series-shunt-series.)

  E. insert the three at the pair's position, then scale EVERYTHING on the propagation side:
       C ← C/N²   ;   L ← N²·L   ;   port Z ← N²·Z
     range: PropagateRight ? [pos+3, end] : [0, pos)
```

**The absolute value guards.** The reference clamps any produced value with `|v| > 1` to `1.0` and
`|v| < 1e-24` to `0.0` — absolute guards in SI units, i.e. 1 H / 1 F, against the infinities at the
threshold. **Keep them as a last-resort assert, not as normal behaviour**: with §7.1's strict-inside
clamp they must never fire. Add a test asserting they do not fire anywhere in §11's sweep, and report
it if they do — a firing guard means the threshold logic is wrong.

---

## 8. `MatchSolutionSearch`

```
  enumerate candidate transform SETS:
      for each pair i:
          emit {i}
          running = {i}
          for each pair k > i:
              if k conflicts with any member of running → skip
              running ∪= {k};  emit running;  emit {i,k}
      de-duplicate as a set

  for each set:
      rebuild the ladder from the basis; apply each transform with N = 1 (clamped into its range),
      recomputing the range at each step
      achieved = Π Nᵢ²
      desired  = R_far_target / R_far_synthesised
      for index = 0 … count−1, while achieved != desired:
          N_index ← clamp(N_index·√(desired/achieved), range_index);  recompute achieved
      VALID if achieved ≈ desired (RELATIVE tolerance 1e-9) and the response's feasibility test passes

  sort: fewest transforms first, then by first pair position, then by second, then by QAdjust
```

Use a **relative** tolerance. The reference uses an absolute `1e3·ε ≈ 2.2e-13`, which is meaningless
against a required transform ratio of ~119; say so in `RESOLVED.md`.

Each solution carries a stable **fingerprint** (order, response, QAdjust, the ordered pair names) so
MN-3 can mark "current" / "previously applied".

### 8.1 The linked-N redistribution — `MatchLinkage`

Pure math, so it lives here and MN-3 only binds to it. Given a new value `v` for transform `cur`:

```
  v ← clamp(v, cur.range)
  if !LinkTransforms → done
  if exactly one transform → v = √(required);  done  (the slider is fully determined, MN-3 disables it)
  otherwise, for each other UNLOCKED transform t:
      target = √(required) / v,  divided by every other transform's N except cur and t
      t.N ← clamp(t.N + (target − t.N), t.range)
      if the running product now matches required (relative 1e-9) → break
  finally: v = clamp(√(required / (Π N² excluding cur)), cur.range)
```

Locked transforms are never written. If the product cannot reach `required` inside the ranges, return
the shortfall so MN-3 can turn the far termination red.

---

## 9. Refusals are values, not exceptions

Every "cannot" in this brief is a **returned result**, never a thrown exception and never a silent
fallback:

| refusal | must carry |
|---|---|
| no real root at this order | n, Q, the response |
| far end not absorbable | which end, `Q_far`, `Q_actual`, the ratio |
| response family infeasible | the maximum `Q_far` the family could reach vs the one needed |
| transforms cannot reach the target | achieved vs required, and which transforms were at their limit |
| no transformable pairs | the ladder as synthesised |

MN-3 renders these verbatim. A refusal that does not carry its numbers is not finished.

---

## 10. The rebuild and the fingerprint (`match.md` §7.3)

```
  1. synthesise the basis ladder from the design's inputs
  2. compare BasisFingerprint; on mismatch, set a flag on the result (do not throw, do not discard)
  3. for each stored TransformRecord, in order:
        a. resolve ElementA/ElementB BY NAME against the current ladder
           — not found → drop the transform, record it in the result's notes
        b. recompute the range (§7.1)
        c. clamp the stored N into it, recording whether clamping occurred
        d. apply (§7.4)
  4. re-link (§8.1) if any transform was dropped
```

`BasisFingerprint` = a short stable hash of `(element count, per-arm type+orientation sequence, the
g-values to 6 significant figures)`. Compute it whenever the design is edited.

**Name-keying is the point.** Positional indices round-trip correctly only while the basis ladder comes
out byte-identical forever; since it is derived, any future change to the synthesis would re-point every
transform at different elements and produce a different network **with no error anywhere**. Names cost
nothing and make the failure detectable.

---

## 11. Tests

All in `tests/Core.Tests/Match/`. The response oracle is a **test-local ABCD cascade** (series arm →
`[[1,Z],[0,1]]`, shunt arm → `[[1,0],[Y,1]]`, then `S11`/`S21` from the two port resistances) — an
independent implementation, deliberately not our engine.

### 11.1 Golden values — the design doc's worked example

200 Ω ‖ 0.125 pF ↔ 1.25 Ω + 10 pF, 3.3–5.0 GHz:

```
  ω₀/2π = 4.06202 GHz     w = 0.418511
  Q(200Ω‖0.125pF) = 0.63806     Q(1.25Ω+10pF) = 3.13450   → analysis end is the series end
  n = 4, ChebyshevFano:
     g = [1, 1.311823, 1.106975, 1.717201, 0.508891, 1.344236]     Q_far = 1.63453
  ladder, from the analysis end:
     series   L = 153.5169 pH   C =  10.00000 pF     ← the load's own 10 pF, EXACTLY
     shunt    L =  18.5164 pH   C =  82.90847 pF
     series   L = 200.9567 pH   C =   7.63931 pF
     shunt    L =  40.2782 pH   C =  38.11411 pF
     R_far = 1.68030 Ω    →    required Π N² = 119.027
  over 3.3–5.0 GHz:  worst |S11| = −16.663 dB,  IL = 0.095 dB,  IL ripple = 0.0361 dB
```

Tolerances: g to 1e-5, elements to 1e-5 relative, the absorbed 10 pF to **1e-12** relative, dB figures
to ±0.02.

Also gate `Q_far` at n = 2 / 4 / 6 = 1.900 / 1.635 / 1.468.

### 11.2 The invariants — one test each

| test | what it protects |
|---|---|
| **Transform invariance** — applying any available transform at any `N` in range leaves S11 and S21 unchanged to **1e-9** | the entire premise. Sweep every pair × {π,T} × several N |
| **Absorption identity** — the analysis-end absorbed element equals the termination's own value to 1e-12 | §4.1 |
| **Duality (§5)** — replacing the 1.25 Ω + 10 pF series load with **1.25 Ω + 153.5169 pH** gives an identical element list and identical S-parameters | `match.md` §5's exactness claim. `Q` must come out 3.13450 |
| **Positivity** — over a sweep of terminations × orders × responses, no returned ladder has a non-positive element unless `AllowNegativeComponents` | §7.1's ranges are the only thing enforcing it |
| **The absolute guards never fire** in that sweep | §7.4 |
| **`movable` generalisation** — a design with an inductive absorbed element rejects a transform the reference's L-only rule would have allowed | §7.3, the one place §5 is not free |
| **Numerical ≡ closed form** — §3's extractor reproduces §2's Chebyshev g-values to 0.5 % at n = 4 | licenses Butterworth and Bessel |
| **Two-ended identities** — §2.1's `g[1] == Q_ana·w` and `g[n]·g[n+1]/w == Q_far` to 1e-12, and the resulting ladder needs **no** excess element | §2.1 is pointless if either identity is off |
| **Real-to-real ripple table** — §2.2 at n = 2…6, 0.1 dB, against published Matthaei values | the only prototype with an external reference |
| **Butterworth feasibility** — n = 4 yields a feasible member at ≈ 13.2 dB worst RL; n = 6's best-RL member is **rejected** and the constrained best is ≈ 8.3 dB | §3.2's constraint actually binds |
| **Bessel gating** — n = 2 feasible at ≈ 7.7 dB (`Q_far ≈ 0.654`); n = 4 and n = 6 **infeasible**, refusal carrying max `Q_far` ≈ 0.33 / 0.18 | `match.md` §6.4 |
| **Order parity** — `ValidOrders` returns {2,4,6} mixed, {3,5} like, {2..6} with a resistive end | §4 |
| **Session round-trip** — a design with two transforms (one π one T, one locked, link on, a Q-adjusted solution applied) encode → decode → rebuild gives identical element values, N's, lock/link state and S-parameters | §10, the "everything I set is still there" guarantee |
| **Basis change is detected** — perturb the synthesis output, confirm names still resolve, the fingerprint mismatch is flagged, and nothing silently re-points | §10's trap, the one that fails silently |
| **Refusals carry numbers** — each row of §9 | §9 |

### 11.3 Write the transform-invariance test first

Before the solution search, before Butterworth, before serialization. It is ~30 lines given the ABCD
oracle and it catches sign errors, propagation-direction errors, net-renaming errors and swap errors in
one shot. Everything else in §7 is easier to debug once it is green.

---

## 12. What is NOT in this brief

| deferred to | what |
|---|---|
| **MN-2** | `MatchModel`, the factory entry, the elaborator's internal-node mint, `SymbolKind.Match`, the bandpass glyph, `ComponentCategory.Matching`, the echo parameters |
| **MN-3** | the Match Designer window, sliders, plots, the solutions list, the ladder preview |
| **MN-4** | `TerminationProbe` — looking back into the external network (needs `SParameterEngine`, so it lands in `src/Engine`) |
| **MN-5** | Flatten to Cell |

Do not stub any of them. Do not add an Avalonia reference, a `ComponentModel` subclass or a
`SymbolKind` value in this brief.

---

## 13. Report

State: the g-values you got at n = 2/4/6 against §11.1; the root-ordering choice you made in §2; the
n = 6 solution-enumeration timing; whether `ChebyshevRipple` shipped and why; anything in §§2–8 that
turned out to be wrong or underspecified, with the correction. Findings to
**`src/Core/Match/RESOLVED.md`**, which you create.

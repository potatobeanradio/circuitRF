# Sonnet Brief — MN-MB2: tri-band Match, the multi-interval Remez family, and odd element counts

**Design:** `docs/design/match.md` **§18.5** (the whole of this brief), §18.3 (the tri-band mirror
rule), §18.2 (Parity — the like-pair gap this closes), §16.3 (the odd-count follow-up this closes),
§18.9 (numerical notes), §13.2 rows marked rev 3, and `src/Core/Match/RESOLVED.md` §MN-LP ("The
polynomial route cannot solve this family past order 4" — the reason the roots below are found in u,
never in s). **Prerequisite: MN-MB1 is landed** (`BandCount`, `MatchBands`, `MatchMultibandSynthesis`,
the Bands selector, the reserved `F5`/`F6`).

**One sentence:** a general **equiripple polynomial on a union of intervals**, produced by a Remez
exchange, replaces the shifted Chebyshev polynomial as the prototype's Φ — which gives tri-band
matching (a union of two intervals in `u = Ω²`), and, in its weighted form, the **odd element counts**
that both the lowpass form (§16.3) and the dual-band form (§18.2) lack.

**Structural facts.**
1. **Remez is not an optimiser in §2.2's sense.** It computes the unique best polynomial on a compact
   set by exchange, deterministically, to machine precision; it takes no "spec" and there is no loop
   around it. Keep it that way: no tolerance the user can set, no iteration count in the UI.
2. **Roots are found in u, never in s.** MN-LP showed the degree-4n-in-s polynomial does not factor in
   double precision at order 6. `c + ε²p(u)² = 0` is `p(u) = ±j√c/ε`: two degree-n complex polynomials
   in u, then `s = ±j√u` exactly. Never form `Φ(−s²)`.
3. **Everything after the g-vector is MB1's.** `Build`, the end rules, Norton, the Designer's search,
   persistence — unchanged. This brief adds one numerical class and one band count.

**Sequencing.** After MB1. Touches `src/Core/Match` (new `MatchRemez.cs`, an entry in
`MatchFormPrototype` for a general polynomial, `MatchBands` for three bands, orders), the Designer's
Bands selector (adds *Tri*) and f5/f6 row, the user reference. Nothing else.

---

## 1. What already exists

| Piece | Where |
|---|---|
| `GvaluesAt` — Chebyshev/Butterworth roots by arccosine, `LeftHalfPlane`, `Extract` | `src/Core/Match/MatchFormSynthesis.cs` 112–309 |
| `MatchPoly` — `Roots` (real coefficients, Cauchy-scaled, polished), `FromRoots` (complex accumulate), `Mul/Add/Sub/Eval` | `src/Core/Match/MatchPrototypes.cs` 9–200 |
| `MatchMultibandSynthesis` — the K/ε² search and dispatch (MB1) | `src/Core/Match/MatchMultibandSynthesis.cs` |
| `MatchBands.Symmetrise` (two bands) and `MatchDesign.Effective/Omega0/W/A` (MB1) | `src/Core/Match/MatchBands.cs`, `MatchDesign.cs` |
| `MatchOrders.ValidOrders(term1, term2, form, bandCount)` (MB1) | `MatchLadder.cs` |
| The lowpass-form K rule and its scan (the odd-count path plugs in here) | `MatchFormSynthesis.Synthesize` 344–556 |
| The ABCD oracle | `tests/Core.Tests/Match/MatchAbcdOracle.cs` |

---

## 2. `MatchRemez` (`src/Core/Match/MatchRemez.cs`, new)

```csharp
/// <summary>The monic-scaled minimax polynomial of degree n on a union of closed intervals in u —
/// max|p| = 1 on the union, equioscillating at n + 1 points (match.md §18.5).</summary>
public static double[]? Minimax(int n, IReadOnlyList<(double Lo, double Hi)> intervals);

/// <summary>The weighted form: minimises max |√(u + uR) · R_k(u)| on the union, for the odd element
/// counts (match.md §16.3, §18.5). Returns R_k (degree k), scaled so the weighted maximum is 1.</summary>
public static double[]? MinimaxWeighted(int k, double uR, IReadOnlyList<(double Lo, double Hi)> intervals);
```

Implementation: classical Remez exchange for the linear problem "approximate `u^n` by degree < n on
the set E" (weighted: `w(u)·(u^k − q(u))`), with:
- a dense reference grid per interval (2,001 points, refined ×4 near the final reference), the initial
  reference the n + 1 Chebyshev-like nodes distributed across intervals in proportion to their length;
- the exchange taking **all** local extrema of the error on the grid, merging same-sign runs to their
  largest, then trimming to n + 1 alternating points by dropping the smallest-magnitude *end* (this is
  the multi-point exchange; the single-point exchange converges too slowly on a union);
- convergence when the levelled error `h` and the grid maximum agree to 1e-12 relative, cap 200
  iterations, return null on failure (a refusal upstream, not an exception);
- **numerically in a scaled variable**: map the outer hull `[u_min, u_max]` to `[−1, 1]` before solving
  and un-map the coefficients after — the raw powers of u at `u ≈ 1e19` (Hz² for route B later) or
  `u ∈ [0, 1]` (here) are fine in the prototype variable, but write it scaled from the start so
  `MatchRemez` is reusable by §18.6's route B unchanged.

**Gate for the class on its own** (`MatchRemezTests`): (a) a single interval `[a², 1]` reproduces the
shifted Chebyshev polynomial `T_n(x(u))` of §16.3 to 1e-10 in the coefficients, n = 1…6, a ∈ {0, 0.5,
0.73}; (b) two equal-length intervals reproduce the closed-form quadratic-mapping polynomial
`T_m(q(u))` (state q in the test); (c) equioscillation: exactly n + 1 alternating extrema of magnitude
1 ± 1e-9 on the union, for every cell of the sweep below; (d) the weighted form at `uR → ∞` reduces to
the unweighted one.

---

## 3. `MatchFormPrototype.GvaluesAtPolynomial(double[] p, double k, double eps2)`

The general-polynomial twin of `GvaluesAt`: Φ = p(u)², so the roots of `c + ε²Φ` are the roots of the
two complex polynomials `p(u) ∓ j√c/ε`. `MatchPoly.Roots` takes real coefficients; add a complex
companion-matrix solve (degree ≤ 6 — a hand-written QR on a 6×6 complex Hessenberg, or Durand–Kerner
with polish, either is fine; **not** a new dependency) and map each u through `s = ±j√u`,
`LeftHalfPlane`, `Extract`, exactly as `GvaluesAt`. Prove it against `GvaluesAt` on the single-interval
case: same g to 1e-9 for the whole MN-LP 360-cell sweep (n × a × K). That sweep is the acceptance
gate for the root path.

---

## 4. Tri-band (`BandCount = 3`)

**Mirror rule** (§18.3): the middle band f3–f4 is kept and defines `ω₀ = 2π√(f3·f4)`; each outer band
is widened *outward* to cover both itself and the log-mirror of its partner: `f1' = min(f1, f0²/f6)`,
`f6' = max(f6, f0²/f1)`, `f2' = max(f2, f0²/f5)`, `f5' = min(f5, f0²/f2)` — then check `f2' < f3` and
`f4 < f5'` (a widened outer band that reaches the middle band is a refusal: *"bands 1 and 2 overlap
after mirroring; move them apart or use dual-band"*). `MatchBands.Symmetrise3` returns the six
effective edges and a note naming every band that moved. `Effective` for three bands; `Omega0` from
the middle band; `W = (f6' − f1')/f0`; the prototype intervals in u are `[0, Ω(f4)²] ∪ [Ω(f5')², 1]`.

**Synthesis**: `MatchMultibandSynthesis` calls `MatchRemez.Minimax(n, intervals)` once per (n, band
set) — memoise it on the effective edges — and runs MB1's K/ε² search with `GvaluesAtPolynomial`
in place of `GvaluesAt`. The worst in-band `|Γ|²` is still `(K + ε²)/(1 + ε²)` because `max|p| = 1` on
the union. Orders 1…3 (4n elements); parity as MB1 for even counts.

**Measured while writing §18.5** (scratch Remez, not hardened — treat as targets to confirm, not as
goldens): bands 0.5–0.6 / 0.9–1.1 / 1.65–1.98 GHz, 50 Ω ‖ 4 pF, Chebyshev: n = 2 → −12.0 dB across all
three bands, 8 elements, `R_far ≈ 29.9 Ω`; n = 3 → −14.5 dB, 12 elements. Reproduce within 0.3 dB and
record the exact member (K, ε², g) as the golden in RESOLVED and in the test.

**Designer**: the Bands selector gains *Tri*; an f5/f6 row; the note lists moved bands; the status
strip's gap line becomes two gap lines; cards read *"Chebyshev · tri-band · order n"*.

---

## 5. Odd element counts — the weighted family

`Φ(u) = (u + u_R) · R_k(u)²` (§16.3) has degree **2k + 1 in u**, so `|Γ|²` has degree 2(2k + 1) in s
and its Hurwitz factor degree 2k + 1: the prototype has **2k + 1 elements** — k = 1 gives 3, k = 2
gives 5 — and the resonated dual-band ladder has 2k + 1 arms, 2(2k + 1) elements. Confirm with the
first extracted case in RESOLVED (the count is what `Extract` is asked for, and asking for the wrong
one returns null rather than a wrong ladder). `Φ ≥ 0` on `u ≥ 0` needs `u_R ≥ 0`.
`u_R` is the free parameter (the position of the extra pole); the family is (K, ε², u_R).

Two consumers:
- **Lowpass/highpass form, odd counts** (`MatchFormSynthesis`): DC pin → ε², §16.4's K rule, and u_R
  chosen for the best worst-case return loss (a bounded 1-D search — the odd-count family's best member
  approaches the even count below it as u_R → ∞, §16.3, so start the search near the largest u_R that
  still improves). `MatchOrders` then offers odd counts in these forms and the like-topology pair
  refusal of §16.4 item 2 goes away.
- **Dual/tri-band like pair** (`MatchMultibandSynthesis`): odd arm count, both ends shunt (or both
  series); g₁ = Q·w pins one parameter, u_R the second, K the third — search u_R and K, ε² from g₁.
  The interstage case of §4.9 (two parallel ends) is the fixture.

If the weighted family costs more than the tri-band part to make robust, **land tri-band first and
report** — the odd counts are a second milestone in this brief, not a condition of the first.

---

## 6. Tests

- `MatchRemezTests` — §2's four gates.
- `MatchFormPrototypeTests` — `GvaluesAtPolynomial ≡ GvaluesAt` across the 360-cell sweep to 1e-9.
- `MatchMultibandSynthesisTests` (extend MB1's): tri-band mirror rule (six edges, overlap refusal),
  the §4 target reproduced within 0.3 dB and pinned as a golden; **every cell extracts** for
  n ∈ {1, 2, 3} × three band sets (narrow/narrow, wide middle, wide outers) × two families; the ABCD
  oracle's worst over the three bands matches `(K + ε²)/(1 + ε²)` to 0.05 dB; old dual-band payloads
  unchanged.
- Odd counts: the 5-element lowpass ladder (k = 2) for §16.2's a = 0.5, r = 10 extracts, beats the
  4-element figure (−10.5 dB) and is beaten by the 6-element one (−19.6 dB); the like-pair dual-band
  fixture absorbs both ends with `AbsorbedEnd` on both end arms.
- Designer: Tri shows the third row and two gap lines; titles; undo.

**No timing tests.** Remez at n ≤ 6 on a 4,000-point grid is milliseconds; memoise per effective band
set and report the number if a spec edit becomes sluggish.

## 7. Gates

```
dotnet build
dotnet test tests/Core.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

Run each ONCE; read the TRX. Grep the diff for vendor or product names before finishing.

## 8. On completion

Findings — the element-count arithmetic of §5, Remez convergence behaviour on a union, the tri-band
golden member, any case where zeros land in an interval the user would not expect — to
**`src/Core/Match/RESOLVED.md`** (Designer findings to `src/Ui/RESOLVED.md`). **Never to any
`CLAUDE.md`.** Do not commit; the owner commits.

## 9. Out of scope, deliberately

- Route B — lowpass/highpass multiband and asymmetric bands (§18.6). `MatchRemez` is written scaled
  so route B can reuse it, but nothing here calls it with bands in Hz².
- Four or more bands (the mechanism generalises; the UI does not need it yet).
- Double-match / Bessel in multiband.
- Elliptic anything (§6.8).

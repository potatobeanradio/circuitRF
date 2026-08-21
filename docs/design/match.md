# circuitRF — Match: the absorbing impedance-matching component

**Status:** Proposal — rev 1, owner decisions folded in (§14) · **Date:** 2026-08-19 · **Phase:** MN0
(design layer)

**Match** is a two-port schematic component that synthesises a bandpass LC matching network which
matches **both** of its ports simultaneously to the external network — and which **absorbs each
termination's reactance into the network** rather than tuning it out. That absorption is the whole
point: a transistor's Cgs or Cds becomes an *element of the matching filter*, so the match holds over
the widest bandwidth the load's Q permits (Fano) instead of over the narrow band a tune-it-out network
gives.

It is a **direct synthesis** — procedural, closed-form, no optimiser. The user states a band, two
terminations, a network order and a response shape; the component produces a ladder, a response, and a
list of alternative solutions. Norton transforms then let the user slide element values into
manufacturable ranges **without changing the frequency response at all**.

The algorithm, the UI concepts (linked sliders, locks, the solutions list, the schematic/grid toggle)
and the worked example come from the author's existing SwiftUI application (*MatchedRF*) and its user
guide. This document specifies the circuitRF component: what is ported, what is reused from circuitRF,
and **two capabilities the reference implementation does not have** — inductive terminations (§5) and
selectable response shape (§6), both derived and numerically verified here — plus **two new UX
features** the owner has asked for: probing the external network for the target terminations (§10) and
flattening the component into an ordinary cell (§11).

---

## 1. The one-paragraph version

A `Match` instance carries a complete **design** (band, terminations, order, response type, prototype
g-values, the applied Norton transforms) as one hidden serialized parameter, exactly as `wBond` carries
its wires. Double-clicking it opens the **Match Designer**, a resizable desktop window with a
specification pane, a live LC-ladder preview (schematic or value grid), rectangular response plots
drawn by the Data Display's own `PlotControl` and computed by circuitRF's own `SParameterEngine`, a
rack of linked Norton-transform sliders, and a solutions list. The component **stamps the ladder
element by element on minted internal nodes** — not as a lumped ABCD block — so it behaves correctly at
DC and in harmonic balance and is identical by construction to the cell that **Flatten to Cell**
writes. The two absorbed termination reactances are *not* part of the component: the external network
supplies them, which is what makes the match real.

---

## 2. Scope, non-goals, and the reuse map

### 2.1 In scope

- A new primitive component type `Match` (`SymbolKind.Match`, engine reference `"Match"`, instance
  prefix `MN`), placeable from the palette, elaborating and stamping headlessly so `Cli sparam`/`hb`
  work with no UI.
- Bandpass Chebyshev/Fano synthesis, orders 2–6, with capacitive **or inductive** terminations in
  **series or shunt** topology, plus purely resistive terminations.
- Selectable response: **Chebyshev (Fano-optimum)**, **Chebyshev (fixed ripple)**, **Butterworth**, and
  **Bessel** where feasible (§6).
- Norton (π and T) transforms with positivity-bounded ranges, per-transform locks, and the linkage rule
  that keeps the cumulative transformation on target.
- The Match Designer window.
- Probe-the-external-network (§10) and Flatten-to-Cell (§11).

### 2.2 Non-goals

- **No optimiser.** Every value is produced by direct synthesis or by a user-driven transform. There is
  no "tune to spec" loop, and none is planned.
- **No distributed elements.** The output is lumped L and C. Converting to microstrip is the user's job
  (and is what §11's flatten exists to enable).
- **No lossy synthesis.** Element Q is not a design input. Losses are visible only if the user flattens
  and adds R.
- **No parameterised/swept Match.** The design is resolved in the Designer, not at elaboration time —
  see §7.5 for why, and for the escape hatch.

### 2.3 What is reused rather than rebuilt

| Need | Reused |
|---|---|
| Response computation | `SParameterEngine.Run` on an elaborated netlist of the ladder |
| Per-port reference impedance | `RFNetwork` renormalisation (`RfCore`) |
| Response plots | `CircuitRF.Ui.DataDisplay.Controls.PlotControl` + `Plot`/`Trace` (`Trace.Data` is an `SNP`) |
| Ladder preview drawing | `SchematicRenderer` conventions and `SymbolGeometry` primitives |
| Symbol glyph | `SinePrimitive` (already exists, `Cycles`/`Amp`/`Length`/`Axis`) |
| Serialized design blob on an instance | the `wBond` `Design` parameter pattern (base64 JSON, hidden) |
| Component-specific editor panel | `ParameterEditorViewModel.WBond.cs` — the established partial-class panel pattern |
| Internal nodes for a primitive | the elaborator's mint-by-instance-path mechanism (`Tuner`, `P1Tone`, `Diode`) |
| Series-LC branch stamping with correct DC | `InductorModel` (already supports `L=` + `C=` as one series branch, DC open) |
| Disabled components in the flattened cell | `DisableState.Open` |
| Undo | the owning schematic's `UndoRedoStack` via command objects |
| Net/topology queries for the probe | `NetExtractor.Extract` → `TestBench` |

---

## 3. Background and references

> "Efficient broadband impedance-matching structures are necessarily filter structures."
> — Matthaei, Young & Jones

1. G. Matthaei, L. Young, E. M. T. Jones, *Microwave Filters, Impedance-Matching Networks, and Coupling
   Structures*, McGraw-Hill 1964 / Artech 1980. §4.09 (matching networks with a prescribed load
   decrement), §4.12 (Norton transforms), pp. 96–99 and p. 130 for the prototypes the reference
   implementation uses.
2. D. E. Dawson, "Closed-form solutions for the design of optimum matching networks," *IEEE Trans.
   MTT*, vol. 57, no. 1, pp. 121–129, Jan. 2009.
3. R. Levy, "Explicit formulas for Chebyshev impedance-matching networks, filters, and interstages,"
   *Proc. IEE*, vol. 111, no. 6, pp. 1099–1106, Jun. 1964. — the closed-form recursion in §4.3.
4. T. E. Shea, *Transmission Networks and Wave Filters*, Van Nostrand 1929, p. 325. — the Norton
   transforms.
5. R. M. Fano, "Theoretical limitations on the broadband matching of arbitrary impedances," *J.
   Franklin Inst.*, 1950. — the bound that makes absorption the right idea and perfection impossible.

---

## 4. The synthesis

This section states the algorithm precisely enough to implement and to test against. It is the
reference implementation's algorithm, re-expressed; §5 and §6 extend it.

### 4.1 Terminations, and the only thing the synthesis needs from them

Each end is a resistance in series or in parallel with **one reactive element**. Define, at the band
centre ω₀ = √(ω₁ω₂) with fractional bandwidth *w* = (ω₂ − ω₁)/ω₀:

```
  parallel R‖C :  Q = ω₀ R C
  series   R+C :  Q = 1 / (ω₀ R C)
```

**Q is the entire content of the termination as far as the prototype is concerned.** R re-appears later
only as an impedance scale factor and as the Norton-transform target. This is the classical *decrement*
δ = 1/(Q·w) of Matthaei §4.09, inverted.

### 4.2 Which end drives the synthesis

The prototype can prescribe the absorbed element at **one** end. The reference implementation calls that
the *analysis end*, chooses it as the **higher-Q end** by default (`AnalysisEnd = "h"`, overridable to
either end), and lets the other end fall out of the synthesis as `Q_far = g_n·g_{n+1}/w`. The far end is
then reconciled against its real termination by §4.5 and §4.7.

Choosing the higher-Q end is correct because the higher-Q termination is the binding constraint — it is
the one Fano's bound limits.

**Order parity is not free.** With a reactive element at both ends, the ladder must start and end on the
right kind of arm: an end absorbing a *series* reactance needs a series arm there, an end absorbing a
*shunt* reactance needs a shunt arm. Since arms alternate, the mixed case (one series, one shunt) forces
**even** *n*, and the like-kind case forces **odd** *n*. The Designer's order picker therefore offers
{2,4,6} or {3,5} accordingly, and offers all of {2,…,6} when either end is purely resistive. Changing a
termination's topology adjusts *n* by ±1 rather than presenting an unsatisfiable order.

### 4.3 The lowpass prototype — Chebyshev, Fano-optimum

Levy's explicit formula, with `Q` the analysis-end Q, `n` the order, θ = π/2n:

```
  c        = (2 / (Q·w))·sin θ
  r        = the first real root of P_n(c)          (a quartic/sextic in r; table below)
  d        = √(c²/4 + r)
  sinh a   = d + c/2
  D        = sinh a / (sin θ /(Q·w)) − 1

  g₀       = 1
  g₁       = Q·w                                     ← the absorbed element. Fixed, by construction.
  g_{j+1}  = 1 / (g_j · k_j²)   for j = 1 … n−1
      k_j² = [ sin²(zθ)cos²(zθ) + (cos²(zθ) + D² sin²(zθ))·sin²θ·(1/(Q·w))² ]
             / [ sin((2z−1)θ)·sin((2z+1)θ) ],   z = j
  g_{n+1}  = Q·w / (D · g_n)
```

`P_n(c)` (descending coefficients), which is where the *Fano* choice of D enters — the root selects the
member of the family with the best flat loss:

| n | P_n(c) |
|---|---|
| 2 | r = 1/2 directly |
| 3 | 16, 16, 3+12c², −3−4c² |
| 4 | 16, 8, 2+8c², −1−2c² |
| 5 | 256, 512, 16(21+20c²), 16(5+12c²), 5(1+12c²+16c⁴), −(5+20c²+16c⁴) |
| 6 | 1024, 1536, 256(3+4c²), 128(1+3c²), 4(3+32c²+48c⁴), −2(3+16c²+16c⁴) |

No real root ⇒ **no solution at this order for this Q** — a first-class outcome the UI must state, not
an error.

**The non-Fano Chebyshev alternative is not a ripple setting — it is the *two-ended* prototype**, and
it is a genuinely different design, not a lesser one. Levy's doubly-prescribed form takes **both** end
Q's as inputs:

```
  x = (1/(Q_far·w) + 1/(Q_ana·w))·sin θ
  y = (1/(Q_far·w) − 1/(Q_ana·w))·sin θ

  g₀     = 1
  g₁     = 2·sin θ /(x − y)                                    ( = Q_ana·w, identically )
  g_{r+1}= 4·sin((2r−1)θ)·sin((2r+1)θ)
           / ( g_r·[ x² + y² + sin²(2rθ) − 2xy·cos(2rθ) ] )     r = 1 … n−1
  g_{n+1}= 2·sin θ / ((x + y)·g_n)                              ( ⇒ Q_far comes out EXACTLY right )
```

Because `x + y = 2 sin θ/(Q_far·w)`, the far end's Q falls out *equal to the real far termination's*.
**Both ends absorb exactly and §4.5's excess element is never needed.** What it gives up is Fano
optimality: the return loss is worse than the Fano member for the same order. This is the choice the
reference implementation's "Fano Optimum" toggle actually makes, and both belong in the response
selector.

A third Chebyshev case is needed when **neither** end has a reactance to absorb — the component is then
a plain bandpass transformer and there is no Q to prescribe. Use the standard equal-ripple filter
prototype with a ripple specification (default 0.1 dB):

```
  B  = ln( coth(L_Ar/17.37) )          γ = sinh(B/(2n))        θ = π/(2n)
  a_k = sin((2k−1)θ)                   b_k = γ² + sin²(kπ/n)            k = 1 … n
  g₀ = 1     g₁ = 2·a₁/γ
  g_k = 4·a_{k−1}·a_k / (b_{k−1}·g_{k−1})                       k = 2 … n
  g_{n+1} = n even ? coth²(B/4) : 1
```

### 4.4 Bandpass transformation and absorption

Frequency-scale, then impedance-scale, then resonate:

```
  g_freq[j]  = g[j] / (w·ω₀)                                   j = 1 … n
  g_imp[j]   = g_freq[j]·R_ana   if arm j is series
             = g_freq[j]/R_ana   if arm j is shunt
  series arm j :  L = g_imp[j],           C = 1/(ω₀²·L)
  shunt  arm j :  C = g_imp[j],           L = 1/(ω₀²·C)
```

`R_ana` is the analysis end's resistance. The end arm's absorbed element then comes out **exactly equal
to the termination's own reactance** — that identity is the acceptance test in §13, and it holds to
machine precision (verified: 10.0000 pF requested, 10.0000 pF synthesised).

The two terminating g's become the ladder's port resistances: `R_ana` at the analysis end, and
`R_far = g_{n+1}·R_ana` (or `R_ana/g_{n+1}` for a series far arm) at the other. `R_far` is **not** the
requested far resistance — closing that gap is §4.7's job.

### 4.5 Excess reactance at the far end

The far end's synthesised Q is `Q_far = g_n·g_{n+1}/w` (inverted for a series far arm). Three cases:

- **Q_far ≈ Q_actual** (within 2 %) — nothing to do.
- **Q_far > Q_actual** — the design wants *more* absorbed reactance than the termination provides. The
  surplus becomes a **real added element** in parallel (shunt end) or in series (series end) with the
  termination's own: the reference implementation names it `CFano`. The end arm's total is unchanged, so
  the response is unchanged; the netlist now shows the load's own reactance separately from ours, which
  is exactly what §11's flatten needs.
- **Q_far < Q_actual** — **not absorbable.** The termination's reactance exceeds what the network can
  take. The design is refused at this order/response; the UI must say *which end* and *by how much*, and
  offer the remedies that actually work: a different order, a different response shape, the other
  analysis end, or a Q-adjusted solution (§4.6).

### 4.6 Q-adjusted solutions

Deliberately inflating the analysis-end Q above its true value adds a shunt (or series) element at the
analysis end and generally **lowers the order needed to complete the match**, at a modest cost in return
loss. The reference implementation finds the minimum such Q by bisection (15 iterations over a decade
around the true value), floors it at a `Qmin` setting, and offers the result as an extra solution
labelled *Q-adjusted*. Port this as-is, including the `Qmin` setting and the label.

### 4.7 Norton transforms — the element-value degrees of freedom

A Norton transform replaces an L-section of two like-kind elements with a π or T of three like-kind
elements plus an ideal transformer of ratio *N*, then **absorbs the transformer by scaling the rest of
the network** (impedances by N², so L·N², C/N²). The network's transfer function is unchanged; only the
element values and the terminating resistance move. This is what makes the tool usable: it is how a
2.1 pH inductor becomes something a PCB can build.

For an L-section with series impedance Z₁ and shunt impedance Z₂ (impedances, so a capacitor enters as
1/C):

```
  π form,  N > 1 :   v₁ = N·Z₁/(N−1)            v₂ = N·Z₁      v₃ = N²Z₁Z₂/(Z₁+(1−N)Z₂)
  π form,  N < 1 :   v₁ = N²Z₁/(1−N)            v₂ = N·Z₁      v₃ = N·Z₁Z₂/(N·Z₁+(N−1)Z₂)
  T form,  N > 1 :   v₁ = Z₁+(1−N)Z₂            v₂ = N·Z₂      v₃ = N(N−1)Z₂
  T form,  N < 1 :   v₁ = N²(Z₁+Z₂)−N·Z₂        v₂ = N·Z₂      v₃ = (1−N)Z₂
```

(capacitors invert back on the way out; when the shunt element is the first of the pair, v₁ and v₃
swap). The transform propagates **right** when (N>1 and the first element is the series one) or (N<1 and
it is the shunt one), and left otherwise; the scaling by N² is applied to everything on the propagation
side.

**Positivity bounds the slider.** The expressions above go negative past a threshold:

```
  N_threshold = 1 + Z₁/Z₂           when the transform must step up
              = Z₂/(Z₁+Z₂)          when it must step down
```

so the slider range is `[1, N_threshold]` or `[N_threshold, 1]`. Only when the user opts into
**Allow negative components** does the range widen to `[1,10]` / `[1e-3,1]`. This is the single
mechanism that keeps the LC solution positive, and it is why the slider ends where it does.

**Which pairs are transformable** is a structural scan: two like-kind elements at most three positions
apart, of opposite orientation (one series, one shunt), with the intervening elements of matching type
when they are three apart, and **never moving an end element that carries an absorbed termination
reactance** (moving it would break the absorption). Conflicting pairs — those that would need the same
element twice, directly or after the moves — are recorded so the solution search never proposes both.

### 4.8 The linkage rule

`Π Nᵢ² ` must equal `required_transform = R_far_target / R_far_synthesised`. With **Link** on (the
default), moving one slider re-solves the *unlocked* others so the product stays on target; with a
single transform and Link on, N is fully determined and the slider is disabled. Locked transforms are
never touched. When the product cannot reach the target within the ranges, the offending termination is
shown **in red** and the design is flagged as matched on one end only.

### 4.9 Worked example — the golden numbers

The user guide's interstage problem, and the acceptance anchor for §13. Stage-1 output (200 Ω ‖
0.125 pF) to stage-2 input (1.25 Ω + 10 pF), 3.3–5.0 GHz:

```
  ω₀/2π = 4.06202 GHz    w = 0.418511
  Q(200Ω‖0.125pF) = 0.63806       Q(1.25Ω+10pF) = 3.13450  → analysis end = the series end
  n = 4, Chebyshev/Fano:
      g = [1, 1.311823, 1.106975, 1.717201, 0.508891, 1.344236]   Q_far = 1.63453  (≥ 0.63806 ✔)
  ladder, from the analysis end:
      series   L = 153.5169 pH  C =  10.00000 pF  ← C is the load's own 10 pF, exactly
      shunt    L =  18.5164 pH  C =  82.90847 pF
      series   L = 200.9567 pH  C =   7.63931 pF
      shunt    L =  40.2782 pH  C =  38.11411 pF
      R_far = 1.68030 Ω →  required Π N² = 119.027
  response over 3.3–5.0 GHz:  worst |S11| = −16.663 dB,  insertion loss 0.095 dB,
                              insertion-loss ripple = 0.0361 dB
```

These were produced by an independent implementation written while designing this document; its
prototype g-values reproduce the reference implementation's closed form, and its ladder reproduces the
published example. **They become golden values in `tests/Core.Tests`.**

---

## 5. Inductive terminations — derivation and result

**The owner asked whether series or parallel *inductive* terminations can be supported. They can, and
the change is one line of arithmetic. The result is exact, not an approximation.**

### 5.1 The claim

A termination `R ‖ L` behaves, for this synthesis, **identically** to `R ‖ C_eq` with

```
  C_eq = 1 / (ω₀² · L)                and dually        L_eq = 1 / (ω₀² · C)
```

and likewise for the series pair. Everything downstream — the prototype, the parity rule, the
excess-reactance rule, the Norton transforms, the solution search — is unchanged.

### 5.2 Why it is exact

The bandpass transformation gives every arm **both** an L and a C, resonant at ω₀:

```
  shunt arm:   C_arm  and  L_arm = 1/(ω₀²·C_arm)     — one fixes the other
  series arm:  L_arm  and  C_arm = 1/(ω₀²·L_arm)
```

Absorption means *the termination's reactive element **is** one of the arm's two elements, and we supply
the other*. For a shunt end arm:

- capacitive load: absorb C_arm = C_load, we build L_arm = 1/(ω₀²C_load);
- inductive load: absorb L_arm = L_load, we build C_arm = 1/(ω₀²L_load).

In both cases the **finished arm is the same two elements** — the only difference is which one arrives
from outside. Since the finished network is literally identical, the frequency response is identical.
There is no approximation and nothing to validate beyond the arithmetic.

The end-element Q follows the same substitution:

```
  parallel R‖L :  Q = ω₀·R·C_eq = R/(ω₀L)
  series   R+L :  Q = 1/(ω₀·R·C_eq) = ω₀L/R
```

which are the textbook Q's of those one-poles — a useful independent check that the substitution is the
right one.

**Verified on §4.9's example.** Its analysis-end arm is L = 153.517 pH with C = 10 pF. Replacing the
1.25 Ω + 10 pF series load with **1.25 Ω + 153.517 pH** gives Q = ω₀L/R = 3.1345 — the same Q to five
figures — and therefore the same prototype, the same ladder and the same response, with the 153.517 pH
now arriving from the load and the 10 pF being ours to build.

### 5.3 What changes in the code

- The termination model becomes `{ R, Kind ∈ {None, C, L}, Topology ∈ {Series, Parallel}, Value }`.
- One helper: `CeqAtCentre(kind, value, ω₀) => kind == C ? value : 1/(ω₀²·value)`. Every existing use of
  `Ca`/`Cb` reads through it.
- The "reactance = 0 means purely resistive" convention becomes "**Kind = None**". This is a genuine
  improvement over the reference implementation, where `C = 0` had to mean *no reactance* and therefore
  could not be distinguished from a legitimately tiny capacitance, and where a series termination with
  `C = 0` had to be silently rewritten to parallel. `Kind = None` says it directly, and the inductive
  case has no zero to overload (an inductive "none" would be L = ∞).
- The excess-reactance rule (§4.5) is stated in **Q**, and so already covers all four combinations
  unchanged: the surplus element is of the *same kind and topology* as the absorbed one, and the
  criterion `Q_far > Q_actual` means the same thing in all four. (It is worth stating why, because in
  C_eq space the direction is *not* the same: at a shunt end a surplus means the design wants more C_eq
  than the load provides, and at a series end it means less — series elements combine reciprocally.
  Q's own inversion for series topologies already absorbs that flip, which is why the rule reads the
  same for all four and why it should be written in Q rather than in C_eq.)
- The parity rule (§4.2) keys on **topology only** (series vs shunt), which the substitution does not
  touch.

### 5.4 Why this matters more than it looks — conjugate targets

**The conjugate of a capacitive load is inductive.** A designer who wants a conjugate match to a
transistor's parallel-RC output — which §10's probe measures directly — needs to enter a parallel-RL
target. Today that is unrepresentable, so the most common thing anyone would want to do with §10's
result cannot be expressed. §5 and §10 are the same feature seen from two sides, and neither is complete
without the other.

### 5.5 Edge cases

- `L → ∞` is the inductive "no reactance"; the UI expresses it as **Kind = None** rather than by a
  magic number.
- `L → 0` (a short) makes Q infinite for the parallel case and zero for the series case; it is refused
  with a stated reason, exactly as `C → ∞` is.
- A termination that is genuinely **R + L + C** is out of scope: the synthesis absorbs *one* reactance
  per end. §10's fit reports the residual so the user can see when the two-element model is inadequate.

---

## 6. Response shape — Butterworth and Bessel

**The owner asked whether the Chebyshev-only workflow can be extended to Butterworth and Bessel, noting
that the papers' approximations may not carry over. The answer is: Butterworth yes, Bessel only in
narrow circumstances — and the reason is precise, not a matter of taste.**

### 6.1 What the Chebyshev closed form actually needs

Look at §4.3: `g₁ = Q·w` is *set*, not derived. The whole synthesis is "produce a ladder whose **first
element is prescribed** and whose transducer response has shape X". A response family can do this only
if it has a **free in-band parameter** to spend on that constraint — and Fano guarantees the in-band
reflection cannot be zero, so that parameter exists physically as *how badly matched the network is at
band centre*. For Chebyshev it is the ripple level (the `D` of §4.3, chosen by the Fano root). The
question for any other family is simply: *does it have such a parameter, and does the resulting ladder
come out realizable and positive?*

### 6.2 The general route

Both new families are synthesised the same way, and the route also **re-derives the Chebyshev case as a
cross-check**:

1. Write the reflection magnitude in the lowpass-prototype domain as a two-parameter family
   ```
     |Γ(jΩ)|² = (K + ε²F(Ω)²) / (1 + ε²F(Ω)²)
   ```
   with `F = T_n` (Chebyshev) or `F = Ωⁿ` (Butterworth). `K = |Γ(0)|²` is the band-centre mismatch floor
   Fano forces; `ε` sets how fast the match degrades toward the band edge.
   For **Bessel** the family is instead `|Γ|² = 1 − C/|θ_n(jΩ/α)|²` with θ_n the reverse Bessel
   polynomial — the transfer function stays exactly Bessel (C is a *flat* loss and does not touch phase),
   so the maximally-flat group delay is preserved; α is the frequency scale and C the mismatch floor.
2. Spectrally factor numerator and denominator, keep the Hurwitz (LHP) factors → Γ(s).
3. `Z(s) = (1+Γ)/(1−Γ)`, choosing the sign of Γ so the first element comes out the required kind.
4. Extract the ladder by continued fraction (Cauer). Refuse the member if any extracted element is
   ≤ 0 or the degrees do not step down — that is the realizability test, and it is decisive.
5. Solve the remaining free parameter so that `g₁ = Q·w` exactly (a monotone 1-D root-find), then
   choose the *other* free parameter to optimise, subject to §4.5's far-end constraint `Q_far ≥ Q_actual`.

**Validation of the route:** run at n = 4 on §4.9's problem it reproduces the closed-form Chebyshev
prototype — extracted `g = [1, 1.3118, 1.1034, 1.7125, 0.5045, 1.3443]` against the closed form's
`[1, 1.3118, 1.1070, 1.7172, 0.5089, 1.3442]`. Same ladder to ~0.4 %, which is the root-find's own
tolerance. The Butterworth and Bessel answers below therefore rest on machinery known to be correct.

### 6.3 Butterworth — available

Realizable, positive ladders exist at every order tested, and the free parameter genuinely trades
in-band return loss against far-end Q. Butterworth is a legitimate choice with a clear character: a
**monotone** in-band response and roughly **half the group-delay variation** of the Chebyshev design, at
a cost of several dB of worst-case return loss.

One caveat the implementation must respect: **the best-return-loss member is not always feasible.** At
n = 6 on §4.9's problem the best-RL Butterworth member has `Q_far = 0.483` against a required 0.638 and
is refused; a different member of the same family reaches `Q_far = 0.642` and is accepted at 8.29 dB.
So the search is a *constrained* optimisation — maximise worst-case return loss **subject to** the
far-end absorption constraint — not an unconstrained one. (The same is true of Chebyshev; the Fano root
happens to land inside the feasible set in most practical cases.)

### 6.4 Bessel — feasible as a prototype, usually refused by the far end

Bessel prototypes with a prescribed first element **do** exist: the family has two free parameters
(α, C), the extraction succeeds, the elements are positive, and the group delay is exactly
maximally flat. The near-end absorption is satisfied.

**What fails is the far end.** The Bessel family's achievable `Q_far` collapses as order rises: on
§4.9's problem the maximum `Q_far` over the whole family is 0.819 at n = 2, 0.325 at n = 4 and 0.183 at
n = 6, against a required 0.638. So only n = 2 closes, and only just. Its in-band return loss is
7.7 dB — poor, as expected: a Bessel response deliberately trades passband flatness for delay
flatness, and a matching network's job is passband flatness.

**Decision (owner, 2026-08-19): ship Bessel**, gated by the same feasibility test every other response
passes through, and expect it to be refused more often than not. It is genuinely the right answer for a low-order,
low-far-end-Q, delay-critical interstage, and it costs nothing extra to offer once §6.2's machinery
exists. It must never be the default, and the refusal message must say *"Bessel cannot absorb
termination 2 at this order — its far-end Q reaches only 0.33 against the 0.64 needed"*, naming numbers.

### 6.5 The measured comparison

§4.9's problem, best feasible member of each family (worst in-band return loss, insertion-loss ripple,
group delay mean ± half-spread, and the far-end Q against the 0.638 required):

| n | Chebyshev (Fano) | Butterworth | Bessel |
|---|---|---|---|
| 2 | **12.21 dB**, 0.17 dB, 172 ± 57 ps, Q_far 1.90 | 9.94 dB, 0.25 dB, 139 ± 34 ps, Q_far 1.13 | 7.74 dB, 0.71 dB, 127 ± 27 ps, Q_far 0.65 |
| 4 | **16.66 dB**, 0.04 dB, 468 ± 195 ps, Q_far 1.63 | 13.20 dB, 0.08 dB, 317 ± 90 ps, Q_far 0.68 | *infeasible* (max Q_far 0.33) |
| 6 | **18.32 dB**, 0.02 dB, 844 ± 438 ps, Q_far 1.47 | 8.29 dB, 0.67 dB, 717 ± 323 ps, Q_far 0.64 | *infeasible* (max Q_far 0.18) |

Read across: Chebyshev wins on return loss at every order, by 3–4 dB where Butterworth is comfortably
feasible; Butterworth wins on group-delay flatness by roughly 2×; Bessel wins on delay and loses on
everything else. Read down the Butterworth column: the far-end constraint tightens with order, which is
the opposite of the intuition that more elements always help.

### 6.6 What the user sees

A **Response** selector in the specification pane:

```
  Response:  ( ) Chebyshev — Fano optimum      [default]
             ( ) Chebyshev — both ends prescribed
             ( ) Butterworth
             ( ) Bessel                        [disabled with a reason when infeasible]
             Ripple [ 0.10 ] dB                [enabled only when neither end has a reactance]
```

**"Both ends prescribed"** is §4.3's two-ended Levy prototype: it absorbs both terminations exactly and
never needs §4.5's excess element, at the cost of Fano optimality. It is the reference implementation's
"Fano Optimum" toggle in its off position, and it is the right choice when a surplus element is
unwelcome. Changing the selector re-runs the solution search and repopulates the solutions list. A response that cannot
absorb both ends at the current order is shown disabled with the numeric reason in its tooltip, never
silently missing.

### 6.7 Numerical robustness

The §6.2 route is polynomial root-finding on degree-2n polynomials, which is well conditioned for
n ≤ 6 but not free: the extraction must trim leading coefficients **by tolerance relative to the
polynomial's scale**, not by exact zero, or a 1e-16 residue makes a degree-3 polynomial look
degree-4 and the extraction silently fails. (This is not hypothetical — it is what happened first while
verifying §6.3.) The Chebyshev path keeps its closed form and is not routed through the numerical
extractor; the numerical extractor's agreement with it is a permanent regression test (§13).

---

## 7. Data model and persistence

### 7.1 `MatchDesign`

One serializable object, the single source of truth:

```
  MatchDesign
    Version                       int
    F1, F2                        double, Hz
    Order                         int  (2…6)
    Response                      enum { ChebyshevFano, ChebyshevTwoEnded, Butterworth, Bessel }
    RippleDb                      double        (real-to-real prototype only; default 0.1)
    Term1, Term2                  Termination { R, Kind{None,C,L}, Topology{Series,Parallel}, Value,
                                                Source{Manual,Probed}, ProbedAtUtc }
    AnalysisEnd                   enum { Highest, Term1, Term2 }
    QAdjust                       double  (0 = none)
    AllowNegativeComponents       bool
    LinkTransforms                bool
    Transforms[]                  { Pair{ElementA, ElementB}, Form{Pi,T}, N, Locked }   ordered
    AppliedSolutions[]            solution fingerprints, for the solutions-list badges
    BasisFingerprint              string, see §7.3
    PlotBandFraction              double  (default 0.10)
    PlotPoints                    int     (default 401)
```

Everything else — the g-values, the element list, the response — is **derived** and never stored. This
matters: a stored element list can disagree with its own inputs, and the reference implementation's
save/restore path is complicated by exactly that. Deriving keeps the design honest at the cost of one
synthesis pass at load, which is microseconds.

**`NMin`/`NMax` are derived too, and deliberately not stored.** A transform's range is the positivity
threshold of §4.7, computed from the element values *as they stand when that transform is applied* —
which depends on every earlier transform in the list. Storing the bounds would let them go stale
against the elements they bound, and a stale bound is worse than no bound: it silently permits a
negative element. They are recomputed during the sequential rebuild of §7.3, where the state they
depend on actually exists. (The reference implementation persists them and carries a decode-time repair
for `N_min > N_max`, which is the symptom of exactly this staleness.)

### 7.2 On the instance

- **`Design`** — hidden string parameter, base64 of the JSON, exactly the `wBond` `Design` pattern. This
  is authoritative and complete.
- **`F1`, `F2`, `Order`, `Response`, `R1`, `R2`** — *echo* parameters, written **only** by the Designer,
  read-only in the generic parameter editor, existing so the user can display them on the schematic.
  This mirrors `wBond`'s `Arrays`: bookkeeping maintained by the editor, never a second input.
- A `Match` whose `Design` fails to decode refuses at elaboration with a message naming the instance and
  telling the user to re-open the Designer — it does not silently fall back to a default network.

### 7.3 What is restored when the schematic re-opens — everything the user set

**Yes: the π/T choice, every N, every lock, the link state, which solution was applied, and the
Q-adjust are all persisted and all restored.** Re-opening a workspace and then the Designer puts the
user back exactly where they left off, with the same ladder, the same element values and the same
response. This is worth stating as a requirement rather than assuming it, because the restore path has
one real trap in it (below).

Load is a **sequential rebuild**, not a snapshot restore:

```
  1. synthesise the basis ladder from F1/F2/Order/Response/Terminations/QAdjust        (§4.3–§4.6)
  2. for each stored transform, in order:
        a. resolve its element pair by NAME against the current network state
        b. recompute NMin/NMax from those elements                                     (§4.7)
        c. clamp the stored N into that range, and record if clamping occurred
        d. apply the transform, producing the next network state
  3. re-run the response                                                               (§9.6)
```

Step 2a is the trap. **Transforms must be stored by element *name*, not by positional index.** The
reference implementation stores `index1`/`index2` — positions in the network array at the moment the
transform is applied — which round-trips correctly only while the basis ladder comes out byte-identical
every time. Since §7.1 derives the ladder rather than storing it, any change to the synthesis (a
different Fano root, a fixed rounding, a new response type) would silently re-point every transform at
different elements and produce a different network with no error anywhere. Names cost nothing and make
the failure detectable.

**`BasisFingerprint`** is the belt to that braces: a short hash of the basis ladder's *structure*
(element count, per-arm type and orientation, and the g-values to 6 significant figures), written when
the design was last edited. On load, a mismatch means the synthesis has changed underneath a stored
design. The Designer then opens with the stored transforms applied **and a banner** saying so, listing
what moved; it neither discards the user's work nor pretends nothing happened. A `Match` that is only
being *simulated* (never opened) uses the rebuilt network and adds an elaboration warning naming the
instance.

A transform whose element pair no longer exists is dropped, named in the banner, and the remaining
transforms are re-linked (§4.8) so the far termination is still driven at its target — the same repair
the Designer performs when the user removes a transform by hand.

### 7.4 File formats

Nothing new. The blob rides inside `.csch` as an ordinary parameter value. A standalone `.match`
export/import of the same JSON is **deferred** (owner, 2026-08-19) — the design already travels inside
the schematic, and once flattened it travels as an ordinary cell. Revisit only when someone needs to
move a design between workspaces without carrying the schematic.

### 7.5 Why the design does not participate in sweeps or expressions

`F1`, `Order` and the terminations cannot be expressions, and a `Match` cannot be swept. The synthesis
involves a **discrete solution search and a user choice among alternatives** (which solution, which
transforms, which locks); re-running it silently per sweep point would change the network's *topology*
between points, and a parametric sweep whose topology changes is not a sweep of anything.

The escape hatch is §11: **flatten to a cell**, after which every L and C is an ordinary parameter that
can be swept, expressed and optimised like any other. That is the intended workflow for anyone who wants
to sweep, and it should be said in the Designer's Help.

---

## 8. The component model

### 8.1 Pins and orientation

Two pins, left = port 1 = **Termination 1** side, right = port 2 = **Termination 2** side. Ground is the
common return. `Nodes[0]` = port-1 signal, `Nodes[1]` = port-2 signal — the `TLineModel` convention.

### 8.2 What it contains, and what it does not

**The component stamps the ladder minus the two absorbed termination reactances.** Those belong to the
external network — absorbing them is the entire premise. If a design's end arm is (L = 153.517 pH,
C = 10 pF) with the 10 pF absorbed, the component contains the 153.517 pH only. A `CFano` surplus
element (§4.5), by contrast, **is** ours and **is** stamped.

Getting this backwards would produce a component that works beautifully in the Designer's preview and
is wrong the moment it is placed. It deserves an explicit test (§13).

### 8.3 How it stamps — elementwise on internal nodes, not as an ABCD block

The model declares `InternalNodeCount` (one per series arm beyond the first), the **elaborator mints
those nodes** keyed on the instance path — the mechanism `Tuner`, `P1Tone` and `Diode` already use — and
`Stamp` contributes each element exactly as the primitive models do:

- a series arm → one branch-current unknown with `Z = jωL + 1/(jωC)`, which is precisely what
  `InductorModel` already implements for `L=` + `C=`, **including the DC-open behaviour at ω = 0**;
- a shunt arm → a capacitive admittance plus an inductive branch to ground.

**Why not a lumped ABCD/Y block.** Three reasons, in order of importance:

1. **DC and HB correctness for free.** A cascade of 2×2 ABCD matrices diverges at ω = 0 (a series C's
   1/(jωC)), and HB always includes the DC harmonic. Stamping elements inherits the primitives'
   already-correct DC limits instead of re-deriving them.
2. **Internal node voltages carry their own harmonic content.** Collapsing them is exact at DC and wrong
   in HB — the documented reason `DiodeModel`'s internal node is not eliminated locally.
3. **Identical by construction to the flattened cell.** §11 writes the same elements as ordinary
   components; §13 asserts the two agree to 1e-12. With an ABCD block that equality would be an accident
   waiting to break.

`ModelKind.Linear`, `PortCount = 2`.

### 8.4 The symbol

A square body carrying the standard bandpass glyph: **three stacked full-cycle sine waves**, with a
**slash across the top and bottom** ones. Built from three `SinePrimitive`s (`Cycles = 1`,
`Axis = Horizontal`) plus two `LinePrimitive` slashes and a `RectPrimitive` body, with pins left and
right. The sine primitive already exists and already rotates and scales correctly.

Registry entry: `ComponentTypeRegistry` — display name `Match`, prefix `MN` (`M` is taken by
`Mutual`), `IsCommon: true`, and a **new** `ComponentCategory.Matching` (owner, 2026-08-19). Search
terms: *impedance matching, filter, filter design, transform, Chebyshev, Butterworth, Bessel* — plus
*match, matching, interstage, Fano, Norton, absorb, Cgs, Cds, Ropt* so the people who think of it by
what it solves rather than by how it works still find it.

A new category rather than `Other` is a deliberate departure from the `wBond` precedent of not
inventing a category for one component. The difference is that "Matching" names a *class of things a
user goes looking for*, not this one part: it is where a future matching-network family belongs, and a
user hunting for "how do I match this" scans category names before search terms. `Other` would hide
the headline component of the release behind the least descriptive label in the picker.

---

## 9. The Match Designer

The reference implementation targets a phone: five tabs, a modal sheet for solutions, and a schematic
that has to share ~360 pt with every input field. **On a desktop none of that is necessary**, and the
port should spend the space rather than reproduce the constraint. The concepts to keep verbatim are the
ones that carry the design intent: linked sliders with locks, the schematic ⇄ value-grid toggle, the
solutions list with its "applied / ever applied / current" badges, and the red flag on an unmatched
termination.

### 9.1 Layout

One resizable window, default 1280 × 860, minimum 1000 × 700, non-modal, opened per instance:

```
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│ Match — MN1                                        [Solutions ▸] [Settings] [Help] [Close]│
├───────────────────┬──────────────────────────────────────┬───────────────────────────────┤
│ SPECIFICATION     │ NETWORK            [schematic│grid]  │ RESPONSE                      │
│                   │                                      │  ┌─────────────────────────┐  │
│ Termination 1     │   ┌────────────────────────────────┐ │  │ |S11|, |S21|  vs freq   │  │
│  ( )Series (•)Par │   │  LC ladder preview, with the   │ │  └─────────────────────────┘  │
│  R  [ 200 ] Ω     │   │  transform brackets drawn      │ │  ┌─────────────────────────┐  │
│  X  (•)C ( )L ( )–│   │  under the pairs they act on   │ │  │ phase / group delay     │  │
│     [0.125] pF    │   │                                │ │  └─────────────────────────┘  │
│  [ Probe ▾ ]      │   └────────────────────────────────┘ │  Band [3.3 … 5.0] GHz ±10%   │
│                   │                                      │  Points [401]                 │
│ Termination 2     │  MN1.L1  153.517 pH                │                               │
│  (•)Series ( )Par │  MN1.C1   10.000 pF   ← absorbed     │  Q1 0.638   Q2 3.134          │
│  R  [ 1.25 ] Ω    │  MN1.L2    18.516 pH                 │  worst RL 16.66 dB            │
│  X  (•)C ( )L ( )–│  MN1.C2   82.909 pF                  │  IL 0.095 dB, ripple 0.036 dB │
│     [ 10  ] pF    │  …                                   │  Π N² 119.0 / 119.0  ✔ matched│
│  [ Probe ▾ ]      │                                      │                               │
│                   ├──────────────────────────────────────┴───────────────────────────────┤
│ Band [3.3][5.0]GHz│ TRANSFORMS                            [+ add ▾] [− remove] [🔗 link]  │
│ Order  2 (4) 6    │  N1  (π│T)  [2.9142]  ├────────●─────────┤  🔓   on (L2, L4)          │
│ Response  ▾       │  N2  (π│T)  [3.7415]  ├──────────●───────┤  🔒   on (C3, C5)          │
│ [ ] Q-adjust      │                                                                       │
│ [ ] Allow neg.    │                                                                       │
├───────────────────┴───────────────────────────────────────────────────────────────────────┤
│ 3 solutions available · applied: 2-transform, Fano   [Flatten to Cell…]  [Apply] [Revert] │
└───────────────────────────────────────────────────────────────────────────────────────────┘
```

`Solutions ▸` slides out a **docked list panel** on the right (not a modal sheet), so a user can click
through candidate solutions and watch the ladder and the response change live.

### 9.2 The specification pane

Termination groups keep the reference implementation's small **RC pictogram** — it is the fastest way to
show series-vs-parallel — extended to draw an inductor when Kind = L. Every numeric field is a
circuitRF value+unit pair using the existing parameter-editor field conventions, so unit handling,
validation and formatting come for free.

The **Order** picker offers only the parities §4.2 permits, and changing a topology adjusts the order
rather than presenting an impossible one — with a one-line note saying it did, because a control that
silently changes another control is worse than one that explains itself.

### 9.3 Network pane

Two presentations of the same thing, toggled:

- **Schematic** — the ladder drawn with circuitRF's own symbol geometry, each element labelled with its
  instance and value, **negative or out-of-range values in red**, absorbed elements drawn in a distinct
  (dimmed) role so it is obvious which two elements the user does not have to buy. Transform brackets
  are drawn beneath the pairs they act on and are stacked when they would overlap.
- **Grid** — instance, type, value, unit, one row per element; sortable; copyable to the clipboard as
  CSV.

**No nearest-standard-value column** (owner, 2026-08-19). What counts as a realizable value is the
user's call and depends on the flow: in an MMIC flow a capacitor is *designed* to its value and an E24
series is meaningless, and even on a board the available series depends on the vendor and the package.
The grid shows the synthesised value and the sliders move it; deciding what is buildable stays with the
person who knows the process.

### 9.4 The transform rack

One row per applied transform: label, π/T selector, numeric box, slider bounded by §4.7's positivity
thresholds, lock toggle, and the **names of the two elements it acts on**. `+ add` lists the currently
available transformable pairs by element name; `− remove` removes the last. `🔗 link` is §4.8's rule.

Slider behaviour is the part to port carefully: with link on and more than one unlocked transform,
dragging one re-solves the others so the product stays on target, clamping each to its own range and
stopping as soon as the target is met.

### 9.5 Solutions list

Each row: a badge (current ✓ / previously applied / never applied), the transform count, the element
pairs each transform acts on, the Q-adjust value when non-zero, the response type, and **Apply**.
Ordering is by transform count, then by position, then by Q-adjust — the reference implementation's
ordering, which puts the simplest realizable solution first.

### 9.6 Plots

Two `PlotControl`s in rectangular mode. Traces come from an `SNP` built by running `SParameterEngine`
on an elaborated netlist of the **full design** — ladder plus both terminations — with the two port
references set to R1 and R2. Per-port renormalisation goes through `RFNetwork`, and only ever because
the design asks for it: the trace's Z0-override stays off, honouring the rule that a display never
renormalises unbidden.

Default traces: |S11| and |S21| on the first plot; phase and group delay on the second. The plot band
defaults to the design band ±10 % and is user-settable.

### 9.7 Status and refusals

The status strip states Q1, Q2, worst in-band return loss, insertion loss and ripple, and the achieved
vs required Π N². Any refusal — no real root at this order, far end not absorbable, transforms cannot
reach the target — appears **there, with numbers**, and the affected termination turns red. "No
solutions available for order 4" is a sentence the UI must be able to say plainly.

### 9.8 Where it opens from

Double-clicking a `Match` opens the Designer, **not** the 420 px generic parameter dialog. The
Properties-region panel for a selected `Match` shows a compact summary (band, order, response, both
terminations, worst RL) and an **Open Match Designer…** button, following the
`ParameterEditorViewModel.WBond` partial-class pattern; the `Design` blob is never rendered as a text
row.

### 9.9 Exports and settings

The reference implementation's export tab maps onto machinery circuitRF already has, so the Designer
adds buttons rather than formats:

- **Touchstone (.s2p)** of the design response — `TouchstoneIO`, with the per-port references R1/R2
  written as the file's own.
- **Component listing (.csv)** — instance, type, value, unit; the same rows as the grid view.
- **Prototype g-values (.csv)** — for anyone checking the synthesis against a published table.
- **The design summary** is *not* a bespoke PDF generator. §11's Flatten writes a cell whose annotation
  already carries the design record, and the response goes to a Data Display tab like every other
  result — which is where a user would want to plot it against measured data anyway.

`Settings` holds what the reference implementation's settings tab holds and nothing more: display units
per dimension, significant digits, `Qmin` for Q-adjusted solutions (§4.6), and whether to offer
Q-adjusted solutions at all.

---

## 10. Probing the external network for the target terminations

### 10.1 What the button does

Each termination group carries a **Probe** button that looks *outward* from that pin, into the circuit
the `Match` is placed in, and fills in R, reactance kind, topology and value.

1. Extract the enclosing testbench from the live schematic (`NetExtractor.Extract`).
2. In that in-memory copy: **delete the `Match` instance** (so the probe cannot measure itself — with
   the instance gone, the two sides are electrically separate and each probe sees only its own side),
   and attach a `Term` (Num = 1, Z = 50 Ω, ground reference) to the net the probed pin was on.
3. Keep every DC source and bias network. This matters: the interesting case is a transistor, and a
   transistor's small-signal impedance is only meaningful at its operating point.
4. Run `SParameterEngine` over the design band with the Designer's point count. Nonlinear devices are
   linearised at the DC operating point by the existing mechanism; **if the DC solve fails, the probe
   refuses and reports the DC failure** rather than returning an impedance computed at zero bias.
5. `Z(f) = 50·(1+Γ)/(1−Γ)`.

### 10.2 Choosing the topology — fit all five, rank by Γ error

Each candidate is a **linear** least-squares fit in its natural domain, so there is no optimiser and no
starting guess:

| model | fit domain | unknowns (both linear) |
|---|---|---|
| R alone | `Z = R` | R |
| series R+C | `Z = R + (1/C)·(1/jω)` | R, 1/C |
| series R+L | `Z = R + jωL` | R, L |
| parallel R‖C | `Y = G + jωC` | G, C |
| parallel R‖L | `Y = G + (1/L)·(1/jω)` | G, 1/L |

Each fit is then converted **back to Γ over the band** and scored by mean |Γ_model − Γ_measured|. That
single bounded metric ranks all five on equal terms — impedance-domain residuals would over-weight
frequencies where |Z| is large and would not be comparable between the series and parallel forms.

**R alone was added on 2026-08-20, and the reason is that without it the SIMPLEST termination was the
one case the fitter refused.** A bare 50 Ω port is fitted perfectly by all four two-element models —
each returns R = 50 Ω with a residual around 1e-16 — but a linear least squares can only put their
reactance at its degenerate end (C = 0 or ∞, L = 0 or ∞), and every one of those fails the
"reactive value > 0" test below. So a schematic with a plain `Term` on the far pin answered *"none of
the models fits this network"* about a network that every model fits exactly. `ReactanceKind.None` is
first class everywhere else here — it is what `Termination.Resistive` makes and what the Designer's
kind selector shows as "–" — so the fifth model simply says it. It is scored by the same residual as
the rest and wins only when it earns it.

- The best-scoring physical fit is applied: R > 0, and the reactive value > 0 for the four models that
  have one. The R-alone model has no reactance to be positive, which is what makes it applicable
  exactly when the measured impedance is flat.
- **All of them are shown with their residuals**, in Γ units, so the user can take the second-best when
  they know better. The residual is data the user is entitled to see, never a hidden gate.
- If even the best residual exceeds a warning threshold — a setting, **default mean |ΔΓ| > 0.05**
  (§14.5) — the result is still applied but is **flagged**: "the external network is not well described
  by a two-element model over this band." That is the honest answer for a network with a resonance in
  band, and it points the user at narrowing the band or at §5.5's three-element caveat.

### 10.3 Conjugate

A **Conjugate** toggle per side. With it on, the target is Z* — which flips the reactance sign, and
therefore turns a measured parallel-RC into a parallel-RL target. §5 exists so that target can be
expressed.

The Designer states, once, near the toggle: *a conjugate match is the right target for a small-signal
stage and generally the wrong one for a power amplifier's output, where the load should come from
loadpull (Ropt), not from the device's own output impedance.* The owner raised exactly this point about
the interstage example; the tool should say it rather than let the user rediscover it.

### 10.4 When the button is disabled

Greyed out, with a tooltip stating which, when: the pin is unconnected; the pin's net has no component
other than the `Match` itself; the schematic has unresolved errors; or the `Match` is inside a cell
rather than in a testbench (there is no external network to look at from inside a definition).

### 10.5 Provenance

A probed termination records `Source = Probed` and a timestamp, and the field shows a small badge.
Editing the value by hand clears the badge to `Manual`. The user's override always wins and is never
silently re-probed.

**A probed termination is a snapshot, not a live link.** Changing the surrounding circuit does not
invalidate or update it; re-probing is always an explicit action. A live link would mean the network
silently re-synthesising — and therefore changing topology — because someone edited a bias resistor
three components away, which is the same objection §7.5 raises against sweeping a `Match`.

---

## 11. Flatten to Cell

### 11.1 What it produces

A new cell folder — `.ccell`, `schematic/<name>.csch`, `symbol/<name>.csym` — containing:

- **Two `Pin` components**, left and right, matching the `Match` symbol's pins so the cell is
  pin-compatible with the component it replaces.
- **The matching network** as ordinary `L` and `C` instances, placed on a grid: series arms along the
  spine, shunt arms dropping to `Ground`, with wires. Series L+C arms are written as **two components**
  (an `L` and a `C` in series) rather than as one `L` with a `C=` parameter, because the user's next
  action is to edit, sweep or replace individual elements.
- **Both terminations**, written as a `Term` (carrying R) plus the absorbed reactive element, **all with
  `DisableState.Open`** so the netlist ignores them and the cell simulates against whatever it is
  wired into. They are there to record what the network was designed for.
- **A text annotation** listing the design: band, order, response, both terminations, achieved worst
  return loss, insertion loss and ripple, Π N², and the date.
- **The `Design` blob**, carried onto the cell so `Re-open in Match Designer…` can reconstruct the
  original design later. A flattened cell that has forgotten what it was is a dead end.

The symbol is a **copy of the `Match` symbol** (the bandpass glyph), written to the cell's `.csym`.

### 11.2 Replacing in place

A checkbox, on by default: *Replace MN1 with an instance of the new cell.* Since the symbol and pin
positions are identical, the wires stay connected and the schematic is immediately runnable. The whole
operation — create cell, write files, replace instance — is one composite undoable command on the
owning schematic's stack, with the file writes going through the existing save-plan machinery so a
half-written cell is not left behind on failure.

### 11.3 Why the terminations are disabled rather than omitted

Two reasons, both practical. Omitted, the design intent is lost the moment the cell is opened. Enabled,
the cell short-circuits its own ports when placed. Disabled, the cell simulates correctly against the
real circuit **and** a user who wants to reproduce the Designer's plot can enable the two `Term`s and
run an S-parameter analysis on the cell alone — which is exactly the check anyone would want to do
after flattening.

---

## 12. Files, projects, layering

```
  src/Core/Match/
      MatchDesign.cs            the serializable design + Termination
      MatchEmbedding.cs         base64 JSON codec (mirrors WBondEmbedding)
      MatchSynthesis.cs         §4.3 closed forms, §4.4 bandpass transform, §4.5 excess element
      MatchPrototypes.cs        §6.2 general route: factorization + Cauer extraction
      NortonTransform.cs        §4.7 forms, thresholds, propagation, pair discovery, conflicts
      MatchLadder.cs            element list, netlist assembly, absorbed-element marking
      MatchSolutionSearch.cs    §4.8 candidate enumeration and ranking
  src/Core/Devices/MatchModel.cs        + a factory entry under _parameterizedTypes
  src/Engine/Match/TerminationProbe.cs  §10 (needs SParameterEngine, so it cannot live in Core)
  src/Ui/Match/                          designer view-models, ladder layout, plot assembly
  src/Ui/Views/Match/                    MatchDesignerWindow.axaml and its panes
  src/Ui/ViewModels/ParameterEditorViewModel.Match.cs   the Properties-region panel
  src/Ui/Schematic/MatchPlacement.cs     placement defaults
  tests/Core.Tests/Match*                synthesis, prototypes, transforms, serialization
  tests/Engine.Tests/Match*              stamping, DC/HB, the probe
  tests/Ui.Tests/Match*                  designer view-model, flatten, symbol
```

**No new project.** `wBond` earned one because it is a large numeric library with a standalone
application; `Match` is a few hundred lines of closed-form algebra with no UI and no separate app, so it
belongs in `src/Core` alongside the rest of the design layer. The firewall is unaffected: nothing under
`src/Core/Match` may reference Avalonia, and `tests/Firewall.Tests` already enforces that.

---

## 13. Testing and acceptance

### 13.1 Golden values (from §4.9)

- `MatchSynthesis` at n = 2/4/6 for the documented example reproduces the prototype g-values to 1e-5.
- The n = 4 ladder reproduces the element values in §4.9 to 1e-5, and its analysis-end absorbed element
  equals the stated 10.0000 pF **exactly** (to 1e-12 relative) — the absorption identity.
- Simulated over 3.3–5.0 GHz: worst |S11| = −16.663 dB ± 0.02, insertion-loss ripple = 0.0361 dB ± 0.002.

### 13.2 The invariants worth a test each

| Test | Why |
|---|---|
| **Transform invariance** — applying any Norton transform leaves S11 and S21 unchanged to 1e-9 | this is the entire premise of the transform rack; if it ever fails, the tool is lying |
| **Absorption duality (§5)** — the same problem with the series 1.25 Ω + 10 pF replaced by 1.25 Ω + 153.517 pH produces an identical element list and identical S-parameters | pins §5's exactness claim |
| **Positivity** — across a sweep of terminations/orders, no solution ever presents a negative element unless *Allow negative components* is on | the slider bounds are the only thing enforcing this |
| **Component ≡ flattened cell** — a `Match` and the cell its Flatten produces give identical S-parameters to 1e-12 | §8.3's whole justification |
| **Absorbed elements are not stamped** — a `Match` in a 50 Ω testbench does *not* contain the termination reactances | §8.2's easy-to-invert mistake |
| **DC and HB** — a `Match` in a DC analysis presents series arms as opens and shunt arms as shorts; an HB run including the DC harmonic converges | §8.3's reason for elementwise stamping |
| **Numerical ≡ closed form (§6.2)** — the general extractor reproduces the Chebyshev closed form to 0.5 % | licenses the Butterworth and Bessel answers |
| **Feasibility gating** — Bessel at n = 4 on §4.9's problem is refused, with the far-end Q named in the message | §6.4 |
| **Probe round-trip** — a schematic containing a bare 200 Ω ‖ 0.125 pF probes back to within 0.1 % and ranks *parallel RC* first | §10.2 |
| **Probe conjugate** — the same network with Conjugate on returns a parallel **RL** target | §5.4, the reason §5 exists |
| **Order parity** — a mixed series/parallel pair never offers an odd order, and switching topology adjusts the order rather than leaving an unsatisfiable one | §4.2 |
| **Design blob round-trip** — encode → decode → re-synthesise gives an identical ladder | §7.1's derive-don't-store choice |
| **Session round-trip** — a design with two transforms (one π, one T, one locked, link on, a Q-adjusted solution applied) saved, reloaded and rebuilt gives identical element values, identical N's, identical lock/link state and identical S-parameters | §7.3 — this is the "everything I set is still there" guarantee |
| **Transform pairs survive a basis change** — perturb the synthesis output slightly and confirm the name-keyed pairs still resolve, the fingerprint mismatch is reported, and no transform silently re-points | §7.3's trap, the one that would fail silently |
| **Corrupt blob** — refuses at elaboration naming the instance, never silently substitutes a default | §7.2 |

### 13.3 Cost

All of the above are milliseconds-to-seconds; **none belongs in the `Category=Benchmark` tier.** The one
to watch is the solution search at n = 6 with all transform combinations enumerated — measure it, and if
it crosses ~5 s, tag that test and cache the search in the Designer rather than re-running it per
keystroke (the reference implementation re-runs it inside a view body, which is affordable on a
four-element network and would not be here).

---

## 14. Owner decisions — resolved 2026-08-19

1. **Bessel — ship it**, gated by the same feasibility test every other response passes through (§6.4).
   It will be refused more often than not, and the refusal must name the numbers.
2. **No nearest-standard-value column** (§9.3). Realizability is the user's call and is
   flow-dependent — an MMIC capacitor is designed to its value, so an E-series is not merely unhelpful
   there but wrong.
3. **Palette category `Matching`** — a new `ComponentCategory` (§8.4), with search terms *impedance
   matching, filter, filter design, transform, Chebyshev, Butterworth, Bessel*.
4. **`.match` standalone export/import — deferred** (§7.4). The design travels inside `.csch` and, once
   flattened, inside an ordinary cell; a separate interchange file waits until someone actually needs
   to move a design between workspaces.
5. **Probe residual threshold — not yet decided, and it does not block the build** (§10.2). The
   residual is *always displayed*, so the threshold only controls when a warning is added; the feature
   is usable and honest at any setting. Build it as a setting with a **0.05 mean |ΔΓ| default** and
   calibrate it against real device networks once they are in hand. For scale: a 0.05 error in Γ is
   about the difference between a −20 dB and a −16.5 dB match, i.e. enough to matter but not enough to
   invalidate a design. Recorded here as a **calibration task, not a design unknown** — it must not
   quietly become permanent by being forgotten.
6. **Default analysis end = the higher-Q end**, exactly as the reference implementation does (§4.2),
   overridable to either termination.

## 15. What this document deliberately does not decide

- The exact AXAML of the Designer, beyond the pane structure in §9.1. Styling follows the existing
  dialogs.
- Whether the Designer is one window per instance or a single reused window. Either works; it is a host
  flag, decided by using it.
- Any change to `SParameterEngine`, `PlotControl`, `NetExtractor` or the elaborator beyond the one
  internal-node mint in §8.3, which follows three existing precedents exactly.

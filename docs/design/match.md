# circuitRF — Match: the absorbing impedance-matching component

**Status:** Proposal — rev 3 · **Date:** 2026-08-19, rev 2 2026-08-28 (§6.8–§6.9, §16–§17: response
families excluded, family naming, lowpass/highpass forms), rev 3 2026-08-28 (§18–§19: multiband) ·
**Phase:** MN0 (design layer); the lowpass/highpass form is **MN-LP**
(`docs/sonnet-briefs/brief-match-lowpass-highpass-form.md`); dual-band is **MN-MB1** and tri-band +
Remez is **MN-MB2** (`docs/sonnet-briefs/brief-match-multiband-{dual,tri-remez}.md`)

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
  **Bessel** where feasible (§6). **Elliptic and inverse Chebyshev are excluded, with reasons** (§6.8).
- **Multiband — dual and tri-band** (§18, rev 3): the same bandpass ladder matched over two or three
  bands at once, geometrically mirrored about the gap centre, with the gap deliberately unmatched.
- **Three network forms — bandpass, lowpass and highpass** (§16, rev 2). Bandpass is §4's synthesis;
  lowpass and highpass are ladders of single elements matched between F1 and F2, with their own
  absorption rules and no Norton degrees of freedom.
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
  Response:  ( ) Chebyshev — single-match (optimum)   [default]
             ( ) Chebyshev — double-match (exact)
             ( ) Butterworth
             ( ) Bessel                                [disabled with a reason when infeasible]
             Ripple [ 0.10 ] dB                        [enabled only when neither end has a reactance]
```

(The names are §6.9's; the first two were "Fano optimum" and "both ends prescribed" until rev 2.)
**Double-match** is §4.3's two-ended Levy prototype: it absorbs both terminations exactly and
never needs §4.5's excess element, at the cost of Fano optimality. It is the reference implementation's
"Fano Optimum" toggle in its off position, and it is the right choice when a surplus element is
unwelcome. Changing the selector re-runs the solution search and repopulates the solutions list. A response that cannot
absorb both ends at the current order is shown disabled with the numeric reason in its tooltip, never
silently missing.

*Since the Solutions-panel round (2026-08-28) the Response and Order selectors live in the panel's
filter rather than the specification pane — the search runs the whole cross-product and the user picks a
network by looking at networks. §16.7 adds the network form to that filter.*

### 6.7 Numerical robustness

The §6.2 route is polynomial root-finding on degree-2n polynomials, which is well conditioned for
n ≤ 6 but not free: the extraction must trim leading coefficients **by tolerance relative to the
polynomial's scale**, not by exact zero, or a 1e-16 residue makes a degree-3 polynomial look
degree-4 and the extraction silently fails. (This is not hypothetical — it is what happened first while
verifying §6.3.) The Chebyshev path keeps its closed form and is not routed through the numerical
extractor; the numerical extractor's agreement with it is a permanent regression test (§13).

---

### 6.8 Excluded, with reasons — elliptic and inverse Chebyshev (rev 2, 2026-08-28)

**The owner asked whether elliptic (Cauer) and inverse-Chebyshev (Chebyshev type II) responses could be
added, on the condition that the transformations actually deliver the target match. They cannot, and
the reason is structural rather than numerical — so both are excluded rather than deferred.**

Both families place **finite transmission zeros**. Inverse Chebyshev is the elliptic family with the
passband ripple taken to zero (`|H|² = 1/(1 + 1/(ε²T_n²(ω_s/ω)))`, equiripple in the *stopband*), so
the two stand or fall together:

1. **The ladder cannot express them.** Everything in Match is an *all-pole* ladder: §4.4 alternates
   series and shunt arms, each arm one L and one C resonant at ω₀, and `MatchNetwork` is a flat list in
   which every element is either in the through path or from the current node to ground. A finite
   transmission zero needs a **series-LC branch to ground** (or a parallel LC in the through path) — a
   branch with an internal node — which that list has no way to say. The ABCD evaluator, the Norton pair
   scan (which relies on strict L/C alternation), the ladder drawing, Flatten and the stamp all inherit
   the same assumption. After the bandpass transformation an elliptic branch is four elements to ground,
   and a 4th-order elliptic bandpass match is ~14 parts against 8.
2. **The synthesis is a different algorithm.** There is no Levy-style g recursion for either family, and
   §6.2's Cauer continued fraction cannot place a finite zero — that needs Darlington zero-shifting
   extraction (partial pole removal), a fussier procedure than anything in §6.
3. **What it would buy a matching network is marginal.** Fano's integral is set by the load; a steeper
   skirt recovers only a sliver of in-band return loss, at the cost of tolerance-sensitive resonators.
   Even-order elliptic and inverse Chebyshev also have a non-zero floor at ∞, which is incompatible with
   a shunt-capacitive termination outright.
4. **In-band, inverse Chebyshev offers nothing Butterworth does not** — both are monotone in the
   passband — at nearly double the part count.

Norton transforms would remain *valid* on such a network in principle (the N² scaling side would simply
include the resonator branch), but §4.7's pair discovery and the response-preserving list-swap rule
would both have to be rewritten. None of this is worth it for a response the user would rarely choose.

If a user actually needs a notch — a harmonic trap in a PA output match — that is a separate *"add a
transmission zero at f_z"* feature on the finished ladder, not a response family, and it is not designed
here.

**The one family that would slot into §6.2 unchanged**, should more choice ever be wanted, is
**Legendre / Papoulis "optimum L"** — monotone passband with the steepest all-pole roll-off, sitting
between Butterworth and Chebyshev. It is a different `F` in the same two-parameter family and needs
nothing else. Not recommended now; recorded so the cheap option is known.

### 6.9 Naming the two Chebyshev families (rev 2, 2026-08-28)

**The owner observed that users cannot tell "Chebyshev (2-ended)" from "Chebyshev (Fano)" by name, and
asked what the RF world calls them — and whether "Chebyshev (Levy)" / "Chebyshev (Dawson)" would do.**

What the user chooses by is the **outcome**: the Fano form gives the best return loss and may add a
surplus element at the far end (§4.5); the two-ended form absorbs both ends exactly, adds nothing, and
gives up a little return loss. Neither current name says that.

- **The field's own vocabulary** is the broadband-matching literature's (Youla, Carlin, Yarman, Chen):
  the **single-matching** problem — one reactive termination prescribed, the other resistive — and the
  **double-matching** problem — both prescribed. That is precisely the distinction between §4.3's two
  prototypes: the Fano form prescribes one end (and reconciles the other with an element), the two-ended
  form prescribes both. Matthaei's terms ("prescribed load decrement", "interstage") do not separate
  them, since this document's own interstage example uses the Fano form.
- **Levy/Dawson is defensible but slightly unfair.** Both prototypes run *Levy's* 1964 recursion; Dawson's
  2009 contribution is the closed-form *optimum* root (§4.3's `P_n(c)` table). So "Levy" for the two-ended
  form is right, and "Dawson" for the Fano form credits the optimisation rather than the network. Fano's
  name belongs on the bound, not on a network. More to the point, an eponym does not help a user choose
  — it swaps one opaque tag for another (Butterworth gets away with it only through ninety years of
  familiarity).

**Recommendation — descriptive primary, eponym in the tooltip and the user reference:**

| Until rev 2 | Selector | Card / footer / filter | Tooltip lead |
|---|---|---|---|
| Chebyshev — Fano optimum | **Chebyshev — single-match (optimum)** | Chebyshev (single-match) | Best return loss at this order; may add a surplus element at the far end. Levy's recursion with Dawson's optimum root. |
| Chebyshev — both ends prescribed | **Chebyshev — double-match (exact)** | Chebyshev (double-match) | Absorbs both terminations exactly and never adds an element; slightly lower return loss. Levy 1964. |

The enum members `ChebyshevFano` / `ChebyshevTwoEnded` and the serialized spelling stay as they are —
this is a display change, and a renamed enum value would break every saved design for no gain. The
user reference (`docs/user/src/reference/match.md`) still calls the second option "fixed ripple", which
was never what it is; it changes with the labels.

## 7. Data model and persistence

### 7.1 `MatchDesign`

One serializable object, the single source of truth:

```
  MatchDesign
    Version                       int
    F1, F2                        double, Hz
    Order                         int  (2…6)   — n in-band match points; 2n elements in every form (§16.2)
    Form                          enum { Bandpass, Lowpass, Highpass }   (rev 2, §16; default Bandpass, additive)
    BandCount                     int  (1, 2 or 3; rev 3, §18.8; default 1, additive)
    F3, F4, F5, F6                double, Hz  (the second and third bands; 0 when unused; additive)
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
- **`F1`, `F2`, `Order`, `Response`, `Form`, `Bands`, `F3`-`F6`, `R1`, `R2`** — *echo* parameters,
  written **only** by the Designer, existing so the design can be READ: they are what makes the `.cnl`
  line legible where the payload is still a base64 token, and `Form` and `Bands` are what the symbol
  draws itself from (§8.4). This mirrors `wBond`'s `Arrays`: bookkeeping maintained by the editor,
  never a second input.
- **A `Match` exposes NO parameter to the schematic, and offers no generic parameter row** (owner,
  2026-08-28). `F1`, `F2` and `Order` used to be drawn beside the symbol; what they were saying —
  which band, how big — the glyph now says itself, and the compact panel of §9.8 says in words. Every
  default is `ShowOnSchematic: false`, `EditableComponent.LabelParameters` enforces the same on an
  instance placed before the change (whose file still says true), and `IsMatchPanelParameter` covers
  every parameter the registry declares, so the Designer is the only place a Match is edited at all.
  That also closes the one way the glyph could have been made to lie: nobody can type `Form=Lowpass`
  beside a bandpass ladder.
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

A square body carrying the standard filter glyph: **three stacked full-cycle sine waves**, with a
**slash struck through every wave the network blocks**. Built from `SinePrimitive`s (`Cycles = 1`,
`Axis = Horizontal`) plus `LinePrimitive` slashes and a `RectPrimitive` body, with pins left and
right. The sine primitive already exists and already rotates and scales correctly.

**The glyph follows the design** (owner, 2026-08-28). The three waves read as a frequency axis with
the highest at the top, so which two carry a slash says what the network is:

| Design | Slashes | Reads as |
|---|---|---|
| Bandpass | top and bottom | the middle band passes |
| Lowpass | the top **two** | only the lowest band passes |
| Highpass | the bottom **two** | only the highest band passes |
| Dual-band | two smaller bandpass stacks, side by side | two passbands |
| Tri-band | three smaller bandpass stacks, two below one | three passbands |

A multiband design (§18) is bandpass in every one of its bands, so its form is not the question its
glyph has to answer — *how many passbands* is, and one stack of three waves cannot say. It is drawn
as one smaller stack per band instead, inside the same body, at the same two pins.

`BuiltInSymbols.PrimitivesForMatch(form, bandCount)` builds and caches the variants, and
`EditableComponent` selects one per instance exactly as SnP does for `RefNode`/`PinConfig`/`Pitch` and
the Tuner family does for `ShowBias`. It reads the `Form` and `Bands` **echo** parameters (§7.2), not
the `Design` payload: this runs on every model rebuild for every component, and base64-decoding a JSON
document to choose a glyph is not what that path is for. The echo does not become a second input by
being read here — the engine still reads the payload and only the payload, so the glyph can no more
change the circuit than the old text label could.

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

The symbol is a **copy of the `Match` symbol** — the glyph that instance was wearing, form and band
count included (§8.4) — written to the cell's `.csym`.

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
| **Lowpass-form DC pin (§16.3)** — a real-to-real lowpass design's R_far equals the target to 1e-9 relative with *no* transforms, and the closed form `worst|Γ|² = Γ₀²/(Γ₀² + T_n²(x₀)(1−Γ₀²))` predicts the simulated worst in-band return loss to 0.05 dB | the ratio is pinned by transparency at DC; the closed form is an oracle the code does not contain |
| **Lowpass-form ≡ classical Chebyshev at F1 = 0** — order n at F1 = 0 with R_far/R_ana = coth²(B/4) reproduces `RippleG(2n, ripple)` to 1e-6 | §16.3's family reduces to the textbook prototype, which pins the frequency mapping |
| **Lowpass/highpass absorption identity** — the end element equals the termination's own value exactly; a load below the family's smallest end element gets an added element of the same kind, a load above its largest is refused naming both numbers | §16.4 |
| **No Norton pairs in a single-element ladder** — `NortonTransform.Discover` returns nothing for every lowpass/highpass basis, and `RequiredTransformRatio` is 1 | §16.5; this must fall out of the existing scan, not be special-cased |
| **Highpass is the dual** — a highpass design over [F1, F2] has the lowpass design's g-values over [1/F2, 1/F1] with L↔C, and the same worst return loss | §16.6 |
| **Old blobs decode as bandpass** — a `Design` payload written before `Form` existed rebuilds to the identical ladder | §16.8 |
| **Dual-band golden member (§18.4)** — `GvaluesAt(ChebyshevFano, 2, 0.7261974, 3.654984e-5, 6.255720e-4)` reproduces the stated g to 1e-6 and, resonated, the stated pH/pF to 1e-6 relative; the ABCD oracle gives −31.79 dB worst in both effective bands and a gap maximum of 0.445 ± 0.002 | rev 3; the whole mechanism in one number |
| **Dual-band beats single-band at equal count** — for §18.4's problem, the dual-band n = 2 design's worst in-band \|S11\| is at least 10 dB better than `FanoG(4, Q, w)`'s over the same two bands | the reason the feature exists, pinned |
| **Symmetrisation** — 2.4–2.5 / 5.15–5.85 GHz widens band 1 to 2.2009–2.5 (to 1e-6 relative), never band 2, and bands that already mirror are untouched; the mirror identity f1·f4 = f2·f3 holds to 1e-12 on the effective bands | §18.3, the one rule that changes a user's spec |
| **Absorption identity, dual-band** — the first shunt C equals the termination's own 2.5 pF to 1e-12 relative and carries `AbsorbedEnd`; a far-end surplus is an `IsExcess` element; a like-topology pair is refused naming parity | §18.2 |
| **Norton invariance and reach, dual-band** — every discovered transform leaves S11/S21 unchanged to 1e-9, and the solution search reaches R2 = 50 Ω from R_far = 7.6746 Ω (Π N² = 6.515) | §18.2 — the transforms are the unchanged machinery, and this proves it on the new ladder |
| **Old blobs decode as single-band** — a payload without `BandCount` rebuilds to the identical ladder | §18.8 |
| **Remez ≡ the closed forms** — the exchange on one interval `[a², 1]` reproduces `T_n(x(u))` to 4e-16 relative for n = 1…6 and a ∈ {0, 0.5, 0.73}, and on two equal-length intervals reproduces `T_m(q(u))` to 2e-15 | rev 4, §18.5; the alternation theorem says the answer is unique, so reproducing the two cases that HAVE a formula is the statement that it found the answer rather than an answer |
| **Equioscillation on a union** — on every band set with no closed form, exactly n + 1 alternating extrema of magnitude 1 ± 1e-9 | rev 4, §18.5; the defining property used as the oracle, which is the only one available there |
| **General polynomial ≡ closed form** — `GvaluesAtPolynomial` and `GvaluesAt` agree over MN-LP's whole 360-cell sweep: all 360 extract, g to 1e-9 at n ≤ 4, and the two ladders' response to 1e-3 dB at n = 5, 6 | rev 4, §18.9; the g-vectors diverge to 7e-5 at order 6 and cannot do better — the roots agree to 2e-16 and the extraction amplifies by 1e11. See RESOLVED §MN-MB2 |
| **Tri-band golden members (§18.5)** — 0.5–0.6 / 0.9–1.1 / 1.65–1.98 GHz into 50 Ω ‖ 4 pF gives −8.941 / −11.997 / −14.473 dB at 4 / 8 / 12 elements with R_far 23.679 / 29.918 / 34.107 Ω, and all three bands come out equal to 0.01 dB | rev 4, §18.5; the note's own targets, and the equiripple property surviving the resonating transform |
| **Mirroring never reaches the middle band** — over a wide random sweep of ordered six-frequency specs, `Overlaps` is never true and the union in u is always two disjoint intervals inside [0, 1] | rev 4, §18.3; this is a theorem (`f₀²/f₅ < f₃`), so the test asserts it rather than producing a refusal |
| **Odd counts absorb a like pair** — a parallel/parallel pair gives 2n + 1 elements in lowpass form and 4n + 2 in multiband, both ends carrying `AbsorbedEnd`, both shunt, each at least the termination's own value | rev 4, §16.4/§18.2; the gap those sections recorded, closed |
| **The odd rung sits under the even one** — at order n the odd member is 0.002 dB worse than the even member at the same n and better than the even member below it | rev 4, §16.3 corrected; the odd count buys absorption, never return loss |
| **The odd terminating value is a conductance ratio** — inverting the far port on an odd ladder costs more than 5 dB with every element value unchanged | rev 4; the parity trap, kept as a gate because the failure is a plausible network with a hopeless response |

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

---

## 16. Network form — bandpass, lowpass, highpass (rev 2, 2026-08-28)

**The owner asked whether lowpass and highpass solutions would work, whether they could serve as the
absorption method, and whether they have value even if only for real-to-real transforms. Answer: yes,
with one real limitation each, and a value proposition that is different from the one first
guessed.** Everything below was checked numerically while writing it (spectral factorisation + Cauer
extraction of the stated family, with an independent ABCD sweep against the closed form); the numbers
are quoted, and they become golden values in §13.2.

### 16.1 Two facts that decide the design

1. **A lowpass ladder is transparent at DC.** Every shunt C is open and every series L is short, so
   `|Γ(0)| = |(R₂ − R₁)/(R₂ + R₁)|` whatever the loads' reactances (they vanish at DC too). A highpass
   ladder is transparent at ∞ in the same way. Two consequences:
   - **The impedance ratio is paid for out of the Fano budget.** In bandpass, the prototype's ratio
     `g_{n+1}` is whatever it is and a Norton transform reaches the real ratio *without changing the
     response* (§4.7) — the ratio is free. In lowpass form there is nothing to absorb a transformer
     into, and the DC value is part of the response.
   - **A "band from DC" lowpass is not a useful product feature.** With DC in the band, `|Γ(0)|` is the
     in-band floor: an even-order 0.1 dB Chebyshev transforms only `coth²(B/4) = 1.354 : 1`, and
     50 → 5 Ω from DC is stuck at |Γ| = 0.82, i.e. **−1.7 dB** (4 elements, verified: −1.74 dB). This is
     the textbook case — the one Fano's own paper solves — and it is the wrong thing to offer.
2. **A ladder of single elements has no Norton pairs.** A Norton transform needs a series/shunt pair
   of *like kind* (§4.7); in a lowpass ladder every series element is an L and every shunt element a C.
   §4.7's scan finds nothing — correctly, and without a special case. So the transform rack is empty,
   the linkage rule is vacuous, and every element value is what the prototype says.

What practitioners mean by a "lowpass matching network" is therefore a **lowpass-form ladder matched
between F1 and F2**, with the mismatch allowed to rise below F1 and the ratio pinned at DC. That is the
feature. F1 = 0 (F2 = ∞ for highpass) is permitted and degenerates to the classical case, carrying its
own penalty honestly.

### 16.2 What it buys, and what it does not — the corrected value proposition

- **Not fewer parts.** An n-match-point lowpass-form network has **2n elements — the same count as
  bandpass order n**. (A ladder of m elements has m/2 in-band match points in any form.) "Order" keeps
  its meaning — n in-band match points — and the element count is 2n in every form, so the order picker
  and the parity language do not change.
- **Tame element values at wide bandwidth.** The bandpass transformation at `w ≳ 1` (an octave and
  more) produces resonators whose L and C values are spread over decades; the lowpass form has no
  resonators and its values stay within a factor of a few of each other. This is the case wideband PA
  output and LNA input matches actually live in, and it is where the form earns its place.
- **A DC path (lowpass) or a DC block (highpass) for free**, which is what a bias network wants.
- **No selectivity** beyond the roll-off, and **no rejection below F1** (lowpass) or above F2 (highpass).
- **The ratio costs return loss.** From the closed form of §16.3, Chebyshev, 2n elements, real-to-real,
  worst in-band return loss (a = F1/F2; verified by simulation to 0.01 dB in every cell):

  | a = F1/F2 | elements | r = 2 | r = 10 |
  |---|---|---|---|
  | 0.33 (3 : 1) | 4 | −15.6 dB | −5.0 dB |
  | 0.33 | 6 | −21.1 dB | −9.5 dB |
  | 0.50 (2 : 1) | 4 | −22.2 dB | −10.5 dB |
  | 0.50 | 6 | −31.7 dB | −19.6 dB |
  | 0.66 (3.3–5 GHz) | 4 | −30.6 dB | −18.5 dB |
  | 0.66 | 6 | −44.3 dB | −32.2 dB |

  Compare bandpass order 2 (4 elements), real-to-real at 0.1 dB ripple: −16.4 dB at **any** ratio the
  Norton ranges can reach. The lowpass form wins comfortably at a = 0.66 and loses at a = 0.33, r = 10;
  the Solutions panel lists both and the user sees the numbers.
- **Butterworth in lowpass form** exists (shifted `F = xⁿ`, §16.3) and is 4–5 dB worse than Chebyshev at
  the same count: a = 0.5, r = 10 → −6.8 dB (4 el.), −10.6 dB (6 el.); a = 0.66, 4 el. → −13.4 dB against
  Chebyshev's −18.5 dB. **Bessel is not offered in lowpass or highpass form** — with the DC pin the
  family has no free parameter left, and a delay-flat network matched over a band that excludes DC is
  not a defined thing. **The double-match Chebyshev is not offered either**: with the ratio pinned there
  is one free parameter, not two, so both end Q's cannot be prescribed; §16.4's rule covers what the
  form can do.

### 16.3 The lowpass-form prototype

Normalise to ω_c = 2πF2 and the analysis-end resistance. With `u = ω²`, `a = F1/F2`, map the band onto
[−1, 1]:

```
  x(u) = (2u − 1 − a²) / (1 − a²)                       x(a²) = −1,  x(1) = +1,  x₀ = x(0) = −(1 + a²)/(1 − a²)
  Φ(u) = T_n(x(u))²      (Chebyshev)     or     x(u)^{2n}   (Butterworth)
  |Γ(jω)|² = (K + ε²Φ) / (1 + ε²Φ)
```

`Φ` is degree n in u, so `|Γ|²` is degree 2n in u and the Hurwitz factor is degree 2n in s — **2n
elements**, all-pole (`|S21|² = (1 − K)/(1 + ε²Φ)`, constant numerator, every transmission zero at ∞), so
§6.2's spectral factorisation and Cauer extraction apply unchanged. At a = 0, `x(u) = 2ω² − 1 = T₂(ω)` and
`T_n(T₂(ω)) = T_{2n}(ω)`: **the family reduces exactly to the classical Chebyshev lowpass of order 2n**,
which is §13.2's cross-check against `RippleG`.

**The DC pin** is one equation in (ε, K):

```
  (K + ε²Φ(0)) / (1 + ε²Φ(0)) = Γ₀²,      Γ₀ = (r − 1)/(r + 1),   r = R_far / R_ana,   Φ(0) = T_n(x₀)²
```

so the family has **one free parameter**, K. For a real-to-real design the worst in-band `|Γ|²` is
`(K + ε²)/(1 + ε²)` and is **monotone increasing in K**, so the optimum is K → 0 and the answer is closed
form:

```
  worst |Γ|²  =  Γ₀² / ( Γ₀² + T_n(x₀)² · (1 − Γ₀²) )
```

This is the oracle in §13.2 — the implementation does not contain it, and the table in §16.2 is it.

**Which orientation the ladder starts with** is chosen by the sign of Γ in §6.2 step 3, exactly as
today: shunt-first for a parallel analysis end, series-first for a series one, shunt-first by default
for a resistive one. The extracted `g_{2n+1}` is the resistance ratio (or its inverse for a shunt last
element, the same convention `Build` uses today) and comes out **equal to the target** by construction
— which is the DC pin seen from the other side, and is what makes §16.5 true.

**Only even element counts have this closed form.** An odd-count ladder between unequal resistances
needs `Φ(u) = (u + u_r)·R_k(u)²` — a *weighted* Chebyshev polynomial with one more free parameter,
solvable by a Remez exchange but not by a formula. The consequence for absorption is in §16.4.

> **Built in MN-MB2 (rev 4, 2026-08-28), and the "best member" sentence above was the wrong way
> round.** `Φ(0) = u_r · R_k(0)²` is what the DC pin is written against, and it rises *monotonically*
> in u_r toward the even family's own `T_k(x₀)²` without ever reaching it — so the odd member at order
> k sits **0.002 dB under the even member at the same k** (measured, a = 0.5, r = 5: −14.303 against
> −14.304 dB at 5 against 4 elements) and comfortably above the even member one order below. The odd
> count is therefore **not a finer grain of performance; it is the only ladder whose two ends share an
> orientation**, which is what absorbs a like termination pair. Parity is decided by the
> TERMINATIONS, never by the user: order still means in-band match points, and the element count is
> 2n or 2n + 1 according to `MatchOrders.NeedsOddCount`. The full measurement is in
> `src/Core/Match/RESOLVED.md` §MN-MB2, including the parity trap in the terminating value.

### 16.4 Absorption in lowpass and highpass form

The prototype stage was always lowpass — Levy's recursions are lowpass formulas and the bandpass step is
the resonating transformation. So absorption works the same way, with three differences:

1. **Each form absorbs half of the termination kinds.** A lowpass ladder has only C to ground and L in
   the through path, so it absorbs **R ‖ C** and **R + L**; highpass absorbs **R ‖ L** and **R + C**. The
   other two are **refusals**, and §5's `C_eq` trick cannot rescue them — it depends on each arm having
   both an L and a C, which is exactly what these forms lack. The refusal names the kind: *"A parallel
   R ‖ L cannot be absorbed by a lowpass network — there is no shunt inductor to absorb it into. Use
   highpass or bandpass form."*
2. **The end elements are of opposite orientation for an even count and the SAME orientation for an
   odd one**, so a mixed pair takes 2n elements and a like pair (shunt C to shunt C, the classic
   interstage) takes 2n + 1. **Both are offered since MN-MB2** — `MatchOrders.ValidOrders` returns
   2…6 for either pair and `NeedsOddCount` decides the parity — and the v1 refusal this paragraph used
   to describe is gone. One constraint replaces it: an odd ladder's two ends flip together, and a
   shunt-ended one steps DOWN, so **a like pair of parallel ends must be analysed from the higher
   resistance and a like pair of series ends from the lower.** The other way round is a refusal naming
   the analysis end rather than the form. The common single-ended cases — a transistor's shunt-C
   output into a 50 Ω load, a series-L drain feed — are unaffected and still take the even count.
3. **K is chosen by both ends at once, and it is a 1-D monotone problem.** With the DC pin used, the
   one free parameter moves the two end elements in opposite directions (verified, a = 0.5, 4 elements,
   r = 10: K from 0 to 0.6 takes `g₁` from 2.49 to 10.6 and `g_far` from 0.248 to 0.069, and the worst
   return loss from −10.5 to −2.2 dB). The rule, at each reactive end, is §4.5's: **synthesised ≥ actual
   → the surplus is a real added element of the same kind and topology; synthesised < actual → not
   absorbable.** Since return loss worsens with K, the design is the **smallest K** whose end elements
   are both ≥ their terminations; the feasible interval is `[K_min from the near end, K_max from the
   far end]`, and an empty interval is a refusal that names both numbers. There is no Fano root to find
   and no §4.6 Q-adjust — the added element at the near end *is* the Q-adjust, produced by the same rule,
   so `QAdjust` is ignored in these forms and Q-adjusted solutions are not searched for.

The absorbed element carries `AbsorbedEnd` exactly as in bandpass and is not stamped; the excess element
carries `IsExcess`; Flatten and the stamp need nothing new.

### 16.5 Norton transforms, the linkage rule and the solutions list

`NortonTransform.Discover` finds no pairs in a single-element ladder because like-kind elements are
never of opposite orientation — this must **fall out of the existing scan**, and a test asserts that it
does. `RequiredTransformRatio` is 1 by the DC pin, so the linkage rule is satisfied with no transforms
and nothing turns red. The transform rack shows its empty state with one line — *"Lowpass and highpass
networks have no Norton pairs: every value is the prototype's."* — rather than an empty list that looks
broken. Each (form, order, family) cell therefore contributes **exactly one solution**, with zero
transforms; the solutions list sorts it where zero transforms sort today, ahead of the bandpass rows
of the same order.

### 16.6 Highpass is the dual, not a second implementation

Design the lowpass form over `[1/F2, 1/F1]` (with `F2 = ∞` allowed) and apply `s → 1/s`: every L becomes
a C and every C an L, `C_hp = 1/(g·ω_ref·R)` and `L_hp = R/(g·ω_ref)` with `ω_ref = 2πF1`. The ratio is
pinned at ∞ instead of DC, the absorbable kinds swap (§16.4), and the worst return loss is identical.
One code path with a mirror, and §13.2's duality test holds it.

### 16.7 What the user sees

- The Solutions panel's filter gains a **Form** group — `Bandpass`, `Lowpass`, `Highpass`, all on by
  default — above the Order group, and the search cross-product becomes (form × order × family): 20
  bandpass cells as today plus 10 lowpass and 10 highpass (two families each). Lowpass/highpass cells
  are cheap — a bracketed 1-D solve in K, no shape sweep — and the design's own form is searched first.
- A card reads **"Chebyshev · lowpass · order 4"**; bandpass cards gain the word too, so the three read
  alike. The footer and the Properties-panel summary carry the form.
- The design's `Form` is set by **applying a solution**, as Order and Response are today; the
  specification pane does not grow a selector.
- The ladder preview draws single-element arms with the same symbols and roles; the response plots are
  unchanged, and the plot band default (design band ±10 %) is unchanged even though a lowpass network's
  response continues to DC — the user can widen it.
- Refusals name the form and the remedy, as §4.5's do.

### 16.8 Persistence

`MatchDesign.Form` is an **additive** field, default `Bandpass`, omitted from nothing and read as
`Bandpass` when absent — a payload written before rev 2 rebuilds to the identical ladder, and `Version`
stays 1. The `Form` echo parameter joins `F1, F2, Order, Response, R1, R2` on the instance (§7.2).

### 16.9 Numerical notes

- **K = 0 exactly is a trap**: the numerator's roots then sit in double pairs on the jω axis and the
  Hurwitz picker's "lower half by real part" tie-break returns a degenerate factor (observed: every
  real-to-real case reported unrealizable at K = 0 and extracted cleanly at K = 1e-6). Floor K at
  1e-6; the return-loss difference is below 0.01 dB.
- The DC pin is applied analytically (ε from K and Γ₀), so the only search is 1-D in K and monotone —
  bracket and bisect, no grid.
- §6.7's leading-coefficient trimming rule applies as written; the polynomials are degree 4n in s.

## 17. Owner decisions — 2026-08-28 (rev 2)

1. **Elliptic and inverse Chebyshev are excluded** (§6.8), not deferred: the ladder cannot express them,
   the synthesis is a different algorithm, and the matching benefit is marginal. A future notch is a
   separate feature.
2. **Lowpass and highpass forms are added as §16 specifies** — matched between F1 and F2, even element
   counts, Chebyshev and Butterworth only, no Norton rack, ratio pinned by transparency. Odd element
   counts are a recorded follow-up (§16.3).
3. **The two Chebyshev families are renamed** per §6.9 — *single-match (optimum)* and *double-match
   (exact)* — as a display change only; enum members and saved designs are untouched. *(Recommended in
   rev 2; the owner has not yet confirmed the exact wording.)*

---

## 18. Multiband matching — dual-band, tri-band, and the asymmetric case (rev 3, 2026-08-28)

**The owner asked whether a "multi-band" Match can be built — two bands, f1–f2 and f3–f4, matched
together, with the region between them deliberately *not* matched so that no Fano budget is spent
there — and whether the idea extends to three bands. Answer: yes to both, and the dual-band case is
two pieces of code this document already specifies, connected.** Everything below was checked
numerically while writing it (the §16.3 prototype resonated by §4.4, against an independent ABCD sweep
of the resulting L/C ladder); closed form and simulation agree to 0.01 dB in every case, and the
numbers are quoted so they can become golden values.

### 18.1 The principle — Fano over a union of bands

Fano's bound is an integral over *all* frequency: for a parallel R‖C load, `∫₀^∞ ln(1/|Γ|) dω ≤ π/(RC)`.
A single wide match from f1 to f4 spends that budget across the whole span, gap included. If the
application only needs f1–f2 and f3–f4, everything spent between f2 and f3 is wasted — so the ideal
multiband network leaves `|Γ| = 1` in the gap and spends the whole budget in the bands. For a flat
match the ideal return loss is `π/(RC · Δω)` nepers with Δω the **sum of the band widths**, and the
difference is not small: the example of §18.4 has an ideal of 24 dB over the union and **87 dB** over
the two bands alone.

A finite lossless ladder cannot put `|Γ| = 1` in the gap — it reaches the ideal only asymptotically
with order — but it can leave the gap *mostly* unmatched, and doing so is what buys the in-band
return loss. **The gap mismatch is the feature, not a defect**, and the Designer reports it as a number
rather than hiding it.

### 18.2 Route A — the resonated multiband network

§16.3's band-shifted prototype is a lowpass-form family whose passband **excludes DC**:
`Φ(u) = T_n(x(u))²` (or `x(u)^{2n}`) is equiripple on `u ∈ [a², 1]` and large on `[0, a²)`. Today that
family is *used directly* as a ladder of single elements. **Resonate it instead** — push its g-vector
through §4.4's bandpass transformation, `Ω = (ω/ω₀ − ω₀/ω)/w` — and its passband `|Ω| ∈ [a, 1]`
lands on **two** frequency bands, one either side of ω₀, with the prototype's own stopband `|Ω| < a`
becoming the gap. That is the classical dual-band frequency transformation, and here it is nothing
more than a different g-vector fed to the existing `Build`:

```
  ω₀ = 2π √(f1·f4)          w = (f4 − f1) / √(f1·f4)          a = (f3 − f2) / (f4 − f1)
  prototype:  MatchFormPrototype.GvaluesAt(family, n, a, K, ε²)   → 2n elements + ratio
  ladder:     MatchSynthesis.Build(g, 2n, …)                        → 2n resonant arms, 4n elements
```

`Ω = ±1` map to f1 and f4, `Ω = ±a` to f2 and f3, and `Ω = 0` to ω₀, the gap centre, where every arm is
transparent. Consequences, all of which fall out rather than being designed in:

- **Order keeps its meaning per band.** `T_n` has n zeros in `[a², 1]`, so the network has **n match
  points in each band** and **4n elements** — the same count as a single-band bandpass network of
  order 2n, which is the fair comparison (§18.4).
- **Norton transforms, absorption, the excess-element rule, Flatten and the stamp are unchanged.**
  The ladder is the alternating two-element-arm ladder of §4.4, in the flat `MatchNetwork` list; every
  arm has an L and a C, so §4.7's pair scan finds its pairs, the far resistance is reached by Norton as
  in every bandpass design, and `RequiredTransformRatio` means what it means today. Unlike the elliptic
  case §6.8 excluded, **no branch with an internal node appears**.
- **The design rule is §4.3's, with a different prototype.** The near end's absorbed element is fixed,
  `g₁ = Q_ana · w` (Q at ω₀, w the *outer* fractional bandwidth), which pins one of the family's two
  parameters (K, ε²); the other is chosen to minimise the worst in-band `|Γ|² = (K + ε²)/(1 + ε²)`
  (Φ's in-band maximum is 1). The far end is then reconciled by §4.5 — surplus becomes an excess
  element, deficit is a refusal naming both numbers — and the ratio by Norton. This is exactly the
  shape of `FanoG` (one end prescribed, one free parameter, optimum root), so it is the **single-match
  (optimum)** member and is labelled as such. Butterworth is the same with `x^{2n}`. With **neither
  end reactive** there is no g₁ to prescribe: K sits on its floor and ε² comes from `RippleDb`, as the
  real-to-real bandpass case does today. **Bessel and the double-match Chebyshev are not offered** in
  v1 (the first has no prototype defined here; the second is a 2-D solve in (K, ε²) for both end
  elements — feasible, and recorded as a follow-up rather than designed).
- **The DC pin of §16.3 becomes the gap-centre pin, and Norton frees it.** Before any transform the
  network is transparent at ω₀, so `|Γ(ω₀)| = |(r − 1)/(r + 1)|` for the *prototype's* ratio — but the
  prototype's ratio is an output here, not the target, so the pin costs nothing. This is why the
  resonated form does not pay §16.1's ratio penalty.

**Parity.** The family has an even element count — Φ is a polynomial in u, so the prototype has 2n
elements — and therefore **2n arms, whose ends have opposite orientation**. Both ends can be absorbed
only for a **mixed** termination pair (one series, one shunt) or when at least one end is resistive.
A like pair — the classic shunt-C-to-shunt-C interstage — needs an odd arm count, which this family
cannot produce.

> **Closed in MN-MB2 (rev 4, 2026-08-28).** §18.5's weighted family produces exactly that: 2n + 1
> arms, both ends carrying the analysis end's own orientation, **4n + 2 elements**. Order still means
> match points per band — `R_n` has n zeros in u and the extra pole sits at `u = −u_r`, off the axis
> — so the picker offers the same 1…3 for either pair and only the element count moves. The refusal
> this paragraph described is gone; what remains is that the odd family is **equiripple by
> construction** (a Remez exchange produces nothing else), so a like pair is Chebyshev-only and
> Butterworth is refused by name.

### 18.3 The one real constraint — geometric symmetry, and how the Designer meets it

A real network's `|Γ(jΩ)|²` is an even function of Ω, so the two bands are mirror images about ω₀ in
*log* frequency: **f1 · f4 = f2 · f3**, equivalently equal fractional (ratio) bandwidths `f2/f1 =
f4/f3`. A user will not type four frequencies that satisfy this, and the Designer must not silently
design to a different spec, so the rule is:

> **Keep the wider band exactly. Widen the narrower band *away from the gap* until its ratio equals
> the wider band's, and design to the widened pair. Show both the requested and the effective bands.**

Away from the gap, because the gap is the budget saving; a widening toward it would spend budget on
frequencies the user did not ask for *and* shrink the reclaim. For 2.4–2.5 / 5.15–5.85 GHz the upper
band's ratio is 1.136, so band 1 becomes **2.201–2.5 GHz** (ω₀ = 3.588 GHz) and the network over-delivers
below 2.4 GHz at a cost of ~6–11 % of the budget in the example below — small, because both bands are
narrow. Where the two bands' ratios differ greatly (0.9–1.0 GHz and 3–6 GHz, say) the stretch is
severe, the design would match 0.5–1.0 GHz to serve a 0.9–1.0 spec, and this route is the wrong tool:
that is the asymmetric case, §18.6.

The same rule generalises: for N bands the **effective** band set is each requested band unioned
with the log-mirror of its partner about ω₀. For tri-band (§18.5) the middle band must be centred on ω₀
— it is kept, `ω₀ = 2π√(f3·f4)` — and the outer pair are mirrored: f1·f6 = f2·f5 = f3·f4, each outer
band widened outward to cover both itself and its partner's image.

### 18.4 What it buys — measured

Load 20 Ω ‖ 2.5 pF (Q = 1.1273 at ω₀) into 50 Ω, effective bands 2.2009–2.5 and 5.15–5.85 GHz
(`w = 1.01699`, `a = 0.72620`, `g₁ = Q·w = 1.14641`), Chebyshev single-match optimum. Same element count
on each row; the single-band column is the classical Fano-optimum Chebyshev over the whole span
2.2009–5.85 GHz computed by the same route at `a = 0`:

| elements | dual-band, worst in either band | single band over the span | gap max \|Γ\| | Fano budget in the gap |
|---|---|---|---|---|
| 4 (n = 1) | **−19.48 dB** | −13.7 dB | 0.319 | 38 % |
| 8 (n = 2) | **−31.79 dB** | −18.8 dB | 0.445 | 38 % |
| 12 (n = 3) | **−41.51 dB** | −20.8 dB | 0.712 | 31 % |

Two things to read off it. The gain is large and grows with order, because a higher-order polynomial
is bigger in the gap — the rising gap |Γ| column is the reclaim happening, and a status strip that
showed only in-band numbers would hide the mechanism. And the finite ladder still leaks a third of
the budget into the gap, so the achieved 32 dB is far from the 87 dB ideal; that is the nature of an
all-pole ladder, not a defect to tune out.

The optimum is **insensitive to K**: a decade either side of the optimum costs 0.1–1 dB, so a coarse
scan followed by a bounded 1-D refinement is sufficient and no root-finding subtlety exists. The
8-element ladder is manufacturable as it stands (L 364–815 pH, C 2.41–5.41 pF at 20 Ω before any
transform).

**Golden values** (the 8-element member, computed at the K and ε² stated — a member is exact at its
parameters, the optimum over K is only defined to the search's tolerance):

```
  n = 2, a = 0.7261974, K = 3.654984e-5, ε² = 6.255720e-4
  g = [1.1464128, 0.9341372, 2.4818781, 0.4175330, 2.6060058]         (2n elements + ratio)
  shunt-first at R_ana = 20 Ω, ω₀/2π = 3.5881750 GHz, w = 1.0169920:
      shunt   L = 786.96065 pH   C = 2.5000000 pF   ← the load's own 2.5 pF, exactly
      series  L = 814.83495 pH   C = 2.4144787 pF
      shunt   L = 363.50768 pH   C = 5.4122698 pF
      series  L = 364.20823 pH   C = 5.4018594 pF
  R_far = 7.674580 Ω  →  required Π N² = 6.515014 to reach 50 Ω
  worst |S11| in 2.2009–2.5 and 5.15–5.85 GHz: −31.793 dB;  max |S11| in 2.5–5.15 GHz: 0.4454
  4-element member for the same problem:  K = 6.039617e-4, ε² = 1.079797e-2,
      g = [1.1464128, 0.5512576, 1.9376596], R_far = 10.321731 Ω, worst −19.477 dB, gap max 0.3192
```

### 18.5 Tri-band and beyond — a union of prototype intervals, and the Remez family

Three bands are a middle band centred on ω₀ plus a symmetric outer pair. In the prototype variable
`u = Ω²` the passband is then a **union of two intervals**, `[0, a²] ∪ [b², 1]` with `a = Ω(f4)` and
`b = Ω(f5)`; N bands are ⌈N/2⌉ intervals. The equiripple polynomial on a union of intervals is the
*Akhiezer* polynomial: it has a closed form only when the intervals have equal length in u (a quadratic
mapping onto one interval), and in general it is produced by a **multi-interval Remez exchange** — a
deterministic approximation with a unique answer (the alternation theorem holds on any compact set),
not a tune-to-spec optimiser, and so within §2.2's rule. The polynomial decides for itself how its
zeros divide between the intervals; the user chooses the total degree n (4n elements) and reads the
per-band match points off the response.

Two changes follow for the numerical route, and both are smaller than the lowpass-form work was:

- **Roots.** `RootsInS` writes the Chebyshev roots down by one arccosine; a Remez polynomial has no
  such form. Solve `p(u) = ±j·√c/ε` instead — two degree-n *complex* polynomials in u (n ≤ 6), not the
  degree-4n polynomial in s whose conditioning failed in MN-LP (`src/Core/Match/RESOLVED.md`) — and
  carry each u through `s = ±j√u` exactly as today. The extraction is unchanged.
- **The same Remez, weighted, gives the odd element counts** §16.3 could not: `Φ(u) = (u + u_r)·R_k(u)²`
  is the minimax problem for `R_k` with weight `√(u + u_r)`, one more free parameter, and closes the
  like-pair parity gap of §18.2 and of §16.4 item 2 with one routine.

Measured (scratch Remez, bands 0.5–0.6 / 0.9–1.1 / 1.65–1.98 GHz, 50 Ω ‖ 4 pF, Q = 1.25): **8 elements
−12.0 dB, 12 elements −14.5 dB** across all three bands, ladder values 2.5–7 nH and 3.6–10.5 pF. Those
are illustrative — the scratch exchange was not hardened past degree 4, and the brief's own gates
measure it — but they show the structure carrying three bands at the same part count as a single-band
order-4 network.

> **Built in MN-MB2 (rev 4, 2026-08-28). The figures above reproduced to 0.03 dB** — −11.997 dB at 8
> elements with `R_far = 29.918 Ω`, −14.473 dB at 12, and −8.941 dB at the 4-element order this
> paragraph did not quote — and they are the goldens from here on. Four things this section did not
> say, all of them measured and all of them recorded in full in `src/Core/Match/RESOLVED.md` §MN-MB2:
>
> - **Butterworth has no tri-band member, structurally.** The maximally-flat member is flat at the
>   centre of ONE interval and a union has no such point; on a union the equiripple polynomial is the
>   only member of the family. Tri-band is **Chebyshev only**, and says so rather than failing.
> - **The overlap refusal §18.3 asks for is unreachable.** `f₂' = max(f₂, f₀²/f₅)` and
>   `f₀²/f₅ < f₀²/f₄ = f₃` because `f₅ > f₄`, so both arguments are already below f₃: the mirror image
>   of a band above f₄ always lands below f₃. The guard exists for a spec that is not yet ordered.
> - **All three bands come out at the same worst return loss** (0.01 dB), which is the equiripple
>   property surviving the resonating transform, and the two gaps come out equal on a symmetric spec.
> - **An odd extraction's terminating value is a conductance ratio, not an impedance one** — the Cauer
>   remainder inverts with parity. Reading an odd ladder with the even rule inverts the far port and
>   turns a −14.3 dB match into −0.5 dB with every element value correct.

### 18.6 Route B — asymmetric bands: wanted, recorded, and deliberately not designed yet

**Users will want bands that are not geometric mirrors of each other, and the widening rule of §18.3
serves them badly when the ratios differ a lot.** The general answer exists and is recorded here so the
future investigation starts from it rather than from zero:

- **Lowpass-form multiband.** Run the multi-interval Remez polynomial directly in `u = ω²` over the
  *requested* intervals — arbitrary, any number, no symmetry — and use it as §16 uses its family: a
  ladder of single elements, ratio pinned at DC, K chosen by §16.4's rule. The machinery is §18.5's
  Remez plus §16's synthesis, nothing else. Its limits are §16's limits, all of which bind harder here:
  the ratio is paid out of the budget; there are no Norton pairs; each form absorbs half the kinds and
  **a shunt capacitance on the low-impedance side is not absorbable at all** (the orientation rule,
  `MatchFormSynthesis`'s remarks) — which is the common PA-output case, and which is why the scratch
  attempt at 20 Ω ‖ 2.5 pF → 50 Ω found no member at any degree. Measured where it does apply,
  50 Ω ‖ 1 pF → 20 Ω at the *exact* 2.4–2.5 / 5.15–5.85 GHz: 4 elements −15.1 dB; purely resistive
  50 → 20 Ω: 8 elements −37 dB. The scratch exchange lost robustness past degree 4, so the higher rows
  are not quoted.
- **The genuinely general form** is a bandpass ladder that is *not* resonant arm by arm: `|S21|²` with
  its transmission zeros split unequally between DC and ∞, whose Hurwitz factor extracts to a ladder
  of single L's and C's in which series L's and series C's both occur. Its `|Γ|²` is an even function
  of ω but not of `(ω/ω₀ − ω₀/ω)`, so it carries asymmetric bands, and it has Norton pairs wherever
  two like-kind elements sit in opposite orientation. `MatchNetwork`'s flat list can *express* such a
  ladder; `Build`'s arm pairing, the arm-indexed absorption marks and the pair scan's alternation
  assumption cannot *produce or transform* one. This is a real design effort — the extraction with
  zeros at both ends (a two-sided Cauer, removing poles at DC and at ∞ in a chosen order) and a rewrite
  of the pair scan — and it is the right thing to investigate when the asymmetric case is taken up.
  It would also subsume §16's forms as the all-zeros-at-one-end special case.

Neither is in MB1 or MB2. The Designer states the widened effective bands plainly (§18.3) so a user
whose spec has been stretched can see it, and that is the extent of v1's asymmetric support.

### 18.7 What the user sees

- **The specification pane's Frequency Band card gains a band-count selector** — *Single · Dual · Tri*
  — and, for Dual, a second row `f3, f4` (Tri: `f5, f6`). Rows must be increasing, `0 < f1 < f2 < f3 <
  f4` (`< f5 < f6`). The card shows a one-line **effective-band note** whenever widening changed
  anything: *"Band 1 widened to 2.201–2.5 GHz to mirror band 2 about 3.588 GHz."*, and for three
  bands *"Widened band 1 to … and band 3 to … to mirror about … GHz (band 2 is kept and sets the
  centre)."* — and nothing when the bands already mirror. **Dual → Tri moves the existing second band
  out to f5–f6 and seeds a new middle band between them**, because the middle band is the one §18.3
  keeps; hanging a third band off the end would mirror it straight onto the second.
- **Order is match points per band**, and the picker's element count reads `(4n)` for a mixed
  termination pair and `(4n + 2)` for a like one — parity is decided by the terminations, not by the
  order (rev 4; the "no order at all" rule this bullet used to state is gone with §18.2's own
  correction). A like pair is Chebyshev-only, because its family comes out of a Remez exchange.
- **Multiband is a specification input, not a solution-list coordinate** (the opposite of `Form`,
  §16.7): the band set changes the problem, so it lives in `MatchSpecKey` and the search runs over
  (order × family) for the design's own band count. While the band count is above one, **only bandpass
  rows exist** — lowpass and highpass multiband is §18.6's route B — and the Form filter group shows
  why in one line rather than listing forms that produce nothing.
- **The status strip gains the Fano ceiling line and the gap mismatch.** The ceiling (§18.10) sits
  between the worst-RL line and the gap lines and is computed from the design alone, so it is there
  before any synthesis and still there when one refuses. The gap line reads
  *"gap 2.5–5.15 GHz: max |S11| 0.445 (−7.0 dB) · prototype rise ×19.89"*, beside
  the worst in-band return loss, because §18.4 says that number is the design working. **Tri-band
  shows two such lines**, one per gap, on separate rows — they are independent numbers a user compares
  against each other, equal on a symmetric spec and unequal the moment the middle band is off centre. The response
  plots' default band is `f1 … f4 ± 10 %`, so both bands and the gap are visible; the in-band
  spans are shaded on the |S11| plot so the eye separates them.
- Cards read **"Chebyshev · dual-band · order 2"**; the footer and the Properties summary carry the
  band count and both bands.
- Probe (§10) and Flatten (§11) need nothing: the terminations are read at ω₀ exactly as today, and
  the flattened cell is the ladder.

### 18.8 Persistence

`MatchDesign` gains **additive** fields `BandCount` (int, default 1) and `F3, F4, F5, F6` (Hz, default
0), all omitted-when-absent and read as single-band when absent, so every existing payload rebuilds to
the identical ladder and `Version` stays 1. `F5`/`F6` join the echo parameters alongside `F3`/`F4`
(rev 4), so a tri-band design reads off the schematic page as well as a dual one. `Omega0` and `W` are derived from the *effective outer*
pair — `√(f1·f4)` for dual, `√(f3·f4)` (= the mirror centre) for tri — and `A` (the inner-edge ratio)
is derived too; the effective bands are recomputed at load, never stored. `F3`/`F4` join the echo
parameters on the instance (§7.2); `BandCount` is echoed as `Bands`.

### 18.9 Numerical notes

- **Dual-band needs no new numerics.** `GvaluesAt` writes its roots down (§16.9, MN-LP's finding), so
  the degree-4n-in-s conditioning problem never arises; the only new arithmetic is the symmetrisation
  of §18.3 and the search of §18.2, whose members are microseconds each.
- **The search is a scan over K with a bracketed solve in ε² inside it.** For fixed K, `g₁` was
  monotone in ε² in every case measured, and the bracket is found on a log grid; K is *not* monotone
  in g₁ (MN-LP found the near element rises then falls with K), so scan K rather than bisecting on it.
  `KFloor = 1e-12` applies; the optimum K here sits at 1e-6…1e-3 and the worst return loss moves by
  ≤ 0.1 dB across a decade of K, so 64 scan points and a bounded refinement are ample.
- **Q is taken at ω₀, the gap centre**, exactly as `Termination.QAt(om0)` does today; the absorbed
  element then comes out equal to the termination's own to machine precision (the identity is
  §13's test, and it held: 2.5000000 pF).
- The tri-band roots come from two degree-n complex polynomial solves in u (`MatchPoly.Roots` with
  complex coefficients, or a companion-matrix eigen-solve); at n ≤ 6 this is well conditioned, and
  the brief's gate is the same "every cell extracts" sweep MN-LP ran.
- **The polynomial must carry the affine map it was solved in** (rev 4, MN-MB2). Coefficients in raw u
  are not good enough to root-find from past order 4: a degree-6 polynomial whose roots cluster in
  [0.53, 1] has coefficients spanning five decades and Horner's rule cancels four of them.
  `MatchPrototypePolynomial` carries `(scaled coefficients, α, β)` and the roots are found in the
  scaled variable, then mapped back exactly. With that, the general-polynomial and closed-form routes'
  roots agree to **2–5e-16** over MN-LP's whole 360-cell sweep — every cell — and what separates their
  g-vectors at orders 5 and 6 (7.3e-5) is the **extraction's** own conditioning, which amplifies an
  incoherent one-ulp root perturbation by ~1e11 there. Their *responses* agree to 6.7e-6 dB. The
  multiband path asks for n ≤ 3, where the g-vectors agree to 1e-10.

### 18.10 Feasibility hints — the Fano ceiling and the gap rise (rev 5, 2026-08-28)

**The owner ran a tri-band spec — 100 Ω ‖ 0.125 pF into 1.25 Ω + 5 pF series, bands 2.5–3 / 4.5–5 /
9–10 GHz — and got two solutions, both a flat −2.6…−3.0 dB from 2.25 to 10 GHz: a single wideband
match with no trace of three bands. The synthesis was correct.** Two things were true that nothing on
screen said, and both are closed-form arithmetic on the specification. Neither needs a synthesis to
run.

#### The two integrals, in Q

Fano's bound comes in two weights, and which one applies is decided by the termination's kind and
topology together. In terms of `Q = Termination.QAt(ω₀)` — the one number everything downstream
already reads:

| termination | integral | bound | `α_max`, nepers |
|---|---|---|---|
| parallel R‖C | `∫ ln(1/\|Γ\|) dω` | `π/(RC)` | `π ω₀ / (Q · Σ (ω_hi − ω_lo))` |
| series R+L | `∫ ln(1/\|Γ\|) dω` | `πR/L` | same |
| series R+C | `∫ ln(1/\|Γ\|) dω/ω²` | `πRC` | `π / (Q · ω₀ · Σ (1/ω_lo − 1/ω_hi))` |
| parallel R‖L | `∫ ln(1/\|Γ\|) dω/ω²` | `πL/R` | same |

The identities hold because a parallel C has `Q = ω₀RC`, so `π/(RC) = πω₀/Q`, and a series C has
`Q = 1/(ω₀RC)`, so `πRC = π/(ω₀Q)`; the two inductive cases follow through `CeqAt`. `ceiling_dB =
−8.686·α_max`, quoted negative like an achieved return loss so the two are directly comparable. A
resistive end has no ceiling at all and never binds.

**The consequence the hints exploit: the 1/ω² class is limited by its LOWEST band edge and the Δω
class by its TOTAL bandwidth.** That is what lets a remedy name a specific edge rather than say
"narrow the bands".

**`ω₀` is inert** — the bound depends on the termination only through `R·C` (or `L/R`) — which is why
reading Q is legitimate and why `MatchFanoBoundTests` asserts invariance over a decade either side
rather than assuming it.

#### The owner's fixture, measured

Effective bands (§18.3's mirror rule) are 2.25–3 / 4.5–5 / 7.5–10 GHz. Termination 2 has RC = 6.25 ps
and is the wall; termination 1 is 86 dB away over the same bands and irrelevant.

| band set | ceiling (term 2) |
|---|---|
| the outer span 2.25–10 GHz | **−3.1 dB** |
| the three effective bands | **−6.4 dB** |
| the three bands as typed | **−10.7 dB** |
| band 1 alone (2.5–3) | −16.1 dB |
| bands 2 + 3 alone (4.5–5, 9–10 — already mirrored) | −32.1 dB |

**The mirror widening cost 4.3 dB of ceiling by itself** (9–10 → 7.5–10, dragging 2.5–3 → 2.25–3),
which is the largest single number in the picture and was invisible. The achieved −2.60 dB is 3.8 dB
short of the band ceiling and **0.5 dB short of the outer-span one** — the network is at a wall, just
not the one the spec was written for.

#### The gap rise

The second half is the prototype, not the terminations. The middle band maps to `u ∈ [0, 0.0042]` and
the Remez polynomial runs on `[0, 0.0042] ∪ [0.337, 1]`. Its largest value in the gap, against the
level 1 it holds on the passband:

| order | 1 | 2 | 3 | 4 | 5 | 6 |
|---|---|---|---|---|---|---|
| rise | 0.99 | 0.97 | 1.16 | 2.90 | 8.77 | 18.0 |

**At orders 1 and 2 the polynomial never exceeds 1 in the gap: it IS the single-band hull
Chebyshev**, and the design is a wideband match spending the outer-span budget. The gap first rises
at order 3 and opens at 4/5/6 — orders `ValidOrders` does not offer for three bands. That, not the
synthesis, is why the owner saw one band where three were asked for.

`GapOpensAtOrder` is the smallest order at which **every** gap's rise exceeds **2.0**. The threshold
has a reason: a rise of `r` puts the gap's `|Γ|²` at `(K + ε²r²)/(1 + ε²r²)`, and below r ≈ 2 the gap
sits within a few dB of the passband — the design is spending budget there as though it were band,
which is the one thing a multiband spec exists to avoid.

**One code path serves both band counts.** A dual-band passband is the single interval `[a², 1]`, on
which the exchange returns the shifted Chebyshev polynomial, and its rise is the closed form
`cosh(n·arccosh((1+a²)/(1−a²)))` — asserted to 1e-9 against §18.4's own fixture. The two frequency
gaps of a tri-band design map onto the SAME interval in u (they are log-mirror images), so their
factors are equal by construction; both are reported because the frequency spans are not.

#### The four remedies

`MatchFanoBound.Remedies(design, targetDb)` solves `α_target = −targetDb/8.686` for one variable with
everything else held. **Four closed forms, in this order, and nothing here searches** — an entry whose
formula has no solution, or whose solution points the unphysical way, is absent rather than clamped.

1. **The reactance.** Δω class: `C ≤ π/(R·α_t·ΣΔω)`, `L ≤ πR/(α_t·ΣΔω)`. 1/ω² class:
   `C ≥ α_t·Σ(1/ω_lo − 1/ω_hi)/(πR)`, `L ≥ α_t·R·Σ(…)/π`. Offered only in the loosening direction —
   a parallel-C end asked for a LARGER C is a hint to make the match worse, and is not offered.
2. **The dominant band's inner edge.** The band with the largest `BandShare`, the others held. 1/ω²
   class: its `f_lo`, if one exists below `f_hi`. Δω class: its width, narrowed symmetrically about
   its own centre.
3. **Dropping the dominant band** — the ceiling the rest give, *whether or not* it meets the target,
   because "how much is this band costing me" is worth answering either way. The remaining bands are
   **re-symmetrised** first: dropping one band of three leaves a dual spec with its own mirror rule.
4. **Un-widening the mirror**, multiband only and only when `Effective.Widened`. Two candidates, and
   the **deeper** ceiling wins. Tri, with `f₀² = f3·f4`: either `f5 = f₀²/f2, f6 = f₀²/f1` or
   `f1 = f₀²/f6, f2 = f₀²/f5`. Dual: hold the narrower band and shrink the wider one onto its image,
   from above or from below.

On the owner's fixture at −15 dB they read:

```
  termination 2's capacitance at or above 11.7 pF;
  or band 1 starting at 2.86 GHz instead of 2.25;
  or without band 1 the ceiling over bands 2 and 3 is -32.1 dB;
  or band 1 as 2.25–2.5 GHz mirrors band 3 without widening (ceiling -13.8 dB)
```

The fourth beats its alternative (2.5–3 / 4.5–5 / 7.5–9 GHz at −9.6 dB), and both are worse than the
typed bands' −10.7 dB only because the typed bands are not a spec any network can have.

**−15 dB is a constant** (`MatchFanoBound.HintTargetDb`), not a user setting: it is a usable match by
any ordinary standard, and every remedy being solved for the SAME number is what makes the four
clauses comparable to each other.

#### The ceiling is a theorem, so it is an invariant

**A synthesised network's worst in-band return loss can never be better than the ceiling over the same
bands.** That is the acceptance test of the formula and it is free — every existing golden is a
fixture. `MatchFanoBoundTests.NoSynthesisedNetwork_BeatsItsOwnFanoCeiling` runs §4.9's interstage
problem, §16.2's Golden B and its inductive highpass dual, §18.4's dual-band problem at both orders,
§18.5's tri-band problem at both orders and the owner's own fixture through the ABCD oracle; the
smallest headroom measured is 3.83 dB. **If it ever fails, the weight class is wrong for that
termination kind — not the fixture.**

#### What the user sees

- **The status strip gains a ceiling line**, between the worst-RL line and the gap lines: *"Fano
  ceiling 6.4 dB (termination 2, over the bands)"*, quoted positive like the RL line. Computed from
  the design alone, so it does not wait for the rebuild and **it survives a refusal** — the strip
  drops every other line but Q there, and a refusal is exactly when a ceiling of −3 dB is the answer.
  Its tooltip carries both ends, the typed-band ceiling, the outer-span ceiling and the widening cost.
- **"— at the ceiling" fires against EITHER ceiling**, the band one or the outer-span one, within
  1.0 dB. A network can be at a wall in two ways: nothing lossless beats the band ceiling, and nothing
  that fails to exclude the gaps beats the span one. The owner's fixture sits at −2.60 against −3.11
  and −6.43, and calling that "not at the ceiling" because it missed the unreachable number would be
  the wrong half of the truth. The gap-rise note says which of the two it is. The 1.0 dB slack is
  §18.4's own K-insensitivity (0.1–1 dB): anything closer is not a search shortfall.
- **Each gap line carries the prototype rise**: *"gap 3–4.5 GHz: max |S11| 0.71 (−3.0 dB) · prototype
  rise ×0.97"*. The two numbers answer different questions — the |S11| says how much is reflected
  there, the rise says whether the polynomial excludes the gap at all.
- **The Frequency Band card gains a second note**, below the effective-band note, whenever any gap's
  rise is ≤ 1 at the current order:

  > *"At order 2 the tri-band prototype does not exclude the gaps — this is a single-band match over
  > 2.25–10 GHz (ceiling −3.1 dB). The gaps open at order 4 (rise ×2.9)."*

  or, when no offered order opens them, *"… No offered order opens them for this band geometry; widen
  the middle band or move the outer bands closer."*
- **The loosen hints sit under the solutions panel**, in the same slot and class as the refusal, shown
  when the search landed either empty or against a ceiling above −10 dB. **A hint, never a refusal:**
  solutions that exist still list. Below −10 dB a ceiling has stopped being an explanation and started
  being an excuse — a design whose ceiling is −20 dB and whose search reached −16 has not been stopped
  by physics.

#### Cost

Nothing here is measurable. The ceiling is four multiplications; the rise table is `MatchRemez` at
n ≤ 6 on a 4,000-point scan, memoised on the interval edges exactly as the synthesis memoises its own
exchange, so a specification edit runs at most six exchanges once and none thereafter.

## 19. Owner decisions — 2026-08-28 (rev 3)

1. **Dual-band matching is added as §18.2–§18.4 specify** — the §16.3 prototype resonated through
   §4.4, bands symmetrised by §18.3's widen-away-from-the-gap rule, bandpass form only, Chebyshev
   single-match and Butterworth (plus the ripple prototype for resistive ends). Brief **MN-MB1**.
2. **Tri-band follows in a second brief** (**MN-MB2**) together with the multi-interval and weighted
   Remez families, which also deliver the odd element counts §16.3 deferred and the like-pair parity
   case §18.2 refuses.
3. **Route B — arbitrary asymmetric bands — is recorded in §18.6 and not designed.** It is wanted;
   the general non-resonant bandpass ladder is the path to investigate when it is taken up.

## 20. Owner decisions — 2026-08-28 (rev 4)

1. **Tri-band is added as §18.5 specifies**, on a multi-interval Remez exchange (`MatchRemez`) whose
   equiripple polynomial on a union of prototype intervals replaces the shifted Chebyshev one. Bands
   are mirrored about the KEPT middle band; the selector gains *Tri*, the card an f5/f6 row and the
   status strip a second gap line. **Chebyshev only** — Butterworth's maximal flatness needs one
   interval to be flat in the middle of, and a union has none.
2. **Odd element counts are added**, from the same exchange in its weighted form
   `Φ(u) = (u + u_r)·R_k(u)²`. They close §16.4 item 2's and §18.2's like-pair gaps at once: **parity
   is decided by the terminations**, order keeps meaning match points, and the element count reads
   2n / 2n + 1 (lowpass, highpass) or 4n / 4n + 2 (multiband). They buy **absorption, not return
   loss** — §16.3's claim to the contrary is corrected in place.
3. **Route B is still §18.6's**, untouched. `MatchRemez` is written in a scaled variable so route B
   can call it with bands in Hz² unchanged, and nothing here does.

## 21. Owner decisions — 2026-08-28 (rev 5)

1. **Feasibility hints are added as §18.10 specifies, and they are CLOSED FORM.** The ceiling, the
   gap-rise table and all four remedies are formulas evaluated once; **none of them searches**, and
   none of them may become an optimiser. "The best spec for these terminations" is a different
   question and is not asked here — each remedy solves for one named variable with everything else
   held, and an entry whose formula has no solution is absent rather than approximated.
2. **The hint target is the constant −15 dB** (`MatchFanoBound.HintTargetDb`), not a user setting.
   Every remedy solved for the same number is what makes the four clauses comparable; four clauses
   each answering a different question would be worse than none. Likewise the two thresholds have
   reasons and not preferences: 1.0 dB of "at the ceiling" slack is §18.4's own K-insensitivity, and
   the ×2 gap-rise threshold is where `(K + ε²r²)/(1 + ε²r²)` stops sitting within a few dB of the
   passband.
3. **"At the ceiling" is judged against either ceiling** — the one over the bands or the one over the
   outer span — because a network can be at a wall in two ways, and the owner's own fixture is at the
   second one. §18.10 states the case; the gap-rise note is what tells the two apart.
4. **Milestone 3 is done: tri-band now offers orders 1 to 6, and dual-band still stops at 3.** The
   asymmetry is the point, not an oversight. A dual-band prototype's passband is the single interval
   `[a², 1]` and it excludes its gap from the very first order (rise 3.2 at n = 1 on §18.4's fixture),
   so there is no low-order failure mode to escape and the twelve-element comparison against a
   single-band order 6 stands. A **tri-band** prototype with a narrow middle band excludes its gaps at
   no order below 4 — §18.10's rise table, ×0.97 at order 2 against ×2.90 at order 4 — so orders 4, 5
   and 6 are the only ones at which such a spec is three bands at all. The price is parts: **16 / 20 /
   24** elements for a mixed termination pair (18 / 22 / 26 for a like one) against order 3's 12, and
   the picker says so.

   **It was raised on a measurement, not on a constant.** `GvaluesAtPolynomial` was proven to degree 6
   on a single interval only; all eighteen cells of §18.5's three band sets × orders 1–6 were run
   through the ABCD oracle before the cap moved. Every cell extracts, every cell puts all three bands
   at the same depth, and the extraction reaches `(K + ε²)/(1 + ε²)` at every degree on a union.
   **Higher order is not monotonically better** — see `src/Core/Match/RESOLVED.md` §MN-FH — so the
   solutions list, which spans every order and ranks by return loss, is the right way to choose one
   and the picker must not be read as "more is better".

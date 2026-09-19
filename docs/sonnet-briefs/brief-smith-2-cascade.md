# Brief 2 — the cascade evaluator and the trajectories, with the engine as its oracle

**Series:** [Smith Chart](brief-smith-0-overview.md) · **Tag:** `R-smith2-n` · **Phase:** P1
**Area:** `src/Design/Smith/` · **Depends on:** 1 · **Blocks:** 3, 5, 10
**Design note:** [`smith-chart.md`](../design/smith-chart.md) §3.2, §3.3, §3.5, §3.6, §4.1, §4.2, §4.6

---

## 0. What this brief delivers

`SmithCascade` — the whole numeric half of the tool, closed form, framework-free, with **no matrix, no
solve and no iteration anywhere**. Perhaps 400 lines, and a gate that is worth more than the code.

---

## 1. `R-smith2-1` — `Evaluate` returns EVERY node, not the last one

```csharp
public static Complex[] Evaluate(SmithDesign d, double fHz);   // length = enabled elements + 1
```

The whole chart is a projection of that one array: node 0 is the generator, node k is the impedance
looking **back toward the generator** from the output of element k−1, and the last is the load.

```
Z₀     = Zgen(f)
series : Z_{k+1} = Z_k + Z_e(f)
shunt  : Z_{k+1} = 1 / ( 1/Z_k + Y_e(f) )
2-port : Z_{k+1} = Z₂₂ − Z₁₂·Z₂₁ / (Z₁₁ + Z_k)          port 1 faces the generator
TLIN   : Z_{k+1} = Z₀ₗ·(Z_k + jZ₀ₗ·tanθ)/(Z₀ₗ + jZ_k·tanθ)
```

**A disabled element contributes nothing and occupies no node.** The array is over *enabled* elements, and
every caller indexes it that way — brief 5's grippers and brief 6's selection both need to map a node back
to an element, so `Evaluate` returns the element index alongside each node rather than leaving three call
sites to recompute the same skip.

**There is no load element and nothing terminates the cascade.** The load is where you read.

### `R-smith2-2` — the element immittances

```
R      Z = R                      Y = 1/R
L      Z = jωL                    Y = 1/(jωL)
C      Z = 1/(jωC)                Y = jωC
SRLC   Z = R + jωL + 1/(jωC)
PRLC   Y = 1/R + 1/(jωL) + jωC
Z1P    Z = Z                      (complex constant; no frequency dependence — that is the point of it)
S1P    Z = Z_file·(1+S₁₁)/(1−S₁₁)  S₁₁ interpolated to ω, referenced to the FILE's own Z
S2P    the Z-parameter form above, S interpolated to ω then converted
open   Y = j·tanθ / Z₀ₗ
short  Y = 1 / (jZ₀ₗ·tanθ)
```

A shunt element uses `Y_e`; a series element uses `Z_e`. Write each element's immittance **once**, in the
form its row above gives, and derive the other by reciprocal — two hand-written forms of the same element
is two places for a sign to be wrong.

### `R-smith2-3` — a TLIN's length scales with frequency

```
θ(f) = (π/180) · E · f / F_ref
```

`TLIN`'s own semantics (`Z`, `E`, `F`), and not negotiable: the whole reason the load point is plotted at
several frequencies is to see the network come apart at the band edges, and a line whose electrical length
did not change with frequency would be the one element in the cascade that never did.

`F_ref` is the element's own `ReferenceFrequencyHz` and **does not follow the design frequency**. Brief 4
defaults it to the design frequency when an element is placed and brief 6 shows it as an editable field;
this brief only reads it.

### `R-smith2-4` — a Touchstone file is fitted ONCE

`SnpInterpolator` behind `TouchstoneCache`. Overview §1j: the S-parameter engine's own re-fit-per-point
defect is recorded in `src/Engine/RESOLVED.md`, and here the cost would land inside a drag.

The fit is keyed by resolved absolute path; the cache is the existing one and this brief adds no second
one. A `FileRef` that does not resolve, or a file whose port count disagrees with its `Kind`, is a
**refusal naming the element and the file** — never a silently-skipped element, which would draw a
perfectly smooth network that is missing a part.

### `R-smith2-5` — the generator, interpolated, and the refusal that is not an extrapolation

`Zgen(f)` is **linearly interpolated in R and X** between the two bracketing table rows. A frequency
outside the table's span is a **refusal whose sentence names the span**, not an extrapolation — except
when the table has exactly one row, where one impedance is flat and every frequency is legal.

The sweep band (brief 9) is the one caller allowed to **clamp** rather than refuse, with a stated note: a
band is a viewing choice where a design frequency is a design input. Expose that as an explicit argument,
so the difference is visible at the call site rather than buried in a bool.

---

## 2. `R-smith2-6` — the trajectories: scale the immittance, not the component values

One polyline per enabled element, in the Γ plane, against the chart's own Z₀.

```
series :  Z(t) = Z_in + t·Z_e(ω)         t ∈ [0,1]
shunt  :  Y(t) = Y_in + t·Y_e(ω)         t ∈ [0,1]
TLIN   :  θ(t) = t·θ_total               the line formula, at the LINE's Z₀
stub   :  θ(t) = t·θ_total               the stub admittance, added to Y_in
S1P/S2P:  no parameter — a two-point dashed chord from Γ_in to Γ_out
```

For an L, a C or an R this is exactly the classical construction and produces exactly the classical
curves — a series reactance walks a constant-**resistance** circle, a shunt susceptance a
constant-**conductance** circle. What it buys is **no special case for the multi-parameter elements**: an
SRLC's `Z_in + t(R + jX)` is a straight segment in Z and therefore a circular arc in Γ, one curve, one
gripper, no discontinuity.

The alternative — scaling the component *values* — is the reading "from 0 to its value" most naturally
suggests and it does not survive contact with a capacitor: as C→0 the reactance −1/ωC runs to −∞, so the
trajectory leaves the chart at t→0 and comes back. **Do not implement it, and do not offer it as an
option.**

### `R-smith2-7` — sample in `t`, never in the derived quantity

Sampling a stub's susceptance uniformly would put no points where the curve moves fastest. Sampling θ
uniformly is correct everywhere, **including through the pole**.

Adaptive on chord error in **canvas** space with a fixed budget, so a curve is as smooth as the zoom
deserves and no smoother. The budget and the tolerance are arguments; the caller (brief 5) supplies the
world-to-canvas map.

### `R-smith2-8` — the three cases that have a wrong version that looks right

- **A trajectory through Γ = 1.** A stub longer than a quarter wave has `tan θ` run through a pole, so its
  susceptance sweeps to +∞ and returns from −∞. On the chart that is **not a discontinuity**: `Y_in + jB`
  over the whole real line is exactly the closed constant-conductance circle, traversed through Γ = 1.
  Emit it as **one** polyline. Sampling in θ walks it correctly; sampling in B cannot.
- **A negative-real-part impedance is drawn, not clamped.** An active `S2P` or a Z1P with negative R puts
  a node outside the unit circle. Clamping to the disc would be a lie about a stability result; the
  chart's own autoscale already enforces a unit-circle *minimum* and grows past it when the data asks.
- **A disabled element emits nothing**, and the gripper between its neighbours belongs to the next
  *enabled* element. There is no zero-length stub sitting invisibly in the chain.

### `R-smith2-9` — direction is part of the geometry

Each trajectory reports its **midpoint and tangent**, so brief 5 can draw an arrowhead pointing from input
to output. Two adjacent arcs sharing a gripper are otherwise ambiguous about which way the walk goes. The
arithmetic belongs here; the glyph belongs there.

---

## 3. `R-smith2-10` — the gate: the engine is the oracle

**This is the reason this brief exists as a brief.** `tests/Ui.Tests/Smith/SmithCascadeTests.cs`:

> For every `Kind`, in every legal `Placement`, build the equivalent `.cnl` — a `Port` carrying the
> generator's impedance, the cascade as real component lines, a `Port` at the load — run the ordinary
> S-parameter analysis, convert S₁₁ back to an impedance, and compare against `SmithCascade.Evaluate`.

- **Tolerance 1e-9 relative on Z.** That is the numerical floor, not an engineering allowance.
- **One test, parameterised over the vocabulary**, not eleven tests. The claim is "the closed form agrees
  with the engine", and it is one claim.
- The equivalent netlist is written by the **same** `Kind → SymbolKind → engine name` function brief 1
  wrote (`R-smith1-2`). A second mapping written for the test would let the test and the product agree
  about a component neither of them spells correctly.

**What this catches is convention, not arithmetic.** A sign, a port order, a reference impedance, a `tan`
where a `cot` belongs — the class of error that produces a plausible picture and survives inspection. It
is affordable only because §1g of the overview holds: every element is a component the engine already has.

### The rest of the gate — one test per claim

1. **The classical constructions.** A series L from a real Z stays on that Z's constant-resistance circle
   to machine precision; a shunt C on the constant-conductance circle. This is what a user checks by eye,
   so it is worth checking by test.
2. **A 135° open stub is one continuous polyline** — successive canvas-space steps bounded, no NaN, no
   dropped segment (`R-smith2-8`).
3. **A TLIN's length tracks frequency**: the same element at 2·F_ref has twice the electrical length, and
   a quarter-wave line at F_ref transforms `Z → Z₀ₗ²/Z` there and not at 2·F_ref.
4. **Disabled elements are absent** from the node array and from the trajectory set, and the array length
   is `enabled + 1`.
5. **Generator interpolation and its refusal**: a mid-table frequency interpolates in R and X; an
   out-of-span one refuses with the span in the sentence; a one-row table accepts anything.
6. **An unresolvable or wrong-port-count `FileRef` refuses**, naming the element and the file.
7. **A Touchstone file is fitted once**: evaluate a 201-point sweep over an S2P element and assert the
   interpolator was constructed once — a counter, not a stopwatch (standing rule 5).

---

## 4. What this brief must NOT do

- **No `Plot`, no `Trace`, no Skia, no canvas.** The trajectory sampler takes a world-to-canvas *function*
  and returns points; it does not know what a `PlotControl` is.
- **No gripper inverse.** Brief 3.
- **No constant-Q arcs and no swept band.** Brief 9.
- **No matrix, no factorisation, no `DataSet`.** If a future element makes closed form untrue, *that
  element* is the thing to reconsider — an interactive Smith chart that has to schedule frames is a
  different and much worse tool, as harmonicaRF's `FrameScheduler` exists to prove.
- **No caching, warm-starting or frame scheduling.** A 12-element network over a 201-point sweep is under
  2,500 element evaluations. Measure it if you doubt it; do not pre-optimise it.

---

## 5. On completion

Findings to `src/Design/RESOLVED.md`. **Never a `CLAUDE.md`.**

If the oracle disagrees anywhere, **record what the disagreement was** before fixing it — a convention
mismatch found this way is exactly the kind of thing the next person needs written down.

# circuitRF — Nonlinear DC, Automatic Differentiation & the SDD Device (Phase 3 Design)

**Status:** Draft for review · **Date:** 2026-05-31
**Reads with:** `docs/design/expressions.md` (§12 the AD/FD tiers, the value model), `docs/design/data-model.md` (§3 partition sets, §5 `ComponentModel`/`Evaluate`, §7 result model), `docs/design/linear-engine.md` (§2.1 the three MNA uses, §4.3 gmin/regularization, §5 DC formulation), `docs/design/harmonic-balance.md` (§4 the `(i,q,dg,dc)` contract, §10 nonlinear-DC as the HB seed, §11 extended-domain/clamp-warn).
**Defers to:** `docs/design/harmonic-balance.md` for the frequency-domain machinery built on top of this (Phase 4).

This note specifies **Phase 3**: bringing the nonlinear engine up on the **DC-only** problem, before any frequency-domain machinery exists. It covers the **`Evaluate` contract** on `ComponentModel`, the **automatic-differentiation (AD)** engine that produces device derivatives, the **SDD device** (the user-authored equation device), and the **nonlinear-DC Newton solver** that finds an operating point. It gates **Phase 3** and the **Phase-3 nonlinear-DC hero** (the grounded-source GaN FET below). No code is written until this is approved.

## 0. Why nonlinear DC is its own phase

Nonlinear DC is not a separate subsystem — it is the **k = 0 slice of harmonic balance** (HB §10): the same `Evaluate` contract, the same Newton method, the same `gmin` continuity, with **no FFT and no conversion matrix**. That is exactly why it is the right first gate. It brings up four brand-new things — the `Evaluate` contract, automatic differentiation, the first nonlinear device, and the nonlinear Newton driver — **in isolation**, validated against a checkable DC operating point, before Phase 4 layers the frequency-domain machinery on top. When nonlinear DC converges to a known bias, the nonlinear *foundation* is proven; any later HB convergence problem is then isolated to the HB-specific parts. This mirrors the Phase-2 discipline (Hero 1 correctness before Hero 1B scale).

A linear-DC guess is **not** an acceptable substitute for the nonlinear-DC solve. A circuit containing a transistor has no meaningful linear DC operating point: the DC bias is *set by* the nonlinear device, so a linear solve (with the device removed or stamped as some fixed linear admittance) returns the bias of a *different* circuit — typically in the wrong operating region (e.g. a FET's gate guessed at 0 V when the true pinch-off bias is −3 V). For the stiff, threshold-like devices circuitRF targets (FETs, diodes), that wrong-region start traps or diverges Newton. And the nonlinear DC is a *required output* regardless: `Pdc`/PAE read from the k = 0 component (measurements §5).

---

## 1. The `Evaluate` contract

A nonlinear `ComponentModel` implements:

```
Evaluate(in PortVoltages v) -> (i, q, dg, dc)
```

returning, for an `n`-port device at the given port voltages `v` (real, time-domain — at DC, a single real operating-point voltage per port; in HB, one sample of the IFFT'd waveform):

- **`i`** — port currents, length `n` (real).
- **`q`** — port charges, length `n` (real). **At DC, `q` carries no current** (`I_Q = jωQ = 0` at ω = 0), so the DC solve does not use `q`; the contract still *returns* it so the HB path (Phase 4) inherits the plumbing unchanged.
- **`dg = ∂i/∂v`** — the `n×n` conductance Jacobian (real). `dg[k][m] = ∂i_k/∂v_m`. For a FET, `dg[drain][gate] = gm`, `dg[drain][drain] = gds`.
- **`dc = ∂q/∂v`** — the `n×n` capacitance Jacobian (real). Zero for a purely-resistive device; unused at DC.

This is the data-model §5 contract, extended (per HB §4) from the prototype's `(i, g)` to add **charge and its derivative** for reactive nonlinearity. **Phase 3 exercises `(i, dg)`**; `q`/`dc` are built as contract plumbing and return zero for the resistive hero device, so the reactive path is structurally present but not stressed until a device with charge storage arrives (Phase 4+).

### 1.1 Where the derivatives come from — three tiers (expressions.md §12)
- **Closed-form** — a native built-in model (future) supplies its own analytic `dg`/`dc`. Fastest; used where the model is hardcoded in C#.
- **Forward-mode AD** — the **SDD** path (§3): the device is a user-authored expression, and AD differentiates it automatically. **This is the tier Phase 3 builds and validates**, because the hero FET is an SDD.
- **Finite-difference** — a per-model fallback (the prototype's 1e-4 perturbation) when neither closed-form nor AD applies. Built as the safety net and the AD cross-check (§2.4), not the primary path.

---

## 2. Automatic differentiation

The SDD device is a user expression `i = f(v)`; the Newton solve needs `∂i/∂v` (the `dg` block) at every evaluation. Hand-differentiating a real device equation is infeasible and unmaintainable — the Phase-3 hero FET (§5) is a five-deep nest of `tanh`/`ln`/`exp` in which the gate voltage appears in five places. **Forward-mode AD** computes the exact derivative automatically by evaluating the expression in **dual arithmetic**, with no symbolic differentiation and no closed-form `∂f/∂v` ever written down.

### 2.1 Dual numbers, N-wide
A **dual number** carries a value and its derivatives with respect to the N independent variables (the device's N port voltages):

```
Dual { double Value;  double[N] Grad; }      // Grad[k] = ∂(this)/∂v_k
```

For the 2-port FET, N = 2 (`v1`, `v2`), so `Grad` is a 2-vector `[∂/∂v1, ∂/∂v2]`. Evaluating `f` once in dual arithmetic yields, in `f`'s result, both the value `i` **and** its full gradient `[∂i/∂v1, ∂i/∂v2]` — i.e. one row of the `dg` block per evaluated current, from a single pass. (Forward mode computes all N partials of one output in one pass, which is the right mode here: few inputs (N small = port count), the output Jacobian wanted whole.)

The independent variables are seeded with unit gradients at the start: `v1 → Dual(value=v1, Grad=[1,0])`, `v2 → Dual(value=v2, Grad=[0,1])`. Constants and parameters get zero gradient.

### 2.2 Threading AD through the existing evaluator — generic over scalar
The Phase-1 expression evaluator already walks the AST and computes a value. Rather than build a **second**, parallel AD evaluator (which would inevitably drift from the plain one), make the evaluator **generic over its scalar type**: it runs on `double` for plain resolution (elaboration, parameter values) and on `Dual` for differentiation (the SDD hot path). One tree-walk, two scalar types, so the value computed by AD and the value computed plainly are *the same code path* and cannot disagree. Each AST operation is defined once on a scalar interface; `double` and `Dual` each implement it.

This is the decided approach (over a separate AD evaluator) precisely because the hero equation is deeply nested: any divergence between "the value" and "the value AD differentiated" would be a silent correctness bug, and a single generic evaluator makes that divergence impossible.

### 2.3 Dual arithmetic and the derivative table
Each operation propagates value and gradient by the chain rule. The arithmetic:

```
(a + b):  val a+b,          grad a' + b'
(a − b):  val a−b,          grad a' − b'
(a · b):  val a·b,          grad a'·b + a·b'          (product rule)
(a / b):  val a/b,          grad (a'·b − a·b')/b²     (quotient rule)
(a ^ k):  val a^k,          grad k·a^(k−1)·a'         (constant power)
```

The function derivative table — the **small, closed set** the SDD needs (the hero FET proves it: `tanh`, `ln`, `exp`, plus arithmetic):

```
exp(a):   val e^a,          grad e^a · a'
ln(a):    val ln(a),        grad a'/a
tanh(a):  val tanh(a),      grad (1 − tanh²(a)) · a'   (= sech²·a')
sqrt(a):  val √a,           grad a'/(2√a)
sin/cos:  standard
abs(a):   val |a|,          grad sign(a)·a'            (non-diff at 0 — see §2.5)
```

Each built-in registers its value-and-derivative once; adding a function later is a one-line table entry, never a change to the evaluator. The table is the same set `expressions.md` §7 lists; AD just attaches each function's derivative beside it.

### 2.4 Validating AD — finite-difference cross-check
AD correctness is the **single most important Phase-3 test**: a wrong derivative silently corrupts every Newton solve (it converges to the wrong place, or fails to converge, with no obvious symptom). So the AD `dg` is cross-checked against a **finite-difference** of the *same* expression: `∂i/∂v_k ≈ (f(v + h·e_k) − f(v − h·e_k)) / 2h` (central difference, `h ≈ 1e-6·scale`). At the hero bias the AD gm/gds must match the FD gm/gds to several digits. FD is also the production fallback tier (§1.1); building it here serves double duty as the AD oracle and the fallback.

### 2.5 Numerical robustness — overflow-safe exp/softplus, clamp-and-warn
The hero equation contains `exp(−(Sv−v1)/Sc)`: for a Newton iterate that overshoots `v1` strongly positive, this is `exp(large)` → overflow → NaN cascade. This is not hypothetical — HB continuation iterates *overshoot* (HB §11), and this device *will* probe extreme arguments. Requirements on the dual evaluator:

- **Overflow-safe `exp` and the softplus pattern.** `ln(1 + exp(x))` (softplus, which appears twice in the hero equation) must use the standard guarded form (`→ x` for large `x`, `→ exp(x)` for very negative `x`) so it neither overflows nor loses precision. Both the value **and** its gradient must use the guarded form consistently.
- **Domain errors clamp and warn, never hard-fail** (HB §11, expressions.md §18 item 2 resolved). `ln`/`sqrt` of a non-positive argument on an overshooting iterate **clamps** the operation (to a small positive floor) and emits an **obvious user-facing warning** naming the model and the operation — rather than aborting a solve that would converge once the iterate returns in-domain. The warning is surfaced, not buried.
- **`abs`/`min`/`max`/`if` are non-differentiable at their switch points.** AD takes the active branch's derivative (the `if` short-circuit, expressions.md §6); at the exact switch point pick one side (documented), since the measure-zero kink does not affect Newton in practice. Well-designed device equations (like the hero's all-smooth `tanh`/softplus formulation) avoid kinks precisely so Newton stays well-behaved.

### 2.6 The hot-path allocation requirement
In HB (Phase 4) `Evaluate` runs per time sample × per Newton iteration × per sweep point — the SDD inner loop must allocate **zero garbage**. The `Dual`'s `Grad` is a **fixed-size** N-vector (N = port count, known at model setup), carried by value / on the stack, not a heap `double[]` allocated per operation. Phase 3 (DC) does not stress this, but the dual type is designed allocation-free from the start so Phase 4 inherits it. (A `Dual` for N=2 is three doubles — value + 2 grads — trivially stack-friendly.)

---

## 3. The SDD device

The **SDD** (symbolically-defined device) is a `ComponentModel` whose behavior is a set of **user-authored expressions** rather than hardcoded C#. It is the device that exercises the AD path, and it is how the hero FET (and Heroes 2–5's FET) are authored — the same equations the golden reference is authored from, so the validation tests circuitRF's math against an identical device.

### 3.1 Authoring surface (`.cnl`) — the `SDD:` grammar
An SDD line declares its ports by **2N nets in `+/−` pairs** and its behavior by per-port equations:
SDD:Name  p1+ p1− p2+ p2− …  I[1,0]=<expr>  I[2,0]=<expr>  [I[p,1]=<charge expr>] …

- **Nets** are `(p1+, p1−, p2+, p2−, …)` — each port has its own `+`/`−` pair (its own return). The
  port voltage is the differential `_vp = v(p+) − v(p−)`; a `−` net of `0` grounds that port's return.
  (This is **2N nets in pairs** — distinct from the SnP block's N-or-N+1 *shared-reference*
  convention, linear-engine §4.1. Do not conflate the two.)
- **Port-voltage variables** in the expressions are named **`_v1`, `_v2`, …** (the underscore signals
  an SDD-injected port voltage, not a user variable).
- **`I[p,w]`** is the explicit port-current equation; `p` = port number, `w` = weighting index:
  - **`I[p,0]`** — the **conductive current** equation → the contract's `i` (AD gives `dg = ∂i/∂_v`).
  - **`I[p,1]`** — the **charge** equation. `w=1` means "multiply by jω in the frequency domain,"
    i.e. charge→current (`I_Q = jωQ`, HB §4) → the contract's `q` (AD gives `dc = ∂q/∂_v`). **Parsed
    and evaluated** via the same dual-arithmetic path; the **nonlinear-DC solver drops it** (jω = 0 at
    DC), so the charge plumbing is built and exercised while DC physics correctly ignores it.
  - **`w ≥ 2`** (a user-defined weighting `H[w]`) — **hard-error** (not supported in v1).
- Equation operands reference ordinary `.cnl` variables by name (`B`, `Sc`, `TV0`, …, defined
  elsewhere) plus the `_vp` port voltages. Reuses the Phase-1 expression engine (cached AST), now run
  in dual arithmetic.

**Unsupported constructs that change the device's physics — hard-error, never silently ignore:**
- **`F[p,w]`** (implicit equation, `F(v,i)=0`) — our AD/Newton design assumes the *explicit* `i=f(v)`
  form; an implicit equation needs different handling. Error.
- **`C[n]` / `Cport[n]`** (current-controlled — equation depends on another device's current) — our
  `Evaluate` is voltage-controlled (`i=f(v)`); a controlling-current term can't be evaluated from port
  voltages. Error.

**Auxiliary constructs irrelevant to the DC/HB solve — store-as-string or skip, no warning:**
- **`In[p,w]`** (noise current), **`Nc[p,q]`** (noise correlation) — noise analysis is out of v1
  scope; they don't affect the solve.

The hero FET SDD (purely resistive, grounded source) is then:
SDD:M1  gate 0 drain 0  I[1,0]=_v1/50  I[2,0]=<the GaN i2 equation in _v1,_v2>
(no `I[p,1]` — the hero has no charge term; `_v1 = vgs`, `_v2 = vds`.)


### 3.2 SDD `Evaluate`
Given port voltages `v`, the SDD:
1. seeds each `v_k` as a `Dual` with unit gradient in slot k (§2.1),
2. evaluates each current expression `f_k` in dual arithmetic → yields `i_k` (value) and row k of `dg` (gradient),
3. (charge expressions likewise → `q`, `dc`; zero/absent for the resistive hero),
4. returns `(i, q, dg, dc)`.

No derivative is ever written by the author or the engine — AD produces the whole `dg` block from the current expressions. This is the **general** SDD+AD machinery; the hero FET is one instance of it, which is the point (Phase 3 proves the general path, not a one-off).

### 3.3 Extended-domain requirement
Device equations must be **smooth and defined beyond the solution domain** (HB §11): Newton iterates overshoot, so a model that returns garbage (or undefined) outside its fitted range breaks convergence even when the final answer is in-range. The hero FET is built this way deliberately — its `tanh` saturations and softplus thresholds are smooth and finite everywhere, with no hard clip. This is a stated requirement on SDD authors, enforced softly by the clamp-and-warn of §2.5.

---

## 4. The nonlinear-DC Newton solver

Find the DC operating point: the node voltages at which **every** current balances (KCL) including the nonlinear device currents. This is Newton's method on the full circuit, where the linear part contributes its (constant) DC stamps and the nonlinear devices contribute `(i, dg)` re-evaluated each iteration.

### 4.1 Formulation
At DC (ω = 0), the linear part is its real DC MNA (linear-engine §5: inductor → short, capacitor → open, `gmin` to ground). The unknowns are the node voltages `V`. The residual is

```
F(V) = G_linear · V + I_nonlinear(V) − I_source   ≈ 0
```

where `G_linear` is the linear DC conductance matrix (constant), `I_nonlinear(V)` are the device currents (from `Evaluate`, recomputed each iteration), and `I_source` the independent DC sources. Newton:

```
V_{n+1} = V_n − J⁻¹ · F(V_n)        J = G_linear + ∂I_nonlinear/∂V = G_linear + dg(V_n)
```

The Jacobian is the constant linear conductance **plus the device `dg` block stamped at the device's nodes** — i.e. the device contributes both its current (to `F`) and its conductance (to `J`) each iteration, exactly the linearized-companion-model idea, but with `dg` supplied by AD rather than hand-derived.

This is a **real**, DC problem — no complex numbers, no harmonics, no conversion matrix. It is deliberately the simplest possible setting in which `Evaluate` + AD + Newton run together.

### 4.2 Solve mechanics
- **Sparse** (CSparse, real): `J` is the full-circuit conductance matrix, same sparsity discipline as linear-engine §6 — symbolic structure fixed by topology, refactor numerically per iteration (the values change as `dg` updates).
- **`gmin` continuity** (linear-engine §4.3, §5): every node has the `gmin` shunt to ground, guaranteeing `J` is non-singular even when a device is pinched off (zero `dg`) and a node would otherwise float. The `IfNecessary`/`Always`/`Never` regularization settings apply here as in the linear engine.
- **Convergence test:** `‖F(V)‖ < ε_abs` (default 1e-6, consistent with HB §12), plus a node-voltage-step test (`‖ΔV‖` small); max-iteration cap triggers continuation backoff (§4.3).

### 4.3 Continuation — source stepping
Newton may not converge from a cold start at full bias (a stiff device far from its operating point). **Source stepping** walks the supplies from zero (or a small fraction) up to the target bias, each step seeded by the last converged `V` (HB §10–§11). For the hero (gate −3.05 V, drain 48 V) this is typically easy, but the machinery is built here because HB depends on it. Step-halving backoff on max-iter, as HB §11. Damping `λ ≤ 1` on the Newton update is the within-step companion knob.

### 4.4 Reuse by HB
This solver **is** the HB initial-guess generator (HB §10): HB calls it to get the k = 0 bias before seeding harmonics. It is specified and built here, standalone, and Phase 4 consumes it unchanged. There is **one** nonlinear-DC formulation, used standalone (this phase) and as the HB seed (Phase 4) — the analog of "one linear-DC formulation" in linear-engine §2.1.

---

## 5. The Phase-3 hero — grounded-source GaN HEMT DC operating point

The Phase-3 acceptance anchor: a single grounded-source GaN HEMT (the IMS short-course device, slide 21), biased through a resistive load line, whose DC operating point circuitRF must find and which is **verified by hand/MATLAB** (no reference-tool run needed — the equation is owner-validated).

### 5.1 The device (authored as an SDD, 2-port, source grounded)
Port 1 = gate (v1, i1), port 2 = drain (v2, i2). Equations:

```
i1 = v1 / 50                         ; simple 50 Ω gate input, NOT a function of v2
i2 = (B*TC*tanh(v2*a*(tanh(g*(TV0 − v1 + v2*th + Sc*ln(exp(−(Sv − v1)/Sc) + 1))) + 1))
       * ln(exp(−(2*TV0 − 2*v1 + 2*v2*th + 2*Sc*ln(exp(−(Sv − v1)/Sc) + 1))/TC) + 1)
       * (v2*lam + 1)) / 2
```
(`ln` = natural log.)

Parameters: `Sv=−0.837, Sc=0.71, TV0=4.268, TC=1.507, th=0.001, a=0.176, g=0.089, lam=0.0012, B=1130`.

`q ≡ 0` (purely resistive — no charge storage), so this hero exercises `(i, dg)` only; the `q`/`dc` path returns zero. The `dg` block is `[[1/50, 0],[∂i2/∂v1, ∂i2/∂v2]]` = `[[0.02, 0],[gm, gds]]`, with gm/gds from AD.

### 5.2 The bias circuit
- **Gate:** DC source at **−3.05 V** → series RF choke (or series R) → gate (v1). At DC the choke is a short, so the gate is driven to −3.05 V (vgs is *set*, a known check value, not solved).
- **Drain:** DC source at **+48 V** → series load resistor **Rd = 20 Ω** → drain (v2). This makes the drain a **genuine nonlinear fixed point**: `vds = 48 − i2·Rd`, where `i2 = i2(vgs, vds)` — vds and i2 must be solved self-consistently. (A bare choke to 48 V would pin vds = 48 with nothing to solve; the 20 Ω load line is what makes the drain solve non-trivial.)

### 5.3 The golden operating point (verified, exact)
At vgs = −3.05 V, the self-consistent "series R" solve (`vds = 48 − i2·20`) lands at:

| Quantity | Value | Note |
|---|---|---|
| **vds** | **47.018 V** | self-consistent (48 − i2·20) |
| **i2** | **49.12 mA** | drain current at the converged point |
| **i1** | **−61.0 mA** | = v1/50 = −3.05/50 (exact, linear) |
| **gm** = ∂i2/∂v1 | **62.4 mS** (0.06241 S) | transconductance (AD target) |
| **gds** = ∂i2/∂v2 | **−9.45 µS** (−9.455e-6 S) | output conductance — **negative**, see below |

(Reference values: i2(−3.05, 48) = 49.113 mA at fixed vds=48; at the self-consistent vds=47.018, i2 = 49.122 mA. gm/gds computed by central finite-difference of the equation at the bias — these are the numbers the AD `dg` block must reproduce.)

**Note — gds is negative (≈ −9.45 µS), and that is correct, not a bug.** Given the FET model parameters, at this bias i2 *decreases* slightly as vds rises (equivalently, i2 *increases* as vds drops): the "series R" solve converges to a point where lowering vds from 48 to 47.0 raises i2 from 49.113 to 49.122 mA. This faint negative output slope falls directly out of the model (the drain-shaping `tanh` is saturated while the threshold feedback through `v2·th` just wins); its magnitude is tiny (1/gds ≈ −106 kΩ, essentially a flat current source). Flagged here so that "i2 went up when vds went down" during the Newton solve is recognized as the device's genuine negative gds, not chased as a sign error. It also makes the Jacobian's `dg[drain][drain]` entry negative — a slightly stronger test of the solver's sign handling than a routine positive gds.

(Equation provenance: the inline and MATLAB forms agree at i2 = 49.113 mA. An earlier 8.8 A hand-figure was a dropped negative sign in the second softplus exponent — the equation is correct as written.)

### 5.4 Acceptance criteria (the Phase-3 gate)
1. **AD correctness (most important):** `Evaluate` at (v1=−3.05, v2=48) returns i2 = 49.11 mA, i1 = −61 mA; and the AD `dg` (gm = ∂i2/∂v1 ≈ 62.4 mS, gds = ∂i2/∂v2 ≈ −9.45 µS) matches a finite-difference of the equation to ≥ 4 significant figures. (Unit test, no solve.)
2. **Nonlinear-DC convergence:** Newton converges from a cold start (source-stepped) to vds ≈ 47.018 V, i2 ≈ 49.12 mA — the self-consistent "series R" point. (The full gate test.)
3. **Robustness:** an overshooting iterate that drives the `exp` argument large does not NaN — the overflow-safe softplus/clamp-and-warn holds the solve together.
4. `dotnet build` / `dotnet test` green; no regression to Phases 1–2 (the linear DC, S-parameter, and elaboration tests still pass).

---

## 6. What Phase 3 builds (scope)

1. **`Evaluate` contract** on `ComponentModel` — the `(i, q, dg, dc)` return, with `q`/`dc` plumbed but zero-returning for resistive devices.
2. **AD engine** — the generic-over-scalar evaluator, the `Dual` type (N-wide, allocation-free), the derivative table, overflow-safe `exp`/softplus, clamp-and-warn, FD cross-check/fallback.
3. **SDD device** — the user-authored-expression `ComponentModel`, evaluated in dual arithmetic (`.cnl` authoring syntax — §7 open item).
4. **Nonlinear-DC Newton solver** — real sparse Newton on the full circuit, `gmin` continuity, source-stepping continuation, step-backoff, reusing the Phase-2 linear DC stamps for the linear part.
5. **CLI** — a nonlinear-DC operating-point command, headless.
6. **The hero** — the grounded-source GaN HEMT SDD + bias circuit, validated to §5.4.

**OUT of scope (Phase 4):** FFT, the conversion-matrix Jacobian, harmonics, multi-tone, continuation across power/Γ sweeps, the `q`/`dc` reactive path under drive, V/I spectral cubes. Phase 3 is DC-only.

---

## 7. Open items

1. **SDD `.cnl` authoring syntax** (§3.1) — **closed:** adopt the specified `SDD:` grammar — 2N nets in
   `+/−` pairs, `_vp` port voltages, `I[p,0]`=current / `I[p,1]`=charge(×jω), `w≥2` and `F`/`C`/`Cport`
   hard-error, `In`/`Nc` stored/skipped.
2. **`Dual` N at runtime vs compile time** (§2.6) — N (port count) is known per-model at setup but varies by device (2 for this FET, more for multiport). Decide the allocation-free representation that still allows per-model N (e.g. a small fixed max with N≤max, or a struct generic over N). Implementation detail; settle at bring-up.
3. **Damping/step-backoff policy** (§4.3) — fixed λ vs line search vs engage-after-failure; tune empirically on the hero (shared with the HB §16 deferred damping item).
4. **gm/gds golden values** (§5.4) — **closed:** gm = 62.4 mS, gds = −9.45 µS at the bias (central FD of the equation); these pin the AD unit-test reference. (Note the negative gds, §5.3.)

---

## 8. Summary of decisions

- Nonlinear DC is the **k = 0 slice of HB**, built first, in isolation, as the gate that proves `Evaluate` + AD + device + Newton before the frequency-domain machinery (Phase 4).
- **`Evaluate` returns `(i, q, dg, dc)`**; Phase 3 exercises `(i, dg)`, `q`/`dc` plumbed-but-zero for the resistive hero.
- **Forward-mode AD via dual numbers**, N-wide (N = port count), produces the `dg` block automatically — no hand-derived or symbolic derivatives.
- **One generic-over-scalar evaluator** runs on `double` (plain) and `Dual` (AD), so value and derivative cannot drift; a small closed derivative table (`exp`/`ln`/`tanh`/arithmetic) covers the SDD.
- **Overflow-safe `exp`/softplus** and **domain-error clamp-and-warn** (HB §11) in the dual evaluator — the hero device will probe extreme arguments under continuation.
- **AD validated against finite-difference** of the same expression — the single most important Phase-3 test; FD is also the fallback tier.
- **`Dual` is allocation-free** (fixed-size gradient) for the HB hot path, though Phase 3 (DC) does not stress it.
- **SDD device** authored as user expressions, evaluated in dual arithmetic; the hero FET (and Heroes 2–5's FET) are SDDs — the same equations the golden reference is authored from.
- **Nonlinear-DC Newton**: real, sparse, full-circuit; `J = G_linear + dg`; `gmin` continuity; source-stepping continuation with step-backoff; reuses Phase-2 linear DC stamps. **It is the HB initial-guess generator** (one nonlinear-DC formulation, standalone and as the HB seed).
- **Phase-3 hero**: grounded-source GaN HEMT SDD, gate −3.05 V (choke), drain 48 V through Rd = 20 Ω; golden point **i2 ≈ 49.12 mA, vds ≈ 47.018 V, i1 = −61 mA, gm ≈ 62.4 mS, gds ≈ −9.45 µS** (verified). Acceptance: AD matches FD; Newton converges to the "series R" point; robustness under overshoot.


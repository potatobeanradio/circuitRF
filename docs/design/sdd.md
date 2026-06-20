# circuitRF — SDD (Symbolically-Defined Device) Design

**Status:** Draft (rev 1) for review · **Date:** 2026-06-19 · **Phase:** 7 (nonlinear-device enrichment)
**Reads with:** `nonlinear-dc.md` (the `Evaluate` `(i,q,dg,dc)` contract + dual-AD this device produces),
`harmonic-balance.md` (§4 nonlinear side, §7 conversion-matrix Jacobian — where weighting functions live),
`nonlinear-in-linear-engines.md` (the S-param small-signal linearization of nonlinear devices),
`expressions.md` (the AST + dual-number AD the SDD differentiates through).
**Companion briefs:** `brief-sdd-single-index-nets.md` (the single-index `I[p]` / `Q[p]` + 2-net-per-port
fixes, landed).

This note specifies the **SDD** — circuitRF's user-authored nonlinear device — and the **weighting-function
generalization** that turns its current/charge pair into the full `I[p,w]` form, so a user can express
arbitrary frequency-domain-weighted port currents (the natural way to write a nonlinear capacitor, a
fractional-derivative element, a lossy reactance, etc.). It documents the device for the engine, for the test
suite, and for the eventual User Guide.

---

## 1. What an SDD is

An SDD defines a device's terminal behavior with **user-authored expressions** instead of compiled physics. The
user writes, per port, an expression for the port current (and/or charge) as a function of the port voltages;
the engine evaluates those expressions in the time domain, differentiates them by dual-number AD for the
Jacobian, and balances them like any other nonlinear device. It is the mechanism behind the FET models the HB
heroes use (transcribed from the owner's references), and it is the extension point for any nonlinearity the
built-in primitives don't cover.

The notation and net convention mirror other SDD components in other simulators so hero references transcribe directly.

### 1.1 Ports and nets
An SDD with **N ports** binds **2N nets**, in `+/−` pairs: `p1+ p1− p2+ p2− … pN+ pN−`. Port count is **half
the net count** (`Elaborator.ResolveSddParameters`: `portCount = NetBindings.Count / 2`). The port voltage seen
by the equations is the differential `_vp = V(p+) − V(p−)`. An **odd** net count, or an equation referencing a
port beyond the nets supplied, is a setup **error** (named, not silently truncated — per the landed
single-index brief). Terminal names follow the FET convention for 2–4 ports (`g d`, `g d s`, `g d s t`); higher
port counts use ordinal names.

### 1.2 Equation variables
Inside an equation the user may reference:
- **`_v1 … _vN`** — the differential port voltages (the Newton unknowns' time-domain samples).
- **`_c1 … _cM`** — **control currents**: the current flowing in *another* device, bound via `C[n]` (§8). Used
  in the time-domain current/charge equations exactly like `_vn`.
- **Scope variables** — any named parameter/variable resolved from the `.cnl` scope (`B`, `Sc`, `TV0`, …),
  constants at eval time.
- **`freq`** — the frequency global (Hz), used **only** in weighting-function expressions `H[w]` (§3), not in
  the time-domain current/charge equations (those are functions of voltage, evaluated per time sample).

---

## 2. Equations: `I[p,w]` and the current/charge pair

The core assignment is **`I[p,w]`** — the contribution to **port p**'s current through **weighting function
w**:

```
SDD:X1  p1+ p1−  I[1,0]=_v1/50        ; a 50Ω conductance at port 1
SDD:X2  g 0 d 0  I[1,0]=…  I[2,0]=…   ; a 2-port (FET-style), gate + drain currents
```

- **`p`** — 1-based port index.
- **`w`** — the weighting-function index (§3): `0`, `1`, or user-defined `≥2`.

**Single-index sugar** (landed): `I[p]` ≡ `I[p,0]` (current, no weighting); `Q[p]` ≡ `I[p,1]` (charge — the
jω-weighted contribution, §3.1). Two-index forms always work.

Each `I[p,w]` expression is parsed to an AST once (at construction), evaluated per time sample in **dual
arithmetic** (`SddEvaluator.EvalDual`) so the value **and** its gradient w.r.t. every port voltage come out
together — the gradient is the device's conductance/capacitance the HB Jacobian needs (`expressions.md` §12;
the active `if`-branch is the one differentiated). Domain errors (`log`/`sqrt` of a non-positive argument on an
overshooting Newton iterate) **clamp + warn**, never hard-fail the solve (`harmonic-balance.md` §11).

---

## 3. Weighting functions `H[w]` — the generalization

A **weighting function** `H[w](ω)` is a frequency-domain multiplier applied to the **spectrum** of the
expression `I[p,w]`. The port current is the sum over all weighting indices:

```
i_p(t) = Σ_w  IFT{ H[w](ω) · FT{ I[p,w]( v(t) ) } }
```

i.e. evaluate `I[p,w]` in the time domain → transform → scale each frequency component by `H[w](ω)` → sum →
back to time. **Weighting functions are evaluated in the frequency domain; the current/charge expressions are
evaluated in the time domain.** This split is the whole point: it lets a *memoryless* voltage expression
acquire *frequency-dependent* (reactive, dispersive) behavior.

### 3.1 The two predefined weights
- **`H[0] = 1`** (identity). `I[p,0]` contributes its spectrum unchanged — a **memoryless/conductive** current.
  This is the `I[p]` current path.
- **`H[1] = jω`** (time derivative). `I[p,1]` contributes `jω ×` its spectrum — i.e. `d/dt` of the expression.
  Assigning a **charge** here, `I[1,1] = Q(v)`, yields the current `i = dQ/dt` — the **capacitive** path. This
  is the `Q[p]` path. (At DC, `ω = 0`, so `H[1] = 0` — charge passes no DC current, exactly as a capacitor
  should.)

These two are built in and require no declaration.

### 3.2 User-defined weights `H[w]`, `w ≥ 2`
Higher weighting functions are **SDD parameters**, declared on the instance as expressions of **`freq`**:

```
SDD:X1  p1+ p1−  I[1,2]=_v1   H[2]=1/(1 + j*2*pi*freq*tau)   tau=1n
```

Here `I[1,2] = _v1` is scaled in the frequency domain by `H[2] = 1/(1+jωτ)` — a single-pole low-pass weighting
— giving a port current that is the voltage filtered by a first-order RC response. Any expression of `freq`
(and scope variables) is allowed; `H[w]` is shared across all ports of the SDD (a port uses it by writing
`I[p,w]`). Weighting indices are dense from 2 upward by convention but need not be (`H[3]` without `H[2]` is
allowed; an `I[p,w]` whose `H[w]` is undeclared is a setup error naming the missing weight).

`ω = 2π·freq`. The engine evaluates `H[w]` at the appropriate analysis frequency for each context (§4).

### 3.3 Why this is one mechanism, not three
`H[0]=1` and `H[1]=jω` are not special cases bolted on — they are the `w=0` and `w=1` members of the same
sum. The current code implements exactly these two (current + jω·charge); this design **lifts the hardwired
pair into the general sum** so `w≥2` becomes expressible. Crucially, the generalization is *exact*: setting
`H[0]=1` and `H[1]=jω` reproduces today's arithmetic bit-for-bit (§4.3), so the validated HB path is preserved
and `w≥2` is purely additive.

---

## 4. How weighting functions act in each engine

The weighting multiplies a **per-harmonic / per-frequency complex scalar** onto the spectrum of `I[p,w]`. Where
that happens differs by engine.

### 4.1 Harmonic balance (the heart)
HB already evaluates each nonlinear device in the time domain, FFTs the result, and assembles the residual and
the conversion-matrix Jacobian per harmonic (`harmonic-balance.md` §4, §7; `HbNewton`). The weighting slots in
at the FFT boundary. For each port `p`, each retained harmonic `k` (frequency `ω_k = k·ω₀`):

```
I_nl,p[k] = Σ_w  H[w](ω_k) · ( FFT{ I[p,w](v(t)) } )[k]
```

- **`w=0`:** `H=1` → the spectrum enters directly. (Today's `iNl`.)
- **`w=1`:** `H(ω_k)=jω_k=jkω₀` → the charge spectrum is multiplied by `jkω₀`. (Today's
  `f += j·kω₀·qNl[n,k]` in `BuildF`, with the `k>0` guard giving the DC zero automatically.)
- **`w≥2`:** `H[w](ω_k)` evaluated from the user expression at `freq = kω₀/2π` → a general complex scale.

**Jacobian.** The conversion matrix couples `∂I_p[k]/∂V_q[i]` through the difference- and sum-frequency
components `D[w]_{k−i}`, `D[w]_{k+i}` of the bucket's derivative waveform `D[w] = FFT{ ∂I[p,w]/∂v }` (the §7
real 2×2 blocks). The weighting multiplies the **row** (output-harmonic-`k`) contribution by the complex scalar
`H[w](ω_k)`:

```
block_w(k,i) = [[Re Hₖ, −Im Hₖ], [Im Hₖ, Re Hₖ]] · ( §7 conversion block from D[w] ),   Hₖ = H[w](ω_k)
```

For `w=0`, `Hₖ=(1,0)` → identity → today's `G` block. For `w=1`, `Hₖ=(0,kω₀)` → `[[0,−kω₀],[kω₀,0]]` → exactly
today's `C`-block rotation (`a00 += −kw·cb10; a01 += −kw·cb11; a10 += kw·cb00; a11 += kw·cb01`). For `w≥2`, a
general complex multiply of the bucket's conversion block. **The Maas DC special-cases (§7.3) and the guard
harmonic apply unchanged** (they act on the assembled block). The §5.2 grid floor (resolve `G`/`C`/`D[w]` to
harmonic `2K`) is unchanged.

So the HB change is: **loop the residual and Jacobian assembly over the weighting buckets present, applying
`H[w](ω_k)` per row-harmonic** — with `w=0,1` kept as the fast path so the common (FET / capacitor) case costs
nothing new.

### 4.2 Nonlinear DC
DC is the `k=0`, `ω=0` slice. `H[0](0)=1` (current contributes), `H[1](0)=jω=0` (charge drops — today's
behavior: the DC engine ignores `q`/`dc`). For `w≥2`, **`H[w](0)` is the user expression evaluated at
`freq=0`** — if it is nonzero at DC, that bucket contributes to the DC operating point; if it vanishes at DC
(like any `jω·…` form), it drops. This is the correct, general behavior and needs only that the DC engine ask
the model for `H[w](0)` rather than assume zero for all reactive buckets. (Most physical `H[w]` vanish at DC;
the engine must not *assume* it.)

### 4.3 S-parameters (small-signal linearization)
The linearized admittance of a nonlinear device at a DC bias (`nonlinear-in-linear-engines.md`,
`StampLinearized`) generalizes the same way. Today: `Y(ω) = Dg + jω·Dc` (the `w=0` conductance plus the `w=1`
capacitance). In general:

```
Y_pq(ω) = Σ_w  H[w](ω) · ∂I[p,w]/∂V_q  |_bias
```

`w=0`→`Dg` (since `H=1`), `w=1`→`jω·Dc` (since `H=jω`), `w≥2`→`H[w](ω) · D[w]_bias`. The DC operating point is
found first (the auto-bias rule), then each bucket's bias-point derivative is scaled by `H[w](ω)` and stamped.
For a nonlinear capacitor at zero bias this reduces to `jω·C(0)` — identical to NonlinearC (§5), which is the
test.

**Control currents (`_cn`) are honored here too.** When the SDD references another device's current, the same
small-signal block gains a **control-current column** coupling each SDD port-KCL row to the referenced device's
branch-current unknown: `Σ_w H[w](ω)·∂I[p,w]/∂_cn`. The sign matches the DC branch column, so the column value
at ω→0 equals the DC engine's entry exactly. See §8.5.

---

## 5. Worked example — a nonlinear capacitor as a 1-port SDD

A nonlinear capacitor has `C = C(V)`, equivalently a charge `Q(V) = ∫₀ᵛ C(v) dv`, and a terminal current
`i = dQ/dt`. In SDD form that is one assignment on a 1-port:

```
SDD:X1  c+ c−   I[1,1] = Q(_v1)          ; H[1]=jω turns Q into dQ/dt
```

with `Q(_v1)` written out as the charge expression. For a polynomial `C(V) = Σ_k aₖ Vᵏ`,
`Q(V) = Σ_k aₖ V^{k+1}/(k+1)`.

This is **physically identical** to the dedicated **NonlinearC** device, which stores the `C(V)` coefficients
and returns `Q(V)` directly in the `q` slot (its `Evaluate` puts `Q` in `q`, `C(V)` in `dc`). NonlinearC is the
compiled fast path; the SDD `I[1,1]=Q` is the same charge balanced through the general `w=1` weighting. They
must produce the same small-signal `Y=jωC(V₀)` and the same large-signal HB spectrum — which is the validation
below.

### 5.1 Test CV data
The owner's measured-style profile (C in pF):

| V | 0 | 1 | 2 | 3 | 4 | 5 | 6 |
|---|---|---|---|---|---|---|---|
| C | 10 | 8.5 | 6.2 | 4.1 | 2.5 | 1.8 | 1.5 |

This fits cleanly to a low-order polynomial (order 2–3). For a **simpler, exact** non-trivial test that avoids
fit residuals, use a closed-form quadratic so both devices get identical coefficients and the integral is
exact:

```
C(V) = 10 − 1.5·V + 0.1·V²   (pF)        →  Q(V) = 10·V − 0.75·V² + (0.1/3)·V³   (pF·V)
```

(monotone-decreasing over 0–6 V, like a varactor; non-trivial; clean to integrate). Then:
- **NonlinearC:** `C0=10p  C1=−1.5p  C2=0.1p`.
- **SDD:** `SDD:X1 c+ c−  I[1,1] = 10e-12*_v1 − 0.75e-12*_v1^2 + (0.1e-12/3)*_v1^3`.

### 5.2 The two tests
1. **S-parameter equivalence (small-signal, no HB).** 1-port S-param of each device (auto DC bias at 0 V →
   `C(0)=10 pF`). Assert `S11(NonlinearC) == S11(SDD I[1,1])` at every frequency, within tight tolerance —
   both stamp `jω·C(0)`. This validates the `w=1` linearization (§4.3) against the NonlinearC charge path.
2. **HB equivalence (large-signal).** Drive each device's port with a tone (a series resistor + tone source so
   the voltage swings across the C(V) curve), run single-tone HB, and assert the node-voltage/current spectra
   match across harmonics. This validates the full `w=1` HB charge mixing (§4.1) against NonlinearC's `q`
   path — the nonlinear capacitance generating harmonics must agree device-for-device.

(Once `H[w≥2]` lands, a third test exercises a user weight, e.g. `I[1,2]=_v1` with `H[2]=jω` reproducing a
*linear* capacitor through the user-weight path — a self-consistency check that `H[2]=jω` ≡ the built-in
`H[1]`.)

---

## 6. Implementation seams (current state)

**Current state (verified on disk — weighting functions AND control currents have landed):**
- `SddModel` carries `currentAst[]` (w=0), `chargeAst[]` (w=1), per-port `higherAst[]` (w≥2), the `H[w]`
  expression map, and the control-current bindings (`ControlRefs` / `ControlBranchIndices`). `Evaluate(v, c)`
  returns `NonlinearResult(i, q, dg, dc, terms, dControl)` — the `w≥2` buckets in `terms`, the
  `∂I/∂_cn` sensitivities in `dControl`.
- `ComponentModelFactory.CreateSddModel` parses `I[p,0]`/`I[p,1]`/`I[p]`/`Q[p]`, `I[p,w≥2]`, `H[w]=expr`,
  and `C[n]=<instance>` / `Cport[n]=<port>`; cross-validates that every referenced `H[w]` and every `_cn`
  is declared. `F[…]` hard-errors; `In/Nc` (noise) are skipped.
- `ComponentModel.Weight(int w, double omega)` returns the built-ins (`w=0→1`, `w=1→jω`) and, for the SDD,
  evaluates the user `H[w]` at `freq=ω/2π`.
- `HbNewton` applies the full `Σ_w H[w](ω_k)·…` sum in `BuildF`/`BuildJ` (the `w=0/1` fast path plus the
  `w≥2` buckets), and, when control currents are present, recomputes `_c_ref` per Newton iterate via the
  linear back-solve and adds the control-current Jacobian coupling `J_cc` (FD-oracle-gated).
- `NonlinearDcEngine` reads `_cn` directly from the referenced branch unknown and stamps `∂I/∂_cn` into the
  branch column (exact at DC — the branch is already a Newton unknown).
- `SnpModel`/`ZPortModel` expose `PortBranchIndices` so `Cport[n]` can select a port's branch current.
- `SddModel.StampLinearized` (S-parameter) adds the **control-current column** `Σ_w H[w](ω)·∂I[p,w]/∂_cn`
  coupling each SDD port row to the referenced branch unknown, via the new `IMnaContext.AddNodeBranchCoupling`
  `(node-row, branch-col)` primitive. The S-param engine re-resolves the referenced branch index against its own
  assembly and seeds the DC operating-point control currents. The column value at ω→0 equals the DC engine's
  branch-column entry exactly (same sign). So `_cn` is now honored across **all three** analyses (DC, HB, S-param).
  See `sdd-control-current.md` §5.

---

## 7. Open items / scope

- **`H[w]` frequency variable.** Settled as **`freq`** (Hz), `ω=2π·freq`, matching the HB `freq` stamping and
  the single-index brief's note. Confirm the expression engine binds `freq` in the SDD parameter-resolution
  scope for `H[w]` (it is already stamped per-harmonic in HB).
- **Per-harmonic `H[w]` caching.** `H[w](ω_k)` is constant across Newton iterations (depends only on the
  harmonic grid), so evaluate once per sweep point and cache the `[w][k]` table — not in the Newton hot loop.
- **`F[…]` implicit equations** and **noise `In/Nc`** remain out of scope (still hard-error / skip) — this
  note covers voltage-controlled `I[p,w]` + `H[w]` + control currents `_cn`. (`C[n]/Cport[n]` current control
  is now **supported** — §8.)
- **Multi-tone `H[w]`.** Under two-tone HB the row frequency is `k₁f₁+k₂f₂`; `Weight(w, ω)` takes that
  frequency — no formula change, just the multi-tone `ω` per mix index. Carried, not specially designed here.
- **Stability of high-order user weights.** A user `H[w]` with gain ≫1 at high harmonics can stiffen Newton;
  the guard harmonic (§4.1, acts on the assembled Jacobian block) still applies. No additional clamp planned.

---

## 8. Control currents — `_cn` (referencing another device's current)

An SDD equation can reference the **current flowing in another device** and use it like any other variable. This
is the current-controlled complement to the voltage-controlled `_vn`. Full design + math in
`sdd-control-current.md`; this section is the user-facing reference.

### 8.1 Binding a control current
Two instance parameters declare the reference; the equation then reads it as `_cn`:

```
C[n]     = <instance>     ; bind _cn to the current in device <instance>
Cport[n] = <port>         ; (multi-port devices only) which port's current
```

- **`n`** is the 1-based control index — `C[1]` defines `_c1`, `C[2]` defines `_c2`, …
- **`<instance>`** is the instance name of a sibling device **in the same schematic**.
- **`Cport[n]`** is required only for multi-port referenced devices (SnP, ZnP); omit it (or set 1) for
  two-terminal devices.
- Every `_cn` used in an equation must have a matching `C[n]`, or it is a setup error naming the missing index.

### 8.2 Referenceable devices
The current of these device classes can be sensed (they each solve their current as a branch unknown):

| Device | `Cport` needed? | What `_cn` is |
|---|---|---|
| Independent voltage source (`Vdc`) | no | source branch current |
| Tone voltage source (`V_1Tone` / `V_nTone`) | no | source branch current |
| Current probe (`IProbe`) | no | the probed series current (0 V ammeter) |
| Inductor (`L`) | no | inductor branch current |
| `SnP` (Touchstone N-port) | **yes** | the selected port's current |
| `ZnP` / `Z_Port` (N-port) | **yes** | the selected port's current |

Referencing any other device kind (resistor, capacitor, another node) is a setup error listing the allowed
kinds. References are same-schematic only (no `C[1]=X1.L2` cross-hierarchy paths in this rev).

### 8.3 Sign convention
`_cn` carries the **branch-current sign of the referenced device**: current flows from the device's **first
net to its second** (the stamp convention). For an `IProbe:IP1 a b`, `_cn > 0` means conventional current
flows `a → b` through the probe. Check the sign against a DC bias point if a mirror comes out inverted — flip
it in the equation (`-beta*_c1`) rather than re-wiring.

### 8.4 Worked examples

**(a) Current mirror / sense-and-scale (DC + HB).** A 2-port SDD whose drain current follows a sensed branch
current — e.g. an output current proportional to the current in a sense inductor `Lsense`:

```
L:Lsense    nsrc nx   L=1n
SDD:Xmirror g 0  d 0
    I[1,0] = _v1/1e6            ; high-Z gate (just a DC path for the solver)
    I[2,0] = beta*_c1          ; drain current = beta × the sensed current
    C[1]   = Lsense            ; _c1 = current in Lsense
    beta   = 5
```

At DC the drain sources `beta ×` the inductor current; under HB the full spectrum of `_c1` is mirrored
(harmonics and all), because `_c1` is recomputed at every Newton iterate from the present operating point.

**(b) Current-controlled transconductance with a real reference.** Sense the current delivered by a bias
supply and fold it into a gate-voltage-controlled drain current — a crude current-feedback transconductor:

```
Vdc:Vdd     vdd 0    Vdc=5
SDD:Xcc     g 0  d 0
    I[2,0] = gm*_v1 - kfb*_c1  ; drain current: gm·Vgs minus feedback on supply current
    C[1]   = Vdd               ; _c1 = current drawn from the 5 V supply
    gm     = 0.05
    kfb    = 0.1
```

**(c) Control current through a weighting function (`I[1,1]`, charge/reactive path).** `_cn` can appear in any
`I[p,w]`, including the jω-weighted charge path. Here a control current drives a *displacement-like* term —
the drain charge depends on a sensed current, so the contributed current is its time derivative:

```
SnP:S1      in 0 out 0   File=coupler.s2p
SDD:Xq      g 0  d 0
    I[2,1] = tau*_c1           ; charge ∝ sensed current → current = d/dt(tau·_c1) via H[1]=jω
    C[1]   = S1
    Cport[1] = 2               ; _c1 = current in port 2 of the S-parameter block
    tau    = 1n
```

Because `I[2,1]` rides the built-in `H[1]=jω` weighting, this contributes `jω·(tau·_c1)` per harmonic — a
reactive response to the sensed current. (At DC, `jω=0`, so this term drops, exactly like a charge.)

### 8.5 Which analyses honor `_cn`
- **Nonlinear DC** — exact (the referenced current is a Newton unknown in the same system).
- **Harmonic balance** — exact, with the control-current Jacobian coupling for quadratic convergence
  (FD-oracle-gated). Works single-tone; multi-tone/loadpull control currents are not wired yet.
- **Small-signal S-parameters** — exact. `StampLinearized` adds the control-current column
  `Σ_w H[w](ω)·∂I[p,w]/∂_cn` coupling each SDD port row to the referenced branch unknown; its ω→0 value equals
  the DC branch-column entry (same sign). The small-signal sensitivities are evaluated at the DC operating point
  (seeded into `ControlBias`); for a linear-in-`_cn` equation the sensitivity is seed-independent.

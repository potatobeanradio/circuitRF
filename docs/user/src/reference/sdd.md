---
title: The SDD (Symbolically-Defined Device)
slug: reference/sdd.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > SDD
lede: circuitRF's user-authored nonlinear device. You write each port's current (and charge) as an equation in the port voltages; the engine differentiates it automatically and balances it like any built-in device. It is how the FET models in the examples are defined, and the extension point for any nonlinearity the built-in parts don't cover.
---

<figure class="symbol"><span class="frame">
    <img class="sym-light" src="../assets/symbols/sdd.svg" alt="SDD symbol">
    <img class="sym-dark"  src="../assets/symbols/sdd-dark.svg" alt="">
  </span><figcaption>SDD at 2 ports</figcaption></figure>

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#what">What an SDD is</a></li>
      <li><a href="#ports">Ports &amp; nets (2N differential pins)</a></li>
      <li><a href="#vars">Equation variables</a></li>
      <li><a href="#eqns">The equations: <code>I[p,w]</code>, <code>Q[p]</code></a></li>
      <li><a href="#weights">Weighting functions <code>H[w]</code></a></li>
      <li><a href="#control">Control currents — referencing another device's current</a></li>
      <li><a href="#params">Parameter summary</a></li>
      <li><a href="#analyses">Which analyses honor each feature</a></li>
    </ol>
  </nav>

## What an SDD is {#what}

Instead of compiled physics, an SDD defines a device's terminal behavior with **expressions**. Per
port, you write the port current (and optionally charge) as a function of the port voltages. The
engine evaluates those expressions in the time domain, differentiates them by automatic
differentiation for the Jacobian, and solves them in DC, harmonic balance, and S-parameters. The
equation notation deliberately mirrors other simulators' SDD/EDD so reference models transcribe
directly.

## Ports & nets (2N differential pins) {#ports}

An **N-port SDD binds 2N nets**, in `+/−` pairs: `p1+ p1− p2+ p2− … pN+ pN−`. The port count is
half the net count. The voltage the equations see for port p is the differential `_vp = V(p+) −
V(p−)`. An odd net count — or an equation referencing a port beyond the nets supplied — is a setup
**error** (named, never silently truncated). Terminal names follow the FET convention for 2–4
ports (`g d`, `g d s`, `g d s t`).

```netlist
SDD:X1  p1+ p1−             I[1,0]=_v1/50        ; a 50 Ω conductance at port 1
SDD:X2  g 0  d 0           I[1,0]=…  I[2,0]=…   ; a 2-port (FET-style), each port's minus to ground
```

## Equation variables {#vars}

Inside an SDD equation you may reference:

<table>
    <thead><tr><th>Variable</th><th>Meaning</th></tr></thead>
    <tbody>
      <tr><td><code>_v1 … _vN</code></td><td>The differential <strong>port voltages</strong> (the device's own ports).</td></tr>
      <tr><td><code>_c1 … _cM</code></td><td><strong>Control currents</strong> — the current flowing in <em>another</em> device, bound via <code>C[n]</code> (see <a href="#control">below</a>).</td></tr>
      <tr><td>scope variables</td><td>Any named parameter/variable in scope (model coefficients <code>B</code>, <code>Sc</code>, <code>TV0</code>, …).</td></tr>
      <tr><td><code>freq</code></td><td>The frequency global (Hz) — used <strong>only</strong> in weighting-function <code>H[w]</code> expressions, never in the time-domain current/charge equations.</td></tr>
    </tbody>
  </table>

SDD equations are **real-only** (real time-domain voltages → real current/charge); the imaginary
unit `j` is not allowed in an `I[p,w]`/`Q[p]` expression. The full function set and operators are
the shared [expression language](expressions.html).

## The equations: `I[p,w]`, `Q[p]` {#eqns}

The core assignment is **`I[p,w]`** — the contribution to **port p**'s current through **weighting
function w**:

- `p` — the 1-based port index.

- `w` — the weighting-function index: `0`, `1`, or a user-defined `w ≥ 2`.

**Single-index shorthand:** `I[p]` ≡ `I[p,0]` (a memoryless current); `Q[p]` ≡ `I[p,1]` (a charge
— the capacitive path). The two-index forms always work too.

Each expression is parsed once and evaluated per time sample in dual arithmetic, so the value
*and* its derivatives (the conductance/capacitance the solver needs) come out together. Domain
errors (e.g. `log`/`sqrt` of a non-positive argument on an overshooting solver iterate) **clamp
and warn** rather than killing the solve.

## Weighting functions `H[w]` {#weights}

A weighting function `H[w](ω)` is a frequency-domain multiplier applied to the spectrum of
`I[p,w]`. The total port current sums over all weights:

```text
i_p(t) = Σ_w  IFT{ H[w](ω) · FT{ I[p,w]( v(t) ) } }
```

That is: evaluate `I[p,w]` in the **time domain**, transform, scale each frequency by `H[w](ω)`
(evaluated in the **frequency domain**), sum, transform back. This split is the whole point — it
lets a memoryless voltage expression acquire frequency-dependent (reactive, dispersive) behavior.

### The two built-in weights

- **`H[0] = 1`** (identity) — `I[p,0]` contributes its spectrum unchanged: a memoryless/conductive
  current. This is the `I[p]` path.

- **`H[1] = jω`** (time derivative) — assigning a charge here, `I[p,1] = Q(v)`, yields the current
  `i = dQ/dt`: the capacitive path. (At DC, `ω = 0`, so charge passes no DC current — exactly as a
  capacitor should.) This is the `Q[p]` path.

### User-defined weights `H[w]`, w ≥ 2

Higher weights are SDD parameters, declared as expressions of `freq` (`ω = 2π·freq`):

```netlist
SDD:X1  p1+ p1−   I[1,2]=_v1   H[2]=1/(1 + j*2*pi*freq*tau)   tau=1n
```

Here `I[1,2] = _v1` is scaled in the frequency domain by a single-pole low-pass `H[2]` — a port
current that is the voltage filtered by a first-order RC response. `H[w]` is shared across all
ports; an `I[p,w]` whose `H[w]` is undeclared is a setup error naming the missing weight.

<div class="callout note">
    <span class="label">Example — a nonlinear capacitor as a 1-port SDD</span>
    <p>For a charge <code>Q(V)</code>, one assignment on the <code>H[1]=jω</code> (charge) path gives
    <code>i = dQ/dt</code>:</p>
    <pre class="netlist"><code>SDD:X1  c+ c−   I[1,1] = 10e-12*_v1 − 0.75e-12*_v1^2 + (0.1e-12/3)*_v1^3</code></pre>
    <p>This is physically identical to the dedicated <a href="components.html#nonlinearc">NonlinearC</a> device
    (the compiled fast path). See also the <a href="nonlinear-capacitor.html">C–V Editor</a>.</p>
  </div>

## Control currents — referencing another device's current {#control}

An SDD equation can reference the **current flowing in another device** and use it like any other
variable — the current-controlled complement to the voltage-controlled `_vn`. This is how you
build current mirrors, current feedback, and sensed-current behavior.

### Binding a control current

Two instance parameters declare the reference; the equation then reads it as `_cn`:

```netlist
C[n]     = <instance>     ; bind _cn to the current in device <instance>
Cport[n] = <port>         ; (multi-port referenced devices only) which port's current
```

- `n` is the 1-based control index — `C[1]` defines `_c1`, `C[2]` defines `_c2`, …

- `<instance>` is the instance name of a sibling device **in the same schematic** (no
  cross-hierarchy paths in this release).

- `Cport[n]` is required only for multi-port referenced devices (SnP, ZnP); omit it (or set 1) for
  two-terminal devices.

- Every `_cn` used in an equation must have a matching `C[n]`, or it is a setup error naming the
  missing index.

### Which devices' current can be sensed

<table>
    <thead><tr><th>Device</th><th>Cport needed?</th><th>What <code>_cn</code> is</th></tr></thead>
    <tbody>
      <tr><td>DC voltage source (<code>Vdc</code>)</td><td>no</td><td>source branch current</td></tr>
      <tr><td>Tone source (<code>VTone</code>)</td><td>no</td><td>source branch current</td></tr>
      <tr><td>Current probe (<code>IProbe</code>)</td><td>no</td><td>the probed series current</td></tr>
      <tr><td>Inductor (<code>L</code>)</td><td>no</td><td>inductor branch current</td></tr>
      <tr><td>Series RLC (<code>SRLC</code>)</td><td>no</td><td>the branch current</td></tr>
      <tr><td>Parallel RLC (<code>PRLC</code>)</td><td>no</td><td>the current in its inductor only, not the whole part</td></tr>
      <tr><td>Touchstone N-port (<code>SnP</code>)</td><td><strong>yes</strong></td><td>the selected port's current</td></tr>
      <tr><td>Impedance N-port (<code>ZPort</code>)</td><td><strong>yes</strong></td><td>the selected port's current</td></tr>
    </tbody>
    <caption>Referencing any other device kind (R, C, a node) is a setup error listing the allowed kinds.</caption>
  </table>

### Sign convention

`_cn` carries the branch-current sign of the referenced device: current flows from the device's
**first net to its second**. For an `IProbe:IP1 a b`, `_cn > 0` means conventional current flows
`a → b` through the probe. If a mirror comes out inverted, flip the sign in the equation
(`-beta*_c1`) rather than re-wiring.

### Examples

**(a) Current mirror / sense-and-scale.** A drain current proportional to the current sensed in an
inductor:

```netlist
L:Lsense    nsrc nx   L=1n
SDD:Xmirror g 0  d 0
    I[1,0] = _v1/1e6            ; high-Z gate (a DC path for the solver)
    I[2,0] = beta*_c1          ; drain current = beta × the sensed current
    C[1]   = Lsense            ; _c1 = current in Lsense
    beta   = 5
```

**(b) Current-feedback transconductor.** Sense a bias supply's current and fold it into a
gate-controlled drain current:

```netlist
Vdc:Vdd     vdd 0    Vdc=5
SDD:Xcc     g 0  d 0
    I[2,0] = gm*_v1 - kfb*_c1  ; drain current: gm·Vgs minus feedback on supply current
    C[1]   = Vdd               ; _c1 = current drawn from the 5 V supply
    gm     = 0.05
    kfb    = 0.1
```

**(c) Control current on the reactive path.** `_cn` can appear in any `I[p,w]`, including the
`H[1]=jω` charge path — here a current sensed in port 2 of a Touchstone block drives a
displacement-like term:

```netlist
SnP:S1      in 0 out 0   File=coupler.s2p
SDD:Xq      g 0  d 0
    I[2,1] = tau*_c1           ; charge ∝ sensed current → current = d/dt(tau·_c1) via H[1]=jω
    C[1]   = S1
    Cport[1] = 2               ; _c1 = current in port 2 of the S-parameter block
    tau    = 1n
```

## Parameter summary {#params}

<table class="param-table"><thead><tr><th>Name</th><th>Meaning</th></tr></thead>
    <tbody>
      <tr><td>NumPorts</td><td>Number of ports N (drives the 2N differential pins). Hidden.</td></tr>
      <tr><td>I[p,w]</td><td>Port-p current contribution through weight w. <code>I[p]</code>=<code>I[p,0]</code> (current); <code>Q[p]</code>=<code>I[p,1]</code> (charge).</td></tr>
      <tr><td>H[w]</td><td>User weighting function (w ≥ 2), an expression of <code>freq</code>. (<code>H[0]=1</code>, <code>H[1]=jω</code> are built in.)</td></tr>
      <tr><td>C[n] / Cport[n]</td><td>Bind control current <code>_cn</code> to another device's (port's) current.</td></tr>
      <tr><td><em>(scope vars)</em></td><td>Any named model coefficients you reference in the equations.</td></tr>
    </tbody>
  </table>

## Which analyses honor each feature {#analyses}

**Every simulation type honors the SDD** — DC, S-parameters, Harmonic Balance, Loadpull, and
Loadpull Pursuit. Loadpull and Loadpull Pursuit are built on the harmonic-balance engine, so the
full `I[p,w]` / `Q[p]` / `H[w]` behavior applies to the DUT at every termination they evaluate.

- **Nonlinear DC** — full `I[p,w]`, charge (drops at DC), and control currents (exact — the
  referenced current is a solver unknown).

- **Harmonic Balance** — full weighting sum, with the control-current Jacobian coupling for fast
  convergence.

- **Loadpull & Loadpull Pursuit** — the SDD is balanced by HB at each termination, so all SDD
  behavior is honored throughout the sweep/search.

- **S-parameters** — small-signal linearization at the DC bias, including the control-current
  coupling. A nonlinear capacitor reduces to `jω·C(0)`, matching NonlinearC.

<p class="small">Note on control currents specifically: the <code>_cn</code> control-current path is wired for
  single-tone HB; multi-tone / two-tone control-current coupling is a follow-on. The SDD's own current/charge
  equations are honored in all analyses regardless.</p>

---

<p class="small">See also: <a href="components.html#sdd">Components › SDD</a> (the symbol + at-a-glance
  parameters) · <a href="dynamic-symbols.html#sdd">Dynamic symbols</a> (how the pin count grows) ·
  <a href="expressions.html">Expressions</a> (the language) · <a href="netlist.html">Netlist format</a> (a
  worked GaN-FET SDD). Full design: <code>docs/design/sdd.md</code> + <code>docs/design/sdd-control-current.md</code>.</p>

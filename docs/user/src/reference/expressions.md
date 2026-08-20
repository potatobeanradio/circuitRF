---
title: Expressions
slug: reference/expressions.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Expressions
lede: One expression language runs everywhere a value can be computed: global variables, component parameters, SDD device equations, and measurements. Anywhere you can type a number, you can type an expression — `2*pi*freq0`, `polar(0.1, 35)`, `if(Vg > Vth, gm*Vg, 0)`.
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#where">Where expressions are used</a></li>
      <li><a href="#values">Values: Real, Complex, Bool, String</a></li>
      <li><a href="#operators">Operators &amp; precedence</a></li>
      <li><a href="#constants">Constants &amp; functions</a></li>
      <li><a href="#units">Units</a></li>
      <li><a href="#vars">Variables, scope &amp; cell parameters</a></li>
      <li><a href="#functions">User-defined functions</a></li>
      <li><a href="#cycles">Cycle detection &amp; errors</a></li>
    </ol>
  </nav>

## Where expressions are used {#where}

<table>
    <thead><tr><th>Consumer</th><th>What it evaluates</th><th>Result</th></tr></thead>
    <tbody>
      <tr><td>Global variables (VAR)</td><td>a testbench variable</td><td>Real or Complex</td></tr>
      <tr><td>Cell parameters</td><td>a parameter default or an instance override</td><td>Real or Complex</td></tr>
      <tr><td>SDD device equations</td><td><code>i = f(v)</code>, <code>q = f(v)</code> over port voltages</td><td>Real (and its derivatives)</td></tr>
      <tr><td><a href="measurements.html">Measurements</a></td><td>a figure of merit over result cubes</td><td>Real or Complex</td></tr>
    </tbody>
    <caption>Same grammar, operators, and functions everywhere; only the available <em>operands</em> differ.</caption>
  </table>

circuitRF parses an expression once into a tree and evaluates it against a scope — it never does
text substitution. That makes it fast (an SDD equation is parsed once, evaluated thousands of
times) and correct (no fragile longest-name-first variable replacement).

## Values: Real, Complex, Bool, String {#values}

Every value carries a **kind**:

- **Real** — a plain number. Component values like `R`, `L`, `C` are Real (`50` stays `50`, not
  `50+j0`).

- **Complex** — appears as soon as the imaginary unit `j` enters, or from an operation that is
  mathematically complex (e.g. `sqrt` of a negative). Impedances resolve here. Write imaginary
  values with `j*`: `j*4`, `2 + j*3`.

- **Bool** — the result of a comparison or logical operator; used only as a condition in `if` /
  `?:`. A parameter that resolves to Bool is an error (a condition isn't a component value).

- **String** — a double-quoted literal (`"spline"`, `"path/to/x.s2p"`). Storage only — no string
  operators. Used for genuinely textual parameters (the SnP block's `File`, `InterpMode`, …).

<div class="callout note">
    <span class="label">Ordering needs real operands</span>
    <p><code>&lt; &lt;= &gt; &gt;=</code> require Real operands (complex numbers are unordered); <code>==</code> /
    <code>!=</code> work on Real and Complex. Real ∘ Complex promotes to Complex.</p>
  </div>

## Operators & precedence {#operators}

Lowest-binding (evaluated last) to highest-binding (tightest):

<table>
    <thead><tr><th>Operators</th><th>Notes</th></tr></thead>
    <tbody>
      <tr><td><code>?:</code> (ternary)</td><td><code>cond ? a : b</code> — same as <code>if(cond, a, b)</code></td></tr>
      <tr><td><code>||</code></td><td>logical OR</td></tr>
      <tr><td><code>&amp;&amp;</code></td><td>logical AND</td></tr>
      <tr><td><code>==</code> <code>!=</code></td><td>equality (Real or Complex)</td></tr>
      <tr><td><code>&lt;</code> <code>&lt;=</code> <code>&gt;</code> <code>&gt;=</code></td><td>ordering (Real only)</td></tr>
      <tr><td><code>+</code> <code>-</code> (binary)</td><td></td></tr>
      <tr><td><code>*</code> <code>/</code></td><td></td></tr>
      <tr><td><code>+</code> <code>-</code> <code>!</code> (unary prefix)</td><td></td></tr>
      <tr><td><code>^</code> (power)</td><td>binds tighter than unary minus: <code>-2^2 == -4</code>; right-assoc: <code>2^3^2 == 2^(3^2)</code></td></tr>
      <tr><td>function call, <code>( )</code>, atoms</td><td>highest</td></tr>
    </tbody>
  </table>

## Constants & functions {#constants}

**Constants:** `j = (0,1)`, `pi`, `e`. These (and all function names) are reserved — a variable
may not shadow them.

<table>
    <thead><tr><th>Group</th><th>Functions</th></tr></thead>
    <tbody>
      <tr><td>Trig</td><td><code>sin cos tan asin acos atan atan2(y,x)</code></td></tr>
      <tr><td>Hyperbolic</td><td><code>sinh cosh tanh</code></td></tr>
      <tr><td>Exp / log / power</td><td><code>exp log</code> (natural) <code>log10 sqrt pow(x,y) abs</code></td></tr>
      <tr><td>Misc</td><td><code>min(a,b) max(a,b) sign(x)</code>, <code>if(cond,then,else)</code></td></tr>
      <tr><td>Complex → Real</td><td><code>real(z) imag(z) abs(z) mag(z)</code> (= abs) <code>phase(z)</code> (degrees) <code>phase_rad(z)</code> (radians)</td></tr>
      <tr><td>Real,Real → Complex</td><td><code>polar(mag, phase_deg)</code> — e.g. <code>polar(0.1, 10)</code> is 0.1∠10°</td></tr>
    </tbody>
    <caption><code>phase</code> and the <code>.phase</code> cube transform both use <strong>degrees</strong>.
    <code>dB</code>/<code>dBm</code> are <a href="measurements.html">measurement</a> functions, not general
    built-ins (and so are never unit suffixes).</caption>
  </table>

## Units {#units}

A unit attaches at the **assignment** level (after the expression), and scales the value by a
linear factor — `L = L1 nH`, `Z = 50 Ohm`, `M = 0.5 pH`. Units are not part of the expression
grammar.

<table>
    <thead><tr><th>Domain</th><th>Units</th></tr></thead>
    <tbody>
      <tr><td>SI prefixes</td><td><code>T G M k m u n p f</code> (1e12 … 1e-15)</td></tr>
      <tr><td>Frequency</td><td><code>Hz kHz MHz GHz THz</code></td></tr>
      <tr><td>Inductance</td><td><code>H mH uH nH pH fH</code></td></tr>
      <tr><td>Capacitance</td><td><code>F mF uF nF pF fF</code></td></tr>
      <tr><td>Resistance</td><td><code>Ohm kOhm MOhm</code></td></tr>
      <tr><td>Length</td><td><code>m mm um mil</code></td></tr>
      <tr><td>Angle</td><td><code>deg rad</code></td></tr>
    </tbody>
  </table>

<div class="callout warn">
    <span class="label">dB and dBm are not units</span>
    <p>They are logarithmic, not linear scale factors, so they are <em>functions</em> — <code>dB(...)</code>,
    <code>dBm(...)</code> — never a trailing unit on a value.</p>
  </div>

## Variables, scope & cell parameters {#vars}

Global variables (authored in a [VAR](components.html#var) block) are visible everywhere. Cell
parameters pass **top-down**: an instance binds overrides in the *parent* scope; the cell
evaluates its own defaults and component values in its *own* scope, then passes its scope down to
its sub-cells. A name resolves to the local cell variable/parameter first, then the global — a
cell never sees a parent's locals or a sibling's, which is what keeps a cell's meaning independent
of where it's placed.

<div class="callout note">
    <span class="label"><code>freq</code> is reserved</span>
    <p>Lowercase <code>freq</code> is the simulator's current stamping frequency (Hz), injected when the engine
    evaluates a frequency-dependent value (a <code>Z_Port</code> impedance, an SDD <code>H[w]</code>). You can't
    name a variable <code>freq</code>. It is distinct from a source's capital-<code>Freq</code> tone parameter.</p>
  </div>

## User-defined functions {#functions}

Define a function whose body is an expression in the same language, with any number of parameters:

```text
gm(vgs, vth) = beta * tanh(vgs - vth)
```

A call binds arguments positionally into a fresh scope whose parent is the definition's scope (so
a user function sees globals, not the caller's locals). User functions compose with built-ins and
with each other, and are subject to the same cycle detection.

## Cycle detection & errors {#cycles}

circuitRF detects and rejects dependency **cycles** across the whole graph — global variables,
cell-parameter defaults, and overrides — and reports the chain (e.g. `a → b → a`) rather than
hanging. Every error names the offending text, never a silent zero or NaN:

- **Cycle** — the dependency chain.

- **Unresolved name** — the name and the scope it was sought in.

- **Type error** — Bool where a number is required; ordering on a complex operand; a non-Bool `if`
  condition.

- **Arity / unknown function** — wrong argument count or an undefined function.

- **Domain** — `log(0)`, `sqrt` of a negative in a real SDD context, division by zero (reported
  with context).

<div class="callout note">
    <span class="label">SDD equations are real-only</span>
    <p>SDD device equations operate on real time-domain voltages and produce real current/charge, so <code>j</code>
    is disallowed there and <code>sqrt</code>/<code>log</code> of a negative is a domain error rather than a
    promotion to Complex. circuitRF differentiates SDD equations automatically (forward-mode AD) to get the
    solver's conductance/capacitance — including through an <code>if</code>, where it differentiates the branch
    that is taken. For tough convergence prefer soft switching (<code>tanh</code>) over a hard <code>if</code>.
    See <a href="sdd.html">The SDD</a>.</p>
  </div>

---

<p class="small">See also: <a href="measurements.html">Measurements</a> · <a href="sdd.html">The SDD</a> ·
  <a href="components.html#var">VAR component</a>. Full design: <code>docs/design/expressions.md</code>.</p>

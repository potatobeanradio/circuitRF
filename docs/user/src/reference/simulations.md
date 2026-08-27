---
title: Simulations
slug: reference/simulations.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Simulations
lede: Every analysis circuitRF runs, what it computes, a short overview of the method, and the full set of settings. Algorithm details are deferred to the design notes / white papers; this chapter is the operational reference.
---

<nav class="toc">
    <h2>Analyses</h2>
    <ol>
      <li><a href="#dc">DC (operating point)</a></li>
      <li><a href="#s-parameters">S-Parameters</a></li>
      <li><a href="#harmonic-balance">Harmonic Balance</a></li>
      <li><a href="#parametric-sweep">Parametric Sweep</a></li>
      <li><a href="#loadpull">Loadpull / Sourcepull</a></li>
      <li><a href="#loadpull-pursuit">Loadpull Pursuit</a></li>
      <li><a href="#two-tone">Multi-Tone Harmonic Balance</a></li>
      <li><a href="#cli">Running an analysis from the command line</a></li>
    </ol>
  </nav>

<div class="callout note">
<span class="label">How results are stored</span>
<p>Every analysis returns a <strong>DataSet</strong> — a named bundle of labelled, unit-bearing arrays
(<strong>DataCube</strong>s), each a single kind, real or complex. A whole run's DataSets go into
<strong>one</strong> file in the workspace's shared <code>results/</code> folder, and
<strong>you name that file</strong> in the <strong>Results file</strong> field above the analyses list;
leaving it blank uses the schematic's own name. This is what the
<a href="data-display.html">Data Display</a> plots and what <code>.npy</code> / <code>.mat</code> export
produces. Full detail: <a href="npy-export.html#where">Where results live</a>.</p>
</div>

## DC (operating point) {#dc}

**Computes:** the steady-state node voltages and branch currents with no signal applied — the bias
point. For nonlinear circuits this is the prerequisite to harmonic balance.

**Method:** modified nodal analysis (MNA); nonlinear devices solved by Newton–Raphson with *gmin*
and source stepping for convergence robustness.

<table class="param-table">
    <thead><tr><th>Setting</th><th>Meaning</th></tr></thead>
    <tbody>
      <tr><td>Tol</td><td>Newton convergence tolerance on the residual.</td></tr>
      <tr><td>MaxIter</td><td>Maximum Newton iterations.</td></tr>
      <tr><td>gmin / source stepping</td><td>Continuation aids that ease a hard bias point into convergence.</td></tr>
    </tbody>
  </table>

<p class="small">Read currents directly with an <a href="components.html#iprobe">IProbe</a> in series.</p>

## S-Parameters {#s-parameters}

**Computes:** the linear, small-signal multiport network parameters over a frequency sweep — the
S-matrix at each frequency, with renormalization to the port reference impedances. Ports are
defined by [Term](components.html#term) components (or [P1Tone](components.html#p1tone) port
numbers).

**Method:** complex MNA solved per frequency point; each port is excited in turn and the scattered
waves are extracted and renormalized. Embedded [Touchstone (SnP) blocks](components.html#snp) are
interpolated onto the sweep.

<table class="param-table">
    <thead><tr><th>Setting</th><th>Meaning</th></tr></thead>
    <tbody>
      <tr><td>Frequency sweep</td><td>Start / Stop / Step (or point count); one or more segments. Units accepted (<code>GHz</code>, <code>MHz</code>, …).</td></tr>
      <tr><td>Reference impedance</td><td>Per-port reference (from each Term's <code>Z</code>); the result renormalizes to it.</td></tr>
    </tbody>
  </table>

<p class="small">From the CLI: <code>circuitrf sparam circuit.cnl --freq 1GHz:10GHz:50MHz -o out.s2p</code>.</p>

## Harmonic Balance {#harmonic-balance}

**Computes:** the steady-state response of a nonlinear circuit driven by one or more tones (up to
six) — the spectrum (DC + harmonics, and mixing products when there is more than one tone) at
every node. From it come Pout, gain, efficiency, PAE, compression, and intermodulation.

**Method:** the circuit is split into a *linear* sub-network (solved in the frequency domain) and
*nonlinear* devices (evaluated in the time domain and transformed by FFT). A multidimensional
Newton solve drives the harmonic-balance residual — frequency-domain KCL — to zero, using a
conversion-matrix Jacobian. Power/source **continuation** ramps the drive to reach convergence
deep into compression. [Multi-tone analysis](#two-tone) uses a diamond-truncated mixing spectrum.

<table class="param-table">
    <thead><tr><th>Setting</th><th>Meaning</th><th>Typical</th></tr></thead>
    <tbody>
      <tr><td>Tone</td><td>The fundamental frequency (a value or a variable such as <code>RFfreq</code>). Two or more tones for intermodulation.</td><td>2 GHz</td></tr>
      <tr><td>ToneUnit</td><td>Unit for the tone value when it doesn't carry its own (a unit on the referenced variable wins).</td><td>GHz</td></tr>
      <tr><td>MaxHarm</td><td>Highest harmonic order retained (5 → DC plus 5 harmonics). Higher captures sharper nonlinearity at more cost.</td><td>5–7</td></tr>
      <tr><td>FFTOverSample</td><td>Oversampling factor for the time grid used by the nonlinear FFT. Raise to reduce aliasing on stiff devices.</td><td>1–2</td></tr>
      <tr><td>Tol</td><td>Newton convergence tolerance on the HB residual.</td><td>1e-6</td></tr>
      <tr><td>DriveStepping</td><td>Power/source continuation: <code>IfNecessary</code> (ramp only when a direct solve stalls), or always/never.</td><td>IfNecessary</td></tr>
      <tr><td>GuardHarmonic</td><td>Extra guard harmonic(s) beyond <code>MaxHarm</code> for anti-alias safety.</td><td>0</td></tr>
      <tr><td>Lambda</td><td>Newton damping factor (1 = full step; lower damps an unstable solve).</td><td>1</td></tr>
      <tr><td>MaxIter</td><td>Maximum Newton iterations per solve point.</td><td>100</td></tr>
    </tbody>
    <caption>Multi-tone analysis adds the extra tones and a mixing order (≥ 5 at two tones, to capture close-in IM products; lower it as tones are added).</caption>
  </table>

## Parametric Sweep {#parametric-sweep}

**Computes:** nothing new on its own — it *wraps* another analysis and re-runs it across one or
more variables (drive power, a bias voltage, frequency, any user variable). Nest sweeps to cover
several variables; the innermost analysis is what each point solves.

<table class="param-table">
    <thead><tr><th>Setting</th><th>Meaning</th></tr></thead>
    <tbody>
      <tr><td>Var</td><td>The variable to sweep (e.g. <code>Pin</code>, <code>RFfreq</code>).</td></tr>
      <tr><td>Start / Stop</td><td>Sweep endpoints (units accepted).</td></tr>
      <tr><td>Step <em>or</em> Npts</td><td>Step size, or a point count; linear or logarithmic.</td></tr>
      <tr><td>Inner</td><td>The analysis (or inner sweep) this sweep wraps — how sweeps nest.</td></tr>
    </tbody>
    <caption>A whole sweep tree writes a single results file named after its innermost analysis.</caption>
  </table>

## Loadpull / Sourcepull {#loadpull}

**Computes:** figures of merit (Pout, gain, efficiency, PAE) as the load (or source) reflection
coefficient is swept over a grid on the Smith chart — the classic loadpull experiment — ready to
draw as **contours**. Harmonic terminations can be swept too (harmonic loadpull).

**Method:** a programmable [Tuner](components.html#tuner) presents each grid termination; a
harmonic-balance solve runs per point (with previous-point continuation for convergence); the FOMs
are collected into the DataSet over the Γ-grid. The Data Display fits a surface and extracts
contour lines; measured loadpull files (`.spl`/`.lpcwave`) plot identically.

<table class="param-table">
    <thead><tr><th>Setting</th><th>Meaning</th></tr></thead>
    <tbody>
      <tr><td>Tuned port / role</td><td>Load or source side, and which DUT port the tuner terminates.</td></tr>
      <tr><td>Γ grid</td><td>The set of reflection-coefficient points (a Smith-chart grid, typically within \|Γ\| ≤ 0.9).</td></tr>
      <tr><td>Tuned harmonic</td><td>Fundamental, or a specified harmonic for harmonic loadpull.</td></tr>
      <tr><td>Power / bias</td><td>The drive level and bias at which the pull is performed (often a power sweep too).</td></tr>
    </tbody>
  </table>

### The termination grid file (`.gam`) {#gam-file}

The grid of terminations a loadpull visits lives in a small, human-readable text file with a
`.gam` extension — one termination per line. It is kept *out* of the netlist on purpose: a grid is
**data** (often hundreds of points), and a real tuner system works the same way, reading a
"pattern file" of impedance states to visit. A loadpull analysis **points its grid at a `.gam`
file**; a [Loadpull Pursuit](#loadpull-pursuit) can **write one** for you (its recommended
terminations).

Each point is either an **impedance** (Ω) or a **reflection coefficient Γ**, in one of three
column layouts. An optional header line (starting `#`) declares the form, the reference impedance,
and the layout; if you omit it, circuitRF assumes **impedance** and infers the layout from the
first data line. The parser is deliberately forgiving — blank lines and `;` or `#` comment lines
are skipped.

<table class="param-table">
    <thead><tr><th>Header token</th><th>Meaning</th><th>Default</th></tr></thead>
    <tbody>
      <tr><td><code>gamma</code> / <code>impedance</code></td><td>Values are reflection coefficients Γ, or impedances in Ω.</td><td><code>impedance</code></td></tr>
      <tr><td><code>Z0=&lt;value&gt;</code></td><td>Reference impedance for the Γ↔Z conversion.</td><td>50 Ω</td></tr>
      <tr><td><code>re_im</code></td><td>Two columns: real, imaginary.</td><td rowspan="3">inferred from the first line: a <code>j</code>/<code>i</code> marker ⇒ <code>re+j*imag</code>, else <code>re_im</code></td></tr>
      <tr><td><code>mag_ang</code></td><td>Two columns: magnitude, then angle in <strong>degrees</strong>.</td></tr>
      <tr><td><code>re+j*imag</code></td><td>One column, a complex literal (e.g. <code>80+j*10</code>, <code>0.5-j*0.3</code>).</td></tr>
    </tbody>
    <caption>Header tokens are case-insensitive and may appear in any order, e.g. <code># gamma Z0=50 mag_ang</code>.</caption>
  </table>

A Γ grid in magnitude/angle form (rings on the Smith chart):

```netlist
# gamma Z0=50 mag_ang
; concentric rings of load reflection coefficient
0.00    0
0.20    0
0.20   90
0.40    0
0.40   90
0.40  180
0.60    0
0.80   30
```

<div class="callout note">
    <span class="label">Multi-frequency grids</span>
    <p>One <code>.gam</code> file can hold a <strong>different grid per frequency</strong>, for frequency-swept
    loadpull. Start each block with a bare <code>freq=&lt;value&gt;&lt;unit&gt;</code> line
    (<code>Hz</code>/<code>kHz</code>/<code>MHz</code>/<code>GHz</code>/<code>THz</code>); every point below it
    belongs to that frequency until the next <code>freq=</code> line. circuitRF reads the block whose frequency
    is <strong>nearest</strong> the one being simulated. A file with no <code>freq=</code> line is a single grid
    used at <em>every</em> frequency.</p>
    <pre class="netlist"><code># impedance Z0=50 re+j*imag
freq=1.8GHz
80+j*10
60-j*5
freq=2.2GHz
85+j*5
70+j*0</code></pre>
  </div>

**Using one.** In the Loadpull editor, set the grid field to your `.gam` file (paths are resolved
relative to the netlist). **Getting one.** A Loadpull Pursuit with an `OutputGrid` path writes its
recommended terminations as a `.gam` — always in `impedance … re+j*imag` form, with a generated-by
header and a `# WARNING:` line if any near-non-convergent points were dropped. That output reads
straight back into a standard loadpull, including the multi-frequency layout for a frequency-swept
pursuit.

## Loadpull Pursuit {#loadpull-pursuit}

**Computes:** the load (or source) terminations that optimize a figure of merit — found
*automatically* by a query-minimizing search rather than by gridding the whole Smith chart. It
reports two optima at constant compression — **MXP** (maximum output power) and **MXE** (maximum
efficiency) — recommends a conjugate-match **source impedance** for each, and can run a focused
high-fidelity loadpull around them, all from one analysis.

**Why use it:** it automates the real-world PA loadpull procedure — find the optimum, then
loadpull a focused grid around it with a sensible source match — into a single unattended run.
Point it at a DUT whose optimal terminations are unknown, run, and come back to a complete
loadpull dataset already concentrated at the right terminations. It is repeatable (great for
characterizing many devices with one netlist), avoids wasted query points far from the optima, and
avoids non-convergent terminations.

**It complements, it doesn't replace, a standard loadpull.** Pursuit finds the useful terminations
and the region of interest; a standard loadpull then shows the performance tradeoffs across that
region. Pursuit shares all [Loadpull](#loadpull) settings *except the Γ grid* (it generates
terminations rather than reading them), plus the keys below.

### How a pursuit run works

<ol class="steps">
    <li><strong>Search</strong> — a steepest-ascent search in the VSWR plane finds MXP, then MXE (seeded from
      MXP, so the second search is cheap). Each "query" is one drive-to-compression harmonic-balance run;
      queries are cached so MXE reuses MXP's data.</li>
    <li><strong>Recommend</strong> — builds a focused-around-the-optima set of recommended terminations (dense
      near MXP/MXE, sparse further out) and a conjugate-match source impedance for each optimum.</li>
    <li><strong>Loadpull</strong> — optionally runs a standard loadpull over those recommended terminations,
      using a recommended source match, producing the high-fidelity contour data.</li>
  </ol>

### Search method

- **SteepestAscent** (default) — fits the gradient and ascends along a fixed direction, shrinking
  the step on rejection; a final polynomial refinement pins the optimum. Fast and adequate for the
  near-1-D real-axis optima typical of PA loadpull.

- **IteratedQuadratic** — a trust-region search that re-fits the local curvature at every step, so
  it follows a curved 2-D ridge instead of committing to one direction. Reuses the same query cache,
  so its query count stays comparable.

### Pursuit settings

<table class="param-table">
    <thead><tr><th>Key</th><th>Meaning</th><th>Default</th></tr></thead>
    <tbody>
      <tr><td>EffType</td><td>The MXE (efficiency) criterion: drain efficiency <code>DE</code> or <code>PAE</code>.</td><td>DE</td></tr>
      <tr><td>SearchMethod</td><td><code>SteepestAscent</code> or <code>IteratedQuadratic</code> (above).</td><td>SteepestAscent</td></tr>
      <tr><td>ZsourceOBO</td><td>Output back-off (dB from compression) at which the input impedance is sampled for the auto-Zsource report. Granularity set by <code>PinStep</code>.</td><td>5</td></tr>
      <tr><td>VSWR1 (focused)</td><td>Focused box size (VSWR-circle radius) around MXP and MXE — the dense sampling region.</td><td>1.5</td></tr>
      <tr><td>VSWR1_resolution</td><td>Grid spacing (N×N samples) inside each focused box.</td><td>4</td></tr>
      <tr><td>VSWR2 (broad)</td><td>Broad box size (VSWR-circle radius) for the surrounding coarse grid.</td><td>3</td></tr>
      <tr><td>VSWR2_resolution</td><td>Grid spacing (N×N samples) for the broad box.</td><td>4</td></tr>
      <tr><td>keepNonconvergingPoints</td><td>If false, drop recommended points near terminations found non-convergent during the search (and warn).</td><td>false</td></tr>
      <tr><td>nonconvergentVSWR</td><td>Exclusion radius (VSWR) around known non-convergent terminations.</td><td>1.05</td></tr>
      <tr><td>OutputGrid</td><td>Path to write the recommended terminations as a <a href="#gam-file"><code>.gam</code> file</a>. Absent → no file written.</td><td>none</td></tr>
      <tr><td>CreateLoadpullResult</td><td>Run the follow-on standard loadpull over the recommended terminations. Independent of <code>OutputGrid</code>.</td><td>on</td></tr>
      <tr><td>LoadpullResultZsource</td><td>Source match the follow-on loadpull uses: <code>MXE</code>, <code>MXP</code>, or <code>None</code> (use the Source Tuner's own Z1).</td><td>MXE</td></tr>
    </tbody>
    <caption>The recommended terminations always exist in memory; <code>OutputGrid</code> controls only the file, <code>CreateLoadpullResult</code> controls only the follow-on simulation — the two are orthogonal.</caption>
  </table>

<div class="callout note">
    <span class="label">Auto-Zsource — a recommended source match</span>
    <p>After each optimum is found, pursuit backs the drive off by <code>ZsourceOBO</code> dB, computes the DUT
    input impedance Zin there, and reports <strong>Zsource = Zin*</strong> (the conjugate match) — a
    ready-to-use input-match target, per optimum, without a separate sourcepull.</p>
  </div>

<div class="callout warn">
    <span class="label">The DUT must compress</span>
    <p>MXP/MXE are defined <em>at compression</em>. If the DUT does not reach <code>Compression</code> within
    <code>PinMax</code>, the search aborts with a clear message (raise <code>PinMax</code> or check
    bias/load) — it never silently raises your <code>PinMax</code> cap.</p>
  </div>

<p class="small">Full design + algorithm: <code>docs/design/loadpull_pursuit.md</code> (in the repo).</p>

## Multi-Tone Harmonic Balance (2 to 6 tones) {#two-tone}

**Computes:** the steady-state spectrum of a nonlinear circuit driven by **several carriers at
once** — every harmonic *and* every intermodulation (mixing) product. Two tones is the standard
test for **linearity**: third-order intermodulation (IM3), intercept points, spectral regrowth,
and the asymmetry between the lower and upper IM products. Three or more tones is the natural
setting for **mixers** (RF, LO and their products) and for multi-carrier work.

Multi-tone is not a separate analysis — it is [Harmonic Balance](#harmonic-balance) with more than
one tone. Everything from the single-tone chapter (the linear/nonlinear split, the Newton solve,
drive continuation, all the convergence knobs) still applies. This chapter covers only what is
different.

Up to **six** tones are supported. Everything below is written for two because that is the common
case, but it holds at any tone count: read "(k₁,&nbsp;k₂)" as the full list
"(k₁,&nbsp;…,&nbsp;k<sub>T</sub>)". The one thing that changes with tone count is how far you can
push **Max mix order** — see [How many tones, at what order](#multi-tone-size).

### Setting it up {#two-tone-setup}

In the Harmonic Balance analysis editor, switch the **Single&nbsp;/&nbsp;Multi-tone** toggle to
**Multi-tone**. Three things change:

- The single **Tone&nbsp;(f₀)** field is replaced by a **tone list**, starting with
  **Tone&nbsp;1** and **Tone&nbsp;2**. **+&nbsp;Tone** adds another (up to six); the **×** on a row
  removes it, down to a minimum of two.

- **Max harmonics** is replaced by **Max mix order** — the truncation that bounds the
  two-dimensional mixing spectrum (see below).

- Every tone accepts an expression, so a common pattern is `Tone&nbsp;1 = RFfreq − Spacing/2`,
  `Tone&nbsp;2 = RFfreq + Spacing/2` with `RFfreq` and `Spacing` as variables you can sweep.

- Beside **Max mix order** the editor shows how many mixing products the current tone count and
  order retain, and warns when that is over the limit — so you can see the cost of an order before
  you run it.

<div class="callout note">
    <span class="label">The analysis owns the tones, not the source</span>
    <p>The fundamentals <strong>come from the HB analysis</strong> (the tone list, written as
    <code>NumFreqs=N&nbsp;Tone[1]=…&nbsp;Tone[N]=…</code> in the netlist), not from the source. A
    <a href="components.html#pntone">PnTone</a> (or <code>V_nTone</code>) just supplies power at those
    frequencies. Every source tone must land <strong>exactly</strong> on the analysis grid — an off-grid drive
    frequency is rejected at setup with a clear message rather than producing silent garbage.</p>
  </div>

### The mixing grid and mixing order {#two-tone-grid}

With two fundamentals, a spectral line is no longer indexed by a single harmonic number. Each line
is a pair of integers **(k₁,&nbsp;k₂)** sitting at the physical frequency

<p style="text-align:center"><code>f = k₁·f₁ + k₂·f₂</code></p>

and its **mixing order** is `m = |k₁| + |k₂|`. The carriers are (1,&nbsp;0) and (0,&nbsp;1) at
order 1; a third-order intermod such as 2f₁−f₂ is (2,&nbsp;−1) at order 3. Solving for every
possible (k₁,&nbsp;k₂) is infinite, so circuitRF keeps a finite set bounded by **Max mix order**:

<p style="text-align:center"><code>retain (k₁, k₂)&nbsp; ⟺ &nbsp;|k₁| + |k₂| ≤ MaxMixOrder</code></p>

That inequality is a **diamond** (a rotated square) in the (k₁,&nbsp;k₂) plane — hence "diamond
truncation." A diamond rather than a full rectangular box because the high–high corner products
(e.g. 5f₁+5f₂) carry no meaningful energy in a real intermod test, while every low-order product
that *does* matter is inside the diamond. `MaxMixOrder` is the two-tone analog of single-tone
`MaxHarm`: order 5 retains products down to IM5. The number of products grows *quadratically* with
the order (≈&nbsp;2·order² lines), so a two-tone run is much larger than a single-tone run at the
same order — order 5 yields dozens of mixing products versus a handful of harmonics.

With **T** tones the same rule reads `retain (k₁,&nbsp;…,&nbsp;k<sub>T</sub>) ⟺ |k₁| + … +
|k<sub>T</sub>| ≤ MaxMixOrder`, and the diamond becomes a T-dimensional one. There is still a
single order knob — no per-tone limits.

### How many tones, at what order {#multi-tone-size}

The retained product count is what actually costs you, and it grows steeply with tone count — much
faster than the quadratic growth two tones would suggest. The default `MaxMixOrder&nbsp;=&nbsp;5`
is sized for two tones; **lower it as you add tones**:

<table class="param-table">
    <caption>Retained mixing products. Configurations over the 600-product limit are refused, with a message
      naming the largest order that fits.</caption>
    <thead><tr><th>Tones</th><th>order 2</th><th>order 3</th><th>order 4</th><th>order 5</th></tr></thead>
    <tbody>
      <tr><td>2</td><td>7</td><td>13</td><td>21</td><td>31</td></tr>
      <tr><td>3</td><td>13</td><td>32</td><td>63</td><td>116</td></tr>
      <tr><td>4</td><td>21</td><td>65</td><td>161</td><td>341</td></tr>
      <tr><td>6</td><td>43</td><td>189</td><td>645 <em>(refused)</em></td><td>1827 <em>(refused)</em></td></tr>
    </tbody>
  </table>

Practical starting points: **order 5 at 2–3 tones**, **order 4 at 4 tones**, **order 3 at 5–6
tones**. On a single-FET PA, six tones at order 3 solves in a few seconds.

<div class="callout note">
    <span class="label">Too large is refused before the run starts, not after</span>
    <p>If the tone count and mixing order together ask for more products than the solver will take, circuitRF
    refuses <strong>immediately</strong> — not after a long solve — and the message tells you the largest
    <code>MaxMixOrder</code> that <em>would</em> work at that tone count. The editor shows the same number
    live beside the field, so you normally see it before you press Run.</p>
  </div>

<div class="callout note">
    <span class="label">Equally spaced tones put two products at the same frequency</span>
    <p>With evenly spaced carriers — the usual multi-carrier stimulus — different mixing products can land on
    the <em>same</em> physical frequency. At 1.99&nbsp;/&nbsp;2.00&nbsp;/&nbsp;2.01&nbsp;GHz, both
    (1,&nbsp;−1,&nbsp;0) and (0,&nbsp;1,&nbsp;−1) sit at 10&nbsp;MHz. They stay <strong>separate</strong>
    products with their own phasors, and the spectrum plot shows both stems at the same position — they are
    not added together. Use the marker readout, which names the product, to tell them apart.</p>
  </div>

<table class="param-table">
    <thead><tr><th>(k₁, k₂)</th><th>Order</th><th>Frequency</th><th>What it is</th><th>At f₁=1.99, f₂=2.01 GHz</th></tr></thead>
    <tbody>
      <tr><td>(0, 0)</td><td>0</td><td>0</td><td>DC / baseband</td><td>0</td></tr>
      <tr><td>(1, −1)</td><td>2</td><td>f₁ − f₂</td><td>IM2, the tone spacing (baseband)</td><td>0.02 GHz</td></tr>
      <tr><td>(1, 0)</td><td>1</td><td>f₁</td><td>Carrier 1</td><td>1.99 GHz</td></tr>
      <tr><td>(0, 1)</td><td>1</td><td>f₂</td><td>Carrier 2</td><td>2.01 GHz</td></tr>
      <tr><td>(2, −1)</td><td>3</td><td>2f₁ − f₂</td><td><strong>IM3 lower</strong></td><td>1.97 GHz</td></tr>
      <tr><td>(−1, 2)</td><td>3</td><td>2f₂ − f₁</td><td><strong>IM3 upper</strong></td><td>2.03 GHz</td></tr>
      <tr><td>(3, −2)</td><td>5</td><td>3f₁ − 2f₂</td><td>IM5 lower</td><td>1.95 GHz</td></tr>
      <tr><td>(−2, 3)</td><td>5</td><td>3f₂ − 2f₁</td><td>IM5 upper</td><td>2.05 GHz</td></tr>
      <tr><td>(2, 0)</td><td>2</td><td>2f₁</td><td>2nd harmonic of carrier 1</td><td>3.98 GHz</td></tr>
      <tr><td>(1, 1)</td><td>2</td><td>f₁ + f₂</td><td>2nd-harmonic sum band</td><td>4.00 GHz</td></tr>
    </tbody>
    <caption>The IM "order" is the sum of the integer coefficients: IM3 = |2|+|−1|, IM5 = |3|+|−2|. Choose
    closely-spaced tones (small Δ) so the IM3 and IM5 products fall right beside the carriers.</caption>
  </table>

<div class="callout note">
    <span class="label">Reading the spectrum in the Data Display</span>
    <p>A multi-tone result plotted as a spectrum uses a <strong>mixIndex</strong> axis: each stem is a mixing
    product, drawn at its physical frequency, labelled with its <strong>(k₁,&nbsp;k₂)</strong> tag — or
    <strong>(k₁,&nbsp;…,&nbsp;k<sub>T</sub>)</strong> at more tones. The spectrum is shown
    <strong>single-sided</strong> — the negative-frequency products (the conjugate halves, e.g. f₁−f₂
    and f₂−f₁) are folded onto their positive frequency, so each product appears once.</p>
    <p>Arrow keys step a marker in <strong>frequency</strong> order, which is what you want with tightly
    spaced IM products. Everything here works the same at any tone count.</p>
  </div>

### Measuring intermodulation (IMD2 / IMD3) {#two-tone-meas}

A [measurement](measurements.html) reads a mixing product out of the result by its **(k₁,&nbsp;k₂)
tag** — at three tones the tag simply has three entries, e.g. `HB1.V("Vout",&nbsp;"(1,1,-1)")` for
the triple-beat product f₁+f₂−f₃. The classic figures of merit are the intermodulation ratios in
**dBc** — an IM product relative to its carrier:

```netlist
IMD2 = dB( HB1.V("Vout", "(1,-1)") ) - dB( HB1.V("Vout", "(1,0)") )   dB
IMD3 = dB( HB1.V("Vout", "(2,-1)") ) - dB( HB1.V("Vout", "(1,0)") )   dB
```

Replace `HB1` with your analysis name and `"Vout"` with your output node. IMD2 uses the baseband
product (1,−1) = f₁−f₂; IMD3 uses the lower third-order product (2,−1) = 2f₁−f₂ (use (−1,2),
referenced to the (0,1) carrier, for the upper IM3). To get the curve *vs Pin*, sweep the drive
power (a parametric sweep over the variable your source `Pavl` references) — the same expression
then returns IMD vs Pin instead of a single value.

<div class="callout note">
    <span class="label">Two ways to address a product — use the accessor for IMD</span>
    <p>The <strong>accessor</strong> <code>HB1.V("Vout", "(1,-1)")</code> names the node and the product tag and
    <strong>keeps any swept axes automatically</strong>, so the same expression works whether or not you have
    added a Pin sweep — one value without it, a curve over Pin with it. The <strong>bracket</strong> form
    <code>HB1.V[:,&nbsp;"Vout",&nbsp;"(1,-1)"]</code> is positional: one token per cube axis (a leading
    <code>:</code> to keep the Pin sweep), so it must be re-edited when a sweep is added or removed. Prefer the
    accessor for IMD metrics you keep; reach for the bracket when copy-pasting from a trace card. See
    <a href="measurements.html#refs">Referencing analysis results</a>.</p>
  </div>

### Source impedance and harmonic terminations {#two-tone-z}

With dozens of products spread across the spectrum, each one needs a source termination. circuitRF
assigns it **by frequency**, not by mixing order. It defines a band-center fundamental — for two
tones the average `f_c = (f₁ + f₂) / 2` — and places each product in the harmonic band nearest its
frequency:

<p style="text-align:center"><code>band n = round( |f| / f_c )</code>&nbsp;→&nbsp;<code>Z[n]</code> if declared, else the shared <code>Z</code></p>

So band&nbsp;0 (|f|&nbsp;<&nbsp;f_c/2) is the baseband and uses `Z[0]`; band&nbsp;1 (around f_c)
holds both carriers and all the close-in IM3/IM5 products and uses `Z[1]` (or the default `Z`);
band&nbsp;2 (around 2·f_c) holds the second-harmonic products and uses `Z[2]`; and so on. The
decision is purely the product's frequency: as a high-order product such as (3,&nbsp;−1) drifts
down past the band-1/band-2 boundary it switches from `Z[2]` to `Z[1]` automatically — no order
threshold is hard-coded. This is the same harmonic-termination model as
[P1Tone](components.html#p1tone), and a [PnTone](components.html#pntone) presents its shared `Z`
and per-band `Z[k]` to the whole multi-tone spectrum this way.

<div class="callout note">
    <span class="label">Why baseband termination matters</span>
    <p>In single-tone HB the baseband is decoupled. In two-tone it is not — the IM2 product (1,&nbsp;−1) at
    f₁−f₂ lands at baseband, and its termination <code>Z[0]</code> feeds back into the carriers and the IM3
    levels. Studying source/load <strong>baseband-termination effects</strong> is a primary reason to run
    two-tone, so set <code>Z[0]</code> deliberately.</p>
  </div>

### What's different from single-tone {#two-tone-vs-single}

- **IM3 asymmetry.** The lower IM3 (2f₁−f₂) and upper IM3 (2f₂−f₁) generally have *different*
  levels in dBc, not just a phase difference — a direct consequence of the device's nonlinear
  I–V/Q–V behaviour and its loading. The full diamond resolves both, so the asymmetry is a real
  output of the run, not an artifact.

- **A fully coupled solve.** The two-tone result is *not* two single-tone solutions superimposed.
  The carriers, harmonics, IM products, and baseband are all unknowns in one Newton solve with a
  single conversion-matrix Jacobian — so the IM levels capture the true bidirectional coupling
  between the carriers and the baseband.

- **A bigger spectrum, a bigger cost.** Because the product count grows quadratically with
  `MaxMixOrder`, raise it only as far as you need: 5 captures IM5 (usually enough); higher captures
  IM7+ at noticeably more cost.

- **Commensurate tones.** f₁ and f₂ must be commensurate (a rational ratio) so every mixing
  product lands exactly on the grid and the waveform is exactly periodic — closely-spaced tones like
  1.99 / 2.01 GHz satisfy this naturally.

<p class="small">Full design + algorithm: <code>docs/design/harmonic-balance.md</code> and the
  harmonic-termination model in <code>docs/design/p1tone-harmonic-terminations.md</code> (in the repo).</p>


---

## Running an analysis from the command line {#cli}

Everything above is driven from the GUI's Run button, and everything above also runs headless. The
command-line driver takes a `.cnl` netlist — hand-authored, or extracted from a schematic — and runs
one analysis:

```text
circuitrf sparam <file.cnl> [--freq start:stop:step] [-o out.sNp]
circuitrf dc     <file.cnl>
circuitrf hb     <file.cnl>       harmonic balance; runs the sweep if one wraps it
circuitrf lp     <file.cnl>       loadpull over the directive's Gamma grid
circuitrf lpp    <file.cnl>       loadpull pursuit: searches for MXP / MXE
circuitrf em     <file.cem>       electromagnetic extraction of the layout it names
circuitrf elab   <file.cnl>       dump the elaborated netlist
```

From a source checkout, put `dotnet run --project src/Cli --` where `circuitrf` appears.

**The full reference for every verb, its options, its output and its exit codes is
[The Command Line](cli.html)** — including a worked EM run from an empty folder. This section covers
only the part that is about *analyses* rather than about the driver.

### Harmonic balance from the command line {#cli-hb}

```netlist
dotnet run --project src/Cli -- hb hero2.cnl -o out.mat
```

That runs the netlist's harmonic-balance analysis — **single- or multi-tone; the same verb does both**
— prints the spectra as tables, and writes `out.mat`.

| Option | What it does |
|---|---|
| `-a`, `--analysis <name>` | Which analysis to run. Optional when the file declares only one HB chain. |
| `--set <var=expr>` | Override a global variable **before elaboration**, so anything derived from it follows. Repeatable. |
| `--maxharm K` | Override `MaxHarm`. |
| `--maxmix M` | Override `MaxMixOrder` (multi-tone only). |
| `--tol t`, `--max-iter N` | Override the convergence tolerance and the iteration cap. |
| `--rows N`, `--all` | How much of each printed table to show. Default is a truncated head. |
| `--diag` | Engine convergence diagnostics to stderr. |
| `-o`, `--export <path>` | Export the results. The **extension picks the format**: `.mat`, `.npy` or `.txt`. |
| `--kits <dir>` | A folder of installed kits, for externally-supplied device models. Repeatable; valid on any verb. |

```netlist
dotnet run --project src/Cli -- hb hero5.cnl --set Pavl_dbm=0 -o hero5.txt
```

<div class="callout note">
<span class="label">The sweep runs, whichever name you give</span>
<p>When a <a href="#parametric-sweep">parametric sweep</a> wraps an analysis, <b>the sweep</b> is what
runs — and naming the inner analysis with <code>-a</code> is <strong>promoted</strong> to its outermost
enabled wrapper, with a line on stderr saying so. That promotion exists because running the inner
analysis alone gives a converged, plausible, complete-looking result with the sweep axis silently
missing. It is the same rule for <code>hb</code>, <code>lp</code> and <code>lpp</code>: a
frequency-swept loadpull has exactly that shape.</p>
</div>

### Loadpull and pursuit from the command line {#cli-lp}

```netlist
dotnet run --project src/Cli -- lp  hero3.cnl  --pin -20:1:15 -o hero3.spl
dotnet run --project src/Cli -- lpp hero3B.cnl --out-grid found.gam -o hero3B.npy
```

`lp` prints **one row per Γ grid point** — where it was, how it stopped, and its figures of merit at
the last converged, non-tickle drive step — rather than the raw `[gridPoint × driveStep]` cubes.
`lpp` prints its MXP and MXE optima first, then the follow-on grid. Both take the HB options above,
plus `--pin`, `--compression`, and one grid option each: `--grid` for `lp`, `--out-grid` for `lpp`.
`lp` also exports `.spl` and `.lpcwave`, the loadpull interchange formats the Data Display reads back.
[Full detail](cli.html#lp).

### Electromagnetic extraction from the command line {#cli-em}

```netlist
dotnet run --project src/Cli -- em Amp.cem
```

`em` takes a [`.cem` EM setup](em-setup.html) and needs no other arguments — the layout and the
technology resolve by walking up to their own workspaces, exactly as they do in the GUI. It writes the
Touchstone and the diagnostics `.npy` **where the Simulate button writes them**, so a schematic's SnP
reference survives a headless re-run. [Full detail, with a worked example](cli.html#em).

### Measurements, and why headless is the regression harness {#cli-measure}

The `measure` lines on the TestBench are evaluated exactly as the GUI evaluates them, and a
measurement that fails to evaluate is reported on stderr and the run continues. **A `.cnl` that works
headless works when opened**, which is what makes the command line usable as a regression harness:
the two paths share the elaborator, the engines and the measurement evaluator.

---

<p class="small">See also: <a href="netlist.html">the netlist format</a> (how analyses are written) ·
  <a href="components.html">Components</a> · <a href="plot-types.html">Plot types</a> ·
  <a href="measurements.html">Measurements</a> · <a href="npy-export.html">Results &amp; data export</a> ·
  <a href="cli.html">The command line</a>.</p>

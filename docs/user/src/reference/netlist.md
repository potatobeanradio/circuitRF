---
title: The Netlist (.cnl) Format
slug: reference/netlist.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > The netlist (.cnl) format
lede: A circuitRF netlist is a human-readable text description of a circuit and what to simulate. It's the same thing the schematic editor produces internally, and it's what the engine actually runs.
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#what">What a netlist is, and why you'd use one</a></li>
      <li><a href="#example">A complete example</a></li>
      <li><a href="#walk">Line-by-line walkthrough</a>
        <ol>
          <li><a href="#w-header">Header comment</a></li>
          <li><a href="#w-define">Cell definition (<code>define … end</code>)</a></li>
          <li><a href="#w-globals">Global variables</a></li>
          <li><a href="#w-components">Components &amp; instances</a></li>
          <li><a href="#w-analyses">Analyses (HB + parametric sweeps)</a></li>
          <li><a href="#w-measures">Measurements</a></li>
          <li><a href="#w-labeled">Labeled nets</a></li>
        </ol>
      </li>
    </ol>
  </nav>

## What a netlist is, and why you'd use one {#what}

Most users never write a netlist by hand — you draw a schematic and press **Run**, and circuitRF
extracts the netlist for you. But the `.cnl` file is worth understanding because:

- It is **human-readable and diff-friendly** — you can read, review, and version-control a design
  as text.

- It is the **engine's input contract**: the schematic, the CLI, and the engine all meet here. The
  headless CLI runs a `.cnl` directly.

- It's the clearest way to see exactly *what* got simulated — components, parameter values after
  resolution, analyses, and measurements, all in one place.

Syntax basics: one statement per line; `;` begins a comment (to end of line); whitespace separates
tokens; values may carry a unit (`50 Ohm`, `2 GHz`, `1 mF`); and expressions may reference
variables and parameters.

## A complete example {#example}

This is a single-FET GaN power-amplifier test bench: a harmonic-balance power sweep, swept again
over frequency, with measurements for gain, efficiency, and return loss. We'll walk through every
line below.

```netlist
; netlist.cnl — generated from TestBench "HBTest.csch"

define MyFET (gate drain)
  parameters Periphery_mm=1
  Sv = -0.837
  Sc = 0.71
  TV0 = 4.268
  TC = 1.507
  th = 0.001
  a = 0.176
  g = 0.089
  lam = 0.0012
  B = 1130
  SDD:X1  gate  0  drain  0  I[1,0]=_v1/50  I[2,0]=Periphery_mm*(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1)))+1))*ln(exp(-(2*TV0 - 2*_v1 +2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1) * (_v2*lam + 1))/2
end MyFET

Pin = 0
RFfreq = 2 GHz

C:C1  Vin  n1  C=1 mF
L:L1  n2  n3  L=1 mH
L:L2  n4  Vout  L=1 mH
R:R2  n5  0  R=80 Ohm
C:C2  n5  n6  C=1 mF
P1Tone:P1  n1  0  Pavl=Pin dBm  Z=50 Ohm  Freq=RFfreq  Phase=0 deg  Z[0]=1 Ohm  Z[2]=30 Ohm
Vdc:V1  n2  0  Vdc=-3.05 V
Vdc:V2  VDD  0  Vdc=48 V
MyFET:X1  n3  Vout  Periphery_mm=1
IProbe:Iout  Vout  n6
IProbe:Iin  Vin  n3
IProbe:IDC  VDD  n4
C:C3  Vout  0  C=0.3 pF

analysis HB1 type=hb Tone="RFfreq" ToneUnit=MHz MaxHarm=5 FFTOverSample=1 Tol=1e-6 DriveStepping=IfNecessary GuardHarmonic=0 Lambda=1 MaxIter=100
analysis HB1_sweep_Pin type=parametric_sweep Var=Pin Start=0 Stop=30 Step=1 Inner=HB1
analysis HB1_sweep_RFfreq type=parametric_sweep Var=RFfreq Start=1 Stop=3 Npts=3 Unit=GHz Inner=HB1_sweep_Pin
measure Pin_avail_dBm = Pin
measure Pin_deliv_W = real(0.5*HB1.V("Vin",1)*conj(HB1.I("Iin",1)))
measure Pin_deliv_dBm = 10*log10(Pin_deliv_W*1000)
measure IRL_dB = Pin_deliv_dBm - Pin_avail_dBm; input return loss
measure Pout_W = real(0.5*HB1.V("Vout",1)*conj(HB1.I("Iout",1)))
measure Pout_dBm = 10*log10(Pout_W*1000)
measure Gp_dB = Pout_dBm - Pin_deliv_dBm          ; power
measure Gt_dB = Pout_dBm - Pin_avail_dBm           ; transducer
measure PDC_W = real(HB1.V("VDD",0)*HB1.I("IDC",0))
measure Eff = Pout_W/PDC_W*100
labelednets VDD Vin Vout
```

## Line-by-line walkthrough {#walk}

### 1 · Header comment {#w-header}

```netlist
; netlist.cnl — generated from TestBench "HBTest.csch"
```

Any line starting with `;` is a comment. When circuitRF generates the netlist from a schematic it
stamps a provenance line naming the source test bench. Comments are ignored by the engine.

### 2 · Cell definition (`define … end`) {#w-define}

```netlist
define MyFET (gate drain)
  parameters Periphery_mm=1
  Sv = -0.837
  ...
  SDD:X1  gate  0  drain  0  I[1,0]=_v1/50  I[2,0]=Periphery_mm*(...)
end MyFET
```

A `define` block declares a reusable **cell** — here a GaN FET model named `MyFET` with two ports,
`gate` and `drain`. Inside:

- `parameters Periphery_mm=1` — the cell's **parameter interface** with a default. An instance can
  override it (and `X1` below does). Parameters pass *top-down*: the override is evaluated in the
  parent's scope, then used inside the cell.

- `Sv = -0.837`, `Sc = 0.71`, … — **local variables** (model coefficients) scoped to this cell.
  They feed the device equations.

- `SDD:X1 gate 0 drain 0 …` — a **Symbolically-Defined Device**: you write the port currents as
  equations and circuitRF differentiates them automatically for the solver. The four nets are two
  differential ports: port&nbsp;1 = `gate`–`0`, port&nbsp;2 = `drain`–`0`. In the equations, **_v1**
  and **_v2** are those two port voltages.

- `I[1,0]=_v1/50` — the current into port 1 (a simple 50&nbsp;Ω gate input); `I[2,0]=…` — the
  drain current, the GaN *I–V* law scaled by `Periphery_mm`. The index notation is `I[port, weight]`
  — weight `0` is the current itself. (The SDD equation grammar is covered under [Components ›
  SDD](components.html#sdd).)

<div class="callout note">
    <span class="label">Cells vs. the top level</span>
    <p>Everything inside <code>define … end</code> is a <em>template</em>. It isn't simulated until it's
    instanced at the top level (see <code>MyFET:X1</code> below). The lines <em>outside</em> any
    <code>define</code> block are the top-level test bench.</p>
  </div>

### 3 · Global variables {#w-globals}

```netlist
Pin = 0
RFfreq = 2 GHz
```

Top-level **global variables**, usable anywhere a value is expected. They're also **sweepable** —
and both are swept by the analyses below (`Pin` is the drive level, `RFfreq` the fundamental). A
variable may carry a unit (`2 GHz`); when it does, that unit wins wherever the variable is
referenced.

### 4 · Components & instances {#w-components}

```netlist
C:C1  Vin  n1  C=1 mF
L:L1  n2  n3  L=1 mH
...
P1Tone:P1  n1  0  Pavl=Pin dBm  Z=50 Ohm  Freq=RFfreq  Phase=0 deg  Z[0]=1 Ohm  Z[2]=30 Ohm
Vdc:V1  n2  0  Vdc=-3.05 V
Vdc:V2  VDD  0  Vdc=48 V
MyFET:X1  n3  Vout  Periphery_mm=1
IProbe:Iout  Vout  n6
...
```

Each line is `Type:Name net1 net2 … key=value …`. The first tokens after the name are the **nets**
the component's pins connect to (order = the component's pin order); the `key=value` tokens are
**parameters**. Nets named `0` are ground. Reading the key lines:

- `C:C1 Vin n1 C=1 mF` — a capacitor between nets `Vin` and `n1`. The large `1 mF` caps and `1 mH`
  chokes here are DC-block / bias-feed elements.

- `P1Tone:P1 …` — the **RF power source** driving the input. `Pavl=Pin dBm` sets the available
  power from the swept `Pin` variable; `Freq=RFfreq` takes the fundamental from `RFfreq`; `Z=50 Ohm`
  is the fundamental source impedance. `Z[0]=1 Ohm` and `Z[2]=30 Ohm` set the source termination at
  the **baseband (DC)** and **2nd-harmonic** zones — harmonic source-pull terminations.

- `Vdc:V1 n2 0 Vdc=-3.05 V` and `Vdc:V2 VDD 0 Vdc=48 V` — the gate and drain DC bias supplies (fed
  to the device through the `L` chokes).

- `MyFET:X1 n3 Vout Periphery_mm=1` — an **instance** of the `MyFET` cell: gate→`n3`,
  drain→`Vout`, with the `Periphery_mm` parameter overridden (here to the same value as the
  default).

- `IProbe:Iout Vout n6` — a 0&nbsp;V series **current probe** (ammeter). Its instance name
  (`Iout`, `Iin`, `IDC`) is how measurements read its branch current, e.g. `I("Iout", 1)` below.

### 5 · Analyses (HB + parametric sweeps) {#w-analyses}

```netlist
analysis HB1 type=hb Tone="RFfreq" ToneUnit=MHz MaxHarm=5 FFTOverSample=1 Tol=1e-6 DriveStepping=IfNecessary GuardHarmonic=0 Lambda=1 MaxIter=100
analysis HB1_sweep_Pin type=parametric_sweep Var=Pin Start=0 Stop=30 Step=1 Inner=HB1
analysis HB1_sweep_RFfreq type=parametric_sweep Var=RFfreq Start=1 Stop=3 Npts=3 Unit=GHz Inner=HB1_sweep_Pin
```

The first line is the harmonic-balance analysis; the next two **wrap** it in swept variables. Each
`analysis` line is `analysis Name type=… key=value …`. The HB settings:

<table class="param-table">
    <thead><tr><th>Setting</th><th>Meaning</th></tr></thead>
    <tbody>
      <tr><td>Tone</td><td>The fundamental tone — here the variable <code>RFfreq</code>.</td></tr>
      <tr><td>ToneUnit</td><td>Unit applied to the tone value when it doesn't carry its own (a unit on the variable wins).</td></tr>
      <tr><td>MaxHarm</td><td>Highest harmonic order kept in the spectrum (5 → DC + 5 harmonics).</td></tr>
      <tr><td>FFTOverSample</td><td>Time-grid oversampling factor for the nonlinear FFT (1 = minimum).</td></tr>
      <tr><td>Tol</td><td>Newton convergence tolerance on the harmonic-balance residual.</td></tr>
      <tr><td>DriveStepping</td><td>Power/source continuation: <code>IfNecessary</code> ramps the drive only when a direct solve won't converge.</td></tr>
      <tr><td>GuardHarmonic</td><td>Extra guard harmonic(s) beyond <code>MaxHarm</code> for aliasing safety (0 = none).</td></tr>
      <tr><td>Lambda</td><td>Newton damping factor (1 = full step).</td></tr>
      <tr><td>MaxIter</td><td>Maximum Newton iterations per solve point.</td></tr>
    </tbody>
    <caption>HB settings are detailed in <a href="simulations.html#harmonic-balance">Simulations › Harmonic Balance</a>.</caption>
  </table>

The two `parametric_sweep` lines compose by `Inner=`: `HB1_sweep_Pin` runs `HB1` at each `Pin`
from 0 to 30 dBm in 1-dB steps (31 points); `HB1_sweep_RFfreq` runs *that whole power sweep* at
each of 3 frequencies from 1 to 3 GHz (`Npts=3`, `Unit=GHz`). The result is a 3-frequency ×
31-power harmonic-balance sweep — frequency is the outer (slow) axis, power the inner.

<div class="callout note">
    <span class="label">Sweeps wrap, they don't replace</span>
    <p>A <code>parametric_sweep</code> never changes the inner analysis; it re-runs it across a variable.
    Nest them with <code>Inner=</code> to sweep more than one variable. The innermost non-sweep analysis
    (<code>HB1</code>) is what each point actually solves.</p>
  </div>

### 6 · Measurements {#w-measures}

```netlist
measure Pin_avail_dBm = Pin
measure Pin_deliv_W = real(0.5*HB1.V("Vin",1)*conj(HB1.I("Iin",1)))
measure Pout_W = real(0.5*HB1.V("Vout",1)*conj(HB1.I("Iout",1)))
measure PDC_W = real(HB1.V("VDD",0)*HB1.I("IDC",0))
measure Eff = Pout_W/PDC_W*100
measure IRL_dB = Pin_deliv_dBm - Pin_avail_dBm; input return loss
```

A `measure` line computes a named result from an expression, evaluated at every sweep point.
Expressions use the same engine as everywhere else, plus accessors into the run's data:

- `HB1.V("Vin", 1)` — the voltage at net `Vin` from analysis `HB1`, at **harmonic 1** (the
  fundamental). `HB1.I("Iin", 1)` is the current through probe `Iin` at the fundamental. Harmonic
  `0` is DC — used for `PDC_W`.

- `real(0.5·V·conj(I))` is average power into a port; `Pout_W`/`Pin_deliv_W` apply it at the
  output and input. `Eff` is drain efficiency in percent.

- Measurements can reference *earlier* measurements (`IRL_dB` uses `Pin_deliv_dBm` and
  `Pin_avail_dBm`).

- A trailing `; text` on a measure line is a comment/label (e.g. `; input return loss`).

Results land in the run dataset and plot like any other signal. (Authored on a schematic, these
come from a **MEAS** component — see [Components › MEAS](components.html#meas).)

### 7 · Labeled nets {#w-labeled}

```netlist
labelednets VDD Vin Vout
```

Records which nets the user explicitly named (versus auto-numbered nodes like `n1`). The Data
Display uses this to default its node picker to the meaningful nets, so you see `VDD`, `Vin`, and
`Vout` first rather than every internal node.

---

<p class="small">See also: <a href="simulations.html">Simulations</a> for the full analysis settings and
  algorithms · <a href="components.html">Components</a> for each part and its parameters ·
  <a href="../new-user-guide/index.html">New User's Guide</a> for a gentler introduction.</p>

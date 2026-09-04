---
title: Compiled Verilog-A Models
slug: reference/veriloga.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Verilog-A
lede: Running a compact model you supply — a published physics-based transistor model, or one you wrote — from Verilog-A source or from a compiled artefact, with no kit and nothing to install inside circuitRF.
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#what">What this component is for</a></li>
      <li><a href="#compiler">The compiler, and why it is yours</a></li>
      <li><a href="#walkthrough">A worked example, end to end</a></li>
      <li><a href="#terminals">Terminals, <code>Pins</code>, and the thermal one</a></li>
      <li><a href="#parameters">Loading a fitted parameter set</a></li>
      <li><a href="#rebuild">When circuitRF rebuilds, and where the artefact goes</a></li>
      <li><a href="#limits">What is not supported</a></li>
    </ol>
  </nav>

## What this component is for {#what}

{{symbol: verilog-a}}

The **VerilogA** component runs a compact model **you** supply. It needs no kit, no manifest and
nothing installed into circuitRF — you point it at a file and place it.

That is the path to take when the device you need is a published physics-based compact model. Those
are distributed as Verilog-A source: a few thousand lines of `analog` equations, a manual, and one
or more fitted parameter sets. What this page buys you is the whole of that route — source in, a
placed five-terminal device out, running DC, S-parameters and harmonic balance like any other
component.

<div class="callout note">
<span class="label">Two file types, one component</span>
<p>The <code>File</code> parameter takes either <b>Verilog-A source</b> (<code>.va</code>,
<code>.vams</code>) or an <b>already-compiled model</b> (<code>.osdi</code>). Source is compiled once,
by the compiler on your machine, and reused. If you already have a compiled artefact, point at it and
no compiler is involved at all.</p>
</div>

## The compiler, and why it is yours {#compiler}

circuitRF does not contain a Verilog-A compiler and never will. The established compilers for this
model format are GPL-3.0; circuitRF is MIT, and bundling or linking one would change that. So
circuitRF does what a build system does with a C compiler: it **runs a compiler you installed**, as a
separate program, and loads the file that comes out. Nothing is linked and nothing is redistributed.

**Install one of your choosing**, then either put it on `PATH` — in which case there is nothing to
configure — or name it explicitly:

> **Settings ▸ Security & Permissions ▸ Verilog-A Compiler**

Leave the box blank to use whatever is on `PATH`. Fill it in when you have more than one, or when
yours lives somewhere `PATH` does not reach. **A compiler you name wins over `PATH`** — that is the
whole point of naming one. Press **Test** and circuitRF will run it and report what it says it is,
which is the fastest way to confirm the setting before you need it.

For a headless run — a script, CI, a batch job — set `CRF_VERILOGA_COMPILER` to the compiler's path.
It outranks `PATH` and leaves nobody's settings file touched.

If no compiler can be found, circuitRF says so at the moment you choose the file, names what to
install and where to point it, and changes nothing else. The `.osdi` route keeps working.

## A worked example, end to end {#walkthrough}

This is the whole path for a model you have just downloaded. It assumes only that you have the
model's source and one of its parameter sets — which is what a model family actually ships.

<ol class="steps">
  <li><b>Put the source where you want it.</b> Keep the family's own folder layout intact: models of
    this size <code>`include</code> parameter and macro files beside the source, and circuitRF passes
    the source's own directory to the compiler as an include path so those resolve exactly as they do
    when you build by hand.</li>

  <li><b>Place a VerilogA component.</b> It is in the palette under <b>Devices</b> — search for
    "Verilog-A", "OSDI" or "compact model".</li>

  <li><b>Set <code>File</code>.</b> Click <i>Browse…</i> beside the <code>File</code> row and choose
    the <code>.va</code>. circuitRF compiles it — once, taking a few seconds for a model of this size
    — and then reads it. <b>The parameter editor tells you underneath which compiler ran, where
    the artefact was written, and whether anything was actually rebuilt</b>; on every later visit
    it will say the source has not changed.
    <p>If the compiler refuses, you get <b>its own diagnostics, verbatim</b>: file, line and column,
    exactly as it printed them. That is deliberate — the line number is the whole value of a compiler
    error, and a paraphrase would be worse than the error.</p></li>

  <li><b>Check <code>Model</code>.</b> A file usually declares one module and circuitRF fills this in
    for you. When it declares several, the row becomes a picker — choose the one you want.</li>

  <li><b>Check <code>Pins</code>.</b> circuitRF fills this in from the model's own terminal count and
    then locks it, because the model has stated it. See <a href="#terminals">below</a> for the one
    case where you deliberately set it lower.</li>

  <li><b>Load the parameter set</b> — <i>Load Parameters…</i> at the foot of the parameter editor. See
    <a href="#parameters">below</a>.</li>

  <li><b>Wire it and run.</b> From here it is an ordinary nonlinear component. Attach a
    <b>DC</b> analysis to sweep a bias point, <b>S-parameters</b> to get small-signal behaviour about
    that point, and <b>harmonic balance</b> for large-signal power, gain and efficiency. Everything in
    <a href="simulations.html">Simulations</a> applies unchanged, and so does
    <a href="harmonicarf.html">harmonicaRF</a> — a compiled model is a valid DUT there too.</li>
</ol>

<div class="callout tip">
<span class="label">Start at DC</span>
<p>Run a DC sweep before anything else. A compact model that is mis-parameterised, or whose thermal
terminal is wired the wrong way, shows it as a bias point that will not converge — and that is far
easier to read at DC than in the middle of a harmonic-balance sweep.</p>
</div>

## Terminals, `Pins`, and the thermal one {#terminals}

The symbol is a **plain box**, deliberately. circuitRF does not know what your model is — it could be
a transistor, a diode or a whole subcircuit — so drawing a transistor glyph would assert something
the file has not said.

The **leads are named by the model**. Once circuitRF has read the file, each lead is labelled with the
model's own name for that terminal — `d`, `g`, `s`, `b`, `dt` — rather than `1..5`. On a five-terminal
part with five identical leads, numbers are the single largest source of mis-wiring, and the model has
already told circuitRF which is which. A lead the model does not name falls back to its number, on its
own; you may see a mix.

### The thermal terminal

Many physics-based models expose a **thermal terminal**: a node whose "voltage" is a temperature and
whose "current" is a power. You have two honest choices, and circuitRF supports both.

<table class="param-table"><thead><tr><th>What you draw</th><th>What the model does</th><th>When to use it</th></tr></thead>
    <tbody>
      <tr>
        <td><code>Pins</code> = the model's full count, thermal terminal <b>wired</b></td>
        <td>Reads the temperature you impose on that node</td>
        <td>You are building the thermal network yourself — a resistance and capacitance from that node
          to an ambient source that sets the baseplate temperature.</td>
      </tr>
      <tr>
        <td><code>Pins</code> = one <b>less</b>, thermal terminal omitted</td>
        <td>Sees that the terminal is unconnected and grounds it internally</td>
        <td>The ordinary case. The model handles its own self-heating and you draw a four-pin part.</td>
      </tr>
    </tbody>
    <caption>circuitRF tells the model how many terminals you connected; that is what the model's own
      <code>$port_connected</code> reads.</caption>
  </table>

When you set `Pins` one below the model's count and the omitted terminal is thermal, **the parameter
editor says so** — that a thermal terminal is deliberately left off and the model is handling its own
self-heating. It is not a warning. It is there because a symbol with one lead fewer than the model
declares otherwise reads as a mistake, and "fixing" it is the one thing you must not do:

<div class="callout warning">
<span class="label">Never leave a thermal terminal drawn but unwired</span>
<p>A thermal node with a pin on it and no thermal network attached is a <b>floating node with no DC
solution</b>. It does not fail quickly or clearly — it spends the whole continuation budget and then
reports a residual. Either omit the pin (and let the model ground it) or attach a real thermal
network. There is no third option.</p>
</div>

## Loading a fitted parameter set {#parameters}

A fitted parameter set for a model of this size is 50 to 200 numbers. Both of the ways to get them in
are at the foot of the parameter editor:

- **Add Parameter…** — a searchable list of everything the model declares, with the model's own
  units, descriptions and defaults. Adds one, seeded at the model's own default. Use it to change a
  handful of values.
- **Load Parameters…** — reads a whole fitted set from a file. Use it for anything larger.

**Load Parameters…** reads sets written as **Verilog-A parameter declarations**, which is the form
these families actually ship:

```verilog
parameter real vxo   = 1.3e7;    // saturation velocity
parameter real beta  = 1.8;
parameter integer nf = 4;
```

Comments are ignored (including one containing a semicolon), `from […]` and `exclude` ranges are
ignored, and both `1.3e7` and engineering notation like `2.4p` are read as written. The whole load is
**one undo step**.

Two behaviours worth knowing:

- **Case is aligned to the model's own spelling.** A set written `vxo` reaches a model that declares
  `VXO`. The match is case-insensitive only — a genuine typo is *not* quietly turned into something
  the model accepts; it is reported by name.
- **Names the model does not declare are reported, never dropped in silence.** circuitRF tells you
  exactly which ones did not land. This is the common case, not the exotic one: a set written for a
  different version of the same family will have names that have since been renamed or removed, and a
  silent drop would leave the device running on the model's own defaults for all of them and looking
  perfectly healthy.

<div class="callout note">
<span class="label">Absent means "the model's default"</span>
<p>circuitRF only writes the parameters your set actually assigns. A parameter that is not on the
component is not sent to the model at all, which already means <i>use your own default</i>. That is
why the whole declared list is never materialised as rows: freezing today's defaults into the design
would mean recompiling with a changed default silently had no effect.</p>
</div>

## When circuitRF rebuilds, and where the artefact goes {#rebuild}

**A simulation of an unedited model compiles nothing.** The compiled artefact is cached, and the
cache key is:

- the **content** of the source — not its path and not its timestamp, so re-saving a file without
  changing a character costs nothing;
- the content of **every file it `` `include ``s**, so editing a parameter or macro file beside the
  source does rebuild — hashing only the top file would hand you a stale artefact that runs and looks
  correct;
- the **compiler's own identity**, so upgrading your compiler rebuilds even though your source has not
  changed by a byte.

Change any of those and you pay exactly one recompile. Change none of them and the compiler is not
asked to build anything, however many times you run.

Artefacts are written to circuitRF's own per-user cache — **not** beside your source. A model family
is often installed as a read-only tree, and writing build output into someone else's delivery is
wrong even where it would succeed. The parameter editor names the exact path each time it reads the
file, so a rebuild
you were not expecting is visible rather than mysterious.

## What is not supported {#limits}

- **No noise analysis** from a compiled model.
- **No operating-point variable read-back.** These models compute dozens of internal quantities
  (`gm`, `ids`, junction temperatures). circuitRF's worker already recognises them and deliberately
  keeps them out of the settable parameter list, but there is not yet a way to plot one.
- **Aging and degradation parameters** are forwarded to the model like any other parameter and then
  do nothing — there is no aging analysis and no stress history to drive one.
- **Verilog-AMS digital constructs** are the compiler's business, not circuitRF's. If your compiler
  builds the file, circuitRF will run it.

{{table: components/VerilogA}}

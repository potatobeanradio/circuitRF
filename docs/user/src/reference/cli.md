---
title: The Command Line
slug: reference/cli.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > The command line
lede: circuitRF runs without the GUI — not just its engines, but authoring, validation, resolution and drawing too. One executable, fifteen verbs — S-parameters, DC, harmonic balance, loadpull, loadpull pursuit, electromagnetic extraction, layout interchange, creating a workspace or a cell, importing a part, rendering a document as a picture, checking a design, explaining what it resolved to, reading a result back, an elaborated-netlist dump, and an MCP server. Every one of them answers --json. This chapter is the operational reference for all of them, including a worked EM run and a worked render, each from an empty folder.
keywords: CLI, command line, command-line, terminal, shell, console, headless, batch, script, scripting, automation, verbs, exit code, stdout, circuitrf, MCP, Model Context Protocol, agent, AI, LLM, JSON-RPC, stdio, tool server, integration, render, image, SVG, PNG, PDF, export, picture, screenshot, plot, viewport, layers, extents, thumbnail
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#invoking">Invoking it</a></li>
<li><a href="#verbs">The verbs at a glance</a></li>
<li><a href="#channels">Results on stdout, everything else on stderr</a></li>
<li><a href="#common">Options every verb takes</a></li>
<li><a href="#sparam"><code>sparam</code> — S-parameters</a></li>
<li><a href="#dc"><code>dc</code> — the operating point</a></li>
<li><a href="#hb"><code>hb</code> — harmonic balance</a></li>
<li><a href="#lp"><code>lp</code> — loadpull</a></li>
<li><a href="#lpp"><code>lpp</code> — loadpull pursuit</a></li>
<li><a href="#em"><code>em</code> — electromagnetic extraction</a></li>
<li><a href="#convert"><code>convert</code> — layout interchange</a></li>
<li><a href="#new"><code>new</code> — a workspace or a cell</a></li>
<li><a href="#import"><code>import part</code> — a footprint and its symbol</a></li>
<li><a href="#render"><code>render</code> — a picture of a document</a>
  <ol>
  <li><a href="#render-viewport">The viewport, and the unit rule</a></li>
  <li><a href="#render-detail">Size, and what <code>--detail</code> costs</a></li>
  <li><a href="#render-layers">Layers and colour</a></li>
  <li><a href="#render-cdd">A data display</a></li>
  <li><a href="#render-example">A worked example, from an empty folder</a></li>
  </ol>
</li>
<li><a href="#check"><code>check</code> — is it sound?</a></li>
<li><a href="#explain"><code>explain</code> — what did it resolve to?</a>
  <ol>
  <li><a href="#explain-cells">What cells are in here?</a></li>
  <li><a href="#explain-layers">What layers may I ask for?</a></li>
  <li><a href="#explain-extents">How big is it?</a></li>
  </ol>
</li>
<li><a href="#read"><code>read</code> — a result or a document, back</a></li>
<li><a href="#netlist"><code>netlist</code> — the netlist a schematic runs as</a></li>
<li><a href="#plot"><code>plot</code> — a picture of a result</a></li>
<li><a href="#find"><code>find</code> — what is in this folder?</a></li>
<li><a href="#reference"><code>reference</code> — what may I write?</a></li>
<li><a href="#elab"><code>elab</code> — the elaborated netlist</a></li>
<li><a href="#json"><code>--json</code> — one machine-readable document</a></li>
<li><a href="#serve"><code>serve</code> — the MCP server</a></li>
<li><a href="#exit">Exit codes</a></li>
<li><a href="#scripting">Scripting patterns</a></li>
</ol>
</nav>

## Invoking it {#invoking}

The command-line driver is the same program as the GUI's Run button with the window taken off. It
reads the same files, elaborates them with the same elaborator, runs the same engines, and evaluates
the test bench's `measure` lines with the same evaluator.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf &lt;verb&gt; &lt;file&gt; [options]</code></pre>

From a source checkout there is no `circuitrf` on your path yet, so put `dotnet run --project src/Cli --`
wherever `circuitrf` appears:

<pre><code class="cmd"><span class="prompt">$ </span>dotnet run --project src/Cli -- sparam mycircuit.cnl --freq 1GHz:3GHz:50MHz</code></pre>

Run it with no arguments for the built-in help.

<div class="callout note">
<span class="label">A file that works headless works when opened</span>
<p>This is the point of the command line being the <em>same</em> code rather than a second
implementation. A <code>.cnl</code> that runs here runs when you open it in the workspace, and an EM
setup run with <code>em</code> writes the byte-identical Touchstone the <b>Simulate</b> button writes.
There is one elaborator, one set of engines, one measurement evaluator and one results-path
convention behind both.</p>
</div>

## The verbs at a glance {#verbs}

| Verb | Takes | Runs | Writes |
|---|---|---|---|
| `sparam` | `.cnl` or `.csch` | The linear S-parameter engine over a frequency sweep | A Touchstone `.sNp`, always |
| `dc` | `.cnl` or `.csch` | The nonlinear DC engine | Node voltages and probe currents, to stdout |
| `hb` | `.cnl` or `.csch` | Harmonic balance, single- or multi-tone | Spectra tables to stdout; `-o .mat/.npy/.txt` |
| `lp` | `.cnl` or `.csch` | Loadpull over the directive's Γ grid | A per-Γ-point table; `-o .mat/.npy/.txt/.spl/.lpcwave` |
| `lpp` | `.cnl` or `.csch` | Loadpull **pursuit** — searches for the optima | Optima + the follow-on grid; `-o` as `hb`; `--out-grid` writes a `.gam` |
| `em` | `.cem` | The EM kernel the setup resolves to | A Touchstone `.sNp` **and** a grouped `.npy`, where **Simulate** writes them |
| `convert` | any layout format | The same importer and exporter **File ▸ Import/Export** runs | The layout in the format you asked for |
| `new workspace` | a directory | The same code **File ▸ New Workspace** runs | A `.cws` and, unless you say otherwise, a copied technology |
| `new cell` | a workspace + a name | The same code **New Cell** runs | A cell folder and one empty-but-valid file per view |
| `import part` | a component file or folder | The same code **Import Component** runs | A cell folder holding the land patterns and the symbol |
| `render` | a `.csch`, `.csym`, `.clay` or `.cdd`, a cell folder, or a workspace | The same Skia renderers the editors draw every frame with | A `.svg`, `.pdf` or `.png`, where `-o` says |
| `check` | a workspace, a cell folder, or one document | Every validator the application already uses | **Nothing** — findings to stdout |
| `explain` | the same | Resolution only — no analysis | **Nothing** — the walk and the answer, to stdout |
| `read` | a result file, or one of circuitRF's own documents | The same loaders the Data Display reads a file with | **Nothing** — what the file holds, to stdout |
| `netlist` | a `.csch`, a cell folder, or a workspace | The same extraction **Simulate** performs | A `.cnl`, or the netlist text to stdout |
| `plot` | a result file | Builds a one-plot data display and draws it | A `.svg`, `.pdf` or `.png`, where `-o` says |
| `find` | a directory | Nothing — it reads documents | **Nothing** — the workspaces, cells, views and analyses under it |
| `reference` | **nothing** | Nothing — it reads no file | **Nothing** — the reference pages, and every netlist primitive with its terminals and parameters |
| `elab` | `.cnl` or `.csch` | Elaboration only, no analysis | The elaborated netlist, to stdout |
| `serve` | `--root <dir>` | An MCP server for an external client | Whatever the tool it is asked for writes |

`hb`, `lp` and `lpp` all run **the whole parametric sweep** when one wraps the analysis — see
[naming the wrapper](#wrapper).

**Every run verb takes a schematic as well as a netlist.** Hand it a `.csch` and it extracts the
netlist in memory first — the same extraction **Simulate** performs — so you do not have to write one
out to run a design you drew. [`netlist`](#netlist) is how you see what it will run.

## Results on stdout, everything else on stderr {#channels}

**stdout is the result. stderr is everything else** — progress, per-grid-point engine chatter,
`[circuitRF]` notes, elaboration and engine warnings, device-worker logs.

That split is what makes the output pipeable while the terminal still shows a long run moving:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf lp hero3.cnl &gt; table.txt</code></pre>

`table.txt` gets the loadpull table and nothing else; the per-drive-step `[LP]` lines and the
convergence notes still scroll past on screen. Redirect `2&gt;/dev/null` to silence them, or
`2&gt;run.log` to keep them.

## Options every verb takes {#common}

| Option | What it does |
|---|---|
| `--kits <dir>` | A folder of installed kits, so an externally-supplied device model (`ExtDevice Provider=…`) resolves headlessly the way opening a workspace resolves it in the GUI. Repeatable. |
| `--json` | Put **one JSON document** on stdout and nothing else — [see below](#json). stderr is untouched. |
| `--only a,b` | Narrow that document's result to these cubes. |
| `--group g,h` | Narrow that document's result to these groups. |
| `--at axis=value` | Narrow it to **one point of an axis** — `--at freq=2GHz`. The nearest grid point, and the document says which one it gave you. |
| `--interp` | Make every `--at` interpolate between the two bracketing points instead. Never the default: it returns a number the run did not compute, and the document says so. |
| `--range axis=lo:hi` | Keep a band of an axis — `--range freq=1GHz:3GHz`. |
| `--result full\|summary` | `summary` returns the result's **shape** — group and cube names, units, axis lengths and extents — and no values at all. |
| `--summary` | Report the informational notes as counts by severity instead of in full. Warnings and errors always travel in full, and stderr is untouched. |

Frequencies are written as `1GHz`, `100MHz`, or bare Hz (`1e9`) anywhere a frequency is accepted.

<div class="callout">
<span class="label">Ask for the part you want, not the whole result</span>
<p><code>--only</code> and <code>--group</code> narrow by cube <i>name</i>, which does nothing when the
result has one cube. A 551-point two-port S-parameter run is about 173&nbsp;kB of JSON; if the
question is "what is S21 at 2&nbsp;GHz", <code class="nowrap">--at freq=2GHz --only S</code> is a few
hundred bytes. Every value carries its own unit — a bare <code>2</code> could be 2&nbsp;Hz or
2&nbsp;GHz — and an axis name nothing in the result has is refused, listing the ones that exist,
rather than quietly handing you everything.</p>
<p>Every run returns <code>result.shape</code> whether or not it returns the values, so
<code class="nowrap">--result summary</code> is how you find out what a run produced before deciding
what to ask for.</p>
</div>

<div class="callout">
<span class="label">An option a verb does not take is refused, never ignored</span>
<p>Every verb stops with <code>unknown option '…'</code> and exit&nbsp;1 rather than dropping a flag it
does not recognise. This matters more than it sounds: most verbs find their input file as
<i>the first argument that is not an option</i>, so a silently dropped flag's <b>value</b> would be
read as the file name — and a flag that carries an override, like
<code class="nowrap">--set</code>, would simply not be applied, giving you a run that answers a
different question with nothing to say so.</p>
</div>

---

## `sparam` — S-parameters {#sparam}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf sparam &lt;file.cnl|.csch&gt; [--freq start:stop:step] [-o out.sNp]</code></pre>

```text
$ circuitrf sparam hero1.cnl --freq 1GHz:3GHz:1GHz -o hero1.s2p
S-parameter analysis: 3 points, 1–3 GHz
Wrote hero1.s2p
```

| Option | What it does |
|---|---|
| `--freq start:stop:step` | Override the sweep. **Omit it and the netlist's own `sparam` analysis is used**, segments and all — which is almost always what you want, because it is the sweep the design was set up with. |
| `-o`, `--output <path>` | Where the result goes, and **its extension picks the format**: `.s1p`…`.s99p` for a Touchstone, or `.npy` / `.mat` / `.txt` for the cubes. Omitted, it is the input file with its extension changed to `.sNp` for the port count found. |

There is no stdout table. The port count in the default extension comes from the network, so a
circuit that grew a port writes `.s3p` without you editing the command. An extension naming no format
this verb writes is refused, listing the ones it does — you never get a Touchstone under a name that
says otherwise.

**A run carrying [WSProbes](wsprobe.html) prints one line per probe** after the S summary, and a run
with `NDF=yes` on its directive prints the right-half-plane pole count:

```text
$ circuitrf sparam amp.cnl
S-parameter analysis 'SP1': 2001 points, 0.5-3 GHz (1 segment(s))
NDF: 2 right-half-plane pole(s)  (net clockwise encirclement 1.989; NDF(100 GHz)=1 ∠ 1.8)
WSProbe P idx=1  H0(0.5 GHz)=11.597 ∠ -1.4  ZG(0.5 GHz)=10.482 ∠ 17.4
                 SM_Y0 min -18.1 dB @ 1.59125 GHz  SM_H0 min -19.8 dB @ 1.73375 GHz
```

`--json` carries the same under `wsprobes` and `ndf`. **The margins are linear there and dB on the
line**, because dB is a display convention and a document should carry the number. A circuit that has
WSProbes and **no ports at all** is a legitimate run — it writes every `wsp` cube and no `S` — and
asking it for a Touchstone is refused, naming the spellings that do carry the result.

<div class="callout">
<span class="label">Ports with different reference impedances</span>
<p>A Touchstone file declares <b>one</b> reference impedance, and circuitRF writes port&nbsp;1's on
the option line. When the ports differ, the file also carries a header note listing each port's own
impedance and saying that the data is referenced to <i>those</i> — and circuitRF reads that note back,
so <code>circuitrf read</code> on the file reports the real per-port references rather than the
option line repeated. Nothing is renormalized: the numbers are the ones the solve produced. If you
want the per-port references in a form every tool reads, write <code>.npy</code> instead.</p>
</div>

## `dc` — the operating point {#dc}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf dc &lt;file.cnl|.csch&gt;</code></pre>

```text
$ circuitrf dc hero2.cnl
DC: converged in 3 iteration(s), residual 7.27E-16
Node voltages:
  0                                         0
  n_src                                     0
  n_gate                                -3.05
  n_drain                                  48
```

No options beyond the common ones. It prints the converged node voltages and any probe currents, and
[exits 2](#exit) if the solve did not converge — the operating point is the one thing every nonlinear
analysis is built on, so a non-converged DC is a failed run, not a partial one.

## `hb` — harmonic balance {#hb}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf hb &lt;file.cnl|.csch&gt; [-a name] [--set var=expr] [-o out.npy]</code></pre>

The same verb runs **single- and multi-tone** — which it is comes from the netlist's directive, not
from a flag.

```text
$ circuitrf hb hero2.cnl --rows 6
HB 'HB1': f0=2 GHz, MaxHarm=4, tol=1E-06
Analysis: HB1   (hero2.cnl)
  Converged: yes (1 solve(s))
  Residual:  1.24E-09 (worst)
  Tones:     2 GHz
  V  [node:7 x harmonic:5]  (mag ∠deg)
                        0                     1                     2
    n_gate              3.05 ∠  180.0         0.029814 ∠    0.1     0
    n_drain             48 ∠    0.0           0.15004 ∠ -172.4      1.6824E-05 ∠ -179.8
    … 1 more row(s) — use --all or --rows N
```

| Option | What it does |
|---|---|
| `-a`, `--analysis <name>` | Which analysis to run. Optional when the file declares one HB chain. |
| `--set <var=expr>` | Override a global variable **before elaboration**. Repeatable. |
| `--maxharm K` | Override `MaxHarm`. |
| `--maxmix M` | Override `MaxMixOrder` (multi-tone only). |
| `--tol t`, `--max-iter N` | Override the convergence tolerance and the iteration cap. |
| `--rows N`, `--all` | How much of each printed table to show. Default is a truncated head. |
| `--diag` | Engine convergence diagnostics, on stderr. |
| `-o`, `--export <path>` | Export the results. **The extension picks the format**: `.mat`, `.npy` or `.txt`. |

### `--set` overrides the VARIABLE, not the number {#set}

`--set Pavl_dbm=0` replaces the global variable in the test bench's own scope, then elaborates. So
every expression derived from it re-derives — a bias that was written `Vg = Vth + 0.2` follows a
changed `Vth`, and a sweep computed from the variable sweeps the new values.

An override pushed at the engine instead would move one number and leave everything computed from it
stale, which is why there is no such option.

### Name the wrapper, or name nothing {#wrapper}

When a [parametric sweep](simulations.html#parametric-sweep) wraps an analysis, the sweep is what
runs. Naming the inner analysis with `-a` is **promoted** to its outermost enabled wrapper, and the
promotion is announced:

```text
[circuitRF] 'HB1' is the inner analysis of 'SW1' — running 'SW1' so the sweep axis is not lost.
```

<div class="callout note">
<span class="label">Why it is promoted rather than obeyed</span>
<p>Running the inner analysis alone produces a converged, plausible, complete-looking result at one
operating point — <em>with the sweep axis silently missing</em>. Nothing about it looks wrong. A
frequency-swept loadpull has exactly this shape, which is why the rule is the same for every verb
rather than something harmonic balance does on its own.</p>
</div>

If more than one runnable chain exists, all their names are printed and the first runs; if none does,
the message says whether the netlist declares no such analysis or declares one that is disabled.

### Measurements {#measurements}

The `measure` lines on the test bench are evaluated exactly as the GUI evaluates them, and the results
join the exported `DataSet` as named cubes. A measurement that fails to evaluate is **reported on
stderr and the run continues** — one bad expression does not throw away a run that took minutes:

```text
[circuitRF] measurement: Measurement 'Gain_dB': failed to evaluate 'Pout_dBm - Pavl_dbm':
                         Unresolved name 'Pout_dBm' in scope 'measurements'
```

## `lp` — loadpull {#lp}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf lp &lt;file.cnl|.csch&gt; [--grid grid.gam] [--pin start:step:max] [-o out.spl]</code></pre>

`lp` sweeps the load (or source) termination over the directive's Γ grid, runs a harmonic-balance
drive ladder at each point, and reports the figures of merit.

```text
$ circuitrf lp hero3.cnl --rows 8
Analysis: LP1   (hero3.cnl)
  Grid: 20 point(s) — 0 reached compression, 20 stopped at max drive
  Nothing reached compression — raise --pin's max (or the directive's PinMax).

      #  GammaLoad           ZLoad (ohm)           stop              Pavl     Pout      Gt     DE%    PAE%
      0  0.0000 ∠    0.0     50.00+j0.00           max drive        10.00    20.54   10.54    3.35    3.09
      1  0.2000 ∠    0.0     75.00+j0.00           max drive        10.00    22.31   12.31    5.03    4.76
      2  0.2000 ∠   90.0     46.15+j19.23          max drive        10.00    20.19   10.19    3.09    2.83
    … 12 more point(s) — use --all or --rows N
```

| Option | What it does |
|---|---|
| `-a`, `--analysis <name>` | Which loadpull analysis to run. |
| `--set <var=expr>` | Override a global variable before elaboration. Repeatable. |
| `--grid <file.gam>` | Override the Γ grid the directive reads. **Resolved against your working directory**, not the netlist's. |
| `--pin start:step:max` | Override the drive ladder, in dBm. |
| `--compression dB` | Override the compression target. |
| `--maxharm K`, `--tol t`, `--max-iter N` | Override the inner HB settings. |
| `--rows N`, `--all` | `--all` dumps every cube instead of the summary table. |
| `--diag` | Engine diagnostics, on stderr. |
| `-o`, `--export <path>` | `.mat`, `.npy`, `.txt` — **or `.spl` / `.lpcwave`**, the loadpull interchange formats. |

### One row per Γ point, at the point that answers the question {#lp-rows}

A loadpull's raw cubes are `[gridPoint × driveStep]` — a 61-point grid driven up in 1 dB steps is a
61 × 30 table *per figure of merit*, and eight of those scroll a terminal without answering anything.

So the default table is **one row per Γ grid point**: where it was, how it stopped, and its FOMs at
the **last converged, non-tickle drive step** — the compression point where the point compressed, the
highest drive it managed otherwise. Reading a fixed drive index instead would mix compressed and
uncompressed points in one column. `--all` still dumps everything.

A swept run prints one table per sweep point.

### `.spl` and `.lpcwave` {#lp-export}

`-o out.spl` writes the loadpull interchange format the [Data Display](data-display.html) reads back
as a measured surface, so a headless run can produce a file the GUI opens. `lp` also runs the same
post-processor a GUI run does, so the exported cubes carry the derived display metrics (`Pout_dBm`,
`Zin`, `IRL_dB`, `AMPM_deg`) — a `.npy` written here and one written by the GUI carry the same cubes.

## `lpp` — loadpull pursuit {#lpp}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf lpp &lt;file.cnl|.csch&gt; [--out-grid found.gam] [-o out.npy]</code></pre>

A pursuit **searches** for the max-power (MXP) and max-efficiency (MXE) terminations rather than
reading a grid, then runs a follow-on loadpull over the terminations it recommends.

```text
$ circuitrf lpp hero3B_at_compression.cnl
Analysis: LP1   (hero3B_at_compression.cnl)
  Pursuit optima:
  MXP (max power)            converged   Pout=40.625 dBm   Zload=80.48+j0.00   Zsource=50.00+j0.00
  MXE (max efficiency)       converged   Eff=69.617 %   Zload=140.31-j4.95   Zsource=50.00+j0.00
  21 termination(s) queried, 45 recommended termination(s)

  Grid: 45 point(s) — 45 reached compression

      #  GammaLoad           ZLoad (ohm)           stop              Pavl     Pout      Gt     DE%    PAE%
      0  0.2690 ∠    7.0     86.15+j6.11           compressed       26.00    40.56   14.56   67.08   64.74
      1  0.2030 ∠    9.6     74.80+j5.30           compressed       27.00    40.68   13.68   63.55   60.82
```

`lpp` takes every `lp` option **except `--grid`**, and adds `--out-grid`:

| Option | What it does |
|---|---|
| `--out-grid <file.gam>` | Where the terminations the pursuit found are written, as a `.gam` you can feed back to `lp`. Resolved against your working directory. |

<div class="callout warn">
<span class="label">The two grid options are refused, not ignored</span>
<p><code>--grid</code> on <code>lpp</code> and <code>--out-grid</code> on <code>lp</code> each stop the
run with a sentence naming the verb that owns them. A grid option silently doing nothing would be a
run that answered a different question and said nothing about it.</p>
</div>

A **non-converged** optimum is still printed, with its status. The engine publishes the last
termination it looked at, and printing nothing there reads as "the search found nothing" when what
actually happened is "nothing it tried reached compression".

---

## `em` — electromagnetic extraction {#em}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf em &lt;setup.cem&gt; [-o out.sNp] [--workspace file.cws]</code></pre>

`em` is the only verb that does not take a `.cnl`. It takes a **`.cem` EM setup** — the document the
[EM Setup panel](em-setup.html) edits — and runs it: extracts the geometry from the layout the setup
names, resolves the stackup, meshes, solves the frequency plan, de-embeds, and writes the results.

**It needs no other arguments.** Everything else it needs is already recorded in the files.

### What an EM run takes {#em-inputs}

Four files, and three of them are things you already have if you have drawn a layout:

| File | What it supplies | Where it comes from |
|---|---|---|
| **`.cem`** | The setup: which layout, which analysis, the frequency plan, port impedances and types, mesh settings, solver switches | **File ▸ New ▸ EM Setup…**, or the layout editor's **EM** button |
| **`.clay`** | The artwork — the metal, and the port labels for a full-wave run | The [layout editor](layout-editor.html) |
| **`.ctech`** | The [stackup](stackup.html): layer thicknesses, ε_r, tanδ, conductivity, which conductor is ground, and which drawing layers map onto what | The technology editor, or one of the shipped starter technologies |
| **`.cws`** | The workspace marker, carrying `DefaultTechRef` — the technology a layout uses when it does not name one itself | Created with the workspace |

<div class="callout note">
<span class="label">Author the setup in the GUI; run it from the command line</span>
<p>The <code>em</code> verb <b>runs</b> a setup — it does not create or edit one, and it will not
repair one. A setup with no ports, no technology or no signal conductor is <a href="#em-refusals">refused
with the sentence explaining what is missing</a>. Build the <code>.cem</code> once in the
<a href="em-setup.html">EM Setup panel</a>, where every control tells you as you type whether the run
is blocked and why, then commit it beside the layout and run it headlessly from then on.</p>
</div>

### Both file references resolve by walking UP, and neither is a flag {#em-resolution}

A `.cem` names a layout; the layout names — or inherits — a technology. Neither reference is stored
absolutely, and neither needs an argument:

- **The layout.** The setup's layout reference is relative to the **workspace root**: the nearest
  ancestor `.cws` found by walking up from the `.cem`. With no workspace above it at all, the
  reference falls back to the `.cem`'s own directory, so a loose `.cem` sitting beside its `.clay`
  simply works.
- **The technology.** Resolved against **the layout's own parent workspace**, found by walking up from
  the `.clay` — never against "the workspace you are in", of which there is none headlessly. A `.clay`
  that names no technology picks up its workspace's `DefaultTechRef`.

**The two walks start from different files, and that is deliberate.** A `.cem` in one workspace may
point at a layout in another, and that layout's layers have to be read by *its* technology, not by
whichever workspace the setup happened to live in.

`--workspace <file.cws>` overrides the first walk, for a `.cem` being run from outside its own tree.
It is never required.

The three resolutions are echoed on stderr before anything expensive starts, so you can see what the
run is actually about to read:

```text
[circuitRF] workspace: /work/amp/.cws
[circuitRF] layout: /work/amp/Line/layout/Line.clay
[circuitRF] technology: /work/amp/pcb.ctech
```

### A worked example, from an empty folder {#em-example}

Here is a complete, minimal EM workspace — a single 20 mm × 2.9 mm microstrip line on a two-layer PCB
technology, swept 1–10 GHz in 3 points. Four files:

```text
amp/
├─ .cws                        the workspace marker, naming the default technology
├─ pcb.ctech                   the stackup
├─ line.cem                    the EM setup
└─ Line/
   └─ layout/
      └─ Line.clay             the artwork
```

The `.cem` is JSON, and this is all of it — every field not written takes its documented default:

```json
{
  "FormatVersion": 1,
  "Name": "line",
  "LayoutRef": "Line/layout/Line.clay",
  "Frequency": {
    "StartExpr": "1", "StopExpr": "10", "NumPoints": 3,
    "Mode": "PointCount", "Kind": "Linear",
    "StartUnit": "GHz", "StopUnit": "GHz"
  },
  "Port1Z0Real": 50, "Port2Z0Real": 50
}
```

`LayoutRef` is **workspace-relative** — relative to the directory holding `.cws`, not to the `.cem`.
The `.cws` supplies the technology:

```json
{ "DefaultTechRef": "pcb.ctech" }
```

Nothing in the `.cem` names a technology, a kernel, a mesh or a port. The technology is inherited, the
kernel is chosen from the geometry, the mesh settings are the engine's own defaults, and this
structure's ports are the two ends of a uniform line by construction. Then:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf em amp/line.cem</code></pre>

```text
[circuitRF] workspace: amp/.cws
[circuitRF] layout: amp/Line/layout/Line.clay
[circuitRF] technology: amp/pcb.ctech
[0] solving the cross-section
[3] solving the cross-section
note: Automatic chose "Uniform transmission line": this geometry is a uniform cross-section, which
      that analysis solves exactly and is about a thousand times cheaper than "Full-wave planar".
      Set Analysis to "Full-wave planar" if you want the full-wave answer anyway.
note: Dielectric interfaces truncated 20 substrate heights (32000 µm) beyond the outermost conductor
      on each side.
EM setup:  line
Kernel:    Quasi-static cross-section (CrossSection)
Points:    3
Wrote amp/results/line.s2p
Wrote amp/results/line_em.npy
```

Everything from `EM setup:` down is on **stdout**; the resolution lines, the progress and the notes are
on stderr.

A **full-wave** run differs only in what the files say, not in how you invoke it: draw port labels in
the layout with the layout editor's **Port** tool, set the setup's analysis to `Planar` (or leave it
`Auto` and let the geometry decide), and run exactly the same command. It will take very much longer —
a de-embedded full-wave point costs tens of seconds at the shipping mesh — which is why the progress
lines exist.

### Where the results go, and what `-o` moves {#em-output}

With no `-o`, the run writes **exactly where the Simulate button writes**: into the workspace's
`results/` folder. Two files come out, and they are not redundant:

| File | Holds |
|---|---|
| `<name>.sNp` | S-parameters only — the artefact a schematic's [SnP component](components.html#snp) references by path |
| `<name>_em.npy` | The whole `DataSet`, including the per-kernel **diagnostics** group — Z_c, γ, ε_eff, RLGC for the cross-section kernel; the calibration residual and usability flags for the full-wave one |

<div class="callout warn">
<span class="label">Why the default path is not the CLI's to choose</span>
<p>That results path is <b>predictable by design</b>, so a schematic's SnP reference stays valid across
re-runs. A headless run that minted its own file name would orphan every one of them — so
<code>circuitrf em</code> writes the same file <b>Simulate</b> does, and the acceptance test for the
verb compares the two Touchstones <em>byte for byte</em>.</p>
</div>

`-o` moves **the Touchstone only**. The `.npy` stays where it was, because it is the diagnostics
record of the run rather than the deliverable:

```text
$ circuitrf em amp/line.cem -o /tmp/mine.s2p
Wrote /tmp/mine.s2p
Wrote amp/results/line_em.npy
```

You do not have to get the extension right — the port count decides it, so a `.s2p` you typed for a
structure that turned out to have four ports is written `.s4p`.

With no workspace above the `.cem`, `results/` is created beside the `.cem` itself.

### note, warning, error — three lists, kept apart {#em-messages}

An EM run has three different things to say and they ask three different things of you, so they are
printed under three labels rather than flattened into one stream:

| Prefix | Means |
|---|---|
| `note:` | The run explaining itself — which kernel it chose and why, the mesh's own sentences, RLGC, the ports it found. Read these; they are the cheapest check that the tool is looking at the structure you think it is. |
| `warning:` | Something to act on — a stale `.sNp` about to be replaced, a technology that resolved but failed validation. |
| `error:` | Something you asked for and did not get — a results file that could not be written. |

### A refusal is a result {#em-refusals}

The EM engine declines geometry it cannot solve *correctly* rather than returning a plausible number.
Each refusal carries a written explanation of what is wrong with **this** setup, and `em` prints that
explanation rather than collapsing it into "EM failed":

```text
[circuitRF] workspace: amp/.cws
warning: Layout file not found: amp/Line/layout/Missing.clay
No layout: The layout 'Line/layout/Missing.clay' could not be found, so there is no geometry to
analyse. Point this EM setup at a layout that exists.
```

| Status | Means | Exit |
|---|---|---|
| **Refused** | The extractor or the kernel declined this geometry — see [what the engine refuses](mom-engine.html#refusals) | 1 |
| **No layout** | The layout reference did not resolve | 1 |
| **Engine error** | The solve failed | 1 |
| **Cancelled** | Stopped at a work boundary | 130 |

## `convert` — layout interchange {#convert}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert &lt;input&gt; -o &lt;output&gt; [options]</code></pre>

Reads a layout in any format circuitRF understands and writes it in any other. It is the same reader
and the same writer **File ▸ Import** and **File ▸ Export** run — see
[Interchange](layout-editor.html#interchange) for what each format can and cannot carry — so a
conversion here and the same conversion through the GUI produce the same bytes.

| Format | Named by | As input | As output |
|---|---|---|---|
| circuitRF layout | `.clay` | the file | a **folder** of cells plus a `.ctech` |
| GDSII | `.gds`, `.gdsii`, `.gds2` | ✓ | ✓ |
| DXF | `.dxf` | ✓ | ✓ |
| Gerber + Excellon | a **folder**, or one Gerber/drill file | ✓ | a **folder** |
| Board | `.kicad_pcb` | ✓ | ✓ |

**Every ordered pair works** — DXF to Gerber, Gerber to board, GDSII to DXF, board to GDSII, and the
rest. There is no privileged direction and no hub format you have to route through by hand: a
conversion is an import followed by an export, and `convert` does both.

Formats are read off the paths. A folder means Gerber; a file with no telling extension is classified
by its *content*, through the same classifier the Gerber import uses. `--from` and `--to` override
that, and `--to` is **required** when the output is a folder, since a folder could be either Gerber or
`.clay`.

### Examples {#convert-examples}

Board file out to a fab house as artwork plus drill:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert board.kicad_pcb -o fab/ --to gerber</code></pre>

A folder of Gerbers back to a board file:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert fab/ -o recovered.kicad_pcb</code></pre>

A mechanical drawing straight to artwork — no board tool in the middle:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert outline.dxf -o gerbers/ --to gerber</code></pre>

A mask set to a drawing your mechanical engineer can open, at the DXF version their tool wants:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert mmic.gds -o mmic.dxf --dxf-version AC1015</code></pre>

Bring a board in as editable circuitRF cells and keep the technology it declared:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert board.kicad_pcb -o cells/ --to clay</code></pre>

<div class="callout note">
<span class="label">A <code>clay</code> target is a <em>folder</em>, not a file</span>
<p>An import writes one cell <b>folder</b> per structure the source holds, plus the technology beside
them — so <code>-o cells/</code> is the shape, and <code>-o cells/board.clay</code> is
<b>refused</b> with the folder spelling in the message. Point it at a folder and look inside: the
<code>.clay</code> is at <code>cells/&lt;Cell&gt;/layout/&lt;Cell&gt;.clay</code>, which is where every
other circuitRF tool expects a layout view to be.</p>
</div>

One cell out of a GDSII library that holds many:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert lib.gds --list-cells
<span class="prompt">$ </span>circuitrf convert lib.gds -o coupler.dxf --cell COUPLER</code></pre>

Convert a directory of drawings in one line:

<pre><code class="cmd"><span class="prompt">$ </span>for f in dxf/*.dxf; do circuitrf convert "$f" -o "gds/$(basename "${f%.dxf}").gds"; done</code></pre>

### Options {#convert-options}

| Option | What it does |
|---|---|
| `-o, --output <path>` | The file to write — or the **folder**, for `gerber` and `clay`; a file-shaped path there is refused. Required. |
| `--from <fmt>`, `--to <fmt>` | `clay`, `gdsii`, `dxf`, `gerber`, `board`. Say it when the path does not. |
| `--cell <name>` | Which cell to export, when the source holds several. |
| `--list-cells` | Report what the input holds and write nothing. |
| `--name <stem>` | What to call the written Gerber file set. Default: the cell's name. |
| `--tech <file.ctech>` | The technology to convert against, instead of the one the layout resolves. |
| `--workspace <file.cws>` | The workspace a `.clay`'s references resolve against. Default: the nearest one above it. |
| `--keep-cells <dir>` | Keep the cells the import produced instead of discarding them. |
| `--dbu <n>` | Database units per micron for an imported design. Default `1000` — one DBU is one nanometre. |
| `--dxf-version <v>` | `AC1015` (R2000), `AC1018` (R2004), `AC1032` (R2018, the default). |
| `--dxf-units <n>` | The `$INSUNITS` value for a DXF that declares none. |
| `--drill-units <mm or inch>` | Excellon coordinate units, when the file does not say. Applies to **every** drill file in the set. |
| `--drill-format <int>:<dec>` | Excellon digit counts, e.g. `2:4`. Applies to every drill file in the set. |
| `--drill-zeros <leading or trailing>` | Excellon zero suppression. Applies to every drill file in the set. |
| `--accept-inferred-drill-format` | Take each drill file's own inference rather than refusing. |
| `--open-archives` | Look inside an archive when a Gerber folder holds no artwork of its own. It is unpacked to a temporary folder, imported from there, and deleted again; nothing is added to the folder you named. Without this flag, such a folder is a refusal that names the flag. |

### Which cell gets exported {#convert-cell}

A GDSII library, a DXF drawing and a board file can all hold more than one cell, and an export writes
one design. Unless `--cell` says otherwise, `convert` takes the source's own idea of the top: the
GDSII structure nothing else instances, DXF's model space (the drawing itself, not a `BLOCK`
definition), the board rather than one of its footprints. A Gerber set is always one flat cell. When
the source genuinely has no unambiguous top, the conversion stops and tells you to name one —
`--list-cells` prints the choices.

### The technology, and why it matters here {#convert-tech}

An import brings a layer table with it, and in the GUI those layers land on the technology your
workspace already has open. Headless there is no open workspace, so `convert` **writes a `.ctech` of
its own** from what the file declared, exactly as **File ▸ Import ▸ Gerber** does. That is what keeps
layer names, colours and Gerber file suffixes alive across a conversion instead of leaving every layer
a bare number.

Two consequences worth knowing:

- **`--tech` is how you convert against a process you already have.** Point it at a `.ctech` and the
  source's layers reconcile against it — matched layers keep your names and your Gerber suffixes,
  unmatched ones are added. Without it, an intermediate technology is invented from the file alone,
  and a Gerber export then names its files from synthetic suffixes.
- **`--keep-cells <dir>` leaves a design you can open.** Cells plus the technology they point at —
  the honest way to see what a conversion actually understood before you send the result anywhere.

**GDSII is the one exception, and it is the format's own doing.** GDSII identifies a layer by a
number, not a name, so an import has nothing to name it *with*: the numbers come through exactly, the
names do not. Convert from GDSII with `--tech` pointing at the technology those numbers belong to and
the names come back.

### When it refuses {#convert-refusals}

<div class="callout note">
<span class="label">A drill file that does not state its format is a refusal, not a guess</span>
<p>Many Excellon files do not say whether their coordinates are inches or millimetres, or whether
leading or trailing zeros are suppressed — and leading versus trailing differ by <em>four orders of
magnitude</em> on identical text. The GUI asks you. There is nobody to ask here, so the conversion
stops, prints what it inferred and the evidence behind it — including whether the holes land inside
the artwork's own outline — and names the flags that answer it. Accept the inference with
<code>--accept-inferred-drill-format</code>, or state it outright with <code>--drill-units</code>,
<code>--drill-format</code> and <code>--drill-zeros</code>.</p>
</div>

**A `--drill-*` flag settles the whole set, not the first file.** A drill flag is a statement about
the run — one exporter wrote the `.drl` and the `.rou` next to it in one format — so it applies to
every drill file the conversion reads, and the refusal is printed once rather than once per file.
`--accept-inferred-drill-format` works the same way, with one difference worth knowing: it accepts
**each file's own** inference rather than forcing the first file's format onto the rest.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert fab/ -o board.kicad_pcb --drill-units mm --drill-format 3:4 --drill-zeros leading</code></pre>

Reach for the flags less often than you might expect: a file that writes every coordinate at its full
width — same number of digits throughout, leading zeros intact — states its own format by doing so,
and the conversion reads it off the coordinates and says as much. The flags are for the files that
leave a genuine question, and the note printed for every drill file names which parts of its format
were **declared**, which were **inferred**, and from what.

It also stops, rather than guessing, when a design instantiates cells drawn against a *different*
technology and the layer mapping needs confirming; when coordinates overflow GDSII's 32-bit range; and
when the source holds several cells and none of them is an unambiguous top. Every refusal exits `1`
and writes nothing at all.

Everything short of a refusal is a **note on stderr**, counted and named: labels flattened to
geometry, curves turned into polygons, holes keyholed, bitmaps dropped, unresolved instance
references, layers with no mapping in the target format. stdout carries only the paths written, one
per line, so a script can consume them directly:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert board.kicad_pcb -o fab/ --to gerber 2&gt; convert.log | zip -j fab.zip -@</code></pre>

## `new` — a workspace or a cell {#new}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf new workspace &lt;dir&gt; [--name N] [--tech &lt;id&gt;|none]
<span class="prompt">$ </span>circuitrf new cell &lt;workspace&gt; &lt;cellName&gt; [--views schematic,symbol,layout]</code></pre>

These create the **first correct document** — the thing that is awkward to write by hand because the
folder structure and the primacy files have to be right before anything will open it.

They are not a second implementation. `new workspace` calls the same function **File ▸ New
Workspace** calls, and `new cell` the same one **New Cell** calls, so a tree created here is a tree
the application created.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf new workspace ~/designs/Amp --tech pcb-4layer_FR-4_62mil_1oz
<span class="output">/home/you/designs/Amp
/home/you/designs/Amp/.cws
/home/you/designs/Amp/tech/pcb-4layer_FR-4_62mil_1oz.ctech</span>

<span class="prompt">$ </span>circuitrf new cell ~/designs/Amp Stage1 --views schematic,symbol
<span class="output">/home/you/designs/Amp/Stage1
/home/you/designs/Amp/Stage1/schematic/Stage1.csch
/home/you/designs/Amp/Stage1/symbol/Stage1.csym</span></code></pre>

**The paths it creates are the result**, on stdout, because what you do next is almost always read or
rewrite one of them.

<div class="callout note">
<span class="label">Every default is the dialog's</span>
<p>Whatever the GUI's dialog pre-selects, the verb selects with no flag: <code>--tech</code> opens on
the same technology the <b>New Workspace</b> combo box opens on (<code>--tech none</code> is its
"None" row), and <code>--views</code> defaults to <code>schematic</code>, which is what <b>New
Cell</b> creates. Anything the dialog would have <em>asked</em> is a refusal that names the flag
answering it — never a guess.</p>
</div>

**There are deliberately no per-primitive edit verbs.** There is no `place-instance` and no
`set-parameter`: once a document exists, the way to change it is to **write** it. Every format
circuitRF owns is readable, versioned JSON — see [File formats](file-formats.html) — and that file
*is* the interface.

<h3 id="new-add">`new` is one verb with a noun</h3>

`new workspace` and `new cell` are two nouns of one verb, not two verbs. It reads better and, more to
the point, the number of top-level verbs is a cost every reader of `--help` pays.

---

## `import part` — a footprint and its symbol {#import}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf import part &lt;file-or-folder&gt; --into &lt;workspace&gt; [--cell N] [--variant V]
                                            [--list-parts] [--tech f.ctech] [--add-layers]</code></pre>

The same code **Import Component** runs: it reads a downloaded component — a land pattern, a symbol,
and the pin-to-pad map that joins them — and writes it into your workspace as one cell.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf import part downloads/SOT-23.zip --into ~/designs/Amp --list-parts
<span class="output">SOT-23-3
SOT-23-5</span>

<span class="prompt">$ </span>circuitrf import part downloads/SOT-23.zip --into ~/designs/Amp --cell SOT-23-3
<span class="output">/home/you/designs/Amp/SOT-23-3
/home/you/designs/Amp/SOT-23-3/layout/SOT-23-3.clay
/home/you/designs/Amp/SOT-23-3/symbol/SOT-23-3.csym</span></code></pre>

A source holding several parts is **refused with them listed**, never resolved by taking the first.

**Layers the technology does not have are reported, and nothing is written**, unless you pass
`--add-layers`. The GUI's own install is session-only and writes nothing to disk either, so this is
the same behaviour and not a headless restriction.

---

## `render` — a picture of a document {#render}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render &lt;path&gt; -o &lt;out.svg|.pdf|.png&gt; [options]</code></pre>

Turns a schematic, a symbol, a layout or a data display into a file you can look at, put in a report,
or diff between two commits. **Every pixel comes out of the renderers the editors draw each frame
with** — the same Skia that produces the picture on the canvas — so the file is what the window would
have shown, not an approximation of it.

**One verb over every document kind**, inferred from the path exactly as `check` and `explain` infer
it. There is no `render-schematic`.

| Path | Resolved by |
|---|---|
| a `.csch`, `.csym`, `.clay` or `.cdd` | directly — **including one in a folder with no workspace above it** |
| a cell folder | `--view`, or the one view it holds. More than one is a refusal listing them |
| a workspace | `--cell <name>` — rendering "the workspace" is not a picture of anything |

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render ~/designs/Amp/Stage1/layout/Stage1.clay -o stage1.png
<span class="output">Wrote stage1.png (1600x1200 device-pixels, 11,540 bytes)
  4 of 4 shape(s) drawn, 12 vertices emitted, 7 draw call(s)</span></code></pre>

**`-o` is required and its extension picks the format** — `.svg`, `.pdf` or `.png`; `--format`
overrides it. There is no picture on stdout: stdout is the *result*, and `--json` has to be able to
co-exist with the write.

<div class="callout note">
<span class="label">A document with no workspace above it renders</span>
<p>It resolves no technology, draws on the generated fallback palette exactly as the layout editor
does with an unresolved technology, and says so as a <b>note</b> — not a warning and never a refusal.
A <code>.clay</code> a converter just handed you has no workspace by construction, and treating that
as a problem would teach you to skip the warnings that <em>are</em> problems.</p>
</div>

<h3 id="render-viewport">The viewport, and the unit rule</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render &lt;path&gt; -o out.png --fit [--margin 0.10]
<span class="prompt">$ </span>circuitrf render &lt;path&gt; -o out.png --window x0,y0,x1,y1
<span class="prompt">$ </span>circuitrf render &lt;path&gt; -o out.png --center x,y --span w</code></pre>

The three are **refused together rather than ordered**. A precedence nobody stated is an invention, so
`--fit --window …` stops rather than quietly picking one.

<div class="callout warn">
<span class="label">On a layout, every coordinate carries a unit — zero included</span>
<p><code>--window 0,0,500,300</code> is <b>refused</b>. It could mean database units, micrometres or
millimetres, and those are three pictures six orders of magnitude apart — all of which come back
looking entirely reasonable. Write <code class="nowrap">--window 0um,0um,500um,300um</code>; the
suffixes are <code>nm</code>, <code>um</code> (or <code>µm</code>, or <code>u</code>),
<code>mm</code>, <code>mil</code> and <code>in</code>. The refusal prints the same number spelled
the two ways you most plausibly meant, plus the document's own display unit when it is neither.</p>
<p>A schematic or a symbol takes <b>bare numbers</b>, because its coordinates really are dimensionless
design units — and <code>--json</code> says <code>"unit": "design-units"</code> rather than leaving you
to assume metres.</p>
<p><b>You do not have to write one from scratch.</b>
<a href="#explain-extents"><code>explain --extents</code></a> prints a ready-made
<code>--window</code> line for the whole document and for each layer, in exactly this spelling. Copy
it; there is no conversion to get wrong.</p>
</div>

**A window of a different shape from the page is letterboxed, never cropped and never stretched.** You
get the whole region you asked for, with bars; the resolved window is in the `--json` document with
`letterboxed` beside it. Silently giving you less of a region than you asked for is not something you
could notice from the picture.

**`--fit` frames the box that gets *painted*, not the box that is stored.** A label's stored extent is
its anchor point, an EM port paints a width bar and an arrow past its own geometry, and an instance's
extent resolves through the cell it places. On a **symbol** the two genuinely differ: pin names are
drawn in pixels, at a size with a floor, so they have no world extent until a page size is chosen — a
fitted symbol page is therefore wider than the box [`explain --extents`](#explain-extents) reports,
which is the box you want when you are sizing a `--window` yourself.

<h3 id="render-detail">Size, and what `--detail` costs</h3>

| Option | What it does |
|---|---|
| `--size WxH` | Device pixels for `.png`, points for `.svg`/`.pdf`. Default `1600x1200`. |
| `--scale n` | Raster multiplier — `2` is "@2x". **`.png` only**; a refusal on a vector format, which has no pixels to multiply. |
| `--dpi n` | The same number spelled relative to 96 dpi. Refused together with `--scale`. |
| `--detail` | `full`, `screen`, or a pixel budget. How much geometry comes out. Layout only. |

**`--detail full` is the default, and it is not free.** `full` turns every level-of-detail tier off, so
what is *stored* is what is *drawn*; `screen` engages the tiers exactly as a canvas does at that zoom,
which is the picture a person actually looks at. Measured on a real six-layer board — 3,284 shapes and
**764,032 vertices** — framed whole at `1600x1200`:

| Format | `--detail full` | `--detail screen` |
|---|---|---|
| `.svg` | **23.5 MB**, 1.44 s | 6.2 MB, 0.36 s |
| `.pdf` | 9.6 MB, 1.22 s | 2.6 MB, 0.51 s |
| `.png` | 1.4 MB, 0.55 s | 1.0 MB, 0.35 s |

A vector file stores every vertex, so `full` is nearly four times the SVG for a picture that cannot
show the difference at that zoom. A raster barely moves, because its size is set by its pixels and not
by the geometry behind them.

**So: `--detail screen` when you want the picture. `--detail full` when you want the geometry** — a
plot you will zoom into, an SVG something downstream will read the paths out of, a diff between two
revisions of the artwork. A pixel budget (`--detail 0.5`) sits between them; `--json` reports the
`toleranceDbu` it actually resolved to, which is bucketed by octave and so is rarely the number you
asked for.

The verb prints the vertex count and the file size it produced, and `--json` carries both, so you find
this out from the answer rather than from a 23 MB file.

<h3 id="render-layers">Layers and colour</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render Stage1.clay -o top.png --layers "Top Copper,Silk Top"
<span class="prompt">$ </span>circuitrf render Stage1.clay -o nosilk.png --hide-layers "Silk Top,Silk Bottom"</code></pre>

Layout only, mutually exclusive, and the default is every layer the resolved technology marks visible —
which is what the editor honours, not "all layers regardless".

<div class="callout note">
<span class="label">Ask <code>explain --layers</code> before <code>render --layers</code></span>
<p>A technology's layer list and the layers a <em>document</em> draws on are different sets, and only
the second one puts anything in the picture.
<a href="#explain-layers"><code>explain --layers</code></a> gives you both at once: every layer the
technology defines, with how many shapes this document has on it.</p>
<p>A name that is neither in the technology nor drawn on by the document is a <b>refusal</b> that
lists the real ones. It is not a silent skip, because a misspelled layer and a genuinely empty layer
produce the same picture and you would have no way to tell which you were looking at. A layer the
document draws on that the technology does <em>not</em> define — ordinary after an import — is
accepted under the generated <code>L&lt;layer&gt;/&lt;datatype&gt;</code> name
<code>explain --layers</code> prints for it, and is excluded like any other when you name a different
one.</p>
</div>

<h4 id="render-fit-layers">Framing on some layers and drawing all of them</h4>

**A fit frames what it draws.** So hiding a layer takes it out of the framing as well as out of the
picture — which is usually what you want, and occasionally not. The case that bites is an imported
board: the drill-map fabrication drawing sits far outside the board outline, and framed with everything
else it shrinks the board to a corner of the page.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render board.clay -o board.png --fit-layers "Top Copper,Bottom Copper"</code></pre>

`--fit-layers` frames on those and **draws everything** — so the drill map is still there, it simply
falls outside the frame. It is refused with `--window` and `--center/--span`, which state the frame
outright, and refused when the layers you named draw nothing, because framing on nothing is not a page.
With `--json` each layer row says whether the frame was taken from it.

<h4 id="render-layer-colors">Recolouring a layer for one picture</h4>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render board.clay -o copper.png \
      --layers "L1 Copper,L2 Copper,L3 Copper" \
      --layer-colors "L1 Copper=#e04030,L2 Copper=#30a050,L3 Copper=#3060e0"</code></pre>

A Gerber import gives every copper layer nearly the same colour, so a copper overlay comes out
unreadable. `--layer-colors` says how a layer draws **for this render only** — nothing is written to
the technology, because changing a design to change a picture of it is not a fix. The flag can be
repeated, and one entry can carry several comma-separated pairs.

Colours are `#rgb`, `#rrggbb` or `#rrggbbaa`. **The eight-digit form sets the layer's fill opacity**,
which is the alpha the renderer actually paints a fill through — a layer's own colour alpha is not read
at all, so an override that only set it would look applied and change nothing. Layer names are the ones
[`explain --layers`](#explain-layers) prints, including the generated `L<layer>/<datatype>` names an
import produces; a name that is not one of them, or a value that is not a colour, is a refusal rather
than a silent skip. `--json` reports each layer's `color` and `fillOpacity` **as drawn**, which is how
you check an override landed — a colour change is the one thing a picture alone cannot confirm.

| Option | What it does |
|---|---|
| `--theme` | A theme *name*, or the path to a `.ccolor` file. Default: the workspace's own recorded theme, else the shipped one. A name that resolves to nothing is a refusal listing where it looked. |
| `--variant` | `light` or `dark`. Default `light`. |
| `--background` | `opaque` or `transparent`. Default `opaque`. |
| `--grid` | Draw the grid. Off by default, as every export is. |
| `--no-rulers` | Leave the rulers out. |

**Rulers are on by default and everything else is off.** A ruler is *document content* — it is in the
`.clay` and the layout editor treats it that way — so an export that dropped it would be showing you a
different document. Selection chrome, handles, the marquee, PCell pin overlays, snap glyphs and the
EM/DRC overlays are all views of editor state rather than of the file, and none of them can appear here
at all.

<h3 id="render-cdd">A data display — the same verb, a different anatomy</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render Amp.cdd -o amp.pdf [--data run.npy]... [--tab name|n] [--plot n] [--all-tabs]</code></pre>

A `.cdd` is the fourth document kind, and the one that is not a drawing: **it holds no data.** Its
traces name a *source* — a file, or the sentinel meaning "whatever this display has selected" — and
every curve is re-resolved from a file on disk each time it opens.

<div class="callout warn">
<span class="label">A source that does not resolve is a refusal, never an empty plot</span>
<p>This is the rule the whole <code>.cdd</code> path is arranged around. An empty plot is a valid
picture: it draws, it exports cleanly, and it looks exactly like a measurement that genuinely came back
empty. So every source the pages you asked for reference is resolved <b>before anything is drawn</b>,
and one that cannot be is a refusal naming <code>--data</code>.</p>
<p><code>--data</code> may be repeated and binds in order; the first one satisfies the document's own
selected source. <b>A <code>--data</code> that binds nothing is a refusal too</b> — handing over last
week's run must not silently get you a picture drawn from whatever was lying beside the document.</p>
</div>

A reference is looked for beside the `.cdd`, then under the nearest ancestor workspace's `results/`.
`--tab` takes a name or a 1-based number (a tab literally called `2` beats the second tab), `--plot` a
1-based number within it, and the default is the tab the display opens on. **`--all-tabs` is PDF's
alone** — a PDF is a multi-page format and SVG and PNG are not, and inventing `out-1.svg`,
`out-2.svg` from one `-o` would be this tool naming your files for you. On those it is a refusal
naming `--tab`.

**`--size` replaces the page and nothing else about the composition changes.** The default is the
792×612 pt landscape page with 36 pt margins, and the plots, axis label strips and any marker info
boxes you dragged are fitted to it as one group — so a box you moved lands in the file where it sits on
screen. The options that describe a *drawing* — `--window`, `--center`, `--span`, `--fit`, `--layers`,
`--hide-layers`, `--detail`, `--view`, `--cell`, `--grid`, `--no-rulers` — are each a refusal naming
themselves, because a display has no world coordinates and no layers, and a `--window` that silently
did nothing would give you a full picture you believed was a crop.

With `--json`, `result.render.dataDisplay` names **every source and the file it actually resolved to**,
and which of `--data` or the document bound it. That is the part the picture cannot tell you: "the plot
is empty" and "the plot read the wrong run" look identical.

<h3 id="render-example">A worked example, from an empty folder</h3>

Two commands to set it up, three questions, one picture. The point of the sequence is that the three
questions are what make the last command *writable*: you cannot name a layer or size a window without
first asking what the document has.

<pre><code class="cmd"><span class="prompt">$ </span>mkdir work &amp;&amp; cd work
<span class="prompt">$ </span>circuitrf new workspace Amp --tech pcb-2layer_FR-4_70mil_1oz
<span class="prompt">$ </span>circuitrf new cell Amp Stage1 --views layout</code></pre>

That is a correct, empty layout. Give it some artwork — a 20 mm line on the top copper, a ground plane
under it, and a label on the top silk:

```json
{
  "FormatVersion": 1,
  "DbuPerMicron": 1000,
  "DisplayUnit": "Um",
  "Shapes": [
    { "$type": "Rect",  "Layer": { "Layer": 1, "Datatype": 0 },
      "X1": 0, "Y1": 0, "X2": 20000000, "Y2": 2900000 },
    { "$type": "Rect",  "Layer": { "Layer": 2, "Datatype": 0 },
      "X1": 2000000, "Y1": -3000000, "X2": 18000000, "Y2": -1000000 },
    { "$type": "Label", "Layer": { "Layer": 5, "Datatype": 0 },
      "X": 400000, "Y": 3400000, "Text": "STAGE 1", "Height": 500000 }
  ]
}
```

**Which cells are in here, and which views does each have?**

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Amp --cells
<span class="output">Amp  (workspace)
  workspace    /home/you/work/Amp/.cws
               via nearest ancestor .cws
  cells: 1
    Stage1               /home/you/work/Amp/Stage1
      schematic  no-view                (none)
      symbol     no-view                (none)
      layout     sole-file              Stage1.clay</span></code></pre>

**What may I ask for with `--layers`?** The technology defines eight; this document uses three.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Amp/Stage1/layout/Stage1.clay --layers
<span class="output">  layers       PCB 2-Layer FR-4 (70mil, 1oz) — 8 defined, 3 used
    Top Copper           1/0      #c87a3e  solid       purpose=drawing   1 shape(s)
    Bottom Copper        2/0      #8a5028  solid       purpose=drawing   1 shape(s)
    Soldermask Top       3/0      #1e6b3c  solid       purpose=drawing   0 shape(s)
    …
    Silk Top             5/0      #f2f2f2  solid       purpose=drawing   1 shape(s)
    …</span></code></pre>

**How big is it, so I can write a window?** In base SI, with the unit and the scale named.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Amp/Stage1/layout/Stage1.clay --extents
<span class="output">  extents      0 -0.003 .. 0.02 0.00376719  (0.02 x 0.00676719 m, scale 1E-06)
               --window 0um,-3000um,20000um,3767.188um
    Top Copper           0 0 .. 0.02 0.0029   --window 0um,0um,20000um,2900um
    Bottom Copper        0.002 -0.003 .. 0.018 -0.001   --window 2000um,-3000um,18000um,-1000um
    Silk Top             0.0004 0.00338437 .. 0.00234306 0.00376719   --window 400um,3384.375um,2343.062um,3767.188um</span></code></pre>

The line is 20 mm long and 2.9 mm wide, on `Top Copper` — and the `--window` line beside each box is
already in the spelling the next command takes. Now the left 6 mm of it, that layer only:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render Amp --cell Stage1 --layers "Top Copper" \
                        --window 0um,0um,6000um,3000um -o stage1-top.png
<span class="output">Wrote stage1-top.png (1600x1200 device-pixels, 10,399 bytes)
  1 of 2 shape(s) drawn, 4 vertices emitted, 2 draw call(s)</span></code></pre>

Three options and one verb, not five commands — because all three questions are the one question
`explain` already exists for: *what did circuitRF decide?*

---

## `check` — is it well formed, does it resolve, is it sound? {#check}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf check &lt;path&gt; [--recursive] [--severity warning|error]</code></pre>

Point it at a workspace, a cell folder, or one document. **It runs no analysis and it writes
nothing**, which is what makes it cheap enough to call after every edit — and safe to run on a
read-only tree, or on a workspace you have open in the GUI at the same time.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf check ~/designs/Amp
<span class="output">Amp/Stage1/schematic/Stage1.csch
  error   two labels name one physical net: 'vout' and 'out'
Amp/Stage1/layout/Stage1.clay
  warning no technology resolves for this layout
5 document(s): 1 error, 1 warning</span></code></pre>

**Every finding comes from a validator the application already uses** — the same view-file check, the
same primacy rule, the same technology walk-up, the same net extractor, the same elaborator, the same
DRC engine. A rule that lived only in `check` would be a rule the application does not enforce, and a
design would pass here and be refused the moment somebody opened it.

<div class="callout note">
<span class="label">Warnings are reported and still exit 0</span>
<p><code>--severity</code> decides the exit code, and it defaults to <code>error</code>. Warnings are
<em>always</em> printed — a check that hid them to keep the exit code clean would make the exit code
useless. Two states are warnings on purpose: a cell folder holding several views with none named
primary, and a layout that resolves no technology. Both are normal.</p>
</div>

The kind of document is inferred from the path, exactly as `convert` infers a format. A GDSII or
Gerber file is **named as interchange** rather than called unreadable — it is simply not validated,
because there is nothing to validate it against.

<h3 id="check-touchstone">Checking a Touchstone file</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf check part.s2p
<span class="output">note: part.s2p: 2-port, 401 points, 1 MHz to 1 GHz, reference 50 Ω.
note: part.s2p: causality not evaluated — the frequency grid is not uniformly spaced.
1 document(s) checked: 0 error(s), 0 warning(s), 2 note(s).</span></code></pre>

An `.sNp` is data rather than a design, and that changes the severity rule in one place. **Passivity,
reciprocity and causality are warnings and never errors**, each carrying the measured number and the
frequency it occurred at: nothing in a Touchstone file says what the part is, so an amplifier is
*supposed* to have gain and a circulator is *supposed* to be non-reciprocal. Only the unambiguous
defects — unreadable, a port count that contradicts the file's own name, a frequency axis that is not
sorted, a reference impedance no renormalisation can use — are errors.

A **folder walk deliberately skips Touchstone files.** A kit directory holds hundreds of them, and
measuring passivity and causality across all of them would bury a workspace's own findings under notes
about parts you did not author. Naming the file is what checks it.

What each finding means, and the limits of the causality measurement, are on the
[Derived Metrics](derived-metrics.html#headless) page.

---

## `explain` — what did circuitRF decide? {#explain}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain &lt;path&gt; [--expr "&lt;expression&gt;"] [--set var=expr]
                            [--analysis [&lt;name&gt;]] [--ref &lt;relative-ref&gt;]
                            [--cells [--all]] [--layers] [--extents] [--view &lt;name&gt;]</code></pre>

`check` answers "is something wrong". `explain` answers the question that is **not** a failure: which
technology did this layout get, which analysis would actually run, what does this expression evaluate
to here, what does this cell reference point at, what cells and layers are in here, and how big is it?

**Six questions, and they are refused together rather than ordered** — one per run. A precedence
nobody stated is an invention, and it would answer a question you did not ask while looking like it
answered the one you did.

It reports **the walk as well as the answer**, and that is the useful half. Resolution in circuitRF is
a series of walk-ups — a document's ancestor workspace, a layout's technology, a `.cem`'s two
independent references — and which one produced an answer is exactly what you cannot see from the
file.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Amp.cem
<span class="output">workspace   from Amp.cem            → /home/you/designs/Amp/.cws        (nearest ancestor .cws)
layout      from Amp.cem            → Amp/Line/layout/Line.clay         (workspace-relative)
workspace   from Line.clay          → /home/you/parts/.cws              (nearest ancestor .cws)
technology  from /home/you/parts    → parts/tech/pcb-2layer.ctech       (the .cws DefaultTechRef)</span></code></pre>

Two different workspaces there, and that is legitimate: a `.cem` in one workspace may point at a
layout in another, and that layout's layers must be read by *its* technology.

<h3 id="explain-analysis">`--analysis` — which chain would run</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain pa.cnl --analysis
<span class="output">SWEEP1   parametric_sweep   enabled  runnable  root   → dispatched by hb
  chain: SWEEP1 → HB1
  sweep: Pavl over 1.0000e+09 … 3.0000e+09 step 5.0000e+08 Hz  (stated GHz, scale 1e9)
HB1      hb                 enabled  runnable         → promoted to SWEEP1</span></code></pre>

A sweep is reported in **base SI with its unit and its scale**. Reading a mark without its scale has
already produced a run at 2 Hz that looked entirely normal.

`runnable` is two claims, not one: the chain reaches an enabled analysis, **and** every reference the
analysis names resolves — the tuner instances, the inner analysis a sweep wraps, the swept variable,
and the variables a tone expression reads. When one does not, the chain is not runnable and the
report says which:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain pa.cnl --analysis
<span class="output">  analyses:
    LP1              loadpull-pursuit   LP1                          not-runnable
      unresolved: LoadTuner=NoSuchTuner: no instance of that name in this document. Its Tuners are: Load.
      unresolved: SourceTuner=AlsoMissing: no instance of that name in this document. Its Tuners are: Load.</span></code></pre>

<div class="callout warning">
    <span class="label">Changed in 1.0</span>
    <p><code>runnable</code> used to mean only the first half, so the analysis above was reported
    <code>runnable</code> and <code>dispatched by lpp</code> while naming two tuners that do not
    exist. Whether a thing will run is the question this verb exists to answer, and a caller that
    acts on an optimistic yes gets its refusal later, about something it has already been told is
    fine.</p>
    <p>What <code>explain</code> still does <em>not</em> claim is anything that would need a solve —
    "this bench has no bias source" is a property of the solved circuit, and this verb solves
    nothing. It reports what it can establish and stays quiet about the rest.</p>
  </div>

<h3 id="explain-touchstone">`explain` on a Touchstone file — what IS this part?</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain part.s2p
<span class="output">part.s2p  (touchstone)
  ports        2
  sweep        401 points, 1 MHz to 1 GHz, non-uniform
  reference impedance 50 Ω
  SRF (shunt-through)     22.507906 MHz
  |Z| min (shunt-through) |Z| = 5.057 mΩ at 22.387211 MHz, ESR 5 mΩ
  SRF (series-through)    (nothing)
  |Z| min (series-through) |Z| = 796.177 Ω at 1 GHz, ESR 1.268 Ω</span></code></pre>

The self-resonance and the impedance floor are what a decoupling capacitor is chosen on, and before
this verb the only way to see them was to build a schematic around the file and plot it.

**Every applicable fixture is reported side by side rather than one being chosen.** Nothing in the file
records how the part was measured, and seeing all the readings is the fastest way to identify an
unlabelled one — above, the series reading finds no resonance at all and puts the floor five orders of
magnitude out, so the part is plainly a shunt-mounted capacitor. The equations behind each reading are
on the [Derived Metrics](derived-metrics.html#fixture) page.

<h3 id="explain-expr">`--expr` — evaluate in the design's own scope</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain pa.cnl --expr "Zopt*2" --set Zopt=12.5
<span class="output">Zopt*2 = 25   (real)</span></code></pre>

Through the one expression engine, in the design's own resolved scope — never by substitution — with
`--set` applied first exactly as a run verb applies it. The kind is reported, never coerced.

<h3 id="explain-ref">`--ref` — where does this reference land</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Stage1.csch --ref ../parts/SOT-23-3
<span class="output">../parts/SOT-23-3 → /home/you/designs/parts/SOT-23-3   resolved
                     outside this workspace: no</span></code></pre>

Where resolution fails, **that is the answer** — a sentence naming what was looked for and where it
was looked. You are usually running this verb precisely because something did not resolve.

<h3 id="explain-cells">`--cells` — what cells are in here?</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain ~/designs/Amp --cells
<span class="output">Amp  (workspace)
  workspace    /home/you/designs/Amp/.cws
               via nearest ancestor .cws
  cells: 2
    Balun                /home/you/designs/Amp/Balun
      schematic  named-present          Balun.csch   of 2: alt.csch, Balun.csch
      symbol     no-view                (none)
      layout     missing-named-primary  Balun_rev3.clay   of 2: Balun.clay, Balun_rev2.clay
    Stage1               /home/you/designs/Amp/Stage1
      schematic  sole-file              Stage1.csch
      symbol     sole-file              Stage1.csym
      layout     sole-file              Stage1.clay</span></code></pre>

**It reports the resolution, not a directory listing**, and that is the whole reason to run it rather
than `ls`. The interesting rows are the ones where the answer is not obvious, and there are five
states, never collapsed into fewer:

| State | Means |
|---|---|
| `sole-file` | One file in the sub-folder; it is primary by being the only one. |
| `named-present` | Several files, and the cell names one of them. |
| `missing-named-primary` | Several files, and the cell names one that **is not there**. A flat contradiction, and the state you most need to see — the name it looked for is printed. |
| `no-primary` | Several files and none named. Not an error; nothing has chosen yet. |
| `no-view` | The sub-folder is empty, or there is none. |

Each is listed **with its state** — never omitted, and never quietly resolved to the alphabetically
first file, which is the answer that would look right and be wrong.

Point it at a **cell folder** and you get that one cell in the same shape, which is what makes it
compose with `render`: ask what views a cell has, then render one. `--all` includes generated cells,
which are hidden by default exactly as the project tree hides them. This is the same enumeration
`render --cell` resolves through, so a cell listed here is a cell that verb can draw.

<h3 id="explain-layers">`--layers` — what may I ask for?</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Stage1.clay --layers
<span class="output">  technology   /home/you/designs/Amp/tech/pcb-2layer_FR-4_70mil_1oz.ctech
               via the workspace's DefaultTechRef — the layout states none
  layers       PCB 2-Layer FR-4 (70mil, 1oz) — 8 defined, 3 used
    Top Copper           1/0      #c87a3e  solid       purpose=drawing   1 shape(s)
    Bottom Copper        2/0      #8a5028  solid       purpose=drawing   1 shape(s)
    Soldermask Top       3/0      #1e6b3c  solid       purpose=drawing   0 shape(s)
    Silk Top             5/0      #f2f2f2  solid       purpose=drawing   1 shape(s)
    …</span></code></pre>

**Two different sets, side by side, and the second is the one that draws anything.** The technology's
layer table is what [`render --layers`](#render-layers) will accept; the shape count is what this
document actually has on each. A layer with a colour, a purpose and **0 shapes** is a name you may pass
that will produce nothing.

The count walks the **hierarchy**: a layer used only inside a placed sub-cell counts, and an array
placement counts its shapes once per element. It is deliberately not `render --json`'s
`counters.shapesDrawn`, which counts only the top-level shapes one frame issued a draw call for.

Two things it cannot tell you, both worth knowing:

- **A via is reported on its barrel layer only.** A via carries a barrel layer and a landing layer, and
  the renderer draws the whole annulus on the barrel one — so a landing layer a via field's pads are
  notionally on can report zero shapes.
- **A layer the fallback palette invented is listed too, and marked.** A key the document draws on that
  the technology does not define is ordinary after an import, and it *renders*. Omitting those rows
  would report a document as drawing on layers it does not and hide the ones it does. The generated
  name is a name [`render --layers`](#render-layers) accepts.

<h3 id="explain-extents">`--extents` — how big is it?</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Stage1.clay --extents
<span class="output">  extents      0 -0.003 .. 0.02 0.00376719  (0.02 x 0.00676719 m, scale 1E-06)
               --window 0um,-3000um,20000um,3767.188um
    Top Copper           0 0 .. 0.02 0.0029   --window 0um,0um,20000um,2900um
    Bottom Copper        0.002 -0.003 .. 0.018 -0.001   --window 2000um,-3000um,18000um,-1000um
    Silk Top             0.0004 0.00338437 .. 0.00234306 0.00376719   --window 400um,3384.375um,2343.062um,3767.188um</span></code></pre>

**In base SI, with the unit *and* the scale named** — the rule `--analysis` already follows, for the
reason it follows it: reading a mark without its scale has already produced a run at 2 Hz that looked
entirely normal. A schematic or a symbol reports `design-units` at scale 1, because its coordinates are
dimensionless and dressing them up in metres would be a lie.

This is **the same function `render --fit` frames on**, which is why you can size a `--window` from it
and get exactly the region you expected. The per-layer boxes are the document's own shapes; an
instance's extent arrives as one box for the whole placement.

<div class="callout note">
<span class="label">The <code>--window</code> line is meant to be pasted</span>
<p>Every box comes with the same numbers written the way
<a href="#render-viewport"><code>render --window</code></a> takes them — in this document's own display
unit, whole document and per layer, and in <code>--json</code> as a <code>window</code> field beside
the coordinates. <b>The metres above are deliberately not what you type</b>: a layout coordinate
carries a unit there, and <code>m</code> is not one of the suffixes it reads. Copy the
<code>--window</code> line and you get exactly that box; there is no conversion to do and no chance of
being three orders of magnitude out.</p>
<p>So <em>framing on one layer</em> is a copy rather than a calculation — and if you want to frame on
one layer while still drawing the others, that is
<a href="#render-layers"><code>render --fit-layers</code></a>.</p>
</div>

An **empty** document says so rather than reporting a zero box, because a zero box is a point at the
origin and that is a different fact. On a **symbol**, or on a layout carrying a fixed-size ruler, the
report adds a note: the fit adds room for marks that are drawn in *pixels* — a pin's name, a ruler's
readout — which have no world extent until a page size is chosen. Those marks are why a fitted symbol
page is wider than the box reported here.

---

## `read` — a result, or a document, back {#read}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf read &lt;path&gt; [--only a,b] [--group g] [--json]</code></pre>

The inverse of a run verb. A `.npy` or a Touchstone file is loaded back into cubes — through the same
two loaders the Data Display's source library reads a file with — and one of circuitRF's own
documents comes back as **its own bytes**, unchanged.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf read results/Amp_em.npy
<span class="output">results/Amp_em.npy  (npy)
  group (default):
    S                        Complex  freq[201] Hz x i[2] port x j[2] port
    Z0                       Complex  port[2] port
  group planar:
    MeshCells                Real     cell[1544]</span></code></pre>

It writes nothing, and it takes one file at a time. For "what is in this workspace", use `check` or
`explain`; for a GDSII or a Gerber set, use `convert`.

With `--json`, `--only` and `--group` narrow what comes back — which matters, because reading a
20,000-point swept loadpull in full is the expensive direction:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf read hero3.npy --only Pout_dBm --json</code></pre>

---

## `netlist` — the netlist a schematic runs as {#netlist}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf netlist &lt;path.csch | cell-folder | workspace --cell N&gt; [-o out.cnl]</code></pre>

The extraction **Simulate** performs, as a file you can read. It takes a `.csch`, a cell folder, or a
workspace with `--cell` — the same three inputs [`render`](#render) takes, resolved the same way.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf netlist Example_SParam_LC.csch
<span class="output">; extracted from Example_SParam_LC

Port:Term1  n1  0  Num=1  Z=50 Ohm
L:L1  n1  n2  L=2 nH
C:C1  n2  0  C=0.8 pF
Port:Term2  n2  0  Num=2  Z=50 Ohm

analysis SP1 type=sparam start="1" startUnit=GHz stop="5" stopUnit=GHz npts=201</span></code></pre>

Without `-o` the netlist goes to stdout, so `circuitrf netlist Stage1.csch &gt; stage1.cnl` is the file
and nothing else. With `-o` it is written there, and the path is what goes to stdout.

<div class="callout">
<span class="label">This is the netlist a run actually consumes — the same bytes</span>
<p>You do not need it to simulate a drawing: every run verb takes a <code>.csch</code> directly and
extracts it in memory. What this verb is for is <b>seeing</b> that extraction — and checking a
<code>.cnl</code> you wrote by hand against what circuitRF produces for the equivalent schematic.
Both go through one function, so the file here is not merely equivalent to what a run reads, it is
byte for byte the same text.</p>
</div>

`-o` takes a `.cnl` and refuses any other extension — there is one format here. A `.cnl` input is
refused too: passing it through the reader and the writer would hand back a file that is not the one
you gave (comments gone, directives reordered) and call it an extraction.

---

## `plot` — a picture of a result {#plot}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf plot &lt;result.npy|.sNp&gt; -o &lt;out.svg|.pdf|.png&gt; --trace &lt;spec&gt; [--trace &lt;spec&gt;]…</code></pre>

One plot, one axis pair, without writing a data display first.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf plot lc.s2p -o match.png \
<span class="prompt">    </span>--trace cube=S,i=1,j=1,y=db --trace cube=S,i=2,j=1,y=db \
<span class="prompt">    </span>--title "LC lowpass" --ylabel dB --x 1:5 --y -40:5
<span class="output">Wrote match.png (792x612 device-pixels, 19,785 bytes)
  1 plot(s) on 1 page(s), 1 data source(s)</span></code></pre>

**A trace spec is comma-separated `key=value`:**

| Key | What it means |
|---|---|
| `cube` | Which cube. Required. It is the same shorthand the trace card's spec box takes, so `S`, `S[:,2,1]`, `Pout` and `mag(V[:,"X1.drain"])` all work. |
| `i`, `j` | The **port numbers** of a matrix cube — `i=2,j=1` is S21. Refused together with a bracketed slice: they are the convenience over writing one. |
| `y` | `db`, `db10`, `db20`, `mag`, `phase`, `real`, `imag` or `conj`. |
| `axis` | `left` (the default) or `right`. |
| `cut` | An antenna pattern cut: a bearing in degrees pins the cube's `phi` axis and sweeps `theta`; `all` keeps every `phi` as a curve family. The verb prints the φ it landed on. |
| `port` | The **port number** on a cube's `port` axis — not an index. A port the run does not hold is refused, listing the ones it does. |
| `freq` | Pins a `freq` axis to its nearest sample. Takes an SI suffix: `2.45G`. |
| `probe` | A [WSProbe](wsprobe.html) label. Given, it turns a `cube=<analysis>.wsp` trace into a probe metric. |
| `metric` | Which of the reference document's quantities — `H0`, `1/Y0`, `ZG`, `SM_Y0`, `LGa`, `SMenv`, … See [The WSProbe](wsprobe.html#metrics). Some take `with=`, `set=`, `z0=`, `side=`, `gi=` or the envelope's grid keys. |

```text
circuitrf plot amp.npy -o margin.svg --trace cube=SP1.wsp,probe=GATE,metric=SM_Y0,y=db
```

**Case is load-bearing in that notation and is not folded away** — `LGF` is one probe's forward
synthetic-circulator loop gain and `LGf` is a probe *pair's* feedback-as-synthetic-FET loop gain.
A name whose canonical form is shared by two quantities resolves only when it is spelled exactly.

`circuitrf read <result>` lists the cubes a file holds and their axes, which is where the names come
from. A cube the file does not hold is refused, listing the ones it does.

| Option | What it does |
|---|---|
| `-o <path>` | Required. Its extension picks the format: `.svg`, `.pdf` or `.png`. |
| `--type rect\|smith\|polar\|table` | Default `rect`. |
| `--freq-unit Hz\|kHz\|MHz\|GHz` | Default GHz. It is also the unit `--x` is read in. |
| `--title`, `--xlabel`, `--ylabel`, `--y2label` | Custom labels. Omitted, the plot labels itself. |
| `--x lo:hi`, `--y lo:hi`, `--y2 lo:hi` | Axis windows. An axis you leave out autoscales. Refused on a Smith or Polar chart, whose window is the complex plane. |
| `--size WxH`, `--scale`, `--dpi` | The page. Default 792×612 points — the same page **File ▸ Export** writes. `--scale`/`--dpi` are `.png` only. |
| `--variant light\|dark`, `--background opaque\|transparent` | As `render`. |
| `--radial linear\|db` | How a **polar** plot's radius is read. `db` makes it an antenna pattern plot. Refused on any other `--type`. |
| `--db-floor <dB>` | The centre of a `db` plot, **relative to the outer ring**. Default −40. |
| `--db-ring <dB>` | Ring spacing. Default 10. |
| `--db-ref peak\|<dB>` | The outer ring: the data's own peak (normalised, the default) or an absolute level. |
| `--db-unit <text>` | What the radial numbers are in — `dBi`, `dB(W/sr)`. |
| `--write-cdd <path>` | Also write the data display this drew. |

```text
circuitrf plot run.npy -o eplane.svg --type polar --radial db --db-unit "dB(W/sr)" \
  --trace cube=farfield.U,cut=0,port=1,y=db10
```

<div class="callout">
<span class="label">A pattern plot says what it is</span>
<p>On a <code>--radial db</code> plot the outer ring is a reference and the centre is a floor, and the
picture states which reference it is using — a 0&nbsp;dB peak with no reference is not a result.
<b>Values below the floor are drawn AT the floor, never dropped</b>: a gap in a pattern trace reads as
a null in the antenna, and a real null and a clipped value must not look the same. 0° is at the top
and angles increase clockwise, and the note under the plot says what the θ range covers — with an
infinite ground plane there is no field below the horizon, so a pattern occupying part of the disc is
the model saying so rather than a drawing fault.</p>
</div>

<div class="callout">
<span class="label"><code>--write-cdd</code> is how you go further</span>
<p>This verb draws one plot with one axis pair. Everything else a data display can do — several plots
on a page, tabs, markers, contours, a summary table — is still done by writing a <code>.cdd</code> and
calling <a href="#render"><code>render</code></a>. <code>--write-cdd</code> hands you the document
this verb built, which is a correct starting point to edit rather than a blank page; the picture it
draws and the picture <code>render</code> draws from that file are byte for byte the same, because
they are the same code.</p>
</div>

A plot with no `--trace` is refused rather than drawn. An empty plot is a valid picture that exports
cleanly and looks exactly like a measurement that came back empty.

---

## `find` — what is in this folder? {#find}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf find &lt;root&gt; [--depth n] [--no-analyses]</code></pre>

The workspaces under a directory, their cells, each cell's views, and the analyses each cell declares.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf find ./projects
<span class="output">/home/you/projects  (1 workspace(s), 2 cell(s), depth 4)
  Amp  /home/you/projects/Amp  [tech tech/generic-mmic.ctech]
    LC                       schematic: LC.csch
                             analyses: SP1
    Stage1                   schematic: Stage1.csch, layout: Stage1.clay
                             analyses: HB1, SWEEP1</span></code></pre>

It reads what the other verbs read, so a cell listed here is the cell `render` would draw and the
analyses are the ones `hb` would dispatch. It writes nothing, so it runs on a read-only tree and on a
workspace the application has open.

| Option | What it does |
|---|---|
| `--depth n` | How many levels below the root a workspace is looked for. Default 4, at most 12. |
| `--no-analyses` | Skip the analyses. Each one costs an extraction, which adds up on a large tree. |

<div class="callout">
<span class="label">The walk is bounded, and it says when it stopped</span>
<p>A listing that quietly gave up reads as "the workspace is not here". So when the walk hits
<code>--depth</code> with directories still below it, it says so — a warning on stderr and
<code>truncated: true</code> in the <code>--json</code> document. Raise <code>--depth</code> and ask
again. A directory symbolic link is never followed, so nothing outside the root you named can appear
in the answer.</p>
</div>

---

## `reference` — what may I write? {#reference}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf reference
<span class="prompt">$ </span>circuitrf reference netlist
<span class="prompt">$ </span>circuitrf reference components
<span class="prompt">$ </span>circuitrf reference components MLIN
<span class="prompt">$ </span>circuitrf reference analyses
<span class="prompt">$ </span>circuitrf reference analyses sparam
<span class="prompt">$ </span>circuitrf reference data-display
<span class="prompt">$ </span>circuitrf reference technology</code></pre>

They are, in order: the list of topics with what each costs; one page as text; every netlist
primitive; just that one; every analysis directive with every key it takes; just that one; and the
two document formats nothing else here creates for you.

`check` tells you that what you wrote is wrong. `explain` tells you what circuitRF made of it.
This one tells you what you are **allowed** to write in the first place — the primitive type names,
how many nets each takes and in what order, what its parameters are called and what they default to.

It reads no file, runs nothing and writes nothing. There is no path argument: it answers about no
particular design, which is exactly why it is useful before one exists.

<div class="callout">
<span class="label">Why this matters more for a script than for you</span>
<p>A netlist naming a type that does not exist fails at elaboration and says so. A component given a
<i>plausible but wrong</i> parameter name does not: the name is ignored, the parameter takes its
default, and the run converges and produces a complete-looking answer to a different circuit. Getting
the spelling from here rather than from memory is what avoids that.</p>
</div>

### The topics {#reference-topics}

With no arguments you get the list, with each topic's size — because reading is the expensive
direction and a 4 kB page and an 84 kB one should not look alike:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf reference
<span class="output">Reference topics — circuitrf reference &lt;topic&gt;

  netlist            17.7 kB  The Netlist (.cnl) Format
  expressions         9.1 kB  Expressions
  units              10.9 kB  Units
  measurements        5.8 kB  Measurements
  pins-ports-terms    4.3 kB  Pins, Ports &amp; Terms
  sdd                12.2 kB  The SDD (Symbolically-Defined Device)
  file-formats       11.6 kB  File Formats
  component-notes    74.2 kB  Components
  data-display       12.8 kB  The .cdd data-display format
  technology          6.1 kB  The .ctech technology format
  analyses            8.5 kB  Analysis directives
  components         91.3 kB  Component types

  circuitrf reference components &lt;TYPE&gt;   one primitive
  circuitrf reference analyses &lt;TYPE&gt;     one analysis directive</span></code></pre>

These are the same pages you are reading now, shipped inside the program so they are there on a
machine that has no copy of this site. `components` is the **generated catalogue** — read from the
live component registry every time you ask, so it cannot go stale — while `component-notes` is this
site's [Components](components.html) page, which explains what each part is *for*. The catalogue
answers "what may I write"; the page answers "what does it mean". Ask for both if you want both.

Asking for a topic prints it as its own Markdown, so it redirects cleanly:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf reference netlist &gt; netlist.md</code></pre>

### The component catalogue {#reference-components}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf reference components MLIN
<span class="output">MLIN
  nets: 2
  terminals: 2  1 2
  order: The two terminals are interchangeable: the line is uniform, so neither end is the input.
  Mlin (MLIN) — Microstrip   search: MLIN, microstrip, microstrip line, line, hammerstad
    terminals: 2  1 2
    W                  2.9              mm     shown
    L                  10               mm     shown
    SignalLayer        -                -      -
    GroundReference    -                -      -</span></code></pre>

The columns are the parameter's **name**, its **default expression**, its **unit**, whether it shows
on the schematic by default, and — where the registry has one — what it means.

`terminals` is the part you cannot get from a picture: the pins the symbol draws, **in the order a
netlist line writes their nets.** That order is a contract the models themselves read, and it is not
always the one you would guess — a MESFET is gate, drain, source while a JFET is drain, gate, source,
and a diode is anode then cathode.

<div class="callout warn">
<span class="label">`nets` and `terminals` are two different numbers, and the first is the one to write to</span>
<p><b>How many nets the instance line binds</b> is <code>nets</code>. <b>How many pins the symbol
draws</b> is <code>terminals</code>. They are usually the same and they are not always: a
<code>Tuner</code> draws one pin and its line takes two, because its reference terminal is implicit
on the glyph. So do <code>Port</code>, <code>Term</code>, <code>Vdc</code> and <code>IProbe</code>;
a 2-port <code>SDD</code> takes four nets, as ± pairs.</p>
<pre><code class="cmd"><span class="prompt">$ </span>circuitrf reference components Tuner
<span class="output">Tuner
  nets: 2
  terminals: 1  1</span></code></pre>
<p>Writing <code>Tuner:T1 n_drain Z[1]=50 BiasTee=on Vbias=48</code> — one net — is now refused by
name. It used to run: the bias tee delivered nothing, every diagnostic was clean, and the output sat
at the engine's floor at every drive point.</p>
</div>

<div class="callout note">
<span class="label">Some counts are not fixed, and they say so</span>
<p>An SDD's, a <code>Z_Port</code>'s and an <code>SnP</code>'s follow a port-count parameter; a
Verilog-A model's follows <code>Pins</code>; an ideal switch's follows <code>Throws</code>; a wBond's
follows the arrays it places. Those report <b>the rule</b> rather than a number:</p>
<pre><code class="cmd"><span class="prompt">$ </span>circuitrf reference components SDD
<span class="output">SDD
  nets: SddPortCount=N binds 2N nets.
  terminals: set by NumPorts — at NumPorts=2: 1+ 1- 2+ 2-</span></code></pre>
<p>Note that the two lines name <i>different parameters</i>, and that is not a mistake: the parameter
panel calls it <code>NumPorts</code>, and a <code>.cnl</code> instance line spells it
<code>SddPortCount</code>. A number printed where the honest answer is "it depends" is worse than no
number at all.</p>
</div>

### Analysis directives {#reference-analyses}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf reference analyses sparam
<span class="output">sparam   also: sp, s_param, sparameter, s_parameters
  bare words: log
  type                   required       The analysis kind. One of the tokens this page lists.
  enabled                true           false skips the analysis at run time without deleting it.
  start                  required       First frequency. Bare number in Hz unless a unit follows or Unit=/startUnit= is given.
  stop                   required       Last frequency, same spelling rules as start.
  step                   1e8            Step size. Mutually exclusive with npts; npts wins when both are given.
  npts                   -              Point count. Selects the point-count sweep mode.
  Unit                   -              Sets startUnit, stopUnit and stepUnit at once. Any one of those given individually overrides it.
  ...</span></code></pre>

Every `analysis type=` token, every other spelling of it that is accepted, and every key it may carry
with its default and whether it is required. It is read from the same table the `.cnl` reader
validates against, so a key that is not on this page is a key the reader **refuses** — it is not
ignored, and it has not been since the netlist contract landed.

### The two formats nothing creates for you {#reference-formats}

`circuitrf reference data-display` and `circuitrf reference technology` describe the `.cdd` and
`.ctech` files field by field, generated from the types their readers actually deserialise into, each
with a minimal example that was written out and run. They are here because those are the two formats
you may have to write by hand: `new workspace` copies a technology in for you, but nothing creates a
data display.

A few entries carry a **note** instead of a clean answer, and the note is the useful part. `GND`,
`VAR`, `MEAS` and `Pin` are schematic elements the netlist extractor consumes rather than components
you can place in a `.cnl`. `Chain`, `ExtDevice`, `SemiC`, `Short`, `Term`, `V_nTone` and `I_nTone` are
the other way round — writable in a `.cnl`, with no palette tile and so no declared defaults.

Unknown names are refused with the real list, never guessed at:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf reference components MLINE
<span class="output">No primitive type 'MLINE'. Types: Amp, Atten, BJT_NPN, BJT_PNP, Balun, Bead, C, Chain, …</span></code></pre>

With `--json` the whole catalogue comes back structured — type token, terminals, and every
parameter with its default, unit, dimension and visibility:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf reference components --json | jq -r '.result.reference.components[].type'</code></pre>

---

## `elab` — the elaborated netlist {#elab}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf elab &lt;file.cnl|.csch&gt;</code></pre>

Elaborates and stops: flattens the hierarchy, resolves every parameter and expression top-down, and
numbers the nodes, then prints [the elaborated netlist](netlist.html) — the exact thing the engines
consume. No analysis runs.

This is the debugging verb. When a value is not what you expected, `elab` is where you find out
whether the expression resolved to something different from what you meant, or resolved correctly and
the analysis is doing something else.

## `--json` — one machine-readable document {#json}

Every verb takes `--json`, spelled that way everywhere. It changes exactly one thing: **stdout carries
a single JSON document and nothing else.**

stderr is untouched — progress, `[circuitRF]` notes, warnings and refusal sentences stream exactly as
they always did — so a script watching stderr cannot tell whether the flag was passed.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf sparam amp.cnl --json | jq '.outputs[].path'
<span class="output">"amp.s2p"</span></code></pre>

One schema serves every verb:

<pre><code>{ "circuitrf": { "version": …, "verb": … },
  "input":     { "path": …, "analysis": … },
  "status":    "ok" | "not-converged" | "failed",
  "exitCode":  0 | 1 | 2 | 130,
  "outputs":     [ { "kind": …, "path": … }, … ],
  "diagnostics": [ { "id": …, "severity": …, "message": …, "arguments": { … } }, … ],
  "result":      { … } }</code></pre>

- **`input.analysis` is the chain that actually ran**, after promotion — not what you asked for. The
  difference is a whole sweep axis, so you must be able to see it from the document alone.
- **A failed run still emits a document.** The failure *is* the payload, so you never have to tell
  "no output" apart from "output I could not parse".
- **The diagnostic `id` is the contract; the `message` is not.** Match on `id`. Templates are
  reworded freely, and the sentence is always English and culture-invariant.
- **`result` holds cubes** (`groups`) for a run, a **summary** for `lp`/`lpp` — the same one-row-per-Γ-
  point projection the table prints, and `--all` adds the cubes — a **check** or **explain** report for
  those two verbs, a **document** for `read`, a **reference** report for `reference`, and a **render**
  report for `render`.
- **`result.render`** is everything the picture cannot say about itself: `viewport` (with
  `letterboxed`), the document's `extents`, the `size`, the `theme` and **which step of the chain
  resolved it**, the `layers` with whether each was drawn and how many shapes the document has on it,
  `detail` with its effective tolerance, `counters`, and `bytes`. Extents and viewport come back in
  base SI **with the unit and the scale named**. A `.cdd` adds `dataDisplay`, naming every source and
  the file it resolved to. **There is no duration** — `counters` is a work count, deterministic and
  machine-independent, so it is something a build can assert on; a wall clock is not.
- **`result.explain`** gains `cells`, `layers` and `extents` for the three questions of the same name.
  `extents` carries `perLayer` boxes beside the whole; `layers` carries the resolved technology and
  the walk that found it.
- **`result.shape` is always there**, on every run and every `read` that produced cubes, whether or
  not the values are: the groups, the cube names, each cube's kind and unit, how many numbers it
  holds, and every axis with its name, unit, length and end points. It is what lets you find out what
  a run produced before deciding what to ask for, and it costs a couple of kilobytes on any result.
- **`result.narrowed`** appears when you used `--at` or `--range`, and says per axis what you asked
  for, what you got, and whether it was the `nearest` grid point or `interpolated`.
- Numbers are raw, invariant and unrounded. `NaN` and infinity are written as JSON's named literals,
  because a loadpull grid genuinely contains NaN wherever a point never converged.

### Every cube says what its numbers are in {#json-units}

A cube's `unit` is always present and never empty. SI symbols as they are written — `Hz`, `V`, `A`,
`W`, `Ohm`, `F`, `H`, `K`, `m`, `s` — and `dB` and `dBm` likewise; `%` for a ratio already scaled to a
percentage, `1` for one that is not, `index` for a flag or a count, and **`unknown`** where circuitRF
cannot say. A `measure` line you wrote yourself has whatever unit your expression has, and saying
`unknown` is an answer where a guess would not be.

<div class="callout">
<span class="label">Why this is worth a field of its own</span>
<p>One loadpull-pursuit result carried <code>Efficiency</code> reading 65.84 and <code>MXE_Eff</code>
reading 0.7087 at the same operating point — the cube in percent, the scalar as a fraction. Reading
the scalar and formatting it as a percentage gives 0.7%; multiplying the cube by 100 gives 6584%.
Both are one plausible line of code, and until the unit was carried there was nothing anywhere to say
which was which. Nothing was rescaled to fix it: the numbers are what the engine computed, and the
document now says what they are.</p>
</div>

### `--summary`: the notes as counts {#json-summary}

Some verbs are deliberately talkative. The Gerber import names every inference it made *as* an
inference, which is exactly what lets you decide whether to trust the result — but it is not what you
want back from `convert --list-cells`, whose answer is one cell name.

`--summary` reports the informational notes as counts by severity and adds a `diagnosticSummary`
block saying how many were left out. **Warnings and errors are never collapsed**, and stderr still
carries everything, so nothing is hidden: what you stop paying for is thirty notes describing
inferences that all went fine.

---

## `serve` — the MCP server {#serve}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf serve --root &lt;dir&gt; [--kits &lt;dir&gt;]</code></pre>

**This is circuitRF as an MCP server.** It speaks the **Model Context Protocol** over its standard
input and output — the stdio transport, newline-delimited JSON-RPC 2.0, with `initialize`,
`tools/list` and `tools/call` — so any MCP client can discover what circuitRF can do and ask it to
do it. Protocol versions `2025-06-18`, `2025-03-26` and `2024-11-05` are accepted.

In practice that means an assistant, a design agent, a CI job or an automation harness can run a
simulation, check a design, create a workspace or import a part without a human at a terminal. It is
started by that program, not by you, and it ends when that program disconnects.

<div class="callout note">
<span class="label">Point an MCP client at it</span>
<p>A client is configured with a command and its arguments. The command is the circuitRF executable,
the arguments are <code>serve --root &lt;dir&gt;</code>, and <code>--root</code> is the only directory
tree the server will read or write, and a path escaping it is refused rather than clamped (see
<em>What it will not do</em> below). Nothing else about the host matters, because the transport is
the process's own stdin and stdout.</p>
</div>

**Thirteen tools, and each is a verb you already have:**

| Tool | Runs |
|---|---|
| `run` | `sparam`, `dc`, `hb`, `lp`, `lpp` or `em`, chosen by an argument |
| `check` | `check` |
| `explain` | `explain`, including `--cells`, `--layers` and `--extents` |
| `create` | `new workspace` or `new cell` |
| `import` | `import part` or `convert` |
| `render` | `render` — one tool over every document kind, as the verb is |
| `read` | `read` |
| `netlist` | `netlist` — the extraction a schematic runs as |
| `plot` | `plot` — one picture from one result file. It takes `attachImage` too |
| `find` | `find` — the workspaces, cells, views and analyses under a directory |
| `history` | [`history checkpoint`, `list` or `restore`](history.html) — the correction nouns (`rename`, `retitle`, `correct`, `review`) are on the verb but not on this server |
| `reference` | `reference` |
| `batch` | The **only** tool with no verb behind it: it holds a restore-point batch open across several calls, which a process that exits after one command cannot |

**Every tool returns exactly the document `--json` writes**, byte for byte, because the server calls
the verb rather than re-implementing it. Nothing is reachable through the server that is not
reachable from your own shell, and nothing is reachable from your shell that the server cannot do.

**`render` and `plot` can hand the picture back, not just its path.** Pass `attachImage: true` and the result
carries the rendered file itself — a `.png` or `.svg` as image content, a `.pdf` as an embedded
resource with its own type. It is **off by default**, because an image is expensive in a way a JSON
document is not, and a client that only wanted the path should not pay for one.

<div class="callout note">
<span class="label">Over 4 MB it hands back the path and says what to narrow</span>
<p>The file is still written and still named in <code>outputs</code>; only the attachment is withheld,
and the answer says how large it came to and which argument would bring it under —
<code>--detail screen</code>, <code>--layers</code>, <code>--window</code>, a smaller
<code>--size</code>, or <code>.png</code> instead of a vector format. It is never truncated and never
dropped in silence: half a PNG is not a smaller PNG, and a request that comes back empty with no
explanation just gets sent again.</p>
<p>The number is the <a href="#render-detail">measured one</a>. A whole six-layer board is 1.4 MB as a PNG and
23.5 MB as an undecimated SVG, so the cap admits every raster this verb plausibly produces and refuses
exactly the case where you should have narrowed the render.</p>
</div>

**The attached bytes are the bytes on disk.** The server attaches the file the verb wrote; it never
draws a second time, and it never converts one format into another to make it attachable — that would
be the adapter making a rendering decision, which is the one thing it does not do.

<div class="callout note">
<span class="label">What it will not do</span>
<ul>
<li><b><code>--root</code> is required</b>, and every path a client names resolves under it. A path
that escapes — through <code>../</code>, through an absolute path, or through a symbolic link — is
<b>refused, naming the root</b>. It is never quietly clamped to something inside.</li>
<li><b>Nothing deletes, and nothing overwrites an existing workspace.</b> There is no person at the
other end to confirm with, so the answer is no. A client that wants a file gone deletes it itself.
<code>create</code> does make a missing <i>parent</i> directory, because that overwrites nothing and
a client with no file tools of its own had nowhere to go from the refusal.</li>
<li><b>No tool writes a file of the client's own text.</b> circuitRF is driven by writing its
documents, and the client supplies that half itself — what these tools write is what they
<i>produce</i>: a created document, a result, a netlist, a picture. A general write tool would put an
unbounded filesystem write behind the root, and it is deliberately not offered.</li>
<li><b>No shell, and no program a client names.</b> Device workers and PCell generators still run as
they always did; nothing new becomes launchable because something asked.</li>
</ul>
</div>

**The reference surface is also published as protocol *resources*** — one per topic, at
`circuitrf://reference/<topic>`, each advertising its size so a client can decide what to spend. That
is the cheaper channel: a resource costs a URI and a title until something reads it, where a tool
description is carried for the whole session whether or not it is used. The `reference` tool exists
alongside it because not every client shows resources to the model at all, and both return the same
bytes.

**A long run reports progress and can be cancelled** — a client that asks for progress is sent it as
the run moves, and a cancellation stops the run at a work boundary and returns exit code 130 having
written nothing.

`serve` is the one verb whose stdout is not the result: it carries the protocol, so everything else —
progress, notes, warnings, device-worker logs — goes to stderr, where the program that started it
picks it up. For that reason it takes no `--json` of its own; every call through it already returns
one.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf serve --root ~/designs 2&gt; serve.log</code></pre>

---

## Exit codes {#exit}

| Code | Meaning |
|---|---|
| **0** | Ran, and produced something usable |
| **1** | Could not run — bad arguments, a missing file, no matching analysis, a refusal, an exception |
| **2** | Ran, but did not converge |
| **130** | Stopped — a run cancelled at a work boundary, by `em`'s own stop or by a `serve` client's cancellation |

**`2` is deliberately not the same test for every verb.** `hb` and `dc` fail on any non-converged
solve. A loadpull grid in which some points do not converge is a normal and useful result — the edge
of a Γ grid routinely will not — so `lp` returns `2` only when **every** grid point failed, and `lpp`
only when neither optimum converged and there is no follow-on grid. A rule that failed the whole run
on one bad point would make the exit code useless in a script.

## Scripting patterns {#scripting}

**Keep the table, keep the log, and still see it run.**

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf lp hero3.cnl -o hero3.npy &gt; hero3-table.txt 2&gt; hero3-run.log</code></pre>

**Sweep a variable the netlist already has**, without editing the netlist:

<pre><code class="cmd"><span class="prompt">$ </span>for p in -10 -5 0 5; do circuitrf hb pa.cnl --set Pavl_dbm=$p -o pa_$p.npy; done</code></pre>

**Fail a build on a regression**, using the exit code:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf hb pa.cnl -o out.npy || echo "PA did not converge" &gt;&amp;2</code></pre>

**Validate a whole workspace in CI**, before anything is run:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf check ~/designs/Amp --severity error || exit 1</code></pre>

**Author, validate and simulate with no display at any step** — the whole loop:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf new workspace build/Amp
<span class="prompt">$ </span>circuitrf new cell build/Amp Stage1
<span class="prompt">$ </span>cat &gt; build/Amp/Stage1/Stage1.cnl &lt;&lt;'EOF'
<span class="prompt">  </span>… your netlist …
<span class="prompt">$ </span>EOF
<span class="prompt">$ </span>circuitrf check build/Amp || exit 1
<span class="prompt">$ </span>circuitrf hb build/Amp/Stage1/Stage1.cnl -o out.npy</code></pre>

**Pull one number out of a result**, without parsing a table:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf read out.npy --only Pout_dBm --json | jq '.result.groups[""].Pout_dBm.values[-1]'</code></pre>

**Ask why a file resolved the way it did**, when a run used a technology you did not expect:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Amp.cem</code></pre>

**Put a picture of every cell into a build's artefacts**, at the size a report reads at:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain ~/designs/Amp --cells --json \
    | jq -r '.result.explain.cells[].name' \
    | while read -r c; do
<span class="prompt">  </span>    circuitrf render ~/designs/Amp --cell "$c" --detail screen -o "artefacts/$c.png"
<span class="prompt">  </span>  done</code></pre>

**Watch a layout change across two revisions**, without opening either:

<pre><code class="cmd"><span class="prompt">$ </span>git show HEAD~1:Stage1/layout/Stage1.clay &gt; Stage1/layout/before.clay
<span class="prompt">$ </span>circuitrf render Stage1/layout/before.clay -o before.png --window 0um,0um,6000um,3000um
<span class="prompt">$ </span>circuitrf render Stage1/layout/Stage1.clay -o after.png  --window 0um,0um,6000um,3000um</code></pre>

Two things make those two pictures comparable, and both are easy to lose. The explicit `--window`:
`--fit` frames each file's own extents, so a change in size would move everything in the frame and the
diff would be of the framing rather than of the artwork. And the older revision written **inside the
workspace**: a `.clay` somewhere else resolves no technology and draws on the fallback palette, so the
whole picture would change colour.

**Re-extract every EM setup in a workspace** after a technology edit — the layout and stackup
references resolve themselves, so the loop needs nothing but the file names:

<pre><code class="cmd"><span class="prompt">$ </span>for f in em/*.cem; do circuitrf em "$f" || exit 1; done</code></pre>

Because each of those writes the same file **Simulate** writes, a schematic that references the
extracted Touchstones picks the new results up with no further action.

---

<p class="small">See also: <a href="simulations.html">Simulations</a> (what each analysis computes) ·
  <a href="netlist.html">The netlist format</a> · <a href="em-setup.html">EM Setup</a> ·
  <a href="layout-editor.html">The layout editor</a> · <a href="data-display.html">The Data Display</a> ·
  <a href="mom-engine.html">The MoM engine</a> ·
  <a href="npy-export.html">Results &amp; data export</a> ·
  <a href="pdk-integration.html">Kits and external device models</a>.</p>

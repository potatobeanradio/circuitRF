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
<li><a href="#import-schematic"><code>import schematic</code> — another editor's schematic, as a cell</a></li>
<li><a href="#render"><code>render</code> — a picture of a document</a>
  <ol>
  <li><a href="#render-viewport">The viewport, and the unit rule</a></li>
  <li><a href="#render-detail">Size, and what <code>--detail</code> costs</a></li>
  <li><a href="#render-layers">Layers and colour</a></li>
  <li><a href="#render-cdd">A data display</a></li>
  <li><a href="#render-field">A 3D view's field plot</a></li>
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
<li><a href="#agent-install">Installing for an agent</a></li>
<li><a href="#exit">Exit codes</a></li>
<li><a href="#scripting">Scripting patterns</a></li>
</ol>
</nav>

## Invoking it {#invoking}

The command-line driver is the same program as the GUI's Run button with the window taken off. It
reads the same files, elaborates them with the same elaborator, runs the same engines, and evaluates
the test bench's `measure` lines with the same evaluator.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf &lt;verb&gt; &lt;file&gt; [options]</code></pre>

**The installed circuitRF executable is the command line.** There is no second program to install:
when its first argument is a verb, circuitRF runs that verb and exits without opening a window; with
no arguments, or with a document to open, it starts the application as it always has. Where that
executable is depends on the platform — and the verb must be the FIRST argument, since that is how
circuitRF tells a command line from a document to open:

| Platform | Installed as | On your `PATH` |
|---|---|---|
| Windows, per-user installer (`…-win-x64-user.msi`) | `%LOCALAPPDATA%\Programs\circuitRF\circuitRF.exe` | yes — type `circuitrf` |
| Windows, per-machine installer (`…-x64.msi`) | `C:\Program Files\circuitRF\circuitRF.exe` | yes — type `circuitrf` |
| macOS | `/Applications/circuitRF.app/Contents/MacOS/circuitRF` | no — see below |
| Linux, `.deb` | `/opt/circuitrf/circuitRF`, linked as `/usr/bin/circuitrf` | yes |
| Linux, `.tar.gz` + `install.sh` | linked as `~/.local/bin/circuitrf` | yes, if `~/.local/bin` is |

<div class="callout note">
<span class="label">Windows: <code>circuitRF.com</code> is what you are typing</span>
<p>Beside <code>circuitRF.exe</code> the installer puts a small <code>circuitRF.com</code>, and both
installers add that folder to <code>PATH</code> (the user's for per-user, the system's for
per-machine; a terminal opened <em>before</em> the install does not see it yet). Windows tries
<code>.com</code> before <code>.exe</code>, so a <code>circuitrf</code> typed in cmd or PowerShell
reaches the console-subsystem <code>.com</code>, which gives the command a console and makes the shell
wait for it. Shortcuts and double-clicked documents still open <code>circuitRF.exe</code>. A program
that starts circuitRF with its own pipes — an MCP client, a script, CI — can use either.</p>
</div>

On macOS, run the full path, or link it onto your `PATH` once:

<pre><code class="cmd"><span class="prompt">$ </span>sudo ln -s /Applications/circuitRF.app/Contents/MacOS/circuitRF /usr/local/bin/circuitrf</code></pre>

`circuitrf --version` prints the version of the build that answered, and nothing else — the quickest
way to confirm which one is on your `PATH`.

**From a source checkout** there is no installed executable, so put `dotnet run --project src/Cli --`
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
| `dc` | `.cnl` or `.csch` | The nonlinear DC engine, or the sweep that wraps it | Node voltages, probe currents and measurements, to stdout; the cubes with `-o` |
| `hb` | `.cnl` or `.csch` | Harmonic balance, single- or multi-tone | Spectra tables to stdout; `-o .mat/.npy/.txt` |
| `lp` | `.cnl` or `.csch` | Loadpull over the directive's Γ grid | A per-Γ-point table; `-o .mat/.npy/.txt/.spl/.lpcwave` |
| `lpp` | `.cnl` or `.csch` | Loadpull **pursuit** — searches for the optima | Optima + the follow-on grid; `-o` as `hb`; `--out-grid` writes a `.gam` |
| `em` | `.cem`, or a `.c3d` with `--setup` | The EM kernel or 3D solver the setup resolves to | A Touchstone `.sNp` **and** a grouped `.npy`, where **Simulate** writes them |
| `solver list`, `install`, `remove` | **nothing** — a tool name | The install assistant **Settings ▸ Solvers** runs | What each 3D solver's row shows, to stdout; an install or a removal, only with `--yes` |
| `opt` | `.csch` or `.cnl` | The schematic's saved optimization — the [Optimizer](optimization.html)'s own run | The best values and each goal's result to stdout; **nothing** in the design (`--save-preset` adds a preset) |
| `yield mc`, `yield estimate`, `yield trial` | `.csch` or `.cnl` | A Monte Carlo or yield run over the tolerances on the tune lines, the kit's statistics and the `statistics` line | The yield, each goal's result and the spread to stdout; the trials to `<design>.yield.npy`; **nothing** in the design (`--save-preset`, `--save-corner` with `--trial`) |
| `yield corners` | `.csch` or `.cnl` | The design at every enabled corner, or a Monte Carlo at each (`--mc`) | A corner × goal margin table to stdout; the corners to `<design>.corners.npy`; **nothing** in the design (`--generate … --write` on a `.csch`) |
| `yield center` | `.csch` or `.cnl` | Design centering — the [Yield](yield.html#centering) panel's Centering run | The verified start and centred yields and the centred values to stdout; the verification to `<design>.yield.npy`; **nothing** in the design (`--save-preset` adds a preset) |
| `yield doe` | `.csch` or `.cnl` | A design of experiments over the optimizer's or the tolerances' values | Each response's effects to stdout; the runs to `<design>.doe.npy`; **nothing** in the design |
| `model new`, `import`, `deembed`, `fit`, `check-charge`, `explain`, `export`, `validate`, `report` | a `.cmc` | The [Model Composer](#model)'s steps — the same functions its window calls | The `.cmc`, its cache, a `.cmodel` or a cell, a report folder — as the noun says |
| `rail` | `.crail` | The same DC solve and via check [railRF](railrf.html)'s **Run** button calls | The ports, the ranked breakdown and the via check to stdout; `-o .csv/.npy/.mat/.txt/.svg/.pdf` |
| `smith` | `.csmith` | The same cascade evaluator the Smith Chart window walks on every edit | The reading and the per-node table to stdout; `-o .s1p` for the load Γ, `-o .svg/.pdf/.png` for the chart |
| `snp` | a Touchstone `.sNp` | The same reading and plots the [S-Parameter Viewer](sparameter-viewer.html) shows | What the file states, to stdout; `-o .svg/.pdf/.png` for the four plots, `-o .cdd` for a Data Display of them |
| `lvs` | a cell folder, a workspace, a `.clay` or a `.csch` | The same comparison the [LVS panel](lvs.html)'s **Compare** button calls | **Nothing** — the report to stdout; `-o report.txt` |
| `recognize` | a `.clay`, a cell folder, or a workspace with `--cell` | Reads the board's copper as a circuit — ground, vias, ports, parts and lines as native components | **Nothing** — the report and the parts table to stdout; `-o circuit.cnl`, `--into new:<name>` for a schematic, `--parts-out parts.csv` |
| `impedance` | a `.clay` or a cell folder | The same analysis the layout editor's [Impedance Analysis](layout-editor.html#impedance-analysis) runs | The per-trace report to stdout; `-o report.pdf` |
| `review` | a `.clay`, a cell folder, or a workspace with `--cell` | The [Layout Review](layout-editor.html#layout-review)'s built-in checks | **Nothing** — the findings, grouped per check and layer, to stdout; `-o` writes the review as a PDF |
| `convert` | any layout format | The same importer and exporter **File ▸ Import/Export** runs | The layout in the format you asked for |
| `new workspace` | a directory | The same code **File ▸ New Workspace** runs | A `.cws` and, unless you say otherwise, a copied technology |
| `new cell` | a workspace + a name | The same code **New Cell** runs | A cell folder and one empty-but-valid file per view |
| `import part` | a component file or folder | The same code **Import Component** runs | A cell folder holding the land patterns and the symbol |
| `import schematic` | a `.kicad_sch`, EDIF 2.0.0 or binary `.DSN` design | The same code **File ▸ Import ▸ Schematic…** runs | A cell folder with its schematic, and a folder of symbol cells beside it |
| `render` | a `.csch`, `.csym`, `.clay` or `.cdd`, a cell folder, or a workspace | The same Skia renderers the editors draw every frame with | A `.svg`, `.pdf` or `.png`, where `-o` says |
| `check` | a workspace, a cell folder, or one document | Every validator the application already uses | **Nothing** — findings to stdout |
| `explain` | the same | Resolution only — no analysis | **Nothing** — the walk and the answer, to stdout |
| `read` | a result file, or one of circuitRF's own documents | The same loaders the Data Display reads a file with | **Nothing** — what the file holds, to stdout |
| `netlist` | a `.csch`, a cell folder, or a workspace — or a `.cnl` with `--to-schematic` | The same extraction **Simulate** performs, or that extraction run backwards | A `.cnl`, or the netlist text to stdout; a drawn `.csch` with `--to-schematic` |
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
| `--trust-kit <dir>` | Lets this run execute the PCell scripts of the kit whose generator manifest is in `<dir>`, so a layout's generated cells can be rebuilt when its `.generated-cells` folder is missing. A kit you have already allowed in circuitRF on this computer needs no flag. Repeatable. |
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

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf sparam &lt;file.cnl|.csch&gt; [--freq start:stop:step] [--set var=expr] [-o out.sNp]</code></pre>

```text
$ circuitrf sparam hero1.cnl --freq 1GHz:3GHz:1GHz -o hero1.s2p
S-parameter analysis: 3 points, 1–3 GHz
Wrote hero1.s2p
```

| Option | What it does |
|---|---|
| `--freq start:stop:step` | Override the sweep. **Omit it and the netlist's own `sparam` analysis is used**, segments and all — which is almost always what you want, because it is the sweep the design was set up with. |
| `--set <var=expr>` | Override a global variable **before elaboration**, [as `hb` does](#set). Repeatable. Everything written in terms of it re-derives, including the netlist's own sweep. |
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

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf dc &lt;file.cnl|.csch&gt; [--set var=expr] [-o out.{npy,mat,txt}]</code></pre>

```text
$ circuitrf dc hero2.cnl
DC: converged in 3 iteration(s), residual 7.27E-16
Node voltages:
  0                                         0
  n_src                                     0
  n_gate                                -3.05
  n_drain                                  48
```

`--set var=expr` overrides a global before elaboration, [as `hb` does](#set); it is repeatable. It
prints the converged node voltages, any probe currents and the netlist's `measure` lines — a
measurement names the result by the DC analysis that declares it (`DC1.V("n_drain")`), as it does in
the window — and
[exits 2](#exit) if the solve did not converge — the operating point is the one thing every nonlinear
analysis is built on, so a non-converged DC is a failed run, not a partial one. `-o` writes the result
cubes and the measurements as `.npy`, `.mat` or `.txt`.

**A `parametric_sweep` that wraps the DC analysis runs as the sweep**, as it does for `sparam`, `hb`, `lp`
and `lpp`: an I–V family is a DC inside two sweeps, and running the DC alone would return one point. The
sweep's name goes to stderr with the count of points that did not converge; stdout carries the
measurements over the whole family, and `-o` writes the cubes with one axis per sweep. A swept run
exits 2 if any point did not converge.

```text
$ circuitrf dc family.cnl -o family.npy
DC sweep 'SWD': 21 point(s) over Vds
DC: converged at all 504 point(s)
Measurements:
  Id  [Vds:21 x Vgs:24]
  ...
Wrote family.npy
```

with the sweeps in the netlist, the inner one stepping `Vgs` at each `Vds`:

```text
analysis DC1 type=dc
analysis SWG type=parametric_sweep Var=Vgs Start=-5 Stop=0.75 Step=0.25 Inner=DC1
analysis SWD type=parametric_sweep Var=Vds Start=0 Stop=20 Step=1 Inner=SWG
measure Id = DC1.I("ID")
```

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

`--device auto|cpu|gpu` chooses where a 3D run solves. A Palace run solves on the CPU unless `--device gpu` says
otherwise; that needs a GPU build of Palace, and [Palace on a GPU](em-setup.html#palace-gpu) has the details. An
EMerge run is `auto` unless `--device` says otherwise, whatever **Settings ▸ Solvers** holds: the GPU sparse solver
for a driven problem large enough to be faster on it ([EMerge on a GPU](em-solvers.html#emerge-gpu)), the CPU
otherwise.
`--device gpu` is refused on a run that uses neither Palace nor EMerge.

A 3D run's solver is stopped if its memory reaches this computer's (`--memory-limit <GB>` sets a lower limit), or if
the startup volume or the results' volume falls below 10 GB free; either way the run writes nothing and exits **1**.
`--force` lifts the memory limit, never the disk floor. `explain` on a 3D setup reports both, and takes
`--memory-limit` too.

A 3D or thermal run makes its mesh in the **Scratch folder** that **Settings ▸ Solvers** names, the system temp folder
when none is named, and prints it as a `Scratch:` line beside the solver. `--json` carries it on every run, refused or
not, as `result.scratch`: `folder`, `source` (`preference` or `system`), `exists`, and `systemTemp`, which a solver's
own helpers still use (the parallel runtime's session files for a Palace run on several processes). A named folder
that is not there, such as a drive that is not connected, refuses the run before meshing and exits **1**.

A `.c3d` runs too — `circuitrf em <view.c3d> --setup <name>` — and a **thermal** setup embedded in it runs through the
same verb with circuitRF's own thermal solver (Gmsh meshes it). It writes `<cell> <setup>.thermal.npy` and the temperature
field beside it, and prints the probes, the measures and, when the setup asks for one, the Rth matrix. See
[Thermal ▸ Headless](thermal.html#headless).

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

### Comparing solvers: `--solver` and `--reference` {#em-compare}

A 3D setup's solver can be changed for one run without editing the file: `--solver palace`, `openems` or
`emerge` runs that solver alone, and a comma list compares them.

```bash
circuitrf em via.cem --solver palace,emerge                      # EMerge against Palace
circuitrf em via.cem --solver openems,emerge --reference emerge  # openEMS against EMerge
circuitrf em via.cem --solver compare                            # the setup's own Compare set
circuitrf em via.cem --solver both                               # Palace and openEMS
```

The solvers run one after the other, Palace first, then openEMS, then EMerge. The reference is Palace when
it is listed, otherwise the first. Each solver's result lands where it would alone, and the comparison in
`results/<name>.compare_em.npy` ([EM Setup ▸ Compare](em-setup.html#compare) describes what it holds). A
solver that is not installed, or cannot solve the setup, is refused before anything runs, naming it and
why.

### One component's drawn part: `--component` {#em-component}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf em mydie --component "SPIRAL N=2.5 W=10 um S=10 um Din=100 um" --freq 1GHz:20GHz:1GHz -o L1.s2p</code></pre>

Extracts one built-in component as it would be drawn: its layout cell on the workspace's technology,
with a port on every terminal — port 1 is terminal 1, and so on — so the Touchstone can replace the
part in the schematic as an [SnP](components.html#snp). The line is the type and its parameters,
written as on a `.cnl` instance line. The path is the workspace (or its `.cws`, or a `.ctech`); with
none, the current folder's workspace is used. `-o` is required; with no `--freq` the sweep is 1–20 GHz.

This is how to get a value to rely on for a [spiral inductor](components.html#spiral), whose built-in
inductance is an estimate. The extraction meshes two cells across the narrowest metal, with no edge
mesh, so that a coil fits the planar solver — the run says so, and the Q it reads is low. A
[thin-film resistor](components.html#tfr) is refused: its film is a sheet resistance, not a layer the
solver meshes, and its own model is exact for what it states.

### note, warning, error — three lists, kept apart {#em-messages}

An EM run has three different things to say and they ask three different things of you, so they are
printed under three labels rather than flattened into one stream:

| Prefix | Means |
|---|---|
| `note:` | The run explaining itself — which kernel it chose and why, the mesh's own sentences, RLGC, the ports it found. Read these; they are the cheapest check that the tool is looking at the structure you think it is. |
| `warning:` | Something to act on — a stale `.sNp` about to be replaced, a technology that resolved but failed validation. |
| `error:` | Something you asked for and did not get — a results file that could not be written. |

### A 3D view's terminal ports on Palace {#em-palace-terminals}

A `.c3d` whose wave port has two terminals runs on Palace too when its lines' modes travel at different speeds
([EM Setup ▸ On Palace](em-setup.html#wave-port-terminals-palace)), and the run's notes say how its terminal S was made: one line per port
naming its terminals as the modes of one Palace wave port and how they were converted, then one giving each terminal's
power and the singular values of S.

```text
note: Port 'Left': terminals P1, P2 as modes 1 and 2 of one Palace wave port, converted to terminal S (modes not
      degenerate, Robin correction K = 1.079 to 1.086).
note: Terminal S checks: per-port power |S_jj|² + Σ|S_ij|² … to …, singular values … to …, reciprocity max |S_ij − S_ji| …
```

An adaptive sweep adds a note naming the frequencies each terminal face's modes were solved at. A fit with no consistent
root, a lossless problem whose S is not lossless, a terminal voltage that does not give Palace's mode impedance, and a
face supporting a mode past its terminals' own are each a `warning:`, with what to do about it. Palace's modal S, the
voltages, the mode impedances and wavenumbers, and the fitted G and K are kept in the run's `.npy` (the `palace` group's
`modal_*` cubes), so a disputed result can be re-derived without running again. Three terminals on one face, a
reference plane moved off the face, or a lumped port beside them are refused before anything runs, naming openEMS; a face
whose modes travel at one speed (a stripline) is refused once the mesh exists, by its own mode solve, before the 3D solve.

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

## `solver` — installing, listing and removing the 3D solvers {#solver}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf solver list [--capability gpu]
<span class="prompt">$ </span>circuitrf solver install &lt;palace|gmsh|openems|emerge&gt; [--version &lt;v&gt;] [--gpu] [--yes]
<span class="prompt">$ </span>circuitrf solver remove &lt;palace|gmsh|openems|emerge&gt; [--version &lt;v&gt;] [--yes]
<span class="prompt">$ </span>circuitrf solver remove --all [--yes]</code></pre>

The install assistant of **Settings ▸ Solvers**, with no window: the same recipes, the same install record, the
same removal. A build machine installs a solver this way.

- **`list`** prints what each Settings row shows: found or not, the version and whether circuitRF has validated or
  tested it, where it is and how it was found, and the command that would install it here. EMerge's line adds the
  sparse solver it would use (*fast sparse solver*, or the single-threaded baseline with what to install) and
  whether its GPU sparse solver is installed. `--capability gpu` also asks Palace whether it solves on a GPU, which
  costs a one-element solve. Read-only: it installs and builds nothing.
- **`install <tool>`** prints what it would fetch, from where, where it would go and what it cost when measured,
  and exits **1** having fetched nothing. **`--yes`** agrees and installs. `circuitrf solver install emerge --yes`
  installs EMerge into a Python of its own and, on an arm64 Mac with the command line tools, builds its faster
  sparse solver in the same run; **`--gpu`** also fetches a temporary Rust toolchain into EMerge's folder to build
  its GPU sparse solver, and deletes the toolchain after. See
  [Getting the most speed from EMerge](em-solvers.html#emerge-speed).
- **`remove <tool>`** removes a copy circuitRF installed, after printing the space it frees; `--all` removes every
  one. Again nothing happens without `--yes`. A program circuitRF did not install is never removed.

Exit **0** when installed (or already installed) or removed, **1** when refused or failed, with the report on
stderr, **130** when cancelled.

---

## `opt` — optimizing from the command line {#opt}

```text
circuitrf opt <schematic.csch | netlist.cnl> [flags]
```

`opt` runs the optimization a schematic saves — its tuned values' ranges, its goals and its algorithm,
as the [Optimizer](optimization.html) panel sets them up — and reports what it found. It is the
panel's own run, not a second one: the same schematic and the same seed give the same best values,
cost and simulation count headless as they do in the window. On the *Tuning and Optimization* example:

```text
$ circuitrf opt LSectionMatch/schematic/LSectionMatch.csch
Optimization: lm   (LSectionMatch/schematic/LSectionMatch.csch)
Finished:  every enabled goal is met
Best cost: 0   (12 iterations, 39 evaluations)

Variables:
  key   start   best                 min     max    railed
  L1.L  5 nH    12.7087915391915 nH  1 nH    50 nH
  C1.C  0.5 pF  1.48508255273602 pF  0.1 pF  10 pF

Goals:
  name   met  value  at               margin
  Match  yes  -20    freq = 1.05E+09  0
```

Each best value is the text the schematic would hold, at full precision; a part of a complex value is
followed by the whole value, in the form the schematic writes it. A met goal reports its tightest
point and the margin there; an unmet one its worst point, with a negative margin.

A [vendor part](pdk-integration.html#vendor-optimize) (`tune C1.Part … opt=1`) is reported by part:
its row's best is the chosen part number and its min and max are the candidates' value span, and a
**Parts** table lists each entry's start, the chosen part, its value and description. With other
variables, the snap line gives the cost with ideal components in the parts' place and the cost with the
real parts, the difference the parts' parasitics make. The `--json` document carries the same as `parts`
(the chosen `part` and the `vendorlib://` reference of its `file`, which is what the instance's Name and
File are written as) and `snap.idealParts`.

| Flag | |
|---|---|
| `--algorithm`, `--max-iter`, `--max-evals`, `--time`, `--cost lsq\|minimax`, `--analyses goals\|all`, `--parallel`, `--seed` | Override the saved settings, for this run only. |
| `--vars key,key`, `--goals name,name` | Optimize only these of the saved variables, against only these goals. A complex value is named by its parts (`mag(Zs)`), never whole. |
| `--corners all\|none\|name,name` | Meet the goals at these [corners](yield.html#corners) as well as the nominal, overriding the saved `corners=`. Each point then costs one simulation per corner, and each goal's result names the corner that binds it. |
| `--snap` | [Snap and polish](optimization.html#snap) at the end. |
| `--sensitivity` | A sensitivity pass at the best point. |
| `--show-iterations` | Every iteration as well, one line each on stderr (and in `--json`). The default is the final result only. |
| `--save-preset <name>` | Add the best values to the schematic as a preset. The one thing `opt` writes; refused for a `.cnl`, where the preset is a line you add. |
| `-o out.npy` | The best point's results, and the run's history in an `opt` group. |
| `--set var=expr` | As for every run verb. |

**It never changes the design's values.** To put the best point in the schematic, push it in the
panel, recall a saved preset, or write the values into the file. An exit code of **3** means the run
finished with at least one goal unmet — not a failure of the run, but not a design that meets its
specification either. Over [MCP](ai-agents.html) the same run is `run analysis=optimize`, and
`reference goals`, `reference tuning` and `reference optimizers` say what may be written;
`reference statistics` covers tolerances, correlations, the statistics settings and corners.

## `yield` — Monte Carlo and yield from the command line {#yield}

```text
circuitrf yield mc|estimate|trial|corners|center|doe <schematic.csch | netlist.cnl> [flags]
```

`yield` draws the design's tolerances — the `dist=` keys on its tune lines, and its kit's process and
mismatch statistics — and simulates each draw. **`mc`** reports the spread alone; **`estimate`**
counts how many trials pass the goals marked `use=yield` or `use=both` and reports that yield with its
confidence interval; **`trial`** re-runs one trial and says what it drew. Every trial is a function of
the seed and its own number, so the same file and seed give the same trials on any machine, at any
parallelism.

```text
$ circuitrf yield estimate div.cnl --target 80%
Yield estimate: div.cnl
  seed 1 · sampling random · 200 of 200 trials · 200 trials
Yield: 88.0 % (95 % interval 82.7 % – 92.2 %; 176 of 200 counted trials pass) · target 80.0 %: met

Goals:
  name  yield   interval         worst margin  trial
  Vout  88.0 %  82.7 % – 92.2 %  -0.01022      58

Statistics over the evaluated trials:
  of                n    mean      sigma     min       max       median    Cpk
  goal:Vout:margin  200  0.004593  0.004064  -0.01022  0.009956  0.005223  0.3767
  Vo                200  0.5003    0.006768  0.4817    0.5202    0.5005

Worst trials for Vout:
  trial 58     margin -0.01022   R1.R=979.902516568072 Ohm  R2.R=1062.49587500816 Ohm
  ...
```

A progress line per batch goes to stderr (`-q` silences it). The trials are written to
`<design>.yield.npy` beside the design: every analysis result with an outer `trial` axis, each trial's
drawn values, margins and pass/fail, the nominal, and a `yield` summary. `read` prints that summary
first, and `plot` draws the statistics functions directly — `--trace "cube=histogram(trials.goal:Vout:worst, 20)"`.

| Flag | |
|---|---|
| `--trials`, `--seed`, `--sampling random\|lhs\|sobol`, `--confidence p%`, `--nonconverged fail\|warn`, `--save scalars\|all\|n\|auto`, `--parallel`, `--analyses goals\|all` | Override the `statistics` line, for this run only. |
| `--target p%`, `--autostop` | `estimate` only: the yield to meet, and stopping as soon as the interval is clear of it either way. |
| `--process 0\|1`, `--mismatch 0\|1`, `--sigma-scale k` | The kit's process and mismatch draws, and a scale on every kit sigma. |
| `--vars key,key`, `--goals name,name` | Draw only these tolerances (the rest stay at nominal); score only these goals. |
| `--trial n` | Re-run only trial `n`. `-o out.npy` then writes its results. `yield trial` scores it as the design's own run would — a yield when there is a yield goal, a Monte Carlo otherwise. |
| `--save-preset <name> --trial n` | Add that trial's values to the schematic as a preset. Refused for a `.cnl`. |
| `--save-corner <name> --trial n` | Add a corner naming that trial, so later runs can replay it. Refused for a `.cnl`, and with `--vars`, `--sigma-scale`, `--process`, `--mismatch` or `--set`: a corner replays the trial under the design's own settings, so it would not be the trial you saw. |
| `--contributions` | Which tolerances drive each goal's and measurement's spread, largest first. The table shows each share between 0 and 100 %; `--json` gives the fitted share as it stands, which can pass 100 % or fall below 0 when tolerances are correlated. |
| `-o out.npy` | Where the trials are written. |
| `--set var=expr` | As for every run verb. |

A flag that a run would not use is refused, naming the runs that take it — `--trials` on `yield corners` without
`--mc`, say, or `--confidence` on `yield doe`. `reference statistics` lists every flag with the runs that take it.

**It never changes the design's values.** Exit **0** means the run finished and the yield met
`--target` (or there was none); **3**, that it finished below the target; **1**, that it was refused;
**2**, that no trial evaluated; **130**, that it was cancelled, with nothing written. Over
[MCP](ai-agents.html) the same runs are `run analysis=montecarlo`, `run analysis=yield` and `run analysis=corners`, and
`reference statistics` says how to write tolerances, yield specs and the `statistics` line.
`explain <file> --analysis` reports how wide the interval of the configured trial count will be and
roughly how long the run takes, before you start it.

### Corners {#yield-corners}

`yield corners` checks the design at every enabled `corner` line — a kit corner selection, a temperature, values —
in one run, and prints one row per corner with each goal's margin:

```text
$ circuitrf yield corners div.cnl
Corners: div.cnl
  a goal FAILS at a corner

  corner   temp  Vout margin  status
  nominal        0.08         passes
  cold     -40   0.1572       passes
  hot      125   -0.002045 ✗  FAILS
  hiR2     85    0.05158      passes

Worst corner per goal:
  Vout: hot (margin -0.002045, FAILS)
```

It exits **3** when a goal fails at any corner, and writes `<design>.corners.npy`. A corner made from a Monte Carlo
trial (`--save-corner`) replays that trial's draws around the design's current values, so it follows the design as
you tune it.

| Flag | |
|---|---|
| `--corners name,name` | Only these corners. On `mc` and `estimate`, a run at each of them. |
| `--mc` | A Monte Carlo — a yield, when the design has a yield goal — at each corner, with the kit's process draws off there: the corner is the process. The table is then a yield per corner. |
| `--generate "spec"` | Print the corners a cross product makes and write nothing, e.g. `"proc=tt,ss;temp=-40,25,85;Vdd=3.0,3.6"` — a kit axis by its name, `temp`, any variable or tunable value. With `--json`, each corner's name, temperature and values. |
| `--write` | With `--generate`, on a schematic: add the generated corners to it. |

```text
$ circuitrf yield corners div.cnl --generate "temp=-40,25,85;R2.R=900 Ohm,1100 Ohm"
corner tm40_R2R900 temp=-40 R2.R=900 Ohm
corner tm40_R2R1100 temp=-40 R2.R=1100 Ohm
corner t25_R2R900 temp=25 R2.R=900 Ohm
...
```

### Centering {#yield-center}

`yield center` moves the values marked `opt=1`, within their ranges, to where the most trials meet the specs, and
then checks the result: the starting values and the centred ones are each run on the same set of fresh trials. On
the *Yield, Corners and Centering* example:

```text
$ circuitrf yield center DividerCentering/schematic/DividerCentering.csch
Design centering: DividerCentering/schematic/DividerCentering.csch
  cmaes · 100 common trials (seed 1) · 10 iterations · 4100 simulations · the iteration limit (10) was reached
Verified: yield 73 % [70.1 %, 75.7 %] → 99.1 % [98.3 %, 99.6 %] on the same 1000 fresh trials; the intervals do not overlap · target 95.0 %: met
...
```

The verified pair is the result; the search's own yields, on its common trials, are shown beside it and labelled as
such. The flags are the `center` line's, for this run only — `--algorithm`, `--trials` (the common trials each
position is scored on), `--verify`, `--max-iter`, `--max-evals`, `--time`, `--width`, `--parallel`, `--seed` — with
`--target`, `--confidence`, `--sampling`, the kit switches, `--vars` and `--goals`.

| Flag | |
|---|---|
| `--surrogate quadratic` | Score each position on a quadratic fit of the specs' margins instead of simulating every trial — 410 simulations instead of 4,100 on the divider above. The final check is still simulated. |
| `--save-preset <name>` | Add the centred values to the schematic as a preset. Refused for a `.cnl`. |
| `-o out.npy` | Where the verification of the centred values is written. |

Exit **0** means the verified yield met `--target`; **3**, that it fell short; **1**, refused; **2**, no position
evaluated; **130**, cancelled with nothing written. Over MCP it is `run analysis=center`.

### Design of experiments {#yield-doe}

`yield doe` runs the design at a planned set of combinations of its values — the `opt=1` values at the ends of
their ranges, or with `--factors stat` the toleranced values at their nominal ± k σ — and reports, for each spec and
measurement, each value's effect and each pair's interaction, largest first. An effect larger than the noise level
estimated from the small ones is starred; an effect a fractional design cannot tell apart from another lists it.

| Flag | |
|---|---|
| `--design full2\|frac\|pb\|ccf` | Full factorial, fractional factorial, Plackett–Burman screening, or a face-centred composite for a curved model. |
| `--resolution 4\|5` | For `frac`. |
| `--factors opt\|stat`, `--levels range\|sigma:k` | Which values are the factors, and where their low and high ends are. |
| `--centre n` | Centre points added (`0` adds none). |
| `--responses goals\|all` | Which goals are responses: the specs, or every enabled goal. Every scalar measurement is a response either way. |
| `--optimum` | Search the fitted model for the point that best meets the specs, and simulate that point to confirm it. |

It writes `<design>.doe.npy` and nothing in the design. There is no target, so the exit codes are **0**, **1**, **2**
and **130**. Over MCP it is `run analysis=doe`.

## `model` — the Model Composer, headless {#model}

```
circuitrf model <noun> <doc.cmc> [flags]
```

Builds a FET model (`.cmodel`) or a passive cell from measured or simulated data, step by step, through the same
functions the Model Composer window calls — so a document worked on here and opened in the window holds the same
bytes. Each noun prints one line per item on stdout (`--json` gives the same lines as `result.model.items`, each with
what it is about), progress on stderr. The formats are in [File Formats](file-formats.html#cmc), and as
`circuitrf reference cmc` and `reference cmodel`.

| Noun | What it does |
|---|---|
| `new <doc.cmc> --kind active\|passive` | Creates the document. Active: `--network` a stock network (`gan-two-shell`, `classic-shell`, `series-only`, copied beside the document) or a six-pin `.csch`; `--fingers N0 --unit-width W0` (`"125 um"`), the measured device's periphery. |
| `import <doc.cmc> <source…> --as <kind>` | Adds a data set — `DcIv`, `PulsedIv`, `SParams`, `PulsedSParams`, `SimulatedTables`, `LargeSignal` or `PassiveSParams` — and imports it. A folder is every file in it. `--pattern` reads a Touchstone set's bias from its file names (`--pattern-source comment` from its comments), and a passive document needs one only to read a family's `{geometry}`; `--map` says what a CSV column (`Vg=vgs:V`) or an array (`vg=vgs`) holds; `--condition Key=Value` sets a measurement condition (`ReferencePlane=device`, `AmbientC=25`, `Quiescent.Vds=28`); `--name`, `--format`, `--no-header`, `--delimiter`, `--z0`, `--freq`. |
| `deembed <doc.cmc>` | Removes the pads and the network: a line for each, then a line per data set of its intrinsic bias ranges. |
| `fit <doc.cmc> --step <step>` | `network`, `thermal`, `current`, `traps`, `charge`, `passive`, or `all` — every step the document needs, in order. The document's own settings, with `--method` (the step's method: `MultiBias`, `ANN`, `Table`, `Equation`, a trap model — with no pulsed I–V, written with the `Shift` coupling), `--seed n` and, for a passive document, `--topology` written into them first. To change anything else, edit the step's `Settings` in the `.cmc` and fit again. Only DC I–V is needed: with no S data, `all` holds the network at its values and the charge step writes the capacitances typed in its `Settings.Typed` (zero when none are). |
| `check-charge <doc.cmc>` | Checks the fitted charges conserve charge. Also `<design.cnl> --device X1 --bias vgs,vds`, for any device in a netlist (`--region` adds the capacitance check over a rectangle). Exit **3** when the spurious DC at the gate is above `--threshold` (default 0.1 % of the fundamental gate current). |
| `explain <doc.cmc>` | The data sets and their planes, the intrinsic ranges, N0 and W0, the temperature mode, each step as fresh, stale or absent, every network value with its error and whether it is poorly determined, and the levels each trap state was trained from, or the shift coupling's strengths. Fits nothing. |
| `export <doc.cmc> -o <out>` | Writes the model in the format `-o`'s extension names: `.cmodel`, with `--symbol` naming the symbol, or `.va` (also `.vams`), [Verilog-A source](model-composer.html#veriloga) for another simulator, with `--module` naming the module (default: the file name). A `.cmodel` exports to `.va` too, with no document. A table constituent is refused, naming each. `--created` sets a `.cmodel`'s timestamp; a `.va` records the model's own. A passive document exports as a cell: `--cell <workspace> <name>`. |
| `validate <doc.cmc>` | Compares the document's current model — or `--model x.cmodel` (or a `.cmodel` with `--cmc doc.cmc`) — with its data, at the measurement plane. `--against set,set`, `--tolerance S95=0.02` (also `SMedian`, `IvRelative`, `PowerDb`, `EfficiencyPoints`, `AmPmDegrees`, `ImpedanceVswr`), `--compression dB` for the compression a loadpull is compared at. Exit **3** when a tolerance is missed. |
| `report <doc.cmc>` | Writes the Model Validation Report folder (`-o`, default `<name>-report` beside the document): `report.npy`, `summary.txt` and every testbench it ran — and its Data Display document, `<folder>.cdd`, beside the folder. `--pdf out.pdf` also draws every tab of it, one page each: the same file the window's Export PDF writes. `--model`, `--sections`, `--classes B,J,F,FInverse`, `--freq`, `--bias vgs,vds`, `--source-2f0 re,im`, `--ropt`, `--disc radius,rings,spokes`, `--pin start,step,max` and `--compression` override the options the document stores. |

A choice the window would ask you for is refused, naming the flag that answers it: a Touchstone import with no
`--pattern`, a fit of an Active document with no N0 (`--fingers`), an export with no `-o`.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf model new fet.cmc --kind active --network gan-two-shell --fingers 4 --unit-width "125 um"
<span class="prompt">$ </span>circuitrf model import fet.cmc sparams --as SParams --pattern "Vgs{vgs}_Vds{vds}_Id{id:mA}" --condition ReferencePlane=device
<span class="prompt">$ </span>circuitrf model import fet.cmc dc.csv --as DcIv --map Vgs=vgs:V --map Vds=vds:V --map Ig=ig:A --map Id=id:A
<span class="prompt">$ </span>circuitrf model fit fet.cmc --step all --seed 1
<span class="prompt">$ </span>circuitrf model export fet.cmc -o fet.cmodel
<span class="prompt">$ </span>circuitrf model export fet.cmodel -o fet.va</code></pre>

Then place it — `ComposedFET:X1 g d 0 File=fet.cmodel` — and run it like any device. `fet.va` is the same model for
another simulator; `--json` adds `result.model.verilogA`: the path, the module, its parameter count and the switches
that change nothing for this model.

Exit **0** done; **1** refused; **3** finished, but a check failed; **130** cancelled, with nothing written. `fit --step
all` keeps the steps that finished before a cancellation. Over MCP it is the `model` tool, one mode per noun.

## `rail` — power integrity, headless {#rail}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf rail &lt;board.crail&gt; [--rail NAME] [--accurate] [-o out.{csv,npy,mat,txt,svg,pdf}]</code></pre>

`rail` runs a **`.crail`** — the document the [railRF window](railrf.html) edits. It resolves the
artwork and the stackup, extracts the copper, solves the rail at DC, checks the vias and prints the
answer. Every number comes out of the same call the window's **Run** button makes, and every pixel of an
`.svg` or `.pdf` report out of the same renderer the window draws with, so a report produced on a build
machine is the one you would have got by pressing the button.

**Omitting `--rail` runs them all**, in dependency order — the same shape `hb` and `lp` have for a
wrapped sweep, and for the same reason: a downstream rail solved on its own would start its source from
a nominal instead of from the upstream answer.

It can also take the `.clay`, the cell folder or the workspace the `.crail` sits in, and find it.

### What it reads, and what walks up to it {#rail-resolution}

Like [`em`](#em), the references are walk-ups rather than flags. The `.crail` names its artwork; the
artwork names — or inherits from its workspace — a technology.

| File | What it supplies |
|---|---|
| **`.crail`** | The rails, their references and extents, the sources, the loads and their currents, the parts, the targets, the band and the aggressors |
| **`.clay`** (or a Gerber set) | The copper being measured |
| **`.ctech`** | The stackup: conductor thicknesses and conductivities, the dielectrics between them, and the via entry's plated-wall thickness |
| **`.crlib`** | The part library the decoupling resolves against |

### Options {#rail-options}

| Option | Meaning |
|---|---|
| `--rail <name>` | Which rail. Omitting it runs every one. |
| `--fast` (default) / `--accurate` | [The two readings of the copper](railrf.html#speeds). Fast is the default, as in the window. |
| `--source REFDES.PIN=<model>` | Repeatable. `3.7V,50mOhm,10nH` — any subset, in any order, each field identified by its unit or by a `v=`/`r=`/`l=` key — or a Touchstone file. A row for the same anchor is **replaced**, not added beside it. |
| `--load REFDES.PIN[=<current>]` | Repeatable. **The current may be left out**: that makes it an observation port. |
| `--target-drop`, `--target-z`, `--mask [PORT=]<file>` | The target forms. A mask is per observation port; one that lands on no port is refused, because a mask nobody applied reads on the report exactly like one that was honoured. |
| `--aggressor NAME=<freq>[xN]` | Repeatable. `x` and `×` both spell the harmonic count. |
| `--reference <layer>`, `--extent as-imported\|filled\|infinite` | The return conductor, and how far it is taken to extend. |
| `--rows N`, `--all` | How much of the ranked breakdown to print. |
| `-o out.…` | `.csv` for the tables, `.npy`/`.mat`/`.txt` through the usual exporter, `.svg`/`.pdf` for the report page. |

**An anchor is `REFDES`, `REFDES.PIN`, or `@x,y` in DBU.** Headless it is always the coordinate form:
a `.crail` names no placement file, so there is nothing to resolve a refdes against — see
[what is not wired up yet](railrf.html#notyet).

**Values carry units**, through the same table the [expression engine](expressions.html) uses, so a
spelling that works in a `.cnl` works here. A bare number is base SI, which is what every number in a
`.crail` already is.

### What is refused, and what is not {#rail-refusals}

| Unstated | Answer |
|---|---|
| The **reference layer** | **Refused**, naming `--reference`. railRF never infers one. |
| The **technology** | **Refused.** Copper priced with no thickness and no conductivity produces numbers that look exactly like numbers with physics behind them. |
| The **Excellon coordinate format**, on a Gerber import | **Refused** — the same sentence [`convert`](#convert) gives. |
| The **via plating thickness** | **Not refused.** It is a setting, and every flag says which basis produced its limit. |
| A **load's current** | **Not refused.** It is an observation port, it contributes nothing to the DC solve, and the report lists it *as observed*. Refusing it — or defaulting it to zero — would make *not added* and *added with no current* indistinguishable. |

`-o out.sNp` is **refused**: Z(f) at the observation ports is the frequency answer, this verb answers
DC, and a Touchstone holding the DC point repeated would look like a measurement. `--set` is refused
too, naming the flags that do state those quantities: a `.crail` declares no variables, so a `--set`
accepted here would be silently dropped.

### Every export says which model produced it {#rail-provenance}

A file read six months later has no status strip beside it, so every format carries the model (Fast or
Accuracy), the reference extent, the copper temperature, how many parts are modelled from a file, how
many have no bias curve, and whether any ESR fell back to a class default — which makes a derived peak
height **indicative** rather than measured.

## `lvs` — does the artwork implement the drawing? {#lvs}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf lvs &lt;path&gt; [--no-reduce] [--flat] [--flatten-cell NAME] [--testbench]
<span class="prompt">  </span>[--recognize] [--set var=expr] [--severity warning|error] [-o report.txt]</code></pre>

`lvs` compares a cell's **layout** against its **schematic** and reports every device, net, terminal and
value the two disagree about. It answers the one question a headless client cannot answer any other way:
the design was drawn twice, and do the two drawings say the same thing? An agent that authored a `.clay`
cannot look at the screen.

Every finding comes out of the same call the [LVS panel](lvs.html#window)'s **Compare** button makes, so
a design that passes on a build machine passes when somebody opens it.

The path may be a **cell folder** (the default unit &mdash; its primary schematic against its primary
layout), a **workspace** (every cell holding both views), a **`.clay`** or a **`.csch`** (each finds its
sibling in the cell folder that holds it). A cell holding only one of the two views is reported and
skipped, not failed: that is the ordinary state of a design being drawn.

| Option | Meaning |
|---|---|
| `--no-reduce` | Compare object for object. By default parallel and series `R`/`C`/`L` collapse first, and every finding un-reduces to the objects you drew. |
| `--flat` / `--flatten-cell <name>` | Flatten the whole hierarchy, or one named sub-cell (repeatable). Every use is reported, so a design that quietly flattens everything is visible. |
| `--testbench` | Compare a bench as drawn rather than the cell it instantiates. |
| `--recognize` | Also read devices out of bare copper, through the technology's own `DeviceRules` deck. Off by default, and off for a process that declares no rules. |
| `--set var=expr` | Set a global before the schematic elaborates, exactly as a run verb does. |
| `--severity warning\|error` | What makes the exit code non-zero. Default `error`. |
| `-o report.txt` | The **only** thing this verb ever writes. With no `-o` it writes nothing at all, so it runs on a read-only tree and on a workspace another process has open. |

**Exit 0** when nothing at or above `--severity` was found, **1** otherwise, **130** on a cancellation.
A run holding warnings and no errors exits 0 and still reports every one.

With `--json`, every finding travels with a stable `lvs.` id and typed arguments &mdash; the layer, the
coordinate, the two values, the marker box &mdash; so a caller reads what it needs without parsing the
sentence apart. It **honours waivers and never creates one**: waiving is a reasoned act with a sentence
attached and belongs beside the thing being waived.

**It is not folded into [`check`](#check)**, deliberately. `check` has to stay cheap enough to call after
every edit; an LVS on a real board is seconds rather than milliseconds, and a `check` that had become
slow is a `check` people stop running. What `check` does carry is the
[terminal-map](lvs.html#terminals) validation, which is cheap and static.

See {{anchor: lvs|the LVS chapter}} for what the findings mean and a worked example on the shipped
example workspace.

## `recognize` — a board's artwork as a circuit {#recognize}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf recognize &lt;file.clay | cell folder | workspace --cell NAME&gt;
<span class="prompt">  </span>[-o circuit.cnl] [--into new:NAME | --into artwork] [--replace]
<span class="prompt">  </span>[--parts-out parts.csv] [--parts parts.csv] [--bom FILE] [--placement FILE]
<span class="prompt">  </span>[--placement-origin symbol|body|pin1] [--placement-unit mm|mil|in]
<span class="prompt">  </span>[--region x0,y0,x1,y1] [--ground NET | --ground-at x,y] [--vias model|ground]
<span class="prompt">  </span>[--coplanar auto|microstrip|gcpw] [--coplanar-factor K] [--start F] [--stop F] [--npts N]
<span class="prompt">  </span>[--digits N] [--free-orientation] [--arrange board|tidy]</code></pre>

`recognize` reads a board's copper and writes the circuit it implements: the ground, the vias that matter,
the ports, every two-terminal part as an R, L, C or S-parameter file, and every trace as an `MLIN` (with its
bends, tees, crosses and tapers), `CPWG`, `SLIN` or `TLIN`, with an S-parameter analysis ready to run. A part
whose value is not known becomes a global variable with a tuning range, so it is a knob in the Tuning and
Optimizer panels from the first simulation.

**With no `-o` and no `--into` it writes nothing**: it prints what it found, one line per kind of finding,
and the **parts table** as CSV. Review that table before writing anything:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf recognize Board --parts-out parts.csv
<span class="prompt">  </span># edit parts.csv: C6's Value to 10pF, L2's Kind to L …
<span class="prompt">$ </span>circuitrf recognize Board --parts parts.csv --into new:Board_model
<span class="prompt">$ </span>circuitrf sparam Board_model/schematic/Board_model.csch -o board.s2p</code></pre>

| Option | Meaning |
|---|---|
| `-o circuit.cnl` | Write the circuit as a netlist. Nothing else. |
| `--into new:NAME` / `--into artwork` | Write the schematic into a new cell beside the artwork's, or into the artwork's own cell (only when that cell has no schematic yet). |
| `--replace` | Replace a schematic this command wrote earlier; a history checkpoint is taken first. A schematic you drew yourself is never replaced. |
| `--parts-out` / `--parts` | Write the parts table as CSV; read an edited one back. A value you give replaces the variable that stood for it. |
| `--bom`, `--placement` | A bill of materials and a placement file. A placement file that does not say which origin it was exported against needs `--placement-origin`. |
| `--region x0,y0,x1,y1` | Read only this rectangle; a line it cuts becomes a port. **Every coordinate carries a unit** (`um`, `mm`, `mil`) &mdash; a bare number is refused. |
| `--ground NET`, `--ground-at x,y` | Read this net, or the copper at this point, as ground. |
| `--vias model\|ground` | Keep vias to ground as `VIAGND` (default), or make them plain grounds. |
| `--coplanar auto\|microstrip\|gcpw`, `--coplanar-factor K` | How a line with ground close beside it is read. |
| `--start`, `--stop`, `--npts` | The analysis range, each frequency with its unit. Default: the layout's EM setup's sweep, else 100 MHz – 6 GHz in 201 points. |
| `--digits N` | The significant digits every number in the circuit is written with, 1 to 15. Default 6. |
| `--free-orientation` | Draws every shunt part below its line. Without it, a shunt part is drawn on the side of its line that its copper is on — the app's **Link symbol and footprint orientation** setting. Applies to `--arrange tidy`. |
| `--arrange board\|tidy` | How the schematic is laid out. `board` (the default) puts each part roughly where it sits on the board, so the drawing can be checked side by side with the layout. `tidy` draws the signal path as one line from port to port with everything else hanging from it. The circuit is the same either way. |

**Exit 0** when the board was read (and written, when asked), **1** when it was refused &mdash; no technology,
no copper in the region, no port &mdash; with nothing written, **130** on a cancellation. With `--json` the
result carries every finding with its count and where it is on the board, and the parts table row for row.

`explain` on a schematic this command wrote shows where it came from &mdash; the layout, the region and the
options &mdash; and `explain --ref` resolves the layout's path.

### A plot digitised by hand {#recognize-by-hand}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf recognize plot.png --manual-box x0px,y0px,x1px,y1px[@deg]
<span class="prompt">  </span>--x-range LOW..HIGH[:log] --y-range LOW..HIGH[:log] [--y2-range LOW..HIGH[:log]]
<span class="prompt">  </span>[--curve "NAME=px,py;px,py;…[,axis=right][,is=S21:db]"] [--follow NAME=px,py]
<span class="prompt">  </span>[--sample N | --sample-step STEP] [--method linear|smooth] --csv out.csv [-o out.s2p]</code></pre>

The dialog's [Digitise by hand](create-from-image.md#by-hand) from the command line. Nothing is read off the picture:
the box is the plot area and the ranges are the values at its edges.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf recognize s21.png --manual-box 228px,147px,671px,443px \
<span class="prompt">  </span>    --x-range 1GHz..5GHz --y-range -36..0 --follow S21=560,193 --csv s21.csv</code></pre>

| Option | Meaning |
|---|---|
| `--manual-box x0px,y0px,x1px,y1px[@deg]` | Two opposite corners of the plot area, in pixels, `px` on every number. `@3` turns the box 3° counter-clockwise, for a scan that is not square. |
| `--x-range`, `--y-range`, `--y2-range` | The values at the box's left and right, and bottom and top, edges. An x range is frequencies with units (`100MHz..10GHz`), or plain numbers for an x that is not frequency. `--y2-range` is a second scale on the right. `:log` marks a logarithmic axis. |
| `--curve "NAME=px,py;…"` | A curve's points in pixels. `,axis=right` reads it on the right-hand scale; `,is=S21:db` makes it an S-parameter quantity, otherwise it is data. Repeatable. |
| `--follow NAME=px,py` | One click on a curve: it is followed by its colour inside the box and its points placed, one every 20 pixels and at every peak and dip. |
| `--sample N`, `--sample-step STEP` | Each curve sampled at N x values (101) from its first point to its last, or every STEP. |
| `--method linear\|smooth` | Straight between points, or a curve through them that never overshoots. Nothing is sampled beyond a curve's points. |
| `--csv out.csv`, `-o out.s2p` | The sampled curves as CSV; a Touchstone of the curves `is=` names, on a frequency x. |

A box given without both ranges is refused, naming the one missing. **Exit 0** when the CSV (and the Touchstone, when
asked) was written, **1** when refused.

## `impedance` — every trace against a target Z0 {#impedance}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf impedance &lt;layout&gt; [--target 50] [--tol 10] [--warn 20] [--max-freq 6GHz]
<span class="prompt">  </span>[--layers "Top Copper,Inner 2"] [--max-width &lt;um&gt;] [--via-transition 400um] [--width "Top Copper=457"]… [--no-scope]
<span class="prompt">  </span>[--region [&lt;layer&gt;@]x0,y0,x1,y1]… [--net &lt;name&gt;]… [--pick &lt;layer&gt;@&lt;x&gt;,&lt;y&gt;[:connected]]…
<span class="prompt">  </span>[--whole-layer &lt;layer&gt;]…
<span class="prompt">  </span>[--survey] [--severity warning|fail] [--ignore-accepted] [-o report.pdf [--report full|summary]]</code></pre>

`impedance` is the layout editor's [Impedance Analysis](layout-editor.html#impedance-analysis): it finds
every trace on the chosen copper layers, cuts it along its length with the same quasi-static
cross-section solve the canvas's **Trace Impedance** uses, and reports per trace Z0 min, max and
average, the share of its length inside target ± tolerance, its line type, and every finding &mdash; Z0
out of band, a return path that breaks, copper only partly under it, a reference that steps to another
layer. `-o` writes the **same PDF** the dialog exports.

The path is a **`.clay`** or a **cell folder** holding one. The technology resolves exactly as the editor
resolves it, and placed cells are flattened as a design-rule check flattens them.

**The review saved in the layout applies by default**, so a headless run reports what the editor
reports: the target, bands, frequency, layers, [trace widths](layout-editor.html#impedance-scope) and
[regions, picks and nets](layout-editor.html#impedance-scope-board) last chosen in the editor's Impedance
panel. A flag overrides the saved value, and the saved value overrides
the default. A saved layer the technology no longer has is skipped with a line on stderr.

| Option | Meaning |
|---|---|
| `--target <ohms>` | The target Z0. Default `50`. `50`, `50ohm` and `50R` all read as 50 Ω. |
| `--tol <percent>` | ± this many percent passes. Default `10`. |
| `--warn <percent>` | ± this many percent is a **warning** rather than a fail. Default `20`; it must be wider than `--tol`, so a `--tol` of 20 or more needs a wider `--warn` too. |
| `--max-freq <freq>` | The highest frequency the traces carry. A stretch outside the warning band shorter than **λ/20** there warns rather than fails &mdash; see [electrically short](layout-editor.html#impedance-findings) &mdash; and a [test pad hanging off a trace](layout-editor.html#impedance-pads) is judged there. **The unit is required** (`6GHz`); a bare number is refused. Default off. |
| `--severity warning\|fail` | What decides the exit code. Default `fail`; `warning` makes a warning exit 1 too. Warnings are reported either way. |
| `--layers "A,B"` | The copper layers, by the technology's layer names. Default every copper layer. A name that is not a copper layer is refused with the names that are. |
| `--max-width <um>` | The widest copper read as a trace. Default ten times the distance to the nearest other copper layer, between 1 and 8 mm. |
| `--via-transition <len>` | Trace within this distance of the land of a via on it is not checked, because a plane is normally cleared round a via; each skipped stretch is listed in its trace's notes. A bare number is µm. Default the saved setting, else 400 µm; `0` checks every trace up to its via. |
| `--width <layer>=<w>[,<w>…]` | Review only the traces of these widths on that layer; repeat it for another layer. A bare width is µm, as `--max-width` reads it, or give a unit (`18mil`, `0.457mm`). It **replaces** the saved widths for that layer for this run; a layer with no widths is reviewed at every width. A trace's width is the width over most of its length, and a width matches within 1 % (or 1 µm). |
| `--region [<layer>@]x0,y0,x1,y1` | Review the traces any part of whose centre line lies inside this rectangle, **whole** &mdash; on that layer only when one is named (`Bottom@10mm,5mm,30mm,20mm`), on every layer when none is. **Every coordinate carries its unit** (`10mm,5mm,30mm,20mm`, `400um`, `50mil`) and a bare number is refused, as for [`render --window`](#render): a layout coordinate could be DBU, µm or mm. [`explain --extents`](#explain) prints a layout's `window` in exactly this spelling. Repeatable. |
| `--net <name>` | Review the traces on copper carrying this net. Repeatable. |
| `--pick <layer>@<x>,<y>[:connected]` | Review the trace whose copper holds this point on that layer, coordinates with their units; `:connected` takes every trace joined to it through vias. A pick with no copper under it is reported on stderr and selects nothing. Repeatable. |
| `--whole-layer <layer>` | Review every trace on this layer while regions, nets or picks narrow the others. Repeatable. |
| `--no-scope` | Ignore the saved scope and review every trace. The saved target, bands and layers still apply. |
| `--ignore-accepted` | Report as if no finding had been [accepted](layout-editor.html#impedance-accept): every finding counts against its trace, and against the exit code. |
| `--survey` | List the width classes per layer &mdash; width, trace count, total length and one **typical** Z0 (a single cut at the middle of the class's longest trace) &mdash; and analyse nothing. The way to choose `--width`. |
| `-o report.pdf` | The PDF report. With no `-o` it writes nothing. |
| `--report full\|summary` | Which PDF `-o` writes: `full` (the default) lists every trace and finding; `summary` keeps the map and groups the findings, with the 20 worst in full &mdash; see [the Summary](layout-editor.html#impedance-report-summary). `--json` carries everything either way. With no `--report`, a Full report of more than 50 pages is still written, with a warning on stderr saying how many pages the Summary would take. |

**Accepted findings apply by default.** A finding accepted in the editor's panel is saved in the
`.clay`, and the verb reports it marked `✓ ACCEPTED` with its reason and date; it does not count against
its trace's verdict or the exit code. Acceptances that matched nothing in this run are listed before the
closing line. There is deliberately **no `--accept`**: a run's trace ids (`T1`, `T2`…) are that run's,
and an acceptance is a decision made reading the finding. A script that must accept one headlessly adds
it to the `.clay`'s `ImpedanceAcceptances`; [`reference layout`](#reference) documents the key &mdash;
the trace's two end points in whole µm and the finding's kind.

**Regions, nets and picks choose; widths filter.** A trace any `--region`, `--net` or `--pick` chooses
is reviewed (with none, every trace is), and `--width` then filters what they chose. With any of them
set, **a layer none of them reaches is not reviewed** &mdash; the scope line names it *nothing selected*
&mdash; unless `--whole-layer` keeps it. Each of the four **replaces the saved selectors of its kind**
for this run and leaves the other kinds as saved &mdash; so a lasso drawn in the editor, which has no
command-line spelling, still applies unless `--region` replaces it.

stdout starts with the target and, on the next line, the **scope in words** &mdash; *Top Copper at 457 µm
(1 trace). 21 traces on Top Copper are outside the scope and were not analysed.* &mdash; then one line per
trace &mdash; `PASS`, `WARN` or `FAIL` &mdash; its findings under it (`!` a fail, `?` a warning), then
one line per [junction](layout-editor.html#impedance-traces) &mdash; its kind and each arm's trace, width,
Z0 and result &mdash; and a closing line *N pass, W warning, F fail*, with the junctions' counts after it
when there are any. stderr says which layer is being analysed and, through a long solve, repeats the
line every few seconds with the cross-sections done, the time elapsed and an estimate of the time left on
that layer. Traces outside the
scope are not cut, solved or listed, only counted, and they still count as grounded copper beside the
traces under review. Lengths
and coordinates are in **the layout's own unit**. With `--json` the result carries every layer, trace and
finding, with coordinates and lengths in **µm** whatever the layout's unit, so a script reads one unit;
`warningCount` sits beside `pass` and `fail`, a trace's verdict can be `"warning"`, and every finding
carries `"severity": "warning"` or `"fail"` &mdash; and an accepted one `"accepted": { "reason", "date" }`.
A test pad loading its line is a finding of kind `"pad-shunt"` whose `shunt` holds `padLayer`, `diameter` (µm),
`planes`, `capacitance` (the pad's, F), `stubCapacitance` (pad, via barrel and unused pads, F), `inductance` (the
via's, H; absent with no via), `resonanceHz`, `s11Db`, `atHz` and `lineOhms`.
Each layer's `junctions` lists its junctions (`id`, `kind`, `verdict`, `at`, and `arms`, each with
`trace`, `width`, `z0` and `verdict`), and `junctions` at the top counts them; both are absent when there
are none. `accepted` counts the accepted findings and `staleAcceptances` lists the saved acceptances that matched
nothing (`key`, `kind`, `layer`, `summary`, `reason`, `date`). `scope` is the scope sentence and each layer's `outOfScope`
its count of traces left out. With `--survey`, the result is `impedanceSurvey` instead: per layer its
`classes`, each with `width`, `widthMin`, `widthMax`, `traces`, `length` (µm) and `typicalZ0`.

**Exit codes follow [`check`](#check)'s convention**, counting **un-accepted** findings only. **0** when
no trace or junction fails &mdash; warnings are always reported and still exit 0 &mdash; **1** when one fails (or no part of it could be solved) or the
run is refused, and with `--severity warning` when one warns; **130** on a cancellation. A cancelled run still writes the report for the layers that **finished**,
and says on its first page that it was cancelled.

### The line calculator: `impedance --tech` {#impedance-line}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf impedance --tech &lt;technology&gt; --layer &lt;name&gt; (--width &lt;w&gt;[,&lt;w&gt;…] | --z0 &lt;ohms&gt;[,&lt;ohms&gt;…])
<span class="prompt">  </span>[--gap &lt;g&gt;] [--freq 10GHz]</code></pre>

With `--tech` and no layout, `impedance` answers for a line **that is not drawn yet**: the width a Z0
needs, or the Z0, ε<sub>eff</sub>, loss and guided wavelength a width gives, on one copper layer of a
technology. Every row has **two answers side by side**:

- **circuit model** &mdash; what a line of that width on that layer simulates as: an **MLIN**, a
  **CPWG** when `--gap` is given, or an **SLIN** when the layer has a ground plane both above and below.
  It shows the substrate the layer resolves to, the static Z0 and ε<sub>eff</sub>, and at `--freq` the
  dispersive Z0 and ε<sub>eff</sub>, the loss in dB/mm and λ<sub>g</sub>. It is the same number a run uses.
- **cross-section (impedance)** &mdash; what `impedance` would report on a line drawn at that width: the
  quasi-static Z0 and ε<sub>eff</sub>, with λ<sub>g</sub> from that ε<sub>eff</sub>. The solve has no
  dispersion and no loss.

The **difference** column shows how far apart they are. A large gap is worth seeing before you draw: the
schematic simulates the model, and the drawn line measures the cross-section.

`--z0` gives each column **its own width**. The model width comes from the same synthesis the
parameter editor's Z0 field uses. The cross-section width is solved to 1&nbsp;nm, the finest width a
layout can hold. A *Z0 at the model's W* row shows what a line drawn at the model's width would measure.

| Option | Meaning |
|---|---|
| `--tech <t>` | A `.ctech` file; a workspace folder, or a document inside one (it resolves the technology a schematic there would use); or the id of a technology that ships with circuitRF ([`reference technologies`](#reference) lists them). |
| `--layer <name>` | One copper layer: a drawing layer's name or a stackup conductor's. A name the technology does not have is refused with the names it does have. |
| `--width <w>[,<w>…]` | Widths to analyse, one row each. A bare number is µm, or give a unit (`18mil`). |
| `--z0 <ohms>[,<ohms>…]` | Impedances to find a width for, one row each. |
| `--gap <g>` | Makes the line **coplanar**, with ground on the same layer this far from each edge; the model is then a CPWG. On a layer with a ground plane both above and below it the line is a stripline with coplanar ground, and the cross-section is the only answer, because no circuit component models one. |
| `--freq <freq>` | Where the dispersive values, the loss and λ<sub>g</sub> are given, **with its unit** (`10GHz`). Without it, only static values are shown. |

A layout path given with `--tech`, or a flag that only means something for a drawn layout (`--target`,
`--region`, `-o` and so on), is **refused rather than ignored**. With `--json` the result is
`impedanceLine`: the substrate and, per row, `model` (`widthUm`, `z0Static`, `eeffStatic`, `z0`, `eeff`,
`lossDbPerMm`, `lambdaGUm`), `crossSection` (`widthUm`, `z0`, `eeff`, `lambdaGUm`, `configuration`),
`z0DifferencePercent` and, on a `--z0` row, `crossSectionAtModelWidth` and `widthDifferencePercent`.
Exit **0** when every row is answered, and **1** when the calculator is refused or a row has no answer.
A column that cannot answer one row is a warning.

## `review` — a reviewer's checklist over the board {#review}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf review &lt;layout&gt; [--cell &lt;name&gt;] [--checks id,id] [--skip id,id]
<span class="prompt">  </span>[--set &lt;check&gt;.&lt;threshold&gt;=&lt;value&gt;]… [--defaults] [--ignore-accepted] [--as pcb|mmic] [--all]
<span class="prompt">  </span>[--severity warning|none] [-o report.pdf]
<span class="prompt">$ </span>circuitrf review [&lt;layout&gt;] [--as pcb|mmic] --list-checks</code></pre>

`review` runs the [Layout Review](layout-editor.html#layout-review): the built-in checks a person
reviewing a board runs by eye, each marking the places worth a look with a sentence saying why. It is
not a design-rule check and does not replace one: a finding is **advice**, and the reviewer either changes
the artwork or accepts the finding with a reason.

The path is a **`.clay`**, a **cell folder** holding one, or a **workspace** with `--cell`. The technology
resolves as the editor resolves it, and placed cells are flattened as a design-rule check flattens them.
The board is read once and every check shares that one copy.

**A review is of a board or of a die.** A PCB review runs the checks below marked neither *MMIC only*; a MMIC
review runs `ground-pin-via`, `coupled-lines`, `probe-pads` and `impedance`. The layout's saved choice applies &mdash; a board when it has
none &mdash; and `--as` overrides it for this run.

| Check | What it marks | Threshold (default) |
|---|---|---|
| `via-in-pad` | A via inside an SMD pad's soldermask opening. Vias in a thermal pad are left to `thermal-vias`, and a pad centred on its one plated drill is a through-hole pin, whose drill is not a via. | &mdash; |
| `ground-via-distance` | A ground pad with no ground via within the distance, pad edge to barrel edge; the nearest one's distance is stated, looked for out to ten times the threshold. Thermal pads and through-hole pins are not judged. | `max-distance` (0.5 mm) |
| `shared-ground-via` | A ground via that is the only one within `ground-via-distance.max-distance` of two or more ground pads. | &mdash; |
| `thermal-vias` | A thermal pad &mdash; a ground pad of at least `thermal-pad-area` &mdash; with fewer ground vias under it than one per `max-via-pitch` squared, or none; a finding states the pad's average pitch. A via counts when at least half its barrel is inside the pad. `thermal-pad-area` is the thermal-pad size all four pad checks use. | `thermal-pad-area` (2 mm²), `max-via-pitch` (1 mm) |
| `stacked-traces` | Two traces on adjacent copper layers, on different nets, running the same way on top of each other for at least `min-length`, each grown by `lateral-margin` first; a crossing is not a run. Layers with another copper layer between are not compared. A trace is what [`impedance`](#impedance) finds. | `lateral-margin` (0), `min-length` (1.0 mm) |
| `coupled-lines` | Two traces of different nets on one copper layer running the same way, closer than `spacing` for at least `min-length`, both in substrate heights (h: the dielectric between the layer and its ground reference). Reported with the run's length and the closest gap. PCB and MMIC. | `spacing` (2 h), `min-length` (5 h) |
| `edge-copper` | Copper on any copper layer closer to the board edge than the pullback. The board edge is read off the board-outline layer &mdash; a closed outline, or a line drawn round the board &mdash; and cut-outs in the board are edges too. | `pullback` (0.25 mm) |
| `edge-trace` | A trace on any copper layer closer to the board edge than `min-distance`. Copper that is not a trace is `edge-copper`'s alone. | `min-distance` (1.0 mm) |
| `edge-stitching` | Within `band-width` of the edge: a stretch at least `max-gap` long with no ground copper on the top or bottom copper (one finding per layer), and a gap of at least `max-gap` between ground vias (a finding on no layer). Measured along the middle of the band. | `band-width` (1.0 mm), `max-gap` (2.0 mm) |
| `ground-pin-via` | MMIC only. A placed part's terminal whose copper is ground with no via to the ground plane within `max-distance`, terminal to the via's edge; the nearest one's distance is stated, looked for out to ten times the threshold. | `max-distance` (100 µm) |
| `probe-pads` | MMIC only. The placed GSGPADs: one whose pitch is not `probe-pitch` (the lab's probe, within 0.5 µm), a ground pad not on ground, and metal within `approach` in front of the pads on their metal or any above it. A pad inside another cell or in an array is not judged. | `probe-pitch` (150 µm), `approach` (100 µm) |
| `impedance` | Each trace in the layout's saved [Impedance review](layout-editor.html#impedance-analysis) scope that does not pass it &mdash; FAIL, WARN or unsolved &mdash; with its verdict, worst Z0 against the target band, line type and finding kinds, marked where its findings are. Its line also says the scope and how many traces passed. With no scope saved it is **not run**: the review never analyses every trace on the board. | None of its own: target, bands, layers and scope are the Impedance review's |

**The review saved in the layout applies by default** &mdash; which checks run, and each threshold that
was changed from its default. `--set` overrides one for this run; `--defaults` ignores what was saved.
`impedance` has no thresholds here: its settings are changed in the Impedance panel or with
[`impedance`](#impedance), and `--list-checks` shows them read-only.

| Option | Meaning |
|---|---|
| `--cell <name>` | With a workspace: the cell whose layout to review. |
| `--checks id,id` | Run only these checks. |
| `--skip id,id` | Do not run these checks. |
| `--set <check>.<threshold>=<value>` | A threshold for this run, e.g. `edge-copper.pullback=0.3mm`. **A length carries its unit** (`0.3mm`, `300um`, `12mil`), and a bare number is refused, as for [`render --window`](#render). **An area carries its unit squared** (`thermal-vias.thermal-pad-area=4mm2`, `4e6um2`, `6200mil2`), and a bare number is refused for the same reason. A height is a number of substrate heights (`coupled-lines.spacing=3`). Repeatable. |
| `--defaults` | Ignore the review saved in the layout. |
| `--ignore-accepted` | Report as if nothing had been accepted: every finding counts. |
| `--as pcb\|mmic` | Review as a board or as a die: which checks run. Naming a check the other kind runs, in `--checks`, `--skip` or `--set`, is refused. |
| `--all` | List every finding. By default at most 20 per check per layer are listed, followed by *… and N more*. |
| `--severity warning\|none` | What decides the exit code. Default `none`: findings are advice. `warning` makes an unaccepted finding exit 1. |
| `--list-checks` | List the checks of this kind of review with their thresholds, their defaults and the layout's saved values, and run nothing. With no path it lists the defaults. |
| `-o report.pdf` | Also write the review as a PDF &mdash; the report the Review panel's **Export PDF** writes. The path must end in `.pdf`. |

**The PDF** is short whatever the board: a summary page, then for each check with findings one map page and
one page of findings. The summary page names the layout, its technology and
the date, and has one row per check: the thresholds it ran with (a threshold changed from its default is
starred and says the default), its findings and accepted counts, and what it came to &mdash; *No findings*, the
layers its findings are on, or *Not run* with the sentence that fixes it. The map page has a map of each layer the check found
something on &mdash; the whole page for one layer, side by side for several &mdash; each drawing the board outline
and that layer's copper, faded, in the technology's colour, with each finding outlined and numbered; findings too
close to draw apart are one badge carrying their count. A `stacked-traces` map is of the upper layer, with the
lower layer's copper outlined. A findings page lists the first 20 findings with where each is and its sentence, then
*… and N more*, and after them the accepted findings with their reasons; `--all` or `--json` has every one. A
cancelled run's PDF holds the checks that finished and says on its first page that the run was cancelled.

stdout gives, per check, a line with its count &mdash; accepted findings counted separately &mdash; and the
findings grouped by layer under it, each with where it is in the layout's own unit. Lengths and areas are said in
that unit too (an area in it squared), in the sentences and in `--list-checks`. A check that cannot
run on this technology says **not run** and the one change that would let it, and the run carries on with
the others: a technology with no board-outline layer, for one, cannot say where the board edge is. stderr
says which check is running.

**Accepted findings apply by default.** An accepted finding is reported `ACCEPTED` with its reason and
does not count. A saved acceptance that matched nothing is listed as stale. There is no `--accept`: a
script that must accept a finding headlessly adds it to the `.clay`'s `ReviewAcceptances` with the
finding's `key`; [`reference layout`](#reference) documents the fields. An `impedance` finding is the
exception: it is accepted in the Impedance review (the `.clay`'s `ImpedanceAcceptances`), and is reported
`ACCEPTED` when every finding on its trace is; a `ReviewAcceptances` entry never covers it. Its `key` is the
Impedance review's trace key, `layer|x,y|x,y` with the trace's end points in whole µm.

With `--json` the result is `review`: `target` (`pcb` or `mmic`), `technology`, `displayUnit`, `warnings` (unaccepted), `accepted`,
`notes` (checks not run), `checks` (each `id`, `title`, `state` &mdash; `ran`, `not-run` or `cancelled`
&mdash; `reason`, `findings`, `accepted`, its `thresholds` with `value`, `default` and `text`, and a
`summary` where the check states one), and
`findings` (each `check`, `layer`, `severity` &mdash; `warning`, or `note` for a check not run &mdash;
`sentence`, `measured`, `key`, `marker` as a bounding box in **µm**, and `accepted` with its `reason`
and `date`), then `staleAcceptances`. With `--list-checks` the result is `reviewChecks` instead, where
`impedance` carries its Impedance review settings as `settings`.

**Exit codes:** **0** when it ran, whatever it found; **1** on a refusal, or with `--severity warning` when
an unaccepted finding remains; **130** on a cancellation, reporting the checks that finished. `review`
writes nothing but `-o`'s PDF.

## `smith` — a matching network, headless {#smith}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf smith &lt;match.csmith&gt; [--at &lt;freq&gt;] [-o out.{s1p,svg,pdf,png}]</code></pre>

`smith` evaluates a **`.csmith`** — the document the Smith Chart window edits. It
walks the cascade from the generator to the load at the design frequency, prints what the window's
status strip states, and then prints the walk **one node at a time**.

That table is the reason to run this rather than open the window. The reading answers *is it matched*;
the table answers *where did it stop being matched*, which is the question you have when the answer is
no — and it is what you diff between two revisions of a network.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf smith Lmatch.csmith
L-match demo — Smith chart, Z0 50 Ω
  design frequency   2 GHz
  generator          10 − j10
  load               17.81 + j20.38
  Γ                  0.538 ∠131°
  VSWR               3.329
  mismatch           1.48 dB

  node  element                          Z (Ω)                    Γ
     0  (generator)                       10 − j10                0.6778 ∠-157°
     1  L1                                10 + j17.65             0.6991 ∠140°
     2  C1                                17.81 + j20.38          0.538 ∠131°</code></pre>

### Where it evaluates {#smith-at}

`--at` moves the design frequency for this run only — the document is not touched. Write the unit and
it is honoured (`2.4 GHz`, `900 MHz`, `1.9e9`); a bare number is hertz.

**Outside the generator table's span it refuses, with the span in the sentence.** The generator
impedance is interpolated between the table's rows and never extrapolated past them, so a frequency
the table cannot answer for has no answer at all. The one exception is a **single-row table**: one row
is one impedance, flat, and every frequency is legal against it.

There is **no `--sweep`**. The swept band is always walked, across the generator table's own span,
so there is nothing to switch on — and a single-row table has no band, because one row is one
impedance and a band needs two ends.

### What it writes {#smith-output}

| `-o` | You get |
|---|---|
| `out.s1p` | The load reflection coefficient as Touchstone: **the whole band**, or **one point** at the design frequency when the generator table states a single frequency and there is no band. Referenced to the chart's own Z₀. |
| `out.svg`, `out.pdf`, `out.png` | The chart — the trajectories, the constant-Q arcs, the swept band, the load points and their frequency labels, the conjugate-match targets, your overlays and your markers. The same picture the window's **Copy chart** puts on the clipboard. |

`.s2p` and the rest are **refused**: what this verb has is the load, which is one reflection
coefficient, and a two-port built from it would be three quarters invented.

The picture takes the same options [`plot`](#plot) takes — `--size`, `--scale`/`--dpi`,
`--background`, `--dark`.

### What it will not guess {#smith-refusals}

`--set` is **refused**. A `.csmith` states every element value as a number, with no expressions and no
variables, so there is nothing for an override to replace — and a flag accepted and then dropped means
the run answered a different question than the one you asked. `--at` moves the design frequency;
anything else is a change to the **document**, which you make by writing it.

An **overlay that does not resolve** is a warning and not a refusal: reference material that is missing
must not take the work down with it, so the chart still draws everything that did resolve and the
warning names what did not.

## `snp` — a Touchstone file, at a glance {#snp}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf snp &lt;file.sNp&gt; [--params S11,S21|all|reflections|default]
                     [-o out.{svg,pdf,png,cdd}]</code></pre>

`snp` is the [S-Parameter Viewer](sparameter-viewer.html) with no window. It prints what the file
states — ports, the option line, the frequency range and how it is spaced, whether a sample is at
0 Hz, the reference impedance, whether it carries noise data — and names the terms it would plot.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf snp amp.s2p
amp.s2p
  Ports         2
  Parameters    S, MA (magnitude, angle)
  Unit          GHz
  Points        41
  Start         1 GHz
  Stop          5 GHz
  DC point      No ⚠
  Spacing       uniform, step 100 MHz
  Reference     50 Ω
  Noise data    none
  Plotted       S11, S12, S21, S22</code></pre>

Those lines are the viewer's own, so a terminal and the window cannot spell one file differently.

### Which terms {#snp-params}

`--params` chooses them, comma-separated: `S21`, or `S(2,1)` once a file has ten ports or more, where
`S101` would be ambiguous. `all`, `reflections` and `default` are words. The default is every term up
to four ports, and above that the reflections plus each forward neighbour — the viewer's own opening
selection. A term the file does not have is a refusal naming the file's port count.

### What it writes {#snp-output}

| `-o` | You get |
|---|---|
| `out.svg`, `out.pdf`, `out.png` | The viewer's four plots: reflections on a Smith chart, transmissions on a polar chart, magnitude in dB and phase in degrees, over exactly the file's own frequency range. |
| `out.cdd` | The same plots as a Data Display — what the viewer's **Save as Data Display** writes for this file, so the two cannot drift. |

The picture takes the same options [`plot`](#plot) takes — `--size`, `--scale`/`--dpi`,
`--background`, `--dark`.

### What it reports, and the exit code {#snp-exit}

A file whose name claims a different port count, whose frequencies are not strictly increasing, or
whose reference impedance has no positive resistance, is reported in the viewer's own sentence and the
command exits **1**. Anything you asked to be written is written first: a picture of a broken file is
still the picture the window shows of it.

Passivity, reciprocity and causality are not this verb's business. [`check`](#check) measures all three.

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
| OASIS | `.oas`, `.oasis` | ✓ | ✓ |
| DXF | `.dxf` | ✓ | ✓ |
| Gerber + Excellon | a **folder**, or one Gerber/drill file | ✓ | a **folder** |
| Board | `.kicad_pcb` | ✓ | ✓ |
| STEP | `.step`, `.stp` | into a **new `.c3d`** | from a `.c3d`, a `.clay`, a cell folder, or any source above |

**Every ordered pair works** — DXF to Gerber, Gerber to board, GDSII to DXF, board to GDSII, and the
rest. There is no privileged direction and no hub format you have to route through by hand: a
conversion is an import followed by an export, and `convert` does both.

Formats are read off the paths. A folder means Gerber; a file with no telling extension is classified
by its *content* — an OASIS file by the signature it begins with, anything else through the same
classifier the Gerber import uses. `--from` and `--to` override
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

Bring a Gerber set in on a stackup you already have, writing no technology of its own:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert fab/ -o cells/ --to clay --into-tech tech/pcb-6layer_FR-4_1.6mm_1oz.ctech</code></pre>

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

### OASIS, and the second GDSII route {#convert-oasis}

OASIS is read and written by **gdstk**, the same reader and writer **File ▸ Import ▸ OASIS (gdstk)…** and
**File ▸ Export ▸ OASIS (gdstk)** use; [Interchange](layout-editor.html#oasis-gdstk) says what an OASIS file
keeps and what it does not.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert mmic.gds -o mmic.oas
<span class="prompt">$ </span>circuitrf convert mask.oas -o cells/ --to clay --tech process.ctech
<span class="prompt">$ </span>circuitrf convert Amp/layout/Amp.clay -o amp.oas --oas-compression 9</code></pre>

An OASIS export takes the Export OASIS dialog's options as flags, at the dialog's defaults:
`--oas-compression 0`–`9` (default `6`), `--oas-validation none|crc32|checksum32` (default `crc32`) and
`--oas-standard-properties`. They apply to an OASIS **target** only; on any other conversion they are
refused rather than ignored. Two runs with the same options write the same bytes.

**`--engine gdstk` sends a GDSII end through gdstk too**, instead of circuitRF's own GDSII reader or
writer, which stay the default (`--engine native`). It is the command-line twin of **GDSII (gdstk)…** in
the File menus. A conversion with no GDSII end refuses the flag. When a GDSII file reads differently from
what you expect, converting it once each way and comparing is a quick way to see whether the file or the
reader is at fault:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert lib.gds -o native/ --to clay
<span class="prompt">$ </span>circuitrf convert lib.gds -o gdstk/ --to clay --engine gdstk</code></pre>

gdstk runs as a separate program shipped beside circuitRF. On an installation without it, a conversion
with an OASIS end, or with `--engine gdstk`, is refused before anything is read, and says why.

### STEP, both ways {#convert-step}

A STEP file is a solid model, so it goes **into a 3D view**, and anything circuitRF can draw in 3D comes
**out as one**. Both directions call the function the 3D editor's own dialog calls — *Import STEP…* and
*Export STEP…* — so a conversion here and the same one in the editor produce the same document, and the
same bytes but for the file's time stamp.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert body.step -o Connector/3d/Connector.c3d --material "flange=Connector alloy"
<span class="prompt">$ </span>circuitrf convert Launch/3d/Launch.c3d -o launch.step
<span class="prompt">$ </span>circuitrf convert board.kicad_pcb -o board.step</code></pre>

- **Import** writes a **new** `.c3d`, with one `Step` object per solid part and the STEP file copied beside
  it. The target must be a `.c3d` path. Each part takes a material as the dialog's table would give it — by
  its name, then its colour, from the technology the new file resolves (`--tech` names another) — and
  `--material <part>=<material>` answers any the file cannot. `--part <path>` imports only the named parts
  (their occurrence paths, `1/2`). A part nothing matches is imported with **no** material, and the run says
  so: it is drawn, and it is left out of a solve until it is given one.
- **Export** writes one file, flattened with the overlap precedence applied, in millimetres (inches for a
  mil or inch design), AP214. `--assembly` keeps placed cells as sub-assemblies, `--as-drawn` writes each
  object as drawn rather than cut by precedence, `--thicken-sheets` gives sheets their thickness,
  `--include-airbox` adds the first 3D setup's air box, `--schema ap242` and `--view 3d|layout` (for a
  cell with both) do what they say. A source that is not already circuitRF's — GDSII, a board — is imported
  first, exactly as the other conversions do.

The geometry kernel does the work in both directions, so on an installation without it both are refused,
saying why and how to restore it.

### glTF, for another renderer {#convert-gltf}

A 3D view comes out as **binary glTF** (`.glb`): the model as the 3D view draws it, with its
[appearances](drawing-in-3d.html#appearance) as glTF materials and its smooth normals, for a path tracer or any other
renderer. It calls the function *File ▸ Export ▸ glTF…* calls, so a file written here and one written from the editor
are the same bytes when neither includes a camera.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert Package/3d/Package.c3d -o package.glb
<span class="prompt">$ </span>circuitrf convert Package/3d/Package.c3d -o package.glb --gltf-assembly
<span class="prompt">$ </span>circuitrf convert cell/3d/cell.c3d -o hot.glb --gltf-field Top</code></pre>

- The source is a `.c3d`. What it shows is the document's own: an object the file hides is left out, and so are the
  air box, ports, boundaries and reference images.
- `--gltf-assembly` writes each placed cell as a node that shares its meshes; without it everything is flattened.
- `--gltf-field <plot>` adds a Surfaces or Faces field plot as an unlit mesh with per-vertex colours (at phase 0).
  `--region <name>` names the region a Surfaces plot of a volume quantity is drawn on, as `render --region` does. A
  ClipPlane plot is refused.
- The file carries the Look's camera when the document saves one (*Set Camera* in the Look panel), and no camera
  otherwise.
- circuitRF writes glTF and does not read it: a `.glb` or `.gltf` source is refused, and so is a `.gltf` target.

### Options {#convert-options}

| Option | What it does |
|---|---|
| `-o, --output <path>` | The file to write — or the **folder**, for `gerber` and `clay`; a file-shaped path there is refused. Required. |
| `--from <fmt>`, `--to <fmt>` | `clay`, `gdsii`, `oasis`, `dxf`, `gerber`, `board`, `step`, `gltf` (a target only). Say it when the path does not. |
| `--engine native\|gdstk` | Which GDSII reader or writer a GDSII source or target goes through. Default `native`. See [OASIS](#convert-oasis). |
| `--oas-compression <0-9>`, `--oas-validation none\|crc32\|checksum32`, `--oas-standard-properties` | OASIS export: the Export OASIS dialog's options. Defaults `6`, `crc32`, off. |
| `--cell <name>` | Which cell to export, when the source holds several. |
| `--list-cells` | Report what the input holds and write nothing. |
| `--name <stem>` | What to call the written Gerber file set. Default: the cell's name. |
| `--tech <file.ctech>` | The technology to convert against, instead of the one the layout resolves. |
| `--into-tech <file.ctech>` | A Gerber set or a board file into a `clay` folder **using** this technology: the cells reference it, nothing is added to it, and no `.ctech` is written. Refused together with `--tech`, and for any other source. See [below](#convert-tech). |
| `--workspace <file.cws>` | The workspace a `.clay`'s references resolve against. Default: the nearest one above it. |
| `--keep-cells <dir>` | Keep the cells the import produced instead of discarding them. |
| `--dbu <n>` | Database units per micron for an imported design. Default `1000` — one DBU is one nanometre. |
| `--dxf-version <v>` | `AC1015` (R2000), `AC1018` (R2004), `AC1032` (R2018, the default). |
| `--dxf-units <n>` | The `$INSUNITS` value for a DXF that declares none. |
| `--drill-units <mm or inch>` | Excellon coordinate units, when the file does not say. Applies to **every** drill file in the set. |
| `--drill-format <int>:<dec>` | Excellon digit counts, e.g. `2:4`. Applies to every drill file in the set. |
| `--drill-zeros <leading or trailing>` | Excellon zero suppression. Applies to every drill file in the set. |
| `--accept-inferred-drill-format` | Take each drill file's own inference rather than refusing. |
| `--material <part>=<material>` | STEP import: the material a part takes. Repeat it per part. |
| `--part <path>` | STEP import: import only this part (its occurrence path). Repeat it per part. |
| `--assembly`, `--as-drawn`, `--thicken-sheets`, `--include-airbox`, `--schema ap214\|ap242`, `--view 3d\|layout` | STEP export: the Export STEP dialog's choices, one flag each. |
| `--gltf-assembly`, `--gltf-field <plot>`, `--region <name>` | glTF export: the Export glTF dialog's choices. See [glTF](#convert-gltf). |
| `--arc-tolerance <len>` | A Gerber source: how far a chord may stray from an arc where a curve has to become a polyline, with its unit, from `1nm` to `100um`. Default `1um`, the same as **Settings ▸ General ▸ Gerber Import**. A bare number is refused. |
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
- **`--into-tech` imports a Gerber set or a board file into a technology you already have**, the way
  the Technology row of the **File ▸ Import ▸ Gerber** and **Board** layer-mapping dialogs does. Each
  layer lands on that technology's own layer, the cells reference the file you named, and no `.ctech`
  is written. A Gerber set is refused, and nothing is created, when its copper does not fit that
  technology's stackup: a different number of copper files than conductors, a copper file on a
  conductor of a different rank, two on one conductor, or drill data on a layer no via binds. A board
  is refused when its number of copper layers is not the stackup's number of conductors. It takes a
  `clay` target only. The other importers already keep the stackup of the technology `--tech` names,
  so the flag is refused for them.
- **`--keep-cells <dir>` leaves a design you can open.** Cells plus the technology they point at —
  the honest way to see what a conversion actually understood before you send the result anywhere.

**GDSII is the one exception, and it is the format's own doing.** GDSII identifies a layer by a
number, not a name, so an import has nothing to name it *with*: the numbers come through exactly, the
names do not. Convert from GDSII with `--tech` pointing at the technology those numbers belong to and
the names come back. An OASIS file *may* name its layers; when it does, a name is matched against
`--tech` before the number is, and when it does not, OASIS behaves as GDSII does.

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
technology and the layer mapping needs confirming; when a coordinate overflows GDSII's 32-bit range, or a layer, datatype or array count overflows its 16-bit one (an OASIS export is held to the same limits); when an OASIS
repetition would expand into more than a million shapes; and
when the source holds several cells and none of them is an unambiguous top. Every refusal exits `1`
and writes nothing at all.

Everything short of a refusal is a **note on stderr**, counted and named: labels flattened to
geometry, curves turned into polygons, holes keyholed, bitmaps dropped, unresolved instance
references, layers with no mapping in the target format. stdout carries only the paths written, one
per line, so a script can consume them directly:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert board.kicad_pcb -o fab/ --to gerber 2&gt; convert.log | zip -j fab.zip -@</code></pre>

## `new` — a workspace or a cell {#new}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf new workspace &lt;dir&gt; [--name N] [--tech &lt;id&gt;|none] [--length-unit um|mm|mil]
                        [--vendor-library &lt;name&gt;=&lt;folder&gt;]...
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

`--length-unit` writes the copied technology's display unit — what a preferred length unit in **Settings ▸ General
▸ Units** does for **File ▸ New Workspace**. Left out, the technology keeps its own.

`--vendor-library` references a [vendor part library](pdk-integration.html#vendor) from the start, and
may be given more than once. **File ▸ New Workspace** references the libraries you have ticked for new
workspaces; headless there is no such list, so nothing is referenced unless you name it here.

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

<h3 id="new-add"><code>new</code> is one verb with a noun</h3>

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

## `import library` — a vendor part library, referenced in place {#import-library}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf import library &lt;folder&gt; --into &lt;workspace&gt; [--name N]</code></pre>

The same code **File ▸ Add Vendor Library…** runs: the folder is indexed, and the workspace's `.cws`
gains a reference to it under `--name` (the folder's own name when left out). Nothing is copied.
Adding a name that is already referenced repairs it to the new folder.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf import library ~/libraries/caps --into ~/designs/Amp --name Caps
<span class="output">Caps	/home/you/libraries/caps
12,410 parts in 31 files.
Folders: general (8,902), automotive (3,508).
2 files are encrypted for another simulator and cannot be read. The plain-text (static) edition of this library can be.</span></code></pre>

A folder with no readable part is refused (exit 1) with that sentence. `--json` carries the counts and
the folder groups as `result.library`.

A run verb given a `.csch` that places vendor parts says two things on stderr, each a warning that leaves
the exit code alone: a placed part that could not be read (`run.vendor-part.unreadable`, naming the part
and the library; the run goes without it), and a part the run's analyses take outside its stated
frequency range (`run.vendor-part.out-of-range`, once per part number). A DC operating point never
warns about range.

---

## `import schematic` — another editor's schematic, as a cell {#import-schematic}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf import schematic &lt;file&gt; --into &lt;workspace-or-dir&gt; [--cell N] [--port &lt;ref-or-net&gt;]...
                                  [--symbol-cell &lt;ref&gt;]... [--codepage N] [--list]</code></pre>

The same code **File ▸ Import ▸ Schematic…** runs, with the dialog's defaults: the cell is named after the file, every
part goes where the import's map sends it, and no Port is made. The format is recognised from the file's content.

| Option | |
|---|---|
| `--port <ref-or-net>` | make a connector (by reference) or a net (by name) a circuitRF Port; repeat it, in port order. With any Port, `SP1` is added |
| `--symbol-cell <ref>` | keep a part that would become a native R, L or C as the source's own symbol |
| `--codepage N` | how EDIF and binary `.DSN` text that is not UTF-8 is decoded; 1252 by default |
| `--list` | print the pages, one `part` line per part with what it becomes, and one `port` line per Port candidate; write nothing |

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf import schematic matching.kicad_sch --into ~/designs/Amp --port J1 --port J2
<span class="output">/home/you/designs/Amp/matching
/home/you/designs/Amp/matching/schematic/matching.csch</span></code></pre>

stdout is the paths written: the cell, its schematic and every symbol or sheet cell. The import's report goes to
stderr. **Exit 1 when the connectivity check is not matched**, with the cell written so the differences can be looked
at; any refusal writes nothing and exits 1. What becomes what, and what the check compares, is in
[Importing a schematic](schematic-import.html).

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

<h3 id="render-detail">Size, and what <code>--detail</code> costs</h3>

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

Layout only, mutually exclusive, and the default is every layer. What you have hidden in the layout
editor's [Layers](layout-editor.html#layers) list is yours alone and does not change a render.

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
| `--outline-text` | `.svg` only: write every label as glyph outlines instead of live text. Refused on `.png` and `.pdf`. |

**`--outline-text` is for an SVG that leaves circuitRF.** By default an SVG's labels are live text that
names IBM Plex Sans. A browser, vector editor or animation tool that does not have that font installed
quietly substitutes another (usually a serif), with different letter shapes and widths and no warning.
With `--outline-text` each letter is written as the outline of the glyph circuitRF drew, at the same
position, so the file looks identical in any viewer. Nothing else in the file changes. The cost is that
the text can no longer be edited or searched as text, and the file is larger. Text in a face circuitRF
does not ship (a platform font used for a symbol neither shipped font covers) stays as text, with a
warning naming the face. A PDF already embeds its fonts and a PNG has no text, so neither needs it.

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

<h3 id="render-field">A 3D view's field plot</h3>

A 3D setup's model — a `.cem` with a 3D solver, or a `.c3d` — is drawn as a section with
`--section z=35um` (or `xz@y=…`, `yz@x=…`; every length carries a unit) or as an outline with `--iso`.
A `.c3d` also keeps its [field plots](em-3d.html) as records, so a plot can be drawn with no window. A
clip-plane plot **is** a section: it is drawn on its own axis at its own position, with the field under
the model's outlines and its legend beside it.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render cavity/3d/cavity.c3d --list-fields
<span class="output">Field1: |E| · mode 1 · clip z = 12.5 mm · setup 'modes'
  data: ready</span>
<span class="prompt">$ </span>circuitrf render cavity/3d/cavity.c3d -o cut.svg --field Field1
<span class="output">Wrote cut.svg (1600x1200 points, 18,513 bytes)
  XY section at z = 12.5 mm: 1 object(s), 0 port(s)
  Field1: |E| at Mode 1: 8.88028 GHz, Q 3.14E+11, 144 triangles, 0 … 2581 V/m</span></code></pre>

- **It reads what the 3D view reads.** The same run (the workspace's `results/`, where `em` writes), the
  same saved solution **by value**, the same triangles and the same colour range. A plot whose data is
  missing — no run yet, a frequency the run did not save, a quantity it no longer offers — is a
  **refusal** with the sentence the Object Tree shows. It is never drawn at the nearest frequency, and
  an outline is never passed off as the field.
- **A stale run still draws**, as in the window, with a `note:` naming what has changed since — the model, or a file
  the run was solved from (`'Board.clay' has changed since the run at …`).
- **`--phase <degrees>`** picks the instant drawn for a quantity read instantaneously (`Re{E}`); it is a
  refusal on any other. The phase is never written to the file.
- **A hidden plot draws the same.** Hiding only chooses which plot the window draws; `--field` names one.
- **The plot's drive is applied**, and its legend states it, as in the window. There is no flag for it:
  set it on the plot. See [what drive a field is shown at](em-3d.html#field-drive).
- A `--section` that is not the plot's own plane is a refusal naming both.
- **A SAR plot can be drawn as a maximum-intensity projection**: `--mip x|y|z` takes, along each line through the
  volume parallel to that axis, the highest SAR on it, and draws the result on a plane through the middle of the
  tissue — where tissue is hottest, which no single section says. A plot whose own *Maximum-intensity projection*
  box is ticked is drawn that way without the flag. It is refused for any other quantity and together with
  `--section`, and it is never written to the file. See [SAR](sar.html).
- **A surfaces or faces plot is drawn as the 3D view shows it, from a direction you give**, because the
  window's camera is not saved with the plot: `--iso` (the 3D view's *Standard Views ▸ Isometric*),
  `--view-dir top` (or `bottom`, `front`, `back`, `left`, `right`), or `--view-dir x,y,z` — the direction from
  the model toward you, so `1,-1,1` is the isometric. Surfaces hidden behind others are left out, the rest of the
  model is shaded around the field, and the edges are drawn over it. It is written as a `.png` for now. A
  temperature is **mirrored across the document's symmetry planes**, as in the window; `--no-mirror` draws only
  the part that was solved. An EM field on surfaces is drawn on the region selected in the window's tree, so
  name it with `--region` (the refusal lists them). The hottest point is ringed and named in the legend.
- **A temperature plot draws a thermal section.** The colours span the section's true minimum to its maximum
  at the plot's point, as the 3D view ranges a temperature. **Each bond wire is painted from its own solved
  T(s)**: a wire is a one-dimensional element, so its temperature is not in the 3D field around it. Metals are
  outlined rather than filled, since they carry a temperature too; there are no ports, and the thermal
  boundaries (a face held at a temperature, a convection face) are drawn and labelled on the frame.
- **`--labels`** writes each solid's material inside it, where the words fit. **`--tight`** crops the page to
  the section, with the legend inset in its corner (no margin unless `--margin` asks for one). Both are for a
  field plot.
- **`--axes`** adds the 3D view's axis indicator in the bottom-left corner, and **`--scale-bar`** its scale bar
  in the bottom-right, in the 3D view's display unit. They work on any section; on `--iso`, or a surfaces
  plot seen from anything but along an axis, only `--axes` does.
- **PNG and vector differ in one way.** A PNG blends colours across each triangle. SVG has no gradient
  mesh, so in SVG and PDF each triangle takes the colour of its centre. A vector slice of more than
  50,000 triangles merges neighbours that fall in the same step of the colour map, never dropping area.
  The report says `drawn as …` when that happened, and `--no-thin` draws every triangle. `--no-legend`
  leaves the legend off.
- `--json` adds `render.em3d.field`: the plot, its setup, the solution as the file spells it, the run
  directory it read, the triangle counts, the range and whether the run is stale — and for a temperature,
  each wire drawn with its temperature span and where it crosses the plane. `--list-fields --json` returns
  the plots as `fieldPlots`.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render Output/3d/Output.c3d -o wires.png --field "DC 14 A — along wire 4" --labels --tight --axes --scale-bar
<span class="output">Wrote wires.png (1600x811 device-pixels, 299,566 bytes)
  XZ section at y = 75 µm: 9 object(s)
  DC 14 A — along wire 4: T_C at Idc = 14, 4,692 triangles, 85 … 393 °C, 1 wire(s) from their T(s)</span></code></pre>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render "Eight Fingers/3d/Eight Fingers.c3d" -o surface.png --field Surface --iso
<span class="output">Wrote surface.png (1600x1200 device-pixels, 217,677 bytes)
  Temperature on every exposed face, isometric, from +x −y +z, orthographic — setup 'Array', the only point: 2 object(s)
  Surface: T_C at the only point, 98,000 triangles on every exposed face, isometric, from +x −y +z, 85 … 105.1 °C, mirrored across 1 plane(s)</span></code></pre>

<h4 id="render-transparency">A see-through object for one picture</h4>

A `.c3d`'s objects are drawn at the [transparency](drawing-in-3d.html#transparency) each states: a section fills
each one at it, and a surfaces or faces plot shades the model around the field with it. An outline (`--iso`)
fills nothing, so it looks the same whatever an object states. `--transparency` sets one **for this render
only** — the file on disk is never written:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render pkg/3d/pkg.c3d -o cut.svg --section xz@y=0mil --transparency lid=80,U1=50</code></pre>

- Each entry is `name=percent`, a whole number from 0 to 95. The name is an object's, a placed cell's (which
  multiplies onto each of its parts' own), or a group's path (`stage1/match=40`), which sets every member of
  it at every depth. The flag repeats, and takes a comma-separated list.
- An unknown name is a refusal that lists the objects, instances and groups the document has. A value out of
  range, or anything but `name=whole number`, is refused before the file is read.
- It is a `.c3d`'s: on any other document, `.cem` included, it is a refusal naming the kind.

<h4 id="render-realistic">A realistic picture</h4>

`--look realistic` draws a `.c3d` as its [realistic view](drawing-in-3d.html#realistic) looks, as a `.png`, with no window. It is
what Export Picture makes in the realistic view:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render pkg/3d/pkg.c3d -o shot.png --look realistic --iso
<span class="prompt">$ </span>circuitrf render pkg/3d/pkg.c3d -o shot.png --look realistic --look-set Exposure=1 --look-set Environment=Dark</code></pre>

- **The Look is the file's.** The environment, exposure, background, shadows, ground and every Show option are read
  from the `.c3d`'s Look. `--look-set Key=value` changes one key for this picture only, spelled as the file spells it
  (`FieldStyle=Lit`, `Background=#ffffff`, `Shadows=false`, `Exposure=null` for the default). The value is checked as
  the file's is, so `Exposure=20` is refused. The file is never written.
- **The camera.** `--iso` or `--view-dir top|bottom|front|back|left|right|x,y,z` takes the picture orthographically,
  framed on the model. With neither, the camera *Set Camera* saved in the Look is used, perspective
  included. With no camera at all, the render is refused.
- **Quality and background.** `--supersample 1|2|4` (default 2) draws the picture at that many times its size each way
  and brings it down. `--background transparent` leaves out the background and keeps the soft ground shadow's alpha.
  `--size` and `--scale` set the pixels.
- **A field plot.** `--field <plot>` draws a Surfaces or Faces plot on the model in the Look's field style. The picture
  carries the legend and the *Lit Fields* or *Blended Fields* label, as Export Picture's does. A ClipPlane plot is a
  section, so draw it without `--look realistic`.
- **What it refuses.** A `.cem`, an `.svg` or `.pdf` output, and `--section`, `--tight`, `--labels`, `--axes` and
  `--scale-bar`.
- The picture is drawn on the processor, by the same shading the realistic view uses, and the same command always
  writes the same bytes. `circuitrf explain pkg.c3d --look` lists the Look as `render` reads it, the environment it
  will light with, and every appearance with where each value came from.

<h4 id="render-images">Reference images</h4>

A `.c3d`'s [reference images](drawing-in-3d.html#images) — image sheets, and images mapped onto faces — are drawn by
`--iso` exactly as the 3D view's *Copy as Vector* draws them, from the same code: under every line, each clipped to its
sheet or its face, at its transparency. An image sheet is usually not modelled and has no material, so a run leaves it
out; the picture puts it back and frames it whole. An SVG carries each picture as an `<image>`; a PDF stays vector apart
from the pictures. A section (`--section`) and a field plot's context geometry draw none: a plane cut edge-on has no
area. A file that cannot be read is drawn as a checker with a red cross, as the view draws it.

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

**What may I ask for with `--layers`?** The technology defines ten; this document uses three.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Amp/Stage1/layout/Stage1.clay --layers
<span class="output">  layers       PCB 2-Layer FR-4 (70mil, 1oz) — 10 defined, 3 used
    Top Copper           1/0      #c87a3e  solid       purpose=drawing   1 shape(s)
    Bottom Copper        2/0      #8a5028  solid       purpose=drawing   1 shape(s)
    Soldermask Top       3/0      #1e6b3c  solid       purpose=drawing   0 shape(s)
    …
    Silk Top             5/0      #f2f2f2  solid       purpose=drawing   1 shape(s)
    …</span></code></pre>

**How big is it, so I can write a window?** In base SI, with the unit and the scale named.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Amp/Stage1/layout/Stage1.clay --extents
<span class="output">  extents      0 -0.003 .. 0.02 0.00393846  (0.02 x 0.00693846 m, scale 1E-06)
               --window 0um,-3000um,20000um,3938.462um
    Top Copper           0 0 .. 0.02 0.0029   --window 0um,0um,20000um,2900um
    Bottom Copper        0.002 -0.003 .. 0.018 -0.001   --window 2000um,-3000um,18000um,-1000um
    Silk Top             0.0004 0.00336154 .. 0.00331941 0.00393846   --window 400um,3361.538um,3319.414um,3938.462um</span></code></pre>

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

On a workspace, every referenced [vendor library](pdk-integration.html#vendor) is re-read through the
same Validate the Manage Libraries dialog runs. A library that is missing or holds nothing readable is
an error (`check.workspace.vendor-library.missing`), and so is a part placed in one of the workspace's
schematics that the library no longer holds (`check.workspace.vendor-library.part-missing`). Files in
the library that could not be read are a warning (`check.workspace.vendor-library.unreadable`).

<div class="callout note">
<span class="label">Warnings are reported and still exit 0</span>
<p><code>--severity</code> decides the exit code, and it defaults to <code>error</code>. Warnings are
<em>always</em> printed — a check that hid them to keep the exit code clean would make the exit code
useless. Two states are warnings on purpose: a cell folder holding several views with none named
primary, and a layout that resolves no technology. Both are normal.</p>
</div>

On a `.c3d`, a [reference image](drawing-in-3d.html#images) is drawing only, so what is wrong with one is a **warning**,
never an error — a design does not fail `check` because a photo moved: an image file that is missing or does not decode
(`c3d.image.unreadable`, naming the object and the path it resolved to), an image mapped onto a face its object no longer
has (`c3d.image.face-missing` — the record is kept, never dropped), two images on one face (`c3d.image.face-twice`), a
face image whose Width or Height is not positive (`c3d.image.face-size`) and `"Locked"` on a sheet with no image
(`c3d.image.locked-without-image`). An object that is not modelled and has no material raises no *has no material*
warning: only what a run solves is warned of. The [realistic view's `Look`](drawing-in-3d.html#realistic) is display too: an
`Exposure` or `Intensity` out of range (`c3d.look.range`), a `Background` (`c3d.look.background`) or an `Environment`
(`c3d.look.environment`) spelled in no form the view reads is an error, and a `.hdr` that is missing or does not read is a
warning (`c3d.look.hdr-unreadable`): the view lights the scene with Studio instead.

**A DRC violation says where it is.** Each one names the layer as the technology names it, the
region in the layout's own display unit, and, for a spacing rule, the two nets and how far apart they
are against what the rule needs. Two shapes on different nets that touch are reported as touching,
because that is a short rather than a near miss:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf check Filter/layout/Filter.clay
<span class="output">error: Filter.clay: Metal1 Min Spacing (MinSpacing) on Metal1 (1/0) — shapes on nets 'in' and 'sig' touch at (98um, -2um)–(102um, 32um)
error: Filter.clay: Metal1 Min Spacing (MinSpacing) on Metal1 (1/0) — 2um, needs 4um between nets 'in' and 'out' at (-1.728um, 30um)–(101.728um, 32um)</span></code></pre>

Under `--json` the same facts are typed arguments: `layer`, `layerName`, `netA`, `netB`, `touching`,
`measured`, `required`, and the region as `minX`, `minY`, `maxX`, `maxY` in `unit`.

**On a planar `.cem`, the mesh budget is part of the result**, not only a note: the document's row in
`result.check.documents` carries `em` with the kernel, the unknown count, the ceiling it is judged
against (`dense` or `accelerated`) and the mesher's verdict (`ok`, `warn` or `refused`). It survives
`--summary`, so the cheap form of the call still answers whether a run is affordable. A
`PlanarMesh` field the file states but `"Auto": true` discards (cells per wavelength, edge mesh, edge
cells) is a warning, `check.em.mesh-auto-override`, naming the field and the value the mesh uses
instead.

The kind of document is inferred from the path, exactly as `convert` infers a format. A GDSII or
Gerber file is **named as interchange** rather than called unreadable — it is simply not validated,
because there is nothing to validate it against.

**An OASIS file is read.** gdstk opens the whole file, checking its CRC or checksum when it carries one,
and `check` reports its cells, their shape and reference counts, its grid and the layer names it declares
— without importing anything. A damaged file is an **error**, because the import would refuse it too. On
an installation without gdstk the file is still named, with a **warning** saying it was not read.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf check mask.oas
<span class="output">note: mask.oas: OASIS, 4 cell(s) (top: top, empty); 1 polygon(s), 0 path(s), 0 label(s), 4 reference(s); 1000 DBU/µm; no named layers. Import it with `circuitrf convert` or File ▸ Import ▸ OASIS (gdstk)….
1 document(s) checked: 0 error(s), 0 warning(s), 1 note(s).</span></code></pre>

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
                            [--cells [--all]] [--layers] [--extents] [--view &lt;name&gt;]
                            [--footprints] [--setup &lt;name&gt;] [--object &lt;name&gt;] [--look]
                            [--tunables]</code></pre>

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

<h3 id="explain-solved">A 3D view: is each setup solved?</h3>

On a `.c3d`, `explain` ends with a **Solved** section: one line per setup and solver, saying whether its last result still
matches the model, whether the run was partial, when it finished, how long it took, and what has changed since. It is the
same answer the editor's setup cards and marks give ([3D EM ▸ Is this solved?](em-3d.html#solved)). `--json` carries it as
`solved`.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Launch.c3d
<span class="output">…
Solved
  'Palace'   FEM (Palace)    Solved 14:32 (18 min)
  'openEMS'  FDTD (openEMS)  Out of date: the model has changed</span></code></pre>

<h3 id="explain-terminals">A 3D view: a wave port with terminals</h3>

On a `.c3d`, `explain` walks every port the way a run resolves it. A wave port met by several conductors
([EM Setup ▸ Several conductors on one face](em-setup.html#wave-port-terminals)) gets a line for the port —
its face, its reference and why that conductor is the reference — then one per terminal with its number,
conductor, Z0 and voltage path (its ends in the view's display unit), and a last line saying what its
S-parameters are. (Below, the `via` line under each, which says how it was decided, is left out.)

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Pair.c3d
<span class="output">…
  port Left    touches — region: 'gnd', 'strip_a', 'strip_b'. A wave port with 2 terminals on the air box's xmin face; the reference is 'gnd': 'gnd' is the only one in the ground set.
  port Left terminal 1 'P1' conductor 'strip_a', Z0 50 Ω, voltage path (0 µm, 2100 µm, 20 µm) to (0 µm, 2100 µm, 1010 µm)
  port Left terminal 2 'P2' conductor 'strip_b', Z0 50 Ω, voltage path (0 µm, 2900 µm, 20 µm) to (0 µm, 2900 µm, 1010 µm)
  port Left result terminal S: each terminal is a port of the result (ports 1, 2), its voltage and current on its own conductor, against its own Z0</span></code></pre>

`check` reports the same port as one line, and each refusal — a terminal on the reference, two terminals on
one conductor, a number another port already has — names the port and the terminal.

When the setup runs on openEMS (or both solvers), an **openEMS wave ports** block says how openEMS builds
each wave port ([EM Setup ▸ Wave ports](em-setup.html#wave-ports)): per face, the feed's length and what
set it, how far the grid grows past the face, the source plane and the three voltage planes; per terminal,
the source's shape, the two current planes, the current loop and how many cells it clears the nearest
other conductor by; then the run's own notes about that face (its PML, PEC side walls). A port openEMS
cannot build says `a run would stop here:` and why.

When it runs on Palace (or both solvers), a **Palace terminal ports** block gives one line per port with
terminals ([EM Setup ▸ On Palace](em-setup.html#wave-port-terminals-palace)): its terminals as the modes
of one Palace wave port on its face, which entry is Active, the one `MaxSize` every entry shares, and
that a run refuses the face, before its 3D solve, if its modes travel at one speed. A port Palace refuses (three
terminals, a reference plane moved off the face) says `a run would stop here:` and why; on both solvers it
says Palace will be skipped.

<h3 id="explain-object">A 3D view: <code>--object</code> — how does this object look?</h3>

On a `.c3d`, `--object <name>` walks the [appearance](drawing-in-3d.html#appearance) the realistic view draws that object
with: one line per field, each saying which statement decided it — the object's own, an instance's (innermost first),
its material's, the material's colour, or the role's default. The name is a top-level object's, which answers for every
solid it elaborates to, or an elaborated one (`U1/trace`). Nothing in it is read from εr or σ: an appearance is drawing
only. `--look` lists the Look itself and every appearance in the view the same way.

<h3 id="explain-analysis"><code>--analysis</code> — which chain would run</h3>

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

An envelope analysis also lists what its run will record: the labeled nets whose voltages it keeps,
and the IProbes it measures power at (see
[What a run records](simulations.html#envelope-records)):

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain NR20_ACPR.cnl --analysis
<span class="output">  analyses:
    ENV1             EnvelopeAnalysis   ENV1                         DISPATCHED by env
      records voltages at 3 net(s): Vin, Vout, VDD
      measures power at 3 IProbe(s): Iout, Iin, IDC</span></code></pre>

`circuitrf env --probes a,b` still runs: the nodes it names join the labeled nets, and the run notes
that `Probes` is no longer a setting. A netlist line with `Probes=` reads the same way, and a file
saved again no longer carries it.

A design whose expressions call a distribution — a kit's statistical models, or a variable such as
`Rsh = agauss(50, 2.5, 1)` — also lists each one, as a **process** draw (in a global, shared by every
instance) or a **mismatch** draw (in a cell, one per instance), with the stream that names it:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain mc.cnl --analysis
<span class="output">Distributions: 1 process, 2 mismatch — nominal in every run but a Monte Carlo trial
  process  agauss Rnom
  mismatch agauss X1.R
  mismatch agauss X2.R</span></code></pre>

<h3 id="explain-touchstone"><code>explain</code> on a Touchstone file — what IS this part?</h3>

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

<h3 id="explain-expr"><code>--expr</code> — evaluate in the design's scope</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain pa.cnl --expr "Zopt*2" --set Zopt=12.5
<span class="output">Zopt*2 = 25   (real)</span></code></pre>

Through the one expression engine, in the design's resolved scope — never by substitution — with
`--set` applied first exactly as a run verb applies it. The kind is reported, never coerced.

<h3 id="explain-ref"><code>--ref</code> — where does this reference land</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Stage1.csch --ref ../parts/SOT-23-3
<span class="output">../parts/SOT-23-3 → /home/you/designs/parts/SOT-23-3   resolved
                     outside this workspace: no</span></code></pre>

Where resolution fails, **that is the answer** — a sentence naming what was looked for and where it
was looked. You are usually running this verb precisely because something did not resolve.

A [vendor part](pdk-integration.html#vendor-placed) reference reports its walk a step at a time: which
`.cws`, which library entry and what it stores, and the folder that resolved. A library the workspace
does not name and a library whose folder is missing say different things.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Stage1.csch --ref vendorlib://Caps/capacitors/series-a.lib
<span class="output">  reference    'vendorlib://Caps/capacitors/series-a.lib' from /home/you/designs/Amp/Stage1/schematic
               resolved → /home/you/libraries/caps/capacitors/series-a.lib  (outside its workspace)
               via workspace /home/you/designs/Amp/.cws
               via library 'Caps' → '/home/you/libraries/caps'
               via folder /home/you/libraries/caps</span></code></pre>

<h3 id="explain-cells"><code>--cells</code> — what cells are in here?</h3>

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

<h3 id="explain-layers"><code>--layers</code> — what may I ask for?</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Stage1.clay --layers
<span class="output">  technology   /home/you/designs/Amp/tech/pcb-2layer_FR-4_70mil_1oz.ctech
               via the workspace's DefaultTechRef — the layout states none
  layers       PCB 2-Layer FR-4 (70mil, 1oz) — 10 defined, 3 used
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

<h3 id="explain-footprints"><code>--footprints</code> — what artwork does each part state?</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Board1/schematic/Board1.csch --footprints
<span class="output">  footprints: 3
    C1             smt:0402@N             builtin   2 pad(s) / 2 port(s)
                   via the built-in case table, because the value starts with 'smt:' — generated on demand, not read from a file
                   → 0402 (metric 1005)   1.00 x 0.50 mm, density N (nominal) — generated
                   against technology PCB 2-Layer FR-4 (70mil, 1oz)
    U1             ../../Widget9          cell      9 pad(s) / 9 port(s)
                   via a path, resolved against the document's own folder
                   → /work/Board/Widget9/layout/Widget9.clay
    S4P1           smt:0402@N             builtin   2 pad(s) / 4 port(s)   MISMATCH
                   via the built-in case table, because the value starts with 'smt:' — generated on demand, not read from a file</span></code></pre>

**What it states, what that resolved to, and how it got there.** A footprint resolves one of two
ways, decided by the first four characters: `smt:` is a built-in case size, which does not exist as a
file and is *generated on demand*; anything else is a path, resolved against the schematic's own
folder exactly as a cell reference is. Which of the two produced the answer is printed, because that
is the half you cannot work out from the result.

**The technology is named for a built-in and only for a built-in.** A generated land pattern picks its
copper, soldermask and silkscreen *by role* out of whatever technology is in force — and the shipped
technologies disagree about every layer key, so which technology that is is part of what the artwork
will be. A cell you imported or drew already has its artwork on disk on keys of its own, and printing
a technology beside it would suggest it was about to be re-resolved.

**Pads and ports are on the same line, and a mismatch says so.** A land pattern with two pads under a
four-port part is a design error that Update Layout reports and refuses to place; two numbers in two
places is how that goes unread.

<div class="callout note">
<span class="label">It reads the schematic, not the netlist</span>
<p><code>Footprint</code> is artwork, not a value, and it is dropped before parameters are resolved —
it never reaches the simulator. Asking the netlist about it would report every design as stating
none.</p>
</div>


<h3 id="explain-tunables"><code>--tunables</code> — which values can be tuned?</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Bench/schematic/Bench.csch --tunables
<span class="output">Tunables (9):
  R1.R         top       50 Ohm          10 Ohm .. 200 Ohm  [tune; opt]
  X1.Rbias     top       1 kOhm          500 Ohm .. 2 kOhm  [tune]
  Rload        top       50 Ohm          25 Ohm .. 100 Ohm  [opt]
  real(Zsrc)   top       40 Ohm          20 Ohm .. 80 Ohm  [part of 40+15j Ohm]
  imag(Zsrc)   top       15 Ohm          7.5 Ohm .. 30 Ohm  [part of 40+15j Ohm]
  mag(Zsrc)    top       42.72 Ohm       20 Ohm .. 80 Ohm  [tune; part of 40+15j Ohm]
  phase(Zsrc)  top       20.556 deg      -45 deg .. 45 deg  [tune; part of 40+15j Ohm]
  DUT:R3.R     DUT · ×2  10 Ohm          5 Ohm .. 50 Ohm
  DUT:R5.R     DUT · ×2  22 Ohm          11 Ohm .. 44 Ohm  [read-only: it belongs to another workspace]</span></code></pre>

Every value of the design that can be tuned or optimized, at any depth, with the **key** a `tune` line
or a preset names it by. A value is tunable when it is written as a plain number with an optional
unit; an expression is not, and the variable it reads is. A value inside a cell is named
`Cell:Instance.Parameter` and moves in **every** instance of that cell — the second column says how
many share it. A sub-circuit instance's own parameters are listed once per instance (`X1.Rbias`), even
when the instance is using the cell's default. A complex value written with numbers only is listed as
its four parts — `real(Zsrc)`, `imag(Zsrc)`, `mag(Zsrc)` and `phase(Zsrc)`, the phase in degrees — each
tuned on its own; a preset still holds the whole value under its own key (`Zsrc=45+10j Ohm`).

A placed [vendor part](pdk-integration.html#vendor-optimize) is one key, `C1.Part`, shown as its part
number, value and description, with a second line giving how its candidates are chosen (`kind=same`, or
`parts="…"` and any value bounds) and how many there are, their value span and series. The `--json`
row carries the same as `part`. Its own subcircuit's values are not listed: the part is chosen whole.

The range is the one the schematic's tuning setup gives the value, or the range it would get when you
first tune it. A value Push cannot write — in a library cell, or another workspace — says why; it can
still be tuned and optimized. Keys the setup names that match nothing in the design are listed last,
and ignored.

<h3 id="explain-extents"><code>--extents</code> — how big is it?</h3>

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf explain Stage1.clay --extents
<span class="output">  extents      0 -0.003 .. 0.02 0.00393846  (0.02 x 0.00693846 m, scale 1E-06)
               --window 0um,-3000um,20000um,3938.462um
    Top Copper           0 0 .. 0.02 0.0029   --window 0um,0um,20000um,2900um
    Bottom Copper        0.002 -0.003 .. 0.018 -0.001   --window 2000um,-3000um,18000um,-1000um
    Silk Top             0.0004 0.00336154 .. 0.00331941 0.00393846   --window 400um,3361.538um,3319.414um,3938.462um</span></code></pre>

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

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf netlist &lt;path.csch | cell-folder | workspace --cell N&gt; [-o out.cnl]
<span class="prompt">$ </span>circuitrf netlist &lt;path.cnl&gt; --to-schematic [-o out.csch]</code></pre>

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
refused too, unless you ask for [a drawing of it](#netlist-to-schematic): passing it through the
reader and the writer would hand back a file that is not the one you gave (comments gone, directives
reordered) and call it an extraction.

### Drawing a netlist: `--to-schematic` {#netlist-to-schematic}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf netlist filter.cnl --to-schematic -o filter.csch
<span class="output">filter.csch</span></code></pre>

The other direction: a `.cnl` you wrote becomes a schematic you can open, review and edit. The
drawing puts the signal path left to right, from port 1 to port 2, with the ports at its ends. Each
element hanging off a net on that path (a shunt capacitor, a termination, a port) drops below the
net. An element bridging two nets on the path sits above it. Everything else goes in rows underneath,
near what it connects to. Every terminal on net `0` gets its own ground symbol, every wire is
horizontal or vertical, and **every net carries a label with the name the netlist gave it**. Globals
go in one VAR block and measurements in one MEAS block, above the circuit. The analyses go into the
schematic as they were.

<div class="callout">
<span class="label">The drawing is the netlist</span>
<p>It extracts back, through the same extraction <b>Simulate</b> performs, to the same instances, the
same nets in the same order, and the same parameter values. The one thing a drawing may add is a port
count a variadic symbol needs to draw its pins (<code>NumPorts</code> on an SDD or a Z-port), when the
line left it to be inferred from its nets. Run <code>circuitrf netlist filter.csch</code> on the
result to see for yourself.</p>
</div>

**A connection the router cannot draw without crossing something is made by net label** — a real
connection, so the circuit is unchanged, and the verb names the nets it did that for. **A netlist it
cannot draw is refused, with every cause named and nothing written:** one that defines cells (a
hierarchy needs a cell folder per definition), an instance whose type has no schematic symbol, or a
wirebond, whose pins come from the design file it references. User functions and directives the
reader keeps verbatim have nowhere to go in a schematic. They are left out, and the verb says so.

A Touchstone `File` reference is written relative to where the drawing is saved, so it still finds
the same file. Without `-o` the drawing goes to stdout with its references relative to the netlist's
own folder, which is right for `circuitrf netlist filter.cnl --to-schematic > filter.csch`.

---

## `plot` — a picture of a result {#plot}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf plot &lt;result.npy|.sNp&gt; -o &lt;out.svg|.pdf|.png&gt; --trace &lt;spec&gt; [--trace &lt;spec&gt;]…</code></pre>

One plot, one axis pair, without writing a data display first.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf plot lc.s2p -o match.png \
<span class="prompt">    </span>--trace cube=S,i=1,j=1,y=db20 --trace cube=S,i=2,j=1,y=db20 \
<span class="prompt">    </span>--title "LC lowpass" --ylabel dB --x 1:5 --y -40:5
<span class="output">Wrote match.png (792x612 device-pixels, 19,785 bytes)
  1 plot(s) on 1 page(s), 1 data source(s)</span></code></pre>

**A trace spec is comma-separated `key=value`:**

| Key | What it means |
|---|---|
| `cube` | Which cube. Required. It is the same shorthand the trace card's spec box takes, so `S`, `S[:,2,1]`, `Pout` and `mag(V[:,"X1.drain"])` all work, and so does [plotting against another quantity](data-display.html#versus): `cube=Gain[~,:] vs Pout_dBm` draws gain against output power, one curve per value of the outer sweep. An X side that does not resolve is refused (`plot.trace.unresolved`). |
| `i`, `j` | The **port numbers** of a matrix cube — `i=2,j=1` is S21. Refused together with a bracketed slice: they are the convenience over writing one. |
| `y` | `db20` (20·log₁₀ — S-parameters, voltages, currents, a probe margin), `db10` (10·log₁₀ — powers), `mag`, `phase`, `real`, `imag` or `conj`. A bare `db` is refused as ambiguous (`plot.trace.db-ambiguous`): in a data display it means 10·log₁₀, while a measurement's `dB()` is 20·log₁₀, and the wrong one draws an S-parameter at half its depth. |
| `axis` | `left` (the default) or `right`. |
| `label` | The trace's own name, quoted when it holds a comma: `label="As built, rev B"`. It replaces the generated name in the legend, the axis title and the marker box, exactly as the trace card's Label box does, and `--write-cdd` writes it. |
| `cut` | An antenna pattern cut: a bearing in degrees pins the cube's `phi` axis and sweeps `theta`; `all` keeps every `phi` as a curve family. The verb prints the φ it landed on. |
| `port` | The **port number** on a cube's `port` axis — not an index. A port the run does not hold is refused, listing the ones it does. |
| `freq` | Pins a `freq` axis to its nearest sample. Takes an SI suffix: `2.45G`. |
| `probe` | A [WSProbe](wsprobe.html) label. Given, it turns a `cube=<analysis>.wsp` trace into a probe metric. |
| `metric` | Which of the reference document's quantities — `H0`, `1/Y0`, `ZG`, `SM_Y0`, `LGa`, `SMenv`, … See [The WSProbe](wsprobe.html#metrics). Some take `with=`, `set=`, `z0=`, `side=`, `gi=` or the envelope's grid keys. |

```text
circuitrf plot amp.npy -o margin.svg --trace cube=SP1.wsp,probe=GATE,metric=SM_Y0,y=db20
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
| `--outline-text` | `.svg` only: labels as glyph outlines, so no viewer substitutes a font. As [`render`](#render). |
| `--radial linear\|db` | How a **polar** plot's radius is read. `db` makes it an antenna pattern plot. Refused on any other `--type`. |
| `--db-floor <dB>` | The centre of a `db` plot, **relative to the outer ring**. Default −40. |
| `--db-ring <dB>` | Ring spacing. Default 10. |
| `--db-ref peak\|<dB>` | The outer ring: the data's own peak (normalised, the default) or an absolute level. |
| `--db-unit <text>` | What the radial numbers are in — `dBi`, `dB(W/sr)`. |
| `--trace …,ref=<dBm>` | Read a dBm **level** (`TrpDbm`, `PeakEirpDbm`) against this conducted input power instead of the one the run published. An exact dB shift — no re-run. Inert on anything that is not a level. |
| `--whole-plane` | Draw each `cut=` as **one** trace spanning −θ<sub>max</sub> … +θ<sub>max</sub>, by fetching the φ + 180° half alongside it — instead of the default two traces per cut. Needs `--type polar --radial db`. |
| `--angle-labels` | Print the bearing every 30° outside the disc, with a spoke to each, the way an antenna-range plot is drawn. Polar only, either radial mode. The disc shrinks to make room rather than the numbers overprinting the outer ring. |
| `--write-cdd <path>` | Also write the data display this drew. |

```text
circuitrf plot run.npy -o eplane.svg --type polar --radial db --db-unit "dB(W/sr)" \
  --trace cube=farfield.U,cut=0,port=1,y=db10

# both principal planes, one trace each, with bearings around the rim
circuitrf plot run.npy -o cuts.svg --type polar --radial db --whole-plane --angle-labels \
  --trace cube=farfield.U,freq=5.8e9,cut=0,port=1,y=db10 \
  --trace cube=farfield.U,freq=5.8e9,cut=90,port=1,y=db10
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

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf find &lt;root&gt; [--depth n] [--no-analyses]
<span class="prompt">$ </span>circuitrf find &lt;workspace&gt; --parts "&lt;query&gt;" [--library &lt;name&gt;] [--limit 50]</code></pre>

The workspaces under a directory, their cells, each cell's views, and the analyses each cell declares.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf find ./projects
<span class="output">/home/you/projects  (1 workspace(s), 2 cell(s), depth 4)
  Amp  /home/you/projects/Amp  [tech tech/generic-mmic.ctech]
    LC                       schematic: LC.csch
                             analyses: SP1
    Stage1                   schematic: Stage1.csch, layout: Stage1.clay
                             analyses: HB1, SWEEP1</span></code></pre>

A cell with a 3D view also gets a `solved:` line: each setup's state (`current`, `outOfDate` or `notRun`), and
`(partial)` where the run was cancelled, interrupted or did not converge. `explain` has the detail.

It reads what the other verbs read, so a cell listed here is the cell `render` would draw and the
analyses are the ones `hb` would dispatch. It writes nothing, so it runs on a read-only tree and on a
workspace the application has open.

| Option | What it does |
|---|---|
| `--depth n` | How many levels below the root a workspace is looked for. Default 4, at most 12. |
| `--no-analyses` | Skip the analyses. Each one costs an extraction, which adds up on a large tree. |

<h3 id="find-parts">Searching vendor libraries: <code>--parts</code></h3>

With `--parts`, `find` searches the workspace's [vendor part libraries](pdk-integration.html#vendor)
instead of listing documents. Words are separated by spaces and every word must match. A word matches a
part when it appears in the part's name, its description (its unit brackets dropped, so `50V` finds
`50[V]`), its file or its folder. A word that reads as a value (`10p`, `10pF`, `10 pF`, `2.2n`, `4n7`) matches
the part's stated value within 0.5 % instead, and a unit, when given, must be the part's (`10nH`
finds inductors only). A case size matches the stated size exactly: `0603` the imperial code, `1608M`
the metric one. The palette's search box and the Parameter Editor's Part field use the same rules.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf find ~/designs/Amp --parts "10p C0G"
<span class="output">Caps	TSTC0402A0018	10 pF	1005M(0402)/C0G(-55~125degC)/10[pF]/50[V]	100 MHz – 30 GHz	File=vendorlib://Caps/capacitors/series-a.lib Name=TSTC0402A0018 Glyph=C Footprint=smt:0402@N</span></code></pre>

Each row ends with **the instance parameters to write** for a SpiceModel component in a `.csch`, so a
part found here can be placed without another question. They are the parameters the palette writes
when the part is dropped, `Footprint` included when the workspace's technology is a board and the part
states its case size in both systems. Rows are sorted by library, then by value.
At most `--limit` rows are printed (50 by default), and a note says how many matched in all.

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
  tuning              3.8 kB  Tuning directives
  goals               2.7 kB  Optimization goals
  statistics          8.7 kB  Monte Carlo, yield and corners
  components         91.3 kB  Component types

  circuitrf reference components &lt;TYPE&gt;   one primitive
  circuitrf reference analyses &lt;TYPE&gt;     one analysis directive</span></code></pre>

These are the same pages you are reading now, shipped inside the program so they are there on a
machine that has no copy of this site. `components` is the **generated catalogue** — read from the
live component registry every time you ask, so it cannot go stale — while `component-notes` is this
site's [Components](components.html) page, which explains what each part is *for*. The catalogue
answers "what may I write"; the page answers "what does it mean". Ask for both if you want both.

The list above is abridged; the program prints every topic. Three more generated ones answer the
questions a design starts with:

| Topic | What it lists | Name one |
|---|---|---|
| `component-index` | every type token in one line — its nets, its category and what it is. A few kB, against the full catalogue's ~100 kB: read it first, then `components <TYPE>` | — |
| `technologies` | every technology that ships, by the id `new workspace --tech` takes, with its stackup top to bottom, the layers drawn on each entry and the material libraries it names | `technologies <id>` |
| `shipped-materials` | every material in the libraries that ship — role, εr and tanδ or σ₂₀ and α₂₀, thermal conductivity and source — and how a technology uses one | — |

Like `components`, all three are generated from the data they describe every time you ask: the
technologies are read from the embedded `.ctech` files through the same reader as your own, and the
materials from the embedded `.cmat` libraries.

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

`--summary` changes the diagnostics only, never the `result`. Anything a caller needs as a number, such
as `check`'s mesh budget on a `.cem`, is in the result and is the same with or without it.

---

## `serve` — the MCP server {#serve}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf serve --root &lt;dir&gt; [--kits &lt;dir&gt;] [--print-config]</code></pre>

**This is circuitRF as an MCP server.** It speaks the **Model Context Protocol** over its standard
input and output — the stdio transport, newline-delimited JSON-RPC 2.0, with `initialize`,
`tools/list` and `tools/call` — so any MCP client can discover what circuitRF can do and ask it to
do it. Protocol versions `2025-06-18`, `2025-03-26` and `2024-11-05` are accepted.

In practice that means an assistant, a design agent, a CI job or an automation harness can run a
simulation, check a design, create a workspace or import a part without a human at a terminal. It is
started by that program, not by you, and it ends when that program disconnects.

<div class="callout note">
<span class="label">Point an MCP client at it</span>
<p>A client is configured with a command and its arguments. The command is the installed circuitRF
executable (<a href="#invoking">where it is</a>), the arguments are <code>serve --root &lt;dir&gt;</code>,
and <code>--root</code> is the only directory tree the server will read or write, and a path escaping
it is refused rather than clamped (see <em>What it will not do</em> below). Nothing else about the
host matters, because the transport is the process's own stdin and stdout.</p>
</div>

Most clients take the same shape of configuration — a server name, a command and its arguments:

```json
{
  "mcpServers": {
    "circuitrf": {
      "command": "/Applications/circuitRF.app/Contents/MacOS/circuitRF",
      "args": ["serve", "--root", "/Users/you/designs"]
    }
  }
}
```

Use the full path to the executable rather than relying on the client's `PATH`, which is often not
your shell's. On Windows it is `%LOCALAPPDATA%\Programs\circuitRF\circuitRF.exe` or
`C:\Program Files\circuitRF\circuitRF.exe`; on Linux, `/usr/bin/circuitrf` or
`~/.local/bin/circuitrf`. With Claude Code it is one line:

<pre><code class="cmd"><span class="prompt">$ </span>claude mcp add circuitrf -- /Applications/circuitRF.app/Contents/MacOS/circuitRF serve --root ~/designs</code></pre>

**Or let circuitRF write the configuration for you.** `--print-config` starts nothing: it prints the
`mcpServers` JSON for the executable you ran it with, with the root (and any `--kits` folders) made
absolute, and exits. The JSON goes to stdout, so it can be redirected into a file; the matching
`claude mcp add` line goes to stderr, ready to paste.

<pre><code class="cmd"><span class="prompt">$ </span>/Applications/circuitRF.app/Contents/MacOS/circuitRF serve --root ~/designs --print-config</code></pre>

The root is checked before anything is printed, so a configuration naming a folder that does not
exist is refused rather than written.

**Choose the root deliberately.** It is everything the server can read and write, with your own
authority. Point it at the folder your designs live in, such as `~/designs`, not at your home
folder or a whole drive: an agent working inside `~/designs` needs nothing outside it, and a root of
`~` lets every tool read and write anywhere in your home folder.

**From a source build**, the command is `dotnet` and the first argument is the built
`CircuitRF.Cli.dll`. Build it once, then point the client at the build output rather than at
`dotnet run`. `dotnet run` builds before every start, so the server is slow to come up and a client
may give up waiting for it:

<pre><code class="cmd"><span class="prompt">$ </span>dotnet build src/Cli -c Release
<span class="prompt">$ </span>dotnet src/Cli/bin/Release/net10.0/CircuitRF.Cli.dll serve --root ~/designs --print-config</code></pre>

Rebuild after pulling changes; the client keeps running whatever was built last.

What to tell an agent once it is connected, which reference topics it should read, and the design
flow that works are in [Designing with an AI agent](ai-agents.html).

**Most clients load a server when a session starts**, so one registered mid-session appears in the
next one. Nothing is lost meanwhile: every tool is a verb, and the same verbs answer from a shell with
the same documents.

**Sixteen tools, and each is a verb you already have:**

| Tool | Runs |
|---|---|
| `run` | `sparam`, `dc`, `hb`, `lp`, `lpp` or `em`, chosen by an argument |
| `check` | `check` |
| `explain` | `explain`, including `--cells`, `--layers`, `--extents` and `--footprints` |
| `create` | `new workspace` or `new cell` |
| `import` | `import part` — a component, as a cell; `import schematic` (`what=schematic`) — a schematic design, as a cell |
| `convert` | `convert` — artwork between formats, in either direction, so exporting a layout is a `convert` |
| `render` | `render` — one tool over every document kind, as the verb is |
| `read` | `read` |
| `netlist` | `netlist` — the extraction a schematic runs as |
| `plot` | `plot` — one picture from one result file. It takes `attachImage` too |
| `find` | `find` — the workspaces, cells, views and analyses under a directory |
| `lvs` | [`lvs`](#lvs) — does a cell's artwork implement its schematic |
| `impedance` | [`impedance`](#impedance) — every trace on a layout against a target Z0, and its return path |
| `review` | [`review`](#review) — the Layout Review's checks over a board |
| `model` | [`model`](#model) — the Model Composer's nouns, chosen by `noun` |
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

**A server outlives an update, and says so when it cannot.** A `serve` that is still running when
the application updates itself is running out of an installation that has changed underneath it —
and a running program cannot load code it had not already loaded from files that have since been
replaced. So before each call the server checks that its own executable is still the one it started
from. If not, that call is answered with a JSON-RPC error, code `-32001`, saying so, and the server
exits. Start it again with the same command and it runs the updated version.

`serve` is the one verb whose stdout is not the result: it carries the protocol, so everything else —
progress, notes, warnings, device-worker logs — goes to stderr, where the program that started it
picks it up. For that reason it takes no `--json` of its own; every call through it already returns
one.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf serve --root ~/designs 2&gt; serve.log</code></pre>

## Installing for an agent {#agent-install}

An agent — or any unattended script — can install circuitRF and start driving it with no person at
the keyboard. Five steps:

**1. Find the release and pick the asset.** Asset names carry the version, and every circuitRF
release so far is a *pre*release, which GitHub's "latest release" link and a tagless
`gh release download` both skip. **There is no fixed download URL**, so look the newest tag up
first:

<pre><code class="cmd"><span class="prompt">$ </span>TAG=$(gh release list --repo potatobeanradio/circuitRF --limit 1 --json tagName -q '.[0].tagName')
<span class="prompt">$ </span>gh release download "$TAG" --repo potatobeanradio/circuitRF --pattern 'circuitRF-*-arm64.dmg'</code></pre>

Without `gh`, the same list is `https://api.github.com/repos/potatobeanradio/circuitRF/releases`,
newest first. A release that carries `update-manifest.json` lists every asset in it with its name,
download URL, size and SHA-256, which is the one file to read to choose and to verify a download.

| Platform | Asset |
|---|---|
| Windows | `circuitRF-<version>-win-x64-user.msi` — per-user, no administrator (also `arm64`, `x86`) |
| macOS | `circuitRF-<version>-arm64.dmg` (Apple Silicon) or `circuitRF-<version>-x64.dmg` (Intel) |
| Linux | `circuitRF-<version>-linux-x64.tar.gz` or `…-linux-arm64.tar.gz` |

**2. Install it without a prompt.**

<pre><code class="cmd"><span class="prompt">&gt; </span>msiexec /i circuitRF-&lt;version&gt;-win-x64-user.msi /qn</code></pre>

<pre><code class="cmd"><span class="prompt">$ </span>hdiutil attach -nobrowse -mountpoint /tmp/circuitrf-dmg circuitRF-&lt;version&gt;-arm64.dmg
<span class="prompt">$ </span>cp -R /tmp/circuitrf-dmg/circuitRF.app /Applications/
<span class="prompt">$ </span>hdiutil detach /tmp/circuitrf-dmg</code></pre>

<pre><code class="cmd"><span class="prompt">$ </span>tar -xzf circuitRF-&lt;version&gt;-linux-x64.tar.gz
<span class="prompt">$ </span>./circuitRF-&lt;version&gt;/install.sh</code></pre>

The Windows install adds circuitRF to the user's `PATH`, but only processes started afterwards see
it — so in the session that installed it, use the full path,
`%LOCALAPPDATA%\Programs\circuitRF\circuitRF.exe`. The Linux one links `~/.local/bin/circuitrf`.

**3. Check that it answers:**

<pre><code class="cmd"><span class="prompt">$ </span>/Applications/circuitRF.app/Contents/MacOS/circuitRF --version</code></pre>

It prints the release's version — the tag from step 1 — and exits `0`, opening no window. Anything else means the path is wrong.

**4. Register the server** with the client: the command is that same executable, the arguments are
`serve --root <dir>` ([above](#serve)). `serve --root <dir> --print-config` prints exactly that
configuration, with the executable's full path filled in.

**5. Keep working in the meantime.** Most clients load a newly registered server only when their
next session starts, so the tools will not appear in the session that did the install. Nothing is
lost: every MCP tool is a verb returning the same JSON document, so the same work can be driven from
the shell right away — `circuitrf check <path> --json` returns byte for byte what the `check` tool
would.

---

## Exit codes {#exit}

| Code | Meaning |
|---|---|
| **0** | Ran, and produced something usable |
| **1** | Could not run — bad arguments, a missing file, no matching analysis, a refusal, an exception |
| **2** | Ran, but did not converge |
| **3** | Finished, but a check failed: an `opt` goal unmet, a `yield` below its target, a `model check-charge` above its threshold or a `model validate` tolerance missed |
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

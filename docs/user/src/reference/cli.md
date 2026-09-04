---
title: The Command Line
slug: reference/cli.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > The command line
lede: Every engine circuitRF has runs without the GUI. One executable, eight verbs — S-parameters, DC, harmonic balance, loadpull, loadpull pursuit, electromagnetic extraction, layout interchange conversion, and an elaborated-netlist dump. This chapter is the operational reference for all of them, including a worked EM run from an empty folder.
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
<li><a href="#elab"><code>elab</code> — the elaborated netlist</a></li>
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
| `sparam` | `.cnl` | The linear S-parameter engine over a frequency sweep | A Touchstone `.sNp`, always |
| `dc` | `.cnl` | The nonlinear DC engine | Node voltages and probe currents, to stdout |
| `hb` | `.cnl` | Harmonic balance, single- or multi-tone | Spectra tables to stdout; `-o .mat/.npy/.txt` |
| `lp` | `.cnl` | Loadpull over the directive's Γ grid | A per-Γ-point table; `-o .mat/.npy/.txt/.spl/.lpcwave` |
| `lpp` | `.cnl` | Loadpull **pursuit** — searches for the optima | Optima + the follow-on grid; `-o` as `hb`; `--out-grid` writes a `.gam` |
| `em` | `.cem` | The EM kernel the setup resolves to | A Touchstone `.sNp` **and** a grouped `.npy`, where **Simulate** writes them |
| `convert` | any layout format | The same importer and exporter **File ▸ Import/Export** runs | The layout in the format you asked for |
| `elab` | `.cnl` | Elaboration only, no analysis | The elaborated netlist, to stdout |

`hb`, `lp` and `lpp` all run **the whole parametric sweep** when one wraps the analysis — see
[naming the wrapper](#wrapper).

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

Frequencies are written as `1GHz`, `100MHz`, or bare Hz (`1e9`) anywhere a frequency is accepted.

---

## `sparam` — S-parameters {#sparam}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf sparam &lt;file.cnl&gt; [--freq start:stop:step] [-o out.sNp]</code></pre>

```text
$ circuitrf sparam hero1.cnl --freq 1GHz:3GHz:1GHz -o hero1.s2p
S-parameter analysis: 3 points, 1–3 GHz
Wrote hero1.s2p
```

| Option | What it does |
|---|---|
| `--freq start:stop:step` | Override the sweep. **Omit it and the netlist's own `sparam` analysis is used**, segments and all — which is almost always what you want, because it is the sweep the design was set up with. |
| `-o`, `--output <path>` | Where the Touchstone goes. Omitted, it is the input file with its extension changed to `.sNp` for the port count found. |

`sparam` **always** writes a Touchstone; there is no stdout table. The port count in the extension
comes from the network, so a circuit that grew a port writes `.s3p` without you editing the command.

## `dc` — the operating point {#dc}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf dc &lt;file.cnl&gt;</code></pre>

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

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf hb &lt;file.cnl&gt; [-a name] [--set var=expr] [-o out.npy]</code></pre>

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

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf lp &lt;file.cnl&gt; [--grid grid.gam] [--pin start:step:max] [-o out.spl]</code></pre>

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

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf lpp &lt;file.cnl&gt; [--out-grid found.gam] [-o out.npy]</code></pre>

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

One cell out of a GDSII library that holds many:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf convert lib.gds --list-cells
<span class="prompt">$ </span>circuitrf convert lib.gds -o coupler.dxf --cell COUPLER</code></pre>

Convert a directory of drawings in one line:

<pre><code class="cmd"><span class="prompt">$ </span>for f in dxf/*.dxf; do circuitrf convert "$f" -o "gds/$(basename "${f%.dxf}").gds"; done</code></pre>

### Options {#convert-options}

| Option | What it does |
|---|---|
| `-o, --output <path>` | The file to write — or the **folder**, for `gerber` and `clay`. Required. |
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

## `elab` — the elaborated netlist {#elab}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf elab &lt;file.cnl&gt;</code></pre>

Elaborates and stops: flattens the hierarchy, resolves every parameter and expression top-down, and
numbers the nodes, then prints [the elaborated netlist](netlist.html) — the exact thing the engines
consume. No analysis runs.

This is the debugging verb. When a value is not what you expected, `elab` is where you find out
whether the expression resolved to something different from what you meant, or resolved correctly and
the analysis is doing something else.

## Exit codes {#exit}

| Code | Meaning |
|---|---|
| **0** | Ran, and produced something usable |
| **1** | Could not run — bad arguments, a missing file, no matching analysis, a refusal, an exception |
| **2** | Ran, but did not converge |
| **130** | Stopped — `em` only, and only when the run was cancelled at a work boundary |

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

**Re-extract every EM setup in a workspace** after a technology edit — the layout and stackup
references resolve themselves, so the loop needs nothing but the file names:

<pre><code class="cmd"><span class="prompt">$ </span>for f in em/*.cem; do circuitrf em "$f" || exit 1; done</code></pre>

Because each of those writes the same file **Simulate** writes, a schematic that references the
extracted Touchstones picks the new results up with no further action.

---

<p class="small">See also: <a href="simulations.html">Simulations</a> (what each analysis computes) ·
  <a href="netlist.html">The netlist format</a> · <a href="em-setup.html">EM Setup</a> ·
  <a href="mom-engine.html">The MoM engine</a> ·
  <a href="npy-export.html">Results &amp; data export</a> ·
  <a href="pdk-integration.html">Kits and external device models</a>.</p>

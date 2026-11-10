---
title: Designing with an AI agent
slug: reference/ai-agents.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Designing with an AI agent
lede: Connect an AI agent to circuitRF's MCP server, tell it what to build, and know where it will need your help. What to set up, what to say, which reference topics it should read, and the design flow that works.
keywords: AI agent, assistant, MCP, Claude Code, automation, headless design, reference topics, design flow, prompt
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What an agent can do in circuitRF</a></li>
<li><a href="#setup">Connecting the agent</a></li>
<li><a href="#brief">What to tell it</a></li>
<li><a href="#topics">The reference topics it should read</a></li>
<li><a href="#flow">The design flow that works</a></li>
<li><a href="#model">Building a device model from data</a></li>
<li><a href="#em">EM runs: check the cost first</a></li>
<li><a href="#traps">Where agents go wrong</a></li>
<li><a href="#safety">Keeping its work reversible</a></li>
</ol>
</nav>

## What an agent can do in circuitRF {#what}

circuitRF runs as an **MCP server**. An agent connected to it can create workspaces and cells,
write designs, check them, simulate them, render them and read the results back. It can also
export artwork and import parts. All of this is available without the window, and each tool returns
the same JSON document as the matching [command-line verb](cli.html).

**The agent works by writing files.** There are no tools for placing one component or setting
one parameter. The agent writes a netlist, a layout or an EM setup in full, using its own file
tools, and then asks circuitRF whether it is sound (`check`), what circuitRF made of it
(`explain`), and what it does (`run`). The file formats are the interface, and the
[reference topics](#topics) describe every one of them, so the agent does not need circuitRF's
source code.

A capable agent, given a filter specification and the GaAs technology that ships with circuitRF,
produces the following in about a quarter of an hour:

- a synthesised prototype;
- a version built from elements that technology can realise, simulated against the spec and its
  tolerance corners;
- a drawn schematic;
- a layout that passes DRC;
- a checked EM setup;
- a GDSII file;
- a report saying what is still unverified.

It will also tell you where it had to work things out for itself. [Where agents go wrong](#traps)
lists the places that happen today.

## Connecting the agent {#setup}

The server is the circuitRF application itself, started with `serve`. The agent's client starts it,
so you only need to tell the client how to.

**1. Choose a root folder.** The server reads and writes only inside it, and refuses any path that
leads out of it. Use the folder your designs live in, not your home folder.

**2. Print the configuration.** circuitRF writes it for you, with the full path to the executable
filled in:

<pre><code class="cmd"><span class="prompt">$ </span>/Applications/circuitRF.app/Contents/MacOS/circuitRF serve --root ~/designs --print-config</code></pre>

On Windows the executable is `%LOCALAPPDATA%\Programs\circuitRF\circuitRF.exe`; on Linux,
`~/.local/bin/circuitrf`. The JSON it prints goes into your client's MCP settings. For Claude Code,
run the `claude mcp add …` line it prints underneath.

**3. Start a new session.** Most clients load MCP servers only when a session starts.

**4. Check that it answers.** Ask the agent to call circuitRF's `reference` tool with no topic. It
should list the reference topics with their sizes. If it cannot see a circuitRF tool, the client
did not start the server: run the printed command yourself and read what it says on stderr.

[The MCP Server](mcp-server.html) describes the server in full, including what it refuses, how to
run it from a source build and how to install circuitRF unattended.

## What to tell it {#brief}

Give the agent the same things you would give a colleague: the specification, the process, what
you want back, and how you will judge it. A brief that has worked well:

```text
Design a 50 ohm two-port low-pass filter as an MMIC on the GaAs technology that ships with
circuitRF. Insertion loss <= 1 dB from 0.1 to 3 GHz, return loss >= 10 dB at both ports over the
same band, rejection >= 20 dB from 6 to 12 GHz.

Work in a circuitRF workspace named lc_filter. Deliver: a circuit-level design simulated against
the spec, a schematic, a layout that passes check, an EM setup that passes check, a GDSII export,
and REPORT.md with the performance against each spec line and everything that is unverified.

Read circuitRF's reference topics before writing a file of a kind you have not written yet. Run
check after every file you write. Ask me before starting any EM run, and tell me its unknown count
first.
```

Five points in that brief do most of the work:

- **Name the technology**, or say that it is one that ships with circuitRF. The `technologies`
  reference topic lists every shipped technology with its stackup.
- **Ask for a report of what is unverified.** An agent that has no EM result will say so, and
  you need to know which numbers come from a circuit model and which from a field solve.
- **Tell it to `check` after every write.** It costs nothing, and it catches most mistakes before
  they reach a simulation.
- **Make it ask before EM.** An EM run can take minutes or hours. [The next section but one](#em)
  explains how to estimate the cost first.
- **Ask for a friction log** if you are evaluating circuitRF or the agent. "Note every time you
  guessed a syntax, were refused, or computed something yourself" produces a list worth reading.

## The reference topics it should read {#topics}

`reference` with no topic lists every topic and its size. The ones an agent needs, in the order it
usually needs them:

| Topic | When | What it gives |
|---|---|---|
| `examples` | before starting a design | the worked designs that ship with circuitRF; name one for its README, files and analyses, then copy it with `create what=example` |
| `technologies` | before creating the workspace | every shipped technology: its id, stackup and materials |
| `netlist` | before the first `.cnl` | the instance line, analysis and measurement syntax |
| `analyses` | before declaring any analysis | every `type=` and every key it accepts, with defaults |
| `tuning`, `goals` | before writing a tune, preset, goal or optimize line | which values can vary and how they are named; the goal grammar; each with a complete worked example |
| `optimizers` | before choosing an algorithm | every algorithm the `optimize` line can name, when to use it, and its options with their defaults |
| `statistics` | before writing a tolerance, a yield spec or a `statistics` line | the distributions and their spreads, `goal … use=yield`, the run's options, exit codes and result layout, with an example per mode |
| `component-index` | when choosing parts | every component type in one line each: nets, category, name |
| `components <TYPE>` | before using a component type | its nets, terminals and parameters |
| `units`, `expressions` | when writing values or measurements | SI suffixes, functions, conditionals |
| `measurements` | when checking against a spec | named quantities computed from a run |
| `shipped-materials` | before choosing or defining a material | every material that ships, with its properties |
| `technology`, `materials` | before reading or writing a `.ctech` / `.cmat` | the stackup, layers, DRC rules and material records |
| `schematic` | before the first `.csch` | components, wires, variables and analyses, with a worked example |
| `layout` | before the first `.clay` | shapes, layers, nets, ports |
| `em-setup` | before the first `.cem` | solver, mesh, ports and sweep |
| `cmc` | before building a model with the Model Composer | the `.cmc` document: its data sets, conditions and every step's settings |
| `model-data` | before producing or importing a model's data | what a model needs, what to run in another simulator, and every file format with an example |
| `cmodel` | before writing a FET model without the Model Composer | every field of a `.cmodel`, the arrays' layout, and complete Python and MATLAB scripts |

**Start from `component-index`, then ask for one type at a time.** `reference components` with no
type is the whole catalogue, around 100 kB, which is more than many agents can take in one tool
result. The index is a few kilobytes, and `components <TYPE>` is a few hundred bytes.

The same topics are offered as MCP *resources* at `circuitrf://reference/<topic>`, which cost less
for clients that support them.

## The design flow that works {#flow}

The order below keeps every expensive step behind a cheap check.

1. **Start from the nearest example**, if one is close. `reference examples` lists them; `create`
   with `what=example` copies one under the server's root, where the agent can read every file, run
   its benches and change it. The example's README says what each bench shows and in what order to
   run them.
2. **Otherwise create the workspace on the technology.** Use `create` with `what=workspace` and a
   `tech`. Every later document in the workspace resolves that technology.
3. **Synthesise an ideal prototype** as a `.cnl` of ideal L and C, and `run` it. This proves that
   the topology and values can meet the spec before anything physical is drawn.
4. **Rebuild it from realisable elements**: microstrip, vias and capacitors the process can make.
   Run it again, including tolerance corners. Each spec line becomes one `measure`: `max_over` and
   `min_over` give the worst value of a quantity over a frequency band, for example
   `measure IL_worst = max_over(-dB(SP1.S(2,1)), 0.1GHz, 3GHz)`, and `value_at` the value at one
   frequency, for example `measure G_2G45 = value_at(dB(SP1.S(2,1)), 2.45GHz)`. The agent reads them
   back with `read`. See [Measurements](measurements.html).
5. **Draw the layout** (`.clay`). Run `check` for DRC, `render` it to look at, and run `impedance`
   to compare each drawn line's Z0 with what the circuit model assumed.
6. **Write the EM setup** (`.cem`) and run `check` on it. Its notes report what the solver will mesh
   and how large the problem is. See the next section.
7. **Run EM** once the size is acceptable, then `read` the Touchstone and compare it with step 4.
8. **Export** the artwork with `convert`, for example a `.clay` in and a `.gds` out.

## Yield: tolerances, Monte Carlo and the worst trial {#yield}

An agent can set up and run a yield analysis with the MCP tools alone:

1. `explain` with `tunables=true` lists the values that can carry a tolerance, by key.
2. Write the tolerances on tune lines, `tune R1.R dist=gauss sd=2%`, and the spec as a goal marked for
   yield, `goal Vout = DC1.V("out") analysis=DC1 in 0.49 0.51 use=yield`. A `statistics` line sets the
   trial count and target: `statistics trials=500 target=95%`. `reference statistics` has the grammar.
3. `check` the file. A setup the run would refuse, such as one where nothing varies, is reported here.
4. `explain` with `analysis` reports how wide the yield's interval will be at that trial count and what
   the run will cost, in nominal simulations.
5. `run` with `analysis=yield` and `trials=500` returns the yield, its interval and each goal's worst
   trial. An exit code of 3 means the yield is below the target.
6. `read` the `.yield.npy` it wrote for the summary and every trial.
7. `run` with `analysis=yield` and `trial=<the worst trial>` shows what that trial drew.

`analysis=montecarlo` is the spread alone, with no pass or fail. Neither changes the design: a trial's
values become a preset only with `savePreset`, and a corner only with `saveCorner`, both on a `.csch`.

The rest of the workflow is reachable the same way:

8. **Corners.** `run` with `analysis=corners` and `generate="temp=-40,25,85;Vdd=3.0,3.6"` returns the
   corners that cross product makes and writes nothing; adding `write=true` on a `.csch` appends them.
   `run analysis=corners` then evaluates every enabled corner and names each goal's worst; with `mc=true`
   it runs a yield at each corner instead.
9. **Optimizing across corners.** `run` with `analysis=optimize` and `corners=all` meets every goal at
   the nominal and at every corner at once, and each goal's result names the corner that binds it.
10. **Centering.** Put `opt=1` and a range on the toleranced lines whose values may move, then `run`
    with `analysis=center`. The verified start and centred yields are the result; `surrogate=quadratic`
    makes each step cheaper when there are few toleranced values.
11. **Which values matter.** `run` with `analysis=sensitivity` ranks, per goal, the values that move it
    most at the design as it stands, each stepped up and down on its own (2k + 1 simulations, nothing
    random); `result.sensitivity.goals[].contributors[]` gives each one's basis, share and status. For
    interactions, `run` with `analysis=doe` and `design=pb` screens many values in a few
    runs; `design=ccf` on the few that matter fits a curved model, and `optimum=true` confirms that
    model's best point with a simulation.

The *Yield, Corners and Centering* example is a working copy of each of these steps.

## Building a device model from data {#model}

The `model` tool is the [Model Composer](model-composer.html) without the window. Each `noun` is one of the window's
runs and calls the same function, so a document an agent builds opens in the window exactly as it left it.

1. `reference` with topic `cmc` gives the document's format, including every step's settings, and `model-data` the data:
   what a model needs, what to run when it comes from another simulator, and every file format with an example. Read
   both first.
2. `model` with `noun=new`, `kind=active`, a stock `network` (`gan-two-shell`) or a six-pin `.csch`, and `fingers` and
   `unitWidth` for the measured device's periphery, creates the `.cmc`.
3. `model` with `noun=import` adds one data set per call. Give the kind with `as`, the bias pattern of a Touchstone
   folder with `pattern`, and what each CSV column holds with `map` (`Vgs=vgs:V`). State every condition the data was
   measured under with `condition`: `ReferencePlane`, `AmbientC`, and for a pulsed set its `Quiescent` (and `PrePulse`)
   bias and timing. A condition left out is a guess a later step makes for you, or a refusal. An array file (`.mat`,
   `.npz`) needs no `map` when its arrays have the usual names. Check each file first with `dryRun=true`: it reports what
   the file holds, the map, how each array is read and every warning, and writes nothing — the `.cmc` need not exist yet.
   When the data comes from another simulator the agent also drives, fix the export there until the dry run is clean.
4. `model` with `noun=deembed` removes the pads and the network and reports each set's intrinsic range. Data that does
   not reach the voltages the circuit will see should be noted here.
5. Write the settings the fit needs into the `.cmc` with your own file tools: which network values are free, self-heating
   and Rth, the trap model. Then run `check` on the `.cmc`, which refuses a misspelt key by name.
6. `model` with `noun=fit` and `step=all` fits every step in order. It writes its results into the `.cmc`. The report
   lines give each constituent's held-out error and every warning.
7. `model` with `noun=check-charge` checks charge conservation (exit 3 above the threshold). `noun=validate` compares
   the model with every data set at the plane it was measured at (exit 3 when a tolerance is missed).
8. `model` with `noun=export` and `output` writes the `.cmodel`. Place it in a netlist as
   `ComposedFET:X1 g d 0 File=fet.cmodel N=8` and `run` it like any other device.

A model that does not come from data — equations from a paper, a table another simulator wrote, a network trained in
another framework — needs no `.cmc`. `reference` with topic `cmodel` gives the file and scripts that write it; the agent
runs its script with its own tools and then calls `check` on the `.cmodel`.

`model` with `noun=explain` says, at any point, what the document holds: each data set and its plane, each step as fresh,
stale or absent, each network value with its error, and the trap levels each state was trained from. It fits nothing.

**Where agents go wrong here.** They guess a condition rather than ask for it: the reference plane and the pulse's
quiescent point change the answer, and neither can be read from the numbers. They take a model whose data stops at the
bias point to large signal: outside the data a model is extrapolated, and a gate swinging past the last measured `Vgs`
is not the device. They also report the fit's training error as accuracy. `validate` is the comparison to quote.

## EM runs: check the cost first {#em}

An EM run is the one step that can take hours. Before starting one, the agent should run `check` on
the `.cem` and read its mesh budget: the number of unknowns, the solver's ceiling, and the mesher's
verdict. The budget is in the document's `em` entry, so it is there even when `summary` collapses the
notes. A mesh setting that `Auto` overrides is reported as a warning naming it. Coarsen the mesh, or narrow what is meshed, until the number is
acceptable, and only then ask circuitRF to run it. [EM Setup](em-setup.html) describes each mesh
setting. [EM Solvers](em-solvers.html) explains what each solver costs.

## Where agents go wrong {#traps}

These are the places an agent working only from the reference topics is likely to stumble.

**Keep netlists inside the workspace.** A microstrip or via in a `.cnl` takes its substrate from
the workspace technology, exactly as one in a schematic does, and `check` says which numbers it
took. Outside any workspace there is no technology, so a line that names a layer is refused and a
line that names none uses a default board substrate.

**Read `check`'s warnings, not just its error count.** A line the netlist reader does not recognise
is skipped and reported as a warning, and warnings do not change the exit code. An agent that
invents a statement will see it reported there, and nowhere else.

**A drawn schematic takes more care than a netlist.** The `schematic` topic gives a complete working
example, and `components <TYPE>` gives each symbol's pin positions, but every wire has to end exactly
on a pin. Have the agent run `check` and `netlist` on the `.csch`: `check` reports a component it
cannot identify, and `netlist` shows what the drawing actually connects. Simpler still, have it
write the netlist and let `netlist` with `toSchematic` draw it
([`--to-schematic`](cli.html#netlist-to-schematic)). The drawing extracts back to the same circuit,
and every net carries its netlist name.

## Keeping its work reversible {#safety}

Nothing the server does deletes or overwrites a workspace. An agent's own file edits are another
matter, because it writes files with its own tools. If the workspace has [history](history.html)
turned on, the agent can take a restore point before a change (`history` with `checkpoint`), and
`batch` groups several calls under one restore point. [History and AI
assistants](history.html#assistants) describes how that works.

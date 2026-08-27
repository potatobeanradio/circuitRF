---
title: EM Setup
slug: reference/em-setup.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > EM setup
lede: The EM Setup panel, control by control, and where the results land.
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#creating">Creating an EM setup</a></li>
<li><a href="#header">The header: name, output file, layout</a></li>
<li><a href="#toolbar">The toolbar: Mesh, Simulate, Cancel, Save</a></li>
<li><a href="#analysis">Analysis</a></li>
<li><a href="#conductors">Conductors</a></li>
<li><a href="#frequency">Frequency</a></li>
<li><a href="#ports">Ports</a></li>
<li><a href="#mesh">Mesh — the uniform-line kernel</a></li>
<li><a href="#surface-mesh">Surface mesh — the full-wave kernel</a></li>
<li><a href="#solver">Solver options</a></li>
<li><a href="#stackup">Stackup</a></li>
<li><a href="#blocked">When Simulate is greyed out</a></li>
<li><a href="#results">Where the results land</a></li>
<li><a href="#headless">Running the setup without the GUI</a></li>
<li><a href="#overlays">What the layout shows after a run</a></li>
</ol>
</nav>

This page is the panel walkthrough. The engine behind it — what it solves, what it will not solve, how
ports and de-embedding work and what the numbers mean — is
[the MoM engine chapter](mom-engine.html), and this page links into it rather than repeating it.

{{ui: em-setup-editor}}

## Creating an EM setup {#creating}

An EM setup is a document of its own, saved as a [`.cem`](file-formats.html). It holds **everything that
affects the answer**: which layout, which analysis, the frequency plan, the port impedances, the mesh
settings and the solver switches. Nothing that changes a result lives in a transient dialog, so a `.cem`
committed beside a layout reproduces that run later.

Two ways in, and they agree:

- **The layout editor's EM button.** One gesture from an open layout to its setup. `Amp.clay` gets
  `Amp.cem`, and if that setup already exists it is opened rather than a second one created.
- **File ▸ New ▸ EM Setup…** Prompts for a name and defaults the layout reference to the layout you are
  looking at.

Either way the file lands in the workspace's `em/` folder, where the project tree lists it. Setups are
workspace-scoped — there is no scratch EM setup.

**The defaults are meant to be run, not configured.** A fresh setup arrives at 50 Ω on every port, a
1–20 GHz 101-point linear sweep, `Auto` analysis, and mesh settings that are the engine's own. On a
small structure the only thing you have to supply is which layout.

## The header: name, output file, layout {#header}

**Name** — the setup's display name, taken from the file stem. It is also what names the written result,
so two setups over one layout do not collide.

**Output file** — where the S-parameters are written. Leave it blank and you get the layout's own name
with an `.sNp` suffix, in the workspace's `results/` folder. A relative path is taken from `results/`; an
absolute path is used as given. **The `.sNp` suffix is added from the port count**, so you do not type
it: the same structure re-run with a coupled pair goes from `.s2p` to `.s4p` on its own. The `…` button
browses; a path inside the workspace is stored relative so the workspace stays portable.

**Layout** — the `.clay` this setup analyses, and the `…` button re-points it. Re-pointing re-derives the
cross-section, the ports and the mesh against the new artwork, and it is undoable.

The reference is by path, resolved when the panel refreshes and again when it runs. If the layout is
moved or deleted the panel says so in place of the extraction, rather than failing at Simulate time.

## The toolbar: Mesh, Simulate, Cancel, Save {#toolbar}

| Button | What it does |
|---|---|
| **Mesh** | Meshes and stops. No solve. This is the cheap "is my mesh sane?" answer — press it as often as you like. |
| **Simulate** | Meshes and solves the whole frequency plan, writing the result files. |
| **Cancel** | Replaces whichever of the two is running. It stops at the next work boundary — a grid row when meshing, a frequency point when solving — so a full-wave run can keep going for tens of seconds after you press it. The button reads *Cancelling…* until it actually stops, and a cancelled run writes nothing. |
| **Undo / Redo** | Every setting in this panel is undoable, including Change Layout. |
| **Save / Save As…** | `Save As…` writes the setup to a different `.cem` and the editor then follows the new file; the original is left as it was on disk. |

`Ctrl+S` (`Cmd+S` on macOS) saves, docked or torn off.

**Press Mesh before Simulate on anything unfamiliar.** The mesh report tells you the unknown count, and
the unknown count is what decides whether the run takes seconds or is refused — see
[what makes a run infeasible](mom-engine.html#budget).

## Analysis {#analysis}

{{ui: em-setup-loaded}}

**Analysis** chooses which kernel you are asking for:

| Choice | Meaning |
|---|---|
| **Auto** *(default)* | Picks from the geometry, preferring the faster kernel whenever it applies, and always says which it picked and why. |
| **Cross-section** | For a straight, constant-width line: solves its cross-section for Z₀, ε_eff, loss and delay. Exact for that geometry and effectively instant, because the answer does not change with frequency. |
| **Planar** | For arbitrary artwork: bends, stubs, gaps, coupled structures and multi-level metal with vias. It sees discontinuities, coupling and radiation — and costs a full solve at every frequency. |

Underneath the selector, in bold, is **the kernel that was actually chosen**, with the registry's own
one-line reason. That distinction matters: `Auto` is a *request*, and the line below is the *outcome*. It
updates as you type, not when you press Simulate, so a setup that is about to take the slow path says so
while you can still do something about it.

Asking for a kernel the geometry does not support is refused with the reason, rather than silently
demoted to the other one.

**Cross-section** shows a readback — the propagation axis, the signal layer and the ground reference the
extractor resolved, plus a one-line summary of the conductors it found. Read it. It is the cheapest
possible check that the tool is looking at the structure you think it is.

**Notes** collects everything the extraction had to say that is not an error: a shape that was merged, a
generator-produced instance that was flattened, an inferred port direction. Notes are not warnings, but
they are the first place to look when a result surprises you.

## Conductors {#conductors}

**Signal conductor** names which stackup conductor layer the cross-section analysis is about. Leave it at
*infer from the drawn geometry* unless the layout carries metal on more than one level and you mean a
particular one.

For the full-wave kernel there is a second, separate control: an expander headed **"n of m included"**
listing the **analysis levels** — which conductor levels the solve meshes, bottom to top. Leave every box
unchecked and the solve includes every level that carries artwork, which is what you want almost always.
Check a subset when you deliberately want a single-level answer out of a multi-level board — it is a
smaller, faster problem, and it is a *different* problem.

The two controls are not the same thing and deliberately do not share a control: one picks the single
conductor a uniform cross-section is about, the other picks a set of levels for a 3D-stacked solve.

## Frequency {#frequency}

The same sweep editor the analyses use: **Start**, **Stop**, and either a **Step** or a **Points** count,
linear or log. Each field takes a coefficient and a unit from the combo beside it, and prints the
resolved value underneath — so `1` + `GHz` reads back as `1 GHz` and there is no ambiguity about what was
entered. See [Units](units.html) for what the text boxes accept.

Default: **1 to 20 GHz, 101 points, linear.**

For the cross-section kernel the sweep is nearly free — the quasi-static answer does not change with
frequency, and the sweep exists so that the written Touchstone has the frequencies your test bench wants.
For the full-wave kernel **every point is a solve**, and the sweep is the single biggest driver of run
time.

Which is why:

**Adaptive sampling** *(on by default, full-wave only)* solves a subset of the requested frequencies and
models the rest, refining until a solved midpoint agrees with the model to 1e-3 in |S|. **The published
sweep is always exactly the grid you asked for**, and every solved point carries the solver's own result
unchanged — the interpolant fills in between them, it never overwrites them. Turn it off to solve every
point. The full mechanism, and how to tell it converged, is
[in the engine chapter](mom-engine.html#adaptive).

When adaptive sampling is unavailable the checkbox is disabled and says why in place of a tooltip.

## Ports {#ports}

Hover the **Ports** header for the explanation that matches the chosen kernel, because the two answer
"where is the port?" completely differently:

- **Full-wave.** Each port is a **port label in the layout** — place them with the layout editor's Port
  tool. Which cut of a conductor a label names is inferred from the geometry and reported in the notes;
  an ambiguous one is refused rather than guessed.
- **Cross-section.** There is no meshed port at all. The ports *are* the ends of the extracted
  conductors by construction: port 2k−1 is conductor k's near end and port 2k its far end, so two
  conductors give four ports. There is nothing to place, and de-embedding is a no-op.

**Port Z₀.** Two fields for the near-end and far-end defaults, both 50 Ω. A **complex** reference
impedance is accepted — type it as an expression, and a value that will not parse is reported under the
field rather than silently ignored. Beyond that, the panel shows a **per-port list** — one row per port
in the engine's own order, each independently overridable. The list appears for **every full-wave
setup**, and for a cross-section setup once it resolves to more than two ports. Ports may sit on
different conductors; that is what a coupled or multi-port structure is.

**Port type** *(full-wave only)*. Each row in the per-port list carries a type as well as an impedance:

- **Edge** *(the default)* — the port is at a conductor's end face, and it **is** de-embedded. This is
  the right answer for anything power flows into or out of.
- **Internal delta gap** — the port is a cut across the middle of a conductor, with metal on both sides,
  for a lumped element or a device terminal embedded in the metal. It is **not** de-embedded, because
  there is no feed outside the cut to remove; its S-parameters are reported at the gap in the reference
  impedance set beside it.
- **Internal** — the port is between the metal and the **ground plane**, at the point you put the
  label, for a component or a device terminal that returns to ground. It does not cut the trace, it is
  **not** de-embedded for the same reason, and its polarity is not yours to set: + is the metal, − is
  the plane. You do not have to draw the via down to the plane — the solver builds that path and
  reports its size, and uses a via you drew when there is one. Which conductor the plane actually is
  comes from [The stackup](stackup.html).

Changing a type **clears the mesh report**, because it changes which cells the excitation drives — the
old report is about a different excitation. Changing an impedance does not; that is a renormalisation
applied to the answer.

The cross-section kernel offers no type. Its ports are the two ends of a uniform line by construction,
so an interior gap would mean nothing there.

Which type to use, what an internal port costs you, auto-ports, and how the reference impedance
interacts with de-embedding are in the engine chapter: [Ports](mom-engine.html#ports).

## Mesh — the uniform-line kernel {#mesh}

This group appears for the **cross-section** kernel. It meshes a 2D cross-section, so its knobs are about
the width of a conductor and how far out the truncation goes.

| Setting | Default | What it does |
|---|---|---|
| **Min cells across width** | 6 | The floor on how many cells span the narrowest conductor. Raise it when the answer still moves as you refine. |
| **Edge cells** | 3 | How many refined cells are placed at each conductor edge, where the current density is singular. |
| **Edge fraction of width** | 0.03 | The size of the first edge cell, as a fraction of the reference width. |
| **Edge growth ratio** | 1.7 | How fast cells grow away from an edge back to the bulk size. |
| **Truncation (substrate heights)** | 20 | How far past the metal the discretised ground/dielectric extends, in substrate heights. Too small truncates the fringing field; too large wastes unknowns. |
| **Truncation tail cells** | 12 | How many cells cover that tail. |

A value that will not parse, or is out of range, is reported under the group and the run is blocked
until it is fixed.

**Mesh report** — pressing Mesh fills in the unknown count, the smallest and largest cell, and the
truncation half-extent, in the layout's own display units. **The unknown count is the number to watch**;
everything about cost scales with it.

## Surface mesh — the full-wave kernel {#surface-mesh}

This group replaces the one above when the **planar** kernel is chosen. It meshes metal surfaces, so its
knobs are about wavelength and edges.

| Setting | Default | What it does |
|---|---|---|
| **Cells per wavelength** | 20 | The cell-size cap, at the mesh frequency. The usual accuracy/cost dial: 20 is a sane default, 30 is a refinement check, below ~10 is not a serious answer. |
| **Edge mesh** | on | Refine at conductor edges, where current crowds. Turning it off is faster and worse; it is there so you can measure what it buys. |
| **Edge cells** | 3 | How many refined cells sit at each edge when edge meshing is on. |
| **Boundary cells** | **Staircase** | How curved and oblique edges are treated. **Staircase** approximates them on the rectangular grid. **Conformal** cuts the boundary cells to follow the metal, which is more accurate on tapers, bends and curves — and is **not** a free win. Read [conformal boundary cells](mom-engine.html#conformal) before turning it on; it ships off because it regresses on one class of board. |
| **Mesh frequency** | blank | The frequency the cell-size cap is sized at. Blank uses the top of the sweep. Sizing lower gives fewer unknowns and a faster run, at the cost of resolution at the top of the band — the notes under the group say what the trade actually is for your geometry. |

Below the fields sits the **mesh summary** — unknown count, cells across the narrowest conductor, and the
mesher's own verdict on whether that is enough — plus the mesh notes. This is the readout that tells you
a run is about to be too big *before* you start it.

## Solver options {#solver}

Four switches, all of which either change how the same answer is computed or trade time for memory. None
of them changes what is being solved.

**Vertical (via) kernel — "Integrate G_A^zz directly"** *(off by default)*. Replaces the fitted Green's
function with direct numerical integration for the one term that couples vias to each other. It costs
roughly **15–45% more per frequency point per via span**. Turn it on when a run is refused for its via
separation — the refusal names this switch.

**Accelerated solve — "Use the AIM accelerator"** *(off by default)*. Solves the same system iteratively
against a grid-accelerated matrix–vector product instead of forming and factoring the full dense matrix,
to its own accuracy gates.

<div class="callout note">
<span class="label">What the accelerator is actually for</span>
<p><b>The win is working-set memory</b> — roughly 4× less past about 900 unknowns. The <i>time</i>
crossover is much later, around 3,700 unknowns; below that the dense path is faster. It does
<b>not</b> raise the unknown ceiling: a mesh past the ceiling is refused before a solver is chosen.
Single metal level only, and no vias.</p>
</div>

**Dispersion — Kirschning–Jansen correction** *(on by default)*. The quasi-static cross-section holds
ε_eff and Z₀ constant with frequency; this correction restores the frequency dependence of a microstrip
line. It is on by default because the default sweep runs to 20 GHz, where an uncorrected quasi-static
answer is visibly wrong at the top of its own band. It is derived for a **single microstrip** — one
conductor over a ground plane on one substrate — so on any other cross-section the checkbox is disabled
and says so.

**Cores.** How many cores the full-wave solver may use at once. *Automatic* uses the whole machine.
Lowering it leaves cores free for other work and makes the run slower; **it never changes the answer.**

Every one of these four disables itself with a stated reason when it does not apply, rather than sitting
enabled and doing nothing.

## Stackup {#stackup}

The bottom group shows the technology the layout resolves to, one row per stackup entry: kind, name,
thickness, the electrical properties (ε_r and tanδ for a dielectric, conductivity for metal), and which
drawing layers map onto it. The signal conductor and the ground reference are marked.

**It is shown, not edited.** The stackup belongs to the [`.ctech`](layout-editor.html#technology), which
is the one place it can be edited and the one place every layout, PCell and EM setup reads it from.
**Edit technology…** opens it.

If the layout has no technology resolved, the panel says so and blocks the run: nothing states how thick
the metal is, what is underneath it, or where the ground plane sits, and there is no defensible default
for any of those.

## When Simulate is greyed out {#blocked}

A banner under the toolbar carries the **blocking reason** — one sentence saying what is unresolved, in
the same words the tooltip on the disabled Simulate button gives. The panel computes it as you type, not
at run time.

The usual causes, in the order the panel checks them:

- no layout selected, or the reference does not resolve;
- the layout has no technology;
- the extractor refused the geometry for the requested kernel;
- the chosen kernel refused the extracted problem;
- a port label is ambiguous, or a port could not be resolved (including an internal delta-gap port that
  is not on the metal, or has no direction on it);
- the mesh exceeds the run budget.

**A refusal is a result.** The engine declines geometry it cannot solve correctly rather than returning a
number that looks plausible; [what the engine refuses](mom-engine.html#refusals) lists the refusals and
what each of them means you should do.

## Where the results land {#results}

A successful run writes **two** files into the workspace's `results/` folder:

- **The Touchstone `.sNp`**, at the output path above. This is the artefact you point a schematic's
  [SnP component](components.html#snp) at — that is the whole co-simulation route, and it is described
  in [Using EM results in a circuit simulation](mom-engine.html#cosim). Its header is stamped with the
  stackup, the mesh settings, the port definitions and a hash of the geometry, so a stale file beside an
  edited layout is *reported*, not silently trusted.
- **The `.npy` dataset**, carrying what Touchstone cannot: the per-kernel diagnostics group — Z_c, γ,
  ε_eff, attenuation and per-unit-length RLGC for the cross-section kernel, and the calibration's own
  residual and usability flags for the full-wave one. Open it in the [Data Display](data-display.html)
  like any other run.

<div class="callout warn">
<span class="label">One flat results folder</span>
<p><code>results/</code> is shared with schematic runs and is keyed by name. An EM setup named after a
schematic that also writes results will collide with it — both resolve to the same stem. Give the setup
its own name, or set the output file explicitly.</p>
</div>

**The `.sNp` is not placed into a schematic for you.** Back-annotation into an `SnP` component exists in
the engine and is exercised by the test suite, but no button in this release invokes it: place the
component yourself and point its `File` parameter at the written path. Re-running updates the file in
place, so the schematic picks the new result up the way it picks up any changed source.

## Running the setup without the GUI {#headless}

A `.cem` is a complete, self-contained description of a run, which means the panel is only one way to
start one. The command line is the other:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf em Amp.cem</code></pre>

**No other arguments.** The layout reference resolves against the nearest workspace above the `.cem`,
and the technology against the layout's own workspace — the same two walk-ups the panel does, because
it is the same code. The run writes **the same two files to the same place** as Simulate, so a
schematic's SnP reference stays valid across a headless re-run; `-o` moves the Touchstone if you want
it elsewhere.

That is what makes re-extraction scriptable: edit the stackup once, then re-run every setup in the
workspace and let each schematic pick up its new Touchstone.

The verb, its options, the three message lists, the exit codes and a worked example from an empty
folder are in [The Command Line](cli.html#em).

## What the layout shows after a run {#overlays}

The layout canvas for the analysed `.clay` draws three overlays, all of them from the engine's own
coordinates rather than re-derived here:

- **The mesh**, after Mesh or Simulate.
- **The current-density map** for a full-wave run, with its scale and normalisation printed in the panel
  under *Current density*. It is a per-cell |J| map — read it for where the current actually goes, not
  as a calibrated number.
- **The de-embedding reference planes**, drawn where the engine reports them. That is worth looking at
  once per new structure: the plane's exact position is a property of the method and is
  [not user-positionable](mom-engine.html#deembedding).

**Every overlay is dropped the moment the artwork changes.** A mesh or a current map drawn over edited
geometry looks like it still matches, which is worse than showing nothing.

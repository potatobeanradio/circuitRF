---
title: EM Setup
slug: reference/em-setup.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > EM setup
lede: The EM Setup panel, control by control, and where the results land.
keywords: EM, electromagnetic, cem, ports, mesh, extraction, simulate layout, substrate
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
<li><a href="#solver">Solver options</a>
  <ul><li><a href="#deembedding">Port de-embedding</a></li></ul></li>
<li><a href="#stackup">Stackup</a></li>
<li><a href="#blocked">When Simulate is greyed out</a></li>
<li><a href="#results">Where the results land</a></li>
<li><a href="#headless">Running the setup without the GUI</a></li>
<li><a href="#install-assistant">Letting circuitRF install the 3D solvers</a></li>
<li><a href="#install-3d-solvers">Installing the 3D solvers by hand</a></li>
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

**Solve region** — the part of the layout this setup solves. It reads **Whole layout** until you set one.
**Draw on layout…** opens the layout and lets you drag a box around the part you care about, such as one
section of a trace on an imported board. Only the geometry inside the box is meshed and solved:

- A shape that crosses the box's edge is cut there.
- A via is kept or left out whole, depending on where its centre is.
- The Messages panel reports how many shapes were kept, cut and left out.

Every port has to lie inside the box, and a port outside it is refused by name. The box is saved in the
`.cem` and drawn on the layout as a dashed outline, and the layout file is not changed. **Clear** goes
back to solving the whole layout. Setting or clearing the region is undoable. The Touchstone header
records the region, so the file itself says it describes only part of the board.

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
  an ambiguous one is refused rather than guessed. **Copper that overlaps on one layer is one
  conductor** — a placed part's footprint pad lying over an imported board's own pad is merged before
  meshing (the notes say so), and the Port tool snaps to the merged copper's edge, not to a face
  buried inside it. A port whose feed has other metal too close is refused with **where** that metal is.
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

Six switches. Four of them either change how the same answer is computed or trade time for memory, and
change nothing about what is being solved. The two port-de-embedding switches are different in kind and
are described last: one of them changes the answer, and the other publishes an answer circuitRF would
otherwise refuse to write.

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

### Port de-embedding {#deembedding}

**"De-embed the ports"** *(on by default, full-wave only)*. The full-wave kernel drives each edge port
with a gap across the metal, and that gap has a reflection of its own that is not part of your circuit.
De-embedding measures it on a two-line calibration standard and removes it, so what you get back is
referenced to **your drawn metal edge**. See
[the full-wave kernel's own page](mom-engine.html#deembedding) for the arithmetic and its validity
condition.

Turn it off to read the **raw solve** instead. Those s-parameters include the port discontinuity, are
referenced to the port cell rather than to your metal, and are for diagnostics rather than for a circuit
— the run says so in its notes. It is also much faster, because every calibration standard is skipped,
and those standards are most of a de-embedded run's solve time.

**"De-embed outside the calibration's validity"** *(off by default, full-wave only)*. The calibration
standard is an **isolated uniform line** of the port's own cross-section. When another conductor sits
inside the run of line that standard reproduces, the error box is measured on a structure that is not the
one being corrected — and the peel divides that mismatch by a quantity of order 10⁻⁴, so a small error in
the standard becomes a large one in your answer. **A run in that condition is refused**, naming the port,
the distance and what that class of neighbour needs:

<pre>
Calibrated port feeds are not isolated: port 1 has other metal 246 µm away (0.27 substrate
heights, against the 5 a neighbour that carries a port of its own needs)…
</pre>

Ticking this box publishes that answer anyway. The answer is still wrong in exactly the way the refusal
describes; what changes is that you asked for it. **The Touchstone it writes gains a provenance line
saying so**, so the file still carries the caveat when it is opened somewhere else, months later, by
somebody who never saw the run:

<pre>
! circuitRF-EM caveat: the port de-embedding was applied OUTSIDE the geometry it is valid for:
! port 1 at 0.27 h (needs 5), … These s-parameters are not a measurement of this structure.
</pre>

There are good reasons to want it — comparing against a previous run, debugging, or simply knowing the
port region is not where your answer lives. There is no good reason for it to be quiet, which is why it
is never quiet.

<div class="callout note">
<span class="label">Every run that de-embeds reports its margin</span>
<p>Each port's note carries the distance to its nearest other conductor <b>in substrate heights</b>,
whether or not it is a problem: <i>"Port 1's feed clearance is 6.38 substrate heights — 5746 µm to the
nearest other conductor, which carries a port of its own and so needs 5."</i> A pass/fail with no
distance tells you nothing about how close you came.</p>
</div>

Every one of these disables itself with a stated reason when it does not apply, rather than sitting
enabled and doing nothing — the override, for instance, is disabled outright when de-embedding is off,
because there is then no calibration to apply outside anything.

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

## Solver — planar or 3D {#solver-3d}

The **Solver** group at the top of the panel picks who solves the setup: circuitRF's own planar and
cross-section kernels (the default), **FEM 3D (Palace)**, **FDTD 3D (openEMS)**, or **FEM & FDTD -
Compare**. A 3D setup builds a 3D model from the layout, its technology and any bond wires. The
settings a 3D run does not read — the surface mesh, adaptive sampling, the port type, the planar
solver options and the radiation pattern — are hidden while a 3D solver is chosen; they stay in the
`.cem` and come back when you switch to Planar. What a 3D run does read stays: the frequency sweep,
each port's Z₀, the return plane, the solve region and the core count. Palace and openEMS each have their own section below, and a setup keeps
both, so switching solver never loses the other's settings.

With Palace chosen the panel shows Palace's own settings. **A blank box is the default shown in it**, and
every box is a field of the `.cem` — nothing here lives only in the panel.

**Quality** (`Quality`: `Draft`, `Standard` or `Accurate`) sets the element order, the refinement and the
sweep tolerance together. Omitted, it is `Standard`, which is exactly the defaults in the table below.
The boxes show the chosen preset's values as their placeholders, and **a box you fill in overrides the
preset for that field only**.

| Preset | Element order | Refinement passes | Refinement tolerance | Sweep tolerance |
|---|---|---|---|---|
| Draft | 1 | 0 | — | 0.001 |
| Standard | 2 | 2 | 0.01 | 0.0001 |
| Accurate | 2 | 3 | 0.005 | 0.00001 |

Measured once on F0's via transition (case B, 0.1–20 GHz, 200 points) on a 10-core, 16 GB machine:
**Draft** took 74 s at 3.8 GB, and its |S21| is within 0.1 dB of Standard's but its phase is up to
**20.6°** away; **Standard** took 35 min at 9.3 GB. Draft is for a quick look at magnitudes, not phase.
Accurate has not been measured.

| Setting | `.cem` field | Default | What it does |
|---|---|---|---|
| Largest element (λ) | `MaxElementWavelengths` | 0.1 | The largest element in each material, as a fraction of the wavelength in that material at the top frequency |
| At metal and ports | `EdgeRefinement` | 0.2 | The element size at conductors and sheets, as a fraction of the smallest size above. Port sheets are always at least four elements across their smaller side |
| Grading | `Grading` | 1.3 | How fast elements grow away from metal and ports |
| Element order | `ElementOrder` | 2 | Palace's finite-element order |
| Refinement tolerance | `AdaptiveTol` | 0.01 | The error at which Palace stops refining the mesh |
| Refinement passes | `AdaptiveMaxIterations` | 2 | The most refinement passes; 0 solves the starting mesh only. Each pass costs a solve and memory |
| Sweep tolerance | `SweepAdaptiveTol` | 0.0001 | The tolerance of Palace's adaptive frequency sweep; 0 solves every frequency |

The settings above size only the **starting** mesh; Palace's adaptive refinement adds elements where its
error estimate says the answer needs them. Simulate then runs Gmsh on the model and Palace on the mesh,
using the same *Cores* setting as the planar solver (as MPI processes). A 3D result is named after its
solver — `results/<name>.palace.sNp` and `<name>.palace_em.npy` — so it never replaces a planar or an
openEMS result, and the folder `results/<name>.palace/` holds everything the run made (the geometry
script, the mesh, the Palace configuration, both programs' logs). An unchanged model reuses its mesh.

Before it solves, circuitRF checks that every surface in the mesh belongs to exactly the object it was
made for — each port sheet, each conductor, each face of the air box. **Any mismatch refuses the run,
naming the object**; a boundary is never guessed onto a face.

**Will it fit?** Palace's memory is checked against the machine's twice: before Gmsh starts, from the
model's volumes, and again once Gmsh has reported how many tetrahedra it made, before Palace starts.
The second check is the one that counts on a bond wire or any small conductor, where the refinement
around the metal is nearly the whole mesh and the volumes alone say almost nothing. Past **75 %** of
the machine's memory the run carries a warning naming the estimate and what would shrink it — the
Draft preset, no refinement passes, a smaller air box — each with the estimate it would give. Past
**150 %** Simulate asks before going on (headless, `circuitrf em` needs `--force`). A warning never stops
a run on its own: the estimate uses the highest memory per unknown circuitRF has measured.

**While it runs**, the progress row names Palace's own stages, from its own log: *Meshing (Gmsh)*;
*Solving: refinement pass k of N* with the unknown count; *Sweep: sampling* with Palace's error and the
tolerance it converges on (the bar is that convergence on a log scale, not a share of the work — the
number of samples is not known in advance); *Sweep: evaluating frequencies* with a count; *Reading
results*. A multi-port setup samples and sweeps each port's excitation in turn, and the row says which.
Beside the stage is the memory Palace's processes are using, read once a second. If Palace prints
something circuitRF does not recognise (a Palace version it has not been validated against), the row
falls back to a count of log lines, with no bar, and the run's notes say so. A 3D run cannot finish
early and keep what it has, so its running button reads **Cancel**, not Stop.

**When it finishes**, one line says what it cost — wall time, Palace's own peak memory, the tetrahedra
it started and finished with, the unknowns, the refinement passes, the sweep's samples and the preset —
and the `.sNp`'s header records the same (`circuitRF-EM 3D run:` and `circuitRF-EM 3D run cost:`).
Palace runs one MPI process per **physical** core by default; a *Cores* setting above the physical count
is refused rather than oversubscribed.

### The openEMS grid {#openems-grid}

openEMS solves on a rectilinear grid — three lists of grid lines, one per axis — and circuitRF writes
that grid itself. Its accuracy is decided almost entirely by where the lines fall, so the rules are
fixed and every one is reported: a line **on every metal edge** aligned with an axis (a diagonal or
curved edge gets lines at its extremes only, and is staircased between); at the edge of a strip or
other thin metal, the **thirds rule** — lines a third of the local cell inside the metal and two thirds
outside, where the field's edge singularity is; lines **exactly** on every port sheet's extent, every
sheet's plane and every face of the air box; cells no larger than a set fraction of the **wavelength
in the densest material** they pass through; and neighbouring cells that grow by at most a **grading
ratio**. Each absorbing face gets a uniform **PML** of extra cells *outside* the air box — the box
grows outward to hold it, never inward over the design.

Two lines closer than **MinCell** are merged into one, and **every merge is reported, naming both
features**: two shapes 10 nm apart through a drawing error would otherwise set the time step of the
whole run without anyone seeing why. A port line or a sheet's plane is never moved; two of those
closer than MinCell are kept, with a warning naming both.

| `.cem` field (in `OpenEms`) | Default | What it does |
|---|---|---|
| `CellsPerWavelength` | 20 | The largest cell, as cells per wavelength in the densest material it passes through, at the top frequency |
| `GradingRatio` | 1.3 | The largest ratio between two neighbouring cells |
| `ThirdsRule` | true | The thirds rule at the edges of strips and other thin metal; false puts one line on each edge, as a hand-built model often does |
| `MinCellUm` | a tenth of the smallest metal width or thickness | Lines closer than this, in micrometres, are merged — and reported |
| `PmlCells` | 8 | Uniform cells added outside each absorbing face |

`circuitrf explain` on the setup prints the grid before anything runs: lines per axis, the total
cells, the smallest cell **and the features that set it**, the time step it allows (an estimate —
openEMS computes its own), the steps and memory the run would take, and every merge. A grid that would
not fit in memory is refused, naming the feature behind the smallest cell and the setting that would
relax it.

### Running openEMS {#openems-run}

With openEMS chosen the panel shows the grid settings above and two of its own, each a field of the
`.cem`'s `OpenEms` section; a blank box is the default.

| `.cem` field (in `OpenEms`) | Default | What it does |
|---|---|---|
| `EndCriterionDb` | −50 | A run stops when every port's voltage and current has fallen this far below its peak |
| `MaxTimeSteps` | ten times the grid's own estimate | The most time steps one run may take |

**openEMS excites one port per simulation, so an N-port setup is N runs**, one after another, each
using every core — a 4-port takes about four times as long as a 2-port, and the progress line says
*port k of N*. circuitRF writes openEMS's model itself, runs the `openEMS` program, reads the voltage
and current it recorded at every port, and computes the S-parameters from them with the same
definition of a wave as every other result, including a complex port impedance.

**When a run stops.** openEMS's own stopping rule is the field energy left in the model, and on a
board whose metal floats in open space — a via transition's ground plane — that energy never falls,
while the ports went quiet long before. So circuitRF watches the ports: once every port's signals have
fallen by `EndCriterionDb`, it tells openEMS to finish. **A run that reaches `MaxTimeSteps` first has not
converged**: its result is still written, with a warning stating how far the signals fell against the
criterion, and it is never presented as converged.

**What FDTD cannot say that Palace can**, each stated in the run's notes, never hidden:

- **Dielectric loss is exact at one frequency.** openEMS holds a material's conductivity constant, which
  reproduces a loss tangent only at the band centre; away from it the loss grows as 1/f, where Palace
  holds tanδ constant. On a lossy substrate this is the largest expected difference between the two.
- **Solid metal is a perfect conductor.** A grid cannot resolve a metal's skin depth, so solid
  conductors — vias, thick lines, bond wires — are lossless in openEMS. Thin metal written as a sheet
  keeps its conductivity and thickness.
- **A bond wire thinner than the grid cell around it** is written as openEMS's thin conductor, whose
  effective radius is set by the cell, not the wire: expect too much inductance. A grid step of half
  the wire's radius around it matched Palace within 0.1 dB in circuitRF's own validation.

Absorbing faces are PML (`PmlCells`; 0 gives a first-order Mur boundary instead), and anything reaching
one is continued through it. The result lands beside a Palace one without replacing it —
`results/<name>.openems.sNp` and `<name>.openems_em.npy` — and `results/<name>.openems/` keeps the model
and, per port, openEMS's log and probe files, so a run can be repeated by hand.

### Running both, and the difference {#run-both}

**FEM & FDTD - Compare** (`Solver3D: Both`, or `circuitrf em x.cem --solver both`) builds the 3D model
once and runs Palace and then openEMS on it, one after the other. Each result lands where it would
alone, and a third file, `results/<name>.compare_em.npy`, holds the difference: both S matrices,
`dMag_dB`, `dPhase_deg` and `dVec` (the magnitude of S_Palace − S_openEMS, which stays meaningful where
|S| is small and a dB difference is not). It opens in the Data Display like any result.

The first line the run writes is the summary: the largest S21 and S11 differences over the band and
where they occur. After it comes what makes a difference **expected** on this model — which solver
suits its geometry better, a lossy substrate, metal that openEMS treats as perfect, a
wire below the grid cell — and, as a warning, an openEMS run that did not reach its end criterion, which
is the first thing to fix. **Agreement is a cross-check, not a validation**: both solvers read the same
model, so an error in building it would appear in both.

Both solvers are checked before either starts, so a missing openEMS is reported in the first second,
not after the Palace run, with the setting that runs the one that is available. If one solver fails
part way through, the other's result is still written and no comparison is. The two results must be
at the same frequencies — they always are when both come from one setup — and a comparison is never
interpolated.

### Package capacitance and inductance {#package-rlc}

A Palace setup can solve for a **matrix** instead of S-parameters. The **Problem** picker
(`Problem3D`) is *Driven* (S-parameters, the default and omitted from the file), *Electrostatic* (the
capacitance matrix) or *Magnetostatic* (the inductance matrix). The two static problems run on Palace
only; a setup naming openEMS or both solvers is refused, and so is its `check`. They ignore the
frequency sweep and the port impedances, which are kept but not read (`check` says so).

**Terminals** (`Terminals3D`) are the matrix's rows and columns, in order. Each names a **net** — a net
of the layout, or a `.wBond` wire array — and every conductor on it (traces, vias, bond wires) belongs to
that terminal. **Ground net** (`Ground3D`) is the reference; left blank it is the technology's
ground-reference conductors, and the floor of the air box when that floor is a ground plane.

```
"Problem3D": "Electrostatic",
"Terminals3D": [ { "Name": "RF_IN", "Net": "RF_IN" }, { "Name": "VDD", "Net": "VDD" } ],
"Ground3D": "GND"
```

- **Electrostatic** gives `C`, the *Maxwell* matrix: `C[i][i]` is terminal i's capacitance to every
  other conductor, and each off-diagonal entry is negative. It also gives `C_mutual`, the
  lumped-circuit form: `C_mutual[i][i]` is i's capacitance to ground alone and `C_mutual[i][j]` is the
  capacitor you would draw between i and j. **Every conductor must be in a terminal or be ground**:
  Palace has no floating conductor, so an unlisted one is refused by name rather than silently grounded.
- **Magnetostatic** gives `L`. Each terminal also names a **source port** (`Source`, the port's number):
  the port's sheet is where its current enters and returns, so the path is port → the terminal's
  metal → back through ground. A terminal without one is refused. Conductors are surfaces, so this is
  the **external** (RF) inductance; the run's notes give the size of the internal term the DC value
  would add for the thickest round conductor.

The air box's open faces are zero-charge (no field line ends on them) in an electrostatic solve, and a
static setup's default padding is the structure's own size rather than a wavelength. The result is
`results/<name>.palace_es.npy` (or `_ms`) and **no Touchstone file**; `circuitrf em` prints the matrix
in engineering units, and `circuitrf explain` lists which conductors are in which terminal.

### Wave ports {#wave-ports}

A 3D port is **lumped** by default: a sheet from the line down (or up) to its return, with the port's
Z0 across it. A lumped sheet has a small series inductance of its own, and on a tall sheet it shows as
a step in S11 at the top of the band. A **wave port** has none: it is a region of the air box's face,
fed by the line's own mode, which Palace computes on that face at every frequency. Palace only; a setup
naming openEMS or both solvers is refused, and so is its `check`.

The **Ports** table (`Ports3D`) sets each port's kind by its number. Lumped and wave ports may be
**mixed** in one setup.

```
"Ports3D": [ { "Port": 1, "Kind": "Wave" }, { "Port": 2, "Kind": "Wave", "OffsetUm": 500 } ]
```

- **The line must run to the edge of the layout** on the port's side: the air box's padding on that
  side becomes zero and its face lies on the line's end. A line that stops short — because a plane, the
  board outline or another conductor reaches further — is refused, naming the port and the face.
- **Width** and **Height** size the region on the face, in line widths and in multiples of the line's
  height above its return. Blank takes the sizing rule: 10 line widths (10 substrate heights for a line
  narrower than its substrate) by 8 heights. The answer does not depend on them: ±20 % of the region
  moved |S21| on a 50 Ω microstrip by under 0.02 dB.
- **OffsetUm** moves the reference plane that far into the structure; Palace de-embeds the line between.
  `circuitrf explain` says where each port's reference plane is.
- **What the numbers are referred to.** Palace refers a wave port's S-parameters to the port's own mode
  at unit power — that is, to the mode's impedance, which changes with frequency (it is Palace's
  *Z_PV*, measured along a line from the return up to the strip). The Touchstone file states one real
  reference impedance per port, so circuitRF renormalises each wave port to its **Z0** and the file's
  header says so. On a 50 Ω microstrip the mode impedance is within a few ohms of 50; on a waveguide it
  is several hundred ohms, so set Z0 to the mode impedance the run reports if you want a matched file.
- **One mode.** A wave port excites and measures its first mode. If the port's region is large enough
  for a second mode to propagate at the top of the sweep, the run warns — its S-parameters then leave
  out the power that mode carries. Make the region smaller, or lower the sweep's top.

A wave port needs Palace's eigensolver (every Palace build has one) and GSLIB (the `+gslib` variant, on
by default). A build without either is refused before anything meshes, naming the variant.

### Eigenmodes {#eigenmodes}

**Problem** *Eigenmode* finds the structure's resonant frequencies and their Q — "is there a lid
resonance inside my band?". `Eigenmode` sets how many modes (`Count`, default 3) above which frequency
(`TargetGHz`, default the sweep's start). Palace only.

```
"Problem3D": "Eigenmode",
"Eigenmode": { "Count": 5, "TargetGHz": 8 }
```

- **f** is each mode's frequency and **Q** Palace's Q: the *loaded* Q, with every loss the problem has —
  finite-conductivity metal, lossy dielectrics, open (absorbing) faces, and each lumped port, which an
  eigenmode solve treats as its resistance, a load. With lumped ports, **Q_ext** is each port's own Q and
  **Q_unloaded** takes the ports' share out (1/Q_u = 1/Q − Σ 1/Q_ext).
- **Participation** is the fraction of each mode's electric energy in each meshed region: it is what
  says where a mode lives.

The result is `results/<name>.palace_eig.npy` and no Touchstone file; `circuitrf em` prints the mode
table and the panel shows it after a run. Palace's own files stay in the run folder.

## Letting circuitRF install the 3D solvers {#install-assistant}

circuitRF can install Palace, Gmsh and openEMS for you. It downloads each one from its own upstream
and installs it for your user account only. It never needs administrator rights.

There are three ways to start an install, and they all do the same thing:

- **Install …** on the message a 3D run gives when a solver is missing.
- **Install …** on the solver's row in {{anchor: settings.html#em3d|Settings ▸ 3D EM}}.
- `circuitrf solver install palace --yes` from a terminal, which is how a build machine does it.

**Nothing is downloaded until you agree.** First you see what will happen:

- the program and version;
- every web address it fetches from, and how each download is checked;
- the folder it installs into;
- how long it took and how much disk it used when it was measured, and on which machine;
- for Palace, its licence note.

**What each platform can install:**

| Program | macOS (Apple silicon) | Linux | Windows |
|---|---|---|---|
| Palace 0.18.1 | yes. It is built from source, which takes about an hour. | yes (arm64 and x64). It is built from source. | not yet |
| Gmsh 4.15.2 | yes, from Gmsh's own archive, in under a minute | x64 only. Gmsh publishes no Linux arm64 build. | yes, from Gmsh's own archive |
| openEMS 0.37.0-rc3 | yes. It is built by openEMS's own script. | yes. It is built by openEMS's own script. | yes, from openEMS's own archive |

**Some installs need tools you install first.** A source build needs a compiler and some libraries.
If one is missing, nothing is downloaded. circuitRF names the one command to run for your system, such
as `xcode-select --install`, `brew install …` or `sudo apt-get install …`, and you run it yourself.
circuitRF never runs `sudo` and never asks for your password.

**The install runs in the background.** Its progress is in the Messages panel. A Palace build shows
*package k of N* as each of its libraries is built. **Cancel** stops the install at any point.
Cancelling installs nothing, and the next attempt removes whatever the cancelled one left behind.

**An install is only reported as done once it has been checked.** circuitRF checks the installed
program the same way it checks one before a run: its version, and for Palace every capability a 3D
setup can need. Only then does the Settings row read *Installed by circuitRF*. From then on a 3D run
finds that copy before anything on `PATH`. A path you name in Settings, or in `CIRCUITRF_PALACE`,
`CIRCUITRF_GMSH` or `CIRCUITRF_OPENEMS`, still takes precedence.

**Where it goes.** Each program goes into its own folder, one per version:

- Linux: `~/.local/share/circuitRF/solvers/`
- Windows: `%LOCALAPPDATA%\circuitRF\solvers\`
- macOS: `~/.circuitRF/solvers/`. It is not in `~/Library/Application Support/circuitRF`, because a
  source build refuses to run in a folder whose path contains a space.

Each folder holds an `install.json`. It records what was installed, where it was downloaded from and
whether each download's checksum was verified. Palace comes with its own copy of Spack inside that
folder, so a Spack you already have is never read or changed.

**If an install fails**, the message names the step that failed and quotes the tool's own last error
lines word for word. It also gives the path of the full log. If circuitRF's own installs have met the
same failure before, the message adds what fixed it and where that was seen. Otherwise it says
circuitRF has not seen this failure. circuitRF never retries on its own, switches to another way of
installing, or patches anything.

**A Palace from conda is found too.** A Palace installed in a conda environment is found without any
Settings entry. circuitRF looks in `$CONDA_PREFIX` and in the environments of `~/miniforge3`,
`~/mambaforge`, `~/miniconda3`, `~/anaconda3` and `/opt/conda`. The same version check applies to it.

## Installing the 3D solvers by hand {#install-3d-solvers}

A 3D setup (one whose `Solver3D` names Palace or openEMS) runs a solver circuitRF does not include. You
install it yourself, and circuitRF finds it — or you name it in
{{anchor: settings.html#em3d|Settings ▸ 3D EM}}. **circuitRF runs only the versions it has validated**
and refuses any other, naming the validated ones, because a solver's input can change meaning between
versions and the result would look plausible either way.

| Program | Validated version | What it reports |
|---|---|---|
| Palace | 0.18.1 | `palace --serial --version` prints `Palace version: 0dc74cd` and `Schema version: 1-7-0` — a git hash, never "0.18.1" |
| Gmsh | 4.15.2 | `gmsh --version` prints `4.15.2` (the Homebrew build adds `-git`) |
| openEMS | 0.37.0-rc3 | `openEMS --help` prints a banner with `version 67d3784` and `CSXCAD -- Version: dcdb62b` |

Below is exactly what was done to validate them. **Only macOS on arm64 has been verified.** On
Windows and Linux the routes are *not yet verified*; use the upstream instructions linked with each
program, and check the Settings row afterwards.

**Gmsh** — macOS: `brew install gmsh`. Other platforms: *not yet verified* —
[gmsh.info](https://gmsh.info).

**openEMS** — macOS: install its dependencies with `brew install cmake boost hdf5 cgal vtk`, then

<pre><code class="cmd"><span class="prompt">$ </span>git clone --recursive -b v0.37.0-rc3 https://github.com/thliebig/openEMS-Project.git
<span class="prompt">$ </span>cd openEMS-Project
<span class="prompt">$ </span>./update_openEMS.sh ~/opt/openEMS --disable-GUI --python --njobs=4</code></pre>

circuitRF looks in `~/opt/openEMS/bin` by itself. (The validation build included the Python interface;
circuitRF never uses it.) Other platforms: *not yet verified* —
[openEMS-Project](https://github.com/thliebig/openEMS-Project).

**Palace** — macOS: built with Spack v1.2 from Palace's own recipe and environment file
(`docs/src/developer/spack/` in Palace's source at the `v0.18.1` tag, including its `setup-macos.sh`).
It took three corrections, each of which fails with a message that names the wrong thing:

| What you see | What fixes it |
|---|---|
| `[SSL: CERTIFICATE_VERIFY_FAILED] certificate verify failed` | Spack is running on a Python with no certificate store. Set `SPACK_PYTHON` to one that has one (Homebrew's `python3`). |
| `No such variant 'gkrand' in package metis` | Spack's package repository is older than Palace's recipe needs: `spack repo update -b develop builtin`, as Palace's own FAQ says. |
| `PETSc could not be found`, after `Illegal instruction`, about 17 minutes in | On an M4 Mac, Spack targets `m4` and the compiler then emits instructions the M4 cannot run. Add `packages: all: require: target=m3` to the environment and rebuild. |

State the variants in the spec (`+superlu-dist+sundials+slepc+libxsmm+gslib~arpack`) — without them
Spack silently turned three of Palace's defaults off.

**There is nothing to set up afterwards.** Palace's recipe installs with no Spack *view*, so nothing it
builds is ever on your `PATH`, and the environment is not loaded when circuitRF starts. circuitRF reads
Spack's own install record instead — in `$SPACK_ROOT/opt/spack`, `~/spack/opt/spack`, `~/opt/spack`
(the recipe's install tree) and `/opt/spack` — and finds Palace there by itself. Palace uses more than
one core through MPI's `mpirun`, and the same record says which MPI this Palace was built against, so
circuitRF uses exactly that `mpirun`. A different MPI's `mpirun` found on `PATH` can start the processes
and then fail to connect them, so that one comes later in the search. If your install tree is somewhere else,
name Palace (and `mpirun`, if you want more than one core) in Settings ▸ 3D EM. `which palace` and
`which mpirun`, run with the environment loaded, print the two paths. Without an `mpirun`, Palace runs
as a single process and the run's notes say so. Other platforms: *not yet verified* — see Palace's own
documentation.
**Palace does not run natively on Windows**; openEMS does.

<div class="callout note">
<span class="label">Palace's licence note</span>
<p>A default Palace build includes ParMETIS, whose licence allows commercial use for evaluation only.
If you build Palace, you accept those terms. circuitRF distributes no copy of Palace, so it passes on
none.</p>
</div>

When a row in Settings ▸ 3D EM reads *validated*, the program is ready. `circuitrf explain` on a 3D
`.cem` gives the same answer from the command line, under *solvers*.

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

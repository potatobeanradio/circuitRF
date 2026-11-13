---
title: EM Setup
slug: reference/em-setup.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > EM setup
lede: The EM Setup panel, control by control, and where the results land.
keywords: EM, electromagnetic, cem, ports, coaxial port, annular port, mesh, extraction, simulate layout, substrate
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
<li><a href="#ports">Ports</a>
  <ul><li><a href="#port-storage">Where a port is stored</a></li>
  <li><a href="#component-ports">Component ports</a></li></ul></li>
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
0.5–8 GHz linear sweep in 50 MHz steps, `Auto` analysis, and mesh settings that are the engine's own. On a
small structure the only thing you have to supply is which layout.

## The header: name, output name, layout {#header}

**Name** — the setup's display name, taken from the file stem. It is also what names the written result,
so two setups over one layout do not collide.

**Output name** — the name of the run's result, for every file it writes: the S-parameters, the far field
and field data, and the run's settings. The box shows what a blank name resolves to: the setup's name and,
in 3D, the solver's (`Setup.palace`), in the workspace's `results/` folder. A relative name is taken from
`results/`; an absolute path is used as given. **The suffixes are the run's to add**, so you type none: a
`.sNp`, `.npy` or `.cemr` you do type is removed, and the same structure re-run with a coupled pair goes from
`.s2p` to `.s4p` on its own. The `…` button browses; a path inside the workspace is stored relative so the
workspace stays portable. **A run replaces the result of the same name, so a new name is how an earlier
result is kept**, and each name has its own `_sim` or `_cosim` cell and co-simulation netlist. A result outside `results/`
is not in a Data Display's list of sources: choose **Add from file…** in a trace's source list to plot it.

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
| **Mesh** | Meshes and stops. No solve. This is the cheap "is my mesh sane?" answer — press it as often as you like. On a [3D setup](#mesh-3d) it makes the Gmsh mesh a run would solve on. |
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

**Analysis** chooses which kernel you are asking for. It sits in the **Solver** group, beside the solver
picker, and appears only while circuitRF's own solver is chosen:

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

Everything the extraction resolved goes in the **Notes** group below it, one full-width column.
**Cross-section** shows a readback — the propagation axis, the signal layer and the ground reference the
extractor resolved, plus a one-line summary of the conductors it found. Read it. It is the cheapest
possible check that the tool is looking at the structure you think it is.

The notes themselves collect everything the extraction had to say that is not an error: a shape that was merged, a
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

Default for a new setup: **0.5 to 8 GHz in 50 MHz steps (151 points), linear.**

**A frequency plan.** **+ Segment** adds a row, and the run solves every row's frequencies together, sorted, each
once. A row's **Single** button makes it one frequency rather than a sweep, which is what a new row starts as. So a
coarse broadband sweep, a finer sweep around the centre and a spot frequency are three rows, and an optimization
that only needs one frequency is one row on **Single**. The **×** on a row removes it; the last row stays. Every
solver takes the same list, the Touchstone file holds exactly those frequencies, and **Create SnP schematic**
sweeps them too.

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
  An edge port drives as much of its edge as its bar in the layout shows: the whole edge with **Use edge
  width** ticked, else its **Width** or 2 × H centred on it ([Ports](layout-editor.html#ports)). A port narrower
  than its edge is cut on mesh lines at its two ends, and its calibration feed is grown at its own width; where
  that feed cannot be grown, the port is reported at its cut, not de-embedded, and the notes name it.
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

### Where a port is stored {#port-storage}

**A port you draw lives in the document you draw it in. A component port is not drawn, and lives in the
setup that names it.**

| Port | Where it is stored | How you move it |
|---|---|---|
| A port label drawn on a layout | The `.clay` | Drag it in the layout |
| A port drawn in a 3D view | The `.c3d` | Drag it, or edit it in the Properties Inspector |
| A port a placed part declares ([TESTPAD](components.html#testpad) `Centre = Port`) | The part's own layout | Move the part |
| **A [component port](#component-ports)** | **The `.cem`, or the `.c3d`'s setup** | **Move the part** |

A `.cem` also stores *settings* for the drawn ports, by number: each one's reference impedance (`PortZ0s`) and, on
a 3D setup, its row of the port table (`Ports3D`). These are settings of a port, not the port: the position and
the type of a drawn port are the `.clay`'s, and a type chosen in the port list is written to the label. So one
layout can be measured at 50 Ω by one setup and at 75 Ω by another.

### Component ports {#component-ports}

<a id="component-ports-list"></a>A **component port** is a port across a placed part's own pads, with nothing
drawn for it. **Component ports** (`ComponentPorts`) lists every part placed on the setup's layout with its pad
count. It is there on a Planar setup and on a driven 3D one, in a `.cem` or in a 3D view's setup, and not on a
thermal, electrostatic or cross-section setup. A ticked part gets a port across its pads, numbered after the drawn
ports; **Every two-terminal part** writes `"*"`. **Check** builds and verifies the ports exactly as a run would and
posts each part's port and verdict to Messages, a part left out as a warning saying why; the list marks that part.
Each built port has a row in the port list, *Port 3 · C1*, whose Z₀, width and, for a part of more than two pads,
reference pin are written into that part's entry. In the file it is `"ComponentPorts": ["C1", "L2"]`,
`"ComponentPorts": "*"`, or entries that state more: `{ "Refdes": "U2", "Z0": "50", "Reference": "GND", "WidthUm": 600 }`,
an entry beside `"*"` overriding that part. An entry naming a part that is not placed keeps its row, marked,
and Check names it. While the `.cem` is open, the layout it names shows each of its component ports as a dashed
gap mark with its number; clicking a mark selects the part.

**On Planar, a component port is a gap port.** The planar solver cannot span the air between two pads, so it adds
a strip of metal across the part's gap, as wide as the two pads share, in the solved problem only, and cuts it at
the middle with an internal delta gap. The layout is not changed. The engine chapter has it in its own terms:
[Component ports](mom-engine.html#component-ports).

**On a 3D setup** it is a lumped gap sheet between the two pads, built as
[A port across each part's own pads](em-3d.html#part-ports) describes. A 3D `.cem` builds it in the 3D view
**New 3D View from Layout** would make of its cell, in memory, so its `.clay` must be saved in a cell as that
cell's layout. Nothing is written.

**Into a 3D view.** New 3D View brings in the component ports of each `.cem` on that layout. After that, the two are
separate: changing the parts in one leaves the other alone. A 3D `.cem`'s list goes into its copied setup. A Planar
`.cem` with component ports becomes a 3D setup of its own, named after it, with its sweep and its list, each entry's
Z₀, width and reference included; the drawn ports' settings stay in the `.cem`. Each part is checked again in 3D, and a
part that cannot be a 3D port is left out there with a warning.

**Planar and 3D are not promised to match.** They are different solvers, and their answers for one board and one set
of component ports differ, more as the frequency rises. Treat them as two models of the board, not as one checking
the other.

**Limits.**

- A part whose pads do not face each other along x or y is left out, and so is a part placed at an angle that is not a
  multiple of 90°.
- A Planar run refuses a board whose vias are farther apart than 0.1 λ at the top of the sweep, unless the
  [vertical (via) kernel](#solver) is on. Boards with ground vias reach this often.
- A via anywhere in a Planar problem stops its radiation pattern; the run says so, and its co-simulation netlist reads
  the Touchstone instead ([A planar result](cosimulation.html#planar)).
- With a **Solve region**, a part whose pads are not all inside it is left out with a warning naming it, on a Planar
  or a 3D `.cem`. A drawn port outside the region is still refused.
- The marks appear on a layout only while a `.cem` naming component ports is open.

A run with component ports writes `<setup>_cosim.cnl` and, with **Create SnP schematic** ticked, a `_cosim` cell:
[A schematic to start from](#snp-schematic).

**Each part's port is one more port of the result.** In the Touchstone each one is terminated in its Z0 when you read
S directly, so a series part's pads are bridged by 50 Ω: S(2,1) across a series capacitor reads a few dB, where the
board with nothing fitted reads tens of dB down. The Data Display a run opens plots the board as it is drawn instead, with
every part's port open: the `PartsOpen` group of `<name>_em.npy`, which holds `S` and `Z0` over the other ports. A
connector's port is a real port and stays in it. To see the board with its parts fitted, run the `_cosim` netlist.

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

With **Auto** on, the mesher chooses cells per wavelength, edge mesh and edge cells itself and those
three fields are not read. Every other field in this group is still used. A hand-written `.cem` that
states one of the three with `"Auto": true` gets a `check` warning naming it; set `"Auto": false` in
`PlanarMesh` for it to take effect.

Below the fields sits the **mesh summary** — unknown count, cells across the narrowest conductor, and the
mesher's own verdict on whether that is enough — plus the mesh notes. This is the readout that tells you
a run is about to be too big *before* you start it.

## Solver options {#solver}

Eight switches. Four of them either change how the same answer is computed or trade time for memory, and
change nothing about what is being solved. **Conductor thickness** changes the geometry the solver is
handed, and so changes the answer, and **Edge ports** changes what an edge port's source is connected to. The two port-de-embedding switches are different in kind and are
described last: one of them changes the answer, and the other publishes an answer circuitRF would
otherwise refuse to write.

**Conductor thickness — "Model copper thickness"** *(on in a new setup)*. The full-wave solver treats metal
as a **zero-thickness sheet**, so a thick strip's sidewalls, and the fringing capacitance they add, are
missing and Z₀ reads **high**. With this on, every outline on a level whose copper has a thickness grows
by the width at which a sheet has the **same Z₀ as the real strip**. That width is solved with the
cross-section kernel, which models real metal thickness, on your technology's own stackup, at the port
width (or a typical width of the artwork when the level has no port). **Port edges stay exactly where
you drew them**, so no reference plane moves. Gaps between conductors on that level narrow by the same
amount, which stands in for the extra sidewall coupling of thick metal. A level where growing would join
two conductors, or close a slot inside one, is left as drawn and the run warns you. The run's notes give
the width added and the impedances it was matched on. One width cannot match ε_eff as well: a thick
strip's sidewalls are partly in air, so its ε_eff is slightly lower, and the note reports both values.
A setup file that does not mention this option runs without it, so its results stay exactly as they
were when it was written.

<div class="callout note">
<span class="label">How much it moves</span>
<p>On a 25 µm copper, ~50 Ω line the sheet read about 1 Ω high, and this brought the full-wave answer to
within 0.1 Ω of the cross-section kernel's thick-metal value. If your full-wave Z₀ disagrees with another
tool by much more than that, check the <b>stackup heights</b> first. The height between the line and its
return plane matters far more than thickness. Copper layers between them that carry no artwork count as
dielectric at their full stated thickness.</p>
</div>

**Edge ports — "Ground-referenced edge ports"** *(on by default)*. An edge port's source sits between the
**ground plane** and the end of the strip, which is what an RF port on a microstrip is. The port then adds
only a small series inductance and shunt capacitance — tens of picohenries and femtofarads on a 50 Ω
laminate line — and the calibration removes them at every frequency, so the answer stays accurate down
to the bottom of the sweep and does not move when you refine the mesh. Turned off, the port is the older
**series gap** against the outermost cell of the strip. Its error grows as 1/f below a few GHz and gets
**worse** as the mesh is refined: on a 5 mm, 50 Ω line it published a characteristic impedance of about
1 Ω at 100 MHz where the line is 51.7 Ω. Leave it on unless you are reproducing an earlier result.
The ground-referenced port is used below the point where the substrate stops being electrically thin
(the run's quasi-static crossover — about 16 GHz on a 0.37 mm laminate, 3 GHz on 1.6 mm FR-4) and the
series gap above it, where its low-frequency problem is gone; the run's notes say where it switched.
A run whose ports are calibrated together as a **coupled group**, or whose calibration standard has to
reproduce a **neighbouring conductor** beside a feed, uses the series gap for every port and says so in
its notes; neither calibration has yet been extended to the grounded port. In the
`.cem` the setting is `"EdgePortReturn": "SeriesGap"`, written only when it is off.

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

A successful run writes these files under its [output name](#header), in the workspace's `results/` folder
unless that name says otherwise:

- **The Touchstone `<name>.sNp`**. This is the artefact you point a schematic's
  [SnP component](components.html#snp) at — that is the whole co-simulation route, and it is described
  in [Using EM results in a circuit simulation](mom-engine.html#cosim). Its header is stamped with the
  stackup, the mesh settings, the port definitions and a hash of the geometry, so a stale file beside an
  edited layout is *reported*, not silently trusted.
- **The `<name>_em.npy` dataset**, carrying what Touchstone cannot: the per-kernel diagnostics group — Z_c, γ,
  ε_eff, attenuation and per-unit-length RLGC for the cross-section kernel, and the calibration's own
  residual and usability flags for the full-wave one. Open it in the [Data Display](data-display.html)
  like any other run. A run with component ports adds the `PartsOpen` group: the other ports'
  S with every part's port open ([Component ports](#component-ports)).
- **The field set `<name>.fields/`**, when the run keeps per-port fields.
- **The result record `<name>.cemr`**, which says how the result was made: the whole setup as this run used
  it, the ports, the sweep, the solver and the mesh. `circuitrf explain <name>.cemr` prints those settings and
  what differs from the setup now, so they can still be read after the setup is edited. A result from before
  records existed gets one with `circuitrf em --adopt <name>.sNp`, whose settings read as not recorded.

**A run that does not finish changes none of them.** Everything is written under staging names and renamed
into place at the end, so a cancel, a solver that fails or a full disk leaves the previous result whole. A 3D
view's saved field plots are the exception: they live in the solver's own run folder, which a new run clears
when it starts, so one of the previous run's may not survive a run that fails.

<div class="callout warn">
<span class="label">One flat results folder</span>
<p><code>results/</code> is shared with schematic runs and is keyed by name. An EM setup named after a
schematic that also writes results will collide with it — both resolve to the same stem. Give the setup
its own name, or set its output name.</p>
</div>

### A schematic to start from {#snp-schematic}

Tick **Create SnP schematic when the run finishes**, under *After the run*, and a driven run also makes a
cell named after the setup with `_sim` on the end. Its schematic holds:

- an `SnP` block whose `File` is the run's Touchstone, workspace-relative;
- a `TermG` on every port, numbered as the port and at the impedance the Touchstone is referenced to,
  placed on the side of the block its port faces in the layout and turned to face out;
- an S-parameter analysis over the setup's sweep;

and its [Data Display](data-display.html) plots dB of the through paths, S(2,1), S(4,3) and so on, and
of every input match. Messages says *Created cell …* with **Open schematic** beside it. Simulate it as it
stands and it gives back the run's own S-parameters; break a wire to put the parts you are studying
between a port and its `TermG`.

A cell of that name is **never overwritten**. On a re-run it already reads the same file, which the run
has just rewritten, so it picks up the new result unchanged. If the port count changed, its `SnP` is
pointed at the new file and Messages says when its `TermG`s no longer match. A cell of that name that
reads something else is left alone, and Messages says so. `circuitrf em` honours the same tick.

A run with **component ports** makes a `_cosim` cell in place of the `_sim` one, because a `TermG` on a part's port would have no part on it: each part
stands on its own port, with the analyses and a Data Display. [Co-simulation](cosimulation.html#pattern) describes it.

#### Your own schematic, with the EM result in it {#snp-schematic-parts}

Tick **Include pre-existing schematic components** as well, and the cell is made from the **Primary
schematic of the cell the layout or 3D view belongs to**, with `_sch` on the end, in place of the `_sim`
or `_cosim` cell. It is your schematic with the board's EM result put into it:

| In your schematic | In the new cell |
|---|---|
| Lines, tees, bends and other parts the layout draws as copper | Removed. The SnP block goes where they were |
| A Port whose `Num` is an EM port's number | Where you put it, joined to that SnP pin |
| A cell Pin whose `Num` is an EM port's number | The same: a cell made to be placed in a test bench marks the board's ports with Pins |
| A part on a component port | Where you put it, with its value and size. One pin goes to its SnP pin and the other to ground, which is how a gap port across its pads is closed |
| A part with a footprint but no component port | Kept, its pins left open: the EM result has no port at its pads |
| A part that is not on the board (an ideal source, a model with no footprint) | Kept, with its own wires. A pin that was joined only to removed copper is left open |
| An EM port with no Port of its number | A new Port beside its SnP pin |

Connections to the SnP block are net labels, `p1`, `p2` and so on (`em_p1` when your schematic already
uses those names), each on a short wire drawn where it touches nothing else on the sheet. A part pin that
goes to ground and already sits on one of your ground symbols keeps it. Your analyses, variables, measures and goals come with it, so a harmonic-balance or
loadpull analysis runs on the EM board as it is. An analysis set to run by EM runs by circuit models
here, since the SnP block already is the EM result. Your schematic's Data Display comes too, reading
the new cell's results. A schematic with no S-parameter analysis gains one over the setup's sweep, unless
its board ports are Pins: that cell is placed where the original is placed, so it takes a copy of the
original's symbol instead.

Every pin left open and every unmatched Port is named in Messages. Before the cell is made, the layout
is compared with the schematic ([LVS](lvs.html)), and each difference that could put a part on the wrong
pin is reported: parts are matched to their component ports by name, so a part renamed on one side only
would otherwise be wired wrong without a word. The cell is still made. With no Primary schematic in the
cell, the ordinary cell is made and Messages says why.

#### Create now {#snp-schematic-now}

**Create now**, beside the check box, makes the same cell without a run, for a run that finished with the
box clear. Its SnP block reads the setup's existing Touchstone. With none yet, the cell is still made:
its block names the file the run will write, and Messages warns that the file does not exist. Include
pre-existing schematic components applies to Create now whether or not the run's box is ticked.

If the cell already exists, Create now asks. **Replace** writes the new schematic over that cell's
schematic, leaving its other views and its Data Display as they are; close the schematic first if it is
open. **Create** makes a cell under the name you type, which starts as the first free one (`board_sim_2`).
A run never asks: it leaves an existing cell alone.

Without the tick, place an [SnP component](components.html#snp) yourself and point its `File`
parameter at the written path. Re-running updates the file in place, so the schematic picks the new
result up the way it picks up any changed source.

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

*When 3D is the right tool, how to get the solvers, the 3D view, and a worked example to open are in
[3D EM](em-3d.html). How the planar, FEM and FDTD solvers differ, and which suits which problem, is in
[EM Solvers: MoM, FEM and FDTD](em-solvers.html). This section is the panel's 3D controls, one by one.*

The **Solver** group at the top of the panel picks who solves the setup: circuitRF's own planar and
cross-section kernels (the default), **FEM 3D (Palace)**, **FDTD 3D (openEMS)**, **FEM 3D (EMerge)**, or
**Compare…** — any two or more of them on one model ([Compare](#compare)). A solver that is not installed, or that
cannot solve this setup, is greyed out; its tooltip says which. A 3D setup builds a 3D model from the layout, its technology and any bond wires. The
settings a 3D run does not read — the surface mesh, adaptive sampling, the port type and the planar
solver options — are hidden while a 3D solver is chosen; they stay in the
`.cem` and come back when you switch to Planar. So is the **Conductors** group: a 3D port finds the plane it
returns through in the layout itself (see [Return](#port-return)). What a 3D run does read stays: the frequency sweep,
each port's Z₀ and kind (set in the Solver group's port table), the solve region, the core count
and the **radiation pattern**, which a driven 3D run produces too — see
[Antennas ▸ From a 3D solver](antennas.html#3d). Palace, openEMS and EMerge each have their own section below, and a
setup keeps them all, so switching solver never loses another's settings. A comparison shows the section of every solver
it runs, each titled with the solver's name.

<a id="conductors-3d"></a>**Conductor bands** (`Conductors` in the `.cem`, omitted at `Auto`) say what a 3D setup makes of
each conductor. `Auto` takes the sheet rule, a band thinner than three skin depths and a tenth of its width being a sheet.
`Sheets` makes every band a sheet, the planar solve's own idealisation. `Solids` gives every band its real thickness. A
[resistive film](stackup.html#film) stays a sheet in all three, because a film's own thickness is a mesh nobody should be
asked to pay for.

Each 3D solver is told about a film in the way it solves accurately, with the same sheet resistance. Palace takes a thin
conducting boundary. openEMS takes a conducting sheet at a thickness large enough that its model does not bypass the film,
and a grid at least six cells across it. EMerge takes a thin lossy volume. The run's notes state the values each one used.

<a id="mesh-frequency-3d"></a>**Mesh frequency** (`MeshFrequencyGHz`), below the solvers' sections on a driven Palace or EMerge
setup, is the frequency the mesh is sized at. Blank, which writes nothing, sizes it for the top of the sweep. The solve still covers
the whole sweep, so this changes where the accuracy goes and not what is solved: in a power amplifier the S-parameters matter most at
the fundamental and the fifth harmonic only has to be reasonable, so mesh at the fundamental and sweep to the fifth harmonic. The mesh is
far smaller, and the harmonics are solved with fewer elements per wavelength; the run's notes say how many times longer they are at
the sweep's top. The dry run's estimate follows it. The `.sNp` header records it, and it is stored in the `.cem` and in a `.c3d`'s
embedded setup. openEMS's grid stays sized at the sweep's top, which it needs for its time step.

<a id="robust-meshing"></a>**Robust meshing** (`RobustMeshing`), below the solvers' sections, applies to every 3D solver,
because they all mesh the same outlines. It removes each outline point that lies within a fraction of a micrometre of
the line through its neighbours: at most 0.5 µm, or 1/50 of the layer's thickness where that is less. Artwork imported
from Gerber files often has such points — arcs broken into short segments that end in a stub of a micrometre or so,
and tiny edges left where shapes were merged — and they can make the mesher build a flat element that stops the run.
No outline moves by more than the tolerance, and the run's notes say how many points were removed and the largest
move. A conductor point that close to the board's edge is moved onto it, and a conductor's side that lies along the
board's edge stays on it, so a plane cut at the edge never ends a fraction of a degree off the board's side, a sliver
the mesher refuses as "overlapping facets". A hole with a corner exactly on its own pour's edge, such as a round hole
tangent to it, has that corner moved into the hole by the same tolerance, because the mesher cannot cross a face's edge
at a point it does not share; the notes say how many. Only conductor outlines change; via barrels, the board outline and slabs
are meshed as drawn. It is on unless a setup says `"RobustMeshing": false`, which is the only value ever written: a
`.cem` or an embedded setup without the field runs with it on. Off, the artwork is meshed exactly as drawn. A port
on a placed layout is inferred on the outline robust meshing leaves, by `check` and by the run alike, and an edge that
touched a point the cleanup moved moves with it; the port's line in `check` says by how much.

<a id="thin-metal-3d"></a>**Thin Metal Correction** (`ThinMetalCorrection`), below Robust meshing on a driven Palace or EMerge
setup. Neither solver meshes the inside of a conductor: its faces carry the impedance of metal thick against the skin
depth. Under about three skin depths that reads too little resistance and too much inductance, because the current
really fills the metal. With the correction on, a conductor under three skin depths thick at the lowest frequency solved has
its faces given its thickness, h = 2A/P for a section of area A and perimeter P, and its resistance levels off at the DC
value. The mesh is the same either way. It is on unless a setup says `"ThinMetalCorrection": false`, which is the only
value ever written. openEMS has nothing to correct, because it treats solid metal as a perfect conductor.

The correction assumes the two faces carry equal current. Under a line close to ground they do not, so **a wide line reads
high**, and near two to three skin depths **any line can read low**. Resistance as a ratio to a 2D reference, on EMerge with a
refined mesh, for a 300 µm line of 1 µm aluminium 10 µm over ground, corrected and then uncorrected:

| Width | 0.1 GHz | 0.45 GHz | 2 GHz | 9 GHz | 40 GHz |
|---|---|---|---|---|---|
| 5 µm | 1.00, 0.05 | 1.00, 0.11 | 1.00, 0.24 | 0.99, 0.48 | 0.82, 0.75 |
| 50 µm | 1.02, 0.07 | 1.13, 0.16 | 1.18, 0.32 | 1.10, 0.61 | 0.85, 0.86 |
| 200 µm | 1.09, 0.08 | 1.36, 0.18 | 1.39, 0.38 | 1.26, 0.71 | 0.82, 0.83 |
| 500 µm | 1.12, 0.09 | 1.43, 0.19 | 1.47, 0.40 | 1.32, 0.75 | 0.80, 0.82 |

Palace agrees: 1.00 to 1.01 up to 9 GHz and 0.82 at 40 GHz at 5 µm, 1.41 at 2 GHz at 200 µm. On its Standard preset as it comes, with
refinement passes and the adaptive sweep, the 5 µm line reads 1.00 to 1.01 up to 9 GHz and 0.86 at 40 GHz, with its inductance 5 % low. The overshoot follows the line's width over its height above ground: at 0.5 it is about 1 %, and at 3 to 4 it is under 10 % on
3 µm metal. Thicker metal also reads low sooner: a 100 µm line of 3 µm aluminium 25 µm over ground reads 0.88 at 2 GHz and 0.76 at 9 GHz,
against 0.66 and 0.82 uncorrected. So below about 2 GHz the correction is the closer answer on every line measured, and from about
9 GHz up it can be a little further off than leaving it out. The inductance of a line this wide also reads high at the bottom
of a sweep, 1.63 of the reference at 0.1 GHz on the 500 µm line against 1.19 uncorrected, where the 5 µm line goes the other way, 0.99 against 1.74.

Drawing the line as a sheet, through [Conductor bands](#conductors-3d), does different things on the two solvers. On EMerge, the same
1 µm lines as sheets read 0.97 to 1.00 up to 9 GHz at widths from 50 to 500 µm, and 1.09 to 1.39 at 40 GHz, so a sheet is the closer model for a
wide line there; at 5 µm it reads 0.90 with its inductance 18 % high. On Palace a sheet's two sides divide the current as a solid's faces
do, so a wide sheet reads as high as the corrected solid, 1.46 at 2 GHz at 500 µm, and a 5 µm sheet reads 0.99 to 1.01 at every frequency
above, 40 GHz included. `Auto` makes a band a sheet when it is thin at the top of the sweep and under a tenth of its width.

The correction costs no memory to speak of and leaves the mesh alone. On Palace the linear solves take about a quarter more iterations, which made a
run about 15 % longer on that 5 µm line. On EMerge the difference does not show.

A conductor the correction leaves alone keeps a warning: on an eigenmode problem, with the setting off, or across a sweep so wide
the solver's own expression would overflow, more than 700 skin depths thick at its top. A kernel solid or a sphere has no single thickness
to use, and gets neither the correction nor this warning.

**Component ports** (`ComponentPorts`), below Robust meshing on a driven 3D setup, is the list
[Component ports](#component-ports) describes; on a 3D setup **Check** also posts each port's landings.

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
| Refinement tolerance | `AdaptiveTol` | 0.01 | Palace's own `Refinement.Tol`: the relative error in the field, from Palace's error estimate over the whole model, at which it stops refining the mesh. It is not a ΔS — no S-parameter is compared between passes |
| Refinement passes | `AdaptiveMaxIterations` | 2 | The most refinement passes; 0 solves the starting mesh only. Each pass costs a solve and memory |
| Sweep tolerance | `SweepAdaptiveTol` | 0.0001 | The tolerance of Palace's adaptive frequency sweep; 0 solves every frequency |
| Linear solver | `LinearSolver` | `Iterative` | How Palace solves each linear system. *Iterative* (GMRES with multigrid) needs the least memory. *Direct* factors it with SuperLU_DIST: more memory, and far faster for an **eigenmode** solve, whose shifted operator an iterative solver converges on slowly. On the 3D Package example's lid mode (128 k unknowns) Direct took 35 s and 4.0 GB; Iterative had not finished after ten minutes. Not part of any preset |
| Curving limit (min) | `MeshCurvingMinutes` | 5 | How long Gmsh may curve and optimise second-order elements on genuinely curved geometry (a cylinder, sphere or curved part in a 3D view, a round bond wire) before the mesh is made again with straight sides; the run says when it was. A layout's own geometry, whose only curves are its drilled via barrels, is always meshed straight-sided and never waits for this |
| Save fields at (GHz) | `SaveFieldsGHz` | the sweep's centre | The frequencies, GHz, whose fields Palace saves for the 3D view; `[]` saves none. See [Fields for the 3D view](#palace-fields) — they are large |

The settings above size only the **starting** mesh; Palace's adaptive refinement adds elements where its
error estimate says the answer needs them. Simulate then runs Gmsh on the model and Palace on the mesh,
using the same *Cores* setting as the planar solver (as MPI processes). A 3D result is named after its
solver — `results/<name>.palace.sNp` and `<name>.palace_em.npy` — so it never replaces a planar or an
openEMS result, and the folder `results/<name>.palace/` holds everything the run made (the geometry
script, the mesh, the Palace configuration, both programs' logs). An unchanged model reuses its mesh, and a run that changes only a frequency, a mesh size or a quality
reads the geometry it built last time (`built-*.brep`) rather than building it again.

Before it solves, circuitRF checks that every surface in the mesh belongs to exactly the object it was
made for — each port sheet, each conductor, each face of the air box. **Any mismatch refuses the run,
naming the object**; a boundary is never guessed onto a face. It then reads the mesh itself: a tetrahedron with no
volume or turned inside out, or a surface triangle that is no tetrahedron's face (a sheet the mesher did not join to
the volume around it), refuses the run with one sentence per place — how many, the objects they lie on, and where in
millimetres — instead of a solver error that names nothing.

**Will it fit?** Palace's memory is checked against the machine's twice: before Gmsh starts, from the
model's volumes and the refined shell round each conductor and port, and again once Gmsh has reported how many tetrahedra it made, before Palace starts.
The second check is the one that counts on a bond wire or another small curved conductor, where the
refinement its curvature asks for is nearly the whole mesh and the first check says almost nothing. Past **75 %** of
the machine's memory the run carries a warning naming the estimate and what would shrink it — the
Draft preset, first-order elements, no refinement passes, a smaller air box, fewer MPI processes, a
smaller crop — each with the estimate it would give, and saying which of them fit. Past
**100 %** Simulate asks before going on: run with a remedy that fits, such as fewer MPI processes, which keeps the memory
limit; **Simulate Anyway**, which lifts it and lets the run swap; or don't run. The question says how much the run may swap and how
much room the startup disk has for it. Headless, `circuitrf em` warns and runs with its limit, needs `--force` past **150 %**, and
`--force` lifts the limit anywhere past 100 %. A warning never stops a run on its own: the estimate uses the highest memory
circuitRF has measured for a run of that size.

**A setup on the Direct linear solver is checked as Direct**: it needs about two to three times the
iterative solver's memory (on a four-layer via transition, 6.2 GB against 3.0 GB at 193 k unknowns). When
Direct will not fit and Iterative would, the question before the run offers it. Its default
button is **Use the Iterative Solver**, which runs this once on Iterative without changing the setup;
**Simulate Anyway** keeps Direct. Headless, `circuitrf em` prints the same warning and runs as the setup says.

**Fewer processes is another remedy a run can take by itself**, without changing the setup: every MPI
process carries its own copy of the solver's preconditioner, so Palace's peak climbs with each doubling
of the count — on a ten-layer board, 2.5 GB on one process against 4.3 GB on eight, at about twice the
time. When a smaller count fits, the question's default button is **Run on N Processes**, which runs
this once on that many (Settings ▸ Solvers' *Cores* is unchanged). The crop is sized: the share of the
current crop's area that would fit, made with Crop to New Cell. **When no setting fits**, the question
says so plainly — only a smaller crop would — and its default is **Don't Simulate**.

**A 3D run's notes appear as they are known** — which solver and version, where each port was placed,
every grid merge, each warning — not when the run ends. A run that is cancelled posts nothing after the
cancel line but the line saying it stopped. `circuitrf em` prints them to the terminal the same way.

**While it runs**, the progress row names Palace's own stages, from its own log: *Meshing (Gmsh)*;
*Solving: refinement pass k of N* with the unknown count; *Adaptive sweep, port i of n: model change e (done
below t)* — how much the frequency model still changes when a new frequency is solved, falling as samples are
added until it is below the tolerance (the bar is that convergence on a log scale, not a share of the work —
the number of samples is not known in advance); *Sweep: evaluating frequencies* with a count; *Reading
results*. A multi-port setup samples and sweeps each port's excitation in turn, and the row says which. At a minute
or more a frequency the row also says the time each one takes, and from the second port on about how long the rest will
take at that pace. An eigenmode run shows *Eigenmode search: linear solve k*, then *Eigenmodes: n found, refining i of n*
with the last refined mode's frequency and Q, and the solver iteration of the solve under way.

`palace.log`, `gmsh.log` and `emerge.log` in the run's folder carry circuitRF's own lines, beginning `[circuitRF` and the
date, time and UTC offset: when the program started, every five minutes while it runs, and how it ended, with its exit code. Pressing
Stop or reaching a limit is written before the program is stopped, so a last line such as `setsid: child 1054 did not exit
normally` after that line is the stop, not a crash.

A port that reaches Palace's limit on solved frequencies before its model settles gets a warning naming the port and
the last figure, and the run is marked not converged; a Sweep tolerance of 0 solves every frequency instead.
Beside the stage is the memory Palace's processes are using, shown once a second; where part of it has been swapped
out or compressed, the larger figure that counts it is shown beside it. If Palace prints
something circuitRF does not recognise (a Palace version it has not been validated against), the row
falls back to a count of log lines, with no bar, and the run's notes say so. A 3D run cannot finish
early and keep what it has, so its running button reads **Cancel**, not Stop.

**A memory limit and a disk floor hold every 3D run while it runs** — Gmsh, Palace, openEMS, EMerge and the short
checks they make, whichever solver. The checks before a run are estimates; these are measurements, taken ten times a
second, of everything the solver's processes hold, including what the operating system has swapped out or compressed.

- **The memory limit is this computer's memory**, or the lower limit set in [Settings ▸ Solvers](settings.html#em3d)
  (`--memory-limit` for `circuitrf em`). A solver that reaches it is stopped, with all the processes it started, and
  the run writes nothing. Stopped while meshing, the message says the mesher most likely ran away on one curve and
  points at the log that names it; stopped while solving, it says the problem is larger than this computer and lists
  what would shrink it. Past 90 % of the limit the run carries a warning.
- **The disk floor is 10 GB free** (5 % of a smaller volume), on the startup volume, where the operating system swaps,
  and on the volume the results are written to. Below it the run is stopped the same way, and a run that would start
  below it is not started. The message names the volume and the free space left.
- **A run you start anyway when the size check asks** (or with `--force`) has no memory limit, since you chose to let
  it swap, but it keeps the disk floor.
- **On Windows the limit is held by the operating system itself**, so a sudden burst cannot overshoot it. Elsewhere
  it can overshoot by the memory taken between two readings, which is why it sits at the computer's memory and not
  above it. **A Palace in the Linux subsystem** is held by the subsystem's own memory setting rather than this
  limit; the disk floor still applies.

**When it finishes**, one line says what it cost — wall time, Palace's own peak memory, the tetrahedra
it started and finished with, the unknowns, the refinement passes, the sweep's samples and the preset —
and the `.sNp`'s header records the same (`circuitRF-EM 3D run:` and `circuitRF-EM 3D run cost:`).
Palace runs one MPI process per **physical** core by default; a *Cores* setting above the physical count
is refused rather than oversubscribed. On a GPU it runs one process per GPU instead
([Palace on a GPU](#palace-gpu)).

**The DC point.** Start the sweep at 0 Hz and the file's first row is a DC conduction solve of the conductors, by
circuitRF rather than by the 3D solver, the same rule as the [planar solver](mom-engine.html#adaptive) follows. It
models each conductor's resistance, a perfect conductor as a short and a lumped R, L or C as a resistor, a short or an
open; it does not model leakage through a dielectric. A series DC block is therefore an open at 0 Hz and a choke a
short, which is what a circuit's bias point needs. It meshes the conductors with Gmsh, so it needs Gmsh even for an
openEMS run. Without Gmsh the run still succeeds, the file has no 0 Hz row and the run's warning says why: a circuit's
DC solve through that file then uses its lowest frequency. The `.sNp`'s header says which it was (`circuitRF-EM 3D DC
row:`), including what each lumped element became.

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

Lines also merge when they are closer than **a quarter of the local cell** they sit in — the cell the
thirds rule chose at that edge — which is far wider than MinCell on a board. Imported layers routinely
land a few microns apart: a ground pour's edge on one layer and the same pour's edge on the next each
get their own thirds pair, and two of those lines a micron apart would set the time step of a 2.4 GHz
run a hundred times smaller than the board needs. Such lines become one line, and the merge is
reported like any other. A thirds line moves onto a feature's own line (an edge, a face, an extreme)
rather than meeting it halfway, since the pair is a refinement and the feature is the geometry; an
extreme of a round or diagonal pad or pour outline, which the grid staircases anyway, moves onto a
feature beside it in the same way. What is never merged this way: one conductor's two sides (a strip's
width, a slot in a pour), and two lines neither of which has a thirds cell, such as a via barrel's
wall or the planes of a stackup — those keep MinCell.

| `.cem` field (in `OpenEms`) | Default | What it does |
|---|---|---|
| `CellsPerWavelength` | 20 | The largest cell, as cells per wavelength in the densest material it passes through, at the top frequency |
| `GradingRatio` | 1.3 | The largest ratio between two neighbouring cells |
| `ThirdsRule` | true | The thirds rule at the edges of strips and other thin metal; false puts one line on each edge, as a hand-built model often does |
| `MinCellUm` | a tenth of the smallest metal width or thickness | Lines closer than this, in micrometres, are merged — and reported |
| `PmlCells` | 8 | Uniform cells added outside each absorbing face |
| `MeshRegions` | false | A 3D view's mesh regions also cap the cell: no cell crossing a region's extent on an axis is larger than its size |

**A curved conductor needs a mesh region.** A round or diagonal edge places no line of its own, so a printed ring is
gridded at the wavelength's cell and a narrow ring can fall between lines. Draw a mesh region over it in the 3D view (its
`MeshRegions`, with a size a third of the narrowest trace or so) and set `MeshRegions: true` in the setup's `OpenEms`
section. It is off unless set because a mesh region is usually drawn for Palace or EMerge, and a fine one honoured along
a whole axis would multiply an openEMS grid; a region also refines the Gmsh mesh of every Palace or EMerge setup of the
view. The **Loop Sensor** example's rings use one.

`circuitrf explain` on the setup prints the grid before anything runs: lines per axis, the total
cells, the smallest cell **and the features that set it**, the time step it allows (an estimate —
openEMS computes its own), the steps and memory the run would take, and every merge. A grid that would
not fit in memory is refused, naming the feature behind the smallest cell and the setting that would
relax it.

### A cylindrical grid {#openems-cylindrical}

A round conductor on a rectilinear grid is a staircase, and a staircased coax carries its wave slowly: the
3D Connector's coax (pin ⌀ 0.4 mm in a 1.34 mm PTFE bore) measured 1 to 2.4 % slow in phase velocity
depending on the cells across its pin, about 1° over a few millimetres at 10 GHz. A 3D view's own setup can
ask openEMS for a **cylindrical grid** instead, whose lines are circles about one axis, spokes from it and
planes along it: a conductor coaxial with that axis then lies exactly on the grid's own circles.

| Field (in `OpenEms`) | Default | What it does |
|---|---|---|
| `Grid` | `Cartesian` | `Cylindrical` asks for the grid below. Never inferred: every existing setup stays Cartesian |
| `Axis` | `Z` | The world axis the grid is round about |
| `AxisOriginUm` | `[0, 0, 0]` | A point the axis passes through, x, y, z in µm: the centre of the coax's pin |
| `AzimuthLines` | the rule below | Lines round the circle, 0 and 2π both counted |
| `MultiGridRadiiUm` | none | openEMS's sub-grids: radii in µm, increasing, inside each of which openEMS takes every second azimuth line |

In the setup panel the openEMS section offers **Grid**, and with *Cylindrical* chosen, **Axis** and **Axis
through**. A `.cem` setup cannot ask for one (a layout problem stays Cartesian) and is refused if it does.

**What suits it.** A problem round about one axis: a coax section, a coax step or bead, a coax-to-coax adapter,
a pin in a round bore. **A coax meeting a board does not**: openEMS has one grid per run, and around the coax's
axis the board and its line would be staircased instead. The 3D Connector's **Launch** therefore stays Cartesian
and keeps the coax's error, about 1 % in phase velocity over its 4.5 mm of coax — roughly 1.4° at 18 GHz. A
Cartesian run with a coaxial wave port says so in a note, with the setting that removes it when it can be
removed, or why it cannot; `circuitrf check` suggests the setting when every wave port is coaxial about one line.

**The domain** is the cylinder inscribed in the air box's cross-section about the axis. The box's two faces the
axis crosses are its ends; its four side faces become one outer radius and must share one boundary kind. A
dielectric reaching past that radius is refused; metal may cross it, as a shield ending on the wall does.

**Where ρ starts.** At the surface of a metal solid that contains the axis along the whole box — a pin — so the
grid wastes no cells inside it. With none, it starts on the axis itself and openEMS treats the axis: correct,
and slow, because the innermost ring's tiny arc sets the time step. Measured on the same coax: identical
impedance and phase, at a time step 25 times smaller.

**How many azimuth lines.** The impedance error is first order in the azimuth step and does not depend on
frequency; the phase is exact at any count. Measured on the 3D Connector's coax (20 µm radial cells), against
the closed form's 50.021 Ω:

| Azimuth cells | 70 | 140 | 210 | 280 |
|---|---|---|---|---|
| Z error | +0.71 Ω | +0.35 Ω | +0.22 Ω | +0.16 Ω |

The default makes the arc at the outermost conductor radius no longer than the radial cell there, rounded up to
a multiple of four; `circuitrf explain` names the radius and the cell that set it. A coarser inner grid on the
axis side (openEMS's *multi-grid*) roughly doubles the time step but measured no faster on a coax, so it is not
used unless `MultiGridRadiiUm` asks for it. openEMS needs an odd number of azimuth lines at every level it
splits, and a count that would give it an even one is refused, naming the level.

**Where the circle starts.** At 0°, from the axis's next world axis towards the one after it (for a z axis, from
+x towards +y). When a lumped gap on the cylinder crosses 0°, the circle starts in the middle of the widest
stretch of azimuth no gap covers instead, since openEMS's lumped port is one box.

**How shapes are written.** A cylinder coaxial with the axis is written in the grid's own coordinates, which
fills its circle exactly. A Boolean **Subtract** whose Blank and Tools are plain shapes — the 3D Connector's
housing with its bore — is written as the Blank with each coaxial Tool cut out above it by priority, the bore
in the kept Tool's own material or in the background's, so the bore too lies on a circle. That is done only
where it is exactly the Boolean: when another solid below the housing in precedence overlaps a Tool (PTFE
drawn as its own solid in a bore that is not kept), the housing is written as its tessellation instead and the
run's notes say why. Every other shape is written as on a Cartesian grid and fills the same cells it would there.

**Sheets, lumped elements and lumped ports on the cylinder.** A strip of zero thickness on a cylinder — a birdcage
coil's rung or end ring — is a `CylinderSheet` object: `Axis`, `Base` (a point on the axis where the strip
starts), `Radius`, `Length` along the axis, `StartDeg` and `SweepDeg` round it. A lumped port or a lumped element
whose gap is such a patch gives it as `Cylinder`, with the same six fields, in place of `Plane`, `Offset` and
`Rect`. Its two ends are found from what its arcs and its two straight edges touch, as a flat port's are found
from its four edges, so a gap between two rungs runs along the axis and one between two arcs of a ring runs
round it. On a cylindrical grid about the same axis each is written in the grid's own coordinates: the sheet
as one box of zero radial thickness, exact on the grid's circle, and the gap as openEMS's own lumped element or
port, its radius and the two edges across its width on the grid's lines. Every other solver and grid refuses
them, naming the object: a Cartesian grid, Palace, the thermal solver and the STEP export. On circuitRF's own
grid a gap's azimuth edges are snapped to the nearest of its evenly spaced azimuth lines, and the run says so;
given lines can put a line on each edge.

**Wave ports** must be coaxial terminals on the two faces the axis crosses, centred on the axis: their source is
openEMS's own radial one, and their current probes are full discs about the axis. Refused, each naming the fix:
a lumped port on a plane, a terminal that is not coaxial or not on the axis, a radiation pattern (openEMS's
far-field box is written on a Cartesian grid only).

**SAR and B1.** A setup's `Sar` group runs on a cylindrical grid as local SAR and B1 only: no cube-averaged SAR,
since a cube is a Cartesian grid's, and the run says so. `MassesGrams` and `CrossCheck` are refused there. B1 is
formed from openEMS's Hρ and Hα, turned onto x and y at each cell's own azimuth. The per-port fields
(`PerPortFields`) are kept as on a Cartesian grid, so a schematic's fields analysis recombines them, but the
volumes are cells of the cylindrical grid, which the 3D view does not draw. No field is saved for the 3D view, whose field plots read a Cartesian
grid; the run says so. Measured end to end through `circuitrf em`: the coax section above within 0.4 Ω of its
closed form, and its transmission phase within 0.01° of the line's.

### Running on given grid lines {#openems-given-lines}

To run on a grid you already have — an openEMS script's own mesh, or a reference you are reproducing —
give its lines instead of letting circuitRF place them. `GridLines` in the `OpenEms` section takes either
a file or three lists:

| Field (in `OpenEms.GridLines`) | What it is |
|---|---|
| `File` | A CSXCAD XML file (what an openEMS script's `Write2XML` or `WriteOpenEMS` writes): its `RectilinearGrid` lines are taken exactly, scaled by its `DeltaUnit`. A relative path is read beside the `.c3d` or `.cem` holding the setup. A cylindrical grid's file (`CoordSystem="1"`) gives ρ, α and z: ρ and z scaled by `DeltaUnit`, α in radians as written, its circle closed; it needs `Grid: Cylindrical` with the axis it is round about |
| `X`, `Y`, `Z` and `Unit` | The lines as three lists, each increasing, in `Unit` — `m`, `mm`, `um`, `nm`, `mil` or `in`. The unit is required |

```json
"OpenEms": { "PmlCells": 8, "GridLines": { "File": "sar_dipole.xml" } }
```

With given lines, circuitRF places none of its own. The grid's extent is the region simulated, as it is in
openEMS: each face's boundary is applied at the outermost line, and the PML takes the outermost `PmlCells`
cells. The run is refused, naming the lines on either side, when:

- the air box reaches beyond the grid (shrink its padding to fit inside);
- a lumped port's or lumped element's sheet is not on a line, both ends of its gap fall on one line, or its
  width has no line across it;
- a sheet is not on a line, or a sheet or gap on a cylinder is not on a line of ρ;
- the setup has a wave port;
- the file's grid is cylindrical and the setup's is not, or the other way round.

A coordinate counts as on a line when it is within a thousandth of the cell beside it, so a script's file,
written to seven significant figures, lines up. `circuitrf explain` prints the grid as usual.

### Running openEMS {#openems-run}

With openEMS chosen the panel shows the grid settings above and two of its own, each a field of the
`.cem`'s `OpenEms` section; a blank box is the default. `SaveFieldsGHz` and `SaveH` are written in the `.cem` itself.

| `.cem` field (in `OpenEms`) | Default | What it does |
|---|---|---|
| `EndCriterionDb` | −50 | A run stops when every port's voltage and current has fallen this far below its peak |
| `MaxTimeSteps` | ten times the grid's own estimate | The most time steps one run may take |
| `SaveFieldsGHz` | the sweep's centre | The frequencies, GHz, whose electric field openEMS records over the air box for the 3D view; `[]` records none |
| `SaveH` | off | Also record the magnetic field H at those frequencies, so the 3D view offers \|H\|. It doubles the field files on disk |
| `CellConstantMaterial` | off; on for a setup that requests SAR | Each grid cell takes the material at its centre instead of an average over its edges. Averaging SAR over cubes needs it, and it is slightly less accurate at a material boundary. The panel's **Cell-constant material** box |

**openEMS excites one port per simulation, so an N-port setup is N runs**, one after another, each
using every core — a 4-port takes about four times as long as a 2-port, and the progress line says
*port k of N*. circuitRF writes openEMS's model itself, runs the `openEMS` program, reads the voltage
and current it recorded at every port, and computes the S-parameters from them with the same
definition of a wave as every other result, including a complex port impedance.

**How long it will take is said before it starts.** openEMS's cost is arithmetic on the grid: every
cell is updated once per time step, once per port. So once the grid is placed, the run states its
estimate — cells × time steps × ports, at 25 million cell updates a second per core, counting at most
eight cores (past that openEMS waits on memory) — in hours or days. That is a conservative figure:
recorded runs went two to ten times faster, and each run stops as soon as its ports have decayed.
**Past an hour, Simulate asks** (*Run Anyway* or *Cancel*). **Past a day, the question also names what
makes it long** — the smallest cell, the edges that set it, and that raising `MinCellUm` above it merges
them. `circuitrf em` has no one to ask: it prints the same estimate as a warning and runs.
`circuitrf explain` shows it too, beside the grid.

**A quiet openEMS says so.** Before its first time step openEMS builds its operator, and on a grid of
tens of millions of cells that takes minutes without a word. If it reports no time step for two
minutes, one line says what it is probably doing (building its operator for the grid's cells, or — after
steps have begun — running short of memory and swapping) and that Cancel keeps nothing from that run.
If its progress lines do not arrive while its probe files keep growing (its output is held back by the
pipe), the step count is read from the probes instead, and a line says so.

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
one is continued through it. They are written as openEMS spells them: `PML_8` with the default 8 cells,
`MUR` with 0 — the two spellings openEMS's own SAR examples use. The result lands beside a Palace one without replacing it —
`results/<name>.openems.sNp` and `<name>.openems_em.npy` — and `results/<name>.openems/` keeps the model
and, per port, openEMS's log and probe files, so a run can be repeated by hand.

**Fields.** For the 3D view, each run also records the electric field over the air box at the
`SaveFieldsGHz` frequencies (the sweep's centre by default; a frequency outside the sweep is refused,
because the pulse carries no energy there). openEMS computes it during the run itself, at every grid
cell's centre, and writes it in each port's folder as `efield<k>_f=<Hz>_abs.vtr` and `…_arg.vtr` — the
magnitude and phase of each component, which is the complex field exactly — plus 21 snapshots at fixed
phases that the view does not need. The 3D view shows it for each excited port at each frequency.
With `"SaveH": true` it records the magnetic field too, as `hfield<k>_…` beside each `efield<k>_…` pair —
twice the disk per saved frequency, which is why it is asked for rather than always on.

### Running EMerge {#emerge-run}

**FEM 3D (EMerge)** (`Solver3D: Emerge`, or `circuitrf em x.c3d --solver emerge`; the MCP `run` tool's
`solver=emerge`) solves a Driven or an Eigenmode setup with EMerge, a finite-element solver installed from
{{anchor: settings.html#em3d|Settings ▸ Solvers}}. It meshes the same geometry Palace does, with its own copy
of Gmsh. The `.cem`'s `EMerge` section holds its settings, the same way the `Palace` section holds Palace's.

With EMerge chosen the panel shows them. **A blank box is the chosen Quality preset's value**, shown in it, and a box
filled in overrides the preset for that setting only:

| Setting | Key | What it sets |
|---|---|---|
| Quality | `Quality` | Draft, Standard or Accurate: the four settings marked *by preset* together. A new setup starts at Draft; a setup that names none runs at Standard. A 3D view's setup card shows it beside the setup's name. |
| Largest element (λ) | `MaxElementWavelengths` | The largest element in each material, as a fraction of the wavelength in it at the sweep's top frequency; at most 0.5. *By preset.* |
| At metal | `EdgeRefinement` | The element size along conductor and sheet edges, as a fraction of the size above. *By preset.* |
| Element order | `ElementOrder` | 1 or 2 (the default). First order is for quick checks. |
| Refinement passes | `AdaptiveMesh` | The most passes in which EMerge refines the mesh itself before the sweep; 0 solves the starting mesh. *By preset.* |
| Adaptive sweep | `AdaptiveSweep` | On (the default) fits the band from a few solves; off solves every frequency. |
| Sweep tolerance | `Tolerance` | The adaptive sweep's tolerance on \|S\|. *By preset.* |
| Most sweep solves | `MaxSamples` | The most frequencies the adaptive sweep solves; at least 5, default 100. |
| Save fields at (GHz) | `SaveFieldsGHz` | The frequencies whose fields the run saves for the 3D view, as Palace's box: see [Fields for the 3D view](#palace-fields). |

| Preset | Largest element (λ) | At metal | Refinement passes | Sweep tolerance |
|---|---|---|---|---|
| Draft | 0.2 | 0.25 | 0 | 0.001 |
| Standard | 0.15 | 0.2 | 0 | 0.001 |
| Accurate | 0.1 | 0.15 | 3 | 0.0001 |

Every preset is element order 2. **Draft** was chosen on the 3D Eigenmode example's cavity, where it puts the
S21 notch at the same 5 MHz step as Standard and Accurate (and as Palace and openEMS) in 3 s and 1.6 GB, against
6 s and 3.1 GB for Standard and 34 s and 6.9 GB for Accurate, whose three passes took its mesh from 35,571 tetrahedra
to 60,503. The depth at that step moves by 0.9 dB across the three: a notch's depth is read from a fine sweep across
it, not from any preset's 5 MHz step. Order 1 put that notch 25–30 MHz low. A match needs Accurate: on a 50 Ω line
below the cavity's modes, the worst |S11| was −14 dB at Draft, −18 dB at Standard and −28 dB at Accurate. EMerge's sparse solver uses the
setup's *Cores* setting as its thread count; how much each choice of solver, core count and device is worth is in
[EM Solvers ▸ Getting the most speed from EMerge](em-solvers.html#emerge-speed).

A driven sweep is built adaptively: EMerge solves the full problem at a few frequencies and fits every
requested point from them, adding frequencies until its estimated error on |S| is below its tolerance. The
progress line shows that error against the tolerance. **A sweep that reaches `MaxSamples` first has not
converged**: its result is still written, with a warning, and the 3D view marks it not converged.

Before solving, the run checks the mesh's size against this computer's memory, as Palace's run does: a
warning past three quarters of it, and a question (or `--force`) past one and a half times it. A refused run
stops before the solve.

The result lands beside the other solvers' results: `results/<name>.emerge.sNp` and `<name>.emerge_em.npy`, or
`<name>.emerge_eig.npy` for an eigenmode run. `results/<name>.emerge/` keeps the input (`problem.json` and the
driver that reads it), EMerge's log (`emerge.log`) and the arrays it wrote, so a run can be repeated by hand.

**Eigenmodes on EMerge** give **f** and **Q** (the loaded Q, as Palace's). EMerge reports nothing else, so
circuitRF computes **Q_ext**, **Q_unloaded** and **Participation** from each mode's own field: Q_ext is each lumped
port's ω·R·∫ε|E|²/|V|² (the mode's stored energy over the power the port takes), and Participation is the share of
the mode's electric energy in each **material** (Palace's is by object). **EMerge finds the `Count` modes nearest
`TargetGHz`, on either side of it**, where Palace finds the first ones above it, and it evaluates its lumped ports at
`TargetGHz` rather than at each mode's own frequency. So **put `TargetGHz` near the modes you want**: at the band's
centre to ask whether there is a mode in the band, or just below a mode whose Q you are reading. On the 3D Eigenmode
example, searched from 8 GHz with three modes, the post's 12.84 GHz mode was not found at all, and the Cavity's
9.957 GHz mode read Q 694; from 10 GHz both were found, and the Cavity's read Q 564 (Palace: 539). Near 0 Hz the
eigensolver can return solutions that are not resonances, so a solution more than 5 % below `TargetGHz` is dropped,
and the run's notes say how many were. **Q unloaded** is the least certain number: it takes each port's share, computed
from the mode's field, out of the loaded Q, and on that example it read 21 % below Palace's for one cavity and 31 %
above for the other. EMerge solves an eigenmode problem on one core.

**Fields and the radiation pattern on EMerge.** An EMerge run saves fields as a Palace run does, by the same
*Save fields at* box, and every field plot draws them. Each saved frequency is a **direct solve of its own** after
the sweep (the adaptive sweep fits S between its own samples and has no field anywhere else), so it costs one more
solve per frequency, may lie outside the sweep, and changes nothing the `.sNp` reports. An eigenmode run saves every
mode. A driven field is at the same drive as Palace's, so the two solvers' plots of one structure read the same
numbers; a mode is normalised as Palace normalises one. Beside E and B, each saved field carries the **surface
current J_s** and surface charge Q_s on the conductors and sheets, as on Palace, so a J_s plot on surfaces, or on a
conductor's or sheet's face, draws an EMerge run too; as on Palace, ports, lumped elements and the air box's own faces
carry none. With **Radiation pattern** on and every air-box face
absorbing, EMerge integrates its far field over those faces at the saved frequencies (the sweep's centre when it
saves none) and the result carries the same `farfield` group Palace's does; a conducting face is said in the run's
notes and the S-parameters are still computed.

### Compare {#compare}

**Compare…** runs two or more solvers on one 3D model and compares each with one of them, the
**reference**. Choosing it shows a checkbox per solver and a **Reference** list over the ticked ones; the
reference starts as Palace when Palace is ticked, otherwise the first ticked solver. A solver that is not
installed, or cannot solve this setup (openEMS has no eigensolver, for one), is greyed out with its reason
as the tooltip, and with fewer than two runnable solvers ticked **Simulate** is off, its tooltip saying
why. Install a solver from **Settings ▸ Solvers**; the panel sees it when that page closes.

In the file it is `"Solver3D": "Compare"` with a block naming the solvers and the reference:

```json
"Solver3D": "Compare",
"Compare": { "Solvers": [ "Palace", "Emerge" ], "Reference": "Palace" }
```

`"Solver3D": "Both"` is Palace and openEMS with Palace as the reference, and a setup saying `Both` is
written back as `Both` while that is still its choice. From the command line,
`circuitrf em x.cem --solver palace,emerge` compares those two, `--reference emerge` picks the reference,
`--solver compare` runs the setup's own set, and `--solver both` is Palace and openEMS.

**What it runs.** The model is built once, and each solver runs on it in turn, Palace, then openEMS, then
EMerge, because each uses every core. Each result lands where it would alone, named for its solver
(`results/<name>.palace.s2p`, `results/<name>.emerge_em.npy`). Every solver is checked before any starts,
so a missing one is reported in the first second, not after another's run, with the command that runs
the ones that are ready. If one fails part way through, the others' results are still written and the
run says which failed and why; with three or more, the ones that completed are still compared when the
reference is among them.

**Where the comparison lands.** `results/<name>.compare_em.npy` holds each solver's S matrix as
`S_<solver>` (`S_palace`, `S_emerge`) and, for each solver against the reference, `dMag_dB_<solver>`,
`dPhase_deg_<solver>` and `dVec_<solver>`: the magnitude difference in dB, the phase difference in
degrees and |S_reference − S_solver|, which stays meaningful where |S| is small and a dB difference is
not. A Palace and openEMS comparison against Palace keeps the names it has always had: `S_palace`,
`S_openems`, `dMag_dB`, `dPhase_deg` and `dVec`. It opens in the Data Display like any result, each
solver's traces overlaid.

**How to read it.** The first line the run writes is the summary: the largest S21 and S11 differences over
the band and where they occur, for the pair that differs most; each other pair's line follows. After them
comes what makes a difference **expected** on this model, each only when this run makes it true: which
method suits the geometry better (when FEM and FDTD are compared), a lossy substrate, metal openEMS treats
as perfect, a wire below its grid cell, EMerge's lumped port model, the conductors EMerge gives a surface
impedance, and the error EMerge's adaptive sweep reached. A run that did not converge — an openEMS run
short of its end criterion, an EMerge sweep that stopped above its tolerance — is a warning and the first
thing to fix. **Agreement is a cross-check, not a validation**: every solver reads the same model, so an
error in building it would appear in every result. The results must be at the same frequencies — they
always are when they come from one setup — and a comparison is never interpolated. An eigenmode
comparison writes each solver's modes to its own result and no comparison file.

### Package capacitance and inductance {#package-rlc}

A Palace setup can solve for a **matrix** instead of S-parameters. The **Problem** picker
(`Problem3D`) is *Driven* (S-parameters, the default and omitted from the file), *Electrostatic* (the
capacitance matrix) or *Magnetostatic* (the inductance matrix). The two static problems run on Palace
only; a setup naming openEMS, or comparing it, is refused, and so is its `check`. They ignore the
frequency sweep and the port impedances, which are kept but not read (`check` says so).

**Terminals** (`Terminals3D`) are the matrix's rows and columns, in order. Each names its metal by a
**net** (a net of the layout, a net given to a shape in Properties ▸ Net, or a `.wBond` wire array), by
an **object's path** as the 3D problem names it, or by the **end of a path** (`TESTPT` for
`U1/Bottom Copper (1 oz)/TESTPT`), tried in that order. An end that several paths share is refused with
them listed, and a name that matches nothing is refused with the nearest names. **Ground**
(`Ground3D`) is the reference, named the same way; left blank it is the technology's ground-reference
conductors, and the floor of the air box when that floor is a ground plane. It takes several names,
comma-separated, and `+ default ground` as an entry keeps the ground-reference conductors in it:
`+ default ground, body:…/Inner 5/2` grounds a plane on a layer the technology does not mark as ground
without losing the others. A list without that entry is exactly the names. Pick adds to the list.

An electrostatic terminal is **whole pieces of metal**. Objects that share a face or overlap — a pad,
the via barrel through it, an inner landing pad and the pad on the far side, or copper pieces on one
layer that touch — are one piece, and naming any of them takes all of it; copper on two layers joins
only through a via. Two terminals that name one piece of metal are refused, naming both. The
**Conductor** box lists the pieces of metal (▾), each by its net, or by `body:` and its largest object
when it has none, with its layers and object count, and typing filters the list. In a 3D view's setup
**Pick** takes the piece of metal you click next in the view, and the pieces a row names are selected in
the view while you edit it. The **Ground** list also offers **Every body not in a terminal**.

**Other metal** (`UnassignedConductors3D`, electrostatic only) says what happens to metal in no
terminal that is not the ground:

| Value | Means |
|---|---|
| `Float` (default, not written) | The metal is connected to nothing and carries no net charge. Each piece is solved as one more terminal and then removed from the matrix, which is exact. Each piece adds one solve; more than 50 is refused, with the counts. |
| `Ground` | The metal is ground: plane copper and its stitching vias. The run's notes say how many pieces and the largest. |
| `Refuse` | The run is refused, naming the pieces. |

```
"Problem3D": "Electrostatic",
"Terminals3D": [ { "Name": "RF_IN", "Net": "RF_IN" }, { "Name": "VDD", "Net": "VDD" } ],
"Ground3D": "GND"
```

- **Electrostatic** gives `C`, the *Maxwell* matrix: `C[i][i]` is terminal i's capacitance to every
  other conductor, and each off-diagonal entry is negative. It also gives `C_mutual`, the
  lumped-circuit form: `C_mutual[i][i]` is i's capacitance to ground alone and `C_mutual[i][j]` is the
  capacitor you would draw between i and j. `C_ground` lists each terminal's capacitance to ground
  alone, the row sums of `C`. A new plot of the result opens on `C_mutual`. With floating metal, all
  three are of the named terminals after the floating pieces are removed; the matrix before is kept as
  `palace.C_full`.
- A **via** whose own net is on none of the copper it passes, all of which is one other net, takes that
  copper's net in a static solve, and the run says so with the drill shape's layer and place. That row
  offers **Give the via its copper's net**, which sets it in the layout as one undoable edit. A via
  whose copper is on two nets still refuses the run, naming the drill shape. In the layout editor, the
  status bar names both nets while the cursor is over such a drill.
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
a step in S11 at the top of the band. A **wave port** has none: it is a region of the air box's face where
the line is fed and measured in its own field. Each solver builds one its own way: Palace and EMerge
compute the line's mode on that face at every frequency, and openEMS feeds the line from behind the face
and measures it with probes (*On openEMS* and *On EMerge*, below). The **3D Wave Ports** example walks through one on each solver
([A first wave port](../new-user-guide/index.html#wave-ports-3d)).

In a 3D view a wave port's two ends are the conductors that meet its region, and its voltage path runs straight
across the gap between them. A conductor **enclosed** by another, a coax's pin inside its housing, has its path
inferred along a ray from the outer to the inner, through the inner's centre. The 3D editor has no field for a
voltage path; a geometry the ray gets wrong states `VoltagePath` in the `.c3d`, and a stated path always wins.

The port table in the **Solver** group lists every port the layout labels, and it is the one place a
3D setup's ports are configured — the planar **Ports** group is hidden while a 3D solver is chosen. Each
row sets the port's reference impedance **Z₀**, its kind (`Ports3D`) and, for a lumped port, its **Return**.
Lumped and wave ports may be **mixed** in one setup. A row whose port the layout no longer labels is leftover
settings, and only that kind of row can be removed.

<a id="port-return"></a>**Return** is the plane a lumped port's sheet runs to. **Automatic**, the default, walks the
layout from the port down and up, layer by layer, and takes the nearer side's first plane with metal under the port's
end (a pad-to-plane port: across its sheet). A layer the technology marks as a ground reference is preferred. A
technology that marks none returns through the nearest metal. A plane with an opening at the port is passed through.
Other metal across the sheet's path is refused, naming the layer, because a lumped sheet runs through dielectric. A
plane that stops short of the line's end by up to a tenth of the line's width still counts: the sheet moves in to where
the plane begins, and a note says how far. Each port's note names the plane, the side and why it was chosen, and
`circuitrf explain` lists each port's return and whether it was walked or stated.

State a layer for a port whose return is another plane. For example, a stripline port that must reference the plane
above, or a board with several reference planes where ports need different ones. The port then goes to that layer on
whichever side it is. Metal of another layer in between, or no metal of that layer under the port, is refused, naming
the layer and the port. A wave port has no Return: its two conductors meet at the air box's face. In the file it is one
key on the port's entry, written only when stated:

```
"Ports3D": [ { "Port": 3, "Return": "Inner 1" }, { "Port": 5, "Kind": "Wave" } ]
```

A `.cem` that states `GroundStackupLayerName` sends every lumped port of a 3D setup to that plane, unless the port's
own Return states another. The panel shows that field for a planar setup only.

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
- **OffsetUm** moves the reference plane that far into the structure. Both solvers measure there, so the
  line between is de-embedded. `circuitrf explain` says where each port's reference plane is.
- **What the numbers are referred to, on Palace.** Palace refers a wave port's S-parameters to the port's own mode
  at unit power — that is, to the mode's impedance, which changes with frequency (it is Palace's
  *Z_PV*, measured along a line from the return up to the strip). The Touchstone file states one real
  reference impedance per port, so circuitRF renormalises each wave port to its **Z0** and the file's
  header says so. On a 50 Ω microstrip the mode impedance is within a few ohms of 50; on a waveguide it
  is several hundred ohms, so set Z0 to the mode impedance the run reports if you want a matched file.
- **One mode, on Palace.** A wave port excites and measures its first mode. If the port's region is large enough
  for a second mode to propagate at the top of the sweep, the run warns — its S-parameters then leave
  out the power that mode carries. Make the region smaller, or lower the sweep's top.

A wave port needs Palace's eigensolver (every Palace build has one) and GSLIB (the `+gslib` variant, on
by default). A build without either is refused before anything meshes, naming the variant.

**On openEMS.** openEMS computes no mode. It builds the port the way a transmission-line port is built on
an FDTD grid:

- **The line is fed from a short extension behind the face.** The grid grows outward past the face, the
  face's own cross-section continues through it, and a source at its far end is shaped to the line: a
  radial field across a coax's dielectric, a sheet from a strip to its reference plane (one sheet over a
  ground, two for a strip between two planes). The feed is at least 4.5 times the largest distance on the
  face from a conductor to its reference, and at least ten cells. The run's notes give its length, and
  the 3D view's openEMS grid draws it, with the source plane and the reference plane marked.
- **The face is absorbing for openEMS** whatever the setup says, because the extension ends in PML. A
  face the setup states as PEC or PMC stays so for Palace, and the run's notes say so.
- **Voltage and current are measured around the reference plane**: the voltage on three planes one cell
  apart, the middle one on the reference plane, and the current on the two planes between them, averaged.
  The result is referred to Z0 directly, with no renormalisation.
- **The run reports the line's own impedance and ε_eff** per port, measured from the same probes
  (`Terminal P1: the line measured Z 50.4 Ω, ε_eff 2.20.`). On openEMS's Cartesian grid a round
  conductor is a staircase, and a coax reads slightly slow there: an ε_eff a few percent above its
  dielectric's is that, not the port.
- **A hollow waveguide is refused**: a region met by one conductor has no voltage between conductors, and
  openEMS would need a mode-matching port, which circuitRF does not build yet. Run it on Palace.
- **Keep PEC side walls away from the line.** Two PEC walls beside a line make a box with modes of its
  own; one near the band is excited by the feed's source and carries voltage but no current on the line.
  The run notes it when the empty cross-section's first mode is below 1.5 times the sweep's top. Make
  those faces PMC or Absorbing. A PEC face that is the line's own reference plane is not a side wall.
- **The current probe needs room**: its loop round each conductor must lie in dielectric with one grid
  cell to spare from any other conductor, corners included, or it reads a fraction of the current. A port
  whose loop cannot be placed is refused, naming the conductor it would touch and the clearance needed;
  refine the openEMS grid there or move the conductors apart.

**On EMerge.** EMerge solves the line's mode on the face, as Palace does, and uses its full wave-port
condition there. A wave port with one conductor on the face runs, a microstrip's end or a coax; so does one with
several terminals ([below](#wave-port-terminals-emerge)):

- **The S-parameters are referred to the mode at unit power**, and so to the mode's impedance along the
  port's voltage path. circuitRF renormalises them to the port's Z0. The run's notes give each wave port's
  mode impedance over the sweep. EMerge solves the mode at every frequency. On a face of one material, a
  coax's PTFE for example, the impedance is the same at every frequency.
- **A reference plane moved by the port's Offset** is de-embedded with the mode's own β, before the
  renormalisation, and the run's notes say how far it moved.
- **The run asks each face for a second mode** at the top of the sweep and warns when one propagates,
  as Palace's run does.
- **A hollow waveguide** runs on Palace. EMerge refuses it by name, as it does a wave port in an
  eigenmode problem.

#### Several conductors on one face {#wave-port-terminals}

When a wave port's region is met by **several signal conductors and one reference** — a coupled pair over
a ground, two strips inside one shield — it is one port with a **terminal** per signal conductor. Each
terminal is a port of the result in every way a port is: it has its own number in the Touchstone file,
its own Z0, and its own row in the port map. These are **terminal** S-parameters, the voltage and current
on each conductor. A coupled pair with a two-terminal wave port at each end is a four-port: with terminals
1 and 2 at one end and 3 and 4 at the other, 1 and 3 on one strip, S31 is its thru, S21 its near-end and S41
its far-end coupling, just as for any four lumped ports. The **3D Wave Ports** example's *Pair* is exactly this,
run on openEMS against Cohn's ideal line, and its *Coupled Microstrip* runs two unequal microstrip lines on both
solvers ([its walk-through](../new-user-guide/index.html#wave-ports-3d)).

The **reference** is the conductor every terminal's voltage is measured from. Left unstated it is the
conductor meeting the region that is in the ground set (the setup's Ground net, or a PEC face of the air
box); failing that, the one with the largest surface. The air box's PEC faces count as **one** reference,
named by the first of them in the order xmin, xmax, ymin, ymax, zmin, zmax. Two candidates with equal
surfaces among drawn conductors are refused, asking for `Reference`. Each terminal's voltage path runs from
the reference to its conductor, inferred as a single wave port's is, or stated.

So strips whose ground planes are the air box's PEC faces, as in the example's *Pair*, take a terminal each:
*Make Port ▸ Wave* on that face writes one per strip, referenced to the box. A bare wave port already in a
file there keeps its own reading, one port from one strip to the other, and `circuitrf check` adds a note
saying what the gesture would write.

In a `.c3d` the port carries `Reference` (optional) and `Terminals`; it states no `Number`, `Z0`,
`Positive`, `Negative`, `VoltagePath` or `Flip` of its own, and each terminal states its own:

```
{ "Name": "Left", "Kind": "Wave", "Plane": "YZ", "Offset": 0, "Rect": { ... },
  "Reference": "gnd",
  "Terminals": [
    { "Number": 1, "Name": "P1", "Conductor": "strip_a", "Z0": "50" },
    { "Number": 2, "Name": "P2", "Conductor": "strip_b", "Z0": "50" } ] }
```

- A terminal's number shares one namespace with every other port and terminal of the 3D view.
- Each terminal's conductor must meet the region and is never the reference; two terminals on one
  conductor are refused, and so is a conductor on the face that is neither a terminal nor the reference.
- One terminal is refused: write the ordinary wave port. A lumped port has one gap and takes no terminals.
- Terminals are never invented when a file is read: a wave port without `Terminals` whose region three or
  more conductors meet is refused, naming them and the fix. *Make Port ▸ Wave* writes them for you
  ([Drawing in 3D ▸ Simulating](drawing-in-3d.html#simulate)).
- `Model` turns the whole port off, every terminal with it.
- A `.cem` cannot state one: a layout's edge port is one conductor. Terminal ports are drawn in a 3D view.

**On openEMS**, each terminal is fed and measured on its own, one run per terminal, and S comes from all
the runs together: a coupled pair whose lines end in the feed's absorber is terminated in the pair's own
impedances, not in each terminal's Z0, so no single run gives a column of S by itself. Any number of
terminals runs.

<a id="wave-port-terminals-palace"></a>**On Palace**, a port with **two terminals** runs when its lines' modes travel
at **different speeds**: a microstrip pair, or any pair over a dielectric that does not fill the cross-section, the two
lines alike or not. Palace takes a wave port's modes from its face, so circuitRF gives each end **one port face carrying
both terminals**: the face's two modes are solved together, each is launched in turn, and circuitRF converts Palace's S
between modes into **terminal S**, one port per terminal at its own Z0, in the same `.sNp` an openEMS run writes. It
corrects for the port absorbing the second mode at the first one's speed, which is what Palace's port does.

**Lines whose modes travel at one speed are refused on Palace**, naming openEMS: a stripline, or any pair in a
homogeneous fill. Palace returns two such modes in an arbitrary mixture, draws it again every time it solves the face
(for each excitation and frequency of a sweep), and on some meshes returns nearly the same field twice, so no conversion
can recover two terminals from them. The run finds this out before the 3D solve, from Palace's own solve of the face's
modes (seconds), and says the two wavenumbers it found.

It costs one solve per terminal at every frequency Palace solves: a pair at each end is four solves, and the whole
cross-section is meshed. The sweep is Palace's ordinary adaptive one. It writes no wavenumber for the modes, so the run
solves each terminal face's modes on their own at five of the sweep's frequencies and interpolates between them. The
face's ground must be one conductor on the face: a face whose ground planes are separate conductors there carries a
mode per extra conductor, which two terminals cannot account for, and the run's check for a mode past the terminals'
own says so.

Also refused on Palace, each naming openEMS: **three or more terminals** on one face, a **reference plane moved off the
face** (the terminal voltages Palace writes are measured at the face), and a **lumped port** in the same problem. A
setup naming both solvers then runs on openEMS, and the run says Palace was skipped or refused.

What a Palace run says about the conversion:

- A **note** naming the route, per port: *Port 'Left': terminals P1, P2 as modes 1 and 2 of one Palace
  wave port, converted to terminal S (modes not degenerate, Robin correction K = 1.079 to 1.086)*, and one
  giving each terminal's power |S_jj|² + Σ|S_ij|² and the singular values of S.
- **The problem has no loss mechanism, yet its terminal S is not lossless** (more than 0.2 %): every
  conductor perfect, every dielectric lossless and no absorbing face, so the power that went missing is
  the conversion's. Refine the mesh at the port face.
- **A terminal's own voltage does not give Palace's mode impedance**: the installed Palace states its
  modes' power differently from the version circuitRF was checked against. Use a validated version.
- **A terminal reads another port face's modes**: two port faces are coupled through their modes. Move
  them apart.
- **The face supports a mode past its terminals' own** at the top of the sweep: that mode's power is not
  in the result. Make the port region smaller, or lower the sweep's top.

<a id="wave-port-terminals-emerge"></a>**On EMerge**, a port runs with **any number of terminals, up to nine**, on a face
whose modes travel at **different speeds** (a microstrip pair, coupled lines over a dielectric that does not fill the
cross-section), and with **two terminals** on a face whose modes travel at **one speed** — a stripline pair, or any pair
in a homogeneous fill, which Palace refuses. As on Palace, each end is one port face carrying every terminal: EMerge solves
the face's modes, one per terminal, launches each in turn, and circuitRF converts its S between modes into terminal S, one
port per terminal at its own Z0. The conversion reads each mode's own field on the face, so it needs no field from the 3D
solve and runs under EMerge's adaptive sweep. EMerge launches and absorbs every mode of a face at the first mode's speed,
and the conversion corrects for that.

A face of **one material** has its modes solved once, before the sweep. Its **reference must be one conductor on the
face**: a face whose ground planes are separate conductors there (two PEC faces with PMC faces between them, as the
example's *Pair* is drawn) carries a mode of the same speed per extra conductor, and the run is refused before the 3D
solve, saying how many such modes the face carries. Make the reference one conductor (PEC side walls), or run the setup on
openEMS. A face of **several materials** has its modes solved at every frequency, and a frequency where the face does not
give one TEM-like mode per terminal is refused the same way.

Refused on EMerge, each naming openEMS: **three or more terminals on a face of one material** (the conversion of
equal-speed modes is stated for two), **more than nine terminals** on one face, a **reference plane moved off the face**,
and a **lumped port** in the same problem. A comparison then goes on without EMerge and says so. EMerge saves no field
for a terminal port: it excites the face by its modes, and a terminal's own excitation is a mixture of them.

What an EMerge run says about the conversion:

- The same **route note** as Palace's, naming EMerge: *Port 'Left': terminals P1, P2 as modes 1 and 2 of one EMerge
  wave port, converted to terminal S (modes not degenerate, Robin correction K = 1.051 to 1.060)*; on a face of one
  material it says the face is degenerate and gives the fit that sorted its modes. The note on each terminal's power and
  the singular values, and the **not lossless** warning, are Palace's too.
- With the adaptive sweep off, the run also measures each terminal's voltage in the 3D field and compares it with what
  the face's modes give, as a note per port. **A difference over 1 %** is a warning: the terminal S is not to be trusted
  there; refine the mesh at the port face. The same voltages tell whether **a terminal reads another port face's modes**.
  An adaptive sweep solves no requested frequency directly, so a note says these two checks were not made.

### Coaxial ports {#coaxial-ports}

A **coaxial port** is a lumped port whose sheet is a **ring**: the flat annulus of a coax's cross-section between its pin
(radius *a*) and its outer conductor (radius *b*). Where a rectangular lumped port imposes a field of one direction across
its sheet, the ring imposes a **radial** one falling as 1/r, E(r) = V / (r·ln(b/a)), which is the transverse field of the
coax's own TEM mode. Its voltage is the line integral of that field from one conductor to the other, the same along every
radius. It needs no air-box face and solves no mode, so it sits where a cable would feed a connector: across the
dielectric at the connector's back face, wherever the connector is. It assumes the TEM mode, the only one a connector
carries in its band.

- **Polarity.** The conductor the inner circle lies on is **+** and the one the outer circle lies on is **−**: the pin is
  positive, as on a cable. *Flip* swaps them; `Positive` and `Negative` in the `.c3d` state them instead.
- **Z0.** The port's reference impedance is its own setting, like any port's, and is what the result is normalised to.
  `check` also gives the ring's own impedance as a coax, η₀/(2π√εr)·ln(b/a) with the dielectric filling it, so a bore
  that is not quite 50 Ω is visible before a run: *As a coax filled with 'PTFE' (εr 2.1), its ring is 48.49 Ω; its Z0
  is 50 Ω*.
- **Where it may lie.** On the XY, YZ or XZ plane, centred anywhere; its open area must be dielectric. A PEC face of the
  air box in the ring's own plane is never one of its two conductors, so a coax may end on the box's wall. The mesher puts
  eight elements across the ring's radial gap.
- **Which solvers.** **Palace** writes it as a lumped port with a radial direction, on a box face or inside the model.
  **EMerge** runs it as circuitRF's radial lumped port, in either place. **openEMS** writes it inside the model as a
  radially weighted source, a thin conducting slab and two probes, and **refuses it on a face of the box**: give the
  connector some air behind its back face, or run Palace or EMerge.

**When the coax wave port is still the better choice.** A [wave port](#wave-ports) on the air box's face solves the
coax's mode at every frequency and terminates the line exactly, with no sheet of its own in the answer, so where a coax
runs out to the box's face it is the exact port and the reference for the other two. The ring is for a connector that
ends inside the model, with air or other parts behind it, and for a feed that should not depend on where the box is. A **rectangular gap port** at the bore's floor, the older way of feeding a
connector, puts the gap's capacitance and one forced field direction into every answer; the **Loop Sensor** example
measures the difference on one connector.

### Eigenmodes {#eigenmodes}

**Problem** *Eigenmode* finds the structure's resonant frequencies and their Q — "is there a lid
resonance inside my band?". `Eigenmode` sets how many modes (`Count`, default 3) above which frequency
(`TargetGHz`, default the sweep's start). Palace and EMerge; see [Running EMerge](#emerge-run) for what EMerge's
table leaves out.

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

**Worked example.** **File ▸ New ▸ Examples ▸ 3D Eigenmode** asks the question on its own: a 50 Ω line through an
aluminium housing, in the band 8–12 GHz. The empty cavity's closed form puts its lowest mode at 10.13 GHz, an upper
bound, since the laminate on the floor pulls the mode down. The cavity's first mode is at **9.956 GHz** (Q **539**),
inside the band, and the driven S21 has a **−11.69 dB** notch there. With a grounded post beside the line, the mode is
at **12.84 GHz**, above the band, and the notch is gone. **The post does not damp the mode; it shortens the cavity the
mode sees**, so the mode's frequency rises (its Q goes up, not down). The example's mode tables also show rows with a Q
near 1: those are the line between its two ports, not resonances. Each cell also has the two setups on EMerge, which puts
the modes within 0.15 % of Palace's; the example's README compares the two.

The result is `results/<name>.palace_eig.npy` and no Touchstone file; `circuitrf em` prints the mode
table and the panel shows it after a run. Palace's own files stay in the run folder.

### A plane wave and radar cross-section {#plane-wave}

A 3D view's Driven setups can be driven by an **incident plane wave** in place of their ports. The result is the model's **radar
cross-section** (RCS) at each frequency, not S-parameters. EMerge and openEMS run it. Palace refuses the setup by name: it
has no plane-wave source of any kind (its one plane-wave excitation is a Floquet port on a periodic cell).

**In a 3D view**, right-click the canvas and choose **Add Plane Wave**: a wave falling from above is added (and a setup
made, when there is none). In Face selection, right-click a flat face and choose **Add Plane Wave Onto Face** to aim the wave
straight into it, its phase zero at the face's centre. A view may hold several waves, each a row of the Object Tree's
**Excitations** group, and **at most one is enabled**: the enabled wave drives every Driven setup of the view, and their
cards are badged **RCS**. Adding a wave, or enabling one, disables the others; a disabled wave's row is greyed and kept to
switch back to. The view draws the enabled wave as a translucent wavefront upstream of the model, an orange arrow from it
along the direction of travel and a red one along E.

Select a wave's row, or its picture, to edit it in the Properties Inspector: its **Name**, **Enabled**, and the values below.
The row's menu, and the picture's on the canvas, has **Rename…**, **Move…** and **Move Along** X, Y or Z, **Enable**/**Disable** and
**Remove**, and **Delete** removes it too. Move works as it does on an object (a base point, then the target, snapped; G starts it)
and moves the wave's phase origin, where it is drawn: the wave arrives along the line through it. Only the enabled wave is drawn,
so a disabled one is moved by its **Phase origin** in the Inspector. The setup
panel says which wave drives the setup. Palace cannot run one: Add Plane Wave and Enable are refused on a Palace setup, saying
why, and changing a setup's solver to Palace disables the enabled wave in the same step (Undo puts both back).

```
"PlaneWaves": [
  { "Name": "Plane wave 1", "Enabled": true, "Direction": [0, 0, -1], "EField": [1, 0, 0] }
]
```

- `Direction` is the way the wave travels and `EField` its polarisation, each as three numbers of any length. They must
  be at right angles. Vectors, not angles, so the wave cannot be read in the wrong angle convention.
- `E0` is the peak amplitude in V/m (default 1). A cross-section does not depend on it.
- `PhaseOrigin` is where the wave's phase is zero, in the file's DBU (default the origin); the Inspector shows it in µm.
- `ThetaStepDeg` and `PhiStepDeg` set the bistatic grid: θ 0–180° and φ 0–360° in those steps (default 2° each).

What the run needs:

- **No port beside the wave.** The 3D view's ports are refused; turn each one's **Model** off for a scattering setup.
- **Every air box face absorbing.** The wave comes in, and the scattered field goes out, through them. A face set to Pec
  or Pmc is refused by name. The default padding is a quarter wavelength, as for a radiation pattern.
- **Room between the model and the air box.** The far field is read on a closed box in the air halfway between the two,
  away from the absorber's own reflection. A model that touches the air box leaves no room for it, and is refused.

The result is the `rcs` group of `results/<name>.emerge_em.npy`. No Touchstone file is written, so `circuitrf em -o`
is refused for this setup.

| Cube | Axes | |
|---|---|---|
| `Bistatic` | f, theta, phi | σ in m² over the grid |
| `BistaticDbsm` | f, theta, phi | the same in dBsm |
| `Monostatic` | f | σ straight back along the incidence, computed in that direction (not read off the grid) |
| `MonostaticDbsm` | f | the same in dBsm |
| `Etheta`, `Ephi` | f, theta, phi | the scattered far field, for a polarised cross-section |

σ = 4π r² |E<sub>s</sub>|² / |E<sub>i</sub>|² as r → ∞. The result also carries the wave it was solved for, and the panel
shows the monostatic value after a run. A plane-wave result has no gain, directivity or efficiency: those need an input
power, and a plane wave has none.

**How close it is.** On a perfectly conducting sphere of radius 35.36 mm at 1 GHz, against the exact series solution,
the monostatic σ came out 0.63 dB low on the Standard preset's mesh, and 0.33 dB low at 0.06 λ elements. The bistatic
RMS error over the grid was 0.36 and 0.23 dB. An oblique incidence (θ 60°, φ 30°) matched the exact pattern as closely.
EMerge refines its mesh only on a driven solve with ports, so a scattering run solves on its first mesh: refine it with
**Largest element** and **At metal**.

**openEMS** injects the wave on a box around the model (its total-field/scattered-field source) and reads the far field on
the radiation pattern's surface outside it, so the air box needs a few grid cells between the model and the absorber. On the
same sphere the monostatic σ came within 0.4 dB and the bistatic RMS error was 1.0 dB at 80 cells per wavelength
(`OpenEms.CellsPerWavelength`), 0.6 dB at 140: FDTD staircases a curved surface, so it needs a finer grid than the
default 20 for a small, round scatterer. The wave's leakage out of its box measured 50 to 70 dB below the sphere's
cross-section. openEMS runs a plane wave on its Cartesian grid only.

A **faceted sphere** is a sphere with `FacetVertices` above 0 in the 3D view: the convex hull of that many points spread
evenly over it, as Hertzon builds one. A Hertzon design's faceted sphere opens as one.

### Looking at a 3D mesh before solving {#mesh-3d}

**Mesh** on a 3D setup's panel, the grid button beside **Run Analysis** on a 3D view's toolbar, and *Simulate ▸ Mesh Only*
all make the mesh a run of that setup would solve on, and stop before any solver starts. A pass uses the run's folder, memory
limit and progress row, and is cancelled from that row. It writes no result, so a result already in the folder stays as it was.
Messages gives the tetrahedra, the count estimated before meshing and the memory a solve of that mesh would need. Palace must
be installed, since the pass checks the same things a run checks. The 3D view turns the mesh on when the pass finishes, and
**Show 3D** does when a `.cem`'s view is not open yet.

**A current mesh is never made twice.** Simulate and Mesh compare the Gmsh script they would write with the one the mesh in
the folder was made from. When the two match, the mesh is reused and Messages says so; when they differ, it is made again.
A change to the sweep, or to anything else that leaves the geometry and the element sizes alone, keeps the mesh.

The button is greyed, with the reason on its tooltip, on a setup that runs only on openEMS or EMerge: openEMS has a grid, and
EMerge meshes inside its own run, so neither leaves a mesh to reuse.

### Fields for the 3D view {#palace-fields}

A Palace run also saves the solved **fields**, which the 3D view's field plots draw on a clip plane, on
surfaces or on chosen faces. **Save fields at (GHz)** in the setup panel's Palace section — `SaveFieldsGHz`
in the `.cem` — says which frequencies. Its box shows the sweep's centre as its placeholder when it is
blank, and takes a comma list (`2, 6, 10`) or `none`. Untick the check box beside it to save no fields
(it writes `none`); tick it again to get the frequencies back. **A new setup starts with it unticked**, so its first run writes S-parameters only; tick it before a run whose fields you want. A field plot's *Other frequency…* adds one to it. A
run saves only these frequencies, so a plot can show only these: a 2–18 GHz sweep left blank saves its
centre, 10 GHz, and nothing else. openEMS and EMerge have the same box, for their own fields.

```
"Palace": { "SaveFieldsGHz": [2.4, 5.8] }
```

- **Omitted**, it saves the sweep's **centre** frequency only: the middle of a linear sweep, the geometric
  middle of a logarithmic one.
- **`[]`** saves no field at all.
- **A frequency that is not one of the sweep's points is solved as an extra point.** Palace 0.18.1 refuses
  to save a field at a frequency it did not sample (checked on a real run), so circuitRF adds each save
  frequency to Palace's samples. Its S-parameters are left out of the `.sNp`, which holds the sweep's
  frequencies and nothing else. A save frequency outside the sweep is refused before anything runs,
  because sampling it would change the sweep's answer at every other frequency.
- An **eigenmode** run saves every mode it computes and a **static** run every terminal's solution,
  unless `SaveFieldsGHz` is `[]` (their fields are not per frequency, so the list's values are not used).

**Fields are large.** Measured on the bond-wire test case (146,769 tetrahedra, element order 2): **137 MB
per saved frequency per driven port** (127 MB inside the volume, 9.8 MB on the surfaces), plus 33 MB
once for Palace's error map. Palace runs one excitation per port, so a two-port setup saving twenty
frequencies writes about **5.5 GB**. The size grows with the mesh, so a larger model or more refinement
passes costs more.

**A driven run says what its fields will take before Palace starts.** Once Gmsh has made the mesh, beside
the memory check, the run states the estimated size of the fields it will save: the measurement above,
scaled by the mesh's tetrahedra, times the save frequencies and the driven ports. A setup that names its
frequencies is estimated from its own list. Adaptive refinement can make the final figure larger. When the
run finishes, its summary line states what the field files took and the folder they are in,
`results/<setup>.palace/postpro/paraview`.

**Delete Saved Fields** removes them once a run is done with. It is on a solved setup's menu: the card in a
3D view's setup list, and the `.c3d`'s or `.cem`'s row in the Project Tree. It moves the run folder's field
folders to the Trash, says how much it freed, and keeps the mesh, the Touchstone and the `.npy`. The setup's
field plots then show nothing and say why. Run the setup again to save its fields again.

### Palace on a GPU {#palace-gpu}

Palace can solve on a GPU, and on several at once, when it was compiled with GPU support. **Device**, under
*Cores* in the setup panel, chooses **CPU** or **GPU** for a setup that runs Palace. Like *Cores*, it is a setting
of this machine, not of the design: it is not saved in the `.cem` or the `.c3d`, so a setup from someone else's
workspace runs on your machine's choice.

{{ui: em-setup-palace-device}}

- **It needs a GPU build of Palace, which you build yourself.** The Palace circuitRF installs is built for the
  CPU. GPU support is chosen when Palace is compiled; Palace's own
  [parallelism guide](https://github.com/awslabs/palace/blob/v0.18.1/docs/src/guide/parallelism.md) says how to
  build one. Point circuitRF at it in **Settings ▸ Solvers**, or with `CIRCUITRF_PALACE`.
- **One process per GPU.** circuitRF asks Palace how many GPUs it finds and runs one Palace process on each. A
  *Cores* setting lower than that count caps it. The run's notes say how many were used.
- **A Palace that cannot use a GPU is refused before anything is meshed.** Given GPU, a build without GPU support
  does not fail: it warns and solves on the CPU. So circuitRF checks first, with a one-element solve, and refuses
  the run if the build has no GPU support or finds no GPU. After a GPU run, it reads Palace's log, and if the
  run used the CPU anyway it says so among the run's warnings.
- **Memory.** The memory check before a run measures this machine's RAM. GPU memory is not estimated; if Palace
  runs out of it, the run fails with Palace's own error message.
- **A GPU build also runs on the CPU.** Device is chosen each run, so if the GPU does not work, set **Device**
  to CPU (or leave out `--device gpu`) and the same Palace solves on the CPU. A GPU build needs the GPU
  toolkit's runtime libraries to start at all, even for a CPU run. If they are missing, point
  **Settings ▸ Solvers** at a CPU-only Palace, such as the one circuitRF installs. Both can stay installed.
- **openEMS is unaffected.** In a comparison, the device applies to the Palace run only.

Once **Device** is GPU, the Palace row in **Settings ▸ Solvers** also reports what Palace found, such as
*GPU: 2 detected* or *GPU: not in this build*. From the command line:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf solver list --capability gpu
<span class="prompt">$ </span>circuitrf em Via.cem --device gpu</code></pre>

A headless run uses the CPU unless `--device gpu` is given; it does not read the panel's setting.
`circuitrf explain` takes `--device` too and reports which device a run would use.

## Letting circuitRF install the 3D solvers {#install-assistant}

circuitRF can install Palace, Gmsh and openEMS for you. It downloads each one from its own upstream
and installs it for your user account only. It never needs administrator rights.

There are three ways to start an install, and they all do the same thing:

- **Install …** on the message a 3D run gives when a solver is missing.
- **Install …** on the solver's row in {{anchor: settings.html#em3d|Settings ▸ Solvers}}.
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
| Palace 0.18.1 | yes. It is built from source, which takes about an hour. | yes (arm64 and x64). It is built from source. | yes, inside the Linux subsystem (WSL 2), built from source there (see [Palace on Windows](em-setup.html#palace-windows)). |
| Gmsh 4.15.2 | yes, from Gmsh's own archive, in under a minute | yes. x64 from Gmsh's own archive; arm64 is built from Gmsh's source against your distribution's OpenCASCADE, in about 3 minutes (Gmsh publishes no Linux arm64 build). | yes, from Gmsh's own archive |
| openEMS 0.37.0 | yes. It is built by openEMS's own script. | yes. It is built by openEMS's own script. | yes, from openEMS's own archive |

**Some installs need tools you install first.** A source build needs a compiler and some libraries.
If one is missing, nothing is downloaded. circuitRF names the one command to run for your system, such
as `xcode-select --install`, `brew install …` or `sudo apt-get update && sudo apt-get install …`, and you
run it yourself.
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

### Palace on Windows: the Linux subsystem {#palace-windows}

**Installing it for the first time?** {{anchor: palace-windows.html|Installing Palace on Windows}} walks
through it step by step, starting with what it changes on your computer. This section is the reference.

Palace does not run natively on Windows. circuitRF runs it inside your own **Windows Subsystem for
Linux** (WSL 2) distribution instead, and treats it like a Palace on this computer: Simulate, the
progress, the `.sNp` and the refusals are the same. Gmsh still runs natively on Windows; only Palace runs
in the subsystem.

**What you need first.** circuitRF does none of these for you. If one is missing, **Install Palace …** and
a 3D run tell you which one, with the step that fixes it:

| What is missing | What fixes it |
|---|---|
| The Linux subsystem is not enabled | In a terminal opened as administrator, run `wsl --install` once, then restart Windows. |
| Virtualization is turned off | This is a firmware (BIOS/UEFI) setting, not a Windows one. Turn on the processor's virtualization there (Intel VT-x, or AMD-V/SVM). |
| No Linux distribution is installed | `wsl --install -d Ubuntu`, then start it once so it can create your Linux user. |
| The distribution is WSL 1 | `wsl --set-version <name> 2`. Everything in it is kept. |
| The distribution lacks build tools | The one `sudo apt-get update && sudo apt-get install …` line the install names. You run it yourself inside the distribution. |

**If `apt-get install` fails with "404 Not Found".** The distribution's package list is older than the
packages on the server, which is usual in a distribution that has not been updated for a while. Run
`sudo apt-get update` inside the distribution, then the install line again. The line circuitRF names
starts with that update for this reason.

**Palace's own build instructions do not cover Windows.** Palace's GitHub page describes building on
Linux and macOS only. On Windows, follow them *inside* the WSL 2 distribution, as a Linux build, or let
**Install Palace …** do it there. circuitRF does not support a Palace built natively on Windows, or one
running under WSL 1.

**Finding it.** circuitRF looks in every WSL 2 distribution, in the order `wsl -l` lists them. Inside
each it looks in circuitRF's own install folder, then Spack's install trees, then conda environments,
then the distribution's `PATH`. The first distribution with a validated Palace is used, and the Settings
row names it. A Palace you built there yourself is found with no setup.

**Installing it.** **Install Palace …** runs the Linux build recipe *inside* the distribution, into
`~/.circuitrf/solvers/palace/0.18.1/` in its own Linux filesystem. The build is from source, as on Linux.
It is reported in Palace's issue tracker as taking most of a day inside the subsystem. circuitRF also
keeps a copy of the install record on the Windows side, so Settings can show the install without
starting the subsystem.

**How a run works.** circuitRF copies the mesh and the configuration into
`~/.circuitrf/runs/` inside the distribution, runs Palace there, and copies Palace's result files back
into the run folder on Windows. It never runs Palace on a Windows drive the subsystem mounts
(`/mnt/c/…`), because file access across that boundary is slow. The run folder on Windows holds
everything, just as on any other computer. Palace uses the distribution's own cores and its own MPI.
**Cancel** stops Palace and every process it started inside the distribution, not only the Windows side.

**Memory.** The subsystem runs in a virtual machine that gets only part of your computer's memory by
default. circuitRF checks a run against the subsystem's memory, not the computer's. When a run may not
fit, the warning names the setting that raises it, `memory=` under `[wsl2]` in `.wslconfig` in your
Windows user folder, with a value worked out from your computer's memory (all of it, less what Windows
keeps for itself). The file's path is a link on the message: click it to show the file in File Explorer.
The message also has a **Give the subsystem more memory…** button, which does the edit for you. The
dialog it opens shows the subsystem's memory now, what `.wslconfig` says, and your computer's memory; the
proposed value, which you can change (from 2 GB up to all of the computer's memory); the one line of the
file that changes, before and after; and the distributions running now. **Save and restart the
subsystem** writes the file and runs `wsl --shutdown`, which stops everything running in the subsystem
(other terminals, containers, Docker Desktop's engine), then reports the new figure in Messages.
**Save only** writes the file and stops nothing: the change applies the next time the subsystem starts.
circuitRF changes only that one line, keeps the previous file beside it as
`.wslconfig.circuitrf-backup`, and never restarts the subsystem while a 3D EM run or an install of its own
is working there. When a run's estimate is past 150 % of the subsystem's memory, the question before the
run offers **Give the Subsystem More Memory First…** too: it opens the same dialog and does not start the
run, so press Simulate again afterwards. When even the largest value would leave the run short, the
warning says so, offers no button, and names the remedies that shrink the problem instead.

**Choosing where Palace runs.** {{anchor: settings.html#em3d|Settings ▸ Solvers}} has a **Location** row
under Palace, on Windows only. **Automatic** (the default) uses a native Palace if there ever is one,
then the subsystem. You can also choose **Native**, or one distribution by name. When Palace runs in the
subsystem, a line under it reads the subsystem's memory each time the page is shown ("Linux subsystem:
3.8 GB of 7.6 GB"), and its **Change…** button opens the memory dialog described above.

**Removing it.** **Uninstall Palace …** removes the folder circuitRF installed inside the distribution.
The distribution itself, and any packages you installed in it on circuitRF's advice, are yours and
stay. If the distribution cannot start, nothing is removed and the message says why. If the
distribution was unregistered, the button reads **Clean up … (missing)** and removes only circuitRF's
record.

## Removing the 3D solvers {#uninstall-solvers}

A Palace built from source takes gigabytes. When you are done with 3D EM you can get that space back.
circuitRF removes **only what it installed**. A solver you installed yourself, or one it finds on
`PATH`, in Spack or in conda, has no Uninstall, and circuitRF never deletes it.

There are three ways, and they all do the same thing:

- **Uninstall …** under the solver's row in {{anchor: settings.html#em3d|Settings ▸ Solvers}}. There is one
  button for each version circuitRF installed.
- **Remove all 3D solvers …** at the foot of that page. It removes every one after a single
  confirmation.
- `circuitrf solver remove palace --yes`, or `circuitrf solver remove --all --yes`, from a terminal.
  Without `--yes` it only shows what it would remove.

**The confirmation shows the space it frees**, measured at that moment. It also says that removal is
permanent, and how long the install took on your computer, because getting a solver back means
installing it again.

**Removing the Gmsh that runs use says what stops working.** The confirmation starts with *The following
solvers will no longer function:* and lists the ones still installed that need it: Palace, and circuitRF's own
thermal solver. EMerge meshes with its own copy of Gmsh, so it keeps running; it and openEMS only lose the 0 Hz
point of a sweep that includes one. Removing an older Gmsh that runs do not use says nothing of the kind.

**Your documents are never touched.** Setups, meshes, results and run folders live in your workspaces,
and every one of them stays. After a removal, a 3D run offers **Install …** again.

**A solver that is in use is not removed.** If a 3D run or an install is using it, in this window or in
a `circuitrf` command in a terminal, nothing is removed and the message says what is using it. **Remove
all** is all or nothing: if one solver is in use, none are removed.

**If a file cannot be deleted**, for example because a program still has it open, the solver is
already gone as far as circuitRF is concerned, so no run will use it. The message lists each file that
remains. Close whatever holds it and remove again, and circuitRF finishes the job.

**Older versions are kept until you remove them.** When circuitRF installs a newer version of a solver,
it lists any older version it installed, with its size, and offers to remove it. It never removes one
by itself, because you may want to compare older results against it.

### Uninstalling circuitRF {#uninstall-circuitrf}

Remove circuitRF the way you remove any application. What happens to the 3D solvers depends on how:

| How circuitRF was installed | How you remove it | The 3D solvers circuitRF installed |
|---|---|---|
| Windows `.msi` | **Uninstall** in the Apps list (Add or Remove Programs) | Removed too. circuitRF warns you first, then runs the Windows uninstall, which asks you to confirm once more. |
| macOS | Drag circuitRF to the Trash | Stay. |
| Linux `.tar.gz` | `~/.local/share/circuitRF/current/install.sh --uninstall` | Removed too, after a warning. |
| Linux `.deb` | `sudo apt remove circuitrf` | Stay. |

The warning is there because reinstalling circuitRF later means reinstalling the solvers too. It
reaches only your own account: solvers another person installed on the same computer stay in their
folder, and their own **Remove all 3D solvers** removes them.

**Where the solvers stay**, they wait in your user folder. When you install circuitRF again it finds
them and uses them without reinstalling, and their Settings rows still offer **Uninstall …**. To get
the space back instead, use **Remove all 3D solvers …** in
{{anchor: settings.html#em3d|Settings ▸ Solvers}} before you remove circuitRF.

**Updating circuitRF never removes a solver.**

## Installing the 3D solvers by hand {#install-3d-solvers}

A 3D setup (one whose `Solver3D` names Palace or openEMS) runs a solver circuitRF does not include. You
install it yourself, and circuitRF finds it — or you name it in
{{anchor: settings.html#em3d|Settings ▸ Solvers}}. **circuitRF runs only the Palace versions it has
validated** and refuses any other, naming the validated ones, because Palace's input can change meaning
between versions and the result would look plausible either way. **Gmsh and openEMS are different: any version
runs.** circuitRF lists the versions it has tested, and a run with any other version goes ahead with a
warning that names them. What a different Gmsh could change is which mesh entities belong to which named
object, and every run checks that before solving, whatever the version.

| Program | Validated version | What it reports |
|---|---|---|
| Palace | 0.18.1 | `palace --serial --version` prints `Palace version: 0dc74cd` and `Schema version: 1-7-0` — a git hash, never "0.18.1" |
| Gmsh | tested: 4.11.0 to 4.15.2, every release between; any other runs with a warning | `gmsh --version` prints the version, e.g. `4.15.2` (the Homebrew build adds `-git`) |
| openEMS | tested: 0.37.0-rc3 and 0.37.0; any other runs with a warning | `openEMS --help` prints a banner with `version v0.37.0` and `CSXCAD -- Version: v0.7.0`; a build from a clone without its tags prints the git hashes `305df56` and `c46e320` instead (0.37.0-rc3: `67d3784` and `dcdb62b`) |

Below is exactly what was done to validate them. **Only macOS on arm64 has been verified**, and Gmsh on
Linux arm64. Elsewhere the routes are *not yet verified*; use the upstream instructions linked with each
program, and check the Settings row afterwards.

**Gmsh** — macOS: `brew install gmsh`. Linux: your distribution's own package is fine where it is a tested
version (Ubuntu 24.04 packages 4.12.1 and Debian 13 packages 4.13.1, both tested; Debian 12's 4.8.4 cannot read
circuitRF's scripts). On Linux arm64, **Install Gmsh…** builds 4.15.2 for you, and
[Building Gmsh from source](#gmsh-from-source) gives the same steps by hand. Other platforms:
*not yet verified* — [gmsh.info](https://gmsh.info).

**openEMS** — macOS: install its dependencies with `brew install cmake boost hdf5 cgal vtk`, then

<pre><code class="cmd"><span class="prompt">$ </span>git clone --recursive -b v0.37.0 https://github.com/thliebig/openEMS-Project.git
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
name Palace (and `mpirun`, if you want more than one core) in Settings ▸ Solvers. `which palace` and
`which mpirun`, run with the environment loaded, print the two paths. Without an `mpirun`, Palace runs
as a single process and the run's notes say so. Other platforms: *not yet verified* — see Palace's own
documentation.
**Palace does not run natively on Windows.** circuitRF runs it inside the Linux subsystem instead (see
{{anchor: em-setup.html#palace-windows|Palace on Windows}}); openEMS runs natively.

<div class="callout note">
<span class="label">Palace's licence note</span>
<p>A default Palace build includes ParMETIS, whose licence allows commercial use for evaluation only.
If you build Palace, you accept those terms. circuitRF distributes no copy of Palace, so it passes on
none.</p>
</div>

When a row in Settings ▸ Solvers reads *validated* (*tested*, for Gmsh), the program is ready. `circuitrf explain` on a 3D
`.cem` gives the same answer from the command line, under *solvers*.

### Building Gmsh from source {#gmsh-from-source}

Gmsh publishes no Linux build for arm64 (aarch64). On such a machine, **Install Gmsh…** in
{{anchor: settings.html#em3d|Settings ▸ Solvers}}, or `circuitrf solver install gmsh --yes`, builds it for you
from Gmsh's source, by the steps below. It first checks for the compiler, CMake and OpenCASCADE, and if one
is missing it names the command that installs them. If you would rather build it yourself, these steps built
Gmsh 4.15.2 in under three minutes on Ubuntu 24.04 arm64, and the build meshed circuitRF's own scripts
identically to the macOS build. A distribution's own `gmsh` package also works when it is a tested version;
see the table above.

Install a compiler, CMake and OpenCASCADE, the geometry library Gmsh needs. On Ubuntu or Debian:

<pre><code class="cmd"><span class="prompt">$ </span>sudo apt-get install build-essential cmake curl libocct-foundation-dev libocct-modeling-data-dev libocct-modeling-algorithms-dev libocct-data-exchange-dev libocct-ocaf-dev libocct-visualization-dev</code></pre>

Then download, build and install Gmsh into your own `~/.local`:

<pre><code class="cmd"><span class="prompt">$ </span>curl -fLO https://gmsh.info/src/gmsh-4.15.2-source.tgz
<span class="prompt">$ </span>tar xzf gmsh-4.15.2-source.tgz
<span class="prompt">$ </span>cd gmsh-4.15.2-source &amp;&amp; mkdir build &amp;&amp; cd build
<span class="prompt">$ </span>cmake .. -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$HOME/.local" -DENABLE_FLTK=0 -DENABLE_OCC=1
<span class="prompt">$ </span>make -j4
<span class="prompt">$ </span>make install</code></pre>

The configure step should print `Found OpenCASCADE`. If it does not, Gmsh builds without it and cannot
read circuitRF's geometry. `-DENABLE_FLTK=0` leaves out Gmsh's own window, which circuitRF never opens,
and with it the X11 and font libraries the window needs. Nothing needs `sudo` except the first step.

circuitRF looks in `~/.local/bin` by itself, so there is nothing to set afterwards: the Gmsh row in
Settings ▸ Solvers reads *4.15.2, tested*. To install somewhere else, change the prefix and name the
program in Settings ▸ Solvers.

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

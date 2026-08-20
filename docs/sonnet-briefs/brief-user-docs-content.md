# Sonnet Brief — User Documentation: content build-out

**Depends on `brief-docs-factory-infrastructure.md`** (DF1–DF5 must be landed and green). This brief
writes the **words and the figures** of the user-facing documentation — the HTML set under `docs/user/`
that the app's Help buttons open through `DocLauncher`.

**Where findings go: `src/Ui/RESOLVED.md`.** **Do not write in any `CLAUDE.md`.**

---

## Gate command

```
dotnet run --project tools/DocGen -- --out docs/user
dotnet test tests/Ui.Tests --no-build
```

Generation must succeed with **zero** lint failures, zero unresolved placeholders and zero unresolved
cross-links (the docs-factory brief §10 makes all three blocking). **This brief adds
`docs/user/src/**.md` and figure-catalog rows. It changes no engine behaviour. If you find yourself
fixing a bug in `src/Core` or `src/Engine` to make a doc true, stop and report it** — the doc describes
what ships, and a discrepancy is a finding, not a doc edit.

---

## 0. Read this first

### 0.1 Who these docs are for

Working RF engineers. They already know what a Smith chart, an S-parameter and a load-pull contour are.
They do **not** know circuitRF's conventions, its file layout, or which of its buttons does what. Write
for that reader: practical, specific, worked examples, no tutorial-on-RF padding and no marketing voice.

### 0.2 Source material — read before writing each chapter

`docs/design/` is the authoritative background and it is extensive. For each chapter below, the brief
names the design doc(s) to read first. **The design docs are written for implementers and are frequently
more honest than a user doc should be terse about — your job is to translate, not to transcribe.**
Where a design doc records a limitation, the user doc must state that limitation plainly. Where a design
doc records an internal rationale, it usually does not belong in the user doc at all.

### 0.3 Every figure is generated

No bitmaps, no hand-drawn mock-ups, no screenshots. Every image is a `{{ui: …}}`, `{{symbol: …}}` or
`{{toolbar: …}}` placeholder backed by a catalog row you add in `src/Ui/Diagnostics/`. If a figure this
brief asks for cannot be captured, **say so and report it** — do not substitute a PNG.

### 0.4 The anchor contract

`DocLauncher` deep-links Help buttons to `reference/components.html#<symbolkind-lowercase>`,
`reference/simulations.html#<analysis>`, `reference/plot-types.html#<type>`. Adding a component or an
analysis means adding the matching anchor. The generator tests this (docs-factory §10.3); do not
hand-maintain it.

---

## 1. Site structure — every page reachable by browsing

The owner's requirement: *a user can browse through every page in a web browser if desired.* That means:

- A **complete table of contents** on `docs/user/index.html`, listing every page in every section — not
  just the three top-level guides it lists today.
- **Previous / Next links** at the foot of every page, forming one linear reading order through the
  whole set, so a reader can start at the top and reach the end without going back to the index.
- A **breadcrumb** on every page (already the convention — keep it).
- **No orphans.** Add a generator check: every emitted page is reachable from `index.html` by following
  links, and every page has a Prev/Next except the first and last. Report any page that fails.

Proposed section order (adjust if a better reading order emerges, but keep it *one* order):

```
Getting started  →  Quick Start · New User Guide
Core concepts    →  Units · Expressions · Grid & connectivity · Pins/Ports/Terms · File formats
Design           →  Components · Dynamic symbols · Symbol editor · SDD · Nonlinear capacitor · Match
Layout & EM      →  Layout editor · PCells · PDK integration · PDK authoring · MoM engine · EM setup
Simulate         →  Simulations · Measurements · Netlist · Data Display · Plot types · .npy export
Tools            →  harmonicaRF · wBond
```

---

## 2. Components (`reference/components.html`) — update and complete

**Read:** `docs/design/standard-library-symbols.md`, `docs/design/data-model.md` §5,
`src/Core/Devices/Fet/FetModelBase.cs`'s header, the component-model factory.

The docs-factory brief adds the **fifteen missing components** to the symbol generator
(`Diode FetAngelov FetCurtice FetCurticeCubic FetMaterka FetStatz Match MBend MCross Mklopf Mlin
Mtaper MTee VerilogA WBond`) and makes every symbol render **with its connection leads and its pins in
the unconnected state**. This brief documents them.

For **each** component: what it is and when you would use it, its symbol figure, and a parameter table
(name · default · unit · what it does · whether it shows on the schematic). Generate the tables from the
live registry via `{{table: components/<Kind>}}` — do not re-type defaults.

### 2.1 The FET family — the part the owner called out specifically

Five native large-signal models: **Angelov, Curtice (quadratic), Curtice cubic, Materka, Statz**, all on
`FetModelBase`. Document, for each, what the model is for and where its parameters come from, then cover
the shared behaviour once:

- **Terminals and ports:** three nets `gate drain source`, mapped as port 0 = (gate, source),
  port 1 = (drain, source) — so `v[0]` is Vgs and `v[1]` is Vds, the form published FET equations use.
- **What is modelled:** drain current and its derivatives (gm = ∂Id/∂Vgs, gds = ∂Id/∂Vds), optional
  forward gate conduction as a diode, and gate charge.
- **What is NOT modelled — state this plainly:** the Statz/TOM charge formulation, transit-time delay,
  breakdown, and self-heating. A user choosing a model needs to know what is absent.

**`Cgs`, `Cgd` and `CapModel` — answer the owner's question directly on the page:**

| `CapModel` | Gate charge | Are `Cgs`/`Cgd` linear? |
|---|---|---|
| `0` | none at all | n/a — no charge storage |
| `1` *(default)* | **constant** `Cgs`/`Cgd` | **Yes — linear.** They are fixed capacitances, bias-independent |
| `2` | **junction** (depletion) charge, applied to Vgs and Vgd separately | **No — bias-dependent.** `Cgs`/`Cgd` are the zero-bias values `Cj0` |

For `CapModel = 2`, give the form the code implements and name its parameters:

```
Q = Cj0·Vbi/(1−M) · [ 1 − (1 − V/Vbi)^(1−M) ]      for V < Fc·Vbi
                                                    continued by its tangent above Fc·Vbi
parameters: Cgs, Cgd, Vbi, M, Fc
```

Say why the choice exists: **the published models disagree on gate charge**, so it is a parameter rather
than a per-model decision. Say which to pick: `1` for speed and for parameter sets extracted with fixed
capacitances; `2` when the extraction gives junction parameters and the swing is large enough for the
bias dependence to matter.

Note also for the reader: `Cgd` sees Vgd = Vgs − Vds, not Vgs.

### 2.2 The rest

- **Diode**, **VerilogA** / external device — what they are, how a model is supplied (cross-link to the
  PDK chapters).
- **Microstrip family** (`Mlin`, `MBend`, `MTee`, `MCross`, `Mtaper`, `Mklopf`) — parameters, what the
  models assume, and the cross-link to the layout/PCell chapter (MLIN is the worked PCell example there).
- **WBond** — short entry that links to the wBond chapter (§9).
- **Match** — short entry that links to the Match chapter (§10).

---

## 3. Data Display

**Read:** `docs/design/data-display.md`, `trace-card.md`, `plot-versus.md`,
`loadpull-contours.md`, `results-dataset-layout.md`.

Cover: what the Data Display is, plots vs tables, adding traces, the trace card, axes/limits, markers,
and how a `DataSet`'s cubes map onto traces.

**Figures required** (each a catalog row with a real fixture and real data):

| Figure | Content |
|---|---|
| `plot-inspector-trace-card` | The Plot Inspector showing a trace card with a **real example trace** |
| `plot-inspector-hb` | A trace card configured against a **HB result** |
| `plot-inspector-loadpull` | A trace card configured against a **loadpull trace** |
| `plot-rectangular-data` | An example plot **with data in it** — not an empty axis frame |
| `plot-loadpull-contours` | A plot with **loadpull data rendering inside it** (contours on the Γ plane) |

Explain what a reader is looking at in each. For the loadpull figure, explain the contour readout and
what the markers mean — cross-link to `loadpull-contours` background rather than repeating it.

---

## 4. Layout editor

**Read:** `docs/design/layout-view.md` (§1 units/DBU, §5 snapping), `pcell-contract.md`,
`pcell-parameter-handles.md`, `placement-connectivity-and-drag-follow.md`, `schematic-hierarchy-navigation.md`.

This chapter must be **detailed** — it is currently the thinnest area of the docs.

### 4.1 Orientation

- Figure: **the workspace with an example workspace open, a layout document open, and a sample
  primitive drawn.** Full `WorkspaceWindow` capture (docs-factory §3.3).
- What the layout view is for, how it relates to the schematic view of the same cell, the project tree,
  and what a technology (`.ctech`) contributes.

### 4.2 Schematic ⇄ layout parameter flow

Describe **both directions**: moving a component's parameters from schematic to layout, and back.
Be concrete — name the commands/menu items, show what changes, and state what happens when a value
cannot be represented in the other view. This is a genuine workflow question and vague prose is worse
than none.

### 4.3 PCells — explain them to a user who has not met the term

What a PCell is, why a parameterised cell beats a fixed one, how you place and re-parameterise it, and
what happens when the technology changes underneath it. **Use MLIN as the worked example** end to end:
place it, set W and L, see the artwork regenerate. Introduce **parameter handles**
(`pcell-parameter-handles.md`) here — dragging a handle *is* editing a parameter.

### 4.4 Geometry snap — full treatment

The owner asked for this specifically:

- How to **toggle it on and off**, and what the snap tolerance means.
- **What it snaps to.** The feature kinds are, in priority order (highest first):
  **Pin · Corner/Endpoint · Intersection · Midpoint · Centroid · Nearest**. Explain the priority — the
  more *intentional* a feature is, the higher it ranks — because that is what makes the behaviour
  predictable when several candidates are near the cursor.
- **A figure showing every snap glyph**, and a table saying what each one indicates. Render the real
  glyphs from the real renderer (a catalog fixture that places the cursor near each feature type in
  turn), not redrawn approximations.
- Note that **intersections are computed live near the cursor** rather than indexed, and that they can be
  toggled separately — a user who does not know this will not understand why they behave differently.
- **Cross-link to the Units page** (§11) from this section — snap distances are in DBU/display units and
  that is where the reader will first meet the question.

### 4.5 Toolbar

`{{toolbar: layout}}` — the generated figure plus the generated per-button table (docs-factory §5).

---

## 5. Toolbars for every editor

One section per editor, each `{{toolbar: …}}` + generated table, with a sentence of prose per button
group explaining *when* you would reach for it (the table gives *what* it does):

- **Schematic** editor
- **Symbol** editor
- **Layout** editor (in §4.5)
- **Data Display**
- **wBond** editor (in §9)

Place the Schematic and Symbol toolbars in their existing pages; do not create a separate "toolbars"
page — a reader looks for the button where they are, not in a catalogue.

---

## 6. PDK integration

**Read:** `docs/design/pdk-import.md`, `pdk-external-devices.md`, `pcell-contract.md`.

**Hard constraint from the owner: name no vendor and no product.** Write entirely in terms of "a kit",
"a vendor kit", "a supplied model library". If an example needs a name, invent a neutral one
(`ExampleKit`). Check the finished page for accidental names before you hand it in — this is on the
double-check list (§14).

Cover:

- What a PDK is in circuitRF's terms and what a kit contributes (symbols, models, technology, artwork).
- **circuitRF supports the OpenPDK file structure** — link to <https://github.com/fossi-foundation/open-pdks>.
- How a kit is imported and where its parts land in the project tree.
- How a kit's compiled/external device models are evaluated (the device-worker route), at a user's level
  of detail: what to expect, what a failure looks like, what to check first.
- **PCells can be authored in Python** — introduce it here and link forward to §12 (PDK Authoring),
  which carries the worked examples.

---

## 7. The MoM engine — a full chapter

**Read:** `docs/design/layout-view.md` §10 in full (§10.1 scope, §10.3 kernel, §10.5 meshing,
**§10.6 ports and de-embedding**), `mom-wirebond-kernel.md`, and the L8/L9 and EM-performance briefs in
`docs/sonnet-briefs/` for the adaptive-sweep and conformal-cell material.

This is the largest chapter. Structure it so a novice can read the first third and stop, while an expert
can skip to the middle.

### 7.1 In plain terms

What an electromagnetic solve buys you over a circuit model, in a paragraph a working engineer who has
never run an EM simulator can act on.

### 7.2 What can and cannot be simulated — be explicit and honest

Two lists, stated plainly:
- **Can:** the planar/2.5D structures the engine is built for — say exactly which.
- **Cannot:** the structures it is not for. Read §10.1 and §10.3.5 and carry those limits across. A user
  who discovers a limit by getting a wrong answer has been failed by the documentation.

### 7.3 For advanced users: how circuitRF implements MoM

**Detailed but succinct** — the owner's words. One tight section: the formulation, the basis functions,
the meshing approach including the edge mesh, the Green's-function treatment for a layered medium, the
de-embedding scheme, and the acceleration. An expert should be able to judge the engine from this
section; a novice should be able to skip it without losing the thread. Do not turn it into a paper.

### 7.4 Using it — the EM Setup UI

Walk the real panel, control by control, with figures. Include the stackup, the mesh settings, the
frequency plan, the boundary model, and where results land.

### 7.5 Ports

- **What a port is** in this engine, and how you define one.
- **The port types**, and — for each — **what application it is best suited to.** Give the reader a
  decision rule, not just a list.
- **Auto-ports:** what gets created automatically, when, and how to override it.
- Port Z0, including per-port overrides.

### 7.6 De-embedding — follow `layout-view.md` §10.6 closely

- What de-embedding does and what reference plane it establishes.
- **Where the reference plane sits, and that it is not user-positionable** — §10.6 states this as a
  limitation; the user doc must too.
- **Rules of thumb for port setup that gives good de-embedding.** The governing physics is that
  **de-embedding accuracy is limited by radiation** — a port whose feed line radiates cannot be cleanly
  de-embedded. Turn that into practical rules: feed-line length, width, proximity to the structure and
  to other ports, what to avoid. Give numbers where §10.6 supports numbers, and say "rule of thumb"
  where it does not.
- What a *good* and a *bad* de-embedded result look like, so a user can tell which they have.

### 7.7 Adaptive frequency sampling

What it is, what problem it solves (a resonant response sampled on a uniform grid is either wrong or
slow), how it works — sample, fit a rational interpolant, add samples where the model and the data
disagree, stop on a tolerance — and what the user's tolerance knob actually trades. Say how to tell it
converged and what to do when it has not.

### 7.8 Conformal boundary cells and convex decomposition

What each is, and how it affects **accuracy and/or performance**. **Read
`brief-conformal-boundary-cells.md` before writing this**: the feature ships **off by default** because
it regresses on one class of board, and the documentation must say when to turn it on and when not to.
Do not write it as a free win.

### 7.9 Worked example — a microstrip line with a bend

An end-to-end tutorial: draw it, set the stackup, place ports, choose a mesh, run, read the result,
sanity-check it against the circuit model. Real numbers, real figures, and the resulting S-parameter
plot rendered as vector.

### 7.10 Topics the owner did not list — find them and add them

Read §10 and the L8/L9 briefs for anything a user must know that is not above. Strong candidates:
mesh convergence and how to check it, memory/time scaling and what makes a run infeasible, the accuracy
budget, substrate/stackup authoring, symmetry and ground planes, what the engine refuses to run and why
a refusal is better than a wrong answer, and how EM results are re-used in a circuit simulation
(co-simulation). **List what you added in your completion report** so the owner can see the delta.

### 7.11 The EM Setup Help button

Add a **subtle Help button** in the EM Setup UI (`src/Ui/Views/Layout/EmSetupEditorView.axaml`) that
opens this chapter through `DocLauncher`, following the existing deep-link pattern. Subtle: an icon
button in the panel header, consistent with the other Help affordances — not a prominent call to action.
Add the anchor and the `DocLauncher` route, and cover it with the anchor-contract test.

---

## 8. harmonicaRF — its own page

**Read:** `docs/design/harmonicarf.md` (§4.3 for DUT kinds, §4.5 for the planes, the R-series briefs for
the UI).

- **High-level description and purpose.** What it is, what it is not (not a general circuit simulator:
  one DUT, two termination planes, an optional package).
- **The active-loadpull framing, which the owner wants stated:** harmonicaRF can act like an **on-wafer
  Active Loadpull simulator** — a user can explore which terminations they want to measure *before*
  committing to a bench measurement that could damage the DUT or the wafer probes. Write it as the
  practical risk-reduction argument it is.
- **Supported DUT models** — enumerate them from §4.3, including the s2p/s4p/s6p embedding and the
  lumped package, and the rule that the source is always grounded (a 3-port DUT has its source port
  grounded).
- **UI guide** with figures: the Smith chart, the readout strip, the display, the menus.
- **Interaction, called out explicitly:**
  - markers can be **clicked and dragged** around the Smith chart;
  - configuration settings in the display can be **double-clicked to edit them in place**.
- **Preset terminations** — Class **B**, **J**, **J\***, **F** and **F⁻¹**, with their keyboard
  shortcuts, what each preset sets, and when a designer would reach for it. Cite the source for the
  termination values:

  > Sharma, T. (2018). *Modelling and Design Methodology of Higher-Efficiency Harmonic Tuned Power
  > Amplifiers for 5G Applications* (Doctoral thesis, University of Calgary).
  > <https://prism.ucalgary.ca/handle/1880/106695>

- The `.charm` document type (cross-link to file formats, §13).

---

## 9. wBond — its own page

**Read:** `docs/design/wbond.md` in full, `mom-wirebond-kernel.md`, and the wBond round briefs.

- **Schematic symbol figure** using an example with **four arrays: G1, G2, D1, D2.**
- **State clearly that wires are designed in the layout view, not the schematic** — the symbol is the
  circuit-side handle; the geometry lives in layout.
- **Layout figure of the G1/G2/D1/D2 arrays with at least eight wires.**
- **How inductance is calculated:** self inductance of an individual wire, **mutual** inductance between
  wires, and **the derivation of the array-basis calculation** — show the equations. This is the section
  an RF engineer will judge the tool by; get it right and keep it tight.
- **Arrays:** how wires are grouped into arrays, and how the **profile editor edits every wire carrying
  that profile** at once.
- **Loop height and span:** give the definitions with a labelled figure.
- **The `<alt>` drag.** The owner wants **heavy emphasis**: holding **`alt`** while dragging a wire
  profile adjusts **both its loop height and its span** together. Give it its own callout, not a
  parenthesis.
- **Saving and sharing:** designs save to the **`.wBond`** format and can be shared.
- **DXF import/export**, including **the layer keyword used to import wires from DXF**. Name it exactly.
- **Parameters table** — with real emphasis on the two the owner flags as mattering most to users:
  **Use Capacitance** and **εr**. Say what each changes and when it matters.
- **The 3D MoM kernel (complete).** What it solves, and **how it solves fast** — the actual mechanism,
  not adjectives. Then **compare and contrast with an FEM solver**: what each is good at, what each
  costs, where they agree. **Do not over-sell** — the audience is engineers, and an unearned claim
  destroys the page's credibility. State the regimes where FEM is the better tool.
- **S-parameter export**, both **lumped** and **distributed** — what the difference means physically and
  which to choose.
- **A simple EM example**: the UI configuration figure **and** the resulting S-parameter solution
  rendered as a **vector plot** (no bitmap).
- wBond toolbar (`{{toolbar: wbond}}`).

---

## 10. The Match component — its own chapter, with a prominent link

**Read:** `docs/design/match.md` in full.

The owner asked for this twice; it is one chapter, linked prominently from the components page and from
the section index.

- What Match is: a two-port component that **synthesises a bandpass LC matching network matching both
  ports simultaneously**, and that **absorbs each termination's reactance into the network** rather than
  tuning it out — so a transistor's Cgs or Cds becomes an element of the matching filter and the match
  holds over the widest bandwidth the load's Q permits. Say why that matters (Fano).
- It is **direct synthesis** — closed-form, no optimiser.
- **The UI**, with figures: the specification pane, the ladder preview, the response plots, the linked
  Norton-transform slider rack with its locks, and the solutions list. Explain that the sliders move
  element values into manufacturable ranges **without changing the frequency response**, which is the
  point of the window.
- **Reference the technical sources** the synthesis rests on, as `match.md` records them.
- **Worked example: a two-stage FET interstage match for a PA application** — with a figure of the UI
  **showing the solved values**. (The owner wrote "interstate"; the intended term is *interstage*.)
- **How to flatten it to a cell** — what flattening produces, when you would do it, and what you lose.

---

## 11. Units — a full page

**Read:** `docs/design/layout-view.md` §1.1–§1.5, `expressions.md`, `wbond.md`.

- **What a DBU is** — the integer database unit, why layout coordinates are integers, and how circuitRF
  stores them. `DbuPerMicron` defaults to **1000** (1 DBU = 1 nm), deliberately: 1 µm = 1000 DBU and
  1 mil = 25400 DBU are **both exact integers**, so metric-authored and imperial-authored layouts are
  both exactly representable.
- **Storage vs display.** A vertex at 25400 DBU displays as `25.4 µm` or `1 mil` depending on the
  display unit; the stored value never changes. Make this distinction unmistakable — it is the single
  most common source of confusion.
- **Changing the DBU resolution is a migration**, not a preference: state the rule (exact integer ratio,
  explicit, undoable, validated) and why.
- **Layout units** and the snap grid (defaults: 1 µm for PCB, 5 nm for MMIC).
- **wBond units.**
- **Entering units in text boxes:** what forms are accepted, and the **SI prefix shortcuts** —
  `m` = milli, `u`/`µ` = micro, `n` = nano, `p` = pico, `k` = kilo, `M` = mega, `G` = giga (list the
  full set the parser accepts, from the expression engine, and check it against the code rather than
  assuming). Show worked examples: `1n`, `2.2p`, `10k`, `1.8G`.
- A warning the owner has been bitten by: **a unit is a row field, not part of an expression** — writing
  a unit inside an expression can fail to parse. State the correct way to enter each.
- **Link to this page** from the layout editor chapter (§4.4), the wBond chapter, the expressions page,
  the components page and the PCell/PDK-authoring chapters.

---

## 12. PDK Authoring — a full page

**Read:** `docs/design/pcell-contract.md`, `pcell-parameter-handles.md`, `pcell-wire-schema.md`,
`tools/pcell-python/README.md`, and the working example at `tools/pcell-python/example/mlin.py`.

Same **no-vendor-names** constraint as §6.

- **circuitRF supports the OpenPDK standard** — <https://github.com/fossi-foundation/open-pdks> — so
  users should follow it. Describe the structure they need to produce.
- **PCell generation with Python**: the contract, how circuitRF invokes a Python PCell, the API surface
  a script sees, and how parameters are declared. **Include `PCell Parameter Handles`** — what they are
  and how a script exposes one.
- **A simple worked example, complete**: schematic side, model, technology and artwork for one part.
  A **microstrip line** is the right example and `tools/pcell-python/example/mlin.py` already exists —
  use it, do not invent a second one, and keep the page and the file in step.
- **A spiral inductor layout example with parameters** — the owner asked for this *if it is easy*. It is
  the natural second example because it shows a real parameterised sweep of geometry (turns, width,
  spacing, inner diameter). **If it turns out not to be easy, say so and leave it out with a one-line
  note in your report** rather than shipping a half-working script.
- A parameter-typing trap worth a callout: kit PCell parameters are commonly declared as **text**, and a
  numeric value supplied where text is expected can be silently ignored (falling back to a default) —
  which shows up as wrong artwork, not as an error. Tell authors to check the produced geometry.

---

## 13. Corrections to existing pages

### 13.1 `reference/simulations.html`

- **"HOW RESULTS ARE STORED" is wrong and must be rewritten.** Results are now stored **wherever the
  user puts them**, specified in the **Analysis Setup**. Describe the actual control, the default, and
  what a user should do to organise results.
- **Harmonic Balance can now be run from the command line.** Document it with a real, runnable example:

  ```
  dotnet run --project src/Cli -- hb <netlist.cnl> -o out.mat
  ```

  Cover: the `hb` verb runs single- and multi-tone HB; a `parametric_sweep` wrapping an HB runs the
  **whole sweep** (name the wrapper, not the inner analysis — naming the inner one silently drops the
  sweep axis); `--set var=expr` overrides a global before elaboration; `-o out.{mat,npy,txt}` exports;
  and `measure` lines are evaluated exactly as the GUI does. Verify the exact syntax against
  `src/Cli` before publishing it.

### 13.2 `reference/npy-export.html`

Same "how results are stored" correction, kept consistent with §13.1.

### 13.3 `reference/file-formats.html`

Add the document types that are missing or stale:

| Extension | What it is |
|---|---|
| `.ctech` | Technology |
| `.cem` | EM setup |
| `.clay` | Layout view — **the page currently calls this "a v1 placeholder (folder present, empty)", which is stale.** Rewrite it: the layout view is real |
| `.charm` | harmonicaRF document |
| `.wBond` | wBond design |

Check the rest of the table against reality while you are in there; if another row is stale, fix it and
note it.

---

## 14. Double-check pass — do this before reporting done

Run this as a **separate, explicit pass** after the writing is finished. Do not merge it into the
writing; the point is to re-read the requirements against the shipped pages with fresh eyes.

**14.1 Requirement traceability.** Walk this checklist against the *generated HTML*, not against your
memory or your outline. Every box needs a page and an anchor:

- [ ] Every new component documented: description + parameter table with defaults and meanings
- [ ] Symbol SVGs regenerated for **all** components, **with connection leads**, **pins shown
      unconnected**
- [ ] FET parameters described, for all five models
- [ ] **`Cgs`/`Cgd` explained, including whether they are linear** (linear at `CapModel=1`,
      bias-dependent at `CapModel=2`)
- [ ] **`CapModel` documented** — all three values
- [ ] Data Display documented
- [ ] Plot Inspector figure with a trace card and an example trace
- [ ] Trace card figure for an **HB** result
- [ ] Trace card figure for a **loadpull** trace
- [ ] Example plot **with data in it**
- [ ] Plot with **loadpull data rendering inside**
- [ ] Layout editor: workspace figure with a layout document open and a primitive drawn
- [ ] Schematic → layout **and** layout → schematic parameter movement described
- [ ] PCells explained, with **MLIN** as the example
- [ ] Geometry snap: on/off toggle, what it works for
- [ ] **Snap glyph figure + a table of what each glyph indicates** (corner, midpoint, centroid,
      intersection, pin, nearest)
- [ ] Toolbar figures + per-button descriptions for **Schematic, Symbol, Layout, Data Display, wBond**
- [ ] Toolbar images are **generated by code**, not hand-made
- [ ] PDK integration page — **no vendor or product names anywhere** (grep the page)
- [ ] OpenPDK structure mentioned + GitHub link
- [ ] Python PCell scripting mentioned with a user-authorable example (microstrip line)
- [ ] Spiral inductor example **or** an explicit note saying why it was left out
- [ ] MoM chapter: layman explanation
- [ ] MoM: what **can** be simulated / what **cannot**
- [ ] MoM: advanced implementation summary — detailed but succinct
- [ ] MoM: UI walkthrough, how to get EM results
- [ ] MoM: what ports are and how to define them
- [ ] MoM: port **types** and the best application for each
- [ ] MoM: de-embedding per `layout-view.md` §10.6
- [ ] MoM: **rules of thumb for port setup**, framed on radiation limiting de-embedding accuracy
- [ ] MoM: auto-ports
- [ ] MoM: worked example — **microstrip line with a bend**
- [ ] MoM: **adaptive frequency sampling** — what, why, how it works
- [ ] MoM: **conformal boundary cells and convex decomposition** — accuracy/performance, and that it
      ships off by default
- [ ] MoM: additional topics found and added — **listed in the completion report**
- [ ] **Subtle EM Setup Help button** wired to the EM chapter
- [ ] harmonicaRF page: description, purpose, UI guide
- [ ] harmonicaRF: supported DUT models
- [ ] harmonicaRF: **on-wafer Active Loadpull** framing, including protecting the DUT and probes
- [ ] harmonicaRF: **drag markers on the Smith chart**
- [ ] harmonicaRF: **double-click config settings in the display to edit**
- [ ] harmonicaRF: preset terminations **B, J, J\*, F, F⁻¹** + the **Sharma (2018)** citation and URL
- [ ] wBond: schematic symbol figure with arrays **G1, G2, D1, D2**
- [ ] wBond: states that wires are designed **in layout, not schematic**
- [ ] wBond: array figure with **≥ 8 wires**
- [ ] wBond: self **and** mutual inductance calculation shown
- [ ] wBond: **array-basis derivation with equations**
- [ ] wBond: grouping into arrays; profile editor edits all wires with that profile
- [ ] wBond: **loop height and span defined**
- [ ] wBond: **`<alt>` drag adjusts loop height AND span — heavily emphasised**
- [ ] wBond: `.wBond` save/share
- [ ] wBond: **DXF import/export + the layer keyword**
- [ ] wBond: parameters, with **Use Capacitance** and **εr** given weight
- [ ] wBond: 3D MoM kernel and **how it solves fast**
- [ ] wBond: **compared with FEM, without over-selling**
- [ ] wBond: S-parameter export, **lumped and distributed**
- [ ] wBond: EM example UI config figure **+ vector S-parameter plot**
- [ ] Match: its own prominent link and chapter
- [ ] Match: UI figures
- [ ] Match: technical references cited
- [ ] Match: **two-stage FET interstage** example with **solved values** in the UI figure
- [ ] Match: **flatten to cell** explained
- [ ] Units page: **DBU**, storage vs display units
- [ ] Units: layout units, wBond units
- [ ] Units: entry in text boxes, **`m` = milli and the other prefixes**
- [ ] Units page **linked from the layout editor docs** and other relevant pages
- [ ] PDK Authoring page: OpenPDK standard + link
- [ ] PDK Authoring: Python PCells **including parameter handles**
- [ ] PDK Authoring: simple example covering **schematic, model, technology and artwork**
- [ ] `simulations.html` "HOW RESULTS ARE STORED" corrected
- [ ] `npy-export.html` same correction
- [ ] `simulations.html`: **HB from the CLI, with an example command**
- [ ] `file-formats.html`: `.ctech`, `.cem`, `.clay`, `.charm`, `.wBond` — and `.clay` no longer called
      a placeholder
- [ ] **Every page reachable by browsing**: full TOC, Prev/Next chain, no orphans

**14.2 Mechanical checks.**

- Regenerate from clean and confirm: zero lint failures, zero unresolved placeholders, zero unresolved
  cross-links, zero orphan pages.
- **Grep the whole `docs/user/` tree for bitmap references** (`.png`, `.jpg`, `.jpeg`, `.gif`). There
  should be none except the favicon path if it is one. Any hit is a defect.
- **Grep the PDK pages for vendor and product names.** Any hit is a defect.
- Click every Help button in the app and confirm it lands on a real anchor.
- Read each page in **both light and dark**; a figure that is illegible in one theme is not done.

**14.3 Report honestly.** In your completion note, state:
- every checklist item you could **not** complete and why;
- every place a design doc and the shipped behaviour **disagreed** (document what ships, and report the
  discrepancy — do not quietly document the intent);
- the MoM topics you added beyond the owner's list;
- total generation time and the emitted byte total (docs ship inside the app bundle).

**Do not report done with unticked boxes and no explanation.** An unticked box with a reason is a
result; an unticked box without one is a defect.

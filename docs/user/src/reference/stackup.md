---
title: The Stackup
slug: reference/stackup.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Stackup
lede: The layers your board is actually made of — and, because it decides the ground plane, where the negative terminal of every EM port is.
keywords: dielectric, substrate, layers, board, PCB, Er, epsilon r, permittivity, loss tangent, tan delta, copper thickness, prepreg, technology, ctech
---

Your layout is a set of flat shapes. The **stackup** is what turns those shapes into a physical
structure: what each conductor is made of, how thick it is, what dielectric separates it from the
next one, and — the part that catches people — **which conductor is ground**.

That last one is not a detail. An EM port has two terminals, and only one of them is anywhere near
your artwork: the other is the ground plane. **You do not set it per port. The stackup does, for
every port in the run at once.**

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#where">Where the stackup lives</a></li>
<li><a href="#anatomy">Anatomy of a stackup</a></li>
<li><a href="#tab">The Stackup tab, field by field</a></li>
<li><a href="#ground">Where your port's negative terminal is</a></li>
<li><a href="#levels">Which conductors get simulated</a></li>
<li><a href="#sheet-surface">Where a conductor's thickness goes</a></li>
<li><a href="#slab">The slab, and what makes one solvable</a></li>
<li><a href="#vias">Vias</a></li>
<li><a href="#mim">A thin-film (MIM) capacitor</a></li>
<li><a href="#ignored">What the EM engine does not read</a></li>
<li><a href="#check">Checking what it resolved to</a></li>
</ol>
</nav>

## Where the stackup lives {#where}

The stackup is part of a **technology** — a `.ctech` file in the workspace's `tech/` folder, edited
in the technology editor's **Stackup** tab. It belongs to the technology rather than to a layout,
because it describes a *process*: every board built on that process has the same layers, whatever is
drawn on them.

A layout names a technology (or leaves the name blank to mean the workspace default). Everything
else — the layer table, the DRC rules, the default display unit — lives in the same file. See
[The Layout Editor](layout-editor.html#technology) for the rest of it.

### The technologies circuitRF ships {#shipped}

New Workspace, **File ▸ Import ▸ Gerber** and `circuitrf new workspace --tech` all offer these. Each one
is copied into the workspace's `tech/` folder, and from then on it is your own file to edit.

| Technology | Conductors | Overall | Drill spans |
|---|---|---|---|
| PCB 2-Layer FR-4 (70mil, 1oz) | 2 | 1.85 mm | through |
| PCB 2-Layer RO4350B (20mil, 1oz) | 2 | 0.58 mm | through |
| PCB 2-Layer RO4350B (30mil, 1oz) | 2 | 0.83 mm | through |
| PCB 4-Layer FR-4 (62mil, 1/0.5oz) | 4 | 1.58 mm | through, Top Copper to Inner 1 |
| PCB 6-Layer FR-4 (1.6 mm, 1/0.5oz) | 6 | 1.6 mm | through |
| PCB 10-Layer FR-4 (1.7 mm, 1/0.5 oz) | 10 | 1.718 mm | through 1-10, laser 1-2, 2-3, 8-9, 9-10, buried 3-8 |
| MMIC GaAs (2 Layer Metal + MIM, 100um) | 4 | 0.112 mm | backside via, Metal1-Metal2 post, MIM via |

The ten-layer board is a 2+6+2 build with ground planes on Inner 1 and Inner 8, so each outer signal
layer sits 71 µm above or below its own plane. Its bottom side is open air rather than a ground plane,
so Bottom Copper can carry signal artwork like Top Copper.

**A missing technology does not block drawing, but it does block an EM run.** Layers fall back to a
generated palette and editing carries on; the EM run refuses rather than inventing a stackup, because
there is no honest default for "how far above the ground plane is this trace".

## Anatomy of a stackup {#anatomy}

A stackup is an ordered list, **top to bottom**, of three kinds of entry, plus two boundary
conditions for what is above and below the whole sandwich. Seen in cross-section, it is a sandwich:

{{ui: stackup-mmic}}

| Entry kind | What it is | What it carries |
|---|---|---|
| **Conductor** | A metal layer | Thickness, conductivity σ, the **drawing layers** that map onto it, and whether it is the **ground reference** |
| **Dielectric** | The material between two conductors | Thickness, ε<sub>r</sub>, tanδ, µ<sub>r</sub>, σ |
| **Via** | A connection *between* two conductors — not a layer of the sandwich | The drawing layer via shapes are drawn on, the two conductors it **spans by name**, and its fill (plated or solid) with a wall thickness |

### That picture is in the application, and you edit it there {#cross-section}

The cross-section above is not a diagram drawn for this page. It is the upper half of the
**Stackup** tab, drawn from whichever `.ctech` you have open, and it is the main way to work on a
stackup: the thing a list cannot tell you — which metal is on top, how thick the substrate is
against the copper on it, which two conductors a via actually joins — is the thing a cross-section
says at a glance.

The card list underneath it edits the same entries and always agrees with it. They are two **views**
of one stackup, not two copies: whichever surface you change it on, the other follows immediately,
the **Undo** and **Redo** buttons at the top of the window cover it, and the layouts you have open
repaint against the changed technology without waiting for a save.

In roughly the order you will meet them:

| Do this on the drawing | What it does |
|---|---|
| **Click** a layer or a via | Selects it — an outline round it on the drawing, and the card list below scrolls to that entry and shades it, so a click on the picture lands you on the fields. If the filter box was hiding that card, the filter clears, because a click that appears to do nothing is the worse outcome. |
| **Double-click** a value | Opens a small edit box **on the number itself** — the same box the schematic editor uses. `Return` commits, `Esc` reverts, clicking away commits. Names, thicknesses, σ, ε<sub>r</sub>, tanδ, µ<sub>r</sub> and a via's wall are all typeable here; a via's **span** is not, because it is a pair of conductor names rather than a number — right-click, or use the card. |
| **Drag** a layer up or down | Reorders the stack. The order top to bottom **is z**, so this is the same edit as the ↑ ↓ buttons on the card. |
| **Drag** a via's end gripper | Moves that end of the span to another conductor. The small handle at each end of a barrel is what you grab; the drawing refuses a span it cannot resolve rather than silently snapping to something else. |
| **Drag** a via's barrel up or down | Moves the whole span, both ends together. |
| **Drag** a via sideways | Slides it across the picture, snapping to a column so two vias can be lined up by eye; hold `Alt` to place it freely. This is **cosmetic only** — it is where the via is *drawn*, never where it is. See [Vias](#vias). |
| **Right-click** anything | A menu built from what is under the pointer, and it selects that entry first so you can see what you are about to act on: **Delete Conductor / Dielectric / Via**, plus the choices that are not typed numbers — **ground reference** and **metal thickness goes to** on a conductor, **Add Via** and **patterned with** on a dielectric, **plated hole** and **fill** on a via, and the drawing-layer binding on either. |
| **Delete** | Removes the selected layer or via. It is the same deletion as the card's ✕ and the menu's **Delete Conductor / Dielectric / Via**, so one `Ctrl`/`Cmd`+`Z` puts it back — and nothing is asked first, because that undo is the confirmation. On a Mac keyboard the key marked *delete* is the one to press. A field you are typing in keeps the key for its own text.
| **Esc** | Clears the selection and all its highlighting. With an edit box open, the first `Esc` reverts the edit and the selection stands; a second clears it. |
| **Right-click ▸ Copy** | Puts the **whole** cross-section on the clipboard as a picture — vector where the receiving application takes it, and never a crop of what happened to be scrolled into view. |

The drawing scrolls when a stack is taller than its pane, and the divider between it and the cards
drags. Neither pane can be dragged out of existence — to give one of them the whole tab, use the two
small chevron toggles at the left of the summary row, which collapse the drawing and the card list
respectively.

### How tall each layer is drawn {#heights}

**Heights are relative within a kind and never across kinds.** A conductor twice the thickness of
another conductor draws twice as tall, and so does a dielectric against another dielectric — but a
metal is never drawn to scale against a substrate, because on a real process it would be a hairline —
the MMIC stackup above puts 3 µm of metal beside 100 µm of substrate, and the metals are what most of
this chapter is about. **Where a kind's own range is too wide to draw even on its
own, the heights are compressed while staying in order**, so a thicker layer still looks thicker; the
same MMIC process spans 0.2 µm to 100 µm of *dielectric* alone, which is 500:1 and would make either
the capacitor film a hairline or the substrate four screens tall.

The drawing carries no note about this, deliberately — a caveat printed on every stackup anyone ever
looks at is worse than a paragraph here. What it does carry is **the real thickness, printed on
every layer**, which is what you actually need to read off it.

<div class="callout note">
<span class="label">A field an EM run cannot use is marked as you look at it</span>
<p>A thickness of zero, a permittivity below 1, a conductivity of zero — anything an EM run would
refuse outright — gets a <b>faint amber outline</b> on the box itself, and hovering it says what is
wrong and what would fix it. It is amber and not red on purpose: an unfilled field is a normal state of
a technology somebody is still writing, not a fault.</p>
<p><b>It marks unusable, never merely unlikely.</b> A value that is present and plausible is never
marked, because nothing in circuitRF knows which board you are looking at — so a mark means the run
will stop, and an unmarked field is not a claim that the number is right.</p>
</div>

The two boundary conditions are properties of the stack as a whole:

- **Top** — `Open` (free space above, the usual case) or `Ground`.
- **Bottom** — `Ground` (the usual case) or `Open`.

<div class="callout note">
<span class="label">A dielectric is never drawn, and it is laterally infinite</span>
<p>A conductor entry has drawing layers; a <strong>dielectric entry has none</strong>. It is a sheet
of material spanning the whole problem at its stated thickness, everywhere, because that is what the
solver's Green's function is built on — a stratified medium, uniform in x and y.</p>
<p>This matters most for a <em>thin</em> dielectric that a real process only leaves under and just
beyond a structure — a capacitor dielectric, say. The model carries it as a full sheet at its true
height. That is the standard trade in this class of tool, and it is a good one where the structure
is; it is <em>not</em> free everywhere else, and <a href="#mim">A thin-film (MIM) capacitor</a> gives
the measured cost.</p>
</div>

<div class="callout note">
<span class="label">A drawing layer is not a conductor</span>
<p>What you draw on is a <em>drawing layer</em> — a GDSII layer/datatype pair with a name and a
colour. What gets simulated is a <em>stackup conductor</em>. The link between them is the conductor
entry's <strong>drawing layers</strong> list, and a shape on a layer that is bound to nothing is
simply not part of the EM problem. If a run tells you it found "nothing on a layer bound to a signal
conductor", that binding is what is missing — not the artwork.</p>
</div>

## The Stackup tab, field by field {#tab}

Below is the shipped **four-layer FR-4** technology — the one a new workspace can be created
against — open in the Technology Editor's Stackup tab. Nine entries: four coppers, three dielectrics
between them, and two via entries. It is worth opening yourself alongside this page; everything on it
is described below.

{{ui: tech-editor-stackup}}

### The top row: what is above and below, and how to add an entry

| Control | What it does |
|---|---|
| **Top** | The boundary condition above the whole sandwich — `Open` (free space, the usual case) or `Ground`. |
| **Bottom** | The boundary condition below it — `Ground` (the usual case) or `Open`. |
| **＋ Dielectric / ＋ Conductor / ＋ Via** | Appends an entry of that kind. A new entry lands at the bottom of the list; the **↑ ↓** buttons on its own row move it to where it belongs. A new dielectric or conductor copies the thickness and material of the last entry of its kind; the first of its kind starts as a generic FR-4 board &mdash; εr 4.4, tanδ 0.02, 1.778 mm &mdash; or 35 µm copper. |

### The summary row: what the stack adds up to, and what it is made of

| Readout | What it means |
|---|---|
| **Stack height** | The sum of every Conductor and Dielectric thickness, in the technology's display unit. **Via entries are excluded** — a via occupies no slice of the stack; it traverses the dielectrics between the conductors it spans. 62.13 mil above is the 62 mil the board is sold as. |
| **conductors / dielectrics / vias** | How many entries of each kind. `conductors: 4` is the layer count a fabricator quotes; `vias: 2` is two *kinds* of connection, not two holes. |
| **board** | Shown only when some other document stated an overall board thickness — a Gerber job file's, today. If it disagrees with the stack height by more than 1%, the row says so and **nothing is corrected**: which of the two numbers is wrong is not something circuitRF can know. |

The filter box below it narrows the list by entry name, and the count beside it is how many of the
nine are showing.

### Every entry: kind, name, order

Each entry is a card. The grey word at its left is its **kind**, fixed when it was added. The box
beside it is its **name**, and the name is not decoration: a via entry names the two conductors it
spans **by name**, and an EM setup names its analysis levels the same way. Renaming an entry carries
every reference to it inside the technology along — a via's span, a dielectric's **Patterned with**,
a 3D body sitting on it — as one undoable edit; an EM setup or a layout part that names it is a separate
file and keeps the old name. The three buttons at the
right are **↑** and **↓** — the list is ordered top to bottom and **that order is z** — and **✕**,
which removes the entry.

### A Conductor entry

| Field | What it is |
|---|---|
| **Thickness** | The metal's thickness, in the display unit. Used by the cross-section kernel and by interchange; the full-wave planar kernel solves a zero-thickness sheet and uses this only to place it — see [What the EM engine does not read](#ignored) — unless the EM setup turns on **Model copper thickness**, which widens the sheet to match this thickness's Z₀ ([EM setup](em-setup.html#solver)). |
| **σ (S/m)** | Conductivity at 20 °C. Sets conductor loss and skin depth in **both** EM kernels — on drawn metal, and on the **ground reference** layer, whose own σ becomes the loss of the infinite plane a microstrip returns through (about a fifth to a quarter of the conductor term; see [the MoM engine](mom-engine.html#ground-loss)). A ground layer left at zero is a perfect plane, and the run's notes say so. The **combo beside it** is a shortcut that fills the box with a metal's bulk value (copper 5.8e7, silver 6.3e7, gold 4.1e7, aluminium 3.77e7, nickel 1.43e7); the box stays typeable and is what gets saved, and a value matching none of them reads as *Custom*. |
| **Rough (µm)** | RMS surface roughness of this copper, in microns. **Empty is smooth**, which is what every technology written before the field existed means. Once the skin depth is comparable to it, the current follows the rough surface and conductor loss rises, up to about double (the Hammerstad–Bekkadal correction). It is applied in **both** EM kernels — to this metal, and to the return plane's face when this is the ground reference — and to **MLIN and MCURVE** through the technology (a `Roughness` you type on the part itself wins). Use the foil's RMS (Rq) figure, not Rz. One value covers both faces of the metal, which overstates the loss when only the substrate side is treated. |
| **Ground reference** | Marks this conductor as the ground plane — **the negative terminal of every port in an EM run**. In the figure it is ticked on `Inner 1` and on `Bottom Copper`; the rule for which one a given run uses is in [the next section](#ground). |
| **Metal thickness goes to** | The full-wave solver has no thickness for metal, so this conductor's thickness is given to the dielectric above it or the one below it. `the layer above` is the default and is what every technology authored before the field existed means. The stack height is the same either way. Full explanation, and the one case that needs the other setting: [Where a conductor's thickness goes](#sheet-surface). |
| **Drawing layer** | Which drawing layers map onto this conductor — a **checkbox list**, because a conductor may carry several, with a filter box above it for a process that has hundreds. A shape on a layer bound to nothing is simply not part of the EM problem. |

### A Dielectric entry

| Field | What it is |
|---|---|
| **Thickness** | The separation this material provides. On the figure, 8 mil of prepreg either side of a 42 mil core. |
| **ε<sub>r</sub>** | Relative permittivity. |
| **tanδ** | Loss tangent. |
| **µ<sub>r</sub>** | Relative permeability — 1 for everything that is not a magnetic material. |
| **σ** | Bulk conductivity, for a [conducting substrate](#conducting-substrate). Read in `S/m` or `Ω·cm`; the file states S/m. `—` means none. |
| **Patterned with** | `(none)` for an ordinary, laterally continuous dielectric, which is what all three on this board are. Naming a conductor here says *this film exists only where that metal's artwork is* — the thin-film capacitor case, described in [The capacitor dielectric rides along only when its plate is analysed](#mim-tied). |

**A dielectric has no drawing layer, and there is no picker for one.** It is not drawn: it is a sheet
of material spanning the whole problem at its stated thickness.

### A Via entry

Via entries are listed **as their own group below the ordered ones**, under a line saying why: a via
is not a layer of the sandwich and has no position in the top-to-bottom order.

| Field | What it is |
|---|---|
| **Spans** | The two conductor entries this via connects, **by name**. The two on the figure differ, and that is the point of a four-layer board: `Plated Through-Hole` runs Top Copper → Bottom Copper, while `Ground Via (L1-L2)` runs Top Copper → Inner 1. |
| **Fill · Plated** | Whether these holes are metal at all. Unticked means a non-plated hole — a mounting hole, a routed cutout — drawn as it is but **not** extracted as a vertical conductor. A 3D problem drills it through its span as **air**, removing the substrate and any meshed copper it crosses. |
| **Fill kind** | Plated or solid. The planar RF solve does not read it, because a plated wall a few µm thick is already many skin depths. A 3D problem does: a plated barrel with a wall is a metal tube with an air core, and a solid one is a metal cylinder. |
| **Wall** | Plated wall thickness — the metal on the barrel wall, **not** the hole radius. 20–25 µm (about 1 mil) is typical and is what the shipped technologies use. |
| **Drawing layer** | The one drawing layer via shapes are drawn on. **One**, not a list: a via binds at most one, so this is a plain picker with an explicit `(none)`. |

<div class="callout note">
<span class="label">The ? in the corner opens this page</span>
<p>The Help button at the top right of the Technology Editor follows the tab you are on: the Stackup
tab opens this chapter, and the Layers, DRC Rules and Interchange tabs open their own sections of
<a href="layout-editor.html#technology">The Layout Editor</a>.</p>
</div>

## Where your port's negative terminal is {#ground}

**Every port in an EM run returns through one plane, and the stackup picks it.** Not the panel, not
the port label, not per port.

The rule, in order:

1. **The ground-designated conductor.** Mark a conductor entry as the **ground reference** in the
   technology editor. The plane is the **top surface of the highest ground-designated conductor that
   lies below the signal level being simulated**.
2. **Failing that, the stack's bottom.** If no conductor is marked and **Bottom = Ground**, the plane
   is taken at the bottom of the stack. The run says so in its notes, and asks you to mark one
   instead — this fallback places the plane by boundary condition rather than by a real conductor's
   surface, which is a different height by the thickness of whatever is down there.
3. **Failing both, the run is refused.** No ground reference and no grounded bottom means no second
   terminal for any port, and there is nothing to solve.

Four consequences worth having in mind:

- **`Bottom = Ground` is a fallback, not an override.** Once step 1 finds a designated conductor, the
  boundary condition is never consulted: it adds no second plane underneath, and it does not make the
  chosen plane perfect. A designated ground carries its own σ as a lossy plane whatever `Bottom` is
  set to, and switching it to `Open` changes nothing except that step 2 is no longer available.
- **The plane is modelled as laterally infinite.** It is the boundary condition the Green's function
  handles analytically, not a meshed pour — so a ground *pour* drawn as artwork is not it, and a
  finite plane's edges are not modelled. A conductor marked as the ground reference is not meshed and
  cannot also be an analysis level; naming one as a level is refused.
- **An intervening metal layer does not become ground just by being underneath.** The rule keys on
  the *designation*, not on stack position, precisely so that an MMIC's second metal level is not
  mistaken for a plane. Mark what you mean.
- **This is the port's negative terminal for every port type** — an edge port at a conductor end, an
  [internal delta gap](mom-engine.html#ports), or an [internal port](mom-engine.html#ports) whose
  whole purpose is to reach it. It is why an internal port needs a path down to the plane at all.

<div class="callout warn">
<span class="label">If the answer looks wrong by a constant factor, check this first</span>
<p>The height between the signal conductor and the ground plane is the single number a microstrip's
impedance depends on most. A ground reference marked one entry too low — on the far side of a
dielectric you forgot was there — changes every impedance in the run while leaving the result looking
completely plausible. The run's own notes name the conductor it used and its height; read them.</p>
</div>

## Which conductors get simulated {#levels}

A conductor entry that is **not** the ground reference is a *signal* conductor, and a signal
conductor with artwork on it is an **analysis level**.

- With one signal conductor, that is the level. Nothing to choose.
- With more than one, the EM setup can name the levels it wants (the analysis-level list); with none
  named, every signal conductor that actually carries artwork is included.
- Levels are ordered **bottom to top**, and the lowest one sits on the slab's top surface.

## Where a conductor's thickness goes {#sheet-surface}

**The full-wave solver has no thickness for metal at all.** It models a conductor as a
**zero-thickness sheet at one height** — so the slice of the stack that conductor occupies has to be
filled with *something*, and the stackup does not say what fills a metal layer where no metal is
drawn. The answer is that the metal's thickness is given to the dielectric on one side of it, and
which side is a per-conductor setting: **Metal thickness goes to** on the conductor's row in the
Stackup tab.

| Setting | The metal's thickness is added to | The sheet ends up at | Height of a line on it |
|---|---|---|---|
| **the layer above** (default) | the dielectric **above** the conductor | the top surface of the dielectric **below** | the substrate under it, exactly as entered |
| **the substrate below** | the dielectric **below** the conductor | the bottom surface of the dielectric **above** | the substrate **plus this conductor's own thickness** |

**Neither setting moves anything, and the stack height is identical either way** — the total is the
same number in both cases. All that changes is which neighbour absorbs the vanished thickness, and
therefore how high above the ground plane the sheet lands.

**"The layer above" is what you want almost everywhere**, and it is what a conductor that says
nothing means: a trace deposited on a substrate and encapsulated by whatever comes next, whose height
above the ground plane comes out as the substrate thickness. It is also the setting the
**closed-form** microstrip models agree with — they model real metal of real thickness and measure
their height to the metal's *underside*.

**"The substrate below" is for the lower plate of a capacitor.** With the thickness given to the
layer above, that plate's whole metal thickness lands inside the plate gap, and the solver separates
the two sheets by the capacitor dielectric *plus that metal* — 3.2 µm rather than 0.2 on a typical
MMIC metal. Giving it to the substrate below instead puts the gap back to the dielectric alone. The
shipped MMIC technology does exactly this on `Metal1`.

**And it applies only where the capacitor does.** A run that is not analysing the plate has no plate
gap to get right, so a conductor directly under a [patterned dielectric](#mim-tied) is put back to
the default for that run — the same run note that reports the dielectric reports this. That is what
lets one technology set it at all: an ordinary line on `Metal1` still solves against 100 µm of GaAs,
exactly as it did before the capacitor module existed.

<div class="callout note">
<span class="label">This is not "expand up / expand down / symmetric"</span>
<p>Tools whose stackup gives you an <em>interface</em> and a metal thickness have to decide which way
the copper grows from it — up, down, or half each way. <b>circuitRF never asks that</b>, because its
stackup is an explicit ordered list: the conductor is its own row with its own thickness, already
sitting between two named dielectric rows. Its extent is fully determined before any EM question is
asked, and nothing is ever displaced.</p>
<p>This setting answers a <em>later</em> question, and one that exists only for the full-wave planar
kernel: given that the metal already occupies a known slice, and the solver will represent it as a
sheet at a single height, which face is that height and which neighbour fills the gap. <b>There is
deliberately no "symmetric".</b> Half to each side is representable, but no structure wants it: it
would place every ordinary line permanently half a metal thickness away from the closed-form models
circuitRF validates itself against, and for the ground-referenced case that matters the field is in
the substrate under the strip — the underside is the right face there, not a crude default that a
midpoint would refine.</p>
</div>

<div class="callout note">
<span class="label">The cross-section kernel ignores this entirely</span>
<p>It models real metal of real thickness and has no sheet to place, so the setting is read by the
<b>full-wave planar</b> path only. It also never applies to the <b>ground plane</b>, whose boundary
is always taken at its top surface whatever its own row says.</p>
</div>

The trade is stated rather than hidden: in a *capacitor* run a `Metal1` microstrip's EM substrate is
103 µm of GaAs rather than 100 — a ~3% height shift, against a 16× error in the modelled plate
separation. The **closed-form** microstrip models are unaffected either way; they model real metal of
real thickness and measure their height to the metal's underside, so in such a run they and the EM
result differ by up to one metal thickness. The run's notes name each level's z *and* the surface it
sits on, which is where you read back what was actually solved.

## The slab, and what makes one solvable {#slab}

The solver works on the **dielectric between the ground plane and the lowest analysis level**. Two
stackup mistakes are refused by name rather than solved around:

- **The signal conductor sits at or below the ground plane.** There is no slab between them. Almost
  always a stackup written in the wrong order — the list runs top to bottom.
- **There is no dielectric entry between the plane and the conductor.** A conductor floating directly
  on a plane with nothing in between has no substrate to be a microstrip over.

**Several dielectric entries between the two are carried as several layers**, each at its own
thickness, ε<sub>r</sub> and tanδ — the medium is stratified and the solver solves it that way. (It
used to refuse a stratified region under the feed and tell you to merge the entries into one; merging
them changes the physics, and the reason for it is gone.) The run's notes name the layers it found and
print the single ε<sub>r</sub> it uses to *size* the calibration standards and the mesh — the
series-capacitance equivalent of the stack. That number is a mesh-sizing average and never the
reference impedance the answer is published against.

Layers *above* the top conductor are not part of that slab: the top boundary is what is above, and it
is `Open` unless you say otherwise.

## Vias {#vias}

A via entry is not a layer — it is a **connection between two conductor entries, named by name** in
its `Span from` / `Span to` fields. Draw on the drawing layer the entry is bound to, and the EM
extractor turns what you drew into a real vertical current path.

**Two kinds of artwork count**, and both go through the same rules below:

- a **via primitive**, the pad-and-drill point you place with the Via tool;
- a **filled region** — a rectangle or a polygon, holes included. This is what a thin-film
  capacitor's plate connection is: a patch nearly as large as the plate itself, rather than a point.
  Several regions on one via entry are several footprints of the same connection.

A **path** on a via layer is ignored, with a note — a path is a centreline and encloses no area, so
there is no footprint to mesh. Draw the region instead.

Three rules the extractor applies, each reported when it bites:

- **The two conductors must be adjacent in the stack.** A via that skips a level is not built, because
  there is nothing to connect it to in between.
- **A via may terminate on the ground plane**, and that is the common backside/through-hole case: the
  named conductor is the ground-designated one, so the via runs from a signal level down to the plane.
  A via naming some *other* non-analysis conductor is ignored with a note — it would otherwise silently
  model a structure you did not draw.
- **A via primitive's footprint is squared.** A round barrel staircased onto the mesh grid would cost
  a gridline per facet for no physics, so it is replaced by the **equal-area square**, which preserves
  the conducting cross-section. The run reports that it did. **A drawn region is not squared** — the
  substitution exists so a circle nobody drew does not staircase, and an outline you drew already is
  the footprint, so it is meshed as it stands.

<div class="callout note">
<span class="label">Where a via sits on the cross-section means nothing</span>
<p>You can slide a via sideways on the drawing, and it is saved with the technology — but it is a
<strong>drawing position and nothing else</strong>, there so a stack with several via entries can be
laid out tidily rather than overlapping. A via entry is a <em>kind</em> of connection between two
named conductors, not one hole at one place: every via you draw on its drawing layer is an instance
of it, and where they are is in the layout. Nothing in the extraction, the solver or any export reads
the drawn position.</p>
</div>

<div class="callout note">
<span class="label">An internal port does not need you to draw one</span>
<p>An <a href="mom-engine.html#ports">internal port</a> is placed on the metal and returns to the
ground plane, so it needs a path down there. If you drew a via, it drives yours. If you did not, the
solver builds one — a square of <strong>the technology's default via drill</strong> (or, if the
technology declares none, a quarter of the substrate height), reported by size in the run's notes.
That path is real metal and its inductance is in the answer, which is why you can override it simply
by drawing the via you want.</p>
</div>

## A thin-film (MIM) capacitor {#mim}

A MIM capacitor is a thin dielectric between two metal plates inside the interlayer dielectric. In a
stackup it is **three entries**:

| Entry | Kind | What it is |
|---|---|---|
| `MIM Metal` | Conductor | The top plate — a thin metal with its own drawing layer |
| `MIM Dielectric` | Dielectric | The capacitor dielectric: thin, higher ε<sub>r</sub>, **no drawing layer** |
| `MIM Via` | Via | The plate's connection up to the routing metal above it |

The bottom plate is the interconnect metal underneath — no fourth entry. You draw the two plates and
the plate connection; the dielectric is never drawn. **A `Cap Dielectric` or `Nitride` layer in a
layer table is mask documentation, not this.**

Both capacitor forms are ordinary multi-level artwork:

- **Shunt** — bottom plate on the lower metal over one or more backside vias, top plate on the plate
  metal, feed landing on the upper metal through a plate-via region.
- **Series** — feed in on the lower metal, which *is* the bottom plate, and out on the upper metal
  through the plate via.

{{ui: stackup-mim}}

### The capacitor dielectric rides along only when its plate is analysed {#mim-tied}

There is **one** MMIC technology, and it carries the capacitor module. That is only possible because
the capacitor dielectric is **tied to its plate** — the `Patterned with` setting on its row names
`MIM Metal`, and it means *this film exists where that metal's artwork is, and nowhere else*, which
is what a thin film physically is: deposited under the plate and etched away everywhere around it.

**So an EM run carries the film only if the plate is one of its analysis levels.** Draw a capacitor
and the plate has artwork on it, so the film is there and the plates are 0.2 µm apart. Draw plain
interconnect and it is not, so the film enters the medium as **air at the same thickness** — nothing
above it moves — and the lower metal's thickness goes back to the layer above. That run is then
identical, number for number, to the same run on a stackup that never had a capacitor module in it.
**No setting to remember, and nothing to switch.**

**It is always reported.** A run whose tie deactivated says so in its notes, naming the dielectric,
the plate that would switch it back on, and the thickness setting it reverted. A medium you did not
author and cannot see is exactly the kind of change that produces a complete, believable answer to a
question nobody asked.

Why it has to work this way: a stackup dielectric is [laterally infinite](#anatomy), so a film that
was simply *present* would be present in every run. That is not free, and both costs are large enough
to see:

- **Airbridge posts would stop solving.** A post between the two metals would cross a dielectric
  interface, and a via that crosses one is refused by name — the closed-form integral along a via is
  written in one region's coefficients. **That refuses the whole run**, it does not drop a shape.
- **A line on the lower metal would move.** The 0.2 µm ε<sub>r</sub> 6.8 sheet would sit on it as
  superstrate: measured on the acceptance line, ε<sub>eff</sub> rises ~1.7% and Z₀ falls ~2.8%.

Both are correct physics *for a run that has a capacitor in it*, and wrong for one that does not. The
tie is what tells the two apart. (circuitRF shipped two MMIC technologies for exactly this reason
until the tie existed; if you have a workspace holding its own copy of the older pair, they still
work — nothing in a workspace is rewritten by a new release.)

**What is still bounded:** with the plate level in the analysis, a post between the two interconnect
metals skips a level, and a via must span two conductors that are adjacent *in the analysis*. It is
dropped with a note. Capacitor plates and airbridge posts in one EM setup remain separate runs — but
they are now separate runs on one technology, not two.

### Adding a capacitor module to an imported technology {#mim-import}

A technology imported from a process description often arrives **without** its capacitor module. A
process stack description states the interconnect — the metals, the insulation between them, the vias
that join them — and treats an optional thin-film module as exactly that: optional, and frequently
left out, while the layer table shipped beside it still lists the module's drawing layers. The import
is faithful to what it was handed, so what you get is a valid technology whose plate layers draw
perfectly and connect to nothing.

**The import report says so, by name.** Two of its notes are about this and only this:

- *"No via in the file names these conductors…"* — a conductor the stack describes that no via entry
  reaches. It cannot be connected, and the file never said how it should be.
- *"The layer table defines N drawing layer(s) no stackup entry is bound to…"* — layers you can draw
  on that the stack does not model. A shape on one is artwork, not structure. A real layer table has
  plenty of these legitimately, so the note counts them and names the first dozen.

Neither is an error, and neither is a guess: nothing in the file states the missing piece, so
circuitRF names the gap rather than inventing an entry to fill it.

**The fix is three rows** in the Technology Editor's Stackup tab, in stack order, and it takes about
two minutes. Numbers below are silicon-nitride-class examples — take yours from the process:

| Row | Kind | What to set |
|---|---|---|
| The capacitor dielectric | Dielectric | Thickness *d* (50–300 nm; 0.2 µm here) and ε<sub>r</sub>. **No drawing layer.** |
| The top plate | Conductor | Its thickness (0.25 µm here) and its **drawing layer** — the plate layer the import already brought in |
| The plate connection | Via | **Span from** the plate **to** the metal above it, and its own **drawing layer** — the plate-via layer the import already brought in |

Insert them between the two interconnect metals, in that order, top plate above dielectric. The bottom
plate is the interconnect metal underneath — there is no fourth row, and nothing is drawn for the
dielectric.

**ε<sub>r</sub> comes from the capacitance density the process quotes**, which is the number a process
actually publishes:

<p class="center">ε<sub>r</sub> = C″·<em>d</em> / ε₀</p>

At C″ = 0.30 fF/µm² over *d* = 0.2 µm that is 6.8 — the value the shipped MMIC technology carries. Work
it out from your own two numbers rather than copying a permittivity out of a materials table: the
capacitance is what you want the model to reproduce, and *d* and ε<sub>r</sub> only ever appear
together in it.

**Then two settings on the rows around it**, and neither is optional:

- **Tie the dielectric to the plate** — `Patterned with` on the dielectric's own row, set to the top
  plate you just added. Without it the film is a continuous layer of the medium, present in every run
  on this technology, and the two costs in [the tie's own section](#mim-tied) land on your
  interconnect: airbridge posts between those metals refuse the whole run, and a line on either metal
  moves in ε<sub>eff</sub> and Z₀.
- **Give the lower metal's thickness to the substrate below** — see
  [Where a conductor's thickness goes](#sheet-surface). A conductor is solved as a
  zero-thickness sheet, and by default its thickness is given to the layer *above* it, so the
  modelled plate separation would be your dielectric **plus the lower plate's whole metal
  thickness** — several times the gap you just entered, with a plausible capacitance to show for it.
  The tie reverts this setting for you in any run that is not analysing the plate, which is why it is
  safe to set at all.

**Add the rows to the technology your designs already use** — there is no copy to make and no
retargeting to do. With the tie set, a run that draws no plate artwork extracts exactly as it did
before you added the rows: the film enters the medium as air at the same thickness and the sheet
surface goes back, and the run's notes say so.

**What activation does change**, stated so it is not a surprise the first time a capacitor run
disagrees with an interconnect one: in a run that *does* analyse the plate, the film is real, and the
lower metal's line sees it. Its thickness is given to the substrate instead, so that substrate grows by that
metal's own thickness, and ε<sub>eff</sub>/Z₀ move with it. That is the capacitor run's real physics,
not an artifact — it is the same structure a capacitor sits in.

Two things to know before you read a number off the result: within a run the dielectric is
[laterally infinite](#anatomy) — present everywhere at its stated thickness, which is a good
approximation because the fields that set the capacitance are confined under the plates — and the
mesh has to resolve the gap, which is the subject of the next section.

### Reading a capacitance off a MIM run {#mim-accuracy}

<div class="callout warn">
<span class="label">Never read a small element off a RAW solve, and mesh the gap</span>
<p>The solver models the plate separation the process states — 0.2 µm on the shipped MMIC technology,
the capacitor dielectric and nothing else. (It used to model 3.2 µm: a conductor is solved as a
zero-thickness sheet, and with that thickness given to the layer above it the lower plate's whole
metal thickness fell inside the gap. The lower plate's entry now gives its thickness to the
<strong>substrate below</strong> instead — see <a href="#sheet-surface">Where a conductor's
thickness goes</a>.) The run's notes print every level's z <em>and</em> the surface it sits on, so you can
always read back the separation it used.</p>
<p><strong>A port on upper metal now de-embeds.</strong> It used to be refused: the reference
impedance a de-embedded answer is published against is Z<sub>c</sub> = γ/(jωC<sub>pul</sub>), and
C<sub>pul</sub> came from an electrostatic image series over one grounded slab — the right problem for
a trace on the substrate's own top surface and the wrong one for metal buried in the interlayer
dielectric. The solver now solves that electrostatics at the port level's own height in the real
stack, so an ordinary two-port MIM network de-embeds like anything else. The same change lets a
technology carry several dielectrics <em>under</em> the lowest analysis level, which used to be
refused with "merge the layers".</p>
<p><strong>Read the capacitance from the de-embedded answer, never from a raw one.</strong> A raw
solve's s-parameters include each port's own discontinuity, and that discontinuity is a
fraction-of-a-femtofarad series element: read a small capacitance through it and you read the port,
not the capacitor, whatever the plates do. That is not special to a capacitor — a matched 50 Ω GaAs
microstrip reads |S<sub>21</sub>| = 0.07 raw at 10 GHz. (An earlier revision of this page said the
plate capacitance was "not modelled", on a measurement taken through exactly that raw path —
retracted.)</p>
<p><strong>And mesh the gap.</strong> The one thing that genuinely limits a plate capacitance is the
mesh: the cross-level part of the fill degrades as the cell size grows against the plate separation,
and the extracted capacitance follows it — within 10% of ε₀ε<sub>r</sub>A/d while the ratio is at
most 5, 1.46× at 12.5, and the wrong sign at 25. <strong>The shipped MMIC technology's default mesh
sits outside that</strong> (a 10 µm plate pair 0.2 µm apart meshes at 2.5 µm, i.e. 12.5), and the run
says so in its notes — it is a note rather than a refusal because the rest of the structure is
unaffected. Refine the mesh over the plates until the note stops firing before you trust the
value.</p>
</div>

**The shunt form's backside via sets an upper frequency.** A vertical basis carries uniform current
along its whole run, so a via is refused above k·ℓ = 0.3. Through 100 µm of GaAs that ceiling is just
under 40 GHz; the refusal says so and names the number it computed. The series form has no such via
and no such bound.

## A resistive film on a level {#film}

A thin-film resistor's film belongs to a metal level without being that level's metal. It is defined on the conductor it is
deposited on: open that conductor's card, and under **Resistive films** press **Add**, or right-click the conductor in the
drawing. **Add Resistor Film** makes an existing drawing layer a film and asks for its sheet resistance. **New Resistor Film…**
asks for a name as well and creates the drawing layer with it. On the Layers tab a film's layer is an ordinary row: name,
colour, pattern and order.

**A film is contacted in one of two ways, and the level it is on says which.**

- **Overlap contact**, common on GaAs. The film is on a level that has metal. It lies on the substrate or a passivation layer, on the
  same surface the metal is deposited on, and the metal laps over its ends. The contact is the metal's underside on the film's top
  surface, across the overlap, and metal drawn over the film is a contact wherever it lies.
- **Via contact**, common in a silicon back-end. The film has a **film level** of its own in the dielectric, and dedicated vias join
  it to a metal level. Metal can cross such a film without touching it, and the via openings set the resistor's length.

Contact resistance is not modelled in either, so a contact is an ideal short. Where it matters, as on a low-value resistor, add it
in the schematic.

### A film level {#film-level}

**＋ Film Level** adds a Conductor entry with no metal, and asks for its film: an unused drawing layer or a new one, a sheet
resistance, and which entry it goes below. In the `.ctech` it is a `Conductor` whose `DrawingLayers` is empty and whose `Films` is not.
Its card shows its name, thickness and films, since nothing else on a conductor's card means anything without metal, and the drawing
fills its band with its film. Checking a drawing layer on the card gives it metal, and it is an ordinary metal level from then on,
whose film is overlap-contact.

A film level needs:

- **A via.** Add a via entry whose span ends on the level. The other end is the metal a `TFR` draws its heads on. With no via a film
  there can only ever be an open, and `check` warns.
- **A dielectric each side.** A conductor directly above or below is an error, because nothing separates them.

Its thickness is the entry's, so a film on one states no thickness and no face: a stated one is unused, and `check` warns. A
drawn film's level joins a planar run on its own, and the run says so. Naming a list of levels in `AnalysisLevelNames` that leaves
it out is a refusal, because a film left out of a run reads as an open circuit.

A via-contacted film reads long at DC, by up to half a via cell at each end, because the current reaches a via at its cells' centres.
A 4.5-square test film at the shipped mesh is 3.7 % high, falling to 1.6 % at 32 cells across.

**In 3D the three solvers differ.** On a zig-zag film 250 nm under a 3 µm metal, over 100 µm of GaAs:

- **Palace** takes it as it stands. `Sheets` and `Solids` agree to 0.002 in |S| through 40 GHz, in a mesh 12 % larger than the same resistor as overlap contact.
- **EMerge** solves a film as a volume at least 2 µm thick, on a film of this sheet resistance. It puts that volume below the level when metal lies over the film,
  and refuses, naming both metals, when metal lies on both sides closer than that. Below a film the Standard preset read half the reactance, so use
  **Accurate**, which took 18 s here. The run's note says so.
- **openEMS** needs a cell the size of the thinnest gap. A 250 nm cell over this structure is estimated at 4.2 hours.

{{ui: stackup-si-tfr}}

The shipped `rfic-Si_3LM_TFR_100um` technology is an example of this style, with generic round numbers taken from no process. It holds
two films on one level, 50 and 500 Ω/sq, between Metal1 and Metal2. Its silicon is the library's `Silicon, 10 kΩ·cm`, which states a
conductivity, so its loss falls with frequency. See [A conducting substrate](#conducting-substrate).

Each film states:

| | |
|---|---|
| **Sheet resistance** | Ω/sq, positive. What a `TFR` on it is worth per square. |
| **Thickness** | On a level with metal. Read by a 3D solve only, whose sheet takes σ = 1/(R_s·t). Left empty, the generator picks one well under a skin depth and says which. |
| **Face** | On a level with metal. Which face of the conductor the film sits on when the metal is a solid in a 3D solve. An overlap-contact film is normally on the bottom, the surface the metal is deposited on. Unstated, it follows the level's own sheet. |

The drawing shows a film as a strip on its conductor, against its face, in its layer's colour. On a film level the strip fills the band.

| Gesture | Means |
|---|---|
| Click a film | Select it. Its conductor's card scrolls into view with the film's row shaded. |
| Double-click its sheet resistance or thickness | Edit in place. |
| Drag it to the other face of its conductor | Set its face. On a film level this writes nothing. |
| Drag it onto another conductor | Move it. A move onto a level with metal makes it overlap-contact. |
| Right-click it | **Face**, **Move to**, **Remove Film**. Removing a film leaves its drawing layer. A film level has no **Face**. |

A film may sit on one conductor. A via, a dielectric and a ground-reference conductor refuse a drop and say why, and a layer
that is a level's metal cannot also pattern a film.

**On a level with metal, where a film and metal overlap, the cell is metal.** The current in an overlap runs in the metal, so a resistor's
length is the distance between its contacts' edges rather than the length of the film you drew.

**On a level with metal, the film and the metal share one sheet, at one height.** A planar solve meshes a conductor as a zero-thickness sheet
at one z, and the film is a surface impedance of `R_s` on the part of that sheet it covers. A film's height above its
metal's bottom is not a quantity this model has, so a 50 nm film on the bottom face of a 1 µm metal solves the
same as one at any other height. A 3D setup can see the difference: with `Conductors: Solids` the metal keeps its
thickness and the film sits on the face it states.

**The conductor-thickness correction does not run on a level carrying a film.** It grows a level's metal to stand in for its
thickness, and that metal along a film's edges would short the resistor. The run says so, and that level's metal lines read
their Z0 as a sheet of the drawn width does.

**A resistor drawn on a layer that is not a film is an open.** It reaches the solver as two contacts with nothing between
them, and a run that meets one names the layer. A film stays out of connectivity, so LVS and DRC read the resistor as a device
rather than a short.

The technology **MMIC GaAs (2 Layer Metal + MIM, 100um)** states a film: `Resistor` on the bottom face of Metal1, 50 Ω/sq, with width and spacing rules
on its layer. The **PDK PCells** example draws a resistor on it.

## A conducting substrate {#conducting-substrate}

Silicon's loss is conduction: tanδ = σ/(ωε₀ε<sub>r</sub>), which falls as 1/f. Over a 1 to 40 GHz sweep, any single
tanδ is wrong by up to a factor of 40 at one end. State **σ** on the dielectric, or choose a material that states it, and each
frequency is solved with its own value. A tanδ beside it is a separate mechanism and adds to it, and `check` and the editor warn about that, because a tanδ worked out from the same conductivity would count the loss twice.

| Resistivity | σ | tanδ at 1 GHz | 5 GHz | 40 GHz | Field decay depth at 5 GHz |
|---|---|---|---|---|---|
| 10 kΩ·cm | 0.01 S/m | 0.015 | 0.0030 | 0.00038 | 1.8 m |
| 1 kΩ·cm | 0.1 S/m | 0.15 | 0.030 | 0.0038 | 18 cm |
| 10 Ω·cm | 10 S/m | 15 | 3.0 | 0.38 | 2.7 mm |
| 0.01 Ω·cm | 10⁴ S/m | 15,000 | 3,000 | 380 | 71 µm |

**When to state σ rather than tanδ.** State σ for any semiconductor substrate swept over more than a narrow band. A constant tanδ
is right for a laminate or an oxide, whose loss really is close to constant.

The substrate falls into one of three regimes, and the run's notes say which:

- **High resistivity: a lossy dielectric.** The wave travels in the silicon. Above about 150 MHz at 1 kΩ·cm, conduction current is smaller
  than displacement current.
- **Moderate, about 10 Ω·cm: the silicon conducts and the oxide does not.** Below about 15 GHz at 10 Ω·cm, most of the voltage
  appears across the oxide, so a line over oxide has a high capacitance and slows down. Its Z<sub>c</sub> is well below its value at the top
  of the band, and is not close to real: 27 + j13 Ω at 5 GHz on a line that is 50 Ω on high-resistivity silicon.
- **Low, about 0.01 Ω·cm: a lossy ground plane.** The skin depth reaches the substrate's thickness, so return current moves from the backside
  metal into the silicon as frequency rises.

### What each solver does with it {#conducting-substrate-solvers}

- **The full-wave planar kernel** solves the first two regimes. Against a Palace reference, a 1 mm line over 2 µm of oxide on 100 µm of
  silicon reads within 0.025 in |S| from 5 to 40 GHz at 10 kΩ·cm, 1 kΩ·cm and 10 Ω·cm. In the third regime **it warns and names the range**: where the skin
  depth is under twice the layer's thickness, up to the frequency where the de-embedding's γ stops being quasi-static, loss reads far too low and phase
  too high, and the S-parameters there are not reliable. At 0.01 Ω·cm on that line, the range is 633 MHz to 27 GHz. Above it the
  same line reads within 0.011.
- **Palace** takes σ directly. Its wave ports are not reliable on conducting silicon at the low end of a sweep: at 10 Ω·cm a wave port took a wrong
  mode at 1 GHz, and a uniform line read S11 of −0.8 dB at 5 GHz. Use lumped ports there. Mesh the skin depth yourself with a mesh region, because
  the automatic sizing follows the wavelength and not the skin depth; at 0.01 Ω·cm a 15 µm region moved α by about 10 %.
- **EMerge** takes σ up to 10⁴ S/m and refuses a dielectric above that, because it would treat it as a conductor. At exactly 10⁴ S/m, on lumped ports at Standard, it read within 0.053 of Palace on a 0.5 mm line.
- **openEMS** writes σ as it is, and a constant conductivity is what FDTD reproduces exactly at every frequency.
- **The 2D quasi-static kernel** has one solve for the whole sweep, so it refuses a dielectric with σ. **Automatic** chooses the full-wave planar kernel instead.

**Line models and the impedance tools.** `MLIN` and the other closed forms assume a low-loss substrate. Above 0.001 S/m the part, the line
calculator and the impedance reports say the closed form does not apply, and point at an EM run.

**Not modelled.** σ is one fixed bulk number: no junction, depletion or carrier model, and no temperature dependence. A via that passes through the
silicon makes no contact with it. A doped layer with a profile can be approximated by a stack of uniform layers.

## What the EM engine does not read {#ignored}

Stated plainly, because a field that is carried but unused is worse than one that is absent:

- **Conductor thickness reaches the full-wave kernel's loss model but not its GEOMETRY** — it still
  solves a zero-thickness sheet, so t sets the sheet's surface impedance (and therefore its loss) and
  nothing about where the metal is. The thickness is also used by the cross-section kernel and by
  interchange, and the full-wave kernel gives it to one of the two dielectrics beside the conductor.
  Which one is normally invisible (3 µm of metal under a 100 µm substrate moves nothing), and matters
  exactly once: between the two plates of a [thin-film capacitor](#mim). It is a per-conductor
  setting — see [Where a conductor's thickness goes](#sheet-surface).
- **A via's fill and wall thickness are carried for thermal work**, not read by the RF solve: a plated
  wall a few µm thick is many skin depths at RF, so plated and solid behave the same.

## Checking what it resolved to {#check}

**Every EM run says which conductor it used as ground, and at what height**, in its notes — along with
the level list, the slab it built, and any via it ignored or built for you. That report is the
authoritative answer to "where is my port's negative terminal", and it is written by the code that
actually did it rather than re-derived for display.

If you are chasing a result that looks plausible but wrong, read those notes before anything else:
almost every stackup mistake produces a complete, believable answer for a structure you did not draw.

**See also:** [The MoM engine](mom-engine.html) for the ports themselves, [EM Setup](em-setup.html)
for the panel that runs them, and [The Layout Editor](layout-editor.html#technology) for the rest of
the technology file.

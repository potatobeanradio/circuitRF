# Brief 49 — simulate from the document: setups, ports, the air box and boundaries

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d49-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §4.1 (ports, boundaries by name), §4.4 (reference
planes), §6.4 (naming); briefs 21 (progress), 22 (terminals), 23 (wave ports), 29 (fields)
**Area:** `src/Engine/Em3d/Em3dProblem.cs` (face boundaries), `src/Design/Em3d/{GmshGeoWriter,
PalaceConfigWriter,CsxcadWriter}.cs`, `src/Design/ThreeD/` (port records, polarity inference, problem
assembly), `src/Ui/ThreeD/Simulate/` (Setups panel, port tool), the existing `.cem` panel
(`EmSetupEditorView`) reused
**Depends on:** 42, 47 · **Blocks:** 50, 52
**Owner decision D5:** setups are embedded in the `.c3d` and reachable from a `.cem`.

---

## 0. What this brief delivers

**Author, edit and simulate from one window** — the owner's goal:

- a **Setups panel** in the 3D editor: the document's embedded setups, add, duplicate, rename, remove, the
  **active** one, each edited in the **same panel a `.cem` uses**;
- a **port tool**: draw a lumped port between two conductors, or a wave port on an air-box face. Its
  polarity is **inferred from what it touches** and drawn as an arrow, and one click flips it;
- **the air box, drawn**, with each face's boundary colour-coded. In Face mode its faces are pickable:
  *Boundary ▸ Absorbing / PEC / PMC / Symmetry*, and a typed padding;
- **face boundaries on solids**: *Boundary ▸ Perfect Conductor / Conductive Surface / None* on a face of a
  dielectric or air solid;
- **Simulate** (*Simulate ▸ Run*, and a Run button on the Setups panel) runs the active setup, with brief
  21's stage progress;
- **results and fields over the drawn model**: the Data Display for S, and brief 29's field view on the
  run's geometry.

---

## 1. `R-em3d49-1` — the Setups panel

**`R-em3d49-1a`** A panel docked beside the 3D view. It lists `Setups` (brief 42 §5a) by name, marks the
active one (editor state, per user), and offers *Add*, *Duplicate*, *Rename* and *Remove*. Selecting a
setup shows **`EmSetupEditorView`, bound to that embedded `EmSetup`** — the same view model a `.cem` uses,
with:
- the geometry-reference row hidden (the document *is* the geometry);
- the planar analysis choices hidden. A setup must name a 3D solver (brief 42 §5a).

**One editor, two containers.** Any field a `.cem` gains later appears here with no change.

**`R-em3d49-1b`** Edits to a setup are **document edits**: undoable, dirty-marking, saved with the `.c3d`.
The panel is the setup's only editor inside the document window. There is no second form.

**`R-em3d49-1c` A `.cem` naming a `.c3d`** keeps its own panel. *Show 3D* there opens the **editor** on the
`.c3d`, with that `.cem` shown as an external setup, read-only in the Setups panel, marked with its path.
The user sees the ports and boundaries it will use.

## 2. `R-em3d49-2` — ports

**`R-em3d49-2a` The record** (`Ports` in the `.c3d`, reserved by brief 41):

```jsonc
{ "Number": 1, "Name": "P1", "Kind": "Lumped",
  "Plane": "XZ", "Offset": 0,
  "Rect": { "Min": [u, v], "Size": [du, dv] },          // DBU, axis-aligned, on a drawing plane
  "Z0": "50",                                              // a complex allowed, as the .cem's
  "Positive": null, "Negative": null,                      // null = inferred (§2b); else object names
  "Flip": false }
```

A **wave** port has the same record with `Kind: "Wave"`. Its rectangle must lie on an air-box face of
**the setup being run**, checked at assembly, because the air box is per setup. Its `VoltagePath` is
inferred as brief 23 infers it, and can be stated.

**Ports belong to the document, not to a setup.** Every setup uses all of them. A setup that wants fewer
ports is a later brief; say so in the reference page. This matches a `.cem`, whose ports come from its
layout, not from the setup.

**`R-em3d49-2b` Polarity by contact.** For a lumped port, the elaborated conductors (including those inside
instances, named `U1/…`) that **touch** each of the rectangle's four edges are found by a geometric query
on the elaborated problem, touching to within 1 DBU. Then:
- exactly one pair of **opposite** edges must each touch **exactly one** conductor (or a PEC air-box face).
  That pair's axis is the port's `Direction`;
- the **negative** object is:
  1. the one in the setup's ground set (`Ground3D`, or a ground-reference conductor from a layout
     instance's stackup);
  2. otherwise, the one with the larger elaborated surface area;
- the positive object is the other one;
- `Flip` swaps them.

**Refusals name what was found.** Zero pairs: *"P1 touches `trace` on its left edge and nothing on its
right"*. Two pairs: *"P1 touches conductors on all four edges; state Positive and Negative"*. Two
conductors on one edge: the edge and both names. Stating `Positive` and `Negative` overrides inference
entirely.

**`R-em3d49-2c` The polarity is drawn.** A port draws as its sheet with its number, and an **arrow from
negative to positive**. A wrong polarity is a 180° error in every transmission term. It must be visible
before the run, not discovered in the Data Display.

**`R-em3d49-2d` The port tool** (*3D ▸ Draw ▸ Port*, and letter **P** in the Shift+A popup). It is
drawn like a sheet (brief 45: two snapped clicks on the drawing plane). The next free number is assigned
and Z0 comes from the last port. Inference runs **as the rectangle is drawn**, and the rubber band shows
the arrow or the refusal's words live. A port can also be made from a Face-mode selection: *Make Port…* on
an axis-aligned face takes that face's rectangle, which is the natural way to put a wave port on the end
of a line that reaches the box.

**`R-em3d49-2e` Sub-cell ports are never used** (overview §1k). A port may **name** a conductor inside an
instance (`U1/pad3`). That is the parent choosing where its signal enters.

## 3. `R-em3d49-3` — the air box, drawn and edited

**`R-em3d49-3a`** The active setup's air box (brief 42 §5c) is drawn as a wireframe box, faces tinted by
kind:
- **absorbing**: none;
- **PEC**: metal grey;
- **PMC**: a distinct hue;
- **symmetry**: hatched.

A toolbar toggle shows or hides it, and it starts shown when the setup has any non-absorbing face.

**`R-em3d49-3b`** In Face mode, with the box shown, its six faces are pickable, at the **lowest priority**,
so a solid face in front always wins and **B** reaches the box face behind. Their context menu:
- *Boundary ▸* the four kinds;
- *Padding…*, typed in the display unit.

Both write the **active setup's** `AirBox` entry for that face. The status bar says *"writes setup
`S1`"*, because this edits the setup, not the geometry.

## 4. `R-em3d49-4` — boundaries on the faces of solids

**`R-em3d49-4a` The record** (`FaceBoundaries`, reserved by brief 41):
`{ "Object": "block", "Face": "zmax", "Kind": "Pec" }` or
`{ …, "Kind": "Conductive", "Material": "Gold" }`.

Boundaries attach by **face name** (overview §1e) and follow folds (brief 47 §3c). They belong to the
document, as ports do.

**`R-em3d49-4b` Where they are allowed:**
- on faces of **dielectric and air** solids only. A conductor is already a void bounded by its metal
  (series 1), so a boundary on it has nothing to add, and it is refused naming the object;
- **absorbing, PMC and symmetry stay air-box-only**, because both backends support them only on the
  domain boundary. Refused elsewhere, with that reason.

**`R-em3d49-4c` The neutral problem** gains `Em3dFaceBoundary(Object, Face, Kind, Material?)`, carried by
the face names already in `Em3dPolyhedron` and in the fixed names of the other primitives. How each
backend lowers it:
- **Palace:** a `PEC` or `Conductivity` boundary on the face's surfaces, recovered by the **face
  polygon's bounding box, counted against expectation** (§6.2's rule). A box that catches a neighbour's
  coplanar surface is a count mismatch, refused, never a guess;
- **openEMS:** a zero-thickness sheet of PEC or of the conducting material, coincident with the face, at a
  priority above the solid;
- **FDTD grid:** the face is a sheet, so it gets its lines.

Existing goldens are unchanged when no boundary is present (overview §1h).

**`R-em3d49-4d`** A boundary's face is drawn tinted, like the air box's, and listed in the tree under its
object.

## 5. `R-em3d49-5` — Simulate, results and fields

**`R-em3d49-5a`** *Simulate ▸ Run* with a `.c3d` active runs the **active setup** through
`Em3dRunService`: the same discovery, lowering, run, progress (brief 21), cancellation and refusals as a
`.cem`. The results land at brief 42 §5d's path, and open in the Data Display as a `.cem` run's do.

**`R-em3d49-5b` Fields** (brief 29) draw over the **run's** geometry, read from the run's own directory.
If the document has changed since the run, the field view says so in a banner (*"fields are from the run
at 14:02; the model has changed since"*) and still shows them. It never re-maps a field onto geometry
the solver did not see (series 2's rule).

**`R-em3d49-5c` Headless** needs nothing new. Ports and boundaries are authored by writing the `.c3d`,
and the reference page gains both records and the inference rule. `check` reports every port's inferred
polarity and every refusal. `explain` names, per port, the conductors it touches and why each was chosen.

## 6. Gate

`tests/Ui.Tests/ThreeD/Simulate*`, `tests/Ui.Tests/Em3d/` (goldens unchanged).

1. **Polarity inference:** fixtures for each case in §2b — a trace-to-ground gap; a gap inside an
   instance (`U1/pad`); a PEC box face; each refusal's text. With `Positive`/`Negative` stated, inference
   is not consulted (counter).
2. **Equivalence with a `.cem`.** Brief 42's oracle, with its setup's ports now drawn as `.c3d` ports
   (brief 48 §7 translates them), gives an `Em3dPort` list equal to the `.cem` route's: numbers,
   rectangles, objects and directions.
3. **A closed-form cavity, by face boundaries.** An air box (or a dielectric block) whose six faces carry
   `Pec` face boundaries, solved for eigenmodes. The TE101 frequency matches the rectangular-cavity
   formula to brief 23's cavity tolerance. This is an **external reference** (a closed form), not
   circuitRF agreeing with itself. `Category=Benchmark` if over ~5 s; use brief 23's fixture size.
4. **Palace boundary recovery** counts the face's surfaces exactly. A fixture where a neighbour's coplanar
   face would be caught by a naive box query is refused, not merged.
5. **Setup edits** from the panel round-trip the `.c3d` byte for byte, and undo restores them.
6. **Air-box edits** write only the active setup.
7. **Goldens:** every existing Palace/openEMS golden is byte-identical (no face boundaries present).
8. **Stale fields banner** appears after an edit, as a view-model state.

## 7. Owner check (pixels not seen)

In the **Debug** build:
- draw a microstrip: substrate box, ground sheet, trace prism;
- draw two ports and see their arrows; flip one;
- make one a wave port on the box face;
- set the box's bottom to PEC;
- Simulate with Palace; read S21; see |E| over the model;
- edit the trace and see the stale-fields banner.

## 8. Scope

- **Ports per setup.** Every setup uses every port (§2a); choosing a subset per setup is a later brief.
- **Absorbing boundaries inside the domain** (§4b), and impedance boundaries with surface roughness.

**In scope, and easy to miss: static terminals on drawn geometry.** Brief 22's `Terminals3D` name their
conductors by **net**, and a drawn object carries no net. So a static setup on a `.c3d` could name no
terminal at all. Add `Objects` to `EmTerminal3D` as an alternative to `Net` (a list of object names,
`U1/…` allowed), and refuse a terminal that states both. This is the smallest change that makes a drawn
package's RLC matrix reachable. Gate: a two-plate capacitor drawn as two boxes, with terminals by object,
matches brief 22's parallel-plate fixture.

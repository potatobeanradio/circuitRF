# circuitRF — Layout View (design + plan)

**Status:** Proposal — rev 5 (kernel W, 3D wirebonds — §10.11, phases LW1/LW2) · **Date:** 2026-08-04 · **Phase:** 8 (proposed)

**Decisions taken (owner, 2026-07-26).** All are folded into the body below; §13 keeps the record.
1. **MoM kernel: quasi-static 2D per-unit-length first**, then full-wave single-dielectric, then the
   general layered stack (§10.3, option A→B→C).
2. **Both markets are first-class**, selected **per workspace** — PCB and MMIC each get a starter
   technology, a layout template, and a hero (§1.2, §2.4).
3. **The layer table and substrate stackup live in a workspace-level shared tech file** (§2.4).
4. **Shapes carry a net**, with LVS a named future direction (§3.4).
5. **Same-layer overlap darkens**, with merge as a documented performance fallback (§2.3, §5.3).
6. **Curves, circles and rounded rects are first-class primitives**, flattened on demand from a context
   menu and automatically on GDSII export (§3.2).
7. **`.clay` is plain JSON**; gzip held in reserve (§4).
8. **Clipper2 approved** (§6.1) — added to the README acknowledgments.
9. **An EM run produces an `.snp` artifact** the schematic consumes through the existing SnP component,
   preserving the TestBench invariant (§10.8).
10. **DRC is in scope**, starting with a minimal width/spacing check that establishes the framework (§9A).
11. **Polygons and curves carry explicit holes** (§3.1a). GDSII keyholes them at export; the database keeps
    them.
12. **Bitmaps are a primitive** (§3.1b) — reference images to trace over, stored by path, always painted
    behind the geometry, and never exported to a fabrication format.

**Decision taken (owner, 2026-08-04).**

13. **A fourth MoM kernel, W, covers 3D wirebond geometry** — thin-wire, quasi-static first, wires-only
    first, sharing `IEmKernel` and everything else in §10 (§10.11). Designed in
    [`mom-wirebond-kernel.md`](mom-wirebond-kernel.md); phases **LW1/LW2**, which depend on L7 and
    **not** on L8/L9.

The layout view holds a cell's **physical geometry**: the 2D shapes that get manufactured. Two consumers
justify it, and they pull in slightly different directions:

1. **Fab handoff** — GDSII to a semiconductor fab, Gerber + drill to a PCB house, DXF to a mechanical
   or tooling flow. Here the layout is a *document*: fidelity, units, and layer mapping are everything.
2. **EM simulation** — a lightweight 2.5D Method-of-Moments s-parameter solve. Here the layout is a
   *model*: it needs a substrate stackup, a mesh, ports, and a numeric kernel.

Companions: `mom-wirebond-kernel.md` (kernel W — 3D wirebonds, §10.11),
`grid-and-connectivity.md` (the two-grid philosophy this extends), `symbol-editor.md`
(the editor pattern to mirror), `workspace-and-project-tree.md` §2 (cell folders and primacy),
`project-file-formats.md` (JSON conventions), `schematic-hierarchy-navigation.md` (push-in/pop-out),
`ui-architecture.md` (the UI firewall).

**Scale of the effort.** Sections 1–9A are a large but conventional editor build — comparable to the
schematic + symbol editor together. Section 10 (MoM) is a research-grade numerics project on its own
and is scoped separately with an explicit staged path. Do not treat them as one phase. Within §10, the
research risk is concentrated in the **layered-medium Green's function** (§10.2) — kernels A and W1/W2
use a free-space kernel and are ordinary, bounded engineering.

---

## 0. Reuse map — what already exists

The single largest cost saving in this plan is *not writing* the following again.

| Existing asset | How layout uses it |
|---|---|
| `CellFolder` — `ViewType.Layout`, `layout/` sub-folder, `.clay` extension, `PrimaryLayout` primacy | Already scaffolded. No new cell-folder work. |
| `.ccell` / `CellPersistence` / five-branch primacy resolution | Unchanged. Layout is the third view, not a special case. |
| `HierarchyResolver`, `ICellResolver`, `IHierarchyHost`, push-in/pop-out nav | Layout hierarchy is the same navigation model with a different resolver (`CellLayoutResolver`). |
| `SymbolEditorViewModel` shape — 15 tools, `SetActiveToolCommand` as `RelayCommand<string>`, `EnumEqualsToBoolConverter` for toolbar active state, per-document `UndoRedoStack`, `Execute(IUiCommand)` | Copy the *pattern* wholesale. Layout has its own tool enum and its own commands, but zero new plumbing invented. |
| Gesture vocabulary: two-point drag, multi-point click, Escape-cancels, live ghost primitive via overlay | Reused verbatim for rect / polygon / path. |
| `SymbolEditorCanvas` — Skia control with pan/zoom, delegates pointer/key to the VM | Structure reused; the *renderer* behind it is new (§5). |
| `AtomicFile`, `format_version` reject-on-mismatch, `System.Text.Json` polymorphic `$type`, `Id` never persisted | `.clay` follows these to the letter. |
| `CellUsageScanner` (reference counting + rewrite on rename) | Extend to scan `.clay` cell references so Rename/Remove Cell stays correct. |
| `SchematicSpatialIndex` | Informs but does not satisfy layout's needs — layout needs a real R-tree (§5.2). |
| `DataSet` / `DataCube` / Data Display / Touchstone export | EM results plug straight in. **No new result type.** This is worth a lot. |
| `IMessageSink` / Messages pane | All import warnings, off-grid reports, unmeshable geometry, port errors. |
| Theming `ColorRole` / `Rgba` / `.ccolor` | Layer colors are *literal RGBA*, not roles (§2.2) — the one deliberate departure. |

**Where reuse stops.** The symbol renderer draws ~20 primitives with per-primitive `SKPath` construction
each frame. A layout draws 10³–10⁶. `SchematicRenderer.DrawSymbol` must **not** be pressed into service
for layout geometry — §5 is a different rendering architecture, and conflating them will silently cap
performance at a few thousand shapes.

---

## 1. Coordinates, units, and grids

This is the section to get right first; everything downstream inherits it.

### 1.1 The database unit (DBU) — integer coordinates

**R1. All layout coordinates are 64-bit integers in database units.** Not doubles.

The schematic's on-grid invariant already established the principle — *"connection is equality, not
proximity"*. Layout needs the same guarantee for a harder problem: after a rotate, a paste, a boolean
op, and a GDSII round-trip, two vertices that should coincide must compare **exactly** equal. Doubles
do not give that; integers do.

Consequences, all of them good:
- Boolean operations, polygon merge, and vertex dedup become exact.
- 90° rotation and mirroring map integers to integers exactly.
- GDSII is *natively* an integer format — the mapping is the identity.
- "Is this vertex on the snap grid?" is `x % snap == 0`, verified by arithmetic (mirroring §2 R1 of
  `grid-and-connectivity.md`), not by tolerance.

`long` gives ±9.2e18 DBU. At 1 nm resolution that is ±9.2e9 metres of addressable space — absurdly
safe. `int` would give only ±2.1 m at 1 nm, which a large panel-level PCB could approach; don't use it.

### 1.2 Three independent numbers — keep them separate

The requirement *"change units such that polygons remain on-grid"* dissolves once these three are
separated. Conflating them is what makes unit changes destructive in lesser tools.

| Number | What it is | Who changes it | Cost of change |
|---|---|---|---|
| **DBU resolution** (`DbuPerMicron`) | The storage quantum. Default **1000** → 1 DBU = 1 nm. | Set at layout creation; changing it is a migration. | §1.4 — expensive |
| **Display unit** | µm, nm, mm, mil, inch. What numbers the user reads and types. | User, freely, anytime. | **Zero** — §1.3 |
| **Snap grid** | The pitch new/edited vertices land on, expressed in DBU. Default 1 µm (PCB) / 5 nm (MMIC). | User, freely, anytime. | Zero for existing geometry — §1.5 |

**R2. `DbuPerMicron` defaults to 1000 (1 DBU = 1 nm), and this is deliberate.** 1 µm = 1000 DBU and
1 mil = 25400 DBU are **both exact integers**. A metric-authored layout and an imperial-authored layout
are therefore commensurable at the database level, and mil↔µm conversion never introduces a fractional
coordinate. This one choice removes the entire class of "the imported board is 0.0003 mil off grid" bugs.

**This is also what lets both markets share one storage model.** A PCB workspace authors in mils on a
1 mil snap; an MMIC workspace authors in µm on a 5 nm snap; both store nanometre integers, and a cell
can be pasted between them with an exact, lossless transform. And because refinement is *always*
lossless (§1.4), starting every workspace at 1 nm is safe — a process that later needs 0.1 nm can be
migrated up with no rounding, whereas a design authored coarse and needing finer detail would not be
recoverable if the resolution were stored as a float scale.

**Per-workspace defaults.** The default display unit and snap grid are properties of the workspace's
technology (§2.4), not hardcoded: `mil` / 1 mil for the PCB starter tech, `µm` / 5 nm for the MMIC
starter tech. `DbuPerMicron` stays 1000 for both.

### 1.3 Changing the display unit is free — this is the elegant answer

**R3. The display unit is a *presentation* property. Changing it mutates no geometry.**

A polygon vertex at 25400 DBU displays as `25.4 µm` or `1 mil` depending on the current display unit.
The stored integer is identical. Therefore:

- Every polygon stays exactly where it was.
- Every polygon stays exactly on whatever grid it was on.
- The operation is instant, lossless, and needs no undo entry beyond a view-preference change.
- It round-trips perfectly: µm → mil → µm returns the identical file.

This is why the storage unit must **not** be the user's working unit. The moment you store "2.9 mm" as
a double and then switch to mils, you are rounding real geometry. Storing integer nanometres and
*rendering* them in whatever unit the user prefers makes the whole problem go away.

The display unit is stored in `.clay` as a convenience (so a layout reopens in the unit it was authored
in) but carries no semantics.

### 1.4 Changing the DBU resolution *is* a migration

**R4. `DbuPerMicron` may only change by an exact integer ratio, as an explicit, undoable, validated
operation — never a preference toggle.**

This mirrors `grid-and-connectivity.md` §4 (changing the connection pitch `P`) and should reuse the same
warn + rescale + validate + report machinery:

- **Refinement** (1 nm → 0.1 nm, ratio 10): always lossless. Multiply every coordinate by 10. Proceed
  silently, log to Messages.
- **Coarsening** (0.1 nm → 1 nm): lossy iff any coordinate is not divisible by the ratio. Pre-scan the
  whole design (including all referenced sub-cells), and if any vertex would move, **report the specific
  offenders to Messages and require confirmation** — never a silent snap.
- **Non-integer ratios**: rejected outright.

In practice most users never touch this. The default is right for both target markets.

### 1.5 Changing the snap grid never re-snaps existing geometry

**R5. The snap grid governs *future* edits only.** Changing it from 1 µm to 0.5 µm does not move a
single existing vertex. A separate, explicit **"Snap selection to grid"** command exists for when the
user actually wants that; it is one undoable action and reports any shape it could not snap without
self-intersecting.

This is the inverse of the schematic's rule and it is correct for layout: in a schematic an off-grid pin
is a *correctness* bug (it breaks connectivity); in a layout an off-grid vertex is merely unusual —
imported GDSII, a 45° diagonal, and a flattened arc all legitimately produce vertices that sit between
snap-grid points.

### 1.6 Why layout does not reuse `P` and `p`

`P = 100` world units is an *abstract* pitch with no physical meaning — 100 mils by convention, but the
schematic never converts it to metres. Layout coordinates are physically real and must survive a fab
handoff. They are different quantities that happen to both be called "grid". Keep them completely
separate: a layout has no `P`, a schematic has no DBU, and nothing converts between them except the
schematic-to-layout generator (§9), which places instances by *layout* extents, not by schematic pitch.

**R6. Layout display always shows real physical dimensions** — a status-bar cursor readout, a live
dimension readout during any draw/drag gesture, and numeric entry fields that accept `2.9mm`, `115mil`,
`50u` with unit suffixes parsed. This is not polish; it is how layout is actually done, and it is a
precondition for the §10.10 thirty-second target.

---

## 2. Layers and the technology file

### 2.1 Layer identity

**R7. A layer is identified by an integer `(Layer, Datatype)` pair, not by its name.**

This is GDSII's model and it is the right one: names are for humans and change; the numeric pair is what
a fab's process assumption is keyed to, what a GDSII file carries, and what a PDK layer map specifies.
DXF is name-keyed, so DXF import/export needs an explicit name↔pair mapping (§8). Gerber has no layer
concept at all — one file per layer, mapping declared at export.

```
LayerDef {
    int    Layer;          // GDSII layer number
    int    Datatype;       // GDSII datatype / purpose
    string Name;           // "M2", "Top Copper", "Via1" — display only
    Rgba   Color;          // literal color, not a ColorRole
    double FillOpacity;    // default 0.35
    int    ZOrder;         // paint order; also the selection-cycling order
    bool   Visible;
    bool   Selectable;     // visible-but-locked is a distinct, useful state
    string? Purpose;       // "drawing" | "pin" | "label" | "blockage" | ...
}
```

### 2.2 Layer colors are literal, not roles

The schematic's `ColorRole` indirection exists so a theme can restyle a symbol. A layout layer's color
is **user-authored data**, not a theme decision — "M2 is teal" is a fact about this technology that must
survive a light/dark switch and must match what the user sees in KLayout. So layers carry `Rgba`
directly. The layout *chrome* (background, grid, rulers, selection, handles) still uses `ColorRole` and
still themes normally.

Dark/light: keep one color per layer and adjust the **background** and stroke contrast per variant,
rather than storing two palettes.

### 2.3 The rendering contract

**R8. Every shape renders as a fill in its layer color at the layer's `FillOpacity`, with a fully opaque
1 px stroke in the same color.** (Exactly the selector marquee's look, as specified.)

**R8a. Same-layer overlap darkens.** Two overlapping shapes on one layer composite their alpha and read
darker, exactly as two shapes on different layers do. This is the more *informative* rendering: an
unintended double-drawn polygon, a shape pasted twice, or a sliver of unmerged geometry all become
visible instead of silently disappearing into a merged fill. Cross-layer overlap blends the same way.

**This has a real rendering cost, and it is the one place §5 must compromise.** Darkening requires each
shape to be composited separately, so a layer's fills cannot collapse into one batched `SKPath`. §5.3
handles this with per-shape fills, batched *strokes* (opaque, so they may still merge into one path per
layer), and an automatic fallback:

**R8b. Merge is an automatic LOD tier, not a mode the user manages.** Above a visible-shape threshold the
renderer switches that layer to a single merged fill. This is not a compromise of intent — at the zoom level
where that many shapes are on screen, individual overlaps are sub-pixel and imperceptible anyway. A
preference forces merge permanently for anyone who prefers it.

**Measured, L2a (2026-07-27): there is no performance crossover — merging is *always* cheaper.** A
single-layer sweep from 500 to 100,000 shapes found merged fills faster than per-shape fills at every
density, by a margin that *shrinks* with density (1.21× at 500 shapes/layer down to 1.02× at 100,000) — the
opposite of the break-even cliff this rule originally assumed when it suggested starting at ~20k. The real
lever is **draw-call count across many layers**, not per-layer fill cost: in the 200-layer scenario merged
wins 15–25% even at only 1k–50k total shapes.

So the threshold is a **UX** decision, not a performance one. The only cost of merging early is the one R8a
names — same-layer overlap stops reading as darker — and the performance case for a low threshold is
stronger than "start at ~20k" implied.

### 2.4 The technology file `.ctech`

**Decided:** the layer table and substrate stackup live in a **workspace-level `.ctech` file**, not in
the `.clay`. A layer table is technology-scoped, not cell-scoped; duplicating it per layout guarantees
drift, and the stackup is by definition shared by every cell fabricated on that process.

**R7a. A `.ctech` holds everything that is true of the *process* rather than of one cell:**

- the layer table (§2.1),
- the substrate stackup (§10.4) — dielectric and conductor layers with thickness, εr, tanδ, σ, µr,
- the drawing-layer → stackup-conductor mapping,
- interchange mappings (GDSII layer/datatype ↔ DXF layer name ↔ Gerber file suffix and X2 file function),
- default display unit and snap grid for layouts created against it (§1.2),
- DRC rules (§9A) — minimum width and minimum spacing per layer to begin with.

**Location and reference.** A `tech/` folder at the workspace root holds one or more `.ctech` files;
`.cws` records which is the workspace default. Each `.clay` stores a relative `tech_ref` plus its own
`dbu_per_micron`. Allowing several per workspace costs nothing and covers the real cases — a module
that is an MMIC die on a PCB carrier, or two board stackups in one project — while "one default per
workspace" keeps the common path a non-decision.

**A null `tech_ref` means "the workspace default", and "the workspace" means the *document's* workspace** —
found by walking up from the `.clay`'s own path to the nearest ancestor `.cws`, not by reading whichever
workspace happens to be loaded. For a layout inside the open workspace these are the same thing; for one
opened from elsewhere they are not, and resolving against the wrong one would silently reinterpret its
layers. See `workspace-and-project-tree.md` §5A.2 (R32) — the collision is real, because both starter
technologies below use layer keys `(1,0)`–`(8,0)`.

**Both markets, per workspace.** Two starter technologies ship, and the New Workspace dialog picks one:

| | **PCB starter** | **MMIC starter** |
|---|---|---|
| Display unit / snap | mil / 1 mil | µm / 5 nm |
| Layers | Top Copper, Bottom Copper, Soldermask ×2, Silk ×2, Drill, Outline | Metal1, Metal2, Via, Resistor, Cap Dielectric, Nitride, Substrate, Backside Via |
| Stackup | 1.6 mm FR-4, εr 4.4, tanδ 0.02, 1 oz copper, bottom ground | 100 µm GaAs, εr 12.9, tanδ 0.0006, 3 µm plated gold, backside ground |
| Primary interchange | Gerber + Excellon | GDSII |
| Hero | 50 Ω microstrip on FR-4 | 50 Ω microstrip on 100 µm GaAs |

The two differ only in *data*, never in code path. If a per-market code branch ever appears, something
has been modelled in the wrong place.

**Editing.** `.ctech` gets its own dockable editor document (layer table grid + the §10.4 stackup
diagram), registered in the project tree like `.cdd` is today, saved through `AtomicFile` with the same
`format_version` policy. Changing a layer color must live-refresh every open layout.

**Missing tech file.** Handle it exactly as `MissingNamedPrimary` is handled — a distinct state,
surfaced as a warning, layers rendered from a generated fallback palette keyed by `(layer, datatype)`,
and editing still permitted. Never block on it, and never silently invent a stackup for an EM run —
that one *does* refuse.

---

## 3. The geometry model

### 3.1 The primitives

**R9. The layout primitive set stays small, and every primitive that a target format cannot express
carries an explicit, user-visible flattening story.** The symbol editor has 14 drawing primitives
because symbols are illustrations. Layout shapes are manufactured, so the question for each primitive
is not "is it useful to draw?" but "what exactly ships when this is exported?"

| Primitive | Fields | Notes |
|---|---|---|
| `Polygon` | layer, `long[] xy` (flat, implicitly closed), optional inner rings | The workhorse. Everything degrades to this. See §3.1a for holes. |
| `Rect` | layer, x1 y1 x2 y2 | Axis-aligned. Not just sugar — the most common shape by far, and a specialised path is meaningfully faster to hit-test, index, and render. |
| `RoundedRect` | layer, x1 y1 x2 y2, corner radius | Pads, keepouts, rounded apertures. Native in DXF; flattened for GDSII. |
| `Circle` | layer, center, radius | Pads, via barrels, guard rings. Native in DXF (`CIRCLE`) and Gerber (circular aperture or G02/G03); flattened for GDSII. |
| `Curve` | layer, closed edge list (see §3.2), optional inner rings | A filled region whose boundary edges may be lines, circular arcs, or cubic Béziers. Spiral inductors, tapers, curved guard structures. |
| `Path` | layer, centerline **edge list**, width, end style (flush / round / square / extended) | Keeps a trace *parametric*: change the width of a 40-segment route in one edit. The centerline uses the same edge vocabulary as `Curve`, so a **curved trace** — a swept bend, a radiused corner — is a `Path`, not a hand-built polygon. Maps to GDSII PATH and to Gerber D01 stroking. |
| `Via` | layer(s), position, pad size, drill size | PCB needs a drill file, and a drill is not a polygon. Carrying it explicitly is what makes Excellon export possible without heuristics. |
| `Label` | layer, position, text, size, rotation, `IsPort` | Two roles: annotation, and the port/pin marker that §9 and §10.6 key on. |
| `Bitmap` | layer, placement rect, image **path reference**, opacity, locked | A reference image — a scanned drawing, a die photo, a datasheet figure — to trace over. See §3.1b; it is the one primitive that is not geometry at all. |
| `Instance` | cell ref, transform (translate + R0/R90/R180/R270 + mirror-X + magnification), optional array (rows, cols, pitch) | §7. Arrays matter enormously for MMIC. |

One thing is deliberately **not** a primitive: **text as geometry.** A `Label` is metadata. If a fab
needs a text marking as real copper, that is an explicit "convert text to polygons" command using a
stroked vector font — the same shape of operation as §3.2's flatten.

### 3.1a Holes

**Decided: `Polygon` and `Curve` carry explicit holes** — an optional list of inner rings alongside the
outer one. Not keyholed slits in the database, and not an operation that gets refused.

This is forced by the most ordinary operation in PCB layout: subtracting a via pad, an antipad or a
clearance from a ground pour. Holes are the common case for pours and keepouts, not an edge case.

Why explicit rings rather than a keyhole:

- Three of the four consumers already handle multi-contour geometry natively — `SKPath` renders it under one
  fill rule, point-in-polygon hit-testing is the same ray cast, and Clipper2 produces exactly this structure
  in its `PolyTree`. Only the flattener changes shape, and it already returns a list of rings.
- A keyhole is **lossy and irreversible**. Once the slit is cut the hole stops being a distinct entity,
  subsequent booleans behave differently, and §10.5's mesher would be asked to mesh a degenerate zero-width
  channel — which is exactly the geometry a mesher handles worst.
- Only **GDSII** genuinely cannot express a hole. That is a property of one interchange format, and §8's
  standing rule is that format limitations are resolved at the export boundary with a reported note. Letting
  GDSII's restriction reshape the database would be the same mistake §1 refuses to make when it keeps the
  display unit out of storage.

**R10b. A hole lies strictly inside its outer ring and intersects neither that ring nor another hole.**
Clipper2 output satisfies this by construction; any other path into the model — paste, import, a hand-edited
file — is normalized through a union rather than trusted. A hole that escapes its outer ring renders and
hit-tests as nonsense, and the resulting bug reads as a rendering fault rather than a data fault.

**At export**: GDSII keyholes (§8) — a zero-width slit from each inner ring to the outer, emitting one
self-touching contour, which is what every GDSII writer does. DXF and Gerber both express holes natively and
need no such treatment. Re-importing keyholed GDSII yields a keyholed polygon rather than the original; that
is inherent to the format and belongs in the export dialog's fidelity note next to curve flattening.

### 3.1b Bitmaps

Every other primitive in §3.1 is manufacturable geometry that some target format can express, however
lossily. A `Bitmap` is neither: it exists so the user can place a reference image — a scanned board, a die
photo, a mechanical drawing — and draw real geometry over it. The symbol editor already has this primitive;
layout reuses its model, its decode cache and its broken-path handling rather than growing a second one.

**R10c. A bitmap is stored as a path reference, never as embedded bytes.** `.clay` stays a readable JSON
text file, and a 4 MB die photo does not become part of the design database. The cost is that a moved or
renamed image breaks the reference, so a missing file renders a visible placeholder box — never an invisible
gap — and a **Resolve Path…** command repairs it.

**R10d. Bitmaps always paint behind all layers, whatever their layer's `ZOrder`.** A bitmap's layer governs
its **visibility and selectability only**, never paint order. The use case is tracing, and §2.3's
semi-transparent fills reading on top of a photograph is exactly the intent. This is a deliberate exception
to the layer ordering rule and is stated here so it does not read as a bug.

**R10e. A bitmap is not geometry, and every geometric consumer skips it — with a note, never silently.**
It is **never** exported to GDSII, DXF or Gerber (§8); DRC (§9A) ignores it; the mesher (§10.5) ignores it;
and the boolean, offset and flatten commands are disabled against it with a reason, per R13a. It **is**
rendered in the clipboard's PDF/SVG/EMF graphic export, because that is a picture rather than a fabrication
artifact. Selection, move, scale, clipboard and undo all treat it as an ordinary shape.

Because a bitmap has no vertices, a single selected bitmap shows §6.1's bounding-box scale handles rather
than vertex handles, and non-uniform scaling is legitimate stretching — there is no arc-to-cubic promotion
to worry about.

### 3.2 Curved primitives and flattening

**Decided:** `Curve`, `Circle` and `RoundedRect` are stored as themselves, not silently converted at
draw time. DXF represents all three natively and Gerber represents arcs natively, so pre-flattening
them would throw away fidelity those formats can carry — and it would make a spiral inductor or a
radiused bend miserable to edit afterwards.

**R9a. One edge vocabulary serves every curved primitive.** An *edge list* is a sequence of vertices
where each edge is `Line`, `Arc` (circular, given by a bulge factor or by center + sweep), or `Cubic`
(Bézier control points). `Curve` is a closed edge list; `Path` uses one for its centerline; `Circle`
and `RoundedRect` are analytic shorthands that expand to edge lists on demand. One flattener therefore
serves all of them, and one hit-tester, and one bbox routine.

**R9b. Every curved primitive carries a flatten tolerance** — a sagitta in DBU, defaulted from the tech
file (suggested: 1 µm for PCB, 10 nm for MMIC), overridable per shape. Tolerance, not angle steps: a
sagitta bound is what actually controls manufacturing error, and it produces few segments on gentle
curves and many on tight ones, which an angle step does not.

**R9c. Rendering flattens adaptively at screen resolution; export flattens at the stored tolerance.**
A circle looks smooth at any zoom, which means the editor is *not* strictly WYSIWYG for curves — what
ships is slightly coarser than what is drawn. Two things resolve that honestly:
- a **"Preview export flattening"** view toggle that renders every curve at its stored tolerance, so the
  shipped geometry is inspectable without committing to it;
- **"Flatten to Polygon"** below, which makes it literal and permanent.

**R9d. "Flatten to Polygon…" is a context-menu command on any curved primitive, and it always prompts.**
Right-click a selected `Curve`, `Circle`, `RoundedRect`, or arc-bearing `Path` → **Flatten to Polygon…**.
It replaces the shape in place with the polygon that export would have produced, as **one undoable action**.
- **There is exactly one entry and it carries the ellipsis.** Flattening is irreversible except by undo and
  its resolution is the point of the operation, so the tolerance is shown and confirmed rather than
  inferred. The dialog pre-fills from the shape's own `FlattenTolDbu` if set, otherwise the technology
  default — labelled so the user can see which — and shows the resulting vertex count live as it is typed.
- The dialog does **not** write its value back: every shape it touches stops being curved, so there is
  nothing left for a tolerance to govern. The persistent per-shape value lives in the properties panel
  (available on all four curved primitives, per R9b) and is what **GDSII export** reads for shapes the user
  never flattens manually. Both surfaces agree by reading the same source.
- The command applies to a multi-selection, naming how many shapes will be skipped for having no curvature.
- **Flatten All Curves** on a layer or on the whole layout uses the same dialog, prompting once.
- After flattening, the primitive is an ordinary `Polygon` — there is no un-flatten beyond undo.

**R9e. Two operations flatten implicitly, and both must say so.**
- **GDSII export** flattens every curved primitive automatically, because the format has no curve
  record. Non-blocking Messages note: *"Flattened 14 curved shapes at 1 µm tolerance for GDSII export."*
- **Boolean and offset operations** flatten their operands, because Clipper2 is polygon-only (§6.1).
  Subtracting a rectangle from a circle yields a polygon, permanently. This is the gotcha most likely
  to surprise a user, so it warns the first time per session rather than silently mutating the design.

**Per-format behaviour** is summarised in §8; briefly: DXF keeps circles, rounded rects (as polylines
with bulge) and arcs; Gerber keeps arcs via G02/G03 and circular apertures; GDSII keeps none;
Béziers flatten everywhere except DXF, where they may optionally emit `SPLINE`.

**Bounding boxes and hit-testing.** `Circle` and `RoundedRect` get exact analytic bboxes; arcs get
exact bboxes from their extreme points; Béziers get the convex hull of their control points
(conservative, which is correct for a spatial index). Hit-testing uses a cached flattened outline at a
tolerance tied to the current zoom, rebuilt only when the shape or the zoom tier changes.

### 3.3 Angle mode

**R10. Angle mode is a per-layout preference: Manhattan / 45° / any-angle. Default: any-angle**, since
RF geometry (spirals, tapers, mitred bends) needs it. Manhattan and 45° modes constrain interactive
drawing and vertex dragging only; they never retroactively reject existing geometry, and they never
constrain curved primitives — a `Circle` in Manhattan mode is still a circle.

### 3.4 Nets on shapes

**Decided: every shape may carry a net, and LVS is a named future direction.**

**R10a. Each shape carries a nullable `Net` string, and the editor maintains it** — it is not merely a
field that something else might one day populate. Concretely:
- §9's schematic-to-layout generator stamps nets onto the pins and shapes it emits.
- Copy/paste, flatten, and boolean operations **propagate** the net of their source; a boolean between
  two differently-netted shapes clears the net and reports, rather than picking one arbitrarily.
- A shape's net is editable from the properties panel and displayed in the status readout alongside its
  layer, so a mis-netted shape is visible rather than latent.
- The ratsnest (§9) draws from it, and §9A's DRC uses it for the check that matters most in practice:
  spacing rules apply *between different nets*, not within one.

**Toward LVS.** Full LVS needs three things layout does not have yet: geometric connectivity extraction
(union shapes that touch, per layer, then bridge through vias), device recognition, and graph
comparison against the extracted schematic netlist. The **first** of those is a natural extension of
§9A's DRC framework — both are "walk the geometry with Clipper2 and report" — and it is what upgrades
the `Net` field from *asserted* to *verified*. Nothing here needs to be built now; carrying the field
from day one is what keeps the door open, since retrofitting a net attribute through a persisted format,
an undo system, and every geometric operation is exactly the kind of change that never happens later.

---

## 4. Persistence — `.clay`

Follows the house conventions exactly: `System.Text.Json`, `format_version` with reject-on-mismatch, no
`Id` ever persisted, polymorphic `$type` discriminators as in `SymbolModel.cs`, written through
`AtomicFile`. **Landed in L0a** (`src/Ui/Layout/LayoutPersistence.cs`) — the block below is real
`LayoutPersistence.Serialize` output, not an illustrative sketch. Two points the original sketch got
wrong, now corrected: property names are **PascalCase with no naming policy** (`FormatVersion`, not
`format_version`) — `.clay` follows `.csym` exactly, snake_case was shorthand and never what ships; and
`Label` is a **shape** living in `Shapes` like every other primitive, not a separate top-level `labels`
array.

```jsonc
{
  "FormatVersion": 1,
  "DbuPerMicron": 1000,
  "DisplayUnit": "Mil",
  "SnapDbu": 1000,
  "AngleMode": "AnyAngle",
  "TechRef": "../../tech/pcb-2layer.ctech",
  "Shapes": [
    { "$type": "Rect", "X1": 0, "Y1": 0, "X2": 2900000, "Y2": 20000000,
      "Layer": { "Layer": 1, "Datatype": 0 }, "Net": "RFin" },
    { "$type": "Poly", "Xy": [0,0, 500,0, 500,300, 0,300],
      "Layer": { "Layer": 1, "Datatype": 0 } },
    { "$type": "Circle", "Cx": 4000000, "Cy": 1000000, "R": 300000,
      "Layer": { "Layer": 2, "Datatype": 0 }, "Net": "GND" },
    { "$type": "RRect", "X1": 0, "Y1": 0, "X2": 1000000, "Y2": 600000, "CornerRadius": 150000,
      "Layer": { "Layer": 1, "Datatype": 0 } },

    // Curve: closed edge list, parallel to Xy — Edges[i] is the edge leaving vertex i. Every edge is
    // a full LayoutEdge object (Kind + Bulge + C1X/C1Y/C2X/C2Y); Bulge is meaningful only for Arc,
    // the control points only for Cubic. A null Edges list (omitted here per WhenWritingNull) means
    // every edge is a straight line, i.e. a plain polygon.
    { "$type": "Curve", "Xy": [0,0, 2000000,0, 2000000,2000000], "FlattenTolDbu": 1000,
      "Layer": { "Layer": 1, "Datatype": 0 },
      "Edges": [
        { "Kind": "Line", "Bulge": 0, "C1X": 0, "C1Y": 0, "C2X": 0, "C2Y": 0 },
        { "Kind": "Arc",  "Bulge": 0.4142, "C1X": 0, "C1Y": 0, "C2X": 0, "C2Y": 0 },
        { "Kind": "Line", "Bulge": 0, "C1X": 0, "C1Y": 0, "C2X": 0, "C2Y": 0 }
      ] },

    // Path: same edge vocabulary on the centerline (N-1 edges for N vertices, open) — a radiused
    // bend is still one editable trace, not a hand-built polygon.
    { "$type": "Path", "Xy": [0,0, 5000000,0, 5000000,3000000], "Width": 2900000, "End": "Flush",
      "FlattenTolDbu": 1000, "Layer": { "Layer": 1, "Datatype": 0 }, "Net": "RFin",
      "Edges": [
        { "Kind": "Line", "Bulge": 0, "C1X": 0, "C1Y": 0, "C2X": 0, "C2Y": 0 },
        { "Kind": "Arc",  "Bulge": 0.4142, "C1X": 0, "C1Y": 0, "C2X": 0, "C2Y": 0 }
      ] },

    // Label: a shape like any other — the port/pin marker §9/§10.6 key on, not a separate section.
    { "$type": "Label", "X": 0, "Y": 0, "Text": "P1", "Height": 500000, "Rotation": "R0", "IsPort": true,
      "Layer": { "Layer": 1, "Datatype": 0 } }
  ],
  "Instances": [
    { "CellRef": "../../inductor_2n5", "X": 100000, "Y": 0, "Rot": "R90", "MirrorX": false,
      "Mag": 1, "Rows": 1, "Cols": 1, "PitchX": 0, "PitchY": 0 }
  ]
}
```

**Format notes.**
- Flat integer arrays for vertices, not `[[x,y],[x,y]]`. Roughly half the bytes and it parses faster.
- Integers stringify shorter than the equivalent doubles — another argument for §1.1.
- Curved primitives store their **edge list**, not their flattened form — a flattened circle at 1 µm
  tolerance is hundreds of vertices. The actual wire encoding is a full `LayoutEdge` object per edge
  (uniform shape regardless of `Kind`, per the house "no per-type wire shorthand" convention), not the
  terse per-kind tags (`"L"`, `{"A": …}`) an earlier sketch of this section showed.
- **Decided: plain JSON, uncompressed.** A 100k-polygon layout lands around 20–40 MB, which in practice
  most cells never approach because hierarchy keeps them small. **Gzip stays in reserve**: gzipping the
  bytes while keeping the `.clay` extension and JSON *content* is one line of code, roughly 10× smaller,
  and still trivially inspectable through `gunzip` — so switching later costs almost nothing and needs
  no format-version bump if the reader sniffs the gzip magic bytes. `LayoutPersistence.LoadFromFile`
  sniffs the gzip magic bytes (`0x1F 0x8B`) from day one (L0a), so the eventual switch is a writer-side
  change only. A synthetic large-layout test lands in L0 so the ceiling is measured rather than guessed.

---

## 5. Rendering and performance

*"Fast and snappy"* is the requirement most likely to be quietly lost, because it is fine at 500 shapes
and unacceptable at 50,000, and the difference does not appear until late. Treat it as a **gated
acceptance criterion**, not an aspiration.

### 5.1 The budget

| Scenario | Shapes in view | Target |
|---|---|---|
| Typical PCB cell | ~2k | 60 fps pan/zoom, no perceptible lag |
| Dense MMIC cell (spiral + fill) | ~50k | 60 fps |
| Pathological / imported full chip | ~500k | ≥20 fps, degrade via LOD, never freeze the UI |

These targets assume the §2.3 R8b merge tier engages above ~20k visible shapes per layer. The benchmark
(§5.4) measures **both** paths — per-shape darkening and merged — at every size, so the threshold is set
from data and the cost of the darkening decision is a known number rather than an assumption.

### 5.2 Spatial index

**R11. Every layout holds an R-tree (or per-layer uniform grid) over shape bounding boxes.** Every
render frame and every hit-test is a viewport query against it, never a linear scan. Rebuild
incrementally on edit; a full rebuild only on load.

Per-layer indices are worth it: with 200 layers and most hidden, a per-layer index lets hidden layers
cost literally zero.

### 5.3 Rendering architecture — the four things that matter

1. **Pan and zoom are an `SKMatrix`, never a geometry rebuild.** Build paths once in DBU-derived local
   float coordinates; apply the view transform to the canvas. Panning then costs one matrix
   multiplication. This alone is the difference between "snappy" and "not".
2. **Batch what can be batched; composite what must be.** §2.3 R8a requires same-layer overlap to
   darken, so *fills* are drawn per shape and cannot collapse into one path. Everything else still
   batches: **strokes are opaque**, so a layer's outlines merge into a single `SKPath` and cost one
   draw call regardless of shape count. Cache both per layer and invalidate on edit — not per frame.
   Above the R8b threshold the layer's fills collapse into one merged path too, at which point a layer
   costs exactly two draw calls. Reuse one `SKPaint` per layer across all its fills rather than
   allocating per shape; paint churn, not draw calls, is usually what actually costs.
3. **Level of detail.** Cull anything outside the viewport (via the index), then drop anything whose
   screen bbox is under ~2 px. At full-chip zoom this discards >95% of the geometry with no visible
   difference. Instances below a size threshold render as their bounding box outline only.
4. **Instance path caching.** A sub-cell's batched per-layer paths are built once and re-drawn under a
   different matrix for every placement. A 50×50 array of a via cell costs one path build and 2500
   matrix draws — not 2500 path builds. Without this, arrays are unusable.

Two further options, deferred until measured: a **tiled raster cache** (re-rasterise only dirty tiles at
a stable zoom) and background/incremental rendering for the pathological case. Do not build these
speculatively.

### 5.4 Measure it

**R12. A synthetic-layout benchmark harness ships with L2 and runs in CI.** Generate 1k / 50k / 500k
shapes across 200 layers — including a curve-heavy variant, since adaptive flattening is a per-frame
cost the polygon-only case never pays — measure frame time for pan and for zoom on **both** the
per-shape darkening path and the merged path, and assert against §5.1. A performance requirement with no
oracle is a wish, and the R8b threshold in particular is a number this harness produces rather than one
anybody should pick by feel.

---

## 6. Editing model

Mirror `SymbolEditorViewModel` structurally: one VM owning tool state, selection, gesture state, and the
document's own `UndoRedoStack`; every mutation an `IUiCommand`; toolbar buttons bound via
`RelayCommand<string>` + `EnumEqualsToBoolConverter` with zero code-behind.

### 6.1 Tools

**Drawing:** Select · Rect · RoundedRect · Circle · Polygon · Curve · Path · Label · Port ·
Instance-place · Array · Ruler/measure.

**Edit operations:** Boolean — **Union grouped per layer** (one result per distinct layer among the
operands; this is what the deleted `Merge` command used to do); Intersect/Difference/XOR require a
same-layer pair to enable but still combine across the whole selection, unchanged from L1e · Size
(grow/shrink by a signed offset) · **Scale** (numeric factor or target size, plus bbox handles:
corner = uniform, side = one axis) · Slice · Align/distribute · Move-to-layer · Set net ·
**Flatten to Polygon…** (§3.2, curve → polygon; always prompts for the tolerance) ·
**Flatten Hierarchy** (§7, instance → geometry) · Group-into-cell. The two flattens are different
operations on different things; label them distinctly in the UI, because "Flatten" alone will be
misread.

**R13a. Every command is either disabled with a stated reason, or it does something — never a silent
no-op.** Context-menu items are disabled rather than hidden, so positions stay stable, and each disabled
item's tooltip names the condition (*"Select 2 or more shapes on the same layer"*). A command that is
legitimately enabled but changes nothing in a particular case — a union of shapes that do not touch —
reports through Messages instead of appearing to fail. There is no separate `Merge`: it was indistinguishable
from a per-layer `Union` for any single-layer selection, and two commands differing by a subtlety nobody
reads a tooltip for are worse than one that does the obvious thing.

**Clipper2 is approved** for boolean ops and offsetting: `Clipper2Lib`, fully managed C#, Boost
Software License (permissive, MIT-compatible, not GPL), integer-coordinate native which matches §1.1
exactly, and it does clipping and offsetting from one library. It is also load-bearing for meshing
(§10.5) and for §9A's DRC. Add it to the README acknowledgments alongside CSparse.NET and NumFlat.

Clipper2 is **polygon-only**, which is what makes §3.2 R9e true: a boolean or offset flattens its
curved operands. Keep that conversion in one place — a single `ToClipperPaths(shape, tolerance)` helper
that every consumer (booleans, offsets, DRC, mesher, hit-test) calls — so the flattening tolerance is
never chosen twice with two different answers.

### 6.2 Selection with overlap cycling

**R13. Repeated clicks at the same point cycle the selection through the overlapping shapes.**

Algorithm:
1. On press, hit-test the point (with a few-px tolerance) against the spatial index, filtered to visible
   and selectable layers.
2. Order the candidates: layer `ZOrder` descending, then **ascending area** — so a small shape sitting
   on a large one is reachable, which is the case that matters in practice.
3. Cache that ordered list plus the click point. If the next press is within a few px and nothing has
   moved or changed, advance the index modulo the list length; otherwise rebuild from scratch.
4. Show the state in the status bar: `Rect · M2 · 2 of 5`. Without this readout the cycling feels like a
   glitch rather than a feature.

Alt-click as an equivalent explicit "next candidate" for users who expect it.

### 6.3 Vertex and edge editing

When exactly one shape is selected, draw square vertex handles and (for polygons) round edge-midpoint
handles.

**R14. Hit priority, strictly: vertex handle > edge handle > edge line > shape interior.** Getting this
order wrong makes vertex dragging feel broken because the body-move grabs first.

- Drag vertex → move it (snapped, angle-mode-constrained).
- Drag edge → translate that edge perpendicular to itself, dragging both endpoints and preserving the
  adjacent edges' directions. This is the operation that makes "widen this trace" a one-drag gesture.
- Ctrl/Cmd+click on an edge → insert a vertex there.
- Delete on a selected vertex → remove it (blocked below 3 vertices).
- Drag the interior → move the whole shape (the specified behaviour).
- Live dimension readout throughout, per R6.

**Curved shapes get their own handles** (§3.2). An `Arc` edge shows a midpoint **bulge handle** — drag
perpendicular to the chord to change the radius, drag past the chord to flip the sweep — which is the
fastest way to radius a corner. A `Cubic` edge shows two control-point handles with tangent lines drawn
to their anchors. `Circle` shows a single radius handle; `RoundedRect` shows corner-radius and
bbox handles. Right-click an edge to **convert** it between Line, Arc and Cubic, so a polygon can grow
a curve without being redrawn. Handle hit priority (R14) extends naturally: control point > bulge
handle > vertex > edge > interior.

Self-intersection: allow it during the drag, flag it on release, offer auto-repair via Clipper2's
union rather than rejecting the edit.

### 6.4 Clipboard across cells

Serialize the selection as a `.clay` **fragment** using the same serializer — self-describing, carrying
its source `dbu_per_micron` and the `LayerDef`s actually used.

On paste:
- **Same DBU** → paste as-is.
- **Different DBU** → rescale by the exact ratio; if the ratio is non-integer or would round, warn
  through Messages naming the offending shapes and let it proceed. This is precisely the
  `grid-and-connectivity.md` §5 cross-grid warn+snap+validate pattern; reuse the machinery and the
  wording.
- **Layers absent from the destination tech** → offer to add them, or map them to an existing layer.
  Never silently drop geometry.

All of it is one undoable action.

---

## 7. Hierarchy

An `Instance` references another cell's layout view, resolved through the existing primacy machinery
(`CellFolder.ResolvePrimary(cellDir, ViewType.Layout)`), rendered through a `CellLayoutResolver` that
parallels `CellSymbolResolver`, with the same three-state handling (resolved / not-found placeholder /
stale-and-reloading).

New concerns specific to layout hierarchy:

- **Arrays (GDSII AREF).** `rows × cols` at a pitch, stored as one object. Essential for via farms and
  MMIC unit cells, and — with §5.3's instance caching — nearly free to render.
- **Cycle detection** before adding an instance. The parameter engine already has the pattern; this is
  the geometric analogue and must be enforced at edit time, not discovered at render time.
- **Push-in / pop-out** navigation reusing the schematic's nav-frame model, including editing a
  sub-cell in place and the hierarchy-save behaviour (`HierarchySaveTests` pattern).
- **Flatten** (one level / all levels) and **Group into new cell** — the two operations users reach for
  constantly. Group-into-cell must create a proper cell folder via `CellFolder.CreateCellFolder`.
- **`CellUsageScanner` extension** so `.clay` cell references are counted on Remove Cell and rewritten
  on Rename Cell, exactly as `.csch` references are today. Easy to forget; breaks designs when omitted.

---

## 8. Interchange — GDSII, DXF, Gerber

**R15. All three formats go through one neutral in-memory model and one shared layer-mapping dialog.**
Format-specific code touches only bytes and records, never editor state.

| Format | Direction | Maps to | Principal risks |
|---|---|---|---|
| **GDSII** | Read + write | Near-identity. BOUNDARY→Polygon, PATH→Path, SREF/AREF→Instance/Array, TEXT→Label, UNITS→DBU. **All curved primitives auto-flatten on write** (§3.2 R9e) and **holes are keyholed on write** (§3.1a), both with a Messages note stating what was converted and how much. | Vendor dialects; 200-char structure-name limit; PATH end types 0/1/2/4 (type 4 has explicit extensions); no arcs, no colors, no layer names, **no holes**. Write from the public spec — never ingest GPL sources. ~1200–1800 lines total. |
| **DXF** | Write first-class; read a documented subset | LWPOLYLINE / POLYLINE / LINE / ARC / CIRCLE / SOLID / INSERT+BLOCK / TEXT. **Curves survive**: `Circle`→`CIRCLE`, `RoundedRect` and arc-bearing outlines→`LWPOLYLINE` with bulge factors, Béziers→`SPLINE` (or flattened, per an export option). **Layer colours round-trip exactly** (docs/sonnet-briefs/brief-dxf-layer-colors.md), the one fidelity claim this table doesn't make for GDSII or Gerber. Layers are *named*, so the name↔(layer,datatype) map is required. | Import is the hard direction: dozens of producers, SPLINE, HATCH, unit ambiguity when `$INSUNITS` is unset. Define the accepted subset explicitly and report everything skipped to Messages, per-entity. Bulge factors are the one DXF feature worth importing carefully — dropping them silently turns arcs into chords. |
| **Gerber RS-274X / X2** + **Excellon** | **Export only — implemented (Phase L4c, brief-L4c-gerber-export.md)** | One file per copper/mask/silk layer. Polygons → G36/G37 region fills; constant-width `Path` → circular-aperture D01 strokes; **arcs → G02/G03 circular interpolation** and `Circle` → a circular aperture flash, so curves stay curves; Béziers flatten; `Via` → Excellon drill hits + pad flashes. X2 attributes (`.FileFunction`) so the fab identifies layers automatically. | Gerber *import* is genuinely hard — aperture macros, arc interpolation modes, LPD/LPC polarity, and the "assemble a board from a folder of files" problem. Recommend deferring import entirely rather than shipping a half-version; a partial Gerber importer that silently loses a clearance region is worse than none. |

**DXF version support** (docs/sonnet-briefs/brief-dxf-version-support.md §1, revised by
docs/sonnet-briefs/brief-dxf-layer-colors.md §1.3/R-col-1) — **export supports three versions, chosen per
export, defaulting to R2018 (`AC1032`):**

| Version | Colour | Notes |
|---|---|---|
| **AC1015** (R2000) | Indexed only (group 62, nearest ACI match) | Widest compatibility; colour is approximate |
| **AC1018** (R2004) | Group 62 **and** group 420 (exact 24-bit RGB) | Full colour, near-universal reader support |
| **AC1032** (R2018) | Group 62 **and** group 420 — **identical colour capability to AC1018** | **Default** |

Every entity this exporter emits (`LWPOLYLINE` with bulge, `LINE`, `ARC`, `CIRCLE`, `ELLIPSE`, `SPLINE`,
`HATCH`, `TEXT`, `INSERT`, `BLOCK`) exists unchanged across all three — geometry fidelity is identical
regardless of which is chosen, exactly as the superseded version-support brief found. **The ONE thing that
changed between R2000 and the two newer versions is colour**: AC1018 added group 420 (true 24-bit RGB per
layer) and AC1032 carries the exact same capability, nothing more — so **defaulting to AC1032 is a product
decision (the newest header a modern reader is likeliest to expect), not a colour decision**, and dropping
the default to AC1018 later would be a one-line change with zero colour regression. **Layer colours
round-trip exactly** through group 420 on both AC1018 and AC1032 — a fidelity claim no other format in this
table can make. **R12 (`AC1009`) is still not built** — some legacy CAM/tooling reads only R12, but it has
no `LWPOLYLINE`, `ELLIPSE`, `SPLINE`, or `HATCH`, so it would mean heavy `POLYLINE` output, flattened
splines/ellipses, and lost hole fills; not built speculatively, only if a user is actually blocked on it —
unaffected by anything colour-related.

**Layer colour is written as `ByLayer`, never per-entity** — every entity omits both 62 and 420 entirely,
so a viewer's own layer-colour override always works; only the LAYER table record itself carries colour.
A 256-entry AutoCAD Color Index (ACI) palette (fixed, standard data — never approximated by a formula) is
the one place both directions of the 62↔RGB conversion happen.

**Import reads the file's own `LAYER` table** (name, indexed colour, true colour when present, frozen/off
flags) — previously unread entirely, so an imported layer could only ever get a generated colour. A layer
absent from the destination technology now opens the shared layer-mapping dialog with **"Add to
technology" pre-selected and pre-filled with the DXF's own name and colour** — a deliberate divergence from
this same dialog's default for cross-technology paste (which stays "Keep as unknown," the safe default
when nothing about the source is a deliberate authoring choice): a DXF's layer names and colours ARE the
author's deliberate intent, so the common case becomes one click. **Colour index 7 means "black or white,
depending on background," never a literal colour** — a layer reporting ACI 7 (including one absent from
the table entirely, or a file this exporter itself wrote before this capability existed) falls back to the
same generated palette an undefined layer already uses, never a naive black.

**Import is version-tolerant across the whole DXF family by construction, not by an explicit version
gate** — the reader dispatches purely on the group codes it understands and reports what it does not,
which works unchanged from R12 through R2018+ (verified directly against real files from an independent
tool, R12/R2000/R2018, per L4b's own gate-12 principle — see `src/Ui/CLAUDE.md`). **No file is ever
refused for its version.**

**Encoding is genuinely version-dependent, and the ONE thing that is not just "read what's understood."**
R2007 (`AC1021`) and later are UTF-8; R2006 and earlier use the drawing's own code page, named in
`$DWGCODEPAGE`, with `\U+XXXX` escapes (AutoCAD's own convention) for any character outside it. Import
sniffs `$ACADVER`/`$DWGCODEPAGE` from the HEADER section first (both are always plain ASCII, so this is
safe regardless of the rest of the file's real encoding), decodes accordingly, and reports which it used
and any fallback applied. Export (R2000, so the code-page path) emits ASCII where possible and `\U+XXXX`
escapes otherwise, reporting when any escaping occurred — never a raw non-ASCII byte that only round-trips
for a reader sharing the exact same code page.

**Out of scope, stated as a documented limitation, not an internal implementation note:** DWG (a different,
proprietary format), binary DXF (ASCII only — detected and refused clearly), dimensions, leaders, xrefs,
paper-space layouts, and 3D entities. None of these block a 2D interchange round-trip; all are reported
by type with a count on import rather than silently dropped.

**Curve, hole, and colour fidelity across the three.** DXF is the only format that carries every curve
type; Gerber carries arcs and circles but not Béziers; GDSII carries none. DXF and Gerber both express
holes; GDSII keyholes them. **Layer colour is DXF-only, and only on two of its three write versions** —
GDSII has no colour concept at all (structures are purely numeric `(layer, datatype)`), Gerber's per-file
X2 attributes identify a layer's *function*, not a display colour, and DXF's own AC1015 (R2000) write
option is colour-*approximate* (nearest ACI index) rather than exact. **No format carries bitmaps** —
reference images are skipped by all three (§3.1b R10e) with a count reported, since they are aids to
drawing rather than things to manufacture. That ordering is worth surfacing in the export dialog as a
one-line note per format (curves/holes for all three; colour fidelity specifically for DXF's version
choice), so a user exporting a spiral inductor or a via-pierced pour learns *before* the fact what will
change, and by how much. Nothing here is a defect — it is what the formats are — but silently different
output from the same design is how trust is lost.

Cross-cutting import rules:
- Source units map into DBU; if the source resolution is **finer** than the target DBU, warn and name
  the count of coordinates that will round.
- Imported layers not in the tech file are auto-created with generated names and a distinct palette,
  and reported. **For DXF specifically** (docs/sonnet-briefs/brief-dxf-layer-colors.md R-col-4), the
  shared layer-mapping dialog's default for an unmatched row is "Add to technology," pre-filled with the
  DXF's own name and colour — not "Keep as unknown," which remains the default for cross-technology
  *paste* (where nothing about the source is a deliberate authoring choice to preserve). A user can still
  override any row before accepting.
- Import always creates cells through the normal cell-folder machinery — an imported GDSII library
  becomes real circuitRF cells, not an opaque blob.

---

## 9. Schematic-to-layout

The command walks the schematic's instances, resolves each cell's layout view, and emits a starting
layout.

1. Reuse `NetExtractor` to get instances and nets — do not re-walk the schematic independently.
2. For each component instance, resolve `ViewType.Layout` primacy. Components with no layout view
   (VAR, MEAS, Ground, and any un-laid-out cell) are reported to Messages and skipped; optionally place
   a labelled placeholder outline so the omission is visible rather than silent.

   **A parametric cell resolves too — its layout is generated rather than stored.** See
   `pcell-contract.md`, whose R1 makes a PCell an ordinary cell in every respect except that
   `ResolvePrimary(…, ViewType.Layout)` answers "generated". The pins that step 4 stamps nets onto are
   part of that contract's output (its R3), not something this step infers from geometry.
3. Place instances in a packed, non-overlapping arrangement — rows by bounding box, roughly following
   schematic order. **No auto-routing. Say so loudly.** Auto-routing is a separate multi-month product,
   and pretending otherwise sets a bad expectation.
4. Carry net names onto instance pins, and draw a **ratsnest**: thin straight lines on a system layer
   between pins sharing a net. This is the guide that makes manual routing tractable.
5. **R16. Re-running must update, not duplicate.** Each generated instance stores the originating
   schematic component's stable `Id`. A second run reconciles: keep and leave in place anything already
   present, add what's new, and report (never auto-delete) what no longer exists in the schematic.
   Users hand-place after the first generation and will run it again after a schematic edit; a
   generator that stomps their placement is one they use exactly once.

---

## 9A. Design rule checking (DRC)

**Decided: DRC is in scope.** Start with minimum width and minimum spacing — not because those two
checks are sufficient, but because they are enough to force every hard architectural question to be
answered, and a framework that answers them will absorb the rest cheaply.

### 9A.1 What the first two rules force you to solve

Minimum width and minimum spacing look trivial and are not. Building them correctly requires:

- **A rule model in `.ctech`** — per-layer, per-rule, with a name, a value in DBU, and a severity.
- **A geometry engine** — both checks are Clipper2 operations. Spacing: offset every shape on a layer
  outward by half the rule and look for intersections between shapes on **different nets** (§3.4 R10a
  is what makes this correct rather than a flood of false positives on a single copper pour). Width:
  offset inward by half the rule and look for regions that vanish.
- **A violation model** — rule, severity, the offending shapes, and a **geometric marker** (the region
  that actually violates), not just a point. A DRC that says "spacing violation somewhere on M1" is
  not usable.
- **A results surface** — a violations panel listing each hit, click-to-zoom, with markers drawn on a
  system layer over the geometry. This is the same superimposed-system-layer mechanism the mesh viewer
  uses (§10.5), so it is built once and used twice.
- **A waiver mechanism** — real designs have deliberate violations. Waiving must be per-violation,
  persisted, and visible, or people stop running DRC entirely.
- **A hierarchy answer** — does the check run flat or per-cell? Flat is correct and slow; per-cell with
  boundary checking is fast and subtle. **v1 runs flat** on the elaborated geometry, with a cell-count
  guard, and says so.

Every one of those is reusable. Adding minimum-area, minimum-enclosure, via-overlap, or antenna rules
afterwards is then a rule definition plus one Clipper2 recipe each — which is exactly why starting with
two rules and a real framework beats starting with twenty rules and no framework.

### 9A.2 Rules for the rules

**R16b. DRC never blocks editing.** It runs on demand (and optionally on export), reports, and gets out
of the way. No live-as-you-type checking in v1 — that is a performance project of its own, and an
editor that fights the user while they work is worse than one that never checks.

**R16c. Curved shapes are flattened for checking**, through the same `ToClipperPaths` helper as
everything else (§6.1), at the export tolerance rather than the screen tolerance — so DRC checks what
ships, not what is drawn.

**R16d. Export offers to run DRC first.** Not mandatory, not silent: a checkbox in the export dialog,
default on, with violations shown before the file is written. Catching a spacing error before it reaches
a fab is most of DRC's value.

### 9A.3 Toward LVS

The connectivity extraction described in §3.4 — union touching shapes per layer, bridge through vias,
assign net identity — is the same kind of Clipper2 walk as spacing, over the same violation-and-marker
plumbing. Building DRC first therefore buys most of the infrastructure LVS would need, and turns the
`Net` attribute from something the editor *asserts* into something the tool can *verify*. That is the
order to do these in.

### 9A.4 Where rules come from, and what happens with more than one process — **SHIPPED (L5b, 2026-08-06)**

**A rule set belongs to a TECHNOLOGY, never to a layout and never to a kit.** `Technology.DrcRules`
in the `.ctech` is the one place rules live, and it has been since L0a. Everything below follows from
that one fact, and it is why the questions people ask about multi-process workspaces have short
answers.

**Where the rules in a `.ctech` come from — three sources, one precedence order:**

1. **The process's own rule DECK**, read at import (§9A.4a). This wins wherever it states a rule: a
   deck is the document a fab actually signs off against.
2. **The process's stack description**, which carries a minimum width and spacing alongside each
   conductor's material properties. A summary written for an electrical model, not the manufacturing
   rule — so it fills in only where the deck states nothing (or where no deck was found).
3. **Hand-authored**, in the Technology Editor's DRC Rules tab. This is how a technology with neither
   of the above gets rules, and how anyone corrects or extends what was imported.

**There is exactly ONE editing surface, and a kit's rules are not special.** Importing a process
produces an ordinary `.ctech` in the workspace's `tech/` folder. It is edited in the same Technology
Editor as a starter technology, with the same undo, the same validation, the same save. There is no
second, kit-flavoured DRC UI to build or maintain, because there is no second kind of rule.

**Two processes in one workspace: no conflict, by construction.** Two imports produce two `.ctech`
files. A layout resolves exactly ONE technology — its own `TechRef` if it declares one, otherwise the
workspace default (see `TechnologyResolver`) — and a check runs against that one. Two layouts in one
workspace, drawn against different processes, each check against their own rules; nothing merges,
nothing arbitrates, and there is no "which rule set wins" question to answer.

**The one failure mode this shape genuinely has, and what is done about it.** A layout that declares
no `TechRef` of its own falls back to the workspace default — which, in a workspace holding two
processes, may not be the one the designer has in mind. **A clean result checked against the wrong
process's rules is indistinguishable from a clean result checked against the right one.** So every
DRC surface NAMES the technology it checked against: the violations panel's header, the Messages
summary, and the pre-export prompt all state it. That is the whole mitigation and it costs nothing.
Do not remove it on the grounds that it is redundant with the panel's own contents; it is not.

**A cross-technology sub-cell is left out and said so, never checked against the wrong rules.** A
sub-cell drawn against a different technology needs a layer mapping confirmed before its geometry can
be placed on this design's layers. Running a CHECK must not be the thing that asks the user for that
decision, so such a sub-cell contributes no geometry and is named in the run's diagnostics.

#### 9A.4a Reading a process's rule deck

A rule deck is a **program** — variables, arrays, loops, conditionals, and a large vocabulary of
geometric operations (enclosure, separation with projection limits, angle and area filters, antenna
ratios, density windows). Interpreting one in general is a language project.

**A half-interpreted deck is worse than none**: a rule silently mapped onto the wrong layer, or a
conditional exclusion quietly dropped, produces a check that passes a design it should have failed.
So `RuleDeckReader` reads exactly the two rule shapes circuitRF can express — minimum width and
minimum spacing on one drawn layer — and **counts and reports everything else by operation name**.
The report is the point. A user who imports a process stating 300 rules and gets 20 needs to see that
number at import, not discover it by trusting a checker that only ever looked at a fourteenth of the
deck. Adding a rule kind later is a `DrcRuleKind` value, one Clipper2 recipe, and one line in the
reader's grammar — the reported "cannot check yet" count is what tells you which one to add first.

Three properties worth stating, because each was arrived at by a measurement rather than a preference:

- **Recognised by GRAMMAR, never by extension, folder or tool name** — the same rule the stack and
  layer-table readers already follow.
- **Deck layers are matched to circuitRF layers by STREAM NUMBER, not by name.** A deck names its
  layers in its own vocabulary and a layer table names them in the process's; the only thing both
  agree on is the (layer, datatype) pair the geometry is actually drawn with.
- **A rule on a DERIVED layer expression** (one layer minus another, a size- or angle-filtered subset)
  is reported, not mapped onto its base layer — mapping it would widen the rule silently.

---

## 10. The 2.5D MoM simulator

### 10.1 What is being proposed, precisely

2.5D MoM solves the mixed-potential integral equation over conductors embedded in a **laterally infinite,
vertically stratified** medium. Metal is horizontal and thin; current flows in-plane, plus z-directed
current through vias. This is the planar-EM class of tool. It is *not* FEM and not
general 3D — a wirebond arcing through air is out of scope **for kernels A, B and C**.

> **Amended 2026-08-04.** That last clause originally read "out of scope by construction," which was
> too strong as a statement about MoM. It is true of the *kernels described here* and false of the
> method. A wirebond is **3D geometry in a stratified medium — the medium is still 2.5D**, which is
> why commercial 2.5D planar solvers were extended to bondwires. **Kernel W** (§10.11) covers it
> and does not disturb anything below.

The pieces, in dependency order:

1. Substrate stackup model + editor (§10.4)
2. Mesher with edge refinement + mesh viewer (§10.5)
3. **Layered-medium Green's function** (§10.3) ← the hard part
4. Matrix fill with singular-integral handling
5. Dense complex solve per frequency
6. Port excitation and de-embedding (§10.6)
7. Results → `DataSet` → Data Display / Touchstone (§10.8)

### 10.2 The honest cost

Items 1, 2, 5, 6, 7 are ordinary engineering — weeks each, well-bounded, independently testable.

**Item 3 is a research-grade numerics problem.** The spatial-domain Green's function for a layered medium
requires inverting the spectral-domain form through a Sommerfeld integral, which is oscillatory,
slowly-converging, and has branch points and surface-wave poles. The standard production answer is
**DCIM** (Discrete Complex Image Method): approximate the spectral Green's function as a sum of complex
exponentials via GPOF/matrix-pencil, each of which inverts in closed form by the Sommerfeld identity.
DCIM is implementable and well-documented in the literature, but it is fiddly, numerically delicate,
and it is where a schedule goes to die. Item 4 (singular self- and near-term integrals) is the second
such place.

Plan for this honestly rather than discovering it in month four.

> **Measured at L8a (2026-08-05).** Item 3 now has a number rather than a warning. The layered
> Green's function for a grounded slab is built and validated against direct Sommerfeld integration
> — a second, independent formulation — over ρ/λ ∈ [1e-4, 10] on both starter substrates at 2, 10
> and 20 GHz. **Error as a fraction of the free-space kernel at the same ρ, which is what a matrix
> fill experiences, is ≤ 6e-3 across that entire span; strict relative error is ≤ 1e-2 out to
> ρ/λ ≈ 1** and degrades beyond, which is where `Dcim.WithinValidatedRange` refuses. Details, the
> full curve and the two occasions an *oracle* rather than the method turned out to be wrong are in
> `src/Engine/Mom/CLAUDE.md` §L8a. Item 4 — the singular self- and near-terms — is untouched and is
> still the second place a schedule goes to die; it is L8c.
>
> **Measured at L8c (2026-08-05). Item 4 now has a number as well, and it is not the one that was
> feared.** The singular self- and near-term integrals turned out NOT to be where the difficulty
> lives, because a rectangular mesh with source and observer in one plane makes the INNER integral
> closed form — six of them, derived and checked against adaptive quadrature to 1e-12. The classic
> "nearly touching cells" problem comes from doing both integrals numerically; here only the outer one
> is, and it sees a continuous function with a kink, which is a quadrature-ORDER question. Against the
> εᵣ = 1 reduction, where the kernel is exact and only the quadrature can be wrong, the assembled
> matrix is right to **5.0e-6**; against direct Sommerfeld integration with the real DCIM kernel it is
> **5.4e-3**, i.e. item 3's own error and not item 4's.
>
> **Where the schedule actually went was a different place, and it is worth naming.** DCIM's fitted
> complex images are only "smooth" while none of them sits closer to the metal plane than a cell is
> wide — and on the FR-4 starter above ~5 GHz several do (min|b|/cell = 0.165 at 10 GHz, 0.079 at
> 20 GHz). The extraction's smooth remainder therefore is not smooth on the mesh's own scale, and a
> quadrature rule that is ample for free space was 5% wrong while converging gently enough to look
> converged at every step. Details in `src/Engine/Mom/CLAUDE.md` §L8c.
>
> Two structural notes for whoever picks up L8b–L8e. **L8 is split into five slices** (L8a the
> Green's function, L8b the mesher and viewer, L8c basis functions and the fill, L8d ports and
> de-embedding, L8e results and the kernel registry), on the same staging principle every phase in
> this area has used. And **the v1 kernel supports exactly one conductor layer, on the top surface
> of the slab** — so source and observer are always at the same height and the Green's function is a
> function of ρ alone. That is enough for all three of L8's own gates; multiple metal levels,
> z-directed current and vias are L9's, and are refused by name until then.

### 10.3 The v1 kernel — 2D quasi-static per-unit-length (decided)

**Decided: A → B → C.** v1 solves the *cross-section* of a uniform transmission-line structure and
produces per-unit-length RLGC, from which everything else follows. Full-wave single-dielectric (B) and
the general layered stack (C) replace the kernel later behind a fixed interface.

#### 10.3.1 The formulation

**Unknowns.** Charge density on boundary segments: **free** charge on conductor perimeters, **bound**
polarization charge on dielectric interfaces.

**Kernel.** With bound charge carried explicitly, the Green's function stays the *free-space* 2D
logarithmic potential `−ln(r)/2πε₀`. This is the single most valuable property of the choice: **no
Sommerfeld integrals, no DCIM, no special functions — and it handles an arbitrary number of dielectrics
immediately**, which satisfies the "multiple dielectrics with different properties" requirement in v1
rather than deferring it. A ground plane is handled exactly by a single image; a conductor-backed stack
needs nothing more.

**Equations.** Constant potential on each conductor (1 V on the excited conductor, 0 V on the rest and
on ground); normal-D continuity across each dielectric interface. Assemble, solve the real dense system
once per excited conductor.

**Known approximation.** Dielectric interfaces are laterally infinite and must be truncated. Truncate
several substrate heights beyond the outermost conductor with a graded tail, and make the truncation
distance a visible (auto-defaulted) setting. This is the one place A can be quietly wrong, so it gets an
explicit convergence test: extending the truncation must not move Z₀ by more than the oracle tolerance.

#### 10.3.2 From charge to s-parameters

1. Solve with the real stackup → **[C]**. Solve again with every dielectric replaced by air → **[C₀]**.
2. **εeff = C/C₀** (per-mode for multiconductor).
3. **[L] = µ₀ε₀[C₀]⁻¹** — the standard TEM identity. No second solve type needed.
4. **[G]** — *superseded at L7, 2026-08-04.* This item originally said "ω·tanδ-weighted partial
   capacitances, accumulated during the [C] fill". There is a cheaper and exactly-correct route: carry
   `ε* = ε_r(1 − j·tanδ)` through the whole system, which makes the interface coefficient K complex and
   [C] come out complex, `C = C′ − jC″`. Then `Y = jω·C_complex = ωC″ + jωC′` exactly, i.e.
   `G = −ω·Im(C)` and `C = Re(C)`. It costs **one** complex solve on a matrix of a few hundred, handles
   any number of independently lossy dielectrics, and `G ∝ ω` for constant tanδ falls out rather than
   being asserted. **Do not implement a separate partial-capacitance accumulation.**
5. **[R]** from **Wheeler's incremental inductance rule**: recede each conductor surface, recompute L,
   and `R(ω) = (R_s(ω)/µ₀)·∂L/∂n` with `R_s = √(ωµ₀/2σ) = 1/(σδ)`. It reuses the same solver with a
   perturbed geometry, so conductor loss costs one extra fill rather than a new formulation, and
   R ∝ √f falls out of R_s.
   *Two corrections made at L7:* (a) this item previously read `R = (ω/2)·∂L/∂n·(2/δ)`, which is short
   by a factor δ² — `R_s/µ₀` is `ωδ/2`, not `ω/δ`; (b) the recession must **not** be half a skin depth,
   because that makes it frequency-dependent and forces a matrix refill per frequency, destroying the
   frequency-independence below for no accuracy gain. ∂L/∂n is a purely *geometric* derivative,
   evaluated once. The recession must be summed over **every** lossy surface — the signal conductors
   *and* the ground plane; omitting the ground-plane term is the common error and it under-reports
   microstrip loss noticeably.
6. **γ = √((R+jωL)(G+jωC))**, **Z_c = √((R+jωL)/(G+jωC))** → ABCD of a length-ℓ uniform line → S,
   renormalized to the port reference impedances. Multiconductor goes through modal decomposition to
   generalized coupled-line s-parameters.

**The property that makes this feel fast.** [C], [C₀] and ∂L/∂n are **frequency-independent**. A 1001-
point sweep therefore costs *one* matrix solve plus 1001 closed-form evaluations — effectively
instantaneous. Full-wave (B/C) must refill and refactor per frequency. For a tool whose stated goal is
"lightweight and snappy", v1 will be dramatically snappier than the thing that eventually replaces it,
and that is worth saying to the user in the UI rather than hiding.

Optional cheap upgrade once the oracle passes: a **Kirschning–Jansen dispersion correction** so εeff(f)
and Z₀(f) track the known dispersion of microstrip. It is a closed-form formula, not a solver, and it
meaningfully extends the useful frequency range.

#### 10.3.3 Getting a cross-section out of a 2D layout

v1 solves a cross-section, but the user draws a layout. The bridge:

**R16a. Cross-section extraction is automatic and its result is shown back to the user.** When an EM
setup is run, analyse the selected geometry: if it reduces to straight, mutually parallel, constant-
width conductors on mapped stackup layers, extract the cross-section and the propagation length, and
display what was found — *"uniform 2-conductor cross-section · W = 2.9 mm · gap — · ℓ = 20 mm"*.

**If it does not reduce**, refuse **clearly and specifically**: *"This geometry has a bend at (x, y);
the quasi-static solver handles uniform cross-sections only. Full-wave analysis of discontinuities
arrives in L8."* A vague failure here is what would make v1 feel broken rather than bounded. A manual
**cut-line tool** is the escape hatch for a structure the auto-detector does not recognise.

This keeps the entire user-facing story identical to what B and C will offer — draw geometry, place
ports, sweep, plot — so nothing about the workflow has to be relearned when the kernel is swapped.

#### 10.3.4 The kernel interface, so A is not throwaway

Everything except the kernel is shared across A, B and C: the `.ctech` stackup model and editor, the
port model and placement UX, the frequency-sweep UI, the mesh viewer, the results plumbing, the
validation harness, and the edge-grading logic (§10.5). Fix the boundary now:

```csharp
IEmKernel {
    string         Name         { get; }
    EmCapabilities Capabilities { get; }          // uniform-cross-section | planar | layered+vias
    EmSuitability  CanSolve(EmProblem problem);   // the ONLY place a refusal is worded
    EmMeshReport   Mesh(EmProblem, EmMeshSettings);               // for the viewer, pre-solve
    DataSet        Solve(EmProblem, EmMeshSettings, double[] freqsHz, CancellationToken);
}
```

**Corrected at L7 (2026-08-04): the kernel consumes a neutral `EmProblem`, not `LayoutFragment` +
`Stackup`.** The original signature above named Ui types, and that is not simultaneously satisfiable
with §10.7's "all of it lives in `src/Engine/Mom/`": `LayoutFragment`, `Stackup` and `Technology`
live in `src/Ui/Layout/`, the reference graph is `Ui → Engine → Core → RfCore`, and inverting the
arrow would break the UI firewall that `tests/Firewall.Tests` enforces.

`EmProblem` (`src/Engine/Mom/EmProblem.cs`) is the neutral cross-section model — conductors as
finite-thickness polygons, dielectric regions as horizontal slabs, an optional ground plane, ports,
and the propagation length — **in SI units throughout, knowing nothing about DBU, `.clay` shapes,
layer tables or `LayerKey`.** The §10.3.3 cross-section extractor produces it, which is what
extraction already had to do. This is the better boundary anyway: it is the standing invariant *"the
numeric layer sees only fully-resolved values"* applied to geometry, and it is what lets the entire
kernel be validated against closed-form oracles without constructing a layout document.

`Capabilities` is what drives the §10.3.3 refusal message, so adding kernel B is a registration plus a
capability widening — not a rewrite of the calling code. **Kernel W (§10.11) is the first real test of
that claim**: it widens `EmCapabilities` with `Wires` (and later `Surfaces`) and registers, touching
nothing else. If W cannot be added this way, the interface is wrong and it is cheaper to learn that at
L7 than at L8.

`CanSolve` splits the refusal duty cleanly: the *geometric* refusals — bends, tapers, non-parallel
conductors — are detected by the Ui-side extractor before an `EmProblem` is ever built, and the
kernel words the ones it can see from the problem itself (a non-tiling region stack, a
zero-thickness conductor, a self-intersecting outline, a port naming an absent conductor, a port
with no resolvable reference, and — since L7b-b — more than `QuasiStaticKernel.MaxSignalConductors`
signal conductors). Both follow the same shape: name the specific feature, name where the capability
arrives. **Each phase has NARROWED those multiconductor refusals rather than deleting them**:
L7b accepted a symmetric coupled pair with its 2N ports; L7b-b's general modal decomposition
accepts an asymmetric pair and any N up to a conductor ceiling bounded by the dense
boundary-element solve, which is stated with its measured cost. Deleting a refusal instead of
narrowing it is how a kernel starts silently answering questions it cannot answer.

**No kernel registry exists yet, deliberately.** One kernel, constructed directly; a registry earns
its place when W or B exists.

#### 10.3.5 What v1 explicitly cannot do

Say this in the docs and in the UI, not just here: no discontinuities, bends, stubs, spirals, or
radiation; no coupling between non-parallel conductors; no full-wave dispersion beyond the optional
closed-form correction; no resonance. What it *does* do — uniform single and coupled lines, on
arbitrary multi-dielectric stacks, with real conductor and dielectric loss, swept instantly — is a
genuinely useful instrument and a complete end-to-end proof of every other part of the system.

### 10.4 Substrate stackup editor

An ordered stack from top to bottom, living in the `.ctech` file:

- **Boundary conditions** top and bottom: open (free space), or perfect/lossy ground plane.
- **Dielectric layer**: thickness, εr, tanδ, µr.
- **Conductor layer**: thickness, σ, optional surface roughness; bound to one or more drawing layers.
- **Via layer**: connects two conductor layers; bound to a drawing layer.

UI: a vertical stack diagram, click a band to edit, with **presets** — "FR-4 2-layer 1.6 mm",
"Rogers 4350B 0.508 mm", "GaAs MMIC 100 µm" — because preset-then-tweak is what makes the 30-second
target reachable. Linear isotropic only, as specified; the model should nonetheless leave room for
anisotropic εr later (Rogers laminates are anisotropic in reality).

### 10.5 Meshing, including the edge mesh

Your instinct about edge current is exactly right: on an RF conductor, current density has a
1/√d singularity at the edge, and a uniform mesh badly under-resolves it — which shows up directly as
wrong loss and wrong Z₀.

**v1 meshes one dimension, not two.** The quasi-static kernel discretizes *boundaries* in the
cross-section: segments along each conductor perimeter and along each dielectric interface. This is much
simpler than a surface mesh, and — importantly — the edge-crowding physics is identical, so the edge-
grading logic written here is the same logic B and C will use. A few hundred segments is a full mesh.

**Surface bases arrive with B.** Two families, decided then rather than now:
- **Rooftop on a rectangular grid** — simple, fast, ideal for Manhattan geometry, staircases diagonals.
- **RWG on triangles** — handles arbitrary geometry, needs a robust constrained Delaunay triangulator.

Leaning rectangular rooftop for B, with triangles added for spirals and tapers: Sonnet has demonstrated
for decades that a rectangular mesher is a production choice, not a toy. Left open until L8.

> **Decided and measured at L8b (2026-08-05): rectangular rooftop on a TENSOR-PRODUCT grid, with
> diagonals and curves STAIRCASED.** Triangles are not built — RWG needs a robust constrained Delaunay
> triangulator, which is a real commitment and is not earned by a slice whose job is to produce a
> number. The staircasing error was measured on the shipping PCells rather than on a synthetic
> diagonal, because those are what a user actually selects and because **this phase's own gate is not
> all-Manhattan** (`MBendPCell` cuts a 45° mitre, and mitred vs. unmitred is exactly the distinction
> the bend gate asks the kernel to make).
>
> **The mitre survives staircasing** — at the auto cell size the cut is reproduced to 2.8% of its area
> and removes 18 cells, so a mitred bend and a square one give different meshes and different unknown
> counts. **The smooth tapers are the sharper result**: local WIDTH error along an `MTaper`/`MKlopf`
> outline is 17–24% at worst and 5.5–11% RMS, while the global AREA error is only ~0.5% — and a
> Klopfenstein taper's whole value is a controlled equiripple |Γ| (0.05 by default), against which the
> local number is the one that matters. Two consequences, both recorded in `src/Engine/Mom/CLAUDE.md`
> §L8b: a smooth taper is a case for the shipped analytic model rather than for full-wave (the N
> report says so, by name), and if a future phase needs a taper's own full-wave answer accurately,
> **conformal/diagonal boundary cells — one straight cut through an otherwise rectangular cell — are
> the proportionate next step, not a triangulator.** The cell type and the report were deliberately
> shaped not to forbid that.
>
> **Sizing rules, as built.** λ_g is taken in the local dielectric at the sweep's HIGHEST frequency
> (εᵣ, i.e. the shortest wavelength the structure can see — conservative, and the only value available
> before a solve), and the mesh is computed **once per sweep**, not once per frequency. Cell size is
> per AXIS, from the narrowest conductor run measured along that axis; that is what keeps a long, thin
> taper affordable on a tensor grid. The unknown count is **basis functions, not cells** — a rooftop
> spans a pair of adjacent cells — and that is the number §10.7's ceiling refuses on.

> **Closed at L8c (2026-08-05): the edge reference length, measured against a converged physical
> quantity.** L8b could only count unknowns; the convergence half needed a solver. Measured on the
> static capacitance of the FR-4 hero above, refining each candidate along its own ladder: the shipped
> **conductor-width reference lands 0.18% from the two candidates' consensus limit at N = 552**, and
> the cell-size alternative 0.11% at N = 787. The mechanism is on the record too — the conductor-width
> edge cell does not shrink as cells/λ rises, so its flat refinement sequence means "already at its own
> limit" rather than "converging", and it sits ~0.35% low. That is inside any EM tolerance and is what
> keeps an ordinary GaAs line under R17, where the alternative measures N = 7,562. The default stands.

**Meshing rules, all auto-derived from the analysis so the user need not think.** (The wavelength rule
binds only from L8 onward; the quasi-static kernel has no wavelength dependence, so its `Auto` mesh is
driven purely by geometry and edge grading — one more reason v1 sweeps for free.)
- Maximum cell size ≤ λ_g/20 at the highest swept frequency, λ_g computed in the local dielectric.
- At least 3–5 cells across any conductor width.
- **Edge mesh**: 2–4 geometrically graded cells at every conductor edge, the outermost being a small
  fraction of the width (~2–5%) and growing by a ratio ~1.5–2 inward. Expose exactly three controls —
  `Auto` (default), `Cells per wavelength`, `Edge mesh on/off + cell count` — and nothing more.
- Report the resulting unknown count *before* solving, with a warning above the §10.7 budget.

**Mesh viewer.** A system layer superimposed on the geometry drawing cell boundaries, toggled from the
toolbar. It reuses the §5 renderer directly (a mesh is just more polygons on a special layer), and after
a solve the same layer renders a **current-density heat map**. This is high-value and cheap given the
existing rendering work — it is how the user develops trust that the mesh is sane, and it should land
*before* the solver, not after.

### 10.6 Ports and de-embedding

This is where EM simulators are won or lost, and where naive implementations produce plausible-looking
but wrong numbers.

- **Port types v1:** edge ports on a conductor boundary (the microstrip case) and, later, internal
  delta-gap ports.
- **Placement UX:** a Port tool that snaps to a conductor edge; click an edge, get P1. Auto-number,
  default 50 Ω, editable reference impedance.
- **De-embedding is mandatory, not optional.** A raw port excitation includes the port discontinuity;
  reporting those s-parameters as the structure's response is simply wrong. v1 approach: simulate a
  short and a longer uniform reference line of the port's cross-section, extract the port's own
  reflection and the line's propagation constant, and remove them — the standard two-line calibration.
  Show the de-embedding reference plane in the layout so its location is never a mystery.
- **…but de-embedding is a no-op for kernel A specifically, and that is a finding, not a shortcut
  (added at L7, 2026-08-04).** The paragraph above is about a *meshed* port excitation, which is what
  arrives with the full-wave kernel at L8. Kernel A never meshes a port: it computes γ and Z_c
  analytically from the per-unit-length RLGC and forms the Z-matrix of a uniform line of length ℓ
  directly, so the reference planes are exactly at the line ends **by construction** and there is no
  port discontinuity to remove. Building the two-line calibration now would be building a calibration
  for an error that does not exist; it becomes real work at L8. The observable consequence is pinned
  by a test: ∠S₂₁ is exactly −βℓ with no offset.
- **Ground reference** must be explicit: for microstrip it is the stackup's ground plane; for CPW it is
  the adjacent coplanar conductors. Get this wrong and everything downstream is wrong.

> **Decided and built at L8e (2026-08-05) — a port is a LABEL, not a new shape type.** The "Port tool
> that snaps to a conductor edge" above is real now, and what it places is an ordinary `LabelShape`
> with `IsPort` set. That flag already existed and already round-trips, so **the `.clay` schema did not
> change** and a layout carrying ports still round-trips byte-identically. Four consequences:
>
> - **Numbering** comes from the label's own text — `1`, `P1`, `p2`, `#3`, `Port 4` all parse. A label
>   that names no number is auto-numbered to the lowest free one rather than refused, and the Port tool
>   uses the same parser, so what the tool writes and what the extractor reads cannot drift.
> - **Two labels naming the same number is a refusal by name**, not a silent win for one of them.
> - **The side is INFERRED from geometry, reported in the notes, and refused when ambiguous.** A label
>   at the exact corner of a conductor is equally close to two edges; guessing reverses the direction of
>   current into the structure, which is a hard π in S₂₁ — smooth, plausible, and invisible in a
>   magnitude plot. So it is named and refused: *"Port 1 is ambiguous… Move the label."* Every resolved
>   port reports its inferred side and which way current flows in.
> - **The reference impedance lives in the `.cem`**, per port, never on the shape. A layout is geometry.
>
> **The de-embedding reference plane is not user-positionable, and that is a stated limitation.** It sits
> one mesh cell in from the drawn metal edge, because that is where L8d's calibration actually removes
> the port discontinuity; an adjustable plane would need a re-referencing step that does not exist. The
> planes are DRAWN over the layout (the bullet list's own requirement, "so its location is never a
> mystery") from the coordinates the *engine* reports, not from a Ui re-derivation of them.

> **Built and measured at L8d (2026-08-05).** The two-line calibration above is implemented, and three
> things about it are worth having in the design note rather than only in the engine's own file.
>
> **The bullet list's own emphasis was right about the wrong thing.** The calibration ALGEBRA is exact:
> a de-embedded uniform section comes out perfectly matched at the two lengths the calibration was
> solved from — |S₁₁| = 8.5e-16, four equations fixing four unknowns — and the two independent routes
> to γ (the two-line trace and a travelling-wave fit that shares no algebra with it) agree to
> **2.5e-4 … 3.9e-3** across 2–10 GHz. What limits accuracy is not the calibration but **direct
> radiative and surface-wave coupling between the two ports**, which decays only algebraically and has
> no term in a "box + matched line + box" model. Measured on 1.6 mm FR-4: a section that should be
> matched reads |S₁₁| = 3.9e-4 at 2 GHz and 6.0e-3 at 10 GHz — an f² scaling, and NOT monotone in the
> standard's length, which is how it was identified. **A de-embedded answer here is good to a few 1e-3
> at 2 GHz and a few 1e-2 at 10 GHz, and a longer feed does not improve it.** Real planar tools suppress
> this with box walls or absorbing boundaries; this kernel has neither, by design.
>
> **"Show the de-embedding reference plane in the layout" is now a trivial UI job**, because the plane
> is not a user choice: it is one cell in from the drawn metal end, fixed by construction, and there is
> deliberately no offset knob — offering one would offer a way to get a different answer for the same
> structure.
>
> **One thing the bullet list does not say and should: the de-embedded S is referenced to the LINE'S own
> Z_c, and the calibration cannot determine it.** That is a property of the method, not a gap. Z_c comes
> from `γ/(jωC_pul)` with C_pul differenced between the two standards so the end effects cancel exactly;
> kernel A is its ORACLE and never an input (measured: C_pul agrees to −0.26%, Z_c to +0.40% at 1 GHz).
> The assumption that C is frequency-independent is the route's real cost, and it is 0.4% / 2.3% / 6.3%
> at 1 / 5 / 20 GHz against kernel A's static value. Details in `src/Engine/Mom/CLAUDE.md` §L8d.

> ### **Requirement, added 2026-08-12 (owner report): THE SOLVER BUILDS ITS OWN CALIBRATION FEED.**
>
> **A user places a port on the part they drew and presses Simulate. Nothing above that is their job.**
> In particular, *the user must not have to add a uniform feed line to their artwork so that the
> de-embedding has something to calibrate against* — circuitRF works out what calibration structure it
> needs, and how much of it, on its own.
>
> **Why this had to become an explicit requirement.** L8d's calibration standard is an ISOLATED UNIFORM
> LINE of the port's cross-section, and its `a₁₁` is only the DUT's `a₁₁` if the DUT's own metal looks
> like that line for the distance the standard replaces. Drop a shipped `MKLOPF` PCell into a layout,
> port its two ends, and it does not: a taper's flanks are oblique from the first cell. That is not a
> mild inaccuracy, because D6's peel forms `(S_meas,ii − a₁₁)/a₂₁²` and `a₂₁ ∝ ω` — on 0.508 mm
> RO4350B at 1 GHz `a₂₁² = 9.8e-5`, so a **0.1% error in the error box is a 10× error in the answer**.
> The owner's 2000 mil, 50 → 12 Ω Klopfenstein taper came back as `|S₁₁| = 1.0000`, `|S₂₁| = 0.0008`
> and `Σ|S|² = 1.06` — a **non-passive open circuit**, shipped to a `.s2p` with nothing but a note.
>
> **What v1 does about it (`PlanarFeedExtension`, R-fed-1/R-fed-2).** Before meshing, each port's own
> polygon is extruded outward from its drawn end face by however much uniform line the calibration is
> short of. The lead is real metal to the fill; afterwards it is removed EXACTLY, as a matched section
> in the line's own `Z_c` using the γ the calibration already measured for that cross-section. Three
> properties are load-bearing and are gated:
>
> - **The reference plane is still the user's drawn metal edge.** The lead moves where the error box is
>   measured, never where the answer is reported — which is what keeps the paragraph above ("one mesh
>   cell in from the drawn metal edge") true rather than quietly false.
> - **A feed that is already uniform grows nothing**, and the problem reaches the mesher by reference,
>   so every number recorded in `src/Engine/Mom/HISTORY.md` stays reproducible bit for bit. Running out
>   of metal counts as uniform: a short line is a SHORT structure, not a flared one.
> - **Every case it cannot be sure of is declined, not guessed** — an end face that is not a single
>   straight segment, a port whose level is ambiguous, a lead that would run into other metal. A
>   decline is the pre-existing behaviour plus the pre-existing warning, which is the honest fallback;
>   moving metal the user drew would be a worse failure than the one being fixed.
>
> **And the answer is now checked before it ships.** σ_max(S) ≤ 1 was a gate in the test project only.
> A de-embedded sweep that publishes σ_max > 1 now says so, at the frequency and by how much
> (R-prt-15) — the excess is the analysis, never the design, and the user has to know that before
> reading the plot.
>
> **What this does NOT fix, and it is the reason `CheckFeedClearance` still exists:** a lead lengthens a
> feed, it cannot move a neighbour sideways. Metal running alongside the port inside the calibration's
> own run is still a limitation and is still warned about — and that warning was itself wrong until
> now (it scanned to the far end of the board, so it fired on every part wider than its port and could
> never be cleared).

### 10.7 Solver and the size budget

**v1 (quasi-static).** The matrix is **real, dense, and small** — a few hundred boundary segments, so
under a megabyte — and it is factored **once for the whole frequency sweep** (§10.3.2). Runtime is
milliseconds. There is no size budget to police at this stage; the constraint below is what arrives with
the full-wave kernel, and it is stated now so nothing in the architecture assumes solves are cheap
forever.

**L8 onward (full-wave).** The matrix is **dense and complex**: N unknowns → N² × 16 bytes.

| N | Matrix memory | Character |
|---|---|---|
| 500 | 4 MB | Instant. The microstrip hero lives here. |
| 2,000 | 64 MB | Interactive: seconds per frequency. |
| 5,000 | 400 MB | The practical ceiling for "lightweight". |
| 10,000 | 1.6 GB | Out of scope without ACA/MLFMM compression. |

**R17. Declare a hard N ceiling (~5000), surface the predicted N before solving, and refuse politely
above it** with a message pointing at mesh coarsening. A "lightweight" simulator that silently tries to
allocate 12 GB is not lightweight.

Sanity check on the hero: 50 Ω microstrip on 1.6 mm FR-4 is W ≈ 2.9 mm; a 20 mm line at 10 GHz has
λ_g ≈ 16.5 mm, so λ_g/20 ≈ 0.8 mm → ~24 cells long × ~6 across with edge refinement → **N of a few
hundred**. Genuinely fast. A spiral inductor lands at 1–3k. The scope is realistic.

Fill is O(N²) singular integrals; solve is O(N³) LU per frequency; both must be recomputed per frequency
because the Green's function is frequency-dependent. Since RF users sweep many points, **adaptive
frequency sampling** (solve sparsely, rational-interpolate, refine where the model disagrees) becomes
essential at L9 — it typically cuts solve count by 5–10× and is the best performance investment after
the mesh. Build it only when the kernel that needs it exists; v1 does not.

> **Measured at L8c (2026-08-05), on the fill and factorisation as built.** At 10 GHz on FR-4, per
> frequency, with the frequency-independent geometric core built once:
>
> | N | cells | kernel fit | core (once) | fill | LU | matrix | cached core | per freq | 101 points |
> |---|---|---|---|---|---|---|---|---|---|
> | **552** (the hero above) | 297 | 0.21 s | 2.87 s | 1.48 s | 0.04 s | 4.6 MB | 2.4 MB | **1.73 s** | **178 s** |
> | 1,956 | 1,012 | 0.20 s | 13.9 s | 6.80 s | 2.08 s | 58 MB | 30 MB | 9.08 s | 931 s |
> | 4,933 (≈ R17) | 2,520 | 0.20 s | 53.9 s | 21.8 s | 42.8 s | 371 MB | 188 MB | 64.8 s | 6,599 s |
>
> **Three corrections to the paragraph above, all in the same direction.** First, *"solve is O(N³) LU
> per frequency"* is true and is **not yet the constraint**: the O(N²) fill is 114× the LU at N = 552
> and is still 1.8× it at the ceiling, so the crossover has not been reached inside R17's own budget.
> Second, the hero's *"Instant"* is a statement about its 4 MB matrix, not about its sweep — a
> 101-point sweep of it is about **three minutes**. (§10.10's 30-second target is an *interaction*
> budget and is unaffected.) Third, the **400 MB line is optimistic by half a matrix**: reusing the
> frequency-independent core is worth 62% of a single-frequency solve at the hero size and 45% at the
> ceiling, but its cached arrays add **51% on top of the matrix** — 559 MB resident at N = 4,933.
>
> **So adaptive frequency sampling is no longer a "build it at L9" item**; the kernel that needs it now
> exists. The cheaper first move, though, is per-cell-pair moment caching in the vector block: adjacent
> rooftops share cells, so the same cell pair is currently integrated up to four times. See
> `src/Engine/Mom/CLAUDE.md` §L8c for both.
>
> **Measured again at L8d (2026-08-05), with ports and de-embedding on, and the multiplier is 4.4×.**
> The table above is the cost of filling and factoring the DUT alone. A de-embedded answer also solves
> the calibration standards at every frequency, and they are not small — on the same hero they measure
> N = 297 / 382 / 331 / 416 against the DUT's 552, i.e. **2.58× the DUT's own unknowns**:
>
> | per frequency, N = 552, FR-4 at 10 GHz | | 101 points |
> |---|---|---|
> | kernel fit (`Dcim.Fit`, shared across all meshes) | 0.20 s | 20 s |
> | the DUT (fill + factor + excite) | 1.47 s | 149 s |
> | **the calibration standards** | **5.98 s** | **604 s** |
> | **total** | **7.66 s** | **~780 s** + 10 s of cores |
>
> (The DUT column reproduces L8c's own 1.48 s fill to 1%, which is what says the two measurements are
> comparable. Both runs above were taken in isolation and repeat to 1.5%; the same test run alongside
> nine other benchmark tests reads more than twice as slow, so **measure this one alone or not at all**.)
>
> **The standards are 78% of the cost, so the first saving to take is not in the fill at all.** Two are
> identified and neither needs new numerics: (1) the two ports of a plain microstrip *should* share one
> calibration and do not, because L8b's edge grading is not exactly mirror-symmetric end to end —
> **making it symmetric is worth 2× here**; (2) a calibration is a first-class reusable object, so a UI
> that caches one per feed cross-section pays for it once across every DUT that shares it. Adaptive
> frequency sampling is worth correspondingly more than §10.7 assumed, because the per-point cost went
> up 4.4× while the number of points did not.

All of it lives in `src/Engine/Mom/`, uses NumFlat for the dense factorisation, and touches no UI.

### 10.8 Results and EM/circuit co-simulation

The solver returns s-parameters → a `DataSet` with an `S` cube → the existing Data Display plots it and
the existing Touchstone exporter writes it. **No new result type**, per the standing invariant.

**Decided: an EM run produces an `.snp` artifact.** This resolves the standing constraint *"Analyses
attach to a `TestBench`, never to a `Cell`"* — an EM setup naturally attaches to a **layout view**,
which is a cell view, and would otherwise violate it.

**R17a. An EM setup is its own document — a `.cem` — that REFERENCES a layout**, and **running it
writes an `.snp` file** plus returning a `DataSet`. The schematic consumes that artifact through the
**existing SnP component** — no new analysis kind, no change to the testbench model, no new result type.

> **Revised at L6/L7 (brief-L6-L7-em-ui.md D1).** This rule originally read *"an EM setup is a property
> of the layout, persisted in the `.clay`"*. The standalone document serves R17a's own stated purpose
> better: the standing invariant *"analyses attach to a `TestBench`, never to a `Cell`"* is satisfied
> more cleanly by a setup that is not embedded in a cell view at all, and it buys three things
> embedding does not — several EM setups against one layout, editing a setup without dirtying the
> `.clay`, and a setup that is independently diffable and versionable. A `.cem` is workspace-scoped and
> never scratch (mirroring `.ctech`), and it names its layout by workspace-relative path, **never by
> embedding geometry** — which is exactly why re-running after a layout edit picks the edit up.

The consequences are all good ones:
- **EM/circuit co-simulation for free.** Lay out a matching network, EM-simulate it, drop the resulting
  `.snp` into the harmonic-balance testbench next to the real device model. That workflow is what makes
  this feature valuable to a PA designer rather than a curiosity, and it needs no new machinery.
- **The artifact is inspectable and portable** — a Touchstone file the user can plot, archive, hand to
  a colleague, or diff against a measurement.
- **Re-running is a file update**, so the schematic picks up the new result the same way it picks up any
  changed SnP source.

> **Extended at L8e (2026-08-05) — still no new result type, and the diagnostics group is per KERNEL.**
> Whichever kernel runs, the `DataSet` has the same shape: `S`, per-port `Z0`, and **one** diagnostics
> group. Kernel A's is `"tline"` (Zc, Gamma, Eeff, AttenDbPerM, Rpul, Lpul, Gpul, Cpul); kernel B's is
> `"planar"` (Gamma, Zc, Eeff, AttenDbPerM, Cpul, CalElectricalDeg, DeembedResidual, DeembedRejected,
> CalibrationUsable). **They deliberately do not share a name.** A per-unit-length quantity from a 2-D
> quasi-static solve and one back-solved from a de-embedded full-wave S-matrix are different claims;
> they agree on a uniform line — that agreement is L8's phase gate — and they diverge with frequency,
> which is dispersion and is a *result*. One shared group would let a Data Display trace silently mix
> the two in any project that contains both kinds of run.
>
> The staleness stamp below now covers the **planar** problem too — geometry, mesh settings, and ports
> each hashed separately, so the warning says *which* of the three moved. Without that it would have
> gone on stamping the cross-section for a run that has no cross-section, and staleness detection would
> have quietly stopped working for kernel B while still appearing to be on.

Two details worth fixing now: write the `.snp` to a **predictable path** derived from the cell and setup
name (mirroring `RunResultsWriter`'s convention) so the schematic's reference is stable across runs; and
stamp the file's comment header with the stackup, mesh settings, port definitions, and a hash of the
geometry, so a stale `.snp` sitting next to an edited layout is **detectable** rather than silently
wrong. That staleness check is the one failure mode this design introduces, and a header stamp plus a
Messages warning on mismatch is the whole mitigation.

### 10.9 Validation oracles

Same philosophy as the MINT work: prefer self-consistency checks that need no external tool, plus a few
closed-form anchors.

**Closed-form anchors:** Hammerstad-Jensen microstrip Z₀ and εeff (±2% is a reasonable gate); coupled
microstrip even/odd-mode impedances; a quarter-wave open stub's resonant frequency; a known
Rogers-substrate line.

**Learned at L7 (2026-08-04): validate the charge solver against *exact* closed forms before comparing
anything to Hammerstad-Jensen.** H-J is an empirical fit, so a ±2% agreement against it can hide a real
defect and a disagreement tells you nothing about which of five stages is wrong. The ladder that
actually worked, each tier passing before the next was written: (0) the potential and field integrals
vs quadrature and vs a finite difference of each other; (1) coax `2πε₀/ln(b/a)`, wire-over-ground
`2πε₀/acosh(h/a)` — this is what tests the image ground — and two parallel wires; (2) two-layer coax
`2πε₀/[ln(r_m/a)/ε₁ + ln(b/r_m)/ε₂]`, which is the only cheap closed form that genuinely exercises a
dielectric interface, plus a fully-filled coax and a lossy fill for the complex-ε* path; then (3) H-J.
Two of these caught real defects that the ±2% H-J gate had passed.

**And H-J is not the arbiter where it is itself extrapolated.** Its finite-thickness correction widens
W, which *raises* εeff; a boundary-element solve of the real rectangle sees the strip's side faces in
air, so a thicker strip *lowers* εeff. At t/W ≈ 0.2 — ordinary 35 µm copper on a narrow strip — the two
disagree by ~5% and H-J is the one outside its regime. Gate against it at a thin strip across the full
W/h span, and against real metal only where t/W is small. Same lesson for `MicrostripLoss`'s
`α_c = R_s/(Z₀W)`: it is a wide-strip asymptote that over-counts the ground plane, and the right check
is that a proper Wheeler computation approaches it monotonically from below as W/h → ∞ (measured 0.40
at W/h = 0.3, 0.96 at W/h = 50), not a fixed tolerance band at one geometry.

**Oracle-free self-consistency, which catches most real bugs:**
- Reciprocity: S₁₂ = S₂₁ to solver tolerance.
- Passivity: eigenvalues of I − SᴴS ≥ 0.
- Losslessness: with σ = ∞ and tanδ = 0, |S₁₁|² + |S₂₁|² = 1.
- Mesh convergence: refine the mesh, results must converge monotonically rather than wander.
- A uniform line of length 2L must equal two cascaded lines of length L.
- Reference-plane invariance: moving the de-embedding plane must only rotate phase.

**Added at L8a (2026-08-05), because the full-wave kernel needs oracles kernel A did not.** The same
ladder discipline, one tier lower: (−1) the special functions themselves, against an *integral
representation* and the Wronskian, before anything uses them; (0) the spectral-domain function alone,
before any inverse transform exists — a spectral function that is wrong produces a spatial function
that is wrong in a way no downstream oracle can localise; (1) the exact reductions, of which
**εᵣ = 1 collapsing to free space plus one image** is the strongest and is the direct analogue of the
image gate that validated kernel A's R-mom-7; (2) the production method against **direct numerical
Sommerfeld integration**, which shares no approximation with it. Two of these caught real defects,
and in one case the defect was in the *oracle* — see `src/Engine/Mom/CLAUDE.md` §L8a. Note also that
**losslessness does not survive into kernel B**: an open planar structure radiates and launches
surface waves, so |S₁₁|² + |S₂₁|² < 1 legitimately. Reciprocity and passivity carry over.

**Regression golden data** reviewed and approved by the owner before it becomes a gate — the established
project pattern.

### 10.10 The 30-second target, as an acceptance test

**R18. "Draw a microstrip line and configure a MoM sim in under 30 seconds" is written as a scripted
acceptance test with a click/keystroke budget, and it gates the MoM phase.**

The path it measures:

| Step | Interaction | Budget |
|---|---|---|
| New layout from the workspace's starter template | 1 click — stackup, layers, units all preset from `.ctech` | 3 s |
| Draw the line | Path tool, click start, click end, type `W = 2.9mm` in the live dimension field | 8 s |
| Frequency | EM panel: `1` `20` `GHz`, 101 points | 8 s |
| Mesh | Untouched — `Auto` is the default and is correct | 0 s |
| Run | 1 click | 1 s |

Total ≈ 20 s.

> **The "Ports — Port tool, click each end, 5 s" row is gone (brief-L6-L7-em-ui.md D5).** For a uniform
> cross-section the two ports simply ARE the two ends of the extracted line, by construction — the same
> fact that makes de-embedding a no-op for kernel A (R-mom-15): there is nothing to place because there
> is no meshed port to place. The `.cem` carries per-port Z₀ and nothing else. A Port tool becomes real
> work at **L8**, when a meshed port exists, and `PinInference` is what it should be built on then
> rather than a new picking mode. The target got *easier*, not harder. **The chosen v1 kernel covers this case exactly** — a uniform microstrip line is precisely
what a quasi-static per-unit-length solver is for — so the headline acceptance test is satisfied by L7
rather than waiting on full-wave. The same test runs against the MMIC starter tech with a GaAs line, so
both markets are gated (§2.4).

What makes the target achievable is not speed of interaction but **defaults that are already right**: preset stackups, auto mesh, ports that need no placing at all (D5), 50 Ω, and numeric entry with unit suffixes
(R6). Design the defaults first and the target falls out; design the dialogs first and it never will.

### 10.11 Kernel W — 3D wirebond simulation

**Full design: [`mom-wirebond-kernel.md`](mom-wirebond-kernel.md).** Summarised here because it changes
§10's kernel inventory, `EmCapabilities`, and the phasing table, and because §10.1's original wording
excluded it.

**What it is.** A **thin-wire MoM kernel** for bond wires: ball- and wedge-bond loop profiles, 0.5–1.25
mil radii, 5–50 mil loop heights, 5–300 mil pitch, arrays to 200 wires, with full mutual coupling
between every wire. This is the founding problem of computational EM (Harrington/Richmond/NEC), and for
this geometry it beats FEM decisively: unknowns scale with **wire count, not with the volume of air
between the wires**, the 1 mil radius collapses into an analytic kernel rather than a meshing problem,
and the radiation condition is exact — no airbox, no PML. Where FEM still wins: inhomogeneous 3D
dielectrics, complex 3D metal (leadframes, clips, lids), cavity resonance, and field plots.

**It is a separate kernel, not an extension of A.** Kernel A is a 2D cross-section solver and cannot see
a wirebond. It is also **not unlocked by C** — a stepped ground is a *lateral* variation, precisely what
the 2.5D premise forbids, so DCIM buys nothing here. Kernel W registers against the §10.3.4 interface
and shares the `.ctech` stackup, the port model, the sweep UI, the mesh viewer, the results plumbing and
the validation harness.

**Staged the same way as A→B→C, for the same reason:**

| | Kernel | Property |
|---|---|---|
| **W1** | Quasi-static PEEC — partial inductance (Neumann), coefficients of potential, exact round-wire Bessel internal impedance | **Frequency-independent matrices — fill once, sweep free**, exactly as kernel A |
| **W2** | Retarded thin-wire MoM — add `e^{-jkR}` to the mutuals | Genuine full-wave; per-frequency refill. A flag on the same kernel, not a second kernel |
| **W3** | Wires in the layered stack | Needs DCIM. **Named, not promised** — downstream of C |

**Scope tiers.** **T1 = wires only** over an image ground plane: free-space kernel, one exact image, no
wire-to-surface junction, ports at the wire ends, its own `.snp` cascaded in the schematic with the
planar result. T1 ships and is useful standalone. **T2 = wires + meshed surfaces**, and this is the
scheduling insight worth carrying: **one piece of machinery (surface panels + wire-to-surface junction
basis functions) unlocks three separately-requested capabilities** — coupling to landing pads, a finite
overmold body, and discontinuous ground. None is obtainable without the junction; all three arrive
together. T2 is the larger half of the wirebond effort and must be budgeted as one deliverable, not as
three small additions to T1.

**Overmold.** The fact that shrinks it: **mold compound is non-magnetic**, so it touches [P] and [G] and
**never [Lp]**. Inductance — the dominant bondwire parasitic — is unaffected by the mold model; only
capacitance, delay and a small dielectric loss carry its error. Ship homogeneous fill as a mode and
**bound charge on the mold surfaces** as the real thing, which is the direct 3D analogue of §10.3.1's
already-chosen formulation and handles a finite cap, sidewalls, die attach and the die surface with the
free-space kernel. The accuracy floor is the EMC datasheet (εr 3.4–4.5, poorly characterised above a few
GHz), which is itself the argument for stopping there.

**Discontinuous ground.** A flat plane is free because it is an image, exact only for a laterally
infinite plane; a z-step kills the image, the dielectric image series **and** DCIM. The answer is to
mesh the ground as a conductor — cheap for [P] (charge panels at V = 0), the real work for [Lp] (surface
current cells, full Ruehli PEEC). Keep it affordable by hybridising: semi-infinite image plane for the
lower tier, meshed panels only for the raised structure, graded because return current spreads over
roughly ±2h. Ignoring a 20 mil step under half a span costs ~6% on L; ignoring a **ground gap** costs
30–50%+.

**Sizing.** ~25–30 segments per wire (set by arc fidelity, not wavelength): 8 wires ≈ 250 unknowns,
40 wires ≈ 1,200, 200 wires ≈ 6,000 → 576 MB dense, ~3 s LU per frequency. Only the 200-wire extreme
brushes R17's ceiling; meshed ground is what pushes it over, and that is where ACA first earns its keep.

**The PRD tension, resolved.** A wirebond is a **parametric component instance whose layout view is its
2D projection** plus an annotation — `.clay` gains no 3D shape type and no volumetric mesher is written,
so "layout is 2D" and "no volume meshing" survive untouched. The PRD's §2 non-goal was **narrowed on
2026-08-04 (PRD v1.3)** from "no 3D full-wave EM" to *no FEM, no volumetric meshing, no arbitrary 3D
geometry* — the old wording would have excluded a solver that requires none of those things.

**Two things this design insists on, because they are how bondwire models usually go wrong:**
- **Ports carry an explicit reference conductor.** Partial inductance is not a physical quantity on its
  own; "the inductance of this bond wire" is meaningless without a stated return path.
- **Ground bond wires are ordinary wires, not a boundary condition.** Modelling only the signal wires
  against an assumed perfect plane reports optimistically low inductance. Conversely, a user who
  declares downbonds explicitly gets much of the stepped-ground effect in **T1**, before any surface
  kernel exists.

---

## 11. Phasing

Each phase has a gate that must pass before the next begins.

| Phase | Content | Gate |
|---|---|---|
| **L0 — Foundations** | DBU/unit model; `LayerDef` + `.ctech` model, editor document, and both starter technologies; `tech/` folder + `.cws` default + project-tree node; `.clay` schema + round-trip; empty layout document + editor shell docked and tearing off like the symbol editor | A `.clay` round-trips byte-identically; unit switch mutates nothing; a layer color edit live-refreshes open layouts; both starter techs create a working empty layout |
| **L1 — Draw & edit** | Rect / RoundedRect / Circle / Polygon / Curve / Path / Label tools; the §3.2 edge-list model, flattener, and **Flatten to Polygon** command; net attribute + properties panel; selection with overlap cycling; vertex, edge, bulge and control-point editing; move; undo/redo; cut/copy/paste; grid + rulers + live dimension readout; Clipper2 booleans and offsets | Draw, reshape, and move every primitive; a circle flattens to a polygon within tolerance and the count is right; a boolean on a curve flattens and warns once; overlap cycling has a status readout; nets survive copy/paste and flatten; cross-cell paste with a DBU mismatch warns and rescales |
| **L2 — Performance** | R-tree, per-shape fills + batched strokes, the R8b merge tier, adaptive curve flattening per zoom tier, LOD, matrix pan/zoom, benchmark harness | §5.1 numbers met in CI at 1k / 50k / 500k shapes, measured on **both** the darkening and merged paths, with the R8b threshold set from that data |
| **L3 — Hierarchy** | Instances, arrays, push-in/pop-out, flatten, group-into-cell, cycle detection, `CellUsageScanner` extension | Rename/Remove Cell stays correct with `.clay` references; a 50×50 array renders inside budget |
| **L4 — Interchange** | GDSII read/write (auto-flatten curves), DXF write + subset read (curves preserved via bulge/`CIRCLE`/`SPLINE`), Gerber + Excellon write (arcs via G02/G03), layer-mapping dialog, per-format curve-fidelity note in the export dialog | GDSII round-trips a hierarchical design with arrays; a circle survives a DXF round-trip as a circle; a Gerber set opens correctly in an independent viewer with arcs intact |
| **L5 — Schematic→layout** | Instance placement, net stamping, net labels, ratsnest, idempotent re-run | Re-running after a schematic edit preserves hand placement and reports removals |
| **L5b — DRC v1** ✅ | Rule model in `.ctech`, min-width + min-spacing checks over Clipper2, violation model with geometric markers, violations panel on a system layer, waivers, run-on-export checkbox | **DONE (2026-08-06).** All four gate clauses met. Both rules fire on a seeded layout and stay silent on a clean one — including the two boundary cases that decide whether anyone runs the check twice: a trace drawn EXACTLY at the minimum width passes, and a gap exactly at the rule passes. Markers are regions, not points, and a spacing marker is centred on the GAP (the thing to widen) rather than on either conductor. Waivers persist in the `.clay`, survive save/reload/re-check, and stay listed-and-visible rather than disappearing. Spacing groups unnamed geometry by CONNECTIVITY, so a pour drawn as several overlapping rectangles is one conductor while two genuinely disjoint unnamed regions are still compared. **Plus, beyond the gate:** a process's own rule DECK is now readable at technology import (§9A.4), and every rule shape circuitRF cannot express is counted and reported rather than silently dropped |
| **L6 — Stackup + mesh** | Stackup editor with presets inside the `.ctech` editor; cross-section extraction (§10.3.3) + cut-line tool; 1D boundary mesher with edge grading; mesh viewer | Extract and mesh a microstrip cross-section, visually inspect edge refinement, segment count reported, non-uniform geometry refused with a specific message — **no solver yet** |
| **L7 — Quasi-static kernel (A)** | `IEmKernel`, charge solve, [C]/[C₀]/[L]/[G]/[R], RLGC → s-parameters, ports + de-embedding, frequency sweep, results → `DataSet` | Microstrip Z₀/εeff within 2% of Hammerstad-Jensen on **both** starter techs; truncation-convergence, reciprocity, passivity, losslessness, and mesh-convergence checks pass; **§10.10 acceptance test passes** |
| **L7b — Coupled lines + co-sim** ✅ | Multiconductor [L][C], even/odd modal decomposition, coupled-line s-parameters as a 4-port, `.snp` back-annotation into the schematic | **DONE (2026-08-05).** Ships the SYMMETRIC pair — a fixed modal matrix, no eigensolver. Exact off-diagonal oracle, far-apart pair reproducing two independent single lines, `Z_o < Z_e`, 4-port reciprocity/passivity/losslessness; an EM-derived `.s4p` drops into an HB testbench and runs end to end. Asymmetric pairs and N > 2 refused by name → **L7b-b** |
| **L7b-b — Asymmetric pairs + N > 2** ✅ | General modal decomposition of `[Z][Y]`; the non-Hermitian complex eigensolver it requires | **DONE (2026-08-05).** Route A only: the real symmetric generalized eigenproblem `Gevd(Re[C], [L]⁻¹)` for the lossless line, with loss carried perturbatively. **The error was MEASURED against an exact closed-form 2×2 modal oracle, not assumed** — worst 5e-4 in |S| on a realistic asymmetric pair, 1.7e-2 in a regime built to break it — so the complex QR eigensolver (Route B) was **not built**, per D2. A symmetric pair goes through the general path like everything else; L7b's fixed `[1 1; 1 −1]` construction survives as a test oracle only. Mode-axis `tline` cubes + `ModeCouplingResidual`; conductor ceiling with its measured cost |
| **L8 — Full-wave, single dielectric (B)** ✅ | Layered Green's function for a grounded slab, surface basis functions + 2D mesher, per-frequency fill/solve, current-density heat map. **SPLIT into L8a–L8e** (2026-08-05), for the same reason L6/L7 and L7b were staged: §10.2 flags this as the one schedule-uncertain phase, so its riskiest piece is isolated and measured first. **L8a** = the Green's function alone + its oracle ladder ([`brief-L8a-layered-greens-function.md`](../sonnet-briefs/brief-L8a-layered-greens-function.md)); **L8b** = 2D mesher + plan-view mesh overlay + the R17 pre-solve N report (§10.5: the viewer lands *before* the solver) ([`brief-L8b-planar-mesher-and-overlay.md`](../sonnet-briefs/brief-L8b-planar-mesher-and-overlay.md)); **L8c** = basis functions + matrix fill + the singular/near-singular integrals (§10.2's second schedule risk) ([`brief-L8c-fill-and-singular-integrals.md`](../sonnet-briefs/brief-L8c-fill-and-singular-integrals.md)); **L8d** = ports + the two-line de-embedding that is finally real work + per-frequency solve ([`brief-L8d-ports-and-de-embedding.md`](../sonnet-briefs/brief-L8d-ports-and-de-embedding.md)); **L8e** = results, current-density heat map, the kernel registry §10.3.4 defers to exactly this moment, narrowed refusals, and the phase gate ([`brief-L8e-results-registry-and-the-phase-gate.md`](../sonnet-briefs/brief-L8e-results-registry-and-the-phase-gate.md)) | **DONE (2026-08-05).** All three gate sentences pass on BOTH starters, at the shipping mesh, through the product path. **Stub:** notch +0.8% (FR-4) / +3.8% (GaAs) against the open-end-corrected prediction, and below the bare λ_g/4 on both — the open-end extension is real and measured. **Bend:** reciprocal to 1e-9, passive at every point, \|S₁₁\| rising with frequency as a shunt capacitance must, and the mitre lowers the reflection. **A vs B on a uniform line:** ε_eff agrees to 0.96% (FR-4, 1 GHz) / 3.15% (GaAs, 10 GHz), diverging with frequency because B has dispersion and A does not. **No losslessness check anywhere** — an open planar structure radiates. The gate cost ~8.5 min and is `Category=Benchmark`; what stays routine is the product-path wiring case. Two real defects were found BY the gate, not by inspection: an ambiguity threshold scaled to the bounding box rather than the line end, and a fixture that chamfered the wrong corner |
| **L9 — General layered stack (C)** | DCIM, N dielectrics, vias and z-directed current, adaptive frequency sampling, N-budget enforcement | Multi-layer structure with backside vias; **agreement with published reference structures — PROPOSED FOR STRIKING, awaiting the owner's ruling** (see below) |
| **LW1 — Wirebond T1 (kernel W)** | Parametric wirebond component (ball + wedge profiles) with a 2D-projection layout view; CSV wirebond-table import; 3D polyline mesher + mesh viewer; quasi-static PEEC ([Lp], [P], Bessel internal impedance) with a `Retarded` flag for W2; image ground; homogeneous + image-series overmold; explicit port reference conductors | Single wire over ground within 2% of closed-form L; 2-wire mutual within 2% of Neumann at 10 and 100 mil pitch; **8-wire GSGSG array within 5% of owner-generated 3D FEM data on all S-parameters to 20 GHz**; molded vs. bare shows the expected signature (L unchanged, C ≈ 4×); reciprocity/passivity/losslessness/segment-convergence pass; a 200-wire array reports N first and solves inside the R17 budget |
| **LW2 — Wirebond T2 (surfaces)** | Surface panels (free charge, bound charge, surface current); wire-to-surface junction basis functions; meshed stepped ground with image/mesh hybrid + graded mesh; finite mold body; coupling to planar metal | **Right-angle-corner 3-image oracle passes before any package geometry**; stepped-paddle case within 5% of the same 3D FEM reference; a ground-gap case reproduces the expected 30–50% inductance jump; a multi-tier ground with no declared connection is refused rather than silently shorted |

L0–L5 are sequential-ish, but L4 can run in parallel with L3 once the geometry model is frozen, L5b
needs only L1's Clipper2 work plus L5's nets, and L6 can start any time after L2. L7 is small — the
reason for choosing kernel A — so **L0…L7 is a shippable product**: a real layout editor with curves,
fab interchange, a working DRC, and an instant transmission-line solver. L8 is the long pole and the
first phase whose schedule is genuinely uncertain (§10.2); it should not start until L7's oracles are
green, because those oracles are what will tell you whether B is right.

**LW1/LW2 depend on L6 and L7 and on nothing in L8 or L9** — kernel W's free-space kernel carries no
DCIM risk. LW1 is therefore schedulable *before* L8, and the case for doing so is that it serves the
PA/module designer who is already this project's hero-circuit persona at a fraction of L8's schedule
uncertainty. **That priority call is left open** (§13). LW2 should not start until LW1's oracles are
green, for the same reason L8 waits on L7. LW3 (kernel W3 — wires in the layered stack) is downstream of
L9 and is named, not scheduled.

---

## 12. Risks

| Risk | Severity | Mitigation |
|---|---|---|
| Full-wave Green's function (L8) consumes the schedule | **High** | Deferred behind a shippable L0–L7 product; `IEmKernel` isolates it; L7's oracles exist before L8 starts so its correctness is measurable from day one |
| L7's kernel interface doesn't actually fit L8, forcing a rewrite of the calling code | Medium | `IEmKernel` (§10.3.4) carries a `Capabilities` flag from the start, and the refusal path in §10.3.3 is already written against it — so widening capability is a registration, not a refactor. Sanity-check the interface against a paper description of a full-wave solver *before* L7 freezes it |
| v1's uniform-cross-section limit reads as "broken" rather than "bounded" | Medium | R16a: specific, located refusal messages naming the offending geometry and the phase that will handle it — never a generic failure |
| Dielectric-interface truncation in the quasi-static kernel is quietly wrong | Medium | Truncation distance is an explicit auto-defaulted setting with a convergence test in the L7 gate |
| Rendering degrades late, after the model is locked | High | L2 benchmark harness in CI *before* L3; §5.3 architecture chosen up front, not retrofitted |
| Per-shape fills (R8a darkening) miss the §5.1 targets | Medium | R8b merge tier is built from the start, not as a rescue; the benchmark measures both paths so the threshold is data, and flipping the default to merge is a one-line change |
| Curve flattening tolerance gets chosen in several places and disagrees | Medium | One `ToClipperPaths(shape, tolerance)` helper (§6.1) that booleans, offsets, DRC, the mesher, hit-test and export all call. No second flattener anywhere |
| Users are surprised that a boolean silently destroyed their curve | Medium | §3.2 R9e: warn once per session; "Preview export flattening" makes the shipped form inspectable beforehand |
| DRC grows from two rules into a rule-engine project | Medium | §9A scopes it as *framework first, two rules*; further rules are a definition plus one Clipper2 recipe. No live checking in v1 |
| A stale `.snp` sits beside an edited layout and is used silently | Medium | §10.8: stamp stackup, mesh, ports and a geometry hash into the Touchstone header; warn on mismatch |
| The symbol renderer gets reused for layout geometry | High | Stated as a rule in §0. Layout gets its own renderer; `DrawSymbol` is not touched |
| JSON `.clay` size on large imports | Medium | Flat int arrays; measure with a synthetic large layout in L0; gzip held ready |
| Gerber import complexity | Medium | Export-only for v1, stated explicitly rather than half-shipped |
| Unit/DBU model gets it wrong and everything inherits the mistake | Medium | §1 is the first thing built and the first thing tested; unit change asserted to be a byte-identical no-op |
| Schematic→layout regenerating over hand placement | Medium | R16 idempotency by stable schematic `Id`, designed in from the start |
| Scope creep into auto-routing and LVS | Medium | Both named as non-goals for this plan. DRC is in scope but bounded to §9A's two rules; LVS is a direction, not a phase. Revisit only after L8 |
| Kernel W (LW1/LW2) dilutes effort across two EM kernels at once | Medium | LW1 shares everything but the kernel with L7 and carries **no DCIM dependency**, so it is not a second research project. Sequenced against L8 rather than beside it, and LW2 gated behind LW1's oracles |
| LW2's wire-to-surface junction is under-estimated because it looks like "just adding panels" | **High** | Named in §10.11 as the larger half of the wirebond effort and budgeted as one deliverable unlocking three capabilities. Gated on the right-angle-corner 3-image oracle *before* any package geometry, so the junction's correctness is measurable in isolation |
| A wirebond result is reported without a stated return path and is silently optimistic | **High** | §10.11: ports carry an explicit reference conductor and the UI refuses a port without one; ground bond wires are ordinary wires; a multi-tier ground with no declared connection is refused rather than shorted |
| Someone waits for L9/DCIM expecting stepped-ground support to fall out of it | Medium | Stated explicitly in §10.11 and `mom-wirebond-kernel.md` §7.1 (RW10): a z-step is a lateral variation, which is exactly what the 2.5D premise forbids. DCIM buys nothing here |
| The overmold model is refined past the accuracy of the material data | Low | §10.11: EMC εr spans 3.4–4.5 and is poorly characterised above a few GHz. The ladder stops at bound-charge surfaces by design; PMCHWT and VIE are named and declined |
| "No 3D EM" in the PRD and kernel W's existence contradict each other in the shipped docs | ~~Medium~~ **Closed 2026-08-04** | §10.11 resolves the layout half (2D projection, no 3D shape type, no volume mesher); the PRD §2 non-goal was narrowed to *no FEM, no volumetric meshing, no arbitrary 3D geometry* in v1.3, and §5/§8/§9 now describe kernel W |

---

## 13. Decisions taken — the record

All twelve questions this plan opened are answered — the original ten, plus 11 and 12 raised on
2026-08-04. Numbers are stable and append-only, so earlier revisions still resolve. Each entry states
the decision and where it now lives in the body.

| # | Question | Decision | Where it lives |
|---|---|---|---|
| 1 | MoM kernel starting point | **A → B → C** — quasi-static 2D per-unit-length first | §10.3, phases L7–L9 |
| 2 | Tech file scope | **Workspace-level shared `.ctech`**, several permitted per workspace, one default in `.cws` | §2.4 |
| 3 | Primary market for defaults | **Both, chosen per workspace** — two starter techs, two templates, two heroes, one code path | §2.4 |
| 4 | Connectivity in layout | **Shapes carry a net and the editor maintains it**; LVS is a named future direction | §3.4 R10a |
| 5 | Same-layer overlap | **Darkens.** Merge becomes an automatic LOD tier above a visible-shape threshold, and the fallback if the benchmark says so | §2.3 R8a/R8b, §5.3 |
| 6 | Curves | **`Curve`, `Circle`, `RoundedRect` are first-class primitives** over a shared edge-list model, with **Flatten to Polygon** on the context menu and automatic flattening on GDSII export | §3.2 R9a–R9e |
| 7 | `.clay` size | **Plain JSON.** Gzip held in reserve; the reader sniffs gzip magic bytes from day one so the switch is writer-side only | §4 |
| 8 | Clipper2 | **Approved.** Managed C#, Boost licence, integer coordinates. Added to the README acknowledgments | §6.1 |
| 9 | EM results vs. the TestBench invariant | **`.snp` artifact** consumed by the existing SnP component — invariant preserved, co-simulation for free | §10.8 R17a |
| 10 | DRC | **In scope.** Min-width + min-spacing first, chosen because those two force the whole framework to exist | §9A, phase L5b |
| 11 | 3D wirebonds (raised 2026-08-04) | **In scope as kernel W** — thin-wire MoM, quasi-static first (W1) then retarded (W2), wires-only (T1) first then meshed surfaces (T2). Separate kernel behind the existing `IEmKernel`; **independent of L8/L9** | §10.11, phases LW1–LW2, and [`mom-wirebond-kernel.md`](mom-wirebond-kernel.md) |
| 12 | How a 3D wire lives in a 2D layout | **A parametric component instance whose layout view is its 2D projection** plus an annotation. `.clay` gains no 3D shape type; no volumetric mesher is written | §10.11, `mom-wirebond-kernel.md` §9.2 (RW15) |

### What is still genuinely open

Nothing blocks starting L0. Five things are deliberately left to be decided *by* the work rather than
ahead of it, and each has a phase that will answer it:

- **The R8b merge threshold** — a number, set from the L2 benchmark, not guessed here.
- **Surface basis functions for the full-wave kernel** — rectangular rooftop vs. RWG triangles.
  Decided at L8, when there is a working mesher and real geometry to judge against (§10.5).
- **DXF `SPLINE` on export** — emit true splines or flatten Béziers to polylines. Decided at L4 against
  what real downstream consumers actually accept, which is an empirical question, not a design one.
- **LW1's priority relative to L8** — kernel W carries no DCIM risk and serves the PA/module designer
  directly, so running it first is defensible on risk-adjusted value. A roadmap call, not a technical
  one, and it does not block either phase's design. Tracked as `PRD.md` §17 open item 4.
- **The N-ceiling for a 200-wire array over a *meshed* ground** — raise R17, coarsen the ground mesh,
  or implement ACA. Decided at LW2 against real numbers, not guessed here.

*(The PRD's "no 3D EM" wording was the third item here until 2026-08-04; it is now resolved in PRD
v1.3 — see the §12 risk row and decision 11.)*

### Non-goals, stated so they stay non-goals

Auto-routing. Live as-you-type DRC. LVS as a phase (it is a direction — §9A.3). Gerber *import*.
**FEM, volumetric meshing, and arbitrary 3D geometry.** Each is a real product on its own; naming them
here is what keeps them from arriving one plausible increment at a time.

> **Narrowed 2026-08-04.** This line previously read "3D/FEM electromagnetics." Kernel W (§10.11) is a
> 3D *thin-wire* MoM over a specific, parameterised conductor class, which is in scope. FEM, volume
> meshing, and drawing arbitrary 3D solids remain out — and kernel W deliberately requires none of
> them: the layout stays 2D, `.clay` gains no 3D shape type, and the only 3D object is a wirebond
> polyline generated from named parameters.


### §11's L9 gate sentence — the proposal on the record (L9e, 2026-08-05)

**This is a proposal, not a decision.** §10.9's own rule is that golden data becomes a gate only when
the owner has approved it, so the second clause of the L9 row stays as written until ruled on.

**Proposed: strike *"agreement with published reference structures"*.** L9a found it does not survive
this project's own rules, and L9e reconstructed the reasoning: a published multilayer S-parameter
almost always arrives **without a verifiable stackup** — no tanδ, no metal thickness, often no
dielectric tolerance. A gate resting on one measures the transcription rather than the kernel, and
when it disagrees there is no way to tell which. That is the same reasoning that made L7b's Tier C3 a
reported non-result rather than a loosened tolerance.

**Proposed: strike *"with backside vias"* too, and say "with vias between metal levels".** This is the
correction the gate itself forced, and it is structural rather than a scoping convenience. **A backside
via is not representable by this kernel at all**: it joins a signal level to the GROUND PLANE, and the
ground plane is the laterally infinite plane the Green's function handles analytically — never a
meshed level. L9c's via basis is a rooftop spanning two ADJACENT MESHED levels, so a via to ground
needs an **attachment (half) basis terminating on the PEC boundary**, which does not exist.
`PlanarExtractor` already drops such a via with a note (its span names a ground-reference conductor,
which is never an analysis level), so the behaviour is correct and reported — it is simply not
something that can be gated. What IS representable on the MMIC starter is the **Metal1↔Metal2 post**,
the airbridge the stackup was built for. Building the attachment basis is now the second-most valuable
thing anyone could add to this area, after L9c's un-run Tier 4.

**Proposed replacement — three self-consistency checks, all external-data-free. BUILT, as
`tests/Ui.Tests/Em/L9PhaseGateTests.cs`:**

1. **The vias carry the current** — |S₂₁| of a drawn airbridge against |S₂₁| of the same artwork with
   the posts removed, on the MMIC starter, plus reciprocity and passivity (never losslessness). The
   fixture's Metal1 has a gap, so the posts are the only conducting path across it.
   **The absolute-inductance form of this check stays a FINDING rather than a pass**: the kernel and
   the fill are right and the midpoint rule is 4.9% high on the shipping MMIC spacer (L9e), so an
   inductance gate would have to be stated on the split via or on the geometric bound, not on the raw
   rule. And the obvious signature — |S₁₁| rising with frequency — is reported and NOT gated, because
   L9d measured the two-level de-embedding residual at the same order as the effect.
   **UPDATE (via-z-integral follow-up, 2026-08-06): the midpoint rule is GONE.** The z-integral is
   resolved and the absolute inductance is now measured against an independently-integrated exact
   partial inductance, flat to **0.124%** over ℓ/w ∈ [0.01, 5] and a 16× range of footprint width
   (`ViaPhysicsTests.T3_1`). That is the absolute-inductance gate this paragraph said could not be
   stated, and it lives in the engine's own tier rather than here — the de-embedding residual still
   bounds what the PRODUCT path can claim, so gate 1 is unchanged.
2. **A two-level structure against the one-level reduction it degenerates to** — a Metal1 line shadowed
   by a floating Metal2 strip; widen the gap and the perturbation must fall, onto the answer the
   SHIPPED one-slab path gives for the same line. The gate is the ordering; the absolute closeness is
   bounded by that same residual.
3. **The wiring** — two levels and two vias extracted with the equal-area square, a backside via
   dropped WITH its note, and a via long against its own footprint refused by name. The only routine
   one, because it refuses or extracts and never fills a matrix.
   **UPDATE (2026-08-06): the ℓ/w refusal is retired** and that third claim now asserts the opposite —
   the geometry it used to refuse must be ACCEPTED — while the ELECTRICAL refusal (k·ℓ ≤ 0.05, which
   is about the via basis carrying a uniform current rather than about any quadrature) must still
   fire. Retiring it widens nothing: `Dcim.ValidatedRhoOverLambdaAtHeights = 0.1` is untouched and is
   what actually restricts a via-bearing run to electrically small structures.

Sizing note, since it constrains any future fixture: `G_A^zz` is validated to ρ/λ ≤ 0.1 (free-space λ),
so these run at 300 µm over 10–30 GHz. §10.7's FR-4 hero at ~0.67 λ is refused by construction, and
**`Dcim.ValidatedRhoOverLambdaAtHeights` must NOT be widened because a gate fixture is inconvenient** —
L9c measured the 14× error that justifies it.

**Confirmed and worth recording so it is not re-scoped:** the buried-level de-embedding refusal does
**not** block this gate. Both ports sit on the slab's top level, so L9c's un-run Tier 4 (a static
Green's function at interior heights) is not a prerequisite for it.

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

Companions: **[`mom-engine.md`](mom-engine.md) (the MoM engine — §10, split out of this file on
2026-08-24 with its section numbers intact)**, `mom-wirebond-kernel.md` (kernel W — 3D wirebonds,
§10.11), `grid-and-connectivity.md` (the two-grid philosophy this extends), `symbol-editor.md`
(the editor pattern to mirror), `workspace-and-project-tree.md` §2 (cell folders and primacy),
`project-file-formats.md` (JSON conventions), `schematic-hierarchy-navigation.md` (push-in/pop-out),
`ui-architecture.md` (the UI firewall).

**Scale of the effort.** Sections 1–9A are a large but conventional editor build — comparable to the
schematic + symbol editor together. Section 10 (MoM) is a research-grade numerics project on its own
and is scoped separately with an explicit staged path — which is why **it now lives in its own note,
[`mom-engine.md`](mom-engine.md)**; every "§10.x" reference in this file resolves there. Do not treat
them as one phase. Within §10, the research risk is concentrated in the **layered-medium Green's
function** (§10.2) — kernels A and W1/W2 use a free-space kernel and are ordinary, bounded
engineering.

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

## 10. The 2.5D MoM simulator — **moved to [`mom-engine.md`](mom-engine.md)**

**This section is now its own design note: [`docs/design/mom-engine.md`](mom-engine.md).** Everything
that was here — §10.1 through §10.11, the three kernels, the Green's function, the mesher, ports and
de-embedding, the size budget, the validation oracles and the 30-second acceptance test — is there
unchanged, **with its section numbers preserved**, so an existing "§10.6" pointer still resolves.

Two reasons for the split, and neither is length on its own:

- **It is a different project.** §§1-9A are a conventional editor build; §10 is a research-grade
  numerics effort with its own phases (L6-L9, LW1/LW2), its own gates and its own risk. The note above
  already said "do not treat them as one phase", and keeping them in one file worked against that.
- **The dependency is one-way and thin.** The MoM engine consumes the geometry model (§3) and the
  technology file (§2.4) and nothing else; the editor consumes nothing from it. A reader of either
  half was carrying the other.

What stays here: §11's phasing table still lists the L6-L9 phases, because the phase plan is one plan;
§13's decision record still holds the kernel decisions, because that is the record of what was decided
for this design, not for one section of it.

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

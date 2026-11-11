# circuitRF — the `.c3d` 3D view format

**Status.** This is the specification of the `.c3d` document as circuitRF reads and writes it today (`FormatVersion` 1),
plus a section of **planned** additions, marked as such, for the 3D constructs other tools' documents hold and
`.c3d` does not yet.

**Audience.** Anyone who reads or writes a `.c3d` outside circuitRF: an importer, an exporter, a script, another
application. It is written so that it can be implemented from this page alone.

**Two sources, one contract.**

- **This page states the model**: what the document is, how its parts relate, the rules a reader must apply, and how
  the format changes over time.
- **The complete field list is generated** from the types the reader deserialises. Run `circuitrf reference
  3d-view`, or call the MCP server's `reference` tool with `topic=3d-view`, and you get every key, its type and its
  default.
- Where this page and the generated list disagree about a field, the generated list is right and this page is the
  defect.

`docs/design/em-3d.md` explains *why* the 3D side is built this way (§6.3 Tier B, §6.3a precedence, §6.4 naming).
This page is the *what*.

---

## 1. Where a `.c3d` lives

A `.c3d` is one **view** of a **cell**. A cell is a folder holding one `.ccell` (its parameters and their defaults)
and its views. 3D views live in the cell's `3d/` subfolder:

```
<workspace>/
  <name>.cws                  the workspace
  <cell>/
    <cell>.ccell              parameters + defaults
    <cell>.clay               layout view (optional)
    <cell>.csch               schematic view (optional)
    3d/
      <view>.c3d              a 3D view (any number)
      part.step               files the view's objects name (STEP parts, images, meshes)
```

A `.c3d` outside any cell folder is legal. It sees only its own variables, and no cell parameters.

**Technology.** Material names resolve against a technology (`.ctech`). That is the one `TechRef` names (a path
relative to the `.c3d`, or `ws://…`), or the workspace's default technology when `TechRef` is omitted.

## 2. The document

UTF-8 JSON, one object. Keys are PascalCase. A reader **must ignore key order**, and a writer should write keys in
the order below.

| Key | Type | Meaning |
|---|---|---|
| `FormatVersion` | int | `1`. See §9 for how it changes |
| `DbuPerMicron` | int | What one database unit is worth; at `1000`, 1 DBU = 1 nm. **Every coordinate in the file is an integer DBU** |
| `DisplayUnit` | `Nm`/`Um`/`Mm`/`Mil`/`Inch` | Editor preference only; nothing is computed from it |
| `SnapDbu` | int | Editor drawing grid; nothing is computed from it |
| `TechRef` | string? | The technology (§1) |
| `Objects` | [object] | The solids, sheets and construction geometry, **in construction order** (§4) |
| `Instances` | [instance] | Other cells placed here: the hierarchy (§6) |
| `Variables` | [variable] | The view's VARs (§5) |
| `Ports` | [port] | Excitations (§7) |
| `FaceBoundaries` | [face boundary] | Boundaries on named faces of dielectric or air objects (§7) |
| `LumpedElements` | [lumped element] | R, L and C across gaps (§7) |
| `Setups` | [setup] | Embedded analysis setups, in the `.cem` schema without its `LayoutRef` (§8) |
| `ActiveSetup` | string? | The setup the editor shows; editor state |
| `AirBox`, `AirBoxHidden` | | The air box while no setup is active; editor state. A run reads its setup's own air box |
| `AirBoxMaterial` | string? | The material filling the air box |
| `MeshRegions` | [mesh region] | Boxes with a target mesh size, for Gmsh-meshed solves |
| `SymmetryPlanes`, `HeatSources`, `Probes`, `ContactResistances`, `EffectiveBlocks`, `WireGroundPlane` | | Read by circuitRF's thermal solver only; never by an EM solver |
| `BandSweeps` | [band sweep] | Phone-SAR band tables; never read by a run |
| `FieldPlots`, `Look` | | Drawing state: field plots and the realistic view's lighting. No solver reads them |

**Unknown keys are kept.** A reader that meets a key it does not know keeps it and writes it back unchanged, so a
document written by a newer build survives a round trip through an older one. circuitRF's `check` names each such key
as a warning.

## 3. Coordinates, units and expressions

- **Axes.** Right-handed, z up. A point is `[x, y, z]`.
- **Drawing planes.** A plane is named by the two axes it spans, and a point on it is `[u, v]`:

  | Plane | (u, v) | Normal |
  |---|---|---|
  | `XY` | (x, y) | +z |
  | `YZ` | (y, z) | +x |
  | `XZ` | (x, z) | +y |

  `Offset` is the plane's position along its normal.
- **Integers only.** Every coordinate and size is an integer DBU. A rotation never appears in coordinates; it lives in
  the `Placement` (§4.2), so geometry never acquires rounding.
- **Sizes, not second corners.** A box is `Min` + `Size`, a rectangle `Min` + `Size`, a cylinder `Base`, `Axis`,
  `Length`, `Radius`. A negative size is legal and is normalised by moving `Min`.
- **Expressions.** Any *named dimension* may hold an expression instead of a number. An expression is always an
  object carrying the unit it was typed in, never a bare string:

  ```json
  "Size": [{ "Expr": "w", "Unit": "Mil" }, 1270000, { "Expr": "h_sub + t_met", "Unit": "Um" }]
  ```

  The fields that may hold one:
  - `Min`/`Size` (box, rectangle)
  - `Offset`, `Height`, `Shear` (prism, sheet, polyline)
  - `Base`, `Length`, `Radius` (cylinder)
  - `ThicknessUm`
  - a placement's `Origin` and each rotation's `Deg`
  - an array's `Counts`/`Pitch`
  - a port's `Rect`
  - a wire's `DiameterUm`, points, `LoopHeight` and `Span`
  - a polyline's points
  - `Radius`, `Distance` and `Distance2` of fillets and chamfers

  `Outline`, `Holes` and polyhedron `Vertices` hold numbers only.
- **Units inside expressions.**
  - A number may carry a unit glued to it (`2*w + 5um`).
  - The field's own `Unit` applies to the bare numbers added to, subtracted from or compared with a unit-bearing
    term. In `2*w + 5`, with `w` in mil, the 5 is five mil and the 2 stays a multiplier.
  - A bare `m` is **milli**; the metre is spelled `metre`.
  - An angle's unit is `Deg`. A count has no unit and must evaluate to a whole number ≥ 1; it is never rounded.
- **Resolution.** Every expression resolves to a number before any geometry is built, and a length is rounded to the
  DBU once. The language is circuitRF's one expression engine (`docs/design/expressions.md`): arithmetic, `^`, the
  standard functions, comparisons, `&&`/`||`/`!` and `if(cond, then, else)`.

## 4. Objects

Each entry of `Objects` is selected by `"$type"`. **A reader must refuse a document holding a `$type` it does not
know, and list every one**, rather than drop the geometry silently. That is the one place where a newer document is
refused instead of round-tripped (§9).

### 4.1 Fields every object has

| Key | Meaning |
|---|---|
| `Name` | Unique across all objects **and** instances, at every depth; cell-name rules apply. `airbox` is reserved |
| `Material` | A material of the technology. A polyline has none |
| `Role` | `Conductor`/`Dielectric`/`Air`. Overrides what the material implies (σ and no εr → conductor; εr → dielectric; named Air → air) |
| `Placement` | §4.2 |
| `Model` | `false` (written only then): drawn and editable, but left out of every run |
| `Group` | An organisation path (`pa/match`); never reaches a solver |
| `Hidden`, `Transparency`, `Appearance`, `DrawingLayer`, `FaceImages` | Drawing only; no solver reads them |
| `FlattenedFrom` | Reporting only: the cell a flattened copy came from |

### 4.2 Placement

`{ "Origin": [x,y,z], "Rotate": [{ "Axis": "Z", "Deg": 90 }, …], "MirrorX": false }`. The object's own frame is:

1. mirrored (`MirrorX` negates x), then
2. rotated by each `Rotate` entry **in list order** (right-handed), then
3. moved to `Origin`.

Omitted, it is the identity. A writer should use the canonical form circuitRF writes: at most three rotations, in the
order Z, Y, X, each omitted at 0°. Any rigid motion, a mirror in any plane included, is a placement.

### 4.3 Primitives

| `$type` | Shape fields | Face names |
|---|---|---|
| `Box` | `Min`, `Size` | `xmin xmax ymin ymax zmin zmax` (own frame) |
| `Prism` | `Plane`, `Offset`, `Outline` [[u,v]], `Holes` [[[u,v]]], `Height` (along the normal, may be negative), `Shear` [u,v] (moves the top against the bottom) | `bottom`, `top`, `side<k>` (outline edge k→k+1), `hole<h>.side<k>` |
| `Cylinder` | `Base`, `Axis` (X/Y/Z), `Length`, `Radius` | `bottom`, `top`, `side` |
| `Sphere` | `Centre`, `Radius` | `surface` |
| `Ellipsoid` | `Centre`, `SemiAxes` (own frame) | `surface` |
| `Polyhedron` | `Vertices` [[x,y,z]], `Faces` [{`Name`, `Outer` [vertex index], `Holes`}]: closed, each edge used twice in opposite directions, each face planar within 1 DBU | each face's own `Name` |
| `Sheet` | `Plane`, `Offset`, and either `Rect` {`Min`,`Size`} or `Outline` + `Holes`; `ThicknessUm` (the metal's thickness). A sheet always conducts. `Image`, `Locked`: a reference picture | — |
| `CylinderSheet` | A patch of a cylinder: `Axis`, `Base`, `Radius`, `Length`, `StartDeg`, `SweepDeg` | — |
| `Polyline` | `Plane`, `Offset`, `Points` [[u,v]] (or `Points3` [[x,y,z]] when a vertex leaves the plane), `Closed`. **Construction geometry only: never solved** | — |
| `Wire` | A bond wire: `Points` [[x,y,z]], `DiameterUm`, `Section` (`Hexagon`/`Round`), `Start`/`End` {`Style` `Wedge`/`Ball`, `FootLengthUm`}, `Array` {`Count`, `Pitch`}, optional held `LoopHeight`/`Span` | — |
| `Phantom`, `VoxelBody`, `Mesh` | Body models for SAR work: a layered phantom; a segmented voxel file; a closed triangle surface file (STL/OBJ/PLY). See the generated reference | — |

### 4.4 Operations

An operation wraps other objects **inline**. **The wrapper takes the name**: the object inside a `Boolean`'s `Blank`
or a `Fillet`'s/`Chamfer`'s `Target` has no `Name`, and its `Material` and `Role` are the result's. Tools keep their
own names. Operands are solids only: `Box`, `Prism`, `Cylinder`, `Sphere`, `Polyhedron`, `Step`, or another
operation.

| `$type` | Fields |
|---|---|
| `Boolean` | `Op` (`Subtract`/`Unite`/`Intersect`), `Blank`, `Tools` [object], `KeepTools` (with `Subtract`: each tool also stays as its own solid, e.g. a dielectric fill in a bore), `Enabled` |
| `Fillet` | `Target`, `Edges` [edge name], `Radius`, `Enabled` |
| `Chamfer` | `Target`, `Edges`, `Distance`, optional `Distance2` (along the edge's second face), `Enabled` |
| `Step` | One solid part of a STEP file copied beside the `.c3d`: `File`, `Part` (occurrence path in the assembly, `1/2`), optional `Solid` (1-based, when a part holds several), `Hash` (`sha256:<hex>` of the file), `Unit` (informational), `SourcePath` (where it came from) |

- **`Enabled: false`** makes an operation as if absent: the Blank elaborates under the operation's name and each
  Tool under its own; a Target elaborates unrounded.
- **Without OpenCASCADE** (installation-dependent), a document holding any operation, at any depth, enabled or not,
  is refused.

### 4.5 Naming faces and edges: never by index

**Nothing in a `.c3d` refers to a face, edge or vertex by number.** Primitives name their faces (§4.3), and an
operation's result names its faces by rule:

- **Faces.**
  - The Blank's faces keep their bare names (`zmax`).
  - A tool's faces are `<tool>:<face>` (`bore:side`; nested: `<tool>:<inner>:<face>`).
  - A face the operation split is `zmax#1`, `zmax#2`, …, numbered by centroid in the object's own frame (x, then y,
    then z). A reference to `zmax` covers every piece.
- **Edges** are the two faces they separate, sorted and joined by `|` (`xmax|zmax`, `bore:side|zmax`). When two faces
  share more than one edge, a third field numbers them (`trench:side|zmax|1`). A seam has no name.
- **New faces.** A fillet's new faces are `fillet(<edge>)`, a chamfer's `chamfer(<edge>)`.
- **STEP parts.** A STEP part's faces are `face<n>` in the part's own order, meaningful only for its `Hash`.

So a port or boundary attached to `lid` before a hole was cut is still on `lid` afterwards.

### 4.6 Overlap and precedence

Where solids overlap, **metal wins over dielectric whatever the order**, and otherwise the **later** object in
`Objects` wins (`em-3d.md` §6.3a).

- **Metal** means conductor-role solids and every sheet.
- **Air** keeps construction order against metal. An air solid drawn after a metal hollows it.

## 5. Variables and parameters

```json
{ "Name": "w", "Expression": "10", "Unit": "Mil" }
{ "Name": "gap", "Expression": "w / 4" }
```

- **Scope.** A 3D view sees its cell's parameters (from the `.ccell`) and its own `Variables`, in one namespace.
  There are no globals.
- **Linking.** A variable named like a cell parameter is **linked** to it: its value is the parameter's (an instance
  override, else the `.ccell` default). With `"Linked": false`, the variable keeps its own expression, and overrides
  do not reach it.
- **Cycles.** Through defaults, variables, links and overrides, a cycle is refused with its chain.

## 6. Hierarchy: instances

An instance places another cell's 3D view (or its layout) here:

```json
{ "Name": "U1", "CellRef": "../Board", "View": "ThreeD",
  "Placement": { "Origin": [0, 0, -35000] },
  "Array": { "Counts": [2, 1, 1], "Pitch": [5000000, 0, 0] },
  "Params": { "w": { "Expr": "2*a", "Unit": "Mil" } } }
```

| Key | Meaning |
|---|---|
| `CellRef` | The placed **cell**: a path relative to this `.c3d`'s folder, or `ws://<path>` from the workspace root |
| `View` | `ThreeD` (the cell's 3D view; the default) or `Layout` (its layout made 3D through its own technology's stackup) |
| `Placement` | As an object's (§4.2) |
| `Array` | `Counts` [i, j, k] and a `Pitch` vector, in this document's frame |
| `Params` | Overrides of the placed cell's **parameters**, each an expression evaluated in **this** document's scope. A name that is only a variable of the child is refused. A `Layout` instance takes none |
| `Model`, `Group`, `Transparency`, `Appearance` | As an object's |

Rules a reader must apply:

- **Names.** The placed objects are named `<instance>/<object>`. An array element's objects are
  `<instance>[i,j,k]/<object>`, and nesting composes (`A/B/obj`).
- **Only geometry comes in.** A placed cell's ports, setups and face boundaries are ignored: only the parent says where
  a signal enters.
- **Materials.** Same-named materials from two technologies merge when they are equal, and otherwise become
  `<name>@<technology stem>`.
- **Cycles.** A view that reaches itself through its instances is refused, judged by cell **and** view.
- **Sharing.** Two instances whose overrides come to the same values share one elaboration of the child.

## 7. Ports, boundaries, lumped elements

### Ports

```json
{ "Number": 1, "Name": "P1", "Kind": "Lumped", "Plane": "XZ", "Offset": 0,
  "Rect": { "Min": [2000000, 0], "Size": [1100000, 508000] }, "Z0": "50" }
```

- **A port is a rectangle on a drawing plane.** It is not an object and not a face.
- `Z0` is `"50"` or a complex `"25+j10"`.
- **A lumped port's two conductors are inferred** from what its rectangle's opposite edges touch. `Positive` and
  `Negative` (elaborated object names, `U1/pad` allowed, or `airbox/zmin`) state them instead, and `Flip` swaps them.
- `Angle` turns a lumped port in the XY plane about its centre.
- **A wave port's rectangle is a region of an air-box face.**
  - `VoltagePath` {`From`, `To`} states its voltage line.
  - `Reference` plus `Terminals` [{`Number`, `Name`, `Conductor`, `Z0`}] make a multi-conductor wave port with
    terminal S-parameters.
- **Numbering.** Port numbers are unique across ports and terminals. A run's result numbers the modelled ports 1…N in
  `Number` order.

### Face boundaries

`{ "Object": "block", "Face": "zmax", "Kind": "Pec" }`, on a **named face of a dielectric or air object** (a conductor
is already bounded by its metal).

- `Kind`: `Pec`, or `Conductive` with a `Material`.
- `Absorbing`, `Pmc` and `Symmetry` belong on the air box, set per face in a setup's `AirBox` together with each face's
  `PaddingUm` or `PaddingPercent`.

### Lumped elements

R, L and C across a gap shaped exactly like a lumped port's gap: `Name`, `Plane`, `Offset`, `Rect`, optional
`Positive`/`Negative`, and `Topology` `Parallel` or `Series`. The values are `R`, `L` and `C` as expressions, with
`RUnit`, `LUnit` and `CUnit`. A lumped element is not excited and has no row in S.

## 8. Setups

`Setups` holds analysis setups in the **`.cem` schema** (`circuitrf reference em-setup`), without its `LayoutRef`. Each
has a unique `Name`, a `Problem3D` and a `Solver3D`:

- `Problem3D`: `Driven`, `Electrostatic`, `Magnetostatic`, `Eigenmode` or `Thermal`.
- `Solver3D`: `Palace`, `OpenEms` or `Emerge`. A `Thermal` setup states none; circuitRF's own thermal solver runs it.

A setup also holds the frequency plan, the air box and solver-specific settings. The setup schema is its own
document's contract and versions with it.

## 9. How the format changes

- **`FormatVersion` rises only when the meaning of something already in the format changes.** A new optional key, a
  new enum value or a new `$type` does not raise it. `FormatVersion` has been `1` through every addition so far.
- **A reader refuses a `FormatVersion` newer than its own**, naming both.
- **A reader keeps keys it does not know** and writes them back unchanged (§2).
- **A reader refuses an unknown `$type`**, listing each one (§4), because it cannot draw or solve geometry it does not
  understand. So a document using a planned type (§10) is refused by a build that predates it, explicitly, never
  misread.
- **A writer omits a key at its default** wherever the generated reference shows one, so documents stay small and
  diffs meaningful.

## 10. Planned: constructs `.c3d` does not hold yet

> **Not built.** This section records **where** each construct would go, so additions land consistently and other tools
> can plan against it. Every row follows the format's existing rules:
> - geometry is integer DBU with optional expressions
> - faces and edges are named, never indexed
> - shapes are `$type`s in `Objects`
> - modifiers are operations that wrap a target inline and take its name
> - anything only a solver reads lives in a setup
>
> No row raises `FormatVersion` (§9). Names here are proposals until the brief that builds each one fixes them.

### 10.1 New primitives: new `$type`s in `Objects`

| Construct | Proposed `$type` | Fields | Faces |
|---|---|---|---|
| Cone / truncated cone | `Cone` | `Base`, `Axis`, `Length`, `BottomRadius`, `TopRadius` | `bottom`, `top`, `side` |
| Torus | `Torus` | `Centre`, `Axis`, `MajorRadius`, `MinorRadius` | `surface` |
| Helix (a round wire on a helical path) | `Helix` | `Base`, `Axis`, `Radius`, `Pitch`, `Turns`, `RightHanded`, `WireRadius` | `surface`, `start`, `end` |
| Planar spiral trace | `Spiral` (a sheet) | `Plane`, `Offset`, `Centre` [u,v], `InnerRadius`, `Pitch`, `Turns`, `Width`, `RightHanded` | — |
| Circle / ellipse sheet | `Sheet` gains `Ellipse` {`Centre` [u,v], `Radii` [ru, rv]} as a third shape beside `Rect` and `Outline` | | — |
| Regular polygon (sheet) and regular prism | `Sheet`/`Prism` gain `Regular` {`Centre` [u,v], `Sides`, `Start` [u,v]} beside `Outline`; the outline is generated at elaboration, so expressions survive | | `side<k>` as for any prism |
| Arcs in an outline | `Outline` gains a parallel `Arcs` list: `{ "From": k, "Through": [u,v] }` turns edge k→k+1 into the circular arc through that point | | the arc edge keeps `side<k>` |
| Bond wire by profile (start, end, two heights) | **Existing** `Wire`, with a `Profile` {`Kind`, `H1`, `H2`} that generates `Points` | | — |

### 10.2 New operations: wrappers in `Objects`, following §4.4

| Construct | Proposed `$type` | Fields | Notes |
|---|---|---|---|
| Sweep a profile along a straight vector | **Existing** `Prism`: `Height` along the normal plus `Shear` for the in-plane part | — | Already expressible; no new type |
| Revolve a profile about an axis | `Revolve` | `Target` (a sheet or closed polyline), `Axis` {`Point` [x,y,z], `Direction` [x,y,z]}, `AngleDeg` | Faces `start`, `end`, `side<k>` |
| Sweep a profile along a path | `SweepPath` | `Target` (profile), `Path` (a `Polyline`'s name), `Twist` (`Fixed`/`Frenet`) | Faces `start`, `end`, `side<k>` |
| Loft between profiles | `Loft` | `Profiles` [names], `Ruled` | |
| Thicken a sheet into a solid | `Thicken` | `Target` (a sheet), `Thickness`, `BothSides`, `Reverse` | Faces `top`, `bottom`, `side<k>` |
| Split by a plane | `Split` | `Target`, `Plane` {`Point`, `Normal`}, `Keep` (`Positive`/`Negative`/`Both`) | With `Both`, the pieces are named `<name>#pos`/`<name>#neg` |
| Section: the sheet a plane cuts from a solid | `Section` | `Target`, `Plane` {`Point`, `Normal`} | The result is a sheet |
| Imprint (split faces where tools touch) | `Imprint` | `Blank`, `Tools` | Faces named as a `Boolean`'s |
| Scale | `Scale` | `Target`, `Centre`, `Factor` | Baked in at elaboration, so `Placement` stays rigid |
| Keep one body of a multi-body result | `Pick` | `Target`, `Inside` [x,y,z] (a point in the body to keep) | **By a point, never an index** (§4.5) |
| Faces → sheets; remove faces; offset faces | `KeepFaces`, `RemoveFaces`, `OffsetFaces` | `Target`, `Faces` [face names], `Distance` (offset) | By face **name** |

**Rotation about an arbitrary axis** needs no new type: a placement already holds any rotation (§4.2). A placement
rotation entry would gain an optional `Vector` (axis direction) and `Pivot`, so that an angle stays an expression
instead of being folded into Euler angles.

### 10.3 Excitations and boundaries

| Construct | Where |
|---|---|
| Multi-mode wave port | `Ports[]` gains `Modes` (int, default 1) |
| Floquet port | `Ports[]` `Kind: "Floquet"` on an air-box face region, with `Modes` (`TE00`, `TM00`, …) |
| Incident plane wave | Setup-level `Excitation`: { `Kind`: `PlaneWave`, `ThetaDeg`, `PhiDeg`, `Polarization`, `E0` }. It belongs to the analysis, not to the geometry |
| Impedance surface | `FaceBoundaries[]` `Kind: "Impedance"` with `R` and `X` (Ω/sq) as expressions |
| Periodic (primary/secondary) pair | A setup's `AirBox` faces gain `Boundary: "Periodic"` plus `Pair` (the opposite face) and `ScanThetaDeg`/`ScanPhiDeg` |
| PML as distinct from a first-order absorber | A setup's `AirBox` face `Boundary: "Pml"` with `Layers` and `Thickness` |
| Lumped RLC from a Touchstone file | `LumpedElements[]` gains `Touchstone` (a path beside the `.c3d`) and `Mount` (`Series`/`Shunt`) |
| Mesh size on an object or a face | `MeshRegions[]` gains `Object` and `Faces` as alternatives to its box |

### 10.4 Frames

| Construct | Where |
|---|---|
| Named coordinate systems | A top-level `Frames` [{`Name`, `Placement`, `Parent`}], and a `Frame` key on a `Placement` meaning "relative to this frame" |

### 10.5 Not in `.c3d` by design

| Construct | Where it belongs instead |
|---|---|
| Material models (dispersion, anisotropic tensors, roughness) | The technology (`.ctech`) and material library (`.cmat`), where every material already lives |
| Reports, plots, optimization and parametric sweeps | circuitRF's Data Display (`.cdd`), test benches and optimize lines |

### 10.6 Placing a design held in another tool's project file

| Construct | Where |
|---|---|
| A 3D design inside a Hertzon project (`.hzn`) placed as an instance | `Instances[]` with `"View": "Hertzon"`, `CellRef` naming the `.hzn` file (spelled as any `CellRef`), and a new `Design` key naming the design. `Params` override that design's variables. circuitRF translates the design to geometry when it elaborates, and refuses it, listing what it cannot represent, exactly as opening it would (`docs/briefs/brief-hzn-9-instance.md`) |

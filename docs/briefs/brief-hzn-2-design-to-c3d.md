# HZN-2 — a `.hzn` 3D design as a `C3dDocument`

Series: `brief-hzn-0-overview.md`. Needs HZN-1.

## Why

Opening, converting, running on circuitRF's EMerge and placing as an instance all start from the same step: one
`.hzn` 3D design turned into a `C3dDocument` in memory. This brief is that translation and its refusal.

## What to build

### R-hzn2-1 — `HznToC3d.Translate(HznProject, designName) → C3dTranslation`

A result holding either:
- a `C3dDocument`, its in-memory technology (R-hzn2-5) and a **provenance map**: each `.c3d` object, port, boundary and
  variable → the `.hzn` object, step, boundary or excitation it came from. Brief 5 saves through this map; or
- a refusal listing every unsupported item (overview D3).

The translation never reads the file system, except a STEP file an `Import` step names, which goes through the
existing `C3dStep` path.

### R-hzn2-2 — histories to the `.c3d` tree

A Hertzon object is a history; a `.c3d` object is a tree. Fold each history into that tree:

- The first step makes the shape.
- A `Subtract`/`Unite`/`Intersect` step wraps the tree so far as the `Blank` of a `C3dBoolean`, whose `Tools` are the
  folded trees of the named objects.
- `Fillet`/`Chamfer` wraps it as the `Target`.
- `Move`/`Rotate`/`Mirror` become the `Placement`, when the placement can say them exactly (ordered axis rotations
  about a stated centre). Otherwise the step is refused.
- A `suppressed` step is skipped and recorded in the provenance map, so brief 5 can write it back.
- An object with `consumed_by` set is not a top-level object; it appears only inside its consumer.

Starting table: each row is confirmed by a test, or moved to "refused" with the reason recorded.

| Hertzon step | `.c3d` | Notes |
|---|---|---|
| `CreateBox` | `C3dBox` | `Position` + `XSize/YSize/ZSize`; negative sizes normalised |
| `CreateCylinder` | `C3dCylinder` | `NumberOfSegments` 0 only (a true circle); faceted → refused at first |
| `CreateSphere` | `C3dSphere` | same rule on segments |
| `CreateRectangle` | `C3dSheet` | axis-aligned on `Axis` |
| `CreatePolyline` closed + covered, straight segments | `C3dSheet` / `C3dPolyline` | arcs in `SegmentTypes` refused at first |
| `CreatePolyline` + `SweepAlongVector` along the normal | `C3dPrism` | any other vector refused |
| `CreateBondwire` | `C3dWire` | profile mapping recorded |
| `Unite` / `Subtract` / `Intersect` | `C3dBoolean` | `KeepOriginals` → `KeepTools` only where the meanings agree |
| `Fillet` / `Chamfer` on solid edges | `C3dFillet` / `C3dChamfer` | edge indices resolved by geometry (R-hzn2-4) |
| `Move` / `Rotate` / `Mirror` | `Placement` | exact placements only |
| `Import` (STEP) | `C3dStep` | the file is copied beside the opened document's scratch folder |
| `CreateRegion` | the setup's air box | padding mapped where the `.c3d` air box can state it |

**Refused at first:** every other step, including cone, torus, helix, spiral, ellipse, regular polygon and polyhedron,
sweep around an axis or along a path, thicken, cover and uncover, section, split, imprint, connect, keep faces and
keep body, sweep faces along their normal, and scale. Each is a later addition with its own test. A vertex-indexed
chamfer or fillet on a sheet is refused, because `.c3d` rounds solid edges only.

### R-hzn2-3 — expressions and variables

- Design `variables` become `C3dVariable`s.
- Project variables (`$name` on Hertzon's command line) become variables under a reserved, reversible name. Brief 5
  writes them back under their own names, at project level.
- Every expression goes through circuitRF's one expression engine (`docs/design/expressions.md`). Hertzon's spelling is
  translated token by token: inline units (`1.524mm`, `2.45GHz`, `0deg`), its constants (`c0`, …) and its functions.
- An identifier, constant or function circuitRF does not have is a refusal item naming it. **Never string
  substitution.**
- A position `"a,b,c"` is split into three expressions at the top-level commas only, so `if(x,1,2)` stays whole.
- `units` sets the `.c3d`'s `DisplayUnit`, and a bare number takes it.

### R-hzn2-4 — references by index, resolved by geometry

A boundary, excitation, fillet or chamfer that names faces, edges or vertices by index is resolved against the
geometry the kernel builds.

1. Build the object through the existing geometry-kernel path (`src/Design/ThreeD/Occ`).
2. Match each `{face, sig}` to the face whose centroid, normal and area agree with `sig` within a stated tolerance.
3. Record that face's `.c3d` name (`xmax`, `bore:side`, `zmax#2`).

Rules:

- **The index alone is never trusted.**
- No match, or two matches, is a refusal item.
- Without the OpenCASCADE worker installed, only shapes the managed kernel builds can be resolved. Anything else is a
  refusal item naming the worker as what enables it.
- Record in `RESOLVED.md` §HZN what each `sig` value was found to mean, and the experiment that showed it.

### R-hzn2-5 — materials

- The project's `materials` become an in-memory technology for the document (`TechRef` unset, the technology attached
  directly). Permittivity, permeability, conductivity and loss tangents map directly. Djordjevic–Sarkar dispersion
  maps only where the technology's material model states it. Anisotropic tensors are refused at first.
- `pec`, `vacuum`, `air` and `copper`, named but not defined in the project, come from a table written here (overview
  Q3). Any other undefined name is a refusal item.
- An object whose `material` is `vacuum` but that carries a conductor boundary on the whole object is a metal sheet in
  `.c3d` terms. Record the rule and its test.

### R-hzn2-6 — ports and boundaries

- **Lumped Port on an object.** When the object is a rectangle sheet in an axis plane, it becomes a `C3dPort` (Lumped)
  with that plane, offset and rectangle, and the sheet does not become a `.c3d` object. The provenance map keeps it for
  brief 5. Its `IntegrationLine` sets `Positive`/`Negative` when the two ends land on named conductors; otherwise they
  are inferred as for any `.c3d` port.
- **Wave Port** follows the same rule. Several modes are refused at first.
- **Perfect E, Finite Conductivity, Radiation, Perfect H, Symmetry** become `C3dFaceBoundary` (PEC, Conductive,
  Absorbing, PMC, Symmetry) or `SymmetryPlanes`, per object or per resolved face.
- **PML** becomes Absorbing.
- **Lumped RLC** becomes `C3dLumpedElement` when the element model can state it. A Touchstone mount is refused at first.
- **Refused:** Impedance, Primary/Secondary (periodic), Floquet Port and Incident Plane Wave.

### R-hzn2-7 — setups

Setups translate to `.c3d` setups for brief 7:

| Hertzon setting | `.c3d` setup |
|---|---|
| Single / multi / broadband adaptive frequency | the setup frequency |
| `MaximumPasses` | `AdaptiveMesh` |
| `BasisOrder` | `ElementOrder` |
| Sweeps (discrete, interpolating, fast; linear, count, log, points) | the frequency plan and `AdaptiveSweep` |

**Settings without an equivalent are not refused**, because they do not change the geometry. They stay in the `.hzn`,
and brief 7 reports them as "not used by circuitRF's run" with their values.

### R-hzn2-8 — the CLI and MCP read `.hzn`

The existing seams gain the kind; no verb holds translation logic of its own:

- `DocumentKinds.Classify` recognises `.hzn` by content (`format: hertzon-project`).
- `find` lists its designs.
- `check <file.hzn> [--design D]` runs the translation and reports the refusal items as errors. A design that
  translates is then checked by the existing `.c3d` validators.
- `explain` reports what each design became.

## Gates

- `HznToC3dTests`: one test per mapping row, against hand-written fixtures.
- The all-unsupported fixture is refused, with every item listed and grouped.
- Index resolution is exercised by a fixture whose boundary names a box face by index and `sig`, where the index alone
  would pick a different face. It resolves to the right face, and an ambiguous `sig` is refused.
- **The coverage measurement:** for every 3D design under `CIRCUITRF_HERTZON_HOME`, record in `RESOLVED.md` whether it
  translates, and the refusal items that stop the rest, counted by kind. This decides what the next additions to the
  table are. Skipped without the variable.
- `check` on a `.hzn` exits 1 with the refusal and 0 on a design that translates; byte-for-byte against the in-process
  call, as every verb gate is.

# Brief 5 — see it before solving it: `render` sections and `explain` on a 3D setup

**Series:** [3D EM, first series](brief-em3d-0-overview.md) · **Tag:** `R-em3d5-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §4.6 ("a picture without a GPU", "the size before the run"), §4.3
**Area:** `src/Engine/Em3d/Em3dTessellation.cs` (new), `src/Render/Renderers/Em3dSectionRenderer.cs`
(new), `src/Cli/Render.cs`, `src/Cli/Explain.cs`, `src/Cli/DocumentKinds.cs`
**Depends on:** 3 (4 for wires; the gates that need wires switch on when it lands) · **Blocks:** —

---

## 0. What this brief delivers

A client (a script, an agent on the MCP server, or the owner at a terminal) can **see** and
**size** a 3D setup with no solver installed and no window:

```
circuitrf render amp.cem -o top.svg --section z=35um
circuitrf render amp.cem -o side.svg --section xz@y=1.2mm
circuitrf render amp.cem -o iso.svg --iso
circuitrf explain amp.cem            # solids, materials, ports, air box, wires, the size
```

A client cannot check a model it cannot see (§4.6), and it should learn that a run will not fit
before the hour, not after it.

---

## 1. `R-em3d5-1` — one tessellation, below the firewall

**`R-em3d5-1a`** `Em3dTessellation.Of(Solid) → TriangleMesh`, in `src/Engine/Em3d/`. It is
deterministic (fixed segment counts per primitive, stated as constants) and tags each triangle with
its solid's name. It is written **once** because three consumers need it: this brief's sweep and
sphere sections, brief 9's polyhedra for hexagonal wires (§6.6: "circuitRF generates the mitred
prisms itself"), and F2's viewer.

**`R-em3d5-1b`** Mitred joints at a sweep's polyline vertices: the section is placed on the bisector
plane, so consecutive prisms share a face exactly with no gap and no overlap. A test checks the
shared-face vertices are bitwise equal.

---

## 2. `R-em3d5-2` — `render` on a `.cem` whose `Solver3D` is set

**`R-em3d5-2a`** `DocumentKinds.Classify` already routes `.cem`. `render` accepts one **only when
it is a 3D setup**. A planar `.cem` is a refusal naming the layout to render instead, which is what
it would draw anyway.

**`R-em3d5-2b` Three view kinds, exactly one per invocation**, refused together rather than ordered
(the `--fit`/`--window` rule `render` already follows):
- `--section z=<length>`: the XY plane at that height;
- `--section xz@y=<length>` / `yz@x=<length>`: vertical cuts;
- `--iso`: an isometric **outline**, meaning every solid's silhouette and sharp edges projected
  orthographically. It has no hidden-line removal, and says so in the picture's caption, because a
  wire-frame that looks like a shaded model misleads.

**Every length carries an SI unit; a bare number is a refusal**, as on a layout (`render`'s existing
rule, for the same reason).

**`R-em3d5-2c` The cut is analytic where the primitive allows it**: extruded polygons, boxes and
cylinders are cut exactly. Sweeps and spheres are cut through the tessellation. The **interface
convention** is stated once and applied everywhere: a solid is present at z when
z_bottom ≤ z < z_top, and a **sheet** is drawn when the plane is within a tolerance of its z. The
obvious request, *z = the top of the copper*, would otherwise flicker between "copper" and "the
layer above" on rounding.

**`R-em3d5-2d` What is drawn:** conductors in their drawing layer's technology colour (the same
`ColorTheme`/layer palette `LayoutRenderer` uses; do not add a second palette), dielectrics and
bodies as light fills keyed by material, air not filled, the air box outline with each face's
boundary kind written beside it, and each port as a hatched rectangle with its number. A legend
lists materials by name. Drawn by a new `Em3dSectionRenderer` in `src/Render`, **using
`SvgFontNormalizer` and the existing SVG/PDF/PNG output path**. It is not a second output
pipeline.

**`R-em3d5-2e`** Deterministic output: the same setup gives the same bytes, so the gate can compare
the CLI process against the in-process call byte for byte. That is `RenderCliVerbTests`' existing
pattern.

---

## 3. `R-em3d5-3` — `explain` on a 3D setup

**`R-em3d5-3a`** `explain amp.cem` on a 3D setup reports, **in addition to** what it reports for a
planar `.cem` today (both walks, the solve region, the return plane):

| Section | Content |
|---|---|
| solver | `Solver3D`; and §4.3's guidance line for this geometry (§3b) |
| temperature | `OperatingTempC` and where it came from (field or default); every conductor's σ at it; entries with no α (brief 3 §4) |
| materials | each name used, its resolved values, and **where it resolved from** (technology, `.wBond`, or a stackup entry's own numbers) |
| solids | name, role, material, primitive, bounding box, construction order; sheets flagged with the reason (brief 3 §5f) |
| wires | per wire: cross-section, each end's style, foot length **and which level set it** (wire/array/process/built-in), ball size, **both** loop heights labelled (brief 4 §5b) |
| ports | number, the two objects it spans, Z0, reference plane |
| air box | extent, each face's boundary, and any enlargement a backend section requested |
| size | §3c |

**`R-em3d5-3b` §4.3's guidance is a sentence, not a decision.** For example: *"bond wires present:
FEM (Palace) fits curved metal better; FDTD will need a fine grid near each wire"*, or *"Manhattan
geometry on a stackup: FDTD (openEMS) fits; one run covers the band"*. The rule is a short table
in one function, mirroring §4.3 row for row, with each row cited. It never changes `Solver3D`.

**`R-em3d5-3c` The size, before the run.**
- **Palace:** an **estimate**, labelled as one: tetrahedra ≈ Σ(volume per region ÷ initial element
  volume there), from the Palace section's initial size fields (brief 7), with the unknown count at
  the configured element order. Say plainly that adaptive refinement will grow it and that the run's
  first line reports the real initial count. **No Gmsh is run to get it.**
- **openEMS:** the **exact** cell count and time step, from brief 8's grid generator. That part of
  this table is **brief 8's to add**. Until brief 8 lands, the row says "grid not yet available in
  this build", and does not say "0".
- **Memory**, each backend, from the count and a per-unknown figure that is a named constant with a
  comment on its provenance (F0's measured runs, brief 1 R-em3d1-2b). A figure with no measurement
  behind it is not printed.

**`R-em3d5-3d` `explain` starts no process.** It must not run Gmsh, Palace or openEMS, and it writes
nothing. Gate 6 holds that with a counter on the process launcher, not with a timing.

**`R-em3d5-3e`** `--json` carries every row, with lengths in base SI **with the unit and scale**.
That is `explain`'s standing rule; a scale read without its unit once produced a 2 Hz run.

---

## 4. Gate

`tests/Ui.Tests/Em3d/Em3dRenderExplainTests.cs`. No solver.

1. **Section z** through brief 3 gate 1's microstrip, at mid-substrate: one substrate fill, no
   copper. At the copper's mid-height: the strip, and ports drawn.
2. **Interface convention**: z exactly at the substrate top draws the copper and not the substrate
   (§2c).
3. **CLI == in-process, byte for byte**, for one section and one `--iso`, SVG and PDF.
4. **A bare-number section is a refusal**; two view kinds together are a refusal; a planar `.cem`
   is a refusal naming its layout.
5. **`explain` lists** every solid, material, port and the air box for brief 3 gate 3's via
   transition, and the `--json` form round-trips through `System.Text.Json`.
6. **Zero processes**: a launcher counter reads 0 after `explain` and after `render`.
7. **Mitred sweep joints** share bitwise-equal vertices (§1b).
8. *(when brief 4 lands)* **Wire rows** show both loop heights and the foot-length source level.

## 5. Scope

- **No GPU, no viewer, no shading.** F2.
- **No mesh or field display.** There is nothing to show before a run, and after one it is F2's.
- **No Gmsh invocation from `explain`** (§3d).

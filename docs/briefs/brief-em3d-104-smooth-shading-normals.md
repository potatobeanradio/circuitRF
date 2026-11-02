# Brief 104 — Smooth shading normals for the realistic view

**Tag:** `R-em3d104-n` · **Series:** the realistic view ([overview 103](brief-em3d-103-overview.md)).
**Area:** `src/Render/Scene3D/Scene3DModel.cs`, `Scene3DBuilder.cs` (`Object`, ~970–1100), `Scene3DPatch.cs`, new
`src/Render/Scene3D/ShadingNormals.cs`; `src/Ui/Viewer3D/Viewer3DBackend.cs` and the three backends (upload and patch only);
`tests/Ui.Tests/Viewer3D/`, `tests/` for `src/Render` (whichever project holds `Scene3DBuilder`'s tests: grep)
**Depends on:** — · **Blocks:** 106, 110, 111

## Why

The 3D view's normal is computed per pixel from screen-space derivatives (`scene.wgsl` `fs_color`:
`normalize(cross(dpdx(world), dpdy(world)))`), so every triangle is flat. That is fine for a Lambert headlight, and it is
the engineering look. It is wrong for anything glossy: a 32-facet cylinder or a 32 × 16 sphere shows every facet in its
highlight. The realistic view (106) needs a per-vertex normal that is **smooth across a curved face and sharp across an
edge**.

`Scene3DVertex` is 24 bytes and "every other draw relies on" that stride (`Scene3DModel.cs`). This brief therefore **does
not change it**. It adds a second, parallel vertex stream that only the realistic pipelines read.

## 0. What already exists — read this first

| Piece | Where |
|---|---|
| One scene vertex per (mesh vertex, face) for a faced object: "a vertex's face must be its triangle's" | `Scene3DBuilder.cs` ~1010–1022 |
| The unfaced path (a welded mesh, `NoFace`: ball bonds, ports, arrows) | `Scene3DBuilder.cs` ~991–1008 |
| Corners welded BY POSITION for feature edges (a polyhedron repeats each corner per face) | `Scene3DBuilder.cs` ~1026–1033 |
| The feature-edge rule: a turn sharper than **30°** | `Em3dSectionScene.FeatureEdges`; `Em3dSurfaceField`'s header |
| Tessellation of primitives (cylinder 32 segments, sphere 32 × 16) | `src/Engine/Em3d/Em3dTessellation.cs` |
| Kernel meshes carry a face per triangle (`faceOfTri`) | `tools/geometry-worker/geometry_worker.cpp` ~1663–1722 |
| Geometry uploads once per generation, patches by range | `Viewer3DBackend.UploadScene` / `PatchScene`; `Scene3DPatch` |
| Upload byte counters | `FrameCounters`, `Viewer3DBackend.Counters` |

## 1. `R-em3d104-1` — the rule

A shading normal is computed **per scene vertex**, from the triangles that use it:

- **a.** Triangles are grouped by the scene vertex's face, as the builder already splits them. **Edges between faces are
  always sharp.**
- **b.** Within one face, triangles whose normals turn by more than **30°** (the feature-edge threshold, one constant
  shared with it, not a second literal) are also split. A prism whose walls share one face name, or an imported B-rep face
  with an internal crease, keeps its corners. A 32-segment cylinder (11.25° per facet) and the sphere come out smooth.
- **c.** The unfaced path uses the same crease rule over its welded vertices.
- **d.** The normal is the **area-weighted** sum of the grouped triangles' normals (the unnormalised cross product),
  normalised. A degenerate triangle (zero area) contributes nothing. A vertex left with no contribution takes its first
  triangle's plane normal, or +z if that triangle is degenerate too. Never NaN; a test holds it.
- **e.** Where (b) or (c) splits a vertex, the builder **duplicates the scene vertex**. Position, id, colour and face are
  identical, so the default view draws the same pixels (its normal comes from derivatives). The index buffer changes only to
  point at the duplicate.
- **f.** The normal is in the object's **scene-local** frame, as positions are. A per-draw transform (`MX.m`, a drag
  preview) is rigid, so the vertex shader rotates the normal by the same matrix's upper 3 × 3. If a non-rigid transform
  can reach `MX.m`, use the inverse transpose; check `Scene3DFramePlan.Transforms`' writers and record which applies.
- **g.** Orientation: a solid's triangles wind outward, so the normal points out. A **sheet** is two-sided: the shader (106)
  flips the normal on a back face (`front_facing`). This brief only records the rule.

The function lives in `src/Render/Scene3D/ShadingNormals.cs`, is pure and deterministic (index order, no hashing order), and
is the **only** place a shading normal is made. 110's CPU mirror and 111's glTF writer call it, and neither has a second
copy (rule 3 of the overview).

## 2. `R-em3d104-2` — the stream

- **a.** `Scene3DShadeVertex { float Nx, Ny, Nz; uint Slot; }`, 16 bytes, `StructLayout(Sequential, Pack = 4)`, in
  `Scene3DModel.cs` beside `Scene3DVertex`. `Scene3DModel.ShadeVertices` is **parallel to** `Vertices`: same length, same
  index.
- **b.** `Slot` is the object's appearance slot (overview D16). This brief writes **0** everywhere; 105 and 106 fill it.
  Declaring it now means the stream's layout is set once.
- **c.** Built in `Scene3DBuilder.Object` alongside the vertices, for objects with triangles. Lines, overlays, images
  and fields get no shade vertices; their draws never bind the stream.
- **d.** Cost: computed once per geometry generation, never per frame. A hover must not move
  `Scene3DBuilder.Tessellations` (gate 5's existing counter) and must not recompute normals. Add a `ShadingNormals.Built`
  counter and hold both.

## 3. `R-em3d104-3` — upload, lazily

- **a.** `Viewer3DBackend` gains `UploadShade(Scene3DModel)` and, for `PatchScene`, patches the same ranges of the shade
  buffer. A backend that cannot patch re-uploads the whole stream, which is always correct.
- **b.** **The stream is uploaded only while the realistic view is on** (106 turns it on). With it off, a scene's upload
  bytes are exactly what they are today. That is a gate: the default view must cost nothing extra in memory or bandwidth.
- **c.** Turning the realistic view on uploads the current stream once. Later generations and patches keep it in sync while
  it is on. Turning it off releases the buffer.
- **d.** Binding: the realistic pipelines (106) declare a second vertex buffer. How naga's per-backend vertex-buffer indices
  are chosen is in `tools/ShaderGen`'s README; extend it there and record the mapping beside the existing per-draw uniform
  note at the top of `scene.wgsl`. **No existing pipeline's vertex layout changes.**

## 4. Gate

Builder-level tests (no GPU) unless stated.

1. **Cylinder.** Every side vertex's normal is radial within 1e-6 and every cap vertex's is axial. The cap rim has two
   vertices per position (side and cap).
2. **Box.** 24 shade vertices, normals axis-aligned and outward.
3. **Crease within a face.** A prism whose walls share one face name keeps its vertical corners sharp: normals at a corner
   differ by the wall angle. Before writing this test, check whether prism walls are one face or one per wall; if every
   wall has its own name, use an imported polyhedron with a creased face instead.
4. **Sphere.** At every vertex the normal is within 1e-3 of (position − centre)/r, and the poles are exact.
5. **Unfaced.** A ball bond's welded mesh is smooth (no vertex split by the crease rule).
6. **Degenerate.** A mesh with a zero-area triangle yields no NaN anywhere.
7. **Determinism.** Building the same scene twice gives byte-identical `ShadeVertices`.
8. **Parallel.** `ShadeVertices.Length == Vertices.Length` for every fixture, and every index addresses both.
9. **Lazy upload.** For a fixture scene, `Counters`' upload bytes with the realistic view off equal the bytes without the
   shade stream. Turning it on adds exactly 16 × vertex count.
10. **Patch.** A patched object's shade range is rewritten and nothing else is (count the bytes).
11. **Default view unchanged** (macOS, Metal offscreen, the existing `Viewer3DFrameGateTests` pattern): a fixture drawn with
    the default plan reads back identical before and after vertex duplication. Build the "before" in the same test by
    disabling duplication (an internal switch), not from a stored PNG.

Run only the classes touched (`--filter`), plus `Firewall.Tests --no-build`.

## Decisions — settled by the owner, 2026-10-04

- **D9 (overview)**: smooth normals are used by the realistic view only. *Recommended* as stated; the default view and every
  DocGen figure stay byte-identical.

## On completion

Record the rule and the vertex-duplication count on the shipped examples in `src/Render/RESOLVED.md`, and the
buffer-index mapping in `src/Ui/Viewer3D/RESOLVED.md`. Never CLAUDE.md. Do not commit unless the owner asks.

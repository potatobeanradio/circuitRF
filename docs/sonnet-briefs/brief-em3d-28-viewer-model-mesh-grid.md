# Brief 28 — the viewer: the model, the mesh and the grid

**Series:** [3D EM, second series](brief-em3d-20-overview.md) · **Tag:** `R-em3d28-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §8.1–§8.5; `em-3d-f2-spike-findings.md` (brief 27)
**Area:** `src/Render/Scene3D/` (new: no GPU, no Avalonia), `src/Engine/Em3d/Em3dTessellation.cs`
(extruded-polygon caps), `src/Ui/Viewer3D/` (new: the GPU backends and surface only), the `.cem` panel
(*Show 3D*), the Dock document factory, `tools/Viewer3dSpike/` (step 0 only)
**Status:** BUILT 2026-09-25, uncommitted. Owner checks (§7) and step 0's Windows / Linux-GPU runs
pending; D3D11 never run; pixels seen only offscreen (Metal, and Vulkan on a software driver).
Findings: `src/Ui/RESOLVED.md` §"The 3D view — brief-em3d-28"; step 0: `em-3d-f2-spike-findings.md` §8.
**Depends on:** 27; **D3 decided 2026-09-25 — route A primary, route B secondary** (§1c) · **Blocks:** 29

---

## 0. What this brief delivers

A **3D view** document, opened from a 3D `.cem` **before any solve**. You see what you are about to
solve, which is the point of §4.6. It shows:
- the generated solids, coloured by material, with dielectrics and air translucent or hidden;
- the ports as named sheets with direction arrows;
- the air box and its boundary kinds;
- after a Palace run, **the mesh Gmsh made**; for an openEMS setup, **the grid circuitRF made**, drawn
  where it meets the metal. That is how an FDTD user sees whether the grid landed on the edges (§8.5);
- hover highlights, click selects, and the object tree shows what was picked;
- a clip plane to see inside a package.

---

## 0b. `R-em3d28-0` — first, prove route A where it has not run

D3 chose route A (each platform's own GPU API behind composition interop) knowing that only its macOS
half was built. **Before any viewer code grows, extend `tools/Viewer3dSpike/` with the two missing
halves**, in the spike's shape, so the owner can run them:
- **Windows: D3D11.** Avalonia's default there is ANGLE over D3D11, which imports a D3D11 texture by
  shared handle (`D3D11TextureGlobalSharedHandle` / `D3D11TextureNtHandle`) with a keyed mutex. A
  harness backend and an `InteropPane` image source.
- **Linux: Vulkan.** Avalonia's default there is GLX; it imports `VulkanOpaquePosixFileDescriptor`
  images and semaphores where the GL driver offers the external-memory extensions. A harness backend
  and an image source. **The pane prints what the compositor offered** (it already does); if GLX
  offers nothing, try Avalonia's `X11RenderingMode.Vulkan` before calling the platform failed.
- Shaders from the one WGSL source (§1c), not hand-written per language.

The same four counters, the same asserts, Debug and Release, and the owner's hand checks on each OS
recorded in `em-3d-f2-spike-findings.md` §7. **If a half cannot be made to present, that platform
takes route B now** (§1c) and the findings say why.

## 1. `R-em3d28-1` — the split across the firewall (§8.5)

**`R-em3d28-1a`** `src/Render/Scene3D/` holds everything that is not a GPU call:
- the scene: meshes as flat vertex and index arrays, object IDs, material slots, visibility, a
  generation number;
- camera math: orbit, pan, zoom-to-cursor, fit, standard views, perspective and orthographic;
- picking-ray math, for the CPU fallback and for tests;
- clip-plane math;
- colour maps (brief 29 uses them);
- the `.msh` reader (§3).

It references **no GPU API and no Avalonia**, and the firewall test holds that. This is what keeps a
later headless `render` of a 3D view possible without a second renderer (§8.5).

**`R-em3d28-1b`** `src/Ui/Viewer3D/` holds only the GPU backends, buffers, shaders, the composition
surface and input translation. **One backend interface, three backends** — Metal (macOS), D3D11
(Windows), Vulkan (Linux) — chosen at run time by platform, so a route-B backend can slot in beside
them without touching the scene model. The pane's shape is the spike's `InteropPane`: a render thread
of its own, a three-image swapchain imported into the compositor once, and a UI-thread share per frame
that only hands a finished image over.

**`R-em3d28-1c` D3, as decided.** Route A is primary and adds **no native package**: the platform APIs
are the OS's, reached through hand-written interop (as the spike's `ObjC.cs`) or a managed binding
named in the completion note. **Shaders are one WGSL source** in `src/Ui/Viewer3D/Shaders/`,
cross-compiled offline by naga to `.metal`, `.hlsl` and `.spv`, and the outputs are committed; each
generated file records the hash of the WGSL it came from, and a test fails when they disagree (an edit
to the WGSL without regenerating). naga's CLI has no MSL binding map (findings §5.4): use its library
from a small build tool, or a checked fix-up — not a hand edit. **Route B** (WebGPU through
wgpu-native) is built now only for a platform where step 0 shows route A cannot present; it is built
for every platform in a later brief. Its native library is accepted for that use, is referenced by
`src/Ui` only, and ships in that platform's installer. A test asserts that no project below the
firewall references any GPU API or native GPU library.

**`R-em3d28-1d` Rules brief 27 learned the hard way** (findings §5):
- **The shared image's alpha stays 1.** Blend alpha with `ONE, ONE_MINUS_SRC_ALPHA`, or every
  translucent object punches a hole through the pane.
- **Check every native handle before encoding with it.** A message to nil is a silent no-op; a
  missing ready signal made the compositor wait forever and froze the whole window. A present step
  that cannot signal is a fault, reported, never a timeout loop.
- **Measure pick latency paced**, as the compositor paces the app.
- **Avalonia 12's macOS compositor is Metal by default** (its API documentation says OpenGL). The
  owner's run showed it imports IOSurface with MetalSharedEvent timeline semaphores — use those.
- **`Update*Async` allocates a `Task` per presented frame** (408 B measured). Record it on the UI lane;
  it is Avalonia's, not a defect of the pane.
- **Keep the device and uploaded buffers in an object that outlives the view**, so a Dock float or
  re-dock re-imports three images and uploads no geometry.

---

## 2. `R-em3d28-2` — the model from the problem

**`R-em3d28-2a`** The scene is built from `Em3dProblem` through `Em3dTessellation`, the one
tessellation (R-em3d5-1). It gains what brief 5 left out: **the caps of an extruded polygon, with
holes**. There is no triangulator in the repo today, so write one:
- ear clipping with hole bridging;
- deterministic like the rest of `Em3dTessellation`: no hash ordering, constants named;
- tested on the shapes a layout produces: slivers, collinear runs, a hole touching the outline at one
  vertex, and a 10,000-vertex board outline.

**Do not bring in a package for this.**

**`R-em3d28-2b` Batched per object and material, never per face** (§8.2 point 1). One draw per
(object, material) pair. Gate 2 counts draws.

**`R-em3d28-2c`** Colours come from the technology's layer colours for conductors, and from a fixed,
documented palette for dielectrics and air. It follows the application theme's light and dark modes as
the 2D editors do.

**`R-em3d28-2d` Regeneration off the drawing path** (§8.2 point 4, §8.4). A change to the `.cem`,
`.clay`, `.ctech` or `.wBond` regenerates the problem in the background with a **generation number**.
The view keeps drawing the last finished scene, and a result for a superseded generation is discarded
(gate 4).

---

## 3. `R-em3d28-3` — the mesh and the grid

**`R-em3d28-3a` Gmsh mesh.** A reader for **MSH 2.2 ASCII**, the format `GmshGeoWriter` asks for
(`Mesh.MshFileVersion = 2.2`). It reads nodes, tetrahedra and boundary triangles with their physical
tags. It shows:
- **boundary triangles per physical group** as a wireframe over the solids;
- **the tetrahedra cut by the clip plane**, as their section edges;
- the element count, which must equal the count the run summary reports (brief 21).

A malformed file refuses with the line number. The reader streams, because a 1.5 M-tet mesh must not
be read into a string.

**`R-em3d28-3b` Mesh availability.** The mesh exists only after Gmsh has run. Before that, the mesh
toggle is disabled, with a tooltip saying *Simulate to mesh*. There is no mesh-only command in this
brief.

**`R-em3d28-3c` FDTD grid.** From brief 8's `FdtdGrid` for the setup, computed on demand. It is exact,
and needs no run. The lines are drawn **on the clip plane and on conductor surfaces**, not through the
volume (a million-cell grid through the volume is noise). The smallest cell on each axis is labelled,
because one tiny cell sets the time step for the whole run (§12).

---

## 4. `R-em3d28-4` — interaction

**`R-em3d28-4a`** Orbit (left drag), pan (middle or Shift-drag), zoom to cursor (wheel or pinch), fit
(F), and standard views (top, front, right, iso) from the toolbar and keys. Trackpad gestures work on
macOS. Keyboard first, and no modal dialog mid-gesture (§8.2 point 5).

**`R-em3d28-4b` GPU picking** (§8.2 point 2), as proven in brief 27. Hover draws a highlight through a
shader state change, and the tooltip shows the solid's name, material, εr, tanδ and σ at the setup's
operating temperature. Click selects, and the object tree beside the view scrolls to it.

**`R-em3d28-4c` The object tree:** solids by kind (conductor, dielectric, air, body, wire), ports and
boundaries, each with a visibility toggle. Air and the outermost dielectric start hidden.

**`R-em3d28-4d` A clip plane:** one plane on any axis or perpendicular to the view, dragged with a
handle. Cut faces are capped in the cut solid's colour.

**`R-em3d28-4e`** Scale bar, axis triad, and the dimension under the cursor in the layout's display
units.

---

## 5. `R-em3d28-5` — where it opens

- *Show 3D* on a 3D `.cem`'s panel opens the view as a document tab beside the setup.
- Opening a Palace or openEMS run directory from the Project Tree opens the same view on that run's
  problem and mesh.
- It is read only (F4 is the editor).
- The view's camera is saved in the workspace's window state, not in the `.cem`, because it is not
  part of the design.

---

## 6. Gate

`tests/Ui.Tests/Viewer3D/` for the scene model (headless), `tests/Firewall.Tests` for the split. These
are the §8.6 counters, not timings.

0. **Step 0.** Route A's D3D11 and Vulkan halves pass the spike harness's asserts on their OS (owner
   runs recorded), or the platform's route-B fallback does and the findings say why.
1. **Firewall.** `src/Render` references no GPU API and no Avalonia; no project below the firewall
   references a GPU API or native GPU library; route A adds no native package.
1b. **Shaders current.** Every committed `.metal`/`.hlsl`/`.spv` carries the hash of the WGSL it was
   generated from, and it matches.
2. **Batches.** The scene for F0 case A and for series 1's via transition has one draw per
   (object, material), independent of triangle count.
3. **Orbit uploads nothing.** With the GPU layer behind a recording fake, 100 camera changes upload 0
   bytes.
4. **Superseded generations.** Two quick edits: the first generation's scene is never handed to the
   renderer (counter).
5. **Hover does no geometry work.** 1,000 hover moves cause 0 tessellations and 0 problem generations.
6. **Caps.** The triangulator's area equals the polygon's area (minus holes) to 1e-12 relative on every
   fixture shape, with no triangle outside the outline.
7. **`.msh` reader.** F0 case A's mesh gives the node, tetrahedron and boundary counts Gmsh's own log
   printed (`gmsh.log` is committed beside it).
8. **Grid lines.** The drawn line set equals `FdtdGrid`'s for series 1's via setup, and the smallest
   cell label matches its minimum spacing.
9. **Picking math.** A CPU ray through a known pixel hits the solid the GPU ID buffer would name, in
   the scene-model test.

## 7. Owner check (pixels not seen from this session)

On each OS, on the route that OS runs (A, or B where step 0 fell back):
- orbit the F0 case A model and the via transition;
- hover, click, the object tree and the clip plane;
- float the tab in Dock and re-dock it;
- type into the Messages filter while orbiting;
- "does it feel fast" in the **Debug** build.

## 8. Scope

- No fields (brief 29). No editing (F4). No headless `render` of the 3D view (a later brief the split
  keeps possible).

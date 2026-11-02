# Brief 138 — A board's side walls: carved where copper reaches them, and drawn with no polygon offset

**Tag:** `R-em3d138-n`
**Status:** Written 2026-10-26.
**Area:** `src/Render/Scene3D/` — `Scene3DBores.cs` (the model to follow, and the carve this one composes with),
`Scene3DBuilder.cs` (the solids loop ~378-436, `Accumulator.Finish` ~1306-1320), `Scene3DModel.cs` (`Scene3DBatch`),
`Scene3DFramePlan.cs` (`TieOf`, every draw built from a batch), `Scene3DPicking.cs`, `Edit/Scene3DIdPatch.cs`,
`Look/RealisticPicture.cs`, `Scene3DPatch.cs`. Findings in `src/Render/RESOLVED.md`.
**Depends on:** `54284f11` (the via-pad carve, `Scene3DBores.Notched`, `BehindSlopeScale` 2 → 1/16).
**Out of scope:** the solvers, the document, `Em3dLayoutSolids` (the problem stays bit for bit what it is), and the value
of `BehindSlopeScale`.

---

## 0. The gap

A multilayer board in the 3D view, realistic look, shows a dotted line of single light pixels along the top of every
inner copper layer where it reaches the board's side. Windows screenshots from 1.5.6 made it look like a D3D11 fault.
**It is not**: Metal draws the same dots. A `UiSnapshot --c3d` render at 1× of the owner's ten-layer board at its
stored camera has 335 against the 352 in the first Windows picture, and the two look the same zoomed in. A high-DPI (2×) screen
hides them: each dot is a quarter the area.

**Every dot is the lit copper top's own colour.** It is the leak `54284f11` narrowed and could not close:

- An inner copper band is absorbed into the dielectric above it (`Em3dLayoutSolids`, "an INNER conductor band … is
  filled … by the dielectric its sheet faces away from"), so the copper is flush with its slab's bottom, and where it
  reaches the board outline its side face lies on the slab's side wall.
- The slab draws with `Scene3DDepthTie.Behind`'s polygon offset so the copper's side wins that coincidence.
- The copper's TOP face is hidden inside the slab, and meets the wall along a line. Within the offset of that line the
  hidden top passes the depth test. That is a band about `BehindSlopeScale` pixels wide (plus the constant's share), lit
  from above.

**Lowering the factor only thins the band, it never closes it** — side-wall dots on the owner's board at 1× on Metal:

| `BehindSlopeScale` | Dots on the side walls |
|---|---:|
| 1/16 (shipped) | 195 |
| 1/32 | 97 |
| 1/64 | 49 |
| 1/128 | 26 |

And the factor cannot go much lower: at 0, the copper's side faces fight the wall (`54284f11`'s own measurement).
**Any face that ends on a biased face leaks.** The fix is the one `Scene3DBores` already made for via pads: carve, don't
bias. Where copper lies on a slab's wall, the wall is not drawn (the copper's side face is that surface). What is
left of the wall coincides with nothing, so it needs no offset at all, and a face that ends on an unbiased face whose
edge it shares cannot come through.

## 1. Reproduction

The owner has the ten-layer board's workspace; ask for it, and **do not commit it or name it anywhere**. Gates use
synthetic fixtures (§3). For the picture:

```
dotnet run --project tools/UiSnapshot --no-build -- --c3d <the board's .c3d> -o out.png --realistic
```

It renders at 1×, which is what makes the dots countable. Count pixels of the lit copper-top colour with the copper's
dark side colour directly below and a saturated dielectric colour directly above, **restricted to the side walls** (the
board's top surface has legitimate copper-over-side-colour edges that a naive count picks up). Rebuild after any source
swap before rendering; `--no-build` has drawn stale geometry here before.

## 2. What to do

### R-em3d138-1 — Carve each slab's outline walls where a conductor's side face lies on them

View only, like `Scene3DBores`: nothing reaches a solver or the document. A new sibling of `Scene3DBores`, applied in
the same place in the solids loop, after the bore carve (a slab can need both, so take the bore carve's output when there
is one).

- **Which slabs:** a `Dielectric` solid whose primitive is an `Em3dExtrudedPolygon` (what the layout generator writes,
  `Em3dLayoutSolids.Extrude`), or an `Em3dBox`, carved as its rectangle extruded the way `Scene3DBores` already does.
- **Which conductors:** `Conductor` solids that are `Em3dExtrudedPolygon` or `Em3dBox`, whose z-range overlaps the
  slab's, and one of whose outline edges is collinear with one of the slab's outline edges (`Tol` = 1e-9 m, as
  `Scene3DBores`; the generator writes both from one number). The coincident region is the overlap interval along the
  edge × the overlap in z.
- **The wall:** each original outline edge's quad, minus those rectangles, as rectangles. **No T-junctions:** every
  conductor vertex on that edge line, at both its z's, is a vertex of the wall pieces that meet it. That is the same rule
  `Notched` keeps ("every original and ring vertex mapped back exactly"), and a missed one leaks the same dots through a
  crack instead of through a bias.
- **Face ids:** each wall piece keeps its original edge's face, so `FacesOf`, hover, measure and snap still name it.
- **Be conservative.** If any OTHER conductor shape (a cylinder, a sweep, a STEP body, a sheet standing on its edge)
  touches a wall's plane within `Tol`, that wall keeps today's treatment: uncarved and biased. A missed coincidence on
  an unbiased wall would fight visibly, which is worse than the dots.

### R-em3d138-2 — Those wall pieces draw with no polygon offset

The tie is per object today (`TieOf(scene, id)`). It becomes per **batch**:

- The carve marks which of the mesh's triangles are the outline walls it rebuilt. Make them a contiguous run at the end
  of the mesh, so the object emits two `Scene3DBatch`es from `Accumulator.Finish`: its other faces, and its free walls.
- `Scene3DBatch` carries whether it is free, and `TieOf` takes the batch: a free batch is `Scene3DDepthTie.None`; every
  other batch is what its object's tie is today.
- Every site that reads a batch's tie goes through it: the frame plan's colour, translucent, ID and moved-preview draws
  (`Scene3DFramePlan`), `Scene3DPicking`, `Scene3DIdPatch`, and through the plan's draws `RealisticPicture` (the CPU
  picture the gate below uses). **The shadow pass keeps `ShadowBias` for every draw**: acne wants it, and nothing
  leaks there.
- The two batches of one object must stay contiguous, so an instance group's `OpaqueFirst`/`OpaqueCount` stays a single
  range. `Scene3DPatch` compares batch arrays: check it still patches a slab edit rather than rebuilding, or say why not.

### R-em3d138-3 — Nothing else changes

- **The default (flat) view and the translucent dielectric look the same.** A translucent wall never tinted the copper's
  side (it failed the depth test there), and a carved wall does not cover it either.
- **The clip plane.** Caps are drawn from back faces, and the carved slab is no longer closed. Where the wall is missing,
  the copper occupies that volume, so the copper's cap is what belongs there. **Check it**: clip through a copper band at
  the board's edge, both axes, and look at the cap.
- **Drawn edge lines and snap features.** A carved wall has rim edges where the copper band was. They must not become
  drawn edge lines in the default view. Compare the edge-line vertex count for a fixture slab before and after. Snap
  features may gain the copper/dielectric boundary there; that is a real edge, so say so in RESOLVED rather than
  suppressing it.
- `BehindSlopeScale` stays 1/16. The other coincidences (a copper's bottom on its slab's bottom, a slab's notches round
  edge vias) still need it.

## 3. Tests

Minimal: one test per claim, targeted classes only.

1. **The leak is gone, not thinned.** Extend `MetalPrecedenceTests.TheRealisticView_ShowsNoHiddenCopperTop_AlongTheBoardsSide`'s
   fixture (two slabs, a copper reaching the side): the CPU realistic picture shows **0** copper-top pixels (it allows
   10 today). Assert it at a few yaws on the same fixture in one test, not as a Theory per view.
2. **The copper's side still wins**, with no bias left to help it: the same picture, sampled along the copper band at
   the wall, is the copper's side colour at every sample.
3. **The geometry is watertight.** For a slab with two conductors on one edge (one of them with an extra collinear vertex
   on it): wall pieces plus copper side faces tile the original wall's area exactly, and every conductor vertex on the
   edge is a wall-piece vertex.
4. **Conservative fallback:** a cylinder conductor tangent to a wall leaves that wall uncarved and biased.
5. **The tie is per batch:** a carved slab has two batches; the free one's tie is `None`; the other's is `Behind`; the
   ID pass and `Scene3DPicking` pick the slab on the free wall and the copper on its band.

Run `MetalPrecedenceTests`, `PlatedViaAndPerspectiveTests`, the `Scene3DFramePlan`/picking/ID-patch test classes this
touches, and the `Viewer3D` patch tests. Not the full suite.

**On the owner's board (local, not committed):** side-wall dot count at 1× on Metal, realistic, before (195 at the
stored camera) and after (target 0). Also a perspective and an orthographic view at two other yaws, and the default
view. Then the owner checks it on the Windows VM. That is the platform the report came from, and D3D11 is the one
backend nothing here can render.

## 4. Text

None for users. A `src/Render/RESOLVED.md` entry that amends `54284f11`'s ("The realistic dashes were the bias itself"):
the dots that 1/16 left were the same leak, visible at 1× on every platform, and carving the walls is what closed them.

## 5. Out of scope

- Carving every metal coincidence out of every dielectric so dielectrics need no tie at all. A `.c3d` body of
  dielectric role meets metal in arbitrary ways; the tie stays its safety net.
- Horizontal coincidences (a copper's bottom on its slab's bottom). They lie inside the board and no reported view shows
  them.

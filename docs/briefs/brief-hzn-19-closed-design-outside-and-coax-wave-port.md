# HZN-19 — what lies outside a closed design, and a wave port on a coaxial line's end cap

Series: `brief-hzn-0-overview.md`. Needs HZN-11 and HZN-13.

## Why

After HZN-18, 88 of Hertzon 0.3.26's 99 example designs open. `Coax.hzn`'s `Coax50` is refused for its two wave ports. Each is a
disc of the line's outer radius on an end of a closed design: a Teflon cylinder (b = 1.675 mm, 20 mm long) that subtracts a
perfect-conductor pin (a = 0.5 mm) and keeps it, with Auto Open off and no region.

A probe with square port sheets in place of the discs translated, but it showed a second problem behind the first. In a closed
design circuitRF makes the air box flush with the model's bounding box, its faces perfect conductors. Round a cylinder that box has
four corners the cylinder does not fill, and circuitRF would solve them as air. The outer conductor would then be the square box, not
the cylinder. HZN-11's rule (a hole cut from the model-bounding object is perfect conductor) only covered holes. Every closed design
that already opened (13, counted) is filled by boxes, so none of them was wrong, but nothing guarded it.

## Established

- **A closed design's outside is perfect conductor.** Hertzon meshes only its objects, and the outer surface of what it meshes is
  perfect conductor. The translator's own note already said so ("as a closed Hertzon design's outer faces are").
- **The EMerge driver keeps a port region only over meshed space.** A region's fragments over a conductor's face are removed with the
  conductor (brief-emw-2). A rectangle over a coaxial end therefore becomes the annulus between the pin and the outer metal.

## What was built

### R-hzn19-1 — the outside of a closed design
In a closed design whose modelled solids do not tile the box round them, what they leave becomes one perfect-conductor solid,
`hzn_outside`. It is the box less a copy of every modelled solid, and it is hidden, so the model stays in view.
- **Tiling** is decided exactly on the grid the solids' faces make, from the solids that are boxes or Subtracts from boxes. Solids that
  tile their box add nothing, and all 13 closed designs that opened before still add nothing.
- **Kept tools.** A Subtract that keeps its tools draws its blank and its tools, so in the cutting copy it becomes their union.
  Otherwise the copy's pin was drawn a second time as a conductor of its own; the first EMerge plan found three conductors on the
  port.
- **Only modelled objects measure the box**, as HZN-15 established for the outer extent. A field-plot plane 0.6 µm outside
  PlaneWave's box otherwise made the box larger than the model and refused that design.
- The box is numbers at the variables' values (noted).

### R-hzn19-2 — a disc wave port
- A circle sheet's bounding square is a port sheet (`SheetGeom.Disc`). Only a wave port takes it, and only on an end cap of a top-level
  cylinder (or of a Subtract whose blank is one) with the same axis, centre and radius, lying on the model's outer extent in a closed
  design. The port's rectangle is the square, whose corners lie on `hzn_outside`.
- A disc elsewhere, a lumped port on a disc, a lumped element on one and a periodic face on one are refused by name.

## Gates

- **`HznCoaxWavePortTests`** (synthetic `coax-line.hzn`): the outside is a hidden perfect-conductor Subtract of the bounding box with
  no kept-tool Subtract left in its cut; a box-tiled closed design (the guide-post fixture) adds nothing; the disc ports are their
  squares at both ends, mode-normalised, and resolve on EMerge as the pin against `hzn_outside` (not hollow), with no validation
  finding; a disc that is no end cap, and a lumped port on a disc, are refused. Hertzon's own `Coax50` translates under
  `CIRCUITRF_HERTZON_HOME`.
- **Census: 89 of 99.** Only `Coax50` changes, and only it gains `hzn_outside`.
- **Against the analytic line**: `Coax50` as written (1–10 GHz, adaptive sweep, first order) on EMerge, 3 s. See the completion note.
- **UI:** UiSnapshot `--c3d` of the converted `Coax50`: the cylinder, the pin and the two port squares, with the outside hidden.

## Risk

- The port is drawn as a square, the rectangle the `.c3d` holds, not as the disc Hertzon draws.
- Not run on Palace. openEMS refuses a mode-normalised port, as it always has.

## Rules

As brief-hzn-10. Two other agents share the repo: stage only this brief's files.

## Completion note (2026-11-16)

Built as above. **Census 88 → 89 of 99.**

- `Coax50` on EMerge (10,583 tetrahedra, 11 full solves of an adaptive sweep, 3 s): |S21| 0.997–0.999, which the Teflon's loss
  tangent of 0.001 predicts (0.997 at 10 GHz). |S11| is at most 0.046, rippling with the 5 GHz period of the 20 mm line, so it comes
  from a small reflection at each port. ∠S21 follows −2πf√εr·L/c to within 0.9 % of the electrical length (−0.34° at 1 GHz,
  −3.2° at 10 GHz): an effective εr of 2.138 against 2.1, as a faceted circle in a first-order mesh gives.
- Neighbouring tests (Hzn, ports, wave ports, EMerge runs): 314 passed. The five `Hertzon_Describes_*` tests fail only because this
  Hertzon download has no `hertzon-cli`.
- `check` on the converted `.c3d`: 0 errors, 0 warnings.

Findings: `src/Design/RESOLVED.md` §HZN-19.

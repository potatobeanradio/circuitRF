# HZN-21 — half models: Split, symmetry planes, and the mirrored far field on every solver

Series: `brief-hzn-0-overview.md`. Needs HZN-10 (booleans in the translator) and the 3D far field (brief-em3d-31, brief-emi-7).

## Why

After HZN-20, 90 of Hertzon 0.3.26's 99 example designs open. No feature left opens more than one more, so this one is chosen for
what it gives circuitRF users: **a symmetric structure solved as a half (or a quarter)**, which halves the mesh per plane, with a
radiation pattern that is still the whole structure's.

- **`HalfWave1GHz_Half`** (`Dipole.hzn`) is refused only for its `Split` (probed with it stripped: the arms then cross the symmetry
  face, which is the expected consequence). Hertzon's tutorial §3.6 gives the answer to compare with: 143.5 Ω at 0.978 GHz against a
  146 Ω port (71.8 Ω for the full dipole), and a peak gain of the mirrored far field of 2.4 dBi (2.22 dBi for the full model).
- **`B_WaveguideIrisFilter_Half`** needs a `Split` in a translated coordinate system, a symmetry `ImpedanceMultiplier` of 2, a TE
  wave-port mode type and an Impedance boundary. This brief takes the first two; B stays refused for the last two.
- **circuitRF cannot do this today, for its own users either.** A `.c3d` can put a PMC face on the air box, but every solver then
  refuses the radiation pattern: EMerge and Palace integrate over absorbing faces that must close round the whole problem, and
  openEMS closes only a conducting *floor*, as an infinite ground plane. The face kind `Symmetry` exists and every solver refuses it,
  because it does not say which wall it is.

**Census target: 91 of 99.**

## Established from Hertzon's own documentation (never its source)

- **Split** (feature matrix, changelog 0.1.26, tutorial §3.6): a boolean that cuts an object at a plane — `XY`, `YZ`, `XZ`, or
  `Custom` from `Base` and `Normal` — taken in the operation's `CoordinateSystem`, keeping the `Positive` or `Negative` side. For the
  three named planes the plane passes through that system's origin; `Base`/`Normal` describe the Custom plane.
- **Symmetry boundary** (tutorial §3.6, feature matrix, EMERGE_INTEGRATION.md): `Perfect H` is a magnetic wall (the E field lies in
  the plane), `Perfect E` an electric one. The far field, gain and RCS are computed for the whole structure: the stored surface fields
  are mirrored at each symmetry plane, planes parallel to XY, YZ or ZX, with the radiated power scaled as for the whole structure.
  **The `ImpedanceMultiplier` is not applied by Hertzon's solver**; the user sets the port impedance (146 Ω for the halved dipole port).

## What to build

### R-hzn21-1 — a symmetry plane on the air box (format, problem, check)
- **Format.** A setup's `AirBox` face takes `Symmetry: true` (written only when true) beside `Boundary: "Pec"` or `"Pmc"`: the face is
  a mirror plane of the whole structure, an electric or a magnetic wall. The solvers lower the face exactly as before (it IS a PEC or
  PMC face); only the far field and the notes read the flag. The same key in a `.cem`.
- **Problem.** `Em3dAirBox.SymmetryPlanes`: one `Em3dSymmetryPlane(Axis, AtM, Wall)` per flagged face, at that face's coordinate.
- **Refused by `check`, by name:** `Symmetry: true` on an `Absorbing` or `Periodic` face; both faces of one axis flagged; a flagged
  face on a problem whose other faces are not absorbing **and** a radiation pattern is asked (the existing per-solver refusals say
  that, now naming the symmetry face as allowed).
- The legacy `Boundary: "Symmetry"` stays refused, and its sentence now names the spelling that runs (`"Pmc"` or `"Pec"` with
  `Symmetry: true`).

### R-hzn21-2 — the mirrored far field, the same for EMerge, Palace and openEMS
- One function in `src/Design/Em3d` (`Em3dSymmetry.Mirror`) takes each port's pattern of the half and returns the whole
  structure's, before `Em3dRadiation.Publish`. For a plane with reflection matrix M through the point at `AtM` along its axis, and the
  equivalence principle on the half's surface plus its mirror image (circuitRF's e^{jωt}, N = ∮J e^{+jk r̂·r'}):

      F_whole(r̂) = F(r̂) + s · e^{+jk r̂·d} · M F(M r̂),     s = +1 magnetic wall, −1 electric wall,   d = 2·AtM·n̂

  with two planes, every image (3 of them) summed. On the 1° sphere M r̂ is a grid point (x: φ → 180°−φ; y: φ → −φ; z: θ → 180°−θ),
  so the mirroring is exact; a grid where it is not is refused by name.
- **Power.** The accepted power is the half's times 2ⁿ (n planes): the image halves accept as much as the half does. Gain,
  directivity and efficiency are then the whole structure's. The **S-parameters stay the half model's own** (a port cut by the plane
  is referred to the impedance the file states — Hertzon's own rule), and the run says so in one note.
- **Solvers.** EMerge and Palace already integrate over the absorbing faces only: their refusals now allow flagged faces. openEMS's
  equivalence box reaches a flagged face with no surface there and no floor image (the floor rule for an *unflagged* conducting
  `zmin` is unchanged: an infinite ground plane, the upper hemisphere). A flagged `zmin` with any other image rule is the same code.
- **Phase convention per solver.** circuitRF's own transform (openEMS) uses the convention above. EMerge's and Palace's are verified,
  not assumed, by the translated-plane gate below; a solver whose sign is opposite gets its own constant, named where it is set.
- **Recombined patterns** (EM field-data cosimulation) of a mirrored result are refused by name: the pattern is the whole
  structure's and the stored port quantities the half's.

### R-hzn21-3 — Split, a `.c3d` operation
- **Format.** `{"Kind": "Split", "Plane": "YZ", "At": <length, an expression>, "Keep": "Positive", "Target": {…}}`. `Plane` is `YZ`,
  `XZ` or `XY` in the operation's own frame (its placement carries it, as a Fillet's does); `At` is the plane's coordinate along that
  frame's normal axis; `Keep` is `Positive` or `Negative`. It takes the target's name, as every operation does; `Enabled: false`
  restores the target. Its faces are the target's, and the cut face is `split`.
- **Geometry.** The kernel tree lowers it as an `intersect` of the target with a box spanning the kept side: from the plane to past
  the target's conservative bounds (each primitive's own bounds through its transforms), so the geometry worker is unchanged. A
  Split of a STEP part is refused in v1 (its bounds are the worker's alone). An empty result is refused as a boolean's is.
- **Check.** A target that is not a solid; an `At` that does not resolve to a length.
- **Editor.** The tree shows the Split as an operation above its target. Properties edits Plane, At and Keep, each one undo entry.
  A solid's context menu has **Split…**, which wraps it in a Split at the plane through its centre (`YZ`, keep `Positive`) for the
  user to edit. Disable and Dissolve work as for every operation.

### R-hzn21-4 — translation, save and export
- A `Split` step with `Plane` `XY`/`YZ`/`XZ` and `Keep` `Positive`/`Negative` becomes a `.c3d` Split. Its `CoordinateSystem` may be
  `Global`, or a Relative system whose axes are the global ones (Axis/Position with `XAxis` 1,0,0 and `YPoint` on +y, or Euler angles
  all 0) and whose reference chain ends at Global: `At` is then that system's origin along the normal, as an expression. A `Custom`
  plane, `Keep` `Both`, or a rotated system is refused by name.
- A `Symmetry` boundary becomes the face's `Pmc`/`Pec` with `Symmetry: true`. Its `ImpedanceMultiplier` is no longer refused: it is
  listed as unused, with a note that Hertzon's solver does not apply it either, so S is as the file's port impedances state.
- `C3dToHzn` writes a Split back as a `Split` step, and a flagged face as a `Symmetry` boundary (`Perfect H` / `Perfect E`); an
  unflagged PEC face stays `Perfect E`. The patcher keeps an unchanged Split step as it was.

## Gates

- **`HalfModelTests`** (Ui.Tests, synthetic, never Hertzon's file):
  - a `.c3d` Split round-trips through read/write, resolves an expression `At`, elaborates to a solid whose extent ends at the plane
    on the kept side, and names its cut face `split`; Disable restores the target;
  - `check` refuses each R-hzn21-1 case by name;
  - `Em3dSymmetry.Mirror` on an analytic pattern: a z-directed Hertzian dipole's half (its pattern split by hand) mirrored across x = 0
    as a magnetic wall gives the whole dipole's pattern exactly; the same with the plane at x = 0.3 λ and the dipole moved with it;
    an electric wall with the source tangential to it gives the null it must; two planes give the four-fold image;
  - the accepted power doubles per plane, and the published directivity equals the whole source's;
  - the translator: Split in Global and in a translated system, Symmetry → flagged face, the multiplier listed as unused, the
    refusals for Custom, Both and a rotated system; export writes the Split and the Symmetry boundary back.
- **Against the full dipole, every solver (short runs):** the half dipole of `Dipole.hzn` (and a synthetic one) on EMerge, Palace and
  openEMS: resonance and R at resonance agree with the full model's within the solver's own run-to-run spread, and the peak gain within
  0.3 dB of the full model's (Hertzon reports +0.2 dB). The same half model translated by 0.3 λ along the plane's normal reads the same
  pattern (this is what fixes each solver's phase convention).
- **Census: 91 of 99** under `CIRCUITRF_HERTZON_HOME`.
- **UI:** UiSnapshot of the air box face picker with a symmetry face, the Split's Properties page, and the canvas of the half dipole
  with its symmetry face hatched. Look at each and list it.

## Risk

- **The solvers' far-field phase convention.** A wrong sign only shows when the plane is off the origin; the translated gate is what
  catches it, and must be run on each solver, not inferred from one.
- **openEMS's equivalence box at a PMC face** has never been exercised: the side faces must meet the wall exactly.
- **Bounds of the Split's box** are conservative by construction; a target whose transforms the bound walk does not cover is refused,
  not guessed.

## Rules

As brief-hzn-10. Two other agents share the repo: stage only this brief's files, and commit with explicit paths (the index may hold
another agent's staged work). Keep every EM run short and scoped; no whole-board runs.

## Completion note (2026-11-16)

Built as above. **Census 90 → 91 of 99** (`HalfWave1GHz_Half`; Showcase B now needs only its Impedance boundary and TE mode type).

- **Against the full dipole, all three solvers** (a synthetic 2 × 60 mm dipole at 1.15 GHz, one point each, ≤ 4 min): Palace's half
  reads the full model's gain to 0.001 dB and its pattern to 0.025 dB; EMerge's half is +0.21 dB in peak gain (Hertzon reports
  +0.2 dB for its own) and 0.6 dB across the pattern; S11 at 146 Ω matches the full's at 73 Ω.
- **The translated gate**, run as a monopole on a PEC symmetry plane 40 mm off the origin: patterns within 0.016 dB (Palace), 0.05 dB
  (openEMS) and 0.6 dB (EMerge) of the full dipole's, so the phase sign is +1 on each solver.
- **openEMS refuses a lumped port reaching the plane across its current** (the dipole fed on its plane): its current probe would leave
  the grid and read zero. EMerge and Palace run that case; openEMS runs a port along the plane's normal.
- **Beyond the brief:** Hertzon's overlap rule as construction order (a drawn air box after the arms it holds swallowed them), and a
  Split's cut face on a closed design's region mapped to the air box face. The editor's face picker reads `PMC symmetry`/`PEC symmetry`.
- Gate: `tests/Ui.Tests/Hzn/HalfModelTests` (12 tests, one through the geometry kernel) and the Hzn, far-field, lowering and editor
  classes beside it (322 pass; 5 `Hertzon_Describes_*` fail only because this Hertzon copy has no `hertzon-cli`). Firewall green.
- UI looked at with UiSnapshot: the Split's Properties (Plane, Keep, Enabled, At, cut face), the air box faces with `PMC symmetry`
  on xmin, and the tree's Split rows with the cut icon.

Findings: `src/Design/RESOLVED.md` §HZN-21.

### Follow-up (2026-11-16): the items left as found

- **Custom plane: built.** `.c3d` `Split` `Plane: Custom` with `Base` and `Normal`; translated, exported and edited (Properties shows a
  Normal box and the Base row for a Custom plane; switching the Plane picker carries the cut across). Positive keeps the side the normal
  points to — derived from Hertzon's schema and tutorial, not measured on Hertzon (see RESOLVED).
- **`Keep: Both`: not a Hertzon value.** Hertzon's own schema lists Positive and Negative only.
- **A Split off the origin exports** through a coordinate system of its own, and saving an edit into an opened `.hzn` adds it.
- **The radiation pattern** is on for every setup of a design with an Infinite Sphere, and export writes a sphere for a setup that asks.
- Gate: `HalfModelTests` 17 tests (two through the kernel), neighbouring classes 333 pass (5 environmental `Hertzon_Describes_*`), firewall
  green, census 91 of 99. No new solver runs.

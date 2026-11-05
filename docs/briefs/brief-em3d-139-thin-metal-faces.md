# Brief EM3D-139 — metal thin against the skin depth, solved correctly on Palace and EMerge

**Tag:** `R-em3d139-<n>`
**Area:** `src/Design/Em3d/` (`GmshGeoWriter`, `PalaceConfigWriter`, `Solvers/EMerge/`), `src/Design/Layout/Em3d/Em3dThinMetalLoss.cs`,
`src/Cli/Check.cs`, `docs/user/src/reference/`. Findings go in `src/Design/RESOLVED.md`, whose entry "Metal thin against the skin depth
reads too little loss" holds the measurement this brief starts from.

## 0. Why

Neither solver meshes a conductor's interior. Its faces carry (1+j)/(σδ), the impedance of metal thick against the skin depth, and
that is the right model for thick metal: it is why a 3D mesh is affordable, and nothing here changes it. Below about three skin depths
it is wrong. The current fills the metal, so the resistance should level off at its DC value, and the face impedance keeps falling.
`Em3dThinMetalLoss` warns of this today; nothing corrects it.

Measured on Palace at the Standard preset's order 2, with no refinement passes and no adaptive sweep. A 300 µm line of 1 µm aluminium
(σ = 3.77e7 S/m), 10 µm over a PEC floor, in a PEC box with 20 µm past each end and 60 µm to the sides and above; a lumped port from the
floor to each end of the strip. R' = Re(−1/Y21)/ℓ and L' = Im(−1/Y21)/(ωℓ). The reference is a 2D filament solve of the same section
over its image, ignoring the box. R' as a ratio to it:

| GHz | R' ref, 5 µm wide (Ω/mm) | today | `Thickness` = 2A/P | R' ref, 50 µm wide (Ω/mm) | today |
|---|---|---|---|---|---|
| 0.1 | 5.305 | 0.05 | 1.00 | 0.531 | 0.07 |
| 0.447 | 5.307 | 0.11 | 1.00 | 0.537 | 0.16 |
| 2 | 5.341 | 0.24 | 1.01 | 0.581 | 0.33 |
| 8.94 | 5.849 | 0.47 | 0.99 | 0.696 | 0.61 |
| 40 | 8.352 | 0.73 | 0.80 | 1.04 | 0.91 |

L' on the 5 µm line, pH/µm, reference / today / `Thickness`: 0.551 / 0.968 / 0.554 at 0.1 GHz, 0.551 / 0.627 / 0.552 at 2 GHz, 0.532 /
0.538 / 0.530 at 40 GHz. The 50 µm reference at 40 GHz is unconverged: three grids gave 1.02, 1.04 and 1.07 Ω/mm.

**The correction both solvers already have.** A slab's face impedance with current on both sides is (1+j)/(σδ)·coth((1+j)h/2δ). It equals
today's value when h ≫ δ and tends to 2/(σh) when h ≪ δ. Take h = 2A/P for a section of area A and perimeter P
(`Em3dThinMetalLoss.EquivalentThicknessM`), put that on every face, and the faces in parallel give exactly 1/(σA), the DC resistance,
for any section. Palace's `Thickness` without `External` is this expression (read from its 0.18.1 source). EMerge's `SurfaceImpedance`
takes a `thickness` and uses coth(γ·thickness), so it takes h/2 = A/P.

**What this does not fix.** At 40 GHz the 5 µm line stays 20 % low, at about two skin depths, where current crowds to the edges and one
value per conductor cannot follow it.

## 1. Measure first, and stop if it fails

**R-em3d139-1 — The three things not yet known.** On the line above, with a scratch harness and no new tests:
- **The 50 µm line on Palace.** First refine the reference until it settles at 40 GHz.
- **EMerge, 5 µm and 50 µm, today and with `thickness`.** EMerge's model is built in micrometres (`MODEL_SCALE`) and its impedance
  evaluates γ in SI, so **check the unit**: a wrong one shows at 0.1 GHz, which must read the DC resistance. Its walls are grouped by
  material, so a scratch run with one conductor is the fair first test.
- **What it costs.** The one `Thickness` run took 294 s against 42 s, on a loaded machine and partly beside a build, with the same mesh.
  Run the pair alone. If the cost is real, say where it goes (`palace.log`: iterations per solve, and which frequencies), and whether
  another linear solver removes it.
- **The Standard preset as a user gets it.** The measurement above had refinement passes and the adaptive sweep off. A face impedance
  that varies with frequency has to survive both, so run the 5 µm line with both on and compare R' and L' at the same points.

Each case took 2 to 5 minutes. Stop and report if the set passes 30 minutes, if EMerge does not reach the reference to within what Palace
does, or if the cost is more than twice the run's time on the Standard preset. Those are decisions for the owner, not for this brief.

## 2. The fix

**R-em3d139-2 — Palace.** A conductor group carries its h in `Em3dGroup.ThicknessM`, which `PalaceConfigWriter` already writes for a sheet.
Rules:
- **Only a thin conductor is written:** h under `Em3dThinMetalLoss.SkinDepths` skin depths at the lowest frequency solved. A thick
  conductor writes nothing, so a design with no thin metal produces the same `config.json` bytes and the same answer as before.
- **Driven only.** An eigenmode problem writes what it writes today and keeps the warning, until someone measures one.
- **Not past the source's range.** Palace evaluates cosh(h/δ) and sinh(h/δ) directly, which overflow past h/δ of about 710, and inf/inf is
  NaN (read from the source, not run). That needs a sweep spanning more than a factor of about 50,000 in frequency. Such a conductor
  is left as today, and keeps the warning. Clamping h is wrong: it would move the low end of the band, which is the part being fixed.
- A kernel solid or a sphere has no h and stays as today. A touching group's leftover faces go to its largest member, as they do now, and
  take that member's h.

**R-em3d139-3 — EMerge.** The same predicate and the same h, as `thickness` = h/2 in whatever unit R-em3d139-1 found. `walls` is keyed by
material; key it by material and conductor (`k` is already in hand, and `plated` already uses it), since two conductors of one metal can
differ in h. Write h into the problem JSON from the C# side, so the two solvers cannot disagree about it.

**R-em3d139-4 — What the run says.** A conductor corrected this way needs no warning. It gets a note giving its h and saying that around
two to three skin depths a narrow line can still read low, by the measured figure. The warning stays for what is not corrected: eigenmode,
a kernel solid, a conductor past the range above. `ThinRoundConductors`' note is about curvature above that range and stands.

**No setting.** The new numbers are the correct ones and today's are not a choice anyone would make, so this is not a setup option. A
thin-metal design's S-parameters change on its next run. The owner may overrule this.

**R-em3d139-5 — `check` on a `.c3d`.** A setup embedded in a `.c3d` reports fidelity rows only when the document holds a kernel object
(`Check.cs`, `C3dKernelUse.Of(doc).Count > 0`), so `check` misses this finding while a dry run and a run both state it. Report it, without
making `check` slow on a large board: say what the assembly costs, measured, in the note.

**R-em3d139-6 — The 0 Hz row.** A run's DC row is a conduction solve of its own (`Em3dDcSolve`), and on thin metal today the sweep's low end
is far from it. Report R at the lowest frequency against that row on the 5 µm line. It should now meet it.

**R-em3d139-7 — Sheets, for the record.** Run the 5 µm and 50 µm lines with `Conductors: Sheets` and report R' and L'. `SheetRule` tests the
top of the band, so a thick-looking metal at 40 GHz stays a solid at 1 GHz. **Do not change the rule here**; the numbers are for the
owner to decide with.

## 3. Gates

Minimal, and only these classes are run:
- `PalaceConfigWriter`: `Thickness` on a thin conductor, nothing on a thick one, nothing on an eigenmode problem, nothing past the range.
- The EMerge problem JSON carries h per conductor.
- **One run per solver against a committed reference** (`testdata/em3d/thin-metal/`: the 2D table and the short script that made it, which
  references nothing in this repo). 5 µm line: R' and L' within 3 % from 0.1 to 9 GHz, R' within 25 % at 40 GHz. Tag `Category=Benchmark`
  if over ~5 s. These are the measured errors plus a margin; tighten them if the measurement allows. No timing assertion.
- The warning's existing tests, moved to what it now covers.

## 4. Docs

`docs/user/src/reference/em-solvers.md` (the metal-loss row and its section) and `em-setup.md` (Conductor bands): what thin metal gets, what
remains low, and that Palace and EMerge differ from openEMS here. Regenerate the edited pages.

## 5. Not in this brief

- **A skin-depth shell of mesh inside the metal.** Copper at 10 GHz would need about 3×10⁸ tetrahedra per mm² of surface at two elements
  per skin depth over three skin depths, and Gmsh's boundary-layer field is 2D only.
- **Meshed metal for the 40 GHz residual.** Only worth building if a design is found that needs it. Where h ≲ 2δ the current is nearly
  uniform, so it would need no skin-depth layers, only 2 to 3 elements across.
- Surface roughness, the conductivity of a `FaceBoundary`, openEMS, and static problems, whose conductors are perfect.

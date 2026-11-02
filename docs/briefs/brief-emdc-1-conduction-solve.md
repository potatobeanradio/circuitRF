# Brief EMDC-1 — The DC conduction solve of a 3D problem

**Tag:** `R-emdc1-n` · **Series:** EMDC (read `brief-emdc-0-overview.md` first, §2 and §3 above all) · **Depends on:**
nothing new: the thermal mesher (brief-em3d-74), the electrothermal φ assembly (brief-em3d-77/85)
**Area:** `src/Thermal/Conduction/` (new: the solve on a mesh), `src/Design/Em3d/Em3dDcLowering.cs` (new: an
`Em3dProblem` to a conductor-only mesh plus its contacts), tests in `tests/Thermal.Tests` and `tests/Ui.Tests/Em3d`.
Findings in `src/Thermal/RESOLVED.md` and `src/Design/RESOLVED.md`.
**Out of scope:** anything a run does with the answer: writing it into the `.sNp`, the notes a run posts, Compare,
docs. All of that is EMDC-2. Nothing in this brief is reachable from a run yet.

---

## 0. Read first

- `src/Engine/Mom/PlanarDcSolve.cs`'s header. It covers why DC is conduction, and why a port is a terminal pair whose
  terminals are both free (the 2026-09-14 defect, where every terminal held at an absolute potential welded a gap's far
  lip to ground). That defect must not be repeated here.
- `src/Thermal/Electrothermal/ElectrothermalSystem.cs`'s header: φ on the conductor sub-mesh, a contact as one
  equipotential carrying a total current, never a uniform current density. `BodyResistances` is the nearest existing
  routine to what §2 does.
- `src/Design/Thermal/ThermalCurrents.cs`'s header: **a port's current enters through the SMALLEST face that holds its
  edge**, so a contact never shorts a conductor along its length. §3 uses the same rule, so circuitRF has one
  definition of where a port's DC current enters.

## 1. `R-emdc1-1` — the result type

```csharp
public sealed record Em3dDcResult(
    double[,]? S,                    // N×N, real, port order = Em3dProblem.Ports by Number; null when refused
    string? Refusal,                 // the one sentence saying why there is no DC row (overview O2), else null
    IReadOnlyList<string> Notes,     // what was solved: contacts, ties, lumped elements, element count, time
    int Elements, double ElapsedMs);
```

S is referred to each port's own Re Z₀. **A port with Re Z₀ ≤ 0 is a refusal** that names the port. It lives in
`src/Engine/Em3d` beside `Em3dProblem`, so `src/Design` and `src/Thermal` can both see it without a new reference. If
it cannot, place it where the reference graph allows and say where in RESOLVED.

## 2. `R-emdc1-2` — the solve on a mesh (`src/Thermal/Conduction/DcConduction.cs`)

Input: a `ThermalMesh` (tets, plus tagged triangles), σ per tet region, sheet conductance σt per triangle tag, a set of
**ties** (node sets that are one potential: PEC bodies, PEC sheets, a lumped L, an R = 0), **resistors** between two
node sets (lumped R), and the **ports**: each a pair of node sets with its Re Z₀.

- **Assembly:** the σ-weighted Laplacian on tets (`ReferenceElement.Tet`/`TetGradients`, the element routine the
  thermal assembly uses for k), plus the σt-weighted 2D Laplacian on sheet triangles (`ReferenceElement.Tri`). Order
  1 or 2, as the mesh is.
- **Ties merge unknowns** before assembly, exactly as the electrothermal system makes a contact one φ item. **Never a
  penalty conductance:** brief-em3d-85 found that no Krylov solve converged with one, and the same would happen here.
- **Each port is a branch** of conductance 1/Re Z₀ₖ between its two contact items. Port j's right-hand side is an EMF
  of 2√Z₀ⱼ in series with that branch, which is the Norton source 2√Z₀ⱼ/Z₀ⱼ across the pair. Solve N right-hand
  sides on one factorisation (`LinearSolver`, symmetric). Then I_k is the current into port k's positive contact, and
  **S_kj = δ_kj − √Z₀ₖ · I_k**.
- **References:** fix one potential in every connected component of the whole graph (conduction, ties, resistors and
  port branches). **Drop the unknowns of a component that no port reaches.** A floating conductor carries no current,
  and solving it would be a singular block, as `ElectrothermalSystem` already says.
- **Checks, as notes, never silent:** reciprocity, ‖S − Sᵀ‖ ≤ 1e-9; passivity, σ_max(S) ≤ 1 + 1e-9. A failure of
  either is a refusal: a DC row that fails them is a solver defect, and must not be written into a user's file.

## 3. `R-emdc1-3` — the lowering (`src/Design/Em3d/Em3dDcLowering.cs`)

`Em3dDcLowering.Build(Em3dProblem problem, …) → (mesh input, contacts) | refusal`, then Gmsh, then §2.

- **What is meshed: conductor solids only** (`Em3dRole.Conductor`) through `GmshGeoWriter.WriteThermal`. Pass the air
  solids that outrank a conductor (a via's bore) as the input's air cuts, so the precedence cut is the one every EM
  lowering makes. Dielectrics and the air box are not meshed: at DC they carry nothing. Conductor **sheets** go in as
  embedded sheets with their own tags.
- **A conductor reads at DC as the EM lowering reads it** (`PalaceConfigWriter`'s rule): σ ≤ 0 or σ = ∞ is PEC (one
  tie per body), and finite σ is a conductivity. A sheet's conductance is σ·ThicknessM. Use `Em3dMaterial.SigmaSm` as
  given: it is already resolved at the problem's operating temperature.
- **Sizing:** the thermal defaults (`GmshThermalSizing`), with no heat sources. The DC answer needs the conductors'
  cross-sections resolved, not a wavelength. Record the element count and the time; §6's budget holds them.
- **The air box:** every conductor surface lying on a **PEC** face is tied to one ground item. PMC, absorbing and
  symmetry faces are insulating (the natural condition), which is the same problem the full-wave solve was given.
- **Contacts:**
  - **Lumped port:** the rectangle's two edges across `Direction`. The negative edge lies on `NegativeObject` and the
    positive edge on `PositiveObject`. A **coaxial** (`Annulus`) port uses its two circles, and a **cylinder-gap** port
    (`Cylinder`) uses its two arcs.
  - **Wave port, or a terminal of a terminal group** (`FaceGroup`): the object's surface lying in the port's region of
    its air-box face, which is that conductor's cross-section on the face.
  - **An object that is an air-box face** (`Em3dAirBox.FaceName`): the ground item, when that face is PEC.
  - **On a PEC object** the contact is the object's own tie. **On a finite-σ object** it is the smallest boundary
    surface of that object, as Gmsh's fragment classified it, that holds the whole edge (ThermalCurrents' rule, at mesh
    level). Where no single surface holds it, use the union of the surfaces the edge lies on, and say so in a note.
    Every contact is noted with its object, surface and area, so an inference nobody meant is visible.
  - **A port whose contact on either side is empty, or whose two contacts are the same node set, has no DC meaning**
    (a hollow waveguide's wave port is the case). That is a refusal naming the port. Two different surfaces of one body
    are fine: that is a loop, and its resistance is the answer.
- **Lumped elements** (`Em3dLumpedElement`): contacts by the lumped-port rule on the conductors at its two edges.
  - Parallel topology: any L, or R = 0, makes it a **tie**. Otherwise R makes it a **resistor**, and C alone makes it
    **nothing**.
  - Series topology: any C makes it **nothing**. Otherwise a non-zero R makes it a **resistor**, and L alone or R = 0
    makes it a **tie**.
  - Each is noted with what it became.
- **Gmsh missing:** `SolverDiscovery.Gmsh.Check`, as the thermal run does. Its refusal text is the result's `Refusal`.
  The install offer is EMDC-2's to show.

## 4. `R-emdc1-4` — the one entry point

`Em3dDcSolve.Solve(Em3dProblem problem, string workDir, CancellationToken ct) → Em3dDcResult`. It is in `src/Design`,
because it runs Gmsh. It takes no preference and posts nothing (`src/Design/CLAUDE.md`: diagnostics are RETURNED). A
problem whose `Type` is not `Driven`, or with no ports, is a refusal. Cancellation throws, as every run stage does.

## 5. Tests (targeted; one per claim)

**`tests/Thermal.Tests/DcConductionTests.cs`, no Gmsh:** meshes from `TestMeshes.Box`, contacts given as node sets.

1. **Resistance:** a copper bar 10 mm × 1 mm × 35 µm, σ = 5.8e7, one port across its end faces. R from S₁₁,
   R = Z₀(1 + S₁₁)/(1 − S₁₁), is within 0.5 % of L/(σA) = 4.926 mΩ.
2. **Open, short and through:**
   - Two disjoint bars, each a port to a separate ground body: S = I.
   - One bar from a port's positive contact to its negative contact: S₁₁ = (R − Z₀)/(R + Z₀), within 1e-9.
   - Two ports on one bar that touches no ground: S₂₁ = 1 − O(R/Z₀), which is a through.
3. **Ties:** a PEC body bridging two bars makes them one conductor (2's through), with no penalty term. Assert the
   merged unknown count.
4. **Lumped elements:** one port across a gap carrying each of R = 100 Ω (S₁₁ = 1/3), L (−1), C (+1), series RC (+1)
   and parallel RC (1/3). One `[Theory]`, five rows.
5. **A floating conductor** in the mesh changes nothing and leaves the solve non-singular.
6. **Reciprocity and passivity** on a three-port: hold to 1e-9.

**`tests/Ui.Tests/Em3d/Em3dDcLoweringTests.cs`, `[GmshFact]`:** small `Em3dProblem`s built in code.

7. **Only conductors are meshed:** the region list holds no dielectric and no air, and a via's bore is cut out of its
   barrel.
8. **Contacts:** a lumped microstrip-style port's positive contact is the trace's end face (not its underside), and its
   negative contact is on the ground body. Assert the noted surface and area.
9. **The DC block that started this:** a 3D series gap capacitor (two traces over a ground plane, ports at the outer
   ends) gives |S₂₁| < 1e-12. The same structure with the gap filled by metal gives S₂₁ within 1e-4 of 1.
10. **Refusals:** a waveguide wave port, a port with Re Z₀ = 0 and a non-Driven problem, each refused with its
    sentence.
11. **Both routes:** a 3D setup built by `Em3dGenerator` from a small `.clay` and one built by `C3dProblemAssembly`
    from a `.c3d` each solve, and agree with test 1's analytic resistance to 1 % for a plain trace. This proves the
    solve reads only the problem.

## 6. Budget and RESOLVED

Record in `src/Design/RESOLVED.md` the element count and wall time for test 9 and for the largest shipped 3D example a
Driven setup exists for. **The DC solve must cost a small fraction of one full-wave point.** If it does not, stop and
report it with the numbers rather than coarsening the mesh until it does. Record any contact the rule in §3 resolved
in a way that surprised you, with the geometry that caused it.

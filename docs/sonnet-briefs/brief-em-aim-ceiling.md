# Brief — the AIM accelerator's ceiling: does R17's 5,000 move, and to what?

**A FOLLOW-UP to M5 of `brief-em-sweep-performance.md`, and it is the question M5 explicitly left
open.** M5 built the AIM accelerator, gated its accuracy, measured it to N = 3,731 and shipped it
**off**, recording that what it still owed was *"measurement above N = 3,731 and on a via-bearing or
cut mesh, plus a decision on whether R17's ceiling moves."* This brief is that debt.

**It adds no physics.** No Green's function, no DCIM fit, no basis function, no closed-form integral,
no de-embedding algebra. If you are editing `SpectralGreens.cs`, `Dcim.cs`, `SingularExtraction.cs`
or `PlanarDeembed.cs`, you are in the wrong brief. What it may touch: `PlanarAim.cs`,
`SurfaceMesher.UnknownCeiling` and its three call sites, and the panel's own wording.

**Its outcome may legitimately be "the ceiling does not move."** That is a result, not a failure, and
it must be recorded as one — with the measurement that decided it — exactly as M3's cross-frequency
parallelism was.

Read first, in this order:

- `src/Engine/Mom/CLAUDE.md` §8 (**"Performance — current shipped state"**), the `PlanarFillSettings.Aim`
  paragraph — every measured AIM number that exists today.
- `src/Engine/Mom/CLAUDE.md` §7's closing paragraph — M5's own statement of what it owes, verbatim.
- `src/Engine/Mom/HISTORY.md` lines **5048–5515** — M5's decision gate and the accelerator's build,
  including R-emp-16's two accuracy gates. Grep `AIM` rather than reading the range whole.
- `src/Engine/Mom/PlanarAim.cs` — `PlanarAimSettings` and `PlanarAimReport`.
- `src/Engine/Mom/SurfaceMesher.cs` `UnknownCeiling` **and its three call sites**
  (`SurfaceMesher.Mesh`'s own verdict, `PlanarSystem.GuardCeiling`, `PlanarFill.cs:1620`).

Nothing below is repeated from those.

---

## §0 — why this is being asked now: a real user, a real refusal

A user demoing the MoM engine on 2026-08-14 pointed an EM setup at **one PCell** — a Klopfenstein
taper, **Z1 = 6.92 Ω into Z2 = 100 Ω**, L = 28.575 mm, on 20 mil RO4350B, swept 1–5 GHz — and was
refused at **N = 7,749**. The workspace is the owner's `NZ_20260814`.

**The geometry is small; its width RATIO is not.** 6.92 Ω on a 0.508 mm substrate is 13.1 mm of
copper; 100 Ω is 299 µm. `MinCellsAcrossConductor` sets the pitch from the narrow end and the grid is
one tensor product over the whole layout, so the wide end pays the narrow end's pitch everywhere.

Measured on that exact file (ports and calibration feed included, so these are the numbers the solver
actually sees):

| Settings | N |
|---|---|
| cells/λ 5, edge 3, conformal, f_mesh 500 MHz — **the user's own `.cem`** | **7,749** |
| cells/λ 10, same | **7,749** |
| cells/λ 10, f_mesh at the sweep top instead | **7,749** |
| cells/λ 20 (Auto) | 7,766 |
| cells/λ 40 | 8,584 |
| Edge cells 3 → 1 | 7,711 |
| **edge mesh OFF** | **5,772** |
| cells/λ 2, edge off, f_mesh 100 MHz — every knob at its floor | **5,772** |

**There is no combination of panel settings that runs this file.** The floor is 5,772 against a
ceiling of 5,000 — a **13% overshoot**, which is the interesting part: this is not a user asking for
something wildly out of scope, it is a user who is 15% over a line.

**Two defects in how that was reported have already been fixed** and are NOT this brief's work
(2026-08-14): the refusal named remedies that are inert on this geometry, and the mesh report's own
diagnostic notes were dropped because the refusal left `PlanarKernel.Solve` as an untyped exception.
Both are gated by `tests/Ui.Tests/Em/EmCeilingRefusalTests.cs`. **The panel also gained its first
user-reachable switch for the accelerator** (`EmSetup.AcceleratedSolve`, persisted in the `.cem`,
gated by `tests/Ui.Tests/Em/AcceleratedSolveUiTests.cs`) — so a user can now turn AIM on, and
**today that changes nothing about whether this file runs**, because the ceiling is checked before a
solver is chosen. Closing that gap is what this brief is for.

**This class of part is not exotic.** A wide-to-narrow impedance transformer is one of the most
ordinary things anyone draws on a PCB, and every one of them lands in the same place.

---

## §1 — the question, stated precisely

`SurfaceMesher.UnknownCeiling = 5000` is **one constant, applied unconditionally**. AIM's whole win
is memory. So:

> **Should the ceiling be a function of the solver, and if so what is the accelerated value?**

Three candidate shapes, and the brief must pick on evidence:

- **(a) It does not move.** The dense path's ceiling is right and AIM is a memory optimisation within
  it. Then say so, record why, and the user's answer is "split the taper" forever.
- **(b) A second, higher ceiling when `Aim` is non-null and the mesh is single-level.** Concrete, and
  it makes the reported file run. Costs: a mesh that runs with a checkbox on and refuses with it off,
  which is a new thing for a user to understand.
- **(c) The ceiling becomes a MEMORY budget rather than an unknown count**, evaluated per solver
  (dense: 16N²; AIM: near entries + grid). This is the honest formulation of what the ceiling has
  always been trying to express — §7 already records that its message *"quotes 381 MB against a real
  run cost of ~607 MB at the ceiling"*, i.e. the current number is not even a faithful statement of
  the dense path's own cost.

**(c) is the most principled and the most work. Do not pick it because it is elegant — pick it only
if A1 and A2 show that a single accelerated unknown count is not a stable line.**

---

## §2 — milestones

### A1 — measure AIM above N = 3,731. **Gating.**

M5's entire measured range stops at N = 3,731; every claim about AIM past that point is an
extrapolation, and the ceiling question lives entirely past it.

Build an N ladder to **at least N ≈ 12,000** (roughly 1.5× the reported file, so the answer is not
read off its own boundary) and report, per N:

- **peak working set** — AIM and dense, on the same mesh, measured the way R-emp-12 measured it;
- **fill/build time and solve time**, split;
- **GMRES iteration count** (M5 saw 2 → 6 across a 6.7× N range; whether that stays flat is the
  question that decides whether AIM's time scaling holds);
- **near-field entries per row** (M5: 290 → 392 over 12× N, i.e. genuinely O(N)).

**The dense path must be run at every rung it can still reach**, because A2's accuracy gate needs a
dense reference and past ~N = 8,000 there may not be one.

> **Trap.** A ladder built by refining one geometry changes the mesh's *character* as it grows, not
> only its size — edge grading, cut cells and aspect ratios all move together. Build the ladder at
> least two ways (refine one part; and grow a part at fixed resolution) and report both. If they
> disagree about where a crossover sits, that disagreement is the finding.

### A2 — accuracy at the ceiling's own scale. **Gating, and the one that can kill (b).**

R-emp-16's two accuracy gates were measured on M5's own range. Re-run them at the top of A1's ladder
against the dense answer wherever one exists.

**The quantity that matters is the DE-EMBEDDED s-parameter, not the raw current vector.** L8d
measured de-embedding amplifying a raw-S error **~22× at 2 GHz**, growing as f⁻², and R-fed-1's own
history is a case where a 0.1% error in the error box became a 10× error in the answer. An
accelerator validated on ‖ΔI‖ and shipped past a raised ceiling is precisely the shape of "a complete,
smooth, plausible, wrong s-parameter set" this directory keeps finding.

> **Trap.** GMRES's tolerance is 1e-8 by default *because* L8c puts the fill's own accuracy at 5.0e-6
> and de-embedding amplifies. Do not "tune" the tolerance to make a time measurement look better —
> that is throwing away the fill and calling it a speedup.

### A3 — the via/cut-mesh question. **Scoping, not necessarily building.**

AIM **refuses multi-level and via-bearing meshes by name** (`PlanarSolve.SolveAt`), because a ẑ basis
needs a different grid kernel per height pairing. That refusal is correct and this brief does not lift
it.

What A3 owes is only: **does AIM work on a CUT (conformal) single-level mesh?** M5 never measured one,
conformal ships off, and the reported file was meshed conformal. If the answer is no, the accelerated
ceiling — if there is one — is conditional on the boundary model too, and the panel must say so rather
than refusing at solve time after the user has waited.

### A4 — the decision, and the wiring if it is (b) or (c).

Whatever is decided:

- **Record it in `src/Engine/Mom/CLAUDE.md` §4's table and §8**, with the measurement. A negative
  result goes in §6 ("Do not retry") with the numbers that produced it, in M3's format.
- If the ceiling moves, **all three call sites move together** — `SurfaceMesher.Mesh`'s verdict,
  `PlanarSystem.GuardCeiling` and `PlanarFill.cs:1620`. A ceiling raised in the report and not in the
  guard is an `OutOfMemoryException` from inside NumFlat, which R-fil-10's own comment says is not a
  refusal.
- **The refusal text is conditional on the solver.** `SurfaceMesher.BuildRefusal` currently ends
  *"the accelerated solve reduces the memory one costs but does not move this ceiling"* — true today
  and false the moment (b) or (c) lands. If a run would fit with the accelerator on, the refusal must
  say **that**, by name, as the first remedy.
- **Fix the megabytes while you are there.** §7 records that the message quotes 381 MB at the ceiling
  against a real run cost of ~607 MB. Whatever the new number is, it should be the one a user's
  machine will actually see.

---

## §3 — the open discrepancy to settle first

`src/Engine/Mom/CLAUDE.md` states the near-field radius **twice and inconsistently**:

- §7: *"The near-field radius must be 8 basis-supports, not the naive 3 — 3 cells degrades with N and
  is beaten by no preconditioner at all on a refined mesh; 8 is flat (3–6 GMRES iterations across a
  6.7× N range)."*
- §8: *"Shipped defaults where it's turned on: projection order 3, auxiliary-grid pitch 0.5 of the
  largest basis support, **near radius 6 supports**."*

`PlanarAimSettings.NearRadiusFactor` is **6.0** in code. Since the §7 sentence says the radius is what
stops the iteration count degrading **with N**, and A1 is entirely about behaviour at large N, **this
must be resolved before A1's ladder is run** — the ladder measures whatever the default is, and
measuring the wrong one wastes the whole milestone. Read M5's own tables in `HISTORY.md` and either
correct the prose or correct the default, with a note saying which was wrong.

---

## §4 — what this brief must NOT do

- **Do not lift AIM's multi-level/via refusal.** It is a separate phase with its own physics.
- **Do not raise the ceiling for the dense path** on the grounds that machines have more RAM now. The
  dense path's cost is 16N² *plus* the LU's working set, it is measured, and the reported file needs
  compression rather than a bigger number.
- **Do not make the accelerator the default** as part of this brief. Its own §8 note is explicit that
  below the time crossover the dense path is faster, and the great majority of meshes are below it.
- **Do not attempt ACA again.** §6 records it as measured and deferred: far blocks under the ceiling
  need 53–62% of min(m,n) rank at 1e-3 even with a full pivot.
- **Do not touch the mesher to make this file smaller.** A locally-graded (non-tensor-product) grid
  would genuinely fix the width-ratio cost and is a real idea — but the fill's closed-form rectangle
  integrals are written against a tensor-product grid, and that is a different brief with a much
  larger blast radius.

---

## §5 — gates

1. **A1's ladder table**, in `HISTORY.md`, both ladder constructions, dense and AIM side by side, with
   peak working set and split times — the artefact this brief exists to produce.
2. **A2's de-embedded accuracy** at the top of the range where a dense reference exists, against
   R-emp-16's own tolerances.
3. **The decision, recorded with its evidence** — in §4's table and §8 if the ceiling moves, in §6 if
   it does not.
4. **`EmCeilingRefusalTests` still passes, or is UPDATED rather than loosened.** Its assertions about
   the refusal naming the binding quantity are about wording that stays true either way; its
   assertion that the refusal says the accelerated solve *"does not move this ceiling"* is the one
   that must change if the decision is (b) or (c) — and it must change to assert the new sentence,
   not to stop asserting.
5. **The owner's `NZ_20260814` file, re-run.** Whatever the decision, the answer to *"what does this
   user do now"* must be a sentence someone can act on, and it must be the sentence the panel
   actually prints.

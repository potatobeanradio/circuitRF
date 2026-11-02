# Brief 77 — electrothermal DC: 1D wires and conductive balance

**Series:** [3D thermal, fifth series](brief-em3d-71-thermal-overview.md) · **Tag:** `R-em3d77-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.6, §9.2; overview §1c, §1d, §1g
**Area:** `src/Thermal/Electrothermal/`, `src/Thermal/Nonlinear/`, `src/Design/Thermal/` (wire lowering, ports
as terminals), `src/Design/Em3d/GmshGeoWriter.cs` (thermal mode: feet and embedded wires),
`src/Design/Layout/Em/EmSetupModel.cs` (`Thermal.Currents`), `src/Ui/ThreeD/` (the Currents group,
`SigmaOfT`), `tests/`
**Depends on:** 72 (W1, W2, W4, W5; Q1, Q2, Q8), 73, 74; 75 to draw the result · **Blocks:** 78, 79, 81

---

## 0. What this brief delivers

Scenario 3 at DC, with the owner's **conductive balance**:

1. **DC current at a port** — the setup says *port 1 carries I*; the current flows through the model's metal
   (§1).
2. **A conduction solve on the metal** — the current distribution, including how an array of wires shares it
   (§2).
3. **Wires as 1D elements** — true 3D length, πd²/4, bonded feet, heat lost into a mould compound (§3).
4. **Conductive balance** — Newton over temperature and potential with σ(T) and k(T), each switchable (§4).
5. **Thermal runaway reported as a result** (§5).
6. **A wire table in the result** — T(s), max, current, dissipated power and resistance per wire — drawn on the
   3D wire by brief 75 §5 (§6).

---

## 1. `R-em3d77-1` — current at a port

**`R-em3d77-1a`** The thermal section's `Currents` (brief 73 left it empty) takes
`{ "Port": 1, "Dc": "Id" }` — amps, an expression, sweepable. Harmonics are brief 78's. A port not listed
carries nothing.

**`R-em3d77-1b` Where it enters.** The current enters the port's **positive** conductor and leaves its
**negative** one (`C3dPort.Positive`/`Negative`, as resolved for EM, with `Flip` honoured), each through an
**equipotential contact**: the face (or faces) of that conductor the port sheet touches, found by the same
contact search the EM lowering uses. `EnterFace`/`LeaveFace` name a face explicitly when the inferred one is
not what the user means. A contact is a floating potential with a prescribed total current — not a uniform
current density, which would put a spurious hot edge where the contact ends.

**`R-em3d77-1c`** Every port's current returns through its own negative conductor, so the sum over the model
is zero by construction; the potential is referenced at one node of the first port's negative contact. The
return path through a flange or ground plane is solved like any other metal (it is where the circuit's DC
return actually flows).

## 2. `R-em3d77-2` — the conduction solve

∇·(σ(T)∇φ) = 0 on **conductor** volumes (role `Conductor`) and on wires; dielectrics carry no current. Joule
heat σ|∇φ|² is added to the thermal source in every conductor element. Per-conductor dissipated power and
per-port voltage are results. Elements: the same second-order tetrahedra on the same mesh (φ lives on the
conductor sub-mesh only).

## 3. `R-em3d77-3` — wires as 1D elements (owner, 2026-09-27)

**`R-em3d77-3a` Where the wires come from.** Every wire in the elaborated problem — a `.c3d` wire (brief 50,
arrays expanded to `w1[k]`) or a `.wBond` array reached through a layout instance — through
`C3dWires.Resolve`, the one function every solver and the viewer already use. No second wire model.

**`R-em3d77-3b` Length — the owner's condition.** The element chain follows the **resolved centreline**: its
length is the **3D arc length** of that polyline (including a ball end's vertical neck), never a plan-view
length and never the loop's chord. Second-order 1D elements, at least one per resolved segment and at most
the size the host mesh has around it.

**`R-em3d77-3c` Area and material.** Area **πd²/4** from the wire's diameter, whatever its EM section (the
hexagon would read ~21 % hot — overview §1d). σ(T) and k(T) from the wire's material through brief 73's
resolver (the technology first, then the `.wBond`'s own list — em-3d.md §4.1a's rule).

**`R-em3d77-3d` Feet.** A wedge foot is 1D elements lying on its pad's top face, from the end point outward
by the foot length (em-3d.md §6.6); a ball end contacts over the ball's footprint. The lowering embeds the
**contact patch** in the pad's surface mesh (brief 72 Q2, Q8). Foot nodes couple to the pad by a contact
conductance per unit length — thermal and electrical — from the contact width and a bond interface
resistance (default: perfect, overridable per setup). The free span starts at the heel.

**`R-em3d77-3e` Along the span.** For each span element, the lowering decides what surrounds it: a solid
(mould compound — the centreline point lies inside it) or nothing (an air cavity).
- **In a solid**: the element couples to the host by the line-source ("well") conductance per unit length
  g′ = 2πk_host / ln(r_e / r_w), with r_e the equivalent radius derived from the local host element size.
  Whether the curve is **embedded** in the host mesh (shared nodes) or coupled through the host element's
  interpolation (non-conforming) is **brief 72 Q1's answer**; the RESOLVED entry records which was built and
  why. W4 gates it either way.
- **In air**: no coupling, unless the setup's `WireConvectionH` gives one (h × perimeter per unit length to
  an ambient).
- A wire that leaves a mould compound partway is handled element by element.

**`R-em3d77-3f`** Wires now appear in the thermal mesh; brief 74's *"wires left out"* note is removed.

## 4. `R-em3d77-4` — conductive balance

**`R-em3d77-4a` The system.** Unknowns: T on every node and wire node, φ on conductor and wire nodes.
Residuals: the conduction equation (§2) and ∇·(k(T)∇T) + σ(T)|∇φ|² + q_src = 0, with the wires' 1D forms.
**Newton on the coupled system** with the full Jacobian — dσ/dT in both equations, dk/dT, and the Joule term's
dependence on φ and on T. Direct: sparse LU. Iterative: BiCGStab with a block preconditioner (AMG on the
thermal block, AMG on the electrical block). Damped by halving on a residual that does not fall.

**`R-em3d77-4b` The switches.** `Balance.SigmaOfT` and `Balance.KOfT` (brief 73 §5b), both on by default,
shown in the Setups dialog's Balance group (brief 75 §3 adds `SigmaOfT` now). Off means σ at 20 °C (resp. k
at its nominal value) everywhere, for every material — so running twice shows each one's share. The notes of
every run state both switches.

**`R-em3d77-4c` Converged**: max temperature update below tolerance × span **and** both residuals down by
1e-8; iteration count in the notes. The owner expects fewer than five iterations away from the limit — the
tests record the actual counts at 0.25, 0.5 and 0.9 of I* (W1), and the RESOLVED entry states them.

**`R-em3d77-4d` The reference path.** A fixed-point (Picard) iteration — solve φ at the current T, then T at
that heat, repeat — is kept in `src/Thermal/Nonlinear` for the tests to compare against. Not a user option.

## 5. `R-em3d77-5` — thermal runaway is a result

Between fixed-temperature ends, no steady state exists above a current I* (overview §1g). Along a sweep:
- each point warm-starts from the last converged one (the HB drive-ladder pattern: a converged neighbour, not a
  cold start);
- a point that does not converge is retried from the last converged point with the swept value **bisected**
  toward it, until the bracket is 0.5 % wide or the step converges;
- when the bracket closes without convergence, the run reports *"No steady state above about X A: thermal
  runaway. The last converged point is Y A (wire w1[3] at Z °C)."* Points above X carry no temperature; their
  cubes hold NaN and a `runaway` flag per point. The run **succeeds** — this is an answer.
A point that fails for any other reason (a singular system from a floating conductor, a bad material) is
reported as that failure, never as runaway.

## 6. `R-em3d77-6` — the wire table

Per wire (`w1[k]` names), per sweep point, in the `DataSet`: `Twire:<name>(s)` over s (the 3D arc length,
metres, 0 at the start heel), `Twire:<name>:max` and where it is, the wire's DC current, its dissipated power,
and its resistance at its solved temperature. A `Wire` probe (brief 73 §4b) reads these. Brief 75 §5 draws
T(s) on the wire solid; the arc length the viewer uses is the one written here.

## 7. Gates

1. **W1** closed form — T(x) to 1e-5 relative at 0.25, 0.5 and 0.9 I*, with `KOfT` off; the iteration counts
   recorded (asserted ≤ a ceiling taken from the first passing run).
2. **Runaway**: W1's sweep past I* reports runaway with the bracket containing the analytical I* within 0.5 %.
3. **W2** (σ(T) and k(T)): the BVP reference to its tolerance; turning `KOfT` off moves the peak by the amount
   the reference predicts (both runs gated).
4. **Newton vs Picard** agree to 1e-9 where Picard converges.
5. **Length**: a wire with a vertical rise and a ball neck — the 1D chain's total length equals the resolved
   centreline's 3D arc length to 1e-12 and differs from its plan length (the assertion that proves the test
   would catch a plan-length bug).
6. **Area**: a hexagon-section wire and a round one of the same diameter give identical thermal results.
7. **W4** mould coupling: the fin-with-conductance closed form within the tolerance brief 72 recorded, on two
   host mesh sizes (the equivalent radius is what makes it mesh-independent).
8. **W5** (Shah) — when brief 72 has it: the paper's temperature rise and fusing current within the tolerance
   its README states. Until then this gate is `Skip` with that reason.
9. **Current share at DC**: two parallel wires of different length between the same pads share current in
   inverse proportion to their resistances at their solved temperatures (to 1e-6).
10. **Contacts**: a current entering a pad through its port contact face — the contact is equipotential (the
    potential's spread over the face below 1e-9 V).
11. **End to end** — `[GmshFact]`: a pad, a lead, three wires, a mould block, a DC sweep of 3 points through
    `em`; the `.npy` holds the wire table; under ~5 s or `Category=Benchmark`.

## 8. Owner check list (Debug build)

1. On an output-wire model: set port 1 to carry a swept DC current; Simulate; see each wire coloured along its
   length; hover to read T and s.
2. Turn `KOfT` off; rerun; compare the hottest wire in the probe table.
3. Extend the sweep past the runaway current; read the runaway line in Messages.
4. Remove the mould compound; rerun; compare.

## 9. Scope

- **DC only** here; harmonics are brief 78's.
- **No EM solve** in the loop (overview §1h).
- **No change** to any EM lowering: a Palace or openEMS run on the same document is byte-identical.
- Findings in `src/Thermal/RESOLVED.md`, `src/Design/RESOLVED.md`; never `CLAUDE.md`.

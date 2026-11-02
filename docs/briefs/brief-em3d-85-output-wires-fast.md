# Brief 85 — Bond-wire temperatures in seconds, and the *Thermal Output Wires* example

**Tag:** `R-em3d85-n` · **Series:** follows [brief 81](brief-em3d-81-thermal-showcase.md), which shipped two of its three
thermal examples; this brief is the third (its §3) and what stopped it.
**Area:** `src/Thermal/Electrothermal/`, `src/Thermal/Nonlinear/` (the conductive-balance solve), `src/Design/Thermal/`
(the run service's electrothermal path), `examples/Thermal Output Wires/` (new), `examples/examples.json`,
`docs/user/src/reference/thermal.md`, `tests/`
**Depends on:** 77 (conductive balance, wires as 1D elements), 78 (RF harmonic currents), 79 (currents from a circuit's HB)
· **Blocks:** —

---

## 0. The short answer

A bond wire is a 1D chain of about thirty elements. The owner's expectation is that a wire-temperature run — a DC current
sweep, RF harmonics, or a harmonic-balance sweep driving the wires — takes **seconds**. It does not: brief 81 measured
**minutes per sweep point** on models the wires are a tiny part of, and the harmonic-balance link cannot be used at all with the
ports an output network actually has. This brief fixes both, then ships the example brief 81 §3 describes.

## 1. What is wrong (measured in brief 81, Debug build, Apple M4)

### 1a. The coupled solve is slow, and the cost is not the wires

Conductive balance (brief 77) solves temperature T on the whole 3D mesh and potential φ on every conductor that carries a
port's current, with the wire chains, as ONE Newton system: BiCGStab with a block smoothed-aggregation AMG on the iterative
path, LU on the direct one.

| Model (six 25.4 µm gold wires, die pad → lead, in mould) | Size | One DC point |
|---|---|---|
| die, flange, alumina standoff, lead, mould; order 2 | 546k tets, 739k nodes | > 16 min, not finished (iterative) |
| same, order 1 | 99k nodes | > 15 min, not finished (iterative); > 7.5 min and 4 GB, not finished (direct) |
| compact (2.1 × 1.2 mm), order 1, one element through thin solids | 54k tets, ~10k unknowns estimated | > 2 min, not finished; Gmsh took 1.8 s |

A `dotnet-stack` sample of every run sat in `LinearSolver.BiCgStab` → `SmoothedAggregationAmg.Cycle` (inside
`ConductiveBalance.PicardSweep` for the cold start, then the Newton steps). Brief 77's own end-to-end gate (28k unknowns,
three points) already takes 36 s. The pure-thermal runs of the same size solve in seconds (brief 81's *Eight Fingers*,
89k unknowns, a Newton over k(T) and an 8 × 8 Rth matrix, in well under a minute) — so it is the **coupled φ/T system**
that the preconditioner does not handle, not the mesh.

**First step: measure, don't guess.** Log BiCGStab's iteration count and final residual per Newton step, and the Newton
step count per point, into the run's notes (they are the numbers a user needs anyway). The likely causes, each to be
confirmed or ruled out by that log:

- the perfect-bond penalties (1e4 electrical, 1e6 thermal, brief 77 RESOLVED) and the floating equipotential contacts
  make the φ block very badly scaled against the T block, and one monolithic AMG hierarchy over both does not see it;
- the φ block is small (only current-carrying conductors) but is carried through every AMG level of the whole system.

### 1b. What to build

In this order, stopping when the gate (§3) is met:

1. **Segregate the blocks in the preconditioner.** φ lives on a few thousand nodes: factor its block directly
   (Cholesky; it is symmetric positive definite once each group's reference is fixed) and keep PCG + AMG for the T block,
   as a block Gauss-Seidel preconditioner for the Newton system — or as the outer iteration itself, with Newton only on
   the coupling. The pure-thermal solver's speed is the target for the T half.
2. **Equipotential pads and leads when their resistance is negligible.** The pads and lead are 10–100 µm of copper or gold
   carrying amperes over millimetres: their resistance is micro-ohms against the wires' tens of milliohms. Treating each
   such conductor as ONE equipotential φ unknown (a lumped node, its Joule heat stated as zero and said so in the notes)
   leaves φ on the wire chains only. Then the 3D is a linear-in-T thermal solve with the wires' heat as line sources and
   Newton runs on 1D chains: seconds. Keep the full 3D φ solve available for a conductor that is not negligible (a thin
   long trace), chosen by a stated ratio — never silently.
3. Only if 1 and 2 are not enough: a Schur complement on the wire chains.

### 1c. Ground-referenced ports are refused

`ElectrothermalSystem` checks each port's current path on its own: a port whose + and − contacts are not joined by a
conductor is refused. An output network's ports are each referenced to the flange (ground) across an insulating die, so
**each port alone has no path** — port 1's current returns through port 2. Brief 79's `FromCircuit` link (port p = pin p)
therefore cannot drive any realistic output network; brief 79's own gate only works because its two ports both span the
pad–lead gap.

**The owner chose the fix (brief 81): accept a balanced superposition.** A port whose contacts lie in two different conductor
groups is allowed when every group it touches is also touched by another port; at every sweep point the currents into
each such group must sum to zero — to **0.1 % of the largest current** (a circuit's DC pin currents from an EM S-parameter
model balance only to the model's own shunt leakage) — or the run is refused, naming the ports. Each group's φ reference is
a contact **in that group** (the lowest port's). A port alone with no path keeps today's refusal, word for word; a port whose
contacts share a group keeps exactly today's reference, so no existing answer moves.

Brief 81 implemented this and reverted it unverified, because every test of it runs the slow solve in §1a. The change is
~40 lines in `ElectrothermalSystem`'s constructor (the path check and the reference choice) and one check in `Assemble`
before the currents are loaded. Re-implement it after §1b, with its gate (§3.2).

### 1d. The shipped library cannot give this model an EM solve

The Output Wires view needs a Palace S-parameter run (brief 79's link reads it). `generic-materials.cmat`'s
`Silicon carbide (4H, semi-insulating)` and `Mould compound (generic)` state thermal properties only, and a technology may
not redefine a library material (it is a refusal to load). Brief 81 had to add εr to the **workspace's copy** of the library.
Decide: add εr and tanδ to those two shipped records (SiC 9.7; mould compounds are typically 3.5–4.5), or keep the
workspace-copy route and say so in the example's README.

**Decided 2026-09-29, already done:** every thermal-era record of the shipped library now states what it is — SiC, GaN, CVD
diamond, the mould compound and the TIM an εr and tanδ, the five metals and alloys a σ₂₀, each with its source — so a model
built from it takes an EM solve with no workspace copy edited.

### 1e. Already fixed in brief 81 (context, not work)

- A face that was both a boundary and a probe's or current's contact wrote two Gmsh physical groups of one name, and Gmsh
  refused the second (`GmshGeoWriter.Thermal`).
- A placed layout's `.wBond` wires reported their pad tops in the layout's own frame, so under a placement with a z offset the
  thermal chain's feet and contact patches landed inside the die and touched nothing: every current through them had "no
  path" (`C3dElaborator`, the wire reports of a layout instance).

## 2. The example — brief 81 §3, unchanged in intent

*Thermal Output Wires*: a die pad, a row of gold wedge–wedge output wires to a package lead, in a generic mould compound whose
glass transition is the wires' probe `LimitC`; the row as a `.wBond` reached through a layout AND, in a second cell, as `.c3d`
wires (brief 78's identity). Setups `DcSweep` (past runaway), `RfHarmonics` (DC plus two harmonics, one Peak, one RMS),
`FromHB` (a small schematic — a shipped nonlinear FET, a bias network, this view's S-parameters, a load). Quote the hottest
wire and its temperature, the runaway current, the `LimitC` sentence, the k(T)/σ(T) A/B, the W1 closed-form check for one
isolated wire, and the outer-EM number (brief 81 §3).

What brief 81 learned about authoring it:

- Pads layout: one `Pad Metal` conductor (10 µm gold), placed at the die's top; die pad and lead finger coplanar; the lead a
  copper box on an alumina standoff so it is insulated from the flange. The wires' names are `U1/wire/<array>/<n>`
  (1-based) and a layout pad's top face is `<pad>/top`, not `zmax`.
- The pads technology has no ground-reference conductor, so `check` warns once per document that uses it. Either give the
  technology a ground conductor (and re-derive the wires' z origin) or state the warning in the README.
- `.wBond` `GroundPlaneEnabled: false`, so the layout's wires and the drawn ones share current identically (brief 78).
- For `DcSweep`/`RfHarmonics` one port with an explicit `LeaveFace` on the die pad's inner edge avoids §1c; `FromHB` needs §1c.

## 3. Gates

1. **Seconds.** The example's `DcSweep` (at least four points, one past runaway) runs through `em` in **under 30 s** in the Debug
   build, and `FromHB` over a 10-point drive sweep in under a minute. Report the time; do not add a timing test (counters only —
   e.g. BiCGStab iterations per Newton step below a stated bound).
2. **Superposition.** Two ground-referenced ports on an insulating layer with +I and −I run, and the wires carry I between them;
   −I/2 against +I is refused naming both ports; every existing electrothermal gate (brief 77–79) unchanged.
3. **No answer moves**: brief 77's W1–W4 references and brief 78/79's gates pass unmodified with the new solve path.
4. Brief 81's example gates for the new workspace: registered, `check` clean, numbers in `expected-numbers.json` quoted
   verbatim by README and `thermal.md`, vendor scan, no personal paths.

## 4. Scope

- No change to the pure-thermal solver (briefs 74–76, 80).
- Findings in `src/Thermal/RESOLVED.md` and `examples/RESOLVED.md`; never `CLAUDE.md`. Doc sources only; DocGen is not run.

# Brief 22 — package RLC: electrostatic and magnetostatic

**Series:** [3D EM, second series](brief-em3d-20-overview.md) · **Tag:** `R-em3d22-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §2 ("exactly package RLC extraction"), §4.2, §10 F1
and Validation
**Area:** `src/Design/Layout/Em/EmSetupModel.cs` + `EmSetupPersistence.cs` (`Problem3D`,
`Terminals3D`), `src/Design/Layout/Em3d/Em3dGenerator.cs` (terminals), `src/Engine/Em3d/Em3dProblem.cs`,
`src/Engine/Em3d/Em3dStaticResult.cs` (new), `src/Design/Em3d/PalaceConfigWriter.cs`,
`src/Design/Em3d/PalaceRun.cs`, `src/Design/Em3d/Em3dRunService.cs`, `src/Cli/CliEntry.cs`,
the `.cem` panel
**Depends on:** 21 (progress parser) · **Blocks:** 29 (potential field), 30 (the RLC example)

---

## 0. What this brief delivers

```
circuitrf em package.cem          # Problem3D: Electrostatic, Terminals3D: [RF_IN, RF_OUT, VDD]
Maxwell capacitance (fF), ground = GND
            RF_IN    RF_OUT     VDD
  RF_IN    142.31    -3.07    -11.84
  RF_OUT    -3.07   139.88    -12.02
  VDD      -11.84   -12.02    410.56
```

This is Palace doing something no planar tool in circuitRF can: the capacitance and inductance of
arbitrary 3D metal (leads, wires, a lid), from one short run. Electrostatics is a scalar problem and
solves in seconds, so it is also the quickest thing to demonstrate.

---

## 1. `R-em3d22-1` — the problem type

**`R-em3d22-1a`** `EmSetup.Problem3D` is `Driven | Electrostatic | Magnetostatic`. Brief 23 adds
`Eigenmode`. It is nullable, null means `Driven`, and it is omitted at default, so every existing
`.cem` is unchanged byte for byte.

**`R-em3d22-1b`** `Electrostatic` and `Magnetostatic` are **Palace only**. On `OpenEms` or `Both` the
run is refused, naming Palace as the solver that runs them (overview §7). `check` reports the same
refusal without running.

**`R-em3d22-1c`** A static setup ignores the frequency sweep and the ports. `check` names each at
**info** if they are set, the way a 3D setup names the planar-only fields (R-em3d3-3b).

---

## 2. `R-em3d22-2` — terminals

**`R-em3d22-2a`** `EmSetup.Terminals3D` is a list of `{ Name, Net }`. `Net` names a net of the layout,
or a `.wBond` wire group resolved through the same lookup ports use. The generator collects every
conductor solid on that net into the terminal. **The ground terminal is the setup's `Ground3D` net**,
which defaults to the net the generator already treats as the ground reference. It is not listed as a
terminal and forms the matrix's reference.

**`R-em3d22-2b` Conductors not named in any terminal are floating**, and that is a choice, not a
default to hide.
- **Electrostatic:** a floating conductor is solved as an isolated equipotential (Palace's
  `ZeroCharge` is *not* that; check what the pinned schema offers, and if it offers no floating
  conductor, refuse with the conductor's name rather than grounding it silently).
- **Magnetostatic:** an unlisted conductor carries no source current.

`explain` lists which conductors are in which terminal and which are floating.

**`R-em3d22-2c` Names are attached to named objects, never to face indices** (series 1 rule 3). A
terminal whose net yields no conductor surface after meshing is a refusal naming the terminal (the
brief 7 entity check, extended).

---

## 3. `R-em3d22-3` — electrostatic

**`R-em3d22-3a`** Configuration from the **pinned schema** (`testdata/em3d/palace-schema/0.18.1.json`),
never from memory:
- `Problem.Type: Electrostatic`;
- a `Boundaries.Terminal` entry per terminal on its conductors' void surfaces;
- `Boundaries.Ground` on the ground conductors;
- the air box per brief 3 §6;
- `Solver.Electrostatic` settings.

Dielectric domains are as for driven. Conductivity is irrelevant here, and a comment says so.

**`R-em3d22-3b` Read back by column name** from the capacitance CSV the pinned version writes. Find its
file name and header by running one, and commit that run's output as a fixture under
`testdata/em3d/static/` (it is small). **Record both the Maxwell matrix and the mutual form** if
Palace writes both, and label which is which. Users mix them up constantly, and the diagonal means
different things in the two.

**`R-em3d22-3c` The result:**
- `Em3dStaticResult` → a `DataSet` holding cube `C` (real, farads) on axes `Terminal i` × `Terminal j`,
  labelled with terminal names, plus `C_mutual` if written;
- `.npy` under key `<key>.palace_es`;
- **no `.sNp`**;
- `em` prints the matrix on stdout in engineering units, and `--json` gives the cube.

---

## 4. `R-em3d22-4` — magnetostatic

**`R-em3d22-4a`** Each terminal needs a **current path**: where current enters and where it returns.
A terminal therefore also names a **source sheet**, `{ Name, Net, Source: <port name> }`, reusing a
lumped port's geometry from brief 3 as the surface where Palace's `SurfaceCurrent` is applied. If a
magnetostatic terminal has no source, refuse and say what is missing.

**`R-em3d22-4b` External inductance, and the notes say so** (overview §1g). Conductors are voids, so
the solve returns the high-frequency (external) inductance with no internal inductance. The result's
notes state that, give the size of the omitted internal term for the thickest round conductor in the
problem (μ₀ℓ/8π per round wire), and say it is the DC limit that differs.

**`R-em3d22-4c`** The result has cube `L` (henries), same axes; `.npy` key `<key>.palace_ms`; no
`.sNp`; `em` prints the matrix.

---

## 5. `R-em3d22-5` — progress and panel

**`R-em3d22-5a`** The brief 21 parser gains the static solves' log lines, anchored on the fixture runs
in `R-em3d22-3b`: one solve per terminal, so progress is *terminal k of N*.

**`R-em3d22-5b`** The panel adds a *Problem* picker (Driven / Electrostatic / Magnetostatic) and a
terminal table (name, net, source). Nothing that affects the answer lives only in the panel.

**`R-em3d22-5c`** The Data Display shows a matrix cube as a table. Check what it does today with a
two-axis real cube before writing anything new. If it already tabulates, use that.

---

## 6. Gate

`tests/Ui.Tests/Em3d/PalaceStaticTests.cs`. Closed forms are computed **in the test, from the formula**,
never from a circuitRF run (CLAUDE.md, references are external).

1. **Writer goldens.** The electrostatic and magnetostatic `config.json` for a two-terminal fixture,
   byte for byte. Both validate against the committed schema.
2. **Parallel plates with PMC side walls.** Two square plates with PMC air-box sides and no fringing
   field: C = ε₀εᵣA/d within **1 %**. *(Palace.)*
3. **Coax, electrostatic.** A coaxial section with natural (Neumann) end faces:
   C = 2πε₀εᵣℓ / ln(b/a) within **1 %**. *(Palace.)*
4. **Coax, magnetostatic.** The same section shorted at the far end and driven at the near end:
   L = μ₀ℓ/(2π) · ln(b/a) within **2 %**. This is external inductance, so it is the exact comparison.
   *(Palace.)*
5. **Symmetry.** Every C and L matrix from gates 2–4 is symmetric to 1e-6 relative. The Maxwell
   diagonal is positive and the off-diagonal is non-positive.
6. **Refusals before work.** openEMS or Both with a static problem, a terminal with no conductor, and a
   magnetostatic terminal with no source are each refused with Gmsh invoked 0 times.
7. **No `.sNp`.** A static run writes none, and a planar setup's paths are unchanged.
8. **Existing `.cem`s.** Every `.cem` in the repo round-trips byte-identically. `Problem3D` is omitted
   at default.

Tag gates 2–4 `Category=Benchmark` only if they exceed ~5 s. Keep each case small enough that they do
not.

## 7. Owner check

- The terminal table in the panel, and the matrix table in the Data Display.

## 8. Scope

- No lumped-circuit synthesis from the matrices (a subcircuit a schematic can place). That is a good
  follow-on and is listed in `em-3d.md` §2, but it is not here.
- No frequency-dependent RLC. That is the driven solve's job.

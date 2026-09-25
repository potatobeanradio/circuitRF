# Brief — 3D full-wave EM, first series: F0, the F1 core and F1b

**Status:** Briefed, not built · **Date:** 2026-09-24 ·
**Design note:** [`docs/design/em-3d.md`](../design/em-3d.md) rev 4 (Draft)
**Area:** `testdata/em3d/` (new), `src/Design/Layout/TechModel.cs`, `src/Design/Layout/TechPersistence.cs`,
`src/Engine/Em3d/` (new), `src/Design/Em3d/` (new), `src/WBond/WBondDesign.cs`, `src/Render/`,
`src/Cli/`, `src/Ui/` (Settings rows only), `tests/`
**Requirement tag for the series:** `R-em3d<n>-<m>`, scoped per brief (`R-em3d3-5` is brief 3's fifth)

> The design note has no numbered requirements of its own. It is cited by section: *§6.5* means
> `em-3d.md` §6.5. A brief's own requirements always carry the brief number.

---

## 0. The short answer

This series gets circuitRF from "no 3D solver" to **a `.cem` that runs through Palace (FEM) or
openEMS (FDTD) from the CLI and from Simulate, with the answer landing as a `.sNp` and a `DataSet`**
like any planar run. The geometry is generated from a layout, a technology and a `.wBond` (Tier A);
nobody draws a solid.

It covers **F0 whole**, **F1's core** and **F1b whole**. It deliberately leaves out F1's
*installation half*, which becomes the next series (§4).

### The one rule the whole series is built on

> **circuitRF owns one resolved 3D problem. Each solver sees a text lowering of it, written by
> circuitRF, run as a separate program, and read back from the files it writes.**

Three consequences, each gated somewhere below:

1. **No circuitRF assembly links a solver, a mesher or their scripting interfaces.** Palace, Gmsh
   and openEMS are GPL or carry a non-commercial dependency (§2); the firewall test in brief 6 holds
   the line the way `UiFirewallTests` holds Avalonia out.
2. **Every file circuitRF writes for a solver is byte-deterministic.** Same problem, same bytes, on
   every platform. That is what makes a writer testable with **no solver installed** (a golden
   comparison), and it is what makes a re-run reuse a mesh.
3. **Nothing stores a face or volume index** (§6.4). Ports, boundaries and materials attach to
   named objects, and the mapping to solver entities is rebuilt on every run. A named object that
   yields nothing is a refusal naming it.

---

## 1. Things that are not obvious, resolved here once

Each is restated in the brief it affects.

### 1a. `EmProblem` is a 2D cross-section and cannot be grown into the 3D problem

`src/Engine/Mom/EmProblem.cs` is kernel A's input: `EmPoint(X, Y)`, laterally infinite
`EmDielectricRegion` slabs, a `LengthMeters`. It has no third axis and no lateral extent, and the
planar extractors depend on exactly that. The 3D problem is a **new type beside it**
(`Em3dProblem`, brief 3). It is not a generalisation of the old one, and nothing in `Mom/` changes.

### 1b. About 30 files read a stackup entry's four material numbers directly

`grep` counts direct reads of `Epsr`/`TanD`/`Mur`/`SigmaSm` in `PlanarExtractor`,
`CrossSectionExtractor`, `RlgcExtractor`, `StackupScene`, the Gerber/PCB stackup mappings,
`PdnCavity` and about twenty more. The note decides that an entry naming a material **uses that
material's values, for every solver** (§4.1a). Threading a lookup through 30 readers would be a
30-place change with 30 chances to miss one.

**Resolution (brief 2): resolve on read, in the one loader.** `TechPersistence` overwrites a named
entry's four numbers with the material's values as it loads. Every existing reader is then correct
with no change, and a save writes numbers equal to the material's, which is exactly what the note
asks for so that an older build reads the same values. `check` must warn when a hand edit makes the
two disagree, so it reads the **raw** file before that normalisation. This is the one place the raw
numbers are visible, so brief 2 gates it.

### 1c. Temperature: the same metal, two numbers, unless the setup says which

`WireMaterial` stores σ at 20 °C and kernel W evaluates it at the design's `OperatingTempC`, which
defaults to **85 °C** (22–25 % lower σ). A stackup entry's `SigmaSm` has always been a number the
user typed, in practice a 20 °C handbook value. If a stackup entry names "Gold", brief 2 resolves it
to **σ₂₀**, so planar answers do not move. The 3D setup then carries an **operating temperature**
(brief 3), default **20 °C**, that applies to every solid, wires included. When the included `.wBond`
states a different temperature, the run says so in a note, because it is the first thing to check
when a 3D result and kernel W disagree by 10 %.
**Owner decision D3** (§3) confirms or changes the 20 °C default.

### 1d. The foot and ball defaults may belong in the `.wasm`, not the `.ctech`

§6.6 puts the default foot length and ball size "in the technology beside §4.1a's materials", on the
grounds that *a bond process is a property of the assembly line*. That rationale describes a
document that already exists. The `.wasm` assembly rule file (`src/Design/Layout/Assembly/`,
WB31) is the assembly house's own document, with `Process`, `Machine` and `Material` rule lists
and `AllowedDiametersNm`. It resolves per workspace with a per-`.wBond` override (`AssemblyRef`),
which is the same override chain §6.6 asks for.
**Owner decision D2.** Brief 4 is written so the answer changes one resolver.

### 1e. The `.geo` language may not expose Gmsh's fragment provenance

§6.2 says Gmsh's fragment operation reports which output came from which input. That is true of
Gmsh's **API** (the `outDimTagsMap` of `occ.fragment`). The API links GPL code into the caller,
so circuitRF may not use it. As far as this series' author could establish without running Gmsh, the
`.geo` scripting language returns the fragment's output entities but **not the input→output map**.

**Resolution (brief 7), confirmed or refuted by F0:**
- **Conductors are voids, not volumes.** Palace models a good conductor as a hole in the mesh with a
  finite-conductivity boundary on its surface, and thin metal as a sheet (§6.1). Only dielectrics and
  air are meshed volumes, and circuitRF makes them disjoint **before** Gmsh sees them (subtracting
  each conductor from the slab it sits in). The fragment then only *imprints* shared faces and splits
  no volume, so with `Geometry.OCCBooleanPreserveNumbering` every volume keeps its tag.
- **Surfaces are recovered by what circuitRF knows about them.** A port sheet or a conductor
  face is a small, known shape. The script selects it with a tight `BoundingBox` query, then writes
  its own entity table (`Printf` to a file). circuitRF reads the table back and checks every named
  object got exactly the count it expected. **A mismatch is a refusal**, never a guess (§6.4).

### 1f. openEMS current probes are sampled half a time step off the voltage probes

In the FDTD update, E and H live at interleaved half steps. So a current probe's samples fall at
t + Δt/2 from the voltage probe's. Dropping that offset is invisible at low frequency and grows into
a phase error toward the top of the band. It is the classic hand-rolled-port bug. **Resolution
(brief 9): every probe file's own time column is used; no two probes are assumed to share
sampling.** F0 confirms the offset exists in the pinned version's output, and brief 9's closed-form
gate exercises it (a matched line's |S11| stays below −40 dB up to the top of the band only if the
offset is handled).

### 1g. `Auto` must never pick a 3D solver

`EmSetup.AnalysisKind` defaults to `Auto`, and L8e made `Auto` conservative: it picks a planar
kernel that accepts the geometry. A 3D solve needs an installed program, can take an hour, and would
change the number an existing `.cem` produces. So **3D is chosen only by name** (brief 3), and a
test asserts that no geometry makes `Auto` resolve to it.

### 1h. The reference data must be produced without circuitRF's generator

CLAUDE.md: references are externally generated. The trap is specific: once brief 3 exists, the
easy way to make a reference is to run circuitRF's generator and commit the solver's answer. That
proves nothing, because a generator bug appears in the "reference" too (§4.4 says the same of
FEM-vs-FDTD agreement). **F0's hand-written solver inputs are the references**, committed with their
outputs in `testdata/em3d/`. Brief 7 and brief 9 each gate on circuitRF's own generated run matching
them, plus one closed form per backend.

### 1k. FDTD and FEM do not model the same dielectric loss, and the cross-check must say so

Palace holds tanδ constant with frequency. A constant-conductivity FDTD material reproduces a given
tanδ at **one** frequency only (for fixed σ, tanδ ∝ 1/f). Brief 9 fits it at the band centre and
states that frequency. Brief 10's comparison names it as the expected source of any |S21|
difference that grows away from band centre on a lossy substrate. Without that sentence, a user
sees two solvers disagree at the band edges and distrusts both (§12). A broadband dispersive fit is
a later brief.

### 1i. Tests that need a solver skip, with a reason, when it is absent

CI and a fresh clone have no Palace, Gmsh or openEMS. `RfCore.Tests`' `FixtureFact` is the precedent:
a test whose input is not on the machine reports **Skipped, with a reason**, never Failed and never a
silent pass. Every gate in this series is split in two:
- a **writer** half: circuitRF's text output compared byte for byte against a committed golden,
  needing no solver. This is the routine gate.
- a **run** half: the solver actually invoked, which skips without it. It is kept to a small problem
  (seconds, per the standing "keep EM runs short" practice) and tagged `Category=Benchmark` if it
  crosses ~5 s.

No test in this series asserts a wall-clock time. Structural properties are counters: *one Gmsh
invocation per run*, *zero solver processes started by `explain`*, and so on.

### 1j. The planar result path must not move

`EmRunService.ResolveSnpPath` is predictable so that a schematic's SnP reference survives a re-run.
Putting the solver in the path (§4.5) applies to **3D results only**. A planar run's path is
byte-identical to today's, and a test says so.

---

## 2. The briefs

| # | Brief | Delivers | Phase | Depends on |
|---|---|---|---|---|
| 1 | [the F0 spike](brief-em3d-1-f0-spike.md) | hand-installed solvers, two cases through both, the references in `testdata/em3d/`, the findings report, the F1-vs-F1b call | F0 | — |
| 2 | [materials and bodies in the technology](brief-em3d-2-materials-and-bodies.md) | `Materials`, `StackupLayer.Material`, `Bodies`; resolve-on-read; `check` | F1 | — |
| 3 | [the 3D problem and the layout generator](brief-em3d-3-the-3d-problem.md) | `Em3dProblem`; Tier A from `.clay` + `.ctech`; lumped 3D ports; air box; the `.cem`'s solver fields | F1 | 2 |
| 4 | [bond wires in 3D](brief-em3d-4-bond-wires.md) | hexagon/round sweeps, bond style per end, feet, balls, the assembly loop height | F1 | 3 (and D2) |
| 5 | [see it before solving it](brief-em3d-5-render-and-explain.md) | `render` section views of a 3D setup; `explain`'s solid list and size report | F1 | 3 |
| 6 | [finding the solvers](brief-em3d-6-finding-the-solvers.md) | discovery, validated versions, capability probe, Settings rows, the GPL firewall test | F1 | 1 |
| 7 | [the Palace backend](brief-em3d-7-palace-backend.md) | `.geo` → Gmsh → Palace JSON → run → `.sNp` + `DataSet`; the `em` verb; solver in the path | F1 | 1, 3, 6 |
| 8 | [the FDTD grid](brief-em3d-8-fdtd-grid.md) | the per-axis grid generator (§6.5), managed, no solver needed | F1b | 3 |
| 9 | [the openEMS backend](brief-em3d-9-openems-backend.md) | CSXCAD XML writer, run, circuitRF's own port transform | F1b | 1, 6, 8 |
| 10 | [running both](brief-em3d-10-run-both.md) | one setup through both, the difference `DataSet` | F1b | 7, 9 |

**Build order.** Briefs 2, 3, 4, 5 and 8 need no solver and nothing from F0, so they can start
the day this series is approved, in parallel with the spike. Briefs 6, 7 and 9 wait for F0's
validated versions and its answers to §1e and §1f.

**F1 or F1b first is F0's call** (§10 of the note, and brief 1's last deliverable). Nothing in this
series presumes the answer. Brief 7 and brief 9 each depend only on 3 and 6, so either can be built
first, and the one built first carries the solver-in-the-path rule (brief 7 §5, repeated in 9).

---

## 2A. Traceability — the note's sections, and the brief that builds each

| Note § | Content | Brief |
|---|---|---|
| §4.1 | the solver-neutral problem | 3 |
| §4.1a | materials, `StackupLayer.Material`, bodies, lateral extent, wire metals via the technology | 2 (document), 3 (lateral extent), 4 (wire metals) |
| §4.2 | shared vs per-solver sections in the setup | 3 (the fields), 7 and 9 (each section's contents) |
| §4.3 | which solver fits, as guidance | 5 (`explain` states it) |
| §4.4 | running both; reference planes | 3 (reference plane on the port), 10 |
| §4.5 | solver in the result path | 7 (or 9, whichever is built first), 10 |
| §4.6 | headless authoring: 3D ports, `render`, `explain` size | 3, 5 |
| §5.3 | validated versions; openEMS S-parameters computed by circuitRF; results like planar; `em` first | 6, 9, 7 |
| §6.1 | Gmsh as a program; physical groups; thin metal as sheet | 7 |
| §6.2 Route A | OCCT through Gmsh | 7 |
| §6.3 Tier A | generated geometry | 3, 4 |
| §6.4 | naming, no face indices | 3 (names), 7 (Gmsh map), 9 (CSXCAD names) |
| §6.5 | CSXCAD lowering, priorities, the grid | 8, 9 |
| §6.6 | bond wires in 3D | 4 |
| §7.1 | discovery, version check, capability probe, named solvers, licence note | 6 |
| §10 F0 | the spike | 1 |
| §10 Validation | closed forms + F0 references | 7, 9 |
| §12 | GPL boundary risk | 6 |

**Not in this series, on purpose (§4):** §7.2 (install assistant, uninstall, uninstalling
circuitRF), §7.3's container and remote locations, §7.4 (the Linux subsystem on Windows), §7.5, and
from F1 the electrostatic/magnetostatic RLC solves and wave ports.

---

## 3. Decisions needed from the owner

None blocks briefs 2, 3, 5 or 8. Each is stated where it bites, with the brief's default.

| # | Decision | Brief's default | Blocks |
|---|---|---|---|
| D1 | The run verb: `em` on a 3D `.cem`, or a sibling verb (note Open 1) | **`em`**, as §4.6 leans. The solver is a field of the setup, overridable with `--solver` | 7 |
| D2 | Foot length / ball size defaults: `.ctech` (note) or `.wasm` (§1d) | **`.wasm`**, if the owner agrees; otherwise `.ctech` exactly as the note says | 4 |
| D3 | 3D setup operating temperature default (§1c) | **20 °C**, so a 3D result is comparable with planar as shipped | 3 |
| D4 | F1 or F1b first | **F0's recommendation** (brief 1) | 7 vs 9 |
| D5 | This series' scope: installation half deferred (§4) | as written | — |

---

## 4. What is deferred, and why

**The next series** carries F1's installation half. It is deferred for a reason, not for lack of
space. The note says the install assistant's table of known failures is *learned, not guessed*, and
it is seeded by F0's repeated installs on each platform (§7.2). Its recipes are data per validated
version, and the validated versions are F0's output too. Briefing the assistant before F0 would mean
guessing the two things the note forbids guessing. The deferred items:

- the install assistant, its uninstall and "Remove all 3D solvers" (§7.2);
- *Uninstall circuitRF…*, the Windows Apps-list routing, and the upgrade-never-removes gate (§7.2);
- the Linux-subsystem location on Windows (§7.4), container and remote locations (§7.3, §7.5);
- the installation page per platform. Brief 6 writes a **manual** install section, because a
  refusal must be able to point at something. The per-platform page and the assistant's consent text
  come with the assistant.

**Until that series lands, a Windows user has openEMS (native, brief 9) and no Palace.** The Palace
refusal says so plainly and names openEMS as the solver that runs on this machine. It does not
promise a feature that is not built.

**Also deferred from F1, into a short third series:**
- **Electrostatic/magnetostatic RLC.** These need terminal definitions rather than ports, and output
  a matrix rather than S-parameters. That is a different result model and a different gate
  (parallel-plate C, coax L), and it rides on brief 7's pipeline unchanged.
- **Wave ports.** They need Palace's eigensolver capability and openEMS's waveguide or microstrip
  port. The lumped port is the one port both backends state identically, which is what brief 10's
  cross-check needs first. Wave ports follow immediately after.

---

## 5. Where the code goes

```
testdata/em3d/                    NEW — F0's hand-written references (brief 1)
    f0/<case>/<solver>/           inputs as written by hand, outputs as the solver wrote them
    README.md                     versions, machine, commands, timings

src/Engine/Em3d/                  NEW — numeric layer, no processes, no files
    Em3dProblem.cs                  the neutral problem (brief 3)
    FdtdGrid.cs                     the per-axis grid generator (brief 8)
    FdtdPortTransform.cs            probe time series -> S (brief 9)

src/Design/Layout/Em3d/           NEW — Tier A: documents -> Em3dProblem
    Em3dGenerator.cs                .clay + .ctech (brief 3)
    Em3dWires.cs                    .wBond -> swept solids, feet, balls (brief 4)

src/Design/Em3d/                  NEW — the backends: lowering, process, read-back
    SolverDiscovery.cs              Palace, Gmsh, openEMS (brief 6)
    GmshGeoWriter.cs                problem -> .geo (brief 7)
    PalaceConfigWriter.cs           problem -> config JSON (brief 7)
    PalaceRun.cs                    stage, run, read port-S.csv (brief 7)
    CsxcadWriter.cs                 problem + grid -> XML (brief 9)
    OpenEmsRun.cs                   stage, run, read probes (brief 9)
    Em3dRunService.cs               behind EmRunService.Run (brief 7; brief 10 adds "both")

src/Render/Renderers/Em3dSectionRenderer.cs   NEW (brief 5)
src/Cli/                          em dispatch (7), render/explain on a 3D .cem (5)
tests/Engine.Tests/Em3d/          grid, port transform
tests/Ui.Tests/Em3d/              generator, writers (goldens), runs (skip without solver)
tests/Firewall.Tests/             the GPL boundary (brief 6)
```

**`EmRunService.Run(...)` stays the one door**, as `LvsRun.Run` is for LVS. When the setup's
`Solver3D` is set it delegates to `Em3dRunService`; no caller branches. The `em` verb, the `.cem`
panel's Simulate and `EmCliVerbTests` therefore reach 3D with no change to themselves, and a source
scan holds that nothing else calls `Em3dRunService`.

---

## 6. The series' own gate

**`R-em3d0-1`** Every brief listed in §2 exists and every link in §2 resolves.

**`R-em3d0-2`** On completion of the whole series: a `.cem` naming each solver runs from the CLI
on a machine with that solver installed and produces a `.sNp`. The same `.cem` on a machine
without it refuses and names the missing program, with exit 1 and no crash. On Windows, the Palace
refusal names openEMS.

---

## 7. Scope for the series

- **No viewer.** F2. `render`'s section views (brief 5) are the only picture of a 3D model.
- **No thermal.** F3.
- **No editable 3D view, no geometry worker, no OCCT in-process.** F4.
- **No change to any planar answer, any planar result path, or any shipped example's numbers.**
  Brief 2 is gated on the planar solvers producing identical numbers for every shipped technology.
- **No per-primitive edit verbs.** A 3D setup is authored by writing the `.cem`, `.ctech` and
  `.wBond` (§4.6).
- **On completion of each brief, findings go in the relevant `RESOLVED.md`**
  (`src/Design/RESOLVED.md`, `src/Engine/RESOLVED.md`, `src/Cli/RESOLVED.md`), never `CLAUDE.md`.
  Doc sources are edited; DocGen is not run per brief.

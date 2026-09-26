# Brief — 3D full-wave EM, second series: Palace, shown

**Status:** Briefed, not built · **Date:** 2026-09-25 ·
**Design note:** [`docs/design/em-3d.md`](../design/em-3d.md) rev 5 (Draft), and
[`em-3d-f0-findings.md`](../design/em-3d-f0-findings.md)
**Previous series:** [`brief-em3d-0-overview.md`](brief-em3d-0-overview.md) (briefs 1–10, all built and
reviewed)
**Area:** `src/Design/Em3d/`, `src/Engine/Em3d/`, `src/Render/` (a 3D scene model, no GPU),
`src/Ui/` (the 3D pane, the install dialogs, Settings), `src/Cli/`, `packaging/`, `examples/`, `tests/`
**Requirement tag for the series:** `R-em3d<n>-<m>` as before. This series is numbered **20–30**, so
`R-em3d24-3` is brief 24's third requirement and can never be mistaken for series 1's.

---

## 0. The short answer

Series 1 made Palace **run**: a `.cem` goes through Gmsh and Palace and comes back as a `.sNp`. This
series makes that **worth showing to someone**. When it is done:

- **a run you can watch.** The progress names Palace's real stages: meshing, refinement pass *i* of
  *N*, the sweep's error converging on its tolerance. Before starting, circuitRF says whether the run
  fits in memory. It offers quality presets whose cost and accuracy were measured.
- **Palace's other problem types**: package RLC (capacitance and inductance matrices), wave ports and
  eigenmodes. These are the things a planar tool cannot do at all, and they make the strongest case
  for Palace.
- **A model you can see.** A GPU 3D viewer inside the application shows the generated geometry, the
  mesh or the FDTD grid, and the solved fields, including an animated phase sweep of |E|.
- **A solver that installs itself.** *Install Palace…* on macOS and Linux, and in the Linux subsystem
  on Windows. Gmsh and openEMS install on every platform. Whatever circuitRF installs, it can
  uninstall.
- **A shipped example and a user page.** Someone who has never seen circuitRF can open the example,
  install Palace, press Simulate and see the result.

This series covers the rest of **F1** (static RLC, wave ports and eigenmode, the install assistant,
uninstall, and the Windows Linux subsystem) and **all of F2**. It does not cover **FU**, **F3** or
**F4**, or the container and remote locations (§4).

### The one rule this series adds

> **Everything a demonstration shows is something the solver produced.** Progress comes from Palace's
> own log lines. Every picture comes from the solver's own output files. Every stated accuracy or cost
> was measured, and the measurement is cited. A progress bar that guesses, or a field that circuitRF
> interpolated in a way it cannot check, is worse than no feature, because the person watching is
> deciding whether to trust the tool.

This extends series 1's rule (circuitRF owns one resolved problem, and each solver sees a text
lowering of it). It does not replace it.

---

## 1. Things that are not obvious, resolved here once

### 1a. FU is the only public phase, and nothing downstream waits for it

**FU** (em-3d.md §10) is upstream work and involves no product code. It has two parts:
- help a community conda-forge recipe for Palace land (it is linux-64 only today), then extend the
  recipe to macOS;
- offer §7.4's findings on a native Windows Palace build to Palace's maintainers.

Both mean posting in public. **The owner has decided (2026-09-25) not to post anything publicly until
circuitRF's Palace integration has been demonstrated.** So no brief in this series files an issue,
opens a pull request, comments upstream or publishes anything. Anything worth offering upstream later
goes in one list in `em-3d.md` §7.4 and waits there.

**FU blocks nothing in F2, F3 or F4.**
- F2 reads the files a Palace run leaves behind, however that Palace was installed.
- F3 is circuitRF's own C# and uses only Gmsh.
- F4's geometry worker is OpenCASCADE.

What deferring FU costs is **install time and Windows reach**:
- Palace stays a from-source Spack build: 52 minutes on the F0 Mac, with three failures before it
  built, and reported as "most of a day" in the Linux subsystem.
- There is no native Windows Palace.

The install assistant (briefs 24–26) is how this series pays that cost for the user, and it is why the
assistant is in this series rather than after it.

**The upstream recipe may land without circuitRF's help.** The design says "whatever lands, §7.1's
discovery picks up with no circuitRF change". That is only true if discovery **looks in conda
environments**, which it does not do today, and which a Finder-launched app has no `PATH` for. Brief 24
adds that route, so FU's result is picked up the day it exists, whoever does the work.

### 1b. F0's Windows and Linux install rows are still owed

F0 installed each solver once, on macOS only (findings §1). The rows for three clean installs on
Windows and on Linux are marked for the owner and are not done. The install assistant's table of
known failures must be **learned, not guessed** (§7.2), so:
- **Linux can be seeded from this Mac.** Install into clean arm64 Linux containers (Docker Desktop is
  already on this machine). Brief 24 does that: three distributions, three runs each, logged in the
  F0 format.
- **Windows cannot be seeded from this Mac.** Brief 26 lists the runs the owner must make on a
  Windows machine. **It cannot close until they are logged.** None of this involves posting
  anything.

### 1c. The per-user folder lives in `src/Ui`, and the CLI must install headlessly

`AppDataRoot` (`src/Ui/AppDataRoot.cs`) is where the assistant installs (§7.2). `src/Cli` cannot
reference `src/Ui`. A headless `solver install` is part of the design (§7.2, "a build machine installs
the same way the GUI does"). So brief 24 moves the **path logic** below the firewall into `src/Design`,
the way `ShippedTechnologies` moved. It keeps the DocGen redirect intact, and `src/Ui` keeps a thin
forwarder. This is a behaviour-free move and a test says so.

### 1d. Palace writes no fields today

`PalaceConfigWriter` asks for no field output (there is no `Save` in the driven section), so the run
directories series 1 kept hold CSVs only. The viewer's fields (brief 29) need a config change, and
that changes every Palace golden. **The goldens are rewritten exactly once, in brief 29**, under the
existing `CRF_WRITE_PALACE_GOLDENS=1` switch. No earlier brief touches field output.

### 1e. Nothing in this series can be seen from an agent's session

An agent's shell cannot launch the GUI (Avalonia dies at the render timer). So:
- **every gate is a counter or a headless check**, never a timing and never a pixel;
- **every brief with a visible surface ends with an owner check list**, and its completion note says
  pixels were not seen.

This matters most for the viewer, whose whole bar is feel (§8.2). Series 1 already said "feel is judged
by hand" (§8.6).

### 1f. The GPU API is a native-dependency question, and that needs the owner

CLAUDE.md: *ask before adding native (non-managed) dependencies*. Two of the viewer's candidate GPU
routes involve one:
- WebGPU through wgpu ships a native library;
- a native-API route may need platform bindings.

**Brief 27 (the spike) measures the candidates and recommends one. The owner decides before brief 28
starts.** Brief 28 is written so the decision changes one project's references, not the scene model.

### 1g. Static solves give matrices, not S-parameters, and void conductors give external inductance

Brief 22's electrostatic and magnetostatic solves return a **capacitance matrix** and an **inductance
matrix** over named terminals. That is a different result from ports and S, so:
- it lands as `DataSet` cubes, and there is no `.sNp`;
- `em` prints the matrix on stdout.

There is also a physics point. Series 1 made conductors **voids** (F0 Q6). With voids, a magnetostatic
solve puts all current on the conductor surface, so it returns **external** (high-frequency)
inductance with **no internal inductance**. At RF that is the right number for a package. It is not
the DC inductance, and a user comparing it against a DC formula will see the gap. **The result says so
in its notes.**

### 1h. A quality preset is a claim, so its numbers are measured

Brief 21 adds `Draft` / `Standard` / `Accurate` presets. Each is a statement about cost and accuracy.
The standing practice is *document the setting you traded away*. So each preset's time, memory and
deviation from `Accurate` are **measured** on F0's cases A and B, and those numbers are what the panel
and the reference page print. **`Standard` equals today's defaults**, so no existing golden and no
existing answer moves.

### 1i. Review leftovers this series takes, and the ones it leaves

The series 1 review left six items for the owner (`src/Design/RESOLVED.md`, "Found and left").
**Taken here:**
- **MPI rank count** (brief 21): the default becomes the physical core count.
- **`--solver` on a planar `.cem`** (brief 21): it is refused rather than converted, because a
  demonstration that silently switches solvers is the wrong kind of surprise.

**Left:**
- undrawn inner ground planes;
- the integer-on-port-axis ambiguity;
- a failed comparison write exiting 0;
- an unknown enum making a `.wBond` unreadable.

None of these affects what this series shows. They are listed again in §4 so they stay visible.

---

## 2. The briefs

| # | Brief | Delivers | Phase | Depends on |
|---|---|---|---|---|
| 21 | [a Palace run you can watch](brief-em3d-21-a-run-you-can-watch.md) | stage progress from Palace's log, the memory check before a run, physical-core ranks, quality presets, the completion summary | F1 | — |
| 22 | [package RLC](brief-em3d-22-package-rlc.md) | electrostatic C matrix, magnetostatic L matrix, terminals, `Problem3D` | F1 | 21 |
| 23 | [wave ports and eigenmodes](brief-em3d-23-wave-ports-and-eigenmodes.md) | the eigensolver capability probe, wave ports, eigenmode frequencies and Q | F1 | 21 |
| 24 | [the install assistant](brief-em3d-24-install-assistant.md) | recipes as data, *Install…* for Palace (macOS, Linux), Gmsh and openEMS (all three OSes), `solver` verb, conda discovery, Linux seeding in containers | F1 | — |
| 25 | [uninstall](brief-em3d-25-uninstall.md) | per tool, *Remove all 3D solvers*, *Uninstall circuitRF…*, upgrade-never-removes | F1 | 24 |
| 26 | [Palace on Windows, in the Linux subsystem](brief-em3d-26-palace-on-windows-wsl.md) | discovery inside distributions, staging, memory, cancellation, the install inside the subsystem | F1 | 24, 25 |
| 27 | [the 3D pane spike](brief-em3d-27-3d-pane-spike.md) | composition GPU interop on three OSes, a million triangles, GPU picking; the GPU API recommendation | F2 | — |
| 28 | [the viewer: model, mesh and grid](brief-em3d-28-viewer-model-mesh-grid.md) | the scene model below the firewall, the 3D pane, Tier A geometry, Gmsh mesh, FDTD grid, picking, clip plane | F2 | 27 + owner's GPU decision |
| 29 | [the viewer: fields](brief-em3d-29-viewer-fields.md) | Palace field output, a VTU reader, \|E\| and surface current, phase animation, PNG export; openEMS fields | F2 | 28 (22/23 for their fields) |
| 30 | [the showcase](brief-em3d-30-showcase.md) | the `3D EM` example workspace, the user page, the newcomer walk-through | — | all |
| 31 | [the radiation pattern, from a 3D run](brief-em3d-31-radiation-pattern.md) | the planar far-field metrics factored out; openEMS NF2FF box; Palace spike; the panel's Radiation pattern group for 3D | — | 7, 9 |

**Three tracks, in parallel.**
- **Solver track:** 21 → 22, 23.
- **Install track:** 24 → 25 → 26.
- **Viewer track:** 27 → (owner) → 28 → 29.

Brief 30 comes last.

**Smallest demonstrable cut:** 21, 27, 28, 29 and 30, plus brief 24 on macOS only, if the owner wants
a first showing before the whole series. The others widen what can be shown. They do not change how
it is shown.

---

## 2A. Traceability — the note's sections, and the brief that builds each

| Note § | Content | Brief |
|---|---|---|
| §2 | electrostatic, magnetostatic (package RLC), eigenmode, wave ports | 22, 23 |
| §5.3 | capability checks per problem type | 23 (the eigensolver row), 22 (the static rows) |
| §7.1 | discovery: conda route, installed-by-circuitRF route | 24 |
| §7.2 | install assistant, recipes as data, known failures, uninstall, *Uninstall circuitRF…*, upgrade-never-removes, headless spelling | 24, 25 |
| §7.3 | the location setting: native and Linux subsystem | 26 |
| §7.4 | the Linux subsystem end to end | 26 |
| §8.1–8.5 | the viewer, hosting, three loops, the firewall split, what it shows | 27, 28, 29 |
| §8.6 | the spike, counter gates, feel by hand | 27 (and every viewer brief) |
| §10 Validation | closed forms: parallel plate, coax, waveguide, cavity | 22, 23 |
| §12 | problem size on a laptop; user-built configurations | 21, 23 |

**Not in this series, on purpose:**
- §7.3's container location and §7.5 remote host;
- the difference field between two solvers (§8.5, last sentence);
- FU, F3 and F4.

---

## 3. Decisions needed from the owner

| # | Decision | Brief's default | Blocks |
|---|---|---|---|
| D1 | The install verb's spelling (note Open 6) | **`solver`**: one verb with nouns (`list`, `install`, `remove`), following `history`'s pattern | 24 |
| D2 | MPI rank default | **physical cores**, overridable by `EmSolveCores` | 21 |
| D3 | The GPU API and any native dependency it brings (note Open 2) | **the spike's recommendation** | 28 |
| D4 | Eigenmode in this series | **in**: its gate is a closed form, and a mode shape is the viewer's best picture | 23 |
| D5 | Windows install runs (§1b) | **owner makes them**, three per tool, in the F0 log format | 26 closes |
| D6 | *Uninstall circuitRF…* in this series or later | **in** (brief 25 §4), because the design ties it to shipping the assistant; it can ship later without harm, because leftovers are reused (§7.2 point 4) | — |
| D7 | Field save default | **the sweep's centre frequency only**; `Palace.SaveFieldsGHz` overrides; `[]` saves none | 29 |

---

## 4. What is deferred, and why

- **FU**: an owner decision (§1a). Brief 24's conda route is the one piece of FU circuitRF does not
  have to wait for.
- **F3 (thermal)**: an owner decision, and agreed. F3 depends on the Gmsh pipeline, which exists. It
  does not depend on anything here. **Two things here are shaped so F3 plugs in with no rework**:
  - brief 29's field layer takes *a scalar or a vector per mesh node*, not an "electric field" type,
    so temperature displays with no viewer change;
  - brief 22's terminal model (named conductors on a mesh) is the same shape as a thermal
    boundary list.
- **F4**: an editable 3D view, construction history and the geometry worker. The viewer here is read
  only. Brief 28's scene model is kept free of anything that would stop a later editor from driving it
  (a generation number on every rebuild, per §8.4).
- **Container and remote locations** (§7.3, §7.5). They matter for problems a laptop cannot hold.
  This series is about showing what a laptop *can* hold. Brief 21's memory check names them as the
  future remedy only once they exist; until then it does not mention them.
- **The difference field** between two solvers (§8.5). It needs one field resampled onto the other's
  mesh. That is a separate problem with its own accuracy question.
- **The series 1 review leftovers** in §1i.
- **brief 9 gate 8** (openEMS phase against F0, 8.6° at 20 GHz). It is still open, and it is openEMS's
  question, not Palace's.

---

## 5. Where the code goes

```
src/Design/AppDataPaths.cs            MOVED path logic from src/Ui/AppDataRoot.cs (24)
src/Design/Em3d/
    PalaceLogProgress.cs              log line -> progress event (21)
    PalaceProblemWriters (partial)    electrostatic/magnetostatic/eigenmode/wave-port sections (22, 23)
    Install/
        SolverRecipes.cs + recipes/*.json   recipes as data, embedded (24)
        SolverInstaller.cs            download, verify, run, record, publish (24)
        InstallRecord.cs              what was put where (24, 25)
        SolverUninstaller.cs          (25)
        KnownFailures.cs              pattern -> remedy, each with its source (24)
    Wsl/
        WslDistributions.cs           list, UTF-16 parse, discovery inside (26)
        WslStaging.cs                 copy in, run, copy out, kill by process group (26)
src/Engine/Em3d/
    PhysicalCores.cs                  per-OS physical core count (21)
    Em3dStaticResult.cs               C/L matrices as cubes (22)
src/Render/Scene3D/                   NEW: scene model, camera, picking math, colour maps,
                                      msh reader, VTU reader; no GPU (28, 29)
src/Ui/Viewer3D/                      NEW: the GPU device and the composition surface only (28)
src/Cli/Solver.cs                     the `solver` verb (24, 25)
tools/Viewer3dSpike/                  NEW, not in the solution, not shipped (27)
examples/3D EM/                       NEW (30)
```

---

## 6. The series' own gate

**`R-em3d20-1`** Every brief in §2 exists and every link resolves.

**`R-em3d20-2` The newcomer walk-through (owner check).** The owner starts from a macOS account where
nothing from series 1 was ever installed. With **no terminal at any step**, they:
1. open Tools ▸ Examples ▸ *3D EM*;
2. press Simulate and get a refusal that offers *Install Palace…*;
3. install;
4. press Simulate again and watch named stages;
5. open the 3D view and see the model, the mesh and an animated |E|;
6. read the S-parameters in the Data Display.

The owner records the elapsed time of each step. The same walk-through on Windows goes through the
Linux subsystem (brief 26).

**`R-em3d20-3`** Nothing in this series has posted, filed or published anything outside this
repository (§1a).

---

## 7. Scope for the series

- **Palace first, in every brief.** openEMS gains whatever falls out at no extra cost: its install in
  brief 24, its grid in brief 28, and its fields in brief 29's second half. A feature that would need
  openEMS work of its own refuses on openEMS with a sentence naming Palace.
- **No change to any planar answer, any planar result path, or any shipped example's numbers.**
- **No per-primitive edit verbs.** New `.cem` fields are authored by writing the `.cem`, and the
  generated reference pages describe them.
- **No timing tests.** Counters only.
- **On completion of each brief, findings go in the relevant `RESOLVED.md`**, never `CLAUDE.md`. Doc
  sources are edited, and DocGen is not run per brief.
- **Keep EM runs short.** A gate that needs a real solve uses the smallest case that tests the claim.
  Anything over ~5 s is `Category=Benchmark`, and measurement runs (brief 21's presets) happen once,
  logged, and are not part of the routine gate.

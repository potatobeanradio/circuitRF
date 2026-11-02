# Brief 135 — Gmsh: a tested list instead of a pin, and a Linux arm64 install

**Tag:** `R-em3d135-n`
**Status:** Written 2026-10-15. Done 2026-10-15, uncommitted: tested list 4.11.0–4.15.2, Linux arm64 recipe measured (findings in `src/Design/RESOLVED.md`).
**Area:** `src/Design/Em3d/SolverDiscovery.cs` and `Install/` (the version policy, the Linux arm64 recipe),
`src/Design/Em3d/Em3dRunService.cs` and `src/Design/Thermal/ThermalRunService.cs` (where a run reports it),
`src/Cli/Solver.cs` / `ExplainEm3d.cs`, `docs/user/src/reference/em-setup.md`. Findings in `src/Design/RESOLVED.md`.
**Depends on:** nothing. Supersedes the Gmsh half of brief 6's R-em3d6-2b.

---

## 0. Why

A user installing circuitRF on a Linux arm64 machine (GitHub issue #9) found the application and the Palace install
assistant worked straight away. Gmsh was the problem:

- **Upstream publishes no Linux arm64 Gmsh.** There is no archive at gmsh.info and no aarch64 wheel, so circuitRF
  ships no recipe for it there (`src/Design/RESOLVED.md`, brief-em3d-24's install section).
- **circuitRF refused every Gmsh except 4.15.2.** The user had to find and build exactly that version from source.

**The pin was never a finding.** Brief 6 (R-em3d6-2b) made an unvalidated version a refusal, and wrote that rule for
*Palace*: there, a key read differently between versions produces a plausible wrong answer. For Gmsh it allowed a
warning instead, provided F0 showed the `.geo` constructs were unchanged across versions. F0 ran one Gmsh and compared
it with no other, so the strict rule stayed by default (`src/Design/RESOLVED.md`, "Gmsh stays strict").

**What a different Gmsh could actually break, and what already guards it:**

| Risk | How it shows | Guard today |
|---|---|---|
| A face left out of every group becomes a PMC in Palace, silently | wrong S-parameters, no error | **`GmshGeoWriter.CheckEntities`** (`PalaceRun.cs:160`) refuses the run when the script's own entity table does not add up. This guards by construction, whatever the version. |
| The `.geo` stops parsing, or Gmsh crashes | non-zero exit | the exit code is checked, never only whether a `.msh` appeared (F0 Q5) |
| A slightly different mesh | small numerical difference | none needed. Already accepted across OpenCASCADE versions: the two validated 4.15.2 builds link OCC 7.8.1 and 7.9.3, and nothing checks OCC |
| The "Meshing 3D" log lines change | progress readout wrong | cosmetic |

**Owner decision (2026-10-15):** keep a list of the Gmsh versions circuitRF has been tested with. Any other version
**runs, with a warning**, and the warning names the tested versions. Palace and openEMS keep the strict refusal.

### Already done, before this brief (uncommitted at the time of writing)

- `SolverRecipes.ByHand(tool)` (`Install/SolverRecipe.cs`). Where no recipe exists, it is the sentence naming the source
  archive of the newest validated Gmsh and the doc section with the build steps. Used by `DescribeFailure` (the run's
  "Gmsh was not found" refusal), the Settings / `solver list` row (`SolverStatus.Of`), the GUI's *Install Gmsh…* refusal
  and `solver install`'s `solver.install.no-recipe`.
- The doc section **"Building Gmsh from source" `{#gmsh-from-source}`** in `docs/user/src/reference/em-setup.md`. Its
  steps were run verbatim in an `ubuntu:24.04` arm64 container (§3 records the measurement).
- Test: `SolverInstallTests.Gate12_GmshByHand_…`.

---

## 1. `R-em3d135-1` — the version policy

**R-em3d135-1a — per-tool strictness, as data.** `SolverDiscovery.Create` gains a per-tool flag (e.g.
`untestedRuns: true` for Gmsh only). Palace and openEMS are unchanged. Keep the list as the existing
`SolverValidatedVersion` entries, oldest first. `SolverRecipes.For` depends on that order: it picks the newest validated
version that has a recipe. For Gmsh, each entry's comment cites where its evidence lives (§2).

**R-em3d135-1b — readiness carries a warning.** `SolverReadiness` gains `string? Warning`. For a flagged tool,
`Check` on an unvalidated program:

- **proceeds** (`Refusal` null);
- sets `Warning`;
- does **not** skip the rest of `Check`. Gmsh has no capability probe, so this only matters if one is ever added. Write
  the code so that a future probe would run for an untested Gmsh too.

**R-em3d135-1c — the warning's text.** It names: the program and its path, the version it reports, that circuitRF has
not tested that version, **the tested versions**, and the remedy. Example:

> Gmsh at '/usr/bin/gmsh' reports version 4.12.1, which circuitRF has not tested; tested: 4.13.1, 4.15.2. The run goes
> ahead. If meshing fails or a result looks wrong, point circuitRF at a tested Gmsh in Settings ▸ Solvers or with
> CIRCUITRF_GMSH.

**For Gmsh, user-facing text says "tested" where it says "validated" today.** The code identifiers can stay.

**R-em3d135-1d — every reader of `Validated`, audited.** These are the reads (counted 2026-10-15), and each needs a
deliberate answer:

| File | Reads | Question to answer |
|---|---|---|
| `SolverDiscovery.cs` | 8 | `Check`, `DescribeForSettings` ("NOT validated — a 3D run will refuse it" is false for Gmsh now), `DescribeUnvalidated` (its Gmsh `why` sentence becomes the warning) |
| `Install/SolverInstaller.cs` | 2 | `WouldHelp`. Should *Install Gmsh…* still be offered when an untested Gmsh is found? **Yes**: installing a tested one changes what runs. Keep the offer. |
| `Install/SolverStatus.cs` | 2 | capabilities are probed only for validated programs; the `ByHand` sentence is appended when nothing validated was found |
| `Install/SolverRecipe.cs` | 2 | recipe selection follows the validated list (unchanged) |
| `Wsl/SolverDiscovery.Subsystem.cs` | 2 | Palace only; confirm nothing there reaches Gmsh |
| `src/Cli/Solver.cs` | 3 | `solver list`'s `validated: yes/no` line and JSON. Add `tested` wording for Gmsh. **Do not rename the JSON key** (it is a contract). |
| `src/Cli/ExplainEm3d.cs` | 1 | `explain`'s solvers section must show the warning, not a refusal |

**R-em3d135-1e — where the warning surfaces.** It goes in a run's **Warnings**, never its Notes:

- `Em3dRunService.Run` (`log.Warnings`, beside the existing "Solver: … mesher: Gmsh …" note);
- `ThermalRunService.Run` (its `warnings` list, at the Gmsh readiness check near line 150);
- `circuitrf explain` / `check` on a 3D setup;
- the Settings row and `solver list`, through `DescribeForSettings`.

One sentence, produced by one function, so the four cannot disagree.

**R-em3d135-1f — tests** (targeted classes only; see §5):

- An untested Gmsh (a stub program printing `4.12.1`) **proceeds**, with a warning that names the tested list.
- An untested Palace and an untested openEMS are still **refused**. The existing tests stay green unchanged.
- `DescribeForSettings` for an untested Gmsh no longer says the run will refuse it.
- **The real guard still fires:** a stub Gmsh at an untested version whose entity table does not add up is
  **refused** by `CheckEntities`. If such a test already exists, cite it rather than adding a second.

## 2. `R-em3d135-2` — the evidence: which versions go on the list

A version goes on the tested list **only on a measured pass**, recorded in a table that the `SolverDiscovery` entry's
comment cites. Put the table in `testdata/em3d/gmsh-versions/README.md`, beside the scripts it ran. Copy the scripts in
small; they are the corpus.

**R-em3d135-2a — the corpus is circuitRF's own scripts**, not F0's hand-written ones:

1. `testdata/em3d/palace-goldens/*/model.geo`: 4 scripts (driven microstrip, via, electrostatic, magnetostatic).
2. `testdata/thermal/**/mesh/*.geo`: the thermal writer's.
3. `testdata/em3d/f4b/q4/*.geo`: the kernel-import (B-rep/STEP) path.
4. **Every example `.c3d` setup's Palace script.** Write a scratch console harness (in the scratchpad, never the repo)
   that references `src/Design/CircuitRF.Design.csproj` and, for each `.c3d` under `examples/`, does exactly what a run
   does:
   - `C3dSetups.Read(doc)`, then `C3dSetups.ForRun(setup, path)`, skipping thermal, planar and openEMS-only setups;
   - `C3dProblemAssembly.Assemble(setup, doc, path, cws, new C3dElaborator(), false)`;
   - `GmshGeoWriter.Write(problem, PalaceSettings.Resolve(setup.Palace))`;
   - write `model.geo`, `groups.json` and each `KernelFiles` entry (`WriteInto`).

   The `.cws` is the nearest ancestor of the `.c3d`. Namespace trap (hit while writing this brief): `Em3dSolver` and
   `PalaceSettings` are **not** in `CircuitRF.Engine.Em3d` / `CircuitRF.Design.Em3d`. Grep for their declarations
   first.

**R-em3d135-2b — the candidate versions.** At least:

- **4.15.2**, the reference;
- **4.15.0 / 4.15.1**, patch neighbours (4.15.1 is also the newest Spack packages offer at the commit our Palace recipe
  pins);
- **4.14.x** and **4.13.1**;
- **what the distributions package**, because a distribution package is the easiest install on Linux arm64 once
  untested versions run. Measured 2026-10-15 (arm64 containers, `apt-cache policy gmsh`):
  - Ubuntu 24.04: **4.12.1**;
  - Debian 13: **4.13.1**;
  - Debian 12: **4.8.4**. Test it too: if it fails, the warning is what a Debian 12 user would see, and the table
    should say so.
- one build linking **OCCT 8.x** if one can be had cheaply, since issue #9's reporter built against OCCT 8.0.1.

**Source of each Gmsh:**

- On macOS arm64, upstream's SDK archives at `https://gmsh.info/bin/MacOSX/`: unpack into the scratchpad and run in
  place, about 1 minute each.
- Distribution builds: in `--platform linux/arm64` Docker containers. Docker Desktop is installed on the owner's machine
  and runs arm64 natively. Start it with `open -a Docker`.
- Record the OpenCASCADE each build links: `gmsh -info`.

**R-em3d135-2c — the pass rule, per version, per script:**

1. Gmsh exits 0.
2. **`entities.txt` is byte-identical to 4.15.2's** for the same script. This is the check that matters: it is what
   `CheckEntities` reads, and it is where a changed `BooleanFragments` or bounding-box selection would show.
3. `GmshGeoWriter.CheckEntities` passes on it.
4. The element count, recorded but **not gated**: meshes legitimately differ between versions. Flag a difference over
   ~10 % in the table for the owner to look at.

A version passes when every script passes. A version that fails goes in the table with the failing script, the
`entities.txt` diff and the first error line, and stays off the list. Do **not** loosen the rule to admit it.

**R-em3d135-2d — keep it short** (owner rule: EM runs short and specific). No Palace solve is needed: the entity table
is the version-sensitive part, and a solve would only re-measure mesh-convergence noise. If Palace happens to be
installed, one `static-es` solve per passing version is a cheap sanity check; it is optional.

**R-em3d135-2e — no new test that downloads.** The routine gate gets no network test. Optional: a test that **replays**
the committed `entities.txt` files through `CheckEntities`, so the evidence cannot rot silently. It must assert
something real.

## 3. `R-em3d135-3` — a Linux arm64 Gmsh recipe

**Measured before this brief.** In a `--platform linux/arm64` `ubuntu:24.04` container on the owner's M4 (Docker
Desktop, 8 GB), the steps now in the doc section built Gmsh 4.15.2 against Ubuntu's own OCCT:

- **Time:** packages 1 min 24 s; download plus `make -j4` plus install **2 min 22 s**.
- **Size:** `~/.local` is 33 MB.
- **What it reports:** `gmsh --version` prints `4.15.2-git`, which is accepted. CMake found OpenCASCADE **7.6.3** and
  OpenCASCADE-CAF.
- **Result:** all four `testdata/em3d/palace-goldens/*/model.geo` meshed with exit 0, and their **`entities.txt` were
  byte-identical** to the macOS Homebrew build's (Gmsh 4.15.2, OCC 7.9.3). Two OCCT versions three minor releases
  apart gave the same entity tables.

The recipe follows that build. **Recommended route: a CMake source build against the distribution's OpenCASCADE**,
`gmsh-4.15.2-linux-arm64.json`:

- **Prerequisites:**
  - `program` entries for `cmake`, `g++` and `make`;
  - a `file` entry for OCCT's headers (`/usr/include/opencascade/Standard_Version.hxx`; the schema already has `file`);
  - each with per-distribution `remedy` text, as `palace-0.18.1-linux-arm64.json` has. Ubuntu/Debian:
    `sudo apt-get install build-essential cmake libocct-foundation-dev libocct-modeling-data-dev
    libocct-modeling-algorithms-dev libocct-data-exchange-dev libocct-ocaf-dev libocct-visualization-dev`. Fedora:
    find the `opencascade-devel` equivalent, and **measure it, don't guess**.
- **Source:** `https://gmsh.info/src/gmsh-4.15.2-source.tgz`, with **`sha256` committed**:
  `be3f66f225d27ba9fa014f07e83169285da8a051b0e8ab7103d88066b39bdd3e` (measured 2026-10-15). The binary recipes have
  `sha256: null` because gmsh.info publishes no checksum; a source build we pin can do better.
- **Steps:**
  1. download;
  2. extract;
  3. `run` cmake with `-DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=${home} -DENABLE_FLTK=0 -DENABLE_OCC=1`;
  4. `run` `make -j${jobs}` with `progress: lines`;
  5. `run` `make install`.
- **`program`:** `${home}/bin/gmsh`.
- **`layout`:** decide from measurement. The default build links `libgmsh` statically into the executable, and OCCT
  comes from system paths, so the home is likely `relocatable`. Check with `ldd` before claiming it.
- **`measured`:** minutes and GB from three clean runs, as Palace's arm64 recipe was measured (F0 README §Install rows
  13-15). Add the rows to that table.

**Rejected route: Spack.** It would reuse the Palace recipe's machinery and needs no OCCT from the distribution. But the
spack-packages commit our Palace recipe pins (`0ec478de`) offers **gmsh ≤ 4.15.1**, not 4.15.2, and `opencascade` is
off by default (`+opencascade` needed). It would need a second pin or a local package override, which is the kind of
package-repository patching brief 24 avoided. Revisit only if the CMake route fails on a supported distribution.

**R-em3d135-3a — after the recipe lands:**

- `ByHand` still returns its sentence; it is simply no longer reached on Linux arm64, because a recipe exists there.
  Keep it: it is the answer for any future platform without a recipe.
- Update the doc section to lead with *Install Gmsh…*, keeping the manual steps underneath.
- Update the `gmsh-4.15.2-linux-x64.json` comment, which says "Linux arm64 has no recipe".

**Gate:** three clean `circuitrf solver install gmsh --yes` runs in an Ubuntu 24.04 arm64 container, then the §2
corpus meshed by the installed Gmsh with the §2c pass rule; one run on Debian 12 arm64. Linux x64 and Windows are the
owner's, as in the F0 README.

## 4. `R-em3d135-4` — docs

- `em-setup.md` §"Installing the 3D solvers by hand": the opening paragraph says circuitRF runs **only validated
  versions**. Make it true per tool: Palace and openEMS validated-only, Gmsh "tested versions; any other runs with a
  warning". The table's Gmsh row lists the tested versions.
- Grep `docs/user/src/reference/{settings,em-3d,cli,em-solvers}.md` for "validated" near Gmsh and fix each.
- `cli.md`: `solver list`'s Gmsh line wording, if it is documented.
- No DocGen run (owner regenerates at the end of a series).

## 5. Gate

- **Targeted test classes only.** Never the full suite, never all of `Ui.Tests`:
  `dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~SolverDiscoveryTests|FullyQualifiedName~SolverInstallTests"`
  plus whichever class holds the thermal / EM readiness tests you touch, and `Firewall.Tests`. **Run once; read the
  TRX.**
- `src/Design/RESOLVED.md`: the policy change and why (cite §0's table, don't restate it), the version table's location,
  and anything the recipe measurement found.
- No vendor names. No personal paths in testdata (the harness output goes to the scratchpad; only the copied scripts and
  their `entities.txt` are committed).
- Do not commit. The owner commits.

## 6. Reply on issue #9 (owner posts)

A draft is in the conversation that produced this brief. After this brief lands, follow it up: Linux arm64 now has
*Install Gmsh…*, and a distribution Gmsh runs (tested ones without a warning).

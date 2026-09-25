# Brief 24 — the install assistant

**Series:** [3D EM, second series](brief-em3d-20-overview.md) · **Tag:** `R-em3d24-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §7.1, §7.2 (all but uninstall), §7.3 (native
location); F0 findings Q12 and `testdata/em3d/f0/README.md` §Install
**Area:** `src/Design/AppDataPaths.cs` (moved), `src/Design/Em3d/Install/` (new),
`src/Design/Em3d/SolverDiscovery.cs` (two routes), `src/Cli/Solver.cs` (new verb),
`src/Ui/Views/Dialogs/Em3dSolverSettingsView.axaml` (the rows), a consent dialog, the refusal's action
**Depends on:** — · **Blocks:** 25, 26, 30
**Owner decision D1:** the verb is `solver` (default).

---

## 0. What this brief delivers

*Install Palace…* on macOS and Linux, and *Install Gmsh…* and *Install openEMS…* on all three OSes. It
is offered from the refusal a 3D run gives when a solver is missing, from the solver's Settings row,
and headlessly:

```
circuitrf solver list
circuitrf solver install palace --yes
```

This does not change the design's position: **circuitRF distributes nothing.** The bytes come from
each program's own upstream, under that upstream's licence, onto the user's machine at the user's
request (§7.2).

Windows Palace is brief 26. Uninstall is brief 25.

---

## 1. Things settled before the requirements

- **The overview's §1c:** the per-user root moves below the firewall. `src/Design/AppDataPaths.cs`
  takes `AppDataRoot`'s path logic and its redirect (the DocGen lever) unchanged, and
  `src/Ui/AppDataRoot.cs` forwards to it. Gate 1 proves both resolve to the same directory, with and
  without the redirect.
- **The install home** is `<AppDataRoot>/solvers/<tool>/<version>/`. Each one is **self-contained**:
  - Palace's gets its **own Spack clone**, pinned to a tag, and its own install tree inside that home.
  - It never uses, reads or modifies a Spack the user already has, whether `$SPACK_ROOT`, `~/spack` or
    `~/opt/spack`. The user's Spack install is theirs.

  One tree per validated version costs a rebuild per version. That rebuild happens anyway (Palace's
  dependencies move with it), and it is what makes brief 25's uninstall a directory removal. F0 found
  that *sharing* a Spack tree makes "what is unused" a hard question: `gmake` was still live inside a
  dead tree.
- **What F0 already knows (findings Q12)** is the seed of the known-failures table:
  1. `CERTIFICATE_VERIFY_FAILED`: pick a Python with certificates for `SPACK_PYTHON`.
  2. `No such variant 'gkrand'`: the builtin package repository is too old for Palace's recipe.
  3. SVE `Illegal instruction` on Apple M4: cap the Spack target at `m3`.
  4. Also: the concretizer silently dropping `+slepc`, `+gslib` and `+sundials` unless the spec states
     them.

  **The recipe pre-empts all four** before the build starts. The table still carries them, because a
  user's own Palace install can meet them too.

---

## 2. `R-em3d24-1` — recipes are data

**`R-em3d24-1a`** Each validated version has a recipe per `(tool, version, platform, architecture)`,
in `src/Design/Em3d/Install/recipes/*.json` (embedded resources). A recipe holds:
- the source URLs and the upstream checksum, or `"checksum": null` with a reason ("upstream publishes
  none");
- prerequisite checks: a program on PATH, a minimum version, a Python with working TLS;
- the steps, each one a command and its arguments, with no shell string interpolation;
- the Spack spec in full, every variant stated (point 4 above);
- the measured time and disk, each with its source ("F0 macOS M4: 52 min, 1.9 GB");
- the known failures that apply to it.

Adopting a new validated version is a recipe edit and a validation run (§7.2).

**`R-em3d24-1b` A JSON schema for the recipe file**, committed and validated in a test, so a recipe
typo fails the build rather than a user's install.

**`R-em3d24-1c` No step runs a shell.** Commands are started with `ProcessStartInfo.ArgumentList`. The
Spack steps call `<home>/spack/bin/spack` with arguments. Environment variables (`SPACK_PYTHON`,
`SPACK_USER_CONFIG_PATH`, `SPACK_DISABLE_LOCAL_CONFIG=1`) come from the recipe. `SPACK_USER_CONFIG_PATH`
points inside the install home, so the user's own `~/.spack` configuration can neither affect nor be
affected by the install.

---

## 3. `R-em3d24-2` — consent, then work

**`R-em3d24-2a` Nothing is downloaded before the user says yes.** The consent dialog, and the CLI's
refusal without `--yes`, states:
- the program and version;
- every upstream URL it will fetch from;
- where it installs;
- the measured time and disk, with the machine they were measured on;
- the ParMETIS sentence for Palace (§2, §7.1);
- that it runs in the background and can be cancelled.

**`R-em3d24-2b` Prerequisites are checked first**, and a missing one is a refusal before any download.
It names **the one command the user runs** (for example `xcode-select --install`, or
`sudo apt install build-essential gfortran python3 …` for the distribution detected). circuitRF never
runs `sudo` and never asks for a password.

**`R-em3d24-2c` Background, with progress and cancellation**, on `RunControl` like an EM run:
- **Spack** prints one `[+] <prefix>` line per installed package. Concretise first (`spack spec`), so
  the total is known, and show *package k of N* with the package name.
- **Downloads** show bytes against length. Reuse `src/Ui/Updates`' downloader behaviour by moving
  what the CLI needs below the firewall, not by copying it.

**`R-em3d24-2d` Cancelling leaves nothing that discovery would find.** Build into
`<home>.partial/`. **Publish by renaming** to `<home>/` only after the checks in 2e pass. A leftover
`.partial` is removed at the next install attempt, and it is never a discovery location.

**`R-em3d24-2e` An install is not a success until discovery agrees.** Afterwards, run §7.1's
discovery, the version check and the capability probe (brief 23's rows included) against the
installed program. Only if all pass is the home published and the Settings row updated to
*installed by circuitRF, <path>, <version>*.

**`R-em3d24-2f` The install record** (`install.json` in the home) says what was installed, from which
URLs, the checksums verified or not verified, when, the recipe id, and the measured size. Brief 25
removes by it.

---

## 4. `R-em3d24-3` — when it fails

**`R-em3d24-3a`** The report says:
- which step failed;
- the upstream tool's own lines **verbatim** (the last lines containing `Error`, or the last 20 lines);
- the full log's path;
- nothing clever: no retry, no second recipe, no patch (§7.2).

**`R-em3d24-3b` Known failures** (`KnownFailures`) are patterns over the verbatim text. Each carries its
remedy and **its source**: which install met it, when, and on what machine. A match adds *"circuitRF's
own installs have met this: <remedy>"*. No match says *"not a failure circuitRF has seen"*, and nothing
more.

**`R-em3d24-3c`** One transient is allowed a sentence: a checksum failure from a dependency mirror.
It says "retrying often clears this", and the user retries. circuitRF does not retry automatically.

---

## 5. `R-em3d24-4` — Gmsh and openEMS on every platform

**`R-em3d24-4a` Gmsh:** upstream's binary archives for Windows, macOS and Linux, at the validated
version. F0 installed Gmsh from Homebrew, so the archive route is new: **try it once per OS** (the
owner does Windows, per brief 26's list) and confirm the archive's `gmsh --version` matches the
validated entry before writing its recipe.

**`R-em3d24-4b` openEMS:**
- **Windows:** upstream's published 64-bit archive.
- **macOS and Linux:** upstream's own build script, as F0 ran it (`--disable-GUI`), with its
  dependencies named as a prerequisite command. F0's openEMS is a **release candidate**, which F0 §6
  says should not stay on a validated list. If 0.37.0 final has shipped, re-run `testdata/em3d/f0`'s
  openEMS cases against it first and update the list.

---

## 6. `R-em3d24-5` — two new discovery routes

**`R-em3d24-5a` Installed by circuitRF** (`SolverHowFound.Installed`) is searched **before** `PATH`
and after Settings and the environment variable. The Settings row shows *installed by circuitRF* and
offers brief 25's *Uninstall*. Any other route shows *found at …* and offers no uninstall (§7.2).

**`R-em3d24-5b` Conda environments** (`SolverHowFound.Conda`, overview §1a). Searched after Spack:
- `$CONDA_PREFIX/bin`;
- `envs/*/bin` under `~/miniforge3`, `~/mambaforge`, `~/miniconda3`, `~/anaconda3` and `/opt/conda`,
  the directories the installers default to.

It is read-only and never activates an environment. When the community Palace package lands, a
GUI-launched app finds it with no Settings entry. The validated-version check still applies, so an
unvalidated build is refused by version, as any other would be.

---

## 7. `R-em3d24-6` — seeding Linux from this Mac (overview §1b)

**`R-em3d24-6a`** Run the Palace recipe **three times from clean** in each of three arm64 Linux
containers under the Docker Desktop already on this machine: Ubuntu 24.04, Debian 12 and Fedora
(current). Also run Gmsh and openEMS once each.
- Log every attempt in `testdata/em3d/f0/README.md` §Install, in F0's format: date, image digest, wall
  clock, disk, every failure verbatim and what fixed it.
- Each new failure becomes a `KnownFailures` entry with that log line as its source.

A container is the right tool here: its clean state is exactly the "fresh distribution" the design
warns about (§7.4).

**`R-em3d24-6b`** Run them **one at a time, in the background**, never alongside a test run. A Palace
Spack build is 35+ minutes of every core. x64 under emulation is optional: if one x64 run takes over
four hours, stop, record that, and leave x64 to the owner.

**`R-em3d24-6c`** Nothing from these runs is committed except the log text and the recipe or
known-failure changes it justifies. No image, no build tree.

---

## 8. `R-em3d24-7` — the `solver` verb

- `solver list` shows each tool: found or not, where, how (the route), version, whether it is
  validated, and its capabilities.
- `solver install <palace|gmsh|openems> [--version v] [--yes]`: without `--yes` it prints the consent
  text and exits 1.

It holds **no logic of its own**. It calls the functions the Settings rows call, and the gate carries
a comment-stripped source scan for that, on `Authoring.cs`' terms. Exit codes:
- 0 on success;
- 1 on refusal or failure, with the report on stderr;
- 130 on cancellation.

---

## 9. Gate

`tests/Ui.Tests/Em3d/SolverInstallTests.cs`. No test downloads from the internet, and no test builds
Palace.

1. **The root moved without moving.** `AppDataPaths` and `AppDataRoot` give the same directory, with
   and without the redirect.
2. **Recipe schema.** Every embedded recipe validates. A planted typo fails.
3. **End to end on a fake recipe.** A recipe whose "upstream" is a local `file://` archive of a stub
   program that prints the validated version string goes through: consent, download, checksum,
   extract, `.partial`, discovery, publish, record. Discovery then reports `Installed`.
4. **Checksum mismatch.** The same with a wrong checksum refuses. No home is published, and discovery
   finds nothing.
5. **Cancellation.** Cancel mid-download and mid-step: nothing is published, the `.partial` goes at
   the next attempt, and discovery finds nothing (counter).
6. **Discovery must agree.** A stub that prints an **unvalidated** version fails the install at 2e and
   publishes nothing.
7. **Known failures.** F0's three verbatim messages each match with their remedy and source. An
   invented message matches nothing.
8. **The user's Spack is untouched.** With a fake `~/opt/spack` present, the Palace recipe's resolved
   steps and environment reference no path outside the install home (scan the resolved commands).
9. **Conda route.** A fake `~/miniforge3/envs/x/bin/palace` stub is found as `Conda`, and an
   unvalidated one is refused by version.
10. **Consent headless.** `solver install palace` without `--yes` exits 1, prints the consent text and
    creates nothing.
11. **Source scan.** `src/Cli/Solver.cs` calls the install functions and holds no install logic.

**Not a test, but required for completion:** the six Linux container logs (§7), and one real
`solver install palace --yes` on this Mac from a machine state with no Palace in the circuitRF home,
logged with its wall clock.

## 10. Owner check

- The consent dialog's text and the Settings rows (*installed by circuitRF* vs *found at*).
- One *Install Palace…* from the GUI, watched to the end.

## 11. Scope

- No uninstall (25) and no Windows Palace (26).
- No container or remote location.
- No automatic update of an installed solver. A newly validated version is a new install, and brief 25
  offers to remove the superseded one.

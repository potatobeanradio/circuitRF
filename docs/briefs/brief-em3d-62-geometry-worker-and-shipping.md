# Brief 62 — the geometry worker, built and shipped

**Series:** [3D EM, fourth series](brief-em3d-60-overview.md) · **Tag:** `R-em3d62-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.2 Route B; overview §1b, §1c, §1d
**Area:** `tools/geometry-worker/` (new), `src/Ui/CircuitRF.Ui.csproj` (build and publish targets),
`packaging/{windows,macos,linux}/`, `src/Ui/bundleFor*MacOS.sh`, `tools/CliSmoke/`,
`THIRD-PARTY-NOTICES.md`, `licenses/`, `src/Ui/Views/Dialogs/AboutWindow.axaml(.cs)`,
`tests/Firewall.Tests/`, `tests/Ui.Tests/PackagingScriptTests.cs`, `BUILDING.md`
**Depends on:** 61 (the recipe, the RID list D2) · **Blocks:** 63

---

## 0. What this brief delivers

A program, `geometry-worker`, that links OpenCASCADE and answers on stdin/stdout — and **every installer
carrying it**, with OCCT's shared libraries beside it and the licence obligations met. When it is done:

- `tools/geometry-worker/` builds the worker from **one C++17 source file**, MIT, against a pinned OCCT that
  a recipe in `tools/geometry-worker/occt/` builds from upstream source;
- a developer's `dotnet build` **copies** a built worker into `bin/` when one exists and **warns** when it
  does not — it never starts an OCCT build (overview §1d, case 1);
- all three packaging scripts ship the worker for every RID in **D2**, smoke-test it, and **fail** when a
  shipping RID lacks it;
- `THIRD-PARTY-NOTICES.md`, `licenses/`, the About box and each release's published assets carry what the
  LGPL-2.1 and the Open CASCADE Exception require.

**The worker speaks a skeleton protocol only** — `hello`, `selftest`, `box`, `quit` (§2). The real protocol,
the managed client, discovery and the capability are **brief 63's**. This brief proves the program exists,
runs and ships everywhere.

---

## 1. `R-em3d62-1` — the tree

```
tools/geometry-worker/
    README.md               the program, the skeleton protocol, how to build it, the licence position
    geometry_worker.cpp     the worker — ONE source file (senior-worker's shape)
    CMakeLists.txt          finds the pinned OCCT; RPATH / install-name rules (§4)
    build.sh / build.cmd    builds OCCT (via occt/) if the cache lacks it, then the worker   [--strict] [--arch]
    ensure-built.sh / .cmd  what `dotnet build` runs: copy-if-built, warn otherwise           (§3)
    occt/
        RECIPE.md           version, source URL, SHA-256, CMake options — copied from brief 61's findings
        recipe.env          the same values, machine-readable, read by build.sh and build.cmd
```

**`R-em3d62-1a` The source is MIT** and says so in its header, with the prominent notice the exception asks
for: *this program uses facilities provided by Open CASCADE Technology*. It includes OCCT headers and links
OCCT's shared libraries **dynamically**; nothing of OCCT's is copied into `tools/`.

**`R-em3d62-1b` No OCCT in the repository.** Source archive, build tree and libraries live in the per-user
build cache (§3) and in publish trees, never under version control — **not one OCCT file, ever, in the
working tree or its history** (owner, 2026-09-27; brief 61's spike build alone was ~5 GB). **Anyone
compiling circuitRF from source obtains OCCT's source themselves**: the recipe fetches the upstream archive
into the cache, and nothing in the repository substitutes for it. This holds in particular for the eight
`TKGeomBase` files whose header contradicts the licence (brief 61 Q11, upstream issue
[OCCT#1564](https://github.com/Open-Cascade-SAS/OCCT/issues/1564)). `.gitignore` already carries the
backstop — OCCT archives and source trees, `tools/geometry-worker/{build,occt/build,occt/install}/`, and
`libTK*` / `TK*.dll` anywhere.

---

## 2. `R-em3d62-2` — the skeleton protocol

Line-delimited JSON on stdin/stdout, one request, one response; diagnostics on stderr. The framing is
brief 63's to extend (it may add a binary payload for tessellations); this brief fixes only:

| Request | Response |
|---|---|
| `{"op":"hello","protocol":1}` | `{"ok":true,"worker":"<VERSION file>","occt":"8.0.1","protocol":1,"modules":[…]}` |
| `{"op":"box","size_um":[x,y,z]}` | `{"ok":true,"solids":1,"faces":6,"volume_um3":…}` |
| `{"op":"selftest"}` | box − cylinder, a 25 µm fillet on the rim, a STEP write and read back in memory: `{"ok":true,"faces":…,"valid":true,"step_roundtrip":true}` |
| `{"op":"quit"}` | `{"ok":true}`, then exit 0 |
| anything else | `{"ok":false,"error":"<sentence>"}`, and the worker keeps running |

**`R-em3d62-2a`** `hello` reports the OCCT version **the worker was linked against**, read at run time
(`OCC_VERSION_COMPLETE` compiled in, compared with the loaded `TKernel`'s own — a mismatch is an error
response, never a silent run against the wrong library).

**`R-em3d62-2b`** `--version` on the command line prints the same two versions and exits 0, so a human and
`CliSmoke` can ask without speaking the protocol.

**`R-em3d62-2c`** Exceptions are caught at the request boundary (`Standard_Failure`, `std::exception`) and
become an error response. Signals are turned into exceptions the way brief 61's Q9 found works
(`OSD::SetSignal`), **only if** Q9 found the process usable afterwards; otherwise the worker exits non-zero
and brief 63 restarts it. The README states which, citing Q9.

---

## 3. `R-em3d62-3` — building it: the recipe, the cache, and what `dotnet build` does

**`R-em3d62-3a` The recipe is data.** `occt/recipe.env` holds `OCCT_VERSION`, `OCCT_URL`, `OCCT_SHA256` and
`OCCT_CMAKE_OPTIONS` exactly as brief 61's findings note wrote them. Adopting a new OCCT is a recipe edit, a
rebuild and the spike's `selftest` — the same "recipes are data" rule the solver install assistant follows
(em-3d.md §7.2).

**`R-em3d62-3b` The download is verified** against `OCCT_SHA256` before it is unpacked. A mismatch is a
failure naming both hashes; nothing is built from an unverified archive.

**`R-em3d62-3c` The cache is per user and outside the repository**: `~/.circuitRF-build/occt/<version>/<rid>/`
(`%LOCALAPPDATA%\circuitRF-build\occt\<version>\<rid>\` on Windows) — a path with no space in it, because
autotools and some CMake paths refuse one (the finding em-3d.md §7.2 records for the solver installs). A
built RID counts only once its `install.json` exists, written last — the solver installer's rule, so an
interrupted build is never mistaken for a finished one.

**`R-em3d62-3d` `dotnet build` never builds OCCT.** `CircuitRF.Ui.csproj` gains a step beside the
senior-worker and osdi-worker steps, in **both** a Windows-conditioned and a non-Windows form
(`PackagingScriptTests.EveryHelper_IsBuiltOnWindowsToo` already demands the pair for every helper):

- OCCT for the target RID **in the cache**: compile the worker if its source is newer than the binary
  (seconds), copy worker + libraries into `$(OutDir)geometry-kernel/`;
- **not in the cache**: print one warning — *the geometry kernel is not built; booleans, fillets and STEP
  are disabled; run `tools/geometry-worker/build.sh` once (about <Q3's number>)* — and succeed;
- **never fail the build** (senior-worker's `ensure-built.sh`: *THIS SCRIPT MUST NEVER FAIL A BUILD*),
  except under `--strict`, which the packaging scripts pass;
- skippable with `-p:CrfSkipGeometryWorker=true`.

**`R-em3d62-3f` circuitRF builds, tests and runs with no OCCT anywhere** (owner, 2026-09-27). A machine
that has never had OCCT's source, its archive, the cache, or network access to any of them runs
`dotnet build`, `dotnet test` and `dotnet run` to success: the `dotnet build` step **reads the cache and
nothing else** — it never downloads, unpacks or configures OCCT, and it needs no C++ toolchain when the
cache is empty. The **only** thing that fetches OCCT is `tools/geometry-worker/build.sh`/`build.cmd`, run
deliberately. Every test that needs the worker skips **with a reason** (overview §1d); nothing fails for
its absence. A `PackagingScriptTests` fact reads the `ensure-built` scripts and the `.csproj` step and
fails if either names a download tool (`curl`, `wget`, `Invoke-WebRequest`, `Start-BitsTransfer`) or the
recipe's `OCCT_URL`. A packaging script with no kernel stops by default and builds a kernel-less installer
with `CRF_ALLOW_NO_KERNEL=1` (§5) — the absent path, not a broken build.

**`R-em3d62-3e` The publish target** (`CrfPublishHelperPrograms`) publishes the whole `geometry-kernel/`
directory, and a `PackagingScriptTests` fact reads the file list from the build script, the way
`EveryDeviceWorkerProduct_IsListedForPublish` does, so a library added to the closure and forgotten in the
publish list fails a test instead of an install.

---

## 4. `R-em3d62-4` — where it sits in each publish tree

One subdirectory, **`geometry-kernel/`**, beside the assemblies, holding the worker and the exact library
closure brief 61's Q2 listed — nothing more. A subdirectory, not the flat publish root, so OCCT's
several dozen libraries never collide with another native file and so the directory can be replaced whole
(the LGPL's "user can substitute" is then one folder).

| Platform | Worker finds its libraries by | Notes |
|---|---|---|
| macOS | install names `@rpath/…`, worker `LC_RPATH` = `@loader_path` | Under `Contents/MacOS/geometry-kernel/`. **No dot in the directory name** — `bundleForMacOS.sh` refuses any directory under `Contents/MacOS` with one, because codesign reads it as a nested bundle |
| Linux | `RUNPATH` = `$ORIGIN` | Library symlink chains (`libTKernel.so` → `.so.8.0` → `.so.8.0.1`) are flattened to the one name the worker actually loads, so the `.deb` and the tarball do not depend on symlinks surviving |
| Windows | DLLs beside `geometry-worker.exe` (the loader's own directory is searched first) | Per architecture, like the osdi-worker pair; the MSVC runtime the build used is shipped app-local or already present — Q2's closure says which |

**`R-em3d62-4a`** A test builds nothing and asserts, from the scripts, that each platform's worker is
linked with the relative search path above (`otool -l`, `readelf -d`, or the CMake file's own settings —
whichever the test can read without a toolchain). A worker that found its libraries only because the build
cache happened to be on the machine would pass every developer's run and fail every user's.

---

## 5. `R-em3d62-5` — the three packaging scripts

**`R-em3d62-5a` Every script builds the kernel for every RID it ships**, from the cache, with `--strict`:
`build-windows.ps1` for x64, arm64 and x86 (if D2 includes x86); `build-macos.sh` for both architectures
(`CMAKE_OSX_ARCHITECTURES`); `build-linux.sh` for x64 and arm64. The first run on a machine takes Q3's
build time per RID and says so before starting; every later run reuses the cache.

**`R-em3d62-5b` A shipping RID without the kernel fails the run**, at the end, the way a CLI that did not
answer already does (`CRF_ALLOW_UNSMOKED`). The escape hatch is **`CRF_ALLOW_NO_KERNEL=1`**, and the script
prints what the package will lack. A RID that D2 excludes is not a failure: the installer notes for it carry
the one sentence brief 61 wrote.

**`R-em3d62-5c` macOS signing, inside-out.** Every dylib and the worker are signed **individually**, with
the same identity and the hardened runtime, **before** the bundle is sealed — the order `bundleForMacOS.sh`
already uses for `crf-vmhost`, because `--deep` re-signs nested code with the app's entitlements and is not
a substitute. Library validation then passes because every library carries the app's team identity.
Notarisation covers them with the bundle. The same change goes into all three `bundleFor*MacOS.sh` scripts
only if the other two applications load the kernel; otherwise only `bundleForMacOS.sh`, and a comment says
why.

**`R-em3d62-5d` Windows.** `build-windows.ps1` harvests `geometry-kernel\` into `Files.wxs` with the rest of
the publish tree. **Every change to a `.ps1` stays pure ASCII** — `PowerShellScripts_ArePureAscii_BecausePs51ReadsThemAsCp1252` holds it, and the reason is in CLAUDE.md: a BOM-less UTF-8 byte
decodes to a curly quote under PowerShell 5.1 and silently turns code into a string. A native command's
stderr is captured only with `ErrorActionPreference = 'Continue'`
(`PowerShellScripts_CaptureNativeStderr_OnlyWithErrorActionContinue`): CMake and MSVC write warnings there.

**`R-em3d62-5e` The smoke test exercises the kernel.** `tools/CliSmoke` gains a fourth check: find
`geometry-kernel/geometry-worker[.exe]` relative to the executable it was given, run `--version` (the OCCT
version must equal the recipe's), then `selftest` over the protocol, and require `"ok":true,"valid":true`.
For a RID D2 excludes, the check reports *not shipped on this RID* and passes. An architecture the host
cannot execute is handled as `CRF_ALLOW_UNSMOKED` already handles it for the CLI.

---

## 6. `R-em3d62-6` — the licence obligations, met

Written from brief 61's Q11 checklist, not from memory.

**`R-em3d62-6a` `THIRD-PARTY-NOTICES.md`** gains an entry **after CSparse.NET, on its pattern**: component and
version, copyright holder (Q11's exact line), *LGPL-2.1 only, with the Open CASCADE Exception 1.0*, the
licence texts, the upstream source, **used by** `tools/geometry-worker`, and a *What this means if you
redistribute a circuitRF binary* paragraph: the libraries are unmodified and dynamically linked, they sit in
one replaceable folder, the corresponding source is available under the **written offer** of §6c (with
`tools/geometry-worker/occt/RECIPE.md` saying how it was built), and anyone redistributing inherits those
obligations. The document's
opening count — *"Two of these are copyleft"* — becomes three. Any third-party library Q2's closure shows
linked gets its own row under §3 with its licence.

**`R-em3d62-6b` `licenses/OCCT-exception-1.0.txt`** is added, verbatim from OCCT's source tree.
`licenses/LGPL-2.1.txt` already exists and is referenced, not duplicated.

**`R-em3d62-6c` Corresponding source: a written offer, not a published archive** (owner, 2026-09-27).
Eight files in OCCT's archive carry a header that contradicts its licence (brief 61 Q11; upstream asked in
[OCCT#1564](https://github.com/Open-Cascade-SAS/OCCT/issues/1564)), so **circuitRF publishes OCCT's source
nowhere** — not in the repository (`R-em3d62-1b`) and not as a release asset. It meets LGPL-2.1 §6(c)
instead: the OCCT entry in `THIRD-PARTY-NOTICES.md`, which every installer carries, includes this offer,
word for word:

> *circuitRF binaries include Open CASCADE Technology <version>, unmodified, as shared libraries. For at
> least three years after the last circuitRF release that includes this version, the circuitRF project will
> give anyone who received such a binary a complete machine-readable copy of the corresponding source code
> of Open CASCADE Technology <version>, for a charge no more than the cost of providing it. To request it,
> open an issue on the circuitRF project's issue tracker titled "OCCT source request".*

The entry also gives the upstream tag and the recipe's SHA-256, **as a convenience, not as the
fulfilment**. No personal name or address appears in the offer; the issue tracker is the contact.

**Honouring it:** the owner keeps the exact verified archive (the recipe's SHA-256) **outside the
repository** for the offer's lifetime, and `BUILDING.md`'s release checklist gains the step *"the OCCT
archive for this version is retained for the written offer"* — never *"attached"*. **If OCCT is ever
patched** (brief 61 `R-em3d61-1c` forbids it now), the patch falls under the same offer. When OCCT#1564 is
answered the owner may revisit publishing the archive; until then this section stands.

**`R-em3d62-6d` The prominent notice.** The About box names Open CASCADE Technology and its version, reading
the version from the worker's `--version` when the kernel is present, and links to
`THIRD-PARTY-NOTICES.md`. Where the kernel is absent it says so, rather than naming a library that is not
there.

**`R-em3d62-6e` Firewall.** `tests/Firewall.Tests` gains a sibling of `SolverBoundaryTests`, with a
planted-violation twin for each rule (that file's own rule, R-em3d6-6b):
- **no managed assembly references or P/Invokes an OCCT library** (`TK*`, the `occt` names), read from
  `ModuleReferences` exactly as the solver scan reads them;
- **no project file references an OCCT package**;
- **no OCCT source is vendored** under `src/` or `tools/`: no file carries OCCT's licence header;
- the existing notices gate (`NoticeViolations`, which forbids naming Palace, Gmsh or openEMS as shipped)
  is **unchanged**, and a new assertion requires the OCCT entry to be present — shipping it without the
  notice is the violation here.

---

## 7. Gate

**`R-em3d62-7a`** From a clean clone with the kernel cache empty **and no OCCT source, archive or network
access on the machine** (`R-em3d62-3f`), `dotnet build` succeeds and prints the one warning;
`dotnet test tests/Firewall.Tests` passes; the worker-dependent tests report *skipped* with their reason. With the cache filled for the host RID,
`dotnet build` puts `geometry-kernel/` in `bin/` and `geometry-worker --version` answers from there.

**`R-em3d62-7b`** The worker, driven by a test over stdin/stdout, answers `hello`, `box`, `selftest` and
`quit`, and answers an unknown request with `ok:false` **and keeps running**. The test skips **with a reason**
when no built worker is present (overview §1d, case 1).

**`R-em3d62-7c`** `PackagingScriptTests`: the publish list covers the build script's products; each platform's
relative library search path is asserted; every `.ps1` stays ASCII; the worker's build step exists in a
Windows and a non-Windows form.

**`R-em3d62-7d`** `build-macos.sh` with no arguments produces two `.dmg`s whose `CliSmoke` run includes the
kernel check and passes, and `codesign --verify --deep --strict` passes on each `.app`. Run by the agent on
the owner's Mac.

**Targeted tests only** (standing practice): `dotnet test tests/Firewall.Tests` and
`dotnet test tests/Ui.Tests --filter "FullyQualifiedName~PackagingScriptTests|FullyQualifiedName~GeometryWorker"`.

### Owner check list

1. On Windows: `build-windows.ps1` with no arguments; the nine artifacts exist; each smoke line includes
   the kernel; install one perUser `.msi` and open About — Open CASCADE Technology is named with its version.
2. On Linux: `build-linux.sh` with no arguments; install the `.deb` and the tarball on a machine that has
   never built OCCT; `circuitRF --version` and `geometry-kernel/geometry-worker --version` both answer.
3. On macOS: open a notarised `.dmg` on a second Mac that has never seen a build; Gatekeeper opens the app
   with no prompt beyond the usual first-launch one.
4. Confirm the release checklist step that **retains** the OCCT archive (outside the repository) for the
   written offer, and read the offer in the installed `THIRD-PARTY-NOTICES.md`.

---

## 8. Scope

- **The skeleton protocol only** (§2). Discovery, the capability, the cache of shapes and every UI surface
  are brief 63's.
- **No OCCT patch, no static link, no vendored OCCT source.**
- **No managed code touches OCCT** — the firewall (§6e) proves it.
- **`CLAUDE.md` is not edited by whoever builds this brief.** Its Stack section and its packaging paragraph
  will need a line about the kernel; that is the owner's call at commit. Findings — build failures met,
  signing traps, anything surprising about the closure — go in `tools/geometry-worker/README.md` and the
  relevant `RESOLVED.md` (`src/Ui/RESOLVED.md` for the build targets, `packaging/RESOLVED.md` for the
  scripts).
- **Commercial names stay out**, including in build logs quoted into `RESOLVED.md`. No personal paths.
- **No timing tests.** Build time is reported by the scripts and by brief 61, never asserted.

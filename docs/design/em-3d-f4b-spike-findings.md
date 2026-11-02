# circuitRF — 3D EM, F4b kernel spike: findings

**Status:** the agent's part is complete on osx-arm64 (Q1–Q12 answered, `selftest` passes); osx-x64 is
built but **not run** (no Rosetta on the spike's machine); Linux and Windows builds and Q13 are **owed by
the owner** (§ Not done). **One licence finding is with upstream** ([OCCT#1564](https://github.com/Open-Cascade-SAS/OCCT/issues/1564), § Licence). · **Date:** 2026-09-27 ·
**Brief:** [`brief-em3d-61-kernel-spike.md`](../briefs/brief-em3d-61-kernel-spike.md) ·
**Series:** [`brief-em3d-60-overview.md`](../briefs/brief-em3d-60-overview.md) ·
**Design note:** [`em-3d.md`](em-3d.md) §6.2 Route B, §6.4, §6.5 ·
**Data:** [`testdata/em3d/f4b/`](../../testdata/em3d/f4b/README.md) — the harness, every input and output,
every command.

Every number below comes from a file under `testdata/em3d/f4b/`, named beside the claim. "OCCT" means
OpenCASCADE Technology 8.0.1 built by the recipe below; lengths are micrometres.

---

## 0. The answers in one screen

| # | Question | Answer |
|---|---|---|
| Q1 | Which OCCT | **8.0.1**, tag `V8_0_1` (commit `b8f597c6`), archive SHA-256 `0d6913ea…c68a`. Upstream publishes **no** checksum for the source. C++17, CMake ≥ 3.10. Nothing deprecated was needed (`q1/VERSION.md`) |
| Q2 | Smallest module set | **All modules off, seven leaf toolkits named, 25 built.** XCAF **does** pull in `TKService`/`TKV3d` (and `TKVCAF`, `TKHLR`) — but with every optional product off they link **no third-party library**: no FreeType, no OpenGL, no TBB. On macOS `TKService` links AppKit/IOKit (≈1 ms of start-up) (`q2/`) |
| Q3 | Build time, size | macOS: **4 m 54 s** build (`-j10`, M4) per architecture; **56.0 MB** uncompressed (arm64), **20.6 MB** in a `.dmg`, **18.2 MB** in a `.zip`; `strip -x` takes 15 % off raw. Five RIDs **owed** (`q3/SIZES.md`) |
| Q4 | Gmsh reads our B-rep (**D12**) | **Yes — all three format versions**, into Gmsh 4.15.2's OCCT 7.9.3; both named faces recovered by a tight box query, 1 hit each; order-2 mesh completes. STEP works only with `OCCTargetUnit = "UM"` and otherwise **silently finds no faces** (`q4/VERDICT.md`) |
| Q5 | Coincident faces | **Fuzzy value 0 is clean** in all 9 cases (integer µm, DBU-derived, 1-ULP gap and overlap, 30° rotated). **`BOPAlgo_GlueShift` is dangerous**: on genuinely intersecting operands it returns silently wrong results that pass `BRepCheck` (`q5/RESULTS.md`) |
| Q6 | Fillet on a curved edge | **All valid; volumes match Pappus to ≤ 2.2e-16** — pin fillet, bore-rim fillet, both chamfers, and a tangent-chain fillet that propagates from one edge to all 8; 0.2–3 ms each (`q6/RESULTS.md`) |
| Q7 | Names survive | **Yes, deterministic** (in process, serial vs parallel, across processes). §1g holds, with **four corrections** now made to the overview: the edge-repeat suffix collided with the split-face suffix; pieces must be numbered geometrically; fillet names come from the whole propagated contour; seam edges (`side|side`) (`q7/`) |
| Q8 | STEP round trip | **Names, colours, units and geometry survive** in mm, µm and mil, AP214 and AP242 (worst volume error 2.2e-13, in mil). A Gmsh-written STEP reads, valid, needing no fix. **STEP files are not byte-reproducible** (a timestamp) (`q8/RESULTS.md`) |
| Q9 | Failure behaviour | Oversize fillet → `IsDone() == false`; zero-thickness box → `Standard_DomainError`; a self-intersecting input → **"done" with an invalid result**; a segfault kills the process unless `OSD::SetSignal`, which then makes it catchable. A boolean honours a user break in 50 ms; **a fillet never polls it** (`q9/RESULTS.md`) |
| Q10 | openEMS reads our tessellation | **Yes, STL and PLY, identically** (byte-identical PEC dumps, 8,838 PEC edges); the mesh is **watertight**; a missing file still exits 0 (`q10/RESULTS.md`) |
| Q11 | Licence | **No third-party library linked.** Three permissive notices to reproduce (Gay/Lucent `strtod`, MIT `delabella`, BSD `FlexLexer.h`). **Eight files in `TKGeomBase` carry a proprietary header** — taken to be wrong; upstream asked (OCCT#1564); no OCCT file enters the repo (`q11/CHECKLIST.md`) |
| Q12 | Sizing numbers | Worker start **40 ms**; boolean + fillet **1.0 ms**; display mesh **6.0 ms**, 3,388 triangles, 62 KB (`q12/RESULTS.md`) |
| Q13 | Windows x86 (**D2**) | **Not answered — owed by the owner** (`q13/RESULTS.md`) |
| **D2** | Which RIDs ship | **Every RID whose `selftest` passes**; osx-arm64 does, osx-x64 is built and awaits one run; win-x86 waits on Q13 (§ D2) |
| **D12** | Palace hand-off | **The worker's B-rep, `TopTools_FormatVersion_VERSION_1`, without triangles** (§ D12) |

---

## 1. What was done, and by whom (`R-em3d61-1a`)

| Part | Who | RID | State |
|---|---|---|---|
| Build OCCT from the upstream archive with the recipe | agent | osx-arm64 | **done** — `q3/build-osx-arm64.log`, first attempt, zero warnings |
| Build OCCT (cross, `CMAKE_OSX_ARCHITECTURES=x86_64`) | agent | osx-x64 | **done** — `q3/build-osx-x64.log`, first attempt, zero warnings |
| Build the harness; Q4–Q12 | agent | osx-arm64 | **done** |
| `selftest` | agent | osx-arm64 | **done, PASS** (`q3/selftest-osx-arm64.txt`) |
| `selftest` under Rosetta | agent | osx-x64 | **not done** — the x86_64 harness builds, and the machine refuses it (*"bad CPU type in executable"*): Rosetta is not installed, and installing it needs an administrator. Owed: the owner runs it on a machine with Rosetta or an x86-64 Mac |
| Build OCCT; `selftest` | owner | linux-x64, linux-arm64 | **not done** — `q3/build-linux-*.log` are placeholders |
| Build OCCT; `selftest`; Q13 | owner | win-x64, win-arm64, win-x86 | **not done** — `q3/build-win-*.log`, `q13/RESULTS.md` |
| Open the three exported STEP files in a STEP viewer (Q8) | owner | — | **not done** |
| Recipe re-built from a clean directory by copy-and-paste (`R-em3d61-7c`) | agent | osx-arm64 | **done** — § The recipe, *Verified* |

## 2. The machine

Apple M4 (10 cores), 16 GB, macOS 27.0 (26A428); Apple clang 21.0.0, macOS SDK 27.0, CMake 4.4.3, GNU Make
3.81. Gmsh 4.15.2 and openEMS 0.37.0-rc3 as validated by F0. Nothing else ran during the two OCCT builds.

---

## The recipe

For brief 62 to copy into `tools/geometry-worker/occt/`. **No patch to OCCT** was needed or made.

```sh
# OCCT 8.0.1 for circuitRF's geometry worker (docs/design/em-3d-f4b-spike-findings.md, "The recipe").
# 1. Fetch the upstream tag archive and check it. (Linux: sha256sum -c works the same way.)
curl -fsSL -o OCCT-V8_0_1.tar.gz https://github.com/Open-Cascade-SAS/OCCT/archive/refs/tags/V8_0_1.tar.gz
echo "0d6913eae4bcc09a3653ceced6dda1aec11c35a1513d4c06762c9b002092c68a  OCCT-V8_0_1.tar.gz" | shasum -a 256 -c -
tar xzf OCCT-V8_0_1.tar.gz
# 2. Configure: every module off, the seven leaf toolkits named, every optional product off.
#    The last line is per RID -- osx-arm64 shown; see the table below.
cmake -S OCCT-8_0_1 -B build -G "Unix Makefiles" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX="$PWD/install" \
  -DBUILD_LIBRARY_TYPE=Shared \
  -DBUILD_MODULE_FoundationClasses=OFF -DBUILD_MODULE_ModelingData=OFF -DBUILD_MODULE_ModelingAlgorithms=OFF \
  -DBUILD_MODULE_Visualization=OFF -DBUILD_MODULE_ApplicationFramework=OFF -DBUILD_MODULE_DataExchange=OFF \
  -DBUILD_MODULE_Draw=OFF \
  "-DBUILD_ADDITIONAL_TOOLKITS=TKBO;TKFillet;TKShHealing;TKMesh;TKPrim;TKDESTEP;TKXCAF" \
  -DUSE_FREETYPE=OFF -DUSE_OPENGL=OFF -DUSE_GLES2=OFF -DUSE_TK=OFF -DUSE_TCL=OFF -DUSE_XLIB=OFF -DUSE_TBB=OFF \
  -DUSE_RAPIDJSON=OFF -DUSE_DRACO=OFF -DUSE_VTK=OFF -DUSE_FREEIMAGE=OFF -DUSE_FFMPEG=OFF -DUSE_OPENVR=OFF \
  -DUSE_EIGEN=OFF -DBUILD_DOC_Overview=OFF -DBUILD_GTEST=OFF -DBUILD_USE_PCH=OFF \
  -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0
# 3. Build and install (about 5 minutes on 10 cores).
cmake --build build -j 10
cmake --install build
```

| RID | the per-RID line | how built |
|---|---|---|
| osx-arm64 | `-DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0` | native — **verified** |
| osx-x64 | `-DCMAKE_OSX_ARCHITECTURES=x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0` | cross from arm64 — **built, not run** |
| linux-x64, linux-arm64 | *(none)* | native, or arm64 cross from x64 — owed |
| win-x64 / win-arm64 / win-x86 | replace `-G "Unix Makefiles"` with `-G "Visual Studio 17 2022" -A x64` / `ARM64` / `Win32`; build and install with `--config Release`; check the archive with `certutil -hashfile OCCT-V8_0_1.tar.gz SHA256` | owed |

`13.0` is circuitRF's `LSMinimumSystemVersion`. `USE_XLIB=OFF` matters only on Linux, where it defaults to
ON and would link libX11 into a headless worker.

**What ships is the 25 `lib*` files only** — the installed `share/opencascade/resources` tree (Draw
resources, shaders, message files) is not needed: `selftest` and the whole Q8 table came out identical with
it removed. **The files must carry their install names**: the executable binds to
`@rpath/libTKernel.8.0.dylib` (not the `8.0.1` file), so the shipped folder holds each library under its
`.8.0` name. `strip -x` is safe (the 25 stripped libraries were swapped in and passed `selftest`).

**Verified** (`R-em3d61-7c`): the block above, extracted from this file by script and run verbatim
(`set -e`) in an empty directory on osx-arm64 after the note was written — a fresh download (the checksum
matched), configure, build and install in **5 m 44 s** end to end, zero warnings, 25 libraries. The harness
built against that install and `selftest` **passed**, printing the same B-rep hash (`53acdaae…89ae`) as the
spike's own build.

---

## D2 — which RIDs ship the kernel

**Recommendation: every RID whose `selftest` passes on the owner's run, and no RID on the strength of a
build alone.**

- **osx-arm64 — ship.** Built, `selftest` passes, all of Q4–Q12 answered on it.
- **osx-x64 — ship, once it has run once.** It builds from the same recipe with no failure; it has not
  executed anywhere yet. The macOS packaging script already treats this architecture as "cannot smoke here"
  (`CRF_ALLOW_UNSMOKED`); brief 62's kernel smoke should follow the same rule rather than pretend.
- **linux-x64, linux-arm64, win-x64, win-arm64 — expected to ship**; nothing macOS showed argues against
  them, and OCCT 8.0 names ARM64 a first-class target on Windows. Owed runs decide.
- **win-x86 — decided by Q13.** If it does not build or does not pass `selftest`, it takes overview §1d's
  absent path, and its installer notes carry this sentence:

  > *The 32-bit Windows edition of circuitRF does not include the geometry kernel, so booleans, fillets,
  > chamfers and STEP import and export are unavailable in it; every other feature is the same. The 64-bit
  > edition includes the kernel.*

The cost of shipping is about **20 MB per installer** (§0 Q3).

## D12 — B-rep or STEP to Gmsh

**Recommendation: the worker's B-rep, written with `TopTools_FormatVersion_VERSION_1` and no
triangles.**

Gmsh 4.15.2's OCCT 7.9.3 read all three versions OCCT 8.0.1 writes, with identical meshes (`q4/VERDICT.md`).
Version 1 is read by every OCCT (v3 appeared in 7.6), so pinning it costs nothing and keeps an older Gmsh
working if the validated list ever widens. **Write the version explicitly**: `BRepTools::Write`'s short
overloads write `CURRENT` (v3) *and include triangles*, whatever their comment says.

What **STEP would lose**, measured:
1. **Units.** A µm STEP file meshed without `Geometry.OCCTargetUnit = "UM"` became a part 1 × 0.8 × 0.5
   mm across, meshed without complaint, and **both face queries returned nothing**. A B-rep has no unit.
2. **Byte stability.** Every STEP export carries a `FILE_NAME` timestamp, so `GmshGeoWriter`'s *unchanged
   file reuses the mesh beside it* would never fire.
3. **Identity.** The STEP round trip meshed to 22,908 nodes where the B-rep gave 22,988 — the translator's
   healing re-derived the shape. The B-rep is the worker's shape exactly.

And B-rep loses nothing Gmsh uses: names and colours are not read by the `.geo` path; the face table
travels in circuitRF's own records (overview §1i).

**A caveat for brief 65:** B-rep text carries each shape's flag bits, which a validity check sets and a STEP
export clears (`selftest`: *"STEP export left the shape's B-rep CHANGED"*, flags `0111000` → `0101000`,
geometry untouched). The worker writes the hand-off file straight from the operation's result, before any
other request touches that shape, or the bytes depend on what else was asked.

---

## Tolerances

- **The worker speaks micrometres**, as `GmshGeoWriter` does. A `.c3d`'s DBU converts by one exact division.
- **Fuzzy value: none (0).** Q5's nine cases — whole and partial shared faces at integer µm; a shared plane at
  1234567 DBU / 1000 reached by two arithmetic paths (the same double); a 1-ULP gap and a 1-ULP overlap
  (2.3e-13 µm, far below `Precision::Confusion()` = 1e-7, so both read as touching); `DbuPerMicron = 3`
  (333.333… µm); two boxes rotated 30° sharing a face; a rotated box against an axis-aligned slab —
  gave valid results, no slivers, and volumes within 2.2e-16 of analytic at fuzzy 0, and **identical** results
  at fuzzy 1e-4 µm. Nothing here needs a fuzzy value; if one is ever wanted it should be justified by a
  failing case, not set by default.
- **Never set `BOPAlgo_GlueShift`.** On the 30°-rotated box against a slab it returned a *Unite* of two
  un-fused solids (+20.6 % volume), a *Subtract* that removed nothing (+29.5 %), and an empty *Intersect* —
  all flagged `IsDone`, all **valid** to `BRepCheck`. The glue option assumes the operands do not
  interfere; the worker cannot know that in advance.
- **Validate inputs and outputs.** Q9's self-intersecting input produced an invalid result reported as done.
  `BRepCheck_Analyzer` on every operand and every result is a millisecond and is the only reliable check.

## Naming

Q7 confirms overview §1g's scheme — Blank faces bare, Tool faces `<tool>:<face>`, split pieces `#n`, fillet
faces `fillet(<edge>)`, edges named by their two faces — with **every result face named and none named
twice** across nine operations. Four things in §1g were not right or not said, and **the overview has been
corrected** (§1g, marked *brief 61*):

1. **The edge-repeat suffix collided with the split-face suffix.** Following §1g literally, `subtract-two`
   produced `cavity:side|zmax#2#1`, and in general `x|zmax#2` means *the second edge between x and zmax* in
   one document and *the edge between x and piece 2 of zmax* in another — so a stored fillet edge could land
   on a different edge after an edit splits `zmax`, where §1g promises a refusal. `#` is legal in names.
   **Correction:** a repeated pair takes a third `|` field holding a bare number — `cavity:side|zmax|2`.
   `|` is forbidden in names and no face name is a bare number, so the three fields cannot be confused.
2. **Pieces are numbered geometrically, not in OCCT's order.** `Modified()` returned `pin:side`'s two pieces
   in the opposite order to their positions (`unite-pin`). It is stable run to run, but it is one kernel
   version's internals; the order is by tight box, lexicographic.
3. **A fillet names faces from every edge of its contour.** OCCT propagates a fillet along a tangent chain
   (`fillet-chain`: one edge given, eight filleted). Naming from the listed edge alone left **7 of 18 faces
   unnamed**; naming from `MakeFillet::Edge(contour, i)` named all 18, each `fillet(<that edge>)` — nested
   names such as `fillet(fillet(xmax|ymin)|zmax)` arise and are fine.
4. **Seam edges.** A cylinder's or torus's side is one face whose seam bounds it on both sides: by the rule it
   is `side|side`. It is not a feature edge; Edge mode (brief 67) should not offer it.

`#n` for edges is needed in ordinary shapes, not only exotic ones: one face pair bounds two edges in each of
the three Q5 booleans checked and in the trench case (`q7/same-face-pair.txt`).

## Crash posture

What Q9 found, and the rule brief 63 is written against:

| failure | how it surfaces | the worker's answer |
|---|---|---|
| operation cannot be done (oversize fillet) | `IsDone() == false` | refusal |
| invalid argument (zero-thickness box) | `Standard_Failure` (`Standard_DomainError`) | refusal (catch `const Standard_Failure&`; it derives from `std::exception` in 8.0) |
| invalid input (self-intersecting) | **none** — "done", invalid result | `BRepCheck_Analyzer` on inputs and outputs → refusal |
| a fault inside the kernel | SIGSEGV kills the process (exit 139) … | … unless `OSD::SetSignal(false)` is installed at start; then `OCC_CATCH_SIGNALS` turns it into `OSD_SIGSEGV` and the process ran a boolean and a fillet correctly afterwards |
| a runaway boolean | honours `Message_ProgressIndicator::UserBreak()` within ~50 ms | optional courtesy |
| a runaway fillet | **never polls `UserBreak()`** (0 polls in a 1.04 s fillet) | **kill** |

**The restart rule:** refuse and carry on after a C++ exception or an `IsDone() == false`; after a
**caught signal**, answer the request with a refusal and then **exit**, and let the client restart the
worker — a handler cannot vouch for a heap after a wild write, and a restart costs 40 ms. **Cancellation
is always by killing the process**; brief 63 must not promise cooperative cancel.

## Licence

`q11/CHECKLIST.md` is written for brief 62's `THIRD-PARTY-NOTICES.md` entry. Against the built tree:

- **No third-party library is linked** on macOS (both architectures). Linux and Windows owed.
- **Licence texts:** `LICENSE_LGPL_21.txt`, `OCCT_LGPL_EXCEPTION.txt` — both ship in `licenses/`.
- **Notice line:** *Open CASCADE Technology 8.0.1 — Copyright © 1990–2026 Matra Datavision and OPEN CASCADE
  SAS; GNU LGPL 2.1 with the Open CASCADE Exception 1.0.*
- **Three notices to reproduce:** David M. Gay's `strtod` (© Lucent Technologies), `delabella` (MIT, ©
  2018 GUMIX – Marcin Sokalski), `FlexLexer.h` (BSD, © The Regents of the University of California); and
  one line on Bison's skeleton exception in the STEP parser.
- **Eight files with a proprietary header.** Eight files compiled into `TKGeomBase` — `GeomConvert_CurveToAnaCurve`,
  `GeomConvert_SurfToAnaSurf`, `Geom2dConvert_ApproxArcsSegments`, `Geom2dConvert_PPoint` (`.cxx` and
  `.hxx` each) — carry a **proprietary** header (*"This file is part of commercial software by OPEN CASCADE
  SAS … may not be provided or otherwise made available to any third party"*), inside a repository
  published under LGPL-2.1. They arrived with upstream's 2022 "Interface for checking canonical geometry"
  (OCCT 7.7) and are in every release since; no upstream issue raises it. `TKGeomBase` cannot be left out,
  and removing the files would be a patch (`R-em3d61-1c`).
  **Owner, 2026-09-27:** the header text is taken to be incorrect, and upstream has been asked to correct it
  — [OCCT#1564](https://github.com/Open-Cascade-SAS/OCCT/issues/1564) (still present on `master` the day it
  was filed). Meanwhile **none of OCCT's files enters the circuitRF repository**, these eight included:
  anyone compiling circuitRF from source fetches OCCT's source themselves with the recipe (brief 62
  `R-em3d62-1b`; `.gitignore` carries the backstop).
  **The corresponding source is met by a written offer**, not a published archive (owner, 2026-09-27; the
  offer's text is brief 62 `R-em3d62-6c`), and **circuitRF builds, tests and runs with no OCCT on the
  machine at all** (`R-em3d62-3f`).

---

## Not done

| part | owed by | what closes it |
|---|---|---|
| osx-x64 `selftest` (`R-em3d61-7b`) | owner | one `occt_probe selftest` on a Mac with Rosetta or an x86-64 Mac; paste into `q3/build-osx-x64.log` |
| linux-x64 and linux-arm64: build, `selftest`, closure, sizes | owner | the recipe (no per-RID line), `q3/build-linux-*.log` in §4's format |
| win-x64, win-arm64, win-x86: build, `selftest`, closure, sizes | owner | the recipe's Windows line, `q3/build-win-*.log` |
| **Q13** — win-x86 and win-arm64, the 32-bit array limit | owner | `q13/RESULTS.md` |
| Q7 on a second platform | owner | free with the above: `selftest` prints the reference B-rep's hash (`53acdaae…89ae` on osx-arm64) |
| Q8's visual check of the exported STEP files | owner | open `q8/three-parts-{mm,um,mil}.step`: three parts `lid`, `pin`, `body`, coloured, the right size |
| The eight proprietary-header files | upstream | an answer on [OCCT#1564](https://github.com/Open-Cascade-SAS/OCCT/issues/1564); the owner's interim decision is recorded in § Licence |
| D2, D12 | owner | decide from the two sections above |

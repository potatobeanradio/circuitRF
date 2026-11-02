# Brief 61 — the kernel spike: OpenCASCADE, built, measured and tried

**Series:** [3D EM, fourth series](brief-em3d-60-overview.md) · **Tag:** `R-em3d61-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.2 Route B, §6.4, §6.5; overview §1a–§1e, §1g, §1i
**Area:** `testdata/em3d/f4b/` (new), `docs/design/em-3d-f4b-spike-findings.md` (new). **No product code.**
**Depends on:** — · **Blocks:** 62 (the recipe, the RID list), 63 (the naming and crash answers), 65 (D12),
and the owner's D1, D2 and D12

---

## 0. What this brief delivers

This is the F0 of the kernel series, on [brief 1](brief-em3d-1-f0-spike.md)'s pattern. Four things:

1. **A pinned OCCT** — version, upstream source archive, SHA-256, and the smallest CMake configuration that
   builds everything the series needs — proposed for brief 62 to adopt as its recipe (§3, Q1–Q2).
2. **Measurements per RID** — build time and shipped size — and a recommendation for **D2** (which RIDs ship
   the kernel), with Windows x86 answered, not assumed (Q3, Q13).
3. **Answers to named questions** (§3), each a sentence with evidence and a file under `testdata/em3d/f4b/`.
   Two of them decide owner decisions: **Q4 decides D12** (the Palace hand-off format), **Q13 decides D2**.
4. **A licence checklist verified against the built tree**, not against the licence text alone (Q11).

**Nothing under `src/` changes, and nothing under `tools/` is created.** The spike's harness is a throwaway
C++ program under `testdata/em3d/f4b/harness/`, marked as spike material. Brief 62 writes the worker; it may
start from the harness, but a spike that leaves product code behind has made a design decision nobody
reviewed.

---

## 1. `R-em3d61-1` — who does what

| Part | Who | Why |
|---|---|---|
| Build OCCT for **osx-arm64** and **osx-x64** (`CMAKE_OSX_ARCHITECTURES`) on the owner's Mac; build the harness; run Q4–Q12 | **agent** | a shell is enough |
| Build OCCT and run the harness's self-test on **linux-x64** and **linux-arm64** | **owner** (or CI), following §4's log format | no Linux box is reachable from this session |
| Build OCCT and run the self-test on **win-x64**, **win-arm64** and **win-x86** (Q13) | **owner** | Windows builds OCCT with MSVC; nothing here can run it |
| Open the spike's exported STEP file in any STEP viewer the owner has (Q8) | **owner** | a visual check of names and colours |

**`R-em3d61-1a`** The findings note states, per part and per RID, whether it was done and by whom. A part
not done is listed as **not done**, never omitted — the rule brief 1 set (`em-3d-f0-findings.md` §1).

**`R-em3d61-1b`** Build OCCT **from upstream's source archive**, with upstream's CMake, exactly as brief 62's
recipe will. No package manager's prebuilt binary is the answer: those do not exist for every RID circuitRF
ships (Windows arm64 and x86 in particular), and a shipped binary must be one circuitRF can rebuild — that is
the LGPL's corresponding-source obligation (overview §1b). A package manager's build may be used as a
**comparison** for Q2's module closure and nothing else.

**`R-em3d61-1c`** **Do not patch OCCT.** A build failure and what fixed it is itself the data brief 62's
known-failures notes are seeded from. A patch, if one turns out to be unavoidable, is a finding reported to
the owner: a modified OCCT changes the licence obligation (the modification's source must be published
too).

---

## 2. `R-em3d61-2` — the harness

A single C++17 file, `testdata/em3d/f4b/harness/occt_probe.cpp`, with a CMake file beside it that finds the
OCCT just built. It is **not** the worker: no protocol, no stdin. Each question below is a sub-command
(`occt_probe q5`, `occt_probe q6` …) that writes its evidence to `testdata/em3d/f4b/<q>/` and prints a
one-line verdict. One sub-command, `selftest`, runs the fast subset (box − cylinder, a fillet, a STEP write
and read back in memory) and prints counts — the same check brief 62's smoke test will make, so the owner's
per-RID runs (§1) exercise what shipping will exercise.

**Lengths are micrometres.** `GmshGeoWriter` writes µm because OCCT's tolerances are absolute (its header
comment: in metres a 25 µm wire section sits two orders of magnitude from them, in µm nine). The harness
takes that as given and **Q5 re-verifies it** for booleans, where tolerance decides whether two touching
faces merge. A `.c3d`'s integer DBU converts to µm by one exact division (`DbuPerMicron`, default 1000).

---

## 3. `R-em3d61-3` — the questions, each answered with evidence

| # | Question | How it is answered | Evidence |
|---|---|---|---|
| **Q1** | **Which OCCT.** Is 8.0.1 (2026-07-30) the version to pin? What changed since 7.9 that the worker would meet (toolkit renames — STEP moved into `TKDESTEP` at 7.8; deprecated APIs; the minimum CMake and compiler)? | Read upstream's release notes for 8.0.0 and 8.0.1; build it; record the tag, the archive URL, its SHA-256 as downloaded, and whether upstream publishes a checksum to compare against | `q1/VERSION.md` |
| **Q2** | **The smallest module set.** Modelling data and algorithms, booleans (`TKBO`), fillets and chamfers (`TKFillet`), shape healing (`TKShHealing`), meshing (`TKMesh`), B-rep I/O, and STEP read/write **with names and colours** (XCAF: `TKXCAF`, `TKLCAF`/`TKCAF`, `TKDESTEP`, `TKXSBase`). **Does XCAF pull in the Visualization module** (`TKService`, `TKV3d`) and with it FreeType or OpenGL? Can `BUILD_MODULE_Visualization`, `BUILD_MODULE_Draw`, `USE_FREETYPE`, `USE_OPENGL`, `USE_TK`, `USE_TBB`, `USE_RAPIDJSON` and friends all be OFF? | Configure with everything OFF but what is needed; add back only what fails to link; record the final CMake line. Then print the harness's **dependency closure** (`otool -L` recursively on macOS; `ldd` on Linux; `dumpbin /dependents` on Windows) | `q2/cmake-options.txt`, `q2/closure-<rid>.txt` |
| **Q3** | **Build time and shipped size, per RID.** | Wall-clock of the OCCT build (configure + build + install, a stated `-j`) and the **size of the closure Q2 found** — uncompressed, and compressed the way each installer compresses (`.dmg`, `.msi`/`.zip`, `.deb`/`.tar.gz`). Seven RIDs: `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `win-x86`. Also: **how each RID is built** — natively, or cross from which host (Linux arm64 from an x64 box; Windows arm64 and x86 from x64 MSVC cross toolsets) | `q3/build-<rid>.log`, `q3/SIZES.md` |
| **Q4** | **Gmsh reads our shape (decides D12).** Does Gmsh 4.15.2 — the validated version (`em-3d-f0-findings.md` §6) — read a B-rep **written by OCCT 8.0.1** through `ShapeFromFile` under `SetFactory("OpenCASCADE")`? Which OCCT is **inside** that Gmsh (`gmsh -info`)? If the default B-rep format version is newer than its reader, does writing an older `TopTools_FormatVersion` fix it, and does that lose anything? | A box − cylinder with a filleted rim, written as text B-rep (each format version the writer offers) and as STEP. For each: a `.geo` that imports it, runs `BooleanFragments` inside an air box with `Geometry.OCCBooleanPreserveNumbering = 1` and `Geometry.OCCBoundsUseStl = 1`, and recovers the fillet face and one planar face by a **tight bounding-box query** against boxes the harness computed (overview §1i). Record: volume count, surface count, the two queries' hit counts, and `gmsh -3` success with `Mesh.ElementOrder = 2` (what `GmshGeoWriter` writes). **Mesh only — no Palace run** | `q4/*.brep`, `q4/*.step`, `q4/*.geo`, `q4/gmsh-*.log`, `q4/VERDICT.md` |
| **Q5** | **Coincident faces in integer DBU.** Two boxes sharing a face **exactly** (integer µm, and a non-integer µm from a DBU that is not a multiple of 1000): do *Unite*, *Subtract* (a tool touching the blank only on a face) and *Intersect* give clean results with the default fuzzy value 0? Is `SetFuzzyValue` needed, and at what value? Does `SetGlue(BOPAlgo_GlueShift)` matter? And a box rotated 30° about X (irrational corners, brief 40 §1d) against an axis-aligned one | For each: `BRepCheck_Analyzer` validity, counts of solids / shells / faces / edges, volume against the analytic value, and whether a **sliver** face (area below 1e-6 of the smallest input face) exists | `q5/RESULTS.md` |
| **Q6** | **A fillet on a curved edge.** A cylinder (a pin) united with a block — the circular intersection edge filleted at 50 µm; the rim of a cylinder subtracted from a lid filleted at 25 µm; a chamfer on each; a fillet across a **tangent chain** (a rounded-rectangle outline extruded, all top edges) | Validity, face counts, volume against the analytic value (Pappus for the torus-segment fillet), and wall clock per operation | `q6/*.brep`, `q6/RESULTS.md` |
| **Q7** | **Names survive (overview §1g).** For `BRepAlgoAPI_*`: does `Modified` / `Generated` / `IsDeleted` on each operand face give the naming map the overview specifies (Blank faces bare, Tool faces `<tool>:<face>`), **including a face split in two**? For `BRepFilletAPI_MakeFillet` / `MakeChamfer`: does `Generated(edge)` give the new faces, and `Modified(face)` the trimmed neighbours? **Is it deterministic** — the same inputs twice give the same face order and **byte-identical** B-rep, in one process, in two processes, and on a second platform? How many edges in Q5/Q6's shapes share the **same face pair** (the case §1g's `#n` suffix exists for)? | Print the history maps as tables; hash the B-rep bytes across runs | `q7/history-*.txt`, `q7/DETERMINISM.md` |
| **Q8** | **STEP round trip.** Write, through `STEPCAFControl_Writer`, three named, coloured solids in **mm**, **µm** and **mil**; read back through `STEPCAFControl_Reader`: names, colours, the unit and the geometry (volume within 1e-9 relative). AP214 and AP242 both. Then **a STEP file written by a different open-source tool** (Gmsh's own `Save "x.step"`, which the spike's machine already has): does it read, and what does `ShapeFix` report? | Tables per case; the owner opens the three exported files in a viewer of their choice (§1) | `q8/*.step`, `q8/RESULTS.md` |
| **Q9** | **Failure behaviour.** A fillet radius larger than the adjacent face; a boolean of a self-intersecting polyhedron; a zero-thickness box; a tool that misses the blank. For each: is it `IsDone() == false`, a `Standard_Failure`, or a **signal** (segfault/abort)? Does `OSD::SetSignal` turn a signal into a catchable exception, and is the process usable afterwards? Does a `Message_ProgressRange` user-break stop a long boolean, and a long fillet? | One run per case, in its own process, exit status and stderr recorded | `q9/RESULTS.md` |
| **Q10** | **openEMS reads our tessellation.** `BRepMesh_IncrementalMesh` at a stated deflection; write **PLY** and **STL**; a hand-written CSXCAD XML with a `PolyhedronReader` for each; openEMS 0.37.0-rc3 (validated) run for **10 timesteps**, only to see it parse: no `Warning: No primitives found in property` (F0 Q8's silent skip), exit 0. Is the written mesh **watertight** (every edge shared by exactly two triangles)? | Logs; edge-manifold counts | `q10/*`, `q10/RESULTS.md` |
| **Q11** | **The licence checklist, against the built tree.** Which third-party libraries ended up **linked** (Q2's closure — FreeType? TBB? RapidJSON? anything else)? Which licence files does OCCT's source carry (`LICENSE_LGPL_21.txt`, `OCCT_LGPL_EXCEPTION.txt`), and does any source file in the configured modules carry a **different** licence header? The exact copyright holder line for the notice | `grep` over the configured source tree for licence headers; the closure from Q2 | `q11/CHECKLIST.md` |
| **Q12** | **Numbers brief 63 sizes itself by.** Worker-shaped cost: process start plus library load; a box − cylinder with a fillet at a display deflection (triangle count, bytes of a float32 vertex + uint32 index buffer); the same at an FDTD-grid deflection. **Measurements for sizing, not gates** | Median of 10 runs each, machine stated | `q12/RESULTS.md` |
| **Q13** | **Windows x86 (decides D2).** Does 8.0.1 build for 32-bit MSVC, does the self-test pass, and what is the largest box − cylinder array it tessellates before the 32-bit address space refuses? Same question, smaller, for win-arm64 (builds and passes, or not) | Owner's logs (§1) | `q13/RESULTS.md` |

**`R-em3d61-3a`** A question not answered is reported **not answered, and why** — never folded into
another answer.

**`R-em3d61-3b`** Solver runs stay tiny (standing practice: keep EM runs short). Q4 meshes and does not
solve; Q10 runs ten timesteps. Nothing in this brief waits minutes on a solver.

---

## 4. `R-em3d61-4` — the per-RID log format

Every build, the agent's and the owner's, is recorded in one shape so §3's Q3 table can be filled from logs:

```
RID:            linux-arm64
Host:           <os, cpu, cores, RAM>          (no user name, no home path — the repo is public)
Toolchain:      <compiler + version, cmake version, generator>
Native/cross:   native | cross from <host rid>
OCCT:           8.0.1, archive SHA-256 <…>
CMake line:     <exactly q2/cmake-options.txt, or the diff from it>
Configure:      <m:ss>    Build: <m:ss> (-j N)    Install: <m:ss>
Closure size:   <bytes uncompressed>  <bytes as the installer compresses it>
selftest:       PASS | FAIL <verbatim line>
Failures met:   <verbatim failing line, what fixed it>   (none: "none")
```

**`R-em3d61-4a`** Paths in committed logs are rewritten to the shape of the path (`<home>/…`, `<build>/…`),
never the machine's real one (standing rule: no personal paths in the repository).

---

## 5. `R-em3d61-5` — what the findings note recommends

`docs/design/em-3d-f4b-spike-findings.md` opens with a one-screen table (brief 1's shape) and then:

- **§ The recipe** — the version, archive URL, SHA-256 and CMake line, ready for brief 62 to copy into
  `tools/geometry-worker/occt/`.
- **§ D2** — the RIDs that ship the kernel, and for any that do not, the one sentence its installer notes
  will carry.
- **§ D12** — B-rep or STEP to Gmsh, with the format version if B-rep, and what Q4 showed would be lost by
  the other.
- **§ Tolerances** — the fuzzy value (or none) Q5 supports, and the unit the worker speaks.
- **§ Naming** — whether Q7 confirms overview §1g as written; if not, the smallest change to §1g that the
  history maps support. **The overview is corrected, not worked around.**
- **§ Crash posture** — what Q9 found; brief 63's restart rule is written against it.
- **§ Licence** — Q11's checklist, which brief 62's `THIRD-PARTY-NOTICES.md` entry is written from.
- **§ Not done** — every part of §1 not done, and by whom it is still owed.

---

## 6. `R-em3d61-6` — what is committed

```
testdata/em3d/f4b/
    README.md                    what each directory holds; the machine; "spike material, not product code"
    harness/occt_probe.cpp       the harness (§2)
    harness/CMakeLists.txt
    q1/ … q13/                   evidence per question (§3)
docs/design/em-3d-f4b-spike-findings.md
```

**Not committed:** OCCT's source, its build tree, any built library, any binary. The recipe says how to get
them. Committed evidence files stay small (a B-rep of a filleted box is kilobytes); anything over 1 MB is
described, not committed.

---

## 7. Gate

**`R-em3d61-7a`** Q1–Q12 answered for **osx-arm64**, each with its evidence file. Q3 and Q13 have a row
per RID, filled or marked *not done — owed by owner*.

**`R-em3d61-7b`** The harness's `selftest` passes on osx-arm64 and osx-x64 (the latter under Rosetta),
and its output is committed.

**`R-em3d61-7c`** The findings note's recipe builds from a clean directory by copy-and-paste, verified once
by the agent after the note is written.

**`R-em3d61-7d`** `git grep` over `testdata/em3d/f4b/` and the findings note finds no commercial vendor or
product name, no personal path, and no user name.

### Owner check list

1. On a Linux x64 and a Linux arm64 machine, and on Windows (x64, arm64, x86): build OCCT with the recipe;
   build the harness; run `occt_probe selftest`; send the §4 log.
2. Open `q8/three-parts-mm.step` (and the µm and mil files) in any STEP viewer: three parts, named
   `lid`, `pin`, `body`, coloured, the right size.
3. Decide **D2** and **D12** from the findings note's recommendations.

---

## 8. Scope

- **No product code** (§0). No change under `src/`, `tools/`, `packaging/` or `tests/`.
- **No patch to OCCT** (`R-em3d61-1c`).
- **No vendor data.** Every STEP file the spike reads is one it wrote, or one an open-source tool on the
  spike's machine wrote. No connector or package from a manufacturer is committed or named.
- **No Palace solve and no long openEMS run** (`R-em3d61-3b`).
- **Commercial names stay out**, in the evidence as well as the note.
- **Findings go in the findings note** and, where they change a design statement, in `em-3d.md` at rev 7
  (overview `R-em3d60-2`) — never in `CLAUDE.md`.

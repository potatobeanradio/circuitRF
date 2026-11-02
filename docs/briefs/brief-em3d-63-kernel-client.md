# Brief 63 — the managed client: finding the kernel, speaking to it, and saying when it is not there

**Series:** [3D EM, fourth series](brief-em3d-60-overview.md) · **Tag:** `R-em3d63-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.2 Route B (*build / pick / export*), §5.2 (why a process)
**Area:** `src/Design/ThreeD/Occ/` (new: `GeometryKernel.cs`, `GeometryKernelSession.cs`,
`GeometryKernelFrame.cs`, `GeometryKernelCache.cs`, `GeometryKernelTree.cs`), `src/Ui/ThreeD/` (the
capability binding only), `src/Ui/Views/Dialogs/` (one read-only Settings row), `tests/Ui.Tests/Support/`
(`KernelFact`, `KernelTheory`), `tests/Ui.Tests/ThreeD/Occ/`, `tests/Firewall.Tests/`
**Depends on:** 62 (a worker that answers) · **Blocks:** 64, 65, 66, 67, 68, 69

---

## 0. What this brief delivers

The one piece of managed code that talks to `tools/geometry-worker`. After it, the rest of the series asks
**one** object — `GeometryKernel` — three questions, and nothing else in circuitRF ever starts the worker,
reads its pipes or words its absence:

1. **Is it here?** A **capability**: available, or absent with a *reason* and an *action*. Every disabled
   command's tooltip, the refusal on open (brief 64), `check`, `explain` and `em` read their sentence from
   it. There is **one** place that words the absence.
2. **Build this.** A resolved tree (numbers only) goes in; a B-rep, a tessellation, a face table and an
   edge table come out — from the cache when the tree has been built before.
3. **Convert this.** Export a shape as B-rep, STEP, PLY or STL; import a STEP file.

It also delivers the rules that keep the worker **off the frame path** (overview §1j): caching by resolved
input, asynchronous previews where the newest supersedes, zero calls during a drag, and a crash that costs
one operation and never the document.

No document format changes here, and no UI surface is added beyond a read-only Settings row. Brief 64 puts
kernel objects in the `.c3d`; brief 66 builds the first command on top.

---

## 1. `R-em3d63-1` — discovery: where the worker is, and what happens when the named one is broken

**`R-em3d63-1a` The order.** `GeometryKernel.Locate()` walks, in this order, and stops at the first
**candidate** (not the first that works — §1b):

| # | Where | Why it is here |
|---|---|---|
| 1 | `CIRCUITRF_GEOMETRY_WORKER`, when set | a developer pointing at a build of their own; CI pointing at the one it built |
| 2 | `<AppContext.BaseDirectory>/geometry-kernel/geometry-worker[.exe]` | **the shipped route** — brief 62 lays the worker and its OCCT libraries in that folder of every publish tree |
| 3 | `tools/geometry-worker/build/<rid>/geometry-worker[.exe]`, found by walking up from `AppContext.BaseDirectory` to the directory holding `circuitrf.slnx` | a developer's `dotnet run` after `tools/geometry-worker/build.sh` |

There is **no Settings path** for the worker. It is circuitRF's own program, shipped with it; a user has
no second copy to point at, and a path field would only be a way to run a mismatched build.

**`R-em3d63-1b` A named worker that does not work is reported, never replaced.** If the environment
variable is set, it is the **only** candidate: a missing file or a failed handshake there is the absence
reason, and the shipped copy is **not** tried. The same holds between rows 2 and 3: a publish tree that has
a `geometry-kernel/` folder whose worker fails is *Broken*, not a cue to go looking for a source tree. This
is the rule `SolverDiscovery` states for Palace (em-3d.md §7.1: *a named program that does not work is
reported, never silently replaced*) and it matters more here — **the installed app must never depend on the
source-tree walk-up**, which is exactly the trap PCell artwork fell into (it rendered from a source checkout
and not from an installed copy, because only the walk-up had ever run). Brief 62's `CliSmoke` step proves
row 2 on every package; a test here proves row 3 is never consulted when row 2's folder exists.

**`R-em3d63-1c` Not a kit worker.** The geometry worker is **not** started through
`ProcessDeviceWorkerTransport.Start`, and so `DeviceWorkerPolicy`'s consent gate does not apply. That gate
exists for programs a **kit** names beside itself (`device-provider.json`); this program is part of the
installation, as `osdi-worker` is. A firewall-style source test pins that `src/Design/ThreeD/Occ/` does not
reference `CircuitRF.Core.Devices.External`, so nobody later "reuses" the kit transport and inherits a
consent prompt for circuitRF's own binary.

**`R-em3d63-1d` Platforms that do not ship it.** `GeometryKernel.ShippedRids` is the list brief 62 decides
(D2). On a RID outside it, `Locate()` does not look at row 2 at all and reports *NotShippedOnThisPlatform*;
rows 1 and 3 still work, so a developer on that platform can build their own.

## 2. `R-em3d63-2` — the handshake and the pinned version

**`R-em3d63-2a` `hello` first, every start.** The first frame a session sends is `hello`; the reply
carries:

```json
{ "ok": true, "protocol": 1, "worker": "0.1.0", "occt": "8.0.1", "rid": "osx-arm64",
  "modules": ["TKBO", "TKFillet", "TKDESTEP", "…"] }
```

**`R-em3d63-2b` Pinned, not ranged.** circuitRF embeds brief 62's recipe (`tools/geometry-worker/occt/
recipe.json`, as an `EmbeddedResource` of `CircuitRF.Design`) and requires **exactly** its `protocol` and
its `occt` version. A different OCCT is a different kernel — booleans and fillets do not give bit-identical
results across OCCT releases, and brief 65's goldens depend on them. A mismatch is *WrongVersion*, naming
both: *"The geometry kernel at <path> is Open CASCADE Technology 7.9.0; this build of circuitRF was made
with 8.0.1."* The `rid` must match the process's own architecture (the `osdi-worker` lesson: a wrong-arch
helper fails far from its cause).

**`R-em3d63-2c` Budget.** The handshake has a 10 s deadline (a cold start loads ~20 shared libraries off a
possibly-slow disk; brief 61 measures the real figure and this constant follows it). A deadline miss is
*Broken*, with the worker's own stderr tail attached.

## 3. `R-em3d63-3` — the capability, and the one place its absence is worded

**`R-em3d63-3a` The record.**

```csharp
public enum GeometryKernelAbsence { NotBuilt, NotShippedOnThisPlatform, Missing, Broken, WrongVersion }

public sealed record GeometryKernelCapability(
    bool Available, GeometryKernelAbsence? Absence,
    string? WorkerPath, string? OcctVersion, string HowFound,
    string Reason,     // a sentence: what is wrong ("…was not found at …", "…stopped during start-up: …")
    string Action);    // a sentence: what fixes it, per Absence (table below)
```

| Absence | When | Action sentence |
|---|---|---|
| `NotBuilt` | running from a source tree, row 3's folder absent | *Build it with tools/geometry-worker/build.sh (build.cmd on Windows), then check again.* |
| `NotShippedOnThisPlatform` | RID outside `ShippedRids` | *This platform's circuitRF does not include the geometry kernel. Booleans, fillets and STEP need a 64-bit build.* (wording follows D2) |
| `Missing` | a publish tree without `geometry-kernel/` or with the worker gone | *Reinstall circuitRF to restore it.* |
| `Broken` | handshake failed, crashed at start, deadline missed | *Reinstall circuitRF. If it persists, report it with the log at <path>.* |
| `WrongVersion` | protocol / OCCT / RID mismatch | env var: *Unset CIRCUITRF_GEOMETRY_WORKER or point it at a matching build.*; else as `Missing` |

**`R-em3d63-3b` One sentence shape for every surface.** `GeometryKernel.NeedsKernel(string what)` returns
*"<What> needs the geometry kernel, which this installation does not have: <Reason> <Action>"*. The
disabled-command tooltip (briefs 66–69: *"Boolean needs the geometry kernel…"*), brief 64's open refusal,
`check`'s finding and `em`'s refusal all call it. A source test scans `src/Ui` and `src/Cli` for the literal
words *"geometry kernel"* outside this class and fails on any hit — the one-place rule is enforced, not
hoped for.

**`R-em3d63-3c` When it is asked.** The GUI probes **once, in the background, after the main window is
shown** (a process start is ~tens of ms, and not on the launch path). Until the probe answers, kernel
commands are disabled with *"Checking for the geometry kernel…"*. The result is cached for the process;
the Settings row's *Check again* re-probes. The CLI probes lazily, the first time a verb needs it, so
`circuitrf dc` never starts the worker.

**`R-em3d63-3d` The Settings row** (Settings ▸ 3D EM, beside the solver rows, read-only): *Geometry kernel —
Open CASCADE Technology 8.0.1, included with circuitRF* (or the absence's reason and action), the path, and
*Check again*. This is also the "prominent notice" brief 62 needs beyond the About box.

## 4. `R-em3d63-4` — the protocol

**`R-em3d63-4a` Framing.** The device worker's layout, reused on purpose so a hex dump of either reads the
same way — `[uint32 jsonLen][uint32 binLen][jsonLen bytes UTF-8 JSON][binLen bytes]`, little-endian — with
one difference: the binary part is **bytes**, not doubles, because a geometry reply carries doubles
(vertices), 32-bit integers (triangles, face ids) and opaque B-rep/STEP bytes. The JSON header declares the
sections in order: `"blobs": [{"name":"vertices","type":"f64","count":3012}, {"name":"tris","type":"u32",…},
{"name":"brep","type":"bytes","count":81234}]`. `GeometryKernelFrame` is a new codec in `src/Design`, not a
reuse of `DeviceWorkerFrame` (which lives in `src/Core` and is doubles-only). It carries the example
worker's three lessons into its tests: **flush after every reply**, **loop short reads**, **one request
at a time per pipe** (`DeviceWorkerChannel`'s lock and its reason).

**`R-em3d63-4b` Requests.**

| Request | In | Out |
|---|---|---|
| `hello` | — | §2a |
| `build` | a resolved tree (§5), options (fuzzy value from brief 61, `KeepTools`) | `shape` handle = the tree's hash; `valid`; `notes[]` (healing, a tool that missed the blank) |
| `tessellate` | `shape`, linear deflection (µm), angular deflection (rad) | vertices f64, triangles u32, per-triangle face index u32 |
| `faces` | `shape` | per face: name, surface kind (`plane`, `cylinder`, `cone`, `sphere`, `torus`, `bspline`, `other`), tight box, area, min radius of curvature (0 for planar) |
| `edges` | `shape`, deflection | per edge: name (overview §1g), the two face names, curve kind, length, min radius, and a **polyline** (f64) for drawing and snapping |
| `export` | `shape[]`, format `brep`/`step`/`ply`/`stl`, units, names, colours | bytes |
| `import-step` | file bytes (or path), options | one `shape` per part, the XCAF tree (names, colours, units), healing report |
| `release` | `shape[]` | — |
| `shutdown` | — | — |

**`R-em3d63-4c` Units.** The worker works in **micrometres**, as the `.geo` script already does
(`GmshGeoWriter`'s header: OCCT's absolute tolerances sit nine orders of magnitude from a 25 µm feature in
µm, two in metres). The client converts from DBU by `LayoutUnits`' exact decimal path, once, when it builds
the tree.

**`R-em3d63-4d` A refusal is an ordinary reply**, `{"ok":false,"code":"fillet.radius-too-large",
"object":"lid","detail":"<OCCT's own text>"}`. The client maps `code` to a sentence naming the object in
circuitRF's voice and appends `detail` verbatim (the rule circuitRF follows for Verilog-A compiler
output: the upstream line is the value). An unmapped code gives *"The geometry kernel could not build
'<object>': <detail>"* — reported as unrecognised, never guessed at.

## 5. `R-em3d63-5` — the tree the worker receives: resolved numbers, and circuitRF's names

**`R-em3d63-5a` Elaborate first.** `GeometryKernelTree.From(...)` takes objects **after** `C3dResolver` has
turned every expression into a number (brief 51), and emits JSON with no expression, no unit and no
variable in it. The worker never sees a `.c3d`.

**`R-em3d63-5b` Every node carries its face names.** A primitive's node lists the names
`C3dObject.FaceNames()` gives it, in the primitive's face order; a polyhedron's node carries each face's
name. The worker names its OCCT faces from these (the correspondence per primitive is written in the
worker's README and pinned by a test that builds each primitive and compares the `faces` reply to
`FaceNames()`). Brief 64 owns what names a boolean's and a fillet's output faces get; this brief only
guarantees the names go in and come back.

**`R-em3d63-5c` Canonical, so it hashes.** The JSON is written with a fixed key order, invariant culture,
round-trip doubles and `\n` — `GmshGeoWriter`'s determinism rules — so the same resolved tree is the same
bytes and the same hash on every platform.

## 6. `R-em3d63-6` — the cache: an unchanged tree makes no call

**`R-em3d63-6a` Key.** SHA-256 over: the canonical tree, the request's own parameters (deflections,
format), and the kernel identity (protocol + OCCT version + worker version). A different kernel never
serves another's result.

**`R-em3d63-6b` Two levels.**
- **In memory**, per process, bounded by bytes (256 MB, least-recently-used). Undo, redo, closing and
  reopening an editor, and re-elaborating after an unrelated edit all hit it.
- **On disk**, under `UserStateDirectory.SubDir("geometry-cache")`, bounded (1 GB, oldest access first),
  holding B-reps and tessellations only. It makes re-opening a document with a large imported STEP part
  after a restart instant. Every file is written to a temporary name and renamed, so a killed process never
  leaves a half-file a later run would trust; a file that fails to parse is deleted and rebuilt. The
  Settings row shows its size and has *Clear*.

**`R-em3d63-6c` A failing tree is cached as failing.** If a `build` crashes the worker (§7), the tree's hash
is recorded as *failed* with its sentence, for the process's lifetime. Re-elaboration of an unchanged tree
then reports the same refusal **without** calling the worker. Without this, every re-elaboration after a
crash would crash the worker again, in a loop the user sees as a stutter.

**`R-em3d63-6d` Counters** on `GeometryKernel`: `RequestsSent`, `MemoryHits`, `DiskHits`, `WorkerStarts`,
`WorkerRestarts`, `PreviewsDiscarded`. Every gate below is one of them.

## 7. `R-em3d63-7` — a crash, a timeout, a cancel

**`R-em3d63-7a` Crash.** The pipe closing or the process exiting mid-request fails **that request** with
*"The geometry kernel stopped while building '<object>' (exit code <n>). Nothing was changed; it has been
restarted."*, and the next request starts a fresh worker (`WorkerRestarts` + 1). The document is never
touched by a failed request: the elaborator reports the object as refused (brief 64), the editor keeps
drawing the last good result, and undo history is unchanged. Three consecutive start-up failures make the
capability *Broken* for the process.

**`R-em3d63-7b` Timeout.** Each request type has a deadline (build 120 s, import-step 300 s, the rest 30 s;
brief 61's measurements set the real values). A miss kills the worker, restarts it, and refuses the request
naming the deadline.

**`R-em3d63-7c` Cancel.** Every request takes a `RunControl`. Its token cancelling **kills the worker**
(the only interruption that always works, overview §1c) and throws `OperationCanceledException`, as every
engine here does. The headless `em` verb passes `RunHost`'s control, so Ctrl-C during a long STEP import
exits 130 with nothing written.

## 8. `R-em3d63-8` — previews, and the drag rule

**`R-em3d63-8a` Two sessions.** A **model** session serves elaboration (what gets saved, simulated and
exported); a **preview** session serves dialogs (brief 66's boolean preview, brief 67's fillet preview).
A preview can then never delay a commit, and killing a superseded preview never loses a model result.

**`R-em3d63-8b` Newest supersedes.** `RequestPreview(tree)` returns a task; a newer request **supersedes**
any pending one (it is dropped unsent) and any in-flight one (its reply is discarded when it arrives,
`PreviewsDiscarded` + 1). Only the latest is ever drawn. Closing the dialog or Esc cancels the in-flight
preview, which kills and lazily restarts the preview session.

**`R-em3d63-8c` Zero calls during a drag** (overview §1h; brief 46's gate restated). Dragging an operand of a
boolean moves its drawn batch by transform; `RequestsSent` does not change until release, and on release it
rises by exactly the number of trees the edit changed — one, for one boolean.

## 9. `R-em3d63-9` — tests that need the worker say so

**`R-em3d63-9a`** `KernelFactAttribute` and `KernelTheoryAttribute` in `tests/Ui.Tests/Support/` skip
**with a reason** when `GeometryKernel.Capability` is not available: *"No geometry kernel — <Reason>
<Action>"*. The shape is `FixtureFactAttribute`'s. A fresh clone and a CI job that did not build the worker
are green and say what they did not test.

**`R-em3d63-9b`** Everything that can be tested **without** a worker is: the codec (against in-memory
streams, including short reads), the cache (with a fake session), the capability's wording for every
`Absence` (with a fake locator), the discovery order, the canonical tree bytes.

## 10. Gates

1. **Discovery.** A test table over fake file trees: env var wins; a broken env-var worker is *Broken*, not
   replaced; a publish tree's `geometry-kernel/` folder is used and the source tree is not consulted when
   it exists; `NotShippedOnThisPlatform` skips row 2.
2. **Handshake.** `[KernelFact]` — a real worker answers `hello` with the recipe's `protocol` and `occt`; a
   stub worker (a script printing a wrong version) gives *WrongVersion* naming both.
3. **One sentence.** Every `Absence` renders through `NeedsKernel`; the source scan finds *"geometry
   kernel"* nowhere else in `src/Ui` or `src/Cli`.
4. **Codec.** Round-trip of every blob type; a reply split across 1-byte reads decodes identically.
5. **Cache.** `[KernelFact]` — building the same tree twice sends one `build`; after `Clear` of memory, the
   disk hit serves it (`DiskHits` = 1, `RequestsSent` unchanged); a changed OCCT identity misses.
6. **Crash.** `[KernelFact]` — a debug-only request `crash` (compiled into the worker's test build only, see
   brief 62) kills it; the request is refused with §7a's sentence, `WorkerRestarts` = 1, the next request
   succeeds; the same tree again is refused from the *failed* cache with `RequestsSent` unchanged.
7. **Cancel.** `[KernelFact]` — a debug-only `sleep` request cancelled via `RunControl` returns in under the
   deadline with `OperationCanceledException` and a restarted worker.
8. **Preview.** Three rapid `RequestPreview` calls: at most two sent, the first two discarded, only the third
   delivered.
9. **Firewall.** No assembly in `src/` P/Invokes an OCCT library or the worker (`tests/Firewall.Tests`,
   beside `SolverBoundaryTests`); `src/Design/ThreeD/Occ/` does not reference the device-worker namespace.

## 11. Owner check list (Debug build)

1. Settings ▸ 3D EM: the *Geometry kernel* row shows the version and path, and *Check again* works.
2. Rename the worker's folder in the build output; restart; the row says what is wrong and what to do; a
   3D editor's Boolean command (once brief 66 lands) is disabled with the same sentence as its tooltip.
3. Put it back; *Check again*; the command enables without a restart.

## 12. Scope

- **No document change, no new command.** Briefs 64 and 66 are the first users.
- **The worker is never started on the launch path**, nor by a verb that does not need it.
- **No timing gates.** Deadlines are behaviour; gates are counters.
- **Names:** user-facing text says *geometry kernel*; the Settings row and About box name *Open CASCADE
  Technology* (brief 62's notice). No commercial kernel is named anywhere.
- Findings go in `src/Design/RESOLVED.md`, never `CLAUDE.md`.

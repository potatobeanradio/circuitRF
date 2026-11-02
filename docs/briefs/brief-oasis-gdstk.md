# Brief — OASIS import and export, and a second GDSII route, through gdstk

**Tag:** `R-oas-n` · **Phases:** G0 (spike, go/no-go) → G1 → G2 → G3 → G4 → G5
**Precedent:** the geometry worker (`tools/geometry-worker/README.md`, briefs em3d-62 and em3d-63, the OCCT recipe in
`docs/design/em-3d-f4b-spike-findings.md`), the GDSII interchange (`brief-L4a-gdsii-interchange.md`), `convert`
(`docs/design/cli.md`, `tests/Ui.Tests/ConvertCliVerbTests.cs`)
**Area:** `tools/gdstk-worker/` (new), `src/Design/Layout/Interchange/` (+ `Gdstk/`, new), `src/Cli/LayoutConvert.cs`,
`src/Cli/DocumentKinds.cs`, `src/Ui` (menus, dialogs, the import/export commands), packaging, `THIRD-PARTY-NOTICES.md`
**Holds:** G1–G5 are not started until G0's go/no-go says **go**. On **hold**, nothing past G0 is built.

---

## 0. Why this brief exists

A user has asked for OASIS (SEMI P39), the compact successor to GDSII that fabs and mask shops increasingly exchange.
circuitRF reads and writes GDSII with its own C# stack (`GdsiiReader`/`GdsiiWriter`, ~1,650 lines) and has no OASIS
support at all.

The owner's decision is to use **gdstk** (https://github.com/heitzmann/gdstk, v1.0.1) and not write an OASIS stack of
our own. gdstk is a C++ library that reads and writes both OASIS and GDSII. It is used for three things:

1. **OASIS import and export**: File ▸ Import ▸ **OASIS (gdstk)…**, File ▸ Export ▸ **OASIS (gdstk)…**, and `oasis`
   as a `convert` format.
2. **A second, independent GDSII route**: File ▸ Import ▸ **GDSII (gdstk)…** and File ▸ Export ▸ **GDSII (gdstk)…**,
   alongside circuitRF's own **GDSII…** entries, which stay the default. It gives users a GDSII path that does not
   depend on circuitRF's own development, and it gives us an independent implementation to test our own GDSII
   reader and writer against (§8).
3. **Nothing else.** gdstk is a black box for reading and writing files. Its booleans, offsets and other geometry
   operations are not used, and nothing in circuitRF's own geometry (editor booleans, DRC, EM extraction) moves off
   Clipper2. gdstk's own Clipper is compiled inside the worker and is gdstk's business only.

The **"(gdstk)"** in the menu labels is deliberate: when the two GDSII routes disagree on a file, the first support
question is which one was used, and the label answers it.

### 0a. Decisions already made

| # | Decision | Why |
|---|---|---|
| D1 | **gdstk runs in a separate worker process**, `tools/gdstk-worker`, started by circuitRF and spoken to over stdin/stdout. It is never loaded into circuitRF's own process. | File readers are where crashes happen (a truncated or hostile 2 GB file). A crash in the worker costs one import, not the user's unsaved document. A runaway read is stopped by killing a process. The licence boundary is a file boundary. Headless use (`convert`, `check`, the MCP server) needs nothing extra. Same reasoning as the geometry worker, §1c of its series overview. |
| D2 | **Referenced, never vendored.** No gdstk, qhull or zlib source enters the repository. A pinned recipe (version, URL, SHA-256) fetches and builds them into the per-user cache, as `tools/geometry-worker/occt/recipe.env` does for OCCT. | The owner prefers referencing over including source. |
| D3 | **One statically linked executable per RID**: gdstk, qhull and zlib linked statically into `gdstk-worker[.exe]`, with nothing beside it. Linux links libstdc++ and libgcc statically (glibc stays dynamic); Windows links llvm-mingw's libc++ statically, so no `libc++.dll` is shipped for it. | One file to sign and stage, rather than OCCT's library closure. |
| D4 | **gdstk reads and writes bytes only.** Everything after the bytes (layer reconciliation, the layer-mapping dialog, structure naming, pin inference, cell-folder creation, the export fidelity plan, DRC/LVS-before-export) is circuitRF's existing C# code, shared with the native GDSII route through `InterchangeStructure` (`src/Design/Layout/Interchange/InterchangeStructure.cs`). | R15 of `layout-view.md` §8: one neutral model and one mapping dialog for every format. A second copy of reconciliation would diverge silently. |
| D5 | **Native GDSII stays the default** everywhere: the plain **GDSII…** menu entries, `convert`'s `gdsii` format with no flag, and every existing caller. The gdstk route is opt-in. | The native route is tested and shipped. The gdstk route is the alternative. |
| D6 | **The application works with the worker absent**, as it does with the geometry kernel absent: the (gdstk) commands are shown disabled with a tooltip naming the reason, `convert` to or from `oasis` is a refusal naming the missing worker, and every test that needs the worker skips with a reason. | `dotnet build` must never need a C++ toolchain or a network (R-em3d62-3d's rule). |

### 0b. Licences (to be confirmed by G0, Q2)

| Component | Licence | What it obliges |
|---|---|---|
| gdstk 1.0.1 | Boost Software License 1.0 | Nothing for a binary distribution. We still list it. |
| Clipper (inside gdstk's `external/`) | Boost Software License 1.0 | As above. |
| qhull (a gdstk dependency) | qhull's own licence (`COPYING.txt`) | Its `COPYING.txt` must be distributed with any copy, and recipients must be told the source is available online. |
| zlib | zlib licence | Do not misrepresent its origin; keep its notice. |

All are permissive and compatible with circuitRF's MIT licence. None is GPL.

---

## 1. Read first

- `tools/geometry-worker/README.md`, all of it: frames, refusals, the cache, `ensure-built`, staging, run paths, signing.
  **The gdstk worker copies this design wherever it applies** and departs from it only where §5 says so.
- `tools/geometry-worker/occt/recipe.env` and `RECIPE.md`; `docs/design/em-3d-f4b-spike-findings.md` (Q1, Q3, Q11: how
  the OCCT recipe and licence audit were done).
- `src/Design/Layout/Interchange/GdsiiImport.cs`, `GdsiiExport.cs`, `GdsiiReader.cs`, `GdsiiWriter.cs`,
  `InterchangeStructure.cs`, `GdsiiLayerReconciliation.cs`, `GdsiiStructureNaming.cs`, `GdsiiCoordinateValidation.cs`.
- `src/Cli/LayoutConvert.cs` (`Fmt`, `LoadSource`, the import and export switches, format inference) and
  `src/Cli/DocumentKinds.cs`.
- `src/Ui/ViewModels/WorkspaceViewModel.cs` (`ImportGdsiiLibraryAsync`, `ExportGdsii`), `LayoutEditorView.axaml.cs`
  (`OnExportGdsii`), `Views/Dialogs/GdsiiExportFidelityDialog.axaml`, and the menus at `Views/WorkspaceWindow.axaml`
  (native menu ≈ 107 and 128; window menu ≈ 862 and 920) and `Views/Shared/TornOffFileMenuView.axaml` (≈ 142 and 170).
- gdstk 1.0.1: `README.md`, `LICENSE`, `CMakeLists.txt`, `include/gdstk/library.h`, `oasis.h`, `gdsii.h`, `cell.h`,
  `polygon.h`, `flexpath.h`, `robustpath.h`, `label.h`, `reference.h`, `repetition.h`, and `external/`. Read the
  issue tracker (read only, never post) for OASIS and GDSII reading bugs, and record anything relevant.
- The OASIS specification as far as the spike needs it: record types, modal variables, repetition types 1–11,
  `CBLOCK`, `LAYERNAME`/`TEXTSTRING` tables and strict mode, and the signature `%SEMI-OASIS\r\n`.

## 2. Method rules

- **Start from the smallest case and grow it.** Each fixture in §8a is designed by hand to exercise one feature, and is
  small enough to read in a hex dump.
- **Report numbers, not impressions:** sizes, times, peak memory, counts of shapes and differences, each next to the
  fixture it came from.
- **Test files are synthetic.** No proprietary layout, no fab data, nothing from a real PDK. Whatever the owner
  supplies from another tool is synthetic too, and is committed only if the owner says so.
- **A disagreement between our GDSII stack and gdstk is a finding, not a bug to fix here.** Work out which side is
  wrong against the specification, record it, and tell the owner. A bug in circuitRF's own GDSII code goes to the
  owner as its own item; this brief does not change `GdsiiReader`/`GdsiiWriter` behaviour.
- **Never fit, patch or fork gdstk.** If a gdstk defect blocks the work, record it with a minimal reproduction and
  stop at that question. Whether to report it upstream is the owner's call.
- **Tests:** one test per claim, the scoped project only (`dotnet test tests/Ui.Tests --no-build --filter …`), read
  the TRX rather than re-running, no new timing tests (assert counters), and never run the full suite.

---

## 3. G0 — the spike (`R-oas-0`)

**Purpose:** decide whether gdstk can be built, shipped and trusted on all three platforms before any product code is
written. The spike builds a throwaway worker and a scratch harness. **It changes no product code, UI or file format.**

Harness: `tools/gdstk-worker/spike/` (a scratch C++ driver and shell scripts, with git-ignored `runs/`). The spike's
worker can be a minimal version of G1's (the `hello`, `read` and `write` of §5 in their simplest form), so that G1
grows it instead of starting over.

### Q1 — Build

Build gdstk 1.0.1's C++ library **without Python**, with qhull and zlib built from pinned sources, statically linked
into one executable, on:

| RID | Who | Note |
|---|---|---|
| osx-arm64, osx-x64 | agent | x64 cross-built with `CMAKE_OSX_ARCHITECTURES=x86_64`, as for OCCT |
| linux-x64, linux-arm64 | agent if a Linux host or container is available, otherwise owner | arm64 cross-built with the distribution's cross g++ |
| win-x64, win-x86, win-arm64 | **agent builds; owner runs one confirmation session** | cross-built **on macOS** with llvm-mingw's macOS release, which is the route `tools/geometry-worker/RESOLVED.md` records for OCCT (llvm-mingw 20260922, win-x64 and win-arm64). Use one CMake toolchain file per target triple (`x86_64-`, `i686-`, `aarch64-w64-mingw32`), kept in the cache and not the repository. Record the llvm-mingw version used. |

**Running the Windows builds before real Windows.** When Wine is installed on the agent's Mac, the agent runs win-x64 and
win-x86 under it: `--version`, `selftest`, and a read and write of every §8a fixture. Their output must equal the macOS
build's under §8b, and each OASIS file must be byte-identical if Q8 shows the writer is deterministic. Wine on macOS
cannot run ARM64 Windows programs, so **win-arm64 is build-only here.** Record the Wine version, and mark every
Wine-run number as Wine's. **Wine is a cheap first pass, never the confirmation.** Its file-path handling and crash
handling are its own, not Windows', and those are exactly what Q3 asks about.

**The owner's confirmation session, on real Windows**, kept as short as possible. The agent prepares the binaries, a
script that runs the session, and a results template, so the owner only runs and reports:
1. win-x64: `--version`, `selftest`, and the §8a read and write, compared against the macOS output;
2. win-x64: Q3's path cases (spaces, non-ASCII, a path over 260 characters) and the malformed-file counts;
3. win-arm64, if ARM Windows hardware is available. If it is not, win-arm64 is **not shipped** until it has run once
   on real hardware (G1e's smoke rule).

The script writes ASCII only (`packaging/`'s `.ps1` rule applies to it too) and needs no toolchain on the Windows
machine.

Report per RID: the exact CMake lines, whether gdstk's CMake finds qhull and zlib through `find_package` or needs
them handed in, every patch or define needed (**none is the goal; a patch to gdstk is a finding to raise with the
owner, not a fix**), build time, executable size, and the dependency listing (`otool -L`, `ldd`, `llvm-objdump -p`)
proving that only system libraries remain. Record each source's version, URL and SHA-256. Upstream may not publish
checksums: compute them, and say so, as the OCCT recipe does.

### Q2 — Licence audit

Read gdstk's `LICENSE` and the licence header of **every** file compiled into the worker, including everything under
`external/`, all of qhull's sources that are compiled, and zlib. Record:

- the licence of each component, and confirm §0b;
- any file whose header disagrees with its project's licence. OCCT had eight such files (OCCT#1564). One here is a
  **stop** until the owner has read it;
- whether gdstk 1.0.1's bundled clipper is Clipper 1 or Clipper2, and which qhull and zlib versions it expects;
- the exact notice text `THIRD-PARTY-NOTICES.md` §4 will need, and which licence texts go in `licenses/` (qhull's
  `COPYING.txt` is mandatory).

### Q3 — API and failure behaviour

From the 1.0.1 headers, record the C++ signatures used: `read_oas`, `read_gds`, `Library::write_oas`,
`Library::write_gds`, `oas_precision`, `gds_units`, `oas_validate`, and how each reports an error (`ErrorCode`,
`error_logger`). Then feed the worker **malformed input**: for each of three fixtures, 50 truncations at random
offsets, 50 single-byte flips, and 5 files of random bytes. Count per case: clean refusal, wrong-but-silent result,
crash, hang (bounded at 10 s). **A crash is acceptable** (D1 isolates it) **and must be counted; a hang decides the
client's timeout policy; a silent wrong result is the finding that matters most.**

Also test **file paths** on every platform: spaces, non-ASCII characters (`é`, `Ω`, CJK), and on Windows a path longer
than 260 characters. **On Windows this runs in the owner's real-Windows session (Q1), never under Wine.** gdstk takes `const char*` filenames, and Windows `fopen` does not read UTF-8 by default. If it
fails, measure the options and pick one: the worker opens the file itself and hands gdstk a stream, a UTF-8 process
code page (an application manifest `activeCodePage`), or a short path. **Copying the file to an ASCII temporary path is
the last resort**, because OASIS files can be gigabytes.

### Q4 — GDSII agreement (the corpus of §8a)

Read every §8a fixture with **both** circuitRF's `GdsiiReader` and gdstk's `read_gds`, and compare them under the
semantic equality of §8b. Then write each fixture back out with both writers and cross-read: ours → gdstk, gdstk →
ours. Report a table: fixture, each direction, *equal* or the exact difference. **On the simple cases they are expected
to agree; byte identity is not expected** (§8b says why). Any difference is classified per §2 as ours, theirs, or
ambiguous in the specification.

### Q5 — OASIS feature coverage

For each OASIS feature below, write a fixture with gdstk's own `write_oas` from a hand-built library, read it back,
and record **what gdstk's C++ objects hold**, which decides the mapping in §6:

- `RECTANGLE` (square and non-square), `POLYGON`, `PATH` (each extension scheme: flush, half-width, explicit),
  `TRAPEZOID` and `CTRAPEZOID` (several of the 26 types), `CIRCLE`, `TEXT` (text layer and text type distinct from
  layer and datatype)
- `PLACEMENT` with rotation (multiples of 90°), mirror, magnification, and **each repetition type 1–11**
- a shape (not a placement) carrying a repetition
- `LAYERNAME` records, the `CELLNAME`/`TEXTSTRING` tables with strict mode on and off, `CBLOCK` compression at levels
  0 and 6, `XNAME`/`XELEMENT`, and properties (standard `S_*` properties and a user property)

Specifically: does a `CIRCLE` come back as a circle or as a polygon, and at what tolerance? Is a repetition kept as a
`Repetition` or expanded? Is a trapezoid a polygon? Is anything silently dropped?

**Independent fixtures (owner, optional but valuable).** gdstk reading its own output proves the round trip, not the
reader. If the owner can produce a few small synthetic OASIS files with another, independent tool (KLayout, an
open-source viewer, can write OASIS; using its output files as data raises no licence question), the spike reads them
too. A file from a second writer exercises encodings gdstk's own writer never emits, such as unusual modal-variable use
and repetition types 4–11.

### Q6 — Coordinate exactness

gdstk holds coordinates as doubles in user units. Show that integer database units survive file → gdstk → integer
database units **exactly** at 1 nm and at 0.25 nm resolution, for coordinates near ±2³¹ and for every value in a
random sample of 10⁶. Say exactly how the worker converts (the multiply, and the rounding mode). Any off-by-one is a
**stop**.

### Q7 — Scale

Two synthetic files, generated by the harness: (a) 1,000,000 distinct polygons in 100 cells; (b) one cell of a
1,000 × 1,000 array of rectangles written as a **single repetition**. Measure per file: gdstk read time, the worker's
peak memory, the expanded shape count, and the size of the reply frames under §5's per-cell transfer. For (b), report
what expansion costs if a repetition on a shape has to be expanded because circuitRF's model has no such repetition.
**This sets G4's expansion limit (R-oas-4c).** Measure on Release builds and say so.

### Q8 — Write side

From the §8a GDSII corpus imported as `InterchangeStructure`s, write OASIS through gdstk with `detect_rectangles`,
`detect_trapezoids`, `compression_level` 6, CRC32 validation, and standard properties off. Read the result back
through gdstk, and through the independent tool if the owner has one. Record the file sizes against the GDSII
equivalents. Confirm that `oas_validate` accepts gdstk's own output.

### G0 go/no-go

**Go** requires all of:
1. Q1: macOS (both RIDs), linux-x64 and all three Windows RIDs (cross-built) build statically with **no patch to
   gdstk**, and the owner's real-Windows session (Q1) passes on win-x64. Passing under Wine does not count.
   The other Windows and Linux RIDs may follow in G1, but each one must build before it ships.
2. Q2: no licence incompatibility, and no file with a contradictory header (or the owner has read and accepted it).
3. Q3: no hang that a timeout cannot bound, and a non-ASCII path works on every platform by one of the routes named,
   with Windows judged on real Windows only.
4. Q4: every simple GDSII case agrees in both directions, or each difference is one circuitRF can map around
   deterministically (for example a path-end convention), stated as such.
5. Q5: rectangles, polygons, paths, text, placements with orthogonal repetitions, `LAYERNAME` and `CBLOCK` read
   correctly.
6. Q6: exact.

**Hold** on any failure of 1, 2, 3 or 6, or on a Q4 difference that is gdstk's defect and cannot be mapped around.
On hold: write the findings, leave the spike harness in place, and **build nothing further**. Product code must not be
started on a partial "go".

**Go with conditions** is allowed for Q5 features that are lossy but detectable (for example a circle that arrives as
a polygon). Each such condition becomes an explicit import message in G4, never a silent loss.

### G0 deliverables

- `docs/design/oasis-gdstk-findings.md`: Q1–Q8 with every number, the recipe as data, the licence table, the go/no-go
  and its conditions.
- `src/Design/RESOLVED.md`: a short section "OASIS and gdstk — spike" pointing to the findings and listing anything a
  future reader of `Interchange/` would trip on.
- `testdata/interchange/gdstk/`: the §8a corpus (`.gds` written by our writer, `.gds` and `.oas` written by gdstk),
  each under 50 KB, with a `README.md` saying which program wrote each file and from what. **They are fixed data,
  written once; tests never regenerate them.**
- `tools/gdstk-worker/spike/`: the harness, with a README naming each script and what it answers.

---

## 4. G1 — the worker and its shipping (`R-oas-1`)

Everything §1 of `tools/geometry-worker/README.md` describes, for gdstk:

- **a. Files.** `tools/gdstk-worker/`: `gdstk_worker.cpp` (one C++17 source file, MIT, circuitRF's own code), plus
  `CMakeLists.txt`, `build.sh`/`build.cmd`, `ensure-built.sh`/`.cmd`, `recipe.env` (gdstk, qhull and zlib: version,
  URL and SHA-256 each, CMake options, `KERNEL_RIDS`), `RECIPE.md`, `README.md` and `RESOLVED.md`. Reuse
  `tools/geometry-worker/find-toolchain.cmd`; do not copy it.
- **b. Cache.** `~/.circuitRF-build/gdstk/<version>/<rid>/` with `install.json` written **last**; the `CRF_GDSTK_CACHE`
  override; the archives verified against the recipe before unpacking, and a mismatch naming both hashes. A gdstk
  build is expected to take well under a minute.
- **c. `dotnet build` never builds gdstk.** `src/Ui/CircuitRF.Ui.csproj` gains a step beside the geometry kernel's that
  runs `ensure-built` (warn once and succeed when the cache is empty), and `-p:CrfSkipGdstkWorker=true` skips it.
- **d. Staging.** `gdstk-kernel/gdstk-worker[.exe]` beside the assemblies (`Contents/MacOS/gdstk-kernel/` in the
  `.app`). The folder name contains no dot (codesign). The executable is signed individually on macOS before the bundle
  is sealed.
- **e. Packaging.** All three scripts (`packaging/windows/build-windows.ps1`, `packaging/macos/build-macos.sh`,
  `packaging/linux/build-linux.sh`) run `build.sh --strict` (or `build.cmd`) per shipping RID and fail when one lacks
  the worker, unless `CRF_ALLOW_NO_GDSTK=1`. **Every `.ps1` change stays pure ASCII.** `tools/CliSmoke` additionally
  checks that `gdstk-kernel/gdstk-worker --version` answers. Extend `tests/Ui.Tests/PackagingScriptTests.cs` the way
  it holds the geometry kernel (staging, the escape variable, the smoke, ASCII).
- **f. Notices.** `THIRD-PARTY-NOTICES.md` §4 gains gdstk, its Clipper, qhull and zlib, with Q2's text. `licenses/`
  gains qhull's `COPYING.txt` (as `Qhull.txt`) and zlib's licence (`Zlib.txt`), so the existing `licenses/*.txt` copy
  step carries them. Boost's text is added only if it is not already there for Clipper2.
- **g. `--version` and `hello`** report the worker's own version, gdstk's version as compiled, qhull's, zlib's, the
  protocol version and the RID it was compiled for.

**Gate:** `PackagingScriptTests` (the new cases), `ensure-built` staging on the agent's RIDs, and CliSmoke against a
local publish tree on macOS.

## 5. The protocol (G1 builds it, G2 consumes it)

**Frames exactly as the geometry worker's** (`[uint32 jsonLen][uint32 binLen][JSON][bytes]`, little-endian, `blobs`
declared in order with `type` `f64`/`u32`/`bytes`). **Coordinates travel as `f64` holding exact integer database
units**, and the managed side asserts each is integral. That needs no new blob type, and Q6 proves it is exact.
Diagnostics go on stderr. A refusal is an ordinary reply (`{"ok":false,"code":…,"detail":…}`) and the worker keeps
running.

**Transfer is per cell**, so no single frame holds a whole library (Q7) and progress and cancellation have a natural
step:

| request | in | out |
|---|---|---|
| `hello` | `protocol` | `worker`, `gdstk`, `qhull`, `zlib`, `protocol`, `rid` |
| `open` | `path`, `format` (`oas`, `gds`) | `handle`, `unit_m`, `precision_m` (the file's own), `cells[]` (name, counts by kind, `top`: referenced by no other cell), `layer_names[]` (OASIS `LAYERNAME`: name, layer and datatype intervals, geometry or text), `notes[]` (what was read but has no circuitRF form: properties, `XNAME`/`XELEMENT`, counted) |
| `cell` | `handle`, `name` | `polygons` (layer, datatype, vertex counts; blob `xy` f64), `paths` (layer, datatype, width, end, extensions; blob `xy`), `labels` (layer, texttype, text, origin, anchor, rotation, magnification, mirror), `refs` (cell, origin, rotation, magnification, mirror, `repetition`: kind and parameters as gdstk holds them), `shape_repetitions` (kept or expanded per R-oas-4c), `notes[]` |
| `close` | `handle` | `ok` |
| `begin-write` | `unit_m`, `precision_m`, `format`, the format's options (§7) | `handle` |
| `add-cell` | `handle`, a cell in the `cell` reply's shape | `ok` |
| `finish-write` | `handle`, `path` | `bytes`, `cells`, `notes[]`; the file is written to a temporary name and renamed, **so a failed write leaves no partial file** |
| `shutdown` | — | `ok`, then exit 0 |
| `selftest` | — | a fixed round trip, for CliSmoke |

The managed client is `src/Design/Layout/Interchange/Gdstk/` (`GdstkWorker`, `GdstkSession`, `GdstkFrame`). **Reuse
`GeometryKernelFrame`'s codec**: move it to a shared place if its namespace makes that awkward, rather than copying it.
Discovery mirrors `GeometryKernelCache` (beside the assemblies, then the build-tree pointer `dotnet run` uses).
Cancellation kills the process. The timeout comes from Q3. A worker that dies mid-read is reported as *the gdstk worker
stopped while reading `<file>`* (a coded diagnostic, not a new allowlist line), and nothing is created.

## 6. G2 — one import path and one export path for both GDSII readers (`R-oas-2`)

**a. Import.** Split `GdsiiImport.Import` at the point where `rawStructures` exist. Everything after it (rescaling,
`GdsiiLayerReconciliation.BuildSourceLayers`, `LayoutLayerMapping.Propose`, the mapping callback, structure naming,
the two cell-folder passes, pin inference, `TopLevelCellDirs`) becomes one format-agnostic function, for example
`StreamLayoutImport.Import(IReadOnlyList<InterchangeStructure>, sourceDbuPerMicron, IReadOnlyList<string> diagnostics,
string formatName, …)`, taking the same arguments `GdsiiImport.Import` takes now. `GdsiiImport.Import` becomes
`GdsiiReader` + that function. The gdstk route becomes `GdstkImport.Import(path, format, …)`: the worker + the gdstk →
`InterchangeStructure` mapping (`GdstkMapping`) + **the same** function. Messages that name the format take
`formatName`, so an OASIS import never says "GDSII".

**b. Export.** `GdsiiExport.CollectHierarchy` is already format-agnostic; keep it. The per-shape lowering that
`GdsiiWriter` performs (curve flattening, hole keyholing, bitmaps skipped, via pads from `LandingLayer`, labels) is
what the gdstk route must match, so **factor that lowering out of `GdsiiWriter`** into a function that produces
stream-ready elements (polygons, paths, labels, references in database units). `GdsiiWriter` then serialises those
elements, and `GdstkExport` sends the same elements to the worker. Each route's `ExportPlan` counts are therefore the
same numbers, by construction. **This refactor must leave native GDSII export byte-identical**: before touching the
code, write every `LayoutGdsiiExportTests` and `LayoutGdsiiRoundTripTests` layout to bytes with the current code, and
afterwards assert the same bytes (a scratch comparison, kept as a test only if it is cheap).

**c. Mapping rules (gdstk ↔ circuitRF)**, fixed by Q4/Q5 and written as a table in `GdstkMapping`'s doc comment:

- polygon ↔ `PolygonShape` (a rectangle that comes back as a 4-vertex axis-aligned polygon becomes `RectShape`, as the
  native reader decides it; match whatever `GdsiiReader` does)
- path ↔ `PathShape` when its end is flush, round or half-width; any other extension is converted to a polygon and
  counted
- label ↔ `LabelShape` (text type → the layer's text purpose, exactly as the native reader maps `TEXTTYPE`)
- reference ↔ `LayoutInstance`; a regular orthogonal repetition ↔ `Rows`/`Cols`/`PitchX`/`PitchY`; any other
  repetition is expanded into single instances and counted
- a circle that arrives as a polygon stays a polygon, with a note (`CircleShape` is not reconstructed from vertices)
- properties, `XNAME`, `XELEMENT`: not imported, counted in one message

**Gate:** the existing GDSII tests unchanged and green (`--filter` on the `Gdsii*`, `LayoutGdsii*`,
`ViaInterchangeExportTests`, `WBondInterchangeTests` and `ConvertCliVerbTests` classes), plus the byte-identity check.

## 7. G3 — GDSII (gdstk): menus, dialogs and the comparison tests (`R-oas-3`)

**a. Menus**, in **all three** menu locations (macOS native menu, window menu, torn-off File menu):

```
File ▸ Import ▸  GDSII…            (circuitRF's own, unchanged)
                 GDSII (gdstk)…
                 OASIS (gdstk)…     (G4; added disabled now, enabled in G4)
File ▸ Export ▸  GDSII              (unchanged)
                 GDSII (gdstk)
                 OASIS (gdstk)      (G4)
```

The layout editor's toolbar export button stays native GDSII only. The (gdstk) entries are disabled when the worker is
absent, with the tooltip *The gdstk worker is not installed in this build.* Import uses the existing layer-mapping
dialog unchanged. Export uses `GdsiiExportFidelityDialog`, **generalised by a format name, not copied**. Its title and
messages read "Export GDSII (gdstk)" / "Export OASIS (gdstk)", and its counts come from the shared lowering (R-oas-2b).
DRC- and LVS-before-export (Settings) apply to the gdstk exports exactly as to the native ones.

**b. Commands.** `ImportGdsiiLibraryAsync` and `ExportGdsii` take the engine as a parameter, rather than each gaining a
copy. The view model holds no import or export logic of its own (the `Authoring.cs` rule: the GUI and the CLI call one
function in `src/Design`).

**c. The comparison tests (the owner's request).** `tests/Ui.Tests/Interchange/GdstkGdsiiComparisonTests.cs`, one test
per claim, each skipping with a reason when the worker is absent:

1. **Same file, two readers.** For each §8a `.gds` fixture, `GdsiiImport` and `GdstkImport` produce semantically equal
   cells (§8b). Use a `[Theory]` over the fixtures, trimmed to the simple ones.
2. **Our writer → their reader.** A layout written by native `GdsiiExport` and imported through gdstk equals the
   original layout.
3. **Their writer → our reader.** The same layout exported through `GdstkExport` (GDSII) and imported through native
   `GdsiiImport` equals the original.
4. **The two writers agree on content.** The same layout exported both ways and read back by the **same** reader gives
   equal cells. Assert *not* byte-identical only if that is true and worth knowing; never assert byte identity.
5. **The fidelity plans agree.** `ExportPlan` counts are equal for both engines on a layout with a curve, a hole, a
   bitmap and a via.

**Simple cases expected to agree** (the §8a corpus): one rectangle; a 7-vertex polygon; a path with each of flush,
round and half-width ends; a label at 0°/90° with mirror and magnification; an SREF with 90° rotation, mirror and
magnification 2; a 3 × 2 AREF; a two-level hierarchy with a cell referenced twice; two layers × two datatypes; 1 nm and
0.25 nm database units; an empty cell.

**Expected to differ in bytes, not content:** `BGNLIB`/`BGNSTR` timestamps, `LIBNAME`, record order inside an
element, optional records each writer emits or omits (`PATHTYPE 0`, `STRANS` with no flags, `ELFLAGS`), cell order,
and the polygon's starting vertex. §8b's equality ignores exactly these and nothing more.

**d. CLI.** `convert --engine native|gdstk` applies to a `gdsii` source or target (default `native`). `--engine gdstk`
with neither side GDSII is a refusal saying the flag only applies to GDSII. The `ConvertCliVerbTests` all-pairs test
gains `--engine gdstk` variants of the gdsii rows.

## 8. The comparison corpus and its equality

**a. Corpus.** The files of G0's deliverables, plus a C# builder (`GdstkCorpus` in the test project) that constructs
each simple case as `InterchangeStructure`s, so the layout-level tests (§7c 2–5) need no file at all.

**b. Semantic equality** (`InterchangeEquality`, test-only): the same set of cell names; per cell, the same multiset of

- polygons by (layer, datatype, vertex cycle normalised: start at the lexicographically smallest vertex, orientation
  counter-clockwise; a 4-vertex axis-aligned polygon equals a `RectShape` with the same extent),
- paths by (layer, datatype, spine, width, end),
- labels by (layer, text, origin, rotation, mirror, magnification),
- instances by (cell, origin, rotation, mirror, magnification, rows, cols, pitch);

and the same database unit. Coordinates compare **exactly** as integers; magnification and rotation compare within
1e-12. The assertion message lists **every** difference, not the first, so one failing run says everything.

## 9. G4 — OASIS (gdstk) import and export (`R-oas-4`)

- **a. Import** through `GdstkImport` with `format: oas`. The new layer-mapping proposal reads OASIS `LAYERNAME`
  records as source layer names, matched against the destination technology the way DXF layer names are matched
  (`DxfLayerReconciliation`'s precedent), before falling back to layer/datatype numbers. Every Q5 "go with
  conditions" item is a message stating its count.
- **b. Export** through `GdstkExport` with `format: oas`. Defaults: `compression_level` 6, `detect_rectangles` and
  `detect_trapezoids` on, CRC32 validation, standard properties off, `circle_tolerance` 0 (circles are flattened by the
  shared lowering as for GDSII; whether to emit OASIS `CIRCLE` for an unflattened `CircleShape` is a question for the
  owner after G0, not a default). The export dialog gains a small *OASIS options* section with these four, remembered
  per user.
- **c. Expansion limit.** A shape repetition is expanded on import. Above the limit Q7 sets, the import is **refused**,
  naming the count it would create and the limit, never truncated. An instance repetition circuitRF cannot hold as an
  array is expanded under the same limit.
- **d. Precision.** An OASIS file's grid finer than the destination resolution uses the existing
  `preferSourceResolution` behaviour and its rounding warning, unchanged.

**Tests** (`tests/Ui.Tests/Interchange/GdstkOasisTests.cs`, skipping when the worker is absent):
1. Layout → OASIS → import equals the layout (§8b), over the corpus.
2. GDSII (native) → `convert` → OASIS → `convert` → GDSII (native) is content-equal to the first GDSII.
3. Each committed gdstk-written `.oas` fixture imports to its stated content (one case per Q5 feature that G4 supports).
4. A repetition over the limit is refused, and nothing is created (using a generated file under the limit's counter,
   not a timed one).
5. A truncated `.oas` reports the worker's refusal or the worker-stopped diagnostic, and creates nothing.
6. `LAYERNAME` names land on matching technology layers.

## 10. G5 — headless, documentation and finish (`R-oas-5`)

- **a. `convert`.** `Fmt.Oasis` (`oas`, `oasis`); the extension `.oas`; content classification by the `%SEMI-OASIS\r\n`
  signature for an unknown extension; the OASIS options as flags (`--oas-compression 0-9`,
  `--oas-validation none|crc32|checksum32`, `--oas-standard-properties`). The all-pairs test covers OASIS as source and
  target against every other format: 30 pairs where there were 24, plus the `--engine gdstk` gdsii rows. **A byte gate
  like the existing GDSII one:** `convert`'s `.oas` equals in-process `GdstkExport` output byte for byte, provided Q8
  showed gdstk's OASIS writer is deterministic (no timestamp). If it is not, exclude exactly what varies and say so.
- **b. `check` and `explain`.** `DocumentKinds` names an `.oas` file as OASIS rather than unreadable. `check` on one
  reads its header through the worker and reports cells and layers, or says the worker is absent.
- **c. Wherever `convert`'s formats are listed** (the usage text, `docs/design/cli.md`, `docs/user/src/reference/cli.md`,
  the MCP server's import/convert tool description, `circuitrf reference`), add `oasis` and `--engine`. Grep for the
  existing format list (`clay | gdsii | dxf | gerber | board`) to find every copy.
- **d. User documentation.** `docs/user/src/reference/layout-editor.md` gains an "OASIS and GDSII through gdstk" section:
  what each menu entry does, why there are two GDSII routes and when to try the other one, what is not imported
  (properties, `XNAME`), the expansion limit, and the OASIS export options. Do not regenerate `docs/user` (the owner
  does that at the end of a series). Do not describe change history.
- **e. Records.** `src/Design/RESOLVED.md`: the mapping table, the expansion limit and its measurement, each departure
  from the geometry worker, and every disagreement Q4 found with how it was classified. `CLAUDE.md` is **not** edited.
  `convert`'s line there is the owner's to update.

---

## 11. Scope

- **In:** OASIS import and export; GDSII import and export through gdstk as an opt-in second route; `convert`, `check`
  and `explain` support; Windows, macOS and Linux on every shipping RID.
- **Out:** gdstk's booleans, offsets, fillets or any geometry operation; any change to circuitRF's own geometry or to
  Clipper2's use; any behaviour change to the native GDSII reader or writer (the G2 refactor is byte-identical); OASIS
  properties as editable data; reconstructing circles or curves from imported vertices; posting anything upstream.
- **Ask the owner before:** patching gdstk in any way; shipping a RID that could not be smoke-tested; any change to the
  frame codec beyond moving it; emitting OASIS `CIRCLE`; committing any file that came from a tool other than gdstk or
  circuitRF.

## 12. Phase gates, in order

| Phase | Done when |
|---|---|
| G0 | Findings written, go/no-go stated, the owner has read it. **On hold, stop here.** |
| G1 | The worker builds, stages, signs and smokes on the agent's RIDs; `PackagingScriptTests` green; each Windows RID cross-built on macOS (x64 and x86 run under Wine), the owner's real-Windows session repeated on the G1 worker, and each Linux RID confirmed |
| G2 | The existing GDSII test classes green, native export byte-identical, `GdstkImport`/`GdstkExport` through the shared path |
| G3 | The menus in all three places; `GdstkGdsiiComparisonTests` green (or each failure classified per §2 and accepted by the owner) |
| G4 | `GdstkOasisTests` green; the Q5 conditions reported as messages |
| G5 | `convert` all-pairs and the byte gate green; documentation sources updated; `RESOLVED.md` written |

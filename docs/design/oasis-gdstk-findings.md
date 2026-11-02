# circuitRF — OASIS and a second GDSII route through gdstk, G0 spike: findings

**Status:** G0 is complete. The owner's real-Windows session passed on win-arm64 and win-x64 (§ Q1).
**The go/no-go is GO WITH CONDITIONS** (§ Go/no-go). Nothing found is a hold. · **Date:** 2026-10-06 ·
**Brief:** [`brief-oasis-gdstk.md`](../briefs/brief-oasis-gdstk.md) §3 (R-oas-0) ·
**Harness:** [`tools/gdstk-worker/spike/`](../../tools/gdstk-worker/spike/README.md) (raw runs in its git-ignored
`runs/`) · **Corpus:** [`testdata/interchange/gdstk/`](../../testdata/interchange/gdstk/README.md)

"gdstk" means gdstk 1.0.1 built by the recipe below, inside the spike's worker (`gdstk_worker.cpp`), spoken to over
§5's frames. "Ours" means circuitRF's `GdsiiReader`/`GdsiiWriter`. DBU means database units. Every number is from
the spike's own runs on an M-series Mac (osx-arm64, Release) unless the RID is named. **Every number marked Wine
comes from Wine 8.0 on amd64 Debian 12 under Docker Desktop's emulation. Those numbers are a first pass, never the
confirmation.**

---

## 0. The answers in one screen

| # | Question | Answer |
|---|---|---|
| Q1 | Build | **All 7 RIDs build statically with no patch to gdstk.** gdstk is added as a CMake sub-project, and its own `find_package` finds our static zlib and qhull. Full builds take 9–23 s; the workers are 0.99–2.58 MB. Each depends only on system libraries. **The builds are reproducible**: a from-scratch rebuild of both Linux RIDs in a fresh container, and a rebuild of osx-arm64, were byte-identical to the earlier binaries. **On real Windows** (the owner's session, Windows 11 ARM64): win-arm64 natively and win-x64 under Windows' x64 emulation both pass `--version` and `selftest` and reproduce macOS's replies on 276/279 and 275/279 jobs, every difference explained. |
| Q2 | Licences | gdstk is Boost, its bundled **Clipper 1 v6.4.2** is Boost, qhull 2020.2 has its own licence (`COPYING.txt` must ship), and zlib 1.3.2 is zlib. **No GPL. No file contradicts its project's licence.** Two items for the owner: static libstdc++/libgcc on Linux (GPLv3 with the GCC Runtime Library Exception), and static libc++/libunwind on Windows (Apache-2.0 WITH LLVM-exception). Both are permitted. |
| Q3 | Failure behaviour | With the worker's guards on, **every** damaged CBLOCK+CRC32 OASIS file is refused (105/105), and every truncated file of every kind is refused (150/150). Silent wrong results remain where the format offers no check: GDSII (19 of 50 flips) and OASIS without a signature (16 of 50). **Crashes exist** (SIGSEGV, up to 12 of 50 unguarded truncations). **One hang**: a runaway allocation past 1.8 GB that only a kill bounds, and it is reachable only with the guards off. With the guards off, a corrupt CBLOCK's outcome depends on the heap, not only on the bytes (under Wine, one file crashes or reads depending on its path). Paths with a space, é, Ω, CJK or 352 characters work on macOS and Linux. **On Windows**, non-ASCII paths work through the UTF-8 route, and a path past 260 characters works only with the `\\?\` prefix. G1 moved adding that prefix into the worker itself (`tools/gdstk-worker/RESOLVED.md`). |
| Q4 | GDSII agreement | **All 11 simple cases agree** in every direction and with both readers. The one exception is a label's mirror and magnification, which circuitRF's model cannot hold: our writer omits them, and our reader drops them from gdstk's file. That is deterministic, and gdstk keeps both. Six differences outside the simple cases are **ours** (§ Q4). None is gdstk's. |
| Q5 | OASIS coverage | Independent hand-encoded fixtures (repetition types 0–11, point lists 0–5, modal variables, CBLOCK, LAYERNAME, strict tables, 0.25 nm grid): **10/10 read exactly.** Lossy but detectable: round path ends and odd widths on write, label transforms (OASIS has none), CIRCLE becomes a polygon, and properties, XNAME and XELEMENT are not carried. One gdstk defect we never trigger: **gdstk#247**, negative explicit repetition offsets. |
| Q6 | Exactness | **Exact.** 8 cases × 1,000,012 values (±2³¹ and 10⁶ random; read and write; GDSII and OASIS; 1 nm and 0.25 nm) give **0 mismatches** on osx-arm64, linux-arm64, linux-x64, and win-x64 and win-x86 (Wine). |
| Q7 | Scale | 10⁶ polygons in 101 cells: OASIS 8.0 MB reads in 0.13 s at 215 MB peak; GDSII 80 MB in 0.22 s at 230 MB. A 1000 × 1000 shape repetition is 316 bytes, reads in under 1 ms, and costs 45–66 ms and 198 MB to expand. **Proposed R-oas-4c limit: 10⁶ expanded shapes per import** (the owner decides). |
| Q8 | Write side | **Deterministic**: two writes give identical bytes, and the output is byte-identical across osx-arm64 and linux-arm64 (all 91 corpus files) and linux-x64 and win-x64/x86 (90/91, the exception being the gdstk#247 file). `oas_validate` accepts every file. 80 MB of GDSII becomes 8.0 MB of OASIS, and an 8000-vertex polygon goes from 64 KB to 489 B. |

---

## 1. What was done, and where

The spike is `tools/gdstk-worker/spike/` (its README names every script and its question). It holds:

- `build.sh`, `CMakeLists.txt` and `recipe.env`, which fetch, verify and build zlib, qhull and gdstk into
  `~/.circuitRF-build/gdstk/1.0.1/<rid>/` and link the spike's worker and probe;
- `gdstk_worker.cpp`, the smallest form of §5's protocol (`hello`, `open`, `cell`, `close`, `begin-write`,
  `add-cell`, `finish-write`, `shutdown`, `selftest`), **which G1 grows**;
- `GdsiiDump`, which drives our own `GdsiiReader`/`GdsiiWriter` into the same canonical JSON as the worker;
- a Python harness (standard library only) with one script per question;
- Docker Desktop images for the Linux builds and the Wine first pass, and the owner's Windows session bundle.

No product code, UI or file format was changed. Nothing was committed.

## 2. The machines

| | |
|---|---|
| Mac | Apple silicon, macOS 27, Release builds, Apple clang. **No Rosetta**, so osx-x64 is build-only. |
| Linux | Docker Desktop (`--context desktop-linux`): `crf-gdstk-linux` = Debian 12 arm64, g++ 12 plus the x86-64 cross g++, CMake 3.31.8. linux-arm64 runs natively. linux-x64 runs in `crf-gdstk-wine` (amd64 Debian 12) under emulation. |
| Windows | Cross-built on the Mac with **llvm-mingw 20260922** (UCRT, macOS universal; archive SHA-256 `52e5f5a7b131021d0c39a37a38fa380a1da7885cd04bd61afd0cd4ecfb8bc1f3`). It is run under **Wine 8.0** (wine64 + wine32) in `crf-gdstk-wine`. Wine cannot run ARM64 Windows programs, so win-arm64 is build-only. |

---

## Q1 — Build

### The recipe, as data

`tools/gdstk-worker/spike/recipe.env` (G1 moves it to `tools/gdstk-worker/`). **Upstream publishes no SHA-256 for
any of the three archives** (zlib publishes a detached `.asc`). Each value below was computed by the spike from the
archive it downloaded and built.

| Source | Version | URL | SHA-256 |
|---|---|---|---|
| gdstk | 1.0.1 | `https://github.com/heitzmann/gdstk/archive/refs/tags/v1.0.1.tar.gz` | `7819120177050db4ef8bd3394158c7cecc4555640d50f634f957eafdcbcd7984` |
| qhull | 2020.2 (qhull_r 8.0.2, which gdstk's `find_package(Qhull 8)` asks for) | `https://github.com/qhull/qhull/archive/refs/tags/2020.2.tar.gz` | `59356b229b768e6e2b09a701448bfa222c37b797a84f87f864f97462d8dbc7c5` |
| zlib | 1.3.2 | `https://github.com/madler/zlib/releases/download/v1.3.2/zlib-1.3.2.tar.gz` | `bb329a0a2cd0274d05519d61c667c062e06990d72e125ee2dfa8de64f0119d16` |

CMake options: zlib `-DZLIB_BUILD_SHARED=OFF -DZLIB_BUILD_STATIC=ON -DZLIB_BUILD_TESTING=OFF`. qhull
`-DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON -DCMAKE_POLICY_VERSION_MINIMUM=3.5`; qhull declares
`cmake_minimum_required(VERSION 3.0)`, which CMake 4 refuses unless told, so this option is a CMake setting and not a
patch. Only `qhullstatic_r` is used, by one gdstk function (`convex_hull`). macOS uses deployment target 13.0.

### The exact CMake line (the worker; zlib and qhull are built the same way into `<rid>/deps`)

```
cmake -S tools/gdstk-worker/spike -B <cache>/<rid>/spike-build -DCMAKE_BUILD_TYPE=Release
      -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DCMAKE_PREFIX_PATH=<cache>/<rid>/deps
      -DCMAKE_FIND_USE_SYSTEM_PACKAGE_REGISTRY=OFF -DCMAKE_FIND_USE_PACKAGE_REGISTRY=OFF
      -DGDSTK_SOURCE_DIR=<cache>/source/gdstk-1.0.1 -DQHULL_ROOT=<cache>/<rid>/deps -DZLIB_ROOT=<cache>/<rid>/deps
      + osx:   -DCMAKE_OSX_ARCHITECTURES=arm64|x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0
      + win/linux cross: -DCMAKE_TOOLCHAIN_FILE=<cache>/toolchain-<rid>.cmake
```

Each RID's exact lines are the `+ cmake` lines of `<cache>/<rid>/build.log`. The toolchain files live in the cache
and never in the repository. They leave `CMAKE_FIND_ROOT_PATH_MODE_LIBRARY/INCLUDE/PACKAGE` at `BOTH`, because
`ONLY` would re-root `CMAKE_PREFIX_PATH` under the sysroot and lose the static zlib and qhull. That is the same trap
`tools/geometry-worker/RESOLVED.md` records for OCCT.

**How gdstk is consumed.** It is added with `add_subdirectory(<gdstk source> gdstk EXCLUDE_FROM_ALL)`. As the
top-level project, gdstk's `CMakeLists.txt` turns its examples and CTest on with a `CACHE FORCE`, and no `-D` can
undo that. As a sub-project it builds the library alone. **gdstk's own `src/CMakeLists.txt` finds zlib and qhull
through `find_package`** (`Found ZLIB: …/deps/lib/libz.a`, `QHULL found … libqhullstatic_r.a`, manual mode). We
hand it nothing beyond `ZLIB_ROOT`/`QHULL_ROOT`/`CMAKE_PREFIX_PATH`. **No patch, no define, and no edit to any gdstk
file.** The build had 0 compiler warnings.

**Static linking (D3).** Linux links `-static-libstdc++ -static-libgcc`, with glibc dynamic. Windows (llvm-mingw)
links `-static`, so no `libc++.dll` or `libunwind.dll` sits beside the worker. macOS links the system's libc++.

### Per RID

| RID | Built on | First full build | Worker | Dependencies (proof) |
|---|---|---|---|---|
| osx-arm64 | Mac | 14 s | 990,416 B | `otool -L`: `/usr/lib/libSystem.B.dylib`, `/usr/lib/libc++.1.dylib` |
| osx-x64 | Mac (`CMAKE_OSX_ARCHITECTURES=x86_64`) | 19 s | 1,081,944 B | the same two. **Build-only**: no Rosetta here |
| linux-arm64 | container, native g++ | 10 s | 2,271,896 B | `readelf -d`: `libm.so.6`, `libc.so.6`, `ld-linux-aarch64.so.1`; GLIBC floor **2.36** (the Debian 12 build host) |
| linux-x64 | container, cross g++ | 9 s | 2,282,576 B | `libm.so.6`, `libc.so.6`, `ld-linux-x86-64.so.2`; GLIBC floor **2.36** |
| win-x64 | Mac, llvm-mingw | 17 s | 2,511,360 B | `llvm-objdump -p`: `KERNEL32.dll` + 13 `api-ms-win-crt-*` (the UCRT, part of Windows 10 and later) |
| win-x86 | Mac, llvm-mingw | 21 s | 2,576,384 B | the same |
| win-arm64 | Mac, llvm-mingw | 23 s | 2,092,544 B | the same. **Build-only**: Wine cannot run it |

An incremental rebuild takes about 4 s. **The GLIBC floor of 2.36 is the build host's.** G1 should build the Linux
RIDs on the oldest distribution circuitRF supports, or record 2.36 as the floor.

**Reproducibility.** The worker source changed after the macOS and Linux binaries were built, through a
Windows-only `#ifdef`. Before reusing any recorded number, the spike rebuilt osx-arm64 (incrementally) and both
Linux RIDs (from scratch, in a fresh container cache). **All three were byte-identical to the binaries the numbers
came from.**

### The Windows builds before real Windows (Wine)

`--version`, `selftest` and every corpus file read and written back (`platform_check.py`) were run on win-x64 and
win-x86 under Wine. **The results equal macOS's** (§ Q8, "Platform check"), and **all 1,000,012 × 8 Q6 values are
exact under Wine.**

The owner's session was then **replayed under Wine** (`replay_session.py`, which mirrors `run-session.ps1`) to learn
which recorded hashes are expected to differ off macOS:

| | win-x64 (Wine) | win-x86 (Wine) |
|---|---|---|
| `--version`, `selftest` | ok (4 GDSII and 4 OASIS elements, valid) | ok (the same) |
| recorded jobs identical to macOS | **275 / 279** | **275 / 279** (the same four) |
| malformed files answered exactly as on macOS | **526 / 530** (same class counts: read 189, refused 296, crash 44, hang 1) | **526 / 530** (read 188, refused 296, crash 45, hang 1: `rich-cblock-flip-36` crashes here) |

**Every difference is explained** (`runs/g0-final-logs/`):

- **`026-read-hand-circle`, `033-read-hand-repetitions`: last-digit floating point.** `reply_dump.py` decodes both
  replies at full precision. 51 of the 1,548 circle-vertex doubles differ in the last digit (`44288.76598936492` against
  `…491`), and so do six repetition coordinates (`350.00000000000006` against `350.0000000000001`). **0 values differ
  after rounding.** The CIRCLE's vertices come from the C library's sin/cos, and the repetition values come from the
  1 ± 1 ulp read factor (§ Q6). Rounding in the worker (§ Worker changes) makes these replies identical across
  platforms.
- **The two `081-write-q5-ref-repetitions` jobs**: the gdstk#247 file (§ Q5), where `llround` of an out-of-range
  value differs by libm.
- **The 4–5 malformed files**: unguarded CBLOCK cases whose inflate fails (§ Q3, *A failed CBLOCK inflate*). Their
  outcome is not a function of the bytes alone.

**On the owner's machine, these are expected to show as DIFFERENT too.** The bundle's `README.txt` lists them, so
anything else it reports is new information.

### The owner's real-Windows session

The bundle is `tools/gdstk-worker/spike/runs/windows-session/`, 18 MB, built by `windows_bundle.py`. It needs only
Windows PowerShell, and the script is pure ASCII. It runs, for each RID the machine can execute:

1. `--version` and `selftest` for both variants (`gdstk-worker.exe`, and `gdstk-worker-utf8.exe` with an
   `activeCodePage` UTF-8 + `longPathAware` manifest);
2. 279 recorded jobs, comparing every reply and every written file by SHA-256 with macOS's;
3. the path cases (a space, é, Ω, CJK, and a path past 260 characters, plain and with `\\?\`), read and write, for
   both variants;
4. the 530 Q3 files (525 malformed and the 5 originals), each in a fresh worker with a 10 s limit.

It writes `results\summary.txt` and `results\results.json`, plus the bytes of any reply that differs, so a difference
can be decoded afterwards (`reply_dump.py`). Its comparison logic was first proved by the Python replayer (279/279 and
530/530 against the macOS worker itself, and the counts above under Wine), then by the whole script under
PowerShell 7 against the Linux worker, and then on Windows (below).

---


**First attempt (2026-10-06, Windows 11 25H2 ARM64 in a VM; the registry's product name reads "Windows 10 Home",
build 26200; Windows PowerShell 5.1; ANSI code page 65001).** `--version` answered for both win-arm64 variants, which
is the first time any Windows build ran on real Windows. **Every framed request then timed out, and the cause was the
session script, not the worker.** Windows PowerShell 5.1 (.NET Framework) builds a child's stdin writer on
`[Console]::InputEncoding`. On a machine whose code page is UTF-8, that writer emits a byte-order mark (`EF BB BF`)
into the pipe as soon as the process starts. The worker read those three bytes as the start of its first header, saw
a JSON length of **297,778,159**, and waited for it. Reproduced on macOS by prefixing a BOM; PowerShell 7 (.NET Core)
strips the preamble and does not do this. The script now sets a preamble-free input encoding, records the stdin
preamble length in its summary, writes its summary line by line plus a transcript, catches each step, and skips a
RID whose selftest fails. Running the whole script in PowerShell 7 against the Linux worker also found that it
stored per-RID results in `$R` while every reply was `$r` (PowerShell names are case-insensitive). Those runs gave
275/279 jobs (the four known differences) and 529/530 malformed files.

Two consequences for G1:
- **The worker should refuse an implausible frame length** (for example a JSON part over 64 MB) and exit, rather than
  wait for it. A client that sends a stray byte should get an answer, not a hang.
- **The managed client is not exposed**: .NET 10's `Process` strips the stdin preamble. Any future script client on
  Windows PowerShell 5.1 must set the encoding itself, as `run-session.ps1` now does.

**This machine's code page is already UTF-8**, so the plain worker's non-ASCII path cases here behave like the
manifest variant's. A default Windows install (code page 1252) would still fail Ω and CJK with the plain worker,
and that can be inferred without a second machine. What this session proves is that the UTF-8 route works.

**Second attempt: the session passed** (the same machine; `runs/windows-results-2/`). The stdin preamble was 0
bytes. Smart App Control was off and `LongPathsEnabled` was 0.

| | win-arm64 (native) | win-x64 (x64 emulation on ARM64) |
|---|---|---|
| `--version`, `selftest`, both variants | ok (4 GDSII, 4 OASIS elements, valid) | ok |
| recorded jobs identical to macOS | **276 / 279** | **275 / 279** |
| jobs that differ | `hand-circle` (59 vertex doubles in the last digit, **0 after rounding**); the two gdstk#247 jobs | the same, plus `hand-repetitions` (repetition offsets in the last digit) — exactly Wine's four |
| non-ASCII paths (space, é, Ω, CJK), read and write, GDSII and OASIS | **all pass, both variants** | **all pass, both variants** |
| a 363–370-character path, plain | **refused, both variants** (open failed; `LongPathsEnabled` = 0, so the manifest's `longPathAware` cannot help) | the same |
| the same path with `\\?\` | **read and write pass, both variants** | the same |
| malformed files answered exactly as on macOS | **525 / 530** | **527 / 530** |
| what differs | only unguarded `rich-cblock` cases: 2 truncations and 2 flips crash (`0xC0000005`), 1 flip reads differently | 1 truncation and 1 flip crash, 1 flip reads differently |
| hangs | 1 (the CBLOCK-size runaway, killed at 10 s) | 1 (the same) |

**Every guarded case matched macOS on real Windows.** Every difference is in the unguarded CBLOCK fixture, the one
§ Q3 shows to depend on heap layout rather than on the bytes. Crashes are reported properly here, as
`0xC0000005` (access violation); under Wine they exited 0. **arm64's 276 shows its libm agrees with macOS's on
the repetition values, while x64's does not.** Rounding in the worker removes both differences (§ Worker changes).

What this settles:
- **Windows paths:** ship the **`activeCodePage` UTF-8 manifest** worker (Windows 10 1903 or later), so `fopen`
  takes the UTF-8 the protocol carries on a machine of any code page. **The managed client hands Windows paths
  with the `\\?\` prefix** (`\\?\UNC\…` for a share) whenever a path reaches 260 characters, because
  `longPathAware` is honoured only when the machine's `LongPathsEnabled` policy is on, and it was off here. Neither
  the stream route nor a temporary copy is needed.
- **win-arm64 has now run on real ARM64 Windows**, which is G1e's smoke condition for shipping it. win-x86 has run
  only under Wine; G1 confirms it.
- **win-x64 ran under Windows' own x64 emulation, not on x64 hardware.** The binary, loader, UCRT and file system
  were real Windows. The owner accepted this run for win-x64; no x64-hardware run is required.

## Q2 — Licences

| Component | Licence | Files compiled | Headers |
|---|---|---|---|
| gdstk 1.0.1 | Boost Software License 1.0 (`LICENSE`) | `src/*.cpp` | `Copyright 2020 Lucas Heitzmann Gabrielli … Boost Software License`. **One source file and its header (gdstk's e-beam data-format module) carry no header at all** and are covered by `LICENSE`, so they are not contradictory. `layername.cpp` carries the Boost header with no copyright line. |
| Clipper (gdstk's `external/clipper`) | Boost Software License 1.0 | `clipper.cpp` | **Clipper 1, v6.4.2**, `Copyright : Angus Johnson 2010-2017`. It is not Clipper2. It is compiled inside the worker only, and circuitRF's own geometry stays on Clipper2. |
| qhull 2020.2 (qhull_r 8.0.2) | Qhull licence (`COPYING.txt`) | `libqhull_r` (static) | Qhull's own header throughout. **`random_r.c`, `rboxlib_r.c` and `usermem_r.c` have no header** and are covered by `COPYING.txt`. |
| zlib 1.3.2 | zlib licence | the 15 library sources | each points to `zlib.h` (`Copyright (C) 1995-2026 Jean-loup Gailly and Mark Adler`) |

**No GPL, and no header that disagrees with its project's licence**, so there is no stop. gdstk's `README` names
qhull 2020.2 and zlib (any 1.2.x or later) as its dependencies. The spike built qhull 2020.2 and zlib 1.3.2.

**For the owner to read (both permitted, neither a stop):**
- **Linux** links libstdc++ and libgcc statically. They are GPLv3 **with the GCC Runtime Library Exception**, which
  permits distributing the combined program under any licence when it is compiled by an unmodified GCC. This is the
  route D3 chose.
- **Windows** links llvm-mingw's libc++ and libunwind statically (Apache-2.0 WITH LLVM-exception). The exception
  waives the attribution requirement for object code. `licenses/Apache-2.0-with-LLVM-exceptions.txt` is already
  shipped for the geometry worker.

### The text `THIRD-PARTY-NOTICES.md` §4 will need (G1f)

Rows for the §4 "Libraries" table:

```
| gdstk (in `gdstk-kernel/gdstk-worker`, linked statically; reads and writes GDSII and OASIS) — Copyright 2020 Lucas Heitzmann Gabrielli | Boost Software License 1.0 | https://github.com/heitzmann/gdstk |
| Clipper 6.4.2 (inside gdstk; used only by gdstk) — Copyright Angus Johnson 2010-2017 | Boost Software License 1.0 | https://sourceforge.net/projects/polyclipping/ |
| Qhull 2020.2 (linked statically into the gdstk worker) — Copyright (c) 1993-2020 C.B. Barber and The Geometry Center, University of Minnesota; licence text [`licenses/Qhull.txt`](licenses/Qhull.txt). Qhull's source is available at http://www.qhull.org | Qhull licence | http://www.qhull.org |
| zlib 1.3.2 (linked statically into the gdstk worker) — Copyright (C) 1995-2026 Jean-loup Gailly and Mark Adler; licence text [`licenses/Zlib.txt`](licenses/Zlib.txt) | zlib licence | https://zlib.net |
```

and the libc++/libunwind row gains "and the gdstk worker (linked statically)".

`licenses/` gains **`Qhull.txt`** (qhull's `COPYING.txt`, 39 lines, verbatim; mandatory, and recipients must be
told the source is online) and **`Zlib.txt`** (zlib's `LICENSE`, verbatim). **No Boost text is needed.** The Boost
licence asks nothing of a binary-only distribution, and `licenses/` does not carry it for Clipper2 either. The
brief's "add Boost's text only if not already there" therefore resolves to "not added".

---

## Q3 — API and failure behaviour

### The signatures used (gdstk 1.0.1, `include/gdstk/library.hpp`)

```cpp
Library   read_gds(const char* filename, double unit = 0, double tolerance = 0,
                   const Set<Tag>* shape_tags = nullptr, ErrorCode* error_code = nullptr);
Library   read_oas(const char* filename, double unit = 0, double tolerance = 0, ErrorCode* error_code = nullptr);
ErrorCode Library::write_gds(const char* filename, uint64_t max_points, tm* timestamp) const;
ErrorCode Library::write_oas(const char* filename, double circle_tolerance, uint8_t deflate_level,
                             uint16_t config_flags);
ErrorCode gds_units(const char* filename, double& unit, double& precision);
ErrorCode oas_precision(const char* filename, double& precision);           // OASIS unit is always 1e-6
bool      oas_validate(const char* filename, uint32_t* signature = nullptr, ErrorCode* error_code = nullptr);
extern FILE* error_logger;  void set_error_logger(FILE* log);               // utils.hpp
```

**How each reports an error.** `ErrorCode` comes in two classes: warnings (`BooleanError`, `EmptyPath`,
`IntersectionNotFound`, `MissingReference`, `UnsupportedRecord`, `UnofficialSpecification`, `InvalidRepetition`,
`Overflow`) and errors (`ChecksumError`, `OutputFileOpenError`, `InputFileOpenError`, `InputFileError`, `FileError`,
`InvalidFile`, `InsufficientMemory`, `ZlibError`). Text goes to `error_logger` (stderr by default). The readers
return a `Library` in every case. Three traps decide the worker's design:

1. **`read_oas` with an error pointer stops at the first `XNAME`/`XELEMENT`/`XGEOMETRY`.** Its loop runs only while
   the code is `NoError`, and `UnsupportedRecord` is a *warning*. It returns 1 of 2 polygons with no failure (probe
   `errptr`). **The worker passes no pointer** and checks that the read reached `END`: `read_oas` names the library
   only at `END`.
2. **`read_oas` never checks the CRC32/checksum32** that `END` carries. `oas_validate` does, and it answers *true*
   for a file that carries no signature.
3. **`oas_precision` faults on a file shorter than its `START` record**, so the guards must run first.

### Malformed input (`q3_fuzz.py`)

There are three fixtures, all the Q4 simple cells in one library: `rich.gds` (1,876 B), `rich-cblock.oas`
(768 B, CBLOCK level 6, CRC32) and `rich-plain.oas` (722 B, no CBLOCK, no signature). Each gets 50 truncations,
50 single-byte flips and 5 random files, every one in a fresh worker that reads the whole library with a 10 s limit.
"Guarded" is the worker as G1 must ship it: an `END`-record structural check over the last 256 bytes, then
`oas_validate`. Final worker, osx-arm64 (`runs/q3-osx-arm64/q3-summary.json`):

| Fixture | Truncate (50) | Flip (50) | Random (5) |
|---|---|---|---|
| `rich.gds` | refused 50 | equal 17 · **wrong-silent 19** · wrong-messaged 6 · refused 8 | refused 5 |
| `rich-cblock.oas`, guarded | refused 50 | **refused 50** | refused 5 |
| `rich-cblock.oas`, unguarded | equal 24 · refused 14 · **crash 12** | equal 13 · wrong-silent 13 · wrong-messaged 8 · refused 6 · **crash 9** · **hang 1** | refused 5 |
| `rich-plain.oas`, guarded | refused 50 | equal 10 · **wrong-silent 16** · wrong-messaged 6 · refused 12 · **crash 6** | refused 5 |
| `rich-plain.oas`, unguarded | equal 18 · refused 21 · **crash 11** | equal 12 · wrong-silent 16 · wrong-messaged 6 · refused 10 · crash 6 | refused 5 |

The other RIDs, with the same 530 files and the final worker:

| RID | Counts against osx-arm64 |
|---|---|
| linux-arm64 | **identical in every cell** |
| linux-x64 | **identical in every cell** |
| win-x64 (Wine) | identical, except `rich-cblock.oas` unguarded: truncate crash 13 / refused 13, and flip crash 11 / wrong-messaged 6 |
| win-x86 (Wine) | identical, except `rich-cblock.oas` unguarded: flip crash 10 / wrong-messaged 7 |

**Every Wine difference is in the unguarded CBLOCK fixture, and they are not stable from run to run.** The same three
files (`truncate-46`, `flip-11`, `flip-33`) read under Wine win-x64 **crash 3/3 from one directory, and are
refused or read 3/3 from another**. The bytes are identical (checked by SHA-256), and only the path differs. On
macOS the path makes no difference (`runs/g0-final-logs/pathdep-*.log`). gdstk's behaviour on a corrupt CBLOCK
therefore depends on the process's heap, not only on the file: it reads memory it never wrote. **With the guards
on, the CRC refuses all of these on every RID (105/105).** Under Wine a crash reports exit status 0, not a signal.

What it means:
- **A silent wrong result is possible wherever the format has no check.** GDSII has no checksum, so a flipped byte
  inside a coordinate is a valid, different file, and no reader can know. OASIS without a signature behaves the
  same way. With CBLOCK + CRC32, which is what circuitRF's own OASIS export writes (§ G4 defaults), **nothing damaged
  got through**.
- **Every truncation is refused once guarded.** Unguarded, gdstk segfaults on 11–12 of 50 truncations.
- **Crashes are SIGSEGV.** D1 isolates them, and the client reports *the gdstk worker stopped while reading …*.
  6 flips of the unsigned OASIS file crash even when guarded, because the guards cannot see them.
- **The one hang** is a flip in a CBLOCK's size field (no validation): gdstk allocates without bound, at 100 % CPU
  with RSS past 1.8 GB, until it is killed. With the guards on, the CRC refuses that file before `read_oas` runs. A
  CBLOCK file *without* a signature has no such guard, so **the client's timeout is what bounds it**, and the client
  should also watch memory (§ Recommendations for G1).
- **A failed CBLOCK inflate is reported and then parsed anyway.** The Wine fuzz differences are flips that make
  gdstk log `[GDSTK] Unable to decompress CBLOCK.` and carry on over a buffer it never filled. The result is
  repeatable for one file at one path on one platform, but it changes with the platform and, under Wine, with the
  file's path (read on one, crash on another, from the same bytes). **G1 must refuse a read whose log contains that
  message.** On macOS these cases are *wrong-messaged* rather than silent.

### File paths (`q3_paths.py`)

A space, `é`, `Ω`, `回路`, and a 352-character path (5 × 50-character directories), each read and written as both
GDSII and OASIS: **all pass on osx-arm64 and linux-arm64.** gdstk takes `const char*` and calls `fopen`, which takes
UTF-8 on both systems. **On Windows** (the owner's session, § Q1), Windows `fopen` reads the ANSI code page, not
UTF-8. The route chosen is the `activeCodePage` UTF-8 manifest, plus the `\\?\` prefix from the client for long
paths. Both variants passed every non-ASCII case on a machine whose code page is already UTF-8. A plain path past 260
characters failed with both variants, and the prefixed one passed with both. No temporary copy is needed.

---

## Q4 — GDSII agreement

Each fixture is written by **both** writers (ours through `GdsiiDump`, gdstk's `write_gds` through the worker), and
each file is read by **both** readers. Every reading is compared with the fixture under §8b
(`runs/q4/q4-osx-arm64.jsonl`; the files are in the corpus).

| Fixture (§7c simple cases) | ours → ours | ours → gdstk | gdstk → ours | gdstk → gdstk | readers agree |
|---|---|---|---|---|---|
| rectangle | equal | equal | equal | equal | yes |
| polygon7 | equal | equal | equal | equal | yes |
| path-ends (flush, round, half-width) | equal | equal | equal | equal | yes |
| labels (0°, 90°, mirror, magnification 2) | mirror and mag lost † | mirror and mag lost † | mirror and mag lost † | equal | on our file yes; on gdstk's file no † |
| sref-transform (90°, mirror, mag 2) | equal | equal | equal | equal | yes |
| aref-3x2 | equal | equal | equal | equal | yes |
| hierarchy (cell referenced twice) | equal | equal | equal | equal | yes |
| layers-datatypes (2 × 2) | equal | equal | equal | equal | yes |
| dbu-1nm | equal | equal | equal | equal | yes |
| dbu-0p25nm | equal | equal | equal | equal | yes |
| empty-cell | equal | equal | equal | equal | yes |

† Our model holds neither a label's mirror nor its magnification, so our writer writes neither, and both readers
agree on our file. Reading gdstk's file, which carries both, our reader drops the mirror with a diagnostic
(`reflection flag ignored`) and the magnification **silently**. **This is deterministic and circuitRF's, not
gdstk's**: the gdstk route will deliver both, and the shared import (G2) drops them the same way.

**Beyond the simple cases (probes)**, which agree: an AREF with a mirror, a label at 45°, an SREF at 30°, an
8000-vertex polygon, a 1-DBU path segment, and an odd path width (3). Paths with extensions equal to half the width
come back as half-width (the same shape).

### Findings, all ours — for the owner, as separate items (not fixed here, per the brief's §2)

1. **`GdsiiReader` collapses a legal rotated AREF.** gdstk writes a 90° 3 × 2 array as `COLROW (2,3)`,
   `XY (0,0  0,3000  6000,0)`, so the column vector points along y. That is legal, because the spec defines the
   lattice by the points. Ours places **all 6 instances at the origin** and says "approximated".
2. **Layers and datatypes above 32767 wrap negative.** GDSII stores them as unsigned 16-bit in practice. gdstk reads
   40000; ours reads −25536. Writing it back, gdstk turns the wrapped value into layer 0, so **G2 must refuse a
   negative layer** rather than pass one to the worker.
3. **GDSII `BOX` elements are dropped silently** (`box-record.gds`). gdstk reads a BOX as a polygon.
4. **`GdsiiWriter`'s `UNITS` record writes the user unit in metres (1e-6) as its first real.** The spec wants the
   database unit in user units (0.001). Geometry is unaffected, because both readers use the second real, but gdstk
   derives a user unit of 1e-9 / 1e-6 = 1 mm from our files.
5. **`PATHTYPE 4` custom extensions are approximated** (known, and diagnosed). Our writer writes `[20, 70]` as
   `[50, 50]`, and our reader turns gdstk's `[20, 70]` into `[50, 50]`, because our model extends by width/2 only.
   gdstk's own round trip keeps `[20, 70]`.
6. **A label's text type other than 0 or 1 is lost** (texttype 5 becomes 0), because our model holds port or not.

**Fixed afterwards** by `docs/briefs/brief-gdsii-native-fixes.md` (2026-10-06): findings 1, 2, 3, 4 and 6, and
the silent magnification in †, each tested against the gdstk file named above; finding 5 was outside that brief and
stands. The numbers in this section are what the spike measured before the fix. Detail: `src/Design/RESOLVED.md`.

None of these is gdstk's. On gdstk's side, the rotated-AREF convention is legal, and it agrees with the fixture
everywhere else.

---

## Q5 — OASIS feature coverage

### The independent half: hand-encoded files (`oasis_hand.py`, `q5_hand.py`)

These were written byte by byte by circuitRF's own encoder, beside a statement of the content an ideal reader
produces. **All 10 are EQUAL**:

| Fixture | Exercises |
|---|---|
| `hand-repetitions` | repetition types 0–11 on placements, including 4–11, which gdstk's writer never emits |
| `hand-shape-repetitions` | repetitions on a RECTANGLE, POLYGON and TEXT |
| `hand-paths` | PATH with flush, half-width and explicit extensions, and every point-list type 0–5 |
| `hand-placements` | PLACEMENT and PLACEMENT_TRANSFORM: magnification, 30°, flip |
| `hand-modal` | implicit modal variables (layer, datatype, width, x/y), XYRELATIVE |
| `hand-names` | CELLNAME/TEXTSTRING by reference number, strict tables, forward references |
| `hand-layernames` | LAYERNAME for geometry and text, every interval type |
| `hand-cblock` | CBLOCK (deflate) around cells |
| `hand-xrecords` | XNAME, XELEMENT, XGEOMETRY, PROPERTY: read to the end and **not** carried |
| `hand-grid4000` | a 0.25 nm grid and a coordinate near 2³¹ |

`hand-circle.oas` holds two CIRCLE records. gdstk turns them into polygons **at read time**, with the vertex count
following the read tolerance (r = 50 µm: 703 vertices at 0.5 DBU, 4968 at 0.01). The vertices are **off the grid**
(1,544 non-integral coordinates), so the worker must round them and count what it rounded.

### gdstk's own writer (`q5_gdstk.py`)

These are written by `write_oas` through the worker's write path (the one G4 uses) and read back. **Equal**:
rectangles (square and not), a 7-vertex polygon, trapezoids (horizontal, vertical, triangle), placements (0/90/180/
270°, mirror, mag 2, 30°), orthogonal and non-orthogonal reference repetitions, a shape repetition, a hierarchy with
an empty cell, CBLOCK at deflate 0/6/9, checksum32, and standard properties on (693 B against 363 B).
**TRAPEZOID/CTRAPEZOID were only checked gdstk against itself**; the hand encoder does not emit them.

**What gdstk holds**: a repetition stays a `Repetition` (five kinds: rectangular, regular, explicit, explicit_x,
explicit_y, for OASIS's eleven). A trapezoid and a rectangle become polygons. A circle becomes a polygon. Labels
carry no transform. Properties are held, but the worker does not pass them on.

### Silent losses on OASIS write (each becomes a G4 message, never silent)

| Input | Comes back as | Why |
|---|---|---|
| round path end | flush | OASIS has no round extension |
| odd path width 3 | 4 | OASIS stores the half-width as an integer |
| label rotation, magnification, mirror | dropped | OASIS TEXT has none |
| CIRCLE (`circle_tolerance` 0) | polygon | by choice (§ G4 defaults); emitting CIRCLE is the owner's question |
| properties, XNAME, XELEMENT, XGEOMETRY | not imported | out of scope, counted in one message |

### A gdstk defect we never trigger: gdstk#247 (still open)

A negative **explicit** repetition offset is written as about 2⁶⁴ (`explicit_x [100, -300]` reads back as
`18446744073709549568`). How that value then rounds depends on the libm, which is why it is the one file that
differs across platforms (§ Q8). circuitRF never writes an explicit repetition: its arrays are orthogonal, and a
negative pitch is written as type 8 and reads back fine. The corpus's `q5-ref-repetitions.oas` is written without
that row. The original stays in `runs/q5` as the reproduction.

---

## Q6 — Coordinate exactness

`q6.py` (and probe `q6`) runs integer DBU → file → gdstk → integer DBU in all four directions. A reader and a writer
written in the harness, **independent of gdstk**, stand on the other side. There are 8 cases of **1,000,012 values**
each: the extremes of ±2³¹ and 10⁶ random values, at 1 nm and at 0.25 nm.

| | osx-arm64 | linux-arm64 | linux-x64 | win-x64 (Wine) | win-x86 (Wine) |
|---|---|---|---|---|---|
| mismatches, all 8 cases | **0** | **0** | **0** | **0** | **0** |

**How the worker converts:**
- **Read GDSII**: `read_gds(path, unit = the file's own database unit)`, so gdstk's scale factor is exactly 1.0 and
  the doubles it holds are the file's integers. They are sent as `f64` holding exact integers.
- **Read OASIS**: `read_oas(path, unit = oas_precision(path))`. The factor is 1 ± 1 ulp, so **566,644 of the
  1,000,012 values arrive non-integral by ulps**. `llround` (round half away from zero) makes every one exact. The
  spike's worker sends the raw doubles, and §5 has the managed side assert integrality, which would fail on these.
  **G1's worker must `llround` OASIS coordinates before sending them**, and count any value that is off by more than
  ulps. None was in Q6; a CIRCLE's vertices are (§ Q5).
- **Write (both)**: the coordinates arrive as integers n and are handed to gdstk as n·precision/unit in user units.
  gdstk writes `llround(x · scaling)`, which gives exactly n.

**One finding on write**: `write_oas` cannot write a 1 nm or 0.25 nm grid as an integer `real`. No double p gives
1e-6/p == 1000 or 4000 exactly (a ±2000-ulp search), so gdstk writes START's unit as the IEEE double
`1000.0000000000001` (or `4000.0000000000005`). Grids of 100, 10,000, 10⁵ and 10⁶ per µm are exact. **Map-around: an
import rounds `DbuPerMicron` to the nearest integer.** Other readers see the same near-integer, which is harmless but
worth knowing.

---

## Q7 — Scale (Release, osx-arm64)

| File | Size | gdstk read | Worker peak | Shapes |
|---|---|---|---|---|
| q7a.oas — 10⁶ distinct polygons in 100 cells + top | 8.0 MB | 0.13 s | 215 MB | 1,000,000 |
| q7a.gds — the same | 80 MB | 0.22 s | 230 MB | 1,000,000 |
| q7a through the worker, per-cell transfer | — | 0.50 s total | 217 / 232 MB | 127 MB of frames; **largest frame 1.27 MB** |
| q7b.oas — one cell, a 1000 × 1000 rectangle repetition | **316 B** | < 1 ms | 198 MB after expansion | 1 shape, 10⁶ placements |

**Expanding q7b to 10⁶ polygons costs 45–66 ms and about 198 MB** in the worker. circuitRF's model has no shape
repetition, so G4 must expand it, and the expanded transfer is then the q7a case. **Proposed R-oas-4c limit: 10⁶
expanded shapes per import, refused above it** (the owner sets it). At that size the worker peaks near 230 MB and
the transfer takes about half a second. The import cost on circuitRF's side (cell folders, spatial index) is G4's to
measure, with a counter rather than a timed test.

---

## Q8 — Write side

`q8.py` takes the §8a corpus as **our** `GdsiiReader` sees it and writes it as OASIS with `detect_rectangles`,
`detect_trapezoids`, compression 6, CRC32 and standard properties off. It writes **twice**, then reads back through
the worker, whose `open` runs `oas_validate` first.

- **Deterministic**: every pair of writes is byte-identical. The OASIS writer has no timestamp. The GDSII writer
  takes one, and the worker fixes it at 2026-01-01, so it is deterministic too.
- **`oas_validate` accepts all of gdstk's output.** Everything reads back equal to what went in, with two
  exceptions: a path whose extensions equal width/2 returns as half-width (the same shape), and the wrapped negative
  layer from Q4 finding 2 becomes layer 0.
- **Sizes**: tiny files grow, because the END record is 256 bytes (a one-rectangle GDSII is smaller than its OASIS).
  q7a goes from 80 MB to 8.0 MB, and an 8000-vertex polygon from 64 KB to 489 B.

**Platform check** (`platform_check.py`, 91 corpus files read, then written back as GDSII and OASIS): linux-arm64 is
**91/91** byte- and content-identical to osx-arm64. linux-x64, win-x64 (Wine) and win-x86 (Wine) are **90/91**. The
one exception is `q5-ref-repetitions.oas`, the gdstk#247 file, where `llround` of an out-of-range value differs by
libm. `selftest` passes everywhere.

No independent tool's output was available. The owner may supply some, and §3 Q5 says how it would be used.

---

## Worker changes the spike forced (G1 must keep every one)

- **No error pointer to `read_oas`.** Instead the worker checks that `END` was reached, and refuses when the library
  has no name.
- **OASIS guards before `oas_precision`**: the END-record structural check (`EndRecordProblem`, the last 256 bytes),
  then `oas_validate` (CRC32/checksum32). They are on by default. `"validate": false` exists for Q3 only and must not
  reach the product.
- **Refuse when the read logged `Unable to decompress CBLOCK`** (new from the Wine replay; not yet in the spike's
  worker).
- **`llround` every OASIS coordinate before sending it**, including a repetition's spacing, vectors and offsets
  (Q6: a read factor of 1 ± 1 ulp). Count any value that is off the grid by more than ulps, such as a CIRCLE's
  vertices. This is not yet in the spike's worker. It also makes the replies byte-identical across platforms
  (§ Q1, Wine).
- **Replies are always valid UTF-8.** Non-UTF-8 name bytes go out as `\u00XX` (Latin-1). Earlier "hangs" in the
  spike were the client failing to decode a reply.
- **gdstk's `error_logger` is captured per call.** On Windows that must not use `tmpfile()`, which creates its file at
  the drive root (under Wine every message was lost). Use a `%TEMP%` file opened `"w+bTD"` instead. `windows.h` needs
  `WIN32_LEAN_AND_MEAN` and `NOGDI`, because wingdi's `Polygon` clashes with `gdstk::Polygon`.
- **`begin-write` takes `unit_m` and `precision_m`** (§5). The spine tolerance is 0.1 DBU on write.
- **Refuse an implausible frame length and exit**, rather than block reading it (§ Q1, the first Windows attempt:
  a three-byte BOM made the worker wait for 297,778,159 bytes).
- **Read tolerance 0.5 DBU.** gdstk removes path points closer than the tolerance, strictly, so 0.5 keeps a 1-DBU
  segment (gdstk issue #277 is the floating-point form of this).
- **Windows path variant**: `gdstk-worker-utf8.exe` (the manifest) exists until the owner's session picks a route.

## Recommendations for G1 (the owner decides)

- **Timeout.** One gdstk read is one blocking call with no progress, so the timeout must scale with the file:
  q7a's 80 MB GDSII reads in 0.22 s. A deadline of **30 s + 1 s per MB of input**, with cancellation killing the
  worker, is generous by two orders of magnitude.
- **Memory.** The only runaway found allocates more than 1.8 GB in 10 s. The client should poll the worker's working
  set and kill it above a cap, for example **max(2 GB, 50 × the file's size)**. This needs no patch to gdstk, and
  rlimits are unreliable on macOS.
- **Messages.** gdstk's warnings during a read go to the user as an import message. A CBLOCK inflate failure is a
  refusal.

---

## Go/no-go

| # | Criterion | State |
|---|---|---|
| 1 | Q1: macOS (both RIDs), linux-x64 and all three Windows RIDs build statically, no patch; **the owner's real-Windows session passes on win-x64** | **met.** All 7 RIDs build. The real-Windows session passed: win-x64 (under x64 emulation on ARM64) gives 275/279 jobs and win-arm64 276/279, with every difference explained; every guarded malformed file matches macOS. |
| 2 | Q2: no licence incompatibility, no contradictory header | **met** (two permitted items for the owner to read, § Q2) |
| 3 | Q3: no hang a timeout cannot bound; a non-ASCII path works on every platform, Windows on real Windows only | hang: **met** (the one runaway is bounded by a kill, and guarded files never reach it). Paths: **met** on macOS and Linux, and on Windows by the UTF-8 manifest plus the `\\?\` prefix for long paths. |
| 4 | Q4: every simple case agrees both ways, or each difference is deterministic and mappable | **met** (label mirror/mag: our model, deterministic) |
| 5 | Q5: rectangles, polygons, paths, text, orthogonal repetitions, LAYERNAME, CBLOCK read correctly | **met** (independent hand fixtures, 10/10) |
| 6 | Q6: exact | **met** (0 mismatches in 8 × 1,000,012 values on 5 RIDs) |

**Verdict: GO WITH CONDITIONS.** All six criteria are met and no hold was found. Per §3, G1 starts once the owner
has read this. win-x64 ran under x64 emulation on ARM64 Windows (§ Q1), and the owner accepted that run for shipping.

**The conditions** (each an explicit G4 import or export message, never a silent loss):
1. Round path ends become flush, and odd widths are rounded up by one DBU, on OASIS write.
2. Label rotation, magnification and mirror are dropped on OASIS write.
3. A CIRCLE arrives as a polygon. It stays a polygon, and its off-grid vertices are rounded and counted.
4. Properties, XNAME, XELEMENT and XGEOMETRY are not imported, and are counted in one message.
5. A shape repetition (or a non-orthogonal placement repetition) is expanded under the R-oas-4c limit and refused
   above it.
6. OASIS grid: `DbuPerMicron` is rounded to the nearest integer on import (Q6).
7. The validation guards are on and the CBLOCK inflate failure is refused (§ Worker changes).
8. gdstk#247 is never reached: circuitRF writes no explicit repetitions. If G2–G4 ever do, this needs re-testing.
9. G2 refuses a negative layer or datatype rather than passing it to the worker (Q4 finding 2).
10. **Windows paths:** the shipped Windows worker carries the `activeCodePage` UTF-8 manifest, and the client adds
    the `\\?\` prefix to any path of 260 characters or more.
11. **64-bit file offsets in the worker.** `EndRecordProblem` uses `ftell`/`fseek` with `long`, which is 32 bits on
    Windows, so it breaks on an OASIS file over 2 GB there. Use `_ftelli64`/`_fseeki64` on Windows (`ftello`/`fseeko`
    elsewhere). The same check reports a file that cannot be opened under the code `read.truncated`; it needs its
    own code (`read.open-failed`).

## For the owner

- **The Windows session is done** (§ Q1). The owner decided that win-x64 ships on the session's run under x64
  emulation on ARM64 Windows; no x64-hardware run is required.
- **Six GDSII findings in our own stack** (§ Q4), each its own item. None is fixed here.
- **gdstk upstream candidates** (read only; whether to report them is the owner's call):
  1. `read_oas` given an error pointer stops silently at the first XNAME/XELEMENT/XGEOMETRY;
  2. gdstk#247 (open), where a negative explicit offset is written as about 2⁶⁴;
  3. unbounded allocation on a corrupt CBLOCK size;
  4. SIGSEGV on truncated OASIS (unguarded) and on some byte flips;
  5. after `Unable to decompress CBLOCK`, parsing continues over an undefined buffer;
  6. `write_oas` cannot express a 1 nm or 0.25 nm grid as an integer real.
- **The R-oas-4c limit** (10⁶ proposed), the timeout and memory policy, and whether OASIS `CIRCLE` should ever be
  emitted.
- **Licensing**: static libstdc++/libgcc on Linux and static libc++/libunwind on Windows (§ Q2).

# Brief VL-18 — Every platform's build from any one machine

**Series:** [Vendor libraries](brief-vendorlib-0-overview.md), model-library part ([VL-6](brief-vendorlib-6-model-library-format.md)) · **Tag:** `R-vl18-m`
**Area:** `src/Core/Devices/External/VerilogACompiler.cs` (`VerilogASourceCompiler.Build`, `RunCompiler`), a new link
driver beside it, `ModelLibraryExport.Protect.cs` (`BuildProtected`, `FixFor`), Settings ▸ Security & Permissions (the
Verilog-A compiler row's neighbour), `circuitrf export part`, `docs/user/src/reference/protected-models.md`
**Depends on:** 15, 16. **Rule:** overview §0's naming rule holds. Fixtures are synthetic (`TSTPA200`).

---

## 0. What this adds

Compile netlist (brief 16) tries every platform and, as installed, builds only the one it runs on. A maker who
wants a library for Windows, Linux and both Macs has to export on each. This brief makes **one export, on any of
the three operating systems, build every platform the compiler can target** — six of the seven circuitRF ships for
(`win-x86` has no target in the compiler, from any machine).

What was measured on a Mac on Apple silicon (src/Design/RESOLVED.md, "Model-library series review"), with no change
to circuitRF:

| Cause | What it does today | What fixes it |
|---|---|---|
| The compiler initialises only its own processor's code generator | every target for another processor fails with "No available targets are compatible with triple …" | a compiler that initialises every target — a four-line change to it, measured with a local build |
| The compiler links by running a program named `clang` (macOS host), `ld` (Linux host) or `link.exe` (any Windows target) from `PATH` | a Linux or Windows target fails at the link; on a Mac the system linker rejects the GNU flags or writes nothing | a linker that targets every platform: `zig cc` did all of it |
| The Mach-O link names no architecture | an `osx-x64` build linked on Apple silicon came out **arm64**, which `OsdiBuilds.PlatformsOf` already refuses | pass the target's architecture |

With both causes answered, from that one Mac: `linux-arm64` and `linux-x64` built and **loaded and described
under `osdi-worker` on Linux**; `win-arm64` and `win-x64` built as DLLs exporting the `OSDI_*` symbols and importing
only the UCRT API sets and KERNEL32; `osx-x64` built as an x86-64 Mach-O library. None of the Windows or Intel Mac
builds has been run.

## 1. Decisions

- **D1 — Windows and Intel Mac builds ship untested.** No step of this brief runs a build on its own platform; a
  cross-built file is listed in `Builds` and not in `Proved`, the state brief 16 already has and states (*"compiled
  on another machine and never run by its publisher"*). The proof (brief 16 §4) still runs on the exporting
  machine's own build, as now.
- **D2 — The platform the export runs on is built exactly as today.** Its own build, its proof and its compile
  cache are untouched; only OTHER platforms go through the link driver (§2). A regression in the new route can cost a
  cross build, never the native one.
- **D3 — circuitRF ships no linker and no compiler.** `zig` is found the way the compiler is
  (`VerilogACompilerDiscovery`'s order: a path in Settings, then an environment variable, then `PATH`), and a missing
  one is a sentence, not a download. Bundling it would be a native dependency; that is the owner's call and is not
  made here.
- **D4 — circuitRF never patches or redistributes the compiler.** A compiler that builds only for its own processor
  is detected and SAID (§3), naming what would fix it in words; the change itself belongs to that compiler's
  maintainers. Whether to propose it to them is the owner's decision, outside this brief.
- **D5 — No dialog, no new option.** Export already tries every platform (R-vl16-2g). This brief changes what the
  attempt can reach and what a failure says, not when it is made.

## 2. `R-vl18-1` — the link driver

- **`R-vl18-1a`** For a cross-platform build, `VerilogASourceCompiler.Build` runs the compiler with a per-run folder
  first on its `PATH`, holding a launcher under each name the compiler can ask for — `clang`, `cc`, `ld`, `gcc`,
  `link.exe` (`.exe` forms on Windows) — and with the target's rid in an environment variable the launcher reads.
  Every launcher runs **one** circuitRF link driver. The folder is deleted after the build.
- **`R-vl18-1b` No native dependency.** The launchers must work on Windows, where `link.exe` has to be a real
  executable. A managed program shipped like `osdi-worker`, whose apphost copies carry the launcher names beside its
  one `.dll`, meets this; a shell script does not. The implementer may choose another route that adds no native
  code; a native stub is an Ask-before.
- **`R-vl18-1c` Windows hosts.** On Windows the compiler asks the installed Windows build tools for `link.exe` before it
  looks at `PATH`. Measure whether that bypasses the driver for a Windows target. If it does, a Windows target on a
  Windows host keeps that route (the host's own toolchain, as today), and the other four go through the driver.
- **`R-vl18-1d` The translation**, as one pure function `LinkCommand(rid, compilerArgs) → zig argv`, tested without
  running anything:
  - **ELF** (`linux-*`): `zig cc -target <arch>-linux-gnu -shared -o <out> <objects> -lc -lm`. Drop `--no-add-needed`,
    `--hash-style=gnu` and `-m <emulation>` (GNU `ld` options). The compiler names its objects `.o1` … `.oN`; zig takes
    only `.o`, so link a renamed copy or a link of each. Pin a glibc old enough for the distributions the Linux
    packages target (measure; `2.28` linked and loaded).
  - **PE** (`win-*`): from `/OUT:<out> /DLL <objects> msvcrt.lib <import>.lib` to
    `zig cc -target <arch>-windows-gnu -shared -o <out> <objects> <import>.lib <fltused>`. Drop `/NOLOGO`, `/DLL` and
    `msvcrt.lib`: zig supplies the Windows runtime libraries. Keep the compiler's own import library. Add a one-line
    object defining `int _fltused = 0;` for x86-64 (the compiled code references it; the Windows C runtime defines it).
  - **Mach-O** (`osx-*`): on a Mac, the system `cc` with `-arch <arch>` added. Off a Mac, `zig cc -target
    <arch>-macos -shared` — **measure** that it links the `-undefined dynamic_lookup` form the compiler asks for, and
    that an `osx-arm64` build made off a Mac carries the ad-hoc signature Apple silicon needs to load it. If either
    fails, `osx-*` off a Mac is reported not built with that reason.
- **`R-vl18-1e`** The driver's own failures (zig missing, zig's error) come back through the compiler's link failure
  unchanged, so `FirstErrorLine` reads them; the driver prints a first line that says what failed in a sentence.
- **`R-vl18-1f` Every build is still judged by its header.** `OsdiBuilds.PlatformsOf` already refuses a build for
  the wrong platform (that is what caught the arm64 `osx-x64`); it stays the last check, for every route.

## 3. `R-vl18-2` — what a failure says

`FixFor` gives the one sentence a Builds row ends with. After this brief:

| The row is not built because | It says |
|---|---|
| no linker was found | *"Install zig to build for other platforms, or name it in Settings ▸ Security & Permissions."* |
| the compiler builds only for its own processor | *"The Verilog-A compiler builds only for the processor it runs on (arm64 here). A compiler build that targets every processor builds this platform too."* |
| `win-x86` | unchanged: no machine can build it with this compiler |
| zig refused | zig's own first line, verbatim |

- **`R-vl18-2a`** The processor limit is classified from the compiler's own message (the "No available targets"
  crash), never assumed from the platform pair, so a compiler that targets every processor is used as soon as one is
  installed.
- **`R-vl18-2b`** The dialog's Builds table, `--json`'s `builds[]` and the CLI's text carry the same sentence.
- **`R-vl18-2c`** Settings ▸ Security & Permissions gains a **Linker (zig)** row beside the compiler's, with the
  same found-on / set-by line the compiler row has.

## 4. `R-vl18-3` — the page

`protected-models.md`'s "Building for every platform" is rewritten from what this builds: one table of the six
platforms with zig installed (and with a compiler that targets every processor), the sentence each failure gives, and
that a cross-built Windows or Intel Mac build is shipped unrun (D1). The "export on each platform" route stays, as
the way to get every build **proved**.

## 5. Gate

1. `LinkCommand` for each of the six rids, against the compiler's link lines as recorded in this brief's
   measurement: the dropped options, the renamed objects, the `_fltused` object for `win-x64`, `-arch` for `osx-*`.
2. A missing zig gives §3's first sentence on every cross row; the native row is built and proved as before (D2).
3. A stub compiler that crashes with the processor message gives §3's second sentence (`R-vl18-2a`).
4. With a compiler and zig installed (a `[CompilerFact]`-style attribute that also needs zig): Export with Compile
   netlist on `TSTPA200` gives every reachable rid a build whose header `OsdiBuilds.PlatformsOf` reads as that rid.
   The native one is proved; the rest are `Builds` and not `Proved`.
5. Nothing run on another platform is part of the gate (D1).

## 6. Not in this brief

Bundling zig; changing or shipping the compiler; running a cross build on its own platform; `win-x86`; the compile-on-
place path (`VerilogASourceCompiler.Compile`), which builds for this machine only and is unchanged.

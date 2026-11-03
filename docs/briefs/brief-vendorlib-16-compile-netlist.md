# Brief VL-16 — Compile Netlist: Export a cell as one protected compiled model

**Series:** [Vendor libraries](brief-vendorlib-0-overview.md), model-library part ([VL-6](brief-vendorlib-6-model-library-format.md)) · **Tag:** `R-vl16-m`
**Area:** new `src/Design/VendorLibraries/ModelLibrary/Protect/` (`NetlistVerilogA.cs`, `ProtectPartition.cs`,
`ProtectCheck.cs`), `src/Design/Modeling/VerilogA/` (`CmodelVerilogA`, `VaNetwork`, `VaEquation`, which are
reused and refactored rather than copied), the Export to Library dialog and `circuitrf export part` (brief
10), `docs/user/src/reference/protected-models.md` (brief 15)
**Depends on:** 10, 15. Builds on 11 (technology parts are already SnPs) and 13 (spreads).
**Rule:** overview §0's naming rule holds unchanged. Fixtures are synthetic (`TSTFET100`, `TSTPA200`).

---

## 0. What this adds

Brief 15 lets a maker publish a compiled-only model, but the maker has to write the Verilog-A by hand. Most
makers already have the model as a circuitRF cell: a ComposedFET with its package network, or a matched stage
built from lumped parts and a device. **Compile Netlist** is an Export to Library option that turns that cell
into one Verilog-A module, compiles it with the user's own compiler, and publishes the compiled build in
place of the cell. **The generated source never enters the library.**

Two things already exist and are reused, not duplicated:
- `CmodelVerilogA` writes a **ComposedFET** as one self-contained module: its extrinsic network,
  constituents, traps, self-heating and thermal pin (MC-26/27).
- `VaNetwork` translates `R`, `C`, `L`, `Mutual` and `Short` into Verilog-A branches.

## 1. Decisions

- **D1 — One flat module, never a hierarchy of modules.** The whole compiled part is a single `module`.
  Sub-cells are flattened and every device is written inline with prefixed names. A compiled file holding
  several modules would still need a netlist to connect them, and that netlist is what is being protected.
- **D2 — Anything that cannot be written as Verilog-A stays outside, visibly.** The cell is partitioned
  (§2). Elements that translate go inside the module. Elements that do not (an SnP, a transmission line, a
  SPICE-file device, a Verilog-A source device) stay in the wrapper `.cnl`. **Every net that joins the two
  becomes an extra terminal of the module.** The dialog shows exactly what stays outside and how many terminals
  that adds, before anything is written. Nothing is approximated to make it fit.
- **D3 — Parameters survive as parameters.** The cell's parameters become module parameters, with defaults
  and units. Flattening keeps expressions rather than values, translated through `VaEquation`'s expression
  writer, so a placed part takes overrides, sweeps, corners (brief 13) and tuning exactly as the unprotected
  part does. An expression using a function with no Verilog-A spelling is refused by name.
- **D4 — Spreads live on parameters, not inside the module.** A brief 13 distribution function on a
  parameter default stays in the wrapper and still draws per instance, because the wrapper evaluates it and
  passes the result in. A distribution written **inside an element value** that would be compiled is refused,
  with *"Make <value> a cell parameter to keep its spread."* A compiled module cannot take part in circuitRF's
  trials.
- **D5 — Export proves the compiled part matches the cell, or writes nothing.** See §4.
- **D6 — The generated source is the maker's, and is deterministic.** It is written outside the library
  (§3c) and carries no timestamp, so the same cell always gives the same bytes. That is what lets another
  platform's build be added later without the parts drifting apart (§3d).

## 2. `R-vl16-1` — the partition and the module

- **`R-vl16-1a` `ProtectPartition`** flattens the cell's `.cnl` (sub-cells inlined, instance paths as name
  prefixes, expressions kept per D3) and sorts each element into **inside** or **outside** by one table:

  | Inside (translated) | Outside (stays in the wrapper) |
  |---|---|
  | `R`, `C`, `L`, `Mutual`, `Short` (`VaNetwork`) | `SnP`, and every technology part brief 11 already turned into one |
  | `ComposedFET` (`CmodelVerilogA`, as an inlined fragment) | ideal and physical transmission lines |
  | further types only when a test proves them to rounding | `SpiceModel`, `VerilogA` (source or compiled), `ExtDevice` |

  Grow the inside column only with a test per type. A type absent from both columns is a refusal naming the
  type, never a silent outside.
- **`R-vl16-1b` ComposedFET as a fragment.** Refactor `CmodelVerilogA` so the same writer emits either the
  standalone module (unchanged bytes, and its existing tests stay green untouched) or a **fragment**: the
  same equations with every node, branch, variable, function and parameter name prefixed by the instance
  path. One writer and two outputs, never a second copy of the FET equations. Its existing refusals carry
  through unchanged. In particular a **table constituent is refused** (Model Composer D17), and the dialog
  quotes the writer's own sentence: the maker refits that constituent as an ANN or an equation, or keeps the
  FET outside as a separate protected device (§2c).
- **`R-vl16-1c` A cell that is only a ComposedFET** compiles to exactly the standalone module, so the
  simplest protected FET is the path that is already tested.
- **`R-vl16-1d` The thermal pin.** A ComposedFET's thermal pin, if wired to a cell pin, is a module terminal.
  If it is left internal, it stays an internal node of the module.
- **`R-vl16-1e` Outside devices that are nonlinear** (a `VerilogA` source device in the cell) may each be
  compiled separately with the same protection: its `.osdi` is built and shipped, and its source is not. The
  dialog offers this per device. Merging a third-party Verilog-A source into the generated module is **not**
  in this brief, because it needs a full Verilog-A parser and renamer.

## 3. `R-vl16-2` — Export to Library

- **`R-vl16-2a` The option.** The Export dialog gains **Compile netlist (protected model)**, off by default
  and greyed with a sentence when no Verilog-A compiler is found (the existing lookup in Settings ▸ Security
  & Permissions, unchanged). When ticked, it shows the partition: what is compiled, what stays outside, the
  terminal count, and each refusal. Export stays disabled while a refusal stands.
- **`R-vl16-2b` What the model folder holds.** `models/<rid>/<Part>.osdi` (brief 15), the outside files
  (`.sNp` and so on), and a **wrapper** `.cnl` that places the compiled module and the outside elements.
  The wrapper `.csch` is drawn from the wrapper `.cnl` by the existing netlist-to-schematic path, so brief 6
  D1 (the `.cnl` is what extracting the `.csch` writes) still holds. **The original `.csch`, its sub-cell
  folders and any `.cmodel` are not copied.** Those are the topology being protected.
- **`R-vl16-2c` The source goes to the maker, not the library.** The generated `.va` is written to
  `<source workspace>/protected-source/<Part>/<Part>.va`, or to `--source-out` headlessly. The dialog's last
  line says *"The model's source is in protected-source/. It is not in the library. Keep it: every platform's
  build is made from it."* Export refuses a source path inside the library folder.
- **`R-vl16-2d` Other platforms.** `part.cpart` records `"CompiledFrom": "sha256:<hex>"` over the generated
  source bytes. Running the same Export with Compile netlist on another platform, into the same library,
  **adds** that platform's build when the hashes match and refuses when they differ: *"The cell changed since
  the osx-arm64 build. Rebuild every platform from one source."* With brief 15's `--build <rid>=<path>`, a
  build machine never needs the GUI.
- **`R-vl16-2f` Who needs a compiler.** Only the maker, at export. A user who places a protected part needs
  none: they load the shipped `.osdi`, as the `.osdi` route already does.
- **`R-vl16-2g` Builds for every platform from one machine, where the compiler can.** The compiler has a
  `--target <triple>` option. Export tries every brief 15 `<rid>` and reports each one as **built** or **not
  built, with the compiler's own reason**. A target that fails never fails the export. Measured on an arm64
  Mac with the compiler build installed there (2026-11-03): only `osx-arm64` succeeded. `linux-arm64` and
  `win-arm64` failed at the link step because no cross-linker was installed. Every x86-64 target stopped
  because that compiler build has no x86-64 code generator. Whether installing a cross-linker, or using a
  compiler build with every code generator, gets all seven is **unverified**. The builder measures it, records
  the result in `src/Design/RESOLVED.md`, and the page states what was found, not what should work. The
  per-platform route (2d) therefore remains the dependable one, and a CI job on each platform is how a maker
  covers all of them.
- **`R-vl16-2h` A shipped build is compiled for a generic CPU.** circuitRF passes no CPU option today, and the
  compiler's default is `native`, meaning the instructions of the machine doing the compiling. That is right
  for a model compiled and run on one machine, and wrong for one that is shipped: it can stop with an
  illegal-instruction fault on a user's older processor. Export passes `--target_cpu generic` for every build
  it puts in a library. The existing compile-on-place path stays unchanged.
- **`R-vl16-2i` Only the native build is proved.** §4 runs the build for the platform Export is running on.
  Every cross-built `.osdi` is listed as *"built, not run on this machine"* in the dialog, in `--json`, and in
  a `part.cpart` key `"Proved": ["osx-arm64"]`. A later Export with Compile netlist on another platform, with
  a matching `CompiledFrom` hash, runs the proof there and adds that platform's `<rid>` to `Proved`.
- **`R-vl16-2e` CLI.** `circuitrf export part … --compile-netlist [--source-out <file>] [--compile-separately
  <instance>]…`. As with every verb, the logic lives in `src/Design` and `src/Cli` only parses arguments and
  reports.

## 4. `R-vl16-3` — the proof (D5)

Before anything is written to the library, `ProtectCheck` runs the **original cell** and the **wrapper with
its freshly compiled module** through the same engine and compares them:

- DC terminal currents, and Y-parameters at three frequencies spread across the part's `Frequency` range
  (brief 6), at a set of bias points. The default set is every terminal at 0 V, plus each terminal pair at
  ±1 V. The dialog shows the set and lets the maker edit it (`--check-bias` headlessly), because a FET's
  meaningful bias is the maker's knowledge, not something the exporter can infer.
- For a cell with a thermal pin: the same points at two ambient temperatures.
- The limit is 1e-6 relative, with an absolute floor of 1e-12 A and 1e-12 S. These are the same equations
  in the same double precision, so a real mismatch is a translation error, not rounding.

The result appears in the dialog and in `--json` as the worst point and its difference. **Over the limit,
nothing is written to the library**, and the generated source is kept in `protected-source/` for the maker
to inspect. This check is what makes the option safe to offer: a translation error in a published protected
model would be invisible to every user, because none of them can read it.

## 4a. `R-vl16-5` — research and report: cross-platform builds (not a gate)

This step **does not gate the brief.** Its result is a report and the wording in 4b. Export works, and the
brief completes, even if no cross-build ever succeeds.

- **`R-vl16-5a` Find out what each target needs.** Start from 2g's measurement and, for each `<rid>`, establish:
  - whether the compiler can generate code for it at all. Its `--supported-targets` list has no 32-bit
    Windows triple, so `win-x86` may be impossible with this compiler: confirm, and say so plainly
  - which linker it calls for that target, and whether that linker can be named. Look for an option or an
    environment variable, and read the compiler's own output on a failed link. On this Mac the Linux link went
    to the system `clang`, which rejected GNU linker flags
  - whether an LLVM-family cross-linker (the ELF and COFF flavours), installed by the maker, makes the link
    succeed, and whether a Windows link also needs import libraries the maker would have to supply
  - whether a compiler build that includes every code generator exists for each host, and whether one host's
    build can then produce all seven
  - for each build that succeeds, **whether it actually runs**: load it with `osdi-worker` on its own platform
    and evaluate the fixture. Use the owner's Windows arm64 VM, and a Linux container or machine for the Linux
    builds. A file that links but never loads is a "not built", not a success.
- **`R-vl16-5b` Report** in `src/Design/RESOLVED.md`, with a summary in the completion note: a table of
  `<rid>` × host giving the result, exactly what had to be installed, the commands that worked, and how long
  each build took. The report ends with a recommendation: what Export should do automatically, what the page
  should tell a maker to install, and what stays impossible.
- **`R-vl16-5c` Constraints.** circuitRF does not bundle a linker or a compiler (the CLAUDE.md "ask before
  native dependencies" rule, and the compiler licence reason in `veriloga.md`). Anything the report
  recommends is something the maker installs, found the way the compiler is found: on `PATH`, in the usual
  install folders, or named in Settings ▸ Security & Permissions, beside the compiler. If the research shows a
  linker setting is worth adding there, that is a follow-up brief, not this one.
- **`R-vl16-5d` Act only on what was measured.** If a target is shown to build **and load**, Export uses
  that route automatically when the tools are found. If it is not, Export still tries it and reports it under
  4b. Nothing claims a platform the research did not prove.

## 4b. `R-vl16-6` — telling both people what is missing

Two people meet a missing platform build, at different times, and each needs a different sentence.

**The maker, at export.**

- **`R-vl16-6a`** The dialog ends with a **Builds** table, one row per `<rid>`:
  - **Built and checked**: the proof (§4) ran here
  - **Built, not run on this machine** (2i)
  - **Not built**: the compiler's own first error line, then one sentence on what would fix it, taken from
    5b's findings (*"Install a Linux cross-linker, or export again on a Linux machine."*). If no fix is
    known, it says so rather than guessing: *"No known way to build this from macOS. Export on Windows."*
- **`R-vl16-6b`** Above the table, one summary line: *"This part will be unavailable on: win-x64, win-x86,
  linux-x64."* It is a warning, not a refusal, because shipping for some platforms is a legitimate choice.
- **`R-vl16-6c`** `--json` carries the same table (`builds[]`: `rid`, `state`, `reason`, `fix`), and the CLI
  prints the summary line to stderr. The exit code stays 0, because a missing platform is not an export
  failure. A CI job that requires every platform tests the JSON.
- **`R-vl16-6d`** `check <library>` reports, once per library, the platforms it misses entirely or
  partially (*"3 parts have no win-x64 build"*), at info severity. A maker sees the gap before publishing,
  and a consumer can ask before adopting a library.

**The designer who places or runs the part.**

- **`R-vl16-6e` In the palette:** brief 15 D3's greyed entry, with the publisher's name and `library.clib`'s
  `Url` when present: *"No build of TSTFET100 for win-x64. The publisher ships it for osx-arm64 and
  linux-arm64. Contact Example Devices: <url>."* Never hidden.
- **`R-vl16-6f` In a design that already places it.** The common case is a workspace shared from a colleague
  on another platform. Open it, and the instance stays on the schematic with its outline marked as a missing
  file is marked today, and the Messages panel gives the 6e sentence once per part, not once per instance.
  `check` reports it as an **error**, because the design cannot run here. A run refuses before solving, with
  the same sentence, naming every instance affected. Nothing substitutes another model, and nothing runs the
  rest of the circuit without the part.
- **`R-vl16-6g` A build that is present but was not checked** (it appears in `Builds`, not in `Proved`) runs
  normally. If it fails to load, the existing worker failure message gets one more sentence: *"This build of
  TSTFET100 was compiled for win-arm64 on another machine and was never run by its publisher. Report this to
  Example Devices."* Without that sentence, a broken cross-build reads as a circuitRF fault.
- **`R-vl16-6h`** The part's **?** page (brief 3) lists the platforms it ships for and which were checked.
  This is the one place a designer can find out before placing it.

## 5. `R-vl16-4` — the page

`protected-models.md` (brief 15) gains a section: what Compile netlist puts inside and leaves outside (the §2
table), why a table constituent has to be refitted, where the source goes and why to keep it, adding other
platforms, and the proof's limit. It also gains a **Building for every platform** section, written from 4a's
report: what was proved to work from each host, what to install, what is impossible, and the per-platform
CI route as the dependable fallback. It also says what a designer sees when a platform is missing (4b), so a
maker knows what their users will read. Next to the page's list of "what a compiled model still reveals", add one
line: **the terminals added for outside elements show where the protected network meets the visible one.**

## 6. Tests (minimal)

1. A cell holding only a ComposedFET (ANN constituents) generates bytes identical to `CmodelVerilogA`'s
   standalone module (`R-vl16-1c`).
2. Partition: a cell with R/L/C, a ComposedFET, an SnP and a sub-cell puts each on the expected side, adds
   exactly the joining nets as terminals, and keeps a sub-cell parameter as an expression of the parent's.
3. Refusals: a table constituent quotes the writer's sentence; a spread inside an element value names the
   value; an unknown type is named.
4. Determinism: two exports of one cell give identical source bytes and one `CompiledFrom`. Every compile
   Export runs passes `--target_cpu generic`, and a target that fails to build is reported with the
   compiler's text without failing the export.
5. The proof, using a compiler when one is installed and skipped with a reason otherwise (as the existing
   Verilog-A compile tests do): a correct cell passes; a cell whose generated source is deliberately
   corrupted by one coefficient fails, and nothing reaches the library.
6. The library folder after a protected export holds no `.va`, `.cmodel`, original `.csch` or sub-cell
   folder, and `check`s clean under brief 15's rules.
7. Spreads (D4): a `gauss(…)` parameter default still draws per instance through the compiled part in a
   yield run, and is nominal in an ordinary run.
8. Missing platform (4b): with the current `<rid>`'s build removed from a library part, `check` on a design
   placing it twice gives one error naming both instances. A run refuses before solving, with 6e's sentence
   including the publisher's `Url`. The `--json` `builds[]` of an export lists a failed target with its
   reason and exits 0.
9. Unchecked build (6g): a deliberately corrupt `.osdi` listed in `Builds` but not in `Proved` fails to load
   with the extra sentence. The same file listed in `Proved` gives the worker's message without it.

Run only the classes you touch.

## 7. On completion

As brief 6 §9. The dialog's new rows, with a partition that leaves an SnP outside and a refusal showing,
each get a `tools/UiSnapshot` render that has been looked at.

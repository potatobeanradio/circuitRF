---
title: Protected Models
slug: reference/protected-models.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Protected Models
lede: For a device maker deciding whether to publish a model here - why circuitRF does not encrypt models, what to compile and what can stay outside, what a compiled model still reveals, how to restrict use from inside it, and how to sign the library.
keywords: protected model, encrypted model, compiled model, osdi, Verilog-A, device maker, model protection, licence, license check, $fatal, signed library, platform build, publish a model
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#why">Why circuitRF does not encrypt models</a></li>
<li><a href="#split">What to compile, and what can stay outside</a></li>
<li><a href="#compile-netlist">Compile netlist: publish a cell as one compiled module</a></li>
<li><a href="#reveals">What a compiled model still reveals</a></li>
<li><a href="#restrict">Restricting use from inside the model</a></li>
<li><a href="#publish">Signing, and the platforms to build for</a></li>
<li><a href="#every-platform">Building for every platform</a></li>
<li><a href="#missing">What your users see when a platform is missing</a></li>
</ol>
</nav>

## Why circuitRF does not encrypt models {#why}

A simulator that accepts encrypted models has to decrypt them on the user's machine, so it needs the key. In a
closed program the key is hidden inside the program's binary. circuitRF's source is public, so any key it held
would be public too, and anyone could rebuild circuitRF to write the decrypted model to disk. An encryption
feature here would protect nothing, so circuitRF has none.

What does protect a model, in any simulator, is **compiled code**. You compile your own model, and circuitRF only
asks it for currents, charges and their derivatives at the terminal voltages it supplies. circuitRF runs a compiled
`.osdi` in a separate process (see [Verilog-A](veriloga.html)). The protection is entirely in your binary, so
circuitRF being open source costs you nothing. circuitRF never holds a secret of yours, so there is nothing in
circuitRF to extract.

## What to compile, and what can stay outside {#split}

A protected model is an ordinary [model folder](model-library-format.html#compiled) in which the device you want to
protect is a compiled `.osdi` with no source beside it.

- **The `.cnl` stays plain text and is still what runs.** It is a short wrapper that names the compiled device,
  its pins and its parameters, and connects it to whatever surrounds it. circuitRF does not compile, encrypt or
  hide a netlist.
- **S-parameter data stays a plain `.sNp` beside the model.** A compiled device cannot read a Touchstone file, so a
  measured package or fixture remains an ordinary SnP instance in the `.cnl`. You give nothing up by this: the
  S-parameters of any placed part can be simulated and exported, so linear data was never protectable.
- **Put the topology you want hidden inside the compiled device.** If the package parasitics or an internal
  network are part of what you protect, write them into the same Verilog-A module as internal nodes, then
  compile it. What sits in the `.cnl` is what you chose to publish.

## Compile netlist: publish a cell as one compiled module {#compile-netlist}

You do not have to write the Verilog-A yourself. If the model is already a circuitRF cell — a ComposedFET with its
package network, or a matched stage built from lumped parts and a device — tick **Compile netlist (protected
model)** in **Export to Library** (on the command line, `--compile-netlist`). It is off unless you tick it. Export
writes the whole cell as **one** Verilog-A module, compiles it with your own compiler, and publishes the compiled
build in place of the cell. The cell's schematic, its sub-cells and any `.cmodel` are not copied into the library.

{{ui: export-compile-netlist}}

**The dialog, top to bottom.** Everything above **Protected model** is Export to Library as it is without Compile
netlist; what changes with it ticked is noted.

| Control | What it does |
|---|---|
| **Library**, **Libraries this workspace references** | The library folder the part goes into. An empty or new folder starts a new library. |
| **Category** | The folder inside the library, `/`-separated (`Amplifiers/LNA`); empty puts the part at the library's root. |
| **Name**, **Publisher**, **Version**, **License** | Written to a new library's `library.clib`; greyed when the library already has one. |
| **Part number**, **Description** | The part's name in the palette (the model folder's name) and its one-line description. |
| **Kind**, **Value** | Active or Passive (a nonlinear part makes it Active), and a passive's value, which value search reads. |
| **Frequency**, **Conditions**, **Footprint** | The range the model is valid over, the conditions it was made at, and the footprint, as the palette shows them. The proof's three frequencies are spread across this range. |
| **Parameters** | The cell's parameters, each with a **Tolerance**: a spread here still draws per placed part in a yield run, because the wrapper evaluates it. |
| **Corners** | Named sets of parameter values a placed part can select. |
| **Frequency** (sweep) | The plan technology parts are sampled over when they become S-parameter files; greyed when the cell has none. |
| **Compile Verilog-A models** | Ships each Verilog-A device and ComposedFET the cell places as its compiled build instead of its source (a ComposedFET is written as Verilog-A first). The cell's topology still ships. With Compile netlist, this applies to the Verilog-A devices left outside. |
| **Builds for other platforms…** | Adds builds of a compiled device made elsewhere. Greyed with Compile netlist, which builds every platform the compiler can reach itself. |
| **Compile netlist (protected model)** | Publishes the cell as one compiled module, without its schematic. Off until you tick it, and greyed with the reason when no Verilog-A compiler is found. The line under it says where the generated source will be written. |
| **Proof bias** | The bias points the proof runs at, one per line (`g=-0.5 d=5`; pins not named are at 0 V). Left empty, the line under it lists the default set. |
| **Builds** | Appears once a protected export has run: one row per platform, led by the platforms the part will be unavailable on. The dialog stays open to show it; **Close** finishes. |
| **Documents** | Files copied into the part's `docs/` folder, such as a data sheet. |
| **Symbol** | The symbol the part is placed with: the cell's own, the FET glyph, R, C, L or a box. |
| **Compiled into the model** | With Compile netlist: what is compiled, what stays outside, and the module's terminals. Without it, this list is **Copied into the model**: every sub-cell and file the export copies. |
| Red lines under it | Every reason the export cannot go ahead. **Export** stays greyed while any stands. |
| **Add this library to the workspace** | References the library from the workspace, so the part is in the palette as soon as the export finishes. |
| **Export** | Builds, compiles and proves the part; nothing reaches the library unless the proof passes. |

**What goes inside, and what stays outside.** Sub-cells are flattened into the one module. Each element then lands
on one side:

| Compiled into the module | Left in the wrapper, visibly |
|---|---|
| `R`, `C`, `L`, `Mutual`, `Short` | SnP files, and every technology part Export turns into one |
| `ComposedFET` | ideal and physical transmission lines |
| | SPICE-file devices, Verilog-A devices and external devices |

Anything else is refused by name; nothing is approximated to make it fit. Each net that joins an outside element
to the module becomes an extra terminal of the module, and ground is its last terminal. The dialog lists what is
compiled, what stays outside and the terminal count before anything is written. A Verilog-A device left outside
ships as source unless you also tick **Compile Verilog-A models**, which ships its build instead.

**Parameters stay parameters.** The cell's parameters become the module's, so a placed part takes overrides,
sweeps, corners and tuning as the cell did. A tolerance on a cell parameter still draws in a yield run, because
the wrapper evaluates it and passes the result in. A distribution written inside an element's value is refused —
compiled, it could never draw — with *"Make … a cell parameter to keep its spread."*

**A ComposedFET with a table constituent is refused**, with Model Composer's own sentence: a table cannot be written
as Verilog-A. Refit that constituent as an ANN or an equation, or keep the FET outside as a separate protected
device.

**Where the source goes, and why to keep it.** The generated `.va` is written to
`protected-source/<Part>/<Part>.va` in your workspace folder (`--source-out` chooses another place; a place inside the
library is refused). It is never in the library, so the target library must be a folder outside your workspace: a
library that is the workspace folder, or a folder above it, is refused with *"Change the target library to a folder
outside the workspace."* Keep it: every platform's build is made from it. The same cell
always generates the same bytes, and `part.cpart` records their hash as `CompiledFrom`.

**The proof.** Before anything reaches the library, Export runs the original cell and the wrapper with its freshly
compiled module through the same engine and compares the DC current into every pin, and the Y-parameters at three
frequencies across the part's range, at a set of bias points: every pin at 0 V, then each pair of pins at +1 V and
−1 V across them. For a FET, the bias that matters is yours to know: type your own points in **Proof bias**, one
per line (`g=-0.5 d=5`; on the command line, `--check-bias "g=-0.5 d=5"`). A cell whose thermal pin is a pin is
checked at two ambient temperatures as well. The limit is 1e-6 relative, with floors of 1e-12 A and 1e-12 S. Over
it, nothing is written to the library, the worst point is named with both values, and the source stays in
`protected-source/` for you to inspect.

## What a compiled model still reveals {#reveals}

Know this before publishing, rather than finding it out afterwards. Anyone who has your compiled model can read:

- the **terminal names**
- the **parameter names**, their **defaults** and their **descriptions**
- the **internal node names**
- the **sparsity pattern of the Jacobian**, which shows which nodes couple to which
- the device's **response to any voltages** someone chooses to apply, to as many points as they like
- with Compile netlist, **the terminals added for outside elements**, which show where the protected network meets
  the visible one

Give internal nodes opaque names (`n1`, `n2`) rather than names that describe the circuit. The sparsity pattern
cannot be hidden, because the simulator has to know it to solve the circuit.

## Restricting use from inside the model {#restrict}

If you want to limit who can run the model — a machine, a date, a licence file, a licence server — build that
check into your own model. circuitRF does not know or care which you choose. What it guarantees is this: **a model
that refuses to start stops the run, and the user reads your sentence.** The run ends as one refusal that leads
with your words, in the Messages panel and on the command line's standard error with exit code 1. It does not
continue on zeros, retry, or wait.

A sketch, where `licensed()` stands for whatever check you write:

```verilog
`include "disciplines.vams"

module tstfet100(g, d, s);
  inout g, d, s;
  electrical g, d, s;
  parameter real vto = -0.8;

  analog begin
    @(initial_step)
      if (!licensed())
        $fatal(1, "TSTFET100: this build is not licensed on this machine. Contact the publisher for one.");
    // the model itself
  end
endmodule
```

Write the sentence for the person who reads it: what stopped, and what they can do about it.

## Signing, and the platforms to build for {#publish}

**Sign the library** so users can tell that it is yours and unmodified. A compiled model is native code that runs on
their machine, so this matters more for a protected model than for any other. `circuitrf sign --new-key` makes a
key and `circuitrf sign <library> --key <key>` signs the library. Keep the key outside the library. The details,
and what users see, are in [Signed libraries](model-library-format.html#signing).

**Build for each platform your users have**, and put each build in its own folder under `models/`, named for the
platform:

| Folder | Platform |
|---|---|
| `win-x64` | Windows, 64-bit Intel and AMD |
| `win-arm64` | Windows on Arm |
| `win-x86` | Windows, 32-bit |
| `osx-arm64` | macOS on Apple silicon |
| `osx-x64` | macOS on Intel |
| `linux-x64` | Linux, 64-bit Intel and AMD |
| `linux-arm64` | Linux on Arm |

List them in `part.cpart`'s `Builds`. On a platform you did not build for, the part is listed greyed with the
platforms you do ship for, so a user knows what to ask you for. **Export to Library** copies the build it ran with
and takes the others under **Builds for other platforms…** (on the command line, `--build <rid>=<path>`).
`circuitrf check` proves that every listed build is there and is for the platform its folder names.

## Building for every platform {#every-platform}

With Compile netlist, Export tries every platform in the table above and reports each one in a **Builds** table:
**built and checked** (the proof ran here), **built, not run on this machine**, or **not built**, with the
compiler's own first error line and what would fix it. A platform that fails never fails the export. Every build put
in a library is compiled for a generic processor, so it runs on older processors than the one it was built on.

The platform you export on is built exactly as it always is, with the compiler's own linker. Every other platform is
linked with **zig**, which circuitRF does not ship: install it and put it on PATH, or name it in **Settings ▸ Security &
Permissions ▸ Linker (zig)**. A Mac build made on a Mac uses the Mac's own linker and needs no zig. With zig installed,
and with a Verilog-A compiler that targets every processor, one export from any of the three operating systems
builds:

| Platform | Built from any machine |
|---|---|
| `win-x64` | Yes. Not run before it ships. |
| `win-arm64` | Yes. Not run before it ships. |
| `win-x86` | No: the compiler has no 32-bit Windows target. |
| `osx-arm64` | Yes. |
| `osx-x64` | Yes. Not run before it ships. |
| `linux-x64` | Yes. |
| `linux-arm64` | Yes. |

A platform that is not built says why in its row:

| Why | The row says |
|---|---|
| zig is not installed | *"Install zig to build for other platforms, or name it in Settings ▸ Security & Permissions."* |
| The compiler builds only for its own processor | *"The Verilog-A compiler builds only for the processor it runs on (arm64 here). A compiler build that targets every processor builds this platform too."* A compiler built this way on a Mac on Apple silicon builds `linux-arm64` and `win-arm64`, and none of the x86-64 platforms. |
| `win-x86` | *"The compiler has no 32-bit Windows target, so no machine can build this platform with it."* |
| zig refused the link | zig's own first line, as zig wrote it. |

Every build made on another machine goes into `Builds` and not `Proved`: it was compiled for that platform and never
run by you. A Windows or Intel Mac build made elsewhere ships without having been run on its platform. **To have
every build proved, export on each platform**: run the same Export with Compile netlist on a Windows, Linux or Intel
Mac machine into the same library. When the source's hash matches `CompiledFrom`, that platform's build is added and
proved there, and `part.cpart`'s `Proved` lists it. When it differs, nothing is written: *"The cell changed since
the osx-arm64 build. Rebuild every platform from one source."* A continuous-integration job on each platform does
this without the GUI; its `--json` output carries the same table as `builds[]` (`rid`, `state`, `reason`, `fix`), and
the exit code stays 0 when a platform is missing.

## What your users see when a platform is missing {#missing}

On a platform you did not build for, the part is listed greyed in the palette with the platforms you do ship for.
A build that is in `Builds` but not in `Proved` was compiled on another machine and never run by you; it runs
normally.

The MCP server's `reference` tool serves this page as the topic `protected-models`.

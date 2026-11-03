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
<li><a href="#reveals">What a compiled model still reveals</a></li>
<li><a href="#restrict">Restricting use from inside the model</a></li>
<li><a href="#publish">Signing, and the platforms to build for</a></li>
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

## What a compiled model still reveals {#reveals}

Know this before publishing, rather than finding it out afterwards. Anyone who has your compiled model can read:

- the **terminal names**
- the **parameter names**, their **defaults** and their **descriptions**
- the **internal node names**
- the **sparsity pattern of the Jacobian**, which shows which nodes couple to which
- the device's **response to any voltages** someone chooses to apply, to as many points as they like

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

The MCP server's `reference` tool serves this page as the topic `protected-models`.

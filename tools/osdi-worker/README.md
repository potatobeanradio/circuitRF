# osdi-worker — evaluating an openly-specified compiled device model

Runs a compiled device model that speaks the **OSDI** ABI in its own process and answers circuitRF's
ordinary device-worker protocol. `dotnet build` never builds a model; the user supplies one.

**A third worker, not an extension of either existing one.** `senior-worker` evaluates a proprietary
model ABI; `netlist-worker` asks a library to *describe* a circuit. This one hosts a documented, open
ABI and shares no vocabulary with either — the same relationship those two already have. Do not fork
one into another.

```
./build.sh                     # worker + the test-only model; a missing compiler warns and succeeds
python3 verify.py              # drives it end to end and checks against closed form
```

---

## Why this ABI is the one worth hosting

Its four load functions map onto `ComponentModel.Evaluate`'s `(i, q, dg, dc)` essentially one to one:

| OSDI | circuitRF |
|---|---|
| residual at `nodes[k].resist_residual_off` | `I[k]` |
| residual at `nodes[k].react_residual_off` | `Q[k]` |
| `load_jacobian_resist` | `Dg` |
| `load_jacobian_react(alpha = 1)` | `Dc` |

The interface separates **resistive** and **reactive** contributions natively — the charge
formulation harmonic balance wants, rather than a transient derivative to undo. One integration
therefore reaches an entire ecosystem of compact models rather than one supplier's.

## Five things that had to be right, each established by reading or measurement

**1. `osdi.h` is third-party and stays byte-identical.** It is the ABI contract: the struct layout
must match the producing compiler's exactly, so a hand-copied or tidied version is a silent
corruption rather than a style choice. It carries its own MPL-2.0 notice, is a separate file, and
does not touch circuitRF's MIT core. Its `PARA_KIND_*` macros are *signed* expressions that overflow
at `3 << 30`; the masks are therefore re-expressed as unsigned **here**, at the call sites, never by
editing the header.

**2. Residual offsets are byte offsets into the INSTANCE, not indices into an output array.**
Confirmed against a reference host, which reads them as
`*(double *)((char *)inst + nodes[i].react_residual_off)`. `UINT32_MAX` means the node has none.
Reading them as array indices would have produced plausible numbers from the wrong memory.

**3. `load_spice_rhs_*` is deliberately NOT used.** Those return a *linearized* right-hand side in
SPICE's own convention (the reference host's DC path uses them for exactly that reason). circuitRF
wants the raw `i` and `q`, which is what the residual offsets give directly. Using the SPICE pair
would converge — to a different formulation's answer.

**4. The Jacobian is written through host-installed pointers, and it accumulates.** The worker
installs scratch doubles at `jacobian_ptr_resist_offset` and each entry's `react_ptr_off`, then
scatters them into `G`/`C` by the entry's own node pair. In a real host several instances share one
matrix entry, so the load functions `+=`; the scratch is zeroed per point rather than assumed to be
overwritten.

**5. Node collapsing is DECLARED, not probed — and it is answered at `create`.** The model states its
collapsible pairs and marks which were collapsed during `setup_instance`. That is circuitRF's *slaved
node*, and it arrives for free — the other worker needs a run-time alias map precisely because its ABI
cannot say which node a degenerate one follows, and getting that wrong is a solve that will not
converge with no error anywhere. It cannot be answered at `describe`, because which nodes collapse
depends on the parameters the instance was given. `probe` here answers with nothing, leaving the
create-time report standing.

Two flavours, and they are different claims: `{"node":n,"to":m}` is *follows node m*, while
`"to": -1` is *tied to the ground reference* — which cannot be spelled "follows node 0", since node 0
is an ordinary pin. circuitRF keeps them apart as `SlavedTo` and `CollapsedToGround`.

## Temperature

OSDI's `setup_instance` takes a temperature as a **required argument**, so circuitRF states it in
**kelvin** through a reserved `temperatureK` field on `create` — not as a model parameter, because a
model that happened to declare a parameter of that name would then receive it twice with the two
meanings competing. circuitRF's own parameters are Celsius throughout; the conversion happens at this
boundary, which is the same rule `Diode`'s factory already follows.

`verify.py` is what proves it arrived: the test model's conductance carries a temperature
coefficient, so `g = 0.004` at 400 K and `0.002` if the temperature were silently defaulted. A
temperature that never lands still produces finite, plausible currents — so it has to be observable
in the answer, not merely passed.

## `tools/fake-osdi-model`

A test-only library implementing this ABI honestly and nothing else, so the worker can be driven on a
machine with no model toolchain — the same bargain `tools/fake-model-lib` strikes for the other
worker's ABI. **It is not a model.** Every device has a closed-form answer written in its comment, so
a test asserts against arithmetic rather than against another implementation.

The real producers of `.osdi` files are Verilog-A compilers under GPL-3.0. circuitRF never links,
bundles or invokes one: a user compiles their own models with their own toolchain, exactly as they
would for any other simulator, and this worker loads the result.

## Licensing — why circuitRF stays MIT

**Nothing in this repository is GPL, and nothing here links to anything that is.** The arrangement is
the same arm's-length posture as building circuitRF with `gcc`:

| Thing | Licence | Where it lives |
|---|---|---|
| this worker, and all of circuitRF | MIT | this repository |
| `osdi.h` | MPL-2.0 | its own file here, unmodified — file-scoped copyleft, touches nothing else |
| the Verilog-A compiler | GPL-3.0 | **the user's machine.** Never invoked by circuitRF, never shipped |
| a kit's `.va` sources | the kit's own (Apache-2.0 for open kits) | **the user's kit.** Never vendored |
| the compiled `.osdi` | a build output of the two above | **the user's machine.** Never committed |

The worker `dlopen`s a file the user built. That is a runtime load of the user's own artifact, which
is why the validation tests locate models through an environment variable and skip without one — the
alternative, committing a binary, would put someone else's build product in an MIT repository.

## Driving it against a real model

```
# once: install a Verilog-A compiler of your choosing, then compile your kit's own sources
<your-compiler> <your-kit>/…/psp103.va -o psp103.osdi

# then
CIRCUITRF_OSDI_MODELS=/path/to/compiled dotnet test tests/Core.Tests
```

**On macOS the compiler binary needs an ad-hoc signature.** A downloaded, unsigned arm64 executable
is killed outright by the OS — `rc=137`, no output, nothing in any log — which reads exactly like a
crash in the compiler. `codesign -s - -f <binary>` and its bundled `.dylib`s fixes it. Worth knowing
before spending an hour on it. A native arm64 macOS build does exist, which settles the standing
question above: **this path needs no VM on macOS.**

## Not done yet

- ~~No C# integration.~~ **DONE** — `tests/Core.Tests/Devices/External/OsdiWorkerTests.cs` drives
  this worker as a real process through the real `DeviceWorkerProvider` (6 tests). `build.sh` runs
  from that test project with `ContinueOnError`, so the gate is real on any machine with a C
  compiler and Skipped-with-a-reason on one without.
- ~~Nothing ships it.~~ **DONE, with no new production code** — a kit's ordinary
  `device-provider.json` names this worker by bare command plus the library as an argument, which the
  existing manifest already resolves (bare command against circuitRF's tools folder, relative
  argument against the manifest's own folder). `dotnet build` copies the worker beside the
  application. Gated by `O7`, which stands up a kit folder as an import leaves one.
- **Only the host platform builds.** No cross-compilation, no Windows split, no VM entry. Unlike the
  other worker this may not need one: a user compiles `.osdi` files natively, so there may be no
  foreign binary to bridge. Worth confirming before building any of that.
- ~~Untested against a real `.osdi`.~~ **DONE (2026-08-03)** — driven against real compact models
  compiled from an openly-licensed kit's own Verilog-A. Read correctly: a 4-terminal resistor with
  a thermal node (129 parameters, 4 internal nodes) and an industrial MOSFET (809 parameters, 9
  internal nodes, 7 collapsed nodes). The `param_opvar[]` reading is confirmed. See
  `tests/Core.Tests/Devices/External/CompiledModelValidationTests.cs`, which is **Skipped with a
  reason** unless `CIRCUITRF_OSDI_MODELS` names a directory of compiled models.

  **Two defects only a real model could find**, both invisible to the fixture by construction:

  1. **`OsdiSimParas` must be NON-NULL and NULL-TERMINATED.** This worker passed four null pointers,
     meaning "no simulator parameters". A model resolves `$simparam("gmin", …)` by SCANNING `names`,
     so a null pointer there is a null dereference *inside the model*, during `setup_instance`. It
     presented as the worker dying with no output at all. The fixture never asks for a simulator
     parameter, so nothing here could have caught it.
  2. **A collapsed TERMINAL must keep the user's net** (fixed in the elaborator, not here). A real
     model collapses its drain terminal onto its internal drain, and its bulk terminal plus three
     internal bulk nodes onto one. Reading "node A follows node B" literally gave the *terminal* the
     internal node's index, dropping the net the user wired to that pin — a device that solves
     perfectly while disconnected from the circuit around it.
- ~~Collapsed-node reporting is emitted but unexercised.~~ **DONE** — `crf_collapse` in the test
  model declares both flavours and collapses them per instance from its own parameters; `O8`/`O9`
  drive it through the real provider, and `tests/Engine.Tests/External/GroundCollapsedNodeTests.cs`
  takes it the rest of the way through elaboration. The reporting was, in fact, **dead**:
  `DeviceWorkerProvider.Create` never read the `collapsed` array, so a collapsed node was still
  given a free unknown. That is the bug this closed.

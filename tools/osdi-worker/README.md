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

**6. Parameter DEFAULTS are not in the descriptor, so they get their own command.** `describe`
answers from the descriptor alone: name, kind, and — since the parameter picker needed them — the
model's own `units` and `description`, both of which sit there already and cost nothing. A *default*
does not: this ABI has no field for one. The value is whatever the model writes during
`setup_model`/`setup_instance` for a parameter nobody gave, so the only way to learn it is to stand a
probe model up with nothing set and read every parameter back through `access` **without**
`ACCESS_FLAG_SET`.

That is `{"cmd":"defaults","typeId":"…"}`, and it is deliberately **not** folded into `describe`.
`describe` runs on every worker launch — including the walk a PDK import does across every `.osdi` it
finds — and instantiating a model per device type would charge that import for an answer it never
asked for. `defaults` runs only when the parameter picker is opened.

The probe is torn down immediately and **never occupies an instance slot**: `MAX_INSTANCES` is small,
and a `defaults` call that leaked one would exhaust the table over a long editing session.
`verify.py` calls it 64 times and then checks a `create` still succeeds. A parameter `access` will
not hand back is omitted rather than reported with an invented value, and a default the model cannot
express (NaN, an infinity — both used as "nothing set" sentinels) is emitted as JSON `null`, which
the host reads as "no default to show".

**7. A node's DISCIPLINE is read from the units the model declares.** This ABI names no discipline,
but `OsdiNode` carries the units of both the node's potential and its residual, and those are
unambiguous: Verilog-AMS's `thermal` is `"K"` against `"W"` where `electrical` is `"V"` against
`"A"`. `describe` emits `"quantityKind":"thermal"|"electrical"` per node, **with the raw strings
alongside** (`units`, `residualUnits`) so a discipline nobody anticipated is visible rather than
silently classified as electrical.

It is answered at `describe`, not at `probe`: a discipline is a property of the TYPE and needs
nothing instantiated, while `probe` answers per-instance ROLES. Emitting nothing here made every OSDI
node electrical on circuitRF's side, and with it every thermal path the host has — the ambient hold
on an unconnected thermal terminal, the ground-reference check, the exclusion of a temperature from
the candidate masters for an unwritten node — was unreachable code with no symptom but a solve that
would not converge.

**8. How many terminals the INSTANCE connects is not how many the TYPE declares.** OSDI passes the
first to `setup_instance`, and it is what a model's `$port_connected` reads. `create` takes an
optional `"connectedTerminals"`; absent, it defaults to the declared count, so a caller with nothing
to say behaves exactly as before. Out of range — above the declared count, or below two — is refused
with a sentence rather than clamped.

Passing the declared count unconditionally, which is what this did until 2026-09, makes every
terminal connected on every instance and the "not connected" branch unreachable. A model that grounds
its own thermal node there instead writes **no equation** for it, having been assured the host wired
it, and the node arrives as an all-zero row. `crf_therm` in the test model is that branch, and
`verify.py` drives both sides of it.

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

The real producers of `.osdi` files are Verilog-A compilers under GPL-3.0. **circuitRF never links,
bundles or ships one.** A user installs a compiler of their own choosing; circuitRF may then RUN it —
as a separate program, at arm's length — to build a `.va` the user pointed a component at, and this
worker loads the result. Compiling by hand and pointing at the `.osdi` works exactly as before and
involves no compiler on circuitRF's side at all.

## Licensing — why circuitRF stays MIT

**Nothing in this repository is GPL, and nothing here links to anything that is.** The arrangement is
the same arm's-length posture as building circuitRF with `gcc`:

| Thing | Licence | Where it lives |
|---|---|---|
| this worker, and all of circuitRF | MIT | this repository |
| `osdi.h` | MPL-2.0 | its own file here, unmodified — file-scoped copyleft, touches nothing else |
| the Verilog-A compiler | GPL-3.0 | **the user's machine.** Never linked, bundled or shipped; run as a separate process when the user asks for a `.va` to be built |
| a kit's `.va` sources | the kit's own (Apache-2.0 for open kits) | **the user's kit.** Never vendored |
| the compiled `.osdi` | a build output of the two above | **the user's machine.** Never committed |

**On running the compiler at all** (added when `circuitrf` learned to accept a `.va` directly, brief
PM2 P1). Starting a separately-installed program, passing it the user's own file and reading the file
it writes is *use*, not derivation: no GPL code is compiled into circuitRF, linked against it, or
distributed with it, and removing the compiler from the machine leaves circuitRF working — it simply
refuses to build source and still loads any `.osdi`. This is the identical relationship every build
system has with `gcc`, and `THIRD-PARTY-NOTICES.md` gains no entry because nothing third-party ships.
Discovery, invocation and caching are `src/Core/Devices/External/VerilogACompiler.cs`.

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

---

## A quarantined model library, on macOS

A kit downloaded from a vendor carries macOS's `com.apple.quarantine` attribute, and `dlopen` then
refuses it outright:

```
dlopen(…/model.osdi): code signature not valid for use in process:
                      library load disallowed by system policy
```

**There is no prompt and nothing to allow.** Measured in the kernel log on macOS 26:

```
ASP: Library load (… -> …/model.osdi) rejected: library load disallowed by system policy
```

Unlike a blocked *application*, a blocked *library* produces no dialog and no System Settings entry,
so a user who knows the "Open Anyway" routine has nowhere to apply it. Approving circuitRF itself
does not help either — the kit is installed separately and carries its own attribute. The remedy is
to clear it on the kit:

```sh
xattr -dr com.apple.quarantine <the kit's folder>
```

`WorkerOutputDiagnosis` (in `src/Core/Devices/External/`) recognises this in the worker's stderr and
appends exactly that, with the kit's own path filled in, wherever worker output is shown — the
exception paths and `Cli`'s end-of-run dump alike. It deliberately recognises almost nothing else: a
worker that explains itself is left alone.

**It is NOT the same as library validation**, which shares the "not valid for use in process" prefix
and says `mapping process and mapped file (non-platform) have different Team IDs` instead. That one
means the hardened runtime is on without
`com.apple.security.cs.disable-library-validation` — a packaging fault, not something a user can
clear, and clearing quarantine will not touch it. The two phrases are matched separately for that
reason.

**None of this applies to `senior-worker`.** Its models are Linux libraries loaded by a Linux process
inside the VM, where macOS quarantine has no meaning at all.

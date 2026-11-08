# osdi-worker — resolved findings

## No Verilog-A model had ever loaded on Linux (GitHub issue #5, 2026-09-29)

A Linux user reported that every Verilog-A model — a `.va` compiled by circuitRF and a `.osdi` they
compiled themselves — failed with `osdi-worker: dlopen failed: <model>.osdi: undefined symbol: log`.
Two independent defects, both Linux-only, both invisible on the macOS machine everything had been
verified on. The second sat directly behind the first, so fixing only the reported one would have
turned a clear refusal into a crash on the very next request.

**1. libm was never in the worker.** OpenVAF's output calls `exp`/`log`/`log10`/… as undefined
symbols and lists only `libc.so.6` as NEEDED — it relies on the host already having libm in its
global scope, as a simulator linked against it does. `build.sh` passes `-lm`, but the worker uses no
libm function (`isfinite` is a macro), so an as-needed link drops it: the zig-built release workers
list `libc.so.6 libdl.so.2` and nothing else. A plain Debian `gcc` link does NOT use as-needed and
keeps libm, which is why a quick container build can make the bug look absent — link with
`-Wl,--as-needed` to see it. Fix: `dl_open` loads `libm.so.6` with `RTLD_GLOBAL` once before the
first model, so the outcome no longer depends on how the worker was linked.

**2. `OSDI_DESCRIPTOR_SIZE` was read as a `size_t`.** The compiler exports it as a 4-byte `uint32_t`
(`readelf --dyn-syms` on an OpenVAF-built model: size 4, beside the other uint32 exports), and the
test model already declared it so. The worker read 8 bytes. On macOS the neighbouring 4 bytes
happened to be zero; on Linux they were not, the descriptor stride became enormous, and `describe`
segfaulted (SIGSEGV, empty stderr — "the worker closed its output"). AddressSanitizer names it
immediately as a global-buffer-overflow at the read.

**How it was verified without a Linux machine.** Docker Desktop's `gcc:13` image, both
architectures (`--platform linux/amd64` runs under emulation): `verify.py` end to end against the
zig-built release worker, the same under ASan, and a real OpenVAF model using `exp`/`log`. The macOS
`openvaf-r` cannot LINK a Linux target (no linker for it) but leaves its `.o` files behind after the
failure; `gcc -shared` over those in the container gives a genuine Linux `.osdi`. The pre-fix worker
reproduces the reported `undefined symbol` message exactly against it.

**Not covered by any automated test.** The repository's tests run on macOS only, where neither
defect can appear. A Linux CI leg running `verify.py` against an as-needed-linked worker would hold
both shut.

## Any `$warning` or `$fatal` in a model killed the worker (brief-modelcomposer-26, 2026-10-21)

A compiled model sends every display task (`$display`, `$strobe`, `$warning`, `$error`, `$fatal`) through one function
pointer, `osdi_log`. The library exports the pointer and the HOST must fill it in. The worker never did, so the first
message a model printed called through null. The worker then died with an empty stderr, which circuitRF reports as a
worker that closed its output. Models whose own text never prints anything were unaffected, which is why nothing caught
it. The Verilog-A export writes a `$warning` for a model used outside its temperature and a `$fatal` for a constituent
that is not finite, and both crashed the worker.

**Fix.** `load_library` installs `crf_osdi_log` when the library exports the pointer. It writes
`osdi-worker: model <level>: <message>` to stderr. circuitRF already attaches stderr to a refused point, so a `$fatal`
reaches the user as the model's own sentence. Three details:

- **Each distinct message is written once.** The worker evaluates every point as a DC operating point, so a model's
  `@(initial_step)` block runs on every evaluation. A harmonic-balance solve is thousands of evaluations, and writing
  each one would fill the pipe and bury the line that matters. A `$fatal` is always written, because its point is
  refused.
- **The message is the compiler's `malloc`'d string, and the worker frees it**, except under `LOG_FMT_ERR`, where the
  compiler passes the format string itself because formatting failed.
- **The level is `lvl & 7`.** `osdi.h`'s `LOG_LVL_MASK` is 8, which masks nothing, and the header is the ABI contract and
  is not edited.

`EVAL_RET_FLAG_FATAL` was already read: the point's status is 0 and its block is left empty. Gate:
`tests/Ui.Tests/ModelComposer/VerilogA/CompiledVerilogATests.ANonFiniteConstituent_IsRefusedNamingItAndThePoint`.

## A model that refuses to run stops the run with its own sentence (brief vendorlib-15 R-vl15-2, 2026-11-03)

A maker who restricts use (an expiry date, a node lock, a licence file) builds that check into the compiled model. What
circuitRF owes them is that a model which refuses to start stops the run, and the user reads the model's own sentence.
The fake library gained a device for this, `crf_refuse`, whose model parameter `mode` picks how it refuses: `1` logs an
error and reports `num_errors` from `setup_model`, `2` logs a fatal and returns `EVAL_RET_FLAG_FATAL` from every `eval`,
`3` logs a fatal and calls `exit(3)` inside `setup_model`. The library now exports `osdi_log`, as a compiled model does.

The worker itself needed no change. The proof found three defects on the host side:

- **An all-refused DC solve was returned as an operating point.** `NonlinearDcEngine` takes a refused point as a step
  to shorten, which is right while some point has succeeded. When a model refuses the FIRST point and every one after
  it, the solve gave up after about a dozen tries and returned a non-converged result. Headless, that printed a node
  table of zeros and exited 2. In the GUI it was a convergence warning. Such a solve now raises the model's refusal as
  an `ExternalDeviceException` ("DC stopped: a model refused every point it was asked to evaluate …"). A model that
  refuses SOME points behaves as before. Two existing tests asserted `Converged == false` for this all-refused shape
  and now assert the refusal: `ThermalNodeRobustnessTests.AStartingPointTheModelRefuses_IsStillAFailure` and
  `VerilogALinearChargeTests.AnUnsetBiasPoint_IsRefusedWithTheSentence`.
- **The model's sentence came last.** It reached the user only inside `Worker output:`, after the worker's own clause
  ("create: model setup reported errors") or after a failed point's list of three possible causes. Both are true and
  add nothing the sentence does not say. `WorkerOutputDiagnosis.LedByModelsOwnWords` now puts
  `The model refused to run: <sentence>` first, for a channel refusal, a worker exit, and a failed point alike. Only
  `model error:` and `model fatal:` lines lead. A warning never does, so it cannot be mistaken for the reason.
- **An in-band refusal read the worker's stderr without waiting.** `DeviceWorkerChannel.Failed` used
  `RecentErrorOutput` for a refusal sent in-band. The model's sentence is flushed before the reply, but on a different
  pipe read by a different thread, so under load the refusal could be composed before the sentence arrived. It now uses
  `ErrorOutputAfterRefusal`, the bounded (100 ms) settle wait a failed point already used.

Smaller fix: a `$fatal` is printed on every point it refuses, so the attached output repeated one sentence a dozen
times. Consecutive identical lines are now written once with `(N times)` (`WorkerOutputDiagnosis.CollapseRepeats`).

**Found on the way, not fixed:** `src/Cli/bin/<config>/net10.0/osdi-worker` is not refreshed by building `src/Cli`.
Only the application's build runs `build.sh --dest`. A stale copy there predated the `osdi_log` hook, so it printed a
model's text with no `model fatal:` prefix. `VerilogARefusingModelCliTests` moves the checkout's own worker in by
rename before it runs.

Gates: `tests/Core.Tests/Devices/External/OsdiRefusingModelTests.cs` (the provider over a real worker, all three cases)
and `tests/Engine.Tests/External/VerilogARefusingModelCliTests.cs` (the `dc` verb as a process: exit 1, the model's
sentence on stderr's first line, no node table, a 60 s bound). In the GUI the same exception text reaches the Messages
panel as an Error through `CircuitEvaluation`'s per-analysis catch: elaboration failure, or `'<analysis>': <message>`,
then `RunStatus.EngineError` and `WorkspaceViewModel`'s `live.Finish(MessageLevel.Error, …)`.

## Every model that uses `$limit` crashed the worker: VBIC, HICUM/L2 (2026-11-05)

A compiled VBIC 1.3 (the bipolar model of SiGe BiCMOS kits) failed its first DC point with
`DC: DID NOT CONVERGE, residual inf`; the worker had died with SIGSEGV. Three host duties were unmet,
and a `$limit` model needs all three on every eval, whether or not limiting is enabled:

**1. The limit table was never filled.** A model that calls `$limit` exports `OSDI_LIM_TABLE` with
every `func_ptr` NULL, for the host to fill, and calls through it unconditionally. The worker now
installs an identity per arity (`install_lim_table`). Identities and not real `pnjlim`: a limiting
host must also load `load_limit_rhs_*`, and this worker loads neither, so an identity is the only
choice that keeps the model's bias and the solved bias the same (see the comment above `crf_lim0`).
The signature is `double f(bool init, bool *check, double vnew, double vold, <num_args doubles>)`,
as the OSDI 0.3 specification has it. Dropping `init` still works on
System V targets, where integer and floating arguments use separate registers, but on Windows x64
registers go by position and the function would hand back the wrong one.

**2. `prev_state`/`next_state` were NULL.** The model reads its last limited value from `prev_state`
and writes the new one to `next_state`, through the instance's `state_idx` table. Each instance now
owns two `num_states` vectors, swapped after each point, and freed on `destroy`.

**3. `state_idx` was never written.** Like `node_mapping`, it is the host's to fill; left at calloc's
zero, every state aliased slot 0. The worker now writes the identity.

PSP103, R3 and the varactor models export no limit table, which is why only bipolar models crashed.

Gate: `tools/fake-osdi-model`'s `crf_fet` now calls a `pnjlim` entry and keeps one state, as compiled
code does, so `verify.py` and every C# test that evaluates `crf_fet` through the real worker
(`OsdiWorkerTests`, `WorkerOpVarProtocolTests`, `ExternalDeviceHbDcConsistencyTests`,
`ExternalDeviceBatchingTests`) crash on a worker without the fix.

## `$simparam("shrink")` was answered 1.0, so a compiled resistor came out 1 % small (2026-11-08)

An open process kit's compiled R3 resistor, drawn 20 µm × 2 µm, read 74.1482 Ω at 27 °C where an
independent SPICE simulator reads 74.1518 Ω. Its op-vars gave leff = 19.8 µm and weff = 1.99 µm,
against 20.0 and 2.01 µm there. R3 declares a model parameter `shrink`, in percent, whose default is
`$simparam("shrink", 0.0)`, and scales every drawn dimension by (1 − 0.01·shrink). The worker's table
answered `shrink` with 1.0, so every such device was set up 1 % short and narrow. Stating `shrink=0`
on the device gave 74.15175 Ω.

**Fix.** `shrink` is no longer in `g_sim_names`, so the model's own default stands. The Verilog-AMS
LRM lists it among the optional simulator parameters, a shrink of instance geometry, and the
reference simulator does not answer it (table below). Answering 0 would have fixed R3 too, but would
zero the geometry of a model that reads it as a factor; leaving it out is right either way.

**Every entry, probed.** One Verilog-A module reads each name with a fallback of −999 and reports
it as an op-var. It was compiled once and run in both hosts ("—": not answered, the model's
fallback stands):

| name | reference simulator | this worker |
|---|---|---|
| `gdev` | 1e-12 | — |
| `gmin` | 1e-12 | 1e-12 |
| `imax` | — | 1 |
| `imelt` | — | 1 |
| `iteration` | — | 1 |
| `minr` | — | 1e-3 |
| `scale` | — | 1 |
| `shrink` | — | — (was 1) |
| `simulatorVersion` | 47 | 1 |
| `sourceScaleFactor` | 1 | 1 |
| `tnom` | 27 | 27 |

The rest are left as they are. `scale` 1 is the identity every model defaults to, and `minr` 1e-3
matches the fallback of the models it was added for. `imax` 1 A equals R3's own default, and nothing
in the kit's model sources reads `imelt`, `iteration`, `gdev` or `simulatorVersion`. But `imax` and
`imelt` override a model whose own default differs, where the reference leaves it alone, and
`iteration` is a constant rather than the solver's count. They are the first place to look if a
model's linearisation current or first-iteration behaviour ever disagrees.

**Gate.** `tools/fake-osdi-model`'s `crf_rc` gained two model parameters that default from
`$simparam`, scanning the host's NULL-terminated names as compiled code does: `gmin`, which the
worker answers, and `shrink`, which it must not. `verify.py` reads both back through `defaults`.
`shrink == 0` fails against the old worker, which gave 1. `gmin == 1e-12` shows the scan works, so
the first cannot pass because the scan found nothing. `OsdiWorkerTests.O1` lists the two parameters.

After the fix, with the kit imported fresh and DC through the CLI, the resistor reads 74.15174 Ω at
27 °C, 87.55906 Ω at 85 °C and 58.85028 Ω at −40 °C, against 74.15175, 87.55907 and 58.85030 Ω from
the reference (74.1482 at 27 °C before). A MOSFET's drain current is unchanged at 35.9392 µA.

**Found on the way, not fixed: the last 1.75e-7.** The two still differ by 1.750e-7 relative at all
three temperatures, so the cause scales neither with temperature nor with the temperature
coefficients. It is R3's weak depletion term. With the model switched linear (`sw_lin=1`) the
reference gives 74.151741294 Ω, and circuitRF gives 74.151741 Ω, the same current to the last bit
with or without the switch. So circuitRF's DC answer is the linearised one: the term is about 2e-10 A here,
which a DC stop rule on the residual can accept after the first Newton step. That is a solver
tolerance question, not the worker's.

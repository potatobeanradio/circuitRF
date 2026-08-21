# senior_worker — evaluating compiled device models

Some device models ship only as a compiled library. circuitRF never loads one itself, for two
reasons that are structural rather than stylistic:

- a compiled model calls back into the process that loaded it for services that process must export
  as **C symbols**, which a managed host cannot do;
- one process can hold exactly **one build of one library**, so several builds means several
  processes.

Both dissolve once the model lives in its own process. That process is this — a small C program
speaking circuitRF's device-worker protocol over stdio.

```
tools/senior-worker/senior_worker.c      the worker — ONE source file, three products
tools/senior-worker/crf-model-host.def   the Windows export list (and the mangled-name alias)
tools/senior-worker/build.sh             cross-compiles it   (build.sh linux | build.sh windows)
tools/senior-worker/ensure-built.sh      keeps it in step with `dotnet build`  (macOS, Linux)
tools/senior-worker/ensure-built.cmd     the same, on Windows
tools/senior-worker/verify-windows.sh    loads a model under Wine and checks what only running can
```

## Build — it happens on its own

`dotnet build` / `dotnet run --project src/Ui` keeps this in step and copies the products next to
the application. A contributor on **any** platform gets a working worker from a plain `dotnet run`,
so "it works on my machine" does not depend on who built it.

**It can only ever warn.** The worker is optional — circuitRF builds and runs without it, and a
design using no compiled device models never notices — so a missing cross-compiler, no network for a
first image pull, or a compile error prints a message and the build still succeeds. A missing worker
must never be the reason somebody cannot build the application. Skip it with
`-p:CrfSkipDeviceWorker=true`.

The products land beside the assemblies because that is where `DeviceWorkerManifest.ToolsDirectory`
looks; on macOS it is also the directory the VM host shares into the guest, and on Windows it is
where the launcher stub finds `crf-model-host.dll` beside itself.

Whichever of these is present does the cross-compiling:

| | cost |
|---|---|
| `zig cc` | seconds, no daemon, no image pull |
| `docker` / `podman` | pulls a small `gcc` / `mingw-w64` image the first time |
| host compiler | when already on a matching target |

**On macOS the Linux worker runs inside the Linux VM `crf-vmhost` supplies** — nothing on macOS can
load a Linux ELF, and there is no macOS build of these libraries to load instead.

## Windows: two products, because a model imports its host by NAME

The tempting assumption is that a Windows worker is a port — swap `dlopen` for `LoadLibrary`, walk
the PE export directory instead of the ELF symbol table. Those parts *are* a port. The part that is
not is visible in the library:

| | how the model finds its host callbacks |
|---|---|
| Linux `.so` | *undefined* symbols, resolved against whatever loaded it. The worker supplies them; that is what `-rdynamic` is for. |
| Windows `.dll` | *imports by name* from a named module (a named host module). |

An executable exporting those symbols does not satisfy an import-by-name from a named module — the
loader looks for a *module*, and an EXE's exports are never consulted. So a DLL under that name must
exist at load time. Hence two products from the one source file:

```
crf-model-host.dll   -DCRF_HOST_DLL    the 15 callbacks, the protocol, and crf_worker_main
senior_worker.exe    -DCRF_HOST_STUB   derive the name, stage the DLL under it, load it, call in
```

**Why the logic lives in the DLL and not the EXE.** The callbacks are not pure — they write worker
state (`g_I`, `g_Q`, `g_G`, `g_C`, `g_curv`, `g_delay`, …). If the callbacks lived in the shim and
the state in the executable, every one of them would need a registration handshake and a forwarding
thunk. Keeping the callbacks and the state they touch in one module makes the handshake disappear.

**Do not fork the source.** Two `#define`s over one file; a forked worker is two implementations of
a wire protocol to keep in step, which is the failure this repo already avoided once by making
`tools/DeviceWorkerExample` reference nothing.

### The three things that are easy to get wrong

1. **The module name is read out of the model, never remembered.** `derive_host_module` parses the
   model library's PE import table and picks the descriptor that imports **our own ABI symbols**.
   Matching a remembered module name would put kit knowledge back in one string at a time and would
   silently serve nothing for a kit that names its host module differently. Nothing in this
   repository is built under a vendor's module name, and nothing is shipped under one: the file
   bearing it is created on the user's machine, from their own kit.

2. **The staged shim is loaded EXPLICITLY, before the model.** `LoadLibraryW(<staged path>)` and
   only then `LoadLibraryW(<model>)`. Windows resolves an import by first checking whether a module
   with that base name is *already loaded*, so the model binds to the shim with no
   `SetDllDirectory`, no `AddDllDirectory` and no `PATH` edit. This is what makes the mechanism
   small; the search-path approaches all work by accident of ordering and fail when something else
   on the machine gets there first.

3. **stdin and stdout go into binary mode before the first frame.** Windows stdio defaults to text
   mode and would translate `\n` to `\r\n` **inside the raw-doubles payload**, corrupting numerics
   in a way that reads as a model producing wrong answers rather than as a transport fault. It does
   not show up in a `describe` round trip — only once real doubles cross.

The staged copy lives in `%LOCALAPPDATA%\circuitRF\hostshim\<hash>\<derived-name>`, refreshed when
the shipped `crf-model-host.dll` is newer. Never the repo, never the install, never the kit: a kit
is read-only and an install may sit under `Program Files`.

### Why this is a separate process on Windows too

On Windows the "a managed host cannot export C symbols" argument genuinely dissolves — the callbacks
come from a named module, so it no longer matters who called `LoadLibrary`, and `circuitRF.exe`
could host the model directly. It is still the wrong choice, for reasons that survive:

- a faulting model library would take the UI and the unsaved workspace with it;
- Windows keys loaded modules by base filename, and a kit ships one library name across **ten**
  build directories — so two kits could not coexist in one process;
- **an x64 worker EXE runs under emulation on ARM64 Windows and loads the x64 DLL**, where a native
  ARM64 host could not load it at all. Process separation is what makes ARM64 Windows work, not what
  costs it.

**Not WSL, either.** Running the Linux worker under WSL needs almost no native work, but makes a
mainstream desktop platform depend on installing WSL and strands ARM64 Windows entirely. Recorded as
a considered alternative, not a fallback to reach for.

## Kit-agnostic by construction

Nothing here names a vendor, a library, a family or an offset.

- **Every entry point is found by walking the library's own symbol/export table** — not from a list
  of names compiled in, which would have to be edited for every library that ever ships and would
  silently serve nothing for one it had not heard of. The library already states what it has;
  `find_boot_symbols` reads it, via `DT_HASH`/`DT_GNU_HASH` on ELF or the export directory on PE
  (skipping forwarder RVAs, which point at a string rather than at code).
- **The host module a Windows model imports from is read out of that model** (above).
- Each entry point's `load_elements` callback hands over its element array, carrying the family
  name, node counts and parameter table.
- Internal node count and the analyze function pointers are read out of the device struct rather
  than from a per-family symbol offset, so it generalises for free.

**The one thing that cannot be derived** is which node a *degenerate* node follows. Probing finds
identically-zero Jacobian rows and so knows **which** nodes are degenerate — but not what each
replicates, and the library does not state it. So it is supplied as data at run time:

```
senior_worker <model-library> [alias-map.json]

    { "FAMILY_NAME": { "6": 5, "7": 4 } }
```

A family with no entry reports `slavedTo = null` and the client refuses to solve rather than
silently produce a dead device. **That failure is loud on purpose** — a device that never conducts,
converging beautifully, is the worst outcome available here.

## Three things that are load-bearing, all found by measurement

1. **`-rdynamic`** (Linux). A compiled model resolves its host services back into whatever process
   loaded it, so those symbols have to be in this executable's *dynamic* table. Without it `dlopen`
   fails outright with `undefined symbol: send_error_to_scn`, naming a function that is plainly
   right there in the file.

2. **Symmetry, not magnitude, discriminates a thermal node.** A conductive path shows a reciprocal
   Jacobian pair; a thermal coupling is strongly one-way. Comparing the two entries separates them
   where comparing either against a threshold cannot.

3. **UCRT is a property of the TOOLCHAIN, not a `#define`.** Adding `-D_UCRT` by hand sends the
   headers down the UCRT path while the link still resolves against msvcrt import libraries, and the
   build fails on `__intrinsic_setjmpex` — confirmed directly on gcc-mingw-w64 13, not reasoned
   about. To get UCRT, use a UCRT-targeting toolchain (MSYS2's `ucrt64` gcc). Running against msvcrt
   is not a hazard on its own, because nothing heap-allocated or `FILE*`-shaped crosses the ABI
   boundary — only `const char*` and `double`. **Do not pass ownership of memory across it.**

## Verified

**Against a compiled model library (Linux):** built from this directory and run against it, every
family the library serves is found from the symbol table alone, with no names compiled in. The worker
reports each family's external and internal node counts and its parameter count, then `worker ready`
with the total — all of it read from the library at run time.

**Against `tools/fake-model-lib` (Linux), a full request/reply exchange:** `describe` reports
`CRF_TEST_V1` with its parameter and node counts, `create` succeeds, `probe` classifies both nodes
as non-degenerate and conductively coupled, and a batched `eval` returns exactly `G·V` with the two
terminal currents equal and opposite.

**On Windows, under Wine (`./verify-windows.sh`):** the whole mechanism runs. An unprivileged user
with a **read-only** install directory gets the host module name read out of `fake_model.dll`'s own
import table (`crf_test_host.dll`), the shim staged under it in `%LOCALAPPDATA%`, the model loaded
with its import bound to that already-loaded module, the family found by the PE export walk, and a
full `describe` → `create` → `eval` exchange whose currents are bit-exact. A second model naming a
different host module stages alongside the first, and a newer shipped shim refreshes the staged copy.

**R-win-7 is proven load-bearing, not assumed.** The same script builds a control with the two
`_setmode` calls neutralised and nothing else changed, and runs the identical payload through it: the
doubles come back corrupted and the stream desyncs. `describe` passes identically in both runs —
which is precisely why this bug is easy to ship and why a `describe`-only test could never catch it.

**What is still NOT proven, and only a real Windows machine with a kit can settle it:**
whether the 15 symbols are *sufficient* (they are demonstrably necessary); a CRT mismatch against a
UCRT-built library; whether a library's own additional exports want anything at load time (they
should be self-contained, but they are the first place to look if a load fails with all 15
satisfied); and the vectored exception handler under a real access violation. Wine is a
reimplementation and the fixture is ours; a PASS there exercises the mechanism, it does not stand in
for a real library.

## The shim is an owner decision, taken knowingly

Authoring a compatibility shim under a vendor's module name is recorded here so the decision is
visible rather than inferred from a filename. We implement all 15 functions ourselves — we already
did, for Linux — redistribute none of their code, and the name functions as an ABI identifier. It is
the ordinary shim pattern.

## Not the second ABI

A delivery can contain libraries built against a completely different callback set, with its own
entry points and its own symbol count. `DeviceLibraryDiscovery.Profiles` is a list precisely so a
second profile can be added — but that is a distinct worker on **every** platform and has nothing to
do with Windows. Out of scope here.

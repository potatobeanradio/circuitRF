# circuitRF — PDKs and External Device Models

**Status:** Shipped · **Date:** 2026-08-01 · Commits `ba537df` (infrastructure), `b8f8847` (workers,
Windows/Linux/macOS, VM)

How circuitRF imports a vendor process design kit and evaluates the compiled device models it ships,
without linking against them, loading them, or knowing anything about any particular vendor.

Companion to `pdk-import.md` (what an import writes into a workspace, and the in-memory design
replacing it), `data-model.md` (`ComponentModel`, ports, stamps), `hierarchical-net-extraction.md`,
and `workspace-and-project-tree.md`. Implementation notes live in `src/Core/CLAUDE.md` and
`src/Ui/Schematic/CLAUDE.md`.

---

## 0. The rule that shapes everything here

**No vendor or product names appear anywhere in circuitRF** — code, comments, tests, fixtures, log
output, or documentation. A kit's identity is *data it supplies at run time*, never something written
down in the product.

That is not a naming convention; it is a design constraint, and every mechanism below was chosen over
an easier alternative because of it. Four times over:

| What circuitRF does | Instead of |
|---|---|
| Scans a library's own export table for the ABI entry points | A compiled-in list of library names |
| Reads the host module name out of the model's own PE import table | A remembered module name |
| Reads node aliases from a run-time data file | A compiled-in table |
| Classifies a kit's device types structurally (§3.2) | A list of known type names |

The test that this is real: a synthetic fixture (`SampleKit`, `KITLIB_DEVICE_v1`) exercises the same
code path a kit does, because recognition is structural. Where a real measurement must be cited,
cite the number, not the kit.

**Corollary — anything a kit must declare and circuitRF cannot derive is DATA beside the kit**, never
knowledge inside the product. That is what `device-provider.json`, the variant declarations and the
alias map all are.

---

## 1. The shape of the problem

A compiled device model is a shared library the vendor built for their own simulator. Two properties
of that arrangement — not of any particular model — decide the entire architecture:

1. **The model calls back into whatever process loaded it**, for services that process must export as
   **C symbols**. A managed host cannot export C symbols to a native library's undefined references.
2. **One process can hold exactly one build of one library.** Several builds means several processes.

Both dissolve once the model lives in **its own process**. So circuitRF loads nothing and links
against nothing: it starts a small C program — a *device worker* — that owns the model and answers
questions over a pipe.

Everything else here follows from that one decision.

---

## 2. Layers

```
 Schematic          a kit part is an ORDINARY CELL REFERENCE  (§3.1)
   │
 NetExtractor       → ExtDevice instance, or a netlist-backed cell instance   (§3.3)
   │
 Elaborator         node layout, internal nodes, slaved nodes                 (§5)
   │
 ExternalDeviceModel  a ComponentModel like any other                         (§4)
   │
 IExternalDeviceProvider  ← the seam. In-process or out, circuitRF cannot tell (§4)
   │
 DeviceWorkerProvider   request/reply over a framed pipe                      (§6)
   │
 IDeviceWorkerTransport   child process, or a VM on macOS                     (§7)
   │
 the worker          C program owning the compiled model                      (§8)
```

The seam that matters is `IExternalDeviceProvider`. Above it, an external device is an ordinary
nonlinear component. Below it, the fact that a separate process and possibly a virtual machine are
involved is invisible.

---

## 3. Import — reading a kit

### 3.1 A kit part is an ordinary cell reference

The load-bearing decision. A cell reference is *already* the component whose artwork lives in an
external file and resolves at render time — so placement, rendering, pin geometry, hit-testing, net
extraction and the symbol editor all work on kit parts **unchanged**. There is no "external part"
species, and there must not be one: a parallel render path would duplicate all of that and drift.

Symbol translation (`DsnSymbolReader`) reads the record-based symbol-description **format**, not any
part. Two conversions are load-bearing and fail silently if wrong:

- **Y is negated** — the file is Y-up, symbol-local is Y-down. Because a flip is a *reflection* it
  also reverses arc handedness, so arc start angle and sweep are both negated. Getting this wrong
  still draws an arc, just mirrored — which survives review, hence a dedicated test.
- **Pins snap to P=100** after scaling, because `SymbolModel` requires it. Two pins colliding after
  snapping are both kept and **reported**, never silently merged.

Scale is a power of ten chosen from the file's own declared view bounding box, targeting a
300–30,000 local-unit extent — so a kit authored in a different drawing unit lands legible without the
reader knowing anything about that kit.

**Text is anchored from the object's bounding box, deliberately not from the text record's own x/y** —
those are min-corner in some files and centre in others, distinguished only by an undocumented flag.
The box is unambiguous everywhere.

**Two artworks, two jobs:** the kit's `.bmp` browser icon becomes the palette tile; the vector symbol
goes on the schematic. Each is used for what it was drawn for.

**A kit part is identified by kit + part id, never by `SymbolKind`** — every kit part shares one kind,
so an identity check on kind alone lights up every kit tile at once.

### 3.2 Which library implements a kit's devices — established, not read

A delivery is several read-only kits beside one shared library package, and **no kit says which
library implements the device types its netlists name**. A host simulator resolves them by
name across everything loaded. `DeviceLibraryDiscovery` closes that gap:

- **The types wanted** are the references a kit's netlists name but do not define. A kit's cell
  instantiates exactly three kinds of thing — circuitRF primitives, other cells the same kit defines,
  and its own compiled models. The first two are recognisable, **so whatever is left is the third.**
  Structural, therefore kit-agnostic.
- **A library is recognised by the entry points OUR worker will call.** That is a fact about
  circuitRF's own worker ABI, not about any vendor.
- **A plain byte scan, not an ELF/PE/Mach-O parse** — an exported name sits verbatim in all three
  formats, so one scan handles the Linux, Windows and macOS builds a vendor ships side by side.
- **The search widens only when the narrow one finds nothing** — the imported folder first, then a
  bounded walk outward, because the library is routinely a *sibling* of the kit. Searching every level
  at once lets unrelated territory compete with what is sitting next to the kit; that was a real
  defect, caught when two test fixtures under `/tmp` found each other.
- **Same file name = one library built for many toolchains** (a real delivery ships 14). Ordinary, so
  the most specifically named build wins and the choice is reported. Different file *names* is genuine
  ambiguity — it would change which model evaluates the design — and is refused.
- **One search PER TARGET, not one for the host.** A manifest describes every platform at once, so the
  Windows entry must name the Windows build even when the import happens on a Mac.

The format is decided by **magic bytes, not extension** — a vendor ships `.so` files that are PE, and
ranking-without-filtering once made a Linux-only kit answer a Windows search with the same file.

### 3.3 Three ways a kit part becomes something the engine can run

| The part is… | Emitted as | Why |
|---|---|---|
| A leaf backed by a provider | one `ExtDevice` instance | the cell has a symbol and deliberately no schematic |
| A packaged part with a circuit | an ordinary **cell instance** | a worker evaluates *one* device; a package is several plus passives |
| Written natively in the kit's netlist | rewritten to `ExtDevice` before the cells are copied | the kit names its own compiled types; whatever isn't a primitive or a kit cell is one |

**Circuit beats device, and a failure on that path is terminal** — a netlist-backed part is never
quietly re-emitted as an `ExtDevice`, because that would answer with something the user did not place.

**An unconnected pin is not an error here.** The engine's mapping makes every node its own
ground-referenced port, so an open thermal terminal is ordinary and correct — it just gets its own
auto-named net.

### 3.4 Data files a model opens itself

A compiled model is told which data files to read through its *own parameters*, using paths relative to
the vendor simulator's data search path — which circuitRF does not have, and which is **not** the
netlist's own folder (kits keep netlists and data in sibling folders).

`KitDataFileResolver` searches around the netlist rather than resolving against one root, and
**rewrites nothing unless a real file is found** — which is what makes it safe to try on every value
instead of on a guessed list of parameter names. The search bound is the point, not a limit to relax:
one level too far starts listing a home or temp directory, and a value that happens to match something
in there resolves to a file the kit never named.

### 3.5 File classification is open

`IPdkFormatRecognizer` is a registry, not a switch. circuitRF ships recognisers for formats that are
industry standards or plainly self-describing; a host or a provider registers more. Recognisers may
**look inside** a file rather than trust its extension. This is what lets a kit be imported without
circuitRF knowing the supplier.

---

## 4. The provider seam

`IExternalDeviceProvider` is where circuitRF stops knowing anything:

```csharp
string Name { get; }
IReadOnlyList<ExternalDeviceDescriptor> Describe();
IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string,string> parameters);
```

**Every device type, parameter name, pin count and node role is learned at run time from `Describe()`.**
circuitRF hardcodes none of them. `TypeId` and `DisplayName` are opaque: rendered, never interpreted.

An implementation may be an in-process model or a proxy for something out-of-process — **circuitRF
neither knows nor cares which.**

### `ExternalDeviceModel` — an ordinary `ComponentModel`

Four properties make this fit the engine with no engine change:

**Node-referenced ports.** A provider reports currents per *node* and derivatives per node *pair*,
while `ComponentModel` speaks in ports spanning a node pair. They reconcile exactly when every node is
its own port referenced to ground: the elaborator lays the array out as `[n₀, 0, n₁, 0, …]`, so
`PortVoltages[k]` *is* node k's voltage, `I[k]` the current into it, `Dg[k,l] = ∂I[k]/∂V[l]`. No
translation layer — the same ground-referenced convention frequency-domain N-ports already use.

**Passive sign convention, applied nowhere.** A provider's current is positive flowing *into* the
device, which is exactly what `NonlinearDcEngine` stamps (`f[node] += i`). Nothing is negated. This is
checked against behaviour, not assumed from documentation: at a drain bias the drain current comes back
positive while the device sinks it, and the thermal node's current comes back negative with magnitude
equal to the dissipated power — power leaving the device. Both agree, in the direction needing no flip.
**A second flip applied "to be safe" would invert every operating point while still converging**, which
is why this is written down rather than left to be inferred.

**Internal nodes are real unknowns**, with their own rows in the global matrix. Deliberately *not*
eliminated locally: Schur-reducing them here would be simpler and is wrong for harmonic balance, where
an internal node voltage carries its own harmonic content and must be first-class.

**`Stamp` contributes nothing, and is an empty override rather than an inherited throw** — the
S-parameter engine makes a preliminary pass over *every* component to count and label branch unknowns,
and that pass reaches nonlinear devices too. Refusing there fails the analysis before it starts.

**Errors name the instance.** A worker can only name the *type*, which is useless the moment a design
holds several devices of one type — a real package holds five, wired differently. `ExternalDeviceModel`
is the only layer that knows which instance failed, so it adds the label.

### Batch evaluation is the load-bearing API, not a convenience

Harmonic balance evaluates every device once per harmonic sample per Newton iteration. A per-evaluation
round trip to an out-of-process provider would make the *transport* the simulator. `EvaluateBatch` must
be a single round trip for any provider carrying real transport cost; the default one-at-a-time
implementation is correct only for in-process providers.

---

## 5. Elaboration — nodes, internal nodes, and slaved nodes

`BuildExternalDeviceNodes` lays out the ground-referenced pairs, mints internal nodes, and resolves
slaving.

**A node is *slaved* when it is not an independent unknown** — its row in the device's own Jacobian is
identically zero, so solving for it makes the system singular. The descriptor reports `SlavedTo`.

**Slaved nodes cost nothing.** A slaved node is given its master's node index instead of a fresh one,
and the engine's existing four-way port stamp then folds the chain rule by itself: the slaved row is
identically zero and adds nothing; the slaved **column** lands on the master's column — exactly what
slaving requires. No special case in the model or the engine.

**A slaved node is never minted.** Minting one first leaves an unknown nothing references — an all-zero
row *and* column, which is the definition of a singular matrix. DC hides it (gmin holds the orphan at 0
and nothing reads it), so it surfaces only in the S-parameter assembly, as a singularity naming nodes
the user cannot find in their schematic **because they do not exist in it**.

**Chains and self-reference are hard errors.** So is a node reported degenerate *without* naming what it
follows — circuitRF refuses rather than guessing, because the alternative is a silently dead device.

**Most `ExtDevice` parameters are not expression-evaluated.** They belong to the provider: `Provider`
and `Type` are names, and a provider may declare file paths or enum values (a leading `/` alone crashes
the expression parser at position 0 — the same trap `SnP`'s `File=` hit). Rule: text that parses as a
plain number is stored as a number, so units and arithmetic still work for genuinely numeric values;
everything else is stored verbatim, and the provider does its own conversion.

---

## 6. The worker protocol

```
[ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes UTF-8 ][ binLen bytes of doubles ]
```

**Control plane is JSON; bulk numerics are raw little-endian doubles.** Control stays readable in a hex
dump when something goes wrong; numerics cost no parsing. **Measured, that split is ~24×** — at one
round trip per evaluation the transport, not the model, becomes the simulator.

`binLen` is a **byte** count, not a value count — it is what the reader must consume, and a length in
elements would be ambiguous the moment anything but a double is carried. A frame claiming more than
512 MB is refused as a desynchronised stream rather than allocating gigabytes on a corrupt number.

Commands: `describe`, `create`, `probe`, `eval`, `destroy`, `shutdown`.

**One request at a time, enforced by a lock.** Two threads writing frames into one pipe interleave them
and the worker reads a header out of the middle of somebody else's JSON — a desync that presents as
corrupt numerics much later. A correctness requirement, not a convenience.

**The worker's own stderr is drained on a thread and attached to errors.** Nobody reading it means the
pipe fills and the worker blocks forever inside a write — a hang partway through a long solve with no
error anywhere. The drain also gives failures the worker's own explanation, which is usually the only
place a refused evaluation says *why*.

### `probe` — measuring what the model will not declare

`cmd_probe` perturbs each node and compares the model's analytic Jacobian against finite differences of
its own currents. It reports, per node: degenerate (row identically zero), conductively coupled
(symmetry, **not** magnitude — a conductive path is reciprocal while a thermal coupling is strongly
one-way), and therefore thermal-vs-electrical.

These roles are **measured and then decide how the device is stamped**. A misreading is invisible from
the host: the device stamps cleanly, every number is finite, and the only symptom is a solve that will
not converge.

### The alias map — the one thing probing cannot reveal

Probing finds *which* nodes are degenerate. It cannot find **which node each one follows** — that comes
from what the model does with the pair. So it is supplied as run-time data (`alias-map.json`), keyed by
family name, searched **kit folder first**, then beside the worker, then circuitRF's shipped fallback.

Measured with and without: **279,127 iterations at residual 35.6 → 5 iterations at 7.6e-12.**

Two findings worth keeping, both measured rather than reasoned:

- **The tell is `degenerate` (a zero row), not the ISOLATED/UNDRIVEN suffix.** On a kit the nodes
  needing an alias report **ISOLATED** — row *and* column exactly zero, with only a ~2e-08 gmin-like
  diagonal in the model's analytic `G`. An isolated node still needs an alias because an unknown is
  minted for it anyway, and a row held up by 2e-08 makes Newton's step in that direction meaningless.
- **The relationship is not derivable from the model's own delay declarations.** `get_delay_v(i, j, tau)`
  calls are recorded, but on a kit there was exactly **one** pair `(9, 6, 7.15 ps)` against two
  aliases `6→5` and `7→4` — node 7 appearing in no pair at all. It is a genuine transit-time delay, not
  a node-replica declaration. Route closed.

---

## 7. Reaching the worker — including on macOS

`IDeviceWorkerTransport` is a pair of byte streams plus enough identity to write a comprehensible error.
Two implementations: a local child process, and a VM.

### macOS: `crf-vmhost`

A model shipping only a Linux build cannot be loaded on macOS at all — a **binary-format and OS-ABI
mismatch, not an instruction-set one**, so nothing at the library level bridges it and there is no Linux
ABI personality to load it into. **A VM is the only mechanism**, and circuitRF ships one so the user
installs nothing.

`crf-vmhost` (Swift, Virtualization.framework) boots a kernel, mounts what it was told, runs one argv,
exits when that argv exits. It is not a container runtime and knows nothing about any model or vendor.

**Two virtio serial ports, and that is the whole reason for the design's shape:**

| | |
|---|---|
| `hvc0` | kernel console + guest stderr → host stderr |
| `hvc1` | guest program stdin/stdout ↔ host stdin/stdout, **bytes untouched** |

Sharing one channel would inject boot chatter mid-frame and desynchronise the protocol — presenting as
corrupt numbers much later. `hvc1` is attached **directly** to the host's own handles: no byte is
copied, buffered or framed by the VM host, so there is no relay thread for the protocol to break in.
The guest puts `hvc1` in **raw mode** before the program sees it — a normal terminal's line discipline
rewrites newlines and steals interrupt characters, corrupting frames intermittently rather than
obviously.

**Rosetta is invoked directly as an interpreter**, not registered through `binfmt_misc`. On an Intel
host the guest is x86-64 already and needs nothing.

**Share/mount is one contract with two halves that must agree** — a host directory is offered as
`--share TAG=PATH`, and the guest sees it at `/mnt/TAG`. `VmHostArguments` writes both halves so a
caller cannot get them out of step. **A host path is meaningless inside the guest**; every file the
guest is told to open must have been mapped through a share first. Building one half alone produces a
command that starts perfectly and fails inside the guest naming a path that plainly exists on the Mac.

A kit's **data files are mounted at their own absolute path** (`--share-at`), because a model is told
which files to read through parameters that arrive from the netlist long after the VM has started —
unlike the model library, there is no command line left in which to rewrite them. So the path is made
*true* rather than translated.

---

## 8. The worker itself

One C source file, three products — **do not fork it**:

| Product | Built with | Role |
|---|---|---|
| `<worker>` (Linux) | — | the whole file compiles as one executable |
| `crf-model-host.dll` | `-DCRF_HOST_DLL` | the 15 callbacks, the protocol, `crf_worker_main` |
| `<worker>.exe` | `-DCRF_HOST_STUB` | derive the host-module name, stage the DLL, load it, call in |

**Why Windows splits.** A Linux model leaves its host callbacks *undefined* and the loader resolves them
against whatever loaded it (that is what `-rdynamic` is for). A Windows model **imports them by name from
a named module**, and an executable's exports are never consulted for a DLL's import-by-name — so a
module under that name must exist at load time. The callbacks are not pure (they write worker state), so
they and that state must live in the same module: **logic in the DLL, launcher in the EXE.**

**The module name is read out of the model's own PE import table, never remembered** — selected by
matching *our own ABI symbols*. Two implementations of that walk exist deliberately: the C one runs
inside the launcher before any managed code exists in its process; the C# one (`PeImports`) lets the rule
be exercised on every platform and lets an importer say whether a kit's Windows build is drivable at all.
Keep them in step.

**The staged shim is loaded explicitly, before the model.** Windows resolves an import by first checking
whether a module with that base name is already loaded — so no `SetDllDirectory`, no `AddDllDirectory`,
no `PATH` edit. The search-path approaches work by accident of ordering and fail when something else on
the machine gets there first. The staged copy is **per-user**, never the repo, install or kit.

**Kit-agnostic by construction:** every entry point is found by walking the library's own export table;
each one's callback hands over its element array carrying name, node counts and parameter table; internal
node counts and the analyse function pointers are read out of the struct rather than from a per-family
offset.

**The build is automatic and can only ever warn.** `dotnet build` keeps the worker (and on macOS the VM
host) in step and copies the products next to the application, via `zig cc`, Docker/Podman, or a matching
host compiler. A missing cross-compiler prints a message and the build still succeeds — **a missing worker
must never be the reason somebody cannot build the application.**

---

## 9. Zero-configuration path

The target is *import kit → place part → configure analysis → Run*, with nothing to configure between.

1. **Import** reads the kit, translates symbols, and — when the kit ships no `device-provider.json`,
   which is the *ordinary* case for an unmodified vendor kit — **synthesises one**, recording the library
   discovery of §3.2. Written into the workspace as ordinary JSON, because everything chosen
   automatically must be visible and one line to correct.
2. **Placement** seeds instance parameters from the cell's published interface, and a part that ships
   several formulations arrives on the choice that works, so the first Run answers rather than explains.
3. **Run** — a netlist names its provider; `ExternalDeviceRegistry` asks its resolver, which finds the
   manifest beside the kit and starts the worker. **A resolver, not a pre-registered provider**, so
   opening a workspace starts no processes and a kit the design never uses is never launched.

**Per-instance model-library override** rides in the *provider name* (`kit|path`), because that is what
the registry keys on — two instances naming different libraries must become two providers, or the second
would be silently evaluated by the first's models.

---

## 10. Known gaps

- **The `ModelLibrary` override reaches leaf provider-backed parts only.** A netlist-backed packaged part
  emits its `ExtDevice` instances from inside the cell, so the override lands on the package and does not
  flow down to the devices that need it.
- **Harmonic balance does not yet apply the per-harmonic delay rotation.** `DelayPairs` are captured and
  surfaced for it; nothing consumes them.
- **`.csch` carries vendor parameter names and values** by construction — see `pdk-import.md` §3.3. An
  accepted limitation, not a gap to close.
- **Vendor kits ship Linux and Windows builds only**, so macOS always goes through the VM. There is no
  macOS build of these libraries to load instead.

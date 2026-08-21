# netlist-worker — asking a compiled model library what its parts are

A kit can ship no netlists at all. Its parts then exist only inside a compiled model library, which
builds each part at run time by calling back into the simulator hosting it. There is nothing on disk
to read: the circuit exists for the duration of one call and is never written down.

This worker plays that host. It loads the library, lets it register its inventory, asks it to build a
named part, records the primitives and wiring the library asks for, and prints the result. What comes
back is an ordinary netlist made of primitives circuitRF already has.

**It is a second worker, not an extension of `tools/senior-worker`.** They solve different problems
and share no ABI. `senior-worker` *evaluates* a compiled model — it is handed voltages and returns
currents and derivatives. This one never evaluates anything; it asks a library to *describe* a
circuit and then gets out of the way. A kit needs one or the other, decided by what its library is.

---

## The rule this exists to enforce: no a priori knowledge

Every part's topology is read from the library itself, per part, at run time. Nothing is derived from
a rule, tabulated ahead of time, or remembered between kits.

That is not a stylistic preference. The alternative — decode a kit once, derive a wiring rule, and
generate netlists offline — was tried, and produced parts that simulated cleanly and were **wrong**:
a rule that reproduced every observed number while assigning those numbers the wrong meaning. It took
a datasheet comparison to notice. A rule that is never derived cannot be derived wrongly.

### The consequence: nothing here is named after a kit

A library resolves its host **by name** — named modules, named symbols — and those names are
properties of the kit on the user's machine, not of circuitRF. They are read at run time and never
written down here, the same arrangement `tools/senior-worker` uses for its staged shim.

What this source does know is ABI vocabulary: the *role* half of a host symbol, the part after any
prefix. It looks for a symbol ending in `AttachEleRecord`, one ending in `GetEleRecord`, and so on,
and takes whatever precedes it as the prefix. Those role words name operations, exactly as
`add_lin_n` and `get_delay_v` do in `tools/senior-worker/crf-model-host.def`. Run `--scan` and it
prints the prefix it derived; that string stays on the user's machine.

If a library names its roles differently, `--scan` says which roles did not bind, by name, instead of
loading a library that will half-work.

---

## How a library finds its host, and why that had to be measured

Two mechanisms exist, and they need different worker designs:

| | how the library reaches its host | how to answer it |
|---|---|---|
| **static** | the host symbols are in the library's PE **import table**, bound to a named module at load | supply a module of that name exporting those symbols |
| **dynamic** | `LoadLibrary` + `GetProcAddress` at run time; the names are not in the import table | intercept those two calls and answer whatever is asked for |

`--scan` classifies which one a library uses. **This is the first thing to run against any library,
because the answer decides everything downstream** — and it is not guessable. A library can carry
nothing but system imports, resolving every host callback **dynamically** at run time, with no host
module named in its import table at all.

That case is worth stating plainly because it contradicts the obvious assumption. `senior-worker`
handles a library of the **static** kind, and its `derive_host_module` reads the import table to find
the module name. The same approach against a dynamic library finds nothing at all — not an error, a
silence. Anyone extending either worker should run `--scan` first rather than reason from the other
one.

### What a dynamic library implies for this worker

The host module and symbol names cannot be read out of the image ahead of time, so there is nothing to
generate a `.def` from. The design that follows is to hook the library's own `LoadLibrary` and
`GetProcAddress` and answer by role suffix as the requests arrive — which is *more* blind than the
static route, not less, and needs no generated modules. The ordering problem it creates is the open
piece: registration happens inside `DLL_PROCESS_ATTACH`, so the hooks must be in place before the
library is loaded, and the library's import thunks do not exist to patch until it is. See Status.

---

## Commands

```
netlist_worker --scan <model-library>
        Read the PE import table and report: every module the library imports from, whether the
        host ABI is resolved statically or dynamically, the derived prefix, and which ABI roles
        bound. Maps the file as data and loads nothing, so no code in the library runs.

netlist_worker --gen-shims <model-library> <out-dir>
        For a STATIC library only: write one .def per host module, listing exactly the symbols that
        module is asked for, each aliased to the entry point implementing its role. Reports and
        does nothing for a dynamic library, which has no such names to read.

netlist_worker --list <model-library>
        Load the library with the host in place and report every element record it registered,
        every host symbol it asked for, and every interface it asked the host to supply.

netlist_worker --build <model-library> <part> [--iid <guid>] [--terminals <n>]
        Ask the library to build one part, recording every component it requests and every node it
        wires, and print the resulting netlist as JSON.
```

### Why `--build` has to be given an interface identifier

A record's factory slot hands back a model only to a caller that asks for the right interface, and
that identifier is 16 bytes compared at run time. It is not in the import table, it is not a string,
and it is assembled into no name — **no static scan can find it**, which is the same reason
`interfacesRequested` exists.

So it is supplied, not guessed: run `--list`, read `iidsAsked`/`interfacesRequested`, pass one back
with `--iid`. That keeps the identifier on the user's machine, like every other kit-specific name
here.

**A wrong identifier cannot produce a wrong netlist.** A factory that does not recognise it refuses
outright — `hr` is non-zero, no model comes back, and the run says so. That is exactly why it is
safe for this one value to have to be asked for, and it is the difference between this and the kind
of knowledge the tool refuses to hold.

### The two commands answer an unknown host entry differently, on purpose

`--list` answers **NULL**, which is what made the observed ABI grow from ten symbols to seventeen —
see the note further down. `--build` cannot afford that: a library that gets nothing back from its
host stops building. So `--build` answers a getter with a generic services object and an
acknowledgement-shaped entry with success, classified by the **verb** its name starts with. That is
the same kind of knowledge as the role suffixes — it names an operation, not a kit.

The services object matters more than it looks: it is what a library reaches for when it wants to
report *its own* errors, and denied one it faults inside its own diagnostics, a long way from the
cause.

### Why registration is a side effect of loading

The library's **export directory is empty**. It cannot be driven by calling into it. Instead it binds
to its host at load time and *pushes* its inventory out through the host callbacks. So loading is the
whole registration experiment, and a zero result is a real refusal rather than a call that was never
made.

### Why most host callbacks can be stubs

Almost all of them can return `NULL` or `1`; the library registers its whole inventory regardless.
Only these carry behaviour:

| role suffix | what the worker does with it |
|---|---|
| `AttachEleRecord` | keep the record — this is the library declaring one part |
| `RemoveEleRecord` | forget it |
| `GetEleRecord` | **answer it**: the kept records first, by name; failing that, a primitive |
| `GetCommonObject` | hand out the host object, which carries the library's own assertion channel |

**Lookup precedence is load-bearing and getting it wrong is silent.** The kept records must be
searched *before* the primitives. Reversed, a library asking for one of its own sub-parts is served a
primitive-shaped record, and the part still builds and reports success — a wrong circuit, reported as
correct. Composite parts are assembled by recursion through this one callback, so the precedence
decides whether a two-level part is built or quietly flattened into nonsense.

### Identity comes from RTTI, not from calling anything

A record's concrete class is read from the RTTI locator at `vtable[-1]` — pointer arithmetic against a
documented, fixed MSVC layout. Nothing is called to find out what a record is. This matters because
vtable slot *labels* recovered by static analysis are indicative only: anything that depends on
calling the right slot is a guess, and a wrong guess faults. Identify first, call later.

---

## Status

**Working and verified, against both a real library and the test double:**

- `--scan` — classifies the resolution mechanism. That classification is the finding recorded above.
- `--list` — intercepts the run-time resolver, loads the library, and reports everything it
  registered. That can run to a hundred or more records, every one named, obtained with
  no input but the path to the library.
- `--build` — **builds a real part and reads its netlist out of the library.** Measured on the
  library available here:

  | part | result |
  |---|---|
  | a 4-diode part | `hr = 0`, model class matches the record, 4 + 1 components, 4 wired nodes |
  | a composite | `hr = 0`, 4 sub-parts resolved **from the library's own records** + 8 primitives |
  | the largest part | `hr = 0`, **1,708 components**, 1,707 wired, nothing dropped, ~4 s |

  Each composition matches what an entirely separate, offline decode of the same library found —
  which is worth stating precisely because it is the *only* kind of agreement that counts here: the
  two derivations share no code and no method.

What a `--list` run produces:

```json
{
  "hostSymbolsAsked": ["<prefix>AttachEleRecord", "<prefix>RemoveEleRecord", "<prefix>GetEleRecord",
                       "<prefix>GetSysBlockRecord", "<prefix>AttachEleUtilRecord", ...],
  "recordsRequested": [ { "asked": "…", "matchedBy": "…", "recordIndex": 1 } ],
  "recordCount": 119,
  "records": [ { "index": 0, "class": "K…" }, ... ]
}
```

`hostSymbolsAsked` is the point: the host ABI is *observed*, not assumed. Roles this worker does not
yet implement still appear there, which is how the next ones get found rather than guessed.

What a `--build` run produces:

```json
{
  "part": "…", "recordIndex": 7, "factorySlot": 3, "hr": "0x00000000", "modelClass": "K…",
  "iidsAsked": ["{…}"],
  "componentsRequested": [ { "asked": "…", "matchedBy": "primitive|exact|class-minus-K",
                             "recordIndex": -2 } ],
  "primitives":  [ { "name": "…", "instances": 4, "terminalsAnswered": 64 } ],
  "recordCalls": [ { "primitive": "…", "slot": 7, "args": [.., 11, -1],
                     "argIsPointer": [true, false, false] } ],
  "wiringSlot":  { "…": 7, "…": null },
  "netlist":     [ { "component": 0, "primitive": "…", "nodes": [11, -1] } ],
  "componentCount": 5, "componentsNotRecorded": 0, "recordCallsNotRecorded": 0
}
```

`recordCalls` is the evidence and `netlist` is the reading of it. **Both are printed, always**, so a
wrong reading shows up as a netlist that disagrees with the log beside it instead of as a plausible
netlist. `wiringSlot` names which slot was read as wiring, per primitive, and is `null` for a
primitive whose wiring was not found.

`interfacesRequested` is the second layer. A library asks its host for factories by INTERFACE,
through names like `<prefix>GetFactory_<Interface>`, where the interface name is assembled at run
time from a class name. It exists nowhere in the image, so no static scan can find it and the only
way to learn which interfaces a library needs is to watch it ask. Each one it asks for and does not
get is a piece of host still to supply — the list is read off the library rather than guessed at.

Worth recording: **a part can build to completion with every one of those
factories answered NULL.** They are not on the path from a record to a netlist. That is a result, not
an assumption, and it is why `--build` did not have to implement any of them.

**Answer an unimplemented entry with NULL, never with a do-nothing function.** This is not tidiness.
While unrecognised lookups were served a harmless stub, the observed ABI was ten symbols; answering
NULL instead took it to seventeen, because a library that gets NULL **retries the same entry under
its stdcall-decorated name** (`_<name>@<bytes>`). That fallback is invisible to a host that answers
everything, and a library handed a callable it cannot actually use finds out much later, somewhere
unrelated. The decoration is stripped before an interface is counted, so the two spellings do not
read as two interfaces — and, for the same reason, before `--build` classifies an entry by its verb,
so a retry is not classified differently from the first attempt.

`--build` relaxes this, and only this: a getter it does not implement gets the generic services
object rather than NULL, because a library denied its host stops building. Everything the relaxation
covers is still listed in `hostSymbolsAsked`.

**`--gen-shims` is written but unexercised.** It serves the static case, and the library available
here is dynamic, so nothing has driven it end to end. Note also that it generates a host for
*registration* only: the module it describes has no services object and no build mode, so a static
library could be `--list`ed through it but not built. Nothing has needed that yet, and building it
blind — against no library of that kind — would be guesswork of exactly the sort this tool refuses.

**The record-answering side has now met a real library, and it found a defect the test double was
hiding.** The worker used to hand a record back as its **return value**. The ABI's actual shape is
an **out-parameter** — `GetEleRecord(name, Record **out)`, write `*out`, the return is ignored — and
the double had been written to read the return value *because that is where the worker put it*. Two
components agreeing with each other is not evidence, and here they agreed while both being wrong: a
library given the record only as a return value reports that it cannot locate the component, while
the non-NULL return makes the call look like it succeeded. Both are written now; the double reads
only `*out` and says so when `*out` is empty.

That was found the only way it could be — by running against something that was not written here.

**The matching rule survived contact.** `class-minus-K` resolved a composite part's four sub-parts
to the library's own records on the first real attempt, and the library then expanded each of them
itself. Precedence held: registry first, primitive second. The rule that matched is still reported
per request, so a wrong one stays visible.

**What `--build` does NOT read: the second node.** For every component the library states one node
index and a second argument that is `-1` in every single call, on every part, at every terminal
count. Two readings fit: `-1` means "referenced to ground", or the second node arrives somewhere
this does not look.

**The ground reading has now been tested, and it is wrong.** A part with published performance
figures was built three ways over the same extracted network and the same device parameters,
changing only which nodes the devices span, and simulated against those figures:

| topology | conversion loss | published |
|---|---|---|
| devices floating across node pairs | **7.1 dB** | 8 – 11 dB |
| devices referenced to ground, other ports open | 84.4 dB | |
| devices referenced to ground, other ports tied | 93.0 dB | |

All three converged to ~1e-9, so those are solutions and not failed solves — worth checking,
because "no mixing" and "no answer" look identical in the output. Two further tells point the same
way: grounded, the devices self-bias and carry no current, and the isolation figures do not move
with drive level *at all* (identical to three decimals across a 15 dB sweep). Only the floating
topology responds to drive.

So `-1` does not mean ground. **The second node arrives through a channel this worker does not yet
read** — and the integers it does report remain undecoded, because they do not match the floating
topology's node numbering either. The library is stating something real that no current reading
decodes correctly.

There is a one-line mapping that would reconcile such a part. It is not adopted, and that is
deliberate: reproducing observed numbers while assigning them the wrong meaning is the specific
failure this tool exists to prevent, and it has already happened three times to the offline decode
of this same library. It stays a hypothesis until parts with different device-to-port arithmetic
either confirm or kill it.

What the worker does in the meantime is report what it was told and stop:

- the terminal count the worker answers with (`--terminals`) changes nothing — 2, 4 and 8 all
  produce identical calls, so the wiring is per-component, not per-terminal, and is not being
  truncated by a count that is too small;
- `recordCalls` prints every slot the library touched with its raw arguments, which is where a
  second wiring channel would have to show up;
- **the worker is not the reason it is missing.** `TestPartBeta` in the double wires two of its
  components across genuine node *pairs*, and `--build` reads both numbers back. So a `-1` from a
  real library is that library's statement, not this reader's blind spot. That check exists in the
  double for exactly this purpose: without it, "we only ever see one node" and "we can only ever
  read one node" look identical.

**The netlist is derived, and the derivation is printed beside it.** Which slot carries wiring is
not assumed: the call log is scanned for the slot whose arguments are small integers rather than
addresses, and the slot chosen is reported. **Per primitive, not once for the part** — the first
real library refuted the global version in a single run, because a slot carrying two node indices
for one primitive carried two pointers for another, at the same index, in the same part. A
primitive with no slot of that shape gets none and is reported as such, rather than being given
invented nodes.

**One primitive's wiring is unread on the real library** — the many-terminal one's. No slot it
received carried wiring-shaped arguments. Its terminals are most likely positional, but that is a
hypothesis, and it is printed as a gap rather than filled in.

**A component's own model object is never called while the part is built.** Measured, on a part
with five components: 30 calls on the element records, **0** on the per-component models. That is a
clean negative and it narrows the search — whatever is stated at construction is stated through the
record slots and nowhere else, so the missing terminal will not be found by instrumenting the build
harder. Reaching it means *driving* the finished aggregate, which is the direction where a wrong
guess faults rather than quietly returning something wrong.

The worker keeps one model per component precisely so such calls are self-identifying, and now logs
them (`"on": "record"` versus `"on": <component>` in `recordCalls`). It had the pool and was not
logging it — the one channel nothing was watching happened to be the one that could have carried the
answer.

### `--dump-args`, and the trap it exists to close

A component with more terminals than fit in a register cannot receive its wiring in one, so it must
arrive by reference. `--dump-args` reads a little of what each pointer argument points at, as
integers and as text.

**Attribute every pointer before reading anything into it.** On x64 the first four arguments are
registers, so a callee always *receives* four whether or not four were passed; a slot taking one
argument still hands over three, and the extras are whatever those registers held. They look
exactly like pointers.

This is not hypothetical. The preview was added, and within minutes produced a confident wrong
conclusion: the many-terminal primitive's wiring call appeared to carry two pointers where a
two-terminal device carried two integers — which reads as the node list arriving by reference, i.e.
exactly the missing piece. It was leftovers. One of them pointed at **this repository's own string
literals**, and the run printed a fragment of `netlist_worker.c`'s own error text as an argument
value.

So every pointer argument is now reported with an `owner` — `self`, `library` or `other` — and one
owned by `self` is recorded but never read, because our own address cannot be something the library
passed.

**How to tell a leftover from an argument.** Re-running proves nothing — leftovers are perfectly
reproducible run to run. Rebuilding is not it either; that was tried, and the next rebuild left the
suspect values untouched, which under that rule would have made them real.

The test that works is narrower: **change only the worker's own code PATH — same binary, same
library, same part.** Toggling `--dump-args` moved the suspect values on its own. Nothing about the
library changed, so an argument that moves when only *our* code moves was never an argument.

Note the asymmetry, because it caught this out twice: **movement proves leftover; stability proves
nothing.** Those registers hold whatever our own last code left in them, and two code paths that
happen to leave the same value look exactly like a real argument. Attribution is the stronger
signal — an address inside our own image is decisive on its own.

**Neither log silently truncates.** A part larger than the logs would still be *served* correctly —
a lookup that fails because a log is full would break the build for a reason that has nothing to do
with the library — and the count that went unrecorded is reported, with the netlist marked
incomplete. The largest part available fits with room to spare.

**Nothing is wired into circuitRF yet.** No C# reads any of this; it is a standalone tool.

**Nothing here has been run on Windows.** Not "lightly tested" — *not tested*. It is built and
exercised entirely under Wine in a Linux container. Wine is not Windows: a pass there exercises the
mechanism and does not stand in for a real Windows host. `tools/senior-worker` carries the same caveat
for the same reason. Treat the Windows path as unverified until someone runs it there.

**One risk worth naming.** The interception patches the loader's own entry points in-process. That is
sound for a worker process that exists to load one library and exit, and it is why no instruction
decoder is needed — but it is also the kind of thing a security product objects to on Windows. Nobody
has checked what happens there, because nobody has run it there.

### macOS and Linux: a container, and this is temporary

The model libraries are Windows DLLs, so there is no native path on macOS or Linux. `run.sh` builds an
amd64 Debian + Wine image and runs the worker inside it. On Apple silicon that runs under Docker's own
emulation.

> **This is expected to change.** A container is a heavy dependency to put in front of importing a
> kit, and asking a user to install Docker to place a part is not an acceptable end state. The
> intended replacement is the sandbox circuitRF already ships for `tools/senior-worker` — `crf-vmhost`
> and the Linux images beside it — so that circuitRF provides the isolation rather than requiring a
> container runtime. Until that lands, the container is how this runs, and every path here that
> assumes it carries this note.

Docker Desktop is pinned via `--context desktop-linux`, so a different runtime cannot be picked up by
accident.

---

## Files

```
netlist_worker.c    one source, two products: the host module and the driver (-DCRF_SHIM / -DCRF_DRIVER)
build.sh            cross-build the driver with mingw-w64
Dockerfile          amd64 Debian + Wine + mingw-w64
run.sh              build the image if needed, then run the worker inside it
```

**Do not fork the source.** Two `#define`s over one file, exactly as `senior-worker` does it: a
forked worker is two implementations of one wire protocol to keep in step.

## Testing without a kit

`testlib.c` is the test double — `tools/fake-model-lib` plays the same role for `senior-worker`. It
reproduces the properties that make the real thing awkward: no exports, a host resolved at run time,
host symbols carrying a prefix the worker must derive, records identified through a genuine MSVC
RTTI chain, records that are **factories**, and a part **made of another part**, so the host's
lookup has to resolve back into the library's own records and the build has to recurse.

Its recipes — which primitives each part is made of and how they are wired — live in `testlib.c`
and nowhere in `netlist_worker.c`. That is the whole point: the worker has to *discover* them, so a
check is meaningful only if the answer is not on the worker's side of the wall.

`build.sh` builds it beside the driver, so a full check with no kit present is:

```
./run.sh --list  build/crf_testlib.dll                       # 3 records, prefix "Tl_" derived
./run.sh --build build/crf_testlib.dll TestPartBeta  --iid {1A2B3C4D-5E6F-4708-91A2-B3C4D5E6F708}
./run.sh --build build/crf_testlib.dll TestPartGamma --iid {1A2B3C4D-5E6F-4708-91A2-B3C4D5E6F708}
```

`TestPartBeta` is a leaf: three components, three wired nodes. `TestPartGamma` is the composite —
two of its own sub-parts plus a primitive, expanding to seven components. Both must come back with
`hr = 0x00000000` and a netlist matching the recipe in `testlib.c`.

**Check the failure modes too, because they are the ones that matter.** With a wrong `--iid` the
factory must refuse (`hr = 0x80004002`, no model, empty netlist); with none at all the run must
decline to start. A build that "succeeds" without the right identifier would be the exact failure
this design is arranged to prevent.

It is not yet run by CI — that needs the container to be available to the build, which is part of what
replacing the container is meant to solve.

## Build artefacts

`build.sh` and `run.sh` write into `tools/netlist-worker/build/`, and the container's Wine prefix
lands in `.wineprefix/`. Neither belongs in version control; both are safe to delete at any time and
are rebuilt on the next run.

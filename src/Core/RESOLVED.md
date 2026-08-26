# src/Core — resolved briefs (detail, off the CLAUDE.md growth path)

`src/Core/CLAUDE.md` was archived to `src/Core/HISTORY.md` once already (see that file's own note).
Going forward, a completed brief's detail lands here instead — one `##` section per brief, sparingly,
only for findings that are still true, still surprising, and would cost someone real time to
rediscover. Mirrors `src/Ui/DataDisplay/RESOLVED.md`'s own pattern.


## `DeviceWorkerProcessTests` is FLAKY — one static event, two workers (2026-08-26, observed, not fixed)

Found while checking a full-suite run for an unrelated change (`brief-cli-em-verb.md`), and recorded
because a flake nobody has written down gets re-diagnosed every time it appears.

`DeviceWorkerProcessTests.AWorkersOwnLog_ReachesTheHost_WhenItIsAskedFor` fails intermittently:

```
Assert.Contains() Failure: Item not found in collection
Collection: ["osdi-worker: dlopen failed: dlopen(/var/folders/k7"...]
Not found:  "measured node 6 as undriven"
```

**It is not about the reference worker.** `ProcessDeviceWorkerTransport.Logged` is a PROCESS-WIDE
static event, and `OsdiWorkerTests` / `OsdiModelDiscoveryTests` / `CompiledModelValidationTests` run
concurrently in the same assembly and publish into the same subscriber. The test's
`WaitForDelivery(transport, () => seen.Count > 0)` then returns on the FIRST line to arrive — which
on this machine is a foreign `osdi-worker: dlopen failed` — and asserts against a collection the
reference worker has not written to yet.

Measured: `dotnet test tests/Core.Tests` on its own fails 2 runs in 3. It needs no other test project
and no change to reproduce, so **it is intra-assembly and pre-existing** — worth stating, because it
turns up in a full-suite run of a change that touched nothing near it and reads like a regression.

The fix, when someone picks it up, is to wait for the line the test is actually about rather than for
any line at all (`seen.Any(l => l.Contains(expected))`), or to key the collector on the transport it
started. Left alone here deliberately: it is in a subsystem this change did not touch, and a "while I
was in there" edit to someone else's flaky test is how two problems become one confusing commit.

## `at(x, "axis", index)` — a reference value that survives adding a sweep (2026-08-19)

Owner's question, and it is the right shape of question: *"AMPM = trans_phase − phase(HB1.V[0, "Vout", 1])
works when only Pin is swept. Add an RFfreq sweep and it's invalid. Is there a single expression that
works either way?"* The answer was **no**, and the reason is a real gap rather than a missing convenience.

**The accessor is shape-independent for KEEPING axes and positional for FIXING one.** `HB1.V("Vout", 1)`
locates node and harmonic by name and keeps every sweep, so it survives any sweep depth. But the
reference term has to fix the Pin axis, and both notations fix by POSITION: `HB1.V("Vout", 1, 0)` /
`HB1.V[0, "Vout", 1]` with one sweep become `HB1.V("Vout", 1, All, 0)` / `HB1.V[0, 0, "Vout", 1]` with
two. There is no third spelling.

**The two failure modes are asymmetric, and the quiet one is the dangerous one.** The bracket form
ERRORS when the axis count changes ("expected 4 axis token(s), got 3") — that is what the owner saw. The
accessor form does not: `EvalQualifiedAccessor`'s sweep loop is bounded by the cube's OWN sweep count,
so surplus arguments are silently dropped. `HB1.V("Vout", 1, All, 0)` — correct with an outer RFfreq
sweep — quietly means "all Pin points" without one, and AM-PM comes out identically 0.00 with nothing
reported anywhere.

**The capability already existed one layer down and was simply unreachable.** `DataCube.At(axisName,
index)` pins by name and keeps the rest, and `ElementWise`/`UnionAxes` already broadcast by axis NAME —
so `[RFfreq, Pin] − [RFfreq]` lines each curve up with its own reference. Both verified numerically
before proposing the feature, not assumed. Exposing `at` as one evaluator function is therefore the
whole fix:

```
trans_phase = phase(HB1.V("Vout", 1))
AMPM        = trans_phase - at(trans_phase, "Pin", 0)
```

End-to-end on the owner's own netlist through `Cli hb`: `[RFfreq:3 × Pin:4]` with **every row starting
at exactly 0** (a per-frequency reference, not one global number), and the single-sweep run reproducing
the 2 GHz row value-for-value from the identical expression text.

**Two things the wiring had to fix, both in the no-sweep direction.** `DataCube.At` did `result.Cube!`
on a `SliceResult` — but pinning the ONLY axis leaves a bare element with a null `Cube`, so it threw a
`NullReferenceException` on exactly the case a shape-independent expression hits first (one sweep, no
RFfreq). It now returns a rank-0 cube. And `AxisIndex`'s "No axis named 'x'." said nothing about what
was there; it now names the axes, because its callers are user expressions.

**Strict by choice.** A missing axis, a scalar argument, and an out-of-range index are all errors — the
last naming both usable ranges (`0..2, or -1..-3`). Returning the value unchanged would have made a
mistyped axis name read as "AM-PM is identically zero", which is the very failure mode the function was
added to remove. Negative indices count from the end (`-1` = last), because "referenced to the top of
the sweep" should not require knowing the sweep length.

Tests: `tests/Core.Tests/Expressions/AtAxisPinTests.cs` (10) — the same expression text against a
`[Pin, …]` and a `[RFfreq, Pin, …]` fixture, the per-frequency reference, negative indexing, the rank-0
result, and each refusal.

## Starting a worker is announced, once, before it starts (2026-08-19)

Owner report: the first time a worker is launched to evaluate an external model there is no feedback
at all.

**It is the one step in evaluating an external model that a user waits on and cannot see.** The
worker process starts, loads the vendor's model library and describes its device types — and on macOS
all of that happens inside the Linux VM circuitRF ships, which has to boot first. Until it finishes, a
run proceeding perfectly normally is indistinguishable from one that has hung, and the next thing
printed is whatever the run says NEXT, which is a result or a failure and never mentions the worker.

**`ProcessDeviceWorkerTransport.Starting`**, a static event carrying a `DeviceWorkerStart(Provider,
Command)`. Three things about the placement are load-bearing:

- **Raised immediately BEFORE `Process.Start`, not after.** The wait is the whole reason for
  announcing it; a message that waited for a successful start would arrive after the thing it
  explains, and would be missing entirely from the case a user most needs it in — a start that never
  completes.
- **At the process, not at the provider lookup.** `ExternalDeviceRegistry.Find` keeps what it
  resolved, so a design placing forty devices from one kit starts one worker and gets one line. That
  is what makes "once" true without anything having to remember whether it has spoken. A worker
  genuinely started a second time — a different kit, or the same kit after the workspace changed — is
  a second thing happening and is reported as one.
- **Structured, not a formed sentence.** How it is worded belongs to whoever shows it; a headless
  host may want it in a different shape entirely. `src/Ui`'s `WorkspaceViewModel` subscribes once, in
  its constructor, beside the other process-lifetime static it already listens to, and posts through
  the dispatcher because this arrives on whatever thread the run is on.

A subscriber that throws is swallowed: a host's own reporting must never be the reason a worker fails
to start, because the failure would then be attributed to the kit, and the kit would be fine.

**Gate:** `tests/Core.Tests/Devices/External/DeviceWorkerStartNotificationTests.cs`, 5 tests —
announced before the process exists (checked by starting a path that is deliberately not an
executable, so the announcement has to precede the failure), one event per started process, a caller
with no provider name still announces, a throwing subscriber does not stop the worker, and a second
lookup of the same provider starts nothing and says nothing. The UI half is a source-level check in
`tests/Ui.Tests/KitLayoutGeneratorRefreshTests.cs`, since the event means nothing until something
subscribes and the subscription is one line in a constructor.

## An ExtDevice selector taken verbatim swallowed a REFERENCE, not just a literal (2026-08-19)

Owner report: a kit transistor that used to simulate failed every operating point, and the worker's
own per-create log read:

```
create <TYPE>: File=File (NOT READABLE HERE)
create <TYPE>: TSNK=-1 (supplied)
create <TYPE>: RTH=1e-06 (supplied)
create <TYPE>: probe eval at zero bias FAILED
```

**The `File` parameter arrived as the literal four characters `File`.** The numeric parameters beside
it — and they varied correctly per device, so the design was plainly reaching the model — did not,
which is what makes this read as a model or a bias problem rather than a plumbing one.

**Root cause.** `Elaborator.ResolveExtDeviceParameters` treats `Provider`, `Type`, `File` and `Model`
as SELECTORS and stored them **verbatim, never evaluated**. That rule exists for good reasons and
they are still good: a path is not an expression (a leading `/` alone stops the parser at position 0
— the same trap `SnP`'s `File=` hit), and falling back to verbatim only when evaluation throws is not
enough, because a path that happens to parse as arithmetic would be silently turned into a number.

What the rule did not allow for is that `File=File` is not a literal at all. It is the ordinary way a
netlist passes a value down: the kit's device cell declares its data file as a cell parameter and
forwards it into the device by name, which is the only way one part can be instantiated at several
file-backed sizes. Verbatim turned the reference into its own name.

**Fix: the QUOTING decides, and the netlist already states it.**

- Quoted → a literal, always. Never looked up, not even when something in scope is spelled the same.
- A bare name the scope BINDS → resolved through the ordinary evaluator, in the scope the device sits
  in, so both an instance override and a cell's declared default work, at any nesting depth. Handed
  on as text whatever kind it resolved to, because `ComponentModelFactory` requires `Provider` and
  `Type` to be strings.
- Anything else — an unquoted path, an enum value, a name nothing binds — is left exactly as it was.

So nothing that worked before changes, and this is a reading of the netlist rather than a heuristic
about it.

**Verified against the reported design end to end**, by elaborating its real `.cnl` against a stub
provider: five devices, each now handed its own correct `.mdl` path, with the thermal parameters
differing per device as the design says. Before the fix all five got `File`.

**Gate:** `tests/Core.Tests/Devices/External/ExtDeviceSelectorForwardingTests.cs`, 8 tests — four for
the forwarding (one instance, two instances at different files, the cell's declared default, and the
non-selector parameters that were already right and must stay so) and four for what must NOT change
(a quoted literal is never looked up even when a scope binding shares its spelling; an unquoted path
stays verbatim; a bare name nothing binds stays verbatim; `Provider`/`Type` follow the same rule).
The first four were confirmed to fail before the change and the last four to pass throughout.

## The SDD evaluator: what `dut.Evaluate` actually costs, and what closing the gap bought (brief-harmonicarf-r3b §1, 2026-08-13)

**The measurement that started this:** on the shipped default harmonicaRF document (Hero 2's GaN
HEMT as an SDD, K = 3, no package), `dut.Evaluate` cost ~9–13 µs and was reported as "100% expression
evaluation, nothing else in it" — `SddEvaluator.EvalDual` tree-walked the parsed AST against a
**freshly-built `Dictionary<string,Dual>` on every single call**, with `"_v{i+1}"` string
interpolation and a string-hashed lookup at every `RefExpr`, even for a one-node expression.

**Fix, in two measured steps, `SddEvaluator.EvalDual`/`EvalDouble` left completely UNTOUCHED as the
reference implementation:**

1. **`CompiledSddExpr`/`SddCompiler` (new, `src/Core/Expressions/SddCompiled.cs` +
   `CompiledSddExpr.cs`)** — each `RefExpr` is resolved to an integer SLOT once, when `SddModel`
   compiles its equations in its own constructor (alongside where it already caches the parsed AST),
   not on every `Evaluate` call. A parameter's `Dual` (zero gradient, correct width) is built ONCE per
   model and reused — it never changes across calls, so building it fresh every time (as the
   dictionary path did) was pure waste. Evaluation touches no `Dictionary`, hashes no string,
   interpolates nothing. **Bit-identical by construction, verified by a corpus test**
   (`SddCompiledBitIdenticalTests`, 123 cases across the shipped default's own two equations, every
   equation in `testdata/`, and hand-written ones exercising `^`, conditionals, every function, and
   the `ExpCap`/`LogFloor` clamp paths — asserts exact `double` equality, not a tolerance) that must
   stay green; it is the gate against a "faster but slightly different" evaluator, which would move
   every SDD-based hero golden at once.
2. **`in T` parameters on `IAdScalar<T>`** (`Add`/`Sub`/`Mul`/`Div`/`Pow`/`Neg`/the function table) —
   `Dual` is a ~144-byte struct carrying a fixed 16-wide gradient regardless of the actual port+control
   count (2, here), so a by-value binary op was copying ~288 bytes of INPUT on every `Add`/`Mul`/etc.
   Read-only-reference parameters cut that; the RESULT is still returned by value (there is no
   caller-owned slot to write into without restructuring the whole evaluator around `Span<double>`
   buffers — out of this step's scope; see the open item below).

**Measured, shipped-default fixture, both steps together:** `dut.Evaluate` **13.4 → 9.5 µs (~29%
faster)**. Every hero golden (Engine.Tests, 1177 tests + the 6-project full-solution run) still
passes — nothing moved.

**A correction worth keeping so nobody re-derives a frame budget from the wrong number.** An earlier
draft of the brief this work came from quoted "~2 ms per solve" from `HarmonicaGridDragCostTests`.
That figure is real but belongs to a DIFFERENT fixture — K = 5 **with an Rd/Rs/Ls package**, a
materially bigger circuit. On the shipped default (K = 3, no package) a warm solve is **0.69–0.86 ms
before this work, 0.36–0.37 ms after** — nowhere near 2 ms either way. The solve COUNT in a grid was
never the bottleneck; the per-solve evaluator cost was.

**The surprising part, worth keeping so nobody re-derives it:** the compiled node-walk ITSELF,
isolated from the per-call setup it replaces, was measured to be *slower* than the dictionary-based
reference on the shipped default's 80-node drain equation (~10.5 µs compiled vs ~8.9 µs reference,
before the `in`-parameter step; the gap narrowed to ~13% after it, but did not close). The equation
has only two names to resolve ("_v1"/"_v2" — the shipped default's coefficients are literal, not
named parameters), so the dictionary it replaces was already tiny and cheap to query; nearly all of
the ~105 ns/node cost the brief measured is genuinely **per-node walk cost** (dispatch + the struct
traffic of a functional, expression-tree-shaped evaluator), not lookup cost — and killing the
dictionary does not touch it. This is exactly what the brief's own step 3 (compile to a flat
instruction array over `Span<double>`, i.e. leave the tree-walk-of-boxed-nodes shape entirely) is
for. **Reordering the compiled dispatch from a type-pattern switch (a chain of `isinst` checks) to an
explicit integer-discriminant switch was tried and measured to NOT close this gap** — the per-node
cost is structural to the tree-walk shape, not an artifact of how one particular walk dispatches.

**Step 3 — built after the above was reported and confirmed with the owner.**
`SddRegisterCompiler`/`RInstr` (`SddRegisterProgram.cs`) flatten the whole equation into a linear
three-address-code array at compile time — no boxed node objects, no recursion, one register per
instruction, walked by a single `for` loop with a `switch` on a byte opcode. Register space is one
flat `Dual[]`: indices `[0, totalSlots)` are the input slots (exactly as steps 1–2 already laid out),
index `totalSlots + i` is instruction `i`'s own output. A bare name/leaf reference costs NO
instruction at all (it is just a slot index), which is why the program is shorter than the
equivalent node tree. **This finally lands the win the per-node-cost finding above said step 3 would
need to reach**, because it genuinely leaves the "one struct-returning virtual call per node" shape
behind rather than reorganising it.

**Scope, deliberately not fully general — and why that is fine.** The compiler handles every
expression EXCEPT a conditional (`if(...)`/ternary); `SddRegisterCompiler.ContainsConditional` scans
the AST once at compile time and routes an equation containing one to step 1's node-tree walk instead
(still correct, still faster than the original dictionary path). A correct, jump-based bytecode VM
with short-circuit `&&`/`||` is a real undertaking with its own correctness surface, and **no SDD
equation in this repository — the shipped default, or anything in `testdata/` — contains a
conditional**; building branch-handling for a construct with zero measured real-world use was judged
not worth the risk. If a future SDD equation genuinely needs one, it still works correctly (via the
fallback), just without step 3's speedup.

**Measured, shipped-default fixture (steps 1–3 together, against the ORIGINAL pre-brief baseline):**

| quantity | before | after | factor |
|---|---|---|---|
| `dut.Evaluate` | 13.4 µs | 4.5 µs | **3.0×** |
| `EvalDual` big (drain eqn), reference dict path | 8.9–11.9 µs | — (superseded) | — |
| `CompiledSddExpr.EvalDual` big, register machine | — | 3.0–3.1 µs | **~2.9×** vs reference |
| warm `ctx.Solve` | 0.69–0.86 ms | 0.36–0.37 ms | **~2×** |
| cold `ctx.Solve` | 0.85–1.06 ms | 0.55–0.57 ms | **~1.7×** |
| `PinSearch.Sweep` (46 solves, tier-A ladder) | 35.7–43.0 ms | 18.3–18.8 ms | **~2×** |

Bit-identical corpus (123 cases, both the register path and the conditional fallback exercised) and
the full solution (Core/Engine/Harmonica/Ui/RfCore/WBond/Firewall) all still pass — nothing moved.
`SddEvaluator.EvalDual`/`EvalDouble` remain completely untouched as the reference implementation.

**What this means for the frame-rate target:** tier-A's 46-solve sweep is now ~18.5 ms — well short
of the brief's own "step 3 optional if the sweep lands near 2–3 ms" bar, but a real 2× cut from the
evaluator alone. Whether an L1-drag frame hits >60 fps depends on the REST of the frame (render,
readout-strip rebuild, pool/dispatcher overhead) — see the harmonicaRF-side write-up for that
breakdown; it is a separate cost this brief's §1.4 measures independently.

## `RFfreq = 2 GHz` in a schematic VAR produced no variable at all (2026-08-18)

> Reported together with the parametric-sweep unit bug, which lives in the engine — see
> `src/Engine/RESOLVED.md` §"A parametric sweep's unit". One schematic reached both.

The other half of that report. A `.cnl` has no unit column, so `CnlReader.SplitExprUnit` has always
lifted a trailing unit into `Variable.Unit`. A schematic VAR row *does* have a unit column, and
`NetExtractor` passed the expression through verbatim — so the identical text meant two different
things, and the schematic one meant **nothing at all**: `Parser.Parse("2 GHz")` is a parse error (the
grammar has no unit-suffix production), and `Elaborator` skips a global it cannot resolve inside a bare
`catch {}`. Nothing anywhere reported it. Downstream, `LoadpullPursuitEngine.Resolve` catches the
resulting `UnresolvedNameException` and substitutes **1 GHz**, and the sweep row had no unit to inherit.

**The rule now lives once, in `Units.SplitTrailingUnit`.** The schematic caller
(`NetExtractor.LiftInlineUnit`) **verifies the split against the parser** instead of trusting the unit
table: every bare SI prefix is a unit name, so a token-only rule tears `"2 * f"` into `"2 *"` + femto
and `"R * m"` into `"R *"` + milli. Split only when the unsplit text does not parse and the split text
does — which makes it reachable by exactly the rows it is for and by no row that already worked. A
netlist keeps the greedy rule; it has no alternative spelling to fall back on.

Related, and already recorded in `src/Core/CLAUDE.md`'s trap list: a cell-parameter declaration, a
top-level variable assignment and an instance-line param are three separate parse sites for the same
unit token, and fixing one has repeatedly left the others wrong. The schematic VAR row was the fourth.
